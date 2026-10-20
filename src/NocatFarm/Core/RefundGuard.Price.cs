using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2 "Protect refunds only for games over" (<see cref="BotConfig.RefundProtectMinPrice"/>): a refundable game is held
/// only when it cost more than that. What it cost is what was PAID, from the account's purchase history when Steam shows
/// it there; otherwise the store's price for the account's country - the higher of today's and the regular price, so a
/// sale never takes a game out of protection. Not known either way: protected, as before.
/// </summary>
/// <remarks>
/// Gentle on Steam: the purchase history is read at most once a day, and only while a refundable game's price paid isn't
/// known; the store is asked through the same queue as the game catalogue (its gap and its 429 pause), a few games a
/// check, each answer kept for 3 days. Kept in config/state/refundprices-&lt;account&gt;.json.
/// </remarks>
public sealed partial class RefundGuard {
	/// <summary>Store prices are asked again after this long.</summary>
	public static readonly TimeSpan StoreFor = TimeSpan.FromDays(3);

	/// <summary>The purchase history is read at most this often.</summary>
	public static readonly TimeSpan HistoryEvery = TimeSpan.FromDays(1);

	/// <summary>Store prices asked for in one check, at most.</summary>
	private const int StoreAsksACheck = 5;

	// ── test seams: the purchase history page and the store's answer, instead of asking Steam (null: Steam itself) ──
	internal Func<CancellationToken, Task<string?>>? ReadHistory { get; set; }
	internal Func<uint, string, CancellationToken, Task<string?>>? ReadStore { get; set; }

	/// <summary>The threshold in cents of the wallet's currency - 0: every refundable game is protected.</summary>
	public static long ThresholdCents(BotConfig cfg) => cfg.RefundProtectMinPrice > 0 ? (long) Math.Round((decimal) cfg.RefundProtectMinPrice * 100m) : 0;

	/// <summary>A store price: the higher of today's and the regular price, its currency, and when it was read (UTC ticks).</summary>
	public sealed record StorePrice(long Cents, string Currency, long At);

	/// <summary>What's kept on disk.</summary>
	public sealed class Prices {
		/// <summary>When the purchase history was last read (UTC ticks).</summary>
		public long HistoryAt { get; set; }

		/// <summary>The price paid per game, from the purchase history, in the wallet's currency (cents).</summary>
		public Dictionary<uint, long> Paid { get; set; } = [];

		/// <summary>The store's price per game, for the account's country.</summary>
		public Dictionary<uint, StorePrice> Store { get; set; } = [];
	}

	private Prices? _prices;
	private readonly Lock _priceGate = new();
	private HashSet<uint> _cheap = [];

	private string PricesPath => Path.Combine(ConfigStore.ConfigDir, "state", $"refundprices-{bot.Name}.json");

	private Prices LoadPrices() {
		lock (_priceGate) {
			if (_prices != null) {
				return _prices;
			}

			try {
				_prices = File.Exists(PricesPath) ? JsonSerializer.Deserialize<Prices>(File.ReadAllText(PricesPath)) : null;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				Log.Failed("couldn't read the refund prices", e, bot.Name);
			}

			_prices ??= new Prices();
			_prices.Paid ??= [];
			_prices.Store ??= [];

			return _prices;
		}
	}

	private void SavePrices() {
		try {
			string json;

			lock (_priceGate) {
				json = JsonSerializer.Serialize(_prices ?? new Prices());
			}

			Directory.CreateDirectory(Path.GetDirectoryName(PricesPath)!);
			AtomicFile.Write(PricesPath, json);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("couldn't save the refund prices", e, bot.Name);
		}
	}

	/// <summary>
	/// A refundable game the threshold lets go: it cost the threshold or less (paid, or the store's price). False when no
	/// threshold is set, or its price isn't known yet - then it's protected. Sync - what the last check decided.
	/// </summary>
	public bool Cheap(uint app) => (ThresholdCents(bot.Cfg) > 0) && _cheap.Contains(app);

	/// <summary>
	/// What a game cost, as far as is known now: what was paid (the purchase history), else the store's price when it's at
	/// most <see cref="StoreFor"/> old and in the wallet's currency. Null when not known.
	/// </summary>
	public long? KnownPrice(uint app, DateTime nowUtc) {
		Prices p = LoadPrices();

		lock (_priceGate) {
			if (p.Paid.TryGetValue(app, out long paid)) {
				return paid;
			}

			if (p.Store.TryGetValue(app, out StorePrice? store) && (nowUtc.Ticks - store.At < StoreFor.Ticks) && SameCurrency(store.Currency)) {
				return store.Cents;
			}

			return null;
		}
	}

	/// <summary>The store's currency is the wallet's (a price in another money says nothing against the threshold).</summary>
	private bool SameCurrency(string currency) =>
		(bot.WalletCurrency != SteamKit2.ECurrencyCode.Invalid) && currency.Equals(bot.WalletCurrency.ToString(), StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Of these refundable games (with their library names, and whether they're only shared with the account), the ones the
	/// threshold lets go: what's missing is asked first - the purchase history at most once a day while a bought game's
	/// price paid isn't known, then the store for a few games. Empty when no threshold is set.
	/// </summary>
	internal async Task<HashSet<uint>> CheapOfAsync(IReadOnlyCollection<(uint App, string Name, bool Shared)> games, DateTime nowUtc, CancellationToken ct) {
		long threshold = ThresholdCents(bot.Cfg);

		if ((threshold <= 0) || (games.Count == 0)) {
			return [];
		}

		Prices p = LoadPrices();
		bool changed = false;

		// The price paid, from the purchase history: only for the account's own purchases, at most once a day.
		bool historyDue;

		lock (_priceGate) {
			historyDue = games.Any(g => !g.Shared && !p.Paid.ContainsKey(g.App)) && (nowUtc.Ticks - p.HistoryAt >= HistoryEvery.Ticks);
		}

		if (historyDue) {
			string? html = await (ReadHistory?.Invoke(ct) ?? bot.Web.GetAsync(new Uri(WebSession.Store, "/account/history/"), ct)).ConfigureAwait(false);
			List<HistoryRow>? rows = html == null ? null : ParseHistory(html);

			lock (_priceGate) {
				p.HistoryAt = nowUtc.Ticks;   // read or not: tried today
				changed = true;

				if (rows != null) {
					foreach ((uint app, string name, bool shared) in games.Where(static g => !g.Shared)) {
						if (PaidFor(rows, name) is long paid) {
							p.Paid[app] = paid;
						}
					}
				}
			}

			Log.Debug($"refund prices: the purchase history {(rows == null ? "couldn't be read" : $"has {rows.Count} purchase(s)")}", bot.Name);
		}

		// Then the store, for what's still not known - a few a check.
		string country = bot.Country.Length == 2 ? bot.Country : "";
		int asked = 0;

		foreach ((uint app, _, _) in games) {
			if ((KnownPrice(app, nowUtc) != null) || (country.Length == 0) || (asked >= StoreAsksACheck)) {
				continue;
			}

			asked++;
			string? json = ReadStore != null ? await ReadStore(app, country, ct).ConfigureAwait(false)
				: (await GameCatalog.StoreGetAsync($"https://store.steampowered.com/api/appdetails?appids={app}&filters=price_overview&cc={country}", TimeSpan.FromSeconds(1.5), ct)
					.ConfigureAwait(false)).Body;

			if (ParseStorePrice(json, app) is { } price) {
				lock (_priceGate) {
					p.Store[app] = new StorePrice(price.Cents, price.Currency.Length > 0 ? price.Currency : bot.WalletCurrency.ToString(), nowUtc.Ticks);
					changed = true;
				}
			}
		}

		if (changed) {
			lock (_priceGate) {
				// Games long out of any refund window have nothing left to say.
				foreach (uint old in p.Store.Where(s => nowUtc.Ticks - s.Value.At > TimeSpan.FromDays(30).Ticks).Select(static s => s.Key).ToList()) {
					p.Store.Remove(old);
				}
			}

			SavePrices();
		}

		HashSet<uint> cheap = [.. games.Where(g => KnownPrice(g.App, nowUtc) is long cents && (cents <= threshold)).Select(static g => g.App)];

		foreach (uint app in cheap.Except(_cheap)) {
			Log.Debug($"refund protection: {Name(app)} cost {BuyPlanMoney(KnownPrice(app, nowUtc) ?? 0)} - at or under {BuyPlanMoney(threshold)}, not held", bot.Name);
		}

		_cheap = cheap;

		return cheap;
	}

	private string BuyPlanMoney(long cents) => Modules.BuyPlan.Money(cents, (int) bot.WalletCurrency);

	// ── the purchase history ────────────────────────────────────────────────

	/// <summary>One purchase in the history: its items (by name) and what it cost in cents (null: not read).</summary>
	public sealed record HistoryRow(string Type, List<string> Items, long? Cents);

	/// <summary>
	/// Steam's purchase history page (store.steampowered.com/account/history), read defensively: each row's type (its first
	/// line - "Purchase", "Gift Purchase", "Refund", "In-Game Purchase", ...), its items and its total. Null when it isn't
	/// that page (a sign-in page, an error).
	/// </summary>
	public static List<HistoryRow>? ParseHistory(string html) {
		if (!html.Contains("wallet_history_table", StringComparison.Ordinal) && !html.Contains("wallet_table_row", StringComparison.Ordinal)) {
			return null;
		}

		List<HistoryRow> rows = [];

		foreach (Match row in RowRegex().Matches(html)) {
			string body = row.Groups[1].Value;
			string? items = Cell(body, "wht_items"), type = Cell(body, "wht_type"), total = Cell(body, "wht_total");

			if ((items == null) || (type == null) || (total == null)) {
				continue;
			}

			List<string> names = [.. DivRegex().Matches(items).Where(static d => !d.Groups[1].Value.Contains("wth_payment", StringComparison.Ordinal))
				.Select(static d => Html.Text(d.Groups[2].Value)).Where(static t => t.Length > 0)];
			string kind = DivRegex().Match(type) is { Success: true } first ? Html.Text(first.Groups[2].Value) : Html.Text(type);
			rows.Add(new HistoryRow(kind, names, Money(Html.Text(total))));
		}

		return rows;
	}

	/// <summary>
	/// What was paid for a game, from the history: the newest plain "Purchase" of that one item alone. A gift, a key, a
	/// bundle or a basket of several things, a refund - not known (null).
	/// </summary>
	public static long? PaidFor(IReadOnlyList<HistoryRow> rows, string game) {
		string want = Plain(game);

		if (want.Length == 0) {
			return null;
		}

		foreach (HistoryRow r in rows) {
			if (r.Items.Any(i => Plain(i) == want)) {
				return r.Type.Equals("Purchase", StringComparison.OrdinalIgnoreCase) && (r.Items.Count == 1) ? r.Cents : null;
			}
		}

		return null;
	}

	/// <summary>A game's name for matching: letters and digits only, lower case ("SOS OPS!" → "sosops").</summary>
	private static string Plain(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

	/// <summary>
	/// A price as Steam writes it - "$4.99", "4,99€", "CDN$ 12.50", "1.299,00 ₽", "Free" - in cents. The last "." or ","
	/// with exactly two digits after it is the decimal point; any other is grouping. Null when there's no number.
	/// </summary>
	public static long? Money(string text) {
		Match m = NumberRegex().Match(text);

		if (!m.Success) {
			return text.Contains("free", StringComparison.OrdinalIgnoreCase) ? 0 : null;
		}

		string n = m.Value.Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).TrimEnd('.', ',');
		int dot = n.LastIndexOfAny(['.', ',']);
		bool cents = (dot >= 0) && (n.Length - dot - 1 == 2);
		string whole = new([.. (cents ? n[..dot] : n).Where(char.IsAsciiDigit)]);
		string frac = cents ? n[(dot + 1)..] : "00";

		return long.TryParse(whole.Length > 0 ? whole : "0", NumberStyles.None, CultureInfo.InvariantCulture, out long w)
			&& long.TryParse(frac, NumberStyles.None, CultureInfo.InvariantCulture, out long f) ? (w * 100) + f : null;
	}

	private static string? Cell(string row, string cls) => CellRegex().Matches(row).FirstOrDefault(c => c.Groups[1].Value.Split(' ').Contains(cls))?.Groups[2].Value;

	[GeneratedRegex(@"<tr[^>]*\bwallet_table_row\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex RowRegex();

	[GeneratedRegex(@"<td[^>]*\bclass\s*=\s*""([^""]*)""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex CellRegex();

	[GeneratedRegex(@"<div([^>]*)>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex DivRegex();

	[GeneratedRegex(@"\d[\d.,\s ]*")]
	private static partial Regex NumberRegex();

	// ── the store ───────────────────────────────────────────────────────────

	/// <summary>
	/// The store's answer to appdetails?filters=price_overview: the higher of its regular price (initial) and today's
	/// (final), in cents, with its currency - a game on sale is never cheaper here than its regular price. A free game (no
	/// price) is 0. Null when the store didn't say.
	/// </summary>
	public static (long Cents, string Currency)? ParseStorePrice(string? json, uint app) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty(app.ToString(CultureInfo.InvariantCulture), out JsonElement node) || (node.ValueKind != JsonValueKind.Object)
				|| !node.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)) {
				return null;
			}

			// A free game has no price: its data comes back as an empty list.
			if (!node.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Object) || !data.TryGetProperty("price_overview", out JsonElement price)
				|| (price.ValueKind != JsonValueKind.Object)) {
				return node.TryGetProperty("data", out JsonElement empty) && (empty.ValueKind == JsonValueKind.Array) ? (0, "") : null;
			}

			long initial = price.TryGetProperty("initial", out JsonElement i) && i.TryGetInt64(out long iv) ? iv : 0;
			long final = price.TryGetProperty("final", out JsonElement f) && f.TryGetInt64(out long fv) ? fv : 0;
			string currency = price.TryGetProperty("currency", out JsonElement c) ? c.GetString() ?? "" : "";

			return (initial <= 0) && (final <= 0) ? null : (Math.Max(initial, final), currency);
		} catch (JsonException) {
			return null;
		}
	}
}
