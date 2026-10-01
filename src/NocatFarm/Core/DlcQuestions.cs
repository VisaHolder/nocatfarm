using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// "Is it OK to carry on?" - the one question asked about a game held for DLC.
/// </summary>
/// <remarks>
/// A game with add-ons the account doesn't own, where Steam doesn't say which achievements they bring, is held whole
/// (<see cref="DlcAchievements"/>): unlocking one achievement that came with an add-on it doesn't own can't be taken
/// back. Only the owner knows whether the account has the parts that matter, so they're asked - once per game, on the
/// dashboard, in the log, and to Discord or Telegram. Call of Duty is the one that made this needed: about fifty team,
/// charity and skin packs plus Black Ops 6 and 7 held it on an account that owns every real part.
///
/// Two answers. "I own what matters - carry on" puts the game on the account's AchievementDlcTrusted list: the add-ons
/// Steam says nothing about then count as owned. One whose achievements ARE known exactly still goes by the licences -
/// Modern Warfare III's are never unlocked on an account without it, carry on or not. "Leave it paused" is remembered
/// with which of the game's add-ons the account has for good then (AchievementDlcLeft, <see cref="DlcAchievements.LicenceKey"/>),
/// so it isn't asked again until that changes - a DLC bought - when it may be asked once more. Only the licences: the
/// add-ons counted as owned change with a "carry on" and with how the game's map grouped them, and keyed on those a game
/// left paused came back after the next look. Either can be taken back ('dlc undo', or the dashboard).
///
/// Only games held WHOLE for that reason are asked about, and only ones the pacer or the hunt wanted to work on. A game
/// held for an add-on whose achievements are known exactly needs no question: the answer is already certain.
/// </remarks>
public static class DlcQuestions {
	/// <summary>How many missing add-ons are named; the rest are "and N more".</summary>
	public const int Named = 5;

	/// <summary>One question, for the dashboard: the game, a few of the add-ons it's missing, and how many more.</summary>
	/// <param name="Missing">Up to <see cref="Named"/> names, without the game's own name in front, packs left out.</param>
	/// <param name="More">How many more there are that aren't named.</param>
	/// <param name="OnlyPacks">Everything missing looks like skins, team packs and the like.</param>
	public sealed record Question(uint App, string Game, List<string> Missing, int More, bool OnlyPacks);

	/// <summary>A game the owner has answered for, for the dashboard's "undo".</summary>
	public sealed record Answered(uint App, string Game);

	/// <summary>The add-ons owned for a game, as one string - the same ones in any order are the same.</summary>
	public static string OwnedKey(IEnumerable<uint>? owned) => string.Join(",", (owned ?? []).Distinct().Order());

	/// <summary>
	/// Is there a question for this game? Held whole, not answered "carry on", and not answered "leave it paused" with
	/// the same add-ons licensed as now (<see cref="DlcAchievements.LicenceKey"/>).
	/// </summary>
	public static bool Asks(bool heldWhole, bool trusted, IReadOnlyDictionary<uint, string>? left, uint app, string licenceKey) =>
		heldWhole && !trusted && !((left != null) && left.TryGetValue(app, out string? then) && (then == licenceKey));

	/// <summary>The questions waiting for this account, oldest game id first. None while it earns no achievements.</summary>
	public static List<Question> For(Bot bot) {
		if (!bot.Cfg.UnlockAchievements || (BotManager.ModuleOf<AchievementPacer>(bot) is not { } pacer)) {
			return [];
		}

		List<Question> asks = [];

		foreach (AchievementPacer.HeldWhole held in pacer.HeldWholeGames()) {
			// Not looked at since an update: its licences are read within the minute, and then it's asked about (or not).
			if (held.Key is { } key && Asks(true, bot.Cfg.AchievementDlcTrusted.Contains(held.App), bot.Cfg.AchievementDlcLeft, held.App, key)) {
				asks.Add(Build(held.App, GameNames.Of(held.App), DlcAchievements.Current(held.App), [.. held.Owned]));
			}
		}

		return asks;
	}

	/// <summary>
	/// The question about one game: the add-ons it's missing that Steam says nothing about, for showing. The ones that
	/// look like packs are left out of the names (see <see cref="LooksLikeExtra"/>) - fifty Call of Duty team packs
	/// would bury Black Ops 7 - and names Steam hasn't given yet go to the end, counted rather than shown as numbers.
	/// </summary>
	public static Question Build(uint app, string game, DlcAchievements.Map? map, IReadOnlyCollection<uint> owned) {
		List<string> all = DlcAchievements.UnplaceableNames(map, owned);
		List<string> real = [.. all.Where(n => !LooksLikeExtra(n, game)).Select(n => DlcAchievements.ShortName(n, game)).Distinct()];
		List<string> named = [.. real.Where(static n => !n.StartsWith("app ", StringComparison.Ordinal)).Take(Named)];

		return new Question(app, game, named, real.Count - named.Count, (all.Count > 0) && (real.Count == 0));
	}

	/// <summary>
	/// Words that make an add-on's name look like something to wear or show rather than something to play: a pack, a
	/// bundle, points, skins, a team or league pack, a charity pack, a soundtrack, an artbook.
	/// </summary>
	private static readonly Regex ExtraMark = new(
		@"(?<![\p{L}\p{N}])(packs?|bundles?|points|skins?|teams?|league|charity|endowment|soundtracks?|ost|original score|artbooks?|art book|wallpapers?|cosmetics?|tracers?|operators?|blueprints?|camos?|emblems?|stickers?|calling cards?|outfits?|costumes?|avatars?|currency|coins|credits|gems|battle ?pass|blackcell)(?![\p{L}\p{N}])",
		RegexOptions.CultureInvariant);

	/// <summary>Words that say a pack has something to play in it after all: a map pack, a campaign, a story...</summary>
	private static readonly Regex PlayMark = new(
		@"(?<![\p{L}\p{N}])(campaigns?|expansions?|stor(y|ies)|episodes?|chapters?|maps?|missions?|season pass|expansion pass|zombies|quests?|raids?|dungeons?|adventures?|heists?|dlc ?#?\d+)(?![\p{L}\p{N}])",
		RegexOptions.CultureInvariant);

	/// <summary>
	/// For SHOWING only: does this add-on look like a pack of looks - what <see cref="DlcAchievements.Cosmetic"/> calls
	/// cosmetic, or a name with a pack, bundle, points, skins, a team or league, charity, a soundtrack or an artbook in
	/// it and nothing to play? Never asked by the rule that holds a game: "Ghost Legacy Pack" says nothing about what's
	/// in it, and is held for just the same. Here it only stays out of the list of names.
	/// </summary>
	public static bool LooksLikeExtra(string name, string baseName = "") {
		if (DlcAchievements.Cosmetic(name, baseName)) {
			return true;
		}

		string plain = DlcAchievements.WithoutGame(name, baseName);

		return ExtraMark.IsMatch(plain) && !PlayMark.IsMatch(plain);
	}

	/// <summary>
	/// How a game is typed in a command: its name - or its appID while Steam hasn't given the name yet, or when the name
	/// wouldn't find just this game (another game of the account's has the same name, or it's too short to look for).
	/// </summary>
	public static string Typed(Bot bot, uint app) {
		string id = app.ToString(System.Globalization.CultureInfo.InvariantCulture);

		if (!GameNames.IsKnown(app)) {
			return id;
		}

		string name = Regex.Replace(GameNames.Of(app), @"[®™©℠]", "").Trim();
		string plain = Plain(name);

		return (plain.Length < MinPart) || Candidates(bot).Any(kv => (kv.Key != app) && (Plain(kv.Value) == plain)) ? id : name;
	}

	// ── the answers ─────────────────────────────────────────────────────────

	/// <summary>
	/// "I own what matters - carry on": the game goes on the account's trusted list, and the pacer looks at it again the
	/// next time it's played. Saved under the account's lock, like every other change to its settings.
	/// </summary>
	public static Said CarryOn(Bot bot, uint app) {
		if (NotAsked(bot, app) is { IsEmpty: false } refused) {
			return refused;
		}

		bool saved;

		lock (bot.CfgGate) {
			// New lists rather than changed in place: a save or the pacer can be reading the old ones right now.
			if (!bot.Cfg.AchievementDlcTrusted.Contains(app)) {
				bot.Cfg.AchievementDlcTrusted = [.. bot.Cfg.AchievementDlcTrusted, app];
			}

			if (bot.Cfg.AchievementDlcLeft.ContainsKey(app)) {
				bot.Cfg.AchievementDlcLeft = new(bot.Cfg.AchievementDlcLeft.Where(kv => kv.Key != app));
			}

			saved = ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		BotManager.ModuleOf<AchievementPacer>(bot)?.Release(app);

		return Told(bot, saved, new Said("{0}: carrying on with {1}. Only achievements that certainly come with an add-on it doesn't own stay locked.", bot.Name, GameNames.Of(app)));
	}

	/// <summary>
	/// "Leave it paused": remembered with which of the game's add-ons the account has for good now, so it isn't asked
	/// again until that changes.
	/// </summary>
	public static Said Leave(Bot bot, uint app) {
		if (NotAsked(bot, app) is { IsEmpty: false } refused) {
			return refused;
		}

		// What the question is asked against - the licences the pacer last read for the game. Not read yet (only just
		// updated, and the game not looked at since), and there's nothing to remember it with: it would be asked again.
		if (BotManager.ModuleOf<AchievementPacer>(bot)?.DlcKeyOf(app) is not { } key) {
			return new Said("{0}: {1} hasn't been looked at since the app started - try again in a few minutes.", bot.Name, GameNames.Of(app));
		}

		bool saved;

		lock (bot.CfgGate) {
			bot.Cfg.AchievementDlcLeft = new(bot.Cfg.AchievementDlcLeft) { [app] = key };

			// Leaving it paused takes back a "carry on" given before.
			if (bot.Cfg.AchievementDlcTrusted.Contains(app)) {
				bot.Cfg.AchievementDlcTrusted = [.. bot.Cfg.AchievementDlcTrusted.Where(a => a != app)];
			}

			saved = ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		return Told(bot, saved, new Said("{0}: {1} stays paused for achievements. It won't be asked about again unless its add-ons change.", bot.Name, GameNames.Of(app)));
	}

	/// <summary>
	/// Only a game that's asked about, or already answered, can be answered: "carry on" for any game at all would put
	/// one on the trusted list that was never held, and it would then never be asked about if it ever was.
	/// </summary>
	public static bool Answerable(Bot bot, uint app) =>
		bot.Cfg.AchievementDlcTrusted.Contains(app) || bot.Cfg.AchievementDlcLeft.ContainsKey(app)
		|| (BotManager.ModuleOf<AchievementPacer>(bot)?.HeldWholeGames().Any(h => h.App == app) ?? false);

	/// <summary>Why a game can't be answered for, or nothing when it can.</summary>
	private static Said NotAsked(Bot bot, uint app) =>
		Answerable(bot, app)
			? default
			: new Said("{0}: {1} isn't paused for add-ons it doesn't own, so there's nothing to answer for it.", bot.Name, GameNames.Of(app));

	/// <summary>
	/// The answer, said and logged - with "not saved to disk" when the settings file couldn't be written: it's in use
	/// now, but gone after a restart.
	/// </summary>
	private static Said Told(Bot bot, bool saved, Said said) {
		Said told = saved ? said : new Said("{0} - {1}", said, new Said("in use now, but not saved to disk - see the Log"));
		Log.Info(told, bot.Name);

		return told;
	}

	/// <summary>
	/// Takes an answer back. After "carry on" the game is paused again until it's answered; after "leave it paused" it's
	/// asked about again.
	/// </summary>
	public static Said Undo(Bot bot, uint app) {
		bool trusted, left, saved;

		lock (bot.CfgGate) {
			trusted = bot.Cfg.AchievementDlcTrusted.Contains(app);
			left = bot.Cfg.AchievementDlcLeft.ContainsKey(app);

			if (!trusted && !left) {
				return new Said("{0}: there's no answer for {1} to take back.", bot.Name, GameNames.Of(app));
			}

			bot.Cfg.AchievementDlcTrusted = [.. bot.Cfg.AchievementDlcTrusted.Where(a => a != app)];
			bot.Cfg.AchievementDlcLeft = new(bot.Cfg.AchievementDlcLeft.Where(kv => kv.Key != app));
			saved = ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		return Told(bot, saved, trusted
			? new Said("{0}: took back \"carry on\" for {1}. Nothing in it is unlocked on a guess any more.", bot.Name, GameNames.Of(app))
			: new Said("{0}: {1} will be asked about again.", bot.Name, GameNames.Of(app)));
	}

	/// <summary>The games answered "carry on", for the dashboard's undo.</summary>
	public static List<Answered> CarriedOn(Bot bot) => [.. bot.Cfg.AchievementDlcTrusted.Distinct().Select(static a => new Answered(a, GameNames.Of(a)))];

	/// <summary>The games answered "leave it paused", for the dashboard's "ask again".</summary>
	public static List<Answered> LeftPaused(Bot bot) => [.. bot.Cfg.AchievementDlcLeft.Keys.Order().Select(static a => new Answered(a, GameNames.Of(a)))];

	// ── which game was meant ────────────────────────────────────────────────

	/// <summary>
	/// The game a command names: the whole name in any case, an appID or store link, or a part of exactly one name (two
	/// letters or more). Looked for among the games this account has been asked about or answered for, what the pacer has
	/// played, and its library. Null game and a reason when it can't be told.
	/// </summary>
	public static (uint App, Said Problem) Find(Bot bot, string typed) {
		string term = typed.Trim();

		if (term.Length == 0) {
			return (0, new Said("Which game? Its name or its appID."));
		}

		Dictionary<uint, string> names = Candidates(bot);
		string want = Plain(term);

		// The whole name first, before the number: a game can be called "140" or "1979", and typed as it's shown, its name
		// is what was meant.
		List<uint> exact = [.. names.Where(kv => Plain(kv.Value) == want).Select(static kv => kv.Key)];

		if (exact.Count == 1) {
			return (exact[0], default);
		}

		uint id = Settings.AppIdFrom(term);

		// Two games both called "140": the number is then the appID of one of them, or of neither.
		if ((id > 0) && ((exact.Count == 0) || exact.Contains(id))) {
			return (id, default);
		}

		// A part of a name only from two letters up: a lone "0" or "e" is in nearly every name, and would pick one at random
		// whenever only one game happened to have it.
		if ((exact.Count == 0) && (want.Length < MinPart)) {
			return (0, new Said("Type more of the game's name, or its appID."));
		}

		List<uint> part = exact.Count > 1 ? exact : [.. names.Where(kv => Plain(kv.Value).Contains(want, StringComparison.Ordinal)).Select(static kv => kv.Key)];

		return part.Count switch {
			1 => (part[0], default),
			0 => (0, new Said("{0} has no game called \"{1}\". Try its appID - the number in its store link.", bot.Name, term)),
			_ => (0, new Said("More than one game matches \"{0}\": {1}. Type more of the name, or the appID.", term,
				string.Join(", ", part.Take(5).Select(a => $"{names[a]} ({a})")) + (part.Count > 5 ? ", ..." : "")))
		};
	}

	/// <summary>The shortest part of a name that's looked for: one letter or digit matches nearly everything.</summary>
	private const int MinPart = 2;

	/// <summary>
	/// The games a name can mean, with their names: the ones this account has been asked about or answered for, what the
	/// pacer has played, and its library.
	/// </summary>
	private static Dictionary<uint, string> Candidates(Bot bot) {
		AchievementPacer? pacer = BotManager.ModuleOf<AchievementPacer>(bot);
		Dictionary<uint, string> names = [];

		void Add(uint app, string? name = null) {
			if ((app != 0) && !names.ContainsKey(app)) {
				names[app] = string.IsNullOrWhiteSpace(name) ? GameNames.Of(app) : name;
			}
		}

		foreach (AchievementPacer.HeldWhole held in pacer?.HeldWholeGames() ?? []) {
			Add(held.App);
		}

		foreach (uint app in bot.Cfg.AchievementDlcTrusted.Concat(bot.Cfg.AchievementDlcLeft.Keys)) {
			Add(app);
		}

		foreach (AchievementPacer.Row row in pacer?.Snapshot() ?? []) {
			Add(row.App);
		}

		foreach (Library.Entry game in bot.Library.Games) {
			Add(game.AppId, game.Name);
		}

		return names;
	}

	/// <summary>A name to compare: no ® or ™, any case, one space between words.</summary>
	private static string Plain(string name) =>
		Regex.Replace(Regex.Replace(name, @"[®™©℠]", ""), @"\s+", " ").Trim().ToLowerInvariant();
}
