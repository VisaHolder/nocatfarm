using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.7: each account's levelling over the week, for the weekly report - the XP it gained (and the level it went to), what
/// its own listings sold for, what buying cards spent, and when it reaches its next goal at this pace. reap: "how much $
/// would it take for me to get new to lvl 100" - now it says so every week.
/// </summary>
/// <remarks>config/state/levels-&lt;account&gt;.json: the Steam level and XP as last read each day, and each sale - 35 days kept.</remarks>
public static class LevelReport {
	public sealed record Xp(DateTime At, int Level, long Total);

	public sealed record Sale(DateTime At, long Cents, int Currency);

	public sealed record Book(List<Xp> Levels, List<Sale> Sales);

	private static readonly Lock Gate = new();
	private static readonly TimeSpan Keep = TimeSpan.FromDays(35);

	private static string PathOf(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"levels-{account}.json");

	public static Book Load(string account) {
		lock (Gate) {
			try {
				string path = PathOf(account);

				return (File.Exists(path) ? JsonSerializer.Deserialize<Book>(File.ReadAllText(path)) : null) ?? new Book([], []);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				return new Book([], []);
			}
		}
	}

	private static void Save(string account, Book book) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(PathOf(account))!);
			AtomicFile.Write(PathOf(account), JsonSerializer.Serialize(book));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("couldn't note the levelling figures: {0}", Log.Describe(e)), account);
		}
	}

	/// <summary>The Steam level and XP just read - kept once an hour at most (the newest of each hour wins).</summary>
	public static void NoteXp(string account, int level, long xp, DateTime now) {
		lock (Gate) {
			Book b = Load(account);

			if ((b.Levels.Count > 0) && (now - b.Levels[^1].At < TimeSpan.FromHours(1))) {
				if (b.Levels[^1].Total == xp) {
					return;
				}

				b.Levels.RemoveAt(b.Levels.Count - 1);
			}

			b.Levels.Add(new Xp(now, level, xp));
			b.Levels.RemoveAll(x => now - x.At > Keep);
			Save(account, b);
		}
	}

	/// <summary>A listing of nocat.farm's that sold, with what the account got.</summary>
	public static void NoteSale(string account, long cents, int currency, DateTime now) {
		if (cents <= 0) {
			return;
		}

		lock (Gate) {
			Book b = Load(account);
			b.Sales.Add(new Sale(now, cents, currency));
			b.Sales.RemoveAll(s => now - s.At > Keep);
			Save(account, b);
		}
	}

	/// <summary>The level to say the time to: "Stop buying at Steam level" when it's set and ahead, else 100, else the next hundred.</summary>
	public static int Goal(int level, int stopAt) => (stopAt > level) ? stopAt : (level < 100) ? 100 : ((level / 100) + 1) * 100;

	/// <summary>
	/// One account's week: "+1,200 XP (level 52 -> 54) · sold 0.92 CAD · spent 6.40 CAD · level 100 in about 9 weeks at this
	/// pace". Null when it didn't level, sell or buy - nothing to say. <paramref name="spent"/> is what buying cards spent in
	/// the week, in <paramref name="currency"/>.
	/// </summary>
	public static Said? Line(Book b, DateTime from, DateTime to, long spent, int currency, int stopAt) {
		Xp? start = b.Levels.Where(x => x.At <= from).LastOrDefault() ?? b.Levels.FirstOrDefault(x => x.At <= to);
		Xp? end = b.Levels.LastOrDefault(x => x.At <= to);
		long gained = (start != null) && (end != null) ? Math.Max(0, end.Total - start.Total) : 0;
		long sold = b.Sales.Where(s => (s.At > from) && (s.At <= to) && ((currency <= 0) || (s.Currency == currency))).Sum(static s => s.Cents);

		if ((gained == 0) && (sold == 0) && (spent <= 0)) {
			return null;
		}

		List<Said?> parts = [
			gained > 0 ? (start!.Level != end!.Level ? new Said("+{0} XP (level {1} -> {2})", gained.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), start.Level, end.Level)
				: new Said("+{0} XP (level {1})", gained.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), end.Level)) : null,
			sold > 0 ? new Said("sold {0}", BuyPlan.Money(sold, currency)) : null,
			spent > 0 ? new Said("spent {0}", BuyPlan.Money(spent, currency)) : null
		];

		// The time to the next goal at this week's pace - only with XP gained (no pace, no guess).
		if ((gained > 0) && (end != null)) {
			int goal = Goal(end.Level, stopAt);
			long left = Math.Max(0, BuyPlan.XpFor(goal) - end.Total);
			double weeks = (double) left / gained;
			parts.Add(left == 0 ? null : weeks < 1.5 ? new Said("level {0} in about {1} day(s) at this pace", goal, Math.Max(1, (int) Math.Ceiling(weeks * 7)))
				: new Said("level {0} in about {1} weeks at this pace", goal, (int) Math.Ceiling(weeks)));
		}

		return ReportCard.Join(parts);
	}
}
