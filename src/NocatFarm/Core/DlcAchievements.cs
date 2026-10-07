using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using SteamKit2;

namespace NocatFarm.Core;

/// <summary>
/// Which of a game's achievements come with a DLC, and whether this account owns that DLC.
/// </summary>
/// <remarks>
/// Plenty of games keep their DLC achievements in the base game's own list: Fallout, Borderlands, Civilization, and
/// Call of Duty's hub (1938090), where Modern Warfare II and III are DLC with dozens of achievements each. The
/// pacer, the hunt, 'cheevo unlock' and unlock-everything all pick from that whole list - and an achievement from a
/// DLC the account doesn't own is one nobody could have earned. It is on the profile for anybody to see, so it is
/// never unlocked. This is the one place that works out which ones those are.
///
/// Where it comes from, all of it public:
///   • the game's DLC: Steam's own app info (PICS <c>extended/listofdlc</c>) together with the store's
///     <c>data.dlc</c>. Neither is complete on its own - Call of Duty's store list leaves out Modern Warfare II and
///     III, and its app info leaves out every DLC added since - so both, joined.
///   • for each DLC, the store's <c>achievements.total</c> (how many it has) and <c>achievements.highlighted</c>
///     (the first ten or so, by name, in the game's own order). Most DLC have none.
///   • the game's full list in order, from <c>IPlayerService/GetGameAchievements</c> (no key).
/// A DLC's achievements sit together in that order, starting at its first highlighted one, so its block is that
/// position and the next <c>total - 1</c>. See <see cref="Block"/> for what happens when the names don't line up.
/// Not everything in a game's DLC list is a DLC: Portal lists Portal with RTX, a game of its own with its own copy of
/// Portal's achievements. An entry whose own page isn't a DLC's (<see cref="NotDlc"/>), and figures as big as the game's
/// whole list (<see cref="WholeGame"/>), say nothing about this game's achievements.
///
/// Older games (Fallout: New Vegas, Borderlands 2, Civilization V) have DLC achievements in their list but the store
/// says nothing about which: their DLC pages carry no achievement figures at all. There the only clue is the wording,
/// so an achievement whose name or description names one of the game's DLC ("Complete Old World Blues") counts as that
/// DLC's too - see <see cref="DistinctName"/>. It can only ever hold more back, never less.
///
/// But the wording can't be trusted to find all of them, and the store only says which achievements come with a DLC
/// for newer DLC. So a DLC with no achievement figures on the store (bar the plainly harmless: a soundtrack, an
/// artbook, wallpapers, a pack of skins or COD Points - see <see cref="Cosmetic"/>), no store page of its own (Black
/// Ops 6 and 7 open Call of Duty's page), no store page at all, or figures whose names are nowhere in the list, means
/// the game can't be mapped. On an account without that DLC, only achievements certainly inside the block of a DLC it
/// DOES own are unlocked (<see cref="Map.Sure"/>), and the rest of the game is left alone. Holding a game costs nothing - the hunt moves on. Unlocking
/// one wrong achievement can't be taken back.
///
/// That is the rule on its own. What the pacer, the hunt and 'cheevo unlock' go by is gentler: a game isn't held whole
/// on a guess. Every DLC that can't be placed is counted as owned (see <see cref="Counted"/>), so the game earns anyway.
/// Something is held only on certain evidence, the same on every account, human mode or not:
///   • the exact block of a DLC the account doesn't own for good (the store's figures and names lined up);
///   • an achievement whose name or description names a DLC it doesn't own;
///   • "DLC" in the API name (Cuphead's CompleteWorldDLC) while the account is missing any of the game's add-ons that
///     aren't only looks (<see cref="NamedDlcHeld"/>).
/// The game's own layout - its first block of achievement stats, then a jump - is only ever used for the ORDER: what
/// comes after the jump looks like an add-on's (<see cref="View.AddOnLikely"/>) and is earned after every eligible
/// base-game one, never held. Read off real games it is wrong both ways: Fallout: New Vegas and Borderlands 2 put their
/// add-ons straight after the base game with no gap, and PAYDAY 2, Dead by Daylight and Left 4 Dead 2 put free updates
/// of the base game after one. Only a game the owner asked to have left alone ('dlc leave', kept in AchievementDlcLeft)
/// keeps to the rule above. Call of Duty on an account with Modern Warfare II but not III never has III's 39 unlocked.
///
/// Built lazily, only for games something is about to unlock in, one game at a time, slowly (the store answers about
/// two hundred questions every five minutes for everything on this PC, and a game like Call of Duty has close to a
/// hundred DLC). Kept on disk for a week. Until a game's map is built nothing in it is unlocked - "don't know yet" is
/// never taken as "base game".
/// </remarks>
public static class DlcAchievements {
	/// <summary>How long a built map is trusted before it is built again. A game gains DLC now and then, not daily.</summary>
	private static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

	/// <summary>
	/// The least time between two store questions from here. The catalogue asks every 1.5 seconds at most and shares
	/// the same queue, so together the two never pass about forty a minute - the store's own limit, for the whole PC.
	/// </summary>
	private static readonly TimeSpan StoreGap = TimeSpan.FromSeconds(3);

	/// <summary>
	/// The rules a map is built by now. 2: skins, COD Points and the like no longer hold a game (<see cref="Cosmetic"/>),
	/// and a DLC with an achievement count but no names is held whatever it's called. 3: the game's own name is taken off
	/// a DLC's before it is called cosmetic ("Avatar: Frontiers of Pandora - The Sky Breaker" is not an avatar pack), a
	/// word that can mean something else only counts with a pack after it, more words say a DLC has something to play,
	/// and a game that can't be fully mapped keeps the certain part of each block (<see cref="Map.Sure"/>). 4 and 5: an
	/// amount of money only makes a currency pack at the end of the name or before a pack word, with two digits or more
	/// ("1849 Gold Rush" and "The 7 Gems" are stories). 6: the map keeps the game's whole DLC list, which a game left
	/// alone ('dlc leave') is remembered with - one built before had only the DLC with achievements. 7: an entry in the
	/// game's DLC list whose own store page isn't a DLC's (Portal with RTX is a game of its own) is no DLC at all
	/// (<see cref="NotDlc"/>), and figures as big as the game's whole list are a separate game's or edition's, never a
	/// DLC's (<see cref="WholeGame"/>) - Portal was held whole on an account without Portal with RTX. 8: only from a
	/// page that isn't a DLC page - a real DLC can be nearly all of a game whose own part is small, and 7 unlocked its
	/// achievements on an account without it. A map saved
	/// under older rules (0: before this was kept) is built again at once, and used as it is for the minute or two that
	/// takes; a game held on it is looked at again once the new map says something else.
	/// </summary>
	public const int RuleNow = 8;

	/// <summary>
	/// How long a map built while Steam didn't answer for some DLC names is used before it is built again. Without the
	/// names those DLC can't be told from ones with achievements, so the game is held - for an hour, not a week.
	/// </summary>
	private static readonly TimeSpan HurriedFor = TimeSpan.FromHours(1);

	/// <summary>A build that failed (store down, Steam didn't answer) isn't tried again for this long.</summary>
	private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

	private static readonly HttpClient Http = Browser.Anonymous(TimeSpan.FromSeconds(20));

	// ── what is kept ─────────────────────────────────────────────────────────

	/// <summary>One DLC (or several ids for the same one) whose achievements are in the game's list.</summary>
	public sealed class Group {
		/// <summary>The DLC's app id - what it is known by here.</summary>
		public uint App { get; set; }

		/// <summary>
		/// Every id that means owning it: the DLC itself, and the store app its page really is when that differs.
		/// Call of Duty's 1962660 is the store's 3595230 - owning either is owning Modern Warfare II.
		/// </summary>
		public List<uint> Ids { get; set; } = [];

		public string Name { get; set; } = "";

		/// <summary>How many achievements the store says it has (0 when only its name gave it away).</summary>
		public int Total { get; set; }

		/// <summary>How many of the game's achievements are counted as this DLC's.</summary>
		public int Located { get; set; }

		/// <summary>The store's names lined up exactly with a block of the right size.</summary>
		public bool Exact { get; set; }

		/// <summary>Some of its achievements were found only by naming the DLC.</summary>
		public bool ByName { get; set; }

		/// <summary>
		/// Why this DLC's achievements, if it has any, can't be placed in the list. Anything but <see cref="Doubt.None"/>:
		/// on an account without it, nothing in the game is unlocked but what is certainly inside the block of a DLC it
		/// owns (<see cref="Map.Sure"/>).
		/// </summary>
		public Doubt Unsure { get; set; }
	}

	/// <summary>Why a DLC can't be placed in the game's list.</summary>
	/// <remarks>Numbered explicitly: written to the saved maps as numbers.</remarks>
	public enum Doubt {
		/// <summary>Placed, or plainly harmless (a soundtrack, an artbook).</summary>
		None = 0,

		/// <summary>Its store page is the base game's own (Black Ops 6 opens Call of Duty's): nothing is said about it.</summary>
		BasePage = 1,

		/// <summary>No store page at all (delisted, or never sold on its own): nothing is known about it.</summary>
		Delisted = 2,

		/// <summary>
		/// Its page gives no achievement figures. Most DLC have none, but older DLC with achievements (Borderlands 2,
		/// Fallout: New Vegas, Cuphead's The Delicious Last Course) have no figures either, and the two can't be told
		/// apart - the "Steam Achievements" tag is copied from the base game onto every DLC page, gun packs included.
		/// </summary>
		NoFigures = 3,

		/// <summary>The page names some of its achievements, and they are nowhere in the game's list.</summary>
		NotFound = 4
	}

	/// <summary>A game's DLC achievements: which achievement (by API name, lower case) needs which DLC.</summary>
	public sealed class Map {
		public uint App { get; set; }
		public DateTime BuiltAt { get; set; }

		/// <summary>
		/// Which version of the rules built it (<see cref="RuleNow"/>). A map from older rules is built again at once -
		/// from what is already known about its DLC, so without asking the store again - and used as it is until then.
		/// </summary>
		public int Rule { get; set; }

		/// <summary>
		/// Built while Steam didn't answer for the names of DLC with no store page - those were taken as ones that may
		/// have achievements. Built again after <see cref="HurriedFor"/>, not a week.
		/// </summary>
		public bool Hurried { get; set; }

		/// <summary>How many DLC the game has at all, with achievements or not.</summary>
		public int DlcCount { get; set; }

		/// <summary>How many of those the store has a DLC page for - a page it would give achievement figures on.</summary>
		public int OnStore { get; set; }

		/// <summary>Whether the store gave achievement figures for any DLC of this game at all.</summary>
		public bool StoreSays { get; set; }

		public int SchemaCount { get; set; }
		public List<Group> Groups { get; set; } = [];

		/// <summary>
		/// API name (lower case) to the DLC it needs. More than one when two DLC could claim it - then every one of
		/// them has to be owned. An achievement not in here is the base game's - unless a DLC is <see cref="Group.Unsure"/>.
		/// </summary>
		public Dictionary<string, List<uint>> Owners { get; set; } = [];

		/// <summary>
		/// API name (lower case) to the DLC whose block it is certainly inside: the store's names lined up exactly with a
		/// block of the right size (<see cref="Group.Exact"/>), and it is in that block as the store gives it - not one
		/// of the hidden ones taken in just in front, not one found only by naming the DLC, not from a union of blocks
		/// that disagree. <see cref="Owners"/> is what holds an achievement back; this is what may let one go in a game
		/// that can't be fully mapped. Null on maps saved before it was kept: then nothing in such a game is certain,
		/// and it is all held until the map is built again.
		/// </summary>
		public Dictionary<string, List<uint>>? Sure { get; set; }

		/// <summary>
		/// Every achievement (API name, lower case) in the list the map was built from. One that isn't in here came
		/// after it - most likely with a new DLC - and nothing is known about it: it waits until the map is built again.
		/// Null on maps saved before this was kept; those are built again before anything in them is unlocked.
		/// </summary>
		public List<string>? Names { get; set; }

		/// <summary>
		/// Every id of every DLC the game has - Steam's list and the store's, and the store app a DLC's page really is -
		/// with achievements or not. The same however the map was built: a hurried build and a full one group the DLC
		/// differently, but the game's DLC are the same ones. What 'dlc leave' is remembered with
		/// (<see cref="LicenceKey"/>). Null on maps saved before it was kept: then the ids in <see cref="Groups"/>.
		/// </summary>
		public List<uint>? Dlc { get; set; }

		private HashSet<string>? _names;

		/// <summary>Was this achievement in the list the map was built from.</summary>
		public bool Covers(string apiName) {
			if (Names == null) {
				return false;
			}

			HashSet<string> names = _names ??= new HashSet<string>(Names, StringComparer.Ordinal);

			return names.Contains(apiName.ToLowerInvariant());
		}

		/// <summary>Any DLC at all that matters here: placed in the list, or that couldn't be.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public bool HasDlc => Groups.Count > 0;
	}

	/// <summary>What the store said about one DLC, kept so a build that stopped half way doesn't ask again.</summary>
	private sealed class Facts {
		public DateTime At { get; set; }
		public bool Listed { get; set; }
		public uint StoreApp { get; set; }
		public string Type { get; set; } = "";
		public string Name { get; set; } = "";
		public int Total { get; set; }
		public List<string> Highlighted { get; set; } = [];
	}

	private sealed class Saved {
		public Dictionary<uint, Map> Maps { get; set; } = [];
		public Dictionary<uint, Facts> Dlc { get; set; } = [];
	}

	private static readonly Dictionary<uint, Map> Maps = [];
	private static readonly Dictionary<uint, Facts> DlcFacts = [];
	private static readonly Lock Data = new();
	private static bool _loaded;

	private static string PathOf => Path.Combine(ConfigStore.ConfigDir, "state", "dlc-achievements.json");

	// ── the rule ─────────────────────────────────────────────────────────────

	/// <summary>Why an achievement is left alone, if it is.</summary>
	public enum Hold {
		/// <summary>The base game's, or from DLC this account owns: fine.</summary>
		None = 0,

		/// <summary>From DLC this account doesn't own (or can't be shown to own).</summary>
		NotOwned = 1,

		/// <summary>
		/// Not worked out for this game yet - nothing is unlocked until it is. Also an achievement that wasn't in the list
		/// when the map was built, and everything in a game with DLC while the account's licences couldn't be read.
		/// </summary>
		Checking = 2,

		/// <summary>
		/// The game has a DLC this account doesn't own whose achievements can't be placed, and this one isn't inside the
		/// block of a DLC it does own - it could be that DLC's.
		/// </summary>
		Unmapped = 3
	}

	/// <summary>
	/// The rule itself. No map yet, or an achievement the map has never seen: checking, so no. Claimed by DLC: every DLC
	/// it could belong to has to be among the ones this account owns. Claimed by none: the base game's - unless the game
	/// has a DLC this account doesn't own whose achievements couldn't be placed, and then it could be that one's.
	/// </summary>
	/// <remarks>
	/// In a game like that, owning the DLC that claims an achievement isn't enough: it has to be certainly inside that
	/// DLC's block (<see cref="Map.Sure"/>). A block is drawn generously on purpose - the hidden achievements just in
	/// front are taken in, and names that disagree give the union of every block they say - which is right for holding
	/// back, not for letting go. Hidden achievements just in front of an owned DLC's block can as well be the DLC that
	/// can't be placed: base 0-4, a DLC nobody can place at 5-7 (hidden), an owned DLC at 8-11. Taken as the owned
	/// one's, 5-7 were unlocked on an account without the other DLC.
	/// </remarks>
	public static Hold HoldOf(Map? map, string apiName, IReadOnlySet<uint> ownedGroups) {
		if ((map == null) || !map.Covers(apiName)) {
			return Hold.Checking;
		}

		string key = apiName.ToLowerInvariant();
		bool unplaced = Unplaced(map, ownedGroups);

		if (map.Owners.TryGetValue(key, out List<uint>? needs) && (needs.Count > 0)) {
			if (!needs.All(ownedGroups.Contains)) {
				return Hold.NotOwned;
			}

			return !unplaced || ((map.Sure != null) && map.Sure.TryGetValue(key, out List<uint>? sure) && sure.Any(ownedGroups.Contains))
				? Hold.None
				: Hold.Unmapped;
		}

		return unplaced ? Hold.Unmapped : Hold.None;
	}

	/// <summary>
	/// Does the game have a DLC this account doesn't own whose achievements can't be placed? Then only what is certainly
	/// inside the block of a DLC it owns is known to be safe, and the rest of the game is left alone. Never for a game with
	/// no achievements at all: there is nothing a DLC could have added, and 'dlc &lt;account&gt;' listed it as uncertain.
	/// </summary>
	public static bool Unplaced(Map? map, IReadOnlySet<uint> ownedGroups) =>
		(map != null) && (map.Names is not { Count: 0 }) && map.Groups.Any(g => (g.Unsure != Doubt.None) && !ownedGroups.Contains(g.App));

	/// <summary>
	/// The DLC this account owns, of the ones in <paramref name="map"/>. Only what <paramref name="owns"/> says yes to -
	/// a licence list that couldn't be read says no to everything, and then nothing from any DLC is unlocked.
	/// </summary>
	public static HashSet<uint> OwnedGroups(Map? map, Func<uint, bool> owns) =>
		map == null ? [] : [.. map.Groups.Where(g => g.Ids.Any(owns)).Select(static g => g.App)];

	/// <summary>
	/// The ids of the game's DLC this account has for good (<paramref name="owns"/>), as one string in order. Only the
	/// licences, never how the map grouped the DLC - so it reads the same after the map is built again (in a hurry, or in
	/// full). What 'dlc leave' is remembered with.
	/// </summary>
	public static string LicenceKey(Map? map, Func<uint, bool> owns) {
		if (map == null) {
			return "";
		}

		IEnumerable<uint> ids = map.Dlc ?? map.Groups.SelectMany(static g => g.Ids.Append(g.App));

		return string.Join(",", ids.Where(owns).Distinct().Order());
	}

	/// <summary>
	/// The DLC the rule counts as owned: what the licences say - and, when the game earns anyway (every game but one the
	/// owner left alone), every DLC that can't be placed as well. That covers only what nobody can know: which
	/// achievements come with a DLC Steam says nothing about. A DLC that IS placed is still judged by the licences, so its
	/// block stays held on an account without it - Call of Duty on an account with Modern Warfare II but not III never
	/// has III's 39 unlocked.
	/// </summary>
	public static HashSet<uint> Counted(Map? map, IReadOnlySet<uint> licensed, bool anyway) =>
		anyway && (map != null)
			? [.. licensed, .. map.Groups.Where(static g => g.Unsure != Doubt.None).Select(static g => g.App)]
			: [.. licensed];

	/// <summary>
	/// Is this achievement certainly from a DLC this account doesn't own: inside the block of a DLC whose store names
	/// lined up exactly (<see cref="Map.Sure"/>)? Only then is it said to be "from DLC this account doesn't own". One
	/// held on a guess - a block drawn generously, a DLC named in its description, a game that can't be mapped - is
	/// "can't tell", not that.
	/// </summary>
	public static bool CertainlyNotOwned(Map? map, string apiName, IReadOnlySet<uint> ownedGroups) =>
		(map?.Sure != null) && map.Sure.TryGetValue(apiName.ToLowerInvariant(), out List<uint>? sure) && sure.Any(d => !ownedGroups.Contains(d));

	/// <summary>
	/// Is the account missing any of the game's add-ons that may bring something to play? Every DLC in the map's groups
	/// is one: placed, unplaceable, or named in an achievement. Soundtracks, artbooks, skin packs and in-game money never
	/// make a group (<see cref="Harmless"/>), so a game whose only missing DLC are those isn't missing anything here.
	/// </summary>
	public static bool MissingAny(Map? map, IReadOnlySet<uint> licensed) => (map != null) && map.Groups.Any(g => !licensed.Contains(g.App));

	/// <summary>
	/// "DLC" in its API name, and the account is missing an add-on that may bring something to play: held. Cuphead's
	/// CompleteWorldDLC on an account without The Delicious Last Course. It's the one guess certain enough to hold on -
	/// a game names an achievement DLC because it comes with one.
	/// </summary>
	public static bool NamedDlcHeld(Map? map, string apiName, IReadOnlySet<uint>? licensed) =>
		(licensed != null) && NamedDlc(apiName) && MissingAny(map, licensed);

	/// <summary>
	/// The rule with every certain hold in: <see cref="HoldOf"/>; an achievement a DLC claims - by its exact block, or by
	/// being named in its text ("Complete Old World Blues") - judged by the licences, even a DLC Steam doesn't place (those
	/// are only counted as owned for what nobody can tell, never for one that names them); and an achievement named "DLC"
	/// while the account is missing an add-on (<see cref="NamedDlcHeld"/>). <paramref name="licensed"/> null: only
	/// <see cref="HoldOf"/>.
	/// </summary>
	public static Hold EffectiveHold(Map? map, string apiName, IReadOnlySet<uint> owned, IReadOnlySet<uint>? licensed) {
		Hold hold = HoldOf(map, apiName, owned);

		if ((hold != Hold.None) || (licensed == null) || (map == null)) {
			return hold;
		}

		bool claimed = map.Owners.TryGetValue(apiName.ToLowerInvariant(), out List<uint>? needs) && needs.Any(d => !licensed.Contains(d));

		return claimed || NamedDlcHeld(map, apiName, licensed) ? Hold.NotOwned : hold;
	}

	/// <summary>
	/// How far apart two achievement stats have to be, in stat numbers, to start a new block - for the ORDER only, never
	/// to hold anything (free updates of the base game are put after a jump too: PAYDAY 2, Dead by Daylight). A game keeps its
	/// achievements as bits of numbered stats, 32 to a stat, and the base game's fill stats 1, 2, 3... in a row. An
	/// add-on added later is usually given stats of its own further on: Cuphead's base game is stat 2 and The Delicious
	/// Last Course stat 5; Call of Duty's are 1, then 68, 97, 135. One empty stat is not a jump - Stardew Valley's own
	/// achievements are in stats 1 and 3, Stellaris's in 4 and 6 - so a block ends only where two or more are skipped.
	/// </summary>
	public const int BlockJump = 3;

	/// <summary>
	/// The base game's block, read from the game's own layout: the achievements (API name, lower case) from the first
	/// stat up to the first jump of <see cref="BlockJump"/> or more. A game with no such jump is one block, all of it.
	/// Fallout: New Vegas, Borderlands 2 and Cyberpunk 2077 put their add-ons straight after the base game, with no gap,
	/// and there this can't tell them apart - only a name can (<see cref="Map.Owners"/>, or "DLC" in the API name).
	/// </summary>
	public static HashSet<string> BaseBlock(IReadOnlyCollection<Achievement> all) {
		List<uint> stats = [.. all.Select(static a => a.StatId).Distinct().Order()];
		HashSet<string> block = new(StringComparer.Ordinal);

		if (stats.Count == 0) {
			return block;
		}

		uint last = stats[0];

		foreach (uint s in stats.Skip(1)) {
			if (s - last >= BlockJump) {
				break;
			}

			last = s;
		}

		foreach (Achievement a in all.Where(a => a.StatId <= last)) {
			block.Add(a.Name.ToLowerInvariant());
		}

		return block;
	}

	/// <summary>"DLC" in the API name: Cuphead's CompleteWorldDLC and SRankAnyDLC are The Delicious Last Course's.</summary>
	public static bool NamedDlc(string apiName) => apiName.Contains("dlc", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Which achievements (API name, lower case) belong to an add-on, owned or not, as far as can be told - and which
	/// add-on. Every one a DLC claims in the map (its exact block, or a DLC named in its text): that DLC, by id. And in a
	/// game with an add-on Steam doesn't place (<see cref="Doubt"/>), also every one outside the game's first block
	/// (<see cref="BaseBlock"/>) and every one with "DLC" in its API name: "?", an add-on nobody can name. There nothing
	/// better is known, and Steam has a game's own achievements first. A fully mapped game is left to its map - Stardew
	/// Valley's second block is its own. The "?" ones only go last; they are never held for it.
	/// </summary>
	public static Dictionary<string, string> AddOnParts(Map? map, IReadOnlyCollection<Achievement> all) {
		Dictionary<string, string> addOn = new(StringComparer.Ordinal);

		if (map == null) {
			return addOn;
		}

		bool guess = map.Groups.Any(static g => g.Unsure != Doubt.None);
		HashSet<string> baseBlock = guess ? BaseBlock(all) : [];

		foreach (Achievement a in all) {
			string key = a.Name.ToLowerInvariant();

			if (map.Owners.TryGetValue(key, out List<uint>? needs) && (needs.Count > 0)) {
				addOn[key] = string.Join("+", needs.Order());
			} else if (guess && (!baseBlock.Contains(key) || NamedDlc(a.Name))) {
				addOn[key] = "?";
			}
		}

		return addOn;
	}

	/// <summary>One account's view of one game: the map, and which of its DLC the account owns.</summary>
	public sealed class View {
		public Map? Map { get; init; }

		/// <summary>The DLC the rule counts as owned - see <see cref="Counted"/>. Unless the game is left alone, every one that can't be placed too.</summary>
		public HashSet<uint> Owned { get; init; } = [];

		/// <summary>The DLC its licences say it owns - for saying what it really has, and which achievements are a guess.</summary>
		public HashSet<uint> Licensed { get; init; } = [];

		/// <summary>
		/// What the certain holds go by (<see cref="EffectiveHold"/>): the licences - which in a game left alone are what it
		/// counts as owned anyway.
		/// </summary>
		public IReadOnlySet<uint> ByLicence => Trusted ? Licensed : Owned;

		/// <summary>
		/// Which of the game's DLC ids it has for good, as one string (<see cref="LicenceKey"/>) - null when the licences
		/// weren't read. What 'dlc leave' is remembered with.
		/// </summary>
		public string? LicenceKey { get; init; }

		/// <summary>
		/// The game earns anyway: the DLC that can't be placed count as owned, the ones that can still go by the licences
		/// - see <see cref="Counted"/>. True for every game but one the owner left alone (AchievementDlcLeft).
		/// </summary>
		public bool Trusted { get; init; }

		/// <summary>
		/// The account's licences were read. When they couldn't be (Steam didn't answer, the list hasn't arrived yet) a
		/// game with DLC is "checking": nothing in it is unlocked, and nothing about it is concluded either - not
		/// "owns none of its DLC", which would call the game done for this account.
		/// </summary>
		public bool OwnershipRead { get; init; } = true;

		/// <summary>The account's <see cref="Bot.LicenseStamp"/> from just before its licences were read for this.</summary>
		public long Licences { get; init; }

		/// <summary>Who asked, so an achievement the map has never seen can have the game worked out again.</summary>
		public Bot? Bot { get; init; }

		/// <summary>The map is built and the licences were read - every achievement can be answered for.</summary>
		public bool Known => (Map != null) && (OwnershipRead || !Map.HasDlc);

		/// <summary>
		/// The game can't be fully mapped and this account doesn't own the DLC that stops it: everything outside the
		/// blocks of DLC it owns is left alone.
		/// </summary>
		public bool Unmapped => Known && Unplaced(Map, Owned);

		/// <summary>
		/// The game has add-ons this account doesn't own whose achievements Steam doesn't place - whether it earns anyway
		/// or was left alone. Such a game can be left alone ('dlc leave').
		/// </summary>
		public bool Unclear => Known && Unplaced(Map, Licensed);

		/// <summary>
		/// Allowed, but only because the game earns anyway: by the licences alone it would be held - it may be the base
		/// game's, or come with an add-on Steam doesn't place. The pacer takes these after every one that is certainly the
		/// base game's (or certainly from an add-on it owns), so if some do come with an add-on it doesn't own, they come
		/// last - like a player who finishes the base game first. 'cheevo list' marks them [?].
		/// </summary>
		public bool Uncertain(Achievement a) =>
			Known && Trusted && (Map != null) && (EffectiveHold(Map, a.Name, Owned, ByLicence) == Hold.None) && (HoldOf(Map, a.Name, Licensed) != Hold.None);

		/// <summary>
		/// Which of the game's achievements (API name, lower case) belong to an add-on, owned or not, as far as can be
		/// told - see <see cref="AddOnParts"/>.
		/// </summary>
		public HashSet<string> AddOnSet(IReadOnlyCollection<Achievement> all) => new(AddOnParts(Map, all).Keys, StringComparer.Ordinal);

		/// <summary>
		/// Allowed only because the game earns anyway (<see cref="Uncertain"/>), and it looks like an add-on's: outside the
		/// game's first block of achievements. Earned only after every eligible base-game one, on every account - never
		/// held for it: the layout is a guess, and free updates of the base game sit after a jump as often as add-ons do.
		/// One that is uncertain but sits in the base game's block is taken as the base game's - Steam has the game's own
		/// achievements first.
		/// </summary>
		public bool AddOnLikely(Achievement a, IReadOnlySet<string> addOn) => Uncertain(a) && addOn.Contains(a.Name.ToLowerInvariant());

		/// <summary>Held only because it has "DLC" in its API name and the account is missing an add-on (<see cref="NamedDlcHeld"/>).</summary>
		public bool HeldForName(Achievement a) =>
			Known && (Map != null) && (HoldOf(Map, a.Name, Owned) == Hold.None) && NamedDlcHeld(Map, a.Name, ByLicence);

		public Hold Of(Achievement a) {
			if (!Known) {
				return Hold.Checking;
			}

			Hold hold = EffectiveHold(Map, a.Name, Owned, ByLicence);

			// One the map has never seen: a DLC added since, most likely. Worked out again soon, not in a week.
			if ((hold == Hold.Checking) && (Bot != null) && (Map != null)) {
				Unfamiliar(Bot, Map);
			}

			return hold;
		}

		/// <summary>May this one be unlocked, as far as DLC goes.</summary>
		public bool Allows(Achievement a) => Of(a) == Hold.None;

		/// <summary>
		/// Is what this was worked out from still so: the account's licences the same, and the game still left alone (or
		/// still not)? Asked right before a write - a DLC refunded or the game left alone since, and what was allowed then
		/// may not be now. A game with no DLC has nothing to go stale.
		/// </summary>
		public bool StillSo(Bot bot) =>
			(Map is not { HasDlc: true } map) || ((bot.LicenseStamp == Licences) && (!bot.Cfg.AchievementDlcLeft.ContainsKey(map.App) == Trusted));

		/// <summary>Still locked, and from DLC this account doesn't own - or, in a game that can't be mapped, maybe.</summary>
		public int HeldIn(AchievementSet set) => set.All.Count(a => !a.Unlocked && (Of(a) is Hold.NotOwned or Hold.Unmapped));

		/// <summary>
		/// Already unlocked although it's certainly from DLC this account doesn't own - not something this app did. Only
		/// what is known exactly (<see cref="Certain"/>): one held on a guess may well be the base game's.
		/// </summary>
		public int UnlockedWithout(AchievementSet set) => set.All.Count(a => a.Unlocked && (Of(a) == Hold.NotOwned) && Certain(a));

		/// <summary>Certainly from a DLC this account doesn't own - see <see cref="CertainlyNotOwned"/>.</summary>
		public bool Certain(Achievement a) => Known && CertainlyNotOwned(Map, a.Name, Owned);

		/// <summary>Is this achievement certainly inside this DLC's block (<see cref="Map.Sure"/>)?</summary>
		public bool SurelyIn(Achievement a, uint group) =>
			Map?.Sure is { } sure && sure.TryGetValue(a.Name.ToLowerInvariant(), out List<uint>? dlc) && dlc.Contains(group);

		/// <summary>
		/// How many of the game's achievements are left alone for DLC, locked or not: the ones from DLC this account
		/// doesn't own, and in a game that can't be mapped, everything else outside the blocks of DLC it owns.
		/// </summary>
		public int NotOwnedCount => Known && (Map!.Names is { } names)
			? names.Count(n => EffectiveHold(Map, n, Owned, ByLicence) is Hold.NotOwned or Hold.Unmapped)
			: Map?.Owners.Count(kv => !kv.Value.All(Owned.Contains)) ?? 0;

		/// <summary>The DLC an achievement needs that this account doesn't own, by name.</summary>
		public List<string> Missing(Achievement a) {
			if (Map is not { } map) {
				return [];
			}

			// Held although every DLC that claims it is owned: it could as well be one that can't be placed.
			if (Of(a) == Hold.Unmapped) {
				return Unplaceable;
			}

			if (map.Owners.TryGetValue(a.Name.ToLowerInvariant(), out List<uint>? needs) && (needs.Count > 0)) {
				return [.. needs.Where(d => !Owned.Contains(d) || !ByLicence.Contains(d)).Select(d => NameOf(map, d)).Distinct()];
			}

			// Named "DLC" while it's missing add-ons: those add-ons, since any of them may be the one.
			return HeldForName(a) ? [.. map.Groups.Where(g => !ByLicence.Contains(g.App)).Select(g => NameOf(map, g.App))] : [];
		}

		/// <summary>The DLC this account doesn't own that stop the game being mapped, by name.</summary>
		public List<string> Unplaceable => Map is { } map
			? [.. map.Groups.Where(g => (g.Unsure != Doubt.None) && !Owned.Contains(g.App)).Select(g => NameOf(map, g.App))]
			: [];

		/// <summary>Which of the game's DLC groups an achievement belongs to.</summary>
		public bool In(Achievement a, uint group) =>
			Map is { } map && map.Owners.TryGetValue(a.Name.ToLowerInvariant(), out List<uint>? needs) && needs.Contains(group);

		private static string NameOf(Map map, uint dlc) => DlcName(map, dlc);
	}

	/// <summary>A DLC's name: the store's, or Steam's when the store has none (Black Ops 6 opens Call of Duty's page).</summary>
	public static string DlcName(Map map, uint dlc) =>
		map.Groups.FirstOrDefault(g => g.App == dlc)?.Name is { Length: > 0 } n ? n : GameNames.Of(dlc);

	/// <summary>How old a map has to be before an achievement it has never seen has the game built again.</summary>
	private static readonly TimeSpan UnfamiliarAfter = TimeSpan.FromHours(1);

	/// <summary>
	/// An achievement the map wasn't built with turned up: have the game worked out again - unless it was only just
	/// built, when the public list simply doesn't have it yet. It waits, still held, and is asked about again in an hour.
	/// </summary>
	private static void Unfamiliar(Bot bot, Map map) {
		if ((map.Names == null) || (DateTime.UtcNow - map.BuiltAt > UnfamiliarAfter)) {
			Request(bot, map.App, urgent: true);
		}
	}

	/// <summary>
	/// This account's view of a game. With no map yet the build is asked for, and waited on for up to
	/// <paramref name="wait"/> (zero: not at all) - long enough for a game with no DLC, not for Call of Duty.
	/// A map past its week is still used while a fresh one is built.
	/// </summary>
	public static async Task<View> ViewAsync(Bot bot, uint app, TimeSpan wait, CancellationToken ct = default) {
		Map? map = Current(app);

		if (Stale(map)) {
			Request(bot, app, urgent: true);
		}

		if ((map == null) && (wait > TimeSpan.Zero)) {
			try {
				await WhenDone(app).WaitAsync(wait, ct).ConfigureAwait(false);
			} catch (TimeoutException) {
				// Still building - the caller says so.
			}

			map = Current(app);
		}

		// Read before the licences are: one that lands while they're being read then shows up as a change later.
		long stamp = bot.LicenseStamp;

		// Every game earns anyway - the add-ons Steam doesn't place counted as owned - but one the owner asked to have left
		// alone ('dlc leave'). Read each time, so leaving a game alone, or taking that back, counts from the next look.
		bool trusted = !bot.Cfg.AchievementDlcLeft.ContainsKey(app);

		if ((map == null) || !map.HasDlc) {
			return new View { Map = map, Bot = bot, Licences = stamp, Trusted = trusted };
		}

		IReadOnlyDictionary<uint, AppOwnership> licences = await bot.GetAppOwnershipAsync().ConfigureAwait(false);

		// Nothing came back: Steam didn't answer, or the licence list hasn't arrived yet. That's "couldn't check", not
		// "owns none of its DLC" - taken as that, the game would be called done for this account until its licences
		// changed. Checking, and asked again in a few minutes. A game that earns anyway waits too: without the licences a
		// DLC known exactly can't be told owned or not, and a few minutes cost nothing.
		if (licences.Count == 0) {
			return new View { Map = map, Bot = bot, Licences = stamp, OwnershipRead = false, Trusted = trusted };
		}

		// Its OWN licence only, and one it has for good. A family member's DLC can't be told apart from the outside, and
		// "can't tell" is no. A free weekend, a timed trial, or a licence that ran out, was cancelled or hasn't gone through
		// is on the list too - and isn't owning the DLC.
		bool Permanent(uint id) => licences.TryGetValue(id, out AppOwnership o) && o.Permanent;
		HashSet<uint> licensed = OwnedGroups(map, Permanent);

		return new View {
			Map = map, Bot = bot, Licences = stamp, Trusted = trusted, Licensed = licensed, Owned = Counted(map, licensed, trusted),
			LicenceKey = LicenceKey(map, Permanent)
		};
	}

	/// <summary>
	/// Is it time this map was built again? None yet; a week old; built by older rules; saved before the achievement
	/// names were kept (without them a new achievement can't be told from an old one, so until then nothing in the game
	/// is unlocked); or built in a hurry, with some DLC names missing, an hour ago.
	/// </summary>
	public static bool Stale(Map? map) =>
		(map == null) || (map.Names == null) || (map.Rule < RuleNow) || (DateTime.UtcNow - map.BuiltAt > FreshFor)
		|| (map.Hurried && (DateTime.UtcNow - map.BuiltAt > HurriedFor));

	/// <summary>Which build of a game's map this is, as one number - a new build is a new number. 0: none.</summary>
	public static long Stamp(Map? map) => map == null ? 0 : map.BuiltAt.Ticks ^ ((long) map.Rule << 56);

	/// <summary>
	/// What the rule says about every achievement in the map, for an account that owns <paramref name="owned"/>, as one
	/// number. The same map built again with nothing new says the same; a map that now places a DLC it couldn't, or
	/// knows achievements it didn't, says something else - and a game held on the old one is looked at again.
	/// </summary>
	/// <remarks>
	/// With <paramref name="licensed"/>, the "DLC"-named holds are in it too (<see cref="EffectiveHold"/>): buying an add-on
	/// the rule already counted as owned changes nothing else, and such a game would have stayed held.
	/// </remarks>
	public static long HoldKey(Map? map, IReadOnlySet<uint> owned, IReadOnlySet<uint>? licensed = null) {
		if (map?.Names is not { } names) {
			return 0;
		}

		ulong hash = 14695981039346656037UL;

		foreach (string name in names.Order(StringComparer.Ordinal)) {
			foreach (char c in name + ":" + (int) EffectiveHold(map, name, owned, licensed) + ";") {
				hash ^= c;
				hash *= 1099511628211UL;
			}
		}

		return hash == 0 ? 1 : unchecked((long) hash);
	}

	/// <summary>The map for a game, however old, or null when there isn't one yet. Asks nothing.</summary>
	public static Map? Current(uint app) {
		Load();

		lock (Data) {
			return Maps.GetValueOrDefault(app);
		}
	}

	// ── working out where a DLC's achievements are ───────────────────────────

	/// <summary>
	/// A DLC's block, from where its highlighted achievements were found in the game's list.
	/// </summary>
	/// <param name="anchors">Each highlighted name's position in the game's list, in the store's order; null for a
	/// name that isn't there.</param>
	/// <param name="total">How many achievements the store says the DLC has.</param>
	/// <param name="count">How many the game has in all.</param>
	/// <param name="hidden">Which positions are hidden achievements, when known.</param>
	/// <remarks>See <see cref="Locate"/>; this is that with one place per name.</remarks>
	public static (List<int> Indices, bool Exact) Block(IReadOnlyList<int?> anchors, int total, int count, IReadOnlyList<bool>? hidden = null) =>
		Locate([.. anchors.Select(static a => (IReadOnlyList<int>) (a is { } i ? [i] : []))], total, count, hidden);

	/// <summary>
	/// A DLC's block, from every place each of its highlighted names is found in the game's list.
	/// </summary>
	/// <param name="places">For each highlighted name, in the store's order, every position it has in the game's list
	/// (none when it isn't there, more than one when the name is used twice).</param>
	/// <param name="total">How many achievements the store says the DLC has.</param>
	/// <param name="count">How many the game has in all.</param>
	/// <param name="hidden">Which positions are hidden achievements, when known.</param>
	/// <remarks>
	/// The store highlights a DLC's first achievements in the game's own order, so the name it lists k-th sits k places
	/// into the block: each name found says where the block starts - its position less k. Modern Warfare II's ten names
	/// are at 0-9 and all say "0"; it has 24, so 0-23. Modern Warfare III's say "24", and it has 39: 24-62.
	///
	/// Counting from every name rather than from the first one found is what keeps two mistakes out:
	///   • the first name missing from the list. Taken from the first one found, a block really at 10-33 whose first
	///     name isn't there was read as 11-34, and 10 - that DLC's - counted as the base game's.
	///   • a name used twice, once in the base game. "Completionist" at 3 in the base game and at 40 in the DLC was taken
	///     at 3, and most of the DLC's block, 40-59, counted as the base game's. Every name is taken at the place that
	///     agrees with the most other names - 40, here, where the other nine agree.
	///
	/// When they don't all agree, or one start is as good as another, every block any of them says is held - the union,
	/// never the likeliest guess - and every name found is inside it. Hidden achievements just in front of a block are
	/// taken in too: whether the store's highlighted list skips hidden ones isn't known, and if it does, the DLC's block
	/// really starts there. Holding back a few base-game achievements costs nothing; unlocking one from DLC the account
	/// doesn't own is the thing this exists to stop.
	/// </remarks>
	public static (List<int> Indices, bool Exact) Locate(IReadOnlyList<IReadOnlyList<int>> places, int total, int count, IReadOnlyList<bool>? hidden = null) {
		(List<int> indices, bool exact, _) = LocateBoth(places, total, count, hidden);

		return (indices, exact);
	}

	/// <summary>
	/// <see cref="Locate"/>, and the part of the block that is certain as well: when the names line up exactly, the
	/// block as the store gives it - from its first name, its total long - without the hidden ones in front; otherwise
	/// nothing. The generous block holds back; only the certain one may let an achievement go in a game that can't be
	/// fully mapped (see <see cref="HoldOf"/>).
	/// </summary>
	public static (List<int> Indices, bool Exact, List<int> Strict) LocateBoth(IReadOnlyList<IReadOnlyList<int>> places, int total, int count, IReadOnlyList<bool>? hidden = null) {
		if ((total <= 0) || (count <= 0)) {
			return ([], false, []);
		}

		List<List<int>> found = [.. places.Select(p => p.Where(i => (i >= 0) && (i < count)).Distinct().ToList())];

		// How many names agree on each start.
		Dictionary<int, int> votes = [];

		for (int k = 0; k < found.Count; k++) {
			foreach (int start in found[k].Select(i => i - k).Distinct()) {
				votes[start] = votes.GetValueOrDefault(start) + 1;
			}
		}

		if (votes.Count == 0) {
			return ([], false, []);
		}

		int most = votes.Values.Max();
		List<int> best = [.. votes.Where(v => v.Value == most).Select(static v => v.Key).Order()];
		SortedSet<int> marked = [];
		HashSet<int> starts = [];

		foreach (int agreed in best) {
			for (int k = 0; k < found.Count; k++) {
				if (found[k].Count == 0) {
					continue;
				}

				// The place that agrees best with this start - every one of them equally close, if there's a tie.
				int nearest = found[k].Min(i => Math.Abs(i - k - agreed));

				foreach (int i in found[k].Where(i => Math.Abs(i - k - agreed) == nearest)) {
					starts.Add(i - k);
					marked.Add(i);
				}
			}
		}

		foreach (int start in starts) {
			int from = Math.Max(0, start);

			while ((hidden != null) && (from > 0) && (from - 1 < hidden.Count) && hidden[from - 1]) {
				from--;
			}

			for (int i = from; i <= Math.Min(start + total - 1, count - 1); i++) {
				marked.Add(i);
			}
		}

		// Exact: every name found, once each, all saying the same start, and the whole block inside the list.
		int only = starts.First();
		bool exact = (starts.Count == 1) && (best.Count == 1) && found.All(static f => f.Count == 1) && (found.Count > 0)
			&& (only >= 0) && (only + total <= count) && (found.Count <= total);

		return ([.. marked], exact, exact ? [.. Enumerable.Range(only, total)] : []);
	}

	/// <summary>
	/// Every place each highlighted name is in the game's list, in the store's order - none for a name that isn't
	/// there, two or more for a name used twice (two DLC can both have a "Completionist", or the base game can).
	/// <see cref="Locate"/> decides which of them is the DLC's.
	/// </summary>
	public static List<IReadOnlyList<int>> Places(IReadOnlyList<string> schemaNames, IReadOnlyList<string> highlighted) {
		Dictionary<string, List<int>> where = [];

		for (int i = 0; i < schemaNames.Count; i++) {
			string key = Plain(schemaNames[i]);

			if (!where.TryGetValue(key, out List<int>? at)) {
				where[key] = at = [];
			}

			at.Add(i);
		}

		return [.. highlighted.Select(name => (IReadOnlyList<int>) (where.TryGetValue(Plain(name), out List<int>? at) ? at : []))];
	}

	/// <summary>Words that say nothing about which DLC it is: "Pro Pack", "Season Pass", "Ultra HD Texture Pack".</summary>
	private static readonly HashSet<string> Generic = new(StringComparer.Ordinal) {
		"a", "an", "and", "the", "of", "for", "in", "on", "with", "or", "to", "pack", "packs", "pro", "starter", "bundle", "edition",
		"upgrade", "season", "pass", "soundtrack", "ost", "music", "skin", "skins", "costume", "costumes", "outfit", "outfits",
		"texture", "textures", "hd", "4k", "ultra", "points", "dlc", "content", "expansion", "map", "maps", "team", "tracer",
		"deluxe", "digital", "artbook", "art", "book", "wallpaper", "wallpapers", "supporter", "support", "collector's",
		"collectors", "cosmetic", "cosmetics", "vault", "premium", "gold", "silver", "standard", "bonus", "extra", "extras",
		"item", "items", "weapon", "weapons", "set", "kit", "mode", "modes", "remastered", "complete", "ultimate", "definitive",
		"goty", "year", "game", "launch", "founders", "founder's", "booster", "currency", "coins", "credits", "gems", "blackcell",
		"battle", "operator", "operators", "preorder", "pre-order", "early", "access", "sticker", "stickers", "avatar",
		"frame", "frames", "emote", "emotes", "theme", "themes", "demo", "trial", "prologue", "chapter", "episode", "part"
	};

	/// <summary>
	/// What a DLC is called once the game's own name and the store's packaging words are off it, or null when nothing
	/// distinctive is left. "Fallout New Vegas: Dead Money" is "dead money"; "Civilization V - Civ and Scenario Pack:
	/// Korea" is "korea"; "Borderlands 2 Season Pass" is nothing at all.
	/// </summary>
	public static string? DistinctName(string dlcName, string baseName) {
		string name = Plain(Regex.Replace(dlcName ?? "", @"\([^)]*\)", " "));
		string game = Plain(baseName ?? "");

		if ((game.Length > 0) && name.StartsWith(game, StringComparison.Ordinal)) {
			name = name[game.Length..];
		}

		string[] parts = Regex.Split(name, @"\s*(?::|\s[-–—]\s)\s*");
		string last = parts.Select(static p => p.Trim(' ', '-', '–', '—', ',', '.', '!', '"', '\'')).LastOrDefault(static p => p.Length > 0) ?? "";
		string[] words = Regex.Matches(last, @"[\p{L}\p{N}'’]+").Select(static m => m.Value).ToArray();

		if ((words.Length == 0) || words.All(static w => Generic.Contains(w) || w.All(char.IsDigit))) {
			return null;
		}

		if ((words.Length == 1) && (words[0].Length < 5)) {
			return null;
		}

		return (last == game) ? null : last;
	}

	/// <summary>One achievement in the game's public list, in its order.</summary>
	public sealed record Entry(string Name, string Display, string Description, bool Hidden);

	/// <summary>One DLC as the store describes it. Total 0 and no names for most; <paramref name="Unsure"/> when its
	/// achievements, if it has any, can't be placed. <paramref name="Type"/> is the store's type of the page its figures
	/// came from - "dlc" for its own DLC page, "game" for one the store sends to a game's (Modern Warfare II), "" unknown.</summary>
	public sealed record Dlc(uint App, List<uint> Ids, string Name, int Total, List<string> Highlighted, Doubt Unsure = Doubt.None, string Type = "");

	/// <summary>
	/// One DLC's store page, as asked for by its DLC id: the app the page really is, its name and figures.
	/// <paramref name="Listed"/> false: the store has no page for it at all. <paramref name="Type"/> is the store's
	/// ("dlc", "music", "game" when the page is a game's).
	/// </summary>
	public sealed record Page(uint Id, uint StoreApp, string Name, int Total, List<string> Highlighted, bool Listed = true, string Type = "");

	/// <summary>
	/// A DLC that plainly adds no achievements: a soundtrack, an artbook, wallpapers - by the store's type, or by a name
	/// that is only that. A name with anything else in it ("Deluxe Edition: soundtrack and mission pack") isn't.
	/// Or a pack that is plainly only looks or in-game money - see <see cref="Cosmetic"/>.
	/// </summary>
	/// <remarks>
	/// Only asked about a DLC the store gives no achievement figures for. One it does give figures for is placed by
	/// them, whatever it is called - see <see cref="Merge"/>.
	/// </remarks>
	public static bool Harmless(string type, string name, string baseName = "") {
		if (type is "music" or "video") {
			return true;
		}

		string plain = Plain(name);

		return (Regex.IsMatch(plain, @"(?<![\p{L}\p{N}])(soundtrack|ost|original score|artbook|art book|digital art ?book|wallpapers?)(?![\p{L}\p{N}])")
				&& !Regex.IsMatch(plain, @"(?<![\p{L}\p{N}])(edition|bundle|upgrade|season|expansion|pass|mission|missions|campaign|story|chapter|episode|map|maps|level|levels|weapon|weapons|character|characters|dlc|content)(?![\p{L}\p{N}])|[+&]"))
			|| Cosmetic(name, baseName);
	}

	/// <summary>
	/// Words that mark a DLC as only looks or in-game money, on their own. Call of Duty alone has close to a hundred DLC
	/// like this - skins, tracer packs, BlackCell, COD Points - and each one held the whole game on an account without
	/// it. "Pack" and "Bundle" on their own say nothing: a pack can be a map pack, a bundle can hold a story DLC.
	/// </summary>
	/// <remarks>
	/// A word that can mean something else isn't here: "skin" ("Second Skin"), "charm" ("Charm City"), "profile" ("The
	/// Profile of Evil"), "avatar" ("Avatar: Frontiers of Pandora"), "credits" ("Closing Credits"), "points", "coins",
	/// "gems". Those count only with a pack after them, or as an amount of money - see <see cref="CosmeticPatterns"/>.
	/// </remarks>
	public static readonly IReadOnlyList<string> CosmeticWords = [
		"skins", "tracers", "tracer pack", "tracer packs", "tracer bundle", "operator pack", "operator packs", "operator bundle",
		"operator bundles", "operator skin", "operator skins", "camo", "camos", "camouflage", "emblems", "calling card",
		"calling cards", "stickers", "outfits", "costumes", "cosmetic", "cosmetics", "blackcell", "battle pass", "battlepass",
		"virtual currency", "gold pack", "gold packs", "pro pack", "pro packs", "starter pack", "starter packs",
		"vault edition pack", "avatars", "wallpaper", "wallpapers", "supporter pack", "supporter packs"
	];

	/// <summary>A pack, a bundle, a set - what makes a word that can mean anything a thing to wear or show.</summary>
	private const string PackWord = "(?:packs?|bundles?|sets?)";

	/// <summary>
	/// The words that can mean something else, where they plainly don't: with a pack after them, or as money.
	/// </summary>
	/// <remarks>
	/// Money is an amount ("1,100 Gold", "2,400 Points" - but "Season 2 Gold Edition" is an edition, and editions can hold
	/// DLC), a game's own money named as such ("COD Points", "Warzone Points" - the last word, after the name of the
	/// money), or a pack of it ("Currency Pack", "Coin Bundle").
	/// </remarks>
	public static readonly IReadOnlyList<string> CosmeticPatterns = [
		@"(?:weapon|operator|ultra|legendary|epic|character|vehicle|gun) skin",
		@"skin " + PackWord,
		@"(?:weapon )?blueprints? " + PackWord,
		@"weapon blueprints?",
		@"emblem " + PackWord,
		@"sticker " + PackWord,
		@"weapon charms?",
		@"charms? " + PackWord,
		@"(?:outfit|costume) " + PackWord,
		@"avatar " + PackWord,
		@"avatar frames?",
		@"profile (?:backgrounds?|frames?|items?)",
		@"profile " + PackWord,
		// Only at the end of the name or before a whole "pack" word, and at least two digits: "1,100 Gold", "500 Credits
		// Pack". Anywhere at all, a story DLC called "1849 Gold Rush" or "2 Gems of Darkness" read as a currency pack and
		// never held its game; so did "500 Gold Setup" and "The 7 Gems".
		@"\d[\d,.]*\d\s?(?:gold|points|coins|credits|gems)(?=$|\s" + PackWord + @"(?![\p{L}\p{N}]))",
		@"(?:cod|warzone|call of duty|premium|bonus|in-game) (?:points|coins|credits|gems)$",
		@"(?:currency|points|coins?|credits|gems|gold) " + PackWord
	];

	/// <summary>
	/// Words that say a DLC may bring something to play, and so achievements - never harmless, whatever else its name
	/// says. "Season Pass" and "Upgrade Pack" are here because they do: Borderlands 2's Ultimate Vault Hunter Upgrade
	/// Pack adds achievements, and a season pass is mostly story DLC. "Bundle" too, bar an operator bundle: a bundle
	/// can hold anything.
	/// </summary>
	public static readonly IReadOnlyList<string> ContentWords = [
		"map", "maps", "map pack", "expansion", "expansions", "campaign", "campaigns", "story", "stories", "episode", "episodes",
		"chapter", "chapters", "mission", "missions", "zombie", "zombies", "mode", "modes", "season pass", "expansion pass", "pass",
		"dlc <number>", "add-on", "addon", "content", "game of the year", "goty", "complete", "definitive", "upgrade",
		"upgrades", "headhunter", "collection", "bundle", "bundles", "level", "levels", "quest", "quests", "scenario",
		"scenarios", "raid", "raids", "dungeon", "dungeons", "character", "characters", "arena", "arenas", "adventure",
		"adventures", "challenge", "challenges", "heist", "heists", "trial", "trials", "survival", "act", "acts", "operation",
		"operations", "saga", "tale", "tales", "island", "islands", "gold edition"
	];

	private static readonly Regex CosmeticMark = new(
		@"(?<![\p{L}\p{N}])(" + string.Join("|", CosmeticWords.OrderByDescending(static w => w.Length).Select(Regex.Escape).Concat(CosmeticPatterns)) + @")(?![\p{L}\p{N}])",
		RegexOptions.CultureInvariant);

	/// <summary>
	/// "pass" but not a battle pass; "bundle" but not an operator, tracer, skin or money bundle; "DLC" with a number after
	/// it. And a "+" or "&amp;": "Skin Pack + New Arena" is two things, and one of them may be something to play.
	/// </summary>
	private static readonly Regex ContentMark = new(
		@"(?<![\p{L}\p{N}])(" + string.Join("|", ContentWords
			.Where(static w => w is not ("pass" or "bundle" or "bundles" or "dlc <number>" or "add-on" or "addon"))
			.OrderByDescending(static w => w.Length).Select(Regex.Escape))
		+ @"|(?<!battle )pass(es)?|(?<!(?:operators?|tracer|skin|currency|points|coins?|credits|gems|gold) )bundles?|dlc ?#?\d+|add-?ons?)(?![\p{L}\p{N}])|[+&]",
		RegexOptions.CultureInvariant);

	/// <summary>
	/// A letter from outside the Latin alphabets. A DLC name in Japanese, Chinese, Korean, Russian... can't be read by
	/// these word lists, so whatever else it says, it isn't called cosmetic: it may well be something to play.
	/// </summary>
	private static readonly Regex Foreign = new(
		@"[\p{L}-[\p{IsBasicLatin}\p{IsLatin-1Supplement}\p{IsLatinExtended-A}\p{IsLatinExtended-B}\p{IsLatinExtendedAdditional}\p{IsIPAExtensions}]]",
		RegexOptions.CultureInvariant);

	/// <summary>
	/// A DLC's name without the game's own name in front, and the " - " or ": " after it - so what the game is called
	/// never counts as what the DLC is. "Avatar: Frontiers of Pandora - The Sky Breaker" is "the sky breaker": an
	/// "avatar" in the game's name said nothing about the DLC. Compared word by word, any case, without ® and ™ - the
	/// store writes "Fallout New Vegas: Dead Money" for "Fallout: New Vegas".
	/// </summary>
	public static string WithoutGame(string name, string baseName) {
		string plain = Plain(name);
		string[] game = [.. Regex.Matches(Plain(baseName), @"[\p{L}\p{N}']+").Select(static m => m.Value)];

		if (game.Length == 0) {
			return plain;
		}

		MatchCollection words = Regex.Matches(plain, @"[\p{L}\p{N}']+");

		if ((words.Count < game.Length) || Enumerable.Range(0, game.Length).Any(i => words[i].Value != game[i])) {
			return plain;
		}

		Match last = words[game.Length - 1];

		return plain[(last.Index + last.Length)..].TrimStart(' ', '-', '–', '—', ':', ',', '.', '|').Trim();
	}

	/// <summary>
	/// A DLC that is plainly only looks or in-game money: once the game's own name is off it (<see cref="WithoutGame"/>),
	/// its name has one of <see cref="CosmeticWords"/> or <see cref="CosmeticPatterns"/>, none of
	/// <see cref="ContentWords"/>, no "+" or "&amp;", and no letters these lists can't read. "Modern Warfare II - Desert
	/// Rogue: Pro Pack", "BlackCell (Season 03)", "Warzone Points" are; "Modern Warfare II - Campaign", "Ultimate Vault
	/// Hunter Upgrade Pack 2", "Gunzerker Madness Pack" (no word says what's in it), "Closing Credits" and "Charm City"
	/// aren't. Whole words, any case.
	/// </summary>
	/// <remarks>
	/// Kept narrow on purpose. A DLC called harmless that does bring achievements lets the rest of the game be unlocked
	/// on an account without it, in its normal order - and in a game left alone ('dlc leave'), too. One missed only puts
	/// the achievements it can't place after the base game's.
	/// </remarks>
	public static bool Cosmetic(string name, string baseName = "") {
		string plain = WithoutGame(name, baseName);

		return (plain.Length > 0) && !Foreign.IsMatch(plain) && CosmeticMark.IsMatch(plain) && !ContentMark.IsMatch(plain);
	}

	/// <summary>
	/// An entry in the game's DLC list that is no DLC: its own store page (the store didn't send it to another app's)
	/// says it is something else - a game, a mod, a demo, a soundtrack. Portal lists Portal with RTX (2012840, a free game
	/// of its own with its own copy of Portal's 15 achievements) and Half-Life 2 lists Half-Life 2 RTX (a mod): taken as
	/// DLC, the first held all of Portal on an account without it. Such an app never adds achievements to this game's list.
	/// </summary>
	/// <remarks>
	/// Only a page that is the entry's own. Call of Duty's Modern Warfare II (1962660) is a DLC the store sends to a page
	/// of type "game" (3595230): that type is the page's, not the DLC's, and its achievements ARE in Call of Duty's list.
	/// A page that is the base game's own is <see cref="Doubt.BasePage"/>, and an unknown type ("") says nothing.
	/// </remarks>
	public static bool NotDlc(uint app, Page page) =>
		page.Listed && (page.StoreApp != app) && ((page.StoreApp == 0) || (page.StoreApp == page.Id))
		&& (page.Type.Length > 0) && !page.Type.Equals("dlc", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Figures as big as the game's whole list - the store's count, or the block its names point at - from a page that
	/// isn't a DLC page: a separate game's or edition's with the same achievements (a remaster, an RTX version, a
	/// "Definitive Edition"), never a DLC's, and they say nothing about which of this game's achievements come with a DLC.
	/// </summary>
	/// <remarks>
	/// The list is the whole game's, its DLC's achievements in it - so a game whose own part is small and whose DLC adds
	/// the rest (2 of its own and an 18-achievement story DLC) has a DLC with nearly all of it. Taken as another game's,
	/// those 18 were unlocked on an account without the DLC. So by the page's type (<paramref name="type"/>):
	/// <list type="bullet">
	/// <item>"dlc": the DLC's own page - never another game's, however big.</item>
	/// <item>any other type (a DLC the store sends to a game's page, as Modern Warfare II's): only all of the list or more.
	/// Call of Duty's own part is a launcher's; a DLC like that can be nearly all of it.</item>
	/// <item>unknown (said about before types were kept): all but less than a tenth of the list (all of it, under ten).</item>
	/// </list>
	/// </remarks>
	public static bool WholeGame(int dlcAchievements, int count, string type = "") =>
		(count > 0) && !IsDlcPage(type) && (dlcAchievements >= (type.Length == 0 ? count - (count / 10) : count));

	/// <summary>
	/// The store's pages, one DLC each. The same DLC under two ids (the store sends 1962660 to its page at 3595230) is
	/// one DLC, owned by either.
	///
	/// A DLC whose page is the base game's own (Black Ops 6's campaign opens Call of Duty's page) says nothing about the
	/// DLC: those figures are the whole game's, and taken as the DLC's they would have marked every achievement in the
	/// game as that DLC's. Dropped, though, its achievements - Black Ops 6's and 7's, positions 63-156 - counted as the
	/// base game's. So it can't be placed: <see cref="Doubt.BasePage"/>. The same for a DLC with no page at all, and for
	/// one whose page gives no achievement figures - unless it's plainly a soundtrack or an artbook.
	/// </summary>
	public static List<Dlc> Merge(uint app, IEnumerable<Page> pages, string baseName = "") {
		Dictionary<uint, (Dlc Dlc, bool Harmless)> merged = [];

		// Two pages that come to the same DLC are one, whichever came first: every id of both, the figures from either,
		// and any doubt either had. Written over instead, a DLC with no page of its own that another DLC's page sends
		// to lost that other's ids - owning it no longer counted - or its figures.
		void Add(uint key, Dlc dlc, bool harmless) =>
			merged[key] = merged.TryGetValue(key, out (Dlc Dlc, bool Harmless) had) ? (Join(had.Dlc, dlc), had.Harmless && harmless) : (dlc, harmless);

		foreach (Page p in pages) {
			bool harmless = Harmless(p.Type, p.Name, baseName);

			// Not a DLC at all - a game, a mod, a demo, a soundtrack with a page of its own: it adds nothing to this
			// game's list, whatever figures its page gives (those are its own list's).
			if (NotDlc(app, p)) {
				continue;
			}

			if (!p.Listed) {
				Add(p.Id, new Dlc(p.Id, [p.Id], p.Name, 0, [], harmless ? Doubt.None : Doubt.Delisted), harmless);

				continue;
			}

			// Its page is the game's: the name and figures are the game's, so nothing is known about the DLC itself.
			if (p.StoreApp == app) {
				Add(p.Id, new Dlc(p.Id, [p.Id], "", 0, [], Doubt.BasePage), false);

				continue;
			}

			bool ownPage = p.StoreApp != 0;
			uint key = ownPage ? p.StoreApp : p.Id;
			List<uint> ids = ownPage && (p.StoreApp != p.Id) ? [p.Id, p.StoreApp] : [p.Id];

			Add(key, new Dlc(p.Id, ids, p.Name, p.Total, p.Highlighted, Type: p.Type), harmless);
		}

		// No figures and not plainly harmless: it may have achievements, and nothing says which. Most DLC are like this,
		// but so are older DLC that do have achievements, and the store's "Steam Achievements" tag can't tell them apart:
		// every DLC page carries the base game's tags.
		//
		// A count but no names to find it by is the same - whatever it's called. The store saying it HAS achievements
		// beats a name that says skins.
		return [.. merged.Values.Select(static m =>
			(m.Dlc.Unsure == Doubt.None) && (m.Dlc.Total > 0 ? m.Dlc.Highlighted.Count == 0 : !m.Harmless)
				? m.Dlc with { Unsure = Doubt.NoFigures }
				: m.Dlc)];
	}

	/// <summary>
	/// One DLC from two pages: every id of both, a name if either has one, the figures from whichever has more, and the
	/// doubt of either - a DLC one page can't place can't be placed.
	/// </summary>
	public static Dlc Join(Dlc had, Dlc next) => had with {
		Ids = [.. had.Ids.Concat(next.Ids).Distinct()],
		Name = had.Name.Length > 0 ? had.Name : next.Name,
		Total = Math.Max(had.Total, next.Total),
		Highlighted = had.Highlighted.Count >= next.Highlighted.Count ? had.Highlighted : next.Highlighted,
		Unsure = had.Unsure != Doubt.None ? had.Unsure : next.Unsure,
		// Either page a DLC page: it is a DLC, and never taken for another game (see WholeGame).
		Type = IsDlcPage(had.Type) || IsDlcPage(next.Type) ? "dlc" : had.Type.Length > 0 ? had.Type : next.Type
	};

	/// <summary>The store's type for a DLC's own page.</summary>
	private static bool IsDlcPage(string type) => type.Equals("dlc", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The map, from the game's list and its DLC. Pure - everything it needs is passed in, so it can be tested without
	/// asking anyone anything.
	/// </summary>
	public static Map Assemble(uint app, string baseName, IReadOnlyList<Entry> schema, IReadOnlyList<Dlc> dlcs, int dlcCount, int onStore, DateTime now) {
		Map map = new() {
			App = app, BuiltAt = now, Rule = RuleNow, DlcCount = dlcCount, OnStore = onStore, SchemaCount = schema.Count,
			Names = [.. schema.Select(static e => e.Name.ToLowerInvariant()).Distinct()], Sure = []
		};
		List<string> names = [.. schema.Select(static e => e.Display)];
		List<bool> hidden = [.. schema.Select(static e => e.Hidden)];
		HashSet<int> inBlocks = [];

		void Claim(int index, uint dlc) {
			string key = schema[index].Name.ToLowerInvariant();

			if (!map.Owners.TryGetValue(key, out List<uint>? who)) {
				map.Owners[key] = who = [];
			}

			if (!who.Contains(dlc)) {
				who.Add(dlc);
			}
		}

		Dictionary<uint, Group> groups = [];

		Group GroupOf(Dlc d) {
			if (!groups.TryGetValue(d.App, out Group? g)) {
				groups[d.App] = g = new Group { App = d.App, Ids = [.. d.Ids.Distinct()], Name = d.Name, Total = d.Total, Unsure = d.Unsure };
			}

			return g;
		}

		// What can't be placed is kept as that, so an account without it has the rest of the game left alone.
		foreach (Dlc d in dlcs.Where(static d => d.Unsure != Doubt.None)) {
			GroupOf(d);
		}

		// Figures as big as the whole game: another game's or edition's, not a DLC's - nothing is known from them, and
		// nothing is held for them, by the figures or by the wording.
		HashSet<uint> wholeGames = [];

		foreach (Dlc d in dlcs.Where(static d => (d.Unsure == Doubt.None) && (d.Total > 0) && (d.Highlighted.Count > 0))) {
			if (WholeGame(d.Total, schema.Count, d.Type)) {
				wholeGames.Add(d.App);

				continue;
			}

			(List<int> indices, bool exact, List<int> strict) = LocateBoth(Places(names, d.Highlighted), d.Total, schema.Count, hidden);

			if (WholeGame(indices.Count, schema.Count, d.Type)) {
				wholeGames.Add(d.App);

				continue;
			}

			map.StoreSays = true;

			// Found nowhere in this game's list. Its achievements could be in a list of their own - or the names just
			// didn't match. Not knowing which, it can't be placed.
			if (indices.Count == 0) {
				GroupOf(d).Unsure = Doubt.NotFound;

				continue;
			}

			Group g = GroupOf(d);
			g.Located = indices.Count;
			g.Exact = exact;

			foreach (int i in indices) {
				Claim(i, d.App);
				inBlocks.Add(i);
			}

			// The certain part of it, for a game that can't be fully mapped - see Map.Sure.
			foreach (int i in strict) {
				string key = schema[i].Name.ToLowerInvariant();

				if (!map.Sure.TryGetValue(key, out List<uint>? sure)) {
					map.Sure[key] = sure = [];
				}

				if (!sure.Contains(d.App)) {
					sure.Add(d.App);
				}
			}
		}

		// The wording, for what no block covers. Only ever adds a DLC an achievement needs.
		foreach (Dlc d in dlcs) {
			if (wholeGames.Contains(d.App) || (DistinctName(d.Name, baseName) is not { } phrase)) {
				continue;
			}

			Regex named = new(@"(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{N}])", RegexOptions.CultureInvariant);

			for (int i = 0; i < schema.Count; i++) {
				if (inBlocks.Contains(i) || !named.IsMatch(Plain($"{schema[i].Display} {schema[i].Description}"))) {
					continue;
				}

				Group g = GroupOf(d);
				g.ByName = true;
				g.Located++;
				Claim(i, d.App);
			}
		}

		map.Groups = [.. groups.Values.OrderBy(static g => g.App)];

		return map;
	}

	/// <summary>Lower case, one space, straight quotes, no ® or ™ - so the store's names and the list's compare equal.</summary>
	private static string Plain(string text) {
		// The marks first: folding turns "™" into the letters "TM", and "Arsenal™" would never match "Arsenal" again.
		string s = Regex.Replace(text ?? "", @"[®™©℠]", "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
		s = s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"');

		return Regex.Replace(s, @"\s+", " ").Trim();
	}

	// ── building, slowly, one game at a time ─────────────────────────────────

	private static readonly LinkedList<(Bot Bot, uint App)> Queue = new();
	private static readonly Dictionary<uint, TaskCompletionSource> Waiters = [];
	private static readonly Dictionary<uint, DateTime> NotBefore = [];
	private static readonly Lock QueueGate = new();
	private static bool _working;   // a worker is running - set and cleared under QueueGate, so none is ever missed
	private static uint _building;

	/// <summary>Is this game waiting to be worked out, or being worked out now?</summary>
	public static bool IsPending(uint app) {
		lock (QueueGate) {
			return (_building == app) || Queue.Any(q => q.App == app);
		}
	}

	/// <summary>
	/// Ask for a game to be worked out. Returns at once; the work happens in the background, one game at a time.
	/// <paramref name="urgent"/> puts it at the front - the game something is about to unlock in comes before a
	/// command's list of fifty.
	/// </summary>
	public static void Request(Bot bot, uint app, bool urgent = false) {
		if (app == 0) {
			return;
		}

		lock (QueueGate) {
			if (NotBefore.TryGetValue(app, out DateTime after) && (after > DateTime.UtcNow)) {
				return;
			}

			if (_building == app) {
				return;
			}

			LinkedListNode<(Bot Bot, uint App)>? node = Queue.First;

			while ((node != null) && (node.Value.App != app)) {
				node = node.Next;
			}

			if (node != null) {
				if (urgent && (node != Queue.First)) {
					Queue.Remove(node);
					Queue.AddFirst(node);
				}

				return;
			}

			if (urgent) {
				Queue.AddFirst((bot, app));
			} else {
				Queue.AddLast((bot, app));
			}

			if (!Waiters.ContainsKey(app)) {
				Waiters[app] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			// A flag, not the worker's Task: that is only marked finished a moment after it has let go of the queue, and
			// a request landing in that moment would have waited for a worker that was already on its way out.
			if (!_working) {
				_working = true;
				_ = Task.Run(WorkAsync);
			}
		}
	}

	/// <summary>Done when the game's build finishes - built or not. Already done when none is going.</summary>
	public static Task WhenDone(uint app) {
		lock (QueueGate) {
			return Waiters.TryGetValue(app, out TaskCompletionSource? waiting) ? waiting.Task : Task.CompletedTask;
		}
	}

	private static async Task WorkAsync() {
		while (true) {
			Bot bot;
			uint app;

			lock (QueueGate) {
				if (Queue.First is not { } first) {
					_building = 0;
					_working = false;

					return;
				}

				Queue.RemoveFirst();
				(bot, app) = first.Value;
				_building = app;
			}

			bool built = false;

			try {
				built = await BuildAsync(bot, app, CancellationToken.None).ConfigureAwait(false);
			} catch (Exception e) {
				// Never let one game stop the rest. Said once per new kind of failure.
				Log.DebugOnChange("dlcach:build", $"couldn't work out the DLC achievements of {app}: {Log.Describe(e)}");
			}

			lock (QueueGate) {
				_building = 0;

				if (!built) {
					NotBefore[app] = DateTime.UtcNow + RetryAfter;
				} else {
					NotBefore.Remove(app);
				}

				if (Waiters.Remove(app, out TaskCompletionSource? waiting)) {
					waiting.TrySetResult();
				}
			}
		}
	}

	/// <summary>Work one game out and keep the answer. False when something couldn't be asked - try again later.</summary>
	private static async Task<bool> BuildAsync(Bot bot, uint app, CancellationToken ct) {
		Load();

		// 1. The DLC Steam's own app info lists. Only a signed-in account can ask, and without it the list is short
		// by exactly the ones that matter (the store's own list leaves out Modern Warfare II and III).
		List<uint>? fromApp = await AppInfoDlcAsync(bot, app, ct).ConfigureAwait(false);

		if (fromApp == null) {
			return false;
		}

		// 2. The store's list, and the game's name.
		(int status, string? body) = await GameCatalog.StoreGetAsync($"https://store.steampowered.com/api/appdetails?appids={app}&filters=basic&l=english", StoreGap, ct).ConfigureAwait(false);

		if ((status != 200) || (body == null)) {
			Log.DebugOnChange("dlcach:store", $"DLC achievements: the store answered {status} for {app} - trying again later");

			return false;
		}

		string baseName = GameNames.Of(app);
		List<uint> fromStore = [];

		using (JsonDocument doc = JsonDocument.Parse(body)) {
			if (doc.RootElement.TryGetProperty(app.ToString(CultureInfo.InvariantCulture), out JsonElement node)
				&& node.TryGetProperty("data", out JsonElement data) && (data.ValueKind == JsonValueKind.Object)) {
				if (data.TryGetProperty("name", out JsonElement n) && (n.GetString() is { Length: > 0 } named)) {
					baseName = named;
				}

				if (data.TryGetProperty("dlc", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
					foreach (JsonElement id in list.EnumerateArray()) {
						if (id.TryGetUInt32(out uint dlc) && (dlc != 0)) {
							fromStore.Add(dlc);
						}
					}
				}
			}
		}

		List<uint> all = [.. fromApp.Concat(fromStore).Where(d => d != app).Distinct().Order()];

		// 3. Each DLC, from the store - what's already known this week isn't asked again.
		int asked = 0;

		foreach (uint dlc in all) {
			Facts? known;

			lock (Data) {
				known = DlcFacts.GetValueOrDefault(dlc);
			}

			if ((known != null) && (DateTime.UtcNow - known.At < FreshFor)) {
				continue;
			}

			Facts? facts = await DlcFactsAsync(dlc, ct).ConfigureAwait(false);

			if (facts == null) {
				SaveFacts();

				return false;
			}

			lock (Data) {
				DlcFacts[dlc] = facts;
			}

			if ((++asked % 10) == 0) {
				SaveFacts();
			}
		}

		List<(uint Id, Facts Facts)> described;

		lock (Data) {
			described = [.. all.Select(d => (d, DlcFacts.GetValueOrDefault(d))).Where(static x => x.Item2 != null).Select(static x => (x.d, x.Item2!))];
		}

		// 4. The game's list, in order - always, DLC or none: the map keeps every achievement it was built from, so one
		// that turns up later (a new DLC's, usually) is known to be new, and waits, rather than taken as the base game's.
		List<Entry>? schema = await SchemaAsync(app, ct).ConfigureAwait(false);

		if (schema == null) {
			SaveFacts();

			return false;
		}

		// A DLC with no store page has no name from the store either; the one this PC knows it by, if any, is better
		// than nothing - a soundtrack is still told apart by it. Steam's own app info has a name for nearly all of them
		// (Call of Duty's "Khaled Al-Asad Operator Bundle"), so one this PC doesn't know yet is asked for there - all in
		// one question, never the store. Not answered, and it stays nameless: held, as before.
		//
		// Asked and not answered is kept with the map: those DLC are held for want of a name, so the map is built again
		// in an hour rather than in a week - and a game held on it is looked at again once it has been.
		bool answered = await LearnNamesAsync(bot, [.. described.Where(static x => !x.Facts.Listed && (KnownName(x.Id).Length == 0)).Select(static x => x.Id)], ct).ConfigureAwait(false);

		List<Dlc> merged = Merge(app, described.Select(static x => new Page(x.Id, x.Facts.StoreApp, x.Facts.Listed ? x.Facts.Name : KnownName(x.Id), x.Facts.Total,
			x.Facts.Highlighted, x.Facts.Listed, x.Facts.Type)), baseName);
		Map map = Assemble(app, baseName, schema, merged, all.Count, described.Count(static x => x.Facts.Listed), DateTime.UtcNow);
		map.Hurried = !answered;
		// Every id of every DLC, however they were grouped: what the owner's answer is remembered against.
		map.Dlc = [.. all.Concat(described.Select(static x => x.Facts.StoreApp)).Concat(map.Groups.SelectMany(static g => g.Ids.Append(g.App)))
			.Where(d => (d != 0) && (d != app)).Distinct().Order()];
		Save(map);

		int unsure = map.Groups.Count(static g => g.Unsure != Doubt.None);
		Log.Debug(all.Count == 0
			? $"DLC achievements for {GameNames.Of(app)}: it has no DLC"
			: $"DLC achievements for {GameNames.Of(app)}: {all.Count} DLC, {map.Groups.Count - unsure} placed in its list, {unsure} that can't be, {map.Owners.Count} of its {schema.Count} achievement(s) need one");

		return true;
	}

	/// <summary>What this PC knows an app as, or "" - not the "app 123" stand-in.</summary>
	private static string KnownName(uint app) {
		string name = GameNames.Of(app);

		return name.StartsWith("app ", StringComparison.Ordinal) ? "" : name;
	}

	/// <summary>The DLC Steam's app info lists for a game. Null when it couldn't be asked; empty when there are none.</summary>
	private static async Task<List<uint>?> AppInfoDlcAsync(Bot bot, uint app, CancellationToken ct) {
		if (!bot.IsOnline || (bot.Apps is not { } apps)) {
			return null;
		}

		try {
			SteamApps.PICSTokensCallback tokens = await apps.PICSGetAccessTokens([app], []).ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = await apps
				.PICSGetProductInfo([new SteamApps.PICSRequest(app, tokens.AppTokens.GetValueOrDefault(app))], [], false)
				.ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

			foreach (SteamApps.PICSProductInfoCallback page in info.Results ?? []) {
				if (page.Apps.TryGetValue(app, out SteamApps.PICSProductInfoCallback.PICSProductInfo? product)) {
					// Answered without the access token it needed: what comes back is the public part, and the DLC list
					// can be short or missing. That's "couldn't ask", not "no DLC" - taken as that, a short map was kept
					// for a week and every achievement of the DLC left out counted as the base game's.
					if (product.MissingToken) {
						Log.DebugOnChange($"dlcach:pics:{app}", $"DLC achievements: Steam's app info for {app} came back without its access token - trying again later", bot.Name);

						return null;
					}

					string list = product.KeyValues["extended"]["listofdlc"].AsString() ?? "";

					return [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.Select(static s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0)
						.Where(static id => id != 0)];
				}
			}

			// Asked and not answered for this app: not "no DLC", just nothing learned.
			Log.DebugOnChange($"dlcach:pics:{app}", $"DLC achievements: Steam's app info had nothing for {app}{(info.Failed ? " (the request failed)" : "")}", bot.Name);

			return null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.DebugOnChange($"dlcach:pics:{app}", $"DLC achievements: couldn't read Steam's app info for {app}: {Log.Describe(e)}", bot.Name);

			return null;
		}
	}

	/// <summary>
	/// Steam's names for DLC this PC has no name for, from its app info - kept with every other game name. Only ever
	/// used to tell a harmless DLC (a soundtrack, a skin pack) from one that may have achievements, so a name that
	/// doesn't come back just leaves that DLC holding its game. False when Steam couldn't be asked or didn't answer -
	/// the map is then built again soon.
	/// </summary>
	private static async Task<bool> LearnNamesAsync(Bot bot, IReadOnlyList<uint> dlcs, CancellationToken ct) {
		if (dlcs.Count == 0) {
			return true;
		}

		if (!bot.IsOnline || (bot.Apps is not { } apps)) {
			return false;
		}

		try {
			SteamApps.PICSTokensCallback tokens = await apps.PICSGetAccessTokens(dlcs, []).ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = await apps
				.PICSGetProductInfo([.. dlcs.Select(d => new SteamApps.PICSRequest(d, tokens.AppTokens.GetValueOrDefault(d)))], [], false)
				.ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

			foreach (SteamApps.PICSProductInfoCallback page in info.Results ?? []) {
				foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo product in page.Apps.Values) {
					GameNames.Learn(product.ID, product.KeyValues["common"]["name"].AsString());
				}
			}

			// Part of it or none of it back: not an answer about the ones that are missing.
			if (info.Failed || (info.Results == null) || !info.Complete) {
				Log.DebugOnChange("dlcach:names", $"DLC achievements: Steam didn't answer for the names of {dlcs.Count} DLC - building the map again in an hour", bot.Name);

				return false;
			}

			return true;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.DebugOnChange("dlcach:names", $"DLC achievements: couldn't read Steam's names for {dlcs.Count} DLC: {Log.Describe(e)}", bot.Name);

			return false;
		}
	}

	/// <summary>What the store says about one DLC. Null when it didn't answer - asked again next time.</summary>
	private static async Task<Facts?> DlcFactsAsync(uint dlc, CancellationToken ct) {
		(int status, string? body) = await GameCatalog.StoreGetAsync($"https://store.steampowered.com/api/appdetails?appids={dlc}&filters=basic,achievements&l=english", StoreGap, ct).ConfigureAwait(false);

		if ((status != 200) || (body == null)) {
			Log.DebugOnChange("dlcach:store", $"DLC achievements: the store answered {status} for DLC {dlc} - carrying on later");

			return null;
		}

		Log.Recovered("dlcach:store");
		using JsonDocument doc = JsonDocument.Parse(body);

		if (!doc.RootElement.TryGetProperty(dlc.ToString(CultureInfo.InvariantCulture), out JsonElement node) || !node.TryGetProperty("success", out JsonElement ok)) {
			return null;
		}

		// No store page (delisted, never sold on its own): nothing it could say about achievements either.
		if ((ok.ValueKind != JsonValueKind.True) || !node.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Object)) {
			return new Facts { At = DateTime.UtcNow, Listed = false };
		}

		Facts facts = new() {
			At = DateTime.UtcNow,
			Listed = true,
			StoreApp = data.TryGetProperty("steam_appid", out JsonElement sa) && sa.TryGetUInt32(out uint storeApp) ? storeApp : dlc,
			Type = data.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "",
			Name = data.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : ""
		};

		if (data.TryGetProperty("achievements", out JsonElement ach) && (ach.ValueKind == JsonValueKind.Object)) {
			facts.Total = ach.TryGetProperty("total", out JsonElement tot) && tot.TryGetInt32(out int total) ? Math.Max(0, total) : 0;

			if (ach.TryGetProperty("highlighted", out JsonElement hl) && (hl.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement h in hl.EnumerateArray()) {
					string? name = (h.TryGetProperty("localized_name", out JsonElement ln) ? ln.GetString() : null)
						?? (h.TryGetProperty("name", out JsonElement nm) ? nm.GetString() : null);

					if (!string.IsNullOrWhiteSpace(name)) {
						facts.Highlighted.Add(name);
					}
				}
			}
		}

		return facts;
	}

	/// <summary>The game's achievements in their own order, from the public list. Null when it couldn't be read.</summary>
	private static async Task<List<Entry>?> SchemaAsync(uint app, CancellationToken ct) {
		try {
			using HttpResponseMessage response = await Http.GetAsync($"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid={app}&language=english", ct).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				Log.DebugOnChange("dlcach:schema", $"DLC achievements: the achievement list for {app} answered HTTP {(int) response.StatusCode}");

				return null;
			}

			using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
			List<Entry> list = [];

			if (doc.RootElement.TryGetProperty("response", out JsonElement r) && r.TryGetProperty("achievements", out JsonElement rows) && (rows.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement a in rows.EnumerateArray()) {
					string name = a.TryGetProperty("internal_name", out JsonElement i) ? i.GetString() ?? "" : "";

					if (name.Length == 0) {
						continue;
					}

					list.Add(new Entry(name,
						a.TryGetProperty("localized_name", out JsonElement d) ? d.GetString() ?? "" : "",
						a.TryGetProperty("localized_desc", out JsonElement s) ? s.GetString() ?? "" : "",
						a.TryGetProperty("hidden", out JsonElement h) && (h.ValueKind == JsonValueKind.True)));
				}
			}

			Log.Recovered("dlcach:schema");

			return list;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// A timeout is an OperationCanceledException too - that one is only a failed ask.
			Log.DebugOnChange("dlcach:schema", $"DLC achievements: couldn't read the achievement list for {app}: {Log.Describe(e)}");

			return null;
		}
	}

	// ── on disk ──────────────────────────────────────────────────────────────

	private static void Load() {
		lock (Data) {
			if (_loaded) {
				return;
			}

			_loaded = true;

			try {
				if (!File.Exists(PathOf)) {
					return;
				}

				Saved? saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathOf));

				foreach ((uint app, Map map) in saved?.Maps ?? []) {
					Maps[app] = map;
				}

				foreach ((uint dlc, Facts facts) in saved?.Dlc ?? []) {
					DlcFacts[dlc] = facts;
				}
			} catch (Exception e) {
				// A bad file is the same as none: the games are worked out again.
				Log.Debug($"couldn't read the DLC achievement maps: {Log.Describe(e)}");
			}
		}
	}

	private static void Save(Map map) {
		lock (Data) {
			Maps[map.App] = map;
		}

		SaveFacts();
	}

	private static void SaveFacts() {
		string json;

		lock (Data) {
			// What the store said about a DLC is asked again after a week anyway; a month on, it's only taking up room.
			foreach (uint old in DlcFacts.Where(static f => DateTime.UtcNow - f.Value.At > TimeSpan.FromDays(30)).Select(static f => f.Key).ToList()) {
				DlcFacts.Remove(old);
			}

			json = JsonSerializer.Serialize(new Saved { Maps = new Dictionary<uint, Map>(Maps), Dlc = new Dictionary<uint, Facts>(DlcFacts) });
		}

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
			AtomicFile.Write(PathOf, json);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.DebugOnChange("dlcach:save", $"couldn't save the DLC achievement maps: {Log.Describe(e)}");
		}
	}
}
