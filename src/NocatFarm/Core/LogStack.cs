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
/// line right before it, and with debug lines showing nothing would ever stack. Exceptions (1.8.2): an account's status
/// row moves down with each new status line, so there's one per account, at the bottom, updating in place - every
/// event in between made it a fresh row, fifty a day; status rows are see-through, so a line finds its row past them (a
/// status line in the middle of 19 listings cut them in two); and a line of an answer finds its answer past whatever
/// came in while it was written.
///
/// 1.8.2: some rows read as one line that isn't the newest - their HEAD. An answer someone asked for (a 'buy preview' that
/// answers in the log, the daily summary) reads as its first line, with every other line under it when opened. A look's
/// total ("bought 5 card(s) · 0.31 CAD · 1 set(s) to craft") is its last line, and ends its stack: the next look is a row
/// of its own.
/// </remarks>
public static class LogStack {
	/// <summary>The kinds that stack.</summary>
	public const string Gems = "gems", Heartbeat = "status", Rep4Rep = "rep4rep", Update = "update", Bought = "bought", Listed = "listed", OnMarket = "market";

	/// <summary>What an answer's kind starts with - each answer has a kind of its own, so two in a row are two rows.</summary>
	public const string AnswerPrefix = "answer#";

	private static long _answers;

	/// <summary>A kind for one more answer (<see cref="Log.Answer(Said, IReadOnlyList{ValueTuple{string, Said}}, string, string)"/>).</summary>
	public static string NewAnswer() => AnswerPrefix + Interlocked.Increment(ref _answers).ToString(CultureInfo.InvariantCulture);

	/// <summary>Whether a kind is an answer's - read as its first line, never as the newest.</summary>
	public static bool IsAnswer(string? kind) => kind?.StartsWith(AnswerPrefix, StringComparison.Ordinal) == true;

	/// <summary>How many rows up a line looks for its own row, past status rows - or an answer's line past anything.</summary>
	private const int LookBack = 20;

	/// <summary>The most lines an account's status row keeps.</summary>
	private const int MaxStatus = 100;

	/// <summary>An item turned into gems - its line, as Log.Earned writes it (Core/SpareRun.cs).</summary>
	public const string GemsLine = "{0} ({1}) → {2} gems";

	/// <summary>A buying look's total - the head of the card lines before it (Modules/CardBuyer.cs).</summary>
	public const string BoughtTotal = "bought {0} card(s) · {1} · {2} set(s) to craft";

	/// <summary>A selling look's total - the head of the listing lines before it (Core/Seller.cs).</summary>
	public const string ListedTotal = "listed {0} item(s) · {1} if they all sell";

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
		["update: asking GitHub for {0}"] = Update,
		["update: asking GitHub what's newest"] = Update,
		["update: downloading {0} ({1}MB)"] = Update,
		["update: {0} {1}% · {2} of {3}MB · about {4}m left"] = Update,
		["update: {0} {1}% · {2} of {3}MB"] = Update,
		["update: {0} ready - restarting in {1}s"] = Update,
		["commented on {0} and credited ({1}/{2} today)"] = Rep4Rep,
		["commented on {0}, not credited ({1}/{2} today)"] = Rep4Rep,
		["rep4rep {0}/{0} · next ~{1}"] = Rep4Rep,
		// 1.8.2: a look that bought, listed or saw its listings go up - a card a line was the whole window (Modules/CardBuyer.cs,
		// Core/Seller.cs, Core/SpareRun.cs).
		["bought {0} ({1}) · {2}"] = Bought,
		[BoughtTotal] = Bought,
		["listed {0} ({1}) for {2} · you get {3}"] = Listed,
		[ListedTotal] = Listed,
		["{0} is on the market · {1}"] = OnMarket,
		["{0} is on the market"] = OnMarket,
	};

	/// <summary>The lines that are their stack's total: read as the row, and the end of it.</summary>
	private static readonly HashSet<string> Totals = new(StringComparer.Ordinal) { BoughtTotal, ListedTotal };

	/// <summary>The kind a line stacks as, or null for a line that never stacks.</summary>
	public static string? KindOf(Said said) => Kinds.GetValueOrDefault(said.English);

	/// <summary>The kind a logged line stacks as: the one it was written with (the heartbeat, an answer), else its sentence's.</summary>
	public static string? KindOf(Log.Entry entry) => entry.Kind ?? KindOf(entry.Said);

	/// <summary>Whether a line is its row's head: an answer's first line (written so), or a look's total.</summary>
	public static bool IsHead(Log.Entry entry) => entry.Head || Totals.Contains(entry.Said.English);

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

		/// <summary>The newest line of its kind - its time is the row's.</summary>
		public Log.Entry Latest => Lines.LastOrDefault() ?? Entries[^1];

		/// <summary>The line the row reads as when it isn't the newest: an answer's first line, a look's total. Null for the rest.</summary>
		public Log.Entry? Head => Kind == null ? null : IsAnswer(Kind) ? Lines.FirstOrDefault(static e => e.Head) : Lines.LastOrDefault(IsHead);

		/// <summary>A look's total came: the stack is done, and the next line of its kind starts a row of its own.</summary>
		public bool Closed => !IsAnswer(Kind) && (Head != null);

		/// <summary>
		/// The row's level, for its colour: a row with a head takes the most serious of its lines - a daily summary with
		/// something to look at in it is yellow, not green. Any other row, the newest line's.
		/// </summary>
		public string Level => Head == null ? Latest.Level : Lines.Select(static e => e.Level).MaxBy(Rank) ?? Latest.Level;

		/// <summary>What the row reads, closed or open: "turned 14 items into 1,080 gems", its head, or the newest line itself.</summary>
		public Said Summary => Head?.Said ?? Summarise(Kind, [.. Lines.Select(static e => e.Said)]);

		/// <summary>The row as the window draws it: "▸ turned 14 items into 1,080 gems", "▸ ×6 still idling ...", "▸ buy preview new · 4 set(s) for 1.76 CAD".</summary>
		public string Header => $"{(Open ? "▾" : "▸")} {((Kind == Gems) || (Head != null) ? "" : $"×{Lines.Count()} ")}{Summary}";
	}

	/// <summary>How serious a level is, for a row's colour.</summary>
	private static int Rank(string level) => level switch {
		"ERROR" => 4,
		"WARN" => 3,
		"GOOD" => 2,
		"INFO" => 1,
		_ => 0
	};

	/// <summary>A stack's line: gems added up, anything else the newest line.</summary>
	public static Said Summarise(string? kind, IReadOnlyList<Said> lines) => lines.Count == 0 ? default
		: kind == Gems ? new Said("turned {0} items into {1} gems", lines.Count, lines.Sum(ValueOf).ToString("N0", CultureInfo.InvariantCulture))
		: lines[^1];

	/// <summary>
	/// Adds a line to the rows: onto the last row when it's the same kind from the same account (or a DEBUG line from the
	/// account of a row that stacks), else as a row of its own. A line that stacks finds its row past status rows, and an
	/// answer's line its answer past anything a few rows up. Returns whether it went onto a row already there.
	/// </summary>
	/// <summary>Two status lines saying the same thing: what it's doing - the part before the first " · " ("farming cards on
	/// SOS OPS!", "settling in", "playing Counter-Strike 2") - minutes and counts aside.</summary>
	private static bool SameDoing(Said a, Said b) => string.Equals(Doing(a), Doing(b), StringComparison.Ordinal);

	private static string Doing(Said said) {
		string text = said.ToEnglish();
		int dot = text.IndexOf(" · ", StringComparison.Ordinal);

		return dot < 0 ? text : text[..dot];
	}

	public static bool Add(List<Row> rows, Log.Entry entry) {
		string? kind = KindOf(entry);

		if ((rows.Count > 0) && (kind == null) && (entry.Level == "DEBUG")) {
			Row last = rows[^1];

			if ((last.Kind != null) && string.Equals(last.Source, entry.Source, StringComparison.OrdinalIgnoreCase)) {
				last.Entries.Add(entry);

				return true;
			}
		}

		// The account's status row, wherever it is: brought down to the bottom with the new line.
		if (kind == Heartbeat) {
			int at = rows.FindLastIndex(r => (r.Kind == Heartbeat) && string.Equals(r.Source, entry.Source, StringComparison.OrdinalIgnoreCase));

			// Only while it's doing the same thing (the same sentence about the same game): settling in, then farming SOS OPS!,
			// then a break are each their own row - the one before stays where it was, so the day reads in order (1.8.2).
			if ((at >= 0) && !SameDoing(rows[at].Latest.Said, entry.Said)) {
				at = -1;
			}

			if (at >= 0) {
				Row row = rows[at];
				row.Entries.Add(entry);

				// A window left open for a month keeps the newest of them - one every ten minutes is 144 a day.
				if (row.Entries.Count > MaxStatus) {
					row.Entries.RemoveRange(0, row.Entries.Count - MaxStatus);
				}

				rows.RemoveAt(at);
				rows.Add(row);

				return true;
			}
		}

		if (kind != null) {
			for (int i = rows.Count - 1; (i >= 0) && (i >= rows.Count - LookBack); i--) {
				Row row = rows[i];

				if ((row.Kind == kind) && !row.Closed && string.Equals(row.Source, entry.Source, StringComparison.OrdinalIgnoreCase)) {
					row.Entries.Add(entry);

					return true;
				}

				// Past status rows, any account's; an answer's line past anything. Any other line in between, and it's news again.
				if (!IsAnswer(kind) && (row.Kind != Heartbeat)) {
					break;
				}
			}
		}

		rows.Add(new Row(entry, kind));

		return false;
	}

	/// <summary>
	/// What the rows show, top to bottom: a stack is one line (its header), and opened, each of its lines under it - all but
	/// its head, which the header already reads as; a row whose kind has only one line so far shows each of its lines as they
	/// are. Depth 1 for a line inside an open stack.
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
				Log.Entry? head = row.Head;

				foreach (Log.Entry e in row.Entries) {
					if (!ReferenceEquals(e, head)) {
						yield return (row, e, 1, false);
					}
				}
			}
		}
	}
}
