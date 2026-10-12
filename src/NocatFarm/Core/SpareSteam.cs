using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Everything spare items and booster packs ask Steam to DO, behind one door - so the checks can stand a pretend Steam in
/// front of the rules and see exactly what would have been turned into gems, sold or opened, without anything real.
/// </summary>
public interface ISpareSteam {
	/// <summary>Gems this item turns into right now (Steam's own figure), or null when Steam wouldn't say.</summary>
	Task<int?> GemValueAsync(SpareItem item, CancellationToken ct);

	/// <summary>Turn one item into gems. Gems = what Steam says it gave.</summary>
	Task<(bool Ok, int Gems, string? Why)> GrindAsync(SpareItem item, int expected, CancellationToken ct);

	/// <summary>List these on the market; returns how many went up, and a line to say.</summary>
	Task<(int Listed, string Said)> SellAsync(IReadOnlyList<SpareDecision> items, CancellationToken ct);

	/// <summary>Pack loose gems from one stack into sacks of 1000.</summary>
	Task<(bool Ok, string? Why)> PackSacksAsync(SpareItem gems, int sacks, CancellationToken ct);

	/// <summary>Open one booster pack: what came out, by name.</summary>
	Task<(bool Ok, List<string> Cards, string? Why)> UnpackAsync(SpareItem pack, CancellationToken ct);

	/// <summary>Send exactly these items to another of your accounts.</summary>
	Task<string> SendAsync(IReadOnlyList<SpareItem> items, string toAccount, CancellationToken ct);

	/// <summary>Hold items against a send, a sale or a craft running at the same moment (see Bot.ClaimItems).</summary>
	HashSet<ulong> Claim(IEnumerable<ulong> ids);

	void Release(IEnumerable<ulong> ids);

	/// <summary>The wait between two items: a few seconds to a minute, as a person working through an inventory.</summary>
	Task<bool> PauseAsync(CancellationToken ct);

	/// <summary>
	/// Whether the account may still act right now - asked again before every item, since a look takes minutes and a
	/// human-mode account's day can end, or its owner start playing, in the middle of one.
	/// </summary>
	bool MayAct => true;

	/// <summary>Scam watch is holding the account: nothing is sold or sent until 'scamwatch ok' (see ScamWatch.HeldBack).</summary>
	bool Held => false;

	/// <summary>
	/// What an item really went up for in the last <see cref="SellAsync"/> - priced again just before listing, so not the
	/// plan's figure - with the wallet's currency: the buyer's price and what you get. Null when it wasn't listed.
	/// </summary>
	(string Price, string YouGet)? ListedFor(ulong assetId) => null;
}

/// <summary>The real Steam, for one account. <paramref name="mayAct"/>: the human-mode gate the run keeps to (none: always).</summary>
public sealed class LiveSpareSteam(Bot bot, Func<bool>? mayAct = null) : ISpareSteam {
	public bool MayAct => mayAct?.Invoke() ?? true;

	private Uri Profile(string path) => new(WebSession.Community, $"/profiles/{bot.SteamId}/{path}");

	public bool Held => Modules.ScamWatch.HeldBack(bot) != null;
	private Uri Inventory => Profile("inventory/");

	public async Task<int?> GemValueAsync(SpareItem item, CancellationToken ct) {
		if (SteamGems.KnownValue(item) is int known) {
			return known;
		}

		if ((item.GooType <= 0) || (item.Game == 0)) {
			return 0;
		}

		string? body = await bot.Web.GetAsync(new Uri(WebSession.Community,
			$"/auction/ajaxgetgoovalueforitemtype/?appid={item.Game}&item_type={item.GooType}&border_color={item.GooBorder}"), ct).ConfigureAwait(false);
		int? value = SteamGems.ParseGooValue(body);

		if (value != null) {
			SteamGems.Remember(item, value.Value);
		}

		return value;
	}

	public async Task<(bool Ok, int Gems, string? Why)> GrindAsync(SpareItem item, int expected, CancellationToken ct) {
		string? body = await bot.Web.PostAsync(Profile("ajaxgrindintogoo/"), new Dictionary<string, string>(StringComparer.Ordinal) {
			["appid"] = item.Game.ToString(CultureInfo.InvariantCulture),
			["contextid"] = "6",
			["assetid"] = item.AssetId.ToString(CultureInfo.InvariantCulture),
			["goo_value_expected"] = expected.ToString(CultureInfo.InvariantCulture)
		}, Inventory, ct).ConfigureAwait(false);

		return SteamGems.ParseGrind(body);
	}

	public async Task<(int Listed, string Said)> SellAsync(IReadOnlyList<SpareDecision> items, CancellationToken ct) {
		// Nothing priced, nothing listed, before Steam has said which money this account's wallet is in.
		if (bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid) {
			return (0, new Said("{0}: Steam hasn't said this account's wallet currency yet - nothing listed", bot.Name).ToString());
		}

		List<Seller.Offer> offers = [.. items.Select(d => new Seller.Offer(d.Item.AssetId, GameNames.Of(d.Item.Game), d.Item.Name, d.PriceCents, d.ListCents, d.ReceiveCents, d.Item.Hash))];
		int currency = (int) bot.WalletCurrency;
		string said = await Seller.SellAsync(bot, offers, ct, () => MayAct, SpareRun.Repricer(items, SpareRules.From(bot.Cfg), currency),
			() => SpareRun.Hold(SpareLedger.Load(bot.Name), bot.Name),
			o => _listed[o.AssetId] = (Spare.Money(o.BuyerCents, currency), Spare.Money(o.YouGetCents, currency))).ConfigureAwait(false);

		// "name: listed 3 of 5, ..." - the count is the first number after "listed".
		int at = said.IndexOf("listed ", StringComparison.Ordinal);
		int listed = 0;

		if (at >= 0) {
			string rest = said[(at + 7)..];
			int end = 0;

			while ((end < rest.Length) && char.IsAsciiDigit(rest[end])) {
				end++;
			}

			int.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out listed);
		}

		return (listed, said);
	}

	private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (string Price, string YouGet)> _listed = new();

	public (string Price, string YouGet)? ListedFor(ulong assetId) => _listed.TryGetValue(assetId, out (string Price, string YouGet) at) ? at : null;

	public async Task<(bool Ok, string? Why)> PackSacksAsync(SpareItem gems, int sacks, CancellationToken ct) {
		string? body = await bot.Web.PostAsync(Profile("ajaxexchangegoo/"), new Dictionary<string, string>(StringComparer.Ordinal) {
			["appid"] = "753",
			["assetid"] = gems.AssetId.ToString(CultureInfo.InvariantCulture),
			["goo_denomination_in"] = "1",
			["goo_amount_in"] = (sacks * 1000).ToString(CultureInfo.InvariantCulture),
			["goo_denomination_out"] = "1000",
			["goo_amount_out_expected"] = sacks.ToString(CultureInfo.InvariantCulture)
		}, Inventory, ct).ConfigureAwait(false);

		return SteamGems.ParseExchange(body);
	}

	public async Task<(bool Ok, List<string> Cards, string? Why)> UnpackAsync(SpareItem pack, CancellationToken ct) {
		string? body = await bot.Web.PostAsync(Profile("ajaxunpackbooster/"), new Dictionary<string, string>(StringComparer.Ordinal) {
			["appid"] = pack.Game.ToString(CultureInfo.InvariantCulture),
			["communityitemid"] = pack.AssetId.ToString(CultureInfo.InvariantCulture)
		}, Inventory, ct).ConfigureAwait(false);

		return SteamGems.ParseUnpack(body);
	}

	public Task<string> SendAsync(IReadOnlyList<SpareItem> items, string toAccount, CancellationToken ct) => Looting.SendTheseAsync(bot, toAccount, items, ct);

	public HashSet<ulong> Claim(IEnumerable<ulong> ids) => bot.ClaimItems(ids);

	public void Release(IEnumerable<ulong> ids) => bot.ReleaseItems(ids);

	public async Task<bool> PauseAsync(CancellationToken ct) {
		int gap = Math.Clamp(bot.Cfg.SpareGapSeconds, 3, 600);

		try {
			// A human-mode account takes its time: up to four times as long, more often nearer the short end.
			await Task.Delay(bot.Cfg.LegitMode ? Rng.Seconds(gap, gap * 4) : Rng.Seconds(Math.Max(2, gap / 2), gap + (gap / 2)), ct).ConfigureAwait(false);

			return true;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			return false;
		}
	}
}

/// <summary>Reading Steam's answers about gems and booster packs, and what each item's gem value is.</summary>
/// <remarks>
/// The calls, as Steam's own inventory page and the long-standing community libraries make them:
/// <list type="bullet">
/// <item>gem value: GET /auction/ajaxgetgoovalueforitemtype/?appid=&amp;item_type=&amp;border_color= - goo_value; the item type
/// and border come from the item's own "GetGooValue(...)" action;</item>
/// <item>turn into gems: POST /profiles/&lt;id&gt;/ajaxgrindintogoo/ appid (the game), contextid 6, assetid, goo_value_expected -
/// success 1 and "goo_value_received " (with that trailing space) and goo_value_total;</item>
/// <item>gems into sacks: POST /profiles/&lt;id&gt;/ajaxexchangegoo/ appid 753, assetid (the gem stack), goo_denomination_in 1,
/// goo_amount_in, goo_denomination_out 1000, goo_amount_out_expected - success 1;</item>
/// <item>open a pack: POST /profiles/&lt;id&gt;/ajaxunpackbooster/ appid, communityitemid - success 1 and rgItems.</item>
/// </list>
/// Only Steam's yes counts, as for crafting (BadgeCraft.Succeeded): a number other than 1, false, a page or nothing is a no.
/// </remarks>
public static class SteamGems {
	private static readonly Dictionary<string, (int Value, DateTime At)> Values = new(StringComparer.Ordinal);

	/// <summary>A gem value is kept a week: Steam sets it per game and type, and it rarely moves.</summary>
	private static readonly TimeSpan ValueLife = TimeSpan.FromDays(7);

	public static int? KnownValue(SpareItem item) {
		lock (Values) {
			return Values.TryGetValue(item.GooKey, out var v) && (DateTime.UtcNow - v.At < ValueLife) ? v.Value : null;
		}
	}

	public static void Remember(SpareItem item, int value) {
		lock (Values) {
			Values[item.GooKey] = (value, DateTime.UtcNow);
		}
	}

	/// <summary>Only the checks: start from nothing known.</summary>
	internal static void Forget() {
		lock (Values) {
			Values.Clear();
		}
	}

	private static bool Yes(JsonElement root) => !root.TryGetProperty("success", out JsonElement s)
		|| (s.ValueKind == JsonValueKind.True) || ((s.ValueKind == JsonValueKind.Number) && s.TryGetInt32(out int n) && (n == 1));

	private static int? Number(JsonElement root, string name) {
		if (!root.TryGetProperty(name, out JsonElement v)) {
			return null;
		}

		return v.ValueKind switch {
			JsonValueKind.Number when v.TryGetInt32(out int n) => n,
			JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) => n,
			_ => null
		};
	}

	private static JsonDocument? Json(string? body) {
		if (string.IsNullOrWhiteSpace(body)) {
			return null;
		}

		try {
			JsonDocument doc = JsonDocument.Parse(body);

			if (doc.RootElement.ValueKind == JsonValueKind.Object) {
				return doc;
			}

			doc.Dispose();
		} catch (JsonException) {
			// a page, not an answer
		}

		return null;
	}

	private static string Why(string? body) => body == null ? "no answer" : Log.Scrub(body.Length > 150 ? body[..150] : body);

	/// <summary>goo_value from a gem-value answer; null when it isn't one.</summary>
	public static int? ParseGooValue(string? body) {
		using JsonDocument? doc = Json(body);

		if ((doc == null) || !Yes(doc.RootElement)) {
			return null;
		}

		return Number(doc.RootElement, "goo_value") is int v and >= 0 ? v : null;
	}

	public static (bool Ok, int Gems, string? Why) ParseGrind(string? body) {
		using JsonDocument? doc = Json(body);

		if ((doc == null) || !doc.RootElement.TryGetProperty("success", out _) || !Yes(doc.RootElement)) {
			return (false, 0, Why(body));
		}

		// Valve's own field name ends in a space; the trimmed one too, should that ever be fixed.
		int gems = Number(doc.RootElement, "goo_value_received ") ?? Number(doc.RootElement, "goo_value_received") ?? 0;

		return gems > 0 ? (true, gems, null) : (false, 0, Why(body));
	}

	public static (bool Ok, string? Why) ParseExchange(string? body) {
		using JsonDocument? doc = Json(body);

		return (doc != null) && doc.RootElement.TryGetProperty("success", out _) && Yes(doc.RootElement) ? (true, null) : (false, Why(body));
	}

	public static (bool Ok, List<string> Cards, string? Why) ParseUnpack(string? body) {
		using JsonDocument? doc = Json(body);
		List<string> cards = [];

		if ((doc == null) || !Yes(doc.RootElement) || !doc.RootElement.TryGetProperty("rgItems", out JsonElement items) || (items.ValueKind != JsonValueKind.Array)) {
			return (false, cards, Why(body));
		}

		foreach (JsonElement card in items.EnumerateArray()) {
			string name = InventoryContents.Text(card, "name");
			bool foil = card.TryGetProperty("foil", out JsonElement f) && ((f.ValueKind == JsonValueKind.True) || (InventoryContents.Text(card, "foil") == "1"));

			if (name.Length > 0) {
				cards.Add(foil ? name + " (foil)" : name);
			}
		}

		return (true, cards, null);
	}
}

/// <summary>
/// Prices for spare items and booster packs. What decides anything for an account - the price limit, the daily value
/// cap, gems or selling, what it's listed at - is priced in that account's OWN wallet currency, one item at a time
/// (<see cref="MarketQuotes"/>). A market search per game is only a shortlist, in US dollars: it says which items anybody
/// sells at all and which are worth the most, and decides whether a pack is worth opening (both sides in dollars).
/// </summary>
public static class SparePrices {
	/// <param name="Cents">Lowest listing in US dollars; 0 when nobody sells it.</param>
	public sealed record Row(int Cents, SpareKind Kind);

	/// <param name="Currency">Always 1, US dollars: what a search answers in, whatever is asked.</param>
	public sealed record Game(uint App, int Currency, long At, Dictionary<string, Row> Items) {
		public DateTime When => new(At, DateTimeKind.Utc);
	}

	public const string SackHash = "753-Sack of Gems";

	/// <summary>Steam's id for the US dollar - the search's only currency.</summary>
	private const int UsDollar = 1;

	/// <summary>How a market page is fetched: the price book's queue. The checks put a pretend market here.</summary>
	public static Func<string, CancellationToken, Task<(string? Json, bool Asked)>> Get = PriceBook.MarketGetAsync;

	private static Dictionary<string, Game>? _games;
	private static readonly Lock Gate = new();

	private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "state", "spareprices.json");

	private static string Key(uint app) => $"{app}:{UsDollar}";

	private static Dictionary<string, Game> Games() {
		if (_games != null) {
			return _games;
		}

		_games = new Dictionary<string, Game>(StringComparer.Ordinal);

		try {
			// Saved under another currency before searches were known to answer in dollars: dollars under the wrong name.
			if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<Game>>(File.ReadAllText(FilePath)) is { } list) {
				foreach (Game g in list.Where(static g => (g.Currency == UsDollar) && (DateTime.UtcNow - g.When < TimeSpan.FromDays(14)))) {
					_games[Key(g.App)] = g;
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Failed("couldn't read the saved spare-item prices", e);
		}

		return _games;
	}

	private static void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			AtomicFile.Write(FilePath, JsonSerializer.Serialize(Games().Values.ToList()));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("couldn't save the spare-item prices", e);
		}
	}

	/// <summary>Only the checks: forget everything, and put the real market back.</summary>
	public static void Reset() {
		lock (Gate) {
			_games = new Dictionary<string, Game>(StringComparer.Ordinal);
		}

		MarketQuotes.Reset();
		Get = PriceBook.MarketGetAsync;
	}

	/// <summary>A game's US-dollar shortlist as held, however old - the plan decides what's too old.</summary>
	public static Game? Cached(uint app) {
		lock (Gate) {
			return Games().GetValueOrDefault(Key(app));
		}
	}

	/// <summary>
	/// An item's price in <paramref name="currency"/> (the account's own wallet currency) as held now, with when it was read
	/// - null when it was never priced in it. Nobody selling it is the same in every currency: a newer shortlist that
	/// doesn't have it on sale says so (0). A shortlist price is never handed out as this currency's.
	/// </summary>
	public static MarketPrice? Of(SpareItem item, int currency) {
		if (currency <= 0) {
			return null;
		}

		MarketQuote? quote = MarketQuotes.Of(item.Hash, currency);
		uint app = item.Kind is SpareKind.Gems or SpareKind.GemSack ? 753u : item.Game;

		if ((Cached(app) is { } g) && (quote == null || g.When > quote.When) && (!g.Items.TryGetValue(item.Hash, out Row? row) || (row.Cents <= 0))) {
			return new MarketPrice(0, g.When);   // priced, and nobody sells this one
		}

		return quote?.Price;
	}

	/// <summary>What an item is worth in US dollars on the shortlist, for putting the most valuable first - 0 when not known.</summary>
	public static int ShortlistCents(SpareItem item) =>
		(Cached(item.Game) is { } g) && g.Items.TryGetValue(item.Hash, out Row? row) ? row.Cents : 0;

	/// <summary>Whether the shortlist says somebody sells this item (true when it doesn't know).</summary>
	public static bool MaySell(SpareItem item) =>
		(Cached(item.Game) is not { } g) || (g.Items.TryGetValue(item.Hash, out Row? row) && (row.Cents > 0));

	/// <summary>A sack of gems' price in the account's own wallet currency, as held now.</summary>
	public static MarketPrice? Sack(int currency) => currency > 0 ? MarketQuotes.Of(SackHash, currency)?.Price : null;

	/// <summary>
	/// The prices a pack decision needs: the pack's own and each plain card of its set - both in US dollars, from one
	/// shortlist, so which is worth more doesn't depend on the currency.
	/// </summary>
	public static Spare.PackPrices? Pack(uint app) {
		if (Cached(app) is not { } g) {
			return null;
		}

		Row? pack = g.Items.Values.FirstOrDefault(static r => r.Kind == SpareKind.BoosterPack);

		return new Spare.PackPrices(pack == null ? null : new MarketPrice(pack.Cents, g.When),
			[.. g.Items.Values.Where(static r => r.Kind == SpareKind.Card).Select(static r => r.Cents)], g.When);
	}

	/// <summary>
	/// One game's US-dollar shortlist from the market, every page of its search. False when the market didn't answer (or
	/// is pausing): what was held stays, and the items wait.
	/// </summary>
	public static async Task<bool> PriceGameAsync(uint app, CancellationToken ct) {
		Dictionary<string, Row> items = new(StringComparer.Ordinal);

		for (int start = 0, pages = 0; pages < 20; pages++) {
			(string? json, _) = await Get($"/market/search/render/?norender=1&appid=753&category_753_Game[]=tag_app_{app}&start={start}&count=100&currency={UsDollar}&l=english", ct).ConfigureAwait(false);

			if (ParseSearch(json) is not { } page) {
				return false;
			}

			foreach ((string hash, Row row) in page.Rows) {
				items[hash] = row;
			}

			start += page.Rows.Count;

			if ((page.Rows.Count == 0) || (start >= page.Total)) {
				break;
			}
		}

		Keep(new Game(app, UsDollar, DateTime.UtcNow.Ticks, items));

		return true;
	}

	/// <summary>Price a sack of gems in the account's own wallet currency. False when the market didn't answer.</summary>
	public static async Task<bool> PriceSackAsync(int currency, CancellationToken ct) => await MarketQuotes.FetchAsync(SackHash, currency, ct).ConfigureAwait(false) != null;

	/// <summary>Price one item in the account's own wallet currency. False when the market didn't answer.</summary>
	public static async Task<bool> QuoteAsync(SpareItem item, int currency, CancellationToken ct) => await MarketQuotes.FetchAsync(item.Hash, currency, ct).ConfigureAwait(false) != null;

	internal static void Keep(Game game) {
		lock (Gate) {
			Games()[Key(game.App)] = game;
			Save();
		}
	}

	internal sealed record Page(int Total, Dictionary<string, Row> Rows);

	/// <summary>A market search page: each result's market name, lowest listing (0 with no listing) and kind.</summary>
	internal static Page? ParseSearch(string? json) {
		if (string.IsNullOrEmpty(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)
				|| (root.TryGetProperty("success", out JsonElement ok) && (ok.ValueKind == JsonValueKind.False))) {
				return null;
			}

			Dictionary<string, Row> rows = new(StringComparer.Ordinal);

			foreach (JsonElement r in results.EnumerateArray()) {
				string hash = InventoryContents.Text(r, "hash_name");

				if (hash.Length == 0) {
					continue;
				}

				int listings = int.TryParse(InventoryContents.Text(r, "sell_listings"), NumberStyles.None, CultureInfo.InvariantCulture, out int l) ? l : 0;
				int price = int.TryParse(InventoryContents.Text(r, "sell_price"), NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : 0;
				SpareKind kind = r.TryGetProperty("asset_description", out JsonElement d) ? Spare.KindOf(d) : SpareKind.Other;
				rows[hash] = new Row(listings > 0 ? price : 0, kind);
			}

			int total = root.TryGetProperty("total_count", out JsonElement t) && t.TryGetInt32(out int n) ? n : rows.Count;

			return new Page(total, rows);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>
	/// A price as the market writes it for people - "$0.05", "0,05€", "CDN$ 1.23", "R$ 0,05", "¥ 12" - in cents. The last
	/// separator followed by exactly two digits is the decimal point; anything else is a whole amount. Null when there's no
	/// number in it.
	/// </summary>
	public static int? MoneyCents(string? text) {
		if (string.IsNullOrWhiteSpace(text)) {
			return null;
		}

		string kept = new([.. text.Where(static c => char.IsAsciiDigit(c) || c is '.' or ',')]);
		kept = kept.Trim('.', ',');

		if (kept.Length == 0) {
			return null;
		}

		int sep = kept.LastIndexOfAny(['.', ',']);
		bool decimals = (sep >= 0) && (kept.Length - sep - 1 == 2);
		string whole = new([.. (decimals ? kept[..sep] : kept).Where(char.IsAsciiDigit)]);
		string cents = decimals ? kept[(sep + 1)..] : "00";

		return long.TryParse(whole.Length == 0 ? "0" : whole, NumberStyles.None, CultureInfo.InvariantCulture, out long w) && (w < 10_000_000)
			? (int) ((w * 100) + int.Parse(cents, CultureInfo.InvariantCulture))
			: null;
	}
}
