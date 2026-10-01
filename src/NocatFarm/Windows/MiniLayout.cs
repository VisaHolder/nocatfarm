using System.Globalization;
using NocatFarm.Core;

namespace NocatFarm.Windows;

/// <summary>
/// Mini mode's arithmetic: which accounts get a row, how tall the window is, what the bar under the rows says and the
/// figure in the title bar.
/// </summary>
/// <remarks>
/// Kept apart from the drawing on purpose. The paint code can only be checked by looking at it; this can be checked by
/// the tests, on any OS and without a window - 30 accounts, a screen too short for them, the scroll at the end.
/// </remarks>
public static class MiniLayout {
	public const int TitleH = 30;
	public const int RowH = 30;

	/// <summary>A farming account's row: the line, then cards left and time left, then its progress bar.</summary>
	public const int FocusH = 58;

	/// <summary>The thin bar under the rows: "+7 more · 6 idling" closed, "12 accounts" open.</summary>
	public const int FooterH = 24;

	/// <summary>Rows shown while closed. Mini is for a glance - five lines is a glance, twelve is a list.</summary>
	public const int ClosedRows = 5;

	/// <summary>Rows shown at once while open. More than that scroll with the wheel.</summary>
	public const int OpenRows = 10;

	/// <summary>
	/// What gets drawn: rows <see cref="First"/> to First + <see cref="Count"/> - 1 of the ordered list, whether the bar
	/// is there, and the window's height. <see cref="Scroll"/> is the scroll as it was clamped, to keep.
	/// </summary>
	public sealed record Plan(int First, int Count, int Scroll, int MaxScroll, bool Footer, int Height, int Total) {
		/// <summary>Accounts with no row on screen right now.</summary>
		public int Hidden => Total - Count;
	}

	/// <summary>
	/// The accounts that need their owner first - a Steam Guard code, a sign-in that failed, the stuck alarm - and then
	/// everything else in the dashboard's order. Closed, mini shows five rows, and the one account you have to do
	/// something about must never be the sixth.
	/// </summary>
	public static List<T> ProblemsFirst<T>(IEnumerable<T> all, Func<T, bool> needsYou) {
		// Asked once each: whether an account needs you is read from live state, and asking twice could split one
		// account into both halves or neither.
		List<(T Item, bool Problem)> marked = [.. all.Select(a => (a, needsYou(a)))];

		return [.. marked.Where(static m => m.Problem).Select(static m => m.Item), .. marked.Where(static m => !m.Problem).Select(static m => m.Item)];
	}

	/// <summary>
	/// Which rows fit and how tall that makes the window. <paramref name="heights"/> is every row's height in the order
	/// they're shown; <paramref name="maxHeight"/> is the work area of the screen the window is on, which the window is
	/// never taller than - on a short screen fewer rows show and the rest scroll.
	/// </summary>
	public static Plan Fit(IReadOnlyList<int> heights, bool open, int scroll, int maxHeight) {
		int n = heights.Count;
		int ceiling = Math.Max(TitleH, maxHeight);

		if (n == 0) {
			// One line to say there are no accounts yet.
			return new Plan(0, 0, 0, 0, false, Math.Min(TitleH + RowH, ceiling), 0);
		}

		bool footer = n > ClosedRows;
		int cap = open ? OpenRows : ClosedRows;
		int room = ceiling - TitleH - (footer ? FooterH : 0);

		// The furthest the list can scroll: as many of the last rows as fit together. Closed never scrolls - it's the top
		// five, so the problems sorted there are always the ones in view.
		int fromEnd = 0, endUsed = 0;

		for (int i = n - 1; (i >= 0) && (fromEnd < cap); i--) {
			if ((fromEnd > 0) && (endUsed + heights[i] > room)) {
				break;
			}

			endUsed += heights[i];
			fromEnd++;
		}

		int maxScroll = open ? n - fromEnd : 0;
		scroll = Math.Clamp(scroll, 0, maxScroll);

		// Rows from the scroll down, while they fit. Always at least one, however short the screen.
		int count = 0, used = 0;

		for (int i = scroll; (i < n) && (count < cap); i++) {
			if ((count > 0) && (used + heights[i] > room)) {
				break;
			}

			used += heights[i];
			count++;
		}

		int height = Math.Min(TitleH + used + (footer ? FooterH : 0), ceiling);

		return new Plan(scroll, count, scroll, maxScroll, footer, height, n);
	}

	/// <summary>
	/// The bar under the rows, in pieces: the numbers are drawn a little bolder than the words. Closed it counts the
	/// accounts that have no row by what they're doing - "+7 more · 6 idling · 1 asleep", the biggest group first - so
	/// the rest of the fleet is still a glance. Open it's "12 accounts", and a click closes it again.
	/// </summary>
	/// <param name="groups">Every row's <see cref="BotStatus.Group"/>, in the order the rows are shown.</param>
	public static List<(string Text, bool Strong)> Footer(Plan plan, bool open, IReadOnlyList<string> groups) {
		List<(string, bool)> parts = [];

		if (open) {
			parts.AddRange(Parts(Loc.T("{0} accounts"), plan.Total));

			return parts;
		}

		parts.AddRange(Parts(Loc.T("+{0} more"), plan.Hidden));

		IEnumerable<string> hidden = groups.Where((_, i) => (i < plan.First) || (i >= plan.First + plan.Count));

		foreach (IGrouping<string, string> g in hidden.GroupBy(static g => g)
			.OrderByDescending(static g => g.Count())
			.ThenBy(static g => (uint) Array.IndexOf(GroupOrder, g.Key))) {   // an unknown key (-1) sorts last as uint
			parts.AddRange(Parts(Counted(g.Key), g.Count()));
		}

		return parts;
	}

	/// <summary>The bar's text as one string - what the checks compare.</summary>
	public static string Text(IEnumerable<(string Text, bool Strong)> parts) => string.Concat(parts.Select(static p => p.Text));

	/// <summary>The dashboard's order of the status chips, for a tie between two groups of the same size.</summary>
	private static readonly string[] GroupOrder =
		["farming", "idling", "online", "connecting", "needsyou", "problem", "playing", "break", "done", "dayoff", "nightidle", "asleep", "off"];

	/// <summary>
	/// "6 idling", as a template. The dot in front makes these keys their own: "{0} idling" is already a sentence about
	/// ONE named account ("kylro idling"), which other languages put in the singular - "6 idlet" in German.
	/// </summary>
	private static string Counted(string group) => group switch {
		"farming" => Loc.T(" · {0} farming"),
		"idling" => Loc.T(" · {0} idling"),
		"online" => Loc.T(" · {0} online"),
		"connecting" => Loc.T(" · {0} connecting"),
		"needsyou" => Loc.T(" · {0} need you"),
		"problem" => Loc.T(" · {0} can't sign in"),
		"playing" => Loc.T(" · {0} playing"),
		"break" => Loc.T(" · {0} on a break"),
		"done" => Loc.T(" · {0} done for today"),
		"dayoff" => Loc.T(" · {0} on a day off"),
		"nightidle" => Loc.T(" · {0} night idling"),
		"asleep" => Loc.T(" · {0} asleep"),
		_ => Loc.T(" · {0} off")
	};

	/// <summary>A translated template split round its number, so the number can be drawn on its own.</summary>
	private static List<(string, bool)> Parts(string template, int value) {
		string number = value.ToString(CultureInfo.InvariantCulture);
		int at = template.IndexOf("{0}", StringComparison.Ordinal);

		if (at < 0) {
			return [(template, false)];   // a translation that lost its number still says its words
		}

		List<(string, bool)> parts = [];

		if (at > 0) {
			parts.Add((template[..at], false));
		}

		parts.Add((number, true));

		if (at + 3 < template.Length) {
			parts.Add((template[(at + 3)..], false));
		}

		return parts;
	}

	// ── the figure in the title bar ─────────────────────────────────────────────
	/// <summary>The choices of the "Mini window shows" setting, by number.</summary>
	public const int StatNothing = 0;
	public const int StatHoursToday = 1;
	public const int StatHoursWeek = 2;
	public const int StatHoursMonth = 3;
	public const int StatCards = 4;
	public const int StatAchievements = 5;
	public const int StatComments = 6;
	public const int StatOnline = 7;
	public const int StatInventory = 8;

	/// <summary>
	/// The one figure in the title bar, for these accounts: "192h today", "3 cards today", "2/3 online", "$41.20
	/// inventory" - or "" for nothing.
	/// </summary>
	/// <remarks>
	/// Hours are game-hours, read from the same day-by-day history and with the same rule as the daily summary and the
	/// Discord card: every game running counts, the way Steam credits them, so 8 games for a whole day is 192 hours. Today
	/// is today on this PC's clock; the week and the month are the last 7 and 30 days, today included. Cards, comments
	/// and achievements are the last 24 hours, like "cards today" always was here.
	/// </remarks>
	public static string Stat(int pick, IReadOnlyCollection<Bot> bots) {
		switch (pick) {
			case StatHoursToday:
				return new Said("{0} today", Hours(History.MinutesOver(1, bots.Select(static b => b.Name)))).ToString();

			case StatHoursWeek:
				return new Said("{0} past week", Hours(History.MinutesOver(7, bots.Select(static b => b.Name)))).ToString();

			case StatHoursMonth:
				return new Said("{0} past month", Hours(History.MinutesOver(30, bots.Select(static b => b.Name)))).ToString();

			case StatCards: {
				int n = Count(Stats.KindCard);

				return (n == 1 ? new Said("1 card today") : new Said("{0} cards today", n)).ToString();
			}

			case StatAchievements: {
				int n = Count(Stats.KindAchievement);

				return (n == 1 ? new Said("1 achievement today") : new Said("{0} achievements today", n)).ToString();
			}

			case StatComments: {
				int n = Count(Stats.KindComment);

				return (n == 1 ? new Said("1 comment today") : new Said("{0} comments today", n)).ToString();
			}

			case StatOnline:
				return new Said("{0}/{1} online", bots.Count(static b => b.IsOnline), bots.Count).ToString();

			case StatInventory:
				return new Said("{0} inventory", PriceBook.Symbol + bots.Sum(static b => b.Inventory.Total).ToString("N2", CultureInfo.InvariantCulture)).ToString();

			default:
				return "";
		}

		static int Count(string kind) => Stats.Recent(24).Count(e => e.Kind == kind);
	}

	/// <summary>"192h", "40m" - the daily summary's own way of writing hours, and "0h" rather than nothing at all.</summary>
	private static string Hours(double minutes) => ReportCard.Hours(minutes) is { Length: > 0 } h ? h : "0h";
}
