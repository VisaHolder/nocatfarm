using System.Text;
using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm.Windows;

/// <summary>
/// The console as a board rather than a firehose.
///
/// A scrolling log answers "what happened" but never "what is it doing", and with several accounts running it
/// buries the answer under lines you have already read. This paints one row per account and repaints it in
/// place, so the screen always shows the present tense: who is playing what, for how much longer, and how much
/// of today is left. The log still exists - the last few lines sit underneath, and the whole thing is in the
/// file and on the dashboard - but it is no longer the main event.
///
/// Everything is drawn with ANSI escapes rather than Console.SetCursorPosition, because that fights with
/// anything else writing to the same handle and flickers badly over a redraw this frequent.
/// </summary>
public sealed class LiveConsole : IDisposable {
	private const int LogLines = 6;

	private readonly BotManager _mgr;
	private readonly Lock _paint = new();
	private readonly List<string> _recent = [];
	private CancellationTokenSource? _cts;
	private string _input = "";
	private int _lastHeight;
	private int _paintFailures;
	private bool _enabled;

	public LiveConsole(BotManager mgr) => _mgr = mgr;

	/// <summary>True once the board owns the screen, so the ordinary log printer stays out of the way.</summary>
	public bool Active => _enabled;

	public void Start() {
		if (!TryEnableAnsi()) {
			Log.Info("this terminal can't do live redrawing - keeping the plain scrolling log");

			return;
		}

		_enabled = true;
		Log.Suppressed = true;
		Log.Written += OnLogged;

		foreach (Log.Entry entry in Log.Recent(LogLines)) {
			_recent.Add(Format(entry));
		}

		_cts = new CancellationTokenSource();
		CancellationToken ct = _cts.Token;

		_ = Task.Run(async () => {
			// One second is fast enough to feel live and slow enough to cost nothing. Countdowns are shown in
			// minutes, so there is nothing on screen that changes faster than this.
			while (!ct.IsCancellationRequested) {
				try {
					Paint();
					_paintFailures = 0;
				} catch (Exception e) {
					// A resize mid-paint can throw and the next tick redraws fine. But if painting keeps failing
					// the user is staring at a frozen screen with the log suppressed behind it, which is far worse
					// than no board at all - so give up and hand the console back.
					if (++_paintFailures >= 5) {
						Dispose();
						Log.Warn($"the live view kept failing to draw ({e.GetType().Name}) - back to the plain log");

						return;
					}
				}

				try {
					await Task.Delay(1000, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					return;
				}
			}
		}, ct);
	}

	/// <summary>The line being typed, so a repaint doesn't wipe it out from under the user.</summary>
	public void SetInput(string text) {
		_input = text;
		Paint();
	}

	/// <summary>
	/// Show what a command answered.
	///
	/// The board repaints over the same rows every second, so anything simply written to the console would be
	/// gone before it could be read. This scrolls the answer out ABOVE the board and starts the board again
	/// underneath it, which is the only way both can share one screen.
	/// </summary>
	public void Show(string command, string output) {
		lock (_paint) {
			_lastHeight = 0;   // the board is about to be pushed up; do not overwrite where it used to be

			StringBuilder sb = new();
			sb.Append("\x1b[2K").Append("  > ").Append(command).Append('\n');

			foreach (string line in output.Replace("\r\n", "\n").Split('\n')) {
				sb.Append("\x1b[2K").Append("  ").Append(line).Append('\n');
			}

			sb.Append('\n');
			Console.Out.Write(sb.ToString());
			Console.Out.Flush();
		}

		Paint();
	}

	private void OnLogged(Log.Entry entry) {
		lock (_recent) {
			_recent.Add(Format(entry));

			while (_recent.Count > LogLines) {
				_recent.RemoveAt(0);
			}
		}

		Paint();
	}

	private static string Format(Log.Entry entry) =>
		$"{entry.When:HH:mm:ss} {Log.Bar} {Log.Pad(entry.Source, 10)} {Log.Bar} {entry.Text}";

	// ── painting ────────────────────────────────────────────────────────────
	private void Paint() {
		if (!_enabled) {
			return;
		}

		lock (_paint) {
			int width = Math.Max(48, SafeWidth());
			StringBuilder sb = new();

			// Back to the top of the block we drew last time, then redraw over it. Clearing the whole screen
			// every second makes the text visibly strobe; overwriting in place does not.
			//
			// height - 1, NOT height. The last line drawn is the prompt, deliberately left without a newline so
			// the cursor sits where the user is typing - which means the cursor is already ON the block's last
			// row, not below it. Moving up the full height would climb one row per repaint and chew through
			// whatever was on screen above.
			if (_lastHeight > 1) {
				sb.Append("\x1b[").Append(_lastHeight - 1).Append('F');
			} else if (_lastHeight == 1) {
				sb.Append('\r');
			}

			int height = 0;

			void Line(string text) {
				sb.Append("\x1b[2K").Append(Trim(text, width)).Append('\n');
				height++;
			}

			Line(Dim($"  nocatFarm {Version()}") + new string(' ', Math.Max(2, width - 22 - Version().Length)) + Dim($"{DateTime.Now:HH:mm:ss}"));
			Line("");

			IReadOnlyCollection<Bot> bots = _mgr.All;

			if (bots.Count == 0) {
				Line(Dim("  no accounts yet - type 'tutorial'"));
			} else {
				int nameWidth = Math.Clamp(bots.Max(static b => b.Name.Length), 6, 14);

				// Column headings, so every figure below has a name against it. Without them the board was a row
				// of bare numbers you had to already know the meaning of.
				Line(Dim($"  {Log.Pad("ACCOUNT", nameWidth)}  {Log.Pad("DOING", 30)}  {Log.Pad("THIS SITTING", 24)}  {Log.Pad("TODAY", 16)}  REP4REP"));

				foreach (Bot bot in bots) {
					Line(Row(bot, nameWidth));
				}
			}

			Line("");

			lock (_recent) {
				foreach (string entry in _recent) {
					Line(Dim("  " + entry));
				}

				for (int i = _recent.Count; i < LogLines; i++) {
					Line("");
				}
			}

			// The prompt is drawn last and left without a newline, so the cursor ends where the user is typing.
			sb.Append("\x1b[2K").Append("  > ").Append(_input);
			height++;

			_lastHeight = height;
			Console.Out.Write(sb.ToString());
			Console.Out.Flush();
		}
	}

	/// <summary>
	/// One line describing what an account is doing, in the present tense and in plain words.
	///
	/// The order matters: the most specific true thing wins. "online" is what an account is when there is
	/// nothing better to say about it, not a thing worth saying when it is mid-session on Counter-Strike.
	/// </summary>
	private static string Row(Bot bot, int nameWidth) {
		string name = Colour(bot, Log.Pad(bot.Name, nameWidth));

		if (!bot.IsOnline) {
			return $"  {name}  {Dim(bot.StatusText)}";
		}

		// Worked out once in BotStatus and painted here. The bars and the dimming are this board's business;
		// deciding WHAT is true is not, and doing both in two places is exactly how the two boards drifted.
		BotStatus s = BotStatus.Of(bot);

		string doing = s.Doing;
		string sitting = s.Sitting;
		string today = "";

		if (s.SessionTotal > 0) {
			sitting = $"{Bar(s.SessionDone, s.SessionTotal)} {sitting}";
		} else if (sitting.Length > 0) {
			sitting = Dim(sitting);
		}

		if (s.DayTotal > 0) {
			today = $"{Bar(s.DayDone, s.DayTotal)} {Fmt.Hm(s.DayDone)}/{Fmt.Hm(s.DayTotal)}";
		} else if (s.Today.Length > 0) {
			today = s.Today is "nothing to farm" ? Dim(s.Today) : s.Today;
		}

		if (s.Persona.Length > 0) {
			doing += Dim("  (" + s.Persona + ")");
		}

		if (s.Warning.Length > 0) {
			doing += "[33m  " + s.Warning + "[0m";
		}

		string said = s.Comments.Length > 0 ? s.Comments : Dim("-");

		return $"  {name}  {Pad(doing, 30)}  {Pad(sitting, 24)}  {Pad(today, 16)}  {said}";
	}

	/// <summary>A small progress bar. Eight cells reads as a fraction without eating the row.</summary>
	private static string Bar(int done, int total) {
		const int Cells = 8;
		int filled = total <= 0 ? 0 : Math.Clamp((int) Math.Round((double) done / total * Cells), 0, Cells);

		return "[36m" + new string('#', filled) + "[90m" + new string('.', Cells - filled) + "[0m";
	}

	/// <summary>Pad to a column width counting only VISIBLE characters, so colour codes can't skew the layout.</summary>
	private static string Pad(string text, int width) {
		int visible = Visible(text);

		return visible >= width ? Trim(text, width) : text + new string(' ', width - visible);
	}

	private static int Visible(string text) {
		int count = 0;
		bool escape = false;

		foreach (char c in text) {
			if (escape) {
				if (char.IsLetter(c)) {
					escape = false;
				}

				continue;
			}

			if (c == '') {
				escape = true;

				continue;
			}

			count++;
		}

		return count;
	}

	private static string Colour(Bot bot, string name) {
		// Signed out and paused keep their own colours whatever the account is set to: those are states you
		// need to see at a glance, and losing them to a per-account colour would be a bad trade.
		if (!bot.IsOnline) {
			return "\x1b[90m" + name + "\x1b[0m";
		}

		if (bot.Paused || bot.PlayingBlocked) {
			return "\x1b[33m" + name + "\x1b[0m";
		}

		return (NameColour.Of(bot.Cfg.LogColour)?.Ansi ?? "\x1b[36m") + name + "\x1b[0m";
	}

	private static string Dim(string text) => "\x1b[90m" + text + "\x1b[0m";

	/// <summary>Trim to the terminal width WITHOUT counting the escape sequences, which take no screen columns.</summary>
	private static string Trim(string text, int width) {
		int visible = 0;
		bool escape = false;

		for (int i = 0; i < text.Length; i++) {
			if (escape) {
				if (char.IsLetter(text[i])) {
					escape = false;
				}

				continue;
			}

			if (text[i] == '\x1b') {
				escape = true;

				continue;
			}

			if (++visible > width) {
				return text[..i] + "\x1b[0m";
			}
		}

		return text;
	}

	private static int SafeWidth() {
		try {
			return Console.WindowWidth - 1;
		} catch {
			return 100;
		}
	}

	private static string Version() =>
		typeof(LiveConsole).Assembly.GetName().Version?.ToString(3) ?? "";

	// ── terminal setup ──────────────────────────────────────────────────────
	/// <summary>
	/// Turn on VT processing. Windows consoles have understood ANSI since Windows 10, but not by default, and
	/// without this the escapes are printed as literal garbage - which is far worse than a plain log.
	/// </summary>
	private static bool TryEnableAnsi() {
		try {
			if (Console.IsOutputRedirected) {
				return false;   // piped to a file: escapes would end up in the file
			}

			if (!OperatingSystem.IsWindows()) {
				return true;
			}

			return NativeConsole.EnableVirtualTerminal();
		} catch {
			return false;
		}
	}

	public void Dispose() {
		if (!_enabled) {
			return;
		}

		_enabled = false;
		Log.Written -= OnLogged;
		Log.Suppressed = false;

		try {
			_cts?.Cancel();
			_cts?.Dispose();
		} catch {
			// already gone
		}

		try {
			Console.Out.Write("\x1b[0m\n");
		} catch {
			// the console may already be gone on the way out
		}
	}
}
