using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Once a week, each account's week next to the week before - hours banked, cards dropped, cards listed for sale,
/// what the inventory gained or lost, rep4rep comments - plus a line for the whole fleet. Off unless switched on.
/// </summary>
/// <remarks>
/// The daily summary answers "what did I get overnight"; one day on its own can't say whether things are going up or
/// down. Everything here comes from the day-by-day history (Core/History.cs), so it counts game-hours the same way
/// the charts and the daily summary do, and it survives restarts. The only thing that doesn't is "listed": Steam
/// never says when a listing sells, so it counts what was put up, from the stats log.
///
/// "This week" is the seven whole days before the day it's sent, so a Monday report is Monday to Sunday and doesn't
/// change with the hour it lands. When it was sent is kept on disk, so it's sent once a week - and a week the PC
/// was off for still gets its report when it comes back.
/// </remarks>
public static class WeeklyReport {
	private sealed class State {
		/// <summary>The date (yyyy-MM-dd, local) of the scheduled report last sent; "" = never.</summary>
		public string LastFired { get; set; } = "";
	}

	private static readonly Lock Gate = new();
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052", Justification = "Held, never read: a timer nothing holds on to is collected and stops firing.")]
	private static Timer? _timer;
	private static BotManager? _mgr;
	private static State _state = new();
	private static bool _loaded;

	internal static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "weekly-report.json");

	public static void Start(BotManager mgr) {
		_mgr = mgr;
		Load();
		_timer = new Timer(static _ => Tick(), null, TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(1));
	}

	/// <summary>The most recent moment the report was due, at or before <paramref name="now"/>.</summary>
	internal static DateTime LastDue(DateTime now, int day, int hour) {
		DateTime slot = now.Date.AddHours(Math.Clamp(hour, 0, 23));
		int back = ((int) now.DayOfWeek - Math.Clamp(day, 0, 6) + 7) % 7;
		slot = slot.AddDays(-back);

		return slot > now ? slot.AddDays(-7) : slot;
	}

	/// <summary>
	/// Whether the scheduled report should go now, and for which date. Never sent before: only when today is the day
	/// (switching it on mid-week doesn't send one straight away - 'report week' is there for that).
	/// </summary>
	internal static DateTime? Due(DateTime now, int day, int hour, string lastFired) {
		DateTime due = LastDue(now, day, hour);
		string key = Key(due);

		if (string.CompareOrdinal(lastFired, key) >= 0) {
			return null;
		}

		return (lastFired.Length == 0) && (due.Date != now.Date) ? null : due.Date;
	}

	private static void Tick() {
		try {
			if (_mgr is not { } mgr || !mgr.Global.WeeklyReport) {
				return;
			}

			string last;

			lock (Gate) {
				last = _state.LastFired;
			}

			DateTime now = DateTime.Now;

			if (Due(now, mgr.Global.WeeklyReportDay, mgr.Global.WeeklyReportHour, last) is not { } day) {
				// Remembered as seen, so switching it on today doesn't hold last week's slot open for ever.
				if (last.Length == 0) {
					Mark(LastDue(now, mgr.Global.WeeklyReportDay, mgr.Global.WeeklyReportHour).Date);
				}

				return;
			}

			(List<Said> lines, Said header) = Build(mgr.All, mgr.Global.Rep4RepEnabled, day);
			Mark(day);

			if (lines.Count == 0) {
				return;
			}

			Log.Good(header, "report");

			foreach (Said line in lines.SkipLast(1)) {
				Log.Info(line, "report");
			}

			Log.Good(lines[^1], "report");
			Log.Publish(Topic.Weekly, "report", new Said(string.Join("\n", lines.Select(static l => l.ToString().TrimStart()).Prepend(header.ToString()))));
		} catch (Exception e) {
			if (Log.DebugOnChange("weekly:tick", $"weekly report tick failed: {Log.Describe(e)}", "report")) {
				Log.StackToFile(e, "report");
			}
		}
	}

	/// <summary>The report for the seven days before today, for 'report week'. Sends nothing and moves nothing.</summary>
	public static string Text(BotManager mgr) {
		(List<Said> lines, Said header) = Build(mgr.All, mgr.Global.Rep4RepEnabled, DateTime.Now.Date);

		return lines.Count == 0
			? "No accounts yet - nothing to report."
			: string.Join(Environment.NewLine, lines.Prepend(header).Select(static l => l.ToString()));
	}

	/// <summary>One row per account and the fleet line last, for the seven days before <paramref name="today"/>.</summary>
	internal static (List<Said> Lines, Said Header) Build(IEnumerable<Bot> fleet, bool r4r, DateTime today) {
		DateTime thisWeek = today.Date.AddDays(-7);
		DateTime lastWeek = today.Date.AddDays(-14);
		Said header = new Said("── weekly report · {0} to {1} ──", Key(thisWeek), Key(today.Date.AddDays(-1)));

		// Listings live in the stats log (a sale is never reported by Steam) - counted by the local day they went up.
		List<Stats.Event> listed = [.. Stats.Recent(24 * 16).Where(static e => e.Kind == Stats.KindListed)];
		int Listed(string bot) => listed.Count(e => string.Equals(e.Bot, bot, StringComparison.OrdinalIgnoreCase)
			&& (e.When.ToLocalTime() >= thisWeek) && (e.When.ToLocalTime() < today.Date));

		List<Said> lines = [];
		double totNow = 0, totBefore = 0;
		int totCards = 0, totListed = 0, totComments = 0;
		decimal? totValue = null;
		bool valueGap = false, anyBefore = false;

		foreach (Bot b in fleet) {
			(double banked, int cards, int comments) = History.Totals(b.Name, thisWeek, 7);
			(double before, _, _) = History.Totals(b.Name, lastWeek, 7);

			// No hours on record before this week at all: a dash, like an unknown value - not "0m", which reads as
			// an account that did nothing.
			bool known = History.HoursBefore(b.Name, thisWeek);
			anyBefore |= known;
			string was = known ? Fmt.Hm((int) before) : "—";
			int sold = Listed(b.Name);
			decimal? end = History.ClosingValue(b.Name, today.Date.AddDays(-1));
			decimal? start = History.ClosingValue(b.Name, thisWeek.AddDays(-1));
			decimal? change = (end is { } e && start is { } s) ? e - s : null;

			totNow += banked;
			totBefore += before;
			totCards += cards;
			totListed += sold;
			totComments += comments;

			if (change is { } c) {
				totValue = (totValue ?? 0) + c;
			} else if (end != null) {
				valueGap = true;
			}

			lines.Add(r4r
				? new Said("  {0} {1} banked (last week {2}) · {3} card(s) · {4} listed · value {5} · {6} comment(s)",
					b.Name.PadRight(14), Fmt.Hm((int) banked).PadRight(8), was, cards, sold, Money(change), comments)
				: new Said("  {0} {1} banked (last week {2}) · {3} card(s) · {4} listed · value {5}",
					b.Name.PadRight(14), Fmt.Hm((int) banked).PadRight(8), was, cards, sold, Money(change)));
		}

		if (lines.Count == 0) {
			return (lines, header);
		}

		// A fleet total that silently left out an account whose change isn't known yet would read as the whole story.
		string fleetValue = Money(totValue) + (valueGap && (totValue != null) ? "*" : "");
		string fleetWas = anyBefore ? Fmt.Hm((int) totBefore) : "—";

		lines.Add(r4r
			? new Said("  fleet: {0} banked (last week {1}) · {2} card(s) · {3} listed · value {4} · {5} comment(s)",
				Fmt.Hm((int) totNow), fleetWas, totCards, totListed, fleetValue, totComments)
			: new Said("  fleet: {0} banked (last week {1}) · {2} card(s) · {3} listed · value {4}",
				Fmt.Hm((int) totNow), fleetWas, totCards, totListed, fleetValue));

		return (lines, header);
	}

	/// <summary>A day as the state file and the header write it: 2026-09-30.</summary>
	private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>"+$1.23", "-€0.40", or a dash when there's nothing to compare yet.</summary>
	internal static string Money(decimal? change) => change is not { } c ? "—"
		: (c < 0 ? "-" : "+") + PriceBook.Symbol + Math.Abs(c).ToString("0.00", CultureInfo.InvariantCulture);

	private static void Mark(DateTime day) {
		lock (Gate) {
			_state.LastFired = Key(day);
		}

		Save();
	}

	private static void Load() {
		lock (Gate) {
			if (_loaded) {
				return;
			}

			_loaded = true;

			try {
				if (File.Exists(Path)) {
					_state = JsonSerializer.Deserialize<State>(File.ReadAllText(Path)) ?? new State();
				}
			} catch (Exception e) {
				Log.Failed("couldn't read the weekly-report state", e);
			}
		}
	}

	/// <summary>Read the state file again - after a restore put a different one in its place.</summary>
	internal static void Reload() {
		lock (Gate) {
			_state = new State();
			_loaded = false;
		}

		Load();
	}

	private static void Save() {
		try {
			State snap;
			lock (Gate) {
				snap = _state;
			}

			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
			AtomicFile.Write(Path, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Failed("couldn't save the weekly-report state", e);
		}
	}
}
