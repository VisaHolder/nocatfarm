using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

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

	/// <summary>Steam sale and event "games". They have badges, they never drop cards.</summary>
	private static readonly HashSet<uint> SalesBlacklist = [
		267420, 303700, 335590, 368020, 425280, 480730, 566020, 639900, 762800, 876740, 991980, 1195670,
		1343890, 1465680, 1658760, 1797760, 2021850, 2243720, 2459330, 2640280, 2861690, 2861720, 3558920,
		3558940, 4761370
	];

	/// <summary>These three lie on the badge list. If they claim zero, the per-game page is asked instead.</summary>
	private static readonly HashSet<uint> UntrustedAppIds = [440, 570, 730];

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
	private string _status = "idle";

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
			if (Bot.Cfg.FarmCards && Bot.Cfg.LegitFarming) {
				RollFarmDayIfNeeded();
			}

			// The loop stays alive when farming is off, so switching it back on takes effect straight away
			// instead of needing a restart.
			if (!Bot.Cfg.FarmCards) {
				_status = "off";
				Release();

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			int waitMinutes;

			try {
				waitMinutes = await CycleAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				// Never silent. A farmer that dies quietly looks exactly like a farmer with nothing to do.
				Log.Warn($"card farming hiccup: {e.GetType().Name}: {e.Message}", Bot.Name);
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

				if (!Bot.Cfg.FarmCards) {
					break;
				}
			}
		}
	}

	/// <summary>Set once the cards have been swept, so a later re-scan that finds nothing doesn't re-send.</summary>
	private bool _sweptThisRun;

	private async Task SweepAsync() {
		try {
			// Long enough for the last drop to actually land in the inventory - Steam is not instant about it.
			await Task.Delay(Rng.Minutes(2, 6)).ConfigureAwait(false);
			Log.Info(await Looting.SendToMasterAsync(Bot).ConfigureAwait(false), Bot.Name);
		} catch (Exception e) {
			Log.Warn($"couldn't send the cards on: {e.Message}", Bot.Name);
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
	private async Task<int> CycleAsync(CancellationToken ct) {
		if (Bot.Grinding) {
			_status = $"standing by - grinding {GameNames.Of(Bot.GrindGame)}";

			return 5;
		}

		if (!Bot.CanPlay) {
			_status = Bot.Paused ? "paused" : Bot.PlayingBlocked ? "standing down (you're using it)" : "waiting for the session";

			return 2;
		}

		// Settle in first, like a person: on a human-mode account don't even scan badges until the post-login
		// warm-up is done. Poll every minute (no scan, so no rate-limit hit) so farming starts right when the
		// warm-up ends instead of up to a full rescan interval later.
		HumanMode? warmup = BotManager.ModuleOf<HumanMode>(Bot);

		if (Bot.HumanOwned && (warmup != null) && !warmup.WarmedUp) {
			int mins = warmup.WarmUpMinutesLeft;
			_status = mins > 0 ? $"settling in first (~{mins}m), then farming" : "settling in first, then farming";

			return 1;
		}

		_status = "checking badges";
		List<FarmTarget>? found = await DiscoverAsync(ct).ConfigureAwait(false);

		if (found == null) {
			_status = "badge pages unavailable";

			return 10;
		}

		lock (_queue) {
			_queue.Clear();
			_queue.AddRange(found);
		}

		Bot.GamesRemaining = found.Count;
		Bot.CardsRemaining = found.Sum(static g => g.CardsRemaining);

		if (found.Count == 0) {
			// Everything is farmed, so this is the moment the cards are worth moving. Doing it here rather than on
			// a timer means one sweep at the end of a farming run instead of an offer every hour whether or not
			// anything changed.
			if (Bot.Cfg.SendOnFarmingFinished && !_sweptThisRun) {
				_sweptThisRun = true;
				_ = SweepAsync();
			}

			if (Bot.Cfg.StopWhenFarmingDone) {
				_status = "finished - logging out";
				Log.Good("nothing left to farm - logging this account out as configured", Bot.Name);
				_ = Bot.StopAsync();

				return 60;
			}

			_status = "nothing left to farm";

			// Said once, not on every rescan, and never claiming to be "idling instead" while human mode has a
			// game open - which is what it used to announce every few minutes in the middle of a visible session.
			if (!_saidNothingLeft) {
				_saidNothingLeft = true;

				// The LIFETIME total, not this session's.
				//
				// "idling 0m so far" thirty seconds after a logon was technically true and worth nothing. What
				// anybody actually wants to know here is how much this account has done in total.
				int lifetime = Lifetime.For(Bot.Name);
				string been = lifetime > 0 ? $" · {Fmt.Hm(lifetime)} played" : "";
				string idle = !string.IsNullOrWhiteSpace(Bot.CustomName)
					? Bot.CustomName + (Bot.Cfg.IdleGames.Count > 0 ? $" (+{Bot.Cfg.IdleGames.Count})" : "")
					: Bot.Cfg.IdleGames.Count > 0 ? $"{Bot.Cfg.IdleGames.Count} game(s)" : "your games";

				Log.Info(Bot.HumanOwned
					? $"no cards left - human mode carries on{been}"
					: $"no cards left to farm - now idling {idle}{been}", Bot.Name);
			}

			// Hand the session straight back to the idler so the custom game name goes back up NOW.
			//
			// Without this, when farming ends the account keeps showing whatever real game was farmed last (or the
			// raw first idle game after a reconnect) until the idler's own 4-7 minute timer next fires - a window
			// where a boosting account visibly reads "Rust" instead of its custom name. The idler's Assert is
			// idempotent and no-ops for human-mode accounts, so this is safe to call on every idle rescan.
			BotManager.ModuleOf<Idler>(Bot)?.Assert();

			return Rng.Next(RescanMinutesLow, RescanMinutesHigh);
		}

		// New games to farm means a new run, so the next time it empties out it sweeps again - and it is
		// allowed to say "nothing left" once more when it does.
		_sweptThisRun = false;
		_saidNothingLeft = false;

		// One game is already spelled out by the "farming X - N to go" line below; only summarise a batch.
		if (found.Count > 1) {
			Log.Good($"{found.Count} games with {Bot.CardsRemaining} cards left to farm", Bot.Name);
		}

		float threshold = Bot.Cfg.HoursUntilCardDrops;
		List<FarmTarget> underThreshold = found.Where(g => g.HoursPlayed < threshold).ToList();
		List<FarmTarget> ready = Order(found.Where(g => g.HoursPlayed >= threshold)).ToList();

		// Prefer games that are not set aside - but if EVERY one is, carry on with them anyway.
		//
		// Removing them outright would empty the list, and an empty list is how this module decides an account
		// has finished farming: it sweeps the inventory, drops IsFarming, and with StopWhenFarmingDone even logs
		// the account out. One quiet game must never be able to trigger all of that.
		List<FarmTarget> live = ready.Where(g => !IsParked(g.AppId)).ToList();

		if (live.Count > 0) {
			if (live.Count < ready.Count) {
				_status = $"farming {live.Count} game(s), {ready.Count - live.Count} set aside";
			}

			ready = live;
		}

		if (ready.Count == 0 && (threshold <= 0 || underThreshold.Count == 0)) {
			return Rng.Next(RescanMinutesLow, RescanMinutesHigh);
		}

		// Cards are the point: farm the moment there are any. In human mode the schedule decides only WHEN the
		// account is up; whenever it is, the farmer takes priority over the weighted games and hands back the
		// instant the drops are gone. It farms while asleep too. "Card farming on but not farming" is not a thing.

		// Two opt-in limits on WHEN to farm - both off by default.
		HumanMode? human = BotManager.ModuleOf<HumanMode>(Bot);

		if (Bot.HumanOwned && Bot.Cfg.FarmOnlyWhileAsleep && human?.InBed != true) {
			_status = $"{Bot.CardsRemaining} card(s) - farming tonight, once it's asleep";

			return Rng.Next(RescanMinutesLow, RescanMinutesHigh);
		}

		if (!InFarmWindow()) {
			_status = $"{Bot.CardsRemaining} card(s) - waiting for the {Bot.Cfg.FarmFromHour:00}:00-{Bot.Cfg.FarmUntilHour:00}:00 farming window";

			return Rng.Next(RescanMinutesLow, RescanMinutesHigh);
		}

		if (Bot.Cfg.LegitFarming && !InLegitFarmWindow(out DateTime next)) {
			_status = next > DateTime.Now
				? $"{Bot.CardsRemaining} card(s) - next sitting around {next:HH:mm}"
				: $"{Bot.CardsRemaining} card(s) - done farming for today";

			return Rng.Next(5, 20);   // short, so a sitting starts near its time rather than up to an hour late
		}

		// Only hold a farming slot while actually farming, never while sleeping between rounds.
		SemaphoreSlim? slots = _slots;

		if (slots != null) {
			_status = "waiting for a farming slot";
			await slots.WaitAsync(ct).ConfigureAwait(false);
		}

		try {
			// Ready games first: they actually produce cards. Bumping hours produces nothing until it finishes.
			if (ready.Count > 0) {
				await FarmSoloAsync(ready[0], ct).ConfigureAwait(false);
			} else {
				await BumpHoursAsync(underThreshold, threshold, ct).ConfigureAwait(false);
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

		Log.Warn($"{game.GameName} gave up nothing in {limit.TotalHours:0}h with {game.CardsRemaining} card(s) still listed - setting it aside for {hours}h and moving on (strike {strikes})", Bot.Name);
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
	private async Task WindDownAsync(FarmTarget game, CancellationToken ct) {
		int lo = Math.Max(0, Bot.Cfg.PostFarmWindDownMinMinutes);
		int hi = Math.Max(lo, Bot.Cfg.PostFarmWindDownMaxMinutes);

		if (hi <= 0) {
			return;
		}

		int mins = Rng.Next(lo, hi + 1);

		if (mins <= 0) {
			return;
		}

		Log.Info($"all card drops done - winding down on {game.GameName} for ~{Fmt.Hm(mins)} before the usual games", Bot.Name);
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

			Claim();
			Bot.SetPlaying([game.AppId], Bot.Cfg.PlayWhileFarming ? null : "");
			int left = (int) Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
			_status = $"winding down on {game.GameName} (~{Math.Max(1, left)}m)";

			if (!await Sleep(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)) {
				return;
			}
		}

		Bot.StopPlaying();
	}

	private async Task FarmSoloAsync(FarmTarget game, CancellationToken ct) {
		DateTime started = DateTime.UtcNow;
		TimeSpan limit = TimeSpan.FromHours(Math.Max(1, Bot.Cfg.MaxFarmingHoursPerGame));
		TimeSpan check = TimeSpan.FromMinutes(Math.Max(2, Bot.Cfg.FarmingDelayMinutes));

		Claim();
		Bot.SetPlaying([game.AppId], Bot.Cfg.PlayWhileFarming ? null : "");
		_status = $"farming {game.GameName} ({game.CardsRemaining} left)";
		Log.Info($"farming {game.GameName} - {game.CardsRemaining} card(s) to go", Bot.Name);

		while (!ct.IsCancellationRequested) {
			if (!Bot.CanPlay) {
				_status = "paused (account in use)";

				return;
			}

			if (Bot.Grinding) {
				// A grind outranks farming - drop the claim and let it take the session, rather than fighting the
				// idler over what's playing and polling a badge page for a game that isn't even running.
				_status = "standing by - grinding";
				Release();

				return;
			}

			// Re-assert the claim every round. Pause() and a PlayingBlocked flap both clear IsFarming while this
			// loop is still running, and the idler would then treat the account as free and take the session.
			Claim();

			// Armed BEFORE the re-check so a drop landing mid-check still wakes us.
			bool pushed = await Bot.WaitForItemDropAsync(check, ct).ConfigureAwait(false);

			if (pushed) {
				// Steam batches the announcement slightly ahead of the badge page updating.
				await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
			}

			if (DateTime.UtcNow - started > limit) {
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
			_status = $"farming {game.GameName} ({game.CardsRemaining} left)";

			if (game.CardsRemaining == 0) {
				ClearStall(game.AppId);

				// Count and announce the final card(s) as well. The old early-return here fired BEFORE the drop
				// tally below, so the last card of a game was never logged and never counted in the daily total.
				if (before > game.CardsRemaining) {
					for (int i = 0; i < before - game.CardsRemaining; i++) {
						Stats.Record(Stats.KindCard, Bot.Name);
					}

					Log.Reward($"last card dropped in {game.GameName} - that game's done", Bot.Name);
				} else {
					Log.Reward($"{game.GameName} is done - no cards left", Bot.Name);
				}

				lock (_queue) {
					_queue.RemoveAll(g => g.AppId == game.AppId);
				}

				Bot.CardsRemaining = Queue.Sum(static g => g.CardsRemaining);

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
				// It is producing, so whatever made it look stuck before is over.
				ClearStall(game.AppId);

				for (int i = 0; i < before - game.CardsRemaining; i++) {
					Stats.Record(Stats.KindCard, Bot.Name);
				}

				Log.Reward($"card dropped in {game.GameName} - {game.CardsRemaining} to go", Bot.Name);
			}
		}
	}

	// ── bumping playtime on games that are too new to drop ──────────────────
	private async Task BumpHoursAsync(List<FarmTarget> games, float threshold, CancellationToken ct) {
		// Priority games lead here too - they're the ones you actually want over the line first.
		List<FarmTarget> batch = Order(games)
			.Take(SteamIds.MaxGamesPlayedConcurrently - (string.IsNullOrWhiteSpace(Bot.CustomName) ? 0 : 1))
			.ToList();

		float best = batch.Max(static g => g.HoursPlayed);
		double needHours = Math.Max(0.1, threshold - best);

		Claim();
		Bot.SetPlaying(batch.Select(static g => g.AppId).ToArray(), Bot.Cfg.PlayWhileFarming ? null : "");
		_status = $"building playtime on {batch.Count} game(s), ~{needHours:0.0}h to go";
		Log.Info($"none of these have enough playtime to drop yet - running {batch.Count} at once for ~{needHours:0.0}h", Bot.Name);

		DateTime until = DateTime.UtcNow.AddHours(needHours);

		while (!ct.IsCancellationRequested && DateTime.UtcNow < until) {
			if (!Bot.CanPlay) {
				_status = "paused (account in use)";

				return;
			}

			if (Bot.Grinding) {
				_status = "standing by - grinding";
				Release();

				return;
			}

			Bot.IsFarming = true;   // same reason as FarmSoloAsync
			TimeSpan left = until - DateTime.UtcNow;
			TimeSpan slice = left < TimeSpan.FromMinutes(10) ? left : TimeSpan.FromMinutes(10);

			// A drop here means Steam disagreed with our threshold - stop bumping and go farm properly.
			if (await Bot.WaitForItemDropAsync(slice, ct).ConfigureAwait(false)) {
				Log.Reward("a card dropped while building playtime - switching to farming", Bot.Name);

				return;
			}

			_status = $"building playtime on {batch.Count} game(s), ~{(until - DateTime.UtcNow).TotalHours:0.0}h to go";
		}
	}

	// ── discovery ───────────────────────────────────────────────────────────
	/// <summary>Read the badge pages and return everything still worth playing. Null means "couldn't check".</summary>
	private async Task<List<FarmTarget>?> DiscoverAsync(CancellationToken ct) {
		string? first = await Bot.Web.GetAsync(new Uri(WebSession.Community, "/my/badges?l=english&p=1"), ct).ConfigureAwait(false);

		if (first == null) {
			return null;
		}

		Dictionary<uint, FarmTarget> byApp = [];
		List<FarmTarget> suspect = [];

		CollectPage(first, byApp, suspect);

		int pages = Math.Min(MaxBadgePages, ParseMaxPages(first));

		for (int p = 2; p <= pages; p++) {
			string? page = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/my/badges?l=english&p={p}"), ct).ConfigureAwait(false);

			if (page == null) {
				break;   // partial results still beat none - the next cycle picks up the rest
			}

			CollectPage(page, byApp, suspect);
		}

		// The three known liars claimed zero. Ask the per-game page, which tells the truth.
		foreach (FarmTarget doubtful in suspect) {
			FarmTarget? real = await GetGameCardsAsync(doubtful.AppId, ct).ConfigureAwait(false);

			if (real is { CardsRemaining: > 0 }) {
				byApp[doubtful.AppId] = real;
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
		const float HoursForRefund = 2.0f;
		int DaysForRefund = Math.Max(1, Bot.Cfg.RefundHoldDays);

		if (byApp.Values.All(static g => g.HoursPlayed >= HoursForRefund)) {
			return;   // nothing is refundable on playtime alone - no need to ask Steam anything
		}

		IReadOnlyDictionary<uint, AppOwnership> owned = await Bot.GetAppOwnershipAsync().ConfigureAwait(false);

		if (owned.Count == 0) {
			return;
		}

		foreach ((uint appId, FarmTarget game) in byApp.ToArray()) {
			if (game.HoursPlayed >= HoursForRefund) {
				continue;
			}

			// Free games carry today's date too, and no amount of playing one costs anybody a refund.
			if (owned.TryGetValue(appId, out AppOwnership own) && own.Paid && ((DateTime.UtcNow - own.Since).TotalDays < DaysForRefund)) {
				Log.Debug($"leaving {game.GameName} alone - still refundable until {own.Since.AddDays(DaysForRefund):d}", Bot.Name);
				byApp.Remove(appId);
			}
		}
	}

	private void CollectPage(string html, Dictionary<uint, FarmTarget> byApp, List<FarmTarget> suspect) {
		foreach (FarmTarget game in ParseBadgePage(html)) {
			if (byApp.ContainsKey(game.AppId) || !ShouldIdle(game.AppId)) {
				continue;
			}

			if (Bot.Cfg.SkipUnplayedGames && (game.HoursPlayed <= 0)) {
				continue;   // never launched - leave it that way
			}

			if (game.CardsRemaining > 0) {
				byApp[game.AppId] = game;
			} else if (UntrustedAppIds.Contains(game.AppId)) {
				suspect.Add(game);
			}
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
		int hours = Math.Clamp(Bot.Cfg.LegitFarmHoursPerDay, 1, 20);
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

		Log.Debug(
			$"today's farming: {_farmWindows.Count} sitting(s), {Fmt.Hm(spent)} in total"
			+ (_farmWindows.Count > 0 ? $", {_farmWindows[0].From:HH:mm}-{_farmWindows[^1].To:HH:mm}" : ""),
			Bot.Name);
	}

	/// <summary>Inside one of today's rolled sittings. The window that is open right now, if any.</summary>
	private bool InLegitFarmWindow(out DateTime until) {
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

		int hour = DateTime.Now.Hour;

		return from < until ? (hour >= from) && (hour < until) : (hour >= from) || (hour < until);
	}

	private bool ShouldIdle(uint appId) =>
		appId > 0
		&& !SalesBlacklist.Contains(appId)
		&& !Bot.Cfg.BlacklistedGames.Contains(appId)
		&& !Live.Global.GlobalBlacklistedGames.Contains(appId);

	/// <summary>Authoritative per-game read. The badge list is a summary; this page is the real state.</summary>
	private async Task<FarmTarget?> GetGameCardsAsync(uint appId, CancellationToken ct) {
		string? html = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/my/gamecards/{appId}?l=english"), ct).ConfigureAwait(false);

		if (html == null) {
			return null;
		}

		return new FarmTarget {
			AppId = appId,
			GameName = TagText(html, "profile_small_header_location") ?? appId.ToString(CultureInfo.InvariantCulture),
			HoursPlayed = ReadDecimal(TagText(html, "badge_title_stats_playtime")),
			CardsRemaining = ReadInt(TagText(html, "progress_info_bold"))
		};
	}

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
