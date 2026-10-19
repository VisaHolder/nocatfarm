using System.Collections.Concurrent;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Smart levelling (1.8.2): "dear" cards. A game whose normal cards the account holds, for a badge it's still levelling,
/// can be worth far more sold than the levels those cards would ever make - Arcane Raise cards at 0.40 to 1.50 each, when
/// the cheapest whole sets on the market cost 0.30 to 0.50 a level. Kept, 7 of its cards make 1 or 2 levels (and still
/// need the missing cards bought); sold, they pay for about 15 cheap levels. Such a game is "dear": its cards are sold,
/// and nothing is bought, packed or crafted for it meanwhile.
/// </summary>
/// <remarks>
/// For each game weighed, at today's prices in the wallet's own money:
/// <list type="bullet">
/// <item>kept: the badge levels the cards held can still make (each level that uses one of them, up to level 5), and what
/// buying the missing cards for those levels costs;</item>
/// <item>sold: what every copy held pays after Steam's fee, listed a cent under the cheapest - plus the missing cards it
/// then needn't buy - turned into levels the way buying cards ranks them, past what the wallet already pays for (the same
/// level list <see cref="SetSale"/> and the gems maths count on).</item>
/// </list>
/// Dear only when selling clearly wins: at least a whole level more and <see cref="MarginPct"/> percent more. Foil cards
/// too, by the same rule - a foil badge is one level, made only by the whole foil set. Whole sets held count like any
/// cards (badge crafting leaves a dear game's sets alone). What's left of a set sold instead of crafted is sold too - the
/// set was weighed, the rest would only sit. Never a game on the keep list or with a card kept by name, never a game it
/// just bought cards of. Anything not known for sure - no price, the market stopping part way, what money buys not known
/// (<see cref="GemsChoice.CostsOf"/>), a card that hardly sells when the verdict hangs on it - changes nothing: the cards
/// are kept, and the debug log says why once. A card that hardly sells waits at most <see cref="SetSale.WaitAtMost"/>,
/// then it's listed all the same. Only with "Sell a set instead of crafting it" and buying cards on (<see cref="Applies"/>).
/// Listed by buying cards' look, through the same careful path as a set sold instead of crafted (<see cref="ListAsync"/>).
/// A game found dear is written down (<see cref="BuyLedger.SoldSet"/>): buying cards, automatic booster packs and badge
/// crafting leave it alone for <see cref="SetSale.NotBoughtFor"/>, unless a later look finds it worth keeping after all.
/// </remarks>
public static class DearCards {
	/// <summary>Sales in the last day a card needs before its price is counted on (as for a set sold instead of crafted).</summary>
	public const int MinSales = SetSale.MinSales;

	/// <summary>Selling has to beat keeping by this many percent (as for the gems maths) - and by a whole level at least.</summary>
	public const int MarginPct = GemsChoice.MarginPct;

	/// <summary>Games priced a look, at most - each is a market request per card.</summary>
	public const int MaxGamesALook = 2;

	/// <summary>Games' sets read a look, at most, while picking which to price.</summary>
	public const int MaxSetReads = 8;

	/// <summary>Card prices asked a look, at most.</summary>
	public const int MaxQuotesALook = 30;

	/// <summary>A normal game badge's top level.</summary>
	private const int MaxLevel = 5;

	/// <summary>One card held, as it would be listed: copies, the cheapest listing now, what a buyer pays for ours, what the account gets.</summary>
	/// <param name="Sold">Listed when the game is dear - false for a card that hardly sells, or pays nothing.</param>
	public sealed record Card(string Hash, int Copies, int Lowest, int Buyer, int Receive, int Sales, bool Sold) {
		public string Name => Seller.CardName(Hash);
	}

	/// <summary>
	/// The word on one game. <paramref name="Kept"/>: the levels its cards held can still make; <paramref name="KeepCost"/>:
	/// what buying the missing cards for them costs. <paramref name="Proceeds"/>: what the cards it would list pay after
	/// Steam's fee; <paramref name="Sold"/>: about the levels that buys; <paramref name="SoldSaving"/>: with the missing
	/// cards' money not spent too - what it's weighed on. <paramref name="Foil"/>: the game's foil cards (one level at
	/// most). <paramref name="Slow"/>: not sure because a card hardly sells - how long it has waited counts.
	/// </summary>
	public sealed record Verdict(uint App, string Game, int Currency, bool Dear, bool Unsure, int Kept, long KeepCost, long Proceeds, int Sold, int SoldSaving,
		string Why, IReadOnlyList<Card> Cards, bool Foil = false, bool Slow = false) {
		/// <summary>What selling brings in, written out ("5.67 CAD").</summary>
		public string Money => BuyPlan.Money(Proceeds, Currency);

		/// <summary>The copies it would list.</summary>
		public int Listing => Cards.Where(static c => c.Sold).Sum(static c => c.Copies);

		/// <summary><see cref="Why"/> for the log, translated.</summary>
		public Said Reason { get; init; } = new(Why);
	}

	/// <summary>What one look found: a word on each game it priced, and - when it couldn't weigh any - why.</summary>
	public sealed record Look(IReadOnlyList<Verdict> Verdicts, Said? Problem = null);

	/// <summary>
	/// What a look weighs besides the plan: the foil cards held and each foil badge's level (null: foils aren't weighed), the
	/// games whose set was sold instead of crafted lately (what's left of them is sold), and since when a game's cards have
	/// waited for a card that hardly sells.
	/// </summary>
	public sealed record Extra(IReadOnlyDictionary<uint, Dictionary<string, int>>? Foils = null, IReadOnlyDictionary<uint, int>? FoilLevels = null,
		IReadOnlyCollection<uint>? Leftovers = null, Func<uint, bool, DateTime?>? WaitingSince = null);

	/// <summary>Whether it weighs at all: the same switch as a set sold instead of crafted, with buying cards on to spend the money.</summary>
	public static bool Applies(BotConfig cfg, bool buying) => SetSale.Applies(cfg, buying);

	/// <summary>The games found dear lately, while it applies - buying cards and automatic booster packs leave them alone.</summary>
	public static IReadOnlyCollection<uint> Marked(Bot bot) =>
		Applies(bot.Cfg, CardBuyer.On(bot)) ? BuyLedger.Of(bot.Name).DearSince(DateTime.UtcNow - SetSale.NotBoughtFor) : [];

	/// <summary>
	/// Badge levels the cards held can still make: one for each level that uses at least one of them (the most copies of any
	/// card), up to the badge's top level - 5, or 1 for a foil badge.
	/// </summary>
	public static int KeptLevels(int level, IReadOnlyList<string> set, IReadOnlyDictionary<string, int> held, int top = MaxLevel) =>
		Math.Max(0, Math.Min(set.Select(c => held.GetValueOrDefault(c)).DefaultIfEmpty(0).Max(), top - level));

	/// <summary>Whole badge levels a sum buys: the cheapest first, past the levels priced today at the dearest of them.</summary>
	public static int LevelsFor(long money, IReadOnlyList<long> costs) =>
		(money <= 0) || (costs.Count == 0) ? 0 : (int) Math.Min(1000, Math.Floor(GemsChoice.LevelsFor(money, costs) + 1e-9));

	/// <summary>Selling clearly wins: at least a whole level more than keeping, and <see cref="MarginPct"/> percent more.</summary>
	public static bool Wins(int sold, int kept) => (sold >= kept + 1) && (sold * 100L >= kept * (100L + MarginPct));

	/// <summary>A game not known for sure: nothing changes.</summary>
	private static Verdict NotSure(uint app, string game, int currency, Said why, int kept = 0) =>
		new(app, game, currency, false, true, kept, 0, 0, 0, 0, why.ToEnglish(), []) { Reason = why };

	/// <summary>
	/// The decision, on its own. <paramref name="set"/>: every card of the game's normal set; <paramref name="held"/>: the
	/// copies held of each; <paramref name="quotes"/>: each card's price now (missing or null: no answer) - every card held
	/// needs one, a missing card without one is counted free (which only favours keeping). <paramref name="costs"/>: what
	/// each next badge level money would buy costs, cheapest first, past what the wallet pays for. <paramref name="foil"/>:
	/// the game's foil cards and foil badge (one level). <paramref name="waited"/>: a card that hardly sells has held it back
	/// for <see cref="SetSale.WaitAtMost"/> - it's counted and sold all the same. <paramref name="leftover"/>: what's left of
	/// a set sold instead of crafted - nothing kept is weighed against it, it's sold.
	/// </summary>
	public static Verdict Decide(uint app, string game, int currency, int level, IReadOnlyList<string> set, IReadOnlyDictionary<string, int> held,
		IReadOnlyDictionary<string, MarketQuote?> quotes, IReadOnlyList<long> costs, int dumpPct, bool foil = false, bool waited = false, bool leftover = false) {
		if (currency <= 0) {
			return NotSure(app, game, currency, new Said("no price in its wallet's currency")) with { Foil = foil };
		}

		int kept = leftover ? 0 : KeptLevels(level, set, held, foil ? 1 : MaxLevel);
		List<Card> cards = [];
		(string Name, int Sales)? thin = null;

		foreach (string hash in set.Where(c => held.GetValueOrDefault(c) > 0)) {
			string name = Seller.CardName(hash);

			if ((quotes.GetValueOrDefault(hash) is not { } quote) || (quote.Currency != currency) || (quote.Lowest is not int lowest)) {
				return NotSure(app, game, currency, new Said("no market price for {0} right now", name), kept) with { Foil = foil };
			}

			if (ListingPrice.For(quote.Price, 3, dumpPct).Cents is not int buyer) {
				return NotSure(app, game, currency, new Said("{0}: its price isn't clear right now", name), kept) with { Foil = foil };
			}

			int receive = Seller.ReceiveFor(buyer);
			bool sells = waited || (quote.Sales >= MinSales);

			if (!sells && (thin == null)) {
				thin = (name, quote.Sales);
			}

			cards.Add(new Card(hash, held[hash], lowest, buyer, receive, quote.Sales, sells && (receive > 0)));
		}

		// Kept, each level still needs the cards missing for it bought: that money is spared by selling.
		long keepCost = 0;

		for (int j = 1; j <= kept; j++) {
			foreach (string hash in set.Where(c => held.GetValueOrDefault(c) < j)) {
				keepCost += (quotes.GetValueOrDefault(hash) is { } q) && (q.Currency == currency) && (q.Lowest is int price and > 0) ? price : 0;
			}
		}

		long sure = cards.Where(static c => c.Sold).Sum(static c => (long) c.Copies * c.Receive);
		long all = cards.Sum(static c => (long) c.Copies * Math.Max(0, c.Receive));
		int sold = LevelsFor(sure, costs), saving = LevelsFor(sure + keepCost, costs);
		Verdict Word(bool dear, Said why) => new(app, game, currency, dear, false, kept, keepCost, sure, sold, saving, why.ToEnglish(), cards, foil) { Reason = why };

		// What's left of a set sold instead of crafted: the set was weighed - the rest would only sit, so it's sold.
		if (leftover) {
			return sure > 0 ? Word(true, new Said("what's left of a set sold instead of crafted - it pays {0}", BuyPlan.Money(sure, currency)))
				: (thin is { } slowCard) ? NotSure(app, game, currency, new Said("{0} sold only {1} time(s) in the last day", slowCard.Name, slowCard.Sales)) with { Foil = foil, Slow = true }
				: Word(false, new Said("what's left of a set sold instead of crafted pays nothing"));
		}

		if (Wins(saving, kept)) {
			return Word(true, new Said("selling buys about {0} levels, keeping makes {1}", saving, kept));
		}

		// A card that hardly sells, and the verdict hangs on it: not sure - never listed to hang, never decided without it (until
		// it has waited long enough).
		if ((thin is { } few) && Wins(LevelsFor(all + keepCost, costs), kept)) {
			return NotSure(app, game, currency, new Said("{0} sold only {1} time(s) in the last day", few.Name, few.Sales), kept) with { Foil = foil, Slow = true };
		}

		return Word(false, new Said("keeping makes {0} level(s), selling buys about {1}", kept, saving));
	}

	/// <summary>
	/// A card's price known without asking the market, in the wallet's money: its last price in that money, else its US
	/// price at the rate learned (or as it is, when none is - only ever to pick which games to price). 0 when nothing is known.
	/// </summary>
	private static long Known(string hash, int currency, double? rate, CardSet? set) {
		if (MarketQuotes.Of(hash, currency)?.Lowest is int wallet and > 0) {
			return wallet;
		}

		int usd = SparePrices.UsdCents(hash) ?? set?.Usd.GetValueOrDefault(hash) ?? 0;

		return usd <= 0 ? 0 : rate is double r ? WalletRate.ToWallet(usd, r) : usd;
	}

	/// <summary>One game's cards a look may weigh: normal or foil, the copies held, and its badge's level and top level.</summary>
	private sealed record Candidate(uint App, bool Foil, Dictionary<string, int> Held, int Level, int Top, bool Marked, bool Leftover, long Worth);

	/// <summary>
	/// Each game worth weighing, priced now and weighed - nothing listed, bought or written down here. A game marked dear
	/// before goes first (its cards still to sell), and what's left of a set sold; then the likeliest by what's known of its
	/// cards' worth - normal cards, and foil cards for a foil badge not made yet. Games whose cards are plainly worth too
	/// little (even half again over what's known) are passed over without a price asked (nothing known: priced to find
	/// out); at most <see cref="MaxGamesALook"/> games and <see cref="MaxQuotesALook"/> prices a look. <paramref name="costs"/>: what each next level more money buys
	/// costs (<see cref="GemsChoice.CostsOf"/>) - null, not known: none weighed, and why.
	/// </summary>
	/// <param name="bought">Games it just bought cards of for a badge - their cards stay for the craft.</param>
	/// <param name="marked">Games found dear before.</param>
	public static async Task<Look> WeighAsync(BuyPlan plan, ICardMarket market, BotConfig cfg, IReadOnlyCollection<uint> bought, IReadOnlyCollection<uint> marked,
		IReadOnlyList<long>? costs, CancellationToken ct, Extra? extra = null) {
		int cur = plan.Currency;

		if ((cur <= 0) || (plan.Problem.Length > 0)) {
			return new Look([], new Said("couldn't price its cards"));
		}

		if (plan.Block != BuyBlock.None) {
			return new Look([], new Said("Steam won't let it buy cards, so the money couldn't be spent"));
		}

		SpareRules rules = SpareRules.From(cfg);
		int dump = Math.Clamp(cfg.SpareDumpPct, 0, 95);
		double? rate = WalletRate.ForDeciding(cur);
		IReadOnlyCollection<uint> leftovers = extra?.Leftovers ?? [];
		bool Kept(uint app, Dictionary<string, int> held) => rules.KeepGames.Contains(app) || bought.Contains(app)
			|| held.Where(static c => c.Value > 0).Any(c => rules.KeepNames.Contains(c.Key) || rules.KeepNames.Contains(Seller.CardName(c.Key)));
		long Worth(Dictionary<string, int> held) => held.Sum(c => (long) c.Value * Known(c.Key, cur, rate, null));

		// Normal cards of a badge it's still levelling, foil cards of a foil badge not made yet - never the keep list's games
		// or cards, nor a game just bought for.
		List<Candidate> games = [.. plan.Held.Where(h => h.Value.Values.Any(static n => n > 0) && (plan.Levels.GetValueOrDefault(h.Key) < MaxLevel) && !Kept(h.Key, h.Value))
			.Select(h => new Candidate(h.Key, false, h.Value, plan.Levels.GetValueOrDefault(h.Key), MaxLevel, marked.Contains(h.Key), leftovers.Contains(h.Key) && !marked.Contains(h.Key), Worth(h.Value)))];

		if ((extra?.Foils is { } foils) && (extra.FoilLevels is { } foilLevels)) {
			games.AddRange(foils.Where(h => h.Value.Values.Any(static n => n > 0) && (foilLevels.GetValueOrDefault(h.Key) < 1) && !Kept(h.Key, h.Value))
				.Select(h => new Candidate(h.Key, true, h.Value, foilLevels.GetValueOrDefault(h.Key), 1, false, false, Worth(h.Value))));
		}

		if (games.Count == 0) {
			return new Look([]);
		}

		// What more money buys has to be known: never weighed on half a list, or on levels the wallet pays for already.
		if (costs == null) {
			return new Look([], new Said("what money buys isn't known right now - buying cards hasn't priced its next levels"));
		}

		List<Verdict> verdicts = [];
		int reads = 0, asked = 0;

		foreach (Candidate g in games.OrderByDescending(static g => g.Marked || g.Leftover).ThenByDescending(static g => g.Worth)
			.ThenByDescending(static g => g.Held.Values.Sum()).ThenBy(static g => g.App).ThenBy(static g => g.Foil)) {
			if ((verdicts.Count >= MaxGamesALook) || (reads >= MaxSetReads)) {
				break;
			}

			reads++;

			if (await market.SetAsync(g.App, ct).ConfigureAwait(false) is not { } normal) {
				break;   // the market stopped answering: the rest wait for the next look
			}

			if (normal.Cards.Count == 0) {
				continue;   // not known whole - nothing is weighed on half a set
			}

			CardSet set = g.Foil ? SetSale.FoilSet(normal) : normal;
			Dictionary<string, int> held = g.Held;

			// A foil card whose name isn't its normal card's with " (Foil)" after it: not known for sure which set it's of.
			if (held.Any(c => (c.Value > 0) && !set.Cards.Contains(c.Key))) {
				continue;
			}

			// What's left of a set sold: only while no whole set is held again - a whole set is weighed as any.
			bool leftover = g.Leftover && !set.Cards.All(c => held.GetValueOrDefault(c) > 0);
			int kept = KeptLevels(g.Level, set.Cards, held, g.Top);

			if ((kept <= 0) && !leftover) {
				continue;
			}

			// Plainly worth too little, even half again over what's known: kept without asking the market. A game found dear
			// before (or what's left of a set sold) is always priced again - to finish selling it, or to find it's worth keeping.
			long guess = set.Cards.Sum(c => (held.GetValueOrDefault(c) * (long) Seller.ReceiveFor(Seller.UndercutFor((int) Math.Min(int.MaxValue, Known(c, cur, rate, normal)))))
				+ (Math.Max(0, kept - held.GetValueOrDefault(c)) * Known(c, cur, rate, normal)));

			// Nothing known at all about what its cards are worth (a foil card no price was ever seen for): priced to find out.
			bool known = set.Cards.Any(c => (held.GetValueOrDefault(c) > 0) && (Known(c, cur, rate, normal) > 0));

			if (!g.Marked && !leftover && known && !Wins(LevelsFor(guess * 3 / 2, costs), kept)) {
				continue;
			}

			// Not enough prices left this look for the whole set: the next look, rather than half of it.
			if ((asked > 0) && (asked + set.Cards.Count > MaxQuotesALook)) {
				break;
			}

			// Priced now: every card held, then the cards the kept levels are missing (every other card of the set).
			Dictionary<string, MarketQuote?> quotes = new(StringComparer.Ordinal);
			bool stopped = false;

			foreach (string hash in set.Cards.OrderByDescending(c => held.GetValueOrDefault(c) > 0)) {
				if (asked >= MaxQuotesALook) {
					stopped = true;

					break;
				}

				asked++;
				MarketQuote? quote = await market.QuoteAsync(hash, cur, ct).ConfigureAwait(false);
				quotes[hash] = quote;

				if (quote == null) {
					stopped = true;   // the market stopped answering: not sure

					break;
				}
			}

			bool waited = (extra?.WaitingSince?.Invoke(g.App, g.Foil) is DateTime since) && (DateTime.UtcNow - since >= SetSale.WaitAtMost);
			Verdict v = stopped && set.Cards.Any(c => (held.GetValueOrDefault(c) > 0) && (quotes.GetValueOrDefault(c) == null))
				? NotSure(g.App, set.Game, cur, new Said("couldn't price its cards"), kept) with { Foil = g.Foil }
				: Decide(g.App, set.Game, cur, g.Level, set.Cards, held, quotes, costs, dump, g.Foil, waited, leftover);
			verdicts.Add(v);

			if (stopped) {
				break;
			}
		}

		return new Look(verdicts);
	}

	// ── the log: each game's word once, and what's written down ─────────────
	private static readonly ConcurrentDictionary<string, string> SaidLast = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Forget what was said - the checks.</summary>
	public static void ForgetSaid() => SaidLast.Clear();

	/// <summary>
	/// A look's verdicts acted on, nothing listed yet: each game found dear is written down (buying and booster packs leave it
	/// alone from now), a game found worth keeping after all is no longer marked, and a game not known for sure is said once
	/// (at Debug - only an outcome is worth the owner's eye) - until its reason changes. The games to list, dear ones.
	/// </summary>
	public static List<Verdict> Note(string account, Look look) {
		BuyLedger ledger = BuyLedger.Of(account);
		DateTime now = DateTime.UtcNow;
		List<Verdict> dear = [];

		if (look.Problem is not { } problem) {
			SaidLast.TryRemove(Key(account, 0), out _);
		} else if (SaidOnce(account, 0, problem.English)) {
			Log.Debug(new Said("cards worth more sold than kept: not weighed · {0}", problem), account);
		}

		foreach (Verdict v in look.Verdicts) {
			// Held back by a card that hardly sells: since when, so it's sold all the same after a while.
			if (v.Slow || !v.Unsure) {
				ledger.Wait(v.App, v.Foil, v.Slow, now);
			}

			if (v.Unsure) {
				if (SaidOnce(account, v.App, v.Why)) {
					Log.Debug(new Said("{0}: cards kept - not sure they're worth more sold · {1}", v.Game, v.Reason), account);
				}

				continue;
			}

			SaidLast.TryRemove(Key(account, v.App), out _);

			// A foil game is never marked: the mark keeps buying and packs off its normal cards.
			if (v.Foil) {
				if (v.Dear) {
					dear.Add(v);
				}

				continue;
			}

			if (!v.Dear) {
				if (ledger.ForgetDear(v.App)) {
					Log.Debug(new Said("{0}: its cards are worth keeping again · {1}", v.Game, v.Reason), account);
				}

				continue;
			}

			if (!ledger.DearSince(DateTime.UtcNow - SetSale.NotBoughtFor).Contains(v.App)) {
				ledger.AddSold(new BuyLedger.SoldSet(DateTime.UtcNow, v.App, Dear: true));
			}

			dear.Add(v);
		}

		return dear;
	}

	private static string Key(string account, uint app) => $"{account}|{app}";

	private static bool SaidOnce(string account, uint app, string why) {
		string key = Key(account, app);

		if (SaidLast.TryGetValue(key, out string? was) && (was == why)) {
			return false;
		}

		SaidLast[key] = why;

		return true;
	}

	/// <summary>The games a look lists: one a look on a human-mode account (like somebody selling a game's cards now and then), all of them otherwise.</summary>
	public static IReadOnlyList<Verdict> ThisLook(IReadOnlyList<Verdict> dear, bool human) => human ? [.. dear.Take(1)] : dear;

	/// <summary>The line said for a dear game as its cards go up: "Arcane Raise: 7 cards worth more than their levels - selling for about 5.70 CAD ≈ 15 cheap levels".</summary>
	public static Said SellingSaid(Verdict v) => v.Foil
		? new Said("{0}: {1} foil cards worth more than their level - selling for about {2} ≈ {3} cheap levels", v.Game, v.Listing, v.Money, v.Sold)
		: new Said("{0}: {1} cards worth more than their levels - selling for about {2} ≈ {3} cheap levels", v.Game, v.Listing, v.Money, v.Sold);

	/// <summary>One card's line, for the debug log: "Heart (Arcane Raise) x2 · lowest 1.52 CAD · you get 1.31 CAD each".</summary>
	public static Said CardSaid(Verdict v, Card c) =>
		new Said("{0} ({1}) x{2} · lowest {3} · you get {4} each", c.Name, v.Game, c.Copies, BuyPlan.Money(c.Lowest, v.Currency), BuyPlan.Money(c.Receive, v.Currency));

	// ── listing ─────────────────────────────────────────────────────────────
	/// <summary>The listing offers for a dear game: every copy held of each card it sells, as many as are in the inventory.</summary>
	public static List<Seller.Offer> Offers(Verdict verdict, IReadOnlyDictionary<string, List<ulong>> copies) {
		List<Seller.Offer> offers = [];

		foreach (Card c in verdict.Cards.Where(static c => c.Sold)) {
			if (copies.TryGetValue(c.Hash, out List<ulong>? ids)) {
				offers.AddRange(ids.Take(c.Copies).Select(id => new Seller.Offer(id, verdict.Game, c.Name, c.Lowest, c.Buyer, c.Receive, c.Hash)));
			}
		}

		return offers;
	}

	/// <summary>
	/// List a dear game's cards: each copy a cent under the cheapest, priced again right before it goes up and kept when it
	/// would pay over a tenth less than it was weighed at (<see cref="SetSale.StillPays"/>). Through the same path as a set
	/// sold instead of crafted: claimed, never a card in a waiting trade or promised to a card swap, never the keep list's,
	/// each listing waiting for the owner's phone like any (or this account's authenticator). Its cards may go above "Never
	/// sell above" - this path only. A human-mode account lists only in its day, minutes apart. How many went up.
	/// </summary>
	public static async Task<int> ListAsync(Bot bot, Verdict verdict, InventoryContents inventory, CancellationToken ct) {
		List<Seller.Offer> offers = Offers(verdict, SetSale.Copies(inventory, verdict.App, verdict.Foil));

		if (offers.Count == 0) {
			return 0;
		}

		int floor = Math.Clamp(bot.Cfg.SpareRelistFloorPct, 1, 100), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);

		async Task<(Seller.Offer? Offer, Said Why)> Reprice(Seller.Offer offer, CancellationToken token) {
			(Seller.Offer? now, Said why) = await Seller.RepriceAsync(offer, verdict.Currency, 3, dump, null, null, token, floorPct: floor,
				ours: OurListings.UpFor(OurListings.Load(bot.Name), offer.Hash)).ConfigureAwait(false);

			return (now != null) && !SetSale.StillPays(offer, now) ? (null, new Said("its price fell since it was weighed - not listed")) : (now, why);
		}

		HumanGate? gate = bot.Cfg.LegitMode ? BotManager.ModuleOf<CardBuyer>(bot)?.Gate ?? HumanGate.OwnDay(bot) : null;
		int listed = 0;

		await Seller.SellAsync(bot, offers, ct, mayAct: gate == null ? null : () => gate.Open, reprice: Reprice, onListed: _ => listed++,
			pause: gate == null ? null : static t => Task.Delay(Rng.HumanMinutes(1, 4), t)).ConfigureAwait(false);

		return listed;
	}

	// ── the preview ─────────────────────────────────────────────────────────
	/// <summary>
	/// The preview, written out: each game weighed - every card with its price and what it pays, the levels kept against the
	/// levels sold, and the word. English, like every console reply. Nothing was listed.
	/// </summary>
	public static List<string> PreviewLines(string name, Look look, string off = "") {
		List<string> lines = [];

		if (off.Length > 0) {
			lines.Add($"{name}: cards worth more sold than kept - not weighed: {off}.");

			return lines;
		}

		if (look.Problem is { } problem) {
			lines.Add($"{name}: cards worth more sold than kept - not weighed: {problem.ToEnglish()}; nothing changes.");

			return lines;
		}

		if (look.Verdicts.Count == 0) {
			lines.Add($"{name}: cards worth more sold than kept - none: every game's cards make more levels kept.");

			return lines;
		}

		lines.Add($"{name}: cards worth more sold than kept (selling sets for levels):");

		foreach (Verdict v in look.Verdicts) {
			string M(long cents) => BuyPlan.Money(cents, v.Currency);

			string game = v.Foil ? $"{v.Game} foil cards" : v.Game;

			if (v.Unsure) {
				lines.Add($"  {game}: not sure - {v.Why}; its cards are kept.");

				continue;
			}

			lines.Add($"  {game}: {(v.Dear ? "sell" : "keep")} - {v.Why}");

			foreach (Card c in v.Cards) {
				string paid = c.Sold ? $"you get {M(c.Receive)} each" : c.Receive <= 0 ? "pays nothing - kept" : $"sold only {c.Sales} time(s) in the last day - kept";
				lines.Add($"    {Columns.PadRight(c.Name, 24)} x{c.Copies}  lowest {M(c.Lowest)} -> list at {M(c.Buyer)}, {paid}");
			}

			lines.Add($"    kept: {v.Kept} level(s){(v.KeepCost > 0 ? $", after buying {M(v.KeepCost)} of missing cards" : "")} · sold: {v.Money} after Steam's fee ≈ {v.Sold} cheap level(s){(v.KeepCost > 0 ? $", {v.SoldSaving} with the {M(v.KeepCost)} not spent" : "")}");
		}

		lines.Add(look.Verdicts.Any(static v => v.Dear)
			? $"{name}: nothing listed - buying cards lists them at its next look ('buy do {name}' looks now); you confirm each listing in the Steam app, like any."
			: $"{name}: nothing to list.");

		return lines;
	}
}
