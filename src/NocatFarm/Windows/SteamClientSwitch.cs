using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Windows;

/// <summary>
/// The Windows half of "Open in Steam" (<see cref="SteamOpen"/>): reading the Steam client's saved-accounts files,
/// backing them up, writing the one account's flags into them, and restarting Steam. It never reads or writes a login
/// token - only <c>loginusers.vdf</c> and the <c>AutoLoginUser</c> / <c>RememberPassword</c> registry values, the same
/// two things the open-source account switchers touch. What to change is decided by <see cref="SteamOpen.Decide"/>,
/// which takes only the text and values read here, so it can be tested without a PC.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamClientSwitch {
	private const string SteamKey = @"Software\Valve\Steam";

	/// <summary>The folder Steam is installed in, from the registry it writes itself. Null when Steam isn't installed.</summary>
	private static string? SteamPath() {
		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SteamKey);

			if (key?.GetValue("SteamPath") is string p && (p.Length > 0)) {
				return p.Replace('/', '\\');
			}
		} catch (Exception e) when (e is IOException or System.Security.SecurityException or UnauthorizedAccessException) {
			// fall through to HKLM
		}

		try {
			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
				?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");

			if (key?.GetValue("InstallPath") is string p && (p.Length > 0)) {
				return p;
			}
		} catch (Exception e) when (e is IOException or System.Security.SecurityException or UnauthorizedAccessException) {
			// not installed
		}

		return null;
	}

	/// <summary>steam.exe, from the registry's SteamExe or SteamPath. Null when it can't be found.</summary>
	private static string? SteamExe(string? path) {
		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SteamKey);

			if (key?.GetValue("SteamExe") is string exe && (exe.Length > 0)) {
				string full = exe.Replace('/', '\\');

				if (File.Exists(full)) {
					return full;
				}
			}
		} catch (Exception e) when (e is IOException or System.Security.SecurityException or UnauthorizedAccessException) {
			// fall through
		}

		if (path != null) {
			string guess = Path.Combine(path, "steam.exe");

			if (File.Exists(guess)) {
				return guess;
			}
		}

		return null;
	}

	private static string VdfPath(string steamPath) => Path.Combine(steamPath, "config", "loginusers.vdf");

	private static bool SteamRunning() {
		try {
			Process[] all = Process.GetProcessesByName("steam");

			try {
				return all.Length > 0;
			} finally {
				foreach (Process p in all) {
					p.Dispose();
				}
			}
		} catch (InvalidOperationException) {
			return false;
		}
	}

	/// <summary>Read the two files and three values Open in Steam looks at, changing nothing.</summary>
	private static SteamOpen.Inputs Read() {
		string? steamPath = SteamPath();
		string? exe = SteamExe(steamPath);
		bool installed = (steamPath != null) && (exe != null);
		string? vdf = null;

		if (steamPath != null) {
			try {
				string file = VdfPath(steamPath);

				if (File.Exists(file)) {
					vdf = File.ReadAllText(file);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				vdf = null;
			}
		}

		string? autoLogin = null;
		bool remember = false;
		uint runningApp = 0;

		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SteamKey);

			if (key != null) {
				autoLogin = key.GetValue("AutoLoginUser") as string;
				remember = key.GetValue("RememberPassword") is int r && (r != 0);

				// RunningAppID is only meaningful while Steam is up; a closed client leaves the last game's number behind.
				if (SteamRunning() && key.GetValue("RunningAppID") is int app && (app > 0)) {
					runningApp = (uint) app;
				}
			}
		} catch (Exception e) when (e is IOException or System.Security.SecurityException or UnauthorizedAccessException) {
			// the defaults above
		}

		return new SteamOpen.Inputs(vdf, autoLogin, remember, runningApp, installed);
	}

	/// <summary>Read-only look for the preview: whether this PC can do it, Steam is here, the account is remembered, a game is on.</summary>
	public static SteamOpen.Numbers Look(string account) {
		SteamOpen.Inputs inputs = Read();
		SteamOpen.Plan plan = SteamOpen.Decide(account, inputs);

		return new SteamOpen.Numbers(
			Supported: true,
			SteamFound: inputs.SteamInstalled,
			Remembered: plan.Code is SteamOpen.Ok,
			GameRunning: plan.Code is SteamOpen.GameRunning,
			Account: account);
	}

	/// <summary>
	/// Do it: back up the files, write the account's flags, restart Steam. Returns the outcome code and its sentence.
	/// Refuses (and changes nothing) unless the client already remembers the account and no game is running.
	/// </summary>
	public static async Task<(string Code, string Said)> OpenAsync(string account) {
		SteamOpen.Inputs inputs = Read();
		SteamOpen.Plan plan = SteamOpen.Decide(account, inputs);

		if (plan.Code is not SteamOpen.Ok) {
			Log.Info(SteamOpen.Message(plan.Code, account), account);

			return (plan.Code, SteamOpen.Message(plan.Code, account).ToEnglish());
		}

		string? steamPath = SteamPath();
		string? exe = SteamExe(steamPath);

		if ((steamPath == null) || (exe == null)) {
			Log.Info(SteamOpen.Message(SteamOpen.NoSteam, account), account);

			return (SteamOpen.NoSteam, SteamOpen.Message(SteamOpen.NoSteam, account).ToEnglish());
		}

		try {
			BackUp(VdfPath(steamPath), inputs);

			// The saved-accounts file, then the two registry values, before Steam is told to close - so a half-written
			// change can never be what a still-running client reads.
			if (plan.NewVdf != null) {
				AtomicFile.Write(VdfPath(steamPath), plan.NewVdf);
			}

			using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SteamKey, true)) {
				key.SetValue("AutoLoginUser", plan.NewAutoLoginUser ?? account, RegistryValueKind.String);
				key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
			Log.Failed("couldn't write Steam's saved-accounts files", e, account);

			return (SteamOpen.NoSteam, new Said("couldn't change Steam's saved accounts - {0}", Log.Scrub(e.Message)).ToEnglish());
		}

		Log.Info(SteamOpen.Message(SteamOpen.Ok, account), account);
		await RestartSteamAsync(exe).ConfigureAwait(false);

		return (SteamOpen.Ok, SteamOpen.Message(SteamOpen.Ok, account).ToEnglish());
	}

	/// <summary>Close Steam the polite way (steam.exe -shutdown), wait for it to go, then start it again.</summary>
	private static async Task RestartSteamAsync(string exe) {
		if (SteamRunning()) {
			try {
				ProcessStartInfo stop = new(exe) { UseShellExecute = false };
				stop.ArgumentList.Add("-shutdown");
				Process.Start(stop)?.Dispose();
			} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
				Log.Debug($"steam -shutdown wouldn't start: {Log.Describe(e)}");
			}

			// Up to ~15 seconds for it to exit on its own; we never kill it, so a client mid-save is left to finish.
			for (int i = 0; (i < 30) && SteamRunning(); i++) {
				await Task.Delay(500).ConfigureAwait(false);
			}
		}

		if (SteamRunning()) {
			Log.Warn(new Said("Steam didn't close - leaving it as it is, nothing started"));

			return;
		}

		try {
			Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			Log.Failed("couldn't start Steam again", e);
		}
	}

	private static string BackupDir => Path.Combine(ConfigStore.ConfigDir, "state", "steam-open-backups");

	/// <summary>
	/// Copy the saved-accounts file and note the old registry values before any change, into a timestamped folder.
	/// Keeps the last three so a mistake can always be undone by hand; never touches a token.
	/// </summary>
	private static void BackUp(string vdfPath, SteamOpen.Inputs inputs) {
		try {
			Directory.CreateDirectory(BackupDir);
			string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
			string into = Path.Combine(BackupDir, stamp);
			Directory.CreateDirectory(into);

			if (File.Exists(vdfPath)) {
				File.Copy(vdfPath, Path.Combine(into, "loginusers.vdf"), overwrite: true);
			}

			File.WriteAllText(Path.Combine(into, "registry.txt"),
				$"AutoLoginUser={inputs.AutoLoginUser}{Environment.NewLine}RememberPassword={(inputs.RememberPassword ? 1 : 0)}{Environment.NewLine}");

			// Keep the newest three folders, drop the rest.
			foreach (string old in Directory.GetDirectories(BackupDir).OrderByDescending(static d => d, StringComparer.Ordinal).Skip(3)) {
				try {
					Directory.Delete(old, recursive: true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// leave it; a stale backup folder is harmless
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug($"couldn't back up Steam's saved-accounts files: {Log.Describe(e)}");
		}
	}
}
