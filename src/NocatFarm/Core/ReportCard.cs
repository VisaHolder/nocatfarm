using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>
/// The daily and weekly summaries, laid out the way the owner's site bot lays out its own: the name and version, the
/// date, then short sections of "◈ label: value" lines - "◈ kylro: 268h played · 8 comments".
/// </summary>
/// <remarks>
/// They used to be a table in a code block - "kylro          banked 268h02m · 0 card(s) · 8 comment(s) · 11868h06m
/// total" - lined up for the log and a grey wall on a phone, every zero spelled out. Now the numbers are rounded to
/// what anyone reads (268h, not 268h02m), a part that is nothing is left out instead of saying "0 card(s)", and an
/// account that did nothing at all just says "off".
///
/// Built once, then drawn three ways from the same parts so they can't drift apart: Telegram HTML and Discord
/// markdown (both in Notifier), and plain lines for the log and the 'report' command (<see cref="Lines"/>). The
/// values are Saids, so the log's copy changes language with everything else on screen.
///
/// To reach Discord and Telegram it rides the same road as every other event - Log.Publish, a line of text - written
/// as one line of JSON behind <see cref="WirePrefix"/>, translated as it was the moment it went out (<see cref="Wire"/>).
/// </remarks>
public sealed class ReportCard {
	/// <summary>"Daily summary" - the first line in the log. Discord and Telegram say it in their own heading.</summary>
	public Said Title { get; init; }

	/// <summary>"2026-10-01", or the week's first and last day.</summary>
	public Said Date { get; init; }

	/// <summary>When it was made: "05:00 NDT".</summary>
	public string Time { get; init; } = "";

	public List<Section> Sections { get; init; } = [];

	/// <summary>"// ACCOUNTS · last 24h" and the lines under it.</summary>
	public sealed record Section(Said Name, Said Note, List<Row> Rows);

	/// <summary>
	/// One "◈ label: value" line. <paramref name="Strong"/> makes the value bold - a real figure, as against "off";
	/// <paramref name="Alert"/> adds the site bot's " !" for something that wants a look.
	/// </summary>
	public sealed record Row(Said Label, Said Value, bool Strong = true, bool Alert = false);

	// ── the parts every row is made of ──────────────────────────────────────

	/// <summary>
	/// Game-minutes as anyone would say them: whole hours ("268h"), minutes under an hour ("45m"), and "" for none at
	/// all so the part is left out. Minutes past the hour stopped meaning anything once a day's total ran to hundreds.
	/// </summary>
	public static string Hours(double minutes) {
		long m = (long) Math.Round(Math.Max(0, minutes), MidpointRounding.AwayFromZero);

		return m == 0 ? "" : m < 60 ? $"{m}m" : $"{(long) Math.Round(minutes / 60, MidpointRounding.AwayFromZero)}h";
	}

	/// <summary>"1 card", "3 cards" - or nothing, for none.</summary>
	public static Said? Cards(int n) => n <= 0 ? null : n == 1 ? new Said("{0} card", n) : new Said("{0} cards", n);

	/// <summary>"1 comment", "8 comments" - or nothing, for none.</summary>
	public static Said? Comments(int n) => n <= 0 ? null : n == 1 ? new Said("{0} comment", n) : new Said("{0} comments", n);

	/// <summary>The parts that are something, joined with " · " - null when none of them are.</summary>
	public static Said? Join(IEnumerable<Said?> parts) {
		object?[] some = [.. parts.Where(static p => p is { IsEmpty: false }).Cast<object?>()];

		// The frame is only placeholders, so it reads the same in every language and each part translates itself.
		return some.Length == 0 ? null : new Said(string.Join(" · ", some.Select(static (_, i) => "{" + i + "}")), some);
	}

	/// <summary>A row whose value is the parts that are something, or a plain <paramref name="none"/> when nothing is.</summary>
	public static Row Figures(Said label, IEnumerable<Said?> parts, Said none) =>
		Join(parts) is { } value ? new Row(label, value) : new Row(label, none, Strong: false);

	// ── what needs a look ───────────────────────────────────────────────────

	/// <summary>
	/// The "// ATTENTION" lines: an account that can't sign in or is waiting on a code, one the stuck alarm has gone off
	/// for, one Steam is turning comments away from, a new ban - and a new version waiting. Only what is true right now,
	/// so it never repeats yesterday's trouble once it's over. <paramref name="newBan"/> says which accounts gained a ban
	/// since the last summary (the weekly one has no "since" to compare with, so it passes none).
	/// </summary>
	internal static List<Row> Attention(IEnumerable<Bot> bots, bool r4r, Func<Bot, bool>? newBan, DateTime nowUtc) {
		List<Row> rows = [];

		foreach (Bot b in bots) {
			if (!b.Cfg.Enabled) {
				continue;
			}

			List<Said?> wrong = [Trouble(b, nowUtc)];

			if (newBan?.Invoke(b) == true) {
				wrong.Add(new Said("new ban on record"));
			}

			if (r4r && (BotManager.ModuleOf<Modules.Rep4RepModule>(b) is { CommentBlocked: true })) {
				wrong.Add(new Said("Steam is blocking its comments"));
			}

			if (Join(wrong) is { } said) {
				rows.Add(new Row(new Said("{0}", b.Name), said, Alert: true));
			}
		}

		// Not one the owner said to skip ('update skip'): that's been answered, and it was in every summary after it.
		if ((UpdateCheck.Available is { } version) && !UpdateCheck.IsSkipped(version)) {
			rows.Add(new Row(new Said("New version"), new Said("{0} is out", version), Alert: true));
		}

		return rows;
	}

	/// <summary>
	/// What's wrong with an account that only its owner can fix - it can't sign in, it's waiting for a Steam Guard code
	/// or a password, or the stuck alarm has gone off - or null when nothing is. One answer for the summaries' "needs a
	/// look" and the mini window, which puts these accounts at the top in yellow.
	/// </summary>
	internal static Said? Trouble(Bot b, DateTime nowUtc) =>
		b.State == BotState.Failed ? new Said("can't sign in")
		// The question can be up a moment before the state says so - the dashboard already counts that as "needs you".
		: (b.State == BotState.NeedsGuard) || (b.GuardPrompt != null) ? new Said("waiting for a Steam Guard code or password")
		: StuckWatch.StuckFor(b.Name, nowUtc) is { } stuck ? new Said("no hours for {0}", Fmt.Rough((int) stuck.TotalMinutes))
		: null;

	/// <summary>The "// SYSTEM" lines: which version this is and how long it has been running.</summary>
	internal static Section System() => new(new Said("System"), default, [
		new Row(new Said("Version"), new Said("{0}", Build.Version)),
		new Row(new Said("Uptime"), new Said("{0}", Uptime()))
	]);

	/// <summary>How long the app has been running: "1d 2h 47m", "3h 5m".</summary>
	internal static string Uptime() {
		TimeSpan up = DateTime.Now - Process.GetCurrentProcess().StartTime;

		return up.TotalDays >= 1 ? $"{(int) up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";
	}

	// ── the clock ───────────────────────────────────────────────────────────

	/// <summary>A day as the summaries write it: 2026-10-01 - the same in every language, and never mistaken for 10 January.</summary>
	internal static string Day(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>"05:00 NDT" - the local time and the zone it's in, so a summary read on another continent isn't misread.</summary>
	internal static string Clock(DateTime local) =>
		local.ToString("HH:mm", CultureInfo.InvariantCulture) + " " + Zone(TimeZoneInfo.Local, local);

	/// <summary>
	/// The zone's short name - NDT, CEST, BST - or its offset from UTC ("UTC+5:30") where there's no short name to be
	/// had. Linux hands over the short names itself; Windows only has long ones ("Newfoundland Standard Time"), and
	/// guessing from their initials goes wrong ("W. Europe" is CET, not WEST), so the common ones are listed here.
	/// </summary>
	internal static string Zone(TimeZoneInfo zone, DateTime local) {
		bool summer = zone.IsDaylightSavingTime(local);
		string name = summer ? zone.DaylightName : zone.StandardName;

		if ((name.Length is >= 2 and <= 5) && name.All(char.IsAsciiLetterUpper)) {
			return name;
		}

		// Summer by the clock, not by the zone's own flag: Ireland's time is written the other way round (summer is its
		// standard time, winter the "daylight" one), so the flag says winter in July there. Every zone listed is north of
		// the equator, so summer is whichever of January and July is ahead.
		if (Short.TryGetValue(zone.Id, out (string Winter, string Summer) known)) {
			TimeSpan january = zone.GetUtcOffset(new DateTime(local.Year, 1, 15, 12, 0, 0, DateTimeKind.Unspecified));
			TimeSpan july = zone.GetUtcOffset(new DateTime(local.Year, 7, 15, 12, 0, 0, DateTimeKind.Unspecified));
			bool ahead = (july != january) && (zone.GetUtcOffset(local) == (july > january ? july : january));

			return ahead ? known.Summer : known.Winter;
		}

		TimeSpan off = zone.GetUtcOffset(local);
		TimeSpan abs = off.Duration();

		return "UTC" + (off < TimeSpan.Zero ? "-" : "+") + (abs.Minutes == 0 ? $"{abs.Hours}" : $"{abs.Hours}:{abs.Minutes:00}");
	}

	/// <summary>Short names for the zones Windows only names in full - by its zone id, and by the IANA ids for the same places.</summary>
	private static readonly Dictionary<string, (string Winter, string Summer)> Short = ByZone([
		(["Newfoundland Standard Time", "America/St_Johns"], "NST", "NDT"),
		(["Atlantic Standard Time", "America/Halifax"], "AST", "ADT"),
		(["Eastern Standard Time", "America/New_York", "America/Toronto", "America/Detroit"], "EST", "EDT"),
		(["Central Standard Time", "America/Chicago", "America/Winnipeg"], "CST", "CDT"),
		(["Mountain Standard Time", "America/Denver", "America/Edmonton"], "MST", "MDT"),
		(["US Mountain Standard Time", "America/Phoenix"], "MST", "MST"),
		(["Pacific Standard Time", "America/Los_Angeles", "America/Vancouver"], "PST", "PDT"),
		(["Alaskan Standard Time", "America/Anchorage"], "AKST", "AKDT"),
		(["Hawaiian Standard Time", "Pacific/Honolulu"], "HST", "HST"),
		// Windows' one id is London, Dublin and Lisbon together; the IANA ones tell them apart.
		(["GMT Standard Time", "Europe/London"], "GMT", "BST"),
		(["Europe/Dublin"], "GMT", "IST"),
		(["Europe/Lisbon", "Atlantic/Madeira", "Atlantic/Canary"], "WET", "WEST"),
		(["W. Europe Standard Time", "Romance Standard Time", "Central Europe Standard Time", "Central European Standard Time",
			"Europe/Berlin", "Europe/Paris", "Europe/Amsterdam", "Europe/Madrid", "Europe/Rome", "Europe/Warsaw", "Europe/Prague"], "CET", "CEST"),
		(["FLE Standard Time", "GTB Standard Time", "E. Europe Standard Time", "Europe/Helsinki", "Europe/Kyiv", "Europe/Athens", "Europe/Bucharest"], "EET", "EEST"),
		(["UTC", "Etc/UTC", "Coordinated Universal Time"], "UTC", "UTC")
	]);

	private static Dictionary<string, (string, string)> ByZone(IEnumerable<(string[] Ids, string Winter, string Summer)> zones) {
		Dictionary<string, (string, string)> map = new(StringComparer.OrdinalIgnoreCase);

		foreach ((string[] ids, string winter, string summer) in zones) {
			foreach (string id in ids) {
				map[id] = (winter, summer);
			}
		}

		return map;
	}

	// ── plain lines: the log, the console, the 'report' command ─────────────

	/// <summary>
	/// The card as plain lines, no markup - "── Daily summary · 2026-10-01 · 05:00 NDT ──", "// ACCOUNTS · last 24h",
	/// "◈ kylro: 268h played · 8 comments". Each is a Said, so the log re-reads it in whatever language is picked.
	/// </summary>
	public IEnumerable<(Said Line, Row? Row)> Lines() {
		yield return (new Said("── {0} · {1} · {2} ──", Title, Date, Time), null);

		foreach (Section s in Sections) {
			Section section = s;

			// Upper case when it's read, not now, so a heading still follows a change of language.
			yield return (section.Note.IsEmpty
				? new Said("// {0}", (Func<string>) (() => section.Name.ToString().ToUpperInvariant()))
				: new Said("// {0} · {1}", (Func<string>) (() => section.Name.ToString().ToUpperInvariant()), section.Note), null);

			foreach (Row r in section.Rows) {
				yield return (r.Alert ? new Said("◈ {0}: {1} !", r.Label, r.Value) : new Said("◈ {0}: {1}", r.Label, r.Value), r);
			}
		}
	}

	/// <summary>Into the log a line at a time: the heading in green, what wants a look in yellow, the rest plain.</summary>
	public void WriteToLog(string source) {
		bool heading = true;

		foreach ((Said line, Row? row) in Lines()) {
			if (heading) {
				Log.Good(line, source);
			} else if (row is { Alert: true }) {
				Log.Warn(line, source);
			} else {
				Log.Info(line, source);
			}

			heading = false;
		}
	}

	/// <summary>The whole card as text, one line under another - what 'report' and 'report week' answer with.</summary>
	public string Text() => string.Join(Environment.NewLine, Lines().Select(static l => l.Line.ToString()));

	// ── on the way to Discord and Telegram ──────────────────────────────────

	/// <summary>What a published line starts with when it's a whole card, not a sentence.</summary>
	public const string WirePrefix = "\u001Enocat-card ";

	private sealed record WireRow(string Label, string Value, bool Strong, bool Alert);

	private sealed record WireSection(string Name, string Note, List<WireRow> Rows);

	private sealed record WireCard(string Title, string Date, string Time, List<WireSection> Sections);

	/// <summary>The card as one line for Log.Publish, every word in the language picked right now.</summary>
	public string Wire() => WirePrefix + JsonSerializer.Serialize(new WireCard(Title.ToString(), Date.ToString(), Time,
		[.. Sections.Select(static s => new WireSection(s.Name.ToString(), s.Note.ToString(),
			[.. s.Rows.Select(static r => new WireRow(r.Label.ToString(), r.Value.ToString(), r.Strong, r.Alert))]))]));

	/// <summary>A published line back into a card - null when it's an ordinary sentence (or not a card that can be read).</summary>
	internal static ReportCard? Read(string line) {
		if (!line.StartsWith(WirePrefix, StringComparison.Ordinal)) {
			return null;
		}

		try {
			if (JsonSerializer.Deserialize<WireCard>(line.AsSpan(WirePrefix.Length)) is not { } w) {
				return null;
			}

			// Already in words: "{0}" passes each one through as it is.
			static Said As(string? s) => string.IsNullOrEmpty(s) ? default : new Said("{0}", s);

			return new ReportCard {
				Title = As(w.Title),
				Date = As(w.Date),
				Time = w.Time ?? "",
				Sections = [.. (w.Sections ?? []).Select(static s => new Section(As(s.Name), As(s.Note),
					[.. (s.Rows ?? []).Select(static r => new Row(As(r.Label), As(r.Value), r.Strong, r.Alert))]))]
			};
		} catch (JsonException) {
			return null;
		}
	}
}
