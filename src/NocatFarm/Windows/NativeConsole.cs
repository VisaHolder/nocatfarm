using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NocatFarm.Windows;

/// <summary>
/// The two things the console window needs from Win32: ANSI escape support, and nocatFarm's own icon.
///
/// The icon matters more than it sounds. A console app inherits its host's identity, so the window - and
/// whatever the taskbar and Alt-Tab show for it - is a generic terminal rather than this program. Stamping the
/// icon on the host window makes the console and the tray icon read as one application instead of two.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeConsole {
	private const int StdOutputHandle = -11;
	private const uint EnableVirtualTerminalProcessing = 0x0004;

	private const int WmSetIcon = 0x0080;
	private const int IconSmall = 0;
	private const int IconBig = 1;

	private const uint ImageIcon = 1;
	private const uint LoadFromFile = 0x0010;
	private const uint DefaultSize = 0x0040;

	private const uint GaRoot = 2;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleMode(IntPtr handle, uint mode);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetAncestor(IntPtr window, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

	/// <summary>
	/// Make a console, for the times we actually want one.
	///
	/// The exe is a WINDOWED binary, so Windows never gives it a console - which is the whole point: a console
	/// app gets its window before any of our code runs and there is no way to stop it flashing up first. The
	/// cost is that the handful of paths that genuinely need one (--no-gui, --help, a window that refused to
	/// open) have to ask, and stdout has to be re-pointed at it afterwards or every Write goes nowhere.
	/// </summary>
	public static bool Attach() {
		try {
			// Inherit a console if we were launched from one - running nocatFarm --help from a terminal should
			// print into THAT terminal, not pop a new black box next to it.
			if (!AttachConsole(AttachParentProcess) && !AllocConsole()) {
				return false;
			}

			StreamWriter output = new(Console.OpenStandardOutput()) { AutoFlush = true };
			Console.SetOut(output);
			Console.SetError(output);
			Console.SetIn(new StreamReader(Console.OpenStandardInput()));

			return true;
		} catch {
			return false;
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AllocConsole();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AttachConsole(uint processId);

	private const uint AttachParentProcess = 0xFFFFFFFF;

	/// <summary>
	/// Let the console window go.
	///
	/// This program is a console app that grew a window. Once the window is up the console is pure clutter: a
	/// second window on screen, a second entry in the taskbar, and on some hosts a second tray icon - for a
	/// surface nobody is reading any more, since the window has its own log and its own command line.
	///
	/// Only ever called AFTER the window reports itself open, so a window that failed to start still leaves a
	/// usable console behind.
	/// </summary>
	public static void Detach() {
		try {
			IntPtr console = GetConsoleWindow();

			if (console != IntPtr.Zero) {
				// Hide the host window first. FreeConsole alone can leave an empty frame on screen for a moment
				// when something else owns the host, which looks exactly like a crash.
				IntPtr root = GetAncestor(console, GaRoot);
				ShowWindow(root != IntPtr.Zero ? root : console, SwHide);
			}

			FreeConsole();
		} catch {
			// Nothing here is worth failing a start over; the worst case is an extra window.
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FreeConsole();

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr window, int command);

	private const int SwHide = 0;

	/// <summary>Ask the console to interpret ANSI escapes. False when it can't, so the caller can fall back.</summary>
	public static bool EnableVirtualTerminal() {
		IntPtr handle = GetStdHandle(StdOutputHandle);

		if ((handle == IntPtr.Zero) || (handle == new IntPtr(-1))) {
			return false;
		}

		if (!GetConsoleMode(handle, out uint mode)) {
			return false;   // not a real console - redirected, or running as a service
		}

		if ((mode & EnableVirtualTerminalProcessing) != 0) {
			return true;
		}

		return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
	}

	/// <summary>
	/// Put nocatFarm's icon on the console window.
	///
	/// GetConsoleWindow returns the window hosting the console, which under Windows Terminal or any ConPTY host
	/// is a hidden child - the icon has to go on its ROOT ancestor, which is the window a person actually sees.
	/// </summary>
	public static void SetWindowIcon(string icoPath) {
		try {
			if (!File.Exists(icoPath)) {
				return;
			}

			IntPtr console = GetConsoleWindow();

			if (console == IntPtr.Zero) {
				return;
			}

			IntPtr window = GetAncestor(console, GaRoot);

			if (window == IntPtr.Zero) {
				window = console;
			}

			IntPtr big = LoadImage(IntPtr.Zero, icoPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
			IntPtr small = LoadImage(IntPtr.Zero, icoPath, ImageIcon, 16, 16, LoadFromFile);

			if (big != IntPtr.Zero) {
				SendMessage(window, WmSetIcon, new IntPtr(IconBig), big);
			}

			if (small != IntPtr.Zero) {
				SendMessage(window, WmSetIcon, new IntPtr(IconSmall), small);
			}
		} catch {
			// Purely cosmetic - never worth failing a start over.
		}
	}
}
