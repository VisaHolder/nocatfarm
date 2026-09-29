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
		"Language", "StartWithWindows", "StartMinimized", "Tray", "AutoUpdate", "WebEnabled", "WebPort", "WebHost", "WebRemoteAccess"
	};

	/// <summary>0 saved; 2 a choice it doesn't take or a value that doesn't fit; 3 nocat.farm is running from this folder.</summary>
	public static int Apply(IEnumerable<string> choices) {
		// Not while it's running: the running copy would write its own settings over these.
		if (Mutex.TryOpenExisting(AppInstance.LockName(ConfigStore.Root), out Mutex? held)) {
			held.Dispose();

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
				return 2;
			}
		}

		ConfigStore.SaveGlobal(g);

		// The setup's three choices, or any importer's own id (IdlerImport.Tools) for a setup that names one directly.
		if ((importFrom != null) && ((importFrom is "asf" or "idlemaster" or "other") || (IdlerImport.Find(importFrom) != null))) {
			PendingImport.Save(importFrom, importPath);
		} else if (importFrom is "none") {
			// Coming from nothing - and an import asked for by an earlier install into the same folder is forgotten.
			PendingImport.Clear();
		}

		// Straight away, not at the first start - closing the setup without opening nocat.farm must still leave it
		// starting with Windows (or not), as chosen.
		if (OperatingSystem.IsWindows()) {
			Windows.WindowsIntegration.SetStartWithWindows(g.StartWithWindows);
		}

		return 0;
	}
}
