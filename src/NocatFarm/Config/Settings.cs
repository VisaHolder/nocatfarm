using System.Globalization;
using System.Reflection;

namespace NocatFarm.Config;

public enum SettingKind {
	Bool,
	Int,
	Float,
	Text,
	Secret,
	AppIds,
	Choice,

	/// <summary>
	/// A choice whose values are TEXT rather than numbers - a language code, say.
	///
	/// Separate from <see cref="Choice"/> because that one parses its values as integers and silently drops
	/// anything that isn't one, so a string-valued list would render with no options at all.
	/// </summary>
	Pick
}

/// <summary>
/// One knob, described once.
///
/// The console's <c>set</c>/<c>config</c>/<c>help set</c>, the dashboard's settings form and the JSON files all
/// read from this list, so a setting has exactly one name and exactly one explanation everywhere it appears.
/// Adding a knob is a property on the config class plus one line here - nothing else has to know about it.
///
/// The tooltips are compiled into the binary on purpose: help text fetched from a web page at runtime breaks the day
/// that page changes, and these work offline and can't rot.
/// </summary>
public sealed record SettingDef(
	string Name,
	string Label,
	string Section,
	SettingKind Kind,
	string Tooltip,
	bool Advanced = false,
	bool NeedsRestart = false,
	string? Choices = null,
	string? Placeholder = null,
	double Min = double.MinValue,
	double Max = double.MaxValue,

	/// <summary>"any" | "legit" (only meaningful in human mode) | "rage" (hidden and neutralised in human mode).</summary>
	string Mode = "any"
);

public static class Settings {
	// ── global sections, in display order ───────────────────────────────────
	public const string SecDashboard = "Dashboard";
	public const string SecBackground = "Running in the background";
	public const string SecRep4RepAccount = "rep4rep account";
	public const string SecConnection = "Steam connection";
	public const string SecLogging = "Logging";
	public const string SecAllAccounts = "All accounts";
	public const string SecNotifications = "Notifications";
	public const string SecDiscordProfile = "Discord profile";
	public const string SecPopups = "Pop-ups";
	public const string SecPrices = "Inventory prices";
	public const string SecUpdates = "Updates & plugins";

	// ── per-account sections, in display order ──────────────────────────────
	public const string SecAccount = "Account";
	public const string SecPlaying = "What it plays";
	public const string SecCards = "Trading cards";
	public const string SecExtras = "Free stuff";
	public const string SecBadges = "Badges, boosters & selling";
	public const string SecInventory = "Inventory & bans";
	public const string SecAchievements = "Achievements";
	public const string SecComments = "rep4rep commenting";
	public const string SecHuman = "Human mode";
	public const string SecSocial = "Friends & messages";
	public const string SecTrading = "Trades";
	public const string SecCourtesy = "Staying out of the way";

	public static readonly IReadOnlyList<SettingDef> Global = ForThisPlatform(BuildGlobal());
	public static readonly IReadOnlyList<SettingDef> Bot = BuildBot();

	public static readonly GlobalConfig GlobalDefaults = new();
	public static readonly BotConfig BotDefaults = new();

	/// <summary>
	/// Without the Windows desktop there is no window, tray icon, pop-up or Windows start-up entry, so on Linux and
	/// in Docker their settings are left off the list (and the settings page) rather than shown doing nothing.
	/// The values stay in the config file untouched, so a config carried back to Windows keeps them.
	/// </summary>
	private static List<SettingDef> ForThisPlatform(List<SettingDef> all) {
		if (OperatingSystem.IsWindows()) {
			return all;
		}

		string[] windowsOnly = ["Tray", "MinimizeToTray", "StartMinimized", "MiniOnTop", "StartWithWindows", "KeepAwake",
			"TrayNotifications", "NotifyEarnings", "NotifySocial", "NotifyProblems", "NotifyTrades"];

		return [.. all.Where(d => !windowsOnly.Contains(d.Name, StringComparer.OrdinalIgnoreCase))];
	}

	public static SettingDef? FindGlobal(string name) => Global.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
	public static SettingDef? FindBot(string name) => Bot.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

	/// <summary>Look a name up in both lists - the console's <c>help set</c> doesn't care which it is.</summary>
	public static SettingDef? Find(string name) => FindGlobal(name) ?? FindBot(name);

	// ── reflection get/set, so there is no switch to keep in sync ───────────
	public static object? Read(object config, string name) =>
		config.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(config);

	/// <summary>Display form of a value: what <c>config</c> prints and what a text box shows.</summary>
	public static string Show(object config, SettingDef def) {
		object? value = Read(config, def.Name);

		if (def.Kind == SettingKind.Secret) {
			return value is string s && s.Length > 0 ? "(set)" : "(not set)";
		}

		if (def.Kind == SettingKind.Choice && value is int choice) {
			return $"{choice} ({ChoiceLabel(def, choice)})";
		}

		if ((def.Kind == SettingKind.Pick) && value is string picked) {
			foreach ((string Value, string Label) option in ParsePicks(def)) {
				if (option.Value.Equals(picked, StringComparison.OrdinalIgnoreCase)) {
					return $"{picked} ({option.Label})";
				}
			}

			return picked;
		}

		return value switch {
			null => "",
			List<uint> apps => apps.Count == 0 ? "(none)" : string.Join(", ", apps),
			bool b => b ? "true" : "false",
			float f => f.ToString("0.##", CultureInfo.InvariantCulture),
			IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString() ?? ""
		};
	}

	public static string ChoiceLabel(SettingDef def, int value) {
		foreach ((int Value, string Label) option in ParseChoices(def)) {
			if (option.Value == value) {
				return option.Label;
			}
		}

		return value.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>The options of a <see cref="SettingKind.Pick"/>, whose values are text.</summary>
	public static List<(string Value, string Label)> ParsePicks(SettingDef def) {
		List<(string, string)> options = [];

		if (string.IsNullOrEmpty(def.Choices)) {
			return options;
		}

		foreach (string option in def.Choices.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			int space = option.IndexOf(' ');

			if (space > 0) {
				options.Add((option[..space], option[(space + 1)..].Trim()));
			}
		}

		return options;
	}

	public static List<(int Value, string Label)> ParseChoices(SettingDef def) {
		List<(int, string)> options = [];

		if (string.IsNullOrEmpty(def.Choices)) {
			return options;
		}

		// "0 offline | 1 online | 7 invisible"
		foreach (string option in def.Choices.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			int space = option.IndexOf(' ');

			if ((space > 0) && int.TryParse(option[..space], out int value)) {
				options.Add((value, option[(space + 1)..].Trim()));
			}
		}

		return options;
	}

	/// <summary>Apply a typed value from raw text. Returns null on success, or why it was rejected.</summary>
	public static string? Apply(object config, SettingDef def, string raw) {
		PropertyInfo? p = config.GetType().GetProperty(def.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

		if (p == null || !p.CanWrite) {
			return $"'{def.Name}' can't be changed";
		}

		raw = raw.Trim();

		switch (def.Kind) {
			case SettingKind.Bool:
				// A word that is neither is refused, like a bad number - "set new Rep4Rep enabled" quietly switched it off.
				if (!IsTrue(raw) && !IsFalse(raw)) {
					return $"{def.Label} must be on or off";
				}

				p.SetValue(config, IsTrue(raw));

				return null;

			case SettingKind.Int: {
				if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) {
					return $"{def.Label} must be a whole number";
				}

				if (i < def.Min || i > def.Max) {
					return $"{def.Label} must be between {Bound(def.Min)} and {Bound(def.Max)}";
				}

				p.SetValue(config, i);

				return null;
			}

			case SettingKind.Float: {
				if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) {
					return $"{def.Label} must be a number";
				}

				if (f < def.Min || f > def.Max) {
					return $"{def.Label} must be between {Bound(def.Min)} and {Bound(def.Max)}";
				}

				p.SetValue(config, f);

				return null;
			}

			case SettingKind.Choice: {
				if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)) {
					// Accept the label too: "invisible" is friendlier to type than "7".
					int? matched = MatchChoice(def, raw);

					if (matched == null) {
						return $"{def.Label} must be one of: {def.Choices}";
					}

					c = matched.Value;
				} else if (ParseChoices(def).TrueForAll(o => o.Value != c)) {
					return $"{def.Label} must be one of: {def.Choices}";
				}

				p.SetValue(config, c);

				return null;
			}

			case SettingKind.Pick: {
				List<(string Value, string Label)> options = ParsePicks(def);

				// The code or the label - "de" and "Deutsch" should both work from the console.
				foreach ((string Value, string Label) option in options) {
					if (option.Value.Equals(raw, StringComparison.OrdinalIgnoreCase)
						|| option.Label.Equals(raw, StringComparison.OrdinalIgnoreCase)) {
						p.SetValue(config, option.Value);

						return null;
					}
				}

				return $"{def.Label} must be one of: {string.Join(", ", options.Select(static o => o.Value))}";
			}

			case SettingKind.AppIds: {
				List<uint> apps = [];

				if (!raw.Equals("none", StringComparison.OrdinalIgnoreCase)) {
					foreach (string token in raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
						// Accept a pasted store URL as well as a bare number.
						string digits = ExtractAppId(token);

						if (!uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out uint app) || (app == 0)) {
							return $"'{token}' is not an appID - it's the number in a game's store URL";
						}

						if (!apps.Contains(app)) {
							apps.Add(app);
						}
					}
				}

				// Steam's limit is on games played SIMULTANEOUSLY, so it applies to the lists that get played and
				// to nothing else. Capping a blacklist at 32 rejected a perfectly sensible "never touch these
				// forty games" - a list whose whole purpose is that they are never played.
				if (def.Name is "IdleGames" or "OfflineIdleGames" && (apps.Count > Core.SteamIds.GamesAtOnce)) {
					return $"Steam only lets an account play {Core.SteamIds.GamesAtOnce} games at once";
				}

				p.SetValue(config, apps);

				return null;
			}

			default:
				p.SetValue(config, raw);

				return null;
		}
	}

	/// <summary>An appID from a bare number or a store URL, or 0 when it is neither (i.e. it's a name).</summary>
	public static uint AppIdFrom(string token) =>
		uint.TryParse(ExtractAppId(token.Trim()), out uint id) ? id : 0;

	/// <summary>"https://store.steampowered.com/app/730/CS2/" -> "730". A bare number passes straight through.</summary>
	private static string ExtractAppId(string token) {
		const string Marker = "/app/";
		int at = token.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);

		if (at < 0) {
			return token;
		}

		int start = at + Marker.Length;
		int end = start;

		while ((end < token.Length) && char.IsAsciiDigit(token[end])) {
			end++;
		}

		return token[start..end];
	}

	private static string Bound(double v) => v is <= double.MinValue or >= double.MaxValue ? "anything" : v.ToString("0.##", CultureInfo.InvariantCulture);

	private static int? MatchChoice(SettingDef def, string raw) {
		foreach ((int Value, string Label) option in ParseChoices(def)) {
			if (option.Label.StartsWith(raw, StringComparison.OrdinalIgnoreCase)) {
				return option.Value;
			}
		}

		return null;
	}

	/// <summary>
	/// Every "shortest / longest" pair in the registry, by setting name.
	///
	/// Derived from the names rather than listed by hand, so a pair added later is covered without anyone
	/// remembering to come back here.
	/// </summary>
	public static IReadOnlyList<(string Min, string Max)> RangePairs { get; } = BuildRangePairs();

	private static List<(string, string)> BuildRangePairs() {
		HashSet<string> names = [.. Bot.Select(static d => d.Name), .. Global.Select(static d => d.Name)];

		return [.. names
			.Where(static n => n.Contains("Min", StringComparison.Ordinal))
			.Select(static n => (Min: n, Max: ReplaceFirst(n, "Min", "Max")))
			.Where(pair => names.Contains(pair.Max))
			.OrderBy(static pair => pair.Min, StringComparer.Ordinal)];
	}

	private static string ReplaceFirst(string text, string find, string with) {
		int at = text.IndexOf(find, StringComparison.Ordinal);

		return at < 0 ? text : text[..at] + with + text[(at + find.Length)..];
	}

	/// <summary>
	/// Pull any "longest" that has fallen below its "shortest" back up to meet it.
	///
	/// A max under its min makes every gap calculation nonsense - and while the consumers all guard themselves
	/// with Math.Max, the stored pair still reads as a contradiction and the dashboard shows it as one. The
	/// dashboard used to fix exactly ONE of the eight pairs, and the console fixed none, so `set kylro
	/// Rep4RepGapMinMinutes 500` next to a max of 5 was accepted and written to disk.
	/// </summary>
	/// <param name="justSet">
	/// The setting the user has this moment changed, if any. Whichever side that is WINS and its partner moves:
	/// lowering a "longest" below its "shortest" used to be silently undone, so the number you had just typed
	/// snapped back and nothing said why. The value someone explicitly asked for is never the one to overwrite.
	/// </param>
	/// <returns>One human-readable line per pair that had to be moved.</returns>
	public static List<string> FixRanges(object config, string? justSet = null) {
		List<string> adjusted = [];

		foreach ((string min, string max) in RangePairs) {
			if ((Read(config, min) is not int lo) || (Read(config, max) is not int hi) || (hi >= lo)) {
				continue;
			}

			// Move the side the user did NOT just touch.
			bool moveMin = string.Equals(justSet, max, StringComparison.Ordinal);
			string moving = moveMin ? min : max;
			int target = moveMin ? hi : lo;

			SettingDef? def = Bot.FirstOrDefault(d => d.Name == moving) ?? Global.FirstOrDefault(d => d.Name == moving);

			if (def == null) {
				continue;
			}

			Apply(config, def, target.ToString(System.Globalization.CultureInfo.InvariantCulture));
			adjusted.Add($"{def.Label} moved to {target} to match");
		}

		return adjusted;
	}

	/// <summary>
	/// Apply the consequences of Legit mode to a config, in the file itself rather than only in the UI.
	///
	/// Turning it ON stashes the settings that don't belong on a believable account and blanks them; turning it
	/// OFF puts them back exactly as they were. The user asked for the config to be genuinely clean while human
	/// mode is on, not just visually filtered.
	/// </summary>
	public static void ApplyLegitMode(BotConfig cfg, bool wasLegit) {
		if (cfg.LegitMode == wasLegit) {
			return;
		}

		if (cfg.LegitMode) {
			// Stash, then clear. Human mode drives what plays via GameWeights instead.
			cfg.LegitBackup = string.Join('|', [
				"IdleGames=" + string.Join(',', cfg.IdleGames)
			]);

			if ((cfg.GameWeights.Length == 0) && (cfg.IdleGames.Count > 0)) {
				// Seed the weights from what it was already idling: the first game becomes the main one.
				List<string> parts = [];

				for (int i = 0; i < cfg.IdleGames.Count; i++) {
					parts.Add(cfg.IdleGames[i] + ":" + (i == 0 ? 70 : Math.Max(1, 30 / Math.Max(1, cfg.IdleGames.Count - 1))));
				}

				cfg.GameWeights = string.Join(", ", parts);
			}

			cfg.IdleGames = [];

			return;
		}

		foreach (string entry in cfg.LegitBackup.Split('|', StringSplitOptions.RemoveEmptyEntries)) {
			int eq = entry.IndexOf('=');

			if ((eq <= 0) || (entry[..eq] != "IdleGames")) {
				continue;
			}

			List<uint> restored = [];

			foreach (string token in entry[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries)) {
				if (uint.TryParse(token, out uint app)) {
					restored.Add(app);
				}
			}

			cfg.IdleGames = restored;
		}

		cfg.LegitBackup = "";
	}

	/// <summary>On, in any casing - "ON" and "Yes" used to read as off.</summary>
	public static bool IsTrue(string v) => v.ToLowerInvariant() is "1" or "on" or "yes" or "y" or "true";

	private static bool IsFalse(string v) => v.ToLowerInvariant() is "0" or "off" or "no" or "n" or "false";

	// ═════════════════════════════════════════════════════════════════════════
	//  GLOBAL
	// ═════════════════════════════════════════════════════════════════════════
	private static List<SettingDef> BuildGlobal() => [
		// ── Dashboard ──
		new("WebEnabled", "Web dashboard", SecDashboard, SettingKind.Bool,
			"Turns on the web dashboard. With it off, everything still works and you control nocat.farm by typing commands in the console.",
			NeedsRestart: true),
		new("Language", "Language", SecDashboard, SettingKind.Pick,
			"The language of the dashboard, status and log lines. Anything not translated yet shows in English. Replies to typed console commands stay in English.",
			Choices: "en English | es Español | pt-BR Português (Brasil) | ru Русский | de Deutsch | fr Français | zh-CN 简体中文 | tr Türkçe | pl Polski | ja 日本語 | ko 한국어"),
		new("WebHost", "Listen on", SecDashboard, SettingKind.Text,
			"Which addresses the dashboard answers on. 127.0.0.1 means this PC only. Use 0.0.0.0 to open it from your phone or another PC, but set a password first.",
			NeedsRestart: true, Placeholder: "127.0.0.1", Advanced: true),
		new("WebPort", "Port", SecDashboard, SettingKind.Int,
			"The port the dashboard runs on. Change it only if another program already uses this port.",
			Advanced: true, NeedsRestart: true, Min: 1, Max: 65535),
		new("WebPassword", "Dashboard password", SecDashboard, SettingKind.Secret,
			"Password for the dashboard. While it is empty, only this PC can open the dashboard, so nobody else can reach your Steam accounts.",
			Advanced: true, NeedsRestart: true),
		new("OpenDashboardAfterAdd", "Open the dashboard after adding an account", SecDashboard, SettingKind.Bool,
			"Opens the dashboard after you add an account, so you can set it up. A new account only farms cards until you tell it what to play, and that is done in the dashboard.", Advanced: true),
		new("OpenBrowserOnStart", "Open the browser on start", SecDashboard, SettingKind.Bool,
			"Opens the dashboard in your browser when nocat.farm starts.", Advanced: true),
		new("WebRefreshSeconds", "Refresh every", SecDashboard, SettingKind.Int,
			"How often the dashboard updates itself, in seconds. Lower feels snappier and costs nothing, since it all runs on your PC.",
			Advanced: true, Min: 1, Max: 60),
		new("WebSessionDays", "Stay signed in for", SecDashboard, SettingKind.Int,
			"How many days a browser stays signed in before it asks for the dashboard password again.",
			Advanced: true, Min: 1, Max: 90),
		new("WebRemoteAccess", "Open from anywhere", SecDashboard, SettingKind.Bool,
			"Like Jellyfin: asks your router to forward the dashboard's port to this PC (UPnP), so it opens from anywhere at your internet address - /dashboard on Telegram and Discord gives the link. Needs a dashboard password of at least 12 characters and 0.0.0.0 in Listen on. Anyone on the internet can reach the sign-in page, and five wrong passwords lock them out for an hour. The link is plain http, not encrypted, so sign in over mobile data or a network you trust. Turning it off takes the forward away again.",
			Advanced: true),
		new("WebPublicAddress", "Public address", SecDashboard, SettingKind.Text,
			"Only if you forwarded the port by hand or use a name like myname.duckdns.org: the address to give out for the dashboard from outside your home. Leave it empty with Open from anywhere on - it finds your address by itself.",
			Advanced: true, Placeholder: "myname.duckdns.org"),
		// ── Running in the background ──
		new("Tray", "Tray icon", SecBackground, SettingKind.Bool,
			"Shows an icon by the clock so nocat.farm can run in the background. Right-click the icon for the menu.",
			NeedsRestart: true),
		new("MinimizeToTray", "Minimise to the tray", SecBackground, SettingKind.Bool,
			"Minimising the window hides it to the tray icon instead of the taskbar.",
			Advanced: true),
		new("StartMinimized", "Start hidden", SecBackground, SettingKind.Bool,
			"Starts with the window hidden in the tray. Double-click the tray icon to bring it back.", Advanced: true),
		new("MiniOnTop", "Keep mini mode on top", SecBackground, SettingKind.Bool,
			"Keeps the small mini mode window above your other windows. The pin in the mini window's title bar switches this too.",
			Advanced: true),
		new("StartWithWindows", "Start with Windows", SecBackground, SettingKind.Bool,
			"Starts nocat.farm when you sign in to Windows. It only adds a startup entry for your own Windows user."),
		new("KeepAwake", "Keep this PC awake", SecBackground, SettingKind.Bool,
			"Stops your PC from going to sleep while nocat.farm is open. The screen can still turn off.", Advanced: true),
		new("ExitWhenAllFinished", "Close when everything's done", SecBackground, SettingKind.Bool,
			"Closes nocat.farm once every account has finished farming and logged out, instead of leaving it idle in the tray.",
			Advanced: true),
		// ── All accounts ──
		new("GroupsToJoin", "Groups every account joins", SecAllAccounts, SettingKind.Text,
			"Steam groups all your accounts join, one after another. It starts with the nocat.farm group, and you can change or clear it. Paste a group's link or its short name, separated by commas. Only open groups are joined, ones that need approval or an invite are skipped.",
			Placeholder: "steamcommunity.com/groups/yourgroup"),
		new("GlobalBlacklistedGames", "Never touch these (all accounts)", SecAllAccounts, SettingKind.AppIds,
			"Games no account will ever farm or idle, listed by game ID (the number in its store link). Each account can still add its own on top.",
			Advanced: true),
		// ── Notifications ──
		new("TelegramBotToken", "Telegram bot token", SecNotifications, SettingKind.Secret,
			"Paste your Telegram bot's token (make a bot by messaging @BotFather - the guide above shows how), press Save, then press Connect Telegram and press Start in Telegram.",
			Placeholder: "123456789:ABC..."),
		new("TelegramCommands", "Take commands from Telegram", SecNotifications, SettingKind.Bool,
			"Lets you control nocat.farm from your Telegram chat: /status for a summary, /console to type commands like in the window, and every other command with a / in front. Only your own connected chat is listened to."),
		new("TelegramChatId", "Telegram chat", SecNotifications, SettingKind.Text,
			"Filled in by itself when you connect with the Connect Telegram button. Clear it to connect a different chat - the button comes back. You can also type a group's chat ID here by hand.",
			Advanced: true),
		new("DiscordBotToken", "Discord bot token", SecNotifications, SettingKind.Secret,
			"For commands from Discord, like /status. Paste your Discord bot's token (the guide above shows how to make a bot), press Save, then press Connect Discord and send /connect with the code to your bot. Not needed for notifications - those use the webhook.",
			Placeholder: "MTIz..."),
		new("DiscordCommands", "Take commands from Discord", SecNotifications, SettingKind.Bool,
			"Lets you control nocat.farm from Discord with / commands: /status for a summary, /nocat to run any console command, and the everyday ones like /cards. Works in a private chat with your bot and in your server. Only your own connected Discord account is obeyed. Off keeps the bot offline."),
		new("DiscordOwnerId", "Discord owner", SecNotifications, SettingKind.Text,
			"The one Discord account your bot obeys - filled in by itself when you send /connect with the code from Connect Discord. Clear it to connect a different account.",
			Advanced: true),
		new("DiscordWebhookUrl", "Discord webhook", SecNotifications, SettingKind.Secret,
			"Paste a Discord channel's webhook link and the notifications you pick below go there. In Discord: Server Settings, Integrations, Webhooks, New Webhook, Copy Webhook URL.",
			Placeholder: "https://discord.com/api/webhooks/..."),
		new("SendCardDrops", "Send card drops and badges", SecNotifications, SettingKind.Bool,
			"Cards dropping, a game's cards finished, badges crafted and booster packs made.", Advanced: true),
		new("SendFreeStuff", "Send free games, items and gifts", SecNotifications, SettingKind.Bool,
			"Free games claimed, free Points Shop and sale items, gifted games and activated keys.", Advanced: true),
		new("SendTrades", "Send trades", SecNotifications, SettingKind.Bool,
			"Trade offers accepted, with how many items came in.", Advanced: true),
		new("SendProblems", "Send problems that need you", SecNotifications, SettingKind.Bool,
			"Anything that needs you: a Steam Guard code, a failed sign-in, a comment ban, listings waiting for your phone.", Advanced: true),
		new("SendUpdates", "Send updates", SecNotifications, SettingKind.Bool,
			"A new version is out, and what's new in it.", Advanced: true),
		new("SendInstalls", "Send install progress", SecNotifications, SettingKind.Bool,
			"When an update installs: downloaded and installing, then installed with the new version and what's new - or that it failed or was undone, and why. Off unless you turn it on.", Advanced: true),
		new("SendDailySummary", "Send the daily summary", SecNotifications, SettingKind.Bool,
			"Once a day, what each account did in the last 24 hours: hours banked, cards and comments. Sent at the time set for \"Daily summary in the log\".", Advanced: true),
		new("SendComments", "Send profile comments", SecNotifications, SettingKind.Bool,
			"Someone commented on one of your Steam profiles.", Advanced: true),
		new("SendAchievements", "Send achievements", SecNotifications, SettingKind.Bool,
			"Every achievement unlocked. Can be a lot.", Advanced: true),
		new("SendRep4Rep", "Send rep4rep comments", SecNotifications, SettingKind.Bool,
			"Every rep4rep comment posted. Can be a lot.", Advanced: true),
		// ── Discord profile ──
		new("DiscordPresence", "Show on my Discord profile", SecDiscordProfile, SettingKind.Bool,
			"While nocat.farm is open, your Discord profile shows Playing nocat.farm - like a game - with what it's doing and the cards it got today. Needs the Discord app open on this PC, and Activity Privacy in Discord letting it share what you play.",
			Advanced: true),
		new("DiscordPresenceAccounts", "Accounts it shows", SecDiscordProfile, SettingKind.Text,
			"Which accounts the Discord card counts. Leave it empty for every account that isn't in human mode, type all for every account, or list names separated by commas.",
			Advanced: true, Placeholder: "every account not in human mode"),
		new("DiscordFeatured", "Featured account", SecDiscordProfile, SettingKind.Text,
			"The account whose Steam avatar sits on the corner of the nocat.farm logo on your Discord card - clicking it opens that account's Steam profile. Any account works, human mode too. Empty uses the first account shown.",
			Advanced: true, Placeholder: "an account name"),
		new("DiscordShowNames", "Show account names", SecDiscordProfile, SettingKind.Bool,
			"The second line of the Discord card lists the accounts by their Steam names, like kylro · old. Off shows what Second line picks instead.",
			Advanced: true),
		new("DiscordSecondLine", "Second line", SecDiscordProfile, SettingKind.Choice,
			"What the second line of the Discord card counts when account names are off: the cards dropped today, or the hours played in the past week or the past month - added up over the accounts the card shows.",
			Advanced: true, Choices: "0 cards today | 1 hours past week | 2 hours past month"),
		new("DiscordShowCounter", "Show accounts online", SecDiscordProfile, SettingKind.Bool,
			"Adds how many of the shown accounts are signed in to the Discord card, like \"2 accounts on\" or \"2 of 3 accounts on\".",
			Advanced: true),
		new("DiscordShowAvatar", "Show an account's avatar", SecDiscordProfile, SettingKind.Bool,
			"Puts the first shown account's Steam avatar in the corner of the nocat.farm logo. Hovering it shows the name, clicking it opens the Steam profile.",
			Advanced: true),
		new("DiscordShowTimer", "Show how long it's been running", SecDiscordProfile, SettingKind.Bool,
			"The Discord card counts up from when nocat.farm was opened, like a game's play time.",
			Advanced: true),
		new("DiscordButton1", "Button 1", SecDiscordProfile, SettingKind.Text,
			"The first of the two buttons Discord allows on the card. github for a Get nocat.farm button, an account name for a link to its Steam profile, your own as Label | https://link, or empty for none. Other people see the buttons - Discord doesn't show your own to you.",
			Advanced: true, Placeholder: "github"),
		new("DiscordButton2", "Button 2", SecDiscordProfile, SettingKind.Text,
			"The second button, the same way: github, an account name, Label | https://link, or empty for none.",
			Advanced: true, Placeholder: "an account name"),
		// ── Pop-ups ──
		new("TrayNotifications", "Show pop-ups", SecPopups, SettingKind.Bool,
			"Turns pop-up notifications on or off. The four settings below choose which pop-ups you get."),
		new("NotifyEarnings", "Pop up when you earn", SecPopups, SettingKind.Bool,
			"Shows a pop-up when a trading card drops or a rep4rep comment is credited.", Advanced: true),
		new("NotifySocial", "Pop up for comments", SecPopups, SettingKind.Bool,
			"Shows a pop-up when someone comments on one of your Steam profiles.", Advanced: true),
		new("NotifyProblems", "Pop up for problems", SecPopups, SettingKind.Bool,
			"Shows a pop-up when an account needs you, like a Steam Guard code, a failed login or a comment ban.", Advanced: true),
		new("NotifyTrades", "Pop up for trade offers", SecPopups, SettingKind.Bool,
			"Shows a pop-up for every new trade offer - who it's from, what you'd give and get, and what nocat.farm will do - and when one is accepted, declined or needs confirming.", Advanced: true),
		// ── Inventory prices ──
		new("MarketCurrency", "Inventory prices in", SecPrices, SettingKind.Choice,
			"The currency inventory values are shown in. Pick the one your Steam store uses so totals match the market. Changing it re-checks every price.",
			Choices: "1 US dollar | 20 Canadian dollar | 21 Australian dollar | 2 British pound | 3 Euro | 5 Russian rouble | 7 Brazilian real | 8 Japanese yen | 23 Chinese yuan | 24 Indian rupee"),
		new("MarketGapSeconds", "Seconds between price lookups", SecPrices, SettingKind.Int,
			"Seconds to wait between each Steam market price lookup. Too fast and Steam starts refusing, which leaves inventory values out of date. Higher is slower but steadier.",
			Advanced: true, Min: 1, Max: 60),
		new("PriceCacheHours", "Trust a price for", SecPrices, SettingKind.Int,
			"How many hours a price is reused before it is checked again. Cheap items are checked less often. Longer means fewer requests, but values update more slowly.",
			Advanced: true, Min: 1, Max: 168),
		// ── rep4rep account ──
		new("Rep4RepEnabled", "Use rep4rep at all", SecRep4RepAccount, SettingKind.Bool,
			"Turns on rep4rep, an optional outside site where users trade Steam profile comments. With it off, all rep4rep features and settings are hidden on every account."),
		new("Rep4RepApiToken", "API token", SecRep4RepAccount, SettingKind.Secret,
			"Your rep4rep API token, found on rep4rep.com under Settings. One token covers all your Steam accounts. No account yet? rep4rep.com/?r=reap"),
		new("Rep4RepPauseHours", "Hold commenting for (hours)", SecRep4RepAccount, SettingKind.Int,
			"Pauses rep4rep commenting on every account for this many hours, then it starts again by itself. The countdown keeps going through restarts. Set 0 for no pause.",
			Advanced: true, Min: 0, Max: 720),
		new("Rep4RepAutoAddProfiles", "Register accounts automatically", SecRep4RepAccount, SettingKind.Bool,
			"Adds each Steam account to rep4rep the first time it logs in, so you don't have to add them on the website.", Advanced: true),
		new("Rep4RepPointsRefreshMinutes", "Check points every", SecRep4RepAccount, SettingKind.Int,
			"How often to check your rep4rep points total, in minutes. Avoid very low values for long stretches, since rep4rep has no published limit.",
			Advanced: true, Min: 1, Max: 1440),
		// ── Updates & plugins ──
		new("AutoUpdate", "Update by itself", SecUpdates, SettingKind.Choice,
			"What happens when a new version is out. Tell me: it says so in the log, on Telegram and Discord, and you type update accept. Install at night: it installs by itself in the hours below, only while no human-mode account is awake and nobody is playing on an account, and tells you before and after. Not in Docker or as a Linux service, which update by hand.",
			Choices: "0 tell me | 1 install at night"),
		new("UpdateWhenAsked", "When I say update", SecUpdates, SettingKind.Choice,
			"What update accept and the Update button do. Right away: it installs now. When my accounts are asleep: it waits until no human-mode account is awake, nobody is playing on an account and no trade or gift is waiting - robot accounts that stay on all night don't hold it up. With no human-mode accounts, it waits for the hours below instead. update now always installs right away. Not in Docker or as a Linux service, which update by hand.",
			Choices: "0 right away | 1 when my accounts are asleep"),
		new("AutoUpdateFromHour", "...between", SecUpdates, SettingKind.Int,
			"The earliest hour it may install by itself, on a 24-hour clock.",
			Advanced: true, Min: 0, Max: 23),
		new("AutoUpdateUntilHour", "...and", SecUpdates, SettingKind.Int,
			"The hour it stops trying for the night. Earlier than the start hour wraps past midnight, so 23 to 5 works.",
			Advanced: true, Min: 0, Max: 24),
		new("AutoUpdateWaitHours", "Wait after a release for", SecUpdates, SettingKind.Int,
			"Hours to wait after a new version comes out before installing it by itself, so a release with a problem can be fixed first.",
			Advanced: true, Min: 0, Max: 168),
		new("CheckForUpdates", "Notify if an update is available", SecUpdates, SettingKind.Bool,
			"Looks for a new version and tells you in the log, on Telegram and Discord. Turning it off also stops Update by itself.",
			Advanced: true),
		new("UpdateCheckHours", "Look for updates every", SecUpdates, SettingKind.Int,
			"Hours between two looks for a new version. It also looks once when nocat.farm starts, and whenever you type update.",
			Advanced: true, Min: 1, Max: 24),
		new("UpdateReminders", "Remind me every hour", SecUpdates, SettingKind.Bool,
			"Reminds you in the log every hour while a new version is out. Type update accept to install it, or update skip to skip that version.",
			Advanced: true),
		new("PluginsEnabled", "Load plugins", SecUpdates, SettingKind.Bool,
			"Loads plugins from the plugins folder. Only turn this on for plugins you wrote or fully trust. A plugin can reach everything nocat.farm can, including your Steam logins.",
			Advanced: true, NeedsRestart: true),
		// ── Steam connection ──
		new("LoginStaggerSeconds", "Gap between logins", SecConnection, SettingKind.Int,
			"Seconds between two accounts signing in - at startup, start all, or a reconnect - give or take a little, and never under 5. Steam limits logins per internet connection, so a gap stops several accounts getting blocked.",
			Min: 0, Max: 600, Advanced: true),
		new("ReconnectDelaySeconds", "Reconnect after", SecConnection, SettingKind.Int,
			"How long to wait before reconnecting after Steam drops the connection, in seconds.",
			Advanced: true, Min: 1, Max: 600),
		new("ConnectionTimeoutSeconds", "Connection timeout", SecConnection, SettingKind.Int,
			"How long to wait for Steam to answer before giving up and reconnecting, in seconds.",
			Advanced: true, NeedsRestart: true, Min: 5, Max: 255),
		new("MaxConcurrentFarming", "Farm at most", SecConnection, SettingKind.Int,
			"How many accounts can farm trading cards at the same time. 0 means no limit.",
			Advanced: true, Min: 0, Max: 100),
		new("LoginCooldownMinutes", "Rate-limit cooldown", SecConnection, SettingKind.Int,
			"How many minutes every account waits after Steam blocks a login for trying too often. They all wait together so they don't get blocked one after another.",
			Advanced: true, Min: 1, Max: 240),
		new("WebRequestGapMs", "Gap between web requests", SecConnection, SettingKind.Int,
			"Shortest wait between two requests to the same Steam website, in milliseconds. Raise it if you see rate-limit warnings, and never go below 200.",
			Advanced: true, Min: 0, Max: 5000),
		new("SteamProtocol", "Connect using", SecConnection, SettingKind.Choice,
			"How to connect to Steam. Leave it on automatic unless a firewall or your internet provider blocks it. Websocket only looks like normal web traffic and usually gets through.",
			Advanced: true, NeedsRestart: true, Choices: "0 automatic | 1 websocket only | 2 tcp only"),
		new("WebProxy", "Proxy", SecConnection, SettingKind.Text,
			"Sends Steam traffic through this proxy, for example http://127.0.0.1:8080. Leave it empty to connect directly.",
			Advanced: true, NeedsRestart: true, Placeholder: "http://host:port"),
		new("WebProxyUsername", "Proxy user name", SecConnection, SettingKind.Text,
			"User name for the proxy above, if it needs one.",
			Advanced: true, NeedsRestart: true),
		new("WebProxyPassword", "Proxy password", SecConnection, SettingKind.Secret,
			"Password for the proxy above, if it needs one.",
			Advanced: true, NeedsRestart: true),
		// ── Logging ──
		new("StatusEveryMinutes", "Say what it's doing every", SecLogging, SettingKind.Int,
			"How often each account says in the log what it's doing while a game is open, in minutes. 0 turns it off. An account can set its own under its Logging settings.",
			Min: 0, Max: 240, Advanced: true),
		new("StatusQuietEveryMinutes", "And while it's resting, every", SecLogging, SettingKind.Int,
			"The same, for when an account is asleep, on a break, paused or signed out. 0 turns it off.",
			Min: 0, Max: 1440, Advanced: true),
		new("FileLogging", "Write a log file", SecLogging, SettingKind.Bool,
			"Saves everything to log files as well as showing it on screen. Leave this on to see what happened while you were away.",
			Advanced: true),
		new("Debug", "Show debug detail on screen", SecLogging, SettingKind.Bool,
			"Shows extra debug detail in the window and console. That detail is always saved to the log file anyway, so leave this off unless you want to watch it live.",
			Advanced: true),
		new("LogRetentionDays", "Keep logs for", SecLogging, SettingKind.Int,
			"Deletes log files older than this many days. 0 keeps them forever.",
			Advanced: true, Min: 0, Max: 3650),
		new("DailyReportEnabled", "Daily summary in the log", SecLogging, SettingKind.Bool,
			"Writes a daily summary in the log of what each account earned in the last 24 hours: hours played, cards and rep4rep comments. Type stats to see it any time.",
			Advanced: true),
		new("DailyReportHour", "Summary time · hour", SecLogging, SettingKind.Int,
			"The hour the daily summary is written, in local time. Only used when \"Daily summary in the log\" is on.",
			Min: 0, Max: 23, Advanced: true),
		new("DailyReportMinute", "Summary time · minute", SecLogging, SettingKind.Int,
			"The minute past the hour the daily summary is written. Hour 9 and minute 30 means 09:30.",
			Min: 0, Max: 59, Advanced: true),
		new("TelegramLogColour", "Telegram's colour in the log", SecLogging, SettingKind.Choice,
			"The colour of \"telegram\" in the log - commands sent from Telegram and their answers - like an account's colour.",
			Choices: NocatFarm.Core.NameColour.ChoicesSpec, Advanced: true),
		new("DiscordLogColour", "Discord's colour in the log", SecLogging, SettingKind.Choice,
			"The colour of \"discord\" in the log - the Discord card and Discord notifications - like an account's colour.",
			Choices: NocatFarm.Core.NameColour.ChoicesSpec, Advanced: true),
	];

	// ═════════════════════════════════════════════════════════════════════════
	//  PER ACCOUNT
	// ═════════════════════════════════════════════════════════════════════════
	private static List<SettingDef> BuildBot() => [
		// ── Account ──
		new("Enabled", "Enabled", SecAccount, SettingKind.Bool,
			"Signs this account in. Turn it off to keep its settings but leave the account completely alone."),
		new("SteamLogin", "Steam account name", SecAccount, SettingKind.Text,
			"The account name you type when signing in to Steam. Not your display name and not your email."),
		new("SteamPassword", "Password", SecAccount, SettingKind.Secret,
			"Optional. Leave it empty and you'll be asked once, then the account is remembered with a login token, which is safer than a saved password."),
		new("SignInWithQr", "Sign in with a QR code", SecAccount, SettingKind.Bool,
			"Shows a QR code on the dashboard to scan with the Steam phone app when this account needs signing in. Nothing gets typed, not even the account name.",
			Advanced: true),
		new("OnlineStatus", "Appear as", SecAccount, SettingKind.Choice,
			"How this account looks to your friends. Invisible still plays and farms, nobody just sees it. With \"Human mode\" on this is only the status while playing, so leave it on Online.",
			Choices: "0 offline | 1 online | 2 busy | 3 away | 4 snooze | 5 looking to trade | 6 looking to play | 7 invisible"),
		new("IUseThisAccount", "I sign into this one myself", SecAccount, SettingKind.Bool,
			"Turn this on for an account you also use in your own Steam app. It then never changes the online status, because that would sign you out of Friends and Chat. Everything else still runs."),
		new("UIMode", "Sign in as", SecAccount, SettingKind.Choice,
			"What kind of Steam app this says it is when signing in. Keep Desktop, since it matches the real Steam app. The others are only for troubleshooting.",
			Choices: "7 desktop (default) | 3 web | 2 mobile | 1 big picture | 0 desktop, legacy",
			Advanced: true),
		new("StartPaused", "Start paused", SecAccount, SettingKind.Bool,
			"Signs in but doesn't play, farm or comment until you press Resume. Good for an account you want online and doing nothing.", Advanced: true),
		new("Notes", "Notes", SecAccount, SettingKind.Text,
			"A note to yourself about what this account is for. It shows on the account card and is never sent to Steam.",
			Advanced: true, Placeholder: "main idler, don't touch"),
		new("SteamParentalCode", "Family View PIN", SecAccount, SettingKind.Secret,
			"The Family View PIN, if this account has Family View on. Without it Steam blocks the pages card farming needs.",
			Advanced: true),
		new("MachineName", "Device name", SecAccount, SettingKind.Text,
			"The device name this account shows in Steam's list of authorised devices. Leave it empty to use this PC's real name.",
			Advanced: true),
		new("SharedSecret", "Authenticator code secret", SecAccount, SettingKind.Secret,
			"The authenticator secret that lets it enter Steam Guard codes itself, so the account signs back in on its own after a reboot. Putting its maFile in the authenticators folder does the same. Keep it private.",
			Advanced: true),
		new("IdentitySecret", "Authenticator confirm secret", SecAccount, SettingKind.Secret,
			"The second authenticator secret, used to accept \"confirm on your phone\" prompts. Only needed when this account gives items away, since receiving never needs confirming.",
			Advanced: true),
		new("AccountProxy", "Proxy for this account", SecAccount, SettingKind.Text,
			"Sends this account's Steam traffic through its own proxy, like http://host:port. Leave empty to use the global proxy or none. Spreading accounts across IPs helps avoid Steam's rate limits.",
			Advanced: true, NeedsRestart: true, Placeholder: "http://host:port"),
		new("AccountProxyUsername", "Proxy user name", SecAccount, SettingKind.Text,
			"User name for this account's proxy, if it needs one.",
			Advanced: true, NeedsRestart: true),
		new("AccountProxyPassword", "Proxy password", SecAccount, SettingKind.Secret,
			"Password for this account's proxy, if it needs one.",
			Advanced: true, NeedsRestart: true),
		new("ClearInventoryNotifications", "Clear the new-items badge", SecAccount, SettingKind.Bool,
			"Clears Steam's green new items counter each time a card drops.",
			Advanced: true),
		new("ClearNotifications", "Clear Steam's notifications", SecAccount, SettingKind.Bool,
			"Marks everything in Steam's notification tray as read, like comments, gifts and invites, so the counters don't keep climbing.",
			Advanced: true),
		// ── Human mode ──
		new("LegitMode", "Human mode", SecHuman, SettingKind.Bool,
			"Plays like a person: one game at a time, realistic sittings, breaks, meals, days off and offline at night. Bot-like settings are hidden while it's on and come back unchanged when you turn it off."),
		new("GameWeights", "Games and how often", SecHuman, SettingKind.Text,
			"Which games it plays and what percent of its time each gets, adding up to about 100. The first one is the main game. Leave a number off to share what's left, or write 440:0 to bench a game.",
			Placeholder: "730:70, 440:20, 550:10", Mode: "legit"),
		new("HourTargets", "Hour targets", SecHuman, SettingKind.Text,
			"Hours to reach in a game, with an optional date. 730:100@2026-12-01 means 100 hours of CS2 by 1 December. It plays that game more until the target is hit.",
			Advanced: true, Placeholder: "730:100@2026-12-01, 440:50", Mode: "legit"),
		// how the day is shaped
		new("WeekdayHours", "Hours on a weekday", SecHuman, SettingKind.Int,
			"Roughly how many hours it plays Monday to Friday. The real number changes every day around this one.",
			Min: 0, Max: 20, Mode: "legit"),
		new("WeekendHours", "Hours at the weekend", SecHuman, SettingKind.Int,
			"Roughly how many hours it plays on Saturday and Sunday. Longer weekends make the week look more real.",
			Min: 0, Max: 20, Mode: "legit"),
		new("DayOffChancePct", "Chance of a day off", SecHuman, SettingKind.Int,
			"The percent chance it doesn't play at all on a given day. Real people don't game every single day.",
			Min: 0, Max: 60, Mode: "legit", Advanced: true),
		new("DayStartHour", "Gets on around", SecHuman, SettingKind.Int,
			"The hour it usually comes online, on a 24-hour clock. The real time varies around it and weekends start earlier.",
			Min: 0, Max: 23, Mode: "legit"),
		new("BedHour", "Goes to bed around", SecHuman, SettingKind.Int,
			"The hour it stops for the night, on a 24-hour clock. Late hours are fine, 2 means 2am.",
			Min: 0, Max: 23, Mode: "legit"),
		new("LateNightExtraHours", "Stays up later Fri/Sat", SecHuman, SettingKind.Int,
			"Extra hours it stays up on Friday and Saturday nights.",
			Min: 0, Max: 6, Mode: "legit", Advanced: true),
		// how the games are split
		new("PureMainDayChancePct", "Days on the main game only", SecHuman, SettingKind.Int,
			"The percent chance a whole day goes on the main game only, so side games show up in bursts instead of a small slice daily. This lowers their weekly share, shown under the games box.",
			Min: 0, Max: 100, Mode: "legit", Advanced: true),
		// settling in
		new("WarmUpMinMinutes", "Settle in for at least", SecHuman, SettingKind.Int,
			"The shortest wait after signing in before it starts a game, in minutes. Also applies when you turn on \"Human mode\", so it doesn't launch a game instantly.",
			Min: 0, Max: 240, Mode: "legit", Advanced: true),
		new("WarmUpMaxMinutes", "And at most", SecHuman, SettingKind.Int,
			"The longest wait after signing in, in minutes. It never starts within 3 minutes of signing in, so it can't take over if you're already playing on this account.",
			Min: 0, Max: 480, Mode: "legit", Advanced: true),
		// sittings and breaks
		new("SessionMinMinutes", "Shortest sitting", SecHuman, SettingKind.Int,
			"The shortest time it stays in one game before switching or taking a break, in minutes.",
			Min: 5, Max: 600, Mode: "legit", Advanced: true),
		new("SessionMaxMinutes", "Longest sitting", SecHuman, SettingKind.Int,
			"The longest time it stays in one game, in minutes. Leave a wide gap between shortest and longest so sittings vary.",
			Min: 10, Max: 1440, Mode: "legit", Advanced: true),
		new("BreakMinMinutes", "Shortest break", SecHuman, SettingKind.Int,
			"The shortest break between sittings, in minutes. Most breaks are close to this.",
			Advanced: true, Min: 1, Max: 180, Mode: "legit"),
		new("BreakMaxMinutes", "Longest break", SecHuman, SettingKind.Int,
			"The longest normal break, in minutes.",
			Advanced: true, Min: 2, Max: 240, Mode: "legit"),
		new("BreakAwayAfterMinMinutes", "On a break, go Away after at least", SecHuman, SettingKind.Int,
			"Human mode only. How many minutes into a break its status switches to Away or offline, like a real idle Steam client. Shorter breaks stay online.",
			Advanced: true, Min: 0, Max: 60, Mode: "legit"),
		new("BreakAwayAfterMaxMinutes", "...up to", SecHuman, SettingKind.Int,
			"The most minutes into a break before its status switches to Away or offline.",
			Advanced: true, Min: 0, Max: 90, Mode: "legit"),
		new("MealBreaksPerDay", "Meals a day", SecHuman, SettingKind.Int,
			"How many longer meal breaks it takes a day. They land around real meal times. 0 for none.",
			Min: 0, Max: 6, Mode: "legit", Advanced: true),
		new("MealBreakMinutes", "How long a meal takes", SecHuman, SettingKind.Int,
			"Roughly how long a meal break lasts, in minutes. Sometimes it runs a bit over.",
			Min: 10, Max: 240, Mode: "legit", Advanced: true),
		new("SignOutOnBreakChancePct", "Chance it drops offline on a break", SecHuman, SettingKind.Int,
			"The percent chance a break shows as offline instead of Away. It stays connected underneath, so it costs nothing. Meals go offline twice as often.",
			Min: 0, Max: 100, Mode: "legit", Advanced: true),
		new("MaxSignOutsPerDay", "Drop offline at most", SecHuman, SettingKind.Int,
			"The most times a day it shows offline on a break, so it doesn't flicker on and off your friends list. 0 turns it off and every break shows Away.",
			Advanced: true, Min: 0, Max: 40, Mode: "legit"),
		// overnight
		new("OfflineIdleAtNight", "Bank hours overnight", SecHuman, SettingKind.Bool,
			"While it's asleep, it goes invisible and keeps idling, so hours still count while friends see it offline. If \"When to farm cards\" sends cards to the night, those farm first.", Mode: "legit"),
		new("OfflineIdleGames", "Games to idle overnight", SecHuman, SettingKind.AppIds,
			"Games to idle while it shows offline at night. Several can run at once. Only used when \"Bank hours overnight\" is on.", Advanced: true, Mode: "legit"),
		new("ActOnlyWhileAwake", "Only react while awake", SecHuman, SettingKind.Bool,
			"Human mode only. Holds anything others can see, like trades, gifts, invites, replies, comments and achievements, while the account is asleep. Turn it off to answer at night too.",
			Advanced: true, Mode: "legit"),
		new("QuietThingsWaitForDay", "Behind-the-scenes things wait for its day too", SecHuman, SettingKind.Bool,
			"Human mode only. Makes background jobs like crafting badges, claiming free games, booster packs and selling cards wait for the account's waking hours too. Off, they run at any hour.",
			Advanced: true, Mode: "legit"),
		new("WakeDelayMinMinutes", "After waking, wait at least", SecHuman, SettingKind.Int,
			"Human mode only. How long after waking up or signing in before it does things others can see, like trades, comments or replies, in minutes. Each kind gets its own random wait.",
			Advanced: true, Min: 0, Max: 600, Mode: "legit"),
		new("WakeDelayMaxMinutes", "...up to", SecHuman, SettingKind.Int,
			"The longest wait after waking or signing in, in minutes.",
			Advanced: true, Min: 0, Max: 900, Mode: "legit"),
		new("QuietDelayMinMinutes", "After signing in, behind-the-scenes things wait at least", SecHuman, SettingKind.Int,
			"Human mode only. How long after signing in before background jobs like badges, free games and booster packs start, in minutes. Each gets its own random wait so they don't all hit at once.",
			Advanced: true, Min: 0, Max: 600, Mode: "legit"),
		new("QuietDelayMaxMinutes", "...up to", SecHuman, SettingKind.Int,
			"The longest wait after signing in before background jobs start, in minutes.",
			Advanced: true, Min: 0, Max: 900, Mode: "legit"),
		new("LegitStopMaxSeconds", "When stopped, finish up for up to", SecHuman, SettingKind.Int,
			"When you stop the account, it keeps playing for a few random seconds up to this many before signing off, instead of vanishing mid-game. 0 stops instantly. Only used when \"Human mode\" is on.",
			Min: 0, Max: 300, Advanced: true, Mode: "legit"),
		// ── What it plays ──
		new("IdleGames", "Games to idle", SecPlaying, SettingKind.AppIds,
			"Games to idle for playtime when no cards are left to farm. Use the game ID (the number in its store link) or paste the whole link, separated by commas.",
			Placeholder: "730, 440", Mode: "rage"),
		new("CustomGameNameEnabled", "Show a custom game name", SecPlaying, SettingKind.Bool,
			"Shows your friends a custom name instead of the real game. Turning it off keeps the name you typed below for next time.",
			Mode: "rage"),
		new("CustomGameName", "Show as", SecPlaying, SettingKind.Text,
			"The name your profile and friends list show instead of the real game. The real games still collect playtime underneath.",
			Placeholder: "nocat.lol", Mode: "rage"),
		new("PlayWhileFarming", "Keep the name while farming", SecPlaying, SettingKind.Bool,
			"Keeps the custom name showing while cards are being farmed. Turn it off to show the real game during card farming.",
			Advanced: true, Mode: "rage"),
		new("GameDevice", "Play as if on", SecPlaying, SettingKind.Choice,
			"Which device badge friends see next to your name while it plays. Steam Deck doesn't stick, Steam shows the controller badge instead.",
			Advanced: true, Choices: "0 a PC | 512 a phone | 1024 Big Picture | 2048 VR | 4096 a controller | 12288 a Steam Deck"),
		// ── Trading cards ──
		new("FarmCards", "Farm trading cards", SecCards, SettingKind.Bool,
			"Farms Steam trading cards. Games with cards left get played until they stop dropping, before anything else."),
		new("FarmCardsWhen", "When to farm cards", SecCards, SettingKind.Choice,
			"Human mode only - this is AUTOMATIC farming: nocat.farm picks the card games itself. Day: cards farm in its normal sittings. Night: only while it's asleep. Any time: nonstop until done. Mixed: some sittings farm cards, the rest play its usual games, so cards trickle in over days. Want one game's cards first? Type drops <account> <appID> <count> (e.g. drops myaccount 460920 2 = 2 cards from Steep first) - on a human-mode account that still plays in normal sittings, with breaks and bedtime.",
			Choices: "0 day, in its sittings | 1 night, while it's asleep | 2 any time | 3 mixed, some sittings cards, the rest its games"),
		new("CardSittingsPct", "Share of sittings that farm cards (mixed)", SecCards, SettingKind.Int,
			"Roughly what percent of sittings farm cards when \"When to farm cards\" is set to mixed. Higher finishes cards sooner, lower keeps more time on its usual games. A drops run (the drops command) gets at least the main game's share, so the game you picked comes sooner.",
			Min: 5, Max: 95, Mode: "legit"),
		new("HoursUntilCardDrops", "Hours before cards drop", SecCards, SettingKind.Float,
			"How many hours a game needs before Steam drops its cards. Set 0 if this account has spent over $5 on Steam, so nothing plays longer than it needs to.",
			Min: 0, Max: 100, Advanced: true),
		new("FarmingOrder", "Farm in this order", SecCards, SettingKind.Choice,
			"Which game to farm first. \"Fewest cards left\" finishes badges soonest. \"Most played first\" adds the least new playtime to your library.",
			Advanced: true, Choices: "0 most played first | 1 least played first | 2 fewest cards left | 3 most cards left | 4 random | 5 alphabetical"),
		new("PriorityGames", "Farm these first", SecCards, SettingKind.AppIds,
			"Game IDs to farm before anything else, whatever the order says.",
			Advanced: true),
		new("FarmPriorityOnly", "Only farm those", SecCards, SettingKind.Bool,
			"Only farms the games in \"Farm these first\" and ignores every other game with cards left.",
			Advanced: true),
		new("BlacklistedGames", "Never touch these", SecCards, SettingKind.AppIds,
			"Game IDs that are never farmed or idled.",
			Advanced: true),
		new("SkipUnplayedGames", "Skip games you've never played", SecCards, SettingKind.Bool,
			"Skips games you've never launched yourself, so none of them get their first playtime from here.",
			Advanced: true),
		new("SkipRefundableGames", "Protect refundable games", SecCards, SettingKind.Bool,
			"Leaves games you just bought alone so you can still refund them. Steam refuses refunds after 2 hours played, so nothing idles, farms or grinds them until the refund window is over.",
			Advanced: true),
		new("ProtectGiftedGames", "...gifted games too", SecCards, SettingKind.Bool,
			"Also leaves gifted games alone while they can still be refunded to the friend who gave them. Only used when \"Protect refundable games\" is on.",
			Advanced: true),
		new("RefundHoldDays", "...for this many days", SecCards, SettingKind.Int,
			"How many days a newly bought game is left alone. Steam's refund window is 14 days, so raise this only if you want extra time to decide.",
			Advanced: true, Min: 1, Max: 90),
		new("FarmInSittings", "Farm in sittings, not flat out", SecCards, SettingKind.Bool,
			"Farms cards in a few blocks a day with breaks between them, instead of nonstop. Only makes a real difference on an account with nothing else to idle.",
			Advanced: true, Mode: "rage"),
		new("FarmHoursPerDay", "Hours a day to farm", SecCards, SettingKind.Int,
			"Roughly how many hours a day the farming blocks add up to. It varies each day and runs longer at weekends. Only used when \"Farm in sittings, not flat out\" is on.",
			Advanced: true, Min: 1, Max: 20, Mode: "rage"),
		new("FarmFromHour", "Farm cards only from", SecCards, SettingKind.Int,
			"Farms cards only from this hour, on a 24-hour clock. Leave this and \"...until\" both at 0 to farm any time.",
			Advanced: true, Min: 0, Max: 23),
		new("FarmUntilHour", "...until", SecCards, SettingKind.Int,
			"Farms cards up to this hour, on a 24-hour clock. Set it earlier than the start hour to run past midnight, so 22 to 6 farms overnight.",
			Advanced: true, Min: 0, Max: 24),
		new("PostFarmWindDownMinMinutes", "After the last card, keep playing at least", SecCards, SettingKind.Int,
			"Human mode only. After a game drops its last card, it keeps playing it for a few minutes, then takes a break. Set both to 0 to stop right away.",
			Advanced: true, Min: 0, Max: 120, Mode: "legit"),
		new("PostFarmWindDownMaxMinutes", "...up to", SecCards, SettingKind.Int,
			"The most minutes it keeps playing after the last card.",
			Advanced: true, Min: 0, Max: 240, Mode: "legit"),
		new("FarmingDelayMinutes", "Re-check every", SecCards, SettingKind.Int,
			"How often it re-checks a game while farming, in minutes. Steam reports drops the moment they happen, so this is just a backup.",
			Advanced: true, Min: 1, Max: 240),
		new("MaxFarmingHoursPerGame", "Give up after", SecCards, SettingKind.Int,
			"Gives up on a game after this many hours and moves on, so one stuck game can't block the rest.",
			Advanced: true, Min: 1, Max: 200),
		new("FarmOffline", "Farm while appearing offline", SecCards, SettingKind.Bool,
			"Shows this account as offline while it farms. Hours still count and cards still drop, friends just don't see it.",
			Mode: "rage", Advanced: true),
		new("StopWhenFarmingDone", "Log out when finished", SecCards, SettingKind.Bool,
			"Signs this account out once no cards are left, instead of idling.",
			Advanced: true, Mode: "rage"),
		// ── Badges, boosters & selling ──
		new("CraftBadges", "Craft badges from card sets", SecBadges, SettingKind.Bool,
			"Turns finished card sets into badges once a day, which is what raises your Steam level. Cards just sitting in the inventory raise nothing."),
		new("UnpackBoosterPacks", "Open booster packs", SecBadges, SettingKind.Bool,
			"Opens booster packs that land in the inventory, so their cards count toward badges.",
			Advanced: true),
		new("BoosterGames", "Make booster packs for", SecBadges, SettingKind.Text,
			"Game IDs to turn gems into booster packs for, separated by commas. Steam allows one pack per game a day, only for games that still drop cards. Leave empty to turn it off.",
			Advanced: true, Placeholder: "730, 440"),
		new("BoosterGems", "Booster packs use", SecBadges, SettingKind.Choice,
			"Which gems booster packs use when the account has both kinds. Tradable first makes packs you can trade or sell. Untradable first uses up gems that can't go anywhere else.",
			Advanced: true, Choices: "0 tradable gems first | 1 untradable gems first"),
		new("SellDuplicates", "Sell duplicate cards", SecBadges, SettingKind.Bool,
			"Lists a few spare cards on the market every 8 to 14 hours, a cent under the cheapest listing. It keeps what you need for badges and never sells foils. Needs phone confirmation unless the authenticator is added."),
		new("SellPerRun", "Cards to list at a time", SecBadges, SettingKind.Int,
			"How many spare cards each round lists, most valuable first. A few at a time looks natural.",
			Advanced: true, Min: 1, Max: 25),
		// ── Achievements ──
		new("UnlockAchievements", "Earn achievements over time", SecAchievements, SettingKind.Bool,
			"Unlocks a few achievements a day in the games this account plays, easiest first. Unlocking a whole list at once shows on the profile forever, and this avoids that."),
		new("AchievementPace", "How fast", SecAchievements, SettingKind.Choice,
			"How long it waits between unlocks. Careful doubles every wait and brisk halves them. No pace unlocks anything before the hours played allow it.",
			Advanced: true, Choices: "0 careful (twice as slow) | 1 normal | 2 brisk (twice as fast)"),
		new("AchievementMaxCompletionPct", "Finish no more than", SecAchievements, SettingKind.Int,
			"The most of any one game it will ever complete, in percent. Leaving a few unearned looks like a real library. The grind command ignores this.",
			Advanced: true, Min: 1, Max: 100),
		new("AchievementIncludeMainGame", "Earn them in the main game too", SecAchievements, SettingKind.Bool,
			"Lets the main game from \"Human mode\" earn achievements like every other game. Best left on, since hundreds of hours with no achievements looks odd.",
			Advanced: true),
		new("AchievementGames", "Only these games", SecAchievements, SettingKind.AppIds,
			"Game IDs to limit achievements to. Leave it empty to use whatever the account is playing.",
			Advanced: true),
		new("AchievementNeverGames", "Never in these games", SecAchievements, SettingKind.AppIds,
			"Games to leave alone completely. No achievements are unlocked in them and boosts never pick them. In human mode the account's main game is skipped too.",
			Advanced: true),
		new("AchievementGrindGapMinMinutes", "While grinding, one achievement every", SecAchievements, SettingKind.Int,
			"How far apart achievements unlock during a grind, in minutes. A grind sits on one game, so this is a faster, active pace. Easiest ones go first.",
			Advanced: true, Min: 1, Max: 120),
		new("AchievementGrindGapMaxMinutes", "...up to", SecAchievements, SettingKind.Int,
			"The longest gap between grind unlocks, in minutes.",
			Advanced: true, Min: 1, Max: 240),
		new("AchievementBoost", "Achievement boost", SecAchievements, SettingKind.Choice,
			"Hunts achievements across several games on its own, one game at a time. \"Games you pick\" uses the list below. \"All single-player\" picks single-player games with achievements from your library, played or not (turn on \"Only hunt games you've played\" to skip the rest). On a human-mode account the game being hunted joins the games it plays, like a side game.", Advanced: true, Choices: "0 off | 1 games you pick | 2 all single-player"),
		new("AchievementBoostGames", "Boost these games", SecAchievements, SettingKind.AppIds,
			"The games the boost works through, as game IDs or store links separated by commas. Only used when \"Achievement boost\" is set to games you pick.",
			Advanced: true, Placeholder: "440, 400, 220"),
		new("BoostSessionHours", "Play each for about", SecAchievements, SettingKind.Int,
			"How long the boost plays one game before moving to the next, in hours. On a human-mode account that is playing time spread over its normal sittings.",
			Advanced: true, Min: 1, Max: 24),
		new("BoostWeight", "Human mode: hunt game's weight", SecAchievements, SettingKind.Int,
			"Human mode only. The game being hunted joins the games it plays, and this is how often it comes up - a weight like the ones in \"Games and how often\". It plays in normal sittings, with breaks and bedtime, and never cuts another game short.",
			Advanced: true, Min: 1, Max: 100, Mode: "legit"),
		new("BoostGamesInRotation", "Games in rotation", SecAchievements, SettingKind.Int,
			"How many games the hunt has on the go at once. It takes turns between them, and when one is resting or done, the next game on the list takes its place.",
			Advanced: true, Min: 1, Max: 20),
		new("BoostHoursPerDay", "Hunt at most, hours a day", SecAchievements, SettingKind.Int,
			"The most time a day it spends on the game it's hunting. On a human-mode account the game leaves its games for the rest of the day once that's played; otherwise the hunt stops for the day. 0 is no limit.",
			Advanced: true, Min: 0, Max: 24),
		new("BoostSwitchFromPct", "Move to another game at about", SecAchievements, SettingKind.Int,
			"Once a game has about this much of its achievements earned - a random point between this and the next setting, different for every game - it moves to another one, like a person who has had enough of a game for now. In percent.",
			Advanced: true, Min: 5, Max: 100),
		new("BoostSwitchToPct", "...up to", SecAchievements, SettingKind.Int,
			"The top of that range. The same number as the one above makes it exact.",
			Advanced: true, Min: 5, Max: 100),
		new("BoostRestDaysMin", "Come back to a game after", SecAchievements, SettingKind.Int,
			"How many days before it goes back to a game it moved on from - a random number between this and the next setting. Each time it comes back it stops a little further in, up to \"Finish no more than\".",
			Advanced: true, Min: 0, Max: 90),
		new("BoostRestDaysMax", "...to", SecAchievements, SettingKind.Int,
			"The top of that range, in days.",
			Advanced: true, Min: 0, Max: 180),
		new("AchievementRealLength", "Pace by how long games really take", SecAchievements, SettingKind.Bool,
			"Looks up how long each game takes a typical player (SteamSpy's public figures - only the game's ID is sent) and paces its achievements by it: the rare ones open up at the speed of that game, a chapter needs its share of the game's hours, and \"beat the game\" never comes before most of a playthrough.",
			Advanced: true),
		new("BoostMinReviews", "Only hunt games with at least", SecAchievements, SettingKind.Int,
			"How many Steam reviews a game needs before \"all single-player\" hunts it. Keeps it off cheap bundle games nobody plays. 0 hunts everything, and games you pick by hand are always allowed.",
			Advanced: true, Min: 0, Max: 100000),
		new("BoostOnlyPlayedGames", "Only hunt games you've played", SecAchievements, SettingKind.Bool,
			"Only lets \"all single-player\" hunt games this account has launched before. The strictest way to keep it to games that fit the account's history.",
			Advanced: true),
		new("IncludeFamilyLibrary", "Include family-shared games", SecAchievements, SettingKind.Bool,
			"Also hunts achievements in games shared with this account through Steam Family. They count on this account like owned games, and owned games always go first.",
			Advanced: true),
		new("HoldNewFamilyGames", "Leave brand-new family games alone", SecAchievements, SettingKind.Bool,
			"Skips a family-shared game while it's new to the library, in case whoever bought it wants a refund. Uses the same number of days as \"Protect refundable games\".",
			Advanced: true),
		new("YieldToFamily", "Give a shared game back when they want it", SecAchievements, SettingKind.Bool,
			"Hands a borrowed family game straight back when someone in the family starts it, and moves on. It stays out of the rotation for 20 minutes after they stop.",
			Advanced: true),
		// ── Free stuff ──
		new("ClaimFreeGames", "Claim free games", SecExtras, SettingKind.Bool,
			"Watches for paid games given away free to keep and adds them to this account. Free-to-play junk is skipped."),
		new("ClaimFreeDlc", "...free DLC too", SecExtras, SettingKind.Bool,
			"Also grabs paid DLC that is marked down to free, like a supporter pack. Steam only gives DLC to accounts that own the game it belongs to, so the rest are skipped. Only used when \"Claim free games\" is on."),
		new("ClaimFreeDlcBase", "...and its game, if that's free too", SecExtras, SettingKind.Bool,
			"When a free DLC needs a game this account doesn't have, and that game is free right now too, it claims the game first and then the DLC. Only used when \"...free DLC too\" is on."),
		new("ClaimEventItems", "Claim free event items", SecExtras, SettingKind.Bool,
			"Picks up the free daily sticker during Steam sales and anything that costs 0 points in the Points Shop."),
		new("DiscoveryQueue", "Go through the discovery queue", SecExtras, SettingKind.Choice,
			"Clicks through the store's discovery queue once a day like a person would. During sales this earns event items and badge progress.",
			Choices: "0 off | 1 during sales | 2 every day"),
		// ── Inventory & bans ──
		new("ShowInventoryValue", "Work out what its inventory is worth", SecInventory, SettingKind.Bool,
			"Works out what this account's inventory is worth at Steam market prices and shows it on the dashboard. Turn it off on accounts that only hold a few cards.",
			Advanced: true),
		new("InventoryIgnoreGames", "...but not these games", SecInventory, SettingKind.AppIds,
			"Game IDs (the number in the store link) to leave out of the inventory value and out of every trade, separated by commas. Games the account is banned in can never trade - the ban watch adds those by itself when it can see them, and you can add them here too.",
			Advanced: true, Placeholder: "730"),
		new("WatchBans", "Watch for bans", SecInventory, SettingKind.Bool,
			"Looks at this account's bans every few hours - VAC, game bans, a trade ban, a community ban - and tells you straight away (log, pop-up, Discord/Telegram) when a new one appears. It only looks; it never changes anything. Games it finds the account banned in are left out of trades by themselves; trading cards keep trading."),
		// ── Trades ──
		new("AcceptDonations", "Accept donations", SecTrading, SettingKind.Bool,
			"Accepts trade offers where this account gives nothing away. These can't scam you, since an offer asking for even one of your items is never accepted here."),
		new("DonationsWhileAsleep", "Accept donations while asleep", SecTrading, SettingKind.Bool,
			"Human mode only. Lets donations go through at night even when \"Only react while awake\" is on. Everything else still waits for morning.",
			Advanced: true, Mode: "legit"),
		new("AcceptGifts", "Accept gifts and guest passes", SecTrading, SettingKind.Bool,
			"Accepts Steam wallet gift cards and guest passes sent to this account. Neither can cost anything. Each gift waits first, see \"Accept a gift after\"."),
		new("AcceptGiftedGames", "Accept gifted games", SecTrading, SettingKind.Bool,
			"Adds games that friends gift this account straight to its library. Turn it off to decide each gift yourself. Each gift waits first, see \"Accept a gift after\"."),
		new("GiftDelayMinMinutes", "Accept a gift after at least", SecTrading, SettingKind.Int,
			"The shortest wait before accepting a gift, like a game, gift card or guest pass, in minutes.",
			Min: 0, Max: 720, Advanced: true),
		new("GiftDelayMaxMinutes", "And at most", SecTrading, SettingKind.Int,
			"The longest wait before accepting a gift, in minutes. Each gift gets a random wait, usually closer to the short end.",
			Min: 0, Max: 1440, Advanced: true),
		new("AcceptFairCardSwaps", "Accept fair card swaps", SecTrading, SettingKind.Bool,
			"Accepts one for one card swaps from anyone within the same game, but only when they bring your sets closer to done. Offers with foils or other items are left alone. Needs the mobile authenticator."),
		new("AcceptFromMasters", "Accept anything from your own accounts", SecTrading, SettingKind.Bool,
			"Accepts any offer from the accounts in \"Your own accounts\", even ones that take items. Handy for moving cards to one account, but only list accounts you own."),
		new("TradeMasters", "Your own accounts", SecTrading, SettingKind.Text,
			"SteamID64s of your own accounts, separated by commas. Anyone listed here can take items from this account, so only add accounts you own.",
			Placeholder: "76561198000000000"),
		new("TradeMasterToken", "Their trade link token", SecTrading, SettingKind.Text,
			"The token from the other account's trade link, the part after &token=. Only needed if the two accounts aren't friends.",
			Advanced: true, Placeholder: "aBcD1234"),
		new("AutoTradeWith", "Trade by itself with", SecTrading, SettingKind.Text,
			"Who this account trades with without asking you, and which way. Account names or SteamID64s, separated by commas, each with :from (accept what they send), :to (let them take items) or :both - both is the default. Example: old, kylro:to. Empty = the accounts in \"Your own accounts\", both ways, when \"Accept anything from your own accounts\" is on. Donations follow their own switch. Anything else waits for you: trade accept or trade decline, in the console or on Telegram.",
			Placeholder: "old, kylro:to"),
		new("DeclineOtherTrades", "Decline everything else", SecTrading, SettingKind.Bool,
			"Declines every other trade offer instead of leaving it. Off leaves unaccepted offers for you to check yourself.",
			Advanced: true),
		new("OneTradeAtATime", "Answer one trade offer at a time", SecTrading, SettingKind.Bool,
			"Human mode only. When several trade offers are due at once, like in the morning, it answers them a few minutes apart instead of all together.",
			Advanced: true, Mode: "legit"),
		new("TradeDelayMinMinutes", "Wait at least", SecTrading, SettingKind.Int,
			"The shortest wait before answering a trade offer, in minutes. Accepting seconds after it arrives looks like a bot.",
			Min: 0, Max: 720, Advanced: true),
		new("TradeDelayMaxMinutes", "And at most", SecTrading, SettingKind.Int,
			"The longest wait before answering a trade offer, in minutes. Each offer gets a random wait, usually closer to the short end.",
			Min: 0, Max: 1440, Advanced: true),
		new("SendItemTypes", "What to send", SecTrading, SettingKind.Text,
			"Which items can leave this account, separated by commas: cards, foils, backgrounds, emoticons, boosters, gems - or all, for everything tradable in every game (TF2 and CS2 items too). Anything not listed is never sent.",
			Placeholder: "cards"),
		new("SendOnFarmingFinished", "Send items when farming finishes", SecTrading, SettingKind.Bool,
			"When there are no cards left to farm, sends what this account collected to the first account in \"Your own accounts\".",
			Advanced: true),
		new("SendEveryHours", "Send items every", SecTrading, SettingKind.Int,
			"Sends this account's items to the first account in \"Your own accounts\" every this many hours, so cards don't pile up. The first send is one period after you turn it on, the account's card on the dashboard shows when the next one is, and each send is in the log. 0 turns it off.",
			Advanced: true, Min: 0, Max: 168),
		new("SendAroundHour", "...around", SecTrading, SettingKind.Int,
			"The hour of the day to send at, 0 to 23 - like 4 for about 4 in the morning. It goes out at a random minute in that hour, on the days \"Send items every\" allows. -1 = no set time: every so many hours from when it was turned on.",
			Advanced: true, Min: -1, Max: 23),
		// ── rep4rep commenting ──
		new("Rep4Rep", "Post rep4rep comments", SecComments, SettingKind.Bool,
			"Lets this account post the profile comments rep4rep hands out. All accounts earn into the same rep4rep points."),
		new("Rep4RepDailyCap", "Most per 24 hours", SecComments, SettingKind.Int,
			"The most comments this account posts in any 24 hours. Steam allows about 10 on non-friends, and going over can get the account comment banned.",
			Advanced: true, Min: 1, Max: 25),
		new("Rep4RepGapMinMinutes", "Shortest gap", SecComments, SettingKind.Int,
			"The shortest time between two comments from this account, in minutes.",
			Min: 1, Max: 720, Advanced: true),
		new("Rep4RepGapMaxMinutes", "Longest gap", SecComments, SettingKind.Int,
			"The longest time between comments, in minutes. Each gap is picked at random between the two.",
			Min: 1, Max: 1440, Advanced: true),
		new("Rep4RepStartHour", "Only post from", SecComments, SettingKind.Int,
			"The earliest hour of the day this account comments. Comments at 4am are an easy bot giveaway.",
			Min: 0, Max: 23, Advanced: true),
		new("Rep4RepEndHour", "Until", SecComments, SettingKind.Int,
			"The latest hour of the day this account comments. Set it the same as \"Only post from\" to comment around the clock.",
			Min: 1, Max: 24, Advanced: true),
		new("Rep4RepLearnCap", "Learn the real limit", SecComments, SettingKind.Bool,
			"When Steam says the account has hit its daily comment limit before the number set above, that becomes its limit (never below 5). A \"commenting too frequently\" refusal is about the gap between comments and never changes it.",
			Advanced: true),
		new("Rep4RepRetryRefused", "Retry a refused comment", SecComments, SettingKind.Bool,
			"Gives a refused comment one more try before skipping that profile for a day.",
			Advanced: true),
		// ── Friends & messages ──
		new("AcceptFriendRequests", "Accept friend requests", SecSocial, SettingKind.Bool,
			"Accepts incoming friend requests automatically. Handy on a rep4rep account, since Steam lets you comment much more freely on friends."),
		new("FriendRequestDelayMinMinutes", "Wait at least", SecSocial, SettingKind.Int,
			"The shortest wait before accepting a friend request, in minutes. Accepting the second it arrives is an easy bot giveaway.",
			Min: 0, Max: 720, Advanced: true),
		new("FriendRequestDelayMaxMinutes", "And at most", SecSocial, SettingKind.Int,
			"The longest wait before accepting a friend request, in minutes. Each request gets a random wait, usually closer to the short end.",
			Min: 0, Max: 1440, Advanced: true),
		new("AcceptGroupInvites", "Accept group invites", SecSocial, SettingKind.Bool,
			"Joins Steam groups this account gets invited to.",
			Advanced: true),
		new("JoinGroup", "Join the shared groups", SecSocial, SettingKind.Bool,
			"Joins the groups in \"Groups every account joins\". Turn it off to keep just this account out of them.",
			Advanced: true),
		new("ExtraGroupsToJoin", "Also join these groups", SecSocial, SettingKind.Text,
			"Steam groups only this account joins, as links or short names separated by commas. Groups in \"Groups every account joins\" are joined too.",
			Advanced: true, Placeholder: "steamcommunity.com/groups/yourgroup"),
		new("IgnoreSuspiciousInvites", "Ignore obvious spam", SecSocial, SettingKind.Bool,
			"Quietly ignores friend requests from new level 0 private profiles, which is what scam bots look like. Only works when \"Accept friend requests\" is on.",
			Advanced: true),
		new("RejectInvalidFriendInvites", "Turn down what you don't accept", SecSocial, SettingKind.Bool,
			"Declines friend requests it doesn't accept instead of leaving them in the list. Off leaves them there for you to look at.",
			Advanced: true),
		new("AutoReplyEnabled", "Reply to messages", SecSocial, SettingKind.Bool,
			"Replies to people who message this account. Turning it off keeps your reply text saved for later."),
		new("AutoReply", "Reply to messages with", SecSocial, SettingKind.Text,
			"The message sent back when someone messages this account. Leave empty to send nothing. A short afk line looks more normal than never answering.",
			Placeholder: "afk right now, I will get back to you"),
		new("AutoReplyOncePerDay", "Only reply once a day", SecSocial, SettingKind.Bool,
			"Sends the reply at most once a day to each person, so nobody gets the same line twice.", Advanced: true),
		new("AutoReplyDelaySeconds", "Wait before replying", SecSocial, SettingKind.Int,
			"Seconds to wait before replying, so it doesn't answer instantly. The real wait is random, up to double this.",
			Advanced: true, Min: 0, Max: 600),
		new("CommandMasters", "Accept commands from", SecSocial, SettingKind.Text,
			"SteamID64s allowed to control nocat.farm by messaging this account, separated by commas. Commands start with a slash, so send /help to begin. Leave empty so nobody can.",
			Advanced: true, Placeholder: "76561198000000000"),
		// ── Staying out of the way ──
		new("PauseWhenYouPlay", "Stand down when you play", SecCourtesy, SettingKind.Bool,
			"Stops idling the moment you launch a game on this account yourself, so it never fights your own Steam client.", Advanced: true),
		new("ResumeDelayMinutes", "Wait before resuming", SecCourtesy, SettingKind.Int,
			"How long after you stop playing before this account picks up where it left off, in minutes.",
			Min: 0, Max: 240, Advanced: true),
		// ── Logging ──
		new("StatusEveryMinutes", "Report in every", SecLogging, SettingKind.Int,
			"How often this account says what it is doing while it has a game open. 0 follows the global setting under Logging; -1 keeps this account quiet. A boosting account you never look at can sit on an hour while the one you actually care about reports every few minutes.",
			Min: -1, Max: 1440, Advanced: true),
		new("StatusQuietEveryMinutes", "And while resting, every", SecLogging, SettingKind.Int,
			"The same, for when this account is asleep, on a break, paused or signed out. 0 follows the global setting; -1 keeps it quiet.",
			Min: -1, Max: 1440, Advanced: true),
		new("LogColour", "Colour in the log", SecLogging, SettingKind.Choice,
			"The colour of this account's name in the log and on the board, so accounts are easier to tell apart. Automatic colours by state: grey when signed out, amber when paused.",
			Choices: NocatFarm.Core.NameColour.ChoicesSpec, Advanced: true),
	];
}
