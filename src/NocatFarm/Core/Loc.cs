using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Translating the text this program generates about itself.
/// </summary>
/// <remarks>
/// The dashboard translated its own furniture - tabs, labels, settings, tooltips - and then displayed, in the
/// middle of it, the one thing people actually read: what each account is doing right now. That came from here,
/// in English, whatever language was selected. A German user got a German shell wrapped around English content.
///
/// Same packs as the dashboard, read from wwwroot/lang. One source of truth for a translation, and a translator
/// who fixes a phrase fixes it in both places at once.
///
/// English is the key AND the fallback, exactly as on the front end: an untranslated string comes back as the
/// English it was written as, never as a missing-key marker. That is what makes it safe to add a status line
/// without touching ten JSON files in the same commit - it simply reads in English until somebody translates it.
///
/// Log lines are deliberately NOT run through this. A log is a diagnostic record: it wants to match the file on
/// disk, be searchable, and be quotable in a bug report by somebody who does not share your language. Status
/// text is for reading now; log text is for reading later, by whoever is helping.
/// </remarks>
public static class Loc {
	private static Dictionary<string, string> _map = [];
	private static string _loaded = "";

	/// <summary>Re-read the pack for whatever language is now selected. Cheap, and idempotent.</summary>
	public static void Refresh() {
		string code = Live.Global.Language ?? "en";

		if (code == _loaded) {
			return;
		}

		_loaded = code;
		_map = [];

		// English is the key, so there is no en.json and nothing to load for it.
		if (string.IsNullOrWhiteSpace(code) || code.Equals("en", StringComparison.OrdinalIgnoreCase)) {
			return;
		}

		try {
			string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "lang", $"{code}.json");

			if (!File.Exists(path)) {
				return;
			}

			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

			if (doc.RootElement.TryGetProperty("ui", out JsonElement ui)) {
				Dictionary<string, string> map = new(StringComparer.Ordinal);

				foreach (JsonProperty entry in ui.EnumerateObject()) {
					if (entry.Value.ValueKind == JsonValueKind.String) {
						map[entry.Name] = entry.Value.GetString() ?? entry.Name;
					}
				}

				_map = map;
			}
		} catch (Exception e) {
			Log.Debug($"couldn't read the {code} language pack: {e.Message}");
		}
	}

	/// <summary>The translation of <paramref name="english"/>, or the English itself.</summary>
	public static string T(string english) {
		Refresh();

		return _map.TryGetValue(english, out string? found) && (found.Length > 0) ? found : english;
	}

	/// <summary>Whether <paramref name="text"/> is <paramref name="english"/> - in any language.</summary>
	/// <remarks>
	/// Statuses are cached strings, written once by whichever module produced them. Change the language and a
	/// module keeps the words it last wrote until it next has something to say, so for a tick or two the app
	/// holds text in the OLD language. Anything asking "is this one off?" by comparing against the CURRENT
	/// translation misses that, and a resting module briefly counts as busy - which is precisely how a wall of
	/// idle rows appeared on every account card the moment the language changed.
	///
	/// Comparing against every known translation closes the window instead of narrowing it. The alternative -
	/// having modules carry an untranslated key alongside the text - is the tidier design and much the larger
	/// change; this buys the same correctness for the handful of words anything actually tests.
	/// </remarks>
	public static bool Is(string text, string english) =>
		string.Equals(text, english, StringComparison.Ordinal) || Variants(english).Contains(text);

	private static readonly Dictionary<string, HashSet<string>> Known = [];

	/// <summary>Every translation of one English string, across all the packs. Read once, then remembered.</summary>
	private static HashSet<string> Variants(string english) {
		lock (Known) {
			if (Known.TryGetValue(english, out HashSet<string>? found)) {
				return found;
			}

			HashSet<string> set = new(StringComparer.Ordinal) { english };

			try {
				string dir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "lang");

				if (Directory.Exists(dir)) {
					foreach (string file in Directory.EnumerateFiles(dir, "*.json")) {
						using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));

						if (doc.RootElement.TryGetProperty("ui", out JsonElement ui)
							&& ui.TryGetProperty(english, out JsonElement value)
							&& (value.ValueKind == JsonValueKind.String)
							&& value.GetString() is { Length: > 0 } word) {
							set.Add(word);
						}
					}
				}
			} catch (Exception e) {
				Log.Debug($"couldn't collect the translations of \"{english}\": {e.Message}");
			}

			Known[english] = set;

			return set;
		}
	}

	/// <summary>
	/// As <see cref="T(string)"/>, with {0}, {1}… filled in afterwards.
	///
	/// Placeholders rather than glued-together fragments, because word order is not the same everywhere and a
	/// sentence assembled from pieces can only ever be right in the language it was assembled for.
	/// </summary>
	public static string T(string english, params object?[] args) {
		string text = T(english);

		for (int i = 0; i < args.Length; i++) {
			text = text.Replace("{" + i + "}", args[i]?.ToString() ?? "", StringComparison.Ordinal);
		}

		return text;
	}
}
