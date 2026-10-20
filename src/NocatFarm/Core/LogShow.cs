using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// What kind of thing a log line is about, so the global "Show in the app's log" chips can keep some kinds off the window
/// and the dashboard's Log tab. Only those two views filter: the log FILE keeps every line, warnings and errors always show,
/// and a line no rule knows shows too. Which lines are INFO and which DEBUG doesn't change - this only hides.
/// </summary>
/// <remarks>
/// A line is sorted by the sentence it was written as (its <see cref="Said.English"/>, the same in every language), like
/// <see cref="LogStack"/> sorts what stacks - not by hundreds of call sites each saying what they are. In order: what the line
/// was written as (an answer to a command, a trade), the account's status line, the sentence's rules below, the
/// notification topic it was sent under, the sentence with its values filled in (for a line built of parts, "{0} · {1}"),
/// and last where it came from - nocat.farm itself, the chats and the daily summary are app messages.
/// </remarks>
public static class LogShow {
	/// <summary>The kinds, each with its chip (a global switch, on by default).</summary>
	public const string Doing = "LogShowDoing", Drops = "LogShowDrops", Badges = "LogShowBadges", Buying = "LogShowBuying", Selling = "LogShowSelling",
		Trades = "LogShowTrades", Comments = "LogShowComments", Achievements = "LogShowAchievements", Free = "LogShowFree", Answers = "LogShowAnswers",
		App = "LogShowApp";

	/// <summary>Every kind, in the order the chips are drawn. Each is the name of its setting.</summary>
	public static readonly string[] All = [Doing, Drops, Badges, Buying, Selling, Trades, Comments, Achievements, Free, Answers, App];

	// A value in a rule: a placeholder in the sentence as written ("{0}"), or the value itself once filled in.
	private const string V = @"(?:\{\d+\}|.+?)";

	/// <summary>
	/// The rules, first match wins: a pattern on the sentence (anchored at its start unless it says otherwise), with
	/// <c>{x}</c> for a value. Read against the sentence as written - "bought {0} ({1}) · {2}" - so a game called "Trade
	/// Simulator" can't make a line a trade.
	/// </summary>
	private static readonly (Regex Rule, string Kind)[] Rules = Build([
		// ── answers to commands: from the chats, and the indented lines of a long answer ──
		("> ", Answers), ("  ", Answers), ("{x}: answered · {x} lines in the log file", Answers),

		// ── trades and card swaps ──
		("offer ", Trades), ("accepted offer ", Trades), ("declined offer ", Trades), ("cancelled the offer ", Trades), ("card swap", Trades),
		("confirmed trade offer ", Trades), ("confirmed: ", Trades), ("denied: ", Trades), ("confirmed {x} from the Authenticator page", Trades),
		("sending items to your main", Trades), ("its items are busy with a sale", Trades), ("scam watch: ", Trades),

		// ── free games and items, Steam Points, sale events (before buying: "wallet gift card" isn't the wallet line) ──
		("claimed ", Free), ("took a free ", Free), ("went through the discovery queue", Free), ("{x} activations in ", Free), ("free-game claims", Free),
		("gifted game added", Free), ("added a gifted game", Free), ("couldn't add a gifted game", Free), ("accepted a guest pass", Free), ("guest pass ", Free),
		("accepted a Steam wallet gift card", Free), ("couldn't accept a Steam wallet gift card", Free), ("wallet gift card", Free), ("{x} was gifted", Free),
		("{x} - trying again around ", Free), ("{x} - not trying again", Free), ("activated a queued key", Free), ("dropping a queued key", Free),
		("Steam Points", Free), ("spent {x} Steam Points", Free),

		// ── buying cards ──
		("bought ", Buying), ("would buy ", Buying), ("would have bought ", Buying), ("wallet ", Buying), ("it spends at most ", Buying),
		("buying waits ", Buying), ("Buying cards is off", Buying), ("buy preview", Buying), ("preview only - nothing is bought", Buying),
		("{x} not bought", Buying), ("{x} card purchase", Buying), ("an order for {x} was left open", Buying), ("{x}: a buy order for it", Buying),
		("more cards wait", Buying), ("{x} is next \\(", Buying), ("that's enough prices for one look", Buying), ("{x}: the price of {x} moved", Buying),
		("Steam didn't say for sure whether {x} was bought", Buying), ("level goal", Buying), ("level price", Buying), ("{x}: craft - buying cards is off", Buying),
		("{x}: {x} (foil )?cards worth more than their level", Buying),

		// ── badges, crafting, level ──
		("crafted ", Badges), ("{x} completed card set", Badges), ("{x}: crafted", Badges), ("{x}: not crafted yet", Badges), ("{x}: its cards are ", Badges),
		("selling the {x} (foil )?set instead of crafting", Badges), ("selling {x} {x} sets instead of crafting", Badges), ("{x} set complete - ", Badges),
		("{x} set: crafted", Badges), ("level plan ready", Badges), ("Steam level ", Badges), ("craft preview", Badges),

		// ── selling, listings, spare items, gems and packs ──
		("listed ", Selling), ("lowered ", Selling), ("sold ", Selling), ("{x} is on the market", Selling), ("{x} listings on the market", Selling),
		("(?:.*) → {x} gems$", Selling), ("turned {x} items into ", Selling), ("spare: ", Selling), ("Spare items is off", Selling), ("packed ", Selling),
		("{x} gems \\({x} tradable\\)", Selling), ("Use gems for the most levels", Selling), ("couldn't turn {x} into gems", Selling),
		("couldn't open ", Selling), ("opened ", Selling), ("made {x} pack", Selling), ("couldn't make a {x} booster pack", Selling),
		("couldn't pack gems", Selling), ("the {x} booster pack", Selling), ("Steam didn't answer about the {x} booster pack", Selling),
		("booster packs ", Selling), ("{x}: booster packs ", Selling), ("{x}: automatic booster packs", Selling), ("\"Make booster packs\"", Selling),
		("{x} is on \"Never open packs", Selling), ("on your keep list", Selling), ("{x} \\({x}\\): ", Selling), ("too many listings ", Selling),
		("Steam's market is unavailable", Selling), ("Steam's market is open again", Selling), ("confirmed market listing ", Selling),
		("{x} would pay you {x}, under your ", Selling), ("{x} \\({x}\\) x{x} · lowest ", Selling),

		// ── comments, friends and rep4rep ──
		("commented on ", Comments), ("comment on ", Comments), ("new comment", Comments), ("rep4rep", Comments), ("retry posted on ", Comments),
		("daily limit found", Comments), ("rate-limited at ", Comments), ("{x} doesn't take comments", Comments), ("the rep4rep hold", Comments),
		("accepted a friend request", Comments), ("turned down a friend request", Comments), ("ignored a friend request", Comments),
		("joined the Steam group", Comments), ("didn't join ", Comments), ("no Steam group at ", Comments), ("couldn't join ", Comments),
		("{x} invited you to play", Comments), ("auto-replied to ", Comments), ("command from ", Comments), ("answered {x} \\(", Comments),
		("message from ", Comments),

		// ── achievements ──
		("🏆", Achievements), ("achievements: ", Achievements), ("achievement boost", Achievements), ("done with {x}'s achievements", Achievements),
		("family member started ", Achievements), ("{x}: {x}% done", Achievements), ("unlocked ", Achievements), ("done - ", Achievements),
		("still going - {x}/{x} apps checked", Achievements), ("carried on ASF's achievement", Achievements), ("{x}: what's left ", Achievements),
		("{x}: achievements are ", Achievements), ("can't tell which achievements", Achievements), ("{x} achievement\\(s\\) from DLC", Achievements),
		("{x}: {x} achievement", Achievements),

		// ── card drops and farming ──
		("card dropped", Drops), ("last card dropped", Drops), ("{x} is done - no cards left", Drops), ("no cards left", Drops), ("nothing left to farm", Drops),
		("all cards in", Drops), ("farming {x} · ", Drops), ("{x} games, {x} cards to farm", Drops), ("drop run", Drops), ("{x}: all {x} card drop", Drops),
		("going for ", Drops), ("still going for ", Drops), ("{x} first: {x} card drop", Drops), ("stopped the {x} drop run", Drops),
		("grinding {x} for {x}, then back to normal", Drops), ("the cards aren't sent before logging out", Drops),

		// ── what it's doing: playing, idling, sittings, settling in, breaks, bed, the day's plan ──
		("grinding ", Doing), ("playing ", Doing), ("farming cards on ", Doing), ("idling", Doing), ("idle rotation", Doing), ("asleep", Doing),
		("awake", Doing), ("warming up", Doing), ("settling in", Doing), ("banking hours", Doing), ("done for the day", Doing), ("done grinding", Doing),
		("day off", Doing), ("taking today off", Doing), ("today: ", Doing), ("new plan for today", Doing), ("plan restored", Doing), ("a quiet spell", Doing),
		("a late one tonight", Doing), ("up late", Doing), ("up for one sitting", Doing), ("reconnected", Doing), ("last card from ", Doing),
		("carrying on with ", Doing), ("resuming the grind", Doing), ("seen you play on ", Doing), ("trying out ", Doing), ("reached {x}h in ", Doing),
		("waiting", Doing), ("you're on this account", Doing), ("account free again", Doing), ("the account is free again", Doing), ("paused", Doing),
		("resumed", Doing), ("starting · ", Doing), ("signing out in ", Doing), ("signed out", Doing), ("signed in", Doing), ("now showing", Doing),
		("now appearing", Doing), ("custom name slipped", Doing), ("building playtime", Doing), ("daily web refresh", Doing), ("{x} is playing {x} - joining in", Doing),
		("{x} is back on the overnight list", Doing), ("leaving {x} alone - ", Doing), ("{x} (is|are) past the refund window", Doing), ("not now - ", Doing),
		("(?:.*) · back in ~", Doing), ("(?:.*) · offline, back in ~", Doing), ("(?:.*) · that sitting's over", Doing), ("{x}: {x}'s copy is in use", Doing),
		("{x}: copy in use", Doing), ("'{x}' starts again in ", Doing), ("{x}: signing in", Doing),

		// ── updates and app messages ──
		("update", App), ("installing ", App), ("what's new in ", App), ("'update skip'", App), ("auto-update", App), ("{x} waits for bedtime", App),
		("dashboard", App), ("settings saved", App), ("global settings saved", App), ("backup", App), ("restor", App), ("Discord", App), ("Telegram", App),
		("plugin", App), ("\\[plugin\\]", App), ("{x} plugin\\(s\\) loaded", App), ("language set to ", App), ("shutting down", App), ("in the tray", App),
		("every account has finished", App), ("config removed", App), ("disabled in config", App), ("login token ", App), ("Steam Guard code sent", App),
		("encrypted ", App), ("made the login key", App), ("keep it with the config folder", App), ("keep the config folder", App), ("setting moved", App),
		("achievement ceiling", App), ("game shares moved", App), ("open from anywhere", App), ("firewall ", App), ("imported ", App), ("terminal can't redraw", App),
		("another copy starts with Windows", App), ("saved your settings before ", App), ("Steam's market is refusing", App), ("Steam's market answers", App),
		("bans on record", App), ("banned in ", App), ("account health", App), ("◈", App), ("// ", App), ("── ", App), ("nothing chosen", App),
		("{x}: Claim free games now also", App), ("{x}: the selling price setting", App), ("{x}: Bank hours overnight", App), ("{x}: alerts about bans", App),
		("{x}: your own accounts are in", App), ("buying cards: a spending limit", App)
	]);

	/// <summary>Each rule as a regex: anchored at the start, and {x} a value.</summary>
	private static (Regex, string)[] Build(IEnumerable<(string Pattern, string Kind)> rules) =>
		[.. rules.Select(static r => (new Regex("^" + r.Pattern.Replace("{x}", V, StringComparison.Ordinal), RegexOptions.CultureInvariant), r.Kind))];

	/// <summary>The notification topic a reward or a social line was sent under, as a kind - when no rule knows its sentence.</summary>
	private static string? OfTopic(Topic? topic) => topic switch {
		Topic.Cards => Drops,
		Topic.FreeStuff or Topic.Points => Free,
		Topic.Trades => Trades,
		Topic.Achievements => Achievements,
		Topic.Rep4Rep or Topic.Social => Comments,
		Topic.Items => Selling,
		Topic.Updates or Topic.Installs or Topic.Backups or Topic.Summary or Topic.Weekly => App,
		_ => null
	};

	/// <summary>Sentences already sorted (null: no rule knows it). Bounded: a sentence with its values baked in is new each time.</summary>
	private static readonly ConcurrentDictionary<string, string?> Known = new(StringComparer.Ordinal);

	/// <summary>The kind a sentence is by the rules, or null.</summary>
	public static string? OfSentence(string english) {
		if (Known.TryGetValue(english, out string? known)) {
			return known;
		}

		string? kind = null;

		foreach ((Regex rule, string k) in Rules) {
			if (rule.IsMatch(english)) {
				kind = k;

				break;
			}
		}

		if (Known.Count > 4000) {
			Known.Clear();
		}

		Known[english] = kind;

		return kind;
	}

	/// <summary>The kind of a logged line, or null when nothing says (it then always shows).</summary>
	public static string? KindOf(Log.Entry e) =>
		e.Show ?? (e.Kind == LogStack.Heartbeat ? Doing : null) ?? KindOf(e.Said, e.Source, e.About);

	/// <summary>The kind of a sentence from <paramref name="source"/>, or null.</summary>
	public static string? KindOf(Said said, string source, Topic? about = null) {
		if (OfSentence(said.English) is { } kind) {
			return kind;
		}

		if (OfTopic(about) is { } topical) {
			return topical;
		}

		// A line built of parts - "{0} · {1}", a status put together - is known by what it says once filled in.
		if (said.English.Contains('{', StringComparison.Ordinal) && (OfSentence(said.ToEnglish()) is { } filled)) {
			return filled;
		}

		return LogStack.KindOf(said) switch {
			LogStack.Gems => Selling,
			LogStack.Rep4Rep => Comments,
			LogStack.Update => App,
			_ => source is "nocat.farm" or "discord" or "telegram" or "report" or "plugins" ? App : null
		};
	}

	/// <summary>Whether a kind's chip is on.</summary>
	public static bool IsOn(GlobalConfig g, string kind) => kind switch {
		Doing => g.LogShowDoing,
		Drops => g.LogShowDrops,
		Badges => g.LogShowBadges,
		Buying => g.LogShowBuying,
		Selling => g.LogShowSelling,
		Trades => g.LogShowTrades,
		Comments => g.LogShowComments,
		Achievements => g.LogShowAchievements,
		Free => g.LogShowFree,
		Answers => g.LogShowAnswers,
		App => g.LogShowApp,
		_ => true
	};

	/// <summary>Whether the window and the dashboard show a line, under the settings in force now.</summary>
	public static bool Shows(Log.Entry e) => Shows(e, Live.Global);

	/// <summary>
	/// Whether the window and the dashboard show a line: warnings and errors always, the window's own lines (what was typed in it
	/// and its answer, sequence 0) always, a line of no known kind always, else as its kind's chip says.
	/// </summary>
	public static bool Shows(Log.Entry e, GlobalConfig g) =>
		(e.Level is "WARN" or "ERROR") || (e.Seq == 0) || !(KindOf(e) is { } kind) || IsOn(g, kind);

	/// <summary>Which chips are off, as one string - the window compares it to notice a change and redraw its log.</summary>
	public static string Off(GlobalConfig g) => string.Join(",", All.Where(k => !IsOn(g, k)));
}
