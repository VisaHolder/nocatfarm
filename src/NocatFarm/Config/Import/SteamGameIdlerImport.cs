using System.Globalization;
using System.Text.Json;

using NocatFarm.Core;

using static NocatFarm.Config.ImportFiles;

namespace NocatFarm.Config;

/// <summary>
/// Steam Game Idler (6.x): a cache folder with settings.json listing its accounts, and a folder per account (by SteamID64)
/// with what it idles, its card-farming blacklist and choices, playtime caps and the custom status.
///
/// Its sign-ins are not in those files but in the Windows Credential Manager. They're only read for an account whose
/// "bring its sign-in over" box was ticked in the preview; otherwise the account asks for its password (or a QR code).
/// </summary>
public sealed class SteamGameIdlerImport : IIdlerImporter {
	public string Id => "sgi";
	public string Name => "Steam Game Idler";

	private const string AppFolder = "com.zevnda.steam-game-idler";

	public IEnumerable<string> Places() {
		List<string> places = [];

		if (ImportPlaces.AppData.Length > 0) {
			places.Add(Path.Combine(ImportPlaces.AppData, AppFolder, "cache"));
		}

		// The portable build keeps "cache" beside its exe.
		places.AddRange(SearchUsual(d => File.Exists(Path.Combine(d, "cache", "settings.json")) && (Resolve(d) != null)));

		return places;
	}

	public string? Resolve(string path) {
		string[] tries = File.Exists(path)
			? [Path.GetDirectoryName(path) ?? path]
			: [path, Path.Combine(path, "cache"), Path.Combine(path, AppFolder, "cache")];

		return tries.FirstOrDefault(static d => Prop(Json(Path.Combine(d, "settings.json")), "agentAccounts").ValueKind == JsonValueKind.Object);
	}

	public ImportScan Read(string resolved) {
		ImportScan scan = new() { Tool = Id, ToolName = Name, Path = resolved };
		JsonElement settings = Json(Path.Combine(resolved, "settings.json"));
		JsonElement ids = Prop(settings, "agentAccountSteamIds");
		HashSet<string> taken = [];

		foreach (JsonProperty entry in Prop(settings, "agentAccounts").EnumerateObject()) {
			string lower = entry.Name;
			string login = entry.Value.ValueKind == JsonValueKind.String && (entry.Value.GetString() is { Length: > 0 } shown) ? shown : lower;
			string steamId = Str(ids, lower) ?? "";
			BotConfig bot = new() { SteamLogin = login };
			ImportedAccount account = new() {
				Key = lower,
				Name = NameFor(login, "sgi", taken),
				SteamLogin = login,
				SteamId = steamId,
				Config = bot,
				CredentialTarget = OperatingSystem.IsWindows() ? lower.ToLowerInvariant() + "." + AppFolder + ".agent" : null
			};

			if (IsSteamId(steamId)) {
				ReadAccount(Path.Combine(resolved, steamId), bot, account);
			}

			scan.Accounts.Add(account);
		}

		return scan;
	}

	private static void ReadAccount(string dir, BotConfig bot, ImportedAccount account) {
		// What it idles: only the games switched on in its list.
		List<uint> idle = [];

		foreach (JsonElement game in Prop(Json(Path.Combine(dir, "auto_idle.json")), "games").EnumerateArrayOrEmpty()) {
			if ((Bool(game, "enabled") != false) && (AppOf(Prop(game, "appId")) is uint app) && !idle.Contains(app)) {
				idle.Add(app);
			}
		}

		if (idle.Count > Core.SteamIds.GamesAtOnce) {
			account.Notes.Add(new Said("{0} games were listed to idle, but Steam plays {1} at once - the first {1} came across", idle.Count, Core.SteamIds.GamesAtOnce));
		}

		bot.IdleGames = idle.Take(Core.SteamIds.GamesAtOnce).ToList();

		foreach (JsonElement game in Prop(Json(Path.Combine(dir, "card_farming_blacklist.json")), "blacklist").EnumerateArrayOrEmpty()) {
			if ((AppOf(game.ValueKind == JsonValueKind.Object ? Prop(game, "appId") : game) is uint app) && !bot.BlacklistedGames.Contains(app)) {
				bot.BlacklistedGames.Add(app);
			}
		}

		JsonElement farming = Json(Path.Combine(dir, "card_farming_settings.json"));

		if ((Find(farming, "hoursUntilFarmable") is { ValueKind: JsonValueKind.Number } hours) && hours.TryGetDouble(out double h) && (h >= 0)) {
			bot.HoursUntilCardDrops = (float) Math.Min(100, h);
		}

		if (Find(farming, "skipNoPlaytime") is { ValueKind: JsonValueKind.True or JsonValueKind.False } skipUnplayed) {
			bot.SkipUnplayedGames = skipUnplayed.GetBoolean();
		}

		if (Find(farming, "skipRefundableGames") is { ValueKind: JsonValueKind.True or JsonValueKind.False } skipRefundable) {
			bot.SkipRefundableGames = skipRefundable.GetBoolean();
		}

		// Its playtime caps ("stop at N minutes") are the nearest thing to our hour targets: play it up to that many hours.
		if (Find(Json(Path.Combine(dir, "max_playtime_settings.json")), "per_game_max_playtime") is { ValueKind: JsonValueKind.Object } caps) {
			List<string> targets = [];

			foreach (JsonProperty cap in caps.EnumerateObject()) {
				if (uint.TryParse(cap.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app > 0)
					&& (cap.Value.ValueKind == JsonValueKind.Number) && cap.Value.TryGetDouble(out double minutes) && (minutes > 0)) {
					targets.Add(app.ToString(CultureInfo.InvariantCulture) + ":" + Math.Max(1, (int) Math.Ceiling(minutes / 60)).ToString(CultureInfo.InvariantCulture));
				}
			}

			bot.HourTargets = string.Join(", ", targets);
		}

		if (Find(Json(Path.Combine(dir, "presence_settings.json")), "customIdleStatus") is { ValueKind: JsonValueKind.String } status && (status.GetString() is { Length: > 0 } custom)) {
			bot.CustomGameName = custom;
			bot.CustomGameNameEnabled = true;
		}
	}
}

internal static class JsonArrays {
	/// <summary>The items of an array, or none when it isn't one.</summary>
	public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement e) =>
		e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
}
