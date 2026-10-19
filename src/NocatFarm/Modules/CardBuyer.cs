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
/// away; GET /market/getbuyorderstatus/ says what it bought, and /market/cancelbuyorder/ takes one back that wasn't.</item>
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
public sealed record BuyAnswer(BuyResult Result, long Paid, string Message = "", string? Order = null, string? Confirm = null);

/// <summary>What buying cards needs from Steam. A fake in the tests - no test ever reaches Steam.</summary>
public interface ICardMarket {
	/// <summary>The wallet, read now. Null when Steam wouldn't say.</summary>
	Task<CardMarket.Wallet?> WalletAsync(CancellationToken ct);

	/// <summary>Each game badge's normal level (not foil). Null when Steam wouldn't say.</summary>
	Task<IReadOnlyDictionary<uint, int>?> BadgeLevelsAsync(CancellationToken ct);

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

	/// <summary>An order it placed earlier whose outcome wasn't known: what it bought, or - still open - taken back.</summary>
	Task<BuyAnswer> CheckOrderAsync(string orderId, long price, CancellationToken ct);

	/// <summary>
	/// A purchase left for the owner to confirm in the Steam app: the order sent again with that confirmation, as Steam's
	/// market page does. Confirmed, the order goes in and is asked about and taken back like any; not confirmed yet is
	/// Waiting; anything Steam says no to (turned down, too old) is Refused. Spends money, at most the order's price.
	/// </summary>
	Task<BuyAnswer> ResumeAsync(BuyOrder order, string confirmation, Action<string>? placed, CancellationToken ct);

	/// <summary>A card's price, last day's sales and how many sold, in the wallet's currency (priceoverview) - for selling a set. Null when the market didn't answer.</summary>
	Task<MarketQuote?> QuoteAsync(string hash, int currency, CancellationToken ct);
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

/// <summary>One card to buy, at the cheapest listing's price (the buyer's price, fees in).</summary>
public sealed record BuyCard(string Hash, long Price) {
	/// <summary>The card's name, without the "730-" in front of its market name.</summary>
	public string Name => Hash.IndexOf('-') is int dash and > 0 ? Hash[(dash + 1)..] : Hash;
}

/// <summary>
/// One set to buy: the missing cards of a badge's next level (<paramref name="FullSet"/> false), or a whole set for a game
/// with none of it yet. Either way, one badge level: 100 XP once crafted.
/// </summary>
public sealed record BuyStep(uint App, string Game, bool FullSet, int Level, int SetSize, IReadOnlyList<BuyCard> Cards) {
	public long Cost => Cards.Sum(static c => c.Price);

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

	/// <summary>Why something wasn't bought, in plain words (English - console replies stay English).</summary>
	public List<string> Notes { get; } = [];

	/// <summary>
	/// The cheapest whole sets on the market for badges it can still level, from the set finder (<see cref="SetFinder"/>) -
	/// what the preview shows beside what it would buy. Only shown: every card is priced again before anything is bought.
	/// </summary>
	public List<SetFinder.Row> Market { get; } = [];

	/// <summary>Why it couldn't look at all - empty when it could.</summary>
	public string Problem { get; set; } = "";

	/// <summary>The problem won't clear by itself (a setting, the spending list) - not worth asking again soon.</summary>
	public bool Lasting { get; set; }

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

	/// <summary>Market reads one plan may make: sets, and cards' prices.</summary>
	internal const int MaxSetReads = 12, MaxBookReads = 30, MaxFullSetGames = 3;

	/// <summary>Whole sets from the set finder priced in one look, at most - the cheapest first.</summary>
	internal const int MaxFinderSets = 4;

	/// <summary>The cheapest sets on the market the preview shows.</summary>
	internal const int MarketShown = 5;

	/// <summary>A normal game badge's top level.</summary>
	internal const int MaxLevel = 5;

	/// <summary>Money as typed (1.50) in cents (150), clamped.</summary>
	public static long Cents(float money) => Math.Clamp(SpareRules.Cents(Math.Clamp(money, 0f, 1_000_000f)), 0, 100_000_000);

	/// <summary>"0.35 CAD".</summary>
	public static string Money(long cents, int currency) => Spare.Money(cents, currency);

	/// <summary>
	/// The plan - smart levelling. What could be bought: for each badge the account is levelling (cards of its set held, badge
	/// under level 5), the cards its next level still needs; and, with "Buy the cheapest full sets" on, whole sets for games
	/// it has none of - the set finder's cheapest on the market (<see cref="SetFinder"/>), any game, played or not. All of them ranked together by what the next badge level costs, cheapest first - the most XP for the
	/// money - so a whole new set for 0.40 goes before a started badge whose one missing card costs 2.00. A set is only ever
	/// planned whole: every missing card on the market, and all of it inside what may be spent now. One that costs more than
	/// a day's or a week's limit is passed over (said why); one that doesn't fit what's left of today stops the plan there.
	/// Games automatic booster packs is working on are left to the packs.
	/// </summary>
	/// <param name="nowUtc">Now on Steam's clock - the limits' windows move with it, not with this PC's.</param>
	/// <param name="stopWhenBlocked">Stop once the wallet says Steam won't let it buy by itself - for a look that would buy.</param>
	/// <param name="boosting">Games automatic booster packs is saving gems for or has a pack waiting for (<see cref="BoosterPick.Busy"/>): not bought.</param>
	/// <param name="heldCards">The cards held, from an inventory the caller has just read - not read again.</param>
	/// <param name="finder">Where the cheapest whole sets on the market come from: the app's set finder unless the checks give their own.</param>
	public static async Task<BuyPlan> MakeAsync(BotConfig cfg, ICardMarket market, ICardSwaps swaps, BuyLedger ledger, Bot? bot, DateTime nowUtc, CancellationToken ct,
		bool stopWhenBlocked = false, IReadOnlyCollection<uint>? boosting = null, IReadOnlyDictionary<uint, Dictionary<string, int>>? heldCards = null,
		SetFinder? finder = null) {
		BuyPlan plan = new() {
			DayLimit = Cents(cfg.BuyDailyLimit),
			WeekLimit = Cents(cfg.BuyWeeklyLimit)
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

			return plan;
		}

		plan.Block = BlockOf(wallet);

		if (stopWhenBlocked && (plan.Block != BuyBlock.None)) {
			return plan;
		}

		plan.Room = Math.Max(0, Math.Min(Math.Min(plan.DayLimit - plan.DaySpent, plan.WeekLimit - plan.WeekSpent), wallet.Balance));

		if (await market.BadgeLevelsAsync(ct).ConfigureAwait(false) is not { } levels) {
			plan.Problem = "Steam wouldn't say what its badges are";

			return plan;
		}

		// The set finder's cheapest whole sets, for badges it can still level - on this PC's clock, like the finder's own.
		finder ??= SetFinder.Shared;
		IReadOnlyList<SetFinder.KnownSet> known = finder.Known(DateTime.UtcNow);
		plan.Market.AddRange(finder.Cheapest(levels, plan.Currency, MarketShown, DateTime.UtcNow));

		// The cards held: as the caller just read them (badge crafting), or read now.
		if ((heldCards ?? await market.CardsAsync(ct).ConfigureAwait(false)) is not { } held) {
			plan.Problem = "couldn't read all of its inventory";

			return plan;
		}

		long left = plan.Room;
		long most = Math.Min(plan.DayLimit, plan.WeekLimit);

		if (most <= 0) {
			plan.Notes.Add("a limit is 0, so it buys nothing");

			return plan;
		}
		int books = 0;
		bool stopped = false;
		Dictionary<string, CardMarket.Book> read = new(StringComparer.Ordinal);

		// The price of every card in a list, read now - null when the market stopped answering, or this look has read
		// enough prices (said in the notes either way).
		async Task<List<(string Hash, CardMarket.Book Book)>?> PricesAsync(IEnumerable<string> hashes) {
			List<(string, CardMarket.Book)> got = [];

			foreach (string hash in hashes) {
				if (!read.TryGetValue(hash, out CardMarket.Book? book)) {
					if (books >= MaxBookReads) {
						plan.Notes.Add("that's enough prices for one look - the rest waits for the next one");

						return null;
					}

					books++;

					if (await market.BookAsync(hash, ct).ConfigureAwait(false) is not { } fresh) {
						plan.Notes.Add("the market stopped answering - the rest waits for the next look");

						return null;
					}

					// Prices in another currency than the wallet's are never compared with its limits.
					if ((fresh.Lowest > 0) && (fresh.Currency != plan.Currency)) {
						plan.Notes.Add($"the market answered in {MarketQuotes.Code(fresh.Currency)}, not the wallet's {MarketQuotes.Code(plan.Currency)} - nothing is planned on that");

						return null;
					}

					read[hash] = book = fresh;
				}

				got.Add((hash, book));
			}

			return got;
		}

		// Whether a set of this price can go in now: true to take it, false to pass it over, null to stop the plan here.
		bool? Fits(string game, long cost) {
			if (cost > most) {
				plan.Notes.Add($"{game}: the cards it needs cost {Money(cost, plan.Currency)}, more than a {(plan.DayLimit <= plan.WeekLimit ? "day's" : "week's")} limit ({Money(most, plan.Currency)})");

				return false;
			}

			if (cost > left) {
				plan.Notes.Add($"{game} is next ({Money(cost, plan.Currency)}) - {(cost > wallet.Balance - (plan.Room - left) ? "the wallet doesn't hold enough" : "waiting for room in the day's or the week's limit")}");

				return null;
			}

			return true;
		}

		// ── what could be bought: the missing cards of badges it's levelling, and (when on) whole sets for new games ──
		// Games with cards held and a badge under level 5, the most different cards held first - each set read costs a market
		// search, so the likeliest are read first.
		List<uint> levelling = [.. held.Where(h => (levels.GetValueOrDefault(h.Key) < MaxLevel) && h.Value.Values.Any(static n => n > 0))
			.OrderByDescending(static h => h.Value.Count(static c => c.Value > 0)).ThenBy(static h => h.Key).Select(static h => h.Key)];
		List<(CardSet Set, int Level, List<string> Missing, bool Full)> candidates = [];
		HashSet<uint> packs = boosting is null ? [] : [.. boosting];
		HashSet<uint> sold = ledger.SoldSince(nowUtc - SetSale.NotBoughtFor);
		int sets = 0;

		// A game automatic booster packs is saving gems for, or has a pack waiting to open for: its cards come from the packs.
		// A set it just sold instead of crafting isn't bought back either.
		bool Boosting(CardSet set) {
			if (sold.Contains(set.App)) {
				plan.Notes.Add($"{set.Game}: its set was just sold instead of crafted - not bought back");

				return true;
			}

			if (!packs.Contains(set.App)) {
				return false;
			}

			plan.Notes.Add($"{set.Game}: booster packs are working on it - its cards come from the packs");

			return true;
		}

		foreach (uint app in levelling) {
			if (sets >= MaxSetReads) {
				break;
			}

			sets++;

			if (await market.SetAsync(app, ct).ConfigureAwait(false) is not { } set) {
				plan.Notes.Add("the market stopped answering - the rest waits for the next look");
				stopped = true;

				break;
			}

			if (set.Cards.Count == 0) {
				continue;   // not known whole - nothing is planned on half a set
			}

			Dictionary<string, int> cards = held[app];
			int level = levels.GetValueOrDefault(app);
			int complete = set.Cards.All(c => cards.GetValueOrDefault(c) > 0) ? set.Cards.Min(c => cards.GetValueOrDefault(c)) : 0;

			// A whole set held: badge crafting's - or sold instead, when that buys more levels (SetSale).
			if ((complete > 0) && (level < MaxLevel)) {
				plan.Complete.Add((set, level));
			}

			// The sets held already take it to level 5 - nothing to buy.
			if (complete >= MaxLevel - level) {
				continue;
			}

			List<string> missing = [.. set.Cards.Where(c => cards.GetValueOrDefault(c) <= complete)];

			if ((missing.Count > 0) && (missing.Count < set.Cards.Count) && !Boosting(set)) {
				candidates.Add((set, level + complete, missing, false));
			}
		}

		if (cfg.BuyFullSets && !stopped && (known.Count > 0)) {
			// The set finder's: whole sets for any game - played, owned or not - the cheapest on the market first. Each in the
			// wallet's money at the rate learned from the market, only to leave out what costs more than a limit before a
			// single price is read; every card is priced for real below, in the wallet's currency, and again before buying.
			double? rate = WalletRate.ForDeciding(plan.Currency);
			int taken = 0;

			foreach (SetFinder.KnownSet k in known) {
				if (taken >= MaxFinderSets) {
					break;
				}

				// Games it holds cards of are the missing cards' business, above; a maxed badge has nothing to give; a card
				// with fewer than 2 on the market is no whole set to buy.
				if ((levels.GetValueOrDefault(k.App) >= MaxLevel) || (held.TryGetValue(k.App, out Dictionary<string, int>? some) && some.Values.Any(static n => n > 0))
					|| (k.Depth < 2)) {
					continue;
				}

				// The cheapest first: once one costs more than a day's or a week's limit, so does every one after it.
				if ((rate is double r) && (WalletRate.ToWallet(k.UsdCents, r) is int about) && (about > most)) {
					if (taken == 0) {
						plan.Notes.Add($"the cheapest whole set on the market ({k.Game}, about {Money(about, plan.Currency)}) costs more than a {(plan.DayLimit <= plan.WeekLimit ? "day's" : "week's")} limit ({Money(most, plan.Currency)})");
					}

					break;
				}

				CardSet set = k.ToCardSet();

				if (Boosting(set)) {
					continue;
				}

				candidates.Add((set, levels.GetValueOrDefault(k.App), [.. set.Cards], true));
				taken++;
			}
		} else if (cfg.BuyFullSets && !stopped) {
			// The set finder hasn't a whole set yet (it has only just started): the games of the cheapest single cards.
			if (await market.CheapestGamesAsync(ct).ConfigureAwait(false) is not { } cheapest) {
				plan.Notes.Add("the market didn't say which sets are cheapest - full sets wait for the next look");
			} else {
				List<CardSet> found = [];
				int looked = 0;

				foreach (uint app in cheapest) {
					// Games it holds cards of are the missing cards' business, above; a maxed badge has nothing to give.
					if ((looked >= MaxSetReads) || (levels.GetValueOrDefault(app) >= MaxLevel) || (held.TryGetValue(app, out Dictionary<string, int>? some) && some.Values.Any(static n => n > 0))) {
						continue;
					}

					looked++;

					if (await market.SetAsync(app, ct).ConfigureAwait(false) is not { } set) {
						break;
					}

					// Every card listed at the last search: a set with a card nobody sells can't be bought whole.
					if ((set.Cards.Count > 0) && set.Cards.All(c => set.Usd.GetValueOrDefault(c) > 0) && !Boosting(set)) {
						found.Add(set);
					}
				}

				// The few cheapest by the search, to be priced in the wallet's own money.
				candidates.AddRange(found.OrderBy(static s => s.UsdCents).ThenBy(static s => s.App).Take(MaxFullSetGames)
					.Select(s => (s, levels.GetValueOrDefault(s.App), s.Cards.ToList(), true)));
			}
		}

		// ── each priced now, the likeliest cheapest first (by the market's last search, in US cents - only to order them) ──
		List<BuyStep> priced = [];

		foreach ((CardSet set, int level, List<string> missing, bool full) in candidates.OrderBy(static c => c.Missing.Sum(h => c.Set.Usd.GetValueOrDefault(h)))
			.ThenBy(static c => c.Full).ThenBy(static c => c.Set.App)) {
			if (await PricesAsync(missing).ConfigureAwait(false) is not { } prices) {
				break;
			}

			if (full) {
				if (prices.FirstOrDefault(p => p.Book.Listings < 2) is { Hash: not null } thin) {
					plan.Notes.Add($"{set.Game}: left out - {new BuyCard(thin.Hash, 0).Name} has fewer than 2 listings");

					continue;
				}

				if (prices.FirstOrDefault(p => p.Book.Dumped) is { Hash: not null } dumped) {
					plan.Notes.Add($"{set.Game}: left out - one cheap listing of {new BuyCard(dumped.Hash, 0).Name} makes the set look cheaper than it is");

					continue;
				}
			} else {
				if (prices.FirstOrDefault(p => p.Book.Lowest <= 0) is { Hash: not null } none) {
					plan.Notes.Add($"{set.Game}: nobody is selling {new BuyCard(none.Hash, 0).Name} right now");

					continue;
				}

				// A card a swap would bring for less waits for the swap - and so does its set: never half a set.
				List<string> swapped = [];

				foreach ((string hash, CardMarket.Book book) in prices) {
					if ((await swaps.SwapCostAsync(bot, set.App, hash, ct).ConfigureAwait(false) is long swap) && (swap < book.Lowest)) {
						swapped.Add(new BuyCard(hash, 0).Name);
					}
				}

				if (swapped.Count > 0) {
					plan.Notes.Add($"{set.Game}: waiting for a card swap for {string.Join(", ", swapped)} - cheaper than buying");

					continue;
				}
			}

			priced.Add(new BuyStep(set.App, set.Game, full, level, set.Cards.Count, [.. prices.Select(static p => new BuyCard(p.Hash, p.Book.Lowest))]));
		}

		// ── smart levelling: whatever makes the next badge level cheapest goes first - the most XP for the money ──
		// A started badge one card short goes after a whole new set that costs less; at the same price, the started badge.
		plan.Ranked.AddRange(priced.OrderBy(static s => s.Cost).ThenBy(static s => s.FullSet).ThenBy(static s => s.App));

		foreach (BuyStep step in plan.Ranked) {
			if (Fits(step.Game, step.Cost) is not { } fits) {
				break;
			}

			if (fits) {
				plan.Steps.Add(step);
				left -= step.Cost;
			}
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
	public sealed record Entry(DateTime At, long Cents, int Currency, uint App, string Card, bool Pending = false, string? Order = null, string? Key = null,
		string? Confirm = null, long Tax = 0) {
		/// <summary>Waiting for the owner to confirm it in the Steam app: no order yet, a confirmation to send it with.</summary>
		public bool OnPhone => Pending && string.IsNullOrEmpty(Order) && !string.IsNullOrEmpty(Confirm);
	}

	/// <summary>A set sold instead of crafted (<see cref="SetSale"/>): its cards aren't bought back for a while.</summary>
	public sealed record SoldSet(DateTime At, uint App);

	internal sealed class Stored {
		public bool Ok { get; set; }
		public List<Entry> Entries { get; set; } = [];
		public List<SoldSet> Sold { get; set; } = [];
	}

	private List<Entry>? _entries;
	private List<SoldSet> _sold = [];
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
			AtomicFile.Write(path, JsonSerializer.Serialize(new Stored { Ok = _ok, Entries = _entries ?? [], Sold = _sold }));
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
/// once confirmed, let go when turned down or a day old), and no other set is started meanwhile. What it buys is ranked
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

		/// <summary>Why it couldn't look or carry on (English). Empty when all went well.</summary>
		public string Problem { get; set; } = "";

		/// <summary>The problem won't clear by itself (a setting, a block, Steam saying no) - the next look is at the usual time.</summary>
		public bool Lasting { get; set; }
	}

	/// <summary>One look at a time per account - across every copy of the module, so a restore can't make two buyers.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Busy = new(StringComparer.OrdinalIgnoreCase);

	private readonly ICardMarket _market = market ?? new SteamCardMarket(bot);
	private ICardSwaps Swaps => swaps ?? SwapCost.Current;
	private readonly Func<CancellationToken, Task<DateTime>> _clock = clock ?? SteamClockAsync;
	private HumanGate? _gate;
	private Said _status = new("");
	private BuyBlock _saidBlocked = BuyBlock.None;
	private bool _saidStopped;

	/// <summary>The spending list couldn't be saved: nothing more is bought until nocat.farm starts again.</summary>
	private bool _cantSave;

	/// <summary>When the next look is - from "Look every (hours)" as it's set now.</summary>
	internal LookTimer Timer { get; } = new(() => Math.Clamp(bot.Cfg.BuyCheckHours, 1, 168), 1.0, 1.2);

	public override string Name => "buying cards";
	public override string Status => Bot.Cfg.BuyCards ? _status : "";

	public BuyLedger Ledger => BuyLedger.Of(Bot.Name);

	/// <summary>The gate a human-mode account's buying waits on: its own day (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>Whether this account buys: the switch and its go-ahead together.</summary>
	public static bool On(Bot bot) => bot.Cfg.BuyCards && BuyLedger.Of(bot.Name).Ok;

	/// <summary>Now on Steam's clock (this PC's until Steam's is known) - what the limits' windows go by.</summary>
	internal static async Task<DateTime> SteamClockAsync(CancellationToken ct) =>
		DateTimeOffset.FromUnixTimeSeconds(await Confirmations.SteamTimeAsync(ct).ConfigureAwait(false)).UtcDateTime;

	private SemaphoreSlim BusyGate => Busy.GetOrAdd(Bot.Name, static _ => new SemaphoreSlim(1, 1));

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Minutes(10, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && Timer.Due(DateTime.UtcNow)) {
				bool buying = On(Bot) && Gate.Open;

				// Off, or outside its day: an order it placed whose outcome isn't known is still asked about and taken back
				// (and a purchase waiting on the phone too long is let go).
				if (buying || (Ledger.OpenOrders.Count > 0) || (Ledger.OnPhone.Count > 0)) {
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

			if (!await Sleep(Rng.Minutes(2, 5), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>Look, and - outside a preview, while it's on and the day allows - buy what the plan says. One look at a time.</summary>
	public async Task<Result> LookAsync(Mode mode, CancellationToken ct) {
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			return await LookLockedAsync(mode, ct).ConfigureAwait(false);
		} finally {
			BusyGate.Release();
		}
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
	/// For badge crafting (smart levelling, <see cref="SetSale"/>): each of these games' whole normal set weighed - sold, or
	/// crafted - against what the money would buy, ranked as buying ranks it, at today's prices. Nothing is bought or listed
	/// here. One verdict per game (up to <see cref="SetSale.MaxSetsALook"/>): when the market or the wallet didn't say, it's
	/// marked not known for sure (Unsure); when Steam won't let this account buy (the money couldn't be spent), crafted.
	/// <paramref name="inventory"/>: the inventory badge crafting just read - its cards are used, not read again a second
	/// later (Steam refuses that as a duplicate request).
	/// </summary>
	public async Task<IReadOnlyList<SetSale.Verdict>> WeighSalesAsync(IReadOnlyList<uint> apps, CancellationToken ct, InventoryContents? inventory = null) {
		List<uint> asked = [.. apps.Distinct().Take(SetSale.MaxSetsALook)];
		string Game(uint app) => inventory == null ? GameNames.Of(app) : SetSale.GameOf(app, inventory);
		await BusyGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			BuyPlan plan = await BuyPlan.MakeAsync(Bot.Cfg, _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct,
				boosting: BoosterPick.Busy(Bot), heldCards: inventory == null ? null : LevelPlanner.NormalCards(inventory)).ConfigureAwait(false);

			if ((plan.Problem.Length > 0) || (plan.Block == BuyBlock.Unknown)) {
				Log.Debug($"weighing sets: {(plan.Problem.Length > 0 ? plan.Problem : plan.Blocked)}", Bot.Name);

				return [.. asked.Select(a => SetSale.Crafted(a, Game(a), plan.Currency, new Said("couldn't price its cards"), unsure: true))];
			}

			if (plan.Block != BuyBlock.None) {
				return [.. asked.Select(a => SetSale.Crafted(a, Game(a), plan.Currency, new Said("Steam won't let it buy cards, so the money couldn't be spent")))];
			}

			List<SetSale.Verdict> weighed = [];

			foreach (uint app in asked) {
				CardSet? set = plan.Complete.Select(static c => c.Set).FirstOrDefault(s => s.App == app) ?? await _market.SetAsync(app, ct).ConfigureAwait(false);

				weighed.Add(set is { Cards.Count: > 0 }
					? await SetSale.WeighAsync(set, plan, _market, Bot.Cfg, ct).ConfigureAwait(false)
					: SetSale.Crafted(app, set?.Game ?? Game(app), plan.Currency, new Said("its set isn't known whole on the market"), unsure: true));
			}

			return weighed;
		} finally {
			BusyGate.Release();
		}
	}

	/// <summary>Why a look can't buy now, before anything is read from the market - or null when it may. Lasting when it won't clear by itself.</summary>
	private (string Problem, bool Lasting)? Stopped() {
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

		if (!Gate.Open) {
			return ($"not now - {Gate.Waiting().ToEnglish()}", false);
		}

		return ScamWatch.HeldBack(Bot) is { } held ? (held, false) : null;
	}

	private async Task<Result> LookLockedAsync(Mode mode, CancellationToken ct) {
		Result result = new();
		bool buying = mode != Mode.Preview;

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
		}

		BuyPlan plan = await BuyPlan.MakeAsync(Bot.Cfg, _market, Swaps, Ledger, Bot, await _clock(ct).ConfigureAwait(false), ct, stopWhenBlocked: buying,
			boosting: BoosterPick.Busy(Bot)).ConfigureAwait(false);
		result.Plan = plan;

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

		if (!buying || (plan.Steps.Count == 0)) {
			return result;
		}

		// A human-mode account buys one set a look, like somebody finishing one badge at a time.
		foreach (BuyStep step in Bot.Cfg.LegitMode ? plan.Steps.Take(1) : plan.Steps) {
			if (!await BuyStepAsync(step, plan.Currency, result, ct).ConfigureAwait(false)) {
				break;
			}
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
		foreach (BuyLedger.Entry open in Ledger.OpenOrders.Take(10)) {
			if (ct.IsCancellationRequested) {
				return;
			}

			BuyAnswer answer;

			try {
				answer = await _market.CheckOrderAsync(open.Order!, open.Cents, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				Log.Debug(new Said("couldn't look at buying cards: {0}", Log.Describe(e)), Bot.Name);

				continue;
			}

			if (!Record(open, answer, open.Cents)) {
				CantSave();

				return;
			}

			if (answer.Result == BuyResult.Bought) {
				Log.Good(new Said("bought {0} · {1} · {2}", new BuyCard(open.Card, 0).Name, GameNames.Of(open.App), BuyPlan.Money(Paid(answer, open.Cents), open.Currency)), Bot.Name);
			}
		}

		if ((Ledger.OnPhone.Count == 0) || ct.IsCancellationRequested) {
			return;
		}

		DateTime now = await _clock(ct).ConfigureAwait(false);
		CardMarket.Wallet? wallet = null;
		int bought = 0, letGo = 0;

		foreach (BuyLedger.Entry phone in Ledger.OnPhone.Take(10)) {
			if (ct.IsCancellationRequested) {
				break;
			}

			// Never confirmed in a day: let go. The order was never sent with the confirmation, so nothing was bought - and it
			// never will be: a confirmation is only ever sent within its day.
			if (now - phone.At >= PhoneWait) {
				if (!Ledger.Replace(phone, null)) {
					CantSave();

					return;
				}

				letGo++;

				continue;
			}

			if (!resume) {
				continue;
			}

			// The wallet once, for the billing details the order carries - and whether Steam still lets it buy.
			wallet ??= await _market.WalletAsync(ct).ConfigureAwait(false);

			if ((wallet == null) || (wallet.Currency != phone.Currency) || (BuyPlan.BlockOf(wallet) != BuyBlock.None)) {
				break;
			}

			BuyLedger.Entry pending = phone;
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

			if (!Record(pending, answer, phone.Cents) || _cantSave) {
				CantSave();

				return;
			}

			switch (answer.Result) {
				case BuyResult.Bought:
					bought++;
					Log.Good(new Said("bought {0} · {1} · {2}", new BuyCard(phone.Card, 0).Name, GameNames.Of(phone.App), BuyPlan.Money(Paid(answer, phone.Cents), phone.Currency)), Bot.Name);

					break;
				case BuyResult.Refused or BuyResult.Gone:
					Log.Info(new Said("{0} not bought · not confirmed in the Steam app", new BuyCard(phone.Card, 0).Name), Bot.Name);

					break;
				case BuyResult.Unsure:
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
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete: Ledger.OnPhone.Count == 0);
		}
	}

	/// <summary>How long a purchase waits for the owner's phone before it's let go - about a day.</summary>
	internal static readonly TimeSpan PhoneWait = TimeSpan.FromHours(24);

	/// <summary>What a purchase cost: what Steam said it paid, else the order's price.</summary>
	private static long Paid(BuyAnswer answer, long price) => answer.Paid > 0 ? answer.Paid : price;

	/// <summary>
	/// Set the list straight after an answer: bought at what it paid, nothing spent off it, waiting on the phone with its
	/// confirmation, unsure still counted (and never sent again with a confirmation). False when it couldn't be saved.
	/// </summary>
	private bool Record(BuyLedger.Entry pending, BuyAnswer answer, long price) => answer.Result switch {
		BuyResult.Bought => Ledger.Replace(pending, pending with { Cents = Paid(answer, price), Pending = false, Order = answer.Order ?? pending.Order, Confirm = null }),
		BuyResult.Gone or BuyResult.Refused or BuyResult.NotSent => Ledger.Replace(pending, null),
		BuyResult.Waiting => Ledger.Replace(pending, pending with { Confirm = answer.Confirm ?? pending.Confirm }),
		_ => Ledger.Replace(pending, pending with { Order = answer.Order ?? pending.Order, Confirm = null })
	};

	private void CantSave() {
		_cantSave = true;
		Stopped();   // says it, once
	}

	/// <summary>Buy one set's cards, every price checked again first. False to stop the look there (said why in the result).</summary>
	private async Task<bool> BuyStepAsync(BuyStep step, int currency, Result result, CancellationToken ct) {
		// The whole set priced again before its first card: anything dearer or gone since the plan, and the set isn't
		// started - the next set may still go.
		foreach (BuyCard card in step.Cards) {
			if (await _market.BookAsync(card.Hash, ct).ConfigureAwait(false) is not { } now || (now.Lowest <= 0) || (now.Currency != currency) || (now.Lowest > card.Price)) {
				result.Problem = $"{step.Game}: the price of {card.Name} moved since the plan - the set waits for the next look";

				return true;
			}
		}

		int got = 0, onPhone = 0;

		foreach (BuyCard card in step.Cards) {
			// A person clicking through the market, not a list being emptied - slower still on a human-mode account.
			if (result.Bought.Count + result.Waiting.Count > 0) {
				await Task.Delay(Bot.Cfg.LegitMode ? Rng.Seconds(20, 90) : Rng.Seconds(3, 10), ct).ConfigureAwait(false);
			}

			// The rules once more, on what's true now - the last word before anything is spent.
			if (Stopped() is { } stop) {
				result.Problem = stop.Problem == "buying cards is off" ? "buying cards was turned off - stopped" : stop.Problem;
				result.Lasting = stop.Lasting;

				return Asked(step, onPhone, result, false);
			}

			if (await _market.BookAsync(card.Hash, ct).ConfigureAwait(false) is not { } book || (book.Lowest <= 0) || (book.Currency != currency)) {
				result.Problem = $"{card.Name} isn't on the market at a price it can read now - it looks again next time";

				return Asked(step, onPhone, result, false);
			}

			if (book.Lowest > card.Price) {
				result.Problem = $"{card.Name} costs more than planned now ({BuyPlan.Money(book.Lowest, currency)}, was {BuyPlan.Money(card.Price, currency)}) - it looks again next time";

				return Asked(step, onPhone, result, false);
			}

			if (await _market.WalletAsync(ct).ConfigureAwait(false) is not { } wallet || (wallet.Currency != currency) || (BuyPlan.BlockOf(wallet) != BuyBlock.None)) {
				result.Problem = "Steam didn't say what's in its wallet, or how its purchases are confirmed - nothing more bought";

				return Asked(step, onPhone, result, false);
			}

			// What it really costs: the card's price and the tax some countries add on Steam's fee, as Steam's page works it out.
			long tax = wallet.FeeTax(book.Lowest);
			long cost = book.Lowest + tax;
			DateTime at = await _clock(ct).ConfigureAwait(false);
			long dayLeft = BuyPlan.Cents(Bot.Cfg.BuyDailyLimit) - Ledger.SpentSince(at.AddDays(-1));
			long weekLeft = BuyPlan.Cents(Bot.Cfg.BuyWeeklyLimit) - Ledger.SpentSince(at.AddDays(-7));

			if ((cost > dayLeft) || (cost > weekLeft) || (cost > wallet.Balance)) {
				result.Problem = "that would go over the day's or the week's limit, or more than the wallet holds - nothing more bought";

				return Asked(step, onPhone, result, false);
			}

			// Written down as spent BEFORE it's sent: whatever happens next - a stop, a disconnect, an update - the money
			// counts against the limits until a look finds out what really happened.
			BuyLedger.Entry pending = new(at, cost, currency, step.App, card.Hash, Pending: true, Key: Guid.NewGuid().ToString("N"), Tax: tax);

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

			bool saved = Record(pending, answer, cost);

			switch (answer.Result) {
				case BuyResult.Bought:
					long paid = Paid(answer, cost);
					result.Bought.Add((step, card, paid));
					got++;
					Log.Good(new Said("bought {0} · {1} · {2}", card.Name, step.Game, BuyPlan.Money(paid, currency)), Bot.Name);

					break;
				case BuyResult.Waiting:
					// Left for the owner's phone, like a market listing: counted as spent until it's confirmed, turned down or a day old.
					result.Waiting.Add((step, card));
					onPhone++;

					break;
				case BuyResult.Unsure:
					Log.Warn(new Said("unsure if {0} was bought · counted as spent", card.Name), Bot.Name);
					result.Problem = $"Steam didn't say for sure whether {card.Name} was bought ({answer.Message}) - counted as spent, and asked about again at the next look";

					break;
				case BuyResult.Gone:
					result.Problem = $"{card.Name} was bought by someone else first - it looks again next time";

					break;
				case BuyResult.NotSent:
					result.Problem = $"buying {card.Name} couldn't be sent to Steam - nothing bought, it looks again soon";

					break;
				default:
					Log.Warn(answer.Message.Length > 0 ? new Said("buying {0} turned down · {1}", card.Name, answer.Message) : new Said("buying {0} turned down", card.Name), Bot.Name);
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

			if ((answer.Result is not (BuyResult.Bought or BuyResult.Waiting)) || ct.IsCancellationRequested) {
				return Asked(step, onPhone, result, false);
			}
		}

		if (got == step.Cards.Count) {
			result.Finished.Add(step);

			// The set is whole: badge crafting looks soon rather than at its daily round.
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete: true);
		}

		// A set waiting on the phone is the only one: the next set waits until it's confirmed.
		return Asked(step, onPhone, result, onPhone == 0);
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
		Log.Reward(new Said("bought {0} card(s) · {1} · {2} set(s) to craft", result.Bought.Count, BuyPlan.Money(spent, currency), result.Finished.Count),
			Bot.Name, Topic.Items);
	}
}
