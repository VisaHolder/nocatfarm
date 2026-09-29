using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm.Windows;

/// <summary>
/// The window.
///
/// A console is a poor fit for this program. What you want from a farmer is a glance - which accounts are up,
/// what each is doing, how far through the day it is - and a scrolling terminal answers that badly: the state
/// you care about has already scrolled away, and everything is the same size and colour.
///
/// So this is a small fixed panel instead: a row per account with its own progress, a log underneath for what
/// just happened, and one input line because every feature is still a command. Everything is drawn by hand into
/// a back buffer - no child controls except the input - which keeps the layout under one roof and the repaint
/// flicker-free.
///
/// Native Win32 by P/Invoke rather than WinForms, for the same reason the tray icon is: WinForms would pin the
/// whole project to a Windows-only target framework for a handful of rectangles.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MainWindow : IDisposable {
	// ── the palette, matching noDeploy ──
	private static readonly int Bg = Rgb(10, 10, 10);
	private static readonly int Surface = Rgb(18, 18, 18);
	private static readonly int Panel = Rgb(14, 14, 14);
	private static readonly int Border = Rgb(42, 42, 42);
	private static readonly int TextBright = Rgb(235, 235, 235);
	private static readonly int TextNormal = Rgb(200, 200, 200);
	private static readonly int TextMid = Rgb(120, 120, 120);
	private static readonly int TextDim = Rgb(72, 72, 72);
	private static readonly int Green = Rgb(80, 200, 120);
	private static readonly int Amber = Rgb(235, 180, 60);
	private static readonly int Red = Rgb(220, 70, 70);
	private static readonly int Accent = Rgb(90, 190, 255);
	
	/// <summary>The 'farm' half of the wordmark. Deliberately NOT the accent: the accent means
	/// "interactive" - buttons, links, the account you can click - and the name is not any of those.</summary>
	private static readonly int Wordmark = Rgb(139, 92, 246);

	/// <summary>Opening size. Everything after this reads _w/_h, which follow the window.</summary>
	private const int StartW = 620;
	private const int StartH = 500;

	// The smallest the layout still reads at. Below this the log columns start colliding with the buttons.
	// These are CLIENT dimensions; the sizing border is added on top when Windows is told the limit.
	private const int MinW = 576;
	private const int MinH = 316;

	/// <summary>
	/// The CURRENT size of the client area.
	///
	/// Every layout figure derives from these rather than from constants, which is what makes the window
	/// resizable at all: with fixed dimensions the paint simply drew the old rectangle into the new one and
	/// left the rest of the window as garbage.
	/// </summary>
	private int _w = StartW;
	private int _h = StartH;
	private const int TitleH = 34;
	private const int BarH = 38;
	private const int StatusH = 24;
	private const int InputH = 28;
	private const int Pad = 14;

	/// <summary>Where the prompt marker and the input text both start, so they share one baseline.</summary>
	private int PromptY => _h - StatusH - InputH + 7;

	private const int InputX = Pad + 16;

	private const int LineH = 16;

	private readonly BotManager _mgr;
	private readonly Func<string> _url;
	private readonly Action _exit;
	private readonly List<LogLine> _log = [];

	/// <summary>Consecutive failed paints, so a persistent one gets said out loud instead of only to the file.</summary>
	private int _paintFailures;
	private readonly Lock _logGate = new();

	private IntPtr _hwnd;
	private IntPtr _fontUi;
	private IntPtr _fontBold;
	private IntPtr _fontSmall;
	private IntPtr _fontMono;
	private IntPtr _input;
	private IntPtr _originalInputProc;
	private IntPtr _inputBrush;
	private IntPtr _iconBig;
	private IntPtr _iconSmall;
	private bool _tracking;
	private WndProc? _proc;
	private WndProc? _inputProc;
	private Thread? _thread;
	private volatile bool _running;
	private readonly List<Button> _buttons = [];
	/// <summary>
	/// The button under the cursor, by ID rather than by position.
	///
	/// _buttons is rebuilt from scratch on every paint and the toolbar has a different layout inside the sheet
	/// than outside it, so an index into that list means "whatever happens to be third this frame" - closing
	/// the sheet left an unrelated button stuck in the hover state.
	/// </summary>
	private int _hoverId = -1;
	private bool _showOnCreate = true;

	/// <summary>The account the user clicked, or null. Selecting one reveals its own buttons on the row.</summary>
	private string? _selected;

	/// <summary>How many lines the log is scrolled back. 0 is "pinned to the newest", which is the default.</summary>
	private int _logScroll;

	/// <summary>First visible account row. With eighty accounts the list scrolls rather than growing forever.</summary>
	private int _accountScroll;

	/// <summary>True while the accounts sheet is open over the top of everything.</summary>
	private bool _sheet;

	/// <summary>The command reference, shown as a panel rather than sixty lines dumped into the log.</summary>
	private bool _helpSheet;

	/// <summary>
	/// Mini mode: the same window shrunk to a small always-on-top panel - a line per account, start/stop, the
	/// dashboard. One window in two layouts rather than a second window, so it shares the message pump, the
	/// fonts and the hit-testing, and the two can't disagree about what an account is doing.
	/// </summary>
	private bool _mini;

	private IntPtr _fontIcon;

	private const int MiniW = 300;
	private const int MiniTitleH = 30;
	private const int MiniRowH = 30;

	/// <summary>A farming account's row: the line, then cards left and time left, then its progress bar.</summary>
	private const int MiniFocusH = 58;

	/// <summary>Past this many accounts the rest are summed up in one line - mini is for a glance, not eighty rows.</summary>
	private const int MiniMaxRows = 10;

	// Segoe MDL2 Assets, which every Windows 10 and 11 has: drawn as text, so they take the palette like any label.
	private const string GlyphPlay = "\uE768";
	private const string GlyphStop = "\uE71A";
	private const string GlyphDashboard = "\uECA5";
	private const string GlyphFullWindow = "\uE740";
	private const string GlyphPin = "\uE718";

	public bool Mini => _mini;

	/// <summary>Wheel movement not yet a whole notch.</summary>
	private int _wheel;

	/// <summary>The question the window last surfaced itself for, so hiding it again isn't undone every second.</summary>
	private string? _surfacedFor;

	private bool _masked;

	/// <summary>
	/// Something is waiting for an answer - a Steam Guard code, a password. That can only be typed in the full window,
	/// so a hidden or mini window comes up for it (once per question), and a password is masked while it's typed.
	/// </summary>
	private void WatchPrompt() {
		string? question = Prompt.Pending;

		if ((question != null) && (question != _surfacedFor)) {
			_surfacedFor = question;

			if (_mini) {
				ApplyMini(false, remember: false);
			}

			ShowWindow(_hwnd, IsIconic(_hwnd) ? SwRestore : SwShow);
			SetForegroundWindow(_hwnd);
			SetFocus(_input);
			Visible = true;
		} else if (question == null) {
			_surfacedFor = null;
		}

		bool secret = (question != null) && Prompt.PendingSecret;

		if ((secret != _masked) && (_input != IntPtr.Zero)) {
			_masked = secret;
			SendMessage(_input, EmSetPasswordChar, new IntPtr(secret ? 0x25CF : 0), IntPtr.Zero);
			InvalidateRect(_input, IntPtr.Zero, true);
		}
	}

	private int _helpScroll;

	/// <summary>Clickable areas worked out during the paint, so hit-testing always matches what is on screen.</summary>
	private readonly List<(int X, int Y, int W, int H, string Bot, char What)> _hits = [];

	private sealed record Button(string Text, int Id, int X, int Y, int W, int H, int Colour);

	/// <summary>
	/// One row of the log pane.
	/// </summary>
	/// <remarks>
	/// Holds the Said, not the rendered text. Rendering at Append time froze each row in the language it was
	/// logged in, so switching language left every line already on screen exactly as it was - the one thing
	/// the re-rendering log exists to avoid, and this window was the surface still doing it.
	///
	/// The newline stripping happens here rather than once at append, because what gets drawn is the
	/// translation, and a translator can put a line break anywhere.
	/// </remarks>
	private sealed record LogLine(string Time, string Source, Core.Said Said, int Colour) {
		public string Text => Said.ToString().Replace('\r', ' ').Replace('\n', ' ');
	}

	public MainWindow(BotManager mgr, Func<string> url, Action exit) {
		_mgr = mgr;
		_url = url;
		_exit = exit;
	}

	public bool Visible { get; private set; }

	/// <summary>
	/// Open the window on its own thread.
	///
	/// <paramref name="show"/> is passed in rather than left to a Show() call afterwards, because the window
	/// does not exist yet when Start returns - the handle is created on the new thread. Calling Show() straight
	/// after Start() therefore did nothing at all, and the app came up with no visible window and no error.
	/// </summary>
	public void Start(bool show = true) {
		_showOnCreate = show;
		_running = true;
		Log.Debug(new Said("opening the window (show={0})", show));

		// Its own thread with its own message pump, exactly like the tray icon. A UI that shares a thread with
		// anything else is a UI that freezes whenever that thing is busy.
		_thread = new Thread(Pump) { IsBackground = true, Name = "nocatFarm window" };
		_thread.SetApartmentState(ApartmentState.STA);
		_thread.Start();
	}

	public void Show() {
		if (_hwnd != IntPtr.Zero) {
			// Restored if it was minimised (Win+Down does that, and with no taskbar button there was no getting it back).
			ShowWindow(_hwnd, IsIconic(_hwnd) ? SwRestore : SwShow);
			SetForegroundWindow(_hwnd);
			Visible = true;
		}
	}

	public void Hide() {
		if (_hwnd != IntPtr.Zero) {
			ShowWindow(_hwnd, SwHide);
			Visible = false;
		}
	}

	public void Toggle() {
		if (Visible) {
			Hide();
		} else {
			Show();
		}
	}

	/// <summary>Add a line to the log pane. Colour follows the level, the way the console did.</summary>
	public void Append(Log.Entry entry) {
		int colour = entry.Level switch {
			"GOOD" => Green,
			"WARN" => Amber,
			"ERROR" => Red,
			"DEBUG" => TextDim,
			_ => TextNormal
		};

		lock (_logGate) {
			// The three parts are kept apart so they can be drawn in fixed columns. Pre-joining them into one
			// string is what makes a log look like a wall - the eye has nothing to line up on.
			_log.Add(new LogLine(entry.When.ToString("HH:mm:ss"), entry.Source, entry.Said, colour));

			while (_log.Count > 200) {
				_log.RemoveAt(0);
			}
		}
	}

	/// <summary>Empty the log pane - the 'clear' command. The log file is untouched.</summary>
	public void ClearLog() {
		lock (_logGate) {
			_log.Clear();
			_logScroll = 0;
		}

		Invalidate();
	}

	// ── the window ──────────────────────────────────────────────────────────
	/// <summary>Raised if the window cannot be created, so the caller can put the console log back.</summary>
	public event Action? Failed;

	/// <summary>Raised once the window is genuinely on screen. Only then is it safe to drop the console.</summary>
	public event Action? Opened;

	private void Pump() {
		try {
			PumpCore();
		} catch (Exception e) {
			// A window that dies quietly is the worst outcome here: the console log has already been suppressed
			// in favour of this window, so the user is left with an app that shows them nothing at all.
			Log.Suppressed = false;
			Log.Error(new Said("window failed ({0}: {1}) - using the console", e.GetType().Name, e.Message));
			Failed?.Invoke();
		}
	}

	private void PumpCore() {
		_proc = WindowProc;

		// The app icon, for the taskbar and Alt-Tab. Without this the window has no icon and Windows shows a
		// blank generic placeholder (which reads as a stray "n"), and no name to go with it. The .ico is embedded
		// in the exe via <ApplicationIcon> AND copied beside it, so load it from the file next to us at its two
		// standard sizes - big for Alt-Tab, small for the taskbar.
		string icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "nocatFarm.ico");
		_iconBig = LoadImage(IntPtr.Zero, icoPath, ImageIcon, GetSystemMetrics(SmCxIcon), GetSystemMetrics(SmCyIcon), LrLoadFromFile);
		_iconSmall = LoadImage(IntPtr.Zero, icoPath, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);

		WndClassEx wc = new() {
			cbSize = Marshal.SizeOf<WndClassEx>(),
			lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
			hInstance = GetModuleHandle(null),
			lpszClassName = "nocatFarmWindow",
			hbrBackground = IntPtr.Zero,
			hCursor = LoadCursor(IntPtr.Zero, IdcArrow),
			hIcon = _iconBig,
			hIconSm = _iconSmall
		};

		if (RegisterClassEx(ref wc) == 0) {
			int error = Marshal.GetLastWin32Error();

			// 1410 is "class already registered", which is fine on a second start; anything else is fatal.
			if (error != 1410) {
				throw new InvalidOperationException($"RegisterClassEx failed with {error}");
			}
		}

		// Centred on the primary screen. WS_POPUP because the title bar is drawn here, not by Windows - the
		// standard one cannot be made to match the rest without fighting it.
		// Whatever it was last closed at, clamped so a saved size from another monitor cannot open it off
		// screen or smaller than the layout survives. Stored in config/nocatFarm.json next to the exe.
		int openW = Live.Global.WindowWidth > 0 ? Math.Clamp(Live.Global.WindowWidth, MinW, GetSystemMetrics(SmCxScreen)) : StartW;
		int openH = Live.Global.WindowHeight > 0 ? Math.Clamp(Live.Global.WindowHeight, MinH, GetSystemMetrics(SmCyScreen)) : StartH;

		_w = openW;
		_h = openH;

		(int x, int y) = FullSpot(openW, openH);

		// WS_THICKFRAME is what gives the edges a grab handle. The caption is still drawn by hand - this only
		// adds the sizing border, which Windows keeps outside the client area we paint.
		_hwnd = CreateWindowEx(WsExToolWindow, "nocatFarmWindow", "nocat.farm",
			unchecked((int) (WsPopup | WsClipChildren | WsMinimizeBox | WsThickFrame)), x, y, openW, openH,
			IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

		if (_hwnd == IntPtr.Zero) {
			throw new InvalidOperationException($"CreateWindowEx failed with {Marshal.GetLastWin32Error()}");
		}

		// Reaffirm the title. CreateWindowEx already set it, but doing it here too keeps the intent obvious next to
		// the other window setup. Correct now that DefWindowProc is the Unicode variant (see its declaration).
		SetWindowText(_hwnd, "nocat.farm");

		int dark = 1;
		DwmSetWindowAttribute(_hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));

		// Paint our own window border instead of inheriting the user's Windows accent colour.
		//
		// Windows 11 draws the FOCUSED window's border in the system accent colour - so alt-tabbing to this
		// window lit it up in whatever the user picked (bright pink, in one case), clashing badly with the dark
		// theme. Setting DWMWA_BORDER_COLOR pins the border to our own line grey for both focused and unfocused
		// states, so the window looks the same however the user has personalised Windows. No-ops on Windows 10,
		// where the attribute is unknown - the call just returns an error we ignore.
		int border = Border;
		DwmSetWindowAttribute(_hwnd, DwmBorderColor, ref border, sizeof(int));

		// Rounded corners in both layouts. Windows 11 rounds a window that has a sizing border on its own, which the
		// full window has and mini mode doesn't - so mini came out square next to the full one. Asked for outright
		// here; Windows 10 doesn't know the attribute and just ignores it.
		int round = DwmCornerRound;
		DwmSetWindowAttribute(_hwnd, DwmWindowCornerPreference, ref round, sizeof(int));

		// Also set the icon on the window itself. The class icon above covers a fresh start; this covers the case
		// where the class was already registered (a second window in the same process) and makes the taskbar pick
		// the icon up immediately.
		if (_iconBig != IntPtr.Zero) {
			SendMessage(_hwnd, WmSetIcon, new IntPtr(IconBig), _iconBig);
		}

		if (_iconSmall != IntPtr.Zero) {
			SendMessage(_hwnd, WmSetIcon, new IntPtr(IconSmall), _iconSmall);
		}

		MakeFonts();
		MakeInput();
		SetTimer(_hwnd, 1, 1000, IntPtr.Zero);

		// Left in mini mode last time: come back as it was left.
		if (Live.Global.MiniMode) {
			ApplyMini(true, remember: false);
		}

		if (_showOnCreate) {
			ShowWindow(_hwnd, SwShow);
			SetForegroundWindow(_hwnd);
			Visible = true;
		}

		Log.Debug("window open");
		Opened?.Invoke();

		while (_running && (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)) {
			TranslateMessage(ref msg);
			DispatchMessage(ref msg);
		}
	}

	private void MakeFonts() {
		_fontUi = CreateFont(15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
		_fontBold = CreateFont(15, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
		_fontSmall = CreateFont(13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
		_fontMono = CreateFont(13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Consolas");
		_fontIcon = CreateFont(14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
	}

	private void MakeInput() {
		// Deliberately the same height as one line of the log font and placed on the SAME top edge as the ">"
		// marker beside it. An EDIT draws its text at its own top-left, so any difference between the control's
		// top and the marker's top shows up directly as text sitting higher or lower than the prompt.
		_input = CreateWindowEx(0, "EDIT", "",
			unchecked((int) (WsChild | WsVisible)) | EsAutoHScroll,
			InputX, PromptY, _w - InputX - Pad, LineH,
			_hwnd, new IntPtr(IdInput), GetModuleHandle(null), IntPtr.Zero);

		SendMessage(_input, WmSetFont, _fontMono, new IntPtr(1));

		// Subclassed only to catch Enter - an EDIT control swallows it otherwise and the command never runs.
		_inputProc = InputProc;
		_originalInputProc = SetWindowLongPtr(_input, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_inputProc));
	}

	private IntPtr InputProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) {
		if ((msg == WmKeyDown) && (wParam.ToInt32() == VkReturn)) {
			RunTypedCommand();

			return IntPtr.Zero;
		}

		// The Enter (and Escape) that follow as characters would reach the text box too, which answers a key it has
		// no use for with the error beep - on every command typed.
		if ((msg == WmChar) && (wParam.ToInt32() is 13 or 27)) {
			return IntPtr.Zero;
		}

		return CallWindowProc(_originalInputProc, hwnd, msg, wParam, lParam);
	}

	private void RunTypedCommand() {
		// As long as what's there: a fixed 512 cut a pasted batch of keys for 'redeem' off in the middle of one.
		StringBuilder buffer = new(Math.Max(1, GetWindowTextLength(_input)) + 1);
		GetWindowText(_input, buffer, buffer.Capacity);
		string line = buffer.ToString().Trim();

		if (line.Length == 0) {
			return;
		}

		SetWindowText(_input, "");

		// A waiting question takes the line first, exactly as the console loop did. With the console detached
		// this is the ONLY place a Steam Guard code can be typed, so getting it wrong would make adding an
		// account impossible rather than merely awkward.
		if (Prompt.Pending != null) {
			Append(new Log.Entry(0, DateTime.Now, "INFO", "you", new Core.Said("> " + (Prompt.PendingSecret ? new string('*', line.Length) : line))));
			Prompt.Answer(line);
			Invalidate();

			return;
		}

		// help is a reference, not output.
		//
		// Sixty lines into the log pushes whatever you were reading off the top and cannot be scrolled on its
		// own, which is exactly what makes it useless at the moment you need it. Shown as a panel instead, the
		// same way the accounts list is. /help and /? both work because both are what people type.
		string asHelp = line.Trim().TrimStart('/').ToLowerInvariant();

		if (asHelp is "help" or "?" or "h") {
			SetHelpSheet(true);
			SetWindowText(_input, "");

			return;
		}

		// 'clear' empties this window only - the dashboard keeps its own log, and the file keeps everything.
		if (Commands.IsClear(line)) {
			ClearLog();
			SetWindowText(_input, "");

			return;
		}

		// Masked like Telegram and Steam chat: 'set myaccount SteamPassword ...' must not sit on screen in plain text.
		Append(new Log.Entry(0, DateTime.Now, "INFO", "you", new Core.Said("> " + Commands.ForLog(line))));

		_ = Task.Run(async () => {
			try {
				string output = await Commands.RunAsync(_mgr, line).ConfigureAwait(false);

				foreach (string outLine in output.Replace("\r\n", "\n").Split('\n')) {
					if (outLine.Length > 0) {
						Append(new Log.Entry(0, DateTime.Now, "INFO", "", new Core.Said("  " + outLine)));
					}
				}
			} catch (Exception e) {
				Append(new Log.Entry(0, DateTime.Now, "ERROR", "", new Core.Said("  " + e.Message)));
			}

			Invalidate();
		});
	}

	/// <summary>Ask for a repaint. Public so a language change can redraw rows that are already on screen.</summary>
	public void Invalidate() {
		if (_hwnd != IntPtr.Zero) {
			InvalidateRect(_hwnd, IntPtr.Zero, false);
		}
	}

	private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) {
		switch (msg) {
			case WmPaint:
				Paint(hwnd);

				return IntPtr.Zero;

			case WmCtlColorEdit: {
				// An EDIT paints itself, so the only way to make it match is to hand back the colours and a
				// background brush every time it asks. Without this it is a white box on a black window.
				SetTextColor(wParam, TextBright);
				SetBkColor(wParam, Rgb(24, 24, 24));
				SetBkMode(wParam, Opaque);

				_inputBrush = _inputBrush == IntPtr.Zero ? CreateSolidBrush(Rgb(24, 24, 24)) : _inputBrush;

				return _inputBrush;
			}

			case WmEraseBkgnd:
				return new IntPtr(1);   // everything is painted into a back buffer; erasing here only flickers

			case WmTimer:
				WatchPrompt();

				if (_mini) {
					FitMini();
				}

				Invalidate();

				return IntPtr.Zero;

			case WmQueryEndSession:
				return new IntPtr(1);   // fine by us - WM_ENDSESSION below does the tidying

			case WmEndSession:
				// Windows is shutting down or signing out. The process is ended soon after this returns, so what the
				// clean exit would save is saved here: where the window was, and the running totals.
				if (wParam != IntPtr.Zero) {
					SavePlace();
					BotManager.Flush();
					_exit();
				}

				return IntPtr.Zero;

			case WmSetMini:
				ApplyMini(wParam != IntPtr.Zero, remember: true);

				return IntPtr.Zero;

			case WmRefreshOnTop:
				ApplyOnTop();

				return IntPtr.Zero;

			case WmSize: {
				// Mini mode sizes itself to its rows, well under the full layout's floor.
				_w = _mini ? Math.Max(1, LoWord(lParam)) : Math.Max(MinW, LoWord(lParam));
				_h = _mini ? Math.Max(1, HiWord(lParam)) : Math.Max(MinH, HiWord(lParam));

				// The input is a real control, so it has to be moved by hand - everything else is repainted from
				// the new figures on the next frame.
				if (_input != IntPtr.Zero) {
					MoveWindow(_input, InputX, PromptY, Math.Max(40, _w - InputX - Pad), LineH, true);
				}

				Invalidate();

				return IntPtr.Zero;
			}

			case WmGetMinMaxInfo: {
				// MinTrackSize is the OUTER size, but MinW/MinH describe the CLIENT area, so the sizing border
				// has to be added. Measured rather than assumed - the border is a different width at a
				// different DPI, and hard-coding it made the real floor wrong on a scaled display.
				int frameW = 0, frameH = 0;

				if ((hwnd != IntPtr.Zero) && GetWindowRect(hwnd, out Rect outer) && GetClientRect(hwnd, out Rect client)) {
					frameW = Math.Max(0, (outer.Right - outer.Left) - client.Right);
					frameH = Math.Max(0, (outer.Bottom - outer.Top) - client.Bottom);
				}

				MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
				info.MinTrackSize = _mini ? new Point { X = 100 + frameW, Y = MiniTitleH + frameH } : new Point { X = MinW + frameW, Y = MinH + frameH };
				Marshal.StructureToPtr(info, lParam, false);

				return IntPtr.Zero;
			}

			case WmExitSizeMove:
				// Saved when the drag FINISHES, not on every WM_SIZE - resizing fires that continuously and
				// would rewrite the config file dozens of times a second.
				if (_mini) {
					RememberMiniSpot();
				} else {
					RememberSize();
				}

				return IntPtr.Zero;

			case WmLButtonDown: {
				int mx = LoWord(lParam);
				int my = HiWord(lParam);

				int hit = ButtonAt(mx, my);

				if (hit >= 0) {
					OnButton(_buttons[hit].Id);

					return IntPtr.Zero;
				}

				// LAST registered wins, not first. The row's own select area covers the whole row including where
				// its buttons sit, so taking the first match meant every click on cards/pause/stop was eaten by
				// the row underneath them and only ever toggled the selection.
				for (int i = _hits.Count - 1; i >= 0; i--) {
					(int hx, int hy, int hw, int hh, string bot, char what) = _hits[i];

					if ((mx < hx) || (mx >= hx + hw) || (my < hy) || (my >= hy + hh)) {
						continue;
					}

					OnAccountClick(bot, what);

					return IntPtr.Zero;
				}

				// Anywhere else on the title bar drags the window, since there is no system caption to grab.
				if (my < (_mini ? MiniTitleH : TitleH)) {
					ReleaseCapture();
					SendMessage(hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
				}

				return IntPtr.Zero;
			}

			case WmMouseWheel: {
				// Positive delta is a scroll UP, which means going BACK through the log. Added up rather than divided
				// on the spot: touchpads and smooth wheels send steps far smaller than one notch, which came out as 0.
				_wheel += (short) ((wParam.ToInt64() >> 16) & 0xFFFF);
				int notches = _wheel / 120;
				_wheel %= 120;

				// The sheet takes the wheel while it is open; otherwise it belongs to the log, which is now the
				// only scrollable thing on the main window.
				if (_helpSheet) {
					_helpScroll = Math.Max(0, _helpScroll - notches);
					Invalidate();

					return IntPtr.Zero;
				}

				if (_sheet) {
					int count = _mgr.All.Count;
					_accountScroll = Math.Clamp(_accountScroll - notches, 0, Math.Max(0, count - 1));
					Invalidate();

					return IntPtr.Zero;
				}

				int lines;

				lock (_logGate) {
					lines = _log.Count;
				}

				_logScroll = Math.Clamp(_logScroll + notches, 0, Math.Max(0, lines - Math.Max(1, LogRoom())));
				Invalidate();

				return IntPtr.Zero;
			}

			case WmMouseMove: {
				int hit = ButtonAt(LoWord(lParam), HiWord(lParam));
				int id = hit >= 0 ? _buttons[hit].Id : -1;

				if (id != _hoverId) {
					_hoverId = id;
					Invalidate();
				}

				// Ask to be told when the cursor leaves, or the last button stays lit after the mouse has gone.
				if (!_tracking) {
					_tracking = true;
					TrackMouseEventArgs track = new() { Size = Marshal.SizeOf<TrackMouseEventArgs>(), Flags = TmeLeave, Window = hwnd, HoverTime = 0 };
					TrackMouseEvent(ref track);
				}

				return IntPtr.Zero;
			}

			case WmMouseLeave:
				_tracking = false;
				_hoverId = -1;
				Invalidate();

				return IntPtr.Zero;

			case WmClose:
				// Hiding is only safe when the tray icon can bring it back. With no tray there is nothing left to
				// click, and the process would keep running with no reachable UI at all - so close means quit.
				if (Commands.TrayPresent) {
					Hide();
				} else {
					_exit();
				}

				return IntPtr.Zero;

			case WmDestroy:
				if (!_mini) {
					RememberSize();
				}

				_running = false;
				PostQuitMessage(0);

				return IntPtr.Zero;
		}

		return DefWindowProc(hwnd, msg, wParam, lParam);
	}

	/// <summary>
	/// Remember where the window was left and how big, so it opens there next time.
	///
	/// Written to config/nocatFarm.json beside the exe - the same file as every other setting, and nothing is
	/// kept in AppData. Failing to save a window size must never take the program down with it, hence the catch.
	/// </summary>
	private void RememberSize() {
		try {
			// The OUTER size, not _w/_h.
			//
			// _w and _h come from WM_SIZE, which reports the CLIENT area, but CreateWindowEx is given the outer
			// size when the window is opened again. Saving one and restoring it as the other made the window
			// lose the width of its own border on every launch - 577 saved, 563 reopened, then 549, forever.
			if ((_hwnd == IntPtr.Zero) || !GetWindowRect(_hwnd, out Rect outer)) {
				return;
			}

			int w = outer.Right - outer.Left;
			int h = outer.Bottom - outer.Top;

			// Where it is as well as how big. Only the size was kept, so every launch put the window back in the
			// middle of the screen however carefully it had been parked.
			if ((w < MinW) || (h < MinH)
				|| ((Live.Global.WindowWidth == w) && (Live.Global.WindowHeight == h) && (Live.Global.WindowX == outer.Left) && (Live.Global.WindowY == outer.Top))) {
				return;   // unchanged, or nonsense - do not churn the file
			}

			Live.Global.WindowWidth = w;
			Live.Global.WindowHeight = h;
			Live.Global.WindowX = outer.Left;
			Live.Global.WindowY = outer.Top;
			ConfigStore.SaveGlobal(Live.Global);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the window size: {0}: {1}", e.GetType().Name, e.Message));
		}
	}

	/// <summary>
	/// What colour to paint an account's name in the log.
	///
	/// Anything that is not an account keeps the neutral grey - the program's own lines say "nocatFarm" in the
	/// same column and must not pick up somebody else's colour.
	/// </summary>
	private int SourceColour(string source) {
		if (source.Length == 0) {
			return TextDim;
		}

		return NameColour.Of(NameColour.ChoiceFor(source, _mgr))?.Win32 ?? TextMid;
	}

	private int ButtonAt(int x, int y) {
		for (int i = 0; i < _buttons.Count; i++) {
			Button b = _buttons[i];

			if ((x >= b.X) && (x < b.X + b.W) && (y >= b.Y) && (y < b.Y + b.H)) {
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// A click on an account row.
	///
	/// 'n' is the name - that opens the Steam profile, because the thing you most often want when looking at a
	/// row is to go and LOOK at the account. Everything else acts on it directly, which is the point of having
	/// the accounts here at all rather than sending you to the dashboard for every small thing.
	/// </summary>
	private void OnAccountClick(string name, char what) {
		Bot? bot = _mgr.Get(name);

		if (bot == null) {
			return;
		}

		switch (what) {
			case 'n':
				if (bot.SteamId != 0) {
					OpenUrl($"https://steamcommunity.com/profiles/{bot.SteamId}");
				}

				break;

			case 's':
				_selected = _selected == name ? null : name;   // clicking the row again puts the buttons away

				break;

			case 'p':
				if (bot.Paused) {
					bot.Resume();
				} else {
					bot.Pause();
				}

				break;

			case 'x':
				// Running, not online: a reconnecting account, or one sitting out a cooldown, showed "start" and
				// couldn't be stopped from here at all.
				if (bot.Running) {
					_ = bot.StopAsync(graceful: true);
				} else {
					_ = bot.StartAsync();
				}

				break;

			case 'c':
				Append(new Log.Entry(0, DateTime.Now, "INFO", "you", new Core.Said("> cards " + name)));
				_ = Task.Run(async () => {
					string output = await Commands.RunAsync(_mgr, "cards " + name).ConfigureAwait(false);

					foreach (string line in output.Replace("\r\n", "\n").Split('\n')) {
						if (line.Length > 0) {
							Append(new Log.Entry(0, DateTime.Now, "INFO", "", new Core.Said("  " + line)));
						}
					}

					Invalidate();
				});

				break;
		}

		Invalidate();
	}

	/// <summary>
	/// Start adding an account.
	///
	/// No new dialog: adding an account is already a command, and the password it asks for already routes to
	/// this window's input line. So this just closes the sheet, says what to type, and puts the cursor in the
	/// right place with the verb already there - which is a smaller thing to get wrong than a second window
	/// with its own two text boxes and its own way of going wrong.
	/// </summary>
	private void StartAddAccount() {
		SetSheet(false);

		Append(new Log.Entry(0, DateTime.Now, "GOOD", "", new Core.Said("Adding an account - type the name you want to call it, then its Steam login:")));
		Append(new Log.Entry(0, DateTime.Now, "INFO", "", new Core.Said("    add mybot mysteamlogin")));
		Append(new Log.Entry(0, DateTime.Now, "INFO", "", new Core.Said("It asks for the password once, then remembers a login token - you never store a password.")));
		Append(new Log.Entry(0, DateTime.Now, "INFO", "", new Core.Said("Already use ArchiSteamFarm?  import asf  brings every account across with its login token.")));

		SetWindowText(_input, "add ");
		SendMessage(_input, EmSetSel, new IntPtr(4), new IntPtr(4));
		SetFocus(_input);
		Invalidate();
	}

	/// <summary>
	/// Open or close the accounts sheet.
	///
	/// The command input is a real child window, not something painted here - so it sits ON TOP of whatever
	/// this draws, and showed straight through the sheet. Everything else on screen is in the back buffer and
	/// obeys the paint order; this one control has to be hidden by hand.
	/// </summary>
	private void SetHelpSheet(bool open) {
		_helpSheet = open;
		_helpScroll = 0;

		if (open) {
			_sheet = false;   // one panel at a time
		}

		// Same reason the accounts sheet hides it: the input is a real child window and paints over anything
		// drawn into the back buffer, so it shows straight through a panel unless hidden by hand.
		ShowWindow(_input, open ? SwHide : SwShow);
		Invalidate();
	}

	private void SetSheet(bool open) {
		_sheet = open;
		_accountScroll = 0;

		if (_input != IntPtr.Zero) {
			ShowWindow(_input, open ? SwHide : SwShow);
		}

		Invalidate();
	}

	private static void OpenUrl(string url) {
		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
		} catch (Exception e) {
			Log.Warn(new Said("couldn't open {0}: {1}", url, e.Message));
		}
	}

	private void OnButton(int id) {
		switch (id) {
			case IdStartAll:
				_ = _mgr.StartAllAsync();

				break;

			case IdStopAll:
				_ = _mgr.StopAllAsync(graceful: true);

				break;

			case IdDashboard: {
				string url = _url();

				if (!string.IsNullOrEmpty(url)) {
					try {
						System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
					} catch (Exception e) {
						Log.Warn(new Said("couldn't open the browser: {0}", e.Message));
					}
				} else {
					Append(new Log.Entry(0, DateTime.Now, "WARN", "", new Core.Said("  the dashboard is switched off - 'set WebEnabled true' then restart")));
				}

				break;
			}

			case IdHide:
				if (Commands.TrayPresent) {
					Hide();
				} else {
					Append(new Log.Entry(0, DateTime.Now, "WARN", "", new Core.Said("there is no tray icon to bring this back - use quit, or start without --no-tray")));
				}

				break;

			case IdQuit:
				_exit();

				break;

			case IdOpenHelp:
				SetHelpSheet(true);

				break;

			case IdOpenSheet:
				SetSheet(true);

				break;

			case IdCloseHelp:
				SetHelpSheet(false);

				break;

			case IdCloseSheet:
				SetSheet(false);

				break;

			case IdAddAccount:
				StartAddAccount();

				break;

			case IdMini:
				ApplyMini(true, remember: true);

				break;

			case IdFullWindow:
				ApplyMini(false, remember: true);

				break;

			case IdPin:
				ToggleOnTop();

				break;
		}

		Invalidate();
	}

	// ── painting ────────────────────────────────────────────────────────────
	private void Paint(IntPtr hwnd) {
		IntPtr dc = BeginPaint(hwnd, out PaintStruct ps);

		// Draw the whole frame into a bitmap and blit it once. Painting straight to the screen at one frame a
		// second makes every text run visibly flash.
		IntPtr mem = CreateCompatibleDC(dc);
		IntPtr bmp = CreateCompatibleBitmap(dc, Math.Max(1, _w), Math.Max(1, _h));
		IntPtr old = SelectObject(mem, bmp);

		// try/finally around the whole thing: a throw from anything that reads live bot state would otherwise
		// skip EndPaint and leak the DC and bitmap. Windows then never sends WM_PAINT again and the window is a
		// dead grey rectangle that still has to be killed from the tray.
		try {

		SetBkMode(mem, Transparent);
		Fill(mem, 0, 0, _w, _h, Bg);

		_buttons.Clear();
		_hits.Clear();

		if (_mini) {
			PaintMini(mem);
		} else {
			PaintTitle(mem);

			if (_helpSheet) {
				PaintHelp(mem);
			} else if (_sheet) {
				// The sheet owns the window while it is open, so nothing underneath registers a click.
				PaintSheet(mem);
			} else {
				PaintToolbar(mem);

				// No account list here any more. It lived in two places, which meant two layouts to keep right and
				// a main window that grew with the number of accounts; the sheet owns them now and the log gets
				// every pixel that frees up.
				PaintLog(mem, TitleH + BarH);
				PaintStatus(mem);
			}
		}

			BitBlt(dc, 0, 0, _w, _h, mem, 0, 0, SrcCopy);
			_paintFailures = 0;
		} catch (Exception e) {
			// A paint that fails ONCE is a resize landing mid-draw and the next tick fixes it. A paint that
			// keeps failing is a window drawing nothing, and at Debug that is invisible: the log said nothing
			// at all unless you had debug detail switched on, so the window simply looked dead. Say it out
			// loud once the failures stop being a blip, and say it only once so the log is not a wall.
			if (++_paintFailures == 5) {
				Log.Error(new Said("window can't draw ({0}: {1}) - dashboard still works", e.GetType().Name, e.Message));
			} else if (_paintFailures < 5) {
				Log.Debug(new Said("paint failed: {0}: {1}", e.GetType().Name, e.Message));
			}
		} finally {
			SelectObject(mem, old);
			DeleteObject(bmp);
			DeleteDC(mem);
			EndPaint(hwnd, ref ps);
		}
	}

	private void PaintTitle(IntPtr dc) {
		Fill(dc, 0, 0, _w, TitleH, Panel);
		Fill(dc, 0, TitleH - 1, _w, 1, Border);

		Text(dc, "nocat.", Pad, 9, _fontBold, TextBright);
		int w = TextWidth(dc, "nocat.", _fontBold);
		Text(dc, "farm", Pad + w, 9, _fontBold, Wordmark);

		Text(dc, DateTime.Now.ToString("HH:mm:ss"), _w - 190, 10, _fontSmall, TextDim);

		AddButton(dc, "mini", IdMini, _w - 128, 7, 34, 20, TextMid);
		AddButton(dc, "hide", IdHide, _w - 88, 7, 34, 20, TextMid);
		AddButton(dc, "quit", IdQuit, _w - 48, 7, 34, 20, Red);
	}

	private void PaintToolbar(IntPtr dc) {
		Fill(dc, 0, TitleH, _w, BarH, Surface);
		Fill(dc, 0, TitleH + BarH - 1, _w, 1, Border);

		int y = TitleH + 7;
		AddButton(dc, "start all", IdStartAll, Pad, y, 74, 24, Green);
		AddButton(dc, "stop all", IdStopAll, Pad + 82, y, 70, 24, TextMid);
		AddButton(dc, "dashboard", IdDashboard, Pad + 160, y, 84, 24, Accent);
		AddButton(dc, "accounts", IdOpenSheet, Pad + 252, y, 76, 24, TextBright);
		AddButton(dc, "commands", IdOpenHelp, Pad + 336, y, 80, 24, TextMid);

		(int online, int total) = Counts();
		string summary = $"{online} of {total} signed in";
		Text(dc, summary, _w - Pad - TextWidth(dc, summary, _fontSmall), TitleH + 12, _fontSmall, TextMid);
	}

	/// <summary>
	/// One block per account: who, what, and two progress bars. Returns where the log should start.
	///
	/// This is the part a console could never do well. Three accounts at a glance, each with its own progress,
	/// in fixed positions that don't move - so you learn where to look once instead of re-reading every time.
	/// </summary>
	/// <summary>
	/// How many account rows are shown at once.
	///
	/// Fixed, deliberately. Drawing one row per account is fine for three and catastrophic for eighty - the
	/// window has a fixed height, so the log and the command line would simply be pushed off the bottom and the
	/// app would become unusable at exactly the point somebody is getting serious about it.
	/// </summary>
	// ── the accounts sheet ──────────────────────────────────────────────────
	/// <summary>
	/// Every account, with every action, over the top of the window.
	///
	/// One place that answers "what have I got and what can I do to it", instead of four buttons squeezed into a
	/// row that then has to grow to fit them. It scrolls, so the number of accounts stops being a layout problem.
	/// </summary>
	/// <summary>What the two button columns plus the right margin occupy, so the text lays out around them.</summary>
	private const int ButtonBlock = 14 + (58 * 2) + 5;

	/// <summary>
	/// Every command, in a panel that scrolls by itself.
	///
	/// Deliberately the same shape as the accounts sheet - a titled panel inset from the window, a close
	/// button top right, wheel to scroll - so there is one idea to learn rather than two.
	/// </summary>
	private void PaintHelp(IntPtr dc) {
		Fill(dc, 0, TitleH, _w, _h - TitleH, Rgb(6, 6, 6));

		int w = _w - 60;
		int x = 30;
		int top = TitleH + 12;
		int h = _h - top - 22;

		Fill(dc, x, top, w, h, Surface);
		Outline(dc, x, top, w, h, Border);

		Text(dc, "COMMANDS", x + 14, top + 10, _fontBold, TextBright);
		AddButton(dc, "close", IdCloseHelp, x + w - 70, top + 8, 56, 20, TextMid);

		// One flat list of rows - a group header, then its commands - so scrolling is a single index rather
		// than a per-group calculation that has to agree with the paint.
		List<(string Text, bool Header)> rows = [];

		foreach (string group in Commands.All.Select(static c => c.Group).Distinct()) {
			rows.Add((group.ToUpperInvariant(), true));

			foreach (CommandDef c in Commands.All.Where(c => c.Group == group)) {
				rows.Add(((c.Display + " " + c.Args).TrimEnd() + "  —  " + c.Help, false));
			}
		}

		int room = Math.Max(1, (h - 52) / LineH);
		_helpScroll = Math.Clamp(_helpScroll, 0, Math.Max(0, rows.Count - room));

		int y = top + 38;

		foreach ((string text, bool header) in rows.Skip(_helpScroll).Take(room)) {
			if (header) {
				Clipped(dc, text, x + 14, y, w - 28, _fontSmall, TextDim);
			} else {
				int split = text.IndexOf("  —  ", StringComparison.Ordinal);
				string left = split > 0 ? text[..split] : text;
				string right = split > 0 ? text[(split + 5)..] : "";

				Clipped(dc, left, x + 22, y, 250, _fontMono, Accent);
				Clipped(dc, right, x + 280, y, w - 300, _fontSmall, TextMid);
			}

			y += LineH;
		}

		if (rows.Count > room) {
			Text(dc, $"showing {_helpScroll + 1}-{Math.Min(rows.Count, _helpScroll + room)} of {rows.Count}  ·  scroll for the rest",
				x + 14, top + h - 18, _fontSmall, TextDim);
		}
	}

	private void PaintSheet(IntPtr dc) {
		// Dim what is behind it, so it reads as being on top rather than as the window having changed.
		Fill(dc, 0, TitleH, _w, _h - TitleH, Rgb(6, 6, 6));

		List<Bot> all = _mgr.All.ToList();
		const int SheetRow = 54;
		const int HeaderH = 38;
		const int FooterH = 14;
		const int MaxVisible = 6;

		int visible = Math.Clamp(all.Count, 1, MaxVisible);
		bool scrolls = all.Count > visible;

		// Sized to what it holds. Three accounts do not need a panel the height of the window - the empty half
		// just reads as something failing to load.
		int w = _w - 80;
		int h = HeaderH + (visible * SheetRow) + FooterH + (scrolls ? 18 : 0);
		int x = (_w - w) / 2;
		int top = TitleH + Math.Max(12, ((_h - TitleH - h) / 2) - 30);

		Fill(dc, x, top, w, h, Surface);
		Outline(dc, x, top, w, h, Border);

		Text(dc, "ACCOUNTS", x + 14, top + 10, _fontBold, TextBright);
		AddButton(dc, "close", IdCloseSheet, x + w - 70, top + 8, 56, 20, TextMid);
		AddButton(dc, "+ add account", IdAddAccount, x + w - 70 - 116, top + 8, 110, 20, Green);

		int room = visible;
		_accountScroll = Math.Clamp(_accountScroll, 0, Math.Max(0, all.Count - room));

		int y = top + HeaderH;

		foreach (Bot bot in all.Skip(_accountScroll).Take(room)) {
			(string doing, string sitting, int done, int total, string today, _, int dayTotal, string said) = Describe(bot);

			int dot = bot.IsOnline ? bot.Paused || bot.PlayingBlocked ? Amber : Green : TextDim;
			Fill(dc, x + 14, y + 10, 6, 6, dot);

			int nameColour = bot.IsOnline ? NameColour.Of(bot.Cfg.LogColour)?.Win32 ?? Accent : TextDim;
			Text(dc, bot.Name, x + 30, y + 4, _fontBold, nameColour);
			_hits.Add((x + 30, y, TextWidth(dc, bot.Name, _fontBold) + 8, 18, bot.Name, 'n'));

			// Columns measured from the panel, not nailed to absolute pixels.
			//
			// The detail column used to start at a hard-coded x + 300 while the buttons were pinned to the right
			// edge, so in a small window the two closed to about 60px apart and everything between them collapsed
			// to "rep4rep: ..." and "42m of 4h..." - an ellipsis where the number should be, which is worse than
			// showing nothing at all. It now takes a share of whatever room exists, and if that share is too
			// small to finish a sentence the column is dropped and the space goes to what the account is doing.
			const int MinDetail = 110;
			int textLeft = x + 30;
			int textRoom = (x + w - ButtonBlock) - textLeft;
			int detailW = (textRoom * 40) / 100;

			if (detailW < MinDetail) {
				detailW = 0;
			}

			int doingW = Math.Max(40, textRoom - detailW - (detailW > 0 ? 12 : 0));
			int detailX = (x + w - ButtonBlock) - detailW;

			Clipped(dc, doing, textLeft, y + 22, doingW, _fontSmall, TextMid);

			string detail = total > 0 ? $"{Fmt.Hm(done)} of {Fmt.Hm(total)}" : sitting;

			if ((detail.Length > 0) && (detailW > 0)) {
				Clipped(dc, detail, detailX, y + 4, detailW, _fontSmall, TextNormal);
			}

			// Both, when there is something to say for each. The old line picked one and silently dropped the
			// other, so switching rep4rep on made the hours-played figure disappear with no explanation.
			//
			// Measured rather than joined and hoped for. Joining unconditionally just moved the problem: an
			// account with a day figure AND a comment count overflowed the column and the row read
			// "12m of 6h40m today   rep4rep:..." - the ellipsis this column was reworked to get rid of, back
			// again. So try the pair, then the pair without the second "today" (the first one already said it),
			// then whichever single fact still fits. Something true and whole beats two things cut in half.
			if (detailW > 0) {
				string day = dayTotal > 0 ? today : "";
				string trimmed = said.EndsWith(" today", StringComparison.Ordinal) && (day.Length > 0)
					? said[..^6]
					: said;

				foreach (string candidate in new[] {
					string.Join("   ", new[] { day, said }.Where(static t => t.Length > 0)),
					string.Join("   ", new[] { day, trimmed }.Where(static t => t.Length > 0)),
					said,
					day
				}) {
					if ((candidate.Length == 0) || (TextWidth(dc, candidate, _fontSmall) > detailW)) {
						continue;
					}

					Clipped(dc, candidate, detailX, y + 22, detailW, _fontSmall, TextDim);

					break;
				}
			}

			// Every action for this account, in fixed positions so they are in the same place on every row.
			const int BtnW = 58;
			const int BtnH = 20;
			const int Gap = 5;
			int bx = (x + w) - ButtonBlock;

			RowButton(dc, bot.Running ? "stop" : "start", bx, y + 4, BtnW, BtnH, bot.Name, 'x', bot.Running ? Red : Green);
			RowButton(dc, bot.Paused ? "resume" : "pause", bx + BtnW + Gap, y + 4, BtnW, BtnH, bot.Name, 'p', Amber);
			RowButton(dc, "cards", bx, y + 4 + BtnH + Gap, BtnW, BtnH, bot.Name, 'c', TextMid);
			RowButton(dc, "profile", bx + BtnW + Gap, y + 4 + BtnH + Gap, BtnW, BtnH, bot.Name, 'n', TextMid);

			y += SheetRow;
			Fill(dc, x + 8, y - 2, w - 16, 1, Rgb(28, 28, 28));
		}

		if (scrolls) {
			Text(dc, $"showing {_accountScroll + 1}-{Math.Min(all.Count, _accountScroll + room)} of {all.Count}  ·  scroll for the rest",
				x + 14, top + h - 16, _fontSmall, TextDim);
		}
	}

	/// <summary>
	/// The shared answer, shaped for this window.
	///
	/// This used to work the whole thing out again by hand, which is how it ended up quietly missing the
	/// "Steam is showing a different game" warning that the console board had.
	/// </summary>
	private static (string Doing, string Sitting, int Done, int Total, string Today, int DayDone, int DayTotal, string Said) Describe(Bot bot) {
		BotStatus s = BotStatus.Of(bot);

		string doing = s.Doing;

		if (!s.Persona.IsEmpty) {
			doing += "   (" + s.Persona + ")";
		}

		if (!s.Warning.IsEmpty) {
			doing += "   " + s.Warning;
		}

		string said = s.Comments.Length > 0 ? $"rep4rep: {s.Comments} today" : "";

		return (doing, s.Sitting, s.SessionDone, s.SessionTotal, s.Today, s.DayDone, s.DayTotal, said);
	}

	/// <summary>A small button that belongs to one account rather than to the app.</summary>
	private void RowButton(IntPtr dc, string text, int x, int y, int w, int h, string bot, char what, int colour) {
		Fill(dc, x, y, w, h, Panel);
		Outline(dc, x, y, w, h, Border);
		Text(dc, text, x + ((w - TextWidth(dc, text, _fontSmall)) / 2), y + 1, _fontSmall, colour);
		_hits.Add((x, y, w, h, bot, what));
	}

	/// <summary>How many log lines fit. Used by both the paint and the wheel handler, so they cannot disagree.</summary>
	private int LogRoom() {
		int height = _h - StatusH - InputH - TitleH - BarH;

		return Math.Max(1, (height - 10) / LineH);
	}

	private void PaintLog(IntPtr dc, int top) {
		int bottom = _h - StatusH - InputH;
		int height = bottom - top;

		if (height < 20) {
			return;
		}

		Fill(dc, 0, top, _w, height, Panel);
		Fill(dc, 0, top, _w, 1, Border);

		List<LogLine> lines;
		int room = Math.Max(1, (height - 10) / LineH);
		int total;

		lock (_logGate) {
			total = _log.Count;
			_logScroll = Math.Clamp(_logScroll, 0, Math.Max(0, total - room));

			// Scrolled back by N lines means dropping the last N and taking a window ending there.
			int end = Math.Max(0, total - _logScroll);
			int start = Math.Max(0, end - room);
			lines = _log.GetRange(start, end - start);
		}

		// Fixed columns, and every line is clipped to the panel with an ellipsis rather than allowed to wrap or
		// run off the edge. A log line that folds onto a second row destroys the alignment of everything under
		// it, which is the single ugliest thing a panel like this can do.
		const int TimeX = Pad;
		const int SourceX = TimeX + 62;
		const int TextX = SourceX + 78;
		int textRoom = _w - TextX - Pad;

		int y = top + 6;

		foreach (LogLine line in lines) {
			Text(dc, line.Time, TimeX, y, _fontMono, TextDim);
			Clipped(dc, line.Source, SourceX, y, 74, _fontMono, SourceColour(line.Source));
			Clipped(dc, line.Text, TextX, y, textRoom, _fontMono, line.Colour);
			y += LineH;
		}

		// Say when the view is held back, so an old line at the bottom is never mistaken for the newest one.
		//
		// Drawn along the BOTTOM on its own filled strip. It used to sit at top + 4 while the first log line
		// started at top + 6 - the two were painted straight over each other, which is unreadable and looks
		// like the window is corrupt. A strip also means it can never collide with a line whatever the size.
		if (_logScroll > 0) {
			string note = $"scrolled back {_logScroll} line(s) - scroll down to catch up";
			int noteH = LineH + 2;
			int noteY = (top + height) - noteH;

			Fill(dc, 0, noteY, _w, noteH, Rgb(38, 30, 12));
			Fill(dc, 0, noteY, _w, 1, Amber);
			Clipped(dc, note, Pad, noteY + 2, _w - (Pad * 2), _fontSmall, Amber);
		}

		// The prompt marker, so the input below reads as a command line rather than a stray text box. When
		// something is actually waiting on an answer, the marker becomes the question.
		int inputTop = _h - StatusH - InputH;

		// The strip, and then a framed well for the input itself.
		//
		// Making it match the window removed the white box but went too far the other way - it stopped reading
		// as somewhere you can type at all. A visible well with a lit left edge says "command line" without
		// going back to a white rectangle.
		Fill(dc, 0, inputTop, _w, InputH, Rgb(16, 16, 16));
		Fill(dc, 0, inputTop, _w, 1, Border);

		int wellX = InputX - 6;
		int wellY = inputTop + 3;
		int wellH = InputH - 6;

		Fill(dc, wellX, wellY, _w - wellX - Pad, wellH, Rgb(24, 24, 24));
		Outline(dc, wellX, wellY, _w - wellX - Pad, wellH, Rgb(48, 48, 48));
		Fill(dc, wellX, wellY, 2, wellH, Accent);

		if (Prompt.Pending is { Length: > 0 } question) {
			Fill(dc, 0, inputTop - 20, _w, 20, Rgb(38, 30, 12));
			Clipped(dc, question, Pad, inputTop - 19, _w - (Pad * 2), _fontSmall, Amber);
			Text(dc, "?", Pad, PromptY, _fontMono, Amber);
		} else {
			Text(dc, ">", Pad, PromptY, _fontMono, Accent);
		}
	}

	private void PaintStatus(IntPtr dc) {
		int y = _h - StatusH;
		Fill(dc, 0, y, _w, StatusH, Surface);
		Fill(dc, 0, y, _w, 1, Border);

		(int cards, int comments) = Stats.Totals(24);
		string left = $"today:  {cards} cards  ·  {comments} comments";
		Text(dc, left, Pad, y + 5, _fontSmall, TextMid);

		string right = _url() is { Length: > 0 } url ? url : "dashboard off";
		Text(dc, right, _w - Pad - TextWidth(dc, right, _fontSmall), y + 5, _fontSmall, TextDim);
	}

	private (int Online, int Total) Counts() {
		IReadOnlyCollection<Bot> bots = _mgr.All;

		return (bots.Count(static b => b.IsOnline), bots.Count);
	}

	// ── mini mode ───────────────────────────────────────────────────────────
	/// <summary>Switch mini mode on or off from any thread - the tray, a typed command. Shows the window too.</summary>
	public void SetMiniMode(bool on) {
		if (_hwnd != IntPtr.Zero) {
			PostMessage(_hwnd, WmSetMini, new IntPtr(on ? 1 : 0), IntPtr.Zero);
		}
	}

	/// <summary>Put the "keep on top" setting into effect, from any thread - after it's changed on the dashboard.</summary>
	public void RefreshOnTop() {
		if (_hwnd != IntPtr.Zero) {
			PostMessage(_hwnd, WmRefreshOnTop, IntPtr.Zero, IntPtr.Zero);
		}
	}

	/// <summary>
	/// Change layout. Only ever runs on the window's own thread: the style, the size and the child input all
	/// belong to it.
	/// </summary>
	private void ApplyMini(bool on, bool remember) {
		if ((_hwnd == IntPtr.Zero) || (on == _mini)) {
			if (remember) {   // asked for - so shown, whichever layout it already was in
				ShowWindow(_hwnd, SwShow);
				Visible = true;
			}

			return;
		}

		if (on && !_mini) {
			RememberSize();   // where the full window was, so coming back puts it there
		} else if (!on && _mini) {
			RememberMiniSpot();
		}

		_mini = on;
		_sheet = false;
		_helpSheet = false;
		_hoverId = -1;

		// The command line has no place in a panel this size, and it's a real child window, so it has to go by hand.
		ShowWindow(_input, on ? SwHide : SwShow);

		// No sizing border in mini: it sizes itself to its rows.
		long style = GetWindowLongPtr(_hwnd, GwlStyle).ToInt64();
		style = on ? style & ~(long) WsThickFrame : style | WsThickFrame;
		SetWindowLongPtr(_hwnd, GwlStyle, new IntPtr(style));

		if (on) {
			int h = MiniHeight();
			(int x, int y) = MiniSpot(h);
			SetWindowPos(_hwnd, Live.Global.MiniOnTop ? HwndTopmost : HwndNoTopmost, x, y, MiniW, h, SwpFrameChanged);
		} else {
			int w = Live.Global.WindowWidth > 0 ? Math.Clamp(Live.Global.WindowWidth, MinW, GetSystemMetrics(SmCxScreen)) : StartW;
			int h = Live.Global.WindowHeight > 0 ? Math.Clamp(Live.Global.WindowHeight, MinH, GetSystemMetrics(SmCyScreen)) : StartH;
			(int x, int y) = FullSpot(w, h);
			SetWindowPos(_hwnd, HwndNoTopmost, x, y, w, h, SwpFrameChanged);
		}

		// Asked for now (a click, the tray, a command): show it. Opening in mini at startup leaves showing to the
		// usual startup path, so "Start hidden" still starts hidden.
		if (remember) {
			ShowWindow(_hwnd, SwShow);
			Visible = true;
		}

		if (remember && (Live.Global.MiniMode != on)) {
			try {
				Live.Global.MiniMode = on;
				ConfigStore.SaveGlobal(Live.Global);
			} catch (Exception e) {
				Log.Debug(new Said("couldn't save mini mode: {0}", e.Message));
			}
		}

		Invalidate();
	}

	/// <summary>
	/// Where the full window goes: where it was left, or the middle of the main screen the first time. Clamped to the
	/// desktop as it is now, so a monitor that has since been unplugged can't leave it somewhere nobody can see.
	/// </summary>
	private static (int X, int Y) FullSpot(int width, int height) {
		if ((Live.Global.WindowX == int.MinValue) || (Live.Global.WindowY == int.MinValue)) {
			return (Math.Max(0, (GetSystemMetrics(SmCxScreen) - width) / 2), Math.Max(0, (GetSystemMetrics(SmCyScreen) - height) / 2));
		}

		return OnScreen(Live.Global.WindowX, Live.Global.WindowY, width, height);
	}

	/// <summary>
	/// Keep a window of this size inside the usable part (taskbar excluded) of the monitor nearest to where it was
	/// left. Clamping to the rectangle around every monitor put it in the gaps no screen covers when the monitors
	/// weren't lined up, or behind a taskbar.
	/// </summary>
	private static (int X, int Y) OnScreen(int x, int y, int width, int height) {
		Rect work = WorkAreaAt(x + (width / 2), y + (height / 2));

		return (Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width)), Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height)));
	}

	private static Rect WorkAreaAt(int x, int y) {
		IntPtr monitor = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNearest);
		MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };

		if ((monitor != IntPtr.Zero) && GetMonitorInfo(monitor, ref info)) {
			return info.Work;
		}

		return new Rect { Left = 0, Top = 0, Right = GetSystemMetrics(SmCxScreen), Bottom = GetSystemMetrics(SmCyScreen) };
	}

	/// <summary>Where mini mode goes: where it was left, or the bottom-right corner above the taskbar the first time.</summary>
	private static (int X, int Y) MiniSpot(int height) {
		if ((Live.Global.MiniX != int.MinValue) && (Live.Global.MiniY != int.MinValue)) {
			// On a screen as the screens are now, so a monitor that has since been unplugged can't strand it.
			return OnScreen(Live.Global.MiniX, Live.Global.MiniY, MiniW, height);
		}

		if (!SystemParametersInfo(SpiGetWorkArea, 0, out Rect work, 0)) {
			work = new Rect { Left = 0, Top = 0, Right = GetSystemMetrics(SmCxScreen), Bottom = GetSystemMetrics(SmCyScreen) };
		}

		return (work.Right - MiniW - 16, work.Bottom - height - 16);
	}

	private void RememberMiniSpot() {
		try {
			if ((_hwnd == IntPtr.Zero) || !GetWindowRect(_hwnd, out Rect r) || ((Live.Global.MiniX == r.Left) && (Live.Global.MiniY == r.Top))) {
				return;
			}

			Live.Global.MiniX = r.Left;
			Live.Global.MiniY = r.Top;
			ConfigStore.SaveGlobal(Live.Global);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save where mini mode was: {0}", e.Message));
		}
	}

	/// <summary>
	/// Grow or shrink to the rows as they are now - a row opens up while its account farms. A panel in the lower
	/// half of the screen grows upwards, so one parked above the taskbar never slides underneath it.
	/// </summary>
	private void FitMini() {
		if (!GetWindowRect(_hwnd, out Rect r)) {
			return;
		}

		int want = MiniHeight();
		int now = r.Bottom - r.Top;

		if (want == now) {
			return;
		}

		Rect work = WorkAreaAt(r.Left + (MiniW / 2), r.Top + (now / 2));
		bool lowerHalf = (r.Top + (now / 2)) > (work.Top + ((work.Bottom - work.Top) / 2));
		int top = lowerHalf ? r.Bottom - want : r.Top;
		SetWindowPos(_hwnd, IntPtr.Zero, r.Left, top, MiniW, want, SwpNoZOrder | SwpNoActivate);
	}

	/// <summary>Accounts in mini mode, and whether each gets the open, farming layout.</summary>
	private List<(Bot Bot, bool Farming)> MiniRows() =>
		[.. _mgr.All.Take(MiniMaxRows).Select(static b => (b, b.IsOnline && b.IsFarming && (b.CardsRemaining > 0)))];

	private int MiniHeight() {
		List<(Bot Bot, bool Farming)> rows = MiniRows();
		int more = _mgr.All.Count > MiniMaxRows ? MiniRowH : 0;

		return MiniTitleH + Math.Max(MiniRowH, rows.Sum(static r => r.Farming ? MiniFocusH : MiniRowH)) + more;
	}

	/// <summary>
	/// One line per account, and a farming account opened up with its cards left, time left and a progress bar -
	/// the question you'd otherwise open the full window to answer.
	/// </summary>
	private void PaintMini(IntPtr dc) {
		Fill(dc, 0, 0, _w, MiniTitleH, Panel);
		Fill(dc, 0, MiniTitleH - 1, _w, 1, Border);

		Text(dc, "nocat.", 10, 7, _fontBold, TextBright);
		Text(dc, "farm", 10 + TextWidth(dc, "nocat.", _fontBold), 7, _fontBold, Wordmark);

		int bx = _w - 6 - 24;
		AddIconButton(dc, GlyphFullWindow, IdFullWindow, bx, 4, 24, 22, TextMid);
		bx -= 26;
		AddIconButton(dc, GlyphDashboard, IdDashboard, bx, 4, 24, 22, Accent);
		bx -= 26;
		AddIconButton(dc, GlyphPin, IdPin, bx, 4, 24, 22, Live.Global.MiniOnTop ? Accent : TextDim);

		(int cards, _) = Stats.Totals(24);
		string today = cards == 1 ? "1 card today" : $"{cards} cards today";
		Text(dc, today, bx - 8 - TextWidth(dc, today, _fontSmall), 8, _fontSmall, TextDim);

		int y = MiniTitleH;
		List<(Bot Bot, bool Farming)> rows = MiniRows();

		if (rows.Count == 0) {
			Text(dc, "no accounts yet - 'full window' to add one", 12, y + 8, _fontSmall, TextMid);

			return;
		}

		foreach ((Bot bot, bool farming) in rows) {
			int h = farming ? MiniFocusH : MiniRowH;

			if (farming) {
				Fill(dc, 0, y, _w, h, Surface);
			}

			int dot = bot.IsOnline ? (bot.Paused || bot.PlayingBlocked ? Amber : Green) : TextDim;
			Fill(dc, 10, y + 12, 6, 6, dot);

			int nameColour = bot.IsOnline ? NameColour.Of(bot.Cfg.LogColour)?.Win32 ?? TextBright : TextDim;
			int nameW = TextWidth(dc, bot.Name, _fontBold);
			Clipped(dc, bot.Name, 24, y + 6, 96, _fontBold, nameColour);
			_hits.Add((24, y + 4, Math.Min(96, nameW) + 4, 20, bot.Name, 'n'));

			int textX = 24 + Math.Clamp(nameW, 36, 96) + 10;
			int buttonX = _w - 6 - 24;
			string doing = farming && (bot.PlayingApps.Count > 0) ? $"farming {GameNames.Of(bot.PlayingApps[0])}" : BotStatus.Of(bot).Doing;
			Clipped(dc, doing, textX, y + 8, buttonX - textX - 6, _fontSmall, farming ? TextNormal : TextMid);

			MiniRowButton(dc, bot.Running ? GlyphStop : GlyphPlay, buttonX, y + 4, 24, 22, bot.Name, bot.Running ? Red : Green);

			if (farming && (BotManager.ModuleOf<CardFarmer>(bot) is { } farmer)) {
				string left = (bot.CardsRemaining == 1 ? "1 card left" : $"{bot.CardsRemaining} cards left") + (bot.HumanOwned ? " · human mode" : "");
				string eta = "~" + Fmt.Rough(farmer.EstimateMinutes);

				Clipped(dc, left, 24, y + 28, _w - 24 - 70, _fontSmall, TextMid);
				Text(dc, eta, _w - 12 - TextWidth(dc, eta, _fontSmall), y + 28, _fontSmall, TextNormal);

				int total = farmer.RunCards;
				ProgressBar(dc, 24, y + 47, _w - 24 - 12, total - bot.CardsRemaining, total, Green);
			}

			y += h;
			Fill(dc, 0, y - 1, _w, 1, Rgb(28, 28, 28));
		}

		if (_mgr.All.Count > MiniMaxRows) {
			Text(dc, $"+{_mgr.All.Count - MiniMaxRows} more - 'full window' for all of them", 12, y + 8, _fontSmall, TextDim);
		}
	}

	/// <summary>A title-bar button drawn with an icon rather than a word - there's no room for words up there.</summary>
	private void AddIconButton(IntPtr dc, string glyph, int id, int x, int y, int w, int h, int colour) {
		_buttons.Add(new Button(glyph, id, x, y, w, h, colour));

		bool hover = _hoverId == id;

		if (hover) {
			Fill(dc, x, y, w, h, Rgb(32, 32, 32));
			Outline(dc, x, y, w, h, Border);
		}

		Text(dc, glyph, x + ((w - TextWidth(dc, glyph, _fontIcon)) / 2), y + ((h - 14) / 2), _fontIcon, colour);
	}

	/// <summary>An account's start/stop button in mini mode: the same 'x' action as the accounts sheet's.</summary>
	private void MiniRowButton(IntPtr dc, string glyph, int x, int y, int w, int h, string bot, int colour) {
		Outline(dc, x, y, w, h, Border);
		Text(dc, glyph, x + ((w - TextWidth(dc, glyph, _fontIcon)) / 2), y + ((h - 14) / 2), _fontIcon, colour);
		_hits.Add((x, y, w, h, bot, 'x'));
	}

	/// <summary>Flip "keep mini mode on top", save it, and put it into effect.</summary>
	private void ToggleOnTop() {
		try {
			Live.Global.MiniOnTop = !Live.Global.MiniOnTop;
			ConfigStore.SaveGlobal(Live.Global);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the keep-on-top choice: {0}", e.Message));
		}

		ApplyOnTop();
	}

	private void ApplyOnTop() {
		if (_hwnd != IntPtr.Zero) {
			SetWindowPos(_hwnd, _mini && Live.Global.MiniOnTop ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
		}

		Invalidate();
	}

	// ── drawing helpers ─────────────────────────────────────────────────────
	private void AddButton(IntPtr dc, string text, int id, int x, int y, int w, int h, int colour) {
		_buttons.Add(new Button(text, id, x, y, w, h, colour));

		bool hover = _hoverId == id;

		Fill(dc, x, y, w, h, hover ? colour : Panel);
		Outline(dc, x, y, w, h, hover ? colour : Border);

		int textWidth = TextWidth(dc, text, _fontSmall);
		Text(dc, text, x + ((w - textWidth) / 2), y + ((h - 14) / 2), _fontSmall, hover ? Bg : colour);
	}

	private static void ProgressBar(IntPtr dc, int x, int y, int w, int done, int total, int colour) {
		int filled = total <= 0 ? 0 : Math.Clamp((int) ((double) done / total * w), 0, w);

		Fill(dc, x, y, w, 3, Rgb(32, 32, 32));

		if (filled > 0) {
			Fill(dc, x, y, filled, 3, colour);
		}
	}

	private static void Fill(IntPtr dc, int x, int y, int w, int h, int colour) {
		Rect r = new() { Left = x, Top = y, Right = x + w, Bottom = y + h };
		IntPtr brush = CreateSolidBrush(colour);
		FillRect(dc, ref r, brush);
		DeleteObject(brush);
	}

	private static void Outline(IntPtr dc, int x, int y, int w, int h, int colour) {
		Fill(dc, x, y, w, 1, colour);
		Fill(dc, x, y + h - 1, w, 1, colour);
		Fill(dc, x, y, 1, h, colour);
		Fill(dc, x + w - 1, y, 1, h, colour);
	}

	/// <summary>Draw inside a fixed width, cutting with an ellipsis instead of wrapping or overrunning.</summary>
	private static void Clipped(IntPtr dc, string text, int x, int y, int width, IntPtr font, int colour) {
		if (string.IsNullOrEmpty(text)) {
			return;
		}

		IntPtr old = SelectObject(dc, font);
		SetTextColor(dc, colour);
		Rect r = new() { Left = x, Top = y, Right = x + width, Bottom = y + LineH };
		DrawText(dc, text, -1, ref r, DtLeft | DtSingleLine | DtNoPrefix | DtEndEllipsis);
		SelectObject(dc, old);
	}

	private static void Text(IntPtr dc, string text, int x, int y, IntPtr font, int colour) {
		if (string.IsNullOrEmpty(text)) {
			return;
		}

		IntPtr old = SelectObject(dc, font);
		SetTextColor(dc, colour);
		// Deliberately no meaningful right edge: single-line, and anything that must be bounded goes through
		// Clipped() with an explicit width. Clipping here at the window width broke as soon as it could resize.
		Rect r = new() { Left = x, Top = y, Right = x + 4096, Bottom = y + 20 };
		DrawText(dc, text, -1, ref r, DtLeft | DtSingleLine | DtNoPrefix);
		SelectObject(dc, old);
	}

	private static int TextWidth(IntPtr dc, string text, IntPtr font) {
		IntPtr old = SelectObject(dc, font);
		GetTextExtentPoint32(dc, text, text.Length, out Size size);
		SelectObject(dc, old);

		return size.Width;
	}

	private static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);
	private static int LoWord(IntPtr value) => (short) (value.ToInt64() & 0xFFFF);
	private static int HiWord(IntPtr value) => (short) ((value.ToInt64() >> 16) & 0xFFFF);

	/// <summary>
	/// Write down where the window is, in whichever layout it's in. Called on the way out: the window is never told it
	/// is closing when the app shuts down (DestroyWindow from another thread does nothing), so without this a window
	/// moved any way but a finished drag opened back where it was before.
	/// </summary>
	public void SavePlace() {
		if (_hwnd == IntPtr.Zero) {
			return;
		}

		if (_mini) {
			RememberMiniSpot();
		} else {
			RememberSize();
		}
	}

	public void Dispose() {
		_running = false;

		SavePlace();

		if (_hwnd != IntPtr.Zero) {
			DestroyWindow(_hwnd);
			_hwnd = IntPtr.Zero;
		}

		foreach (IntPtr font in new[] { _fontUi, _fontBold, _fontSmall, _fontMono, _fontIcon, _inputBrush }) {
			if (font != IntPtr.Zero) {
				DeleteObject(font);
			}
		}

		foreach (IntPtr icon in new[] { _iconBig, _iconSmall }) {
			if (icon != IntPtr.Zero) {
				DestroyIcon(icon);
			}
		}

		_iconBig = _iconSmall = IntPtr.Zero;
	}

	// ── Win32 ───────────────────────────────────────────────────────────────
	private const int WmDestroy = 0x0002;
	private const int WmPaint = 0x000F;
	private const int WmClose = 0x0010;
	private const int WmEraseBkgnd = 0x0014;
	private const int WmSetFont = 0x0030;
	private const int WmCtlColorEdit = 0x0133;
	private const int Opaque = 2;
	private const int EmSetSel = 0x00B1;
	private const int WmKeyDown = 0x0100;
	private const int WmTimer = 0x0113;
	private const int WmMouseMove = 0x0200;
	private const int WmMouseWheel = 0x020A;
	private const int WmMouseLeave = 0x02A3;
	private const int WmSize = 0x0005;
	private const int WmGetMinMaxInfo = 0x0024;
	private const int WmExitSizeMove = 0x0232;
	private const uint WsThickFrame = 0x00040000;
	private const uint TmeLeave = 0x00000002;
	private const int WmLButtonDown = 0x0201;
	private const int WmNcLButtonDown = 0x00A1;

	private const uint WsPopup = 0x80000000;
	private const uint WsChild = 0x40000000;
	private const uint WsVisible = 0x10000000;
	private const uint WsClipChildren = 0x02000000;
	private const uint WsMinimizeBox = 0x00020000;

	// Keeps the window out of the taskbar and Alt-Tab - it's a background process that lives in the tray, only
	// brought up to set things up. The caption is hand-drawn (WS_POPUP), so the tool-window style costs nothing
	// visually here; it just removes the taskbar button.
	private const int WsExToolWindow = 0x00000080;
	private const int EsAutoHScroll = 0x0080;

	private const int SwHide = 0;
	private const int SwShow = 5;
	private const int HtCaption = 2;
	private const int Transparent = 1;
	private const int SrcCopy = 0x00CC0020;
	private const int DtLeft = 0x0000;
	private const int DtSingleLine = 0x0020;
	private const int DtNoPrefix = 0x0800;
	private const int DtEndEllipsis = 0x8000;
	private const int VkReturn = 0x0D;
	private const int GwlWndProc = -4;
	private const int SmCxScreen = 0;
	private const int SmCyScreen = 1;
	private const int SmCxIcon = 11;
	private const int SmCyIcon = 12;
	private const int SmCxSmIcon = 49;
	private const int SmCySmIcon = 50;
	private const int IdcArrow = 32512;
	private const uint ImageIcon = 1;
	private const uint LrLoadFromFile = 0x0010;
	private const int WmSetIcon = 0x0080;
	private const int IconSmall = 0;
	private const int IconBig = 1;
	private const int DwmUseImmersiveDarkMode = 20;
	private const int DwmBorderColor = 34;   // DWMWA_BORDER_COLOR, Windows 11 22000+
	private const int DwmWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE, Windows 11 22000+
	private const int DwmCornerRound = 2;   // DWMWCP_ROUND

	private const int IdStartAll = 1;
	private const int IdStopAll = 2;
	private const int IdDashboard = 3;
	private const int IdHide = 4;
	private const int IdQuit = 5;
	private const int IdOpenSheet = 6;
	private const int IdOpenHelp = 15;
	private const int IdCloseSheet = 7;
	private const int IdCloseHelp = 14;
	private const int IdAddAccount = 8;
	private const int IdMini = 16;
	private const int IdFullWindow = 17;
	private const int IdPin = 18;
	private const int IdInput = 100;

	/// <summary>WM_APP + 1: mini mode on (wParam 1) or off, posted so the switch always runs on the window's own thread.</summary>
	private const int WmSetMini = 0x8001;
	private const int WmChar = 0x0102;
	private const int WmQueryEndSession = 0x0011;
	private const int WmEndSession = 0x0016;
	private const int EmSetPasswordChar = 0x00CC;
	private const int SwRestore = 9;
	private const uint MonitorDefaultToNearest = 2;

	/// <summary>WM_APP + 2: put the keep-on-top setting into effect after it was changed somewhere else.</summary>
	private const int WmRefreshOnTop = 0x8002;
	private const uint SwpNoSize = 0x0001;
	private const int GwlStyle = -16;
	private const uint SwpNoMove = 0x0002;
	private const uint SwpNoZOrder = 0x0004;
	private const uint SwpNoActivate = 0x0010;
	private const uint SwpFrameChanged = 0x0020;
	private static readonly IntPtr HwndTopmost = new(-1);
	private static readonly IntPtr HwndNoTopmost = new(-2);
	private const int SpiGetWorkArea = 0x0030;

	private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
	[StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
	[StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

	[StructLayout(LayoutKind.Sequential)]
	private struct TrackMouseEventArgs { public int Size; public uint Flags; public IntPtr Window; public uint HoverTime; }

	[StructLayout(LayoutKind.Sequential)]
	private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

	[StructLayout(LayoutKind.Sequential)]
	private struct Msg { public IntPtr Hwnd; public int Message; public IntPtr WParam, LParam; public int Time; public Point Pt; }

	[StructLayout(LayoutKind.Sequential)]
	private struct PaintStruct { public IntPtr Hdc; public int Erase; public Rect Paint; public int Restore, IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WndClassEx {
		public int cbSize, style;
		public IntPtr lpfnWndProc;
		public int cbClsExtra, cbWndExtra;
		public IntPtr hInstance, hIcon, hCursor, hbrBackground;
		[MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
		[MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
		public IntPtr hIconSm;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx wc);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
	// CharSet.Unicode is load-bearing: this is a Unicode window (RegisterClassExW), so its default message handler
	// MUST be DefWindowProcW. Without it the P/Invoke defaults to DefWindowProcA, and WM_SETTEXT - which is how a
	// title is stored - then reads the incoming UTF-16 string as ANSI and keeps only the first character. That is
	// why the window was named "n" instead of "nocatFarm" no matter how the title was set.
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, int min, int max);
	[DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
	[DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
	[DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
	[DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
	[DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr hIcon);
	[DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouseEventArgs args);
	[DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
	[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
	[DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
	[DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
	[DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
	[DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
	[DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect r, IntPtr brush);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int count, ref Rect r, int format);
	[DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int cursor);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
	[DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
	[DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, int elapse, IntPtr func);
	[DllImport("user32.dll")] private static extern bool ReleaseCapture();
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hwnd, string text);
	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
	[DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
	[DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(int action, int param, out Rect rect, int winIni);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int colour);
	[DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
	[DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
	[DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int colour);
	[DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr dc, int colour);
	[DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
	[DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
	[DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
	[DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
	[DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
	[DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int len, out Size size);
	[DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

	[DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
