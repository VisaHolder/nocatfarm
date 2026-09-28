using System.Globalization;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Incoming trade offers.
///
/// The safe half of this is donations - an offer that asks for nothing at all. Those can only ever gain items and
/// never lose any, so no offer accepted on that rule can cost the account a thing; an offer that wants even one
/// item of yours is not a donation and is never accepted by it. That covers the ordinary case of somebody sending
/// cards or a gift to an idler.
///
/// The other half is your own accounts. Anything on that list is trusted with everything, which is what makes it
/// possible to sweep the cards off six idlers onto one account without touching a mouse - and exactly why the
/// list has to be accounts you personally own.
///
/// And card swaps: one for one, judged by <see cref="FairSwap"/> against the cards held at the moment of accepting.
/// Offers from the other accounts in this nocat.farm are always judged that way, so the matcher's offers between
/// them go through without anybody having to turn swaps on for strangers.
///
/// Nothing is acted on the instant it lands. A trade accepted two seconds after it was sent is a bot accepting.
/// </summary>
public sealed class Trading(Bot bot) : BotModule(bot) {
	/// <summary>How many looks that turn up nothing to do before Steam's waiting count stops meaning "hurry".</summary>
	private const int FruitlessBeforeBackingOff = 3;

	/// <summary>Offers sitting out their wait before being accepted or declined.</summary>
	private readonly ReactionQueue<ulong> _waiting = new();
	private readonly HashSet<ulong> _done = [];
	private int _accepted;
	private int _declined;
	private int _fruitless;

	/// <summary>
	/// Cards on the giving side of swaps accepted but still waiting on a phone or email confirmation. Rebuilt from
	/// Steam's own list on every look, so a restart can't forget them and let two swaps spend one card.
	/// </summary>
	private readonly Dictionary<ulong, List<FairSwap.Card>> _promised = [];

	/// <summary>Offers accepted here (of any kind) still waiting on a confirmation, for the status line.</summary>
	private int _awaitingConfirmation;

	// ── numbers and announcements ──

	/// <summary>A short number per live incoming offer - "trade accept new 3" - instead of Steam's 11-digit ids.</summary>
	private readonly Dictionary<ulong, int> _numbers = [];

	private int _nextNumber = 1;

	/// <summary>Offers already announced, and who they were from - kept on disk so a restart doesn't announce them again.</summary>
	private Dictionary<ulong, string>? _announced;

	private string AnnouncedPath => Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "state", $"trades-announced-{Bot.Name}.json");

	public override string Name => "trades";

	public override string Status {
		get {
			List<string> bits = [];

			if (_accepted > 0) {
				bits.Add(Loc.T("{0} accepted", _accepted));
			}

			if (_declined > 0) {
				bits.Add(Loc.T("{0} declined", _declined));
			}

			if (_waiting.Count > 0) {
				bits.Add(Loc.T("{0} waiting", _waiting.Count));
			}

			if (_awaitingConfirmation > 0) {
				bits.Add(Loc.T("{0} to confirm on your phone", _awaitingConfirmation));
			}

			return bits.Count > 0 ? string.Join(" · ", bits) : "";
		}
	}

	/// <summary>How long an offer waits before it's answered - a person's time, not a flat random pick.</summary>
	private TimeSpan TradeWait() => Rng.HumanMinutes(Bot.Cfg.TradeDelayMinMinutes, Bot.Cfg.TradeDelayMaxMinutes);

	// Another account in this nocat.farm counts too: swaps between your own accounts are always looked at.
	private bool Wanted => Bot.Cfg.AcceptDonations || Bot.Cfg.AcceptFromMasters || Bot.Cfg.AcceptFairCardSwaps || Bot.Cfg.DeclineOtherTrades
		|| ((BotManager.Instance?.All.Count ?? 0) > 1);

	/// <summary>
	/// What the fair-swap test said about each offer. A "no" is kept only for a while: cards move, and an offer
	/// that wasn't fair an hour ago - one of your own accounts' swaps, especially - may be now.
	/// </summary>
	private readonly Dictionary<ulong, (bool Fair, DateTime At)> _fair = [];

	private static readonly TimeSpan NotFairFor = TimeSpan.FromMinutes(30);

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			// A trade accepted at 4am by an account whose friends list says it is offline is not a person. When
			// human mode owns the account, offers simply sit until morning - which is what would really happen -
			// and anything already waiting goes back on hold, to get a fresh wait once the account is up.
			bool awake = HumanMode.AwakeFor(Bot);

			if (!awake) {
				_waiting.Hold();
			}

			// Asleep, only donations may still go through - and only when that's switched on.
			bool nightDonations = !awake && Bot.Cfg.AcceptDonations && Bot.Cfg.DonationsWhileAsleep;

			if (Wanted && Bot.IsOnline && Bot.Web.Ready && (awake || nightDonations) && ShouldLook()) {
				try {
					await CheckAsync(awake, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't check trade offers: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
				}
			}

			// Sleeps until the gap runs out OR Steam says an offer arrived, whichever comes first. That is what
			// makes the long gap safe: nothing waits an hour, it just stops asking when there is nothing to ask.
			try {
				await Bot.WaitForTradeOfferAsync(NextGap(), ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}
	}

	/// <summary>
	/// Whether there is any point opening the trade offers page at all.
	///
	/// Steam pushes the number of waiting offers over the client connection - on login and again the instant it
	/// changes - so an account it has told us has none does not need to go and look. Fetching that page every
	/// few minutes on every account, to learn nothing, is what got the community site answering 429; it is also
	/// nothing like what a person does. When Steam hasn't told us yet, or something is mid-flight, we still look.
	/// </summary>
	private bool ShouldLook() {
		if (_waiting.Count > 0) {
			return true;   // an offer is sitting out its wait and has to be re-read to be acted on
		}

		// A zero here is Steam's word that nothing is waiting, and it is trustworthy: the notification sweep
		// deliberately resets this to "don't know" and wakes us for one look, so an offer that was already
		// waiting when the sweep ran is found rather than swept away with everything else.
		return Bot.TradeOffersWaiting != 0;
	}

	/// <summary>
	/// How long to wait before looking again. Minutes while something is actually in flight, the better part of
	/// an hour otherwise - the slow pass exists only to catch an offer Steam never pushed a notification for.
	/// </summary>
	private TimeSpan NextGap() {
		if (_waiting.Count > 0) {
			return Rng.Minutes(4, 9);
		}

		// Steam's counter can stand at one for something we are never going to touch - an offer held in escrow,
		// or one from a stranger on an account that accepts nothing. Coming back every five minutes to re-read
		// the same untouchable offer is the same wasted request as before, so after a few fruitless looks the
		// count stops being taken as a reason to hurry.
		return (Bot.TradeOffersWaiting > 0) && (_fruitless < FruitlessBeforeBackingOff) ? Rng.Minutes(4, 9) : Rng.Minutes(45, 90);
	}

	private async Task CheckAsync(bool awake, CancellationToken ct) {
		// Steam's own list of this account's live incoming offers. Null is "Steam didn't answer" - try again later,
		// never read as "nothing waiting".
		if (await TradeOffers.ActiveAsync(Bot, received: true, sent: true, ct).ConfigureAwait(false) is not { } live) {
			return;
		}

		// Accepted already and waiting on a confirmation: not ours to accept again, and its cards are spoken for.
		List<TradeOffers.Offer> awaiting = [.. live.Where(static o => o.AwaitingConfirmation)];
		List<TradeOffers.Offer> offers = [.. live.Where(static o => !o.Ours && (o.State == TradeOffers.Active) && (o.ConfirmationMethod == 0))];
		HashSet<ulong> ids = [.. live.Select(static o => o.Id)];

		// Cards on their way out: accepted swaps waiting on a confirmation, and this account's own offers (a 'match'
		// swap it sent, for one) that are still out. They're in the inventory, but they're going.
		lock (_promised) {
			_promised.Clear();

			foreach (TradeOffers.Offer o in awaiting.Concat(live.Where(static o => o.Ours && (o.State is TradeOffers.Active or TradeOffers.NeedsConfirmation)))) {
				if (FairSwap.CardsLeaving(o) is { Count: > 0 } leaving) {
					_promised[o.Id] = leaving;
				}
			}
		}

		_awaitingConfirmation = awaiting.Count;

		// Anything no longer live was accepted, declined or withdrawn somewhere else - forget it.
		_waiting.RetainOnly(offers.Select(static o => o.Id));

		lock (_done) {
			_done.IntersectWith(ids);
		}

		lock (_fair) {
			foreach (ulong gone in _fair.Keys.Where(k => !ids.Contains(k)).ToList()) {
				_fair.Remove(gone);
			}
		}

		lock (_nightDue) {
			foreach (ulong gone in _nightDue.Keys.Where(k => !ids.Contains(k)).ToList()) {
				_nightDue.Remove(gone);
			}
		}

		// Offers held while the account slept get their wait now, from the moment it's up - not all at once.
		foreach ((ulong id, DateTime due) in awake ? _waiting.Arm(DateTime.UtcNow, TradeWait) : []) {
			Log.Debug(new Said("trade offer #{0} waited for the account to wake - handling it around {1}", id, (Func<string>) (() => Fmt.Clock(due))), Bot.Name);
		}

		bool actedOnSomething = false;
		bool swappedThisLook = false;
		_answeredThisLook = false;
		HashSet<ulong> fleet = [.. (BotManager.Instance?.All ?? []).Where(b => (b != Bot) && (b.SteamId != 0)).Select(static b => b.SteamId)];
		Dictionary<ulong, Way> allowed = Allowed();

		KeepNumbers(offers);
		await AnnounceAsync(offers, ids, allowed, fleet, awake, ct).ConfigureAwait(false);

		foreach (TradeOffers.Offer offer in offers) {
			// One offer that can't be read or judged must not stop the rest - donations and your own accounts included.
			// Asleep: donations only; everything else stays put for the morning.
			if (!awake && !offer.IsPureDonation) {
				continue;
			}

			try {
				if (!awake) {
					actedOnSomething |= await NightDonationAsync(offer, ct).ConfigureAwait(false);

					continue;
				}

				(bool acted, bool swapped) = await HandleAsync(offer, allowed, fleet, swappedThisLook, ct).ConfigureAwait(false);
				actedOnSomething |= acted;
				swappedThisLook |= swapped;
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("couldn't handle trade offer #{0}: {1}: {2}", offer.Id, e.GetType().Name, e.Message), Bot.Name);
			}
		}

		_fruitless = actedOnSomething ? 0 : _fruitless + 1;

		// We have just read the list, so we know better than the notification counter does - tell it. Without
		// this a count left at "don't know" (which is what the notification sweep deliberately does) stayed that
		// way for ever, and the account went on looking every hour or so with nothing to find.
		Bot.NoteTradeOffersSeen(offers.Count);
	}

	/// <returns>Whether it's being dealt with (queued or acted on), and whether a card swap was accepted just now.</returns>
	private async Task<(bool Acted, bool Swapped)> HandleAsync(TradeOffers.Offer offer, Dictionary<ulong, Way> allowed, HashSet<ulong> fleet, bool swappedThisLook, CancellationToken ct) {
		lock (_done) {
			if (_done.Contains(offer.Id)) {
				return (false, false);
			}
		}

		bool fromMaster = Auto(offer, allowed);
		bool donation = Bot.Cfg.AcceptDonations && offer.IsPureDonation;
		bool swapWanted = Bot.Cfg.AcceptFairCardSwaps || fleet.Contains(offer.Partner);
		bool? swap = !fromMaster && !donation && swapWanted ? await FairSwapAsync(offer, ct).ConfigureAwait(false) : false;
		bool fair = swap == true;
		bool accept = fromMaster || donation || fair;

		// The swap test couldn't finish: never declined on a guess. Left alone, and judged again at the next look.
		if (!accept && (swap == null)) {
			return (false, false);
		}

		if (!accept && !Bot.Cfg.DeclineOtherTrades) {
			return (false, false);   // leave it sitting there for the user to look at
		}

		// Everything waits its turn. The wait is per offer, so two arriving together are not handled together.
		if (_waiting.Add(offer.Id, DateTime.UtcNow + TradeWait()) && (_waiting.DueOf(offer.Id) is { } due)) {
			Said what = !accept ? new Said("unwanted") : fromMaster ? new Said("from one of your accounts") : fair ? new Said("a fair card swap") : new Said("a donation");
			Log.Info(new Said("offer {0} ({1}: {2}) - handling it in {3}", NumberOf(offer.Id), what, offer.Describe, Fmt.Hm((int) Math.Max(1, (due - DateTime.UtcNow).TotalMinutes))), Bot.Name);
		}

		if ((_waiting.DueOf(offer.Id) is not { } when) || (DateTime.UtcNow < when)) {
			return (true, false);
		}

		// Human mode answers one offer per look - the rest wait for the next one, minutes later. Several that came
		// due together (the morning after a night on hold, typically) were all accepted seconds apart.
		if (Bot.Cfg.LegitMode && Bot.Cfg.OneTradeAtATime && _answeredThisLook) {
			return (true, false);
		}

		if (fair && !fromMaster && !donation) {
			// One swap per look: the next is judged against the cards as they are after this one.
			if (swappedThisLook) {
				return (true, false);
			}

			// Would it sit in a trade hold? Steam only says so up front through its hold lookup - an offer's own
			// hold date appears once it's already held. A held swap can be cancelled and leaves cards in limbo.
			TimeSpan? hold = await TradeOffers.HoldAsync(Bot, offer.Partner, ct).ConfigureAwait(false);

			if (hold == null) {
				return (true, false);   // Steam didn't say - ask again at the next look
			}

			if (hold > TimeSpan.Zero) {
				ForgetVerdict(offer.Id);
				_waiting.Remove(offer.Id);
				Log.Info(new Said("trade offer #{0} is no longer a fair card swap - {1}; left alone", offer.Id, new Said("the cards would sit in a trade hold")), Bot.Name);

				return (true, false);
			}

			// Judged again NOW, against the cards held now. The first verdict is minutes old - hours, if it waited
			// out the night - and other swaps may have moved cards since.
			(bool? still, Said why) = await FairSwap.JudgeAsync(Bot, offer, Promised(), ct).ConfigureAwait(false);

			if (still != true) {
				ForgetVerdict(offer.Id);
				_waiting.Remove(offer.Id);

				if (still == false) {
					Log.Info(new Said("trade offer #{0} is no longer a fair card swap - {1}; left alone", offer.Id, why), Bot.Name);
				}

				return (true, false);
			}
		}

		bool swapped = false;
		_answeredThisLook = true;

		if (accept) {
			// A fair swap with a stranger is accepted, but the confirmation waits for you - only accounts you allowed (and
			// your own) are confirmed without asking.
			Accepted result = await AcceptAsync(offer, ct, mayConfirm: fromMaster || fleet.Contains(offer.Partner)).ConfigureAwait(false);

			if (result == Accepted.Failed) {
				return (true, false);
			}

			Finish(offer.Id);
			swapped = fair && !fromMaster && !donation;

			if (swapped) {
				lock (_fair) {
					_fair.Clear();   // every other verdict was made against cards that have now changed
				}
			}

			if (result == Accepted.Done) {
				_accepted++;
				Log.Trade(new Said("accepted offer {0} from {1} - {2} item(s) in, {3} out", NumberOf(offer.Id), Who(offer), offer.Receiving.Sum(static i => i.Amount), offer.Giving.Sum(static i => i.Amount)), Bot.Name, good: true);
			} else {
				// Accepted, but nothing moves until it's confirmed. Not a reward yet - and its cards stay promised,
				// so another swap can't be judged as if they were still here.
				if (swapped && (FairSwap.CardsOf(offer) is { } cards)) {
					lock (_promised) {
						_promised[offer.Id] = cards.Giving;
					}
				}

				Log.Trade(NeedsConfirming(offer, result), Bot.Name);
			}
		} else if (await DeclineAsync(offer, ct).ConfigureAwait(false)) {
			_declined++;
			Log.Trade(new Said("declined offer {0} from {1}", NumberOf(offer.Id), Who(offer)), Bot.Name);
			Finish(offer.Id);
		}

		await Task.Delay(Rng.Seconds(3, 12), ct).ConfigureAwait(false);

		return (true, swapped);
	}

	private void ForgetVerdict(ulong id) {
		lock (_fair) {
			_fair.Remove(id);
		}
	}

	/// <summary>Set once an offer has been answered this look - a human-mode account answers one per look.</summary>
	private bool _answeredThisLook;

	/// <summary>When each donation that arrived at night is due - its own short wait, like in the day.</summary>
	private readonly Dictionary<ulong, DateTime> _nightDue = [];

	/// <summary>A donation while the account sleeps: the same short wait, then accepted. Returns whether it's being handled.</summary>
	private async Task<bool> NightDonationAsync(TradeOffers.Offer offer, CancellationToken ct) {
		lock (_done) {
			if (_done.Contains(offer.Id)) {
				return false;
			}
		}

		DateTime due;

		lock (_nightDue) {
			if (!_nightDue.TryGetValue(offer.Id, out due)) {
				due = DateTime.UtcNow + TradeWait();
				_nightDue[offer.Id] = due;
				Log.Info(new Said("trade offer #{0} ({1}: {2}) - handling it in {3}", offer.Id, new Said("a donation"), offer.Describe,
					Fmt.Hm((int) Math.Max(1, (due - DateTime.UtcNow).TotalMinutes))), Bot.Name);

				return true;
			}
		}

		if ((DateTime.UtcNow < due) || (Bot.Cfg.OneTradeAtATime && _answeredThisLook)) {
			return true;
		}

		_answeredThisLook = true;

		if (await AcceptAsync(offer, ct).ConfigureAwait(false) == Accepted.Failed) {
			return true;
		}

		lock (_nightDue) {
			_nightDue.Remove(offer.Id);
		}

		_accepted++;
		Log.Trade(new Said("accepted offer {0} from {1} - {2} item(s) in, {3} out", NumberOf(offer.Id), Who(offer), offer.Receiving.Sum(static i => i.Amount), 0), Bot.Name, good: true);
		Finish(offer.Id);

		return true;
	}

	private void Finish(ulong id) {
		lock (_done) {
			_done.Add(id);
		}

		_waiting.Remove(id);
		Forget(id);
	}

	// ── who it trades with by itself ──

	/// <summary>Which way an account may trade with this one by itself.</summary>
	[Flags]
	public enum Way { None = 0, From = 1, To = 2, Both = From | To }

	/// <summary>
	/// The "Trade by itself with" list, as SteamIDs. Empty falls back to "Your own accounts" both ways - but only with
	/// "Accept anything from your own accounts" on, which is what that setting always meant.
	/// </summary>
	public Dictionary<ulong, Way> Allowed() {
		Dictionary<ulong, Way> allowed = [];
		string list = Bot.Cfg.AutoTradeWith.Trim();

		if (list.Length == 0) {
			if (Bot.Cfg.AcceptFromMasters) {
				foreach (ulong id in Social.ParseIds(Bot.Cfg.TradeMasters)) {
					allowed[id] = Way.Both;
				}
			}

			return allowed;
		}

		foreach (string entry in list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			int colon = entry.IndexOf(':');
			string who = colon < 0 ? entry : entry[..colon];
			string way = colon < 0 ? "both" : entry[(colon + 1)..].ToLowerInvariant();
			ulong id = ulong.TryParse(who, out ulong n) && (n > 76561197960265728UL) ? n
				: BotManager.Instance?.Get(who)?.SteamId ?? 0;

			if (id != 0) {
				allowed[id] = way switch { "from" => Way.From, "to" => Way.To, _ => Way.Both };
			}
		}

		return allowed;
	}

	/// <summary>Whether this offer is one to take without asking: nothing leaves and they're allowed "from", or items leave and they're allowed "to".</summary>
	private static bool Auto(TradeOffers.Offer offer, Dictionary<ulong, Way> allowed) =>
		allowed.TryGetValue(offer.Partner, out Way way) && (offer.Giving.Count == 0 ? way.HasFlag(Way.From) : way.HasFlag(Way.To));

	// ── numbers ──

	/// <summary>The short number an offer goes by - the same in the log, on Telegram, in 'offers' and for 'trade accept'.</summary>
	public int NumberOf(ulong id) {
		lock (_numbers) {
			if (!_numbers.TryGetValue(id, out int n)) {
				n = _nextNumber++;
				_numbers[id] = n;
			}

			return n;
		}
	}

	/// <summary>Numbers for every live offer, oldest first; the counter starts over once nothing is waiting.</summary>
	private void KeepNumbers(List<TradeOffers.Offer> live) {
		lock (_numbers) {
			HashSet<ulong> ids = [.. live.Select(static o => o.Id)];

			foreach (ulong gone in _numbers.Keys.Where(k => !ids.Contains(k)).ToList()) {
				_numbers.Remove(gone);
			}

			if (_numbers.Count == 0) {
				_nextNumber = 1;
			}
		}

		foreach (TradeOffers.Offer o in live.OrderBy(static o => o.Id)) {
			NumberOf(o.Id);
		}
	}

	// ── announcements ──

	/// <summary>Who an offer is from, as it was announced - the SteamID when it wasn't.</summary>
	private string Who(TradeOffers.Offer offer) {
		lock (_numbers) {
			return (_announced ?? []).TryGetValue(offer.Id, out string? who) ? who : offer.Partner.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	/// <summary>
	/// Every new offer is announced once - log, pop-up, Discord/Telegram - saying who it's from, what would come and go,
	/// and what happens next. Offers that went away without being answered here are announced too.
	/// </summary>
	private async Task AnnounceAsync(List<TradeOffers.Offer> offers, HashSet<ulong> live, Dictionary<ulong, Way> allowed, HashSet<ulong> fleet, bool awake, CancellationToken ct) {
		_announced ??= LoadAnnounced();
		bool changed = false;

		foreach ((ulong id, string who) in _announced.Where(a => !live.Contains(a.Key)).ToList()) {
			lock (_done) {
				if (!_done.Contains(id)) {
					Log.Trade(new Said("an offer from {0} is gone - cancelled, answered somewhere else, or it expired", who), Bot.Name);
				}
			}

			_announced.Remove(id);
			changed = true;
		}

		foreach (TradeOffers.Offer offer in offers.Where(o => !_announced.ContainsKey(o.Id))) {
			string name = await SteamNames.OfAsync(Bot, offer.Partner, ct).ConfigureAwait(false);
			bool own = fleet.Contains(offer.Partner) || allowed.ContainsKey(offer.Partner);
			string who = own ? new Said("{0} (one of your accounts)", name).ToString() : name;
			int n = NumberOf(offer.Id);
			_announced[offer.Id] = who;
			changed = true;

			Log.Trade(new Said("new trade offer {0} from {1}: you get {2}, you give {3} - {4}", n, who, Items(offer.Receiving), Items(offer.Giving), Plan(offer, allowed, fleet, awake, n)), Bot.Name);
		}

		if (changed) {
			SaveAnnounced();
		}
	}

	/// <summary>What nocat.farm is going to do with it, in words.</summary>
	private Said Plan(TradeOffers.Offer offer, Dictionary<ulong, Way> allowed, HashSet<ulong> fleet, bool awake, int n) {
		bool later = !awake;

		if (Bot.Cfg.AcceptDonations && offer.IsPureDonation) {
			return later && !Bot.Cfg.DonationsWhileAsleep ? new Said("a donation - accepting it once the account is up") : new Said("a donation - accepting it in a few minutes");
		}

		if (Auto(offer, allowed)) {
			return later ? new Said("you allowed this account - accepting it once the account is up") : new Said("you allowed this account - accepting it in a few minutes");
		}

		if ((Bot.Cfg.AcceptFairCardSwaps || fleet.Contains(offer.Partner)) && (offer.Giving.Count > 0) && (offer.Giving.Count == offer.Receiving.Count)) {
			return new Said("checking whether it's a fair card swap - accepted only if it is, otherwise it waits for you (trade accept {0} {1})", Bot.Name, n);
		}

		return Bot.Cfg.DeclineOtherTrades
			? new Said("declining it in a few minutes (\"Decline everything else\" is on) - trade accept {0} {1} keeps it", Bot.Name, n)
			: new Said("waiting for you - trade accept {0} {1} or trade decline {0} {1}", Bot.Name, n);
	}

	/// <summary>"3 items: Alpha x2, Beta" - or "nothing".</summary>
	public static Said Items(List<TradeOffers.Item> items) {
		uint total = (uint) items.Sum(static i => i.Amount);

		if (total == 0) {
			return new Said("no items");
		}

		var names = items.GroupBy(static i => i.Described && (i.Name.Length > 0) ? i.Name : "?")
			.Select(static g => (Name: g.Key, Count: g.Sum(static i => i.Amount)))
			.OrderByDescending(static g => g.Count)
			.ToList();
		string shown = string.Join(", ", names.Take(3).Select(static g => g.Count > 1 ? $"{g.Name} x{g.Count}" : g.Name));

		return names.Count > 3
			? new Said("{0} item(s): {1} and {2} more", total, shown, names.Count - 3)
			: new Said("{0} item(s): {1}", total, shown);
	}

	private Said NeedsConfirming(TradeOffers.Offer offer, Accepted result) => result == Accepted.NeedsEmail
		? new Said("offer {0} from {1} is accepted but needs confirming from the email Steam sent", NumberOf(offer.Id), Who(offer))
		: Bot.CanConfirmTrades
			? new Said("offer {0} from {1} is accepted and waiting for you to confirm it - 'confirmations {2}' or the Authenticator page", NumberOf(offer.Id), Who(offer), Bot.Name)
			: new Said("offer {0} from {1} is accepted but needs confirming on your phone", NumberOf(offer.Id), Who(offer));

	private void Forget(ulong id) {
		if ((_announced != null) && _announced.Remove(id)) {
			SaveAnnounced();
		}
	}

	private Dictionary<ulong, string> LoadAnnounced() {
		try {
			return File.Exists(AnnouncedPath)
				? System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, string>>(File.ReadAllText(AnnouncedPath)) ?? []
				: [];
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the announced offers: {0}", e.Message), Bot.Name);

			return [];
		}
	}

	private void SaveAnnounced() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(AnnouncedPath)!);
			AtomicFile.Write(AnnouncedPath, System.Text.Json.JsonSerializer.Serialize(_announced));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the announced offers: {0}", e.Message), Bot.Name);
		}
	}

	// ── answering by hand ──

	/// <summary>
	/// 'trade accept|decline &lt;account&gt; &lt;number|all&gt;' - answering by hand. An accepted offer that sends items out
	/// is confirmed by itself when the authenticator is here: you asked for it, so that IS the confirmation.
	/// </summary>
	public async Task<string> AnswerAsync(bool accept, string which, CancellationToken ct = default) {
		if (!Bot.IsOnline || !Bot.Web.Ready) {
			return new Said("{0} isn't logged in", Bot.Name).ToString();
		}

		if (await TradeOffers.ActiveAsync(Bot, received: true, sent: false, ct).ConfigureAwait(false) is not { } live) {
			return new Said("Steam didn't answer - try again in a minute").ToString();
		}

		List<TradeOffers.Offer> incoming = [.. live.Where(static o => !o.Ours && (o.State == TradeOffers.Active))];
		KeepNumbers(incoming);
		_announced ??= LoadAnnounced();

		List<TradeOffers.Offer> picked = which.Equals("all", StringComparison.OrdinalIgnoreCase) ? incoming
			: int.TryParse(which.TrimStart('#'), out int n) ? [.. incoming.Where(o => NumberOf(o.Id) == n)]
			: [];

		if (picked.Count == 0) {
			return incoming.Count == 0
				? new Said("{0} has no trade offers waiting", Bot.Name).ToString()
				: new Said("{0} has no offer {1} - 'offers {0}' lists them with their numbers", Bot.Name, which).ToString();
		}

		List<string> lines = [];

		foreach (TradeOffers.Offer offer in picked) {
			int num = NumberOf(offer.Id);

			if (!_announced.ContainsKey(offer.Id)) {
				_announced[offer.Id] = await SteamNames.OfAsync(Bot, offer.Partner, ct).ConfigureAwait(false);
			}

			if (!accept) {
				if (await DeclineAsync(offer, ct).ConfigureAwait(false)) {
					_declined++;
					Log.Trade(new Said("declined offer {0} from {1}", num, Who(offer)), Bot.Name);
					Finish(offer.Id);
					lines.Add(new Said("declined offer {0}", num).ToString());
				} else {
					lines.Add(new Said("offer {0}: Steam didn't take the decline - try again", num).ToString());
				}

				continue;
			}

			Accepted result = await AcceptAsync(offer, ct).ConfigureAwait(false);

			switch (result) {
				case Accepted.Done:
					_accepted++;
					Log.Trade(new Said("accepted offer {0} from {1} - {2} item(s) in, {3} out", num, Who(offer), offer.Receiving.Sum(static i => i.Amount), offer.Giving.Sum(static i => i.Amount)), Bot.Name, good: true);
					Finish(offer.Id);
					lines.Add(new Said("accepted offer {0}", num).ToString());

					break;
				case Accepted.Failed:
					lines.Add(new Said("offer {0}: Steam didn't take the accept - try again", num).ToString());

					break;
				default:
					Log.Trade(NeedsConfirming(offer, result), Bot.Name);
					Finish(offer.Id);
					lines.Add(NeedsConfirming(offer, result).ToString());

					break;
			}

			await Task.Delay(Rng.Seconds(1, 3), ct).ConfigureAwait(false);
		}

		return string.Join(Environment.NewLine, lines);
	}

	private List<FairSwap.Card> Promised() {
		lock (_promised) {
			return [.. _promised.Values.SelectMany(static c => c)];
		}
	}

	/// <summary>
	/// Whether this is a one-for-one card swap that only helps the sets. Offers whose counts don't even match are
	/// passed over without reading anything; the rest are judged once here and again at the moment of accepting.
	/// </summary>
	/// <returns>Whether it's a fair swap - null when that couldn't be worked out this time.</returns>
	private async Task<bool?> FairSwapAsync(TradeOffers.Offer offer, CancellationToken ct) {
		if ((offer.Giving.Count == 0) || (offer.Giving.Count != offer.Receiving.Count)) {
			return false;
		}

		lock (_fair) {
			if (_fair.TryGetValue(offer.Id, out (bool Fair, DateTime At) known) && (known.Fair || (DateTime.UtcNow - known.At < NotFairFor))) {
				return known.Fair;
			}
		}

		(bool? fair, Said why) = await FairSwap.JudgeAsync(Bot, offer, Promised(), ct).ConfigureAwait(false);

		// Couldn't tell - Steam didn't answer, the inventory didn't read. Not remembered, so the next look tries again.
		if (fair is not bool verdict) {
			Log.Debug(new Said("trade offer #{0}: couldn't check it as a card swap yet - {1}", offer.Id, why), Bot.Name);

			return null;
		}

		bool firstTime;

		lock (_fair) {
			firstTime = !_fair.ContainsKey(offer.Id);
			_fair[offer.Id] = (verdict, DateTime.UtcNow);
		}

		if (!verdict && firstTime) {
			Log.Info(new Said("trade offer #{0} isn't a fair card swap - {1}; left alone", offer.Id, why), Bot.Name);
		}

		return verdict;
	}

	private enum Accepted { Failed, Done, NeedsPhone, NeedsEmail }

	/// <param name="mayConfirm">Whether an accept that needs a mobile confirmation may be confirmed here without asking:
	/// yes for accounts on the "trade by itself with" list, your own accounts, and anything you accepted by hand.</param>
	private async Task<Accepted> AcceptAsync(TradeOffers.Offer offer, CancellationToken ct, bool mayConfirm = true) {
		Dictionary<string, string> form = new() {
			["sessionid"] = Bot.Web.SessionId,
			["serverid"] = "1",
			["tradeofferid"] = offer.Id.ToString(CultureInfo.InvariantCulture),
			["partner"] = offer.Partner.ToString(CultureInfo.InvariantCulture),
			["captcha"] = ""
		};

		Uri referer = new(WebSession.Community, $"/tradeoffer/{offer.Id}/");
		string? body = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/tradeoffer/{offer.Id}/accept"), form, referer, ct).ConfigureAwait(false);

		if (body == null) {
			return Accepted.Failed;
		}

		// Steam says what the accept still needs as true/false flags - reading the key alone called an email
		// confirmation a phone one.
		if (Flag(body, "needs_mobile_confirmation")) {
			return mayConfirm && await Bot.ConfirmMobileAsync(offer.Id, true, ct).ConfigureAwait(false) ? Accepted.Done : Accepted.NeedsPhone;
		}

		if (Flag(body, "needs_email_confirmation")) {
			return Accepted.NeedsEmail;
		}

		// A trade offer accepted successfully answers with JSON carrying the trade id. Treating "any 200 without
		// strError" as success meant an HTML refusal page - which is what Steam serves for an expired session or
		// a rate limit - was logged as a reward for items that never arrived, and never retried.
		return body.Contains("tradeid", StringComparison.OrdinalIgnoreCase)
			|| body.Contains("\"success\":1", StringComparison.Ordinal)
			|| body.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase)
			? Accepted.Done
			: Accepted.Failed;
	}

	/// <summary>A true/1 JSON flag in Steam's answer.</summary>
	private static bool Flag(string body, string name) {
		try {
			using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);

			return doc.RootElement.TryGetProperty(name, out System.Text.Json.JsonElement v)
				&& ((v.ValueKind == System.Text.Json.JsonValueKind.True) || ((v.ValueKind == System.Text.Json.JsonValueKind.Number) && (v.GetInt32() == 1)));
		} catch (System.Text.Json.JsonException) {
			return false;
		}
	}

	/// <summary>
	/// 'trade cancel &lt;account&gt; &lt;offer id|all&gt;' - take back offers this account SENT that haven't gone through yet,
	/// including ones stuck waiting on a confirmation that will never come (an account with no authenticator).
	/// </summary>
	public async Task<string> CancelSentAsync(string which, CancellationToken ct = default) {
		if (!Bot.IsOnline || !Bot.Web.Ready) {
			return new Said("{0} isn't logged in", Bot.Name).ToString();
		}

		if (await TradeOffers.ActiveAsync(Bot, received: false, sent: true, ct).ConfigureAwait(false) is not { } live) {
			return new Said("Steam didn't answer - try again in a minute").ToString();
		}

		List<TradeOffers.Offer> sent = [.. live.Where(static o => o.Ours && (o.State is TradeOffers.Active or TradeOffers.NeedsConfirmation))];
		List<TradeOffers.Offer> picked = which.Equals("all", StringComparison.OrdinalIgnoreCase) ? sent
			: ulong.TryParse(which.TrimStart('#'), out ulong id) ? [.. sent.Where(o => o.Id == id)]
			: [];

		if (picked.Count == 0) {
			return sent.Count == 0
				? new Said("{0} has no sent offers waiting", Bot.Name).ToString()
				: new Said("{0} has no sent offer {1} - 'offers {0}' lists them", Bot.Name, which).ToString();
		}

		List<string> lines = [];

		foreach (TradeOffers.Offer offer in picked) {
			Dictionary<string, string> form = new() { ["sessionid"] = Bot.Web.SessionId };
			string? body = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/tradeoffer/{offer.Id}/cancel"), form,
				new Uri(WebSession.Community, "/profiles/" + Bot.SteamId + "/tradeoffers/sent/"), ct).ConfigureAwait(false);
			string who = await SteamNames.OfAsync(Bot, offer.Partner, ct).ConfigureAwait(false);

			if (body != null) {
				Log.Trade(new Said("cancelled the offer it sent to {0} (#{1})", who, offer.Id), Bot.Name);
				lines.Add(new Said("cancelled #{0} to {1}", offer.Id, who).ToString());
			} else {
				lines.Add(new Said("#{0}: Steam didn't take the cancel - try again", offer.Id).ToString());
			}
		}

		return string.Join(Environment.NewLine, lines);
	}

	private async Task<bool> DeclineAsync(TradeOffers.Offer offer, CancellationToken ct) {
		Dictionary<string, string> form = new() { ["sessionid"] = Bot.Web.SessionId };
		string? body = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/tradeoffer/{offer.Id}/decline"), form, new Uri(WebSession.Community, "/profiles/" + Bot.SteamId + "/tradeoffers/"), ct).ConfigureAwait(false);

		return body != null;
	}
}
