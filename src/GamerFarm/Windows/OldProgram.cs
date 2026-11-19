using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using GamerFarm.Config;
using GamerFarm.Core;

namespace GamerFarm.Windows;

/// <summary>
/// 2.0.1, Windows: an install from before 2.0.1 moved over to the new program name - once this version is staying (see
/// <see cref="OldNames.MoveIn"/>). The startup entry, the shortcuts and the installed-apps entry start gamerFarm.exe, and
/// the old program's files go.
/// </summary>
/// <remarks>
/// One stays, in an install made with the setup: nocatFarm.exe, hidden. The uninstaller an install from before 2.0.1 still
/// has closes the app through that file before it removes anything - without it, the app was left running and its files in
/// use. It's a copy of the new program's starter that hands over to gamerFarm.exe (<see cref="OldNames.RelayIfOldProgram"/>),
/// and the setup of 2.0.1 or later, which brings an uninstaller that knows the new name, takes it away.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OldProgram {
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

	/// <summary>The old program's own files beside the new one: the starter, the program and its two .json files, the icon.</summary>
	internal static readonly string[] Files = [
		OldNames.Program + ".exe", OldNames.Program + ".dll", OldNames.Program + ".deps.json", OldNames.Program + ".runtimeconfig.json",
		OldNames.Program + ".ico", OldNames.Program + ".pdb", OldNames.Program + ".staticwebassets.endpoints.json"
	];

	public static void MoveIn() {
		string here = AppContext.BaseDirectory.TrimEnd('\\');
		string newExe = Path.Combine(here, "gamerFarm.exe");

		if (!File.Exists(newExe) || !File.Exists(Path.Combine(here, "gamerFarm.dll"))) {
			return;   // not an install of this version (a test, dotnet run)
		}

		string oldExe = OldNames.ProgramIn(here, true);

		StartupEntry(here);
		InstallRecord.Refresh();

		// Nothing of the old program here (moved in at an earlier start, or a new install): nothing more to do.
		bool installed = InstallRecord.IsThisFolder();

		if (!Files.Any(f => File.Exists(Path.Combine(here, f)))
			|| (installed && Files.Where(static f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).All(f => !File.Exists(Path.Combine(here, f)))
				&& ((File.GetAttributes(oldExe) & FileAttributes.Hidden) != 0))) {
			return;
		}

		Shortcuts(oldExe, newExe);

		// The relay that started this one may still be on its way out (it goes a second after this version said it's fine):
		// its file goes once it has.
		_ = Task.Run(async () => {
			for (int i = 0; (i < 30) && !DropFiles(here, keepRelay: installed); i++) {
				await Task.Delay(2000).ConfigureAwait(false);
			}
		});
	}

	/// <summary>The old program's files go - all of them, or all but the hidden starter for the old uninstaller. True when nothing's left to do.</summary>
	internal static bool DropFiles(string here, bool keepRelay) {
		bool done = true;

		foreach (string f in Files) {
			string path = Path.Combine(here, f);

			if (!File.Exists(path)) {
				continue;
			}

			try {
				if (keepRelay && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
					File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

					continue;
				}

				File.Delete(path);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				done = false;   // still in use - the relay hasn't quite gone yet
				Log.Debug($"moving in: {f} stays for now: {Log.Describe(e)}");
			}
		}

		return done;
	}

	/// <summary>
	/// The startup entry from before 2.0.1, when it starts a copy in this folder: it goes, and the new one is written in its
	/// place if starting with Windows is on. One that starts another copy is that copy's own, and stays.
	/// </summary>
	internal static void StartupEntry(string here) {
		try {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);

			if (key.GetValue(OldNames.RunValue) is string old && string.Equals(Path.GetDirectoryName(WindowsIntegration.ExeOf(old))?.TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase)) {
				key.DeleteValue(OldNames.RunValue, false);
				Log.Debug("moving in: the startup entry from before 2.0.1 is gone");
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
			Log.Debug($"moving in: the startup entry from before 2.0.1 stays for now: {Log.Describe(e)}");
		}

		WindowsIntegration.KeepInStep(Live.Global.StartWithWindows);
	}

	/// <summary>Where shortcuts that start gamer.farm are: the Start menu (and a folder in it), the desktop, the taskbar's pins.</summary>
	private static IEnumerable<string> ShortcutFolders() {
		string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
		string pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

		yield return programs;

		if (Directory.Exists(programs)) {
			foreach (string sub in Directory.EnumerateDirectories(programs)) {
				yield return sub;
			}
		}

		yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
		yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		yield return pins;
	}

	/// <summary>
	/// Every shortcut that starts the old program in this folder starts the new one instead - and one with the old name in its
	/// own name (made by hand for a portable copy) takes the new name, unless there's one by that name already.
	/// </summary>
	internal static void Shortcuts(string oldExe, string newExe) {
		object? shell = null;

		try {
			if (Type.GetTypeFromProgID("WScript.Shell") is not { } type || (Activator.CreateInstance(type) is not { } made)) {
				return;
			}

			shell = made;

			foreach (string folder in ShortcutFolders().Distinct(StringComparer.OrdinalIgnoreCase)) {
				if (!Directory.Exists(folder)) {
					continue;
				}

				foreach (string lnk in Directory.EnumerateFiles(folder, "*.lnk")) {
					try {
						Repoint(shell, lnk, oldExe, newExe);
					} catch (Exception e) when (e is TargetInvocationException or IOException or UnauthorizedAccessException or COMException) {
						Log.Debug($"moving in: the shortcut {Path.GetFileName(lnk)} stays as it is: {Log.Describe(e)}");
					}
				}
			}
		} catch (Exception e) when (e is TargetInvocationException or IOException or UnauthorizedAccessException or COMException or System.Security.SecurityException) {
			Log.Debug($"moving in: couldn't look at the shortcuts: {Log.Describe(e)}");
		} finally {
			if (shell != null) {
				Marshal.ReleaseComObject(shell);
			}
		}
	}

	private static void Repoint(object shell, string lnk, string oldExe, string newExe) {
		object link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnk])!;

		try {
			Type t = link.GetType();
			string target = t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string ?? "";

			if (!string.Equals(target, oldExe, StringComparison.OrdinalIgnoreCase)) {
				return;
			}

			t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [newExe]);

			string icon = t.InvokeMember("IconLocation", BindingFlags.GetProperty, null, link, null) as string ?? "";

			if (icon.StartsWith(oldExe, StringComparison.OrdinalIgnoreCase) || icon.StartsWith(",", StringComparison.Ordinal) || (icon.Length == 0)) {
				t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, [newExe + ",0"]);
			}

			t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
			Log.Debug($"moving in: the shortcut {Path.GetFileName(lnk)} starts {Path.GetFileName(newExe)} now");
		} finally {
			Marshal.ReleaseComObject(link);
		}

		string name = Path.GetFileNameWithoutExtension(lnk);

		if (name.Contains("nocat", StringComparison.OrdinalIgnoreCase)) {
			string renamed = Path.Combine(Path.GetDirectoryName(lnk)!, name.Replace("nocat", "gamer", StringComparison.OrdinalIgnoreCase) + ".lnk");

			if (!File.Exists(renamed)) {
				File.Move(lnk, renamed);
			}
		}
	}

	/// <summary>
	/// Going back to a version from before 2.0.1 ('update to'): it knows only its own startup entry and program name, so the
	/// new startup entry goes (it writes its own) and the shortcuts start the old program again.
	/// </summary>
	public static void GoingBack() {
		string here = AppContext.BaseDirectory.TrimEnd('\\');

		WindowsIntegration.SetStartWithWindows(false);
		Shortcuts(Path.Combine(here, "gamerFarm.exe"), OldNames.ProgramIn(here, true));
	}
}
