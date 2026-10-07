using System.Text;
using NocatFarm.Core;

namespace NocatFarm;

/// <summary>
/// The five-minute version.
///
/// Somebody who has just unzipped this has one question - "what do I type?" - and the answer should not be a
/// wiki. This walks the same path everybody actually takes, in order, and it knows what the machine in front of
/// it has already done: steps that are finished say so, and the next thing to do is always the thing at the top
/// that is not ticked.
/// </summary>
public static class Tutorial {
	private const string Topics = "cards, human, free, trades, rep4rep, achievements, tray";

	public static string Render(BotManager mgr, string? topic) {
		if (!string.IsNullOrWhiteSpace(topic)) {
			return Detail(topic.ToLowerInvariant());
		}

		IReadOnlyCollection<Bot> bots = mgr.All;

		bool hasAccount = bots.Count > 0;
		bool signedIn = bots.Any(static b => b.IsOnline);
		bool playsSomething = bots.Any(static b => (b.Cfg.IdleGames.Count > 0) || (b.Cfg.GameWeights.Length > 0) || b.Cfg.FarmCards);
		bool humanOn = bots.Any(static b => b.Cfg.LegitMode);

		// Commenting needs all three: the feature switched on, the token, and at least one account opted in.
		// Ticking this on the token alone said "done" for a setup that would never post a thing.
		bool rep4rep = mgr.Global.Rep4RepEnabled && mgr.Rep4Rep.HasToken && bots.Any(static b => b.Cfg.Rep4Rep);

		// Every other step asks "has ANY account done this". This one asked only the FIRST, so a fleet where
		// the custom name is set on the second and third accounts - which is the normal shape, since the one
		// you care about usually shows the real game - reported the step as never done.
		bool customName = bots.Any(static b => !string.IsNullOrEmpty(b.Cfg.CustomGameName));

		StringBuilder sb = new();
		sb.AppendLine("Getting started - the whole thing takes about five minutes.");
		sb.AppendLine();

		Step(sb, 1, hasAccount, "Add a Steam account",
			"add <name> <steamLogin>",
			"The name is yours - it labels the config file and it's what you type in commands. The login is what",
			"you type into Steam's sign-in box. Already using ArchiSteamFarm? 'import asf' brings every account",
			"across WITH its login token - no passwords, no Steam Guard codes.");

		// 'add' starts signing in straight away, so there is nothing to type here but the answers. And in this
		// window a pending question takes the next line typed AS the answer - telling people to type
		// 'answer <code>' would send the word "answer" to Steam as part of their password.
		Step(sb, 2, signedIn, "Let it sign in",
			"(it starts by itself - type each answer when it asks)",
			"The first time, it asks for the password and then a Steam Guard code. After that it keeps a login",
			"token and signs itself in, with no password stored anywhere. If the account has the mobile",
			"authenticator, drop its maFile into config/authenticators/ and it answers its own Guard prompts.",
			"Stopped it? 'start <name>' signs it back in.");

		Step(sb, 3, playsSomething, "Tell it what to play",
			"play <name> 730,440",
			"Those numbers are appIDs - the number in a game's Steam store URL. You can paste the whole URL.",
			"Card farming is already on by default, and cards come first: it works through everything with cards",
			"left, then falls back to idling this list. 'cards <name>' shows what's left.");

		Step(sb, 4, customName, "Optional - show a custom name",
			"gamename <name> whatever you like",
			"Your friends list shows that text instead of the real game, while the real games keep banking",
			"playtime underneath. Both at the same time.");

		Step(sb, 5, humanOn, "Optional - make it look like a person",
			"set <name> LegitMode true",
			"One game at a time, sittings of realistic length, breaks, meals, quiet days and offline overnight -",
			"and it hides the settings that would give it away. 'human <name> week' shows the week it rolled.",
			"This is the difference between an account that survives being looked at and one that doesn't.");

		Step(sb, 6, rep4rep, "Optional - earn rep4rep points",
			"set Rep4RepEnabled true",
			"rep4rep is off until you switch it on. Then paste your token from rep4rep.com (Settings there):",
			"  set Rep4RepApiToken <token>",
			"and opt accounts in one at a time with 'set <name> Rep4Rep true'. One token covers every account and",
			"they all feed one points pool. It paces itself well under Steam's comment ceiling on its own.");

		sb.AppendLine("Some things happen by themselves: free event items (sale stickers, 0-point Points Shop items) and");
		sb.AppendLine("gifts sent to the account are collected. 'tutorial free' covers those and the ones you switch on.");
		sb.AppendLine();
		sb.AppendLine("That's it. It runs in the tray - close the window and it keeps going.");
		sb.AppendLine();
		sb.AppendLine("  status            what everything is doing right now");
		sb.AppendLine("  config <name>     every setting for an account, with its value");
		sb.AppendLine("  help <setting>    what one setting means, e.g. help HoursUntilCardDrops");
		sb.Append($"  tutorial <topic>  more on: {Topics}");

		return sb.ToString();
	}

	private static void Step(StringBuilder sb, int number, bool done, string title, string command, params string[] lines) {
		sb.AppendLine($"  {(done ? "[done]" : "[ ]   ")} {number}. {title}");
		sb.AppendLine($"          {command}");

		foreach (string line in lines) {
			sb.AppendLine($"          {line}");
		}

		sb.AppendLine();
	}

	private static string Detail(string topic) => topic switch {
		"cards" or "farming" => string.Join(Environment.NewLine, [
			"Trading cards",
			"",
			"On by default. It reads your own badge pages, plays everything that still has cards - 32 at a time",
			"to get them past the playtime threshold, then one at a time to actually farm - and stops when a game",
			"is done. Drops arrive as a push from Steam, so a finished game is noticed in seconds, not on a timer.",
			"",
			"  cards <name>                    what's left, per game",
			"  drops <name>                    go for the next game's drops now, whatever the schedule says",
			"  set <name> FarmCards false      turn it off for one account",
			"  set <name> HoursUntilCardDrops 0   if the account has spent over $5 on Steam",
			"",
			"Craft the sets into badges too - that's what actually raises your Steam level:",
			"  set <name> CraftBadges true"
		]),

		"human" or "legit" => string.Join(Environment.NewLine, [
			"Human mode",
			"",
			"Plays like a person rather than a bot. One game at a time, sittings of believable length, short",
			"breaks and proper meal breaks, some days off entirely, and offline overnight - where it can still",
			"quietly bank hours while invisible.",
			"",
			"Cards come first, inside its day: while there are cards the card game is what its sittings play, and",
			"nights stay for the overnight games. To farm them at night instead, or any time:",
			"  set <name> FarmCardsWhen night          (or: any, or day to go back)",
			"",
			"  set <name> LegitMode true",
			"  set <name> GameWeights \"730:70, 440:20, 550:10\"     first game is the main one",
			"  human <name>                                        what it's doing and what it played today",
			"  human <name> week                                   the week it rolled, as a sample",
			"",
			"Trade offers, gifts and friend requests each wait a person's time before they're answered, and while the",
			"account sleeps they wait for morning - each with a fresh wait once it's up. To answer at night too:",
			"  set <name> ActOnlyWhileAwake false",
			"",
			"While this is on, the idle list is set aside (and put back exactly as it was if you turn it off), and",
			"farming while appearing offline is ignored."
		]),

		"free" or "gifts" or "boosters" => string.Join(Environment.NewLine, [
			"Free stuff",
			"",
			"On by default, on every account:",
			"  ClaimEventItems    the daily sticker during a Steam sale, and anything in the Points Shop at 0 points",
			"  AcceptGifts        Steam wallet gift cards and guest passes people send you",
			"  AcceptGiftedGames  games friends gift the account, added straight to its library",
			"Each gift waits its own 2-15 minutes first, most often nearer 2 (GiftDelayMinMinutes / GiftDelayMaxMinutes).",
			"",
			"One switch each, per account:",
			"  set <name> ClaimFree games            paid games given away free-to-keep, never free-to-play filler",
			"  set <name> BoosterPackGames 730,440   turn gems into booster packs for these games, one a day each",
			"  set <name> UnpackBoosterPacks true    open booster packs that land in the inventory",
			"",
			"  booster <name>                        gems, and which games can be made into packs",
			"  freegames all                         look for free games now",
			"  freeitems all                         look for free event items now",
			"",
			"A gift is never declined. Turn AcceptGiftedGames off to decide each gifted game yourself.",
			"While human mode has the account asleep, gifts, event items and booster packs wait for morning -",
			"'set <name> ActOnlyWhileAwake false' answers at night too."
		]),

		"trades" or "trading" => string.Join(Environment.NewLine, [
			"Trades",
			"",
			"  set <name> AcceptDonations true       accept offers that ask for NOTHING",
			"  set <name> TradeMasters 7656119...    accounts you own",
			"  set <name> AcceptFromMasters true     let those take items",
			"  send <name>                           sweep this account's cards to the first master",
			"  set <name> SendEveryHours 24          ...or do that by itself once a day",
			"  send <name> to <other> [types]        send items to any of your other accounts",
			"  set <name> TradeDelayMaxMinutes 30    each offer waits 2-30 minutes, most often nearer 2",
			"",
			"A donation is an offer where you give up nothing at all, so accepting one can never cost the account",
			"anything. Anything that asks for even one of your items is not a donation and is never auto-accepted",
			"on that rule - only accounts on your own masters list can take items.",
			"",
			"  set <name> AcceptFairCardSwaps true   one-for-one card swaps that only help your sets",
			"  fairswap <name> <offerID>             would it accept this offer - and if not, why not",
			"",
			"Fair swaps are what Steam Trade Matcher users send: ordinary trading cards only, one for one within",
			"each game, and never a swap that takes the last copy of a card. Giving cards away needs the account's",
			"mobile authenticator to confirm; without it, the swap waits for you on your phone.",
			"",
			"Games the account is banned in can't be traded. Put them in InventoryIgnoreGames, or let a send find",
			"them - a game Steam refuses while the others go through is added to that list by itself."
		]),

		"rep4rep" => string.Join(Environment.NewLine, [
			"rep4rep",
			"",
			"Posts the comments rep4rep assigns and claims the points. Off until you switch it on, and one API",
			"token covers every account.",
			"",
			"  set Rep4RepEnabled true          the whole feature, including its dashboard tab",
			"  set Rep4RepApiToken <token>      from rep4rep.com, under Settings",
			"  set <name> Rep4Rep true          per account",
			"  rep4rep                          summary",
			"  rep4rep tasks <name>             what's queued for one account",
			"",
			"Steam only lets an account comment on about 10 non-friends a day, and going past that gets it",
			"comment-banned for a day. The pacing here stays under that on its own: a hard daily cap counted",
			"across restarts, randomised gaps, and a commenting window so nothing posts at 4am."
		]),

		"achievements" or "cheevo" => string.Join(Environment.NewLine, [
			"Achievements",
			"",
			"  achievements <name> 730               what it has, easiest first, with how rare each one is",
			"  achievements <name> 730 unlock all    all of them, now",
			"  achievements <name> 730 unlock ACH_NAME   just one",
			"  achievements <name> 730 lock ACH_NAME     put one back",
			"",
			"Unlocking a game's whole list at once is permanent, stamped with one shared timestamp, and visible on",
			"the profile forever. For an account meant to look real, drip them instead:",
			"  set <name> UnlockAchievements true",
			"A few a day, easiest first, only in a game the account is actually playing."
		]),

		"tray" or "background" => string.Join(Environment.NewLine, [
			"Running in the background",
			"",
			"It lives in the notification area by the clock. Right-click for the menu; close the console window",
			"and everything keeps running.",
			"",
			"  set StartWithWindows true     launch when you sign in to Windows",
			"  set StartMinimized true       start straight to the tray",
			"  set KeepAwake true            stop the PC sleeping while nocat.farm is open",
			"",
			"The dashboard is the same product with a mouse, and it switches off:",
			"  set WebEnabled false          console only (restart to apply)"
		]),

		_ => $"No tutorial topic called '{topic}'. Try: {Topics}."
	};
}
