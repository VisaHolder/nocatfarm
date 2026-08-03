using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm;

/// <summary>
/// What Steam's community market says things sell for, cached to disk and shared by every account.
///
/// One price book for the whole program on purpose: three accounts holding the same cases and the same cards
/// would otherwise ask the market the same question three times, and the market's rate limit is the tightest
/// thing Steam has - roughly twenty questions a minute before it starts answering with nothing at all. So
/// lookups are spaced out, capped per sweep, and a price is kept for a day before it's asked again.
///
/// Nothing here is ever load-bearing: an unknown price simply counts as zero and is looked up later, which is why
/// a fresh install shows a value that climbs for a few minutes and then settles.
/// </summary>
public static partial class PriceBook {
	/// <summary>Seconds between two market lookups. The market starts refusing at roughly one every three.</summary>
	private const double GapSeconds = 3.5;

	/// <summary>How long a price is trusted before it's worth asking again.</summary>
	private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

	private sealed class Price {
		public decimal Usd { get; set; }
		public long At { get; set; }   // unix seconds

		public DateTime When => DateTimeOffset.FromUnixTimeSeconds(At).UtcDateTime;
	}

	private static readonly Dictionary<string, Price> Cache = new(StringComparer.Ordinal);
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>
	/// Its own client, signed in as nobody.
	///
	/// Prices are public - the market answers this question to anyone - and asking it through an account's
	/// session gets that SESSION rate-limited, which is both stricter and far more annoying: after a few hundred
	/// lookups Steam simply stopped answering the accounts, and every sweep after that gave up on its first item
	/// while the same URL fetched fine from anywhere else. Nothing here needs to know who is asking.
	/// </summary>
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	private static DateTime _lastCall = DateTime.MinValue;
	private static DateTime _coolUntil = DateTime.MinValue;
	private static DateTime _lastSave = DateTime.MinValue;
	private static bool _loaded;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "prices.json");

	/// <summary>Steam's currency id for everything here. Changing it makes every cached price a different key.</summary>
	private static int Currency => Math.Max(1, Live.Global.MarketCurrency);

	/// <summary>The symbol to print. Steam uses "$" for several of these, which is exactly what people expect.</summary>
	public static string Symbol => Currency switch {
		2 => "£",
		3 => "€",
		8 or 23 => "¥",
		5 => "₽",
		24 => "₹",
		_ => "$"
	};

	/// <summary>
	/// Market prices are per (game, item name, currency) - the same name in two games is two different things,
	/// and the same item in two currencies is two different numbers. Currency is part of the key so switching it
	/// re-prices from scratch instead of quietly mixing dollars into a euro total.
	/// </summary>
	private static string Key(uint app, string marketHashName) => $"{app}/{Currency}/{marketHashName}";

	/// <summary>A price we already hold, or null. Never touches the network.</summary>
	public static decimal? Known(uint app, string marketHashName) {
		Load();

		lock (Cache) {
			return Cache.TryGetValue(Key(app, marketHashName), out Price? p) ? p.Usd : null;
		}
	}

	/// <summary>True when the cached price is old enough to be worth asking about again.</summary>
	public static bool NeedsRefresh(uint app, string marketHashName) {
		Load();

		lock (Cache) {
			return !Cache.TryGetValue(Key(app, marketHashName), out Price? p) || (DateTime.UtcNow - p.When > MaxAge);
		}
	}

	/// <summary>
	/// Ask the market what one item sells for, and remember the answer. Returns null if it wouldn't say - which
	/// includes items that simply have no market listing, so those are remembered as zero rather than asked
	/// about for ever.
	/// </summary>
	public static async Task<decimal?> FetchAsync(uint app, string marketHashName, CancellationToken ct) {
		if (DateTime.UtcNow < _coolUntil) {
			return null;   // the market told us to slow down; everyone waits it out together
		}

		await Gate.WaitAsync(ct).ConfigureAwait(false);

		try {
			TimeSpan since = DateTime.UtcNow - _lastCall;

			if (since < TimeSpan.FromSeconds(GapSeconds)) {
				await Task.Delay(TimeSpan.FromSeconds(GapSeconds) - since, ct).ConfigureAwait(false);
			}

			_lastCall = DateTime.UtcNow;

			string url = $"https://steamcommunity.com/market/priceoverview/?appid={app}&currency={Currency}&market_hash_name={Uri.EscapeDataString(marketHashName)}";
			using HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				// 429 is the market saying "enough". Back off for a good while rather than retrying into the wall.
				_coolUntil = DateTime.UtcNow.AddMinutes(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 15 : 2);
				Log.Debug($"the market answered {(int) response.StatusCode} - pausing price lookups until {_coolUntil.ToLocalTime():HH:mm}");

				return null;
			}

			string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(json)) {
				return null;   // ask again next sweep, don't poison the cache
			}

			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)) {
				Remember(app, marketHashName, 0);   // no market listing at all - genuinely worth nothing

				return 0;
			}

			// median_price is the more honest number of the two: lowest_price is one optimistic listing, the
			// median is what things have actually been going for.
			decimal price = Money(Text(doc.RootElement, "median_price")) ?? Money(Text(doc.RootElement, "lowest_price")) ?? 0;
			Remember(app, marketHashName, price);

			return price;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Log.Debug($"market lookup for {marketHashName} failed: {e.Message}");

			return null;
		} finally {
			Gate.Release();
		}
	}

	private static string? Text(JsonElement node, string name) =>
		node.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() : null;

	/// <summary>"$1,234.56" / "1.234,56 EUR" -> 1234.56. The symbol is noise; only the digits matter.</summary>
	private static decimal? Money(string? text) {
		if (string.IsNullOrWhiteSpace(text)) {
			return null;
		}

		string digits = MoneyChars().Replace(text, "");

		// Whichever separator comes LAST is the decimal point - that is the only rule that works for both
		// "1,234.56" and "1.234,56".
		int comma = digits.LastIndexOf(',');
		int dot = digits.LastIndexOf('.');

		if ((comma >= 0) && (comma > dot)) {
			digits = digits.Replace(".", "").Replace(',', '.');
		} else {
			digits = digits.Replace(",", "");
		}

		return decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value) ? value : null;
	}

	private static void Remember(uint app, string marketHashName, decimal usd) {
		lock (Cache) {
			Cache[Key(app, marketHashName)] = new Price { Usd = usd, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
		}

		if (DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(30)) {
			_lastSave = DateTime.UtcNow;
			Save();
		}
	}

	private static void Load() {
		if (_loaded) {
			return;
		}

		_loaded = true;

		try {
			if (!File.Exists(Path)) {
				return;
			}

			Dictionary<string, Price>? saved = JsonSerializer.Deserialize<Dictionary<string, Price>>(File.ReadAllText(Path));

			if (saved != null) {
				lock (Cache) {
					foreach ((string key, Price p) in saved) {
						// Anything a month old is either an item nobody holds any more or a currency nobody uses
						// any more - keeping either for ever is how a cache file quietly becomes a megabyte.
						if (DateTime.UtcNow - p.When < TimeSpan.FromDays(30)) {
							Cache[key] = p;
						}
					}
				}
			}
		} catch (Exception e) {
			Log.Debug($"couldn't read the price book: {e.Message}");
		}
	}

	/// <summary>Write the book out. Called on a timer and once on the way out.</summary>
	public static void Save() {
		try {
			Dictionary<string, Price> snapshot;

			lock (Cache) {
				if (Cache.Count == 0) {
					return;
				}

				snapshot = new Dictionary<string, Price>(Cache, StringComparer.Ordinal);
			}

			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
			AtomicFile.Write(Path, JsonSerializer.Serialize(snapshot));
		} catch (Exception e) {
			Log.Debug($"couldn't save the price book: {e.Message}");
		}
	}

	[GeneratedRegex(@"[^\d.,]")]
	private static partial Regex MoneyChars();
}
