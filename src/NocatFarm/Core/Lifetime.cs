using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// How long each account has spent actually running games, for as long as nocatFarm has been watching.
/// </summary>
/// <remarks>
/// The log could already tell you what an account is doing this minute and how far into today it is, but not
/// the one number anybody actually wants: how much has this thing done for me. "no cards left to farm - idling
/// 0m so far" thirty seconds after a logon was the reductio - technically true, and worth nothing.
///
/// Counted from what Steam was TOLD is running, not from wall-clock uptime, so an account sitting signed in
/// doing nothing accrues nothing. Written to disk on a timer rather than on every tick, because this is a
/// vanity number and is not worth an fsync a minute.
/// </remarks>
public static class Lifetime {
	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, double> Minutes = new(StringComparer.OrdinalIgnoreCase);

	private static DateTime _lastSave = DateTime.MinValue;
	private static bool _loaded;

	/// <summary>Set when the file existed but could not be read, so Save refuses to write over it.</summary>
	private static bool _loadFailed;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "lifetime.json");

	/// <summary>Total minutes this account has spent with a game running.</summary>
	public static int For(string bot) {
		Load();

		lock (Gate) {
			return Minutes.TryGetValue(bot, out double m) ? (int) m : 0;
		}
	}

	/// <summary>Every account added together.</summary>
	public static int Total {
		get {
			Load();

			lock (Gate) {
				return (int) Minutes.Values.Sum();
			}
		}
	}

	/// <summary>Credit time actually spent playing. Anything longer than a few minutes is treated as a gap.</summary>
	/// <remarks>
	/// Written every minute, not every five.
	///
	/// At five, everything since the last write was lost whenever the process ended without the graceful stop
	/// that calls Save - a crash, a kill, a machine going down. That is not the rare case it sounds like: it
	/// silently ate most of the number. An account with an hour and a half on the clock for the day was still
	/// reporting a lifetime total of three minutes, because each run only ever persisted the twenty seconds
	/// credited by its first tick and then died before the next five-minute boundary.
	///
	/// The cost of the shorter interval is one atomic write of a few hundred bytes a minute, which is nothing
	/// next to a total that is wrong by a factor of thirty.
	/// </remarks>
	public static void Add(string bot, double minutes) {
		if ((minutes <= 0) || (minutes > 5)) {
			return;
		}

		Load();

		bool due;

		lock (Gate) {
			Minutes[bot] = Minutes.GetValueOrDefault(bot) + minutes;
			due = DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(1);

			if (due) {
				_lastSave = DateTime.UtcNow;
			}
		}

		if (due) {
			Save();
		}
	}

	private static void Load() {
		lock (Gate) {
			if (_loaded) {
				return;
			}

			_loaded = true;

			try {
				if (File.Exists(Path)) {
					Dictionary<string, double>? saved = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path));

					if (saved != null) {
						foreach ((string bot, double m) in saved) {
							Minutes[bot] = m;
						}
					}
				}
			} catch (Exception e) {
				// Remembered, because a read we could not complete must never be written back over. Starting
				// from zero after a failed read and then saving is how a transient problem - a file still being
				// written, a lock, a bad sector - turns into permanent loss.
				_loadFailed = true;
				Log.Warn(new Said("couldn't read the lifetime totals ({0}: {1}) - they will not be overwritten", e.GetType().Name, e.Message));
			}
		}
	}

	/// <summary>Write the totals out.</summary>
	/// <remarks>
	/// Load FIRST, and never write nothing over something.
	///
	/// This is called on shutdown, and it used to serialise whatever was in memory without reading the file
	/// first. Every read path (For, Total, Add) loads on demand, so a run that ended before ANY of them
	/// happened - started and stopped again, or stopped while still signing in - held an empty dictionary and
	/// wrote `{}` straight over months of accumulated hours. One quick restart at the wrong moment and the
	/// number was gone, silently, with the log line about it sitting at Debug where nobody would see it.
	///
	/// The empty-guard is the belt to Load's braces: if there is genuinely nothing to record, the right thing
	/// to do with a file that already has something in it is leave it alone.
	/// </remarks>
	public static void Save() {
		try {
			Load();

			Dictionary<string, double> snapshot;

			lock (Gate) {
				snapshot = new Dictionary<string, double>(Minutes);
			}

			string path = Path;

			if (_loadFailed) {
				Log.Debug("the lifetime totals could not be read this run - not writing over them");

				return;
			}

			if ((snapshot.Count == 0) && File.Exists(path)) {
				Log.Debug("nothing to record - leaving the existing lifetime totals alone");

				return;
			}

			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
		} catch (Exception e) {
			Log.Warn(new Said("couldn't save the lifetime totals: {0}: {1}", e.GetType().Name, e.Message));
		}
	}
}
