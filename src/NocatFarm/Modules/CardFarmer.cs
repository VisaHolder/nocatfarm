using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>When a human-mode account farms cards - the FarmCardsWhen setting.</summary>
public static class FarmWhen {
	/// <summary>In human mode's sittings: the card game is what the day plays; nights are for the overnight games.</summary>
	public const int Day = 0;

	/// <summary>Only while it's asleep and invisible; the day plays the usual games.</summary>
	public const int Night = 1;

	/// <summary>The moment there are cards, day and night.</summary>
	public const int Any = 2;

	/// <summary>
	/// In human mode's sittings, but only some of them (<c>CardSittingsPct</c>) - the rest play the usual games, so
	/// cards drop in between the games it's actually meant to be into instead of replacing them until they're gone.
	/// </summary>
	public const int Mixed = 3;

	/// <summary>Cards farm inside human mode's own sittings - every one of them (Day) or some of them (Mixed).</summary>
	public static bool InSittings(int when) => when is Day or Mixed;
}

/// <summary>One game with cards still to drop.</summary>
public sealed class FarmTarget {
	public uint AppId { get; init; }
	public string GameName { get; init; } = "";
	public float HoursPlayed { get; set; }
	public int CardsRemaining { get; set; }

	public override string ToString() => $"{GameName} ({AppId})";
}

/// <summary>
/// Trading-card farming.
///
/// Steam does not expose "cards left" over the client protocol, so this reads the same badge pages a browser
/// would, using the account's own web session. Two modes, exactly as Steam's own rules require:
///
///   • BUMP HOURS - a limited account gets no drops at all until a game has a few hours on it. Playtime does
///     accrue on up to 32 games at once while drops do not, so under-threshold games are played as one big
///     batch until they cross the line. This is pure setup work, not farming.
///   • FARM - once a game is over the threshold it is played on its own and watched until its last card drops.
///
/// Drops are detected from Steam's own item-announcement push, with a periodic re-check as a backstop, so a
/// finished game is noticed in seconds instead of at the end of a fixed polling interval.
/// </summary>
public sealed class CardFarmer(Bot bot) : BotModule(bot) {
	private const int MaxBadgePages = 20;
	private const int RescanMinutesLow = 45;
	private const int RescanMinutesHigh = 90;

	/// <summary>How many apps the store is asked about per badge read, at most - the rest wait for the next read.</summary>
	private const int StoreChecksPerRead = 8;

	/// <summary>
	/// Badge rows that said "no drops" but whose game has been played since, and when each was last looked at on its
	/// own card page. Kept on disk so the weekly pace survives restarts.
	/// </summary>
	private readonly Dictionary<uint, (float Hours, DateTime Checked)> _zeroRows = [];
	private bool _zeroRowsLoaded;

	// Optional cap on how many accounts farm at once. Farming is the only thing here that hammers the community
	// site, so on a machine running a lot of accounts this is the knob that keeps Steam happy.
	private static SemaphoreSlim? _slots;
	private static int _limit;

	public static void ApplyConcurrencyLimit(int limit) {
		if (limit == _limit) {
			return;
		}

		_limit = limit;
		_slots = limit > 0 ? new SemaphoreSlim(limit, limit) : null;
	}

	private readonly List<FarmTarget> _queue = [];
	private Said _status = new("idle");

	/// <summary>
	/// Games we have given up on, so the queue stops handing back the same one forever.
	///
	/// Giving up used to just return. The next pass rediscovered the identical list, the sort produced the
	/// identical head, and the same game was farmed for another full limit - for as long as the account stayed
	/// up, earning nothing the whole time. That is the failure a drop watchdog is supposed to catch, and it was
	/// happening in the farmer itself.
	///
	/// Deliberately NOT measured in playtime. Human mode drives SetPlaying/StopPlaying without touching
	/// IsFarming, Paused or PlayingBlocked, so any wall-clock "we have been playing X for N hours" figure is a
	/// number this module is in no position to know. The two things it CAN trust are its own decision to give
	/// up, and the card count on the badge page - so the strike count is driven by the former and reset by the
	/// latter.
	/// </summary>
	private sealed class Stall {
		public int Strikes;
		public int CardsWhenParked;
		public DateTime ParkedUntil;
	}

	private readonly Dictionary<uint, Stall> _stalled = [];

	/// <summary>So "nothing left to farm" is said once per run rather than on every rescan.</summary>
	private bool _saidNothingLeft;

	public override string Name => "cards";
	public override string Status => _status;

	private CardPace? _pace;
	private CardPace Pace => _pace ??= new CardPace(Bot.Name);

	/// <summary>About how many minutes of farming are left for everything queued - a rough figure, see <see cref="CardPace"/>.</summary>
	public int EstimateMinutes => Pace.EstimateMinutes(Queue, Bot.Cfg.HoursUntilCardDrops);

	/// <summary>Minutes per card behind the estimate, and whether that's this account's own pace or Steam's usual 30.</summary>
	public (double Minutes, bool Learned) MinutesPerCard => (Pace.Minutes, Pace.Learned);

	private string AllDoneIn => Fmt.Rough(EstimateMinutes);

	/// <summary>
	/// The game it will farm next - what human mode plays in its next sitting when cards farm in the day. 0 with
	/// nothing to farm.
	/// </summary>
	public uint NextGame => Bot.CardsRemaining > 0 ? _nextGame : 0;

	private uint _nextGame;

	/// <summary>Inside the "farm cards only from ... until" hours (always, when they're both 0).</summary>
	public bool InFarmWindowNow => InFarmWindow();

	/// <summary>Farming happens inside human mode's sittings, and waits between them.</summary>
	private bool FarmsInHumanDay => Bot.HumanOwned && FarmWhen.InSittings(Bot.EffectiveFarmWhen);

	/// <summary>
	/// Whether the schedule wants the account back: in the day's sittings, when a sitting ends; only at night, when
	/// it wakes up. Checked often while farming, so a break or bedtime takes the game off within seconds.
	/// </summary>
	private bool ScheduleWantsItBack() {
		if (!Bot.HumanOwned || (BotManager.ModuleOf<HumanMode>(Bot) is not { } human)) {
			return false;
		}

		return Bot.EffectiveFarmWhen switch {
			FarmWhen.Day or FarmWhen.Mixed => !human.FarmSittingOpen,
			FarmWhen.Night => !human.InBed || human.AwakeHoursNow,   // asleep ends at wake time, whatever the phase says
			_ => false
		};
	}

	private Said WaitingForSitting(HumanMode human) => human.InBed
		? new Said("{0} card(s) - farmed in the day's sittings, from morning", Bot.CardsRemaining)
		: Bot.DropsFirstActive
			? new Said("going for {0}: {1} of {2} card drop(s), in its sittings", GameNames.Of(Bot.DropsFirstApp), Bot.DropsFirstGot, Bot.DropsFirstWant)
		: Bot.EffectiveFarmWhen == FarmWhen.Mixed
			? new Said("{0} card(s) - farmed in some of its sittings, between its usual games", Bot.CardsRemaining)
			: new Said("{0} card(s) - farmed in human mode's next sitting", Bot.CardsRemaining);

	/// <summary>The most cards this farming run has had left - the whole of the run, for a progress bar.</summary>
	public int RunCards => Math.Max(_runCards, Bot.CardsRemaining);

	private int _runCards;

	public IReadOnlyList<FarmTarget> Queue {
		get {
			lock (_queue) {
				return _queue.ToArray();
			}
		}
	}

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Seconds(20, 60), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			// Roll the day up front rather than at the first game with cards on it. An account with nothing to
			// farm still has a shape for today, and saying so is how you can tell the sittings were rolled at
			// all - otherwise the whole feature is invisible until cards happen to appear.
			if (Bot.Cfg.FarmCards && Bot.Cfg.FarmInSittings) {
				RollFarmDayIfNeeded();
			}

			// The loop stays alive when farming is off, so switching it back on takes effect straight away
			// instead of needing a restart.
			if (!Bot.EffectiveFarmCards) {
				_status = new Said("off");
				Release();

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			int waitMinutes;

			try {
				waitMinutes = await CycleAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				// Never silent. A farmer that dies quietly looks exactly like a farmer with nothing to do.
				Log.Warn(new Said("card farming hiccup: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
				waitMinutes = 15;
			}

			Release();

			// Slept in chunks rather than in one go, so switching farming off is noticed within the minute
			// instead of whenever the next scan happened to be due. With nothing left to farm that wait runs to
			// hours, and for all of it the status line went on saying "nothing left to farm" to somebody who had
			// just turned the whole thing off.
			for (int slept = 0; slept < waitMinutes; slept++) {
				if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				// Farming switched off, or a game just added to the account (it may have cards) - look now, not in hours.
				if (!Bot.EffectiveFarmCards || (Bot.LicenseGeneration != _licensesSeen)) {
					break;
				}
			}
		}
	}

	/// <summary>The licence list the last badge read saw - a newer one means a game may have been added.</summary>
	private int _licensesSeen = -1;

	/// <summary>
	/// Farming time on each game since its card count last moved, carried across sittings, pauses and restarts of the
	/// loop - so "gave up nothing in N hours" means N hours of farming, not N hours of one uninterrupted run, which a
	/// day of short sittings never reached.
	/// </summary>
	private readonly Dictionary<uint, (int Cards, TimeSpan Farmed)> _farmedSinceDrop = [];

	private TimeSpan FarmedSinceDrop(FarmTarget game) {
		lock (_farmedSinceDrop) {
			return _farmedSinceDrop.TryGetValue(game.AppId, out (int Cards, TimeSpan Farmed) was) && (was.Cards == game.CardsRemaining)
				? was.Farmed
				: TimeSpan.Zero;
		}
	}

	private void NoteFarmed(FarmTarget game, TimeSpan farmed) {
		lock (_farmedSinceDrop) {
			_farmedSinceDrop[game.AppId] = (game.CardsRemaining, farmed);
		}
	}

	/// <summary>Whether this module may put a farming game on right now - asked again after any long wait.</summary>
	private bool MayFarmNow() => Bot.EffectiveFarmCards && Bot.CanPlay && !Bot.Grinding && !ScheduleWantsItBack() && InFarmWindow();

	/// <summary>
	/// Handing the account back: the claim goes, and so does the farm game if it's still the only thing on and human
	/// mode isn't the one playing it. Letting go of the claim alone left the game running over a break.
	/// </summary>
	private void HandBack(uint appId) {
		Release();

		IReadOnlyList<uint> playing = Bot.PlayingApps;
		uint humanGame = BotManager.ModuleOf<HumanMode>(Bot)?.PlayingNow ?? 0;

		if ((playing.Count == 1) && (playing[0] == appId) && (humanGame != appId)) {
			Bot.StopPlaying();
		}

		BotManager.ModuleOf<Idler>(Bot)?.Assert();   // a non-human account's own games go straight back on
	}

	/// <summary>Set once the cards have been swept, so a later re-scan that finds nothing doesn't re-send.</summary>
	private bool _sweptThisRun;

	/// <summary>A card dropped since the app started - there's something to send.</summary>
	private bool _farmedThisRun;

	private async Task SweepAsync() {
		try {
			// Long enough for the last drop to actually land in the inventory - Steam is not instant about it.
			await Task.Delay(Rng.Minutes(2, 6)).ConfigureAwait(false);

			// It goes to one of your own accounts - nobody sees it - so it isn't held for the account's day.
			HumanGate gate = HumanGate.Quiet(Bot);

			while (!gate.Open) {
				await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
			}

			Log.Info(await Looting.SendToMasterAsync(Bot).ConfigureAwait(false), Bot.Name);
		} catch (Exception e) {
			Log.Warn(new Said("couldn't send the cards on: {0}", e.Message), Bot.Name);
		}
	}

	private void Release() {
		if (Bot.IsFarming) {
			Bot.IsFarming = false;
			Bot.GamesRemaining = 0;

			// Only undo an override this module put there. The guard has to match Claim's exactly: in human mode
			// the farmer never sets one, but human mode itself does - so clearing here on the way out would drop
			// the account's invisible-overnight state and put it back online for the whole friends list.
			if (Bot.Cfg.FarmOffline && !Bot.Cfg.LegitMode) {
				Bot.ClearPersonaOverride();
			}
		}
	}

	/// <summary>
	/// Claim the account for farming, going offline first when that was asked for.
	///
	/// Farming is the part that looks least like a person - forty games in a row, minutes apart. Appearing offline
	/// while it happens costs nothing: Steam counts the hours and drops the cards either way, the only difference
	/// is whether your friends list watches it happen.
	/// </summary>
	private void Claim() {
		Bot.IsFarming = true;

		if (Bot.Cfg.FarmOffline && !Bot.Cfg.LegitMode) {
			Bot.SetPersonaOverride(Bot.PersonaDark);
		}
	}

	/// <summary>One discovery + farming pass. Returns how many minutes to wait before looking again.</summary>
	/// <summary>
	/// Cards are left, but the farmer is deliberately waiting - for bedtime, its clock window or its next sitting.
	/// Human mode reads this: stepping aside for a farmer that isn't going to farm left the account sitting online
	/// doing nothing for hours, which is exactly the look human mode exists to avoid.
	/// </summary>
	public bool HoldingBack { get; private set; }

	private async Task<int> CycleAsync(CancellationToken ct) {
		if (Bot.Grinding) {
			// A drop run is a grind the farmer watches: it counts the drops and ends it once they're in.
			if ((Bot.GrindDropsLeft > 0) && (DateTime.UtcNow >= Bot.GrindStartsAt)) {
				await DropRunAsync(ct).ConfigureAwait(false);

				return 1;
			}

			_status = new Said("standing by - grinding {0}", GameNames.Of(Bot.GrindGame));

			return Bot.GrindDropsLeft > 0 ? 1 : 5;
		}

		if (!Bot.CanPlay) {
			_status = Bot.Paused ? new Said("paused") : Bot.PlayingBlocked ? new Said("standing down (you're using it)") : new Said("waiting for the session");

			return 2;
		}

		// Settle in first, like a person: on a human-mode account don't even scan badges until the post-login
		// warm-up is done. Poll every minute (no scan, so no rate-limit hit) so farming starts right when the
		// warm-up ends instead of up to a full rescan interval later.
		HumanMode? warmup = BotManager.ModuleOf<HumanMode>(Bot);

		// Not while it's asleep: the warm-up is about how the account looks when it comes online, and at night it's
		// invisible - waiting then only left it banking hours on the night list while cards were waiting.
		if (Bot.HumanOwned && (warmup != null) && !warmup.WarmedUp && !warmup.InBed) {
			int mins = warmup.WarmUpMinutesLeft;
			_status = mins > 0 ? new Said("settling in first (~{0}m), then farming", mins) : new Said("settling in first, then farming");

			return 1;
		}

		// A recent read of the badge pages is reused rather than every page read again: after each game, between
		// sittings, all night while cards wait for the day. The queue's counts are kept current by the farming itself.
		// Read again when it's an hour old (three while waiting), when a game has been added to the account, or when
		// the queue has run dry - the last to confirm it really is finished.
		int licenses = Bot.LicenseGeneration;
		TimeSpan maxAge = HoldingBack ? TimeSpan.FromHours(3) : TimeSpan.FromHours(1);
		bool reuse = (licenses == _licensesSeen) && (Bot.CardsCheckedAt is { } looked) && (DateTime.UtcNow - looked < maxAge)
			&& Queue.Any(static g => g.CardsRemaining > 0);

		List<FarmTarget>? found;

		if (reuse) {
			found = [.. Queue.Where(static g => g.CardsRemaining > 0)];
		} else {
			_status = new Said("checking badges");
			found = await DiscoverAsync(ct).ConfigureAwait(false);

			if (found == null) {
				_status = new Said("badge pages unavailable");

				return 10;
			}

			Bot.CardsCheckedAt = DateTime.UtcNow;
			_licensesSeen = licenses;
		}

		lock (_queue) {
			_queue.Clear();
			_queue.AddRange(found);
		}

		Bot.GamesRemaining = found.Count;
		Bot.CardsRemaining = found.Sum(static g => g.CardsRemaining);
		_runCards = found.Count == 0 ? 0 : Math.Max(_runCards, Bot.CardsRemaining);

		if (found.Count == 0) {
			_nextGame = 0;
		}

		if (found.Count == 0) {
			// Everything is farmed, so this is the moment the cards are worth moving. Doing it here rather than on
			// a timer means one sweep at the end of a farming run instead of an offer every hour whether or not
			// anything changed.
			// Only after a run that actually farmed something - not once after every start with nothing to farm.
			if (Bot.Cfg.SendOnFarmingFinished && !_sweptThisRun && _farmedThisRun) {
				_sweptThisRun = true;
				_ = SweepAsync();
			}

			if (Bot.Cfg.StopWhenFarmingDone) {
				// Human mode is still playing on after the last card: log out once that sitting is over, not a
				// minute into it.
				if (FarmsInHumanDay && (BotManager.ModuleOf<HumanMode>(Bot) is { PlayingNow: > 0, NextChange: { } ends })) {
					_status = new Said("finished - logging out after this sitting");

					return Math.Max(1, (int) Math.Ceiling((ends - DateTime.UtcNow).TotalMinutes));
				}

				_status = new Said("finished - logging out");
				Log.Good("nothing left to farm - logging this account out as configured", Bot.Name);
				_ = Bot.StopAsync();

				return 60;
			}

			_status = new Said("nothing left to farm");

			// Said once, not on every rescan, and never claiming to be "idling instead" while human mode has a
			// game open - which is what it used to announce every few minutes in the middle of a visible session.
			if (!_saidNothingLeft) {
				_saidNothingLeft = true;

				// The LIFETIME total, not this session's.
				//
				// "idling 0m so far" thirty seconds after a logon was technically true and worth nothing. What
				// anybody actually wants to know here is how much this account has done in total.
				//
				// "run by nocat.farm", not "played in total".
				//
				// "in total" was the fix for "· 4m played" reading as a contradiction under human mode's
				// "1h34m/7h12m today" - but it overshot: it sounds like the account's lifetime Steam hours,
				// which for an account with thousands of them makes a figure of 3h49m look like a counter that
				// has just been wiped. It is neither of those spans. It is how long THIS program has had a game
				// running for this account since it started counting, so it says so.
				int lifetime = Lifetime.For(Bot.Name);
				// A Said, not a formatted string. It is passed as a VALUE into the sentence below, and a value that
				// is already finished text stays in whatever language it was built in - which is how the two lines
				// ended up reading "keine Karten mehr zu farmen · 1h23m played in total".
				Said been = lifetime > 0 ? new Said(" · {0} run by nocat.farm", Fmt.Hm(lifetime)) : default;
				Said idle = !string.IsNullOrWhiteSpace(Bot.CustomName)
					? new Said("{0}", Bot.CustomName + (Bot.Cfg.IdleGames.Count > 0 ? $" (+{Bot.Cfg.IdleGames.Count})" : ""))
					: Bot.Cfg.IdleGames.Count > 0 ? new Said("{0} game(s)", Bot.Cfg.IdleGames.Count) : new Said("your games");

				Log.Info(Bot.HumanOwned
					? new Said("no cards left - human mode carries on{0}", been)
					: new Said("no cards left to farm - now idling {0}{1}", idle, been), Bot.Name);
			}

			// Hand the session straight back to the idler so the custom game name goes back up NOW.
			//
			// Without this, when farming ends the account keeps showing whatever real game was farmed last (or the
			// raw first idle game after a reconnect) until the idler's own 4-7 minute timer next fires - a window
			// where a boosting account visibly reads "Rust" instead of its custom name. The idler's Assert is
			// idempotent and no-ops for human-mode accounts, so this is safe to call on every idle rescan.
			BotManager.ModuleOf<Idler>(Bot)?.Assert();

			// Nothing to farm changes only when a game is added, and that cuts this wait short (see RunAsync) - so the
			// slow look is only a backstop.
			return Rng.Next(180, 361);
		}

		// New games to farm means a new run, so the next time it empties out it sweeps again - and it is
		// allowed to say "nothing left" once more when it does.
		_sweptThisRun = false;
		_saidNothingLeft = false;

		// One game is already spelled out by the "farming X - N to go" line below; only summarise a batch.
		if (found.Count > 1) {
			Log.Good(new Said("{0} games with {1} cards left to farm - about {2} of farming", found.Count, Bot.CardsRemaining, AllDoneIn), Bot.Name);
		}

		float threshold = Bot.Cfg.HoursUntilCardDrops;
		List<FarmTarget> underThreshold = [.. Order(found.Where(g => g.HoursPlayed < threshold))];   // ordered once: a random order must not change its mind
		List<FarmTarget> ready = Order(found.Where(g => g.HoursPlayed >= threshold)).ToList();

		// Prefer games that are not set aside - but if EVERY one is, carry on with them anyway.
		//
		// Removing them outright would empty the list, and an empty list is how this module decides an account
		// has finished farming: it sweeps the inventory, drops IsFarming, and with StopWhenFarmingDone even logs
		// the account out. One quiet game must never be able to trigger all of that.
		List<FarmTarget> live = ready.Where(g => !IsParked(g.AppId)).ToList();

		if (live.Count > 0) {
			if (live.Count < ready.Count) {
				_status = new Said("farming {0} game(s), {1} set aside", live.Count, ready.Count - live.Count);
			}

			ready = live;
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(Bot);

		// Farming in human mode's sittings: the sitting already has a game on - the one this said was next. Farm that
		// one while it still has cards, rather than let a fresh (possibly random) order swap it a minute in.
		if (FarmsInHumanDay && (human?.PlayingNow is > 0 and var sat) && (ready.FindIndex(g => g.AppId == sat) is > 0 and var at)) {
			FarmTarget keep = ready[at];
			ready.RemoveAt(at);
			ready.Insert(0, keep);
		}

		_nextGame = ready.Count > 0 ? ready[0].AppId : (underThreshold.FirstOrDefault()?.AppId ?? 0);

		// One card game per human-mode sitting. When the sitting's game has just run out, the next one waits for the
		// next sitting (human mode plays on for a few minutes, then takes a break) instead of starting a minute later.
		if (FarmsInHumanDay && (human?.PlayingNow is > 0 and var onNow) && (ready.Count > 0) && !found.Any(g => g.AppId == onNow)) {
			HoldingBack = true;
			_status = WaitingForSitting(human!);

			return 1;
		}

		if (ready.Count == 0 && (threshold <= 0 || underThreshold.Count == 0)) {
			return Rng.Next(RescanMinutesLow, RescanMinutesHigh);
		}

		// On a human-mode account the schedule decides WHEN cards farm (FarmCardsWhen). In the day (the default)
		// the card game is what human mode plays in its sittings, and the farmer works only while one is open -
		// breaks, meals and bedtime included - so nights stay for the overnight games. Only at night, it waits for
		// bed. Any time, it farms the moment there are cards. Waiting costs no badge reads now (see the top), so it
		// checks back every few minutes and starts close to the moment it may.
		if (Bot.HumanOwned && (human != null)) {
			if ((Bot.EffectiveFarmWhen == FarmWhen.Night) && (!human.InBed || human.AwakeHoursNow)) {
				HoldingBack = true;
				_status = new Said("{0} card(s) - farming tonight, once it's asleep", Bot.CardsRemaining);

				return 5;
			}

			if (FarmWhen.InSittings(Bot.EffectiveFarmWhen) && !human.FarmSittingOpen) {
				HoldingBack = true;
				_status = WaitingForSitting(human);

				return 1;   // a sitting can open any minute
			}
		}

		if (!InFarmWindow()) {
			HoldingBack = true;
			_status = new Said("{0} card(s) - waiting for the {1}:00-{2}:00 farming window", Bot.CardsRemaining, (Bot.Cfg.FarmFromHour).ToString("00"), (Bot.Cfg.FarmUntilHour).ToString("00"));

			return 5;
		}

		// Human mode's own sittings already shape a day-farming account; its separate farming sittings don't apply.
		if (Bot.Cfg.FarmInSittings && !FarmsInHumanDay && !InSittingNow(out DateTime next)) {
			HoldingBack = true;
			_status = next > DateTime.Now
				? new Said("{0} card(s) - next sitting around {1}", Bot.CardsRemaining, next.ToString("HH:mm"))
				: new Said("{0} card(s) - done farming for today", Bot.CardsRemaining);

			return Rng.Next(5, 20);   // short, so a sitting starts near its time rather than up to an hour late
		}

		// Only hold a farming slot while actually farming, never while sleeping between rounds. Waiting for one is
		// holding back too - it can take hours with several accounts farming - so human mode keeps its day meanwhile.
		// The flag is cleared only here, once every gate has passed: clearing it at the top of each cycle dropped it
		// for any cycle that ended early (a badge page that didn't load), and human mode went idle again.
		SemaphoreSlim? slots = _slots;

		if (slots != null) {
			_status = new Said("waiting for a farming slot");
			HoldingBack = true;
			await slots.WaitAsync(ct).ConfigureAwait(false);

			// That wait can be hours. Whatever the account is doing by now - a break, bedtime, you, a grind - wins over
			// a slot that finally came free.
			if (!MayFarmNow()) {
				slots.Release();

				return 1;
			}
		}

		HoldingBack = false;

		try {
			// Ready games first: they actually produce cards. Bumping hours produces nothing until it finishes.
			if (ready.Count > 0) {
				await FarmSoloAsync(ready[0], ct).ConfigureAwait(false);
			} else {
				// Building playtime runs up to 32 games at once - fine at night, invisible, but in a visible day's
				// sitting that's the loudest tell there is. One game at a time there.
				// ...and on a human-mode account that's awake and showing online, whatever the farming setting says.
				bool visible = FarmsInHumanDay || (Bot.HumanOwned && !(human?.InBed ?? false));
				await BumpHoursAsync(visible ? [.. underThreshold.Take(1)] : underThreshold, threshold, ct).ConfigureAwait(false);
			}
		} finally {
			slots?.Release();
		}

		return 1;
	}

	/// <summary>Whether a game is currently set aside for producing nothing.</summary>
	private bool IsParked(uint appId) {
		lock (_stalled) {
			return _stalled.TryGetValue(appId, out Stall? stall) && (DateTime.UtcNow < stall.ParkedUntil);
		}
	}

	/// <summary>
	/// Note that we gave up on a game, and set it aside for a while.
	///
	/// The card count is the check that keeps this honest. If it moved since the last time we gave up then the
	/// game IS dropping, just slowly, and the earlier strikes were about something transient - so they are
	/// forgotten rather than accumulated into a long park on a perfectly good game.
	/// </summary>
	private void NoteStall(FarmTarget game, TimeSpan limit) {
		int strikes;
		int hours;

		lock (_stalled) {
			if (!_stalled.TryGetValue(game.AppId, out Stall? stall)) {
				stall = new Stall();
				_stalled[game.AppId] = stall;
			}

			if ((stall.Strikes > 0) && (game.CardsRemaining != stall.CardsWhenParked)) {
				stall.Strikes = 0;
			}

			stall.Strikes++;
			stall.CardsWhenParked = game.CardsRemaining;

			// Backs off as the evidence piles up: a first strike might be a slow afternoon, a fourth is a game
			// that is not going to drop anything today. Capped so it always comes back and is retried.
			hours = Math.Clamp((int) Math.Max(1, limit.TotalHours) * stall.Strikes, 1, 24);
			stall.ParkedUntil = DateTime.UtcNow.AddHours(hours);
			strikes = stall.Strikes;
		}

		Log.Warn(new Said("{0} gave up nothing in {1}h with {2} card(s) still listed - setting it aside for {3}h and moving on (strike {4})", game.GameName, (limit.TotalHours).ToString("0"), game.CardsRemaining, hours, strikes), Bot.Name);
	}

	/// <summary>A game that dropped a card, or finished, is not stuck - forget everything about it.</summary>
	private void ClearStall(uint appId) {
		lock (_stalled) {
			_stalled.Remove(appId);
		}
	}

	/// <summary>
	/// Queue order. Priority games always come first, whatever the sort says - that is what "priority" means -
	/// and the chosen order breaks ties inside each group.
	/// </summary>
	private IEnumerable<FarmTarget> Order(IEnumerable<FarmTarget> games) {
		IEnumerable<FarmTarget> sorted = Bot.Cfg.FarmingOrder switch {
			1 => games.OrderBy(static g => g.HoursPlayed),
			2 => games.OrderBy(static g => g.CardsRemaining),
			3 => games.OrderByDescending(static g => g.CardsRemaining),
			4 => games.OrderBy(static _ => Rng.Next(0, int.MaxValue)),
			5 => games.OrderBy(static g => g.GameName, StringComparer.OrdinalIgnoreCase),
			_ => games.OrderByDescending(static g => g.HoursPlayed)
		};

		// A drop run's game ('drops' on a human-mode account) leads everything, priority games included.
		if (Bot.DropsFirstActive) {
			sorted = sorted.OrderByDescending(g => Bot.Cfg.PriorityGames.Contains(g.AppId));

			return sorted.OrderByDescending(g => g.AppId == Bot.DropsFirstApp);
		}

		if (Bot.Cfg.PriorityGames.Count == 0) {
			return sorted;
		}

		return sorted.OrderByDescending(g => Bot.Cfg.PriorityGames.Contains(g.AppId));
	}

	// ── farming a single game ───────────────────────────────────────────────
	/// <summary>
	/// After the last card of a run, keep the game on a jittered while longer for a human-mode account rather
	/// than quitting the instant the drop lands. Holds the farming claim throughout so the human scheduler stays
	/// stood off; it takes the session back (and steps away for a break) once this releases. Length is the
	/// PostFarmWindDown min/max setting; 0/0 switches it off.
	/// </summary>
	/// <summary>The game this account last saw give its last card - so human mode can play on after exactly that.</summary>
	public uint FinishedGame { get; private set; }

	private async Task WindDownAsync(FarmTarget game, CancellationToken ct) {
		// Farming in human mode's sittings: the sitting ends with human mode's own break, so there's nothing to wind down.
		if (FarmsInHumanDay) {
			return;
		}

		int lo = Math.Max(0, Bot.Cfg.PostFarmWindDownMinMinutes);
		int hi = Math.Max(lo, Bot.Cfg.PostFarmWindDownMaxMinutes);

		if (hi <= 0) {
			return;
		}

		int mins = Rng.Next(lo, hi + 1);

		if (mins <= 0) {
			return;
		}

		Log.Info(new Said("all card drops done - winding down on {0} for ~{1} before the usual games", game.GameName, Fmt.Hm(mins)), Bot.Name);
		DateTime until = DateTime.UtcNow.AddMinutes(mins);

		while (!ct.IsCancellationRequested && (DateTime.UtcNow < until)) {
			if (!Bot.CanPlay) {
				Bot.StopPlaying();

				return;
			}

			if (Bot.Grinding) {
				Bot.StopPlaying();   // a grind outranks the wind-down - hand the session over

				return;
			}

			// Farmed at night and it's morning now: the day takes over rather than the game running on into it.
			if (ScheduleWantsItBack()) {
				Bot.StopPlaying();

				return;
			}

			Claim();
			Bot.SetPlaying([game.AppId], Bot.Cfg.PlayWhileFarming ? null : "");
			int left = (int) Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
			_status = new Said("winding down on {0} (~{1}m)", game.GameName, Math.Max(1, left));

			if (!await Sleep(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)) {
				return;
			}
		}

		Bot.StopPlaying();
	}

	private async Task FarmSoloAsync(FarmTarget game, CancellationToken ct) {
		if (!MayFarmNow()) {
			return;   // checked before anything goes on, not only at the top of the loop
		}

		DateTime started = DateTime.UtcNow;
		TimeSpan before = FarmedSinceDrop(game);
		TimeSpan limit = TimeSpan.FromHours(Math.Max(1, Bot.Cfg.MaxFarmingHoursPerGame));
		TimeSpan check = TimeSpan.FromMinutes(Math.Max(2, Bot.Cfg.FarmingDelayMinutes));

		try {
			await FarmSoloLoopAsync(game, limit, check, () => before + (DateTime.UtcNow - started), () => {
				before = TimeSpan.Zero;
				started = DateTime.UtcNow;
			}, ct).ConfigureAwait(false);
		} finally {
			if (game.CardsRemaining > 0) {
				NoteFarmed(game, before + (DateTime.UtcNow - started));
			}
		}
	}

	/// <param name="farmed">Farming time on this game since its card count last moved.</param>
	/// <param name="dropped">Called when a card drops, which starts that count again.</param>
	private async Task FarmSoloLoopAsync(FarmTarget game, TimeSpan limit, TimeSpan check, Func<TimeSpan> farmed, Action dropped, CancellationToken ct) {
		DateTime started = DateTime.UtcNow;

		Claim();
		Bot.SetPlaying([game.AppId], Bot.Cfg.PlayWhileFarming ? null : "");
		_status = new Said("farming {0} ({1} left · ~{2} in all)", game.GameName, game.CardsRemaining, AllDoneIn);
		Log.Info(new Said("farming {0} - {1} card(s) to go · all done in ~{2} of farming", game.GameName, game.CardsRemaining, AllDoneIn), Bot.Name);

		// Farming time since the last drop - the measure of this game's pace. Restarts with every call, so a pause,
		// a break or the owner playing is never counted as waiting for a card. The first drop of a call isn't
		// measured: Steam's timer may have been partway through from earlier play, which would read as a fast game.
		DateTime sinceDrop = started;
		bool measuring = false;

		while (!ct.IsCancellationRequested) {
			if (!Bot.CanPlay) {
				_status = new Said("paused (account in use)");

				return;
			}

			if (Bot.Grinding) {
				// A grind outranks farming - drop the claim and let it take the session, rather than fighting the
				// idler over what's playing and polling a badge page for a game that isn't even running.
				_status = new Said("standing by - grinding");
				Release();

				return;
			}

			// The schedule wants the account back - a sitting ended, bedtime, morning - or farming was switched off.
			// Checked before the claim and the game go back on below, or they'd go straight back over human mode's break.
			if (ScheduleWantsItBack() || !Bot.EffectiveFarmCards) {
				_status = Bot.EffectiveFarmCards ? new Said("between sittings - {0} card(s) left", game.CardsRemaining) : new Said("off");
				HandBack(game.AppId);

				return;
			}

			// Re-assert the claim every round. Pause() and a PlayingBlocked flap both clear IsFarming while this
			// loop is still running, and the idler would then treat the account as free and take the session.
			Claim();

			// And the game itself. Human mode now plays while the farmer waits, so a claim can land in the same
			// instant human mode sends its own game - and nothing here would ever put the farm game back.
			if (!Bot.PlayingApps.Contains(game.AppId)) {
				Bot.SetPlaying([game.AppId], Bot.Cfg.PlayWhileFarming ? null : "");
			}

			// Armed BEFORE the re-check so a drop landing mid-check still wakes us. Waited in short slices, so a break,
			// bedtime, the owner or a grind is noticed within seconds rather than at the next badge-page read.
			bool pushed = false;
			// Not the same number of minutes every time: the badge page is read on an uneven beat.
			DateTime recheckAt = DateTime.UtcNow + (check * (0.8 + (Rng.Next(0, 46) / 100.0)));

			while (!pushed && !ct.IsCancellationRequested && (DateTime.UtcNow < recheckAt) && Bot.CanPlay && !Bot.Grinding && !ScheduleWantsItBack()) {
				TimeSpan left = recheckAt - DateTime.UtcNow;
				pushed = await Bot.WaitForItemDropAsync(left < TimeSpan.FromSeconds(20) ? left : TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
			}

			if (!pushed && (DateTime.UtcNow < recheckAt)) {
				continue;   // cut short by one of those - the top of the loop deals with it, no page read needed
			}

			if (pushed) {
				// Steam batches the announcement slightly ahead of the badge page updating.
				await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
			}

			if (farmed() > limit) {
				NoteStall(game, limit);

				return;
			}

			FarmTarget? fresh = await GetGameCardsAsync(game.AppId, ct).ConfigureAwait(false);

			if (fresh == null) {
				// Transient web problem - keep playing and try again. The give-up check above still runs, so a
				// permanently unreadable page can't pin this game (and the farming slot) forever.
				continue;
			}

			int before = game.CardsRemaining;
			game.CardsRemaining = fresh.CardsRemaining;
			game.HoursPlayed = fresh.HoursPlayed;
			Bot.CardsRemaining = Queue.Sum(static g => g.CardsRemaining);
			if (before > game.CardsRemaining) {
				if (measuring) {
					Pace.Dropped(game.AppId, DateTime.UtcNow - sinceDrop, before - game.CardsRemaining);
				}

				sinceDrop = DateTime.UtcNow;
				measuring = true;
			}

			_status = new Said("farming {0} ({1} left · ~{2} in all)", game.GameName, game.CardsRemaining, AllDoneIn);

			if (game.CardsRemaining == 0) {
				ClearStall(game.AppId);

				// Count and announce the final card(s) as well. The old early-return here fired BEFORE the drop
				// tally below, so the last card of a game was never logged and never counted in the daily total.
				if (before > game.CardsRemaining) {
					for (int i = 0; i < before - game.CardsRemaining; i++) {
						Stats.Record(Stats.KindCard, Bot.Name);
						_farmedThisRun = true;
					}

					Bot.CountDropsFirst(game.AppId, before - game.CardsRemaining);
					Log.Reward(new Said("last card dropped in {0} - that game's done", game.GameName), Bot.Name);
					Plugins.PluginHost.RaiseCardDropped(Bot, game.AppId, 0);
				} else {
					Log.Reward(new Said("{0} is done - no cards left", game.GameName), Bot.Name);
				}

				lock (_queue) {
					_queue.RemoveAll(g => g.AppId == game.AppId);
				}

				FinishedGame = game.AppId;
				Bot.CardsRemaining = Queue.Sum(static g => g.CardsRemaining);
				_nextGame = Queue.FirstOrDefault(static g => g.CardsRemaining > 0)?.AppId ?? 0;   // or the next sitting opens on a finished game

				// That was the last game with cards: on a human-mode account, wind down on it a little longer
				// like a person who just finished, instead of snapping off the second the drop lands. Human mode
				// takes the session back and steps away for a break once this releases the claim.
				if (Bot.HumanOwned && (Bot.CardsRemaining == 0)) {
					await WindDownAsync(game, ct).ConfigureAwait(false);
				}

				return;
			}

			// Count what the badge page actually says, not the push. Steam announces ANY new inventory item -
			// a trade, a market buy, a gift - so trusting the announcement inflated the daily card tally with
			// things that were never card drops.
			if (game.CardsRemaining < before) {
				dropped();

				// It is producing, so whatever made it look stuck before is over.
				ClearStall(game.AppId);

				for (int i = 0; i < before - game.CardsRemaining; i++) {
					Stats.Record(Stats.KindCard, Bot.Name);
					_farmedThisRun = true;
				}

				Bot.CountDropsFirst(game.AppId, before - game.CardsRemaining);
				Log.Reward(new Said("card dropped in {0} - {1} to go · all done in ~{2} of farming", game.GameName, game.CardsRemaining, AllDoneIn), Bot.Name);
				Plugins.PluginHost.RaiseCardDropped(Bot, game.AppId, game.CardsRemaining);
			}
		}
	}

	// ── drop runs ───────────────────────────────────────────────────────────
	/// <summary>The badge page for one game - what the <c>drops</c> command checks before it starts a run.</summary>
	public Task<FarmTarget?> CardsForAsync(uint appId, CancellationToken ct = default) => GetGameCardsAsync(appId, ct);

	/// <summary>About how long <paramref name="drops"/> cards take in one game, at its pace or the account's.</summary>
	public int MinutesForDrops(uint appId, int drops) => (int) Math.Ceiling(Math.Max(1, drops) * Pace.MinutesFor(appId));

	/// <summary>
	/// A drop run - the <c>drops</c> command: the grind plays the game, whatever the schedule says; this counts the
	/// drops off the badge page and ends the grind once they're in, or once the game has none left. The grind's
	/// time is only the cap for a game that stops dropping.
	/// </summary>
	private async Task DropRunAsync(CancellationToken ct) {
		uint app = Bot.GrindGame;
		FarmTarget? start = await GetGameCardsAsync(app, ct).ConfigureAwait(false);

		if (start == null) {
			_status = new Said("badge pages unavailable");

			return;   // looked at again in a minute
		}

		// The catalogue's name, not the page's: on some games' card pages that header reads "Badges".
		string name = GameNames.Of(app);

		if (start.CardsRemaining == 0) {
			Log.Good(new Said("drop run over - {0} has no card drops left, back to the usual day", name), Bot.Name);
			Bot.StopGrind();

			return;
		}

		int cards = start.CardsRemaining;
		TimeSpan check = TimeSpan.FromMinutes(Math.Max(2, Bot.Cfg.FarmingDelayMinutes));

		// Pace from full intervals only, as in ordinary farming: the first drop may come off a timer that was partway.
		DateTime sinceDrop = DateTime.UtcNow;
		bool measuring = false;

		while (!ct.IsCancellationRequested && Bot.Grinding && (Bot.GrindGame == app) && (Bot.GrindDropsLeft > 0)) {
			_status = new Said("drop run on {0} - {1} to go", name, Bot.GrindDropsLeft);

			bool pushed = false;
			DateTime recheckAt = DateTime.UtcNow + (check * (0.8 + (Rng.Next(0, 46) / 100.0)));

			while (!pushed && !ct.IsCancellationRequested && (DateTime.UtcNow < recheckAt) && Bot.Grinding && (Bot.GrindGame == app)) {
				// You playing, or a pause: the game isn't running, so this stretch doesn't count towards a card's pace.
				if (!Bot.CanPlay) {
					sinceDrop = DateTime.UtcNow;
					measuring = false;
				}

				TimeSpan left = recheckAt - DateTime.UtcNow;
				pushed = await Bot.WaitForItemDropAsync(left < TimeSpan.FromSeconds(20) ? left : TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
			}

			if (ct.IsCancellationRequested || !Bot.Grinding || (Bot.GrindGame != app)) {
				break;
			}

			if (pushed) {
				await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);   // the badge page lags the push a little
			}

			FarmTarget? now = await GetGameCardsAsync(app, ct).ConfigureAwait(false);

			if ((now == null) || (now.CardsRemaining >= cards)) {
				continue;
			}

			int dropped = cards - now.CardsRemaining;
			cards = now.CardsRemaining;

			if (measuring) {
				Pace.Dropped(app, DateTime.UtcNow - sinceDrop, dropped);
			}

			sinceDrop = DateTime.UtcNow;
			measuring = true;

			for (int i = 0; i < dropped; i++) {
				Stats.Record(Stats.KindCard, Bot.Name);
				_farmedThisRun = true;
			}

			lock (_queue) {
				if (_queue.FirstOrDefault(g => g.AppId == app) is { } queued) {
					queued.CardsRemaining = cards;
				}

				if (cards == 0) {
					_queue.RemoveAll(g => g.AppId == app);
				}
			}

			if (cards == 0) {
				_nextGame = Queue.FirstOrDefault(static g => g.CardsRemaining > 0)?.AppId ?? 0;
			}

			Bot.CardsRemaining = Queue.Sum(static g => g.CardsRemaining);
			Bot.CountGrindDrops(dropped);
			Plugins.PluginHost.RaiseCardDropped(Bot, app, cards);
			Log.Reward(new Said("card dropped in {0} - {1} more on this drop run", name, Bot.GrindDropsLeft), Bot.Name);

			if ((cards == 0) || (Bot.GrindDropsLeft == 0)) {
				Log.Good(cards == 0
					? new Said("drop run done - {0} has no card drops left, back to the usual day", name)
					: new Said("drop run done - back to the usual day"), Bot.Name);
				Bot.StopGrind();

				return;
			}
		}

		// Ran out of time before the drops came - said, and cleared, rather than left looking like a run. Not on the
		// way out of the app: a run still going then is picked back up after the restart.
		if (!ct.IsCancellationRequested && !Bot.Grinding && (Bot.GrindGame == app) && (Bot.GrindDropsLeft > 0)) {
			Log.Info(new Said("drop run on {0} ran out of time with {1} still to come - back to the usual day", name, Bot.GrindDropsLeft), Bot.Name);
			Bot.StopGrind();
		}
	}

	// ── bumping playtime on games that are too new to drop ──────────────────
	private async Task BumpHoursAsync(List<FarmTarget> games, float threshold, CancellationToken ct) {
		// Priority games lead here too - they're the ones you actually want over the line first.
		List<FarmTarget> batch = Order(games)
			.Take(SteamIds.GamesAtOnce - (string.IsNullOrWhiteSpace(Bot.CustomName) ? 0 : 1))
			.ToList();

		float best = batch.Max(static g => g.HoursPlayed);
		double needHours = Math.Max(0.1, threshold - best);

		Claim();
		Bot.SetPlaying(batch.Select(static g => g.AppId).ToArray(), Bot.Cfg.PlayWhileFarming ? null : "");
		_status = new Said("building playtime on {0} game(s), ~{1}h to go", batch.Count, (needHours).ToString("0.0"));
		Log.Info(new Said("none of these have enough playtime to drop yet - running {0} at once for ~{1}h", batch.Count, (needHours).ToString("0.0")), Bot.Name);

		DateTime until = DateTime.UtcNow.AddHours(needHours);

		while (!ct.IsCancellationRequested && DateTime.UtcNow < until) {
			if (!Bot.CanPlay) {
				_status = new Said("paused (account in use)");

				return;
			}

			if (Bot.Grinding) {
				_status = new Said("standing by - grinding");
				Release();

				return;
			}

			if (ScheduleWantsItBack() || !Bot.EffectiveFarmCards) {
				HandBack(batch.Count == 1 ? batch[0].AppId : 0);

				return;
			}

			Bot.IsFarming = true;   // same reason as FarmSoloAsync

			// Same again: put the batch back if something else got its games onto Steam in the hand-over.
			if (batch.Any(g => !Bot.PlayingApps.Contains(g.AppId))) {
				Bot.SetPlaying(batch.Select(static g => g.AppId).ToArray(), Bot.Cfg.PlayWhileFarming ? null : "");
			}

			TimeSpan left = until - DateTime.UtcNow;
			TimeSpan most = Bot.HumanOwned && (Bot.Cfg.FarmCardsWhen != FarmWhen.Any) ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(10);
			TimeSpan slice = left < most ? left : most;

			// A drop here means Steam disagreed with our threshold - stop bumping and go farm properly.
			if (await Bot.WaitForItemDropAsync(slice, ct).ConfigureAwait(false)) {
				Log.Reward("a card dropped while building playtime - switching to farming", Bot.Name);

				return;
			}

			_status = new Said("building playtime on {0} game(s), ~{1}h to go", batch.Count, ((until - DateTime.UtcNow).TotalHours).ToString("0.0"));
		}
	}

	// ── discovery ───────────────────────────────────────────────────────────
	/// <summary>Read the badge pages and return everything still worth playing. Null means "couldn't check".</summary>
	private async Task<List<FarmTarget>?> DiscoverAsync(CancellationToken ct) {
		string? first = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/badges?l=english&p=1"), ct).ConfigureAwait(false);

		// A page without the profile header isn't a badge page at all - Steam serves its error pages with a 200 too,
		// and read as "no badges" that finished every game, swept the cards and could log the account out.
		if ((first == null) || !IsProfilePage(first)) {
			return null;
		}

		Dictionary<uint, FarmTarget> byApp = [];
		List<FarmTarget> zero = [];

		CollectPage(first, byApp, zero);

		int pages = Math.Min(MaxBadgePages, ParseMaxPages(first));

		for (int p = 2; p <= pages; p++) {
			await Task.Delay(Rng.Seconds(2, 6), ct).ConfigureAwait(false);   // paged through, not fired all at once

			string? page = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/badges?l=english&p={p}"), ct).ConfigureAwait(false);

			if ((page == null) || !IsProfilePage(page)) {
				break;   // partial results still beat none - the next cycle picks up the rest
			}

			CollectPage(page, byApp, zero);
		}

		// A row that says "no drops left" isn't always the last word: a free-to-play game can earn drops again, a game
		// can get a new card set. So a game that has been PLAYED since it last said zero is looked at on its own card
		// page - at most once a week, and only for games that are actually being played, so finished games that just
		// sit in the library never cost a request.
		foreach (FarmTarget played in PlayedSinceZero(zero)) {
			FarmTarget? real = await GetGameCardsAsync(played.AppId, ct).ConfigureAwait(false);

			if (real is { CardsRemaining: > 0 }) {
				byApp[played.AppId] = real;
			}
		}

		// Only games. Steam's sale and event badges sit on the badge page with "drops remaining" too, but they're not
		// games and playing them earns nothing - the store's own description of each app says which is which. Known
		// answers cost nothing; a few unknown apps are asked about per read, and anything still unknown is farmed
		// (the give-up rule parks it if it really never drops).
		int asked = 0;

		foreach (uint app in byApp.Keys.ToArray()) {
			GameCatalog.Facts? facts = GameCatalog.Known(app);

			if ((facts == null) && (asked < StoreChecksPerRead)) {
				asked++;
				facts = await GameCatalog.LookUpAsync(app, ct).ConfigureAwait(false);
			}

			if (facts is { IsGame: false }) {
				Log.Debug(new Said("{0} has card drops listed but isn't a game (a sale or event badge) - not farmed", GameNames.Of(app)), Bot.Name);
				byApp.Remove(app);
			}
		}

		if (Bot.Cfg.FarmPriorityOnly) {
			if (Bot.Cfg.PriorityGames.Count == 0) {
				// Honour the flag literally rather than quietly farming everything, which is the opposite of
				// what "only farm those" says. Say so, once, so it isn't a mystery.
				Log.Warn("\"Only farm those\" is on but the priority list is empty - nothing will be farmed", Bot.Name);
				byApp.Clear();
			} else {
				foreach (uint appId in byApp.Keys.Where(a => !Bot.Cfg.PriorityGames.Contains(a)).ToArray()) {
					byApp.Remove(appId);
				}
			}
		}

		if (Bot.Cfg.SkipRefundableGames) {
			await DropRefundableAsync(byApp).ConfigureAwait(false);
		}

		return byApp.Values.OrderByDescending(static g => g.HoursPlayed).ToList();
	}

	/// <summary>
	/// Steam refunds a game bought within 14 days that has under 2 hours on it. Farming would push it over that
	/// two-hour line, so a game still inside the refund window is left alone.
	///
	/// If we can't work out when something was bought, it is farmed - failing open here costs a refund the user
	/// probably wasn't going to claim; failing closed would silently farm nothing at all.
	/// </summary>
	private async Task DropRefundableAsync(Dictionary<uint, FarmTarget> byApp) {
		const float RefundableUnderHours = 2.0f;
		int RefundableForDays = Math.Max(1, Bot.Cfg.RefundHoldDays);

		if (byApp.Values.All(static g => g.HoursPlayed >= RefundableUnderHours)) {
			return;   // nothing is refundable on playtime alone - no need to ask Steam anything
		}

		IReadOnlyDictionary<uint, AppOwnership> owned = await Bot.GetAppOwnershipAsync().ConfigureAwait(false);

		if (owned.Count == 0) {
			return;
		}

		foreach ((uint appId, FarmTarget game) in byApp.ToArray()) {
			if (game.HoursPlayed >= RefundableUnderHours) {
				continue;
			}

			// Free games carry today's date too, and no amount of playing one costs anybody a refund.
			if (owned.TryGetValue(appId, out AppOwnership own) && own.Refundable(Bot.Cfg.ProtectGiftedGames) && ((DateTime.UtcNow - own.Since).TotalDays < RefundableForDays)) {
				Log.Debug(new Said("leaving {0} alone - still refundable until {1}", game.GameName, (own.Since.AddDays(RefundableForDays)).ToString("d")), Bot.Name);
				byApp.Remove(appId);
			}
		}
	}

	private void CollectPage(string html, Dictionary<uint, FarmTarget> byApp, List<FarmTarget> zero) {
		foreach (FarmTarget game in ParseBadgePage(html)) {
			if (byApp.ContainsKey(game.AppId) || !ShouldIdle(game.AppId)) {
				continue;
			}

			if (Bot.Cfg.SkipUnplayedGames && (game.HoursPlayed <= 0)) {
				continue;   // never launched - leave it that way
			}

			if (game.CardsRemaining > 0) {
				byApp[game.AppId] = game;
			} else if (game.HoursPlayed > 0) {
				zero.Add(game);
			}
		}
	}

	private string ZeroRowsPath => Path.Combine(ConfigStore.ConfigDir, "state", $"cardcheck-{Bot.Name}.json");

	/// <summary>
	/// The "no drops" rows worth a second look now: played since they were last seen (or last checked), and not checked
	/// in the past week. The first sighting of a game only notes its hours, so a fresh start doesn't check the whole
	/// library at once.
	/// </summary>
	private List<FarmTarget> PlayedSinceZero(List<FarmTarget> zero) {
		LoadZeroRows();

		List<FarmTarget> due = [];
		DateTime now = DateTime.UtcNow;
		bool changed = false;

		foreach (FarmTarget game in zero) {
			if (!_zeroRows.TryGetValue(game.AppId, out (float Hours, DateTime Checked) seen)) {
				_zeroRows[game.AppId] = (game.HoursPlayed, now);
				changed = true;

				continue;
			}

			if ((game.HoursPlayed > seen.Hours + 0.05f) && (now - seen.Checked > TimeSpan.FromDays(7))) {
				due.Add(game);
				_zeroRows[game.AppId] = (game.HoursPlayed, now);
				changed = true;
			}
		}

		if (changed) {
			SaveZeroRows();
		}

		return due;
	}

	private void LoadZeroRows() {
		if (_zeroRowsLoaded) {
			return;
		}

		_zeroRowsLoaded = true;

		try {
			if (File.Exists(ZeroRowsPath)
				&& System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(ZeroRowsPath)) is { } saved) {
				foreach ((string app, double[] v) in saved) {
					if (uint.TryParse(app, out uint id) && (v.Length == 2)) {
						_zeroRows[id] = ((float) v[0], new DateTime((long) v[1], DateTimeKind.Utc));
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the card-page check list: {0}", e.Message), Bot.Name);
		}
	}

	private void SaveZeroRows() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(ZeroRowsPath)!);
			AtomicFile.Write(ZeroRowsPath, System.Text.Json.JsonSerializer.Serialize(
				_zeroRows.ToDictionary(static kv => kv.Key.ToString(CultureInfo.InvariantCulture), static kv => new[] { kv.Value.Hours, (double) kv.Value.Checked.Ticks })));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the card-page check list: {0}", e.Message), Bot.Name);
		}
	}

	// ── farming that looks like playing ───────────────────────────────────────
	//
	// A card farmer runs flat out until the cards are gone. That is efficient and it is also the single most
	// obvious thing on the account: hours accruing in a straight line, through the night, every night, on games
	// nobody would grind. FarmFromHour/FarmUntilHour helped, but a window that opens at exactly 09:00 and shuts
	// at exactly 23:00 every single day is its own pattern - a person is not a timer.
	//
	// Legit farming rolls a DAY instead: a few sittings of believable length, with gaps between them, starting
	// and finishing at different times, longer at the weekend. The farmer still does everything it did; it just
	// only does it inside those sittings.
	//
	// The roll is seeded from the account name and the date rather than persisted. That is deliberate: a
	// restart re-rolls the same day rather than handing out a fresh set of sittings, so bouncing the app cannot
	// be used - accidentally or otherwise - to farm more hours than the day allows.
	private int _farmDayStamp = -1;
	private List<(DateTime From, DateTime To)> _farmWindows = [];

	private void RollFarmDayIfNeeded() {
		DateTime now = DateTime.Now;

		if (_farmDayStamp == now.DayOfYear) {
			return;
		}

		_farmDayStamp = now.DayOfYear;
		_farmWindows = [];

		// Same account, same date, same day - however many times it is rolled.
		//
		// NOT HashCode.Combine: .NET randomises string hashing per process, so that produced a different
		// schedule on every restart - which is precisely the thing this seeding exists to prevent. Restarting
		// twice would have handed out two fresh sets of sittings. A plain rolling hash of the name is stable
		// across processes and machines, which is all that is wanted here.
		int seed = now.Year * 1000 + now.DayOfYear;

		foreach (char c in Bot.Name) {
			seed = (seed * 31) + c;
		}

		Random rng = new(seed);

		bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
		int hours = Math.Clamp(Bot.Cfg.FarmHoursPerDay, 1, 20);
		int target = (int) (hours * 60 * (weekend ? rng.Next(115, 146) : rng.Next(85, 116)) / 100.0);

		// Start somewhere in the morning-to-midday spread, then lay sittings end to end with gaps until the
		// day's minutes are spent or it gets too late to plausibly still be at it.
		DateTime at = now.Date.AddHours(rng.Next(8, 13)).AddMinutes(rng.Next(0, 60));
		DateTime latest = now.Date.AddHours(rng.Next(23, 27));   // some days run past midnight
		int spent = 0;

		while ((spent < target) && (at < latest)) {
			int length = Math.Min(rng.Next(45, 165), target - spent);

			if (length < 20) {
				break;   // not worth starting a sitting this short
			}

			DateTime end = at.AddMinutes(length);

			_farmWindows.Add((at, end));
			spent += length;
			at = end.AddMinutes(rng.Next(20, 110));   // up, away from the desk, back later
		}

		Log.Debug(new Said("today's farming: {0} sitting(s), {1} in total", _farmWindows.Count, Fmt.Hm(spent))
			+ (_farmWindows.Count > 0 ? $", {_farmWindows[0].From:HH:mm}-{_farmWindows[^1].To:HH:mm}" : ""),
			Bot.Name);
	}

	/// <summary>Inside one of today's rolled sittings. The window that is open right now, if any.</summary>
	private bool InSittingNow(out DateTime until) {
		RollFarmDayIfNeeded();

		DateTime now = DateTime.Now;

		foreach ((DateTime from, DateTime to) in _farmWindows) {
			if ((now >= from) && (now < to)) {
				until = to;

				return true;
			}
		}

		until = _farmWindows.FirstOrDefault(w => w.From > now).From;

		return false;
	}

	/// <summary>Whether now is inside the card-farming clock window, if the account set one (0-0 = any time).</summary>
	private bool InFarmWindow() {
		int from = Bot.Cfg.FarmFromHour;
		int until = Bot.Cfg.FarmUntilHour;

		if (from == until) {
			return true;
		}

		// Each edge moves by up to 45 minutes, rolled fresh every day - opening on the hour, every day, is a clock.
		DateTime now = DateTime.Now;

		if (_windowDay != now.DayOfYear) {
			_windowDay = now.DayOfYear;
			_windowOpenShift = Rng.Next(0, 46);
			_windowCloseShift = Rng.Next(-45, 1);
		}

		int minute = (now.Hour * 60) + now.Minute;
		int open = (from * 60) + _windowOpenShift;
		int close = (until * 60) + _windowCloseShift;

		return from < until ? (minute >= open) && (minute < close) : (minute >= open) || (minute < close);
	}

	private int _windowDay = -1;
	private int _windowOpenShift;
	private int _windowCloseShift;

	private bool ShouldIdle(uint appId) =>
		appId > 0
		&& !Bot.Cfg.BlacklistedGames.Contains(appId)
		&& !Live.Global.GlobalBlacklistedGames.Contains(appId);

	/// <summary>Authoritative per-game read. The badge list is a summary; this page is the real state.</summary>
	private async Task<FarmTarget?> GetGameCardsAsync(uint appId, CancellationToken ct) {
		string? html = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/gamecards/{appId}?l=english"), ct).ConfigureAwait(false);

		// Same as the badge list: an error page is "couldn't read", never "0 cards left".
		if ((html == null) || !IsProfilePage(html)) {
			return null;
		}

		return new FarmTarget {
			AppId = appId,
			GameName = TagText(html, "profile_small_header_location") ?? appId.ToString(CultureInfo.InvariantCulture),
			HoursPlayed = ReadDecimal(TagText(html, "badge_title_stats_playtime")),
			CardsRemaining = ReadInt(TagText(html, "progress_info_bold"))
		};
	}

	/// <summary>Steam's own profile pages - badges, a game's cards - carry the profile header; its error pages don't.</summary>
	private static bool IsProfilePage(string html) => html.Contains("profile_small_header", StringComparison.Ordinal);

	// ── parsing ─────────────────────────────────────────────────────────────
	// Steam's badge markup is stable and narrow enough to read with string scanning; a full HTML parser would be a
	// large dependency for four fields.
	internal static List<FarmTarget> ParseBadgePage(string html) {
		List<FarmTarget> rows = [];
		const string RowMarker = "badge_row_inner";
		int i = 0;

		while (true) {
			int start = html.IndexOf(RowMarker, i, StringComparison.Ordinal);

			if (start < 0) {
				return rows;
			}

			int next = html.IndexOf(RowMarker, start + RowMarker.Length, StringComparison.Ordinal);
			string row = next < 0 ? html[start..] : html[start..next];
			i = start + RowMarker.Length;

			// The appID lives in the id of the drop-info dialog: card_drop_info_gamebadge_<appid>_<level>_<bool>
			int idAt = row.IndexOf("card_drop_info_gamebadge_", StringComparison.Ordinal);

			if (idAt < 0) {
				continue;   // a badge with no game behind it (Steam awards, sale badges, ...)
			}

			int digits = idAt + "card_drop_info_gamebadge_".Length;
			int end = digits;

			while ((end < row.Length) && char.IsAsciiDigit(row[end])) {
				end++;
			}

			if ((end == digits) || !uint.TryParse(row.AsSpan(digits, end - digits), out uint appId) || (appId == 0)) {
				continue;
			}

			string? badgeName = ReadBadgeName(row);

			// The badge page already knows what every game is called. Remembering it here is free, and it is what
			// lets the rest of the app say "Counter-Strike 2" without ever asking Steam a second time.
			GameNames.Learn(appId, badgeName);

			rows.Add(new FarmTarget {
				AppId = appId,
				GameName = badgeName ?? appId.ToString(CultureInfo.InvariantCulture),
				HoursPlayed = ReadDecimal(TagText(row, "badge_title_stats_playtime")),
				CardsRemaining = ReadInt(TagText(row, "progress_info_bold"))
			});
		}
	}

	/// <summary>Steam writes the game name as <c>&lt;div class="badge_title"&gt;Name&amp;nbsp;</c>.</summary>
	private static string? ReadBadgeName(string row) {
		string? raw = Html.Between(row, "badge_title\">", "&nbsp;") ?? Html.Between(row, "badge_title\">", "<");

		if (raw == null) {
			return null;
		}

		string name = Html.Text(raw);

		return name.Length == 0 ? null : name;
	}

	/// <summary>Text of the first element whose opening tag contains <paramref name="marker"/>.</summary>
	private static string? TagText(string html, string marker) {
		int at = html.IndexOf(marker, StringComparison.Ordinal);

		if (at < 0) {
			return null;
		}

		int gt = html.IndexOf('>', at);

		if (gt < 0) {
			return null;
		}

		int lt = html.IndexOf('<', gt);

		return lt > gt ? Html.Text(html[(gt + 1)..lt]) : null;
	}

	/// <summary>First run of digits, e.g. "6 card drops remaining" -> 6. No digits legitimately means zero.</summary>
	private static int ReadInt(string? text) {
		if (string.IsNullOrEmpty(text)) {
			return 0;
		}

		int i = 0;

		while ((i < text.Length) && !char.IsAsciiDigit(text[i])) {
			i++;
		}

		int start = i;

		while ((i < text.Length) && char.IsAsciiDigit(text[i])) {
			i++;
		}

		return (i > start) && int.TryParse(text.AsSpan(start, i - start), out int v) ? v : 0;
	}

	/// <summary>First decimal, e.g. "1.4 hrs on record" -> 1.4. Thousands separators are dropped.</summary>
	private static float ReadDecimal(string? text) {
		if (string.IsNullOrEmpty(text)) {
			return 0f;
		}

		int i = 0;

		while ((i < text.Length) && !char.IsAsciiDigit(text[i])) {
			i++;
		}

		int start = i;

		while ((i < text.Length) && (char.IsAsciiDigit(text[i]) || (text[i] == '.') || (text[i] == ','))) {
			i++;
		}

		if (i <= start) {
			return 0f;
		}

		string number = text[start..i].Replace(",", "", StringComparison.Ordinal);

		return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
	}

	internal static int ParseMaxPages(string html) {
		int max = 1;
		int i = 0;

		while (true) {
			int at = html.IndexOf("pagelink", i, StringComparison.Ordinal);

			if (at < 0) {
				return max;
			}

			i = at + "pagelink".Length;
			int gt = html.IndexOf('>', i);

			if (gt < 0) {
				return max;
			}

			int lt = html.IndexOf('<', gt);

			if (lt < 0) {
				return max;
			}

			int page = ReadInt(Html.Text(html[(gt + 1)..lt]));

			if (page > max) {
				max = page;
			}
		}
	}
}
