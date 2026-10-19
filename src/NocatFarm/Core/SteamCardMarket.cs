using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// Buying cards' market, for real: over the account's own web session, so every price is in its wallet's currency. See
/// <see cref="CardMarket"/> for how Steam's market answers.
/// </summary>
/// <remarks>
/// Money first: an answer that doesn't say plainly what happened is never taken for "nothing bought". Three outcomes:
/// <list type="bullet">
/// <item>nothing happened - only Steam's own readable refusal with no order in it, or a request that never left this PC;</item>
/// <item>gone - only when the order's status says it's closed with nothing bought (active 0, purchased 0);</item>
/// <item>unsure - everything else: counted as spent at the order's price, the order taken back, and asked about again
/// at the next look.</item>
/// </list>
/// </remarks>
public sealed class SteamCardMarket(Bot bot, IMarketWeb? web = null) : ICardMarket {
	/// <summary>One card's price read at a time across every account, a little apart - the market minds being rushed.</summary>
	private static readonly SemaphoreSlim Pace = new(1, 1);

	private static DateTime _lastRead = DateTime.MinValue;

	/// <summary>Steam's confirmation type for a market purchase (SteamKit's EMobileConfirmationType.MarketPurchase).</summary>
	internal const int MarketPurchase = 12;

	/// <summary>
	/// Once an order is sent, how long finding out what it did - and taking it back - may take. On a clock of its own, not
	/// the module's: a stop, a disconnect or an update in the middle still finds out, and still takes it back.
	/// </summary>
	internal static readonly TimeSpan AfterSending = TimeSpan.FromMinutes(3);

	private readonly IMarketWeb _web = web ?? new BotMarketWeb(bot);

	public async Task<CardMarket.Wallet?> WalletAsync(CancellationToken ct) {
		string? json = await _web.GetAsync(new Uri(WebSession.Community, "/market/userbillinginfo"), ct).ConfigureAwait(false);

		if (CardMarket.ParseWallet(json) is { } wallet) {
			return wallet;
		}

		// The page didn't answer: what Steam last pushed, with nothing known about confirmations - which buys nothing.
		return (bot.WalletCents is { } cents) && (bot.WalletCurrency != SteamKit2.ECurrencyCode.Invalid)
			? new CardMarket.Wallet(cents, (int) bot.WalletCurrency, false, -1)
			: null;
	}

	/// <summary>
	/// 1.8.2: for an estimate ('levelup'), the badges and the inventory as read this long ago will do (0: read them now) - so
	/// it answers at once. Never for buying: a look reads them afresh.
	/// </summary>
	public TimeSpan ReuseFor { get; set; }

	private (LevelPlanner.BadgeState Badges, DateTime At)? _badges;

	public async Task<IReadOnlyDictionary<uint, int>?> BadgeLevelsAsync(CancellationToken ct) {
		LevelPlanner.BadgeState? badges = (ReuseFor > TimeSpan.Zero) && (_badges is { } kept) && (DateTime.UtcNow - kept.At < ReuseFor) ? kept.Badges : null;

		if (badges == null) {
			if (await LevelPlanner.BadgesAsync(bot, ct).ConfigureAwait(false) is not { } read) {
				return null;
			}

			badges = read;
			_badges = (read, DateTime.UtcNow);
		}

		_foils = badges.FoilLevels;
		SteamLevel = (badges.Level, badges.Xp);

		return badges.GameLevels;
	}

	/// <summary>1.8.2: each foil badge's level, from the badges the plan just read - never asked again for this.</summary>
	public Task<IReadOnlyDictionary<uint, int>?> FoilLevelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<uint, int>?>(_foils);

	private Dictionary<uint, int>? _foils;

	/// <summary>1.8.2: the Steam level and XP the last badge read said.</summary>
	public (int Level, int Xp)? SteamLevel { get; private set; }

	public async Task<IReadOnlyDictionary<uint, Dictionary<string, int>>?> CardsAsync(CancellationToken ct) =>
		((ReuseFor > TimeSpan.Zero) && (_inventory is { } kept) && (DateTime.UtcNow - _inventoryAt < ReuseFor) ? kept : await ReadInventoryAsync(ct).ConfigureAwait(false)) is { } inventory
			? LevelPlanner.NormalCards(inventory) : null;

	/// <summary>
	/// 1.8.2: every item held by market name, with its copies' ids - to see a bought card arrive. Not fresh: the inventory read
	/// a few minutes ago (the plan's) will do. Fresh: read now, never sooner than 20 seconds after the last read - Steam turns
	/// a quick second read down as a duplicate, and an inventory is read gently.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> AssetsAsync(bool fresh, CancellationToken ct) {
		InventoryContents? inventory = !fresh && (_inventory is { } kept) && (DateTime.UtcNow - _inventoryAt < KeptInventory) ? kept : null;

		if (inventory == null) {
			TimeSpan since = DateTime.UtcNow - _inventoryAt;

			if (since < InventoryGap) {
				await Task.Delay(InventoryGap - since, ct).ConfigureAwait(false);
			}

			inventory = await ReadInventoryAsync(ct).ConfigureAwait(false);
		}

		return inventory == null ? null : CardMarket.AssetsOf(inventory);
	}

	/// <summary>1.8.2: the inventory the plan read, while it's under <see cref="KeptInventory"/> old - never read again for it.</summary>
	public Task<InventoryContents?> InventoryAsync(CancellationToken ct) =>
		Task.FromResult((_inventory is { } kept) && (DateTime.UtcNow - _inventoryAt < KeptInventory) ? kept : null);

	/// <summary>How long an inventory read stands in for a fresh one, and the least time between two reads.</summary>
	internal static readonly TimeSpan KeptInventory = TimeSpan.FromMinutes(5), InventoryGap = TimeSpan.FromSeconds(20);

	private InventoryContents? _inventory;
	private DateTime _inventoryAt = DateTime.MinValue;

	/// <summary>The Steam inventory read whole now, and kept for <see cref="AssetsAsync"/>. Null when it couldn't be read whole.</summary>
	private async Task<InventoryContents?> ReadInventoryAsync(CancellationToken ct) {
		InventoryContents? read = await Inventory.ReadAsync(bot, CardMarket.SteamItems, "6", ct).ConfigureAwait(false);
		_inventoryAt = DateTime.UtcNow;
		_inventory = read is { Complete: true } ? read : null;

		return _inventory;
	}

	/// <summary>1.8.2: the game's own search, now (the set finder's page, US cents) - every page of it, three at most.</summary>
	public async Task<IReadOnlyDictionary<string, int>?> SetSearchAsync(uint app, CancellationToken ct) {
		Dictionary<string, int> found = new(StringComparer.Ordinal);

		for (int start = 0, pages = 0; pages < 3; pages++) {
			(string? json, _) = await SparePrices.Get(SetFinder.GamePage(app, start), ct).ConfigureAwait(false);

			if (SparePrices.ParseSearch(json) is not { } page) {
				return null;
			}

			foreach ((string hash, SparePrices.Row row) in page.Rows) {
				found[hash] = row.Cents;
			}

			start += page.Rows.Count;

			if ((page.Rows.Count == 0) || (start >= page.Total)) {
				break;
			}
		}

		return found;
	}

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
			string? json = await _web.GetAsync(new Uri(WebSession.Community, $"/market/orderbook?q=Load&qp={Uri.EscapeDataString(qp)}"), ct).ConfigureAwait(false);

			return CardMarket.ParseBook(json);
		} finally {
			_lastRead = DateTime.UtcNow;
			Pace.Release();
		}
	}

	/// <summary>
	/// One card: a buy order for one at the order's price, which the cheapest listing fills at once. Asked what it bought a
	/// few times; still open after that, it's taken back - an order is never left waiting on the market. Once the order is
	/// sent, <paramref name="ct"/> no longer stops it: finding out and taking it back run on their own clock. Steam wanting
	/// it confirmed in the Steam app: with the authenticator here exactly that confirmation is accepted; without, it's left
	/// for the owner's phone (Waiting) - nothing is bought until it's sent again with it (<see cref="ResumeAsync"/>).
	/// </summary>
	public async Task<BuyAnswer> BuyAsync(BuyOrder order, Action<string>? placed, CancellationToken ct) {
		if (ct.IsCancellationRequested) {
			return new BuyAnswer(BuyResult.NotSent, 0, "stopped before anything was sent");
		}

		string hash = order.Hash;
		long price = order.Price + order.Tax;
		Uri page = Page(hash);
		using CancellationTokenSource own = new(AfterSending);
		CancellationToken t = own.Token;

		try {
			(string? body, bool sent) = await _web.PostAsync(new Uri(WebSession.Community, "/market/createbuyorder/"), OrderForm(order, "0"), page, t).ConfigureAwait(false);

			if (!sent) {
				return new BuyAnswer(BuyResult.NotSent, 0, "it couldn't be sent - nothing bought");
			}

			Order first = ReadOrder(body);

			switch (first.Kind) {
				case OrderKind.Placed:
					return await PlacedAsync(first.Id!, price, page, placed, t).ConfigureAwait(false);
				case OrderKind.Refused:
					return new BuyAnswer(BuyResult.Refused, 0, first.Message, Leftover: first.Leftover);
				case OrderKind.Unclear:
					Log.Debug(new Said("buy order for {0} · no clear answer · {1}", hash, Shown(body)), bot.Name);

					return new BuyAnswer(BuyResult.Unsure, price, "Steam didn't answer clearly");
			}

			// Steam wants the purchase confirmed. No authenticator here: left for the owner's phone - nothing is bought until
			// the order is sent again with it, at the next look.
			string confirm = first.Confirm!;

			if (!_web.CanConfirm) {
				return new BuyAnswer(BuyResult.Waiting, price, "waiting to be confirmed in the Steam app", null, confirm);
			}

			// With its authenticator here, exactly this confirmation is accepted - the one Steam just named, and only a
			// market purchase - and the order sent again with it.

			if (!await ConfirmAsync(confirm, accept: true, t).ConfigureAwait(false)) {
				// Nothing is left waiting in the Steam app: exactly that confirmation is turned down.
				return await ConfirmAsync(confirm, accept: false, t).ConfigureAwait(false)
					? new BuyAnswer(BuyResult.Refused, 0, "Steam wanted the purchase confirmed, and it couldn't be - turned down")
					: new BuyAnswer(BuyResult.Unsure, price, "a purchase confirmation may still be waiting in the Steam app");
			}

			(body, sent) = await _web.PostAsync(new Uri(WebSession.Community, "/market/createbuyorder/"), OrderForm(order, confirm), page, t).ConfigureAwait(false);
			Order second = sent ? ReadOrder(body) : default;

			if (second.Kind == OrderKind.Placed) {
				return await PlacedAsync(second.Id!, price, page, placed, t).ConfigureAwait(false);
			}

			// Confirmed, and then no order id: it may well have gone in.
			Log.Debug(new Said("buy order for {0} · no clear answer · {1}", hash, Shown(body)), bot.Name);

			return new BuyAnswer(BuyResult.Unsure, price, "confirmed, but Steam didn't say the order went in");
		} catch (Exception e) {
			// Sent, or maybe sent: whatever broke, it may have bought.
			return new BuyAnswer(BuyResult.Unsure, price, Log.Describe(e));
		}
	}

	/// <summary>
	/// An order bought earlier that Steam didn't settle: what it bought, or - still open - taken back. Only ever an order
	/// this program placed (its id was written down when it was), never one the owner placed by hand.
	/// </summary>
	public async Task<BuyAnswer> CheckOrderAsync(string orderId, long price, string hash, CancellationToken ct) {
		if (ct.IsCancellationRequested) {
			return new BuyAnswer(BuyResult.Unsure, price, "", orderId);
		}

		using CancellationTokenSource own = new(AfterSending);

		try {
			return await SettleAsync(orderId, price, string.IsNullOrEmpty(hash) ? null : Page(hash), looks: 1, own.Token).ConfigureAwait(false);
		} catch (Exception e) {
			return new BuyAnswer(BuyResult.Unsure, price, Log.Describe(e), orderId);
		}
	}

	/// <summary>
	/// A purchase the owner confirmed in the Steam app (or hasn't yet): the same order sent again with that confirmation,
	/// as Steam's market page does. Placed, it's settled like any order. Not confirmed yet: Waiting, with the confirmation
	/// Steam names now. Not sent: still Waiting - nothing happened. Anything Steam says no to: Refused, nothing bought.
	/// </summary>
	public async Task<BuyAnswer> ResumeAsync(BuyOrder order, string confirmation, Action<string>? placed, CancellationToken ct) {
		long price = order.Price + order.Tax;

		if (ct.IsCancellationRequested) {
			return new BuyAnswer(BuyResult.Waiting, price, "", null, confirmation);
		}

		Uri page = Page(order.Hash);
		using CancellationTokenSource own = new(AfterSending);
		CancellationToken t = own.Token;

		try {
			(string? body, bool sent) = await _web.PostAsync(new Uri(WebSession.Community, "/market/createbuyorder/"), OrderForm(order, confirmation), page, t).ConfigureAwait(false);

			if (!sent) {
				return new BuyAnswer(BuyResult.Waiting, price, "", null, confirmation);
			}

			Order again = ReadOrder(body);

			switch (again.Kind) {
				case OrderKind.Placed:
					return await PlacedAsync(again.Id!, price, page, placed, t).ConfigureAwait(false);
				case OrderKind.Confirm:
					return new BuyAnswer(BuyResult.Waiting, price, "", null, again.Confirm);
				case OrderKind.Refused:
					return new BuyAnswer(BuyResult.Refused, 0, again.Message);
			}

			Log.Debug(new Said("buy order for {0} · no clear answer · {1}", order.Hash, Shown(body)), bot.Name);

			return new BuyAnswer(BuyResult.Unsure, price, "Steam didn't answer clearly");
		} catch (Exception e) {
			return new BuyAnswer(BuyResult.Unsure, price, Log.Describe(e));
		}
	}

	public Task<MarketQuote?> QuoteAsync(string hash, int currency, CancellationToken ct) => MarketQuotes.FetchAsync(hash, currency, ct);

	/// <summary>The market's own front page - where Steam lists the account's buy orders ("My buy orders").</summary>
	private static readonly Uri MarketHome = new(WebSession.Community, "/market/");

	/// <summary>
	/// 1.8.2: the account's open buy orders, as the market's front page lists them - one read, from the page itself as a
	/// browser opens it. Null when the page couldn't be read (nothing is taken back on a guess then).
	/// </summary>
	public async Task<IReadOnlyList<OpenBuyOrder>?> OpenOrdersAsync(CancellationToken ct) =>
		ReadOpenOrders(await _web.GetFromAsync(MarketHome, MarketHome, ct).ConfigureAwait(false));

	private static readonly Regex OrderRow = new("<div[^>]*\\bid=\"mybuyorder_(\\d+)\"", RegexOptions.CultureInvariant);
	private static readonly Regex OrderItem = new("href=\"[^\"]*/market/listings/753/([^\"?#]+)\"", RegexOptions.CultureInvariant);
	private static readonly Regex OrderPrice = new("market_listing_inline_buyorder_qty\"\\s*>\\s*(\\d+)\\s*@\\s*</span>\\s*([^<]+?)\\s*</span>", RegexOptions.CultureInvariant);

	/// <summary>
	/// "My buy orders" on the market's front page, read defensively: each row's order id (mybuyorder_&lt;id&gt;), its card
	/// (the link to the card's page) and its price per card as shown ("1 @ 0,05€"). A row it can't read whole is left out -
	/// never taken back. Null when the page isn't the account's market page at all (signed out, an error page).
	/// </summary>
	public static List<OpenBuyOrder>? ReadOpenOrders(string? html) {
		if (string.IsNullOrWhiteSpace(html) || (!html.Contains("tabContentsMyListings", StringComparison.Ordinal) && !html.Contains("mybuyorder_", StringComparison.Ordinal))) {
			return null;
		}

		List<OpenBuyOrder> found = [];
		MatchCollection rows = OrderRow.Matches(html);

		for (int i = 0; i < rows.Count; i++) {
			int start = rows[i].Index;
			int end = i + 1 < rows.Count ? rows[i + 1].Index : Math.Min(html.Length, start + 6000);
			string row = html[start..end];

			if ((OrderItem.Match(row) is not { Success: true } item) || (OrderPrice.Match(row) is not { Success: true } price)) {
				continue;
			}

			string hash;

			try {
				hash = Uri.UnescapeDataString(item.Groups[1].Value);
			} catch (UriFormatException) {
				continue;
			}

			if ((hash.Length == 0) || !int.TryParse(price.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int quantity) || (quantity <= 0)) {
				continue;
			}

			found.Add(new OpenBuyOrder(rows[i].Groups[1].Value, hash, MoneyCents(System.Net.WebUtility.HtmlDecode(price.Groups[2].Value)) ?? -1, quantity));
		}

		return found;
	}

	/// <summary>
	/// A price as the market writes it, in cents: "$0.05", "0,05€", "1,--€", "CDN$ 1.23", "¥ 500" (no cents: whole units).
	/// Null when there's no number in it.
	/// </summary>
	public static long? MoneyCents(string text) {
		string t = text.Replace("--", "00", StringComparison.Ordinal);
		int first = -1, last = -1;

		for (int i = 0; i < t.Length; i++) {
			if (char.IsAsciiDigit(t[i])) {
				first = first < 0 ? i : first;
				last = i;
			}
		}

		if (first < 0) {
			return null;
		}

		string number = t[first..(last + 1)];
		string digits = new([.. number.Where(char.IsAsciiDigit)]);
		int separator = number.LastIndexOfAny(['.', ',']);
		bool cents = (separator >= 0) && (number.Length - separator - 1 == 2) && number[(separator + 1)..].All(char.IsAsciiDigit);

		return (digits.Length > 12) || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? null : cents ? n : n * 100;
	}

	private static Uri Page(string hash) => new(WebSession.Community, $"/market/listings/{CardMarket.SteamItems}/{Uri.EscapeDataString(hash)}");

	/// <summary>The market's page of one's own buy orders - where an earlier order is asked about from.</summary>
	private static readonly Uri OrdersPage = new(WebSession.Community, "/market/buyorders");

	/// <summary>
	/// The createbuyorder form, field for field as Steam's own buy-order dialog sends it (October 2026): one card at the
	/// order's price, the tax on the fee worked out from the wallet page, and the billing details saved on the account -
	/// the dialog fills them in from there and saves them back as they are. Never logged: they're personal.
	/// </summary>
	internal static Dictionary<string, string> OrderForm(BuyOrder order, string confirmation) {
		CardMarket.Billing billing = order.Wallet.Saved ?? new CardMarket.Billing();

		return new(StringComparer.Ordinal) {
			["currency"] = order.Currency.ToString(CultureInfo.InvariantCulture),
			["appid"] = CardMarket.SteamItems.ToString(CultureInfo.InvariantCulture),
			["market_hash_name"] = order.Hash,
			["price_total"] = order.Price.ToString(CultureInfo.InvariantCulture),
			["tradefee_tax"] = order.Tax.ToString(CultureInfo.InvariantCulture),
			["quantity"] = "1",
			["first_name"] = billing.FirstName,
			["last_name"] = billing.LastName,
			["billing_address"] = billing.Address,
			["billing_address_two"] = billing.AddressTwo,
			["billing_country"] = billing.Country,
			["billing_city"] = billing.City,
			["billing_state"] = billing.State,
			["billing_postal_code"] = billing.PostalCode,
			["confirmation"] = confirmation,
			["save_my_address"] = "1"
		};
	}

	private async Task<BuyAnswer> PlacedAsync(string orderId, long price, Uri page, Action<string>? placed, CancellationToken t) {
		placed?.Invoke(orderId);   // written down before anything else, so a sweep can find it whatever happens next

		return await SettleAsync(orderId, price, page, looks: 4, t).ConfigureAwait(false);
	}

	/// <summary>Ask what an order did; still open (or not said), take it back and ask again. Bought, Gone or Unsure - never a guess.</summary>
	private async Task<BuyAnswer> SettleAsync(string orderId, long price, Uri? page, int looks, CancellationToken t) {
		for (int look = 0; look < looks; look++) {
			if (page != null) {
				await _web.DelayAsync(Rng.Seconds(2, 3), t).ConfigureAwait(false);
			}

			OrderStatus? status = await StatusAsync(orderId, page, t).ConfigureAwait(false);

			if (Settled(status, orderId, price) is { } done) {
				return done;
			}

			// No answer (an error, a rate limit): asking again at once only makes it worse - it's taken back now, and the
			// inventory says later whether the card came (CardBuyer).
			if (status == null) {
				break;
			}
		}

		(string? body, _) = await _web.PostAsync(new Uri(WebSession.Community, "/market/cancelbuyorder/"),
			new Dictionary<string, string>(StringComparer.Ordinal) { ["buy_orderid"] = orderId }, page, t).ConfigureAwait(false);
		bool cancelled = ReadCancel(body);

		// Filled between the last look and taking it back is bought after all - so it's asked once more, whatever the
		// cancel said.
		for (int again = 0; again < 2; again++) {
			await _web.DelayAsync(Rng.Seconds(2, 3), t).ConfigureAwait(false);
			OrderStatus? after = await StatusAsync(orderId, page, t).ConfigureAwait(false);

			if (Settled(after, orderId, price) is { } done) {
				return done;
			}

			if (after is null or { Active: > 0 }) {
				break;   // no answer, or still open: asking again at once won't change that
			}
		}

		Log.Debug(new Said("buy order {0} · not settled yet · {1}", orderId, Shown(body)), bot.Name);

		return new BuyAnswer(BuyResult.Unsure, price, cancelled ? "taken back, but Steam didn't say whether it bought" : "the order couldn't be taken back yet", orderId);
	}

	private static BuyAnswer? Settled(OrderStatus? status, string orderId, long price) => status switch {
		{ Filled: true } s => new BuyAnswer(BuyResult.Bought, s.Paid > 0 ? s.Paid : price, "", orderId),
		{ Gone: true } => new BuyAnswer(BuyResult.Gone, 0, "", orderId),
		_ => null
	};

	/// <summary>
	/// What a buy order has done. Null when Steam didn't say. Asked as Steam's own market page asks it (October 2026): GET
	/// /market/getbuyorderstatus/?sessionid=&amp;buy_orderid= from the card's page - with the page it's asked from, as a
	/// browser always sends it. Asked without one, Steam answered 400 [] every time (October 2026, the owner's account).
	/// </summary>
	private async Task<OrderStatus?> StatusAsync(string orderId, Uri? page, CancellationToken t) {
		string? json = await _web.GetFromAsync(new Uri(WebSession.Community,
			$"/market/getbuyorderstatus/?sessionid={Uri.EscapeDataString(_web.SessionId)}&buy_orderid={Uri.EscapeDataString(orderId)}"), page ?? OrdersPage, t).ConfigureAwait(false);

		return ReadStatus(json);
	}

	/// <summary>Accept (or deny) exactly the confirmation Steam named for this purchase, with the account's authenticator. False when it can't.</summary>
	private async Task<bool> ConfirmAsync(string confirmationId, bool accept, CancellationToken t) {
		if (!_web.CanConfirm || !ulong.TryParse(confirmationId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || (id == 0)) {
			return false;
		}

		for (int attempt = 0; attempt < 3; attempt++) {
			await _web.DelayAsync(Rng.Seconds(2, 4), t).ConfigureAwait(false);
			(bool ok, IReadOnlyList<Confirmations.Item> items) = await _web.ConfirmationsAsync(t).ConfigureAwait(false);

			if (ok && (items.FirstOrDefault(i => (i.Id == id) && (i.Type == MarketPurchase)) is { } match)) {
				return await _web.ActAsync(match, accept, t).ConfigureAwait(false);
			}
		}

		return false;
	}

	/// <summary>A createbuyorder answer, read.</summary>
	public enum OrderKind {
		/// <summary>Not an answer that says plainly what happened: it may have gone in.</summary>
		Unclear,

		/// <summary>An order went in - Steam gave its id.</summary>
		Placed,

		/// <summary>Steam wants it confirmed first.</summary>
		Confirm,

		/// <summary>Steam said no, readably, with no order: nothing happened.</summary>
		Refused
	}

	/// <param name="Leftover">1.8.2: refused because a buy order for this card is already open ("You already have an active
	/// buy order for this item") - proof an earlier order went in.</param>
	public readonly record struct Order(OrderKind Kind, string? Id, string? Confirm, string Message, bool Leftover = false);

	/// <summary>
	/// A createbuyorder answer. Any order id that isn't 0 means it went in, whatever "success" says (1 or true). Only a JSON
	/// answer that says no - success false, or a failure code - with no order is a refusal; anything else is unclear.
	/// </summary>
	public static Order ReadOrder(string? body) {
		if (string.IsNullOrWhiteSpace(body)) {
			return new Order(OrderKind.Unclear, null, null, "");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return new Order(OrderKind.Unclear, null, null, "");
			}

			string message = root.TryGetProperty("message", out JsonElement m) && (m.ValueKind == JsonValueKind.String) ? Log.Scrub(m.GetString() ?? "") : "";

			if (root.TryGetProperty("buy_orderid", out JsonElement id) && (Text(id) is { } order) && ulong.TryParse(order, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n) && (n > 0)) {
				return new Order(OrderKind.Placed, order, null, message);
			}

			if (root.TryGetProperty("need_confirmation", out JsonElement need) && (need.ValueKind == JsonValueKind.True)) {
				return root.TryGetProperty("confirmation", out JsonElement c) && (c.ValueKind == JsonValueKind.Object)
					&& c.TryGetProperty("confirmation_id", out JsonElement cid) && (Text(cid) is { Length: > 0 } confirm)
					? new Order(OrderKind.Confirm, null, confirm, message)
					: new Order(OrderKind.Unclear, null, null, message);
			}

			// No order: a refusal only when Steam says so plainly. Success with no order, Steam's "timed out" (16) or
			// "pending" (22), or no word at all - it may have gone in. A buy order for the card already open (Steam's
			// "duplicate request", 29): refused, and proof that an earlier order is still on the market.
			return root.TryGetProperty("success", out JsonElement s) && (Code(s) is { } code) && (code is not (1 or 16 or 22 or 0))
				? new Order(OrderKind.Refused, null, null, message, (code == 29) || message.Contains("active buy order", StringComparison.OrdinalIgnoreCase))
				: new Order(OrderKind.Unclear, null, null, message);
		} catch (Exception e) when (e is JsonException or InvalidOperationException) {
			return new Order(OrderKind.Unclear, null, null, "");
		}
	}

	/// <summary>A buy order's status. Active and Bought are -1 when Steam didn't say.</summary>
	public readonly record struct OrderStatus(int Active, int Bought, long Paid) {
		public bool Filled => Bought > 0;

		/// <summary>Closed with nothing bought - the only answer that means nothing was spent.</summary>
		public bool Gone => (Active == 0) && (Bought == 0);
	}

	/// <summary>A getbuyorderstatus answer: {"success":1,"active":0,"purchased":1,"quantity":"1","purchases":[{"price_total":7,...}]}. Null when it isn't one.</summary>
	public static OrderStatus? ReadStatus(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("success", out JsonElement s) || (Code(s) != 1)) {
				return null;
			}

			int active = root.TryGetProperty("active", out JsonElement a) && (Code(a) is { } on) ? (int) Math.Clamp(on, 0, int.MaxValue) : -1;
			int bought = root.TryGetProperty("purchased", out JsonElement p) && (Code(p) is { } got) ? (int) Math.Clamp(got, 0, int.MaxValue) : -1;
			long paid = 0;

			if (root.TryGetProperty("purchases", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement one in list.EnumerateArray()) {
					if (one.ValueKind != JsonValueKind.Object) {
						continue;
					}

					paid += one.TryGetProperty("price_total", out JsonElement total) && (Code(total) is { } whole) ? whole
						: (one.TryGetProperty("price_subtotal", out JsonElement sub) ? Code(sub) ?? 0 : 0) + (one.TryGetProperty("price_fee", out JsonElement fee) ? Code(fee) ?? 0 : 0);
				}
			}

			return new OrderStatus(active, bought, Math.Max(0, paid));
		} catch (Exception e) when (e is JsonException or InvalidOperationException) {
			return null;
		}
	}

	/// <summary>Whether a cancelbuyorder answer says it was taken back.</summary>
	public static bool ReadCancel(string? body) {
		if (string.IsNullOrWhiteSpace(body)) {
			return false;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			return (doc.RootElement.ValueKind == JsonValueKind.Object) && doc.RootElement.TryGetProperty("success", out JsonElement s) && (Code(s) == 1);
		} catch (JsonException) {
			return false;
		}
	}

	/// <summary>An answer for the log: Steam's JSON as it is, a page by its title.</summary>
	private static string Shown(string? body) => body == null ? "-" : Log.Scrub(WebSession.FailureText(body));

	private static string? Text(JsonElement e) => e.ValueKind switch {
		JsonValueKind.String => e.GetString(),
		JsonValueKind.Number => e.GetRawText(),
		_ => null
	};

	/// <summary>A number Steam may send as a number, as text, or as true/false (1/2 - EResult's OK and Fail). Null when it's none of those.</summary>
	private static long? Code(JsonElement e) => e.ValueKind switch {
		JsonValueKind.True => 1,
		JsonValueKind.False => 2,
		JsonValueKind.Number when e.TryGetInt64(out long n) => n,
		JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n,
		_ => null
	};
}

/// <summary>What buying cards needs from the account's web session and its authenticator. A fake in the tests - nothing reaches Steam.</summary>
public interface IMarketWeb {
	Task<string?> GetAsync(Uri url, CancellationToken ct);

	/// <summary>GET from a page, as a browser does - the market answers some reads only then. Without one here: a plain GET.</summary>
	Task<string?> GetFromAsync(Uri url, Uri referer, CancellationToken ct) => GetAsync(url, ct);

	/// <summary>POST a form. Sent is false only when it never left this PC; a failed answer's JSON body (Steam's reason) comes back.</summary>
	Task<(string? Body, bool Sent)> PostAsync(Uri url, Dictionary<string, string> form, Uri? referer, CancellationToken ct);

	string SessionId { get; }

	/// <summary>Whether its authenticator is here, to confirm (or deny) its own purchases.</summary>
	bool CanConfirm { get; }

	Task<(bool Ok, IReadOnlyList<Confirmations.Item> Items)> ConfirmationsAsync(CancellationToken ct);

	Task<bool> ActAsync(Confirmations.Item item, bool accept, CancellationToken ct);

	Task DelayAsync(TimeSpan wait, CancellationToken ct);
}

/// <summary>The account's own web session and authenticator.</summary>
internal sealed class BotMarketWeb(Bot bot) : IMarketWeb {
	public Task<string?> GetAsync(Uri url, CancellationToken ct) => bot.Web.GetAsync(url, ct);

	public Task<string?> GetFromAsync(Uri url, Uri referer, CancellationToken ct) => bot.Web.GetFromAsync(url, referer, ct);

	public Task<(string? Body, bool Sent)> PostAsync(Uri url, Dictionary<string, string> form, Uri? referer, CancellationToken ct) =>
		bot.Web.PostTrackedAsync(url, form, referer, ct, errorVerdict: true);

	public string SessionId => bot.Web.SessionId;

	public bool CanConfirm => bot.CanConfirmTrades;

	public async Task<(bool Ok, IReadOnlyList<Confirmations.Item> Items)> ConfirmationsAsync(CancellationToken ct) {
		(bool ok, _, List<Confirmations.Item> items) = await Confirmations.ListAsync(bot, ct, fresh: true).ConfigureAwait(false);

		return (ok, items);
	}

	public Task<bool> ActAsync(Confirmations.Item item, bool accept, CancellationToken ct) => Confirmations.ActAsync(bot, [item], accept, ct);

	public Task DelayAsync(TimeSpan wait, CancellationToken ct) => Task.Delay(wait, ct);
}
