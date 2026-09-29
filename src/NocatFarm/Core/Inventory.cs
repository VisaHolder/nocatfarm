using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>Everything in one inventory context, read to the end rather than to the first page.</summary>
public sealed class InventoryContents {
	/// <summary>Every copy held, one entry per asset (a stack is one asset with an "amount").</summary>
	public List<JsonElement> Assets { get; } = [];

	/// <summary>What each kind of item is, keyed the way an asset refers to it: classid_instanceid.</summary>
	public Dictionary<string, JsonElement> Descriptions { get; } = new(StringComparer.Ordinal);

	/// <summary>False when a later page failed - what is here is real, just not everything.</summary>
	public bool Complete { get; set; }

	/// <summary>classid plus instanceid. Neither alone is unique, and the pair is what ties an asset to its description.</summary>
	public static string KeyOf(JsonElement e) => $"{Text(e, "classid")}_{Text(e, "instanceid")}";

	public JsonElement? DescriptionOf(JsonElement asset) => Descriptions.TryGetValue(KeyOf(asset), out JsonElement d) ? d : null;

	public static string Text(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
}

/// <summary>Reading a Steam inventory, all of it.</summary>
/// <remarks>
/// Steam serves an inventory in pages: ask for up to N items and it says whether there are more, and which asset to
/// start from next. Every reader here took the first page and stopped. Past 2,000 items in one game, the rest
/// silently vanished - from the inventory value and from loot sweeps alike - and the booster-pack opener asked for
/// only 200, so on an account holding 914 Steam items any pack past the two-hundredth was never seen at all.
/// </remarks>
public static class Inventory {
	/// <summary>Steam's ceiling for one page of an account's own inventory.</summary>
	private const int PageSize = 2000;

	/// <summary>A hundred thousand items. Past that, something is wrong and paging on forever is not the answer.</summary>
	private const int MaxPages = 50;

	public static Task<InventoryContents?> ReadAsync(Bot bot, uint app, string context, CancellationToken ct) =>
		ReadAsync((uri, c) => bot.Web.GetAsync(uri, c), bot.SteamId, app, context, ct, source: bot.Name);

	/// <param name="get">How to fetch one page. Passed in so the paging can be exercised against a public inventory
	/// without an account.</param>
	/// <param name="pageSize">Only ever changed by a test, to force paging on an inventory smaller than a page.</param>
	/// <param name="source">The account, for the log.</param>
	/// <returns>Null when even the first page couldn't be read.</returns>
	public static async Task<InventoryContents?> ReadAsync(Func<Uri, CancellationToken, Task<string?>> get, ulong steamId, uint app,
		string context, CancellationToken ct, int pageSize = PageSize, string source = "nocat.farm") {
		InventoryContents found = new();
		string? start = null;

		for (int page = 0; page < MaxPages; page++) {
			if (page > 0) {
				// Steam rate-limits inventory reads; a browser paging through one doesn't fire requests back to back.
				await Task.Delay(Rng.Seconds(2, 4), ct).ConfigureAwait(false);
			}

			string path = $"/inventory/{steamId}/{app}/{context}?l=english&count={pageSize}" + (start == null ? "" : $"&start_assetid={start}");
			string? body = await get(new Uri(WebSession.Community, path), ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(body)) {
				if (page > 0) {
					Log.Debug($"inventory {app}/{context}: page {page + 1} didn't come back - keeping {found.Assets.Count} item(s) as incomplete", source);
				}

				return page == 0 ? null : found;   // WebSession already wrote down why
			}

			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			// A refusal still parses as JSON - {"success":false} or plain null - and read as "no assets, no more
			// pages" it became a complete, EMPTY inventory: every card judged as not held.
			if ((root.ValueKind != JsonValueKind.Object)
				|| (root.TryGetProperty("success", out JsonElement ok) && (ok.ValueKind is JsonValueKind.False || ((ok.ValueKind == JsonValueKind.Number) && (ok.GetInt32() != 1))))) {
				Log.Debug($"inventory {app}/{context}: Steam refused page {page + 1}: {Log.Scrub(body[..Math.Min(150, body.Length)])}", source);

				return page == 0 ? null : found;
			}

			if (root.TryGetProperty("assets", out JsonElement assets)) {
				foreach (JsonElement a in assets.EnumerateArray()) {
					found.Assets.Add(a.Clone());
				}
			}

			if (root.TryGetProperty("descriptions", out JsonElement descriptions)) {
				foreach (JsonElement d in descriptions.EnumerateArray()) {
					found.Descriptions.TryAdd(InventoryContents.KeyOf(d), d.Clone());
				}
			}

			bool more = root.TryGetProperty("more_items", out JsonElement m)
				&& ((m.ValueKind == JsonValueKind.True) || ((m.ValueKind == JsonValueKind.Number) && (m.GetInt32() == 1)));
			string last = InventoryContents.Text(root, "last_assetid");
			start = last.Length > 0 ? last : null;

			if (!more || string.IsNullOrEmpty(start)) {
				found.Complete = true;

				return found;
			}
		}

		Log.Debug($"inventory {app}/{context}: stopped after {MaxPages} pages ({found.Assets.Count} item(s)) - kept as incomplete", source);

		return found;
	}
}
