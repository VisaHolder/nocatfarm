using System.Text;

namespace NocatFarm;

/// <summary>
/// Questions that can be answered from either front end. There is exactly ONE thing reading the keyboard (the
/// console loop), so a prompt doesn't read input itself - it publishes the question and waits for an answer to
/// arrive, from the console or from the web UI, whichever gets there first.
/// </summary>
public static class Prompt {
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>The question currently waiting for an answer, or null.</summary>
	public static string? Pending { get; private set; }

	/// <summary>Who asked it, so stopping one account can't answer a different account's question.</summary>
	public static string? PendingOwner { get; private set; }

	/// <summary>Whether the pending answer is a secret, so the console stops echoing what is typed.</summary>
	public static bool PendingSecret { get; private set; }

	private static TaskCompletionSource<string>? _answer;

	/// <summary>Supply the answer to the pending prompt. Returns false when nothing was asked.</summary>
	public static bool Answer(string value) => _answer?.TrySetResult(value) ?? false;

	/// <summary>
	/// Abandon the pending question, but only if <paramref name="owner"/> is the one who asked it. Used when the
	/// thing that asked is being stopped - otherwise stopping an account mid-login would leave its password
	/// prompt on screen forever, or worse, blank-answer the prompt a DIFFERENT account was waiting on.
	/// </summary>
	private static readonly HashSet<string> Abandoned = new(StringComparer.OrdinalIgnoreCase);

	public static void Cancel(string? owner = null) {
		if (owner != null && PendingOwner != null && !string.Equals(owner, PendingOwner, StringComparison.OrdinalIgnoreCase)) {
			// Someone else's question is on screen. This owner may still be QUEUED behind it - remember that it
			// was cancelled, or its turn would come round and hang the prompt on a bot nobody is running.
			lock (Abandoned) {
				Abandoned.Add(owner);
			}

			return;
		}

		_answer?.TrySetResult("");
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

			_answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			Pending = question;
			PendingOwner = owner;
			PendingSecret = secret;

			Console.WriteLine();
			Console.Write($"  {question}: ");

			return (await _answer.Task.ConfigureAwait(false)).Trim();
		} finally {
			Pending = null;
			PendingOwner = null;
			PendingSecret = false;
			_answer = null;
			Gate.Release();
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

	/// <summary>Every occurrence of the digits following <paramref name="marker"/>, e.g. "gamecards/" -> appIDs.</summary>
	public static List<uint> UIntsAfter(string s, string marker) {
		List<uint> found = new();
		int i = 0;

		while (true) {
			int at = s.IndexOf(marker, i, StringComparison.Ordinal);

			if (at < 0) {
				return found;
			}

			int p = at + marker.Length;
			int start = p;

			while ((p < s.Length) && char.IsAsciiDigit(s[p])) {
				p++;
			}

			if ((p > start) && uint.TryParse(s.AsSpan(start, p - start), out uint v) && !found.Contains(v)) {
				found.Add(v);
			}

			i = at + marker.Length;
		}
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
}

public static class Fmt {
	/// <summary>Minutes as "3h20m" / "45m".</summary>
	public static string Hm(int minutes) {
		if (minutes < 60) {
			return minutes + "m";
		}

		return $"{minutes / 60}h{minutes % 60:00}m";
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
