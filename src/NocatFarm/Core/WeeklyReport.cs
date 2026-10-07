using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Once a week, each account's week next to the week before - hours played, cards dropped, cards listed for sale,
/// what the inventory gained or lost, rep4rep comments - plus a line for all of them. Off unless switched on. Laid
/// out like the daily summary (a ReportCard): a part that is nothing is left out, and an account that did nothing
/// all week says "off".
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

			ReportCard? card = Build(mgr.All, mgr.Global.Rep4RepEnabled, day, Attention(mgr));
			Mark(day);

			if (card == null) {
				return;
			}

			card.WriteToLog("report");
			Log.Publish(Topic.Weekly, "report", new Said("{0}", card.Wire()));
		} catch (Exception e) {
			if (Log.DebugOnChange("weekly:tick", $"weekly report tick failed: {Log.Describe(e)}", "report")) {
				Log.StackToFile(e, "report");
			}
		}
	}

	/// <summary>The report for the seven days before today, for 'report week'. Sends nothing and moves nothing.</summary>
	public static string Text(BotManager mgr) =>
		Build(mgr.All, mgr.Global.Rep4RepEnabled, DateTime.Now.Date, Attention(mgr))?.Text() ?? "No accounts yet - nothing to report.";

	/// <summary>What wants a look right now. No new-ban line: there's no "since last week" kept for bans - the daily summary has it.</summary>
	private static List<ReportCard.Row> Attention(BotManager mgr) => ReportCard.Attention(mgr.All, mgr.Global.Rep4RepEnabled, null, DateTime.UtcNow);

	/// <summary>One account's week.</summary>
	/// <param name="Before">The week before's game-minutes - null when nothing was recorded before this week at all (hours
	/// only went into the history from 1.5.5 on), so the comparison is left out rather than reading "last week 0h".</param>
	/// <param name="Change">What the inventory gained or lost - null when there's nothing to compare yet.</param>
	/// <param name="ValueGap">It has a value now but none from before the week to compare with - so the total of the
	/// changes leaves it out, and says so.</param>
	public sealed record Week(string Name, double Minutes, double? Before, int Cards, int Listed, int Comments, decimal? Change, bool ValueGap = false);

	/// <summary>
	/// The card for the seven days before <paramref name="today"/>, from the history: a line per account, "All accounts",
	/// what wants a look, and the app itself. Null when there are no accounts.
	/// </summary>
	internal static ReportCard? Build(IEnumerable<Bot> fleet, bool r4r, DateTime today, List<ReportCard.Row>? attention = null) {
		DateTime thisWeek = today.Date.AddDays(-7);
		DateTime lastWeek = today.Date.AddDays(-14);

		// Listings live in the stats log (a sale is never reported by Steam) - counted by the local day they went up.
		List<Stats.Event> listed = [.. Stats.Recent(24 * 16).Where(static e => e.Kind == Stats.KindListed)];
		int Listed(string bot) => listed.Count(e => string.Equals(e.Bot, bot, StringComparison.OrdinalIgnoreCase)
			&& (e.When.ToLocalTime() >= thisWeek) && (e.When.ToLocalTime() < today.Date));

		List<Week> weeks = [];

		foreach (Bot b in fleet) {
			(double played, int cards, int comments) = History.Totals(b.Name, thisWeek, 7);
			(double before, _, _) = History.Totals(b.Name, lastWeek, 7);
			decimal? end = History.ClosingValue(b.Name, today.Date.AddDays(-1));
			decimal? start = History.ClosingValue(b.Name, thisWeek.AddDays(-1));

			weeks.Add(new Week(b.Name, played, History.HoursBefore(b.Name, thisWeek) ? before : null, cards, Listed(b.Name), comments,
				(end is { } e && start is { } s) ? e - s : null, (end != null) && (start == null)));
		}

		return weeks.Count == 0 ? null : Card(weeks, r4r, thisWeek, today.Date.AddDays(-1), attention ?? [], DateTime.Now);
	}

	/// <summary>The weekly card from plain figures, accounts in the order given - separate so the checks can hand it figures.</summary>
	public static ReportCard Card(IReadOnlyList<Week> weeks, bool r4r, DateTime first, DateTime last, List<ReportCard.Row> attention, DateTime local) {
		List<ReportCard.Row> rows = [.. weeks.Select(w => Line(new Said("{0}", w.Name), w, partial: false))];

		// A total that silently left out an account whose change isn't known yet would read as the whole story - so it
		// says "(partial)" when it did.
		decimal? value = weeks.Any(static w => w.Change != null) ? weeks.Sum(static w => w.Change ?? 0) : null;
		bool gap = (value != null) && weeks.Any(static w => w.ValueGap);
		Week all = new("", weeks.Sum(static w => w.Minutes), weeks.Any(static w => w.Before != null) ? weeks.Sum(static w => w.Before ?? 0) : null,
			weeks.Sum(static w => w.Cards), weeks.Sum(static w => w.Listed), weeks.Sum(static w => w.Comments), value);
		rows.Add(Line(new Said("All accounts"), all, gap));

		// What needs a look first, as in the daily summary: a big fleet's list can't push it off the end of the message.
		List<ReportCard.Section> sections = [];

		if (attention.Count > 0) {
			sections.Add(new(new Said("Attention"), default, attention));
		}

		sections.Add(new(new Said("This week"), default, rows));
		sections.Add(ReportCard.System());

		return new ReportCard {
			Title = new Said("Weekly report"),
			Date = new Said("{0} to {1}", ReportCard.Day(first), ReportCard.Day(last)),
			Time = ReportCard.Clock(local),
			Sections = sections
		};

		// "1876h played (last week 1840h) · 3 cards · 2 listed · inventory +$1.23 · 50 comments", each part only when
		// it's something; nothing at all all week is "off" - "off (last week 100h)" when it did play the week before, which
		// is exactly the change worth seeing. Said as plain "off", an account that stopped dead lost its comparison.
		ReportCard.Row Line(Said label, Week w, bool partial) {
			string hours = ReportCard.Hours(w.Minutes);
			string before = w.Before is { } bm ? ReportCard.Hours(bm) : "";
			Said? inventory = w.Change is not { } c || (c == 0) ? null
				: partial ? new Said("inventory {0} (partial)", Money(c))
				: new Said("inventory {0}", Money(c));
			List<Said?> others = [
				ReportCard.Cards(w.Cards),
				w.Listed > 0 ? new Said("{0} listed", w.Listed) : null,
				inventory,
				r4r ? ReportCard.Comments(w.Comments) : null
			];
			bool anyOther = others.Any(static p => p is { IsEmpty: false });
			Said? played = hours.Length > 0
				? w.Before != null ? new Said("{0} played (last week {1})", hours, before.Length > 0 ? before : "0h") : new Said("{0} played", hours)
				// No hours this week, some the week before: kept beside the other figures as 0h, or said with "off".
				: (before.Length > 0) && anyOther ? new Said("{0} played (last week {1})", "0h", before)
				: null;
			Said none = before.Length > 0 ? new Said("off (last week {0})", before) : new Said("off");

			return ReportCard.Figures(label, [played, .. others], none);
		}
	}

	/// <summary>A day as the state file writes it: 2026-09-30.</summary>
	private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>"+$1.23", "-€0.40", or a dash when there's nothing to compare yet.</summary>
	internal static string Money(decimal? change) => change is not { } c ? "—" : PriceBook.Signed(c);

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
