using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>
/// What the setup wizard chose - language, starting with Windows, the dashboard, phone and "from anywhere" - saved
/// into the config before nocat.farm first starts, through the same checks as 'set'. The installer never writes the
/// config itself, so the file only ever has one writer and one format. Nothing secret comes this way: the dashboard
/// password is chosen in the dashboard's own first-run setup.
/// </summary>
public static class SetupChoices {
	/// <summary>The settings the wizard offers. Anything else is refused, so nothing unexpected can slip in.</summary>
	private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) {
		"Language", "StartWithWindows", "StartMinimized", "Tray", "UpdateMode", "WebEnabled", "WebPort", "WebHost", "WebRemoteAccess"
	};

	/// <summary>Puts the startup entry in step with the choice - Windows only. Tests put their own here, so a test of the
	/// setup never touches the real startup entry.</summary>
	public static Action<bool> StartWithWindows { get; set; } = static on => {
		if (OperatingSystem.IsWindows()) {
			Windows.WindowsIntegration.SetStartWithWindows(on);
		}
	};

	/// <summary>The choices were fine but didn't reach the disk - a folder it can't write, a config that didn't load.</summary>
	public const int SaveFailed = 4;

	/// <summary>Why the last run didn't end in 0, in plain words; null when it did.</summary>
	public static string? Problem { get; private set; }

	/// <summary>Where a setup that didn't save says why. The installer reads its last line.</summary>
	public static string LogPath => Path.Combine(ConfigStore.Root, "logs", "setup.log");

	/// <summary>
	/// Apply, for the installer. This runs before logging starts and the installer only ever sees the exit code, so a
	/// save that failed used to come back as 0 - "saved" - with the choices gone. Anything but 0 now also says why, on
	/// stderr and in logs\setup.log, and a throw is an exit code too rather than a crash with nothing written down.
	/// </summary>
	public static int Run(IEnumerable<string> choices) {
		int code;

		try {
			code = Apply(choices);
		} catch (Exception e) {
			code = SaveFailed;
			Problem = $"couldn't save the setup's choices: {Log.Describe(e)}";
		}

		if (code != 0) {
			Report(code);
		}

		return code;
	}

	private static void Report(int code) {
		string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} --setup exit {code}: {Problem ?? "no reason given"}";

		try {
			Console.Error.WriteLine(line);
		} catch {
			// no stderr - the file below still has it
		}

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
			File.AppendAllText(LogPath, line + Environment.NewLine);
		} catch {
			// a folder it can't write to is often the very problem; stderr and the exit code still say it
		}
	}

	/// <summary>0 saved; 2 a choice it doesn't take or a value that doesn't fit; 3 nocat.farm is running from this folder;
	/// 4 (SaveFailed) the choices couldn't be written. <see cref="Problem"/> says why.</summary>
	public static int Apply(IEnumerable<string> choices) {
		Problem = null;

		// Not while it's running: the running copy would write its own settings over these.
		if (Mutex.TryOpenExisting(AppInstance.LockName(ConfigStore.Root), out Mutex? held)) {
			held.Dispose();
			Problem = "nocat.farm is running from this folder - close it first";

			return 3;
		}

		GlobalConfig g = ConfigStore.LoadGlobal();
		string? importFrom = null, importPath = null;

		foreach (string choice in choices) {
			int eq = choice.IndexOf('=');
			string name = choice[..eq].Trim();
			string value = choice[(eq + 1)..].Trim();

			// "coming from" another idler: not a setting - the dashboard's first-run setup opens on importing from it.
			if (name.Equals("ImportFrom", StringComparison.OrdinalIgnoreCase)) {
				importFrom = value.ToLowerInvariant();

				continue;
			}

			if (name.Equals("ImportPath", StringComparison.OrdinalIgnoreCase)) {
				importPath = value;

				continue;
			}

			if (!Allowed.Contains(name) || (Settings.FindGlobal(name) is not { } def) || (Settings.Apply(g, def, value) != null)) {
				// The name only - a refused choice could be a password somebody tried to pass, and this goes in a file.
				Problem = $"'{name}' can't be set from the setup, or its value doesn't fit";

				return 2;
			}
		}

		// Checked: the choices said "saved" whether or not they were, and were simply gone at the first start.
		if (!ConfigStore.SaveGlobal(g)) {
			Problem = ConfigStore.LastSaveProblem ?? $"couldn't write {ConfigStore.GlobalPath}";

			return SaveFailed;
		}

		// The setup's three choices, or any importer's own id (IdlerImport.Tools) for a setup that names one directly.
		if ((importFrom != null) && ((importFrom is "asf" or "idlemaster" or "other") || (IdlerImport.Find(importFrom) != null))) {
			PendingImport.Save(importFrom, importPath);
		} else if (importFrom is "none") {
			// Coming from nothing - and an import asked for by an earlier install into the same folder is forgotten.
			PendingImport.Clear();
		}

		// Straight away, not at the first start - closing the setup without opening nocat.farm must still leave it
		// starting with Windows (or not), as chosen.
		StartWithWindows(g.StartWithWindows);

		return 0;
	}
}
