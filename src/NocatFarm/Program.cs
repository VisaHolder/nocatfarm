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
	Console.WriteLine("  Move the folder somewhere you can write to - Desktop, Documents or its own folder on another drive.");
	Console.WriteLine();
	await Task.Delay(8000).ConfigureAwait(false);

	return 1;
}

// A crash on any thread lands in the log, not nowhere. Only the ones that end the process get here.
AppDomain.CurrentDomain.UnhandledException += static (_, e) => {
	try {
		File.AppendAllText(Path.Combine(ConfigStore.Root, "logs", "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.ExceptionObject}{Environment.NewLine}");
	} catch {
		// nothing more can be done from here
	}

	// A new version that crashes while it's being tried out goes straight back to the old one.
	NocatFarm.Core.SelfUpdate.ReportCrashed();
};

// One instance per config folder. Two copies running the same accounts share a Steam login ID, so they take
// turns kicking each other off - and they put two icons in the tray, which is how you notice.
using Mutex singleInstance = new(false, "nocatFarm-" + Convert.ToHexStringLower(
	System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ConfigStore.Root.ToLowerInvariant())))[..16]);

if (!singleInstance.WaitOne(TimeSpan.Zero, false)) {
	// This runs before any window exists and, in a GUI launch, before any console does either - so without
	// somewhere to say it, a second launch was a silent four-second no-op that looked like the exe was broken.
	if (OperatingSystem.IsWindows()) {
		NativeConsole.Attach();
	}

	Console.WriteLine();
	Console.WriteLine("  nocatFarm is already running for this folder.");
	Console.WriteLine("  Look for its icon by the clock, or close the other one first.");
	Console.WriteLine();
	Console.WriteLine("  (Two copies would share a Steam login and keep signing each other out.)");
	await Task.Delay(4000).ConfigureAwait(false);

	return 1;
}

GlobalConfig global = ConfigStore.LoadGlobal();
Live.Global = global;
Log.Configure(global.FileLogging, global.Debug, root, global.LogRetentionDays);

Banner();

// NOCATFARM_WEB_HOST / _PORT / _PASSWORD(_FILE), for Docker. Nothing happens unless one is set.
Platform.ApplyEnvironment(global);

// Off Windows: a data folder that can't be written (a Docker bind mount made by root) is said now, with the fix.
Platform.CheckWritable(ConfigStore.ConfigDir, Path.Combine(ConfigStore.Root, "logs"));

BotManager manager = new(global);
Commands.Host = manager;   // so a command sent by Steam message can reach the same engine the console does
await manager.SyncFromDiskAsync().ConfigureAwait(false);

// Keep the registry entry in step with the setting, in case the exe moved since it was last written.
if (OperatingSystem.IsWindows() && ((global.StartWithWindows != WindowsIntegration.StartsWithWindows())
	|| (global.StartWithWindows && !WindowsIntegration.StartupPointsHere()))) {
	WindowsIntegration.SetStartWithWindows(global.StartWithWindows);
}

// Before anything else is said: if this start finishes an update, that's the first line in the window.
SelfUpdate.AnnounceIfJustUpdated();

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

// Docker and systemd stop a program with SIGTERM. Left alone, .NET ends the process on it (and the dashboard's own
// host takes it as its cue to stop and leave the rest running), so the orderly sign-out below never happened and a
// `docker stop` waited out its timeout and killed it. Taken here, SIGTERM is the same clean shutdown as 'exit'.
// Not on Windows, where nothing sends it and closing works the way it always has.
using PosixSignalRegistration? sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
	ctx.Cancel = true;
	shutdown.Cancel();
});

if (OperatingSystem.IsWindows()) {
	NativeConsole.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "nocatFarm.ico"));
}

// Told once, so anything that wants the dashboard asks Commands rather than carrying its own copy of the URL and
// its own Process.Start. Outside the tray block: it used to be set only when there was a tray icon, so with the
// tray off, 'add' never opened the dashboard however OpenDashboardAfterAdd was set.
Commands.DashboardUrl = () => web?.Url ?? "";

TrayIcon? tray = null;

if (global.Tray && !forceNoTray && OperatingSystem.IsWindows()) {
	tray = StartTray(manager, () => web?.Url ?? "", shutdown);
	Commands.TrayPresent = tray != null;
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
	window = new MainWindow(manager, () => web?.Url ?? "", () => {
		Commands.RequestExit();
		shutdown.Cancel();
	});

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
} else {
	board = new LiveConsole(manager);
	board.Start();
	Commands.Board = board;
}

Ready(web?.Url, manager.All.Count);

// Plugins load BEFORE any account signs in, so a plugin that subscribes to "account online" actually sees
// the first one rather than missing the whole fleet by a second.
await NocatFarm.Plugins.PluginHost.LoadAllAsync(manager, CancellationToken.None).ConfigureAwait(false);

// Before the accounts start, not after: they start spread out over a minute or more, and Telegram commands, the
// notifications of that startup, and an update's one-by-one sign-out all need to work from the first second.
NocatFarm.Core.Notifier.Start(manager);
NocatFarm.Core.DiscordPresence.Start(manager);
NocatFarm.Core.SelfUpdate.Fleet = () => manager.All;

// The day-by-day totals behind the dashboard's history charts. Before the accounts start, so the first card of the
// run is counted once, after what the short-term records already know has been filled in.
NocatFarm.Core.History.Start(manager);

if (manager.All.Count == 0) {
	FirstRunHint(web?.Url);
} else {
	await manager.StartAllAsync().ConfigureAwait(false);
}

// Once-a-day "what did the fleet bank overnight" summary to the log (default 09:30). Self-scheduling; no-ops
// with no accounts. Type `report` to see it on demand.
NocatFarm.Core.DailyReport.Start(manager);

// Looking for a new version, the hourly reminder and "Update by itself" - for the app, not for an account, so it
// happens with no account signed in too.
NocatFarm.Core.UpdateCheck.Start(manager);


// Only where there's a desktop: a server or a container has no browser, and the attempt is just a baffling error.
if (global.OpenBrowserOnStart && (web != null) && Platform.HasDesktop) {
	OpenBrowser(web.Url);
}

// The console is the only thing reading the keyboard, so a Steam Guard prompt and a typed command can never
// fight over stdin: whatever is typed goes to the prompt if one is waiting, and to the command router if not.
// Its own thread, because reading a key blocks it for as long as nobody types.
List<string> consoleHistory = [];

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
	: Task.Factory.StartNew(() => ConsoleLoop(manager, shutdown), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

Console.CancelKeyPress += (_, e) => {
	e.Cancel = true;
	shutdown.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

// ExitWhenAllFinished pairs with the per-account "log out when finished": a user who set both is asking for a
// finite run, and leaving the process parked in the tray (still blocking sleep) is not what they asked for.
try {
	while (!shutdown.IsCancellationRequested) {
		await Task.Delay(TimeSpan.FromSeconds(30), shutdown.Token).ConfigureAwait(false);

		if (manager.Global.ExitWhenAllFinished && manager.AllFinished) {
			Log.Good("every account has finished - closing down as configured");
			await shutdown.CancelAsync().ConfigureAwait(false);
		}
	}
} catch (OperationCanceledException) {
	// asked to stop
}

Log.Info("shutting down...");

// Closed straight after an update: that's somebody closing it, not the new version failing to start.
NocatFarm.Core.SelfUpdate.ConfirmStarted();

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
		Log.Debug(new Said("couldn't open the browser: {0}", e.Message));
	}
}

[SupportedOSPlatform("windows")]
TrayIcon StartTray(BotManager mgr, Func<string> url, CancellationTokenSource cts) {
	TrayIcon icon = new(
		"nocat.farm",
		url,
		() => _ = mgr.StartAllAsync(),
		() => _ = mgr.StopAllAsync(),
		() => cts.Cancel()) {
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

	Log.Info("running in the notification area - right-click the icon for the menu");

	return icon;
}

async Task ConsoleLoop(BotManager mgr, CancellationTokenSource cts) {
	while (!cts.IsCancellationRequested) {
		string? line = ReadLine(cts.Token);

		if (line == null) {
			// stdin closed (a service, or the console was detached) - keep the engine alive.
			await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);

			return;
		}

		if (Prompt.Pending != null) {
			Prompt.Answer(line);

			continue;
		}

		if (line.Trim().Length == 0) {
			continue;
		}

		string output = await Commands.RunAsync(mgr, line).ConfigureAwait(false);

		if (output.Length > 0) {
			if (Commands.Board is { Active: true } showing) {
				showing.Show(line, output);
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
			return Console.ReadLine();   // redirected input
		}

		switch (key.Key) {
			case ConsoleKey.Enter:
				if (board != null) {
					board.SetInput("");
				} else {
					Console.WriteLine();
				}

				string result = buffer.ToString();

				if (result.Trim().Length > 0 && Prompt.Pending == null) {
					typed.Insert(0, result);

					if (typed.Count > 50) {
						typed.RemoveAt(typed.Count - 1);
					}
				}

				return result;

			case ConsoleKey.Backspace:
				if (buffer.Length > 0) {
					buffer.Length--;

					if (board == null) {
						Console.Write("\b \b");
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
