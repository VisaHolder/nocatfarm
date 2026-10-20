using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// The market as buying cards reads it, from Steam's own answers (October 2026).
/// </summary>
/// <remarks>
/// Checked against Steam itself rather than guessed - the community market was rebuilt in 2026:
/// <list type="bullet">
/// <item>Trading cards are "commodity" items. The old /market/listings/&lt;app&gt;/&lt;name&gt;/render JSON is gone (it
/// answers with the new page), and the new page's listings query (/market/actions, QueryListingsForItem) lists no single
/// listings for a commodity at all - so there's no listing id to hand /market/buylisting/. That endpoint is only for
/// one-of-a-kind items now.</item>
/// <item>A commodity's sell side comes from GET /market/orderbook?q=Load&amp;qp=[753,"&lt;hash&gt;"]: the cheapest price
/// (amtMinSellOrder), how many are listed (cSellOrders) and every price with its count (rgCompactSellOrders, price and
/// count in turn), in the reader's own currency (eCurrency) - the wallet's when signed in. Prices are what the buyer pays,
/// Steam's fees included. The old itemordershistogram needed an item_nameid the new page no longer shows.</item>
/// <item>The new page buys a commodity with a buy order: POST /market/createbuyorder/ with price_total (per card times the
/// quantity, the buyer's price), quantity, currency and the hash. One at the cheapest listing's price is filled straight
/// away; GET /market/getbuyorderstatus/?sessionid=&amp;buy_orderid= (asked from the card's page, as the page asks it - its
/// answer has purchased and purchases when filled; the page asks every second for 5 seconds, then counts the order as
/// placed) says what it bought, and /market/cancelbuyorder/ takes one back that wasn't.</item>
/// <item>Steam may want each purchase confirmed (need_confirmation with a confirmation_id): by email, or in the mobile
/// app - /market/userbillinginfo says which (confirmation_type 1 email, 2 mobile), with the wallet (wallet_info), the
/// billing details saved on the account (billing_address, country_code, billing_states), the tax on the market fee
/// (tax_rate) and whether a buy order has to carry billing details (require_billing_info).</item>
/// <item>Steam's own buy-order dialog (its market script, read October 2026) fills the billing fields in from the saved
/// billing_address and sends them with every buy order - require_billing_info only makes it show them, and it stops
/// only when they're incomplete (no names, address, city or postal code; no state where the country has states; a US
/// postal code under 5 digits). tradefee_tax is worked out from tax_rate, on top of price_total. Wanting a confirmation,
/// the dialog waits and sends the same order again with confirmation=&lt;id&gt; every 1.5 seconds: not confirmed yet is
/// need_confirmation again; confirmed, the order goes in (a buy_orderid); anything else is "not confirmed". Nothing is
/// bought until that second order - confirming on the phone alone buys nothing.</item>
/// </list>
/// </remarks>
public static class CardMarket {
	/// <summary>Steam's own inventory, where trading cards live.</summary>
	public const uint SteamItems = 753;

	/// <summary>A card's sell side: the cheapest price, how many are listed, and each price with its count, cheapest first.</summary>
	public sealed record Book(long Lowest, int Currency, int Listings, IReadOnlyList<(long Price, int Count)> Levels) {
		/// <summary>What the second-cheapest card costs - the cheapest again when two or more are listed at it.</summary>
		public long Second {
			get {
				int seen = 0;

				foreach ((long price, int count) in Levels) {
					seen += count;

					if (seen >= 2) {
						return price;
					}
				}

				return 0;
			}
		}

		/// <summary>
		/// A lone listing far under the rest - a dump: the next card costs more than half as much again (and over 2 cents more).
		/// A price made by one listing like that isn't the card's price: the moment somebody buys it, the set costs more.
		/// </summary>
		public bool Dumped => (Second > 0) && (Second > Lowest + 2) && (Second * 2 > Lowest * 3);
	}

	/// <summary>
	/// An orderbook answer: {"data":{"success":true,"data":{"amtMinSellOrder":5,"eCurrency":20,"cSellOrders":154088,
	/// "rgCompactSellOrders":[5,39,6,248,...]}}}. Null when it isn't one; a card nobody sells has Lowest 0.
	/// </summary>
	public static Book? ParseBook(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			// The query wraps its answer in "data", and the book's own answer is "data" again.
			if (root.ValueKind != JsonValueKind.Object) {
				return null;
			}

			if (root.TryGetProperty("data", out JsonElement outer) && (outer.ValueKind == JsonValueKind.Object)) {
				root = outer;
			}

			if (!Flag(root, "success") || !root.TryGetProperty("data", out JsonElement d) || (d.ValueKind != JsonValueKind.Object)) {
				return null;
			}

			List<(long, int)> levels = [];

			if (d.TryGetProperty("rgCompactSellOrders", out JsonElement compact) && (compact.ValueKind == JsonValueKind.Array)) {
				long[] n = [.. compact.EnumerateArray().Select(static e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) ? v : -1)];

				for (int i = 0; i + 1 < n.Length; i += 2) {
					if ((n[i] > 0) && (n[i + 1] > 0)) {
						levels.Add((n[i], (int) Math.Min(int.MaxValue, n[i + 1])));
					}
				}
			}

			levels.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
			long lowest = Num(d, "amtMinSellOrder");
			int listings = (int) Math.Clamp(Num(d, "cSellOrders"), 0, int.MaxValue);

			if ((lowest <= 0) && (levels.Count > 0)) {
				lowest = levels[0].Item1;
			}

			return new Book(listings > 0 ? Math.Max(0, lowest) : 0, (int) Math.Clamp(Num(d, "eCurrency"), 0, int.MaxValue),
				listings > 0 ? listings : levels.Sum(static l => l.Item2), levels);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>The account's wallet and what the market asks of a purchase.</summary>
	/// <param name="NeedsBilling">
	/// Steam's require_billing_info: every buy order has to carry the billing details. Steam's own market page fills them in
	/// from the ones saved on the account (<paramref name="Saved"/>) - it only stops when there are none to fill in.
	/// </param>
	/// <param name="Confirm">Steam's confirmation_type: 0 none, 1 by email, 2 in the mobile app; -1 when it wasn't said.</param>
	/// <param name="Saved">The billing details saved on the account, and its country. Never logged or shown.</param>
	/// <param name="FeeTaxRate">The tax Steam adds on its market fee, in percent (tax_rate.tradefee_taxrate); 0 when it adds none.</param>
	public sealed record Wallet(long Balance, int Currency, bool NeedsBilling, int Confirm, Billing? Saved = null, double FeeTaxRate = 0) {
		/// <summary>The tax on the market fee for one card at this price, worked out as Steam's market page does. 0 for none.</summary>
		public long FeeTax(long price) {
			if ((FeeTaxRate <= 0) || (price < 3)) {
				return 0;
			}

			long fees;

			if (price - 2 <= 20) {
				fees = 2;
			} else {
				// The fees in a buyer's price: Steam's 5% and the game's 10%, at least 1 each - found the way the page finds them.
				long net = price * 100 / 115;
				fees = 0;

				for (int i = 0; i < 3; i++) {
					fees = Math.Max(1, net * 10 / 100) + Math.Max(1, net * 5 / 100);

					if (net + fees < price) {
						net++;
					} else {
						break;
					}
				}
			}

			return (long) Math.Floor((fees * FeeTaxRate / 100) + 0.5);
		}
	}

	/// <summary>
	/// The billing details saved on an account, as /market/userbillinginfo gives them, for buy orders. Personal: never
	/// written to the log, the screen or a file - <see cref="ToString"/> says only what it is.
	/// </summary>
	public sealed class Billing {
		public string FirstName { get; init; } = "";
		public string LastName { get; init; } = "";
		public string Address { get; init; } = "";
		public string AddressTwo { get; init; } = "";
		public string City { get; init; } = "";
		public string State { get; init; } = "";
		public string PostalCode { get; init; } = "";

		/// <summary>The account's country (country_code).</summary>
		public string Country { get; init; } = "";

		/// <summary>The country has states or provinces to pick from (billing_states isn't empty): one is needed.</summary>
		public bool HasStates { get; init; }

		/// <summary>Whether Steam's market page would send these as they are - its own check: names, address, city, postal code, a state where there are states.</summary>
		public bool Complete {
			get {
				if ((FirstName.Length == 0) || (LastName.Length == 0) || (Address.Length == 0) || (City.Length == 0) || (PostalCode.Length == 0)) {
					return false;
				}

				if (HasStates && (State.Length == 0)) {
					return false;
				}

				if (Country.Equals("US", StringComparison.OrdinalIgnoreCase)) {
					if (PostalCode.Length < 5) {
						return false;
					}

					// A US military state goes with a military city, or the page says no.
					if (HasStates && (State is "AE" or "AP" or "AA") && (City is not ("APO" or "FPO" or "DPO"))) {
						return false;
					}
				}

				return true;
			}
		}

		public override string ToString() => "billing details";
	}

	/// <summary>A /market/userbillinginfo answer. Null when it isn't one (signed out, an error page).</summary>
	public static Wallet? ParseWallet(string? json) {
		if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("wallet_info", out JsonElement w) || (w.ValueKind != JsonValueKind.Object)) {
				return null;
			}

			int confirm = root.TryGetProperty("confirmation_type", out JsonElement c) ? (int) Math.Clamp(Value(c), -1, 99) : -1;

			return new Wallet(Math.Max(0, Num(w, "wallet_balance")), (int) Math.Clamp(Num(w, "wallet_currency"), 0, int.MaxValue),
				Flag(root, "require_billing_info"), confirm, SavedBilling(root), FeeTaxRate(root));
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>The saved billing details: billing_address {firstname, lastname, address1, address2, city, state, postcode}, country_code, billing_states.</summary>
	private static Billing SavedBilling(JsonElement root) {
		JsonElement a = root.TryGetProperty("billing_address", out JsonElement found) && (found.ValueKind == JsonValueKind.Object) ? found : default;

		string Text(JsonElement e, string name) =>
			(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? (v.GetString() ?? "").Trim() : "";

		bool states = root.TryGetProperty("billing_states", out JsonElement s) && (((s.ValueKind == JsonValueKind.Object) && s.EnumerateObject().Any())
			|| ((s.ValueKind == JsonValueKind.Array) && (s.GetArrayLength() > 0)));

		return new Billing {
			FirstName = Text(a, "firstname"),
			LastName = Text(a, "lastname"),
			Address = Text(a, "address1"),
			AddressTwo = Text(a, "address2"),
			City = Text(a, "city"),
			State = Text(a, "state"),
			PostalCode = Text(a, "postcode"),
			Country = Text(root, "country_code"),
			HasStates = states
		};
	}

	/// <summary>tax_rate {tradefee_addtax, tradefee_taxrate}: the percent added on the market fee, or 0.</summary>
	private static double FeeTaxRate(JsonElement root) {
		if (!root.TryGetProperty("tax_rate", out JsonElement t) || (t.ValueKind != JsonValueKind.Object) || (!Flag(t, "tradefee_addtax") && (Num(t, "tradefee_addtax") <= 0))) {
			return 0;
		}

		if (!t.TryGetProperty("tradefee_taxrate", out JsonElement r)) {
			return 0;
		}

		double rate = r.ValueKind switch {
			JsonValueKind.Number when r.TryGetDouble(out double n) => n,
			JsonValueKind.String when double.TryParse(r.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) => n,
			_ => 0
		};

		return double.IsFinite(rate) ? Math.Clamp(rate, 0, 100) : 0;
	}

	/// <summary>1.8.2: every item of an inventory by market name, with the ids of its copies - what a bought card is seen arriving by.</summary>
	public static IReadOnlyDictionary<string, IReadOnlyList<string>> AssetsOf(InventoryContents inventory) {
		Dictionary<string, List<string>> found = new(StringComparer.Ordinal);

		foreach (JsonElement asset in inventory.Assets) {
			string id = InventoryContents.Text(asset, "assetid");

			if ((id.Length > 0) && (inventory.DescriptionOf(asset) is { } d) && (InventoryContents.Text(d, "market_hash_name") is { Length: > 0 } hash)) {
				(found.TryGetValue(hash, out List<string>? ids) ? ids : found[hash] = []).Add(id);
			}
		}

		return found.ToDictionary(static f => f.Key, static f => (IReadOnlyList<string>) f.Value, StringComparer.Ordinal);
	}

	/// <summary>A JSON number Steam may send as a number or as text. 0 when it's missing or neither.</summary>
	private static long Num(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) ? Value(v) : 0;

	private static long Value(JsonElement v) => v.ValueKind switch {
		JsonValueKind.Number when v.TryGetInt64(out long n) => n,
		JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n,
		JsonValueKind.True => 1,
		_ => 0
	};

	private static bool Flag(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && ((v.ValueKind == JsonValueKind.True) || (Value(v) == 1));
}

/// <summary>A game's normal card set: the cards' market names, and what the market's search last said each costs in US cents (0: none listed).</summary>
public sealed record CardSet(uint App, string Game, IReadOnlyList<string> Cards, IReadOnlyDictionary<string, int> Usd) {
	public int UsdCents => Usd.Values.Sum();
}

/// <summary>Why Steam won't let an account buy by itself.</summary>
public enum BuyBlock {
	None,

	/// <summary>Steam wants billing details with each market purchase, and none are saved on the account to send.</summary>
	Billing,

	/// <summary>Every purchase confirmed by email.</summary>
	Email,

	/// <summary>
	/// No longer a block (kept so the numbers stay the same): a purchase Steam wants confirmed in the Steam app, with no
	/// authenticator here, waits for the owner to confirm it on the phone.
	/// </summary>
	App,

	/// <summary>Steam didn't say how its purchases are confirmed (the wallet page didn't answer): nothing is bought on a guess.</summary>
	Unknown
}

/// <summary>How one card purchase went.</summary>
public enum BuyResult {
	/// <summary>Bought.</summary>
	Bought,

	/// <summary>The order's status says it closed with nothing bought (active 0, purchased 0): nothing spent.</summary>
	Gone,

	/// <summary>Steam said no, readably, with no order in its answer - the reason is in the message. Nothing bought.</summary>
	Refused,

	/// <summary>Not sure what happened: it may have been bought. Counted as spent at the order's price, and asked about again.</summary>
	Unsure,

	/// <summary>Never sent - it didn't leave this PC. Nothing bought.</summary>
	NotSent,

	/// <summary>
	/// Waiting for the owner to confirm it in the Steam app (no authenticator here). Nothing is bought until the order is
	/// sent again with the confirmation, the way Steam's market page does; counted as spent until then.
	/// </summary>
	Waiting
}

/// <summary>A purchase still to send: the card, its price (the buyer's, fees in), the tax on the fee, and the wallet it's paid from.</summary>
public sealed record BuyOrder(uint App, string Hash, long Price, long Tax, CardMarket.Wallet Wallet) {
	public int Currency => Wallet.Currency;
}

/// <summary>One purchase's answer: how it went, what it cost, Steam's reason when it said no, the order's id once there is one, and the confirmation it waits on.</summary>
/// <param name="Leftover">1.8.2: refused because a buy order for the card is already open - an earlier one may have gone in.</param>
public sealed record BuyAnswer(BuyResult Result, long Paid, string Message = "", string? Order = null, string? Confirm = null, bool Leftover = false);

/// <summary>1.8.2: one of the account's open buy orders, as the market's front page lists it: its id, the card, the price per card (-1: not read) and how many.</summary>
public sealed record OpenBuyOrder(string Id, string Hash, long Price, int Quantity);

/// <summary>What buying cards needs from Steam. A fake in the tests - no test ever reaches Steam.</summary>
public interface ICardMarket {
	/// <summary>The wallet, read now. Null when Steam wouldn't say.</summary>
	Task<CardMarket.Wallet?> WalletAsync(CancellationToken ct);

	/// <summary>Each game badge's normal level (not foil). Null when Steam wouldn't say.</summary>
	Task<IReadOnlyDictionary<uint, int>?> BadgeLevelsAsync(CancellationToken ct);

	/// <summary>1.8.2: the account's Steam level and XP, as the last <see cref="BadgeLevelsAsync"/> said. Null when not known.</summary>
	(int Level, int Xp)? SteamLevel => null;

	/// <summary>Normal trading cards held: game -> market name -> copies. Null when the inventory couldn't be read whole.</summary>
	Task<IReadOnlyDictionary<uint, Dictionary<string, int>>?> CardsAsync(CancellationToken ct);

	/// <summary>A game's normal set. Null when the market didn't answer; no cards when it isn't known whole.</summary>
	Task<CardSet?> SetAsync(uint app, CancellationToken ct);

	/// <summary>Games with the cheapest cards on the market, cheapest first. Null when the market didn't answer.</summary>
	Task<IReadOnlyList<uint>?> CheapestGamesAsync(CancellationToken ct);

	/// <summary>A card's sell side, in the wallet's currency. Null when the market didn't answer.</summary>
	Task<CardMarket.Book?> BookAsync(string hash, CancellationToken ct);

	/// <summary>
	/// Buy one card for at most the order's price (the buyer's price, fees in) plus the tax on the fee, in the wallet's
	/// currency, with the billing details saved on the account. Spends money. <paramref name="placed"/> hears the order's id
	/// the moment Steam gives it. <paramref name="ct"/> only stops it before anything is sent (NotSent); once sent, it finds
	/// out and takes the order back on its own clock. Wanting a confirmation in the Steam app with no authenticator here,
	/// it's Waiting, with the confirmation's id.
	/// </summary>
	Task<BuyAnswer> BuyAsync(BuyOrder order, Action<string>? placed, CancellationToken ct);

	/// <summary>
	/// An order it placed earlier whose outcome wasn't known: what it bought, or - still open - taken back. <paramref name="hash"/>:
	/// the card's market name - Steam answers about an order only when asked from its card's page.
	/// </summary>
	Task<BuyAnswer> CheckOrderAsync(string orderId, long price, string hash, CancellationToken ct);

	/// <summary>
	/// A purchase left for the owner to confirm in the Steam app: the order sent again with that confirmation, as Steam's
	/// market page does. Confirmed, the order goes in and is asked about and taken back like any; not confirmed yet is
	/// Waiting; anything Steam says no to (turned down, too old) is Refused. Spends money, at most the order's price.
	/// </summary>
	Task<BuyAnswer> ResumeAsync(BuyOrder order, string confirmation, Action<string>? placed, CancellationToken ct);

	/// <summary>A card's price, last day's sales and how many sold, in the wallet's currency (priceoverview) - for selling a set. Null when the market didn't answer.</summary>
	Task<MarketQuote?> QuoteAsync(string hash, int currency, CancellationToken ct);

	/// <summary>
	/// 1.8.2: the account's open buy orders, from the market's front page - to find an order that went in when Steam's answer
	/// didn't give its id. Null when they couldn't be read (or aren't known here): nothing is taken back then.
	/// </summary>
	Task<IReadOnlyList<OpenBuyOrder>?> OpenOrdersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<OpenBuyOrder>?>(null);

	/// <summary>
	/// 1.8.2: one search of a game's cards on the market, now: each card's cheapest listing in US cents (0: nobody selling) -
	/// to check a whole set before it's started with one request. Null when the market didn't answer (or it isn't known here).
	/// </summary>
	Task<IReadOnlyDictionary<string, int>?> SetSearchAsync(uint app, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, int>?>(null);

	/// <summary>
	/// 1.8.2: every item held, by market name, with its copies' ids - to see a bought card arrive. Not <paramref name="fresh"/>:
	/// an inventory read a few minutes ago will do. Null when it couldn't be read whole (or isn't known here).
	/// </summary>
	Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> AssetsAsync(bool fresh, CancellationToken ct) =>
		Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>?>(null);

	/// <summary>
	/// 1.8.2: the inventory the plan read a few minutes ago, whole - never read again for this (Steam turns a quick second
	/// read down as a duplicate). What a dear game's cards are listed from (<see cref="DearCards"/>). Null when there's none
	/// at hand (or it isn't known here).
	/// </summary>
	Task<InventoryContents?> InventoryAsync(CancellationToken ct) => Task.FromResult<InventoryContents?>(null);

	/// <summary>
	/// 1.8.2: each game's foil badge level (0 or 1), from the badges the plan read - for weighing foil cards. Null when not
	/// known (or not known here): foil cards aren't weighed then.
	/// </summary>
	Task<IReadOnlyDictionary<uint, int>?> FoilLevelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<uint, int>?>(null);
}

/// <summary>
/// Card swaps, as buying cards asks them: whether a missing card could come by a swap instead, and for how much. Card
/// swapping answers through <see cref="SwapCost.Current"/> (Core/CardExchange.BuyCost.cs): a card is only waited for when
/// a match nocat.lol made brings it - otherwise it's bought.
/// </summary>
public interface ICardSwaps {
	/// <summary>
	/// What getting this card by a swap costs, in the wallet's currency (what goes out for it, priced), or null when no swap
	/// for it is on offer. Read only - asking never starts a swap.
	/// </summary>
	Task<long?> SwapCostAsync(Bot? bot, uint app, string hash, CancellationToken ct);
}

/// <summary>Where buying cards finds card swapping.</summary>
public static class SwapCost {
	private sealed class NoSwaps : ICardSwaps {
		public Task<long?> SwapCostAsync(Bot? bot, uint app, string hash, CancellationToken ct) => Task.FromResult<long?>(null);
	}

	/// <summary>No swap on offer, ever.</summary>
	public static readonly ICardSwaps None = new NoSwaps();

	/// <summary>The swaps buying cards asks: card swapping's own answer.</summary>
	public static ICardSwaps Current { get; set; } = new CardExchange.BuyCosts();
}

/// <summary>
/// One card to buy, at the cheapest listing's price (the buyer's price, fees in). <paramref name="Tax"/>: 1.8.2, the tax some
/// countries add on Steam's fee, paid on top - counted in what the set costs, as each purchase counts it.
/// </summary>
public sealed record BuyCard(string Hash, long Price, long Tax = 0) {
	/// <summary>The card's name, without the "730-" in front of its market name.</summary>
	public string Name => Hash.IndexOf('-') is int dash and > 0 ? Hash[(dash + 1)..] : Hash;
}

/// <summary>
/// One set to buy: the missing cards of a badge's next level (<paramref name="FullSet"/> false), or a whole set for a game
/// with none of it yet. Either way, one badge level: 100 XP once crafted.
/// </summary>
public sealed record BuyStep(uint App, string Game, bool FullSet, int Level, int SetSize, IReadOnlyList<BuyCard> Cards) {
	/// <summary>What the level really costs: every card's price and the tax on its fee - what the limits and the wallet are checked against.</summary>
	public long Cost => Cards.Sum(static c => c.Price + c.Tax);

	/// <summary>XP for every whole unit of money (100 cents): a level is 100 XP.</summary>
	public long XpPerUnit => Cost > 0 ? 100L * 100 / Cost : 0;
}

/// <summary>What buying cards would buy now, within its settings - and, when nothing, why.</summary>
public sealed class BuyPlan {
	public int Currency { get; set; }
	public long Wallet { get; set; }
	public long DayLimit { get; set; }
	public long WeekLimit { get; set; }
	public long DaySpent { get; set; }
	public long WeekSpent { get; set; }

	/// <summary>What may be spent now: inside the day's and the week's limits, and never more than the wallet holds.</summary>
	public long Room { get; set; }

	public List<BuyStep> Steps { get; } = [];

	/// <summary>
	/// Everything it could buy, priced now and ranked - cheapest next badge level first - whatever the limits leave room for
	/// today. What a set sold instead of crafted would buy is counted on this (<see cref="SetSale"/>).
	/// </summary>
	public List<BuyStep> Ranked { get; } = [];

	/// <summary>Whole normal sets it holds for a badge under level 5 - the ones badge crafting would craft.</summary>
	public List<(CardSet Set, int Level)> Complete { get; } = [];

	/// <summary>The preview's word on each whole set: sold instead of crafted, or crafted (<see cref="SetSale"/>).</summary>
	public List<SetSale.Verdict> Sales { get; } = [];

	/// <summary>1.8.2: the normal cards held, game -> market name -> copies, as the plan read them (empty until it has).</summary>
	public IReadOnlyDictionary<uint, Dictionary<string, int>> Held { get; set; } = new Dictionary<uint, Dictionary<string, int>>();

	/// <summary>1.8.2: each game badge's normal level, as the plan read them (empty until it has).</summary>
	public IReadOnlyDictionary<uint, int> BadgeLevels { get; set; } = new Dictionary<uint, int>();

	/// <summary>Why something wasn't bought, in plain words (English - console replies stay English).</summary>
	public List<string> Notes { get; } = [];

	/// <summary>
	/// The cheapest whole sets on the market for badges it can still level, from the set finder (<see cref="SetFinder"/>) -
	/// what the preview shows beside what it would buy. Only shown: every card is priced again before anything is bought.
	/// </summary>
	public List<SetFinder.Row> Market { get; } = [];

	/// <summary>
	/// 1.8.2: why each cheaper candidate it reached wasn't planned - a badge at level 5, a card listed once, the packs on it,
	/// dearer than the most to pay for a level - for the debug log (English, like the notes).
	/// </summary>
	public List<string> Skipped { get; } = [];

	/// <summary>
	/// 1.8.2: what each next badge level costs, the cheapest first, in the wallet's money - the levels priced this look
	/// (<see cref="Ranked"/>), then the set finder's cheapest known sets past them at the rate learned from the market (only
	/// ever a number to weigh by: nothing is bought at it). What money from gems or a set sold would buy (GemsChoice, SetSale).
	/// Whatever the most to pay for one level says (it only holds buying back), never past what's known (nothing is made up
	/// past the last one), none dearer than a day's or a week's limit, none past the Steam level buying stops at.
	/// </summary>
	public List<(uint App, long Cost)> Levels { get; } = [];

	/// <summary>1.8.2: <see cref="Levels"/> was worked out by a look (a plan put together by hand has only <see cref="Ranked"/>).</summary>
	public bool LevelsKnown { get; set; }

	/// <summary>What each next level costs, cheapest first: <see cref="Levels"/> when a look worked them out, else <see cref="Ranked"/>.</summary>
	public IReadOnlyList<(uint App, long Cost)> LevelCosts => LevelsKnown ? Levels : [.. Ranked.Select(static s => (s.App, s.Cost))];

	/// <summary>1.8.2: the most it pays for one badge level this look (the setting, or a little over what a level usually costs) - 0 when there's no level to go by.</summary>
	public long MaxLevelPrice { get; set; }

	/// <summary>1.8.2: the most to pay for a level is the automatic one (a little over what a level usually costs, <see cref="UsualLevelPrice"/>).</summary>
	public bool MaxLevelAuto { get; set; }

	/// <summary>
	/// 1.8.2: what a level usually costs, in the wallet's money - what the automatic most per level goes by: the middle of
	/// the set finder's 5 cheapest whole sets, else the cheapest level priced over the last week. 0: not known (or not automatic).
	/// </summary>
	public long UsualLevelPrice { get; set; }

	/// <summary>
	/// 1.8.2: the set finder hasn't checked the cheapest sets the lists name yet (<see cref="SetFinder.WarmUp"/>) - empty
	/// once it has: until then only whole sets at most a tenth over the cheapest known level are bought.
	/// </summary>
	public string WarmUp { get; set; } = "";

	/// <summary>1.8.2: while warming up, the cheapest sets checked and how many it waits for.</summary>
	public int WarmUpChecked { get; set; }

	public int WarmUpNeeded { get; set; }

	/// <summary>The automatic most per level, in percent of what a level usually costs: 5 sets known, and fewer.</summary>
	public const int CapPct = 125, CapPctFew = 150;

	/// <summary>While the set finder warms up: whole sets at most this many percent of the cheapest known level.</summary>
	public const int WarmUpPct = 110;

	/// <summary>
	/// 1.8.2: what a level usually costs, and how far over it the automatic most per level goes: the middle of the set
	/// finder's 5 cheapest whole sets in the wallet's money - 125% of it (150% when fewer than 5 are known) - else
	/// <paramref name="weekLow"/>, the cheapest level priced over the last week (150%). Null when neither is known.
	/// </summary>
	public static (long Usual, int Pct)? UsualLevel(IEnumerable<SetFinder.Row> cheapest, long? weekLow) {
		List<long> sets = [.. cheapest.Select(static r => r.Wallet ?? 0).Where(static w => w > 0).Order().Take(MarketShown)];

		return sets.Count > 0 ? ((sets[(sets.Count - 1) / 2] + sets[sets.Count / 2]) / 2, sets.Count >= MarketShown ? CapPct : CapPctFew)
			: weekLow is long low and > 0 ? (low, CapPctFew) : null;
	}

	/// <summary>1.8.2: the cheapest badge level priced this look, in the wallet's money (0: none priced) - "0.33 CAD a level".</summary>
	public long LevelPrice => Ranked.Count > 0 ? Ranked.Min(static s => s.Cost) : 0;

	/// <summary>1.8.2: with "Stop buying at Steam level", the badge levels still to buy once the sets held are crafted (-1: not stopping).</summary>
	public int LevelsToBuy { get; set; } = -1;

	/// <summary>1.8.2: the games in <see cref="Levels"/> counted at the set finder's price, not a card price known now.</summary>
	public HashSet<uint> Estimated { get; } = [];

	/// <summary>1.8.2: the look read as many prices as one look may - dearer levels than it priced wait for the next.</summary>
	public bool Capped { get; set; }

	/// <summary>1.8.2: the account is at the Steam level buying stops at ("Stop buying at Steam level").</summary>
	public bool LevelReached { get; set; }

	/// <summary>Why it couldn't look at all - empty when it could.</summary>
	public string Problem { get; set; } = "";

	/// <summary>The problem won't clear by itself (a setting, the spending list) - not worth asking again soon.</summary>
	public bool Lasting { get; set; }

	/// <summary>
	/// 1.8.2: the market stopped answering part way (or answered in another money), so <see cref="Ranked"/> may be missing
	/// levels - what money buys is not known for sure this look, and is never read as "it buys no level".
	/// </summary>
	public bool Short { get; set; }

	/// <summary>1.8.2: Steam said the account has no wallet at all - money from selling could never be spent.</summary>
	public bool NoWallet { get; set; }

	/// <summary>Why Steam won't let it buy by itself, or None: billing details first, or every purchase confirmed in a way it can't.</summary>
	public BuyBlock Block { get; set; }

	/// <summary><see cref="Block"/> in plain words (English, for the console). Empty when nothing blocks it.</summary>
	public string Blocked => Block switch {
		BuyBlock.Billing => "Steam wants billing details with each market purchase and none are saved on this account - buy one card by hand on the market once (that saves them), then it can buy",
		BuyBlock.Email => "Steam asks for every market purchase on this account to be confirmed by email, which nocat.farm can't do",
		BuyBlock.Unknown => "Steam didn't say how this account's market purchases are confirmed, so nothing is bought this time",
		_ => ""
	};

	/// <summary><see cref="Block"/> for the log, translated.</summary>
	public Said BlockedSaid => Block switch {
		BuyBlock.Billing => new Said("Steam wants billing details with each market purchase and none are saved on this account - buy one card by hand on the market once (that saves them), then it can buy"),
		BuyBlock.Email => new Said("Steam asks for every market purchase on this account to be confirmed by email, which nocat.farm can't do"),
		BuyBlock.Unknown => new Said("Steam didn't say how this account's market purchases are confirmed, so nothing is bought this time"),
		_ => new Said("")
	};

	public long Cost => Steps.Sum(static s => s.Cost);
	public int CardCount => Steps.Sum(static s => s.Cards.Count);

	/// <summary>
	/// Why Steam won't let it buy by itself, from the wallet page: billing details wanted and none saved to send (Steam's own
	/// market page stops there too), or every purchase confirmed by email - and anything it didn't say plainly (no answer, a
	/// type it doesn't know) buys nothing. Confirmed in the Steam app is fine: with the authenticator here it confirms its
	/// own; without, each purchase waits for the owner's phone.
	/// </summary>
	public static BuyBlock BlockOf(CardMarket.Wallet wallet) =>
		wallet.NeedsBilling && (wallet.Saved is not { Complete: true }) ? BuyBlock.Billing
			: wallet.Confirm switch {
				0 or 2 => BuyBlock.None,
				1 => BuyBlock.Email,
				_ => BuyBlock.Unknown
			};

	/// <summary>Market searches one plan may make for started badges the set finder doesn't know; whole sets the old way (no finder yet).</summary>
	internal const int MaxSetReads = 12, MaxFullSetGames = 3;

	/// <summary>1.8.2, an estimate: the most market searches and card prices it asks for - a handful, so it answers at once.</summary>
	public const int KnownOnlySearches = 2, KnownOnlyReads = 6;

	/// <summary>1.8.2, the preview: the most new card prices it reads - about half a minute (<see cref="MakeAsync"/>'s quick).</summary>
	public const int QuickReads = 16;

	/// <summary>1.8.2, the preview's note on what it didn't get to: "still checking: 4 more badge level(s) whose card prices it hasn't read yet ...".</summary>
	public static string QuickSaid(int levels, int badges) {
		List<string> what = [];

		if (levels > 0) {
			what.Add($"{levels} more badge level(s) whose card prices it hasn't read yet");
		}

		if (badges > 0) {
			what.Add($"{badges} started badge(s) whose sets it hasn't looked up yet");
		}

		return $"still checking: {string.Join(" and ", what)} - the next looks price them, and every card is priced again before it's bought";
	}

	/// <summary>1.8.2, the preview: badge levels it didn't price this time, and started badges whose sets it didn't look up - said in the notes.</summary>
	public int StillChecking { get; set; }

	/// <summary>
	/// Card prices one look reads, at least and at most: as many as the money left can use (about every card of the levels
	/// it could buy, and half as much again), never fewer than 30 nor more than 60 - inside the price book's hourly budget
	/// shared by every account (360 an hour).
	/// </summary>
	public const int MinBookReads = 30, MaxBookReads = 60;

	/// <summary>Levels priced at least before the money left stops the pricing - so what more money buys is known too.</summary>
	public const int MinPriced = 5;

	/// <summary>A card's price read for a plan is used again for this long: the price read right before buying is the real check.</summary>
	public static readonly TimeSpan PriceKept = TimeSpan.FromHours(2);

	/// <summary>The cheapest sets on the market the preview shows.</summary>
	internal const int MarketShown = 5;

	/// <summary>A normal game badge's top level.</summary>
	internal const int MaxLevel = 5;

	/// <summary>The most levels <see cref="Levels"/> holds.</summary>
	internal const int LevelsKept = 200;

	/// <summary>
	/// 1.8.2: card prices read lately for an account's plans (in the wallet's money), used again by the next look for
	/// <see cref="PriceKept"/> - and refreshed by every price read right before buying.
	/// </summary>
	public sealed class Prices {
		private readonly Dictionary<string, (CardMarket.Book Book, DateTime At)> _kept = new(StringComparer.Ordinal);
		private readonly Lock _gate = new();

		public void Remember(string hash, CardMarket.Book book, DateTime now) {
			lock (_gate) {
				_kept[hash] = (book, now);
			}
		}

		/// <summary>A price read in the last <see cref="PriceKept"/>, in this money - or null.</summary>
		public CardMarket.Book? Recent(string hash, int currency, DateTime now) {
			lock (_gate) {
				return _kept.TryGetValue(hash, out (CardMarket.Book Book, DateTime At) k) && (k.Book.Currency == currency) && (now - k.At < PriceKept) && (now >= k.At) ? k.Book : null;
			}
		}
	}

	/// <summary>Money as typed (1.50) in cents (150), clamped.</summary>
	public static long Cents(float money) => Math.Clamp(SpareRules.Cents(Math.Clamp(money, 0f, 1_000_000f)), 0, 100_000_000);

	/// <summary>"0.35 CAD".</summary>
	public static string Money(long cents, int currency) => Spare.Money(cents, currency);

	/// <summary>1.8.2: a day's or a week's limit set to "No limit" (below 0): far more than any wallet holds - the wallet is the limit.</summary>
	public const long NoLimit = long.MaxValue / 4;

	/// <summary>A day's or a week's limit in cents: <see cref="NoLimit"/> when it's set to no limit (below 0), else the amount.</summary>
	public static long Limit(float money) => money < 0 ? NoLimit : Cents(money);

	/// <summary>A limit as said: 1.50 CAD, or no limit (<see cref="Settings.NoLimitWord"/>).</summary>
	public static string LimitSaid(long cents, int currency) => cents >= NoLimit ? Settings.NoLimitWord : Money(cents, currency);

	/// <summary>"0.33 CAD a level (100 XP)" - empty when no level was priced.</summary>
	public string LevelPriceSaid => LevelPrice > 0 ? $"{Money(LevelPrice, Currency)} a level (100 XP)" : "";

	/// <summary>
	/// 1.8.2: the levels MORE money would buy, cheapest first - past the ones the wallet's balance already pays for. The
	/// cheapest levels are skipped while the wallet covers them; the first one it covers only part of is left at what's
	/// still missing; the rest as they are. Never the day's or the week's limits: they only hold the buying back a while.
	/// Empty when the wallet already pays for every level priced.
	/// </summary>
	public static List<long> BeyondWallet(IEnumerable<long> costs, long wallet) {
		List<long> more = [];
		long money = Math.Max(0, wallet);

		foreach (long cost in costs.Where(static c => c > 0).Order()) {
			if (money >= cost) {
				money -= cost;   // the wallet pays for this one already

				continue;
			}

			more.Add(cost - money);
			money = 0;
		}

		return more;
	}

	/// <summary>XP a Steam level takes from nothing: 100 for each of the first 10 levels, 200 each up to 20, and so on (level 100 is 55,000).</summary>
	public static long XpFor(int level) {
		long xp = 0;

		for (int n = 0; n < Math.Clamp(level, 0, 100_000); n++) {
			xp += 100L * ((n / 10) + 1);
		}

		return xp;
	}

	/// <summary>A level the plan could buy, before its prices are read: the set, from which level, the cards it needs, whole or not, and what the finder or the market's search says those cost in US cents (only to order them).</summary>
	private sealed record Candidate(CardSet Set, int Level, List<string> Missing, bool Full, long Usd, string? Skip = null, string? Note = null);

	/// <summary>
	/// The plan - smart levelling. Everything that could make a badge level, in one list: for each badge the account is
	/// levelling (cards of its set held, badge under level 5), the cards its next level still needs; and, with "Buy the
	/// cheapest full sets" on, whole sets for any game - the set finder's cheapest on the market (<see cref="SetFinder"/>),
	/// played or not. A game it holds even one card of is a started badge priced as its missing cards - never left out of the
	/// whole sets (live, 2026-10-19: Dota 2 at 0.36 and World of Warships at 0.43 were never priced because one card of each
	/// was held). All of it in the order of what the finder (or the market's search) says the next level costs, and priced
	/// in that order, in the wallet's money, until the money left is used or the look has read its prices - so a cheaper
	/// known level is never passed over for a dearer one. Then ranked by the real cost, cheapest first - the most XP for the
	/// money. A set is only ever planned whole: every missing card on the market, and all of it inside what may be spent now.
	/// One that costs more than a day's or a week's limit, or more than the most to pay for one level, is passed over (said
	/// why); one that doesn't fit what's left of today stops the plan there. Games automatic booster packs has a pack waiting
	/// for are left to the packs. Every cheaper one passed over is said why in <see cref="Skipped"/>.
	/// </summary>
	/// <param name="nowUtc">Now on Steam's clock - the limits' windows move with it, not with this PC's.</param>
	/// <param name="stopWhenBlocked">Stop once the wallet says Steam won't let it buy by itself - for a look that would buy.</param>
	/// <param name="boosting">Games automatic booster packs has a pack waiting to open for (<see cref="BoosterPick.Busy"/>): not bought.</param>
	/// <param name="heldCards">The cards held, from an inventory the caller has just read - not read again.</param>
	/// <param name="finder">Where the cheapest whole sets on the market come from: the app's set finder unless the checks give their own.</param>
	/// <param name="prices">Card prices read for this account's plans lately, used again (and kept): none unless the buyer gives its own.</param>
	/// <param name="dearWeighed">1.8.2: whether the dear-cards check weighed a game's cards lately (<see cref="DearCards.WeighedLately"/>):
	/// the account's own record when not given; with no account either, not asked.</param>
	/// <param name="goalBudget">1.8.2: a level goal's budget left - the most spent, whatever the day's and week's limits say.</param>
	/// <param name="knownOnly">1.8.2, an estimate ('levelup'): only prices already known - read for a plan in the last 2 hours,
	/// or saved from the market in the last day - and the set finder's; a handful of a started badge's missing cards priced at
	/// most. What isn't known is counted at the set finder's price (<see cref="Estimated"/>). Nothing is bought on it.</param>
	/// <param name="quick">1.8.2, the preview: answers in about a minute - prices known already (read lately, or saved from
	/// the market in the last day) and the set finder's sets, a couple of searches and <see cref="QuickReads"/> new card
	/// prices at most, two of the cheapest games' sets. What it didn't get to is said in the notes (<see cref="StillChecking"/>).</param>
	public static async Task<BuyPlan> MakeAsync(BotConfig cfg, ICardMarket market, ICardSwaps swaps, BuyLedger ledger, Bot? bot, DateTime nowUtc, CancellationToken ct,
		bool stopWhenBlocked = false, IReadOnlyCollection<uint>? boosting = null, IReadOnlyDictionary<uint, Dictionary<string, int>>? heldCards = null,
		SetFinder? finder = null, Prices? prices = null, Func<uint, bool>? dearWeighed = null, long? goalBudget = null, bool knownOnly = false, bool quick = false) {
		BuyPlan plan = new() {
			DayLimit = Limit(cfg.BuyDailyLimit),
			WeekLimit = Limit(cfg.BuyWeeklyLimit)
		};

		// A spending list that can't be read: nothing is bought, and no made-up number is shown for what was spent.
		if (ledger.Unreadable) {
			plan.Problem = ledger.UnreadableText;
			plan.Lasting = true;

			return plan;
		}

		plan.DaySpent = ledger.SpentSince(nowUtc.AddDays(-1));
		plan.WeekSpent = ledger.SpentSince(nowUtc.AddDays(-7));

		if (await market.WalletAsync(ct).ConfigureAwait(false) is not { } wallet) {
			plan.Problem = "Steam didn't say what's in its wallet";

			return plan;
		}

		plan.Currency = wallet.Currency;
		plan.Wallet = wallet.Balance;

		if (wallet.Currency <= 0) {
			plan.Problem = "it has no Steam wallet to buy with";
			plan.NoWallet = true;

			return plan;
		}

		plan.Block = BlockOf(wallet);

		if (stopWhenBlocked && (plan.Block != BuyBlock.None)) {
			return plan;
		}

		plan.Room = Math.Max(0, Math.Min(Math.Min(plan.DayLimit - plan.DaySpent, plan.WeekLimit - plan.WeekSpent), wallet.Balance));

		// 1.8.2: a level goal's own budget - what's left of it is the most this look spends.
		if (goalBudget is long goalLeft) {
			plan.Room = Math.Max(0, Math.Min(plan.Room, goalLeft));
		}

		if (await market.BadgeLevelsAsync(ct).ConfigureAwait(false) is not { } levels) {
			plan.Problem = "Steam wouldn't say what its badges are";

			return plan;
		}

		plan.BadgeLevels = levels;

		// "Stop buying at Steam level": there already, nothing more - on the way, only the levels still needed.
		long? levelsToGo = null;

		// 1.8.2: on, and the Steam level not known - nothing is bought on a guess this look.
		if ((cfg.BuyStopAtLevel > 0) && (market.SteamLevel == null)) {
			plan.Notes.Add($"its Steam level couldn't be read - with \"Stop buying at Steam level\" ({cfg.BuyStopAtLevel}) on, nothing is bought this look");
			plan.Skipped.Add("everything: its Steam level couldn't be read (\"Stop buying at Steam level\" is on)");
			plan.Short = true;

			return plan;
		}

		if ((cfg.BuyStopAtLevel > 0) && (market.SteamLevel is { } steam)) {
			if (steam.Level >= cfg.BuyStopAtLevel) {
				plan.LevelReached = true;
				plan.Notes.Add($"it's at Steam level {steam.Level} - buying stops at level {cfg.BuyStopAtLevel} (\"Stop buying at Steam level\")");

				return plan;
			}

			levelsToGo = Math.Max(1, (XpFor(cfg.BuyStopAtLevel) - steam.Xp + 99) / 100);
		}

		// The set finder's cheapest whole sets, for badges it can still level - on this PC's clock, like the finder's own.
		finder ??= SetFinder.Shared;
		IReadOnlyList<SetFinder.KnownSet> known = finder.Known(DateTime.UtcNow);
		Dictionary<uint, SetFinder.KnownSet> knownBy = known.ToDictionary(static k => k.App);
		plan.Market.AddRange(finder.Cheapest(levels, plan.Currency, MarketShown, DateTime.UtcNow));

		// The cards held: as the caller just read them (badge crafting), or read now.
		if ((heldCards ?? await market.CardsAsync(ct).ConfigureAwait(false)) is not { } held) {
			plan.Problem = "couldn't read all of its inventory";

			return plan;
		}

		plan.Held = held;

		long left = plan.Room;
		long most = Math.Min(plan.DayLimit, plan.WeekLimit);

		if (most <= 0) {
			plan.Notes.Add("a limit is 0, so it buys nothing");

			return plan;
		}

		long fixedCap = cfg.BuyMaxLevelPrice > 0 ? Cents(cfg.BuyMaxLevelPrice) : 0;
		string limitName = plan.DayLimit <= plan.WeekLimit ? "day's" : "week's";
		int books = 0, budget = MinBookReads;
		bool asking = false;   // 1.8.2, an estimate: whether the candidate being priced may ask the market at all
		bool stopped = false;
		Dictionary<string, CardMarket.Book> read = new(StringComparer.Ordinal);

		// The price of every card in a list: read now, or a price this account's plans read lately - null when the market
		// stopped answering, or this look has read enough prices (said in the notes either way).
		async Task<List<(string Hash, CardMarket.Book Book)>?> PricesAsync(IEnumerable<string> hashes) {
			List<(string, CardMarket.Book)> got = [];

			foreach (string hash in hashes) {
				if (!read.TryGetValue(hash, out CardMarket.Book? book)) {
					if (prices?.Recent(hash, plan.Currency, DateTime.UtcNow) is { } kept) {
						read[hash] = book = kept;
						got.Add((hash, book));

						continue;
					}

					// 1.8.2, an estimate: a price saved from the market in the last day will do - else, past a handful, not known.
					if (knownOnly || quick) {
						if ((MarketQuotes.Of(hash, plan.Currency) is { Lowest: int saved and > 0 } q) && (DateTime.UtcNow - q.When < TimeSpan.FromDays(1))) {
							read[hash] = book = new CardMarket.Book(saved, plan.Currency, Math.Max(2, q.Sales), [(saved, Math.Max(2, q.Sales))]);
							got.Add((hash, book));

							continue;
						}

						if (!asking || (books >= (quick ? QuickReads : KnownOnlyReads))) {
							return null;
						}
					}

					if (books >= budget) {
						plan.Notes.Add("that's enough prices for one look - the dearer levels wait for the next one");
						plan.Capped = true;

						return null;
					}

					books++;

					if (await market.BookAsync(hash, ct).ConfigureAwait(false) is not { } fresh) {
						plan.Notes.Add("the market stopped answering - the rest waits for the next look");
						plan.Short = true;

						return null;
					}

					// Prices in another currency than the wallet's are never compared with its limits.
					if ((fresh.Lowest > 0) && (fresh.Currency != plan.Currency)) {
						plan.Notes.Add($"the market answered in {MarketQuotes.Code(fresh.Currency)}, not the wallet's {MarketQuotes.Code(plan.Currency)} - nothing is planned on that");
						plan.Short = true;

						return null;
					}

					prices?.Remember(hash, fresh, DateTime.UtcNow);
					read[hash] = book = fresh;
				}

				got.Add((hash, book));
			}

			return got;
		}

		// Whether a set of this price can go in now: true to take it, false to pass it over, null to stop the plan here.
		bool? Fits(string game, long cost) {
			if (cost > most) {
				plan.Notes.Add($"{game}: the cards it needs cost {Money(cost, plan.Currency)}, more than a {limitName} limit ({Money(most, plan.Currency)})");
				plan.Skipped.Add($"{game}: {Money(cost, plan.Currency)} - more than a {limitName} limit");

				return false;
			}

			if (cost > left) {
				plan.Notes.Add($"{game} is next ({Money(cost, plan.Currency)}) - {(cost > wallet.Balance - (plan.Room - left) ? "the wallet doesn't hold enough" : "waiting for room in the day's or the week's limit")}");

				return null;
			}

			return true;
		}

		// ── everything that could make a level ──
		List<Candidate> candidates = [];
		HashSet<uint> packs = boosting is null ? [] : [.. boosting];
		HashSet<uint> sold = ledger.SoldSince(nowUtc - SetSale.NotBoughtFor);

		HashSet<uint> dear = ledger.DearSince(nowUtc - SetSale.NotBoughtFor);

		// 1.8.2: a card asked for in the last day that Steam hasn't said was bought - never asked for again meanwhile, and its
		// set waits for the answer (an unsure purchase may well have gone through).
		HashSet<string> pendingCards = ledger.PendingCards(nowUtc.AddDays(-1));

		// A game automatic booster packs has a pack waiting to open for: its cards come from the pack. A set it just sold
		// instead of crafting isn't bought back either, nor a game whose cards are worth more sold (1.8.2).
		(string Skip, string Note)? Busy(uint app, string game) =>
			dear.Contains(app) ? ($"{game}: its cards are worth more sold than the levels they'd make", $"{game}: its cards are worth more sold than the levels they'd make - not bought")
			: sold.Contains(app) ? ($"{game}: its set was just sold instead of crafted", $"{game}: its set was just sold instead of crafted - not bought back")
			: packs.Contains(app) ? ($"{game}: booster packs are working on it", $"{game}: booster packs are working on it - its cards come from the packs")
			: null;

		(string Skip, string Note)? Pending(string game, IEnumerable<string> cards) =>
			cards.FirstOrDefault(pendingCards.Contains) is { } asked
				? ($"{game}: {new BuyCard(asked, 0).Name} was asked for and Steam hasn't said yet whether it was bought", $"{game}: waiting to hear whether {new BuyCard(asked, 0).Name} was bought - not asked for again")
				: null;

		// 1.8.2, "Stop buying at Steam level": the levels the cards held make once crafted - every set held of each game, up to
		// its badge's level 5, a guess for a game whose set isn't known (as many as the fewest copies of what it holds, when it
		// holds 5 different cards or more), a whole foil set, and the cards asked for that Steam hasn't answered about.
		int heldLevels = 0;
		Dictionary<uint, Dictionary<string, int>> pendingBy = [];

		if (levelsToGo != null) {
			foreach (BuyLedger.Entry e in ledger.All.Where(e => e.Pending && (e.At >= nowUtc.AddDays(-1)) && (e.App != 0))) {
				Dictionary<string, int> cards = pendingBy.TryGetValue(e.App, out Dictionary<string, int>? had) ? had : pendingBy[e.App] = new(StringComparer.Ordinal);
				cards[e.Card] = cards.GetValueOrDefault(e.Card) + 1;
			}
		}

		void Counted(uint app, Dictionary<string, int> cards) {
			if (pendingBy.TryGetValue(app, out Dictionary<string, int>? more)) {
				foreach ((string hash, int n) in more) {
					cards[hash] = cards.GetValueOrDefault(hash) + n;
				}
			}
		}

		int Guess(uint app, Dictionary<string, int> cards, int level) {
			Dictionary<string, int> all = new(cards, StringComparer.Ordinal);
			Counted(app, all);
			List<int> copies = [.. all.Values.Where(static n => n > 0)];

			return copies.Count >= 5 ? Math.Max(0, Math.Min(copies.Min(), MaxLevel - level)) : 0;
		}

		// Badges it's levelling: the set from the finder when it knows it (no request), else the game's own search - the most
		// different cards held first, a few a look. A game left to the packs isn't searched at all.
		List<uint> levelling = [.. held.Where(h => (levels.GetValueOrDefault(h.Key) < MaxLevel) && h.Value.Values.Any(static n => n > 0))
			.OrderByDescending(static h => h.Value.Count(static c => c.Value > 0)).ThenBy(static h => h.Key).Select(static h => h.Key)];
		int searched = 0, unsearched = 0;
		List<uint> unknownStarted = [];

		foreach (uint app in levelling) {
			CardSet? set = knownBy.TryGetValue(app, out SetFinder.KnownSet? k) ? k.ToCardSet() : null;

			if (set == null) {
				if (Busy(app, GameNames.Of(app)) is { } busy) {
					plan.Skipped.Add(busy.Skip);
					plan.Notes.Add(busy.Note);
					heldLevels += Guess(app, held[app], levels.GetValueOrDefault(app));

					continue;
				}

				// 1.8.2, an estimate: a set read lately is used as it is; only a handful searched.
				if ((knownOnly || quick) && (LevelPlanner.KnownSet(app) is { Count: > 0 } usd)) {
					set = new CardSet(app, GameNames.Of(app), [.. usd.Keys.Order(StringComparer.Ordinal)], usd);
				}
			}

			if (set == null) {
				if (stopped || (searched >= (knownOnly || quick ? KnownOnlySearches : MaxSetReads))) {
					unsearched++;
					unknownStarted.Add(app);
					heldLevels += Guess(app, held[app], levels.GetValueOrDefault(app));

					continue;
				}

				searched++;

				if (await market.SetAsync(app, ct).ConfigureAwait(false) is not { } found) {
					plan.Notes.Add("the market stopped answering - the rest waits for the next look");
					stopped = true;
					plan.Short = true;
					heldLevels += Guess(app, held[app], levels.GetValueOrDefault(app));

					continue;
				}

				if (found.Cards.Count == 0) {
					plan.Skipped.Add($"{GameNames.Of(app)}: its set isn't known whole on the market");
					heldLevels += Guess(app, held[app], levels.GetValueOrDefault(app));

					continue;   // nothing is planned on half a set
				}

				set = found;
			}

			Dictionary<string, int> cards = held[app];
			int level = levels.GetValueOrDefault(app);
			int complete = set.Cards.All(c => cards.GetValueOrDefault(c) > 0) ? set.Cards.Min(c => cards.GetValueOrDefault(c)) : 0;

			// Every set held counts towards "Stop buying at Steam level", up to level 5 - and a card asked for that Steam hasn't answered about.
			if (levelsToGo != null) {
				Dictionary<string, int> counting = new(cards, StringComparer.Ordinal);
				Counted(app, counting);
				int sets = set.Cards.All(c => counting.GetValueOrDefault(c) > 0) ? set.Cards.Min(c => counting.GetValueOrDefault(c)) : 0;
				heldLevels += Math.Max(0, Math.Min(sets, MaxLevel - level));
			}

			// A whole set held: badge crafting's - or sold instead, when that buys more levels (SetSale).
			if ((complete > 0) && (level < MaxLevel)) {
				plan.Complete.Add((set, level));
			}

			// The sets held already take it to level 5 - nothing to buy.
			if (complete >= MaxLevel - level) {
				continue;
			}

			List<string> missing = [.. set.Cards.Where(c => cards.GetValueOrDefault(c) <= complete)];

			if ((missing.Count > 0) && (missing.Count < set.Cards.Count)) {
				(string Skip, string Note)? busy = Busy(app, set.Game) ?? Pending(set.Game, missing);
				candidates.Add(new Candidate(set, level + complete, missing, false, missing.Sum(h => (long) set.Usd.GetValueOrDefault(h)), busy?.Skip, busy?.Note));
			}
		}

		// A whole foil set held for a foil badge not made yet: one more level once crafted.
		if ((levelsToGo != null) && (await market.FoilLevelsAsync(ct).ConfigureAwait(false) is { } foilLevels)
			&& (await market.InventoryAsync(ct).ConfigureAwait(false) is { } foilInventory)) {
			foreach ((uint app, Dictionary<string, int> foil) in LevelPlanner.FoilCards(foilInventory)) {
				if (foilLevels.GetValueOrDefault(app) >= 1) {
					continue;
				}

				int different = foil.Count(static f => f.Value > 0);
				bool whole = knownBy.TryGetValue(app, out SetFinder.KnownSet? k) ? k.Cards.All(c => foil.GetValueOrDefault(SetSale.FoilHash(c)) > 0) : different >= 5;
				heldLevels += whole ? 1 : 0;
			}
		}

		finder.Want(unknownStarted);   // 1.8.3: the set finder reads these first

		if (unsearched > 0) {
			plan.Skipped.Add($"{unsearched} more started badge(s) whose sets the set finder doesn't know yet - looked at as it learns them");
		}

		HashSet<uint> started = [.. levelling];

		if (cfg.BuyFullSets && !stopped && (known.Count > 0)) {
			// The set finder's: whole sets for any game - played, owned or not. A game with cards held is a started badge, above.
			foreach (SetFinder.KnownSet k in known) {
				if (started.Contains(k.App) || (held.TryGetValue(k.App, out Dictionary<string, int>? some) && some.Values.Any(static n => n > 0))) {
					continue;
				}

				CardSet set = k.ToCardSet();
				string? skip = levels.GetValueOrDefault(k.App) >= MaxLevel ? $"{k.Game}: its badge is at level 5"
					: k.Depth < 2 ? $"{k.Game}: a card of it has fewer than 2 on the market"
					: null;
				(string Skip, string Note)? busy = skip == null ? Busy(k.App, k.Game) ?? Pending(k.Game, set.Cards) : null;
				candidates.Add(new Candidate(set, levels.GetValueOrDefault(k.App), [.. set.Cards], true, k.UsdCents, skip ?? busy?.Skip, busy?.Note));
			}
		} else if (cfg.BuyFullSets && !stopped && !knownOnly) {
			// The set finder hasn't a whole set yet (it has only just started): the games of the cheapest single cards.
			if (await market.CheapestGamesAsync(ct).ConfigureAwait(false) is not { } cheapest) {
				plan.Notes.Add("the market didn't say which sets are cheapest - full sets wait for the next look");
				plan.Short = true;
			} else {
				List<CardSet> found = [];
				int looked = 0;

				foreach (uint app in cheapest) {
					// Games it holds cards of are the missing cards' business, above; a maxed badge has nothing to give.
					// 1.8.2, the preview: a couple of sets searched, not a dozen.
					if ((looked >= (quick ? KnownOnlySearches : MaxSetReads)) || (levels.GetValueOrDefault(app) >= MaxLevel) || started.Contains(app)
						|| (held.TryGetValue(app, out Dictionary<string, int>? some) && some.Values.Any(static n => n > 0))) {
						continue;
					}

					if (Busy(app, GameNames.Of(app)) is { } busy) {
						plan.Skipped.Add(busy.Skip);

						continue;
					}

					looked++;

					if (await market.SetAsync(app, ct).ConfigureAwait(false) is not { } set) {
						plan.Short = true;

						break;
					}

					// Every card listed at the last search: a set with a card nobody sells can't be bought whole.
					if ((set.Cards.Count > 0) && set.Cards.All(c => set.Usd.GetValueOrDefault(c) > 0)) {
						found.Add(set);
					}
				}

				// The few cheapest by the search, to be priced in the wallet's own money.
				candidates.AddRange(found.OrderBy(static s => s.UsdCents).ThenBy(static s => s.App).Take(MaxFullSetGames)
					.Select(s => Pending(s.Game, s.Cards) is { } waiting
						? new Candidate(s, levels.GetValueOrDefault(s.App), [.. s.Cards], true, s.UsdCents, waiting.Skip, waiting.Note)
						: new Candidate(s, levels.GetValueOrDefault(s.App), [.. s.Cards], true, s.UsdCents)));
			}
		}

		// ── each priced now, in the order of what the finder or the search says the level costs (US cents - only to order them) ──
		double? rate = WalletRate.ForDeciding(plan.Currency);
		long? Wallet(long usd) => rate is double r ? WalletRate.ToWallet((int) Math.Min(usd, int.MaxValue), r) : null;
		List<Candidate> order = [.. candidates.OrderBy(static c => c.Usd).ThenBy(static c => c.Full).ThenBy(static c => c.Set.App)];

		// As many prices as the money left can use: every card of the levels it could buy, and half as much again.
		long room = Math.Max(plan.Room, 1) * 3 / 2, planned = 0;
		int wanted = 0;

		foreach (Candidate c in order.Where(static c => c.Skip == null)) {
			if (planned >= room) {
				break;
			}

			planned += Wallet(c.Usd) ?? c.Usd;
			wanted += c.Missing.Count;
		}

		budget = Math.Clamp(wanted + 5, MinBookReads, MaxBookReads);

		List<BuyStep> priced = [];
		List<Candidate> unpriced = [];
		long fitting = 0, anchorLow = long.MaxValue;
		bool saidDear = false, pricing = true;

		// 1.8.2, the automatic most to pay for one level: tight - a quarter over what a level usually costs (the middle of the
		// set finder's 5 cheapest whole sets; half over with fewer known, or the cheapest level priced over the last week) - a
		// number that doesn't climb as the cheap sets run out, nor drop to a few cents for a started badge one cheap card
		// short. Neither known: half over this look's cheapest whole set (or started badge missing 2 cards or more).
		(long Usual, int Pct)? usual = fixedCap > 0 ? null : UsualLevel(plan.Market, ledger.WeekLow(plan.Currency, nowUtc));
		long CapNow() => fixedCap > 0 ? fixedCap : usual is { } u ? u.Usual * u.Pct / 100 : anchorLow < long.MaxValue ? anchorLow * CapPctFew / 100 : long.MaxValue;

		// 1.8.2: a started badge whose cards held may be worth more sold than the levels they'd make waits for the dear-cards
		// check to weigh it (2 games a look) - buying its missing cards first would throw that money into one level.
		dearWeighed ??= bot == null ? null : app => DearCards.WeighedLately(bot.Name, app, nowUtc);
		bool weighsDear = DearCards.Applies(cfg, buying: true) && (dearWeighed != null);
		IReadOnlyCollection<uint> keepGames = SpareRules.From(cfg).KeepGames;
		double? rateKnown = WalletRate.ForDeciding(plan.Currency);

		foreach (Candidate c in order) {
			CardSet set = c.Set;

			if (!pricing) {
				if (c.Skip == null) {
					unpriced.Add(c);
				}

				continue;
			}

			if (c.Skip != null) {
				plan.Skipped.Add(c.Skip);

				if (c.Note != null) {
					plan.Notes.Add(c.Note);
				}

				continue;
			}

			// The money left is used by levels priced already (and enough are priced to know what more money buys): the rest
			// - dearer by what the finder says - waits.
			if ((fitting >= plan.Room) && (priced.Count >= MinPriced)) {
				pricing = false;
				unpriced.Add(c);

				continue;
			}

			// A whole set: once one costs more than a day's or a week's limit by the finder's price, so does every one after it.
			if (c.Full && (Wallet(c.Usd) is long about) && (about > most)) {
				if (!saidDear && !priced.Any(static s => s.FullSet)) {
					plan.Notes.Add($"the cheapest whole set on the market ({set.Game}, about {Money(about, plan.Currency)}) costs more than a {limitName} limit ({Money(most, plan.Currency)})");
				}

				saidDear = true;
				plan.Skipped.Add($"{set.Game}: about {Money(about, plan.Currency)} - more than a {limitName} limit");

				continue;
			}

			// Dearer by the finder's price than the most to pay for one level, by a good margin (the rate is only learned).
			if ((CapNow() is long capBefore and < long.MaxValue) && ((fixedCap > 0) || (usual != null)) && (Wallet(c.Usd) is long guess) && (guess * 4 > capBefore * 5)) {
				plan.Skipped.Add($"{set.Game}: about {Money(guess, plan.Currency)} a level - more than the most to pay for one ({Money(capBefore, plan.Currency)})");
				unpriced.Add(c);

				continue;
			}

			asking = quick || !c.Full;

			if (await PricesAsync(c.Missing).ConfigureAwait(false) is not { } got) {
				// 1.8.2, an estimate: a level whose prices aren't known is counted at the set finder's price - the rest go on.
				if ((knownOnly || quick) && !plan.Short) {
					unpriced.Add(c);
					plan.StillChecking++;

					continue;
				}

				pricing = false;

				continue;
			}

			if (c.Full) {
				if (got.FirstOrDefault(p => p.Book.Listings < 2) is { Hash: not null } thin) {
					plan.Notes.Add($"{set.Game}: left out - {new BuyCard(thin.Hash, 0).Name} has fewer than 2 listings");
					plan.Skipped.Add($"{set.Game}: {new BuyCard(thin.Hash, 0).Name} has fewer than 2 listings");

					continue;
				}

				if (got.FirstOrDefault(p => p.Book.Dumped) is { Hash: not null } dumped) {
					plan.Notes.Add($"{set.Game}: left out - one cheap listing of {new BuyCard(dumped.Hash, 0).Name} makes the set look cheaper than it is");
					plan.Skipped.Add($"{set.Game}: one cheap listing of {new BuyCard(dumped.Hash, 0).Name} makes it look cheaper than it is");

					continue;
				}
			}

			if (got.FirstOrDefault(p => p.Book.Lowest <= 0) is { Hash: not null } none) {
				plan.Notes.Add($"{set.Game}: nobody is selling {new BuyCard(none.Hash, 0).Name} right now");
				plan.Skipped.Add($"{set.Game}: nobody is selling {new BuyCard(none.Hash, 0).Name}");

				continue;
			}

			if (!c.Full) {
				// A card a swap would bring for less waits for the swap - and so does its set: never half a set.
				List<string> swapped = [];

				foreach ((string hash, CardMarket.Book book) in got) {
					if ((await swaps.SwapCostAsync(bot, set.App, hash, ct).ConfigureAwait(false) is long swap) && (swap < book.Lowest)) {
						swapped.Add(new BuyCard(hash, 0).Name);
					}
				}

				if (swapped.Count > 0) {
					plan.Notes.Add($"{set.Game}: waiting for a card swap for {string.Join(", ", swapped)} - cheaper than buying");
					plan.Skipped.Add($"{set.Game}: waiting for a card swap");

					continue;
				}
			}

			// Each card with the tax on its fee, as the purchase will pay it - so the ranking and what fits today are what it costs.
			BuyStep step = new(set.App, set.Game, c.Full, c.Level, set.Cards.Count, [.. got.Select(p => new BuyCard(p.Hash, p.Book.Lowest, wallet.FeeTax(p.Book.Lowest)))]);
			long before = Wallet(c.Usd) ?? 0;

			// 1.8.2: half as dear again as the set finder said - not planned on this look's one price (it waits for the next look).
			if ((before > 0) && (step.Cost * 2 > before * 3)) {
				plan.Skipped.Add($"{set.Game}: about {Money(before, plan.Currency)} by the set finder, {Money(step.Cost, plan.Currency)} now - its price moved");

				continue;
			}

			// 1.8.2: a started badge whose cards held look worth more than twice its next level - left until the dear-cards check
			// has weighed them (a day at most, then it's bought as any).
			if (!c.Full && weighsDear && !keepGames.Contains(set.App) && !dearWeighed!(set.App) && held.TryGetValue(set.App, out Dictionary<string, int>? mine)
				&& (DearCards.HeldWorth(mine, plan.Currency, rateKnown, set) is long worth) && (worth > step.Cost * 2) && DearCards.WaitForWeighing(bot?.Name ?? "", set.App, nowUtc)) {
				plan.Skipped.Add($"{set.Game}: the cards it holds may be worth more sold ({Money(worth, plan.Currency)}) than this level costs ({Money(step.Cost, plan.Currency)}) - waiting until they're weighed");

				continue;
			}

			priced.Add(step);

			// A whole set, or a started badge missing 2 cards or more - never one cheap card short - is what a level costs.
			if (step.FullSet || (step.Cards.Count >= 2)) {
				anchorLow = Math.Min(anchorLow, step.Cost);
			}

			if ((step.Cost <= most) && (step.Cost <= CapNow())) {
				fitting += step.Cost;
			}
		}

		// What a level cost this look, for the next ones' automatic most per level (kept a week).
		if (anchorLow < long.MaxValue) {
			ledger.NoteLow(anchorLow, plan.Currency, nowUtc);
		}

		// Enough prices read for one look, and not one level priced: what money buys isn't known at all this look.
		if (plan.Capped && (priced.Count == 0)) {
			plan.Short = true;
		}

		// ── smart levelling: whatever makes the next badge level cheapest goes first - the most XP for the money ──
		// A started badge one card short goes after a whole new set that costs less; at the same price, the started badge.
		plan.Ranked.AddRange(priced.OrderBy(static s => s.Cost).ThenBy(static s => s.FullSet).ThenBy(static s => s.App));

		// The most to pay for one level: the setting, or twice what a level usually costs (1.8.2 - see above).
		plan.MaxLevelAuto = fixedCap <= 0;

		if (plan.MaxLevelAuto) {
			usual ??= ledger.WeekLow(plan.Currency, nowUtc) is long low ? (low, CapPctFew) : plan.LevelPrice > 0 ? (plan.LevelPrice, CapPctFew) : null;
			plan.UsualLevelPrice = usual?.Usual ?? 0;
		}

		plan.MaxLevelPrice = fixedCap > 0 ? fixedCap : usual is { } cap ? cap.Usual * cap.Pct / 100 : 0;
		string capSaid = plan.MaxLevelAuto ? $"{Money(plan.MaxLevelPrice, plan.Currency)}, a little over what a level usually costs ({Money(usual?.Usual ?? 0, plan.Currency)})" : Money(plan.MaxLevelPrice, plan.Currency);

		// 1.8.2, warming up: until the set finder has checked the cheapest sets the lists name, a whole set is bought only at
		// most a tenth over the cheapest level known - a small pool of known sets never makes a dear one look cheapest.
		long warmCap = 0;

		if (finder.WarmUp(DateTime.UtcNow) is { } warm && (warm.Checked < warm.Needed)) {
			long cheapestKnown = Math.Min(plan.Ranked.Where(static r => r.FullSet && (r.Cost > 0)).Select(static r => r.Cost).DefaultIfEmpty(long.MaxValue).Min(),
				plan.Market.Select(static r => r.Wallet ?? long.MaxValue).Where(static w => w > 0).DefaultIfEmpty(long.MaxValue).Min());   // whole sets only: a started badge two cards short isn't what a set costs
			warmCap = cheapestKnown < long.MaxValue ? cheapestKnown * WarmUpPct / 100 : 1;
			plan.WarmUpChecked = warm.Checked;
			plan.WarmUpNeeded = warm.Needed;
			plan.WarmUp = $"buying waits for the set finder: {warm.Checked} of {warm.Needed} cheapest sets checked - until then only whole sets up to {Money(warmCap, plan.Currency)} (the cheapest known, and a tenth more)";
			plan.Notes.Add(plan.WarmUp);
		}
		bool saidCap = false;

		foreach (BuyStep step in plan.Ranked) {
			if ((levelsToGo is long togo) && (plan.Steps.Count >= togo - heldLevels)) {
				plan.Notes.Add(togo <= heldLevels
					? $"the sets it holds reach Steam level {cfg.BuyStopAtLevel} once crafted - buying stops there (\"Stop buying at Steam level\")"
					: $"{togo - heldLevels} more badge level(s) reach Steam level {cfg.BuyStopAtLevel} - buying stops there (\"Stop buying at Steam level\")");

				break;
			}

			if (step.FullSet && (warmCap > 0) && (step.Cost > warmCap)) {
				plan.Skipped.Add($"{step.Game}: {Money(step.Cost, plan.Currency)} - the set finder is still checking the cheapest sets");

				continue;
			}

			if ((plan.MaxLevelPrice > 0) && (step.Cost > plan.MaxLevelPrice)) {
				plan.Skipped.Add($"{step.Game}: {Money(step.Cost, plan.Currency)} a level - more than the most to pay for one ({capSaid})");

				if (!saidCap) {
					saidCap = true;
					plan.Notes.Add($"levels dearer than {capSaid} wait for prices to come down (\"Most to pay for one badge level\")");
				}

				continue;
			}

			if (Fits(step.Game, step.Cost) is not { } fits) {
				break;
			}

			if (fits) {
				plan.Steps.Add(step);
				left -= step.Cost;
			}
		}

		// What each next level costs, for what money from gems or a set sold buys: the levels priced, then the finder's
		// cheapest known past them at the learned rate. 1.8.2: whatever the most to pay for one level says - that only holds
		// buying back a while, and leaving the dear ones out made money look like it buys cheap levels without end. Never
		// one dearer than a day's or a week's limit (it could never be bought), nor past the Steam level buying stops at.
		foreach (BuyStep s in plan.Ranked.Where(s => s.Cost <= most)) {
			plan.Levels.Add((s.App, s.Cost));
		}

		foreach (Candidate c in unpriced) {
			if ((Wallet(c.Usd) is long est) && (est > 0) && (est <= most) && !plan.Levels.Any(l => l.App == c.Set.App)) {
				plan.Levels.Add((c.Set.App, est));
				plan.Estimated.Add(c.Set.App);
			}
		}

		List<(uint App, long Cost)> sorted = [.. plan.Levels.OrderBy(static l => l.Cost).ThenBy(static l => l.App).Take(levelsToGo is long most2 ? (int) Math.Clamp(most2 - heldLevels, 0, LevelsKept) : LevelsKept)];
		plan.LevelsToBuy = levelsToGo is long most3 ? (int) Math.Clamp(most3 - heldLevels, 0, int.MaxValue) : -1;
		plan.Levels.Clear();
		plan.Levels.AddRange(sorted);
		plan.LevelsKnown = true;

		// 1.8.2, the preview: what it didn't get to this time, said plainly.
		if (quick && ((plan.StillChecking > 0) || (unsearched > 0))) {
			plan.Notes.Add(QuickSaid(plan.StillChecking, unsearched));
		}

		if ((plan.Steps.Count == 0) && (plan.Notes.Count == 0)) {
			plan.Notes.Add(candidates.Count == 0 && !cfg.BuyFullSets
				? "no badge it's levelling is missing cards it can buy"
				: "nothing to buy right now");
		}

		return plan;
	}
}

/// <summary>
/// What buying cards has spent, per account - for the day's and the week's limits, and so they survive a restart - and
/// whether the owner has turned it on (its go-ahead). Both in config/state/buy-&lt;account&gt;.json, beside the account's
/// spending, never in its settings: a settings file or a backup can't bring the go-ahead with it.
/// </summary>
public sealed class BuyLedger(string path) {
	/// <param name="At">When, on Steam's clock.</param>
	/// <param name="Pending">Not known yet whether it was bought: counted as spent, at the order's price, until a look finds out.</param>
	/// <param name="Order">The buy order's id, once Steam gave one - the only orders a look ever takes back.</param>
	/// <param name="Key">This purchase's own name in the list, so it's updated in place.</param>
	/// <param name="Confirm">
	/// The Steam app confirmation it waits on (no order yet): the owner confirms it on the phone, and the next look sends the
	/// order again with it - as Steam's market page does. Never sent again once it's a day old.
	/// </param>
	/// <param name="Tax">The tax on the market fee in <paramref name="Cents"/> - the order's price is the rest.</param>
	/// <param name="Before">1.8.2: the ids of the copies of this card held before it was ordered (null: not known) - a new one
	/// arriving after the order proves it was bought, when Steam won't say.</param>
	/// <param name="Asset">1.8.2: the copy that proved it was bought - never proof for another purchase.</param>
	public sealed record Entry(DateTime At, long Cents, int Currency, uint App, string Card, bool Pending = false, string? Order = null, string? Key = null,
		string? Confirm = null, long Tax = 0, string[]? Before = null, string? Asset = null) {
		/// <summary>Waiting for the owner to confirm it in the Steam app: no order yet, a confirmation to send it with.</summary>
		public bool OnPhone => Pending && string.IsNullOrEmpty(Order) && !string.IsNullOrEmpty(Confirm);
	}

	/// <summary>A set sold instead of crafted (<see cref="SetSale"/>): its cards aren't bought back for a while.</summary>
	/// <param name="Dear">1.8.2: not a set - the game's cards were worth more sold than the levels they'd make (<see cref="DearCards"/>).
	/// Written when that's decided, so buying and booster packs leave the game alone while its cards are sold.</param>
	/// <param name="Weighed">1.8.2: what each card of the set was weighed at (what the account gets, by market name) - what's left
	/// of it is never sold for over a tenth less (<see cref="DearCards"/>). Null: not known (written before this was kept).</param>
	public sealed record SoldSet(DateTime At, uint App, bool Dear = false, Dictionary<string, int>? Weighed = null);

	/// <summary>
	/// 1.8.2: a game's cards (its foil ones, <paramref name="Foil"/>) waiting since then to be sold - a card of them hardly
	/// sells. After <see cref="SetSale.WaitAtMost"/> they're listed all the same: a slow sale beats one that never happens.
	/// </summary>
	/// <param name="Source">1.8.2: whose wait it is - a set sold instead of crafted (<see cref="SetSource"/>) or a dear game's cards
	/// (<see cref="DearSource"/>): the two never share one "waiting since". Null: written before that - dropped when read.</param>
	public sealed record Waiting(DateTime Since, uint App, bool Foil = false, string? Source = null);

	/// <summary>1.8.2: a set sold instead of crafted waits (<see cref="SetSale"/>).</summary>
	public const string SetSource = "set";

	/// <summary>1.8.2: a dear game's cards wait (<see cref="DearCards"/>).</summary>
	public const string DearSource = "dear";

	/// <summary>1.8.2: a game's normal badge crafted then - the cards bought for it are no longer protected (<see cref="BoughtSince"/>).</summary>
	public sealed record Craft(DateTime At, uint App);

	/// <summary>1.8.2: the cheapest badge level a look priced (whole sets, or a started badge missing 2 cards or more), in that money.</summary>
	public sealed record Low(DateTime At, long Cents, int Currency);

	/// <summary>
	/// 1.8.2, a level goal ('levelup &lt;account&gt; &lt;level&gt; buy yes'): buy the cheapest badge levels up to Steam level
	/// <paramref name="Target"/> - <paramref name="Levels"/> of them, for about <paramref name="Total"/> as previewed (never
	/// over a tenth more, <see cref="Budget"/>), in that money - from <paramref name="Started"/>, when the account had
	/// <paramref name="StartXp"/> XP (<paramref name="LastXp"/>: as last read).
	/// </summary>
	public sealed record LevelGoal(int Target, int Levels, long Total, int Currency, DateTime Started, int StartXp, int LastXp = 0) {
		/// <summary>The most the goal may spend: what was previewed and a tenth more.</summary>
		public long Budget => Total * 11 / 10;

		/// <summary>Badge levels gained since it started, by the XP read.</summary>
		public int Done => Math.Clamp((Math.Max(LastXp, StartXp) - StartXp) / 100, 0, Math.Max(0, Levels));
	}

	internal sealed class Stored {
		public bool Ok { get; set; }
		public List<Entry> Entries { get; set; } = [];
		public List<SoldSet> Sold { get; set; } = [];
		public List<Waiting> Waits { get; set; } = [];
		public List<Craft> Crafts { get; set; } = [];
		public List<Low> Lows { get; set; } = [];

		/// <summary>1.8.2: whether the day's / the week's limit was "No limit" when buying got its go-ahead (null: not known).</summary>
		public bool? OkDayNoLimit { get; set; }
		public bool? OkWeekNoLimit { get; set; }

		/// <summary>1.8.2: the level goal under way - null when none.</summary>
		public LevelGoal? Goal { get; set; }
	}

	private List<Entry>? _entries;
	private List<SoldSet> _sold = [];
	private List<Waiting> _waits = [];
	private List<Craft> _crafts = [];
	private List<Low> _lows = [];
	private bool? _okDayNoLimit, _okWeekNoLimit;
	private LevelGoal? _goal;

	/// <summary>1.8.2: the level goal under way ('levelup &lt;account&gt; &lt;level&gt; buy yes') - null when none.</summary>
	public LevelGoal? Goal {
		get {
			lock (_gate) {
				Load();

				return _goal;
			}
		}
	}

	/// <summary>1.8.2: a level goal started, changed or (null) ended. False when it couldn't be saved.</summary>
	public bool SetGoal(LevelGoal? goal) {
		lock (_gate) {
			Load();
			_goal = goal;

			return Save();
		}
	}
	private bool _ok, _onDisk, _unreadable;
	private readonly Lock _gate = new();

	private static readonly Dictionary<string, BuyLedger> Loaded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>One per account for the whole program, so 'buy on' typed while a look runs isn't saved over.</summary>
	public static BuyLedger Of(string account) {
		string file = Path.Combine(ConfigStore.ConfigDir, "state", $"buy-{account}.json");

		lock (Loaded) {
			if (Loaded.TryGetValue(file, out BuyLedger? held) && (!held._onDisk || File.Exists(file))) {
				return held;
			}

			BuyLedger fresh = new(file);
			Loaded[file] = fresh;

			return fresh;
		}
	}

	/// <summary>As if the program had just started: every ledger is read from its file again.</summary>
	public static void ForgetLoaded() {
		lock (Loaded) {
			Loaded.Clear();
		}
	}

	/// <summary>Whether buying has its go-ahead: turned on through its preview ('buy on', or the switch).</summary>
	public bool Ok {
		get {
			lock (_gate) {
				Load();

				return _ok;
			}
		}
	}

	/// <summary>
	/// The file is there and can't be read. Nothing is bought - what was spent isn't known - and the file is never written
	/// over, so it can be looked at; fixed or deleted, the next look reads it again.
	/// </summary>
	public bool Unreadable {
		get {
			lock (_gate) {
				Load();

				return _unreadable;
			}
		}
	}

	/// <summary><see cref="Unreadable"/> in plain words (English, for the console).</summary>
	public string UnreadableText => $"the list of what it spent on cards (config/state/{Path.GetFileName(path)}) can't be read, so it buys nothing until that file is fixed or deleted";

	public void SetOk(bool ok) {
		lock (_gate) {
			Load();
			_ok = ok;
			Save();
		}
	}

	/// <summary>
	/// 1.8.2: the go-ahead given (or taken back) with the limits as they are now - a limit turned to "No limit" later needs the
	/// preview and the go-ahead again (<see cref="NoLimitSince"/>).
	/// </summary>
	public void SetOk(bool ok, float dayLimit, float weekLimit) {
		lock (_gate) {
			Load();
			_ok = ok;
			_okDayNoLimit = ok ? dayLimit < 0 : null;
			_okWeekNoLimit = ok ? weekLimit < 0 : null;
			Save();
		}
	}

	/// <summary>
	/// 1.8.2: a spend limit is "No limit" now that wasn't when buying got its go-ahead - the preview and the go-ahead are needed
	/// again. A go-ahead from before this was kept takes the limits as they are now.
	/// </summary>
	public bool NoLimitSince(float dayLimit, float weekLimit) {
		lock (_gate) {
			Load();

			if (!_ok) {
				return false;
			}

			if ((_okDayNoLimit == null) || (_okWeekNoLimit == null)) {
				_okDayNoLimit ??= dayLimit < 0;
				_okWeekNoLimit ??= weekLimit < 0;
				Save();

				return false;
			}

			return ((dayLimit < 0) && (_okDayNoLimit == false)) || ((weekLimit < 0) && (_okWeekNoLimit == false));
		}
	}

	/// <summary>What was spent since then, pending purchases included. An unreadable list counts as everything spent.</summary>
	public long SpentSince(DateTime utc) {
		lock (_gate) {
			List<Entry> all = Load();

			return _unreadable ? long.MaxValue / 4 : all.Where(e => e.At >= utc).Sum(static e => e.Cents);
		}
	}

	/// <summary>The games it bought cards of since then (a purchase Steam didn't answer about too) - their cards stay for the craft.</summary>
	public HashSet<uint> GamesSince(DateTime utc) {
		lock (_gate) {
			return [.. Load().Where(e => (e.At >= utc) && (e.App != 0)).Select(static e => e.App)];
		}
	}

	/// <summary>
	/// 1.8.2: the games it bought cards of since then whose badge hasn't been crafted since the last purchase - their cards
	/// stay for the craft (<see cref="CardExchange.BoughtGames"/>).
	/// </summary>
	public HashSet<uint> BoughtSince(DateTime utc) {
		lock (_gate) {
			List<Entry> all = Load();
			Dictionary<uint, DateTime> crafted = [];

			foreach (Craft c in _crafts) {
				crafted[c.App] = crafted.TryGetValue(c.App, out DateTime was) && (was > c.At) ? was : c.At;
			}

			return [.. all.Where(e => (e.At >= utc) && (e.App != 0)).GroupBy(static e => e.App)
				.Where(g => !crafted.TryGetValue(g.Key, out DateTime at) || (g.Max(static e => e.At) > at)).Select(static g => g.Key)];
		}
	}

	/// <summary>1.8.2: a game's normal badge crafted now - the cards bought for it are spent. False when it couldn't be saved.</summary>
	public bool Crafted(uint app, DateTime now) {
		lock (_gate) {
			Load();
			_crafts.RemoveAll(c => (c.App == app) || (now - c.At > TimeSpan.FromDays(60)));
			_crafts.Add(new Craft(now, app));

			return Save();
		}
	}

	/// <summary>1.8.2: the normal badges crafted lately (the latest of each game) - see <see cref="Crafted"/>.</summary>
	public IReadOnlyList<Craft> Crafts {
		get {
			lock (_gate) {
				Load();

				return [.. _crafts];
			}
		}
	}

	/// <summary>1.8.2: the most it paid for one copy of a card (bought, not just asked for), in that money - 0 when it bought none.</summary>
	public long PaidFor(string hash, int currency) {
		lock (_gate) {
			return Load().Where(e => !e.Pending && (e.Card == hash) && (e.Currency == currency)).Select(static e => e.Cents).DefaultIfEmpty(0).Max();
		}
	}

	/// <summary>1.8.2: the copies of a card it bought (not just asked for) since then.</summary>
	public int BoughtCopies(string hash, DateTime utc) {
		lock (_gate) {
			return Load().Count(e => !e.Pending && (e.Card == hash) && (e.At >= utc));
		}
	}

	/// <summary>1.8.2: the cards with a purchase not known yet, asked for since then - never asked for again meanwhile.</summary>
	public HashSet<string> PendingCards(DateTime utc) {
		lock (_gate) {
			return new(Load().Where(e => e.Pending && (e.At >= utc)).Select(static e => e.Card), StringComparer.Ordinal);
		}
	}

	/// <summary>1.8.2: the cheapest level a look priced, written down (kept a week) - what the automatic most per level goes by when nothing else is known.</summary>
	public void NoteLow(long cents, int currency, DateTime now) {
		if ((cents <= 0) || (currency <= 0)) {
			return;
		}

		lock (_gate) {
			Load();

			// One a day is plenty: kept only when it's the day's first, or lower than the day's.
			if (_lows.Any(l => (l.Currency == currency) && (l.At.Date == now.Date) && (l.Cents <= cents))) {
				return;
			}

			_lows.RemoveAll(l => (now - l.At > LowsFor) || ((l.Currency == currency) && (l.At.Date == now.Date)));
			_lows.Add(new Low(now, cents, currency));
			Save();
		}
	}

	/// <summary>1.8.2: the cheapest level priced over the last week, in this money - null when none was.</summary>
	public long? WeekLow(int currency, DateTime now) {
		lock (_gate) {
			Load();

			return _lows.Where(l => (l.Currency == currency) && (now - l.At <= LowsFor) && (l.Cents > 0)).Select(static l => (long?) l.Cents).Min();
		}
	}

	/// <summary>How long the cheapest levels priced are kept.</summary>
	public static readonly TimeSpan LowsFor = TimeSpan.FromDays(7);

	public IReadOnlyList<Entry> All {
		get {
			lock (_gate) {
				return [.. Load()];
			}
		}
	}

	/// <summary>Purchases still not known, with an order id to ask about - the oldest first.</summary>
	public IReadOnlyList<Entry> OpenOrders {
		get {
			lock (_gate) {
				return [.. Load().Where(static e => e.Pending && !string.IsNullOrEmpty(e.Order)).OrderBy(static e => e.At)];
			}
		}
	}

	/// <summary>
	/// 1.8.2: purchases sent with no clear answer and no order id - an order may have gone in all the same, and only the
	/// market's list of open buy orders can find it. The oldest first.
	/// </summary>
	public IReadOnlyList<Entry> Unplaced {
		get {
			lock (_gate) {
				return [.. Load().Where(static e => e.Pending && string.IsNullOrEmpty(e.Order) && string.IsNullOrEmpty(e.Confirm) && (e.Asset == null)).OrderBy(static e => e.At)];
			}
		}
	}

	/// <summary>Purchases waiting for the owner to confirm them in the Steam app - the oldest first.</summary>
	public IReadOnlyList<Entry> OnPhone {
		get {
			lock (_gate) {
				return [.. Load().Where(static e => e.OnPhone).OrderBy(static e => e.At)];
			}
		}
	}

	/// <summary>The games whose set it sold instead of crafting since then.</summary>
	public HashSet<uint> SoldSince(DateTime utc) {
		lock (_gate) {
			Load();

			return [.. _sold.Where(e => e.At >= utc).Select(static e => e.App)];
		}
	}

	/// <summary>1.8.2: the games whose cards were found worth more sold than kept since then (<see cref="DearCards"/>).</summary>
	public HashSet<uint> DearSince(DateTime utc) {
		lock (_gate) {
			Load();

			return [.. _sold.Where(e => e.Dear && (e.At >= utc)).Select(static e => e.App)];
		}
	}

	/// <summary>
	/// 1.8.2: a game weighed again and found worth keeping after all - no longer left alone by buying and booster packs. True
	/// when there was a mark to take off and it was saved.
	/// </summary>
	public bool ForgetDear(uint app) {
		lock (_gate) {
			Load();

			return (_sold.RemoveAll(e => e.Dear && (e.App == app)) > 0) && Save();
		}
	}

	/// <summary>1.8.2: since when the game's cards (or foil cards) have waited to be sold - null when they aren't waiting.</summary>
	public DateTime? WaitingSince(uint app, bool foil, string source = SetSource) {
		lock (_gate) {
			Load();

			return _waits.FirstOrDefault(w => (w.App == app) && (w.Foil == foil) && (w.Source == source))?.Since;
		}
	}

	/// <summary>1.8.2: what each card of a set sold instead of crafted since then was weighed at - null when not known.</summary>
	public Dictionary<string, int>? WeighedFor(uint app, DateTime since) {
		lock (_gate) {
			Load();

			return _sold.Where(e => (e.App == app) && !e.Dear && (e.At >= since) && (e.Weighed != null)).OrderByDescending(static e => e.At).FirstOrDefault()?.Weighed;
		}
	}

	/// <summary>
	/// 1.8.2: the game's cards wait to be sold from now (<paramref name="waiting"/>), or no longer wait. The first time they
	/// wait is kept; how long they've waited is what lets them go at last. Saved only when it changes.
	/// </summary>
	public void Wait(uint app, bool foil, bool waiting, DateTime now, string source = SetSource) {
		lock (_gate) {
			Load();
			bool was = _waits.Exists(w => (w.App == app) && (w.Foil == foil) && (w.Source == source));

			if (waiting && !was) {
				_waits.Add(new Waiting(now, app, foil, source));
				Save();
			} else if (!waiting && was) {
				_waits.RemoveAll(w => (w.App == app) && (w.Foil == foil) && (w.Source == source));
				Save();
			}
		}
	}

	/// <summary>A set sold instead of crafted, written down. False when it couldn't be saved.</summary>
	public bool AddSold(SoldSet sold) {
		lock (_gate) {
			Load();
			_sold.Add(sold);
			_sold.RemoveAll(static e => e.At < DateTime.UtcNow.AddDays(-60));

			return Save();
		}
	}

	/// <summary>Write one down. False when it couldn't be saved (kept in memory either way).</summary>
	public bool Add(Entry entry) {
		lock (_gate) {
			List<Entry> all = Load();
			all.Add(entry);
			all.RemoveAll(static e => e.At < DateTime.UtcNow.AddDays(-60));   // two months is plenty for a week's limit

			return Save();
		}
	}

	/// <summary>Change one entry (found by its key), or take it out (null). False when it couldn't be saved.</summary>
	public bool Replace(Entry old, Entry? now) {
		lock (_gate) {
			List<Entry> all = Load();
			int at = all.FindIndex(e => old.Key != null ? e.Key == old.Key : e == old);

			if (at < 0) {
				return Save();
			}

			if (now == null) {
				all.RemoveAt(at);
			} else {
				all[at] = now;
			}

			return Save();
		}
	}

	private bool Save() {
		if (_unreadable) {
			return false;   // never written over: whatever is in it stays for the owner to look at
		}

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(new Stored { Ok = _ok, Entries = _entries ?? [], Sold = _sold, Waits = _waits, Crafts = _crafts, Lows = _lows,
				OkDayNoLimit = _okDayNoLimit, OkWeekNoLimit = _okWeekNoLimit, Goal = _goal }));
			_onDisk = true;

			return true;
		} catch (Exception e) {
			// Kept in memory either way, so this run's limits still hold - and the buyer stops (it hears the false).
			Log.Debug(new Said("couldn't save the card buying list: {0}", Log.Describe(e)));

			return false;
		}
	}

	private List<Entry> Load() {
		if (_entries != null) {
			return _entries;
		}

		try {
			Stored? read = File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new JsonException("empty") : null;
			_onDisk = read != null;
			_entries = read?.Entries ?? [];
			_sold = read?.Sold ?? [];
			_waits = [.. (read?.Waits ?? []).Where(static w => w.Source != null)];   // 1.8.2: a wait from before it said whose starts again
			_crafts = read?.Crafts ?? [];
			_lows = read?.Lows ?? [];
			_okDayNoLimit = read?.OkDayNoLimit;
			_okWeekNoLimit = read?.OkWeekNoLimit;
			_goal = read?.Goal;
			_ok = read?.Ok ?? false;
		} catch (Exception e) {
			// A list that can't be read counts the week as already spent, rather than nothing spent - and no go-ahead.
			Log.Debug(new Said("couldn't read the card buying list: {0}", Log.Describe(e)));
			_entries = [];
			_unreadable = true;
			_onDisk = true;   // deleted, the next ask starts a fresh list
			_ok = false;
		}

		return _entries;
	}
}

/// <summary>
/// Buying cards: the missing normal cards of the badges an account is levelling, at the cheapest listing, so the next
/// badge level can be crafted - and, with "Buy the cheapest full sets" on, whole sets for games it has none of.
/// </summary>
/// <remarks>
/// It spends real money, so every way in is careful. Off by default on every account; turned on only through its preview
/// (the switch's pop-up, or 'buy on &lt;account&gt;'). It never spends more than "Spend at most, a day" in any 24 hours or
/// "Spend at most, a week" in any 7 days (on Steam's clock), nor more than the wallet holds, and reads the wallet and every
/// card's price again before buying - a set whose price moved isn't started, a card dearer than planned isn't bought. Each
/// card is one buy order at the cheapest listing's price for one card, written down as spent before it's sent, which
/// fills at once or is taken back; one whose outcome Steam didn't say stays counted and is asked about at the next look.
/// A human-mode account buys only in its own day, one set a look, spaced out. Bought cards are crafted by badge crafting
/// (which has to be on - otherwise nothing is bought).
///
/// 1.8.2: each order carries the billing details saved on the account, as Steam's own market page sends them (it only
/// stops when none are saved), and the tax some countries add on Steam's fee. Steam wanting a purchase confirmed in the
/// Steam app: with the account's authenticator here it confirms exactly that one itself; without, it's left for the
/// owner's phone like a market listing - counted as spent, sent again with the confirmation at the next look (bought
/// once confirmed, let go when turned down or a day old), and no other set is started meanwhile. 1.8.2 fixes: the
/// waiting ones are sent again every few minutes between looks (<see cref="PhoneRoundAsync"/>), so a purchase is bought
/// minutes after the owner confirms it; a purchase Steam never says the end of is proven by the card arriving in the
/// inventory (<see cref="ProveAsync"/>); Steam's "too many purchases without confirmation" stops new ones until the
/// waiting ones are done. What it buys is ranked
/// by what the next badge level costs, cheapest first; games automatic booster packs is working on are left to the packs.
/// </remarks>
public sealed class CardBuyer(Bot bot, ICardMarket? market = null, ICardSwaps? swaps = null, Func<CancellationToken, Task<DateTime>>? clock = null) : BotModule(bot) {
	/// <summary>How a look was asked for.</summary>
	public enum Mode {
		/// <summary>'buy preview': read only, whatever the settings say.</summary>
		Preview,

		/// <summary>The account's own loop: only while buying is on.</summary>
		Auto,

		/// <summary>'buy do &lt;account&gt;': now, by hand - still only while it's on, inside the limits.</summary>
		Now
	}

	/// <summary>What one look came to.</summary>
	public sealed class Result {
		public BuyPlan? Plan { get; set; }

		/// <summary>Cards really bought, with what each cost.</summary>
		public List<(BuyStep Step, BuyCard Card, long Paid)> Bought { get; } = [];

		/// <summary>Sets bought whole this look.</summary>
		public List<BuyStep> Finished { get; } = [];

		/// <summary>Cards left for the owner to confirm in the Steam app this look - nothing is bought until they are.</summary>
		public List<(BuyStep Step, BuyCard Card)> Waiting { get; } = [];

		/// <summary>Purchases confirmed on the phone since the last look, bought at this look.</summary>
		public int Confirmed { get; set; }

		/// <summary>1.8.2: purchases Steam didn't say the end of, found bought in the inventory at this look.</summary>
		public int Proven { get; set; }

		/// <summary>Why it couldn't look or carry on (English). Empty when all went well.</summary>
		public string Problem { get; set; } = "";

		/// <summary>The problem won't clear by itself (a setting, a block, Steam saying no) - the next look is at the usual time.</summary>
		public bool Lasting { get; set; }

		/// <summary>1.8.2: the problem stops all buying (Steam's market blocked, scam watch) - the preview leads with it.</summary>
		public bool Blocks { get; set; }

		/// <summary>1.8.2: games whose cards were found worth more sold than kept this look (<see cref="DearCards"/>) - listed once the look is done.</summary>
		public List<DearCards.Verdict> Dear { get; } = [];

		/// <summary>1.8.2: their cards that went up on the market.</summary>
		public int DearListed { get; set; }
	}

	/// <summary>One look at a time per account - across every copy of the module, so a restore can't make two buyers.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Busy = new(StringComparer.OrdinalIgnoreCase);

	private readonly ICardMarket _market = market ?? new SteamCardMarket(bot);

	/// <summary>1.8.2: the set finder a look and an estimate go by - the app's own; the checks give their own.</summary>
	public SetFinder? Finder { get; set; }

	/// <summary>1.8.2: card prices this account's plans read lately - used again for a couple of hours, refreshed by every read before buying.</summary>
	private readonly BuyPlan.Prices _prices = new();

	/// <summary>1.8.2: the cheapest badge level each account's last look priced, in its wallet's money - for 'buy'.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Cents, int Currency, DateTime At)> LevelPrices = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The cheapest level a look priced, noted for 'buy' (kept a day).</summary>
	private void NoteLevelPrice(BuyPlan plan) {
		if (plan.LevelPrice > 0) {
			LevelPrices[Bot.Name] = (plan.LevelPrice, plan.Currency, DateTime.UtcNow);
		}
	}

	/// <summary>"0.33 CAD a level (100 XP)" - the cheapest level the account's last look (in the last day) priced; empty when none.</summary>
	public static string LevelPriceOf(string account) =>
		LevelPrices.TryGetValue(account, out (long Cents, int Currency, DateTime At) p) && (DateTime.UtcNow - p.At < TimeSpan.FromDays(1))
			? $"{BuyPlan.Money(p.Cents, p.Currency)} a level (100 XP)" : "";
	private ICardSwaps Swaps => swaps ?? SwapCost.Current;
	private readonly Func<CancellationToken, Task<DateTime>> _clock = clock ?? SteamClockAsync;
	private HumanGate? _gate;
	private Said _status = new("");
	private BuyBlock _saidBlocked = BuyBlock.None;
	private bool _saidStopped;

	/// <summary>The spending list couldn't be saved: nothing more is bought until nocat.farm starts again.</summary>
	private bool _cantSave;

	/// <summary>1.8.2: Steam said too many purchases wait for a confirmation - said once, until they're confirmed or let go.</summary>
	private bool _saidTooMany;

	/// <summary>The "too many waiting" line's said-once memory, kept on disk (<see cref="MarketHold.Once"/>).</summary>
	internal const string TooManyKey = "buy-too-many";

	/// <summary>1.8.2: when purchases waiting for the owner's phone are next sent again, and the wallet those rounds use.</summary>
	private DateTime _nextPhone = DateTime.MinValue;

	private (CardMarket.Wallet Wallet, DateTime At)? _phoneWallet;

	/// <summary>1.8.2: when the inventory was last read to prove a purchase - at most once a minute (<see cref="ProofGap"/>).</summary>
	private DateTime _lastProof = DateTime.MinValue;

	/// <summary>How long a fresh purchase is given to show up in the inventory before it's looked for. The tests make it 0.</summary>
	public Func<TimeSpan> ProofWait { get; set; } = static () => Rng.Seconds(15, 30);

	/// <summary>The least time between two inventory reads to prove purchases.</summary>
	public TimeSpan ProofGap { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>How long a purchase Steam never said the end of is still looked for in the inventory.</summary>
	internal static readonly TimeSpan ProofFor = TimeSpan.FromDays(7);

	/// <summary>How long an order Steam leaves unsure is asked about again; after that it's counted as bought (see SweepLockedAsync).</summary>
	internal static readonly TimeSpan AskFor = TimeSpan.FromHours(6);

	/// <summary>When the next look is - from "Look every (hours)" as it's set now.</summary>
	internal LookTimer Timer { get; } = new(() => Math.Clamp(bot.Cfg.BuyCheckHours, 1, 168), 1.0, 1.2);

	public override string Name => "buying cards";
	public override string Status => Bot.Cfg.BuyCards ? (Ledger.Goal is { } goal ? GoalSaid(goal).ToString() + " · " : "") + _status : "";

	/// <summary>1.8.2: a level goal's progress: "level goal 70: 12 of 18 levels, 4.10 of 6.20 CAD".</summary>
	public Said GoalSaid(BuyLedger.LevelGoal goal) =>
		new("level goal {0}: {1} of {2} levels, {3} of {4}", goal.Target, goal.Done, goal.Levels,
			Spare.Money(Ledger.SpentSince(goal.Started)), BuyPlan.Money(goal.Total, goal.Currency));

	/// <summary>
	/// 1.8.2: the settings a level goal buys by - the account's own (its pacing, its most per level), with "Stop buying at
	/// Steam level" at the goal, whole sets on, and the day's and week's limits off: the goal's own budget is the limit.
	/// </summary>
	public BotConfig GoalCfg(int target) {
		BotConfig c = JsonSerializer.Deserialize<BotConfig>(JsonSerializer.Serialize(Bot.Cfg))!;
		c.BuyStopAtLevel = target;
		c.BuyFullSets = true;
		c.BuyDailyLimit = -1f;
		c.BuyWeeklyLimit = -1f;

		return c;
	}

	/// <summary>What a level goal may still spend: its budget less what was spent since it started (never under 0).</summary>
	private long GoalLeft(BuyLedger.LevelGoal goal) => Math.Max(0, goal.Budget - Ledger.SpentSince(goal.Started));

	/// <summary>1.8.2, 'levelup': what reaching a level would buy - an estimate from what's known, nothing bought or written down.</summary>
	/// <param name="Needed">Badge levels in all to reach it (100 XP each); <paramref name="Held"/> of them the sets held make once crafted.</param>
	/// <param name="Costs">The cheapest levels to buy for the rest, within the most per level - fewer than needed when not enough are known.</param>
	/// <param name="Known">How many of <paramref name="Costs"/> are from card prices known now (the rest at the set finder's price).</param>
	/// <param name="Usual">What a level usually costs - what the levels not found are counted at.</param>
	/// <param name="Days">About how many days buying them takes at this account's pace.</param>
	public sealed record GoalPreview(string Problem, int Level, int Xp, int Target, int Needed, int Held, IReadOnlyList<long> Costs, int Known, int Currency,
		double Days, long MaxLevel, long Usual, long Wallet = -1) {
		public long Total => Costs.Sum();

		/// <summary>1.8.2: how many of the levels to buy the wallet pays for now - the cheapest first.</summary>
		public int Affordable {
			get {
				long left = Wallet;
				int n = 0;

				foreach (long cost in Costs.Order()) {
					if (cost > left) {
						break;
					}

					left -= cost;
					n++;
				}

				return n;
			}
		}

		/// <summary>Levels to buy that no known price covers yet.</summary>
		public int Missing => Math.Max(0, Needed - Held - Costs.Count);
	}

	/// <summary>
	/// 1.8.2: the plan for a level goal - at once, from what's known: the set finder's fresh sets, prices read for a plan in the
	/// last 2 hours, prices saved in the last day, and a handful of a started badge's missing cards; the badges and the
	/// inventory as read in the last 10 minutes. The cheapest levels first, within the most per level.
	/// </summary>
	public async Task<GoalPreview> GoalPreviewAsync(int target, CancellationToken ct) {
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);
		SteamCardMarket? steamMarket = _market as SteamCardMarket;

		try {
			if (steamMarket != null) {
				steamMarket.ReuseFor = TimeSpan.FromMinutes(10);
			}

			BuyPlan plan = await BuyPlan.MakeAsync(GoalCfg(target), _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct,
				boosting: Boosting(), prices: _prices, knownOnly: true, finder: Finder).ConfigureAwait(false);
			GoalPreview Problem(string why) => new(why, 0, 0, target, 0, 0, [], 0, plan.Currency, 0, 0, 0);

			if (plan.Problem.Length > 0) {
				return Problem(plan.Problem);
			}

			if (_market.SteamLevel is not { } steam) {
				return Problem("Steam didn't say its level");
			}

			if (steam.Level >= target) {
				return Problem($"it's already level {steam.Level}");
			}

			int needed = (int) Math.Max(0, (BuyPlan.XpFor(target) - steam.Xp + 99) / 100);
			int toBuy = plan.LevelsToBuy >= 0 ? plan.LevelsToBuy : needed;
			List<(uint App, long Cost)> levels = [.. plan.Levels.Where(l => (plan.MaxLevelPrice <= 0) || (l.Cost <= plan.MaxLevelPrice)).OrderBy(static l => l.Cost).Take(toBuy)];

			return new GoalPreview("", steam.Level, steam.Xp, target, needed, Math.Max(0, needed - toBuy), [.. levels.Select(static l => l.Cost)],
				levels.Count(l => !plan.Estimated.Contains(l.App)), plan.Currency, GoalDays(Bot.Cfg, toBuy), plan.MaxLevelPrice,
				plan.UsualLevelPrice > 0 ? plan.UsualLevelPrice : plan.MaxLevelPrice, plan.Wallet);
		} finally {
			if (steamMarket != null) {
				steamMarket.ReuseFor = TimeSpan.Zero;
			}

			BusyGate.Release();
		}
	}

	/// <summary>
	/// About how many days buying this many levels takes at an account's pace: a human-mode account buys "Sets per look,
	/// human mode" a look, a look every "Time between looks, human mode" in a 16-hour day; a robot about 200 a day.
	/// </summary>
	public static double GoalDays(BotConfig cfg, int levels) {
		if (levels <= 0) {
			return 0;
		}

		double perDay = cfg.LegitMode
			? SetsALook(cfg) * (16 * 60.0 / Math.Max(5, (Math.Clamp(cfg.BuyHumanGapMinMinutes, 5, 720) + Math.Clamp(cfg.BuyHumanGapMaxMinutes, 5, 720)) / 2.0))
			: 200;

		return Math.Max(0.1, Math.Round(levels / perDay, 1));
	}

	/// <summary>
	/// 1.8.2: a level goal started from its preview - buying's own looks buy its levels (the goal's budget instead of the day's
	/// and week's limits, never over the previewed total and a tenth) until the account reaches the level. Needs buying's
	/// go-ahead. Null when started, else why not (English).
	/// </summary>
	public string? StartGoal(GoalPreview p) {
		if (!On(Bot)) {
			return "buying cards is off - turn it on first (its preview shows what it does)";
		}

		if ((p.Problem.Length > 0) || (p.Costs.Count == 0) || (p.Currency <= 0)) {
			return p.Problem.Length > 0 ? p.Problem : "no level to buy within the most per level";
		}

		if (!Ledger.SetGoal(new BuyLedger.LevelGoal(p.Target, p.Costs.Count, p.Total, p.Currency, DateTime.UtcNow, p.Xp, p.Xp))) {
			return "the list of what it spent on cards couldn't be saved";
		}

		Log.Info(new Said("level goal {0} started: {1} levels for about {2} (never over {3})", p.Target, p.Costs.Count, BuyPlan.Money(p.Total, p.Currency),
			BuyPlan.Money(p.Total * 11 / 10, p.Currency)), Bot.Name);
		Timer.LookAt(DateTime.UtcNow + Rng.Minutes(1, 3));

		return null;
	}

	/// <summary>1.8.2: the level goal cancelled ('levelup &lt;account&gt; stop'). True when there was one.</summary>
	public bool StopGoal() {
		if (Ledger.Goal is not { } goal) {
			return false;
		}

		Ledger.SetGoal(null);
		Log.Info(new Said("level goal {0} stopped", goal.Target), Bot.Name);

		return true;
	}

	/// <summary>A look's word on the goal: the level reached ends it; otherwise its progress is kept.</summary>
	private void GoalSeen(BuyLedger.LevelGoal goal, BuyPlan plan) {
		if (_market.SteamLevel is not { } steam) {
			return;
		}

		if (plan.LevelReached || (steam.Level >= goal.Target)) {
			Ledger.SetGoal(null);
			Log.Good(new Said("level goal reached: Steam level {0} · {1} spent", steam.Level, BuyPlan.Money(Ledger.SpentSince(goal.Started), goal.Currency)), Bot.Name);
		} else if (steam.Xp != goal.LastXp) {
			Ledger.SetGoal(goal with { LastXp = steam.Xp });
		}
	}

	public BuyLedger Ledger => BuyLedger.Of(Bot.Name);

	/// <summary>The gate a human-mode account's buying waits on: its own day (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>Whether this account buys: the switch and its go-ahead together.</summary>
	public static bool On(Bot bot) => bot.Cfg.BuyCards && BuyLedger.Of(bot.Name).Ok;

	/// <summary>Now on Steam's clock (this PC's until Steam's is known) - what the limits' windows go by.</summary>
	internal static async Task<DateTime> SteamClockAsync(CancellationToken ct) =>
		DateTimeOffset.FromUnixTimeSeconds(await Confirmations.SteamTimeAsync(ct).ConfigureAwait(false)).UtcDateTime;

	private SemaphoreSlim BusyGate => Busy.GetOrAdd(Bot.Name, static _ => new SemaphoreSlim(1, 1));

	/// <summary>
	/// The games left to booster packs: a pack waiting to open for them (<see cref="BoosterPick.Busy(Bot)"/>) - and, 1.8.2, the games
	/// whose cards from a pack made to sell are still to be sold: those copies are for the market, never counted as cards held
	/// for a badge (buying the rest of the set would leave them stranded or crafted).
	/// </summary>
	private IReadOnlyCollection<uint> Boosting() => [.. BoosterPick.Busy(Bot), .. GemsChoice.Memory.Of(Bot.Name).CardsNow().Select(static c => c.App)];

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Minutes(10, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && Timer.Due(DateTime.UtcNow)) {
				bool buying = On(Bot) && Gate.Open;

				// Off, or outside its day: an order it placed whose outcome isn't known is still asked about and taken back
				// (and a purchase waiting on the phone too long is let go).
				if (buying || (Ledger.OpenOrders.Count > 0) || (Ledger.OnPhone.Count > 0) || (Ledger.Unplaced.Count > 0)) {
					Timer.Looked(DateTime.UtcNow);

					try {
						if (buying) {
							Result result = await LookAsync(Mode.Auto, ct).ConfigureAwait(false);

							if (result.Bought.Count > 0) {
								Gate.Space(30, 120);
							}

							// Something that clears by itself (no answer, a price that moved) is looked at again sooner;
							// a setting, a block or Steam saying no waits for the usual time.
							if ((result.Problem.Length > 0) && !result.Lasting) {
								Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
							}

							// 1.8.2: sets bought, money left and cheap levels still to buy - the next look soon, not in 12 hours.
							if (FollowUp(result, Bot.Cfg.LegitMode, Bot.Cfg.BuyHumanGapMinMinutes, Bot.Cfg.BuyHumanGapMaxMinutes) is { } soon) {
								Timer.LookAt(DateTime.UtcNow + soon);
								Log.Debug(new Said("buying cards · money left for more cheap levels - next look in {0} minutes", (int) soon.TotalMinutes), Bot.Name);
							}
						} else {
							await BusyGate.WaitAsync(ct).ConfigureAwait(false);

							try {
								await SweepLockedAsync(ct, resume: false).ConfigureAwait(false);
							} finally {
								BusyGate.Release();
							}
						}

						_status = new Said("buying cards: next look around {0}", (Func<string>) (() => Fmt.Clock(Timer.Next)));
					} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
						throw;
					} catch (Exception e) {
						Log.Debug(new Said("couldn't look at buying cards: {0}", Log.Describe(e)), Bot.Name);
						Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
					}
				}
			}

			// Purchases waiting for the owner's phone: finished as soon as they're confirmed, between looks - outside a
			// human-mode account's day too (the owner already said yes), but nothing new is started here.
			if (Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && PhoneDue(DateTime.UtcNow)) {
				try {
					Result phone = await PhoneRoundAsync(ct).ConfigureAwait(false);

					// 1.8.2: the last one waiting on the phone confirmed and bought - new sets may be bought again, soon.
					if ((phone.Confirmed > 0) && (Ledger.OnPhone.Count == 0) && On(Bot)) {
						Timer.LookAt(DateTime.UtcNow + FollowUpGap());
					}
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at buying cards: {0}", Log.Describe(e)), Bot.Name);
					_nextPhone = DateTime.UtcNow + Rng.Minutes(5, 10);
				}
			}

			if (!await Sleep(Wake(DateTime.UtcNow), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>Whether purchases wait for the owner's phone and it's time to send them again.</summary>
	public bool PhoneDue(DateTime now) => (now >= _nextPhone) && (Ledger.OnPhone.Count > 0);

	/// <summary>The loop's next wake: the usual few minutes - sooner while purchases wait for the owner's phone.</summary>
	private TimeSpan Wake(DateTime now) {
		TimeSpan usual = Rng.Minutes(2, 5);

		return Ledger.OnPhone.Count > 0 ? TimeSpan.FromTicks(Math.Clamp((_nextPhone - now).Ticks, TimeSpan.FromSeconds(15).Ticks, usual.Ticks)) : usual;
	}

	/// <summary>
	/// 1.8.2: the next look soon after one that bought, while money and cheap levels are left. A robot: 3 to 8 minutes - it
	/// buys every set that fits in one look, so another comes only when a look stopped short (it read its 60 prices, or a
	/// price moved). A human-mode account buys a few sets a look ("Sets per look, human mode", 3), only in its day, the next
	/// look 30 to 90 minutes later ("Time between looks, human mode"), like somebody buying a few sets now and then - about
	/// 16 looks in a 16-hour day, 25 to 40 sets, so 11.00 a day at 0.30 to 0.50 a set gets spent. Gentle on the
	/// market either way: a follow-up mostly re-uses the prices read in the last 2 hours (<see cref="BuyPlan.PriceKept"/>)
	/// and reads each card once right before buying - a set's 5 to 10 reads, inside the app's 360 an hour.
	/// </summary>
	/// <param name="humanMin">"Time between looks, human mode", shortest (30 by default).</param>
	/// <param name="humanMax">Longest (90 by default).</param>
	public static TimeSpan FollowUpGap(bool human, int humanMin = 30, int humanMax = 90) {
		int lo = Math.Clamp(Math.Min(humanMin, humanMax), 5, 720), hi = Math.Clamp(Math.Max(humanMin, humanMax), 5, 720);

		return human ? Rng.Minutes(lo, hi) : Rng.Minutes(3, 8);
	}

	/// <summary>The follow-up gap for this account, from its settings.</summary>
	private TimeSpan FollowUpGap() => FollowUpGap(Bot.Cfg.LegitMode, Bot.Cfg.BuyHumanGapMinMinutes, Bot.Cfg.BuyHumanGapMaxMinutes);

	/// <summary>Sets one look buys: every one that fits on a robot; "Sets per look, human mode" (1 to 10) on a human-mode account.</summary>
	public static int SetsALook(BotConfig cfg) => cfg.LegitMode ? Math.Clamp(cfg.BuyHumanSetsPerLook, 1, 10) : int.MaxValue;

	/// <summary>
	/// 1.8.2: after a look that bought sets, while money is left today and cheap levels it knows are still to buy - the next
	/// look soon (<see cref="FollowUpGap"/>) instead of in "Look every" hours (12), so the day's money buys the day's cheap
	/// levels. A human-mode account's look still waits for its day (outside it, nothing).
	/// Never while purchases wait on the owner's phone (Steam takes only a few unconfirmed - the next look waits for them
	/// anyway), nor after Steam said no. Null: the usual time.
	/// </summary>
	public static TimeSpan? FollowUp(Result result, bool human, int humanMin = 30, int humanMax = 90) {
		if ((result.Plan is not { } plan) || (result.Bought.Count == 0) || (result.Waiting.Count > 0) || ((result.Problem.Length > 0) && result.Lasting)) {
			return null;
		}

		long left = plan.Room - result.Bought.Sum(static b => b.Paid);
		HashSet<uint> bought = [.. result.Bought.Select(static b => b.Step.App)];
		bool Within(long cost) => (cost <= left) && ((plan.MaxLevelPrice <= 0) || (cost <= plan.MaxLevelPrice));
		bool more = (left > 0) && (plan.Capped || plan.Ranked.Any(s => !bought.Contains(s.App) && Within(s.Cost)) || plan.Levels.Any(l => !bought.Contains(l.App) && Within(l.Cost)));

		return more ? FollowUpGap(human, humanMin, humanMax) : null;
	}

	/// <summary>
	/// How long until purchases waiting for the phone are sent again, by how long the oldest has waited: about every 2
	/// minutes for the first half hour, every 5 for the next few hours, then every 10 to 15 - gentle on Steam, and still
	/// quick when the owner confirms soon after the prompt.
	/// </summary>
	public static TimeSpan PhoneGap(TimeSpan waited) =>
		waited < TimeSpan.FromMinutes(30) ? Rng.Seconds(100, 140)
			: waited < TimeSpan.FromHours(3) ? Rng.Minutes(4, 6)
			: Rng.Minutes(10, 15);

	/// <summary>
	/// 1.8.2: purchases waiting for the owner's phone, between looks - each sent again with its confirmation, as Steam's
	/// market page does, so it's bought the moment the owner has confirmed it ("bought ... · 0.04 CAD"). Only finishes
	/// what the owner already confirmed: no new purchase is started, the money was counted when it was asked for, and it
	/// runs outside a human-mode account's day too. Not while buying is off, paused, held by scam watch or its list can't
	/// be saved. One a day old is let go. Steam asking for less (a 429) shuts the market for every account (WebSession):
	/// nothing is sent until that passes, and the next round comes on its own.
	/// </summary>
	public async Task<Result> PhoneRoundAsync(CancellationToken ct) {
		Result result = new();
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			if (Ledger.OnPhone.Count > 0) {
				bool unsure = await ResumePhoneAsync(ct, Stopped(finishing: true) == null, result).ConfigureAwait(false);

				if (unsure) {
					await ProveAsync(justBought: true, ct).ConfigureAwait(false);
				}
			}
		} finally {
			BusyGate.Release();
		}

		DateTime now = await _clock(ct).ConfigureAwait(false);
		TimeSpan waited = Ledger.OnPhone.Select(e => now - e.At).DefaultIfEmpty(TimeSpan.Zero).Max();
		_nextPhone = DateTime.UtcNow + PhoneGap(waited);

		return result;
	}

	/// <summary>
	/// Look, and - outside a preview, while it's on and the day allows - buy what the plan says. One look at a time. 1.8.2:
	/// then the cards of games found worth more sold than kept are listed - after the look, so purchases waiting on the
	/// owner's phone aren't held up by listings minutes apart.
	/// </summary>
	public async Task<Result> LookAsync(Mode mode, CancellationToken ct) {
		Result result;
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			result = await LookLockedAsync(mode, ct).ConfigureAwait(false);
		} finally {
			BusyGate.Release();
		}

		// 1.8.2: a human-mode look that bought leaves its dear cards for a later look - listed seconds after buying, before
		// its day's spacing (Gate.Space) had even started, it was a burst on the market.
		if ((mode != Mode.Preview) && (result.Dear.Count > 0)) {
			if (Bot.Cfg.LegitMode && (result.Bought.Count + result.Waiting.Count > 0)) {
				Log.Debug($"cards worth more sold than kept: this look bought - listed at a later look ({result.Dear.Count} game(s))", Bot.Name);
			} else {
				await SellDearAsync(result, ct).ConfigureAwait(false);
			}
		}

		return result;
	}

	/// <summary>Lists a dear game's cards - how many went up. The checks give their own.</summary>
	public Func<Bot, DearCards.Verdict, InventoryContents, CancellationToken, Task<int>> ListDear { get; set; } = DearCards.ListAsync;

	/// <summary>
	/// 1.8.2: the cards of the games this look found dear, listed from the inventory the plan read: one line a game, each
	/// card at Debug. One game a look on a human-mode account, the others at later looks (they stay marked meanwhile).
	/// </summary>
	private async Task SellDearAsync(Result result, CancellationToken ct) {
		if (await _market.InventoryAsync(ct).ConfigureAwait(false) is not { } inventory) {
			Log.Debug(new Said("cards worth more sold than kept: the inventory isn't at hand to list them - the next look"), Bot.Name);

			return;
		}

		foreach (DearCards.Verdict v in DearCards.ThisLook(result.Dear, Bot.Cfg.LegitMode)) {
			if (ct.IsCancellationRequested) {
				return;
			}

			Log.Info(DearCards.SellingSaid(v), Bot.Name);

			foreach (DearCards.Card c in v.Cards) {
				Log.Debug(DearCards.CardSaid(v, c), Bot.Name);
			}

			int listed = await ListDear(Bot, v, inventory, ct).ConfigureAwait(false);
			result.DearListed += listed;

			if (listed < v.Listing) {
				Log.Debug(new Said("{0}: {1} of {2} cards listed · the rest wait for the next look", v.Game, listed, v.Listing), Bot.Name);
			}

			if (listed > 0) {
				Gate.Space(30, 120);
			}
		}
	}

	/// <summary>
	/// 1.8.2, for the previews ('sell &lt;account&gt; preview'): the games whose cards are worth more sold than kept, weighed
	/// now as a look would - nothing listed, bought, written down or logged. <paramref name="inventory"/>: one the caller
	/// just read - its cards are used, not read again. Off (its setting, or buying cards): why, and nothing weighed.
	/// </summary>
	public async Task<(DearCards.Look Look, string Off)> WeighDearAsync(InventoryContents? inventory, CancellationToken ct) {
		if (!Bot.Cfg.SellSetsForLevels) {
			return (new DearCards.Look([]), "\"Sell a set instead of crafting it when the money buys more levels\" is off");
		}

		if (!Bot.Cfg.CraftBadges || !On(Bot)) {
			return (new DearCards.Look([]), "buying cards is off - the money from selling couldn't be spent");
		}

		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			BuyPlan plan = await BuyPlan.MakeAsync(Bot.Cfg, _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct,
				boosting: Boosting(), heldCards: inventory == null ? null : LevelPlanner.NormalCards(inventory)).ConfigureAwait(false);
			GemsChoice.NoteCosts(Bot.Name, plan, DateTime.UtcNow);

			if ((plan.Problem.Length > 0) || (plan.Block != BuyBlock.None)) {
				return (new DearCards.Look([]), plan.Problem.Length > 0 ? plan.Problem : plan.Blocked);
			}

			return (await WeighDearLockedAsync(plan, inventory, ct).ConfigureAwait(false), "");
		} finally {
			BusyGate.Release();
		}
	}

	/// <summary>
	/// The games worth more sold than kept, on a plan just made (and its level costs noted): the foil cards and foil badges
	/// from the inventory and badges it read, what's left of sets sold lately, and how long each has waited. Reads only.
	/// </summary>
	private async Task<DearCards.Look> WeighDearLockedAsync(BuyPlan plan, InventoryContents? inventory, CancellationToken ct) {
		DateTime now = DateTime.UtcNow;
		inventory ??= await _market.InventoryAsync(ct).ConfigureAwait(false);
		HashSet<uint> dear = Ledger.DearSince(now - SetSale.NotBoughtFor);
		BuyLedger ledger = Ledger;
		DearCards.Extra extra = new(inventory == null ? null : LevelPlanner.FoilCards(inventory), await _market.FoilLevelsAsync(ct).ConfigureAwait(false),
			[.. Ledger.SoldSince(now - SetSale.NotBoughtFor).Where(a => !dear.Contains(a))], (a, f) => ledger.WaitingSince(a, f, BuyLedger.DearSource)) {
			// 1.8.2: each game weighed against the levels money buys without its own (its missing cards are counted as kept's cost),
			// what's left of a set sold never under what the set was weighed at, a bought card never under what it cost, and a
			// card never for less than its gems or "Only sell if you get at least".
			CostsWithout = app => GemsChoice.CostsOf(Bot.Name, plan.Currency, now, except: app),
			Weighed = app => ledger.WeighedFor(app, now - SetSale.NotBoughtFor),
			Paid = hash => ledger.PaidFor(hash, plan.Currency),
			BoughtCopies = hash => ledger.BoughtCopies(hash, now.AddDays(-60)),
			MinReceive = SpareRules.From(Bot.Cfg).MinSaleCents,
			GemsWorth = DearCards.KnownGemsWorth(inventory, plan.Currency, SpareRules.From(Bot.Cfg).DumpPct)
		};

		return await DearCards.WeighAsync(plan, _market, Bot.Cfg, CardExchange.BoughtGames(Bot.Name, now), dear,
			GemsChoice.CostsOf(Bot.Name, plan.Currency, now), ct, extra).ConfigureAwait(false);
	}

	/// <summary>
	/// 'buy do': a look that buys, now - on this module's own run, so stopping the account (a restore, an update, signing
	/// out) stops it too, and never beside a look already running.
	/// </summary>
	public Task<Result> BuyNowAsync() {
		CancellationToken ct;

		try {
			ct = Cts?.Token ?? new CancellationToken(true);
		} catch (ObjectDisposedException) {
			ct = new CancellationToken(true);
		}

		return ct.IsCancellationRequested
			? Task.FromResult(new Result { Problem = "buying cards isn't running on this account right now" })
			: LookAsync(Mode.Now, ct);
	}

	/// <summary>
	/// 1.8.2, for the gems maths (<see cref="GemsChoice"/>): what each next badge level costs, cheapest first, priced now the
	/// way a look ranks them - nothing is bought. Null when it couldn't tell (or the wallet's money isn't
	/// <paramref name="currency"/>); empty when Steam won't let this account buy by itself. Noted for a day either way.
	/// </summary>
	/// <param name="held">The cards held, from an inventory the caller just read - not read again.</param>
	public async Task<IReadOnlyList<long>?> LevelCostsAsync(int currency, IReadOnlyDictionary<uint, Dictionary<string, int>>? held, CancellationToken ct) {
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			BuyPlan plan = await BuyPlan.MakeAsync(Bot.Cfg, _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct,
				boosting: Boosting(), heldCards: held, prices: _prices).ConfigureAwait(false);
			GemsChoice.NoteCosts(Bot.Name, plan, DateTime.UtcNow);
			NoteLevelPrice(plan);

			return GemsChoice.CostsOf(Bot.Name, currency, DateTime.UtcNow);
		} finally {
			BusyGate.Release();
		}
	}

	/// <summary>
	/// For badge crafting (smart levelling, <see cref="SetSale"/>): each of these games' whole normal set weighed - sold, or
	/// crafted - against what the money would buy, ranked as buying ranks it, at today's prices. Nothing is bought or listed
	/// here. One verdict per game (up to <see cref="SetSale.MaxSetsALook"/>): when the market or the wallet didn't say, it's
	/// marked not known for sure (Unsure).
	/// <paramref name="inventory"/>: the inventory badge crafting just read - its cards are used, not read again a second
	/// later (Steam refuses that as a duplicate request).
	/// </summary>
	public Task<IReadOnlyList<SetSale.Verdict>> WeighSalesAsync(IReadOnlyList<uint> apps, CancellationToken ct, InventoryContents? inventory = null) =>
		WeighSalesAsync([.. apps.Distinct().Select(static a => new SetSale.Ready(a, 0, 1))], ct, inventory, note: false);

	/// <summary>
	/// 1.8.2: each finished set weighed - a foil set too (Border 1: the game's foil cards), a game's several sets together.
	/// When Steam won't let this account buy by itself (billing details, email), a set worth more sold isn't crafted
	/// meanwhile: it waits (Hold), and the owner is told once. A set held back because a card of it hardly sells is noted
	/// with when it began to wait (<paramref name="note"/>: never by a preview) - after <see cref="SetSale.WaitAtMost"/>
	/// it's sold all the same.
	/// </summary>
	public async Task<IReadOnlyList<SetSale.Verdict>> WeighSalesAsync(IReadOnlyList<SetSale.Ready> ready, CancellationToken ct, InventoryContents? inventory, bool note) {
		List<SetSale.Ready> asked = [.. ready.DistinctBy(static r => (r.App, r.Border > 0)).Take(SetSale.MaxSetsALook)];
		string Game(uint app) => inventory == null ? GameNames.Of(app) : SetSale.GameOf(app, inventory);
		SetSale.Verdict As(SetSale.Ready r, SetSale.Verdict v) => v with { Sets = Math.Max(1, r.Levels), Foil = r.Border > 0 };
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			BuyPlan plan = await BuyPlan.MakeAsync(Bot.Cfg, _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct,
				boosting: Boosting(), heldCards: inventory == null ? null : LevelPlanner.NormalCards(inventory), prices: _prices).ConfigureAwait(false);
			GemsChoice.NoteCosts(Bot.Name, plan, DateTime.UtcNow);
			NoteLevelPrice(plan);

			// No wallet at all: the money from a set sold could never be spent - crafted, as without buying cards.
			if (plan.NoWallet) {
				return [.. asked.Select(r => As(r, SetSale.Crafted(r.App, Game(r.App), 0, new Said("it has no Steam wallet to spend the money from"))))];
			}

			if ((plan.Problem.Length > 0) || (plan.Block == BuyBlock.Unknown)) {
				Log.Debug($"weighing sets: {(plan.Problem.Length > 0 ? plan.Problem : plan.Blocked)}", Bot.Name);

				return [.. asked.Select(r => As(r, SetSale.Crafted(r.App, Game(r.App), plan.Currency, new Said("couldn't price its cards"), unsure: true)))];
			}

			List<SetSale.Verdict> weighed = [];
			HashSet<uint> bought = CardExchange.BoughtGames(Bot.Name, DateTime.UtcNow);

			foreach (SetSale.Ready r in asked) {
				// Cards were bought to finish it - for the level: crafted, never sold (as badge crafting decides - SetSale.ChooseAsync).
				if ((r.Border == 0) && bought.Contains(r.App)) {
					weighed.Add(As(r, SetSale.Crafted(r.App, Game(r.App), plan.Currency, new Said("cards were bought to finish it - crafted, never sold"))));

					continue;
				}

				CardSet? set = plan.Complete.Select(static c => c.Set).FirstOrDefault(s => s.App == r.App) ?? await _market.SetAsync(r.App, ct).ConfigureAwait(false);
				bool foil = r.Border > 0;

				if (set is not { Cards.Count: > 0 }) {
					weighed.Add(As(r, SetSale.Crafted(r.App, set?.Game ?? Game(r.App), plan.Currency, new Said("its set isn't known whole on the market"), unsure: true)));

					continue;
				}

				SetSale.Verdict v = await SetSale.WeighAsync(foil ? SetSale.FoilSet(set) : set, plan, _market, Bot.Cfg, ct, Math.Max(1, r.Levels), foil,
					Ledger.WaitingSince(r.App, foil, BuyLedger.SetSource), paid: h => Ledger.PaidFor(h, plan.Currency),
					gemsWorth: DearCards.KnownGemsWorth(inventory, plan.Currency, SpareRules.From(Bot.Cfg).DumpPct)).ConfigureAwait(false);

				// Steam won't let it buy by itself: what the set sells for couldn't be spent yet - it waits rather than being
				// crafted away, and the owner is told once what to fix.
				if ((plan.Block != BuyBlock.None) && v.Sell) {
					Said why = new Said("{0} buys about {1} levels, but Steam won't let it buy cards by itself - waiting until it can", v.Money, v.About);
					v = v with { Sell = false, Hold = true, Why = why.ToEnglish(), Reason = why };

					if (note && (_saidSetsBlocked != plan.Block)) {
						_saidSetsBlocked = plan.Block;
						Log.Attention(new Said("sets worth more sold than crafted wait uncrafted · {0}", plan.BlockedSaid), Bot.Name);
					}
				}

				// How long it has waited for a card that hardly sells: kept while it waits, let go once it doesn't.
				if (note && !v.Unsure) {
					Ledger.Wait(r.App, foil, v.Slow, DateTime.UtcNow, BuyLedger.SetSource);
				}

				weighed.Add(v);
			}

			if (plan.Block == BuyBlock.None) {
				_saidSetsBlocked = BuyBlock.None;
			}

			return weighed;
		} finally {
			BusyGate.Release();
		}
	}

	/// <summary>1.8.2: the accounts told the set finder is still warming up - told once, until it's done.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> WarmSaid = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>1.8.2: the block last told about for sets waiting uncrafted - told once, until it clears.</summary>
	private BuyBlock _saidSetsBlocked = BuyBlock.None;

	/// <summary>
	/// Why a look can't buy now, before anything is read from the market - or null when it may. Lasting when it won't clear by
	/// itself. <paramref name="finishing"/>: only finishing purchases the owner already confirmed - a human-mode account's day
	/// doesn't stop that.
	/// </summary>
	private (string Problem, bool Lasting)? Stopped(bool finishing = false, bool midSet = false) {
		if (!On(Bot)) {
			return ("buying cards is off", true);
		}

		if (Ledger.Unreadable || _cantSave) {
			if (!_saidStopped) {
				_saidStopped = true;
				Log.Attention(new Said("card buying stopped · its spending list can't be read or saved"), Bot.Name);
			}

			return (_cantSave ? "the list of what it spent on cards couldn't be saved, so it stopped buying until nocat.farm starts again" : Ledger.UnreadableText, true);
		}

		if (!Bot.Cfg.CraftBadges) {
			return ("\"Craft badges from card sets\" is off, so bought cards would only sit there - nothing bought", true);
		}

		if (Bot.Paused) {
			return ("it's paused - nothing bought", true);
		}

		// 1.8.3: not between two cards of one set - something else on the account (a gems action, say) shut the gate halfway
		// through Dota 2 and left 4 of its 8 cards bought. A person buying a set finishes it.
		if (!finishing && !midSet && !Gate.Open) {
			return ($"not now - {Gate.Waiting().ToEnglish()}", false);
		}

		// 1.8.2: Steam's market blocked for this account - nothing new bought; purchases the owner confirmed still finish.
		// It lasts until Steam lifts it, never "try again in a minute".
		if (!finishing && (MarketHold.Blocked(Bot) is { } market)) {
			return (market.ToEnglish(), true);
		}

		return ScamWatch.HeldBack(Bot) is { } held ? (held, false) : null;
	}

	/// <summary>1.8.2, the preview: what stops all buying before anything is read - Steam's market blocked, or scam watch holding it back. Null when nothing does.</summary>
	private string? Blocker() =>
		MarketHold.Blocked(Bot) is { } market ? market.ToEnglish() : ScamWatch.HeldBack(Bot);

	private async Task<Result> LookLockedAsync(Mode mode, CancellationToken ct) {
		Result result = new();
		bool buying = mode != Mode.Preview;
		_phoneWallet = null;   // a look reads the wallet afresh

		if (buying) {
			if (Stopped() is { } stop) {
				result.Problem = stop.Problem;
				result.Lasting = stop.Lasting;

				return result;
			}

			// Orders from before whose outcome wasn't known: asked about, and taken back if still open, before anything new.
			// Purchases the owner was asked to confirm in the Steam app: sent again with the confirmation.
			await SweepLockedAsync(ct, resume: true, result).ConfigureAwait(false);

			// One set waiting on the phone at a time: nothing new until it's confirmed, turned down or a day old.
			if (Ledger.OnPhone.Count is int waiting and > 0) {
				result.Problem = $"{waiting} card purchase(s) waiting to be confirmed in the Steam app - nothing new is bought until then";

				return result;
			}
		} else if (Blocker() is { } blocker) {
			// 1.8.2: the preview leads with what stops all buying - never a list of sets it "would buy" that it can't.
			result.Problem = blocker;
			result.Lasting = true;
			result.Blocks = true;

			return result;
		}

		// 1.8.2: a level goal under way buys by its own budget and stops at its level.
		BuyLedger.LevelGoal? goal = Ledger.Goal;
		BuyPlan plan = await BuyPlan.MakeAsync(goal == null ? Bot.Cfg : GoalCfg(goal.Target), _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct,
			stopWhenBlocked: true, boosting: Boosting(), prices: _prices, goalBudget: goal == null ? null : GoalLeft(goal), finder: Finder, quick: !buying).ConfigureAwait(false);
		result.Plan = plan;

		if (goal != null) {
			GoalSeen(goal, plan);
		}
		GemsChoice.NoteCosts(Bot.Name, plan, DateTime.UtcNow);   // 1.8.2: what the gems maths turns money into
		NoteLevelPrice(plan);

		// 1.8.2: the set finder still warming up - said once in plain words (the preview shows it in its notes).
		if (plan.WarmUp.Length == 0) {
			WarmSaid.TryRemove(Bot.Name, out _);
		} else if (buying && WarmSaid.TryAdd(Bot.Name, true)) {
			Log.Info(new Said("buying waits for the set finder: {0} of {1} cheapest sets checked", plan.WarmUpChecked, plan.WarmUpNeeded), Bot.Name);
		}

		// 1.8.2: why each cheaper level it reached wasn't planned - one debug line a look, never more.
		if (plan.Skipped.Count > 0) {
			Log.Debug($"buying cards · passed over: {string.Join("; ", plan.Skipped)}", Bot.Name);
		}

		// The preview says which whole sets it would sell instead of crafting (smart levelling) - nothing is listed here.
		if ((mode == Mode.Preview) && (plan.Problem.Length == 0) && (plan.Block == BuyBlock.None) && SetSale.Applies(Bot.Cfg, buying: true)) {
			foreach ((CardSet set, _) in plan.Complete.Take(SetSale.MaxSetsALook)) {
				plan.Sales.Add(await SetSale.WeighAsync(set, plan, _market, Bot.Cfg, ct).ConfigureAwait(false));
			}
		}

		if (plan.Problem.Length > 0) {
			result.Problem = plan.Problem;
			result.Lasting = plan.Lasting;

			return result;
		}

		if (buying && (plan.Blocked.Length > 0)) {
			result.Problem = plan.Blocked;

			// Steam not saying is asked again soon; the rest is said once with a notification, not every look.
			if (plan.Block != BuyBlock.Unknown) {
				result.Lasting = true;

				if (_saidBlocked != plan.Block) {
					_saidBlocked = plan.Block;
					Log.Attention(plan.BlockedSaid, Bot.Name);
				}
			}

			return result;
		}

		// 1.8.2: games whose cards are worth more sold than the levels they'd make - written down now (nothing bought or packed
		// for them from here), listed once the look is done.
		if (buying && DearCards.Applies(Bot.Cfg, buying: true)) {
			try {
				DearCards.Look dear = await WeighDearLockedAsync(plan, null, ct).ConfigureAwait(false);
				result.Dear.AddRange(DearCards.Note(Bot.Name, dear));
				plan.Steps.RemoveAll(s => result.Dear.Exists(d => !d.Foil && (d.App == s.App)));
			} catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested)) {
				Log.Debug($"couldn't weigh cards worth more sold than kept: {Log.Describe(e)}", Bot.Name);
			}
		}

		if (!buying || (plan.Steps.Count == 0)) {
			return result;
		}

		// A human-mode account buys a few sets a look ("Sets per look, human mode"), like somebody buying a few badges at a
		// time - the next look comes 30 to 90 minutes later in its day while money and cheap sets are left (FollowUp).
		bool unsure = false;

		foreach (BuyStep step in plan.Steps.Take(SetsALook(Bot.Cfg))) {
			bool carryOn = await BuyStepAsync(step, plan.Currency, result, ct).ConfigureAwait(false);
			unsure |= result.Problem.StartsWith(UnsureText, StringComparison.Ordinal);

			if (!carryOn) {
				break;
			}
		}

		// Steam didn't say whether a card was bought: the inventory says, once it's caught up.
		if (unsure && (await ProveAsync(justBought: true, ct).ConfigureAwait(false) > 0) && result.Problem.StartsWith(UnsureText, StringComparison.Ordinal)
			&& Ledger.All.All(static e => !e.Pending || e.OnPhone)) {
			result.Problem = "";
		}

		Said(result, plan.Currency);

		return result;
	}

	/// <summary>
	/// The orders it placed whose outcome Steam didn't say: each asked about - and taken back if still open - and the list
	/// set straight: bought at what it paid, gone off it, or still counted. Only ids it wrote down itself. Then the purchases
	/// waiting for the owner's phone: a day old, let go (nothing was bought - the order was never sent with the
	/// confirmation); otherwise, with <paramref name="resume"/>, sent again with it, as Steam's market page does.
	/// </summary>
	private async Task SweepLockedAsync(CancellationToken ct, bool resume, Result? result = null) {
		bool unsure = false;

		IReadOnlyList<BuyLedger.Entry> orders = Ledger.OpenOrders;
		bool anyPending = Ledger.All.Any(static e => e.Pending && !e.OnPhone);
		DateTime now = (orders.Count > 0) || anyPending ? await _clock(ct).ConfigureAwait(false) : default;

		// 1.8.2: a purchase Steam never said the end of in a week (no order to ask about, no copy proving it): settled as
		// bought at what it was counted at - never less spent - so it stops being asked about and leaves the list in time.
		if (anyPending && (SettleOld(Ledger, now) is int settled and not 0)) {
			if (settled < 0) {
				CantSave();

				return;
			}

			Log.Debug(new Said("{0} card purchase(s) Steam never said the end of in a week · counted as bought", settled), Bot.Name);
			orders = Ledger.OpenOrders;
		}

		foreach (BuyLedger.Entry open in OrdersToAsk(orders, now)) {
			if (ct.IsCancellationRequested) {
				return;
			}

			// Asked for hours with no answer: still asked about - and taken back if it's open - gently, a few a look, until Steam
			// says for sure (a week at most, above). It stays counted as spent meanwhile, and the inventory still decides when it
			// knows what was held before.
			if ((now - open.At >= AskFor) && (open.Before != null)) {
				unsure = true;
			}

			BuyAnswer answer;

			try {
				answer = await _market.CheckOrderAsync(open.Order!, open.Cents, open.Card, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				Log.Debug(new Said("couldn't look at buying cards: {0}", Log.Describe(e)), Bot.Name);

				continue;
			}

			if (!Record(open, answer, open.Cents, now)) {
				CantSave();

				return;
			}

			if (answer.Result == BuyResult.Bought) {
				Log.Good(new Said("bought {0} ({1}) · {2}", new BuyCard(open.Card, 0).Name, GameNames.Of(open.App), BuyPlan.Money(Paid(answer, open.Cents), open.Currency)), Bot.Name);
			}

			unsure |= answer.Result == BuyResult.Unsure;
		}

		// Orders that may have gone in without Steam giving their id: found on the market's list of open buy orders, and taken
		// back (or settled bought).
		if (!ct.IsCancellationRequested && (Ledger.Unplaced.Count > 0)) {
			await LeftoversAsync(force: false, ct).ConfigureAwait(false);
		}

		// Purchases waiting for the owner's phone: sent again (or let go after a day).
		unsure |= !ct.IsCancellationRequested && await ResumePhoneAsync(ct, resume, result).ConfigureAwait(false);

		// Still not said by Steam: the inventory decides, if the card has come.
		if (unsure && (await ProveAsync(justBought: false, ct).ConfigureAwait(false) is int proven and > 0) && (result != null)) {
			result.Proven += proven;
		}
	}

	/// <summary>Orders asked about a sweep: those still in their first hours, and a few older ones still not settled.</summary>
	public const int FreshALook = 10, StaleALook = 3;

	/// <summary>
	/// 1.8.2: the open orders a sweep asks about - the ones asked for under <see cref="AskFor"/> ago first (the oldest first,
	/// up to <see cref="FreshALook"/>), then a few older ones Steam left unsure (<see cref="StaleALook"/>, a different few each
	/// look) - so stale ones never take every turn, and are still asked about (and taken back) until Steam says for sure.
	/// </summary>
	public static List<BuyLedger.Entry> OrdersToAsk(IReadOnlyList<BuyLedger.Entry> open, DateTime now) =>
		[.. open.Where(e => now - e.At < AskFor).OrderBy(static e => e.At).Take(FreshALook),
			.. open.Where(e => (now - e.At >= AskFor) && (now - e.At < ProofFor)).OrderBy(static _ => Random.Shared.Next()).Take(StaleALook)];

	/// <summary>
	/// 1.8.2: purchases Steam never said the end of in <see cref="ProofFor"/> (not one waiting on the phone): settled as bought,
	/// at what they were counted at and when - never less spent, never counted twice. How many; -1 when the list couldn't be saved.
	/// </summary>
	public static int SettleOld(BuyLedger ledger, DateTime now) {
		int settled = 0;

		foreach (BuyLedger.Entry old in ledger.All.Where(e => e.Pending && !e.OnPhone && (now - e.At >= ProofFor))) {
			if (!ledger.Replace(old, old with { Pending = false, Confirm = null })) {
				return -1;   // couldn't be saved
			}

			settled++;
		}

		return settled;
	}

	/// <summary>
	/// The purchases waiting for the owner's phone: a day old, let go (nothing was bought - the order was never sent with the
	/// confirmation); otherwise, with <paramref name="resume"/>, sent again with it, as Steam's market page does. True when
	/// Steam left one unsure.
	/// </summary>
	private async Task<bool> ResumePhoneAsync(CancellationToken ct, bool resume, Result? result) {
		bool unsure = false;

		if (Ledger.OnPhone.Count == 0) {
			_saidTooMany = false;   // nothing waits any more: "too many waiting" may be said again
			MarketHold.Forget(Bot.Name, TooManyKey);

			return false;
		}

		if (ct.IsCancellationRequested) {
			return false;
		}

		DateTime now = await _clock(ct).ConfigureAwait(false);
		CardMarket.Wallet? wallet = null;
		int bought = 0, letGo = 0, sent = 0;

		foreach (BuyLedger.Entry phone in Ledger.OnPhone.Take(10)) {
			if (ct.IsCancellationRequested) {
				break;
			}

			// Never confirmed in a day: let go. The order was never sent with the confirmation, so nothing was bought - and it
			// never will be: a confirmation is only ever sent within its day.
			if (now - phone.At >= PhoneWait) {
				if (!Ledger.Replace(phone, null)) {
					CantSave();

					return unsure;
				}

				letGo++;

				continue;
			}

			if (!resume) {
				continue;
			}

			// The wallet, for the billing details the order carries - and whether Steam still lets it buy. Read again every
			// 10 minutes at most: rounds come every couple of minutes.
			wallet ??= await PhoneWalletAsync(ct).ConfigureAwait(false);

			if ((wallet == null) || (wallet.Currency != phone.Currency) || (BuyPlan.BlockOf(wallet) != BuyBlock.None)) {
				break;
			}

			// A few seconds between two, like somebody going down a list - never a burst.
			if (sent++ > 0) {
				await Task.Delay(SendGap(), ct).ConfigureAwait(false);
			}

			// 1.8.2: written down as sent BEFORE it goes - without its confirmation, so a crash or a stop while it's out never
			// sends it again: after a restart it's one Steam may have taken, proven from the open buy orders or the inventory
			// (LeftoversAsync, ProveAsync), and counted as spent until then. Steam's answer puts the confirmation back when the
			// owner hasn't confirmed it yet.
			BuyLedger.Entry pending = phone with { Confirm = null };

			if (!Ledger.Replace(phone, pending)) {
				CantSave();

				return unsure;
			}

			BuyAnswer answer;

			try {
				answer = await _market.ResumeAsync(new BuyOrder(phone.App, phone.Card, phone.Cents - phone.Tax, phone.Tax, wallet), phone.Confirm!, order => {
					BuyLedger.Entry noted = pending with { Order = order };

					if (!Ledger.Replace(pending, noted)) {
						_cantSave = true;
					}

					pending = noted;
				}, ct).ConfigureAwait(false);
			} catch (Exception e) {
				// Whatever broke, it may have been sent: it stays counted, never sent again with that confirmation.
				answer = new BuyAnswer(BuyResult.Unsure, phone.Cents, Log.Describe(e));
			}

			// Not sent at all (stopping, the market's hourly cap): it goes back to waiting on the phone as it was.
			if ((answer.Result == BuyResult.Waiting) && string.IsNullOrEmpty(answer.Confirm)) {
				answer = answer with { Confirm = phone.Confirm };
			}

			if (!Record(pending, answer, phone.Cents, now) || _cantSave) {
				CantSave();

				return unsure;
			}

			switch (answer.Result) {
				case BuyResult.Bought:
					bought++;
					Log.Good(new Said("bought {0} ({1}) · {2}", new BuyCard(phone.Card, 0).Name, GameNames.Of(phone.App), BuyPlan.Money(Paid(answer, phone.Cents), phone.Currency)), Bot.Name);

					break;
				case BuyResult.Refused or BuyResult.Gone:
					Log.Info(new Said("{0} not bought · not confirmed in the Steam app", new BuyCard(phone.Card, 0).Name), Bot.Name);

					break;
				case BuyResult.Unsure:
					unsure = true;
					Log.Warn(new Said("unsure if {0} was bought · counted as spent", new BuyCard(phone.Card, 0).Name), Bot.Name);

					break;
			}
		}

		if (letGo > 0) {
			Log.Info(new Said("{0} card purchase(s) not confirmed in a day · let go, nothing bought", letGo), Bot.Name);
		}

		if (bought > 0) {
			if (result != null) {
				result.Confirmed += bought;
			}

			// Confirmed on the phone and bought: a set may be whole now - badge crafting looks soon.
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete: Ledger.OnPhone.Count == 0, bought: true);
		}

		if (Ledger.OnPhone.Count == 0) {
			_saidTooMany = false;
			MarketHold.Forget(Bot.Name, TooManyKey);
		}

		return unsure;
	}

	/// <summary>
	/// 1.8.2: purchases sent with no clear answer and no order id (<see cref="BuyLedger.Unplaced"/>): an order may be sitting on
	/// the market all the same - and a new one for the card is refused while it does ("You already have an active buy
	/// order"). The account's open buy orders are read once (never more often than <see cref="LeftoverGap"/>, unless
	/// <paramref name="force"/>: Steam just said one is open), and an order for the same card at the same price - never one
	/// already written down, never the owner's at another price - is asked about and taken back: closed with nothing
	/// bought, the purchase goes off the list; bought, it's settled at what it paid. Anything unclear stays counted as spent
	/// and is looked for again next time. How many were settled.
	/// </summary>
	private async Task<int> LeftoversAsync(bool force, CancellationToken ct) {
		if (ct.IsCancellationRequested) {
			return 0;
		}

		DateTime now = await _clock(ct).ConfigureAwait(false);
		List<BuyLedger.Entry> unplaced = [.. Ledger.Unplaced.Where(e => now - e.At < ProofFor)];

		if ((unplaced.Count == 0) || (!force && (DateTime.UtcNow - _lastLeftovers < LeftoverGap))) {
			return 0;
		}

		_lastLeftovers = DateTime.UtcNow;
		IReadOnlyList<OpenBuyOrder>? open;

		try {
			open = await _market.OpenOrdersAsync(ct).ConfigureAwait(false);
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't read the open buy orders: {0}", Log.Describe(e)), Bot.Name);

			return 0;
		}

		if (open == null) {
			return 0;
		}

		HashSet<string> known = new(Ledger.All.Select(static e => e.Order).OfType<string>(), StringComparer.Ordinal);
		HashSet<string> taken = new(StringComparer.Ordinal);
		int settled = 0;

		foreach (BuyLedger.Entry entry in unplaced) {
			// The same card, at the price it was sent at (the order's own price, or with the tax on the fee), one card.
			if (open.FirstOrDefault(o => (o.Hash == entry.Card) && (o.Quantity == 1) && ((o.Price == entry.Cents - entry.Tax) || (o.Price == entry.Cents))
				&& !known.Contains(o.Id) && !taken.Contains(o.Id)) is not { } order) {
				continue;
			}

			taken.Add(order.Id);
			BuyAnswer answer;

			try {
				answer = await _market.CheckOrderAsync(order.Id, entry.Cents, entry.Card, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				Log.Debug(new Said("couldn't look at buying cards: {0}", Log.Describe(e)), Bot.Name);

				continue;
			}

			string name = new BuyCard(entry.Card, 0).Name;

			switch (answer.Result) {
				case BuyResult.Bought:
					if (!Record(entry, answer with { Order = answer.Order ?? order.Id }, entry.Cents, now)) {
						CantSave();

						return settled;
					}

					settled++;
					Log.Good(new Said("bought {0} ({1}) · {2}", name, GameNames.Of(entry.App), BuyPlan.Money(Paid(answer, entry.Cents), entry.Currency)), Bot.Name);

					break;
				case BuyResult.Gone:
					if (!Record(entry, answer, entry.Cents, now)) {
						CantSave();

						return settled;
					}

					settled++;
					Log.Info(new Said("an order for {0} was left open on the market · taken back, nothing bought", name), Bot.Name);

					break;
				default:
					// Still not clear: it stays counted as spent, and is looked for again at the next look.
					Log.Debug(new Said("buy order {0} · not settled yet · {1}", order.Id, answer.Message), Bot.Name);

					break;
			}
		}

		return settled;
	}

	/// <summary>When the open buy orders were last read for <see cref="LeftoversAsync"/>.</summary>
	private DateTime _lastLeftovers = DateTime.MinValue;

	/// <summary>The least time between two reads of the open buy orders (unless Steam just said one is open).</summary>
	public static readonly TimeSpan LeftoverGap = TimeSpan.FromMinutes(30);

	/// <summary>The gap between two purchases sent again in one round. The tests make it 0.</summary>
	public Func<TimeSpan> SendGap { get; set; } = static () => Rng.Seconds(2, 4);

	/// <summary>The wallet for sending waiting purchases again: read at most every 10 minutes. Null when Steam wouldn't say.</summary>
	private async Task<CardMarket.Wallet?> PhoneWalletAsync(CancellationToken ct) {
		if ((_phoneWallet is { } kept) && (DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(10))) {
			return kept.Wallet;
		}

		CardMarket.Wallet? wallet = await _market.WalletAsync(ct).ConfigureAwait(false);
		_phoneWallet = wallet == null ? null : (wallet, DateTime.UtcNow);

		return wallet;
	}

	/// <summary>How long a purchase waits for the owner's phone before it's let go - about a day.</summary>
	internal static readonly TimeSpan PhoneWait = TimeSpan.FromHours(24);

	/// <summary>
	/// What a purchase cost: what Steam said it paid, or what it was counted at (the order's price and the tax on the fee),
	/// whichever is more - 1.8.2: Steam's figure can leave the tax out, and less spent is never written down.
	/// </summary>
	public static long Paid(BuyAnswer answer, long price) => Math.Max(answer.Paid, price);

	/// <summary>
	/// Set the list straight after an answer: bought at what it paid, nothing spent off it, waiting on the phone with its
	/// confirmation, unsure still counted (and never sent again with a confirmation). False when it couldn't be saved.
	/// 1.8.2: bought, it counts from <paramref name="now"/> (Steam's clock) - an order found bought hours after it was asked
	/// for counts from when the money is known to have gone, never rolling off the day's or the week's limit early. The
	/// same entry, moved: never counted twice.
	/// </summary>
	private bool Record(BuyLedger.Entry pending, BuyAnswer answer, long price, DateTime now) => answer.Result switch {
		BuyResult.Bought => Ledger.Replace(pending, pending with { At = Later(pending.At, now), Cents = Paid(answer, price), Pending = false, Order = answer.Order ?? pending.Order, Confirm = null }),
		BuyResult.Gone or BuyResult.Refused or BuyResult.NotSent => Ledger.Replace(pending, null),
		BuyResult.Waiting => Ledger.Replace(pending, pending with { Confirm = answer.Confirm ?? pending.Confirm }),
		_ => Ledger.Replace(pending, pending with { Order = answer.Order ?? pending.Order, Confirm = null })
	};

	/// <summary>The later of two times - a purchase's time only ever moves forward.</summary>
	private static DateTime Later(DateTime a, DateTime b) => a >= b ? a : b;

	private void CantSave() {
		_cantSave = true;
		Stopped();   // says it, once
	}

	/// <summary>Buy one set's cards, every price checked again first. False to stop the look there (said why in the result).</summary>
	private async Task<bool> BuyStepAsync(BuyStep step, int currency, Result result, CancellationToken ct) {
		// The whole set priced again before its first card: anything dearer or gone since the plan, and the set isn't
		// started - the next set may still go. 1.8.2: by ONE search of the game (US cents at the learned rate, a little
		// margin) instead of a request per card - each card is still priced exactly right before it's bought. A search that
		// doesn't answer, or no rate yet: each card priced, as before.
		bool searched = false;

		if (WalletRate.ForDeciding(currency) is double rate) {
			IReadOnlyDictionary<string, int>? usd = null;

			try {
				usd = await _market.SetSearchAsync(step.App, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				Log.Debug(new Said("couldn't search the market: {0}", Log.Describe(e)), Bot.Name);
			}

			if ((usd != null) && step.Cards.All(c => usd.ContainsKey(c.Hash))) {
				if (step.Cards.FirstOrDefault(c => !StillCheap(usd[c.Hash], c.Price, rate)) is { } moved) {
					result.Problem = $"{step.Game}: the price of {moved.Name} moved since the plan - the set waits for the next look";

					return true;
				}

				searched = true;
			}
		}

		foreach (BuyCard card in searched ? [] : step.Cards) {
			CardMarket.Book? read = await _market.BookAsync(card.Hash, ct).ConfigureAwait(false);

			if (read != null) {
				_prices.Remember(card.Hash, read, DateTime.UtcNow);
			}

			if (read is not { } now || (now.Lowest <= 0) || (now.Currency != currency) || (now.Lowest > card.Price)) {
				result.Problem = $"{step.Game}: the price of {card.Name} moved since the plan - the set waits for the next look";

				return true;
			}
		}

		int got = 0, onPhone = 0;
		bool leftover = false;

		// 1.8.2: the wallet is read once for the set, not once a card; each card's check is that reading less what this set
		// has spent (or left waiting on the phone) since.
		CardMarket.Wallet? setWallet = null;
		long spentHere = 0;

		// The copies of each card held now (the plan's inventory read will do): a new one arriving after its order proves it
		// was bought, when Steam won't say.
		IReadOnlyDictionary<string, IReadOnlyList<string>>? held = await HeldAsync(fresh: false, ct).ConfigureAwait(false);

		foreach (BuyCard card in step.Cards) {
			// A person clicking through the market, not a list being emptied - slower still on a human-mode account.
			if (result.Bought.Count + result.Waiting.Count > 0) {
				await Task.Delay(Bot.Cfg.LegitMode ? Rng.Seconds(20, 90) : Rng.Seconds(3, 10), ct).ConfigureAwait(false);
			}

			// The rules once more, on what's true now - the last word before anything is spent. Once a set is started, its
			// day doesn't stop it halfway (1.8.3).
			if (Stopped(midSet: !ReferenceEquals(card, step.Cards[0])) is { } stop) {
				result.Problem = stop.Problem == "buying cards is off" ? "buying cards was turned off - stopped" : stop.Problem;
				result.Lasting = stop.Lasting;

				return Asked(step, onPhone, result, false);
			}

			CardMarket.Book? last = await _market.BookAsync(card.Hash, ct).ConfigureAwait(false);

			if (last != null) {
				_prices.Remember(card.Hash, last, DateTime.UtcNow);
			}

			if (last is not { } book || (book.Lowest <= 0) || (book.Currency != currency)) {
				result.Problem = $"{card.Name} isn't on the market at a price it can read now - it looks again next time";

				return Asked(step, onPhone, result, false);
			}

			if (book.Lowest > card.Price) {
				result.Problem = $"{card.Name} costs more than planned now ({BuyPlan.Money(book.Lowest, currency)}, was {BuyPlan.Money(card.Price, currency)}) - it looks again next time";

				return Asked(step, onPhone, result, false);
			}

			setWallet ??= await _market.WalletAsync(ct).ConfigureAwait(false);

			if (setWallet is not { } wallet || (wallet.Currency != currency) || (BuyPlan.BlockOf(wallet) != BuyBlock.None)) {
				result.Problem = "Steam didn't say what's in its wallet, or how its purchases are confirmed - nothing more bought";

				return Asked(step, onPhone, result, false);
			}

			// What it really costs: the card's price and the tax some countries add on Steam's fee, as Steam's page works it out.
			long tax = wallet.FeeTax(book.Lowest);
			long cost = book.Lowest + tax;
			DateTime at = await _clock(ct).ConfigureAwait(false);
			long dayLeft = BuyPlan.Limit(Bot.Cfg.BuyDailyLimit) - Ledger.SpentSince(at.AddDays(-1));
			long weekLeft = BuyPlan.Limit(Bot.Cfg.BuyWeeklyLimit) - Ledger.SpentSince(at.AddDays(-7));

			// 1.8.2: a level goal under way - its own budget instead (never over what was previewed and a tenth more).
			if (Ledger.Goal is { } goalNow) {
				dayLeft = weekLeft = GoalLeft(goalNow);
			}

			if ((cost > dayLeft) || (cost > weekLeft) || (cost > wallet.Balance - spentHere)) {
				result.Problem = "that would go over the day's or the week's limit, or more than the wallet holds - nothing more bought";

				return Asked(step, onPhone, result, false);
			}

			// Written down as spent BEFORE it's sent: whatever happens next - a stop, a disconnect, an update - the money
			// counts against the limits until a look finds out what really happened.
			BuyLedger.Entry pending = new(at, cost, currency, step.App, card.Hash, Pending: true, Key: Guid.NewGuid().ToString("N"), Tax: tax,
				Before: held == null ? null : [.. held.GetValueOrDefault(card.Hash) ?? []]);

			if (!Ledger.Add(pending)) {
				Ledger.Replace(pending, null);
				CantSave();
				result.Problem = "the list of what it spent on cards couldn't be saved, so it stopped buying until nocat.farm starts again";
				result.Lasting = true;

				return Asked(step, onPhone, result, false);
			}

			BuyAnswer answer;

			try {
				answer = await _market.BuyAsync(new BuyOrder(step.App, card.Hash, book.Lowest, tax, wallet), order => {
					BuyLedger.Entry noted = pending with { Order = order };

					if (!Ledger.Replace(pending, noted)) {
						_cantSave = true;
					}

					pending = noted;
				}, ct).ConfigureAwait(false);
			} catch (Exception e) {
				// Whatever broke, it may have been sent: it stays counted until a look finds out.
				answer = new BuyAnswer(BuyResult.Unsure, cost, Log.Describe(e));
			}

			bool saved = Record(pending, answer, cost, at);

			switch (answer.Result) {
				case BuyResult.Bought:
					long paid = Paid(answer, cost);
					result.Bought.Add((step, card, paid));
					got++;
					spentHere += paid;
					Log.Good(new Said("bought {0} ({1}) · {2}", card.Name, step.Game, BuyPlan.Money(paid, currency)), Bot.Name);

					break;
				case BuyResult.Waiting:
					// Left for the owner's phone, like a market listing: counted as spent until it's confirmed, turned down or a day old.
					result.Waiting.Add((step, card));
					onPhone++;
					spentHere += cost;

					break;
				case BuyResult.Unsure:
					Log.Warn(new Said("unsure if {0} was bought · counted as spent", card.Name), Bot.Name);
					result.Problem = $"{UnsureText} {card.Name} was bought ({answer.Message}) - counted as spent, and asked about again at the next look";

					break;
				case BuyResult.Gone:
					result.Problem = $"{card.Name} was bought by someone else first - it looks again next time";

					break;
				case BuyResult.NotSent:
					result.Problem = $"buying {card.Name} couldn't be sent to Steam - nothing bought, it looks again soon";

					break;
				case BuyResult.Refused when answer.Leftover:
					// An order for this card is already open on the market: nothing bought by this one - and an earlier one
					// of its own, sent with no clear answer, is found and taken back below.
					Log.Info(new Said("{0}: a buy order for it is already open on the market · its own is taken back, one you placed is left alone", card.Name), Bot.Name);
					result.Problem = $"Steam says a buy order for {card.Name} is already open on the market - its own is taken back; one you placed yourself is left alone";
					leftover = true;

					break;
				case BuyResult.Refused when TooMany(answer):
					// Steam won't take more purchases until the ones waiting are confirmed: none is tried until they're
					// confirmed or let go (the next look waits for them anyway). Said once.
					if (!_saidTooMany) {
						_saidTooMany = true;

						// 1.8.2: said once until they're confirmed - a restart doesn't say it again.
						if (MarketHold.Once(Bot.Name, TooManyKey)) {
							Log.Info(new Said("more cards wait · confirm the ones in the Steam app first"), Bot.Name);
						}
					}

					result.Problem = "Steam takes no more purchases until the ones waiting are confirmed in the Steam app - nothing more is tried until then";
					result.Lasting = true;

					break;
				default:
					// 1.8.2: not allowed to use the market - the market hold, told once by itself. Any other lasting refusal is said
					// once a week (kept on disk): every look said it again, and a restart said it again.
					if (MarketHold.Classify(answer.Message) == MarketHold.Refusal.NotAllowed) {
						MarketHold.Refused(Bot, MarketHold.Refusal.NotAllowed, DateTime.UtcNow, selling: false);
					} else if (MarketHold.Once(Bot.Name, "buy:" + answer.Message, TimeSpan.FromDays(7))) {
						Log.Warn(answer.Message.Length > 0 ? new Said("buying {0} turned down · {1}", card.Name, answer.Message) : new Said("buying {0} turned down", card.Name), Bot.Name);
					} else {
						Log.Debug($"buying {card.Name} turned down again: {answer.Message}", Bot.Name);
					}

					result.Problem = $"Steam turned it down{(answer.Message.Length > 0 ? $": {answer.Message}" : "")}";
					result.Lasting = true;

					break;
			}

			if (!saved || _cantSave) {
				CantSave();
				result.Problem = "the list of what it spent on cards couldn't be saved, so it stopped buying until nocat.farm starts again";
				result.Lasting = true;

				return Asked(step, onPhone, result, false);
			}

			// Steam said an order for the card is open: read the open buy orders now (once), and take back its own.
			if (leftover) {
				await LeftoversAsync(force: true, ct).ConfigureAwait(false);
			}

			if ((answer.Result is not (BuyResult.Bought or BuyResult.Waiting)) || ct.IsCancellationRequested) {
				return Asked(step, onPhone, result, false);
			}
		}

		if (got == step.Cards.Count) {
			result.Finished.Add(step);

			// The set is whole: badge crafting looks soon rather than at its daily round.
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete: true, bought: true);
		}

		// A set waiting on the phone is the only one: the next set waits until it's confirmed.
		return Asked(step, onPhone, result, onPhone == 0);
	}

	/// <summary>
	/// 1.8.2: a card the game's search lists (US cents, 0 nobody selling) still about its planned price in the wallet's money:
	/// listed, and at the learned rate no more than a twentieth (and a cent) over - the rate is only learned.
	/// </summary>
	public static bool StillCheap(int usdCents, long planned, double rate) =>
		(usdCents > 0) && (WalletRate.ToWallet(usdCents, rate) <= (planned * 21 / 20) + 1);

	/// <summary>The start of a look's problem when Steam didn't say whether a card was bought.</summary>
	private const string UnsureText = "Steam didn't say for sure whether";

	/// <summary>
	/// Steam turning a purchase down because too many wait for a confirmation ("You have tried too many purchases without
	/// confirmation"): said so, or said no while purchases of this account wait on the phone - in any language.
	/// </summary>
	private bool TooMany(BuyAnswer answer) =>
		(Ledger.OnPhone.Count > 0) || answer.Message.Contains("without confirmation", StringComparison.OrdinalIgnoreCase);

	/// <summary>The copies of every item held, by market name - null when the inventory couldn't be read (nothing is proven on it then).</summary>
	private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> HeldAsync(bool fresh, CancellationToken ct) {
		try {
			return await _market.AssetsAsync(fresh, ct).ConfigureAwait(false);
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't read the inventory: {0}", Log.Describe(e)), Bot.Name);

			return null;
		}
	}

	/// <summary>
	/// Purchases Steam never said the end of (counted as spent all along): the inventory decides. A copy of the card that
	/// wasn't held before its order, and isn't already proof of another purchase, proves it was bought - settled at what it
	/// was counted at, never counted twice. A purchase of the same card that Steam did say was bought since takes one of the
	/// new copies first. Only its own orders' cards, only for a week, and gently: one inventory read a minute at most, a
	/// fresh purchase given a little time to show up first. How many were proven.
	/// </summary>
	private async Task<int> ProveAsync(bool justBought, CancellationToken ct) {
		if (ct.IsCancellationRequested || (DateTime.UtcNow - _lastProof < ProofGap)) {
			return 0;
		}

		int proven = 0;

		try {
			DateTime now = await _clock(ct).ConfigureAwait(false);
			List<BuyLedger.Entry> open = [.. Ledger.All.Where(e => e is { Pending: true, Before: not null, Asset: null } && !e.OnPhone && (now - e.At < ProofFor)).OrderBy(static e => e.At)];

			if (open.Count == 0) {
				return 0;
			}

			if (justBought) {
				await Task.Delay(ProofWait(), ct).ConfigureAwait(false);
			}

			_lastProof = DateTime.UtcNow;

			if (await HeldAsync(fresh: true, ct).ConfigureAwait(false) is not { } held) {
				return 0;
			}

			List<BuyLedger.Entry> all = [.. Ledger.All];
			HashSet<string> claimed = new(all.Select(static e => e.Asset).OfType<string>(), StringComparer.Ordinal);

			foreach (BuyLedger.Entry entry in open) {
				HashSet<string> before = new(entry.Before!, StringComparer.Ordinal);
				List<string> arrived = [.. (held.GetValueOrDefault(entry.Card) ?? []).Where(id => !before.Contains(id) && !claimed.Contains(id))];
				int others = all.Count(e => (e != entry) && (e.Card == entry.Card) && !e.Pending && (e.Asset == null) && (e.At >= entry.At.AddMinutes(-10)));

				if (arrived.Count <= others) {
					continue;
				}

				string proof = arrived.OrderByDescending(static id => id.Length).ThenByDescending(static id => id, StringComparer.Ordinal).First();
				claimed.Add(proof);

				if (!Ledger.Replace(entry, entry with { At = Later(entry.At, now), Pending = false, Asset = proof, Confirm = null })) {
					CantSave();

					break;
				}

				proven++;
				Log.Good(new Said("bought {0} ({1}) · {2}", new BuyCard(entry.Card, 0).Name, GameNames.Of(entry.App), BuyPlan.Money(entry.Cents, entry.Currency)), Bot.Name);
				Log.Debug(new Said("{0} · found in the inventory after its order", new BuyCard(entry.Card, 0).Name), Bot.Name);
			}
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't read the inventory: {0}", Log.Describe(e)), Bot.Name);
		} catch (OperationCanceledException) {
			// stopping: the next look looks again
		}

		if (proven > 0) {
			// A set may be whole now - badge crafting looks soon.
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete: Ledger.OnPhone.Count == 0, bought: true);
		}

		return proven;
	}

	/// <summary>
	/// Purchases of this set left for the owner's phone: said once for the set, where it can't be missed - like market
	/// listings - and the look stops there (one set waiting at a time). Hands back <paramref name="carryOn"/>.
	/// </summary>
	private bool Asked(BuyStep step, int onPhone, Result result, bool carryOn) {
		if (onPhone == 0) {
			return carryOn;
		}

		Log.Attention(new Said("confirm the card purchase in the Steam app · {0} card(s) for {1}", onPhone, step.Game), Bot.Name);

		if (result.Problem.Length == 0) {
			result.Problem = $"{onPhone} card purchase(s) for {step.Game} to confirm in the Steam app on your phone - bought once confirmed, at the next look; let go after a day";
		}

		return false;
	}

	/// <summary>One line and one notification for a look that bought something - not one per card.</summary>
	private void Said(Result result, int currency) {
		if (result.Bought.Count == 0) {
			return;
		}

		long spent = result.Bought.Sum(static b => b.Paid);
		// 1.8.2: each card was its own line as it was bought - in the window this round-up came at the end of the look, often
		// after a craft that ran meanwhile, and read as a second purchase. The log file and the notifications keep it.
		Said line = new("bought {0} card(s) · {1} · {2} set(s) to craft", result.Bought.Count, BuyPlan.Money(spent, currency), result.Finished.Count);
		Log.Debug(line, Bot.Name);
		Log.Notify?.Invoke(NotifyKind.Earning, Bot.Name, line.ToString());
	}
}
