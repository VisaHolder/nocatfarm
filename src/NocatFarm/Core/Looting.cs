using System.Globalization;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Sending this account's items somewhere else - in practice, sweeping the cards off six idlers onto the one
/// account you actually sell from.
///
/// This is the one thing in here that can genuinely lose you something, so it is deliberately narrow: it will
/// only ever send to the account named in the trade-master settings, it only sends what Steam marks tradable,
/// and it will not touch anything outside the item types you asked for. There is no "send everything" path and
/// no way to point it at an arbitrary SteamID from a command.
/// </summary>
public static class Looting {
	/// <summary>Steam's own context ids inside app 753 (Steam itself).</summary>
	private const uint SteamAppId = 753;
	private const uint CommunityContext = 6;

	public readonly record struct Item(ulong AssetId, ulong ClassId, ulong InstanceId, uint Amount, string Type, string Name);

	/// <summary>
	/// This account's tradable Steam community items: cards, backgrounds, emoticons, boosters.
	///
	/// Steam splits the answer in two - assets are the things you hold, descriptions are what they are - and only
	/// the descriptions know whether an item may be traded at all. Sending an untradable item does not fail
	/// politely; the whole offer is rejected, so they are filtered out here rather than discovered later.
	/// </summary>
	public static async Task<List<Item>> InventoryAsync(Bot bot, CancellationToken ct = default) {
		List<Item> items = [];

		if (!bot.Web.Ready || (bot.SteamId == 0)) {
			return items;
		}

		string? body = await bot.Web.GetAsync(
			new Uri(WebSession.Community, $"/inventory/{bot.SteamId}/{SteamAppId}/{CommunityContext}?l=english&count=2000"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(body)) {
			return items;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets) || !doc.RootElement.TryGetProperty("descriptions", out JsonElement descriptions)) {
				return items;   // an empty or private inventory answers without these
			}

			// class+instance is what ties an asset to its description; neither alone is unique.
			Dictionary<string, (bool Tradable, string Type, string Name)> byClass = [];

			foreach (JsonElement description in descriptions.EnumerateArray()) {
				string key = Text(description, "classid") + "_" + Text(description, "instanceid");
				bool tradable = description.TryGetProperty("tradable", out JsonElement t) && (t.ValueKind == JsonValueKind.Number ? t.GetInt32() == 1 : t.ValueKind == JsonValueKind.True);
				byClass[key] = (tradable, Text(description, "type"), Text(description, "name"));
			}

			foreach (JsonElement asset in assets.EnumerateArray()) {
				string key = Text(asset, "classid") + "_" + Text(asset, "instanceid");

				if (!byClass.TryGetValue(key, out (bool Tradable, string Type, string Name) info) || !info.Tradable) {
					continue;
				}

				if (!ulong.TryParse(Text(asset, "assetid"), out ulong assetId) || (assetId == 0)) {
					continue;
				}

				ulong.TryParse(Text(asset, "classid"), out ulong classId);
				ulong.TryParse(Text(asset, "instanceid"), out ulong instanceId);
				uint.TryParse(Text(asset, "amount"), out uint amount);

				items.Add(new Item(assetId, classId, instanceId, Math.Max(1, amount), info.Type, info.Name));
			}
		} catch (Exception e) {
			Log.Warn($"couldn't read the inventory: {e.Message}", bot.Name);
		}

		return items;
	}

	private static string Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value)
			? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
			: "";

	/// <summary>True for the item types this account was told it may send.</summary>
	public static bool WantedType(string type, string wanted) {
		if (string.IsNullOrWhiteSpace(wanted)) {
			return type.Contains("Trading Card", StringComparison.OrdinalIgnoreCase);
		}

		foreach (string token in wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			bool match = token.ToLowerInvariant() switch {
				"cards" or "card" => type.Contains("Trading Card", StringComparison.OrdinalIgnoreCase),
				"backgrounds" or "background" => type.Contains("Profile Background", StringComparison.OrdinalIgnoreCase),
				"emoticons" or "emoticon" => type.Contains("Emoticon", StringComparison.OrdinalIgnoreCase),
				"boosters" or "booster" => type.Contains("Booster Pack", StringComparison.OrdinalIgnoreCase),
				"gems" or "gem" => type.Contains("Gems", StringComparison.OrdinalIgnoreCase) || type.Contains("Sack of Gems", StringComparison.OrdinalIgnoreCase),
				"foil" or "foils" => type.Contains("Foil", StringComparison.OrdinalIgnoreCase),
				"all" or "everything" => true,
				_ => false
			};

			if (match) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Send this account's wanted items to the first trade master.
	///
	/// Returns a line to show the user either way. It refuses rather than guesses: no master, no send.
	/// </summary>
	public static async Task<string> SendToMasterAsync(Bot bot, CancellationToken ct = default) {
		BotConfig cfg = bot.Cfg;
		ulong master = Modules.Social.ParseIds(cfg.TradeMasters).FirstOrDefault();

		if (master == 0) {
			return $"{bot.Name}: nowhere to send to - set \"Your own accounts\" under Trades first";
		}

		if (master == bot.SteamId) {
			return $"{bot.Name}: it is the trade master, so there is nothing to send anywhere";
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return $"{bot.Name}: not logged in";
		}

		List<Item> all = await InventoryAsync(bot, ct).ConfigureAwait(false);
		List<Item> sending = all.Where(i => WantedType(i.Type, cfg.SendItemTypes)).ToList();

		if (sending.Count == 0) {
			return $"{bot.Name}: nothing tradable to send" + (all.Count > 0 ? $" ({all.Count} tradable item(s), none of the types you asked for)" : "");
		}

		// Steam rejects an offer with too many items in it outright, so send it in batches.
		const int BatchSize = 100;
		int sent = 0;
		List<string> problems = [];

		foreach (Item[] batch in sending.Chunk(BatchSize)) {
			(bool ok, string message) = await SendOfferAsync(bot, master, batch, cfg.TradeMasterToken, ct).ConfigureAwait(false);

			if (ok) {
				sent += batch.Length;
			} else {
				problems.Add(message);

				break;   // if one batch was refused the next will be too
			}

			await Task.Delay(Rng.Seconds(5, 15), ct).ConfigureAwait(false);
		}

		if (sent == 0) {
			return $"{bot.Name}: couldn't send - {(problems.Count > 0 ? problems[0] : "Steam refused the offer")}";
		}

		string note = $"{bot.Name}: sent {sent} item(s) to {master}";

		return problems.Count > 0 ? note + $" (then stopped: {problems[0]})" : note;
	}

	private static async Task<(bool Ok, string Message)> SendOfferAsync(Bot bot, ulong master, IReadOnlyCollection<Item> items, string accessToken, CancellationToken ct) {
		StringBuilder assets = new();

		foreach (Item item in items) {
			if (assets.Length > 0) {
				assets.Append(',');
			}

			assets.Append(CultureInfo.InvariantCulture,
				$"{{\"appid\":{SteamAppId},\"contextid\":\"{CommunityContext}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}

		string offer = "{\"newversion\":true,\"version\":2,"
			+ "\"me\":{\"assets\":[" + assets + "],\"currency\":[],\"ready\":false},"
			+ "\"them\":{\"assets\":[],\"currency\":[],\"ready\":false}}";

		// The account id (the low 32 bits) is what the trade URL wants, not the full SteamID64.
		uint partnerAccountId = (uint) (master & 0xFFFFFFFF);
		string token = accessToken.Trim();

		Dictionary<string, string> form = new() {
			["sessionid"] = bot.Web.SessionId,
			["serverid"] = "1",
			["partner"] = master.ToString(CultureInfo.InvariantCulture),
			["tradeoffermessage"] = "",
			["json_tradeoffer"] = offer,
			["captcha"] = "",
			["trade_offer_create_params"] = token.Length > 0 ? "{\"trade_offer_access_token\":\"" + token + "\"}" : "{}"
		};

		Uri referer = new(WebSession.Community, $"/tradeoffer/new/?partner={partnerAccountId}" + (token.Length > 0 ? "&token=" + Uri.EscapeDataString(token) : ""));
		string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, "/tradeoffer/new/send"), form, referer, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(body)) {
			return (false, "Steam didn't answer");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			if (doc.RootElement.TryGetProperty("strError", out JsonElement error)) {
				return (false, error.GetString() ?? "refused");
			}

			if (doc.RootElement.TryGetProperty("tradeofferid", out JsonElement id)) {
				bool needsConfirming = doc.RootElement.TryGetProperty("needs_mobile_confirmation", out JsonElement confirm)
					&& (confirm.ValueKind == JsonValueKind.True || (confirm.ValueKind == JsonValueKind.Number && confirm.GetInt32() == 1));

				if (needsConfirming && ulong.TryParse(id.GetString() ?? id.ToString(), out ulong offerId)) {
					if (!await bot.ConfirmMobileAsync(offerId, true, ct).ConfigureAwait(false)) {
						Log.Warn($"the offer went out but needs confirming on your phone - add this account's authenticator secrets to do that here", bot.Name);
					}
				}

				return (true, "sent");
			}
		} catch (Exception e) {
			return (false, e.Message);
		}

		// Steam answers a rejected offer as an HTML page, not JSON. Anything that isn't a trade id is a refusal.
		return (false, "Steam refused the offer - the other account may not be a friend, or its trade link may need a token");
	}
}
