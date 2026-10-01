using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// The owner's one choice about a game with add-ons Steam doesn't explain: leave it alone, or (the default) let it earn.
/// </summary>
/// <remarks>
/// A game with add-ons the account doesn't own, where Steam doesn't say which achievements they bring, earns anyway
/// (<see cref="DlcAchievements"/>): the achievements that can't be placed are counted as possibly the base game's, and
/// the pacer takes them last, after every one that certainly is. Nobody is asked about it any more - Call of Duty, with
/// about fifty team, charity and skin packs plus Black Ops 6 and 7, waited on a question on an account that owns every
/// real part. Achievements Steam DOES tie to an add-on the account doesn't own are never unlocked, whatever is chosen.
///
/// Someone who would rather a game like that were left alone says so with 'dlc leave': the game goes on the account's
/// AchievementDlcLeft list, with which of its add-ons the account has for good then (<see cref="DlcAchievements.LicenceKey"/>),
/// and is held as it used to be - nothing unlocked on a guess. 'dlc undo' (or the dashboard) takes that back. The old
/// "carry on" answer (AchievementDlcTrusted) isn't needed: every game earns anyway now. 'dlc carryon' still works, for
/// scripts - it takes back a 'dlc leave', or says it isn't needed.
/// </remarks>
public static class DlcChoices {
	/// <summary>A game left alone, for the dashboard's undo.</summary>
	public sealed record Answered(uint App, string Game);

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

	// ── the choices ─────────────────────────────────────────────────────────

	/// <summary>
	/// "Leave it alone": remembered with which of the game's add-ons the account has for good now. From the next look,
	/// nothing in it that might come with an add-on it doesn't own is unlocked. Saved under the account's lock, like every
	/// other change to its settings.
	/// </summary>
	public static Said Leave(Bot bot, uint app) {
		if (NotUnclear(bot, app) is { IsEmpty: false } refused) {
			return refused;
		}

		// The licences the pacer last read for the game. Not read yet (only just updated, and the game not looked at
		// since), and there's nothing to remember it with.
		if (BotManager.ModuleOf<AchievementPacer>(bot)?.DlcKeyOf(app) is not { } key) {
			return new Said("{0}: {1} hasn't been looked at since the app started - try again in a few minutes.", bot.Name, GameNames.Of(app));
		}

		bool saved;

		lock (bot.CfgGate) {
			// A new dictionary rather than changed in place: a save or the pacer can be reading the old one right now.
			bot.Cfg.AchievementDlcLeft = new(bot.Cfg.AchievementDlcLeft) { [app] = key };
			saved = ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		return Told(bot, saved, new Said("{0}: {1} is left alone for achievements now - 'dlc undo {0} {2}' lets it earn them again.",
			bot.Name, GameNames.Of(app), Typed(bot, app)));
	}

	/// <summary>
	/// Only a game with add-ons Steam doesn't explain - or one already left alone - can be left alone. Every other game
	/// already goes by what is known, and "left alone" would change nothing.
	/// </summary>
	public static bool Answerable(Bot bot, uint app) =>
		bot.Cfg.AchievementDlcLeft.ContainsKey(app) || (BotManager.ModuleOf<AchievementPacer>(bot)?.IsUnclear(app) ?? false);

	/// <summary>Why a game can't be left alone, or nothing when it can.</summary>
	private static Said NotUnclear(Bot bot, uint app) =>
		Answerable(bot, app)
			? default
			: new Said("{0}: as far as it has seen, {1} has no add-ons Steam doesn't explain, so there's nothing to leave alone.", bot.Name, GameNames.Of(app));

	/// <summary>
	/// The choice, said and logged - with "not saved to disk" when the settings file couldn't be written: it's in use
	/// now, but gone after a restart.
	/// </summary>
	private static Said Told(Bot bot, bool saved, Said said) {
		Said told = saved ? said : new Said("{0} - {1}", said, new Said("in use now, but not saved to disk - see the Log"));
		Log.Info(told, bot.Name);

		return told;
	}

	/// <summary>
	/// Takes back a 'dlc leave': the game earns again, base game first, and is looked at again the next time it's played.
	/// </summary>
	public static Said Undo(Bot bot, uint app) {
		bool saved;

		lock (bot.CfgGate) {
			if (!bot.Cfg.AchievementDlcLeft.ContainsKey(app)) {
				return new Said("{0}: {1} isn't left alone, so there's nothing to take back.", bot.Name, GameNames.Of(app));
			}

			bot.Cfg.AchievementDlcLeft = new(bot.Cfg.AchievementDlcLeft.Where(kv => kv.Key != app));
			saved = ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		BotManager.ModuleOf<AchievementPacer>(bot)?.Release(app);

		return Told(bot, saved, new Said("{0}: {1} earns achievements again - the base game's first. Those of an add-on it doesn't own stay locked.", bot.Name, GameNames.Of(app)));
	}

	/// <summary>
	/// The old "carry on" answer, kept so scripts that type it still work: it takes back a 'dlc leave', and otherwise
	/// says it isn't needed - every game earns anyway now.
	/// </summary>
	public static Said CarryOn(Bot bot, uint app) =>
		bot.Cfg.AchievementDlcLeft.ContainsKey(app)
			? Undo(bot, app)
			: new Said("{0}: 'dlc carryon' isn't needed any more - {1} earns anyway, the base game's achievements first.", bot.Name, GameNames.Of(app));

	/// <summary>The games left alone, for the dashboard's undo.</summary>
	public static List<Answered> LeftPaused(Bot bot) => [.. bot.Cfg.AchievementDlcLeft.Keys.Order().Select(static a => new Answered(a, GameNames.Of(a)))];

	// ── which game was meant ────────────────────────────────────────────────

	/// <summary>
	/// The game a command names: the whole name in any case, an appID or store link, or a part of exactly one name (two
	/// letters or more). Looked for among the games this account has left alone, what the pacer has played, and its
	/// library. Null game and a reason when it can't be told.
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

	/// <summary>The games a name can mean, with their names: the ones left alone, what the pacer has played, and its library.</summary>
	private static Dictionary<uint, string> Candidates(Bot bot) {
		AchievementPacer? pacer = BotManager.ModuleOf<AchievementPacer>(bot);
		Dictionary<uint, string> names = [];

		void Add(uint app, string? name = null) {
			if ((app != 0) && !names.ContainsKey(app)) {
				names[app] = string.IsNullOrWhiteSpace(name) ? GameNames.Of(app) : name;
			}
		}

		foreach (uint app in bot.Cfg.AchievementDlcLeft.Keys) {
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
