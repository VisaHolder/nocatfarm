using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>Reading and setting a profile's privacy - the same page and request the Steam website uses.</summary>
public static partial class Privacy {
	/// <summary>One account's privacy. Each part is Steam's own number: 1 private, 2 friends only, 3 public.</summary>
	public sealed record Settings(int Profile, int Games, int Playtime, int Friends, int Inventory, int Gifts, int Comments);

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
			Log.Debug("privacy settings: the settings page had no privacy block in it", bot.Name);

			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(block.Groups[1].Value);
			JsonElement root = doc.RootElement;

			if (!root.TryGetProperty("PrivacySettings", out JsonElement p)) {
				Log.Debug("privacy settings: the page's privacy block had no PrivacySettings", bot.Name);

				return null;
			}

			int Get(string key) => p.TryGetProperty(key, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;
			int comments = root.TryGetProperty("eCommentPermission", out JsonElement c) && c.TryGetInt32(out int cp) ? cp : -1;

			return new Settings(Get("PrivacyProfile"), Get("PrivacyOwnedGames"), Get("PrivacyPlaytime"), Get("PrivacyFriendsList"),
				Get("PrivacyInventory"), Get("PrivacyInventoryGifts"), comments);
		} catch (JsonException e) {
			Log.Failed("privacy settings: couldn't read the page's privacy block", e, bot.Name);

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

		// A null body is already logged by the web session; an answer that isn't success:1 is not.
		if ((body != null) && !SuccessOne().IsMatch(body)) {
			Log.Debug($"privacy settings not saved - Steam answered: {Log.Scrub(body.Length > 150 ? body[..150] : body)}", bot.Name);

			return false;
		}

		return body != null;
	}

	[GeneratedRegex("\"Privacy\"\\s*:\\s*(\\{\"PrivacySettings\"\\s*:\\s*\\{[^}]*\\}[^}]*\\})")]
	private static partial Regex PrivacyBlock();

	[GeneratedRegex("\"success\"\\s*:\\s*1\\b")]
	private static partial Regex SuccessOne();
}
