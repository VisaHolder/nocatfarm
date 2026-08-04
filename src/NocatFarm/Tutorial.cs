using System.Text;
using NocatFarm.Config;
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
	public static string Render(BotManager mgr, string? topic) {
		if (!string.IsNullOrWhiteSpace(topic)) {
			return Detail(topic.ToLowerInvariant());
		}

		IReadOnlyCollection<Bot> bots = mgr.All;

		bool hasAccount = bots.Count > 0;
		bool signedIn = bots.Any(static b => b.IsOnline);
		bool playsSomething = bots.Any(static b => (b.Cfg.IdleGames.Count > 0) || (b.Cfg.GameWeights.Length > 0) || b.Cfg.FarmCards);
		bool humanOn = bots.Any(static b => b.Cfg.LegitMode);
		bool rep4rep = mgr.Rep4Rep.HasToken;

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
			"you type into Steam's sign-in box. It asks for the password once, then remembers a login token, so",
			"the password never has to be stored. Already using ArchiSteamFarm? 'import asf' brings every account",
			"across WITH its login token - no passwords, no Steam Guard codes.");

		Step(sb, 2, signedIn, "Sign it in",
			"start <name>",
			"First time only, it'll ask for the password and a Steam Guard code. After that it signs itself in.",
			"If the account has the mobile authenticator, drop its maFile into config/authenticators/ and it will",
			"answer its own Guard prompts forever.");

		Step(sb, 3, playsSomething, "Tell it what to play",
			"play <name> 730,440",
			"Those numbers are appIDs - the number in a game's Steam store URL. You can paste the whole URL.",
			"Card farming is already on by default, and cards come first: it works through everything with cards",
			"left, then falls back to idling this list. 'cards <name>' shows what's left.");

		Step(sb, 4, customName, "Optional - show a custom name",
			"name <name> whatever you like",
			"Your friends list shows that text instead of the real game, while the real games keep banking",
			"playtime underneath. Both at the same time.");

		Step(sb, 5, humanOn, "Optional - make it look like a person",
			"set <name> LegitMode true",
			"One game at a time, sittings of realistic length, breaks, meals, quiet days and offline overnight -",
			"and it hides the settings that would give it away. 'human <name> week' shows the week it rolled.",
			"This is the difference between an account that survives being looked at and one that doesn't.");

		Step(sb, 6, rep4rep, "Optional - earn rep4rep points",
			"set Rep4RepApiToken <token from rep4rep.com>",
			"Then 'set <name> Rep4Rep true' per account. One token covers however many accounts you run and they",
			"all feed one points pool. It paces itself well under Steam's comment ceiling on its own.");

		sb.AppendLine();
		sb.AppendLine("That's it. It runs in the tray - close the window and it keeps going.");
		sb.AppendLine();
		sb.AppendLine("  status            what everything is doing right now");
		sb.AppendLine("  config <name>     every setting for an account, with its value");
		sb.AppendLine("  help <setting>    what one setting means, e.g. help HoursUntilCardDrops");
		sb.Append("  tutorial <topic>  more on: cards, human, rep4rep, trades, achievements, tray");

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
			"  set <name> LegitMode true",
			"  set <name> GameWeights \"730:70, 440:20, 550:10\"     first game is the main one",
			"  human <name>                                        what it's doing and what it played today",
			"  human <name> week                                   the week it rolled, as a sample",
			"",
			"The settings that would give an account away are hidden AND cleared while this is on, and put back",
			"exactly as they were if you turn it off."
		]),

		"rep4rep" => string.Join(Environment.NewLine, [
			"rep4rep",
			"",
			"Posts the comments rep4rep assigns and claims the points. One API token covers every account.",
			"",
			"  set Rep4RepApiToken <token>      from rep4rep.com, under Settings",
			"  set <name> Rep4Rep true          per account",
			"  rep4rep                          summary",
			"  rep4rep tasks                    what's queued",
			"",
			"Steam only lets an account comment on about 10 non-friends a day, and going past that gets it",
			"comment-banned for a day. The pacing here stays under that on its own: a hard daily cap counted",
			"across restarts, randomised gaps, and a commenting window so nothing posts at 4am."
		]),

		"trades" or "trading" => string.Join(Environment.NewLine, [
			"Trades",
			"",
			"  set <name> AcceptDonations true       accept offers that ask for NOTHING",
			"  set <name> TradeMasters 7656119...    accounts you own",
			"  set <name> AcceptFromMasters true     let those take items",
			"  send <name>                           sweep this account's cards to the first master",
			"",
			"A donation is an offer where you give up nothing at all, so accepting one can never cost the account",
			"anything. Anything that asks for even one of your items is not a donation and is never auto-accepted",
			"on that rule - only accounts on your own masters list can take items."
		]),

		"achievements" or "cheevo" => string.Join(Environment.NewLine, [
			"Achievements",
			"",
			"  cheevo <name> 730                     what it has, easiest first, with how rare each one is",
			"  cheevo <name> 730 unlock all          all of them, now",
			"  cheevo <name> 730 unlock ACH_NAME     just one",
			"  cheevo <name> 730 lock ACH_NAME       put one back",
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
			"  set KeepAwake true            stop the PC sleeping while accounts are running",
			"",
			"The dashboard is the same product with a mouse, and it switches off:",
			"  set WebEnabled false          console only (restart to apply)"
		]),

		_ => $"No tutorial topic called '{topic}'. Try: cards, human, rep4rep, trades, achievements, tray."
	};
}
