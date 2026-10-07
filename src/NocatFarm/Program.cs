using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NocatFarm;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Web;
using NocatFarm.Windows;

// ─────────────────────────────────────────────────────────────────────────────
//  nocatFarm - Steam idler, trading-card farmer and rep4rep commenter.
//
//  The console is the product; the dashboard is the same product with a mouse. Both drive one command router
//  and one settings registry, so anything you can click you can type, and the other way round.
// ─────────────────────────────────────────────────────────────────────────────

string root = AppContext.BaseDirectory;
bool forceNoWeb = false;
bool forceNoTray = false;
bool startMinimized = false;
bool forceNoGui = false;
bool quitRunning = false;
bool ping = false;
bool steamSelfTest = false;
List<string>? setupChoices = null;

for (int i = 0; i < args.Length; i++) {
	switch (args[i].ToLowerInvariant()) {
		case "--path" when i + 1 < args.Length:
			root = args[++i];

			break;
		case "--no-web":
			forceNoWeb = true;

			break;
		case "--no-tray":
			forceNoTray = true;

			break;
		case "--no-gui":
		case "--console":
			forceNoGui = true;

			break;
		case "--minimized":
		case "--background":
			startMinimized = true;

			break;

		// For the installer: close the copy running from this folder cleanly (before files are replaced, or on
		// uninstall), or save the choices made in the setup wizard - Setting=value pairs, checked like 'set' checks
		// them. Neither starts the app.
		case "--quit":
			quitRunning = true;

			break;
		// For Docker's HEALTHCHECK (the image has no curl or wget): is the dashboard of the copy for this folder answering?
		// Exits 0 when it is (or when the dashboard is switched off), 1 when it isn't. Starts nothing.
		case "--ping":
			ping = true;

			break;
		// For the release tests: the real connection to Steam and the games-played message the idler sends, signed in
		// anonymously - no account involved. Prints PASS/FAIL lines, exits 0 when all passed. Starts nothing else.
		case "--steam-selftest":
			steamSelfTest = true;

			break;
		case "--setup":
			setupChoices = [.. args.Skip(i + 1).Where(static a => a.Contains('='))];
			i = args.Length;

			break;
		case "--help":
		case "-h":
			if (OperatingSystem.IsWindows()) {
				NativeConsole.Attach();
			}

			Console.WriteLine("""
				nocatFarm [options]
				  --path <dir>    where config/ and logs/ live (default: next to the exe)
				  --no-web        don't start the dashboard, whatever the config says
				  --no-tray       don't create a notification-area icon
				  --no-gui        no window - the plain console board instead
				  --minimized     start hidden, straight to the tray
				  --steam-selftest  connect to Steam anonymously (no account), send a games-played message, exit 0 if it all works
				""");

			return 0;
	}
}

try {
	Console.OutputEncoding = Encoding.UTF8;
	Console.Title = "nocat.farm";
} catch {
	// a redirected or unusual console - harmless
}

// No console exists yet - this is a windowed binary on purpose. Make one only for the runs that need it:
// no window wanted, or no window possible.
bool wantWindow = OperatingSystem.IsWindows() && !forceNoGui;

if (!wantWindow && OperatingSystem.IsWindows()) {
	NativeConsole.Attach(interactive: true);
}

try {
	ConfigStore.UseRoot(root);
} catch (Exception e) when (e is UnauthorizedAccessException or IOException) {
	// Extracted somewhere Windows won't let a program write, like Program Files. A windowed exe has nowhere to say
	// that yet, so a double-click used to do nothing at all.
	if (OperatingSystem.IsWindows()) {
		NativeConsole.Attach();
	}

	Console.WriteLine();
	Console.WriteLine($"  nocat.farm can't write its settings next to itself: {e.Message}");

	// Said for the machine it's on: on Linux or a Mac the usual cause is a folder that belongs to another user (unzipped
	// with sudo, or /opt), and "move it to Documents" was Windows advice that fixes nothing there.
	if (OperatingSystem.IsWindows()) {
		Console.WriteLine("  Move the folder somewhere you can write to - Desktop, Documents or its own folder on another drive.");
	} else {
		Console.WriteLine($"  The folder isn't writable by this user ({Environment.UserName}). Give it to this user -");
		Console.WriteLine($"  sudo chown -R {Environment.UserName}: \"{Path.GetFullPath(root)}\" - or run nocat.farm as its owner.");
	}

	Console.WriteLine();

	if (OperatingSystem.IsWindows()) {
		await Task.Delay(8000).ConfigureAwait(false);
	}

	return 1;
}

if (ping) {
	return await Platform.PingAsync().ConfigureAwait(false);
}

if (steamSelfTest) {
	// A windowed exe run from a terminal has nowhere to print; one whose output is captured already does.
	if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) {
		NativeConsole.Attach();
	}

	return await SteamSelfTest.RunAsync().ConfigureAwait(false);
}

if (quitRunning) {
	// Nothing running (or a copy from before this existed): nothing to wait for.
	return !AppInstance.Signal(ConfigStore.Root, "quit") || AppInstance.WaitUntilClosed(ConfigStore.Root, TimeSpan.FromSeconds(120)) ? 0 : 1;
}

if (setupChoices != null) {
	return SetupChoices.Run(setupChoices);
}

// A crash on any thread lands in the log, not nowhere. Only the ones that end the process get here.
AppDomain.CurrentDomain.UnhandledException += static (_, e) => {
	// Into the day's log first, where the lines leading up to it are - crash.log alone left the crash in one file
	// and everything that explains it in another. Then crash.log, which also catches one before logging is set up.
	try {
		if (e.ExceptionObject is Exception crash) {
			Log.Crash(new Said("crashed: {0}", Log.Describe(crash)), crash);
		}
	} catch {
		// on to crash.log
	}

	try {
		string logs = Path.Combine(ConfigStore.Root, "logs");
		Directory.CreateDirectory(logs);
		File.AppendAllText(Path.Combine(logs, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Log.Scrub(e.ExceptionObject?.ToString())}{Environment.NewLine}");
	} catch {
		// nothing more can be done from here
	}

	// A new version that crashes while it's being tried out goes straight back to the old one.
	NocatFarm.Core.SelfUpdate.ReportCrashed();
};

// A fire-and-forget task that throws doesn't crash anything - its exception just waits, unseen, until the task is
// garbage-collected and then vanishes. There are dozens of those (Task.Run from a click, a timer, a callback), so
// this is the one place they can all be caught: written down with the stack, and marked seen.
TaskScheduler.UnobservedTaskException += Log.OnUnobservedTask;

// One instance per config folder. Two copies running the same accounts share a Steam login ID, so they take
// turns kicking each other off - and they put two icons in the tray, which is how you notice. A named mutex on
// Windows, a locked config/state/instance.lock everywhere else (see AppInstance.TryClaim).
using IDisposable? singleInstance = AppInstance.TryClaim(ConfigStore.Root);

if (singleInstance == null) {
	// Opened again - from the Start menu, say - while it's running: the running one comes to the front, and this one
	// goes quietly. Only a copy from before that existed gets the message below.
	if (AppInstance.Signal(ConfigStore.Root, "show")) {
		return 0;
	}

	// This runs before any window exists and, in a GUI launch, before any console does either - so without
	// somewhere to say it, a second launch was a silent four-second no-op that looked like the exe was broken.
	if (OperatingSystem.IsWindows()) {
		NativeConsole.Attach();
	}

	Console.WriteLine();
	Console.WriteLine("  nocatFarm is already running for this folder.");

	if (OperatingSystem.IsWindows()) {
		Console.WriteLine("  Look for its icon by the clock, or close the other one first.");
	} else {
		int other = AppInstance.HolderPid(ConfigStore.Root);
		Console.WriteLine(other > 0
			? $"  It's process {other}. Close that one first, or start this one with its own --path folder."
			: "  Close the other one first, or start this one with its own --path folder.");
	}

	Console.WriteLine();
	Console.WriteLine("  (Two copies would share a Steam login and keep signing each other out.)");

	// The pause is for a window that would otherwise vanish before it's read - a terminal keeps the lines anyway.
	if (OperatingSystem.IsWindows()) {
		await Task.Delay(4000).ConfigureAwait(false);
	}

	return 1;
}

// Back on a version after going back to an older one: the settings the older one didn't know, and so left out when it
// saved, are put back before anything reads them - only those (see Rollback). Said once the log is open.
// Nothing in it may stop nocat.farm starting: whatever it didn't expect is said, and the settings load as they are.
List<Rollback.Note> broughtBack;

try {
	broughtBack = Rollback.RestoreMissing();
} catch (Exception e) {
	broughtBack = [
		new Rollback.Note(true, new Said("couldn't bring back the settings from {0} ({1}) - it tries again next start", Path.GetFileName(Rollback.Folder), Log.Scrub(e.Message))),
		new Rollback.Note(false, default, $"rollback: {Log.Describe(e)}")
	];
}

GlobalConfig global = ConfigStore.LoadGlobal();
Live.Global = global;
Log.Configure(global.FileLogging, global.Debug, root, global.LogRetentionDays);

foreach (Rollback.Note note in broughtBack) {
	if (note.Detail != null) {
		Log.Debug(note.Detail);
	} else if (note.Problem) {
		Log.Warn(note.Line);
	} else {
		Log.Good(note.Line);
	}
}

// Said before there was a file to say it in - so written again, now that there is one.
if (ConfigStore.GlobalLoadProblem is { } configProblem) {
	Log.Debug(configProblem);
}

// A brand-new config took the computer's language: said once, in it, with where to change it.
if (ConfigStore.LanguageFromComputer is { } fromComputer) {
	Log.Info(new Said("language set to {0} from this computer - change it in Settings", SystemLanguage.NameOf(fromComputer)));
}

Banner();

// NOCATFARM_WEB_HOST / _PORT / _PASSWORD(_FILE), for Docker. Nothing happens unless one is set.
Platform.ApplyEnvironment(global);

// Off Windows: a data folder that can't be written (a Docker bind mount made by root) is said now, with the fix.
// In Docker the backups folder is a volume of its own too, and can be made by root the same way.
Platform.CheckWritable(Platform.InContainer
	? [ConfigStore.ConfigDir, Path.Combine(ConfigStore.Root, "logs"), Backup.Folder]
	: [ConfigStore.ConfigDir, Path.Combine(ConfigStore.Root, "logs")]);

BotManager manager = new(global);
Commands.Host = manager;   // so a command sent by Steam message can reach the same engine the console does
await manager.SyncFromDiskAsync().ConfigureAwait(false);

// Keep the registry entry in step with the setting, in case the exe moved since it was last written.
if (OperatingSystem.IsWindows()) {
	WindowsIntegration.KeepInStep(global.StartWithWindows);
}

// Before anything else is said: if this start finishes an update, that's the first line in the window.
SelfUpdate.AnnounceIfJustUpdated();

// Installed with the setup: Windows' list of installed apps shows the version actually running, updates included.
if (OperatingSystem.IsWindows()) {
	InstallRecord.Refresh();
}

WebHost? web = null;

if (global.WebEnabled && !forceNoWeb) {
	web = new WebHost(manager, global);

	if (!await web.StartAsync().ConfigureAwait(false)) {
		await web.DisposeAsync().ConfigureAwait(false);
		web = null;
	}
}

CancellationTokenSource shutdown = new();
Commands.ExitHandler = () => shutdown.Cancel();

// Every way out goes through Commands.RequestExit (see Quit): the "closing" flag first, then the cancel. The tray's
// Exit, Ctrl+C, SIGTERM and "every account has finished" used to cancel on their own, the flag never went up, and an
// update part-way through signing the accounts out carried on - installed itself and started nocat.farm again after
// it had been closed. Anything that still cancels directly raises the flag here too.
shutdown.Token.Register(static () => Commands.RequestExit());

// Docker and systemd stop a program with SIGTERM. Left alone, .NET ends the process on it (and the dashboard's own
// host takes it as its cue to stop and leave the rest running), so the orderly sign-out below never happened and a
// `docker stop` waited out its timeout and killed it. Taken here, SIGTERM is the same clean shutdown as 'exit'.
// Not on Windows, where nothing sends it and closing works the way it always has.
using PosixSignalRegistration? sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
	ctx.Cancel = true;
	Quit();
});

// SIGHUP is closing the Terminal window on a Mac, or an SSH session ending: it used to end the process on the spot, with
// no sign-out and the last minutes of lifetime totals lost. Now it's the same clean shutdown - unless it was started to
// ignore it (nohup), which is somebody asking for it to keep running after they log out.
using PosixSignalRegistration? sighup = OperatingSystem.IsWindows() || Platform.HangupIgnored() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => {
	ctx.Cancel = true;
	Quit();
});

if (OperatingSystem.IsWindows()) {
	NativeConsole.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "nocatFarm.ico"));
}

// Told once, so anything that wants the dashboard asks Commands rather than carrying its own copy of the URL and
// its own Process.Start. Outside the tray block: it used to be set only when there was a tray icon, so with the
// tray off, 'add' never opened the dashboard however OpenDashboardAfterAdd was set.
Commands.DashboardUrl = () => web?.Url ?? "";

TrayIcon? tray = null;

// Whether there is a tray icon is the tray thread's to say, once the icon is really in the notification area (or isn't).
// Set here to "there is one" as soon as the object existed, it overwrote the icon's own "couldn't add it" - and a
// window started hidden stayed hidden, with no icon to bring it back.
if (global.Tray && !forceNoTray && OperatingSystem.IsWindows()) {
	tray = StartTray(manager, () => web?.Url ?? "");
}

if (OperatingSystem.IsWindows() && global.KeepAwake) {
	WindowsIntegration.KeepAwake(true);
}

// The window comes up FIRST, before a single account signs in.
//
// Logins are deliberately staggered - Steam rate-limits them per IP - so starting the accounts first meant the
// window did not appear until every one of them was in. With three accounts that is half a minute of nothing
// on screen, and with twenty it is minutes. The accounts now fill in behind a window that is already there.
//
// The window is the face of this on Windows; the console board is what you get without one (a headless run,
// another OS, or --no-gui). Only one of them ever owns the screen, so they can't fight over it.
MainWindow? window = null;
LiveConsole? board = null;
bool windowFailed = false;

if (wantWindow && OperatingSystem.IsWindows()) {
	window = new MainWindow(manager, () => web?.Url ?? "", Quit);

	// If it can't open, put the console log straight back rather than leaving a silent app behind.
	window.Failed += () => {
		windowFailed = true;
		Commands.Window = null;

		if (OperatingSystem.IsWindows()) {
			Log.Written -= Show;
		}

		Log.Suppressed = false;

		// There is no console to fall back into - the exe is windowed - so make one, or the app is invisible.
		if (OperatingSystem.IsWindows()) {
			NativeConsole.Attach(interactive: true);
		}
	};


	// DEBUG is always in the file; whether it is also on screen is the "Show debug detail" setting, and it is
	// read live so the toggle takes effect on the next line rather than the next restart. Left unfiltered this
	// window showed a wall of "reusing web token" and "243 licence(s) known" that buried the six lines actually
	// worth reading - the console and the dashboard's Log tab both already filtered it; this was the one surface
	// still showing it unasked.
	[SupportedOSPlatform("windows")]
	void Show(Log.Entry entry) {
		if ((entry.Level != "DEBUG") || Log.DebugEnabled) {
			window.Append(entry);
		}
	}

	// Take 40 lines the window will actually SHOW, not 40 entries of which it might show three.
	//
	// Filtering a fixed 40-entry backfill was the mistake: a startup burst is mostly DEBUG, so dropping those
	// left a window opening on two lines and looking like the log had been wiped. Count what survives.
	bool ShowsDebug = Log.DebugEnabled;
	List<Log.Entry> backfill = [];

	foreach (Log.Entry old in Log.Recent(600)) {
		if ((old.Level != "DEBUG") || ShowsDebug) {
			backfill.Add(old);
		}
	}

	foreach (Log.Entry old in backfill.Skip(Math.Max(0, backfill.Count - 40))) {
		Show(old);
	}

	Log.Written += Show;
	Log.Suppressed = true;
	Commands.Window = window;

	// Hidden at start only with a tray icon to bring it back - without one it was an app with no way to be seen.
	window.Start(!((global.StartMinimized || startMinimized) && (tray != null)));
	Ready(web?.Url, manager.All.Count);
} else {
	// Said BEFORE the board starts. The board repaints the bottom of the screen every second, and written after it
	// these raced its first paint - half the block drawn over, or the board's rows scribbled through the middle.
	// Written first, they sit above it and scroll away with the rest.
	Ready(web?.Url, manager.All.Count);

	if (manager.All.Count == 0) {
		FirstRunHint(web?.Url);
	}

	board = new LiveConsole(manager);
	board.Start();
	Commands.Board = board;
}

// A second launch brings this one to the front - the window, or the dashboard when there is no window - and the
// installer can ask it to close cleanly before it replaces files.
AppInstance.Listen(ConfigStore.Root,
	show: () => {
		if (OperatingSystem.IsWindows() && (window != null) && !windowFailed) {
			window.Show();
		} else if ((web != null) && Platform.HasDesktop) {
			OpenBrowser(web.Url);
		}
	},
	quit: Commands.RequestExit);

// Plugins load BEFORE any account signs in, so a plugin that subscribes to "account online" actually sees
// the first one rather than missing the whole fleet by a second.
await NocatFarm.Plugins.PluginHost.LoadAllAsync(manager, CancellationToken.None).ConfigureAwait(false);

// Before the accounts start, not after: they start spread out over a minute or more, and Telegram commands, the
// notifications of that startup, and an update's one-by-one sign-out all need to work from the first second.
NocatFarm.Core.Notifier.Start(manager);
NocatFarm.Core.SelfUpdate.NotifierReady();
NocatFarm.Core.DiscordPresence.Start(manager);
NocatFarm.Core.SelfUpdate.Fleet = () => manager.All;

// The day-by-day totals behind the dashboard's history charts. Before the accounts start, so the first card of the
// run is counted once, after what the short-term records already know has been filled in.
NocatFarm.Core.History.Start(manager);

if (manager.All.Count == 0) {
	// Said above the board already, where there is one.
	if (board == null) {
		FirstRunHint(web?.Url);
	}
} else {
	// Not waited on past a quit: the installer's --quit (or exit) while accounts are still queueing to sign in has to
	// close now, not after the last login slot. The accounts still queueing are stopped with the rest on the way out.
	await Task.WhenAny(manager.StartAllAsync(), Task.Delay(Timeout.Infinite, shutdown.Token)).ConfigureAwait(false);
}

// Closed while the accounts were starting: none of what follows is wanted any more. Started anyway, the update timer,
// the router's port forward and a browser tab all came up in the seconds the app was on its way out.
if (!shutdown.IsCancellationRequested) {
	// Once-a-day "what did the fleet bank overnight" summary to the log (default 09:30). Self-scheduling; no-ops
	// with no accounts. Type `report` to see it on demand.
	NocatFarm.Core.DailyReport.Start(manager);

	// The same once a week (off unless switched on), and the stuck-account alarm - both look once a minute.
	NocatFarm.Core.WeeklyReport.Start(manager);
	NocatFarm.Core.StuckWatch.Start(manager);

	// Looking for a new version, the hourly reminder and "Update by itself" - for the app, not for an account, so it
	// happens with no account signed in too.
	NocatFarm.Core.UpdateCheck.Start(manager);

	// "Open from anywhere": the router forwards the dashboard's port, while the switch is on.
	NocatFarm.Core.RemoteAccess.Start();

	// "Count me as a user": the hourly ping behind "1,240 Steam accounts on nocat.farm". Sends nothing while it's off.
	NocatFarm.Core.UserCount.Start();
}

// 1.5.9's "Tell me if nocat.farm stops" (removed in 1.6.0) left its random id behind; nothing reads it any more.
try {
	File.Delete(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "state", "stop-alert-id.txt"));
} catch (IOException) {
	// in use or gone - it's two lines of nothing
} catch (UnauthorizedAccessException) {
	// the same
}


// Only where there's a desktop: a server or a container has no browser, and the attempt is just a baffling error.
// And not when it starts hidden (with Windows) or has just restarted itself into an update - a browser tab popping
// up then is something nobody asked for.
if (global.OpenBrowserOnStart && (web != null) && Platform.HasDesktop && !startMinimized && !NocatFarm.Core.SelfUpdate.OnTrial && !shutdown.IsCancellationRequested) {
	OpenBrowser(web.Url);
}

// The console is the only thing reading the keyboard, so a Steam Guard prompt and a typed command can never
// fight over stdin: whatever is typed goes to the prompt if one is waiting, and to the command router if not.
// Its own thread, because reading a key blocks it for as long as nobody types.
List<string> consoleHistory = [];

// The question that was up when the last line read started being typed (its first key) - see ConsoleLoop.
Prompt.Question? lineTypedFor = null;

// With a window there is no console to read from - it has its own command line - so the keyboard loop is only
// started when the console is still ours.
// The window creates itself on another thread, so whether it succeeded is not known yet. Give it a moment
// before deciding who owns the keyboard - otherwise a window that failed left a console nobody was reading,
// where typing did nothing at all.
if ((window != null) && OperatingSystem.IsWindows()) {
	for (int i = 0; (i < 40) && !windowFailed && !window.Visible; i++) {
		await Task.Delay(50).ConfigureAwait(false);
	}
}

Task console = (window != null) && !windowFailed
	? Task.Delay(Timeout.Infinite, shutdown.Token)
	: Task.Factory.StartNew(async () => {
		// Nothing awaits this until the very end, so a loop that threw left a console where typing did nothing,
		// and not a word about why.
		try {
			await ConsoleLoop(manager, shutdown).ConfigureAwait(false);
		} catch (OperationCanceledException) when (shutdown.IsCancellationRequested) {
			// closing
		} catch (Exception e) {
			Log.Failed("the console's keyboard loop stopped - typed commands won't be read", e);
			Log.StackToFile(e);
		}
	}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

Console.CancelKeyPress += (_, e) => {
	e.Cancel = true;
	Quit();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => Quit();

// ExitWhenAllFinished pairs with the per-account "log out when finished": a user who set both is asking for a
// finite run, and leaving the process parked in the tray (still blocking sleep) is not what they asked for.
try {
	while (!shutdown.IsCancellationRequested) {
		await Task.Delay(TimeSpan.FromSeconds(30), shutdown.Token).ConfigureAwait(false);

		if (manager.Global.ExitWhenAllFinished && manager.AllFinished) {
			Log.Good("every account has finished - closing down as configured");
			Quit();
		}
	}
} catch (OperationCanceledException) {
	// asked to stop
}

Log.Info("shutting down...");

// No more update looks, reminders or installs by itself from here - the timer is gone, not just ignored.
NocatFarm.Core.UpdateCheck.Stop();
NocatFarm.Core.UserCount.Stop();

// Closed straight after an update: that's somebody closing it, not the new version failing to start.
NocatFarm.Core.SelfUpdate.ConfirmStarted();

// Nothing left forwarded to a PC where nothing is listening.
await NocatFarm.Core.RemoteAccess.StopAsync().ConfigureAwait(false);

// Whatever notifications are still waiting go out first (a few seconds at most).
await NocatFarm.Core.Notifier.StopAsync().ConfigureAwait(false);
NocatFarm.Core.DiscordPresence.Stop();

// Plugins first, while the accounts, commands and their saved state still work - OnUnloadAsync is documented as
// "called on shutdown" and was never called at all.
await NocatFarm.Plugins.PluginHost.UnloadAllAsync().ConfigureAwait(false);

if (OperatingSystem.IsWindows()) {
	WindowsIntegration.KeepAwake(false);
	tray?.Dispose();

	// Where the window was left - it's never told it is closing on the way out, so it wouldn't save that itself.
	window?.SavePlace();
}

if (web != null) {
	await web.DisposeAsync().ConfigureAwait(false);
}

await manager.DisposeAsync().ConfigureAwait(false);
BotManager.Flush();   // persist the last few minutes of lifetime totals a clean exit would otherwise drop
await Task.WhenAny(console, Task.Delay(1000)).ConfigureAwait(false);

return 0;

// ─────────────────────────────────────────────────────────────────────────────
void Banner() {
	ConsoleColor prev = Console.ForegroundColor;
	bool box = Log.Bar != "|";

	try {
		Console.WriteLine();

		if (box) {
			const string Tagline = "Steam idling · trading cards · rep4rep";
			string Title = $"nocatFarm  {Build.Version}";
			int inner = Math.Max(Title.Length, Tagline.Length) + 4;   // 2 spaces of padding each side

			// Padding is computed, never hand-counted: a hand-counted box drifts the moment the text changes.
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			Console.WriteLine("  ╭" + new string('─', inner) + "╮");

			Console.Write("  │  ");
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write("nocat.");
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.Write("farm");
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Write($"  {Build.Version}");
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			Console.WriteLine(new string(' ', inner - Title.Length - 2) + "│");

			Console.Write("  │  ");
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Write(Tagline);
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			Console.WriteLine(new string(' ', inner - Tagline.Length - 2) + "│");

			Console.WriteLine("  ╰" + new string('─', inner) + "╯");
		} else {
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine($"  nocatFarm {Build.Version}");
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.WriteLine("  Steam idling, trading cards and rep4rep");
		}

		Console.WriteLine();
	} finally {
		Console.ForegroundColor = prev;
	}
}

/// <summary>A short "here's where everything is" block, rather than three log lines that scroll away.</summary>
void Ready(string? url, int accounts) {
	ConsoleColor prev = Console.ForegroundColor;

	try {
		void Row(string key, string value, ConsoleColor colour) {
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Write("   " + key.PadRight(12));
			Console.ForegroundColor = colour;
			Console.WriteLine(value);
		}

		if (url != null) {
			Row("dashboard", url, ConsoleColor.Cyan);
		}

		Row("accounts", accounts == 0 ? "none yet" : $"{accounts} configured", accounts == 0 ? ConsoleColor.DarkYellow : ConsoleColor.Gray);
		Row("commands", "type 'help'", ConsoleColor.Gray);
		Console.WriteLine();
	} finally {
		Console.ForegroundColor = prev;
	}
}

void FirstRunHint(string? url) {
	ConsoleColor prev = Console.ForegroundColor;

	try {
		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine("  No accounts yet. Add one:");
		Console.ForegroundColor = ConsoleColor.Cyan;
		Console.WriteLine("      add mybot mysteamlogin");
		Console.ForegroundColor = ConsoleColor.DarkGray;
		Console.WriteLine("  It asks for the password and a Steam Guard code once, then remembers the account.");

		if (url != null) {
			Console.WriteLine($"  Or do it in the dashboard: {url}");
		}

		Console.WriteLine();
	} finally {
		Console.ForegroundColor = prev;
	}
}

void OpenBrowser(string url) {
	try {
		Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
	} catch (Exception e) {
		Log.Debug(new Said("couldn't open the browser: {0}", Log.Describe(e)));
	}
}

// The one way out: "closing" goes up first (Commands.ExitRequested - an update reads it and stops), then the shutdown
// token is cancelled through Commands.ExitHandler. Every exit - the tray, the window, Ctrl+C, SIGTERM, SIGHUP, ProcessExit,
// "every account has finished" - comes through here.
static void Quit() => Commands.RequestExit();

[SupportedOSPlatform("windows")]
TrayIcon StartTray(BotManager mgr, Func<string> url) {
	TrayIcon icon = new(
		"nocat.farm",
		url,
		() => _ = mgr.StartAllAsync(),
		() => _ = mgr.StopAllAsync(),
		Quit) {
		MinimizeToTray = mgr.Global.MinimizeToTray
	};

	icon.Start(startMinimized || mgr.Global.StartMinimized);
	Commands.TrayHook = value => {
		if (OperatingSystem.IsWindows()) {
			icon.MinimizeToTray = value;
		}
	};

	// Which pop-ups actually appear is read live, so the settings apply the moment they're saved.
	Log.Notify = (kind, source, text) => {
		if (!OperatingSystem.IsWindows()) {
			return;   // never: there is no tray icon anywhere else. Said so the platform check can see it.
		}

		GlobalConfig g = mgr.Global;
		icon.MinimizeToTray = g.MinimizeToTray;

		if (!g.TrayNotifications) {
			return;
		}

		bool wanted = kind switch {
			NotifyKind.Earning => g.NotifyEarnings,
			NotifyKind.Social => g.NotifySocial,
			NotifyKind.Problem => g.NotifyProblems,
			NotifyKind.Trade => g.NotifyTrades,
			_ => false
		};

		if (wanted) {
			icon.Notify(source, text);
		}
	};

	Log.Info("running in the tray - right-click the icon for the menu");

	return icon;
}

async Task ConsoleLoop(BotManager mgr, CancellationTokenSource cts) {
	while (!cts.IsCancellationRequested) {
		string? line = ReadLine(cts.Token);

		// The question up when this line started being typed - see below.
		Prompt.Question? typedFor = lineTypedFor;

		if (line == null) {
			// stdin closed (a service, or the console was detached) - keep the engine alive.
			await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);

			return;
		}

		// Answers the question it was typed for, and only that one. Answered from the dashboard while this was being
		// typed, and another question up by now (the next account's code), the line is dropped rather than handed to
		// a question it was never meant for - nor run as a command (a password typed for a question that has gone
		// would otherwise land in the log and the history).
		Prompt.Question? question = Prompt.Current;

		if ((question != null) || (typedFor != null)) {
			if ((question != null) && ((typedFor == null) || (typedFor == question))) {
				Prompt.Answer(question, line);
			}

			continue;
		}

		if (line.Trim().Length == 0) {
			continue;
		}

		// 'clear' clears this screen and nothing else - the window and the dashboard keep theirs.
		if (Commands.IsClear(line)) {
			if (Commands.Board is { Active: true } board) {
				board.ClearLog();
			} else {
				try {
					Console.Clear();
				} catch (IOException) {
					// not a real terminal (docker logs, a pipe) - there is no screen to clear
				}
			}

			continue;
		}

		string output = await Commands.RunAtThisPcAsync(mgr, line, "console").ConfigureAwait(false);

		if (output.Length > 0) {
			// Masked as the log has it: the board scrolls the line out above itself, and 'add new login hunter2' stayed there.
			if (Commands.Board is { Active: true } showing) {
				showing.Show(Commands.LineForLog(line), output);
			} else {
				Console.WriteLine(output);
			}
		}

		if (Commands.ExitRequested) {
			await cts.CancelAsync().ConfigureAwait(false);

			return;
		}
	}
}

// A hand-rolled reader so a password prompt can stop the echo mid-line, and so the up arrow walks back through
// what was typed. Falls back to Console.ReadLine when input isn't a real terminal.
string? ReadLine(CancellationToken ct) {
	StringBuilder buffer = new();
	List<string> typed = consoleHistory;
	int at = -1;

	// The live board draws the prompt as part of its own layout, so it takes the echo instead of the console.
	// Without this the two would write to the same rows and the typed line would be scribbled over every second.
	LiveConsole? board = Commands.Board is { Active: true } live ? live : null;

	// A question up at any point while this line was typed makes it an answer, never history - a password answered
	// from the dashboard a moment before Enter would otherwise sit under the up arrow.
	bool asked = false;
	bool firstKey = true;
	lineTypedFor = null;

	void Echo() {
		if (board != null) {
			board.SetInput(Prompt.PendingSecret ? new string('*', buffer.Length) : buffer.ToString());
		}
	}

	while (!ct.IsCancellationRequested) {
		ConsoleKeyInfo key;

		try {
			key = Console.ReadKey(true);
		} catch (InvalidOperationException) {
			string? piped = Console.ReadLine();   // redirected input
			lineTypedFor = Prompt.Current;

			return piped;
		}

		// Which question this line is for: the one up as its first key was pressed.
		Prompt.Question? up = Prompt.Current;

		if (firstKey) {
			firstKey = false;
			lineTypedFor = up;
		}

		asked |= up != null;

		switch (key.Key) {
			case ConsoleKey.Enter:
				if (board != null) {
					board.SetInput("");
				} else {
					Console.WriteLine();
				}

				string result = buffer.ToString();

				// Never a line with a secret in it either - a password typed after 'add', a product key - as the window keeps none.
				if (result.Trim().Length > 0 && !asked && Prompt.Pending == null && !Commands.HoldsSecret(result)) {
					typed.Insert(0, result);

					if (typed.Count > 50) {
						typed.RemoveAt(typed.Count - 1);
					}
				}

				return result;

			case ConsoleKey.Backspace:
				if (buffer.Length > 0) {
					// A whole emoji, not half of one (see TypedLine.Backspace).
					int columns = TypedLine.Backspace(buffer, Prompt.PendingSecret);

					if (board == null) {
						Console.Write(string.Concat(Enumerable.Repeat("\b \b", columns)));
					}

					Echo();
				}

				break;

			case ConsoleKey.Escape:
				while (buffer.Length > 0) {
					buffer.Length--;

					if (board == null) {
						Console.Write("\b \b");
					}
				}

				Echo();

				break;

			case ConsoleKey.UpArrow:
			case ConsoleKey.DownArrow:
				if (typed.Count == 0 || Prompt.Pending != null) {
					break;
				}

				at = key.Key == ConsoleKey.UpArrow ? Math.Min(at + 1, typed.Count - 1) : Math.Max(at - 1, -1);

				while (buffer.Length > 0) {
					buffer.Length--;

					if (board == null) {
						Console.Write("\b \b");
					}
				}

				if (at >= 0) {
					buffer.Append(typed[at]);

					if (board == null) {
						Console.Write(typed[at]);
					}
				}

				Echo();

				break;

			default:
				if (char.IsControl(key.KeyChar)) {
					break;
				}

				buffer.Append(key.KeyChar);

				if (board == null) {
					Console.Write(Prompt.PendingSecret ? '*' : key.KeyChar);
				}

				Echo();

				break;
		}
	}

	return null;
}
