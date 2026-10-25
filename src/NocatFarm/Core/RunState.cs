using System.Globalization;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.4: how the last run ended, so a copy that comes back by itself says so. A crash, a power cut, the machine
/// restarting or a watcher bringing it back used to be silent - and on a box with no screen the Telegram it sends is
/// the only sign anyone gets. Closed on purpose ('exit', the tray, the window) or restarted by an update: nothing said.
/// </summary>
/// <remarks>
/// config/state/run-state.txt holds one line: "running &lt;utc ticks&gt;" while it runs (the time refreshed every few
/// minutes, so it says about when it went down), or "stopped &lt;why&gt;" once it has closed on its way out. A file still
/// saying "running" at the next start is a run that never got to close.
/// </remarks>
public static class RunState {
	/// <summary>Why it's closing: "you", "update" or "system" (a signal - the machine shutting down, a service stop). Set before the exit is asked for.</summary>
	public static string Why { get; set; } = "you";

	/// <summary>How often the "still running" time is written.</summary>
	public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

	private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "state", "run-state.txt");
	private static Timer? _timer;

	/// <summary>What the last run left behind: null when it closed on purpose or was updated (or there's no record), else when it was last seen running (null time: not known).</summary>
	public sealed record Ended(DateTime? LastSeen, bool System);

	/// <summary>Reads how the last run ended, then marks this one running. Null: nothing worth saying.</summary>
	public static Ended? Begin(DateTime now) {
		Ended? last = Read(SafeRead());
		Mark(now);
		_timer = new Timer(_ => Mark(DateTime.UtcNow), null, Every, Every);

		return last;
	}

	/// <summary>Closing: what's written instead of "running".</summary>
	public static void End() {
		_timer?.Dispose();
		_timer = null;
		Write($"stopped {Why}");
	}

	/// <summary>The file's line, as how the last run ended.</summary>
	public static Ended? Read(string? line) {
		if (string.IsNullOrWhiteSpace(line)) {
			return null;   // the first run, or no record
		}

		string[] parts = line.Trim().Split(' ', 2);

		return parts[0] switch {
			"running" => new Ended(parts.Length > 1 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long t) && (t > 0)
				? new DateTime(t, DateTimeKind.Utc) : null, false),
			"stopped" when (parts.Length > 1) && (parts[1].Trim() == "system") => new Ended(null, true),
			_ => null
		};
	}

	/// <summary>
	/// 1.8.5: when the machine last started (UTC), or null when it can't be told for sure. Linux: /proc/stat's btime, trusted
	/// only when /proc/uptime agrees with it - inside proot (Termux on an Android box) both are made up (btime in 2023, uptime
	/// minutes) and a made-up one would call every reboot a crash. Windows and macOS: the time since boot the system counts.
	/// </summary>
	public static DateTime? MachineStarted(DateTime now) {
		if (!OperatingSystem.IsLinux()) {
			return now - TimeSpan.FromMilliseconds(Environment.TickCount64);
		}

		try {
			return Booted(File.ReadAllText("/proc/stat"), File.ReadAllText("/proc/uptime"), now);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	/// <summary>The boot time /proc/stat and /proc/uptime agree on (within 2 minutes), else null.</summary>
	public static DateTime? Booted(string stat, string uptime, DateTime now) {
		string? btime = stat.Split('\n').FirstOrDefault(static l => l.StartsWith("btime ", StringComparison.Ordinal));

		if ((btime == null) || !long.TryParse(btime[6..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long unix) || (unix <= 0)
			|| !double.TryParse(uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double up)) {
			return null;
		}

		DateTime byStat = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, byUptime = now - TimeSpan.FromSeconds(up);

		return ((byStat - byUptime).Duration() <= TimeSpan.FromMinutes(2)) && (byStat <= now) ? byStat : null;
	}

	/// <summary>The line for Telegram and the log, or null when there's nothing to say.</summary>
	/// <param name="machineStarted">1.8.5: when the machine last started (<see cref="MachineStarted"/>) - after the run was last
	/// seen: the machine restarted; before: nocat.farm alone stopped (a crash). Null: not known - the line says either.</param>
	public static Said? Line(Ended? last, DateTime now, int accounts, DateTime? machineStarted = null) {
		if (last == null) {
			return null;
		}

		if (last.System) {
			return new Said("nocat.farm is back up after the machine shut it down (a restart or a stop) · {0} account(s) signing in", accounts);
		}

		if (last.LastSeen is { } seen && (now > seen) && (machineStarted is { } booted)) {
			string ago = Fmt.Hm(Math.Max(1, (int) Math.Ceiling((now - seen).TotalMinutes)));

			return booted > seen
				? new Said("nocat.farm is back up - the machine restarted (a power cut or a reboot) at about {0}, last seen running {1} ago · {2} account(s) signing in",
					(Func<string>) (() => Fmt.Clock(booted)), ago, accounts)
				: new Said("nocat.farm is back up after it stopped unexpectedly (a crash - the machine kept running) - last seen running {0} ago · {1} account(s) signing in", ago, accounts);
		}

		if (last.LastSeen is { } seenAt && (now > seenAt)) {
			return new Said("nocat.farm is back up after an unexpected stop (a crash, a power cut or the machine restarting) - last seen running {0} ago · {1} account(s) signing in",
				Fmt.Hm(Math.Max(1, (int) Math.Ceiling((now - seenAt).TotalMinutes))), accounts);
		}

		return new Said("nocat.farm is back up after an unexpected stop (a crash, a power cut or the machine restarting) · {0} account(s) signing in", accounts);
	}

	private static void Mark(DateTime now) => Write($"running {now.Ticks.ToString(CultureInfo.InvariantCulture)}");

	private static string? SafeRead() {
		try {
			return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private static void Write(string line) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			AtomicFile.Write(FilePath, line);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// a note for the next start only - never worth stopping anything over
		}
	}
}
