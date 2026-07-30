using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NocatFarm.Windows;

/// <summary>
/// The two bits of Windows housekeeping a background tool needs: starting with the user's session, and
/// stopping the machine sleeping out from under a farm that's been running for six hours.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsIntegration {
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "nocatFarm";

	[Flags]
	private enum ExecutionState : uint {
		Continuous = 0x80000000,
		SystemRequired = 0x00000001,
		AwayModeRequired = 0x00000040
	}

	[DllImport("kernel32.dll")]
	private static extern uint SetThreadExecutionState(ExecutionState flags);

	/// <summary>Is nocatFarm registered to start when this user signs in?</summary>
	public static bool StartsWithWindows() {
		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);

			return key?.GetValue(ValueName) != null;
		} catch {
			return false;
		}
	}

	/// <summary>
	/// Add or remove the startup entry. HKEY_CURRENT_USER only - it needs no administrator rights and it
	/// affects nobody else who uses this PC.
	/// </summary>
	public static bool SetStartWithWindows(bool enabled) {
		try {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);

			if (!enabled) {
				key.DeleteValue(ValueName, false);

				return true;
			}

			string? exe = Environment.ProcessPath;

			if (string.IsNullOrEmpty(exe)) {
				return false;
			}

			// --minimized, because something launched at sign-in should not throw a console window in your face.
			key.SetValue(ValueName, $"\"{exe}\" --minimized");

			return true;
		} catch (Exception e) {
			Log.Warn($"couldn't change the Windows startup entry: {e.Message}");

			return false;
		}
	}

	private static bool _awake;

	/// <summary>
	/// Hold the machine awake, or let it sleep again. The flag is per-thread, so this is called from a thread
	/// that lives as long as the process - the timer callback would set it on a pool thread that then dies.
	/// </summary>
	public static void KeepAwake(bool keep) {
		if (keep == _awake) {
			return;
		}

		try {
			// AwayModeRequired keeps work going with the screen off rather than only deferring the sleep timer.
			SetThreadExecutionState(keep
				? ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.AwayModeRequired
				: ExecutionState.Continuous);

			_awake = keep;
		} catch (Exception e) {
			Log.Debug($"keep-awake: {e.Message}");
		}
	}
}
