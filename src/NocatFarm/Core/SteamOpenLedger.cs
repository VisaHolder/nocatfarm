using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The one thing the Open in Steam button remembers per account, in config/state: that you pressed Turn on after
/// seeing its preview. Kept here, beside the account's state and not in its settings, for the same reason spare items
/// and Steam Points keep theirs here - a settings file or a backup copied from another PC can't switch it on; only
/// the preview's Turn on can.
/// </summary>
public sealed class SteamOpenLedger {
	/// <summary>You pressed Turn on after the preview: the button may sign the Steam client into this account.</summary>
	public bool Approved { get; set; }

	[System.Text.Json.Serialization.JsonIgnore]
	public string Account { get; private set; } = "";

	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, SteamOpenLedger> Loaded = new(StringComparer.OrdinalIgnoreCase);
	private bool _onDisk;

	/// <summary>Where this one is kept: an account's own file, or the every-account one.</summary>
	private string _path = "";

	private static string PathOf(string name) => Path.Combine(ConfigStore.ConfigDir, "state", $"steamopen-{name}.json");

	/// <summary>
	/// 1.8.2: the go-ahead of "Open in Steam buttons on every account" - kept the same way, in a file of its own, named so
	/// no account's file can ever be it (an account's is "steamopen-&lt;name&gt;.json").
	/// </summary>
	private static string EveryonePath => Path.Combine(ConfigStore.ConfigDir, "state", "steamopen_all.json");

	/// <summary>1.8.2: the go-ahead for the button on every account.</summary>
	public static SteamOpenLedger Everyone() => LoadFrom(EveryonePath, "every account");

	public static SteamOpenLedger Load(string account) => LoadFrom(PathOf(account), account);

	private static SteamOpenLedger LoadFrom(string path, string account) {
		lock (Gate) {
			if (Loaded.TryGetValue(path, out SteamOpenLedger? held) && (!held._onDisk || File.Exists(path))) {
				return held;
			}

			SteamOpenLedger? read = null;

			try {
				if (File.Exists(path)) {
					read = JsonSerializer.Deserialize<SteamOpenLedger>(File.ReadAllText(path));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				// Started over: the button has to be turned on again through its preview.
				Log.Failed("couldn't read what Open in Steam remembers", e, account);
			}

			bool fromFile = read != null;
			read ??= new SteamOpenLedger();
			read.Account = account;
			read._path = path;
			read._onDisk = fromFile;
			Loaded[path] = read;

			return read;
		}
	}

	/// <summary>Only the checks: as if the program had just started - every ledger is read from its file again.</summary>
	public static void ForgetLoaded() {
		lock (Gate) {
			Loaded.Clear();
		}
	}

	public void Save() {
		if (Account.Length == 0) {
			return;
		}

		lock (Gate) {
			try {
				string path = _path.Length > 0 ? _path : PathOf(Account);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				AtomicFile.Write(path, JsonSerializer.Serialize(this));
				_onDisk = true;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed("couldn't save what Open in Steam remembers", e, Account);
			}
		}
	}
}
