using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NocatFarm.Core;

/// <summary>
/// Encryption at rest for the things that ARE the account: login tokens, the access token, stored passwords.
///
/// A Steam refresh token is not a convenience - it is a credential. Anything holding one can sign in as that
/// account without the password and without a Steam Guard code, which is exactly why they were the one thing in
/// here worth protecting and the one thing sitting in plain text.
///
/// Windows does this properly with DPAPI: the ciphertext is bound to the user account, so a copied file is
/// useless on another machine or under another user, and there is no key for us to store badly. Everywhere else
/// there is no equivalent that isn't security theatre - a key kept next to the data it encrypts protects nobody -
/// so those platforms keep plain text and say so once, rather than pretending.
///
/// Reading is deliberately tolerant: an unencrypted file left by an older version is read as-is and rewritten
/// encrypted the next time it is saved, so upgrading takes no migration step and nobody gets logged out.
/// </summary>
public static class Secrets {
	/// <summary>Marks a file this wrote. Anything without it is plain text from an older version.</summary>
	private const string Marker = "nocat1:";

	private static bool _warned;

	public static bool Available => OperatingSystem.IsWindows();

	/// <summary>Encrypt for storage. Falls back to the plain text where the platform can't do better.</summary>
	public static string Protect(string plain, string forBot) {
		if (string.IsNullOrEmpty(plain)) {
			return plain;
		}

		if (!Available) {
			WarnOnce(forBot);

			return plain;
		}

		try {
			return Marker + Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(plain)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't encrypt a stored secret ({0}) - keeping it as it is", e.Message), forBot);

			return plain;
		}
	}

	/// <summary>Decrypt something Protect wrote. Anything else is handed straight back.</summary>
	public static string Unprotect(string stored) {
		if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Marker, StringComparison.Ordinal)) {
			return stored;   // plain text from an older version, or from a platform without DPAPI
		}

		if (!Available) {
			return "";   // written on Windows, being read somewhere else - it cannot be recovered here
		}

		try {
			return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(stored[Marker.Length..])));
		} catch (Exception e) {
			// A different Windows user, a restored profile, or a corrupted file. Treat it as absent: the account
			// signs in again with its password, which is a nuisance rather than a failure.
			Log.Debug(new Said("a stored secret couldn't be decrypted ({0}) - it will be asked for again", e.Message));

			return "";
		}
	}

	/// <summary>True when this string is already encrypted, so a re-save can be skipped.</summary>
	public static bool IsProtected(string stored) => stored.StartsWith(Marker, StringComparison.Ordinal);

	[SupportedOSPlatform("windows")]
	private static byte[] Encrypt(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

	[SupportedOSPlatform("windows")]
	private static byte[] Decrypt(byte[] cipher) => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);

	private static void WarnOnce(string bot) {
		if (_warned) {
			return;
		}

		_warned = true;
		Log.Warn(new Said("login tokens are stored as plain text on {0} - only Windows has a key store to bind them to. Keep the config folder somewhere private.", RuntimeInformation.RuntimeIdentifier), bot);
	}
}
