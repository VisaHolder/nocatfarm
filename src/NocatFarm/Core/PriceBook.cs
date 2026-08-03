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
	private static DateTime _lastCall = DateTime.MinValue;
	private static DateTime _lastSave = DateTime.MinValue;
	private static bool _loaded;

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "prices.json");

	/// <summary>Market prices are per (game, item name) - the same name in two games is two different things.</summary>
	private static string Key(uint app, string marketHashName) => $"{app}/{marketHashName}";

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
	public static async Task<decimal?> FetchAsync(WebSession web, uint app, string marketHashName, CancellationToken ct) {
		await Gate.WaitAsync(ct).ConfigureAwait(false);

		try {
			TimeSpan since = DateTime.UtcNow - _lastCall;

			if (since < TimeSpan.FromSeconds(GapSeconds)) {
				await Task.Delay(TimeSpan.FromSeconds(GapSeconds) - since, ct).ConfigureAwait(false);
			}

			_lastCall = DateTime.UtcNow;

			string url = $"/market/priceoverview/?appid={app}&currency=1&market_hash_name={Uri.EscapeDataString(marketHashName)}";
			string? json = await web.GetAsync(new Uri(WebSession.Community, url), ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(json)) {
				return null;   // rate limited or offline: ask again next sweep, don't poison the cache
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

	/// <summary>"$1,234.56" / "1.234,56€" -> 1234.56. Currency is forced to USD, so the symbol is noise.</summary>
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
						Cache[key] = p;
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
