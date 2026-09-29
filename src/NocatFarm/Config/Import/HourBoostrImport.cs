using System.Text.Json;

using NocatFarm.Core;

using static NocatFarm.Config.ImportFiles;

namespace NocatFarm.Config;

/// <summary>
/// HourBoostr: a Settings.json beside its exe with every account, its password and the games it boosts.
///
/// Its LoginKey (and the sentry files beside it) belong to a way of signing in that Steam switched off, so they are
/// left where they are: the password comes over, and Steam Guard is asked for once.
/// </summary>
public sealed class HourBoostrImport : IIdlerImporter {
	public string Id => "hourboostr";
	public string Name => "HourBoostr";

	public IEnumerable<string> Places() => SearchUsual(d => Resolve(d) != null);

	public string? Resolve(string path) {
		string file = Directory.Exists(path) ? Path.Combine(path, "Settings.json") : path;

		return IsHourBoostr(Json(file)) ? file : null;
	}

	/// <summary>{"Accounts":[{"Details":{"Username":…}}]} - the Details object is what tells it from other Settings.json files.</summary>
	private static bool IsHourBoostr(JsonElement root) =>
		(Prop(root, "Accounts") is { ValueKind: JsonValueKind.Array } accounts)
		&& accounts.EnumerateArray().Any(static a => Prop(a, "Details").ValueKind == JsonValueKind.Object);

	public ImportScan Read(string resolved) {
		ImportScan scan = new() { Tool = Id, ToolName = Name, Path = resolved };
		HashSet<string> taken = [];
		bool loginKeys = false;

		foreach (JsonElement entry in Prop(Json(resolved), "Accounts").EnumerateArray()) {
			JsonElement details = Prop(entry, "Details");

			if (Str(details, "Username") is not { Length: > 0 } login) {
				continue;
			}

			loginKeys |= !string.IsNullOrEmpty(Str(details, "LoginKey"));

			List<uint> games = Apps(Prop(entry, "Games"));
			BotConfig bot = new() {
				SteamLogin = login,
				SteamPassword = Str(details, "Password") ?? "",
				IdleGames = games.Take(Core.SteamIds.GamesAtOnce).ToList()
			};

			ImportedAccount account = new() { Key = login, Name = NameFor(login, "hourboostr", taken), SteamLogin = login, Config = bot };

			if (games.Count > Core.SteamIds.GamesAtOnce) {
				account.Notes.Add(new Said("{0} games were listed to idle, but Steam plays {1} at once - the first {1} came across", games.Count, Core.SteamIds.GamesAtOnce));
			}

			// It boosted with its status hidden: the nearest thing here is invisible, which still plays.
			if (Bool(entry, "ShowOnlineStatus") == false) {
				bot.OnlineStatus = 7;
			}

			if (Bool(entry, "IgnoreAccount") == true) {
				bot.Enabled = false;
			}

			scan.Accounts.Add(account);
		}

		if (loginKeys) {
			scan.Notes.Add(new Said("HourBoostr's saved login keys no longer work on Steam, so they were left behind - each account asks for a Steam Guard code once"));
		}

		return scan;
	}
}
