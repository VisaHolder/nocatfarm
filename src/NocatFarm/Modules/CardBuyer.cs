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
/// app - /market/userbillinginfo says which (confirmation_type 1 email, 2 mobile), with the wallet (wallet_info) and
/// whether billing details are needed first (require_billing_info).</item>
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
	/// <param name="Confirm">Steam's confirmation_type: 0 none, 1 by email, 2 in the mobile app; -1 when it wasn't said.</param>
	public sealed record Wallet(long Balance, int Currency, bool NeedsBilling, int Confirm);

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
				Flag(root, "require_billing_info"), confirm);
		} catch (JsonException) {
			return null;
		}
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

	/// <summary>Billing details are wanted before its first market purchase.</summary>
	Billing,

	/// <summary>Every purchase confirmed by email.</summary>
	Email,

	/// <summary>Every purchase confirmed in the Steam app, and its authenticator isn't in nocat.farm.</summary>
	App
}

/// <summary>How one card purchase went.</summary>
public enum BuyResult {
	/// <summary>Bought.</summary>
	Bought,

	/// <summary>Nobody was selling at that price any more: the order was taken back, nothing bought.</summary>
	Gone,

	/// <summary>Steam said no - the reason is in the message. Nothing bought.</summary>
	Refused,

	/// <summary>No answer: it may have been bought. Counted as spent.</summary>
	NoAnswer
}

/// <summary>One purchase's answer: how it went, what it cost, and Steam's reason when it said no.</summary>
public sealed record BuyAnswer(BuyResult Result, long Paid, string Message = "");

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

	/// <summary>Buy one card for at most <paramref name="price"/> (the buyer's price, fees in), in <paramref name="currency"/>. Spends money.</summary>
	Task<BuyAnswer> BuyAsync(uint app, string hash, long price, int currency, CancellationToken ct);
}

/// <summary>
/// Card swaps, as buying cards asks them: whether a missing card could come by a swap instead, and for how much. Card
/// swapping answers through <see cref="SwapCost.Current"/> once it's there; until then there's no swap on offer and
/// every missing card is bought.
/// </summary>
public interface ICardSwaps {
	/// <summary>
	/// What getting this card by a swap costs, in the wallet's currency (what goes out for it, priced), or null when no swap
	/// for it is on offer. Read only - asking never starts a swap.
	/// </summary>
	Task<long?> SwapCostAsync(Bot? bot, uint app, string hash, CancellationToken ct);
}

/// <summary>Where buying cards finds card swapping. None for now.</summary>
public static class SwapCost {
	private sealed class NoSwaps : ICardSwaps {
		public Task<long?> SwapCostAsync(Bot? bot, uint app, string hash, CancellationToken ct) => Task.FromResult<long?>(null);
	}

	/// <summary>No swap on offer, ever.</summary>
	public static readonly ICardSwaps None = new NoSwaps();

	/// <summary>The swaps buying cards asks - card swapping sets this when it starts.</summary>
	public static ICardSwaps Current { get; set; } = None;
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

	/// <summary>Why something wasn't bought, in plain words (English - console replies stay English).</summary>
	public List<string> Notes { get; } = [];

	/// <summary>Why it couldn't look at all - empty when it could.</summary>
	public string Problem { get; set; } = "";

	/// <summary>Why Steam won't let it buy by itself, or None: billing details first, or every purchase confirmed in a way it can't.</summary>
	public BuyBlock Block { get; set; }

	/// <summary><see cref="Block"/> in plain words (English, for the console). Empty when nothing blocks it.</summary>
	public string Blocked => Block switch {
		BuyBlock.Billing => "Steam wants billing details before this account's first market purchase - buy one card by hand once, then it can buy",
		BuyBlock.Email => "Steam asks for every market purchase on this account to be confirmed by email, which nocat.farm can't do",
		BuyBlock.App => "Steam asks for every market purchase on this account to be confirmed in the Steam app - add its authenticator to nocat.farm so it can confirm its own purchases",
		_ => ""
	};

	/// <summary><see cref="Block"/> for the log, translated.</summary>
	public Said BlockedSaid => Block switch {
		BuyBlock.Billing => new Said("Steam wants billing details before this account's first market purchase - buy one card by hand once, then it can buy"),
		BuyBlock.Email => new Said("Steam asks for every market purchase on this account to be confirmed by email, which nocat.farm can't do"),
		BuyBlock.App => new Said("Steam asks for every market purchase on this account to be confirmed in the Steam app - add its authenticator to nocat.farm so it can confirm its own purchases"),
		_ => new Said("")
	};

	public long Cost => Steps.Sum(static s => s.Cost);
	public int CardCount => Steps.Sum(static s => s.Cards.Count);

	/// <summary>Market reads one plan may make: sets, and cards' prices.</summary>
	internal const int MaxSetReads = 12, MaxBookReads = 30, MaxFullSetGames = 3;

	/// <summary>A normal game badge's top level.</summary>
	internal const int MaxLevel = 5;

	/// <summary>Money as typed (1.50) in cents (150), clamped.</summary>
	public static long Cents(float money) => Math.Clamp(SpareRules.Cents(Math.Clamp(money, 0f, 1_000_000f)), 0, 100_000_000);

	/// <summary>"0.35 CAD".</summary>
	public static string Money(long cents, int currency) => Spare.Money(cents, currency);

	/// <summary>
	/// The plan. Missing cards first: for each badge the account is levelling (cards of its set held, badge under level 5),
	/// the cards its next level still needs, closest badge first - the fewest missing, then the cheaper - as automatic booster
	/// packs pick. Then, with "Buy the cheapest full sets" on, whole sets for games it has none of, cheapest first. A set is
	/// only ever planned whole: every missing card on the market, and all of it inside what may be spent now. One that costs
	/// more than a day's or a week's limit is passed over (said why); one that doesn't fit what's left of today stops the plan
	/// there - the closest goes first, not a cheaper one further away.
	/// </summary>
	public static async Task<BuyPlan> MakeAsync(BotConfig cfg, ICardMarket market, ICardSwaps swaps, BuyLedger ledger, Bot? bot, DateTime nowUtc, CancellationToken ct) {
		BuyPlan plan = new() {
			DayLimit = Cents(cfg.BuyDailyLimit),
			WeekLimit = Cents(cfg.BuyWeeklyLimit),
			DaySpent = ledger.SpentSince(nowUtc.AddDays(-1)),
			WeekSpent = ledger.SpentSince(nowUtc.AddDays(-7))
		};

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

		plan.Block = wallet.NeedsBilling ? BuyBlock.Billing
			: wallet.Confirm == 1 ? BuyBlock.Email
			: (wallet.Confirm == 2) && !(bot?.CanConfirmTrades ?? false) ? BuyBlock.App
			: BuyBlock.None;

		plan.Room = Math.Max(0, Math.Min(Math.Min(plan.DayLimit - plan.DaySpent, plan.WeekLimit - plan.WeekSpent), wallet.Balance));

		if (await market.BadgeLevelsAsync(ct).ConfigureAwait(false) is not { } levels) {
			plan.Problem = "Steam wouldn't say what its badges are";

			return plan;
		}

		if (await market.CardsAsync(ct).ConfigureAwait(false) is not { } held) {
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

		// ── the missing cards of badges it's levelling, closest first ──
		// Games with cards held and a badge under level 5, the most different cards held first - each set read costs a market
		// search, so the likeliest are read first.
		List<uint> levelling = [.. held.Where(h => (levels.GetValueOrDefault(h.Key) < MaxLevel) && h.Value.Values.Any(static n => n > 0))
			.OrderByDescending(static h => h.Value.Count(static c => c.Value > 0)).ThenBy(static h => h.Key).Select(static h => h.Key)];
		List<(CardSet Set, int Level, List<string> Missing)> closest = [];
		int sets = 0;

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

			// The sets held already take it to level 5 - nothing to buy.
			if (complete >= MaxLevel - level) {
				continue;
			}

			List<string> missing = [.. set.Cards.Where(c => cards.GetValueOrDefault(c) <= complete)];

			if ((missing.Count > 0) && (missing.Count < set.Cards.Count)) {
				closest.Add((set, level + complete, missing));
			}
		}

		// Fewest missing first, then the cheaper by the market's last search (US cents, only to order them).
		foreach ((CardSet set, int level, List<string> missing) in closest.OrderBy(static c => c.Missing.Count)
			.ThenBy(c => c.Missing.Sum(h => c.Set.Usd.GetValueOrDefault(h))).ThenBy(static c => c.Set.App)) {
			if (stopped) {
				break;
			}

			if (await PricesAsync(missing).ConfigureAwait(false) is not { } prices) {
				stopped = true;

				break;
			}

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

			long cost = prices.Sum(static p => p.Book.Lowest);

			if (Fits(set.Game, cost) is not { } fits) {
				stopped = true;

				break;
			}

			if (fits) {
				plan.Steps.Add(new BuyStep(set.App, set.Game, false, level, set.Cards.Count, [.. prices.Select(static p => new BuyCard(p.Hash, p.Book.Lowest))]));
				left -= cost;
			}
		}

		// ── the cheapest full sets, for games it has none of ──
		if (cfg.BuyFullSets && !stopped) {
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
					if ((set.Cards.Count > 0) && set.Cards.All(c => set.Usd.GetValueOrDefault(c) > 0)) {
						found.Add(set);
					}
				}

				// Priced in the wallet's own money, the few cheapest by the search; then the cheapest of those that's steady.
				List<BuyStep> full = [];

				foreach (CardSet set in found.OrderBy(static s => s.UsdCents).ThenBy(static s => s.App).Take(MaxFullSetGames)) {
					if (await PricesAsync(set.Cards).ConfigureAwait(false) is not { } prices) {
						break;
					}

					if (prices.FirstOrDefault(p => p.Book.Listings < 2) is { Hash: not null } thin) {
						plan.Notes.Add($"{set.Game}: left out - {new BuyCard(thin.Hash, 0).Name} has fewer than 2 listings");

						continue;
					}

					if (prices.FirstOrDefault(p => p.Book.Dumped) is { Hash: not null } dumped) {
						plan.Notes.Add($"{set.Game}: left out - one cheap listing of {new BuyCard(dumped.Hash, 0).Name} makes the set look cheaper than it is");

						continue;
					}

					full.Add(new BuyStep(set.App, set.Game, true, levels.GetValueOrDefault(set.App), set.Cards.Count, [.. prices.Select(static p => new BuyCard(p.Hash, p.Book.Lowest))]));
				}

				foreach (BuyStep step in full.OrderBy(static s => s.Cost).ThenBy(static s => s.App)) {
					if (Fits(step.Game, step.Cost) is not { } fits) {
						break;
					}

					if (fits) {
						plan.Steps.Add(step);
						left -= step.Cost;
					}
				}
			}
		}

		if ((plan.Steps.Count == 0) && (plan.Notes.Count == 0)) {
			plan.Notes.Add(closest.Count == 0 && !cfg.BuyFullSets
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
	/// <param name="Pending">Steam didn't answer: counted as spent, since it may have been bought.</param>
	public sealed record Entry(DateTime At, long Cents, int Currency, uint App, string Card, bool Pending = false);

	internal sealed class Stored {
		public bool Ok { get; set; }
		public List<Entry> Entries { get; set; } = [];
	}

	private List<Entry>? _entries;
	private bool _ok, _onDisk;
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

	public void SetOk(bool ok) {
		lock (_gate) {
			Load();
			_ok = ok;
			Save();
		}
	}

	public long SpentSince(DateTime utc) {
		lock (_gate) {
			return Load().Where(e => e.At >= utc).Sum(static e => e.Cents);
		}
	}

	public IReadOnlyList<Entry> All {
		get {
			lock (_gate) {
				return [.. Load()];
			}
		}
	}

	public void Add(Entry entry) {
		lock (_gate) {
			List<Entry> all = Load();
			all.Add(entry);
			all.RemoveAll(static e => e.At < DateTime.UtcNow.AddDays(-60));   // two months is plenty for a week's limit
			Save();
		}
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(new Stored { Ok = _ok, Entries = _entries ?? [] }));
			_onDisk = true;
		} catch (Exception e) {
			// Kept in memory either way, so this run's limits still hold.
			Log.Debug(new Said("couldn't save the card buying list: {0}", Log.Describe(e)));
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
			_ok = read?.Ok ?? false;
		} catch (Exception e) {
			// A list that can't be read counts the week as already spent, rather than nothing spent - and no go-ahead.
			Log.Debug(new Said("couldn't read the card buying list: {0}", Log.Describe(e)));
			_entries = [new Entry(DateTime.UtcNow, long.MaxValue / 4, 0, 0, "unreadable list")];
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
/// "Spend at most, a week" in any 7 days, nor more than the wallet holds, and reads the wallet and each card's price again
/// right before buying - a card dearer than planned isn't bought. Only whole sets: never 3 of 5 cards on purpose. Each card
/// is one buy order at the cheapest listing's price for one card, which fills at once or is taken back. A human-mode
/// account buys only in its own day, one set a look, spaced out. Bought cards are crafted by badge crafting (which has to
/// be on - otherwise nothing is bought).
/// </remarks>
public sealed class CardBuyer(Bot bot, ICardMarket? market = null, ICardSwaps? swaps = null) : BotModule(bot) {
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

		/// <summary>Why it couldn't look or carry on (English). Empty when all went well.</summary>
		public string Problem { get; set; } = "";
	}

	private readonly ICardMarket _market = market ?? new SteamCardMarket(bot);
	private ICardSwaps Swaps => swaps ?? SwapCost.Current;
	private readonly SemaphoreSlim _busy = new(1, 1);
	private HumanGate? _gate;
	private Said _status = new("");
	private BuyBlock _saidBlocked = BuyBlock.None;

	/// <summary>When the next look is - from "Look every (hours)" as it's set now.</summary>
	internal LookTimer Timer { get; } = new(() => Math.Clamp(bot.Cfg.BuyCheckHours, 1, 168), 1.0, 1.2);

	public override string Name => "buying cards";
	public override string Status => Bot.Cfg.BuyCards ? _status : "";

	public BuyLedger Ledger => BuyLedger.Of(Bot.Name);

	/// <summary>The gate a human-mode account's buying waits on: its own day (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>Whether this account buys: the switch and its go-ahead together.</summary>
	public static bool On(Bot bot) => bot.Cfg.BuyCards && BuyLedger.Of(bot.Name).Ok;

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Minutes(10, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (On(Bot) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && Timer.Due(DateTime.UtcNow) && Gate.Open) {
				Timer.Looked(DateTime.UtcNow);

				try {
					Result result = await LookAsync(Mode.Auto, ct).ConfigureAwait(false);

					if (result.Bought.Count > 0) {
						Gate.Space(30, 120);
					}

					if (result.Problem.Length > 0) {
						Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
					}

					_status = new Said("buying cards: next look around {0}", (Func<string>) (() => Fmt.Clock(Timer.Next)));
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at buying cards: {0}", Log.Describe(e)), Bot.Name);
					Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
				}
			}

			if (!await Sleep(Rng.Minutes(2, 5), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>Look, and - outside a preview, while it's on and the day allows - buy what the plan says. One look at a time.</summary>
	public async Task<Result> LookAsync(Mode mode, CancellationToken ct) {
		await _busy.WaitAsync(ct).ConfigureAwait(false);

		try {
			return await LookLockedAsync(mode, ct).ConfigureAwait(false);
		} finally {
			_busy.Release();
		}
	}

	private async Task<Result> LookLockedAsync(Mode mode, CancellationToken ct) {
		Result result = new();
		BuyPlan plan = await BuyPlan.MakeAsync(Bot.Cfg, _market, Swaps, Ledger, Bot, DateTime.UtcNow, ct).ConfigureAwait(false);
		result.Plan = plan;

		if (plan.Problem.Length > 0) {
			result.Problem = plan.Problem;

			return result;
		}

		if ((mode == Mode.Preview) || (plan.Steps.Count == 0)) {
			return result;
		}

		if (!On(Bot)) {
			result.Problem = "buying cards is off";

			return result;
		}

		if (!Bot.Cfg.CraftBadges) {
			result.Problem = "\"Craft badges from card sets\" is off, so bought cards would only sit there - nothing bought";

			return result;
		}

		if (plan.Blocked.Length > 0) {
			result.Problem = plan.Blocked;

			// Said once with a notification, not every look.
			if (_saidBlocked != plan.Block) {
				_saidBlocked = plan.Block;
				Log.Attention(plan.BlockedSaid, Bot.Name);
			}

			return result;
		}

		if (!Gate.Open) {
			result.Problem = $"not now - {Gate.Waiting().ToEnglish()}";

			return result;
		}

		if (ScamWatch.HeldBack(Bot) is { } held) {
			result.Problem = held;

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

	/// <summary>Buy one set's cards, each checked again first. False to stop there (said why in the result).</summary>
	private async Task<bool> BuyStepAsync(BuyStep step, int currency, Result result, CancellationToken ct) {
		int got = 0;

		foreach (BuyCard card in step.Cards) {
			// A person clicking through the market, not a list being emptied - slower still on a human-mode account.
			if (result.Bought.Count > 0) {
				await Task.Delay(Bot.Cfg.LegitMode ? Rng.Seconds(20, 90) : Rng.Seconds(3, 10), ct).ConfigureAwait(false);
			}

			// The rules once more, on what's true now - the last word before anything is spent.
			if (!On(Bot)) {
				result.Problem = "buying cards was turned off - stopped";

				return false;
			}

			if (!Gate.Open) {
				result.Problem = $"not now - {Gate.Waiting().ToEnglish()}";

				return false;
			}

			if (await _market.BookAsync(card.Hash, ct).ConfigureAwait(false) is not { } book || (book.Lowest <= 0) || (book.Currency != currency)) {
				result.Problem = $"{card.Name} isn't on the market at a price it can read now - it looks again next time";

				return false;
			}

			if (book.Lowest > card.Price) {
				result.Problem = $"{card.Name} costs more than planned now ({BuyPlan.Money(book.Lowest, currency)}, was {BuyPlan.Money(card.Price, currency)}) - it looks again next time";

				return false;
			}

			if (await _market.WalletAsync(ct).ConfigureAwait(false) is not { } wallet || (wallet.Currency != currency)) {
				result.Problem = "Steam didn't say what's in its wallet";

				return false;
			}

			DateTime now = DateTime.UtcNow;
			long dayLeft = BuyPlan.Cents(Bot.Cfg.BuyDailyLimit) - Ledger.SpentSince(now.AddDays(-1));
			long weekLeft = BuyPlan.Cents(Bot.Cfg.BuyWeeklyLimit) - Ledger.SpentSince(now.AddDays(-7));

			if ((book.Lowest > dayLeft) || (book.Lowest > weekLeft) || (book.Lowest > wallet.Balance)) {
				result.Problem = "that would go over the day's or the week's limit, or more than the wallet holds - nothing more bought";

				return false;
			}

			BuyAnswer answer;

			try {
				answer = await _market.BuyAsync(step.App, card.Hash, book.Lowest, currency, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				answer = new BuyAnswer(BuyResult.NoAnswer, book.Lowest, Log.Describe(e));
			}

			switch (answer.Result) {
				case BuyResult.Bought:
					long paid = answer.Paid > 0 ? Math.Min(answer.Paid, book.Lowest) : book.Lowest;
					Ledger.Add(new BuyLedger.Entry(DateTime.UtcNow, paid, currency, step.App, card.Hash));
					result.Bought.Add((step, card, paid));
					got++;
					Log.Good(new Said("bought {0} · {1} · {2}", card.Name, step.Game, BuyPlan.Money(paid, currency)), Bot.Name);

					break;
				case BuyResult.NoAnswer:
					// It may have gone through: counted as spent, never bought a second time meanwhile.
					Ledger.Add(new BuyLedger.Entry(DateTime.UtcNow, book.Lowest, currency, step.App, card.Hash, Pending: true));
					Log.Warn(new Said("no answer buying {0} · counted as spent", card.Name), Bot.Name);
					result.Problem = $"Steam didn't answer about buying {card.Name} - counted as spent";

					return false;
				case BuyResult.Gone:
					result.Problem = $"{card.Name} was bought by someone else first - it looks again next time";

					return false;
				default:
					Log.Warn(answer.Message.Length > 0 ? new Said("buying {0} turned down · {1}", card.Name, answer.Message) : new Said("buying {0} turned down", card.Name), Bot.Name);
					result.Problem = $"Steam turned it down{(answer.Message.Length > 0 ? $": {answer.Message}" : "")}";

					return false;
			}
		}

		if (got == step.Cards.Count) {
			result.Finished.Add(step);

			// The set is whole: badge crafting looks soon rather than at its daily round.
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete: true);
		}

		return true;
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
