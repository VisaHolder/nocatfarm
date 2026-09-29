using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NocatFarm.Windows;

/// <summary>
/// The entry the installer leaves in Windows' installed-apps list (Settings > Apps). nocat.farm updates itself, so
/// without this the list would go on showing the version that was first installed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallRecord {
	/// <summary>The installer's AppId - never changes, or Windows would list two nocat.farms.</summary>
	public const string AppId = "{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}";

	private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

	/// <summary>Bring the listed version up to this one, when this copy is the one that was installed.</summary>
	public static void Refresh() {
		try {
			using RegistryKey? key = Here(writable: true);

			if ((key != null) && !string.Equals(key.GetValue("DisplayVersion") as string, Build.Version, StringComparison.Ordinal)) {
				key.SetValue("DisplayVersion", Build.Version);
			}
		} catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) {
			// the list shows the old number - nothing else depends on it
			Log.Failed("install record: updating the version Windows lists", e);
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
