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
/// The tooltips are compiled into the binary on purpose. ASF-ui scrapes its help text off GitHub's rendered
/// wiki HTML and has broken twice doing it; these work offline and can't rot.
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

	// ── per-account sections, in display order ──────────────────────────────
	public const string SecAccount = "Account";
	public const string SecPlaying = "What it plays";
	public const string SecCards = "Trading cards";
	public const string SecExtras = "Free games & badges";
	public const string SecAchievements = "Achievements";
	public const string SecComments = "rep4rep commenting";
	public const string SecHuman = "Human mode";
	public const string SecSocial = "Friends & messages";
	public const string SecTrading = "Trades";
	public const string SecCourtesy = "Staying out of the way";

	public static readonly IReadOnlyList<SettingDef> Global = BuildGlobal();
	public static readonly IReadOnlyList<SettingDef> Bot = BuildBot();

	public static readonly GlobalConfig GlobalDefaults = new();
	public static readonly BotConfig BotDefaults = new();

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
				if (def.Name is "IdleGames" or "OfflineIdleGames" && (apps.Count > Core.SteamIds.MaxGamesPlayedConcurrently)) {
					return $"Steam only lets an account play {Core.SteamIds.MaxGamesPlayedConcurrently} games at once";
				}

				p.SetValue(config, apps);

				return null;
			}

			default:
				p.SetValue(config, raw);

				return null;
		}
	}

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

	public static bool IsTrue(string v) => v is "1" or "on" or "yes" or "y" || v.Equals("true", StringComparison.OrdinalIgnoreCase);

	// ═════════════════════════════════════════════════════════════════════════
	//  GLOBAL
	// ═════════════════════════════════════════════════════════════════════════
	private static List<SettingDef> BuildGlobal() => [
		// ── Dashboard ──
		new("WebEnabled", "Web dashboard", SecDashboard, SettingKind.Bool,
			"Serve the web dashboard. Turn it off and nocat.farm is console-only - everything still works, you just drive it by typing.",
			NeedsRestart: true),
		new("WebHost", "Listen on", SecDashboard, SettingKind.Text,
			"Which addresses the dashboard answers on. 127.0.0.1 means this PC only; use 0.0.0.0 to reach it from your phone or another machine, and set a password first.",
			NeedsRestart: true, Placeholder: "127.0.0.1"),
		new("WebPort", "Port", SecDashboard, SettingKind.Int,
			"Port for the dashboard. Change it if something else already uses 7242.",
			NeedsRestart: true, Min: 1, Max: 65535),
		new("WebPassword", "Dashboard password", SecDashboard, SettingKind.Secret,
			"Password for the dashboard. While it is empty the dashboard refuses every connection that isn't from this PC, so you can't accidentally publish your Steam accounts.",
			NeedsRestart: true),
		new("OpenDashboardAfterAdd", "Open the dashboard after adding an account", SecDashboard, SettingKind.Bool,
			"Open the dashboard in your browser once a new account has been added, so you can set it up. A brand new account does nothing at all until it is told what to play, and the app window has no form for that - only the dashboard does. Turning this off means you are on your own to remember."),
		new("OpenBrowserOnStart", "Open the browser on start", SecDashboard, SettingKind.Bool,
			"Open the dashboard in your browser automatically when nocat.farm starts."),
		new("WebRefreshSeconds", "Refresh every", SecDashboard, SettingKind.Int,
			"How often the dashboard refreshes itself, in seconds. Lower feels snappier and costs nothing - it's all local.",
			Advanced: true, Min: 1, Max: 60),
		new("WebSessionDays", "Stay signed in for", SecDashboard, SettingKind.Int,
			"How many days a browser stays signed in before it has to enter the dashboard password again.",
			Advanced: true, Min: 1, Max: 90),

		// ── Running in the background ──
		new("Tray", "Tray icon", SecBackground, SettingKind.Bool,
			"Put an icon in the notification area (bottom-right, by the clock) so nocat.farm can run in the background. Right-click it for the menu.",
			NeedsRestart: true),
		new("MinimizeToTray", "Minimise to the tray", SecBackground, SettingKind.Bool,
			"Minimising the console window hides it to the tray instead of leaving it in the taskbar."),
		new("StartMinimized", "Start hidden", SecBackground, SettingKind.Bool,
			"Start with the console window hidden, straight to the tray. Double-click the tray icon to bring it back."),
		new("StartWithWindows", "Start with Windows", SecBackground, SettingKind.Bool,
			"Launch nocat.farm when you sign in to Windows. It adds one entry under your own user account and touches nothing else."),
		new("KeepAwake", "Keep this PC awake", SecBackground, SettingKind.Bool,
			"Stop Windows going to sleep while accounts are running. The screen can still switch off - only sleep is blocked."),
		new("TrayNotifications", "Show pop-ups", SecBackground, SettingKind.Bool,
			"Master switch for the balloon pop-ups. The three below decide which ones you actually get."),
		new("NotifyEarnings", "Pop up when you earn", SecBackground, SettingKind.Bool,
			"Pop up when a trading card drops or a rep4rep comment is credited."),
		new("NotifySocial", "Pop up for comments", SecBackground, SettingKind.Bool,
			"Pop up when somebody comments on one of your profiles."),
		new("NotifyProblems", "Pop up for problems", SecBackground, SettingKind.Bool,
			"Pop up when an account genuinely needs you: a Steam Guard code, a failed login, or a comment ban."),

		// ── rep4rep account ──
		new("Rep4RepEnabled", "Use rep4rep at all", SecRep4RepAccount, SettingKind.Bool,
			"rep4rep is a third-party site that trades profile comments between its users. It's entirely optional. Turn this off and nocat.farm stops everything rep4rep-related on every account, and the whole feature disappears - its tab, its points, its per-account options and these settings all go away until you turn it back on."),
		new("Rep4RepApiToken", "API token", SecRep4RepAccount, SettingKind.Secret,
			"Your rep4rep API token. One token covers your whole rep4rep account however many Steam accounts you run - get it from rep4rep.com under Settings. No account yet? rep4rep.com/?r=reap"),
		new("Rep4RepAutoAddProfiles", "Register accounts automatically", SecRep4RepAccount, SettingKind.Bool,
			"Register each Steam account with rep4rep the first time it logs in, so you don't have to add them by hand on the website."),
		new("Rep4RepPointsRefreshMinutes", "Check points every", SecRep4RepAccount, SettingKind.Int,
			"How often to ask rep4rep for your points total, in minutes. rep4rep publishes no rate limit, so don't set this to 1 and leave it running for a month.",
			Advanced: true, Min: 1, Max: 1440),

		// ── Steam connection ──
		new("LoginStaggerSeconds", "Gap between logins", SecConnection, SettingKind.Int,
			"Seconds between logging in one account and the next at startup. Steam rate-limits by IP address, so a gap here is what stops several accounts tripping it.",
			Min: 0, Max: 600),
		new("ReconnectDelaySeconds", "Reconnect after", SecConnection, SettingKind.Int,
			"How long to wait before reconnecting after Steam drops the connection, in seconds.",
			Advanced: true, Min: 1, Max: 600),
		new("ConnectionTimeoutSeconds", "Connection timeout", SecConnection, SettingKind.Int,
			"How long to wait for Steam to answer before giving up and reconnecting, in seconds.",
			Advanced: true, NeedsRestart: true, Min: 5, Max: 255),
		new("MaxConcurrentFarming", "Farm at most", SecConnection, SettingKind.Int,
			"How many accounts may farm trading cards at the same time. 0 means no limit.",
			Advanced: true, Min: 0, Max: 100),
		new("LoginCooldownMinutes", "Rate-limit cooldown", SecConnection, SettingKind.Int,
			"When Steam rate-limits a login, every account waits this many minutes - they sit it out together rather than queueing up to be blocked in turn.",
			Advanced: true, Min: 1, Max: 240),
		new("WebRequestGapMs", "Gap between web requests", SecConnection, SettingKind.Int,
			"Smallest gap between two requests to the same Steam website, in milliseconds. This is the biggest thing keeping badge reading under Steam's radar - raise it if you see rate-limit warnings, and never go below 200.",
			Advanced: true, Min: 0, Max: 5000),
		new("SteamProtocol", "Connect using", SecConnection, SettingKind.Choice,
			"How to reach Steam. Leave it automatic unless a firewall or your ISP blocks something - websocket-only looks like ordinary HTTPS traffic and usually gets through.",
			Advanced: true, NeedsRestart: true, Choices: "0 automatic | 1 websocket only | 2 tcp only"),
		new("WebProxy", "Proxy", SecConnection, SettingKind.Text,
			"Send Steam traffic through this proxy, for example http://127.0.0.1:8080. Leave it empty to connect directly.",
			Advanced: true, NeedsRestart: true, Placeholder: "http://host:port"),
		new("WebProxyUsername", "Proxy user name", SecConnection, SettingKind.Text,
			"User name for the proxy above, if it asks for one.",
			Advanced: true, NeedsRestart: true),
		new("WebProxyPassword", "Proxy password", SecConnection, SettingKind.Secret,
			"Password for the proxy above, if it asks for one.",
			Advanced: true, NeedsRestart: true),

		new("GlobalBlacklistedGames", "Never touch these (all accounts)", SecConnection, SettingKind.AppIds,
			"AppIDs no account will ever farm or idle, so you don't have to repeat the same list on every account you add. Each account can still add its own on top.",
			Advanced: true),
		new("ExitWhenAllFinished", "Close when everything's done", SecBackground, SettingKind.Bool,
			"Close nocat.farm completely once every account has finished farming and logged out, instead of leaving it sitting in the tray doing nothing.",
			Advanced: true),

		// ── Logging ──
		new("StatusEveryMinutes", "Say what it's doing every", SecLogging, SettingKind.Int,
			"How often each account reports in while it has a game open - \"still playing Counter-Strike 2 · 12m of 41m\". Without this a quiet log is ambiguous: you cannot tell an account that farmed all night from one that silently stopped at 3am. 0 turns it off.",
			Min: 0, Max: 240),
		new("StatusQuietEveryMinutes", "And while it's resting, every", SecLogging, SettingKind.Int,
			"The same report while an account is asleep, on a break, paused or offline. Slower on purpose - a line every five minutes across an eight-hour night is noise, not information. 0 turns it off.",
			Min: 0, Max: 1440),
		new("Language", "Language", SecDashboard, SettingKind.Pick,
			"What language the dashboard is in. Anything a translation hasn't covered yet falls back to English rather than showing a blank, so a partly translated language is still perfectly usable. The console and the log stay in English.",
			Choices: "en English | es Español | pt-BR Português (Brasil) | ru Русский | de Deutsch | fr Français | zh-CN 简体中文 | tr Türkçe | pl Polski | ja 日本語 | ko 한국어"),
		new("CheckForUpdates", "Notify if an update is available", SecDashboard, SettingKind.Bool,
			"Look once a day for a newer release and mention it in the log. It only ever tells you - it never downloads, replaces or restarts anything. Something holding the keys to your Steam accounts should not be able to swap its own binary out on a schedule, and an update landing mid-farm is how a session gets lost.",
			Advanced: true),
		new("MarketCurrency", "Inventory prices in", SecDashboard, SettingKind.Choice,
			"Which currency inventory values are shown in. Use the same one your Steam store is set to, or the totals will not match what you see on the market. Changing it re-prices everything from scratch.",
			Choices: "1 US dollar | 20 Canadian dollar | 21 Australian dollar | 2 British pound | 3 Euro | 5 Russian rouble | 7 Brazilian real | 8 Japanese yen | 23 Chinese yuan | 24 Indian rupee"),
		new("FileLogging", "Write a log file", SecLogging, SettingKind.Bool,
			"Write everything to logs/ as well as the screen. Leave this on - it's the only way to see what happened while you were asleep."),
		new("Debug", "Debug detail", SecLogging, SettingKind.Bool,
			"Show a lot more detail, including every web request. Useful when something isn't working; noisy otherwise.",
			Advanced: true),
		new("LogRetentionDays", "Keep logs for", SecLogging, SettingKind.Int,
			"Delete log files older than this many days. 0 keeps them forever.",
			Advanced: true, Min: 0, Max: 3650),
		new("DailyReportEnabled", "Daily summary in the log", SecLogging, SettingKind.Bool,
			"Once a day, write a one-look summary of what every account banked in the last 24 hours - hours played, cards, rep4rep comments and a running total. Answers \"what did I get overnight?\" without scrolling; type `report` to see it any time."),
		new("DailyReportHour", "Summary time · hour", SecLogging, SettingKind.Int,
			"Hour of day the daily summary is written, 0-23 (local time). Default 9.",
			Min: 0, Max: 23),
		new("DailyReportMinute", "Summary time · minute", SecLogging, SettingKind.Int,
			"Minute past the hour for the daily summary, 0-59. Hour 9 + minute 30 = 09:30.",
			Min: 0, Max: 59)
	];

	// ═════════════════════════════════════════════════════════════════════════
	//  PER ACCOUNT
	// ═════════════════════════════════════════════════════════════════════════
	private static List<SettingDef> BuildBot() => [
		// ── Account ──
		new("Enabled", "Enabled", SecAccount, SettingKind.Bool,
			"Log this account in. Turn it off to keep the account configured but leave it completely alone."),
		new("SteamLogin", "Steam account name", SecAccount, SettingKind.Text,
			"The Steam account name you type when signing in - not your display name and not your email."),
		new("SteamPassword", "Password", SecAccount, SettingKind.Secret,
			"Optional. Leave it empty and nocat.farm asks once, then remembers the account with a login token instead - which is safer than a password sitting in a file."),
		new("OnlineStatus", "Appear as", SecAccount, SettingKind.Choice,
			"How this account looks to your friends while nocat.farm runs. Invisible still plays and still farms - nobody just sees it happening. While human mode is on, this is what it shows WHILE PLAYING; human mode takes the rest over by itself - Away on a quick break, Snooze over a meal, offline overnight - so leaving this on Online is the right answer there.",
			Choices: "0 offline | 1 online | 2 busy | 3 away | 4 snooze | 5 looking to trade | 6 looking to play | 7 invisible"),
		new("IUseThisAccount", "I sign into this one myself", SecAccount, SettingKind.Bool,
			"Turn this on for an account you also use from your own Steam client. nocat.farm will then never change its online status - because Steam handles two sessions setting a persona by signing the other one out of Friends and Chat, which on your own account means kicking you off your friends list. Everything else still works: it plays, farms, comments and keeps its schedule. It just leaves your online status alone."),
		new("UIMode", "Sign in as", SecAccount, SettingKind.Choice,
			"What kind of Steam client this tells Steam it is. Desktop is the right answer and the default - it is what the real Steam application says, so Steam has nothing to work out and leaves your own session alone. The others exist because Steam offers them; they are not better hidden and there is no reason to pick one unless you are debugging something.",
			Choices: "7 desktop (default) | 3 web | 2 mobile | 1 big picture | 0 desktop, legacy",
			Advanced: true),
		new("StartPaused", "Start paused", SecAccount, SettingKind.Bool,
			"Log in but don't play, farm or comment until you press Resume. For an account you want online and doing nothing."),
		new("StatusEveryMinutes", "Report in every", SecAccount, SettingKind.Int,
			"How often this account says what it is doing while it has a game open. 0 follows the global setting under Logging; -1 keeps this account quiet. A boosting account you never look at can sit on an hour while the one you actually care about reports every few minutes.",
			Min: -1, Max: 1440),
		new("StatusQuietEveryMinutes", "And while resting, every", SecAccount, SettingKind.Int,
			"The same, for when this account is asleep, on a break, paused or signed out. 0 follows the global setting; -1 keeps it quiet.",
			Min: -1, Max: 1440),
		new("LogColour", "Colour in the log", SecAccount, SettingKind.Choice,
			"What colour to paint this account's name in the log and on the board. Handy once there are more than a few accounts - the eye finds a colour far faster than it reads a name. \"Automatic\" colours by state instead: grey when signed out, amber when paused.",
			Choices: NocatFarm.Core.NameColour.ChoicesSpec),
		new("Notes", "Notes", SecAccount, SettingKind.Text,
			"A note to yourself about what this account is for. It shows on the account card and goes nowhere near Steam.",
			Placeholder: "main idler, don't touch"),
		new("SteamParentalCode", "Family View PIN", SecAccount, SettingKind.Secret,
			"Family View PIN, if this account has Family View switched on. Without it Steam blocks the pages card farming needs to read.",
			Advanced: true),
		new("MachineName", "Device name", SecAccount, SettingKind.Text,
			"The device name this account shows in Steam's list of authorised devices. Leave it empty to use this PC's real name.",
			Advanced: true),
		new("SharedSecret", "Authenticator code secret", SecAccount, SettingKind.Secret,
			"This account's mobile authenticator shared secret. With it, nocat.farm answers its own Steam Guard prompts and the account logs back in unattended - which is the difference between a farmer that survives a reboot at 4am and one that sits there waiting for you. Drop the account's maFile into config/authenticators/ and it is picked up automatically instead.",
			Advanced: true),
		new("IdentitySecret", "Authenticator confirm secret", SecAccount, SettingKind.Secret,
			"The other half of the same authenticator, used to clear the \"confirm on your phone\" prompt. Only matters when this account GIVES something away - receiving never needs confirming - so a donation-only idler can leave it empty.",
			Advanced: true),

		// ── Human mode ──
		new("LegitMode", "Human mode", SecHuman, SettingKind.Bool,
			"Play like a person: one game at a time, sittings of realistic length, breaks and meals, quiet days, and offline overnight. Everything that would look like a bot is switched off and hidden while this is on, and comes back exactly as you left it when you turn it off."),
		new("LegitStopMaxSeconds", "When stopped, finish up for up to", SecHuman, SettingKind.Int,
			"Human mode only: on a manual stop, keep playing for a random few seconds up to this many (a person finishing up) before logging off, instead of vanishing mid-game. Seconds; 0 stops instantly.",
			Min: 0, Max: 300),
		new("GameWeights", "Games and how often", SecHuman, SettingKind.Text,
			"Which games it plays and roughly how much of its time each one gets. The FIRST game is the main game - the one this account is meant to be into - and the rest are what it dips into now and then.",
			Placeholder: "730:70, 440:20, 550:10", Mode: "legit"),

		// how the day is shaped
		new("WeekdayHours", "Hours on a weekday", SecHuman, SettingKind.Int,
			"Roughly how many hours it plays Monday to Friday. The real number is rolled every morning around this one - some days short, some days long - so no two days match.",
			Min: 0, Max: 20, Mode: "legit"),
		new("WeekendHours", "Hours at the weekend", SecHuman, SettingKind.Int,
			"Same again for Saturday and Sunday. Weekends running longer than weekdays is most of what makes a week look real.",
			Min: 0, Max: 20, Mode: "legit"),
		new("DayOffChancePct", "Chance of a day off", SecHuman, SettingKind.Int,
			"The chance, out of 100, that it does not play at all on a given day. Somebody who games every single day without exception is a bot - this is what stops that.",
			Min: 0, Max: 60, Mode: "legit"),
		new("DayStartHour", "Gets on around", SecHuman, SettingKind.Int,
			"The hour it usually comes online, 0-23. The real time lands in a bell around it and weekends start earlier, so it is never the same minute twice.",
			Min: 0, Max: 23, Mode: "legit"),
		new("BedHour", "Goes to bed around", SecHuman, SettingKind.Int,
			"The hour it stops for the night, 0-23. Small hours are fine - 2 means 2am.",
			Min: 0, Max: 23, Mode: "legit"),
		new("LateNightExtraHours", "Stays up later Fri/Sat", SecHuman, SettingKind.Int,
			"Extra hours it stays up on a Friday and Saturday night, when there is nothing on tomorrow.",
			Min: 0, Max: 6, Mode: "legit"),

		// how the games are split
		new("MainGameSharePct", "Main game gets", SecHuman, SettingKind.Int,
			"What share the FIRST game in the list takes on a day it plays anything else, as a percentage - rolled fresh each day within about 10 points of this and held there however many other games you add. Across a whole week the main game lands well above this number, because most days it is the only thing played at all: that is what \"Days on the main game only\" below controls.",
			Min: 5, Max: 95, Mode: "legit"),
		new("PureMainDayChancePct", "Days on the main game only", SecHuman, SettingKind.Int,
			"The chance, out of 100, that a whole day goes on the main game alone. This is what makes the other games arrive in bursts instead of the same little slice every single day - which is the most obvious pattern a bot leaves behind.",
			Min: 0, Max: 100, Mode: "legit"),
		new("SideGameSharePct", "On a mixed day, others get", SecHuman, SettingKind.Int,
			"On the days it does branch out, roughly what share of that day the other games take between them. Once that is used up it is back on the main game for the rest of the day.",
			Min: 1, Max: 90, Mode: "legit"),

		// settling in
		new("WarmUpMinMinutes", "Settle in for at least", SecHuman, SettingKind.Int,
			"Shortest wait after signing in before it starts anything, in minutes. Being in a game the same second the account comes online is the most mechanical thing it can do - a person opens Steam, looks at something, and gets round to it. This also applies the moment you switch human mode on, which is when you would otherwise watch it launch a game instantly.",
			Min: 0, Max: 240, Mode: "legit"),
		new("WarmUpMaxMinutes", "And at most", SecHuman, SettingKind.Int,
			"Longest settling-in wait, in minutes. The real one is picked at random between the two. Whatever you set, it never starts within three minutes of signing in - Steam takes up to about two and a half minutes to report that the account's own owner has started playing, and launching inside that window is how a farmer takes the session off you.",
			Min: 0, Max: 480, Mode: "legit"),

		// sittings and breaks
		new("SessionMinMinutes", "Shortest sitting", SecHuman, SettingKind.Int,
			"The shortest it will stay in one game before doing something else. Nobody changes game every five minutes.",
			Min: 5, Max: 600, Mode: "legit"),
		new("SessionMaxMinutes", "Longest sitting", SecHuman, SettingKind.Int,
			"The longest it will stay in one game. Most sittings land between the two; a quick half hour and an all-evening session are both normal, so leave plenty of room between them.",
			Min: 10, Max: 1440, Mode: "legit"),
		new("BreakMinMinutes", "Shortest break", SecHuman, SettingKind.Int,
			"The shortest gap between sittings, in minutes. Most breaks sit down at this end - a drink, a tab-out.",
			Advanced: true, Min: 1, Max: 180, Mode: "legit"),
		new("BreakMaxMinutes", "Longest break", SecHuman, SettingKind.Int,
			"The longest ordinary break, in minutes. Real lengths are weighted towards the short end rather than spread evenly, because that is how people actually behave.",
			Advanced: true, Min: 2, Max: 240, Mode: "legit"),
		new("MealBreaksPerDay", "Meals a day", SecHuman, SettingKind.Int,
			"How many proper away-from-the-desk breaks it takes a day. These land around real meal times rather than at random, which is what makes them read as meals. 0 for none.",
			Min: 0, Max: 6, Mode: "legit"),
		new("MealBreakMinutes", "How long a meal takes", SecHuman, SettingKind.Int,
			"Roughly how long it is away for one of those, in minutes. Sometimes it runs well over - people get talking.",
			Min: 10, Max: 240, Mode: "legit"),
		new("SignOutOnBreakChancePct", "Chance it drops offline on a break", SecHuman, SettingKind.Int,
			"The chance, out of 100, that a break is spent appearing offline rather than just Away - which to everyone on your friends list is indistinguishable from closing Steam. It stays connected underneath, so this costs nothing and never touches Steam's login rate limit. An account visibly online for eighteen unbroken hours a day is doing something no person does. Meals are twice as likely to go offline as a quick break.",
			Min: 0, Max: 100, Mode: "legit"),
		new("MaxSignOutsPerDay", "Drop offline at most", SecHuman, SettingKind.Int,
			"A ceiling on how many times a day it does that, so the account isn't flickering on and off your friends list all evening. 0 turns it off and every break is spent Away instead.",
			Advanced: true, Min: 0, Max: 40, Mode: "legit"),

		// overnight
		new("OfflineIdleAtNight", "Bank hours overnight", SecHuman, SettingKind.Bool,
			"While it is asleep, go invisible and quietly idle anyway. Your friends list shows the account offline, nobody sees a thing, and the hours still count. If there are trading cards left to farm, the card farmer takes the night instead and works through them properly - it knows which games still have drops, which a fixed list cannot.", Mode: "legit"),
		new("OfflineIdleGames", "Games to idle overnight", SecHuman, SettingKind.AppIds,
			"What to idle while it is offline for the night, once there are no cards left to farm. As many as you like - there is nobody to see how many, so the one-game-at-a-time rule buys nothing here.", Mode: "legit"),

		// ── What it plays ──
		new("IdleGames", "Games to idle", SecPlaying, SettingKind.AppIds,
			"AppIDs to run for playtime when there's nothing left to farm, comma separated. The appID is the number in a game's Steam store URL - you can paste the whole URL.",
			Placeholder: "730, 440", Mode: "rage"),
		new("CustomGameNameEnabled", "Show a custom game name", SecPlaying, SettingKind.Bool,
			"Show your friends list something other than the real game. Switching this off keeps whatever name you have written below, so you can turn it back on without retyping it."),
		new("CustomGameName", "Show as", SecPlaying, SettingKind.Text,
			"Show this name on your profile and friends list instead of the real game. The real games keep banking playtime underneath - both happen at once.",
			Placeholder: "nocat.lol"),
		new("PlayWhileFarming", "Keep the name while farming", SecPlaying, SettingKind.Bool,
			"Keep the custom name showing even while trading cards are being farmed. Turn it off and card farming shows the real game.",
			Advanced: true),
		new("GameDevice", "Play as if on", SecPlaying, SettingKind.Choice,
			"What kind of machine your friends think you're playing on - this is what puts the little Steam Deck or phone badge next to your name.",
			Advanced: true, Choices: "0 a PC | 512 a phone | 1024 Big Picture | 2048 VR | 12288 a Steam Deck"),

		// ── Trading cards ──
		new("FarmCards", "Farm trading cards", SecCards, SettingKind.Bool,
			"Farm Steam trading cards. Games with cards left are played until they stop dropping, before anything else gets a turn."),
		new("HoursUntilCardDrops", "Hours before cards drop", SecCards, SettingKind.Float,
			"Hours a game needs before Steam will drop cards for it. 3 is right for most accounts; set 0 if this account has spent over $5 on Steam, so nothing gets played that doesn't need to be.",
			Min: 0, Max: 100),
		new("FarmingOrder", "Farm in this order", SecCards, SettingKind.Choice,
			"Which game to farm first. \"Fewest cards left\" finishes badges soonest; \"most played first\" adds the least new playtime to your library.",
			Choices: "0 most played first | 1 least played first | 2 fewest cards left | 3 most cards left | 4 random | 5 alphabetical"),
		new("PriorityGames", "Farm these first", SecCards, SettingKind.AppIds,
			"AppIDs to farm before anything else, whatever the sort order says.",
			Advanced: true),
		new("FarmPriorityOnly", "Only farm those", SecCards, SettingKind.Bool,
			"Only farm the games on the priority list and ignore every other game that still has cards.",
			Advanced: true),
		new("BlacklistedGames", "Never touch these", SecCards, SettingKind.AppIds,
			"AppIDs to never farm and never idle, whatever the badge page says.",
			Advanced: true),
		new("SkipUnplayedGames", "Skip games you've never played", SecCards, SettingKind.Bool,
			"Skip games you have never launched yourself, so nocat.farm doesn't put first-ever playtime on games you'd rather nobody saw.",
			Advanced: true),
		new("SkipRefundableGames", "Protect refundable games", SecCards, SettingKind.Bool,
			"Leave a newly bought game completely alone until you can no longer get your money back - Steam refuses a refund once a game has two hours on it, and two hours is one boost session. Applies everywhere, not just to card farming: idling, grinds and the achievement hunter all skip the game until the window closes or you have played two hours of it yourself. Free games are never held back, and the hold lifts on its own.",
			Advanced: true),
		new("RefundHoldDays", "...for this many days", SecCards, SettingKind.Int,
			"How long a newly bought game is left alone, in days. Steam's own refund window is 14 days, which is the default; raise it if you take longer than that to make up your mind.",
			Advanced: true, Min: 1, Max: 90),
		new("FarmOnlyWhileAsleep", "Only farm cards while asleep", SecCards, SettingKind.Bool,
			"Human mode only: hold card farming until the account is asleep for the night (when it goes invisible), then farm, and play its normal schedule by day. Off (the default) farms as soon as there are cards, day or night.",
			Advanced: true),
		new("FarmFromHour", "Farm cards only from", SecCards, SettingKind.Int,
			"Only farm cards from this hour, on a 24-hour clock. Leave this and \"until\" both at 0 to farm at any time.",
			Advanced: true, Min: 0, Max: 23),
		new("FarmUntilHour", "...until", SecCards, SettingKind.Int,
			"Farm cards up to this hour (24-hour clock). Set it earlier than \"from\" and the window wraps past midnight - e.g. 22 to 6 farms overnight only.",
			Advanced: true, Min: 0, Max: 24),
		new("PostFarmWindDownMinMinutes", "After the last card, keep playing at least", SecCards, SettingKind.Int,
			"Human mode only: when a card-farming run finishes, keep that game on for a random time in this range (minutes) before it steps away, instead of quitting the instant the last card drops. Set both to 0 to switch off instantly.",
			Advanced: true, Min: 0, Max: 120),
		new("PostFarmWindDownMaxMinutes", "...up to", SecCards, SettingKind.Int,
			"The top of the post-farming wind-down range, in minutes.",
			Advanced: true, Min: 0, Max: 240),
		new("FarmingDelayMinutes", "Re-check every", SecCards, SettingKind.Int,
			"How often to re-check a game while farming it, in minutes. Drops are pushed by Steam the moment they happen, so this is only a safety net.",
			Advanced: true, Min: 1, Max: 240),
		new("MaxFarmingHoursPerGame", "Give up after", SecCards, SettingKind.Int,
			"Give up on one game after this many hours and move to the next. Stops one broken game blocking the queue forever.",
			Advanced: true, Min: 1, Max: 200),
		new("FarmOffline", "Farm while appearing offline", SecCards, SettingKind.Bool,
			"Show as offline to your friends while this account farms. Steam still counts every hour and every card still drops - the only thing that changes is that nobody watches an account grind 40 games back to back.",
			Mode: "rage"),
		new("StopWhenFarmingDone", "Log out when finished", SecCards, SettingKind.Bool,
			"Log this account out once there are no cards left to farm, instead of idling.",
			Advanced: true),

		// ── Achievements ──
		new("UnlockAchievements", "Earn achievements over time", SecAchievements, SettingKind.Bool,
			"Unlock a few achievements a day in the games this account plays, easiest first. Unlocking a game's whole list at once is permanent, timestamped to the same minute and visible on the profile forever - this is the version that survives somebody looking. To do the whole lot deliberately, use the 'cheevo' command instead."),
		new("AchievementPace", "How fast", SecAchievements, SettingKind.Choice,
			"How long to leave between unlocks. Careful doubles every gap, which is what you want on an account meant to survive somebody actually reading its profile. Brisk halves them. This only stretches the waiting - it never opens the rarity gate early, so a careful account can never earn something a brisk one could not.",
			Choices: "0 careful (twice as slow) | 1 normal | 2 brisk (twice as fast)"),
		new("AchievementMaxCompletionPct", "Never finish more than", SecAchievements, SettingKind.Int,
			"A hard ceiling on how much of any one game gets completed, as a percentage. 0 leaves each game to its own tuned ceiling, which is usually the better answer - a 3-hour puzzle game and a 500-hour grind should not stop at the same figure. 100%% completion on an idled account is itself the giveaway, so this never goes above 95.",
			Min: 0, Max: 95),
		new("AchievementBoost", "Achievement boost", SecAchievements, SettingKind.Choice,
			"Auto-hunt achievements across several games without starting each grind by hand. OFF by default; most accounts won't use it. \"Games you pick\" works through the list below; \"all single-player\" finds them itself - every single-player game in the library that has achievements, is a game (never DLC, demos, soundtracks or tools) and that people actually play. Either way it takes one game at a time, plays it like a normal grind (easiest-first, at your Achievement pace, only what the hours make reachable), then rotates. On a human-mode account it stays weighted-first: a session here and there between long stretches of the normal schedule, never while asleep.",
			Advanced: true, Choices: "0 off | 1 games you pick | 2 all single-player"),
		new("AchievementBoostGames", "Boost these games", SecAchievements, SettingKind.AppIds,
			"The games the boost works through, comma separated (appIDs or store URLs). Only used when Achievement boost is \"games you pick\".",
			Advanced: true, Placeholder: "440, 400, 220"),
		new("BoostSessionHours", "Play each for about", SecAchievements, SettingKind.Int,
			"How long the boost sits on one game before rotating to the next, in hours.",
			Advanced: true, Min: 1, Max: 24),
		new("MaxBoostGamesInARow", "Human mode: max boosts in a row", SecAchievements, SettingKind.Int,
			"Human mode only: how many boost sessions it does back-to-back before a longer stretch of the normal weighted schedule - keeps a legit account weighted-first.",
			Advanced: true, Min: 1, Max: 20, Mode: "legit"),
		new("BoostRestMinutesHuman", "Human mode: weighted gap between boosts", SecAchievements, SettingKind.Int,
			"Human mode only: minutes of the normal weighted schedule between boost sessions, so hunting never dominates a legit account's day.",
			Advanced: true, Min: 15, Max: 1440, Mode: "legit"),
		new("BoostMinReviews", "Only hunt games with at least", SecAchievements, SettingKind.Int,
			"Steam reviews a game needs before \"all single-player\" will hunt it. This is the bundle-filler filter: nobody has an explanation for why their account spent an evening on a game with eleven reviews that they have never launched. 0 hunts everything. Ignored for games you pick by hand.",
			Advanced: true, Min: 0, Max: 100000),
		new("BoostOnlyPlayedGames", "Only hunt games you've played", SecAchievements, SettingKind.Bool,
			"Restrict \"all single-player\" to games this account has actually launched at some point. Off by default - a hunter starting a new game is perfectly normal - but it is the strictest way to keep the account to games that fit its history.",
			Advanced: true),
		new("InventoryIgnoreGames", "...but not these games", SecExtras, SettingKind.AppIds,
			"AppIDs to leave out of the inventory value, comma separated. This is where a game the account is BANNED in goes: its items are still sitting in the inventory but they can never be sold, so counting them inflates the total. Steam doesn't say which game an account is banned in - nothing in the inventory reliably shows it either - so this is a list you fill in rather than something guessed at.",
			Advanced: true, Placeholder: "730"),
		new("ShowInventoryValue", "Work out what its inventory is worth", SecExtras, SettingKind.Bool,
			"Show this account's inventory value on the dashboard, priced at the Steam market's median. It reads the account's OWN inventory with its own session, so a private profile is no obstacle, and it only counts items that can actually be sold - which means a game the account is banned in contributes nothing. Prices are looked up slowly in the background and cached for a day.",
			Advanced: true),
		new("YieldToFamily", "Give a shared game back when they want it", SecAchievements, SettingKind.Bool,
			"If somebody in the family starts a game this account is borrowing, hand it straight back and move on to the next one. Steam lends a shared game to one person at a time and the owner always wins, so the alternative is being thrown out mid-session and sitting there \"playing\" a game it no longer has. It stays out of the rotation for twenty minutes after they stop, rather than grabbing it the second they quit.",
			Advanced: true),
		new("HoldNewFamilyGames", "Leave brand-new family games alone", SecAchievements, SettingKind.Bool,
			"Skip a family-shared game for its first two weeks in the shared library (the same number of days as \"Protect refundable games\"), in case whoever bought it is still deciding. It can only go on when the game ARRIVED, never on how long its owner has played it - that number isn't visible from this account - so it will sometimes hold back a game the owner has already sunk hours into and can no longer refund.",
			Advanced: true),
		new("IncludeFamilyLibrary", "Include family-shared games", SecAchievements, SettingKind.Bool,
			"Also hunt games shared with this account through a Steam Family. Achievements earned on a borrowed game are recorded on THIS account exactly like an owned one. Games the family has excluded from sharing are skipped, as are non-games. Owned games are always hunted before borrowed ones, because the owner can take a shared game back at any moment.",
			Advanced: true),
		new("AchievementGrindGapMinMinutes", "While grinding, one achievement every", SecAchievements, SettingKind.Int,
			"How far apart a GRIND unlocks achievements, in minutes (the low end of a jittered range). A grind means actively sitting on one game, so this is a person's active-hunting pace, not the slow background drip. Easiest-first and still gated by the hours in the game.",
			Advanced: true, Min: 1, Max: 120),
		new("AchievementGrindGapMaxMinutes", "...up to", SecAchievements, SettingKind.Int,
			"The top of the grind unlock spacing, in minutes.",
			Advanced: true, Min: 1, Max: 240),
		new("AchievementNeverGames", "Never in these games", SecAchievements, SettingKind.AppIds,
			"Games to leave completely alone - no achievements are ever written for anything listed here, and the achievement boost never picks one. In human mode the account's headline game is skipped as well. (Note: some games, like Counter-Strike 2, keep their achievements server-side, so they can't be unlocked by anything regardless.)",
			Advanced: true),
		new("AchievementGames", "Only these games", SecAchievements, SettingKind.AppIds,
			"Restrict it to these appIDs. Leave it empty for whatever the account happens to be playing.",
			Advanced: true),

		// ── free games & badges ──
		new("ClaimFreeGames", "Claim free games", SecExtras, SettingKind.Bool,
			"Watch for games being given away free-to-keep and add them to this account automatically. These are normally-paid games, so they grow your library and your card farming - free-to-play shovelware is deliberately skipped."),
		new("CraftBadges", "Craft badges from card sets", SecExtras, SettingKind.Bool,
			"Turn completed card sets into badges once a day, which is what actually raises your Steam level. Farming cards and leaving the sets in your inventory raises nothing."),
		new("UnpackBoosterPacks", "Open booster packs", SecExtras, SettingKind.Bool,
			"Open any booster packs that land in your inventory automatically, so the cards inside count towards the sets that get crafted into badges.",
			Advanced: true),
		new("ClearInventoryNotifications", "Clear the new-items badge", SecExtras, SettingKind.Bool,
			"Clear Steam's green \"new items\" counter each time a card drops, so your inventory isn't permanently flagged as unread.",
			Advanced: true),

		new("AccountProxy", "Proxy for this account", SecAccount, SettingKind.Text,
			"Send just this account's Steam traffic through its own proxy, for example http://host:port - leave it empty to use the global proxy, or to connect straight out. Spreading accounts across IPs is what stops one machine tripping Steam's rate limit.",
			Advanced: true, NeedsRestart: true, Placeholder: "http://host:port"),
		new("AccountProxyUsername", "Proxy user name", SecAccount, SettingKind.Text,
			"User name for this account's own proxy, if it asks for one.",
			Advanced: true, NeedsRestart: true),
		new("AccountProxyPassword", "Proxy password", SecAccount, SettingKind.Secret,
			"Password for this account's own proxy, if it asks for one.",
			Advanced: true, NeedsRestart: true),

		// ── rep4rep commenting ──
		new("Rep4Rep", "Post rep4rep comments", SecComments, SettingKind.Bool,
			"Let this account post the comments rep4rep assigns. Every account earns into the same rep4rep points pool."),
		new("Rep4RepDailyCap", "Most per 24 hours", SecComments, SettingKind.Int,
			"Most comments this account will post in any 24 hours. Steam's real ceiling for people who aren't your friends is about 10 - going past it is what gets an account comment-banned.",
			Min: 1, Max: 25),
		new("Rep4RepGapMinMinutes", "Shortest gap", SecComments, SettingKind.Int,
			"Never post two comments from this account closer together than this many minutes.",
			Min: 1, Max: 720),
		new("Rep4RepGapMaxMinutes", "Longest gap", SecComments, SettingKind.Int,
			"Longest gap between comments, in minutes. The real gap is picked at random between the two, so the timing never looks mechanical.",
			Min: 1, Max: 1440),
		new("Rep4RepStartHour", "Only post from", SecComments, SettingKind.Int,
			"Earliest hour of the day this account will comment, 0-23. Comments arriving at 4am is the most obvious bot tell there is.",
			Min: 0, Max: 23),
		new("Rep4RepEndHour", "Until", SecComments, SettingKind.Int,
			"Latest hour of the day this account will comment, 1-24. Set it the same as the start hour to allow commenting around the clock.",
			Min: 1, Max: 24),
		new("Rep4RepLearnCap", "Learn the real limit", SecComments, SettingKind.Bool,
			"If Steam ever refuses a comment BEYOND the cap set above, remember that number as this account's real ceiling. It will never lower the cap on an ordinary \"you are commenting too frequently\" refusal - that message is about the gap between two comments and says nothing about the daily limit, which is normally 10.",
			Advanced: true),
		new("Rep4RepRetryRefused", "Retry a refused comment", SecComments, SettingKind.Bool,
			"Give a refused comment one more try before writing that target profile off for a day.",
			Advanced: true),

		// ── Friends & messages ──
		new("AcceptFriendRequests", "Accept friend requests", SecSocial, SettingKind.Bool,
			"Accept incoming friend requests automatically. Worth having on for a rep4rep account - Steam lets you comment far more freely on people who are already your friends."),
		new("AcceptGroupInvites", "Accept group invites", SecSocial, SettingKind.Bool,
			"Join Steam groups this account gets invited to.",
			Advanced: true),
		new("JoinGroup", "Join the nocat.farm group", SecSocial, SettingKind.Bool,
			"Have this account join the nocat.farm Steam group once, on sign-in. Purely optional - turn it off and it won't. Nothing else depends on it."),
		new("IgnoreSuspiciousInvites", "Ignore obvious spam", SecSocial, SettingKind.Bool,
			"Quietly ignore friend requests from brand new level-0 private profiles - the shape every scam bot has. Only does anything while accepting requests is on.",
			Advanced: true),
		new("RejectInvalidFriendInvites", "Turn down what you don't accept", SecSocial, SettingKind.Bool,
			"Actively decline a friend request rather than leaving it sitting in the list. Off means anything not accepted is simply left there for you to look at.",
			Advanced: true),
		new("AutoReplyEnabled", "Reply to messages", SecSocial, SettingKind.Bool,
			"Answer strangers who message this account. Switching it off keeps whatever you have written below, so you can turn it back on without retyping it - which is the whole point of having a switch rather than making an empty box mean off."),
		new("AutoReply", "Reply to messages with", SecSocial, SettingKind.Text,
			"Send this back when somebody messages this account. Leave it empty to say nothing at all. An account that plays eight hours a day and has never once answered a message looks stranger than one that says it is afk.",
			Placeholder: "afk right now, I will get back to you"),
		new("AutoReplyOncePerDay", "Only reply once a day", SecSocial, SettingKind.Bool,
			"Send that reply at most once a day to any one person. Without this, anybody who messages twice gets the identical line twice and it is instantly obvious what they are talking to."),
		new("AutoReplyDelaySeconds", "Wait before replying", SecSocial, SettingKind.Int,
			"Seconds to wait before the reply goes out. An instant answer is a robot answering; the real wait is randomised up to double this.",
			Advanced: true, Min: 0, Max: 600),
		new("FriendRequestDelayMinMinutes", "Wait at least", SecSocial, SettingKind.Int,
			"Shortest wait before a friend request is accepted, in minutes. Accepting the second it arrives is the single easiest bot tell to spot, because no person is sitting there watching for it.",
			Min: 0, Max: 720),
		new("FriendRequestDelayMaxMinutes", "And at most", SecSocial, SettingKind.Int,
			"Longest wait before accepting, in minutes. The real wait is picked at random between the two.",
			Min: 0, Max: 1440),
		new("ActOnlyWhileAwake", "Only react while awake", SecSocial, SettingKind.Bool,
			"Hold trades, replies and friend requests until human mode says this account is up. Without it the account is asleep on the friends list at 4am and still accepting trades that minute - which is worse than not pretending at all. Ignored when human mode is off.",
			Mode: "legit"),
		new("CommandMasters", "Accept commands from", SecSocial, SettingKind.Text,
			"SteamID64s allowed to drive nocat.farm by messaging this account on Steam, comma separated. Commands must start with a slash - send /help and the answer comes back as a Steam message. Anything without a slash is treated as ordinary conversation, so you can still just talk to your own account. Empty means nobody can.",
			Advanced: true, Placeholder: "76561198000000000"),

		// ── Trades ──
		new("AcceptDonations", "Accept donations", SecTrading, SettingKind.Bool,
			"Accept trades where this account gives up nothing at all. A donation can only ever gain items and never lose them, so it cannot be used to scam the account - an offer asking for even one item of yours is not a donation and is never accepted here."),
		new("AcceptFromMasters", "Accept anything from your own accounts", SecTrading, SettingKind.Bool,
			"Accept any offer at all from the SteamID64s listed below, including ones that take items. That is what lets you sweep cards off your idlers onto one account - and exactly why the list must only ever be accounts you own."),
		new("TradeMasters", "Your own accounts", SecTrading, SettingKind.Text,
			"SteamID64s trusted to take items off this account, comma separated. Anyone on this list can empty its inventory, so put nothing here you do not personally own.",
			Placeholder: "76561198000000000"),
		new("DeclineOtherTrades", "Decline everything else", SecTrading, SettingKind.Bool,
			"Actively decline every other offer instead of leaving it sitting there. Turn it off and anything not accepted is simply left alone for you to look at yourself."),
		new("SendOnFarmingFinished", "Send items when farming finishes", SecTrading, SettingKind.Bool,
			"Once there are no cards left to farm, send what this account collected to the first account listed above. That is the whole point of running several idlers: the cards end up in one inventory instead of six.",
			Advanced: true),
		new("SendItemTypes", "What to send", SecTrading, SettingKind.Text,
			"Which item types are allowed to leave this account, comma separated: cards, foils, backgrounds, emoticons, boosters, gems - or \"all\". Anything not listed here is never sent, whatever else is set.",
			Advanced: true, Placeholder: "cards"),
		new("TradeMasterToken", "Their trade link token", SecTrading, SettingKind.Text,
			"The token out of the other account's trade URL (the part after &token=). Only needed when the two accounts aren't friends - between friends you can leave it empty.",
			Advanced: true, Placeholder: "aBcD1234"),
		new("TradeDelayMinMinutes", "Wait at least", SecTrading, SettingKind.Int,
			"Shortest wait before acting on an offer, in minutes. Accepting two seconds after it lands is not something a person does.",
			Min: 0, Max: 720),
		new("TradeDelayMaxMinutes", "And at most", SecTrading, SettingKind.Int,
			"Longest wait before acting on an offer, in minutes. The real wait is picked at random between the two.",
			Min: 0, Max: 1440),

		// ── Staying out of the way ──
		new("PauseWhenYouPlay", "Stand down when you play", SecCourtesy, SettingKind.Bool,
			"Stop playing the second you launch a game on this account yourself, so nocat.farm never fights your own Steam client for the session."),
		new("ResumeDelayMinutes", "Wait before resuming", SecCourtesy, SettingKind.Int,
			"How long to wait after you stop playing before this account quietly picks up where it left off, in minutes.",
			Min: 0, Max: 240)
	];
}
