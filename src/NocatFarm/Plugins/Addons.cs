using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Plugins;

/// <summary>One official add-on, as the signed index lists it.</summary>
/// <param name="Id">Short and unique: what 'addons install' takes.</param>
/// <param name="Plugin">The plugin's own Name once it's loaded - for its on/off switch.</param>
/// <param name="Description">What it does, per language code ("en" always there).</param>
/// <param name="File">The DLL's file name in plugins/.</param>
/// <param name="Url">Where to get it: a release file on the nocat.farm GitHub, nowhere else.</param>
/// <param name="Sha256">The DLL's SHA-256, lowercase hex.</param>
public sealed record AddonEntry(string Id, string Name, string Plugin, IReadOnlyDictionary<string, string> Description, string Version,
	string MinAppVersion, string File, string Url, string Sha256) {
	/// <summary>The description in this language, or English.</summary>
	public string DescriptionIn(string language) =>
		Description.TryGetValue(language, out string? d) && (d.Length > 0) ? d : Description.GetValueOrDefault("en") ?? "";
}

/// <summary>
/// 1.9.0: reap's official add-ons - the list on GitHub, and installing one from it.
/// </summary>
/// <remarks>
/// The list is addons/addons.json on the nocat.farm repository's main branch, with addons.json.sig beside it: an ECDSA P-256
/// signature over the file's exact bytes, made at release time with a key that lives on reap's PC and nowhere else. The
/// public half is built in here. A sha256 in a list fetched from the same place as the file proves nothing on its own -
/// whoever could change one could change both - so nothing is installed unless, all three:
///
///   • the list's signature verifies with the built-in key
///   • the add-on is in that list, and its minimum nocat.farm version is this one or older
///   • the downloaded file's SHA-256 is the one the list gives
///
/// Only names from the list can be asked for - never an address - and the address it gives must be a release file on the
/// nocat.farm GitHub. A DLL put in plugins/ by hand still loads as it always has, with the warning that goes with it.
/// </remarks>
public static partial class Addons {
	public const string IndexUrl = "https://raw.githubusercontent.com/VisaHolder/nocatfarm/main/addons/addons.json";
	public const string SignatureUrl = IndexUrl + ".sig";

	/// <summary>Where every official add-on comes from: a release file of nocat.farm's own repository.</summary>
	public const string DownloadPrefix = "https://github.com/VisaHolder/nocatfarm/releases/download/";

	/// <summary>The public key the list must be signed with (SubjectPublicKeyInfo, base64). The private key is on reap's PC only.</summary>
	public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExyV0DW47QH+b8G2LwGwUCLOIYdIMzkhFu/Xrz9CDQPra7qfjFs3dV3W6TZ7943zXfd84FuOsekmMsHW8qSOfgg==";

	/// <summary>The key lists are checked with: <see cref="PublicKey"/>. Not readonly: the checks sign their own lists.</summary>
	internal static string Key = PublicKey;

	/// <summary>The biggest add-on file taken. A real one is tens of kilobytes.</summary>
	internal const int MaxBytes = 20 * 1024 * 1024;

	/// <summary>Not readonly: the checks answer for GitHub.</summary>
	internal static HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = MaxListBytes };

	/// <summary>The biggest add-on list (or signature) read - the list is a few kilobytes; the add-on files are read apart, to <see cref="MaxBytes"/>.</summary>
	internal const int MaxListBytes = 1024 * 1024;

	static Addons() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("nocatFarm");

	[GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
	private static partial Regex IdRx();

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\.dll$", RegexOptions.CultureInvariant)]
	private static partial Regex FileRx();

	[GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
	private static partial Regex ShaRx();

	/// <summary>
	/// The list, if its signature verifies with <paramref name="publicKey"/> - checked before a byte of it is read - and every
	/// entry is well formed. Otherwise null, and why.
	/// </summary>
	public static IReadOnlyList<AddonEntry>? Parse(byte[] json, string signature, string publicKey, out Said why) =>
		Parse(json, signature, publicKey, out why, out _);

	/// <summary>The same, and the list's <c>serial</c>: a number every new signed list has higher than the one before (0 when it has none).</summary>
	public static IReadOnlyList<AddonEntry>? Parse(byte[] json, string signature, string publicKey, out Said why, out long serial) {
		why = default;
		serial = 0;

		if (!Verify(json, signature, publicKey)) {
			why = new Said("the add-on list's signature doesn't match - nothing from it is installed");

			return null;
		}

		List<AddonEntry> entries = [];

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("addons", out JsonElement list) || (list.ValueKind != JsonValueKind.Array)) {
				why = new Said("the add-on list can't be read");

				return null;
			}

			if (doc.RootElement.TryGetProperty("serial", out JsonElement n) && (n.ValueKind == JsonValueKind.Number) && n.TryGetInt64(out long number) && (number > 0)) {
				serial = number;
			}

			foreach (JsonElement e in list.EnumerateArray()) {
				Dictionary<string, string> description = new(StringComparer.OrdinalIgnoreCase);

				if (e.TryGetProperty("description", out JsonElement d) && (d.ValueKind == JsonValueKind.Object)) {
					foreach (JsonProperty p in d.EnumerateObject()) {
						if (p.Value.ValueKind == JsonValueKind.String) {
							description[p.Name] = p.Value.GetString() ?? "";
						}
					}
				}

				AddonEntry entry = new(Str(e, "id"), Str(e, "name"), Str(e, "plugin"), description, Str(e, "version"),
					Str(e, "minAppVersion"), Str(e, "file"), Str(e, "url"), Str(e, "sha256").ToLowerInvariant());

				// A malformed entry is left out rather than trusted in part.
				if (WellFormed(entry)) {
					entries.Add(entry);
				}
			}
		} catch (JsonException) {
			why = new Said("the add-on list can't be read");

			return null;
		}

		return entries;
	}

	private static string Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() ?? "" : "";

	/// <summary>An entry nothing odd can come of: a plain id and file name, real versions, a 64-digit hash, a nocat.farm release address.</summary>
	internal static bool WellFormed(AddonEntry e) =>
		IdRx().IsMatch(e.Id) && FileRx().IsMatch(e.File) && ShaRx().IsMatch(e.Sha256)
		&& Version.TryParse(e.Version, out _) && Version.TryParse(e.MinAppVersion, out _)
		&& (e.Plugin.Length is > 0 and <= 64) && (e.Name.Length is > 0 and <= 64)
		&& FromGitHub(e.Url);

	/// <summary>A release file of nocat.farm's own repository, over https, with nothing odd in the path.</summary>
	internal static bool FromGitHub(string url) =>
		url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
		&& (u.Scheme == Uri.UriSchemeHttps) && (u.Host == "github.com") && !url.Contains("..", StringComparison.Ordinal)
		&& string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment);

	/// <summary>Whether <paramref name="signature"/> (base64, r and s) is <paramref name="publicKey"/>'s over exactly these bytes.</summary>
	internal static bool Verify(byte[] data, string signature, string publicKey) {
		try {
			using ECDsa key = ECDsa.Create();
			key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

			return key.VerifyData(data, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		} catch (Exception e) when (e is FormatException or CryptographicException) {
			return false;
		}
	}

	/// <summary>Why this file isn't the add-on the list means, or default when it is.</summary>
	internal static Said CheckFile(AddonEntry entry, byte[] file) {
		string sha = Convert.ToHexStringLower(SHA256.HashData(file));

		return string.Equals(sha, entry.Sha256, StringComparison.Ordinal)
			? default
			: new Said("{0} downloaded, but it isn't the file the add-on list signed for - not installed", entry.Name);
	}

	/// <summary>Whether this nocat.farm is new enough for it.</summary>
	internal static bool Fits(AddonEntry entry, string appVersion) =>
		Version.TryParse(entry.MinAppVersion, out Version? min) && Version.TryParse(appVersion, out Version? app) && (app >= min);

	// ── the list on GitHub ───────────────────────────────────────────────────
	private static (DateTime At, IReadOnlyList<AddonEntry> List)? _cached;

	/// <summary>The last list that verified, as it came (and its signature) - so an add-on can be taken out without GitHub.</summary>
	internal static string KeptPath => Path.Combine(ConfigStore.ConfigDir, "state", "addons.json");

	/// <summary>The highest list serial seen here. Never goes down; not in a backup, so a restore can't take it back either.</summary>
	internal static string SerialPath => Path.Combine(ConfigStore.ConfigDir, "state", "addons-serial.txt");

	private static readonly Lock KeptGate = new();

	/// <summary>The highest serial of a list taken here, 0 for none.</summary>
	internal static long SeenSerial() {
		try {
			return File.Exists(SerialPath) && long.TryParse(File.ReadAllText(SerialPath).Trim(), System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return 0;
		}
	}

	/// <summary>
	/// A fetched list: verified, then checked against the newest one seen here - an older one (a copy of the list from before
	/// an add-on was fixed, served again) is refused, as an update that would go backwards. One that's taken is kept on disk.
	/// </summary>
	internal static (IReadOnlyList<AddonEntry>? List, Said Why) Take(byte[] json, string signature) {
		IReadOnlyList<AddonEntry>? list = Parse(json, signature, Key, out Said why, out long serial);

		if (list == null) {
			return (null, why);
		}

		lock (KeptGate) {
			long seen = SeenSerial();

			if (serial < seen) {
				return (null, new Said("the add-on list from GitHub is older than one this nocat.farm has already had - nothing from it is installed"));
			}

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(KeptPath)!);

				if (serial > seen) {
					AtomicFile.Write(SerialPath, serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
				}

				AtomicFile.Write(KeptPath + ".sig", signature.Trim());
				AtomicFile.Write(KeptPath, json);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug($"add-ons: couldn't keep the list: {Log.Describe(e)}");
			}
		}

		return (list, default);
	}

	/// <summary>The last list that verified here, read from disk and checked again - null when there's none or it doesn't verify.</summary>
	internal static IReadOnlyList<AddonEntry>? Kept() {
		try {
			lock (KeptGate) {
				return File.Exists(KeptPath) && File.Exists(KeptPath + ".sig")
					? Parse(File.ReadAllBytes(KeptPath), File.ReadAllText(KeptPath + ".sig"), Key, out _)
					: null;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	/// <summary>The official add-on this file is, by the last list kept here - when its plugin is switched off. Null otherwise.</summary>
	internal static AddonEntry? SwitchedOff(string file) =>
		(Live.Global.DisabledPlugins.Count > 0) && (Kept()?.FirstOrDefault(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)) is { } entry)
		&& Live.Global.DisabledPlugins.Contains(entry.Plugin, StringComparer.OrdinalIgnoreCase)
			? entry
			: null;

	/// <summary>A DLL's own version, read from its file without loading it ("1.0.0"), or null when it can't be read.</summary>
	internal static string? VersionOnDisk(string path) {
		try {
			return File.Exists(path) && System.Reflection.AssemblyName.GetAssemblyName(path).Version is { } v ? Norm(v).ToString(3) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) {
			return null;
		}
	}

	/// <summary>A version with the parts it leaves out as 0, so 1.0.0 and 1.0.0.0 are the same.</summary>
	private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

	/// <summary>Whether <paramref name="offered"/> is older than <paramref name="installed"/> (false when either isn't a version).</summary>
	internal static bool Older(string offered, string? installed) =>
		Version.TryParse(offered, out Version? o) && Version.TryParse(installed, out Version? i) && (Norm(o) < Norm(i));

	/// <summary>The version of an add-on installed now: the one loaded, else its file's own (one waiting for a restart first) - null when there's none.</summary>
	internal static string? InstalledVersion(AddonEntry entry) {
		string? loaded = PluginHost.Discovered.FirstOrDefault(p => string.Equals(p.File, entry.File, StringComparison.OrdinalIgnoreCase)).Version;
		string target = Path.Combine(PluginHost.Folder, entry.File);

		return File.Exists(target + PluginHost.NewSuffix) ? VersionOnDisk(target + PluginHost.NewSuffix)
			: !string.IsNullOrEmpty(loaded) ? loaded
			: VersionOnDisk(target);
	}

	/// <summary>1.9.0: the last time the list couldn't be had, and why - asked again only a minute later, unless asked fresh.</summary>
	private static (DateTime At, Said Why)? _failed;

	/// <summary>1.9.0: the one fetch under way, which every look waits on.</summary>
	private static Task<(IReadOnlyList<AddonEntry>? List, Said Why)>? _fetching;

	private static readonly Lock FetchGate = new();

	/// <summary>The list, fetched and verified (kept 10 minutes). Null and why when it can't be had or doesn't verify.</summary>
	/// <remarks>
	/// 1.9.0: one fetch at a time, and a failure remembered for a minute. With GitHub slow or not answering, every look at the
	/// Add-ons tab (and every 'addons') started a fetch of its own and waited out its whole minute - the tab hung, and GitHub
	/// was asked over and over by everything that looked.
	/// </remarks>
	public static async Task<(IReadOnlyList<AddonEntry>? List, Said Why)> ListAsync(bool fresh, CancellationToken ct = default) {
		if (!fresh && (_cached is { } c) && (DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10))) {
			return (c.List, default);
		}

		if (!fresh && (_failed is { } f) && (DateTime.UtcNow - f.At < TimeSpan.FromMinutes(1))) {
			return (null, f.Why);
		}

		Task<(IReadOnlyList<AddonEntry>? List, Said Why)> fetch;

		lock (FetchGate) {
			fetch = _fetching ?? Task.Run(FetchAsync);

			if (_fetching == null) {
				_fetching = fetch;

				// Let go of by the fetch itself when it's done - not by whoever waits on it, who may have given up first.
				_ = fetch.ContinueWith(static done => {
					lock (FetchGate) {
						if (_fetching == done) {
							_fetching = null;
						}
					}
				}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			}
		}

		return await fetch.WaitAsync(ct).ConfigureAwait(false);
	}

	/// <summary>The fetch itself, shared by everything asking at the time - so cut off only by its own time limit, never by one of them.</summary>
	private static async Task<(IReadOnlyList<AddonEntry>? List, Said Why)> FetchAsync() {
		byte[] json;
		string signature;

		try {
			json = await Http.GetByteArrayAsync(IndexUrl).ConfigureAwait(false);
			signature = await Http.GetStringAsync(SignatureUrl).ConfigureAwait(false);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
			Log.Debug($"add-ons: couldn't get the list from GitHub: {Log.Describe(e)}");
			Said down = new("couldn't get the list of add-ons from GitHub - try again in a while");
			_failed = (DateTime.UtcNow, down);

			return (null, down);
		}

		(IReadOnlyList<AddonEntry>? list, Said why) = Take(json, signature);

		if (list == null) {
			Log.Warn(why);
			_failed = (DateTime.UtcNow, why);

			return (null, why);
		}

		_cached = (DateTime.UtcNow, list);
		_failed = null;

		return (list, default);
	}

	/// <summary>Install from a list already verified, with the download handed in - <see cref="InstallAsync"/>, and the checks' way in.</summary>
	internal static async Task<(bool Ok, Said Message)> InstallFromAsync(IReadOnlyList<AddonEntry> list, string id, Func<string, CancellationToken, Task<byte[]>> download, CancellationToken ct) {
		AddonEntry? entry = list.FirstOrDefault(e => string.Equals(e.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

		if (entry == null) {
			return (false, new Said("there's no official add-on called '{0}' - 'addons' lists them", id ?? ""));
		}

		if (!Fits(entry, Build.Version)) {
			return (false, new Said("{0} needs nocat.farm {1} or newer - this is {2}; update first", entry.Name, entry.MinAppVersion, Build.Version));
		}

		// Never backwards: a list offering an older one than is installed is an old list, or a mistake. Removing it is the way back.
		if (InstalledVersion(entry) is { } have && Older(entry.Version, have)) {
			return (false, new Said("{0} {1} is installed - the list offers the older {2}, so nothing is changed", entry.Name, have, entry.Version));
		}

		byte[] file;

		try {
			file = await download(entry.Url, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"add-ons: downloading {entry.Id}: {Log.Describe(e)}");

			return (false, new Said("couldn't download {0} from GitHub - try again in a while", entry.Name));
		}

		if (CheckFile(entry, file) is { IsEmpty: false } bad) {
			Log.Warn(bad);

			return (false, bad);
		}

		string folder = PluginHost.Folder;
		string target = Path.Combine(folder, entry.File);

		try {
			Directory.CreateDirectory(folder);
			File.Delete(target + PluginHost.RemoveSuffix);   // an uninstall waiting for a restart is called off

			// Loaded right now, it's held open: in beside it, put in place at the next start. The same when the file turns out
			// to be held all the same (Windows refuses to replace a DLL anything has open).
			if (PluginHost.IsLoaded(entry.File)) {
				await AtomicFile.WriteAsync(target + PluginHost.NewSuffix, file).ConfigureAwait(false);
			} else {
				try {
					await AtomicFile.WriteAsync(target, file).ConfigureAwait(false);
					File.Delete(target + PluginHost.NewSuffix);   // an older install still waiting would be put over this one
				} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(target)) {
					Log.Debug($"add-ons: {entry.File} is held ({Log.Describe(e)}) - it goes in at the next start");
					await AtomicFile.WriteAsync(target + PluginHost.NewSuffix, file).ConfigureAwait(false);
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return (false, new Said("couldn't save {0} in {1}: {2}", entry.File, folder, Log.Scrub(e.Message)));
		}

		// Plugins on, and this one's own switch on: installing it is asking for it to run.
		bool turnedOn;

		lock (ConfigStore.GlobalEditGate) {
			turnedOn = !Live.Global.PluginsEnabled;
			Live.Global.PluginsEnabled = true;
			Live.Global.DisabledPlugins = [.. Live.Global.DisabledPlugins.Where(n => !string.Equals(n, entry.Plugin, StringComparison.OrdinalIgnoreCase))];
			ConfigStore.SaveGlobal(Live.Global);
		}

		Log.Good(new Said("add-on {0} {1} installed - restart nocat.farm to load it", entry.Name, entry.Version));

		return (true, turnedOn
			? new Said("{0} {1} is installed, and Load plugins is now on. Restart nocat.farm to load it.", entry.Name, entry.Version)
			: new Said("{0} {1} is installed. Restart nocat.farm to load it.", entry.Name, entry.Version));
	}

	/// <summary>Install one by its id, from the list on GitHub, every check made (see the class remarks).</summary>
	public static async Task<(bool Ok, Said Message)> InstallAsync(string id, CancellationToken ct = default) {
		(IReadOnlyList<AddonEntry>? list, Said why) = await ListAsync(fresh: true, ct).ConfigureAwait(false);

		return list == null ? (false, why) : await InstallFromAsync(list, id, DownloadAsync, ct).ConfigureAwait(false);
	}

	/// <summary>The file, from the address the verified list gave - a release file on GitHub, no bigger than <see cref="MaxBytes"/>.</summary>
	private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct) {
		if (!FromGitHub(url)) {
			throw new InvalidOperationException("not a nocat.farm release address");
		}

		using HttpResponseMessage r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		r.EnsureSuccessStatusCode();

		if (r.Content.Headers.ContentLength is > MaxBytes) {
			throw new InvalidOperationException("too big for an add-on");
		}

		await using Stream body = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		using MemoryStream kept = new();
		byte[] buffer = new byte[81920];
		int read;

		while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
			if (kept.Length + read > MaxBytes) {
				throw new InvalidOperationException("too big for an add-on");
			}

			kept.Write(buffer, 0, read);
		}

		return kept.ToArray();
	}

	/// <summary>
	/// Which installed file 'addons remove &lt;name&gt;' means, and what to call it: by the list in hand, else the last list kept
	/// here, else the file in plugins/ itself - "rep4rep" finds nocatfarm.rep4rep.dll or rep4rep.dll, a file name is taken as
	/// it is. Taking one out needs no fresh list: GitHub down, or the add-on gone from the list, mustn't leave it stuck in.
	/// </summary>
	internal static (string File, string Name)? Installed(IReadOnlyList<AddonEntry>? list, string? id) {
		string want = id?.Trim() ?? "";

		foreach (IReadOnlyList<AddonEntry>? from in new[] { list, Kept() }) {
			if (from?.FirstOrDefault(e => string.Equals(e.Id, want, StringComparison.OrdinalIgnoreCase)) is { } entry) {
				return (entry.File, entry.Name);
			}
		}

		// Only a plain name or file name - never a path - and only a file that is there.
		string[] files = FileRx().IsMatch(want) ? [want] : IdRx().IsMatch(want.ToLowerInvariant()) ? [$"nocatfarm.{want}.dll", $"{want}.dll"] : [];

		foreach (string file in files) {
			string target = Path.Combine(PluginHost.Folder, file);

			if (File.Exists(target) || File.Exists(target + PluginHost.NewSuffix)) {
				return (file, want);
			}
		}

		return null;
	}

	/// <summary>
	/// Take an add-on out: its file goes (at the next start, when it's held open now). Its settings and what it saved stay in
	/// config/plugins, so installing it again picks up where it was. <paramref name="list"/> may be null (see <see cref="Installed"/>).
	/// </summary>
	public static Said Uninstall(IReadOnlyList<AddonEntry>? list, string id) {
		if (Installed(list, id) is not { } found) {
			return new Said("there's no add-on called '{0}' in plugins/ - 'plugins' lists what is", id ?? "");
		}

		string target = Path.Combine(PluginHost.Folder, found.File);

		if (!File.Exists(target) && !File.Exists(target + PluginHost.NewSuffix)) {
			return new Said("{0} isn't installed", found.Name);
		}

		try {
			bool held = PluginHost.IsLoaded(found.File);

			if (!held) {
				try {
					File.Delete(target);
				} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(target)) {
					// Held all the same - Windows refuses to delete a DLL anything has open: it goes at the next start.
					Log.Debug($"add-ons: {found.File} is held ({Log.Describe(e)}) - it goes at the next start");
					held = true;
				}
			}

			File.Delete(target + PluginHost.NewSuffix);

			if (held) {
				File.WriteAllText(target + PluginHost.RemoveSuffix, "");
				Log.Info(new Said("add-on {0} is removed at the next restart", found.Name));

				return new Said("{0} is removed when nocat.farm restarts. Its settings are kept, in case you put it back.", found.Name);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return new Said("couldn't remove {0}: {1}", found.File, Log.Scrub(e.Message));
		}

		Log.Info(new Said("add-on removed: {0}", found.File));

		return new Said("{0} is removed. Its settings are kept, in case you put it back.", found.Name);
	}

	/// <summary>What is installed of an add-on: the version loaded, a file waiting for a restart, or nothing.</summary>
	public static (bool Installed, string? Version, bool Waiting) StateOf(AddonEntry entry) {
		string target = Path.Combine(PluginHost.Folder, entry.File);
		bool waiting = File.Exists(target + PluginHost.NewSuffix) || File.Exists(target + PluginHost.RemoveSuffix);
		string? version = PluginHost.Discovered.FirstOrDefault(p => string.Equals(p.File, entry.File, StringComparison.OrdinalIgnoreCase)).Version;

		return (File.Exists(target) || File.Exists(target + PluginHost.NewSuffix), version, waiting);
	}

	/// <summary>Whether the list has a newer one than the version loaded.</summary>
	public static bool Newer(AddonEntry entry, string? loaded) =>
		(loaded != null) && Version.TryParse(loaded, out Version? have) && Version.TryParse(entry.Version, out Version? offered) && (offered > have);
}
