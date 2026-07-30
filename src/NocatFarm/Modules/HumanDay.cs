using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Modules;

/// <summary>
/// Today's plan, kept on disk.
///
/// Without this, restarting nocatFarm re-rolls the whole day: a fresh target, a fresh wake and bed time, and -
/// worst of all - the minutes already played reset to zero. Restart three times and the account cheerfully
/// plays three full days' worth in one, which is exactly the thing human mode exists to avoid. It also means a
/// day rolled as a day off could turn into an eight-hour session simply because the app was restarted.
///
/// So the plan is written once when it is rolled and read back on start. Same calendar day, same plan, with the
/// minutes already banked still counted.
/// </summary>
public sealed class HumanDay {
	public int DayOfYear { get; set; } = -1;
	public int Year { get; set; }

	public int TargetMinutes { get; set; }
	public int PlayedMinutes { get; set; }
	public int MainSharePct { get; set; }
	public int OtherBudget { get; set; }
	public int OtherPlayed { get; set; }
	public int SignOutCap { get; set; }
	public int SignOutsUsed { get; set; }
	public int MealCap { get; set; }
	public int MealsUsed { get; set; }
	public int WakeMinuteOfDay { get; set; }
	public int BedHour { get; set; }
	public int BedMinute { get; set; }
	public bool BedIsTomorrow { get; set; }
	public uint LastGame { get; set; }

	/// <summary>Minutes per appID so far today, so the console's breakdown survives a restart too.</summary>
	public Dictionary<string, int> ByGame { get; set; } = [];

	public bool IsFor(DateTime when) => (DayOfYear == when.DayOfYear) && (Year == when.Year);

	private static string PathFor(string bot) => Path.Combine(ConfigStore.ConfigDir, "state", $"human-{bot}.json");

	public static HumanDay? Load(string bot, DateTime when) {
		try {
			string path = PathFor(bot);

			if (!File.Exists(path)) {
				return null;
			}

			HumanDay? day = JsonSerializer.Deserialize<HumanDay>(File.ReadAllText(path));

			// A plan from yesterday is not a plan, it is a leftover. Rolling a fresh one is correct.
			return day?.IsFor(when) == true ? day : null;
		} catch (Exception e) {
			Log.Debug($"couldn't read today's plan: {e.Message}", bot);

			return null;
		}
	}

	public void Save(string bot) {
		try {
			string path = PathFor(bot);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Debug($"couldn't save today's plan: {e.Message}", bot);
		}
	}
}
