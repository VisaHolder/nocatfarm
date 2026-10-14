using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// How many of a wallet's currency make a US dollar on the Steam market - so prices from the market's search pages, which
/// are always in US dollars, can decide things for an account whose wallet is in Canadian dollars, euros or anything else.
/// </summary>
/// <remarks>
/// A search page prices a whole game's cards in one request; asking priceoverview in the wallet's own currency is one
/// request per item. An inventory of several hundred items priced that way, one by one in Canadian dollars, ran into the
/// market's limit overnight (2026-10-12) and did nothing at all. So: the dollar prices, turned into the wallet's currency
/// at this rate, decide what to do; only an item about to be LISTED is asked about in its real currency, right before it
/// goes up (Seller.RepriceAsync) - a listing price never comes from a converted number.
///
/// The rate is learned from items priced both ways - an item's wallet-currency price over its dollar price, the median of
/// a few - kept for a day, and saved across restarts. Steam's own prices per currency don't follow the bank rate exactly,
/// which is why it's learned from the market rather than looked up. Until it's learned, a fixed table stands in - only for
/// the inventory's shown value, never for deciding anything (<see cref="ForDeciding"/>).
/// </remarks>
public static class WalletRate {
	/// <summary>Steam's id for the US dollar.</summary>
	public const int UsDollar = 1;

	/// <summary>Items priced both ways it takes before the rate is used.</summary>
	public const int SamplesNeeded = 3;

	/// <summary>How long a learned rate - and each item it was learned from - is trusted.</summary>
	public static readonly TimeSpan Life = TimeSpan.FromHours(24);

	/// <summary>One item priced both ways: its market name, cents in dollars and in the wallet's currency, when.</summary>
	public sealed record Sample(string Hash, int Usd, int Wallet, long At) {
		public DateTime When => new(At, DateTimeKind.Utc);
	}

	private static Dictionary<int, List<Sample>>? _samples;
	private static readonly Lock Gate = new();

	private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "state", "wallet-rates.json");

	private static Dictionary<int, List<Sample>> Samples() {
		if (_samples != null) {
			return _samples;
		}

		_samples = [];

		try {
			if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<int, List<Sample>>>(File.ReadAllText(FilePath)) is { } saved) {
				foreach ((int currency, List<Sample> list) in saved) {
					_samples[currency] = [.. list.Where(static s => DateTime.UtcNow - s.When < Life)];
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Debug(new Said("couldn't read the saved currency rates: {0}", Log.Describe(e)));
		}

		return _samples;
	}

	private static void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			AtomicFile.Write(FilePath, JsonSerializer.Serialize(Samples()));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("couldn't save the currency rates: {0}", Log.Describe(e)));
		}
	}

	/// <summary>Only the checks: forget everything, and read the file again next time.</summary>
	public static void Reset() {
		lock (Gate) {
			_samples = null;
		}
	}

	/// <summary>
	/// One item priced both ways: kept, the newest per item, the last twenty per currency. Nothing for US dollars, nor an
	/// item under 5 cents - a cent either way is too much of so small a price to learn a rate from.
	/// </summary>
	public static void Observe(int currency, string hash, int usdCents, int walletCents, DateTime now) {
		if ((currency <= 0) || (currency == UsDollar) || (usdCents < 5) || (walletCents <= 0) || (hash.Length == 0)) {
			return;
		}

		lock (Gate) {
			Dictionary<int, List<Sample>> all = Samples();

			if (!all.TryGetValue(currency, out List<Sample>? list)) {
				all[currency] = list = [];
			}

			list.RemoveAll(s => (s.Hash == hash) || (now - s.When >= Life));
			list.Add(new Sample(hash, usdCents, walletCents, now.Ticks));

			if (list.Count > 20) {
				list.RemoveRange(0, list.Count - 20);
			}

			Save();
		}
	}

	/// <summary>
	/// The rate learned from the market for this currency - the median of the items priced both ways in the last day - or
	/// null with fewer than <see cref="SamplesNeeded"/>. 1 for US dollars.
	/// </summary>
	public static double? Learned(int currency, DateTime now) {
		if (currency == UsDollar) {
			return 1;
		}

		lock (Gate) {
			if (!Samples().TryGetValue(currency, out List<Sample>? list)) {
				return null;
			}

			List<double> rates = [.. list.Where(s => now - s.When < Life).Select(static s => (double) s.Wallet / s.Usd).Order()];

			if (rates.Count < SamplesNeeded) {
				return null;
			}

			return rates.Count % 2 == 1 ? rates[rates.Count / 2] : (rates[(rates.Count / 2) - 1] + rates[rates.Count / 2]) / 2;
		}
	}

	/// <summary>Whether a few items should be priced both ways now - a currency other than dollars with no rate learned in the last day.</summary>
	public static bool NeedsSamples(int currency, DateTime now) => (currency > 0) && (Learned(currency, now) == null);

	/// <summary>The rate for deciding anything (gems, selling, the price limit): learned from the market only - null until it is.</summary>
	public static double? ForDeciding(int currency) => currency > 0 ? Learned(currency, DateTime.UtcNow) : null;

	/// <summary>The rate for showing a value: learned, else the fixed table, else null (a currency it doesn't know).</summary>
	public static double? ForShowing(int currency) => ForDeciding(currency) ?? Table(currency);

	/// <summary>Dollar cents in the wallet's currency at <paramref name="rate"/>, rounded to the cent.</summary>
	public static int ToWallet(int usdCents, double rate) => (int) Math.Round(usdCents * rate, MidpointRounding.AwayFromZero);

	/// <summary>
	/// Roughly what Steam's market charged per US dollar in late 2026, per Steam currency id - a stand-in for an inventory's
	/// shown value until the real rate is learned. Never used to decide anything. Null for a currency not listed.
	/// </summary>
	public static double? Table(int currency) => currency switch {
		1 => 1,
		2 => 0.75,      // GBP
		3 => 0.86,      // EUR
		4 => 0.80,      // CHF
		5 => 80,        // RUB
		6 => 3.65,      // PLN
		7 => 5.4,       // BRL
		8 => 150,       // JPY
		9 => 10.2,      // NOK
		10 => 16500,    // IDR
		11 => 4.2,      // MYR
		12 => 58,       // PHP
		13 => 1.3,      // SGD
		14 => 33,       // THB
		15 => 26000,    // VND
		16 => 1400,     // KRW
		17 => 41,       // TRY
		18 => 41,       // UAH
		19 => 18.5,     // MXN
		20 => 1.38,     // CAD
		21 => 1.52,     // AUD
		22 => 1.7,      // NZD
		23 => 7.1,      // CNY
		24 => 88,       // INR
		25 => 950,      // CLP
		26 => 3.5,      // PEN
		27 => 4000,     // COP
		28 => 17.5,     // ZAR
		29 => 7.8,      // HKD
		30 => 31,       // TWD
		31 => 3.75,     // SAR
		32 => 3.67,     // AED
		35 => 3.4,      // ILS
		37 => 520,      // KZT
		38 => 0.31,     // KWD
		39 => 3.64,     // QAR
		40 => 505,      // CRC
		41 => 40,       // UYU
		_ => null
	};
}
