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

			// Only one that still works. An expired one was brought over and tried first - a failed logon that left the
			// account stopped as Failed, when the password that came with it would have signed straight in.
			if (!string.IsNullOrWhiteSpace(token)) {
				if (IsLiveJwt(token, out string? steamId)) {
					account.Token = token;
				} else {
					account.Notes.Add(new Said("its ASF login token has run out, so it isn't brought over"));
				}

				if (IsSteamId(steamId)) {
					account.SteamId = steamId!;
				}
			}

			// No login in the file - ASF asks for it at start. This PC's Steam may know it by the SteamID; otherwise the
			// preview asks, and without one it signs in by QR code. The bot's own name was used, which is somebody else's
			// account, or nobody's.
			if ((account.SteamLogin.Length == 0) && (SteamLogins.AccountFor(account.SteamId.Length > 0 ? account.SteamId : null) is { Length: > 0 } known)) {
				account.SteamLogin = account.Config.SteamLogin = known;
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
				// With rep4rep's own switch: the token and every account's tick came across and it stayed off, so the preview
				// said "rep4rep on" and nothing was ever posted. Only when this brings the token - one set here already is
				// under a switch somebody set here.
				if (string.IsNullOrEmpty(g.Rep4RepApiToken)) {
					g.Rep4RepApiToken = kept;
					g.Rep4RepEnabled = true;
				}
			}));
		}

		return scan;
	}

	/// <summary>ASF.json: the global blacklist and the web proxy, where nocat.farm has the same thing.</summary>
	private static void ReadGlobal(JsonElement asf, ImportScan scan) {
		if (Apps(Prop(asf, "Blacklist")) is { Count: > 0 } blacklist) {
			scan.Settings.Add(new ImportSetting("GlobalBlacklistedGames", string.Join(", ", blacklist), ToGlobal: g => {
				// A new list, not added to in place: this is the live one, and a save or a copy reading it at the same moment
				// threw "collection was modified" half way through.
				g.GlobalBlacklistedGames = [.. g.GlobalBlacklistedGames, .. blacklist.Where(a => !g.GlobalBlacklistedGames.Contains(a))];
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
				// Not a .PENDING one: ASF writes that at 2fainit, before the authenticator is live on Steam, and one never
				// finalised makes codes Steam turns down.
				if (path.EndsWith(".PENDING", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

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
			// Left out means ASF's default, which is off: a bot ASF never started started signing in here.
			Enabled = Bool(cfg, "Enabled") ?? false,
			SteamLogin = Str(cfg, "SteamLogin") ?? "",
			SteamParentalCode = Str(cfg, "SteamParentalCode") ?? ""
		};

		// With no PIN in the config ASF works the Family View PIN out itself and keeps it in the bot's database. ASF also
		// takes "0" for "none" - that isn't a PIN, and copied as one it kept the real one from the database out.
		if (!((bot.SteamParentalCode.Length == 4) && bot.SteamParentalCode.All(char.IsAsciiDigit))) {
			bot.SteamParentalCode = "";
		}

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
				// Once per account: every preview of the folder reads it again.
				Log.DebugOnChange($"import:asf-password:{name}", $"import: {name}'s ASF password (PasswordFormat {format}) wasn't copied: {problem}", name);
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
			// ASF takes up to 255; here the most is 100, and more was quietly put back to 3 at the first save from the dashboard.
			bot.HoursUntilCardDrops = (float) Math.Clamp(hours, 0, 100);
		}

		// ASF's scheduled loot, in hours - the same meaning as ours.
		if (Int(cfg, "SendTradePeriod") is int period && period > 0) {
			bot.SendEveryHours = Math.Min(period, 168);
		}

		// The BoosterCreator plugin's list, in the same bot config.
		if (cfg.TryGetProperty("GamesToBooster", out JsonElement boosters) && (boosters.ValueKind == JsonValueKind.Array)) {
			bot.BoosterPackGames = [.. boosters.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Number)
				.Select(static e => e.TryGetUInt32(out uint id) ? id : 0).Where(static id => id != 0).Distinct()];
		}

		// What the ASF account did, copied. Left out of its file means ASF's own default - off - and not nocat.farm's,
		// which accepts gifts: an account that never took a gift under ASF started taking every one after the import.
		// ASF's one switch covers every kind of gift, so it decides gifted games too.
		bool gifts = Bool(cfg, "AcceptGifts") ?? false;
		bot.AcceptGifts = gifts;
		bot.AcceptGiftedGames = gifts;

		// ASF's current key is a list, FarmingOrders, tried in turn; the single-number FarmingOrder is what older ASF
		// configs used. Reading only that missed every order set on a current install.
		List<int> asfOrders = [];

		if (cfg.TryGetProperty("FarmingOrders", out JsonElement orders) && (orders.ValueKind == JsonValueKind.Array)) {
			foreach (JsonElement entry in orders.EnumerateArray()) {
				if (AsfOrder(entry) is int known) {
					asfOrders.Add(known);
				}
			}
		} else if (Int(cfg, "FarmingOrder") is int legacyOrder) {
			asfOrders.Add(legacyOrder);
		}

		ApplyFarmingOrders(asfOrders, bot, account);

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
		JsonElement asf = Json(Path.Combine(dir, "ASF.json"));
		ulong owner = (Prop(asf, "SteamOwnerID") is { ValueKind: JsonValueKind.Number } o) && o.TryGetUInt64(out ulong id) ? id
			: ulong.TryParse(Str(asf, "SteamOwnerID") ?? Str(asf, "s_SteamOwnerID"), CultureInfo.InvariantCulture, out ulong typed) ? typed : 0;
		TranslateTrading(cfg, bot, owner);

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
			Log.DebugOnChange("import:asf-dpapi", $"import: an ASF password protected by Windows didn't open: {Log.Describe(e)}");

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
		// Left out means ASF's default, none of them - as with the trading switches. Skipped instead, the sale-event
		// things below stayed at nocat.farm's "on" for a bot ASF never ran them on.
		int flags = Int(cfg, "FarmingPreferences") ?? 0;

		// ASF's EFarmingPreferences values. These were read one bit off, so FarmingPausedByDefault (1) arrived as
		// "log out when finished" and nothing arrived where it belonged. FarmingPausedByDefault itself has no
		// equivalent here, and ASF has no farm-offline flag at all.
		bot.StopWhenFarmingDone = (flags & 2) != 0;      // ShutdownOnFarmingFinished
		bot.SendOnFarmingFinished = (flags & 4) != 0;    // SendOnFarmingFinished
		bot.FarmPriorityOnly = (flags & 8) != 0;         // FarmPriorityQueueOnly
		bot.SkipRefundableGames = (flags & 16) != 0;     // SkipRefundableGames
		bot.SkipUnplayedGames = (flags & 32) != 0;       // SkipUnplayedGames
		bot.UnpackBoosterPacks = (flags & 256) != 0;     // AutoUnpackBoosterPacks

		// AutoSteamSaleEvent: the sale's discovery queue and its free items. It was never read, so both stayed on here
		// whatever ASF had.
		bool saleEvent = (flags & 128) != 0;
		bot.ClaimEventItems = saleEvent;
		bot.DiscoveryQueue = saleEvent ? 1 : 0;          // during sales, as ASF does it
	}

	/// <summary>
	/// The same again for BotBehaviour. ASF never accepts group invites whatever its flags say, so ours stay at
	/// their defaults (not accepted, suspicious ones ignored). Reading bit 2 - RejectInvalidTrades - as "don't
	/// accept group invites" meant a bot with BotBehaviour 0 arrived accepting every group invite with the spam
	/// filter off.
	/// </summary>
	private static void TranslateBehaviour(JsonElement cfg, BotConfig bot) {
		// Left out means ASF's default, 0 - "clear the new-items badge" stayed on for a bot that never cleared it.
		int flags = Int(cfg, "BotBehaviour") ?? 0;

		bot.RejectInvalidFriendInvites = (flags & 1) != 0;       // RejectInvalidFriendInvites
		bot.ClearInventoryNotifications = (flags & 8) != 0;      // DismissInventoryNotifications
	}

	/// <summary>
	/// TradingPreferences, plus whoever ASF was set to trust.
	///
	/// ASF's SteamUserPermissions map is where the accounts allowed to take items live - anyone at Master level
	/// or above. That is exactly what our trade-masters list means, so it comes straight across.
	/// </summary>
	private static void TranslateTrading(JsonElement cfg, BotConfig bot, ulong owner) {
		// Left out of the file means ASF's default, 0: no donations, no card swaps - not nocat.farm's defaults.
		int flags = Int(cfg, "TradingPreferences") ?? 0;
		bot.AcceptDonations = (flags & 1) != 0;   // AcceptDonations
		bot.AcceptFairCardSwaps = (flags & 2) != 0;   // SteamTradeMatcher

		List<string> masters = [];

		if (cfg.TryGetProperty("SteamUserPermissions", out JsonElement permissions) && (permissions.ValueKind == JsonValueKind.Object)) {
			foreach (JsonProperty entry in permissions.EnumerateObject()) {
				// 1 FamilySharing · 2 Operator · 3 Master · 4 Owner. Master and up may take items.
				// TryGet: a level written as 3.0 threw, and took the whole ASF folder's preview with it.
				if ((entry.Value.ValueKind == JsonValueKind.Number) && entry.Value.TryGetDouble(out double level) && (level >= 3) && ulong.TryParse(entry.Name, out ulong id)) {
					masters.Add(id.ToString(CultureInfo.InvariantCulture));
				}
			}
		}

		// No Master on the bot: ASF sends to the owner in ASF.json instead, so that's who the items go to here too. Left
		// out, an account that sent its items every day under ASF came across with nobody to send them to.
		if ((masters.Count == 0) && (owner != 0)) {
			masters.Add(owner.ToString(CultureInfo.InvariantCulture));
		}

		// Lowest SteamID first, as ASF picks the first Master to send to.
		masters = [.. masters.OrderBy(static m => ulong.Parse(m, CultureInfo.InvariantCulture))];

		if (masters.Count > 0) {
			bot.TradeMasters = string.Join(", ", masters);
			bot.CommandMasters = bot.TradeMasters;
			bot.AcceptFromMasters = true;
		}
	}

	/// <summary>ASF's farming order enum onto ours. Anything without an equivalent falls back to the default.</summary>
	private static int? MapFarmingOrder(int asf) => asf switch {
		3 => 2,    // CardDropsAscending  -> fewest cards left
		4 => 3,    // CardDropsDescending -> most cards left
		5 => 1,    // HoursAscending      -> least played first
		6 => 0,    // HoursDescending     -> most played first
		7 => 5,    // NamesAscending      -> alphabetical (A to Z; there is no Z to A here)
		9 => 4,    // Random
		_ => null
	};

	/// <summary>ASF's EFarmingOrder names, by number - for saying which ones had no match.</summary>
	private static readonly string[] AsfOrderNames = [
		"Unordered", "AppIDsAscending", "AppIDsDescending", "CardDropsAscending", "CardDropsDescending", "HoursAscending",
		"HoursDescending", "NamesAscending", "NamesDescending", "Random", "BadgeLevelsAscending", "BadgeLevelsDescending",
		"RedeemDateTimesAscending", "RedeemDateTimesDescending", "MarketableAscending", "MarketableDescending"
	];

	/// <summary>One FarmingOrders entry: ASF writes the number, and takes the name too.</summary>
	private static int? AsfOrder(JsonElement e) =>
		e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n
		: e.ValueKind == JsonValueKind.String && Array.FindIndex(AsfOrderNames, a => a.Equals(e.GetString(), StringComparison.OrdinalIgnoreCase)) is >= 0 and int i ? i
		: null;

	/// <summary>
	/// ASF's whole ordered list of farming orders, in turn. The first one with a match here is the order; what couldn't
	/// be matched, and anything after it (here there is one order, no tie-breakers), is said in the import's notes.
	/// </summary>
	/// <remarks>
	/// Only the first entry was read, and one with no match here - BadgeLevelsAscending first, say - fell back to "most
	/// played first", though the next entry, CardDropsAscending, had an exact match. And nothing said so.
	/// </remarks>
	internal static void ApplyFarmingOrders(IReadOnlyList<int> asfOrders, BotConfig bot, ImportedAccount account) {
		int? chosen = null;
		List<string> unmatched = [];
		List<string> after = [];

		foreach (int asf in asfOrders) {
			string name = (asf >= 0) && (asf < AsfOrderNames.Length) ? AsfOrderNames[asf] : asf.ToString(CultureInfo.InvariantCulture);

			if (asf == 0) {
				continue;   // Unordered - no preference, nothing to carry
			}

			if (MapFarmingOrder(asf) is not int mapped) {
				unmatched.Add(name);
			} else if (chosen == null) {
				chosen = mapped;
			} else if (mapped != chosen) {
				after.Add(name);
			}
		}

		if (chosen is int order) {
			bot.FarmingOrder = order;
		}

		if (unmatched.Count > 0) {
			account.Notes.Add(new Said("ASF's farming order {0} has no match here, so it was left out", string.Join(", ", unmatched)));
		}

		if (after.Count > 0) {
			account.Notes.Add(new Said("only one farming order is used here - {0} came after it in ASF, so it was left out", string.Join(", ", after)));
		}
	}

	// ── reading ─────────────────────────────────────────────────────────────
	private static IEnumerable<string> SafeFiles(string dir) {
		try {
			return Directory.GetFiles(dir, "*.json");
		} catch (Exception e) {
			// The folder chosen to import from, and it won't list: every account in it would just be missing.
			Log.DebugOnChange($"import:{dir}", $"import: couldn't list {dir}: {Log.Describe(e)}");

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
