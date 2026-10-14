using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// 1.8.2 card swaps with other nocat.farm users - the sending side. With "Swap cards with other nocat.farm users" on, each
/// look sends at most one offer for a match nocat.lol made (see <see cref="CardExchange"/>), after checking it all again
/// against this account's own cards and prices, and follows up the offers it sent: noted when they go through, taken
/// back after 2 days unanswered.
/// </summary>
/// <remarks>
/// Never more than "Swaps a day, at most"; on a human-mode account only in its own day and spaced out (45 minutes to
/// 2 hours between swaps); never while scam watch holds its trades; never with an account whose trades would be held,
/// when that's switched on; never with an account that once sent a bad offer. The offer is confirmed by itself when the
/// account's authenticator is in nocat.farm - otherwise it waits for your phone, and the log says so.
/// </remarks>
public sealed class CardSwaps(Bot bot) : BotModule(bot) {
	private Said _status = new("");

	public override string Name => "swaps";
	public override string Status => CardExchange.On(Bot.Cfg, Bot.Name) ? _status : "";

	private HumanGate? _gate;

	/// <summary>Its own day, settled in, you not playing on it - on a human-mode account; always open on the others.</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	// ── test seams: what it reads and does on Steam, instead of asking Steam (null: Steam itself) ──
	internal Func<CancellationToken, Task<InventoryContents?>>? ReadInventory { get; set; }
	internal Func<CancellationToken, Task<List<TradeOffers.Offer>?>>? ReadOffers { get; set; }
	internal Func<ulong, string, CancellationToken, Task<TimeSpan?>>? ReadHold { get; set; }
	internal Func<ulong, string, List<Looting.Item>, List<Looting.Item>, string, CancellationToken, Task<Looting.SwapSent>>? Send { get; set; }
	internal Func<ulong, CancellationToken, Task<(bool Answered, TradeOffers.Offer? Offer)>>? ReadOffer { get; set; }
	internal Func<ulong, CancellationToken, Task<bool>>? Cancel { get; set; }
	internal Func<string, int, CancellationToken, Task>? PriceCard { get; set; }
	internal Func<ulong, CancellationToken, Task<string>>? NameOf { get; set; }
	internal Func<bool>? DayOpen { get; set; }
	internal Func<ulong, CancellationToken, Task<bool>>? ConfirmOffer { get; set; }

	/// <summary>Offers it has already asked you to confirm on your phone - once each.</summary>
	private readonly HashSet<ulong> _askedPhone = [];

	/// <summary>When each offer out was last looked at - every half hour is plenty.</summary>
	private readonly Dictionary<ulong, DateTime> _checked = [];

	/// <summary>Looks that couldn't decide a match yet (no price, Steam didn't answer) - it's let go after a few.</summary>
	private readonly Dictionary<string, int> _tries = [];

	private Task<string> Who(ulong id, CancellationToken ct) => NameOf?.Invoke(id, ct) ?? SteamNames.OfAsync(Bot, id, ct);

	protected override async Task RunAsync(CancellationToken ct) {
		// Well clear of everything a sign-in does.
		if (!await Sleep(Rng.Minutes(10, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (CardExchange.On(Bot.Cfg, Bot.Name) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				try {
					await LookAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug($"card swaps: {Log.Describe(e)}", Bot.Name);
				}
			}

			if (!await Sleep(Rng.Minutes(4, 8), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>One look: the offers out first, then at most one new one. What it's doing, for the dashboard.</summary>
	internal async Task<Said> LookAsync(DateTime now, CancellationToken ct) {
		CardExchange.Ledger ledger = CardExchange.Ledger.Load(Bot.Name);

		await FollowUpAsync(ledger, now, ct).ConfigureAwait(false);

		if (ScamWatch.HoldsTrades(Bot)) {
			return _status = new Said("held by scam watch - no swaps until 'scamwatch ok'");
		}

		if (!(DayOpen?.Invoke() ?? Gate.Open)) {
			return _status = new Said("waiting for its day");
		}

		int perDay = CardExchange.PerDay(Bot.Cfg);

		if (ledger.DoneToday(now) >= perDay) {
			return _status = new Said("{0} swaps today - the most for a day", perDay);
		}

		if (now.Ticks < ledger.NextAt) {
			DateTime at = new(ledger.NextAt, DateTimeKind.Utc);

			return _status = new Said("next swap from about {0}", (Func<string>) (() => Fmt.Clock(at)));
		}

		CardExchange.Match? next = CardExchange.MatchesOf(Bot.SteamId)
			.Where(m => m.Send && (ledger.Of(m.Id) == null) && (now - m.Made < CardExchange.MatchFor) && !ledger.IsBlocked(m.You))
			.OrderBy(static m => m.Made).FirstOrDefault();

		if (next == null) {
			return _status = new Said("no swap to send right now");
		}

		return _status = await SendAsync(next, ledger, now, ct).ConfigureAwait(false);
	}

	/// <summary>The swap offers it sent: noted once they went through, taken back after 2 days, let go when they're gone.</summary>
	private async Task FollowUpAsync(CardExchange.Ledger ledger, DateTime now, CancellationToken ct) {
		foreach ((string id, CardExchange.Record r) in ledger.InState("sent")) {
			if ((r.Offer == 0) || (_checked.TryGetValue(r.Offer, out DateTime last) && (now - last < TimeSpan.FromMinutes(30)))) {
				continue;
			}

			_checked[r.Offer] = now;
			(bool answered, TradeOffers.Offer? offer) = await (ReadOffer?.Invoke(r.Offer, ct) ?? TradeOffers.OneAsync(Bot, r.Offer, ct)).ConfigureAwait(false);

			if (!answered) {
				continue;
			}

			string who = await Who(r.Partner, ct).ConfigureAwait(false);

			switch (offer?.State) {
				case 3:   // accepted
					ledger.SetState(id, "done", r.Partner, now);
					Log.Trade(new Said("card swap with {0} done · {1} card(s) each way", who, offer.Giving.Count), Bot.Name, good: true);

					break;
				case TradeOffers.Active or TradeOffers.NeedsConfirmation when offer.Ours && (now - new DateTime(r.At, DateTimeKind.Utc) > CardExchange.OfferFor):
					if (await (Cancel?.Invoke(r.Offer, ct) ?? Looting.CancelSwapAsync(Bot, r.Offer, ct)).ConfigureAwait(false)) {
						ledger.SetState(id, "dropped", r.Partner, now);
						Log.Info(new Said("card swap offer to {0} taken back · not accepted in 2 days", who), Bot.Name);
					}

					break;
				case TradeOffers.Active when !offer.Ours && (now - new DateTime(r.At, DateTimeKind.Utc) > CardExchange.OfferFor):
					// One it accepted that still waits for your phone after 2 days: it's yours now, not a match any more.
					ledger.SetState(id, "dropped", r.Partner, now);

					break;
				case TradeOffers.NeedsConfirmation when offer.Ours && (CardExchange.Find(Bot.SteamId, id) is { } match):
					await ConfirmAsync(match, offer, who, ledger, now, ct).ConfigureAwait(false);

					break;
				case TradeOffers.Active or TradeOffers.NeedsConfirmation or TradeOffers.InEscrow:
					break;   // still waiting
				default:
					ledger.SetState(id, "dropped", r.Partner, now);
					Log.Info(new Said("card swap with {0} didn't go through", who), Bot.Name);

					break;
			}
		}
	}

	private async Task<Said> SendAsync(CardExchange.Match match, CardExchange.Ledger ledger, DateTime now, CancellationToken ct) {
		string who = await Who(match.You, ct).ConfigureAwait(false);

		// Can't tell yet: tried again at the next look - six times, then it's let go.
		Said Wait(Said why) {
			int tries = _tries[match.Id] = _tries.GetValueOrDefault(match.Id) + 1;

			return tries >= 6 ? Drop(why) : why;
		}

		Said Drop(Said why) {
			_tries.Remove(match.Id);
			ledger.SetState(match.Id, "dropped", match.You, now);
			Log.Info(new Said("card swap with {0} skipped · {1}", who, why), Bot.Name);

			return why;
		}

		if (Bot.Cfg.CardSwapSkipHeld) {
			TimeSpan? hold = await (ReadHold?.Invoke(match.You, match.Token, ct) ?? TradeOffers.HoldAsync(Bot, match.You, ct, match.Token)).ConfigureAwait(false);

			if (hold == null) {
				return Wait(new Said("Steam didn't say whether the trade would be held"));
			}

			if (hold > TimeSpan.Zero) {
				return Drop(new Said("the trade would be held"));
			}
		}

		if (await (ReadInventory?.Invoke(ct) ?? Inventory.ReadAsync(Bot, 753, "6", ct)).ConfigureAwait(false) is not { Complete: true } inventory) {
			return Wait(new Said("couldn't read its cards"));
		}

		if (await (ReadOffers?.Invoke(ct) ?? TradeOffers.ActiveAsync(Bot, received: true, sent: true, ct)).ConfigureAwait(false) is not { } live) {
			return Wait(new Said("Steam didn't say what's on its offers"));
		}

		// Its own cards in this match are what it sends now; its other matches' stay promised to them.
		HashSet<ulong> busy = [.. TradeOffers.Committed(live), .. CardExchange.Reserved(Bot.Name, Bot.SteamId, now)];
		busy.ExceptWith(match.Pairs.Select(static p => p.GiveAsset));
		List<CardExchange.OwnCard> cards = CardExchange.ReadCards(inventory);
		CardExchange.Rules rules = CardExchange.Rules.From(Bot.Cfg);
		int currency = Bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) Bot.WalletCurrency;

		if (currency == 0) {
			return Wait(new Said("Steam hasn't said its wallet currency yet"));
		}

		long maxLoss = CardExchange.MaxLossCents(Bot.Cfg);
		List<CardExchange.Chosen> chosen = CardExchange.Choose(match, cards, busy, rules, h => CardExchange.WalletCents(h, currency, now), maxLoss, out Said why, out List<string> unpriced);

		// Cards with no fresh price are asked about in the wallet's own currency (a few a look), then it's judged again.
		if (unpriced.Count > 0) {
			foreach (string hash in unpriced.Take(6)) {
				await (PriceCard?.Invoke(hash, currency, ct) ?? MarketQuotes.FetchAsync(hash, currency, ct)).ConfigureAwait(false);
			}

			chosen = CardExchange.Choose(match, cards, busy, rules, h => CardExchange.WalletCents(h, currency, DateTime.UtcNow), maxLoss, out why, out unpriced);

			if ((unpriced.Count > 0) && (_tries.GetValueOrDefault(match.Id) < 5)) {
				return Wait(new Said("waiting for prices"));
			}
		}

		if (chosen.Count == 0) {
			return Drop(why.IsEmpty ? new Said("nothing in it fits any more") : why);
		}

		List<Looting.Item> giving = [.. chosen.Select(static c => new Looting.Item(c.Asset, c.Pair.Give, 0, 1, "", c.Pair.GiveName, 753, 6, c.Pair.Game))];
		List<Looting.Item> taking = [.. chosen.Select(static c => new Looting.Item(c.Pair.GetAsset, c.Pair.Get, 0, 1, "", c.Pair.GetName, 753, 6, c.Pair.Game))];
		Looting.SwapSent sent = await (Send?.Invoke(match.You, match.Token, giving, taking, CardExchange.Tag + match.Id, ct)
			?? Looting.SendCardSwapAsync(Bot, match.You, match.Token, giving, taking, CardExchange.Tag + match.Id, ct)).ConfigureAwait(false);

		if (!sent.Ok) {
			return Drop(new Said("Steam refused the offer: {0}", sent.Message));
		}

		ledger.SetState(match.Id, "sent", match.You, now, sent.OfferId);
		ledger.Counted(now, Bot.Cfg.LegitMode ? Rng.HumanMinutes(45, 120) : Rng.Minutes(2, 5));
		_tries.Remove(match.Id);

		string give = Spare.Money(chosen.Sum(static c => (decimal) c.GivePrice), currency), get = Spare.Money(chosen.Sum(static c => (decimal) c.GetPrice), currency);
		Log.Trade(new Said("card swap · offer sent to {0} · {1} card(s) each way", who, chosen.Count), Bot.Name);
		Log.Info(new Said("card swap · you give {0} · you get {1}", give, get), Bot.Name);

		if (sent.ByEmail) {
			Log.Attention(new Said("card swap · confirm the offer to {0} from Steam's email", who), Bot.Name, Topic.Trades);
		} else if (sent.WaitsForPhone) {
			// Nobody sees it until it's confirmed - and it's only confirmed once the offer Steam made is checked to be the match.
			_checked[sent.OfferId] = now;
			(bool answered, TradeOffers.Offer? made) = await (ReadOffer?.Invoke(sent.OfferId, ct) ?? TradeOffers.OneAsync(Bot, sent.OfferId, ct)).ConfigureAwait(false);

			if (answered && (made is { State: TradeOffers.NeedsConfirmation })) {
				await ConfirmAsync(match, made, who, ledger, now, ct).ConfigureAwait(false);
			}
		}

		return new Said("sent a swap to {0}", who);
	}

	/// <summary>
	/// An offer of ours waiting on its mobile confirmation: confirmed only when it's exactly the match - our cards out, the
	/// partner's normal cards in, one for one. Anything else is taken back, and that partner never swapped with again.
	/// Confirmed by the authenticator when it's here; otherwise you're asked once, on your phone.
	/// </summary>
	private async Task ConfirmAsync(CardExchange.Match match, TradeOffers.Offer offer, string who, CardExchange.Ledger ledger, DateTime now, CancellationToken ct) {
		if (!CardExchange.AsPaired(offer, match)) {
			await (Cancel?.Invoke(offer.Id, ct) ?? Looting.CancelSwapAsync(Bot, offer.Id, ct)).ConfigureAwait(false);
			ledger.SetState(match.Id, "bad", match.You, now);
			ledger.Change(l => {
				if (!l.Blocked.Contains(match.You)) {
					l.Blocked.Add(match.You);
				}
			});
			Log.Attention(new Said("card swap offer to {0} wasn't what nocat.lol said · taken back", who), Bot.Name, Topic.Trades);

			return;
		}

		if (await (ConfirmOffer?.Invoke(offer.Id, ct) ?? Bot.ConfirmMobileAsync(offer.Id, true, ct)).ConfigureAwait(false)) {
			return;
		}

		if (_askedPhone.Add(offer.Id)) {
			Log.Attention(new Said("card swap · confirm the offer to {0} on your phone", who), Bot.Name, Topic.Trades);
		}
	}
}
