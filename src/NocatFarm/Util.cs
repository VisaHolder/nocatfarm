using System.Text;
using Said = NocatFarm.Core.Said;

namespace NocatFarm;

/// <summary>
/// Questions that can be answered from either front end. There is exactly ONE thing reading the keyboard (the
/// console loop), so a prompt doesn't read input itself - it publishes the question and waits for an answer to
/// arrive, from the console or from the web UI, whichever gets there first.
/// </summary>
public static class Prompt {
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>
	/// One question, with its own answer. Everything about it - the words, whether it's a secret, who asked, and where
	/// the answer goes - travels together, so nothing can pair one question's text with another question's answer.
	/// </summary>
	/// <remarks>
	/// Four separate fields used to be set and cleared one by one: the window could read "a question is up", the dashboard
	/// answer it and the next account ask its own, and the line typed for the first went to the second.
	/// </remarks>
	public sealed class Question {
		internal Question(string text, bool secret, string? owner) {
			Text = text;
			Secret = secret;
			Owner = owner;
		}

		public string Text { get; }
		public bool Secret { get; }
		public string? Owner { get; }

		internal TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private static volatile Question? _current;

	/// <summary>The question up right now, or null. Read it once and use that: it can change between two reads.</summary>
	public static Question? Current => _current;

	/// <summary>The question currently waiting for an answer, or null.</summary>
	public static string? Pending => _current?.Text;

	/// <summary>Whether the pending answer is a secret, so the console stops echoing what is typed.</summary>
	public static bool PendingSecret => _current?.Secret ?? false;

	/// <summary>A question went up or came down - the live board redraws its prompt line.</summary>
	public static event Action? Changed;

	/// <summary>Supply the answer to the pending prompt. Returns false when nothing was asked.</summary>
	public static bool Answer(string value) => _current?.Reply.TrySetResult(value) ?? false;

	/// <summary>
	/// Answer <paramref name="question"/> and nothing else - the one on screen when the answer was typed. False when it has
	/// already been answered (from the dashboard, say) or given up: the answer never carries over to the next question.
	/// </summary>
	public static bool Answer(Question question, string value) => question.Reply.TrySetResult(value);

	/// <summary>
	/// Abandon the pending question, but only if <paramref name="owner"/> is the one who asked it. Used when the
	/// thing that asked is being stopped - otherwise stopping an account mid-login would leave its password
	/// prompt on screen forever, or worse, blank-answer the prompt a DIFFERENT account was waiting on.
	/// </summary>
	private static readonly HashSet<string> Abandoned = new(StringComparer.OrdinalIgnoreCase);

	public static void Cancel(string? owner = null) {
		Question? up = _current;

		if (owner != null && up?.Owner != null && !string.Equals(owner, up.Owner, StringComparison.OrdinalIgnoreCase)) {
			// Someone else's question is on screen. This owner may still be QUEUED behind it - remember that it
			// was cancelled, or its turn would come round and hang the prompt on a bot nobody is running.
			lock (Abandoned) {
				Abandoned.Add(owner);
			}

			return;
		}

		up?.Reply.TrySetResult("");
	}

	public static Task<string> LineAsync(string question, string? owner = null) => AskAsync(question, false, owner);
	public static Task<string> SecretAsync(string question, string? owner = null) => AskAsync(question, true, owner);

	private static async Task<string> AskAsync(string question, bool secret, string? owner) {
		if (owner != null) {
			lock (Abandoned) {
				Abandoned.Remove(owner);   // a fresh ask clears any stale cancellation
			}
		}

		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			// Cancelled while we were queued behind another account's prompt - don't put a dead question up.
			if (owner != null) {
				lock (Abandoned) {
					if (Abandoned.Remove(owner)) {
						return "";
					}
				}
			}

			Question asked = new(question, secret, owner);
			_current = asked;
			Raise();

			// Straight onto the console only while nothing else owns the screen. The live board repaints the bottom of it
			// every second - a question written there was painted over at once, and on Linux or --no-gui the Steam Guard
			// code was asked for where nobody could read it. The board draws it on its own prompt line instead (Changed),
			// and the window shows it above its command box.
			if (!Log.Suppressed) {
				Log.ToConsole(Environment.NewLine + $"  {question}: ");
			}

			return (await asked.Reply.Task.ConfigureAwait(false)).Trim();
		} finally {
			_current = null;
			Raise();
			Gate.Release();
		}
	}

	private static void Raise() {
		try {
			Changed?.Invoke();
		} catch (Exception e) {
			// a screen that couldn't redraw must not lose the question, or the answer
			Log.Debug($"the prompt line couldn't be redrawn: {Log.Describe(e)}");
		}
	}
}

/// <summary>
/// Tiny hand-rolled HTML/JSON readers. Deliberately dependency-free: Steam's markup is scraped in a handful
/// of narrow places and pulling a whole parser in for that is not worth the supply-chain surface.
/// </summary>
public static class Html {
	/// <summary>Text between two markers, starting the search at <paramref name="from"/>. Null when absent.</summary>
	public static string? Between(string s, string open, string close, int from = 0) {
		int a = s.IndexOf(open, from, StringComparison.Ordinal);

		if (a < 0) {
			return null;
		}

		a += open.Length;
		int b = s.IndexOf(close, a, StringComparison.Ordinal);

		return b > a ? s[a..b] : null;
	}

	/// <summary>Strip tags, decode the entities Steam actually emits, and collapse whitespace.</summary>
	public static string Text(string html) {
		StringBuilder sb = new();
		bool inTag = false;

		foreach (char c in html) {
			if (c == '<') {
				inTag = true;
			} else if (c == '>') {
				inTag = false;
			} else if (!inTag) {
				sb.Append(c is '\n' or '\r' or '\t' ? ' ' : c);
			}
		}

		string t = sb.ToString()
			.Replace("&quot;", "\"", StringComparison.Ordinal)
			.Replace("&#39;", "'", StringComparison.Ordinal)
			.Replace("&lt;", "<", StringComparison.Ordinal)
			.Replace("&gt;", ">", StringComparison.Ordinal)
			.Replace("&nbsp;", " ", StringComparison.Ordinal)
			.Replace("&amp;", "&", StringComparison.Ordinal);

		while (t.Contains("  ", StringComparison.Ordinal)) {
			t = t.Replace("  ", " ", StringComparison.Ordinal);
		}

		return t.Trim();
	}
}

public static class Rng {
	private static readonly Random R = new();

	/// <summary>Thread-safe inclusive-lo, exclusive-hi random. Random itself is not thread-safe.</summary>
	public static int Next(int lo, int hi) {
		lock (R) {
			return R.Next(lo, hi);
		}
	}

	public static TimeSpan Minutes(int lo, int hi) => TimeSpan.FromSeconds(Next(lo * 60, (hi * 60) + 1));
	public static TimeSpan Seconds(int lo, int hi) => TimeSpan.FromSeconds(Next(lo, hi + 1));

	/// <summary>
	/// How long a person takes to get round to something - a trade offer, a gift, a friend request - somewhere
	/// between <paramref name="lo"/> and <paramref name="hi"/> minutes.
	/// </summary>
	/// <remarks>
	/// Not a flat pick. Spread enough answers evenly across a window and the even smear is itself a shape no
	/// person makes: people mostly see a notification fairly soon and now and then only much later. So this leans
	/// towards the early part of the window - a triangle peaking a quarter of the way in - while still using all of
	/// it, and it never lands outside it.
	/// </remarks>
	public static TimeSpan HumanMinutes(int lo, int hi) {
		lo = Math.Max(0, lo);
		hi = Math.Max(lo, hi);

		if (hi == lo) {
			return TimeSpan.FromMinutes(lo);
		}

		double a = lo, b = hi, peak = lo + ((hi - lo) * 0.25);
		double u;

		lock (R) {
			u = R.NextDouble();
		}

		double split = (peak - a) / (b - a);
		double minutes = u < split
			? a + Math.Sqrt(u * (b - a) * (peak - a))
			: b - Math.Sqrt((1 - u) * (b - a) * (b - peak));

		return TimeSpan.FromSeconds(Math.Round(Math.Clamp(minutes, a, b) * 60));
	}
}

public static class Fmt {
	/// <summary>
	/// A whole number with its thousands grouped the way the language picked writes them - "1,240", "1.240", "1 240" - the
	/// same as the dashboard's toLocaleString. By hand because the app runs without the system's culture data
	/// (InvariantGlobalization), where every culture groups like English. Spanish and Polish leave a 4-digit number whole.
	/// </summary>
	public static string Grouped(int n, string? language = null) {
		string lang = language ?? Config.Live.Global.Language ?? "en";
		(string sep, bool fourWhole) = lang switch {
			"de" or "pt-BR" or "tr" => (".", false),
			"es" => (".", true),
			"fr" => (" ", false),
			"ru" => (" ", false),
			"pl" => (" ", true),
			_ => (",", false)
		};

		string digits = Math.Abs((long) n).ToString(System.Globalization.CultureInfo.InvariantCulture);

		if ((digits.Length <= 3) || (fourWhole && (digits.Length == 4))) {
			return (n < 0 ? "-" : "") + digits;
		}

		StringBuilder sb = new(digits.Length + 8);

		for (int i = 0; i < digits.Length; i++) {
			if ((i > 0) && ((digits.Length - i) % 3 == 0)) {
				sb.Append(sep);
			}

			sb.Append(digits[i]);
		}

		return (n < 0 ? "-" : "") + sb;
	}

	/// <summary>Minutes as "3h20m" / "45m".</summary>
	public static string Hm(int minutes) {
		if (minutes < 60) {
			return minutes + "m";
		}

		return $"{minutes / 60}h{minutes % 60:00}m";
	}

	/// <summary>A length of time for an estimate: minutes stop mattering past a few hours, and hours past a day.</summary>
	public static string Rough(int minutes) {
		minutes = Math.Max(1, minutes);

		return minutes < 60 ? minutes + "m"
			: minutes < 6 * 60 ? $"{minutes / 60}h{minutes % 60:00}m"
			: minutes < 48 * 60 ? $"{(int) Math.Round(minutes / 60.0)}h"
			: $"{minutes / (24 * 60)}d{(minutes % (24 * 60)) / 60}h";
	}

	/// <summary>
	/// Round a set of exact percentages to whole numbers that still add up to <paramref name="total"/>.
	///
	/// Largest remainder: floor everything, then give the leftover points to whichever values were cut hardest.
	/// Rounding each one on its own is what prints a row totalling 101 - an exact 77.5 and an exact 10.5 both go
	/// up, and the column gains a point that was never there.
	/// </summary>
	public static int[] RoundToTotal(double[] values, int total) {
		int[] floors = values.Select(static v => (int) Math.Floor(v)).ToArray();
		int left = total - floors.Sum();

		foreach (int i in values
			.Select(static (v, i) => (Index: i, Frac: v - Math.Floor(v), Size: v))
			.OrderByDescending(static x => x.Frac)
			.ThenByDescending(static x => x.Size)   // a tie goes to the biggest row, where a point shows least
			.Select(static x => x.Index)) {
			if (left <= 0) {
				break;
			}

			floors[i]++;
			left--;
		}

		return floors;
	}

	/// <summary>
	/// A clock time that can never be read as the wrong day.
	///
	/// A bare "17:16" is only unambiguous for today. Printed for a stamp on another date it reads as a time
	/// still to come - the achievement pacer spent a long while reporting "next after 17:16" for a moment that
	/// had already passed the previous afternoon, which looks exactly like a stuck module.
	/// </summary>
	public static string Clock(DateTime utc) {
		DateTime local = utc.ToLocalTime();
		int days = (local.Date - DateTime.Now.Date).Days;

		// "tomorrow" and "yesterday" are words, not formats, so they need translating like any other word - a
		// Chinese dashboard was reporting "下一个在 tomorrow 06:49 之后". The day and month names come from the
		// framework's own formatting and follow the machine's culture, which is the right source for those.
		return days switch {
			0 => $"{local:HH:mm}",
			1 => Core.Loc.T("tomorrow {0}", local.ToString("HH:mm")),
			-1 => Core.Loc.T("yesterday {0}", local.ToString("HH:mm")),
			> 1 and < 7 => $"{local:ddd HH:mm}",
			_ => $"{local:d MMM HH:mm}"
		};
	}

	public static string Ago(DateTime? utc) {
		if (utc == null) {
			return "-";
		}

		TimeSpan d = DateTime.UtcNow - utc.Value;

		return d.TotalMinutes < 1 ? "just now"
			: d.TotalHours < 1 ? $"{(int) d.TotalMinutes}m"
			: d.TotalDays < 1 ? $"{(int) d.TotalHours}h"
			: $"{(int) d.TotalDays}d";
	}
}

/// <summary>
/// Text cut and padded for a table, by what the eye sees rather than by chars.
/// </summary>
/// <remarks>
/// A char is half an emoji. The status table cut a custom game name of "💀nocat.lol/nocatfarm💀" between the two halves of
/// the second skull and printed "💀nocat.lol/nocatfarm�…" - a lone surrogate the console can only show as "�". So text is
/// cut on whole text elements (a pair, an emoji with its variation selector, a letter with its accent), and measured in
/// the columns a terminal gives it: two for an emoji or a CJK character, one for the rest. Padded by chars, a row with
/// an emoji in it ended a column later than the rest.
/// </remarks>
public static class Columns {
	/// <summary>The columns <paramref name="text"/> takes in a terminal.</summary>
	public static int Width(string text) {
		int width = 0;

		for (int i = 0; i < text.Length;) {
			int n = System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(i));
			width += ElementWidth(text.AsSpan(i, n));
			i += n;
		}

		return width;
	}

	/// <summary>One text element's columns: none for a control char, two for an emoji or a wide (CJK) character, one otherwise.</summary>
	public static int ElementWidth(ReadOnlySpan<char> element) {
		if (element.IsEmpty || (Rune.DecodeFromUtf16(element, out Rune first, out _) != System.Buffers.OperationStatus.Done)) {
			return element.IsEmpty ? 0 : 1;
		}

		if (Rune.IsControl(first)) {
			return 0;
		}

		// U+FE0F asks for the emoji picture, which is drawn two wide: ☠️ as against the text-style ☠.
		return element.Contains('️') || Wide(first.Value) ? 2 : 1;
	}

	private static bool Wide(int c) =>
		c is (>= 0x1100 and <= 0x115F)             // Hangul jamo
			or (>= 0x2E80 and <= 0x303E)           // CJK radicals and punctuation
			or (>= 0x3041 and <= 0x33FF)           // kana, CJK symbols
			or (>= 0x3400 and <= 0x4DBF)
			or (>= 0x4E00 and <= 0x9FFF)           // CJK ideographs
			or (>= 0xA000 and <= 0xA4CF)
			or (>= 0xAC00 and <= 0xD7A3)           // Hangul syllables
			or (>= 0xF900 and <= 0xFAFF)
			or (>= 0xFE30 and <= 0xFE4F)
			or (>= 0xFF00 and <= 0xFF60)           // fullwidth forms
			or (>= 0xFFE0 and <= 0xFFE6)
			// Emoji in the BMP drawn as a picture without asking: ⌚ ⏳ ☔ ♈ ⚡ ⚽ ⛔ ✅ ❌ ❓ ➕ ⭐ ⭕ and their kin.
			or 0x231A or 0x231B or (>= 0x23E9 and <= 0x23EC) or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE
			or 0x2614 or 0x2615 or (>= 0x2648 and <= 0x2653) or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB
			or 0x26BD or 0x26BE or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5
			or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E or (>= 0x2753 and <= 0x2755)
			or 0x2757 or (>= 0x2795 and <= 0x2797) or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55
			or 0x1F004 or 0x1F0CF or 0x1F18E or (>= 0x1F191 and <= 0x1F19A)
			or (>= 0x1F1E6 and <= 0x1F1FF)         // regional indicators: a flag is a pair of them, one element, two wide
			or (>= 0x1F200 and <= 0x1F251)
			or (>= 0x1F300 and <= 0x1F64F)         // pictographs and faces
			or (>= 0x1F680 and <= 0x1F6FF)         // transport and map
			or (>= 0x1F7E0 and <= 0x1F7EB)         // coloured circles and squares
			or (>= 0x1F900 and <= 0x1F9FF)
			or (>= 0x1FA70 and <= 0x1FAFF)
			or (>= 0x20000 and <= 0x3FFFD);

	/// <summary>At most <paramref name="width"/> columns, cut on a whole text element and ended with "…" when something was left off.</summary>
	public static string Clip(string text, int width) {
		if (Width(text) <= width) {
			return text;
		}

		if (width <= 0) {
			return "";
		}

		StringBuilder sb = new();
		int used = 0;

		for (int i = 0; i < text.Length;) {
			int n = System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(i));
			int w = ElementWidth(text.AsSpan(i, n));

			if (used + w > width - 1) {
				break;
			}

			sb.Append(text, i, n);
			used += w;
			i += n;
		}

		return sb.Append('…').ToString();
	}

	/// <summary>Exactly <paramref name="width"/> columns: cut as <see cref="Clip"/> does, or padded with spaces.</summary>
	public static string Fit(string text, int width) {
		string cut = Clip(text, width);

		return cut + new string(' ', Math.Max(0, width - Width(cut)));
	}

	/// <summary>Padded out to <paramref name="width"/> columns, and never cut - the {x,-30} of a column, measured by eye.</summary>
	public static string PadRight(string text, int width) => text + new string(' ', Math.Max(0, width - Width(text)));

	/// <summary>
	/// At most <paramref name="max"/> chars, <paramref name="tail"/> included when it's cut - for a limit counted in chars
	/// (a chat message, a Discord button, a tray balloon) - and cut on a whole text element, never half an emoji.
	/// </summary>
	public static string ClipChars(string text, int max, string tail = "…") {
		if (text.Length <= max) {
			return text;
		}

		int room = max - tail.Length;

		if (room < 0) {
			return "";
		}

		int end = 0;

		while (end < text.Length) {
			int n = System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(end));

			if (end + n > room) {
				break;
			}

			end += n;
		}

		return text[..end] + tail;
	}
}

/// <summary>Loops started in the background and never awaited.</summary>
public static class Background {
	/// <summary>
	/// Starts a loop meant to run as long as the app, and says so if it ever ends by throwing - which loop, and why.
	/// Each loop catches its own failures; one that got past that used to simply stop, the feature going quiet with
	/// nothing in the log.
	/// </summary>
	public static void Loop(Said which, Func<Task> loop, string source = "nocat.farm") =>
		_ = Task.Run(loop).ContinueWith(t => {
			Exception e = t.Exception!.GetBaseException();
			Log.Error(new Said("{0} stopped: {1} - restart nocat.farm to get it back", which, Log.Describe(e)), source);
			Log.StackToFile(e, source);
		}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

	/// <summary>
	/// One job started and not waited for - an account's start or stop from a button - with a failure written down
	/// under the account's name. Left to the lost-task handler, the line said what broke but not on which account.
	/// </summary>
	public static void Run(string what, Func<Task> job, string source) =>
		_ = job().ContinueWith(t => {
			Exception e = t.Exception!.GetBaseException();
			Log.Failed(what, e, source);
			Log.StackToFile(e, source);
		}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}

/// <summary>Line editing for the console's own reader (Program's ReadLine), the one every OS types commands into.</summary>
public static class TypedLine {
	/// <summary>
	/// Backspace: takes the last thing typed off the line and says how many columns to rub out on the screen.
	/// </summary>
	/// <remarks>
	/// The last thing typed, not the last char. An emoji is two chars (a surrogate pair), and taking one off left half of
	/// it behind: "name kylro nocat💀", a backspace to drop the 💀, and the name saved was "nocat" plus a broken half-char
	/// that went to Steam as "�" on the friends list. A whole text element goes - a pair, an emoji with its variation
	/// selector (☠️), a letter with its accent - and a masked answer rubs out one star per char it echoed.
	/// </remarks>
	public static int Backspace(StringBuilder line, bool masked) {
		ArgumentNullException.ThrowIfNull(line);

		if (line.Length == 0) {
			return 0;
		}

		string text = line.ToString();
		int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
		int from = starts.Length > 0 ? starts[^1] : text.Length - 1;
		string gone = text[from..];
		line.Length = from;

		if (masked) {
			return gone.Length;
		}

		// Emoji draw two columns wide in a terminal; anything else typed here, one.
		return gone.Any(char.IsSurrogate) || gone.Contains('\uFE0F', StringComparison.Ordinal) ? 2 : 1;
	}
}
