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

	/// <summary>Off Windows: the file the running copy for a folder keeps locked, with its process id in it.</summary>
	public static string LockFile(string root) => Path.Combine(root, "config", "state", "instance.lock");

	/// <summary>
	/// Claim this folder for this process: a handle to keep for as long as it runs, or null when another copy already has it.
	/// </summary>
	/// <remarks>
	/// On Windows a named mutex, as always. Anywhere else a named mutex without "Global\" is only seen inside one login
	/// session, so a copy started from another terminal, over SSH or by setsid never saw the first one - two copies ran on
	/// one config folder. There an exclusive lock on config/state/instance.lock does it: the system lets it go the moment
	/// the process ends, however it ends, so a crash never leaves the folder claimed.
	/// </remarks>
	public static IDisposable? TryClaim(string root) {
		if (OperatingSystem.IsWindows()) {
			Mutex mutex = new(false, LockName(root));

			try {
				if (mutex.WaitOne(TimeSpan.Zero, false)) {
					return mutex;
				}
			} catch (AbandonedMutexException) {
				return mutex;   // the last one went without letting go - it's ours now
			}

			mutex.Dispose();

			return null;
		}

		string path = LockFile(root);
		FileStream held;

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		} catch (IOException e) when (IsHeldByAnother(e)) {
			return null;   // another copy holds it
		} catch (Exception e) when (e is UnauthorizedAccessException or IOException) {
			// A folder this user can't write (made by root: sudo once, or a Docker bind mount) or a read-only disk isn't
			// "another copy is running", and not a crash either: it runs unclaimed, and the writability check straight
			// after says what's wrong and how to fix it. Nothing can be saved there, so nothing can be saved over.
			return new Unclaimed();
		}

		// Which process has it, for a person (or the update script) to read. The lock is advisory, so reading is allowed.
		try {
			byte[] pid = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
			held.SetLength(0);
			held.Write(pid);
			held.Flush();
		} catch (IOException) {
			// the lock is what matters
		}

		return held;
	}

	/// <summary>
	/// The lock refused because another process has it (EWOULDBLOCK, which .NET gives as the IOException's HResult off
	/// Windows: 11 on Linux, 35 on a Mac and the BSDs) - not because the file or its folder can't be made or opened.
	/// </summary>
	private static bool IsHeldByAnother(IOException e) =>
		e is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
		&& (e.HResult == (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 11 : 35));

	/// <summary>A claim that holds nothing: the folder couldn't be locked for a reason other than another copy.</summary>
	private sealed class Unclaimed : IDisposable {
		public void Dispose() {
			// nothing was taken
		}
	}

	/// <summary>Off Windows: the process id written in the lock file by the copy that holds it, or 0.</summary>
	/// <remarks>
	/// Read with the system's own open and read: every FileStream .NET opens off Windows takes a lock of its own (a shared
	/// one to read), which the running copy's exclusive lock refuses - so the file that says who holds it couldn't be read
	/// by the one copy that wants to know. The lock is advisory; a plain read is always allowed.
	/// </remarks>
	public static int HolderPid(string root) {
		if (OperatingSystem.IsWindows()) {
			return 0;
		}

		try {
			int fd = SysOpen(LockFile(root), 0);   // O_RDONLY, the same on Linux and a Mac

			if (fd < 0) {
				return 0;
			}

			try {
				byte[] buffer = new byte[32];
				nint read = SysRead(fd, buffer, buffer.Length);

				return (read > 0) && int.TryParse(Encoding.ASCII.GetString(buffer, 0, (int) read).Split('\n')[0].Trim(),
					System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) ? pid : 0;
			} finally {
				_ = SysClose(fd);
			}
		} catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) {
			return 0;
		}
	}

	[DllImport("libc", EntryPoint = "open")]
	private static extern int SysOpen(string path, int flags);

	[DllImport("libc", EntryPoint = "read")]
	private static extern nint SysRead(int fd, byte[] buffer, nint count);

	[DllImport("libc", EntryPoint = "close")]
	private static extern int SysClose(int fd);

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

		if (!OperatingSystem.IsWindows()) {
			while (DateTime.UtcNow < until) {
				using (IDisposable? claimed = TryClaim(root)) {
					if (claimed != null) {
						return true;
					}
				}

				Thread.Sleep(500);
			}

			return false;
		}

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
			Log.Debug(new Said("couldn't listen for a second launch: {0}", Log.Describe(e)));

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
					// show and quit catch nothing themselves: whatever reaches here is a bug.
					Log.Debug(new Said("couldn't answer another launch: {0}", Log.Describe(e)));
					Log.StackToFile(e);
				}
			}
		}) { IsBackground = true, Name = "nocat.farm instance" };

		_listener.Start();
	}

	private const int AsfwAny = -1;

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(int processId);
}
