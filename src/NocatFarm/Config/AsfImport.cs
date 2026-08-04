using System.Globalization;
using System.Text.Json;

namespace NocatFarm.Config;

/// <summary>
/// Bring accounts across from an ArchiSteamFarm install.
///
/// The valuable part isn't the settings, it's <c>&lt;bot&gt;.db</c>: ASF stores the Steam REFRESH TOKEN there, and
/// that is the same kind of token nocatFarm uses. Copying it across means an imported account logs straight in
/// with no password and no Steam Guard code - which is the whole point of importing rather than retyping.
///
/// Settings from ASF itself and from the HumanIdler plugin are both understood, because on a real install the
/// interesting bits (which games to idle, the custom game name, whether rep4rep is on) live in the plugin keys.
/// </summary>
public static class AsfImport {
	public sealed record Candidate(string Name, string SteamLogin, bool HasToken, bool HasPassword);

	public sealed record Result(int Imported, int Skipped, List<string> Notes);

	/// <summary>Find an ASF config folder near the usual places. Null when there isn't one.</summary>
	public static string? Detect() {
		List<string> candidates = [];

		try {
			string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			string root = ConfigStore.Root;

			candidates.Add(Path.Combine(root, "..", "config"));
			candidates.Add(Path.Combine(root, "..", "..", "arch", "config"));
			candidates.Add(Path.Combine(desktop, "arch", "config"));
			candidates.Add(Path.Combine(desktop, "ArchiSteamFarm", "config"));
			candidates.Add(Path.Combine(desktop, "ASF", "config"));
		} catch {
			// a locked-down profile - the explicit path argument still works
		}

		foreach (string candidate in candidates) {
			try {
				string full = Path.GetFullPath(candidate);

				// ASF.json is the marker; a bare "config" folder could be anything.
				if (File.Exists(Path.Combine(full, "ASF.json")) && !string.Equals(full, ConfigStore.ConfigDir, StringComparison.OrdinalIgnoreCase)) {
					return full;
				}
			} catch {
				// unusable path, try the next
			}
		}

		return null;
	}

	/// <summary>What would be imported from <paramref name="dir"/>, without importing anything.</summary>
	public static List<Candidate> Preview(string dir) {
		List<Candidate> found = [];

		foreach (string file in SafeFiles(dir)) {
			string name = Path.GetFileNameWithoutExtension(file);

			if (!IsBotFile(name)) {
				continue;
			}

			JsonElement cfg;

			try {
				cfg = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
			} catch {
				continue;
			}

			found.Add(new Candidate(
				name,
				Str(cfg, "SteamLogin") ?? name,
				ReadRefreshToken(dir, name) != null,
				!string.IsNullOrEmpty(Str(cfg, "SteamPassword"))));
		}

		return found;
	}

	/// <summary>
	/// Import every bot from an ASF config folder. Existing nocatFarm accounts of the same name are left alone
	/// unless <paramref name="overwrite"/> is set - importing twice should not clobber settings you've changed.
	/// </summary>
	public static Result Run(string dir, GlobalConfig global, bool overwrite = false) {
		List<string> notes = [];
		int imported = 0;
		int skipped = 0;

		Dictionary<string, BotConfig> existing = ConfigStore.LoadBots();

		foreach (string file in SafeFiles(dir)) {
			string name = Path.GetFileNameWithoutExtension(file);

			if (!IsBotFile(name)) {
				continue;
			}

			if (!ConfigStore.IsValidBotName(name)) {
				notes.Add($"{name}: skipped, that name can't be used as a config file");
				skipped++;

				continue;
			}

			if (existing.ContainsKey(name) && !overwrite) {
				notes.Add($"{name}: already exists here, left alone");
				skipped++;

				continue;
			}

			JsonElement cfg;

			try {
				cfg = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
			} catch (Exception e) {
				notes.Add($"{name}: couldn't read it ({e.Message})");
				skipped++;

				continue;
			}

			BotConfig bot = Translate(cfg, name, global, notes);
			ConfigStore.SaveBot(name, bot);

			// The token is the reason to do this at all.
			string? token = ReadRefreshToken(dir, name);

			if (token != null) {
				Core.TokenStore.Save(name, token);
				notes.Add($"{name}: imported with its login token - no password needed");
			} else if (!string.IsNullOrEmpty(bot.SteamPassword)) {
				notes.Add($"{name}: imported with its password - it'll ask for a Steam Guard code once");
			} else {
				notes.Add($"{name}: imported, but it will ask for the password on first login");
			}

			// If ASF was holding this account's mobile authenticator, bring it too. Without it an account with 2FA
			// on stops and asks for a code on every fresh login, which defeats the point of an unattended farmer.
			if (CopyAuthenticator(dir, name)) {
				notes.Add($"{name}: brought its mobile authenticator across - it answers its own Steam Guard now");
			}

			imported++;
		}

		return new Result(imported, skipped, notes);
	}

	/// <summary>Copy an ASF bot's maFile into config/authenticators/, leaving the original exactly where it was.</summary>
	private static bool CopyAuthenticator(string dir, string name) {
		try {
			string source = Path.Combine(dir, name + ".maFile");

			if (!File.Exists(source)) {
				return false;
			}

			(string? shared, _, _) = Core.MobileAuth.ReadMaFile(source);

			if (string.IsNullOrWhiteSpace(shared)) {
				return false;   // a file we can't read is worse than no file - it would look like 2FA was handled
			}

			Directory.CreateDirectory(Core.MaFiles.Dir);
			string target = Core.MaFiles.PathFor(name);

			// Never overwrite one that is already here: the one in place is the one that has been working.
			if (File.Exists(target)) {
				return false;
			}

			File.Copy(source, target);

			return true;
		} catch (Exception e) {
			Log.Debug($"couldn't bring {name}'s authenticator across: {e.Message}");

			return false;
		}
	}

	// ── translation ─────────────────────────────────────────────────────────
	private static BotConfig Translate(JsonElement cfg, string name, GlobalConfig global, List<string> notes) {
		BotConfig bot = new() {
			Enabled = Bool(cfg, "Enabled") ?? true,
			SteamLogin = Str(cfg, "SteamLogin") ?? name,
			SteamPassword = Str(cfg, "SteamPassword") ?? "",
			SteamParentalCode = Str(cfg, "SteamParentalCode") ?? ""
		};

		// ASF writes "{0}" here as a template placeholder, which is not a device name.
		string? machine = Str(cfg, "MachineName");

		if (!string.IsNullOrEmpty(machine) && !machine.Contains('{', StringComparison.Ordinal)) {
			bot.MachineName = machine;
		}

		if (Int(cfg, "OnlineStatus") is int persona) {
			bot.OnlineStatus = persona;   // ASF uses the same EPersonaState numbering
		}

		if (Num(cfg, "HoursUntilCardDrops") is double hours) {
			bot.HoursUntilCardDrops = (float) hours;
		}

		if (Int(cfg, "FarmingOrder") is int order) {
			bot.FarmingOrder = MapFarmingOrder(order);
		}

		bot.IdleGames = Apps(cfg, "GamesPlayedWhileIdle");
		bot.CustomGameName = Str(cfg, "CustomGamePlayedWhileIdle") ?? "";

		// HumanIdler plugin keys. On a real install this is where the interesting settings actually live.
		if (Apps(cfg, "HumanIdlerGames") is { Count: > 0 } plugin) {
			bot.IdleGames = plugin;
		}

		if (Str(cfg, "HumanIdlerCustomGame") is { Length: > 0 } custom) {
			bot.CustomGameName = custom;
		}

		if (bot.IdleGames.Count == 0 && Apps(cfg, "HumanIdlerCustomGameApps") is { Count: > 0 } customApps) {
			bot.IdleGames = customApps;
		}

		if (Bool(cfg, "HumanIdlerRep4Rep") == true) {
			bot.Rep4Rep = true;
		}

		// One rep4rep token covers the whole account, so it belongs in the global config.
		if (Str(cfg, "HumanIdlerRep4RepToken") is { Length: > 0 } r4rToken && string.IsNullOrEmpty(global.Rep4RepApiToken)) {
			global.Rep4RepApiToken = r4rToken;
			ConfigStore.SaveGlobal(global);
			notes.Add($"{name}: brought its rep4rep API token across too");
		}

		if (Apps(cfg, "IdlePriorityQueue") is { Count: > 0 } priority) {
			bot.PriorityGames = priority;
		}

		TranslateFarming(cfg, bot);
		TranslateBehaviour(cfg, bot);
		TranslateTrading(cfg, bot);

		return bot;
	}

	/// <summary>ASF packs its farming switches into one bitfield. Unpack it into the individual settings here.</summary>
	private static void TranslateFarming(JsonElement cfg, BotConfig bot) {
		if (Int(cfg, "FarmingPreferences") is not int flags) {
			return;
		}

		bot.StopWhenFarmingDone = (flags & 1) != 0;      // ShutdownOnFarmingFinished
		bot.FarmPriorityOnly = (flags & 4) != 0;         // FarmPriorityQueueOnly
		bot.SkipRefundableGames = (flags & 8) != 0;      // SkipRefundableGames
		bot.FarmOffline = (flags & 16) != 0;             // FarmOffline
	}

	/// <summary>
	/// The same again for BotBehaviour. Two of these are inverted: ASF's flags say what to switch OFF, so
	/// "RejectInvalidFriendInvites" set means don't accept them, and the absence of a flag is the permissive case.
	/// </summary>
	private static void TranslateBehaviour(JsonElement cfg, BotConfig bot) {
		if (Int(cfg, "BotBehaviour") is not int flags) {
			return;
		}

		bot.RejectInvalidFriendInvites = (flags & 1) != 0;
		bot.AcceptGroupInvites = (flags & 2) == 0;
		bot.IgnoreSuspiciousInvites = (flags & 1) != 0;
	}

	/// <summary>
	/// TradingPreferences, plus whoever ASF was set to trust.
	///
	/// ASF's SteamUserPermissions map is where the accounts allowed to take items live - anyone at Master level
	/// or above. That is exactly what our trade-masters list means, so it comes straight across.
	/// </summary>
	private static void TranslateTrading(JsonElement cfg, BotConfig bot) {
		if (Int(cfg, "TradingPreferences") is int flags) {
			bot.AcceptDonations = (flags & 1) != 0;   // AcceptDonations
		}

		List<string> masters = [];

		if (cfg.TryGetProperty("SteamUserPermissions", out JsonElement permissions) && (permissions.ValueKind == JsonValueKind.Object)) {
			foreach (JsonProperty entry in permissions.EnumerateObject()) {
				// 1 FamilySharing · 2 Operator · 3 Master · 4 Owner. Master and up may take items.
				if ((entry.Value.ValueKind == JsonValueKind.Number) && (entry.Value.GetInt32() >= 3) && ulong.TryParse(entry.Name, out ulong id)) {
					masters.Add(id.ToString(CultureInfo.InvariantCulture));
				}
			}
		}

		if (masters.Count > 0) {
			bot.TradeMasters = string.Join(", ", masters);
			bot.CommandMasters = bot.TradeMasters;
			bot.AcceptFromMasters = true;
		}
	}

	/// <summary>ASF's farming order enum onto ours. Anything without an equivalent falls back to the default.</summary>
	private static int MapFarmingOrder(int asf) => asf switch {
		3 => 2,    // CardDropsAscending  -> fewest cards left
		4 => 3,    // CardDropsDescending -> most cards left
		5 => 1,    // HoursAscending      -> least played first
		6 => 0,    // HoursDescending     -> most played first
		7 or 8 => 5,   // Names*          -> alphabetical
		9 => 4,    // Random
		_ => 0
	};

	// ── reading ─────────────────────────────────────────────────────────────
	private static IEnumerable<string> SafeFiles(string dir) {
		try {
			return Directory.GetFiles(dir, "*.json");
		} catch {
			return [];
		}
	}

	private static bool IsBotFile(string name) =>
		(name.Length > 0)
		&& (name[0] != '.')
		&& !name.Equals("ASF", StringComparison.OrdinalIgnoreCase)
		&& !name.Equals("IPC", StringComparison.OrdinalIgnoreCase)
		&& !name.EndsWith(".config", StringComparison.OrdinalIgnoreCase);

	/// <summary>The Steam refresh token out of ASF's per-bot database.</summary>
	private static string? ReadRefreshToken(string dir, string bot) {
		try {
			string path = Path.Combine(dir, bot + ".db");

			if (!File.Exists(path)) {
				return null;
			}

			JsonElement db = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

			// ASF's serialised property name for BotDatabase.RefreshToken.
			string? token = Str(db, "BackingRefreshToken") ?? Str(db, "RefreshToken");

			return string.IsNullOrWhiteSpace(token) ? null : token;
		} catch {
			return null;
		}
	}

	private static string? Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
			? v.GetString()
			: null;

	private static bool? Bool(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
			? v.GetBoolean()
			: null;

	private static int? Int(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
			? i
			: null;

	private static double? Num(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
			? d
			: null;

	private static List<uint> Apps(JsonElement e, string name) {
		List<uint> apps = [];

		if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array) {
			return apps;
		}

		foreach (JsonElement item in v.EnumerateArray()) {
			if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out uint app) && (app > 0)) {
				apps.Add(app);
			}
		}

		return apps;
	}
}
