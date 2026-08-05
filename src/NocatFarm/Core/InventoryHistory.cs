using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// What each account's inventory was worth, over time.
///
/// A total on its own answers "how much have I got"; it takes a second reading to answer the question people
/// actually care about, which is "am I up or down". One point an hour is plenty for that - skins move on the scale
/// of days - and a month of them is a few kilobytes.
///
/// Deliberately tolerant: a missing history simply means no percentage is shown yet, never a wrong one. Nothing
/// here is allowed to make the value itself unavailable.
/// </summary>
public static class InventoryHistory {
	private sealed class Point {
		public long At { get; set; }        // unix seconds
		public decimal Value { get; set; }
	}

	private static readonly Dictionary<string, List<Point>> Points = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Lock Gate = new();
	private static DateTime _lastSave = DateTime.MinValue;
	private static bool _loaded;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "invhistory.json");

	/// <summary>
	/// Record what an inventory is worth now. Cheap to call constantly - it keeps at most one point an hour.
	///
	/// Zero is never recorded: a fresh start, a cleared cache or a failed read all read as zero for a while, and
	/// a zero in the history would show up later as "down 100%" for a day.
	/// </summary>
	public static void Note(string bot, decimal value) {
		if (value <= 0) {
			return;
		}

		Load();

		lock (Gate) {
			if (!Points.TryGetValue(bot, out List<Point>? points)) {
				Points[bot] = points = [];
			}

			DateTime now = DateTime.UtcNow;

			if ((points.Count > 0) && (now - DateTimeOffset.FromUnixTimeSeconds(points[^1].At).UtcDateTime < TimeSpan.FromHours(1))) {
				points[^1].Value = value;   // same hour: keep the latest figure rather than adding a second point

				return;
			}

			points.Add(new Point { At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Value = value });

			// A month is as far back as anybody looks, and it bounds the file.
			long cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
			points.RemoveAll(p => p.At < cutoff);
		}

		if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(5)) {
			_lastSave = DateTime.UtcNow;
			Save();
		}
	}

	/// <summary>
	/// How much this inventory has moved over the last day, as an amount and a percentage. Null until there is a
	/// reading old enough to compare against - a number computed from twenty minutes of history is noise.
	/// </summary>
	public static (decimal Change, double Percent)? Since(string bot, TimeSpan window) {
		Load();

		lock (Gate) {
			if (!Points.TryGetValue(bot, out List<Point>? points) || (points.Count < 2)) {
				return null;
			}

			long want = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();

			// The oldest point that is still no older than the window - or the oldest we have, if the history is
			// younger than the window itself. Comparing against a point from ten minutes ago would report ~0%.
			Point? baseline = points.LastOrDefault(p => p.At <= want) ?? points[0];
			decimal latest = points[^1].Value;

			if ((baseline.Value <= 0) || (points[^1].At - baseline.At < (long) TimeSpan.FromHours(1).TotalSeconds)) {
				return null;
			}

			decimal change = latest - baseline.Value;

			return (change, (double) (change / baseline.Value) * 100);
		}
	}

	private static void Load() {
		if (_loaded) {
			return;
		}

		_loaded = true;

		try {
			if (!File.Exists(Path)) {
				return;
			}

			Dictionary<string, List<Point>>? saved = JsonSerializer.Deserialize<Dictionary<string, List<Point>>>(File.ReadAllText(Path));

			if (saved != null) {
				lock (Gate) {
					foreach ((string bot, List<Point> points) in saved) {
						Points[bot] = points;
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the inventory history: {0}", e.Message));
		}
	}

	public static void Save() {
		try {
			Dictionary<string, List<Point>> snapshot;

			lock (Gate) {
				if (Points.Count == 0) {
					return;
				}

				snapshot = new Dictionary<string, List<Point>>(Points, StringComparer.OrdinalIgnoreCase);
			}

			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
			AtomicFile.Write(Path, JsonSerializer.Serialize(snapshot));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the inventory history: {0}", e.Message));
		}
	}
}
