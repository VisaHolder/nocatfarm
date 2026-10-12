using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The daily backup that opens on any PC: the same files as the 'backup' zip (<see cref="Backup"/>), but with the saved
/// logins and other secrets opened here and the whole thing locked with a password the owner picks - so a dead PC is a
/// new one, the file and the password, and the accounts sign straight back in.
/// </summary>
/// <remarks>
/// <para>The 'backup' zip keeps the secrets as they are on disk: sealed to this Windows user (DPAPI), or with the key
/// file beside them off Windows. Neither carries to another PC. Here every one of them is opened before it goes in, and
/// the whole archive is encrypted instead: AES-256-GCM, its key from the password by PBKDF2-HMAC-SHA256 with 600,000
/// rounds and a random salt. On the way back in, each is sealed again with the new PC's own key.</para>
///
/// <para>The file: <c>NOCATBAK</c>, a format byte (1), a key-derivation byte (1 = PBKDF2-SHA256), the rounds (4 bytes,
/// big-endian), the salt (16), the nonce (12), a password check (16), then the encrypted archive and its tag (16). All
/// of the header is bound into the encryption, so none of it can be changed. The password check is a second 16 bytes
/// from the same derivation: it tells a wrong password from a file that was changed, and makes guessing no cheaper -
/// a guess still costs the 600,000 rounds. A later version reads the format byte first and knows what follows.</para>
///
/// <para>The password is never chosen for anyone, never logged, and kept on this PC encrypted like every other secret
/// (<see cref="GlobalConfig.BackupPassword"/>) so the daily backup can run with nobody there.</para>
/// </remarks>
public static partial class LockedBackup {
	public const int MinPassword = 12;
	public const string Extension = ".nfbackup";

	/// <summary>The rounds every new backup is made with. OWASP's figure for PBKDF2-HMAC-SHA256 (2023).</summary>
	public const int Rounds = 600_000;

	/// <summary>What a backup to be opened may say it used - fewer is no protection, more is somebody making the PC work for nothing.</summary>
	public const int MinRounds = 100_000;
	public const int MaxRounds = 10_000_000;

	public const byte FormatNow = 1;
	public const byte KdfPbkdf2Sha256 = 1;
	private static readonly byte[] Magic = "NOCATBAK"u8.ToArray();
	private const int SaltBytes = 16, NonceBytes = 12, CheckBytes = 16, TagBytes = 16;
	public const int HeaderBytes = 8 + 1 + 1 + 4 + SaltBytes + NonceBytes + CheckBytes;

	/// <summary>A locked backup can be a little bigger than the zip inside: the header and tag.</summary>
	public const long MaxFileBytes = Backup.MaxZipBytes + HeaderBytes + TagBytes;

	/// <summary>The newest log files that go in, when logs are picked.</summary>
	public const int LogDays = 7;

	public static bool IsLocked(ReadOnlySpan<byte> bytes) => (bytes.Length >= Magic.Length) && bytes[..Magic.Length].SequenceEqual(Magic);

	// ── the lock ────────────────────────────────────────────────────────────
	private static (byte[] Key, byte[] Check) Derive(string password, byte[] salt, int rounds) {
		byte[] both = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32 + CheckBytes);

		return (both[..32], both[32..]);
	}

	/// <summary>The archive locked with the password. Salt and nonce are new every time; the parameters are for the checks.</summary>
	public static byte[] Lock(byte[] plain, string password, int rounds = Rounds, byte[]? salt = null, byte[]? nonce = null) {
		salt ??= RandomNumberGenerator.GetBytes(SaltBytes);
		nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
		(byte[] key, byte[] check) = Derive(password, salt, rounds);

		byte[] file = new byte[HeaderBytes + plain.Length + TagBytes];
		Span<byte> head = file.AsSpan(0, HeaderBytes);
		Magic.CopyTo(head);
		head[8] = FormatNow;
		head[9] = KdfPbkdf2Sha256;
		BinaryPrimitives.WriteInt32BigEndian(head[10..14], rounds);
		salt.CopyTo(head[14..]);
		nonce.CopyTo(head[(14 + SaltBytes)..]);
		check.CopyTo(head[(14 + SaltBytes + NonceBytes)..]);

		using AesGcm aes = new(key, TagBytes);
		aes.Encrypt(nonce, plain, file.AsSpan(HeaderBytes, plain.Length), file.AsSpan(HeaderBytes + plain.Length, TagBytes), head);
		CryptographicOperations.ZeroMemory(key);

		return file;
	}

	public enum Problem { None, NotOurs, Newer, WrongPassword, Damaged }

	/// <summary>The archive inside, or null and why not: not a locked backup, from a newer version, the wrong password, or changed since.</summary>
	public static byte[]? Unlock(byte[] file, string password, out Problem problem) {
		problem = Problem.None;

		if (!IsLocked(file)) {
			problem = Problem.NotOurs;

			return null;
		}

		if ((file.Length < HeaderBytes + TagBytes) || (file.LongLength > MaxFileBytes)) {
			problem = Problem.Damaged;

			return null;
		}

		ReadOnlySpan<byte> head = file.AsSpan(0, HeaderBytes);

		if ((head[8] > FormatNow) || (head[9] != KdfPbkdf2Sha256)) {
			problem = head[8] > FormatNow ? Problem.Newer : Problem.Damaged;

			return null;
		}

		int rounds = BinaryPrimitives.ReadInt32BigEndian(head[10..14]);

		if (rounds is < MinRounds or > MaxRounds) {
			problem = Problem.Damaged;

			return null;
		}

		byte[] salt = head.Slice(14, SaltBytes).ToArray();
		byte[] nonce = head.Slice(14 + SaltBytes, NonceBytes).ToArray();
		ReadOnlySpan<byte> check = head.Slice(14 + SaltBytes + NonceBytes, CheckBytes);
		(byte[] key, byte[] expected) = Derive(password, salt, rounds);

		try {
			if (!CryptographicOperations.FixedTimeEquals(expected, check)) {
				problem = Problem.WrongPassword;

				return null;
			}

			int length = file.Length - HeaderBytes - TagBytes;
			byte[] plain = new byte[length];

			using AesGcm aes = new(key, TagBytes);
			aes.Decrypt(nonce, file.AsSpan(HeaderBytes, length), file.AsSpan(HeaderBytes + length, TagBytes), plain, head);

			return plain;
		} catch (CryptographicException) {
			// The right password and a tag that doesn't match: something in the file changed after it was made.
			problem = Problem.Damaged;

			return null;
		} finally {
			CryptographicOperations.ZeroMemory(key);
		}
	}

	public static Said Says(Problem p) => p switch {
		Problem.NotOurs => new Said("that isn't a nocat.farm backup"),
		Problem.Newer => new Said("that backup is from a newer nocat.farm - update first"),
		Problem.WrongPassword => new Said("wrong backup password"),
		_ => new Said("that backup file has been changed or damaged since it was made, so it can't be trusted")
	};

	// ── what goes in ────────────────────────────────────────────────────────
	/// <summary>What a backup holds, picked under Settings, Backups.</summary>
	public sealed record Parts(bool Settings, bool Accounts, bool Authenticators, bool History, bool Logs) {
		public static Parts From(GlobalConfig g) => new(g.BackupIncludeSettings, g.BackupIncludeAccounts, g.BackupIncludeAuthenticators, g.BackupIncludeHistory, g.BackupIncludeLogs);

		public bool Any => Settings || Accounts || Authenticators || History || Logs;

		public List<string> Names() => [.. new[] { (Settings, "settings"), (Accounts, "accounts"), (Authenticators, "authenticators"), (History, "history"), (Logs, "logs") }
			.Where(static p => p.Item1).Select(static p => p.Item2)];
	}

	/// <summary>
	/// Which part a file of the 'backup' zip's list belongs to - or null for the one never put in a locked backup, the key
	/// file (config/state/secret.key): every secret goes in opened, and the new PC seals them with its own.
	/// </summary>
	public static string? PartOf(string rel) => Backup.Kind(rel) switch {
		"settings" => "settings",
		"account" or "login token" => "accounts",
		"authenticator" => "authenticators",
		"history" => "history",
		"state" when HistoryState().IsMatch(rel) => "history",
		"state" => "accounts",
		_ => null
	};

	[GeneratedRegex(@"^state/(?:lifetime|lifetime-games|report|weekly-report)\.json\z", RegexOptions.CultureInvariant)]
	private static partial Regex HistoryState();

	[GeneratedRegex(@"^logs/nocatFarm-\d{4}-\d{2}-\d{2}\.log\z", RegexOptions.CultureInvariant)]
	private static partial Regex LogName();

	public sealed class Manifest {
		public string App { get; set; } = "";
		public int Format { get; set; }
		public string Version { get; set; } = "";
		public DateTime Created { get; set; }
		public string Machine { get; set; } = "";
		public List<string> Accounts { get; set; } = [];
		public List<string> Parts { get; set; } = [];

		/// <summary>
		/// The files whose secrets were opened to go in, and which: "*" for a whole file (a login token, an authenticator),
		/// or the names of the settings in a .json (an account's password). Exactly these are sealed again on restore.
		/// </summary>
		public Dictionary<string, List<string>> Opened { get; set; } = [];
		public int Files { get; set; }
	}

	private static bool Sealed(string? s) => !string.IsNullOrEmpty(s) && !Secrets.IsPlain(s);

	/// <summary>A file's bytes with its secrets opened, and which they were. Unopened: a secret this PC can't open goes in as it is.</summary>
	private static (byte[] Data, List<string>? Opened, int Unopened) OpenSecrets(string rel, byte[] data) {
		string text = Encoding.UTF8.GetString(data).TrimStart('﻿');

		if (Sealed(text.Trim())) {
			string plain = Secrets.Unprotect(text.Trim());

			return plain.Length > 0 ? (Encoding.UTF8.GetBytes(plain), ["*"], 0) : (data, null, 1);
		}

		if (!rel.EndsWith(".json", StringComparison.Ordinal) || !text.TrimStart().StartsWith('{')) {
			return (data, null, 0);
		}

		JsonObject? obj;

		try {
			obj = JsonNode.Parse(text) as JsonObject;
		} catch (JsonException) {
			return (data, null, 0);
		}

		if (obj == null) {
			return (data, null, 0);
		}

		List<string> opened = [];
		int unopened = 0;

		foreach ((string name, JsonNode? value) in obj.ToList()) {
			if ((value is JsonValue v) && v.TryGetValue(out string? s) && Sealed(s)) {
				string plain = Secrets.Unprotect(s!);

				if (plain.Length > 0) {
					obj[name] = plain;
					opened.Add(name);
				} else {
					unopened++;
				}
			}
		}

		return opened.Count == 0 ? (data, null, unopened) : (Encoding.UTF8.GetBytes(obj.ToJsonString(Pretty)), opened, unopened);
	}

	private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

	/// <summary>Room kept for the manifest and the zip's own bookkeeping when deciding whether one more log still fits.</summary>
	private const long Spare = 256 * 1024;

	/// <summary>
	/// The archive that goes inside a locked backup, made now from what's on disk: the picked parts, every secret opened.
	/// Also how many secrets this PC couldn't open (they go in as they are, and won't open anywhere else either), and how
	/// many log files were left out. It always fits what a restore takes (<see cref="Open"/>): logs stop going in before
	/// it would be too big, and anything else too big is <see cref="Failed"/> - a backup that can't be restored is no backup.
	/// </summary>
	public static (byte[] Zip, int Unopened, int LogsLeftOut) Pack(Parts parts) {
		Manifest manifest = new() {
			App = Backup.App, Format = FormatNow, Version = Build.Version, Created = DateTime.UtcNow, Machine = Environment.MachineName,
			Parts = parts.Names()
		};
		int unopened = 0, leftOut = 0, entries = 1;
		long unpacked = 0;

		using MemoryStream ms = new();

		using (ZipArchive zip = new(ms, ZipArchiveMode.Create, leaveOpen: true)) {
			foreach ((string rel, string full) in Backup.Files()) {
				string? part = PartOf(rel);

				if ((part == null) || !manifest.Parts.Contains(part)) {
					continue;
				}

				byte[] data;

				try {
					data = ReadShared(full);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed($"backup: reading {rel}", e);

					continue;
				}

				(byte[] bytes, List<string>? opened, int missed) = OpenSecrets(rel, data);
				unopened += missed;

				if (opened != null) {
					manifest.Opened[rel] = opened;
				}

				if (Backup.Kind(rel) == "account") {
					manifest.Accounts.Add(rel[..^".json".Length]);
				}

				if (bytes.LongLength > Backup.MaxFileBytes) {
					throw new Failed(new Said("{0} is too big to go in a backup (over {1} MB), so the backup couldn't be restored", rel, Backup.MaxFileBytes / (1024 * 1024)));
				}

				Put(zip, "config/" + rel, bytes);
				manifest.Files++;
				entries++;
				unpacked += bytes.LongLength;
			}

			if ((entries > Backup.MaxEntries) || (unpacked + Spare > Backup.MaxTotalBytes) || (ms.Length + Spare > Backup.MaxZipBytes)) {
				throw new Failed(new Said("the backup would be too big to restore - pick less to go in under Settings, Backups"));
			}

			if (parts.Logs) {
				List<string> logs = RecentLogs();

				for (int i = 0; i < logs.Count; i++) {
					byte[] data;

					try {
						data = ReadShared(logs[i]);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						Log.Failed($"backup: reading {Path.GetFileName(logs[i])}", e);

						continue;
					}

					// Newest first: once one more would make it too big to restore, it and the older ones stay out.
					if ((data.LongLength > Backup.MaxFileBytes) || (entries + 1 > Backup.MaxEntries) || (unpacked + data.LongLength + Spare > Backup.MaxTotalBytes)
						|| (ms.Length + Packed(data) + Spare > Backup.MaxZipBytes)) {
						leftOut = logs.Count - i;

						break;
					}

					Put(zip, "logs/" + Path.GetFileName(logs[i]), data);
					entries++;
					unpacked += data.LongLength;
				}
			}

			Put(zip, Backup.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, Pretty));
		}

		// The last word: never one a restore would turn away.
		if (ms.Length > Backup.MaxZipBytes) {
			throw new Failed(new Said("the backup would be too big to restore - pick less to go in under Settings, Backups"));
		}

		return (ms.ToArray(), unopened, leftOut);

		// How big a log is once squeezed, the way it goes in.
		static long Packed(byte[] data) {
			using MemoryStream counted = new();

			using (DeflateStream d = new(counted, CompressionLevel.Optimal, leaveOpen: true)) {
				d.Write(data);
			}

			return counted.Length;
		}

		static void Put(ZipArchive zip, string name, byte[] data) {
			using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
			s.Write(data);
		}
	}

	/// <summary>The newest few days of log files, none over the size one file in a backup may be.</summary>
	private static List<string> RecentLogs() {
		string dir = Path.Combine(ConfigStore.Root, "logs");

		if (!Directory.Exists(dir)) {
			return [];
		}

		return [.. Directory.GetFiles(dir, "nocatFarm-*.log")
			.Where(f => LogName().IsMatch("logs/" + Path.GetFileName(f)) && (new FileInfo(f).Length <= Backup.MaxFileBytes))
			.OrderByDescending(static f => Path.GetFileName(f), StringComparer.Ordinal)
			.Take(LogDays)];
	}

	private static byte[] ReadShared(string path) {
		using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using MemoryStream ms = new();
		fs.CopyTo(ms);

		return ms.ToArray();
	}

	// ── opening one ─────────────────────────────────────────────────────────
	/// <summary>A locked backup opened with its password, ready for the usual two-step restore - or why it can't be.</summary>
	public sealed class Opened {
		public string? Error { get; set; }
		public Problem Problem { get; set; }
		public Manifest? Manifest { get; set; }

		/// <summary>A 'backup' zip of the same files, every secret sealed again with this PC's key: what <see cref="Backup.RestoreAsync"/> takes.</summary>
		public byte[]? Zip { get; set; }

		/// <summary>The log files in it, put back by <see cref="PutBackLogs"/> - never over one that's there.</summary>
		public List<(string Name, byte[] Data)> Logs { get; set; } = [];

		public bool Ok => Error == null;
	}

	/// <summary>
	/// Open a locked backup: the password, then every entry checked as strictly as a 'backup' zip's (a name of ours, inside
	/// config/, no bigger than it should be), then the secrets sealed again for this PC. Nothing is written anywhere.
	/// </summary>
	public static Opened Open(byte[] file, string password) {
		byte[]? inner = Unlock(file, password ?? "", out Problem problem);

		if (inner == null) {
			return new Opened { Error = Says(problem).ToString(), Problem = problem };
		}

		Opened result = new();
		Manifest? manifest = null;
		List<(string Rel, byte[] Data)> files = [];

		try {
			using ZipArchive zip = new(new MemoryStream(inner, writable: false), ZipArchiveMode.Read);

			if (zip.Entries.Count > Backup.MaxEntries) {
				return Fail(new Said("that backup has far too many files in it"));
			}

			long total = 0;
			HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

			foreach (ZipArchiveEntry entry in zip.Entries) {
				string name = entry.FullName;

				if (name.EndsWith('/') && (entry.Length == 0)) {
					continue;
				}

				bool config = name.StartsWith("config/", StringComparison.Ordinal);

				// Nothing but our own names: a settings file, the account files and state the 'backup' zip knows (never the key
				// file - a locked backup never has one), a log file, the manifest.
				if (!Backup.SafeName(name) || !seen.Add(name)
					|| !((name == Backup.ManifestName) || (config && (PartOf(name["config/".Length..]) != null)) || LogName().IsMatch(name))) {
					return Fail(new Said("refused: {0} isn't something a nocat.farm backup holds", name));
				}

				byte[] data = ReadCapped(entry, Backup.MaxFileBytes);
				total += data.LongLength;

				if ((data.LongLength > Backup.MaxFileBytes) || (total > Backup.MaxTotalBytes)) {
					return Fail(new Said("refused: {0} is too big", name));
				}

				if (name == Backup.ManifestName) {
					manifest = JsonSerializer.Deserialize<Manifest>(data);
				} else if (config) {
					files.Add((name["config/".Length..], data));
				} else {
					result.Logs.Add((name["logs/".Length..], data));
				}
			}
		} catch (Exception e) when (e is InvalidDataException or JsonException or IOException or NotSupportedException) {
			Log.Failed("backup: reading a locked backup", e);

			return Fail(new Said("that backup file has been changed or damaged since it was made, so it can't be trusted"));
		}

		if (manifest is not { App: Backup.App }) {
			return Fail(new Said("that isn't a nocat.farm backup - it has no manifest"));
		}

		if (manifest.Format > FormatNow) {
			return Fail(Says(Problem.Newer));
		}

		// Sealed again for this PC, then handed on as an ordinary 'backup' zip made here.
		Backup.Manifest local = new() {
			App = Backup.App, Format = Backup.Format, Version = manifest.Version, Created = manifest.Created,
			Machine = Environment.MachineName, Windows = OperatingSystem.IsWindows(), Accounts = [.. manifest.Accounts]
		};

		using MemoryStream ms = new();

		using (ZipArchive zip = new(ms, ZipArchiveMode.Create, leaveOpen: true)) {
			foreach ((string rel, byte[] data) in files) {
				byte[] bytes = manifest.Opened.TryGetValue(rel, out List<string>? which) ? SealSecrets(rel, data, which) : data;
				using Stream s = zip.CreateEntry("config/" + rel, CompressionLevel.Optimal).Open();
				s.Write(bytes);
				local.Files++;
			}

			using Stream m = zip.CreateEntry(Backup.ManifestName, CompressionLevel.Optimal).Open();
			m.Write(JsonSerializer.SerializeToUtf8Bytes(local, Pretty));
		}

		result.Manifest = manifest;
		result.Zip = ms.ToArray();

		return result;

		Opened Fail(Said why) => new() { Error = why.ToString(), Problem = Problem.Damaged };
	}

	/// <summary>A file's opened secrets sealed again with this PC's own key - the whole file, or the named settings in it.</summary>
	private static byte[] SealSecrets(string rel, byte[] data, List<string> which) {
		string owner = Backup.Kind(rel) == "settings" ? "global" : Path.GetFileNameWithoutExtension(rel);
		string text = Encoding.UTF8.GetString(data).TrimStart('﻿');

		if (which.Contains("*")) {
			return Encoding.UTF8.GetBytes(Secrets.Protect(text, owner));
		}

		if (JsonNode.Parse(text) is not JsonObject obj) {
			return data;
		}

		foreach (string name in which) {
			if ((obj[name] is JsonValue v) && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)) {
				obj[name] = Secrets.Protect(s, owner);
			}
		}

		return Encoding.UTF8.GetBytes(obj.ToJsonString(Pretty));
	}

	private static byte[] ReadCapped(ZipArchiveEntry entry, long cap) {
		using Stream s = entry.Open();
		using MemoryStream ms = new();
		byte[] buffer = new byte[81920];
		int read;

		while ((read = s.Read(buffer, 0, buffer.Length)) > 0) {
			ms.Write(buffer, 0, read);

			if (ms.Length > cap) {
				break;
			}
		}

		return ms.ToArray();
	}

	/// <summary>A restored backup's log files, into the logs folder - only ones that aren't there already. How many.</summary>
	public static int PutBackLogs(IEnumerable<(string Name, byte[] Data)> logs) {
		string dir = Path.Combine(ConfigStore.Root, "logs");
		int put = 0;

		foreach ((string name, byte[] data) in logs) {
			if (!LogName().IsMatch("logs/" + name)) {
				continue;
			}

			string path = Path.Combine(dir, name);

			try {
				Directory.CreateDirectory(dir);

				if (!File.Exists(path)) {
					Backup.WriteOwnerOnly(path, data);
					put++;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed($"restore: putting back {name}", e);
			}
		}

		return put;
	}

	// ── where they go ───────────────────────────────────────────────────────
	/// <summary>nocat.farm-backup-2026-10-11.nfbackup - the date it was made, local time.</summary>
	public static string FileName(DateTime local, string suffix = "") =>
		$"nocat.farm-backup-{local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{suffix}{Extension}";

	[GeneratedRegex(@"^nocat\.farm-backup-\d{4}-\d{2}-\d{2}(?:-\d{6}(?:-\d+)?)?\.nfbackup\z", RegexOptions.CultureInvariant)]
	private static partial Regex OurName();

	/// <summary>Whether a file name is one of ours - the only files the keep count ever deletes.</summary>
	public static bool IsOurs(string fileName) => OurName().IsMatch(fileName);

	/// <summary>
	/// The folder the daily backup goes in: as set (with %OneDrive% or ~ worked out), a relative one under the app's own folder,
	/// or the backups folder next to config when it's empty.
	/// </summary>
	public static string Folder(GlobalConfig g) {
		string set = (g.BackupFolder ?? "").Trim().Trim('"');

		if (set.Length == 0) {
			return Backup.Folder;
		}

		set = Environment.ExpandEnvironmentVariables(set);

		if ((set == "~") || set.StartsWith("~/", StringComparison.Ordinal)) {
			set = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), set.TrimStart('~').TrimStart('/'));
		}

		return Path.GetFullPath(Path.IsPathFullyQualified(set) ? set : Path.Combine(ConfigStore.Root, set));
	}

	/// <summary>Why a backup couldn't be made - said as it is, in the log and the notification.</summary>
	public sealed class Failed(Said why) : Exception(why.ToEnglish()) {
		public Said Why { get; } = why;
	}

	/// <summary>
	/// Make a locked backup now, into the backup folder (and the copy here, when that's on), and keep only the newest
	/// <see cref="GlobalConfig.BackupKeep"/> of ours in each. The path written and how many log files were left out to
	/// keep it restorable, or <see cref="Failed"/> saying why not.
	/// </summary>
	public static Written WriteNow(GlobalConfig g, DateTime now) {
		string password = g.BackupPassword ?? "";

		if (password.Length < MinPassword) {
			throw new Failed(new Said("no backup password set - pick one under Settings, Backups"));
		}

		Parts parts = Parts.From(g);

		if (!parts.Any) {
			throw new Failed(new Said("nothing is picked to go in the backup - see Settings, Backups"));
		}

		string folder = Folder(g);

		try {
			BotManager.Flush();   // what's in memory goes to disk first, so it's in the backup
		} catch (Exception e) {
			Log.Failed("backup: saving what's in memory first", e);
		}

		(byte[] zip, int unopened, int logsLeftOut) = Pack(parts);
		byte[] locked = Lock(zip, password);

		if (unopened > 0) {
			Log.Info(new Said("backup: {0} saved secret(s) couldn't be opened on this PC - they go in as they are and will be asked for again after a restore", unopened));
		}

		if (logsLeftOut > 0) {
			Log.Warn(new Said("backup: {0} older log file(s) left out, so the backup stays small enough to restore", logsLeftOut));
		}

		string path = WriteInto(folder, locked, now, mustExist: !string.Equals(folder, Backup.Folder, StringComparison.OrdinalIgnoreCase));
		Prune(folder, g.BackupKeep);

		if (g.BackupLocalCopy && !string.Equals(folder, Backup.Folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
			try {
				WriteInto(Backup.Folder, locked, now, mustExist: false);
				Prune(Backup.Folder, g.BackupKeep);
			} catch (Failed f) {
				Log.Warn(new Said("backup: the copy next to nocat.farm wasn't saved - {0}", f.Why));
			}
		}

		return new Written(path, logsLeftOut);
	}

	/// <summary>A locked backup just written: where, and how many log files were left out to keep it restorable.</summary>
	public sealed record Written(string Path, int LogsLeftOut);

	/// <summary>One locked backup into a folder, under a name nothing else has, whole or not at all.</summary>
	private static string WriteInto(string folder, byte[] locked, DateTime now, bool mustExist) {
		string? tmp = null;

		try {
			if (!Directory.Exists(folder)) {
				// A folder of its own inside one that's there is made; one on a drive that isn't plugged in, or under a
				// OneDrive that isn't there, is a failure to hear about - not a folder quietly made somewhere else.
				string? parent = Path.GetDirectoryName(folder);

				if (mustExist && ((parent == null) || !Directory.Exists(parent))) {
					throw new Failed(new Said("the backup folder {0} isn't there - is the drive plugged in?", folder));
				}
			}

			if (TightensFolder(folder)) {
				Backup.OwnerOnlyFolder(folder);
			}

			string path = Path.Combine(folder, FileName(now));

			for (int n = 1; File.Exists(path); n++) {
				string suffix = "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture) + (n > 1 ? "-" + n.ToString(CultureInfo.InvariantCulture) : "");
				path = Path.Combine(folder, FileName(now, suffix));
			}

			tmp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
			Backup.WriteOwnerOnly(tmp, locked);
			File.Move(tmp, path, overwrite: false);
			tmp = null;

			return path;
		} catch (Failed) {
			throw;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) {
			throw new Failed(Reason(e, folder));
		} finally {
			if (tmp != null) {
				try {
					File.Delete(tmp);
				} catch {
					// nothing more to do
				}
			}
		}
	}

	/// <summary>
	/// Whether a backup folder is made owner-only (0700 off Windows): one nocat.farm makes itself, and its own backups
	/// folder. A folder of the owner's that's already there - a Dropbox, a NAS share - is left as it is; the backup files
	/// in it are owner-only all the same.
	/// </summary>
	public static bool TightensFolder(string folder) =>
		!Directory.Exists(folder)
		|| string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(Backup.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	/// <summary>Why writing failed, in plain words: the folder gone, the disk full, no permission - or what the system said.</summary>
	public static Said Reason(Exception e, string folder) {
		const int DiskFull = unchecked((int) 0x80070070), HandleDiskFull = unchecked((int) 0x80070027), NoSpace = 28;

		return e switch {
			DirectoryNotFoundException or DriveNotFoundException => new Said("the backup folder {0} isn't there - is the drive plugged in?", folder),
			UnauthorizedAccessException => new Said("no permission to write in {0}", folder),
			IOException io when (io.HResult is DiskFull or HandleDiskFull or NoSpace) || io.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
				=> new Said("the disk with {0} is full", folder),
			_ => new Said("couldn't write in {0}: {1}", folder, Log.Describe(e))
		};
	}

	/// <summary>Our backups in a folder past the newest <paramref name="keep"/> are deleted - never any other file. How many went.</summary>
	public static int Prune(string folder, int keep) {
		keep = Math.Max(1, keep);
		int gone = 0;

		try {
			List<FileInfo> ours = [.. new DirectoryInfo(folder).GetFiles("*" + Extension).Where(static f => IsOurs(f.Name))
				.OrderByDescending(static f => f.LastWriteTimeUtc).ThenByDescending(static f => f.Name, StringComparer.Ordinal)];

			foreach (FileInfo old in ours.Skip(keep)) {
				try {
					old.Delete();
					gone++;
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed($"backup: deleting the old backup {old.Name}", e);
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("backup: looking for old backups to delete", e);
		}

		return gone;
	}
}
