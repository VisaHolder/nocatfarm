using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Encryption at rest for the things that ARE the account: login tokens, the access token, stored passwords.
///
/// A Steam refresh token is not a convenience - it is a credential. Anything holding one can sign in as that
/// account without the password and without a Steam Guard code, which is exactly why they were the one thing in
/// here worth protecting and the one thing sitting in plain text.
///
/// Windows does this properly with DPAPI: the ciphertext is bound to the user account, so a copied file is
/// useless on another machine or under another user, and there is no key for us to store badly.
///
/// Linux (and Docker) has no equivalent every install can count on, so there it is AES-256-GCM with a random key
/// kept in config/state/secret.key, readable by its owner only (chmod 600). Be clear about what that buys: the
/// key sits in the same folder as what it protects, so anyone who can read the WHOLE config folder can read the
/// secrets too. What it does stop is the everyday leak - one account's .json pasted into a chat, a tokens folder
/// synced or backed up on its own, another user on the same machine reading files left world-readable.
///
/// Every stored value says which scheme wrote it: "nocat1:" is DPAPI, "nocat-aes1:" is AES-GCM, and anything
/// without a prefix is plain text. Reading is deliberately tolerant: an unencrypted file left by an older version
/// is read as-is and rewritten encrypted the next time it is saved, so upgrading takes no migration step and
/// nobody gets logged out. A value the other platform wrote reads as empty and is simply asked for again.
/// </summary>
public static class Secrets {
	/// <summary>DPAPI (Windows). Unchanged since the first version, so every file ever written on Windows still reads.</summary>
	private const string Marker = "nocat1:";

	/// <summary>AES-256-GCM with the key file (Linux, Docker). Stored as the marker + base64(nonce | tag | ciphertext).</summary>
	private const string AesMarker = "nocat-aes1:";

	private const int KeyBytes = 32;
	private const int NonceBytes = 12;
	private const int TagBytes = 16;

	/// <summary>Bound into every AES value, so a ciphertext can't be passed off as belonging to a later scheme.</summary>
	private static readonly byte[] Context = Encoding.ASCII.GetBytes(AesMarker);

	private static readonly Lock KeyGate = new();
	private static byte[]? _key;
	private static string _keyProblem = "";
	private static bool _warned;
	private static bool _warnedForeign;

	/// <summary>Where the AES key lives. Only ever made off Windows; Windows only reads it, to open a copied-over config.</summary>
	public static string KeyPath => Path.Combine(ConfigStore.ConfigDir, "state", "secret.key");

	public static bool Available => OperatingSystem.IsWindows() || (Key(create: true) != null);

	/// <summary>
	/// A stored value that is plain text - neither scheme's marker. Whoever reads one rewrites it encrypted straight
	/// away: waiting for "the next save" left Steam login tokens from before encryption existed in the clear for
	/// months, because a token that doesn't change is never saved again.
	/// </summary>
	public static bool IsPlain(string? stored) =>
		!string.IsNullOrEmpty(stored) && !stored.StartsWith(Marker, StringComparison.Ordinal) && !stored.StartsWith(AesMarker, StringComparison.Ordinal);

	/// <summary>Encrypt for storage. Falls back to the plain text where the platform can't do better.</summary>
	public static string Protect(string plain, string forBot) {
		if (string.IsNullOrEmpty(plain)) {
			return plain;
		}

		try {
			if (OperatingSystem.IsWindows()) {
				return Marker + Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(plain)));
			}

			byte[]? key = Key(create: true);

			if (key == null) {
				WarnOnce(forBot);

				return plain;
			}

			return AesMarker + Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(plain)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't encrypt a stored secret ({0}) - keeping it as it is", Log.Describe(e)), forBot);

			return plain;
		}
	}

	/// <summary>Decrypt something Protect wrote. Anything else is handed straight back.</summary>
	public static string Unprotect(string stored) {
		if (string.IsNullOrEmpty(stored)) {
			return stored;
		}

		if (stored.StartsWith(AesMarker, StringComparison.Ordinal)) {
			return UnprotectAes(stored);
		}

		if (!stored.StartsWith(Marker, StringComparison.Ordinal)) {
			return stored;   // plain text from an older version, or typed into the file by hand
		}

		if (!OperatingSystem.IsWindows()) {
			// Written by Windows' DPAPI, being read somewhere else - it cannot be recovered here.
			if (!_warnedForeign) {
				_warnedForeign = true;
				Log.Warn(new Said("Windows-encrypted logins can't be read here - they sign in again"));
			}

			return "";
		}

		try {
			return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(stored[Marker.Length..])));
		} catch (Exception e) {
			// A different Windows user, a restored profile, or a corrupted file. Treat it as absent: the account
			// signs in again with its password, which is a nuisance rather than a failure.
			Log.Debug(new Said("a stored secret couldn't be decrypted ({0}) - it will be asked for again", Log.Describe(e)));

			return "";
		}
	}

	private static string UnprotectAes(string stored) {
		// Never made here on Windows - only read, for a config folder brought over from Linux with its key.
		byte[]? key = Key(create: false);

		if (key == null) {
			Log.Debug(new Said("a stored secret needs the key in {0}, which is missing - it will be asked for again", KeyPath));

			return "";
		}

		try {
			return Encoding.UTF8.GetString(Open(key, Convert.FromBase64String(stored[AesMarker.Length..])));
		} catch (Exception e) {
			// A key from another install, or a value damaged by hand. Same answer as DPAPI: ask again.
			Log.Debug(new Said("a stored secret couldn't be decrypted ({0}) - it will be asked for again", Log.Describe(e)));

			return "";
		}
	}

	[SupportedOSPlatform("windows")]
	private static byte[] Encrypt(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

	[SupportedOSPlatform("windows")]
	private static byte[] Decrypt(byte[] cipher) => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);

	// ── AES-GCM ─────────────────────────────────────────────────────────────
	private static byte[] Seal(byte[] key, byte[] plain) {
		byte[] box = new byte[NonceBytes + TagBytes + plain.Length];
		Span<byte> nonce = box.AsSpan(0, NonceBytes);
		RandomNumberGenerator.Fill(nonce);   // random per value: a repeated nonce under one key breaks GCM outright

		using AesGcm aes = new(key, TagBytes);
		aes.Encrypt(nonce, plain, box.AsSpan(NonceBytes + TagBytes), box.AsSpan(NonceBytes, TagBytes), Context);

		return box;
	}

	private static byte[] Open(byte[] key, byte[] box) {
		if (box.Length < NonceBytes + TagBytes) {
			throw new CryptographicException("too short to be an encrypted value");
		}

		byte[] plain = new byte[box.Length - NonceBytes - TagBytes];

		using AesGcm aes = new(key, TagBytes);
		aes.Decrypt(box.AsSpan(0, NonceBytes), box.AsSpan(NonceBytes + TagBytes), box.AsSpan(NonceBytes, TagBytes), plain, Context);

		return plain;
	}

	/// <summary>
	/// The key: read from config/state/secret.key, or made there the first time it's needed (off Windows only).
	///
	/// An existing key file is NEVER replaced, even one that won't read - a new key would silently orphan every
	/// value the old one sealed. Instead nothing is encrypted until it's sorted out, and the log says why.
	/// </summary>
	private static byte[]? Key(bool create) {
		lock (KeyGate) {
			if (_key != null) {
				return _key;
			}

			if (!AesGcm.IsSupported) {
				return null;
			}

			string path = KeyPath;

			try {
				if (!File.Exists(path)) {
					if (!create || OperatingSystem.IsWindows()) {
						return null;
					}

					Directory.CreateDirectory(Path.GetDirectoryName(path)!);
					byte[] fresh = RandomNumberGenerator.GetBytes(KeyBytes);

					// Made owner-only from the first byte, rather than written and then chmodded: there is no moment
					// when another user could read it.
					try {
						using FileStream fs = new(path, new FileStreamOptions {
							Mode = FileMode.CreateNew,
							Access = FileAccess.Write,
							UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
						});

						fs.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(fresh) + "\n"));
						fs.Flush(true);
						_key = fresh;
						Log.Info(new Said("made the login key: {0}", path));
						Log.Info(new Said("keep it with the config folder - saved logins need it"));

						return _key;
					} catch (IOException) when (File.Exists(path)) {
						// made by someone else a moment ago - read theirs below
					}
				}

				byte[] key = Convert.FromBase64String(File.ReadAllText(path).Trim());

				if (key.Length != KeyBytes) {
					throw new CryptographicException($"{key.Length} bytes, expected {KeyBytes}");
				}

				OwnerOnly(path);
				_key = key;

				return _key;
			} catch (Exception e) {
				// Asked whenever something is saved or read: the same problem is said once.
				Log.DebugOnChange("secrets:key", $"login key {path} unusable: {Log.Describe(e)}");
				_keyProblem = Log.Scrub(e.Message);

				return null;
			}
		}
	}

	/// <summary>A key left readable by other users (copied in, restored from a backup) is tightened back to 600.</summary>
	private static void OwnerOnly(string path) {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		try {
			const UnixFileMode Loose = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
				| UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

			if ((File.GetUnixFileMode(path) & Loose) != 0) {
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
		} catch (Exception e) {
			// owned by someone else (a bind mount) - it still works, it just isn't tightened
			Log.Failed($"tightening {path} to owner-only", e);
		}
	}

	private static void WarnOnce(string bot) {
		if (_warned) {
			return;
		}

		_warned = true;

		Log.Warn(AesGcm.IsSupported
			? new Said("saved logins are plain text - key {0} unusable: {1}", KeyPath, _keyProblem)
			: new Said("saved logins are plain text - no AES-GCM on this system"), bot);
		Log.Info(new Said("keep the config folder somewhere private"), bot);
	}
}
