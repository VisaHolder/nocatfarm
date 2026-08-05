using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// AppID → the name a person would recognise.
///
/// "playing 590830 for 2h10m" is a log line for a machine. Everything the user reads - the log, the account
/// cards, the weights box - goes through here so it says s&box instead.
///
/// Three layers, cheapest first: a small built-in table of the games idlers actually use, then a cache on disk
/// that grows as it learns, then one lookup against Steam's public store endpoint. Names never change, so a name
/// once learned is kept forever and never asked for again.
/// </summary>
public static class GameNames {
	private static readonly ConcurrentDictionary<uint, string> Known = new();
	private static readonly SemaphoreSlim SaveLock = new(1, 1);
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
	private static bool _loaded;
	private static bool _dirty;

	private static string CachePath => Path.Combine(ConfigStore.ConfigDir, ".appnames.json");

	/// <summary>The ones worth shipping known, so a fresh install reads properly before it has looked anything up.</summary>
	private static readonly Dictionary<uint, string> BuiltIn = new() {
		[10] = "Counter-Strike",
		[20] = "Team Fortress Classic",
		[240] = "Counter-Strike: Source",
		[300] = "Day of Defeat: Source",
		[320] = "Half-Life 2: Deathmatch",
		[440] = "Team Fortress 2",
		[500] = "Left 4 Dead",
		[550] = "Left 4 Dead 2",
		[570] = "Dota 2",
		[730] = "Counter-Strike 2",
		[4000] = "Garry's Mod",
		[8930] = "Sid Meier's Civilization V",
		[72850] = "The Elder Scrolls V: Skyrim",
		[105600] = "Terraria",
		[107410] = "Arma 3",
		[218620] = "PAYDAY 2",
		[221100] = "DayZ",
		[227300] = "Euro Truck Simulator 2",
		[230410] = "Warframe",
		[238960] = "Path of Exile",
		[251570] = "7 Days to Die",
		[252490] = "Rust",
		[271590] = "Grand Theft Auto V",
		[292030] = "The Witcher 3: Wild Hunt",
		[304930] = "Unturned",
		[346110] = "ARK: Survival Evolved",
		[359550] = "Rainbow Six Siege",
		[377160] = "Fallout 4",
		[381210] = "Dead by Daylight",
		[386360] = "SMITE",
		[431960] = "Wallpaper Engine",
		[440900] = "Conan Exiles",
		[578080] = "PUBG: BATTLEGROUNDS",
		[582010] = "Monster Hunter: World",
		[590830] = "s&box",
		[594570] = "Total War: WARHAMMER II",
		[730310] = "Steam Deck",
		[739630] = "Phasmophobia",
		[892970] = "Valheim",
		[945360] = "Among Us",
		[1085660] = "Destiny 2",
		[1091500] = "Cyberpunk 2077",
		[1172470] = "Apex Legends",
		[1174180] = "Red Dead Redemption 2",
		[1203220] = "NARAKA: BLADEPOINT",
		[1245620] = "ELDEN RING",
		[1449850] = "Yu-Gi-Oh! Master Duel",
		[1517290] = "Battlefield 2042",
		[1599340] = "Lost Ark",
		[1623730] = "Palworld",
		[2073850] = "THE FINALS",
		[2246340] = "Monster Hunter Wilds",
		[2767030] = "Marvel Rivals"
	};

	/// <summary>The name if we have one, otherwise "app 730" - never null, never blank, never throws.</summary>
	public static string Of(uint appId) {
		if (appId == 0) {
			return "nothing";
		}

		Load();

		if (Known.TryGetValue(appId, out string? name) && (name.Length > 0)) {
			return name;
		}

		return "app " + appId.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>True once we know the real name, rather than falling back to the appID.</summary>
	public static bool IsKnown(uint appId) {
		Load();

		return Known.TryGetValue(appId, out string? name) && (name.Length > 0);
	}

	/// <summary>Record a name we got for free - a badge page, a licence, anything that already knew it.</summary>
	public static void Learn(uint appId, string? name) {
		if ((appId == 0) || string.IsNullOrWhiteSpace(name)) {
			return;
		}

		name = name.Trim();

		// Card farming falls back to the appID as the "name" when a page won't parse; don't cache that as truth.
		if (uint.TryParse(name, out _)) {
			return;
		}

		Load();

		if (Known.TryGetValue(appId, out string? had) && (had == name)) {
			return;
		}

		Known[appId] = name;
		_dirty = true;
	}

	/// <summary>
	/// Fill in whatever we don't already know, from Steam's public store endpoint. Best effort: this is only ever
	/// cosmetic, so a failure is silent and simply leaves "app 730" showing.
	/// </summary>
	public static async Task ResolveAsync(IEnumerable<uint> appIds, CancellationToken ct = default) {
		Load();
		List<uint> missing = appIds.Distinct().Where(static id => (id != 0) && !Known.ContainsKey(id)).Take(40).ToList();

		if (missing.Count == 0) {
			return;
		}

		foreach (uint appId in missing) {
			if (ct.IsCancellationRequested) {
				break;
			}

			try {
				string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&l=english";
				using HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					continue;
				}

				using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

				if (doc.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out JsonElement entry)
					&& entry.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
					&& entry.TryGetProperty("data", out JsonElement data)
					&& data.TryGetProperty("name", out JsonElement name)) {
					Learn(appId, name.GetString());
				} else {
					// A delisted or private app answers success:false forever. Remember that so we stop asking.
					Known[appId] = "";
					_dirty = true;
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't look up the name of app {0}: {1}", appId, e.Message));
			}

			await Task.Delay(250, ct).ConfigureAwait(false);
		}

		await SaveAsync().ConfigureAwait(false);
	}

	private static void Load() {
		if (_loaded) {
			return;
		}

		_loaded = true;

		foreach ((uint appId, string name) in BuiltIn) {
			Known.TryAdd(appId, name);
		}

		try {
			if (!File.Exists(CachePath)) {
				return;
			}

			Dictionary<string, string>? cached = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));

			if (cached == null) {
				return;
			}

			foreach ((string key, string name) in cached) {
				if (uint.TryParse(key, out uint appId)) {
					Known[appId] = name;
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the game-name cache: {0}", e.Message));
		}
	}

	private static async Task SaveAsync() {
		if (!_dirty) {
			return;
		}

		await SaveLock.WaitAsync().ConfigureAwait(false);

		try {
			if (!_dirty) {
				return;
			}

			_dirty = false;
			Dictionary<string, string> map = Known.ToDictionary(static kv => kv.Key.ToString(CultureInfo.InvariantCulture), static kv => kv.Value);
			Directory.CreateDirectory(ConfigStore.ConfigDir);
			await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the game-name cache: {0}", e.Message));
		} finally {
			SaveLock.Release();
		}
	}
}
