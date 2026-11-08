using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>
/// 1.9.0: rep4rep left the app and became an add-on (addons/rep4rep). Everything a 1.8.x config had for it moves into the
/// add-on's own files as the config is read - whether or not the add-on is installed yet - so installing it picks up
/// exactly where it was, and an old config never trips anything:
///
///   nocatFarm.json   Rep4RepApiToken (still encrypted), Rep4RepAutoAddProfiles, Rep4RepPointsRefreshMinutes -> the add-on's
///                    settings; Rep4RepHoldUntil / From -> its state; Rep4RepEnabled decides whether accounts stay on;
///                    SendRep4Rep -> SendAddons
///   &lt;account&gt;.json  Rep4Rep, Rep4RepDailyCap, the gaps, the hours, Rep4RepLearnCap, Rep4RepRetryRefused -> its per-account settings
///   state/rep4rep-&lt;account&gt;.json  -> the add-on's state, and the last 24h into the app's comment count (Plugins/Comments.cs)
///
/// The old keys are dropped from the config by the save that follows (the settings are gone from the app). A value the
/// add-on's files already have is never overwritten: a second run, or a config restored from an old backup later, only
/// fills in what's missing.
/// </summary>
public static partial class ConfigStore {
	/// <summary>The add-on's plugin name - what its files under config/plugins are called.</summary>
	internal const string Rep4RepPlugin = "rep4rep";

	internal static string Rep4RepSettingsFile => Path.Combine(ConfigDir, "plugins", $"{Rep4RepPlugin}.settings.json");
	internal static string Rep4RepStateFile => Path.Combine(ConfigDir, "plugins", $"{Rep4RepPlugin}.json");

	/// <summary>Whether rep4rep was switched on at all in 1.8.x - kept in the add-on's settings so an account's flag means what it did.</summary>
	internal const string LegacyEnabledKey = "_MigratedRep4RepEnabled";

	private static readonly Lock Rep4RepGate = new();

	private static readonly (string Old, string New)[] GlobalKeys = [
		("Rep4RepAutoAddProfiles", "AutoAddProfiles"),
		("Rep4RepPointsRefreshMinutes", "PointsRefreshMinutes")
	];

	private static readonly (string Old, string New)[] AccountKeys = [
		("Rep4RepDailyCap", "DailyCap"),
		("Rep4RepGapMinMinutes", "GapMinMinutes"),
		("Rep4RepGapMaxMinutes", "GapMaxMinutes"),
		("Rep4RepStartHour", "StartHour"),
		("Rep4RepEndHour", "EndHour"),
		("Rep4RepLearnCap", "LearnCap"),
		("Rep4RepRetryRefused", "RetryRefused")
	];

	/// <summary>The value as the add-on's settings keep it: text, booleans as "true"/"false".</summary>
	private static string? AsText(JsonElement v) => v.ValueKind switch {
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		JsonValueKind.Number => v.GetRawText(),
		JsonValueKind.String => v.GetString(),
		_ => null
	};

	private static Dictionary<string, JsonElement> Props(JsonDocument doc) {
		Dictionary<string, JsonElement> all = new(StringComparer.OrdinalIgnoreCase);

		if (doc.RootElement.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty p in doc.RootElement.EnumerateObject()) {
				all[p.Name] = p.Value;
			}
		}

		return all;
	}

	/// <summary>The add-on's settings file as it is, case-insensitive (the host reads it that way).</summary>
	private static Dictionary<string, string> ReadPluginSettings() {
		Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

		if (File.Exists(Rep4RepSettingsFile)) {
			foreach ((string k, string v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Rep4RepSettingsFile)) ?? []) {
				values[k] = v;
			}
		}

		return values;
	}

	private static void WritePluginSettings(Dictionary<string, string> values) {
		Directory.CreateDirectory(Path.GetDirectoryName(Rep4RepSettingsFile)!);
		AtomicFile.Write(Rep4RepSettingsFile, JsonSerializer.Serialize(values));
	}

	/// <summary>A string value, or null for anything else - a null, a number, a list. Every 1.8.x file has "Rep4RepHoldUntil": null.</summary>
	private static string? Text(Dictionary<string, JsonElement> props, string key) =>
		props.TryGetValue(key, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() : null;

	/// <summary>A date the file holds as text, in UTC - or null when it's null, missing or not a date.</summary>
	private static DateTime? Date(Dictionary<string, JsonElement> props, string key) =>
		props.TryGetValue(key, out JsonElement v) && (v.ValueKind == JsonValueKind.String) && v.TryGetDateTime(out DateTime d) ? d.ToUniversalTime() : null;

	/// <summary>The global part. True when the file had any of the old keys - the save that follows drops them.</summary>
	/// <remarks>Never throws: whatever is odd in an old file, the config still loads (the move is tried again next start).</remarks>
	public static bool MigrateRep4RepGlobal(GlobalConfig g, string json) {
		try {
			return MigrateRep4RepGlobalOnce(g, json);
		} catch (Exception e) {
			Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)));

			return false;
		}
	}

	private static bool MigrateRep4RepGlobalOnce(GlobalConfig g, string json) {
		Dictionary<string, JsonElement> props;

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			props = Props(doc).ToDictionary(static kv => kv.Key, static kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
		} catch (JsonException) {
			return false;
		}

		bool old = props.Keys.Any(static k => k.StartsWith("Rep4Rep", StringComparison.OrdinalIgnoreCase) || k.Equals("SendRep4Rep", StringComparison.OrdinalIgnoreCase));

		// "Send rep4rep comments" is "Send add-on messages" now - unless the file already says that one.
		if (props.TryGetValue("SendRep4Rep", out JsonElement send) && !props.ContainsKey(nameof(GlobalConfig.SendAddons))
			&& (send.ValueKind is JsonValueKind.True or JsonValueKind.False)) {
			g.SendAddons = send.ValueKind == JsonValueKind.True;
		}

		if (!old) {
			// Nothing in the config - but 1.8.x commenting records may still be lying in config/state.
			MigrateRep4RepState(null, null);

			return false;
		}

		try {
			lock (Rep4RepGate) {
				Dictionary<string, string> values = ReadPluginSettings();

				void Put(string key, string? value) {
					if ((value != null) && !values.ContainsKey(key)) {
						values[key] = value;
					}
				}

				if (Text(props, "Rep4RepApiToken") is { Length: > 0 } stored) {
					// Still encrypted, the way the host keeps a secret setting: read, then sealed again for the plugin's file.
					string plain = Secrets.Unprotect(stored);

					if (plain.Length > 0) {
						Put("ApiKey", Secrets.Protect(plain, "plugin"));
					}
				}

				foreach ((string from, string to) in GlobalKeys) {
					if (props.TryGetValue(from, out JsonElement v)) {
						Put(to, AsText(v));
					}
				}

				// Off (or missing - it was off by default) means no account was commenting, whatever its own flag said.
				Put(LegacyEnabledKey, props.TryGetValue("Rep4RepEnabled", out JsonElement on) && (on.ValueKind == JsonValueKind.True) ? "true" : "false");
				WritePluginSettings(values);
			}

			MigrateRep4RepState(Date(props, "Rep4RepHoldUntil"), Date(props, "Rep4RepHoldFrom"));
			Log.Debug($"1.9.0: {Rep4RepPlugin}'s settings moved to the add-on's files (config/plugins)");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// Not dropped from the config when they couldn't be kept: the next start tries again.
			Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)));

			return false;
		}

		return true;
	}

	/// <summary>
	/// An import (ArchiSteamFarm's HumanIdler keys) bringing a rep4rep key and the accounts that commented: into the add-on's
	/// settings, the same as a 1.8.x config - a key already set here is kept. Returns whether anything was written.
	/// </summary>
	public static bool ImportRep4Rep(string? token, IEnumerable<string> accountsOn) {
		try {
			lock (Rep4RepGate) {
				Dictionary<string, string> values = ReadPluginSettings();
				bool changed = false;

				if (!string.IsNullOrEmpty(token) && !values.ContainsKey("ApiKey")) {
					values["ApiKey"] = Secrets.Protect(token, "plugin");
					changed = true;
				}

				foreach (string name in accountsOn) {
					changed |= values.TryAdd($"{name}:On", "true");
				}

				if (changed) {
					WritePluginSettings(values);
				}

				return changed;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)));

			return false;
		}
	}

	/// <summary>One account's part. True when its file had any of the old keys. Never throws (see the global part).</summary>
	public static bool MigrateRep4RepAccount(string name, string json) {
		try {
			return MigrateRep4RepAccountOnce(name, json);
		} catch (Exception e) {
			Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)), name);

			return false;
		}
	}

	private static bool MigrateRep4RepAccountOnce(string name, string json) {
		Dictionary<string, JsonElement> props;

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			props = Props(doc).ToDictionary(static kv => kv.Key, static kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
		} catch (JsonException) {
			return false;
		}

		if (!props.Keys.Any(static k => k.StartsWith("Rep4Rep", StringComparison.OrdinalIgnoreCase))) {
			return false;
		}

		try {
			lock (Rep4RepGate) {
				Dictionary<string, string> values = ReadPluginSettings();

				void Put(string key, string? value) {
					if ((value != null) && !values.ContainsKey($"{name}:{key}")) {
						values[$"{name}:{key}"] = value;
					}
				}

				bool wasOn = props.TryGetValue("Rep4Rep", out JsonElement r) && (r.ValueKind == JsonValueKind.True);
				bool masterOn = values.TryGetValue(LegacyEnabledKey, out string? m) && (m == "true");
				Put("On", wasOn && masterOn ? "true" : "false");

				foreach ((string from, string to) in AccountKeys) {
					if (props.TryGetValue(from, out JsonElement v)) {
						Put(to, AsText(v));
					}
				}

				WritePluginSettings(values);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)), name);

			return false;
		}

		return true;
	}

	/// <summary>
	/// The 1.8.x commenting records (config/state/rep4rep-&lt;account&gt;.json) into the add-on's state, and their last 24 hours
	/// into the app's own count - so neither the add-on's cap nor the app's ceiling starts again from zero. Each file is kept
	/// as .migrated once it's in. One that can't be read rests that account a day rather than letting it start at zero.
	/// </summary>
	internal static void MigrateRep4RepState(DateTime? holdUntil, DateTime? holdFrom) {
		string dir = Path.Combine(ConfigDir, "state");
		string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "rep4rep-*.json", SearchOption.TopDirectoryOnly) : [];

		if ((files.Length == 0) && (holdUntil == null)) {
			return;
		}

		lock (Rep4RepGate) {
			JsonObject root;

			try {
				root = File.Exists(Rep4RepStateFile) ? JsonNode.Parse(File.ReadAllText(Rep4RepStateFile)) as JsonObject ?? [] : [];
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				// The add-on's own file is there and unreadable: leave everything as it is, the add-on holds on its own.
				Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)));

				return;
			}

			JsonObject accounts = root["Accounts"] as JsonObject ?? [];
			root["Accounts"] = accounts;
			bool held = false;

			foreach (string file in files) {
				string bot = Path.GetFileNameWithoutExtension(file)["rep4rep-".Length..];

				if (bot.Length == 0) {
					continue;
				}

				JsonNode? record;

				try {
					record = JsonNode.Parse(File.ReadAllText(file));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
					// Fail safe, as 1.8.x did: unknown count, so no comments - for a day here, not for good.
					Log.Warn(new Said("can't read {0} ({1}) - no comments until it's fixed", Path.GetFileName(file), Log.Describe(e)), bot);
					record = new JsonObject {
						["BlockedUntil"] = DateTime.UtcNow.AddHours(24).Ticks,
						["BlockReason"] = "state unreadable - holding"
					};
				}

				if (!accounts.Any(a => a.Key.Equals(bot, StringComparison.OrdinalIgnoreCase)) && (record is JsonObject)) {
					accounts[bot] = record.DeepClone();
				}

				// The app's own count of the last 24 hours, so its ceiling knows these comments too.
				if (record?["Posts"] is JsonArray posts) {
					long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;

					foreach (long at in posts.Select(static p => (p is JsonValue jv) && jv.TryGetValue(out long t) ? t : 0).Where(t => t >= cutoff).Order()) {
						Plugins.Comments.Count(bot, new DateTime(at, DateTimeKind.Utc));
					}
				}

				try {
					File.Move(file, file + ".migrated", true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed($"keeping {Path.GetFileName(file)} aside after moving it to the rep4rep add-on", e, bot);
				}
			}

			if ((holdUntil is { } until) && (until > DateTime.UtcNow) && (root["HoldUntil"] == null)) {
				root["HoldUntil"] = until;
				root["HoldFrom"] = holdFrom ?? DateTime.UtcNow;
				held = true;
			}

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(Rep4RepStateFile)!);
				AtomicFile.Write(Rep4RepStateFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Warn(new Said("couldn't move rep4rep's settings to the add-on: {0}", Log.Describe(e)));
			}

			if (files.Length > 0 || held) {
				Log.Debug($"1.9.0: {files.Length} rep4rep record(s) moved to the rep4rep add-on's state");
			}
		}
	}
}
