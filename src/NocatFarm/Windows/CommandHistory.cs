namespace NocatFarm.Windows;

/// <summary>
/// Up and down through the commands typed before, the way a terminal does it: up goes back one, down comes forward,
/// and coming forward past the newest gives back whatever was being typed before up was pressed. The same command
/// twice in a row is kept once. In memory only - nothing typed is written to disk.
/// </summary>
public sealed class CommandHistory {
	public const int Keep = 100;

	private readonly List<string> _lines = [];

	/// <summary>Where up/down has got to: -1 = not looking back, 0 = the newest.</summary>
	private int _at = -1;

	/// <summary>What was in the box when up was first pressed, for coming back down to.</summary>
	private string _draft = "";

	public void Add(string line) {
		if ((_lines.Count == 0) || (_lines[^1] != line)) {
			_lines.Add(line);

			if (_lines.Count > Keep) {
				_lines.RemoveAt(0);
			}
		}

		Reset();
	}

	public void Reset() {
		_at = -1;
		_draft = "";
	}

	/// <summary>The line to show after up (<paramref name="up"/>) or down; null when there's nowhere to go.</summary>
	public string? Step(bool up, string current) {
		if (up) {
			if (_at + 1 >= _lines.Count) {
				return null;   // already at the oldest (or nothing typed yet)
			}

			if (_at == -1) {
				_draft = current;
			}

			_at++;

			return _lines[_lines.Count - 1 - _at];
		}

		if (_at == -1) {
			return null;
		}

		_at--;

		return _at == -1 ? _draft : _lines[_lines.Count - 1 - _at];
	}
}

/// <summary>
/// Which question a line typed in the window is for, the way the console tells: the one up when its first key was pressed -
/// and whether one was up at any time while it was typed. Read at Enter only, a password typed for a question that was
/// answered from the dashboard (or given up) a moment before Enter found nothing up, and ran as a command: into the log,
/// on screen, and under the up arrow.
/// </summary>
public sealed class QuestionWatch {
	public enum Verdict { Run, Answer, Drop }

	private readonly Lock _gate = new();

	/// <summary>A line is being typed: a key has been pressed since the last Enter.</summary>
	private bool _typing;

	private Prompt.Question? _for;
	private bool _asked;

	/// <summary>A key in the command box. <paramref name="empty"/>: nothing in it yet, so this key starts a new line.</summary>
	public void Key(bool empty, Prompt.Question? up) {
		lock (_gate) {
			if (empty || !_typing) {
				_typing = true;
				_for = up;
				_asked = false;
			}

			_asked |= up != null;
		}
	}

	/// <summary>A question went up or came down - one up at any moment of the line counts, not only at a key.</summary>
	public void Changed(Prompt.Question? up) {
		lock (_gate) {
			_asked |= _typing && (up != null);
		}
	}

	/// <summary>
	/// Enter, with the question up now: answer it when the line was typed for it (or before any was up); run it when no
	/// question was up at any time; and otherwise drop it - typed for a question that has gone, it may be a password.
	/// </summary>
	public (Verdict What, Prompt.Question? Question) Enter(Prompt.Question? up) {
		lock (_gate) {
			Prompt.Question? typedFor = _typing ? _for : up;
			bool asked = (_typing && _asked) || (up != null);
			_typing = false;
			_for = null;
			_asked = false;

			if ((up != null) && ((typedFor == null) || (typedFor == up))) {
				return (Verdict.Answer, up);
			}

			return (asked ? Verdict.Drop : Verdict.Run, null);
		}
	}
}
