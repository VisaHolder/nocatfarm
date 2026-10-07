using System.Globalization;
using System.Text;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Modules;
using NocatFarm.Rep4Rep;

namespace NocatFarm;

/// <summary>
/// One command, described once - the console's help and the dashboard's command list read this.
///
/// Aliases matter more than they look: the dispatcher has always accepted short forms, but nothing listed them,
/// so a chunk of the command set was undiscoverable unless you read the source.
/// </summary>
/// <param name="HiddenAliases">
/// Names that still reach the command but aren't listed: older names ('name' is 'gamename' now, 'unlock' is 'dashboard
/// unlock') and ArchiSteamFarm's ('loot', 'bots'). Kept so muscle memory, scripts and plugins don't break - listed, they
/// only made the list longer and two commands look alike.
/// </param>
public sealed record CommandDef(string Name, string Args, string Group, string Help, string Aliases = "", string HiddenAliases = "") {
	/// <summary>"status|s" for the listing - the real name first, then the short forms. Hidden aliases are left out.</summary>
	public string Display => Aliases.Length == 0 ? Name : Name + "|" + Aliases;

	public bool Matches(string typed) =>
		Name.Equals(typed, StringComparison.OrdinalIgnoreCase)
		|| $"{Aliases}|{HiddenAliases}".Split('|', StringSplitOptions.RemoveEmptyEntries).Any(a => a.Equals(typed, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The command set, shared verbatim by the console and the dashboard's command box - so anything you can type
/// in the terminal you can also type in the browser, and both produce the same text back.
/// </summary>
public static partial class Commands {
	// The groups the command list is shown in - help, the dashboard, Telegram's /help and the docs - in this order.
	public const string GroupAccounts = "Accounts";
	public const string GroupPlaying = "Playing";
	public const string GroupCards = "Trading cards";
	public const string GroupTrades = "Trades & items";
	public const string GroupGuard = "Steam Guard";
	public const string GroupAchievements = "Achievements";
	public const string GroupFree = "Free stuff & keys";
	public const string GroupInfo = "Profile & info";
	public const string GroupRep4Rep = "rep4rep";
	public const string GroupSettings = "Settings";
	public const string GroupOther = "The app";

	/// <summary>Last in every list: commands for checking and fixing things by hand, which the settings already do by themselves.</summary>
	public const string GroupAdvanced = "Advanced";

	/// <summary>Every command. This is the only list - <c>help</c> and /api/commands both render it.</summary>
	public static readonly IReadOnlyList<CommandDef> All = [
		new("status", "[account]", GroupAccounts, "What everything is doing right now.", "s", "bots"),
		new("start", "<account|all>", GroupAccounts, "Log an account in."),
		new("stop", "<account|all>", GroupAccounts, "Log an account out. It stays configured."),
		new("restart", "<account|all>", GroupAccounts, "Stop then start again."),
		new("pause", "<account|all> [minutes]", GroupAccounts, "Stay logged in but stop playing, farming and commenting. Give it minutes and it picks back up by itself."),
		new("resume", "<account|all>", GroupAccounts, "Undo a pause."),
		new("add", "<name> <steamLogin|qr> [human|robot]", GroupAccounts, "Add an account. It asks for the password once, then remembers a login token. Or 'qr' signs it in by scanning a code on the dashboard with the Steam app, no password at all. End with 'human' for your main (human mode) or 'robot' for a farm account. It says which it made."),
		new("remove", "<account>", GroupAccounts, "Delete an account and its stored login token.", "delete"),
		new("enable", "<account>", GroupAccounts, "Let this account log in again."),
		new("disable", "<account>", GroupAccounts, "Keep the account configured but never log it in."),
		new("stuck", "", GroupAccounts, "The stuck-account alarm: when each account last banked hours, whether it's counting as stuck (and why not, when it isn't), and any automatic restart. The StuckAlarm setting switches it on or off.", "alarm"),

		new("play", "<account> <appIDs|none>", GroupPlaying, "Set the games this account idles for playtime."),
		new("gamename", "<account> [text|off]", GroupPlaying, "Custom non-Steam game name shown instead of the real game. No text shows the current one; 'off' clears it.", "", "name"),
		new("persona", "<account> [state]", GroupPlaying, "What the account shows your friends: online | offline | busy | away | snooze | looking to trade | looking to play | invisible (or its number, 0-7). Same as the OnlineStatus setting. No state says what it shows now."),
		new("nickname", "<account> <profile name>", GroupPlaying, "Change the name everybody sees on the profile and friends list. Not the custom game name, that's 'gamename'."),
		new("grind", "<account|all> <appID> <hours> | <account> off", GroupPlaying,
			"Put an account on one game for a set number of hours, then let it go back to whatever it was doing. Outranks human mode while it runs."),
		new("human", "[account] [week|reroll]", GroupPlaying, "What human mode is doing today, and what it played. Add 'week' to see the next seven days, or 'reroll' to throw today's plan away and roll a fresh one from the current settings."),
		new("habits", "[account] [forget]", GroupPlaying, "What human mode has learned from you playing on the account yourself (days seen, the hours you're usually on, your top games), and whether \"Learn from how I play\" is using it yet. 'habits <account> forget' wipes it and it starts learning again."),
		new("wake", "<account>", GroupPlaying, "Wake a sleeping human-mode account and start its day now. Bed time is unchanged.", "wakeup|skipsleep"),
		new("hours", "<account>", GroupPlaying, "How the account's hour targets are going: hours so far, what's left, and the pace needed to make a date."),
		new("rotation", "<account> [next]", GroupPlaying, "The idle rotation: whether it's on, how many games are on the list, which batch is idling and when the next one takes over, and a look at the next batch. 'next' moves on to the next batch now."),
		new("selfcheck", "[account]", GroupPlaying, "Does a human-mode account look like a bot? A score out of 100 from what other people can see (hours on the profile, what its status shows, comments), with the setting that fixes each tell. Boost accounts are left out unless you name one.", "tells"),

		new("cards", "[account]", GroupCards, "What is still left to farm, and about how long it will take."),
		new("drops", "<account> [appID|next] [count|all] | <account> off", GroupCards,
			"You pick a game and how many cards, and it goes first. On a human-mode account it's played in the normal sittings - the main game's share of them, with breaks and bedtime - until that many have dropped. On other accounts it's played non-stop until then. Without an appID, the next game with cards. Automatic card farming (\"When to farm cards\") needs no command."),
		new("match", "[do]", GroupCards, "Swap duplicate trading cards between your own accounts so sets finish. Only swaps that help both sides, never a card already on an offer. Shows what it would trade; 'match do' sends the offers, and the other account accepts them by itself."),
		new("sell", "<account> [preview|do|relist] [count]", GroupCards, "Spare trading cards on the market: 'preview' (the default) shows what it would list and what you'd get after Steam's fees, 'do' lists them (5 by default), 'relist' takes down week-old listings the market has gone under. SellDuplicates does it by itself."),
		new("booster", "[account|all] | <account> <appIDs>", GroupCards, "Gems, and which games can be made into booster packs now. With appIDs it makes those packs straight away; the \"Make booster packs for\" setting does it by itself every day.", "boosters"),
		new("levelup", "<account> <level>", GroupCards, "What reaching a Steam level would cost: the XP missing, badges it can craft from its own cards, sets it has nearly finished, and the cheapest complete sets on the market for the rest, priced gently in the background.", "lvlup"),

		new("offers", "[account|all]", GroupTrades, "Live trade offers, straight from Steam: what's waiting to be accepted, what's been sent, and anything stuck on a confirmation or a trade hold."),
		new("trade", "accept|decline <account> <number|all> | cancel <account> <offer id|all>", GroupTrades, "Answer a trade offer yourself, by the number 'offers' and the announcements give it. Accepting one that sends items out confirms it too when this account's authenticator is in nocat.farm. You asked, so that is the confirmation. 'trade cancel' takes back offers the account sent that haven't gone through, such as one stuck waiting on a confirmation."),
		new("send", "<account|all> [to <account>] [types]", GroupTrades, "Send an account's tradable items to the account listed under Trades, or 'to' another of your accounts. Types as in the send setting (cards, foils, backgrounds, emoticons, boosters, gems, all); leave them off for what the send setting says, or trading cards when sending 'to' an account.", "", "loot"),

		new("2fa", "[account]", GroupGuard, "Show this account's Steam Guard code, if its authenticator is set up here. Without an account, every account's code.", "guard"),
		new("confirmations", "[account]", GroupGuard, "What's waiting to be confirmed on this account, like the Steam app's list: trades, market listings, account changes. They're numbered for confirm and deny. Needs the account's authenticator in nocat.farm."),
		new("confirm", "<account> <number|all>", GroupGuard, "Confirm what 'confirmations' listed under that number, or all of it."),
		new("deny", "<account> <number|all>", GroupGuard, "Deny (cancel) what 'confirmations' listed under that number, or all of it."),

		new("achievements", "<account> <appID> [list|unlock|lock] [name|all]", GroupAchievements, "A game's achievements: see them, unlock them all, or put them back.", "ach|cheevo"),
		new("hunt", "[account]", GroupAchievements, "What the achievement boost would hunt next, in order, and what it ruled out and why."),
		new("dlc", "<account> [game] | leave|undo <account> <game>", GroupAchievements, "Achievements that come with add-ons (DLC). 'dlc <account>' lists the games where add-ons matter. Add a game, by name or appID, to see its add-ons, which ones the account owns, and what is held. Achievements Steam ties to an add-on the account doesn't own are never unlocked. When a game has add-ons the account doesn't own and Steam doesn't say which achievements they bring, it earns anyway: the base game's first, the ones it can't place last. 'dlc leave <account> <game>' leaves a game like that alone instead, and 'dlc undo <account> <game>' takes that back.", "dlcach|dlcachievements"),

		new("freegames", "[account|all]", GroupFree, "Look for free games now: paid games given away free to keep, the same check \"Claim free games\" makes by itself every half hour or so. Says what it claimed, and what was free but couldn't be claimed, and why. On an account with it off, it takes games only."),
		new("freeitems", "[account|all]", GroupFree, "Look for free event items now: the daily sale sticker, and anything in the Points Shop at 0 points. The ClaimEventItems setting does it by itself."),
		new("queue", "[account|all]", GroupFree, "Go through today's discovery queue now, a few seconds on each game. The DiscoveryQueue setting does it by itself once a day (during sales, by default)."),
		new("redeem", "[account] <key|file.txt> [key...]", GroupFree, "Activate product keys, or point it at a text file full of them. More than five queues itself and activates them slowly. With an account, only that account ever gets them, queued ones too. Without one it tries each account in turn until one can use it."),
		new("keys", "[list|clear]", GroupFree, "Product keys waiting to be activated. A big batch queues itself rather than burning Steam's per-account activation allowance all at once."),

		new("value", "[account|all] [refresh]", GroupInfo, "What each inventory is worth, by game, and how it has moved in the last day. Add 'refresh' to read the inventories again.", "inv|inventory"),
		new("level", "[account|all]", GroupInfo, "Each account's Steam level."),
		new("balance", "[account|all]", GroupInfo, "Steam wallet balance, and anything still pending.", "wallet"),
		new("points", "[account|all]", GroupInfo, "Steam points each account can spend in the Points Shop."),
		new("bans", "[account|all]", GroupInfo, "Look up the account's bans now: VAC, game bans, a trade ban, a community ban, and which games it's banned in when Steam shows that. Read-only. It also checks by itself every few hours."),
		new("owns", "<appID|name>", GroupInfo,
			"Which accounts already own a game, and how long each has played it. Takes an appID, a store URL, or part of a name."),
		new("privacy", "<account> [public|friends|private|part=level ...]", GroupInfo,
			"See an account's profile privacy, or set it - one word for everything, or parts such as inventory=public comments=friends. Parts: profile, games, playtime, friends, inventory, gifts, comments."),
		new("joingroup", "<account|all> <group link or name>", GroupInfo, "Join a Steam group now if it's open, on one account or all of them. For a group every account should always be in, put it in the \"Groups every account joins\" setting instead."),

		new("rep4rep", "status|points|profiles|tasks|now|pause|resume|clear|rest", GroupRep4Rep, "Everything rep4rep. Run it bare for a summary. To switch it on or off for an account: set <account> Rep4Rep on|off.", "r4r"),

		new("config", "[account] [all]", GroupSettings, "Show the settings and their current values. Add 'all' to include the advanced ones."),
		new("set", "[account] <key> <value>", GroupSettings, "Change a setting. Without an account name it changes a global one."),
		new("backup", "", GroupSettings, "Save a backup zip of your settings, accounts, saved logins, authenticators and history into the backups folder next to config, and say where. Restoring one is done in the dashboard: Settings, Backup & restore."),
		new("import", "<asf|ime|idlemaster|hourboostr|singleboostr|sgi|steamidler|auto> [path] [force]", GroupSettings, "Bring accounts and settings across from another idler, ArchiSteamFarm login tokens and all."),

		new("log", "[count|folder]", GroupOther, "The last few log lines. 'log folder' opens the folder the log files are in, on this PC.", "logs"),
		new("stats", "[hours]", GroupOther, "Everything: each account's last 24 hours (hours banked, cards, comments, totals), then cards dropped and comments posted, by hour. Give a number of hours to look further back."),
		new("report", "[week]", GroupOther, "The short one: just each account's last 24 hours, the daily summary - the first part of 'stats'. 'report week' is the weekly report: the last seven days next to the seven before (hours banked, cards, cards listed, inventory value, comments).", "", "weekly"),
		new("notify", "[test|link]", GroupOther, "Discord and Telegram notifications: says what's set up (the webhook, the Telegram bot, the Discord bot) and what gets sent. 'notify test' sends a test message to each right now. 'notify link' gives the link that connects your Telegram, only in the nocat.farm window or its console (or the dashboard on this PC): whoever opens it first is connected as the owner."),
		new("plugins", "", GroupOther, "Which plugins are loaded, and where they came from."),
		new("tutorial", "[topic]", GroupOther, "Getting started, in order, ticking off what you have already done.", "guide|setup"),
		new("help", "[command|setting]", GroupOther, "This list, or what one command or setting does. Only know how it starts? 'help rot' lists every command and setting starting with \"rot\".", "?|h"),
		new("theme", "[dark|light]", GroupOther, "Switch the dashboard between the dark and light themes. Without an argument it says which is on.", "dark|light"),
		new("mini", "[on|off]", GroupOther, "Shrink the window to a small panel of your accounts (what each is doing, start and stop, the dashboard), or back to the full window."),
		new("dashboard", "[unlock]", GroupOther, "The dashboard's address: on this PC, on your phone over the same Wi-Fi, and from outside your home if you've set that up. /dashboard on Telegram or Discord sends the same links there. Locked out after too many wrong passwords? 'dashboard unlock' lets you (and anyone else locked out) sign in again straight away. To open it from anywhere, see 'anywhere'.", "web|link", "unlock"),
		new("anywhere", "[on|off]", GroupOther, "Open the dashboard from anywhere, not just your Wi-Fi. Your router forwards the port (UPnP), like Jellyfin. 'anywhere on' does all of it and answers with the link; 'anywhere off' closes it again; on its own it says whether it's on and the link. Works from Telegram and Discord too.", "remote"),
		new("clear", "", GroupOther, "Clears the log off the screen you type it in: the nocat.farm window, or the dashboard's Log and Console. The other one keeps its lines, and nothing is deleted: the log file has every line (Settings, Logging, Open the log folder).", "cls"),
		new("visitors", "[signout]", GroupOther, "Who has been at the dashboard: sign-ins, wrong passwords, lockouts, and visitors from the internet turned away. It shows when, from where and on what. 'visitors signout' signs every browser and phone out; you sign in again with the password.", "who"),
		new("version", "", GroupOther, "Which version this is.", "about"),
		new("update", "[accept|now|skip] | file [zip] [force] | versions | to <version>", GroupOther, "Check for a newer release. 'update accept' downloads it and restarts into it. Or, with Updates set to install when I click, once everyone's asleep, it installs once your accounts are asleep. 'update now' always installs right away. 'update skip' skips that version until a newer one comes out: no more reminders about it, and it never installs by itself. Nothing installs by itself unless Updates is set to install by itself at night. 'update file <zip>' installs a nocat.farm zip that's on this PC, the same way (add 'force' for the same or an older version); with no zip named, a window opens to pick it. Only in the nocat.farm window or its console, or the dashboard on this PC. 'update versions' lists the older versions still on GitHub, and 'update to <version>' goes back to one. Your settings are saved first and come back when you update again, and the version you came from doesn't install by itself until a newer one is out."),
		new("answer", "<text>", GroupOther, "Answer whatever nocat.farm is waiting on, like a Steam Guard code or a password."),
		new("exit", "", GroupOther, "Shut nocat.farm down.", "quit|q"),

		// For checking and fixing things by hand. They all keep working; they're just last, out of the everyday list.
		new("fairswap", "<account> <offerID>", GroupAdvanced, "Whether a trade offer is a fair card swap that AcceptFairCardSwaps would accept, and if not, why. Only looks, never accepts or declines."),
		new("addlicense", "<account|all> <IDs>", GroupAdvanced,
			"Add free licences to an account's library - a subID, or a/<appID> for a free app. Only works for genuinely free licences - a paid one is refused by Steam, and it says why."),
		new("reload", "", GroupAdvanced, "Re-read every config file from disk."),
		new("screen", "off", GroupAdvanced, "Turns this computer's screens off now to save power. nocat.farm keeps running. Moving the mouse or pressing a key turns them back on. Works from Telegram and Discord too.", "monitor|display")
	];

	/// <summary>
	/// Closing. Set before anything is cancelled, by every way out (exit, the tray, the window, Ctrl+C, SIGTERM) - an
	/// update that is downloading or signing accounts out reads it and stops rather than installing and starting
	/// nocat.farm again after it was closed. Volatile: it is read on other threads, and once set it stays set.
	/// </summary>
	public static bool ExitRequested => _exitRequested;

	private static volatile bool _exitRequested;

	/// <summary>
	/// The running manager, so a command arriving from somewhere that has no reference to it - a Steam message to
	/// one of your own accounts - can still be run. Set once at startup.
	/// </summary>
	public static BotManager? Host { get; set; }

	/// <summary>
	/// Run a command that came in over Steam chat.
	///
	/// Commands that take an account name are rewritten to name the account the message was sent to, so you can
	/// message an idler "pause" and have it pause itself rather than having to remember what you called it.
	/// </summary>
	/// <param name="later">Where a slow command's answer goes once it's done - back to the chat it came from, which can't read the log.</param>
	public static async Task<string> RunAsync(string input, string botName, Action<string>? later = null) {
		BotManager? mgr = Host;

		if (mgr == null) {
			return "nocat.farm isn't ready yet";
		}

		// The guards below read the words as the dispatcher does - split at any white space, so 'set<tab>WebPassword' is 'set'
		// to them too. The line itself goes on as typed: put back together with one space between its words, a game name
		// sent as 'gamename new a<nbsp>b' was stored as "a b".
		string line = input.Trim();
		string verb = Words(line).FirstOrDefault()?.ToLowerInvariant() ?? "";

		// Never let a remote command shut the whole thing down - it is one mis-sent word from taking every
		// account offline, and there is no way to start it again from Steam. Deleting an account is the same kind
		// of thing: it can't be undone, and Steam chat has no way to ask "are you sure". Checked by what the word
		// REACHES, not the word itself - 'delete' is 'remove' under another name, and it used to get straight through.
		if (Resolve(verb)?.Name is "exit" or "remove") {
			return "that one has to be done at the PC";
		}

		// Installing a zip from this PC is for this PC alone - not Telegram or Discord, as the refusal below would say.
		if ((Resolve(verb)?.Name == "update") && Words(line) is [_, string file, ..]
			&& file.Equals("file", StringComparison.OrdinalIgnoreCase)) {
			return await UpdateFromFile("", atThisPc: false).ConfigureAwait(false);
		}

		// Someone allowed to command ONE account - an ASF Master of that bot comes across as one - could reach every other
		// account from its chat: every account's Steam Guard codes with a bare /2fa, /confirm on another account, /set
		// WebPassword and /anywhere on, which opens the dashboard to the internet under a password they chose. What
		// reaches past this account, or hands out its secrets and the app's, is for the PC, Telegram and Discord only.
		if ((Resolve(verb)?.Name is { } name && SteamChatRefuses(name)) || SteamChatRefusesAnswer(mgr, line)) {
			return "that one has to be done at the PC, or from Telegram or Discord";
		}

		if (ReachesPast(mgr, line, botName)) {
			return $"from Steam chat this account only takes commands for itself ({botName})";
		}

		// By the command the word reaches, so the hidden 'bots' means this account too, like 'status'. And not only when the
		// word is alone: 'value refresh' or 'pause 30' name no account either, and with none these mean every account.
		line = ThisBotFilledIn(mgr, line, botName);

		return await RunAsync(mgr, line, atThisPc: false, later).ConfigureAwait(false);
	}

	/// <summary>The command a typed word reaches - its name or any alias - or null when it reaches none.</summary>
	/// <remarks>Slashes and '!' off first, as the dispatcher takes them off: "//exit" or "!exit" from a chat has to meet the same
	/// guard as "exit".</remarks>
	public static CommandDef? Resolve(string typed) => All.FirstOrDefault(c => c.Matches(Unprefixed(typed)));

	/// <summary>
	/// A command word without what may come before it: '/' - the Telegram habit - and '!', ArchiSteamFarm's. Typed in the
	/// window, Telegram's console or Discord's /nocat, '!status' answered "there's no '!…' command" though Steam chat took it.
	/// </summary>
	internal static string Unprefixed(string word) => word.TrimStart('/', '!');

	/// <summary>
	/// A typed line as the log, the chats, the window and the dashboard may have it, and what was hidden from it - so a reply
	/// that says it back is masked the same. An allowlist, not a guess at what looks secret: six rounds of guessing kept
	/// finding a password that looked like a typo, a code alone, a key with a comma on it, and a group or game name hidden
	/// that was nothing of the sort.
	///
	/// A command it knows is shown as typed, with only the slots that take a secret masked (<see cref="ArgumentsMasked"/>).
	/// A word that is no command is never shown at all: "(not a command)", and the command it's nearest to - unless it is one
	/// typo off a command's name or a plugin's (3 or more letters, and the name's own digits - <see cref="MayBeTypoOf"/>), when
	/// it's the word and "***" for the rest.
	/// </summary>
	/// <param name="Word">The command word as it may be shown - "(not a command)" for one that isn't.</param>
	/// <param name="Hidden">What was hidden, and what it is shown as instead - for the reply.</param>
	/// <param name="Kept">Whether the line may be kept for the up arrow.</param>
	/// <param name="NotKeys">Words never masked as a product key, in the line or its reply: a Steam login (<see cref="NotKeys"/>).</param>
	private readonly record struct Masking(string Shown, string Word, (string Text, string As)[] Hidden, bool Kept, IReadOnlySet<string>? NotKeys = null);

	private static Masking Masked(string line) {
		line = line.Trim();
		string[] parts = Words(line);

		if (parts.Length == 0) {
			return new(line, "", [], true);
		}

		// The command word as typed: a Telegram bot's name is taken off on the way in from Telegram (Notifier.TelegramLine), not
		// here - 's@cretbot' cut at the '@' was 's', which is 'status', and the password went in the log whole.
		string verb = Unprefixed(parts[0]).ToLowerInvariant();

		if ((verb.Length > 0) && ((Resolve(verb) != null) || Plugins.PluginHost.Commands.ContainsKey(verb))) {
			string command = Resolve(verb)?.Name ?? verb;
			(string args, (string, string)[] hidden) = ArgumentsMasked(command, line, parts);
			HashSet<string> notKeys = NotKeys(command, line, parts);
			string shown = KeysMasked(Log.Scrub(args), notKeys);

			return new(shown, Words(shown).FirstOrDefault() ?? "", hidden, shown == line, notKeys);
		}

		// One typo off a command: shown and kept for the up arrow, 'pasue alt' typed again fixed - but nothing after it shown, and
		// not kept when a command it may have meant would hide what follows ('anwser hunter2', 'sett WebPassword x', 'redem KEY'):
		// any 2 typos off it, or starting with it - 'ans hunter2' is one off 'bans', and 'answer' cut short.
		if (OneTypoOff(parts[0]).Count > 0) {
			string rest = parts.Length > 1 ? " " + Tail(line, 1) : "";

			return new(parts[0] + (rest.Length > 0 ? " ***" : ""), parts[0], [], (rest.Length == 0) || MayHaveMeant(parts[0]).All(n => Masked(n + rest).Kept));
		}

		// Anything else - a password or a Steam Guard code pasted at the prompt, a product key, a token - in no shape at all.
		string said = Close(parts[0]) is { } close ? $"(not a command, nearest '{close}')" : "(not a command)";

		return new(said, said, [], false);
	}

	/// <summary>
	/// A known command's arguments, with the slots that take a secret masked - and what each masked one was. Every other
	/// command is as typed: its arguments are accounts, appIDs, numbers, names, paths.
	/// </summary>
	private static (string Line, (string Text, string As)[] Hidden) ArgumentsMasked(string command, string line, string[] parts) {
		switch (command) {
			// A password, a Steam Guard code, a Family View PIN - whatever is being asked.
			case "answer" when parts.Length > 1:
				return ($"{parts[0]} ***", [(Tail(line, 1), "***")]);

			// 'add <name> <login> [human|robot|qr]' takes no password - one typed on the end anyway stays out.
			case "add":
				return Slots(line, parts, (w, i) => (i < 3) || (w.ToLowerInvariant() is "human" or "robot" or "qr") ? null : "***");

			// 'redeem [account] <key|file> [key...]': a key in any shape it takes. A path is where the keys are, not one; anything
			// else - a key half typed ('AAAA-BBBB-CCCC'), a password pasted in the wrong place - is masked as well.
			case "redeem": {
				int keep = (parts.Length > 1) && (Host?.Get(parts[1]) != null) ? 2 : 1;

				return Slots(line, parts, (w, i) => i < keep ? null : Redeeming.LooksLikeKey(w) ? "[key]" : LooksLikePath(w) ? null : "***");
			}

			case "set":
				return SetMasked(line, parts);

			default:
				return (line, []);
		}
	}

	/// <summary>
	/// A line with some of its words masked: as typed when none is, put back together with one space between them when one is.
	/// <paramref name="masked"/> says what a word is shown as instead, or null to keep it.
	/// </summary>
	private static (string Line, (string Text, string As)[] Hidden) Slots(string line, string[] parts, Func<string, int, string?> masked) {
		string?[] instead = [.. parts.Select((w, i) => masked(w, i))];

		return instead.All(static m => m == null)
			? (line, [])
			: (string.Join(' ', parts.Select((w, i) => instead[i] ?? w)), [.. parts.Select((w, i) => (w, instead[i])).Where(static x => x.Item2 != null).Select(static x => (x.w, x.Item2!))]);
	}

	/// <summary>
	/// 'set <key> <value>' or 'set <account> <key> <value>': the key shown, the value only for a setting it knows that isn't a
	/// secret. A proxy's address is shown without the user name and password typed into it. A key it doesn't know - a misspelt
	/// secret as likely as anything - keeps the first word and nothing after it.
	/// </summary>
	private static (string Line, (string Text, string As)[] Hidden) SetMasked(string line, string[] parts) {
		if (parts.Length < 2) {
			return (line, []);
		}

		int words = Math.Min(3, parts.Length);

		// What a value is hidden as, with its quotes and without: 'set' takes them off ('set WebProxy "user:pass@host"'), so
		// what it says back has none - and with them on, the reply's address went past the mask whole.
		static (string, string)[] Both(string value, string shown) => Unquote(value) is var bare && (bare != value) && (bare.Length > 0)
			? [(value, shown), (bare, shown)]
			: [(value, shown)];

		for (int i = 1; i < words; i++) {
			(string key, bool joined) = KeyOf(parts[i]);
			SettingDef? def = Settings.Find(key);
			string shownKey = string.Join(' ', [.. parts[..i], key]);

			if (def is { Kind: SettingKind.Secret }) {
				string value = SetValue(line, parts, i);

				return (shownKey + (joined || (parts.Length > i + 1) ? " ***" : ""), value.Length > 0 ? Both(value, "***") : []);
			}

			// A proxy's address with a user name and password typed into it ('user:pass@host:port'): scheme://host:port, or "***"
			// when it won't read as an address - in quotes or not, as 'set' takes it. Without an '@' there's no password in it,
			// and it's as typed.
			if (Settings.IsProxy(def) && SetValue(line, parts, i) is { } proxy && proxy.Contains('@', StringComparison.Ordinal)) {
				string address = Settings.ProxyShown(Unquote(proxy));

				return ($"{shownKey} {address}", Both(proxy, address));
			}
		}

		// An ordinary setting is left as typed.
		for (int i = 1; i < words; i++) {
			if (Settings.Find(KeyOf(parts[i]).Key) != null) {
				return (line, []);
			}
		}

		// A word that is no setting - a password pasted after 'set' as likely as a name misspelt - shows nothing of itself or
		// what follows it: 'set hunter2' went in the log as typed, and was kept for the up arrow. An account before it is shown.
		int at = (Host?.Get(parts[1]) != null) || parts[1].Equals("all", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

		if (at >= parts.Length) {
			return (line, []);
		}

		(string Text, string As)[] hidden = [(Tail(line, at), "***"), (KeyOf(parts[at]).Key, "***"), .. Both(SetValue(line, parts, at), "***")];

		return ($"{string.Join(' ', parts[..at])} (not a setting)", [.. hidden.Where(static h => h.Text.Length > 0).Distinct()]);
	}

	/// <summary>
	/// The value of 'set' after the key at word <paramref name="key"/>: stuck on it by '=' or ':' ('WebPassword=x'), and whatever
	/// comes after it as typed - two spaces in a password kept. Empty when there's none.
	/// </summary>
	private static string SetValue(string line, string[] parts, int key) {
		int cut = parts[key].IndexOfAny(['=', ':']);

		return cut >= 0 ? Tail(line, key)[(cut + 1)..].Trim() : parts.Length > key + 1 ? Tail(line, key + 1) : "";
	}

	/// <summary>
	/// The words of a known command never masked as a product key: a Steam login - the login of 'add', the value of 'set ...
	/// SteamLogin' - is any run of letters and digits its owner chose. 'Login2024user99' went in the log as "[key]", and the
	/// line was dropped from the up arrow.
	/// </summary>
	private static HashSet<string> NotKeys(string command, string line, string[] parts) {
		HashSet<string> login = new(StringComparer.OrdinalIgnoreCase);

		if ((command == "add") && (parts.Length > 2)) {
			login.Add(parts[2]);
		}

		for (int i = 1; (command == "set") && (i < Math.Min(3, parts.Length)); i++) {
			if (Settings.Find(KeyOf(parts[i]).Key)?.Name == nameof(BotConfig.SteamLogin)) {
				login.UnionWith(Words(Unquote(SetValue(line, parts, i))));

				break;
			}
		}

		return login;
	}

	/// <summary>
	/// A word as the setting it may name: quotes off, and cut at '=' or ':' - 'WebPassword=x' and '"WebPassword"' went
	/// straight past a lookup of the word as typed. Joined: a value came with it.
	/// </summary>
	private static (string Key, bool Joined) KeyOf(string word) {
		int cut = word.IndexOfAny(['=', ':']);
		string key = (cut < 0 ? word : word[..cut]).Trim('"', '\'', '`', '“', '”', '‘', '’');

		return (key, cut >= 0);
	}

	/// <summary>
	/// A line's words as every command reads them: split at any white space. Split at spaces alone, a tab or a no-break
	/// space after 'answer' made one unknown word of the answer too - and "there's no such command" said the answer back.
	/// For the command word and the account and setting slots; free text after them is <see cref="Tail"/>.
	/// </summary>
	internal static string[] Words(string line) => line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);

	/// <summary>
	/// What was typed after a line's first <paramref name="skip"/> words, as it was typed: the gap after them taken off, and
	/// everything from there on kept - a no-break space in a game name, two spaces in a password. Put back together from
	/// <see cref="Words"/>, 'gamename new a<nbsp>b' stored "a b".
	/// </summary>
	internal static string Tail(string line, int skip) {
		int i = 0;

		for (int word = 0; word < skip; word++) {
			while ((i < line.Length) && char.IsWhiteSpace(line[i])) {
				i++;
			}

			while ((i < line.Length) && !char.IsWhiteSpace(line[i])) {
				i++;
			}
		}

		while ((i < line.Length) && char.IsWhiteSpace(line[i])) {
			i++;
		}

		return line[i..].TrimEnd();
	}

	/// <summary>
	/// The words after a line's first <paramref name="skip"/>, split at spaces alone - as a plugin's command and the path of
	/// 'update file' always had them, a no-break space staying inside its word.
	/// </summary>
	internal static string[] Loose(string line, int skip) => Tail(line, skip).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// A command word without the name of the Telegram bot it was sent to: '/status@mybot', picked from the menu in a group.
	/// Only a bot's name - Telegram's end in "bot" - is taken off, <paramref name="bot"/>'s when it's known: cut at any '@',
	/// the password 's@cret12' was 's', which is 'status', and went in the log whole. On the Telegram path only
	/// (Notifier.TelegramLine): taken off anywhere, 's@cretbot' was 's' as well.
	/// </summary>
	/// <param name="bot">The connected bot's name, with or without its '@' - empty when it isn't known yet.</param>
	internal static string WithoutBotName(string word, string bot = "") {
		int at = word.IndexOf('@', StringComparison.Ordinal);

		if (at <= 0) {
			return word;
		}

		string name = word[(at + 1)..];
		string own = bot.TrimStart('@');

		return BotName().IsMatch(name) && ((own.Length == 0) || name.Equals(own, StringComparison.OrdinalIgnoreCase)) ? word[..at] : word;
	}

	/// <summary>A Telegram bot's user name: 5 to 32 letters, digits and underscores, starting with a letter, ending in "bot".</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z]\w{2,29}bot$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex BotName();

	/// <summary>
	/// The commands a word that is no command is one typo off - the word with one '/' or '!' off the front, 3 or more letters
	/// (and the digits of a name with digits, <see cref="MayBeTypoOf"/>), against every name a command has and every
	/// plugin's. Empty for anything else: a word with a mark in it, or a digit no name has, is a password as likely as a typo.
	/// </summary>
	private static List<string> OneTypoOff(string word) {
		if (word.StartsWith('/') || word.StartsWith('!')) {
			word = word[1..];
		}

		if ((word.Length < 3) || !word.All(char.IsLetterOrDigit)) {
			return [];
		}

		return Within(word.ToLowerInvariant(), static (typed, name) => MayBeTypoOf(typed, name) && (Typos(typed, name) <= 1));
	}

	/// <summary>
	/// Whether a typed word can be a typo of a name at all: letters only - or, for a name with a digit in it ('2fa', 'rep4rep',
	/// 'r4r'), letters and that name's own digits in its order. '2af' and 'rep4ep' are typos; '2fa9' and 'Status1' are a
	/// password as likely as not.
	/// </summary>
	private static bool MayBeTypoOf(string typed, string name) =>
		typed.All(char.IsLetter)
		|| (typed.All(char.IsLetterOrDigit) && name.Any(char.IsAsciiDigit) && typed.Where(char.IsDigit).SequenceEqual(name.Where(char.IsAsciiDigit)));

	/// <summary>The commands a word one typo off a command may have been meant for: 2 typos off it at most, or starting with it.</summary>
	private static List<string> MayHaveMeant(string word) =>
		Within(Unprefixed(word).ToLowerInvariant(), static (typed, name) => (Typos(typed, name) <= 2) || name.StartsWith(typed, StringComparison.Ordinal));

	/// <summary>The commands with a name - its own, a short one, a hidden one or a plugin's - that <paramref name="near"/> says a typed word is near.</summary>
	private static List<string> Within(string typed, Func<string, string, bool> near) =>
		[.. Names().Where(n => near(typed, n.Name.ToLowerInvariant())).Select(static n => n.Command).Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// Every name a command can be typed by - its own, its short ones, its hidden ones - with the command it reaches, and every
	/// plugin's command: 'nigthjob' was no command at all, never one typo off a plugin's 'nightjob'.
	/// </summary>
	private static IEnumerable<(string Name, string Command, bool Hidden)> Names() =>
		All.SelectMany(static c => $"{c.Name}|{c.Aliases}".Split('|', StringSplitOptions.RemoveEmptyEntries).Select(n => (n, c.Name, false))
			.Concat(c.HiddenAliases.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(n => (n, c.Name, true))))
			.Concat(Plugins.PluginHost.Commands.Keys.Select(static verb => (verb, verb, false)));

	/// <summary>
	/// The command a word that is no command is nearest to, 1 or 2 typos off, for "did you mean" - the word with the marks
	/// around it off ('.status', '"status"'), 3 or more letters (and a name's own digits, <see cref="MayBeTypoOf"/>), against
	/// every command's names and every plugin's. A word of 3 or 4 only 1 off, and never a name shorter than 3: two off 's' or
	/// 'h' is any short word ('the'). The name it is nearest is the one given, unless that's a hidden one. Null when nothing
	/// is that near.
	/// </summary>
	private static string? Close(string word) {
		string bare = Bare(word).ToLowerInvariant();

		if ((bare.Length < 3) || !bare.All(char.IsLetterOrDigit)) {
			return null;
		}

		(string? name, int typos) best = (null, bare.Length < 5 ? 2 : 3);

		foreach ((string n, string command, bool hidden) in Names().Where(n => (n.Name.Length >= 3) && MayBeTypoOf(bare, n.Name.ToLowerInvariant()))) {
			if (Typos(bare, n.ToLowerInvariant()) is int t && (t < best.typos)) {
				best = (hidden ? command : n, t);
			}
		}

		return best.name;
	}

	/// <summary>A word with the marks around it taken off - '.status' and '"status"' are "status".</summary>
	private static string Bare(string word) {
		int start = 0, end = word.Length;

		while ((start < end) && !char.IsLetterOrDigit(word[start])) {
			start++;
		}

		while ((end > start) && !char.IsLetterOrDigit(word[end - 1])) {
			end--;
		}

		return word[start..end];
	}

	/// <summary>
	/// Whether a line is kept from the up arrow (the window's, the console's, the dashboard's): whenever <see cref="LineForLog"/>
	/// hides anything in it - a secret slot of a command it knows, or a word that is no command. Not a mistyped one that is
	/// one typo off a command, 'pasue new': typing it again with the up arrow is the point - unless the command it's off would
	/// hide what follows it.
	/// </summary>
	public static bool HoldsSecret(string line) => !Masked(line).Kept;

	/// <summary>How many letters apart two words are: one changed, added, dropped, or two swapped round, each counts one.</summary>
	private static int Typos(string a, string b) {
		int[,] d = new int[a.Length + 1, b.Length + 1];

		for (int i = 0; i <= a.Length; i++) {
			d[i, 0] = i;
		}

		for (int j = 0; j <= b.Length; j++) {
			d[0, j] = j;
		}

		for (int i = 1; i <= a.Length; i++) {
			for (int j = 1; j <= b.Length; j++) {
				d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));

				if ((i > 1) && (j > 1) && (a[i - 1] == b[j - 2]) && (a[i - 2] == b[j - 1])) {
					d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
				}
			}
		}

		return d[a.Length, b.Length];
	}

	/// <summary>
	/// Commands where a bare verb sensibly means "this account", rather than "all of them" - by the command's own name,
	/// whichever of its names was typed.
	/// </summary>
	public static bool DefaultsToThisBot(string command) =>
		command is "status" or "pause" or "resume" or "start" or "stop" or "cards" or "config" or "human" or "habits" or "2fa"
			// With no account these go over every account - claiming event items on all of them, every account's trades,
			// wallet, inventory and bans - which from one account's chat reached past it.
			or "hunt" or "selfcheck" or "confirmations" or "offers" or "booster" or "freegames" or "freeitems" or "queue" or "value" or "level"
			or "balance" or "points" or "bans";

	/// <summary>
	/// A Steam-chat line with this account's name put in after the command, for a command that takes the account first and,
	/// without one, means every account - unless an account is already named there.
	/// </summary>
	public static string ThisBotFilledIn(BotManager mgr, string line, string botName) {
		string[] words = Words(line);

		if ((words.Length == 0) || !DefaultsToThisBot(Resolve(words[0])?.Name ?? "") || ((words.Length > 1) && (mgr.Get(words[1]) != null))) {
			return line;
		}

		// The rest as it was typed, not put back together a word at a time.
		return $"{words[0]} {botName}" + (words.Length > 1 ? " " + Tail(line, 1) : "");
	}

	/// <summary>
	/// A choice about a game with add-ons ('dlc leave|undo'): the owner's word, which decides
	/// what it unlocks - not something a Steam-chat master gives for them. Looking ('dlc &lt;account&gt;') is fine. An
	/// account that happens to be called "leave" is that account, as it is at the PC.
	/// </summary>
	public static bool SteamChatRefusesAnswer(BotManager mgr, string line) {
		string[] words = Words(line);

		return (words.Length > 1) && (Resolve(words[0])?.Name == "dlc") && (words[1].ToLowerInvariant() is "leave" or "undo")
			&& (mgr.Get(words[1]) == null);
	}

	/// <summary>
	/// Commands a Steam-chat master can't give: the app's own settings and secrets, the dashboard and its way in from the
	/// internet, updates, files on this PC, and the ones that act on every account at once.
	/// </summary>
	public static bool SteamChatRefuses(string command) =>
		command is "set" or "anywhere" or "dashboard" or "unlock" or "update" or "import" or "redeem" or "keys" or "answer"
			or "add" or "reload" or "plugins" or "notify" or "screen" or "match" or "theme" or "mini"
			// Who has been at the dashboard (addresses, devices, and signing everybody out), a zip of every saved login,
			// and the reports that show every account.
			or "visitors" or "backup" or "report" or "stuck"
			// 'stats' opens with that same report, and the log has every account in it: the answers Telegram and Discord got
			// (Steam Guard codes from /2fa among them), and the Telegram connect link before a chat is connected - which made
			// whoever opened it the owner. 'log folder' opens a folder on this PC.
			or "log" or "stats"
			// 'owns' looks a game up on every account, and rep4rep is one queue for them all: its summary lists every account,
			// and 'rep4rep pause' or 'clear' stops or wipes it for every one of them.
			or "owns" or "rep4rep";

	/// <summary>
	/// Whether a command sent to <paramref name="botName"/> over Steam chat names another account, or all of them - in the
	/// account's place ('trade' has it second: 'trade accept kylro 1'), or anywhere else ('send new to kylro' is fine,
	/// the items are this account's; 'send kylro to new' is not).
	/// </summary>
	internal static bool ReachesPast(BotManager mgr, string line, string botName) {
		string[] words = Words(line);
		int accountAt = Resolve(words.FirstOrDefault() ?? "")?.Name == "trade" ? 2 : 1;

		if ((words.Length > accountAt) && words[accountAt].Equals("all", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		// 'send <this> to <other>' moves this account's own items - the one place another account may be named.
		bool sendTo = (Resolve(words.FirstOrDefault() ?? "")?.Name == "send") && (words.Length > 3) && words[2].Equals("to", StringComparison.OrdinalIgnoreCase);

		return words.Skip(1).Select((w, i) => (w, i: i + 1))
			.Any(x => !(sendTo && (x.i == 3)) && (mgr.Get(x.w) is { } other) && !other.Name.Equals(botName, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Run a command typed at this PC: the nocat.farm window, its console, or the dashboard opened on this PC itself. Only
	/// those three call it. Everything else - Steam chat, Telegram, Discord, a plugin, a dashboard on a phone or from the
	/// internet - is not at this PC.
	/// </summary>
	/// <remarks>
	/// Handed down to the command as it runs, never left lying about for whatever runs next. It used to flow with the command
	/// (an AsyncLocal), and so into everything the command started: 'start' typed in the window started the account's loop
	/// inside it, the loop ran its Steam chat, and a master's 'update file' there counted as typed at this PC.
	/// </remarks>
	/// <param name="from">Where it was typed, for the log file ("window", "console", "dashboard on this PC"); null writes nothing there.</param>
	public static async Task<string> RunAtThisPcAsync(BotManager mgr, string input, string? from = null) {
		string reply = await RunAsync(mgr, input, atThisPc: true).ConfigureAwait(false);

		if (from != null) {
			LogExchange(from, input, reply);
		}

		return reply;
	}

	/// <summary>Run a command from somewhere else - Telegram, Discord, a dashboard on a phone, a plugin - and write it and its reply to the log file.</summary>
	public static async Task<string> RunLoggedAsync(BotManager mgr, string input, string from) {
		string reply = await RunAsync(mgr, input).ConfigureAwait(false);
		LogExchange(from, input, reply);

		return reply;
	}

	/// <summary>The most reply lines the log file takes from one command - 'log 1000' or every account's status is plenty.</summary>
	internal const int LoggedReplyLines = 200;

	/// <summary>
	/// A command and its whole reply into the log file, marked with where it came from - the window shows the reply and
	/// forgets it, and a question about something done from a phone or a chat last night had nothing to go on. Secrets
	/// never go in (see <see cref="ExchangeLines"/>).
	/// </summary>
	public static void LogExchange(string from, string line, string reply) {
		try {
			Log.FileLines("INFO", "command", ExchangeLines(from, line, reply));
		} catch (Exception e) {
			// the command has run; only its line in the file is lost
			Log.Failed("writing a command to the log file", e);
		}
	}

	/// <summary>
	/// The lines a command and its reply take in the log file: "telegram> 2fa", then one line per reply line, past
	/// <see cref="LoggedReplyLines"/> counted rather than written. Masked as <see cref="LineForLog"/> and
	/// <see cref="ReplyForLog"/> say.
	/// </summary>
	internal static List<string> ExchangeLines(string from, string line, string reply) =>
		[$"{from}> {LineForLog(line)}", .. ReplyForLog(line, reply).Select(l => $"{from}  {l}")];

	/// <summary>
	/// A typed line as the log file, the chats, the window and the dashboard may have it. A command it knows as typed, with
	/// only what goes in a secret's place masked: the value of a secret setting, whatever is typed after 'answer', anything
	/// extra after 'add', the keys after 'redeem', a password in a proxy's address - and the tokens and keys of
	/// <see cref="FreeTextForLog"/>. A word that is no command shows nothing of what was typed: "(not a command)".
	/// </summary>
	public static string LineForLog(string line) => Masked(line).Shown;

	/// <summary>
	/// A Steam chat message as the log may have it. A command ('/' or '!' first) masked as <see cref="LineForLog"/> masks it -
	/// written word for word, '/set new SteamPassword x', '/answer x' and '/redeem KEY' put the secret in the log before the
	/// command was even looked at. Anything else is somebody talking, and stays readable (<see cref="FreeTextForLog"/>).
	/// </summary>
	/// <param name="fromOwn">Sent by one of your own accounts - see <see cref="FreeTextForLog"/>.</param>
	public static string MessageForLog(string text, bool fromOwn = false) {
		string trimmed = text.Trim();

		return trimmed.StartsWith('/') || trimmed.StartsWith('!') ? LineForLog(trimmed) : FreeTextForLog(trimmed, fromOwn);
	}

	/// <summary>
	/// Text nobody typed as a command - a chat message, a reply - with only what is surely a secret masked: a product key with
	/// a digit in it, a password in a proxy's address after its scheme, the tokens <see cref="Log.Scrub"/> knows, and a Steam
	/// Guard code with letters and digits both in it. A dashed word, a game's name in capitals, a price or a time is as said.
	/// One of your own accounts answering '/2fa' sends its codes: from one (<paramref name="fromOwn"/>), or on a line in the
	/// shape '2fa' answers with ("BCDFG   (25s left)"), a code of letters alone or digits alone is masked too.
	/// </summary>
	public static string FreeTextForLog(string text, bool fromOwn = false) {
		string masked = KeysMasked(Log.Scrub(text));
		masked = (fromOwn ? GuardCode() : GuardCodeWithTimeLeft()).Replace(masked, "*****");

		return MixedGuardCode().Replace(masked, "*****");
	}

	/// <summary>
	/// The command word of a line as the log file may have it. The first word split at spaces was 'answer<tab>hunter2' -
	/// the answer and all.
	/// </summary>
	public static string CommandForLog(string line) => Masked(line).Word;

	/// <summary>
	/// A command's reply as the log file may have it: one entry per line, masked, and past <see cref="LoggedReplyLines"/>
	/// "… N more lines". For Telegram and Discord too, which show their replies in the log as well. What the line's masking
	/// hid is hidden here too, wherever the reply says it back: 'set WebProxy' answers with the address, 'redeem' with the key.
	/// </summary>
	public static List<string> ReplyForLog(string line, string reply) {
		string verb = VerbOf(line);
		Masking masking = Masked(line);
		string[] said = [.. reply.ReplaceLineEndings("\n").Split('\n').Where(static l => l.Trim().Length > 0)];
		List<string> lines = [.. said.Take(LoggedReplyLines).Select(l => MaskedReply(verb, l, masking.Hidden, masking.NotKeys))];

		if (said.Length > LoggedReplyLines) {
			lines.Add($"… {said.Length - LoggedReplyLines} more lines");
		}

		return lines;
	}

	/// <summary>The command a line reaches, by its own name: "2fa" for "/guard new".</summary>
	private static string VerbOf(string line) => Resolve(Unprefixed(Words(line).FirstOrDefault() ?? ""))?.Name ?? "";

	/// <param name="hidden">What the line it answers had hidden, and what as - shown the same wherever the reply says it.</param>
	/// <param name="notKeys">What the line it answers says is no key: the login 'add' was given, said back.</param>
	private static string MaskedReply(string verb, string text, (string Text, string As)[]? hidden = null, IReadOnlySet<string>? notKeys = null) {
		string masked = text;

		// Before the scrubber, which would have changed a proxy's address so it's no longer found. Longest first, so a password's
		// words don't break up the password before it's found whole. Short ones are left: a two-letter word in a reply is any word.
		foreach ((string secret, string shown) in (hidden ?? []).SelectMany(static h => Words(h.Text).Select(w => (w, h.As)).Prepend(h))
			.Where(static h => h.Item1.Length >= 3).OrderByDescending(static h => h.Item1.Length)) {
			masked = System.Text.RegularExpressions.Regex.Replace(masked, $@"(?<![\p{{L}}\p{{Nd}}]){System.Text.RegularExpressions.Regex.Escape(secret)}(?![\p{{L}}\p{{Nd}}])",
				shown.Replace("$", "$$", StringComparison.Ordinal), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
		}

		masked = KeysMasked(Log.Scrub(masked), notKeys);

		// 'redeem' answers with the key on a line of its own, or first, or quoted - in whatever shape it was given, and a key
		// queued and activated later comes back with no line to say what it was. Not every long word in it: the game's name.
		if (verb == "redeem") {
			masked = RedeemedKey().Replace(masked, static m => Redeeming.LooksLikeKey(m.Groups["key"].Value) ? m.Value.Replace(m.Groups["key"].Value, "[key]", StringComparison.Ordinal) : m.Value);
		}

		// '2fa' answers with the codes themselves - good for 30 seconds, but a code in a file is a code someone can read.
		return verb == "2fa" ? GuardCode().Replace(masked, "*****") : masked;
	}

	/// <summary>A word first on a line, or in quotes: where 'redeem' puts the key it was given.</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?<key>[^\s']+)|'(?<key>[^\s']+)'", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex RedeemedKey();

	/// <summary>A Steam Guard code: five of Steam's own 26 letters and digits.</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"\b[23456789BCDFGHJKMNPQRTVWXY]{5}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex GuardCode();

	/// <summary>A Steam Guard code as '2fa' says it, with the seconds it has left after it: "BCDFG   (25s left)".</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"\b[23456789BCDFGHJKMNPQRTVWXY]{5}\b(?=\s+\(\d+s left\))", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex GuardCodeWithTimeLeft();

	/// <summary>A Steam Guard code with a letter and a digit both in it - 'B2C4D', never a price (23456) or a word (BHGKM).</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"\b(?=[2-9BCDFGHJKMNPQRTVWXY]{0,4}[2-9])(?=[2-9BCDFGHJKMNPQRTVWXY]{0,4}[BCDFGHJKMNPQRTVWXY])[23456789BCDFGHJKMNPQRTVWXY]{5}\b",
		System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex MixedGuardCode();

	/// <summary>Set by the host so 'exit' works from the dashboard too, not just from the console.</summary>
	public static Action? ExitHandler { get; set; }

	/// <summary>The live board, so command output can be shown without the repaint eating it.</summary>
	public static Windows.LiveConsole? Board { get; set; }

	/// <summary>The window, when there is one. Set at startup on Windows.</summary>
	public static Windows.MainWindow? Window { get; set; }

	/// <summary>The dashboard address, when it is running. Set by the host once it has bound a port.</summary>
	public static Func<string>? DashboardUrl { get; set; }

	/// <summary>
	/// Show the dashboard.
	///
	/// One helper rather than the four separate Process.Start calls that had grown up around the app, so the
	/// "is it even running" check and the failure handling exist once instead of four times.
	/// </summary>
	public static bool OpenDashboard() {
		string url = DashboardUrl?.Invoke() ?? "";

		// A server or a container has no browser to open - trying only logs a confusing "no such file".
		if (string.IsNullOrEmpty(url) || !Core.Platform.HasDesktop) {
			return false;
		}

		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

			return true;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't open the dashboard: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)));

			return false;
		}
	}

	/// <summary>
	/// Whether a tray icon exists to bring the window back.
	///
	/// Without one, hiding the window strands the process: it keeps running with nothing left to click.
	/// </summary>
	public static bool TrayPresent { get; set; }

	/// <summary>
	/// Ask for shutdown from anywhere - the window's quit button, the tray, Ctrl+C. The flag goes up FIRST, then the
	/// host is told: an update checking the flag can never see the app closing without it. Safe to call twice.
	/// </summary>
	/// <remarks>The flag goes up under the updater's handover lock, which checks it and starts the swap as one step.
	/// Set without it, a close between that check and the swap starting installed the new version and started nocat.farm
	/// again after it had been closed. Only the flag is under it: the host is told outside, since cancelling runs every
	/// shutdown callback on this thread, and none of them should wait on a swap starting.</remarks>
	public static void RequestExit() {
		lock (SelfUpdate.HandoverGate) {
			_exitRequested = true;
		}

		ExitHandler?.Invoke();
	}

	/// <summary>Set by the tray icon so minimise-to-tray applies the moment it's changed.</summary>
	public static Action<bool>? TrayHook { get; set; }

	/// <summary>Run a command from anywhere but this PC - see <see cref="RunAtThisPcAsync"/> for the one that is.</summary>
	public static Task<string> RunAsync(BotManager mgr, string input) => RunAsync(mgr, input, atThisPc: false);

	/// <param name="atThisPc">Typed at this PC (<see cref="RunAtThisPcAsync"/>): 'update file' and 'notify link' work only then.</param>
	/// <param name="later">Where a slow command's answer goes once it's done, besides the log (see <see cref="LateReply"/>).</param>
	private static async Task<string> RunAsync(BotManager mgr, string input, bool atThisPc, Action<string>? later = null) {
		string line = input.Trim();

		// Set by every command, the chat null for all but Steam chat - never left over from an earlier one: an account a Steam-chat
		// 'start' set going carries it along, and a command run from inside it later would have answered to that chat. With what
		// the line hid: a slow 'redeem' answering late put "'ABCDE-FGHIJ-KLMN' doesn't look like a Steam key" in the log whole.
		LateReply.Value = (later, Masked(line).Hidden);

		if (line.Length == 0) {
			return "";
		}

		// At any white space, exactly as the log masks the line (LineForLog): split at spaces alone, 'answer<tab>hunter2' was one
		// unknown word here and the answer went out in the reply.
		string[] parts = Words(line);

		// With or without a slash, as 'help' and 'clear' already were: "/pause kylro" - the Telegram habit - typed in the
		// window or the dashboard's Console answered "there's no '/pause' command". And '!', as ArchiSteamFarm has it.
		// Free text - a game name, a setting's value, a path - is taken from the line as typed (Tail), not from these words.
		string cmd = Unprefixed(parts[0]).ToLowerInvariant();
		string[] rest = parts[1..];

		// A '!' or '/' with nothing on it - alone, or '! status' with a space after it - answered "There's no '' command".
		if (cmd.Length == 0) {
			return $"Type a command after the '{parts[0][^1]}' - 'help' lists them.";
		}

		try {
			return cmd switch {
				"help" or "?" or "h" => Help(rest),
				"tutorial" or "guide" or "setup" => Tutorial.Render(mgr, rest.FirstOrDefault()),
				"status" or "s" or "bots" => Status(mgr, rest.FirstOrDefault()),
				"start" => await LifecycleAsync(mgr, rest, "start").ConfigureAwait(false),
				"stop" => await LifecycleAsync(mgr, rest, "stop", graceful: true).ConfigureAwait(false),
				"pause" => await LifecycleAsync(mgr, rest, "pause").ConfigureAwait(false),
				"resume" => await LifecycleAsync(mgr, rest, "resume").ConfigureAwait(false),
				"restart" => await RestartAsync(mgr, rest).ConfigureAwait(false),
				"play" => Play(mgr, rest),
				"grind" => Grind(mgr, rest),
				"drops" => await DropsAsync(mgr, rest).ConfigureAwait(false),
				"hours" => Hours(mgr, rest),
				"rotation" => Rotation(mgr, rest),
				"offers" => await OffersAsync(mgr, rest).ConfigureAwait(false),
				"bans" => await BansAsync(mgr, rest).ConfigureAwait(false),
				"trade" => await TradeAsync(mgr, rest).ConfigureAwait(false),
				"levelup" or "lvlup" => LevelUp(mgr, rest),
				"selfcheck" or "tells" => await SelfCheckAsync(mgr, rest).ConfigureAwait(false),
				"human" => Human(mgr, rest),
				"habits" => Habits(mgr, rest),
				"wake" or "wakeup" or "skipsleep" => Wake(mgr, rest),
				"redeem" => await RedeemAsync(mgr, rest, line).ConfigureAwait(false),
				"send" or "loot" => await SendAsync(mgr, rest).ConfigureAwait(false),
				"2fa" or "guard" => TwoFactor(mgr, rest),
				"confirmations" => await ConfirmationsAsync(mgr, rest).ConfigureAwait(false),
				"confirm" => await AnswerConfirmationsAsync(mgr, rest, true).ConfigureAwait(false),
				"deny" => await AnswerConfirmationsAsync(mgr, rest, false).ConfigureAwait(false),
				"cheevo" or "ach" or "achievements" => await CheevoAsync(mgr, rest).ConfigureAwait(false),
				"hunt" => await HuntAsync(mgr, rest).ConfigureAwait(false),
				"dlc" or "dlcach" or "dlcachievements" => await DlcAsync(mgr, rest).ConfigureAwait(false),
				"value" or "inv" or "inventory" => InventoryText(mgr, rest),
				"keys" => KeysText(rest),
				"match" => await MatchAsync(mgr, rest).ConfigureAwait(false),
				"gamename" or "name" => Name(mgr, rest, line),
				"persona" => Persona(mgr, rest, line),
				"nickname" => await NicknameAsync(mgr, rest, line).ConfigureAwait(false),
				"level" => await LevelAsync(mgr, rest).ConfigureAwait(false),
				"balance" or "wallet" => Balance(mgr, rest),
				"points" => await PointsAsync(mgr, rest).ConfigureAwait(false),
				"booster" or "boosters" => await BoosterAsync(mgr, rest).ConfigureAwait(false),
				"freegames" => await FreeGamesAsync(mgr, rest).ConfigureAwait(false),
				"freeitems" => await FreeItemsAsync(mgr, rest).ConfigureAwait(false),
				"queue" => await QueueAsync(mgr, rest).ConfigureAwait(false),
				"sell" => await SellAsync(mgr, rest).ConfigureAwait(false),
				"fairswap" => await FairSwapCheckAsync(mgr, rest).ConfigureAwait(false),
				"privacy" => await PrivacyAsync(mgr, rest).ConfigureAwait(false),
				"joingroup" => await JoinGroupAsync(mgr, rest, line).ConfigureAwait(false),
				"notify" => await NotifyAsync(rest, atThisPc).ConfigureAwait(false),
				"cards" => Cards(mgr, rest),
				"rep4rep" or "r4r" => await Rep4RepAsync(mgr, rest).ConfigureAwait(false),
				"add" => await AddAsync(mgr, rest).ConfigureAwait(false),
				"remove" or "delete" => await RemoveAsync(mgr, rest).ConfigureAwait(false),
				"enable" => Enable(mgr, rest, true),
				"disable" => Enable(mgr, rest, false),
				"set" => Set(mgr, rest, line),
				"config" => ShowConfig(mgr, rest),
				"import" => await ImportAsync(mgr, rest, line).ConfigureAwait(false),
				"reload" => await ReloadAsync(mgr).ConfigureAwait(false),
				"log" or "logs" => Logs(rest),
				"stats" => StatsText(rest),
				"report" => Report(mgr, rest),
				"weekly" => WeeklyReport.Text(mgr),
				"stuck" or "alarm" => StuckWatch.Text(mgr),
				"backup" => BackupNow(),
				"answer" => Prompt.Answer(Tail(line, 1)) ? "answered" : "nothing is waiting for an answer",
				"theme" or "dark" or "light" => Theme(cmd, rest),
				"screen" or "monitor" or "display" => await ScreenAsync(rest).ConfigureAwait(false),
				"dashboard" or "web" or "link" => (rest.Length > 0) && rest[0].Equals("unlock", StringComparison.OrdinalIgnoreCase)
					? Unlock()
					: (rest.Length > 0) && rest[0].ToLowerInvariant() is "anywhere" or "remote"
						? await Anywhere(mgr, rest[1..]).ConfigureAwait(false)   // 'dashboard anywhere on' - the same as 'anywhere on'
						: DashboardLinks.Text(mgr.Global),
				"unlock" => Unlock(),
				"visitors" or "who" => VisitorsCommand(rest),
				"clear" or "cls" => new Said("clear works in the nocat.farm window and the dashboard - it clears that screen").ToString(),
				"anywhere" or "remote" => await Anywhere(mgr, rest).ConfigureAwait(false),
				"version" or "about" => About(),
				"mini" => Mini(rest),
				"plugins" => PluginList(),
				"owns" => Owns(mgr, rest),
				"addlicense" => await AddLicense(mgr, rest).ConfigureAwait(false),
				// 'update file <path>': the path just as it was typed, not split into words and joined again - from the line's
				// words, a no-break space in a folder's name was a gap, and joined with one space, two in a row ("a  b") were one
				// and there was "no file at" a path that was there.
				"update" => (rest.Length > 0) && rest[0].Equals("file", StringComparison.OrdinalIgnoreCase)
					? await UpdateFromFile(Tail(line, 2), atThisPc).ConfigureAwait(false)
					: await Update(rest).ConfigureAwait(false),
				"exit" or "quit" or "q" => Exit(),
				// A plugin's own command, tried only after every built-in has been ruled out - so a plugin can
				// never take a verb the app already answers to, whatever it registered.
				_ => Plugins.PluginHost.Commands.TryGetValue(cmd, out (string Usage, string Help, Func<string[], Task<string>> Run) added)
					? await added.Run(Loose(line, 1)).ConfigureAwait(false)   // split at spaces, as a plugin's words always were
					: Suggest(parts[0])
			};
		} catch (Exception e) {
			// A command that throws is a bug, not an answer - the reply scrolls away, so the file keeps the stack.
			Log.Failed($"command '{cmd}' failed", e);
			Log.StackToFile(e);

			return $"'{cmd}' failed: {e.GetType().Name}: {Log.Scrub(e.Message)}";
		}
	}

	/// <summary>
	/// The answer to a word that is no command. Never the word: it goes on screen, to the chats and into the log, and what
	/// isn't a command is as often a password, a Steam Guard code or a key pasted in the wrong place - every attempt to show
	/// "only the safe part" of it found another shape it leaked in. A command 1 or 2 typos off it is named.
	/// </summary>
	private static string Suggest(string typed) => Close(typed) is { } near
		? new Said("That's not a command - did you mean '{0}'?", near).ToString()
		: new Said("That's not a command - 'help' lists them.").ToString();

	/// <summary>
	/// Check for a newer release, and on "now", install it.
	///
	/// The check is forced rather than daily-gated: somebody typing this has asked, and answering "I looked
	/// this morning" is not an answer. Installing is always explicit - see SelfUpdate for why nothing here
	/// ever happens on a schedule.
	/// </summary>
	/// <summary>
	/// 'anywhere on|off': Open from anywhere in one go - the same as the Phone page. On opens the dashboard to other devices
	/// if it isn't yet (restarting only the dashboard, so the accounts stay on), switches the router forward on, and waits a
	/// few seconds for the router's answer so the reply can carry the link. From Telegram or Discord too, which is the point:
	/// it can be switched on while away, from the only thing that already reaches you.
	/// </summary>
	private static async Task<string> Anywhere(BotManager mgr, string[] args) {
		GlobalConfig g = mgr.Global;
		string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";

		if (what is "off" or "stop" or "false") {
			lock (ConfigStore.GlobalEditGate) {
				mgr.Global.WebRemoteAccess = false;
				ConfigStore.SaveGlobal(mgr.Global);
			}

			RemoteAccess.Poke();

			return new Said("Open from anywhere is off. The router forward is taken away; your Wi-Fi still works.").ToString();
		}

		// No dashboard, nothing to forward: 'anywhere on' waited fifteen seconds and then said it was "still asking your
		// router", which it never would.
		if (!g.WebEnabled) {
			return new Said("the dashboard is switched off (Web dashboard, in Settings) - turn it on and restart nocat.farm first").ToString();
		}

		if (what is "on" or "start" or "true") {
			if (string.IsNullOrEmpty(g.WebPassword) || (g.WebPassword.Length < RemoteAccess.MinPasswordLength)) {
				return new Said("needs a dashboard password of at least {0} characters first - anyone on the internet can try it. Set one on the Phone page, or: set WebPassword <password>", RemoteAccess.MinPasswordLength).ToString();
			}

			bool relisten = Platform.IsLoopback(g.WebHost ?? "");

			lock (ConfigStore.GlobalEditGate) {
				g = mgr.Global;

				if (relisten) {
					g.WebHost = "0.0.0.0";
				}

				g.WebRemoteAccess = true;
				ConfigStore.SaveGlobal(g);
			}

			if (relisten && (Web.WebHost.Current is { } web)) {
				await web.RelistenAsync().ConfigureAwait(false);
			}

			RemoteAccess.Poke();

			// The router usually answers in a second or two; the reply waits for it, so it can carry the link.
			for (int i = 0; (i < 30) && (RemoteAccess.Link == null) && (RemoteAccess.Problem == null); i++) {
				await Task.Delay(500).ConfigureAwait(false);
			}
		}

		if (!g.WebRemoteAccess) {
			return RemoteAccess.Blocker(g) is { } why
				? new Said("Open from anywhere is off - it {0}", why).ToString()
				: new Said("Open from anywhere is off - 'anywhere on' opens it").ToString();
		}

		return RemoteAccess.Link is { } link ? new Said("Open from anywhere is on: {0} - sign in with the dashboard password", link).ToString()
			: RemoteAccess.Problem is { } problem ? new Said("Open from anywhere is on, but {0}", problem).ToString()
			: new Said("Open from anywhere is on - still asking your router; 'anywhere' shows the link in a moment").ToString();
	}

	/// <summary>
	/// 'screen off': the monitors off, the way the power button's display-off does it. A second and a half first, so
	/// letting go of Enter after typing it here doesn't wake them straight back up.
	/// </summary>
	private static async Task<string> ScreenAsync(string[] args) {
		if ((args.Length == 0) || (args[0].ToLowerInvariant() is not ("off" or "sleep"))) {
			return new Said("screen off turns the screens off - moving the mouse turns them back on").ToString();
		}

		if (!Platform.HasDesktop) {
			return new Said("there's no screen here to turn off").ToString();
		}

		_ = Task.Run(async () => {
			await Task.Delay(1500).ConfigureAwait(false);

			try {
				if (OperatingSystem.IsWindows()) {
					Windows.WindowsIntegration.ScreenOff();
				} else {
					string[] how = OperatingSystem.IsMacOS() ? ["pmset", "displaysleepnow"] : ["xset", "dpms", "force", "off"];
					System.Diagnostics.ProcessStartInfo start = new(how[0]) { UseShellExecute = false };
					foreach (string a in how.Skip(1)) {
						start.ArgumentList.Add(a);
					}
					System.Diagnostics.Process.Start(start)?.Dispose();
				}
			} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
				Log.Warn(new Said("couldn't turn the screen off: {0}", Log.Scrub(e.Message)));
			} catch (Exception e) {
				Log.Failed("screen off", e);
			}
		});

		await Task.CompletedTask.ConfigureAwait(false);

		return new Said("screens off in a moment - move the mouse to turn them back on").ToString();
	}

	/// <summary>'clear' or 'cls', with or without a slash or '!' - handled by whichever screen it was typed in.</summary>
	public static bool IsClear(string line) => Unprefixed(line.Trim()).ToLowerInvariant() is "clear" or "cls";

	/// <summary>'visitors': who has been at the dashboard; 'visitors signout' signs every browser and phone out.</summary>
	private static string VisitorsCommand(string[] args) {
		if ((args.Length > 0) && args[0].Equals("signout", StringComparison.OrdinalIgnoreCase)) {
			Web.WebHost.Current?.SignOutAll();
			Log.Info(new Said("dashboard: every browser and phone signed out"));

			return "Every browser and phone is signed out of the dashboard. Sign in again with the password - change it too if somebody else had it.";
		}

		return Visitors.Describe();
	}

	/// <summary>'unlock': lifts every dashboard sign-in lockout, so whoever mistyped their password can try again now.</summary>
	private static string Unlock() {
		int n = Web.WebHost.Current?.ClearLockouts() ?? 0;

		return n > 0 ? new Said("dashboard unlocked - you can sign in again").ToString() : new Said("nothing was locked - the dashboard sign-in works").ToString();
	}

	private static async Task<string> Update(string[] args) {
		string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";

		if (what == "versions") {
			return await UpdateVersions().ConfigureAwait(false);
		}

		if (what == "to") {
			return await UpdateTo(args[1..]).ConfigureAwait(false);
		}

		bool accept = what is "accept" or "now" or "install";

		string? problem = await UpdateCheck.LookAsync(force: true, quiet: true).ConfigureAwait(false);

		// Not "you're on the newest" when GitHub never answered - that's a guess, and a wrong one on the day it matters.
		if ((problem != null) && (UpdateCheck.Available == null)) {
			return new Said("couldn't reach GitHub to check ({0}) - try again in a minute", problem).ToString();
		}

		// Newer, but not downloadable here yet (the Mac zips come a while after the tag): not "you're on the newest", and a
		// queued install waits for the download rather than being dropped.
		if ((UpdateCheck.Available == null) && (UpdateCheck.NoDownloadYet is { } coming)) {
			string said = UpdateCheck.NoDownloadSaid(coming, queued: UpdateCheck.Queued != null).ToString();

			// Asked to install or skip it: neither can happen before there's a download, and the reply has to say so -
			// it read the same as a plain 'update', so an accept looked queued and a skip looked recorded.
			return (accept || (what == "skip")) && (UpdateCheck.Queued == null)
				? said + " " + new Said("Nothing is queued or skipped yet. Ask again once the download is up.")
				: said;
		}

		if (UpdateCheck.Available == null) {
			return $"You're on the newest release ({Build.Version}).";
		}

		if (what is "skip" or "ignore") {
			// Through the updater, which takes turns with an install about to swap: a skip that comes too late says so,
			// rather than "won't install" a second before it restarts into that very version.
			if (!SelfUpdate.Skip(UpdateCheck.Available)) {
				return new Said("too late to skip {0} - it's already installing", UpdateCheck.Available).ToString();
			}

			return new Said("Skipping {0} - no more reminders about it, and it won't install by itself. The next version after it is announced as usual; 'update accept' still installs {0}.", UpdateCheck.Available).ToString();
		}

		// Installing by hand is choosing it after all.
		if (accept && (UpdateCheck.Skipped != null)) {
			UpdateCheck.Skipped = null;
		}

		// Off Windows there is nothing to accept - it can't swap itself (see SelfUpdate) - so both answers say how.
		if (!SelfUpdate.Supported) {
			return $"{UpdateCheck.Available} is out - you have {Build.Version}."
				+ Environment.NewLine + $"  {UpdateCheck.Url}"
				+ Environment.NewLine + "  " + SelfUpdate.ByHand(UpdateCheck.Available);
		}

		if (!accept) {
			return $"{UpdateCheck.Available} is out - you have {Build.Version}."
				+ Environment.NewLine + $"  {UpdateCheck.Url}"
				+ Environment.NewLine + (UpdateCheck.Queued != null
					? "  " + new Said("{0} is waiting for your accounts to go to sleep - 'update now' installs it right away", UpdateCheck.Queued)
					: "  'update accept' downloads it and restarts into it; 'update skip' skips this version.");
		}

		// Updates set to "install when I click, once everyone's asleep": 'accept' queues it for when the accounts are asleep; 'now' doesn't wait.
		if ((what is not "now") && Config.Live.Global.UpdateClickWaits) {
			return UpdateCheck.Queue(UpdateCheck.Available).ToString();
		}

		// In the background: a 50MB download can take minutes, and the console used to sit frozen for all of them.
		// Progress goes to the log in green every 10%, and any failure is said there in red, with the reason.
		_ = Task.Run(async () => {
			try {
				// Its answer said too: "already downloading", or shutting down, came back here and went nowhere - after the
				// reply had promised a download that wasn't happening.
				if (await SelfUpdate.ApplyAsync(CancellationToken.None).ConfigureAwait(false) is { Length: > 0 } refused) {
					Log.Warn(refused);
				}
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", Log.Scrub(e.Message)));
				Log.Failed("update", e);
			}
		});

		return $"downloading {UpdateCheck.Available} in the background - the log shows how far along it is, and it restarts into it by itself when it's done.";
	}

	/// <summary>
	/// 'update file [zip] [force]': install a nocat.farm zip that's on this PC - a build to try out before it's released -
	/// picked in a window when no zip is named (see <see cref="PickAndInstall"/>),
	/// the way an update goes in, safety copy and putting back included.
	/// </summary>
	/// <remarks>
	/// Only at this PC. It replaces the program with whatever is in the file, so from anywhere else it would be a way to run
	/// any program on this PC: from Steam chat, Telegram, Discord, a plugin, or a dashboard opened on a phone or from the
	/// internet, it is refused. A version that isn't newer needs 'force', so an old zip lying around isn't put in by mistake.
	/// </remarks>
	/// <param name="atThisPc">Typed at this PC - handed down from <see cref="RunAtThisPcAsync"/>, and false from everywhere else.</param>
	/// <param name="rest">Everything typed after 'update file', as it was typed.</param>
	private static async Task<string> UpdateFromFile(string rest, bool atThisPc) {
		// Docker and a service first: there it's "update by hand" wherever it was typed - "only at this PC" sent somebody to
		// the PC for something that can't happen there either.
		if (!SelfUpdate.Supported) {
			return SelfUpdate.ByHand(null).ToString();
		}

		if (!atThisPc) {
			return "update file only works at this PC - in the nocat.farm window or its console, or the dashboard opened on this PC. Not from Steam chat, Telegram, Discord or another device.";
		}

		(string path, bool force) = FileAndForce(rest);

		return path.Length == 0 ? PickAndInstall(force) : await InstallFromFile(path, force).ConfigureAwait(false);
	}

	/// <summary>
	/// What follows 'update file': the path, and whether it ends in the word 'force' (any case, after a space or a tab). Only
	/// that one word comes off the end, and the quotes round the path - the rest is left exactly as typed: two spaces in a
	/// row, a tab inside a quoted path.
	/// </summary>
	internal static (string Path, bool Force) FileAndForce(string rest) {
		const string Force = "force";
		string path = rest.Trim();
		bool force = path.EndsWith(Force, StringComparison.OrdinalIgnoreCase)
			&& ((path.Length == Force.Length) || char.IsWhiteSpace(path[^(Force.Length + 1)]));

		if (force) {
			path = path[..^Force.Length].Trim();
		}

		return (DraggedPath(path.Trim('"', '\'').Trim(), OperatingSystem.IsWindows()), force);
	}

	/// <summary>
	/// A file dragged into a Mac's Terminal is pasted with a backslash before every space and bracket -
	/// .../nocat.farm-v1.7.4_osx-arm64\ \(1\).zip - and there was "no file at" it. Off Windows, a path that isn't there is taken
	/// without them. On Windows a backslash is a folder, and stays.
	/// </summary>
	internal static string DraggedPath(string path, bool windows) =>
		windows || !path.Contains('\\') || File.Exists(path) ? path : Escaped().Replace(path, "$1");

	/// <summary>A backslash and the character it stands before.</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"\\(.)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex Escaped();

	/// <summary>
	/// The "pick a file" window for 'update file' typed with no path: on Windows, the nocat.farm window's own, opened on
	/// this PC's desktop. Null where there's no desktop to open one on - Linux, a Mac, Docker, --no-gui, a service.
	/// </summary>
	private static Func<Task<string?>>? Picker() =>
		FilePicker ?? (OperatingSystem.IsWindows() && (Window is { CanPick: true } window) ? window.PickZipAsync : null);

	/// <summary>Stands in for the "pick a file" window in the checks, Cancel and all. Null: the real one, where there is one.</summary>
	internal static Func<Task<string?>>? FilePicker { get; set; }

	/// <summary>Whether 'update file' with no path can open a window to pick the zip here - for the dashboard's button.</summary>
	public static bool CanPickFile => SelfUpdate.Supported && (Picker() != null);

	/// <summary>The window to pick a file, while it's open - one at a time. The checks wait on it.</summary>
	internal static Task? Picking { get; private set; }

	private static int _picking;

	/// <summary>
	/// 'update file' with no path: a window to pick the zip opens on this PC, and whatever is picked goes through exactly
	/// what a typed path does. The answer can't wait for it - typed in the dashboard, the page would sit there until the
	/// window closed - so what happens next goes in the log.
	/// </summary>
	private static string PickAndInstall(bool force) {
		if (!SelfUpdate.Supported) {
			return SelfUpdate.ByHand(null).ToString();
		}

		if (Picker() is not { } pick) {
			return "There's no window to pick a file with here. Type where the zip is:  update file <path to the zip>";
		}

		if (SelfUpdate.Busy) {
			return "an update is already going - give it a minute";
		}

		if (Interlocked.CompareExchange(ref _picking, 1, 0) != 0) {
			return "The window to pick the file is already open on this PC.";
		}

		Picking = Task.Run(async () => {
			try {
				string? chosen;

				try {
					chosen = await pick().ConfigureAwait(false);
				} catch (Exception e) {
					Log.Failed("update file: the window to pick a file", e);
					Log.Warn(new Said("the window to pick a file didn't open ({0}) - type where the zip is: update file <path>", Log.Scrub(e.Message)));

					return;
				}

				if (string.IsNullOrEmpty(chosen)) {
					Log.Info(new Said("nothing chosen - nothing changed"));

					return;
				}

				Log.Info(await InstallFromFile(chosen, force).ConfigureAwait(false));
			} catch (Exception e) {
				// Nobody awaits this task: a throw left in it reached the lost-task handler, said without what it was.
				Log.Error(new Said("update failed: {0} - nothing was changed", Log.Cause(e)));
				Log.Failed("update file", e);
			} finally {
				Volatile.Write(ref _picking, 0);
			}
		});

		return "A window to pick the file opened on this PC. Pick the nocat.farm zip there, or Cancel to change nothing.";
	}

	/// <summary>A zip on this PC, typed or picked: checked, then installed the way an update is.</summary>
	private static async Task<string> InstallFromFile(string path, bool force) {
		if (!SelfUpdate.Supported) {
			return SelfUpdate.ByHand(null).ToString();
		}

		if (SelfUpdate.Busy) {
			return "an update is already going - give it a minute";
		}

		// ~ is the home folder, as a shell has it: 'update file ~/Downloads/x.zip' looked for a folder called "~" in here.
		if ((path == "~") || path.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal))) {
			path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
		}

		try {
			path = Path.GetFullPath(path);
		} catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
			return $"'{path}' isn't a path to a file";
		}

		(string? version, Said problem) = SelfUpdate.LookInZip(path);

		if (version == null) {
			return problem.ToString();
		}

		// Before 'force' is offered: forced in, it signed every account out, never said it started, and was put back.
		if (SelfUpdate.TooOld(version) is { } tooOld) {
			return tooOld.ToString();
		}

		string file = Path.GetFileName(path);

		if (!force && !UpdateCheck.IsNewerThanThisBuild(version)) {
			return $"{file} has nocat.farm {version}, and you have {Build.Version} - {(version == Build.Version ? "the same version" : "an older one")}. To install it anyway:  update file {path} force";
		}

		// A file goes in skipped or not (the install doesn't look), and a skip of the release with its number is left as it is:
		// the file is a build of its own, a test build often. Cleared here, a test build that crashed and was put back - or an
		// install that stopped before the swap - lost the skip, and "install at night" put that release in after all.
		bool back = UpdateCheck.IsOlderThanThisBuild(version);

		// Which version to hold off depends on the newest release, and 'update to' has always asked for it by then - a zip
		// may well be the first thing typed after starting. Not knowing it, this version was held off even with a newer one
		// out, and a skip typed for that one was lost. Asked here, before the install reads it too; a list from the last ten
		// minutes does, and with GitHub out of reach it holds off this version as before.
		if (back && (UpdateCheck.Latest == null)) {
			_ = await UpdateCheck.ReleasesAsync(fresh: false).ConfigureAwait(false);
		}

		string? hold = back ? Rollback.HoldOff(version)?.TrimStart('v') : null;

		_ = Task.Run(async () => {
			try {
				if (await SelfUpdate.ApplyAsync(CancellationToken.None, fromFile: path).ConfigureAwait(false) is { Length: > 0 } refused) {
					Log.Warn(refused);
				}
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", Log.Scrub(e.Message)));
				Log.Failed("update file", e);
			}
		});

		return $"installing nocat.farm {version} from {file} - the log shows how it goes, and it restarts into it by itself. If it doesn't start, {Build.Version} is put back."
			+ (back ? " " + GoingBackSaid(version, hold) : "");
	}

	/// <summary>What going back to an older version keeps, and what it holds off - the same words wherever it's asked for.</summary>
	/// <param name="hold">Null when nothing is held off: the newest release is newer than this one, and that's what the older
	/// version offers (or leaves alone, if it was skipped).</param>
	private static string GoingBackSaid(string version, string? hold) =>
		$"Your settings are saved first, and the ones {version} doesn't know come back when you update again."
			+ (hold != null ? $" {hold} won't install by itself until a newer one is out; 'update accept' brings it back." : "");

	/// <summary>'update versions': the older versions still on GitHub, that 'update to' can go back to.</summary>
	private static async Task<string> UpdateVersions() {
		(List<string>? all, Said? problem) = await UpdateCheck.ReleasesAsync(fresh: true).ConfigureAwait(false);

		if (all == null) {
			return new Said("couldn't reach GitHub to check ({0}) - try again in a minute", problem).ToString();
		}

		List<string> older = [.. all.Where(static v => UpdateCheck.IsOlderThanThisBuild(v))];

		if (older.Count == 0) {
			return $"You have {Build.Version}. There's no older version on GitHub to go back to - only the last 5 releases are kept there.";
		}

		return $"You have {Build.Version}. You can go back to: {string.Join(", ", older)}"
			+ Environment.NewLine + $"  To go back:  update to {older[0]}"
			+ Environment.NewLine + "  Your settings are saved first, and come back when you update again.";
	}

	/// <summary>
	/// 'update to &lt;version&gt;': that release from GitHub, installed through the normal update path - an older one too, which
	/// is what it's for, without 'force'. The same rights as 'update accept': the PC, the dashboard signed in, Telegram,
	/// Discord - never Steam chat.
	/// </summary>
	private static async Task<string> UpdateTo(string[] args) {
		string version = args.Length > 0 ? args[0].Trim().TrimStart('v', 'V') : "";

		if (!UpdateCheck.IsVersion(version)) {
			return "update to <version> - like update to 1.7.1. 'update versions' lists the ones you can go back to.";
		}

		if (UpdateCheck.Compare(version, Build.Version) == 0) {
			return $"You're on {Build.Version} already.";
		}

		// Docker and a service update by hand, whichever version it is - told how to put in THAT one, not the newest. Going
		// back, the settings are saved first all the same: the older version drops the ones it doesn't know whoever put it in,
		// and they come back once this one or a newer one starts again.
		if (!SelfUpdate.Supported) {
			if (!UpdateCheck.IsOlderThanThisBuild(version)) {
				return SelfUpdate.ByHandTo(version).ToString();
			}

			try {
				Rollback.Snapshot(version);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed("update to: saving the settings before going back", e);

				return new Said("update stopped - couldn't save your settings first ({0}); nothing changed", Log.Scrub(e.Message)).ToString();
			}

			return SelfUpdate.ByHandTo(version).ToString() + " " + $"Your settings are saved first, and the ones {version} doesn't know come back when you update again.";
		}

		// Too old to say it started: no point asking GitHub for it.
		if (SelfUpdate.TooOld(version) is { } tooOld) {
			return tooOld.ToString();
		}

		if (SelfUpdate.Busy) {
			return "an update is already going - give it a minute";
		}

		(List<string>? all, Said? problem) = await UpdateCheck.ReleasesAsync(fresh: true).ConfigureAwait(false);

		if (all == null) {
			return new Said("couldn't reach GitHub to check ({0}) - try again in a minute", problem).ToString();
		}

		// There, but with no zip for this computer - yet: the Mac zips go up a while after the rest.
		if (!all.Contains(version) && UpdateCheck.Tagged.Contains(version)) {
			return $"{version} has no download for this computer (yet) - try again later.";
		}

		if (!all.Contains(version)) {
			List<string> older = [.. all.Where(static v => UpdateCheck.IsOlderThanThisBuild(v))];

			return $"{version} isn't on GitHub any more - only the last 5 releases are kept there."
				+ (older.Count > 0 ? $" You can go back to: {string.Join(", ", older)}." : " There's no older version there to go back to.");
		}

		// Choosing a version by hand is choosing it, skipped or not - as 'update accept' does.
		if (UpdateCheck.IsSkipped(version)) {
			UpdateCheck.Skipped = null;
		}

		bool back = UpdateCheck.IsOlderThanThisBuild(version);
		string? hold = back ? Rollback.HoldOff(version)?.TrimStart('v') : null;

		_ = Task.Run(async () => {
			try {
				if (await SelfUpdate.ApplyAsync(CancellationToken.None, toVersion: version).ConfigureAwait(false) is { Length: > 0 } refused) {
					Log.Warn(refused);
				}
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", Log.Scrub(e.Message)));
				Log.Failed("update to", e);
			}
		});

		return back
			? $"Going back to {version}: downloading it now - the log shows how it goes, and it restarts into it by itself. " + GoingBackSaid(version, hold)
			: $"downloading {version} in the background - the log shows how far along it is, and it restarts into it by itself when it's done.";
	}

	/// <summary>
	/// Who already owns a game, across the whole fleet.
	///
	/// The question you ask before buying something: an account that already owns it does not need another
	/// copy, and one that owns it with no hours on it is a card-farming candidate nobody has touched yet.
	/// Accepts an appID, a store URL, or part of a name, because nobody remembers appIDs.
	/// </summary>
	private static string Owns(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return "owns <appID|name>       which accounts already have it";
		}

		string term = string.Join(' ', args).Trim();
		uint wanted = Settings.AppIdFrom(term);
		List<Bot> bots = mgr.All.Where(static b => b.Library.Ready).ToList();

		if (bots.Count == 0) {
			return "No account has read its library yet - give it a moment after signing in.";
		}

		// An appID is exact; a name is a contains-match across every library, so one search can turn up several
		// games and the answer has to say which is which.
		List<(uint App, string Name)> hits = wanted > 0
			? [(wanted, GameNames.Of(wanted))]
			: bots.SelectMany(static b => b.Library.Games)
				.Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
				.GroupBy(static g => g.AppId)
				.Select(static g => (g.Key, g.First().Name))
				.OrderBy(static g => g.Item2, StringComparer.OrdinalIgnoreCase)
				.Take(12)
				.ToList();

		if (hits.Count == 0) {
			return $"Nothing in any library matches '{term}'.";
		}

		StringBuilder sb = new();

		foreach ((uint app, string name) in hits) {
			List<Bot> owners = bots.Where(b => b.Library.Find(app) != null).ToList();

			sb.AppendLine($"{name}  ({app})");

			if (owners.Count == 0) {
				sb.AppendLine("  nobody owns it");

				continue;
			}

			foreach (Bot bot in owners) {
				Library.Entry entry = bot.Library.Find(app)!;
				string how = entry.SharedFrom != 0 ? " (family)" : "";
				string played = entry.MinutesPlayed > 0 ? Fmt.Hm(entry.MinutesPlayed) : "never played";

				sb.AppendLine($"  {bot.Name,-14} {played}{how}");
			}
		}

		return sb.ToString().TrimEnd();
	}

	/// <summary>
	/// Add free packages by subID.
	///
	/// The same call the free-games watcher makes, exposed for the times you know the subID yourself - a
	/// giveaway that has not been picked up yet, or a free weekend. Steam refuses anything that is not actually
	/// free, so the worst case is a "no".
	/// </summary>
	/// <summary>One free app, the same request the free-games watcher makes - so the two can never drift apart.</summary>
	private static async Task<string> AddFreeAppAsync(Bot bot, uint appId) {
		FreeGames.ClaimResult result = await FreeGames.AddAppAsync(bot, appId, CancellationToken.None).ConfigureAwait(false);

		return result.Added ? $"added {GameNames.Of(appId)}" : $"not granted - {result.Reason}";
	}

	private static async Task<string> AddLicense(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "addlicense <account|all> <IDs>     subIDs, or a/<appID> for a free app - comma or space separated";
		}

		List<Bot> targets = args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
			? mgr.All.Where(static b => b.IsOnline).ToList()
			: mgr.Get(args[0]) is { } one ? [one] : [];

		if (targets.Count == 0) {
			return args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
				? "No account is online."
				: NoSuchAccount(mgr, args[0]);
		}

		// A bare number or s/123 is a package, a/123 is an app. Apps go over the Steam
		// connection rather than the store, which is the only way a free-to-play app can be added at all.
		List<(bool App, uint Id)> wanted = string.Join(' ', args[1..])
			.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static s => {
				bool app = s.StartsWith("a/", StringComparison.OrdinalIgnoreCase);
				string digits = app || s.StartsWith("s/", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;

				return (App: app, Id: uint.TryParse(digits, out uint n) ? n : 0);
			})
			.Where(static w => w.Id > 0)
			.Distinct()
			.ToList();

		if (wanted.Count == 0) {
			return "No ID in that. A subID is a number - 12345 - and a free app is a/12345. Several can be separated by commas.";
		}

		async Task<string> Add(Bot bot) {
			// Every ID this batch will ask for on this account, taken before the first is asked for and given back once it's
			// through: an ID another addlicense is already adding here is left to it. Two batches with an ID in common asked
			// Steam for it twice, a second or so apart.
			HashSet<(bool App, uint Id)> mine = [];

			lock (Licensing) {
				foreach ((bool App, uint Id) w in wanted) {
					if (Licensing.Add((bot.Name.ToLowerInvariant(), w.App, w.Id))) {
						mine.Add(w);
					}
				}
			}

			try {
				return await AddMine(bot, mine).ConfigureAwait(false);
			} finally {
				lock (Licensing) {
					foreach ((bool App, uint Id) w in mine) {
						Licensing.Remove((bot.Name.ToLowerInvariant(), w.App, w.Id));
					}
				}
			}
		}

		async Task<string> AddMine(Bot bot, HashSet<(bool App, uint Id)> mine) {
			StringBuilder sb = new();

			foreach ((bool app, uint id) in wanted) {
				if (!mine.Contains((app, id))) {
					sb.AppendLine($"{bot.Name}: {(app ? "a/" : "")}{id} - already being added by another addlicense");

					continue;
				}

				if (app) {
					sb.AppendLine($"{bot.Name}: a/{id} - {await AddFreeAppAsync(bot, id).ConfigureAwait(false)}");

					continue;
				}

				if (bot.OwnsPackage(id)) {
					sb.AppendLine($"{bot.Name}: {id} - already has it");

					continue;
				}

				FreeGames.ClaimResult result = await FreeGames.AddPackageAsync(bot, id, CancellationToken.None).ConfigureAwait(false);
				sb.AppendLine($"{bot.Name}: {id} - {(result.Added ? "added" : $"refused - {result.Reason}")}");
			}

			return sb.ToString().TrimEnd();
		}

		// Several IDs on every account, one request after another: past the reply's wait, the rest goes to the log.
		// Other IDs are another job; the same ones, however they were typed, aren't added twice at once.
		Slow job = new("addlicense", "adding the licences",
			Key: "addlicense " + string.Join(',', wanted.Select(static w => (w.App ? "a/" : "") + w.Id.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)));

		return await SlowAsync(job, targets, Add, ReplyWithin, oneAtATime: true,
			cannot: static b => b.IsOnline ? null : $"{b.Name}: not online").ConfigureAwait(false);
	}

	/// <summary>The licences an addlicense is asking for right now, by account - see AddLicense.</summary>
	private static readonly HashSet<(string Account, bool App, uint Id)> Licensing = [];

	private static string PluginList() {
		if (!Live.Global.PluginsEnabled) {
			return "Plugins are off. Turn them on with 'set PluginsEnabled true' and restart - read what that setting says first.";
		}

		IReadOnlyList<(string Name, string Version, string File)> running = Plugins.PluginHost.Running;

		if (running.Count == 0) {
			return $"Plugins are on, but nothing loaded. Put a .dll in:{Environment.NewLine}  {Plugins.PluginHost.Folder}";
		}

		StringBuilder sb = new();
		sb.AppendLine($"{running.Count} plugin(s) loaded:");

		foreach ((string name, string version, string file) in running) {
			sb.AppendLine($"  {name,-24} {version,-10} {file}");
		}

		IReadOnlyDictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> added = Plugins.PluginHost.Commands;

		if (added.Count > 0) {
			sb.AppendLine();
			sb.AppendLine("commands they added:");

			foreach ((string verb, (string usage, string help, _)) in added.OrderBy(static c => c.Key, StringComparer.Ordinal)) {
				foreach (string line in UsageLines((verb + " " + usage).TrimEnd(), 32, help)) {
					sb.AppendLine(line);
				}
			}
		}

		return sb.ToString().TrimEnd();
	}

	private static string Exit() {
		RequestExit();

		return "";   // the host logs "shutting down" - saying it twice looks broken
	}

	/// <summary>
	/// Set the dashboard theme.
	///
	/// Stored globally rather than in the browser, so it follows you between browsers and survives clearing
	/// site data - the page still keeps its own copy so it can paint before it has talked to the server.
	/// Typing 'dark' or 'light' on its own works too, because that is what people actually try.
	/// </summary>
	private static string Theme(string verb, string[] args) {
		string want = verb is "dark" or "light" ? verb : args.Length > 0 ? args[0].ToLowerInvariant() : "";

		if (want.Length == 0) {
			return $"The dashboard is on the {Live.Global.Theme} theme. Say 'theme light' or 'theme dark' to change it.";
		}

		if (want is not ("dark" or "light")) {
			return $"'{want}' is not a theme - it is either dark or light.";
		}

		lock (ConfigStore.GlobalEditGate) {
			Live.Global.Theme = want;
			ConfigStore.SaveGlobal(Live.Global);
		}

		return $"Dashboard set to the {want} theme. Reload the page to see it.";
	}

	private static string About() =>
		$"""
		nocat.farm {Build.Version} - Steam idler, trading-card farmer and rep4rep commenter.
		Everything runs on this PC. Your accounts never leave it; the only thing that talks
		to rep4rep is the task queue. Once an hour it tells nocat.lol it's running, to count
		Steam accounts: a random install ID, the version, the platform and how many accounts
		are signed in (just the number), nothing else. Count me as a user turns it off.
		""";

	// ── help ────────────────────────────────────────────────────────────────
	/// <summary>One setting explained: its name, label, the dashboard's sentence, its range and its default.</summary>
	private static string SettingHelp(SettingDef def, object defaults) {
		StringBuilder sb = new();
		sb.AppendLine($"{def.Name}  ({def.Label})");
		sb.AppendLine($"  {def.Tooltip}");

		if (def.Choices != null) {
			sb.AppendLine($"  One of: {def.Choices}");
		}

		if (def.Min > double.MinValue || def.Max < double.MaxValue) {
			sb.AppendLine($"  Between {def.Min:0.##} and {def.Max:0.##}.");
		}

		object? fallback = Settings.Read(defaults, def.Name);
		sb.AppendLine($"  Default: {(fallback is List<uint> l ? (l.Count == 0 ? "(none)" : string.Join(", ", l)) : fallback)}");

		if (def.NeedsRestart) {
			sb.AppendLine("  Takes effect the next time nocat.farm starts.");
		}

		return sb.ToString().TrimEnd();
	}

	/// <summary>
	/// 'help' with only the start of a name: every command and every setting that begins with it. "help rot" used to
	/// say "nothing called 'rot'" - now it lists rotation and "Rotate the idle list", and one command on its own is
	/// explained in full as if it had been typed out.
	/// </summary>
	internal static string HelpStartingWith(string start) {
		const int Most = 15;

		if (start.Length == 0) {
			return "Type 'help' for commands, or 'config' to list settings.";
		}

		bool Begins(string s) => s.StartsWith(start, StringComparison.OrdinalIgnoreCase);

		List<CommandDef> commands = [.. All.Where(c => Begins(c.Name) || c.Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(Begins))];

		// A setting by its name (RotateIdleGames) or by any word of what the dashboard calls it ("Rotate the idle list").
		List<SettingDef> settings = [.. Settings.Global.Concat(Settings.Bot)
			.Where(d => Begins(d.Name) || d.Label.Split([' ', '-', '·', '.', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries).Any(Begins))
			.DistinctBy(static d => d.Name)];

		if ((commands.Count == 1) && (settings.Count == 0)) {
			return $"{commands[0].Display} {commands[0].Args}\n  {commands[0].Help}";
		}

		if ((commands.Count == 0) && (settings.Count == 0)) {
			return $"Nothing starts with '{start}'. Type 'help' for commands, or 'config' to list settings.";
		}

		StringBuilder sb = new();

		if (commands.Count > 0) {
			sb.AppendLine($"Commands starting with '{start}':");

			foreach (CommandDef c in commands.Take(Most)) {
				string left = (c.Display + " " + c.Args).TrimEnd();
				string what = Columns.ClipChars(c.Help, 70, "...");

				foreach (string line in UsageLines(left, 34, what)) {
					sb.AppendLine(line);
				}
			}

			if (commands.Count > Most) {
				sb.AppendLine($"  ...and {commands.Count - Most} more - type a little more of it");
			}
		}

		if (settings.Count > 0) {
			if (commands.Count > 0) {
				sb.AppendLine();
			}

			sb.AppendLine($"Settings starting with '{start}':");

			foreach (SettingDef d in settings.Take(Most)) {
				foreach (string line in UsageLines(d.Name, 34, d.Label)) {
					sb.AppendLine(line);
				}
			}

			if (settings.Count > Most) {
				sb.AppendLine($"  ...and {settings.Count - Most} more - type a little more of it");
			}
		}

		sb.Append("'help <name>' explains one.");

		return sb.ToString();
	}

	/// <summary>
	/// A usage and what it does, the usage padded to <paramref name="column"/> - or, when it doesn't fit with two spaces to
	/// spare, on a line of its own with the description under it, indented to the column. Only a usage LONGER than the
	/// column used to wrap: one exactly as wide ran straight into its description - "joingroup &lt;account|all&gt; &lt;group
	/// link or name&gt;Join a Steam group..." - and one a column short was left a single space.
	/// </summary>
	public static string[] UsageLines(string usage, int column, string what) =>
		Columns.Width(usage) + 2 > column
			? [$"  {usage}", $"  {new string(' ', column)}{what}"]
			: [$"  {Columns.PadRight(usage, column)}{what}"];

	private static string Help(string[] args) {
		if (args.Length > 0) {
			// 'help set <key>' and 'help <key>' both explain a setting - the same sentence the dashboard shows.
			string wanted = args[^1];

			// A command typed in lower case ("help joingroup") is the command, even when a setting shares the word
			// (JoinGroup) - settings are written in their own CamelCase, and 'help set <key>' always means the setting.
			bool command = (args.Length == 1) && !wanted.Any(char.IsUpper) && All.Any(c => c.Matches(wanted));
			SettingDef? def = command ? null : Settings.Find(wanted);

			if (def != null) {
				// A setting that exists for the whole app AND per account (StatusEveryMinutes, say) is two settings with one
				// name. Explaining only the first one found left the per-account one out of reach of 'help' entirely.
				SettingDef? global = Settings.FindGlobal(def.Name);
				SettingDef? perAccount = Settings.FindBot(def.Name);

				if ((global != null) && (perAccount != null)) {
					return "For the whole app (Global settings):\n" + SettingHelp(global, Settings.GlobalDefaults)
						+ "\n\nPer account (set it on one account to override the whole-app one):\n" + SettingHelp(perAccount, Settings.BotDefaults);
				}

				return SettingHelp(def, global != null ? Settings.GlobalDefaults : Settings.BotDefaults);
			}

			CommandDef? cmd = All.FirstOrDefault(c => c.Matches(wanted));

			if (cmd != null) {
				return $"{cmd.Display} {cmd.Args}\n  {cmd.Help}";
			}

			return HelpStartingWith(wanted.TrimStart('/', '!'));
		}

		StringBuilder help = new();

		foreach (string group in All.Select(static c => c.Group).Distinct()) {
			help.AppendLine(group.ToUpperInvariant());

			foreach (CommandDef c in All.Where(c => c.Group == group)) {
				// Wrap rather than widen. Padding to fit the longest entry would push every other line's help
				// text 24 columns to the right to accommodate three commands, which reads far worse than the
				// three long ones taking a second line.
				foreach (string line in UsageLines((c.Display + " " + c.Args).TrimEnd(), 44, c.Help)) {
					help.AppendLine(line);
				}
			}

			help.AppendLine();
		}

		help.AppendLine("Anything after a | is a shorter way to type the same command.");
		help.AppendLine("Settings aren't listed here - there are far too many. 'config' shows every one with its");
		help.AppendLine("current value, 'help <setting>' explains a single one (e.g. 'help HoursUntilCardDrops'),");
		help.Append("and 'set <account> <setting> <value>' changes it - drop the account for a global setting.");

		return help.ToString();
	}

	// ── accounts ────────────────────────────────────────────────────────────
	private static string Status(BotManager mgr, string? which) {
		IReadOnlyCollection<Bot> bots = mgr.All;

		if (bots.Count == 0) {
			return "No accounts yet. Add one with:  add <name> <steamLogin>";
		}

		if (!string.IsNullOrEmpty(which) && !which.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			Bot? one = mgr.Get(which);

			if (one == null) {
				return NoSuchAccount(mgr, which);
			}

			bots = [one];
		}

		// A real table: fixed columns, a rule under the header, and the per-module detail on its own indented
		// line instead of a run-on tail that wraps and destroys the alignment.
		StringBuilder sb = new();
		string bar = Log.Bar;

		sb.AppendLine($"  {"ACCOUNT",-13}{"STATE",-13}{"UPTIME",-8}{"PLAYING",-24}{"CARDS",-7}REP4REP");
		sb.AppendLine("  " + new string('─', 70));

		foreach (Bot b in bots) {
			string uptime = b.OnlineSince == null ? "—" : Fmt.Hm((int) (DateTime.UtcNow - b.OnlineSince.Value).TotalMinutes);
			string playing = string.IsNullOrEmpty(b.Playing) ? "—" : b.Playing;
			string cards = b.CardsRemaining > 0 ? b.CardsRemaining.ToString() : "—";
			Rep4RepModule? r4r = BotManager.ModuleOf<Rep4RepModule>(b);
			string comments = b.Cfg.Rep4Rep && r4r != null ? $"{r4r.PostsToday}/{r4r.Cap}" : "—";

			// One space always left between columns: a value cut to the full width ran straight into the next one
			// ("waiting for …—"). A state that doesn't fit is given whole on its own line below.
			string state = StateWord(b);
			sb.AppendLine($"  {Log.Pad(b.Name, 12)} {Log.Pad(state, 12)} {Log.Pad(uptime, 7)} {Log.Pad(playing, 23)} {Log.Pad(cards, 6)} {comments}");

			if (Columns.Width(state) > 12) {
				sb.AppendLine($"    {bar} {Log.Pad("state", 12)} {state}");
			}

			if (b.GuardPrompt != null) {
				sb.AppendLine($"    {bar} waiting on you: {b.GuardPrompt}");
			}

			foreach (IBotModule m in b.Modules) {
				// Module statuses are localised, so a bare `is not ("idle" or "off")` only ever matched in
				// English and printed a wall of resting modules in every other language. Compare against the
				// same words in the same language.
				if (!string.IsNullOrEmpty(m.Status) && !Core.Loc.Is(m.Status, "idle") && !Core.Loc.Is(m.Status, "off")) {
					sb.AppendLine($"    {bar} {Log.Pad(m.Name, 12)} {m.Status}");
				}
			}
		}

		return sb.ToString().TrimEnd();
	}

	public static string StateWord(Bot b) {
		if (!b.Cfg.Enabled) {
			return "disabled";
		}

		// Only while it's running. A pause outlives a stop (and 'pause all' reaches stopped accounts too), and a signed-out
		// account read "paused" in the status, on its card and on Telegram.
		if (b.Paused && b.Running) {
			return "paused";
		}

		if (b.State == BotState.Online) {
			if (b.PlayingBlocked) {
				return "stood down";
			}

			if (b.Stopping) {
				return "finishing up";
			}

			if (b.IsFarming) {
				return "farming";
			}

			// A grind outranks everything below it, and nothing here was asking. An account put on one game
			// for three hours reported itself as "idling" - the same word as an account doing nothing in
			// particular - while the log line right above it said "grinding". One of them had to be wrong.
			if (b.Grinding) {
				return "grinding";
			}

			// "online" while a game is clearly running was the confusing one - say what it is actually doing.
			HumanMode? human = BotManager.ModuleOf<HumanMode>(b);

			if (human is { Current: not HumanMode.Phase.Off }) {
				return human.Current switch {
					HumanMode.Phase.Playing => "playing",
					HumanMode.Phase.ShortBreak or HumanMode.Phase.MealBreak => "on a break",
					HumanMode.Phase.NightIdle => "offline idling",
					HumanMode.Phase.Asleep => "asleep",
					HumanMode.Phase.DoneForToday => "done today",

					// These were missing, so an account that was plainly settling in or closing a game down
					// reported itself as "online" - the one word this whole readout exists to replace.
					HumanMode.Phase.WarmingUp => "settling in",
					HumanMode.Phase.SwitchingGame => "switching game",
					HumanMode.Phase.DayOff => "day off",
					HumanMode.Phase.StoodDown => "stood down",
					_ => "online"
				};
			}

			return string.IsNullOrEmpty(b.Playing) ? "online" : "idling";
		}

		return b.StatusText;
	}

	private static string NoSuchAccount(BotManager mgr, string name) {
		string known = mgr.All.Count == 0 ? "none yet" : string.Join(", ", mgr.All.Select(static b => b.Name));

		return $"There's no account called '{name}'. You have: {known}";
	}

	private static async Task<string> LifecycleAsync(BotManager mgr, string[] args, string verb, bool graceful = false, bool untilStopped = false) {
		if (args.Length == 0) {
			return $"{verb} <account|all>";
		}

		bool all = args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
		IEnumerable<Bot> targets;

		if (all) {
			targets = mgr.All;
		} else {
			Bot? one = mgr.Get(args[0]);

			if (one == null) {
				return NoSuchAccount(mgr, args[0]);
			}

			targets = [one];
		}

		int count = 0;

		// "pause kylro 30" pauses for half an hour and then picks back up by itself.
		TimeSpan? pauseFor = null;

		if ((verb == "pause") && (args.Length > 1)) {
			// Finite: "NaN" and "Infinity" parse as doubles, and a NaN wait threw from TimeSpan instead of saying this.
			if (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || !double.IsFinite(minutes) || (minutes <= 0)) {
				return $"'{args[1]}' isn't a number of minutes - e.g. pause {args[0]} 30";
			}

			pauseFor = TimeSpan.FromMinutes(Math.Min(minutes, 7 * 24 * 60));
		}

		List<(Bot Bot, Task Task)> stops = [];
		int disabled = 0;

		foreach (Bot bot in targets.ToArray()) {
			switch (verb) {
				case "start":
					if (!bot.Cfg.Enabled) {
						disabled++;

						continue;
					}

					await bot.StartAsync().ConfigureAwait(false);

					break;
				case "stop":
					stops.Add((bot, bot.StopAsync(graceful)));

					break;
				case "pause":
					bot.Pause(pauseFor);

					break;
				case "resume":
					bot.Resume();
					BotManager.ModuleOf<Idler>(bot)?.Assert();

					break;
			}

			count++;
		}

		// A human-mode account finishes up for up to half a minute (LegitStopMaxSeconds) before it signs out. "signing out" is
		// true from the start, so past the reply's wait it answers and the stop carries on - a restart waits for it.
		if (stops.Count > 0) {
			if (untilStopped) {
				await Task.WhenAll(stops.Select(static s => s.Task)).ConfigureAwait(false);
			} else {
				// Each one's failure said under its own name: past the reply's wait nothing else looks at it, and a stop left
				// faulted turned up later as "a background task failed", in red.
				Task stopped = Task.WhenAll(stops.Select(static s => s.Task.ContinueWith(t => {
					if (t.Exception?.GetBaseException() is { } e) {
						Fell("stop", e, [s.Bot], null, s.Bot.Name);
					}
				}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)));

				await Task.WhenAny(stopped, Task.Delay(ReplyWithin)).ConfigureAwait(false);
			}
		}

		// Echoing the verb back - "kylro: pause" - reads like the command bounced rather than ran. Say what
		// actually happened to the account instead, in the same shape as every other command's reply.
		string what = verb switch {
			"start" => "signing in",
			"stop" => "signing out",
			"pause" => pauseFor is { } span
				? $"paused for {Fmt.Hm((int) Math.Ceiling(span.TotalMinutes))} - picks back up by itself"
				: "paused - staying signed in, but not playing, farming or commenting",
			"resume" => "resumed",
			_ => verb
		};

		// The account's own name, not as typed - "pause KYLRO" answered "KYLRO: paused".
		string named = all ? "" : targets.First().Name;

		// A disabled account is skipped, and the reply has to say so - "signing in" about an account that never
		// will left people waiting on it.
		if (!all && (disabled > 0)) {
			return $"{named} is disabled, so it won't sign in. 'enable {named}' lets it sign in again.";
		}

		return all ? $"{what}: {count} account(s)" + (disabled > 0 ? $" ({disabled} disabled, left alone)" : "") : $"{named}: {what}";
	}

	private static async Task<string> RestartAsync(BotManager mgr, string[] args) {
		// Checked before anything stops: a bare 'restart' waited a second and a half and then answered with the usage
		// of 'start', and a mistyped name was only reported after the pause.
		if (args.Length == 0) {
			return "restart <account|all>";
		}

		if (!args[0].Equals("all", StringComparison.OrdinalIgnoreCase) && (mgr.Get(args[0]) == null)) {
			return NoSuchAccount(mgr, args[0]);
		}

		// Stopped the way 'stop' stops: a human-mode account finishes up and signs off a short beat later, as a person
		// would, instead of vanishing mid-game. Robots (not human-owned) stop at once either way - Bot.StopAsync decides.
		// The stop has to be over before the start, and a human-mode account finishing up takes up to half a minute - so past
		// the reply's wait it says so, and the log has the rest.
		return await SlowAsync(new Slow("restart", "restarting", Key: "restart " + args[0].ToLowerInvariant()), async () => {
			await LifecycleAsync(mgr, args, "stop", graceful: true, untilStopped: true).ConfigureAwait(false);
			await Task.Delay(1500).ConfigureAwait(false);

			return await LifecycleAsync(mgr, args, "start").ConfigureAwait(false);
		}, ReplyWithin, mgr.Get(args[0]) is { } one ? [one] : [.. mgr.All]).ConfigureAwait(false);
	}

	private static async Task<string> AddAsync(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "add <name> <steamLogin|qr> [human|robot]\n  name       a nickname just for you - it names the config file\n  steamLogin what you type into Steam's sign-in box\n  qr         sign in by scanning a code with the Steam app instead - no password\n  human      your main: human mode, it keeps a believable day\n  robot      a spare or farm account: full speed, around the clock (the default)";
		}

		string name = args[0];

		// The rule the message states, as the dashboard's Add enforces it - and never "all": an account called that could
		// not be named on its own, and 'stop all' meant every account.
		if (ConfigStore.NameProblem(name) is { } problem) {
			return problem.ToString();
		}

		if (!ConfigStore.IsValidBotName(name) || !ConfigStore.IsPlainBotName(name)) {
			return "That name can't be used. Letters, numbers, dashes and underscores - and 'nocatFarm' and 'all' are taken.";
		}

		if (mgr.Get(name) != null) {
			return $"'{name}' already exists.";
		}

		bool qr = args[1].Equals("qr", StringComparison.OrdinalIgnoreCase);
		string? kind = args.Length > 2 ? args[2].ToLowerInvariant() : null;

		if (kind is not (null or "human" or "robot")) {
			return "The last word is 'human' (your main - human mode) or 'robot' (a farm account, full speed).";
		}

		BotConfig cfg = qr ? new BotConfig { SteamLogin = name, SignInWithQr = true } : new BotConfig { SteamLogin = args[1] };
		cfg.LegitMode = kind == "human";
		Bot? bot = await mgr.AddAsync(name, cfg).ConfigureAwait(false);

		if (bot == null) {
			return $"Couldn't add '{name}'.";
		}

		// A brand new account plays nothing until it is told what to. The app window has no form for that, so
		// leaving somebody staring at a command line having "added" an account that will sit there doing
		// nothing is the least helpful thing this could do. Only from the app's own command line - the
		// dashboard adds accounts through its own endpoint, and it is already open.
		string andThen = "";

		if (Live.Global.OpenDashboardAfterAdd && OpenDashboard()) {
			andThen = " Opening the dashboard so you can set it up.";
		}

		// Always say which it became: leaving the word off used to make a robot without a word about it.
		string kindNote = kind == "human" ? " It's in human mode."
			: $" It's a robot account (full speed). For your main, add 'human' at the end instead - or type: set {name} LegitMode true";

		return qr
			? $"Added '{name}'. Its QR code appears on the dashboard in a moment - scan it with the Steam app on your phone (Steam Guard tab) and approve, and that's it: no password, nothing else to type.{kindNote}{andThen}"
			: $"Added '{name}' ({args[1]}). It will ask for the password and a Steam Guard code once, then remember this account.{kindNote}{andThen}";
	}

	private static async Task<string> RemoveAsync(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return "remove <account>";
		}

		return await mgr.RemoveAsync(args[0]).ConfigureAwait(false) ? $"Removed '{args[0]}'." : NoSuchAccount(mgr, args[0]);
	}

	private static string Enable(BotManager mgr, string[] args, bool enabled) {
		if (args.Length == 0) {
			return enabled ? "enable <account>" : "disable <account>";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		// Changed and saved under the account's lock, like every change to its settings: a dashboard save of the same account
		// at the same moment put its older copy back, in memory and on disk.
		lock (bot.CfgGate) {
			bot.Cfg.Enabled = enabled;
			ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		ApplyBotSideEffects(bot, Settings.FindBot("Enabled")!);

		return $"{bot.Name}: {(enabled ? "enabled - logging in" : "disabled - logging out")}";
	}

	// ── playing ─────────────────────────────────────────────────────────────
	private static string Wake(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return "usage: wake <account>   - wake a sleeping human-mode account and start its day now";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (!bot.Cfg.LegitMode) {
			return $"{bot.Name} isn't in human mode, so it never sleeps - there's nothing to wake.";
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);

		if (human == null) {
			return $"{bot.Name} has no human-mode module running.";
		}

		if (!human.InBed) {
			return $"{bot.Name} is already awake.";
		}

		human.WakeNow();

		return $"waking {bot.Name} up - starting its day now (a short settle, then it plays or farms).";
	}

	/// <summary>'habits': what "Learn from how I play" has picked up from you, and 'habits account forget' to wipe it.</summary>
	private static string Habits(BotManager mgr, string[] args) {
		bool forget = args.Any(static a => a.Equals("forget", StringComparison.OrdinalIgnoreCase));
		string? name = args.FirstOrDefault(static a => !a.Equals("forget", StringComparison.OrdinalIgnoreCase));

		if (forget && (name == null)) {
			return "habits <account> forget - say which account's habits to wipe.";
		}

		List<Bot> bots = name == null
			? mgr.All.Where(static b => b.Cfg.LegitMode).ToList()
			: mgr.Get(name) is { } one ? [one] : [];

		if ((name != null) && (bots.Count == 0)) {
			return NoSuchAccount(mgr, name);
		}

		if (bots.Count == 0) {
			return "No account has human mode switched on. Turn it on under Human mode in the settings.";
		}

		StringBuilder sb = new();

		foreach (Bot bot in bots) {
			if (BotManager.ModuleOf<HumanMode>(bot) is not { } human) {
				continue;
			}

			if (forget) {
				human.ForgetHabits();
				sb.AppendLine($"{bot.Name}: forgot everything it learned from you - it starts learning again from now");

				continue;
			}

			sb.AppendLine($"{bot.Name}  ({bot.Cfg.SteamLogin})");

			foreach (string line in human.HabitsReport()) {
				sb.AppendLine(line);
			}

			sb.AppendLine();
		}

		return sb.ToString().TrimEnd();
	}

	/// <summary>
	/// What human mode is up to.
	///
	/// This exists because "online" told you nothing. You could not see which weighted game it had picked, how
	/// long it meant to stay there, or how much of the day was left - so there was no way to tell a working
	/// schedule from a broken one. Now you can read the whole day off one screen.
	/// </summary>
	private static string Human(BotManager mgr, string[] args) {
		bool week = args.Any(static a => a.Equals("week", StringComparison.OrdinalIgnoreCase));
		bool reroll = args.Any(static a => a.Equals("reroll", StringComparison.OrdinalIgnoreCase));
		string? name = args.FirstOrDefault(static a =>
			!a.Equals("week", StringComparison.OrdinalIgnoreCase) && !a.Equals("reroll", StringComparison.OrdinalIgnoreCase));

		List<Bot> bots = name == null
			? mgr.All.Where(static b => b.Cfg.LegitMode).ToList()
			: mgr.Get(name) is { } one ? [one] : [];

		if ((name != null) && (bots.Count == 0)) {
			return NoSuchAccount(mgr, name);
		}

		if (bots.Count == 0) {
			return "No account has human mode switched on. Turn it on under Human mode in the settings.";
		}

		StringBuilder sb = new();

		if (reroll) {
			foreach (Bot bot in bots) {
				HumanMode? mode = BotManager.ModuleOf<HumanMode>(bot);

				if (!bot.Cfg.LegitMode || (mode == null)) {
					sb.AppendLine($"{bot.Name}: human mode is off - nothing to reroll");

					continue;
				}

				mode.RerollToday();

				sb.AppendLine(mode.TargetMinutesToday == 0
					? $"{bot.Name}: rolled a day off - back tomorrow"
					: $"{bot.Name}: rolled {Fmt.Hm(mode.TargetMinutesToday)} of play for today");
			}

			return sb.ToString().TrimEnd();
		}

		foreach (Bot bot in bots) {
			HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);

			if (!bot.Cfg.LegitMode || (human == null)) {
				sb.AppendLine($"{bot.Name}: human mode is off");

				continue;
			}

			sb.AppendLine($"{bot.Name}  ({bot.Cfg.SteamLogin})");
			sb.AppendLine($"  right now   {human.Status}");

			if (human.TargetMinutesToday > 0) {
				int pct = human.PlayedMinutesToday * 100 / human.TargetMinutesToday;
				sb.AppendLine($"  today       {Fmt.Hm(human.PlayedMinutesToday)} of about {Fmt.Hm(human.TargetMinutesToday)}  ({pct}%)");
			}

			// Only the extras that are switched on - with none on, this prints exactly what it always did.
			foreach (string line in human.ExtrasReport()) {
				sb.AppendLine("  " + line);
			}

			List<(uint Game, int Minutes)> byGame = human.TodayByGame().ToList();

			if (byGame.Count > 0) {
				int total = Math.Max(1, byGame.Sum(static g => g.Minutes));

				foreach ((uint game, int minutes) in byGame) {
					sb.AppendLine($"                {Columns.PadRight(GameNames.Of(game), 28)} {Fmt.Hm(minutes),8}   {minutes * 100 / total,3}%");
				}
			}

			// What the day really picks from - the hunt's game included - split the way the picker splits it.
			List<(uint Game, int Weight)> weights = human.Rotation;
			List<double> shares = HumanMode.Shares(weights);

			if (weights.Count > 0) {
				int[] mixed = Fmt.RoundToTotal([.. shares], 100);
				sb.AppendLine("  set to play " + string.Join(", ", weights.Select((w, i) => $"{GameNames.Of(w.Game)} {mixed[i]}%")));

				// What those percentages actually come to over a week.
				//
				// They describe a MIXED day, and main-game-only days don't have any side games in them at all, so
				// every side number is worth less across a week than it reads on its own - by a lot, at a high
				// pure-main chance. Printing the configured figures alone made the box look like a promise it was
				// never making; showing both leaves the tuning alone and stops the number lying.
				int pure = Math.Clamp(bot.Cfg.PureMainDayChancePct, 0, 100);

				if ((weights.Count > 1) && (pure > 0)) {
					// Exact first, rounded once at the end. Rounding each share on its own printed a row that
					// added up to 101, because a 77.5 and a 10.5 both went up.
					double[] exact = shares
						.Select((share, i) => i == 0 ? pure + ((100 - pure) * share / 100) : (100 - pure) * share / 100)
						.ToArray();

					int[] shown = Fmt.RoundToTotal(exact, 100);

					IEnumerable<string> real = weights.Select((w, i) => $"{GameNames.Of(w.Game)} {shown[i]}%");

					sb.AppendLine($"  over a week   {string.Join(", ", real)}   ({pure}% of days are {GameNames.Of(weights[0].Game)} only)");
				}
			}

			if (week) {
				sb.AppendLine("  the week ahead (rolled the same way the real one is, so it's a sample - not a promise):");

				foreach (string line in HumanMode.PreviewWeek(bot.Cfg, human.Rotation, bot.Name, bot.Cfg.LearnFromOwner > 0 ? human.LearnedHabits : null)) {
					sb.AppendLine("                " + line);
				}
			}

			sb.AppendLine();
		}

		return sb.ToString().TrimEnd();
	}

	/// <summary>
	/// Activate one or more product keys.
	///
	/// Naming an account sends the keys only there. Without one, each key walks the accounts until somebody can
	/// use it - which is the case that actually comes up, because a key you got from a bundle only fits whichever
	/// of your accounts doesn't already own the game.
	/// </summary>
	private static async Task<string> RedeemAsync(BotManager mgr, string[] args, string line) {
		if (args.Length == 0) {
			return "redeem <key>, or redeem <account> <key> to send it to one account only.";
		}

		// A first argument that names an account is the account; otherwise every argument is a key.
		Bot? only = mgr.Get(args[0]);
		string[] keys = only == null ? args : args[1..];

		// A first word that is neither an account nor a key nor a file is a mistyped account - "redeem kylr0 KEY".
		// Carrying on treated it as a bad key and then walked the REAL key across every account, and an activation
		// on the wrong account can't be undone. So it stops here and says so.
		if ((only == null) && !Redeeming.LooksLikeKey(args[0]) && !LooksLikePath(args[0])) {
			return $"There's no account called '{args[0]}', and it isn't a key either - nothing was activated. 'status' lists the accounts.";
		}

		// Point it at a text file and it reads the keys out of it.
		//
		// A batch of keys arrives as a file far more often than as something anybody would type, and pasting two
		// hundred of them into a command line is not a thing people do. Any line shape works - one per line, with
		// or without a game name beside it - because the key is found by its shape rather than by position.
		// The path as typed, from the line itself: the command line is split into words, so "C:\Users\John Smith\keys.txt"
		// arrived as two words and each was turned away as a bad key - and put back together, a no-break space was a space.
		if ((keys.Length > 0) && LooksLikePath(keys[0]) && !keys.Any(Redeeming.LooksLikeKey)) {
			string path = Tail(line, only == null ? 1 : 2).Trim().Trim('"');

			if (!File.Exists(path)) {
				return $"There's no file at '{path}'.";
			}

			try {
				keys = [.. KeysIn(File.ReadAllText(path))];
			} catch (Exception e) {
				Log.Failed($"redeem: reading keys from {path}", e);

				return $"Couldn't read '{path}': {Log.Scrub(e.Message)}";
			}

			if (keys.Length == 0) {
				return $"No Steam keys found in '{Path.GetFileName(path)}'. They look like AAAAA-BBBBB-CCCCC.";
			}
		}

		if (keys.Length == 0) {
			return "Give me at least one key.";
		}

		if ((only != null) && !only.IsOnline) {
			return $"{only.Name} isn't logged in.";
		}

		List<Bot> targets = only != null ? [only] : mgr.All.Where(static b => b.IsOnline).ToList();

		if (targets.Count == 0) {
			return "No account is logged in.";
		}

		// A handful goes straight in; a batch queues.
		//
		// Steam counts activations per account and stops answering after a few, so working through fifty keys in
		// one go means the first few land and the rest come back "rate limited" - which, done then and there,
		// simply wastes them. Past a handful they go on the queue instead, which retries slowly and survives a
		// restart. 'keys' shows what is left.
		const int StraightAway = 5;

		if (keys.Length > StraightAway) {
			// The named account goes on every key, so the queue tries them there and nowhere else.
			int queued = KeyQueue.Add(keys, only?.Name);

			return $"{queued} key(s) queued{(only != null ? $" for {only.Name}" : "")} - they'll be activated a few at a time, because Steam limits how many "
				+ $"an account may try per hour. 'keys' shows what's left.{(queued < keys.Length ? $" ({keys.Length - queued} were already in the queue.)" : "")}";
		}

		async Task<string> Activate() {
			StringBuilder sb = new();

			foreach (string key in keys) {
				sb.AppendLine(await Redeeming.RedeemAcrossAsync(targets, key).ConfigureAwait(false));
			}

			return sb.ToString().TrimEnd();
		}

		// Each key tries the accounts in turn, a few seconds apart: five keys over a few accounts outlast the reply, and the
		// rest goes to the log. The same keys typed twice aren't activated twice at once; other keys go ahead.
		Slow job = new("redeem", "activating the keys", Key: "redeem " + string.Join(',', keys.Select(static k => k.ToUpperInvariant()).Order(StringComparer.Ordinal)));

		return await SlowAsync(job, Activate, ReplyWithin, targets).ConfigureAwait(false);
	}

	/// <summary>
	/// Card swaps between your own accounts.
	///
	/// Prints the plan by default and only sends offers when told to - trades are irreversible once accepted, and
	/// a matcher that fires the moment you type its name is not one anybody should have to trust.
	/// </summary>
	private static async Task<string> MatchAsync(BotManager mgr, string[] args) {
		bool send = args.Any(static a => a.Equals("do", StringComparison.OrdinalIgnoreCase));
		List<Bot> bots = [.. mgr.All.Where(static b => b.IsOnline && b.Web.Ready)];

		if (bots.Count < 2) {
			return "Card matching needs at least two accounts logged in - it swaps between your own.";
		}

		// Every account's offers and cards read, then each swap sent a few seconds apart: with a few accounts it outlasts the
		// reply, and the rest goes to the log.
		// Working the plan out only reads, so 'match do' isn't turned away while it does - only a second 'match do' is.
		Slow job = new("match", send ? "sending the card swaps" : "working out card swaps", Key: send ? "match do" : "match");

		return await SlowAsync(job, () => MatchNowAsync(mgr, bots, send), ReplyWithin, bots).ConfigureAwait(false);
	}

	private static async Task<string> MatchNowAsync(BotManager mgr, List<Bot> bots, bool send) {
		Dictionary<Bot, List<Looting.Item>> inventories = [];
		HashSet<ulong> busy = [];
		HashSet<(Bot, Bot)> busyPairs = [];
		List<string> lines = [];

		foreach (Bot bot in bots) {
			// What's already on a live offer has to be known first: planning around an offer we can't see could send
			// the same card twice. An account whose offers can't be read sits this run out.
			if (await TradeOffers.ActiveAsync(bot, received: true, sent: true).ConfigureAwait(false) is not { } live) {
				lines.Add($"{bot.Name}: couldn't check its trade offers - left out this time");

				continue;
			}

			// Just the cards - reading every game's inventory to throw all but these away was the heaviest thing this did.
			if (await Matching.CardsAsync(bot).ConfigureAwait(false) is not { } cards) {
				lines.Add($"{bot.Name}: couldn't read its cards in full - left out this time");

				continue;
			}

			inventories[bot] = cards;
			busy.UnionWith(TradeOffers.Committed(live));

			// A pair that already has an offer waiting between them waits for it to be settled.
			foreach (TradeOffers.Offer o in live) {
				if (mgr.All.FirstOrDefault(b => b.SteamId == o.Partner) is { } partner) {
					busyPairs.Add((bot, partner));
					busyPairs.Add((partner, bot));
				}
			}
		}

		Matching.MatchPlan plan = inventories.Count < 2 ? new Matching.MatchPlan([], 0) : Matching.PlanAll(inventories, busy, busyPairs);
		List<Matching.Swap> swaps = plan.Swaps;

		if (swaps.Count == 0) {
			lines.Add(busyPairs.Count > 0
				? "Nothing new to swap right now - wait for the offers already out between your accounts to go through."
				: "No swaps to make - no two accounts have cards that would help each other.");

			return string.Join(Environment.NewLine, lines);
		}

		foreach (Matching.Swap swap in swaps) {
			int pairs = swap.Cards;
			lines.Add($"{swap.From.Name} <-> {swap.To.Name}: {pairs} card(s) each way");

			foreach (Matching.Move move in swap.Give.Take(pairs).Take(4)) {
				lines.Add($"      {swap.From.Name} gives  {move.Card}  ({move.Game})");
			}

			foreach (Matching.Move move in swap.Take.Take(pairs).Take(4)) {
				lines.Add($"      {swap.To.Name} gives  {move.Card}  ({move.Game})");
			}

			if (pairs > 4) {
				lines.Add($"      ...and {pairs - 4} more each way");
			}

			if (!send) {
				continue;
			}

			(bool ok, string message) = await Looting.SwapAsync(
				swap.From, swap.To,
				[.. swap.Give.Take(pairs).Select(static m => m.Item)],
				[.. swap.Take.Take(pairs).Select(static m => m.Item)]).ConfigureAwait(false);

			lines.Add(ok ? $"      {message} - {swap.To.Name} checks it's fair and accepts it by itself" : $"      couldn't send - {message}");
		}

		if (plan.Deferred > 0) {
			lines.Add($"(Each account swaps with one other at a time - {plan.Deferred} more pair(s) can swap once these are done. Run 'match' again then.)");
		}

		if (!send) {
			lines.Add("");
			lines.Add("Nothing has been sent. 'match do' sends these offers.");
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>A path rather than a key - keys have no dots, slashes or backslashes in them.</summary>
	private static bool LooksLikePath(string text) =>
		text.Contains('/', StringComparison.Ordinal)
		|| text.Contains('\\', StringComparison.Ordinal)
		|| text.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

	/// <summary>Every Steam key in a blob of text, however the file is laid out around them.</summary>
	private static IEnumerable<string> KeysIn(string text) =>
		SteamKeyPattern().Matches(text).Select(static m => m.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal);

	[System.Text.RegularExpressions.GeneratedRegex(@"\b[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){0,2}\b")]
	private static partial System.Text.RegularExpressions.Regex SteamKeyPattern();

	/// <summary>
	/// Text with every product key in it that is surely one masked: Steam's dashed groups of five (5-5-5 up to 5x5), or a run of
	/// 15 to 25 letters and digits - each with a digit in it. A run with small letters in it needs digits in two places at
	/// least: 'Ab3dE6gH9jKlMn0p' is a key, 'NightRaid2024Edition' a game. Never a word with no digit ('steam-cards-trade',
	/// 'PLAYERUNKNOWNS-BATTLEGROUNDS', 'CONGRATULATIONS'), a run of digits alone (a SteamID, an offer), a setting's name, or an
	/// account's name or login. On a 'redeem' line every key in any shape it takes goes anyway (<see cref="ArgumentsMasked"/>).
	/// </summary>
	/// <param name="notKeys">Words that are never a key wherever they are: a Steam login typed into the line (<see cref="NotKeys"/>).</param>
	private static string KeysMasked(string text, IReadOnlySet<string>? notKeys = null) =>
		DashedKey().Replace(KeyRun().Replace(text, m => RunIsKey(m.Value) && (notKeys?.Contains(m.Value) != true) ? "[key]" : m.Value),
			m => m.Value.Any(char.IsAsciiDigit) && (notKeys?.Contains(m.Value) != true) ? "[key]" : m.Value);

	private static bool RunIsKey(string run) =>
		run.Any(char.IsAsciiLetter) && run.Any(char.IsAsciiDigit)
		&& (!run.Any(char.IsLower) || (run.Where((c, i) => char.IsAsciiDigit(c) && ((i == 0) || !char.IsAsciiDigit(run[i - 1]))).Count() >= 2))
		&& (Settings.Find(run) == null) && !AccountOrLogin(run);

	/// <summary>An account's name, or the Steam login of any account.</summary>
	private static bool AccountOrLogin(string word) =>
		(Host is { } mgr) && ((mgr.Get(word) != null) || mgr.All.Any(b => b.Cfg.SteamLogin.Equals(word, StringComparison.OrdinalIgnoreCase)));

	/// <summary>Steam's key shape: 3 to 5 groups of five letters and digits, a dash between.</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"(?<![\p{L}\p{Nd}-])[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){2,4}(?![\p{L}\p{Nd}-])", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex DashedKey();

	/// <summary>A run of 15 to 25 letters and digits, a word of its own.</summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"(?<![\p{L}\p{Nd}-])[A-Za-z0-9]{15,25}(?![\p{L}\p{Nd}-])", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex KeyRun();

	/// <summary>What is still waiting to be activated.</summary>
	private static string KeysText(string[] args) {
		if (args.FirstOrDefault()?.Equals("clear", StringComparison.OrdinalIgnoreCase) == true) {
			int had = KeyQueue.Clear();

			return had == 0 ? "The queue was already empty." : $"Dropped {had} queued key(s).";
		}

		List<(string Key, int Tries, DateTime NotBefore, string? Account)> pending = KeyQueue.Snapshot();

		if (pending.Count == 0) {
			return "No keys are waiting. Paste more than five at once and they'll queue automatically.";
		}

		List<string> lines = [$"{pending.Count} key(s) waiting:"];

		foreach ((string key, int tries, DateTime notBefore, string? account) in pending.Take(15)) {
			string when = notBefore > DateTime.UtcNow ? $"not before {notBefore.ToLocalTime():HH:mm}" : "ready";

			lines.Add($"   {Mask(key),-24} {(account != null ? $"for {account,-12} " : "")}{when}{(tries > 0 ? $"   {tries} try/tries so far" : "")}");
		}

		if (pending.Count > 15) {
			lines.Add($"   ...and {pending.Count - 15} more");
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>A key is worth money - show enough to recognise it, not enough to use it over somebody's shoulder.</summary>
	private static string Mask(string key) => key.Length <= 5 ? key : key[..5] + new string('-', Math.Min(12, key.Length - 5));

	/// <summary>
	/// Send an account's items to its trade master, or 'to' another of your own accounts. Never anywhere else -
	/// see Looting for why. This used to be two commands, 'send' and 'transfer', that did the same thing with a
	/// different recipient.
	/// </summary>
	private static async Task<string> SendAsync(BotManager mgr, string[] args) {
		string target = args.FirstOrDefault() ?? "";

		if (target.Length == 0) {
			return "send <account|all> [to <account>] [types]";
		}

		bool all = target.Equals("all", StringComparison.OrdinalIgnoreCase);
		string[] more = args[1..];
		Bot? to = null;

		if ((more.Length > 0) && more[0].Equals("to", StringComparison.OrdinalIgnoreCase)) {
			if (more.Length < 2) {
				return "send <account|all> to <account> [types]";
			}

			if (mgr.Get(more[1]) is not { } recipient) {
				return NoSuchAccount(mgr, more[1]);
			}

			if (recipient.SteamId == 0) {
				return $"{recipient.Name} hasn't signed in yet this run, so its Steam ID isn't known - start it first.";
			}

			to = recipient;
			more = more[2..];
		} else if ((more.Length > 0) && (mgr.Get(more[0]) is { } named)) {
			// The old 'transfer <from> <to>' shape. Taking the account name as an item type would send the wrong
			// things to the wrong place, so it asks instead.
			return $"To send to {named.Name}, put 'to' in front of it:  send {target} to {named.Name}";
		}

		// Leave the types off and the account's own send setting decides - or trading cards, going 'to' an account.
		string? types = more.Length > 0 ? string.Join(',', more) : null;

		List<Bot> bots = all
			? mgr.All.Where(b => b.IsOnline && (b != to)).ToList()
			: mgr.Get(target) is { } one ? [one] : [];

		if (bots.Count == 0) {
			return all ? "No account is logged in." : NoSuchAccount(mgr, target);
		}

		if (!all && (bots[0] == to)) {
			return "That's the same account both ways.";
		}

		// An inventory read, then the offers a few seconds apart - several accounts in a row outlast the reply, and the rest
		// goes to the log.
		// One send at a time an account, wherever it goes: two at once could put the same items in two offers.
		Slow job = new("send", to != null ? $"sending its items to {to.Name}" : "sending its items");

		return await SlowAsync(job, bots, b => to != null ? Looting.SendItemsAsync(b, to.SteamId, null, types ?? "") : Looting.SendToMasterAsync(b, types),
			ReplyWithin, oneAtATime: true).ConfigureAwait(false);
	}

	/// <summary>
	/// The current Steam Guard code for an account whose authenticator lives here.
	///
	/// Useful on its own - it means you can sign in to the website as one of these accounts without digging your
	/// phone out - and it is also the quickest way to prove the secret was imported correctly.
	/// </summary>
	private static string TwoFactor(BotManager mgr, string[] args) {
		string name = args.FirstOrDefault() ?? "";

		if (name.Length == 0) {
			List<string> lines = [];

			foreach (Bot b in mgr.All.Where(static b => b.HasAuthenticator)) {
				(string? c, int left) = Confirmations.Code(b);
				lines.Add($"  {b.Name,-12} {c}   ({left}s left)");
			}

			return lines.Count == 0
				? "No account has its authenticator set up here. Drop a maFile into config/authenticators/, or paste the secret into the account's settings."
				: "Steam Guard codes (they change every 30 seconds):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
		}

		Bot? bot = mgr.Get(name);

		if (bot == null) {
			return NoSuchAccount(mgr, name);
		}

		(string? code, int secondsLeft) = Confirmations.Code(bot);

		return code == null
			? $"{bot.Name} has no authenticator set up here. Drop {bot.Name}.maFile into config/authenticators/, or paste its shared secret into the account's settings."
			: $"{bot.Name}: {code}   ({secondsLeft}s left)";
	}

	/// <summary>The numbers 'confirm' and 'deny' go by: the order of the last list shown for each account.</summary>
	private static readonly Dictionary<string, List<ulong>> ListedConfirmations = new(StringComparer.OrdinalIgnoreCase);

	private static async Task<string> ConfirmationsAsync(BotManager mgr, string[] args) {
		// No account: every account whose authenticator is here - the one-tap version for Telegram's menu.
		if (args.Length < 1) {
			List<Bot> able = [.. mgr.All.Where(static b => b.CanConfirmTrades)];

			if (able.Count == 0) {
				return new Said("No account has its authenticator in nocat.farm - add its maFile under config/authenticators to confirm from here.").ToString();
			}

			List<string> all = [];

			foreach (Bot each in able) {
				all.Add(await ConfirmationsAsync(mgr, [each.Name]).ConfigureAwait(false));
			}

			return string.Join(Environment.NewLine, all);
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		(bool ok, string error, List<Confirmations.Item> items) = await Confirmations.ListAsync(bot, fresh: true).ConfigureAwait(false);

		if (!ok) {
			return new Said("{0}: couldn't read the confirmations - {1}", bot.Name, error).ToString();
		}

		lock (ListedConfirmations) {
			ListedConfirmations[bot.Name] = [.. items.Select(static i => i.Id)];
		}

		if (items.Count == 0) {
			return new Said("{0}: nothing waiting to be confirmed", bot.Name).ToString();
		}

		List<string> lines = [new Said("{0} - waiting to be confirmed:", bot.Name).ToString()];

		for (int n = 0; n < items.Count; n++) {
			Confirmations.Item c = items[n];
			string ago = c.Created > 0 ? " · " + Fmt.Hm((int) Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - c.Created) / 60)) : "";
			lines.Add($"  {n + 1}  {c.TypeName}  {c.Headline}{ago}");

			foreach (string line in c.Summary.Where(static l => l.Length > 0)) {
				lines.Add($"       {line}");
			}
		}

		lines.Add($"  confirm {bot.Name} <number|all>  /  deny {bot.Name} <number|all>");

		return string.Join(Environment.NewLine, lines);
	}

	private static async Task<string> AnswerConfirmationsAsync(BotManager mgr, string[] args, bool accept) {
		string verb = accept ? "confirm" : "deny";

		if ((args.Length < 2) || (mgr.Get(args[0]) is not { } bot)) {
			return args.Length < 2 ? $"{verb} <account> <number|all>    after 'confirmations <account>' shows the numbers" : NoSuchAccount(mgr, args[0]);
		}

		(bool ok, string error, List<Confirmations.Item> items) = await Confirmations.ListAsync(bot, fresh: true).ConfigureAwait(false);

		if (!ok) {
			return new Said("{0}: couldn't read the confirmations - {1}", bot.Name, error).ToString();
		}

		List<ulong>? listed;

		lock (ListedConfirmations) {
			ListedConfirmations.TryGetValue(bot.Name, out listed);
		}

		// Nothing shown yet - since the start, or ever: "all" confirmed everything waiting, a trade nobody had looked at
		// included, and a number picked from a list nobody had seen. Both are only ever answers to a list.
		if (listed == null) {
			return new Said("{0}: see what's waiting first - 'confirmations {0}' - then confirm or deny by its number", bot.Name).ToString();
		}

		List<Confirmations.Item> picked;

		if (args[1].Equals("all", StringComparison.OrdinalIgnoreCase)) {
			// All of what was shown - a trade that turned up after the list, and was never seen, isn't confirmed with it.
			picked = [.. items.Where(c => listed.Contains(c.Id))];
		} else if (int.TryParse(args[1].TrimStart('#'), out int n) && (n >= 1)) {
			if (n > listed.Count) {
				return new Said("{0}: there's no number {1} - 'confirmations {0}' showed {2}", bot.Name, n, listed.Count).ToString();
			}

			// By the list that was shown - so a new confirmation arriving in between can't shift what "2" means.
			ulong id = listed[n - 1];
			picked = [.. items.Where(c => c.Id == id)];
		} else {
			return $"'{args[1]}' isn't a number from 'confirmations {bot.Name}' - or say all.";
		}

		if (picked.Count == 0) {
			return items.Count == 0
				? new Said("{0}: nothing waiting to be confirmed", bot.Name).ToString()
				: new Said("{0}: that one is gone - 'confirmations {0}' shows what's waiting now", bot.Name).ToString();
		}

		if (!await Confirmations.ActAsync(bot, picked, accept).ConfigureAwait(false)) {
			return new Said("{0}: Steam didn't take it - try again", bot.Name).ToString();
		}

		string what = string.Join(", ", picked.Select(static c => $"{c.TypeName} {c.Headline}".Trim()));
		Log.Trade(accept ? new Said("confirmed: {0}", what) : new Said("denied: {0}", what), bot.Name, good: accept);

		return accept ? new Said("{0}: confirmed {1}", bot.Name, what).ToString() : new Said("{0}: denied {1}", bot.Name, what).ToString();
	}

	/// <summary>
	/// Achievements, by hand.
	///
	/// Mass unlocking lives here rather than in a setting on purpose: it is instant, permanent, stamped on the
	/// profile with one shared timestamp, and there is no undo on Steam's side beyond re-locking. Something that
	/// consequential should be a thing you typed, not a checkbox you left on.
	/// </summary>
	/// <summary>
	/// What the inventories are worth.
	///
	/// The per-game breakdown matters as much as the total: "$1,900" tells you nothing about whether that is one
	/// knife or four hundred trading cards, and the answer changes what you would do about it.
	/// </summary>
	private static string InventoryText(BotManager mgr, string[] args) {
		// ", about 14 request(s), ~2m to go" - requests, not items: one market search can price a whole game's cards.
		static string Left(int requests) => requests <= 0 ? ""
			: $", about {requests} request(s), {InventoryValue.Eta(requests, Live.Global.MarketGapSeconds)} to go";

		bool refresh = args.Any(static a => a.Equals("refresh", StringComparison.OrdinalIgnoreCase));
		string[] names = [.. args.Where(static a => !a.Equals("refresh", StringComparison.OrdinalIgnoreCase))];

		List<Bot> targets = (names.Length == 0) || names[0].Equals("all", StringComparison.OrdinalIgnoreCase)
			? [.. mgr.All]
			: mgr.Get(names[0]) is { } one ? [one] : [];

		if (targets.Count == 0) {
			return names.Length == 0 ? "No accounts are set up yet." : NoSuchAccount(mgr, names[0]);
		}

		List<string> lines = [];
		decimal total = 0;

		foreach (Bot bot in targets) {
			if (refresh) {
				bot.Inventory.ForceRefresh();
			}

			if (!bot.Cfg.ShowInventoryValue) {
				lines.Add($"{bot.Name}: not being valued (its \"Work out what its inventory is worth\" setting is off)");

				continue;
			}

			total += bot.Inventory.Total;

			string moved = InventoryHistory.Since(bot.Name, TimeSpan.FromHours(24)) is { } d
				? $"   {PriceBook.Signed(d.Change)} ({PriceBook.SignedPercent(d.Percent)}) in 24h"
				: "";

			lines.Add($"{bot.Name}: {PriceBook.Symbol}{bot.Inventory.Total:N2}{moved}"
				+ (bot.Inventory.Pricing > 0 ? $"   ({bot.Inventory.Pricing} item(s) still being priced{Left(bot.Inventory.RequestsLeft)})" : "")
				+ (bot.Inventory.Ready ? "" : "   (reading it now)"));

			foreach (InventoryValue.GameValue game in bot.Inventory.ByGame.Take(6)) {
				lines.Add(game.Blocked
					? $"      {Columns.PadRight(game.Game, 30)} skipped - on this account's ignore list ({game.Items} item(s))"
					: $"      {Columns.PadRight(game.Game, 30)} {PriceBook.Symbol}{game.Value,10:N2}   {game.Items} item(s)");
			}
		}

		if (targets.Count > 1) {
			// Each item once, from the accounts being valued - two holding the same card wait on one price, as the dashboard says;
			// none from an account that isn't online, which isn't pricing anything (InventoryValue.Pricing).
			List<(uint App, string Hash)> waiting = [.. targets.Where(static b => b.Cfg.ShowInventoryValue).SelectMany(static b => b.Inventory.Waiting()).Distinct()];
			lines.Add($"all: {PriceBook.Symbol}{total:N2}{(waiting.Count > 0 ? $"   ({waiting.Count} still being priced{Left(PriceBook.RequestsFor(waiting))})" : "")}");
		}

		if (refresh) {
			lines.Add("Reading the inventories again - prices are kept for a day, so only what CHANGED gets looked up.");
		}

		// The market refusing, with prices still to refresh: the value stands on the last prices it gave - say from when,
		// and when it is asked again, as the dashboard's tile does.
		if ((PriceBook.Stale is { } stale) && targets.Any(static b => b.Cfg.ShowInventoryValue && (b.Inventory.Pricing > 0))) {
			// "14:20" today, "2026-10-04 14:20" on an earlier day.
			static string Stamp(DateTime utc) {
				DateTime local = utc.ToLocalTime();

				return local.ToString(local.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
			}

			lines.Add(new Said("prices from {0} · Steam's market isn't answering, trying again at {1}", Stamp(stale.From), Stamp(stale.RetryAt)).ToString());
		} else if (PriceBook.PausedUntil is { } paused) {
			lines.Add($"Steam asked the price lookups to wait - they start again at {paused.ToLocalTime():HH:mm}.");
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>
	/// What the achievement hunter would play next, and what it has ruled out.
	///
	/// Worth a command of its own: "all single-player" decides its own targets, and a list an account chose for
	/// itself is exactly the kind of thing that should be inspectable before it runs for a fortnight. It also
	/// answers the only question anybody actually asks of it - why isn't it playing X.
	/// </summary>
	private static async Task<string> HuntAsync(BotManager mgr, string[] args) {
		List<Bot> targets = (args.Length == 0) || args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
			? [.. mgr.All]
			: mgr.Get(args[0]) is { } one ? [one] : [];

		if (targets.Count == 0) {
			return args.Length == 0 ? "No accounts are set up yet." : NoSuchAccount(mgr, args[0]);
		}

		List<string> blocks = [];

		foreach (Bot bot in targets) {
			if (BotManager.ModuleOf<AchievementBoost>(bot) is { } boost) {
				blocks.Add(boost.Explain());
			}
		}

		return blocks.Count == 0 ? "nothing to show" : string.Join(Environment.NewLine + Environment.NewLine, blocks);
	}

	/// <summary>
	/// Hold an account on one game for a while.
	///
	/// The hours are capped at a week: a grind is a deliberate short-term thing, and a typo of 1000 should not
	/// silently take an account off its schedule until next month.
	/// </summary>
	private static string Grind(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return string.Join(Environment.NewLine, [
				"grind <account|all> <appID> <hours>   put it on one game for a while",
				"grind <account|all> off               stop early and go back to normal",
				"  grind new 730 6      six hours of Counter-Strike 2, then back to its usual day",
				"  grind all 440 2      two hours of Team Fortress 2 on every account"
			]);
		}

		List<Bot> targets = args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
			? [.. mgr.All]
			: mgr.Get(args[0]) is { } one ? [one] : [];

		if (targets.Count == 0) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
			foreach (Bot bot in targets) {
				bot.StopGrind();
			}

			return $"{string.Join(", ", targets.Select(static b => b.Name))}: back to normal.";
		}

		if (!uint.TryParse(args[1], out uint appId) || (appId == 0)) {
			return $"'{args[1]}' is not an appID - it's the number in a game's store URL.";
		}

		if ((args.Length < 3) || !TryHours(string.Join("", args[2..]), out double hours)) {
			return "How many hours? e.g.  grind " + args[0] + " " + appId + " 6   (or 1.5, 90m, 1h30m)";
		}

		hours = Math.Min(hours, 24 * 7);
		TimeSpan how = TimeSpan.FromHours(hours);

		List<Bot> started = [];
		List<Bot> refused = [];

		foreach (Bot bot in targets) {
			// Legit accounts finish up their current game first (a short, jittered beat) rather than snapping over;
			// non-human accounts start instantly.
			TimeSpan delay = bot.HumanOwned ? TimeSpan.FromSeconds(Rng.Next(45, 210)) : TimeSpan.Zero;

			if (!bot.StartGrind(appId, how, delay)) {
				refused.Add(bot);   // inside its refund window; StartGrind said so in the log

				continue;
			}

			started.Add(bot);
			// The start time it will really have: finishing up, and a warm-up still left from signing in.
			TimeSpan wait = bot.HumanOwned && (BotManager.ModuleOf<Modules.HumanMode>(bot)?.SettleLeft is { } settle) && (settle > delay) ? settle : delay;
			Said lead = wait > TimeSpan.Zero ? new Said(" (starts in ~{0})", Fmt.Hm((int) Math.Ceiling(wait.TotalMinutes))) : default;
			Log.Info(new Said("grinding {0} for {1}{2}, then back to normal", GameNames.Of(appId), Fmt.Hm((int) how.TotalMinutes), lead), bot.Name);
		}

		string no = refused.Count > 0
			? $"{(started.Count > 0 ? "  " : "")}{string.Join(", ", refused.Select(static b => b.Name))}: skipped - {GameNames.Of(appId)} is still refundable, and a grind would spend that."
			: "";

		return started.Count > 0
			? $"{string.Join(", ", started.Select(static b => b.Name))}: {GameNames.Of(appId)} for {Fmt.Hm((int) how.TotalMinutes)}.{no}"
			: no.TrimStart();
	}

	private static async Task<string> SelfCheckAsync(BotManager mgr, string[] args) {
		// Only the accounts that are trying to pass as a person. A boost account is a robot on purpose - telling it to
		// take breaks or drop its idle games is advice nobody wants - so it's only scored when asked for by name.
		List<Bot> online = [.. mgr.All.Where(static b => b.IsOnline)];
		List<Bot> bots = args.Length > 0 && mgr.Get(args[0]) is { } one ? [one] : args.Length > 0 ? [] : [.. online.Where(static b => b.Cfg.LegitMode)];
		List<Bot> skipped = args.Length > 0 ? [] : [.. online.Where(static b => !b.Cfg.LegitMode)];

		if (bots.Count == 0) {
			return args.Length > 0 ? NoSuchAccount(mgr, args[0])
				: skipped.Count > 0 ? $"No signed-in account runs human mode - {string.Join(", ", skipped.Select(static b => b.Name))} are boost accounts. 'selfcheck <name>' scores one anyway."
				: "No account is signed in to check.";
		}

		StringBuilder sb = new();

		foreach (Bot bot in bots) {
			if (!bot.Library.Ready) {
				sb.AppendLine($"{bot.Name}: hasn't read its library yet - give it a moment after signing in.");

				continue;
			}

			SelfCheck.Report report = await SelfCheck.RunAsync(bot).ConfigureAwait(false);

			bool boost = !bot.Cfg.LegitMode;

			sb.AppendLine($"{bot.Name}: {report.Score}/100 - {report.Verdict}   ({report.Visibility})"
				+ (boost ? Environment.NewLine + "  a boost account - these are what it's for, nothing here needs changing" : ""));

			foreach (SelfCheck.Tell tell in report.Tells) {
				sb.AppendLine($"  -{tell.Points,-3} {tell.What}");

				if (!boost) {
					sb.AppendLine($"        fix: {tell.Fix}");
				}
			}

			if (report.Tells.Count == 0) {
				sb.AppendLine("  nothing an outsider could pick out");
			}
		}

		if (skipped.Count > 0) {
			sb.AppendLine($"({string.Join(", ", skipped.Select(static b => b.Name))}: boost account(s), not meant to look human - left out. 'selfcheck <name>' scores one anyway.)");
		}

		return sb.ToString().TrimEnd();
	}

	private static string LevelUp(BotManager mgr, string[] args) {
		if ((args.Length < 2) || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int target) || (target < 1)) {
			return "levelup <account> <level>      what reaching that Steam level would cost";
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return $"{bot.Name}: not logged in";
		}

		return LevelPlanner.Ask(bot, Math.Min(target, 5000));
	}

	private static async Task<string> BansAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		List<Bot> watched = [.. bots.Where(static b => BotManager.ModuleOf<BanWatch>(b) != null)];

		if (watched.Count == 0) {
			return new Said("nothing to look up").ToString();
		}

		static async Task<string> Look(Bot bot) {
			BanWatch watch = BotManager.ModuleOf<BanWatch>(bot)!;
			BanWatch.Bans? now = bot.IsOnline && bot.Web.Ready ? await watch.CheckAsync().ConfigureAwait(false) : null;
			BanWatch.Bans? shown = now ?? watch.Last;

			if (shown == null) {
				return new Said("{0}: couldn't look - it needs to be logged in", bot.Name).ToString();
			}

			string when = now != null ? "" : watch.CheckedAt is { } at ? " " + new Said("(as of {0})", Fmt.Clock(at)) : "";
			string games = watch.BannedGames.Count > 0 ? " - " + new Said("banned in {0}", string.Join(", ", watch.BannedGames.Select(GameNames.Of))) : "";

			return $"{bot.Name}: {BanWatch.Summary(shown)}{games}{when}";
		}

		// A second or so an account - but a fleet of them, or Steam slow to answer, and it outlasts the reply.
		return await SlowAsync(new Slow("bans", "looking up its bans"), watched, Look, ReplyWithin, oneAtATime: true).ConfigureAwait(false);
	}

	private static async Task<string> TradeAsync(BotManager mgr, string[] args) {
		if ((args.Length < 3) || !(args[0].ToLowerInvariant() is "accept" or "decline" or "cancel")) {
			return string.Join(Environment.NewLine, [
				"trade accept <account> <number|all>    accept a waiting offer",
				"trade decline <account> <number|all>   decline one",
				"trade cancel <account> <offer id|all>  take back an offer the account sent",
				"  trade accept myaccount 3  the offer numbered 3 in 'offers myaccount' and the announcement",
				"  trade decline farm1 all   every offer waiting on farm1",
				"  trade cancel farm2 all    every offer farm2 sent that hasn't gone through"
			]);
		}

		if (mgr.Get(args[1]) is not { } bot) {
			return NoSuchAccount(mgr, args[1]);
		}

		if (BotManager.ModuleOf<Trading>(bot) is not { } trading) {
			return $"{bot.Name} has no trade module running.";
		}

		// 'all' is each offer in turn, a second or three apart, and an accept that sends items out is confirmed too: a long
		// list outlasts the reply, and the rest goes to the log.
		string verb = args[0].ToLowerInvariant();
		// By offer, not by account: 'trade decline 5' while 'trade accept 3' goes is another offer.
		Slow job = new("trade", verb switch { "accept" => "accepting the offers", "decline" => "declining the offers", _ => "taking back the offers" },
			Key: $"trade {verb} {args[2].TrimStart('#').ToLowerInvariant()}");

		return await SlowAsync(job, [bot], _ => verb == "cancel" ? trading.CancelSentAsync(args[2]) : trading.AnswerAsync(verb == "accept", args[2]), ReplyWithin).ConfigureAwait(false);
	}

	private static async Task<string> OffersAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		List<string> lines = [];

		foreach (Bot bot in bots) {
			if (!bot.IsOnline || !bot.Web.Ready) {
				lines.Add($"{bot.Name}: not logged in");

				continue;
			}

			if (await TradeOffers.ActiveAsync(bot, received: true, sent: true).ConfigureAwait(false) is not { } live) {
				lines.Add($"{bot.Name}: Steam didn't answer");

				continue;
			}

			if (live.Count == 0) {
				lines.Add($"{bot.Name}: no live trade offers");

				continue;
			}

			lines.Add($"{bot.Name}:");

			foreach (TradeOffers.Offer o in live.OrderBy(static o => o.Ours)) {
				string who = mgr.All.FirstOrDefault(b => b.SteamId == o.Partner)?.Name ?? o.Partner.ToString(CultureInfo.InvariantCulture);
				string state = o.State switch {
					TradeOffers.Active => "waiting",
					TradeOffers.NeedsConfirmation => "needs confirming on your phone",
					TradeOffers.InEscrow => "in a trade hold",
					_ => $"state {o.State}"
				};
				string hold = o.HoldUntil is { } until ? $", hold until {until.ToLocalTime():d MMM HH:mm}" : "";

				// Offers it received get their short number - the one 'trade accept' and the announcements use.
				string num = !o.Ours && (o.State == TradeOffers.Active) && !o.AwaitingConfirmation && (BotManager.ModuleOf<Trading>(bot) is { } tr) ? $"[{tr.NumberOf(o.Id)}]" : "   ";
				lines.Add($"  {num} #{o.Id}  {(o.Ours ? "sent to" : "from")} {who}  {o.Describe}  {state}{hold}");
			}

			if (live.Any(static o => !o.Ours && (o.State == TradeOffers.Active))) {
				lines.Add($"  trade accept {bot.Name} <number>  /  trade decline {bot.Name} <number>");
			}

			if (live.Any(static o => o.Ours && (o.State is TradeOffers.Active or TradeOffers.NeedsConfirmation))) {
				lines.Add($"  trade cancel {bot.Name} <#id|all>  takes back an offer it sent");
			}
		}

		return string.Join(Environment.NewLine, lines);
	}

	private static string Hours(BotManager mgr, string[] args) {
		if ((args.Length < 1) || (mgr.Get(args[0]) is not { } bot)) {
			return args.Length < 1 ? "hours <account>        how its hour targets are going" : NoSuchAccount(mgr, args[0]);
		}

		if (string.IsNullOrWhiteSpace(bot.Cfg.HourTargets)) {
			return $"{bot.Name} has no hour targets. Set some, e.g.:  set {bot.Name} HourTargets \"730:100@2026-12-01, 440:50\"";
		}

		if (!bot.Library.Ready) {
			return $"{bot.Name} hasn't read its library yet - give it a moment after signing in.";
		}

		List<string> lines = BotManager.ModuleOf<HumanMode>(bot)?.TargetReport() ?? [];
		string note = bot.Cfg.LegitMode ? "" : Environment.NewLine + "  (human mode is off, so nothing is working towards these right now)";

		return $"{bot.Name}:" + Environment.NewLine + string.Join(Environment.NewLine, lines.Select(static l => "  " + l)) + note;
	}

	private static string Rotation(BotManager mgr, string[] args) {
		if ((args.Length < 1) || (mgr.Get(args[0]) is not { } bot)) {
			return args.Length < 1 ? "rotation <account> [next]   the idle rotation, or 'next' to move on to the next batch now" : NoSuchAccount(mgr, args[0]);
		}

		if (bot.Cfg.LegitMode) {
			return $"{bot.Name} is in human mode - it plays one game at a time, so there's no idle rotation.";
		}

		if (BotManager.ModuleOf<Idler>(bot) is not { } idler) {
			return $"{bot.Name} has no idler running.";
		}

		string Names(List<uint> apps) =>
			string.Join(", ", apps.Take(6).Select(static a => GameNames.Of(a))) + (apps.Count > 6 ? $" and {apps.Count - 6} more" : "");

		bool next = (args.Length > 1) && args[1].Equals("next", StringComparison.OrdinalIgnoreCase);

		if (!bot.Cfg.RotateIdleGames) {
			return $"{bot.Name}: rotation is off (\"Rotate the idle list\"). Turn it on with:  set {bot.Name} RotateIdleGames true";
		}

		if (next) {
			idler.MoveOn();

			// Farming, a grind, paused or you on it: the idler isn't the one playing, so the move waits for it.
			if (bot.IsFarming || bot.Grinding || !bot.CanPlay) {
				return $"{bot.Name}: it isn't idling right now - it moves on to the next batch as soon as it is.";
			}
		}

		// Farming or a grind: the idler isn't the one playing, and working a plan out here would put a rotation card up
		// that nothing is following.
		if ((idler.Rotating == null) && (bot.IsFarming || bot.Grinding)) {
			return $"{bot.Name}: rotation is on - it isn't idling right now ({(bot.IsFarming ? "farming cards" : "a grind")}), and picks the rotation up again as soon as it is.";
		}

		// Worked out fresh, not from the last re-assert - the list may have changed since.
		List<uint> now = idler.Rotating == null ? idler.Plan(DateTime.UtcNow) : idler.Rotating.Now;
		int slots = IdleRotation.Slots(!string.IsNullOrWhiteSpace(bot.CustomName));

		if (idler.Rotating is not { } r) {
			return $"{bot.Name}: rotation is on, but all {now.Count} game(s) fit at once ({slots} can play together) - nothing to rotate."
				+ (bot.Cfg.IdleWholeLibrary && !bot.Library.Ready ? " The library hasn't been read yet." : "");
		}

		string when = IdleRotation.When(r.MovesAt);
		string waiting = bot.IsFarming ? Environment.NewLine + "  (farming cards right now - the rotation carries on once the cards are done)"
			: !bot.CanPlay ? Environment.NewLine + "  (not playing right now - paused, offline or you're on it)" : "";

		return string.Join(Environment.NewLine, [
			(next ? $"{bot.Name}: moved on. " : $"{bot.Name}: ") + $"rotation on, every {Math.Max(1, bot.Cfg.RotateEveryHours)}h",
			$"  list:  {r.Total} games" + (bot.Cfg.IdleWholeLibrary ? " (whole library)" : ""),
			$"  batch: {r.Now.Count} at once, batch {r.Batch} of {r.Batches}, next batch at {when}",
			$"  now:   {Names(r.Now)}",
			$"  next:  {Names(r.Next)}"
		]) + waiting;
	}

	private static async Task<string> DropsAsync(BotManager mgr, string[] args) {
		if (args.Length < 1) {
			return string.Join(Environment.NewLine, [
				"drops <account> [appID|next] [count|all]   you pick the game and how many cards - it goes first",
				"drops <account> off                        stop early and go back to normal",
				"  drops new 460920 2       2 cards from Steep first, then back to the usual mix",
				"  drops new                every card left in the next game with cards",
				"  drops new next 1         1 card from whatever has cards next",
				"",
				"Human-mode account: the game plays in its normal sittings - the main game's share of them, with breaks,",
				"meals and bedtime - so it still looks like a person playing it a lot that day.",
				"Other accounts: it plays non-stop until the cards are in.",
				"Automatic farming (\"When to farm cards\", e.g. mixed) needs no command: it picks the games itself."
			]);
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		if ((args.Length > 1) && args[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
			if (bot.DropsFirstActive) {
				string was = GameNames.Of(bot.DropsFirstApp);
				bot.StopDropsFirst();
				Log.Info(new Said("stopped the {0} drop run - back to normal", was), bot.Name);

				return $"{bot.Name}: drop run stopped - back to normal.";
			}

			if (!bot.Grinding || (bot.GrindDropsLeft == 0)) {
				return $"{bot.Name} isn't on a drop run.";
			}

			bot.StopGrind();
			Log.Info("drop run stopped - back to the usual day", bot.Name);

			return $"{bot.Name}: drop run stopped - back to normal.";
		}

		if (BotManager.ModuleOf<CardFarmer>(bot) is not { } farmer) {
			return $"{bot.Name} has no card farmer running.";
		}

		// [appID|next] then [count|all], both optional. Two bare numbers read as appID then count.
		uint app = 0;
		int? count = null;
		string[] rest = args[1..];

		if ((rest.Length > 0) && !rest[0].Equals("next", StringComparison.OrdinalIgnoreCase) && !rest[0].Equals("all", StringComparison.OrdinalIgnoreCase)) {
			if (!uint.TryParse(rest[0], out app) || (app == 0)) {
				return $"'{rest[0]}' is not an appID - it's the number in a game's store URL. 'next' means the next game with cards.";
			}
		}

		string? countArg = rest.Length > 1 ? rest[1] : (rest.Length == 1) && rest[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? "all" : null;

		if ((countArg != null) && !countArg.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			if (!int.TryParse(countArg, out int n) || (n <= 0)) {
				return $"'{countArg}' isn't a number of drops - try 2, or 'all'.";
			}

			count = n;
		}

		if (app == 0) {
			app = farmer.NextGame != 0 ? farmer.NextGame : farmer.Queue.FirstOrDefault(static g => g.CardsRemaining > 0)?.AppId ?? 0;
		}

		if (app == 0) {
			return $"{bot.Name}: no game with card drops left, as of its last look at the badge pages.";
		}

		FarmTarget? target = await farmer.CardsForAsync(app).ConfigureAwait(false);

		if (target == null) {
			return $"Couldn't read the badge page for {GameNames.Of(app)} on {bot.Name} - try again in a minute.";
		}

		// The catalogue's name, not the page's: on some games' card pages that header reads "Badges".
		string game = GameNames.Of(app);

		if (target.CardsRemaining == 0) {
			return $"{game} has no card drops left on {bot.Name}.";
		}

		int want = Math.Min(count ?? target.CardsRemaining, target.CardsRemaining);
		int estimate = farmer.MinutesForDrops(app, want);

		// Human mode: no grind over the schedule - the game goes first in the day's normal sittings, with real weight.
		if (bot.Cfg.LegitMode) {
			if (!bot.StartDropsFirst(app, want)) {
				return $"{bot.Name}: {game} is still refundable, and a drop run would spend that.";
			}

			Log.Info(new Said("{1} first: {0} card drop(s), in normal sittings", want, game), bot.Name);

			return $"{bot.Name}: {game} goes first until {want} card(s) drop. It plays in the normal sittings - the main game's share of them, with its usual breaks and bedtime - so it looks like a person playing it a lot. About {Fmt.Rough(estimate)} of play, spread over the day. 'drops {bot.Name} off' stops it.";
		}

		// The grind's time is only a cap, for a game that stops dropping: twice the estimate and a half hour, two hours
		// at the least and a day at the most.
		TimeSpan cap = TimeSpan.FromMinutes(Math.Clamp((estimate * 2) + 30, 120, 24 * 60));
		TimeSpan delay = bot.HumanOwned ? TimeSpan.FromSeconds(Rng.Next(45, 210)) : TimeSpan.Zero;

		if (!bot.StartGrind(app, cap, delay, drops: want)) {
			return $"{bot.Name}: {game} is still refundable, and a drop run would spend that.";
		}

		// The start time it will really have: finishing up, and a warm-up still left from signing in.
		TimeSpan wait = bot.HumanOwned && (BotManager.ModuleOf<Modules.HumanMode>(bot)?.SettleLeft is { } settle) && (settle > delay) ? settle : delay;
		Said lead = wait > TimeSpan.Zero ? new Said(" (starts in ~{0})", Fmt.Hm((int) Math.Ceiling(wait.TotalMinutes))) : default;
		Log.Info(new Said("going for {0} card drop(s) in {1} - about {2}{3}", want, game, Fmt.Rough(estimate), lead), bot.Name);

		return $"{bot.Name}: {game} until {want} card(s) drop - about {Fmt.Rough(estimate)}"
			+ (delay > TimeSpan.Zero ? $", starting in ~{Fmt.Hm((int) Math.Ceiling(delay.TotalMinutes))}" : "")
			+ $". It stops by itself after {Fmt.Rough((int) cap.TotalMinutes)} if the drops don't come; 'drops {bot.Name} off' stops it now.";
	}

	private static async Task<string> CheevoAsync(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return string.Join(Environment.NewLine, [
				"achievements <account> <appID> [list|unlock|lock] [name|all]",
				"  achievements new 730              what it has and what's left",
				"  achievements new 730 unlock all   every one it's allowed to set",
				"  achievements new 730 unlock ACH_X just that one"
			]);
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (!bot.IsOnline) {
			return $"{bot.Name} isn't logged in.";
		}

		if (!uint.TryParse(args[1], out uint appId) || (appId == 0)) {
			return $"'{args[1]}' is not an appID - it's the number in a game's store URL.";
		}

		AchievementSet? set = await Achievements.GetAsync(bot, appId).ConfigureAwait(false);

		if (set == null) {
			return $"Couldn't read achievements for {GameNames.Of(appId)}. Does {bot.Name} own it, and does it have any?";
		}

		string verb = args.Length > 2 ? args[2].ToLowerInvariant() : "list";

		if (verb == "list") {
			return DescribeAchievements(bot, set, set.Total > 0 ? await DlcAchievements.ViewAsync(bot, appId, TimeSpan.Zero).ConfigureAwait(false) : null);
		}

		if (verb is not ("unlock" or "lock")) {
			return $"'{verb}' isn't one of list, unlock or lock.";
		}

		bool unlock = verb == "unlock";
		string target = args.Length > 3 ? args[3] : "";

		if (target.Length == 0) {
			return $"Which one? Name it, or say 'all'. 'achievements {bot.Name} {appId}' lists them.";
		}

		List<Achievement> chosen;

		// Never one from DLC this account doesn't own - asked for by name or not. It's on the profile for anybody to
		// see, and nobody could have earned it. There is no way round this here: buy the DLC, and it's allowed.
		// Putting one BACK is fine, so only an unlock asks. A game not worked out yet gets a short wait - enough for
		// one with no DLC - and is otherwise refused until it has been.
		DlcAchievements.View? dlc = null;

		if (unlock && (set.Total > 0)) {
			dlc = await DlcAchievements.ViewAsync(bot, appId, TimeSpan.FromSeconds(20)).ConfigureAwait(false);

			if (!dlc.Known) {
				return new Said("Still checking which of {0}'s achievements come with DLC - try again in a few minutes.", GameNames.Of(appId)).ToString();
			}
		}

		int held = 0;
		Said why = default;

		// The pacer's own rules for what may be unlocked - the same as unlock-everything: held for DLC, short of the game's
		// own counter, multiplayer on an account that skips those, "all other achievements" with others staying locked.
		// Only the hours and the order are left to whoever typed it.
		AchievementPacer.Sorted? sorted = (unlock && (dlc != null)) ? AchievementPacer.Sort(set, dlc, bot.Cfg.AchievementSkipMultiplayer, new HashSet<string>()) : null;

		if (target.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			if (sorted != null) {
				(chosen, int metas) = AchievementPacer.WithoutWaitingMetas(set.All, sorted.Candidates);
				held = sorted.HeldDlc;
				int steamOnly = set.All.Count(static a => !a.Unlocked && !a.Settable);

				// What it left, and why - said with the answer, so "unlocked 40" is never read as "all of them".
				foreach (Said part in new[] {
					sorted.Counters > 0 ? new Said("{0} left locked - the game's own counter for them isn't there yet", sorted.Counters) : default,
					sorted.Multiplayer > 0 ? new Said("{0} multiplayer ones skipped", sorted.Multiplayer) : default,
					metas > 0 ? new Said("{0} left locked - they're for having all the others, and some of those stay locked", metas) : default,
					steamOnly > 0 ? new Said("{0} only Steam can award", steamOnly) : default
				}.Where(static p => !p.IsEmpty)) {
					why = why.IsEmpty ? part : new Said("{0}, {1}", why, part);
				}
			} else {
				chosen = (unlock ? set.Locked : set.Unlocked.Where(static a => a.Settable)).ToList();
			}
		} else {
			Achievement? one = set.All.FirstOrDefault(a => a.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

			if (one == null) {
				return $"{GameNames.Of(appId)} has no achievement called '{target}'.";
			}

			if (!one.Settable) {
				return $"\"{one.Display}\" is awarded by Steam's own servers - a client isn't allowed to set it.";
			}

			if ((dlc != null) && !dlc.Allows(one)) {
				// "Comes with DLC it doesn't own" only when that's known exactly; one held on a guess may come with it. Held
				// on a guess at all is only in a game left alone ('dlc leave'), or inside a block of a DLC it doesn't own
				// whose edges are drawn wide to be safe.
				return dlc.Of(one) switch {
					DlcAchievements.Hold.Unmapped or DlcAchievements.Hold.NotOwned when !dlc.Certain(one) => new Said("\"{0}\" may come with DLC {1} doesn't own, and Steam doesn't list which achievements that DLC adds ({2}) - it won't be unlocked.",
						one.Display, bot.Name, Few(dlc.Missing(one))) + (dlc.Trusted ? "" : " " + LeftHint(bot, appId)),
					DlcAchievements.Hold.Checking => new Said("Still checking which of {0}'s achievements come with DLC - try again in a few minutes.", GameNames.Of(appId)).ToString(),
					_ => new Said("\"{0}\" comes with DLC {1} doesn't own ({2}) - it won't be unlocked.", one.Display, bot.Name, string.Join(", ", dlc.Missing(one))).ToString()
				};
			}

			if (unlock && (sorted != null) && (WhyNotOne(bot, set, one) is { IsEmpty: false } refusal)) {
				return refusal.ToString();
			}

			chosen = [one];
		}

		// Said with the answer, whatever it is, so "unlocked 40" is never read as "all of them".
		string heldNote = (held == 0 ? ""
			: dlc is { Unmapped: true } ? " " + new Said("{0} left alone - can't tell which achievements come with its DLC", held) + "."
			: " " + new Said("{0} left alone - from DLC this account doesn't own", held) + ".")
			+ (why.IsEmpty ? "" : " " + why + ".");

		if (chosen.Count == 0) {
			return (unlock ? "Nothing left to unlock." : "Nothing unlocked that can be put back.") + heldNote;
		}

		(bool ok, string message, int changed) = await Achievements.SetAsync(bot, set, chosen, unlock, dlc: dlc).ConfigureAwait(false);

		if (!ok) {
			return $"{bot.Name}: {message}";
		}

		// Already that way by the time it wrote (the pacer got there first): said, but not logged as an unlock.
		if (changed == 0) {
			return (unlock ? "Nothing left to unlock." : "Nothing unlocked that can be put back.") + heldNote;
		}

		Log.Reward(new Said("{0} in {1}", message, GameNames.Of(appId)), bot.Name, topic: Topic.Achievements);

		// Counted for "achievements today" - only ones gained, not ones put back.
		if (unlock) {
			Stats.Record(Stats.KindAchievement, bot.Name, changed);

			// One that completed a "for having the others" achievement: the pacer awards that on the game's next tick.
			BotManager.ModuleOf<AchievementPacer>(bot)?.LookForMetas(appId);
		}

		return $"{bot.Name}: {message} in {GameNames.Of(appId)}.{heldNote}";
	}

	/// <summary>
	/// Why one achievement named in 'cheevo unlock' won't be unlocked by the pacer's rules, or nothing: multiplayer on an
	/// account that skips those, short of the game's own counter, or "all other achievements" with others still locked.
	/// (Held for DLC and Steam's own are said before this.) Called after Sort, which marks each one's add-on part.
	/// </summary>
	internal static Said WhyNotOne(Bot bot, AchievementSet set, Achievement one) {
		if (one.Unlocked) {
			return default;
		}

		if (bot.Cfg.AchievementSkipMultiplayer && AchievementPacer.IsMultiplayer(one, AchievementPacer.MultiplayerGame(set.All))) {
			return new Said("\"{0}\" needs other players, and {1} skips multiplayer achievements (\"Skip multiplayer achievements\") - it won't be unlocked.", one.Display, bot.Name);
		}

		if (one.CounterShort) {
			return new Said("\"{0}\" counts something in the game, and the game's own counter is at {1} of {2} - it won't be unlocked before the game gets there.",
				one.Display, one.ProgressNow.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), one.ProgressMax.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
		}

		// The pacer's own rule for one at a time. Asked as a batch of one, two "for having the others" ones ("all other
		// achievements" and "unlock every achievement") each waited on the other, and both were refused for good.
		if (AchievementPacer.MetaBlocked(one, set.All)) {
			return new Said("\"{0}\" is for having the others, and some of those are still locked - it won't be unlocked before they are.", one.Display);
		}

		return default;
	}

	private static string DescribeAchievements(Bot bot, AchievementSet set, DlcAchievements.View? dlc = null) {
		// Steam's answer for a game with no stats at all - "0/0 unlocked" and a legend for an empty list said the
		// same thing far less clearly.
		//
		// Steam gives the same answer, Fail, when the account doesn't own the game - asked about ARC Raiders on an
		// account without it, this said "ARC Raiders has no achievements" of a game that has fifty.
		if (set.Total == 0) {
			return bot.Library.Find(set.AppId) is null
				? $"{bot.Name} doesn't own {GameNames.Of(set.AppId)}, and Steam only shows achievements for games in the library."
				: $"{GameNames.Of(set.AppId)} has no achievements - there is nothing to see or unlock.";
		}

		StringBuilder sb = new();
		int blocked = set.All.Count(static a => !a.Settable && !a.Unlocked);

		sb.AppendLine($"{GameNames.Of(set.AppId)} on {bot.Name} - {set.UnlockedCount}/{set.Total} unlocked"
			+ (blocked > 0 ? $", {blocked} of them Steam won't let a client set" : ""));

		// Easiest first, which is both the order they would really be earned in and the order that makes the
		// list readable - the ones near the top are the ones anybody playing would already have.
		// From DLC this account doesn't own: marked, because none of those is ever unlocked - and one that already is
		// wasn't earned by playing, so it's worth seeing.
		//
		// Held on other evidence - a DLC named in it, "DLC" in its API name, a game left alone - is [h]: held, but not known
		// to be that DLC's. What only looks like an add-on's by the game's layout is [?]: earned last, never held.
		bool anyDlc = false, anyUnsure = false, anyLater = false;
		HashSet<string> addOn = dlc?.AddOnSet(set.All) ?? [];

		foreach (Achievement a in set.All.OrderByDescending(static a => a.GlobalPercent ?? 50)) {
			DlcAchievements.Hold hold = dlc?.Of(a) ?? DlcAchievements.Hold.None;

			// "From DLC this account doesn't own" only where that is known exactly: inside the block of a DLC whose store
			// names lined up (Map.Sure). Black Ops 6's achievements, held because nobody can say which DLC they come
			// with, were marked [X] - "unlocked, but from DLC this account doesn't own" - which nobody knows. Those are
			// "can't tell", locked or not. An unlocked one in a game that can't be mapped at all stays plain [x]: it isn't
			// tied to any DLC, the whole game just can't be told apart.
			bool fromDlc = (hold == DlcAchievements.Hold.NotOwned) && dlc!.Certain(a);
			bool unsure = !a.Unlocked && !fromDlc && (hold is DlcAchievements.Hold.NotOwned or DlcAchievements.Hold.Unmapped);
			// Allowed only because the game earns anyway, and it looks like an add-on's (after the jump in the game's own
			// layout): probably an add-on's, so it's earned after every base-game one - on every account, never held for it.
			bool later = !a.Unlocked && a.Settable && (hold == DlcAchievements.Hold.None) && (dlc?.AddOnLikely(a, addOn) ?? false);
			string mark = a.Unlocked ? (fromDlc ? "X" : "x") : fromDlc ? "d" : unsure ? "h" : later ? "?" : a.Settable ? " " : "-";
			string rarity = a.GlobalPercent is { } p ? $"{p,5:0.#}%" : "     ?";
			anyDlc |= fromDlc;
			anyUnsure |= unsure;
			anyLater |= later;
			sb.AppendLine($"  [{mark}] {rarity}  {Log.Pad(a.Name, 34)} {a.Display}");
		}

		sb.Append("  [x] unlocked   [ ] can be unlocked   [-] Steam-awarded only");

		if (anyDlc) {
			sb.AppendLine().Append("  [d] ").Append(new Said("from DLC this account doesn't own - never unlocked")).Append("   [X] ").Append(new Said("unlocked, but from DLC this account doesn't own"));
		}

		if (anyUnsure) {
			sb.AppendLine().Append("  [h] ").Append(new Said("may come with DLC this account doesn't own - left alone")).Append(" (").Append(new Said("see 'dlc {0} {1}'", bot.Name, set.AppId)).Append(')');
		}

		if (anyLater) {
			sb.AppendLine().Append("  [?] ").Append(new Said("probably DLC - earned last, after the base game's")).Append(" (").Append(new Said("see 'dlc {0} {1}'", bot.Name, set.AppId)).Append(')');
		}

		if (!anyDlc && !anyUnsure && !anyLater && dlc is { Known: false }) {
			sb.AppendLine().Append("  ").Append(new Said("Still checking which of {0}'s achievements come with DLC - try again in a few minutes.", GameNames.Of(set.AppId)));
		}

		return sb.ToString();
	}

	/// <summary>
	/// 'dlc': achievements that come with add-ons. Looking (a game, or every game it works on), and the one choice about
	/// a game with add-ons Steam doesn't explain - leave it alone, or undo that. The looking never changes anything.
	/// </summary>
	private static async Task<string> DlcAsync(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return string.Join(Environment.NewLine, [
				"dlc <account> [game]                  which add-ons matter, and what is held",
				"dlc leave <account> <game>            leave a game with add-ons Steam doesn't explain alone",
				"dlc undo <account> <game>             let a game left alone earn again",
				"  dlc new Call of Duty                Call of Duty's add-ons, and which this account owns"
			]);
		}

		// A choice - unless there's an account called that, which is then what was meant.
		string verb = args[0].ToLowerInvariant();

		if ((verb is "leave" or "undo") && (mgr.Get(args[0]) == null)) {
			return DlcAnswer(mgr, verb, args[1..]);
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (!bot.IsOnline) {
			return $"{bot.Name} isn't logged in.";
		}

		if (args.Length > 1) {
			(uint app, Said problem) = DlcChoices.Find(bot, string.Join(' ', args[1..]));

			return app == 0 ? problem.ToString() : await DlcGameAsync(bot, app).ConfigureAwait(false);
		}

		// The games it works on: what the pacer has tracked, and what the hunt has lined up.
		List<uint> apps = [.. (BotManager.ModuleOf<AchievementPacer>(bot)?.Snapshot().Select(static r => r.App) ?? [])
			.Concat(BotManager.ModuleOf<AchievementBoost>(bot)?.Plan().Next.Select(static r => r.App) ?? [])
			.Distinct()];

		if (apps.Count == 0) {
			return new Said("{0}: the achievement pacer and the hunt haven't worked on any game yet", bot.Name).ToString();
		}

		// Only what is already worked out is read now. The rest is asked for - twenty at a time at most, one game after
		// another, slowly - and a later look has them. A hunt list can be hundreds of games, and working them all out at
		// once is hundreds of questions to the store.
		List<string> rows = [];
		int waiting = 0, asked = 0;

		foreach (uint app in apps) {
			DlcAchievements.Map? map = DlcAchievements.Current(app);

			if (map == null) {
				waiting++;

				if (!DlcAchievements.IsPending(app) && (asked < 20)) {
					DlcAchievements.Request(bot, app);
					asked++;
				}

				continue;
			}

			if (map.Groups.Count == 0) {
				continue;
			}

			DlcAchievements.View view = await DlcAchievements.ViewAsync(bot, app, TimeSpan.Zero).ConfigureAwait(false);

			// Nothing held, and nothing it can't place: its add-ons don't matter here.
			if ((view.NotOwnedCount == 0) && !view.Unclear) {
				continue;
			}

			Said row;

			if (view.Unmapped) {
				// Left alone ('dlc leave'): held as it used to be.
				row = new Said("{0} left alone, as you asked - can't tell which achievements come with its DLC", view.NotOwnedCount);
			} else if (view.Unclear) {
				row = view.NotOwnedCount > 0
					? new Said("earns them anyway, base game first; {0} from DLC it doesn't own left alone", view.NotOwnedCount)
					: new Said("earns them anyway, base game first - Steam doesn't say which come with the add-ons it doesn't own");
			} else {
				AchievementSet? set = await Achievements.GetAsync(bot, app).ConfigureAwait(false);
				Said got = set is { Total: > 0 } ? new Said("{0} of them unlocked", view.UnlockedWithout(set)) : new Said("couldn't read what it has unlocked");
				row = new Said("{0} from DLC it doesn't own, {1}", view.NotOwnedCount, got);
			}

			rows.Add($"  {GameNames.Of(app)} ({app}) - " + row);
		}

		List<string> lines = rows.Count == 0
			? [new Said("{0}: none of the games it hunts or plays has add-ons that matter for achievements", bot.Name)]
			: [new Said("{0}: games with add-ons it doesn't own (achievements from those are never unlocked)", bot.Name), .. rows];

		if (waiting > 0) {
			lines.Add("  " + new Said("{0} more game(s) still being looked at - ask again in a few minutes", waiting));
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>
	/// 'dlc leave|undo &lt;account&gt; &lt;game&gt;' - the dashboard's undo, typed. Doesn't
	/// need the account signed in: it's only the account's choice, kept in its settings file.
	/// </summary>
	private static string DlcAnswer(BotManager mgr, string verb, string[] args) {
		if (args.Length < 2) {
			return $"dlc {verb} <account> <game>   (the game's name or appID)";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		(uint app, Said problem) = DlcChoices.Find(bot, string.Join(' ', args[1..]));

		if (app == 0) {
			return problem.ToString();
		}

		return verb == "leave" ? DlcChoices.Leave(bot, app) : DlcChoices.Undo(bot, app);
	}

	private static async Task<string> DlcGameAsync(Bot bot, uint app) {
		DlcAchievements.View view = await DlcAchievements.ViewAsync(bot, app, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

		if (view.Map is not { } map) {
			return new Said("Still working out {0}'s DLC - try again in a few minutes.", GameNames.Of(app)).ToString();
		}

		// Couldn't read which DLC it owns just now: nothing in the game is unlocked until it can, and nothing is said
		// about owning or not owning.
		if (!view.Known) {
			return new Said("Couldn't read which DLC {0} owns just now - nothing in {1} is unlocked until it can. Try again in a few minutes.", bot.Name, GameNames.Of(app)).ToString();
		}

		AchievementSet? set = await Achievements.GetAsync(bot, app).ConfigureAwait(false);
		List<DlcAchievements.Group> placed = [.. map.Groups.Where(static g => g.Unsure == DlcAchievements.Doubt.None)];
		List<DlcAchievements.Group> unsure = [.. map.Groups.Where(static g => g.Unsure != DlcAchievements.Doubt.None)];
		List<string> lines = [new Said("{0} on {1}: {2} DLC, {3} with achievements in its list", GameNames.Of(app), bot.Name, map.DlcCount, placed.Count)];

		foreach (DlcAchievements.Group g in placed) {
			bool owned = view.Licensed.Contains(g.App);
			int count = g.Located;
			Said got = new("couldn't read what it has unlocked");

			if (set is { Total: > 0 }) {
				count = set.All.Count(a => view.In(a, g.App));
				int unlocked = set.All.Count(a => a.Unlocked && view.In(a, g.App));

				// "Without owning it" only for the ones certainly inside its block: the edges of a block are drawn wide to be
				// safe, and an unlocked one there may well be the base game's.
				int without = owned ? 0 : set.All.Count(a => a.Unlocked && view.SurelyIn(a, g.App));
				got = without > 0
					? new Said("{0} of them unlocked without owning it", without)
					: new Said("{0} of them unlocked", unlocked);
			}

			string name = g.Name.Length > 0 ? g.Name : GameNames.Of(g.App);
			Said how = g.ByName && (g.Total == 0) ? new Said("found by its name")
				: (g.Total > 0) && !g.Exact ? new Said("the store's names didn't line up, so more are held back to be safe")
				: default;

			Said state = owned ? new Said("owned") : new Said("not owned - never unlocked");

			lines.Add($"  {name} ({g.App}): " + new Said("{0} achievement(s), {1}, {2}", count, state, got)
				+ (how.IsEmpty ? "" : " - " + how));
		}

		// DLC whose achievements, if they have any, can't be placed - by its licences, not what is counted as owned: a game
		// that earns anyway counts them all as owned. Owned, they change nothing.
		List<DlcAchievements.Group> missing = [.. unsure.Where(g => !view.Licensed.Contains(g.App))];

		if ((missing.Count > 0) && !view.Trusted) {
			// Left alone ('dlc leave'): the whole game is held but for the DLC it owns - said as that, with which DLC and
			// why, so it's plain why nothing is unlocked.
			int held = set is { Total: > 0 } ? view.HeldIn(set) : view.NotOwnedCount;

			// None left to hold - every one is already unlocked - reads as that, not as "0 are left alone".
			lines.Add("  " + (held > 0
				? new Said("Held: {0} is missing DLC, and Steam doesn't list which achievements they add - so {1} achievement(s) outside the DLC it owns are left alone.", bot.Name, held)
				: new Said("{0} is missing DLC, and Steam doesn't list which achievements they add - but every achievement outside the DLC it owns is already unlocked, so there's nothing to hold.", bot.Name)));
			MissingRows(lines, missing);
			lines.Add("  " + LeftHint(bot, app));
		} else if (missing.Count > 0) {
			// Earns anyway, on every account: the base game's first, what looks like an add-on's by the game's layout last,
			// and only what is held on certain evidence never - an add-on's exact block, one naming an add-on it doesn't own,
			// one with "DLC" in its API name.
			HashSet<string> addOn = set is { Total: > 0 } ? view.AddOnSet(set.All) : [];
			int later = set is { Total: > 0 } ? set.All.Count(a => !a.Unlocked && a.Settable && view.AddOnLikely(a, addOn)) : 0;
			int named = set is { Total: > 0 } ? set.All.Count(a => !a.Unlocked && view.HeldForName(a)) : 0;

			lines.Add("  " + (later > 0
				? new Said("{0} is missing DLC, and Steam doesn't list which achievements they add. It earns them anyway - the base game's first, and the {1} it can't place after those, in case some come with that DLC.", bot.Name, later)
				: new Said("{0} is missing DLC, and Steam doesn't list which achievements they add. It earns them anyway - the base game's first, and the ones it can't place after those.", bot.Name)));

			if (named > 0) {
				lines.Add("  " + new Said("{0} with \"DLC\" in their API name are left alone while it's missing add-ons.", named));
			}

			MissingRows(lines, missing);

			List<string> exact = [.. placed.Where(g => !view.Licensed.Contains(g.App)).Select(g => DlcAchievements.DlcName(map, g.App))];

			if (exact.Count > 0) {
				lines.Add("  " + new Said("It never touches the achievements of {0}, which it doesn't own.", Few(exact)));
			}

			lines.Add("  " + new Said("To have {1} left alone instead: 'dlc leave {0} {2}'.", bot.Name, GameNames.Of(app), DlcChoices.Typed(bot, app)));
		} else if (unsure.Count > 0) {
			lines.Add("  " + new Said("Steam doesn't list which achievements {0} of its DLC add, but {1} owns all of those - nothing is held for them.", unsure.Count, bot.Name));
		} else if (map.DlcCount == 0) {
			lines.Add("  " + new Said("It has no DLC."));
		} else if (placed.Count == 0) {
			lines.Add("  " + new Said("None of its DLC add achievements to its list - they're soundtracks and the like."));
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>The DLC it's missing that can't be placed, ten at most, each with why.</summary>
	private static void MissingRows(List<string> lines, List<DlcAchievements.Group> missing) {
		foreach (DlcAchievements.Group g in missing.Take(10)) {
			Said why = g.Unsure switch {
				DlcAchievements.Doubt.BasePage => new Said("its store page is the game's own"),
				DlcAchievements.Doubt.Delisted => new Said("it isn't on the store any more"),
				DlcAchievements.Doubt.NotFound => new Said("the store's names for its achievements aren't in the game's list"),
				_ => new Said("the store gives no achievement figures for it")
			};

			lines.Add($"    {(g.Name.Length > 0 ? g.Name : GameNames.Of(g.App))} ({g.App}): " + new Said("not owned - {0}", why));
		}

		if (missing.Count > 10) {
			lines.Add("    " + new Said("and {0} more", missing.Count - 10));
		}
	}

	/// <summary>A game left alone says how to let it earn again.</summary>
	private static Said LeftHint(Bot bot, uint app) =>
		new Said("It's left alone because you asked - 'dlc undo {0} {1}' lets it earn them, base game first.", bot.Name, DlcChoices.Typed(bot, app));

	/// <summary>A few names, then how many more: a game can have a hundred DLC.</summary>
	private static string Few(List<string> names) =>
		names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + ", " + new Said("and {0} more", names.Count - 3);

	private static string Play(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "play <account> <appIDs...>   or   play <account> none";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		// Human mode plays from its own list, and with that filled in the idle list does nothing - this answered
		// "idling 730" and nothing changed, and the saved list was put back from the backup when human mode went off.
		if (bot.Cfg.LegitMode && !string.IsNullOrWhiteSpace(bot.Cfg.GameWeights)) {
			return new Said("{0} is in human mode - it plays the games in \"Games and how often\" (GameWeights), not this list", bot.Name).ToString();
		}

		lock (bot.CfgGate) {
			string? error = Settings.Apply(bot.Cfg, Settings.FindBot("IdleGames")!, string.Join(',', args[1..]));

			if (error != null) {
				return error;
			}

			ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		BotManager.ModuleOf<Idler>(bot)?.Assert();

		return bot.Cfg.IdleGames.Count == 0 ? $"{bot.Name}: stopped idling" : $"{bot.Name}: idling {string.Join(", ", bot.Cfg.IdleGames)}";
	}

	/// <summary>
	/// A length of time as people type it: "6", "1.5" or "1,5" (whatever the PC's language), "1h", "90m", "1h30m", "2h 15m".
	/// In hours. It used to be the PC's own number format only - "1h" was refused, and on a German PC so was "1.5".
	/// At least a minute: "0.5m" started a grind of half a minute, shown as "0m", over before anything had happened.
	/// </summary>
	public static bool TryHours(string text, out double hours) {
		hours = 0;
		string t = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal).Replace(',', '.');

		// A bare number is hours.
		// [0-9], not \d: \d takes every script's digits, a full-width "３" among them, and double.Parse only reads 0-9 -
		// typed on a Japanese keyboard, 'grind' threw instead of answering.
		if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9]+(?:\.[0-9]+)?$")) {
			hours = double.Parse(t, CultureInfo.InvariantCulture);

			return AtLeastAMinute(hours);
		}

		// Otherwise every number has its unit, and the pieces cover the whole text: "1h30m" yes, "1h30" no. One pattern
		// with both parts optional read "90m" as 9 hours and 0 minutes.
		System.Text.RegularExpressions.MatchCollection parts = System.Text.RegularExpressions.Regex.Matches(t,
			@"([0-9]+(?:\.[0-9]+)?)(hours|hour|hrs|hr|h|minutes|minute|mins|min|m)");

		if ((parts.Count == 0) || (string.Concat(parts.Select(static p => p.Value)) != t)) {
			return false;
		}

		foreach (System.Text.RegularExpressions.Match part in parts) {
			double n = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
			hours += part.Groups[2].Value.StartsWith('h') ? n : n / 60;
		}

		return AtLeastAMinute(hours);

		// Rounded first, so "1m" - a sixtieth of an hour, not exactly - still counts as the minute it is.
		static bool AtLeastAMinute(double h) => double.IsFinite(h) && (Math.Round(h * 60, 6) >= 1);
	}

	private static string Name(BotManager mgr, string[] args, string line) {
		if (args.Length == 0) {
			return "gamename <account> [text|off]   (no text shows the current one, 'off' clears it)";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		// Just the account: say what it shows. This used to clear the name - typing 'name kylro' to look at it
		// wiped it, which is exactly what anybody would try first.
		if (args.Length == 1) {
			return string.IsNullOrEmpty(bot.Cfg.CustomGameName)
				? $"{bot.Name}: no custom name - it shows the real game. 'gamename {bot.Name} <text>' sets one."
				: $"{bot.Name}: shows \"{bot.Cfg.CustomGameName}\"{(bot.Cfg.LegitMode ? " (not while human mode is on)" : bot.Cfg.CustomGameNameEnabled ? "" : " (switched off under Settings)")} · 'gamename {bot.Name} off' clears it";
		}

		// As typed, from the line: put back together from its words, "a<nbsp>b" was stored as "a b".
		string text = Tail(line, 2);
		bool clearing = text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("clear", StringComparison.OrdinalIgnoreCase);

		lock (bot.CfgGate) {
			bot.Cfg.CustomGameName = clearing ? "" : text;

			// Naming it is asking for it to be shown. With "Show a custom game name" switched off this answered "now showing"
			// and the friends list went on showing the real game.
			if (!clearing) {
				bot.Cfg.CustomGameNameEnabled = true;
			}

			ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		BotManager.ModuleOf<Idler>(bot)?.Assert();

		// Human mode always shows the real game (Bot.CustomName), so "now showing" would be untrue there.
		return string.IsNullOrEmpty(bot.Cfg.CustomGameName) ? $"{bot.Name}: showing the real game again"
			: bot.Cfg.LegitMode ? new Said("{0}: saved \"{1}\" - it shows once human mode is off; human mode always shows the real game", bot.Name, bot.Cfg.CustomGameName).ToString()
			: $"{bot.Name}: now showing \"{bot.Cfg.CustomGameName}\"";
	}

	private static string Persona(BotManager mgr, string[] args, string line) {
		if (args.Length == 0) {
			return "persona <account> [online|offline|busy|away|snooze|looking to trade|looking to play|invisible (or 0-7)]   (no state says the current one)";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		SettingDef def = Settings.FindBot("OnlineStatus")!;

		// Just the account: say what it shows, as 'gamename <account>' does for the name. This answered with the usage.
		if (args.Length == 1) {
			return PersonaNow(bot, def);
		}

		lock (bot.CfgGate) {
			// Everything after the account: "looking to trade" is three words, and only the first used to reach here.
			string? error = Settings.Apply(bot.Cfg, def, Tail(line, 2));

			if (error != null) {
				return error;
			}

			ConfigStore.SaveBot(bot.Name, bot.Cfg);
		}

		bot.ApplyPersona();

		return $"{bot.Name}: {Settings.ChoiceLabel(def, bot.Cfg.OnlineStatus)}";
	}

	/// <summary>
	/// 'persona &lt;account&gt;': what its friends see now and what OnlineStatus is set to - the two differ while human mode has it
	/// away on a break or invisible for the night, while your own Steam client is overriding it, and while it's signed out.
	/// </summary>
	internal static string PersonaNow(Bot bot, SettingDef def) {
		string set = Settings.ChoiceLabel(def, bot.Cfg.OnlineStatus);
		string shows = !bot.IsOnline ? $"signed out, so friends see it offline - set to {set}"
			: bot.Cfg.IUseThisAccount ? $"left to your own Steam client (\"I sign into this one myself\" is on) - set to {set} for when that's off"
			: bot.PersonaOverridden ? $"{bot.PersonaReallyWord} - your own Steam client is overriding the {bot.PersonaWord} it sets"
			: bot.EffectivePersona != bot.Cfg.OnlineStatus ? $"{bot.PersonaWord} for now - set to {set}"
			: set;
		string human = bot.Cfg.LegitMode && !bot.Cfg.IUseThisAccount
			? ". Human mode sets it through the day - away on breaks, invisible overnight - so this is its status while playing"
			: "";

		return $"{bot.Name}: {shows} - 'persona {bot.Name} <state>' changes it (online, offline, busy, away, snooze, looking to trade, looking to play, invisible){human}";
	}

	/// <summary>'notify': what's set up, and what gets sent - in words, not the code's names for them.</summary>
	/// <param name="atThisPc">'notify link' gives the Telegram connect link only then: whoever opens it first is connected as
	/// the owner, so it isn't for a phone, a chat, or the log (it used to be written there for anyone with the file to use).</param>
	private static async Task<string> NotifyAsync(string[] args, bool atThisPc) {
		if ((args.Length > 0) && args[0].Equals("test", StringComparison.OrdinalIgnoreCase)) {
			return string.Join(Environment.NewLine, await Notifier.TestAsync().ConfigureAwait(false));
		}

		GlobalConfig g = Live.Global;

		if ((args.Length > 0) && args[0].Equals("link", StringComparison.OrdinalIgnoreCase)) {
			if (!atThisPc) {
				return "The Telegram connect link is only given at this PC: 'notify link' in the nocat.farm window or its console, or Connect Telegram in the dashboard's Settings, Notifications.";
			}

			return Notifier.TelegramConnectLink is { } link
				? $"Open {link} and press Start. Whoever does that first is connected as the owner, so keep the link to yourself."
				: g.TelegramBotToken.Length == 0 ? "There's no Telegram bot yet - put its token in Settings, Notifications first."
				: g.TelegramChatId.Length > 0 ? "Telegram is already connected."
				: "Telegram hasn't said who the bot is yet - try again in a minute.";
		}

		List<string> sent = [.. Enum.GetValues<Topic>().Where(Notifier.Wanted).Select(Notifier.Label)];

		return $"Discord: {(g.DiscordWebhookUrl.Length > 0 ? "set up" : "not set up")}"
			+ Environment.NewLine + $"Telegram: {(g.TelegramBotToken.Length == 0 ? "not set up" : g.TelegramChatId.Length == 0 ? "bot set - press Connect Telegram in Settings, Notifications to link it, or type 'notify link' here at this PC" : "connected")}"
			+ Environment.NewLine + Notifier.DiscordBotState()
			+ Environment.NewLine + $"Sends: {(sent.Count > 0 ? string.Join(", ", sent) : "nothing")}"
			+ Environment.NewLine + "'notify test' sends a test message now. Set it up under Settings, Notifications.";
	}

	private static async Task<string> JoinGroupAsync(BotManager mgr, string[] args, string line) {
		if (args.Length < 2) {
			return "joingroup <account|all> <group link or name>   e.g. joingroup all steamcommunity.com/groups/nocatfarm";
		}

		// The group as typed - its word split at spaces alone, as it always was: from the line's words, a no-break space in a
		// group's name cut the name short there. A line break or a tab is a gap too: a Telegram message on two lines ran the
		// group into the next line, and it "didn't look like a Steam group".
		string typed = Tail(line, 2).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];

		if (GroupJoin.Normalise(typed) is not { } path) {
			return $"\"{typed}\" doesn't look like a Steam group - paste its link, like steamcommunity.com/groups/name";
		}

		if (Pick(mgr, [args[0]], out string? problem) is not { } bots) {
			return problem!;
		}

		List<string> lines = [];
		List<Bot> ready = [];

		foreach (Bot b in bots) {
			if (!b.IsOnline || !b.Web.Ready || (BotManager.ModuleOf<GroupJoin>(b) is null)) {
				lines.Add($"{b.Name}: not logged in");
			} else {
				ready.Add(b);
			}
		}

		string Say(Bot b, GroupJoin.Outcome outcome, string name) => outcome switch {
			GroupJoin.Outcome.Joined => $"{b.Name}: joined {name}",
			GroupJoin.Outcome.AlreadyIn => $"{b.Name}: already in {name}",
			GroupJoin.Outcome.NeedsApproval => $"{b.Name}: {name} needs an admin to approve new members - not joined",
			GroupJoin.Outcome.InviteOnly => $"{b.Name}: {name} is invite only - not joined",
			GroupJoin.Outcome.NotFound => $"{b.Name}: there's no Steam group at \"{typed}\"",
			_ => $"{b.Name}: Steam didn't answer - try again in a minute"
		};

		if (ready.Count == 0) {
			return string.Join(Environment.NewLine, lines);
		}

		// The first account now, so you see straight away whether the group can be joined at all.
		(GroupJoin.Outcome first, string groupName) = await BotManager.ModuleOf<GroupJoin>(ready[0])!.JoinAsync(path, null, CancellationToken.None).ConfigureAwait(false);
		lines.Add(Say(ready[0], first, groupName));

		// The rest a minute or three apart, in the background: several accounts landing in one group's member list
		// in the same few seconds is a giveaway. Not worth doing at all when the group can't be joined.
		if ((ready.Count > 1) && first is GroupJoin.Outcome.Joined or GroupJoin.Outcome.AlreadyIn) {
			List<Bot> rest = [.. ready.Skip(1)];

			_ = Task.Run(async () => {
				foreach (Bot b in rest) {
					await Task.Delay(Rng.Seconds(60, 180)).ConfigureAwait(false);

					try {
						if ((BotManager.ModuleOf<GroupJoin>(b) is { } g) && b.IsOnline) {
							(GroupJoin.Outcome o, string n) = await g.JoinAsync(path, null, CancellationToken.None).ConfigureAwait(false);
							Log.Info(Say(b, o, n), b.Name);
						}
					} catch (Exception e) {
						// One account's failure mustn't stop the rest - nor vanish, in a task nobody awaits.
						Log.Failed("joining the group", e, b.Name);
					}
				}
			});

			lines.Add($"the other {rest.Count} account(s) follow one every minute or three - the log says how each went");
		}

		return string.Join(Environment.NewLine, lines);
	}

	// ── slow commands ───────────────────────────────────────────────────────
	/// <summary>
	/// How long a command waits on its own work before it answers that the work is still going. The discovery queue takes
	/// about four minutes; the dashboard's request gave up at two and said "timed out" about a queue that went through
	/// fine, and Telegram and Discord sat there with nothing. Anything quicker than this still comes back as the reply.
	/// </summary>
	public static TimeSpan ReplyWithin { get; set; } = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Command work still going, by what and whose, with what it is doing - so a second 'queue old' says it's on it rather
	/// than starting another, and says what is going rather than what was just asked for.
	/// </summary>
	private static readonly Dictionary<string, Underway> Going = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Where the answer of a slow command run now goes once it's done, besides the log - set by every command as it starts
	/// (see <see cref="RunAsync(BotManager, string, bool, Action{string})"/>). Only Steam chat has one: 'log' is refused
	/// there, so a master's '!queue' was told "the log says when it's done" and never saw how it went. Telegram and Discord
	/// read the log with 'log'. And what the command's line hid (<see cref="Masked"/>), so the answer that comes late is masked
	/// in the log as one in time is (<see cref="ReplyForLog"/>).
	/// </summary>
	private static readonly AsyncLocal<(Action<string>? Tell, (string Text, string As)[]? Hidden)> LateReply = new();

	/// <summary>One account's slow work still going: what it's doing, and the chats waiting to be told how it went.</summary>
	private sealed class Underway(string doing) {
		private readonly List<Action<string>> _waiting = [];
		private string? _answer;

		public string Doing { get; } = doing;

		/// <summary>Told the answer when it comes - false when it has already come, and nothing will be told any more.</summary>
		public bool Tell(Action<string> who) {
			lock (_waiting) {
				if (_answer != null) {
					return false;
				}

				_waiting.Add(who);

				return true;
			}
		}

		/// <summary>The answer, to everybody waiting for it. A chat that can't be reached any more doesn't keep it from the rest.</summary>
		public void Answer(string answer, string source) {
			Action<string>[] waiting;

			lock (_waiting) {
				_answer = answer;
				waiting = [.. _waiting];
				_waiting.Clear();
			}

			foreach (Action<string> who in waiting) {
				try {
					who(answer);
				} catch (Exception e) {
					Log.Failed("sending a command's answer back", e, source);
				}
			}
		}
	}

	/// <summary>
	/// A command whose work can take a while. <paramref name="What"/> is the command; <paramref name="Doing"/> reads after the
	/// account's name and after "already" and "still" ("going through today's discovery queue"); <paramref name="HowLong"/>
	/// is said when it answers straight away. <paramref name="Key"/>, when the same command can rightly run twice at once
	/// for different things - two batches of keys, two offers. Without one, a second 'trade decline 5' while 'trade accept 3'
	/// was going was turned away as "already", and offer 5 was never declined.
	/// </summary>
	public sealed record Slow(string What, string Doing, string HowLong = "", string? Key = null);

	/// <summary>
	/// Run a slow command's work, account by account, and answer within <paramref name="wait"/>: what each finished account
	/// said, and for the rest that it's going on and the log says when it's done - where its answer then goes, under the
	/// account's name. Asked from Steam chat, which can't read the log, the answer goes back to the chat as well, and the reply
	/// says so. An account already doing this same thing for an earlier command isn't started twice.
	/// <paramref name="cannot"/> answers at once for an account that can't (not signed in, say). One at a time when
	/// <paramref name="oneAtATime"/>: several accounts claiming games or sending items in the same second is a giveaway.
	/// </summary>
	public static Task<string> SlowAsync(Slow job, IReadOnlyList<Bot> bots, Func<Bot, Task<string>> work, TimeSpan wait,
		bool oneAtATime = false, Func<Bot, string?>? cannot = null) =>
		SlowAsync(job, bots, static b => b.Name, static b => [b], work, wait, oneAtATime, cannot);

	/// <summary>
	/// The same for work that isn't any one account's - card swaps, a batch of keys: said and logged without a name.
	/// <paramref name="accounts"/> are the ones it works with, so one of them signing out partway is said as that.
	/// </summary>
	public static Task<string> SlowAsync(Slow job, Func<Task<string>> work, TimeSpan wait, IReadOnlyList<Bot>? accounts = null) =>
		SlowAsync(job, [""], static _ => "", _ => accounts ?? [], _ => work(), wait, false, null);

	private static async Task<string> SlowAsync<T>(Slow job, IReadOnlyList<T> who, Func<T, string> nameOf, Func<T, IReadOnlyList<Bot>> accountsOf,
		Func<T, Task<string>> work, TimeSpan wait, bool oneAtATime, Func<T, string?>? cannot) {
		string[] lines = new string[who.Count];
		Task<Done>?[] tasks = new Task<Done>?[who.Count];
		Underway?[] underway = new Underway?[who.Count];
		Task before = Task.CompletedTask;

		// Asked from Steam chat, the answer goes back there when it's done: the log, where it goes anyway, can't be read from there.
		(Action<string>? later, (string Text, string As)[]? typed) = LateReply.Value;
		string whereItGoes = later != null ? "the answer comes here when it's done" : "the log says when it's done";

		for (int i = 0; i < who.Count; i++) {
			T one = who[i];
			string name = nameOf(one);
			string named = name.Length > 0 ? name + ": " : "";

			if (cannot?.Invoke(one) is { } why) {
				lines[i] = why;

				continue;
			}

			string key = $"{job.Key ?? job.What} {name}";

			lock (Going) {
				if (Going.TryGetValue(key, out Underway? going)) {
					// Still in Going, so its answer hasn't come yet: that is given only after it's out of here.
					if (later != null) {
						going.Tell(later);
					}

					lines[i] = $"{named}already {going.Doing} - {whereItGoes}";

					continue;
				}

				Going[key] = underway[i] = new Underway(job.Doing);
			}

			Underway mine = underway[i]!;
			string from = name.Length > 0 ? name : "nocat.farm";
			tasks[i] = Run(one, name, key, before);
			_ = tasks[i]!.ContinueWith(done => mine.Answer(done.Result.Reply, from), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
				TaskScheduler.Default);

			if (oneAtATime) {
				before = tasks[i]!;
			}
		}

		// Run never throws - a failure comes back as its answer, already in the log - so nothing here is ever a faulted task
		// left for the lost-task handler to find: "a background task failed", in red, about a queue cut short by a sign-out.
		Task all = Task.WhenAll(tasks.OfType<Task<Done>>());

		if (!all.IsCompleted && (wait > TimeSpan.Zero)) {
			await Task.WhenAny(all, Task.Delay(wait)).ConfigureAwait(false);
		}

		// And should one ever throw after all, seen here: past the reply nothing else is left to look at it.
		_ = all.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

		for (int i = 0; i < who.Count; i++) {
			if (tasks[i] is not { } task) {
				continue;
			}

			string name = nameOf(who[i]);
			string named = name.Length > 0 ? name + ": " : "";

			// Done in time, it's the reply itself. Asked to be told later and the answer came meanwhile (false), the same.
			if (task.IsCompletedSuccessfully || ((later != null) && !underway[i]!.Tell(later))) {
				lines[i] = task.Result.Reply;

				continue;
			}

			lines[i] = wait == TimeSpan.Zero
				? $"{named}{job.Doing} now{(job.HowLong.Length > 0 ? " - " + job.HowLong : "")}; {whereItGoes}"
				: $"{named}still {job.Doing} - {whereItGoes}";

			string source = name.Length > 0 ? name : "nocat.farm";
			Background.Run(job.What, () => task.ContinueWith(done => {
				// Cut short or failed: Run has already said so in the log, the way it was.
				if (done.Result.Logged) {
					return;
				}

				foreach (string line in done.Result.Reply.ReplaceLineEndings("\n").Split('\n').Where(static l => l.Trim().Length > 0)) {
					// Masked as the command log masks a reply: a product key 'redeem' was given stays out of the log. And without
					// the account's name in front - it goes in under that name already, and read "licclaim │ licclaim: 71456".
					Log.Info(MaskedReply(job.What, Unnamed(line, name), typed), source);
				}
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default), source);
		}

		return string.Join(Environment.NewLine, lines);

		async Task<Done> Run(T one, string name, string key, Task after) {
			string source = name.Length > 0 ? name : "nocat.farm";
			IReadOnlyList<Bot> accounts = accountsOf(one);
			long[] stops = [.. accounts.Select(static b => b.StopCount)];

			try {
				// The one before it, finished - each account's own failure is its own, and none of them throws.
				await after.ConfigureAwait(false);

				// Looked at again now its turn has come: that can be minutes after the command, and 'freegames all' went on
				// claiming for an account that had signed out meanwhile - every claim failing, and each put off for hours.
				if (cannot?.Invoke(one) is { } why) {
					return new Done(why, false);
				}

				return new Done(await work(one).ConfigureAwait(false), false);
			} catch (Exception e) {
				// Said in the log, not left to the debug log: nobody is waiting on the reply any more to see it.
				(Said said, bool cutOff) = Fell(job.What, e, accounts, stops, source);

				return new Done((name.Length > 0 ? name + ": " : "") + (cutOff ? said.ToEnglish() : "couldn't - " + Log.Cause(e).ToEnglish()), true);
			} finally {
				lock (Going) {
					Going.Remove(key);
				}
			}
		}
	}

	/// <summary>A slow command's answer for one account, and whether it was already said in the log.</summary>
	private readonly record struct Done(string Reply, bool Logged);

	/// <summary>A line of an account's answer without the "name: " it starts with, for the log that already names the account.</summary>
	private static string Unnamed(string line, string name) =>
		(name.Length > 0) && line.StartsWith(name + ": ", StringComparison.OrdinalIgnoreCase) ? line[(name.Length + 2)..] : line;

	/// <summary>
	/// Says in the log how a command's work ended before it was done, and returns what it said. An account it works with
	/// signing out, stopping or losing its connection partway isn't a failure - Steam drops every request still out, and
	/// that showed as a warning ("Exception of type 'SteamKit2.AsyncJobFailedException' was thrown") and then again, in
	/// red, as a lost task - so that's one plain line saying what stopped. Anything else is a warning with the cause in
	/// plain words. The type and the stack go to the debug log either way. <paramref name="stops"/> are the accounts'
	/// <see cref="Bot.StopCount"/> when the work began: one stopped and started again meanwhile counts as signed out too.
	/// </summary>
	internal static (Said Said, bool CutOff) Fell(string what, Exception e, IReadOnlyList<Bot> accounts, long[]? stops, string source) {
		Log.Failed($"'{what}' stopped before it was done", e, source);
		Log.StackToFile(e, source);

		Bot? gone = null;

		for (int i = 0; (i < accounts.Count) && (gone == null); i++) {
			Bot b = accounts[i];

			// Its web sign-in gone counts too - still connected, but the store and the community no longer take it: 'freegames'
			// stops there, and that said "Steam didn't answer in time". It's 'not logged in' to the commands' own check.
			if (!b.IsOnline || !b.Web.Ready || b.Stopping || ((stops != null) && (i < stops.Length) && (b.StopCount != stops[i]))) {
				gone = b;
			}
		}

		if ((gone != null) && Log.IsCutOff(e)) {
			Said said = SignedOutPartway(what, gone.Name);
			Log.Info(said, source);

			return (said, true);
		}

		Said failed = new("'{0}' stopped before it was done: {1}", what, Log.Cause(e));
		Log.Warn(failed, source);

		return (failed, false);
	}

	/// <summary>What stopped when an account signed out partway through a command's work, and what becomes of the rest.</summary>
	private static Said SignedOutPartway(string what, string account) => what switch {
		"queue" => new Said("the discovery queue stopped - {0} signed out before it was done; it's done again next time", account),
		"freeitems" => new Said("the look for free event items stopped - {0} signed out before it was done; 'freeitems' looks again once it's signed in", account),
		"freegames" => new Said("the look for free games stopped - {0} signed out before it was done; 'freegames' looks again once it's signed in", account),
		"booster" => new Said("making booster packs stopped - {0} signed out before it was done; 'booster' again once it's signed in makes the rest", account),
		"sell" => new Said("the market work stopped - {0} signed out before it was done; 'sell' again once it's signed in carries on", account),
		"match" => new Said("the card swaps stopped - {0} signed out before they were done; 'match' again once it's signed in", account),
		"redeem" => new Said("activating the keys stopped - {0} signed out before it was done; give 'redeem' the keys it didn't get to once it's signed in", account),
		"send" => new Said("sending the items stopped - {0} signed out before it was done; 'send' again once it's signed in sends the rest", account),
		"trade" => new Said("answering the trade offers stopped - {0} signed out before it was done; the ones it didn't get to are still waiting", account),
		"bans" => new Said("the ban look stopped - {0} signed out before it was done; 'bans' looks again once it's signed in", account),
		"addlicense" => new Said("adding the licences stopped - {0} signed out before it was done; 'addlicense' again once it's signed in adds the rest", account),
		"restart" => new Said("the restart stopped - {0} signed out before it was done; 'start {0}' signs it back in", account),
		"stop" => new Said("{0} signed out before it had finished up", account),
		_ => new Said("'{1}' stopped - {0} signed out before it was done", account, what)
	};

	private static List<Bot>? Pick(BotManager mgr, string[] args, out string? problem) {
		problem = null;

		if ((args.Length == 0) || args[0].Equals("all", StringComparison.OrdinalIgnoreCase)) {
			return [.. mgr.All];
		}

		if (mgr.Get(args[0]) is { } one) {
			return [one];
		}

		problem = NoSuchAccount(mgr, args[0]);

		return null;
	}

	private static async Task<string> NicknameAsync(BotManager mgr, string[] args, string line) {
		if (args.Length < 2) {
			return "nickname <account> <profile name>";
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		string name = Tail(line, 2);   // as typed - put back together from its words, a no-break space in it was a space

		// Steam's own limit for a profile name.
		if (name.Length > 32) {
			return $"That's {name.Length} characters - Steam allows 32 at most.";
		}

		return await bot.SetProfileNameAsync(name).ConfigureAwait(false) switch {
			null => $"{bot.Name}: not logged in",
			SteamKit2.EResult.OK => $"{bot.Name}: profile name is now \"{name}\"",
			_ => $"{bot.Name}: sent - Steam hasn't shown \"{name}\" yet, so check the profile in a minute"
		};
	}

	private static async Task<string> LevelAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		string[] lines = await Task.WhenAll(bots.Select(static async b => !b.IsOnline
			? $"{b.Name}: not logged in"
			: await b.GetLevelAsync().ConfigureAwait(false) is { } level ? $"{b.Name}: level {level}" : $"{b.Name}: Steam didn't say")).ConfigureAwait(false);

		return string.Join(Environment.NewLine, lines);
	}

	private static string Balance(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		return string.Join(Environment.NewLine, bots.Select(static b => {
			if (!b.IsOnline) {
				return $"{b.Name}: not logged in";
			}

			if (b.WalletCents is not { } cents) {
				return $"{b.Name}: no Steam wallet";
			}

			string money = (cents / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
			string pending = b.WalletPendingCents > 0 ? $" (+{(b.WalletPendingCents / 100.0).ToString("0.00", CultureInfo.InvariantCulture)} pending)" : "";

			return $"{b.Name}: {money} {b.WalletCurrency}{pending}";
		}));
	}

	private static async Task<string> PointsAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		string[] lines = await Task.WhenAll(bots.Select(static async b => !b.IsOnline
			? $"{b.Name}: not logged in"
			: await b.GetPointsAsync().ConfigureAwait(false) is { } points
				? $"{b.Name}: {points.ToString("N0", CultureInfo.InvariantCulture)} Steam points"
				: $"{b.Name}: Steam didn't say")).ConfigureAwait(false);

		return string.Join(Environment.NewLine, lines);
	}

	private static async Task<string> FairSwapCheckAsync(BotManager mgr, string[] args) {
		if ((args.Length < 2) || (mgr.Get(args[0]) is not { } bot) || !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong offerId)) {
			return "fairswap <account> <offerID>";
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return $"{bot.Name}: not logged in";
		}

		(bool? fair, Said why) = await FairSwap.CheckAsync(bot, offerId, CancellationToken.None).ConfigureAwait(false);

		return fair switch {
			true => $"{bot.Name}: offer {offerId} is a fair card swap",
			false => $"{bot.Name}: offer {offerId} is not a fair card swap - {why}",
			null => $"{bot.Name}: couldn't check offer {offerId} - {why}; try again in a minute"
		};
	}

	/// <summary>
	/// 'freegames': the free-games look, now - the same one "Claim free games" makes every half hour or so, with the same
	/// limits: Steam's activation window, a rate limit's pause, what it has stopped asking for, and on a human-mode account
	/// its day. One account at a time, so several accounts don't all add games in the same second.
	/// </summary>
	private static async Task<string> FreeGamesAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		string setting = Settings.FindBot(nameof(BotConfig.ClaimFree))?.Label ?? "Claim free games";

		async Task<string> Look(Bot b) {
			FreeGames free = BotManager.ModuleOf<FreeGames>(b)!;
			List<string> lines = [];

			// Off on this account: asked for, so it looks anyway - for games only, the least the setting would take.
			bool off = b.Cfg.ClaimFree == FreeClaims.Off;
			string howToTurnOn = $"To have it look by itself: Settings, {b.Name}, Free stuff, \"{setting}\" (or: set {b.Name} ClaimFree games).";
			string NotNow(Said holding) {
				lines.Add($"{b.Name}: not now - {holding}.{(off ? "" : " It looks by itself once it can.")}");

				if (off) {
					lines.Add($"{b.Name}: \"{setting}\" is off, so it doesn't look by itself. {howToTurnOn}");
				}

				return string.Join(Environment.NewLine, lines);
			}

			Said holding = free.Holding();

			if (!holding.IsEmpty) {
				return NotNow(holding);
			}

			FreeGames.Report report = new();

			try {
				await PicsWatch.PollAsync(b, CancellationToken.None).ConfigureAwait(false);
				await free.CheckAsync(true, CancellationToken.None, off ? FreeClaims.Games : null, report).ConfigureAwait(false);
			} catch (Exception e) when (!Log.IsCutOff(e)) {
				// Anything but signing out partway - that goes on to the slow command's own "signed out before it was done", not
				// "couldn't look".
				Log.Failed("freegames", e, b.Name);

				return $"{b.Name}: couldn't look - {Log.Scrub(e.Message)}";
			}

			// Shut by the time its turn came - behind a look that spaced the next ones out, say.
			if (!report.NotNow.IsEmpty) {
				return NotNow(report.NotNow);
			}

			lines.AddRange(FreeGamesSaid(b.Name, report));

			if (off) {
				lines.Add($"{b.Name}: \"{setting}\" is off, so this looked for games only. {howToTurnOn}");
			}

			return string.Join(Environment.NewLine, lines);
		}

		// Usually seconds, but each claim waits a little before the next, and several accounts go one after another - so
		// past the reply's wait it carries on and the log has the rest.
		return await SlowAsync(new Slow("freegames", "looking for free games"), bots, Look, ReplyWithin, oneAtATime: true,
			cannot: static b => !b.IsOnline || !b.Web.Ready || (BotManager.ModuleOf<FreeGames>(b) is null) ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
	}

	/// <summary>One account's look, in plain words: what it claimed, what was free but wasn't, and why - or that there's nothing.</summary>
	internal static List<string> FreeGamesSaid(string name, FreeGames.Report report) {
		List<string> lines = [];

		if (report.Claimed.Count > 0) {
			lines.Add($"{name}: claimed {string.Join(", ", report.Claimed)}");
		}

		if (report.Missed.Count > 0) {
			lines.Add($"{name}: free, but not claimed:");
			lines.AddRange(report.Missed.Select(static m => $"  {m.Name} - {m.Why}"));
		}

		if (!report.Stopped.IsEmpty) {
			lines.Add($"{name}: {report.Stopped}");
		}

		if (lines.Count == 0) {
			lines.Add(report.LibraryNotReady ? $"{name}: still reading its game library - try again in a minute"
				: report.Unanswered > 0 ? $"{name}: nothing claimed - the Steam store didn't answer about {report.Unanswered}, so it asks again next time"
				: $"{name}: nothing free right now");
		}

		return lines;
	}

	private static async Task<string> FreeItemsAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		static async Task<string> Look(Bot b) {
			EventItems events = BotManager.ModuleOf<EventItems>(b)!;
			bool sticker = await events.StickerAsync(CancellationToken.None).ConfigureAwait(false);
			int shop = await events.ShopAsync(CancellationToken.None).ConfigureAwait(false);

			return $"{b.Name}: {(sticker ? "claimed the sale item" : "no sale item to claim right now")} · {shop} free Points Shop item(s) taken";
		}

		// The first look after a start reads the whole Points Shop, which takes minutes - long enough that a
		// console just sitting there looks hung. So that one answers at once and says so; the answer lands in the log.
		bool known = EventItems.ShopKnown;
		Slow job = new("freeitems", "looking for free event items", known ? "" : "the first look after a start reads the whole Points Shop, so a few minutes");

		return await SlowAsync(job, bots, Look, known ? ReplyWithin : TimeSpan.Zero, oneAtATime: true,
			cannot: static b => !b.IsOnline || !b.Web.Ready || (BotManager.ModuleOf<EventItems>(b) is null) ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
	}

	private static async Task<string> SellAsync(BotManager mgr, string[] args) {
		if (args.Length < 1) {
			return "sell <account> [preview|do|relist] [count]";
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return $"{bot.Name}: not logged in";
		}

		string what = args.Length > 1 ? args[1].ToLowerInvariant() : "preview";
		int count = (args.Length > 2) && int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 25) : Math.Clamp(bot.Cfg.SellPerRun, 1, 25);

		// Listing waits half a minute or so between cards, as a person working the market page would - five of them is a few
		// minutes - and taking listings down a few seconds each. The reply says it's going; the log, what it did.
		// By what it does: a preview or a relist while 'sell do' lists cards is another job, not "already listing".
		string doing = what is "do" or "relist" ? what : "preview";
		Slow job = new("sell", doing switch {
			"do" => "listing spare cards on the market",
			"relist" => "taking down stale listings",
			_ => "working out what it would sell"
		}, Key: "sell " + doing);

		return await SlowAsync(job, [bot], b => SellNowAsync(b, what, count), ReplyWithin).ConfigureAwait(false);
	}

	private static async Task<string> SellNowAsync(Bot bot, string what, int count) {
		if (what == "relist") {
			(int removed, int stale) = await Seller.RelistAsync(bot, CancellationToken.None).ConfigureAwait(false);

			return stale == 0 ? $"{bot.Name}: no week-old listing the market has gone under." : $"{bot.Name}: took down {removed} of {stale} stale listing(s) - the next sell lists them again at today's price.";
		}

		Seller.Plan plan = await Seller.PlanAsync(bot, count, CancellationToken.None).ConfigureAwait(false);

		if (plan.Problem != null) {
			return $"{bot.Name}: {plan.Problem}";
		}

		if (plan.Offers.Count == 0) {
			return plan.Duplicates == 0 ? $"{bot.Name}: no spare cards - every copy is still useful for a badge." : $"{bot.Name}: {plan.Duplicates} spare card(s), but none with a price to go under yet.";
		}

		if (what == "do") {
			return await Seller.SellAsync(bot, plan.Offers, CancellationToken.None).ConfigureAwait(false);
		}

		StringBuilder sb = new();
		sb.AppendLine($"{bot.Name}: {plan.Duplicates} spare card(s){(plan.Unpriced > 0 ? $" ({plan.Unpriced} not priced yet)" : "")} - the {plan.Offers.Count} it would list next:");

		foreach (Seller.Offer o in plan.Offers) {
			sb.AppendLine($"  {Columns.Fit(o.Game, 24)} {Columns.PadRight(o.Card, 24)} lowest {Seller.Money(o.LowestCents, bot)} -> list at {Seller.Money(o.BuyerCents, bot)}, you get {Seller.Money(o.YouGetCents, bot)}");
		}

		List<Seller.Listing>? up = await Seller.ListingsAsync(bot, CancellationToken.None).ConfigureAwait(false);

		if (up != null) {
			sb.AppendLine($"  already on the market: {up.Count(static l => !l.AwaitingConfirmation)} listing(s), {up.Count(static l => l.AwaitingConfirmation)} waiting on a confirmation");
		}

		sb.Append($"  'sell {bot.Name} do' lists them{(bot.CanConfirmTrades ? " and confirms them on its authenticator" : " - then confirm them in the Steam app")}.");

		return sb.ToString();
	}

	private static async Task<string> QueueAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		// A few seconds on each game, a dozen of them: about four minutes, every time. So it answers at once and the log says
		// when it's through - waited on, the dashboard gave up at two minutes and said "timed out".
		return await SlowAsync(new Slow("queue", "going through today's discovery queue", "about 4 minutes"), bots, static async b => {
			int seen = await BotManager.ModuleOf<EventItems>(b)!.QueueAsync(CancellationToken.None).ConfigureAwait(false);

			return seen < 0 ? $"{b.Name}: Steam wouldn't hand out the queue right now" : $"{b.Name}: looked through {seen} game(s) in the discovery queue";
		}, TimeSpan.Zero, cannot: static b => !b.IsOnline || (BotManager.ModuleOf<EventItems>(b) is null) ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
	}

	private static async Task<string> BoosterAsync(BotManager mgr, string[] args) {
		// "booster all 730" used to list every account and quietly drop the appIDs - so it looked like it had
		// done something it never tried. Packs cost gems per account, so making them is one account at a time.
		if ((args.Length > 1) && args[0].Equals("all", StringComparison.OrdinalIgnoreCase)) {
			return "Booster packs are made one account at a time:  booster <account> <appIDs>. 'booster all' on its own lists every account's gems and games.";
		}

		// booster <account> <appIDs> - make them now
		if ((args.Length > 1) && (mgr.Get(args[0]) is { } maker)) {
			if (!maker.IsOnline || !maker.Web.Ready) {
				return $"{maker.Name}: not logged in";
			}

			// A pause of a few seconds after each pack, as a person clicking through the creator would: a list of games
			// outlasts the reply, and the rest goes to the log.
			// One batch at a time an account, whatever games: each reads the gems once and spends from that, so two at once
			// spent the same gems twice - and '440 730' with '730' made 730's pack in one and had it "refused by Steam" in the
			// other. The "already" says which games are being made.
			string games = string.Join(", ", args[1..].SelectMany(static a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				.Distinct(StringComparer.Ordinal));

			return await SlowAsync(new Slow("booster", $"making booster packs for {games}"), [maker], b => MakeBoostersAsync(b, args[1..]), ReplyWithin).ConfigureAwait(false);
		}

		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		List<string> lines = [];

		foreach (Bot b in bots) {
			if (!b.IsOnline || !b.Web.Ready) {
				lines.Add($"{b.Name}: not logged in");

				continue;
			}

			if (await Boosters.ReadAsync(b).ConfigureAwait(false) is not { } page) {
				lines.Add($"{b.Name}: couldn't read the booster creator");

				continue;
			}

			List<Boosters.Offer> ready = [.. page.Offers.Values.Where(o => !o.Unavailable && (o.Price <= page.Gems)).OrderBy(static o => o.Price)];
			lines.Add($"{b.Name}: {page.Gems.ToString("N0", CultureInfo.InvariantCulture)} gems ({page.TradableGems.ToString("N0", CultureInfo.InvariantCulture)} tradable) · {page.Offers.Count} game(s) on the booster creator, {ready.Count} affordable now");

			foreach (uint appId in Boosters.Games(b)) {
				lines.Add(page.Offers.TryGetValue(appId, out Boosters.Offer? o)
					? o.Unavailable ? $"  {o.Name} ({appId}): made - again {o.AvailableAt}" : $"  {o.Name} ({appId}): ready, {o.Price} gems"
					: $"  {GameNames.Of(appId)} ({appId}): not on the booster creator");
			}

			if ((Boosters.Games(b).Count == 0) && (ready.Count > 0)) {
				lines.Add("  cheapest: " + string.Join(", ", ready.Take(5).Select(static o => $"{o.Name} ({o.AppId}) {o.Price}")));
			}
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>'booster &lt;account&gt; &lt;appIDs&gt;': make those packs now, each from the gems the one before left.</summary>
	private static async Task<string> MakeBoostersAsync(Bot maker, string[] ids) {
		if (await Boosters.ReadAsync(maker).ConfigureAwait(false) is not { } page) {
			return $"{maker.Name}: couldn't read the booster creator";
		}

		List<string> said = [];
		uint tradable = page.TradableGems, untradable = page.UntradableGems, gems = page.Gems;

		// "730,440" as well as "730 440" - a comma list came back as one bad appID.
		foreach (string arg in ids.SelectMany(static a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))) {
			if (!uint.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out uint appId) || (appId == 0)) {
				said.Add($"'{arg}' isn't an appID");

				continue;
			}

			if (!page.Offers.TryGetValue(appId, out Boosters.Offer? offer)) {
				said.Add($"{GameNames.Of(appId)}: not on the booster creator - no card drops left in it here, or no cards at all");
			} else if (offer.Unavailable) {
				said.Add($"{offer.Name}: made recently - available again {offer.AvailableAt}");
			} else if (gems < offer.Price) {
				said.Add($"{offer.Name}: needs {offer.Price} gems, {gems} here");
			} else {
				(bool made, uint left, uint leftTradable, uint leftUntradable, string? why) = await Boosters.CreateAsync(maker, offer, tradable, untradable).ConfigureAwait(false);

				if (made) {
					(gems, tradable, untradable) = (left, leftTradable, leftUntradable);
					said.Add($"{offer.Name}: made a booster pack for {offer.Price} gems - {gems} left");
				} else {
					said.Add($"{offer.Name}: Steam refused it {why}");
				}
			}
		}

		return $"{maker.Name}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", said);
	}

	private static string PrivacyWord(int level) => level switch { 1 => "private", 2 => "friends", 3 => "public", _ => $"?{level}" };

	// Comments use their own numbering on Steam's side: 0 friends only, 1 public, 2 private.
	private static string CommentWord(int permission) => permission switch { 0 => "friends", 1 => "public", 2 => "private", _ => $"?{permission}" };

	private static int? PrivacyLevel(string word) => word.ToLowerInvariant() switch {
		"private" or "off" or "1" => 1,
		"friends" or "friendsonly" or "friends-only" or "2" => 2,
		"public" or "everyone" or "3" => 3,
		_ => null
	};

	private static int? CommentLevel(string word) => PrivacyLevel(word) switch { 1 => 2, 2 => 0, 3 => 1, _ => null };

	private static async Task<string> PrivacyAsync(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return "privacy <account> [public|friends|private|part=level ...]";
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return $"{bot.Name}: not logged in";
		}

		Privacy.Settings? now = await Privacy.ReadAsync(bot).ConfigureAwait(false);

		if (now == null) {
			return $"{bot.Name}: couldn't read the privacy settings from Steam";
		}

		static string Show(Privacy.Settings p) =>
			$"profile {PrivacyWord(p.Profile)} · games {PrivacyWord(p.Games)} · playtime {PrivacyWord(p.Playtime)} · friends {PrivacyWord(p.Friends)}"
			+ $" · inventory {PrivacyWord(p.Inventory)} · gifts {PrivacyWord(p.Gifts)} · comments {CommentWord(p.Comments)}";

		if (args.Length == 1) {
			return $"{bot.Name}: {Show(now)}";
		}

		Privacy.Settings next = now;

		foreach (string arg in args[1..]) {
			int eq = arg.IndexOf('=');

			// One word on its own sets everything.
			if (eq < 0) {
				if (PrivacyLevel(arg) is not { } all) {
					return $"'{arg}' isn't public, friends or private.";
				}

				next = new Privacy.Settings(all, all, all, all, all, all, CommentLevel(arg)!.Value);

				continue;
			}

			string part = arg[..eq].ToLowerInvariant();
			string value = arg[(eq + 1)..];

			if (part is "comments" or "comment") {
				next = next with { Comments = CommentLevel(value) ?? -1 };
			} else if (PrivacyLevel(value) is { } level) {
				next = part switch {
					"profile" => next with { Profile = level },
					"games" or "ownedgames" => next with { Games = level },
					"playtime" => next with { Playtime = level },
					"friends" or "friendslist" => next with { Friends = level },
					"inventory" => next with { Inventory = level },
					"gifts" or "inventorygifts" => next with { Gifts = level },
					_ => null!
				};

				if (next == null) {
					return $"'{part}' isn't a privacy part. Parts: profile, games, playtime, friends, inventory, gifts, comments.";
				}
			} else {
				return $"'{value}' isn't public, friends or private.";
			}

			if (next.Comments < 0) {
				return $"'{value}' isn't public, friends or private.";
			}
		}

		if (next == now) {
			return $"{bot.Name}: already {Show(now)}";
		}

		if (!await Privacy.WriteAsync(bot, next).ConfigureAwait(false)) {
			return $"{bot.Name}: Steam didn't accept the change";
		}

		// Read it back rather than trust the reply, the same way licences are checked by whether they arrive.
		Privacy.Settings? saved = await Privacy.ReadAsync(bot).ConfigureAwait(false);

		return saved == next ? $"{bot.Name}: {Show(saved)}" : $"{bot.Name}: sent, but Steam now shows {(saved == null ? "nothing readable" : Show(saved))}";
	}

	private static string Cards(BotManager mgr, string[] args) {
		IEnumerable<Bot> bots;

		if (args.Length > 0) {
			Bot? one = mgr.Get(args[0]);

			if (one == null) {
				return NoSuchAccount(mgr, args[0]);
			}

			bots = [one];
		} else {
			bots = mgr.All;
		}

		StringBuilder sb = new();

		foreach (Bot bot in bots) {
			CardFarmer? farmer = BotManager.ModuleOf<CardFarmer>(bot);

			if (farmer == null) {
				continue;
			}

			sb.AppendLine($"{bot.Name}: {farmer.Status}");

			if (farmer.Queue.Count > 0) {
				// Farming time, not a clock time: sittings, a farming window or waiting for the night stretch it.
				(double pace, bool learned) = farmer.MinutesPerCard;
				sb.AppendLine($"    about {Fmt.Rough(farmer.EstimateMinutes)} of farming left - {pace:0}m a card, "
					+ (learned ? "this account's pace so far" : "Steam's usual rate until a drop has been timed"));
			}

			foreach (FarmTarget g in farmer.Queue.Take(15)) {
				sb.AppendLine($"    {g.CardsRemaining,3} card(s)  {g.HoursPlayed,6:0.0}h  {g.GameName}");
			}
		}

		return sb.Length == 0 ? "Nothing is farming." : sb.ToString().TrimEnd();
	}

	private static string Mini(string[] args) {
		if ((Window == null) || !OperatingSystem.IsWindows()) {
			return "There's no app window in this run - mini mode is part of it.";
		}

		string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";
		bool on = arg switch { "on" => true, "off" => false, _ => !Window.Mini };
		Window.SetMiniMode(on);

		return on ? "Mini mode on - 'mini off' or its full-window button brings the full window back." : "Back to the full window.";
	}

	// ── rep4rep ─────────────────────────────────────────────────────────────
	private static async Task<string> Rep4RepAsync(BotManager mgr, string[] args) {
		string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
		string? who = args.Length > 1 ? args[1] : null;

		switch (sub) {
			case "points": {
				if (!mgr.Rep4Rep.HasToken) {
					return "No rep4rep API token set. Get one from rep4rep.com under Settings, then:  set Rep4RepApiToken <token>";
				}

				(int Points, int PendingPoints)? user = await mgr.Rep4Rep.GetUserAsync().ConfigureAwait(false);

				if (user is not { } got) {
					return "rep4rep didn't answer. Check the token is right and that you can reach rep4rep.com.";
				}

				// rep4rep really does answer "-107 still being verified" now and then - the dashboard's own check, and its
				// last sensible figure when there is one, rather than the nonsense as if it were true.
				if (!Web.WebHost.Believable(got)) {
					return $"rep4rep gave an impossible balance ({got.Points} points, {got.PendingPoints} still being verified)"
						+ (Web.WebHost.Current?.LastSensiblePoints is { } kept
							? $" - the last sensible one was {kept.Points} points you can spend, {kept.PendingPoints} still being verified."
							: " - nothing sensible from it yet; try again in a while.");
				}

				Web.WebHost.Current?.RememberPoints(got.Points, got.PendingPoints);

				return $"rep4rep: {got.Points} points you can spend, {got.PendingPoints} still being verified.";
			}

			case "profiles": {
				if (!mgr.Rep4Rep.HasToken) {
					return "No rep4rep API token set.";
				}

				List<(string Id, string SteamId)> profiles = await mgr.Rep4Rep.GetProfilesAsync().ConfigureAwait(false);

				if (profiles.Count == 0) {
					return "rep4rep has no Steam profiles registered yet. They get added automatically the first time each account logs in.";
				}

				StringBuilder sb = new();

				foreach ((string id, string steamId) in profiles) {
					Bot? owner = mgr.All.FirstOrDefault(b => b.SteamId.ToString() == steamId);
					sb.AppendLine($"  {steamId}  rep4rep id {id}  {(owner != null ? owner.Name : "(not one of yours)")}");
				}

				return sb.ToString().TrimEnd();
			}

			case "tasks": {
				if (who == null) {
					return "rep4rep tasks <account>";
				}

				Bot? bot = mgr.Get(who);

				if (bot == null) {
					return NoSuchAccount(mgr, who);
				}

				string? profileId = await mgr.Rep4Rep.ResolveProfileIdAsync(bot.SteamId, false).ConfigureAwait(false);

				if (profileId == null) {
					return $"{bot.Name} isn't registered with rep4rep yet.";
				}

				List<Rep4RepTask> tasks = await mgr.Rep4Rep.GetTasksAsync(profileId).ConfigureAwait(false);

				if (tasks.Count == 0) {
					return $"No tasks waiting for {bot.Name} right now. rep4rep hands them out in batches - check back later.";
				}

				StringBuilder sb = new();
				sb.AppendLine($"{tasks.Count} task(s) waiting for {bot.Name}:");

				foreach (Rep4RepTask t in tasks.Take(15)) {
					sb.AppendLine($"  {Columns.PadRight(t.TargetName, 24)} \"{t.CommentText}\"");
				}

				return sb.ToString().TrimEnd();
			}

			case "status": {
				StringBuilder sb = new();
				sb.AppendLine(mgr.Rep4Rep.HasToken ? "token: set" : "token: MISSING - set Rep4RepApiToken <token>");

				foreach (Bot bot in mgr.All) {
					Rep4RepModule? m = BotManager.ModuleOf<Rep4RepModule>(bot);

					if (m == null) {
						continue;
					}

					string last = m.LastPost == null ? "never" : Fmt.Ago(m.LastPost) + " ago";
					string steam = m.CapIsSteamLimit ? " (Steam's)" : "";
					string frees = (m.PostsToday >= m.Cap) && m.NextSlot is { } ns ? $"   frees {ns.ToLocalTime():HH:mm}" : "";
					sb.AppendLine($"{bot.Name,-14}{(bot.Cfg.Rep4Rep ? "on " : "off")}  {m.PostsToday}/{m.Cap}{steam} in 24h   last {last,-10} {m.Status}{frees}");
				}

				return sb.ToString().TrimEnd();
			}
		}

		// Fan a per-account action out over every account with one word.
		if ((who != null) && who.Equals("all", StringComparison.OrdinalIgnoreCase)
			&& sub is "rest" or "clear" or "pause" or "resume" or "now") {
			int n = 0;

			foreach (Bot b in mgr.All) {
				Rep4RepModule? mm = BotManager.ModuleOf<Rep4RepModule>(b);

				// Only accounts that comment at all - resting one with rep4rep off would block it for a day the
				// moment it was switched on.
				if ((mm == null) || !b.Cfg.Rep4Rep) {
					continue;
				}

				// Counted only where it actually happened - clear and rest do nothing on an account whose rep4rep
				// state never loaded, and counting those anyway was a lie.
				bool did = true;

				switch (sub) {
					case "rest": did = await mm.RestFullDayAsync("manual reset").ConfigureAwait(false); break;
					case "clear": did = await mm.ClearHoldAsync().ConfigureAwait(false); break;
					case "pause": mm.Paused = true; break;
					case "resume": mm.Paused = false; break;
					case "now": mm.RunNow(); break;
				}

				if (did) {
					n++;
				}
			}

			return $"rep4rep {sub}: {n} account(s)";
		}

		// A word that isn't one of these is a typo, not a subcommand waiting for an account - answering
		// "rep4rep typo <account>" presented the typo as if it were real.
		const string Usage = "rep4rep status | points | profiles | tasks <account> | now/pause/resume/clear/rest <account|all>\n"
			+ "  to switch it on or off for an account:  set <account> Rep4Rep on|off";

		if (sub is not ("now" or "pause" or "resume" or "clear" or "rest")) {
			return Usage;
		}

		if (who == null) {
			return $"rep4rep {sub} <account|all>";
		}

		Bot? target = mgr.Get(who);

		if (target == null) {
			return NoSuchAccount(mgr, who);
		}

		Rep4RepModule? mod = BotManager.ModuleOf<Rep4RepModule>(target);

		switch (sub) {
			case "now":
				mod?.RunNow();

				if (mod is { } m2 && (m2.PostsToday >= m2.Cap) && m2.NextSlot is { } slot) {
					return $"{target.Name}: at its cap ({m2.PostsToday}/{m2.Cap}) - the next slot frees at {slot.ToLocalTime():HH:mm}, it'll post then.";
				}

				return $"{target.Name}: queued - it'll post at the next gap.";
			case "pause":
				if (mod != null) {
					mod.Paused = true;
				}

				return $"{target.Name}: rep4rep paused";
			case "resume":
				if (mod != null) {
					mod.Paused = false;
				}

				return $"{target.Name}: rep4rep resumed";
			case "clear":
				return (mod != null) && await mod.ClearHoldAsync().ConfigureAwait(false)
					? $"{target.Name}: hold cleared, refused profiles forgotten"
					: $"{target.Name}: nothing to clear - rep4rep isn't running on this account";
			case "rest":
				return (mod != null) && await mod.RestFullDayAsync("manual reset").ConfigureAwait(false)
					? $"{target.Name}: rep4rep resting a full day, back at baseline after"
					: $"{target.Name}: rep4rep isn't running on this account, so there's nothing to rest";
			default:
				return Usage;
		}
	}

	// ── settings ────────────────────────────────────────────────────────────
	private static string ShowConfig(BotManager mgr, string[] args) {
		bool showAdvanced = args.Contains("all", StringComparer.OrdinalIgnoreCase);
		string[] positional = args.Where(a => !a.Equals("all", StringComparison.OrdinalIgnoreCase)).ToArray();

		object config;
		IReadOnlyList<SettingDef> defs;
		string title;

		if (positional.Length == 0) {
			config = mgr.Global;
			defs = Settings.Global;
			title = $"GLOBAL   {ConfigStore.GlobalPath}";
		} else {
			Bot? bot = mgr.Get(positional[0]);

			if (bot == null) {
				return NoSuchAccount(mgr, positional[0]);
			}

			config = bot.Cfg;
			defs = Settings.Bot;
			title = $"{bot.Name}   config/{bot.Name}.json";
		}

		StringBuilder sb = new();
		sb.AppendLine(title);
		int hidden = 0;

		// As wide as the longest name: a fixed 28 ran FriendRequestDelayMinMinutes straight into its value.
		int width = defs.Max(static d => d.Name.Length) + 2;

		foreach (string section in defs.Select(static d => d.Section).Distinct()) {
			List<SettingDef> shown = defs.Where(d => (d.Section == section) && (showAdvanced || !d.Advanced)).ToList();
			hidden += defs.Count(d => (d.Section == section) && d.Advanced && !showAdvanced);

			if (shown.Count == 0) {
				continue;
			}

			sb.AppendLine();
			sb.AppendLine($"  {section.ToUpperInvariant()}");

			foreach (SettingDef def in shown) {
				// A proxy without the password typed into it (Settings.Shown): this goes to the chats and the log as well.
				sb.AppendLine($"    {def.Name.PadRight(width)}{Settings.Shown(config, def)}");
			}
		}

		if (hidden > 0) {
			sb.AppendLine();
			sb.AppendLine($"  {hidden} advanced setting(s) hidden - 'config{(positional.Length > 0 ? " " + positional[0] : "")} all' shows them.");
		}

		sb.Append("  'help <setting>' explains any of these.");

		return sb.ToString();
	}

	/// <summary>
	/// Drop one matching pair of quotes from around a value typed at the console.
	///
	/// The command line is split on spaces with no notion of quoting, so a value written the way the help, the
	/// tutorial and the README all show it - set acct GameWeights "730:70, 440:20" - arrived with the quote
	/// characters still attached to the first and last words. For most settings that is a visible mess; for a
	/// list it was worse than that, because the quote made only the FIRST and LAST entries unparseable and the
	/// middle ones came through fine. A four-game spread silently became a two-game one with a different main
	/// game, and nothing reported an error.
	/// </summary>
	private static string Unquote(string value) {
		string trimmed = value.Trim();

		return (trimmed.Length >= 2) && (trimmed[0] == trimmed[^1]) && (trimmed[0] is '"' or '\'')
			? trimmed[1..^1]
			: value;
	}

	/// <summary>
	/// A key typed in quotes with its value stuck on inside them - '"SteamPassword:x"': what is the value, and where do the
	/// quotes end? Asked for again with a space rather than saved as 'x"'.
	/// </summary>
	private static bool QuotedKey(string word) => word.Length > 0 && (word[0] is '"' or '\'' or '`' or '“' or '‘');

	private static string Set(BotManager mgr, string[] args, string line) {
		const string Usage = "set <key> <value>            change a global setting\nset <account> <key> <value>  change one account's setting";

		if (args.Length == 0) {
			return Usage;
		}

		// The key read as the log's masking reads it - quotes off, cut at '=' or ':' (KeyOf) - and its value after a space or
		// stuck on with '=' (SetValue): 'set alt Rep4Rep=on' answered "there's no setting called 'alt'", and 'set alt "Rep4Rep" on'
		// that there was no Rep4Rep. A quoted key with a value stuck on inside the quotes is asked for again, with a space.
		string[] parts = Words(line);
		Bot? bot = mgr.Get(args[0]);

		// 'set <account> <key> <value>' when the first word really is an account and a key follows.
		if ((bot != null) && (args.Length >= 2)) {
			(string key, bool joined) = KeyOf(args[1]);

			if (Settings.FindBot(key) is not { } def) {
				// What was typed isn't said back: a password pasted in the key's place is as likely as a name misspelt. Only a real
				// setting's name - one of the global ones - is.
				return Settings.FindGlobal(key) is { } global
					? $"'{global.Name}' is a global setting, not one account's. Try:  set {global.Name} <value>"
					: $"That's not a per-account setting - 'config {bot.Name}' lists them all.";
			}

			if (joined && QuotedKey(args[1])) {
				return $"Use a space: set {bot.Name} {def.Name} <value>";
			}

			string value = SetValue(line, parts, 2);

			// 'set kylro Rep4Rep' with the value left off answered "there's no setting called 'kylro'".
			if (value.Length == 0) {
				return new Said("set {0} {1} <value> - the value is missing. It's {2} now.", bot.Name, def.Name, Settings.Shown(bot.Cfg, def)).ToString();
			}

			List<string> pulled;

			// The change and its save under the account's lock: a dashboard save of it at the same moment put its older copy
			// back over this one, in memory and on disk.
			lock (bot.CfgGate) {
				bool wasLegit = bot.Cfg.LegitMode;
				string? error = Settings.Apply(bot.Cfg, def, Unquote(value));

				if (error != null) {
					return error;
				}

				Settings.ApplyLegitMode(bot.Cfg, wasLegit);

				// Raising a "shortest" above its "longest" (or the reverse) used to be accepted and written to disk.
				// The dashboard fixed one such pair; this fixes all of them, on both paths.
				pulled = [.. Settings.FixRanges(bot.Cfg, def.Name).Select(static s => s.ToEnglish())];

				ConfigStore.SaveBot(bot.Name, bot.Cfg);
			}

			ApplyBotSideEffects(bot, def);

			// The value as 'config' shows it: a proxy without the password typed into it.
			return $"{bot.Name}.{def.Name} = {Settings.Shown(bot.Cfg, def)}"
				+ (def.NeedsRestart ? "   (applies after a restart)" : "")
				+ (pulled.Count > 0 ? Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", pulled) : "");
		}

		// 'set <account>' alone.
		if (bot != null) {
			return Usage;
		}

		(string globalKey, bool globalJoined) = KeyOf(args[0]);
		SettingDef? globalDef = Settings.FindGlobal(globalKey);

		// What was typed isn't said back here either - 'set hunter2' answered "There's no setting called 'hunter2'", on screen,
		// in the chats and in the log. Only a real setting's name is.
		if (globalDef == null) {
			SettingDef? asBot = Settings.FindBot(globalKey);

			return asBot != null
				? $"'{asBot.Name}' is a per-account setting. Try:  set <account> {asBot.Name} <value>"
				: "That's not a setting - 'config' lists the global ones, 'config <account>' the per-account ones.";
		}

		if (globalJoined && QuotedKey(args[0])) {
			return $"Use a space: set {globalDef.Name} <value>";
		}

		string globalValue = SetValue(line, parts, 1);

		if (globalValue.Length == 0) {
			return Usage;
		}

		string passwordBefore;

		// Under the lock every change to the live settings takes: 'reload' or a restore swapping them in the meantime took
		// this change away with the settings it was made on.
		lock (ConfigStore.GlobalEditGate) {
			passwordBefore = mgr.Global.WebPassword;
			string? failure = Settings.Apply(mgr.Global, globalDef, Unquote(globalValue));

			if (failure != null) {
				return failure;
			}

			ConfigStore.SaveGlobal(mgr.Global);
			mgr.ApplyGlobal(mgr.Global);
		}

		ApplyGlobalSideEffects(mgr, globalDef);

		// A new password ends every browser's session, like the dashboard's own save. Only when it really changed.
		if (!string.Equals(passwordBefore, mgr.Global.WebPassword, StringComparison.Ordinal)) {
			Web.WebHost.Current?.SignOutAll();
		}

		return $"{globalDef.Name} = {Settings.Shown(mgr.Global, globalDef)}"
			+ (globalDef.NeedsRestart && (globalDef.Name != "WebPassword") ? "   (applies after a restart)" : "");
	}

	/// <summary>Make a changed setting take effect now, where it can.</summary>
	public static void ApplyBotSideEffects(Bot bot, SettingDef def) {
		switch (def.Name) {
			// The name and the switch that turns it on are the same change as far as Steam is concerned. Only the
			// name was here, so turning the custom name OFF and back ON left the account showing the real game
			// until something else happened to re-assert - the config said one thing and the friends list showed
			// another, for as long as nobody looked.
			case "CustomGameNameEnabled":
			case "CustomGameName":
			case "IdleGames":
			case "PlayWhileFarming":
			case "BlacklistedGames":
			case "FarmOffline":
				BotManager.ModuleOf<Idler>(bot)?.Assert();

				break;
			case "OnlineStatus":
			case "GameDevice":
				bot.ApplyPersona();

				break;
			// Only ever START. The module's loop stays alive while it is off and reads the flag itself, so stopping
			// it here would kill the very loop that has to notice the flag being turned back on later.
			case "Rep4Rep":
				if (bot.Cfg.Rep4Rep && bot.IsOnline) {
					_ = BotManager.ModuleOf<Rep4RepModule>(bot)?.StartAsync();
				}

				break;
			// A gift left for the owner while its switch was off is taken once the switch is on - now, not at the
			// gifts module's next slow pass hours later.
			case "AcceptGifts":
			case "AcceptGiftedGames":
				BotManager.ModuleOf<Gifts>(bot)?.LookAgain();

				break;
			case "Enabled":
				// "Disabled" has to actually stop it. It used to keep farming and commenting while the dashboard
				// said disabled, which is the worst kind of wrong.
				// Not running rather than Stopped: an account whose sign-in gave up (three wrong passwords, say) is Failed,
				// and 'enable' answered "logging in" and left it there.
				if (!bot.Cfg.Enabled) {
					Background.Run("couldn't stop", () => bot.StopAsync(), bot.Name);
				} else if (!bot.Running) {
					Background.Run("couldn't start", bot.StartAsync, bot.Name);
				}

				break;
		}
	}

	public static void ApplyGlobalSideEffects(BotManager mgr, SettingDef def) {
		// Not the password: the dashboard runs these for EVERY setting on every save, and signing everybody out here
		// sent the person saving back to the password screen each time. A changed password is handled where it's set.
		switch (def.Name) {
			case "WebRemoteAccess":
				RemoteAccess.Poke();

				break;
			case "CountMeAsUser":
				UserCount.Nudge();

				break;
			case "MiniOnTop":
				if (OperatingSystem.IsWindows()) {
					Window?.RefreshOnTop();
				}

				break;
			// The status text this program writes about itself is translated too, so a language change has to
			// reach the pack the modules read from - not only the one the browser fetches.
			// "Hold for N days" is turned into a deadline exactly once, here, when the number changes.
			//
			// Counting days down at runtime would restart the count on every launch, so a two-day hold on a
			// machine that reboots nightly would never end. A stored deadline cannot drift: it either has
			// passed or it has not.
			case "Rep4RepPauseHours": {
				int hours = Live.Global.Rep4RepPauseHours;

				if (hours <= 0) {
					bool lifted = false;

					lock (ConfigStore.GlobalEditGate) {
						if (Live.Global.Rep4RepHoldUntil != null) {
							Live.Global.Rep4RepHoldUntil = null;
							Live.Global.Rep4RepHoldFrom = null;
							ConfigStore.SaveGlobal(Live.Global);
							lifted = true;
						}
					}

					if (lifted) {
						Log.Info("rep4rep hold lifted - commenting resumes");
					}

					break;
				}

				// Anchored to when the hold STARTED, not to now.
				//
				// This runs on every global save, so "now + days" moved the finish line every time any unrelated
				// setting was touched - a two-day hold plus a theme change an hour later became two days from
				// then. From a fixed start, saving the same number is a no-op and changing it moves only the end.
				DateTime from = Live.Global.Rep4RepHoldFrom ?? DateTime.UtcNow;
				DateTime until = from.AddHours(hours);

				if ((Live.Global.Rep4RepHoldFrom != from) || (Live.Global.Rep4RepHoldUntil != until)) {
					Live.Global.Rep4RepHoldFrom = from;
					Live.Global.Rep4RepHoldUntil = until;
					ConfigStore.SaveGlobal(Live.Global);
					Log.Info(new Said("rep4rep commenting held for {0}h - back on {1}",
						hours, (Func<string>) (() => Fmt.Clock(until))));
				}

				break;
			}

			case "Language":
				Core.Loc.Refresh();

				// And repaint. Every row on screen can now render in the new language, but neither surface
				// redraws on its own - so without this the change did not appear until the next log line
				// happened to arrive, which on a quiet night is minutes of a window that looks broken.
				if (OperatingSystem.IsWindows()) {
					Window?.Invalidate();
				}

				Board?.Repaint();

				break;
			case "FileLogging":
			case "Debug":
			case "LogRetentionDays":
				Log.Configure(mgr.Global.FileLogging, mgr.Global.Debug, ConfigStore.Root, mgr.Global.LogRetentionDays);

				break;
			case "StartWithWindows":
				if (OperatingSystem.IsWindows()) {
					Windows.WindowsIntegration.SetStartWithWindows(mgr.Global.StartWithWindows);
				}

				break;
			case "KeepAwake":
				if (OperatingSystem.IsWindows()) {
					Windows.WindowsIntegration.KeepAwake(mgr.Global.KeepAwake);
				}

				break;
			case "MinimizeToTray":
				TrayHook?.Invoke(mgr.Global.MinimizeToTray);

				break;
		}
	}

	private static async Task<string> ImportAsync(BotManager mgr, string[] args, string line) {
		string tools = string.Join("|", IdlerImport.Tools.Select(static t => t.Id)) + "|" + IdlerImport.Auto;

		if ((args.Length == 0) || ((IdlerImport.Find(args[0]) == null) && !args[0].Equals(IdlerImport.Auto, StringComparison.OrdinalIgnoreCase))) {
			return $"import <{tools}> [path] [force]\n"
				+ string.Join("\n", IdlerImport.Tools.Select(static t => $"  {t.Id,-13} {t.Name}")) + "\n"
				+ $"  {IdlerImport.Auto,-13} work out which idler a folder belongs to (needs the path)\n"
				+ "  Leave the path out and nocat.farm looks in that idler's usual places.\n"
				+ "  Add 'force' to overwrite accounts that already exist here.\n"
				+ "  The dashboard (Accounts > Import from another idler) shows what it found before anything is written.";
		}

		bool force = args.Any(static a => a.Equals("force", StringComparison.OrdinalIgnoreCase));
		// The path as typed, from the line - only a 'force' of its own taken out. Put back together from the line's words, a
		// no-break space in a folder's name was a space, and the folder wasn't found. A line break or a tab is a gap, as it
		// is to 'force' above (no folder's name has one): a Telegram message with 'force' on its own line kept the path as
		// "C:\ASF" + a line break + "force".
		string rest = string.Join(' ', Tail(line, 2).Split([' ', '\t', '\r', '\n']).Where(static w => !w.Trim().Equals("force", StringComparison.OrdinalIgnoreCase))).Trim().Trim('"');
		string? path = rest.Length > 0 ? rest : null;

		if ((path != null) && !Directory.Exists(path) && !File.Exists(path)) {
			return $"There's no folder at {path}";
		}

		ImportScan scan = IdlerImport.Scan(args[0], path);

		if (!scan.Found) {
			return $"Nothing to import was found. Looked in:\n  {string.Join("\n  ", scan.Looked)}"
				+ string.Concat(scan.Notes.Select(static n => "\n  " + n))
				// The example in the shape of a path on this machine: a C:\ one was no help on Linux, a Mac or in Docker.
				+ $"\nPoint at it directly:  import {args[0]} {(OperatingSystem.IsWindows() ? @"C:\path\to\it" : "/path/to/it")}";
		}

		if ((scan.Accounts.Count == 0) && (scan.Settings.Count == 0)) {
			return $"Nothing to import in {scan.Path}";
		}

		IdlerImport.Outcome outcome = IdlerImport.Apply(scan, IdlerImport.All(scan), mgr.Global, force);
		await mgr.SyncFromDiskAsync().ConfigureAwait(false);

		StringBuilder sb = new();
		sb.AppendLine($"Imported {outcome.Imported} account(s) from {scan.ToolName} ({scan.Path}){(outcome.Skipped > 0 ? $", skipped {outcome.Skipped}" : "")}");

		foreach (Said note in outcome.Notes) {
			sb.AppendLine("  " + note);
		}

		if (outcome.Imported > 0) {
			sb.AppendLine();
			sb.AppendLine($"  Heads up: close {scan.ToolName} before starting these here. Two programs on one Steam account");
			sb.AppendLine("  take turns kicking each other off.");
			sb.AppendLine("  Start them with:  start all");
		}

		return sb.ToString().TrimEnd();
	}

	private static async Task<string> ReloadAsync(BotManager mgr) {
		string passwordBefore;
		bool broken;

		// Read and swapped in under the lock every change to the live settings takes. A dashboard save running meanwhile
		// wrote its change onto the settings being thrown away - saved, then gone - or saved the old ones over the file.
		lock (ConfigStore.GlobalEditGate) {
			passwordBefore = mgr.Global.WebPassword;
			GlobalConfig loaded = ConfigStore.LoadGlobal();
			broken = ConfigStore.GlobalBroken;

			if (!broken) {
				mgr.ApplyGlobal(loaded);
			}
		}

		// A file that doesn't load comes back as defaults. Put in force, one stray comma blanked the dashboard password,
		// the rep4rep token and the Telegram bot - so the settings in use stay until the file is fixed.
		if (broken) {
			await mgr.SyncFromDiskAsync().ConfigureAwait(false);

			return new Said("nocatFarm.json didn't load - still on the settings in use; fix it and 'reload' again").ToString();
		}

		// A password changed in the file and reloaded ends every browser's session, like a change anywhere else.
		if (!string.Equals(passwordBefore, mgr.Global.WebPassword, StringComparison.Ordinal)) {
			Web.WebHost.Current?.SignOutAll();
		}

		// What a change made anywhere else sets off - logging, a rep4rep hold, start with Windows. All of them are safe
		// to run again, which is how the dashboard's own save does it.
		foreach (SettingDef def in Settings.Global) {
			ApplyGlobalSideEffects(mgr, def);
		}

		await mgr.SyncFromDiskAsync().ConfigureAwait(false);

		return "Configs reloaded.";
	}

	private static string Logs(string[] args) {
		if ((args.Length > 0) && args[0].ToLowerInvariant() is "folder" or "dir" or "open") {
			return Log.Folder is not { } dir ? new Said("file logging is off - turn on Write a log file under Settings, Logging").ToString()
				: Platform.OpenFolder(dir) ? new Said("opened the log folder: {0}", dir).ToString()
				: new Said("the log files are in {0}", dir).ToString();
		}

		int n = args.Length > 0 && int.TryParse(args[0], out int parsed) ? Math.Clamp(parsed, 1, 500) : 30;

		return string.Join(Environment.NewLine, Log.Recent(n).Select(static e => $"{e.When:HH:mm:ss}  {e.Source,-12}{e.Text}"));
	}

	/// <summary>'report' is the daily summary now; 'report week' the weekly one - neither sends anything or moves a baseline.</summary>
	private static string Report(BotManager mgr, string[] args) {
		if ((args.Length > 0) && args[0].ToLowerInvariant() is "week" or "weekly" or "w") {
			return WeeklyReport.Text(mgr);
		}

		string daily = DailyReport.Summary();

		return daily.Length > 0 ? daily : "Nothing to report yet - no accounts, or nothing banked so far.";
	}

	/// <summary>'backup': the same zip the dashboard hands out, written next to config. It holds logins, so it says so.</summary>
	private static string BackupNow() {
		try {
			string path = Backup.WriteToFolder();
			Log.Info(new Said("backup saved: {0}", path));

			return $"Backup saved: {path}{Environment.NewLine}It holds your saved logins - keep it private. Restore it from the dashboard: Settings, Backup & restore.";
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"Couldn't save the backup: {Log.Describe(e)}";
		}
	}

	private static string StatsText(string[] args) {
		int hours = args.Length > 0 && int.TryParse(args[0], out int parsed) ? Math.Clamp(parsed, 1, 168) : 24;
		List<(DateTime Hour, int Cards, int Comments)> buckets = Stats.ByHour(hours);
		(int cards, int comments) = Stats.Totals(hours);

		// Each account's last day first - the daily report's table, which used to need a command of its own.
		string summary = DailyReport.Summary();
		StringBuilder sb = new();

		if (summary.Length > 0) {
			sb.AppendLine(summary);
			sb.AppendLine();
		}

		if (cards + comments == 0) {
			return sb.Append($"Nothing earned in the last {hours}h yet.").ToString();
		}

		sb.AppendLine($"Last {hours}h:  {cards} card(s)   {comments} comment(s)");
		int peak = Math.Max(1, buckets.Max(static b => b.Cards + b.Comments));

		foreach ((DateTime hour, int c, int m) in buckets) {
			int total = c + m;

			if (total == 0) {
				continue;
			}

			sb.AppendLine($"  {hour:HH:mm}  {new string('#', Math.Max(1, total * 30 / peak)),-30} {c} card(s), {m} comment(s)");
		}

		return sb.ToString().TrimEnd();
	}
}
