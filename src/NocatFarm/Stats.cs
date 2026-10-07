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

	/// <summary>A card put up on the market - the weekly report counts them. A sale itself isn't something Steam tells us.</summary>
	public const string KindListed = "listed";

	/// <summary>An achievement unlocked - by the pacer, 'achievements ... unlock' or unlock-everything. The mini window can
	/// show how many today, and nothing else kept a count that outlives a restart.</summary>
	public const string KindAchievement = "achievement";

	public sealed record Event(DateTime When, string Kind, string Bot);

	private static readonly object Gate = new();
	private static readonly List<Event> Cache = [];
	private static bool _loaded;

	private static string PathFor() => Path.Combine(ConfigStore.Root, "logs", "stats.log");

	/// <summary>What "now" is for the reading side - the hour bars and the counts. Swappable so the checks can pin the time
	/// of day: the bars' labels hang on it, and a check on the real clock only met the first minute of an hour once in 60 runs.</summary>
	internal static Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

	/// <summary>The time zone the bars are labelled in - this computer's. Swappable so the checks can put a clock change in the
	/// window on any computer.</summary>
	internal static TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

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

	/// <summary>The same event several times over - "unlock all" sets a whole game's achievements in one go.</summary>
	public static void Record(string kind, string bot, int times) {
		for (int i = 0; i < times; i++) {
			Record(kind, bot);
		}
	}

	/// <summary>Events from the last <paramref name="hours"/> hours, oldest first.</summary>
	public static IReadOnlyList<Event> Recent(int hours) {
		DateTime cutoff = UtcNow().AddHours(-Math.Clamp(hours, 1, 24 * 90));

		lock (Gate) {
			EnsureLoaded();

			return Cache.Where(e => e.When >= cutoff).OrderBy(static e => e.When).ToArray();
		}
	}

	/// <summary>
	/// Per-hour counts for the last <paramref name="hours"/> hours, oldest bucket first - the hour now under way last, and
	/// first the part of an hour that the window starts in, so the bars add up to <see cref="Totals"/> for the same hours.
	/// Starting at the next full hour, the last minutes of the window's first hour were in the totals and in no bar. That
	/// first part-hour is given its real start, up to the minute (14:37, not 14:00) - as 14:00 it had the same time as the
	/// hour now under way.
	/// </summary>
	/// <remarks>
	/// The bars are real hours, stepped in UTC; only the times they're shown with are local. Stepped on the local clock, the
	/// day the clocks changed the bars ran an hour off the window: going back, the first bar was empty and the hour that
	/// came twice was one bar with both in it; going forward, the window's first minutes were in no bar at all.
	/// </remarks>
	public static List<(DateTime Hour, int Cards, int Comments)> ByHour(int hours) {
		hours = Math.Clamp(hours, 1, 24 * 14);
		TimeZoneInfo zone = Zone;
		DateTime now = UtcNow();
		DateTime from = now.AddHours(-hours);

		// When the hour now under way began on the local clock, in UTC - not always a whole UTC hour (India is 5:30 ahead).
		long intoHour = (now.Ticks + zone.GetUtcOffset(now).Ticks) % TimeSpan.TicksPerHour;
		DateTime end = new(now.Ticks - intoHour, DateTimeKind.Utc);
		List<(DateTime, int, int)> buckets = [];
		IReadOnlyList<Event> events = Recent(hours + 1);

		for (int i = hours; i >= 0; i--) {
			DateTime hour = end.AddHours(-i);
			DateTime next = hour.AddHours(1);

			int cards = 0;
			int comments = 0;

			foreach (Event e in events) {
				if ((e.When < hour) || (e.When >= next) || (e.When < from)) {
					continue;
				}

				if (e.Kind == KindCard) {
					cards++;
				} else if (e.Kind == KindComment) {
					comments++;
				}
			}

			DateTime shown = TimeZoneInfo.ConvertTimeFromUtc(hour, zone);
			buckets.Add(((i == hours) && (from > hour) ? StartLabel(TimeZoneInfo.ConvertTimeFromUtc(from, zone), shown.AddHours(1)) : shown, cards, comments));
		}

		return buckets;
	}

	/// <summary>
	/// The time the first part-hour bar is shown with, to the minute. Its start is rounded up to the next whole minute: the
	/// 'stats' text prints HH:mm, and at 22:00:30 the start read "22:00" - the same as the hour now under way. Not up into
	/// the next hour, though - 22:59:30 stays 22:59, as 23:00 is the bar after it.
	/// </summary>
	private static DateTime StartLabel(DateTime start, DateTime next) {
		DateTime minute = new(start.Ticks - (start.Ticks % TimeSpan.TicksPerMinute), start.Kind);
		DateTime up = minute < start ? minute.AddMinutes(1) : minute;

		return up < next ? up : minute;
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
