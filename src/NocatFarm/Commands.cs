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
		new("pause", "<account|all>", GroupAccounts, "Stay logged in but stop playing, farming and commenting."),
		new("resume", "<account|all>", GroupAccounts, "Undo a pause."),
		new("add", "<name> <steamLogin>", GroupAccounts, "Add an account. It asks for the password once, then remembers a login token."),
		new("remove", "<account>", GroupAccounts, "Delete an account and its stored login token.", "delete"),
		new("enable", "<account>", GroupAccounts, "Let this account log in again."),
		new("disable", "<account>", GroupAccounts, "Keep the account configured but never log it in."),

		new("play", "<account> <appIDs|none>", GroupPlaying, "Set the games this account idles for playtime."),
		new("grind", "<account|all> <appID> <hours> | <account> off", GroupPlaying,
			"Put an account on one game for a set number of hours, then let it go back to whatever it was doing. Outranks human mode while it runs."),
		new("human", "[account] [week|reroll]", GroupPlaying, "What human mode is doing today, and what it played. Add 'week' to see the next seven days, or 'reroll' to throw today's plan away and roll a fresh one from the current settings."),
		new("wake", "<account>", GroupPlaying, "Wake a sleeping human-mode account and start its day now. Bed time is unchanged."),
		new("name", "<account> [text]", GroupPlaying, "Custom non-Steam game name shown instead of the real game. No text clears it."),
		new("persona", "<account> <state>", GroupPlaying, "online | offline | busy | away | snooze | invisible."),

		new("cards", "[account]", GroupCards, "What is still left to farm."),
		new("farm", "<account> on|off", GroupCards, "Turn trading-card farming on or off."),

		new("rep4rep", "status|points|profiles|tasks|on|off|now|pause|resume|clear|rest", GroupRep4Rep, "Everything rep4rep. Run it bare for a summary.", "r4r"),

		new("redeem", "[account] <key|file.txt> [key...]", GroupAccounts, "Activate product keys - or point it at a text file full of them. More than five queues itself and activates them slowly. Without an account it tries each in turn until one can use it.", "key"),
		new("send", "<account|all>", GroupCards, "Send an account's tradable items to the account listed under Trades.", "loot"),
		new("2fa", "<account>", GroupAccounts, "Show this account's Steam Guard code, if its authenticator is set up here.", "guard"),
		new("cheevo", "<account> <appID> [list|unlock|lock] [name|all]", GroupPlaying, "Achievements: see them, unlock them all, or put them back.", "ach|achievements"),
		new("hunt", "[account]", GroupPlaying, "What the achievement hunter would play, in order - and what it ruled out and why.", "boost"),
		new("match", "[do]", GroupCards, "Swap duplicate trading cards between your own accounts so sets finish. Shows what it would trade; 'match do' sends the offers."),
		new("keys", "[list|clear]", GroupAccounts, "Product keys waiting to be activated. A big batch queues itself rather than burning Steam's per-account activation allowance all at once."),
		new("value", "[account|all] [refresh]", GroupCards, "What each inventory is worth, by game, and how it has moved in the last day. Add 'refresh' to read the inventories again.", "inv|inventory"),

		new("import", "asf [path] [force]", GroupSettings, "Bring accounts across from ArchiSteamFarm, login tokens and all."),
		new("config", "[account]", GroupSettings, "Show every setting and its current value."),
		new("set", "[account] <key> <value>", GroupSettings, "Change a setting. Without an account name it changes a global one."),
		new("reload", "", GroupSettings, "Re-read every config file from disk."),

		new("log", "[count]", GroupOther, "The last few log lines.", "logs"),
		new("stats", "[hours]", GroupOther, "Cards dropped and comments posted, by hour."),
		new("plugins", "", GroupOther, "Which plugins are loaded, and where they came from."),
		new("owns", "<appID|name>", GroupOther,
			"Which accounts already own a game, and how long each has played it. Takes an appID, a store URL, or part of a name."),
		new("addlicense", "<account|all> <subIDs>", GroupOther,
			"Add free packages (subIDs) to an account's library. Only works for genuinely free licences - a paid one is refused by Steam."),
		new("report", "", GroupOther, "Write the daily summary - hours banked, cards, comments, totals - to the log now."),
		new("answer", "<text>", GroupOther, "Answer whatever nocat.farm is waiting on - a Steam Guard code, or a password."),
		new("tutorial", "[topic]", GroupOther, "Getting started, in order, ticking off what you have already done.", "guide|setup"),
		new("help", "[command|setting]", GroupOther, "This list, or what one command or setting does.", "?|h"),
		new("theme", "[dark|light]", GroupOther, "Switch the dashboard between the dark and light themes. Without an argument it says which is on.", "dark|light"),
		new("version", "", GroupOther, "Which version this is.", "about"),
		new("update", "[now]", GroupOther, "Check for a newer release. 'update now' downloads it and restarts into it - nothing updates on its own, ever."),
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
		bool now = args.Any(static a => a.Equals("now", StringComparison.OrdinalIgnoreCase));

		await UpdateCheck.LookAsync(force: true).ConfigureAwait(false);

		if (UpdateCheck.Available == null) {
			return $"You're on the newest release ({Build.Version}).";
		}

		if (!now) {
			return $"{UpdateCheck.Available} is out - you have {Build.Version}."
				+ Environment.NewLine + $"  {UpdateCheck.Url}"
				+ Environment.NewLine + "  'update now' downloads it and restarts into it. Nothing updates on its own.";
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
	private static async Task<string> AddLicense(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "addlicense <account|all> <subIDs>     comma or space separated";
		}

		List<Bot> targets = args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
			? mgr.All.Where(static b => b.IsOnline).ToList()
			: mgr.Get(args[0]) is { } one ? [one] : [];

		if (targets.Count == 0) {
			return args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
				? "No account is online."
				: NoSuchAccount(mgr, args[0]);
		}

		List<uint> subs = string.Join(' ', args[1..])
			.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static s => uint.TryParse(s, out uint n) ? n : 0)
			.Where(static n => n > 0)
			.Distinct()
			.ToList();

		if (subs.Count == 0) {
			return "No subID in that. They are numbers - 12345, or several separated by commas.";
		}

		StringBuilder sb = new();

		foreach (Bot bot in targets) {
			if (!bot.IsOnline) {
				sb.AppendLine($"{bot.Name}: not online");

				continue;
			}

			foreach (uint sub in subs) {
				if (bot.OwnsPackage(sub)) {
					sb.AppendLine($"{bot.Name}: {sub} - already has it");

					continue;
				}

				bool ok = await FreeGames.AddPackageAsync(bot, sub, CancellationToken.None).ConfigureAwait(false);
				sb.AppendLine($"{bot.Name}: {sub} - {(ok ? "added" : "refused (not free, region-locked, or already gone)")}");
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
					bot.Pause();

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
			"pause" => "paused - staying signed in, but not playing, farming or commenting",
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
			return "add <name> <steamLogin>\n  name       a nickname just for you - it names the config file\n  steamLogin what you type into Steam's sign-in box";
		}

		string name = args[0];

		if (!ConfigStore.IsValidBotName(name)) {
			return "That name can't be used. Letters, numbers, dashes and underscores - and 'nocatFarm' is taken by the global config.";
		}

		if (mgr.Get(name) != null) {
			return $"'{name}' already exists.";
		}

		Bot? bot = await mgr.AddAsync(name, new BotConfig { SteamLogin = args[1] }).ConfigureAwait(false);

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

		return $"Added '{name}' ({args[1]}). It will ask for the password and a Steam Guard code once, then remember this account.{andThen}";
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

		foreach (Bot bot in bots) {
			inventories[bot] = await Looting.InventoryAsync(bot).ConfigureAwait(false);
		}

		List<Matching.Swap> swaps = Matching.Plan(inventories);

		if (swaps.Count == 0) {
			return "No swaps to make - no account has a spare card that another one is missing.";
		}

		List<string> lines = [];

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

			lines.Add(ok ? $"      offer sent - {swap.To.Name} needs to accept it" : $"      couldn't send - {message}");
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
			string lead = delay > TimeSpan.Zero ? $" (finishing up first, starts in ~{Fmt.Hm((int) Math.Ceiling(delay.TotalMinutes))})" : "";
			Log.Info(new Said("grinding {0} for {1}{2} - normal schedule resumes after", GameNames.Of(appId), Fmt.Hm((int) how.TotalMinutes), lead), bot.Name);
		}

		string no = refused.Count > 0
			? $"{(started.Count > 0 ? "  " : "")}{string.Join(", ", refused.Select(static b => b.Name))}: skipped - {GameNames.Of(appId)} is still refundable, and a grind would spend that."
			: "";

		return started.Count > 0
			? $"{string.Join(", ", started.Select(static b => b.Name))}: {GameNames.Of(appId)} for {Fmt.Hm((int) how.TotalMinutes)}.{no}"
			: no.TrimStart();
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
			return "name <account> [text]   (no text clears it)";
		}

		Bot? bot = mgr.Get(args[0]);

		if (bot == null) {
			return NoSuchAccount(mgr, args[0]);
		}

		bot.Cfg.CustomGameName = string.Join(' ', args[1..]);
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

			foreach (FarmTarget g in farmer.Queue.Take(15)) {
				sb.AppendLine($"    {g.CardsRemaining,3} card(s)  {g.HoursPlayed,6:0.0}h  {g.GameName}");
			}
		}

		return sb.Length == 0 ? "Nothing is farming." : sb.ToString().TrimEnd();
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

		foreach (string section in defs.Select(static d => d.Section).Distinct()) {
			List<SettingDef> shown = defs.Where(d => (d.Section == section) && (showAdvanced || !d.Advanced)).ToList();
			hidden += defs.Count(d => (d.Section == section) && d.Advanced && !showAdvanced);

			if (shown.Count == 0) {
				continue;
			}

			sb.AppendLine();
			sb.AppendLine($"  {section.ToUpperInvariant()}");

			foreach (SettingDef def in shown) {
				sb.AppendLine($"    {def.Name,-28}{Settings.Show(config, def)}");
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
			// The status text this program writes about itself is translated too, so a language change has to
			// reach the pack the modules read from - not only the one the browser fetches.
			// "Hold for N days" is turned into a deadline exactly once, here, when the number changes.
			//
			// Counting days down at runtime would restart the count on every launch, so a two-day hold on a
			// machine that reboots nightly would never end. A stored deadline cannot drift: it either has
			// passed or it has not.
			case "Rep4RepPauseDays": {
				int days = Live.Global.Rep4RepPauseDays;

				if (days <= 0) {
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
				DateTime until = from.AddDays(days);

				if ((Live.Global.Rep4RepHoldFrom != from) || (Live.Global.Rep4RepHoldUntil != until)) {
					Live.Global.Rep4RepHoldFrom = from;
					Live.Global.Rep4RepHoldUntil = until;
					ConfigStore.SaveGlobal(Live.Global);
					Log.Info(new Said("rep4rep commenting held for {0} day(s) - back on {1}",
						days, (Func<string>) (() => Fmt.Clock(until))));
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
