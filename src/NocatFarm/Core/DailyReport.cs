using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// A once-a-day, plain-language summary of what every account got in the last 24 hours - hours played, trading
/// cards dropped, rep4rep comments - plus anything that wants a look, sent to Discord/Telegram and written to the
/// log at a time you choose (default 09:30). The heartbeat tells you what each account is doing this minute; this
/// answers "what did I actually get overnight?" without reading the whole log. Laid out as a ReportCard.
///
/// "Played" is a delta: game-minutes now minus the game-minutes snapshotted at the last summary. So it counts only
/// the time nocat.farm actually ran games, and it survives a restart because the snapshot is on disk. Every
/// running game counts, the way Steam credits playtime: 32 games for an hour is 32 hours played - the same hours
/// that land on the account's Steam profile, which is why the word is "played" (it used to say "banked") and why a
/// day can show far more than 24h. The lifetime total is left out of the message and kept for 'report' and 'stats',
/// where somebody is asking for it. The day-plan line that human mode prints at midnight is a different thing
/// entirely - that is the PLAN for the coming day; this is the RESULT of the day that just passed.
/// </summary>
public static class DailyReport {
	private sealed class State {
		public string LastFired { get; set; } = "";   // yyyy-MM-dd, local time; "" = never fired

		/// <summary>When the last one went out (UTC) - what "banked" counts from. Missing in a file from before 1.6.2.</summary>
		public DateTime? LastAt { get; set; }
		public Dictionary<string, double> Lifetime { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Game-minutes at the last report - what "banked" counts from now. Missing in a report file from
		/// before game-hours were counted; that one report works it out from the clock time instead.</summary>
		public Dictionary<string, double>? Games { get; set; }

		/// <summary>The most bans each account has been seen with (only accounts the ban watch has looked at), so a new one
		/// is flagged once, the next morning. Missing in a file from before this was kept: that one report flags none.</summary>
		public Dictionary<string, int>? Bans { get; set; }

		/// <summary>
		/// 2: Bans holds only accounts the ban watch had looked at. Missing in a file from before: those wrote 0 for an
		/// account never looked at too, so a 0 there is read as not known (see <see cref="BansBefore"/>).
		/// </summary>
		public int? BansFormat { get; set; }
	}

	/// <summary>What <see cref="State.BansFormat"/> is written as now.</summary>
	private const int BansFormatNow = 2;

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
	/// The daily summary as text, for the 'report' and 'stats' commands - with each account's lifetime total, as these
	/// are where somebody asks for it. Empty when there is nothing to report on. Does not log anything, consume the
	/// day's scheduled report or move the 24h baseline.
	/// </summary>
	public static string Summary() {
		if (_mgr is not { } mgr) {
			return "";
		}

		Load();
		Built built = Build(mgr, withTotal: true, DateTime.UtcNow);

		return built.Empty ? "" : built.Card.Text();
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
		Built built = Build(mgr, withTotal: false, DateTime.UtcNow);

		if (built.Empty) {
			return false;
		}

		// In the log only when "Daily summary in the log" is on.
		if (mgr.Global.DailyReportEnabled) {
			built.Card.WriteToLog("report");
		}

		// The same summary as one message for Discord / Telegram, when that's switched on - only for the real
		// daily one.
		if (commit) {
			Log.Publish(Topic.Summary, "report", new Said("{0}", built.Card.Wire()));

			lock (Gate) {
				_state.Lifetime = built.Snapshot;
				_state.Games = built.Games;
				_state.Bans = built.Bans;
				_state.BansFormat = BansFormatNow;
				_state.LastFired = today;
				_state.LastAt = DateTime.UtcNow;
			}

			Save();
		}

		return true;
	}

	/// <summary>
	/// What the numbers cover: "last 24h" only when the last summary really was a day ago. Asked for at 09:47 with the
	/// summary sent at 06:00, 'report' said "last 24h" over 3 hours and 47 minutes of hours - a third of what a day had.
	/// It goes beside the heading: "// ACCOUNTS · last 24h".
	/// </summary>
	public static Said Note(bool first, DateTime nowUtc) {
		if (first) {
			return new Said("counting starts now");
		}

		DateTime? at;
		string day;

		lock (Gate) {
			at = _state.LastAt;
			day = _state.LastFired;
		}

		// A file from before LastAt: the day it went out, at the time it's set for.
		if ((at == null) && (_mgr is { } mgr) && DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.None, out DateTime fired)) {
			at = fired.AddHours(mgr.Global.DailyReportHour).AddMinutes(mgr.Global.DailyReportMinute).ToUniversalTime();
		}

		if (at is not { } last) {
			return new Said("since the last daily summary");
		}

		TimeSpan gap = nowUtc - last;

		if ((gap > TimeSpan.FromHours(23.5)) && (gap < TimeSpan.FromHours(24.5))) {
			return new Said("last 24h");
		}

		DateTime local = last.ToLocalTime();
		string when = local.Date == nowUtc.ToLocalTime().Date ? local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
			: local.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

		return new Said("since the daily summary at {0} ({1})", when, Fmt.Hm((int) gap.TotalMinutes));
	}

	/// <summary>One account's day as the summary counts it.</summary>
	/// <param name="Minutes">Game-minutes played since the last summary - null for an account with nothing to count
	/// from yet (the very first summary), which is "not counted yet" rather than a made-up "off".</param>
	/// <param name="Total">Every game-minute nocat.farm has put on it - shown only where someone asks ('report', 'stats').</param>
	public sealed record Day(string Name, double? Minutes, int Cards, int Comments, double Total);

	/// <summary>The card, and what the next summary counts from - kept only when the real one goes out.</summary>
	private sealed record Built(ReportCard Card, bool Empty, Dictionary<string, double> Snapshot, Dictionary<string, double> Games, Dictionary<string, int> Bans);

	/// <summary>
	/// The summary from plain figures: what needs a look when anything does, then the accounts in the order they were
	/// given (the dashboard's) - a line each, then "All accounts" - then the app itself. Separate from the counting, so
	/// the checks can hand it figures.
	/// </summary>
	public static ReportCard Card(IReadOnlyList<Day> days, bool r4r, Said note, bool withTotal, List<ReportCard.Row> attention, DateTime local) {
		List<ReportCard.Row> rows = [.. days.Select(d => Line(new Said("{0}", d.Name), d.Minutes, d.Cards, d.Comments, withTotal ? d.Total : null))];

		// Not counted for any of them (the first summary) stays "not counted yet"; otherwise the ones that were counted.
		double? played = days.Any(static d => d.Minutes != null) ? days.Sum(static d => d.Minutes ?? 0) : null;
		rows.Add(Line(new Said("All accounts"), played, days.Sum(static d => d.Cards), days.Sum(static d => d.Comments), withTotal ? days.Sum(static d => d.Total) : null));

		// What needs a look first: after the accounts, a big fleet's list ran it off the end of the message.
		List<ReportCard.Section> sections = [];

		if (attention.Count > 0) {
			sections.Add(new(new Said("Attention"), default, attention));
		}

		sections.Add(new(new Said("Accounts"), note, rows));
		sections.Add(ReportCard.System());

		return new ReportCard {
			Title = new Said("Daily summary"),
			Date = new Said("{0}", ReportCard.Day(local)),
			Time = ReportCard.Clock(local),
			Sections = sections
		};

		// "268h played · 3 cards · 8 comments" - each part only when it's something. Nothing at all is "off": no game
		// ran for it and it earned nothing, which is what an account that was switched off all day looks like.
		ReportCard.Row Line(Said label, double? minutes, int cards, int comments, double? total) {
			Said? figures = ReportCard.Join([
				(minutes is { } m) && (ReportCard.Hours(m) is { Length: > 0 } h) ? new Said("{0} played", h) : null,
				ReportCard.Cards(cards),
				r4r ? ReportCard.Comments(comments) : null
			]);
			Said none = minutes == null ? new Said("not counted yet") : new Said("off");

			if (total is not { } life) {
				return figures is { } f ? new ReportCard.Row(label, f) : new ReportCard.Row(label, none, Strong: false);
			}

			Said lifetime = new Said("{0} total", ReportCard.Hours(life) is { Length: > 0 } lh ? lh : "0m");

			return new ReportCard.Row(label, ReportCard.Join([figures ?? none, lifetime])!.Value, Strong: figures != null);
		}
	}

	/// <summary>
	/// The bans an account has on record, as one number - null when the ban watch hasn't looked yet. Read as 0 then, an
	/// account added yesterday (or one whose watch hadn't run) had every ban it already had called new the first morning
	/// after its first look.
	/// </summary>
	private static int? BanCount(Bot b) => BotManager.ModuleOf<Modules.BanWatch>(b) is { CheckedAt: not null, Last: { } bans }
		? bans.Vac + bans.Game + (bans.Community ? 1 : 0) + (bans.TradeBanned ? 1 : 0)
		: null;

	/// <summary>
	/// One account's ban count against the last summary's: what to keep for the next one, and whether a ban is new. Kept
	/// is the most ever seen, so a ban lifted and then back again isn't new - only a count above anything seen before is.
	/// Not looked at now: the count from before is kept as it was. Nothing from before (a new account, the first look):
	/// noted, never new.
	/// </summary>
	internal static (int? Keep, bool New) BanStep(int? now, int? before) =>
		now is not { } n ? (before, false)
		: before is not { } b ? (n, false)
		: (Math.Max(n, b), n > b);

	/// <summary>
	/// The last summary's ban counts, as known. A file from before <see cref="State.BansFormat"/> wrote 0 for an account the
	/// ban watch had never looked at as well - read as 0, an account with a ban it always had was called newly banned on the
	/// first summary after updating. Its 0s are dropped there (not known: noted, never new); any other count was a real look.
	/// </summary>
	internal static Dictionary<string, int>? BansBefore(Dictionary<string, int>? stored, int? format) =>
		stored is not { } pb ? null
		: new Dictionary<string, int>(format >= BansFormatNow ? pb : pb.Where(static kv => kv.Value != 0), StringComparer.OrdinalIgnoreCase);

	/// <summary>Every account's figures counted up, the card made from them, and the snapshots the next summary counts from.</summary>
	private static Built Build(BotManager mgr, bool withTotal, DateTime nowUtc) {
		List<Bot> bots = [.. mgr.All];   // in the dashboard's order, like everywhere else

		bool r4r = mgr.Global.Rep4RepEnabled;
		Dictionary<string, int> cards = Count(Stats.KindCard);
		Dictionary<string, int> comments = Count(Stats.KindComment);

		Dictionary<string, double> prev;
		Dictionary<string, double>? prevGames;
		Dictionary<string, int>? prevBans;
		lock (Gate) {
			prev = new Dictionary<string, double>(_state.Lifetime, StringComparer.OrdinalIgnoreCase);
			prevGames = _state.Games is { } g ? new Dictionary<string, double>(g, StringComparer.OrdinalIgnoreCase) : null;
			prevBans = BansBefore(_state.Bans, _state.BansFormat);
		}

		bool first = prev.Count == 0;
		Dictionary<string, double> snapshot = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, double> games = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> bans = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> newBan = new(StringComparer.OrdinalIgnoreCase);
		List<Day> days = [];

		foreach (Bot b in bots) {
			double clock = Lifetime.For(b.Name);
			double played = Lifetime.GamesFor(b.Name);
			snapshot[b.Name] = clock;
			games[b.Name] = played;

			(int? keep, bool fresh) = BanStep(BanCount(b), (prevBans != null) && prevBans.TryGetValue(b.Name, out int had) ? had : null);

			if (keep is { } kept) {
				bans[b.Name] = kept;
			}

			if (fresh) {
				newBan.Add(b.Name);
			}

			// Null = nothing to count from yet (the first report for this account) - "not counted yet", not a fake "off".
			double? since = prevGames != null
				? prevGames.TryGetValue(b.Name, out double gamesBefore) ? Math.Max(0, played - gamesBefore) : null
				: prev.TryGetValue(b.Name, out double before) ? Math.Max(0, clock - before) * GamesPerHour(b.Name) : null;

			days.Add(new Day(b.Name, since, cards.GetValueOrDefault(b.Name), comments.GetValueOrDefault(b.Name), played));
		}

		// A ban is new when the count went above anything seen before (BanStep) - one that was already there isn't flagged
		// every morning.
		List<ReportCard.Row> attention = ReportCard.Attention(bots, r4r, prevBans == null ? null : b => newBan.Contains(b.Name), nowUtc);
		ReportCard card = Card(days, r4r, Note(first, nowUtc), withTotal, attention, nowUtc.ToLocalTime());

		return new Built(card, days.Count == 0, snapshot, games, bans);

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

			string path = Path;
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Failed("couldn't save the daily-report state", e);
		}
	}
}
