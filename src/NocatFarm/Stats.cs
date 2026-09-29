using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm;

/// <summary>
/// A running tally of what was actually earned, so the dashboard can answer "what did it do overnight?" -
/// the one question an idler exists to answer and that no idler's UI ever shows.
///
/// Append-only text, one short line per event. Not a database, not JSON per line, because this file is written
/// from several accounts at once and has to survive being killed mid-write: a torn last line loses one card,
/// not the file.
/// </summary>
public static class Stats {
	public const string KindCard = "card";
	public const string KindComment = "comment";
	public const string KindBadge = "badge";

	public sealed record Event(DateTime When, string Kind, string Bot);

	private static readonly object Gate = new();
	private static readonly List<Event> Cache = [];
	private static bool _loaded;

	private static string PathFor() => Path.Combine(ConfigStore.Root, "logs", "stats.log");

	public static void Record(string kind, string bot) {
		DateTime now = DateTime.UtcNow;

		// The day-by-day totals behind the dashboard's history charts. Before this event joins the cache, and outside
		// the lock: History fills itself in from this cache the first time it loads, so the other order could count
		// this one card twice, and it takes this lock while it does.
		History.Record(kind, bot, now);

		lock (Gate) {
			EnsureLoaded();
			Cache.Add(new Event(now, kind, bot));

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(PathFor())!);
				File.AppendAllText(PathFor(), $"{now.Ticks}|{kind}|{bot}{Environment.NewLine}");
			} catch (Exception e) {
				// the in-memory tally still works; a stats file is not worth an exception - but a line, said once
				Log.DebugOnChange("stats:write", $"couldn't add a {kind} to stats.log: {Log.Describe(e)}", bot);
			}
		}
	}

	/// <summary>Events from the last <paramref name="hours"/> hours, oldest first.</summary>
	public static IReadOnlyList<Event> Recent(int hours) {
		DateTime cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 90));

		lock (Gate) {
			EnsureLoaded();

			return Cache.Where(e => e.When >= cutoff).OrderBy(static e => e.When).ToArray();
		}
	}

	/// <summary>Per-hour counts for the last <paramref name="hours"/> hours, oldest bucket first.</summary>
	public static List<(DateTime Hour, int Cards, int Comments)> ByHour(int hours) {
		hours = Math.Clamp(hours, 1, 24 * 14);
		DateTime end = new(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, 0, 0, DateTimeKind.Local);
		List<(DateTime, int, int)> buckets = [];
		IReadOnlyList<Event> events = Recent(hours + 1);

		for (int i = hours - 1; i >= 0; i--) {
			DateTime hour = end.AddHours(-i);
			DateTime next = hour.AddHours(1);

			int cards = 0;
			int comments = 0;

			foreach (Event e in events) {
				DateTime local = e.When.ToLocalTime();

				if ((local < hour) || (local >= next)) {
					continue;
				}

				if (e.Kind == KindCard) {
					cards++;
				} else if (e.Kind == KindComment) {
					comments++;
				}
			}

			buckets.Add((hour, cards, comments));
		}

		return buckets;
	}

	public static (int Cards, int Comments) Totals(int hours) {
		IReadOnlyList<Event> events = Recent(hours);

		return (events.Count(static e => e.Kind == KindCard), events.Count(static e => e.Kind == KindComment));
	}

	private static void EnsureLoaded() {
		if (_loaded) {
			return;
		}

		_loaded = true;

		try {
			if (!File.Exists(PathFor())) {
				return;
			}

			DateTime cutoff = DateTime.UtcNow.AddDays(-90);
			string[] lines = File.ReadAllLines(PathFor());

			foreach (string line in lines) {
				string[] parts = line.Split('|');

				if ((parts.Length < 3) || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
					|| (ticks < DateTime.MinValue.Ticks) || (ticks > DateTime.MaxValue.Ticks)) {
					continue;   // a torn line from a kill mid-write - skip it, don't fail the load (a garbled number threw, and emptied the chart)
				}

				DateTime when = new(ticks, DateTimeKind.Utc);

				if (when >= cutoff && when <= DateTime.UtcNow) {
					Cache.Add(new Event(when, parts[1], parts[2]));
				}
			}

			// Only the 90 days anything reads are kept. The file was appended to for ever and read whole at every start,
			// and the log clean-up only looks at the daily log files.
			if (Cache.Count < lines.Length) {
				AtomicFile.Write(PathFor(), string.Concat(Cache.Select(static e => $"{e.When.Ticks}|{e.Kind}|{e.Bot}{Environment.NewLine}")));
			}
		} catch (Exception e) {
			// an unreadable stats file means an empty chart, nothing worse
			Log.Failed("couldn't read stats.log", e);
		}
	}
}
