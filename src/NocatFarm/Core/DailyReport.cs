using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// A once-a-day, plain-language summary of what the whole fleet banked in the last 24 hours - hours played,
/// trading cards dropped, rep4rep comments, and a running lifetime total - written to the log/console at a time
/// you choose (default 09:30). The heartbeat tells you what each account is doing this minute; this is the one
/// line that answers "what did I actually get overnight?" without reading the whole log.
///
/// "Hours banked in the last 24h" is a delta: lifetime-now minus the lifetime we snapshotted at the last
/// report. So it counts only the time nocat.farm actually ran, and it survives a restart because the snapshot
/// is on disk. Every running game counts, the way Steam credits them: 32 games for an hour is 32 hours banked,
/// and "total" is everything nocat.farm has banked for the account, counted the same way. The day-plan line that human mode prints at midnight is a different thing entirely - that is the
/// PLAN for the coming day; this is the RESULT of the day that just passed.
/// </summary>
public static class DailyReport {
	private sealed class State {
		public string LastFired { get; set; } = "";   // yyyy-MM-dd, local time; "" = never fired
		public Dictionary<string, double> Lifetime { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Game-minutes at the last report - what "banked" counts from now. Missing in a report file from
		/// before game-hours were counted; that one report works it out from the clock time instead.</summary>
		public Dictionary<string, double>? Games { get; set; }
	}

	private static readonly Lock Gate = new();
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052", Justification = "Held, never read: a timer nothing holds on to is collected and stops firing.")]
	private static Timer? _timer;
	private static BotManager? _mgr;
	private static State _state = new();
	private static bool _loaded;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "report.json");

	public static void Start(BotManager mgr) {
		_mgr = mgr;
		Load();

		// Checked once a minute - cheap, and it honours the chosen time to within a minute of the clock without
		// caring what time the process happened to start. First check shortly after boot so a report that was due
		// while the machine was off still lands once (and once only) when it comes back.
		_timer = new Timer(static _ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
	}

	/// <summary>
	/// The same per-account rows the daily report writes, as text for the 'stats' command - empty when there is
	/// nothing to report on. Does not log anything, consume the day's scheduled report or move the 24h baseline.
	/// </summary>
	public static string Summary() {
		if (_mgr is not { } mgr) {
			return "";
		}

		Load();
		(List<Said> lines, Said fleet, bool first, _, _) = Build(mgr);

		if (lines.Count == 0) {
			return "";
		}

		Said header = first
			? new Said("── daily report · 24h · 'banked' starts counting now ──")
			: new Said("── daily report · last 24h ──");

		return string.Join(Environment.NewLine, lines.Prepend(header).Append(fleet).Select(static l => l.ToString()));
	}

	private static void Tick() {
		try {
			// Either one wants it: "Send the daily summary" is sent at this time even with the log's copy switched off -
			// it used to wait on that switch, and with it off nothing was ever sent.
			if (_mgr is not { } mgr || (!mgr.Global.DailyReportEnabled && !mgr.Global.SendDailySummary)) {
				return;
			}

			DateTime now = DateTime.Now;
			string today = now.ToString("yyyy-MM-dd");
			DateTime fireAt = now.Date
				.AddHours(Math.Clamp(mgr.Global.DailyReportHour, 0, 23))
				.AddMinutes(Math.Clamp(mgr.Global.DailyReportMinute, 0, 59));

			lock (Gate) {
				if (_state.LastFired == today || now < fireAt) {
					return;
				}
			}

			Fire(mgr, today, commit: true);
		} catch (Exception e) {
			// A timer callback, and a report that failed is tried again next minute: once (an hour apart at most), with its stack.
			if (Log.DebugOnChange("report:tick", $"daily report tick failed: {Log.Describe(e)}", "report")) {
				Log.StackToFile(e, "report");
			}
		}
	}

	private static bool Fire(BotManager mgr, string today, bool commit) {
		(List<Said> lines, Said fleet, bool first, Dictionary<string, double> snapshot, Dictionary<string, double> games) = Build(mgr);

		if (lines.Count == 0) {
			return false;
		}

		// In the log only when "Daily summary in the log" is on.
		if (mgr.Global.DailyReportEnabled) {
			Log.Good(first
				? new Said("── daily report · 24h · 'banked' starts counting now ──")
				: new Said("── daily report · last 24h ──"), "report");
			foreach (Said line in lines) {
				Log.Info(line, "report");
			}

			Log.Good(fleet, "report");
		}

		// The same summary as one message for Discord / Telegram, when that's switched on - only for the real
		// daily one.
		if (commit) {
			Log.Publish(Topic.Summary, "report", new Said(string.Join("\n", lines.Select(static l => l.ToString().TrimStart()).Append(fleet.ToString().TrimStart()))));

			lock (Gate) {
				_state.Lifetime = snapshot;
				_state.Games = games;
				_state.LastFired = today;
			}

			Save();
		}

		return true;
	}

	/// <summary>One row per account plus the fleet line, and the lifetime snapshots the next report counts from.</summary>
	private static (List<Said> Lines, Said Fleet, bool First, Dictionary<string, double> Snapshot, Dictionary<string, double> Games) Build(BotManager mgr) {
		List<Bot> bots = mgr.All.OrderBy(static b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

		bool r4r = mgr.Global.Rep4RepEnabled;
		Dictionary<string, int> cards = Count(Stats.KindCard);
		Dictionary<string, int> comments = Count(Stats.KindComment);

		Dictionary<string, double> prev;
		Dictionary<string, double>? prevGames;
		lock (Gate) {
			prev = new Dictionary<string, double>(_state.Lifetime, StringComparer.OrdinalIgnoreCase);
			prevGames = _state.Games is { } g ? new Dictionary<string, double>(g, StringComparer.OrdinalIgnoreCase) : null;
		}

		bool first = prev.Count == 0;
		Dictionary<string, double> snapshot = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, double> games = new(StringComparer.OrdinalIgnoreCase);
		int totBanked = 0, totCards = 0, totComments = 0, totLife = 0;
		List<Said> lines = [];

		foreach (Bot b in bots) {
			double clock = Lifetime.For(b.Name);
			double played = Lifetime.GamesFor(b.Name);
			snapshot[b.Name] = clock;
			games[b.Name] = played;
			double life = played;

			int banked = prevGames != null
				? prevGames.TryGetValue(b.Name, out double gamesBefore) ? (int) Math.Max(0, played - gamesBefore) : -1
				: prev.TryGetValue(b.Name, out double before) ? (int) (Math.Max(0, clock - before) * GamesPerHour(b.Name)) : -1;
			int c = cards.GetValueOrDefault(b.Name);
			int cm = comments.GetValueOrDefault(b.Name);

			totCards += c;
			totComments += cm;
			totLife += (int) life;
			if (banked > 0) {
				totBanked += banked;
			}

			// -1 = no baseline yet (first report for this account), shown as a dash rather than a fake "0m".
			string banked24 = banked < 0 ? "—" : Fmt.Hm(banked);
			Said row = r4r
				? new Said("  {0} banked {1} · {2} card(s) · {3} comment(s) · {4} total",
					b.Name.PadRight(14), banked24.PadRight(7), c, cm, Fmt.Hm((int) life))
				: new Said("  {0} banked {1} · {2} card(s) · {3} total",
					b.Name.PadRight(14), banked24.PadRight(7), c, Fmt.Hm((int) life));

			lines.Add(row);
		}

		Said fleet = r4r
			? new Said("  fleet: banked {0} · {1} card(s) · {2} comment(s) · {3} total",
				Fmt.Hm(totBanked), totCards, totComments, Fmt.Hm(totLife))
			: new Said("  fleet: banked {0} · {1} card(s) · {2} total",
				Fmt.Hm(totBanked), totCards, Fmt.Hm(totLife));

		return (lines, fleet, first, snapshot, games);

		// The one report after game-hours started being counted has only a clock-time baseline: the clock time since,
		// times how many games were on at once over the last two days of history.
		static double GamesPerHour(string bot) {
			(double clock, double banked) = History.Recent(2, bot);

			return clock > 0 ? Math.Max(1, banked / clock) : 1;
		}

		static Dictionary<string, int> Count(string kind) =>
			Stats.Recent(24)
				.Where(e => e.Kind == kind)
				.GroupBy(static e => e.Bot, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.OrdinalIgnoreCase);
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
				Log.Failed("couldn't read the daily-report state", e);
			}
		}
	}

	private static void Save() {
		try {
			State snap;
			lock (Gate) {
				snap = _state;
			}

			string path = Path;
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Failed("couldn't save the daily-report state", e);
		}
	}
}
