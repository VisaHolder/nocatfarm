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
