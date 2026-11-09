using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>What one item in an offer is worth, in each basis a rule may ask for. Null means "not known"; 0 is a real zero.</summary>
public sealed record ValuedItem(TradeOffers.Item Item, SpareKind Kind, int? Gems, int? WalletCents, int? SetSize, bool SetKnown, string? Problem) {
	public int Count => (int) Math.Min(int.MaxValue, Math.Max(1u, Item.Amount));
}

/// <summary>Where a rule's values come from. The real one talks to Steam; the tests use a fake with no network.</summary>
public interface ITradeValueSource {
	/// <summary>The account's wallet currency, 0 when Steam hasn't said.</summary>
	int Currency { get; }

	DateTime Now { get; }

	SpareKind KindOf(TradeOffers.Item item);

	/// <summary>The item's gem value from what's held already - never a request.</summary>
	int? Gems(TradeOffers.Item item);

	/// <summary>The item's gem value, asking Steam when it isn't held (budgeted).</summary>
	Task<int?> GemsAsync(TradeOffers.Item item, CancellationToken ct);

	/// <summary>A price held in the wallet's currency - never a request.</summary>
	MarketQuote? Quote(string hash);

	/// <summary>A price now, in the wallet's currency (budgeted).</summary>
	Task<MarketQuote?> QuoteAsync(string hash, CancellationToken ct);

	/// <summary>A game's normal card set from what's known - never a request.</summary>
	CardSet? Set(uint app);

	/// <summary>A game's normal card set, asking the market when it isn't known (budgeted).</summary>
	Task<CardSet?> SetAsync(uint app, CancellationToken ct);

	/// <summary>US cents to the wallet's currency at the learned rate - null until it is known.</summary>
	double? Rate(int currency);

	/// <summary>What a sack of 1,000 gems pays after Steam's fee, in the wallet's currency.</summary>
	int? SackNetCents();

	/// <summary>Loose gems in wallet cents: only for an explicit looseGems policy, never a market price.</summary>
	int? LooseGemWalletCents(int gems, string looseGemsMode);
}

/// <summary>How far one look may go for a price.</summary>
public enum TradeValueLookups {
	/// <summary>Only prices already held. A missing one stays unknown; no request is made.</summary>
	Cached,

	/// <summary>A price already held is used as it is, however old; one that isn't held is asked for. The default.</summary>
	Missing,

	/// <summary>Like <see cref="Missing"/>, and a held price too old to value the item is asked for again - the second pass.</summary>
	Stale
}

/// <summary>
/// The two sides of one offer, valued lazily: a rule about counts reads nothing, a gem rule asks for gems, a wallet rule
/// asks Steam for prices - at most eight uncached lookups and ten seconds for the whole offer.
/// </summary>
public sealed class TradeValues {
	public const int MaxLookups = 8;
	public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

	private readonly Dictionary<string, MarketQuote?> _quotes = new(StringComparer.Ordinal);
	private readonly Dictionary<uint, CardSet?> _sets = [];
	private readonly Dictionary<ulong, int?> _gems = [];
	private readonly TradeValueLookups _lookups;
	private int _lookupsUsed;
	private DateTime _until;

	private TradeValues(ITradeValueSource source, ulong partner, IReadOnlyList<TradeOffers.Item> giving, IReadOnlyList<TradeOffers.Item> receiving,
		TradeValueLookups lookups = TradeValueLookups.Missing) {
		Source = source;
		Partner = partner;
		Giving = giving;
		Receiving = receiving;
		_lookups = lookups;
		_until = DateTime.UtcNow + Budget;
	}

	public ITradeValueSource Source { get; }
	public ulong Partner { get; }
	public IReadOnlyList<TradeOffers.Item> Giving { get; }
	public IReadOnlyList<TradeOffers.Item> Receiving { get; }
	public int Currency => Source.Currency;
	public DateTime Now => Source.Now;

	/// <summary>A rule needed a price that was held but too old for it - worth asking the market once.</summary>
	public bool PriceStale { get; private set; }

	/// <summary>A rule needed a price that wasn't held (or the market didn't answer), and the look could ask.</summary>
	public bool PriceMissing { get; private set; }

	/// <summary>The look ran out of its eight uncached lookups (or its ten seconds) before answering.</summary>
	public bool OutOfLookups { get; private set; }

	/// <summary>Whether asking the market again could turn an Unknown into an answer.</summary>
	public bool RefreshWanted => PriceStale || PriceMissing;

	/// <summary>Values one offer from the real source.</summary>
	public static Task<TradeValues> ReadAsync(Bot bot, TradeOffers.Offer offer, IReadOnlyCollection<ulong> committed, CancellationToken ct,
		TradeValueLookups lookups = TradeValueLookups.Missing) =>
		Task.FromResult(new TradeValues(new BotTradeValues(bot, committed), offer.Partner, offer.Giving, offer.Receiving, lookups));

	/// <summary>For the tests: a source with explicit values and no network.</summary>
	public static TradeValues From(ITradeValueSource source, IReadOnlyList<TradeOffers.Item> giving, IReadOnlyList<TradeOffers.Item> receiving,
		ulong partner = 0, TradeValueLookups lookups = TradeValueLookups.Missing) =>
		new(source, partner, giving, receiving, lookups);

	private static int Copies(TradeOffers.Item item) => (int) Math.Min(int.MaxValue, Math.Max(1u, item.Amount));

	private bool Spend() {
		if (DateTime.UtcNow > _until) {
			OutOfLookups = true;

			return false;
		}

		if (++_lookupsUsed > MaxLookups) {
			OutOfLookups = true;

			return false;
		}

		return true;
	}

	/// <summary>The item's gem value, in gems (not wallet cents).</summary>
	public async Task<ValuedItem> GemsAsync(TradeOffers.Item item, CancellationToken ct) {
		SpareKind kind = Source.KindOf(item);

		if (kind == SpareKind.Gems) {
			return new ValuedItem(item, kind, Copies(item), null, null, false, null);
		}

		if (kind == SpareKind.GemSack) {
			return new ValuedItem(item, kind, Copies(item) * 1000, null, null, false, null);
		}

		if (Source.Gems(item) is int held) {
			return new ValuedItem(item, kind, held, null, null, false, null);
		}

		if (_lookups == TradeValueLookups.Cached) {
			PriceMissing = true;

			return new ValuedItem(item, kind, null, null, null, false, "its gem value isn't held, and this look doesn't ask Steam");
		}

		if (!Spend()) {
			return new ValuedItem(item, kind, null, null, null, false, "the offer is out of price lookups for this look");
		}

		int? value = await Source.GemsAsync(item, ct).ConfigureAwait(false);

		return value is int v
			? new ValuedItem(item, kind, v, null, null, false, null)
			: new ValuedItem(item, kind, null, null, null, false, "Steam didn't say what it's worth in gems");
	}

	/// <summary>The item's wallet value: what selling it pays after Steam's fee, in the wallet's currency.</summary>
	public async Task<ValuedItem> WalletAsync(TradeOffers.Item item, bool giving, int minSales, int maxAgeHours, double undercutPct, string looseGems,
		CancellationToken ct) {
		SpareKind kind = Source.KindOf(item);

		if (kind == SpareKind.Gems) {
			int? loose = Source.LooseGemWalletCents(Copies(item), looseGems);

			return loose is int l
				? new ValuedItem(item, kind, Copies(item), l, null, false, null)
				: new ValuedItem(item, kind, Copies(item), null, null, false, "loose gems aren't sold on the market, and there's no sack price to work out what they're worth");
		}

		if (kind == SpareKind.GemSack) {
			int? sack = Source.SackNetCents();

			return sack is int s
				? new ValuedItem(item, kind, Copies(item) * 1000, s * Copies(item), null, false, null)
				: new ValuedItem(item, kind, Copies(item) * 1000, null, null, false, "there's no price for a sack of gems right now");
		}

		if (!item.Marketable) {
			return new ValuedItem(item, kind, null, null, null, false, "it isn't sold on the Steam Community Market");
		}

		if (Currency <= 0) {
			return new ValuedItem(item, kind, null, null, null, false, "Steam hasn't said this account's wallet currency");
		}

		TimeSpan age = TimeSpan.FromHours(Math.Clamp(maxAgeHours, 1, 24 * 30));
		bool TooOld(MarketQuote? q) => q != null && (q.When > Now.AddHours(1) || (Now - q.When > age));
		MarketQuote? quote = Source.Quote(item.Hash) ?? _quotes.GetValueOrDefault(item.Hash);

		if (quote == null) {
			if (_lookups == TradeValueLookups.Cached) {
				PriceMissing = true;

				return new ValuedItem(item, kind, null, null, null, false, "no price is held for it, and this look doesn't ask the market");
			}

			if (!Spend()) {
				return new ValuedItem(item, kind, null, null, null, false, "the offer is out of price lookups for this look");
			}

			quote = await Source.QuoteAsync(item.Hash, ct).ConfigureAwait(false);
			_quotes[item.Hash] = quote;
		} else if ((_lookups == TradeValueLookups.Stale) && TooOld(quote)) {
			// The price held is too old for this rule: ask once more. A market that doesn't answer leaves the old one, and
			// the rule still says Unknown rather than deciding on a stale number.
			if (!Spend()) {
				PriceStale = true;

				return new ValuedItem(item, kind, null, null, null, false, "the offer is out of price lookups for this look");
			}

			quote = await Source.QuoteAsync(item.Hash, ct).ConfigureAwait(false) ?? quote;
			_quotes[item.Hash] = quote;
		}

		if (quote == null) {
			PriceMissing = true;

			return new ValuedItem(item, kind, null, null, null, false, "the market didn't answer for it");
		}

		if (TooOld(quote)) {
			PriceStale = true;

			return new ValuedItem(item, kind, null, null, null, false, "its price is older than the rule allows");
		}

		if ((quote.Lowest is not > 0) || (quote.Sales < Math.Max(1, minSales))) {
			PriceMissing = true;

			return new ValuedItem(item, kind, null, null, null, false, "there aren't enough fresh sales to go by");
		}

		// Conservative: the receiving side counts the lower of the two, the giving side the higher - never flatter the trade.
		List<int> buys = [];

		if (quote.Lowest is int lowest and > 0) {
			buys.Add(lowest);
		}

		if (quote.Median is int median and > 0) {
			buys.Add(median);
		}

		int price = giving ? buys.Max() : buys.Min();
		int? seller = ListingPrice.For(new MarketPrice(price, quote.When, quote.Median, quote.Sales), 3, (int) Math.Clamp(undercutPct, 0, 99)).Cents is int buyer
			? Seller.ReceiveFor(buyer)
			: null;

		return new ValuedItem(item, kind, null, seller is > 0 ? seller : null, null, false, seller is > 0 ? null : "its price can't be trusted (a dumped listing, or no recent sale)");
	}

	/// <summary>The item's game's normal card set, and its size.</summary>
	public async Task<ValuedItem> SetAsync(TradeOffers.Item item, CancellationToken ct) {
		if (Source.Set(item.Game) is { Cards.Count: > 0 } known) {
			return new ValuedItem(item, Source.KindOf(item), null, null, known.Cards.Count, true, null);
		}

		if (_lookups == TradeValueLookups.Cached) {
			PriceMissing = true;

			return new ValuedItem(item, Source.KindOf(item), null, null, null, false, "its set isn't known, and this look doesn't ask Steam");
		}

		if (!Spend()) {
			return new ValuedItem(item, Source.KindOf(item), null, null, null, false, "the offer is out of price lookups for this look");
		}

		CardSet? set = await Source.SetAsync(item.Game, ct).ConfigureAwait(false);
		_sets[item.Game] = set;

		return set is { Cards.Count: > 0 }
			? new ValuedItem(item, Source.KindOf(item), null, null, set.Cards.Count, true, null)
			: new ValuedItem(item, Source.KindOf(item), null, null, null, false, "its set isn't known whole on the market");
	}

	/// <summary>The game's set, for a booster rule that needs the cards behind it.</summary>
	public async Task<CardSet?> SetOfAsync(uint app, CancellationToken ct) {
		if (Source.Set(app) is { } known) {
			return known;
		}

		if (_sets.TryGetValue(app, out CardSet? cached)) {
			return cached;
		}

		if (_lookups == TradeValueLookups.Cached) {
			PriceMissing = true;

			return null;
		}

		if (!Spend()) {
			return null;
		}

		CardSet? set = await Source.SetAsync(app, ct).ConfigureAwait(false);
		_sets[app] = set;

		return set;
	}

	/// <summary>
	/// The market price held for this hash in this wallet's currency, or a fresh one (budgeted). <paramref name="maxAge"/>:
	/// when the held price is older than this, a <see cref="TradeValueLookups.Stale"/> look asks again; null never counts it stale.
	/// </summary>
	public async Task<MarketQuote?> QuoteAsync(string hash, CancellationToken ct, TimeSpan? maxAge = null) {
		MarketQuote? quote = Source.Quote(hash);

		if (quote == null) {
			_quotes.TryGetValue(hash, out quote);
		}

		bool TooOld(MarketQuote? q) => (q != null) && (maxAge is TimeSpan age) && (q.When > Now.AddHours(1) || (Now - q.When > age));

		if (quote == null) {
			if (_lookups == TradeValueLookups.Cached) {
				PriceMissing = true;

				return null;
			}

			if (!Spend()) {
				return null;
			}

			quote = await Source.QuoteAsync(hash, ct).ConfigureAwait(false);
			_quotes[hash] = quote;

			if (quote == null) {
				PriceMissing = true;
			}

			return quote;
		}

		if ((_lookups == TradeValueLookups.Stale) && TooOld(quote)) {
			if (!Spend()) {
				PriceStale = true;

				return quote;
			}

			MarketQuote? fresh = await Source.QuoteAsync(hash, ct).ConfigureAwait(false);

			if (fresh != null) {
				quote = fresh;
				_quotes[hash] = quote;
			}
		}

		if (TooOld(quote)) {
			PriceStale = true;
		}

		return quote;
	}

	/// <summary>What a side is worth in gems: all of it, or null when any item's value is unknown.</summary>
	public async Task<long?> GemsOfAsync(IReadOnlyList<TradeOffers.Item> side, CancellationToken ct) {
		long total = 0;

		foreach (TradeOffers.Item item in side) {
			ValuedItem v = await GemsAsync(item, ct).ConfigureAwait(false);

			if (v.Gems is not int gems) {
				return null;
			}

			total += (long) gems * Copies(item);
		}

		return total;
	}

	/// <summary>What a side pays on the market, all of it, or null when any item's value is unknown.</summary>
	public async Task<long?> WalletOfAsync(IReadOnlyList<TradeOffers.Item> side, bool giving, int minSales, int maxAgeHours, double undercutPct, string looseGems,
		CancellationToken ct) {
		long total = 0;

		foreach (TradeOffers.Item item in side) {
			ValuedItem v = await WalletAsync(item, giving, minSales, maxAgeHours, undercutPct, looseGems, ct).ConfigureAwait(false);

			if (v.WalletCents is not int cents) {
				return null;
			}

			total += (long) cents * Copies(item);
		}

		return total;
	}
}

/// <summary>The real values: the account's inventory's gem values, the market's prices, and the set finder's sets.</summary>
public sealed class BotTradeValues(Bot bot, IReadOnlyCollection<ulong> committed) : ITradeValueSource {
	private readonly HashSet<ulong> _committed = [.. committed];
	private readonly LiveSpareSteam _steam = new(bot);
	private readonly SteamCardMarket _market = new(bot);

	public int Currency => (int) bot.WalletCurrency;
	public DateTime Now => DateTime.UtcNow;

	/// <summary>Whether the item is spoken for by a live offer - what a gate must never give away.</summary>
	public bool IsCommitted(ulong asset) => _committed.Contains(asset);

	public SpareKind KindOf(TradeOffers.Item item) => item.Kind;
	public int? Gems(TradeOffers.Item item) => SteamGems.KnownValue(Spare(item));
	public Task<int?> GemsAsync(TradeOffers.Item item, CancellationToken ct) => _steam.GemValueAsync(Spare(item), ct);

	public MarketQuote? Quote(string hash) => MarketQuotes.Of(hash, Currency);

	public async Task<MarketQuote?> QuoteAsync(string hash, CancellationToken ct) {
		PriceBook.Background.Value = true;

		try {
			return await _market.QuoteAsync(hash, Currency, ct).ConfigureAwait(false);
		} finally {
			PriceBook.Background.Value = false;
		}
	}

	public CardSet? Set(uint app) =>
		SetFinder.Shared.Known(DateTime.UtcNow).FirstOrDefault(k => k.App == app)?.ToCardSet();

	public async Task<CardSet?> SetAsync(uint app, CancellationToken ct) {
		PriceBook.Background.Value = true;

		try {
			return await _market.SetAsync(app, ct).ConfigureAwait(false);
		} finally {
			PriceBook.Background.Value = false;
		}
	}

	public double? Rate(int currency) => WalletRate.ForDeciding(currency);

	public int? SackNetCents() => SparePrices.SackPays(Currency, 0)?.Cents;

	public int? LooseGemWalletCents(int gems, string looseGemsMode) =>
		(looseGemsMode != "unknown") && (SackNetCents() is int sack) ? (int) ((long) gems * sack / 1000) : null;

	private static SpareItem Spare(TradeOffers.Item item) =>
		new(item.AssetId, item.Game, item.Kind, item.Hash, item.Name, item.Tradable, item.Marketable, item.Amount, item.GooType, item.GooBorder);
}
