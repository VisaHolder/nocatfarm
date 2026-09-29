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
/// A HUMAN account never hands itself over to a hunt: the game being hunted joins its weighted games (see
/// <see cref="HuntTarget"/>, at <c>BoostWeight</c>) and is played in ordinary sittings. A NON-human account rotates
/// targets back-to-back as grinds. It never fights a manual grind: while one the operator started is running, it stays out.
/// </summary>
public sealed class AchievementBoost(Bot bot) : BotModule(bot) {
	private int _index;                          // round-robin position in the target list

	/// <summary>
	/// On a human account, the game the hunter is on - human mode adds it to the games it plays. 0 when there isn't
	/// one (off, nothing to hunt, or not a human account).
	/// </summary>
	public uint HuntTarget { get; private set; }

	/// <summary>The hunt game while it may be played today - 0 once "Hunt at most, hours a day" is used up.</summary>
	public uint HuntTargetNow => DailyLimitReached ? 0 : HuntTarget;

	private DateTime _grindTick = DateTime.MinValue;   // a robot's hunt grind: when its time was last counted
	private double _todayMinutes;                // minutes hunted today (a human account's hunt game, or a robot's grinds)
	private DateTime _today = DateTime.MinValue;

	/// <summary>Per game: where it moves on (percent) and when it may come back.</summary>
	private readonly Dictionary<uint, GamePlan> _plans = [];

	private sealed record GamePlan(int SwitchAtPct, long RestUntil);

	private bool DailyLimitReached {
		get {
			RollDay();

			return (Bot.Cfg.BoostHoursPerDay > 0) && (_todayMinutes >= Bot.Cfg.BoostHoursPerDay * 60.0);
		}
	}

	private void RollDay() {
		if (_today != DateTime.Today) {
			_today = DateTime.Today;
			_todayMinutes = 0;
		}
	}

	/// <summary>This game's stopping point - a fresh random one in the range the first time it's seen.</summary>
	private GamePlan PlanFor(uint app) {
		if (!_plans.TryGetValue(app, out GamePlan? plan)) {
			int lo = Math.Clamp(Math.Min(Bot.Cfg.BoostSwitchFromPct, Bot.Cfg.BoostSwitchToPct), 5, 100);
			int hi = Math.Clamp(Math.Max(Bot.Cfg.BoostSwitchFromPct, Bot.Cfg.BoostSwitchToPct), lo, 100);
			plan = new GamePlan(_rng.Next(lo, hi + 1), 0);
			_plans[app] = plan;
		}

		return plan;
	}

	private bool Resting(uint app) => _plans.TryGetValue(app, out GamePlan? p) && (p.RestUntil > DateTime.UtcNow.Ticks);

	/// <summary>Far enough into this game for now: its achievements have reached its stopping point.</summary>
	private bool ReachedStop(uint app, out int pct) {
		pct = 0;
		(int unlocked, int total) = BotManager.ModuleOf<AchievementPacer>(Bot)?.Progress(app) ?? (0, 0);

		if (total == 0) {
			return false;
		}

		pct = unlocked * 100 / total;

		return pct >= PlanFor(app).SwitchAtPct;
	}

	/// <summary>
	/// Had enough of this game for now: back in a few days, and next time it goes a little further in - the way a
	/// person drifts between games and comes back to them, rather than finishing one after another.
	/// </summary>
	private void RestGame(uint app, int pct) {
		int lo = Math.Clamp(Math.Min(Bot.Cfg.BoostRestDaysMin, Bot.Cfg.BoostRestDaysMax), 0, 180);
		int hi = Math.Clamp(Math.Max(Bot.Cfg.BoostRestDaysMin, Bot.Cfg.BoostRestDaysMax), lo, 180);
		int days = _rng.Next(lo, hi + 1);
		int next = Math.Min(100, Math.Max(PlanFor(app).SwitchAtPct, pct) + _rng.Next(8, 16));
		_plans[app] = new GamePlan(next, DateTime.UtcNow.AddDays(days).AddMinutes(_rng.Next(0, 24 * 60)).Ticks);
		Log.Info(new Said("{0}: {1}% of its achievements - moving to another game, back to it in about {2} day(s)", GameNames.Of(app), pct, days), Bot.Name);
	}

	/// <summary>
	/// The next game to hunt: it takes turns between the first "Games in rotation" games on the list that aren't
	/// resting or at their stopping point - a handful on the go at once, like a person's - and when one of those
	/// rests or is done, the next game on the list takes its place. 0 when every game is resting.
	/// </summary>
	private uint NextTarget(List<uint> targets) =>
		Rotate(targets, Bot.Cfg.BoostGamesInRotation, app => {
			if (Resting(app)) {
				return false;
			}

			if (ReachedStop(app, out int pct)) {
				RestGame(app, pct);

				return false;
			}

			return true;
		}, ref _index, HuntTarget);

	/// <summary>
	/// The pick itself: the first <paramref name="size"/> usable games on the list, taken in turn, never straight back
	/// to <paramref name="current"/> when there's another to go to. 0 when none is usable.
	/// </summary>
	internal static uint Rotate(IReadOnlyList<uint> targets, int size, Func<uint, bool> usable, ref int index, uint current) {
		size = Math.Clamp(size, 1, 20);
		List<uint> active = [];

		foreach (uint app in targets) {
			if (!usable(app)) {
				continue;
			}

			active.Add(app);

			if (active.Count == size) {
				break;
			}
		}

		if (active.Count == 0) {
			return 0;
		}

		uint pick = active[index % active.Count];

		if ((pick == current) && (active.Count > 1)) {
			pick = active[++index % active.Count];
		}

		index++;

		return pick;
	}

	private double _huntMinutes;                  // minutes the hunt game has actually been played (human account)
	private int _huntGoal;                        // about how long it plays one hunt game before moving on
	private DateTime _lastHuntTick = DateTime.MinValue;
	private DateTime _huntSavedAt = DateTime.MinValue;
	private bool _huntLoaded;
	// Was the grind currently running started by the boost? Backed by the flag the grind itself persists, so
	// a session that outlived a restart is still recognised as ours - it used to come back disowned, which left
	// it running with nothing able to stop it short of ending the grind by hand.
	private bool _ours {
		get => Bot.GrindIsBoost;
		set => Bot.GrindIsBoost = value;
	}
	private bool _sawGrind;                       // a grind was running at the last tick (ours or the operator's)
	private bool _sweeping;                       // mid store-sweep: the target list is still being worked out

	/// <summary>New store lookups per tick. Each is throttled, so this is about a minute's worth.</summary>
	private const int LookupsPerTick = 40;
	private readonly Random _rng = new();
	private Said _status = new("");

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
						Log.Info(new Said("achievement boost switched off - leaving {0}", GameNames.Of(Bot.GrindGame)), Bot.Name);
						Bot.StopGrind();
					}

					_ours = false;
					_sawGrind = false;
					HuntTarget = 0;
					_status = new Said("off");
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Warn(new Said("achievement boost hiccup: {0}", e.Message), Bot.Name);
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

		string now = Bot.Grinding && _ours ? GameNames.Of(Bot.GrindGame) : HuntTarget != 0 ? GameNames.Of(HuntTarget) : "";

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
		} else if (HuntTarget != 0) {
			lines.Add($"   now: {GameNames.Of(HuntTarget)} is in its games - {Fmt.Hm((int) _huntMinutes)} of about {Fmt.Hm(_huntGoal)} played");
		} else if (Bot.Grinding) {
			lines.Add("   now: standing by - a grind you started is running");
		} else {
			lines.Add($"   now: {(_status.IsEmpty ? "starting up" : _status.ToString())}");
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
				_status = new Said("on - waiting for this account's library");
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
				_status = new Said("on - {0} game(s) so far, still working through the rest", found.Count);

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

			Log.Info(shared > 0
				? new Said("achievement boost - {0} game(s) worth hunting ({1} shared with this account)", found.Count, shared)
				: new Said("achievement boost - {0} game(s) worth hunting", found.Count), Bot.Name);
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

		AchievementPacer? pacer = BotManager.ModuleOf<AchievementPacer>(Bot);

		return raw.Where(app =>
			!(Bot.Cfg.YieldToFamily && Bot.Library.FamilyIsPlaying(app))
			&& !(pacer?.NothingLeft(app) ?? false)   // finished, at the ceiling, or Steam-only - a sitting would earn nothing
			&& !Bot.Refunds.Holds(app)
			&& !Bot.Cfg.BlacklistedGames.Contains(app)
			&& !Live.Global.GlobalBlacklistedGames.Contains(app)
			&& !Bot.Cfg.AchievementNeverGames.Contains(app)
			&& (app != main)
			&& ((allowed.Count == 0) || allowed.Contains(app))).ToList();
	}

	// ── the boost decision ───────────────────────────────────────────────────
	private void Tick() {
		// A hunt running as a grind on a human account - from before hunts joined the day, or picked back up after a
		// restart. Hand the account back to human mode and keep the game, now as one of the games it plays.
		if (Bot.Cfg.LegitMode && Bot.Grinding && _ours) {
			LoadHunt();
			uint game = Bot.GrindGame;
			Bot.StopGrind();
			_ours = false;

			if (HuntTarget != game) {
				StartHunt(game);
			}

			return;
		}

		// A grind is running. If it's ours, let it run; if it's a manual grind, stay completely out of the way.
		if (Bot.Grinding) {
			// Unless the family has taken the game back. Steam lends a shared game to one person at a time and the
			// owner wins, so carrying on would leave the account "playing" something it has been thrown out of -
			// earning nothing and looking like it is. Hand it back and move down the list; the twenty-minute grace
			// in the library keeps it out of the rotation until they are actually finished with it.
			if (_ours && Bot.Cfg.YieldToFamily && Bot.Library.FamilyIsPlaying(Bot.GrindGame)) {
				Log.Info(new Said("someone in the family started {0} - leaving it to them and moving on", GameNames.Of(Bot.GrindGame)), Bot.Name);
				Bot.StopGrind();

				return;   // the next tick sees the grind gone and starts the rest before the following game
			}

			// Everything worth earning here is earned - up to the ceiling, or all that a client can set. The rest of
			// the sitting would be hours on a game with nothing left in it; close it and move on, like a person would.
			if (_ours && (BotManager.ModuleOf<AchievementPacer>(Bot)?.NothingLeft(Bot.GrindGame) == true)) {
				Log.Info(new Said("done with {0}'s achievements for now - moving on", GameNames.Of(Bot.GrindGame)), Bot.Name);
				Bot.StopGrind();

				return;
			}

			// The day's hunting counts the time really played - a grind cut short counts what it got to.
			if (_ours) {
				DateTime now = DateTime.UtcNow;

				if (_grindTick != DateTime.MinValue) {
					RollDay();
					_todayMinutes += Math.Clamp((now - _grindTick).TotalMinutes, 0, 5);
					SaveHunt();
				}

				_grindTick = now;
			}

			_sawGrind = true;
			_status = _ours ? new Said("hunting {0}", GameNames.Of(Bot.GrindGame)) : new Said("waiting - a manual grind is running");

			return;
		}

		// A grind just finished, ours or somebody else's: the account has just spent hours on one game, so the next
		// hunt waits a tick rather than starting in the same breath.
		if (_sawGrind) {
			_sawGrind = false;
			_ours = false;
			_grindTick = DateTime.MinValue;
			_status = new Said("between games");

			return;
		}

		if (!Bot.IsOnline || !Bot.CanPlay) {
			return;
		}

		// Cards outrank achievements. A grind takes the account off whatever the farmer is doing, and a drop that
		// was twenty minutes away would have to start its hours over - so hunting waits for the farmer to finish.
		if (Bot.IsFarming) {
			_status = new Said("waiting - farming cards first");

			return;
		}

		List<uint> targets = Targets();

		if (targets.Count == 0) {
			HuntTarget = 0;
			_status = _sweeping ? new Said("on - working out which games are worth hunting")
				: Bot.Cfg.AchievementBoost == 2 ? new Said("on - no single-player games with achievements found")
				: new Said("on - no games to hunt (pick some under \"Boost these games\")");

			return;
		}

		// A human account: the hunt game joins the day instead of taking it over.
		if (Bot.Cfg.LegitMode) {
			HumanTick(targets);

			return;
		}

		LoadHunt();

		if (DailyLimitReached) {
			_status = new Said("done hunting for today (Hunt at most, hours a day)");

			return;
		}

		uint target = NextTarget(targets);

		if (target == 0) {
			_status = new Said("every game on the list is resting for now - it comes back to them in a few days");

			return;
		}

		// Nobody plays for exactly two hours, twice. The setting is the middle of a range, not a stopwatch.
		int hours = Math.Clamp(Bot.Cfg.BoostSessionHours, 1, 24);
		int minutes = _rng.Next(hours * 60 * 70 / 100, (hours * 60 * 130 / 100) + 1);

		// Never past today's limit.
		if (Bot.Cfg.BoostHoursPerDay > 0) {
			minutes = Math.Max(15, Math.Min(minutes, (int) ((Bot.Cfg.BoostHoursPerDay * 60.0) - _todayMinutes)));
		}

		// Targets are already filtered, so a refusal here means the guard changed its mind between the two - fine,
		// leave it, the next tick picks the game after it.
		if (!Bot.StartGrind(target, TimeSpan.FromMinutes(minutes), boost: true)) {
			return;
		}

		_ours = true;
		_grindTick = DateTime.UtcNow;
		_status = new Said("hunting {0}", GameNames.Of(target));
		Log.Info(new Said("achievement boost - hunting {0} for {1} ({2}/{3} through the list)", GameNames.Of(target), Fmt.Hm(minutes), _index, targets.Count), Bot.Name);
	}

	/// <summary>
	/// A human account: the game being hunted is one of the games it plays. Human mode picks it for some of its
	/// ordinary sittings (at "hunt game's weight"), with breaks and bedtime as usual, and never cuts another game short.
	/// Once it has had about "Play each for about" of playing, or has nothing left to earn, the next game on the list
	/// takes its place. Where it is on the list is kept across restarts, so a restart doesn't go back to the top.
	/// </summary>
	private void HumanTick(List<uint> targets) {
		LoadHunt();
		DateTime now = DateTime.UtcNow;
		double elapsed = _lastHuntTick == DateTime.MinValue ? 0 : (now - _lastHuntTick).TotalMinutes;
		_lastHuntTick = now;

		// Only minutes it really played the hunt game count - a tick that ran late is a gap, not play.
		if ((HuntTarget != 0) && (BotManager.ModuleOf<HumanMode>(Bot)?.PlayingNow == HuntTarget) && (elapsed is > 0 and <= 3)) {
			_huntMinutes += elapsed;
			RollDay();
			_todayMinutes += elapsed;
		}

		bool nothingLeft = (HuntTarget != 0) && (BotManager.ModuleOf<AchievementPacer>(Bot)?.NothingLeft(HuntTarget) ?? false);
		int pct = 0;
		bool enough = (HuntTarget != 0) && !nothingLeft && ReachedStop(HuntTarget, out pct);

		if ((HuntTarget == 0) || !targets.Contains(HuntTarget) || nothingLeft || enough || Resting(HuntTarget) || (_huntMinutes >= _huntGoal)) {
			if (nothingLeft) {
				Log.Info(new Said("done with {0}'s achievements for now - moving on", GameNames.Of(HuntTarget)), Bot.Name);
			} else if (enough) {
				RestGame(HuntTarget, pct);
			}

			uint next = NextTarget(targets);

			if (next == 0) {
				HuntTarget = 0;
				_status = new Said("every game on the list is resting for now - it comes back to them in a few days");
				SaveHunt();

				return;
			}

			StartHunt(next, targets.Count);
			SaveHunt();
		} else if (now - _huntSavedAt > TimeSpan.FromMinutes(10)) {
			SaveHunt();
		}

		_status = DailyLimitReached
			? new Said("{0} is done for today (Hunt at most, hours a day) - back tomorrow", GameNames.Of(HuntTarget))
			: new Said("{0} is in its games - {1} of about {2} played", GameNames.Of(HuntTarget), Fmt.Hm((int) _huntMinutes), Fmt.Hm(_huntGoal));
	}

	private void StartHunt(uint game, int listed = 0) {
		HuntTarget = game;
		_huntMinutes = 0;

		// Nobody plays exactly two hours of a game, twice. The setting is the middle of a range, not a stopwatch.
		int hours = Math.Clamp(Bot.Cfg.BoostSessionHours, 1, 24);
		_huntGoal = _rng.Next(hours * 60 * 70 / 100, (hours * 60 * 130 / 100) + 1);

		Log.Info(listed > 0
			? new Said("achievement boost - {0} joins the games it plays, for about {1} ({2}/{3} through the list)", GameNames.Of(game), Fmt.Hm(_huntGoal), ((_index - 1 + listed) % listed) + 1, listed)
			: new Said("achievement boost - {0} joins the games it plays, for about {1}", GameNames.Of(game), Fmt.Hm(_huntGoal)), Bot.Name);
		SaveHunt();
	}

	private sealed record HuntSave(int Index, uint Target, double Minutes, int Goal, double TodayMinutes = 0, DateTime Today = default,
		Dictionary<uint, GamePlan>? Plans = null);

	private string HuntPath => Path.Combine(Config.ConfigStore.ConfigDir, "state", $"hunt-{Bot.Name}.json");

	private void LoadHunt() {
		if (_huntLoaded) {
			return;
		}

		_huntLoaded = true;

		try {
			if (File.Exists(HuntPath) && (JsonSerializer.Deserialize<HuntSave>(File.ReadAllText(HuntPath)) is { } saved)) {
				_index = Math.Max(0, saved.Index);
				HuntTarget = saved.Target;
				_huntMinutes = Math.Max(0, saved.Minutes);
				_huntGoal = Math.Max(30, saved.Goal);
				_today = saved.Today.Date;
				_todayMinutes = Math.Max(0, saved.TodayMinutes);

				foreach ((uint app, GamePlan plan) in saved.Plans ?? []) {
					_plans[app] = plan;
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// A bad file is the same as none: it starts again from the top of the list.
		}
	}

	private void SaveHunt() {
		_huntSavedAt = DateTime.UtcNow;

		try {
			AtomicFile.Write(HuntPath, JsonSerializer.Serialize(new HuntSave(_index, HuntTarget, _huntMinutes, _huntGoal, _todayMinutes, _today, _plans)));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// Next time it simply doesn't remember where it was on the list.
		}
	}
}
