using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// One zip with everything worth keeping - the settings, every account, login tokens, authenticator files and the
/// state that took time to build (history, lifetime totals, today's plans, hunt progress, rep4rep counts, the key
/// queue, what "Learn from how I play" learned, the idle rotation's place) - and the way back from one.
/// </summary>
/// <remarks>
/// What goes in is a fixed list, not "the config folder": caches, logs and temp files are either rebuilt by themselves
/// or worthless, and a restore has to be able to say no to anything it doesn't know. The same list decides both, so a
/// backup can always be restored and a zip with anything else in it can't be.
///
/// The logins and secrets inside are exactly as encrypted as they are on disk. On Windows that's the Windows user's
/// own key (DPAPI): the zip restores on this PC under this user, and anywhere else the accounts simply ask for their
/// passwords again. Off Windows the key file (state/secret.key) goes in with them, so the zip opens anywhere - which
/// is also why the zip itself has to be kept private.
///
/// A restore never deletes: an account or a file the zip doesn't have is left as it is. What it does overwrite is
/// first saved as a backup of its own (backups/...-before-restore.zip), so a wrong zip can be undone.
/// </remarks>
public static partial class Backup {
	internal const string ManifestName = "manifest.json";
	internal const string ReadmeName = "README.txt";
	internal const string App = "nocat.farm";
	internal const int Format = 1;

	/// <summary>The most any one file in a backup may be - the biggest real one (a history month) is a few hundred KB.</summary>
	internal const long MaxFileBytes = 20L * 1024 * 1024;

	/// <summary>All of it together, unpacked.</summary>
	internal const long MaxTotalBytes = 100L * 1024 * 1024;

	/// <summary>The zip as uploaded.</summary>
	public const long MaxZipBytes = 50L * 1024 * 1024;

	internal const int MaxEntries = 5000;

	/// <summary>What the copy made just before a restore is called after: nocat.farm-backup-2026-09-30-before-restore.zip.</summary>
	internal const string BeforeRestore = "-before-restore";

	/// <summary>A moment as the log shows it: 2026-09-30 14:05.</summary>
	private static string Stamp(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

	public sealed class Manifest {
		public string App { get; set; } = "";
		public int Format { get; set; }
		public string Version { get; set; } = "";
		public DateTime Created { get; set; }

		/// <summary>The PC it was made on and whether its secrets are sealed by Windows - so a restore can say what won't carry over.</summary>
		public string Machine { get; set; } = "";
		public bool Windows { get; set; }
		public List<string> Accounts { get; set; } = [];
		public int Files { get; set; }
	}

	/// <summary>What a zip holds, checked - or why it can't be restored.</summary>
	public sealed class Inspection {
		public string? Error { get; set; }
		public Manifest? Manifest { get; set; }
		public List<string> Files { get; set; } = [];
		public List<string> Accounts { get; set; } = [];

		/// <summary>Made on this PC (Windows: and so its logins open here). False = they'll ask for passwords again.</summary>
		public bool SameMachine { get; set; }

		public bool Ok => Error == null;
	}

	/// <summary>
	/// What a path under config/ is to a backup, or null for "not ours". The one allow-list: making a backup takes what
	/// it names, and a restore refuses anything it doesn't.
	/// </summary>
	internal static string? Kind(string rel) {
		if (rel == "nocatFarm.json") {
			return "settings";
		}

		Match m = Pattern().Match(rel);

		if (!m.Success) {
			return null;
		}

		// Whatever the pattern let through, an account name still has to be one the app would accept - no "..",
		// no device names, nothing that is a path in disguise.
		string name = m.Groups["name"].Value;

		if ((name.Length > 0) && !ConfigStore.IsValidBotName(name)) {
			return null;
		}

		return m.Groups["acct"].Success ? "account"
			: m.Groups["token"].Success ? "login token"
			: m.Groups["ma"].Success ? "authenticator"
			: m.Groups["key"].Success ? "login key"
			: m.Groups["hist"].Success ? "history"
			: "state";
	}

	// Each branch marks itself with an empty group, so Kind can tell which one matched.
	[GeneratedRegex(@"^(?:(?<acct>)(?<name>[^/\\:*?""<>|]+)\.json" +
		@"|tokens/(?<token>)(?<name>[^/\\:*?""<>|]+)\.(?:token|access)" +
		@"|authenticators/(?<ma>)(?<name>[^/\\:*?""<>|]+)\.maFile" +
		@"|state/(?<key>)secret\.key" +
		@"|state/history/(?<hist>)\d{4}-\d{2}\.json" +
		@"|state/(?:lifetime|lifetime-games|keys|report|weekly-report)\.json" +
		@"|state/(?:human|hunt|rep4rep|owner|rotation)-(?<name>[^/\\:*?""<>|]+)\.json)$", RegexOptions.CultureInvariant)]
	private static partial Regex Pattern();

	/// <summary>Every file of ours in the config folder now, as (path under config/ with forward slashes, full path).</summary>
	internal static List<(string Rel, string Full)> Files() {
		List<(string, string)> found = [];
		string dir = ConfigStore.ConfigDir;

		if (!Directory.Exists(dir)) {
			return found;
		}

		foreach (string full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
			string rel = Path.GetRelativePath(dir, full).Replace('\\', '/');

			if (Kind(rel) != null) {
				found.Add((rel, full));
			}
		}

		return [.. found.OrderBy(static f => f.Item1, StringComparer.Ordinal)];
	}

	/// <summary>The accounts a set of backup paths holds: one per account .json.</summary>
	private static List<string> AccountsIn(IEnumerable<string> rels) =>
		[.. rels.Where(static r => (Kind(r) == "account")).Select(static r => r[..^".json".Length]).Order(StringComparer.OrdinalIgnoreCase)];

	/// <summary>The backup, as the bytes of a zip.</summary>
	public static byte[] Create() {
		List<(string Rel, string Full)> files = Files();
		Manifest manifest = new() {
			App = App,
			Format = Format,
			Version = Build.Version,
			Created = DateTime.UtcNow,
			Machine = Environment.MachineName,
			Windows = OperatingSystem.IsWindows(),
			Accounts = AccountsIn(files.Select(static f => f.Rel)),
			Files = 0
		};

		using MemoryStream ms = new();

		using (ZipArchive zip = new(ms, ZipArchiveMode.Create, leaveOpen: true)) {
			foreach ((string rel, string full) in files) {
				byte[] bytes;

				try {
					bytes = ReadShared(full);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// Gone between listing and reading (a token being renewed), or locked: the rest is still worth having.
					Log.Failed($"backup: reading {rel}", e);

					continue;
				}

				ZipArchiveEntry entry = zip.CreateEntry("config/" + rel, CompressionLevel.Optimal);
				entry.LastWriteTime = File.GetLastWriteTime(full);

				using Stream s = entry.Open();
				s.Write(bytes);
				manifest.Files++;
			}

			Write(zip, ManifestName, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
			Write(zip, ReadmeName, Readme(manifest));
		}

		return ms.ToArray();

		static void Write(ZipArchive zip, string name, string text) {
			using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
			s.Write(Encoding.UTF8.GetBytes(text));
		}
	}

	/// <summary>
	/// A new file - never one that's there already (an IOException then) - that only its owner can read or write off Windows
	/// (0600); an ordinary one on Windows.
	/// </summary>
	internal static void WriteOwnerOnly(string path, byte[] data) {
		using FileStream fs = OperatingSystem.IsWindows()
			? new(path, FileMode.CreateNew, FileAccess.Write)
			: new(path, new FileStreamOptions {
				Mode = FileMode.CreateNew,
				Access = FileAccess.Write,
				UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
			});

		fs.Write(data);
		fs.Flush(true);
	}

	/// <summary>The backups folder, only its owner let in off Windows (0700) - made that way, or put that way if it's there.</summary>
	internal static void OwnerOnlyFolder(string dir) {
		if (OperatingSystem.IsWindows()) {
			Directory.CreateDirectory(dir);

			return;
		}

		const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
		Directory.CreateDirectory(dir, Owner);

		try {
			if (File.GetUnixFileMode(dir) != Owner) {
				File.SetUnixFileMode(dir, Owner);
			}
		} catch (Exception e) when (e is UnauthorizedAccessException or IOException) {
			// Not ours to change (a Docker volume made by root, say): the files in it are owner-only all the same.
			Log.DebugOnChange("backup:folder-mode", $"backup: couldn't make {dir} owner-only: {Log.Describe(e)}");
		}
	}

	/// <summary>Read a file another part of the app may be writing at the same moment.</summary>
	private static byte[] ReadShared(string path) {
		using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using MemoryStream ms = new();
		fs.CopyTo(ms);

		return ms.ToArray();
	}

	/// <summary>nocat.farm-backup-2026-09-30.zip - the date it was made, local time.</summary>
	public static string FileName(DateTime local, string suffix = "") =>
		$"nocat.farm-backup-{local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{suffix}.zip";

	/// <summary>Where the 'backup' command puts them: a backups folder next to config.</summary>
	public static string Folder => Path.Combine(ConfigStore.Root, "backups");

	/// <summary>Write a backup into the backups folder and hand back its path. A second one the same day gets the time too.</summary>
	public static string WriteToFolder(string suffix = "") {
		OwnerOnlyFolder(Folder);
		DateTime now = DateTime.Now;
		string path = Path.Combine(Folder, FileName(now, suffix));

		if (File.Exists(path)) {
			path = Path.Combine(Folder, FileName(now, "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture) + suffix));
		}

		byte[] zip = Create();

		// Its own temporary name: two backups at once (the command and a restore's "before" copy) both wrote "<zip>.tmp",
		// and one moved the other's half-written file into place or failed finding it gone.
		string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
		string? claimed = null;

		try {
			// Owner-only from the first byte off Windows: a backup holds every saved login and config/state/secret.key, the
			// key that opens them. Written with the usual 0644, anyone else on the machine could read the lot.
			WriteOwnerOnly(tmp, zip);

			// And never over another backup: two finished in the same second each get a name of their own. Moved over one
			// another, the second was refused ("access denied") while the first was still landing. The name is claimed
			// first, by creating it: that fails for everyone but one, on every system. A move that refuses to overwrite
			// only checks and then renames off Windows, so two backups at once both "won" the same name and one was lost.
			for (int n = 2; ; n++) {
				try {
					WriteOwnerOnly(path, []);
					claimed = path;

					break;
				} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && (n < 100)) {
					// Any refusal means "taken", not only a file that's plainly there: on Windows a name another backup is
					// replacing at that instant is "access denied" and doesn't exist yet - which failed the whole backup.
					path = Path.Combine(Folder, FileName(now, "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture) + suffix));
				}
			}

			// Onto our own placeholder. Windows can refuse that for a moment while something else has the new file open
			// (an antivirus scan of a fresh zip, the search indexer), so a refusal is tried again for a few seconds.
			for (int attempt = 1; ; attempt++) {
				try {
					File.Move(tmp, path, overwrite: true);

					break;
				} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && (attempt < 20)) {
					Thread.Sleep(100 * attempt);
				}
			}
		} catch {
			foreach (string? leftover in new[] { tmp, claimed }) {
				try {
					if (leftover != null) {
						File.Delete(leftover);
					}
				} catch {
					// nothing more to do
				}
			}

			throw;
		}

		return path;
	}

	/// <summary>The README.txt that goes in every backup - what's inside and how to put it back, in plain words.</summary>
	internal static string Readme(Manifest m) {
		StringBuilder sb = new();
		sb.AppendLine("nocat.farm backup");
		sb.AppendLine("=================");
		sb.AppendLine();
		sb.AppendLine(CultureInfo.InvariantCulture, $"Made {m.Created.ToLocalTime():yyyy-MM-dd HH:mm} by nocat.farm {m.Version} on {m.Machine}.");
		sb.AppendLine(CultureInfo.InvariantCulture, $"Accounts: {(m.Accounts.Count == 0 ? "none" : string.Join(", ", m.Accounts))}");
		sb.AppendLine();
		sb.AppendLine("What's inside (all under config/):");
		sb.AppendLine("  nocatFarm.json            your settings");
		sb.AppendLine("  <account>.json            each account's settings");
		sb.AppendLine("  tokens/                   saved Steam logins, so accounts sign in without a password");
		sb.AppendLine("  authenticators/           Steam Guard authenticator files (.maFile), if you added any");
		sb.AppendLine("  state/history/            the day-by-day history behind the charts");
		sb.AppendLine("  state/lifetime*.json      hours banked in all");
		sb.AppendLine("  state/human-, hunt-,      today's human-mode plans, achievement hunt progress,");
		sb.AppendLine("  owner-, rotation-         what it learned from how you play, where the idle rotation is,");
		sb.AppendLine("  rep4rep-, keys, report    rep4rep counts, the key queue, the daily/weekly report");
		sb.AppendLine("  manifest.json             what this backup is, so nocat.farm knows it on restore");
		sb.AppendLine();
		sb.AppendLine("Not inside: logs, caches (prices, game names), and anything nocat.farm rebuilds by itself.");
		sb.AppendLine();
		sb.AppendLine("How to restore:");
		sb.AppendLine("  In the dashboard: Settings, Global settings, Backup & restore, Restore a backup - pick this zip.");
		sb.AppendLine("  It shows what's inside and asks first. Accounts stop, the files go back, and they start again.");
		sb.AppendLine("  Accounts that aren't in the backup are left alone. What it replaces is kept in backups/ first.");
		sb.AppendLine();
		sb.AppendLine("Passwords, login tokens and authenticator secrets are stored encrypted, as on disk:");

		if (m.Windows) {
			sb.AppendLine("  Made on Windows: they only open on the same PC, signed in as the same Windows user.");
			sb.AppendLine("  Restored anywhere else, your settings and history come back, but each account asks");
			sb.AppendLine("  for its password (and Steam Guard) once, and authenticators need adding again.");
		} else {
			sb.AppendLine("  Made off Windows: the key that opens them (state/secret.key) is in this zip too,");
			sb.AppendLine("  so it restores on any Linux/Mac/Docker install. Anyone with this zip has your logins.");
		}

		sb.AppendLine();
		sb.AppendLine("Keep this file private - it signs in to your Steam accounts.");

		return sb.ToString();
	}

	/// <summary>Check a zip before anything is written: ours, nothing outside config/, nothing unknown, nothing oversized.</summary>
	public static Inspection Inspect(byte[] bytes) => Inspect(bytes, out _);

	private static Inspection Inspect(byte[] bytes, out List<(string Rel, byte[] Data)> contents) {
		contents = [];
		List<(string Rel, byte[] Data)> found = [];
		Inspection result = new();

		if (bytes.LongLength > MaxZipBytes) {
			return Fail(new Said("that file is too big to be a nocat.farm backup"));
		}

		try {
			using ZipArchive zip = new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);

			if (zip.Entries.Count > MaxEntries) {
				return Fail(new Said("that zip has far too many files to be a nocat.farm backup"));
			}

			long total = 0;
			HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

			foreach (ZipArchiveEntry entry in zip.Entries) {
				string name = entry.FullName;

				// A folder entry carries nothing.
				if (name.EndsWith('/') && (entry.Length == 0)) {
					continue;
				}

				if (!SafeName(name)) {
					return Fail(new Said("refused: {0} points outside the config folder", name));
				}

				if (!seen.Add(name)) {
					return Fail(new Said("refused: {0} is in the zip twice", name));
				}

				if ((name is not (ManifestName or ReadmeName)) && (!name.StartsWith("config/", StringComparison.Ordinal) || (Kind(name["config/".Length..]) == null))) {
					return Fail(new Said("refused: {0} isn't something a nocat.farm backup holds", name));
				}

				// The size in the header is only what the zip claims - read no more than the limit whatever it says.
				byte[] data = ReadCapped(entry, MaxFileBytes);

				if (data.LongLength > MaxFileBytes) {
					return Fail(new Said("refused: {0} is too big", name));
				}

				total += data.LongLength;

				if (total > MaxTotalBytes) {
					return Fail(new Said("refused: the files in it add up to too much"));
				}

				if (name == ManifestName) {
					result.Manifest = JsonSerializer.Deserialize<Manifest>(data);
				} else if (name.StartsWith("config/", StringComparison.Ordinal)) {
					found.Add((name["config/".Length..], data));
				}
			}
		} catch (Exception e) when (e is InvalidDataException or JsonException or IOException or NotSupportedException) {
			Log.Failed("backup: reading a zip to restore", e);

			return Fail(new Said("that isn't a zip nocat.farm can read"));
		}

		if (result.Manifest is not { App: App } manifest) {
			return Fail(new Said("that isn't a nocat.farm backup - it has no manifest"));
		}

		if (manifest.Format > Format) {
			return Fail(new Said("that backup is from a newer nocat.farm ({0}) - update first", manifest.Version));
		}

		contents = found;
		result.Files = [.. found.Select(static c => c.Rel)];
		result.Accounts = AccountsIn(result.Files);
		result.SameMachine = string.Equals(manifest.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
			&& (manifest.Windows == OperatingSystem.IsWindows());

		return result;

		static Inspection Fail(Said why) {
			return new Inspection { Error = why.ToString() };
		}
	}

	/// <summary>A zip entry name that can only ever land inside the folder it's unpacked into.</summary>
	internal static bool SafeName(string name) =>
		(name.Length is > 0 and < 260)
		&& !name.StartsWith('/') && !name.Contains('\\', StringComparison.Ordinal) && !name.Contains(':', StringComparison.Ordinal)
		&& !name.Contains('\0', StringComparison.Ordinal)
		&& name.Split('/').All(static part => (part.Length > 0) && (part != ".") && (part != ".."));

	private static byte[] ReadCapped(ZipArchiveEntry entry, long cap) {
		using Stream s = entry.Open();
		using MemoryStream ms = new();
		byte[] buffer = new byte[81920];
		int read;

		while ((read = s.Read(buffer, 0, buffer.Length)) > 0) {
			ms.Write(buffer, 0, read);

			if (ms.Length > cap) {
				break;   // one byte over is enough to say no - never unpack the rest of a zip bomb
			}
		}

		return ms.ToArray();
	}

	/// <summary>
	/// Put a backup's files in place. Every path is checked twice - by name above, and here against where it really
	/// resolves to - so nothing is ever written outside config/. Nothing but files, and only files the zip has.
	/// </summary>
	internal static int WriteFiles(byte[] bytes) {
		Inspection check = Inspect(bytes, out List<(string Rel, byte[] Data)> contents);

		if (!check.Ok) {
			throw new InvalidDataException(check.Error);
		}

		string root = Path.GetFullPath(ConfigStore.ConfigDir) + Path.DirectorySeparatorChar;
		int written = 0;

		foreach ((string rel, byte[] data) in contents) {
			string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

			if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
				throw new InvalidDataException($"{rel} resolves outside the config folder");
			}

			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			string tmp = $"{full}.{Guid.NewGuid():N}.tmp";

			try {
				// Owner-only off Windows, like the files it puts back were: secret.key, the login tokens, the authenticators.
				WriteOwnerOnly(tmp, data);
				File.Move(tmp, full, overwrite: true);
			} catch {
				try {
					File.Delete(tmp);
				} catch {
					// nothing more to do
				}

				throw;
			}

			written++;
		}

		return written;
	}

	/// <summary>Every global secret that came back empty takes the value in use now. True when any did.</summary>
	internal static bool KeepSecrets(GlobalConfig restored, GlobalConfig current) {
		bool kept = false;

		foreach (SettingDef def in Settings.Global.Where(static d => d.Kind == SettingKind.Secret)) {
			System.Reflection.PropertyInfo? p = typeof(GlobalConfig).GetProperty(def.Name);

			if ((p?.GetValue(restored) is "" or null) && (p?.GetValue(current) is string { Length: > 0 } now)) {
				p.SetValue(restored, now);
				kept = true;
			}
		}

		return kept;
	}

	/// <summary>
	/// Restore a backup into the running app: every account stops, what's there now is kept as a backup of its own, the
	/// files go back, everything that holds them in memory reads them again, and the accounts start. The answer is one
	/// line for the dashboard.
	/// </summary>
	public static async Task<string> RestoreAsync(BotManager mgr, byte[] bytes) {
		Inspection check = Inspect(bytes);

		if (!check.Ok) {
			return check.Error!;
		}

		// One restore at a time: a second one staged and confirmed while the first was still writing mixed the two.
		await RestoreGate.WaitAsync().ConfigureAwait(false);

		try {
			Log.Info(new Said("restoring a backup from {0} - stopping every account", Stamp(check.Manifest!.Created.ToLocalTime())));
			await mgr.StopAllAsync(graceful: true).ConfigureAwait(false);

			bool wrote = false, replaced = false;

			try {
				string before;
				int written;
				List<SettingDef> changed = [];

				// Nothing but the restore saves a setting from here until everything has read the restored files again. A
				// dashboard save or a module's save in the meantime wrote the old settings over them - or its file move
				// collided with the restore's, and the restore stopped half way with every account stopped.
				using (ConfigStore.BeginRestore()) {
					// What's in memory goes to disk first, so it's in the "before" copy and nothing is left waiting to be saved
					// over the restored files later.
					BotManager.Flush();
					before = WriteToFolder(BeforeRestore);
					wrote = true;
					written = WriteFiles(bytes);

					// Everything that read these files once and kept them reads them again.
					Secrets.ForgetKey();
					Lifetime.Reload();
					History.Reload();
					DailyReport.Reload();
					WeeklyReport.Reload();
					KeyQueue.Reload();
					StuckWatch.Reset();

					// Swapped under the lock every change to the live settings takes, so a change made at the same moment
					// isn't put onto the settings being thrown away.
					lock (ConfigStore.GlobalEditGate) {
						GlobalConfig current = mgr.Global;
						GlobalConfig loaded = ConfigStore.LoadGlobal();

						if (!ConfigStore.GlobalBroken) {
							// A dashboard password or a bot token sealed on another PC opens as nothing here. Taken as it comes, a
							// restore from a phone would leave the dashboard with no password - local only - and lock that phone
							// out. So one the backup can't give (can't open, or never had) keeps the one set now.
							if (KeepSecrets(loaded, current)) {
								ConfigStore.SaveGlobal(loaded);
							}

							// The environment wins over a restored file as it does at every start: a backup from a PC has no
							// public address or trusted proxy, and taken as it came it switched a VPS's off until the next start.
							Platform.ApplyEnvironment(loaded);

							// Only what the restore changed sets anything off - start with Windows, keep awake, the log. Running
							// them all would rewrite the startup entry and the rest for settings that came back exactly as they were.
							changed = [.. Settings.Global.Where(d => !Equals(Settings.Show(current, d), Settings.Show(loaded, d)))];
							mgr.ApplyGlobal(loaded);
						}
					}

					await mgr.ReplaceAllFromDiskAsync().ConfigureAwait(false);
					replaced = true;
				}

				foreach (SettingDef def in changed) {
					Commands.ApplyGlobalSideEffects(mgr, def);
				}

				Log.Good(new Said("backup restored: {0} file(s) - what was there before is in {1}", written, before));

				return new Said("Restored {0} file(s). The accounts are starting again.", written).ToString();
			} finally {
				// Whatever happened, the accounts come back: a restore that failed half way used to leave every one stopped.
				// Files that did go back are read first, so what runs is what's on disk.
				if (wrote && !replaced) {
					try {
						await mgr.ReplaceAllFromDiskAsync().ConfigureAwait(false);
					} catch (Exception e) {
						Log.Failed("restore: reading the accounts again after it failed", e);
					}
				}

				_ = mgr.StartAllAsync();
			}
		} finally {
			RestoreGate.Release();
		}
	}

	private static readonly SemaphoreSlim RestoreGate = new(1, 1);
}
