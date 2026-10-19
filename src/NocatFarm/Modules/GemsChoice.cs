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
	/// it reaches in part. Past the levels priced today, the rest at the dearest of them.
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

		return last > 0 ? levels + (left / (double) last) : levels;
	}

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
	}

	/// <summary>Everything a look weighs: the wallet's money, the booster creator, automatic's ranking, the level costs, the games never opened.</summary>
	public sealed record Inputs(int Currency, Boosters.Page Page, BoosterPick.Ranking Ranking, IReadOnlyList<long>? Costs, int DumpPct,
		IReadOnlyCollection<uint> NeverOpen, DateTime Now);

	/// <summary>
	/// Every way weighed with today's prices, and the pick. A sack's price is read again when it's over
	/// <see cref="SackFresh"/> old; packs' and sets' prices it doesn't hold are learned a few a look (<paramref name="learn"/>),
	/// the cheapest packs first - the rest wait for later looks. Reads only: nothing is made, listed or bought.
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

		if (sackKnown) {
			options.Add(new Option(Way.Sack, 0, "Sack of Gems", Per, sack, sack, LevelsFor(sack, costs)));
		}

		// (b) and (c): every game the booster creator offers today, the cheapest packs first.
		int learned = 0;

		foreach (Boosters.Offer offer in x.Page.Offers.Values.Where(static o => !o.Unavailable && (o.Price > 0)).OrderBy(static o => o.Price).ThenBy(static o => o.AppId)) {
			if (!sackKnown) {
				break;   // nothing to check them against
			}

			// The pack's gems as a sack would pay: a way that doesn't beat this loses money.
			decimal worth = Spare.GemsWorth((int) offer.Price, sack);

			// (b): the pack sealed.
			string packHash = PackHash(offer.AppId, offer.Name);
			MarketQuote? pack = market.Known(packHash, cur);

			if (((pack == null) || (x.Now - pack.When > ChooseFresh)) && (learned < learn)) {
				learned++;
				pack = await market.QuoteAsync(packHash, cur, ct).ConfigureAwait(false) ?? pack;
			}

			if ((pack != null) && (x.Now - pack.When <= ChooseFresh) && (pack.Sales >= MinPackSales) && (Receive(pack, x.DumpPct) is int paid and > 0) && (paid >= worth + 1m)) {
				long per = paid * (long) Per / offer.Price;
				options.Add(new Option(Way.PackSell, offer.AppId, offer.Name, (int) offer.Price, paid, per, LevelsFor(per, costs)));
			}

			// (c): opened, its 3 cards sold - never a game whose packs stay sealed.
			if (x.NeverOpen.Contains(offer.AppId) || (market.Rate(cur) is not double rate)) {
				continue;
			}

			CardSet? set = market.KnownSet(offer.AppId);

			if ((set == null) && (learned < learn)) {
				(set, bool asked) = await market.SetAsync(offer.AppId, ct).ConfigureAwait(false);

				if (asked) {
					learned++;
				}
			}

			if ((set == null) || (set.Cards.Count == 0) || (set.Cards.Count != BoosterPick.SetSize(offer.Price))) {
				continue;
			}

			long ev = OpenedWorth(set, cur, rate, market, x.Now, x.DumpPct, live: false);

			if ((ev > 0) && (ev >= worth + 1m)) {
				long per = ev * Per / offer.Price;
				options.Add(new Option(Way.PackOpen, offer.AppId, offer.Name, (int) offer.Price, ev, per, LevelsFor(per, costs)));
			}
		}

		return Decide(options, sackKnown, x.Costs, cur);
	}

	/// <summary>
	/// What a pack's 3 random cards bring in on average, after the fee: 3 x the average of each card's take. Each card at its
	/// own price in the wallet's money when one is held (<see cref="ChooseFresh"/>) - a card that sold fewer than
	/// <see cref="MinCardSales"/> times in the last day counts as nothing - else, while choosing, its US price at the learned
	/// rate. <paramref name="live"/>: only prices held in the wallet's money count (right before acting, every card is priced).
	/// </summary>
	public static long OpenedWorth(CardSet set, int cur, double rate, IMarket market, DateTime now, int dumpPct, bool live) {
		long total = 0;

		foreach (string hash in set.Cards) {
			MarketQuote? q = market.Known(hash, cur);

			if ((q != null) && (now - q.When <= (live ? ActFresh : ChooseFresh))) {
				total += q.Sales >= MinCardSales ? Receive(q, dumpPct) : 0;
			} else if (live) {
				return 0;
			} else if (set.Usd.GetValueOrDefault(hash) is int usd and > 0) {
				int wallet = WalletRate.ToWallet(usd, rate);
				total += Seller.ReceiveFor(Seller.UndercutFor(wallet));
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

	// ── what buying cards priced last, per account ────────────────────────────
	private static readonly ConcurrentDictionary<string, (DateTime At, int Currency, long[]? Costs)> Costs = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How long a look's level costs are used before buying cards prices them again.</summary>
	public static readonly TimeSpan CostsFor = TimeSpan.FromHours(24);

	/// <summary>
	/// What a buying look found the next badge levels cost, cheapest first: none when it couldn't tell (a problem, Steam not
	/// saying how purchases are confirmed, the market stopping part way); empty when the money can't be spent by itself
	/// (billing details, email).
	/// </summary>
	public static void NoteCosts(string account, BuyPlan plan, DateTime now) {
		long[]? costs = (plan.Problem.Length > 0) || (plan.Block == BuyBlock.Unknown) || (plan.Currency <= 0) || plan.Short ? null
			: plan.Block != BuyBlock.None ? []
			: [.. plan.Ranked.Select(static s => s.Cost).Where(static c => c > 0).Order()];

		Costs[account] = (now, plan.Currency, costs);
	}

	/// <summary>The level costs noted for an account lately, in its wallet's money - null when there are none to go by.</summary>
	public static IReadOnlyList<long>? CostsOf(string account, int currency, DateTime now) =>
		Costs.TryGetValue(account, out (DateTime At, int Currency, long[]? Costs) c) && (c.Currency == currency) && (now - c.At < CostsFor) ? c.Costs : null;

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

		/// <summary>The latest word, for 'booster plan' and the status line.</summary>
		public string Last { get; set; } = "";
		public long LastAt { get; set; }

		/// <summary>A card from a pack opened to sell, by name; it's let go of after 3 days (a spare card like any other then).</summary>
		public sealed record OpenedCard(uint App, string Name, DateTime At);

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
			}
		}

		public void AddCards(uint app, IEnumerable<string> names, DateTime at) {
			lock (_gate) {
				Cards.AddRange(names.Select(n => new OpenedCard(app, n, at)));
			}
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
