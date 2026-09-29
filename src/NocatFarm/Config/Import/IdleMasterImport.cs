using System.Xml.Linq;

using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>
/// Idle Master Extended and the original Idle Master. Both are .NET Framework programs that keep their settings in a
/// user.config under %LOCALAPPDATA% (the original, installed by ClickOnce, can also be under Apps\2.0\Data).
///
/// Neither keeps anything to sign in with - they borrow a browser cookie - so an account comes over with its games and
/// its farming choices, and is asked for its password (or a QR code) the first time it signs in. The cookie is used
/// for one thing only: the SteamID64 at its front, to find the account's sign-in name in this PC's Steam.
/// </summary>
public sealed class IdleMasterImport(bool extended) : IIdlerImporter {
	public string Id => extended ? "ime" : "idlemaster";
	public string Name => extended ? "Idle Master Extended" : "Idle Master";

	private string Section => extended ? "IdleMasterExtended.Properties.Settings" : "IdleMaster.Properties.Settings";
	private string Exe => extended ? "IdleMasterExtended.exe" : "IdleMaster.exe";

	/// <summary>The Steam web cookies they keep - only ever read for the SteamID64 in front.</summary>
	private const string SecureCookie = "steamLoginSecure", OldCookie = "steamLogin";

	public IEnumerable<string> Places() {
		if (ImportPlaces.LocalAppData.Length == 0) {
			return [];
		}

		return extended
			? [Path.Combine(ImportPlaces.LocalAppData, "IdleMasterExtended")]
			: [Path.Combine(ImportPlaces.LocalAppData, "IdleMaster"), Path.Combine(ImportPlaces.LocalAppData, "Apps", "2.0", "Data")];
	}

	public string? Resolve(string path) {
		if (File.Exists(path)) {
			return DotNetUserConfig.Holds(path, Section) ? path : null;
		}

		if (!Directory.Exists(path)) {
			return null;
		}

		// ClickOnce buries it: Apps\2.0\Data\<random>\<random>\idle..tion_<hash>\Data\<version>\user.config.
		int depth = path.Contains(Path.Combine("Apps", "2.0"), StringComparison.OrdinalIgnoreCase) ? 7 : 3;

		if (DotNetUserConfig.Newest(path, Section, depth) is { } found) {
			return found;
		}

		// The program's own folder (what the installer finds) holds only the exe - the settings are in the profile.
		if (File.Exists(Path.Combine(path, Exe))) {
			foreach (string place in Places()) {
				if (!string.Equals(Path.GetFullPath(place), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && (Resolve(place) is { } inProfile)) {
					return inProfile;
				}
			}
		}

		return null;
	}

	public ImportScan Read(string resolved) {
		ImportScan scan = new() { Tool = Id, ToolName = Name, Path = resolved };
		Dictionary<string, XElement> settings = DotNetUserConfig.Read(resolved, Section);
		BotConfig bot = new();

		bot.BlacklistedGames = ImportFiles.Apps(DotNetUserConfig.Strings(settings, "blacklist"));

		if (extended) {
			// Idle Master Extended's whitelist: the games it was told to idle.
			List<uint> whitelist = ImportFiles.Apps(DotNetUserConfig.Strings(settings, "whitelist"));

			if (whitelist.Count > Core.SteamIds.GamesAtOnce) {
				scan.Notes.Add(new Said("{0} games were listed to idle, but Steam plays {1} at once - the first {1} came across", whitelist.Count, Core.SteamIds.GamesAtOnce));
			}

			bot.IdleGames = whitelist.Take(Core.SteamIds.GamesAtOnce).ToList();

			if (DotNetUserConfig.Bool(settings, "IdleOnlyPlayed") == true) {
				bot.SkipUnplayedGames = true;
			}
		}

		switch (DotNetUserConfig.String(settings, "sort")?.ToLowerInvariant()) {
			case "leastcards":
				bot.FarmingOrder = 2;   // fewest cards left
				break;
			case "mostcards":
				bot.FarmingOrder = 3;   // most cards left
				break;
			case "mostvalue":
				scan.Notes.Add(new Said("it farmed the most valuable cards first, which nocat.farm doesn't do - it farms most played first"));
				break;
		}

		// The account number only - the rest of the cookie is a web session and is never used.
		string? steamId = ImportFiles.SteamIdFromCookie(DotNetUserConfig.String(settings, SecureCookie))
			?? ImportFiles.SteamIdFromCookie(DotNetUserConfig.String(settings, OldCookie));
		string login = SteamLogins.AccountFor(steamId) ?? "";
		HashSet<string> taken = [];

		ImportedAccount account = new() {
			Key = steamId ?? Id,
			Name = ImportFiles.NameFor(login, extended ? "idlemasterx" : "idlemaster", taken),
			SteamLogin = login,
			SteamId = steamId ?? "",
			Config = bot
		};

		bot.SteamLogin = login;
		account.Notes.Add(new Said("{0} doesn't keep a password, so it asks for one (or a QR code) the first time it signs in", Name));

		if ((steamId != null) && (login.Length == 0)) {
			scan.Notes.Add(new Said("it was signed in as Steam account {0}, which this PC's Steam doesn't know by name - type the account name, or leave it empty to sign in with a QR code", steamId));
		}

		scan.Accounts.Add(account);

		return scan;
	}
}
