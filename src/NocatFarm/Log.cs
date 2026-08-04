using System.Collections.Concurrent;

namespace NocatFarm;

/// <summary>What a notification is about, so the user can switch off the kinds they don't care for.</summary>
public enum NotifyKind { Earning, Social, Problem }

/// <summary>
/// Console + file logging, and a ring buffer the dashboard reads so the browser shows the same stream you see
/// in the terminal. Colours are per-category and foreground only - background colours fill the whole row and
/// bleed across wrapped lines, which looks awful.
/// </summary>
public static class Log {
	public sealed record Entry(long Seq, DateTime When, string Level, string Source, string Text);

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
	private static string? _logFile;
	private static bool _debug;

	/// <summary>
	/// Set by the tray icon so the things worth interrupting someone for can raise a balloon. Null when there is
	/// no tray, which is the normal case off Windows.
	/// </summary>
	public static Action<NotifyKind, string, string>? Notify { get; set; }

	/// <summary>Raised for every line, so the dashboard's live stream doesn't have to poll.</summary>
	public static event Action<Entry>? Written;

	/// <summary>
	/// Stop printing lines to the screen, while still filing them and still raising <see cref="Written"/>.
	///
	/// Set while the live board owns the console: it draws the recent lines itself as part of its own layout,
	/// and a second writer scribbling over the same rows would tear the display apart.
	/// </summary>
	public static bool Suppressed { get; set; }

	public static void Configure(bool fileLogging, bool debug, string root, int retentionDays = 0) {
		_debug = debug;

		if (!fileLogging) {
			_logFile = null;

			return;
		}

		try {
			string dir = Path.Combine(root, "logs");
			Directory.CreateDirectory(dir);

			// One file per day, so retention is deleting old files rather than rewriting a live one.
			_logFile = Path.Combine(dir, $"nocatFarm-{DateTime.Now:yyyy-MM-dd}.log");

			if (retentionDays > 0) {
				DateTime cutoff = DateTime.Now.AddDays(-retentionDays);

				foreach (string old in Directory.GetFiles(dir, "nocatFarm-*.log")) {
					if (File.GetLastWriteTime(old) < cutoff) {
						File.Delete(old);
					}
				}
			}
		} catch {
			_logFile = null;   // logging must never take the app down
		}
	}

	public static string? FilePath => _logFile;
	public static bool DebugEnabled => _debug;

	public static IReadOnlyList<Entry> Recent(int max = 200) {
		Entry[] all = Ring.ToArray();

		return all.Length <= max ? all : all[^max..];
	}

	/// <summary>Everything logged after <paramref name="seq"/>. The dashboard uses this to catch up cheaply.</summary>
	public static IReadOnlyList<Entry> Since(long seq) => Ring.Where(e => e.Seq > seq).ToArray();

	public static void Info(string text, string source = "nocat.farm") => Write("INFO", source, text, ConsoleColor.Gray);
	public static void Good(string text, string source = "nocat.farm") => Write("GOOD", source, text, ConsoleColor.Green);
	public static void Warn(string text, string source = "nocat.farm") => Write("WARN", source, text, ConsoleColor.DarkYellow);

	public static void Error(string text, string source = "nocat.farm") {
		Write("ERROR", source, text, ConsoleColor.Red);
		Notify?.Invoke(NotifyKind.Problem, source, text);
	}

	/// <summary>Something social happened - somebody commented on a profile.</summary>
	public static void Event(string text, string source = "nocat.farm") {
		Write("GOOD", source, text, ConsoleColor.Cyan);
		Notify?.Invoke(NotifyKind.Social, source, text);
	}

	/// <summary>Something was earned - a card dropped, a comment was credited.</summary>
	public static void Reward(string text, string source = "nocat.farm") {
		Write("GOOD", source, text, ConsoleColor.Yellow);
		Notify?.Invoke(NotifyKind.Earning, source, text);
	}

	/// <summary>Something needs a human: a Steam Guard code, a password, a decision.</summary>
	public static void Attention(string text, string source = "nocat.farm") {
		Write("WARN", source, text, ConsoleColor.Magenta);
		Notify?.Invoke(NotifyKind.Problem, source, text);
	}

	/// <summary>
	/// Detail for diagnosing something, written to the log FILE and never to the console.
	///
	/// This used to print on screen too, and the screen is the one place it does not belong: every web request,
	/// every licence count, every re-assert, in grey, scrolling the three lines somebody actually wanted off the
	/// top. The file is where you go when something is wrong and you want everything; the console is where you
	/// glance to see if anything is. Debug detail serves the first and ruins the second.
	///
	/// The dashboard's Log tab still receives it - it has a DEBUG filter of its own, off by default, so it can
	/// be turned on there when it is wanted.
	/// </summary>
	public static void Debug(string text, string source = "nocat.farm") {
		if (_debug) {
			Write("DEBUG", source, text, ConsoleColor.DarkGray, toConsole: false);
		}
	}

	private static void Write(string level, string source, string text, ConsoleColor colour, bool toConsole = true) {
		DateTime now = DateTime.Now;
		Entry e = new(Interlocked.Increment(ref _seq), now, level, source, text);

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
		} catch {
			// a subscriber must never break logging
		}

		if (_logFile == null) {
			return;
		}

		try {
			File.AppendAllText(_logFile, $"{now:yyyy-MM-dd HH:mm:ss}|{level}|{source}|{text}{Environment.NewLine}");
		} catch {
			// logging must never take the app down
		}
	}
}
