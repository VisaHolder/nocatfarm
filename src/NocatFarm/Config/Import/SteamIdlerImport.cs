using System.Text.Json;

using NocatFarm.Core;

using static NocatFarm.Config.ImportFiles;

namespace NocatFarm.Config;

/// <summary>
/// 3urobeat's steam-idler: accounts.txt, one "login:password:shared_secret" per line (the shared secret is optional,
/// a first line starting // is a comment), and config.json with the games it plays.
/// </summary>
public sealed class SteamIdlerImport : IIdlerImporter {
	public string Id => "steamidler";
	public string Name => "steam-idler";

	public IEnumerable<string> Places() => SearchUsual(d => Looks(d));

	public string? Resolve(string path) {
		string dir = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;

		return File.Exists(Path.Combine(dir, "accounts.txt")) && (Lines(dir).Any() || Looks(dir)) ? dir : null;
	}

	/// <summary>Its folder: accounts.txt beside a config.json with playingGames (or its own package.json).</summary>
	private static bool Looks(string dir) =>
		File.Exists(Path.Combine(dir, "accounts.txt"))
		&& ((Prop(Json(Path.Combine(dir, "config.json")), "playingGames").ValueKind != JsonValueKind.Undefined)
			|| (Str(Json(Path.Combine(dir, "package.json")), "name") is "steam-idler"));

	/// <summary>The account lines, comments and blanks left out.</summary>
	private static IEnumerable<string> Lines(string dir) =>
		(Text(Path.Combine(dir, "accounts.txt")) ?? "")
			.Split('\n')
			.Select(static l => l.Trim())
			.Where(static l => (l.Length > 0) && !l.StartsWith("//", StringComparison.Ordinal) && (l.IndexOf(':', StringComparison.Ordinal) > 0));

	public ImportScan Read(string resolved) {
		ImportScan scan = new() { Tool = Id, ToolName = Name, Path = resolved };
		JsonElement config = Json(Path.Combine(resolved, "config.json"));
		JsonElement playing = Prop(config, "playingGames");
		HashSet<string> taken = [];

		foreach (string line in Lines(resolved)) {
			string[] parts = line.Split(':');

			// "user:pass:" - an empty secret on the end. Kept, the colon went into the password: a failed logon.
			if ((parts.Length >= 3) && string.IsNullOrWhiteSpace(parts[^1])) {
				parts = parts[..^1];
			}

			string login = parts[0].Trim();
			string password;
			string shared = "";

			// A password may itself hold a colon; a shared secret never does and is always 28 characters of base64.
			if ((parts.Length >= 3) && LooksLikeSecret(parts[^1])) {
				password = string.Join(':', parts[1..^1]);
				shared = parts[^1].Trim();
			} else {
				password = string.Join(':', parts[1..]);
			}

			if (login.Length == 0) {
				continue;
			}

			BotConfig bot = new() { SteamLogin = login, SteamPassword = password, SharedSecret = shared };

			// The games: one list for everyone, or a list per account. A string in it is the custom status it shows.
			JsonElement games = playing.ValueKind == JsonValueKind.Object ? Prop(playing, login) : playing;

			if (games.ValueKind == JsonValueKind.Array) {
				List<uint> apps = Apps(games);

				if (apps.Count > Core.SteamIds.GamesAtOnce) {
					scan.Notes.Add(new Said("{0} games were listed to idle, but Steam plays {1} at once - the first {1} came across", apps.Count, Core.SteamIds.GamesAtOnce));
				}

				bot.IdleGames = apps.Take(Core.SteamIds.GamesAtOnce).ToList();

				if (games.EnumerateArray().FirstOrDefault(static g => (g.ValueKind == JsonValueKind.String) && !uint.TryParse(g.GetString(), out _)) is { ValueKind: JsonValueKind.String } name
					&& (name.GetString() is { Length: > 0 } custom)) {
					bot.CustomGameName = custom;
				}
			}

			if ((Int(config, "onlinestatus") is int status) && (status is >= 0 and <= 7)) {
				bot.OnlineStatus = status;
			}

			scan.Accounts.Add(new ImportedAccount { Key = login, Name = NameFor(login, "idler", taken), SteamLogin = login, Config = bot });
		}

		return scan;
	}

	private static bool LooksLikeSecret(string value) {
		string v = value.Trim();

		if ((v.Length != 28) || !v.EndsWith('=')) {
			return false;
		}

		try {
			return Convert.FromBase64String(v).Length == 20;
		} catch (FormatException) {
			return false;
		}
	}
}
