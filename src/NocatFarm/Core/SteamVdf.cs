using System.Text;

namespace NocatFarm.Core;

/// <summary>
/// Just enough of Valve's KeyValues text format to read and edit <c>config/loginusers.vdf</c> - the list of accounts
/// the Steam client remembers, each a block of flags (AccountName, RememberPassword, MostRecent, ...). Nothing here
/// touches a login token: those live elsewhere, encrypted, and nocat.farm never reads or writes them.
/// </summary>
/// <remarks>
/// A node is either a leaf (a quoted string value) or a map of child nodes, kept in the order they were read so the
/// file round-trips unchanged except for what we deliberately set. Tabs and the Valve layout are reproduced on write,
/// so the Steam client reads our edit exactly as it reads its own.
/// </remarks>
public sealed class SteamVdf {
	/// <summary>A leaf's text, or null for a map.</summary>
	public string? Value { get; private set; }

	/// <summary>Children in file order (a key can repeat, as Valve allows). Empty for a leaf.</summary>
	public List<KeyValuePair<string, SteamVdf>> Children { get; } = [];

	/// <summary>
	/// The exact text between the quotes as it was read, escapes and all, for a value we did NOT change - so an untouched
	/// value is written back byte for byte, never re-escaped. Null once we set the value ourselves (then it's escaped fresh).
	/// </summary>
	private string? _rawValue;

	/// <summary>The same for a key: its raw text as read, so a key we never edit round-trips exactly.</summary>
	private string? _rawKey;

	private SteamVdf() { }

	private SteamVdf(string value) => Value = value;

	private SteamVdf(string value, string raw) {
		Value = value;
		_rawValue = raw;
	}

	public bool IsMap => Value == null;

	/// <summary>The first child under <paramref name="key"/> (case-insensitive), or null.</summary>
	public SteamVdf? Child(string key) {
		foreach (KeyValuePair<string, SteamVdf> kv in Children) {
			if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) {
				return kv.Value;
			}
		}

		return null;
	}

	/// <summary>The leaf value under <paramref name="key"/>, or null when it's missing or a map.</summary>
	public string? Leaf(string key) => Child(key)?.Value;

	/// <summary>Set (or add) a leaf child to <paramref name="value"/>, keeping its place (and its raw key) when it's already there.</summary>
	public void SetLeaf(string key, string value) {
		for (int i = 0; i < Children.Count; i++) {
			if (string.Equals(Children[i].Key, key, StringComparison.OrdinalIgnoreCase)) {
				// Keep the existing key node's raw key (so it writes back as it was); only the value is ours now, escaped fresh.
				var replacement = new SteamVdf(value) { _rawKey = Children[i].Value._rawKey };
				Children[i] = new KeyValuePair<string, SteamVdf>(Children[i].Key, replacement);

				return;
			}
		}

		Children.Add(new KeyValuePair<string, SteamVdf>(key, new SteamVdf(value)));
	}

	/// <summary>Parse a KeyValues document. Throws <see cref="FormatException"/> on anything it can't read.</summary>
	public static SteamVdf Parse(string text) {
		int i = 0;
		SteamVdf root = new();

		while (true) {
			SkipSpace(text, ref i);

			if (i >= text.Length) {
				break;
			}

			string key = ReadString(text, ref i, out string rawKey);
			SkipSpace(text, ref i);

			if ((i < text.Length) && (text[i] == '{')) {
				i++;
				SteamVdf child = ParseMap(text, ref i);
				child._rawKey = rawKey;
				root.Children.Add(new KeyValuePair<string, SteamVdf>(key, child));
			} else {
				string value = ReadString(text, ref i, out string rawValue);
				root.Children.Add(new KeyValuePair<string, SteamVdf>(key, new SteamVdf(value, rawValue) { _rawKey = rawKey }));
			}
		}

		return root;
	}

	private static SteamVdf ParseMap(string text, ref int i) {
		SteamVdf map = new();

		while (true) {
			SkipSpace(text, ref i);

			if (i >= text.Length) {
				throw new FormatException("unterminated block");
			}

			if (text[i] == '}') {
				i++;

				return map;
			}

			string key = ReadString(text, ref i, out string rawKey);
			SkipSpace(text, ref i);

			if (i >= text.Length) {
				throw new FormatException("missing value");
			}

			if (text[i] == '{') {
				i++;
				SteamVdf child = ParseMap(text, ref i);
				child._rawKey = rawKey;
				map.Children.Add(new KeyValuePair<string, SteamVdf>(key, child));
			} else {
				string value = ReadString(text, ref i, out string rawValue);
				map.Children.Add(new KeyValuePair<string, SteamVdf>(key, new SteamVdf(value, rawValue) { _rawKey = rawKey }));
			}
		}
	}

	private static void SkipSpace(string text, ref int i) {
		while (i < text.Length) {
			char c = text[i];

			if (c is ' ' or '\t' or '\r' or '\n') {
				i++;
			} else if ((c == '/') && (i + 1 < text.Length) && (text[i + 1] == '/')) {
				while ((i < text.Length) && (text[i] != '\n')) {
					i++;
				}
			} else {
				break;
			}
		}
	}

	private static string ReadString(string text, ref int i, out string raw) {
		if ((i >= text.Length) || (text[i] != '"')) {
			throw new FormatException("expected a quoted string");
		}

		i++;
		int rawStart = i;
		StringBuilder sb = new();

		while (i < text.Length) {
			char c = text[i++];

			if (c == '"') {
				// The exact bytes between the quotes, so an untouched value round-trips verbatim (no backslash lost on an
				// escape we don't know, no real newline where the file had a literal "\n").
				raw = text.Substring(rawStart, (i - 1) - rawStart);

				return sb.ToString();
			}

			if ((c == '\\') && (i < text.Length)) {
				char n = text[i++];
				sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', _ => n });
			} else {
				sb.Append(c);
			}
		}

		throw new FormatException("unterminated string");
	}

	/// <summary>Back to text, in Valve's own layout: tab indent, key and value a tab apart, blocks in braces.</summary>
	public string Serialize() {
		StringBuilder sb = new();

		foreach (KeyValuePair<string, SteamVdf> kv in Children) {
			Write(sb, kv.Key, kv.Value, 0);
		}

		return sb.ToString();
	}

	private static void Write(StringBuilder sb, string key, SteamVdf node, int depth) {
		string indent = new('\t', depth);
		// A key we never edited goes back exactly as it came in; one we added ourselves is escaped fresh.
		string keyText = node._rawKey ?? Escape(key);

		if (node.IsMap) {
			sb.Append(indent).Append('"').Append(keyText).Append('"').Append('\n');
			sb.Append(indent).Append('{').Append('\n');

			foreach (KeyValuePair<string, SteamVdf> kv in node.Children) {
				Write(sb, kv.Key, kv.Value, depth + 1);
			}

			sb.Append(indent).Append('}').Append('\n');
		} else {
			// Likewise the value: the raw text for one we left alone, a fresh escape only for one we set.
			string valueText = node._rawValue ?? Escape(node.Value ?? "");
			sb.Append(indent).Append('"').Append(keyText).Append("\"\t\t\"").Append(valueText).Append('"').Append('\n');
		}
	}

	/// <summary>
	/// 1.8.2: change one leaf's value in a KeyValues text WITHOUT rewriting the rest - every other byte (layout, line
	/// endings, comments, the other values) stays exactly as it was. For <c>config/config.vdf</c>, which is Steam's big
	/// settings file: only the value between the quotes is replaced, wherever the key appears with value
	/// <paramref name="from"/> (case-insensitive key, exact value). Null when there's nothing to change, or when the text
	/// can't be read - then the file is left alone.
	/// </summary>
	public static string? ReplaceLeafInPlace(string text, string key, string from, string to) {
		List<(int Start, int Length)> spans = [];
		int i = text.StartsWith('﻿') ? 1 : 0;   // a byte-order mark is kept, not read
		int depth = 0;

		try {
			while (true) {
				SkipSpace(text, ref i);

				if (i >= text.Length) {
					break;
				}

				if (text[i] == '}') {
					if (--depth < 0) {
						return null;
					}

					i++;

					continue;
				}

				string k = ReadString(text, ref i, out _);
				SkipSpace(text, ref i);

				if (i >= text.Length) {
					return null;
				}

				if (text[i] == '{') {
					depth++;
					i++;

					continue;
				}

				int start = i + 1;
				string value = ReadString(text, ref i, out string raw);

				if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && string.Equals(value, from, StringComparison.Ordinal)) {
					spans.Add((start, raw.Length));
				}
			}
		} catch (FormatException) {
			return null;
		}

		if ((depth != 0) || (spans.Count == 0)) {
			return null;
		}

		StringBuilder sb = new(text);

		// Back to front, so the earlier positions stay right.
		for (int n = spans.Count - 1; n >= 0; n--) {
			sb.Remove(spans[n].Start, spans[n].Length).Insert(spans[n].Start, Escape(to));
		}

		return sb.ToString();
	}

	private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
