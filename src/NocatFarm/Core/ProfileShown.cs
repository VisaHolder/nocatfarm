using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.3: the items an account shows on its profile - what it has equipped (background, mini-profile background, avatar
/// frame, animated avatar, profile theme) and every item in its showcases. Spare items never sells them or turns them
/// into gems: selling the equipped background takes it off the profile.
/// </summary>
/// <remarks>
/// Equipped: IPlayerService/GetProfileItemsEquipped - each slot's communityitemid is that item's asset id in the Steam
/// inventory (753/6). Showcases: the profile page links each item shown as "inventory/#753_6_&lt;asset id&gt;".
/// </remarks>
public static partial class ProfileShown {
	/// <summary>Tries before falling back on the last good read.</summary>
	public const int Tries = 3;

	/// <summary>How long the last good read stands in when Steam won't answer now.</summary>
	public static readonly TimeSpan LastGoodFor = TimeSpan.FromHours(24);

	private static readonly Dictionary<string, (HashSet<ulong> Shown, DateTime At)> LastGood = new(StringComparer.Ordinal);
	private static readonly Lock Gate = new();

	/// <summary>
	/// The asset ids shown: read up to <see cref="Tries"/> times (a few seconds apart), else the last good read of the last
	/// <see cref="LastGoodFor"/>. Null only when neither - half an answer is a reason to wait, never to guess.
	/// </summary>
	public static async Task<HashSet<ulong>?> ReadAsync(Bot bot, CancellationToken ct) {
		for (int i = 0; i < Tries; i++) {
			if (i > 0) {
				await Task.Delay(Rng.Seconds(5, 15), ct).ConfigureAwait(false);
			}

			string? equipped = await bot.Web.ApiGetAsync("IPlayerService", "GetProfileItemsEquipped", new Dictionary<string, string> {
				["steamid"] = bot.SteamId.ToString(CultureInfo.InvariantCulture)
			}, ct).ConfigureAwait(false);
			string? page = equipped == null ? null : await bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/"), ct).ConfigureAwait(false);

			if (Parse(equipped, page) is { } shown) {
				lock (Gate) {
					LastGood[bot.Name] = (shown, DateTime.UtcNow);
				}

				return shown;
			}
		}

		lock (Gate) {
			if (LastGood.TryGetValue(bot.Name, out var last) && (DateTime.UtcNow - last.At < LastGoodFor)) {
				Log.Debug(new Said("couldn't read what's on its profile - going by the last read ({0} ago)", Fmt.Hm((int) (DateTime.UtcNow - last.At).TotalMinutes)), bot.Name);

				return last.Shown;
			}
		}

		return null;
	}

	/// <summary>Both answers together; null when either isn't what Steam sends.</summary>
	public static HashSet<ulong>? Parse(string? equipped, string? page) {
		if (string.IsNullOrEmpty(page) || !page.Contains("g_rgProfileData", StringComparison.Ordinal)) {
			return null;
		}

		HashSet<ulong> shown = [];

		try {
			using JsonDocument doc = JsonDocument.Parse(equipped ?? "");

			if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("response", out JsonElement response)
				|| (response.ValueKind != JsonValueKind.Object)) {
				return null;
			}

			foreach (JsonProperty slot in response.EnumerateObject()) {
				if ((slot.Value.ValueKind == JsonValueKind.Object) && slot.Value.TryGetProperty("communityitemid", out JsonElement id)
					&& ulong.TryParse(id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong asset)
					&& (asset != 0)) {
					shown.Add(asset);
				}
			}
		} catch (JsonException) {
			return null;
		}

		foreach (Match m in ShowcaseLink().Matches(page)) {
			if (ulong.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong asset)) {
				shown.Add(asset);
			}
		}

		return shown;
	}

	[GeneratedRegex(@"inventory/#753_6_(\d{1,20})")]
	private static partial Regex ShowcaseLink();
}
