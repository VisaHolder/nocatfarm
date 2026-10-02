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

	/// <summary>
	/// How far through a game's achievements this account is, as last read from Steam; (0, 0) when unknown. The total
	/// is what it can reach: the ones from DLC it doesn't own are left out, or the hunt's "far enough" could never be
	/// reached in Call of Duty on an account with neither Modern Warfare - 63 of its 157 are theirs.
	/// </summary>
	public (int Unlocked, int Total) Progress(uint app) {
		lock (_gate) {
			return _games.TryGetValue(app, out GameState? g) && (g.Total > 0) ? (Math.Max(0, g.Unlocked), Reachable(g)) : (0, 0);
		}
	}

	/// <summary>How many of a game's achievements this account could ever have - all of them, less what's held for DLC.</summary>
	private static int Reachable(GameState g) => g.Reachable > 0 ? Math.Min(g.Reachable, g.Total) : g.Total;

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
		public uint App;                // which game this is
		public long PlayedMins;        // our accumulated in-game minutes, which drive the rarity gate
		public long MinsAtLastUnlock;   // enforces the played-time gap
		public DateTime NextAllow;      // earliest wall-clock moment the next unlock may fire
		public int Unlocked = -1;       // last known unlocked count; -1 means unknown, so treat as onboarding
		public int Total;               // how many the game has at all, so progress reads as a fraction
		public int BurstLeft;           // mid-burst: this many more pop quickly, bypassing the played-time gate
		public Outcome Last;            // what the last real read of Steam concluded
		public int CappedAt;            // the ceiling in force when it stopped, so raising it can release the game
		public int Reachable;           // Total less the locked ones from DLC this account doesn't own; 0 = not worked out
		public int DlcHeld;             // locked ones from DLC this account doesn't own - never unlocked
		public int DlcSaid = -1;        // the DlcHeld last written to the log, so it is said once, not every look
		public bool Unmapped;           // left alone ('dlc leave'), and can't tell which achievements come with a DLC it doesn't own: all but its owned DLC's left alone
		public bool Unclear;            // has add-ons it doesn't own whose achievements Steam doesn't place - earns anyway, or left alone
		public long Licences;           // the account's licence stamp when the DLC it owns was last read for this game
		public List<uint> DlcOwned = []; // which of the game's DLC it counted as owned then (with the unplaceable ones, unless left alone) - a change releases DlcOnly, DlcUnmapped and Capped
		public long MapStamp;           // which build of the game's DLC map that was read from (DlcAchievements.Stamp); 0 = not known
		public long HoldKey;            // what that map said about every achievement then (DlcAchievements.HoldKey) - a change releases the game too
		public string? DlcKey;          // which of the game's DLC ids the account has for good (DlcAchievements.LicenceKey) - what 'dlc leave' is kept with; null = not read
		public int Multiplayer;         // locked multiplayer ones, skipped (AchievementSkipMultiplayer)
		public int Counters;            // locked ones whose counter in the game isn't there yet - not counted as reachable
		public int Rules;               // RulesOf the account when it was last read - the multiplayer skip changes what it can reach
		public Dictionary<string, DateTime> Refused = []; // API name -> when Steam refused the write: not asked again for a week
		public DateTime RecheckAt;      // a "nothing left for now" that can change by itself ends here; MinValue = it can't
		public long StuckLibMins = -1;  // Steam's minutes in the game when it stopped like that (-1: not stopped so)
		public long StuckOwnMins;       // and RanMins as of that same read of the library - more on Steam's side than on ours is the owner playing
		public long RanMins;            // minutes this app had the game running at all: PlayedMins, and the ones not played for achievements (a night idle)
		public long RanAtLib;           // RanMins when the library was last read (see NoteLibraryRead) - not saved: the next read sets it again
		public DateTime WroteOkAt;      // the last time a write in this game went through - a refusal after that is about the achievement
		public int PaceUsed = -1;       // the pace the wait to NextAllow was spaced at (-1: that wait isn't a spacing one)
		public DateTime PacedFrom;      // when that spacing started
		public bool Pulled;             // a grind pulled that wait forward - a new pace never puts it back later
		public Dictionary<string, List<DateTime>> Strikes = []; // API name -> when Steam turned it down lately, whatever it said - see StruckOut
		public string? SkipOnce;        // turned down on the last look and not parked: the next look picks another, once (not saved)
		public bool MetaLook = true;    // look on the next tick, outside the spacing, for a "for having the others" one with nothing left to wait for - see MetaLookAsync
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
	public enum Outcome {
		Unknown = 0, Earning = 1, None = 2, Complete = 3, SteamOnly = 4, Capped = 5, NeedsHours = 6, DlcOnly = 7, DlcChecking = 8, DlcUnmapped = 9,

		/// <summary>What's left is multiplayer, which this account skips (AchievementSkipMultiplayer).</summary>
		Multiplayer = 10,

		/// <summary>Steam has no figures at all for how many players have each achievement: nothing to order them by.</summary>
		NoRarity = 11,

		/// <summary>What's left counts something in the game ("100 parries") and the account's counter isn't there.</summary>
		CountersOnly = 12,

		/// <summary>
		/// Something is left, but nothing of it could open at any number of hours: too rare for this account ever, or
		/// waiting on others that stay locked ("all other achievements"). Looked at again in a few days.
		/// </summary>
		NothingOpen = 13
	}

	/// <summary>The outcome as it stands NOW - a game capped under a ceiling that has since been raised is not capped.</summary>
	/// <remarks>
	/// A game done for DLC the account doesn't own is let go by <see cref="RecheckLicencesAsync"/> instead, when the DLC
	/// it owns for that game has changed - not on every licence change: a free game claimed changes the licences too,
	/// and the hunt would have gone back to every held game in turn, for a sitting each, to find nothing new.
	///
	/// Nor is a game stopped under other rules: the multiplayer skip turned on or off since changes what it can reach
	/// (see <see cref="RulesOf"/>). Nor one stopped for a while only (<see cref="GameState.RecheckAt"/>) whose while is
	/// up, or whose owner has played it since.
	/// </remarks>
	private Outcome Current(GameState g) =>
		(g.Last == Outcome.Capped) && (Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100) > g.CappedAt) ? Outcome.Unknown
		: RulesChanged(g) ? Outcome.Unknown
		: (g.Last is Outcome.CountersOnly or Outcome.NothingOpen or Outcome.Capped or Outcome.SteamOnly) && StopOver(g, DateTime.UtcNow) ? Outcome.Unknown
		: g.Last;

	/// <summary>
	/// A stop that can change by itself is over: its few days are up, or the owner has played the game since - Steam's
	/// count of minutes in it went up by half an hour more than this app played it. Counters only move when the game is
	/// really played, and a refusal is only kept a week; a game called done for good on those never came back.
	/// </summary>
	private bool StopOver(GameState g, DateTime now) =>
		(g.RecheckAt != DateTime.MinValue) &&((now >= g.RecheckAt) || OwnerPlayed(g));

	/// <summary>Did the account's owner play the game since it stopped: Steam's minutes up by 30 more than ours?</summary>
	internal static bool OwnerPlayed(long steamMinutesNow, long stuckSteamMins, long stuckOwnMins, long ownMinsNow) =>
		(stuckSteamMins >= 0) && ((steamMinutesNow - stuckSteamMins) - (ownMinsNow - stuckOwnMins) >= 30);

	/// <summary>
	/// "Ours" is every minute this app ran the game (RanMins), not only the ones played for achievements: a game idled
	/// overnight on a human-mode account earns nothing then, but Steam counts the night - and taken as the owner playing,
	/// every stopped game idled at night was looked at again the next day, for a sitting each, to find nothing new.
	///
	/// Both sides are counted from the same moment: the library's reads. Steam's minutes are only read every few hours,
	/// while RanMins went up every minute - compared as they were, the minutes this app ran the game between the
	/// library's last read and the stop came in on Steam's side at the next read and not on ours, and two hours idled
	/// before a stop read as the owner playing: the stop ended early, for a sitting that found nothing new.
	/// </summary>
	private bool OwnerPlayed(GameState g) =>
		Bot.Library.Ready && OwnerPlayed(Bot.Library.MinutesOn(g.App), g.StuckLibMins, g.StuckOwnMins, RanAtLibRead(g));

	/// <summary>The library's read that <see cref="GameState.RanAtLib"/> was taken at (MinValue: none yet).</summary>
	private DateTime _libSeen = DateTime.MinValue;

	/// <summary>
	/// RanMins when the library was last read. A read the tick hasn't seen yet came after the tick's last minute was
	/// counted, so RanMins is still where it was then.
	/// </summary>
	private long RanAtLibRead(GameState g) => Bot.Library.RefreshedAt == _libSeen ? g.RanAtLib : g.RanMins;

	/// <summary>
	/// The library has been read again since the last tick: every game's RanMins is noted as of that read. Called under
	/// the gate, before the tick counts its minute.
	/// </summary>
	private void NoteLibraryRead() {
		DateTime read = Bot.Library.RefreshedAt;

		if (!Bot.Library.Ready || (read == _libSeen)) {
			return;
		}

		_libSeen = read;

		foreach (GameState each in _games.Values) {
			each.RanAtLib = each.RanMins;
		}
	}

	/// <summary>How long a "nothing left for now" lasts before the game is looked at again: three to five days.</summary>
	private DateTime RecheckSoon() => DateTime.UtcNow.AddHours(_rng.Next(72, 121));

	/// <summary>Stopped until <paramref name="until"/> (MinValue: for good, until something else changes it).</summary>
	/// <remarks>
	/// With the library not read yet there are no Steam minutes to start from - only its few days end that stop.
	/// </remarks>
	private void StopUntil(uint app, GameState g, DateTime until) {
		g.RecheckAt = until;
		g.StuckLibMins = (until == DateTime.MinValue) || !Bot.Library.Ready ? -1 : Bot.Library.MinutesOn(app);
		g.StuckOwnMins = RanAtLibRead(g);
	}

	/// <summary>
	/// A stop that ended by itself (its few days are up, the owner played the game, a refusal ran out) is let go for good:
	/// the game is looked at again now. Only Current read it as over before - the game still held its 8-25 hour back-off,
	/// so the hunt came back to it and earned nothing there for most of a day. Called under the gate.
	/// </summary>
	private bool EndStopIfOver(GameState g, DateTime now) {
		if ((g.Last is not (Outcome.CountersOnly or Outcome.NothingOpen or Outcome.Capped or Outcome.SteamOnly)) || !StopOver(g, now)) {
			return false;
		}

		EndStop(g, now);

		return true;
	}

	/// <summary>The stop is over: unknown until the next look, which is now.</summary>
	private static void EndStop(GameState g, DateTime now) {
		g.Last = Outcome.Unknown;
		g.NextAllow = now;
		g.PaceUsed = -1;
		g.Pulled = false;
		g.RecheckAt = DateTime.MinValue;
		g.StuckLibMins = -1;
	}

	/// <summary>
	/// The one setting that changes what a game can reach, as a number: skipping multiplayer achievements. (Human mode
	/// was here too while it left add-on-looking ones alone; it no longer changes what a game can reach.)
	/// </summary>
	private static int RulesOf(Config.BotConfig cfg) => cfg.AchievementSkipMultiplayer ? 2 : 0;

	/// <summary>Stopped (held, capped, or only multiplayer left) under rules that have changed since.</summary>
	private bool RulesChanged(GameState g) =>
		(g.Last is Outcome.DlcOnly or Outcome.DlcUnmapped or Outcome.Capped or Outcome.Multiplayer) && (g.Rules != RulesOf(Bot.Cfg));

	/// <summary>Outcomes that can only change when the account gets (or loses) one of the game's DLC.</summary>
	internal static bool HeldForDlc(Outcome last) => last is Outcome.DlcOnly or Outcome.DlcUnmapped or Outcome.Capped;

	/// <summary>Has the DLC the account owns for a game changed since it was last looked at?</summary>
	internal static bool DlcChanged(IReadOnlyCollection<uint>? before, IReadOnlySet<uint> now) => !now.SetEquals(before ?? []);

	/// <summary>
	/// A game done for DLC the account doesn't own - or capped, which is counted on what it can reach - is kept that way
	/// (DlcOnly, DlcUnmapped and Capped are saved) and the hunt never comes back to it. Kept for good, a DLC bought later
	/// never let it go. So when the account's licences change, each such game has the DLC it owns read again, from what
	/// is already known (no store questions): the same, and it stays as it is; different, and it's looked at afresh.
	/// Licences that can't be read yet leave it as it is and are tried again next minute.
	///
	/// The owner leaving a game alone ('dlc leave', kept in AchievementDlcLeft) counts as a change too: the DLC of it
	/// that can't be placed are then no longer counted as owned. Taken back ('dlc undo'), the game earns anyway again and
	/// is let go within the minute. A DLC whose achievements are known exactly always goes by the licences.
	///
	/// So does the game's DLC map being built again. A game held on a map that couldn't place a DLC (Steam didn't
	/// answer for its name, say) stayed held when the next build could place it - nothing about the licences had
	/// changed. A held game's map is built again when it is due (a week, or an hour for one built without every name),
	/// and the game is looked at again when what the new map says about its achievements differs.
	/// </summary>
	private async Task RecheckLicencesAsync(CancellationToken ct) {
		long stamp = Bot.LicenseStamp;

		if (stamp == 0) {
			return;
		}

		string trusted = TrustKey(Bot.Cfg.AchievementDlcLeft.Keys);
		bool trustChanged = trusted != _trustSeen;

		// Unseen from the start of the pass, so a pass cut short (an exception, a cancel) can't leave the old list marked.
		if (trustChanged) {
			_trustSeen = null;
		}
		List<(uint App, List<uint> Owned, long Licences, long MapStamp, long HoldKey)> held;

		lock (_gate) {
			held = [.. _games.Where(static kv => HeldForDlc(kv.Value.Last))
				.Select(static kv => (kv.Key, kv.Value.DlcOwned, kv.Value.Licences, kv.Value.MapStamp, kv.Value.HoldKey))];
		}

		// Only marked as seen once every held game has been looked at: one whose licences couldn't be read is left for
		// next minute, and would otherwise never be asked again (its licence stamp may well not have changed).
		bool all = true;

		foreach ((uint app, List<uint> before, long licences, long mapStamp, long holdKey) in held) {
			DlcAchievements.Map? map = DlcAchievements.Current(app);

			// Due to be built again: in the background, behind whatever is being played. Once it has been, its stamp is
			// new, and the game is looked at below.
			if (DlcAchievements.Stale(map)) {
				DlcAchievements.Request(Bot, app);
			}

			// No map yet, or one being built again right now: wait for the background build rather than asking for it
			// here. ViewAsync asks urgently, and doing that every minute for every held game pushed them all ahead of the
			// game being played - which then waited behind the whole backlog, unlocking nothing. Once built, the new stamp
			// brings the game back here. A stale map that isn't being rebuilt (the last try failed and is waiting out its
			// back-off) is still looked at, or a game whose build keeps failing would stay held even after the DLC was
			// bought. A game without a map can't be judged at all.
			if ((map == null) || (DlcAchievements.Stale(map) && DlcAchievements.IsPending(app))) {
				// Skipped while a change to the left-alone list is waiting: that change isn't marked as seen until this
				// game has had its look. Marked anyway, an undo made while its rebuild was queued was never seen if the
				// rebuild then failed. (A game with no map at all is looked at once one is built - its new stamp does that.)
				if (trustChanged && (map != null)) {
					all = false;
				}

				continue;
			}

			bool mapChanged = (map != null) && (DlcAchievements.Stamp(map) != mapStamp);

			if (!trustChanged && (licences == stamp) && !mapChanged) {
				continue;
			}

			DlcAchievements.View view = await DlcAchievements.ViewAsync(Bot, app, TimeSpan.Zero, ct).ConfigureAwait(false);

			if (!view.Known) {
				all = false;

				continue;
			}

			// What the rule says about the game now, against what it said when the game was held. The same, and it stays
			// held - a map built again with nothing new in it, a free game claimed. Different, and it's looked at again.
			long key = DlcAchievements.HoldKey(view.Map, view.Owned, view.ByLicence);
			// A game held before this was recorded (holdKey 0, an empty owned list) is only noted down the first time, not
			// let go: otherwise every capped and held game was looked at again after an update, a sitting in each, to end
			// up exactly where it was.
			bool first = holdKey == 0;
			bool changed = first
				? (licences != 0) && DlcChanged(before, view.Owned)
				: (DlcChanged(before, view.Owned) || (key != holdKey));

			lock (_gate) {
				if (!_games.TryGetValue(app, out GameState? g) || !HeldForDlc(g.Last)) {
					continue;
				}

				g.Licences = view.Licences;
				g.DlcOwned = [.. view.Owned.Order()];
				g.DlcKey = view.LicenceKey ?? g.DlcKey;
				g.MapStamp = DlcAchievements.Stamp(view.Map);
				g.HoldKey = key;

				if (changed) {
					g.Last = Outcome.Unknown;
					g.NextAllow = DateTime.UtcNow;
					ForgetReach(g);
				}
			}

			if (changed) {
				Log.Debug($"{GameNames.Of(app)}: the DLC this account owns for it, or what is known about its DLC, changed - looking at its achievements again", Bot.Name);
			}
		}

		// A change not yet seen by every held game stays unseen until one pass has seen it all the way through. Left as the
		// old list, taking a game off and putting it back while a pass was incomplete read as "no change" - and the game
		// stayed held for up to a week.
		if (all) {
			_trustSeen = trusted;
		} else if (trustChanged) {
			_trustSeen = null;
		}
	}

	/// <summary>The games left alone, as one string - the same list in any order is the same.</summary>
	internal static string TrustKey(IEnumerable<uint>? apps) => string.Join(",", (apps ?? []).Distinct().Order());

	/// <summary>
	/// The left-alone list as last acted on. Null after a start, so the first minute looks at every held game once: the
	/// list may have been changed while the app was closed. What it owns for a game is compared - a held game whose
	/// counted DLC are the same stays held.
	/// </summary>
	private string? _trustSeen;

	private readonly Dictionary<uint, GameState> _games = [];
	private readonly Random _rng = new();
	private readonly Lock _gate = new();

	private DateTime _lastTick = DateTime.MinValue;
	private Said _status = new("off");
	private uint _grindReset;   // the app whose schedule we've already pulled forward for the current grind (0 = none)

	public override string Name => "achievements";
	public override string Status => Bot.Cfg.UnlockAchievements ? _status : "";

	/// <summary>
	/// Read the saved state, once - by the minute's tick, or first by whoever asks about the games (the dashboard, a
	/// 'dlc' command) before the first tick has run. Asked before it, an answer was given for a game this didn't know
	/// was held yet.
	/// </summary>
	private void EnsureLoaded() {
		// Held for the whole read: a tick that went ahead while the dashboard's read was half done saved the state it
		// had so far over the file.
		lock (_loadGate) {
			if (!_loaded) {
				Load();
				_loaded = true;
			}
		}
	}

	private bool _loaded;
	private readonly Lock _loadGate = new();

	/// <summary>
	/// Did the last look at this game find add-ons the account doesn't own whose achievements Steam doesn't place? Only
	/// such a game can be left alone ('dlc leave') - every other one already goes by what is known.
	/// </summary>
	public bool IsUnclear(uint app) {
		EnsureLoaded();

		lock (_gate) {
			return _games.TryGetValue(app, out GameState? g) && g.Unclear;
		}
	}

	/// <summary>
	/// Which of a game's DLC ids the account had for good when it was last looked at (<see cref="DlcAchievements.LicenceKey"/>);
	/// null when that hasn't been read.
	/// </summary>
	public string? DlcKeyOf(uint app) {
		EnsureLoaded();

		lock (_gate) {
			return _games.TryGetValue(app, out GameState? g) ? g.DlcKey : null;
		}
	}

	/// <summary>
	/// A game left alone was let go again ('dlc undo'): looked at again the next time it's played, rather than waiting out
	/// the minute's licence look (which would get there too). What it may then unlock is worked out afresh - nothing is
	/// unlocked by this.
	/// </summary>
	public void Release(uint app) {
		EnsureLoaded();

		lock (_gate) {
			if (!_games.TryGetValue(app, out GameState? g) || !HeldForDlc(g.Last)) {
				return;
			}

			g.Last = Outcome.Unknown;
			g.NextAllow = DateTime.UtcNow;
			ForgetReach(g);
		}

		Save();
	}

	/// <summary>
	/// A game held for DLC is let go: what it could reach was counted without the held ones, and that's forgotten until
	/// the next look works it out again. Kept, Portal let go after being held 9 of 15 read as 6 of 6 - finished - and the
	/// hunt rested it for days the moment it came back. Called under the gate.
	/// </summary>
	private static void ForgetReach(GameState g) {
		g.Reachable = 0;
		g.DlcHeld = 0;
	}

	/// <summary>
	/// Something was unlocked in <paramref name="app"/> outside the pacer ('cheevo unlock'): a "for having the others"
	/// one it completed goes on the next tick the game runs (see MetaLookAsync), as the game would have awarded it.
	/// </summary>
	public void LookForMetas(uint app) {
		EnsureLoaded();

		lock (_gate) {
			StateFor(app).MetaLook = true;
		}

		Save();
	}

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
				Log.Warn(new Said("achievement pacer hiccup: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);

				// where it broke, once per new kind of failure - the message says what, only the stack says where
				if (Log.DebugOnChange($"hiccup:{Name}:{Bot.Name}", $"{Name}: {Log.Describe(e)}", Bot.Name)) {
					Log.StackToFile(e, Bot.Name);
				}
			}

			if (!await Sleep(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>
	/// Whether an achievement may pop now: always without human mode; with it, only while the account is up.
	///
	/// Asked "may it react while asleep" (AwakeFor) before - which is always yes with "Only react while awake" off, so
	/// on an account set that way a drops run or a hunt going on into the night unlocked achievements at 4am, with the
	/// time on the profile for anybody to read. That switch is about answering other people; this is the account's own
	/// record.
	/// </summary>
	internal static bool MayUnlockNow(Bot bot) => !bot.Cfg.LegitMode || HumanMode.UpAndAbout(bot);

	/// <summary>When each running game's current sitting may first unlock something (10-30 minutes in).</summary>
	private readonly Dictionary<uint, DateTime> _sittingSince = [];

	/// <summary>
	/// Ends the sitting of every game not in <paramref name="running"/>: the next time it runs is a new sitting, with its
	/// own 10-30 minutes before anything may unlock. Called before any of the tick's returns that a stopped game can get
	/// to - a break, the night, a pause or a sign-out in between makes a new sitting. Done only after them, a game played
	/// again after a break (or the next morning) kept its old sitting and could unlock in its first minute.
	/// </summary>
	internal static void KeepSittings(Dictionary<uint, DateTime> sittings, IReadOnlyCollection<uint> running) {
		foreach (uint gone in sittings.Keys.Where(k => !running.Contains(k)).ToList()) {
			sittings.Remove(gone);
		}
	}

	private async Task StepAsync(CancellationToken ct) {
		EnsureLoaded();

		if (!Bot.IsOnline || Bot.Paused || Bot.PlayingBlocked) {
			_status = new Said("waiting");

			// Nothing of ours is running now, so whatever runs next is a new sitting.
			KeepSittings(_sittingSince, []);

			return;
		}

		await RecheckLicencesAsync(ct).ConfigureAwait(false);

		// A new "How fast" re-spaces the waits already running, every minute - asleep or not, so the status says the new
		// time straight away. Every game's, not only the ones running now: a game not being played kept a wait spaced at
		// the old pace for anything that showed it, until the next time it ran.
		lock (_gate) {
			foreach (GameState each in _games.Values) {
				RepaceIfChanged(each, Bot.Cfg.AchievementPace);
			}
		}

		// One minute of credit per tick, and only for time that genuinely elapsed. Without this a restart loop,
		// or a tick that ran late, would hand out playtime the account never spent - and playtime is exactly
		// what the rarity gate is made of, so inflating it is how a bot ends up popping a 2% achievement on day
		// one. Anything longer than three minutes is treated as a gap rather than as time played.
		DateTime now = DateTime.UtcNow;
		double elapsed = _lastTick == DateTime.MinValue ? 0 : (now - _lastTick).TotalMinutes;
		_lastTick = now;

		if (elapsed > 3) {
			// A gap (the PC asleep, a stall): no telling what ran in it - start every sitting again.
			KeepSittings(_sittingSince, []);

			return;
		}

		if (elapsed < 0.5) {
			return;
		}

		if (!Bot.Grinding) {
			_grindReset = 0;   // grind ended - a later grind may re-engage the schedule
		}

		List<uint> running = CurrentGames();

		// The minutes this app runs a game without playing it for achievements - a night idle on a human-mode account, the
		// main game left alone, a game beside a grind - count as its own too, or Steam's count of them reads as the owner
		// playing (see OwnerPlayed). Only for a game already known here: that is the only kind that can be stopped.
		lock (_gate) {
			NoteLibraryRead();   // first, so a read since the last tick is noted with the minutes it saw

			foreach (uint idled in Bot.PlayingApps.Distinct()) {
				if (!running.Contains(idled) && _games.TryGetValue(idled, out GameState? known)) {
					known.RanMins++;
				}
			}
		}

		// A game that stopped ends its sitting - before the "nothing running" return below (see KeepSittings).
		KeepSittings(_sittingSince, running);

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
		bool mayUnlock = MayUnlockNow(Bot);

		// Each game's current sitting: an achievement doesn't pop a minute after launching, whatever was played before.
		foreach (uint app in running) {
			if (!_sittingSince.ContainsKey(app)) {
				_sittingSince[app] = now + Rng.Minutes(10, 30);   // the earliest this sitting may unlock anything
			}

			GameState g = StateFor(app);
			Profile prof = ProfileFor(app);

			lock (_gate) {
				g.PlayedMins++;
				g.RanMins++;

				// The ceiling was raised since this game stopped at it. A capped game backs off for most of a day,
				// so without this, raising the setting did nothing visible for up to 25 hours - which is exactly
				// what "broken" looks like from the outside.
				if ((g.Last == Outcome.Capped) && (ceilingNow > g.CappedAt)) {
					g.Last = Outcome.Unknown;
					g.NextAllow = now;
				}

				// The same for the multiplayer skip turned on or off: what it can reach is different now.
				if (RulesChanged(g)) {
					g.Last = Outcome.Unknown;
					g.NextAllow = now;
				}

				// And for a stop that has ended by itself - the owner played it, its few days are up, a refusal ran out.
				EndStopIfOver(g, now);

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
						g.Pulled = true;
					}
				}
			}

			if (!mayUnlock) {
				continue;
			}

			// A "for having the others" one with nothing left to wait for goes now, outside the spacing and the sitting: a
			// game awards it by itself, the moment it's earned - or as it starts, for one earned while it wasn't running.
			bool metaLook;

			lock (_gate) {
				metaLook = g.MetaLook;
			}

			if (metaLook && await MetaLookAsync(app, g, prof, ct).ConfigureAwait(false)) {
				break;
			}

			if ((now < _sittingSince[app]) || !Due(g, prof)) {
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
			int pace = Bot.Cfg.AchievementPace;
			int playedGate = PlayedGate(prof, onboarding, pace);
			RepaceIfChanged(g, pace);

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

		// Nothing from DLC this account doesn't own, ever. Games keep their DLC achievements in the base game's list
		// (Call of Duty: Modern Warfare II and III are 63 of its 157), and one of those on the profile of an account
		// without the DLC is something nobody could have earned. Until the game has been worked out - one question to
		// Steam for a game with no DLC, a few minutes for one with a hundred - nothing in it is unlocked at all.
		//
		// "Not worked out" is also: the account's licences couldn't be read just now. That is checking too - never
		// "owns none of its DLC", which would call the game done for this account until its licences changed.
		DlcAchievements.View dlc = await DlcAchievements.ViewAsync(Bot, app, TimeSpan.Zero, ct).ConfigureAwait(false);

		if (!dlc.Known) {
			lock (_gate) {
				g.Last = Outcome.DlcChecking;
			}

			Back(g, TimeSpan.FromMinutes(_rng.Next(3, 9)));

			return false;
		}

		// What the account may earn here, and what it leaves alone and why - see Sort. Refused by Steam in the last week:
		// left alone too, rather than asked again every hour.
		bool skipMultiplayer = Bot.Cfg.AchievementSkipMultiplayer;
		HashSet<string> refused;
		DateTime refusalEnds = DateTime.MinValue;

		lock (_gate) {
			DateTime weekAgo = DateTime.UtcNow - RefusedFor;
			refused = new HashSet<string>(g.Refused.Where(kv => kv.Value > weekAgo).Select(static kv => kv.Key), StringComparer.Ordinal);

			// When the first of those refusals runs out: a game that is "Steam only" because of them is looked at again then.
			if (refused.Count > 0) {
				refusalEnds = g.Refused.Where(kv => kv.Value > weekAgo).Min(static kv => kv.Value) + RefusedFor;
			}
		}

		Sorted sorted = Sort(set, dlc, skipMultiplayer, refused);
		int reachable = sorted.Reachable;
		int held = sorted.HeldDlc;
		bool unmapped = dlc.Unmapped;
		bool saidHeld;

		// Which map this was, and what it says - kept with a game held for DLC, so a map built again that says something
		// else lets it go (see RecheckLicencesAsync).
		long mapStamp = DlcAchievements.Stamp(dlc.Map);
		long holdKey = DlcAchievements.HoldKey(dlc.Map, dlc.Owned, dlc.ByLicence);

		lock (_gate) {
			g.Reachable = reachable;
			g.DlcHeld = held;
			g.Multiplayer = sorted.Multiplayer;
			g.Counters = sorted.Counters;
			g.Rules = RulesOf(Bot.Cfg);
			g.Unmapped = unmapped;
			g.Unclear = dlc.Unclear;
			g.Licences = dlc.Licences;
			g.DlcOwned = [.. dlc.Owned.Order()];
			g.DlcKey = dlc.LicenceKey ?? g.DlcKey;
			g.MapStamp = mapStamp;
			g.HoldKey = holdKey;
			saidHeld = g.DlcSaid == held;
			g.DlcSaid = held;
		}

		// Said once per count. "Can't tell" only for a game left alone ('dlc leave'): every other game earns anyway.
		if (!saidHeld && (held > 0)) {
			Log.Info(new Said("{0}: {1}", GameNames.Of(app), unmapped
				? new Said("can't tell which achievements come with its DLC - {0} left alone", held)
				: new Said("{0} achievement(s) are from DLC this account doesn't own - left alone", held)), Bot.Name);
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
		//
		// And what's left being DLC this account doesn't own is the same: nothing more to earn here. Said as that, not
		// as "Steam only", when nothing left is Steam's to award. So is what's left being multiplayer, on an account that
		// skips those.
		//
		// An achievement the game's map has never seen (a DLC added since it was built, most likely) is neither: it waits
		// while the game is worked out again, and the game is looked at again soon rather than called done.
		if (sorted.Candidates.Count == 0) {
			if (sorted.Checking > 0) {
				lock (_gate) {
					g.Last = Outcome.DlcChecking;
				}

				Back(g, TimeSpan.FromMinutes(_rng.Next(10, 30)));

				return false;
			}

			// What's left waiting on the game's own counters comes first: those move when the game is really played, so the
			// game is only done for a few days (or until the owner plays it), whatever else is held. Kept as held for DLC
			// before, a game with counters left and some DLC held was never looked at again until the DLC was bought.
			//
			// Then anything held for DLC makes it a game held for DLC, even with some left that only Steam can award: buying
			// the DLC (or taking back 'dlc leave') is what can change it, and only a game kept as held for DLC is looked at
			// again when that happens.
			bool countersOnly = (already < total) && (sorted.Counters > 0);
			bool dlcOnly = !countersOnly && (held > 0);
			bool multiplayerOnly = !countersOnly && !dlcOnly && (sorted.Multiplayer > 0);

			lock (_gate) {
				g.Last = already >= total ? Outcome.Complete
					: countersOnly ? Outcome.CountersOnly
					: dlcOnly ? (unmapped ? Outcome.DlcUnmapped : Outcome.DlcOnly)
					: multiplayerOnly ? Outcome.Multiplayer
					: Outcome.SteamOnly;

				// "Steam only" because Steam refused some lately: only until the first refusal runs out, a week after it.
				StopUntil(app, g, countersOnly ? RecheckSoon()
					: (g.Last == Outcome.SteamOnly) && (sorted.Refused > 0) ? refusalEnds
					: DateTime.MinValue);
			}

			if (grind && (already < total)) {
				Log.Info(countersOnly ? new Said("{0}: what's left waits for the game's own counters - nothing to grind", GameNames.Of(app))
					: multiplayerOnly ? new Said("{0}: what's left is multiplayer, which this account skips - nothing to grind", GameNames.Of(app))
					: !dlcOnly ? new Said("{0}: achievements are server-side - nothing to grind", GameNames.Of(app))
					: unmapped ? new Said("{0}: can't tell which achievements come with its DLC - nothing to grind", GameNames.Of(app))
					: new Said("{0}: what's left is from DLC this account doesn't own - nothing to grind", GameNames.Of(app)), Bot.Name);
			}

			Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));

			return false;
		}

		// Of what this account can reach: DLC it doesn't own, skipped multiplayer ones and the ones whose counter in the
		// game isn't there yet aren't part of its game - for now, in the last case: worked out again on every look. That
		// stops it a little earlier than the same percentage of everything would, which is the safe way round. (The
		// onboarding cluster lifts it only as far as the ceiling rounds - see CeilingCount.)
		int ceilingCount = CeilingCount(reachable, ceiling, prof.OnboardCount);

		bool Capped() {
			lock (_gate) {
				g.Last = Outcome.Capped;
				g.CappedAt = ceiling;

				// Capped only because some are short of their counter: more becomes reachable when those move, so it is
				// looked at again in a few days rather than left for good.
				StopUntil(app, g, sorted.Counters > 0 ? RecheckSoon() : DateTime.MinValue);
			}

			// Done with this game. Back off hard rather than re-reading Steam's stats every played minute.
			Back(g, TimeSpan.FromHours(_rng.Next(8, 25)));

			return false;
		}

		if (already >= ceilingCount) {
			return Capped();
		}

		// "For having the others" ones with nothing left to wait for go first, all together and without a pacing slot of
		// their own - a game awards them by itself (see MetaLookAsync). Only when they all fit under the ceiling: two that
		// a game awards together aren't split.
		lock (_gate) {
			g.MetaLook = false;
		}

		List<Achievement> ready = ReadyMetas(sorted.Candidates, set.All, NoNames);

		if ((ready.Count > 0) && (already + ready.Count <= ceilingCount)) {
			return await UnlockMetasAsync(app, g, set, ready, dlc, already, backOff: true, ct).ConfigureAwait(false) > 0;
		}

		// No figure at all for how many players have each achievement: the order (easiest first) and the rarity floor are
		// both made of those, so nothing is unlocked on a guess. Most often the figures couldn't be fetched just now - they
		// aren't kept then, and the next look asks again; a game Steam has no figures for at all keeps waiting. One
		// achievement without a figure in a game that has them (a newly added one) is different: see RarityAllows.
		if (!set.All.Any(static a => a.GlobalPercent != null)) {
			lock (_gate) {
				g.Last = Outcome.NoRarity;
				g.BurstLeft = 0;
			}

			Back(g, TimeSpan.FromMinutes(_rng.Next(60, 181)));

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
		// The rarity floor is the "possibility" gate and applies to a grind TOO: it opens with the hours in the
		// game, so a rare/grindy achievement (a low global %, e.g. "win 1000 rounds") can't be unlocked two hours
		// in - only what a real player could plausibly have reached by now, easiest-first. A grind just plays the
		// game continuously so the hours (and the floor) move faster; it does not skip ahead to the hard tail.
		int floor = Math.Max(Math.Max(1, prof.MinPercent), RarityFloorForHours(hours / ScaleFor(app, prof)));
		double? typical = Bot.Cfg.AchievementRealLength ? Core.Playtime.TypicalHours(app) : null;

		// Never a "for having the others" one: those go by themselves, straight after the one that completes them.
		List<Achievement> eligible = [.. Eligible(sorted, set.All, already, floor, hours, typical).Where(static a => TraitsOf(a).Meta == Meta.None)];

		// Nothing else to earn, and the ones for having the others don't all fit under the ceiling: as far as it goes.
		if ((eligible.Count == 0) && (ready.Count > 0)) {
			return Capped();
		}

		if (eligible.Count == 0) {
			// More hours open more of it - or nothing left could open at any number of hours (see CanEverOpen): all of it too
			// rare for this account ever, or waiting on ones that stay locked. Then the game is done for now, so the hunt
			// moves on - for a few days, or until the owner plays it: counters move, and so do Steam's figures.
			bool canOpen = CanEverOpen(sorted, set.All, already, Math.Max(1, prof.MinPercent), typical);

			lock (_gate) {
				g.BurstLeft = 0;
				g.Last = canOpen ? Outcome.NeedsHours : sorted.Counters > 0 ? Outcome.CountersOnly : Outcome.NothingOpen;
				StopUntil(app, g, canOpen ? DateTime.MinValue : RecheckSoon());
			}

			// The gate is shut at this playtime, which is it working, not failing. Come back later rather than
			// asking again every minute (sooner during a grind, which is actively waiting on them).
			Back(g, !canOpen ? TimeSpan.FromHours(_rng.Next(8, 25)) : TimeSpan.FromMinutes(grind ? _rng.Next(15, 41) : _rng.Next(60, 181)));

			return false;
		}

		// Base game first, then most-common first - see NextPick. One turned down on the last look steps aside once.
		string? skip;

		lock (_gate) {
			skip = g.SkipOnce;
			g.SkipOnce = null;
		}

		Achievement pick = NextPick(eligible, a => sorted.Later.Contains(a.Name), _rng.Next(100) < 20, skip);

		(bool ok, string message, int changed, Achievements.Refusal refusal) = await Achievements.SetCheckedAsync(Bot, set, [pick], true, ct, dlc).ConfigureAwait(false);

		if (!ok) {
			Log.Debug(new Said("couldn't unlock \"{0}\" in {1} - {2}", pick.Display, GameNames.Of(app), message), Bot.Name);

			// Some achievements are Steam's own without the schema saying so. One Steam said can't be set by a client is left
			// alone for a week rather than asked about again every hour, and the next look picks another - see ParkRefusal.
			// So is one turned down three times over two days, whatever Steam said (StruckOut). Otherwise the next look
			// tries another one first: the same top pick asked about every hour, turned down every hour, was all a game
			// ever did - nothing else in it unlocked, and it was never called done.
			bool shared = Bot.Library.Find(app)?.Shared == true;

			lock (_gate) {
				TurnedDown(g, pick.Name, refusal, shared, DateTime.UtcNow);
			}

			Back(g, TimeSpan.FromMinutes(_rng.Next(30, 90)));

			return false;
		}

		// Already unlocked when the write came to it: something else got there between the read above and the write (a
		// 'cheevo unlock', unlock-everything). Nothing was earned now, so nothing is said, notified or put in the history,
		// and no gap is spaced out from it - the next look picks the next one. Taken as an unlock, it said "unlocked" a
		// second time, counted it twice, and held the game back as if it had just earned one.
		if (changed == 0) {
			lock (_gate) {
				g.Unlocked = already + 1;
				g.MetaLook = true;   // it may have been the last one a "for having the others" one waited on
			}

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
			g.PacedFrom = DateTime.UtcNow;
			g.PaceUsed = PaceOf(Bot.Cfg.AchievementPace);
			g.Pulled = false;
			g.WroteOkAt = DateTime.UtcNow;
			g.Strikes.Remove(pick.Name);
		}

		Said rarity = pick.GlobalPercent is { } percent ? new Said(" ({0}% have it)", percent.ToString("0.#")) : default;
		Log.Reward(new Said("unlocked \"{0}\" in {1}{2} ({3}/{4})", pick.Display, GameNames.Of(app), rarity, nowUnlocked, total), Bot.Name, topic: Topic.Achievements);
		Remember(new Unlock(app, GameNames.Of(app), pick.Display, pick.GlobalPercent, DateTime.UtcNow, nowUnlocked, total));
		Stats.Record(Stats.KindAchievement, Bot.Name);

		// The last one a "for having the others" one waited on: the game awards it a moment later, with any other one it
		// completes too. The pick's own spacing stands - this takes no slot of its own. One that doesn't all fit under the
		// ceiling stays locked, as the pick before it would have.
		List<Achievement> follow = ReadyMetas(sorted.Candidates, set.All, new HashSet<string>([pick.Name], StringComparer.Ordinal));

		if ((follow.Count > 0) && (nowUnlocked + follow.Count <= ceilingCount)) {
			await Task.Delay(TimeSpan.FromSeconds(_rng.Next(1, 6)), ct).ConfigureAwait(false);
			await UnlockMetasAsync(app, g, set, follow, dlc, nowUnlocked, backOff: false, ct).ConfigureAwait(false);
		}

		return true;
	}

	private static readonly HashSet<string> NoNames = [];

	/// <summary>
	/// The "for having the others" achievements of <paramref name="candidates"/> with nothing left to wait for, once
	/// <paramref name="justUnlocked"/> are unlocked too: every one <see cref="MetaBlocked"/> no longer holds - and with them
	/// any that only waited on those ("all other achievements" on "all base game achievements"), as a game awards them in
	/// the same moment. Only candidates: never one held for DLC, skipped as multiplayer, refused lately, short of a counter
	/// or Steam's own - and one of those still locked holds the rest as it always did.
	/// </summary>
	internal static List<Achievement> ReadyMetas(IReadOnlyCollection<Achievement> candidates, IReadOnlyCollection<Achievement> all, IReadOnlySet<string> justUnlocked) {
		HashSet<string> going = new(StringComparer.Ordinal);
		List<Achievement> ready = [];
		bool more = true;

		while (more) {
			more = false;
			List<Achievement> after = [.. all.Select(a => !a.Unlocked && (justUnlocked.Contains(a.Name) || going.Contains(a.Name)) ? a with { Unlocked = true } : a)];

			foreach (Achievement m in candidates) {
				if (m.Unlocked || !m.Settable || m.CounterShort || IsSpecialGlobal(m) || (TraitsOf(m).Meta == Meta.None)
					|| justUnlocked.Contains(m.Name) || going.Contains(m.Name) || MetaBlocked(m, after)) {
					continue;
				}

				going.Add(m.Name);
				ready.Add(m);
				more = true;
			}
		}

		return ready;
	}

	/// <summary>
	/// The account has everything a "for having the others" one waits for - unlocked before they went straight after the
	/// last one, by 'cheevo unlock', or by a write the pacer didn't make: it goes now, on the tick, outside the spacing and
	/// the sitting (a game awards one like that as it starts), with the DLC rule, the multiplayer skip, refusals and the
	/// ceiling as for any other. One read of the game, once - the next look comes when something could have changed.
	/// True when one was unlocked.
	/// </summary>
	private async Task<bool> MetaLookAsync(uint app, GameState g, Profile prof, CancellationToken ct) {
		lock (_gate) {
			g.MetaLook = false;
		}

		AchievementSet? set = await Achievements.GetAsync(Bot, app, ct).ConfigureAwait(false);

		if ((set == null) || (set.All.Count == 0) || set.All.All(static a => a.Unlocked)) {
			return false;
		}

		DlcAchievements.View dlc = await DlcAchievements.ViewAsync(Bot, app, TimeSpan.Zero, ct).ConfigureAwait(false);

		// Its add-ons not worked out yet: nothing goes on a guess - looked at again on the next tick.
		if (!dlc.Known) {
			lock (_gate) {
				g.MetaLook = true;
			}

			return false;
		}

		HashSet<string> refused;

		lock (_gate) {
			DateTime weekAgo = DateTime.UtcNow - RefusedFor;
			refused = new HashSet<string>(g.Refused.Where(kv => kv.Value > weekAgo).Select(static kv => kv.Key), StringComparer.Ordinal);
		}

		Sorted sorted = Sort(set, dlc, Bot.Cfg.AchievementSkipMultiplayer, refused);
		int already = set.All.Count(static a => a.Unlocked);
		int ceiling = Bot.Grinding && (Bot.GrindGame == app) && !Bot.GrindIsBoost ? 100 : Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100);
		List<Achievement> ready = ReadyMetas(sorted.Candidates, set.All, NoNames);

		return (ready.Count > 0) && (already + ready.Count <= CeilingCount(sorted.Reachable, ceiling, prof.OnboardCount))
			&& (await UnlockMetasAsync(app, g, set, ready, dlc, already, backOff: false, ct).ConfigureAwait(false) > 0);
	}

	/// <summary>
	/// Unlocks "for having the others" ones together, in one write, as a game awards them: logged, kept and counted like
	/// any other, but no pacing slot is used - the next ordinary one keeps its own wait. Turned down: a strike each, as for
	/// any other; <paramref name="backOff"/> also spaces the next look out (when this was the look's whole answer). How
	/// many were unlocked.
	/// </summary>
	private async Task<int> UnlockMetasAsync(uint app, GameState g, AchievementSet set, List<Achievement> metas, DlcAchievements.View dlc, int already, bool backOff, CancellationToken ct) {
		(bool ok, string message, int changed, Achievements.Refusal refusal) = await Achievements.SetCheckedAsync(Bot, set, metas, true, ct, dlc).ConfigureAwait(false);
		int total = set.All.Count;

		if (!ok) {
			Log.Debug(new Said("couldn't unlock \"{0}\" in {1} - {2}", string.Join("\", \"", metas.Select(static m => m.Display)), GameNames.Of(app), message), Bot.Name);
			bool shared = Bot.Library.Find(app)?.Shared == true;

			lock (_gate) {
				foreach (Achievement m in metas) {
					TurnedDown(g, m.Name, refusal, shared, DateTime.UtcNow);
				}
			}

			if (backOff) {
				Back(g, TimeSpan.FromMinutes(_rng.Next(30, 90)));
			}

			return 0;
		}

		lock (_gate) {
			// None changed: another write got there first - unlocked all the same, and nothing to say.
			g.Unlocked = already + (changed == 0 ? metas.Count : changed);

			if (changed > 0) {
				g.Last = Outcome.Earning;
				g.WroteOkAt = DateTime.UtcNow;

				foreach (Achievement m in metas) {
					g.Strikes.Remove(m.Name);
				}
			}
		}

		if (changed == 0) {
			return 0;
		}

		if (changed == metas.Count) {
			int n = already;

			foreach (Achievement m in metas) {
				n++;
				Said rarity = m.GlobalPercent is { } percent ? new Said(" ({0}% have it)", percent.ToString("0.#")) : default;
				Log.Reward(new Said("unlocked \"{0}\" in {1}{2} ({3}/{4})", m.Display, GameNames.Of(app), rarity, n, total), Bot.Name, topic: Topic.Achievements);
				Remember(new Unlock(app, GameNames.Of(app), m.Display, m.GlobalPercent, DateTime.UtcNow, n, total));
			}
		} else {
			// Steam put some back: which ones isn't known here, so the count is said as it is.
			Log.Reward(new Said("{0} in {1}", message, GameNames.Of(app)), Bot.Name, topic: Topic.Achievements);
		}

		Stats.Record(Stats.KindAchievement, Bot.Name, changed);

		return changed;
	}

	/// <summary>How long an achievement Steam refused to set is left alone before it is tried again.</summary>
	private static readonly TimeSpan RefusedFor = TimeSpan.FromDays(7);

	/// <summary>
	/// A write that didn't go through: a strike, and parked or stepped past once - see the caller. Not when Steam was never
	/// asked about the achievement (not connected, the read before the write failed or timed out, what the account owns
	/// changed): that says nothing about it. Struck and stepped past for those, a Steam hiccup moved the hunt off its
	/// best pick, and three of them over two days parked a perfectly good achievement for a week. Called under the gate.
	/// </summary>
	private static void TurnedDown(GameState g, string name, Achievements.Refusal refusal, bool shared, DateTime at) {
		if (refusal == Achievements.Refusal.NotAsked) {
			return;
		}

		if (!g.Strikes.TryGetValue(name, out List<DateTime>? strikes)) {
			strikes = [];
			g.Strikes[name] = strikes;
		}

		strikes.RemoveAll(t => at - t > StrikesKept);
		strikes.Add(at);

		if (ParkRefusal(refusal, shared, g.WroteOkAt, at) || StruckOut(strikes)) {
			g.Refused[name] = at;
			g.Strikes.Remove(name);
		} else {
			g.SkipOnce = name;
		}
	}

	/// <summary>
	/// Is a refused write about the achievement itself, so it is left alone for a week? Only when Steam said so. It put
	/// the stat back ("failed validation"): always - Steam took the write and the game's servers said no, whoever owns
	/// the game. It answered AccessDenied / InsufficientPrivilege: unless the game is borrowed from the family, where the
	/// owner starting it can be the reason. Any other answer (InvalidParam and the like) only counts when another write in
	/// the same game went through in the last month: then it's this achievement, not the game. Fail, a timeout, no answer,
	/// "not now", a family member taking the game back - nothing is remembered on one go: those parked a perfectly good
	/// achievement for a week, and the game read "Steam only" with it. Turned down again and again, it is (StruckOut).
	/// </summary>
	/// <remarks>
	/// A put-back in a family game used to be let off as well. The game then tried the same top achievement every 30-90
	/// minutes for good, nothing else in it ever unlocked, and it was never called done.
	/// </remarks>
	internal static bool ParkRefusal(Achievements.Refusal refusal, bool shared, DateTime wroteOkAt, DateTime now) {
		bool wroteHere = now - wroteOkAt < TimeSpan.FromDays(30);

		return refusal switch {
			Achievements.Refusal.PutBack => true,
			Achievements.Refusal.NotSettable => !shared || wroteHere,
			Achievements.Refusal.Answered => wroteHere && !shared,
			_ => false
		};
	}

	/// <summary>How many times one achievement may be turned down, over at least <see cref="StrikeSpan"/>, before it is parked anyway.</summary>
	private const int StrikesToPark = 3;

	/// <summary>The first and last of those at least this far apart: a bad hour on Steam's side is not three strikes.</summary>
	private static readonly TimeSpan StrikeSpan = TimeSpan.FromDays(2);

	/// <summary>Turned down longer ago than this doesn't count any more.</summary>
	private static readonly TimeSpan StrikesKept = TimeSpan.FromDays(14);

	/// <summary>
	/// Turned down three times, the first and last at least two days apart: parked for a week whatever Steam's answers
	/// were. A game where every answer is Fail (a family game, Steam's catch-all) never parks on any one of them.
	/// </summary>
	internal static bool StruckOut(IReadOnlyCollection<DateTime> strikes) =>
		(strikes.Count >= StrikesToPark) && (strikes.Max() - strikes.Min() >= StrikeSpan);

	/// <summary>
	/// One look at a game, sorted before the hours and the order rules have their say.
	/// </summary>
	/// <param name="Candidates">Locked, settable, allowed, its counter there: what the hours and the order rules choose from.</param>
	/// <param name="Reach">What the order rules look at: everything but the locked ones it will never have. A ladder's
	/// lower rung, a story step or an easier difficulty that only comes with DLC (or is multiplayer, on an account that
	/// skips those) would otherwise hold a base-game one back for ever.</param>
	/// <param name="Later">API names of the add-on-looking ones it may earn - only after every eligible base-game one.</param>
	/// <param name="HeldDlc">Locked and held for DLC this account doesn't own (or, in a game left alone, can't be placed).</param>
	/// <param name="Multiplayer">Locked multiplayer ones, skipped.</param>
	/// <param name="Refused">Locked ones Steam refused to set in the last week.</param>
	/// <param name="Checking">Locked ones the game's DLC map has never seen - waiting for it to be worked out again.</param>
	/// <param name="Counters">Locked ones whose counter in the game isn't there yet ("100 parries" at 37).</param>
	/// <param name="Reachable">How many it can have as things stand: all of them, less the held, the skipped and the ones
	/// short of their counter - worked out again on every look, so a counter that moves brings its achievement back.</param>
	internal sealed record Sorted(List<Achievement> Candidates, List<Achievement> Reach, HashSet<string> Later,
		int HeldDlc, int Multiplayer, int Refused, int Checking, int Counters, int Reachable);

	/// <summary>
	/// What may be earned in a game, and what is left alone and why. The same on a human-mode account and a robot.
	///
	///   • DLC: only what is held on certain evidence is never earned - an add-on's exact block it doesn't own, one that
	///     names an add-on it doesn't own, one with "DLC" in its API name while it's missing an add-on
	///     (<see cref="DlcAchievements.View.Of"/>), and everything it can't place in a game the owner left alone ('dlc leave').
	///   • Add-ons Steam doesn't place: the game earns anyway. Its own layout - the first block of achievement stats, then
	///     a jump - is only used for the order: what comes after the jump looks like an add-on's
	///     (<see cref="DlcAchievements.View.AddOnLikely"/>) and goes last, after every eligible base-game one. Never held for
	///     it: PAYDAY 2 and Dead by Daylight put free updates of the base game after a jump, Fallout: New Vegas puts its
	///     add-ons straight after the base game with none.
	///   • Multiplayer, on an account that skips those (<see cref="IsMultiplayer"/>).
	///   • A counted one ("Complete 100 parries") whose counter in the game isn't there: not a candidate, and not counted
	///     as reachable while it's short - it waits for the game to be played.
	///   • Steam's own (the schema says so, or Steam refused it in the last week) and Valve's mass-granted GLOBAL_ ones:
	///     never candidates - but still in reach, as they always were.
	/// Also marks every achievement that belongs to an add-on (<see cref="Achievement.AddOn"/>) for the order rules.
	/// </summary>
	internal static Sorted Sort(AchievementSet set, DlcAchievements.View dlc, bool skipMultiplayer, IReadOnlySet<string> refused) {
		Dictionary<string, string> parts = DlcAchievements.AddOnParts(dlc.Map, set.All);
		HashSet<string> addOn = new(parts.Keys, StringComparer.Ordinal);
		bool multiplayerGame = MultiplayerGame(set.All);
		List<Achievement> candidates = [], reach = [];
		HashSet<string> later = new(StringComparer.Ordinal);
		int heldDlc = 0, multiplayer = 0, refusedCount = 0, checking = 0, counters = 0;

		foreach (Achievement a in set.All) {
			a.AddOnPart = parts.GetValueOrDefault(a.Name.ToLowerInvariant(), "");
		}

		foreach (Achievement a in set.All) {
			if (a.Unlocked) {
				reach.Add(a);

				continue;
			}

			DlcAchievements.Hold hold = dlc.Of(a);

			if (hold is DlcAchievements.Hold.NotOwned or DlcAchievements.Hold.Unmapped) {
				heldDlc++;

				continue;
			}

			if (skipMultiplayer && IsMultiplayer(a, multiplayerGame)) {
				multiplayer++;

				continue;
			}

			reach.Add(a);

			// One the map has never seen waits while the game is worked out again; it may be a lower rung of something.
			if (hold == DlcAchievements.Hold.Checking) {
				checking++;

				continue;
			}

			if (!a.Settable || IsSpecialGlobal(a)) {
				continue;
			}

			// Nothing writes counters, so it waits for the real count. Still in reach: a lower rung short of its count
			// holds the higher rungs, as it should.
			if (a.CounterShort) {
				counters++;

				continue;
			}

			if (refused.Contains(a.Name)) {
				refusedCount++;

				continue;
			}

			if ((hold == DlcAchievements.Hold.None) && dlc.AddOnLikely(a, addOn)) {
				later.Add(a.Name);
			}

			candidates.Add(a);
		}

		return new Sorted(candidates, reach, later, heldDlc, multiplayer, refusedCount, checking, counters,
			set.All.Count - heldDlc - multiplayer - counters);
	}

	/// <summary>Does the game have multiplayer achievements at all (for <see cref="IsMultiplayer"/>'s prestige rule)?</summary>
	internal static bool MultiplayerGame(IEnumerable<Achievement> all) => all.Any(static a => TraitsOf(a).Multiplayer);

	/// <summary>
	/// The ones that may go now: open at these hours (<see cref="RarityAllows"/>) and every order rule satisfied. One short
	/// of its counter ("Complete 100 parries") never gets here (see <see cref="Sort"/>); nothing writes counters, so a
	/// ladder of counted rungs waits for the real count, rung by rung.
	/// </summary>
	internal static List<Achievement> Eligible(Sorted sorted, IReadOnlyCollection<Achievement> all, int already, int floor, double hours, double? typical) =>
		[.. sorted.Candidates.Where(a => !a.CounterShort && RarityAllows(a, floor) && InOrder(a, sorted.Reach, all, already, hours, typical))];

	/// <summary>
	/// Could anything left open at all, at any number of hours: the lowest rarity floor there is, and every hour a story
	/// could ask? None: what's left is too rare for this account ever, or waits on ones that stay locked ("all other
	/// achievements" with some held), or on a lower rung short of its counter - and more play only changes that through
	/// the game's own counters or Steam's figures. Then the game is done for now.
	/// </summary>
	internal static bool CanEverOpen(Sorted sorted, IReadOnlyCollection<Achievement> all, int already, int minPercent, double? typical) =>
		Eligible(sorted, all, already, Math.Max(minPercent, LowestFloor), 1_000_000, typical).Count > 0;

	/// <summary>
	/// The ones of <paramref name="unlocking"/> that may go together, and how many "for having the others" ones wait: one
	/// of those ("obtain all other achievements", "all base game achievements") goes only when nothing it counts would
	/// still be locked once the rest are unlocked - held for DLC, short of a counter, skipped, Steam's own. Two of them
	/// don't hold each other, but for "all base game achievements" staying locked, which holds "all other achievements" -
	/// the pacer's rule (<see cref="MetaBlocked"/>).
	/// </summary>
	internal static (List<Achievement> Go, int Waiting) WithoutWaitingMetas(IReadOnlyCollection<Achievement> all, IReadOnlyCollection<Achievement> unlocking) {
		HashSet<string> going = new(unlocking.Select(static a => a.Name), StringComparer.Ordinal);
		List<Achievement> metas = [.. unlocking.Where(static a => TraitsOf(a).Meta != Meta.None)];
		HashSet<string> metaOk = new(metas.Select(static a => a.Name), StringComparer.Ordinal);
		bool changed = true;

		// Stays locked once this write is done: locked now, and not going - or a meta that has to wait itself.
		bool StaysLocked(Achievement o) => !o.Unlocked && (!going.Contains(o.Name) || ((TraitsOf(o).Meta != Meta.None) && !metaOk.Contains(o.Name)));

		while (changed) {
			changed = false;

			foreach (Achievement meta in metas.Where(m => metaOk.Contains(m.Name)).ToList()) {
				Meta kind = TraitsOf(meta).Meta;
				bool blocked = all.Any(o => (o.Name != meta.Name) && !IsSpecialGlobal(o) && ((kind == Meta.All) || !o.AddOn)
					&& (TraitsOf(o).Meta == Meta.None) && StaysLocked(o));

				// Another meta only holds "all other achievements" back when it is the base game's one, as in the pacer
				// (MetaBlocked). Any meta staying locked counted before: two "every achievement" ones, one of them held for
				// DLC or Steam's own, kept the other locked here while the pacer unlocked it.
				blocked |= all.Any(o => (o.Name != meta.Name) && StaysLocked(o) && (kind == Meta.All) && (TraitsOf(o).Meta == Meta.BaseGame));

				if (blocked) {
					metaOk.Remove(meta.Name);
					changed = true;
				}
			}
		}

		return ([.. unlocking.Where(a => (TraitsOf(a).Meta == Meta.None) || metaOk.Contains(a.Name))], metas.Count - metaOk.Count);
	}

	/// <summary>
	/// Open at this rarity floor. One with no figure (newly added, so Steam hasn't counted it yet) is open whenever
	/// anything is - and is taken last (see <see cref="NextPick"/>); a game with no figures at all isn't asked about here,
	/// it waits. Under half an hour in, nothing is open, figure or not.
	/// </summary>
	internal static bool RarityAllows(Achievement a, int floor) => a.GlobalPercent is { } percent ? percent >= floor : floor <= 100;

	/// <summary>
	/// Every order rule at once: the milestones and "N achievements" ones after what they count, "every other achievement"
	/// after every other one, a ladder's rungs in order, an easier difficulty before a harder one, a story's steps before its
	/// ending (and a DLC's ending after the game's own), New Game+ after the game's endings, the plain thing before the
	/// same thing harder, and the hours a story takes.
	/// </summary>
	/// <param name="reach">What the order rules look at: everything but the locked ones it will never have.</param>
	/// <param name="all">The game's whole list - a milestone counts every one of its group, held or not.</param>
	/// <param name="already">How many it has unlocked in the game.</param>
	internal static bool InOrder(Achievement a, IReadOnlyCollection<Achievement> reach, IReadOnlyCollection<Achievement> all, int already, double hours, double? typical) =>
		(RequiredPriorAchievements(a) <= already)
		&& !MetaBlocked(a, all)
		&& !TierBlocked(a, reach)
		&& !DifficultyBlocked(a, reach)
		&& !StoryEndBlocked(a, reach)
		&& !NewGamePlusBlocked(a, reach)
		&& !VariantBlocked(a, reach)
		&& StoryTimeAllows(a, reach, hours, typical)
		&& !MilestoneUnearned(a, all);

	/// <summary>
	/// Which of the eligible ones is unlocked next.
	///
	/// Base game first. In a game with add-ons the account doesn't own and Steam doesn't say which achievements they
	/// bring, the ones that look like an add-on's (<paramref name="later"/>, see <see cref="DlcAchievements.View.AddOnLikely"/>)
	/// wait until no base-game one is eligible - done, or not open yet at these hours. So if some of them do come with an
	/// add-on, they come last, like a player who finishes the base game before anything else - on every account.
	///
	/// Then strictly most-common first, working toward the rarest last, which is broadly what real players do. A window
	/// of "one of the top three" looked reasonable and was not: it would occasionally pop a 12% one while an 18% and a
	/// 14% sat still locked, and that ordering is unmistakably mechanical. One with no figure yet (newly added) goes after
	/// every one that has a figure.
	///
	/// The only wobble kept is an occasional step to the SECOND most common (<paramref name="wobble"/>), so it is not a
	/// flawless metronome - and only between two that are nearly as common as each other (the second at least
	/// <see cref="WobbleShare"/> of the first): 40% then 39% is a coin toss for a real player, 40% then 12% is not. Never
	/// onto an ending, a step of the story, a New Game+ one or a rung of a ladder, which have a place in the order of
	/// their own, and never from a base-game one to an add-on one.
	///
	/// <paramref name="skip"/> (turned down on the last look) steps aside when there is another to take in its place -
	/// still base game first: an add-on one never goes ahead of a base-game one for it.
	/// </summary>
	internal static Achievement NextPick(IReadOnlyList<Achievement> eligible, Func<Achievement, bool> later, bool wobble, string? skip = null) {
		List<Achievement> sure = [.. eligible.Where(a => !later(a))];
		List<Achievement> pool = sure.Count > 0 ? sure : [.. eligible];

		if ((skip != null) && (pool.Count > 1)) {
			pool.RemoveAll(a => a.Name == skip);
		}

		pool.Sort(static (a, b) => (b.GlobalPercent ?? -1).CompareTo(a.GlobalPercent ?? -1));

		if (wobble && (pool.Count > 1) && (pool[0].GlobalPercent is { } first) && (pool[1].GlobalPercent is { } second)
			&& (second >= first * WobbleShare) && !HasItsPlace(pool[1])) {
			return pool[1];
		}

		return pool[0];
	}

	/// <summary>How close the second most common has to be to the first for the wobble to step to it.</summary>
	private const double WobbleShare = 0.85;

	/// <summary>An ending, a story step, a New Game+ one or a ladder's rung: these go where the order puts them.</summary>
	private static bool HasItsPlace(Achievement a) {
		Traits t = TraitsOf(a);

		return t.Ending || t.Numbered || t.Named || t.NewGamePlus || (t.Ladders.Count > 0);
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
		bool LevelUp,
		bool NewGamePlus,                               // "in New Game+", "NG+": a second run, after the first one's ending
		bool Multiplayer,                               // needs other players - see IsMultiplayer
		Meta Meta                                       // "obtain all other achievements" - see MetaOf
	);

	/// <summary>An achievement for having the others: none, every other one, or every other one of the base game.</summary>
	private enum Meta { None, All, BaseGame }

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
			Regex.IsMatch(Normalize(desc), @"\b(reach|reached|attain|attained|hit)\s+(the\s+)?(max(imum)?\s+)?(level|rank)\b") && !Regex.IsMatch(text, @"\bprestige\b", RegexOptions.IgnoreCase),
			IsNewGamePlus(a),
			MultiplayerByItself(a),
			MetaOf(a));
	}

	/// <summary>
	/// "Purchase something from Baku in New Game+", "Complete the game in NG+", Cuphead's NewGamePlus - a second run of
	/// the game, which only starts once the first one is over.
	/// </summary>
	private static bool IsNewGamePlus(Achievement a) =>
		Regex.IsMatch($"{a.Display} {a.Description}", @"\bnew\s+game\s*(\+|plus\b)|\bng\s*\+|\bng\s+plus\b", RegexOptions.IgnoreCase)
		|| Regex.IsMatch(a.Name, @"new_?game_?plus|ng_?plus", RegexOptions.IgnoreCase);

	/// <summary>
	/// "Obtain all other achievements" (God of War's Father and Son), "Unlock every achievement", "Earn 100% of the
	/// achievements", a "Platinum" for "all trophies" - an achievement for having the rest. "Obtain all base game
	/// achievements" (Ghost of Tsushima's Living Legend) is only the base game's rest. Only the wording that counts
	/// achievements or trophies: Borderlands 2's "Completionist" (all Pirate's Booty side missions) is no such thing, and
	/// nor is Stellaris's "...and all I got was this lousy achievement". The numbered kind ("Achieve 17 of the achievements
	/// in the Sniper pack") is RequiredPriorAchievements' instead.
	/// </summary>
	private static Meta MetaOf(Achievement a) {
		string text = $"{a.Display} {a.Description}".ToLowerInvariant();
		const string Kind = @"(steam\s+)?(achievements?|trophies|trophy)\b";

		bool all = Regex.IsMatch(text, @"\b(all|every|each)\s+(of\s+)?(the\s+)?(other|remaining|the\s+other)?\s*(base[\s-]game\s+|main[\s-]game\s+)?" + Kind)
			|| Regex.IsMatch(text, @"\b100\s?%\s+(of\s+)?(the\s+)?(base[\s-]game\s+|main[\s-]game\s+)?" + Kind)
			|| Regex.IsMatch(text, Kind + @"\s+(at\s+)?100\s?%")
			|| (Regex.IsMatch(a.Display ?? "", @"\b(platinum|completionist|100\s?%)\b", RegexOptions.IgnoreCase)
				&& Regex.IsMatch(text, @"\b(all|every|100\s?%)\b[^.!?]*\b(achievements|trophies)\b"));

		if (!all) {
			return Meta.None;
		}

		return Regex.IsMatch(text, @"\b(base|main)[\s-]game\b") ? Meta.BaseGame : Meta.All;
	}

	/// <summary>
	/// An achievement for having the rest waits for the rest: "every other achievement" for every other one in the game
	/// (Valve's mass-granted GLOBAL_ ones aside), "every base game achievement" for every other one that isn't an
	/// add-on's (<see cref="Achievement.AddOn"/>). Any of them still locked - held for DLC, Steam's own, too rare for this
	/// account ever to earn - and it waits for good, which is the only honest answer: nobody has it without the others.
	/// </summary>
	/// <remarks>
	/// Another one of these only holds "every other achievement" back when it is the base game's ("all base game
	/// achievements"), as in <see cref="WithoutWaitingMetas"/>. Counted like any other, a game with both - or with two
	/// "every other" ones - had each waiting on the other, and neither was ever unlocked, not even in a grind.
	/// </remarks>
	internal static bool MetaBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		Meta meta = TraitsOf(a).Meta;

		return (meta != Meta.None) && all.Any(o => !o.Unlocked && (o.Name != a.Name) && !IsSpecialGlobal(o) && ((meta == Meta.All) || !o.AddOn)
			&& ((TraitsOf(o).Meta == Meta.None) || ((meta == Meta.All) && (TraitsOf(o).Meta == Meta.BaseGame))));
	}

	/// <summary>
	/// New Game+ waits for every locked ending of the same game: a second run starts once the first is finished. An ending
	/// too rare for this account ever to earn, or far rarer than the New Game+ one, doesn't hold it for ever (the same
	/// escape as a story's steps - see <see cref="CouldComeFirst"/>), and only an ending of the same part counts: an add-on's
	/// ending doesn't hold the base game's New Game+.
	/// </summary>
	private static bool NewGamePlusBlocked(Achievement a, IReadOnlyCollection<Achievement> reach) {
		Traits me = TraitsOf(a);

		if (!me.NewGamePlus) {
			return false;
		}

		foreach (Achievement other in reach) {
			if (other.Unlocked || (other.Name == a.Name)) {
				continue;
			}

			Traits them = TraitsOf(other);

			if (them.Ending && !them.NewGamePlus && (them.Label == me.Label) && (other.AddOnPart == a.AddOnPart) && CouldComeFirst(other, a)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Needs other players, by its own words or API name: multiplayer, co-op, online, versus, PvP, ranked, a public match,
	/// friends from the friends list, Call of Duty's Zombies, MWZ, DMZ and Warzone; or an API name with an mp, zm, pvp,
	/// coop, versus, online or ranked part (Call of Duty's t10_mp_*, t10_zm_*, sat_mp_*, jup_mp_*). Not one that may as
	/// well be done alone: "in the Campaign or in Co-op" is the campaign's too. Read off Portal 2, Call of Duty, Team
	/// Fortress 2, Borderlands 2 and Civilization V: Portal 2's co-op course, Call of Duty's multiplayer, zombies and co-op
	/// campaign are caught; Team Fortress 2's class achievements aren't (only its "five friends in a game" ones).
	/// </summary>
	/// <remarks>
	/// Read off real schemas that it got wrong, so each word only counts where it means other players:
	///   • "co-op" - not "co-op moves with Oda" (Yakuza 0, alone); a bare "coop" only as play ("in coop", "coop mode"), not
	///     "the chicken coop" (2947440).
	///   • "online" - anywhere, but not "some online forum" (738520), an online store, shop, guide, manual, help or community.
	///   • "ranked" - not after a hyphen: "a Master-ranked weapon" (S.T.A.L.K.E.R. 2) is a weapon's grade.
	///   • "versus" - only "versus mode" in the words, never from the API name alone: Call of Duty 4's MAN_VERSUS_MACHINE is
	///     a campaign mission.
	/// And not when alone is offered too, with words in between: The Forest's "Alone in Single Player or together in
	/// Multiplayer", Halo's "Complete 10 missions or multiplayer games" and "a campaign mission or a match of
	/// multiplayer", PAYDAY 2's "within 7 minutes on solo, or 4 minutes on multiplayer".
	/// </remarks>
	private static bool MultiplayerByItself(Achievement a) {
		string text = $"{a.Display} {a.Description}";
		string lower = text.ToLowerInvariant();

		bool byName = Regex.IsMatch(a.Name, @"(^|[_.])(mp|zm|pvp|coop|online|ranked|multiplayer|wz|dmz)([_.]|$)", RegexOptions.IgnoreCase);
		bool byWords = Regex.IsMatch(lower, @"\b(multi-?player|cooperative|pvp|versus mode|matchmaking|mwz|dmz|warzone|zombies mode|public (match|matches|game|games|server|servers)|with a friend|friends? list|other players?|another player|split-?screen)\b")
			|| Regex.IsMatch(lower, CoopWords) || Regex.IsMatch(lower, OnlineWords) || Regex.IsMatch(lower, @"(?<![\w-])ranked\b")
			|| Regex.IsMatch(text, @"\b(in|of) Zombies\b|\bZombies (mode|match|matches|game|games|map|maps|category|categories)\b");

		return (byName || byWords) && !AlsoAlone(lower);
	}

	/// <summary>
	/// Co-op as play: "co-op"/"co op" anywhere but before moves, attacks and the like (a single-player game's tag-team
	/// moves); a bare "coop" only after in/on/play or before mode, match, campaign... (never "the chicken coop").
	/// </summary>
	private const string CoopWords =
		@"\bco[- ]op\b(?!\s+(moves?|attacks?|actions?|techniques?|combos?|finishers?|skills?|heat)\b)"
		+ @"|\b(in|on|via|during|play|playing|played|local|online)\s+(a\s+|the\s+)?coop\b"
		+ @"|\bcoop\s+(mode|modes|game|games|match|matches|campaign|campaigns|mission|missions|partner|partners|player|players|session|sessions|level|levels|map|maps|run|runs|play)\b";

	/// <summary>
	/// Online, wherever it is - "win 10 matches online", "defeat 50 players online", "reach level 20 online", "Red Dead
	/// Online:" - except where it plainly isn't play: an online forum, store, shop, guide, manual, help or community.
	/// </summary>
	/// <remarks>
	/// It was narrowed to a list of the words that may come with it ("online match", "play online"), and that missed
	/// every way of saying it the list didn't think of: "complete 5 races online", "win a round of deathmatch online".
	/// A missed one is the unsafe way round - an achievement only other players could have given, on an account that
	/// skips those - so it's every "online" bar the few that are something to read or buy. An online leaderboard counts:
	/// it's a score other players see, and leaving one alone costs one achievement.
	/// </remarks>
	private const string OnlineWords =
		@"\bonline\b(?!\s+(forums?|stores?|shops?|guides?|manuals?|help|community|communities)\b)";

	/// <summary>
	/// It can be done alone too: alone (the campaign, single player, story, solo, missions) on one side of an "or" and
	/// other players (co-op, online, multiplayer) on the other, a few words apart at most and in the same sentence -
	/// either way round. Not "co-op missions or multiplayer games": both sides are other players there.
	/// </summary>
	internal static bool AlsoAlone(string lower) {
		const string Alone = @"(campaign|single[\s-]?player|story(\s+mode)?|solo|(?<!co-?op\s)missions?)";
		const string Others = @"(co-?op|online|multi-?player)";

		return Regex.IsMatch(lower, @"\b" + Alone + @"\b[^.!?;]{0,30}?\bor\b[^.!?;]{0,30}?\b" + Others + @"\b")
			|| Regex.IsMatch(lower, @"\b" + Others + @"\b[^.!?;]{0,30}?\bor\b[^.!?;]{0,30}?\b" + Alone + @"\b");
	}

	/// <summary>
	/// Multiplayer by itself (<see cref="MultiplayerByItself"/>) - or a prestige in a game whose other achievements are
	/// multiplayer ones. Call of Duty's "Enter Prestige 1" says nothing about where, but prestige is the online ranks;
	/// one that names the campaign or the story isn't.
	/// </summary>
	internal static bool IsMultiplayer(Achievement a, bool multiplayerGame) {
		Traits t = TraitsOf(a);

		return t.Multiplayer
			|| (t.Prestige && multiplayerGame && !Regex.IsMatch($"{a.Display} {a.Description}", @"\b(campaign|story|single-?player)\b", RegexOptions.IgnoreCase));
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
	/// comes first - worded the same apart from the difficulty (see <see cref="DifficultyKey"/>). Medals the same:
	/// silver after bronze.
	///
	/// Only when the easier one is at least about as common as the harder one (<see cref="EasierShare"/>), which it is
	/// whenever players really go up the scale. Where it isn't - Easy rarer than Normal, because most players start on
	/// Normal and never touch Easy - the easier one is no step on the way, and holding Normal behind a 3% Easy held it
	/// for ever on an account whose hours never open 3%.
	/// </summary>
	private static bool DifficultyBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		if (TraitsOf(a).Difficulty is not { } mine) {
			return false;
		}

		foreach (Achievement other in all) {
			if (!other.Unlocked && (other.Name != a.Name) && (TraitsOf(other).Difficulty is { } theirs)
				&& (theirs.Stem == mine.Stem) && (theirs.Rank < mine.Rank)
				&& ((other.GlobalPercent is not { } easier) || (a.GlobalPercent is not { } harder) || (easier >= harder * EasierShare))) {
				return true;
			}
		}

		return false;
	}

	/// <summary>How common an easier difficulty has to be, next to the harder one, to come first: about as common.</summary>
	private const double EasierShare = 0.9;

	/// <summary>
	/// The difficulty an achievement asks for, and the rest of its wording with the difficulty taken out - two
	/// achievements with the same rest are the same thing on different difficulties. The rest is made plain first, so
	/// "Complete the game on Normal" and "Finish the campaign on Hard difficulty" compare equal: the words difficulty,
	/// mode, setting and level go, beat/complete/finish/clear read as one, and game/campaign/story as one.
	/// </summary>
	private static (string Stem, int Rank)? DifficultyKey(Achievement a) {
		foreach (string text in new[] { a.Description, a.Display }) {
			string normal = Regex.Replace(Regex.Replace(Normalize(text ?? ""), @"[^\p{L}\p{N}%:\s]", " "), @"\s+", " ").Trim();
			Match m = Regex.Match(normal, DifficultyPattern);

			if (m.Success) {
				return (DifficultyStem(normal[..m.Index] + " ~ " + normal[(m.Index + m.Length)..]), Difficulties[m.Value]);
			}
		}

		return null;
	}

	/// <summary>The wording around a difficulty, made plain - see <see cref="DifficultyKey"/>.</summary>
	private static string DifficultyStem(string text) {
		string s = Regex.Replace(text, @"\b(difficulty|difficulties|mode|setting|settings|level|on|in|at|the|a|or\s+(harder|higher|above|greater|better))\b", " ");
		s = Regex.Replace(s, @"\b(beat|beaten|complete|completed|finish|finished|clear|cleared)\b", "beat");
		s = Regex.Replace(s, @"\b(game|campaign|story|storyline|adventure)\b", "game");

		return Regex.Replace(s, @"\s+", " ").Trim();
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

			// Only a step of the same part holds an ending: an add-on's step never holds the base game's ending (the add-on
			// is played after, if at all), nor one add-on's step another's - Black Ops 6's missions don't hold Modern
			// Warfare II's campaign. The base game's ending before an add-on's is VariantBlocked's.
			if (other.AddOnPart != a.AddOnPart) {
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
	///   • A DLC's ending after the game's own: "Finish the Whistleblower DLC" after "Finish the game". Which ones are an
	///     add-on's is what the game's DLC map says (<see cref="Achievement.AddOn"/>) as well as the words DLC and
	///     expansion: Cuphead's "Complete your quest on Inkwell Isle IV" names no DLC, but sits in The Delicious Last
	///     Course's block.
	///   • Prestige after the level it takes: "Enter Prestige" after "Reach Level 55".
	/// </summary>
	private static bool VariantBlocked(Achievement a, IReadOnlyCollection<Achievement> all) {
		Traits me = TraitsOf(a);
		bool dlcEnd = (me.Ending || me.Named || Regex.IsMatch(me.Core, @"^(complete|completed|finish|finished|beat|beaten|defeat|defeated)\b"))
			&& (a.AddOn || Regex.IsMatch(me.Modes, @"\b(dlc|expansion)\b"));

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

			if (dlcEnd && them.PlainEnding && (them.Label == me.Label) && !other.AddOn && !Regex.IsMatch(them.Modes, @"\b(dlc|expansion)\b")) {
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
	private int Paced(int minutes) => PaceMinutes(Bot.Cfg.AchievementPace, minutes);

	/// <summary>The pace as one of its three choices: 0 careful, 1 normal, 2 brisk - anything else is normal.</summary>
	internal static int PaceOf(int pace) => pace is 0 or 2 ? pace : 1;

	/// <summary>
	/// Minutes of waiting at a pace: careful twice as long, brisk half (never under a minute), normal as it is.
	/// </summary>
	internal static int PaceMinutes(int pace, int minutes) => PaceOf(pace) switch {
		0 => minutes * 2,
		2 => Math.Max(1, minutes / 2),
		_ => minutes
	};

	/// <summary>
	/// The minutes the game has to be really played between two unlocks, at a pace: the game's own figure (onboarding
	/// or steady), doubled when careful, halved when brisk. A wait like any other - only the hours-based rarity floor and
	/// the order rules are untouched by the pace.
	/// </summary>
	private static int PlayedGate(Profile prof, bool onboarding, int pace) => PaceMinutes(pace, onboarding ? prof.OnboardPlayedMins : prof.SteadyPlayedMins);

	/// <summary>
	/// A wait spaced at one pace, re-spaced at another from when it started: half way through a careful wait, brisk
	/// leaves a quarter of the careful one from its start - which may already be past.
	/// </summary>
	internal static DateTime Repace(DateTime from, DateTime next, int wasPace, int nowPace) {
		double factor(int p) => PaceOf(p) switch { 0 => 2.0, 2 => 0.5, _ => 1.0 };

		return from.AddMinutes((next - from).TotalMinutes / factor(wasPace) * factor(nowPace));
	}

	/// <summary>
	/// "How fast" changed since the wait was spaced out: the rest of it is stretched or shortened now, so the new pace
	/// counts from the next minute rather than after a wait spaced at the old one. Called under the gate.
	///
	/// A wait a grind pulled forward keeps the earlier of the two: re-spaced from where it started, a slower pace put the
	/// grind's first unlock back hours out - exactly what pulling it forward was for.
	/// </summary>
	private static void RepaceIfChanged(GameState g, int pace) {
		if ((g.PaceUsed >= 0) && (g.PaceUsed != PaceOf(pace)) && (g.NextAllow > DateTime.UtcNow)) {
			g.NextAllow = Respaced(g.PacedFrom, g.NextAllow, g.PaceUsed, pace, g.Pulled);
			g.PaceUsed = PaceOf(pace);
		}
	}

	/// <summary>A wait re-spaced at a new pace - never later than it is when a grind has pulled it forward.</summary>
	internal static DateTime Respaced(DateTime from, DateTime next, int wasPace, int nowPace, bool pulled) {
		DateTime repaced = Repace(from, next, wasPace, nowPace);

		return pulled && (repaced > next) ? next : repaced;
	}

	private void Back(GameState g, TimeSpan wait) {
		lock (_gate) {
			g.NextAllow = DateTime.UtcNow.Add(wait);
			g.PaceUsed = -1;
			g.Pulled = false;
		}
	}

	/// <summary>
	/// Nothing more this account will earn in <paramref name="app"/> right now: finished, up to the ceiling, only
	/// Steam-awarded achievements left, or only ones held for DLC - from DLC it doesn't own, or in a game where it can't
	/// be told which come with its DLC. The hunter skips such a game rather than spend a sitting earning nothing, until
	/// the ceiling is raised or the account's licences change.
	///
	/// So does it a game where nothing left could open now - waiting on the game's own counters, too rare for this account
	/// ever, waiting on ones that stay locked - or that is "Steam only" because Steam refused some lately. Those can change
	/// by themselves, so only for a while: a few days (a week for a refusal), or until the owner plays the game (see
	/// <see cref="Current"/>). Then the hunt comes back to it.
	/// </summary>
	public bool NothingLeft(uint app) {
		lock (_gate) {
			return _games.TryGetValue(app, out GameState? g)
				&& (Current(g) is Outcome.Complete or Outcome.SteamOnly or Outcome.Capped or Outcome.DlcOnly or Outcome.DlcUnmapped or Outcome.Multiplayer
					or Outcome.CountersOnly or Outcome.NothingOpen);
		}
	}

	/// <summary>
	/// Where a game stops: the ceiling's share of what the account can reach, never more than there is. <paramref
	/// name="reachable"/> leaves out DLC it doesn't own - counted in, a game whose DLC is most of its list could never get
	/// to its ceiling, and would be played for nothing for ever.
	///
	/// The onboarding cluster lifts it only as far as the ceiling rounds: a short game with an early burst (Portal
	/// onboards 5 of its 15) isn't cut off a little below its share, but a game with one to three achievements can't be
	/// taken past the ceiling on the cluster's account. Three at 90% is 2.7 - three is what that rounds to, so all three;
	/// three at 50% is 1.5 - two, never all three; one at 40% is none.
	/// </summary>
	internal static int CeilingCount(int reachable, int ceilingPercent, int onboard) {
		int percent = Math.Clamp(ceilingPercent, 1, 100);
		int share = reachable * percent / 100;
		int rounded = (int) Math.Round(reachable * percent / 100.0, MidpointRounding.AwayFromZero);

		return Math.Min(reachable, Math.Max(share, Math.Min(onboard, rounded)));
	}

	private GameState StateFor(uint app) {
		lock (_gate) {
			if (!_games.TryGetValue(app, out GameState? g)) {
				g = new GameState { App = app };
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
		int CeilingPercent,
		int Unlocked,
		int Total,
		bool Blocked,
		string Why,
		bool Running,
		string State,
		int DlcHeld = 0,        // locked ones from DLC this account doesn't own, left alone
		bool Unmapped = false,  // left alone, and can't tell which achievements come with its DLC: everything but its owned DLC's left alone
		int Multiplayer = 0     // locked multiplayer ones, skipped
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

					// The hours the pacing goes by: Steam's own count when it's the bigger one, as in the status line. Only
					// the minutes nocat.farm played itself went out here, so the dashboard read "45.6h played" beside a
					// status of "401.7h in" for the same game.
					return new Row(
						kv.Key,
						GameNames.Of(kv.Key),
						(int) Math.Max(kv.Value.PlayedMins, Bot.Library.MinutesOn(kv.Key)),
						Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100),
						kv.Value.Unlocked,
						kv.Value.Total,
						why.Length > 0,
						why,
						live.Contains(kv.Key),
						Current(kv.Value).ToString(),
						kv.Value.DlcHeld,
						kv.Value.Unmapped,
						kv.Value.Multiplayer);
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

			Said said = DescribeOne(app, g);
			Outcome now = Current(g);

			// Held back for DLC it doesn't own: said on every line but the one that is already about exactly that.
			if ((g.DlcHeld > 0) && (now is not (Outcome.DlcOnly or Outcome.DlcUnmapped))) {
				said = new Said("{0} ({1})", said, g.Unmapped
					? new Said("can't tell which achievements come with its DLC - {0} left alone", g.DlcHeld)
					: new Said("{0} achievement(s) are from DLC this account doesn't own - left alone", g.DlcHeld));
			}

			// The same for the multiplayer ones it skips.
			if ((g.Multiplayer > 0) && (now != Outcome.Multiplayer)) {
				said = new Said("{0} ({1})", said, new Said("{0} multiplayer ones skipped", g.Multiplayer));
			}

			return said;
		}
	}

	/// <summary>The status line for one game. Called under the gate.</summary>
	private Said DescribeOne(uint app, GameState g) {
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
			case Outcome.Multiplayer:
				return new Said("{0}: {1}/{2} - the rest are multiplayer achievements, which this account skips", GameNames.Of(app), g.Unlocked, g.Total);
			case Outcome.NoRarity:
				return new Said("{0}: waiting for Steam's figures on how many players have each achievement", GameNames.Of(app));
			case Outcome.CountersOnly:
				return new Said("{0}: {1}/{2} - the rest count something in the game, and wait for the game's own counter", GameNames.Of(app), g.Unlocked, g.Total);
			case Outcome.NothingOpen:
				return new Said("{0}: {1}/{2} - nothing left it would earn for now; looked at again in a few days", GameNames.Of(app), g.Unlocked, g.Total);
			case Outcome.DlcOnly:
				// Some of the rest only Steam can award: said, rather than calling them all DLC.
				return (g.DlcHeld > 0) && (g.Unlocked >= 0) && (g.DlcHeld < g.Total - g.Unlocked)
					? new Said("{0}: {1}/{2} - {3} are from DLC this account doesn't own, and only Steam can award the rest", GameNames.Of(app), g.Unlocked, g.Total, g.DlcHeld)
					: new Said("{0}: {1}/{2} - the rest are from DLC this account doesn't own", GameNames.Of(app), g.Unlocked, g.Total);
			case Outcome.DlcUnmapped:
				return new Said("{0}: {1}/{2} - can't tell which achievements come with its DLC - left alone", GameNames.Of(app), g.Unlocked, g.Total);
			case Outcome.DlcChecking:
				return new Said("{0}: checking which achievements come with its DLC", GameNames.Of(app));
		}

		Profile prof = ProfileFor(app);
		bool onboarding = (g.Unlocked < 0) || (g.Unlocked < prof.OnboardCount);
		int playedGate = PlayedGate(prof, onboarding, Bot.Cfg.AchievementPace);
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

		// Absent from files written before DLC was checked: 0, which reads as "not worked out yet".
		public int Reachable { get; set; }
		public int DlcHeld { get; set; }

		// Absent from older files: false, 0 and none. A 0 stamp never matches a real one, so a game called done for
		// DLC before these were kept has the DLC it owns read again once the licences are in.
		public bool Unmapped { get; set; }
		public long Licences { get; set; }
		public List<uint>? DlcOwned { get; set; }

		// Absent from older files: 0. A 0 never matches a real map, so a game held before these were kept is looked at
		// again once, the first time its map is read.
		public long MapStamp { get; set; }
		public long HoldKey { get; set; }

		// Absent from older files: false. A game held whole then (Unmapped) had it too - see Load.
		public bool Unclear { get; set; }

		// Absent from older files: null - read again the next time the game is looked at.
		public string? DlcKey { get; set; }

		// Absent from older files: 0 and none. A game stopped under rules 0 on an account whose rules aren't 0 is looked at
		// again once (see RulesChanged).
		public int Multiplayer { get; set; }
		public int Rules { get; set; }
		public Dictionary<string, DateTime>? Refused { get; set; }

		// Only read, from files written while a human-mode account left add-on-looking achievements alone: such a game is
		// let go once (see Load). Never written - always 0 now.
		public int Likely { get; set; }

		// Absent from older files: 0, MinValue, -1-ish defaults - see Load for the stops that came without an end.
		public int Counters { get; set; }
		public DateTime RecheckAt { get; set; }
		public long StuckLibMins { get; set; } = -1;
		public long StuckOwnMins { get; set; }
		public DateTime WroteOkAt { get; set; }
		public int PaceUsed { get; set; } = -1;
		public DateTime PacedFrom { get; set; }

		// Absent from older files: null. StuckOwnMins was PlayedMins then, so RanMins starts from that (see Load).
		public long? RanMins { get; set; }
		public Dictionary<string, List<DateTime>>? Strikes { get; set; }

		// Absent from older files: false - every game is looked at once for a "for having the others" achievement with
		// nothing left to wait for (everything else unlocked before metas went straight after the last one).
		public bool MetasLooked { get; set; }
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

			int released = 0;

			lock (_gate) {
				foreach (Saved s in saved) {
					_games[s.App] = new GameState {
						App = s.App,
						PlayedMins = s.PlayedMins,
						MinsAtLastUnlock = s.MinsAtLastUnlock,
						NextAllow = s.NextAllow,
						Unlocked = s.Unlocked,
						Total = s.Total,
						Last = s.Last,
						CappedAt = s.CappedAt,
						Reachable = s.Reachable,
						DlcHeld = s.DlcHeld,
						Unmapped = s.Unmapped,
						Licences = s.Licences,
						DlcOwned = s.DlcOwned ?? [],
						MapStamp = s.MapStamp,
						HoldKey = s.HoldKey,
						// Held whole before it was kept: it can't have been for anything else.
						Unclear = s.Unclear || s.Unmapped,
						DlcKey = s.DlcKey,
						Multiplayer = s.Multiplayer,
						Counters = s.Counters,
						// Without the old human-mode bit (1), which RulesOf no longer gives. Kept, every game stopped on a
						// human-mode account read as stopped under other rules and was let go once, for a sitting each.
						Rules = s.Rules & ~1,
						Refused = s.Refused ?? [],
						RecheckAt = s.RecheckAt,
						StuckLibMins = s.StuckLibMins,
						StuckOwnMins = s.StuckOwnMins,
						RanMins = s.RanMins ?? s.PlayedMins,
						WroteOkAt = s.WroteOkAt,
						PaceUsed = s.PaceUsed,
						PacedFrom = s.PacedFrom,
						Strikes = s.Strikes ?? [],
						MetaLook = !s.MetasLooked
					};

					// A file written before outcomes existed says nothing about them, and a finished or capped game
					// is only read again every 8-25 hours - so until then it would have been shown as "earning".
					// Both can be worked out from the counts already saved.
					GameState g = _games[s.App];

					// Saved before a game with DLC held back and a few Steam-only ones left was kept as held for DLC: it
					// was kept as Steam-only, and nothing ever looked at it again when the DLC was bought. Held for DLC now.
					if ((g.Last == Outcome.SteamOnly) && (g.DlcHeld > 0)) {
						g.Last = g.Unmapped ? Outcome.DlcUnmapped : Outcome.DlcOnly;
					}

					// Held whole because it couldn't be told which achievements come with an add-on it doesn't own. Such a
					// game earns anyway now, base game first - unless the owner left it alone. Let go at once, so the
					// pacer and the hunt pick it up again: left to the minute's licence look, it waited for its map to be
					// built again - up to a week. What it could reach was counted without the guesses, so that's
					// forgotten too; otherwise it could read as capped below.
					if ((g.Last == Outcome.DlcUnmapped) && !Bot.Cfg.AchievementDlcLeft.ContainsKey(s.App)) {
						g.Last = Outcome.Unknown;
						g.NextAllow = DateTime.UtcNow;
						g.Unmapped = false;
						g.Reachable = 0;
						g.DlcHeld = 0;
						released++;
					}

					// Held because a human-mode account left the add-on-looking ones alone. Nothing is held on the layout any
					// more - they're only earned last - so it is let go at once, and what it can reach is worked out again.
					if ((s.Likely > 0) && (g.Last is Outcome.DlcOnly or Outcome.Capped)) {
						g.Last = Outcome.Unknown;
						g.NextAllow = DateTime.UtcNow;
						g.Reachable = 0;
						released++;
					}

					// Stopped for counters before that stop had an end: for good, by mistake. Looked at again now.
					if ((g.Last == Outcome.CountersOnly) && (g.RecheckAt == DateTime.MinValue)) {
						g.Last = Outcome.Unknown;
						g.NextAllow = DateTime.UtcNow;
						released++;
					}

					// "Steam only" with refusals kept, from before that ended with them: it ends when the first one does.
					if ((g.Last == Outcome.SteamOnly) && (g.RecheckAt == DateTime.MinValue) && (g.Refused.Count > 0)) {
						g.RecheckAt = g.Refused.Values.Min() + RefusedFor;
					}

					if ((g.Last == Outcome.Unknown) && (g.Total > 0) && (g.Unlocked >= 0)) {
						int ceiling = Math.Clamp(Bot.Cfg.AchievementMaxCompletionPct, 1, 100);
						int ceilingCount = CeilingCount(Reachable(g), ceiling, ProfileFor(s.App).OnboardCount);

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

			if (released > 0) {
				Log.Debug($"{released} game(s) held on a guess about add-ons, or stopped for counters for good, are let go - they're looked at again", Bot.Name);
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the achievement state: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);
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
						App = app,
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
			Log.Debug(new Said("couldn't import the ArchiSteamFarm achievement state: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);
		}
	}

	/// <summary>
	/// Held from the copy to the write: an undo on the dashboard saves from its own thread while the minute's tick
	/// saves from this one. Without it, a copy taken earlier could be written last and put back what the later one changed.
	/// </summary>
	private readonly Lock _saveGate = new();

	private void Save() {
		lock (_saveGate) {
			SaveInOrder();
		}
	}

	private void SaveInOrder() {
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
					CappedAt = kv.Value.CappedAt,
					Reachable = kv.Value.Reachable,
					DlcHeld = kv.Value.DlcHeld,
					Unmapped = kv.Value.Unmapped,
					Licences = kv.Value.Licences,
					DlcOwned = kv.Value.DlcOwned,
					MapStamp = kv.Value.MapStamp,
					HoldKey = kv.Value.HoldKey,
					Unclear = kv.Value.Unclear,
					DlcKey = kv.Value.DlcKey,
					Counters = kv.Value.Counters,
					RecheckAt = kv.Value.RecheckAt,
					StuckLibMins = kv.Value.StuckLibMins,
					StuckOwnMins = kv.Value.StuckOwnMins,
					RanMins = kv.Value.RanMins,
					WroteOkAt = kv.Value.WroteOkAt,
					PaceUsed = kv.Value.PaceUsed,
					PacedFrom = kv.Value.PacedFrom,
					MetasLooked = !kv.Value.MetaLook,
					Multiplayer = kv.Value.Multiplayer,
					Rules = kv.Value.Rules,
					// A copy: the tick changes the live one while this is written out. Only the last week's - older ones
					// are tried again anyway.
					Refused = kv.Value.Refused.Where(static r => DateTime.UtcNow - r.Value < RefusedFor).ToDictionary(static r => r.Key, static r => r.Value),
					// The same: a copy, and only what still counts.
					Strikes = kv.Value.Strikes
						.Select(static s => (s.Key, Times: s.Value.Where(static t => DateTime.UtcNow - t <= StrikesKept).ToList()))
						.Where(static s => s.Times.Count > 0)
						.ToDictionary(static s => s.Key, static s => s.Times)
				}).ToList();
			}

			string path = PathFor(Bot.Name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			// Saved every minute's tick, so a file that can't be written is said once rather than every minute.
			Log.DebugOnChange($"cheevo-save:{Bot.Name}", $"couldn't save the achievement state: {Log.Describe(e)}", Bot.Name);
		}
	}
}
