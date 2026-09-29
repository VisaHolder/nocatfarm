using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using NocatFarm.Core;

using static NocatFarm.Config.ImportFiles;

namespace NocatFarm.Config;

/// <summary>
/// Bring accounts across from an ArchiSteamFarm install.
///
/// The valuable part isn't the settings, it's <c>&lt;bot&gt;.db</c>: ASF stores the Steam REFRESH TOKEN there, and
/// that is the same kind of token nocatFarm uses. Copying it across means an imported account logs straight in
/// with no password and no Steam Guard code - which is the whole point of importing rather than retyping. The same
/// file holds the mobile authenticator ASF imported (it deletes the maFile once it has read it), so that comes too.
///
/// Settings from ASF itself and from the HumanIdler plugin are both understood, because on a real install the
/// interesting bits (which games to idle, the custom game name, whether rep4rep is on) live in the plugin keys.
/// </summary>
public static class AsfImport {
	public sealed record Candidate(string Name, string SteamLogin, bool HasToken, bool HasPassword);

	public sealed record Result(int Imported, int Skipped, List<string> Notes);

	/// <summary>The importer as the dashboard's list sees it.</summary>
	public sealed class Importer : IIdlerImporter {
		public string Id => "asf";
		public string Name => "ArchiSteamFarm";
		public IEnumerable<string> Places() => Candidates();
		public string? Resolve(string path) => ConfigFolder(path);
		public ImportScan Read(string resolved) => AsfImport.Read(resolved);
	}

	/// <summary>Find an ASF config folder near the usual places. Null when there isn't one.</summary>
	public static string? Detect() => Candidates().Select(ConfigFolder).FirstOrDefault(static c => c != null);

	/// <summary>Where an ASF install usually is: beside or above this copy, or unzipped on the Desktop, in Documents or Downloads.</summary>
	private static IEnumerable<string> Candidates() {
		List<string> candidates = [];

		try {
			string root = ConfigStore.Root;

			candidates.Add(Path.Combine(root, "..", "config"));
			candidates.Add(Path.Combine(root, "..", "..", "arch", "config"));

			foreach (string usual in ImportPlaces.UsualRoots) {
				candidates.Add(Path.Combine(usual, "arch", "config"));
				candidates.Add(Path.Combine(usual, "ArchiSteamFarm", "config"));
				candidates.Add(Path.Combine(usual, "ASF", "config"));
			}
		} catch {
			// a locked-down profile - the explicit path argument still works
		}

		List<string> full = [];

		foreach (string candidate in candidates) {
			try {
				full.Add(Path.GetFullPath(candidate));
			} catch {
				// unusable path
			}
		}

		// And anywhere a couple of folders down the usual places - "Desktop\tools\ASF-win-x64", say.
		full.AddRange(Folders(ImportPlaces.UsualRoots, 2, static d => File.Exists(Path.Combine(d, "config", "ASF.json"))).Select(static d => Path.Combine(d, "config")));

		return full.Distinct(PathComparer);
	}

	/// <summary>
	/// ASF's config folder for a path: the folder itself when ASF.json is in it, or its config subfolder (the installer
	/// and most people point at the folder ArchiSteamFarm.exe is in). Never nocat.farm's own config folder.
	/// </summary>
	private static string? ConfigFolder(string path) {
		try {
			foreach (string dir in new[] { path, Path.Combine(path, "config") }) {
				string full = Path.GetFullPath(dir);

				// ASF.json is the marker; a bare "config" folder could be anything.
				if (File.Exists(Path.Combine(full, "ASF.json")) && !string.Equals(full, ConfigStore.ConfigDir, StringComparison.OrdinalIgnoreCase)) {
					return full;
				}
			}
		} catch {
			// unusable path
		}

		return null;
	}

	/// <summary>What would be imported from <paramref name="dir"/>, without importing anything.</summary>
	public static List<Candidate> Preview(string dir) =>
		Read(ConfigFolder(dir) ?? dir).Accounts.Select(static a => new Candidate(a.Name, a.SteamLogin, a.Token != null, a.HasPassword)).ToList();

	/// <summary>
	/// Import every bot from an ASF config folder. Existing nocatFarm accounts of the same name are left alone
	/// unless <paramref name="overwrite"/> is set - importing twice should not clobber settings you've changed.
	/// </summary>
	/// <param name="human">Accounts to bring across in human mode (by name); the rest keep the robot defaults.</param>
	public static Result Run(string dir, GlobalConfig global, bool overwrite = false, IReadOnlyCollection<string>? human = null) {
		ImportScan scan = Read(ConfigFolder(dir) ?? dir);
		IdlerImport.Outcome outcome = IdlerImport.Apply(scan, IdlerImport.All(scan, human), global, overwrite);

		return new Result(outcome.Imported, outcome.Skipped, outcome.Notes.Select(static n => n.ToString()).ToList());
	}

	/// <summary>Everything in an ASF config folder, as a preview. Reads only.</summary>
	public static ImportScan Read(string dir) {
		ImportScan scan = new() { Tool = "asf", ToolName = "ArchiSteamFarm", Path = dir };
		JsonElement asf = Json(Path.Combine(dir, "ASF.json"));

		ReadGlobal(asf, scan);

		string? rep4repToken = null;

		foreach (string file in SafeFiles(dir)) {
			string name = Path.GetFileNameWithoutExtension(file);

			if (!IsBotFile(name)) {
				continue;
			}

			JsonElement cfg = Json(file);

			if (cfg.ValueKind != JsonValueKind.Object) {
				scan.Notes.Add(new Said("{0}: couldn't read it", name));

				continue;
			}

			JsonElement db = Json(Path.Combine(dir, name + ".db"));
			ImportedAccount account = new() { Key = name, Name = name };
			account.Config = Translate(cfg, name, db, account, dir);
			account.SteamLogin = account.Config.SteamLogin;

			// The token is the reason to do this at all.
			string? token = Str(db, "BackingRefreshToken") ?? Str(db, "RefreshToken");

			if (!string.IsNullOrWhiteSpace(token)) {
				account.Token = token;

				if (IsLiveJwt(token, out string? steamId) && IsSteamId(steamId)) {
					account.SteamId = steamId!;
				}
			}

			// ASF reads a dropped-in maFile once and then DELETES it, keeping the two secrets in the bot's database.
			// Looking only for the file missed every authenticator ASF had actually imported.
			JsonElement mobile = Prop(db, "_MobileAuthenticator");

			if (Str(mobile, "shared_secret") is { Length: > 0 } shared) {
				account.Config.SharedSecret = shared;
				account.Config.IdentitySecret = Str(mobile, "identity_secret") ?? "";
			}

			// A maFile still lying about - not yet read by ASF (.maFile), half-way through ASF's own 2FA setup
			// (.maFile.NEW), or named by SteamID the way Steam Desktop Authenticator names them - comes whole, device ID
			// and all. One that doesn't hold a shared secret is worse than none: it would look like 2FA was handled.
			if (FindMaFile(dir, name, account.SteamLogin, account.SteamId) is { } maFile) {
				account.MaFile = maFile;
			}

			if ((Str(cfg, "HumanIdlerRep4RepToken") is { Length: > 0 } r4r) && (rep4repToken == null)) {
				rep4repToken = r4r;
			}

			scan.Accounts.Add(account);
		}

		// One rep4rep token covers the whole account, so it belongs in the global config.
		if (rep4repToken != null) {
			string kept = rep4repToken;
			scan.Settings.Add(new ImportSetting("Rep4RepApiToken", "••••••", ToGlobal: g => {
				if (string.IsNullOrEmpty(g.Rep4RepApiToken)) {
					g.Rep4RepApiToken = kept;
				}
			}));
		}

		return scan;
	}

	/// <summary>ASF.json: the global blacklist and the web proxy, where nocat.farm has the same thing.</summary>
	private static void ReadGlobal(JsonElement asf, ImportScan scan) {
		if (Apps(Prop(asf, "Blacklist")) is { Count: > 0 } blacklist) {
			scan.Settings.Add(new ImportSetting("GlobalBlacklistedGames", string.Join(", ", blacklist), ToGlobal: g => {
				foreach (uint app in blacklist.Where(a => !g.GlobalBlacklistedGames.Contains(a))) {
					g.GlobalBlacklistedGames.Add(app);
				}
			}));
		}

		// Only into an empty box: a proxy already set here is the one that works here.
		if (Str(asf, "WebProxy") is { Length: > 0 } proxy) {
			string? user = Str(asf, "WebProxyUsername");
			string? password = Str(asf, "WebProxyPassword");

			scan.Settings.Add(new ImportSetting("WebProxy", proxy, ToGlobal: g => {
				if (g.WebProxy.Length > 0) {
					return;
				}

				g.WebProxy = proxy;
				g.WebProxyUsername = user ?? "";
				g.WebProxyPassword = password ?? "";
			}));
		}
	}

	/// <summary>
	/// The first maFile that belongs to this bot: &lt;bot&gt;.maFile, &lt;bot&gt;.maFile.NEW, &lt;SteamID64&gt;.maFile, then any
	/// maFile whose account_name is this bot's login. Its text, or null.
	/// </summary>
	private static string? FindMaFile(string dir, string bot, string login, string steamId) {
		List<string> tries = [Path.Combine(dir, bot + ".maFile"), Path.Combine(dir, bot + ".maFile.NEW")];

		if (steamId.Length > 0) {
			tries.Add(Path.Combine(dir, steamId + ".maFile"));
		}

		foreach (string path in tries) {
			if ((Text(path) is { } text) && (MaFileSecrets(text).Shared is { Length: > 0 })) {
				return text;
			}
		}

		try {
			foreach (string path in Directory.EnumerateFiles(dir, "*.maFile*")) {
				if ((Text(path) is { } text) && (MaFileSecrets(text).Shared is { Length: > 0 })
					&& (Find(Json(path), "account_name") is { ValueKind: JsonValueKind.String } account)
					&& string.Equals(account.GetString(), login, StringComparison.OrdinalIgnoreCase)) {
					return text;
				}
			}
		} catch {
			// a folder it may not list
		}

		return null;
	}

	// ── translation ─────────────────────────────────────────────────────────
	private static BotConfig Translate(JsonElement cfg, string name, JsonElement db, ImportedAccount account, string dir) {
		BotConfig bot = new() {
			Enabled = Bool(cfg, "Enabled") ?? true,
			SteamLogin = Str(cfg, "SteamLogin") ?? name,
			SteamParentalCode = Str(cfg, "SteamParentalCode") ?? ""
		};

		// With no PIN in the config ASF works the Family View PIN out itself and keeps it in the bot's database.
		if (bot.SteamParentalCode.Length == 0) {
			bot.SteamParentalCode = Str(db, "CachedSteamParentalCode") ?? Str(db, "BackingCachedSteamParentalCode") ?? "";
		}

		// PasswordFormat says how ASF stored the password: plain, encrypted with ASF's key or with Windows' protection
		// for its user, or not stored at all but named - an environment variable, or a file. Only a password that
		// really opens is copied: sending the scrambled text to Steam is a failed logon that counts against the
		// account. Anything else is left empty, and the login token that comes across signs in, or it asks once.
		if (Str(cfg, "SteamPassword") is { Length: > 0 } stored) {
			int format = Int(cfg, "PasswordFormat") ?? 0;
			string? password = OpenPassword(stored, format, dir, out Said problem);

			if (password != null) {
				bot.SteamPassword = password;
			} else {
				account.Notes.Add(problem);
			}
		}

		// ASF writes "{0}" here as a template placeholder, which is not a device name.
		string? machine = Str(cfg, "MachineName");

		if (!string.IsNullOrEmpty(machine) && !machine.Contains('{', StringComparison.Ordinal)) {
			bot.MachineName = machine;
		}

		if (Int(cfg, "OnlineStatus") is int persona) {
			bot.OnlineStatus = persona;   // ASF uses the same EPersonaState numbering
		}

		// ASF's GamingDeviceType numbers devices differently from our "Play as if on", so it isn't guessed at.
		if (Int(cfg, "GamingDeviceType") is int device && (device > 1)) {
			account.Notes.Add(new Said("ASF played it as a different device - choose one under \"Play as if on\" in its settings"));
		}

		if (Num(cfg, "HoursUntilCardDrops") is double hours) {
			bot.HoursUntilCardDrops = (float) hours;
		}

		// ASF's scheduled loot, in hours - the same meaning as ours.
		if (Int(cfg, "SendTradePeriod") is int period && period > 0) {
			bot.SendEveryHours = Math.Min(period, 168);
		}

		// The BoosterCreator plugin's list, in the same bot config.
		if (cfg.TryGetProperty("GamesToBooster", out JsonElement boosters) && (boosters.ValueKind == JsonValueKind.Array)) {
			bot.BoosterGames = string.Join(", ", boosters.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Number).Select(static e => e.GetRawText()));
		}

		if (Bool(cfg, "AcceptGifts") is bool gifts) {
			bot.AcceptGifts = gifts;
		}

		// ASF's current key is a list, FarmingOrders, tried in turn; ours is one choice, so the first one wins.
		// The single-number FarmingOrder is what older ASF configs used. Reading only that missed every order
		// set on a current install.
		if (cfg.TryGetProperty("FarmingOrders", out JsonElement orders) && (orders.ValueKind == JsonValueKind.Array)
			&& (orders.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } firstOrder) && firstOrder.TryGetInt32(out int order)) {
			bot.FarmingOrder = MapFarmingOrder(order);
		} else if (Int(cfg, "FarmingOrder") is int legacyOrder) {
			bot.FarmingOrder = MapFarmingOrder(legacyOrder);
		}

		bot.IdleGames = Apps(Prop(cfg, "GamesPlayedWhileIdle"));
		bot.CustomGameName = Str(cfg, "CustomGamePlayedWhileIdle") ?? "";

		// HumanIdler plugin keys. On a real install this is where the interesting settings actually live.
		if (Apps(Prop(cfg, "HumanIdlerGames")) is { Count: > 0 } plugin) {
			bot.IdleGames = plugin;
		}

		if (Str(cfg, "HumanIdlerCustomGame") is { Length: > 0 } custom) {
			bot.CustomGameName = custom;
		}

		if (bot.IdleGames.Count == 0 && Apps(Prop(cfg, "HumanIdlerCustomGameApps")) is { Count: > 0 } customApps) {
			bot.IdleGames = customApps;
		}

		// ASF shows the real game while it farms unless CustomGamePlayedWhileFarming names something else. Ours keeps
		// the custom name up while farming by default, so say which. A name with ASF's {0}/{1} game placeholders
		// can't be shown here - there's no template - so only a plain one comes across.
		string? whileFarming = Str(cfg, "CustomGamePlayedWhileFarming");
		bool plainFarmingName = !string.IsNullOrEmpty(whileFarming) && !whileFarming.Contains('{', StringComparison.Ordinal);

		if (bot.CustomGameName.Length == 0 && plainFarmingName) {
			bot.CustomGameName = whileFarming!;
		}

		if (bot.CustomGameName.Length > 0) {
			bot.PlayWhileFarming = plainFarmingName;
		}

		if (Bool(cfg, "HumanIdlerRep4Rep") == true) {
			bot.Rep4Rep = true;
		}

		// The farming priority queue and blacklist aren't in the config at all - ASF keeps them in the bot's
		// database, next to the login token, because they're edited by command rather than by hand.
		if (Apps(Prop(db, "FarmingPriorityQueueAppIDs")) is { Count: > 0 } priority) {
			bot.PriorityGames = priority;
		} else if (Apps(Prop(cfg, "IdlePriorityQueue")) is { Count: > 0 } legacyPriority) {
			bot.PriorityGames = legacyPriority;
		}

		if (Apps(Prop(db, "FarmingBlacklistAppIDs")) is { Count: > 0 } blacklist) {
			bot.BlacklistedGames = blacklist;
		}

		TranslateFarming(cfg, bot);
		TranslateBehaviour(cfg, bot);
		TranslateTrading(cfg, bot);

		return bot;
	}

	/// <summary>ASF's own key for its AES format, unless it was started with --cryptkey.</summary>
	private static readonly byte[] DefaultCryptKey = "ArchiSteamFarm"u8.ToArray();

	/// <summary>
	/// The password out of however ASF stored it (ASF's ECryptoMethod), or null with the reason. 0 plain text; 1 AES
	/// with ASF's key (the first block is the IV, itself ECB-encrypted, then CBC); 2 Windows DPAPI for the user ASF
	/// ran as; 3 the name of an environment variable; 4 the path of a file.
	/// </summary>
	internal static string? OpenPassword(string stored, int format, string dir, out Said problem) {
		problem = default;

		switch (format) {
			case 0:
				return stored;
			case 1:
				if (DecryptAes(stored) is { } aes) {
					return aes;
				}

				problem = new Said("its ASF password is encrypted with a key of its own, so it wasn't copied - the login token signs in, or it asks for the password once");

				return null;
			case 2:
				if (!OperatingSystem.IsWindows()) {
					problem = new Said("its ASF password is protected by Windows, so only Windows can open it - the login token signs in, or it asks for the password once");

					return null;
				}

				if (DecryptDpapi(stored) is { } dpapi) {
					return dpapi;
				}

				problem = new Said("its ASF password is encrypted with a key of its own, so it wasn't copied - the login token signs in, or it asks for the password once");

				return null;
			case 3:
				if (Environment.GetEnvironmentVariable(stored) is { Length: > 0 } fromEnvironment) {
					return fromEnvironment;
				}

				problem = new Said("its ASF password comes from the environment variable {0}, which isn't set for nocat.farm - the login token signs in, or it asks for the password once", stored);

				return null;
			case 4: {
				// A relative path is relative to where ASF runs from - the folder above config.
				string? root = Path.GetDirectoryName(Path.GetFullPath(dir));
				IEnumerable<string> paths = Path.IsPathRooted(stored) ? [stored] : new[] { root, dir }.Where(static d => d != null).Select(d => Path.Combine(d!, stored));

				foreach (string path in paths) {
					if (Text(path) is { } text && (text.Trim() is { Length: > 0 } fromFile)) {
						return fromFile;
					}
				}

				problem = new Said("its ASF password file {0} couldn't be read - the login token signs in, or it asks for the password once", stored);

				return null;
			}
			default:
				problem = new Said("its ASF password is stored in a way nocat.farm doesn't know, so it wasn't copied - the login token signs in, or it asks for the password once");

				return null;
		}
	}

	private static string? DecryptAes(string stored) {
		try {
			byte[] data = Convert.FromBase64String(stored);

			if ((data.Length < 32) || ((data.Length % 16) != 0)) {
				return null;
			}

			using Aes aes = Aes.Create();
			aes.Key = SHA256.HashData(DefaultCryptKey);
			byte[] iv = aes.DecryptEcb(data.AsSpan(0, 16), PaddingMode.None);
			byte[] plain = aes.DecryptCbc(data.AsSpan(16), iv, PaddingMode.PKCS7);

			return Readable(plain);
		} catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) {
			return null;   // a custom --cryptkey: the padding doesn't check out
		}
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static string? DecryptDpapi(string stored) {
		try {
			return Readable(ProtectedData.Unprotect(Convert.FromBase64String(stored), DefaultCryptKey, DataProtectionScope.CurrentUser));
		} catch (Exception e) when (e is CryptographicException or FormatException) {
			return null;   // another Windows user, or a custom --cryptkey
		}
	}

	/// <summary>Decoded text only if it is plausibly a password - a wrong key that happens to pad correctly gives garbage.</summary>
	private static string? Readable(byte[] bytes) {
		try {
			string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);

			return (text.Length > 0) && !text.Any(char.IsControl) ? text : null;
		} catch (DecoderFallbackException) {
			return null;
		}
	}

	/// <summary>ASF packs its farming switches into one bitfield. Unpack it into the individual settings here.</summary>
	private static void TranslateFarming(JsonElement cfg, BotConfig bot) {
		if (Int(cfg, "FarmingPreferences") is not int flags) {
			return;
		}

		// ASF's EFarmingPreferences values. These were read one bit off, so FarmingPausedByDefault (1) arrived as
		// "log out when finished" and nothing arrived where it belonged. FarmingPausedByDefault itself has no
		// equivalent here, and ASF has no farm-offline flag at all.
		bot.StopWhenFarmingDone = (flags & 2) != 0;      // ShutdownOnFarmingFinished
		bot.SendOnFarmingFinished = (flags & 4) != 0;    // SendOnFarmingFinished
		bot.FarmPriorityOnly = (flags & 8) != 0;         // FarmPriorityQueueOnly
		bot.SkipRefundableGames = (flags & 16) != 0;     // SkipRefundableGames
		bot.SkipUnplayedGames = (flags & 32) != 0;       // SkipUnplayedGames
		bot.UnpackBoosterPacks = (flags & 256) != 0;     // AutoUnpackBoosterPacks
	}

	/// <summary>
	/// The same again for BotBehaviour. ASF never accepts group invites whatever its flags say, so ours stay at
	/// their defaults (not accepted, suspicious ones ignored). Reading bit 2 - RejectInvalidTrades - as "don't
	/// accept group invites" meant a bot with BotBehaviour 0 arrived accepting every group invite with the spam
	/// filter off.
	/// </summary>
	private static void TranslateBehaviour(JsonElement cfg, BotConfig bot) {
		if (Int(cfg, "BotBehaviour") is not int flags) {
			return;
		}

		bot.RejectInvalidFriendInvites = (flags & 1) != 0;       // RejectInvalidFriendInvites
		bot.ClearInventoryNotifications = (flags & 8) != 0;      // DismissInventoryNotifications
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
			bot.AcceptFairCardSwaps = (flags & 2) != 0;   // SteamTradeMatcher
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
}
