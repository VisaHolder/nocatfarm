using System.Runtime.InteropServices;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The few things that depend on what this is running on.
///
/// Windows gets the app window, the tray icon and the rest of the desktop. Linux - a server, a Raspberry Pi, a
/// Docker container - gets the console and the web dashboard and nothing that needs a screen. Everything else
/// in the app is the same code on both.
/// </summary>
public static class Platform {
	/// <summary>
	/// Running inside a container. Microsoft's .NET images all set DOTNET_RUNNING_IN_CONTAINER, and Docker leaves
	/// /.dockerenv in every container it starts, so either one is enough. Never true on Windows.
	/// </summary>
	public static bool InContainer { get; } = !OperatingSystem.IsWindows()
		&& (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
			|| File.Exists("/.dockerenv"));

	/// <summary>
	/// Whether there is a desktop to open a browser on. Always on Windows. On Linux only with a graphical session -
	/// on a server or in a container there is nothing to open, and trying just fails with a confusing error.
	/// </summary>
	public static bool HasDesktop => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
		|| (!InContainer && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
			|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))));

	/// <summary>Opens a folder in the file manager on this machine - Explorer, Finder, or the desktop's own. False when
	/// there's no desktop to open it on (a server, a container) or it wouldn't start.</summary>
	public static bool OpenFolder(string dir) {
		if (!HasDesktop || !Directory.Exists(dir)) {
			return false;
		}

		try {
			string opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
			System.Diagnostics.ProcessStartInfo start = new(opener) { UseShellExecute = false };
			start.ArgumentList.Add(dir);
			System.Diagnostics.Process.Start(start)?.Dispose();

			return true;
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			return false;
		}
	}

	/// <summary>"linux-x64", "linux-arm64" and so on: which release zip fits this machine.</summary>
	public static string ReleaseRid {
		get {
			string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
			string arch = RuntimeInformation.OSArchitecture switch {
				Architecture.X64 => "x64",
				Architecture.Arm64 => "arm64",
				Architecture.Arm => "arm",
				Architecture.X86 => "x86",
				Architecture other => other.ToString().ToLowerInvariant()
			};

			return $"{os}-{arch}";
		}
	}

	/// <summary>
	/// Say so, once and up front, when a folder the app saves into can't be written.
	///
	/// On Linux, and in Docker above all, that's the usual first-run snag: a bind-mounted folder Docker made as
	/// root, which the container's user can't write. Left alone it surfaces much later as a string of "couldn't
	/// save" warnings - or as file logging quietly switching itself off. Off Windows only; nothing changes there.
	/// </summary>
	public static void CheckWritable(params string[] dirs) {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		foreach (string dir in dirs) {
			try {
				Directory.CreateDirectory(dir);
				string probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
				File.WriteAllText(probe, "");
				File.Delete(probe);
			} catch (Exception e) when (e is UnauthorizedAccessException or IOException) {
				if (InContainer) {
					Log.Error(new Said("can't write to {0} as uid {1}", dir, getuid()));
					Log.Info(new Said("fix on the host: sudo chown -R {0}:{1} that folder", getuid(), getgid()));
					Log.Info(new Said("or set user: in docker-compose.yml to its owner"));
				} else {
					Log.Error(new Said("can't write to {0} ({1}) - nothing is saved (uid {2})", dir, e.Message, getuid()));
				}
			}
		}
	}

	[DllImport("libc")]
	private static extern uint getuid();

	[DllImport("libc")]
	private static extern uint getgid();

	/// <summary>Is this dashboard address this machine only?</summary>
	public static bool IsLoopback(string host) =>
		host.Trim().Trim('[', ']') is "127.0.0.1" or "localhost" or "::1";

	// ── environment overrides ───────────────────────────────────────────────
	private const string EnvHost = "NOCATFARM_WEB_HOST";
	private const string EnvPort = "NOCATFARM_WEB_PORT";
	private const string EnvPassword = "NOCATFARM_WEB_PASSWORD";
	private const string EnvPasswordFile = "NOCATFARM_WEB_PASSWORD_FILE";
	private const string EnvHomeAddress = "NOCATFARM_HOME_ADDRESS";

	/// <summary>
	/// The computer's own address on the home network, for the phone link - "192.168.1.20", or with a port when Docker
	/// publishes a different one. Inside a container nocat.farm only sees Docker's internal network, which no phone can
	/// reach, so this is the only way it can know. Null when not set.
	/// </summary>
	public static string? HomeAddress => Environment.GetEnvironmentVariable(EnvHomeAddress)?.Trim() is { Length: > 0 } v ? v : null;

	/// <summary>
	/// The dashboard's address, port and password from environment variables, when they are set.
	///
	/// For Docker, where editing a JSON file inside a volume is the awkward way round and a compose file or a
	/// Docker secret is the natural one. Nothing changes when none of them is set - which is every ordinary run.
	/// A value that is set wins at every start and is written into config/nocatFarm.json, so the settings page
	/// shows what is actually in use.
	/// </summary>
	public static void ApplyEnvironment(GlobalConfig g) {
		bool changed = false;

		string? host = Environment.GetEnvironmentVariable(EnvHost)?.Trim();

		if (!string.IsNullOrEmpty(host) && (host != g.WebHost)) {
			g.WebHost = host;
			changed = true;
			Log.Info(new Said("dashboard: listening on {0}, from {1}", host, EnvHost));
		}

		string? port = Environment.GetEnvironmentVariable(EnvPort)?.Trim();

		if (!string.IsNullOrEmpty(port)) {
			if (int.TryParse(port, out int p) && (p is >= 1 and <= 65535)) {
				if (p != g.WebPort) {
					g.WebPort = p;
					changed = true;
					Log.Info(new Said("dashboard: port {0}, from {1}", p, EnvPort));
				}
			} else {
				Log.Warn(new Said("{0}=\"{1}\" isn't a port - keeping {2}", EnvPort, port, g.WebPort));
			}
		}

		// The file wins over the plain variable: that is how Docker secrets arrive, and whoever set it up
		// went to the trouble of keeping the password out of the environment.
		string? password = Environment.GetEnvironmentVariable(EnvPassword);
		string? file = Environment.GetEnvironmentVariable(EnvPasswordFile)?.Trim();

		if (!string.IsNullOrEmpty(file)) {
			try {
				password = File.ReadAllText(file);
			} catch (Exception e) {
				Log.Warn(new Said("couldn't read the dashboard password from {0} ({1})", file, e.Message));
			}
		}

		password = password?.Trim('\r', '\n');

		if (!string.IsNullOrEmpty(password) && (password != g.WebPassword)) {
			g.WebPassword = password;
			changed = true;
			Log.Info(new Said("dashboard: password set from {0}", string.IsNullOrEmpty(file) ? EnvPassword : EnvPasswordFile));
		}

		if (changed) {
			ConfigStore.SaveGlobal(g);
		}
	}
}
