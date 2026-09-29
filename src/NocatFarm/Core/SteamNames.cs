using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>
/// The Steam name behind a SteamID, for saying who a trade offer is from. Your own accounts are named by their
/// nocat.farm name; anyone else's name comes from their public profile, remembered for a day.
/// </summary>
public static partial class SteamNames {
	private static readonly ConcurrentDictionary<ulong, (string Name, DateTime At)> Known = new();

	/// <summary>The name to show - never empty; the SteamID when nothing better could be found.</summary>
	public static async Task<string> OfAsync(Bot via, ulong id, CancellationToken ct = default) {
		if (BotManager.Instance?.All.FirstOrDefault(b => b.SteamId == id) is { } own) {
			return own.Name;
		}

		if (Known.TryGetValue(id, out (string Name, DateTime At) hit) && (DateTime.UtcNow - hit.At < TimeSpan.FromDays(1))) {
			return hit.Name;
		}

		try {
			string? xml = await via.Web.GetAsync(new Uri($"https://steamcommunity.com/profiles/{id}/?xml=1"), ct).ConfigureAwait(false);

			if ((xml != null) && (NameRegex().Match(xml) is { Success: true } m) && (m.Groups[1].Value.Trim() is { Length: > 0 } name)) {
				Known[id] = (name, DateTime.UtcNow);

				return name;
			}
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
			// the ID will do
			Log.Failed($"couldn't look up the Steam name of {id}", e, via.Name);
		}

		return id.ToString();
	}

	[GeneratedRegex(@"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.Singleline)]
	private static partial Regex NameRegex();
}
