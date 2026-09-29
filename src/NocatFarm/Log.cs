using System.Collections.Concurrent;
using Said = NocatFarm.Core.Said;

namespace NocatFarm;

/// <summary>What a notification is about, so the user can switch off the kinds they don't care for.</summary>
public enum NotifyKind { Earning, Social, Problem, Trade }

/// <summary>
/// What an event is about, finer than <see cref="NotifyKind"/>: the Discord and Telegram notifications let people
/// pick exactly which of these they want sent.
/// </summary>
public enum Topic { Cards, FreeStuff, Trades, Achievements, Rep4Rep, Social, Problems, Updates, Summary, Installs }

/// <summary>
/// Console + file logging, and a ring buffer the dashboard reads so the browser shows the same stream you see
/// in the terminal. Colours are per-category and foreground only - background colours fill the whole row and
/// bleed across wrapped lines, which looks awful.
/// </summary>
public static class Log {
	/// <summary>
	/// One logged line.
	/// </summary>
	/// <remarks>
	/// The sentence is kept as a Said - the English it was written as, plus its values - rather than as finished
	/// text. That is what lets the log already on screen redraw in a new language the moment one is chosen,
	/// instead of every line being frozen in the language it happened in.
	///
	/// The FILE is a different matter: it is written once, as it happens. A line already on disk stays in the
	/// language it was written in, and nothing short of rewriting history could change that - a log that edited
	/// its own past would be worth less than one that did not.
	/// </remarks>
	public sealed record Entry(long Seq, DateTime When, string Level, string Source, Core.Said Said) {
		/// <summary>
		/// The line in whatever language is selected right now - always ONE line. A line break inside it took more
		/// rows than the live board had counted, so every redraw scrolled and the header repeated down the screen,
		/// and the log file (one entry per line) read it as two entries.
		/// </summary>
		public string Text => Said.ToString().ReplaceLineEndings(" ");
	}

	private const int RingSize = 1000;

	/// <summary>
	/// Column rule. Box-drawing where the console can render it, a plain pipe where it can't - a legacy code
	/// page turns U+2502 into a question mark, and a log full of those is worse than a log with no rules.
	/// </summary>
	internal static readonly string Bar = Supports(Box) ? Box : "|";

	private const string Box = "│";

	private static bool Supports(string s) {
		try {
			return Console.OutputEncoding.GetString(Console.OutputEncoding.GetBytes(s)) == s;
		} catch {
			return false;
		}
	}

	/// <summary>Pad or truncate to an exact width, so columns line up whatever the account is called.</summary>
	internal static string Pad(string s, int width) =>
		s.Length <= width ? s.PadRight(width) : s[..(width - 1)] + "…";

	private static readonly ConcurrentQueue<Entry> Ring = new();
	private static readonly object ConsoleLock = new();
	private static long _seq;
	private static string? _logDir;
	private static string? _logFile;     // today's file; recomputed when the day turns
	private static DateTime _logFileDay;
	private static int _retentionDays;
	private static readonly object FileLock = new();
	private static bool _debug;

	/// <summary>
	/// Set by the tray icon so the things worth interrupting someone for can raise a balloon. Null when there is
	/// no tray, which is the normal case off Windows.
	/// </summary>
	public static Action<NotifyKind, string, string>? Notify { get; set; }

	/// <summary>Raised for every line, so the dashboard's live stream doesn't have to poll.</summary>
	public static event Action<Entry>? Written;

	/// <summary>
	/// Raised for the events worth telling someone about - earnings, problems, comments, updates, the daily
	/// summary - with what each is about. The Discord/Telegram notifier listens here.
	/// </summary>
	public static event Action<Topic, string, string>? Published;

	/// <summary>Tell the notifier about something without writing a log line for it (the daily summary, say).</summary>
	public static void Publish(Topic topic, string source, Core.Said text) {
		try {
			Published?.Invoke(topic, source, text.ToString());
		} catch (Exception e) {
			// a listener's problem is never the caller's - but it is written down, once per different failure
			FileOnlyOnChange("publish", source, $"a notification listener failed: {Describe(e)}");
		}
	}

	/// <summary>
	/// Stop printing lines to the screen, while still filing them and still raising <see cref="Written"/>.
	///
	/// Set while the live board owns the console: it draws the recent lines itself as part of its own layout,
	/// and a second writer scribbling over the same rows would tear the display apart.
	/// </summary>
	public static bool Suppressed { get; set; }

	public static void Configure(bool fileLogging, bool debug, string root, int retentionDays = 0) {
		_debug = debug;
		_retentionDays = retentionDays;

		if (!fileLogging) {
			_logFile = null;
			_logDir = null;   // TodaysFile rebuilds the path from the folder, so the folder has to go too

			return;
		}

		string dir = Path.Combine(root, "logs");

		try {
			Directory.CreateDirectory(dir);

			// The FOLDER is settled here; the filename is not. See TodaysFile.
			_logDir = dir;
			_logFile = null;
			Sweep();
		} catch (Exception e) {
			_logDir = null;
			_logFile = null;   // logging must never take the app down

			// Nowhere to file it, so at least the screen hears why there is no log file.
			Warn(new Said("can't write log files in {0}: {1}", Path.GetFullPath(dir), Describe(e)));
		}
	}

	/// <summary>The file today's lines go to. Rolls at midnight.</summary>
	/// <remarks>
	/// Resolved per write rather than once in Configure. Frozen at startup it named the file after the day the
	/// app STARTED, so a machine left running poured a fortnight into "nocatFarm-2026-09-08.log" - 14k lines in
	/// one file, no way to open a given day, and the "one file per day" that retention is built around simply
	/// was not true. Surfaced by a 17-day run.
	/// </remarks>
	private static string? TodaysFile() {
		if (_logDir == null) {
			return null;
		}

		DateTime today = DateTime.Now.Date;

		if ((_logFile != null) && (_logFileDay == today)) {
			return _logFile;
		}

		lock (FileLock) {
			if ((_logFile != null) && (_logFileDay == today)) {
				return _logFile;
			}

			_logFile = Path.Combine(_logDir, $"nocatFarm-{today:yyyy-MM-dd}.log");
			_logFileDay = today;

			// The day just turned. On a long run this is the only chance to clear old files - doing it once at
			// startup only ever tidies up for a process that gets restarted.
			Sweep();

			return _logFile;
		}
	}

	/// <summary>Delete logs older than the retention setting. By last-write time, so a live file is never taken.</summary>
	private static void Sweep() {
		if ((_logDir == null) || (_retentionDays <= 0)) {
			return;
		}

		try {
			DateTime cutoff = DateTime.Now.AddDays(-_retentionDays);

			foreach (string old in Directory.GetFiles(_logDir, "nocatFarm-*.log")) {
				if (File.GetLastWriteTime(old) < cutoff) {
					File.Delete(old);
				}
			}
		} catch (Exception e) {
			// tidying up must never take the app down - but say so, or the folder just grows without anyone knowing why.
			// Straight to the file: this runs inside the day roll, and a full Write from here would roll again.
			FileOnly("nocat.farm", $"couldn't clear old log files: {Describe(e)}");
		}
	}

	public static bool DebugEnabled => _debug;

	/// <summary>The folder the log files go in; null when file logging is off.</summary>
	public static string? Folder => _logDir;

	public static IReadOnlyList<Entry> Recent(int max = 200) {
		Entry[] all = Ring.ToArray();

		return all.Length <= max ? all : all[^max..];
	}

	/// <summary>Everything logged after <paramref name="seq"/>. The dashboard uses this to catch up cheaply.</summary>
	public static IReadOnlyList<Entry> Since(long seq) => Ring.Where(e => e.Seq > seq).ToArray();

	public static void Info(string text, string source = "nocat.farm") => Info(new Core.Said(text), source);
	public static void Info(Core.Said text, string source = "nocat.farm") => Write("INFO", source, text, ConsoleColor.Gray);
	public static void Good(string text, string source = "nocat.farm") => Good(new Core.Said(text), source);
	public static void Good(Core.Said text, string source = "nocat.farm") => Write("GOOD", source, text, ConsoleColor.Green);
	public static void Warn(string text, string source = "nocat.farm") => Warn(new Core.Said(text), source);
	public static void Warn(Core.Said text, string source = "nocat.farm") => Write("WARN", source, text, ConsoleColor.DarkYellow);

	/// <summary>A trade offer event: the log, the trades pop-up, and Discord/Telegram's Trades topic.</summary>
	public static void Trade(Core.Said text, string source, bool good = false) {
		Write(good ? "GOOD" : "INFO", source, text, good ? ConsoleColor.Yellow : ConsoleColor.Cyan);
		Notify?.Invoke(NotifyKind.Trade, source, text.ToString());
		Publish(Topic.Trades, source, text);
	}

	public static void Error(string text, string source = "nocat.farm", Topic topic = Topic.Problems) => Error(new Core.Said(text), source, topic);

	public static void Error(Core.Said text, string source = "nocat.farm", Topic topic = Topic.Problems) {
		Write("ERROR", source, text, ConsoleColor.Red);
		Notify?.Invoke(NotifyKind.Problem, source, text.ToString());
		Publish(topic, source, text);
	}

	/// <summary>Something social happened - somebody commented on a profile.</summary>
	public static void Event(Core.Said text, string source = "nocat.farm", Topic topic = Topic.Social) {
		Write("GOOD", source, text, ConsoleColor.Cyan);
		Notify?.Invoke(NotifyKind.Social, source, text.ToString());
		Publish(topic, source, text);
	}

	/// <summary>Something was earned - a card dropped, a comment was credited.</summary>
	public static void Reward(string text, string source = "nocat.farm", Topic topic = Topic.Cards) => Reward(new Core.Said(text), source, topic);

	public static void Reward(Core.Said text, string source = "nocat.farm", Topic topic = Topic.Cards) {
		Write("GOOD", source, text, ConsoleColor.Yellow);
		Notify?.Invoke(NotifyKind.Earning, source, text.ToString());
		Publish(topic, source, text);
	}

	/// <summary>Something needs a human: a Steam Guard code, a password, a decision.</summary>
	public static void Attention(string text, string source = "nocat.farm", Topic topic = Topic.Problems) => Attention(new Core.Said(text), source, topic);

	public static void Attention(Core.Said text, string source = "nocat.farm", Topic topic = Topic.Problems) {
		Write("WARN", source, text, ConsoleColor.Magenta);
		Notify?.Invoke(NotifyKind.Problem, source, text.ToString());
		Publish(topic, source, text);
	}

	/// <summary>
	/// The same, when the log line is too much for a pop-up: the log gets <paramref name="text"/> in full (commands,
	/// links), while the pop-up and Discord/Telegram get <paramref name="brief"/> under <paramref name="title"/>.
	/// With <paramref name="loud"/> false it's the log only - for reminders, which would be spam as pop-ups.
	/// </summary>
	public static void Attention(Core.Said text, Core.Said brief, Core.Said title, Topic topic, bool loud = true) {
		Write("WARN", "nocat.farm", text, ConsoleColor.Magenta);

		if (loud) {
			Notify?.Invoke(NotifyKind.Problem, title.ToString(), brief.ToString());
			Publish(topic, "nocat.farm", brief);
		}
	}

	/// <summary>
	/// Detail for diagnosing something. Always written to the log FILE; on screen only if asked for.
	///
	/// The switch used to decide whether the line existed at all, which is backwards: the one moment you want
	/// this detail is after something has already gone wrong, and by then a switch that was off means the
	/// evidence was never recorded and the fault has to be reproduced to get it. So the file always keeps it -
	/// that is what a log is for - and the setting only decides whether it is ALSO put in front of you.
	///
	/// On screen it is genuinely unwanted by default: every web request, every licence count, every re-assert,
	/// in grey, scrolling the three lines somebody actually cared about off the top.
	/// </summary>
	public static void Debug(string text, string source = "nocat.farm") => Debug(new Core.Said(text), source);

	public static void Debug(Core.Said text, string source = "nocat.farm") =>
		Write("DEBUG", source, text, ConsoleColor.DarkGray, toConsole: _debug);

	// ── when something goes wrong ─────────────────────────────────────────────
	// A failure that leaves no line behind can only be found by reproducing it. These are the plain, untranslated
	// DEBUG lines that say what failed, on which account, and why - always in the file, on screen only if asked.

	/// <summary>"what: ExceptionType: message", as a DEBUG line - for a catch that carries on without the thing it tried.</summary>
	public static void Failed(string what, Exception e, string source = "nocat.farm") => Debug($"{what}: {Describe(e)}", source);

	/// <summary>An exception as "Type: message", with anything secret-looking in the message hidden.</summary>
	public static string Describe(Exception e) => $"{e.GetType().Name}: {Scrub(e.Message)}";

	/// <summary>
	/// A web address safe to write down: host and path only, never the query (that is where the Web API's
	/// access_token and the rep4rep key ride), and the secret path segments of a Telegram bot URL or a Discord
	/// webhook masked - both carry the token in the path itself.
	/// </summary>
	public static string Where(Uri? url) {
		if (url == null) {
			return "?";
		}

		if (!url.IsAbsoluteUri) {
			return Scrub(url.OriginalString.Split('?', '#')[0]);
		}

		return url.Host + Scrub(url.AbsolutePath);
	}

	private static readonly System.Text.RegularExpressions.Regex[] Secrets = [
		// key=value pairs in a query, a form or a cookie header
		new(@"(?i)\b((?:access_token|oauth_token|refresh_token|webapi_token|apitoken|apikey|api_key|key|token|password|pass|steamLoginSecure|sessionid|secret)=)[^&\s;""',]+",
			System.Text.RegularExpressions.RegexOptions.Compiled),
		// user:password@ in a proxy or any other address
		new(@"(://)[^/\s:@]+:[^/\s@]+(?=@)",System.Text.RegularExpressions.RegexOptions.Compiled),
		// Telegram: api.telegram.org/bot<id>:<secret>/method
		new(@"(?i)(bot)\d{5,}:[A-Za-z0-9_-]{20,}", System.Text.RegularExpressions.RegexOptions.Compiled),
		// Discord: /api/webhooks/<id>/<secret>
		new(@"(?i)(webhooks/\d+/)[A-Za-z0-9_.-]+", System.Text.RegularExpressions.RegexOptions.Compiled),
		// Authorization: Bot xxx / Bearer xxx
		new(@"(?i)\b((?:Bot|Bearer)\s+)[A-Za-z0-9_.\-]{20,}", System.Text.RegularExpressions.RegexOptions.Compiled),
		// a bare JWT (Steam's access and refresh tokens)
		new(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*", System.Text.RegularExpressions.RegexOptions.Compiled),
	];

	/// <summary>Hide tokens, keys, passwords, cookies and webhook secrets in text headed for the log.</summary>
	public static string Scrub(string? text) {
		if (string.IsNullOrEmpty(text)) {
			return "";
		}

		string s = text;

		for (int i = 0; i < Secrets.Length; i++) {
			s = Secrets[i].Replace(s, i == Secrets.Length - 1 ? "[hidden]" : "$1[hidden]");   // the last, a bare JWT, has no prefix to keep
		}

		return s;
	}

	private static readonly ConcurrentDictionary<string, (string Text, DateTime At)> LastSaid = new(StringComparer.Ordinal);

	/// <summary>
	/// A DEBUG line only when it differs from the last one under <paramref name="key"/>, or the same one was last
	/// written over an hour ago - for something that fails the same way every minute in a loop, where the first line
	/// says it all and the next thousand bury the rest. Returns whether it was written. <see cref="Recovered"/>
	/// clears it, so the next failure after a success is written straight away.
	/// </summary>
	public static bool DebugOnChange(string key, string text, string source = "nocat.farm") {
		if (!OnChange(key, text)) {
			return false;
		}

		Debug(text, source);

		return true;
	}

	private static bool OnChange(string key, string text) {
		DateTime now = DateTime.UtcNow;

		if (LastSaid.TryGetValue(key, out (string Text, DateTime At) last)
			&& string.Equals(last.Text, text, StringComparison.Ordinal) && (now - last.At < TimeSpan.FromHours(1))) {
			return false;
		}

		LastSaid[key] = (text, now);

		return true;
	}

	/// <summary>The thing under <paramref name="key"/> works again: its next failure is news.</summary>
	public static void Recovered(string key) => LastSaid.TryRemove(key, out _);

	/// <summary>
	/// An exception nothing else caught - a crash, a background task nobody awaited, a loop that died. The line goes
	/// to the screen and the file, then the whole stack trace to the file only, one line each, straight away: this
	/// can run with the process on its way down, and anything slower than an append is not going to happen.
	/// </summary>
	public static void Crash(Core.Said what, Exception e, string source = "nocat.farm") {
		Write("ERROR", source, what, ConsoleColor.Red);
		StackToFile(e, source);
	}

	/// <summary>For <see cref="TaskScheduler.UnobservedTaskException"/>: a task nobody awaited threw. Written down and marked seen.</summary>
	public static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e) {
		try {
			Exception inner = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
			Crash(new Said("a background task failed: {0}", Describe(inner)), e.Exception);
		} catch {
			// logging must never take the app down
		}

		e.SetObserved();
	}

	/// <summary>The full exception - type, message, stack, inner exceptions - into the file only, one line each.</summary>
	public static void StackToFile(Exception e, string source = "nocat.farm") {
		string? file = TodaysFile();

		if (file == null) {
			return;
		}

		string when = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
		System.Text.StringBuilder lines = new();

		foreach (string line in Scrub(e.ToString()).Split('\n')) {
			string trimmed = line.Trim();

			if (trimmed.Length > 0) {
				lines.Append(when).Append("|DEBUG|").Append(source).Append("|   ").Append(trimmed).Append(Environment.NewLine);
			}
		}

		Append(file, lines.ToString(), debug: true);
	}

	private static void Write(string level, string source, Core.Said said, ConsoleColor colour, bool toConsole = true) {
		DateTime now = DateTime.Now;
		Entry e = new(Interlocked.Increment(ref _seq), now, level, source, said);

		// Rendered once, here, for the console and the file. Both are written as it happens, in the language
		// selected at that moment; only the entry in the ring can be re-rendered later.
		string text = e.Text;

		Ring.Enqueue(e);

		while (Ring.Count > RingSize) {
			Ring.TryDequeue(out _);
		}

		// Three columns with dim rules between them, so the eye can find the account name in a wall of output.
		// The message keeps the level colour; the furniture stays out of the way.
		//
		// Skipped entirely while the live board owns the screen - it draws these itself as part of its layout,
		// and two writers on the same rows tear the display apart. Everything below still happens: the entry is
		// filed, and the subscribers (the dashboard, the board) still get it.
		if (toConsole && !Suppressed) {
			lock (ConsoleLock) {
				ConsoleColor prev = Console.ForegroundColor;

				try {
					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.Write($"{now:HH:mm:ss} ");
					Console.Write(Bar);

					Console.ForegroundColor = source == "nocat.farm" ? ConsoleColor.DarkGray : ConsoleColor.DarkCyan;
					Console.Write($" {Pad(source, 10)} ");

					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.Write(Bar);
					Console.Write(' ');

					Console.ForegroundColor = colour;
					Console.WriteLine(text);
				} catch (IOException) {
					// no console attached (a service, redirected output) - the file log still has it
				} finally {
					try {
						Console.ForegroundColor = prev;
					} catch (IOException) {
						// ditto
					}
				}
			}
		}

		try {
			Written?.Invoke(e);
		} catch (Exception x) {
			// a subscriber must never break logging - nor start a loop of lines about itself, so file only, once
			FileOnlyOnChange("written", source, $"a log listener failed: {Describe(x)}");
		}

		string? file = TodaysFile();

		if (file == null) {
			return;
		}

		Append(file, $"{now:yyyy-MM-dd HH:mm:ss}|{level}|{source}|{text}{Environment.NewLine}", level == "DEBUG");
	}

	private static readonly Lock FileGate = new();

	/// <summary>
	/// How big one day's file may get before DEBUG detail stops going into it. Retention bounds the number of files,
	/// but nothing bounded one file: a failure repeating every second on a machine left running for a day writes
	/// hundreds of megabytes of the same line. Past this the day keeps everything that isn't DEBUG - the warnings,
	/// errors and what happened - and a note says why the detail stopped. The next day starts clean.
	/// </summary>
	internal const long DebugCapBytes = 100L * 1024 * 1024;

	private static string? _sizedFile;
	private static long _fileBytes;
	private static bool _capNoted;

	/// <summary>
	/// One writer at a time. Two threads appending together collided on the file and the loser's line was dropped
	/// without a word - the log is the one place that must not quietly lose things.
	/// </summary>
	private static void Append(string file, string text, bool debug) {
		lock (FileGate) {
			try {
				if (!string.Equals(_sizedFile, file, StringComparison.Ordinal)) {
					_sizedFile = file;
					_fileBytes = File.Exists(file) ? new FileInfo(file).Length : 0;
					_capNoted = false;
				}

				if (debug && (_fileBytes > DebugCapBytes)) {
					if (_capNoted) {
						return;
					}

					_capNoted = true;
					text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}|WARN|nocat.farm|this log passed {DebugCapBytes / (1024 * 1024)} MB today - debug detail stops here until tomorrow{Environment.NewLine}";
				}

				File.AppendAllText(file, text);
				_fileBytes += System.Text.Encoding.UTF8.GetByteCount(text);
			} catch {
				// logging must never take the app down
			}
		}
	}

	/// <summary>
	/// A DEBUG line for the file alone, not the screen or the listeners - for trouble inside logging itself, where
	/// going through Write would call the very thing that just failed.
	/// </summary>
	private static void FileOnly(string source, string text) {
		string? dir = _logDir;

		if (dir == null) {
			return;
		}

		string file = _logFile ?? Path.Combine(dir, $"nocatFarm-{DateTime.Now:yyyy-MM-dd}.log");
		Append(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}|DEBUG|{source}|{text.ReplaceLineEndings(" ")}{Environment.NewLine}", debug: true);
	}

	private static void FileOnlyOnChange(string key, string source, string text) {
		if (OnChange("log:" + key, text)) {
			FileOnly(source, text);
		}
	}
}
