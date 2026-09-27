using System.Globalization;

namespace NocatFarm.Modules;

/// <summary>
/// "Get this game to 100 hours by the 1st of December" - hour targets a human-mode account works towards inside its
/// ordinary day, instead of a booster that runs one game flat out until a counter trips.
/// </summary>
/// <remarks>
/// Written as <c>730:100@2026-12-01, 440:50</c>: a game, the hours it should reach, and optionally the date. Human mode
/// leans its sittings towards a target game - harder the further a dated target has fallen behind - and a dated one
/// that can't make it on day play alone joins the overnight games, if night banking is on. Once a target is reached
/// it simply stops being favoured.
/// </remarks>
public static class HourTargets {
	/// <summary>One target: the game, the hours it should reach, and the day to reach them by, if there is one.</summary>
	public sealed record Target(uint AppId, int Hours, DateTime? By);

	public static List<Target> Parse(string spec) {
		List<Target> targets = [];

		foreach (string entry in (spec ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			string[] at = entry.Split('@', 2, StringSplitOptions.TrimEntries);
			string[] halves = at[0].Split(':', 2, StringSplitOptions.TrimEntries);

			if ((halves.Length != 2) || !uint.TryParse(halves[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint app) || (app == 0)
				|| !int.TryParse(halves[1], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) || (hours <= 0)) {
				continue;
			}

			DateTime? by = (at.Length == 2) && DateTime.TryParseExact(at[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime day)
				? day.Date.AddDays(1)   // "by the 1st" means by the end of the 1st
				: null;

			if (targets.All(t => t.AppId != app)) {
				targets.Add(new Target(app, hours, by));
			}
		}

		return targets;
	}

	/// <summary>Where a target stands, from the account's own playtime.</summary>
	public sealed record Progress(Target Target, int MinutesNow, int MinutesLeft, double DailyMinutesNeeded, bool Done, bool Late);

	public static Progress Of(Target target, int minutesNow, DateTime now) {
		int left = Math.Max(0, (target.Hours * 60) - minutesNow);
		double days = target.By is { } by ? Math.Max(0.25, (by - now).TotalDays) : 0;
		double perDay = days > 0 ? left / days : 0;

		return new Progress(target, minutesNow, left, perDay, left == 0, (target.By is { } deadline) && (now >= deadline) && (left > 0));
	}
}
