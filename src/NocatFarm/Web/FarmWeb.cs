using System.Security.Cryptography;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>
/// 1.9.2: the dashboard served from https://farm.nocat.lol, talking straight to this app on http://127.0.0.1. Off unless
/// "Let farm.nocat.lol open this dashboard" (FarmWebAllowed) is on.
///
/// What a page there may do is decided here, in one place, deny-by-default: every API route and every command word is
/// in a list below - allowed, allowed in part, or refused - and anything that isn't in a list is refused. The checks
/// walk <see cref="Commands.All"/> and every mapped route, so a new one can't slip through unclassified.
///
/// The one risk it guards against is the page's own code being swapped (nocat.lol, its Cloudflare account or GitHub taken
/// over). So only what would let that code steal or take over is refused: items leaving or being sent somewhere else, the
/// Steam sign-in and its secrets (Steam Guard codes are never even shown), running code (updates, add-ons, plugins, a
/// restore), lifting a scam-watch hold, and the back doors (the bots' tokens and owners, who may command the accounts, how
/// the dashboard is reached, removing its password). Everything else - settings, switches, buying within the app's own
/// money limits, importing - works from there too. Pages there get Bearer tokens only - never a cookie - each one approved
/// at the app.
/// </summary>
public static class FarmWeb {
	/// <summary>The one website allowed, exactly: https, no port, no subdomain, no other nocat.lol host.</summary>
	public const string SiteOrigin = "https://farm.nocat.lol";

	/// <summary>Checked against the Origin header, ordinal. Only the checks change it.</summary>
	internal static string Origin { get; set; } = SiteOrigin;

	/// <summary>
	/// What the page is told the app can do, so a page newer than the app can say "update your app". Raised whenever the
	/// page would need something this app doesn't answer.
	/// </summary>
	public const int ApiVersion = 1;

	/// <summary>Pair requests waiting for an answer at the app, at most.</summary>
	public const int MaxPending = 5;

	/// <summary>Pair requests in a minute, at most.</summary>
	public const int PairsPerMinute = 3;

	/// <summary>How long a pair request waits for an answer.</summary>
	public static readonly TimeSpan PairLifetime = TimeSpan.FromMinutes(5);

	/// <summary>How long a paired page stays paired. Revocable any time from the Phone page or 'visitors signout'.</summary>
	public const int TokenDays = 90;

	/// <summary>Paired pages kept, at most; the oldest goes when another is approved.</summary>
	public const int MaxTokens = 10;

	/// <summary>The page, for this PC's nocat.farm on <paramref name="port"/> - what "open the dashboard" opens once it's set up.</summary>
	public static string PageUrl(int port) => $"{SiteOrigin}/?port={port}";

	/// <summary>
	/// The switch's value for a brand-new install only (a config made on first start): on where there's a desktop with a
	/// browser on the same machine. Off in Docker and on a server with no screen - the page only reaches nocat.farm from a
	/// browser on the PC it runs on, and there is none there. A config that already exists is never changed: missing the
	/// key, it's off, as in 1.9.1.
	/// </summary>
	public static bool DefaultForNewInstall => Platform.HasDesktop && !Platform.InContainer;

	/// <summary>Sent to the page this long ago with nothing paired since: the next "open the dashboard" opens this PC's own.</summary>
	public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(3);

	/// <summary>The refusal the page shows instead of a button that fails.</summary>
	public static Said InAppOnly => new Said("Do this in the app's own dashboard on your PC - farm.nocat.lol can't.");

	/// <summary>Letters and digits nobody misreads (no 0/O, 1/I/L).</summary>
	private const string CodeLetters = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

	/// <summary>The six characters shown on the page and in the app, for the person to check they match.</summary>
	public static string NewCode() => string.Create(6, 0, static (span, _) => {
		for (int i = 0; i < span.Length; i++) {
			span[i] = CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)];
		}
	});

	// ── routes ──────────────────────────────────────────────────────────────

	public enum Access {
		/// <summary>No token: the hello and the pairing itself.</summary>
		Open,

		/// <summary>With a token.</summary>
		Allowed,

		/// <summary>With a token, and what was sent is looked at too (a command, a settings save, a switch).</summary>
		Checked,

		/// <summary>Never from farm.nocat.lol.</summary>
		Refused
	}

	/// <summary>
	/// Every API route, by method and pattern as it's mapped. One missing here is refused; the checks fail when a route is
	/// mapped that isn't listed, or listed that isn't mapped.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, Access> Routes = new Dictionary<string, Access>(StringComparer.Ordinal) {
		// farm.nocat.lol itself
		["GET /api/farm/hello"] = Access.Open,
		["POST /api/farm/pair"] = Access.Open,
		["GET /api/farm/pair/{id}"] = Access.Open,
		["POST /api/farm/answer"] = Access.Refused,   // approving is for the app's own dashboard only
		["POST /api/farm/revoke"] = Access.Allowed,   // a page can only give its own access back

		// reading
		["GET /api/ping"] = Access.Allowed,
		["GET /api/status"] = Access.Allowed,   // Steam Guard codes and the Telegram connect link left out
		["GET /api/log"] = Access.Allowed,
		["GET /api/history"] = Access.Allowed,
		["GET /api/commands"] = Access.Allowed,
		["GET /api/settings/schema"] = Access.Allowed,
		["GET /api/appnames"] = Access.Allowed,
		["GET /api/config"] = Access.Allowed,   // secrets are never sent anyway
		["GET /api/plugins"] = Access.Allowed,
		["GET /api/plugins/page"] = Access.Allowed,
		["GET /api/addons"] = Access.Allowed,
		["GET /api/update/versions"] = Access.Allowed,
		["GET /api/backups/status"] = Access.Allowed,
		["GET /api/visitors"] = Access.Allowed,
		["GET /api/phone"] = Access.Allowed,
		["GET /api/auth"] = Access.Allowed,   // which accounts have an authenticator - the codes themselves left out
		["GET /api/auth/{name}/confirmations"] = Access.Allowed,   // the list only
		["GET /api/bots/{name}/library"] = Access.Allowed,
		["GET /api/library"] = Access.Allowed,
		["GET /api/bots/{name}/owns"] = Access.Allowed,
		["GET /api/bots/{name}/cards"] = Access.Allowed,
		["GET /api/bots/{name}/achievements"] = Access.Allowed,
		["GET /api/bots/{name}/switch/{feature}/preview"] = Access.Allowed,
		["GET /api/steam-open/everyone/preview"] = Access.Allowed,
		["GET /api/bots/{name}/copy-settings/preview"] = Access.Allowed,
		["GET /api/import/tools"] = Access.Allowed,
		["GET /api/import/scan"] = Access.Allowed,   // never a network share; never a secret in the answer
		["GET /api/import/pending"] = Access.Allowed,

		// doing
		["POST /api/bots/{name}/{action}"] = Access.Allowed,   // start, stop, pause, resume - nothing else is answered there
		["POST /api/bots/{name}/inventory/refresh"] = Access.Allowed,
		["POST /api/visitors/signout"] = Access.Allowed,
		["POST /api/logs/open"] = Access.Allowed,   // opens nothing: farm.nocat.lol is never "at this PC"
		["POST /api/command/history"] = Access.Allowed,
		["POST /api/notify/test"] = Access.Allowed,
		["POST /api/theme"] = Access.Allowed,
		["POST /api/tutorial/done"] = Access.Allowed,
		["POST /api/accounts/order"] = Access.Allowed,
		["POST /api/backups/now"] = Access.Allowed,   // a locked zip in the backups folder here
		["POST /api/import/apply"] = Access.Allowed,   // logins go app to app, here - nothing goes back to the page
		["POST /api/import/pending/clear"] = Access.Allowed,
		["POST /api/bots/{name}/achievements/dlc"] = Access.Allowed,
		["POST /api/bots/{name}/achievements/unlock-all"] = Access.Allowed,
		["POST /api/bots/{name}/copy-settings"] = Access.Allowed,   // never the sign-in, the send target or the trade rules (SettingsCopy)
		["POST /api/steam-open/everyone"] = Access.Allowed,   // the buttons only; pressing one is refused
		["POST /api/command"] = Access.Checked,   // by command word: see Commands below
		["POST /api/config"] = Access.Checked,   // by setting: see SettingRule
		["POST /api/bots/{name}/config"] = Access.Checked,
		["POST /api/bots/{name}/switch"] = Access.Checked,   // every switch but card swaps on

		// only in the app: items leaving, the sign-in and its secrets, running code, the scam hold, the back doors
		["POST /api/login"] = Access.Refused,
		["POST /api/login/code"] = Access.Refused,
		["POST /api/addons/install"] = Access.Refused,
		["POST /api/addons/remove"] = Access.Refused,
		["POST /api/plugins/press"] = Access.Refused,
		["POST /api/plugins/setting"] = Access.Refused,
		["POST /api/plugins/toggle"] = Access.Refused,
		["POST /api/update"] = Access.Refused,
		["POST /api/backup"] = Access.Refused,   // the zip itself, every saved login in it, handed to the page
		["POST /api/restore/check"] = Access.Refused,
		["POST /api/restore/unlock"] = Access.Refused,
		["POST /api/restore/apply"] = Access.Refused,
		["POST /api/discord/connect"] = Access.Refused,
		["POST /api/prompt"] = Access.Refused,   // a Steam Guard code or a password
		["POST /api/bots/{name}/steam-open"] = Access.Refused,
		["GET /api/bots/{name}/steam-open"] = Access.Refused,   // holds the account's Steam Guard code
		["GET /api/bots/{name}/qr.svg"] = Access.Refused,
		["POST /api/bots"] = Access.Refused,
		["DELETE /api/bots/{name}"] = Access.Refused,
		["POST /api/phone/restart"] = Access.Refused,
		["POST /api/phone/firewall"] = Access.Refused,
		["POST /api/auth/{name}/confirmations"] = Access.Refused
	};

	/// <summary>How a route is treated; refused when it isn't listed.</summary>
	public static Access RouteAccess(string method, string pattern) =>
		Routes.TryGetValue($"{method.ToUpperInvariant()} {pattern}", out Access a) ? a : Access.Refused;

	// ── commands ────────────────────────────────────────────────────────────

	/// <summary>
	/// Every command, by name. A function says, from the words after the command (lower case), whether that use of it is
	/// allowed; null means never. A command that isn't listed - a plugin's, or a new one - is refused.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, Func<BotManager?, string[], bool>?> Commands =
		new Dictionary<string, Func<BotManager?, string[], bool>?>(StringComparer.OrdinalIgnoreCase) {
			// Accounts
			["status"] = Always, ["start"] = Always, ["stop"] = Always, ["restart"] = Always, ["pause"] = Always, ["resume"] = Always,
			["enable"] = Always, ["disable"] = Always, ["stuck"] = Always,
			["copy"] = Always,   // never the sign-in, the send target or the trade rules (SettingsCopy)
			["add"] = null, ["remove"] = null,
			["steam"] = static (_, w) => !Has(w, "open"),   // the buttons on or off; signing this PC's Steam in, never

			// Playing
			["play"] = Always, ["gamename"] = Always, ["persona"] = Always, ["nickname"] = Always, ["grind"] = Always, ["human"] = Always,
			["habits"] = Always, ["wake"] = Always, ["hours"] = Always, ["rotation"] = Always, ["selfcheck"] = Always,

			// Trading cards: buying, crafting and packs within the app's own limits (which only change here)
			["cards"] = Always, ["drops"] = Always, ["sets"] = Always, ["addmoney"] = Always, ["booster"] = Always, ["craft"] = Always,
			["buy"] = Always, ["levelup"] = Always,
			["match"] = static (_, w) => !Has(w, "do"),   // sending trade offers
			["sell"] = static (_, w) => !Has(w, "do") && !Has(w, "relist"),   // listing items on the market
			["spare"] = static (_, w) => !Has(w, "do"),   // selling or packing them now; the switch itself may change

			// Trades & items
			["offers"] = Always,
			["trade"] = null, ["send"] = null,
			["swaps"] = static (_, w) => !Has(w, "on"),

			// Steam Guard
			["confirmations"] = Always,   // the list
			["2fa"] = null, ["confirm"] = null, ["deny"] = null,

			// Achievements
			["achievements"] = Always, ["hunt"] = Always, ["dlc"] = Always,

			// Free stuff & keys
			["freegames"] = Always, ["freeitems"] = Always, ["queue"] = Always, ["events"] = Always, ["redeem"] = Always,
			["keys"] = null,   // the product keys waiting, in full

			// Profile & info
			["value"] = Always, ["level"] = Always, ["balance"] = Always, ["bans"] = Always, ["health"] = Always, ["owns"] = Always,
			["joingroup"] = Always, ["points"] = Always, ["privacy"] = Always,
			["scamwatch"] = static (_, w) => !Has(w, "ok"),   // lifting a scam-watch hold

			// Settings
			["config"] = Always,
			["set"] = null,   // by setting - see CommandAllowed
			["backup"] = Always,   // a locked zip in the backups folder here
			["import"] = Always,   // logins go app to app, here; never from a network share

			// The app
			["log"] = Always, ["stats"] = Always, ["report"] = Always, ["plugins"] = Always, ["tutorial"] = Always, ["help"] = Always,
			["theme"] = Always, ["mini"] = Always, ["clear"] = Always, ["visitors"] = Always, ["version"] = Always, ["exit"] = Always,
			["notify"] = static (_, w) => !Has(w, "link"),   // the link makes whoever opens it the owner
			["addons"] = static (_, w) => !Has(w, "install") && !Has(w, "remove"),
			["dashboard"] = static (_, w) => !Has(w, "unlock") && !Has(w, "anywhere") && !Has(w, "remote"),
			["anywhere"] = static (_, w) => !Has(w, "on"),
			["update"] = static (_, w) => (w.Length == 0) || (w[0] is "versions" or "skip"),
			["answer"] = null,   // a Steam Guard code or a password

			// Advanced
			["fairswap"] = Always, ["reload"] = Always, ["screen"] = Always, ["addlicense"] = Always
		};

	private static bool Always(BotManager? mgr, string[] words) => true;

	private static bool Has(string[] words, string word) => words.Contains(word, StringComparer.Ordinal);

	/// <summary>
	/// Whether a line typed in the page's Console may run. By the command the first word reaches, as the console reads it;
	/// 'set' by the setting it changes (<see cref="SettingAllowed"/>). A plugin's command, or anything unknown, is refused.
	/// </summary>
	public static bool CommandAllowed(BotManager? mgr, string line) {
		string[] words = NocatFarm.Commands.Words(line.Trim());

		if (words.Length == 0) {
			return true;
		}

		string typed = NocatFarm.Commands.Unprefixed(words[0]).ToLowerInvariant();

		// 'unlock' is 'dashboard unlock' under its old name.
		if ((typed == "unlock") || (NocatFarm.Commands.Resolve(typed) is not { } def)) {
			return false;
		}

		string[] rest = [.. words[1..].Select(static w => w.ToLowerInvariant())];

		if (def.Name == "set") {
			return SetAllowed(mgr, line.Trim(), words);
		}

		return Commands.TryGetValue(def.Name, out Func<BotManager?, string[], bool>? rule) && (rule != null) && rule(mgr, rest);
	}

	/// <summary>'set [account] key value': the setting it reaches, and the value, as 'set' reads them.</summary>
	private static bool SetAllowed(BotManager? mgr, string line, string[] words) {
		if (words.Length < 3) {
			return words.Length < 2;   // 'set' alone answers with its usage
		}

		bool forAccount = (mgr?.Get(words[1]) != null) && (words.Length >= 3);
		int at = forAccount ? 2 : 1;
		string key = NocatFarm.Commands.KeyOf(words[at]).Key;
		SettingDef? def = forAccount ? Settings.FindBot(key) : Settings.FindGlobal(key);

		if (def == null) {
			return false;
		}

		string value = NocatFarm.Commands.Unquote(NocatFarm.Commands.SetValue(line, words, at)).Trim();

		// The dashboard password from 'set': only setting a first one. Changing it needs the current one (the settings page
		// asks for it); removing it, only here.
		if (def.Name == nameof(GlobalConfig.WebPassword)) {
			return (mgr != null) && string.IsNullOrEmpty(mgr.Global.WebPassword) && (value.Length > 0);
		}

		return SettingAllowed(def, Settings.IsTrue(value) ? true : Settings.IsFalse(value) ? false : null);
	}

	// ── settings ────────────────────────────────────────────────────────────

	public enum SettingWay {
		/// <summary>Changed any way.</summary>
		Open,

		/// <summary>Never changed from there.</summary>
		Closed,

		/// <summary>Only switched off - card swaps, which send cards to other people.</summary>
		OffOnly
	}

	/// <summary>
	/// Settings never changed from there: where items go and who may trade or take them, the money limits (so buying and
	/// selling from there stay inside what was set here), the Steam sign-in and where its traffic goes, the bots' owners and
	/// who may command the accounts, how the dashboard is reached, and loading code. Every secret too, by its kind - the
	/// dashboard password has its own rule (WebHostFarm).
	/// </summary>
	private static readonly HashSet<string> ClosedKeys = new(StringComparer.Ordinal) {
		// items leaving, or going somewhere else
		nameof(BotConfig.TradeMasters), nameof(BotConfig.TradeMasterToken), nameof(BotConfig.AutoTradeWith), nameof(BotConfig.SendItemTypes),
		nameof(BotConfig.SendOnFarmingFinished), nameof(BotConfig.BoosterSendTo), nameof(BotConfig.SpareGemsSendTo),
		nameof(BotConfig.AcceptFairCardSwaps), nameof(BotConfig.AddonTrades), nameof(BotConfig.AddonTradesConfirm),
		// the money limits
		nameof(BotConfig.BuyDailyLimit), nameof(BotConfig.BuyWeeklyLimit), nameof(BotConfig.BuyMaxLevelPrice), nameof(BotConfig.BuyKeepInWallet),
		nameof(BotConfig.PointsWeeklyCap), nameof(BotConfig.PointsKeepAtLeast), nameof(BotConfig.SpareDailyValue), nameof(BotConfig.SpareDailyItems),
		nameof(BotConfig.SpareMaxPrice), nameof(BotConfig.SpareMinSale), nameof(BotConfig.SpareRelistFloorPct), nameof(BotConfig.SpareDumpPct),
		nameof(BotConfig.SpareKeepGems), nameof(BotConfig.BoosterKeepGems), nameof(BotConfig.CardSwapMaxLoss), nameof(BotConfig.CardSwapsPerDay),
		nameof(BotConfig.RefundProtectMinPrice),
		// the sign-in, and where Steam traffic goes
		nameof(BotConfig.SteamLogin), nameof(BotConfig.SignInWithQr), nameof(BotConfig.AccountProxy), nameof(BotConfig.AccountProxyUsername),
		nameof(GlobalConfig.WebProxy), nameof(GlobalConfig.WebProxyUsername),
		// back doors: the bots' owners, who may command the accounts, how the dashboard is reached
		nameof(GlobalConfig.DiscordOwnerId), nameof(GlobalConfig.TelegramChatId), nameof(BotConfig.CommandMasters),
		nameof(GlobalConfig.WebEnabled), nameof(GlobalConfig.WebHost), nameof(GlobalConfig.WebPort), nameof(GlobalConfig.WebRemoteAccess),
		nameof(GlobalConfig.WebPublicAddress), nameof(GlobalConfig.WebTrustedProxies), nameof(GlobalConfig.WebSignInCode),
		nameof(GlobalConfig.WebRemoteOffAfter), nameof(GlobalConfig.FarmWebAllowed),
		// running code; and a backup written to a folder of the page's choosing (a network share) with every login in it
		nameof(GlobalConfig.PluginsEnabled), nameof(GlobalConfig.BackupFolder)
	};

	/// <summary>Only switched off from there: card swaps send cards to other people.</summary>
	private static readonly HashSet<string> OffOnlyKeys = new(StringComparer.Ordinal) { nameof(BotConfig.CardSwaps) };

	/// <summary>Every section a setting is in today. One added later starts closed from there until it's looked at and put here.</summary>
	private static readonly HashSet<string> KnownSections = [Settings.SecGeneral, Settings.SecNotifications, Settings.SecUpdates, Settings.SecBackups,
		Settings.SecAllAccounts, Settings.SecDashboard, Settings.SecDiscordProfile, Settings.SecConnection, Settings.SecSteamConnection, Settings.SecLogging,
		Settings.SecAccount, Settings.SecHuman, Settings.SecPlaying, Settings.SecCards, Settings.SecExtras, Settings.SecBadges, Settings.SecAchievements,
		Settings.SecSafety, Settings.SecTrading, Settings.SecSocial, Settings.SecItems, Settings.SecFamily, Settings.SecSaleEvents, Settings.SecScamWatch,
		Settings.SecHealth, Settings.SecSpare, Settings.SecPacks, Settings.SecPoints, Settings.SecBuy];

	/// <summary>How a setting may be changed from farm.nocat.lol.</summary>
	public static SettingWay WayOf(SettingDef def) =>
		(def.Kind == SettingKind.Secret) || ClosedKeys.Contains(def.Name) || !KnownSections.Contains(def.Section) ? SettingWay.Closed
		: OffOnlyKeys.Contains(def.Name) ? SettingWay.OffOnly
		: SettingWay.Open;

	/// <summary>Whether changing <paramref name="def"/> to <paramref name="on"/> (a switch's new value; null for anything else) is allowed from there.</summary>
	public static bool SettingAllowed(SettingDef def, bool? on) => WayOf(def) switch {
		SettingWay.Open => true,
		SettingWay.OffOnly => on == false,
		_ => false
	};

	/// <summary>Whether every section the settings are in is sorted into one of the lists above - for the checks.</summary>
	internal static bool Sorted(string section) => KnownSections.Contains(section);
}
