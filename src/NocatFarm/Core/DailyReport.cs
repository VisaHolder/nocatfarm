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
/// is on disk. The day-plan line that human mode prints at midnight is a different thing entirely - that is the
/// PLAN for the coming day; this is the RESULT of the day that just passed.
/// </summary>
public static class DailyReport {
	private sealed class State {
		public string LastFired { get; set; } = "";   // yyyy-MM-dd, local time; "" = never fired
		public Dictionary<string, double> Lifetime { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}

	private static readonly Lock Gate = new();
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

	/// <summary>Write the report now regardless of the clock (the `report` command). Does not consume the day's
	/// scheduled report or move the 24h baseline - it just shows where things stand right now.</summary>
	public static string RunNow() {
		if (_mgr is not { } mgr) {
			return "not ready yet";
		}

		Load();

		return Fire(mgr, DateTime.Now.ToString("yyyy-MM-dd"), commit: false)
			? "daily report written to the log"
			: "no accounts to report on";
	}

	private static void Tick() {
		try {
			if (_mgr is not { } mgr || !mgr.Global.DailyReportEnabled) {
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
			Log.Debug(new Said("daily report tick failed: {0}: {1}", e.GetType().Name, e.Message));
		}
	}

	private static bool Fire(BotManager mgr, string today, bool commit) {
		List<Bot> bots = mgr.All.OrderBy(static b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

		if (bots.Count == 0) {
			return false;
		}

		bool r4r = mgr.Global.Rep4RepEnabled;
		Dictionary<string, int> cards = Count(Stats.KindCard);
		Dictionary<string, int> comments = Count(Stats.KindComment);

		Dictionary<string, double> prev;
		lock (Gate) {
			prev = new Dictionary<string, double>(_state.Lifetime, StringComparer.OrdinalIgnoreCase);
		}

		bool first = prev.Count == 0;
		Dictionary<string, double> snapshot = new(StringComparer.OrdinalIgnoreCase);
		int totBanked = 0, totCards = 0, totComments = 0, totLife = 0;
		List<Said> lines = [];

		foreach (Bot b in bots) {
			double life = Lifetime.For(b.Name);
			snapshot[b.Name] = life;

			int banked = prev.TryGetValue(b.Name, out double before) ? (int) Math.Max(0, life - before) : -1;
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

		Log.Good(new Said("── daily report · last 24h{0} ──", (first ? " · first one, 'banked' counts from here on" : "")), "report");
		foreach (Said line in lines) {
			Log.Info(line, "report");
		}

		Log.Good(r4r
			? new Said("  fleet: banked {0} · {1} card(s) · {2} comment(s) · {3} total",
				Fmt.Hm(totBanked), totCards, totComments, Fmt.Hm(totLife))
			: new Said("  fleet: banked {0} · {1} card(s) · {2} total",
				Fmt.Hm(totBanked), totCards, Fmt.Hm(totLife)), "report");

		if (commit) {
			lock (Gate) {
				_state.Lifetime = snapshot;
				_state.LastFired = today;
			}

			Save();
		}

		return true;

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
				Log.Debug(new Said("couldn't read the daily-report state: {0}: {1}", e.GetType().Name, e.Message));
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
			File.WriteAllText(path, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the daily-report state: {0}: {1}", e.GetType().Name, e.Message));
		}
	}
}
