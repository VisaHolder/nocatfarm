using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

using NocatFarm.Core;

namespace NocatFarm.Windows;

/// <summary>
/// The two bits of Windows housekeeping a background tool needs: starting with the user's session, and
/// stopping the machine sleeping out from under a farm that's been running for six hours.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsIntegration {
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "nocatFarm";

	// A power request, not SetThreadExecutionState. That one belongs to the calling THREAD: set it from a pool
	// thread (startup runs on one, and so does every settings change) and it lapses when that thread is recycled,
	// and clearing it from a different thread clears nothing - so switching it off could keep the PC awake until
	// a restart. A power request is a handle the process owns, and any thread can set or clear it.
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ReasonContext {
		public uint Version;
		public uint Flags;
		[MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
	}

	private enum PowerRequestType {
		SystemRequired = 1,
		AwayModeRequired = 2
	}

	private const uint PowerRequestContextVersion = 0;
	private const uint PowerRequestContextSimpleString = 0x1;

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	/// <summary>
	/// Every monitor off, now - the 'screen off' command. What the power button's "turn off the display" does: moving
	/// the mouse or pressing a key brings them back. Posted, not sent: a broadcast send waits on every window there is,
	/// and one hung program would hang this with it.
	/// </summary>
	public static void ScreenOff() {
		const uint WmSysCommand = 0x0112;
		const int ScMonitorPower = 0xF170;
		PostMessage(new IntPtr(0xFFFF), WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(2));
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PowerSetRequest(IntPtr request, PowerRequestType type);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PowerClearRequest(IntPtr request, PowerRequestType type);

	/// <summary>Is nocatFarm registered to start when this user signs in?</summary>
	public static bool StartsWithWindows() {
		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);

			return key?.GetValue(ValueName) != null;
		} catch (Exception e) {
			Log.Failed("reading the Windows startup entry", e);

			return false;
		}
	}

	/// <summary>Whether the startup entry starts THIS exe - a moved or copied folder leaves it pointing at the old one.</summary>
	public static bool StartupPointsHere() {
		try {
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);

			return string.Equals(key?.GetValue(ValueName) as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
		} catch (Exception e) {
			Log.Failed("reading the Windows startup entry", e);

			return false;
		}
	}

	private static bool StartsThisExe(string command) =>
		!string.IsNullOrEmpty(Environment.ProcessPath) && string.Equals(ExeOf(command), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

	/// <summary>The program a startup command runs: the quoted path, or everything up to ".exe".</summary>
	internal static string ExeOf(string command) {
		command = command.Trim();

		if (command.StartsWith('"')) {
			int end = command.IndexOf('"', 1);

			return end > 0 ? command[1..end] : command.Trim('"');
		}

		int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

		return exe >= 0 ? command[..(exe + 4)] : command;
	}

	// --minimized, because something launched at sign-in should not throw a console window in your face.
	private static string? StartupCommand() => string.IsNullOrEmpty(Environment.ProcessPath) ? null : $"\"{Environment.ProcessPath}\" --minimized";

	/// <summary>
	/// Add or remove the startup entry. HKEY_CURRENT_USER only - it needs no administrator rights and it
	/// affects nobody else who uses this PC.
	/// </summary>
	public static bool SetStartWithWindows(bool enabled) {
		try {
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);

			if (!enabled) {
				// Only our own entry: a second copy (a portable one opened to check something, a test copy) with this
				// switched off must not take the startup away from the copy that has it. One pointing at a program
				// that's gone - the folder was moved or deleted - is fair game.
				if (key.GetValue(ValueName) is string current && (StartsThisExe(current) || !File.Exists(ExeOf(current)))) {
					key.DeleteValue(ValueName, false);
				}

				return true;
			}

			if (StartupCommand() is not { } command) {
				return false;
			}

			key.SetValue(ValueName, command);

			return true;
		} catch (Exception e) {
			Log.Warn(new Said("couldn't change the Windows startup entry: {0}", Log.Describe(e)));

			return false;
		}
	}

	private static readonly Lock AwakeGate = new();
	private static IntPtr _request = IntPtr.Zero;
	private static bool _awake;
	private static bool _awayMode;

	/// <summary>Hold the machine awake while nocat.farm is open, or let it sleep again. Safe from any thread.</summary>
	public static void KeepAwake(bool keep) {
		lock (AwakeGate) {
			if (keep == _awake) {
				return;
			}

			try {
				if (_request == IntPtr.Zero) {
					ReasonContext reason = new() {
						Version = PowerRequestContextVersion,
						Flags = PowerRequestContextSimpleString,
						SimpleReasonString = "nocat.farm is running your Steam accounts"
					};

					IntPtr handle = PowerCreateRequest(ref reason);

					if ((handle == IntPtr.Zero) || (handle == new IntPtr(-1))) {
						Log.Debug(new Said("keep-awake: {0}", Marshal.GetLastPInvokeErrorMessage()));

						return;
					}

					_request = handle;
				}

				if (keep) {
					if (!PowerSetRequest(_request, PowerRequestType.SystemRequired)) {
						Log.Debug(new Said("keep-awake: {0}", Marshal.GetLastPInvokeErrorMessage()));

						return;
					}

					// Away mode keeps work going with the screen off rather than only deferring the sleep timer. Not
					// every PC allows it, and that's fine - SystemRequired alone still stops the sleep.
					_awayMode = PowerSetRequest(_request, PowerRequestType.AwayModeRequired);
				} else {
					PowerClearRequest(_request, PowerRequestType.SystemRequired);

					if (_awayMode) {
						PowerClearRequest(_request, PowerRequestType.AwayModeRequired);
						_awayMode = false;
					}
				}

				_awake = keep;
			} catch (Exception e) {
				Log.Debug(new Said("keep-awake: {0}", Log.Describe(e)));
			}
		}
	}
}
