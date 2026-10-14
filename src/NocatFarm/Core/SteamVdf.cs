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

	private SteamVdf() { }

	private SteamVdf(string value) => Value = value;

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

	/// <summary>Set (or add) a leaf child to <paramref name="value"/>, keeping its place when it's already there.</summary>
	public void SetLeaf(string key, string value) {
		for (int i = 0; i < Children.Count; i++) {
			if (string.Equals(Children[i].Key, key, StringComparison.OrdinalIgnoreCase)) {
				Children[i] = new KeyValuePair<string, SteamVdf>(Children[i].Key, new SteamVdf(value));

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

			string key = ReadString(text, ref i);
			SkipSpace(text, ref i);

			if ((i < text.Length) && (text[i] == '{')) {
				i++;
				root.Children.Add(new KeyValuePair<string, SteamVdf>(key, ParseMap(text, ref i)));
			} else {
				root.Children.Add(new KeyValuePair<string, SteamVdf>(key, new SteamVdf(ReadString(text, ref i))));
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

			string key = ReadString(text, ref i);
			SkipSpace(text, ref i);

			if (i >= text.Length) {
				throw new FormatException("missing value");
			}

			if (text[i] == '{') {
				i++;
				map.Children.Add(new KeyValuePair<string, SteamVdf>(key, ParseMap(text, ref i)));
			} else {
				map.Children.Add(new KeyValuePair<string, SteamVdf>(key, new SteamVdf(ReadString(text, ref i))));
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

	private static string ReadString(string text, ref int i) {
		if ((i >= text.Length) || (text[i] != '"')) {
			throw new FormatException("expected a quoted string");
		}

		i++;
		StringBuilder sb = new();

		while (i < text.Length) {
			char c = text[i++];

			if (c == '"') {
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

		if (node.IsMap) {
			sb.Append(indent).Append('"').Append(Escape(key)).Append('"').Append('\n');
			sb.Append(indent).Append('{').Append('\n');

			foreach (KeyValuePair<string, SteamVdf> kv in node.Children) {
				Write(sb, kv.Key, kv.Value, depth + 1);
			}

			sb.Append(indent).Append('}').Append('\n');
		} else {
			sb.Append(indent).Append('"').Append(Escape(key)).Append("\"\t\t\"").Append(Escape(node.Value ?? "")).Append('"').Append('\n');
		}
	}

	private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
