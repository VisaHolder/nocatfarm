using System.Text;

using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>One account found in another idler: what it signs in with and the settings it would arrive with.</summary>
/// <remarks>
/// Secrets (the password, the login token, the authenticator) stay on the server. The dashboard is only told
/// whether each one is there; importing reads the files again rather than taking anything back from the browser.
/// </remarks>
public sealed class ImportedAccount {
	/// <summary>Identifies the account within one scan - how the dashboard's choices are matched back to it.</summary>
	public required string Key { get; init; }

	/// <summary>What it will be called here (its config file's name).</summary>
	public string Name { get; set; } = "";

	/// <summary>The Steam sign-in name, or empty when the other idler never knew it (a QR code or a typed name then).</summary>
	public string SteamLogin { get; set; } = "";

	public string SteamId { get; set; } = "";

	/// <summary>A Steam refresh token - the same kind nocat.farm keeps, so the account signs in with no password.</summary>
	public string? Token { get; set; }

	/// <summary>A whole maFile, kept (encrypted) so confirmations have its device ID as well as the secrets.</summary>
	public string? MaFile { get; set; }

	/// <summary>
	/// The Windows credential that holds its sign-in (Steam Game Idler). Read only when the user ticks "bring its
	/// sign-in over" for this account, never while looking.
	/// </summary>
	public string? CredentialTarget { get; set; }

	/// <summary>The settings it arrives with, password and authenticator secrets included.</summary>
	public BotConfig Config { get; set; } = new();

	/// <summary>What comes across besides the sign-in, for the preview: "12 games to idle", "custom name: …".</summary>
	public List<Said> Brings { get; } = [];

	/// <summary>Anything worth saying about it that isn't a setting - a password that couldn't be opened, say.</summary>
	public List<Said> Notes { get; } = [];

	public bool HasPassword => !string.IsNullOrEmpty(Config.SteamPassword);

	public bool HasAuthenticator => !string.IsNullOrWhiteSpace(Config.SharedSecret) || (MaFile != null);
}

/// <summary>A setting that isn't any one account's: into the global config, or onto every account already here.</summary>
/// <param name="Name">The setting's name in Settings.cs - the dashboard shows its translated label.</param>
/// <param name="Value">What it's set to, as shown in the preview.</param>
public sealed record ImportSetting(string Name, string Value, Action<GlobalConfig>? ToGlobal = null, Action<BotConfig>? ToAccounts = null) {
	public bool EveryAccount => ToAccounts != null;
}

/// <summary>What one look at another idler found - the preview, before anything is written.</summary>
public sealed class ImportScan {
	public required string Tool { get; init; }
	public required string ToolName { get; init; }

	/// <summary>The folder (or file) it was read from; null when nothing was found.</summary>
	public string? Path { get; set; }

	/// <summary>Everywhere it looked, in order - shown so "nothing found" says where nothing was found.</summary>
	public List<string> Looked { get; } = [];

	public List<ImportedAccount> Accounts { get; } = [];
	public List<ImportSetting> Settings { get; } = [];
	public List<Said> Notes { get; } = [];

	public bool Found => Path != null;
}

/// <summary>One idler nocat.farm can read.</summary>
public interface IIdlerImporter {
	/// <summary>Short id used by the dashboard, the console and the installer ("asf", "ime", …).</summary>
	string Id { get; }

	/// <summary>The program's own name. Not translated - it's a name.</summary>
	string Name { get; }

	/// <summary>Where it looks by itself, in order.</summary>
	IEnumerable<string> Places();

	/// <summary>
	/// The file or folder this importer would read for <paramref name="path"/> (a folder the user picked, or one of
	/// <see cref="Places"/>), or null when there's nothing of this program's there.
	/// </summary>
	string? Resolve(string path);

	/// <summary>Read what's there. Never writes anything, anywhere.</summary>
	ImportScan Read(string resolved);
}

/// <summary>
/// Bringing accounts and settings over from the other popular Steam idlers.
///
/// Each program has a small reader of its own that turns its files into the same shape - accounts with their sign-in
/// and settings, plus a few global settings - and everything is written here, in one place, the same way for all of
/// them: secrets encrypted by <see cref="ConfigStore.SaveBot"/>, login tokens in <see cref="TokenStore"/>, maFiles
/// encrypted into config/authenticators. The other program's own files are only ever opened for reading.
/// </summary>
public static class IdlerImport {
	/// <summary>Every importer, in the order the dashboard lists them - and the order a picked folder is tried in.</summary>
	public static IReadOnlyList<IIdlerImporter> Tools { get; } = [
		new AsfImport.Importer(),
		new IdleMasterImport(extended: true),
		new IdleMasterImport(extended: false),
		new HourBoostrImport(),
		new SingleBoostrImport(),
		new SteamGameIdlerImport(),
		new SteamIdlerImport()
	];

	/// <summary>"Pick a folder": try every importer on it.</summary>
	public const string Auto = "auto";

	public static IIdlerImporter? Find(string? id) =>
		Tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Look for <paramref name="tool"/>'s files - where the user pointed, or its usual places - and read them. With
	/// <see cref="Auto"/> the folder decides which program it is.
	/// </summary>
	public static ImportScan Scan(string tool, string? path) {
		path = Clean(path);

		if (string.Equals(tool, Auto, StringComparison.OrdinalIgnoreCase)) {
			return Detect(path);
		}

		IIdlerImporter? importer = Find(tool);

		if (importer == null) {
			ImportScan none = new() { Tool = tool, ToolName = tool };
			none.Notes.Add(new Said("nocat.farm can't import from that program"));

			return none;
		}

		List<string> places = path != null ? [path] : importer.Places().ToList();

		foreach (string place in places) {
			string? resolved = SafeResolve(importer, place);

			if (resolved != null) {
				ImportScan found = SafeRead(importer, resolved);
				found.Looked.InsertRange(0, places.TakeWhile(p => p != place).Append(place));

				return found;
			}
		}

		ImportScan missing = new() { Tool = importer.Id, ToolName = importer.Name };
		missing.Looked.AddRange(places);

		return missing;
	}

	/// <summary>Which program a folder belongs to, then what's in it.</summary>
	private static ImportScan Detect(string? path) {
		if (path == null) {
			ImportScan none = new() { Tool = Auto, ToolName = "" };
			none.Notes.Add(new Said("Pick the folder the other idler is in"));

			return none;
		}

		foreach (IIdlerImporter importer in Tools) {
			if (SafeResolve(importer, path) is { } resolved) {
				ImportScan found = SafeRead(importer, resolved);
				found.Looked.Insert(0, path);

				return found;
			}
		}

		ImportScan missing = new() { Tool = Auto, ToolName = "" };
		missing.Looked.Add(path);
		missing.Notes.Add(new Said("None of the idlers nocat.farm knows keeps its files in that folder"));

		return missing;
	}

	/// <summary>
	/// The "coming from" choice the installer saved, turned into an importer and a folder. "idlemaster" is either Idle
	/// Master Extended or the original; "other" is a folder to recognise, or just the list when there's no folder.
	/// </summary>
	public static (string Tool, string? Path)? FromPending(PendingImport.Choice? choice) {
		if (choice == null) {
			return null;
		}

		string? path = Clean(choice.Path);

		switch (choice.From.ToLowerInvariant()) {
			case "idlemaster": {
				bool original = (path != null) && File.Exists(System.IO.Path.Combine(path, "IdleMaster.exe")) && !File.Exists(System.IO.Path.Combine(path, "IdleMasterExtended.exe"));
				IIdlerImporter first = Find(original ? "idlemaster" : "ime")!;
				IIdlerImporter second = Find(original ? "ime" : "idlemaster")!;

				// The installer points at the program's folder, but both keep their settings in the profile.
				return (Scan(first.Id, path).Found || !Scan(second.Id, path).Found ? first.Id : second.Id, path);
			}
			case "other":
				return (path == null ? "" : Auto, path);
			default:
				return Find(choice.From) is { } known ? (known.Id, path) : null;
		}
	}

	// ── writing ─────────────────────────────────────────────────────────────

	/// <summary>What the user chose for one account in the preview.</summary>
	/// <param name="Human">Bring it over in human mode.</param>
	/// <param name="SignIn">Bring its sign-in over from the Windows Credential Manager (Steam Game Idler).</param>
	/// <param name="SteamLogin">The Steam sign-in name typed in the preview, for an account the other idler never knew by name.</param>
	public sealed record Pick(string Key, bool Human = false, bool SignIn = false, string? SteamLogin = null);

	/// <param name="Names">Every account written, by its name here.</param>
	/// <param name="Human">The ones of those brought over in human mode.</param>
	public sealed record Outcome(int Imported, int Skipped, List<Said> Notes, List<string> Names, List<string> Human);

	/// <summary>The account here that signs in as <paramref name="login"/> (ignoring case), by its name - or null.</summary>
	public static string? ExistingFor(IReadOnlyDictionary<string, BotConfig> existing, string? login) =>
		string.IsNullOrWhiteSpace(login) ? null
			: existing.FirstOrDefault(e => string.Equals(e.Value.SteamLogin?.Trim(), login.Trim(), StringComparison.OrdinalIgnoreCase)).Key;

	/// <summary>
	/// Write what the user confirmed. Accounts not in <paramref name="picks"/> are left out; an account that already
	/// exists here is left alone unless <paramref name="overwrite"/>. <paramref name="settings"/> are the indexes of the
	/// scan's settings to bring (null = all of them).
	/// </summary>
	/// <param name="global">The global settings to bring settings into. The live ones are passed in, and then it's the
	/// live ones as they are when it gets to them that change - see the lock near the end.</param>
	public static Outcome Apply(ImportScan scan, IReadOnlyCollection<Pick> picks, GlobalConfig global, bool overwrite = false, IReadOnlyCollection<int>? settings = null) {
		// Were these the live settings? A reload or a restore while the accounts go in puts new ones in their place, and
		// the ones passed in are then an old copy.
		bool live = ReferenceEquals(global, Live.Global);
		List<Said> notes = [];
		List<string> names = [];
		List<string> human = [];
		int imported = 0, skipped = 0;
		Dictionary<string, BotConfig> existing = ConfigStore.LoadBots();
		HashSet<string> taken = new(existing.Keys, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> logins = new(StringComparer.OrdinalIgnoreCase);

		foreach (ImportedAccount account in scan.Accounts) {
			if (picks.FirstOrDefault(p => p.Key == account.Key) is not { } pick) {
				continue;
			}

			BotConfig bot = account.Config;
			string name = account.Name;

			// A name typed in the preview for an account the other program only knew by number.
			if ((account.SteamLogin.Length == 0) && !string.IsNullOrWhiteSpace(pick.SteamLogin)) {
				bot.SteamLogin = pick.SteamLogin.Trim();
				name = ImportFiles.NameFor(bot.SteamLogin, name, taken);
			}

			// A name the other program was happy with but that can't be one here - ASF's "con" or "all" - comes in under
			// the next free one, and says so, rather than being left out.
			if (ConfigStore.NameProblem(name) is not null) {
				string was = name;
				name = ImportFiles.NameFor(name, "account", taken);
				notes.Add(new Said("{0}: that name can't be used here, so it's {1}", was, name));
			}

			if (!ConfigStore.IsValidBotName(name)) {
				notes.Add(new Said("{0}: skipped, that name can't be used as a config file", name));
				skipped++;

				continue;
			}

			// The same Steam account under another name - added by hand as "old", brought from ASF as "old-main", or the
			// same login typed twice. Two configs for one account sign each other off Steam all day. It is that account:
			// left alone, or written over it when overwriting - never a second copy beside it.
			if (ExistingFor(existing, bot.SteamLogin) is { } already && !already.Equals(name, StringComparison.OrdinalIgnoreCase)) {
				if (!overwrite) {
					notes.Add(new Said("{0}: already here as {1}, left alone", name, already));
					skipped++;

					continue;
				}

				name = already;
			}

			if ((bot.SteamLogin.Length > 0) && names.Any(n => string.Equals(logins.GetValueOrDefault(n), bot.SteamLogin, StringComparison.OrdinalIgnoreCase))) {
				notes.Add(new Said("{0}: skipped, another account in this import signs in as {1} too", name, bot.SteamLogin));
				skipped++;

				continue;
			}

			if (existing.ContainsKey(name) && !overwrite) {
				notes.Add(new Said("{0}: already exists here, left alone", name));
				skipped++;

				continue;
			}

			if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) {
				notes.Add(new Said("{0}: skipped, another account in this import has the same name", name));
				skipped++;

				continue;
			}

			taken.Add(name);

			if (string.IsNullOrWhiteSpace(bot.SteamLogin)) {
				// Nothing to type a password against: Steam's QR sign-in doesn't need the name at all.
				bot.SteamLogin = "";
				bot.SignInWithQr = true;
			}

			if (pick.Human && !bot.LegitMode) {
				bot.LegitMode = true;
				Settings.ApplyLegitMode(bot, false);
				notes.Add(new Said("{0}: set to human mode", name));
			}

			// Under the account's lock when it's running: a dashboard save of it at the same moment wrote its copy after this.
			lock (BotManager.GateFor(name)) {
				ConfigStore.SaveBot(name, bot);
			}

			names.Add(name);
			logins[name] = bot.SteamLogin;
			imported++;

			if (bot.LegitMode) {
				human.Add(name);
			}

			string? token = account.Token;
			bool fromCredential = (token == null) && pick.SignIn && (account.CredentialTarget != null);

			if (fromCredential) {
				token = ReadCredentialToken(account.CredentialTarget!);
			}

			if (token != null) {
				TokenStore.Save(name, token);
				notes.Add(fromCredential
					? new Said("{0}: brought its sign-in over from {1}", name, scan.ToolName)
					: new Said("{0}: imported with its login token - no password needed", name));
			} else if (fromCredential) {
				notes.Add(new Said("{0}: no saved sign-in was found for it in Windows, so it asks for the password once", name));
			} else if (bot.SignInWithQr) {
				notes.Add(new Said("{0}: imported - it shows a QR code to scan with the Steam app when it first signs in", name));
			} else if (account.HasPassword) {
				notes.Add(account.HasAuthenticator
					? new Said("{0}: imported with its password - its authenticator answers Steam Guard", name)
					: new Said("{0}: imported with its password - it'll ask for a Steam Guard code once", name));
			} else {
				notes.Add(new Said("{0}: imported, but it will ask for the password on first login", name));
			}

			if ((account.MaFile != null) && KeepMaFile(name, account.MaFile)) {
				notes.Add(new Said("{0}: brought its mobile authenticator across - it answers its own Steam Guard now", name));
			} else if (!string.IsNullOrWhiteSpace(bot.SharedSecret) && (account.MaFile == null)) {
				notes.Add(new Said("{0}: brought its authenticator's secrets across - it answers its own Steam Guard now", name));
			}

			notes.AddRange(account.Notes.Select(n => new Said("{0}: {1}", name, n)));
		}

		// Settings that belong to nobody in particular.
		bool globalChanged = false;
		List<ImportSetting> chosen = scan.Settings.Where((_, i) => (settings == null) || settings.Contains(i)).ToList();

		// The live global settings: changed and saved under the lock every other change to them takes - and the live ones as
		// they are now, read under it. The copy passed in is from before the accounts went in: a reload or a backup
		// restored meanwhile replaced it, and changing and saving the old copy wrote its stale settings over nocatFarm.json.
		lock (ConfigStore.GlobalEditGate) {
			GlobalConfig target = live ? Live.Global : global;

			foreach (ImportSetting setting in chosen.Where(static s => s.ToGlobal != null)) {
				setting.ToGlobal!(target);
				globalChanged = true;
			}

			if (globalChanged) {
				ConfigStore.SaveGlobal(target);
			}
		}

		if (globalChanged) {
			notes.Add(new Said("brought {0} setting(s) across into the global settings", chosen.Count(static s => s.ToGlobal != null)));
		}

		List<ImportSetting> everyAccount = chosen.Where(static s => s.ToAccounts != null).ToList();

		if (everyAccount.Count > 0) {
			Dictionary<string, BotConfig> all = ConfigStore.LoadBots();

			// Each one read again, changed and saved under its account's lock. Read all at once above and saved one by one
			// here, a save made in between (the dashboard, 'set', a learned ban) was written over with the older copy.
			foreach (string name in all.Keys) {
				lock (BotManager.GateFor(name)) {
					if (ConfigStore.LoadBot(name) is { } cfg) {
						everyAccount.ForEach(s => s.ToAccounts!(cfg));
						ConfigStore.SaveBot(name, cfg);
					}
				}
			}

			notes.Add(all.Count > 0
				? new Said("brought {0} setting(s) across onto every account here", everyAccount.Count)
				: new Said("there are no accounts here yet, so the per-account settings had nothing to go on"));
		}

		notes.AddRange(scan.Notes);

		return new Outcome(imported, skipped, notes, names, human);
	}

	/// <summary>What an account brings besides its sign-in, in a few words each - the same list for every program.</summary>
	public static List<Said> Describe(ImportedAccount account) {
		BotConfig c = account.Config;
		List<Said> said = [];

		if (c.IdleGames.Count > 0) {
			said.Add(new Said("{0} games to idle", c.IdleGames.Count));
		}

		if (c.PriorityGames.Count > 0) {
			said.Add(new Said("{0} games farmed first", c.PriorityGames.Count));
		}

		if (c.BlacklistedGames.Count > 0) {
			said.Add(new Said("{0} games never touched", c.BlacklistedGames.Count));
		}

		if (c.CustomGameName.Length > 0) {
			said.Add(new Said("shows as \"{0}\"", c.CustomGameName));
		}

		if (c.FarmingOrder != 0) {
			said.Add(new Said("its farming order"));
		}

		if (c.SkipUnplayedGames) {
			said.Add(new Said("skips games never played"));
		}

		if (c.SkipRefundableGames) {
			said.Add(new Said("protects refundable games"));
		}

		if (Math.Abs(c.HoursUntilCardDrops - new BotConfig().HoursUntilCardDrops) > 0.01) {
			said.Add(new Said("cards after {0} hours", c.HoursUntilCardDrops.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
		}

		if (c.HourTargets.Length > 0) {
			said.Add(new Said("hour targets for {0} games (human mode)", c.HourTargets.Split(',').Length));
		}

		if (c.OnlineStatus == 7) {
			said.Add(new Said("appears invisible"));
		}

		if (c.Rep4Rep) {
			said.Add(new Said("rep4rep on"));
		}

		if (c.TradeMasters.Length > 0) {
			said.Add(new Said("who may take its items"));
		}

		if (!c.Enabled) {
			said.Add(new Said("switched off, as it was there"));
		}

		said.AddRange(account.Brings);

		return said;
	}

	/// <summary>Everything in the scan, as a console import takes it - none of it in human mode.</summary>
	public static List<Pick> All(ImportScan scan) => scan.Accounts.Select(static a => new Pick(a.Key)).ToList();

	/// <summary>
	/// Steam Game Idler's saved sign-in: a generic credential whose secret is UTF-16 text holding base64 of the JWT.
	/// Anything that doesn't turn out to be a live Steam token is ignored.
	/// </summary>
	internal static string? ReadCredentialToken(string target) {
		byte[]? blob;

		try {
			blob = ImportPlaces.ReadCredential(target);
		} catch (Exception e) {
			Log.Failed($"import: reading the Windows credential '{target}'", e);
			blob = null;
		}

		if ((blob == null) || (blob.Length == 0)) {
			return null;
		}

		List<string> tries = [Encoding.Unicode.GetString(blob).Trim('\0', ' ', '\r', '\n'), Encoding.UTF8.GetString(blob).Trim('\0', ' ', '\r', '\n')];

		foreach (string text in tries.ToList()) {
			try {
				tries.Add(Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim());
			} catch (FormatException) {
				// not base64 - maybe the token itself
			}
		}

		return tries.FirstOrDefault(static t => ImportFiles.IsLiveJwt(t, out _));
	}

	/// <summary>A maFile into config/authenticators/, encrypted. One already there is the one that works - never replaced.</summary>
	private static bool KeepMaFile(string name, string json) {
		try {
			Directory.CreateDirectory(MaFiles.Dir);
			string target = MaFiles.PathFor(name);

			if (File.Exists(target)) {
				return false;
			}

			AtomicFile.Write(target, Secrets.Protect(json.Trim(), name));

			return true;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't bring {0}'s authenticator across: {1}", name, Log.Describe(e)), name);

			return false;
		}
	}

	/// <summary>
	/// The folder or file asked for, as a full local path - or null. Never a network share (\server\share): only looking
	/// for a file there makes Windows sign in to that server with the user's Windows login, and the scan can be asked for by
	/// a link on any web page when the dashboard has no password.
	/// </summary>
	public static string? Clean(string? path) {
		if (string.IsNullOrWhiteSpace(path)) {
			return null;
		}

		string trimmed = path.Trim().Trim('"').Trim();

		try {
			string expanded = Environment.ExpandEnvironmentVariables(trimmed);

			// A network share, judged as typed as well as in full: off Windows GetFullPath puts the working folder in front
			// of "\\server\share", and the check after it let the share through.
			if ((expanded.Length == 0) || NetworkPath(expanded)) {
				return null;
			}

			string full = System.IO.Path.GetFullPath(expanded);

			return NetworkPath(full) ? null : full;
		} catch {
			return null;
		}
	}

	private static bool NetworkPath(string path) =>
		path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

	private static string? SafeResolve(IIdlerImporter importer, string path) {
		try {
			return importer.Resolve(path);
		} catch (Exception e) {
			Log.Debug(new Said("{0}: couldn't look at {1}: {2}", importer.Name, path, Log.Describe(e)));

			return null;
		}
	}

	private static ImportScan SafeRead(IIdlerImporter importer, string resolved) {
		try {
			return importer.Read(resolved);
		} catch (Exception e) {
			// The note reaches the screen; the file gets the reason and, since a reader that throws is a bug, the stack.
			Log.Failed($"import: {importer.Name} reading {resolved}", e);
			Log.StackToFile(e);

			ImportScan failed = new() { Tool = importer.Id, ToolName = importer.Name };
			failed.Notes.Add(new Said("couldn't read {0}: {1}", resolved, Log.Scrub(e.Message)));

			return failed;
		}
	}
}
