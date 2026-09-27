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
public sealed record CommandDef(string Name, string Args, string Group, string Help, string Aliases = "") {
	/// <summary>"status|s|bots" for the listing - the real name first, then everything else that reaches it.</summary>
	public string Display => Aliases.Length == 0 ? Name : Name + "|" + Aliases;

	public bool Matches(string typed) =>
		Name.Equals(typed, StringComparison.OrdinalIgnoreCase)
		|| Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries).Any(a => a.Equals(typed, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The command set, shared verbatim by the console and the dashboard's command box - so anything you can type
/// in the terminal you can also type in the browser, and both produce the same text back.
/// </summary>
public static partial class Commands {
	public const string GroupAccounts = "ACCOUNTS";
	public const string GroupPlaying = "PLAYING";
	public const string GroupCards = "TRADING CARDS";
	public const string GroupRep4Rep = "REP4REP";
	public const string GroupSettings = "SETTINGS";
	public const string GroupOther = "OTHER";

	/// <summary>Every command. This is the only list - <c>help</c> and /api/commands both render it.</summary>
	public static readonly IReadOnlyList<CommandDef> All = [
		new("status", "[account]", GroupAccounts, "What everything is doing right now.", "s|bots"),
		new("start", "<account|all>", GroupAccounts, "Log an account in."),
		new("stop", "<account|all>", GroupAccounts, "Log an account out. It stays configured."),
		new("restart", "<account|all>", GroupAccounts, "Stop then start again."),
		new("pause", "<account|all> [minutes]", GroupAccounts, "Stay logged in but stop playing, farming and commenting. Give it minutes and it picks back up by itself."),
		new("resume", "<account|all>", GroupAccounts, "Undo a pause."),
		new("add", "<name> <steamLogin|qr>", GroupAccounts, "Add an account. It asks for the password once, then remembers a login token - or 'qr' signs it in by scanning a code on the dashboard with the Steam app, no password at all."),
		new("remove", "<account>", GroupAccounts, "Delete an account and its stored login token.", "delete"),
		new("enable", "<account>", GroupAccounts, "Let this account log in again."),
		new("disable", "<account>", GroupAccounts, "Keep the account configured but never log it in."),

		new("play", "<account> <appIDs|none>", GroupPlaying, "Set the games this account idles for playtime."),
		new("selfcheck", "[account]", GroupAccounts, "Does a human-mode account look like a bot? A score out of 100 from what other people can see - hours on the profile, what its status shows, comments - with the setting that fixes each tell. Boost accounts are left out unless you name one.", "tells"),
		new("hours", "<account>", GroupPlaying, "How the account's hour targets are going - hours so far, what's left, and the pace needed to make a date."),
		new("drops", "<account> [appID|next] [count|all] | <account> off", GroupCards,
			"Go for card drops now, whatever the schedule says: one game until it has dropped that many (all it has left by default), then back to the usual day. Without an appID, the next game with cards."),
		new("grind", "<account|all> <appID> <hours> | <account> off", GroupPlaying,
			"Put an account on one game for a set number of hours, then let it go back to whatever it was doing. Outranks human mode while it runs."),
		new("human", "[account] [week|reroll]", GroupPlaying, "What human mode is doing today, and what it played. Add 'week' to see the next seven days, or 'reroll' to throw today's plan away and roll a fresh one from the current settings."),
		new("wake", "<account>", GroupPlaying, "Wake a sleeping human-mode account and start its day now. Bed time is unchanged."),
		new("name", "<account> [text|off]", GroupPlaying, "Custom non-Steam game name shown instead of the real game. No text shows the current one; 'off' clears it."),
		new("persona", "<account> <state>", GroupPlaying, "online | offline | busy | away | snooze | invisible."),
		new("nickname", "<account> <profile name>", GroupPlaying, "Change the name everybody sees on the profile and friends list. Not the custom game name - that's 'name'."),

		new("cards", "[account]", GroupCards, "What is still left to farm, and about how long it will take."),
		new("farm", "<account> on|off", GroupCards, "Turn trading-card farming on or off."),

		new("rep4rep", "status|points|profiles|tasks|on|off|now|pause|resume|clear|rest", GroupRep4Rep, "Everything rep4rep. Run it bare for a summary.", "r4r"),

		new("redeem", "[account] <key|file.txt> [key...]", GroupAccounts, "Activate product keys - or point it at a text file full of them. More than five queues itself and activates them slowly. Without an account it tries each in turn until one can use it.", "key"),
		new("send", "<account|all>", GroupCards, "Send an account's tradable items to the account listed under Trades.", "loot"),
		new("transfer", "<from> <to> [types]", GroupCards, "Send items from one of your accounts to another. Types as in the send setting (cards, foils, backgrounds, emoticons, boosters, gems, all); leave it off for trading cards."),
		new("2fa", "<account>", GroupAccounts, "Show this account's Steam Guard code, if its authenticator is set up here.", "guard"),
		new("cheevo", "<account> <appID> [list|unlock|lock] [name|all]", GroupPlaying, "Achievements: see them, unlock them all, or put them back.", "ach|achievements"),
		new("hunt", "[account]", GroupPlaying, "What the achievement hunter would play, in order - and what it ruled out and why.", "boost"),
		new("levelup", "<account> <level>", GroupCards, "What reaching a Steam level would cost: the XP missing, badges it can craft from its own cards, sets it has nearly finished, and the cheapest complete sets on the market for the rest - priced gently in the background.", "lvlup"),
		new("match", "[do]", GroupCards, "Swap duplicate trading cards between your own accounts so sets finish - only swaps that help both sides, never a card already on an offer. Shows what it would trade; 'match do' sends the offers, and the other account accepts them by itself."),
		new("offers", "[account|all]", GroupCards, "Live trade offers, straight from Steam: what's waiting to be accepted, what's been sent, and anything stuck on a confirmation or a trade hold."),
		new("keys", "[list|clear]", GroupAccounts, "Product keys waiting to be activated. A big batch queues itself rather than burning Steam's per-account activation allowance all at once."),
		new("value", "[account|all] [refresh]", GroupCards, "What each inventory is worth, by game, and how it has moved in the last day. Add 'refresh' to read the inventories again.", "inv|inventory"),

		new("import", "asf [path] [force]", GroupSettings, "Bring accounts across from ArchiSteamFarm, login tokens and all."),
		new("config", "[account]", GroupSettings, "Show every setting and its current value."),
		new("set", "[account] <key> <value>", GroupSettings, "Change a setting. Without an account name it changes a global one."),
		new("reload", "", GroupSettings, "Re-read every config file from disk."),

		new("log", "[count]", GroupOther, "The last few log lines.", "logs"),
		new("stats", "[hours]", GroupOther, "Cards dropped and comments posted, by hour."),
		new("plugins", "", GroupOther, "Which plugins are loaded, and where they came from."),
		new("level", "[account|all]", GroupAccounts, "Each account's Steam level."),
		new("balance", "[account|all]", GroupAccounts, "Steam wallet balance, and anything still pending.", "wallet"),
		new("points", "[account|all]", GroupAccounts, "Steam points each account can spend in the Points Shop."),
		new("fairswap", "<account> <offerID>", GroupCards, "Whether a trade offer is a fair card swap that AcceptFairCardSwaps would accept, and if not, why. Only looks - never accepts or declines."),
		new("sell", "<account> [preview|do|relist] [count]", GroupCards, "Spare trading cards on the market: 'preview' (the default) shows what it would list and what you'd get after Steam's fees, 'do' lists them (5 by default), 'relist' takes down week-old listings the market has gone under. SellDuplicates does it by itself."),
		new("queue", "[account|all]", GroupCards, "Go through today's discovery queue now, a few seconds on each game. The DiscoveryQueue setting does it by itself once a day (during sales, by default)."),
		new("freeitems", "[account|all]", GroupCards, "Look for free event items now: the daily sale sticker, and anything in the Points Shop at 0 points. The ClaimEventItems setting does it by itself."),
		new("booster", "[account|all] | <account> <appIDs>", GroupCards, "Gems, and which games can be made into booster packs now. With appIDs it makes those packs straight away; the BoosterGames setting does it by itself every day.", "boosters"),
		new("privacy", "<account> [public|friends|private|part=level ...]", GroupAccounts,
			"See an account's profile privacy, or set it - one word for everything, or parts such as inventory=public comments=friends. Parts: profile, games, playtime, friends, inventory, gifts, comments."),
		new("owns", "<appID|name>", GroupOther,
			"Which accounts already own a game, and how long each has played it. Takes an appID, a store URL, or part of a name."),
		new("addlicense", "<account|all> <IDs>", GroupOther,
			"Add free licences to an account's library - a subID, or a/<appID> for a free app. Only works for genuinely free licences - a paid one is refused by Steam, and it says why."),
		new("report", "", GroupOther, "Write the daily summary - hours banked, cards, comments, totals - to the log now."),
		new("answer", "<text>", GroupOther, "Answer whatever nocat.farm is waiting on - a Steam Guard code, or a password."),
		new("tutorial", "[topic]", GroupOther, "Getting started, in order, ticking off what you have already done.", "guide|setup"),
		new("help", "[command|setting]", GroupOther, "This list, or what one command or setting does.", "?|h"),
		new("theme", "[dark|light]", GroupOther, "Switch the dashboard between the dark and light themes. Without an argument it says which is on.", "dark|light"),
		new("version", "", GroupOther, "Which version this is.", "about"),
		new("mini", "[on|off]", GroupOther, "Shrink the window to a small panel of your accounts - what each is doing, start and stop, the dashboard - or back to the full window."),
		new("update", "[accept|ignore]", GroupOther, "Check for a newer release. 'update accept' downloads it and restarts into it; 'update ignore' stops the hourly reminders until the next launch. Nothing updates on its own, ever."),
		new("exit", "", GroupOther, "Shut nocat.farm down.", "quit|q")
	];

	public static bool ExitRequested { get; private set; }

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
	public static async Task<string> RunAsync(string input, string botName) {
		BotManager? mgr = Host;

		if (mgr == null) {
			return "nocat.farm isn't ready yet";
		}

		string line = input.Trim();
		string verb = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";

		// Never let a remote command shut the whole thing down - it is one mis-sent word from taking every
		// account offline, and there is no way to start it again from Steam.
		if (verb is "exit" or "quit" or "q") {
			return "that one has to be done at the PC";
		}

		if ((verb.Length > 0) && (line.IndexOf(' ') < 0) && DefaultsToThisBot(verb)) {
			line = $"{verb} {botName}";
		}

		return await RunAsync(mgr, line).ConfigureAwait(false);
	}

	/// <summary>Commands where a bare verb sensibly means "this account", rather than "all of them".</summary>
	private static bool DefaultsToThisBot(string verb) =>
		verb is "status" or "s" or "pause" or "resume" or "start" or "stop" or "cards" or "config" or "human";

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

		if (string.IsNullOrEmpty(url)) {
			return false;
		}

		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

			return true;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't open the dashboard: {0}: {1}", e.GetType().Name, e.Message));

			return false;
		}
	}

	/// <summary>
	/// Whether a tray icon exists to bring the window back.
	///
	/// Without one, hiding the window strands the process: it keeps running with nothing left to click.
	/// </summary>
	public static bool TrayPresent { get; set; }

	/// <summary>Ask for shutdown from somewhere that isn't the command router - the window's quit button.</summary>
	public static void RequestExit() {
		ExitRequested = true;
		ExitHandler?.Invoke();
	}

	/// <summary>Set by the tray icon so minimise-to-tray applies the moment it's changed.</summary>
	public static Action<bool>? TrayHook { get; set; }

	public static async Task<string> RunAsync(BotManager mgr, string input) {
		string line = input.Trim();

		if (line.Length == 0) {
			return "";
		}

		string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string cmd = parts[0].ToLowerInvariant();
		string[] rest = parts[1..];

		try {
			return cmd switch {
				"help" or "?" or "h" => Help(rest),
				"tutorial" or "guide" or "setup" => Tutorial.Render(mgr, rest.FirstOrDefault()),
				"status" or "s" => Status(mgr, rest.FirstOrDefault()),
				"bots" => Status(mgr, null),
				"start" => await LifecycleAsync(mgr, rest, "start").ConfigureAwait(false),
				"stop" => await LifecycleAsync(mgr, rest, "stop", graceful: true).ConfigureAwait(false),
				"pause" => await LifecycleAsync(mgr, rest, "pause").ConfigureAwait(false),
				"resume" => await LifecycleAsync(mgr, rest, "resume").ConfigureAwait(false),
				"restart" => await RestartAsync(mgr, rest).ConfigureAwait(false),
				"play" => Play(mgr, rest),
				"grind" => Grind(mgr, rest),
				"drops" => await DropsAsync(mgr, rest).ConfigureAwait(false),
				"hours" => Hours(mgr, rest),
				"offers" => await OffersAsync(mgr, rest).ConfigureAwait(false),
				"levelup" or "lvlup" => LevelUp(mgr, rest),
				"selfcheck" or "tells" => await SelfCheckAsync(mgr, rest).ConfigureAwait(false),
				"human" => Human(mgr, rest),
				"wake" or "wakeup" or "skipsleep" => Wake(mgr, rest),
				"redeem" or "key" => await RedeemAsync(mgr, rest).ConfigureAwait(false),
				"send" or "loot" => await SendAsync(mgr, rest).ConfigureAwait(false),
				"2fa" or "guard" => TwoFactor(mgr, rest),
				"cheevo" or "ach" or "achievements" => await CheevoAsync(mgr, rest).ConfigureAwait(false),
				"hunt" or "boost" => await HuntAsync(mgr, rest).ConfigureAwait(false),
				"value" or "inv" or "inventory" => InventoryText(mgr, rest),
				"keys" => KeysText(rest),
				"match" => await MatchAsync(mgr, rest).ConfigureAwait(false),
				"name" => Name(mgr, rest),
				"persona" => Persona(mgr, rest),
				"nickname" => Nickname(mgr, rest),
				"level" => await LevelAsync(mgr, rest).ConfigureAwait(false),
				"balance" or "wallet" => Balance(mgr, rest),
				"points" => await PointsAsync(mgr, rest).ConfigureAwait(false),
				"booster" or "boosters" => await BoosterAsync(mgr, rest).ConfigureAwait(false),
				"freeitems" => await FreeItemsAsync(mgr, rest).ConfigureAwait(false),
				"queue" => await QueueAsync(mgr, rest).ConfigureAwait(false),
				"sell" => await SellAsync(mgr, rest).ConfigureAwait(false),
				"fairswap" => await FairSwapCheckAsync(mgr, rest).ConfigureAwait(false),
				"privacy" => await PrivacyAsync(mgr, rest).ConfigureAwait(false),
				"transfer" => await TransferAsync(mgr, rest).ConfigureAwait(false),
				"farm" => Farm(mgr, rest),
				"cards" => Cards(mgr, rest),
				"rep4rep" or "r4r" => await Rep4RepAsync(mgr, rest).ConfigureAwait(false),
				"add" => await AddAsync(mgr, rest).ConfigureAwait(false),
				"remove" or "delete" => await RemoveAsync(mgr, rest).ConfigureAwait(false),
				"enable" => Enable(mgr, rest, true),
				"disable" => Enable(mgr, rest, false),
				"set" => Set(mgr, rest),
				"config" => ShowConfig(mgr, rest),
				"import" => await ImportAsync(mgr, rest).ConfigureAwait(false),
				"reload" => await ReloadAsync(mgr).ConfigureAwait(false),
				"log" or "logs" => Logs(rest),
				"stats" => StatsText(rest),
				"report" => DailyReport.RunNow(),
				"answer" => Prompt.Answer(string.Join(' ', rest)) ? "answered" : "nothing is waiting for an answer",
				"theme" or "dark" or "light" => Theme(cmd, rest),
				"version" or "about" => About(),
				"mini" => Mini(rest),
				"plugins" => PluginList(),
				"owns" => Owns(mgr, rest),
				"addlicense" => await AddLicense(mgr, rest).ConfigureAwait(false),
				"update" => await Update(rest).ConfigureAwait(false),
				"exit" or "quit" or "q" => Exit(),
				// A plugin's own command, tried only after every built-in has been ruled out - so a plugin can
				// never take a verb the app already answers to, whatever it registered.
				_ => Plugins.PluginHost.Commands.TryGetValue(cmd, out (string Usage, string Help, Func<string[], Task<string>> Run) added)
					? await added.Run(rest).ConfigureAwait(false)
					: Suggest(cmd)
			};
		} catch (Exception e) {
			return $"'{cmd}' failed: {e.GetType().Name}: {e.Message}";
		}
	}

	private static string Suggest(string cmd) {
		CommandDef? near = All.FirstOrDefault(c => c.Name.StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
			?? All.FirstOrDefault(c => c.Name.Contains(cmd, StringComparison.OrdinalIgnoreCase));

		return near == null
			? $"There's no '{cmd}' command. Type 'help' for the list, or 'tutorial' if you're just starting."
			: $"There's no '{cmd}' command. Did you mean '{near.Name}'? Type 'help' for the list.";
	}

	/// <summary>
	/// Check for a newer release, and on "now", install it.
	///
	/// The check is forced rather than daily-gated: somebody typing this has asked, and answering "I looked
	/// this morning" is not an answer. Installing is always explicit - see SelfUpdate for why nothing here
	/// ever happens on a schedule.
	/// </summary>
	private static async Task<string> Update(string[] args) {
		string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";

		if (what == "ignore") {
			UpdateCheck.Ignored = true;

			return "No more update reminders until the next launch. 'update' still checks, and 'update accept' still installs.";
		}

		bool accept = what is "accept" or "now" or "install";

		await UpdateCheck.LookAsync(force: true).ConfigureAwait(false);

		if (UpdateCheck.Available == null) {
			return $"You're on the newest release ({Build.Version}).";
		}

		if (!accept) {
			return $"{UpdateCheck.Available} is out - you have {Build.Version}."
				+ Environment.NewLine + $"  {UpdateCheck.Url}"
				+ Environment.NewLine + "  'update accept' downloads it and restarts into it; 'update ignore' stops the reminders until the next launch.";
		}

		return await SelfUpdate.ApplyAsync(CancellationToken.None).ConfigureAwait(false)
			?? "downloading and restarting - this window will come back on its own";
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

		StringBuilder sb = new();

		foreach (Bot bot in targets) {
			if (!bot.IsOnline) {
				sb.AppendLine($"{bot.Name}: not online");

				continue;
			}

			foreach ((bool app, uint id) in wanted) {
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
		}

		return sb.ToString().TrimEnd();
	}

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
				sb.AppendLine($"  {(verb + " " + usage).TrimEnd(),-30} {help}");
			}
		}

		return sb.ToString().TrimEnd();
	}

	private static string Exit() {
		ExitRequested = true;
		ExitHandler?.Invoke();

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

		Live.Global.Theme = want;
		ConfigStore.SaveGlobal(Live.Global);

		return $"Dashboard set to the {want} theme. Reload the page to see it.";
	}

	private static string About() =>
		$"""
		nocat.farm {Build.Version} - Steam idler, trading-card farmer and rep4rep commenter.
		Everything runs on this PC. Your accounts never leave it; the only thing that talks
		to rep4rep is the task queue.
		""";

	// ── help ────────────────────────────────────────────────────────────────
	private static string Help(string[] args) {
		if (args.Length > 0) {
			// 'help set <key>' and 'help <key>' both explain a setting - the same sentence the dashboard shows.
			string wanted = args[^1];
			SettingDef? def = Settings.Find(wanted);

			if (def != null) {
				StringBuilder sb = new();
				sb.AppendLine($"{def.Name}  ({def.Label})");
				sb.AppendLine($"  {def.Tooltip}");

				if (def.Choices != null) {
					sb.AppendLine($"  One of: {def.Choices}");
				}

				if (def.Min > double.MinValue || def.Max < double.MaxValue) {
					sb.AppendLine($"  Between {def.Min:0.##} and {def.Max:0.##}.");
				}

				object? fallback = Settings.Read(Settings.FindGlobal(def.Name) != null ? Settings.GlobalDefaults : Settings.BotDefaults, def.Name);
				sb.AppendLine($"  Default: {(fallback is List<uint> l ? (l.Count == 0 ? "(none)" : string.Join(", ", l)) : fallback)}");

				if (def.NeedsRestart) {
					sb.AppendLine("  Takes effect the next time nocat.farm starts.");
				}

				return sb.ToString().TrimEnd();
			}

			CommandDef? cmd = All.FirstOrDefault(c => c.Matches(wanted));

			if (cmd != null) {
				return $"{cmd.Display} {cmd.Args}\n  {cmd.Help}";
			}

			return $"Nothing called '{wanted}'. Type 'help' for commands, or 'config' to list settings.";
		}

		StringBuilder help = new();

		foreach (string group in All.Select(static c => c.Group).Distinct()) {
			help.AppendLine(group);

			foreach (CommandDef c in All.Where(c => c.Group == group)) {
				string left = (c.Display + " " + c.Args).TrimEnd();

				// Wrap rather than widen. Padding to fit the longest entry would push every other line's help
				// text 24 columns to the right to accommodate three commands, which reads far worse than the
				// three long ones taking a second line.
				if (left.Length > 44) {
					help.AppendLine($"  {left}");
					help.AppendLine($"  {new string(' ', 44)}{c.Help}");
				} else {
					help.AppendLine($"  {left,-44}{c.Help}");
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

			sb.AppendLine($"  {Log.Pad(b.Name, 13)}{Log.Pad(StateWord(b), 13)}{Log.Pad(uptime, 8)}{Log.Pad(playing, 24)}{Log.Pad(cards, 7)}{comments}");

			if (b.GuardPrompt != null) {
				sb.AppendLine($"    {bar} waiting on you: {b.GuardPrompt}");
			}

			foreach (IBotModule m in b.Modules) {
				// Module statuses are localised, so a bare `is not ("idle" or "off")` only ever matched in
				// English and printed a wall of resting modules in every other language. Compare against the
				// same words in the same language.
				if (!string.IsNullOrEmpty(m.Status) && !Core.Loc.Is(m.Status, "idle") && !Core.Loc.Is(m.Status, "off")) {
					sb.AppendLine($"    {bar} {Log.Pad(m.Name, 8)} {m.Status}");
				}
			}
		}

		return sb.ToString().TrimEnd();
	}

	public static string StateWord(Bot b) {
		if (!b.Cfg.Enabled) {
			return "disabled";
		}

		if (b.Paused) {
			return "paused";
		}

		if (b.State == BotState.Online) {
			if (b.PlayingBlocked) {
				return "stood down";
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

	private static async Task<string> LifecycleAsync(BotManager mgr, string[] args, string verb, bool graceful = false) {
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
			if (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || (minutes <= 0)) {
				return $"'{args[1]}' isn't a number of minutes - e.g. pause {args[0]} 30";
			}

			pauseFor = TimeSpan.FromMinutes(Math.Min(minutes, 7 * 24 * 60));
		}

		List<Task> stops = [];

		foreach (Bot bot in targets.ToArray()) {
			switch (verb) {
				case "start":
					if (!bot.Cfg.Enabled) {
						continue;
					}

					await bot.StartAsync().ConfigureAwait(false);

					break;
				case "stop":
					stops.Add(bot.StopAsync(graceful));

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

		if (stops.Count > 0) {
			await Task.WhenAll(stops).ConfigureAwait(false);
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

		return all ? $"{what}: {count} account(s)" : $"{args[0]}: {what}";
	}

	private static async Task<string> RestartAsync(BotManager mgr, string[] args) {
		await LifecycleAsync(mgr, args, "stop").ConfigureAwait(false);
		await Task.Delay(1500).ConfigureAwait(false);

		return await LifecycleAsync(mgr, args, "start").ConfigureAwait(false);
	}

	private static async Task<string> AddAsync(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "add <name> <steamLogin|qr>\n  name       a nickname just for you - it names the config file\n  steamLogin what you type into Steam's sign-in box\n  qr         sign in by scanning a code with the Steam app instead - no password";
		}

		string name = args[0];

		if (!ConfigStore.IsValidBotName(name)) {
			return "That name can't be used. Letters, numbers, dashes and underscores - and 'nocatFarm' is taken by the global config.";
		}

		if (mgr.Get(name) != null) {
			return $"'{name}' already exists.";
		}

		bool qr = args[1].Equals("qr", StringComparison.OrdinalIgnoreCase);
		Bot? bot = await mgr.AddAsync(name, qr ? new BotConfig { SteamLogin = name, SignInWithQr = true } : new BotConfig { SteamLogin = args[1] }).ConfigureAwait(false);

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

		return qr
			? $"Added '{name}'. Its QR code appears on the dashboard in a moment - scan it with the Steam app on your phone (Steam Guard tab) and approve, and that's it: no password, nothing else to type.{andThen}"
			: $"Added '{name}' ({args[1]}). It will ask for the password and a Steam Guard code once, then remember this account.{andThen}";
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

		bot.Cfg.Enabled = enabled;
		ConfigStore.SaveBot(bot.Name, bot.Cfg);
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

			List<(uint Game, int Minutes)> byGame = human.TodayByGame().ToList();

			if (byGame.Count > 0) {
				int total = Math.Max(1, byGame.Sum(static g => g.Minutes));

				foreach ((uint game, int minutes) in byGame) {
					sb.AppendLine($"                {GameNames.Of(game),-28} {Fmt.Hm(minutes),8}   {minutes * 100 / total,3}%");
				}
			}

			List<(uint Game, int Weight)> weights = HumanMode.ParseWeights(bot.Cfg.GameWeights);

			if (weights.Count > 0) {
				int total = Math.Max(1, weights.Sum(static w => w.Weight));
				sb.AppendLine("  set to play " + string.Join(", ", weights.Select(w => $"{GameNames.Of(w.Game)} {w.Weight * 100 / total}%")));

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
					double[] exact = weights
						.Select((w, i) => {
							double share = w.Weight * 100.0 / total;

							return i == 0 ? pure + ((100 - pure) * share / 100) : (100 - pure) * share / 100;
						})
						.ToArray();

					int[] shown = Fmt.RoundToTotal(exact, 100);

					IEnumerable<string> real = weights.Select((w, i) => $"{GameNames.Of(w.Game)} {shown[i]}%");

					sb.AppendLine($"  over a week   {string.Join(", ", real)}   ({pure}% of days are {GameNames.Of(weights[0].Game)} only)");
				}
			}

			if (week) {
				sb.AppendLine("  the week ahead (rolled the same way the real one is, so it's a sample - not a promise):");

				foreach (string line in HumanMode.PreviewWeek(bot.Cfg)) {
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
	private static async Task<string> RedeemAsync(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return "redeem <key>, or redeem <account> <key> to send it to one account only.";
		}

		// A first argument that names an account is the account; otherwise every argument is a key.
		Bot? only = mgr.Get(args[0]);
		string[] keys = only == null ? args : args[1..];

		// Point it at a text file and it reads the keys out of it.
		//
		// A batch of keys arrives as a file far more often than as something anybody would type, and pasting two
		// hundred of them into a command line is not a thing people do. Any line shape works - one per line, with
		// or without a game name beside it - because the key is found by its shape rather than by position.
		if ((keys.Length == 1) && LooksLikePath(keys[0])) {
			string path = keys[0].Trim('"');

			if (!File.Exists(path)) {
				return $"There's no file at '{path}'.";
			}

			try {
				keys = [.. KeysIn(File.ReadAllText(path))];
			} catch (Exception e) {
				return $"Couldn't read '{path}': {e.Message}";
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
			int queued = KeyQueue.Add(keys);

			return $"{queued} key(s) queued - they'll be activated a few at a time, because Steam limits how many "
				+ $"an account may try per hour. 'keys' shows what's left.{(queued < keys.Length ? $" ({keys.Length - queued} were already in the queue.)" : "")}";
		}

		StringBuilder sb = new();

		foreach (string key in keys) {
			sb.AppendLine(await Redeeming.RedeemAcrossAsync(targets, key).ConfigureAwait(false));
		}

		return sb.ToString().TrimEnd();
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

	/// <summary>What is still waiting to be activated.</summary>
	private static string KeysText(string[] args) {
		if (args.FirstOrDefault()?.Equals("clear", StringComparison.OrdinalIgnoreCase) == true) {
			int had = KeyQueue.Clear();

			return had == 0 ? "The queue was already empty." : $"Dropped {had} queued key(s).";
		}

		List<(string Key, int Tries, DateTime NotBefore)> pending = KeyQueue.Snapshot();

		if (pending.Count == 0) {
			return "No keys are waiting. Paste more than five at once and they'll queue automatically.";
		}

		List<string> lines = [$"{pending.Count} key(s) waiting:"];

		foreach ((string key, int tries, DateTime notBefore) in pending.Take(15)) {
			string when = notBefore > DateTime.UtcNow ? $"not before {notBefore.ToLocalTime():HH:mm}" : "ready";

			lines.Add($"   {Mask(key),-24} {when}{(tries > 0 ? $"   {tries} try/tries so far" : "")}");
		}

		if (pending.Count > 15) {
			lines.Add($"   ...and {pending.Count - 15} more");
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>A key is worth money - show enough to recognise it, not enough to use it over somebody's shoulder.</summary>
	private static string Mask(string key) => key.Length <= 5 ? key : key[..5] + new string('-', Math.Min(12, key.Length - 5));

	/// <summary>Send an account's items to its trade master. Never anywhere else - see Looting for why.</summary>
	private static async Task<string> SendAsync(BotManager mgr, string[] args) {
		string target = args.FirstOrDefault() ?? "";

		if (target.Length == 0) {
			return "send <account>, or send all.";
		}

		List<Bot> bots = target.Equals("all", StringComparison.OrdinalIgnoreCase)
			? mgr.All.Where(static b => b.IsOnline).ToList()
			: mgr.Get(target) is { } one ? [one] : [];

		if (bots.Count == 0) {
			return target.Equals("all", StringComparison.OrdinalIgnoreCase) ? "No account is logged in." : NoSuchAccount(mgr, target);
		}

		List<string> lines = [];

		foreach (Bot bot in bots) {
			lines.Add(await Looting.SendToMasterAsync(bot).ConfigureAwait(false));
		}

		return string.Join(Environment.NewLine, lines);
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
				lines.Add($"  {b.Name,-12} {Core.MobileAuth.GenerateCode(b.Secrets.Shared)}");
			}

			return lines.Count == 0
				? "No account has its authenticator set up here. Drop a maFile into config/authenticators/, or paste the secret into the account's settings."
				: "Steam Guard codes (they change every 30 seconds):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
		}

		Bot? bot = mgr.Get(name);

		if (bot == null) {
			return NoSuchAccount(mgr, name);
		}

		string? code = Core.MobileAuth.GenerateCode(bot.Secrets.Shared);

		return code == null
			? $"{bot.Name} has no authenticator set up here. Drop {bot.Name}.maFile into config/authenticators/, or paste its shared secret into the account's settings."
			: $"{bot.Name}: {code}   (changes every 30 seconds)";
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
		int pending = 0;

		foreach (Bot bot in targets) {
			if (refresh) {
				bot.Inventory.ForceRefresh();
			}

			if (!bot.Cfg.ShowInventoryValue) {
				lines.Add($"{bot.Name}: not being valued (its \"Work out what its inventory is worth\" setting is off)");

				continue;
			}

			total += bot.Inventory.Total;
			pending += bot.Inventory.Pending;

			string moved = InventoryHistory.Since(bot.Name, TimeSpan.FromHours(24)) is { } d
				? $"   {(d.Change >= 0 ? "+" : "")}{PriceBook.Symbol}{d.Change:0.00} ({(d.Percent >= 0 ? "+" : "")}{d.Percent:0.0}%) in 24h"
				: "";

			lines.Add($"{bot.Name}: {PriceBook.Symbol}{bot.Inventory.Total:N2}{moved}"
				+ (bot.Inventory.Pending > 0 ? $"   ({bot.Inventory.Pending} item(s) still being priced)" : "")
				+ (bot.Inventory.Ready ? "" : "   (reading it now)"));

			foreach (InventoryValue.GameValue game in bot.Inventory.ByGame.Take(6)) {
				lines.Add(game.Blocked
					? $"      {game.Game,-30} skipped - on this account's ignore list ({game.Items} item(s))"
					: $"      {game.Game,-30} {PriceBook.Symbol}{game.Value,10:N2}   {game.Items} item(s)");
			}
		}

		if (targets.Count > 1) {
			lines.Add($"all: {PriceBook.Symbol}{total:N2}{(pending > 0 ? $"   ({pending} still being priced)" : "")}");
		}

		if (refresh) {
			lines.Add("Reading the inventories again - prices are kept for a day, so only what CHANGED gets looked up.");
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
				blocks.Add(await boost.ExplainAsync(CancellationToken.None).ConfigureAwait(false));
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

		if ((args.Length < 3) || !double.TryParse(args[2], out double hours) || (hours <= 0)) {
			return "How many hours? e.g.  grind " + args[0] + " " + appId + " 6";
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
			Said lead = delay > TimeSpan.Zero ? new Said(" (finishing up first, starts in ~{0})", Fmt.Hm((int) Math.Ceiling(delay.TotalMinutes))) : default;
			Log.Info(new Said("grinding {0} for {1}{2} - normal schedule resumes after", GameNames.Of(appId), Fmt.Hm((int) how.TotalMinutes), lead), bot.Name);
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

				lines.Add($"  #{o.Id}  {(o.Ours ? "sent to" : "from")} {who}  {o.Describe}  {state}{hold}");
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

	private static async Task<string> DropsAsync(BotManager mgr, string[] args) {
		if (args.Length < 1) {
			return string.Join(Environment.NewLine, [
				"drops <account> [appID|next] [count|all]   go for card drops now, whatever the schedule says",
				"drops <account> off                        stop early and go back to normal",
				"  drops new                every card left in the next game with cards",
				"  drops new 460920 2       two drops from Steep, then back to the usual day",
				"  drops new next 1         one drop from whatever has cards next"
			]);
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		if ((args.Length > 1) && args[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
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

		// The grind's time is only a cap, for a game that stops dropping: twice the estimate and a half hour, two hours
		// at the least and a day at the most.
		TimeSpan cap = TimeSpan.FromMinutes(Math.Clamp((estimate * 2) + 30, 120, 24 * 60));
		TimeSpan delay = bot.HumanOwned ? TimeSpan.FromSeconds(Rng.Next(45, 210)) : TimeSpan.Zero;

		if (!bot.StartGrind(app, cap, delay, drops: want)) {
			return $"{bot.Name}: {game} is still refundable, and a drop run would spend that.";
		}

		Said lead = delay > TimeSpan.Zero ? new Said(" (finishing up first, starts in ~{0})", Fmt.Hm((int) Math.Ceiling(delay.TotalMinutes))) : default;
		Log.Info(new Said("going for {0} card drop(s) in {1} - about {2}{3}, then back to the usual day", want, game, Fmt.Rough(estimate), lead), bot.Name);

		return $"{bot.Name}: {game} until {want} card(s) drop - about {Fmt.Rough(estimate)}"
			+ (delay > TimeSpan.Zero ? $", starting in ~{Fmt.Hm((int) Math.Ceiling(delay.TotalMinutes))}" : "")
			+ $". It stops by itself after {Fmt.Rough((int) cap.TotalMinutes)} if the drops don't come; 'drops {bot.Name} off' stops it now.";
	}

	private static async Task<string> CheevoAsync(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return string.Join(Environment.NewLine, [
				"cheevo <account> <appID> [list|unlock|lock] [name|all]",
				"  cheevo new 730              what it has and what's left",
				"  cheevo new 730 unlock all   every one it's allowed to set",
				"  cheevo new 730 unlock ACH_X just that one"
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
			return DescribeAchievements(bot, set);
		}

		if (verb is not ("unlock" or "lock")) {
			return $"'{verb}' isn't one of list, unlock or lock.";
		}

		bool unlock = verb == "unlock";
		string target = args.Length > 3 ? args[3] : "";

		if (target.Length == 0) {
			return $"Which one? Name it, or say 'all'. 'cheevo {bot.Name} {appId}' lists them.";
		}

		List<Achievement> chosen;

		if (target.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			chosen = (unlock ? set.Locked : set.Unlocked.Where(static a => a.Settable)).ToList();
		} else {
			Achievement? one = set.All.FirstOrDefault(a => a.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

			if (one == null) {
				return $"{GameNames.Of(appId)} has no achievement called '{target}'.";
			}

			if (!one.Settable) {
				return $"\"{one.Display}\" is awarded by Steam's own servers - a client isn't allowed to set it.";
			}

			chosen = [one];
		}

		if (chosen.Count == 0) {
			return unlock ? "Nothing left to unlock." : "Nothing unlocked that can be put back.";
		}

		(bool ok, string message) = await Achievements.SetAsync(bot, set, chosen, unlock).ConfigureAwait(false);

		if (!ok) {
			return $"{bot.Name}: {message}";
		}

		Log.Reward(new Said("{0} in {1}", message, GameNames.Of(appId)), bot.Name);

		return $"{bot.Name}: {message} in {GameNames.Of(appId)}.";
	}

	private static string DescribeAchievements(Bot bot, AchievementSet set) {
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
		foreach (Achievement a in set.All.OrderByDescending(static a => a.GlobalPercent ?? 50)) {
			string mark = a.Unlocked ? "x" : a.Settable ? " " : "-";
			string rarity = a.GlobalPercent is { } p ? $"{p,5:0.#}%" : "     ?";
			sb.AppendLine($"  [{mark}] {rarity}  {Log.Pad(a.Name, 34)} {a.Display}");
		}

		sb.Append("  [x] unlocked   [ ] can be unlocked   [-] Steam-awarded only");

		return sb.ToString();
	}

	private static string Play(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "play <account> <appIDs...>   or   play <account> none";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		string? error = Settings.Apply(bot.Cfg, Settings.FindBot("IdleGames")!, string.Join(',', args[1..]));

		if (error != null) {
			return error;
		}

		ConfigStore.SaveBot(bot.Name, bot.Cfg);
		BotManager.ModuleOf<Idler>(bot)?.Assert();

		return bot.Cfg.IdleGames.Count == 0 ? $"{bot.Name}: stopped idling" : $"{bot.Name}: idling {string.Join(", ", bot.Cfg.IdleGames)}";
	}

	private static string Name(BotManager mgr, string[] args) {
		if (args.Length == 0) {
			return "name <account> [text|off]   (no text shows the current one, 'off' clears it)";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		// Just the account: say what it shows. This used to clear the name - typing 'name kylro' to look at it
		// wiped it, which is exactly what anybody would try first.
		if (args.Length == 1) {
			return string.IsNullOrEmpty(bot.Cfg.CustomGameName)
				? $"{bot.Name}: no custom name - it shows the real game. 'name {bot.Name} <text>' sets one."
				: $"{bot.Name}: shows \"{bot.Cfg.CustomGameName}\"{(bot.Cfg.CustomGameNameEnabled ? "" : " (switched off under Settings)")} · 'name {bot.Name} off' clears it";
		}

		string text = string.Join(' ', args[1..]);
		bot.Cfg.CustomGameName = text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("clear", StringComparison.OrdinalIgnoreCase) ? "" : text;
		ConfigStore.SaveBot(bot.Name, bot.Cfg);
		BotManager.ModuleOf<Idler>(bot)?.Assert();

		return string.IsNullOrEmpty(bot.Cfg.CustomGameName)
			? $"{bot.Name}: showing the real game again"
			: $"{bot.Name}: now showing \"{bot.Cfg.CustomGameName}\"";
	}

	private static string Persona(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "persona <account> online|offline|busy|away|snooze|invisible";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		SettingDef def = Settings.FindBot("OnlineStatus")!;
		string? error = Settings.Apply(bot.Cfg, def, args[1]);

		if (error != null) {
			return error;
		}

		ConfigStore.SaveBot(bot.Name, bot.Cfg);
		bot.ApplyPersona();

		return $"{bot.Name}: {Settings.ChoiceLabel(def, bot.Cfg.OnlineStatus)}";
	}

	/// <summary>The accounts a read-only command answers for: the one named, or every account for "all" or nothing.</summary>
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

	private static string Nickname(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "nickname <account> <profile name>";
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return NoSuchAccount(mgr, args[0]);
		}

		string name = string.Join(' ', args[1..]).Trim();

		// Steam's own limit for a profile name.
		if (name.Length > 32) {
			return $"That's {name.Length} characters - Steam allows 32 at most.";
		}

		return bot.SetProfileName(name) ? $"{bot.Name}: profile name is now \"{name}\"" : $"{bot.Name}: not logged in";
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

	private static async Task<string> FreeItemsAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		List<string> lines = [];
		List<(Bot Bot, EventItems Events)> ready = [];

		foreach (Bot b in bots) {
			if (!b.IsOnline || !b.Web.Ready || (BotManager.ModuleOf<EventItems>(b) is not { } events)) {
				lines.Add($"{b.Name}: not logged in");
			} else {
				ready.Add((b, events));
			}
		}

		async Task<string> Look(Bot b, EventItems events) {
			bool sticker = await events.StickerAsync(CancellationToken.None).ConfigureAwait(false);
			int shop = await events.ShopAsync(CancellationToken.None).ConfigureAwait(false);

			return $"{b.Name}: {(sticker ? "claimed the sale item" : "no sale item to claim right now")} · {shop} free Points Shop item(s) taken";
		}

		// The first look after a start reads the whole Points Shop, which takes minutes - long enough that a
		// console just sitting there looks hung. So that one goes on in the background and says so; the answer
		// lands in the log.
		if ((ready.Count > 0) && !EventItems.ShopKnown) {
			_ = Task.Run(async () => {
				foreach ((Bot b, EventItems events) in ready) {
					try {
						Log.Info(await Look(b, events).ConfigureAwait(false), b.Name);
					} catch (Exception e) {
						Log.Debug(new Said("free items: {0}", e.Message), b.Name);
					}
				}
			});
			lines.Add($"Reading the Points Shop first - it's big, so the first look after a start takes a few minutes. {(ready.Count == 1 ? ready[0].Bot.Name : $"{ready.Count} accounts")}: the answer goes to the log when it's done.");

			return string.Join(Environment.NewLine, lines);
		}

		foreach ((Bot b, EventItems events) in ready) {
			lines.Add(await Look(b, events).ConfigureAwait(false));
		}

		return string.Join(Environment.NewLine, lines);
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
			string game = o.Game.Length > 24 ? o.Game[..23] + "…" : o.Game;
			sb.AppendLine($"  {game,-24} {o.Card,-24} lowest {Seller.Money(o.LowestCents, bot)} -> list at {Seller.Money(o.BuyerCents, bot)}, you get {Seller.Money(o.YouGetCents, bot)}");
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

		string[] lines = await Task.WhenAll(bots.Select(static async b => {
			if (!b.IsOnline || (BotManager.ModuleOf<EventItems>(b) is not { } events)) {
				return $"{b.Name}: not logged in";
			}

			int seen = await events.QueueAsync(CancellationToken.None).ConfigureAwait(false);

			return seen < 0 ? $"{b.Name}: Steam wouldn't hand out the queue right now" : $"{b.Name}: looked through {seen} game(s) in the discovery queue";
		})).ConfigureAwait(false);

		return string.Join(Environment.NewLine, lines);
	}

	private static async Task<string> BoosterAsync(BotManager mgr, string[] args) {
		// booster <account> <appIDs> - make them now
		if ((args.Length > 1) && (mgr.Get(args[0]) is { } maker)) {
			if (!maker.IsOnline || !maker.Web.Ready) {
				return $"{maker.Name}: not logged in";
			}

			if (await Boosters.ReadAsync(maker).ConfigureAwait(false) is not { } page) {
				return $"{maker.Name}: couldn't read the booster creator";
			}

			List<string> said = [];
			uint tradable = page.TradableGems, untradable = page.UntradableGems, gems = page.Gems;

			foreach (string arg in args[1..]) {
				if (!uint.TryParse(arg.Trim(','), NumberStyles.None, CultureInfo.InvariantCulture, out uint appId) || (appId == 0)) {
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

	private static async Task<string> TransferAsync(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "transfer <from> <to> [types]";
		}

		if (mgr.Get(args[0]) is not { } from) {
			return NoSuchAccount(mgr, args[0]);
		}

		if (mgr.Get(args[1]) is not { } to) {
			return NoSuchAccount(mgr, args[1]);
		}

		if (from == to) {
			return "That's the same account both ways.";
		}

		if (to.SteamId == 0) {
			return $"{to.Name} hasn't signed in yet this run, so its Steam ID isn't known - start it first.";
		}

		string types = args.Length > 2 ? string.Join(',', args[2..]) : "";

		return await Looting.SendItemsAsync(from, to.SteamId, null, types).ConfigureAwait(false);
	}

	private static string Farm(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "farm <account> on|off";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		bool on = Settings.IsTrue(args[1]);
		bot.Cfg.FarmCards = on;
		ConfigStore.SaveBot(bot.Name, bot.Cfg);

		// The farmer's loop stays alive while it is off, so both directions take effect on its next pass - a
		// minute at most. The old message promised a restart was needed, which stopped being true when the loop
		// was made to survive being switched off.
		return $"{bot.Name}: card farming {(on ? "on" : "off")}";
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
		if (Window == null) {
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

				return user == null
					? "rep4rep didn't answer. Check the token is right and that you can reach rep4rep.com."
					: $"rep4rep: {user.Value.Points} points you can spend, {user.Value.PendingPoints} still being verified.";
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
					sb.AppendLine($"  {t.TargetName,-24} \"{t.CommentText}\"");
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
			&& sub is "rest" or "clear" or "pause" or "resume" or "now" or "on" or "off") {
			int n = 0;

			foreach (Bot b in mgr.All) {
				Rep4RepModule? mm = BotManager.ModuleOf<Rep4RepModule>(b);

				if (mm == null) {
					continue;
				}

				switch (sub) {
					case "rest": await mm.RestFullDayAsync("manual reset").ConfigureAwait(false); break;
					case "clear": await mm.ClearHoldAsync().ConfigureAwait(false); break;
					case "pause": mm.Paused = true; break;
					case "resume": mm.Paused = false; break;
					case "now": mm.RunNow(); break;
					case "on": case "off": b.Cfg.Rep4Rep = sub == "on"; ConfigStore.SaveBot(b.Name, b.Cfg); if (sub == "on") await mm.StartAsync().ConfigureAwait(false); break;
				}

				n++;
			}

			return $"rep4rep {sub}: {n} account(s)";
		}

		if (who == null) {
			return $"rep4rep {sub} <account>";
		}

		Bot? target = mgr.Get(who);

		if (target == null) {
			return NoSuchAccount(mgr, who);
		}

		Rep4RepModule? mod = BotManager.ModuleOf<Rep4RepModule>(target);

		switch (sub) {
			case "on":
			case "off":
				target.Cfg.Rep4Rep = sub == "on";
				ConfigStore.SaveBot(target.Name, target.Cfg);

				// Only ever START. The module's loop stays alive and reads the flag itself, so stopping it here
				// would kill the very loop that has to notice the flag being turned back on later.
				if (mod != null && sub == "on") {
					await mod.StartAsync().ConfigureAwait(false);
				}

				return $"{target.Name}: rep4rep {sub}";
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
				if (mod != null) {
					await mod.ClearHoldAsync().ConfigureAwait(false);
				}

				return $"{target.Name}: hold cleared, refused profiles forgotten";
			case "rest":
				if (mod != null) {
					await mod.RestFullDayAsync("manual reset").ConfigureAwait(false);
				}

				return $"{target.Name}: rep4rep resting a full day, back at baseline after";
			default:
				return "rep4rep status | points | profiles | tasks <account> | on/off/now/pause/resume/clear/rest <account|all>";
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
				sb.AppendLine($"    {def.Name.PadRight(width)}{Settings.Show(config, def)}");
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

	private static string Set(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "set <key> <value>            change a global setting\nset <account> <key> <value>  change one account's setting";
		}

		// 'set <account> <key> <value>' only when the first word really is an account AND a key follows.
		Bot? bot = mgr.Get(args[0]);

		if (bot != null && args.Length >= 3 && Settings.FindBot(args[1]) != null) {
			SettingDef def = Settings.FindBot(args[1])!;
			bool wasLegit = bot.Cfg.LegitMode;
			string? error = Settings.Apply(bot.Cfg, def, Unquote(string.Join(' ', args[2..])));

			if (error != null) {
				return error;
			}

			Settings.ApplyLegitMode(bot.Cfg, wasLegit);

			// Raising a "shortest" above its "longest" (or the reverse) used to be accepted and written to disk.
			// The dashboard fixed one such pair; this fixes all of them, on both paths.
			List<string> pulled = Settings.FixRanges(bot.Cfg, def.Name);

			ConfigStore.SaveBot(bot.Name, bot.Cfg);
			ApplyBotSideEffects(bot, def);

			return $"{bot.Name}.{def.Name} = {Settings.Show(bot.Cfg, def)}"
				+ (def.NeedsRestart ? "   (applies after a restart)" : "")
				+ (pulled.Count > 0 ? Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", pulled) : "");
		}

		// A real account name followed by something that isn't a setting: the complaint is about the SETTING, not
		// about the account. Falling through to the global branch here reported "there's no setting called 'new'",
		// which points at the one part of the line that was correct.
		if ((bot != null) && (args.Length >= 3)) {
			return $"There's no per-account setting called '{args[1]}'. 'config {bot.Name}' lists them all.";
		}

		SettingDef? globalDef = Settings.FindGlobal(args[0]);

		if (globalDef == null) {
			SettingDef? asBot = Settings.FindBot(args[0]);

			return asBot != null
				? $"'{asBot.Name}' is a per-account setting. Try:  set <account> {asBot.Name} <value>"
				: $"There's no setting called '{args[0]}'. 'config' lists the global ones, 'config <account>' the per-account ones.";
		}

		string? failure = Settings.Apply(mgr.Global, globalDef, Unquote(string.Join(' ', args[1..])));

		if (failure != null) {
			return failure;
		}

		ConfigStore.SaveGlobal(mgr.Global);
		mgr.ApplyGlobal(mgr.Global);
		ApplyGlobalSideEffects(mgr, globalDef);

		return $"{globalDef.Name} = {Settings.Show(mgr.Global, globalDef)}"
			+ (globalDef.NeedsRestart ? "   (applies after a restart)" : "");
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
			// A gift left for the owner while its switch was off is taken once the switch is on - now, not at the
			// gifts module's next slow pass hours later.
			case "AcceptGifts":
			case "AcceptGiftedGames":
				BotManager.ModuleOf<Gifts>(bot)?.LookAgain();

				break;
			case "Enabled":
				// "Disabled" has to actually stop it. It used to keep farming and commenting while the dashboard
				// said disabled, which is the worst kind of wrong.
				if (!bot.Cfg.Enabled) {
					_ = bot.StopAsync();
				} else if (bot.State == BotState.Stopped) {
					_ = bot.StartAsync();
				}

				break;
		}
	}

	public static void ApplyGlobalSideEffects(BotManager mgr, SettingDef def) {
		switch (def.Name) {
			case "MiniOnTop":
				Window?.RefreshOnTop();

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
					if (Live.Global.Rep4RepHoldUntil != null) {
						Live.Global.Rep4RepHoldUntil = null;
						Live.Global.Rep4RepHoldFrom = null;
						ConfigStore.SaveGlobal(Live.Global);
						Log.Info("rep4rep hold lifted - commenting resumes on its own schedule");
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
				Window?.Invalidate();
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

	private static async Task<string> ImportAsync(BotManager mgr, string[] args) {
		if (args.Length == 0 || !args[0].Equals("asf", StringComparison.OrdinalIgnoreCase)) {
			return "import asf [path to ASF's config folder] [force]\n  Leave the path out and nocat.farm looks for an ASF install nearby.\n  Add 'force' to overwrite accounts that already exist here.";
		}

		bool force = args.Any(static a => a.Equals("force", StringComparison.OrdinalIgnoreCase));
		string[] rest = args[1..].Where(static a => !a.Equals("force", StringComparison.OrdinalIgnoreCase)).ToArray();
		string? dir = rest.Length > 0 ? string.Join(' ', rest).Trim('"') : AsfImport.Detect();

		if (dir == null) {
			return "Couldn't find an ArchiSteamFarm install. Point at it directly:  import asf C:\\path\\to\\ArchiSteamFarm\\config";
		}

		if (!Directory.Exists(dir)) {
			return $"There's no folder at {dir}";
		}

		List<AsfImport.Candidate> preview = AsfImport.Preview(dir);

		if (preview.Count == 0) {
			return $"No ASF accounts found in {dir}";
		}

		AsfImport.Result result = AsfImport.Run(dir, mgr.Global, force);
		await mgr.SyncFromDiskAsync().ConfigureAwait(false);

		StringBuilder sb = new();
		sb.AppendLine($"Imported {result.Imported} account(s) from {dir}{(result.Skipped > 0 ? $", skipped {result.Skipped}" : "")}");

		foreach (string note in result.Notes) {
			sb.AppendLine("  " + note);
		}

		if (result.Imported > 0) {
			sb.AppendLine();
			sb.AppendLine("  Heads up: an imported account shares its Steam login token with ASF. Don't run both at");
			sb.AppendLine("  once on the same account - they'd take turns kicking each other off.");
			sb.AppendLine("  Start them with:  start all");
		}

		return sb.ToString().TrimEnd();
	}

	private static async Task<string> ReloadAsync(BotManager mgr) {
		mgr.ApplyGlobal(ConfigStore.LoadGlobal());
		await mgr.SyncFromDiskAsync().ConfigureAwait(false);

		return "Configs reloaded.";
	}

	private static string Logs(string[] args) {
		int n = args.Length > 0 && int.TryParse(args[0], out int parsed) ? Math.Clamp(parsed, 1, 500) : 30;

		return string.Join(Environment.NewLine, Log.Recent(n).Select(static e => $"{e.When:HH:mm:ss}  {e.Source,-12}{e.Text}"));
	}

	private static string StatsText(string[] args) {
		int hours = args.Length > 0 && int.TryParse(args[0], out int parsed) ? Math.Clamp(parsed, 1, 168) : 24;
		List<(DateTime Hour, int Cards, int Comments)> buckets = Stats.ByHour(hours);
		(int cards, int comments) = Stats.Totals(hours);

		if (cards + comments == 0) {
			return $"Nothing earned in the last {hours}h yet.";
		}

		StringBuilder sb = new();
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
