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
	Pick,

	/// <summary>
	/// An hour of the day, kept as a whole number (0-23, or 24 for "the end of the day", -1 where that means "no set
	/// time"). The dashboard shows a list of hours in the viewer's own clock - "9 am" or "09:00" - and the console takes
	/// the number, "9pm" or "21:00".
	/// </summary>
	Hour,

	/// <summary>
	/// A time of day to the minute, kept as minutes from midnight (0-1439 - 11:30 is 690). The dashboard shows a list of
	/// times in the viewer's own clock, and the console takes 11:30, 9:45pm, 12:15 AM or a plain hour.
	/// </summary>
	Time
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
	string Mode = "any",

	/// <summary>
	/// Shown on the dashboard only while another setting has one value: "FarmCardsWhen=3" (a choice's number) or
	/// "SomeSwitch=true" - or anything but one: "LearnFromOwner!=0". For a setting that only means something under one
	/// answer of another - the share of sittings that farm cards, say, which only "mixed" uses. Null shows it whenever its
	/// Mode does. The console lists it anyway.
	/// </summary>
	string? ShowWhen = null,

	/// <summary>
	/// The small titled box inside its section the dashboard draws it in - "Limits", "Selling", "Making packs". Plain
	/// English, translated by the page like a section name. Settings of one group are drawn together in the order of this
	/// list (the boxes in <see cref="Settings.GroupOrder"/>'s order, then the others as they first come). A setting whose
	/// ShowWhen names another setting in the same group is indented under it. Null: no box title. The console ignores it.
	/// </summary>
	string? Group = null
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

	// Global: where scam-watch and account-health alerts go
	public const string SecScamWatchAlerts = "Scam watch";
	public const string SecHealthAlerts = "Account health";
	public const string SecBackups = "Backups";
	public const string SecPhoneApp = "Phone app";

	// ── per-account sections, in display order ──────────────────────────────
	public const string SecAccount = "Account";
	public const string SecPlaying = "What it plays";
	public const string SecCards = "Trading cards";
	public const string SecExtras = "Free stuff";
	public const string SecSaleEvents = "Sale events";
	public const string SecPoints = "Steam Points";
	public const string SecBadges = "Badges & selling";
	public const string SecSpare = "Spare items";
	public const string SecPacks = "Booster packs";
	public const string SecInventory = "Inventory & bans";
	public const string SecAchievements = "Achievements";
	public const string SecComments = "rep4rep commenting";
	public const string SecHuman = "Human mode";
	public const string SecSocial = "Friends & messages";
	public const string SecTrading = "Trades";
	public const string SecCourtesy = "Staying out of the way";
	public const string SecFamily = "Steam Family";

	// Scam watch and account health, per account
	public const string SecScamWatch = "Scam watch";
	public const string SecHealth = "Account health";

	/// <summary>
	/// The order of the titled boxes (<see cref="SettingDef.Group"/>) in a section, where it isn't simply the order the
	/// settings come in. A box not named here follows, in the order it first comes.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string[]> GroupOrder = new Dictionary<string, string[]> {
		[SecSpare] = ["What it does", "What counts as spare", "Never touch", "Limits", "Selling", "Gems"],
		[SecPacks] = ["Making packs", "Opening packs", "Packs it doesn't open"],
		[SecPoints] = ["What it does", "What it buys", "Limits", "How often"],
		[SecScamWatch] = ["What it does", "What it checks", "On a warning", "How often"],
		[SecHealth] = ["What it does", "Tell me about", "How often"],
		[SecSaleEvents] = ["What it does", "Sales", "Steam Awards", "Next Fest", "How often"],
		[SecBackups] = ["Daily backup", "Folder and password", "What goes in", "Messages"]
	};

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

		string[] windowsOnly = ["Tray", "MinimizeToTray", "StartMinimized", "MiniOnTop", "MiniStats", "StartWithWindows", "KeepAwake",
			"TrayNotifications", "NotifyEarnings", "NotifySocial", "NotifyProblems", "NotifyTrades",
			"ScamAlertPopup", "HealthAlertPopup"];

		return [.. all.Where(d => !windowsOnly.Contains(d.Name, StringComparer.OrdinalIgnoreCase))];
	}

	public static SettingDef? FindGlobal(string name) => Global.FirstOrDefault(d => d.Name.Equals(NewName(name), StringComparison.OrdinalIgnoreCase));
	public static SettingDef? FindBot(string name) => Bot.FirstOrDefault(d => d.Name.Equals(NewName(name), StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Settings that changed name and mean the same thing, so 'set new BoosterGames 730' and 'help BoosterGames' - typed
	/// from habit, or in a script or a plugin - still reach the setting.
	/// </summary>
	private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase) {
		["BoosterGames"] = "BoosterPackGames",
		["SellDuplicates"] = "SpareItems",
		// 1.8.1: the hour it gets on and goes to bed became a time to the minute - a plain hour still sets it ("13" is 13:00)
		["DayStartHour"] = "DayStartTime",
		["BedHour"] = "BedTime"
	};

	private static string NewName(string name) => Renamed.GetValueOrDefault(name, name);

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

		// A time of day as a clock reads, 07:00 - not the minutes it's kept as. Anything that isn't one stays the number it is.
		if ((def.Kind == SettingKind.Time) && value is int minutes and >= 0 and < 24 * 60) {
			return Clock(minutes);
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

	/// <summary>
	/// What 'config' and 'set' say a setting is: <see cref="Show"/>, but a proxy's address without a user name and password
	/// typed into it (<see cref="ProxyShown"/>) - 'config all' printed 'user:pass@host:port' as it was saved, on screen, in
	/// the chats and in the log. <see cref="Show"/> itself stays the value: the dashboard compares settings with it.
	/// </summary>
	public static string Shown(object config, SettingDef def) => IsProxy(def) ? ProxyShown(Show(config, def)) : Show(config, def);

	/// <summary>A proxy's address - the global one or an account's own - which can have a user name and password typed into it.</summary>
	public static bool IsProxy(SettingDef? def) => def?.Name is nameof(GlobalConfig.WebProxy) or nameof(BotConfig.AccountProxy);

	/// <summary>
	/// A proxy's address as it may be shown: scheme://host:port when a user name and password are typed into it
	/// ('user:pass@host:port'), "***" when that won't read as an address. Without an '@' there's no password in it, and it's as saved.
	/// </summary>
	public static string ProxyShown(string address) =>
		address.Contains('@', StringComparison.Ordinal) ? Core.Bot.ProxyShown(address) ?? "***" : address;

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

	/// <summary>
	/// An hour of the day as typed: a plain number ("21", "-1"), a 12-hour time ("9pm", "9 PM", "12am" is 0, "12pm" is 12,
	/// "9:00pm"), or a 24-hour one ("21:00", "9:00", "24:00"). Only whole hours. Null when it isn't one of those.
	/// </summary>
	public static int? ParseHour(string raw) {
		// Only the dots of "a.m." and "p.m.": taken out everywhere, "1.5" read as 15 and "0.9" as 9 - a valid hour, set
		// without a word, from what was never one.
		string s = System.Text.RegularExpressions.Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), @"([ap])\.?(m)?\.?$", "$1$2");

		if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain)) {
			return plain;
		}

		// [0-9], not \d: \d takes any script's digits ("２１:00" in full width), which int.Parse then threw on.
		System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(s, @"^([0-9]{1,2})(?::(00))?\s*(am|pm|a|p)?$");

		if (!m.Success) {
			return null;
		}

		int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
		string half = m.Groups[3].Value;

		if (half.Length == 0) {
			// "21:00" - a 24-hour time needs its minutes, or it's the plain number already handled above.
			return m.Groups[2].Success && (h <= 24) ? h : null;
		}

		if (h is < 1 or > 12) {
			return null;
		}

		return half.StartsWith('p') ? (h % 12) + 12 : h % 12;
	}

	/// <summary>Minutes from midnight as a 24-hour clock reads them: 690 is 11:30.</summary>
	public static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

	/// <summary>
	/// A time of day as typed, in minutes from midnight: a 24-hour time ("11:30", "07:05", "23:59"), a 12-hour one ("9:45pm",
	/// "12:15 AM", "9pm" - "12am" is midnight) or a plain hour ("7" is 07:00). Null when it isn't one of those.
	/// </summary>
	public static int? ParseTime(string raw) {
		// The dots of "a.m." and "p.m." only, as for an hour.
		string s = System.Text.RegularExpressions.Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), @"([ap])\.?(m)?\.?$", "$1$2");
		// [0-9], not \d: \d takes any script's digits, which int.Parse then throws on.
		System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(s, @"^([0-9]{1,2})(?::([0-9]{2}))?\s*(am|pm|a|p)?$");

		if (!m.Success) {
			return null;
		}

		int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
		int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
		string half = m.Groups[3].Value;

		if (min > 59) {
			return null;
		}

		if (half.Length == 0) {
			return h <= 23 ? (h * 60) + min : null;
		}

		if (h is < 1 or > 12) {
			return null;
		}

		return ((half.StartsWith('p') ? (h % 12) + 12 : h % 12) * 60) + min;
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

			case SettingKind.Hour: {
				if (ParseHour(raw) is not { } h) {
					return $"{def.Label} must be an hour of the day - a number from {Bound(def.Min)} to {Bound(def.Max)}, or like 9pm or 21:00";
				}

				if (h < def.Min || h > def.Max) {
					return $"{def.Label} must be between {Bound(def.Min)} and {Bound(def.Max)}";
				}

				p.SetValue(config, h);

				return null;
			}

			case SettingKind.Time: {
				if (ParseTime(raw) is not { } at) {
					return $"{def.Label} must be a time of day, like 11:30 or 9:45pm";
				}

				p.SetValue(config, at);

				return null;
			}

			case SettingKind.Float: {
				// Finite too: "nan" parses, passes every range check (NaN is neither below nor above), and a NaN can't be
				// written as JSON - so every save of that account failed from then on, and each change was lost at restart.
				if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f)) {
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
				// ...unless the idle list is rotated, which is exactly what a longer one is for.
				if (def.Name is "IdleGames" or "OfflineIdleGames" && (apps.Count > Core.SteamIds.GamesAtOnce)
					&& !((def.Name == "IdleGames") && config is BotConfig { RotateIdleGames: true })) {
					return def.Name == "IdleGames"
						? $"Steam only lets an account play {Core.SteamIds.GamesAtOnce} games at once - turn on \"Rotate the idle list\" to idle more, a batch at a time"
						: $"Steam only lets an account play {Core.SteamIds.GamesAtOnce} games at once";
				}

				p.SetValue(config, apps);

				return null;
			}

			default:
				// Long enough to hold up against guessing: anyone with the file can try passwords as fast as their PC allows.
				if ((def.Name == "BackupPassword") && (raw.Length is > 0 and < Core.LockedBackup.MinPassword)) {
					return $"{def.Label} has to be at least {Core.LockedBackup.MinPassword} characters";
				}

				if ((def.Name == "WebTrustedProxies") && (Core.TrustedProxies.Unreadable(raw) is { } bad)) {
					return $"'{bad}' isn't an address or a range like 172.16.0.0/12";
				}

				// Not said back: an address that won't parse is often one with the password typed into it (user:pass@host).
				if ((def.Name is "WebProxy" or "AccountProxy") && (raw.Length > 0) && !Core.Bot.ProxyReadable(raw)) {
					return $"{def.Label} has to be an address like http://host:port, and that one can't be used. A user name and password go in their own settings below it";
				}

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

	/// <summary>
	/// A choice by its label: the whole label, or the start of exactly one. Nothing typed matched every label and took the
	/// first ("" set Appear as to offline), and "o" picked offline over online by being listed first.
	/// </summary>
	internal static int? MatchChoice(SettingDef def, string raw) {
		if (raw.Length == 0) {
			return null;
		}

		List<(int Value, string Label)> options = ParseChoices(def);

		if (options.FirstOrDefault(o => o.Label.Equals(raw, StringComparison.OrdinalIgnoreCase)) is { Label: not null } exact) {
			return exact.Value;
		}

		List<(int Value, string Label)> starts = [.. options.Where(o => o.Label.StartsWith(raw, StringComparison.OrdinalIgnoreCase))];

		return starts.Count == 1 ? starts[0].Value : null;
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
	/// <returns>One human-readable line per pair that had to be moved - English for the console, translated for the dashboard.</returns>
	public static List<Core.Said> FixRanges(object config, string? justSet = null) =>
		FixRanges(config, justSet == null ? [] : [justSet]);

	/// <summary>
	/// The same, for a save that changed several settings at once - the dashboard's. Every "longest" in
	/// <paramref name="justSet"/> keeps its number and pulls its "shortest" down; it used to be given no names, so a
	/// longest lowered below its shortest there snapped back up, while the same change typed at the console stuck.
	/// </summary>
	public static List<Core.Said> FixRanges(object config, IReadOnlyCollection<string> justSet) {
		List<Core.Said> adjusted = [];

		foreach ((string min, string max) in RangePairs) {
			if ((Read(config, min) is not int lo) || (Read(config, max) is not int hi) || (hi >= lo)) {
				continue;
			}

			// Move the side the user did NOT just touch - both touched, the longest gives way as before.
			bool moveMin = justSet.Contains(max, StringComparer.Ordinal) && !justSet.Contains(min, StringComparer.Ordinal);
			string moving = moveMin ? min : max;
			int target = moveMin ? hi : lo;

			SettingDef? def = Bot.FirstOrDefault(d => d.Name == moving) ?? Global.FirstOrDefault(d => d.Name == moving);

			if (def == null) {
				continue;
			}

			Apply(config, def, target.ToString(System.Globalization.CultureInfo.InvariantCulture));
			adjusted.Add(new Core.Said("{0} moved to {1} to match", new Core.Said(def.Label), target));
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
	/// <param name="skipChosen">The same save also changed "Skip multiplayer achievements" - the owner chose it, so it
	/// stays as chosen rather than following the mode. One dashboard save that turned human mode on and the skip off
	/// ended with the skip on.</param>
	/// <remarks>
	/// Never Steam Points or Spare items: both go on only through their confirm popup ('points on', 'spare on'), so whatever
	/// they are is the owner's own choice. Human mode switched off and on again for a test used to turn spending off - and
	/// its go-ahead with it - and the account lost a day of spending nobody had asked to stop.
	/// </remarks>
	public static void ApplyLegitMode(BotConfig cfg, bool wasLegit, bool skipChosen = false) {
		if (cfg.LegitMode == wasLegit) {
			return;
		}

		// Skipping multiplayer achievements goes with the mode: on for a human-mode account, off for a robot - unless it
		// was chosen in the same save.
		if (!skipChosen) {
			cfg.AchievementSkipMultiplayer = cfg.LegitMode;
		}

		// Booster packs are never opened on a human-mode account unless you turn it on: turning human mode on takes "only
		// when the cards are worth more" to never. "Always", chosen, stays.
		if (cfg.LegitMode && (cfg.OpenBoosterPacks == 1)) {
			cfg.OpenBoosterPacks = 0;
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

	/// <summary>Off, in any casing - every word <see cref="Apply"/> reads as off.</summary>
	public static bool IsFalse(string v) => v.ToLowerInvariant() is "0" or "off" or "no" or "n" or "false";

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
		new("WebSignInCode", "Code on Telegram for sign-ins from outside", SecDashboard, SettingKind.Bool,
			"Signing in from outside your home takes the password and then a 6-digit code sent to your Telegram, so the password alone isn't enough. At home and on this PC the password is all it asks. Needs Telegram connected (Settings, Notifications); until it is, the password is enough from outside too."),
		new("WebRemoteOffAfter", "Turn Open from anywhere off after", SecDashboard, SettingKind.Int,
			"Wrong passwords and codes from the internet in a day before Open from anywhere switches itself off - and stays off until you turn it on again (Phone page, or anywhere on). Nothing from the internet gets in after that, a Public address set by hand included, until then. 0 never switches it off.",
			Min: 0, Max: 1000),
		new("WebPublicAddress", "Public address", SecDashboard, SettingKind.Text,
			"Only if you forwarded the port by hand or use a name like myname.duckdns.org: the address to give out for the dashboard from outside your home. Leave it empty with Open from anywhere on - it finds your address by itself.",
			Advanced: true, Placeholder: "myname.duckdns.org"),
		new("WebTrustedProxies", "Trust forwarded addresses from", SecDashboard, SettingKind.Text,
			"Only for a reverse proxy that isn't on this PC, like Caddy in front of Docker: its address or range, like 172.16.0.0/12. The visitor's address it passes on is then believed, so people on the internet are treated as the internet and not as home. A proxy on this PC needs nothing here. Leave it empty otherwise.",
			Advanced: true, Placeholder: "172.16.0.0/12"),
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
		new("MiniStats", "Mini window shows", SecBackground, SettingKind.Choice,
			"The one number beside the name at the top of the mini window. Hours are game-hours added up over all your accounts, counted like the daily summary: every game running counts, so 8 games for a whole day is 192 hours. Past week and past month are the last 7 and 30 days, today included. Cards, achievements and comments are the last 24 hours.",
			Advanced: true, Choices: "0 nothing | 1 hours today | 2 hours past week | 3 hours past month | 4 cards today | 5 achievements today | 6 comments today | 7 accounts online | 8 inventory value"),
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
		new("StuckAlarm", "Restart a stuck account", SecAllAccounts, SettingKind.Bool,
			"If an account that should be playing hasn't banked any hours for a few hours - disconnected, stuck signing in, failed, or its games gone - it tells you (on Telegram and Discord too) and restarts that account once. Never for an account you stopped, paused or are playing on, or one human mode has asleep, done for the day or on a day off. At most one restart per account every 12 hours."),
		new("StuckAlarmHours", "Stuck after", SecAllAccounts, SettingKind.Int,
			"How many hours without banking any hours, when it should be, before an account counts as stuck.",
			Advanced: true, Min: 1, Max: 48),
		// ── Notifications ──
		new("TelegramBotToken", "Telegram bot token", SecNotifications, SettingKind.Secret,
			"Paste your Telegram bot's token (make a bot by messaging @BotFather - the guide above shows how), press Save, then press Connect Telegram and press Start in Telegram."),
		new("TelegramCommands", "Take commands from Telegram", SecNotifications, SettingKind.Bool,
			"Lets you control nocat.farm from your Telegram chat: /status for a summary, /console to type commands like in the window, and every other command with a / in front. Only your own connected chat is listened to."),
		new("TelegramChatId", "Telegram chat", SecNotifications, SettingKind.Text,
			"Filled in by itself when you connect with the Connect Telegram button. Clear it to connect a different chat - the button comes back. You can also type a group's chat ID here by hand.",
			Advanced: true),
		new("DiscordBotToken", "Discord bot token", SecNotifications, SettingKind.Secret,
			"For commands from Discord, like /status. Paste your Discord bot's token (the guide above shows how to make a bot), press Save, then press Connect Discord and send /connect with the code to your bot. Not needed for notifications - those use the webhook."),
		new("DiscordCommands", "Take commands from Discord", SecNotifications, SettingKind.Bool,
			"Lets you control nocat.farm from Discord with / commands: /status for a summary, /nocat to run any console command, and the everyday ones like /cards. Works in a private chat with your bot and in your server. Only your own connected Discord account is obeyed. Off keeps the bot offline."),
		new("DiscordOwnerId", "Discord owner", SecNotifications, SettingKind.Text,
			"The one Discord account your bot obeys - filled in by itself when you send /connect with the code from Connect Discord. Clear it to connect a different account.",
			Advanced: true),
		new("DiscordWebhookUrl", "Discord webhook", SecNotifications, SettingKind.Secret,
			"Paste a Discord channel's webhook link and the notifications you pick below go there. In Discord: Server Settings, Integrations, Webhooks, New Webhook, Copy Webhook URL."),
		new("SendCardDrops", "Send card drops and badges", SecNotifications, SettingKind.Bool,
			"Cards dropping, a game's cards finished, badges crafted and booster packs made.", Advanced: true),
		new("SendFreeStuff", "Send free games, items and gifts", SecNotifications, SettingKind.Bool,
			"Free games claimed, free Points Shop and sale items, gifted games and activated keys.", Advanced: true),
		new("SendPoints", "Send Steam Points spending", SecNotifications, SettingKind.Bool,
			"Badge levels bought with Steam Points, how many points and what's left. Only real purchases are sent.", Advanced: true),
		new("SendTrades", "Send trades", SecNotifications, SettingKind.Bool,
			"Trade offers accepted, with how many items came in.", Advanced: true),
		new("SendProblems", "Send problems that need you", SecNotifications, SettingKind.Bool,
			"Anything that needs you: a Steam Guard code, a failed sign-in, a comment ban, listings waiting for your phone.", Advanced: true),
		new("SendUpdates", "Send updates", SecNotifications, SettingKind.Bool,
			"A new version is out, and what's new in it.", Advanced: true),
		new("SendInstalls", "Send install progress", SecNotifications, SettingKind.Bool,
			"When an update installs: downloaded and installing, then installed with the new version and what's new - or that it failed or was undone, and why. Off unless you turn it on.", Advanced: true),
		new("SendSignIns", "Send dashboard sign-ins", SecNotifications, SettingKind.Bool,
			"Somebody signed in to the dashboard from outside your home. Signing in at home is only written in the visitor log - type visitors.", Advanced: true),
		new("SendItems", "Send spare items and booster packs", SecNotifications, SettingKind.Bool,
			"One message per look by Spare items or at booster packs: how many items became gems or were listed and how many packs were opened, the first 10 by name - for example one message for 14 items, not 14. Off: none of these are sent.", Advanced: true),
		new("SendBreakIns", "Send break-in attempts", SecNotifications, SettingKind.Bool,
			"Somebody is guessing the dashboard password: an address locked out after wrong passwords or codes, signing in from outside paused after too many, or Open from anywhere switched off by itself.", Advanced: true),
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
			"Which accounts the card is about: what its top line says they're doing. Automatic is every account not in human mode, or tick the ones you want. The counts on the card always include every account.",
			Advanced: true),
		new("DiscordFeatured", "Featured account", SecDiscordProfile, SettingKind.Text,
			"The account whose Steam avatar sits on the corner of the nocat.farm logo on your Discord card - clicking it opens that account's Steam profile. Any account works, human mode too. Empty uses the first account shown.",
			Advanced: true),
		new("DiscordSecondLine", "Second line", SecDiscordProfile, SettingKind.Choice,
			"What the second line of the Discord card shows: how many Steam accounts are on nocat.farm, the cards dropped today, or the hours played in the past week or the past month - added up over all your accounts. The Steam accounts count needs Count me as a user on; until it's known, it shows the cards today.",
			Advanced: true, Choices: "3 Steam accounts on nocat.farm | 0 cards today | 1 hours past week | 2 hours past month"),
		new("DiscordShowCounter", "Show accounts online", SecDiscordProfile, SettingKind.Bool,
			"Adds how many of your accounts are signed in to the Discord card - all of them, including ones the card doesn't name - like \"3 accounts linked\" or \"2 of 3 accounts linked\".",
			Advanced: true),
		new("DiscordShowAvatar", "Show an account's avatar", SecDiscordProfile, SettingKind.Bool,
			"Puts the first shown account's Steam avatar in the corner of the nocat.farm logo. Hovering it shows the name, clicking it opens the Steam profile.",
			Advanced: true),
		new("DiscordShowTimer", "Show how long it's been running", SecDiscordProfile, SettingKind.Bool,
			"The Discord card counts up from when nocat.farm was opened, like a game's play time.",
			Advanced: true),
		new("DiscordButton1", "Button 1", SecDiscordProfile, SettingKind.Text,
			"The first of the two buttons Discord allows on the card: Get nocat.farm, an account's Steam page, or your own link with your own text. Other people see the buttons - Discord doesn't show your own to you.",
			Advanced: true),
		new("DiscordButton2", "Button 2", SecDiscordProfile, SettingKind.Text,
			"The second button, the same way: Get nocat.farm, an account's Steam page, your own link, or none.",
			Advanced: true),
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
		// ── scam watch and account health - where the alerts go ──
		new("ScamAlertTelegram", "Scam warnings on Telegram", SecScamWatchAlerts, SettingKind.Bool,
			"Sends scam-watch warnings to your Telegram chat. For example, a new device signing in on one account sends one message naming the account and the device. On: every warning goes there. Off: none go to Telegram - the log and the dashboard still show them. Needs Telegram set up under Notifications."),
		new("ScamAlertDiscord", "Scam warnings on Discord", SecScamWatchAlerts, SettingKind.Bool,
			"Sends scam-watch warnings to your Discord channel. For example, a new API key on one account sends one message naming the account. On: every warning goes there. Off: none go to Discord - the log and the dashboard still show them. Needs the Discord webhook set up under Notifications."),
		new("ScamAlertPopup", "Scam warnings as a pop-up", SecScamWatchAlerts, SettingKind.Bool,
			"Shows a pop-up on this PC for each scam-watch warning. For example, a changed email on one account pops up one box naming the account. On: a pop-up for every warning, as long as Show pop-ups is on. Off: no pop-up - the log and the dashboard still show it."),
		new("ScamWatchTrustedDevices", "Devices of yours to ignore", SecScamWatchAlerts, SettingKind.Text,
			"Your own devices, so their sign-ins are never reported as new. Type their names exactly as Steam lists them under Authorized devices, separated by commas - for example: My Phone, LAPTOP-7. Empty: every new device is reported. nocat.farm's own sign-ins are always recognised by themselves.",
			Advanced: true, Placeholder: "My Phone, LAPTOP-7"),
		new("HealthAlertTelegram", "Health alerts on Telegram", SecHealthAlerts, SettingKind.Bool,
			"Sends a message to your Telegram chat when something about an account gets worse. For example, a 15-day trade hold starting on one account sends one message. On: every change for the worse is sent. Off: none go to Telegram - the account's Health line still shows it. Needs Telegram set up under Notifications."),
		new("HealthAlertDiscord", "Health alerts on Discord", SecHealthAlerts, SettingKind.Bool,
			"Sends a message to your Discord channel when something about an account gets worse. For example, the market being blocked for 7 days sends one message. On: every change for the worse is sent. Off: none go to Discord - the Health line still shows it. Needs the Discord webhook set up under Notifications."),
		new("HealthAlertPopup", "Health alerts as a pop-up", SecHealthAlerts, SettingKind.Bool,
			"Shows a pop-up on this PC when something about an account gets worse. For example, Steam Guard going from the phone app to email pops up one box. On: a pop-up each time, as long as Show pop-ups is on. Off: no pop-up - the Health line still shows it."),
		// ── Inventory prices ──
		new("MarketCurrency", "Inventory prices in", SecPrices, SettingKind.Choice,
			"The currency inventory values are shown in. Pick the one your Steam store uses so totals match the market. Changing it re-checks every price.",
			Choices: "1 US dollar | 20 Canadian dollar | 21 Australian dollar | 2 British pound | 3 Euro | 5 Russian rouble | 7 Brazilian real | 8 Japanese yen | 23 Chinese yuan | 24 Indian rupee"),
		new("MarketGapSeconds", "Seconds between price lookups", SecPrices, SettingKind.Int,
			"Seconds to wait between each Steam market request. One request can price a whole game's cards or every wear of a skin; the price counted is the lowest listing. Too fast and Steam starts refusing, which leaves inventory values out of date. Higher is slower but steadier.",
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
		// One choice where there were two ("Update by itself" and "When I say update") that only made sense together.
		new("UpdateMode", "Updates", SecUpdates, SettingKind.Choice,
			"What happens when a new version is out. It always tells you first, in the log and on Telegram and Discord. Install when I click, once everyone's asleep: the Update button or update accept installs it once your accounts are asleep - no human-mode account awake, nobody playing, no trade or gift waiting. Install when I click: right away. Install by itself at night: it installs on its own in the hours below, at a quiet time, and tells you before and after. update now always installs right away. Not in Docker or as a Linux service, which update by hand.",
			Choices: "0 install when I click, once everyone's asleep | 1 install when I click | 2 install by itself at night", Group: "Updates"),
		new("CountMeAsUser", "Count me as a user", SecUpdates, SettingKind.Bool,
			"Tells nocat.lol once an hour that this copy is running, so its page can count users. It sends a random number made for this install, the app version, the platform and how many of your Steam accounts are signed in (just the number), for example 3, plus the hours and cards totals and the rest (gems, badges, packs, items sold) if you share them (the next setting) - nothing else: no account names, Steam IDs or settings. It's how the Steam accounts count is made. Off sends nothing.", Group: "Counting users"),
		new("ShareFarmTotals", "Share my farming totals", SecUpdates, SettingKind.Bool,
			"Adds a few numbers to the hourly \"Count me as a user\" message: the hours of game time your accounts banked, the cards they got, the items they turned into gems, the badges they crafted (foil too), the booster packs they opened and the items they sold on the market since the last one - for example 41.5 hours, 12 cards, 30 items into gems, 2 badges, 4 packs and 9 items sold. The first time it's on, it also sends once the hours and cards already in this copy's history from before. nocat.lol adds everyone's up and shows the totals on its nocat.farm page. Nothing about which account, game, card or item. On: the numbers go with the hourly message. Off: none of them do. Only while \"Count me as a user\" is on.",
			ShowWhen: "CountMeAsUser=true", Group: "Counting users"),
		new("AutoUpdateFromHour", "...between", SecUpdates, SettingKind.Hour,
			"The earliest hour it may install by itself.",
			Advanced: true, Min: 0, Max: 23, ShowWhen: "UpdateMode=2", Group: "Updates"),
		new("AutoUpdateUntilHour", "...and", SecUpdates, SettingKind.Hour,
			"The hour it stops trying for the night. Earlier than the start hour runs on past midnight, so late evening until early morning works.",
			Advanced: true, Min: 0, Max: 24, ShowWhen: "UpdateMode=2", Group: "Updates"),
		new("AutoUpdateWaitHours", "Wait after a release for", SecUpdates, SettingKind.Int,
			"Hours to wait after a new version comes out before installing it by itself, so a release with a problem can be fixed first.",
			Advanced: true, Min: 0, Max: 168, ShowWhen: "UpdateMode=2", Group: "Updates"),
		new("CheckForUpdates", "Notify if an update is available", SecUpdates, SettingKind.Bool,
			"Looks for a new version and tells you in the log, on Telegram and Discord. Turning it off also stops it installing by itself at night.",
			Advanced: true, Group: "Updates"),
		new("UpdateCheckHours", "Look for updates every", SecUpdates, SettingKind.Int,
			"Hours between two looks for a new version. It also looks once when nocat.farm starts, and whenever you type update.",
			Advanced: true, Min: 1, Max: 24, Group: "Updates"),
		new("UpdateReminders", "Remind me every hour", SecUpdates, SettingKind.Bool,
			"Reminds you in the log every hour while a new version is out. Type update accept to install it, or update skip to skip that version.",
			Advanced: true, Group: "Updates"),
		new("PluginsEnabled", "Load plugins", SecUpdates, SettingKind.Bool,
			"Loads plugins from the plugins folder. Only turn this on for plugins you wrote or fully trust. A plugin can reach everything nocat.farm can, including your Steam logins.",
			Advanced: true, NeedsRestart: true, Group: "Plugins"),
		// ── Backups ──
		new("BackupAuto", "Back up every day", SecBackups, SettingKind.Bool,
			"Makes one backup a day of everything - settings, accounts, saved logins, authenticators and history - in one file locked with your backup password, so it can be put back on any PC, even if this one dies. For example, with Back up at 4 am and Keep the last 7, there's a new backup each morning and a week of them in the folder. On: daily, once a backup password is set. Off (the default): only backups you make yourself.", Group: "Daily backup"),
		new("BackupFolder", "Save backups in", SecBackups, SettingKind.Text,
			"The folder the daily backups go in. Pick a OneDrive or Dropbox folder, or a USB drive, so they survive this PC - for example D:\\OneDrive\\nocat.farm. Empty: the backups folder next to nocat.farm.",
			Placeholder: "D:\\OneDrive\\nocat.farm", Group: "Folder and password"),
		new("BackupPassword", "Backup password", SecBackups, SettingKind.Secret,
			"The password that locks your backups - at least 12 characters, for example a short sentence you'll remember. You need it to restore a backup on a new PC, so write it down somewhere safe: without it nobody can open the backup, not even you. It stays on this PC, encrypted, and is never sent anywhere. Changing it only locks the backups made after the change - older ones still need the old password.", Group: "Folder and password"),
		new("BackupKeep", "Keep the last (backups)", SecBackups, SettingKind.Int,
			"How many daily backups stay in the folder. For example 7 keeps a week: the 8th backup deletes the oldest. Only its own backup files are ever deleted.",
			Advanced: true, Min: 1, Max: 365, Group: "Folder and password"),
		new("BackupHour", "Back up at", SecBackups, SettingKind.Hour,
			"The hour the daily backup runs - for example 4 am, while the PC is quiet. If the PC was off then, it runs as soon as nocat.farm is open again.",
			Advanced: true, Min: 0, Max: 23, ShowWhen: "BackupAuto=true", Group: "Daily backup"),
		new("BackupJitterMinutes", "Up to this many minutes later", SecBackups, SettingKind.Int,
			"A random number of minutes added to the backup time each day, up to this many. For example 30 with Back up at 4 am: somewhere between 4:00 and 4:30. 0 runs it on the hour exactly.",
			Advanced: true, Min: 0, Max: 180, ShowWhen: "BackupAuto=true", Group: "Daily backup"),
		new("BackupIncludeSettings", "Include the settings", SecBackups, SettingKind.Bool,
			"Puts the global settings in the backup: the dashboard, notifications, this page - for example your Telegram and Discord set-up. On: a restore brings them back. Off: a restore keeps the settings the new PC already has.", Advanced: true, Group: "What goes in"),
		new("BackupIncludeAccounts", "Include accounts and saved logins", SecBackups, SettingKind.Bool,
			"Puts every account's settings, its saved Steam login and its progress (human mode's day, the achievement hunt, rep4rep counts) in the backup. For example, 3 accounts come back ready to sign in. Off: a restore brings back no accounts.", Advanced: true, Group: "What goes in"),
		new("BackupIncludeAuthenticators", "Include authenticators", SecBackups, SettingKind.Bool,
			"Puts the Steam Guard authenticator files (.maFile) in the backup, if you added any - for example main.maFile. On: after a restore they can confirm trades again straight away - so the backup password matters even more. Off: you add them again by hand.", Advanced: true, Group: "What goes in"),
		new("BackupIncludeHistory", "Include history and stats", SecBackups, SettingKind.Bool,
			"Puts the day-by-day history behind the charts, the hours banked in all and the daily and weekly reports in the backup - for example a year of daily charts. On: the charts carry on after a restore. Off: they start again from empty.", Advanced: true, Group: "What goes in"),
		new("BackupIncludeLogs", "Include logs", SecBackups, SettingKind.Bool,
			"Puts the last 7 days of log files in the backup too, for example to look back at what an account did the day before something went wrong. It makes each backup bigger. Off (the default): no logs.", Advanced: true, Group: "What goes in"),
		new("BackupNotify", "Tell me", SecBackups, SettingKind.Choice,
			"When you hear about backups on Telegram and Discord. Only when a backup fails: a message only if one couldn't be made - for example the folder is missing, the disk is full or there's no permission. After every backup: a message for each backup saved too. Needs Send backups on under Notifications.",
			Advanced: true, Choices: "0 only when a backup fails | 1 after every backup", Group: "Messages"),
		new("BackupLocalCopy", "Also keep a copy here", SecBackups, SettingKind.Bool,
			"Also keeps the same backups in the backups folder next to nocat.farm, so one is at hand if the backup folder goes missing - for example a USB drive left unplugged. The same number are kept there. Does nothing when Save backups in is empty.",
			Advanced: true, Group: "Folder and password"),
		new("BackupRetryMinutes", "Try again after (minutes)", SecBackups, SettingKind.Int,
			"After a backup fails, how long to wait before trying again that day. For example 60: a backup that failed at 4 am tries again at 5 am. 0 waits until tomorrow.",
			Advanced: true, Min: 0, Max: 1440, ShowWhen: "BackupAuto=true", Group: "Daily backup"),
		new("SendBackups", "Send backups", SecNotifications, SettingKind.Bool,
			"A message when a backup fails, and why - for example the folder is missing. With Tell me set to after every backup, one for each backup saved too.", Advanced: true),
		// ── Phone app ──
		new("PhoneInstallHint", "Show how to install it on a phone", SecPhoneApp, SettingKind.Bool,
			"Shows a short how-to on the Phone page for putting the dashboard on your phone's home screen, so it opens like an app - for example in Chrome: the menu, then Add to Home screen. On: the how-to shows. Off: it's hidden, for when it's already on your phone."),
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
			"Writes a daily summary in the log of what each account earned in the last 24 hours: hours banked, cards and rep4rep comments, and everything banked so far. Every running game counts, the way Steam counts it - 32 games for an hour is 32 hours. Type stats to see it any time.",
			Advanced: true),
		new("DailyReportHour", "Summary time · hour", SecLogging, SettingKind.Hour,
			"The hour the daily summary is written, in local time - and sent, when \"Send the daily summary\" is on.",
			Min: 0, Max: 23, Advanced: true),
		new("DailyReportMinute", "Summary time · minute", SecLogging, SettingKind.Int,
			"The minute past the hour the daily summary is written. Hour 9 and minute 30 means 09:30.",
			Min: 0, Max: 59, Advanced: true),
		new("WeeklyReport", "Weekly report", SecLogging, SettingKind.Bool,
			"Once a week, each account's week next to the week before: hours banked, cards dropped, cards listed for sale, inventory value and rep4rep comments. Written in the log and sent on Telegram and Discord when those are set up. Type report week to see it any time.",
			Advanced: true),
		new("WeeklyReportDay", "Weekly report · day", SecLogging, SettingKind.Choice,
			"The day the weekly report comes. It covers the seven days before it.",
			Advanced: true, Choices: "1 Monday | 2 Tuesday | 3 Wednesday | 4 Thursday | 5 Friday | 6 Saturday | 0 Sunday"),
		new("WeeklyReportHour", "Weekly report · hour", SecLogging, SettingKind.Hour,
			"The hour the weekly report comes, in local time.",
			Min: 0, Max: 23, Advanced: true),
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
			"Signs this account in. Turn it off to keep its settings but leave the account completely alone.", Group: "Signing in"),
		new("SteamLogin", "Steam account name", SecAccount, SettingKind.Text,
			"The account name you type when signing in to Steam. Not your display name and not your email.", Group: "Signing in"),
		new("SteamPassword", "Password", SecAccount, SettingKind.Secret,
			"Optional. Leave it empty and you'll be asked once, then the account is remembered with a login token, which is safer than a saved password.", Group: "Signing in"),
		new("SignInWithQr", "Sign in with a QR code", SecAccount, SettingKind.Bool,
			"Shows a QR code on the dashboard to scan with the Steam phone app when this account needs signing in. Nothing gets typed, not even the account name.",
			Advanced: true, Group: "Signing in"),
		new("OnlineStatus", "Appear as", SecAccount, SettingKind.Choice,
			"How this account looks to your friends. Invisible still plays and farms, nobody just sees it. With \"Human mode\" on this is only the status while playing, so leave it on Online.",
			Choices: "0 offline | 1 online | 2 busy | 3 away | 4 snooze | 5 looking to trade | 6 looking to play | 7 invisible", Group: "How it shows"),
		new("IUseThisAccount", "I sign into this one myself", SecAccount, SettingKind.Bool,
			"Turn this on for an account you also use in your own Steam app. It then never changes the online status, because that would sign you out of Friends and Chat. Everything else still runs.", Group: "How it shows"),
		new("UIMode", "Sign in as", SecAccount, SettingKind.Choice,
			"What kind of Steam app this says it is when signing in. Keep Desktop, since it matches the real Steam app. The others are only for troubleshooting.",
			Choices: "7 desktop (default) | 3 web | 2 mobile | 1 big picture | 0 desktop, legacy",
			Advanced: true, Group: "How it shows"),
		new("StartPaused", "Start paused", SecAccount, SettingKind.Bool,
			"Signs in but doesn't play, farm or comment until you press Resume. Good for an account you want online and doing nothing.", Advanced: true, Group: "Signing in"),
		new("Notes", "Notes", SecAccount, SettingKind.Text,
			"A note to yourself about what this account is for. It shows on the account card and is never sent to Steam.",
			Advanced: true, Placeholder: "main idler, don't touch", Group: "Signing in"),
		new("SteamParentalCode", "Family View PIN", SecAccount, SettingKind.Secret,
			"The Family View PIN, if this account has Family View on. Without it Steam blocks the pages card farming needs.",
			Advanced: true, Group: "Signing in"),
		new("MachineName", "Device name", SecAccount, SettingKind.Text,
			"The device name this account shows in Steam's list of authorised devices. Leave it empty to use this PC's real name.",
			Advanced: true, Group: "Signing in"),
		new("SharedSecret", "Authenticator code secret", SecAccount, SettingKind.Secret,
			"The authenticator secret that lets it enter Steam Guard codes itself, so the account signs back in on its own after a reboot. Putting its maFile in the authenticators folder does the same. Keep it private.",
			Advanced: true, Group: "Authenticator"),
		new("IdentitySecret", "Authenticator confirm secret", SecAccount, SettingKind.Secret,
			"The second authenticator secret, used to accept \"confirm on your phone\" prompts. Only needed when this account gives items away, since receiving never needs confirming.",
			Advanced: true, Group: "Authenticator"),
		new("AccountProxy", "Proxy for this account", SecAccount, SettingKind.Text,
			"Sends this account's Steam traffic through its own proxy, like http://host:port. Leave empty to use the global proxy or none. Spreading accounts across IPs helps avoid Steam's rate limits.",
			Advanced: true, NeedsRestart: true, Placeholder: "http://host:port", Group: "Proxy"),
		new("AccountProxyUsername", "Proxy user name", SecAccount, SettingKind.Text,
			"User name for this account's proxy, if it needs one.",
			Advanced: true, NeedsRestart: true, Group: "Proxy"),
		new("AccountProxyPassword", "Proxy password", SecAccount, SettingKind.Secret,
			"Password for this account's proxy, if it needs one.",
			Advanced: true, NeedsRestart: true, Group: "Proxy"),
		new("ClearInventoryNotifications", "Clear the new-items badge", SecAccount, SettingKind.Bool,
			"Clears Steam's green new items counter each time a card drops.",
			Advanced: true, Group: "Steam's notifications"),
		new("ClearNotifications", "Clear Steam's notifications", SecAccount, SettingKind.Bool,
			"Marks everything in Steam's notification tray as read, like comments, gifts and invites, so the counters don't keep climbing.",
			Advanced: true, Group: "Steam's notifications"),
		// ── Human mode ──
		new("LegitMode", "Human mode", SecHuman, SettingKind.Bool,
			"Plays like a person: one game at a time, realistic sittings, breaks, meals, days off and offline at night. Bot-like settings are hidden while it's on and come back unchanged when you turn it off.", Group: "Human mode"),
		new("GameWeights", "Games and how often", SecHuman, SettingKind.Text,
			"Which games it plays and what percent of its time each gets, adding up to about 100. The first one is the main game. Leave a number off to share what's left, or write 440:0 to bench a game. These are averages over a week - one day can lean toward one game, like a person's. The game \"Achievement boost\" is on shares the other games' part.",
			Mode: "legit", Group: "Human mode"),
		new("HourTargets", "Hour targets", SecHuman, SettingKind.Text,
			"Hours to reach in a game, with an optional date. 730:100@2026-12-01 means 100 hours of CS2 by 1 December. It plays that game more until the target is hit.",
			Advanced: true, Placeholder: "730:100@2026-12-01, 440:50", Mode: "legit", Group: "Human mode"),
		// how the day is shaped
		new("WeekdayHours", "Hours on a weekday", SecHuman, SettingKind.Int,
			"Roughly how many hours it plays Monday to Friday. The real number changes every day around this one.",
			Min: 0, Max: 20, Mode: "legit", Group: "Its day"),
		new("WeekendHours", "Hours at the weekend", SecHuman, SettingKind.Int,
			"Roughly how many hours it plays on Saturday and Sunday. Longer weekends make the week look more real.",
			Min: 0, Max: 20, Mode: "legit", Group: "Its day"),
		new("DayOffChancePct", "Chance of a day off", SecHuman, SettingKind.Int,
			"The percent chance it doesn't play at all on a given day. Real people don't game every single day.",
			Min: 0, Max: 60, Mode: "legit", Advanced: true, Group: "Its day"),
		// When it gets on and goes to bed, each with how far a day may land either side. The window is a hard limit: the
		// day's roll, weekends, late nights, a night's sleep and learning from you all stay inside it (HumanMode.RollDayWith).
		new("DayStartTime", "Gets on at", SecHuman, SettingKind.Time,
			"The time it comes online, like 11:00. Every day it gets on within \"Varies by up to\" of this time, most days close to it and a little earlier at weekends.",
			Mode: "legit", Group: "Its day"),
		new("DayStartVaryMinutes", "Varies by up to (minutes)", SecHuman, SettingKind.Int,
			"How far either side of \"Gets on at\" it may get on, in minutes. Nothing pushes it outside that: with 11:00 and 30 it always gets on between 10:30 and 11:30. 0 is the same minute every day.",
			Min: 0, Max: 360, Mode: "legit", Group: "Its day"),
		new("BedTime", "Goes to bed at", SecHuman, SettingKind.Time,
			"The time it stops for the night. Past midnight is fine: 02:00 is two in the morning, after the day it got on. Every night it goes to bed within \"Varies by up to\" of this time, a little later on Friday and Saturday.",
			Mode: "legit", Group: "Its day"),
		new("BedVaryMinutes", "Varies by up to (minutes)", SecHuman, SettingKind.Int,
			"How far either side of \"Goes to bed at\" it may go to bed, in minutes. Nothing pushes it outside that: with 02:00 and 60 it always goes to bed between 01:00 and 03:00. 0 is the same minute every night.",
			Min: 0, Max: 360, Mode: "legit", Group: "Its day"),
		new("LateNightExtraHours", "Stays up later Fri/Sat", SecHuman, SettingKind.Int,
			"Up to this many hours later on Friday and Saturday nights - but never past \"Goes to bed at\" and how much it varies.",
			Min: 0, Max: 6, Mode: "legit", Advanced: true, Group: "Its day"),
		new("SleepMinHours", "Sleeps at least", SecHuman, SettingKind.Int,
			"How long a night lasts before the next day can start, in hours - a random length between this and the next setting, different every night. A night too long to fit before the latest \"Gets on at\" allows is cut short: it never gets on later than that.",
			Min: 3, Max: 14, Mode: "legit", Advanced: true, Group: "Its day"),
		new("SleepMaxHours", "...up to", SecHuman, SettingKind.Int,
			"The top of that range, in hours.",
			Min: 3, Max: 16, Mode: "legit", Advanced: true, Group: "Its day"),
		// how the games are split
		new("PureMainDayChancePct", "Days on the main game only", SecHuman, SettingKind.Int,
			"The percent chance a whole day goes on the main game only, so side games show up in bursts instead of a small slice daily. This lowers their weekly share, shown under the games box.",
			Min: 0, Max: 100, Mode: "legit", Advanced: true, Group: "Its day"),
		// a life, not just a day - all off unless you switch them on
		new("LearnFromOwner", "Learn from how I play", SecHuman, SettingKind.Choice,
			"Notes when you play on this account yourself and which games. After 7 days of that, it leans its day toward yours: when it gets on and goes to bed - only inside the times set under \"Gets on at\" and \"Goes to bed at\", never outside them - and how its time splits between the games in \"Games and how often\". It never adds a game that isn't in that list, and how long it plays still comes from the hours settings. The habits command shows what it has learned.",
			Mode: "legit", Choices: "0 off | 1 a little | 2 a lot", Group: "Learning from you"),
		new("LearnFollow", "Keeps up with changes", SecHuman, SettingKind.Choice,
			"How fast a change in when or what you play shows up. Older days count less and less - quickly follows a new routine in about a week, slowly takes about a month.",
			Mode: "legit", Choices: "0 slowly | 1 normal | 2 quickly", ShowWhen: "LearnFromOwner!=0", Group: "Learning from you"),
		new("LearnWeekends", "Weekends separately", SecHuman, SettingKind.Bool,
			"Learns your Saturdays and Sundays apart from the rest of the week, since most people play differently then.",
			Mode: "legit", ShowWhen: "LearnFromOwner!=0", Group: "Learning from you"),
		new("NewGamesFirst", "Play new games more at first", SecHuman, SettingKind.Bool,
			"A game that has just arrived in the library, bought, gifted or free, gets extra sittings for 3 to 10 days, fewer each day, like trying something new. Blacklisted or family-shared games and games your other accounts are running are left alone, and a game \"Protect refunds\" is holding waits until the hold ends.",
			Advanced: true, Mode: "legit", Group: "Learning from you"),
		new("LongerRhythms", "Quiet spells and late nights", SecHuman, SettingKind.Bool,
			"Once or twice a month it has a quiet spell of 2 to 5 days with noticeably shorter days, and now and then a Friday or Saturday night runs later than usual. Days off and the hours settings still apply. human week shows them.",
			Advanced: true, Mode: "legit", Group: "Learning from you"),
		new("JoinFriends", "Sometimes play what a friend is playing", SecHuman, SettingKind.Bool,
			"When a Steam friend is in a game this account owns, it now and then picks that game for its next sitting, at most twice a day and only while it's up. Your own accounts and the games they're running are ignored, and so are blacklisted games and games \"Protect refunds\" is holding.",
			Advanced: true, Mode: "legit", Group: "Learning from you"),
		// One knob for all the waits below (and the reaction waits under Trades and Friends): it scales them as they're
		// picked, never the numbers themselves - see Modules/ReactionSpeed for exactly which.
		new("ReactionSpeed", "Reaction speed", SecHuman, SettingKind.Choice,
			"How quickly it reacts and how long its breaks last. Every wait is still random - Quick halves them, Relaxed doubles them.",
			Choices: "0 normal | 1 quick | 2 relaxed", Mode: "legit", Group: "Sittings and breaks"),
		// settling in
		new("WarmUpMinMinutes", "Settle in for at least", SecHuman, SettingKind.Int,
			"The shortest wait after signing in before it starts a game, in minutes. Also applies when you turn on \"Human mode\", so it doesn't launch a game instantly.",
			Min: 0, Max: 240, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		new("WarmUpMaxMinutes", "And at most", SecHuman, SettingKind.Int,
			"The longest wait after signing in, in minutes. It never starts within 3 minutes of signing in, so it can't take over if you're already playing on this account.",
			Min: 0, Max: 480, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		// sittings and breaks
		new("SessionMinMinutes", "Shortest sitting", SecHuman, SettingKind.Int,
			"The shortest time it stays in one game before switching or taking a break, in minutes.",
			Min: 5, Max: 600, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		new("SessionMaxMinutes", "Longest sitting", SecHuman, SettingKind.Int,
			"The longest time it stays in one game, in minutes. Leave a wide gap between shortest and longest so sittings vary.",
			Min: 10, Max: 1440, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		new("BreakMinMinutes", "Shortest break", SecHuman, SettingKind.Int,
			"The shortest break between sittings, in minutes. Most breaks are close to this.",
			Advanced: true, Min: 1, Max: 180, Mode: "legit", Group: "Sittings and breaks"),
		new("BreakMaxMinutes", "Longest break", SecHuman, SettingKind.Int,
			"The longest normal break, in minutes.",
			Advanced: true, Min: 2, Max: 240, Mode: "legit", Group: "Sittings and breaks"),
		new("BreakAwayAfterMinMinutes", "On a break, go Away after at least", SecHuman, SettingKind.Int,
			"Human mode only. How many minutes into a break its status switches to Away or offline, like a real idle Steam client. Shorter breaks stay online.",
			Advanced: true, Min: 0, Max: 60, Mode: "legit", Group: "Sittings and breaks"),
		new("BreakAwayAfterMaxMinutes", "...up to", SecHuman, SettingKind.Int,
			"The most minutes into a break before its status switches to Away or offline.",
			Advanced: true, Min: 0, Max: 90, Mode: "legit", Group: "Sittings and breaks"),
		new("MealBreaksPerDay", "Meals a day", SecHuman, SettingKind.Int,
			"How many longer meal breaks it takes a day. They land around real meal times. 0 for none.",
			Min: 0, Max: 6, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		new("MealBreakMinutes", "How long a meal takes", SecHuman, SettingKind.Int,
			"Roughly how long a meal break lasts, in minutes. Sometimes it runs a bit over.",
			Min: 10, Max: 240, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		new("SignOutOnBreakChancePct", "Chance it drops offline on a break", SecHuman, SettingKind.Int,
			"The percent chance a break shows as offline instead of Away. It stays connected underneath, so it costs nothing. Meals go offline twice as often.",
			Min: 0, Max: 100, Mode: "legit", Advanced: true, Group: "Sittings and breaks"),
		new("MaxSignOutsPerDay", "Drop offline at most", SecHuman, SettingKind.Int,
			"The most times a day it shows offline on a break, so it doesn't flicker on and off your friends list. 0 turns it off and every break shows Away.",
			Advanced: true, Min: 0, Max: 40, Mode: "legit", Group: "Sittings and breaks"),
		// overnight
		// The list sits right under its switch and isn't behind Show advanced: the switch is on by default, and with the
		// list empty (and hidden) it used to bank nothing at all without saying so. Now an empty list means the account's
		// own most-played games, as many as the number below it.
		new("OfflineIdleAtNight", "Bank hours overnight", SecHuman, SettingKind.Bool,
			"While it's asleep, it goes invisible and keeps idling the games in \"Games to idle overnight\" - or, with none chosen, its most-played games - so hours still count while friends see it offline. If \"When to farm cards\" sends cards to the night, those farm first.", Mode: "legit", Group: "Overnight"),
		new("OfflineIdleGames", "Games to idle overnight", SecHuman, SettingKind.AppIds,
			"Games to idle while it shows offline at night. Several can run at once. Leave it empty to use its most-played games instead. Only used when \"Bank hours overnight\" is on.", Mode: "legit", Group: "Overnight"),
		new("OfflineIdleTopGames", "...or, with none chosen, its top games", SecHuman, SettingKind.Int,
			"With nothing in \"Games to idle overnight\", it idles this many of the account's most-played games at night. 1 or 2 looks the most natural - many at once is the first thing selfcheck flags.",
			Min: 1, Max: 10, Mode: "legit", Group: "Overnight"),
		new("ActOnlyWhileAwake", "Only react while awake", SecHuman, SettingKind.Bool,
			"Human mode only. Holds anything others can see, like trades, gifts, invites, replies, comments and achievements, while the account is asleep. Turn it off to answer at night too.",
			Advanced: true, Mode: "legit", Group: "Reacting"),
		new("QuietThingsWaitForDay", "Behind-the-scenes things wait for its day too", SecHuman, SettingKind.Bool,
			"Human mode only. Makes background jobs like crafting badges, claiming free games, booster packs and selling cards wait for the account's waking hours too. Off, they run at any hour.",
			Advanced: true, Mode: "legit", Group: "Reacting"),
		new("WakeDelayMinMinutes", "After waking, wait at least", SecHuman, SettingKind.Int,
			"Human mode only. How long after waking up or signing in before it does things others can see, like trades, comments or replies, in minutes. Each kind gets its own random wait.",
			Advanced: true, Min: 0, Max: 600, Mode: "legit", Group: "Reacting"),
		new("WakeDelayMaxMinutes", "...up to", SecHuman, SettingKind.Int,
			"The longest wait after waking or signing in, in minutes.",
			Advanced: true, Min: 0, Max: 900, Mode: "legit", Group: "Reacting"),
		new("QuietDelayMinMinutes", "After signing in, behind-the-scenes things wait at least", SecHuman, SettingKind.Int,
			"Human mode only. How long after signing in before background jobs like badges, free games and booster packs start, in minutes. Each gets its own random wait so they don't all hit at once.",
			Advanced: true, Min: 0, Max: 600, Mode: "legit", Group: "Reacting"),
		new("QuietDelayMaxMinutes", "...up to", SecHuman, SettingKind.Int,
			"The longest wait after signing in before background jobs start, in minutes.",
			Advanced: true, Min: 0, Max: 900, Mode: "legit", Group: "Reacting"),
		new("LegitStopMaxSeconds", "When stopped, finish up for up to", SecHuman, SettingKind.Int,
			"When you stop the account, it keeps playing for a few random seconds up to this many before signing off, instead of vanishing mid-game. 0 stops instantly. Only used when \"Human mode\" is on.",
			Min: 0, Max: 300, Advanced: true, Mode: "legit", Group: "Reacting"),
		// ── What it plays ──
		new("IdleGames", "Games to idle", SecPlaying, SettingKind.AppIds,
			"Games to idle for playtime when no cards are left to farm. Use the game ID (the number in its store link) or paste the whole link, separated by commas.",
			Mode: "rage"),
		new("IdleWholeLibrary", "Idle my whole library", SecPlaying, SettingKind.Bool,
			"Idles every game the account owns, after the ones in \"Games to idle\". Skips games in \"Never touch these\", family-shared games (unless \"Include family-shared games\" is on), and games \"Protect refunds\" is holding. Steam plays 32 at once, so turn on \"Rotate the idle list\" to give every game a turn.",
			Mode: "rage"),
		// One switch for every kind of refund, where the games are chosen - it used to be three settings and a day count,
		// behind Show advanced under Trading cards, while it guards idling, grinds and the achievement hunt just as much.
		new("SkipRefundableGames", "Protect refunds", SecPlaying, SettingKind.Bool,
			"Leaves a game alone while it can still be refunded, so nothing idles, farms, grinds or hunts it past Steam's 2 hours. That covers games bought or gifted in the last 14 days, and games new to the family library for their first 14 days."),
		new("RotateIdleGames", "Rotate the idle list", SecPlaying, SettingKind.Bool,
			"When there are more games than Steam plays at once (32, or 31 with a custom name), it idles one batch at a time and moves to the next batch every so often, so every game gets hours. Games with an hour target and the least played go first.",
			Mode: "rage"),
		new("RotateEveryHours", "Rotate every", SecPlaying, SettingKind.Int,
			"How many hours each batch idles before the next batch takes over. Only used when \"Rotate the idle list\" is on.",
			Advanced: true, Min: 1, Max: 720, Mode: "rage"),
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
			"Farms Steam trading cards. Games with cards left get played until they stop dropping, before anything else.", Group: "Farming"),
		// Human mode only: a robot account farms the moment there are cards, so the choice did nothing there.
		new("FarmCardsWhen", "When to farm cards", SecCards, SettingKind.Choice,
			"When this account farms cards - it picks the card games itself. Day: in its normal sittings. Night: only while it's asleep. Any time: nonstop until done. Mixed: some sittings farm cards and the rest play its usual games, so cards come in over a few days.",
			Choices: "0 day, in its sittings | 1 night, while it's asleep | 2 any time | 3 mixed, some sittings cards, the rest its games", Mode: "legit", Group: "Farming"),
		new("CardSittingsPct", "Share of sittings that farm cards", SecCards, SettingKind.Int,
			"Roughly what percent of sittings farm cards. Higher finishes cards sooner, lower keeps more time on its usual games. A drops run (the drops command) gets at least the main game's share, so the game you picked comes sooner.",
			Min: 5, Max: 95, Mode: "legit", ShowWhen: "FarmCardsWhen=3", Group: "Farming"),
		new("HoursUntilCardDrops", "Hours before cards drop", SecCards, SettingKind.Float,
			"How many hours a game needs before Steam drops its cards. Set 0 if this account has spent over $5 on Steam, so nothing plays longer than it needs to.",
			Min: 0, Max: 100, Advanced: true, Group: "Farming"),
		new("FarmingOrder", "Farm in this order", SecCards, SettingKind.Choice,
			"Which game to farm first. \"Fewest cards left\" finishes badges soonest. \"Most played first\" adds the least new playtime to your library.",
			Advanced: true, Choices: "0 most played first | 1 least played first | 2 fewest cards left | 3 most cards left | 4 random | 5 alphabetical", Group: "Which games first"),
		new("PriorityGames", "Farm these first", SecCards, SettingKind.AppIds,
			"Game IDs to farm before anything else, whatever the order says.",
			Advanced: true, Group: "Which games first"),
		new("FarmPriorityOnly", "Only farm those", SecCards, SettingKind.Bool,
			"Only farms the games in \"Farm these first\" and ignores every other game with cards left.",
			Advanced: true, Group: "Which games first"),
		new("BlacklistedGames", "Never touch these", SecCards, SettingKind.AppIds,
			"Game IDs that are never farmed or idled.",
			Advanced: true, Group: "Which games first"),
		new("SkipUnplayedGames", "Skip games you've never played", SecCards, SettingKind.Bool,
			"Skips games you've never launched yourself, so none of them get their first playtime from here.",
			Advanced: true, Group: "Which games first"),
		new("FarmInSittings", "Farm in sittings, not flat out", SecCards, SettingKind.Bool,
			"Farms cards in a few blocks a day with breaks between them, instead of nonstop. Only makes a real difference on an account with nothing else to idle.",
			Advanced: true, Mode: "rage", Group: "Pace"),
		new("FarmHoursPerDay", "Hours a day to farm", SecCards, SettingKind.Int,
			"Roughly how many hours a day the farming blocks add up to. It varies each day and runs longer at weekends. Only used when \"Farm in sittings, not flat out\" is on.",
			Advanced: true, Min: 1, Max: 20, Mode: "rage", Group: "Pace"),
		// Robot accounts only. On a human-mode account "When to farm cards" and its own day already decide when cards farm,
		// and a clock window on top fought them - with night farming and a 9-to-23 window, cards never farmed at all.
		new("FarmFromHour", "Farm cards only from", SecCards, SettingKind.Hour,
			"Farms cards only from this hour. Leave this and \"...until\" both at midnight to farm any time.",
			Advanced: true, Min: 0, Max: 23, Mode: "rage", Group: "Pace"),
		new("FarmUntilHour", "...until", SecCards, SettingKind.Hour,
			"Farms cards up to this hour. Set it earlier than the start hour to run past midnight - from late evening until early morning farms overnight.",
			Advanced: true, Min: 0, Max: 24, Mode: "rage", Group: "Pace"),
		new("PostFarmWindDownMinMinutes", "After the last card, keep playing at least", SecCards, SettingKind.Int,
			"Human mode only. After a game drops its last card, it keeps playing it for a few minutes, then takes a break. Set both to 0 to stop right away.",
			Advanced: true, Min: 0, Max: 120, Mode: "legit", Group: "Pace"),
		new("PostFarmWindDownMaxMinutes", "...up to", SecCards, SettingKind.Int,
			"The most minutes it keeps playing after the last card.",
			Advanced: true, Min: 0, Max: 240, Mode: "legit", Group: "Pace"),
		new("FarmingDelayMinutes", "Re-check every", SecCards, SettingKind.Int,
			"How often it re-checks a game while farming, in minutes. Steam reports drops the moment they happen, so this is just a backup.",
			Advanced: true, Min: 1, Max: 240, Group: "Pace"),
		new("MaxFarmingHoursPerGame", "Give up after", SecCards, SettingKind.Int,
			"Gives up on a game after this many hours and moves on, so one stuck game can't block the rest.",
			Advanced: true, Min: 1, Max: 200, Group: "Pace"),
		new("FarmOffline", "Farm while appearing offline", SecCards, SettingKind.Bool,
			"Shows this account as offline while it farms. Hours still count and cards still drop, friends just don't see it.",
			Mode: "rage", Advanced: true, Group: "Farming"),
		new("StopWhenFarmingDone", "Log out when finished", SecCards, SettingKind.Bool,
			"Signs this account out once no cards are left, instead of idling.",
			Advanced: true, Mode: "rage", Group: "Farming"),
		// ── Badges & selling ── (everything about booster packs is in "Booster packs" since 1.8.1)
		new("CraftBadges", "Craft badges from card sets", SecBadges, SettingKind.Bool,
			"Turns finished card sets into badges once a day, which is what raises your Steam level. Cards just sitting in the inventory raise nothing."),
		new("CraftFoilBadges", "Also craft foil badges", SecBadges, SettingKind.Bool,
			"Crafts finished foil card sets into foil badges too. Off, it crafts normal badges only and leaves foil cards alone. If Steam refuses a foil badge, only foils wait - normal badges carry on.",
			Advanced: true, ShowWhen: "CraftBadges=true"),
		new("SellPerRun", "Cards to list at a time", SecBadges, SettingKind.Int,
			"How many spare cards the sell command lists when you don't say how many, most valuable first. Spare items has its own number.",
			Advanced: true, Min: 1, Max: 25),
		// ── Spare items (1.8.0) ──
		new("SpareItems", "Deal with spare items", SecSpare, SettingKind.Bool,
			"Turns spare cards, backgrounds and emoticons into gems or sells them, a few a day - whichever pays more. For example, a 4th copy of a card worth 0.03 becomes gems when its gems are worth more. Only Steam's own items (cards, backgrounds, emoticons) - never game items or CS2 skins. It never touches what your badges still need, anything new or anything over the price limit. Turning it on first shows what it would do, and asks you to confirm; off stops it at once.", Group: "What it does"),
		new("SpareAction", "What to do with spare items", SecSpare, SettingKind.Choice,
			"Best of gems or selling turns an item into gems when the gems are worth more than selling it (by the margin below), and sells it otherwise - for example gems worth 0.05 beat a sale that pays you 0.02. Gems only: never sells. Sell only: never makes gems. Keep everything: only reports what's spare, nothing changes.",
			Choices: "0 best of gems or selling | 1 gems only | 2 sell only | 3 keep everything (just report)", ShowWhen: "SpareItems=true", Group: "What it does"),
		new("SpareGems", "Gems afterwards", SecSpare, SettingKind.Choice,
			"What happens to the gems. Automatic packs them into sacks of 1000 and keeps them tradable on a human-mode account, and keeps them loose on robot accounts so booster packs can be made from them - automatic never sells. Pack into sacks: for example 2,450 gems make 2 sacks and 450 stay loose. Send the sacks: to the account named below. \"Sell the sacks\" lists one sack per look, inside the spare-items daily limits. Picking automatic in \"Make booster packs\" changes automatic or sacks here to \"keep them loose\", because packs can't be made from gems in sacks.",
			Choices: "0 automatic | 1 keep them loose (booster packs use them) | 2 pack into 1000-gem sacks and keep them tradable | 3 send the sacks to another of my accounts | 4 sell the sacks", ShowWhen: "SpareItems=true", Group: "Gems"),
		new("SpareGemsSendTo", "Send the sacks to", SecSpare, SettingKind.Text,
			"The name of one of your other accounts here, e.g. main - the sacks go to it in a trade. Only used when \"Gems afterwards\" sends the sacks. Sending needs the phone to confirm unless the authenticator is added.",
			Advanced: true, ShowWhen: "SpareGems=3", Group: "Gems"),
		new("SpareKeepGems", "Keep at least this many gems loose", SecSpare, SettingKind.Int,
			"Gems never packed, sent or sold. For example 500 with 2,450 gems: 1,950 may go, so 1 sack is made and 1,450 stay loose. 0 packs every full 1000.",
			Advanced: true, Min: 0, Max: 1_000_000, ShowWhen: "SpareItems=true", Group: "Gems"),
		new("SpareMaxedBadgeCards", "Cards from finished badges", SecSpare, SettingKind.Bool,
			"Counts cards of a game whose badge is already finished - level 5, or the foil badge made - as spare, keeping the copies set in \"Keep this many of each card\". For example, 3 copies of one card with keep 1: 2 are spare. Off: cards of finished badges stay.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "What counts as spare"),
		new("SpareExtraCards", "Extra card copies", SecSpare, SettingKind.Bool,
			"Counts card copies beyond what the badge still needs as spare. For example, at badge level 2 it keeps 3 of each card (for levels 3, 4 and 5), so a 4th copy is spare. Off: extra copies stay.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "What counts as spare"),
		new("SpareKeepCopies", "Keep this many of each card", SecSpare, SettingKind.Int,
			"Copies of each card always kept, even when the badge is finished. For example 1: of 3 copies, 2 can go. 0 keeps none, so the last copy can go too. Cards a badge still needs are always kept.",
			Advanced: true, Min: 0, Max: 10, ShowWhen: "SpareItems=true", Group: "What counts as spare"),
		new("SpareFoils", "Foil cards too", SecSpare, SettingKind.Bool,
			"Lets foil cards count as spare too - for example a 2nd copy of a foil whose foil badge is made. Off (the default): foils always stay, since they're rarer and often worth more.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "What counts as spare"),
		new("SpareBackgrounds", "Duplicate backgrounds", SecSpare, SettingKind.Bool,
			"Counts extra copies of a profile background as spare. For example, 3 copies with keep 1: 2 are spare. Off: backgrounds stay.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "What counts as spare"),
		new("SpareKeepBackgrounds", "Keep this many of each background", SecSpare, SettingKind.Int,
			"Copies of each background always kept. For example 1: of 3 copies, 2 can go. 0 keeps none, so the last copy can go too.",
			Advanced: true, Min: 0, Max: 10, ShowWhen: "SpareBackgrounds=true", Group: "What counts as spare"),
		new("SpareEmoticons", "Duplicate emoticons", SecSpare, SettingKind.Bool,
			"Counts extra copies of an emoticon as spare. For example, 3 copies with keep 1: 2 are spare. Off: emoticons stay.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "What counts as spare"),
		new("SpareKeepEmoticons", "Keep this many of each emoticon", SecSpare, SettingKind.Int,
			"Copies of each emoticon always kept. For example 1: of 3 copies, 2 can go. 0 keeps none, so the last copy can go too.",
			Advanced: true, Min: 0, Max: 10, ShowWhen: "SpareEmoticons=true", Group: "What counts as spare"),
		new("SpareMaxPrice", "Never touch items worth more than", SecSpare, SettingKind.Float,
			"Anything worth more is never touched: never turned into gems, never sold. In this account's own Steam wallet currency, e.g. 0.25 for dollars or euros - use the same value in your own currency, e.g. 40 for yen. Worth is the higher of the cheapest listing and what it sold for over the last day: listed at 0.20 but sold for 0.30 is worth 0.30.",
			Advanced: true, Min: 0.01, Max: 1_000_000, ShowWhen: "SpareItems=true", Group: "Never touch"),
		new("SpareNewDays", "Leave new items alone for (days)", SecSpare, SettingKind.Int,
			"Days an item stays untouched after it arrives. For example 7: a card that drops on a Monday can go from the next Monday. nocat.farm counts from when it first saw the item. Everything already in the inventory the first time it looks counts as old and can go straight away - only items that come in after that wait. 0 turns the rule off.",
			Advanced: true, Min: 0, Max: 30, ShowWhen: "SpareItems=true", Group: "Never touch"),
		new("SpareKeepItems", "Never touch these items", SecSpare, SettingKind.Text,
			"Full item names as the inventory shows them, separated by commas, e.g. Sans, Gordon Freeman (Foil). A name keeps that item in every game. These are always kept. Empty: nothing is kept by name.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "Never touch"),
		new("SpareKeepGames", "Never touch items from these games", SecSpare, SettingKind.AppIds,
			"Games whose cards, backgrounds and emoticons are always kept - for example a game whose set you collect. Empty: no game is kept whole.",
			Advanced: true, ShowWhen: "SpareItems=true", Group: "Never touch"),
		new("SpareGemMarginPct", "Gems must beat selling by (%)", SecSpare, SettingKind.Int,
			"How much more the gems must be worth than selling before an item becomes gems, in percent. For example 20: if selling pays you 0.10 after Steam's fees, gems worth 0.12 or more win and it becomes gems; worth 0.11 and it's sold. Only for \"best of gems or selling\".",
			Advanced: true, Min: 0, Max: 500, ShowWhen: "SpareAction=0", Group: "What it does"),
		new("SparePerRun", "Items at a time", SecSpare, SettingKind.Int,
			"The most items one look turns into gems or sells on a robot account, most valuable first. For example 5: a look that finds 40 spare items does 5, the next look 5 more, within the daily limits. A human-mode account uses \"Items per look, human mode\" instead.",
			Advanced: true, Min: 1, Max: 50, ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SpareDailyItems", "Items a day, at most", SecSpare, SettingKind.Int,
			"The most items turned into gems or sold in one day. For example 10: two looks of 5 and it's done for the day. A human-mode account spreads them through its day instead: for example 50 in a 6-hour day, a look every hour, is about 9 a look.",
			Advanced: true, Min: 1, Max: 500, ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SpareDailyValue", "Value a day, at most", SecSpare, SettingKind.Float,
			"The most a day's items may be worth together, in this account's own Steam wallet currency. For example 1.00: ten items at 0.10 fill the day and the 11th waits for tomorrow. e.g. 1.00 for dollars or euros - use the same value in your own currency, e.g. 150 for yen.",
			Advanced: true, Min: 0.01, Max: 1_000_000, ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SpareRunHours", "Look every (hours)", SecSpare, SettingKind.Int,
			"Hours between looks for spare items - it varies a little each time. For example 12: about twice a day. A human-mode account only looks during its day.",
			Advanced: true, Min: 1, Max: 72, ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SpareGapSeconds", "Wait between items (seconds)", SecSpare, SettingKind.Int,
			"The wait between two items in one look on a robot account - it varies each time. For example 40: 5 items take about 3 minutes. A human-mode account uses \"Wait between items, human mode\" instead.",
			Advanced: true, Min: 3, Max: 600, ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SparePriceMaxAgeHours", "Trust a price for (hours)", SecSpare, SettingKind.Int,
			"How long a market price counts as fresh. For example 24: a price read yesterday morning is too old by this morning, and that item is kept until it's priced again. An item with no known price is always kept.",
			Advanced: true, Min: 1, Max: 168, ShowWhen: "SpareItems=true", Group: "Selling"),
		new("SpareSellPrice", "Selling price", SecSpare, SettingKind.Choice,
			"What anything is listed for, priced again in this account's own Steam wallet currency just before it goes up. For example, an item that sold for 0.30 a few times today, with one listing dumped at 0.10: a cent under the real price lists at 0.29, the same as the cheapest at 0.10, the median at 0.30. \"A cent under the real price\" (the safe default): what it sold for over the last day, or the cheapest listing if that's higher - one listing dumped far below is ignored. No sale lately or nobody selling: kept. The others: the same as the cheapest, or the median (never under the cheapest).",
			Advanced: true, Choices: "0 a cent under the real price | 1 the same as the cheapest | 2 the median sale price", ShowWhen: "SpareItems=true", Group: "Selling"),
		new("SpareDumpPct", "Keep an item whose cheapest listing is this far under its sales (%)", SecSpare, SettingKind.Int,
			"Keeps an item instead of listing it when its cheapest listing is this far under what it sold for, and only one sale backs that price - the two prices contradict each other. For example 50: sold once at 0.30, cheapest listing 0.12 (60% under): kept. With two or more sales it's listed a cent under what it sold for. 0 lists it on a single sale too. Only for \"a cent under the real price\".",
			Advanced: true, Min: 0, Max: 95, ShowWhen: "SpareSellPrice=0", Group: "Selling"),
		new("SpareRelistDays", "Take down unsold listings after (days)", SecSpare, SettingKind.Int,
			"Days before an unsold listing comes down and goes up again a little cheaper. For example 7: a card listed at 0.30 on the 1st, still unsold on the 8th, goes up again at 0.29 - or at 0.25 when someone else sells it at 0.26 (a dumped listing doesn't count). It never goes under \"Never go lower than\"; at that price it stays up. Only listings nocat.farm put up itself. Listings you made yourself are never touched.",
			Advanced: true, Min: 1, Max: 60, ShowWhen: "SpareItems=true", Group: "Selling"),
		// ── Spare items (1.8.1) ──
		new("SpareRelistFloorPct", "Never go lower than (% of the real sale price)", SecSpare, SettingKind.Int,
			"The lowest an unsold listing steps down to, in percent of what the item sold for over the last day. For example 80: a card that sold for 0.30 never goes under 0.24 - at that price it stays up and isn't listed again. With no sale lately it isn't lowered at all.",
			Advanced: true, Min: 1, Max: 100, ShowWhen: "SpareItems=true", Group: "Selling"),
		new("SpareMinSale", "Only sell if you get at least", SecSpare, SettingKind.Float,
			"The least a sale must pay you after Steam's fees, in this account's own Steam wallet currency. For example 0.05: a card that would pay you 0.03 is turned into gems if its gems are worth anything, and kept if not. e.g. 0.05 for dollars or euros - use the same value in your own currency, e.g. 5 for yen. 0 sells anything.",
			Advanced: true, Min: 0, Max: 1_000_000, ShowWhen: "SpareItems=true", Group: "Selling"),
		new("SpareDeclinedDays", "If you decline a listing on your phone, try again after (days)", SecSpare, SettingKind.Int,
			"How long an item waits after you decline its listing in the Steam app. For example 30: you decline a card's listing on the 1st, and that card - every copy of it - isn't listed again until the 31st. A listing taken down before it ever went up counts as declined too, and so does one it can't tell about for sure. 0 never lists it again.",
			Advanced: true, Min: 0, Max: 365, ShowWhen: "SpareItems=true", Group: "Selling"),
		new("SpareHumanGapMinMinutes", "Wait between items, human mode (minutes)", SecSpare, SettingKind.Int,
			"The shortest wait between two items on a human-mode account, in minutes. Each wait is picked at random between this and the longest below. For example 3 and 20: after one item it waits anywhere from 3 to 20 minutes before the next.",
			Advanced: true, Min: 0, Max: 240, Mode: "legit", ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SpareHumanGapMaxMinutes", "...up to (minutes)", SecSpare, SettingKind.Int,
			"The longest wait between two items on a human-mode account, in minutes. For example 20, with 3 as the shortest: each wait is somewhere from 3 to 20 minutes, so 3 items take up to about 40 minutes.",
			Advanced: true, Min: 0, Max: 240, Mode: "legit", ShowWhen: "SpareItems=true", Group: "Limits"),
		new("SpareHumanPerLook", "Items per look, human mode", SecSpare, SettingKind.Int,
			"The most items one look turns into gems or sells on a human-mode account, most valuable first. It spreads the day's items through its day - about what's left today over the looks left in it, never more than this. For example 50 a day in a 6-hour day, a look every hour: about 9 a look; with this at 3, 3 a look and the looks come sooner.",
			Advanced: true, Min: 1, Max: 50, Mode: "legit", ShowWhen: "SpareItems=true", Group: "Limits"),
		// ── Booster packs ── everything about them, in the order it happens: making, opening, the ones it doesn't open.
		// The keys are the old ones (configs stay as they were); only their section moved here in 1.8.1. The dashboard
		// draws them in three boxes: "Making packs", "Opening packs" and "Packs it doesn't open" (Settings.GroupOrder).
		new("BoosterMake", "Make booster packs", SecPacks, SettingKind.Choice,
			"Turns gems into booster packs, one pack per game a day, as Steam allows. Off: no packs are made. Games I pick: packs for the games on the list below. Automatic: the game whose normal badge is closest to its next level - the fewest cards missing, counting the cards you have - among the games Steam lets you make packs for, never one whose badge is already at level 5 and never foils. For example, a game missing 1 card of its set goes before one missing 4. Automatic opens the packs it makes itself, whatever \"Open booster packs\" says, so their cards go toward the badge; turn on \"Craft badges from card sets\" so finished sets become levels. Picking automatic keeps gems loose (\"Gems afterwards\"), because packs can't use gems in sacks. A human-mode account makes and opens packs only during its day. 'booster plan' shows what automatic would pick next.",
			Choices: "0 off | 1 games I pick | 2 automatic - closest badge", Group: "Making packs"),
		// A game list like the others now (search by name, chips with names), where it was a box of numbers to type.
		new("BoosterPackGames", "Make booster packs for", SecPacks, SettingKind.AppIds,
			"Games to turn gems into booster packs for, one pack each a day. For example two games on the list: up to two packs a day, while the gems last. Only games on Steam's booster creator can be made - games you own that have trading cards.",
			ShowWhen: "BoosterMake=1", Group: "Making packs"),
		new("BoosterGems", "Booster packs use", SecPacks, SettingKind.Choice,
			"Which gems booster packs use when the account has both kinds. Tradable first makes packs you can trade or sell. Untradable first uses up gems that can't go anywhere else.",
			Advanced: true, Choices: "0 tradable gems first | 1 untradable gems first", ShowWhen: "BoosterMake!=0", Group: "Making packs"),
		new("BoosterKeepGems", "Keep at least this many gems", SecPacks, SettingKind.Int,
			"Gems a booster pack never takes the account below. A pack is made only while the gems cover its price plus this. For example 1000 with 1,500 gems: a pack costing 400 is made and 1,100 are left, and the next one waits for more gems. 0 can use them all.",
			Advanced: true, Min: 0, Max: 1_000_000, ShowWhen: "BoosterMake!=0", Group: "Making packs"),
		new("OpenBoosterPacks", "Open booster packs", SecPacks, SettingKind.Choice,
			"Opens booster packs in the inventory, so their cards count toward badges (or become spare items). For levelling up, pick always. Never by default - nothing opens until you choose. \"Only when the cards are worth more\" opens a pack only when its 3 cards are worth more on the market than the sealed pack - for example cards at 0.08 + 0.05 + 0.05 beat a pack at 0.15; an unknown price keeps it shut. Always: every pack. Packs that automatic \"Make booster packs\" made are opened by it whatever this says. A human-mode account opens packs only during its day.",
			Choices: "0 never | 1 only when the cards are worth more | 2 always", Group: "Opening packs"),
		new("BoosterOpenNewDays", "Leave new packs alone for (days)", SecPacks, SettingKind.Int,
			"Days a pack stays shut after it arrives. For example 7: a pack from a Monday opens from the next Monday. 0 opens them straight away. Packs nocat.farm made itself aren't held back - they're opened at the next chance.",
			Advanced: true, Min: 0, Max: 30, Group: "Opening packs"),
		new("BoosterNeverOpen", "Never open packs from these games", SecPacks, SettingKind.AppIds,
			"Games whose booster packs always stay sealed - for example a game whose sealed packs you trade. Empty: no game is held back.",
			Advanced: true, Group: "Opening packs"),
		new("BoosterUnopened", "Packs it doesn't open", SecPacks, SettingKind.Choice,
			"What happens to packs left sealed because they're worth more sealed, their game is on the list above, or opening is off. Keep them: they stay. Send: to the account named below, for example main. Sell them: listed like spare items. Sending or selling them happens only while Spare items is on, and selling keeps to its limits. Packs that are new or on the market are always left alone, and so are the packs automatic \"Make booster packs\" made.",
			Advanced: true, Choices: "0 keep them | 1 send to another of my accounts | 2 sell them", Group: "Packs it doesn't open"),
		new("BoosterSendTo", "Send unopened packs to", SecPacks, SettingKind.Text,
			"The name of one of your other accounts here, e.g. main - the packs go to it in a trade. Sending needs the phone to confirm unless the authenticator is added.",
			Advanced: true, ShowWhen: "BoosterUnopened=1", Group: "Packs it doesn't open"),
		new("BoosterCheckHours", "Look for packs every (hours)", SecPacks, SettingKind.Int,
			"Hours between looks for booster packs - it varies a little each time. For example 24: about once a day.",
			Advanced: true, Min: 1, Max: 168, Group: "Opening packs"),
		// ── Achievements ──
		new("UnlockAchievements", "Earn achievements over time", SecAchievements, SettingKind.Bool,
			"Unlocks a few achievements a day in the games this account plays, easiest first. Unlocking a whole list at once shows on the profile forever, and this avoids that.", Group: "Earning"),
		new("AchievementPace", "How fast", SecAchievements, SettingKind.Choice,
			"How long it waits between unlocks. Careful doubles every wait and brisk halves them. No pace unlocks anything before the hours played allow it.",
			Choices: "0 careful (twice as slow) | 1 normal | 2 brisk (twice as fast)", ShowWhen: "UnlockAchievements=true", Group: "Earning"),
		new("AchievementMaxCompletionPct", "Finish no more than", SecAchievements, SettingKind.Int,
			"The most of any one game it will ever complete, in percent. Leaving a few unearned looks like a real library. Once a game gets there, the hunt leaves it for good - raise this and it comes back. The grind command ignores this.",
			Advanced: true, Min: 1, Max: 100, Group: "Earning"),
		new("AchievementBurstMinPct", "Quick bursts only for achievements at least", SecAchievements, SettingKind.Int,
			"Sometimes 2 or 3 achievements come a few minutes apart, like finishing a level. Only achievements at least this many percent of players have can come that way - rarer ones always come alone, with the full wait. 0 lets any achievement burst; 100 turns bursts off.",
			Advanced: true, Min: 0, Max: 100, ShowWhen: "UnlockAchievements=true", Group: "Earning"),
		new("AchievementIncludeMainGame", "Earn them in the main game too", SecAchievements, SettingKind.Bool,
			"Lets the main game from \"Human mode\" earn achievements like every other game. Best left on, since hundreds of hours with no achievements looks odd.",
			Advanced: true, Group: "Earning"),
		new("AchievementSkipMultiplayer", "Skip multiplayer achievements", SecAchievements, SettingKind.Bool,
			"Never unlocks achievements that need other players: multiplayer, co-op, zombies, versus, ranked and online ones. Those games keep a record of every match, and an achievement with no match behind it stands out. On for a human-mode account, off for a robot. Turning human mode on or off sets it to match.", Group: "Earning"),
		new("AchievementGames", "Only these games", SecAchievements, SettingKind.AppIds,
			"Game IDs to limit achievements to. Leave it empty to use whatever the account is playing.",
			Advanced: true, Group: "Which games"),
		new("AchievementNeverGames", "Never in these games", SecAchievements, SettingKind.AppIds,
			"Games to leave alone completely. No achievements are unlocked in them and boosts never pick them. In human mode the account's main game is skipped too.",
			Advanced: true, Group: "Which games"),
		new("AchievementGrindGapMinMinutes", "While grinding, one achievement every", SecAchievements, SettingKind.Int,
			"How far apart achievements unlock during a grind, in minutes. A grind sits on one game, so this is a faster, active pace. Easiest ones go first.",
			Advanced: true, Min: 1, Max: 120, Group: "Grinding"),
		new("AchievementGrindGapMaxMinutes", "...up to", SecAchievements, SettingKind.Int,
			"The longest gap between grind unlocks, in minutes.",
			Advanced: true, Min: 1, Max: 240, Group: "Grinding"),
		new("AchievementBoost", "Achievement boost", SecAchievements, SettingKind.Choice,
			"Hunts achievements across several games on its own, one game at a time. \"Games you pick\" uses the list below. \"All single-player\" picks single-player games with achievements from your library, played or not (turn on \"Only hunt games you've played\" to skip the rest). On a human-mode account the game being hunted joins the games it plays, like a side game.", Advanced: true, Choices: "0 off | 1 games you pick | 2 all single-player", Group: "Boost"),
		new("AchievementBoostGames", "Boost these games", SecAchievements, SettingKind.AppIds,
			"The games the boost works through, as game IDs or store links separated by commas. Only used when \"Achievement boost\" is set to games you pick.",
			Advanced: true, ShowWhen: "AchievementBoost=1", Group: "Boost"),
		new("BoostSessionHours", "Play each for about", SecAchievements, SettingKind.Int,
			"How long the boost plays one game before moving to the next, in hours. On a human-mode account that is playing time spread over its normal sittings.",
			Advanced: true, Min: 1, Max: 24, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostWeight", "Human mode: hunt game's weight", SecAchievements, SettingKind.Int,
			"Human mode only. The game being hunted joins the games it plays, and this is how often it comes up - a weight like the ones in \"Games and how often\". It plays in normal sittings, with breaks and bedtime, and never cuts another game short.",
			Advanced: true, Min: 1, Max: 100, Mode: "legit", ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostGamesInRotation", "Games in rotation", SecAchievements, SettingKind.Int,
			"How many games the hunt has on the go at once. It takes turns between them, and when one is resting or done, the next game on the list takes its place.",
			Advanced: true, Min: 1, Max: 20, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostHoursPerDay", "Hunt at most, hours a day", SecAchievements, SettingKind.Int,
			"The most time a day it spends on the game it's hunting. On a human-mode account the game leaves its games for the rest of the day once that's played; otherwise the hunt stops for the day. 0 is no limit.",
			Advanced: true, Min: 0, Max: 24, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostSwitchFromPct", "Move to another game at about", SecAchievements, SettingKind.Int,
			"Once a game has about this much of its achievements earned - a random point between this and the next setting, different for every game - it moves to another one, like a person who has had enough of a game for now. In percent.",
			Advanced: true, Min: 5, Max: 100, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostSwitchToPct", "...up to", SecAchievements, SettingKind.Int,
			"The top of that range. The same number as the one above makes it exact.",
			Advanced: true, Min: 5, Max: 100, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostRestDaysMin", "Come back to a game after", SecAchievements, SettingKind.Int,
			"How many days before it goes back to a game it moved on from - a random number between this and the next setting. Each time it comes back it stops a little further in, up to \"Finish no more than\".",
			Advanced: true, Min: 0, Max: 90, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("BoostRestDaysMax", "...to", SecAchievements, SettingKind.Int,
			"The top of that range, in days.",
			Advanced: true, Min: 0, Max: 180, ShowWhen: "AchievementBoost!=0", Group: "Boost"),
		new("AchievementRealLength", "Pace by how long games really take", SecAchievements, SettingKind.Bool,
			"Looks up how long each game takes a typical player (SteamSpy's public figures - only the game's ID is sent) and paces its achievements by it: the rare ones open up at the speed of that game, a chapter needs its share of the game's hours, and \"beat the game\" never comes before most of a playthrough.",
			Advanced: true, Group: "Earning"),
		new("BoostMinReviews", "Only hunt games with at least", SecAchievements, SettingKind.Int,
			"How many Steam reviews a game needs before \"all single-player\" hunts it. Keeps it off cheap bundle games nobody plays. 0 hunts everything, and games you pick by hand are always allowed.",
			Advanced: true, Min: 0, Max: 100000, ShowWhen: "AchievementBoost=2", Group: "Boost"),
		new("BoostOnlyPlayedGames", "Only hunt games you've played", SecAchievements, SettingKind.Bool,
			"Only lets \"all single-player\" hunt games this account has launched before. The strictest way to keep it to games that fit the account's history.",
			Advanced: true, ShowWhen: "AchievementBoost=2", Group: "Boost"),
		// ── Steam Family ──
		new("IncludeFamilyLibrary", "Include family-shared games", SecFamily, SettingKind.Bool,
			"Also hunts achievements in games shared with this account through Steam Family, as well as in its own games. For example, with 40 owned games and 200 shared, it can pick from all 240 - owned games always go first. On: shared games count like owned ones. Off: only games this account owns.",
			Advanced: true),
		new("YieldToFamily", "Give a shared game back when they want it", SecFamily, SettingKind.Bool,
			"Hands a borrowed family game straight back when someone in the family starts it, and moves on. For example, your brother starts Portal 2 while this account is hunting in it: it closes Portal 2 and plays something else. It stays out of the rotation for a while after they stop (\"Wait after a family member stops\", below). Off: it doesn't give way.",
			Advanced: true),
		new("BorrowFamilyCopy", "Borrow another family member's copy when one is in use", SecFamily, SettingKind.Bool,
			"When someone in your Steam Family is playing a shared game on one member's copy and another member owns it too, it switches to that free copy and carries on, instead of leaving the game out. For example, two members own Stardew Valley and one is playing it: it borrows the other copy. Only that one game's \"borrow from\" member is changed, never a copy anyone is using. Off: it leaves the game until the copy is free. For family-shared games only (\"Include family-shared games\")."),
		new("FamilyWaitMinutes", "Wait after a family member stops (minutes)", SecFamily, SettingKind.Int,
			"Minutes a family-shared game is left alone after someone in your Steam Family stops playing it, e.g. 20 - people often start again. 0 uses it straight away.",
			Advanced: true, Min: 0, Max: 240),
		// ── Free stuff ──
		// One choice where there were three chained switches (free games, then free DLC too, then the DLC's game too).
		new("ClaimFree", "Claim free games", SecExtras, SettingKind.Choice,
			"Watches for paid games given away free to keep and adds them to this account. Free-to-play junk is skipped. Games and DLC also takes paid DLC marked down to free, like a supporter pack. Steam only gives DLC to accounts that own its game, so when that game is free right now too, it claims the game first.",
			Choices: "0 off | 1 games | 2 games and DLC"),
		// ── Sale events (1.8.0): the sticker and the queue moved in here, with the Steam Awards and Next Fest ──
		new("SaleEvents", "Take part in Steam events", SecSaleEvents, SettingKind.Bool,
			"The switch for everything below: during Steam sales, the Steam Awards and Next Fest it picks up what's free - the daily sticker, free Points Shop items, the discovery queue, award nominations and votes. For example, in a 14-day winter sale that's a sticker a day, plus one for each award vote. Outside an event it only looks now and then. Off does none of it on this account.", Group: "What it does"),
		new("SaleEventsHumanToo", "Human-mode accounts too", SecSaleEvents, SettingKind.Bool,
			"Lets this human-mode account nominate and vote in the Steam Awards, claim the sale's queue item and Next Fest's item - one at a time, inside its day, minutes apart. For example, a dozen votes spread over an afternoon. On by default. Turn it off so a human-mode account only does the sticker and the queue (their own switches below). Robot accounts always take part.",
			Mode: "legit", ShowWhen: "SaleEvents=true", Group: "What it does"),
		new("SaleAwardsNominate", "Nominate in the Steam Awards", SecSaleEvents, SettingKind.Bool,
			"In late November, nominates a game in each Steam Awards category, for the nomination badge. For example, a game this account played for 300 hours goes in the category Steam says it fits. Only games this account has played, picked as set in \"Which game to nominate\"; a category with none of them is left empty. Off: no nominations.", ShowWhen: "SaleEvents=true", Group: "Steam Awards"),
		new("SaleAwardsVote", "Vote in the Steam Awards", SecSaleEvents, SettingKind.Bool,
			"In the winter sale, votes once in each Steam Awards category, for the sticker each vote gives - for example about a dozen categories, about a dozen stickers. It picks the finalist this account played most, or any finalist when it played none. Off: no votes.", ShowWhen: "SaleEvents=true", Group: "Steam Awards"),
		new("ClaimEventItems", "Claim free event items", SecSaleEvents, SettingKind.Bool,
			"Picks up the free daily sticker during Steam sales and anything that costs 0 points in the Points Shop. For example, a 14-day sale gives up to 14 stickers. Off: nothing is claimed.", ShowWhen: "SaleEvents=true", Group: "Sales"),
		new("DiscoveryQueue", "Go through the discovery queue", SecSaleEvents, SettingKind.Choice,
			"Clicks through the store's discovery queue once a day, a game at a time, like a person would. During sales this earns event items and badge progress - for example one item a day in a sale that gives one. Off: never. During sales: only while a sale is on. Every day: every day, sale or not.",
			Choices: "0 off | 1 during sales | 2 every day", ShowWhen: "SaleEvents=true", Group: "Sales"),
		new("SaleQuests", "Claim the sale's queue item", SecSaleEvents, SettingKind.Bool,
			"During a sale, once the discovery queue is done, picks up the item the sale gives for it straight away instead of at the next sticker look - for example a few hours sooner. Off: it waits for the next sticker look.",
			Advanced: true, ShowWhen: "SaleEvents=true", Group: "Sales"),
		new("SaleNextFest", "Next Fest items", SecSaleEvents, SettingKind.Bool,
			"While Steam Next Fest is on, claims its free item when it has one - for example a profile sticker. Steam hasn't given one at recent Next Fests, so usually nothing happens - this is ready if it comes back. Off: it never looks.",
			Advanced: true, ShowWhen: "SaleEvents=true", Group: "Next Fest"),
		new("SaleNominatePick", "Which game to nominate", SecSaleEvents, SettingKind.Choice,
			"Which game goes in each Steam Awards category, from the games this account played that Steam says fit it. The game it played most: for example 300 hours of Terraria beats 2 hours of Celeste. Any game it played: one of them at random. Either way it tries not to use the same game twice.",
			Advanced: true, Choices: "0 the game it played most | 1 any game it played", ShowWhen: "SaleAwardsNominate=true", Group: "Steam Awards"),
		new("SaleGapMinSeconds", "Robot accounts wait between steps (seconds)", SecSaleEvents, SettingKind.Int,
			"Seconds between two nominations or votes on a robot account, the shortest wait - for example 5. With the longest at 20, each wait is a random 5 to 20 seconds.",
			Advanced: true, Min: 1, Max: 600, ShowWhen: "SaleEvents=true", Group: "How often"),
		new("SaleGapMaxSeconds", "...up to (seconds)", SecSaleEvents, SettingKind.Int,
			"The longest wait between two steps on a robot account, in seconds - for example 20. With the shortest at 5, each wait is a random 5 to 20 seconds.",
			Advanced: true, Min: 1, Max: 600, ShowWhen: "SaleEvents=true", Group: "How often"),
		new("SaleHumanGapMinMinutes", "Human mode waits between steps (minutes)", SecSaleEvents, SettingKind.Int,
			"Minutes between two nominations or votes on a human-mode account, the shortest wait - for example 5. With the longest at 30, each wait is a random 5 to 30 minutes. Only inside its day.",
			Advanced: true, Min: 0, Max: 240, Mode: "legit", ShowWhen: "SaleEvents=true", Group: "How often"),
		new("SaleHumanGapMaxMinutes", "...up to (minutes)", SecSaleEvents, SettingKind.Int,
			"The longest wait between two steps on a human-mode account, in minutes - for example 30. With the shortest at 5, each wait is a random 5 to 30 minutes.",
			Advanced: true, Min: 0, Max: 240, Mode: "legit", ShowWhen: "SaleEvents=true", Group: "How often"),
		new("SaleCheckHours", "Look for events every (hours)", SecSaleEvents, SettingKind.Int,
			"How often it looks whether a Steam event has started, in hours - for example 6, about 4 looks a day. While one is on it looks about every hour.",
			Advanced: true, Min: 1, Max: 48, ShowWhen: "SaleEvents=true", Group: "How often"),
		// ── Steam Points (Modules/PointsSpender.cs) ──
		new("SpendPoints", "Spend Steam Points on levels", SecPoints, SettingKind.Bool,
			"Spends this account's Steam Points on levels of the Points Shop's seasonal badge - the only thing there that raises your Steam level, about 100 XP for every 1,000 points. For example, 3,000 points buy 3 levels. Off by default on every account. Turning it on first shows what it would buy, and asks you to confirm; off stops it at once.", Group: "What it does"),
		new("PointsKeepAtLeast", "Always keep at least", SecPoints, SettingKind.Int,
			"Points it never spends, so some are left for you. For example 1000 with 4,500 points: it may spend 3,500, which is 3 badge levels at 1,000 each. 0 lets it spend them all.",
			Advanced: true, Min: 0, Max: 10000000, ShowWhen: "SpendPoints=true", Group: "Limits"),
		new("PointsWeeklyCap", "Spend at most, a week", SecPoints, SettingKind.Int,
			"The most points it spends in any 7 days. For example 3000: at most 3 badge levels a week, however many points there are. 0 spends nothing.",
			Advanced: true, Min: 0, Max: 10000000, ShowWhen: "SpendPoints=true", Group: "Limits"),
		new("PointsBuySeasonalBadge", "Buy levels of the seasonal badge", SecPoints, SettingKind.Bool,
			"Lets it buy levels of the badge the Points Shop is selling now, for example the Summer Collection - a new one comes with each big Steam sale. On: it levels that badge. Off: it doesn't buy it.",
			Advanced: true, ShowWhen: "SpendPoints=true", Group: "What it buys"),
		new("PointsBuyOlderBadges", "Also older seasonal badges", SecPoints, SettingKind.Bool,
			"Also buys levels of older seasonal badges the shop still lists, once the current one is done - for example last winter's. Steam has turned some of these down, so it's off; a refusal is only logged. Off: only the current badge.",
			Advanced: true, ShowWhen: "SpendPoints=true", Group: "What it buys"),
		new("PointsBadgeMaxLevel", "Stop each badge at level", SecPoints, SettingKind.Int,
			"The highest level it takes any badge to. For example 10 stops each badge at level 10, about 10,000 points; 40 is the most Steam allows. Lower it to keep points for something else.",
			Advanced: true, Min: 1, Max: 40, ShowWhen: "SpendPoints=true", Group: "Limits"),
		new("PointsCheckEveryHours", "Look every (hours)", SecPoints, SettingKind.Int,
			"How often it looks at the points and the shop, in hours - for example 12, twice a day. A human-mode account only looks during its day.",
			Advanced: true, Min: 1, Max: 168, ShowWhen: "SpendPoints=true", Group: "How often"),
		// ── Inventory & bans ──
		new("ShowInventoryValue", "Work out what its inventory is worth", SecInventory, SettingKind.Bool,
			"Works out what this account's inventory is worth at Steam market prices and shows it on the dashboard. Turn it off on accounts that only hold a few cards.",
			Advanced: true),
		new("InventoryIgnoreGames", "...but not these games", SecInventory, SettingKind.AppIds,
			"Game IDs (the number in the store link) to leave out of the inventory value and out of every trade, separated by commas. Games the account is banned in can never trade - the ban watch adds those by itself when it can see them, and you can add them here too.",
			Advanced: true),
		new("WatchBans", "Watch for bans", SecInventory, SettingKind.Bool,
			"Looks at this account's bans every few hours - VAC, game bans, a trade ban, a community ban - and tells you straight away (log, pop-up, Discord/Telegram) when a new one appears. It only looks; it never changes anything. Games it finds the account banned in are left out of trades by themselves; trading cards keep trading."),
		// ── scam watch ──
		new("WatchForScams", "Watch for scams", SecScamWatch, SettingKind.Bool,
			"Every half hour or so, looks for the signs of a stolen account or a trade scam: an API key that wasn't there before, a device signing in that isn't yours, the email, phone or Steam Guard changing, or every session being signed out. For example, a key a fake trading site made at 14:00 is reported by about 14:30. It only looks: what happens on a warning is set below. Off: nothing is checked.", Group: "What it does"),
		new("ScamWatchStopTrades", "On a warning: stop trading", SecScamWatch, SettingKind.Bool,
			"After a warning, this account stops accepting trade offers and stops sending items by itself until you type scamwatch ok and the account name. For example, a donation that would be accepted waits for you. Answering offers yourself still works. Off: trading carries on as usual.", ShowWhen: "WatchForScams=true", Group: "On a warning"),
		new("ScamWatchCancelOffers", "On a warning: take back sent offers", SecScamWatch, SettingKind.Bool,
			"After a warning, cancels the trade offers this account has sent that haven't gone through yet - a scammer with your API key swaps them for fake ones. For example, 2 offers waiting to your other account are taken back. Off: sent offers stay.", ShowWhen: "WatchForScams=true", Group: "On a warning"),
		new("ScamWatchRevokeKey", "On a new API key: revoke it", SecScamWatch, SettingKind.Bool,
			"When an API key appears that wasn't there before, presses Revoke on Steam's key page straight away, so a key a scam site made stops working - for example within the hour it appeared. Off by default: then it only warns. Leave it off if you make API keys yourself (some trading sites ask for one).",
			Advanced: true, ShowWhen: "WatchForScams=true", Group: "On a warning"),
		new("ScamWatchApiKey", "Check for an API key", SecScamWatch, SettingKind.Bool,
			"Looks at the account's Steam Web API key page. A key that appears or changes without you is the most common trade scam - for example one made by a fake trading site you signed in to. Only a fingerprint of the key is kept, never the key. Off: keys aren't checked.",
			Advanced: true, ShowWhen: "WatchForScams=true", Group: "What it checks"),
		new("ScamWatchDevices", "Check for new devices", SecScamWatch, SettingKind.Bool,
			"Looks at the account's list of authorized devices and warns about one that is new and isn't yours or nocat.farm's own - for example a \"DESKTOP-8XQ2\" you've never seen. Off: devices aren't checked.",
			Advanced: true, ShowWhen: "WatchForScams=true", Group: "What it checks"),
		new("ScamWatchDetails", "Check email, phone and Steam Guard", SecScamWatch, SettingKind.Bool,
			"Looks at the account details page and warns when the email address, the phone number or Steam Guard changes. Only the hidden forms Steam shows are kept - for example th***@gmail.com, or a phone ending in 12. Off: these aren't checked.",
			Advanced: true, ShowWhen: "WatchForScams=true", Group: "What it checks"),
		new("ScamWatchSessions", "Check for everything signed out", SecScamWatch, SettingKind.Bool,
			"Warns when Steam refuses the saved login because every session on the account was signed out - what a thief does first, for example with \"Deauthorize all other devices\". A normal reconnect, or you playing on the account, never counts. Off: not checked.",
			Advanced: true, ShowWhen: "WatchForScams=true", Group: "What it checks"),
		new("ScamWatchMinMinutes", "Shortest wait between looks (minutes)", SecScamWatch, SettingKind.Int,
			"The shortest time between two looks, in minutes - for example 30. With the longest at 60, each wait is a random 30 to 60 minutes. Each look is a few page views, the same as opening them in a browser.",
			Advanced: true, Min: 10, Max: 1440, ShowWhen: "WatchForScams=true", Group: "How often"),
		new("ScamWatchMaxMinutes", "...up to (minutes)", SecScamWatch, SettingKind.Int,
			"The longest time between two looks, in minutes - for example 60. With the shortest at 30, each wait is a random 30 to 60 minutes.",
			Advanced: true, Min: 10, Max: 1440, ShowWhen: "WatchForScams=true", Group: "How often"),
		// ── account health ──
		new("WatchHealth", "Keep an eye on account health", SecHealth, SettingKind.Bool,
			"Shows one Health line on the account's card - level, bans, trade hold, market access, limited account, Steam Guard and phone - and tells you the moment something gets worse. For example, when a trade hold starts you get one message and the line says so. Checked when it signs in and every few hours; it only looks. Off: no Health line and no alerts.", Group: "What it does"),
		new("HealthAlertBans", "Alert on a new ban", SecHealth, SettingKind.Bool,
			"Tells you when a ban appears - for example a VAC ban, a game ban, a trade or community ban. While Watch for bans is on, that one already tells you, so this stays quiet to avoid saying it twice. Off: no ban message from here.",
			Advanced: true, ShowWhen: "WatchHealth=true", Group: "Tell me about"),
		new("HealthAlertTradeHold", "Alert on a trade hold", SecHealth, SettingKind.Bool,
			"Tells you when this account's trades start being held for days - for example 15 days after Steam Guard changes. Off: only the Health line shows it.",
			Advanced: true, ShowWhen: "WatchHealth=true", Group: "Tell me about"),
		new("HealthAlertMarket", "Alert when the market is blocked", SecHealth, SettingKind.Bool,
			"Tells you when Steam stops this account using the Community Market - for example for 7 days after signing in on a new device. Off: only the Health line shows it.",
			Advanced: true, ShowWhen: "WatchHealth=true", Group: "Tell me about"),
		new("HealthAlertLimited", "Alert on a limited account", SecHealth, SettingKind.Bool,
			"Tells you when Steam starts treating this account as limited: no friend invites, no market. For example, an account that has never spent 5 dollars is limited. Off: only the Health line shows it.",
			Advanced: true, ShowWhen: "WatchHealth=true", Group: "Tell me about"),
		new("HealthAlertGuard", "Alert when Steam Guard gets weaker", SecHealth, SettingKind.Bool,
			"Tells you when Steam Guard gets weaker - for example from the phone app to email codes, or off. Off: only the Health line shows it.",
			Advanced: true, ShowWhen: "WatchHealth=true", Group: "Tell me about"),
		new("HealthAlertPhone", "Alert when the phone is removed", SecHealth, SettingKind.Bool,
			"Tells you when the account no longer has a phone number on it - for example after it was removed on Steam's site. Off: only the Health line shows it.",
			Advanced: true, ShowWhen: "WatchHealth=true", Group: "Tell me about"),
		new("HealthMinHours", "Shortest wait between health checks (hours)", SecHealth, SettingKind.Int,
			"The shortest time between two health checks, in hours - for example 5. With the longest at 7, a check comes every 5 to 7 hours, at random. It always checks once when the account signs in.",
			Advanced: true, Min: 1, Max: 48, ShowWhen: "WatchHealth=true", Group: "How often"),
		new("HealthMaxHours", "...up to (hours)", SecHealth, SettingKind.Int,
			"The longest time between two health checks, in hours - for example 7. With the shortest at 5, a check comes every 5 to 7 hours, at random.",
			Advanced: true, Min: 1, Max: 48, ShowWhen: "WatchHealth=true", Group: "How often"),
		// ── Trades ──
		new("AcceptDonations", "Accept donations", SecTrading, SettingKind.Bool,
			"Accepts trade offers where this account gives nothing away. These can't scam you, since an offer asking for even one of your items is never accepted here.", Group: "Donations and gifts"),
		new("DonationsWhileAsleep", "Accept donations while asleep", SecTrading, SettingKind.Bool,
			"Human mode only. Lets donations go through at night even when \"Only react while awake\" is on. Everything else still waits for morning.",
			Advanced: true, Mode: "legit", Group: "Donations and gifts"),
		new("AcceptGifts", "Accept gifts and guest passes", SecTrading, SettingKind.Bool,
			"Accepts Steam wallet gift cards and guest passes sent to this account. Neither can cost anything. Each gift waits first, see \"Accept a gift after\".", Group: "Donations and gifts"),
		new("AcceptGiftedGames", "Accept gifted games", SecTrading, SettingKind.Bool,
			"Adds games that friends gift this account straight to its library. Turn it off to decide each gift yourself. Each gift waits first, see \"Accept a gift after\".", Group: "Donations and gifts"),
		new("GiftDelayMinMinutes", "Accept a gift after at least", SecTrading, SettingKind.Int,
			"The shortest wait before accepting a gift, like a game, gift card or guest pass, in minutes.",
			Min: 0, Max: 720, Advanced: true, Group: "Donations and gifts"),
		new("GiftDelayMaxMinutes", "And at most", SecTrading, SettingKind.Int,
			"The longest wait before accepting a gift, in minutes. Each gift gets a random wait, usually closer to the short end.",
			Min: 0, Max: 1440, Advanced: true, Group: "Donations and gifts"),
		new("AcceptFairCardSwaps", "Accept fair card swaps", SecTrading, SettingKind.Bool,
			"Accepts one for one card swaps from anyone within the same game, but only when they bring your sets closer to done. Offers with foils or other items are left alone. Needs the mobile authenticator.", Group: "Offers from others"),
		new("AcceptFromMasters", "Accept anything from your own accounts", SecTrading, SettingKind.Bool,
			"Accepts any offer from the accounts in \"Your own accounts\", even ones that take items. Handy for moving cards to one account, but only list accounts you own.", Group: "Your own accounts"),
		new("TradeMasters", "Your own accounts", SecTrading, SettingKind.Text,
			"SteamID64s of your own accounts, separated by commas. Anyone listed here can take items from this account, so only add accounts you own.",
			Placeholder: "76561198000000000", Group: "Your own accounts"),
		new("TradeMasterToken", "Their trade link token", SecTrading, SettingKind.Text,
			"The token from the other account's trade link, the part after &token=. Only needed if the two accounts aren't friends.",
			Advanced: true, Placeholder: "aBcD1234", Group: "Your own accounts"),
		new("AutoTradeWith", "Trade by itself with", SecTrading, SettingKind.Text,
			"Who this account trades with without asking you, and which way. Account names or SteamID64s, separated by commas, each with :from (accept what they send), :to (let them take items) or :both - both is the default. Example: farm1, farm2:to. Empty = the accounts in \"Your own accounts\", both ways, when \"Accept anything from your own accounts\" is on. Donations follow their own switch. Anything else waits for you: trade accept or trade decline, in the console or on Telegram.",
			Placeholder: "farm1, farm2:to", Group: "Your own accounts"),
		new("DeclineOtherTrades", "Decline everything else", SecTrading, SettingKind.Bool,
			"Declines every other trade offer instead of leaving it. Off leaves unaccepted offers for you to check yourself.",
			Advanced: true, Group: "Offers from others"),
		new("OneTradeAtATime", "Answer one trade offer at a time", SecTrading, SettingKind.Bool,
			"Human mode only. When several trade offers are due at once, like in the morning, it answers them a few minutes apart instead of all together.",
			Advanced: true, Mode: "legit", Group: "Offers from others"),
		new("TradeDelayMinMinutes", "Wait at least", SecTrading, SettingKind.Int,
			"The shortest wait before answering a trade offer, in minutes. Accepting seconds after it arrives looks like a bot.",
			Min: 0, Max: 720, Advanced: true, Group: "Offers from others"),
		new("TradeDelayMaxMinutes", "And at most", SecTrading, SettingKind.Int,
			"The longest wait before answering a trade offer, in minutes. Each offer gets a random wait, usually closer to the short end.",
			Min: 0, Max: 1440, Advanced: true, Group: "Offers from others"),
		new("SendItemTypes", "What to send", SecTrading, SettingKind.Text,
			"Which items can leave this account, separated by commas: cards, foils, backgrounds, emoticons, boosters, gems - or all, for everything tradable in every game (TF2 and CS2 items too). Anything not listed is never sent.",
			Placeholder: "cards", Group: "Sending items"),
		new("SendOnFarmingFinished", "Send items when farming finishes", SecTrading, SettingKind.Bool,
			"When there are no cards left to farm, sends what this account collected to the first account in \"Your own accounts\".",
			Advanced: true, Group: "Sending items"),
		new("SendEveryHours", "Send items every", SecTrading, SettingKind.Int,
			"Sends this account's items to the first account in \"Your own accounts\" every this many hours, so cards don't pile up. The first send is one period after you turn it on, the account's card on the dashboard shows when the next one is, and each send is in the log. 0 turns it off.",
			Advanced: true, Min: 0, Max: 168, Group: "Sending items"),
		new("SendAroundHour", "...around", SecTrading, SettingKind.Hour,
			"The hour of the day to send at - early morning, say. It goes out at a random minute in that hour, on the days \"Send items every\" allows. No set time: every so many hours from when it was turned on.",
			Advanced: true, Min: -1, Max: 23, Group: "Sending items"),
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
		new("Rep4RepStartHour", "Only post from", SecComments, SettingKind.Hour,
			"The earliest hour of the day this account comments. Comments in the middle of the night are an easy bot giveaway.",
			Min: 0, Max: 23, Advanced: true),
		new("Rep4RepEndHour", "Until", SecComments, SettingKind.Hour,
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
			"Accepts incoming friend requests automatically. Handy on a rep4rep account, since Steam lets you comment much more freely on friends.", Group: "Friends and groups"),
		new("FriendRequestDelayMinMinutes", "Wait at least", SecSocial, SettingKind.Int,
			"The shortest wait before accepting a friend request, in minutes. Accepting the second it arrives is an easy bot giveaway.",
			Min: 0, Max: 720, Advanced: true, Group: "Friends and groups"),
		new("FriendRequestDelayMaxMinutes", "And at most", SecSocial, SettingKind.Int,
			"The longest wait before accepting a friend request, in minutes. Each request gets a random wait, usually closer to the short end.",
			Min: 0, Max: 1440, Advanced: true, Group: "Friends and groups"),
		new("AcceptGroupInvites", "Accept group invites", SecSocial, SettingKind.Bool,
			"Joins Steam groups this account gets invited to.",
			Advanced: true, Group: "Friends and groups"),
		new("JoinGroup", "Join the shared groups", SecSocial, SettingKind.Bool,
			"Joins the groups in \"Groups every account joins\". Turn it off to keep just this account out of them.",
			Advanced: true, Group: "Friends and groups"),
		new("ExtraGroupsToJoin", "Also join these groups", SecSocial, SettingKind.Text,
			"Steam groups only this account joins, as links or short names separated by commas. Groups in \"Groups every account joins\" are joined too.",
			Advanced: true, Placeholder: "steamcommunity.com/groups/yourgroup", Group: "Friends and groups"),
		new("IgnoreSuspiciousInvites", "Ignore obvious spam", SecSocial, SettingKind.Bool,
			"Quietly ignores friend requests from new level 0 private profiles, which is what scam bots look like. Only works when \"Accept friend requests\" is on.",
			Advanced: true, Group: "Friends and groups"),
		new("RejectInvalidFriendInvites", "Turn down what you don't accept", SecSocial, SettingKind.Bool,
			"Declines friend requests it doesn't accept instead of leaving them in the list. Off leaves them there for you to look at.",
			Advanced: true, Group: "Friends and groups"),
		new("AutoReplyEnabled", "Reply to messages", SecSocial, SettingKind.Bool,
			"Replies to people who message this account. Turning it off keeps your reply text saved for later.", Group: "Messages"),
		new("AutoReply", "Reply to messages with", SecSocial, SettingKind.Text,
			"The message sent back when someone messages this account. Leave empty to send nothing. A short afk line looks more normal than never answering.",
			Placeholder: "afk right now, I will get back to you", Group: "Messages"),
		new("AutoReplyOncePerDay", "Only reply once a day", SecSocial, SettingKind.Bool,
			"Sends the reply at most once a day to each person, so nobody gets the same line twice.", Advanced: true, Group: "Messages"),
		new("AutoReplyDelaySeconds", "Wait before replying", SecSocial, SettingKind.Int,
			"Seconds to wait before replying, so it doesn't answer instantly. The real wait is random, up to double this.",
			Advanced: true, Min: 0, Max: 600, Group: "Messages"),
		new("CommandMasters", "Accept commands from", SecSocial, SettingKind.Text,
			"SteamID64s allowed to control this account by messaging it, separated by commas - this account only, not the others or the app's settings. Commands start with a slash, so send /help to begin. Leave empty so nobody can.",
			Advanced: true, Placeholder: "76561198000000000", Group: "Commands"),
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
