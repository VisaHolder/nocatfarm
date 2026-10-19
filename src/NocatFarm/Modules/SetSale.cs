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
/// default); a foil set too (its badge is one level), and a game's several sets together; only cards that sold at least
/// <see cref="MinSales"/> times in the last day, so a listing doesn't hang - for <see cref="WaitAtMost"/>, then a slow sale
/// beats none; never for much less than it was weighed at. With Steam not letting the account buy by itself, a set worth
/// more sold waits rather than being crafted, and the owner is told once.
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

	/// <summary>
	/// 1.8.2: a set worth more sold whose card hardly sells waits at most this long - then it's listed all the same, a cent
	/// under the cheapest: a slow sale beats one that never happens.
	/// </summary>
	public static readonly TimeSpan WaitAtMost = TimeSpan.FromDays(2);

	/// <summary>1.8.2: a foil card's market name - the normal card's with " (Foil)" after it, as Steam names them.</summary>
	public static string FoilHash(string hash) => hash + " (Foil)";

	/// <summary>1.8.2: a game's foil set, from its normal one: the same cards, foil. One level only - a foil badge has one.</summary>
	public static CardSet FoilSet(CardSet set) => new(set.App, set.Game, [.. set.Cards.Select(FoilHash)], new Dictionary<string, int>());

	/// <summary>One card as it would be listed: the cheapest listing now, what a buyer pays for ours, and what the account gets.</summary>
	public sealed record SalePrice(string Hash, int Lowest, int Buyer, int Receive);

	/// <summary>
	/// The word on one set: sold or crafted, what selling brings in after Steam's fee, the levels that buys counted at
	/// today's prices, about how many in all, and why - in plain English for the console and the preview.
	/// </summary>
	/// <param name="Unsure">Not decided: a price or the market didn't answer - crafted only when its cards aren't known to be worth much (<see cref="ChooseAsync"/>).</param>
	/// <param name="Top">The dearest card's cheapest listing seen while weighing it, in the wallet's currency (0: none seen).</param>
	/// <param name="Hold">1.8.2: neither sold nor crafted - worth more than crafting is (it would buy 2 levels or more), but a
	/// card of it hardly sells (for at most <see cref="WaitAtMost"/>), or Steam won't let the account buy by itself; it
	/// waits for the next look rather than being crafted.</param>
	/// <param name="Sets">1.8.2: how many of the game's sets are weighed together - each a level crafted, one copy of each card sold.</param>
	/// <param name="Foil">1.8.2: the game's foil set (one level only).</param>
	/// <param name="Slow">1.8.2: held because a card of it hardly sells - how long it has waited counts.</param>
	public sealed record Verdict(uint App, string Game, int Currency, bool Sell, long Proceeds, int Levels, int About, string Why, IReadOnlyList<SalePrice> Prices,
		bool Unsure = false, long Top = 0, bool Hold = false, int Sets = 1, bool Foil = false, bool Slow = false) {
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

	/// <summary>
	/// Which finished sets are weighed: 1.8.2, every one - a foil set too (its badge is one level), and a game with more than
	/// one set ready (all of them together: each a level crafted, or a copy of each card sold).
	/// </summary>
	public static bool Considered(int border, int levels) => (levels >= 1) && ((border == 0) || (levels == 1));

	/// <summary>
	/// The decision, on its own. <paramref name="cards"/>: every card of the set with its price now (null: no answer).
	/// <paramref name="levelCosts"/>: what each next badge level buying cards would buy costs, cheapest first - this set's
	/// own game left out. Sold only when it clearly wins: at least 2 levels, or 1 with a quarter of the money left over.
	/// A card that sold fewer than <see cref="MinSales"/> times in the last day: crafted when selling would buy 1 level at
	/// most; worth more than that, it waits (Hold) - a listing could hang, and a craft would throw the money away - until it
	/// has <paramref name="waited"/> <see cref="WaitAtMost"/>: then it's sold all the same. 1.8.2: <paramref name="sets"/> sets
	/// of the game weighed together - sold only when the money buys at least a level more than crafting them all makes, and
	/// <see cref="GemsChoice.MarginPct"/> percent more.
	/// </summary>
	/// <param name="minReceive">1.8.2: "Only sell if you get at least" - a card that would pay less: the set is crafted.</param>
	/// <param name="gemsWorth">1.8.2: what a card's gems are worth (null: not known) - a card that wouldn't pay a cent more: crafted.</param>
	/// <param name="paid">1.8.2: the most paid for one copy of a card bought for a badge (0: none) - selling it for less counts that loss.</param>
	public static Verdict Decide(uint app, string game, int currency, IReadOnlyList<(string Hash, MarketQuote? Quote)> cards, IEnumerable<long> levelCosts, int dumpPct,
		int sets = 1, bool waited = false, int minReceive = 0, Func<string, decimal?>? gemsWorth = null, Func<string, long>? paid = null) {
		sets = Math.Max(1, sets);
		long top = cards.Where(c => c.Quote?.Currency == currency).Select(static c => (long) (c.Quote!.Lowest ?? 0)).DefaultIfEmpty(0).Max();
		Verdict Craft(Said why, bool unsure = false) => Crafted(app, game, currency, why, unsure, top);

		if ((currency <= 0) || (cards.Count == 0)) {
			return Craft(new Said("no price in its wallet's currency"), unsure: true);
		}

		List<SalePrice> prices = [];
		(string Name, int Sales)? thin = null;

		foreach ((string hash, MarketQuote? quote) in cards) {
			string name = new BuyCard(hash, 0).Name;

			if ((quote == null) || (quote.Currency != currency) || (quote.Lowest is not int lowest)) {
				return Craft(new Said("no market price for {0} right now", name), unsure: true);
			}

			// Hardly selling: weighed all the same - whether it's crafted depends on what the set is worth.
			if ((quote.Sales < MinSales) && (thin == null)) {
				thin = (name, quote.Sales);
			}

			(int? buyer, _) = ListingPrice.For(quote.Price, 3, dumpPct);

			if (buyer is not int pays) {
				return Craft(new Said("{0}: its price isn't clear right now", name), unsure: true);
			}

			int receive = Seller.ReceiveFor(pays);

			if (receive <= 0) {
				return Craft(new Said("{0} pays nothing on the market", name));
			}

			// 1.8.2: never under "Only sell if you get at least", never for what its gems are worth or less - crafted instead.
			if (receive < minReceive) {
				return Craft(new Said("{0} would pay you {1}, under your {2} minimum", name, BuyPlan.Money(receive, currency), BuyPlan.Money(minReceive, currency)));
			}

			if ((gemsWorth?.Invoke(hash) is decimal gems) && (gems > 0) && !Spare.SellingPays(gems, receive)) {
				return Craft(new Said("{0} would pay you {1}, not more than its gems are worth", name, BuyPlan.Money(receive, currency)));
			}

			prices.Add(new SalePrice(hash, lowest, pays, receive));
		}

		long proceeds = prices.Sum(static p => (long) p.Receive) * sets;

		// 1.8.2: a card bought for a badge sold for less than it paid: that loss counts against what selling buys.
		long loss = paid == null ? 0 : prices.Sum(p => Math.Max(0, paid(p.Hash) - p.Receive)) * sets;
		long left = proceeds - loss;
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
		string money = BuyPlan.Money(proceeds, currency);

		// A card that hardly sells: a set worth 1 level at most is crafted as before; worth more, it waits - never crafted
		// away, never listed to hang.
		if ((thin is { } few) && !waited) {
			Said slow = new("{0} sold only {1} time(s) in the last day", few.Name, few.Sales);

			if (levels < sets + 1) {
				return Craft(slow) with { Sets = sets };
			}

			Said held = new("{0} sold only {1} time(s) in the last day - waiting, the set is worth {2} ({3} levels)", few.Name, few.Sales, money, levels);

			return new Verdict(app, game, currency, false, proceeds, levels, about, held.ToEnglish(), prices, false, top, Hold: true, Sets: sets, Slow: true) { Reason = held };
		}

		bool sell = sets == 1 ? (levels >= 2) || ((levels == 1) && (left * 4 >= proceeds))
			: (levels >= sets + 1) && (levels * 100L >= sets * (100L + GemsChoice.MarginPct));
		Said reason = sell && (thin is { } slowCard) ? new Said("{0} buys about {1} levels · {2} hardly sells, but the set has waited long enough - listed all the same", money, about, slowCard.Name)
			: sell ? new Said("{0} buys about {1} levels", money, about)
			: sets > 1 ? new Said("{0} sets worth {1} buy only {2} levels", sets, money, levels)
			: levels == 0 ? new Said("set worth {0} buys no badge level today", money) : new Said("set worth {0} buys only {1} level", money, levels);

		return new Verdict(app, game, currency, sell, proceeds, levels, about, reason.ToEnglish(), prices, false, top, Sets: sets) { Reason = reason };
	}

	/// <summary>
	/// One set weighed: each card's price now, against what the plan's ranked levels cost (this game left out). 1.8.2:
	/// <paramref name="sets"/> of them together; <paramref name="foil"/>, the game's foil set; <paramref name="waitingSince"/>,
	/// since when it has waited for a card that hardly sells (null: it hasn't).
	/// </summary>
	public static async Task<Verdict> WeighAsync(CardSet set, BuyPlan plan, ICardMarket market, BotConfig cfg, CancellationToken ct, int sets = 1, bool foil = false,
		DateTime? waitingSince = null, Func<string, long>? paid = null, Func<string, decimal?>? gemsWorth = null) {
		Verdict Mark(Verdict v) => v with { Sets = Math.Max(1, sets), Foil = foil };

		List<(string, MarketQuote?)> cards = [];

		foreach (string hash in set.Cards) {
			MarketQuote? quote = await market.QuoteAsync(hash, plan.Currency, ct).ConfigureAwait(false);
			cards.Add((hash, quote));

			if (quote == null) {
				break;   // the market stopped answering: crafted, not guessed
			}
		}

		if (cards.Count < set.Cards.Count) {
			return Mark(Decide(set.App, set.Game, plan.Currency, [.. cards, .. set.Cards.Skip(cards.Count).Select(static h => (h, (MarketQuote?) null))], [], 0));
		}

		// Only the levels past what the wallet already pays for: the money from the set buys those, not the ones it has money for.
		// 1.8.2: the levels priced and the set finder's cheapest known past them (BuyPlan.Levels).
		List<long> ranked = [.. plan.LevelCosts.Where(l => l.App != set.App).Select(static l => l.Cost).Where(static c => c > 0)];
		List<long> more = BuyPlan.BeyondWallet(ranked, plan.Wallet);
		bool waited = (waitingSince is DateTime since) && (DateTime.UtcNow - since >= WaitAtMost);
		Verdict v = Mark(Decide(set.App, set.Game, plan.Currency, cards, more, Math.Clamp(cfg.SpareDumpPct, 0, 95), sets, waited, SpareRules.From(cfg).MinSaleCents, gemsWorth, paid));

		if (v.Unsure || v.Hold) {
			return v;
		}

		// The plan's prices stopped part way: what the money buys isn't known for sure - never sold or crafted on half a list.
		if (plan.Short) {
			return Mark(Crafted(set.App, set.Game, plan.Currency, new Said("the market stopped answering before what its money buys was priced"), unsure: true, top: v.Top));
		}

		// The wallet already pays for every level priced today: what more money would buy isn't priced - not sure either.
		return (ranked.Count > 0) && (more.Count == 0)
			? Mark(Crafted(set.App, set.Game, plan.Currency, new Said("its wallet already pays for every level priced today"), unsure: true, top: v.Top))
			: v;
	}

	/// <summary>
	/// Every marketable copy of each normal card of the game - or, <paramref name="foil"/>, of each foil one - by market name:
	/// asset ids, as a listing takes them.
	/// </summary>
	public static Dictionary<string, List<ulong>> Copies(InventoryContents inventory, uint app, bool foil = false) {
		string game = app.ToString(CultureInfo.InvariantCulture);
		Dictionary<string, List<ulong>> copies = new(StringComparer.Ordinal);

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			if (!type.EndsWith("Trading Card", StringComparison.OrdinalIgnoreCase) || (type.Contains("Foil", StringComparison.OrdinalIgnoreCase) != foil)
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

	/// <summary>
	/// The listing offers for a set sold: one copy of each card - for each of the sets sold (<see cref="Verdict.Sets"/>). Null
	/// when a card isn't in the inventory to list as many times.
	/// </summary>
	public static List<Seller.Offer>? Offers(Verdict verdict, IReadOnlyDictionary<string, List<ulong>> copies) {
		List<Seller.Offer> offers = [];
		int sets = Math.Max(1, verdict.Sets);

		foreach (SalePrice p in verdict.Prices) {
			if (!copies.TryGetValue(p.Hash, out List<ulong>? ids) || (ids.Count < sets)) {
				return null;
			}

			offers.AddRange(ids.Take(sets).Select(id => new Seller.Offer(id, verdict.Game, Seller.CardName(p.Hash), p.Lowest, p.Buyer, p.Receive, p.Hash)));
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
		if (Offers(verdict, Copies(inventory, verdict.App, verdict.Foil)) is not { } offers) {
			return 0;
		}

		int floor = Math.Clamp(bot.Cfg.SpareRelistFloorPct, 1, 100), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);
		SpareRules rules = SpareRules.From(bot.Cfg);

		// 1.8.2: like every listing (Seller.OwnRule) - never under "Only sell if you get at least", never for what its gems are
		// worth or less (a card whose gem value isn't known is kept).
		Dictionary<ulong, decimal?> gems = await GemsForListingAsync(bot, offers, inventory, verdict.Currency, rules, ct).ConfigureAwait(false);

		async Task<(Seller.Offer? Offer, Said Why)> Reprice(Seller.Offer offer, CancellationToken token) {
			if (gems.GetValueOrDefault(offer.AssetId) is not decimal worth) {
				return (null, new Said("its gem value isn't known yet - kept"));
			}

			(Seller.Offer? now, Said why) = await Seller.RepriceAsync(offer, verdict.Currency, 3, dump, null, null, token, floorPct: floor,
				ours: OurListings.UpFor(OurListings.Load(bot.Name), offer.Hash), minReceive: rules.MinSaleCents, gemsWorth: worth > 0 ? worth : null).ConfigureAwait(false);

			return (now != null) && !StillPays(offer, now) ? (null, new Said("its price fell since the set was weighed - not listed")) : (now, why);
		}

		// 1.8.2: a human-mode account lists a set only in its day (you not playing on it), a few minutes between cards - it
		// listed one card every 20 to 60 seconds at any hour. One set a sweep (BadgeCraft.SellInsteadAsync).
		HumanGate? gate = bot.Cfg.LegitMode ? BotManager.ModuleOf<BadgeCraft>(bot)?.Gate ?? HumanGate.OwnDay(bot) : null;
		int listed = 0;
		await Seller.SellAsync(bot, offers, ct, mayAct: gate == null ? null : () => gate.Open, reprice: Reprice, onListed: _ => listed++,
			pause: gate == null ? null : static t => Task.Delay(Rng.HumanMinutes(1, 4), t)).ConfigureAwait(false);

		if ((listed > 0) && (gate != null)) {
			gate.Acted();
		}

		return listed;
	}

	/// <summary>Gem values one listing run may ask Steam for.</summary>
	private const int GemAsks = 3;

	/// <summary>
	/// 1.8.2: what each offer's gems are worth, for listing a set or a dear game's cards: by asset id - 0 for an item with no
	/// gems, null when its gem value or a sack's price isn't known (that card is kept, as Seller.OwnRule keeps it). A gem value
	/// not known yet is asked a few a run; a sack's price older than "Trust a price for" is read again.
	/// </summary>
	internal static async Task<Dictionary<ulong, decimal?>> GemsForListingAsync(Bot bot, IReadOnlyList<Seller.Offer> offers, InventoryContents inventory, int currency,
		SpareRules rules, CancellationToken ct) {
		Dictionary<ulong, SpareItem> items = [];

		foreach (SpareItem i in Spare.Read(inventory)) {
			items[i.AssetId] = i;
		}

		Dictionary<string, int?> gooOf = new(StringComparer.Ordinal);
		Dictionary<ulong, decimal?> worth = [];
		LiveSpareSteam? steam = null;
		int asks = 0;
		MarketPrice? sack = null;
		bool sackRead = false;

		foreach (Seller.Offer o in offers) {
			if (!items.TryGetValue(o.AssetId, out SpareItem? item)) {
				worth[o.AssetId] = null;

				continue;
			}

			if (item.GooType <= 0) {
				worth[o.AssetId] = 0;

				continue;
			}

			if (!gooOf.TryGetValue(item.GooKey, out int? goo)) {
				goo = SteamGems.KnownValue(item) ?? (asks++ < GemAsks ? await (steam ??= new LiveSpareSteam(bot)).GemValueAsync(item, ct).ConfigureAwait(false) : null);
				gooOf[item.GooKey] = goo;
			}

			if (goo is not int g) {
				worth[o.AssetId] = null;

				continue;
			}

			if (g <= 0) {
				worth[o.AssetId] = 0;

				continue;
			}

			if (!sackRead) {
				sackRead = true;

				if ((SparePrices.Sack(currency) is not { Cents: > 0 } held) || (DateTime.UtcNow - held.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))) {
					await SparePrices.PriceSackAsync(currency, ct).ConfigureAwait(false);
				}

				sack = SparePrices.SackPays(currency, rules.DumpPct);
			}

			worth[o.AssetId] = (sack is { Cents: > 0 } s) && (DateTime.UtcNow - s.At <= TimeSpan.FromHours(rules.PriceMaxAgeHours)) ? Spare.GemsWorth(g, s.Cents) : null;
		}

		return worth;
	}
}
