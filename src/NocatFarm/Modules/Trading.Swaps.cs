using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// 1.8.2 card swaps with other nocat.farm users - the receiving side. An offer whose note names a match ("nocat.farm card
/// swap #...") on an account with card swaps on is checked against that match (<see cref="CardExchange.Verify"/>) instead
/// of the ordinary fair-swap test, once when it comes and again right before it's accepted:
/// <list type="bullet">
/// <item>exactly what nocat.lol paired (or some of its pairs), from the account it paired, nothing else - else it's never
/// accepted, it's left for you, nocat.lol is told, and that account is never swapped with again;</item>
/// <item>a match this account hasn't swapped, declined or found bad already, paired no more than 2 days ago, asking for no
/// copy that's on another offer or promised to another match - else it's declined;</item>
/// <item>this account keeps "Keep this many of each card" (never its last copy), its sets lose nothing (ASF's own test),
/// and by its own prices - of the cards as Steam names them - it gets at least what it gives, less "Most a swap may
/// lose" - else it's declined;</item>
/// <item>within "Swaps a day, at most", and on a human-mode account in its own day, spaced out.</item>
/// </list>
/// Then it's accepted the usual way: after the usual wait, one swap a look, and confirmed by the authenticator when it's
/// here (otherwise it waits for your phone, and the log says so).
/// </summary>
public sealed partial class Trading {
	/// <summary>Test seam: this account's Steam inventory, instead of asking Steam.</summary>
	internal Func<CancellationToken, Task<InventoryContents?>>? ReadCards { get; set; }

	/// <summary>Test seam: asking the market for one card's price in the wallet's currency.</summary>
	internal Func<string, int, CancellationToken, Task>? PriceCard { get; set; }

	/// <summary>What each swap offer was judged - accept or decline - until it's answered. Under its own lock.</summary>
	private readonly Dictionary<ulong, bool> _exchange = [];

	/// <summary>Looks that couldn't judge a swap offer (no fresh price yet) - declined after a few.</summary>
	private readonly Dictionary<ulong, int> _exchangeWaits = [];

	/// <summary>
	/// Swap offers naming a match this copy hasn't been handed yet, and since when: the sender's copy may have asked nocat.lol
	/// for its matches before this one did. Asked again at once, and given 3 hours before it's left for you.
	/// </summary>
	private readonly Dictionary<ulong, DateTime> _exchangeUnknown = [];

	/// <summary>Both sides' worth in the wallet's currency, from the last check - said when it's accepted.</summary>
	private readonly Dictionary<ulong, (long Give, long Get, int Currency)> _exchangeWorth = [];

	/// <summary>An offer that says it's a card swap nocat.lol paired, on an account with card swaps on.</summary>
	private bool IsExchangeOffer(TradeOffers.Offer offer) => !offer.Ours && (CardExchange.MatchIdOf(offer.Message) != null) && CardExchange.On(Bot.Cfg, Bot.Name);

	/// <summary>The verdict for the queue: true accept, false decline, null not now (or left for you).</summary>
	private async Task<bool?> ExchangeVerdictAsync(TradeOffers.Offer offer, CancellationToken ct) {
		lock (_exchange) {
			if (_exchange.TryGetValue(offer.Id, out bool known)) {
				return known;
			}
		}

		return (await ExchangeJudgeNowAsync(offer, ct).ConfigureAwait(false)).Fair;
	}

	/// <summary>Judged on the cards and prices as they are now - when it comes, and right before it's accepted.</summary>
	private async Task<(bool? Fair, Said Why)> ExchangeJudgeNowAsync(TradeOffers.Offer offer, CancellationToken ct) {
		string id = CardExchange.MatchIdOf(offer.Message)!;
		CardExchange.Ledger ledger = CardExchange.Ledger.Load(Bot.Name);
		DateTime now = DateTime.UtcNow;
		int n = NumberOf(offer.Id);

		(bool? Fair, Said Why) Remember(bool? fair, Said why) {
			lock (_exchange) {
				if (fair is bool f) {
					_exchange[offer.Id] = f;
				} else {
					_exchange.Remove(offer.Id);
				}
			}

			return (fair, why);
		}

		if (ledger.IsBlocked(offer.Partner)) {
			LeaveHeldSwap(offer.Id);
			Log.Info(new Said("offer {0} is from an account that once sent a bad swap · left for you", n), Bot.Name);

			return Remember(null, new Said("from an account that once sent a bad swap"));
		}

		// Today's swaps done, the next not due yet, or not its day: it waits - never declined for that.
		if ((ledger.DoneToday(now) >= CardExchange.PerDay(Bot.Cfg)) || (now.Ticks < ledger.NextAt) || (Bot.Cfg.LegitMode && !HumanMode.UpFor(Bot))) {
			Log.DebugOnChange($"swapwait:{Bot.Name}:{offer.Id}", $"card swap offer #{offer.Id} waits: today's swaps are done, or it's not its time yet", Bot.Name);

			return Remember(null, new Said("not now"));
		}

		// A match is swapped once: an offer for one this account already swapped, declined or found bad is never accepted.
		if (ledger.Of(id) != null) {
			Said used = new("that swap was already done or dropped");
			Log.Info(new Said("offer {0} isn't a good swap now ({1}) - declining", n, used), Bot.Name);

			return Remember(false, used);
		}

		int currency = Bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) Bot.WalletCurrency;

		if ((currency == 0) || (await (ReadCards?.Invoke(ct) ?? Inventory.ReadAsync(Bot, 753, "6", ct)).ConfigureAwait(false) is not { Complete: true } inventory)) {
			return Remember(null, new Said("couldn't read this account's cards"));
		}

		if (await (ReadOffers?.Invoke(ct) ?? TradeOffers.ActiveAsync(Bot, received: true, sent: true, ct)).ConfigureAwait(false) is not { } live) {
			return Remember(null, new Said("Steam didn't say what's on its offers"));
		}

		// Its cards now, less what's on its other offers (swaps accepted and waiting on a confirmation too) and promised to
		// its other matches - none of those may go in this one, nor count as held.
		HashSet<ulong> leaving = [.. TradeOffers.Committed(live.Where(o => o.Id != offer.Id)), .. CardExchange.Reserved(Bot.Name, Bot.SteamId, now, id)];
		Dictionary<SwapRules.Card, int> held = CardExchange.Held(CardExchange.ReadCards(inventory), leaving);
		CardExchange.Match? match = CardExchange.Find(Bot.SteamId, id);
		long maxLoss = CardExchange.MaxLossCents(Bot.Cfg);
		CardExchange.Rules rules = CardExchange.RulesOf(Bot, now);
		(CardExchange.Verdict verdict, Said why, long give, long get) = CardExchange.Verify(offer, match, held, i => CardExchange.WalletCents(i.Hash, currency, now), maxLoss, rules, now, leaving);

		// No fresh price: the cards in it are asked about in the wallet's own currency, then judged again.
		if (verdict == CardExchange.Verdict.Wait) {
			foreach (TradeOffers.Item item in offer.Giving.Concat(offer.Receiving).Where(i => CardExchange.WalletCents(i.Hash, currency, now) == null).DistinctBy(static i => i.Hash).Take(6)) {
				await (PriceCard?.Invoke(item.Hash, currency, ct) ?? MarketQuotes.FetchAsync(item.Hash, currency, ct)).ConfigureAwait(false);
			}

			(verdict, why, give, get) = CardExchange.Verify(offer, match, held, i => CardExchange.WalletCents(i.Hash, currency, DateTime.UtcNow), maxLoss, rules, now, leaving);
		}

		switch (verdict) {
			case CardExchange.Verdict.Accept:
				lock (_exchange) {
					_exchangeWaits.Remove(offer.Id);
					_exchangeWorth[offer.Id] = (give, get, currency);
				}

				return Remember(true, default);
			case CardExchange.Verdict.Wait: {
				int waits;

				lock (_exchange) {
					waits = _exchangeWaits[offer.Id] = _exchangeWaits.GetValueOrDefault(offer.Id) + 1;
				}

				if (waits < 6) {
					return Remember(null, why);
				}

				goto case CardExchange.Verdict.Decline;
			}
			case CardExchange.Verdict.Decline:
				ledger.SetState(id, "dropped", offer.Partner, now);
				Log.Info(new Said("offer {0} isn't a good swap now ({1}) - declining", n, why), Bot.Name);

				if ((give > 0) && (get > 0)) {
					Log.Info(new Said("card swap · you'd give {0} · you'd get {1}", Spare.Money(give, currency), Spare.Money(get, currency)), Bot.Name);
				}

				return Remember(false, why);
			case CardExchange.Verdict.Bad:
				// Never accepted, never declined behind your back: it's yours to look at. nocat.lol hears at the next sync.
				LeaveHeldSwap(offer.Id);
				ledger.Change(l => {
					if (!l.Blocked.Contains(offer.Partner)) {
						l.Blocked.Add(offer.Partner);
					}

					l.Reports.Add($"{id}|{offer.Partner}");
				});
				ledger.SetState(id, "bad", offer.Partner, now);
				Log.Attention(new Said("card swap offer {0} isn't what was paired ({1}) · left for you", n, why), Bot.Name, Topic.Trades);
				CardExchange.Nudge();

				return Remember(null, why);
			default:
				if (match == null) {
					bool fresh;

					lock (_exchange) {
						fresh = !_exchangeUnknown.TryGetValue(offer.Id, out DateTime since);

						if (fresh) {
							_exchangeUnknown[offer.Id] = since = now;
						}

						if (now - since < TimeSpan.FromHours(3)) {
							if (fresh) {
								CardExchange.Nudge();
							}

							return Remember(null, why);
						}
					}
				}

				LeaveHeldSwap(offer.Id);
				Log.Info(new Said("offer {0} says it's a card swap nocat.lol didn't pair · left for you", n), Bot.Name);

				return Remember(null, why);
		}
	}

	/// <summary>A card swap accepted: counted for the day (and spaced out on a human-mode account), its worth said.</summary>
	/// <param name="done">Through already - not waiting on a confirmation.</param>
	private void ExchangeAccepted(TradeOffers.Offer offer, bool done) {
		if (CardExchange.MatchIdOf(offer.Message) is not { } id) {
			return;
		}

		DateTime now = DateTime.UtcNow;
		CardExchange.Ledger ledger = CardExchange.Ledger.Load(Bot.Name);
		// What comes in, by Steam's own names - what buying cards waits for instead of buying.
		ledger.SetState(id, done ? "done" : "sent", offer.Partner, now, offer.Id, offer.Receiving.Select(static i => i.Hash));
		ledger.Counted(now, Bot.Cfg.LegitMode ? Rng.HumanMinutes(45, 120) : TimeSpan.Zero);

		(long Give, long Get, int Currency) worth;

		lock (_exchange) {
			_exchange.Remove(offer.Id);
			_exchangeWorth.Remove(offer.Id, out worth);
		}

		if (worth.Currency > 0) {
			Log.Info(new Said("card swap · you give {0} · you get {1}", Spare.Money(worth.Give, worth.Currency), Spare.Money(worth.Get, worth.Currency)), Bot.Name);
		}
	}
}
