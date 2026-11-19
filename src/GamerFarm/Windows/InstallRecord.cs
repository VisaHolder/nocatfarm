using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GamerFarm.Windows;

/// <summary>
/// The entry the installer leaves in Windows' installed-apps list (Settings > Apps). gamer.farm updates itself, so
/// without this the list would go on showing the version that was first installed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallRecord {
	/// <summary>The installer's AppId - never changes, or Windows would list two gamer.farms.</summary>
	public const string AppId = "{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}";

	private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

	/// <summary>The name the list shows. 2.0.0: an install made before the rename is listed under the old name until this says otherwise.</summary>
	internal const string DisplayName = "gamer.farm";

	/// <summary>Bring the listed version (and, from 2.0.0, the name) up to this one, when this copy is the one that was installed.</summary>
	public static void Refresh() {
		try {
			using RegistryKey? key = Here(writable: true);

			if ((key != null) && !string.Equals(key.GetValue("DisplayVersion") as string, Build.Version, StringComparison.Ordinal)) {
				key.SetValue("DisplayVersion", Build.Version);
			}

			// Only the name Windows shows: the uninstaller works from its own files, not from this.
			if ((key != null) && !string.Equals(key.GetValue("DisplayName") as string, DisplayName, StringComparison.Ordinal)) {
				key.SetValue("DisplayName", DisplayName);
			}

			// 2.0.1: the icon from the program as it's called now - an install from before showed the old program's.
			string icon = Path.Combine(AppContext.BaseDirectory, "gamerFarm.exe");

			if ((key != null) && File.Exists(icon) && !string.Equals(key.GetValue("DisplayIcon") as string, icon, StringComparison.OrdinalIgnoreCase)) {
				key.SetValue("DisplayIcon", icon);
			}
		} catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) {
			// the list shows the old number - nothing else depends on it
			Log.Failed("install record: updating the version Windows lists", e);
		}
	}

	/// <summary>Whether this copy is the one the setup installed (a portable copy isn't).</summary>
	public static bool IsThisFolder() {
		try {
			using RegistryKey? key = Here();

			return key != null;
		} catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) {
			return false;
		}
	}

	/// <summary>The installed-apps entry, only when it points at this folder - a portable copy never touches it.</summary>
	private static RegistryKey? Here(bool writable = false) {
		RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key, writable);
		string? place = key?.GetValue("InstallLocation") as string;

		if ((key != null) && (place != null)
			&& string.Equals(Path.GetFullPath(place).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) {
			return key;
		}

		key?.Dispose();

		return null;
	}
}
