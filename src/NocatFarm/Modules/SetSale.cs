using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Smart levelling (1.8.2): before badge crafting crafts a normal badge, whether the set is worth more sold than crafted.
/// Crafting makes 1 level. Sold - each card listed a cent under the cheapest, after Steam's fee - the money may buy many
/// more levels through buying cards (the same ranking: the cheapest next levels first, at today's prices). Only when that
/// clearly wins is the set listed instead: at least 2 levels, or 1 level with a quarter of the money left over.
/// </summary>
/// <remarks>
/// Careful by design: only with "Buy missing cards" on (and "Sell sets worth more than the levels they'd buy", on by
/// default); only normal sets, one level at a time - a foil set is always crafted; only cards that sold at least
/// <see cref="MinSales"/> times in the last day, so a listing doesn't hang; never for much less than it was weighed at.
/// Every listing waits for the owner's phone like any listing. Its cards may go above "Never sell above" - this path only.
/// The game isn't bought back for <see cref="NotBoughtFor"/>.
/// </remarks>
public static partial class SetSale {
	/// <summary>Each card of the set sold at least this many times in the last day (Steam's priceoverview volume).</summary>
	public const int MinSales = 10;

	/// <summary>Sets weighed per look - each is a market request per card.</summary>
	public const int MaxSetsALook = 3;

	/// <summary>A game whose set was sold isn't bought back for this long.</summary>
	public static readonly TimeSpan NotBoughtFor = TimeSpan.FromDays(30);

	/// <summary>One card as it would be listed: the cheapest listing now, what a buyer pays for ours, and what the account gets.</summary>
	public sealed record SalePrice(string Hash, int Lowest, int Buyer, int Receive);

	/// <summary>
	/// The word on one set: sold or crafted, what selling brings in after Steam's fee, the levels that buys counted at
	/// today's prices, about how many in all, and why - in plain English for the console and the preview.
	/// </summary>
	/// <param name="Unsure">Not decided: a price or the market didn't answer - crafted only when its cards aren't known to be worth much (<see cref="ChooseAsync"/>).</param>
	/// <param name="Top">The dearest card's cheapest listing seen while weighing it, in the wallet's currency (0: none seen).</param>
	public sealed record Verdict(uint App, string Game, int Currency, bool Sell, long Proceeds, int Levels, int About, string Why, IReadOnlyList<SalePrice> Prices,
		bool Unsure = false, long Top = 0) {
		/// <summary>What selling brings in, written out ("4.97 CAD").</summary>
		public string Money => BuyPlan.Money(Proceeds, Currency);

		/// <summary><see cref="Why"/> for the log, translated.</summary>
		public Said Reason { get; init; } = new(Why);
	}

	/// <summary>A set crafted: decided (<paramref name="unsure"/> false), or not known for sure.</summary>
	public static Verdict Crafted(uint app, string game, int currency, Said reason, bool unsure = false, long top = 0) =>
		new(app, game, currency, false, 0, 0, 0, reason.ToEnglish(), [], unsure, top) { Reason = reason };

	/// <summary>Whether sets are weighed at all: the setting, badge crafting, and buying cards on to spend the money.</summary>
	public static bool Applies(BotConfig cfg, bool buying) => cfg.SellSetsForLevels && cfg.CraftBadges && buying;

	/// <summary>Only a normal badge, one level: a foil set is always crafted, and so is a game with more than one set ready.</summary>
	public static bool Considered(int border, int levels) => (border == 0) && (levels <= 1);

	/// <summary>
	/// The decision, on its own. <paramref name="cards"/>: every card of the set with its price now (null: no answer).
	/// <paramref name="levelCosts"/>: what each next badge level buying cards would buy costs, cheapest first - this set's
	/// own game left out. Sold only when it clearly wins: at least 2 levels, or 1 with a quarter of the money left over.
	/// </summary>
	public static Verdict Decide(uint app, string game, int currency, IReadOnlyList<(string Hash, MarketQuote? Quote)> cards, IEnumerable<long> levelCosts, int dumpPct) {
		long top = cards.Where(c => c.Quote?.Currency == currency).Select(static c => (long) (c.Quote!.Lowest ?? 0)).DefaultIfEmpty(0).Max();
		Verdict Craft(Said why, bool unsure = false) => Crafted(app, game, currency, why, unsure, top);

		if ((currency <= 0) || (cards.Count == 0)) {
			return Craft(new Said("no price in its wallet's currency"), unsure: true);
		}

		List<SalePrice> prices = [];

		foreach ((string hash, MarketQuote? quote) in cards) {
			string name = new BuyCard(hash, 0).Name;

			if ((quote == null) || (quote.Currency != currency) || (quote.Lowest is not int lowest)) {
				return Craft(new Said("no market price for {0} right now", name), unsure: true);
			}

			if (quote.Sales < MinSales) {
				return Craft(new Said("{0} sold only {1} time(s) in the last day", name, quote.Sales));
			}

			(int? buyer, _) = ListingPrice.For(quote.Price, 3, dumpPct);

			if (buyer is not int pays) {
				return Craft(new Said("{0}: its price isn't clear right now", name), unsure: true);
			}

			int receive = Seller.ReceiveFor(pays);

			if (receive <= 0) {
				return Craft(new Said("{0} pays nothing on the market", name));
			}

			prices.Add(new SalePrice(hash, lowest, pays, receive));
		}

		long proceeds = prices.Sum(static p => (long) p.Receive);
		long left = proceeds;
		int levels = 0;
		long last = 0;
		bool more = false;

		foreach (long cost in levelCosts.Where(static c => c > 0).Order()) {
			last = cost;

			if (cost > left) {
				more = true;

				break;
			}

			left -= cost;
			levels++;
		}

		// Past the levels priced today, the rest at the dearest of them: about how many in all (never counted on to decide).
		int about = levels + ((!more && (last > 0)) ? (int) Math.Min(1000, left / last) : 0);
		bool sell = (levels >= 2) || ((levels == 1) && (left * 4 >= proceeds));
		string money = BuyPlan.Money(proceeds, currency);
		Said reason = sell ? new Said("{0} buys about {1} levels", money, about)
			: levels == 0 ? new Said("set worth {0} buys no badge level today", money) : new Said("set worth {0} buys only {1} level", money, levels);

		return new Verdict(app, game, currency, sell, proceeds, levels, about, reason.ToEnglish(), prices, false, top) { Reason = reason };
	}

	/// <summary>One set weighed: each card's price now, against what the plan's ranked levels cost (this game left out).</summary>
	public static async Task<Verdict> WeighAsync(CardSet set, BuyPlan plan, ICardMarket market, BotConfig cfg, CancellationToken ct) {
		List<(string, MarketQuote?)> cards = [];

		foreach (string hash in set.Cards) {
			MarketQuote? quote = await market.QuoteAsync(hash, plan.Currency, ct).ConfigureAwait(false);
			cards.Add((hash, quote));

			if (quote == null) {
				break;   // the market stopped answering: crafted, not guessed
			}
		}

		if (cards.Count < set.Cards.Count) {
			return Decide(set.App, set.Game, plan.Currency, [.. cards, .. set.Cards.Skip(cards.Count).Select(static h => (h, (MarketQuote?) null))], [], 0);
		}

		return Decide(set.App, set.Game, plan.Currency, cards, plan.Ranked.Where(s => s.App != set.App).Select(static s => s.Cost), Math.Clamp(cfg.SpareDumpPct, 0, 95));
	}

	/// <summary>Every marketable copy of each normal card of the game, by market name - asset ids, as a listing takes them.</summary>
	internal static Dictionary<string, List<ulong>> Copies(InventoryContents inventory, uint app) {
		string game = app.ToString(CultureInfo.InvariantCulture);
		Dictionary<string, List<ulong>> copies = new(StringComparer.Ordinal);

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			if (!type.EndsWith("Trading Card", StringComparison.OrdinalIgnoreCase) || type.Contains("Foil", StringComparison.OrdinalIgnoreCase)
				|| (InventoryContents.Text(d, "marketable") != "1") || (InventoryContents.Text(d, "market_fee_app") != game)
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || (id == 0)) {
				continue;
			}

			string hash = InventoryContents.Text(d, "market_hash_name");

			if (!copies.TryGetValue(hash, out List<ulong>? ids)) {
				copies[hash] = ids = [];
			}

			ids.Add(id);
		}

		return copies;
	}

	/// <summary>The listing offers for a set sold: one copy of each card. Null when a card isn't in the inventory to list.</summary>
	public static List<Seller.Offer>? Offers(Verdict verdict, IReadOnlyDictionary<string, List<ulong>> copies) {
		List<Seller.Offer> offers = [];

		foreach (SalePrice p in verdict.Prices) {
			if (!copies.TryGetValue(p.Hash, out List<ulong>? ids) || (ids.Count == 0)) {
				return null;
			}

			offers.Add(new Seller.Offer(ids[0], verdict.Game, Seller.CardName(p.Hash), p.Lowest, p.Buyer, p.Receive, p.Hash));
		}

		return offers;
	}

	/// <summary>
	/// Whether a card priced again right before it's listed still pays about what the set was weighed at - never for more
	/// than a tenth less. Under that it's kept, and the set is weighed again at the next look.
	/// </summary>
	public static bool StillPays(Seller.Offer weighed, Seller.Offer now) => now.YouGetCents * 10L >= weighed.YouGetCents * 9L;

	/// <summary>
	/// List a set sold instead of crafted: one copy of each card, a cent under the cheapest, priced again right before each
	/// goes up. Its cards may go above "Never sell above" - this path only. Each listing waits for the owner's phone like
	/// any listing (or this account's authenticator). How many went up.
	/// </summary>
	public static async Task<int> ListAsync(Bot bot, Verdict verdict, InventoryContents inventory, CancellationToken ct) {
		if (Offers(verdict, Copies(inventory, verdict.App)) is not { } offers) {
			return 0;
		}

		int floor = Math.Clamp(bot.Cfg.SpareRelistFloorPct, 1, 100), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);

		async Task<(Seller.Offer? Offer, Said Why)> Reprice(Seller.Offer offer, CancellationToken token) {
			(Seller.Offer? now, Said why) = await Seller.RepriceAsync(offer, verdict.Currency, 3, dump, null, null, token, floorPct: floor,
				ours: OurListings.UpFor(OurListings.Load(bot.Name), offer.Hash)).ConfigureAwait(false);

			return (now != null) && !StillPays(offer, now) ? (null, new Said("its price fell since the set was weighed - not listed")) : (now, why);
		}

		int listed = 0;
		await Seller.SellAsync(bot, offers, ct, reprice: Reprice, onListed: _ => listed++).ConfigureAwait(false);

		return listed;
	}
}
