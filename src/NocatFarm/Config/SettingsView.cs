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

	/// <summary>The words a unit can be - each one translated by the page.</summary>
	public static readonly string[] UnitWords = ["minutes", "hours", "seconds", "days", "%", "ms", "wallet currency", "points", "backups", "reviews", "times a day"];

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
}
