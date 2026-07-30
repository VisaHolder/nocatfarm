using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NocatFarm.Windows;

/// <summary>
/// Notification-area icon, so nocatFarm can sit in the background of a normal PC instead of owning a console
/// window all day. Right-click for the menu, double-click to open the dashboard.
///
/// This is hand-rolled Win32 rather than WinForms on purpose: WinForms would pin the whole build to a
/// Windows-only target framework for one icon. A hidden message-only window on its own thread pumps the
/// messages Shell_NotifyIcon sends back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable {
	private const int WmDestroy = 0x0002;
	private const int WmCommand = 0x0111;
	private const int WmApp = 0x8000;
	private const int WmTrayCallback = WmApp + 1;
	private const int WmLButtonDblClk = 0x0203;
	private const int WmRButtonUp = 0x0205;
	private const int WmLButtonUp = 0x0202;

	private const int NimAdd = 0x0000;
	private const int NimModify = 0x0001;
	private const int NimDelete = 0x0002;

	private const int NifMessage = 0x0001;
	private const int NifIcon = 0x0002;
	private const int NifTip = 0x0004;
	private const int NifInfo = 0x0010;
	private const int NifGuid = 0x0020;

	private const int SwHide = 0;
	private const int SwShow = 5;
	private const int SwRestore = 9;

	private const uint MfString = 0x0000;
	private const uint MfSeparator = 0x0800;
	private const uint TpmRightButton = 0x0002;
	private const uint TpmReturnCmd = 0x0100;

	private const int IdOpenWeb = 1;
	private const int IdToggleConsole = 2;
	private const int IdStartAll = 3;
	private const int IdStopAll = 4;
	private const int IdExit = 9;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData {
		public int cbSize;
		public IntPtr hWnd;
		public int uID;
		public int uFlags;
		public int uCallbackMessage;
		public IntPtr hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
		public int dwState;
		public int dwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
		public int uVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
		public int dwInfoFlags;
		public Guid guidItem;
		public IntPtr hBalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WndClassEx {
		public int cbSize;
		public int style;
		public IntPtr lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public IntPtr hInstance;
		public IntPtr hIcon;
		public IntPtr hCursor;
		public IntPtr hbrBackground;
		public IntPtr lpszMenuName;
		public IntPtr lpszClassName;
		public IntPtr hIconSm;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Msg {
		public IntPtr hwnd;
		public int message;
		public IntPtr wParam;
		public IntPtr lParam;
		public int time;
		public int ptX;
		public int ptY;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point {
		public int X;
		public int Y;
	}

	private delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx wc);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
	[DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hWnd, int min, int max);
	[DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
	[DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
	[DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
	[DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
	[DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? item);
	[DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);
	[DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
	[DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
	[DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
	[DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr instance, string exeFileName, int iconIndex);

	private readonly string _tooltip;
	private readonly Func<string> _webUrl;
	private readonly Action _startAll;
	private readonly Action _stopAll;
	private readonly Action _exit;

	private readonly WndProc _wndProcDelegate;   // must outlive the window, or the GC collects the callback

	private Thread? _thread;
	private IntPtr _hwnd;
	private IntPtr _icon;
	private bool _consoleVisible = true;
	private bool _added;

	public TrayIcon(string tooltip, Func<string> webUrl, Action startAll, Action stopAll, Action exit) {
		_tooltip = tooltip;
		_webUrl = webUrl;
		_startAll = startAll;
		_stopAll = stopAll;
		_exit = exit;
		_wndProcDelegate = HandleMessage;
	}

	/// <summary>Create the icon on its own message-pumping thread. Never throws - a missing tray is not fatal.</summary>
	public void Start(bool startHidden) {
		_thread = new Thread(() => Run(startHidden)) {
			IsBackground = true,
			Name = "nocatFarm tray"
		};

		_thread.SetApartmentState(ApartmentState.STA);
		_thread.Start();
	}

	private void Run(bool startHidden) {
		try {
			IntPtr instance = GetModuleHandle(null);
			string className = "nocatFarmTray";

			WndClassEx wc = new() {
				cbSize = Marshal.SizeOf<WndClassEx>(),
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
				hInstance = instance,
				lpszClassName = Marshal.StringToHGlobalUni(className)
			};

			RegisterClassEx(ref wc);

			// A message-only window: never shown, exists purely to receive the tray callbacks.
			_hwnd = CreateWindowEx(0, className, "nocatFarm", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);

			if (_hwnd == IntPtr.Zero) {
				Log.Debug("tray: couldn't create the message window - running without a tray icon");

				return;
			}

			_icon = LoadOwnIcon();

			NotifyIconData data = NewData();
			data.uFlags = NifMessage | NifIcon | NifTip | NifGuid;
			data.uCallbackMessage = WmTrayCallback;
			data.hIcon = _icon;
			data.szTip = _tooltip;
			_added = Shell_NotifyIcon(NimAdd, ref data);

			if (!_added) {
				// A GUID-identified icon is refused if Windows still has one registered from a process that was
				// killed rather than closed - which is exactly the case that leaves a dead icon behind. Delete
				// the stale registration and claim it again, so there is only ever one nocatFarm in the tray.
				NotifyIconData stale = NewData();
				stale.uFlags = NifGuid;
				Shell_NotifyIcon(NimDelete, ref stale);

				data = NewData();
				data.uFlags = NifMessage | NifIcon | NifTip | NifGuid;
				data.uCallbackMessage = WmTrayCallback;
				data.hIcon = _icon;
				data.szTip = _tooltip;
				_added = Shell_NotifyIcon(NimAdd, ref data);
			}

			if (!_added) {
				// Some setups refuse GUID icons outright (the exe was moved since it was registered). Fall back
				// to a plain one rather than running with no tray at all.
				data = NewData();
				data.uFlags = NifMessage | NifIcon | NifTip;
				data.uCallbackMessage = WmTrayCallback;
				data.hIcon = _icon;
				data.szTip = _tooltip;
				_usingGuid = false;
				_added = Shell_NotifyIcon(NimAdd, ref data);
			}

			if (!_added) {
				Log.Warn("couldn't add the notification-area icon - nocatFarm still runs, it just won't show in the tray");
			} else {
				Log.Debug("tray icon added");
			}

			if (startHidden) {
				ShowConsole(false);
			}

			StartMinimizeWatcher();

			while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0) {
				TranslateMessage(ref msg);
				DispatchMessage(ref msg);
			}
		} catch (Exception e) {
			Log.Debug($"tray: {e.Message}");
		}
	}

	/// <summary>
	/// Minimise-to-tray. A console window is not ours to subclass, so instead of hooking its message loop this
	/// notices it has been minimised and hides it. Half a second of lag is invisible and it can't break anything.
	/// </summary>
	private void StartMinimizeWatcher() {
		Thread watcher = new(() => {
			while (true) {
				try {
					Thread.Sleep(500);

					if (!MinimizeToTray || !_consoleVisible || !HasRealConsole()) {
						continue;
					}

					IntPtr console = ConsoleHostWindow();

					if ((console != IntPtr.Zero) && IsIconic(console)) {
						ShowWindow(console, SwHide);
						_consoleVisible = false;
					}
				} catch {
					return;
				}
			}
		}) { IsBackground = true, Name = "nocatFarm minimise watcher" };

		watcher.Start();
	}

	/// <summary>Whether minimising the console should hide it. Read live so the setting applies without a restart.</summary>
	public bool MinimizeToTray { get; set; } = true;

	/// <summary>The icon compiled into the exe, falling back to nothing rather than failing.</summary>
	private static IntPtr LoadOwnIcon() {
		try {
			string? exe = Environment.ProcessPath;

			return string.IsNullOrEmpty(exe) ? IntPtr.Zero : ExtractIcon(GetModuleHandle(null), exe, 0);
		} catch {
			return IntPtr.Zero;
		}
	}

	/// <summary>
	/// A fixed identity for the icon. Without it, a process that was killed rather than closed leaves its icon
	/// registered and the next run adds a SECOND one - which is how you end up with two nocatFarms by the clock.
	/// </summary>
	private static readonly Guid IconId = new("6ec0f3b2-2d47-4f0e-9a1c-7b5e0f2a11d4");

	private bool _usingGuid = true;

	private NotifyIconData NewData() => new() {
		cbSize = Marshal.SizeOf<NotifyIconData>(),
		hWnd = _hwnd,
		uID = 1,
		guidItem = _usingGuid ? IconId : Guid.Empty,
		szTip = "",
		szInfo = "",
		szInfoTitle = ""
	};

	private IntPtr HandleMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam) {
		switch (msg) {
			case WmTrayCallback:
				switch ((int) lParam) {
					case WmLButtonDblClk:
						// Double-click brings the app back. Opening a browser tab was the old behaviour from when
						// the dashboard was the only face it had; now there is a window, and that is what people
						// expect a tray icon to restore.
						if (Commands.Window is { } window) {
							window.Show();
						} else {
							OpenWeb();
						}

						break;
					case WmLButtonUp:
					case WmRButtonUp:
						ShowMenu();

						break;
				}

				return IntPtr.Zero;
			case WmCommand:
				OnCommand((int) wParam & 0xFFFF);

				return IntPtr.Zero;
			case WmDestroy:
				PostQuitMessage(0);

				return IntPtr.Zero;
			default:
				return DefWindowProc(hWnd, msg, wParam, lParam);
		}
	}

	private void ShowMenu() {
		IntPtr menu = CreatePopupMenu();

		if (menu == IntPtr.Zero) {
			return;
		}

		try {
			AppendMenu(menu, MfString, IdOpenWeb, "Open dashboard");

			// Only offer it when there is a REAL console window to show or hide.
			if (Commands.Window != null) {
				AppendMenu(menu, MfString, IdToggleConsole, Commands.Window.Visible ? "Hide the window" : "Show the window");
			} else if (HasRealConsole()) {
				AppendMenu(menu, MfString, IdToggleConsole, _consoleVisible ? "Hide the window" : "Show the window");
			}

			AppendMenu(menu, MfSeparator, 0, null);
			AppendMenu(menu, MfString, IdStartAll, "Start all accounts");
			AppendMenu(menu, MfString, IdStopAll, "Stop all accounts");
			AppendMenu(menu, MfSeparator, 0, null);
			AppendMenu(menu, MfString, IdExit, "Exit nocatFarm");

			GetCursorPos(out Point p);

			// Required by Windows, otherwise the menu refuses to close when you click elsewhere.
			SetForegroundWindow(_hwnd);

			int chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, p.X, p.Y, _hwnd, IntPtr.Zero);

			if (chosen != 0) {
				OnCommand(chosen);
			}
		} finally {
			DestroyMenu(menu);
		}
	}

	private void OnCommand(int id) {
		switch (id) {
			case IdOpenWeb:
				OpenWeb();

				break;
			case IdToggleConsole:
				if (Commands.Window is { } window) {
					window.Toggle();

					break;
				}

				ShowConsole(!_consoleVisible);

				break;
			case IdStartAll:
				_startAll();

				break;
			case IdStopAll:
				_stopAll();

				break;
			case IdExit:
				_exit();

				break;
		}
	}

	private void OpenWeb() {
		string url = _webUrl();

		if (string.IsNullOrEmpty(url)) {
			ShowConsole(true);   // no dashboard running - the console is the interface

			return;
		}

		try {
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		} catch (Exception e) {
			Log.Warn($"couldn't open {url}: {e.Message}");
		}
	}

	private const uint GaRoot = 2;

	/// <summary>
	/// The window a human would actually click to get to our console.
	///
	/// GetConsoleWindow alone is not it. Under Windows Terminal (and anything else using a pseudo-console) that
	/// handle is an invisible PseudoConsoleWindow sitting inside the terminal's real window - so hiding it did
	/// nothing at all, which is exactly what "show terminal doesn't work" looks like. Walking up to the root
	/// window gets the terminal itself, and the classic conhost case walks up to itself.
	/// </summary>
	private static IntPtr ConsoleHostWindow() {
		IntPtr console = GetConsoleWindow();

		if (console == IntPtr.Zero) {
			return IntPtr.Zero;
		}

		IntPtr root = GetAncestor(console, GaRoot);

		return root != IntPtr.Zero ? root : console;
	}

	private static bool HasRealConsole() => ConsoleHostWindow() != IntPtr.Zero;

	/// <summary>Hide or show the console window. Hiding it is what makes this a background app.</summary>
	public void ShowConsole(bool show) {
		IntPtr console = ConsoleHostWindow();

		if (console == IntPtr.Zero) {
			return;
		}

		ShowWindow(console, show ? SwRestore : SwHide);

		if (show) {
			ShowWindow(console, SwShow);
			SetForegroundWindow(console);
		}

		_consoleVisible = show;
	}

	public bool ConsoleVisible => IsWindowVisible(ConsoleHostWindow());

	/// <summary>Balloon notification. Silently does nothing if the icon never got created.</summary>
	public void Notify(string title, string text) {
		if (!_added) {
			return;
		}

		NotifyIconData data = NewData();
		data.uFlags = NifInfo | (_usingGuid ? NifGuid : 0);
		data.szInfoTitle = title.Length > 60 ? title[..60] : title;
		data.szInfo = text.Length > 250 ? text[..250] : text;
		Shell_NotifyIcon(NimModify, ref data);
	}

	public void Dispose() {
		if (_added) {
			NotifyIconData data = NewData();
			data.uFlags = _usingGuid ? NifGuid : 0;
			Shell_NotifyIcon(NimDelete, ref data);
			_added = false;
		}

		if (_hwnd != IntPtr.Zero) {
			DestroyWindow(_hwnd);
			_hwnd = IntPtr.Zero;
		}
	}
}
