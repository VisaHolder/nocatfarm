using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Runtime.CompilerServices;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Earning achievements at a rate a real player would.
/// </summary>
/// <remarks>
/// Brought across from our own earlier HumanIdler plugin, where it was tuned against live Steam
/// rarity data and how-long-to-beat figures over a long period. The old drip here - a few a day, easiest first
/// - was a reasonable first approximation and is not in the same league: it had no notion of a game's shape, no
/// completion ceiling, no rarity gate tied to playtime, and it spaced unlocks evenly, which is the one thing
/// real play never does. (The per-game ceilings that came across with it are gone - see the ceiling note below.)
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
///   • <b>Nobody finishes.</b> Nothing is taken past the account's completion ceiling - a game sitting at
///     100% on an idled account is itself the giveaway. One figure, set in the settings, applied everywhere.
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
		int MinPercent = 0
	);

	//                          onboard: count, gap lo-hi, played gate | steady: gap lo-hi, played gate | rarity scale
	private static readonly Dictionary<uint, Profile> Profiles = new() {
		{ 400, new Profile(5, 8, 20, 10, 40, 90, 30, 0.5) },            // Portal - 15 achievements, ~3h. Quick easy cluster, challenge tail basically never.
		{ 286690, new Profile(5, 12, 30, 12, 40, 90, 25, 1.0) },        // Metro 2033 Redux - 49, ~7h story.
		{ 220, new Profile(6, 12, 30, 12, 40, 90, 25, 1.3) },           // Half-Life 2 - 69, ~13h, big collectible tail.
		{ 719070, new Profile(5, 12, 30, 12, 40, 90, 25, 1.0) },        // Project Warlock - ~40, ~8h retro FPS.
		{ 500, new Profile(9, 6, 18, 8, 45, 100, 30, 2.0) },            // Left 4 Dead - 73, co-op grind.
		{ 550, new Profile(10, 6, 18, 8, 45, 100, 30, 2.0) },           // Left 4 Dead 2 - 101, co-op grind.
		{ 440, new Profile(10, 6, 18, 8, 45, 100, 30, 2.0) },           // Team Fortress 2 - 520, a huge 5-19% middle tier and only 10 genuinely rare.
		{ 1938090, new Profile(3, 60, 180, 30, 150, 360, 50, 1.0) },    // Call of Duty - 157, every one of them under 10% globally. Playtime-gated hard; never the 0% tail.
		{ 1172470, new Profile(3, 20, 60, 15, 90, 220, 35, 1.0) },      // Apex Legends - 12, all >=10%. A real player has most of them.
		{ 578080, new Profile(4, 20, 60, 15, 90, 240, 35, 1.3) },       // PUBG - 37, battle-royale grind. The lone 0% is skipped automatically.
		{ 1085660, new Profile(4, 20, 60, 15, 90, 240, 35, 1.2) },      // Destiny 2 - 23, healthy spread.
		{ 1808500, new Profile(4, 15, 45, 12, 70, 200, 30, 1.0) },      // ARC Raiders - 50, 39 of them common.
		{ 1943950, new Profile(4, 15, 45, 12, 60, 180, 30, 1.0) }       // Muck - 34, healthy spread.
	};

	/// <summary>
	/// Anything not in the table. Conservative but not glacial.
	///
	/// The steady gaps are wall-clock minutes, paced so that active play earns roughly one an hour while the
	/// rarity floor and the played-time gate still hold the hard tail shut. An earlier version used gaps of
	/// eight to forty HOURS, which meant an all-night session earned nothing at all.
	/// </summary>
	private static readonly Profile Fallback = new(3, 20, 120, 25, 60, 150, 25, 1.0);

	private static Profile ProfileFor(uint app) => Profiles.TryGetValue(app, out Profile? p) ? p : Fallback;

	/// <summary>
	/// How much playtime stretches this game's rarity floor: the hand-tuned figure for the games in the table, and
	/// otherwise the game's real length - a three-hour game opens its rare tail far sooner than a hundred-hour one.
	/// </summary>
	private double ScaleFor(uint app, Profile prof) =>
		!Profiles.ContainsKey(app) && Bot.Cfg.AchievementRealLength && (Core.Playtime.TypicalHours(app) is { } typical)
			? Math.Clamp(typical / 9.0, 0.4, 4.0)
			: prof.RarityScale;

	/// <summary>
	/// Hours a real player needs for this one: "beat the game" most of a playthrough, chapter 3 of 10 about three
	/// tenths of it. Nothing extra for the rest - the rarity floor already paces those.
	/// </summary>
	private static bool StoryTimeAllows(Achievement a, IReadOnlyCollection<Achievement> all, double hours, double? typical) {
		if (typical is not { } length || (length <= 0)) {
			return true;
		}

		if (TraitsOf(a).Ending) {
			return hours >= length * 0.8;
		}

		foreach ((string family, int[] rungs) in TraitsOf(a).Ladders) {
			if (!Regex.IsMatch(family, @"\b(" + StoryParts + @")\s+#") || (rungs.Length == 0) || (rungs[^1] is <= 0 or >= AllRung)) {
				continue;
			}

			int top = all.SelectMany(static x => TraitsOf(x).Ladders)
				.Where(k => (k.Family == family) && (k.Rungs.Length > 0) && (k.Rungs[^1] is > 0 and < AllRung))
				.Select(static k => k.Rungs[^1]).DefaultIfEmpty(rungs[^1]).Max();

			if ((top > 1) && (hours < length * 0.8 * rungs[^1] / top)) {
				return false;
			}
		}

		return true;
	}

	/// <summary>How far through a game's achievements this account is, as last read from Steam; (0, 0) when unknown.</summary>
	public (int Unlocked, int Total) Progress(uint app) {
		lock (_gate) {
			return _games.TryGetValue(app, out GameState? g) && (g.Total > 0) ? (Math.Max(0, g.Unlocked), g.Total) : (0, 0);
		}
	}

	/// <summary>The rarity floor at its lowest, however many hours: nothing rarer than this is ever unlocked.</summary>
	private const int LowestFloor = 1;

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
		LowestFloor;          // never 0: a sub-1% achievement on an idled account is the giveaway

	private sealed class GameState {
		public long PlayedMins;         // our accumulated in-game minutes, which drive the rarity gate
		public long MinsAtLastUnlock;   // enforces the played-time gap
		public DateTime NextAllow;      // earliest wall-clock moment the next unlock may fire
		public int Unlocked = -1;       // last known unlocked count; -1 means unknown, so treat as onboarding
		public int Total;               // how many the game has at all, so progress reads as a fraction
		public int BurstLeft;           // mid-burst: this many more pop quickly, bypassing the played-time gate
		public Outcome Last;            // what the last real read of Steam concluded
		public int CappedAt;            // the ceiling in force when it stopped, so raising it can release the game
	}

	/// <summary>
	/// Why a game is or isn't earning, as of the last time Steam was actually read.
	///
	/// Without this every screen had one thing to say about a running game - "earning" - whether it had fifty
	/// left or none at all. A finished game, a game at the ceiling and a game with no achievements whatsoever all
	/// read "Earning in X", with a "next after" time that was only ever the back-off timer. It looked queued.
	/// </summary>
	/// <remarks>Numbered explicitly: these are written to the state file as numbers, and reordering the names
	/// must never quietly change what a saved game means.</remarks>
	public enum Outcome { Unknown = 0, Earning = 1, None = 2, Complete = 3, SteamOnly = 4, Capped = 5, NeedsHours = 6 }

	/// <summary>The outcome as it stands NOW - a game capped under a ceiling that has since been raised is not capped.</summary>
	private Outcome Current(GameState g) =>
		(g.Last == Outcome.Capped) && (Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100) > g.CappedAt) ? Outcome.Unknown : g.Last;

	private readonly Dictionary<uint, GameState> _games = [];
	private readonly Random _rng = new();
	private readonly Lock _gate = new();

	private DateTime _lastTick = DateTime.MinValue;
	private bool _loaded;
	private Said _status = new("off");
	private uint _grindReset;   // the app whose schedule we've already pulled forward for the current grind (0 = none)

	public override string Name => "achievements";
	public override string Status => Bot.Cfg.UnlockAchievements ? _status : "";

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				if (Bot.Cfg.UnlockAchievements) {
					await StepAsync(ct).ConfigureAwait(false);
				} else {
					_status = new Said("off");
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Warn(new Said("achievement pacer hiccup: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
			}

			if (!await Sleep(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>When each running game's current sitting may first unlock something (10-30 minutes in).</summary>
	private readonly Dictionary<uint, DateTime> _sittingSince = [];

	private async Task StepAsync(CancellationToken ct) {
		if (!_loaded) {
			_loaded = true;
			Load();
		}

		if (!Bot.IsOnline || Bot.Paused || Bot.PlayingBlocked) {
			_status = new Said("waiting");

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

		if (!Bot.Grinding) {
			_grindReset = 0;   // grind ended - a later grind may re-engage the schedule
		}

		List<uint> running = CurrentGames();

		if (running.Count == 0) {
			// "nothing being played" while a game is plainly running reads as broken. The main game is skipped
			// deliberately - never fully completing the one this account plays most is the whole point - so say
			// that instead of implying the account is idle.
			HumanMode? mode = BotManager.ModuleOf<HumanMode>(Bot);
			uint main = mode?.MainGameId ?? 0;

			_status = (mode is { Current: not HumanMode.Phase.Off }) && (mode.PlayingNow != 0) && (mode.PlayingNow == main)
				? new Said("leaving {0} alone - it's the main game", GameNames.Of(main))
				: new Said("nothing being played");

			return;
		}

		int ceilingNow = Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100);

		// Unlock times are public. A human-mode account that's asleep (a hunt or drops run into the night) keeps the
		// minutes but unlocks nothing until morning.
		bool mayUnlock = !Bot.Cfg.LegitMode || HumanMode.AwakeFor(Bot);

		// Each game's current sitting: an achievement doesn't pop a minute after launching, whatever was played before.
		foreach (uint gone in _sittingSince.Keys.Where(k => !running.Contains(k)).ToList()) {
			_sittingSince.Remove(gone);
		}

		foreach (uint app in running) {
			if (!_sittingSince.ContainsKey(app)) {
				_sittingSince[app] = now + Rng.Minutes(10, 30);   // the earliest this sitting may unlock anything
			}

			GameState g = StateFor(app);
			Profile prof = ProfileFor(app);

			lock (_gate) {
				g.PlayedMins++;

				// The ceiling was raised since this game stopped at it. A capped game backs off for most of a day,
				// so without this, raising the setting did nothing visible for up to 25 hours - which is exactly
				// what "broken" looks like from the outside.
				if ((g.Last == Outcome.Capped) && (ceilingNow > g.CappedAt)) {
					g.Last = Outcome.Unknown;
					g.NextAllow = now;
				}

				// A deliberate grind engages the achievement schedule NOW. Otherwise a spacing gap set during
				// ordinary play (NextAllow hours out) blocks the grind for its whole duration - it'd drop nothing.
				// Pulled forward exactly once when the grind begins; the played-time gate still applies, so the
				// first unlock isn't instant and the pace stays legit.
				if (Bot.Grinding && (Bot.GrindGame == app) && (_grindReset != app)) {
					_grindReset = app;

					// Soon, but not the second it starts - a grind's first unlock lands 8-30 minutes in.
					DateTime soon = now + Rng.Minutes(8, 30);

					if (g.NextAllow > soon) {
						g.NextAllow = soon;
					}
				}
			}

			if (!mayUnlock || (now < _sittingSince[app]) || !Due(g, prof)) {
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
	/// The main game is deliberately excluded during normal play (a grind still earns it), plus anything on the account's AchievementNeverGames list. This came across from
	/// the earlier plugin, where it was arrived at deliberately rather than by accident.
	/// </summary>
	private List<uint> CurrentGames() {
		List<uint> allowed = Bot.Cfg.AchievementGames;
		HumanMode? human = BotManager.ModuleOf<HumanMode>(Bot);
		List<uint> running = [];

		// A grind (or a hunter session, which is one) first. On a human account the grind has no game of human
		// mode's own - PlayingNow is 0 while it runs - so reading human mode alone credited nothing, and a grind
		// there never earned a single achievement. Only once Steam has actually been told it is running, so the
		// minutes credited are minutes really played.
		if (Bot.Grinding && Bot.PlayingApps.Contains(Bot.GrindGame)) {
			running.Add(Bot.GrindGame);
		} else if (human is { Current: not HumanMode.Phase.Off }) {
			uint playing = human.PlayingNow;

			// The main game used to be skipped outright, on the theory that the one game an account is known for
			// is the worst place to show a steady drip of unlocks. That reasoning ignored the obvious: the main
			// game is also where the hours actually are, so it is the one game whose achievements are least
			// surprising. Skipping it meant an account with a thousand hours in its headline game never earned a
			// single achievement there, which reads far stranger than earning them. Now a choice, defaulting to
			// earning them; the rarity floor and the ceiling still apply exactly as they do everywhere else.
			if ((playing != 0) && (Bot.Cfg.AchievementIncludeMainGame || (playing != human.MainGameId))) {
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

			// The played-time gate applies to EVERYTHING, including a grind: you cannot legitimately unlock an
			// achievement faster than you put the hours in, and a game with 0-1 hours on it must not dump a pile of
			// them. A grind just plays the game continuously so the hours (and the unlocks) come steadily. The only
			// bypass is a mid-burst (a cluster from one moment of play).
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
			lock (_gate) {
				g.Unlocked = 0;
				g.Total = 0;
				g.Last = Outcome.None;
			}

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
			g.Total = total;
		}

		// One ceiling, the one in the settings box.
		//
		// Every game used to roll its own out of a hardcoded per-game range - Team Fortress 2 drew from 18-28,
		// landed on 23, and that number was written into the state file for good. It could not be predicted,
		// set, or explained: the only honest thing the UI could say was "it stops at 23%", and the only answer
		// to "why 23" was a dice roll. A single figure the account holder chooses does the same job and can
		// actually be reasoned about. A grind ignores it, because that is the explicit "finish this" path.
		//
		// Only a grind someone typed. The hunter works through the library on its own, hundreds of games, and taking
		// every one of them to 100% is exactly the pattern the ceiling exists to prevent - it stops where the account
		// holder said to stop, like normal play.
		int ceiling = grind && !Bot.GrindIsBoost ? 100 : Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100);

		// Nothing left that a client may set. Either the game is finished, or everything still locked is awarded
		// by Steam itself (Counter-Strike 2's are, bar the one for launching it). Asked before the ceiling, so a
		// game in that state is described as what it is rather than as having hit a limit.
		if (!set.All.Any(a => !a.Unlocked && a.Settable && !IsSpecialGlobal(a))) {
			lock (_gate) {
				g.Last = already >= total ? Outcome.Complete : Outcome.SteamOnly;
			}

			if (grind && (already < total)) {
				Log.Info(new Said("{0}: achievements are server-side - nothing to grind", GameNames.Of(app)), Bot.Name);
			}

			Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));

			return false;
		}

		// Never below the onboarding cluster, or a short game with a big early burst (Portal onboards 5 of its
		// 15) would be cut off mid-cluster by a percentage that was never meant to apply that finely.
		int ceilingCount = Math.Min(total, Math.Max(prof.OnboardCount, total * ceiling / 100));

		if (already >= ceilingCount) {
			lock (_gate) {
				g.Last = Outcome.Capped;
				g.CappedAt = ceiling;
			}

			// Done with this game. Back off hard rather than re-reading Steam's stats every played minute.
			Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));

			return false;
		}

		// Steam's own total for the game, not just the minutes this tool has idled.
		//
		// The rarity floor asks one question: could a real player plausibly have reached this achievement by now?
		// Answering it from our own accrued minutes threw away every hour the account played before nocat.farm
		// ever saw it. An account with thousands of genuine hours in a game was treated as a beginner and had the
		// rare tail locked shut forever - the opposite of what the gate is for, and the reason a long-played
		// library would never fill in. Steam's figure already includes anything we idled, so it is simply the
		// better number; our own count stays as the throttle below, which is what actually paces the drip.
		double hours = Math.Max(g.PlayedMins, Bot.Library.MinutesOn(app)) / 60.0;
		bool onboarding = already < prof.OnboardCount;
		// A grind ignores the rarity floor: it works through everything settable, most-common first, rather
		// than only what the hours "justify". Normal play keeps the floor so it never pops a rare one early.
		// The rarity floor is the "possibility" gate and applies to a grind TOO: it opens with the hours in the
		// game, so a rare/grindy achievement (a low global %, e.g. "win 1000 rounds") can't be unlocked two hours
		// in - only what a real player could plausibly have reached by now, easiest-first. A grind just plays the
		// game continuously so the hours (and the floor) move faster; it does not skip ahead to the hard tail.
		int floor = Math.Max(Math.Max(1, prof.MinPercent), RarityFloorForHours(hours / ScaleFor(app, prof)));
		double? typical = Bot.Cfg.AchievementRealLength ? Core.Playtime.TypicalHours(app) : null;

		List<Achievement> eligible = set.All
			// Unknown rarity (Steam's global-percent endpoint was unreachable) counts as eligible rather than being
			// excluded - otherwise a missing fetch would silently stop the account unlocking anything at all.
			.Where(a => !a.Unlocked && a.Settable && !IsSpecialGlobal(a) && ((a.GlobalPercent ?? floor) >= floor)
				&& (RequiredPriorAchievements(a) <= already)
				&& !TierBlocked(a, set.All)
				&& !DifficultyBlocked(a, set.All)
				&& !StoryEndBlocked(a, set.All)
				&& !VariantBlocked(a, set.All)
				&& StoryTimeAllows(a, set.All, hours, typical)
				&& !MilestoneUnearned(a, set.All))
			.ToList();

		if (eligible.Count == 0) {
			lock (_gate) {
				g.BurstLeft = 0;
			}

			lock (_gate) {
				g.Last = Outcome.NeedsHours;
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
			Log.Debug(new Said("couldn't unlock \"{0}\" in {1} - {2}", pick.Display, GameNames.Of(app), message), Bot.Name);
			Back(g, TimeSpan.FromMinutes(_rng.Next(30, 90)));

			return false;
		}

		int nowUnlocked = already + 1;
		int gap;

		lock (_gate) {
			g.MinsAtLastUnlock = g.PlayedMins;
			g.Unlocked = nowUnlocked;
			g.Last = Outcome.Earning;

			// Cluster like a person: several within a couple of minutes as a level or campaign finishes, then
			// nothing for a long stretch. A steady one-every-N-minutes drip is the thing to avoid.
			if (grind) {
				// Active-play pace: what a real person mopping up a game's easy achievements looks like - a handful
				// an hour, not a dump. Mostly the configured gap apart (default ~12-24 min, so ~3-5/hr, never 20),
				// easiest-first, only among what's reachable for the hours in the game (the rarity floor above), and
				// still Paced() by the account's setting. But not a metronome: now and then a level or objective
				// pops two or three together, then a longer quiet - exactly how a person's history looks.
				int glo = Math.Max(1, Bot.Cfg.AchievementGrindGapMinMinutes);
				int ghi = Math.Max(glo, Bot.Cfg.AchievementGrindGapMaxMinutes);

				if (g.BurstLeft > 0) {
					g.BurstLeft--;
					gap = _rng.Next(1, 5);
				} else if ((eligible.Count > 1) && (_rng.Next(100) < 25)) {
					g.BurstLeft = _rng.Next(1, 3);
					gap = _rng.Next(1, 5);
				} else {
					gap = _rng.Next(glo, ghi + 1);
				}
			} else if (g.BurstLeft > 0) {
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

		Said rarity = pick.GlobalPercent is { } percent ? new Said(" ({0}% have it)", percent.ToString("0.#")) : default;
		Log.Reward(new Said("unlocked \"{0}\" in {1}{2} ({3}/{4})", pick.Display, GameNames.Of(app), rarity, nowUnlocked, total), Bot.Name, topic: Topic.Achievements);
		Remember(new Unlock(app, GameNames.Of(app), pick.Display, pick.GlobalPercent, DateTime.UtcNow, nowUnlocked, total));

		return true;
	}

	/// <summary>
	/// What the order rules need to know about one achievement, worked out from its wording once per achievement.
	/// Every rule compares every locked achievement against every other one, so without this a 500-achievement game
	/// ran the same few dozen regular expressions a quarter of a million times per look.
	/// </summary>
	private sealed record Traits(
		string Label,                                   // "halo 4", "me1": the game or mode a collection's achievement belongs to
		List<(string Family, int[] Rungs)> Ladders,     // see LadderKeys
		(string Stem, int Rank)? Difficulty,            // see DifficultyKey
		bool Ending,                                    // "finish the game", "the final mission"
		bool Challenge,                                 // ...with a challenge on top ("without dying", "in under 4 hours")
		bool PlainEnding,                               // ...with nothing harder asked at all, not even "on Hard"
		bool Numbered,                                  // one numbered step of the story: "Complete Chapter 3"
		bool Named,                                     // one named step of the story: "ME1: Complete Ilos"
		string Modes,                                   // co-op, multiplayer, DLC...: which part of the game it is about
		string Core,                                    // the description, plain, for "the same thing, but harder"
		bool Prestige,
		bool LevelUp
	);

	private static readonly ConditionalWeakTable<Achievement, Traits> TraitCache = new();

	private static Traits TraitsOf(Achievement a) => TraitCache.GetValue(a, static x => Work(x));

	private static Traits Work(Achievement a) {
		string desc = a.Description ?? "";
		string text = $"{a.Display} {desc}";
		(string label, string body) = SplitLabel(string.IsNullOrWhiteSpace(desc) ? a.Display : desc);
		string modes = string.Join(",", Regex.Matches(Normalize(text), ModeWords).Select(static m => m.Groups[1].Value.Replace("coop", "co-op", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal));
		List<(string Family, int[] Rungs)> ladders = LadderKeys(a);
		(string Stem, int Rank)? difficulty = DifficultyKey(a);
		bool ending = IsEnding(a);
		bool challenge = ending && Regex.IsMatch(Plain(body), Qualifiers);

		return new Traits(
			label,
			ladders,
			difficulty,
			ending,
			challenge,
			ending && !challenge && ((difficulty is null) || Regex.IsMatch(Normalize(body), @"\bany\b")),
			!ending && IsNumberedStoryPart(ladders),
			!ending && IsNamedStoryPart(a),
			modes,
			Plain(body),
			Regex.IsMatch(text, @"\bprestige\b", RegexOptions.IgnoreCase),
			Regex.IsMatch(Normalize(desc), @"\b(reach|reached|attain|attained|hit)\s+(the\s+)?(max(imum)?\s+)?(level|rank)\b") && !Regex.IsMatch(text, @"\bprestige\b", RegexOptions.IgnoreCase));
	}

	/// <summary>
	/// "Halo 4: Beat the par score on Dawn", "ME1: Complete Eden Prime", "Red Dead Online: Reach Rank 10" - a collection
	/// or a mode says which part of it an achievement belongs to in a label up front. Two achievements with different
	/// labels are different games: the Halo 4 one never waits for the Halo 2 one, and the 4 in "Halo 4" is a name, not
	/// a rung. Returns the label (lower case, "" when there is none) and the text after it.
	/// </summary>
	private static (string Label, string Body) SplitLabel(string text) {
		string s = (text ?? "").Trim();
		List<string> parts = [];

		while (Regex.Match(s, @"^([A-Za-z0-9][A-Za-z0-9 .'’&+\-]{0,30}?):\s+(?=\S)") is { Success: true } m) {
			string part = Regex.Replace(m.Groups[1].Value.ToLowerInvariant(), @"\s+", " ").Trim();

			// "Chapter IV: The Bridge" and "Complete Chapter 3: Into the Dark" are a step with its title, not a label -
			// and neither is anything that reads as a thing to do ("Kill 3 enemies: ...").
			if (Regex.IsMatch(Normalize(part), @"\b(" + StoryParts + @")\s+\d") || Regex.IsMatch(part, @"^(complete|completed|finish|beat|defeat|kill|get|win|reach|collect|find|earn|play|survive|clear|unlock|use)\b")) {
				break;
			}

			if (!parts.Contains(part)) {
				parts.Add(part);
			}

			s = s[m.Length..];
		}

		return (string.Join(":", parts), s);
	}

	/// <summary>Lower case, no punctuation, no "on any difficulty": what an achievement asks for, to compare two of them.</summary>
	private static string Plain(string text) {
		string s = Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}%+\s]", " ");
		s = Regex.Replace(s, @"\b(on|in|at)\s+any\s+(difficulty|mode)(\s+level)?\b", " ");

		return Regex.Replace(s, @"\s+", " ").Trim();
	}

	/// <summary>Which part of a game an achievement is about. A step of the co-op campaign is not a step of the story.</summary>
	private const string ModeWords = @"\b(multiplayer|online|co-op|coop|zombies|versus|pvp|arcade|horde|dlc|expansion|new game\+|ng\+|battlemode|snapmap|score attack|spartan ops)(?![\w])";

	/// <summary>Asked on top of the plain thing: "without dying", "in under 2 hours", "using only kicks", "solo", "on Hard".</summary>
	private const string Qualifiers = @"\b(without|under|within|less than|fewer than|only|exactly|never|no|solo|alone|yourself|single|one life|deathless|speedrun|mode)\b";

	/// <summary>Words that start "the same thing, but harder" after a plain description: "...without taking a hit", "...on Veteran".</summary>
	private const string HarderWords = @"^(without|in|under|within|with|while|using|only|on|by|solo|firing|before|after|at|as|no|never|exactly|twice|again|from|having|taking|wearing|alone)\b";

	/// <summary>
	/// Is this one rung of a ladder whose lower rungs are still locked?
	///
	/// Games number their tiers, and the numbering IS the dependency: "Sniper Milestone 3" after 1 and 2, "Level
	/// 50" after "Level 10", "Chapter 4" after "Chapter 3" - and just as often the numbers are only in the
	/// description: "Sharpshooter - get 5 kills" before "Marksman - get 10 kills". Steam publishes no dependency
	/// graph at all, so this is inferred for every game the same way: two achievements whose name, or whose
	/// description, reads the same once the numbers are taken out are one ladder, and a rung is held until every
	/// lower rung is done. "Chapter One", "Act II" and "the third mission" count as numbers too, "all" and "every"
	/// are the top rung ("Find all Collectibles" after "Find 25 Collectibles"), and a time limit counts down ("in
	/// under 5 hours" after "in under 10 hours").
	///
	/// Rarity ordering already gets this right most of the time (a later tier is rarer, and the easiest is always
	/// taken first), but not always: tiers can share a rarity, and a profile showing "10 kills" with "5 kills"
	/// missing is the exact shape of a faked achievement.
	/// </summary>
	private static bool TierBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		List<(string Family, int[] Rungs)> mine = TraitsOf(a).Ladders;

		if (mine.Count == 0) {
			return false;
		}

		foreach (Achievement other in all) {
			if (other.Unlocked || (other.Name == a.Name)) {
				continue;
			}

			List<(string Family, int[] Rungs)> theirs = TraitsOf(other).Ladders;

			// A lower rung of the same ladder, still locked - so this one is not next. Unless the two read as each
			// other's lower rung at once ("Speedrun 1 - under 10 hours" and "Speedrun 2 - under 5 hours" before time
			// limits counted down): a contradiction is no evidence either way, and honouring it held both for ever.
			if (Lower(theirs, mine) && !Lower(mine, theirs)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>Any ladder in <paramref name="lower"/> a lower rung of the same ladder in <paramref name="higher"/>.</summary>
	private static bool Lower(List<(string Family, int[] Rungs)> lower, List<(string Family, int[] Rungs)> higher) {
		foreach ((string family, int[] rungs) in lower) {
			foreach ((string otherFamily, int[] otherRungs) in higher) {
				if (string.Equals(family, otherFamily, StringComparison.Ordinal) && Below(rungs, otherRungs)) {
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>Every number in <paramref name="lower"/> at most its partner in <paramref name="higher"/>, and one smaller.</summary>
	private static bool Below(int[] lower, int[] higher) {
		if (lower.Length != higher.Length) {
			return false;
		}

		bool smaller = false;

		for (int i = 0; i < lower.Length; i++) {
			if (lower[i] > higher[i]) {
				return false;
			}

			smaller |= lower[i] < higher[i];
		}

		return smaller;
	}

	/// <summary>"all" and "every" as a number: more than any count a game asks for.</summary>
	private const int AllRung = 9_999_999;

	/// <summary>
	/// The ladders an achievement could be a rung of: its name and its description, each with the numbers taken
	/// out (the family) and the numbers themselves (the rungs). None when there is no number to order by, which
	/// is most achievements - those aren't part of any ladder we can see.
	/// </summary>
	private static List<(string Family, int[] Rungs)> LadderKeys(Achievement a) {
		List<(string, int[])> keys = [];

		foreach ((string raw, bool isName) in new[] { (a.Display, true), (a.Description, false) }) {
			if (string.IsNullOrWhiteSpace(raw)) {
				continue;
			}

			(string label, string body) = SplitLabel(raw);
			string normal = Normalize(body);

			// A label's own number is its name: "every Halo 4 level" is Halo 4's, not rung 4 of anything.
			foreach (string part in label.Split(':').Where(static p => Regex.IsMatch(p, @"\d"))) {
				normal = Regex.Replace(normal, @"\b" + Regex.Escape(part) + @"\b", "@");
			}

			// "Chapter IV: The Bridge" is rung 4 of the chapters, whatever this one is called.
			normal = Regex.Replace(normal, @"^(.*?\b(" + StoryParts + @")\s+\d{1,4})\s*[:\-–—]\s+\S.*$", "$1");

			// A name ending in a numeral is a tier: "Medal of Exploration II", "Insanity III".
			if (isName) {
				normal = Regex.Replace(normal, @"\s([ivx]{1,4})[\s.!]*$", static m => Roman(m.Groups[1].Value) is > 0 and var r ? $" {r}" : m.Value);
			}

			// A limit counts down: "in under 5 hours" is harder than "in under 10 hours", so its rung is the negative.
			normal = Regex.Replace(normal, @"\b(under|within|less than|fewer than|at most|no more than)\s+(?=\d)", "$1 ~");
			normal = Regex.Replace(normal, @"(?<![\w~])(\d{1,7})((?:\s+[a-z]+){0,3}\s+or\s+(?:less|fewer))\b", "~$1$2");

			// "Find all Collectibles" is the top rung of "Find 10 Collectibles" and "Find 25 Collectibles".
			normal = Regex.Replace(normal, @"\b(all|every)\b(?!\s+(of\s+)?(the\s+)?~?\d)", AllRung.ToString(CultureInfo.InvariantCulture));

			// Only a number standing on its own: "ME1", "E1M1", "The D20" and "2Fort" are names.
			MatchCollection numbers = Regex.Matches(normal, NumberPattern);

			if ((numbers.Count == 0) || (numbers.Count > 4)) {
				continue;
			}

			string rest = Regex.Replace(Regex.Replace(normal, NumberPattern, "#"), @"[^\p{L}\p{N}#%@\s]", " ");
			string family = Regex.Replace(rest, @"\s+", " ").Trim();

			// A number with hardly a word around it ("100", "#1") says nothing about what it belongs to.
			if (family.Replace("#", "").Replace("@", "").Trim().Length < 4) {
				continue;
			}

			int[] rungs = [.. numbers.Select(static m => {
				string v = m.Value.TrimEnd('s', 't', 'n', 'd', 'r', 'h');
				bool down = v.StartsWith('~');
				return int.TryParse(down ? v[1..] : v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? (down ? -n : n) : int.MaxValue;
			})];
			keys.Add((label.Length > 0 ? $"{label}|{family}" : family, rungs));
		}

		return keys;
	}

	private const string NumberPattern = @"(?<![\p{L}\d~])~?\d{1,7}(st|nd|rd|th)?(?![\p{L}\d])";

	private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal) {
		["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
		["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
		["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["fifty"] = 50, ["hundred"] = 100,
		["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8,
		["ninth"] = 9, ["tenth"] = 10
	};

	/// <summary>Story parts: what a "beat the game" achievement comes after, and what a roman numeral follows.</summary>
	private const string StoryParts = "chapter|act|part|episode|mission|stage|book|quest|world|area|zone|level|season";

	/// <summary>Story parts that mean one whatever the sentence: "Kill 5 enemies in Chapter 3" is chapter 3 reached.</summary>
	private const string SureStoryParts = "chapter|act|episode|mission|book";

	/// <summary>Lower case, "1,000" and "10k" as plain numbers, "one"/"third"/"act ii" as digits.</summary>
	private static string Normalize(string text) {
		string s = text.ToLowerInvariant();
		s = Regex.Replace(s, @"(?<=\d),(?=\d{3}\b)", "");
		s = Regex.Replace(s, @"\b(\d{1,4})k\b", static m => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000).ToString(CultureInfo.InvariantCulture));
		s = Regex.Replace(s, @"\b[a-z]+\b", static m => NumberWords.TryGetValue(m.Value, out int n) ? n.ToString(CultureInfo.InvariantCulture) : m.Value);
		s = Regex.Replace(s, @"\b(" + StoryParts + @")\s+([ivx]{1,5})\b", static m => Roman(m.Groups[2].Value) is > 0 and var r ? $"{m.Groups[1].Value} {r}" : m.Value);

		return s;
	}

	private static int Roman(string r) {
		int total = 0, last = 0;

		for (int i = r.Length - 1; i >= 0; i--) {
			int v = r[i] switch { 'i' => 1, 'v' => 5, 'x' => 10, _ => 0 };

			if (v == 0) {
				return 0;
			}

			total += v < last ? -v : v;
			last = Math.Max(last, v);
		}

		return total;
	}

	/// <summary>Difficulty names, and medal grades, easiest first. Names shared by several games' scales rank where most put them.</summary>
	private static readonly Dictionary<string, int> Difficulties = new(StringComparer.Ordinal) {
		["easy"] = 1, ["casual"] = 1, ["recruit"] = 1, ["normal"] = 2, ["medium"] = 2, ["standard"] = 2, ["regular"] = 2,
		["hard"] = 3, ["hardened"] = 3, ["heroic"] = 3, ["veteran"] = 4, ["expert"] = 4, ["hardcore"] = 4, ["professional"] = 4,
		["master"] = 5, ["insane"] = 5, ["extreme"] = 5, ["madhouse"] = 5, ["realism"] = 5, ["realistic"] = 5,
		["nightmare"] = 6, ["legendary"] = 6, ["hell"] = 6, ["inferno"] = 7, ["torment"] = 7,
		["bronze"] = 1, ["silver"] = 2, ["gold"] = 3, ["platinum"] = 4
	};

	private static readonly string DifficultyPattern = @"\b(" + string.Join("|", Difficulties.Keys) + @")\b";

	/// <summary>
	/// "Beat it on Hard" while "beat it on Normal" is still locked: the same achievement at a lower difficulty
	/// comes first - worded identically apart from the difficulty. Medals the same: silver after bronze.
	/// </summary>
	private static bool DifficultyBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		if (TraitsOf(a).Difficulty is not { } mine) {
			return false;
		}

		foreach (Achievement other in all) {
			if (!other.Unlocked && (other.Name != a.Name) && (TraitsOf(other).Difficulty is { } theirs)
				&& (theirs.Stem == mine.Stem) && (theirs.Rank < mine.Rank)) {
				return true;
			}
		}

		return false;
	}

	private static (string Stem, int Rank)? DifficultyKey(Achievement a) {
		foreach (string text in new[] { a.Description, a.Display }) {
			string normal = Regex.Replace(Regex.Replace(Normalize(text ?? ""), @"[^\p{L}\p{N}%:\s]", " "), @"\s+", " ").Trim();
			Match m = Regex.Match(normal, DifficultyPattern);

			if (m.Success) {
				return (normal[..m.Index] + "~" + normal[(m.Index + m.Length)..], Difficulties[m.Value]);
			}
		}

		return null;
	}

	/// <summary>
	/// "Beat the game", "finish the story", the final chapter, every level: never before the chapters, missions and
	/// acts that lead up to it. Nobody plays the last mission first.
	/// </summary>
	private static bool IsEnding(Achievement a) {
		string text = Normalize($"{a.Display} {a.Description}");

		// Not "play a complete game on 2Fort" (an adjective), nor "complete the game intro" (the opposite of an ending).
		return Regex.IsMatch(text, @"(?<!\b(a|an)\s+)\b(complete|completed|finish|finished|beat|beaten|clear|cleared|conquer|conquered)\s+(the\s+)?(entire\s+|whole\s+|main\s+|base\s+)?(game|story|campaign|adventure|storyline|main quest)(?!['’]s|\s+(intro|introduction|tutorial|prologue|demo))\b")
			|| Regex.IsMatch(text, @"\b(roll|see|watch)\s+the\s+(credits|ending)\b")
			|| Regex.IsMatch(text, @"\b(reach|reached|witness|witnessed|see|saw|complete|completed)\s+the\s+epilogue\b")
			|| Regex.IsMatch(text, @"\b(complete|completed|finish|finished|beat|clear)\s+(all|every)\s+(the\s+)?(?!(side|optional|secret|bonus|extra|daily|weekly|challenge|co-op|coop)\b)([a-z]+\s+)?(chapters?|missions?|levels?|acts?|episodes?|stages?)\b(?!\s+(challenges?|collectibles?|secrets?))")
			|| Regex.IsMatch(text, @"\b(final|last)\s+(" + StoryParts + @"|boss)\b");
	}

	/// <summary>
	/// "Complete Chapter 3", "Mission 7", "Chapter IV: The Bridge" - one numbered step of the story. A chapter or a
	/// mission is one whatever the sentence around it; a level, a stage or a zone only when it is being completed
	/// ("Complete Level 3"), because "Reach Level 5 in Multiplayer" and "Upgrade any weapon to level 10" are a
	/// player's level, and they are not part of the story at all.
	/// </summary>
	private static bool IsNumberedStoryPart(List<(string Family, int[] Rungs)> ladders) {
		foreach ((string family, _) in ladders) {
			string f = family[(family.IndexOf('|') + 1)..];

			if (Regex.IsMatch(f, @"\b(" + SureStoryParts + @")\s+#")
				|| Regex.IsMatch(f, @"^(" + StoryParts + @")\s+#")
				|| Regex.IsMatch(f, @"\b(complete|completed|finish|finished|beat|beaten|clear|cleared|pass|passed|survive|survived)\s+(the\s+)?(" + StoryParts + @")\s+#")) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// A real step before an ending is at least roughly as common as the ending: everyone who has the ending has the
	/// step. One a quarter as common or rarer is some other part of the game - a zombies act, a co-op zone, a DLC -
	/// and holding the story's ending behind it could only ever look stranger than not.
	///
	/// And never one under the lowest rarity floor: this account will never earn it, so whatever waited on it would
	/// wait for ever ("Complete Act III in MWZ", 0.7%, held Call of Duty's "Complete the campaign" at 1.9% for good).
	/// These rules are read off the wording, so a step that rare is far likelier some other mode than the story.
	/// </summary>
	private static bool CouldComeFirst(Achievement step, Achievement after) =>
		(step.GlobalPercent is not { } s) || ((s >= LowestFloor) && ((after.GlobalPercent is not { } e) || (s * 4 >= e)));

	/// <summary>A step of the same game and the same part of it: same label, and no mode (co-op, DLC...) the other doesn't have.</summary>
	private static bool SameStory(Traits step, Traits after) =>
		(step.Label == after.Label) && step.Modes.Split(',', StringSplitOptions.RemoveEmptyEntries).All(m => after.Modes.Split(',').Contains(m));

	private static bool StoryEndBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		Traits me = TraitsOf(a);

		if (!me.Ending) {
			return false;
		}

		foreach (Achievement other in all) {
			if (other.Unlocked || (other.Name == a.Name)) {
				continue;
			}

			Traits them = TraitsOf(other);

			// A still-locked step of this story - numbered ("complete chapter 3", "Mission 7") or named ("Complete Blood
			// Feud in Campaign", "ME1: Complete Ilos") - comes first. Another ending ("the final boss in World 2") is never
			// a step: two of them held each other for ever. A named step only holds the game finished, not a challenge run
			// of it ("Complete the game within 4 hours"): a name is a looser clue than a number - it's as likely a DLC
			// chapter as the story's own - and the run already waits for the plain finish (VariantBlocked).
			if ((them.Numbered || (them.Named && !me.Challenge)) && SameStory(them, me) && CouldComeFirst(other, a)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// "Complete Blood Feud in Campaign", "ME1: Complete Eden Prime", "Complete the Casino" - one named step of the story,
	/// not the whole of it (that's an ending), and not a challenge ("Complete Frontline without killing anyone").
	/// </summary>
	internal static bool IsNamedStoryPart(Achievement a) {
		string text = $"{a.Display} {a.Description}".ToLowerInvariant();

		// Not "Complete all Safehouse Puzzles in Campaign": all of something is a collection, not a step.
		if (Regex.IsMatch(text, @"\b(complete|finish|beat|clear|survive)\s+(?!(the\s+)?(entire\s+|whole\s+|main\s+)?(game|campaign|story)\b)(?!(all|every)\b).{2,60}?\s+(in|on)\s+(the\s+)?(campaign|story(\s+mode)?)\b")) {
			return true;
		}

		// The description is the sentence: a verb, then a Name - capitalised or quoted, since that's what makes it a
		// place in the game rather than a thing done ("Complete a mission discovered by scanning" is not a step).
		(_, string body) = SplitLabel(a.Description ?? "");
		Match m = Regex.Match(body, @"^(?i:complete|completed|finish|finished|beat|beaten|clear|cleared|survive|survived)\s+(?i:the\s+)?(?<name>[""'‘“][^""'’”]{2,40}[""'’”]|[A-Z][\w'’\-]*(?:\s+(?:of|the|a|an|and|&|[A-Z0-9][\w'’\-]*))*)(?<rest>.*)$");

		if (!m.Success) {
			return false;
		}

		string name = m.Groups["name"].Value.ToLowerInvariant();
		string rest = Plain(m.Groups["rest"].Value);

		return !Regex.IsMatch(name, @"\b(mode|difficulty|challenges?|trials?|tutorials?|training|contracts?|bount(y|ies)|side|optional|trade|gauntlet|race|collection|arcade|horde|playlists?|puzzles?|tombs?|benchmarks?|course|dlc|expansion|survival|new game)\b" + "|" + DifficultyPattern)
			&& Regex.IsMatch(rest, @"^(?:(?:level|mission|chapter|stage|quest|area|campaign|story|act|episode|in|the)\b\s*)*$");
	}

	/// <summary>
	/// The same thing again, but harder, while the plain one is still locked:
	///
	///   • "Contain the Citadel core without killing any stalkers" after "Contain the Citadel core"; "Complete the Game
	///     in Under 4 Hours" after "Complete the Game"; "Halo 2: Complete Delta Halo without entering a vehicle" after
	///     "Halo 2: Complete Delta Halo". The harder one is the plain one's words with more asked on the end.
	///   • A DLC's ending after the game's own: "Finish the Whistleblower DLC" after "Finish the game".
	///   • Prestige after the level it takes: "Enter Prestige" after "Reach Level 55".
	/// </summary>
	private static bool VariantBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		Traits me = TraitsOf(a);
		bool dlcEnd = (me.Ending || me.Named || Regex.IsMatch(me.Core, @"^(complete|completed|finish|finished|beat|beaten)\b")) && Regex.IsMatch(me.Modes, @"\b(dlc|expansion)\b");

		foreach (Achievement other in all) {
			if (other.Unlocked || (other.Name == a.Name) || !CouldComeFirst(other, a)) {
				continue;
			}

			Traits them = TraitsOf(other);

			// Not when the plain one is "all" of something: "Discover all named locations in The Highlands" is a part of
			// "Discover all named locations", not a harder version of it.
			if ((them.Label == me.Label) && (them.Core.Length >= 12) && (them.Core.Count(static c => c == ' ') >= 2) && me.Core.StartsWith(them.Core + " ", StringComparison.Ordinal)
				&& Regex.IsMatch(me.Core[(them.Core.Length + 1)..], HarderWords) && !Regex.IsMatch(them.Core, @"\b(all|every|each)\b")) {
				return true;
			}

			if (dlcEnd && them.PlainEnding && (them.Label == me.Label) && !Regex.IsMatch(them.Modes, @"\b(dlc|expansion)\b")) {
				return true;
			}

			if (me.Prestige && them.LevelUp && SameStory(them, me)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// How many OTHER achievements a milestone/meta achievement needs first (e.g. TF2's "Achieve 17 of the
	/// achievements in the Sniper pack" -> 17). Unlocking one before its prerequisites is impossible for a real
	/// player, so the caller holds it until the account has at least this many unlocked in the game. We only know
	/// the COUNT, not which ones, so "N unlocked total" is the safe necessary condition.
	/// </summary>
	private static int RequiredPriorAchievements(Achievement a) {
		string text = $"{a.Display} {a.Description}".ToLowerInvariant();

		Match m = Regex.Match(text, @"\b(?:achieve|complete|earn|unlock|obtain|collect)\s+(\d{1,3})\b[^.!?]*achievement");

		if (!m.Success) {
			m = Regex.Match(text, @"\b(\d{1,3})\s+of\s+the\s+achievements\b");
		}

		return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
	}

	/// <summary>
	/// A milestone and the group of achievements it is a milestone OF, read off the API name.
	///
	/// "TF_SNIPER_ACHIEVE_PROGRESS2" is not a thing you earn by playing - it is awarded for having eleven of the
	/// other TF_SNIPER_* achievements. The display name gives no clue about that; the API name does, and the
	/// prefix is the group. Returns false for anything that isn't a milestone.
	/// </summary>
	private static bool MilestoneOf(Achievement a, out string family, out int rung) {
		family = "";
		rung = 0;

		// The two shapes Valve and most others use: a PROGRESS/MILESTONE suffix, or a plain trailing number on a
		// name whose display says "Milestone".
		Match m = Regex.Match(a.Name, @"^(?<base>.+?)_?(?:ACHIEVE_)?(?:PROGRESS|MILESTONE)_?(?<n>\d{1,2})$", RegexOptions.IgnoreCase);

		if (!m.Success) {
			return false;
		}

		rung = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
		family = m.Groups["base"].Value.TrimEnd('_');

		return (rung > 0) && (family.Length >= 3);
	}

	/// <summary>
	/// Is this a milestone whose own group hasn't been earned yet?
	///
	/// This is the one dependency Steam really does have and really does publish, just not as a graph: a
	/// milestone is granted for having N of its siblings. Unlocking "Sniper Milestone 2" on a profile showing
	/// three Sniper achievements is not a rare-achievement problem, it is an impossible one - no amount of
	/// playing produces it, so it reads as written rather than earned no matter how well paced everything else
	/// is. The count comes from the achievement's own description where it states one ("achieve 11 of the
	/// Sniper achievements"), and otherwise from spreading the group evenly across its rungs, which errs toward
	/// demanding more rather than fewer.
	/// </summary>
	private static bool MilestoneUnearned(Achievement a, IReadOnlyCollection<Achievement> all) {
		if (!MilestoneOf(a, out string family, out int rung)) {
			return false;
		}

		List<Achievement> siblings = [];
		int topRung = rung;

		foreach (Achievement other in all) {
			if (other.Name == a.Name) {
				continue;
			}

			if (MilestoneOf(other, out string otherFamily, out int otherRung)) {
				// A higher rung of the same ladder tells us how many steps the ladder has.
				if (string.Equals(family, otherFamily, StringComparison.OrdinalIgnoreCase)) {
					topRung = Math.Max(topRung, otherRung);
				}

				continue;   // milestones don't count toward each other
			}

			if (other.Name.StartsWith(family + "_", StringComparison.OrdinalIgnoreCase)) {
				siblings.Add(other);
			}
		}

		if (siblings.Count == 0) {
			return false;   // nothing recognisable to require - don't invent a rule
		}

		int stated = RequiredPriorAchievements(a);
		int needed = stated > 0
			? Math.Min(stated, siblings.Count)
			: Math.Max(1, (int) Math.Ceiling((double) siblings.Count / (topRung + 1) * rung));

		return siblings.Count(static s => s.Unlocked) < needed;
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

	/// <summary>
	/// Nothing more this account will earn in <paramref name="app"/> right now: finished, up to the ceiling, or only
	/// Steam-awarded achievements left. The hunter skips such a game rather than spend a sitting earning nothing.
	/// </summary>
	public bool NothingLeft(uint app) {
		lock (_gate) {
			return _games.TryGetValue(app, out GameState? g) && (Current(g) is Outcome.Complete or Outcome.SteamOnly or Outcome.Capped);
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

	/// <summary>An achievement this account earned, newest first. Kept in memory - a session's worth is enough.</summary>
	public sealed record Unlock(uint App, string Game, string Name, double? Percent, DateTime When, int Unlocked, int Total);

	private readonly List<Unlock> _recent = [];

	/// <summary>The last few achievements earned, newest first.</summary>
	public IReadOnlyList<Unlock> Recent {
		get {
			lock (_recent) {
				return [.. _recent];
			}
		}
	}

	private void Remember(Unlock unlock) {
		lock (_recent) {
			_recent.Insert(0, unlock);

			if (_recent.Count > 20) {
				_recent.RemoveRange(20, _recent.Count - 20);
			}
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
		int Total,
		DateTime NextAllow,
		bool Blocked,
		string Why,
		bool Running,
		string State
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

		// Nothing is earned in a game that isn't running, whatever its timers say.
		HashSet<uint> live = [.. CurrentGames()];

		lock (_gate) {
			return _games
				.OrderByDescending(static kv => kv.Value.PlayedMins)
				.Select(kv => {
					Profile prof = ProfileFor(kv.Key);
					double eff = Math.Max(kv.Value.PlayedMins, Bot.Library.MinutesOn(kv.Key)) / 60.0 / ScaleFor(kv.Key, prof);
					int floor = Math.Max(Math.Max(1, prof.MinPercent), RarityFloorForHours(eff));

					string why =
						never.Contains(kv.Key) ? "on your never list" :
						(main != 0) && (kv.Key == main) && !Bot.Cfg.AchievementIncludeMainGame ? "the main game - left alone" :
						(allowed.Count > 0) && !allowed.Contains(kv.Key) ? "not on your allow list" :
						floor > 100 ? "not enough hours yet" :
						"";

					return new Row(
						kv.Key,
						GameNames.Of(kv.Key),
						(int) kv.Value.PlayedMins,
						eff,
						Math.Min(floor, 100),
						Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100),
						kv.Value.Unlocked,
						kv.Value.Total,
						kv.Value.NextAllow,
						why.Length > 0,
						why,
						live.Contains(kv.Key),
						Current(kv.Value).ToString());
				})
				.ToList();
		}
	}

	private Said Describe() {
		lock (_gate) {
			if (_games.Count == 0) {
				return new Said("watching");
			}

			// Prefer whatever is actually running. Falling straight to the most-played game meant an account
			// idling one thing reported its progress on a different one entirely, which reads as a live plan
			// rather than the standings it actually is.
			List<uint> running = CurrentGames();

			(uint app, GameState g) = _games
				.OrderByDescending(kv => running.Contains(kv.Key))
				.ThenByDescending(static kv => kv.Value.PlayedMins)
				.First();

			double hours = Math.Max(g.PlayedMins, Bot.Library.MinutesOn(app)) / 60.0;

			// A finished, capped or achievement-less game has no "next". Its NextAllow is only the back-off timer,
			// and printing it as "next after 21:38" dressed a game that will never earn again as one that is queued.
			switch (Current(g)) {
				case Outcome.None:
					return new Said("{0} has no achievements", GameNames.Of(app));
				case Outcome.Complete:
					return new Said("{0}: every achievement done ({1}/{2})", GameNames.Of(app), g.Unlocked, g.Total);
				case Outcome.SteamOnly:
					return new Said("{0}: {1}/{2} - the rest can only be awarded by Steam", GameNames.Of(app), g.Unlocked, g.Total);
				case Outcome.Capped:
					return new Said("{0}: {1}/{2} - stopped at your {3}% ceiling", GameNames.Of(app), g.Unlocked, g.Total, g.CappedAt);
				case Outcome.NeedsHours:
					return new Said("{0}: {1}h in - the next ones need more hours in it", GameNames.Of(app), hours.ToString("0.#"));
			}

			Profile prof = ProfileFor(app);
			bool onboarding = (g.Unlocked < 0) || (g.Unlocked < prof.OnboardCount);
			int playedGate = onboarding ? prof.OnboardPlayedMins : prof.SteadyPlayedMins;
			long shortBy = playedGate - (g.PlayedMins - g.MinsAtLastUnlock);

			// Report the gate that is actually holding it up. "next after <time>" while the spacing had long
			// since elapsed and the real hold-up was in-game minutes gave no way to tell waiting from stuck.
			DateTime next = g.NextAllow;
			Said when =
				DateTime.UtcNow < g.NextAllow ? new Said("next after {0}", (Func<string>) (() => Fmt.Clock(next)))
				: (g.BurstLeft <= 0) && (shortBy > 0) ? new Said("next after {0} more play", Fmt.Hm((int) shortBy))
				: new Said("next one due");

			// `when` is passed through as a Said, not as text. Loc.T calls ToString on its arguments when it
			// renders, so the inner sentence is translated at the same instant as the outer one - bake it to a
			// string here and it would be frozen in whatever language was selected when the status was written.
			return new Said("{0}: {1}h in, {2}", GameNames.Of(app), hours.ToString("0.#"), when);
		}
	}

	// ── remembering where it got to ─────────────────────────────────────────
	private sealed class Saved {
		public uint App { get; set; }
		public long PlayedMins { get; set; }
		public long MinsAtLastUnlock { get; set; }
		public DateTime NextAllow { get; set; }
		public int Unlocked { get; set; } = -1;
		public int Total { get; set; }

		// Absent from files written before outcomes existed; Unknown until the game is next read, apart from
		// a finished game, which the counts alone can prove.
		public Outcome Last { get; set; }
		public int CappedAt { get; set; }
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
						Unlocked = s.Unlocked,
						Total = s.Total,
						Last = s.Last,
						CappedAt = s.CappedAt
					};

					// A file written before outcomes existed says nothing about them, and a finished or capped game
					// is only read again every 8-25 hours - so until then it would have been shown as "earning".
					// Both can be worked out from the counts already saved.
					GameState g = _games[s.App];

					if ((g.Last == Outcome.Unknown) && (g.Total > 0) && (g.Unlocked >= 0)) {
						int ceiling = Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100);
						int ceilingCount = Math.Min(g.Total, Math.Max(ProfileFor(s.App).OnboardCount, g.Total * ceiling / 100));

						if (g.Unlocked >= g.Total) {
							g.Last = Outcome.Complete;
						} else if (g.Unlocked >= ceilingCount) {
							g.Last = Outcome.Capped;
							g.CappedAt = ceiling;
						}
					}
				}
			}

			Log.Debug(new Said("achievement pacing restored for {0} game(s)", saved.Count), Bot.Name);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the achievement state: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
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
					};

					taken++;
				}
			}

			if (taken > 0) {
				Log.Good(new Said("carried on ASF's achievement pacing for {0} game(s)", taken), Bot.Name);
				Save();
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't import the ArchiSteamFarm achievement state: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
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
					Unlocked = kv.Value.Unlocked,
					Total = kv.Value.Total,
					Last = kv.Value.Last,
					CappedAt = kv.Value.CappedAt
				}).ToList();
			}

			string path = PathFor(Bot.Name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the achievement state: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
		}
	}
}
