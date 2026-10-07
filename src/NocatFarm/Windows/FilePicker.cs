using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NocatFarm.Core;

namespace NocatFarm.Windows;

/// <summary>
/// The window's "pick a file" window, for 'update file' typed with no path: Windows' own open-file window, owned by the
/// nocat.farm window and run on its thread - the one thread here with a message pump that may show one.
/// </summary>
public sealed partial class MainWindow {
	/// <summary>WM_APP + 3: run what's waiting in <see cref="_onThread"/>, posted so it runs on the window's own thread.</summary>
	private const int WmRunHere = 0x8003;

	/// <summary>The timer that brings the "pick a file" window to the top once it's up. The window's own ticks are timer 1.</summary>
	private const int PickTimer = 2;

	private readonly ConcurrentQueue<Action> _onThread = new();

	/// <summary>Ticks of <see cref="PickTimer"/> without finding the window, so it gives up after a few seconds.</summary>
	private int _pickTicks;

	/// <summary>The window is up and pumping, so it can own a "pick a file" window.</summary>
	internal bool CanPick => _running && _ready && (_hwnd != IntPtr.Zero);

	/// <summary>
	/// Windows' open-file window for a nocat.farm zip, starting in Downloads, on top of everything. The path picked, or null
	/// for Cancel. From any thread: the window itself is shown on the window's own.
	/// </summary>
	internal Task<string?> PickZipAsync() {
		TaskCompletionSource<string?> picked = new(TaskCreationOptions.RunContinuationsAsynchronously);

		_onThread.Enqueue(() => {
			try {
				picked.TrySetResult(ShowPicker());
			} catch (Exception e) {
				picked.TrySetException(e);
			}
		});

		if ((_hwnd == IntPtr.Zero) || !PostMessage(_hwnd, WmRunHere, IntPtr.Zero, IntPtr.Zero)) {
			picked.TrySetException(new InvalidOperationException("the nocat.farm window isn't open"));
		}

		return picked.Task;
	}

	/// <summary>Everything posted to run on the window's thread. Only ever called there, from the message.</summary>
	private void RunPosted() {
		while (_onThread.TryDequeue(out Action? run)) {
			run();
		}
	}

	private string? ShowPicker() {
		const int Chars = 4096;
		IntPtr file = Marshal.AllocHGlobal(Chars * 2);

		try {
			Marshal.WriteInt16(file, 0);

			OpenFileName ofn = new() {
				lStructSize = Marshal.SizeOf<OpenFileName>(),
				hwndOwner = _hwnd,
				// Pairs of "what it's called" and "*.zip", each ending in a nul, and one more at the end.
				lpstrFilter = Loc.T("nocat.farm zip") + " (*.zip)\0*.zip\0\0",
				nFilterIndex = 1,
				lpstrFile = file,
				nMaxFile = Chars,
				lpstrInitialDir = Downloads(),
				lpstrTitle = Loc.T("Pick the nocat.farm zip to install"),
				Flags = OfnFileMustExist | OfnPathMustExist | OfnHideReadOnly | OfnNoChangeDir | OfnExplorer
			};

			// Typed in the dashboard, the browser has the screen, and Windows won't hand another program the front - the
			// window opened behind it and only its taskbar button flashed. Put on top by the timer once it's there.
			_pickTicks = 0;
			SetTimer(_hwnd, new IntPtr(PickTimer), 50, IntPtr.Zero);

			try {
				if (GetOpenFileName(ref ofn)) {
					return Marshal.PtrToStringUni(file);
				}
			} finally {
				KillTimer(_hwnd, new IntPtr(PickTimer));
			}

			// Cancel says nothing; anything else went wrong.
			int error = CommDlgExtendedError();

			return error == 0 ? null : throw new InvalidOperationException($"Windows refused the window to pick a file (error {error:x})");
		} finally {
			Marshal.FreeHGlobal(file);
		}
	}

	/// <summary>
	/// The "pick a file" window, once it's there: put on top of every other window, and to the front if Windows lets it.
	/// It's this thread's own dialog window, owned by the nocat.farm window.
	/// </summary>
	private void RaisePicker() {
		IntPtr found = IntPtr.Zero;
		StringBuilder kind = new(16);

		EnumThreadWindows(GetCurrentThreadId(), (h, _) => {
			kind.Clear();

			if (IsWindowVisible(h) && (GetWindow(h, GwOwner) == _hwnd) && (GetClassName(h, kind, kind.Capacity) > 0) && (kind.ToString() == "#32770")) {
				found = h;

				return false;
			}

			return true;
		}, IntPtr.Zero);

		if (found != IntPtr.Zero) {
			KillTimer(_hwnd, new IntPtr(PickTimer));
			SetWindowPos(found, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
			SetForegroundWindow(found);
		} else if (++_pickTicks > 100) {
			KillTimer(_hwnd, new IntPtr(PickTimer));   // five seconds and no window: it opened as it opened
		}
	}

	/// <summary>The user's Downloads folder, where a zip from GitHub lands - wherever they've moved it to.</summary>
	private static string? Downloads() {
		try {
			if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr path) == 0) {
				try {
					return Marshal.PtrToStringUni(path);
				} finally {
					Marshal.FreeCoTaskMem(path);
				}
			}
		} catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
			// the usual place, then
		}

		string usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

		return Directory.Exists(usual) ? usual : null;
	}

	private const int OfnHideReadOnly = 0x4;
	private const int OfnNoChangeDir = 0x8;
	private const int OfnPathMustExist = 0x800;
	private const int OfnFileMustExist = 0x1000;
	private const int OfnExplorer = 0x80000;
	private const uint GwOwner = 4;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct OpenFileName {
		public int lStructSize;
		public IntPtr hwndOwner;
		public IntPtr hInstance;
		public string lpstrFilter;
		public IntPtr lpstrCustomFilter;
		public int nMaxCustFilter;
		public int nFilterIndex;
		public IntPtr lpstrFile;
		public int nMaxFile;
		public IntPtr lpstrFileTitle;
		public int nMaxFileTitle;
		public string? lpstrInitialDir;
		public string lpstrTitle;
		public int Flags;
		public short nFileOffset;
		public short nFileExtension;
		public IntPtr lpstrDefExt;
		public IntPtr lCustData;
		public IntPtr lpfnHook;
		public IntPtr lpTemplateName;
		public IntPtr pvReserved;
		public int dwReserved;
		public int FlagsEx;
	}

	private delegate bool EnumThreadProc(IntPtr hwnd, IntPtr lParam);

	[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")] private static extern bool GetOpenFileName(ref OpenFileName ofn);
	[DllImport("comdlg32.dll")] private static extern int CommDlgExtendedError();
	[DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
	[DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadProc proc, IntPtr lParam);
	[DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
	[DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
	[DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
