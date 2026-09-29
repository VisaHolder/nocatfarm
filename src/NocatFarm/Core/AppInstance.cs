using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NocatFarm.Core;

/// <summary>
/// The one running copy for a folder, and the two things another process can ask of it: come to the front (a
/// second launch - the Start menu, the desktop shortcut) and close cleanly (the installer, before it replaces
/// files or uninstalls). Named after the folder, like the single-instance lock, so copies in two folders never
/// hear each other.
/// </summary>
public static class AppInstance {
	/// <summary>The part of every name that says which folder: the same hash the single-instance lock has always used.</summary>
	public static string Id(string root) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];

	public static string LockName(string root) => "nocatFarm-" + Id(root);

	private static string EventName(string root, string what) => $"nocatFarm-{what}-{Id(root)}";

	/// <summary>
	/// Ask the running copy for this folder to "show" or "quit". False when there is no copy listening - none
	/// running, or one from before this existed.
	/// </summary>
	public static bool Signal(string root, string what) {
		if (!OperatingSystem.IsWindows()) {
			return false;
		}

		try {
			using EventWaitHandle handle = EventWaitHandle.OpenExisting(EventName(root, what));

			// Windows only lets the program the person is using put a window in front. Whoever asks for "show" is
			// that program right now, so it hands the right over before asking.
			AllowSetForegroundWindow(AsfwAny);
			handle.Set();

			return true;
		} catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) {
			return false;
		}
	}

	/// <summary>Wait for the copy for this folder to be gone: its lock free. True when it is, false at the time limit.</summary>
	public static bool WaitUntilClosed(string root, TimeSpan limit) {
		DateTime until = DateTime.UtcNow + limit;

		while (DateTime.UtcNow < until) {
			if (!Mutex.TryOpenExisting(LockName(root), out Mutex? held)) {
				return true;
			}

			using (held) {
				try {
					if (held.WaitOne(0)) {
						held.ReleaseMutex();

						return true;
					}
				} catch (AbandonedMutexException) {
					return true;   // it went without letting go - gone all the same
				}
			}

			Thread.Sleep(500);
		}

		return false;
	}

	private static Thread? _listener;

	/// <summary>Answer "show" and "quit" for this folder, on a thread of its own, for as long as the app runs.</summary>
	public static void Listen(string root, Action show, Action quit) {
		if (!OperatingSystem.IsWindows() || (_listener != null)) {
			return;
		}

		EventWaitHandle showing, quitting;

		try {
			showing = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(root, "show"));
			quitting = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(root, "quit"));
		} catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) {
			Log.Debug(new Said("couldn't listen for a second launch: {0}", e.Message));

			return;
		}

		_listener = new Thread(() => {
			WaitHandle[] both = [showing, quitting];

			while (true) {
				int which = WaitHandle.WaitAny(both);

				try {
					if (which == 0) {
						show();
					} else {
						quit();
					}
				} catch (Exception e) {
					Log.Debug(new Said("couldn't answer another launch: {0}", e.Message));
				}
			}
		}) { IsBackground = true, Name = "nocat.farm instance" };

		_listener.Start();
	}

	private const int AsfwAny = -1;

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(int processId);
}
