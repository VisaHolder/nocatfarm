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
public static class Commands {
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
		new("human", "[account] [week]", GroupPlaying, "What human mode is doing today, and what it played. Add 'week' to see the next seven days."),
		new("name", "<account> [text]", GroupPlaying, "Custom non-Steam game name shown instead of the real game. No text clears it."),
		new("persona", "<account> <state>", GroupPlaying, "online | offline | busy | away | snooze | invisible."),

		new("cards", "[account]", GroupCards, "What is still left to farm."),
		new("farm", "<account> on|off", GroupCards, "Turn trading-card farming on or off."),

		new("rep4rep", "status|points|profiles|tasks|on|off|now|pause|resume|clear", GroupRep4Rep, "Everything rep4rep. Run it bare for a summary.", "r4r"),

		new("redeem", "[account] <key> [key...]", GroupAccounts, "Activate product keys. Without an account it tries each in turn until one can use it.", "key"),
		new("send", "<account|all>", GroupCards, "Send an account's tradable items to the account listed under Trades.", "loot"),
		new("2fa", "<account>", GroupAccounts, "Show this account's Steam Guard code, if its authenticator is set up here.", "guard"),
		new("cheevo", "<account> <appID> [list|unlock|lock] [name|all]", GroupPlaying, "Achievements: see them, unlock them all, or put them back.", "ach|achievements"),

		new("import", "asf [path] [force]", GroupSettings, "Bring accounts across from ArchiSteamFarm, login tokens and all."),
		new("config", "[account]", GroupSettings, "Show every setting and its current value."),
		new("set", "[account] <key> <value>", GroupSettings, "Change a setting. Without an account name it changes a global one."),
		new("reload", "", GroupSettings, "Re-read every config file from disk."),

		new("log", "[count]", GroupOther, "The last few log lines.", "logs"),
		new("stats", "[hours]", GroupOther, "Cards dropped and comments posted, by hour."),
		new("report", "", GroupOther, "Write the daily summary - hours banked, cards, comments, totals - to the log now."),
		new("answer", "<text>", GroupOther, "Answer whatever nocat.farm is waiting on - a Steam Guard code, or a password."),
		new("tutorial", "[topic]", GroupOther, "Getting started, in order, ticking off what you have already done.", "guide|setup"),
		new("help", "[command|setting]", GroupOther, "This list, or what one command or setting does.", "?|h"),
		new("theme", "[dark|light]", GroupOther, "Switch the dashboard between the dark and light themes. Without an argument it says which is on.", "dark|light"),
		new("version", "", GroupOther, "Which version this is.", "about"),
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
			Log.Debug($"couldn't open the dashboard: {e.GetType().Name}: {e.Message}");

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
				"stop" => await LifecycleAsync(mgr, rest, "stop").ConfigureAwait(false),
				"pause" => await LifecycleAsync(mgr, rest, "pause").ConfigureAwait(false),
				"resume" => await LifecycleAsync(mgr, rest, "resume").ConfigureAwait(false),
				"restart" => await RestartAsync(mgr, rest).ConfigureAwait(false),
				"play" => Play(mgr, rest),
				"grind" => Grind(mgr, rest),
				"human" => Human(mgr, rest),
				"redeem" or "key" => await RedeemAsync(mgr, rest).ConfigureAwait(false),
				"send" or "loot" => await SendAsync(mgr, rest).ConfigureAwait(false),
				"2fa" or "guard" => TwoFactor(mgr, rest),
				"cheevo" or "ach" or "achievements" => await CheevoAsync(mgr, rest).ConfigureAwait(false),
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
				"exit" or "quit" or "q" => Exit(),
				_ => Suggest(cmd)
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
		"""
		nocat.farm 1.0.0 - Steam idler, trading-card farmer and rep4rep commenter.
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
				if (!string.IsNullOrEmpty(m.Status) && m.Status is not ("idle" or "off")) {
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

	private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

	private static async Task<string> LifecycleAsync(BotManager mgr, string[] args, string verb) {
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

		foreach (Bot bot in targets.ToArray()) {
			switch (verb) {
				case "start":
					if (!bot.Cfg.Enabled) {
						continue;
					}

					await bot.StartAsync().ConfigureAwait(false);

					break;
				case "stop":
					await bot.StopAsync().ConfigureAwait(false);

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

		return all ? $"{verb}: {count} account(s)" : $"{args[0]}: {verb}";
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
	/// <summary>
	/// What human mode is up to.
	///
	/// This exists because "online" told you nothing. You could not see which weighted game it had picked, how
	/// long it meant to stay there, or how much of the day was left - so there was no way to tell a working
	/// schedule from a broken one. Now you can read the whole day off one screen.
	/// </summary>
	private static string Human(BotManager mgr, string[] args) {
		bool week = args.Any(static a => a.Equals("week", StringComparison.OrdinalIgnoreCase));
		string? name = args.FirstOrDefault(static a => !a.Equals("week", StringComparison.OrdinalIgnoreCase));

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
			HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);

			if (!bot.Cfg.LegitMode || (human == null)) {
				sb.AppendLine($"{bot.Name}: human mode is off");

				continue;
			}

			sb.AppendLine($"{bot.Name}  ({bot.Cfg.SteamLogin})");
			sb.AppendLine($"  right now   {human.Status}");

			if (human.TargetMinutesToday > 0) {
				int pct = human.TargetMinutesToday == 0 ? 0 : human.PlayedMinutesToday * 100 / human.TargetMinutesToday;
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

		StringBuilder sb = new();

		foreach (string key in keys) {
			sb.AppendLine(await Redeeming.RedeemAcrossAsync(targets, key).ConfigureAwait(false));
		}

		return sb.ToString().TrimEnd();
	}

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

		foreach (Bot bot in targets) {
			bot.StartGrind(appId, how);
			Log.Info($"grinding {GameNames.Of(appId)} for {Fmt.Hm((int) how.TotalMinutes)} - normal schedule resumes after", bot.Name);
		}

		return $"{string.Join(", ", targets.Select(static b => b.Name))}: {GameNames.Of(appId)} for {Fmt.Hm((int) how.TotalMinutes)}.";
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

		Log.Reward($"{message} in {GameNames.Of(appId)}", bot.Name);

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

		return $"{bot.Name}: card farming {(on ? "on (takes effect on the next start)" : "off")}";
	}

	private static string Cards(BotManager mgr, string[] args) {
		IEnumerable<Bot> bots = args.Length > 0 ? (mgr.Get(args[0]) is Bot b ? [b] : []) : mgr.All;
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
					sb.AppendLine($"{bot.Name,-14}{(bot.Cfg.Rep4Rep ? "on " : "off")}  {m.PostsToday}/{m.Cap} in 24h   last {last,-10} {m.Status}");
				}

				return sb.ToString().TrimEnd();
			}
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

				return $"{target.Name}: will post as soon as the daily cap allows.";
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
			default:
				return "rep4rep status | points | profiles | tasks <account> | on <account> | off <account> | now <account> | pause <account> | resume <account> | clear <account>";
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

	private static string Set(BotManager mgr, string[] args) {
		if (args.Length < 2) {
			return "set <key> <value>            change a global setting\nset <account> <key> <value>  change one account's setting";
		}

		// 'set <account> <key> <value>' only when the first word really is an account AND a key follows.
		Bot? bot = mgr.Get(args[0]);

		if (bot != null && args.Length >= 3 && Settings.FindBot(args[1]) != null) {
			SettingDef def = Settings.FindBot(args[1])!;
			bool wasLegit = bot.Cfg.LegitMode;
			string? error = Settings.Apply(bot.Cfg, def, string.Join(' ', args[2..]));

			if (error != null) {
				return error;
			}

			Settings.ApplyLegitMode(bot.Cfg, wasLegit);
			ConfigStore.SaveBot(bot.Name, bot.Cfg);
			ApplyBotSideEffects(bot, def);

			return $"{bot.Name}.{def.Name} = {Settings.Show(bot.Cfg, def)}"
				+ (def.NeedsRestart ? "   (applies after a restart)" : "");
		}

		SettingDef? globalDef = Settings.FindGlobal(args[0]);

		if (globalDef == null) {
			SettingDef? asBot = Settings.FindBot(args[0]);

			return asBot != null
				? $"'{asBot.Name}' is a per-account setting. Try:  set <account> {asBot.Name} <value>"
				: $"There's no setting called '{args[0]}'. 'config' lists the global ones, 'config <account>' the per-account ones.";
		}

		string? failure = Settings.Apply(mgr.Global, globalDef, string.Join(' ', args[1..]));

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
			case "IdleGames":
			case "CustomGameName":
			case "PlayWhileFarming":
			case "BlacklistedGames":
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
