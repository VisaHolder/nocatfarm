using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm;

/// <summary>
/// What Steam's public store says about a game - specifically whether it is single-player and has achievements -
/// cached to disk so the achievement boost can pick single-player targets without asking the store the same thing
/// over and over. Store categories effectively never change, so an answer is kept for good once we have it.
///
/// This is the one place the app touches store.steampowered.com. It's only ever reached when a boost is set to
/// "all single-player" (off by default), and lookups are throttled and fail-safe: a network blip returns "unknown"
/// and is simply retried later, never cached as a false negative.
/// </summary>
public static class GameCatalog {
	private sealed class Entry {
		public bool Single { get; set; }
		public bool Achievements { get; set; }
	}

	private static readonly Dictionary<uint, Entry> Cache = [];
	private static readonly SemaphoreSlim FileGate = new(1, 1);
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
	private static DateTime _lastCall = DateTime.MinValue;
	private static bool _loaded;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "gamecatalog.json");

	/// <summary>
	/// True = single-player AND has Steam achievements (a boost target). False = not. Null = couldn't find out
	/// (store unreachable / unknown app), so the caller should try again later rather than treating it as "no".
	/// </summary>
	public static async Task<bool?> IsSingleplayerWithAchievementsAsync(uint app, CancellationToken ct) {
		Load();

		lock (Cache) {
			if (Cache.TryGetValue(app, out Entry? cached)) {
				return cached.Single && cached.Achievements;
			}
		}

		// One store call at a time, spaced out - the store rate-limits a burst, and the boost is never in a hurry.
		await FileGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			lock (Cache) {
				if (Cache.TryGetValue(app, out Entry? cached)) {
					return cached.Single && cached.Achievements;   // filled in while we waited for the gate
				}
			}

			TimeSpan since = DateTime.UtcNow - _lastCall;

			if (since < TimeSpan.FromSeconds(1.5)) {
				await Task.Delay(TimeSpan.FromSeconds(1.5) - since, ct).ConfigureAwait(false);
			}

			_lastCall = DateTime.UtcNow;

			string body = await Http.GetStringAsync(
				$"https://store.steampowered.com/api/appdetails?appids={app}&filters=categories", ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(body);

			if (!doc.RootElement.TryGetProperty(app.ToString(), out JsonElement node)
				|| !node.TryGetProperty("success", out JsonElement ok) || !ok.GetBoolean()) {
				return null;
			}

			bool single = false;
			bool ach = false;

			if (node.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("categories", out JsonElement cats)) {
				foreach (JsonElement c in cats.EnumerateArray()) {
					string? desc = c.TryGetProperty("description", out JsonElement d) ? d.GetString() : null;

					if (desc == "Single-player") {
						single = true;
					} else if (desc == "Steam Achievements") {
						ach = true;
					}
				}
			}

			Entry entry = new() { Single = single, Achievements = ach };

			lock (Cache) {
				Cache[app] = entry;
			}

			await SaveAsync().ConfigureAwait(false);

			return single && ach;
		} catch (Exception e) {
			Log.Debug($"store lookup for {app} failed: {e.Message}");

			return null;
		} finally {
			FileGate.Release();
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

			Dictionary<uint, Entry>? saved = JsonSerializer.Deserialize<Dictionary<uint, Entry>>(File.ReadAllText(Path));

			if (saved != null) {
				lock (Cache) {
					foreach ((uint app, Entry e) in saved) {
						Cache[app] = e;
					}
				}
			}
		} catch (Exception e) {
			Log.Debug($"couldn't read the game catalog: {e.Message}");
		}
	}

	private static async Task SaveAsync() {
		try {
			Dictionary<uint, Entry> snapshot;

			lock (Cache) {
				snapshot = new Dictionary<uint, Entry>(Cache);
			}

			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
			await AtomicFile.WriteAsync(Path, JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Debug($"couldn't save the game catalog: {e.Message}");
		}
	}
}
