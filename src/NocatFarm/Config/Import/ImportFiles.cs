using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>
/// Where the other idlers keep their files. Every importer asks here instead of reading the environment itself, so
/// the tests can point all of it at a temporary folder and never go near the real profile, Steam or the Windows
/// credential store.
/// </summary>
public static class ImportPlaces {
	/// <summary>
	/// NOCATFARM_IMPORT_SANDBOX=&lt;folder&gt; points every place below into that folder (local, roaming, desktop, documents,
	/// downloads, steam) and turns the credential store off - for trying the importers on sample files without them
	/// ever seeing the real profile.
	/// </summary>
	private static readonly string? Sandbox = Environment.GetEnvironmentVariable("NOCATFARM_IMPORT_SANDBOX") is { Length: > 0 } box ? Path.GetFullPath(box) : null;

	public static string LocalAppData { get; set; } = Place("local", Environment.SpecialFolder.LocalApplicationData);
	public static string AppData { get; set; } = Place("roaming", Environment.SpecialFolder.ApplicationData);
	public static string Desktop { get; set; } = Place("desktop", Environment.SpecialFolder.DesktopDirectory);
	public static string Documents { get; set; } = Place("documents", Environment.SpecialFolder.MyDocuments);
	public static string Downloads { get; set; } = Sandbox != null ? Path.Combine(Sandbox, "downloads")
		: Special(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? Path.Combine(home, "Downloads") : "";

	/// <summary>Steam's own folder (for config/loginusers.vdf), or null when Steam isn't installed.</summary>
	public static Func<string?> SteamPath { get; set; } = Sandbox != null ? static () => Path.Combine(Sandbox, "steam") : ReadSteamPath;

	/// <summary>
	/// A generic credential's secret out of the Windows Credential Manager, read-only. Only ever called for an account
	/// the user ticked "bring its sign-in over" for.
	/// </summary>
	public static Func<string, byte[]?> ReadCredential { get; set; } = static target =>
		(Sandbox == null) && OperatingSystem.IsWindows() ? Windows.CredentialStore.ReadGeneric(target) : null;

	private static string Place(string sandboxed, Environment.SpecialFolder real) =>
		Sandbox != null ? Path.Combine(Sandbox, sandboxed) : Special(real);

	/// <summary>The folders a portable idler is usually unzipped into, searched a couple of folders deep - and in Docker,
	/// /import, where docker-compose.example.yml mounts another idler's folder.</summary>
	public static IEnumerable<string> UsualRoots => new[] { Desktop, Documents, Downloads, DockerImport }.Where(static r => r.Length > 0);

	private static string DockerImport => (Sandbox == null) && Core.Platform.InContainer && Directory.Exists("/import") ? "/import" : "";

	private static string Special(Environment.SpecialFolder folder) {
		try {
			return Environment.GetFolderPath(folder);
		} catch {
			return "";
		}
	}

	private static string? ReadSteamPath() {
		if (!OperatingSystem.IsWindows()) {
			string home = Special(Environment.SpecialFolder.UserProfile);

			return new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam") }.FirstOrDefault(Directory.Exists);
		}

		try {
			using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");

			return key?.GetValue("SteamPath") is string path && (path.Length > 0) ? Path.GetFullPath(path) : null;
		} catch {
			return null;
		}
	}
}

/// <summary>Reading other programs' files without ever changing, locking or trusting them.</summary>
internal static class ImportFiles {
	/// <summary>Nothing an idler writes is anywhere near this; anything bigger is not what we're looking for.</summary>
	private const long MaxBytes = 8 * 1024 * 1024;

	private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

	/// <summary>
	/// A file's text, opened read-only and shared, so the other idler can keep running and keep writing to it. Null
	/// when it isn't there or can't be read.
	/// </summary>
	public static string? Text(string path) {
		try {
			FileInfo info = new(path);

			if (!info.Exists || (info.Length > MaxBytes)) {
				return null;
			}

			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

			return reader.ReadToEnd();
		} catch {
			return null;
		}
	}

	/// <summary>A JSON file's root, or an undefined element when it's missing or isn't JSON.</summary>
	public static JsonElement Json(string path) {
		string? text = Text(path);

		if (string.IsNullOrWhiteSpace(text)) {
			return default;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(text, Lenient);

			return doc.RootElement.Clone();
		} catch {
			return default;
		}
	}

	public static string? Str(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() : null;

	public static bool? Bool(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

	public static int? Int(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetInt32(out int i) ? i : null;

	public static double? Num(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetDouble(out double d) ? d : null;

	public static JsonElement Prop(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) ? v : default;

	/// <summary>A property wherever it sits in the document - for files whose nesting has moved between versions.</summary>
	public static JsonElement Find(JsonElement e, string name) {
		if (e.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty p in e.EnumerateObject()) {
				if (p.NameEquals(name)) {
					return p.Value;
				}
			}

			foreach (JsonProperty p in e.EnumerateObject()) {
				if (Find(p.Value, name) is { ValueKind: not JsonValueKind.Undefined } nested) {
					return nested;
				}
			}
		}

		return default;
	}

	/// <summary>App IDs from a JSON array of numbers (or numeric strings), in order, without repeats or zeros.</summary>
	public static List<uint> Apps(JsonElement array) {
		List<uint> apps = [];

		if (array.ValueKind != JsonValueKind.Array) {
			return apps;
		}

		foreach (JsonElement item in array.EnumerateArray()) {
			uint? app = AppOf(item);

			if (app is uint id && !apps.Contains(id)) {
				apps.Add(id);
			}
		}

		return apps;
	}

	public static uint? AppOf(JsonElement item) => item.ValueKind switch {
		JsonValueKind.Number when item.TryGetUInt32(out uint n) && (n > 0) => n,
		JsonValueKind.String when uint.TryParse(item.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint s) && (s > 0) => s,
		_ => null
	};

	/// <summary>App IDs out of text lines ("730", " 440 ") - how the .NET settings files keep a list.</summary>
	public static List<uint> Apps(IEnumerable<string> values) {
		List<uint> apps = [];

		foreach (string value in values) {
			if (uint.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app > 0) && !apps.Contains(app)) {
				apps.Add(app);
			}
		}

		return apps;
	}

	/// <summary>
	/// Folders under <paramref name="roots"/> (the roots themselves included) for which <paramref name="match"/> says
	/// yes, up to <paramref name="depth"/> levels down. Hidden and system folders are skipped, and it gives up
	/// quietly on anything it isn't allowed to list.
	/// </summary>
	/// <summary>
	/// Two paths are the same folder when they match ignoring case on Windows and macOS, but only exactly on Linux -
	/// there "/import/asf" and "/import/ASF" are two folders, and ignoring case dropped the real one as a copy.
	/// </summary>
	public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
		? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	public static IEnumerable<string> Folders(IEnumerable<string> roots, int depth, Func<string, bool> match) {
		foreach (string root in roots) {
			foreach (string hit in Walk(root, depth, match)) {
				yield return hit;
			}
		}
	}

	/// <summary>
	/// For a portable idler with no fixed home: every folder a couple of levels under the Desktop, Documents and
	/// Downloads that <paramref name="match"/> recognises, then those three themselves (shown as "…\" - searched below).
	/// </summary>
	public static IEnumerable<string> SearchUsual(Func<string, bool> match) =>
		Folders(ImportPlaces.UsualRoots, 2, match)
			.Concat(ImportPlaces.UsualRoots.Select(static r => Path.Combine(r, "…")))
			.Distinct(PathComparer);

	private static IEnumerable<string> Walk(string dir, int depth, Func<string, bool> match) {
		if (!Directory.Exists(dir)) {
			yield break;
		}

		bool hit;

		try {
			hit = match(dir);
		} catch {
			hit = false;
		}

		if (hit) {
			yield return dir;

			yield break;
		}

		if (depth <= 0) {
			yield break;
		}

		string[] children;

		try {
			children = Directory.GetDirectories(dir);
		} catch {
			yield break;
		}

		foreach (string child in children) {
			FileAttributes attributes;

			try {
				attributes = File.GetAttributes(child);
			} catch {
				continue;
			}

			if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) {
				continue;
			}

			foreach (string found in Walk(child, depth - 1, match)) {
				yield return found;
			}
		}
	}

	/// <summary>A config name from a Steam login - letters, numbers, dashes and underscores - that isn't taken yet.</summary>
	public static string NameFor(string login, string fallback, ICollection<string> taken) {
		StringBuilder clean = new();

		foreach (char c in login) {
			if (char.IsAsciiLetterOrDigit(c) || (c is '_' or '-')) {
				clean.Append(c);
			}
		}

		string stem = clean.Length > 0 ? clean.ToString()[..Math.Min(32, clean.Length)] : fallback;

		if (stem.Equals("nocatFarm", StringComparison.OrdinalIgnoreCase)) {
			stem += "1";
		}

		string name = stem;

		for (int i = 2; taken.Contains(name, StringComparer.OrdinalIgnoreCase); i++) {
			name = stem + i.ToString(CultureInfo.InvariantCulture);
		}

		taken.Add(name);

		return name;
	}

	/// <summary>A SteamID64 of an ordinary account - "7656119…", 17 digits.</summary>
	public static bool IsSteamId(string? value) =>
		(value != null) && (value.Length == 17) && value.StartsWith("7656119", StringComparison.Ordinal) && value.All(char.IsAsciiDigit);

	/// <summary>
	/// The SteamID64 out of a Steam web cookie ("7656119…||eyJ…", URL-encoded). Only the account number is taken: the
	/// rest is a web session, and nothing here ever signs in with somebody else's session.
	/// </summary>
	public static string? SteamIdFromCookie(string? cookie) {
		if (string.IsNullOrWhiteSpace(cookie)) {
			return null;
		}

		string decoded;

		try {
			decoded = Uri.UnescapeDataString(cookie.Trim());
		} catch {
			decoded = cookie.Trim();
		}

		int bar = decoded.IndexOf('|', StringComparison.Ordinal);
		string id = bar > 0 ? decoded[..bar] : decoded;

		return IsSteamId(id) ? id : null;
	}

	/// <summary>The top-level string claims of a JWT (the Steam refresh token), or empty when it isn't one.</summary>
	public static Dictionary<string, JsonElement> JwtClaims(string? token) {
		Dictionary<string, JsonElement> claims = new(StringComparer.Ordinal);
		string[] parts = (token ?? "").Split('.');

		if (parts.Length != 3) {
			return claims;
		}

		try {
			string b64 = parts[1].Replace('-', '+').Replace('_', '/');
			b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');

			using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(b64));

			if (doc.RootElement.ValueKind == JsonValueKind.Object) {
				foreach (JsonProperty p in doc.RootElement.EnumerateObject()) {
					claims[p.Name] = p.Value.Clone();
				}
			}
		} catch {
			claims.Clear();
		}

		return claims;
	}

	/// <summary>True for a JWT that has a subject and hasn't run out.</summary>
	public static bool IsLiveJwt(string? token, out string? steamId) {
		Dictionary<string, JsonElement> claims = JwtClaims(token);
		steamId = claims.TryGetValue("sub", out JsonElement sub) && (sub.ValueKind == JsonValueKind.String) ? sub.GetString() : null;

		if (claims.Count == 0) {
			return false;
		}

		return !claims.TryGetValue("exp", out JsonElement exp) || (exp.ValueKind != JsonValueKind.Number) || !exp.TryGetInt64(out long seconds)
			|| (DateTimeOffset.FromUnixTimeSeconds(seconds) > DateTimeOffset.UtcNow);
	}

	/// <summary>The two secrets out of a maFile's JSON, or nulls when it isn't one.</summary>
	public static (string? Shared, string? Identity) MaFileSecrets(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return (null, null);
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json, Lenient);
			JsonElement root = doc.RootElement;

			return (Find(root, "shared_secret") is { ValueKind: JsonValueKind.String } s ? s.GetString() : null,
				Find(root, "identity_secret") is { ValueKind: JsonValueKind.String } i ? i.GetString() : null);
		} catch {
			return (null, null);
		}
	}
}

/// <summary>
/// The "user.config" file every .NET Framework program keeps its settings in (Idle Master and Idle Master Extended both
/// do): <c>&lt;Section&gt;&lt;setting name=".." serializeAs=".."&gt;&lt;value&gt;..&lt;/value&gt;</c>.
/// </summary>
internal static class DotNetUserConfig {
	/// <summary>Every setting in <paramref name="section"/>, by name. Empty when the file isn't one of these.</summary>
	public static Dictionary<string, XElement> Read(string path, string section) {
		Dictionary<string, XElement> values = new(StringComparer.OrdinalIgnoreCase);
		string? text = ImportFiles.Text(path);

		if (string.IsNullOrWhiteSpace(text)) {
			return values;
		}

		try {
			using StringReader sr = new(text);
			using XmlReader reader = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
			XDocument doc = XDocument.Load(reader);

			foreach (XElement block in doc.Descendants().Where(e => e.Name.LocalName.Equals(section, StringComparison.Ordinal))) {
				foreach (XElement setting in block.Elements().Where(static e => e.Name.LocalName == "setting")) {
					if ((setting.Attribute("name")?.Value is { Length: > 0 } name) && (setting.Elements().FirstOrDefault(static e => e.Name.LocalName == "value") is { } value)) {
						values[name] = value;
					}
				}
			}
		} catch {
			values.Clear();
		}

		return values;
	}

	public static string? String(Dictionary<string, XElement> values, string name) =>
		values.TryGetValue(name, out XElement? value) ? value.Value.Trim() : null;

	public static bool? Bool(Dictionary<string, XElement> values, string name) =>
		bool.TryParse(String(values, name), out bool b) ? b : null;

	/// <summary>A list setting (serializeAs="Xml": an ArrayOfString of &lt;string&gt;), empty entries left out.</summary>
	public static List<string> Strings(Dictionary<string, XElement> values, string name) =>
		values.TryGetValue(name, out XElement? value)
			? value.Descendants().Where(static e => e.Name.LocalName == "string").Select(static e => e.Value.Trim()).Where(static s => s.Length > 0).ToList()
			: [];

	/// <summary>Whether the file at <paramref name="path"/> is a user.config holding <paramref name="section"/>.</summary>
	public static bool Holds(string path, string section) =>
		(ImportFiles.Text(path) is { } text) && text.Contains("<" + section, StringComparison.Ordinal);

	/// <summary>
	/// The newest user.config holding <paramref name="section"/> under <paramref name="root"/>. .NET keeps one per
	/// program version (…\1.2.0.0\user.config), so the highest version is the one in use; ties go to the newest file.
	/// </summary>
	public static string? Newest(string root, string section, int depth) {
		List<string> files = [];
		Collect(root, depth, files);

		return files
			.Where(f => Holds(f, section))
			.OrderByDescending(static f => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(f)), out Version? v) ? v : new Version(0, 0))
			.ThenByDescending(static f => {
				try {
					return File.GetLastWriteTimeUtc(f);
				} catch {
					return DateTime.MinValue;
				}
			})
			.FirstOrDefault();
	}

	private static void Collect(string dir, int depth, List<string> into) {
		if ((into.Count > 200) || !Directory.Exists(dir)) {
			return;
		}

		string here = Path.Combine(dir, "user.config");

		if (File.Exists(here)) {
			into.Add(here);
		}

		if (depth <= 0) {
			return;
		}

		try {
			foreach (string child in Directory.GetDirectories(dir)) {
				Collect(child, depth - 1, into);
			}
		} catch {
			// a folder it may not list
		}
	}
}

/// <summary>The accounts this PC's Steam app has signed into, from its config/loginusers.vdf.</summary>
internal static class SteamLogins {
	public sealed record Login(string SteamId, string AccountName, string PersonaName, bool MostRecent);

	public static List<Login> Read() {
		List<Login> logins = [];

		try {
			string? steam = ImportPlaces.SteamPath();

			if (string.IsNullOrEmpty(steam) || (ImportFiles.Text(Path.Combine(steam, "config", "loginusers.vdf")) is not { } text)) {
				return logins;
			}

			if (Vdf.Parse(text).TryGetValue("users", out object? users) && (users is Dictionary<string, object> byId)) {
				foreach ((string id, object entry) in byId) {
					if (ImportFiles.IsSteamId(id) && (entry is Dictionary<string, object> fields) && (fields.GetValueOrDefault("AccountName") is string { Length: > 0 } account)) {
						logins.Add(new Login(id, account, fields.GetValueOrDefault("PersonaName") as string ?? "",
							(fields.GetValueOrDefault("MostRecent") as string) == "1"));
					}
				}
			}
		} catch {
			// no Steam here, or a file it didn't expect
		}

		return logins;
	}

	/// <summary>The Steam sign-in name for a SteamID64, when this PC's Steam knows it.</summary>
	public static string? AccountFor(string? steamId) =>
		steamId == null ? null : Read().FirstOrDefault(l => l.SteamId == steamId)?.AccountName;
}

/// <summary>Valve's KeyValues text format: "key" "value" and "key" { … }. Keys case-insensitive, like Steam reads them.</summary>
internal static class Vdf {
	public static Dictionary<string, object> Parse(string text) {
		int i = 0;

		return Block(text, ref i);
	}

	private static Dictionary<string, object> Block(string s, ref int i) {
		Dictionary<string, object> map = new(StringComparer.OrdinalIgnoreCase);

		while (true) {
			string? key = Token(s, ref i, out bool open, out bool close);

			if (close || ((key == null) && !open)) {
				return map;
			}

			if (key == null) {
				continue;   // a stray brace
			}

			string? value = Token(s, ref i, out open, out close);

			if (open) {
				map[key] = Block(s, ref i);
			} else if (value != null) {
				map[key] = value;
			} else {
				return map;
			}
		}
	}

	private static string? Token(string s, ref int i, out bool open, out bool close) {
		open = close = false;

		while (i < s.Length) {
			char c = s[i];

			if (char.IsWhiteSpace(c)) {
				i++;

				continue;
			}

			if ((c == '/') && (i + 1 < s.Length) && (s[i + 1] == '/')) {
				while ((i < s.Length) && (s[i] != '\n')) {
					i++;
				}

				continue;
			}

			if (c == '{') {
				i++;
				open = true;

				return null;
			}

			if (c == '}') {
				i++;
				close = true;

				return null;
			}

			StringBuilder sb = new();

			if (c == '"') {
				for (i++; (i < s.Length) && (s[i] != '"'); i++) {
					if ((s[i] == '\\') && (i + 1 < s.Length)) {
						i++;
						sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });

						continue;
					}

					sb.Append(s[i]);
				}

				i++;

				return sb.ToString();
			}

			for (; (i < s.Length) && !char.IsWhiteSpace(s[i]) && (s[i] is not ('{' or '}' or '"')); i++) {
				sb.Append(s[i]);
			}

			return sb.ToString();
		}

		return null;
	}
}
