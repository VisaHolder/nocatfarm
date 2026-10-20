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

	/// <summary>1.8.2: Steam's settings file, read for (and only ever changed in) "Ask which account to use each time Steam starts".</summary>
	private static string ConfigPath(string steamPath) => Path.Combine(steamPath, "config", "config.vdf");

	/// <summary>
	/// A text file exactly as it is - null when it's missing, unreadable, or not plain UTF-8 that would be written back byte
	/// for byte (then it's never changed). A byte-order mark is kept as the text's first character, so it's written back too.
	/// </summary>
	private static string? ReadExact(string path) {
		try {
			if (!File.Exists(path)) {
				return null;
			}

			byte[] bytes = File.ReadAllBytes(path);
			string text = new System.Text.UTF8Encoding(false, true).GetString(bytes);

			return System.Text.Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(bytes) ? text : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) {
			return null;
		}
	}

	/// <summary>Write text back with no encoding changes (a leading byte-order mark stays), through a temp file and a rename.</summary>
	private static void WriteExact(string path, string text) => AtomicFile.Write(path, text);

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
	internal static SteamOpen.Inputs Read() {
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

		string? config = steamPath == null ? null : ReadExact(ConfigPath(steamPath));
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

		return new SteamOpen.Inputs(vdf, autoLogin, remember, runningApp, installed, config);
	}

	/// <summary>One press at a time: a double-click (or the window button and the dashboard together) can't race on the files.</summary>
	private static readonly SemaphoreSlim OneAtATime = new(1, 1);

	/// <summary>
	/// Do it: close Steam first and wait for it to go, then (and only then) back up the files, write the account's flags,
	/// and start Steam. Returns the outcome code and its sentence. Changes nothing - and says so - when the client already
	/// doesn't remember the account, a game is running, Steam wouldn't close, or the backup failed.
	/// </summary>
	/// <param name="login">The Steam login name (Cfg.SteamLogin), matched against the saved accounts.</param>
	/// <param name="steamId">The account's SteamID64, matched first; 0 when it isn't signed in yet.</param>
	/// <param name="display">The nocat.farm account name, for the log and the reply.</param>
	public static async Task<(string Code, string Said)> OpenAsync(string login, ulong steamId, string display) {
		if (!await OneAtATime.WaitAsync(0).ConfigureAwait(false)) {
			return (SteamOpen.SteamBusy, new Said("one Open in Steam is already running - wait for it to finish").ToEnglish());
		}

		try {
			SteamOpen.Inputs inputs = Read();
			SteamOpen.Plan plan = SteamOpen.Decide(login, steamId, inputs);

			if (plan.Code is not (SteamOpen.Ok or SteamOpen.SignIn)) {
				Log.Info(SteamOpen.Message(plan.Code, display), display);

				return (plan.Code, SteamOpen.Message(plan.Code, display).ToEnglish());
			}

			string? steamPath = SteamPath();
			string? exe = SteamExe(steamPath);

			if ((steamPath == null) || (exe == null)) {
				Log.Info(SteamOpen.Message(SteamOpen.NoSteam, display), display);

				return (SteamOpen.NoSteam, SteamOpen.Message(SteamOpen.NoSteam, display).ToEnglish());
			}

			string vdfPath = VdfPath(steamPath);
			string configPath = ConfigPath(steamPath);
			Exception? writeError = null;

			var ops = new SteamOpen.Ops(
				Running: SteamRunning,
				CloseAndWait: () => CloseSteamAndWaitAsync(exe),
				// Read again once Steam has closed: it saves its files as it goes, and the change goes onto what it saved.
				Replan: () => SteamOpen.Decide(login, steamId, Read()),
				Backup: p => BackUp(vdfPath, configPath, p, inputs),
				Write: p => {
					try {
						if (p.NewVdf != null) {
							AtomicFile.Write(vdfPath, p.NewVdf);
						}

						// 1.8.2: "Ask which account to use each time Steam starts" off - only that one value changes in
						// config.vdf, every other byte stays as it was.
						if (p.NewConfig != null) {
							WriteExact(configPath, p.NewConfig);
							Log.Info(SteamOpen.ChooserLine, display);
						}

						// Only AutoLoginUser - the single "sign this one in" pointer (for an account Steam doesn't remember,
						// the name its sign-in is filled in with). RememberPassword is the owner's own global choice and is
						// never written here; no password or token is written anywhere.
						using RegistryKey key = Registry.CurrentUser.CreateSubKey(SteamKey, true);
						key.SetValue("AutoLoginUser", p.NewAutoLoginUser ?? login, RegistryValueKind.String);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
						writeError = e;
					}
				},
				Start: () => {
					if (writeError != null) {
						return;   // the write failed, so don't start Steam onto a half-written change
					}

					// 1.8.2: the way a double-click starts it - through Explorer, so Steam isn't nocat.farm's child.
					try {
						Process.Start(SteamOpen.StartInfo(exe, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))?.Dispose();
					} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
						Log.Failed("couldn't start Steam again", e, display);
					}
				});

			SteamOpen.Plan done = await SteamOpen.ApplyAsync(plan, ops).ConfigureAwait(false);

			if (writeError != null) {
				Log.Failed("couldn't write Steam's saved-accounts files", writeError, display);

				return (SteamOpen.NoSteam, new Said("couldn't change Steam's saved accounts - {0}", Log.Scrub(writeError.Message)).ToEnglish());
			}

			Said said = SteamOpen.Message(done.Code, display, done.NewAutoLoginUser);
			Log.Info(said, display);

			return (done.Code, said.ToEnglish());
		} finally {
			OneAtATime.Release();
		}
	}

	/// <summary>
	/// Close Steam the polite way (steam.exe -shutdown) and wait up to ~60 seconds for it to go. True once it's gone (or was
	/// never up); false if it wouldn't close - we never kill it, so a client mid-save is left to finish and the caller
	/// changes nothing.
	/// </summary>
	private static async Task<bool> CloseSteamAndWaitAsync(string exe) {
		if (!SteamRunning()) {
			return true;
		}

		// 1.8.2: through the shell too (no handles inherited, never elevated) - see SteamOpen.ShutdownInfo.
		try {
			Process.Start(SteamOpen.ShutdownInfo(exe))?.Dispose();
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			Log.Debug($"steam -shutdown wouldn't start: {Log.Describe(e)}");
		}

		// Up to ~60 seconds for it to exit on its own.
		for (int i = 0; (i < 120) && SteamRunning(); i++) {
			await Task.Delay(500).ConfigureAwait(false);
		}

		return !SteamRunning();
	}

	private static string BackupDir => Path.Combine(ConfigStore.ConfigDir, "state", "steam-open-backups");

	/// <summary>
	/// Copy the saved-accounts file and note the old registry values before any change, into a folder of its own (a
	/// timestamp plus a short unique tag, so two presses in the same second never share - or overwrite - a folder). Keeps
	/// the last three so a mistake can always be undone by hand; never touches a token. True on success; false when it
	/// couldn't back up, so the caller leaves the files alone.
	/// </summary>
	private static bool BackUp(string vdfPath, string configPath, SteamOpen.Plan plan, SteamOpen.Inputs inputs) {
		try {
			Directory.CreateDirectory(BackupDir);
			string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
			string into = Path.Combine(BackupDir, $"{stamp}-{Guid.NewGuid().ToString("N")[..6]}");
			Directory.CreateDirectory(into);

			if (File.Exists(vdfPath)) {
				File.Copy(vdfPath, Path.Combine(into, "loginusers.vdf"), overwrite: false);
			}

			// 1.8.2: config.vdf too, whenever the account picker is about to be turned off in it.
			if ((plan.NewConfig != null) && File.Exists(configPath)) {
				File.Copy(configPath, Path.Combine(into, "config.vdf"), overwrite: false);
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

			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug($"couldn't back up Steam's saved-accounts files: {Log.Describe(e)}");

			return false;
		}
	}
}
