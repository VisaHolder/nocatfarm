using System.Globalization;
using System.Text.Json;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// Buying cards' market, for real: over the account's own web session, so every price is in its wallet's currency. See
/// <see cref="CardMarket"/> for how Steam's market answers.
/// </summary>
internal sealed class SteamCardMarket(Bot bot) : ICardMarket {
	/// <summary>One card's price read at a time across every account, a little apart - the market minds being rushed.</summary>
	private static readonly SemaphoreSlim Pace = new(1, 1);

	private static DateTime _lastRead = DateTime.MinValue;

	public async Task<CardMarket.Wallet?> WalletAsync(CancellationToken ct) {
		string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, "/market/userbillinginfo"), ct).ConfigureAwait(false);

		if (CardMarket.ParseWallet(json) is { } wallet) {
			return wallet;
		}

		// The page didn't answer: what Steam last pushed, with nothing known about confirmations.
		return (bot.WalletCents is { } cents) && (bot.WalletCurrency != SteamKit2.ECurrencyCode.Invalid)
			? new CardMarket.Wallet(cents, (int) bot.WalletCurrency, false, -1)
			: null;
	}

	public async Task<IReadOnlyDictionary<uint, int>?> BadgeLevelsAsync(CancellationToken ct) =>
		await LevelPlanner.BadgesAsync(bot, ct).ConfigureAwait(false) is { } badges ? badges.GameLevels : null;

	public async Task<IReadOnlyDictionary<uint, Dictionary<string, int>>?> CardsAsync(CancellationToken ct) =>
		await Inventory.ReadAsync(bot, CardMarket.SteamItems, "6", ct).ConfigureAwait(false) is { Complete: true } inventory ? LevelPlanner.NormalCards(inventory) : null;

	public async Task<CardSet?> SetAsync(uint app, CancellationToken ct) {
		(LevelPlanner.SetPrice? set, _) = await LevelPlanner.PriceAsync(bot, app, ct).ConfigureAwait(false);

		if (set == null) {
			return null;
		}

		// Size 0: the search didn't hand back the whole set - nothing is planned on half of one.
		return set.Size == 0
			? new CardSet(app, set.Game, [], new Dictionary<string, int>())
			: new CardSet(app, set.Game, [.. set.Cards.Keys.Order(StringComparer.Ordinal)], set.Cards);
	}

	public async Task<IReadOnlyList<uint>?> CheapestGamesAsync(CancellationToken ct) => await LevelPlanner.CheapestGamesAsync(bot, ct).ConfigureAwait(false);

	public async Task<CardMarket.Book?> BookAsync(string hash, CancellationToken ct) {
		await Pace.WaitAsync(ct).ConfigureAwait(false);

		try {
			TimeSpan since = DateTime.UtcNow - _lastRead;
			TimeSpan gap = Rng.Seconds(1, 2);

			if (since < gap) {
				await Task.Delay(gap - since, ct).ConfigureAwait(false);
			}

			string qp = JsonSerializer.Serialize(new object[] { CardMarket.SteamItems, hash });
			string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/market/orderbook?q=Load&qp={Uri.EscapeDataString(qp)}"), ct).ConfigureAwait(false);

			return CardMarket.ParseBook(json);
		} finally {
			_lastRead = DateTime.UtcNow;
			Pace.Release();
		}
	}

	/// <summary>
	/// One card: a buy order for one at <paramref name="price"/>, which the cheapest listing fills at once. Asked what it
	/// bought a few times; still open after that, it's taken back - an order is never left waiting on the market.
	/// </summary>
	public async Task<BuyAnswer> BuyAsync(uint app, string hash, long price, int currency, CancellationToken ct) {
		Uri page = new(WebSession.Community, $"/market/listings/{CardMarket.SteamItems}/{Uri.EscapeDataString(hash)}");
		string confirmation = "0";
		string? orderId = null;

		for (int attempt = 0; (attempt < 3) && (orderId == null); attempt++) {
			Dictionary<string, string> form = new(StringComparer.Ordinal) {
				["currency"] = currency.ToString(CultureInfo.InvariantCulture),
				["appid"] = CardMarket.SteamItems.ToString(CultureInfo.InvariantCulture),
				["market_hash_name"] = hash,
				["price_total"] = price.ToString(CultureInfo.InvariantCulture),
				["tradefee_tax"] = "0",
				["quantity"] = "1",
				["first_name"] = "",
				["last_name"] = "",
				["billing_address"] = "",
				["billing_address_two"] = "",
				["billing_country"] = "",
				["billing_city"] = "",
				["billing_state"] = "",
				["billing_postal_code"] = "",
				["save_my_address"] = "0",
				["confirmation"] = confirmation
			};

			string? body = await bot.Web.PostAllowingFailureAsync(new Uri(WebSession.Community, "/market/createbuyorder/"), form, page, ct).ConfigureAwait(false);

			if (body == null) {
				// It may have reached Steam: an order left open is taken back, and the card counts as spent.
				await CancelOpenAsync(hash, ct).ConfigureAwait(false);

				return new BuyAnswer(BuyResult.NoAnswer, price, "no answer");
			}

			(string? id, string? confirm, string message) = ReadOrder(body);

			if (id != null) {
				orderId = id;

				break;
			}

			if ((confirm == null) || (confirmation != "0")) {
				return new BuyAnswer(BuyResult.Refused, 0, message.Length > 0 ? message : confirm != null ? "the purchase wasn't confirmed" : "");
			}

			// Steam wants the purchase confirmed: with its authenticator here, exactly this confirmation is accepted - the one
			// Steam just named for this purchase, never a trade or a listing - and the order sent again with it.
			if (!await ConfirmAsync(confirm, ct).ConfigureAwait(false)) {
				return new BuyAnswer(BuyResult.Refused, 0, "Steam wants the purchase confirmed, and it couldn't be");
			}

			confirmation = confirm;
		}

		if (orderId == null) {
			return new BuyAnswer(BuyResult.Refused, 0, "Steam didn't place the order");
		}

		for (int look = 0; look < 4; look++) {
			await Task.Delay(Rng.Seconds(2, 3), ct).ConfigureAwait(false);

			if (await StatusAsync(orderId, ct).ConfigureAwait(false) is { Bought: > 0 } status) {
				return new BuyAnswer(BuyResult.Bought, status.Paid > 0 ? status.Paid : price);
			}
		}

		await bot.Web.PostAllowingFailureAsync(new Uri(WebSession.Community, "/market/cancelbuyorder/"),
			new Dictionary<string, string>(StringComparer.Ordinal) { ["buy_orderid"] = orderId }, page, ct).ConfigureAwait(false);

		// Filled between the last look and taking it back: bought after all.
		return await StatusAsync(orderId, ct).ConfigureAwait(false) is { Bought: > 0 } late
			? new BuyAnswer(BuyResult.Bought, late.Paid > 0 ? late.Paid : price)
			: new BuyAnswer(BuyResult.Gone, 0);
	}

	/// <summary>A createbuyorder answer: the order's id, or the confirmation Steam wants, and its message.</summary>
	internal static (string? Id, string? Confirm, string Message) ReadOrder(string body) {
		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return (null, null, "");
			}

			string message = root.TryGetProperty("message", out JsonElement m) && (m.ValueKind == JsonValueKind.String) ? Log.Scrub(m.GetString() ?? "") : "";
			bool success = root.TryGetProperty("success", out JsonElement s) && (s.ValueKind == JsonValueKind.Number) && s.TryGetInt32(out int ok) && (ok == 1);

			if (success && root.TryGetProperty("buy_orderid", out JsonElement id) && (Text(id) is { Length: > 0 } order) && (order != "0")) {
				return (order, null, message);
			}

			if (root.TryGetProperty("need_confirmation", out JsonElement need) && (need.ValueKind == JsonValueKind.True)
				&& root.TryGetProperty("confirmation", out JsonElement c) && c.TryGetProperty("confirmation_id", out JsonElement cid) && (Text(cid) is { Length: > 0 } confirm)) {
				return (null, confirm, message);
			}

			return (null, null, message);
		} catch (JsonException) {
			return (null, null, "");
		}
	}

	/// <summary>What a buy order has bought so far, and what it paid. Null when Steam didn't say.</summary>
	private async Task<(int Bought, long Paid)?> StatusAsync(string orderId, CancellationToken ct) {
		string? json = await bot.Web.GetAsync(new Uri(WebSession.Community,
			$"/market/getbuyorderstatus/?sessionid={Uri.EscapeDataString(bot.Web.SessionId)}&buy_orderid={Uri.EscapeDataString(orderId)}"), ct).ConfigureAwait(false);

		return ReadStatus(json);
	}

	/// <summary>A getbuyorderstatus answer: {"success":1,"active":0,"purchased":1,"quantity":"1","purchases":[{"price_total":7,...}]}.</summary>
	internal static (int Bought, long Paid)? ReadStatus(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("success", out JsonElement s) || (Number(s) != 1)) {
				return null;
			}

			int bought = root.TryGetProperty("purchased", out JsonElement p) ? (int) Math.Clamp(Number(p), 0, int.MaxValue) : 0;
			long paid = 0;

			if (root.TryGetProperty("purchases", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement one in list.EnumerateArray()) {
					paid += one.TryGetProperty("price_total", out JsonElement total) ? Number(total)
						: (one.TryGetProperty("price_subtotal", out JsonElement sub) ? Number(sub) : 0) + (one.TryGetProperty("price_fee", out JsonElement fee) ? Number(fee) : 0);
				}
			}

			return (bought, Math.Max(0, paid));
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Accept exactly the confirmation Steam named for this purchase, with the account's authenticator. False when it can't.</summary>
	private async Task<bool> ConfirmAsync(string confirmationId, CancellationToken ct) {
		if (!bot.CanConfirmTrades || !ulong.TryParse(confirmationId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) {
			return false;
		}

		for (int attempt = 0; attempt < 3; attempt++) {
			await Task.Delay(Rng.Seconds(2, 4), ct).ConfigureAwait(false);
			(bool ok, _, List<Confirmations.Item> items) = await Confirmations.ListAsync(bot, ct, fresh: true).ConfigureAwait(false);

			if (ok && (items.FirstOrDefault(i => ((i.Id == id) || (i.CreatorId == id)) && (i.Type is not (Confirmations.Trade or Confirmations.MarketListing))) is { } match)) {
				return await Confirmations.ActAsync(bot, [match], accept: true, ct).ConfigureAwait(false);
			}
		}

		return false;
	}

	/// <summary>After a buy order went unanswered: an order for this card still open is taken back, so nothing waits on the market.</summary>
	private async Task CancelOpenAsync(string hash, CancellationToken ct) {
		try {
			string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, "/market/mylistings/render/?start=0&count=10&norender=1"), ct).ConfigureAwait(false);

			foreach (string order in OpenOrders(json, hash)) {
				await bot.Web.PostAllowingFailureAsync(new Uri(WebSession.Community, "/market/cancelbuyorder/"),
					new Dictionary<string, string>(StringComparer.Ordinal) { ["buy_orderid"] = order }, null, ct).ConfigureAwait(false);
			}
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't look for an open buy order: {0}", Log.Describe(e)), bot.Name);
		}
	}

	/// <summary>The account's open buy orders for one card, from /market/mylistings/render/?norender=1 ("buy_orders").</summary>
	internal static List<string> OpenOrders(string? json, string hash) {
		List<string> orders = [];

		if (string.IsNullOrWhiteSpace(json)) {
			return orders;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("buy_orders", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement o in list.EnumerateArray()) {
					if (o.TryGetProperty("hash_name", out JsonElement h) && (h.GetString() == hash) && o.TryGetProperty("buy_orderid", out JsonElement id) && (Text(id) is { Length: > 0 } order)) {
						orders.Add(order);
					}
				}
			}
		} catch (JsonException) {
			// nothing readable - nothing to take back
		}

		return orders;
	}

	private static string? Text(JsonElement e) => e.ValueKind switch {
		JsonValueKind.String => e.GetString(),
		JsonValueKind.Number => e.GetRawText(),
		_ => null
	};

	private static long Number(JsonElement e) => e.ValueKind switch {
		JsonValueKind.Number when e.TryGetInt64(out long n) => n,
		JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n,
		_ => 0
	};
}
