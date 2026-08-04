using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
public static partial class Looting {
	/// <summary>Steam's own context ids inside app 753 (Steam itself).</summary>
	private const uint SteamAppId = 753;
	private const uint CommunityContext = 6;

	public readonly record struct Item(ulong AssetId, ulong ClassId, ulong InstanceId, uint Amount, string Type, string Name, uint App, uint Context);

	/// <summary>
	/// This account's tradable Steam community items: cards, backgrounds, emoticons, boosters.
	///
	/// Steam splits the answer in two - assets are the things you hold, descriptions are what they are - and only
	/// the descriptions know whether an item may be traded at all. Sending an untradable item does not fail
	/// politely; the whole offer is rejected, so they are filtered out here rather than discovered later.
	/// </summary>
	/// <summary>
	/// Everything tradable the account holds, across every game - not just Steam's own cards and backgrounds.
	///
	/// It used to read app 753 context 6 and nothing else, which is where cards, backgrounds, emoticons, boosters
	/// and gems live. That is the right answer for "loot the card farmer" and the wrong one for "send me
	/// everything": an account with two hundred game items reported "nothing tradable to send". The inventory
	/// page lists which games hold items, so the list comes from Steam rather than from a guess.
	/// </summary>
	public static async Task<List<Item>> InventoryAsync(Bot bot, CancellationToken ct = default) {
		List<Item> items = [];

		if (!bot.Web.Ready || (bot.SteamId == 0)) {
			return items;
		}

		foreach ((uint app, uint context) in await InventoriesAsync(bot, ct).ConfigureAwait(false)) {
			items.AddRange(await OneInventoryAsync(bot, app, context, ct).ConfigureAwait(false));
		}

		return items;
	}

	/// <summary>Which (game, context) pairs actually hold something. Steam's own inventory is always included.</summary>
	private static async Task<List<(uint App, uint Context)>> InventoriesAsync(Bot bot, CancellationToken ct) {
		List<(uint, uint)> found = [(SteamAppId, CommunityContext)];

		try {
			string? page = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/inventory/"), ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(page)) {
				return found;
			}

			Match blob = Regex.Match(page, @"g_rgAppContextData\s*=\s*(\{.*?\})\s*;", RegexOptions.Singleline);

			if (!blob.Success) {
				return found;
			}

			using JsonDocument doc = JsonDocument.Parse(blob.Groups[1].Value);

			foreach (JsonProperty app in doc.RootElement.EnumerateObject()) {
				if (!uint.TryParse(app.Name, out uint appId) || !app.Value.TryGetProperty("rgContexts", out JsonElement contexts)) {
					continue;
				}

				foreach (JsonProperty context in contexts.EnumerateObject()) {
					bool holds = context.Value.TryGetProperty("asset_count", out JsonElement a) && a.TryGetInt32(out int count) && (count > 0);

					if (holds && uint.TryParse(context.Name, out uint contextId) && !found.Contains((appId, contextId))) {
						found.Add((appId, contextId));
					}
				}
			}
		} catch (Exception e) {
			Log.Debug($"couldn't list the inventories: {e.Message}", bot.Name);
		}

		return found;
	}

	private static async Task<List<Item>> OneInventoryAsync(Bot bot, uint app, uint context, CancellationToken ct) {
		List<Item> items = [];

		string? body = await bot.Web.GetAsync(
			new Uri(WebSession.Community, $"/inventory/{bot.SteamId}/{app}/{context}?l=english&count=2000"), ct).ConfigureAwait(false);

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

				items.Add(new Item(assetId, classId, instanceId, Math.Max(1, amount), info.Type, info.Name, app, context));
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

		// The trade-link token is only needed between accounts that are not Steam friends - and when the
		// recipient is one of YOUR OWN accounts we are signed into it too, so asking the user to go and paste a
		// token out of a URL is asking for something we can simply read. An explicit setting still wins.
		string token = cfg.TradeMasterToken.Trim();

		if (token.Length == 0) {
			token = await TradeTokenOfAsync(master, ct).ConfigureAwait(false) ?? "";

			if (token.Length > 0) {
				Log.Debug($"using {master}'s own trade token - it is one of your accounts", bot.Name);
			}
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
			(bool ok, string message) = await SendOfferAsync(bot, master, batch, token, ct).ConfigureAwait(false);

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

	/// <summary>
	/// The trade-link token belonging to one of our OWN accounts, read from its own session.
	///
	/// Steam shows it on the account's trade-offer privacy page as part of the full trade URL. Only works for an
	/// account this app is signed into - which is exactly the case that matters, because sweeping items between
	/// your own accounts is what the token requirement gets in the way of.
	/// </summary>
	private static async Task<string?> TradeTokenOfAsync(ulong steamId, CancellationToken ct) {
		Bot? owner = BotManager.Instance?.All.FirstOrDefault(b => b.SteamId == steamId);

		if ((owner == null) || !owner.IsOnline || !owner.Web.Ready) {
			return null;   // not one of ours, or not signed in - nothing we can read
		}

		try {
			string? page = await owner.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{steamId}/tradeoffers/privacy"), ct).ConfigureAwait(false);

			if (page == null) {
				return null;
			}

			Match hit = TradeTokenPattern().Match(page);

			return hit.Success ? hit.Groups[1].Value : null;
		} catch (Exception e) {
			Log.Debug($"couldn't read the trade token for {steamId}: {e.Message}", owner.Name);

			return null;
		}
	}

	// Anchored on the trade URL itself. A bare "token=" appears elsewhere on Steam's pages, and grabbing the
	// wrong one would send a perfectly well-formed offer that Steam then refuses for a reason nobody could see.
	[GeneratedRegex(@"tradeoffer/new/\?partner=\d+&(?:amp;)?token=([A-Za-z0-9_-]{6,})", RegexOptions.CultureInvariant)]
	private static partial Regex TradeTokenPattern();

	/// <summary>
	/// Steam's trade errors are a sentence and a number, and the number is the useful half.
	///
	/// "There was an error sending your trade offer. Please try again later. (15)" is not a transient fault to be
	/// retried, whatever it says - 15 is access denied, and on this endpoint that almost always means the sending
	/// account has never enabled the mobile authenticator, which Steam requires before an account may send a trade
	/// offer at all. Passing that through unexplained had people waiting for a problem that never resolves.
	/// </summary>
	private static string Explain(string steamError) {
		string extra = steamError switch {
			_ when steamError.Contains("(15)", StringComparison.Ordinal) =>
				"  -  that's Steam's \"access denied\". Usually it means the SENDING account has no Steam Guard Mobile Authenticator: Steam won't let an account send trade offers without one. A trade ban or trade hold on either account does the same thing.",
			_ when steamError.Contains("(11)", StringComparison.Ordinal) =>
				"  -  Steam won't let this offer through to that account. Between two accounts that are not Steam friends it needs the recipient's trade-link token, which is looked up automatically when the recipient is one of your own accounts - so this usually means the RECIPIENT has trading switched off, is trade banned, or has never set up the mobile authenticator.",
			_ when steamError.Contains("(16)", StringComparison.Ordinal) => "  -  Steam timed out. Worth trying again.",
			_ when steamError.Contains("(26)", StringComparison.Ordinal) =>
				"  -  one of the items is no longer there. The inventory has changed since it was read; try again.",
			_ when steamError.Contains("(20)", StringComparison.Ordinal) => "  -  Steam's trading service is down for the moment.",
			_ when steamError.Contains("(25)", StringComparison.Ordinal) =>
				"  -  too many offers already open between these accounts, or a Steam limit has been hit.",
			_ when steamError.Contains("(2)", StringComparison.Ordinal) =>
				"  -  Steam gave a generic failure. Check neither account is limited, trade banned, or newly password-changed.",
			_ => ""
		};

		return steamError + extra;
	}

	/// <summary>
	/// A two-way offer: these items for those. Used by the card matcher, where a one-sided offer would be a gift.
	///
	/// Steam wants both halves in the same message, so this is the same endpoint as a plain send with the "them"
	/// side filled in as well.
	/// </summary>
	public static async Task<(bool Ok, string Message)> SwapAsync(Bot bot, Bot partner, IReadOnlyCollection<Item> giving, IReadOnlyCollection<Item> taking, CancellationToken ct = default) {
		if ((giving.Count == 0) || (taking.Count == 0)) {
			return (false, "a swap needs items on both sides");
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return (false, $"{bot.Name} isn't logged in");
		}

		return await SendOfferAsync(bot, partner.SteamId, giving, bot.Cfg.TradeMasterToken, ct, taking).ConfigureAwait(false);
	}

	private static async Task<(bool Ok, string Message)> SendOfferAsync(Bot bot, ulong master, IReadOnlyCollection<Item> items, string accessToken, CancellationToken ct, IReadOnlyCollection<Item>? wanted = null) {
		StringBuilder assets = new();

		foreach (Item item in items) {
			if (assets.Length > 0) {
				assets.Append(',');
			}

			assets.Append(CultureInfo.InvariantCulture,
				$"{{\"appid\":{item.App},\"contextid\":\"{item.Context}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}

		StringBuilder theirs = new();

		foreach (Item item in wanted ?? []) {
			if (theirs.Length > 0) {
				theirs.Append(',');
			}

			theirs.Append(CultureInfo.InvariantCulture,
				$"{{\"appid\":{item.App},\"contextid\":\"{item.Context}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}

		string offer = "{\"newversion\":true,\"version\":2,"
			+ "\"me\":{\"assets\":[" + assets + "],\"currency\":[],\"ready\":false},"
			+ "\"them\":{\"assets\":[" + theirs + "],\"currency\":[],\"ready\":false}}";

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
		// Keeps the body on a failure: a refusal is a 500 whose body carries Steam's own explanation.
		string? body = await bot.Web.PostAllowingFailureAsync(new Uri(WebSession.Community, "/tradeoffer/new/send"), form, referer, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(body)) {
			return (false, "Steam didn't answer at all - check the connection and try again.");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			if (doc.RootElement.TryGetProperty("strError", out JsonElement error)) {
				return (false, Explain(error.GetString() ?? "refused"));
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
