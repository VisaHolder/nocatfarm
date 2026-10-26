using System.Globalization;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.5: how the machine nocat.farm runs on is doing - memory free, storage free where its files live, how hot the CPU
/// is - in 'status', Telegram's /status and the daily summary, with a warning once when it overheats, or storage or
/// memory runs out (and once more only after it recovered). reap's farm lives on a TV box nobody looks at.
/// </summary>
public static class BoxHealth {
	/// <param name="MemoryMb">Memory free for programs (null: can't be read here).</param>
	/// <param name="StorageMb">Storage free where nocat.farm's files are.</param>
	/// <param name="TempC">The hottest CPU sensor (null: none readable - most Android boxes and PCs).</param>
	public sealed record Health(long? MemoryMb, long? StorageMb, double? TempC);

	public const double HotC = 80;
	public const long LowStorageMb = 1024, LowMemoryMb = 200;
	public static readonly TimeSpan HotFor = TimeSpan.FromMinutes(10);

	/// <summary>Read now. Anything that can't be read is left out, silently.</summary>
	public static Health Read() => new(Memory(), Storage(), Temp());

	private static long? Memory() {
		try {
			if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo")) {
				string[] parts = (File.ReadLines("/proc/meminfo").FirstOrDefault(static l => l.StartsWith("MemAvailable:", StringComparison.Ordinal)) ?? "")
					.Split(' ', StringSplitOptions.RemoveEmptyEntries);

				return (parts.Length > 1) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kb) && (kb > 0) ? kb / 1024 : null;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// not readable here
		}

		return null;
	}

	private static long? Storage() {
		try {
			return new DriveInfo(Path.GetFullPath(ConfigStore.ConfigDir)).AvailableFreeSpace / (1024 * 1024);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
			return null;
		}
	}

	private static double? Temp() {
		try {
			if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/thermal")) {
				return null;
			}

			double? hottest = null;

			foreach (string zone in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*")) {
				try {
					// millidegrees; a sensor that says something silly (0, or over 150°C) is left out
					if (double.TryParse(File.ReadAllText(Path.Combine(zone, "temp")).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double milli)
						&& (milli / 1000 is > 1 and < 150 and var c) && ((hottest == null) || (c > hottest))) {
						hottest = c;
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// that sensor isn't readable
				}
			}

			return hottest;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	/// <summary>"2.4 GB memory free · 3.1 GB storage free · 54°C" - only what's known; null when nothing is.</summary>
	public static Said? Line(Health h) => ReportCard.Join([
		h.MemoryMb is { } m ? new Said("{0} memory free", Size(m)) : null,
		h.StorageMb is { } s ? new Said("{0} storage free", Size(s)) : null,
		h.TempC is { } t ? new Said("{0}°C", Math.Round(t).ToString(CultureInfo.InvariantCulture)) : null
	]);

	private static string Size(long mb) => mb >= 1024 ? (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : mb.ToString(CultureInfo.InvariantCulture) + " MB";

	/// <param name="HotSince">Since when it has been over <see cref="HotC"/> (null: it isn't).</param>
	/// <param name="Said">The warnings standing: "hot", "storage", "memory" - each said once, again only after it recovered.</param>
	public sealed record Watch(DateTime? HotSince, IReadOnlySet<string> Said);

	/// <summary>One look: the watch from here, and the warnings to say now.</summary>
	public static (Watch Next, List<Said> Warn) Step(Watch w, Health h, DateTime now) {
		HashSet<string> said = [.. w.Said];
		List<Said> warn = [];
		DateTime? hotSince = h.TempC is >= HotC ? w.HotSince ?? now : null;

		// 1.8.6: recovered only well clear of the line - memory hovering at 190, 210, 195 MB warned every few minutes.
		void Check(string key, bool bad, bool clear, Func<Said> line) {
			if (bad) {
				if (said.Add(key)) {
					warn.Add(line());
				}
			} else if (clear) {
				said.Remove(key);
			}
		}

		Check("hot", (hotSince is { } since) && (now - since >= HotFor), h.TempC is not >= HotC - 5, () => new Said("the machine is running hot - {0}°C for over {1} minutes; give it air (or a fan), or it may slow down or shut itself off",
			Math.Round(h.TempC ?? 0).ToString(CultureInfo.InvariantCulture), (int) HotFor.TotalMinutes));
		Check("storage", h.StorageMb is < LowStorageMb, h.StorageMb is not < LowStorageMb * 3 / 2, () => new Said("storage is nearly full where nocat.farm keeps its files - {0} free; logs and backups need room", Size(h.StorageMb ?? 0)));
		Check("memory", h.MemoryMb is < LowMemoryMb, h.MemoryMb is not < LowMemoryMb * 3 / 2, () => new Said("the machine is nearly out of memory - {0} free; nocat.farm (or something beside it) may be stopped", Size(h.MemoryMb ?? 0)));

		return (new Watch(hotSince, said), warn);
	}

	private static Timer? _timer;
	private static Watch _watch = new(null, new HashSet<string>());

	/// <summary>Looks once a minute while nocat.farm runs.</summary>
	public static void Start() {
		_timer = new Timer(_ => {
			try {
				(Watch next, List<Said> warn) = Step(_watch, Read(), DateTime.UtcNow);
				_watch = next;

				foreach (Said line in warn) {
					Log.Attention(line);
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't look at the machine's health: {0}", Log.Describe(e)));
			}
		}, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
	}
}
