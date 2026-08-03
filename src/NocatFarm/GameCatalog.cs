using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm;

/// <summary>
/// What Steam's public store says about a game, cached to disk so the achievement hunter can choose targets
/// without asking the store the same thing over and over. Store facts effectively never change, so an answer is
/// kept for good once we have it.
///
/// Four things are worth knowing about a candidate:
///   - is it a GAME at all (not DLC, a demo, a soundtrack, a tool or a video),
///   - is it single-player,
///   - does it have achievements, and
///   - do real people play it - the review count, which is how bundle filler is told from a game.
///
/// This is the one place the app touches store.steampowered.com. It's only ever reached when the achievement
/// hunter is on (off by default), and lookups are throttled and fail-safe: a network blip returns "unknown" and is
/// simply retried later, never cached as a false negative.
/// </summary>
public static class GameCatalog {
	/// <summary>Bumped when a new fact is added, so older cached entries are re-fetched instead of trusted.</summary>
	private const int CurrentVersion = 2;

	private sealed class Entry {
		public int V { get; set; }
		public bool Game { get; set; }
		public bool Single { get; set; }
		public bool Achievements { get; set; }
		public int Reviews { get; set; }
	}

	/// <summary>Everything the hunter wants to know about one app. Null anywhere means "ask again later".</summary>
	public sealed record Facts(bool IsGame, bool Single, bool Achievements, int Reviews);

	private static readonly Dictionary<uint, Entry> Cache = [];
	private static readonly SemaphoreSlim FileGate = new(1, 1);
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
	private static DateTime _lastCall = DateTime.MinValue;
	private static DateTime _lastSave = DateTime.MinValue;
	private static bool _loaded;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "gamecatalog.json");

	/// <summary>
	/// True = a single-player game with achievements that people actually play. False = not a target. Null =
	/// couldn't find out (store unreachable / unknown app), so the caller should try again later rather than
	/// treating it as "no".
	/// </summary>
	public static async Task<bool?> IsHuntableAsync(uint app, int minReviews, CancellationToken ct) {
		Facts? facts = await LookUpAsync(app, ct).ConfigureAwait(false);

		if (facts == null) {
			return null;
		}

		return facts.IsGame && facts.Single && facts.Achievements && (facts.Reviews >= Math.Max(0, minReviews));
	}

	/// <summary>
	/// What we already know about one app, without asking the store. Null means "not looked up yet".
	///
	/// For anything that must answer NOW - a command someone typed - because a cold cache would otherwise turn one
	/// question into a several-minute throttled sweep of the whole library.
	/// </summary>
	public static Facts? Known(uint app) {
		Load();

		lock (Cache) {
			return Cache.TryGetValue(app, out Entry? e) && (e.V >= CurrentVersion)
				? new Facts(e.Game, e.Single, e.Achievements, e.Reviews)
				: null;
		}
	}

	/// <summary>Everything known about one app, or null if the store wouldn't say.</summary>
	public static async Task<Facts?> LookUpAsync(uint app, CancellationToken ct) {
		Load();

		lock (Cache) {
			if (Cache.TryGetValue(app, out Entry? cached) && (cached.V >= CurrentVersion)) {
				return new Facts(cached.Game, cached.Single, cached.Achievements, cached.Reviews);
			}
		}

		// One store call at a time, spaced out - the store rate-limits a burst, and the hunter is never in a hurry.
		await FileGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			lock (Cache) {
				if (Cache.TryGetValue(app, out Entry? cached) && (cached.V >= CurrentVersion)) {
					return new Facts(cached.Game, cached.Single, cached.Achievements, cached.Reviews);   // filled in while we waited
				}
			}

			TimeSpan since = DateTime.UtcNow - _lastCall;

			if (since < TimeSpan.FromSeconds(1.5)) {
				await Task.Delay(TimeSpan.FromSeconds(1.5) - since, ct).ConfigureAwait(false);
			}

			_lastCall = DateTime.UtcNow;

			string body = await Http.GetStringAsync(
				$"https://store.steampowered.com/api/appdetails?appids={app}&filters=basic,categories,recommendations", ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(body);

			if (!doc.RootElement.TryGetProperty(app.ToString(), out JsonElement node)
				|| !node.TryGetProperty("success", out JsonElement ok) || !ok.GetBoolean()
				|| !node.TryGetProperty("data", out JsonElement data)) {
				return null;
			}

			Entry entry = new() {
				V = CurrentVersion,

				// "game" excludes dlc, demo, music, video, series, hardware and tool. An account playing a
				// soundtrack or a DLC entry is the kind of thing nobody does by accident twice.
				Game = data.TryGetProperty("type", out JsonElement type) && (type.GetString() == "game"),
				Reviews = data.TryGetProperty("recommendations", out JsonElement recs)
					&& recs.TryGetProperty("total", out JsonElement total) && total.TryGetInt32(out int n) ? n : 0
			};

			if (data.TryGetProperty("categories", out JsonElement cats) && (cats.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement c in cats.EnumerateArray()) {
					string? desc = c.TryGetProperty("description", out JsonElement d) ? d.GetString() : null;

					if (desc == "Single-player") {
						entry.Single = true;
					} else if (desc == "Steam Achievements") {
						entry.Achievements = true;
					}
				}
			}

			lock (Cache) {
				Cache[app] = entry;
			}

			// Written every so often rather than after every lookup: a family library is over a thousand games, and
			// re-serialising the whole catalogue thirteen hundred times to add one line each is pure disk churn.
			// Nothing is lost by waiting - an unsaved entry is simply looked up again next time.
			if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(30)) {
				_lastSave = DateTime.UtcNow;
				await SaveAsync().ConfigureAwait(false);
			}

			return new Facts(entry.Game, entry.Single, entry.Achievements, entry.Reviews);
		} catch (OperationCanceledException) {
			throw;
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

	/// <summary>Write out anything the throttled save hasn't got to yet. Called on the way out.</summary>
	public static void Flush() {
		lock (Cache) {
			if (Cache.Count == 0) {
				return;
			}
		}

		SaveAsync().GetAwaiter().GetResult();
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
