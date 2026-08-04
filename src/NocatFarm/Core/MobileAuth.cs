using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Steam's mobile authenticator, done on this PC instead of on a phone.
///
/// Two secrets, two jobs, and they are worth keeping straight:
///
///   • the SHARED secret generates the five-character code Steam asks for at sign-in. With it, an account with
///     the authenticator on logs in unattended and never stops to ask you for a code.
///   • the IDENTITY secret signs the "confirm on your phone" prompts. Without it, a trade this account accepts
///     is accepted but sits pending until you pick up your phone - which for an unattended idler means forever.
///
/// Neither is needed to RECEIVE items, so a donation-only account never needs any of this. It matters the moment
/// an account gives something away, which is exactly when it should matter.
///
/// Both come out of the maFile that Steam Desktop Authenticator or ASF already wrote, so nobody has to move an
/// authenticator or re-do their 2FA setup to use this.
/// </summary>
public static partial class MobileAuth {
	/// <summary>Steam's alphabet for the five-character code. Not base32, not hex - its own thing.</summary>
	private static readonly char[] CodeAlphabet = "23456789BCDFGHJKMNPQRTVWXY".ToCharArray();

	/// <summary>The five-character sign-in code for right now, or null when we hold no shared secret.</summary>
	public static string? GenerateCode(string? sharedSecret, long? unixTime = null) {
		byte[]? secret = Decode(sharedSecret);

		if ((secret == null) || (secret.Length == 0)) {
			return null;
		}

		// Steam's codes step every 30 seconds, counted from the epoch, like ordinary TOTP.
		long window = (unixTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 30L;
		byte[] counter = BitConverter.GetBytes(window);

		if (BitConverter.IsLittleEndian) {
			Array.Reverse(counter);
		}

		byte[] hash = HMACSHA1.HashData(secret, counter);
		int offset = hash[^1] & 0x0F;

		int truncated = ((hash[offset] & 0x7F) << 24)
			| ((hash[offset + 1] & 0xFF) << 16)
			| ((hash[offset + 2] & 0xFF) << 8)
			| (hash[offset + 3] & 0xFF);

		StringBuilder code = new(5);

		for (int i = 0; i < 5; i++) {
			code.Append(CodeAlphabet[truncated % CodeAlphabet.Length]);
			truncated /= CodeAlphabet.Length;
		}

		return code.ToString();
	}

	/// <summary>
	/// The signature Steam's confirmation pages want: HMAC-SHA1 over time + tag, keyed with the identity secret.
	/// </summary>
	public static string? Confirmation(string? identitySecret, long time, string tag) {
		byte[]? secret = Decode(identitySecret);

		if ((secret == null) || (secret.Length == 0)) {
			return null;
		}

		byte[] tagBytes = Encoding.UTF8.GetBytes(tag);
		byte[] buffer = new byte[8 + Math.Min(tagBytes.Length, 32)];
		byte[] timeBytes = BitConverter.GetBytes(time);

		if (BitConverter.IsLittleEndian) {
			Array.Reverse(timeBytes);
		}

		Array.Copy(timeBytes, buffer, 8);
		Array.Copy(tagBytes, 0, buffer, 8, buffer.Length - 8);

		return Convert.ToBase64String(HMACSHA1.HashData(secret, buffer));
	}

	/// <summary>Secrets are base64 in every maFile format, but accept hex too rather than fail confusingly.</summary>
	private static byte[]? Decode(string? secret) {
		if (string.IsNullOrWhiteSpace(secret)) {
			return null;
		}

		secret = secret.Trim();

		// Shape first, and hex BEFORE base64. A 40-character hex secret is also perfectly valid base64 - every
		// hex digit is in the base64 alphabet and the length divides by four - so trying base64 first decoded it
		// to thirty unrelated bytes and quietly produced codes Steam would never accept.
		if (((secret.Length % 2) == 0) && secret.All(char.IsAsciiHexDigit)) {
			try {
				return Convert.FromHexString(secret);
			} catch (FormatException) {
				// fall through and try base64
			}
		}

		try {
			return Convert.FromBase64String(secret);
		} catch (FormatException) {
			return null;
		}
	}

	/// <summary>
	/// Read the two secrets out of a maFile. The format has drifted between tools over the years, so this looks
	/// for the fields wherever they sit rather than binding to one schema.
	/// </summary>
	public static (string? Shared, string? Identity, string? DeviceId) ReadMaFile(string path) {
		try {
			if (!File.Exists(path)) {
				return (null, null, null);
			}

			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

			return (Find(doc.RootElement, "shared_secret"), Find(doc.RootElement, "identity_secret"), Find(doc.RootElement, "device_id"));
		} catch (Exception e) {
			Log.Warn($"couldn't read the authenticator file {Path.GetFileName(path)}: {e.Message}");

			return (null, null, null);
		}
	}

	private static string? Find(JsonElement element, string name) {
		if (element.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty property in element.EnumerateObject()) {
				if (property.NameEquals(name) && (property.Value.ValueKind == JsonValueKind.String)) {
					return property.Value.GetString();
				}

				string? nested = Find(property.Value, name);

				if (nested != null) {
					return nested;
				}
			}
		}

		return null;
	}

	/// <summary>A device id in the shape Steam expects, derived from the account so it stays the same every run.</summary>
	public static string DeviceId(ulong steamId) {
		string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(steamId.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant();

		return $"android:{hash[..8]}-{hash[8..12]}-{hash[12..16]}-{hash[16..20]}-{hash[20..32]}";
	}

	/// <summary>One pending "confirm on your phone" prompt.</summary>
	public readonly record struct Pending(ulong Id, ulong Key, ulong CreatorId);

	/// <summary>
	/// The confirmations waiting on this account.
	///
	/// /mobileconf/getlist answers JSON - {"success":true,"conf":[{"id":…,"nonce":…,"creator_id":…}]} - and has
	/// done for years. Reading it as HTML data attributes, which is what the old confirmation PAGE used, matched
	/// nothing at all, so self-confirming a trade silently never worked and every offer sat pending forever.
	///
	/// The HTML shape is still tried as a fallback, because it costs nothing and Steam has moved this more than
	/// once already.
	/// </summary>
	public static List<Pending> ParseConfirmations(string body) {
		List<Pending> pending = [];

		if (string.IsNullOrWhiteSpace(body)) {
			return pending;
		}

		if (body.TrimStart().StartsWith('{')) {
			try {
				using JsonDocument doc = JsonDocument.Parse(body);

				if (doc.RootElement.TryGetProperty("conf", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
					foreach (JsonElement entry in list.EnumerateArray()) {
						ulong id = Number(entry, "id");
						ulong key = Number(entry, "nonce");

						// The trade offer id lives under creator_id, and older payloads spelled it creatorid.
						ulong creator = Number(entry, "creator_id");

						if (creator == 0) {
							creator = Number(entry, "creatorid");
						}

						if ((id != 0) && (key != 0)) {
							pending.Add(new Pending(id, key, creator));
						}
					}
				}

				return pending;
			} catch (JsonException) {
				// not the JSON we expected - fall through to the markup reader
			}
		}

		foreach (Match match in ConfirmationEntry().Matches(body)) {
			if (ulong.TryParse(match.Groups[1].Value, out ulong id)
				&& ulong.TryParse(match.Groups[2].Value, out ulong key)
				&& ulong.TryParse(match.Groups[3].Value, out ulong creator)) {
				pending.Add(new Pending(id, key, creator));
			}
		}

		return pending;
	}

	/// <summary>Steam sends these ids as strings in some payloads and as numbers in others.</summary>
	private static ulong Number(JsonElement element, string name) {
		if (!element.TryGetProperty(name, out JsonElement value)) {
			return 0;
		}

		return value.ValueKind switch {
			JsonValueKind.Number => value.TryGetUInt64(out ulong n) ? n : 0,
			JsonValueKind.String => ulong.TryParse(value.GetString(), out ulong s) ? s : 0,
			_ => 0
		};
	}

	[GeneratedRegex(@"data-confid=""(\d+)""\s+data-key=""(\d+)""[^>]*?data-creator(?:id)?=""(\d+)""", RegexOptions.IgnoreCase)]
	private static partial Regex ConfirmationEntry();
}

/// <summary>Where an account's authenticator secrets live on disk: config/authenticators/&lt;bot&gt;.maFile.</summary>
public static class MaFiles {
	public static string Dir => Path.Combine(ConfigStore.ConfigDir, "authenticators");

	public static string PathFor(string botName) => Path.Combine(Dir, botName + ".maFile");
}
