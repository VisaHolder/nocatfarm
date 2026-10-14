using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>What one Steam community item is, as far as spare items and booster packs care.</summary>
public enum SpareKind { Other, Card, FoilCard, Background, Emoticon, BoosterPack, Gems, GemSack }

/// <summary>One item in the Steam inventory (app 753, context 6).</summary>
/// <param name="Game">The game it belongs to (market_fee_app); 0 when it belongs to none.</param>
/// <param name="Hash">Its market name ("440-Scout"), the same for every copy - what a price is kept under.</param>
/// <param name="GooType">The item type Steam's "Turn into gems" action asks about, 0 when it has no such action.</param>
public sealed record SpareItem(ulong AssetId, uint Game, SpareKind Kind, string Hash, string Name, bool Tradable, bool Marketable,
	uint Amount = 1, int GooType = 0, int GooBorder = 0) {
	/// <summary>Where its gem value is kept: the same for every item of this game, type and border.</summary>
	public string GooKey => $"{Game}:{GooType}:{GooBorder}";
}

/// <summary>
/// A market price in cents of the account's own wallet currency, and when it was read: the lowest listing (0 when nobody
/// sells it), the median of the last day's sales (null when nothing sold) and how many sold. See <see cref="MarketQuote"/>.
/// </summary>
public readonly record struct MarketPrice(int Cents, DateTime At, int? Median = null, int Sales = 0);

/// <summary>A game badge's level, plain and foil, by game.</summary>
public sealed record SpareBadges(Dictionary<uint, int> Levels, Dictionary<uint, int> FoilLevels) {
	/// <summary>
	/// Steam's GetBadges answer: every game badge with its level, the foil one (border_color 1) apart. Null when the answer
	/// isn't one - a refusal reads as "no badges at all", and that would make every card of a maxed game look needed.
	/// </summary>
	public static SpareBadges? Parse(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("response", out JsonElement res) || !res.TryGetProperty("player_xp", out _)) {
				return null;
			}

			Dictionary<uint, int> levels = [], foils = [];

			if (res.TryGetProperty("badges", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement b in list.EnumerateArray()) {
					if (!b.TryGetProperty("appid", out JsonElement a) || !a.TryGetUInt32(out uint app) || !b.TryGetProperty("level", out JsonElement lv)
						|| !lv.TryGetInt32(out int level)) {
						continue;
					}

					bool foil = b.TryGetProperty("border_color", out JsonElement border) && border.TryGetInt32(out int bc) && (bc != 0);
					(foil ? foils : levels)[app] = Math.Max(level, (foil ? foils : levels).GetValueOrDefault(app));
				}
			}

			return new SpareBadges(levels, foils);
		} catch (JsonException) {
			return null;
		}
	}
}

/// <summary>The spare-items settings of one account, read once per look so a change halfway through can't mix two.</summary>
public sealed record SpareRules {
	public bool On { get; init; }

	/// <summary>0 best of gems or selling, 1 gems only, 2 sell only, 3 keep everything (just report).</summary>
	public int Action { get; init; }

	public bool MaxedBadgeCards { get; init; } = true;
	public bool ExtraCards { get; init; } = true;
	public int KeepCopies { get; init; } = 1;
	public bool Backgrounds { get; init; } = true;
	public int KeepBackgrounds { get; init; } = 1;
	public bool Emoticons { get; init; } = true;
	public int KeepEmoticons { get; init; } = 1;
	public bool Foils { get; init; }
	public int MaxPriceCents { get; init; } = 25;
	public int NewDays { get; init; } = 7;
	public IReadOnlyCollection<string> KeepNames { get; init; } = [];
	public IReadOnlyCollection<uint> KeepGames { get; init; } = [];
	public int MarginPct { get; init; } = 20;
	public int PerRun { get; init; } = 5;
	public int DailyItems { get; init; } = 10;
	public int DailyValueCents { get; init; } = 100;
	public int PriceMaxAgeHours { get; init; } = 24;

	/// <summary>0 a cent under the real price, 1 the same as the cheapest listing, 2 the median sale price.</summary>
	public int SellPrice { get; init; }

	/// <summary>Sold only when the account would get at least this many cents after fees (0: anything).</summary>
	public int MinSaleCents { get; init; }

	/// <summary>Days an item whose listing was declined isn't listed again - 0 never again.</summary>
	public int DeclinedDays { get; init; } = 30;

	/// <summary>An unsold listing steps down to no lower than this percent of what it sold for.</summary>
	public int FloorPct { get; init; } = 80;

	/// <summary>A cheapest listing this many percent or more under the median is a dump, and doesn't set the price.</summary>
	public int DumpPct { get; init; } = 50;

	public static int Cents(float money) => (int) Math.Round((decimal) money * 100m, MidpointRounding.AwayFromZero);

	public static SpareRules From(BotConfig c) => new() {
		On = c.SpareItems,
		Action = Math.Clamp(c.SpareAction, 0, 3),
		MaxedBadgeCards = c.SpareMaxedBadgeCards,
		ExtraCards = c.SpareExtraCards,
		KeepCopies = Math.Clamp(c.SpareKeepCopies, 0, 10),
		Backgrounds = c.SpareBackgrounds,
		KeepBackgrounds = Math.Clamp(c.SpareKeepBackgrounds, 0, 10),
		Emoticons = c.SpareEmoticons,
		KeepEmoticons = Math.Clamp(c.SpareKeepEmoticons, 0, 10),
		Foils = c.SpareFoils,
		MaxPriceCents = Math.Clamp(Cents(Math.Clamp(c.SpareMaxPrice, 0f, 1_000_000f)), 1, 100_000_000),   // the wallet's own currency: yen, won and rupiah need big numbers
		NewDays = Math.Clamp(c.SpareNewDays, 0, 30),
		KeepNames = Spare.Names(c.SpareKeepItems),
		KeepGames = [.. c.SpareKeepGames],
		MarginPct = Math.Clamp(c.SpareGemMarginPct, 0, 500),
		// A human-mode account does a few a look, spread out; the rest wait for a later look in its day.
		PerRun = c.LegitMode ? Math.Clamp(c.SpareHumanPerLook, 1, 50) : Math.Clamp(c.SparePerRun, 1, 50),
		DailyItems = Math.Clamp(c.SpareDailyItems, 1, 500),
		DailyValueCents = Math.Clamp(Cents(Math.Clamp(c.SpareDailyValue, 0f, 1_000_000f)), 1, 100_000_000),
		PriceMaxAgeHours = Math.Clamp(c.SparePriceMaxAgeHours, 1, 168),
		SellPrice = ListingPrice.Choice(c.SpareSellPrice),
		DumpPct = Math.Clamp(c.SpareDumpPct, 0, 95),
		MinSaleCents = Math.Clamp(Cents(Math.Clamp(c.SpareMinSale, 0f, 1_000_000f)), 0, 100_000_000),
		DeclinedDays = Math.Clamp(c.SpareDeclinedDays, 0, 365),
		FloorPct = Math.Clamp(c.SpareRelistFloorPct, 1, 100)
	};
}

/// <summary>What happens to one item.</summary>
public enum SpareVerdict {
	/// <summary>Turned into gems.</summary>
	Gems,

	/// <summary>Listed on the market.</summary>
	Sell,

	/// <summary>Spare, but kept: worth nothing either way, or "keep everything" is chosen.</summary>
	Keep,

	/// <summary>Not spare at all: needed for a badge, or a copy kept.</summary>
	NotSpare,

	/// <summary>Spare by count, but one of the never-touch rules covers it (new, trade-locked, keep list, in an offer...).</summary>
	Protected,

	/// <summary>Worth more than the price limit: never touched, and never shown - not spare as far as you can tell.</summary>
	OverLimit,

	/// <summary>No known price, or one too old - kept until it's priced.</summary>
	Unpriced
}

/// <param name="PriceCents">What the item is worth in the wallet's currency - the higher of its cheapest listing and its
/// median sale price (0 = not known).</param>
/// <param name="GemValue">Gems it turns into (0 = none, or not known).</param>
/// <param name="ListCents">What the buyer would pay, when it's sold.</param>
/// <param name="ReceiveCents">What the account gets for it after Steam's fees.</param>
public sealed record SpareDecision(SpareItem Item, SpareVerdict Verdict, Said Why, int PriceCents = 0, int GemValue = 0, int ListCents = 0, int ReceiveCents = 0);

/// <summary>Everything one look decided, one decision per item.</summary>
public sealed record SparePlan(List<SpareDecision> Decisions) {
	/// <summary>What it would do, most valuable first - gems and sales only.</summary>
	public List<SpareDecision> Actions => [.. Decisions.Where(static d => d.Verdict is SpareVerdict.Gems or SpareVerdict.Sell).OrderByDescending(static d => d.PriceCents)];

	public int Count(SpareVerdict v) => Decisions.Count(d => d.Verdict == v);
}

/// <summary>What a look knows besides the items: badges, when each item came in, prices and gem values.</summary>
public sealed class SpareInput {
	public required IReadOnlyList<SpareItem> Items { get; init; }
	public required SpareBadges Badges { get; init; }
	public required SpareRules Rules { get; init; }

	/// <summary>When nocat.farm first saw the item - null for one it hasn't seen before now (counted as just received).</summary>
	public required Func<ulong, DateTime?> Received { get; init; }

	/// <summary>Items on the market or in a waiting trade offer.</summary>
	public IReadOnlySet<ulong> Busy { get; init; } = new HashSet<ulong>();

	/// <summary>An item's price in the account's wallet currency - null when it was never priced in it.</summary>
	public Func<SpareItem, MarketPrice?> Price { get; init; } = static _ => null;

	/// <summary>Steam's id of the wallet currency the prices are in - only for saying so ("0.57 CAD"); 0 says nothing.</summary>
	public int Currency { get; init; }

	/// <summary>A sack of 1000 gems' lowest listing, in the wallet's currency.</summary>
	public MarketPrice? Sack { get; init; }

	/// <summary>Gems an item turns into: 0 none, null not known.</summary>
	public Func<SpareItem, int?> GemValue { get; init; } = static _ => null;

	public DateTime Now { get; init; } = DateTime.UtcNow;

	/// <summary>Why an item isn't listed now - its listing was declined on the phone (see <see cref="SpareListings"/>) - or null.</summary>
	public Func<SpareItem, Said?> Declined { get; init; } = static _ => null;

	/// <summary>The step an unsold listing of this item was given when it was taken down to go up cheaper - or null.</summary>
	public Func<SpareItem, SpareListings.Step?> StepOf { get; init; } = static _ => null;
}

/// <summary>
/// Spare items: which Steam community items an account can do without, and whether each is worth more as gems, on the
/// market or not at all. Only decides - <see cref="SpareRun"/> does it.
/// </summary>
/// <remarks>
/// Spare means: cards of a badge already at its top level, card copies beyond what the badge still needs, and backgrounds
/// and emoticons past the copies kept. Never spare, each on its own: anything that came in within the last days, anything
/// on the keep list, foil cards unless switched on, a trade-locked item, anything on the market or in a trade offer, the
/// last copy of anything, anything worth more than the price limit, and anything without a fresh price - it waits for one
/// rather than deciding on a guess.
/// </remarks>
public static partial class Spare {
	/// <summary>The top level of a game's plain badge; its foil badge has one.</summary>
	public const int MaxLevel = 5;

	/// <summary>Item and game names from the keep list, as typed: "Sans, Scout, 440-Heavy".</summary>
	public static HashSet<string> Names(string? list) =>
		new((list ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

	// ── reading the inventory ───────────────────────────────────────────────
	/// <summary>Every item of the Steam inventory, with what kind it is.</summary>
	public static List<SpareItem> Read(InventoryContents inventory) {
		List<SpareItem> items = [];

		foreach (JsonElement asset in inventory.Assets) {
			if ((inventory.DescriptionOf(asset) is not { } d)
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || (id == 0)) {
				continue;
			}

			uint.TryParse(InventoryContents.Text(d, "market_fee_app"), NumberStyles.None, CultureInfo.InvariantCulture, out uint game);
			LearnGameName(game, d);
			uint amount = uint.TryParse(InventoryContents.Text(asset, "amount"), NumberStyles.None, CultureInfo.InvariantCulture, out uint a) ? Math.Max(1, a) : 1;
			(int type, int border) = GooOf(d);

			items.Add(new SpareItem(id, game, KindOf(d), InventoryContents.Text(d, "market_hash_name"), InventoryContents.Text(d, "name"),
				InventoryContents.Text(d, "tradable") == "1", InventoryContents.Text(d, "marketable") == "1", amount, type, border));
		}

		return items;
	}

	/// <summary>
	/// The game's name as the item's own "Game" tag says it ("Spacebase DF-9") - free with the inventory, and there for games
	/// the store no longer lists, which otherwise showed as "app 270310".
	/// </summary>
	internal static void LearnGameName(uint game, JsonElement description) {
		if ((game == 0) || GameNames.IsKnown(game) || !description.TryGetProperty("tags", out JsonElement tags) || (tags.ValueKind != JsonValueKind.Array)) {
			return;
		}

		foreach (JsonElement tag in tags.EnumerateArray()) {
			if ((InventoryContents.Text(tag, "category") == "Game") && (InventoryContents.Text(tag, "internal_name") == $"app_{game}")) {
				string name = InventoryContents.Text(tag, "localized_tag_name");
				GameNames.Learn(game, name.Length > 0 ? name : InventoryContents.Text(tag, "name"));

				return;
			}
		}
	}

	/// <summary>An item's kind, from its item-class tag - or, without tags, from the type line Steam writes for people.</summary>
	public static SpareKind KindOf(JsonElement description) {
		string itemClass = "", border = "";

		if (description.TryGetProperty("tags", out JsonElement tags) && (tags.ValueKind == JsonValueKind.Array)) {
			foreach (JsonElement tag in tags.EnumerateArray()) {
				switch (InventoryContents.Text(tag, "category")) {
					case "item_class":
						itemClass = InventoryContents.Text(tag, "internal_name");

						break;
					case "cardborder":
						border = InventoryContents.Text(tag, "internal_name");

						break;
				}
			}
		}

		string type = InventoryContents.Text(description, "type");
		string name = InventoryContents.Text(description, "name");
		bool sack = name.Equals("Sack of Gems", StringComparison.OrdinalIgnoreCase) || InventoryContents.Text(description, "market_hash_name").EndsWith("-Sack of Gems", StringComparison.Ordinal);

		return itemClass switch {
			"item_class_2" => border == "cardborder_1" || type.Contains("Foil", StringComparison.OrdinalIgnoreCase) ? SpareKind.FoilCard : SpareKind.Card,
			"item_class_3" => SpareKind.Background,
			"item_class_4" => SpareKind.Emoticon,
			"item_class_5" => SpareKind.BoosterPack,
			"item_class_7" => sack ? SpareKind.GemSack : SpareKind.Gems,
			"" => type switch {
				_ when type.EndsWith("Foil Trading Card", StringComparison.OrdinalIgnoreCase) => SpareKind.FoilCard,
				_ when type.EndsWith("Trading Card", StringComparison.OrdinalIgnoreCase) => SpareKind.Card,
				_ when type.EndsWith("Profile Background", StringComparison.OrdinalIgnoreCase) => SpareKind.Background,
				_ when type.EndsWith("Emoticon", StringComparison.OrdinalIgnoreCase) => SpareKind.Emoticon,
				_ when type.Contains("Booster Pack", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Booster Pack", StringComparison.Ordinal) => SpareKind.BoosterPack,
				_ when type.Equals("Steam Gems", StringComparison.OrdinalIgnoreCase) => sack ? SpareKind.GemSack : SpareKind.Gems,
				_ => SpareKind.Other
			},
			_ => SpareKind.Other
		};
	}

	/// <summary>
	/// Steam's own "Turn into gems" action, as the inventory page reads it: <c>GetGooValue( contextid, assetid, appid,
	/// item_type, border_color )</c> in the item's owner actions. (0, 0) when the item has none.
	/// </summary>
	public static (int Type, int Border) GooOf(JsonElement description) {
		if (!description.TryGetProperty("owner_actions", out JsonElement actions) || (actions.ValueKind != JsonValueKind.Array)) {
			return (0, 0);
		}

		foreach (JsonElement action in actions.EnumerateArray()) {
			Match m = GooAction().Match(InventoryContents.Text(action, "link"));

			if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int type)
				&& int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int border)) {
				return (type, border);
			}
		}

		return (0, 0);
	}

	[GeneratedRegex(@"GetGooValue\(\s*'?[^,]*'?\s*,\s*'?[^,]*'?\s*,\s*'?[0-9]+'?\s*,\s*'?([0-9]+)'?\s*,\s*'?([0-9]+)'?")]
	private static partial Regex GooAction();

	// ── deciding ─────────────────────────────────────────────────────────────
	/// <summary>How many copies of this kind of item to keep, and whether its extras count as spare at all.</summary>
	private static (int Keep, Said? NotSpare) Keeping(SpareItem item, SpareInput input) {
		SpareRules r = input.Rules;

		switch (item.Kind) {
			case SpareKind.Card: {
				int level = input.Badges.Levels.GetValueOrDefault(item.Game);

				if (level >= MaxLevel) {
					return r.MaxedBadgeCards ? (r.KeepCopies, null) : (int.MaxValue, new Said("cards from finished badges are switched off"));
				}

				return r.ExtraCards ? (Math.Max(r.KeepCopies, MaxLevel - level), null) : (int.MaxValue, new Said("extra card copies are switched off"));
			}

			case SpareKind.FoilCard: {
				bool done = input.Badges.FoilLevels.GetValueOrDefault(item.Game) >= 1;

				if (done ? !r.MaxedBadgeCards : !r.ExtraCards) {
					return (int.MaxValue, done ? new Said("cards from finished badges are switched off") : new Said("extra card copies are switched off"));
				}

				return (done ? r.KeepCopies : Math.Max(r.KeepCopies, 1), null);
			}

			case SpareKind.Background:
				return r.Backgrounds ? (r.KeepBackgrounds, null) : (int.MaxValue, new Said("duplicate backgrounds are switched off"));
			case SpareKind.Emoticon:
				return r.Emoticons ? (r.KeepEmoticons, null) : (int.MaxValue, new Said("duplicate emoticons are switched off"));
			default:
				return (int.MaxValue, new Said("not a card, background or emoticon"));
		}
	}

	/// <summary>A never-touch rule that covers this one copy, or null.</summary>
	private static Said? Protection(SpareItem item, SpareInput input) {
		SpareRules r = input.Rules;

		if (input.Busy.Contains(item.AssetId)) {
			return new Said("on the market or in a trade offer");
		}

		if (r.KeepGames.Contains(item.Game) || r.KeepNames.Contains(item.Name) || r.KeepNames.Contains(item.Hash)) {
			return new Said("on your keep list");
		}

		if ((item.Kind == SpareKind.FoilCard) && !r.Foils) {
			return new Said("a foil card - foils are switched off");
		}

		if (!item.Tradable) {
			return new Said("trade-locked");
		}

		if (item.Amount != 1) {
			return new Said("a stack, not one item");
		}

		if (r.NewDays > 0) {
			DateTime? came = input.Received(item.AssetId);

			if ((came == null) || (input.Now - came.Value < TimeSpan.FromDays(r.NewDays))) {
				return new Said("came in within the last {0} day(s)", r.NewDays);
			}
		}

		return null;
	}

	/// <summary>Decide every card, background and emoticon. Each item gets exactly one decision.</summary>
	public static SparePlan Plan(SpareInput input) {
		List<SpareDecision> decisions = [];
		SpareRules r = input.Rules;

		// One group per kind of item: the copies of a card, of a background.
		foreach (IGrouping<(SpareKind Kind, string Hash), SpareItem> group in input.Items
			.Where(static i => i.Kind is SpareKind.Card or SpareKind.FoilCard or SpareKind.Background or SpareKind.Emoticon)
			.GroupBy(static i => (i.Kind, i.Hash.Length > 0 ? i.Hash : $"{i.Game}-{i.Name}"))) {
			List<SpareItem> copies = [.. group.OrderBy(static i => i.AssetId)];
			(int keep, Said? notSpare) = Keeping(copies[0], input);

			if (notSpare is { } why) {
				decisions.AddRange(copies.Select(c => new SpareDecision(c, SpareVerdict.NotSpare, why)));

				continue;
			}

			// "Keep this many" can be 0: then the last copy of a finished badge's card, a background or an emoticon may go too.
			// A card the badge still needs is always kept - Keeping counts those whatever the setting says.
			keep = Math.Max(0, keep);

			// What's leaving anyway (on the market, in an offer) isn't a copy that stays - it's protected, and doesn't count.
			List<SpareItem> staying = [.. copies.Where(c => !input.Busy.Contains(c.AssetId))];
			List<(SpareItem Item, Said Why)> covered = [.. staying.Select(c => (c, Protection(c, input))).Where(static p => p.Item2 != null).Select(static p => (p.c, p.Item2!.Value))];
			List<SpareItem> free = [.. staying.Where(c => covered.TrueForAll(p => p.Item.AssetId != c.AssetId))];

			foreach (SpareItem busy in copies.Where(c => input.Busy.Contains(c.AssetId))) {
				decisions.Add(new SpareDecision(busy, SpareVerdict.Protected, new Said("on the market or in a trade offer")));
			}

			// Protected copies are kept first - they stay anyway - and the copies to keep that are left come from the free ones,
			// newest kept. Free copies past that are spare.
			int keepFromFree = Math.Max(0, keep - covered.Count);
			int spareCount = Math.Max(0, free.Count - keepFromFree);

			// With no more copies than are kept, a copy that's also new (say) was never spare in the first place.
			bool extras = staying.Count > keep;

			foreach ((SpareItem item, Said rule) in covered) {
				decisions.Add(extras ? new SpareDecision(item, SpareVerdict.Protected, rule) : new SpareDecision(item, SpareVerdict.NotSpare, new Said("a copy you keep")));
			}

			for (int i = 0; i < free.Count; i++) {
				SpareItem item = free[i];

				if (i < spareCount) {
					decisions.Add(Choose(item, input));
				} else {
					decisions.Add(new SpareDecision(item, SpareVerdict.NotSpare, item.Kind is SpareKind.Card or SpareKind.FoilCard && (keep > r.KeepCopies)
						? new Said("needed for the badge")
						: new Said("a copy you keep")));
				}
			}
		}

		return new SparePlan(decisions);
	}

	/// <summary>A spare item: gems, sell or keep - or wait, when its price isn't known.</summary>
	private static SpareDecision Choose(SpareItem item, SpareInput input) {
		SpareRules r = input.Rules;
		TimeSpan maxAge = TimeSpan.FromHours(r.PriceMaxAgeHours);

		if ((input.Price(item) is not { } price) || (input.Now - price.At > maxAge)) {
			return new SpareDecision(item, SpareVerdict.Unpriced, new Said("no fresh price yet - kept until it has one"));
		}

		// Nobody sells it: it can't be sold, but gems don't need a market - where gems are allowed (best of both, or gems
		// only), it becomes gems. Live, 2026-10-13: 358 of one account's spare items sat "kept" for this alone.
		if (price.Cents <= 0) {
			if ((r.Action is 0 or 1) && (item.GooType > 0)) {
				int? goo = input.GemValue(item);

				return goo is null ? new SpareDecision(item, SpareVerdict.Unpriced, new Said("its gem value isn't known yet - kept"))
					: goo > 0 ? new SpareDecision(item, SpareVerdict.Gems, new Said("{0} gems - nobody sells it on the market", goo), 0, goo.Value, 0, 0)
					: new SpareDecision(item, SpareVerdict.Unpriced, new Said("nobody sells it, so there's no price - kept"));
			}

			return new SpareDecision(item, SpareVerdict.Unpriced, new Said("nobody sells it, so there's no price - kept"));
		}

		// Worth: the higher of the cheapest listing and what it really sold for - in the wallet's own currency, the same
		// money as the limit.
		int worth = ListingPrice.Worth(price);
		string Cash(decimal cents) => Money(cents, input.Currency);

		if (worth > r.MaxPriceCents) {
			return new SpareDecision(item, SpareVerdict.OverLimit, new Said("worth {0} - over your price limit, never touched", Cash(worth)), worth);
		}

		(int? listed, Said noPrice) = ListingPrice.For(price, r.SellPrice, r.DumpPct);

		// An unsold listing taken down to go up cheaper: at the step it was given, while the market hasn't gone up since, and
		// never under the floor.
		if ((listed is int rule) && (input.StepOf(item) is { } step) && (SpareListings.Capped(rule, price, step, r.FloorPct) is int capped)) {
			listed = capped;
		}

		int list = listed ?? 0;
		int receive = item.Marketable && (listed != null) ? Seller.ReceiveFor(list) : 0;
		int? gems = item.GooType > 0 ? input.GemValue(item) : 0;
		bool sackKnown = (input.Sack is { } sack) && (sack.Cents > 0) && (input.Now - sack.At <= maxAge);
		Said? declined = receive > 0 ? input.Declined(item) : null;
		bool tooLittle = (receive > 0) && (receive < r.MinSaleCents);

		SpareDecision Gems(Said why) => new(item, SpareVerdict.Gems, why, worth, gems ?? 0, 0, 0);
		SpareDecision Sell(Said why) => new(item, SpareVerdict.Sell, why, worth, gems ?? 0, list, receive);
		SpareDecision Keep(Said why) => new(item, SpareVerdict.Keep, why, worth, gems ?? 0);
		SpareDecision NoGemValue() => new(item, SpareVerdict.Unpriced, new Said("its gem value isn't known yet - kept"), worth);
		Said Little() => new("you'd get {0} - under your {1} minimum", Cash(receive), Cash(r.MinSaleCents));

		// Sold, or weighed against gems, only at a price the market really pays: with none for certain, it waits.
		if ((listed == null) && item.Marketable && (r.Action is 0 or 2)) {
			return new SpareDecision(item, SpareVerdict.Unpriced, noPrice, worth);
		}

		switch (r.Action) {
			case 3:
				return Keep(new Said("you chose to keep everything - only reported"));
			case 1:
				return gems is null ? NoGemValue() : gems > 0 ? Gems(new Said("{0} gems", gems)) : Keep(new Said("it doesn't turn into gems"));
			case 2:
				return declined is { } no ? Keep(no)
					: tooLittle ? Keep(Little())
					: receive > 0 ? Sell(new Said("sells for {0}, you get {1}", Cash(list), Cash(receive))) : Keep(new Said("the market pays nothing for it"));
		}

		// Selling would pay under the minimum: gems when they're worth anything, kept when they're not.
		if (tooLittle && (declined == null)) {
			return gems is null ? NoGemValue()
				: gems > 0 ? Gems(new Said("{0} gems - selling would pay {1}, under your {2} minimum", gems, Cash(receive), Cash(r.MinSaleCents)))
				: Keep(Little());
		}

		SpareDecision best = Best();

		// Its listing was declined on the phone: not listed again for now - turned into gems only where gems won anyway.
		return (declined is { } why) && (best.Verdict != SpareVerdict.Gems) ? Keep(why) : best;

		// Best of the two.
		SpareDecision Best() {
			if (gems is null) {
				return receive > 0 ? NoGemValue() : Keep(new Said("worth nothing on the market, gem value not known"));
			}

			if (gems == 0) {
				return receive > 0 ? Sell(new Said("sells for {0}, you get {1} - no gems for it", Cash(list), Cash(receive))) : Keep(new Said("worth nothing as gems or on the market"));
			}

			if (receive == 0) {
				return Gems(new Said("{0} gems - the market pays nothing for it", gems));
			}

			if (!sackKnown) {
				return new SpareDecision(item, SpareVerdict.Unpriced, new Said("the price of gems isn't known yet - kept"), worth);
			}

			decimal gemsWorth = GemsWorth(gems.Value, input.Sack!.Value.Cents);

			return GemsWin(gemsWorth, receive, r.MarginPct)
				? Gems(new Said("{0} gems, worth {1} - beats selling ({2}) by {3}% or more", gems, Cash(gemsWorth), Cash(receive), r.MarginPct))
				: Sell(new Said("sells for {0}, you get {1} - more than its {2} gems are worth ({3})", Cash(list), Cash(receive), gems, Cash(gemsWorth)));
		}
	}

	/// <summary>What some gems are worth: their share of a sack of 1000, in cents.</summary>
	public static decimal GemsWorth(int gems, int sackCents) => gems * (decimal) sackCents / 1000m;

	/// <summary>Gems beat selling: worth at least the margin more than the market pays after fees.</summary>
	public static bool GemsWin(decimal gemsWorth, int receiveCents, int marginPct) => gemsWorth >= receiveCents * (1m + (marginPct / 100m));

	/// <summary>Cents as money, "0.25" - the account's own currency, whichever it is.</summary>
	public static string Money(decimal cents) {
		return (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// US-dollar cents, labelled so: "US$0.57". For the market search's figures, which are always US dollars whatever the
	/// wallet - a pack and its cards compared from that one list, never mistaken for the wallet's own money.
	/// </summary>
	public static string UsMoney(decimal cents) => "US$" + Money(cents);

	/// <summary>Cents as money with the wallet's currency after it, "0.57 CAD" - plain "0.57" when the currency isn't known.</summary>
	public static string Money(decimal cents, int currency) {
		return MarketQuotes.Code(currency) is { Length: > 0 } code ? Money(cents) + " " + code : Money(cents);
	}

	// ── booster packs ────────────────────────────────────────────────────────
	/// <summary>One sealed pack, and whether to open it.</summary>
	/// <param name="Disposable">Kept for a reason the "packs it doesn't open" setting covers (worth more sealed, never-open
	/// game, opening off) - not because it's new, busy or unpriced.</param>
	public sealed record PackDecision(SpareItem Pack, bool Open, Said Why, int PackCents = 0, int CardsCents = 0, bool Disposable = false);

	/// <summary>A game's prices for deciding about its packs: the pack's own, and every plain card of the set.</summary>
	public sealed record PackPrices(MarketPrice? Pack, IReadOnlyList<int> Cards, DateTime At);

	/// <param name="choice">0 never, 1 only when the cards are worth more than the pack, 2 always.</param>
	/// <param name="madeHere">Packs nocat.farm made itself (BoosterPick.Memory.MadeIds): never held back as new - "Leave new
	/// packs alone" is for packs that came in other ways, drops and trades. One made to level a badge waited 7 days.</param>
	/// <param name="own">Packs automatic "Make booster packs" made to level a badge and still has to open: opened at the next
	/// chance, whatever "Open booster packs" says - only one on the market or in a trade offer is left.</param>
	public static List<PackDecision> PlanPacks(IReadOnlyList<SpareItem> items, int choice, int newDays, IReadOnlyCollection<uint> neverGames,
		Func<ulong, DateTime?> received, IReadOnlySet<ulong> busy, Func<uint, PackPrices?> prices, int maxAgeHours, DateTime now,
		IReadOnlySet<ulong>? madeHere = null, IReadOnlySet<ulong>? own = null) {
		List<PackDecision> found = [];

		foreach (SpareItem pack in items.Where(static i => i.Kind == SpareKind.BoosterPack).OrderBy(static i => i.AssetId)) {
			if (busy.Contains(pack.AssetId)) {
				found.Add(new PackDecision(pack, false, new Said("on the market or in a trade offer")));

				continue;
			}

			if (own?.Contains(pack.AssetId) == true) {
				found.Add(new PackDecision(pack, true, new Said("made to level the {0} badge", GameNames.Of(pack.Game))));

				continue;
			}

			if ((newDays > 0) && (madeHere?.Contains(pack.AssetId) != true)) {
				DateTime? came = received(pack.AssetId);

				if ((came == null) || (now - came.Value < TimeSpan.FromDays(newDays))) {
					found.Add(new PackDecision(pack, false, new Said("came in within the last {0} day(s)", newDays)));

					continue;
				}
			}

			if (neverGames.Contains(pack.Game)) {
				found.Add(new PackDecision(pack, false, new Said("its game is on the never-open list"), Disposable: true));

				continue;
			}

			if (choice <= 0) {
				found.Add(new PackDecision(pack, false, new Said("opening booster packs is off"), Disposable: true));

				continue;
			}

			if (choice >= 2) {
				found.Add(new PackDecision(pack, true, new Said("set to always open")));

				continue;
			}

			PackPrices? p = prices(pack.Game);

			if ((p == null) || (now - p.At > TimeSpan.FromHours(maxAgeHours)) || (p.Pack is not { Cents: > 0 } packPrice) || (p.Cards.Count == 0) || p.Cards.Any(static c => c <= 0)) {
				found.Add(new PackDecision(pack, false, new Said("no fresh price for the pack or its cards yet - kept until there is")));

				continue;
			}

			// Three cards out of the set, each as likely as the next: three times the average card. Foils are rare enough to
			// leave out, so this never opens a pack on the hope of one. Both sides after Steam's fees - what you'd get.
			int cards = (int) Math.Round(3m * (decimal) p.Cards.Average(static c => Seller.ReceiveFor(c)), MidpointRounding.ToZero);
			int sealedValue = Seller.ReceiveFor(packPrice.Cents);

			found.Add(cards > sealedValue
				? new PackDecision(pack, true, new Said("its cards are worth about {0}, the pack {1}", UsMoney(cards), UsMoney(sealedValue)), sealedValue, cards)
				: new PackDecision(pack, false, new Said("worth more sealed: the pack {0}, its cards about {1}", UsMoney(sealedValue), UsMoney(cards)), sealedValue, cards, Disposable: true));
		}

		return found;
	}
}
