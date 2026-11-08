using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace NocatFarm.Addons.Rep4Rep;

/// <summary>
/// The add-on's own translations: lang/&lt;code&gt;.json inside the DLL, English text to translated, in the app's language
/// (<c>host.Language</c>). English is the key, so a missing one simply stays English. tools/check-translations.py checks every
/// key given to T or R here against every pack, the same as the app's own.
/// </summary>
internal static class Texts {
	/// <summary>The language to translate into - the host's, read at each call so a change in Settings follows at once.</summary>
	public static Func<string> Language { get; set; } = static () => "en";

	private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Packs = new(StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyDictionary<string, string> Pack(string code) =>
		Packs.GetOrAdd(code, static c => {
			if (c.Equals("en", StringComparison.OrdinalIgnoreCase)) {
				return new Dictionary<string, string>();
			}

			try {
				using Stream? s = typeof(Texts).Assembly.GetManifestResourceStream($"lang.{c}.json");

				return s == null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
			} catch (JsonException) {
				return new Dictionary<string, string>();
			}
		});

	/// <summary>
	/// English kept as it is - a reason stored on disk, which has to mean the same next week - and translated when shown,
	/// with T(). Marked so the translation check knows it's a key.
	/// </summary>
	public static string R(string english) => english;

	/// <summary>The English, translated - and {0}, {1} filled in, invariant so numbers look the same everywhere.</summary>
	public static string T(string english, params object?[] args) {
		string text = Pack(Language()).TryGetValue(english, out string? t) && (t.Length > 0) ? t : english;

		if (args.Length == 0) {
			return text;
		}

		try {
			return string.Format(CultureInfo.InvariantCulture, text, args);
		} catch (FormatException) {
			// A translation with a broken placeholder must not cost the line - the English still has them right.
			return string.Format(CultureInfo.InvariantCulture, english, args);
		}
	}
}
