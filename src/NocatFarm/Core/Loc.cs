using System.Text;
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
/// Log lines go through this too, and they land in two different places with two different rules.
///
/// On screen - the window and the dashboard console - a line keeps its Said and is rendered afresh every time it
/// is drawn. Switch language and the lines already on screen change with it, including ones written hours ago.
///
/// In the file on disk, a line is rendered once, when it is written, in whatever language was selected at that
/// moment. That is why an old log can have a run of Polish in the middle of it - not a bug, just a record of what
/// the app was set to at the time. A log wants to be a fixed record, so it is never rewritten afterwards.
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
			Log.Debug(new Said("couldn't read the {0} language pack: {1}", code, e.Message));
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
				Log.Debug(new Said("couldn't collect the translations of \"{0}\": {1}", english, e.Message));
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
	/// <remarks>
	/// One pass over the sentence, not one Replace per value.
	///
	/// Replace-per-value re-reads text it has already written, so a value containing a placeholder gets
	/// substituted into and then substituted again. It is not hypothetical: the rep4rep line passed a status
	/// whose own text was "{0}/{1} today - done", and the next pass replaced the {1} sitting INSIDE it,
	/// printing "rep4rep: {0}/20m today - done - next look in 20m". A game or account name with a brace in it
	/// would do the same to any sentence in the app.
	///
	/// Walking once and copying each value in verbatim makes a substituted value final by construction.
	/// </remarks>
	public static string T(string english, params object?[] args) {
		string text = T(english);

		if ((args.Length == 0) || (text.IndexOf('{') < 0)) {
			return text;
		}

		StringBuilder built = new(text.Length + 32);

		for (int i = 0; i < text.Length; i++) {
			// {{ and }} are literal braces and pass straight through, same as they were written.
			if (((text[i] == '{') || (text[i] == '}')) && (i + 1 < text.Length) && (text[i + 1] == text[i])) {
				built.Append(text[i]);
				i++;

				continue;
			}

			int close = text.IndexOf('}', i + 1);

			if ((text[i] != '{') || (close < 0) || !int.TryParse(text.AsSpan(i + 1, close - i - 1), out int slot)) {
				built.Append(text[i]);

				continue;
			}

			// A Func is evaluated HERE rather than wherever the sentence was assembled.
			//
			// Most values are language-agnostic - a game name, a count, a clock time - but a few are not, and
			// one of those is Fmt.Clock, which says "tomorrow". Passed as a plain string it was formatted when
			// the status was built and then frozen: the achievements row read "已玩 2907.9 小时，下一个在
			// tomorrow 06:49 之后", the sentence translated around an English word baked into it hours earlier.
			// Passing `() => Fmt.Clock(x)` defers it to the moment somebody actually reads the line.
			built.Append((slot >= 0) && (slot < args.Length)
				? args[slot] switch {
					null => "",
					Func<string> lazy => lazy(),
					{ } other => other.ToString() ?? ""
				}
				: text.AsSpan(i, close - i + 1));   // no value for it - leave the placeholder visible, not blank

			i = close;
		}

		return built.ToString();
	}
}
