namespace NocatFarm.Config;

/// <summary>
/// How the dashboard draws the settings (1.8.2): which pairs share one row, the unit after a box, and what a fresh account's
/// basic view holds. The page reads the pairs and units from the schema; the basic view here is the same rules, for the
/// checks and for anyone asking "how many settings does a new account show?".
/// </summary>
public static partial class Settings {
	/// <summary>
	/// Every "shortest / longest" pair the dashboard draws as one row - "Breaks: [2] to [30] minutes". The Min/Max pairs
	/// (<see cref="RangePairs"/>), and the few named another way: a window of hours (from/until, start/end, which may run past
	/// midnight, so they're never put in order) and the percent where the hunt moves on. The first of each has the row's
	/// label (<see cref="SettingDef.Range"/>).
	/// </summary>
	/// <remarks>Worked out on first use: across this class's files the order its statics start in isn't set, and the
	/// Min/Max pairs come from the lists.</remarks>
	public static IReadOnlyList<(string Min, string Max)> RowPairs => _rowPairs ??= [
		.. RangePairs,
		("FarmFromHour", "FarmUntilHour"),
		("AutoUpdateFromHour", "AutoUpdateUntilHour"),
		("Rep4RepStartHour", "Rep4RepEndHour"),
		("BoostSwitchFromPct", "BoostSwitchToPct")
	];

	private static List<(string Min, string Max)>? _rowPairs;

	/// <summary>
	/// Switches that answer one question, drawn as one row of chips under its title - "What counts as spare": [extra card
	/// copies] [cards of finished badges] [foils] [backgrounds] [emoticons]. Each stays its own setting; the first one's
	/// place in the list is the row's.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string[]> ChipRows = new Dictionary<string, string[]> {
		["What counts as spare"] = ["SpareExtraCards", "SpareMaxedBadgeCards", "SpareFoils", "SpareBackgrounds", "SpareEmoticons"],
		// 1.8.2: which kinds of line the window and the dashboard's Log tab show - the log file keeps them all (Core/LogShow.cs).
		["Show in the app's log"] = Core.LogShow.All
	};

	/// <summary>
	/// Global settings drawn by their section's own panel instead of as rows: the Discord profile card's switch and chips,
	/// and the kinds of message as chips under Notifications.
	/// </summary>
	public static readonly string[] PanelRows = ["DiscordPresence", "DiscordSecondLine", "DiscordShowCounter", "DiscordShowAvatar", "DiscordShowTimer",
		"SendCardDrops", "SendFreeStuff", "SendTrades", "SendProblems", "SendUpdates", "SendInstalls", "SendDailySummary", "SendComments",
		"SendAchievements", "SendRep4Rep", "SendSignIns", "SendBreakIns", "SendPoints", "SendItems", "SendScamWarnings", "SendHealthAlerts"];

	/// <summary>The words a unit can be - each one translated by the page.</summary>
	public static readonly string[] UnitWords = ["minutes", "hours", "seconds", "days", "%", "ms", "wallet currency", "points", "backups", "reviews", "times a day", "sets"];

	/// <summary>
	/// What a number setting is counted in: its own <see cref="SettingDef.Unit"/>, or worked out from the name's ending -
	/// BreakMinMinutes is minutes, DayOffChancePct is %, BoostRestDaysMax is days. Null for anything that isn't a number, or
	/// has none ("Hours on a weekday" says it already).
	/// </summary>
	public static string? UnitOf(SettingDef def) {
		if (def.Kind is not (SettingKind.Int or SettingKind.Float)) {
			return null;
		}

		if (def.Unit != null) {
			return def.Unit.Length == 0 ? null : def.Unit;
		}

		string n = def.Name;

		return n.EndsWith("Minutes", StringComparison.Ordinal) ? "minutes"
			: n.EndsWith("Hours", StringComparison.Ordinal) ? "hours"
			: n.EndsWith("Seconds", StringComparison.Ordinal) ? "seconds"
			: n.EndsWith("Days", StringComparison.Ordinal) || n.EndsWith("DaysMin", StringComparison.Ordinal) || n.EndsWith("DaysMax", StringComparison.Ordinal) ? "days"
			: n.EndsWith("Pct", StringComparison.Ordinal) ? "%"
			: n.EndsWith("Ms", StringComparison.Ordinal) ? "ms"
			: null;
	}

	/// <summary>Each setting's unit by name, for the page - only the ones that have one.</summary>
	public static Dictionary<string, string> Units() {
		Dictionary<string, string> units = [];

		foreach (SettingDef def in Global.Concat(Bot)) {
			if (UnitOf(def) is { } unit) {
				units[def.Name] = unit;
			}
		}

		return units;
	}

	/// <summary>One row the settings page draws: the setting it's drawn from, and its section and part.</summary>
	public sealed record ViewRow(string Name, string Section, string? Part);

	/// <summary>
	/// What the settings page shows with "Show advanced" off, in order: the rules the page uses (app.js renderSettings), so a
	/// count here is what someone sees. Hidden and advanced settings left out, each mode's own, only what its switches show
	/// (ShowWhen), a pair of shortest / longest or a row of chips as one row, the panels' chips not as rows, rep4rep's only
	/// while it's on.
	/// </summary>
	/// <param name="config">The account's settings, or the global ones for <paramref name="global"/>.</param>
	/// <param name="rep4rep">Whether rep4rep is on (the global "Use rep4rep").</param>
	public static List<ViewRow> BasicView(bool global, object config, bool rep4rep) {
		IReadOnlyList<SettingDef> defs = global ? Global : Bot;
		bool legit = !global && (config is BotConfig { LegitMode: true });
		HashSet<string> longest = [.. RowPairs.Select(static p => p.Max)];
		HashSet<string> chipRest = [.. ChipRows.Values.SelectMany(static names => names.Skip(1))];
		List<ViewRow> rows = [];

		foreach (SettingDef d in defs) {
			if (d.Hidden || d.Advanced || longest.Contains(d.Name) || chipRest.Contains(d.Name)) {
				continue;
			}

			if (!rep4rep && ((d.Section == SecRep4RepAccount) || (d.Part == SecComments)) && (d.Name != "Rep4RepEnabled")) {
				continue;
			}

			if ((d.Mode == "rage" && legit) || (d.Mode == "legit" && !legit) || (global && PanelRows.Contains(d.Name))) {
				continue;
			}

			if (!Shown(d, defs, config, 0)) {
				continue;
			}

			rows.Add(new ViewRow(d.Name, d.Section, d.Part));
		}

		return rows;
	}

	/// <summary>A setting's ShowWhen against the values in <paramref name="config"/>, and the one it names in turn.</summary>
	private static bool Shown(SettingDef def, IReadOnlyList<SettingDef> defs, object config, int depth) {
		if (def.ShowWhen is not { } when) {
			return true;
		}

		int at = when.IndexOf('=');
		bool not = (at > 0) && (when[at - 1] == '!');
		string owner = when[..(not ? at - 1 : at)];
		string[] wanted = when[(at + 1)..].Split('|');
		string value = Read(config, owner) switch {
			bool b => b ? "true" : "false",
			null => "",
			object o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? ""
		};

		if (wanted.Contains(value) == not) {
			return false;
		}

		SettingDef? parent = defs.FirstOrDefault(d => d.Name == owner);

		return (parent == null) || (depth > 8) || Shown(parent, defs, config, depth + 1);
	}
}
