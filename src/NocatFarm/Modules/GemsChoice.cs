using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// "Use gems for the most levels" (1.8.2): with buying cards on, what 1,000 gems are worth in badge levels each way, and
/// the most levels picked - with a safety margin, and the gems kept loose when it isn't sure.
/// </summary>
/// <remarks>
/// The four ways, each counted in levels per 1,000 gems at today's prices in the wallet's own money:
/// <list type="bullet">
/// <item>(a) sell a sack: a sack of 1,000 gems listed a cent under the cheapest, after Steam's fee - that money buys the
/// cheapest next badge levels, as buying cards ranks them (<see cref="BuyPlan.Ranked"/>).</item>
/// <item>(b) make a booster pack and sell it sealed: only when the pack pays more than its gems would as a sack.</item>
/// <item>(c) make a pack, open it and sell the 3 cards: the average of 3 random cards of the set, each after the fee - a
/// card that sold fewer than <see cref="MinCardSales"/> times in the last day counts as nothing; only when that pays more
/// than the pack's gems would as a sack.</item>
/// <item>(d) make a pack and open it for the closest badge (what automatic always did): 1,000 / the gems it takes on average
/// to finish that badge's next level (<see cref="BoosterPick.ExpectedGems"/>).</item>
/// </list>
/// Each way is riskier than the one before it ((d) never touches the market; a sack sells fastest; a pack, then 3 cards,
/// less surely), so a riskier way is picked only when it beats every safer one by <see cref="MarginPct"/> percent. When
/// it can't tell - no price for a sack, or buying cards hasn't priced its next levels - the gems stay loose. Only tradable
/// gems can go to the market (a sack, or a pack made from them); untradable gems only ever make (d) packs.
/// Nothing here lists, makes or buys: it only works the numbers out (<see cref="Boosters"/> acts on them).
/// </remarks>
public static class GemsChoice {
	public enum Way { Keep, Badge, Sack, PackSell, PackOpen }

	/// <summary>Gems everything is counted per.</summary>
	public const int Per = 1000;

	/// <summary>A riskier way has to beat every safer one by this many percent.</summary>
	public const int MarginPct = 15;

	/// <summary>Sales in the last day each item needs before its price is counted on.</summary>
	public const int MinSackSales = 10, MinPackSales = 3, MinCardSales = 3;

	/// <summary>Market reads (a pack's price, a set's cards) one look may make to learn prices it doesn't have.</summary>
	public const int LearnALook = 4;

	/// <summary>Listings it makes a day, at most, on one account.</summary>
	public const int ListingsADay = 10;

	/// <summary>How old a price may be: a sack's to decide on, a pack's or card's to choose with, and either right before acting.</summary>
	public static readonly TimeSpan SackFresh = TimeSpan.FromHours(6), ChooseFresh = TimeSpan.FromDays(3), ActFresh = TimeSpan.FromHours(1);

	/// <summary>Whether it decides at all: the setting, "Make booster packs" automatic, and buying cards on to spend the money.</summary>
	public static bool Applies(BotConfig cfg, bool buying) => cfg.GemsForLevels && buying && (cfg.BoosterMake == BoosterPick.Auto);

	/// <summary>A booster pack's market name: "&lt;app&gt;-&lt;game&gt; Booster Pack".</summary>
	public static string PackHash(uint app, string game) => $"{app.ToString(CultureInfo.InvariantCulture)}-{game} Booster Pack";

	/// <summary>
	/// One way, worked out. <paramref name="PackGems"/>: what it costs in gems (1,000 for a sack). <paramref name="Proceeds"/>:
	/// what it brings in after Steam's fee, in the wallet's money (0 for (d)). <paramref name="PerThousand"/>: that per 1,000
	/// gems. <paramref name="Levels"/>: badge levels per 1,000 gems.
	/// </summary>
	public sealed record Option(Way Way, uint App, string Game, int PackGems, long Proceeds, long PerThousand, double Levels);

	/// <summary>The word: which way (Keep: none), the option picked, every option weighed, why in plain words, and the money.</summary>
	public sealed record Choice(Way Way, Option? Pick, IReadOnlyList<Option> Options, string Why, int Currency) {
		/// <summary>The best option of each way - what the preview shows.</summary>
		public IEnumerable<Option> Best => Options.GroupBy(static o => o.Way).Select(static g => g.OrderByDescending(static o => o.Levels).First())
			.OrderBy(static o => Risk(o.Way));
	}

	/// <summary>How far a way relies on the market: (d) not at all, then a sack, a sealed pack, 3 loose cards.</summary>
	public static int Risk(Way way) => way switch {
		Way.Badge => 0,
		Way.Sack => 1,
		Way.PackSell => 2,
		Way.PackOpen => 3,
		_ => 9
	};

	/// <summary>What one listing pays after Steam's fee, listed a cent under the cheapest (0: no price to go by).</summary>
	public static int Receive(MarketQuote? q, int dumpPct) =>
		(q is { Lowest: > 0 }) && (ListingPrice.For(q.Price, 3, dumpPct).Cents is int buyer) ? Seller.ReceiveFor(buyer) : 0;

	/// <summary>
	/// Badge levels a sum buys: the cheapest next levels first (<paramref name="costs"/>, each a level's price), the last one
	/// it reaches in part. Past the levels known, the rest at a quarter over the dearest of them - 1.8.2: never at the
	/// dearest itself, which made money look worth more than it is (the levels past the list cost more, not the same).
	/// </summary>
	public static double LevelsFor(long money, IReadOnlyList<long> costs) {
		double levels = 0;
		long left = money;
		long last = 0;

		foreach (long cost in costs.Where(static c => c > 0).Order()) {
			last = cost;

			if (cost > left) {
				return levels + (left / (double) cost);
			}

			left -= cost;
			levels++;
		}

		return last > 0 ? levels + (left / (last * PastListPct / 100.0)) : levels;
	}

	/// <summary>1.8.2: money past the levels known buys levels at this many percent of the dearest known.</summary>
	public const int PastListPct = 125;

	/// <summary>
	/// The decision on its own. <paramref name="costs"/>: what each next badge level costs, cheapest first (null: not known
	/// right now - kept loose; empty: money buys no level, so only (d) counts). <paramref name="sackKnown"/>: a sack has a
	/// price to go by - without it nothing can be checked against what the gems are worth, so they're kept loose (unless money
	/// buys nothing anyway). The most levels wins - but a riskier way only when it beats every safer one by the margin.
	/// </summary>
	public static Choice Decide(IReadOnlyList<Option> options, bool sackKnown, IReadOnlyList<long>? costs, int currency, int marginPct = MarginPct) {
		if (costs == null) {
			return new Choice(Way.Keep, null, options, "not sure what money buys right now - buying cards hasn't priced its next levels", currency);
		}

		if ((costs.Count > 0) && !sackKnown) {
			return new Choice(Way.Keep, null, options, "no price for a sack of gems right now, so it can't tell what the gems are worth", currency);
		}

		List<Option> live = [.. options.Where(static o => (o.Levels > 0) && !double.IsNaN(o.Levels) && !double.IsInfinity(o.Levels))];

		if (live.Count == 0) {
			return new Choice(Way.Keep, null, options, "nothing turns gems into badge levels right now", currency);
		}

		foreach (Option o in live.OrderByDescending(static o => o.Levels).ThenBy(static o => Risk(o.Way)).ThenBy(static o => o.App)) {
			if (live.Where(s => Risk(s.Way) < Risk(o.Way)).All(s => o.Levels * 100 >= s.Levels * (100 + marginPct))) {
				return new Choice(o.Way, o, options, $"{Words(o, currency)} gives the most levels", currency);
			}
		}

		return new Choice(Way.Keep, null, options, "nothing turns gems into badge levels right now", currency);   // never: the safest always passes
	}

	/// <summary>One way in plain words: "sell a sack", "make a Crypt pack and sell it", ...</summary>
	public static string Words(Option o, int currency) => o.Way switch {
		Way.Sack => "sell a sack",
		Way.PackSell => $"make {A(o.Game)} {o.Game} pack and sell it",
		Way.PackOpen => $"open {A(o.Game)} {o.Game} pack and sell its cards",
		Way.Badge => $"open {o.Game} packs for its badge",
		_ => "keep the gems"
	};

	/// <summary>"an" before a game whose name starts with a vowel ("an Arcane Raise pack"), else "a".</summary>
	private static string A(string game) => (game.Length > 0) && "AEIOUaeiou".Contains(game[0], StringComparison.Ordinal) ? "an" : "a";

	/// <summary>
	/// The numbers in one line: "1,000 gems: sell a sack = 0.45 CAD = about 1.8 levels; make a Crypt pack and sell it = 0.52
	/// CAD = about 2.1 levels; ... → make a Crypt pack and sell it". English, like every console answer.
	/// </summary>
	public static string Line(Choice c) {
		List<string> parts = [.. c.Best.Select(o => o.Way == Way.Badge
			? $"{Words(o, c.Currency)} = about {o.Levels.ToString("0.0", CultureInfo.InvariantCulture)} levels"
			: $"{Words(o, c.Currency)} = {BuyPlan.Money(o.PerThousand, c.Currency)} = about {o.Levels.ToString("0.0", CultureInfo.InvariantCulture)} levels")];

		string what = parts.Count > 0 ? string.Join("; ", parts) : "no way to weigh yet";
		string then = c.Pick is { } p ? Words(p, c.Currency) : $"keep them loose ({c.Why})";

		return $"{Per:N0} gems: {what} → {then}";
	}

	// ── the numbers, from today's prices ─────────────────────────────────────
	/// <summary>Where prices come from: what's held, the market now (priceoverview, in the wallet's money), and a game's set.</summary>
	public interface IMarket {
		/// <summary>The price held for an item in this currency, however old. Never a request.</summary>
		MarketQuote? Known(string hash, int currency);

		/// <summary>The price now, in the wallet's money, with the last day's sales. Null when the market didn't answer.</summary>
		Task<MarketQuote?> QuoteAsync(string hash, int currency, CancellationToken ct);

		/// <summary>A game's whole normal set known lately without asking (the set finder), in US cents - or null.</summary>
		CardSet? KnownSet(uint app);

		/// <summary>
		/// A game's whole normal set from the market's search, in US cents - null when the market didn't answer or it isn't
		/// whole - and whether the market was asked (a set read lately is answered without asking).
		/// </summary>
		Task<(CardSet? Set, bool Asked)> SetAsync(uint app, CancellationToken ct);

		/// <summary>US cents to the wallet's money, as learned from the market - null until it is (never guessed for a decision).</summary>
		double? Rate(int currency);

		/// <summary>
		/// 1.8.2: roughly what a game's pack holds - sealed, or 3 of its cards - in US cents, from anything held however old (a
		/// search, a set, the inventory's prices): only which packs to price first. Never a request; null when nothing is held.
		/// </summary>
		int? Promise(uint app) => null;
	}

	/// <summary>
	/// Everything a look weighs: the wallet's money, the booster creator, automatic's ranking, the level costs, the games never
	/// opened - and (1.8.2) the games on the keep list ("Never touch items from these games": nothing of theirs is ever sold,
	/// so no pack is made of them to sell). Not the price limit: the cards of a pack made to sell are sold whatever it says -
	/// they were made for money, and the limit is for what the account already had - so each counts at its real value.
	/// </summary>
	public sealed record Inputs(int Currency, Boosters.Page Page, BoosterPick.Ranking Ranking, IReadOnlyList<long>? Costs, int DumpPct,
		IReadOnlyCollection<uint> NeverOpen, DateTime Now, IReadOnlyCollection<uint>? Keep = null);

	/// <summary>
	/// Every way weighed with today's prices, and the pick. A sack's price is read again when it's over
	/// <see cref="SackFresh"/> old. Every pack whose price (and set) is held fresh enough is weighed; those it doesn't hold
	/// are learned a few a look (<paramref name="learn"/>) - 1.8.2: the most promising first, by what's known of what the
	/// pack holds however old, for its gems (<see cref="IMarket.Promise"/>), then the cheapest; the rest wait for later
	/// looks. Never a game on the keep list. Reads only: nothing is made, listed or bought.
	/// </summary>
	public static async Task<Choice> WeighAsync(Inputs x, IMarket market, int learn, CancellationToken ct) {
		int cur = x.Currency;

		if (cur <= 0) {
			return new Choice(Way.Keep, null, [], "Steam hasn't said this account's wallet currency yet", cur);
		}

		List<Option> options = [];
		IReadOnlyList<long> costs = x.Costs ?? [];

		// (d): the closest badge, as automatic always picked it.
		if (x.Ranking.Ranked.Count > 0) {
			BoosterPick.Candidate top = x.Ranking.Ranked[0];
			options.Add(new Option(Way.Badge, top.App, top.Name, (int) top.Price, 0, 0, Per / (double) Math.Max(1, BoosterPick.ExpectedGems(top))));
		}

		// (a): a sack - and what the gems are worth, which every other market way has to beat.
		MarketQuote? sackQuote = market.Known(SparePrices.SackHash, cur);

		if ((sackQuote == null) || (x.Now - sackQuote.When > SackFresh)) {
			sackQuote = await market.QuoteAsync(SparePrices.SackHash, cur, ct).ConfigureAwait(false) ?? sackQuote;
		}

		bool sackKnown = (sackQuote != null) && (x.Now - sackQuote.When <= SackFresh) && (sackQuote.Sales >= MinSackSales) && (Receive(sackQuote, x.DumpPct) > 0);
		int sack = sackKnown ? Receive(sackQuote, x.DumpPct) : 0;

		if (!sackKnown) {
			return Decide(options, sackKnown, x.Costs, cur);   // nothing to check the packs against
		}

		options.Add(new Option(Way.Sack, 0, "Sack of Gems", Per, sack, sack, LevelsFor(sack, costs)));

		// (b) and (c): every game the booster creator offers today - 1.8.2: never one on the keep list, whose pack and cards
		// would never be sold.
		IReadOnlyCollection<uint> keep = x.Keep ?? [];
		List<Boosters.Offer> offers = [.. x.Page.Offers.Values.Where(o => !o.Unavailable && (o.Price > 0) && !keep.Contains(o.AppId))
			.OrderBy(static o => o.Price).ThenBy(static o => o.AppId)];
		double? rate = market.Rate(cur);
		Dictionary<uint, MarketQuote?> packs = [];
		Dictionary<uint, CardSet?> sets = [];

		// (c) only for a game whose packs may be opened, and with a rate to read its set's US prices by.
		bool Opens(Boosters.Offer o) => !x.NeverOpen.Contains(o.AppId) && (rate != null);
		bool Stale(MarketQuote? q) => (q == null) || (x.Now - q.When > ChooseFresh);

		foreach (Boosters.Offer offer in offers) {
			packs[offer.AppId] = market.Known(PackHash(offer.AppId, offer.Name), cur);
			sets[offer.AppId] = Opens(offer) ? market.KnownSet(offer.AppId) : null;
		}

		// What it doesn't hold, learned a few a look: the packs whose contents look worth the most for their gems first.
		int learned = 0;

		foreach (Boosters.Offer offer in offers.Where(o => Stale(packs[o.AppId]) || (Opens(o) && (sets[o.AppId] == null)))
			.OrderByDescending(o => market.Promise(o.AppId) is int p and > 0 ? p / (double) o.Price : -1).ThenBy(static o => o.Price).ThenBy(static o => o.AppId)) {
			if (learned >= learn) {
				break;
			}

			if (Stale(packs[offer.AppId])) {
				learned++;
				packs[offer.AppId] = await market.QuoteAsync(PackHash(offer.AppId, offer.Name), cur, ct).ConfigureAwait(false) ?? packs[offer.AppId];
			}

			if (Opens(offer) && (sets[offer.AppId] == null) && (learned < learn)) {
				(CardSet? set, bool asked) = await market.SetAsync(offer.AppId, ct).ConfigureAwait(false);
				sets[offer.AppId] = set;

				if (asked) {
					learned++;
				}
			}
		}

		foreach (Boosters.Offer offer in offers) {
			// The pack's gems as a sack would pay: a way that doesn't beat this loses money.
			decimal worth = Spare.GemsWorth((int) offer.Price, sack);

			// (b): the pack sealed.
			if ((packs[offer.AppId] is { } pack) && !Stale(pack) && (pack.Sales >= MinPackSales) && (Receive(pack, x.DumpPct) is int paid and > 0) && (paid >= worth + 1m)) {
				long per = paid * (long) Per / offer.Price;
				options.Add(new Option(Way.PackSell, offer.AppId, offer.Name, (int) offer.Price, paid, per, LevelsFor(per, costs)));
			}

			// (c): opened, its 3 cards sold - never a game whose packs stay sealed.
			if (!Opens(offer) || (sets[offer.AppId] is not { Cards.Count: > 0 } set) || (set.Cards.Count != BoosterPick.SetSize(offer.Price))) {
				continue;
			}

			long ev = OpenedWorth(set, cur, rate!.Value, market, x.Now, x.DumpPct, live: false);

			if ((ev > 0) && (ev >= worth + 1m)) {
				long per = ev * Per / offer.Price;
				options.Add(new Option(Way.PackOpen, offer.AppId, offer.Name, (int) offer.Price, ev, per, LevelsFor(per, costs)));
			}
		}

		Choice now = Decide(options, sackKnown, x.Costs, cur);

		// 1.8.2: a pack made once a day is waiting for today's to be made again - when it's clearly better than the best way open
		// now (by the margin), the gems wait for it rather than going somewhere worse. Only prices already held: no lookups here.
		if ((now.Way != Way.Keep) && (now.Pick is { } best)) {
			foreach (Boosters.Offer later in x.Page.Offers.Values.Where(o => o.Unavailable && (o.Price > 0) && !keep.Contains(o.AppId))) {
				decimal worth = Spare.GemsWorth((int) later.Price, sack);
				long per = 0;

				if ((market.Known(PackHash(later.AppId, later.Name), cur) is { } pack) && !Stale(pack) && (pack.Sales >= MinPackSales) && (Receive(pack, x.DumpPct) is int paid and > 0) && (paid >= worth + 1m)) {
					per = Math.Max(per, paid * (long) Per / later.Price);
				}

				if (Opens(later) && (market.KnownSet(later.AppId) is { Cards.Count: > 0 } laterSet) && (laterSet.Cards.Count == BoosterPick.SetSize(later.Price))
					&& (OpenedWorth(laterSet, cur, rate!.Value, market, x.Now, x.DumpPct, live: false) is long ev and > 0) && (ev >= worth + 1m)) {
					per = Math.Max(per, ev * Per / later.Price);
				}

				double levels = per > 0 ? LevelsFor(per, costs) : 0;

				if ((levels > 0) && (levels * 100 >= best.Levels * (100 + MarginPct))) {
					return new Choice(Way.Keep, null, options, $"waiting for {later.Name}'s pack ({later.AvailableAt}) - about {levels:0.0} levels per 1,000 gems, against {best.Levels:0.0} now", cur);
				}
			}
		}

		return now;
	}

	/// <summary>
	/// What a pack's 3 random cards bring in on average, after the fee: 3 x the average of each card's take. Each card at its
	/// own price in the wallet's money when one is held (<see cref="ChooseFresh"/>) - a card that sold fewer than
	/// <see cref="MinCardSales"/> times in the last day counts as nothing - else, while choosing, its US price at the learned
	/// rate. <paramref name="live"/>: only prices held in the wallet's money count (right before acting, every card is priced).
	/// 1.8.2: a card worth more than <paramref name="maxCents"/> ("Never touch items worth more than", 0: none) counts as
	/// nothing too - for a pack that came in, whose cards are spare items and never sold over the limit. Never for a pack
	/// made to sell: its cards are sold whatever the limit.
	/// </summary>
	public static long OpenedWorth(CardSet set, int cur, double rate, IMarket market, DateTime now, int dumpPct, bool live, int maxCents = 0) {
		long total = 0;
		bool Over(int worth) => (maxCents > 0) && (worth > maxCents);

		foreach (string hash in set.Cards) {
			MarketQuote? q = market.Known(hash, cur);

			if ((q != null) && (now - q.When <= (live ? ActFresh : ChooseFresh))) {
				total += (q.Sales >= MinCardSales) && !Over(ListingPrice.Worth(q.Price)) ? Receive(q, dumpPct) : 0;
			} else if (live) {
				return 0;
			} else if (set.Usd.GetValueOrDefault(hash) is int usd and > 0) {
				int wallet = WalletRate.ToWallet(usd, rate);
				total += Over(wallet) ? 0 : Seller.ReceiveFor(Seller.UndercutFor(wallet));
			}
		}

		return set.Cards.Count > 0 ? total * 3 / set.Cards.Count : 0;
	}

	/// <summary>
	/// Right before acting on a pack: its price read again now ((b): the pack; (c): every card of the set), and the choice
	/// made again with it. The same way and game - it goes ahead; anything else - it waits for the next look.
	/// </summary>
	public static async Task<Choice> ConfirmAsync(Choice was, Inputs x, IMarket market, CancellationToken ct) {
		if (was.Pick is not { Way: Way.PackSell or Way.PackOpen } pick) {
			return was;
		}

		int cur = x.Currency;
		Option? sackOption = was.Options.FirstOrDefault(static o => o.Way == Way.Sack);
		decimal worth = Spare.GemsWorth(pick.PackGems, (int) (sackOption?.Proceeds ?? 0));
		IReadOnlyList<long> costs = x.Costs ?? [];
		Option? now = null;

		if (pick.Way == Way.PackSell) {
			MarketQuote? q = await market.QuoteAsync(PackHash(pick.App, pick.Game), cur, ct).ConfigureAwait(false);

			if ((q != null) && (q.Sales >= MinPackSales) && (Receive(q, x.DumpPct) is int paid and > 0) && (paid >= worth + 1m)) {
				long per = paid * (long) Per / pick.PackGems;
				now = pick with { Proceeds = paid, PerThousand = per, Levels = LevelsFor(per, costs) };
			}
		} else if ((market.KnownSet(pick.App) ?? (await market.SetAsync(pick.App, ct).ConfigureAwait(false)).Set) is { Cards.Count: > 0 } set) {
			foreach (string hash in set.Cards) {
				if (await market.QuoteAsync(hash, cur, ct).ConfigureAwait(false) == null) {
					break;   // the market stopped answering: not sure - the next look
				}
			}

			long ev = OpenedWorth(set, cur, market.Rate(cur) ?? 0, market, x.Now, x.DumpPct, live: true);

			if ((ev > 0) && (ev >= worth + 1m)) {
				long per = ev * Per / pick.PackGems;
				now = pick with { Proceeds = ev, PerThousand = per, Levels = LevelsFor(per, costs) };
			}
		}

		List<Option> options = [.. was.Options.Where(o => !((o.Way == pick.Way) && (o.App == pick.App)))];

		if (now != null) {
			options.Add(now);
		}

		Choice again = Decide(options, sackOption != null, x.Costs, cur);

		return (again.Way == pick.Way) && (again.Pick?.App == pick.App) ? again
			: new Choice(Way.Keep, null, options, $"the price moved since it was weighed - {Words(pick, cur)} waits for the next look", cur);
	}

	// ── a pack that came in (a drop, a trade), weighed ─────────────────────────
	/// <summary>1.8.2: what a sealed pack in the inventory is best used for.</summary>
	public enum PackUse { Unknown, OpenBadge, SellSealed, OpenSell }

	/// <summary>
	/// 1.8.2: one sealed pack weighed three ways, in the wallet's money: <paramref name="Sealed"/> - sold sealed, after the fee;
	/// <paramref name="Cards"/> - opened and its 3 cards sold, after the fee; <paramref name="Badge"/> - opened for its badge,
	/// the 3 cards at what buying them would cost (only for a badge it's levelling). 0: not known, or not a way for it.
	/// </summary>
	public sealed record PackWorth(uint App, string Game, long Sealed, long Cards, long Badge) {
		/// <summary>
		/// The best: the most money - a way that relies on the market more (the badge none, a sealed pack, then 3 loose cards)
		/// only when it beats every safer one by <see cref="MarginPct"/> percent, as for gems.
		/// </summary>
		public PackUse Best {
			get {
				List<(PackUse Use, long Worth)> ways = [(PackUse.OpenBadge, Badge), (PackUse.SellSealed, Sealed), (PackUse.OpenSell, Cards)];
				ways.RemoveAll(static w => w.Worth <= 0);

				foreach ((PackUse use, long worth) in ways.OrderByDescending(static w => w.Worth).ThenBy(static w => w.Use)) {
					if (ways.Where(s => s.Use < use).All(s => worth * 100 >= s.Worth * (100 + MarginPct))) {
						return use;
					}
				}

				return PackUse.Unknown;
			}
		}
	}

	/// <summary>
	/// 1.8.2: a sealed pack weighed (<see cref="PackWorth"/>) from what's held: the pack's own price in the wallet's money
	/// (<see cref="ChooseFresh"/>, enough sales), and its game's set (<paramref name="set"/>, null: not known) - each card at
	/// its own price when held, else its US price at the learned rate. <paramref name="levelling"/>: its badge is one the
	/// account is levelling, so its cards would otherwise be bought. Never a request.
	/// </summary>
	public static PackWorth WeighPack(uint app, string game, string packHash, CardSet? set, bool levelling, IMarket market, int cur, DateTime now, int dumpPct, int maxCents) {
		long sealedPays = (market.Known(packHash, cur) is { } q) && (now - q.When <= ChooseFresh) && (q.Sales >= MinPackSales) ? Receive(q, dumpPct) : 0;

		if ((set is not { Cards.Count: > 0 }) || (market.Rate(cur) is not double rate)) {
			return new PackWorth(app, game, sealedPays, 0, 0);
		}

		long cards = OpenedWorth(set, cur, rate, market, now, dumpPct, live: false, maxCents);
		long badge = 0;

		if (levelling) {
			// What 3 of its cards cost to buy: each card's cheapest listing in the wallet's money, or its US price at the rate.
			List<int> buy = [.. set.Cards.Select(h => market.Known(h, cur) is { Lowest: > 0 } c && (now - c.When <= ChooseFresh) ? c.Lowest!.Value
				: set.Usd.GetValueOrDefault(h) is int usd and > 0 ? WalletRate.ToWallet(usd, rate) : 0).Where(static c => c > 0)];
			badge = buy.Count > 0 ? (long) Math.Round(3 * buy.Average(), MidpointRounding.ToZero) : 0;
		}

		return new PackWorth(app, game, sealedPays, cards, badge);
	}

	/// <summary>A pack's best use in plain words: "open it for its badge", "sell it sealed", "open it and sell its cards".</summary>
	public static string Words(PackUse use) => use switch {
		PackUse.OpenBadge => "open it for its badge",
		PackUse.SellSealed => "sell it sealed",
		PackUse.OpenSell => "open it and sell its cards",
		_ => "not known yet - no prices for it"
	};

	/// <summary>
	/// 1.8.2: what an account's settings do with a sealed pack that came in (not one automatic made), in plain words, and
	/// whether that's <paramref name="best"/> - with the setting that would do it when it isn't. English, like every console
	/// answer.
	/// </summary>
	public static string Fate(BotConfig cfg, PackWorth w, PackUse best) {
		bool never = cfg.BoosterNeverOpen.Contains(w.App);
		int open = never ? 0 : Math.Clamp(cfg.OpenBoosterPacks, 0, 2);
		bool opens = (open == 2) || ((open == 1) && (w.Cards > 0) && (w.Cards > w.Sealed));
		int unopened = Math.Clamp(cfg.BoosterUnopened, 0, 2);
		string does = opens ? "open it"
			: unopened == 2 ? (cfg.SpareItems ? "sell it sealed" : "sell it sealed once Spare items is on")
			: unopened == 1 ? "send it to another of your accounts"
			: never ? "keep it sealed (its game is on \"Never open packs from these games\")"
			: "keep it sealed";
		bool same = best switch {
			PackUse.OpenBadge or PackUse.OpenSell => opens,
			PackUse.SellSealed => !opens && (unopened == 2) && cfg.SpareItems,
			_ => true
		};

		if (same) {
			return $"your settings {does}";
		}

		string how = best == PackUse.SellSealed ? "\"Packs it doesn't open\": sell them, with Spare items on"
			: never ? "take its game off \"Never open packs from these games\""
			: "\"Open booster packs\": always";

		return $"your settings {does} - {how} would {Words(best)}";
	}

	// ── what buying cards priced last, per account ────────────────────────────
	/// <summary>A look's level costs: when, the money, the levels with their games (null: not known; empty: money can't be spent by itself) and the wallet.</summary>
	private sealed record Noted(DateTime At, int Currency, (uint App, long Cost)[]? Levels, long Wallet);

	private static readonly ConcurrentDictionary<string, Noted> Costs = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How long a look's level costs are used before buying cards prices them again.</summary>
	public static readonly TimeSpan CostsFor = TimeSpan.FromHours(24);

	/// <summary>
	/// What a buying look found the next badge levels cost, cheapest first: none when it couldn't tell (a problem, Steam not
	/// saying how purchases are confirmed, the market stopping part way); empty when the money can't be spent by itself
	/// (billing details, email). Only the levels past what the wallet already pays for: money from gems buys those
	/// (<see cref="BuyPlan.BeyondWallet"/>). 1.8.2: the levels are the ones priced and the set finder's cheapest known past
	/// them (<see cref="BuyPlan.Levels"/>), so a wallet that pays for the few priced still leaves money a value; only when it
	/// pays for every level known is what more money buys not known.
	/// </summary>
	public static void NoteCosts(string account, BuyPlan plan, DateTime now) {
		(uint App, long Cost)[]? levels = (plan.Problem.Length > 0) || (plan.Block == BuyBlock.Unknown) || (plan.Currency <= 0) || plan.Short ? null
			: plan.Block != BuyBlock.None ? []
			: [.. plan.LevelCosts];

		Costs[account] = new Noted(now, plan.Currency, levels, plan.Block == BuyBlock.None ? plan.Wallet : 0);
	}

	/// <summary>
	/// The level costs noted for an account lately, in its wallet's money, past what the wallet already pays for - null when
	/// there are none to go by. 1.8.2: <paramref name="except"/> - without that game's own levels (what its cards held are
	/// weighed against never counts the level they'd make themselves).
	/// </summary>
	public static IReadOnlyList<long>? CostsOf(string account, int currency, DateTime now, uint except = 0) {
		if (!Costs.TryGetValue(account, out Noted? c) || (c.Currency != currency) || (now - c.At >= CostsFor) || (c.Levels == null)) {
			return null;
		}

		// Steam won't let it buy by itself: money buys no level (empty), not "not known".
		if (c.Levels.Length == 0) {
			return [];
		}

		List<(uint App, long Cost)> levels = [.. c.Levels.Where(l => (except == 0) || (l.App != except))];
		List<long> more = BuyPlan.BeyondWallet(levels.Select(static l => l.Cost), c.Wallet);

		// The wallet already pays for every level known: what more money buys isn't known (the levels past them weren't
		// priced or found) - not "nothing", or opening packs would look better than a sack that buys several cheap sets later.
		return (more.Count == 0) && (levels.Count > 0) ? null : more;
	}

	/// <summary>Forget them - the checks.</summary>
	public static void ForgetCosts() => Costs.Clear();

	// ── what it still has to sell, per account ────────────────────────────────
	/// <summary>
	/// What the gems were turned into that's still to be listed (config/state/gems-&lt;account&gt;.json): sacks it packed, and
	/// the cards of packs it opened to sell - and how many it listed today.
	/// </summary>
	public sealed class Memory {
		public int Sacks { get; set; }
		public List<OpenedCard> Cards { get; set; } = [];
		public int Day { get; set; }
		public int ListedToday { get; set; }

		/// <summary>1.8.2: when a sack was last put up without a clear yes (UTC ticks, 0 none) - looked for among the listings before another goes up.</summary>
		public long SackTriedAt { get; set; }

		/// <summary>The latest word, for 'booster plan' and the status line.</summary>
		public string Last { get; set; } = "";
		public long LastAt { get; set; }

		/// <summary>1.8.2: Steam's refusals of its listings in a row, and when the last came (UTC ticks, 0 none) - see <see cref="RefusedWait"/>.</summary>
		public int Refusals { get; set; }
		public long RefusedAt { get; set; }

		/// <summary>
		/// A card from a pack opened to sell; it's let go of after 3 days (a spare card like any other then). 1.8.2: by its own
		/// copy - <paramref name="Before"/>, the ids of the game's cards held before the pack was opened (it's a copy that wasn't
		/// one of them), and <paramref name="Asset"/> once it's found (0: not yet) - never another copy of the same name.
		/// </summary>
		public sealed record OpenedCard(uint App, string Name, DateTime At, ulong[]? Before = null, ulong Asset = 0);

		public static readonly TimeSpan CardsFor = TimeSpan.FromDays(3);

		private static string PathOf(string name) => Path.Combine(ConfigStore.ConfigDir, "state", $"gems-{name}.json");

		private static readonly ConcurrentDictionary<string, Memory> Loaded = new(StringComparer.OrdinalIgnoreCase);
		private readonly Lock _gate = new();

		public static Memory Of(string name) => Loaded.GetOrAdd(name, static n => Read(n));

		public static void ForgetLoaded() => Loaded.Clear();

		private static Memory Read(string name) {
			try {
				if (File.Exists(PathOf(name)) && JsonSerializer.Deserialize<Memory>(File.ReadAllText(PathOf(name))) is { } m) {
					return m;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				Log.Failed("couldn't read what the gems were turned into", e, name);
			}

			return new Memory();
		}

		public void Save(string name) {
			lock (_gate) {
				try {
					Cards.RemoveAll(static c => DateTime.UtcNow - c.At > CardsFor);
					Directory.CreateDirectory(Path.GetDirectoryName(PathOf(name))!);
					AtomicFile.Write(PathOf(name), JsonSerializer.Serialize(this));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed("couldn't save what the gems were turned into", e, name);
				}
			}
		}

		/// <summary>Listings it may still make today (by this PC's day).</summary>
		public int LeftToday(DateTime local) {
			lock (_gate) {
				int day = (local.Year * 10000) + (local.Month * 100) + local.Day;

				if (Day != day) {
					Day = day;
					ListedToday = 0;
				}

				return Math.Max(0, ListingsADay - ListedToday);
			}
		}

		public void Listed(int n) {
			lock (_gate) {
				ListedToday += n;

				if (n > 0) {
					Refusals = 0;   // a listing went up: the refusals are over
				}
			}
		}

		/// <summary>
		/// 1.8.2: Steam refused a listing. It counts against today's listings like one that went up (refusals didn't, and a
		/// refused sack was tried again every look - about 100 times a day), and the next try waits <see cref="RefusedWait"/>.
		/// </summary>
		public void Refused(DateTime nowUtc) {
			lock (_gate) {
				ListedToday++;
				Refusals++;
				RefusedAt = nowUtc.Ticks;
			}
		}

		/// <summary>
		/// 1.8.2: until when listing waits after Steam's refusals: 3 hours after the first, then 6, 12, and 24 at most - null
		/// when it needn't wait.
		/// </summary>
		public DateTime? RefusedWait(DateTime nowUtc) {
			lock (_gate) {
				if ((Refusals <= 0) || (RefusedAt <= 0)) {
					return null;
				}

				DateTime until = new DateTime(RefusedAt, DateTimeKind.Utc) + TimeSpan.FromHours(Math.Min(24, 3 << Math.Min(Refusals - 1, 3)));

				return nowUtc < until ? until : null;
			}
		}

		public void AddCards(uint app, IEnumerable<string> names, DateTime at, IEnumerable<ulong>? before = null) {
			ulong[]? had = before == null ? null : [.. before];

			lock (_gate) {
				Cards.AddRange(names.Select(n => new OpenedCard(app, n, at, had)));
			}
		}

		/// <summary>1.8.2: the copy a card from a pack opened to sell is - found in the inventory, kept from now on.</summary>
		public OpenedCard Bind(OpenedCard card, ulong asset) {
			lock (_gate) {
				int at = Cards.IndexOf(card);
				OpenedCard bound = card with { Asset = asset };

				if (at >= 0) {
					Cards[at] = bound;
				}

				return bound;
			}
		}

		/// <summary>
		/// 1.8.2: which copy in the inventory a card from a pack opened to sell is: the one found before (by its id), or else one
		/// of that name that came in after the pack was opened (not among <see cref="OpenedCard.Before"/>) and isn't taken -
		/// never a copy the account already held. Null when there's none (crafted, swapped or sold since - or a card noted
		/// before the copies were known, which is let go of).
		/// </summary>
		public static SpareItem? CopyOf(OpenedCard card, IReadOnlyList<SpareItem> items, IReadOnlySet<ulong> taken) {
			if (card.Asset != 0) {
				return items.FirstOrDefault(i => (i.AssetId == card.Asset) && (i.Kind == SpareKind.Card) && i.Marketable && !taken.Contains(i.AssetId));
			}

			if (card.Before == null) {
				return null;
			}

			HashSet<ulong> before = [.. card.Before];

			return items.Where(i => (i.Kind == SpareKind.Card) && (i.Game == card.App) && i.Marketable && !taken.Contains(i.AssetId) && !before.Contains(i.AssetId)
				&& i.Name.Equals(card.Name, StringComparison.Ordinal)).OrderByDescending(static i => i.AssetId).FirstOrDefault();
		}

		public List<OpenedCard> CardsNow() {
			lock (_gate) {
				return [.. Cards.Where(static c => DateTime.UtcNow - c.At <= CardsFor)];
			}
		}

		public void RemoveCard(OpenedCard card) {
			lock (_gate) {
				Cards.Remove(card);
			}
		}

		public void Said(string line, DateTime at) {
			lock (_gate) {
				Last = line;
				LastAt = at.Ticks;
			}
		}
	}
}
