using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>Reading and setting a profile's privacy - the same page and request the Steam website uses.</summary>
public static partial class Privacy {
	/// <summary>One account's privacy. Each part is Steam's own number: 1 private, 2 friends only, 3 public.</summary>
	public sealed record Settings(int Profile, int Games, int Playtime, int Friends, int Inventory, int Gifts, int Comments);

	/// <summary>The parts, in the order the command takes them, with the name Steam's JSON uses for each.</summary>
	public static readonly (string Word, string Key)[] Parts = [
		("profile", "PrivacyProfile"), ("games", "PrivacyOwnedGames"), ("playtime", "PrivacyPlaytime"),
		("friends", "PrivacyFriendsList"), ("inventory", "PrivacyInventory"), ("gifts", "PrivacyInventoryGifts")
	];

	/// <summary>Read the current settings from the profile's settings page. Null if the page wouldn't say.</summary>
	public static async Task<Settings?> ReadAsync(Bot bot, CancellationToken ct = default) {
		string? page = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/edit/settings"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(page)) {
			return null;
		}

		// The page carries its whole state as JSON in an attribute, HTML-escaped. Rather than depend on the exact
		// attribute, find the privacy object wherever it sits once the escaping is undone.
		string text = WebUtility.HtmlDecode(page);
		Match block = PrivacyBlock().Match(text);

		if (!block.Success) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(block.Groups[1].Value);
			JsonElement root = doc.RootElement;

			if (!root.TryGetProperty("PrivacySettings", out JsonElement p)) {
				return null;
			}

			int Get(string key) => p.TryGetProperty(key, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;
			int comments = root.TryGetProperty("eCommentPermission", out JsonElement c) && c.TryGetInt32(out int cp) ? cp : -1;

			return new Settings(Get("PrivacyProfile"), Get("PrivacyOwnedGames"), Get("PrivacyPlaytime"), Get("PrivacyFriendsList"),
				Get("PrivacyInventory"), Get("PrivacyInventoryGifts"), comments);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Save new settings. True only when Steam says it took them.</summary>
	public static async Task<bool> WriteAsync(Bot bot, Settings s, CancellationToken ct = default) {
		string privacy = JsonSerializer.Serialize(new Dictionary<string, int> {
			["PrivacyProfile"] = s.Profile, ["PrivacyOwnedGames"] = s.Games, ["PrivacyPlaytime"] = s.Playtime,
			["PrivacyFriendsList"] = s.Friends, ["PrivacyInventory"] = s.Inventory, ["PrivacyInventoryGifts"] = s.Gifts
		});

		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["Privacy"] = privacy,
			["eCommentPermission"] = s.Comments.ToString(CultureInfo.InvariantCulture)
			// sessionid is injected by WebSession
		};

		string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/ajaxsetprivacy/"), form,
			new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/edit/settings"), ct).ConfigureAwait(false);

		return (body != null) && SuccessOne().IsMatch(body);
	}

	[GeneratedRegex("\"Privacy\"\\s*:\\s*(\\{\"PrivacySettings\"\\s*:\\s*\\{[^}]*\\}[^}]*\\})")]
	private static partial Regex PrivacyBlock();

	[GeneratedRegex("\"success\"\\s*:\\s*1\\b")]
	private static partial Regex SuccessOne();
}
