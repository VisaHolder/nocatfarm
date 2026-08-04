using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Achievement Boost - an auto-rotating hunter that earns achievements across several games without you starting
/// each grind by hand. OFF by default (<c>AchievementBoost = 0</c>); most accounts won't use it.
///
/// It drives the ordinary grind, so a boost session behaves exactly like a deliberate one: it sits on a game and
/// unlocks easiest-first, at the account's Achievement pace, only what the hours in the game make reachable - and
/// it's persisted, so a restart resumes mid-session. When a session ends it rotates to the next game.
///
/// Targets come from one of two modes: "games you pick" (the <c>AchievementBoostGames</c> list) or "all
/// single-player" (every owned game Steam's store marks Single-player AND with achievements, discovered from the
/// account's own games list and cached). Multiplayer games are left out - grinding a multiplayer game for
/// achievements looks less like a person.
///
/// A HUMAN account stays weighted-FIRST: a boost session is only an occasional grind slotted between long
/// stretches of the normal weighted schedule (<c>BoostRestMinutesHuman</c> apart, capped at
/// <c>MaxBoostGamesInARow</c> before a longer weighted rest), and never while asleep. A NON-human account rotates
/// targets back-to-back. It never fights a manual grind: while one the operator started is running, it stays out.
/// </summary>
public sealed class AchievementBoost(Bot bot) : BotModule(bot) {
	private int _index;                          // round-robin position in the target list
	private int _inARow;                         // consecutive boost sessions (human weighted-first cap)
	private bool _ours;                           // is the grind currently running one WE started?
	private bool _sawGrind;                       // a grind was running at the last tick (ours or the operator's)
	private bool _sweeping;                       // mid store-sweep: the target list is still being worked out

	/// <summary>New store lookups per tick. Each is throttled, so this is about a minute's worth.</summary>
	private const int LookupsPerTick = 40;
	private readonly Random _rng = new();
	private DateTime _lastEnded = DateTime.MinValue;
	private int _restNeeded;                      // this gap's own jittered length, rolled when the gap starts
	private string _status = "";

	private List<uint> _singleplayer = [];       // discovered owned single-player games with achievements (mode 2)
	private DateTime _discoveredAt = DateTime.MinValue;

	public override string Name => "boost";
	public override string Status => On ? _status : "";

	private bool On => (Bot.Cfg.AchievementBoost != 0) && Bot.Cfg.UnlockAchievements;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				if (On) {
					await DiscoverIfNeededAsync(ct).ConfigureAwait(false);
					Tick();
				} else {
					// Switching the boost off has to stop what it is DOING, not just stop it starting anything
					// else. Without this, an account put on a two-hour hunt carried on playing that game for the
					// full two hours after the setting said off - and the status line cheerfully said "off" while
					// it did. A manual grind is somebody else's and is left alone.
					if (_ours && Bot.Grinding) {
						Log.Info($"achievement boost switched off - leaving {GameNames.Of(Bot.GrindGame)}", Bot.Name);
						Bot.StopGrind();
					}

					_ours = false;
					_sawGrind = false;
					_status = "off";
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Warn($"achievement boost hiccup: {e.Message}", Bot.Name);
			}

			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>One line of the plan, for the dashboard: a game and either its playtime or why it's out.</summary>
	public sealed record PlanRow(uint App, string Game, int Minutes, bool Shared, string Why);

	/// <summary>
	/// The plan as data: what it would hunt, in order, and what it ruled out and why.
	///
	/// The same decisions the <c>hunt</c> command prints, minus the formatting - so the dashboard and the console
	/// can never disagree about what the hunter is going to do.
	/// </summary>
	public (string Mode, string Now, List<PlanRow> Next, List<PlanRow> Out) Plan() {
		if (!On) {
			return ("off", "", [], []);
		}

		List<uint> plan = Targets();
		int start = plan.Count > 0 ? _index % plan.Count : 0;

		List<PlanRow> next = [.. plan.Skip(start).Concat(plan.Take(start)).Select(Row)];
		List<PlanRow> left = [];

		foreach (Library.Entry game in Bot.Library.Games.OrderByDescending(static g => g.MinutesPlayed)) {
			if (plan.Contains(game.AppId)) {
				continue;
			}

			string why = WhyNot(game);

			if (why.Length > 0) {
				left.Add(new PlanRow(game.AppId, game.Name, game.MinutesPlayed, game.Shared, why));
			}
		}

		string now = Bot.Grinding && _ours ? GameNames.Of(Bot.GrindGame) : "";

		return (Bot.Cfg.AchievementBoost == 2 ? "all single-player" : "games you pick", now, next, left);
	}

	/// <summary>
	/// Reasons, counted. "only 11 reviews" and "only 7 reviews" are the same reason with different numbers, and a
	/// thousand games left out is a sentence rather than a list.
	/// </summary>
	public static List<(string Why, int Count)> Reasons(IEnumerable<PlanRow> rows) => [.. rows
		.GroupBy(static r => r.Why.StartsWith("only ", StringComparison.Ordinal) ? "too few reviews to be worth playing" : r.Why)
		.Select(static g => (g.Key, g.Count()))
		.OrderByDescending(static g => g.Item2)];

	private PlanRow Row(uint app) {
		Library.Entry? game = Bot.Library.Find(app);

		return new PlanRow(app, game?.Name ?? GameNames.Of(app), game?.MinutesPlayed ?? 0, game?.Shared ?? false, "");
	}

	/// <summary>
	/// The plan, in words: what it would play next and - more usefully - why everything else was ruled out.
	///
	/// Every reason here is a setting or a fact about the game, so anything surprising in the "left out" list is
	/// something the user can go and change.
	/// </summary>
	public Task<string> ExplainAsync(CancellationToken ct) {
		if (!On) {
			return Task.FromResult($"{Bot.Name}: achievement boost is off"
				+ (Bot.Cfg is { AchievementBoost: not 0, UnlockAchievements: false } ? " (it needs \"Unlock achievements\" on too)" : ""));
		}

		List<string> lines = [$"{Bot.Name}: achievement boost - {(Bot.Cfg.AchievementBoost == 2 ? "all single-player" : "games you pick")}, about {Math.Clamp(Bot.Cfg.BoostSessionHours, 1, 24)}h per game"];

		if (Bot.Grinding && _ours) {
			lines.Add($"   now: {GameNames.Of(Bot.GrindGame)}{(Bot.GrindUntil is { } until ? $", {Fmt.Hm((int) (until - DateTime.UtcNow).TotalMinutes)} left" : "")}");
		} else if (Bot.Grinding) {
			lines.Add("   now: standing by - a grind you started is running");
		} else {
			lines.Add($"   now: {(_status.Length > 0 ? _status : "starting up")}");
		}

		List<uint> plan = Targets();

		lines.Add(plan.Count == 0 ? "   nothing to hunt" : $"   next up ({plan.Count} game(s)):");

		foreach (uint app in plan.Skip(_index % Math.Max(1, plan.Count)).Concat(plan.Take(_index % Math.Max(1, plan.Count))).Take(12)) {
			Library.Entry? game = Bot.Library.Find(app);

			lines.Add($"      {GameNames.Of(app),-38} {(game == null ? "" : $"{Fmt.Hm(game.MinutesPlayed)} played")}{(game?.Shared == true ? "  (shared)" : "")}");
		}

		if (plan.Count > 12) {
			lines.Add($"      ...and {plan.Count - 12} more");
		}

		// Why not the rest. Only for the auto-discovering mode - a picked list needs no explanation beyond the
		// safety rules, which are reported the same way.
		Dictionary<string, List<string>> reasons = [];

		foreach (Library.Entry game in Bot.Library.Games.OrderByDescending(static g => g.MinutesPlayed)) {
			if (plan.Contains(game.AppId)) {
				continue;
			}

			string why = WhyNot(game);

			if (why.Length == 0) {
				continue;
			}

			// "only 11 review(s)" and "only 7 review(s)" are the same reason with different numbers.
			string bucket = why.StartsWith("only ", StringComparison.Ordinal) ? "too few reviews to be worth playing" : why;

			if (!reasons.TryGetValue(bucket, out List<string>? games)) {
				reasons[bucket] = games = [];
			}

			games.Add(game.Name);
		}

		// Grouped by reason, not listed one by one: a family library leaves a thousand games out, and "640 have
		// no single-player mode" is the useful sentence - a screenful of names is not.
		if (reasons.Count > 0) {
			lines.Add($"   left out ({reasons.Sum(static r => r.Value.Count)}):");

			foreach ((string why, List<string> games) in reasons.OrderByDescending(static r => r.Value.Count)) {
				string examples = string.Join(", ", games.Take(3));

				lines.Add($"      {games.Count,5}  {why}{(games.Count > 0 ? $"   e.g. {examples}" : "")}");
			}
		}

		return Task.FromResult(string.Join(Environment.NewLine, lines));
	}

	/// <summary>Reads only what the catalogue already knows - a typed command must not trigger a store sweep.</summary>
	private string WhyNot(Library.Entry game) {
		uint main = Bot.HumanOwned ? BotManager.ModuleOf<HumanMode>(Bot)?.MainGameId ?? 0 : 0;

		if (Bot.Cfg.YieldToFamily && Bot.Library.FamilyIsPlaying(game.AppId)) {
			return "someone in the family is playing it";
		}

		if (Bot.Refunds.Holds(game.AppId)) {
			return "inside its refund window";
		}

		if (Bot.Cfg.BlacklistedGames.Contains(game.AppId) || Live.Global.GlobalBlacklistedGames.Contains(game.AppId)) {
			return "blacklisted";
		}

		if (Bot.Cfg.AchievementNeverGames.Contains(game.AppId)) {
			return "on your never list";
		}

		if (game.AppId == main) {
			return "the main game - left alone";
		}

		if ((Bot.Cfg.AchievementGames.Count > 0) && !Bot.Cfg.AchievementGames.Contains(game.AppId)) {
			return "not on your achievement allow list";
		}

		if (Bot.Cfg.AchievementBoost == 1) {
			return "";   // simply not on the picked list, which is not a rejection worth listing
		}

		if (Bot.Cfg.BoostOnlyPlayedGames && (game.MinutesPlayed == 0)) {
			return "never launched";
		}

		GameCatalog.Facts? facts = GameCatalog.Known(game.AppId);

		return facts switch {
			null => "the store hasn't answered yet",
			{ IsGame: false } => "not a game (DLC, demo, soundtrack or tool)",
			{ Single: false } => "no single-player mode",
			{ Achievements: false } => "no achievements",
			_ when facts.Reviews < Math.Max(0, Bot.Cfg.BoostMinReviews) => $"only {facts.Reviews} review(s)",
			_ => ""
		};
	}

	// ── target discovery (mode 2: all single-player) ─────────────────────────
	private async Task DiscoverIfNeededAsync(CancellationToken ct) {
		if (Bot.Cfg.AchievementBoost != 2) {
			return;   // only "all single-player" needs discovery; the picked list is just the setting
		}

		if ((_discoveredAt != DateTime.MinValue) && (DateTime.UtcNow - _discoveredAt < TimeSpan.FromHours(6))) {
			return;   // rebuilt at most every 6h - store facts don't change and libraries rarely do
		}

		if (!Bot.IsOnline) {
			return;
		}

		if (!await Bot.Library.RefreshIfStaleAsync(TimeSpan.FromHours(6), ct).ConfigureAwait(false) || (Bot.Library.Games.Count == 0)) {
			if (_singleplayer.Count == 0) {
				_status = "on - waiting for this account's library";
			}

			return;   // a blip - keep any list we already had and try again next cycle
		}

		// Owned games first, then most-played first.
		//
		// A person with thirty unplayed bundle games and one they love does not hunt the bundle games first, and
		// the pacer can unlock more in a game that already has hours on it. Owned before borrowed because a shared
		// game belongs to somebody else: they can start playing it at any moment, and Steam hands it straight back
		// to them - so it is the less reliable half of the list, not the front of it.
		List<Library.Entry> candidates = Bot.Library.Games
			.Where(g => !Bot.Cfg.BoostOnlyPlayedGames || (g.MinutesPlayed > 0))
			.OrderBy(static g => g.Shared)
			.ThenByDescending(static g => g.MinutesPlayed)
			.ToList();

		List<uint> found = [];
		int unknown = 0;
		int asked = 0;

		foreach (Library.Entry game in candidates) {
			if (ct.IsCancellationRequested) {
				return;
			}

			// A first sweep of a family library is over a thousand store lookups, spaced out - half an hour of
			// work. Doing it all inside one tick would freeze every other decision this module makes for that
			// whole time, so it does a slice per tick and picks up where it left off: the catalogue is on disk,
			// so everything already answered flies past for free.
			if ((asked >= LookupsPerTick) && (GameCatalog.Known(game.AppId) == null)) {
				// Out of lookups for this tick - but hunt with whatever has been confirmed so far rather than
				// sitting idle until the whole library has been asked about. A family library is over a thousand
				// games and the store answers a couple hundred every five minutes, so "wait for the full sweep"
				// meant an account with forty perfectly good targets did nothing at all for the best part of an
				// hour. The list keeps growing on later ticks; _discoveredAt stays unset so the sweep continues.
				_sweeping = true;
				_status = $"on - {found.Count} game(s) so far, still working through the rest";

				if (found.Count > _singleplayer.Count) {
					_singleplayer = found;
				}

				return;
			}

			if (GameCatalog.Known(game.AppId) == null) {
				asked++;
			}

			bool? huntable = await GameCatalog.IsHuntableAsync(game.AppId, Bot.Cfg.BoostMinReviews, ct).ConfigureAwait(false);

			if (huntable == true) {
				found.Add(game.AppId);
			} else if (huntable == null) {
				unknown++;   // store didn't answer for this one; don't finalise a list that's still missing games
			}
		}

		_sweeping = false;

		// Only replace the list once every game has a definite answer (or we found some) - so a half-finished store
		// sweep doesn't briefly shrink the target list. A sweep that resolved NOTHING means the store is down, so
		// back off for a while instead of re-asking for every game in the library once a minute.
		if ((found.Count == 0) && (unknown > 0)) {
			_discoveredAt = DateTime.UtcNow - TimeSpan.FromHours(5.5);   // ~30 minutes before it tries again

			return;
		}

		bool changed = !found.SequenceEqual(_singleplayer);

		_singleplayer = found;
		_discoveredAt = DateTime.UtcNow;

		if (changed) {
			int shared = found.Count(a => Bot.Library.Find(a)?.Shared == true);

			Log.Info($"achievement boost - {found.Count} game(s) worth hunting{(shared > 0 ? $" ({shared} shared with this account)" : "")}", Bot.Name);
		}
	}

	/// <summary>
	/// The games to work through, in order, with every "don't touch this" the account has asked for applied.
	///
	/// Picked lists are honoured as picked - the only things stripped from them are the ones that would be a
	/// mistake at any price: a game inside its refund window, a blacklisted game, and anything the achievement
	/// settings already refuse to unlock in (there is no point sitting on a game for two hours to earn nothing).
	/// </summary>
	private List<uint> Targets() {
		List<uint> raw = Bot.Cfg.AchievementBoost switch {
			1 => Bot.Cfg.AchievementBoostGames,
			2 => _singleplayer,
			_ => []
		};

		// A picked list can name a game this account doesn't have. Steam ignores a games-played for something you
		// don't own, so the session would be two hours of nothing at all - drop those once the library is known.
		if ((Bot.Cfg.AchievementBoost == 1) && Bot.Library.Ready) {
			raw = [.. raw.Where(app => Bot.Library.Find(app) != null)];
		}

		if (raw.Count == 0) {
			return [];
		}

		List<uint> allowed = Bot.Cfg.AchievementGames;

		// The pacer deliberately never unlocks in a human account's main game, so hunting it would be two hours
		// spent earning nothing at all.
		uint main = Bot.HumanOwned ? BotManager.ModuleOf<HumanMode>(Bot)?.MainGameId ?? 0 : 0;

		return raw.Where(app =>
			!(Bot.Cfg.YieldToFamily && Bot.Library.FamilyIsPlaying(app))
			&& !Bot.Refunds.Holds(app)
			&& !Bot.Cfg.BlacklistedGames.Contains(app)
			&& !Live.Global.GlobalBlacklistedGames.Contains(app)
			&& !Bot.Cfg.AchievementNeverGames.Contains(app)
			&& (app != main)
			&& ((allowed.Count == 0) || allowed.Contains(app))).ToList();
	}

	// ── the boost decision ───────────────────────────────────────────────────
	private void Tick() {
		// A grind is running. If it's ours, let it run; if it's a manual grind, stay completely out of the way.
		if (Bot.Grinding) {
			// Unless the family has taken the game back. Steam lends a shared game to one person at a time and the
			// owner wins, so carrying on would leave the account "playing" something it has been thrown out of -
			// earning nothing and looking like it is. Hand it back and move down the list; the twenty-minute grace
			// in the library keeps it out of the rotation until they are actually finished with it.
			if (_ours && Bot.Cfg.YieldToFamily && Bot.Library.FamilyIsPlaying(Bot.GrindGame)) {
				Log.Info($"someone in the family started {GameNames.Of(Bot.GrindGame)} - leaving it to them and moving on", Bot.Name);
				Bot.StopGrind();

				return;   // the next tick sees the grind gone and starts the rest before the following game
			}

			_sawGrind = true;
			_status = _ours ? $"hunting {GameNames.Of(Bot.GrindGame)}" : "waiting - a manual grind is running";

			return;
		}

		// A grind just finished. Ours counts toward the run-length cap; somebody else's doesn't - but either way
		// the account has just spent hours on one game, so the rest before the next hunt starts now. (A boost
		// session that outlived a restart comes back as "not ours", which is exactly why this is not keyed on
		// _ours: the old code let a resumed session be followed immediately by a fresh one.)
		if (_sawGrind) {
			_sawGrind = false;
			_inARow += _ours ? 1 : 0;
			_ours = false;
			_lastEnded = DateTime.UtcNow;
			_status = "between games";

			return;
		}

		if (!Bot.IsOnline || !Bot.CanPlay) {
			return;
		}

		// Cards outrank achievements. A grind takes the account off whatever the farmer is doing, and a drop that
		// was twenty minutes away would have to start its hours over - so hunting waits for the farmer to finish.
		if (Bot.IsFarming) {
			_status = "waiting - farming cards first";

			return;
		}

		List<uint> targets = Targets();

		if (targets.Count == 0) {
			_status = _sweeping ? "on - working out which games are worth hunting"
				: Bot.Cfg.AchievementBoost == 2 ? "on - no single-player games with achievements found"
				: "on - no games to hunt (pick some under \"Boost these games\")";

			return;
		}

		// Human accounts hunt only while awake, and stay weighted-first: a stretch of the normal schedule sits
		// between boost sessions, and a longer one after a run of them.
		if (Bot.HumanOwned) {
			if (!HumanMode.AwakeFor(Bot)) {
				_status = "resting until the account is awake";

				return;
			}

			// Hunting comes out of the day's budget, not on top of it.
			//
			// Human mode rolls a target for how long the account plays today; a hunt is playing, so once that
			// target is met the day is over for hunting too. Without this the two settings quietly fought - the
			// schedule would finish its six hours and stop, and the hunter would carry on adding two-hour
			// sessions to an account that was supposed to be done for the night.
			HumanMode? human = BotManager.ModuleOf<HumanMode>(Bot);

			if ((human != null) && (human.TargetMinutesToday > 0) && (human.PlayedMinutesToday >= human.TargetMinutesToday)) {
				_status = "done for today - hunting again tomorrow";

				return;
			}

			// The gap is rolled ONCE per gap, not read from the setting each tick.
			//
			// A setting of 120 used literally means every gap between hunts is exactly two hours, for ever - and a
			// perfectly regular rhythm is the thing human mode exists to avoid. This spreads it across roughly
			// two-thirds to one-and-a-half times the setting, and holds that roll until the gap is served, so the
			// countdown doesn't jump about while it waits.
			int rest = Math.Max(15, Bot.Cfg.BoostRestMinutesHuman);
			bool capped = _inARow >= Math.Max(1, Bot.Cfg.MaxBoostGamesInARow);

			if (_restNeeded <= 0) {
				int spread = _rng.Next(rest * 65 / 100, (rest * 150 / 100) + 1);
				_restNeeded = capped ? spread * _rng.Next(25, 36) / 10 : spread;   // 2.5-3.5x after a run of them
			}

			if ((_lastEnded != DateTime.MinValue) && (DateTime.UtcNow - _lastEnded < TimeSpan.FromMinutes(_restNeeded))) {
				_status = $"weighted schedule - next hunt in {Fmt.Hm((int) (TimeSpan.FromMinutes(_restNeeded) - (DateTime.UtcNow - _lastEnded)).TotalMinutes)}";

				return;
			}

			_restNeeded = 0;   // served - the next gap rolls its own

			if (capped) {
				_inARow = 0;   // the longer weighted rest has been served; start a fresh run of boost sessions
			}
		}

		uint target = targets[_index % targets.Count];
		_index++;

		// Nobody plays for exactly two hours, twice. The setting is the middle of a range, not a stopwatch.
		int hours = Math.Clamp(Bot.Cfg.BoostSessionHours, 1, 24);
		int minutes = _rng.Next(hours * 60 * 70 / 100, (hours * 60 * 130 / 100) + 1);

		// Targets are already filtered, so a refusal here means the guard changed its mind between the two - fine,
		// leave it, the next tick picks the game after it.
		if (!Bot.StartGrind(target, TimeSpan.FromMinutes(minutes))) {
			return;
		}

		_ours = true;
		_status = $"hunting {GameNames.Of(target)}";
		Log.Info($"achievement boost - hunting {GameNames.Of(target)} for {Fmt.Hm(minutes)} ({_index}/{targets.Count} through the list)", Bot.Name);
	}
}
