using System.Diagnostics;
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
	NativeConsole.Attach();
}

ConfigStore.UseRoot(root);

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

BotManager manager = new(global);
Commands.Host = manager;   // so a command sent by Steam message can reach the same engine the console does
await manager.SyncFromDiskAsync().ConfigureAwait(false);

// Keep the registry entry in step with the setting, in case the exe moved since it was last written.
if (OperatingSystem.IsWindows() && (global.StartWithWindows != WindowsIntegration.StartsWithWindows())) {
	WindowsIntegration.SetStartWithWindows(global.StartWithWindows);
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

if (OperatingSystem.IsWindows()) {
	NativeConsole.SetWindowIcon(Path.Combine(AppContext.BaseDirectory, "nocatFarm.ico"));
}

TrayIcon? tray = null;

if (global.Tray && !forceNoTray && OperatingSystem.IsWindows()) {
	// Told once, so anything that wants the dashboard asks Commands rather than carrying its own copy of
	// the URL and its own Process.Start.
	Commands.DashboardUrl = () => web?.Url ?? "";

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

if (wantWindow) {
	window = new MainWindow(manager, () => web?.Url ?? "", () => {
		Commands.RequestExit();
		shutdown.Cancel();
	});

	// If it can't open, put the console log straight back rather than leaving a silent app behind.
	window.Failed += () => {
		windowFailed = true;
		Commands.Window = null;
		Log.Written -= Show;
		Log.Suppressed = false;

		// There is no console to fall back into - the exe is windowed - so make one, or the app is invisible.
		if (OperatingSystem.IsWindows()) {
			NativeConsole.Attach();
		}
	};


	// DEBUG is always in the file; whether it is also on screen is the "Show debug detail" setting, and it is
	// read live so the toggle takes effect on the next line rather than the next restart. Left unfiltered this
	// window showed a wall of "reusing web token" and "243 licence(s) known" that buried the six lines actually
	// worth reading - the console and the dashboard's Log tab both already filtered it; this was the one surface
	// still showing it unasked.
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

	window.Start(!global.StartMinimized && !startMinimized);
} else {
	board = new LiveConsole(manager);
	board.Start();
	Commands.Board = board;
}

Ready(web?.Url, manager.All.Count);

// Plugins load BEFORE any account signs in, so a plugin that subscribes to "account online" actually sees
// the first one rather than missing the whole fleet by a second.
await NocatFarm.Plugins.PluginHost.LoadAllAsync(manager, CancellationToken.None).ConfigureAwait(false);

if (manager.All.Count == 0) {
	FirstRunHint(web?.Url);
} else {
	await manager.StartAllAsync().ConfigureAwait(false);
}

// Once-a-day "what did the fleet bank overnight" summary to the log (default 09:30). Self-scheduling; no-ops
// with no accounts. Type `report` to see it on demand.
NocatFarm.Core.DailyReport.Start(manager);

if (global.OpenBrowserOnStart && (web != null)) {
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
if (window != null) {
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

if (OperatingSystem.IsWindows()) {
	WindowsIntegration.KeepAwake(false);
}

tray?.Dispose();

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
		Log.Debug($"couldn't open the browser: {e.Message}");
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
	Commands.TrayHook = value => icon.MinimizeToTray = value;

	// Which pop-ups actually appear is read live, so the settings apply the moment they're saved.
	Log.Notify = (kind, source, text) => {
		GlobalConfig g = mgr.Global;
		icon.MinimizeToTray = g.MinimizeToTray;

		if (!g.TrayNotifications) {
			return;
		}

		bool wanted = kind switch {
			NotifyKind.Earning => g.NotifyEarnings,
			NotifyKind.Social => g.NotifySocial,
			NotifyKind.Problem => g.NotifyProblems,
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
