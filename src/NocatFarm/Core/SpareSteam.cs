using System.Globalization;
using System.Net;
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

	/// <summary>
	/// After a turn into gems that got no clear yes (no answer, a timeout, an error): whether it went through anyway - the
	/// inventory read again, the item gone and the gems up by at least <paramref name="expected"/>. The gems it gave, or null
	/// when it's still there, the gems aren't in, or the inventory couldn't be read: then it's a failure.
	/// </summary>
	Task<int?> GemsIfGoneAsync(SpareItem item, int expected, CancellationToken ct) => Task.FromResult<int?>(null);

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

	/// <summary>The wait between two items: seconds on a robot account, minutes on a human-mode one (see <see cref="LiveSpareSteam.Gap"/>).</summary>
	Task<bool> PauseAsync(CancellationToken ct);

	/// <summary>
	/// Whether the account may still act right now - asked again before every item, since a look takes minutes and a
	/// human-mode account's day can end, or its owner start playing, in the middle of one.
	/// </summary>
	bool MayAct => true;

	/// <summary>Scam watch is holding the account: nothing is sold or sent until 'scamwatch ok' (see ScamWatch.HeldBack).</summary>
	bool Held => false;

	/// <summary>
	/// 1.8.2: whether this item may still go, right before it's turned into gems, listed or sent - by the settings as they are
	/// now (<see cref="SpareRun.StillOk"/>). Null when it may; All: the look stops there.
	/// </summary>
	(Said Why, bool All)? StillOk(SpareItem item, int worthCents) => null;

	/// <summary>
	/// 1.8.2: right before an item the look had no price for at all becomes gems - its price read now, and the price limit
	/// and the gems rule checked again (<see cref="SpareRun.BeforeGems"/>). Null when it may become gems.
	/// </summary>
	Task<Said?> BeforeGemsAsync(SpareDecision d, CancellationToken ct) => Task.FromResult<Said?>(null);

	/// <summary>
	/// What an item really went up for in the last <see cref="SellAsync"/> - priced again just before listing, so not the
	/// plan's figure - with the wallet's currency: the buyer's price and what you get. Null when it wasn't listed.
	/// </summary>
	(string Price, string YouGet)? ListedFor(ulong assetId) => null;

	/// <summary>The same in cents of the wallet's currency: what you get, and the currency. Null when it wasn't listed.</summary>
	(int YouGetCents, int Currency)? ListedCents(ulong assetId) => null;

	/// <summary>1.8.2: Steam's reason for refusing a listing in the last <see cref="SellAsync"/> ("" when it gave none); null when none was refused.</summary>
	string? Refused => null;

	/// <summary>1.8.2: Steam's market held for this account, or listing paused after a refusal (<see cref="MarketHold.SellBlocked"/>) - nothing is listed. Null when it may list.</summary>
	Said? MarketHeld => null;
}

/// <summary>The real Steam, for one account. <paramref name="mayAct"/>: the human-mode gate the run keeps to (none: always).
/// <paramref name="spread"/>: a human-mode look's spread through its day - its waits between items fill the time to the next look.
/// <paramref name="gemsAtStart"/>: the loose gems the look's inventory held (-1: not known) - what an unclear answer is checked against.</summary>
public sealed class LiveSpareSteam(Bot bot, Func<bool>? mayAct = null, SpareSpread.Plan? spread = null, long gemsAtStart = -1) : ISpareSteam {
	/// <summary>The loose gems there should be now: the look's start, plus what each yes gave.</summary>
	private long _gems = gemsAtStart;

	public bool MayAct => mayAct?.Invoke() ?? true;

	/// <summary>By the settings as they are now; the price limit against the freshest price known for it, or the look's.</summary>
	public (Said Why, bool All)? StillOk(SpareItem item, int worthCents) {
		MarketPrice? fresh = SpareRun.PriceFor((int) bot.WalletCurrency, SpareRules.From(bot.Cfg))(item);

		return SpareRun.StillOk(bot, item, fresh is { } p ? Math.Max(worthCents, ListingPrice.Worth(p)) : worthCents);
	}

	public async Task<Said?> BeforeGemsAsync(SpareDecision d, CancellationToken ct) {
		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

		// 1.8.2: also an item priced only by an estimate from the dollar search, or over an hour ago - read again in the wallet's money.
		if (!SpareRun.NeedsPriceBeforeGems(d, currency, DateTime.UtcNow)) {
			return null;
		}

		if (currency <= 0) {
			return new Said("no price right now - kept until it has one");
		}

		SpareRules rules = SpareRules.From(bot.Cfg);
		MarketQuote? now = await MarketQuotes.FetchAsync(d.Item.Hash, currency, ct).ConfigureAwait(false);

		if ((SparePrices.Sack(currency) is not { Cents: > 0 } sack) || (DateTime.UtcNow - sack.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))) {
			if ((now != null) && (ListingPrice.Worth(now.Price) > 0)) {
				await SparePrices.PriceSackAsync(currency, ct).ConfigureAwait(false);
			}
		}

		return SpareRun.BeforeGems(d, now, SparePrices.SackPays(currency, rules.DumpPct), rules, currency, DateTime.UtcNow);
	}

	private Uri Profile(string path) => new(WebSession.Community, $"/profiles/{bot.SteamId}/{path}");

	public bool Held => Modules.ScamWatch.HeldBack(bot) != null;

	public Said? MarketHeld => MarketHold.SellBlocked(bot);

	public string? Refused { get; private set; }
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
		} else {
			SteamGems.NoAnswer(item, DateTime.UtcNow);
		}

		return value;
	}

	public async Task<(bool Ok, int Gems, string? Why)> GrindAsync(SpareItem item, int expected, CancellationToken ct) {
		string? body = await bot.Web.PostAsync(Profile("ajaxgrindintogoo/"), new Dictionary<string, string>(StringComparer.Ordinal) {
			["appid"] = item.Game.ToString(CultureInfo.InvariantCulture),
			["contextid"] = "6",
			["assetid"] = item.AssetId.ToString(CultureInfo.InvariantCulture),
			["goo_value_expected"] = expected.ToString(CultureInfo.InvariantCulture)
		}, Inventory, ct, errorVerdict: true).ConfigureAwait(false);

		// Steam's refusal comes with an error status and its reason in the body ("The item you selected has expired or no
		// longer exists."): read, so the log says that instead of "no answer".
		(bool Ok, int Gems, string? Why) result = SteamGems.ParseGrind(body);

		if (result.Ok && (_gems >= 0)) {
			_gems += result.Gems;
		}

		return Counted(bot.Name, result);
	}

	/// <summary>
	/// Steam's answer to turning an item into gems, as it is - and a yes counted in the history and the live stats on
	/// nocat.lol (1.8.1). Only a yes: a no, or no clear answer, counts nothing (an unclear one the inventory then shows went
	/// through is counted by <see cref="GemsIfGoneAsync"/>).
	/// </summary>
	public static (bool Ok, int Gems, string? Why) Counted(string account, (bool Ok, int Gems, string? Why) result) {
		if (result.Ok) {
			History.Note(FarmTotals.Extra.Gems, account, DateTime.UtcNow);
		}

		return result;
	}

	/// <summary>Steam's answer to opening a booster pack, as it is - a yes counted in the history and the live stats (1.8.1).</summary>
	public static (bool Ok, List<string> Cards, string? Why) Opened(string account, (bool Ok, List<string> Cards, string? Why) result) {
		if (result.Ok) {
			History.Note(FarmTotals.Extra.Pack, account, DateTime.UtcNow);
		}

		return result;
	}

	public async Task<int?> GemsIfGoneAsync(SpareItem item, int expected, CancellationToken ct) {
		if ((_gems < 0) || (expected <= 0)) {
			return null;   // nothing to compare with: a failure, as before
		}

		InventoryContents? inventory = await NocatFarm.Core.Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory is not { Complete: true }) {
			return null;
		}

		List<SpareItem> now = Spare.Read(inventory);
		long gems = now.Where(static i => i.Kind == SpareKind.Gems).Sum(static i => (long) i.Amount);

		// Gone, and the gems in: it went through. Gems spent meanwhile (a booster pack made) make it look like it didn't -
		// counted as a failure then, which never counts against today's limit.
		if (now.Exists(i => i.AssetId == item.AssetId) || (gems - _gems < expected)) {
			return null;
		}

		_gems += expected;
		Counted(bot.Name, (true, expected, null));   // it went through after all: counted like a clear yes

		return expected;
	}

	public async Task<(int Listed, string Said)> SellAsync(IReadOnlyList<SpareDecision> items, CancellationToken ct) {
		// Nothing priced, nothing listed, before Steam has said which money this account's wallet is in.
		if (bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid) {
			return (0, new Said("{0}: Steam hasn't said this account's wallet currency yet - nothing listed", bot.Name).ToString());
		}

		Refused = null;
		List<Seller.Offer> offers = [.. items.Select(d => new Seller.Offer(d.Item.AssetId, GameNames.Of(d.Item.Game), d.Item.Name, d.PriceCents, d.ListCents, d.ReceiveCents, d.Item.Hash))];
		int currency = (int) bot.WalletCurrency;
		Seller.Reprice reprice = SpareRun.Guarded(bot, items, rules => SpareRun.Repricer(items, rules, currency, hash => SpareListings.Load(bot.Name).StepOf(hash, DateTime.UtcNow),
			hash => SpareListings.Forget(bot.Name, hash), SpareLedger.Load(bot.Name), hash => OurListings.UpFor(OurListings.Load(bot.Name), hash), SparePrices.SackPays(currency, SpareRules.From(bot.Cfg).DumpPct),
			hash => Modules.BuyLedger.Of(bot.Name).PaidFor(hash, currency)));
		string said = await Seller.SellAsync(bot, offers, ct, () => MayAct, reprice,
			() => SpareRun.Hold(SpareLedger.Load(bot.Name), bot.Name) ?? SpareRun.Halted(bot),
			o => {
				_listed[o.AssetId] = (Spare.Money(o.BuyerCents, currency), Spare.Money(o.YouGetCents, currency));
				_listedCents[o.AssetId] = (o.YouGetCents, currency);
			},
			// A human-mode account's listings are spread out like its gems; a robot keeps the market's own gap.
			bot.Cfg.LegitMode ? async t => await PauseAsync(t).ConfigureAwait(false) : null, refused: why => Refused = why).ConfigureAwait(false);

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

	private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (int YouGetCents, int Currency)> _listedCents = new();

	public (int YouGetCents, int Currency)? ListedCents(ulong assetId) => _listedCents.TryGetValue(assetId, out (int YouGetCents, int Currency) at) ? at : null;

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

		return Opened(bot.Name, SteamGems.ParseUnpack(body));
	}

	public Task<string> SendAsync(IReadOnlyList<SpareItem> items, string toAccount, CancellationToken ct) => Looting.SendTheseAsync(bot, toAccount, items, ct);

	public HashSet<ulong> Claim(IEnumerable<ulong> ids) => bot.ClaimItems(ids);

	public void Release(IEnumerable<ulong> ids) => bot.ReleaseItems(ids);

	public async Task<bool> PauseAsync(CancellationToken ct) {
		(TimeSpan lo, TimeSpan hi) = Gap(bot.Cfg);

		if (bot.Cfg.LegitMode && (spread != null)) {
			(lo, hi) = SpareSpread.Gap(lo, hi, spread);
		}

		try {
			await Task.Delay(TimeSpan.FromSeconds(Rng.Next((int) lo.TotalSeconds, (int) hi.TotalSeconds + 1)), ct).ConfigureAwait(false);

			return true;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			return false;
		}
	}

	/// <summary>
	/// The shortest and longest wait between two items, picked at random in between each time: on a human-mode account
	/// minutes ("Wait between items, human mode", 3 to 20), on a robot the seconds gap, half to one and a half times it.
	/// </summary>
	public static (TimeSpan Lo, TimeSpan Hi) Gap(BotConfig cfg) {
		if (cfg.LegitMode) {
			int lo = Math.Clamp(cfg.SpareHumanGapMinMinutes, 0, 240);

			return (TimeSpan.FromMinutes(lo), TimeSpan.FromMinutes(Math.Max(lo, Math.Clamp(cfg.SpareHumanGapMaxMinutes, 0, 240))));
		}

		int gap = Math.Clamp(cfg.SpareGapSeconds, 3, 600);

		return (TimeSpan.FromSeconds(Math.Max(2, gap / 2)), TimeSpan.FromSeconds(gap + (gap / 2)));
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
	private static Dictionary<string, (int Value, DateTime At)>? _values;

	/// <summary>
	/// A gem value is kept a month: Steam sets it per game, type and border, and it rarely moves. 1.8.2: kept on disk too
	/// (config/state/gemvalues.json) - held only in memory, every restart forgot them all: one account's 445-564 items sat
	/// "gem value isn't known yet" all day, 20 learned a look and every update starting over (5 restarts on 2026-10-19).
	/// </summary>
	private static readonly TimeSpan ValueLife = TimeSpan.FromDays(30);

	/// <summary>1.8.2: a lookup Steam didn't answer isn't asked again for this long - the next ones get their turn.</summary>
	public static readonly TimeSpan FailedWait = TimeSpan.FromHours(6);

	/// <summary>When a lookup last got no answer, by <see cref="SpareItem.GooKey"/> - in memory only.</summary>
	private static readonly Dictionary<string, DateTime> Failed = new(StringComparer.Ordinal);

	private static readonly Lock Gate = new();

	private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "state", "gemvalues.json");

	/// <summary>One saved gem value: <see cref="SpareItem.GooKey"/>, the gems, when Steam said it (UTC ticks).</summary>
	public sealed record Saved(string Key, int Value, long At);

	private static Dictionary<string, (int Value, DateTime At)> Values() {
		if (_values != null) {
			return _values;
		}

		_values = new Dictionary<string, (int, DateTime)>(StringComparer.Ordinal);

		try {
			if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FilePath)) is { } list) {
				foreach (Saved v in list.Where(static v => (v.Key.Length > 0) && (v.Value >= 0) && (DateTime.UtcNow - new DateTime(v.At, DateTimeKind.Utc) < ValueLife))) {
					_values[v.Key] = (v.Value, new DateTime(v.At, DateTimeKind.Utc));
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Failed("couldn't read the saved gem values", e);
		}

		return _values;
	}

	private static void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			AtomicFile.Write(FilePath, JsonSerializer.Serialize(Values().Where(static v => DateTime.UtcNow - v.Value.At < ValueLife)
				.Select(static v => new Saved(v.Key, v.Value.Value, v.Value.At.Ticks)).ToList()));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("couldn't save the gem values", e);
		}
	}

	public static int? KnownValue(SpareItem item) {
		lock (Gate) {
			return Values().TryGetValue(item.GooKey, out var v) && (DateTime.UtcNow - v.At < ValueLife) ? v.Value : null;
		}
	}

	public static void Remember(SpareItem item, int value) {
		lock (Gate) {
			Values()[item.GooKey] = (value, DateTime.UtcNow);
			Failed.Remove(item.GooKey);
			Save();
		}
	}

	/// <summary>1.8.2: Steam didn't say an item's gem value - not asked again for <see cref="FailedWait"/>.</summary>
	public static void NoAnswer(SpareItem item, DateTime now) {
		lock (Gate) {
			Failed[item.GooKey] = now;
		}
	}

	/// <summary>1.8.2: whether an item's gem value is worth asking for now: not known, and not asked in vain lately.</summary>
	public static bool Ask(SpareItem item, DateTime now) {
		if (KnownValue(item) != null) {
			return false;
		}

		lock (Gate) {
			return !Failed.TryGetValue(item.GooKey, out DateTime at) || (now - at >= FailedWait);
		}
	}

	/// <summary>Only the checks: start from nothing known (the file is left as it is).</summary>
	internal static void Forget() {
		lock (Gate) {
			_values = new Dictionary<string, (int, DateTime)>(StringComparer.Ordinal);
			Failed.Clear();
		}
	}

	/// <summary>Only the checks: as if the program had just started - the values are read from the file again.</summary>
	public static void ForgetLoaded() {
		lock (Gate) {
			_values = null;
			Failed.Clear();
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

	private static string Why(string? body) {
		if (body == null) {
			return "no answer";
		}

		// Steam's own words when it gives them ({"success":42,"message":"The item you selected has expired or no longer exists."}).
		using (JsonDocument? doc = Json(body)) {
			if ((doc != null) && doc.RootElement.TryGetProperty("message", out JsonElement m) && (m.ValueKind == JsonValueKind.String) && (m.GetString() is { Length: > 0 } said)) {
				return Log.Scrub(said.Length > 150 ? said[..150] : said);
			}
		}

		return Log.Scrub(body.Length > 150 ? body[..150] : body);
	}

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
	/// - null when it was never priced in it. Nobody selling it is the same in every currency: a newer shortlist that lists
	/// it with no listing at all says so (0). A shortlist price is never handed out as this currency's.
	/// </summary>
	/// <remarks>
	/// Only the market saying so - a search result with no listing, or priceoverview's own answer - means nobody sells it.
	/// An item that simply wasn't on the pages fetched has no price yet and gets a lookup of its own (<see cref="NotListed"/>):
	/// taken as nobody selling, 93 items were kept on an account where single lookups had found 32 that morning.
	/// </remarks>
	public static MarketPrice? Of(SpareItem item, int currency) {
		if (currency <= 0) {
			return null;
		}

		MarketQuote? quote = MarketQuotes.Of(item.Hash, currency);
		uint app = item.Kind is SpareKind.Gems or SpareKind.GemSack ? 753u : item.Game;

		if ((Cached(app) is { } g) && (quote == null || g.When > quote.When) && (RowOf(g, item.Hash) is { Cents: <= 0 })) {
			return new MarketPrice(0, g.When);   // priced, and nobody sells this one
		}

		return quote?.Price;
	}

	/// <summary>
	/// An item's row on its game's shortlist - by its market name exactly, or written the way a page might write it
	/// differently (an "&amp;" left in, other capitals). Null when the pages didn't have it.
	/// </summary>
	private static Row? RowOf(Game g, string hash) {
		if (g.Items.TryGetValue(hash, out Row? row)) {
			return row;
		}

		string plain = WebUtility.HtmlDecode(hash);

		foreach ((string name, Row r) in g.Items) {
			if (string.Equals(WebUtility.HtmlDecode(name), plain, StringComparison.OrdinalIgnoreCase)) {
				return r;
			}
		}

		return null;
	}

	/// <summary>
	/// Its game's shortlist is fresh but doesn't have it at all - not on the pages fetched, which isn't the market saying
	/// nobody sells it: it gets a lookup of its own in the wallet's currency.
	/// </summary>
	public static bool NotListed(SpareItem item) => (Cached(item.Game) is { } g) && (RowOf(g, item.Hash) == null);

	/// <summary>An item's US-dollar price on its game's shortlist, by market name ("440-Scout") - null when not known or nobody sells it.</summary>
	public static int? UsdCents(string hash) =>
		(hash.IndexOf('-') is int dash and > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app)
		&& (Cached(app) is { } g) && (RowOf(g, hash) is { Cents: > 0 } row) ? row.Cents : null;

	/// <summary>
	/// An item's price for DECIDING, in the wallet's currency: its dollar shortlist price at the learned rate (see
	/// <see cref="WalletRate"/>), dated by the shortlist - one search page priced it, no request of its own. Null when there's
	/// no shortlist price or no rate yet, and near the price limit (from 80% to 125% of it): a converted price could fall
	/// either side, so that item is asked about in its own currency instead. Never a listing price: a sale is always priced
	/// again in the real currency right before it goes up.
	/// </summary>
	public static MarketPrice? Estimate(SpareItem item, int currency, int maxCents) {
		if ((Cached(item.Game) is not { } g) || (RowOf(g, item.Hash) is not { Cents: > 0 } row) || (WalletRate.ForDeciding(currency) is not double rate)) {
			return null;
		}

		int cents = WalletRate.ToWallet(row.Cents, rate);

		if (Borderline(cents, maxCents)) {
			return null;
		}

		// The shortlist has the cheapest listing only: taken as the real price too, two sales behind it - the listing rule
		// then reads it as it is. What it really sells for is asked right before listing - and (1.8.2) right before it becomes
		// gems (SpareRun.NeedsPriceBeforeGems), so one dumped listing on the search never turns a card into gems.
		return new MarketPrice(Math.Max(1, cents), g.When, Math.Max(1, cents), 2);
	}

	/// <summary>A converted price too close to the price limit to trust either way: 80% to 125% of it.</summary>
	public static bool Borderline(int cents, int maxCents) => (cents * 5L >= maxCents * 4L) && (cents * 4L <= maxCents * 5L);

	/// <summary>Whether an item's converted price is near the price limit, so it's asked about in its own currency.</summary>
	public static bool NeedsOwnPrice(SpareItem item, int currency, int maxCents) =>
		(Cached(item.Game) is { } g) && (RowOf(g, item.Hash) is { Cents: > 0 } row) && (WalletRate.ForDeciding(currency) is double rate)
		&& Borderline(WalletRate.ToWallet(row.Cents, rate), maxCents);

	/// <summary>What an item is worth in US dollars on the shortlist, for putting the most valuable first - 0 when not known.</summary>
	public static int ShortlistCents(SpareItem item) =>
		(Cached(item.Game) is { } g) && (RowOf(g, item.Hash) is { } row) ? row.Cents : 0;

	/// <summary>Whether somebody may sell this item: false only when the shortlist has it with no listing (true when it doesn't know).</summary>
	public static bool MaySell(SpareItem item) =>
		(Cached(item.Game) is not { } g) || (RowOf(g, item.Hash) is not { Cents: <= 0 });

	/// <summary>A sack of gems' price in the account's own wallet currency, as held now.</summary>
	public static MarketPrice? Sack(int currency) => currency > 0 ? MarketQuotes.Of(SackHash, currency)?.Price : null;

	/// <summary>
	/// 1.8.2: what a sack of gems really pays - listed a cent under the cheapest (a dumped listing ignored), after Steam's
	/// fee - with when it was read: what gems are worth wherever they're weighed against selling. A sack's buyer price
	/// overvalued them by about a sixth (0.94 CAD, where a sack pays 0.81), so items were turned into gems that selling paid
	/// more for. Cents 0 when nobody sells one; null when it was never priced in this currency.
	/// </summary>
	public static MarketPrice? SackPays(int currency, int dumpPct) => Sack(currency) is { } sack ? Pays(sack, dumpPct) : null;

	/// <summary>What a sack listed at this price pays after the fee (<see cref="SackPays"/>).</summary>
	public static MarketPrice Pays(MarketPrice sack, int dumpPct) =>
		new((sack.Cents > 0) && (ListingPrice.For(sack, 3, Math.Clamp(dumpPct, 0, 95)).Cents is int buyer) ? Seller.ReceiveFor(buyer) : 0, sack.At, null, sack.Sales);

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

	public static void Keep(Game game) {
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
				// Written as the inventory writes it: "&amp;" on a page is the "&" in the item's own market name.
				string hash = WebUtility.HtmlDecode(InventoryContents.Text(r, "hash_name"));

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
