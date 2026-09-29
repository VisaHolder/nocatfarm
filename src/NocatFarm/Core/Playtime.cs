using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// How long a game really takes a typical player - so the achievement hunter never earns "beat the game" three hours
/// into a twenty-hour game, and a game's rare tail opens at the pace of that game rather than one scale for all.
/// </summary>
/// <remarks>
/// From SteamSpy's public figures for each game (median and average playtime of its owners). Only the game's ID is
/// sent. Each answer is kept for a month in config/state/playtime.json, and they are asked one at a time, a couple of
/// seconds apart - it's a small free service. No answer means "don't know", and pacing falls back to what the
/// achievements' own rarity says.
/// </remarks>
public static class Playtime {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
	private static readonly Lock Gate = new();
	private static readonly HashSet<uint> Wanted = [];
	private static Dictionary<uint, Entry>? _cache;
	private static int _fetching;

	static Playtime() {
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	private sealed record Entry(double Hours, long At);

	private static string CachePath => Path.Combine(ConfigStore.ConfigDir, "state", "playtime.json");

	/// <summary>
	/// A typical player's hours in <paramref name="app"/>, or null when it isn't known (yet). Asking is what gets
	/// it looked up - the first ask answers null and the figure is there a few seconds later.
	/// </summary>
	public static double? TypicalHours(uint app) {
		lock (Gate) {
			Load();

			bool known = _cache!.TryGetValue(app, out Entry? e);

			// A real answer is good for a month; a failed look (offline, a timeout, SteamSpy busy) for an hour.
			if (!known || (DateTime.UtcNow - new DateTime(e!.At, DateTimeKind.Utc) > (e.Hours < 0 ? TimeSpan.FromHours(1) : TimeSpan.FromDays(30)))) {
				Wanted.Add(app);
			}

			if (Wanted.Count > 0) {
				Kick();
			}

			return known && (e!.Hours > 0) ? e.Hours : null;
		}
	}

	/// <summary>
	/// The figure from SteamSpy's numbers: the median player, or 60% of the average when that's higher - the median
	/// alone undercounts story games (plenty of owners barely start them), the average alone overcounts (idlers).
	/// </summary>
	internal static double FromMinutes(long median, long average) {
		double hours = Math.Max(median, average * 0.6) / 60.0;

		return hours <= 0 ? 0 : Math.Clamp(hours, 0.5, 400);
	}

	private static void Kick() {
		if (Interlocked.Exchange(ref _fetching, 1) == 1) {
			return;
		}

		_ = Task.Run(async () => {
			try {
				while (true) {
					uint app;

					lock (Gate) {
						if (Wanted.Count == 0) {
							return;
						}

						app = Wanted.First();
						Wanted.Remove(app);
					}

					double hours = await FetchAsync(app).ConfigureAwait(false);

					lock (Gate) {
						_cache![app] = new Entry(hours, DateTime.UtcNow.Ticks);
						Save();
					}

					await Task.Delay(2000).ConfigureAwait(false);
				}
			} catch (Exception e) {
				// The next ask starts it again, but the ones still waiting are dropped until then.
				Log.Failed("the playtime lookups stopped", e);
				Log.StackToFile(e);
			} finally {
				Volatile.Write(ref _fetching, 0);
			}
		});
	}

	/// <summary>The hours, 0 when SteamSpy has no figure for it, -1 when it couldn't be asked.</summary>
	private static async Task<double> FetchAsync(uint app) {
		try {
			string json = await Http.GetStringAsync(new Uri($"https://steamspy.com/api.php?request=appdetails&appid={app.ToString(CultureInfo.InvariantCulture)}")).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(json);

			return FromMinutes(Number(doc.RootElement, "median_forever"), Number(doc.RootElement, "average_forever"));
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) {
			Log.Debug(new Said("couldn't look up how long {0} takes: {1}", app, Log.Describe(e)));

			return -1;
		}
	}

	private static long Number(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement v) && v.TryGetInt64(out long n) ? n : 0;

	private static void Load() {
		if (_cache != null) {
			return;
		}

		try {
			_cache = File.Exists(CachePath) ? JsonSerializer.Deserialize<Dictionary<uint, Entry>>(File.ReadAllText(CachePath)) ?? [] : [];
		} catch (Exception e) {
			Log.Failed("couldn't read the playtime cache - asking again", e);
			_cache = [];
		}
	}

	private static void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
			AtomicFile.Write(CachePath, JsonSerializer.Serialize(_cache));
		} catch (Exception e) {
			// asked again next start
			Log.Failed("couldn't save the playtime cache", e);
		}
	}
}
