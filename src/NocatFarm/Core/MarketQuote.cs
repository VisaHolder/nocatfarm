using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// One item's market price in the account's OWN wallet currency: the cheapest listing, the median of the last day's
/// sales and how many sold - Steam's priceoverview, the one signed-out market answer that honours the currency asked for.
/// </summary>
/// <remarks>
/// The market's search pages don't. Asked for Canadian dollars (currency=20) or euros (3), a search still answers in US
/// dollars: "The shine through darkness" came back as "$0.39" both ways, while priceoverview said "CDN$ 0.57" and
/// "0,36€" for the same card at the same minute, and "$0.39" for US dollars (live, 2026-10-12). Taken as the wallet's
/// own money, a search price listed a Canadian account's cards about a quarter under what they sell for. So a search is
/// only ever a shortlist in US dollars, and every price that decides anything for an account comes from here.
/// Lowest and Median are null when there's none: nobody selling, nothing sold in the last day.
/// </remarks>
public sealed record MarketQuote(string Hash, int Currency, int? Lowest, int? Median, int Sales, long At) {
	public DateTime When => new(At, DateTimeKind.Utc);

	/// <summary>As the spare items rules read a price: the cheapest listing (0 when nobody sells), when, the median and sales.</summary>
	public MarketPrice Price => new(Lowest ?? 0, When, Median, Sales);
}

/// <summary>Prices in the wallet's own currency, one item at a time, kept a while - see <see cref="MarketQuote"/>.</summary>
public static class MarketQuotes {
	private static Dictionary<string, MarketQuote>? _quotes;
	private static readonly Lock Gate = new();

	/// <summary>Quotes older than this are dropped from the file: a fortnight-old price decides nothing.</summary>
	private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

	private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "state", "marketquotes.json");

	private static string Key(string hash, int currency) => $"{currency}|{hash}";

	private static Dictionary<string, MarketQuote> Quotes() {
		if (_quotes != null) {
			return _quotes;
		}

		_quotes = new Dictionary<string, MarketQuote>(StringComparer.Ordinal);

		try {
			if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<MarketQuote>>(File.ReadAllText(FilePath)) is { } list) {
				foreach (MarketQuote q in list.Where(static q => DateTime.UtcNow - q.When < KeepFor)) {
					_quotes[Key(q.Hash, q.Currency)] = q;
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Failed("couldn't read the saved market prices", e);
		}

		return _quotes;
	}

	private static void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			AtomicFile.Write(FilePath, JsonSerializer.Serialize(Quotes().Values.Where(static q => DateTime.UtcNow - q.When < KeepFor).ToList()));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("couldn't save the market prices", e);
		}
	}

	/// <summary>Only the checks: forget everything.</summary>
	public static void Reset() {
		lock (Gate) {
			_quotes = new Dictionary<string, MarketQuote>(StringComparer.Ordinal);
		}
	}

	/// <summary>The quote held for an item in this currency, however old - the caller decides what's too old.</summary>
	public static MarketQuote? Of(string hash, int currency) {
		lock (Gate) {
			return Quotes().GetValueOrDefault(Key(hash, currency));
		}
	}

	internal static void Keep(MarketQuote quote) {
		lock (Gate) {
			Quotes()[Key(quote.Hash, quote.Currency)] = quote;
			Save();
		}
	}

	/// <summary>
	/// Ask the market for one item's price in <paramref name="currency"/> now, and keep it. Null when the market didn't
	/// answer (or is pausing) or the answer couldn't be read - never a guess.
	/// </summary>
	public static async Task<MarketQuote?> FetchAsync(string hash, int currency, CancellationToken ct) {
		if ((currency <= 0) || (hash.Length == 0)) {
			return null;
		}

		(string? json, _) = await SparePrices.Get($"/market/priceoverview/?appid=753&currency={currency}&market_hash_name={Uri.EscapeDataString(hash)}", ct).ConfigureAwait(false);

		if (Parse(json, hash, currency, DateTime.UtcNow) is not { } quote) {
			return null;
		}

		Keep(quote);

		// Priced both ways - the dollar shortlist and now the wallet's own currency: the rate is learned from it.
		if ((quote.Lowest is int wallet) && (SparePrices.UsdCents(hash) is int usd)) {
			WalletRate.Observe(currency, hash, usd, wallet, DateTime.UtcNow);
		}

		return quote;
	}

	/// <summary>
	/// A priceoverview answer: {"success":true,"lowest_price":"CDN$ 0.57","volume":"16","median_price":"CDN$ 0.58"}. Each
	/// price is text in the currency asked for. "success":false is Steam's "nothing on the market for it" - a quote with no
	/// prices. Null when it isn't an answer at all, or a price in it can't be read: that's no price, not a cheap one.
	/// </summary>
	public static MarketQuote? Parse(string? json, string hash, int currency, DateTime at) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return null;
			}

			if (!root.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)) {
				return new MarketQuote(hash, currency, null, null, 0, at.Ticks);
			}

			int? lowest = null, median = null;

			if (Text(root, "lowest_price") is { } l) {
				if (Cents(l, currency) is not int c) {
					return null;
				}

				lowest = c;
			}

			if (Text(root, "median_price") is { } m) {
				if (Cents(m, currency) is not int c) {
					return null;
				}

				median = c;
			}

			// "4,470" - a count, its separators only for people.
			string volume = new([.. (Text(root, "volume") ?? "").Where(char.IsAsciiDigit)]);
			int sales = int.TryParse(volume, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : 0;

			return new MarketQuote(hash, currency, lowest is > 0 ? lowest : null, median is > 0 ? median : null, sales, at.Ticks);
		} catch (JsonException) {
			return null;
		}
	}

	private static string? Text(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement e) && (e.ValueKind == JsonValueKind.String) && (e.GetString() is { Length: > 0 } s) ? s : null;

	/// <summary>Steam's currency id for the Kuwaiti dinar: the one wallet currency written with three decimals.</summary>
	private const int Kwd = 38;

	/// <summary>
	/// A price as the market writes it in a currency - "CDN$ 0.57", "0,57€", "R$ 1,23", "¥ 12", "1,--€" - in the hundredths
	/// Steam counts every wallet in (so ¥ 12 is 1200). Null when it can't be read for certain: a dinar with three decimals,
	/// say, would otherwise read a thousand times too high.
	/// </summary>
	public static int? Cents(string? text, int currency) {
		if (string.IsNullOrWhiteSpace(text)) {
			return null;
		}

		if (currency == Kwd) {
			string kept = new([.. text.Where(static c => char.IsAsciiDigit(c) || c is '.' or ',')]);
			int sep = kept.Trim('.', ',').LastIndexOfAny(['.', ',']);

			if ((sep >= 0) && (kept.Trim('.', ',').Length - sep - 1 == 3)) {
				return null;
			}
		}

		return SparePrices.MoneyCents(text);
	}

	/// <summary>A currency's three-letter code for people ("CAD"), or "" when it isn't known.</summary>
	public static string Code(int currency) =>
		(currency > 0) && Enum.IsDefined(typeof(SteamKit2.ECurrencyCode), currency) && ((SteamKit2.ECurrencyCode) currency != SteamKit2.ECurrencyCode.Invalid)
			? ((SteamKit2.ECurrencyCode) currency).ToString()
			: "";
}

/// <summary>
/// What an item goes on the market for - one rule for everything that lists (spare items, unopened packs, sacks of gems,
/// 'sell'), from a price in the wallet's own currency.
/// </summary>
public static class ListingPrice {
	/// <summary>The market's floor: 3 cents to the buyer, of which the seller gets 1.</summary>
	private const int Floor = 3;

	/// <summary>
	/// The buyer's price for one item, or null with the reason it's kept instead. <paramref name="strategy"/>: 0 a cent
	/// under the real price (the default), 1 the same as the cheapest listing, 2 the median sale price, 3 a cent under the
	/// cheapest (see <see cref="UnderCheapest"/>) - anything else is read as 0 (see <see cref="Choice"/>). <paramref name="dumpPct"/>: under the default, a cheapest listing this many percent or more under the
	/// median with only one sale behind it is a contradiction, and the item is kept (0 = list it anyway). It never moves the
	/// price itself: the price is the higher of the two, so a listing under the median never sets it.
	/// </summary>
	/// <remarks>
	/// The real price is what it sold for over the last day (the median) - not one listing dumped far under it. Listed a
	/// cent under the higher of that and the cheapest listing that isn't a dump, it's never under what it really sells for.
	/// No sale lately, nobody selling, or prices that contradict each other: kept, never a guess.
	///
	/// Never under the highest buy order either, and that needs no extra request: a buy order always sits under the
	/// cheapest listing (the two would have met), and every choice here lists at a cent under the cheapest listing or
	/// higher. The order book itself is only on the item's page, in the currency of the country asking, not the wallet's.
	/// </remarks>
	/// <param name="minReceive">Only for "a cent under the cheapest": "Only sell if you get at least" - see <see cref="UnderCheapest"/>.</param>
	/// <param name="floorPct">Only for "a cent under the cheapest": "Never go lower than", in percent of the sale price; 0 none.</param>
	/// <param name="ours">Only for "a cent under the cheapest": what a buyer pays for our own cheapest listing of the item still
	/// up (<see cref="OurListings.UpFor"/>), 0 none.</param>
	public static (int? Cents, Said Why) For(MarketPrice price, int strategy, int dumpPct, int minReceive = 0, int floorPct = 0, int ours = 0) {
		int lowest = price.Cents;
		int median = price.Median ?? 0;

		if (lowest <= 0) {
			return (null, new Said("nobody is selling it right now - kept, there's no price to go by"));
		}

		switch (Choice(strategy)) {
			case 1:
				return (Math.Max(Floor, lowest), default);
			case 2:
				// The median - but never under a cent below the cheapest listing: that would be giving it away.
				return median > 0 ? (Math.Max(Math.Max(Floor, median), Seller.UndercutFor(lowest)), default)
					: (null, new Said("no sale in the last day, so no median - kept"));
			case 3:
				return (UnderCheapest(lowest, median, dumpPct, minReceive, floorPct, ours), default);
		}

		if (median <= 0) {
			return (null, new Said("no sale in the last day, so no real price - kept"));
		}

		bool dump = (dumpPct > 0) && (lowest * 100L < median * (100L - Math.Clamp(dumpPct, 0, 99)));

		// A dumped listing with one sale to set against it, or sales nobody counted: which one is the price? Kept.
		if (price.Sales <= 0 || (dump && (price.Sales < 2))) {
			return (null, new Said("its prices contradict each other (cheapest {0}, recent sales {1}) - kept", Spare.Money(lowest), Spare.Money(median)));
		}

		int real = Math.Max(median, dump ? 0 : lowest);

		return (Math.Max(Floor, real - 1), default);
	}

	/// <summary>
	/// The selling price choice as it may be: 0, 1, 2 or 3 - anything that isn't one of them is the safe default.
	/// </summary>
	public static int Choice(int saved) => saved is >= 0 and <= 3 ? saved : 0;

	/// <summary>
	/// "A cent under the cheapest" (1.8.2): a cent under the cheapest listing, so ours is the cheapest and sells first -
	/// cheapest 0.12, listed at 0.11. The 1.8.0 one of that name followed a dumped listing down; this one doesn't:
	/// <list type="bullet">
	/// <item>A cheapest listing <paramref name="dumpPct"/> percent or more under what it sold for over the last day is a dump,
	/// and ignored: it goes a cent under the sale price instead. priceoverview only says the cheapest listing, never the
	/// second-cheapest, so the sale price is the next real price there is.</item>
	/// <item>Our own listing already the cheapest (<paramref name="ours"/> is the cheapest): listed at the same price, never
	/// under ourselves.</item>
	/// <item>Never under <paramref name="floorPct"/> percent of the sale price when there is one, nor the market's 3 cents
	/// (you get 0.01).</item>
	/// <item>A cent under that would pay under <paramref name="minReceive"/>: the least that pays it, while that's no more
	/// than the price it undercuts. When even that pays under it, the price stays and the minimum decides (gems or kept) -
	/// never a listing above the cheapest only to make the minimum.</item>
	/// </list>
	/// </summary>
	internal static int UnderCheapest(int lowest, int median, int dumpPct, int minReceive, int floorPct, int ours) {
		bool mine = (ours > 0) && (ours == lowest);
		bool dump = !mine && (median > 0) && (dumpPct > 0) && (lowest * 100L < median * (100L - Math.Clamp(dumpPct, 0, 99)));
		int beat = dump ? median : lowest;
		int price = Math.Max(Floor, mine ? lowest : beat - 1);

		if ((median > 0) && (floorPct > 0)) {
			price = Math.Max(price, (int) Math.Ceiling(median * Math.Clamp(floorPct, 1, 100) / 100m));
		}

		int least = minReceive > 0 ? minReceive + Seller.FeesFor(minReceive) : 0;

		return least <= beat ? Math.Max(price, least) : price;
	}

	/// <summary>What the item is worth, for the price limit and the day's value cap: the higher of the cheapest listing and the median.</summary>
	public static int Worth(MarketPrice price) => Math.Max(price.Cents, price.Median ?? 0);
}
