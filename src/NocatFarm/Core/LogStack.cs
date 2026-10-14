using System.Globalization;

namespace NocatFarm.Core;

/// <summary>
/// Lines in a row from one account that say the same kind of thing, shown as ONE row that updates in place: items turned
/// into gems, the "still ..." heartbeat, rep4rep comments. A look that turned 14 items into gems was 14 rows - in a small
/// window that's the whole log. Clicked, the row opens to show each line.
/// </summary>
/// <remarks>
/// Only the views stack - the window's log and the dashboard's Log tab. The log FILE keeps every line as it was.
/// Kept simple on purpose: never across accounts, and any other line in between ends the stack. A DEBUG line from the
/// same account doesn't end it (it's tucked inside, shown when opened): every item into gems has its reason as a DEBUG
/// line right before it, and with debug lines showing nothing would ever stack.
/// </remarks>
public static class LogStack {
	/// <summary>The kinds that stack.</summary>
	public const string Gems = "gems", Heartbeat = "status", Rep4Rep = "rep4rep", Update = "update";

	/// <summary>An item turned into gems - its line, as Log.Earned writes it (Core/SpareRun.cs).</summary>
	public const string GemsLine = "{0} ({1}) → {2} gems";

	/// <summary>Which sentence is which kind - by the sentence as written, so it's the same in every language.</summary>
	private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal) {
		[GemsLine] = Gems,
		// An update's countdown and sign-outs, ten lines in half a minute (Core/SelfUpdate.cs).
		["installing {0}"] = Update,
		["update · {0}MB copied, unpacking"] = Update,
		["update · {0}MB downloaded, unpacking"] = Update,
		["update · {0} ready, restart in {1}s"] = Update,
		["update in {0}s"] = Update,
		["update · signing out {0} {1}/{2}"] = Update,
		["update · restarting into {0}"] = Update,
		["commented on {0} and credited ({1}/{2} today)"] = Rep4Rep,
		["commented on {0}, not credited ({1}/{2} today)"] = Rep4Rep,
		["rep4rep {0}/{0} · next ~{1}"] = Rep4Rep,
	};

	/// <summary>The kind a line stacks as, or null for a line that never stacks.</summary>
	public static string? KindOf(Said said) => Kinds.GetValueOrDefault(said.English);

	/// <summary>The kind a logged line stacks as: the one it was written with (the heartbeat), else its sentence's.</summary>
	public static string? KindOf(Log.Entry entry) => entry.Kind ?? KindOf(entry.Said);

	/// <summary>What a line adds to its stack's count: the gems an item made. 0 for every other line.</summary>
	public static long ValueOf(Said said) =>
		(KindOf(said) == Gems) && (said.Arg(2) is { } v) && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace(",", "", StringComparison.Ordinal),
			NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;

	/// <summary>One row of a stacked view: a line, or a stack of lines of one kind from one account.</summary>
	public sealed class Row(Log.Entry first, string? kind) {
		/// <summary>Every line in it, in order - DEBUG lines tucked in from the same account too.</summary>
		public List<Log.Entry> Entries { get; } = [first];

		public string Source => Entries[0].Source;

		public string? Kind { get; } = kind;

		/// <summary>Opened by a click: each line shows under it.</summary>
		public bool Open { get; set; }

		/// <summary>The lines of its kind - what it counts.</summary>
		public IEnumerable<Log.Entry> Lines => Entries.Where(e => (Kind != null) && (KindOf(e) == Kind));

		/// <summary>Two or more lines of its kind: shown as one row. One alone shows as itself.</summary>
		public bool Stacked => Lines.Skip(1).Any();

		/// <summary>The newest line of its kind - its level and time are the row's.</summary>
		public Log.Entry Latest => Lines.LastOrDefault() ?? Entries[^1];

		/// <summary>What the row reads, closed or open: "turned 14 items into 1,080 gems", or the newest line itself.</summary>
		public Said Summary => Summarise(Kind, [.. Lines.Select(static e => e.Said)]);

		/// <summary>The row as the window draws it: "▸ turned 14 items into 1,080 gems", "▸ ×6 still idling ...".</summary>
		public string Header => $"{(Open ? "▾" : "▸")} {(Kind == Gems ? "" : $"×{Lines.Count()} ")}{Summary}";
	}

	/// <summary>A stack's line: gems added up, anything else the newest line.</summary>
	public static Said Summarise(string? kind, IReadOnlyList<Said> lines) => lines.Count == 0 ? default
		: kind == Gems ? new Said("turned {0} items into {1} gems", lines.Count, lines.Sum(ValueOf).ToString("N0", CultureInfo.InvariantCulture))
		: lines[^1];

	/// <summary>
	/// Adds a line to the rows: onto the last row when it's the same kind from the same account (or a DEBUG line from the
	/// account of a row that stacks), else as a row of its own. Returns whether it went onto the last row.
	/// </summary>
	public static bool Add(List<Row> rows, Log.Entry entry) {
		string? kind = KindOf(entry);

		if (rows.Count > 0) {
			Row last = rows[^1];
			bool same = (last.Kind != null) && string.Equals(last.Source, entry.Source, StringComparison.OrdinalIgnoreCase);

			if (same && ((kind == last.Kind) || ((kind == null) && (entry.Level == "DEBUG")))) {
				last.Entries.Add(entry);

				return true;
			}
		}

		rows.Add(new Row(entry, kind));

		return false;
	}

	/// <summary>
	/// What the rows show, top to bottom: a stack is one line (its header), and opened, each of its lines under it; a row
	/// whose kind has only one line so far shows each of its lines as they are. Depth 1 for a line inside an open stack.
	/// </summary>
	public static IEnumerable<(Row Row, Log.Entry Entry, int Depth, bool Head)> Shown(IEnumerable<Row> rows) {
		foreach (Row row in rows) {
			if (!row.Stacked) {
				foreach (Log.Entry e in row.Entries) {
					yield return (row, e, 0, false);
				}

				continue;
			}

			yield return (row, row.Latest, 0, true);

			if (row.Open) {
				foreach (Log.Entry e in row.Entries) {
					yield return (row, e, 1, false);
				}
			}
		}
	}
}
