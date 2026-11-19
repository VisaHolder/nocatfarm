using System.Diagnostics;
using GamerFarm.Config;

namespace GamerFarm.Core;

/// <summary>
/// 2.0.1: the names from before. gamer.farm was called something else until 2.0.0, and 2.0.0 still had that name on its
/// program, its settings file, its logs, its environment variables and its add-on file. 2.0.1 renamed them all - and this is
/// everything that still knows the names from before, so an install that had them keeps working, and nothing new is ever
/// made under them.
/// </summary>
/// <remarks>
/// What happens to an install from before, on its first start of 2.0.1 (see <see cref="MoveIn"/>): the settings file, the
/// logs and the add-on file take their new names, the startup entry and the shortcuts start the new program, and the old
/// program's files go - all only once the new version has said it came up fine (an update's trial), so putting the old
/// version back always finds everything where it left it. Anything that can't be moved is read where it is, and tried again
/// at the next start.
///
/// What stays, on purpose (each has its reason where it's used):
///   • the environment variables from before are still read, when the new one isn't set (<see cref="Env"/>)
///   • a systemd service installed before 2.0.1 keeps its unit, its folders, its helper and the old program name, which is a
///     small script that starts the new program - the unit names them, and only its owner can change a unit
///   • the lock that keeps two copies off one folder, and the names it's asked by, so a version from before and this one
///     never run on one folder together
///   • the marks inside the files (encrypted settings, locked backups, a backup's manifest), which every version reads
///   • the note on a card swap offer, while copies from before 2.0.0 still swap (see <see cref="CardExchange.Tag"/>)
///   • a plugin built for a version from before loads as it always has (see <see cref="Plugins.PluginHost"/>)
/// </remarks>
public static class OldNames {
	/// <summary>The program, before 2.0.1: nocatFarm.exe on Windows, nocatFarm elsewhere - and its .dll.</summary>
	public const string Program = "nocatFarm";

	/// <summary>The settings file, before 2.0.1 (config/).</summary>
	public const string Settings = "nocatFarm.json";

	/// <summary>A day's log, before 2.0.1: logs/nocatFarm-2026-11-20.log.</summary>
	public const string LogPrefix = "nocatFarm-";

	/// <summary>The environment variables, before 2.0.1: NOCATFARM_WEB_PASSWORD and so on.</summary>
	public const string EnvPrefix = "NOCATFARM_";

	/// <summary>The startup entry's name in HKCU\...\Run, before 2.0.1.</summary>
	public const string RunValue = "nocatFarm";

	/// <summary>An official add-on's file, before 2.0.1: plugins/nocatfarm.rep4rep.dll.</summary>
	public const string AddonPrefix = "nocatfarm.";

	/// <summary>The helper of a systemd service installed before 2.0.1, and its folder beside the program.</summary>
	public const string ServiceHelper = "nocatfarm-service.sh";

	public const string ServiceBox = ".nocatfarm-update";

	/// <summary>An update's folder in temp, before 2.0.1.</summary>
	public const string UpdateWork = "nocatfarm-update-";

	/// <summary>The Windows firewall rule, before 2.0.1.</summary>
	public const string FirewallRule = "nocat.farm dashboard";

	/// <summary>
	/// The single-instance lock and the signals ("show", "quit") are named this way by every version, this one too: renamed,
	/// an older version put back by an update, or started from an old shortcut, couldn't see this one running on the same
	/// folder - two copies on one set of logins. Never shown anywhere.
	/// </summary>
	public const string LockPrefix = "nocatFarm-";

	/// <summary>
	/// Set by the small script that stands in for the old program (Linux and a Mac) before it starts this one: something -
	/// a systemd unit from before 2.0.1, a script, a cron line - still starts the old name, so that script stays.
	/// </summary>
	internal const string StartedAsVar = "GAMERFARM_STARTED_AS";

	/// <summary>
	/// An environment variable: GAMERFARM_... when it's set, else the same one as it was called before 2.0.1. A docker-compose
	/// file, a systemd unit or a script written for an older version keeps working as it is.
	/// </summary>
	public static string? Env(string name) {
		string? value = Environment.GetEnvironmentVariable(name);

		if (!string.IsNullOrEmpty(value) || !name.StartsWith("GAMERFARM_", StringComparison.Ordinal)) {
			return value;
		}

		return Environment.GetEnvironmentVariable(EnvPrefix + name["GAMERFARM_".Length..]);
	}

	/// <summary>The program's file in a folder under the name from before: nocatFarm.exe on Windows, nocatFarm elsewhere.</summary>
	public static string ProgramIn(string folder, bool windows) => Path.Combine(folder, windows ? Program + ".exe" : Program);

	/// <summary>
	/// The old program's file is the small script that starts this one (Linux and a Mac) - not a program of its own.
	/// </summary>
	internal static bool IsStandIn(string path) {
		try {
			if (!File.Exists(path) || (new FileInfo(path).Length > 4096)) {
				return false;
			}

			using FileStream f = File.OpenRead(path);
			Span<byte> head = stackalloc byte[2];

			return (f.Read(head) == 2) && (head[0] == '#') && (head[1] == '!');
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	// ── the relay on Windows ─────────────────────────────────────────────────

	/// <summary>
	/// 2.0.1, Windows: started under the old program name (nocatFarm.exe) - by an update from a version before 2.0.1, whose
	/// swap script starts and watches nocatFarm.exe, or by a shortcut not yet pointed at the new one. That file is a copy of
	/// the new program's starter, so it runs this very code: it starts gamerFarm.exe with the same options and steps aside.
	/// Null when this isn't that start; otherwise what to exit with.
	/// </summary>
	/// <remarks>
	/// An update's trial (NF_OK handed down): the old swap script watches nocatFarm.exe and puts the old version back if it
	/// goes. So this waits - until the new version says it came up fine, or stops - then exits with it, and the swap script
	/// finds the answer either way. The new version is told who it was started by, so it stops at once if the swap script
	/// stops this one to put the old version back (see <see cref="WatchRelay"/>).
	/// </remarks>
	public static int? RelayIfOldProgram(string[] args) {
		if (!OperatingSystem.IsWindows() || (Environment.ProcessPath is not { } me)
			|| !Path.GetFileNameWithoutExtension(me).Equals(Program, StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		string target = Path.Combine(Path.GetDirectoryName(me)!, "gamerFarm.exe");

		if (!File.Exists(target)) {
			return null;   // nothing to hand over to: run as this file, as it is
		}

		ProcessStartInfo start = new(target) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };

		foreach (string a in args) {
			start.ArgumentList.Add(a);
		}

		start.Environment[RelayParentVar] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

		Process? child;

		try {
			child = Process.Start(start);
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			return null;   // couldn't: run as this file instead - the program is the same
		}

		if (child == null) {
			return null;
		}

		using (child) {
			string? ok = Environment.GetEnvironmentVariable("NF_OK");

			// Asked to do one thing and say how it went - the old uninstaller's --quit, the installer's --setup: the answer is
			// the new program's, so it's waited for.
			if (args.Any(static a => a.ToLowerInvariant() is "--quit" or "--setup" or "--ping" or "--steam-selftest" or "--help" or "-h")) {
				child.WaitForExit();

				return child.ExitCode;
			}

			if (string.IsNullOrEmpty(ok)) {
				return 0;   // nobody watching this one: the new one is up, nothing to wait for
			}

			while (!child.WaitForExit(1000)) {
				if (File.Exists(ok)) {
					return 0;
				}
			}

			return child.ExitCode;
		}
	}

	/// <summary>The relay's process id, handed to the program it started (see <see cref="RelayIfOldProgram"/>).</summary>
	internal const string RelayParentVar = "GAMERFARM_RELAY_PARENT";

	/// <summary>
	/// Started by the relay during an update's trial: if the relay is stopped before this version has said it came up fine,
	/// that's the old swap script putting the old version back - this one goes at once, so the files can go back and the old
	/// version find the folder free.
	/// </summary>
	public static void WatchRelay(Func<bool> confirmed) {
		if (!OperatingSystem.IsWindows() || !int.TryParse(Environment.GetEnvironmentVariable(RelayParentVar), out int parentId)) {
			return;
		}

		Process parent;

		try {
			parent = Process.GetProcessById(parentId);
		} catch (ArgumentException) {
			return;   // already gone: it saw the answer, or nothing was watching
		}

		Thread watch = new(() => {
			using (parent) {
				parent.WaitForExit();
			}

			if (!confirmed()) {
				Log.Warn(new Said("update: the version before is being put back - stopping"));
				Process.GetCurrentProcess().Kill();
			}
		}) { IsBackground = true, Name = "relay watch" };

		watch.Start();
	}

	// ── moving in ────────────────────────────────────────────────────────────

	private static int _moved;

	/// <summary>
	/// Bring an install from before 2.0.1 over to the new names, once: the settings file, the logs and the add-on files here;
	/// on Windows the startup entry, the shortcuts and the old program's files too. Only once this version is staying - at
	/// start when it isn't an update on trial, else once the trial has passed (<see cref="SelfUpdate.ConfirmStarted"/>).
	/// Whatever can't be moved is left where it is and read there; the next start tries again.
	/// </summary>
	public static void MoveIn() {
		if (Interlocked.Exchange(ref _moved, 1) != 0) {
			return;
		}

		ConfigStore.TakeOldSettings();
		Log.TakeOldFiles(Path.Combine(ConfigStore.Root, "logs"));
		TakeOldAddons(Plugins.PluginHost.Folder);

		if (OperatingSystem.IsWindows()) {
			Windows.OldProgram.MoveIn();
		} else {
			DropOldProgram(AppContext.BaseDirectory);
		}
	}

	/// <summary>
	/// plugins/nocatfarm.&lt;id&gt;.dll becomes gamerfarm.&lt;id&gt;.dll, with anything waiting beside it (an update or a removal for
	/// the next start). When the new name is already there, that one is the one kept.
	/// </summary>
	internal static void TakeOldAddons(string folder) {
		try {
			if (!Directory.Exists(folder)) {
				return;
			}

			foreach (string old in Directory.GetFiles(folder, AddonPrefix + "*")) {
				string name = Path.GetFileName(old);
				string to = Path.Combine(folder, "gamerfarm." + name[AddonPrefix.Length..]);

				try {
					if (File.Exists(to)) {
						File.Delete(old);
					} else {
						File.Move(old, to);
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Debug($"moving in: {name} stays as it is for now: {Log.Describe(e)}");
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug($"moving in: couldn't look in {folder}: {Log.Describe(e)}");
		}
	}

	/// <summary>
	/// Linux and a Mac: the old program's own files go once nothing needs them - the .dll and its two .json files, left behind
	/// by an update from before 2.0.1 - and the small script under the old program name too, unless something still starts
	/// this through it (a systemd unit from before 2.0.1, a script, a cron line: it stays for them).
	/// </summary>
	internal static void DropOldProgram(string folder) {
		if (!File.Exists(Path.Combine(folder, "gamerFarm.dll"))) {
			return;   // not an install of this version (a test, dotnet run): nothing here is ours to tidy
		}

		// And the copy of the unit the zip had beside them - the one systemd runs is in /etc, and stays.
		foreach (string f in new[] { Program + ".dll", Program + ".deps.json", Program + ".runtimeconfig.json", Program + ".ico", Program + ".pdb",
			Program + ".staticwebassets.endpoints.json", "nocatfarm.service" }) {
			TryDelete(Path.Combine(folder, f));
		}

		// A systemd unit from before 2.0.1 runs the helper under its old name here, and starts the old program name: its helper
		// has stamped a start in its folder. Those stay - unless this very start is the service under the unit 2.0.1 ships,
		// put in its place (the old one can't run beside it). A start by hand in that folder never takes them away.
		string oldBox = Path.Combine(folder, ServiceBox);
		bool oldUnitHere = File.Exists(Path.Combine(oldBox, SelfUpdate.ServiceStamp));
		bool newUnitNow = SelfUpdate.RunsUnder(folder, SelfUpdate.ServiceBox);
		string standIn = ProgramIn(folder, false);

		if (IsStandIn(standIn) && !StandInStillUsed() && (!oldUnitHere || newUnitNow)) {
			TryDelete(standIn);
		}

		// The helper under its old name came in the zip for updaters from before 2.0.1; nothing runs it unless that unit does.
		if ((!oldUnitHere && !StandInStillUsed()) || newUnitNow) {
			TryDelete(Path.Combine(folder, ServiceHelper));
		}

		if (newUnitNow) {
			try {
				if (Directory.Exists(oldBox) && !Directory.Exists(Path.Combine(oldBox, "job"))) {
					Directory.Delete(oldBox, true);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug($"moving in: {ServiceBox} stays for now: {Log.Describe(e)}");
			}
		}
	}

	/// <summary>
	/// Whether this start came through the script under the old program name for a reason that will come again: started by
	/// it, and not by an update's trial (NF_OK, from the swap script of a version from before) or by start.command (which is
	/// this version's own). Then a unit, a script or a cron line names the old program, and its script stays.
	/// </summary>
	internal static bool StandInStillUsed() =>
		!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(StartedAsVar))
		&& string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NF_OK"))
		&& (Env("GAMERFARM_STARTER") != "start.command");

	internal static void TryDelete(string path) {
		try {
			if (File.Exists(path)) {
				File.Delete(path);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug($"moving in: {Path.GetFileName(path)} stays for now: {Log.Describe(e)}");
		}
	}
}
