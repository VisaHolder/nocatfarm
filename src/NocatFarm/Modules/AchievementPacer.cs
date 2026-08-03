using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Earning achievements at a rate a real player would.
/// </summary>
/// <remarks>
/// A faithful port of the pacer from the ArchiSteamFarm HumanIdler plugin, which was tuned against live Steam
/// rarity data and how-long-to-beat figures over a long period. The old drip here - a few a day, easiest first
/// - was a reasonable first approximation and is not in the same league: it had no notion of a game's shape, no
/// completion ceiling, no rarity gate tied to playtime, and it spaced unlocks evenly, which is the one thing
/// real play never does.
///
/// What makes an unlock history look earned rather than granted:
///
///   • <b>Rarity opens with playtime.</b> A locked achievement that only 3% of owners have is not eligible at
///     two hours in. <see cref="RarityFloorForHours"/> starts at "40%+ only" and steps down as the hours
///     accumulate, bottoming at 1% around eighty hours. It never reaches 0%: sub-half-a-percent achievements on
///     an idled account are the actual tell.
///
///   • <b>Games have shapes.</b> A three-hour puzzle game front-loads its easy cluster and then stops. A
///     hundred-hour co-op grind pops a quick early burst and then crawls for months. One global rate cannot
///     express both, so each game carries its own <see cref="Profile"/>.
///
///   • <b>Nobody finishes.</b> Every game gets a completion ceiling rolled once, well short of 100% - a
///     perfect completion on an idle account is itself the giveaway.
///
///   • <b>Real unlocks cluster.</b> Finishing a level pops three at once and then nothing for a day. A steady
///     one-every-N-minutes metronome does not happen, so bursts are modelled explicitly.
///
/// One rule comes across with it, learned the hard way:
///   • <b>Never the main game.</b> The account's headline game is the one people look at, so it is left alone
///     during normal play. (A deliberate grind still earns it - that path is opt-in, so it's yours to make.)
/// Games are excluded only if you list them under <c>AchievementNeverGames</c>; nothing is hardcoded off. (VAC
/// does not act on achievement writes - it's an in-game anti-cheat - so there is no game to block on that basis.)
/// </remarks>
public sealed class AchievementPacer(Bot bot) : BotModule(bot) {
	/// <summary>
	/// How a particular game doles out its achievements.
	///
	/// Gaps are wall-clock MINUTES between unlocks. The played gates are ACTUAL in-game minutes accrued since
	/// the last unlock, so a game that is only idled twenty minutes a week advances at a twentieth of the pace.
	/// </summary>
	private sealed record Profile(
		int OnboardCount,
		int OnboardGapLo,
		int OnboardGapHi,
		int OnboardPlayedMins,
		int SteadyGapLo,
		int SteadyGapHi,
		int SteadyPlayedMins,
		double RarityScale,
		int CeilingLo,
		int CeilingHi,
		int MinPercent = 0
	);

	//                          onboard: count, gap lo-hi, played gate | steady: gap lo-hi, played gate | rarity scale | ceiling lo-hi
	private static readonly Dictionary<uint, Profile> Profiles = new() {
		{ 400, new Profile(5, 8, 20, 10, 40, 90, 30, 0.5, 33, 45) },            // Portal - 15 achievements, ~3h. Quick easy cluster, challenge tail basically never.
		{ 286690, new Profile(5, 12, 30, 12, 40, 90, 25, 1.0, 50, 60) },        // Metro 2033 Redux - 49, ~7h story.
		{ 220, new Profile(6, 12, 30, 12, 40, 90, 25, 1.3, 40, 50) },           // Half-Life 2 - 69, ~13h, big collectible tail.
		{ 719070, new Profile(5, 12, 30, 12, 40, 90, 25, 1.0, 50, 60) },        // Project Warlock - ~40, ~8h retro FPS.
		{ 500, new Profile(9, 6, 18, 8, 45, 100, 30, 2.0, 30, 40) },            // Left 4 Dead - 73, co-op grind.
		{ 550, new Profile(10, 6, 18, 8, 45, 100, 30, 2.0, 25, 35) },           // Left 4 Dead 2 - 101, co-op grind.
		{ 440, new Profile(10, 6, 18, 8, 45, 100, 30, 2.0, 18, 28) },           // Team Fortress 2 - 520, a huge 5-19% middle tier and only 10 genuinely rare.
		{ 1938090, new Profile(3, 60, 180, 30, 150, 360, 50, 1.0, 12, 18) },    // Call of Duty - 157, every one of them under 10% globally. Playtime-gated hard; never the 0% tail.
		{ 1172470, new Profile(3, 20, 60, 15, 90, 220, 35, 1.0, 60, 80) },      // Apex Legends - 12, all >=10%. A real player has most of them.
		{ 578080, new Profile(4, 20, 60, 15, 90, 240, 35, 1.3, 40, 58) },       // PUBG - 37, battle-royale grind. The lone 0% is skipped automatically.
		{ 1085660, new Profile(4, 20, 60, 15, 90, 240, 35, 1.2, 48, 66) },      // Destiny 2 - 23, healthy spread.
		{ 1808500, new Profile(4, 15, 45, 12, 70, 200, 30, 1.0, 55, 72) },      // ARC Raiders - 50, 39 of them common.
		{ 1943950, new Profile(4, 15, 45, 12, 60, 180, 30, 1.0, 45, 62) }       // Muck - 34, healthy spread.
	};

	/// <summary>
	/// Anything not in the table. Conservative but not glacial.
	///
	/// The steady gaps are wall-clock minutes, paced so that active play earns roughly one an hour while the
	/// rarity floor and the played-time gate still hold the hard tail shut. An earlier version used gaps of
	/// eight to forty HOURS, which meant an all-night session earned nothing at all.
	/// </summary>
	private static readonly Profile Fallback = new(3, 20, 120, 25, 60, 150, 25, 1.0, 40, 65);

	private static Profile ProfileFor(uint app) => Profiles.TryGetValue(app, out Profile? p) ? p : Fallback;

	/// <summary>
	/// The rarest an achievement may be, as a percentage of owners, for a given number of hours in the game.
	///
	/// This is the heart of it. An achievement below the floor is not eligible yet, so the rare tail opens
	/// tier by tier as the hours accumulate rather than being available from the first minute.
	/// </summary>
	private static int RarityFloorForHours(double hours) =>
		hours < 0.5 ? 101 :   // under half an hour in: nothing at all yet
		hours < 2 ? 40 :      // the tutorial tier
		hours < 6 ? 25 :
		hours < 12 ? 15 :
		hours < 22 ? 9 :
		hours < 35 ? 5 :
		hours < 50 ? 3 :      // around forty hours the genuinely low-percentage tail starts opening
		hours < 80 ? 2 :
		1;                    // never 0: a sub-1% achievement on an idled account is the giveaway

	private sealed class GameState {
		public long PlayedMins;         // our accumulated in-game minutes, which drive the rarity gate
		public long MinsAtLastUnlock;   // enforces the played-time gap
		public DateTime NextAllow;      // earliest wall-clock moment the next unlock may fire
		public int Ceiling = -1;        // rolled once per game: the most of this game we will ever complete
		public int Unlocked = -1;       // last known unlocked count; -1 means unknown, so treat as onboarding
		public int BurstLeft;           // mid-burst: this many more pop quickly, bypassing the played-time gate
	}

	private readonly Dictionary<uint, GameState> _games = [];
	private readonly Random _rng = new();
	private readonly Lock _gate = new();

	private DateTime _lastTick = DateTime.MinValue;
	private bool _loaded;
	private string _status = "off";

	public override string Name => "achievements";
	public override string Status => Bot.Cfg.UnlockAchievements ? _status : "";

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				if (Bot.Cfg.UnlockAchievements) {
					await StepAsync(ct).ConfigureAwait(false);
				} else {
					_status = "off";
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Warn($"achievement pacer hiccup: {e.GetType().Name}: {e.Message}", Bot.Name);
			}

			if (!await Sleep(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private async Task StepAsync(CancellationToken ct) {
		if (!_loaded) {
			_loaded = true;
			Load();
		}

		if (!Bot.IsOnline || Bot.Paused || Bot.PlayingBlocked) {
			_status = "waiting";

			return;
		}

		// One minute of credit per tick, and only for time that genuinely elapsed. Without this a restart loop,
		// or a tick that ran late, would hand out playtime the account never spent - and playtime is exactly
		// what the rarity gate is made of, so inflating it is how a bot ends up popping a 2% achievement on day
		// one. Anything longer than three minutes is treated as a gap rather than as time played.
		DateTime now = DateTime.UtcNow;
		double elapsed = _lastTick == DateTime.MinValue ? 0 : (now - _lastTick).TotalMinutes;
		_lastTick = now;

		if ((elapsed < 0.5) || (elapsed > 3)) {
			return;
		}

		List<uint> running = CurrentGames();

		if (running.Count == 0) {
			_status = "nothing being played";

			return;
		}

		foreach (uint app in running) {
			GameState g = StateFor(app);
			Profile prof = ProfileFor(app);

			lock (_gate) {
				if (g.Ceiling < 0) {
					g.Ceiling = _rng.Next(prof.CeilingLo, prof.CeilingHi + 1);
				}

				g.PlayedMins++;
			}

			if (!Due(g, prof)) {
				continue;
			}

			// One unlock per tick across the whole account - two games popping an achievement in the same minute
			// is not something that happens to a person playing one game at a time. But ONLY when one actually
			// fired: breaking on a no-op meant the first unlucky game in the list starved every game behind it.
			if (await TryUnlockOneAsync(app, g, prof, ct).ConfigureAwait(false)) {
				break;
			}
		}

		Save();
		_status = Describe();
	}

	/// <summary>
	/// Which games are eligible for a minute of credit right now.
	///
	/// The main game is deliberately excluded during normal play (a grind still earns it), plus anything on the account's AchievementNeverGames list. This came across from the
	/// ArchiSteamFarm version, where they were arrived at deliberately rather than by accident.
	/// </summary>
	private List<uint> CurrentGames() {
		List<uint> allowed = Bot.Cfg.AchievementGames;
		HumanMode? human = BotManager.ModuleOf<HumanMode>(Bot);
		List<uint> running = [];

		if (human is { Current: not HumanMode.Phase.Off }) {
			uint playing = human.PlayingNow;

			if ((playing != 0) && (playing != human.MainGameId)) {
				running.Add(playing);
			}
		} else {
			// What Steam has actually been told is running, NOT the configured idle list. Those differ whenever
			// the card farmer has claimed the account, and crediting the configured list there would award hours
			// nobody spent - which is the one way this could open the rarity gate earlier than it should.
			running.AddRange(Bot.PlayingApps);
		}

		List<uint> never = Bot.Cfg.AchievementNeverGames;

		return running
			.Where(a => !never.Contains(a) && ((allowed.Count == 0) || allowed.Contains(a)))
			.Distinct()
			.ToList();
	}

	/// <summary>The cheap gates, which need no network: enough playtime since the last one, and past the spacing.</summary>
	private bool Due(GameState g, Profile prof) {
		lock (_gate) {
			bool onboarding = (g.Unlocked < 0) || (g.Unlocked < prof.OnboardCount);
			int playedGate = onboarding ? prof.OnboardPlayedMins : prof.SteadyPlayedMins;

			// Mid-burst is the exception: a burst is several achievements from one moment of play, so they are
			// only a couple of wall-clock minutes apart and share the same played-time.
			// A deliberate grind skips the slow played-time gate - you told it to sit on this game, so it
			// works through the achievements briskly instead of making you wait hours between each.
			if ((g.BurstLeft <= 0) && ((g.PlayedMins - g.MinsAtLastUnlock) < playedGate)) {
				return false;
			}

			return DateTime.UtcNow >= g.NextAllow;
		}
	}

	/// <summary>Returns true only when an achievement was actually unlocked.</summary>
	private async Task<bool> TryUnlockOneAsync(uint app, GameState g, Profile prof, CancellationToken ct) {
		AchievementSet? set = await Achievements.GetAsync(Bot, app, ct).ConfigureAwait(false);

		// Back off even here.
		//
		// Most owned apps - DLC, tools, soundtracks - simply have no achievements, and this path returned
		// without touching NextAllow. A new GameState starts at DateTime.MinValue, so once the played gate was
		// met that game was due forever: every tick asked Steam for its stats again, and because the caller
		// then broke out of the loop unconditionally, no game behind it in the list ever got a look. The pacer
		// went silently dead for the account while the status line kept reporting healthy progress.
		if (set == null) {
			Back(g, TimeSpan.FromMinutes(_rng.Next(30, 90)));   // transient - Steam refused, ask again later

			return false;
		}

		if (set.All.Count == 0) {
			Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));      // it has none, and never will

			return false;
		}

		int total = set.All.Count;
		int already = set.All.Count(static a => a.Unlocked);

		// A grind works through the WHOLE game (ignores the rarity floor, up to the account's completion cap), but
		// it still unlocks at the account's own Achievement-pace setting - never instantly, human mode or not. The
		// only thing human-mode changes about a grind is the game-switch timing, not the achievement speed.
		bool grind = Bot.Grinding && (Bot.GrindGame == app);

		lock (_gate) {
			g.Unlocked = already;
		}

		// Stop well short of everything. Never below the onboarding cluster though, or a game with a low
		// ceiling and a big early cluster (Portal: 33% of 15 is 4, but it onboards 5) gets cut off mid-burst.
		// The account's own cap wins when it is stricter than the game's. Never the other way round: raising a
		// game's tuned ceiling from a settings box would undo the research behind it.
		int ceiling = g.Ceiling;
		int cap = Bot.Cfg.AchievementMaxCompletionPct;

		if ((cap > 0) && (cap < ceiling)) {
			ceiling = cap;
		}

		// A grind may take a game far past the stealthy research ceiling - up to the account's own
		// completion cap, or all of them when no cap is set.
		if (grind) {
			ceiling = (cap > 0) ? cap : 100;
		}

		int ceilingCount = Math.Min(total, Math.Max(prof.OnboardCount, total * ceiling / 100));

		if (already >= ceilingCount) {
			// Done with this game. Back off hard rather than re-reading Steam's stats every played minute.
			Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));

			return false;
		}

		double hours = g.PlayedMins / 60.0;
		bool onboarding = already < prof.OnboardCount;
		// A grind ignores the rarity floor: it works through everything settable, most-common first, rather
		// than only what the hours "justify". Normal play keeps the floor so it never pops a rare one early.
		int floor = grind ? 1 : Math.Max(Math.Max(1, prof.MinPercent), RarityFloorForHours(hours / prof.RarityScale));

		List<Achievement> eligible = set.All
			// Unknown rarity (Steam's global-percent endpoint was unreachable) counts as eligible rather than being
			// excluded - otherwise a missing fetch would silently stop the account unlocking anything at all.
			.Where(a => !a.Unlocked && a.Settable && !IsSpecialGlobal(a) && ((a.GlobalPercent ?? floor) >= floor))
			.ToList();

		if (eligible.Count == 0) {
			lock (_gate) {
				g.BurstLeft = 0;
			}

			// When a grind can't unlock anything but the game still has locked achievements, the usual
			// reason is Steam controls them server-side (Counter-Strike 2 is the classic case) - say so plainly
			// and stop hammering Steam's stats for it, rather than looking silently stuck.
			if (grind && set.All.Any(a => !a.Unlocked && !IsSpecialGlobal(a)) && !set.All.Any(a => !a.Unlocked && a.Settable && !IsSpecialGlobal(a))) {
				Log.Info($"can't unlock {GameNames.Of(app)}'s achievements - Steam sets them server-side, so there's nothing to grind here", Bot.Name);
				Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));

				return false;
			}

			// The gate is shut at this playtime, which is it working, not failing. Come back later rather than
			// asking again every minute (sooner during a grind, which is actively waiting on them).
			Back(g, TimeSpan.FromMinutes(grind ? _rng.Next(15, 41) : _rng.Next(60, 181)));

			return false;
		}

		// Strictly most-common first, working toward the rarest last, which is broadly what real players do.
		// A window of "one of the top three" looked reasonable and was not: it would occasionally pop a 12% one
		// while an 18% and a 14% sat still locked, and that ordering is unmistakably mechanical. The only
		// wobble kept is an occasional step to the SECOND most common, so it is not a flawless metronome.
		eligible.Sort(static (a, b) => (b.GlobalPercent ?? 0).CompareTo(a.GlobalPercent ?? 0));
		int idx = (eligible.Count > 1) && (_rng.Next(100) < 20) ? 1 : 0;
		Achievement pick = eligible[idx];

		(bool ok, string message) = await Achievements.SetAsync(Bot, set, [pick], true, ct).ConfigureAwait(false);

		if (!ok) {
			Log.Debug($"couldn't unlock \"{pick.Display}\" in {GameNames.Of(app)} - {message}", Bot.Name);
			Back(g, TimeSpan.FromMinutes(_rng.Next(30, 90)));

			return false;
		}

		int nowUnlocked = already + 1;
		int gap;

		lock (_gate) {
			g.MinsAtLastUnlock = g.PlayedMins;
			g.Unlocked = nowUnlocked;

			// Cluster like a person: several within a couple of minutes as a level or campaign finishes, then
			// nothing for a long stretch. A steady one-every-N-minutes drip is the thing to avoid.
			if (g.BurstLeft > 0) {
				g.BurstLeft--;
				gap = _rng.Next(1, 5);
			} else if ((eligible.Count > 1) && (_rng.Next(100) < (onboarding ? 45 : 12))) {
				g.BurstLeft = _rng.Next(1, 3);
				gap = _rng.Next(1, 5);
			} else {
				gap = onboarding
					? _rng.Next(prof.OnboardGapLo, prof.OnboardGapHi + 1)
					: _rng.Next(prof.SteadyGapLo, prof.SteadyGapHi + 1);
			}

			g.NextAllow = DateTime.UtcNow.AddMinutes(Paced(gap));
		}

		string rarity = pick.GlobalPercent is { } percent ? $" ({percent:0.#}% of owners have it)" : "";
		Log.Reward($"unlocked \"{pick.Display}\" in {GameNames.Of(app)}{rarity}  ({nowUnlocked}/{total})", Bot.Name);

		return true;
	}

	/// <summary>
	/// Valve's mass-granted community achievements, which are not earned by playing.
	///
	/// Left 4 Dead 2's GNOME ALONE is the example: handed out en masse in 2010, so it reads as a comfortable
	/// ~69% "common" while being nothing of the sort. The pacer must never feature one.
	/// </summary>
	private static bool IsSpecialGlobal(Achievement a) => a.Name.StartsWith("GLOBAL_", StringComparison.Ordinal);

	/// <summary>
	/// Stretch or shorten a gap by the account's pace.
	///
	/// Only ever the WAITING. The rarity floor and the played-time gate are untouched by this, so no pace
	/// setting can make an account unlock something it has not put the hours in for - "brisk" simply spends
	/// its eligible unlocks sooner, it does not get more of them.
	/// </summary>
	private int Paced(int minutes) => Bot.Cfg.AchievementPace switch {
		0 => minutes * 2,
		2 => Math.Max(1, minutes / 2),
		_ => minutes
	};

	private void Back(GameState g, TimeSpan wait) {
		lock (_gate) {
			g.NextAllow = DateTime.UtcNow.Add(wait);
		}
	}

	private GameState StateFor(uint app) {
		lock (_gate) {
			if (!_games.TryGetValue(app, out GameState? g)) {
				g = new GameState();
				_games[app] = g;
			}

			return g;
		}
	}

	/// <summary>One row per game the pacer is tracking, for anything that wants to show its working.</summary>
	public sealed record Row(
		uint App,
		string Game,
		int PlayedMinutes,
		double EffectiveHours,
		int FloorPercent,
		int CeilingPercent,
		int Unlocked,
		DateTime NextAllow,
		bool Blocked,
		string Why
	);

	/// <summary>
	/// What the pacer is doing, per game.
	///
	/// Worth exposing rather than keeping to itself: the whole reason to trust this thing is that it refuses to
	/// unlock what the hours cannot justify, and there is no way to believe that from a settings page. Showing
	/// the accumulated hours, the rarity floor those hours have opened, and when the next one is even allowed
	/// turns an assertion into something checkable.
	/// </summary>
	public IReadOnlyList<Row> Snapshot() {
		List<uint> never = Bot.Cfg.AchievementNeverGames;
		List<uint> allowed = Bot.Cfg.AchievementGames;
		uint main = BotManager.ModuleOf<HumanMode>(Bot)?.MainGameId ?? 0;

		lock (_gate) {
			return _games
				.OrderByDescending(static kv => kv.Value.PlayedMins)
				.Select(kv => {
					Profile prof = ProfileFor(kv.Key);
					double eff = kv.Value.PlayedMins / 60.0 / prof.RarityScale;
					int floor = Math.Max(Math.Max(1, prof.MinPercent), RarityFloorForHours(eff));

					string why =
						never.Contains(kv.Key) ? "on your never list" :
						(main != 0) && (kv.Key == main) ? "the main game - left alone" :
						(allowed.Count > 0) && !allowed.Contains(kv.Key) ? "not on your allow list" :
						floor > 100 ? "not enough hours yet" :
						"";

					return new Row(
						kv.Key,
						GameNames.Of(kv.Key),
						(int) kv.Value.PlayedMins,
						eff,
						Math.Min(floor, 100),
						kv.Value.Ceiling,
						kv.Value.Unlocked,
						kv.Value.NextAllow,
						why.Length > 0,
						why);
				})
				.ToList();
		}
	}

	private string Describe() {
		lock (_gate) {
			if (_games.Count == 0) {
				return "watching";
			}

			(uint app, GameState g) = _games.OrderByDescending(static kv => kv.Value.PlayedMins).First();
			double hours = g.PlayedMins / 60.0;

			return $"{GameNames.Of(app)}: {hours:0.#}h in, next after {g.NextAllow.ToLocalTime():HH:mm}";
		}
	}

	// ── remembering where it got to ─────────────────────────────────────────
	private sealed class Saved {
		public uint App { get; set; }
		public long PlayedMins { get; set; }
		public long MinsAtLastUnlock { get; set; }
		public DateTime NextAllow { get; set; }
		public int Ceiling { get; set; } = -1;
		public int Unlocked { get; set; } = -1;
	}

	private static string PathFor(string bot) => Path.Combine(ConfigStore.ConfigDir, "state", $"cheevo-{bot}.json");

	private void Load() {
		try {
			string path = PathFor(Bot.Name);

			if (!File.Exists(path)) {
				ImportFromArchiSteamFarm();

				return;
			}

			List<Saved>? saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(path));

			if (saved == null) {
				return;
			}

			lock (_gate) {
				foreach (Saved s in saved) {
					_games[s.App] = new GameState {
						PlayedMins = s.PlayedMins,
						MinsAtLastUnlock = s.MinsAtLastUnlock,
						NextAllow = s.NextAllow,
						Ceiling = s.Ceiling,
						Unlocked = s.Unlocked
					};
				}
			}

			Log.Debug($"achievement pacing restored for {saved.Count} game(s)", Bot.Name);
		} catch (Exception e) {
			Log.Debug($"couldn't read the achievement state: {e.GetType().Name}: {e.Message}", Bot.Name);
		}
	}

	/// <summary>
	/// Pick up where ArchiSteamFarm left off, the first time this runs.
	///
	/// The accumulated per-game minutes ARE the pacing - they are what the rarity gate reads - so starting from
	/// zero on an account that has already idled hundreds of hours would slam the gate shut and earn nothing
	/// for weeks. The plugin's format is one line per game: appId|playedMinutes|nextAllowTicks|ceiling.
	/// </summary>
	private void ImportFromArchiSteamFarm() {
		try {
			// Found rather than hard-coded. AsfImport.Detect() already locates an ArchiSteamFarm install for the
			// account importer, and the plugin's state files sit in the install root, one level above config.
			string? config = AsfImport.Detect();

			if (config == null) {
				return;
			}

			string? root = Path.GetDirectoryName(config);

			if (root == null) {
				return;
			}

			string source = Path.Combine(root, $"humanidler.cheevo.{Bot.Name}.state");

			if (!File.Exists(source)) {
				return;
			}

			int taken = 0;

			lock (_gate) {
				foreach (string line in File.ReadAllLines(source)) {
					string[] parts = line.Split('|');

					if ((parts.Length < 4)
						|| !uint.TryParse(parts[0], out uint app)
						|| !long.TryParse(parts[1], out long mins)
						|| !long.TryParse(parts[2], out long ticks)
						|| !int.TryParse(parts[3], out int ceiling)) {
						continue;
					}

					// The ticks are DateTime.Now on the machine that wrote them. Anything already in the past is
					// simply "due", so it does not matter that the clock reference differs.
					DateTime next = DateTime.UtcNow;

					if (ticks > 0) {
						try {
							DateTime local = new(ticks, DateTimeKind.Local);
							next = local.ToUniversalTime();
						} catch (ArgumentOutOfRangeException) {
							next = DateTime.UtcNow;
						}
					}

					_games[app] = new GameState {
						PlayedMins = mins,
						MinsAtLastUnlock = mins,
						NextAllow = next,
						Ceiling = ceiling
					};

					taken++;
				}
			}

			if (taken > 0) {
				Log.Good($"picked up ArchiSteamFarm's achievement pacing for {taken} game(s) - carrying on from where it left off", Bot.Name);
				Save();
			}
		} catch (Exception e) {
			Log.Debug($"couldn't import the ArchiSteamFarm achievement state: {e.GetType().Name}: {e.Message}", Bot.Name);
		}
	}

	private void Save() {
		try {
			List<Saved> saved;

			lock (_gate) {
				saved = _games.Select(static kv => new Saved {
					App = kv.Key,
					PlayedMins = kv.Value.PlayedMins,
					MinsAtLastUnlock = kv.Value.MinsAtLastUnlock,
					NextAllow = kv.Value.NextAllow,
					Ceiling = kv.Value.Ceiling,
					Unlocked = kv.Value.Unlocked
				}).ToList();
			}

			string path = PathFor(Bot.Name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Debug($"couldn't save the achievement state: {e.GetType().Name}: {e.Message}", Bot.Name);
		}
	}
}
