using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>
/// Selling duplicate trading cards on the market - the copies an account can't use, a few at a time, just under
/// the cheapest listing, with Steam's fees worked out so the price you're told is the price you get.
/// </summary>
/// <remarks>
/// What counts as a duplicate: with a game's whole set in hand, it keeps as many copies of each card as badge
/// levels it can still craft; with only part of the set it keeps one of each, so the set can still finish; once the
/// game's badge is maxed nothing is kept. Foils are never sold. Listings need confirming on the phone - with the
/// account's authenticator secrets loaded that happens by itself, otherwise the Steam app shows them.
/// </remarks>
public static class Seller {

	/// <summary>A price asked because a listing waits on it: it may use the hour's reserve (<see cref="PriceBook.ForListing"/>).</summary>
	private static async Task<MarketQuote?> FetchForListingAsync(string hash, int currency, CancellationToken ct) {
		PriceBook.ForListing.Value = true;

		try {
			return await MarketQuotes.FetchAsync(hash, currency, ct).ConfigureAwait(false);
		} finally {
			PriceBook.ForListing.Value = false;
		}
	}
	private const uint SteamApp = 753;
	private const int MaxBadgeLevel = 5;

	/// <summary>The market's floor: 3 cents to the buyer, of which the seller gets 1.</summary>
	private const int MinBuyerCents = 3;

	/// <summary>Games to price per run - each is a market request or two.</summary>
	private const int MaxLookups = 8;

	/// <param name="Hash">The card's market name ("391540-Sans") - what a listing is recognised by once Steam has given the
	/// card a new asset id.</param>
	public sealed record Offer(ulong AssetId, string Game, string Card, int LowestCents, int BuyerCents, int YouGetCents, string Hash = "");

	/// <param name="Gems">1.8.2: each listed card's gems by asset id (null: its gem value isn't known) - what 'sell do' weighs selling against.</param>
	public sealed record Plan(List<Offer> Offers, int Duplicates, int Unpriced, string? Problem, IReadOnlyDictionary<ulong, int?>? Gems = null);

	/// <summary>Steam's cut on top of what the seller receives: 5% for Steam, 10% for the game, each at least a cent.</summary>
	public static int FeesFor(int receive) => Math.Max(1, receive * 5 / 100) + Math.Max(1, receive * 10 / 100);

	/// <summary>The most the seller can receive while the buyer pays no more than <paramref name="buyer"/>. 0 if nothing fits.</summary>
	public static int ReceiveFor(int buyer) {
		for (int receive = buyer - 2; receive >= 1; receive--) {
			if (receive + FeesFor(receive) <= buyer) {
				return receive;
			}
		}

		return 0;
	}

	/// <summary>A cent under the cheapest listing, never below the market's floor.</summary>
	public static int UndercutFor(int lowest) => lowest > MinBuyerCents ? lowest - 1 : MinBuyerCents;

	// ── deciding what to sell ────────────────────────────────────────────────
	/// <summary>
	/// The account's wallet currency, as Steam's currency id - a listing's price is read in it, so prices have to be
	/// looked up in it too. Null until Steam has said (just after signing in).
	/// </summary>
	private static int? WalletCurrency(Bot bot) => bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? null : (int) bot.WalletCurrency;

	/// <summary>Cents in the account's own wallet currency, e.g. "0.05 CAD".</summary>
	public static string Money(int cents, Bot bot) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture)
		+ (bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? "" : " " + bot.WalletCurrency);

	/// <summary>
	/// Copies of each card 'sell' keeps: none of a finished badge's, every level's worth of a set it holds whole - or will,
	/// with buying cards on (it buys the missing ones) - and otherwise one.
	/// </summary>
	public static int KeepCopies(int levelsLeft, bool wholeSet, bool buying) => levelsLeft <= 0 ? 0 : wholeSet || buying ? levelsLeft : 1;

	/// <param name="read">1.8.2: hears the inventory once it's read whole - so a preview after it uses the same read, not a second one.</param>
	public static async Task<Plan> PlanAsync(Bot bot, int max, CancellationToken ct, Action<InventoryContents>? read = null) {
		// Priced in US dollars and listed on a Canadian wallet, a card went up about a quarter cheaper than meant.
		if (WalletCurrency(bot) is not int currency) {
			return new Plan([], 0, 0, "Steam hasn't said this account's wallet currency yet - try again in a minute");
		}

		if (await LevelPlanner.BadgesAsync(bot, ct).ConfigureAwait(false) is not { } badges) {
			return new Plan([], 0, 0, "Steam wouldn't say what its badges are");
		}

		InventoryContents? inventory = await Inventory.ReadAsync(bot, SteamApp, "6", ct).ConfigureAwait(false);

		if (inventory == null) {
			return new Plan([], 0, 0, "couldn't read its inventory");
		}

		// Half an inventory is worse than none. With a page missing, the cards on it don't count towards a set, so
		// a set it could finish looks hopeless and its cards get listed as spares. Matching refuses the same way.
		if (!inventory.Complete) {
			return new Plan([], 0, 0, "couldn't read all of its inventory - Steam stopped part way; try again in a while");
		}

		read?.Invoke(inventory);

		// Cards promised in a trade that's still waiting aren't this account's to sell - and don't count towards its sets
		// either, since they're about to leave. Not knowing is a reason to wait, not to guess: listing one breaks the trade.
		if (await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is not { } waiting) {
			return new Plan([], 0, 0, "Steam wouldn't say which cards are in a waiting trade - try again in a while");
		}

		// 1.8.2: nor are its cards promised to a card swap nocat.lol paired.
		HashSet<ulong> promised = CardExchange.NotForSale(bot, waiting, DateTime.UtcNow);

		// game -> card -> the copies held (asset ids), marketable normal cards only
		Dictionary<uint, Dictionary<string, List<ulong>>> cards = [];

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			if (!type.EndsWith("Trading Card", StringComparison.OrdinalIgnoreCase) || type.Contains("Foil", StringComparison.OrdinalIgnoreCase)
				|| (InventoryContents.Text(d, "marketable") != "1")
				|| !uint.TryParse(InventoryContents.Text(d, "market_fee_app"), out uint game) || (game == 0)
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), out ulong id) || promised.Contains(id)) {
				continue;
			}

			string hash = InventoryContents.Text(d, "market_hash_name");

			if (!cards.TryGetValue(game, out Dictionary<string, List<ulong>>? held)) {
				cards[game] = held = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
			}

			if (!held.TryGetValue(hash, out List<ulong>? copies)) {
				held[hash] = copies = [];
			}

			copies.Add(id);
		}

		List<Offer> offers = [];
		int duplicates = 0;
		int unpriced = 0;
		int lookups = 0;

		// 1.8.2: with buying cards on, a set it's missing cards of will be finished by buying them, so its copies are kept for
		// the levels still to come; a game it just bought cards of keeps them all until they're crafted.
		bool buying = Modules.CardBuyer.On(bot);
		HashSet<uint> bought = CardExchange.BoughtGames(bot.Name, DateTime.UtcNow);

		// The games with the most spare copies first - that's where a price lookup sells the most.
		foreach ((uint game, Dictionary<string, List<ulong>> held) in cards.OrderByDescending(static c => c.Value.Values.Sum(static v => v.Count - 1))) {
			int levelsLeft = MaxBadgeLevel - badges.GameLevels.GetValueOrDefault(game);
			int spare = held.Values.Sum(v => Math.Max(0, v.Count - Math.Max(1, levelsLeft)));

			// Bought for a badge, or nothing spare even on the generous reading - no need to ask the market.
			if (bought.Contains(game) || ((levelsLeft > 0) && (spare == 0))) {
				continue;
			}

			if (lookups >= MaxLookups) {
				unpriced += spare;

				continue;
			}

			(LevelPlanner.SetPrice? set, bool asked) = await LevelPlanner.PriceAsync(bot, game, ct).ConfigureAwait(false);
			lookups += asked ? 1 : 0;

			if (set == null) {
				unpriced += spare;

				continue;
			}

			bool wholeSet = (set.Size > 0) && set.Cards.Keys.All(held.ContainsKey);
			int keep = KeepCopies(levelsLeft, wholeSet, buying);

			foreach ((string hash, List<ulong> copies) in held) {
				foreach (ulong asset in copies.Skip(keep)) {
					duplicates++;
					int lowest = set.Cards.GetValueOrDefault(hash);

					if (lowest <= 0) {
						unpriced++;   // nobody is selling it, so there's no price to go under

						continue;
					}

					// A shortlist only: the search answers in US dollars whatever currency is asked for. The price that's
					// listed is asked for below, in the wallet's own currency.
					offers.Add(new Offer(asset, set.Game, CardName(hash), lowest, 0, 0, hash));
				}
			}
		}

		// 1.8.2: never a card of a game - or a card - on the keep list.
		SpareRules keepList = SpareRules.From(bot.Cfg);
		offers.RemoveAll(o => OnKeepList(o, keepList));

		// 1.8.2: what each card's gems are worth, so a sale never pays less than its gems - each game's gem value asked at
		// most once, a few games a look; one not known keeps its cards. And a sack's price to value gems by.
		Dictionary<ulong, SpareItem> items = [];

		foreach (SpareItem i in Spare.Read(inventory)) {
			items[i.AssetId] = i;
		}

		Dictionary<string, int?> gooOf = new(StringComparer.Ordinal);
		Dictionary<ulong, int?> gems = [];
		LiveSpareSteam steam = new(bot);

		foreach (Offer o in offers.OrderByDescending(static o => o.LowestCents)) {
			if (!items.TryGetValue(o.AssetId, out SpareItem? item)) {
				gems[o.AssetId] = null;

				continue;
			}

			if (!gooOf.TryGetValue(item.GooKey, out int? goo)) {
				goo = SteamGems.KnownValue(item) ?? (gooOf.Count < MaxLookups ? await steam.GemValueAsync(item, ct).ConfigureAwait(false) : null);
				gooOf[item.GooKey] = goo;
			}

			gems[o.AssetId] = goo;
		}

		if ((gems.Values.Any(static g => g > 0)) && Stale(SparePrices.Sack(currency), keepList)) {
			await SparePrices.PriceSackAsync(currency, ct).ConfigureAwait(false);
		}

		// Most valuable first: if only a few go today, they should be the ones worth the most. Each priced in the wallet's own
		// currency, by the account's selling price rule - one market request a card, so only as many as are listed.
		List<Offer> priced = [];
		Reprice rule = OwnRule(bot, currency, gems);
		Dictionary<string, Offer?> byCard = new(StringComparer.Ordinal);

		foreach (Offer o in offers.OrderByDescending(static o => o.LowestCents)) {
			if (priced.Count >= max) {
				break;
			}

			if (!byCard.TryGetValue(o.Hash, out Offer? same)) {
				(same, Said why) = await rule(o, ct).ConfigureAwait(false);
				byCard[o.Hash] = same;

				if (same == null) {
					Log.Debug(KeptSaid(o, why), bot.Name);
				}
			}

			if (same == null) {
				unpriced++;

				continue;
			}

			priced.Add(o with { LowestCents = same.LowestCents, BuyerCents = same.BuyerCents, YouGetCents = same.YouGetCents });
		}

		return new Plan([.. priced.OrderByDescending(static o => o.YouGetCents)], duplicates, unpriced, null, gems);
	}

	/// <summary>A price too old to go by ("Trust a price for"), or none.</summary>
	private static bool Stale(MarketPrice? price, SpareRules rules) => (price is not { Cents: > 0 } p) || (DateTime.UtcNow - p.At > TimeSpan.FromHours(rules.PriceMaxAgeHours));

	/// <summary>"391540-Sans" is the card "Sans".</summary>
	internal static string CardName(string hash) => hash.IndexOf('-') is int dash and > 0 ? hash[(dash + 1)..] : hash;

	/// <summary>"391540-Sans" is an item of game 391540 (0: no game in its name).</summary>
	internal static uint GameOf(string hash) => (hash.IndexOf('-') is int dash and > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) ? app : 0;

	/// <summary>1.8.2: an item on the keep list - its game ("Never touch items from these games"), or itself by name.</summary>
	internal static bool OnKeepList(Offer offer, SpareRules rules) =>
		((GameOf(offer.Hash) is uint app and > 0) && rules.KeepGames.Contains(app)) || rules.KeepNames.Contains(offer.Card) || ((offer.Hash.Length > 0) && rules.KeepNames.Contains(offer.Hash));

	// ── listing ──────────────────────────────────────────────────────────────
	/// <summary>
	/// Right before one listing goes up: the offer at today's price, in the account's own wallet currency - or null, with
	/// why it's kept instead.
	/// </summary>
	public delegate Task<(Offer? Offer, Said Why)> Reprice(Offer offer, CancellationToken ct);

	/// <summary>Why a listing didn't go up after all: it's worth more than the price limit by now - never sold.</summary>
	private const string OverLimitWhy = "worth {0} now - over your price limit, not listed";

	/// <summary>Why a listing didn't go up after all: its price went up since it was counted, past what today's value limit has left.</summary>
	public const string WentUpWhy = "price went up to {0} · over today's value limit, not listed";

	/// <summary>Whether a reason from <see cref="RepriceAsync"/> is the price limit - said only in the debug log.</summary>
	public static bool OverLimit(Said why) => why.English == OverLimitWhy;

	/// <summary>
	/// Price one offer again from the market, in <paramref name="currency"/> (the account's own wallet currency), as the
	/// selling price setting says (<see cref="ListingPrice"/>). Kept - null - when the market doesn't answer, the price
	/// can't be told for certain, it's now worth more than <paramref name="maxWorth"/>, or <paramref name="fits"/> says its
	/// worth no longer fits what was counted for it.
	/// </summary>
	/// <param name="step">The step an unsold listing of ours was given: it goes up at that, while today's price is still at or
	/// under what the listing had - never under <paramref name="floorPct"/> percent of the real price (see <see cref="SpareListings.Capped"/>).</param>
	/// <param name="minReceive">Kept when the account would get less than this ("Only sell if you get at least").</param>
	/// <param name="stepGone">Called when the market went up since the step: the step is stale and goes.</param>
	/// <param name="ours">What a buyer pays for our own cheapest listing of this item still up, 0 none: "a cent under the
	/// cheapest" never goes under it when it's the cheapest (see <see cref="ListingPrice.UnderCheapest"/>).</param>
	/// <param name="gemsWorth">1.8.2: what the item's gems are worth (null: not weighed against gems) - kept when selling at
	/// today's price wouldn't pay at least a cent more (<see cref="Spare.SellingPays"/>); the next look turns it into gems.</param>
	public static async Task<(Offer? Offer, Said Why)> RepriceAsync(Offer offer, int currency, int strategy, int dumpPct, int? maxWorth,
		Func<int, bool>? fits, CancellationToken ct, SpareListings.Step? step = null, int minReceive = 0, int floorPct = 80, Action? stepGone = null, int ours = 0,
		decimal? gemsWorth = null) {
		if ((currency <= 0) || (offer.Hash.Length == 0)) {
			return (null, new Said("no price in this account's currency, not listed"));
		}

		if (await FetchForListingAsync(offer.Hash, currency, ct).ConfigureAwait(false) is not { } quote) {
			return (null, new Said("no price yet, tries later"));
		}

		(int? list, Said why) = ListingPrice.For(quote.Price, strategy, dumpPct, minReceive, floorPct, ours);

		if (list is not int buyer) {
			return (null, why);
		}

		int worth = ListingPrice.Worth(quote.Price);

		if ((maxWorth is int max) && (worth > max)) {
			return (null, new Said(OverLimitWhy, Spare.Money(worth, currency)));
		}

		if (step is { } s) {
			if (SpareListings.Capped(buyer, quote.Price, s, floorPct) is int capped) {
				buyer = capped;
			} else {
				stepGone?.Invoke();
			}
		}

		int receive = ReceiveFor(buyer);

		if ((receive > 0) && (receive < minReceive)) {
			return (null, new Said("not listed: you'd get {0}, under your {1} minimum", Spare.Money(receive, currency), Spare.Money(minReceive, currency)));
		}

		// 1.8.2, gems never lose money: at today's price, selling has to pay at least a cent more than the gems are worth.
		if ((receive > 0) && (gemsWorth is decimal gems) && !Spare.SellingPays(gems, receive)) {
			return (null, new Said("not listed: you'd get {0}, not more than its gems are worth ({1})", Spare.Money(receive, currency), Spare.Money(gems, currency)));
		}

		// Last, once nothing else keeps it: a price that went up since it was counted is fine while today's value limit
		// still has room for it (and that room is taken now) - Starry Background 1, counted at 0.16, was kept at 0.23.
		if ((receive > 0) && (fits?.Invoke(worth) == false)) {
			return (null, new Said(WentUpWhy, Spare.Money(worth, currency)));
		}

		// 1.8.2: what a buyer is really charged - what the account gets and Steam's fees on it, which can be a cent under the
		// price aimed at - so "never under our own listing" goes by what's really up.
		return receive > 0 ? (offer with { LowestCents = quote.Lowest ?? 0, BuyerCents = receive + FeesFor(receive), YouGetCents = receive }, default)
			: (null, new Said("the market pays nothing for it"));
	}

	/// <summary>"Starry Background 1 (Waveform): no price yet, tries later" - an item it didn't list after all, and why.</summary>
	public static Said KeptSaid(Offer offer, Said why) => new("{0} ({1}): {2}", offer.Card, offer.Game, why);

	/// <summary>"listed Scout (Team Fortress 2) for 0.15 CAD · you get 0.13 CAD" - each listing as it goes up.</summary>
	public static Said ListedSaid(Offer offer, string price, string youGet) => new("listed {0} ({1}) for {2} · you get {3}", offer.Card, offer.Game, price, youGet);

	/// <summary>
	/// The account's own selling price rule, for anything that has no other: 'sell'. 1.8.2: by the spare items limits too -
	/// never anything worth more than "Never touch items worth more than", never for less than "Only sell if you get at
	/// least", and - with <paramref name="gems"/> - never for less than a cent more than its gems are worth (a card whose gem
	/// value or the sack's price isn't known is kept).
	/// </summary>
	public static Reprice OwnRule(Bot bot, int currency, IReadOnlyDictionary<ulong, int?>? gems = null) {
		int strategy = ListingPrice.Choice(bot.Cfg.SpareSellPrice), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);
		int floorPct = Math.Clamp(bot.Cfg.SpareRelistFloorPct, 1, 100);
		SpareRules rules = SpareRules.From(bot.Cfg);

		return (o, ct) => {
			decimal? gemsWorth = null;

			if (gems != null) {
				if (!gems.TryGetValue(o.AssetId, out int? goo) || (goo is null)) {
					return Task.FromResult<(Offer?, Said)>((null, new Said("its gem value isn't known yet - kept")));
				}

				if (goo > 0) {
					if (Stale(SparePrices.Sack(currency), rules)) {
						return Task.FromResult<(Offer?, Said)>((null, new Said("the price of gems isn't known yet - kept")));
					}

					gemsWorth = Spare.GemsWorth(goo.Value, SparePrices.SackPays(currency, rules.DumpPct)!.Value.Cents);
				}
			}

			return RepriceAsync(o, currency, strategy, dump, rules.MaxPriceCents, null, ct, minReceive: rules.MinSaleCents, floorPct: floorPct,
				ours: OurListings.UpFor(OurListings.Load(bot.Name), o.Hash), gemsWorth: gemsWorth);
		};
	}

	/// <summary>List them, a little apart, then confirm them if this account can. Returns a summary.</summary>
	/// <param name="mayAct">Asked before each listing: false (a human-mode account's day ended, or its owner started
	/// playing) stops the rest. None: always.</param>
	/// <param name="reprice">Each offer priced again just before it goes up; the account's own rule when not given.</param>
	/// <param name="hold">Asked before each listing too: a reason to stop (spare items put back to preview only), or null.</param>
	/// <param name="pause">The wait between two listings: 20 seconds to a minute when not given (a human-mode account's
	/// spare items wait minutes).</param>
	/// <param name="refused">1.8.2: told Steam's reason when it refused a listing ("" when it gave none).</param>
	public static async Task<string> SellAsync(Bot bot, IReadOnlyList<Offer> offers, CancellationToken ct, Func<bool>? mayAct = null,
		Reprice? reprice = null, Func<Said?>? hold = null, Action<Offer>? onListed = null, Func<CancellationToken, Task>? pause = null, Action<string>? refused = null) {
		// Held by scam watch: nothing goes on the market by itself (ScamWatch.HeldBack).
		if (Modules.ScamWatch.HeldBack(bot) is { } held) {
			return held;
		}

		// 1.8.2: Steam's market blocked for this account, or listing paused after Steam's refusal: nothing listed.
		if (MarketHold.SellBlocked(bot) is { } market) {
			return $"{bot.Name}: nothing listed - {market.ToEnglish()}";
		}

		// Priced in US dollars and listed on a Canadian wallet, a card went up about a quarter cheaper than meant: nothing is
		// listed before Steam has said which money this account's wallet is in.
		if (WalletCurrency(bot) is not int currency) {
			return new Said("{0}: Steam hasn't said this account's wallet currency yet - nothing listed", bot.Name).ToString();
		}

		// 1.8.2, the last word whatever asked: never an item of a game on the keep list ("Never touch items from these
		// games"), nor an item on it by name.
		SpareRules keep = SpareRules.From(bot.Cfg);
		List<Offer> onKeepList = [.. offers.Where(o => OnKeepList(o, keep))];

		foreach (Offer o in onKeepList) {
			Log.Info(KeptSaid(o, new Said("on your keep list - never sold")), bot.Name);
		}

		if (onKeepList.Count > 0) {
			offers = [.. offers.Where(o => !OnKeepList(o, keep))];

			if (offers.Count == 0) {
				return $"{bot.Name}: nothing listed - {(onKeepList.Count == 1 ? "it's" : "they're all")} on the keep list";
			}
		}

		// Claimed, then checked against the waiting trades once more. The plan read them a while ago (pricing takes
		// minutes), and a send that went out since would lose its whole offer to one of these listings - see
		// Bot.ClaimItems. A send starting from now on leaves the claimed ones out.
		HashSet<ulong> claimed = bot.ClaimItems(offers.Select(static o => o.AssetId));

		try {
			if (await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is not { } waiting) {
				return new Said("{0}: Steam wouldn't say which items are in a waiting trade - try again in a while", bot.Name).ToString();
			}

			// 1.8.2: never a card promised to a card swap nocat.lol paired, either.
			HashSet<ulong> promised = CardExchange.NotForSale(bot, waiting, DateTime.UtcNow);

			return await SellClaimedAsync(bot, [.. offers.Where(o => claimed.Contains(o.AssetId) && !promised.Contains(o.AssetId))], ct, mayAct,
				reprice ?? OwnRule(bot, currency), hold, onListed, pause, refused).ConfigureAwait(false);
		} finally {
			bot.ReleaseItems(claimed);
		}
	}

	/// <summary>What one go at listing did: what went up, what was kept and why, Steam's refusal, and why it stopped.</summary>
	public sealed record Listed(List<Offer> Up, List<(Offer Offer, Said Why)> Kept, string? Refusal, Said? Stopped);

	/// <summary>
	/// The listing loop on its own, for the checks too: before EACH listing, after the pause, whether it may still go
	/// (<paramref name="stop"/>: human mode, scam watch, a hold) - then its price again - then whether it may still go once
	/// more, since a price is a market request's time later. A hold typed while a run lists stops it before the next one.
	/// </summary>
	public static async Task<Listed> ListEachAsync(IReadOnlyList<Offer> offers, Func<Said?> stop, Reprice reprice,
		Func<Offer, CancellationToken, Task<(bool Ok, string? Refusal)>> post, Func<CancellationToken, Task> pause, CancellationToken ct) {
		List<Offer> up = [];
		List<(Offer, Said)> kept = [];
		string? refusal = null;
		Said? stopped = null;
		bool first = true;

		foreach (Offer offer in offers) {
			if (!first) {
				await pause(ct).ConfigureAwait(false);
			}

			first = false;

			if (stop() is { } why) {
				stopped = why;

				break;
			}

			(Offer? priced, Said keep) = await reprice(offer, ct).ConfigureAwait(false);

			if (priced == null) {
				kept.Add((offer, keep));

				continue;
			}

			if (stop() is { } late) {
				stopped = late;

				break;
			}

			(bool ok, string? no) = await post(priced, ct).ConfigureAwait(false);

			if (ok) {
				up.Add(priced);

				continue;
			}

			refusal = no ?? "no answer";

			// The first one refused - the rest would be refused for the same reason. 1.8.2: and whenever Steam's reason is a
			// lasting one (not allowed, too many waiting for a confirmation, the wallet full, the market unavailable).
			if ((up.Count == 0) || MarketHold.Lasting(MarketHold.Classify(no))) {
				break;
			}
		}

		return new Listed(up, kept, refusal, stopped);
	}

	private static async Task<string> SellClaimedAsync(Bot bot, IReadOnlyList<Offer> offers, CancellationToken ct, Func<bool>? mayAct, Reprice reprice, Func<Said?>? hold,
		Action<Offer>? onListed, Func<CancellationToken, Task>? pause, Action<string>? refused) {
		DateTime started = DateTime.UtcNow;

		Said? Stop() {
			if (mayAct?.Invoke() == false) {
				return new Said("human mode (its day ended or you're playing on it)");
			}

			if (Modules.ScamWatch.HeldBack(bot) != null) {
				return Modules.ScamWatch.OnHold(bot.Name);
			}

			// 1.8.2: Steam's market blocked meanwhile (or listing paused after a refusal): the rest wait.
			if (MarketHold.SellBlocked(bot) is { } market) {
				return market;
			}

			return hold?.Invoke();
		}

		async Task<(bool, string?)> Post(Offer offer, CancellationToken token) {
			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, "/market/sellitem/"), new Dictionary<string, string> {
				["appid"] = SteamApp.ToString(CultureInfo.InvariantCulture),
				["contextid"] = "6",
				["assetid"] = offer.AssetId.ToString(CultureInfo.InvariantCulture),
				["amount"] = "1",
				["price"] = offer.YouGetCents.ToString(CultureInfo.InvariantCulture)
			}, new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/inventory/"), token, errorVerdict: true).ConfigureAwait(false);

			if (body?.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase) == true) {
				Stats.Record(Stats.KindListed, bot.Name);
				// Written down as ours, so taking down stale listings later never touches one you listed yourself.
				OurListings.Add(bot.Name, offer.AssetId, offer.Hash, offer.YouGetCents, DateTime.UtcNow, offer.BuyerCents);
				// An unsold listing stepped down a cent: that step is used now.
				SpareListings.Listed(bot.Name, offer.Hash);

				// Each one the moment it's up, by name and price - the summary only came once the whole batch was done,
				// by which time the phone had been asking about listings nobody had been told of.
				Log.Good(ListedSaid(offer, Money(offer.BuyerCents, bot), Money(offer.YouGetCents, bot)), bot.Name);
				onListed?.Invoke(offer);

				return (true, null);
			}

			// The automatic seller drops the summary this ends up in, so the reason is written down here as well.
			Log.Debug($"market listing of {offer.Card} ({offer.Game}, asset {offer.AssetId}) refused: {Log.Scrub(Message(body) ?? (body == null ? "no answer" : body[..Math.Min(150, body.Length)]))}", bot.Name);

			// Steam says why in the body ("You have too many listings pending confirmation", a market ban, ...) - kept with an
			// error status too (errorVerdict). 1.8.2: a lasting reason holds the market, or pauses listing, for every part.
			string? why = Message(body);
			MarketHold.Refused(bot, MarketHold.Classify(why), DateTime.UtcNow);

			return (false, why);
		}

		Listed done = await ListEachAsync(offers, Stop, reprice, Post, pause ?? (static token => Task.Delay(Rng.Seconds(20, 60), token)), ct).ConfigureAwait(false);

		if (done.Refusal != null) {
			refused?.Invoke(done.Refusal == "no answer" ? "" : done.Refusal);
		}

		foreach ((Offer offer, Said why) in done.Kept) {
			// Worth more than the price limit by now: never sold, so nothing you need to read - only the debug log.
			if (OverLimit(why)) {
				Log.Debug(KeptSaid(offer, why), bot.Name);
			} else {
				Log.Info(KeptSaid(offer, why), bot.Name);
			}
		}

		List<(Offer Offer, Said Why)> keptSaid = [.. done.Kept.Where(static k => !OverLimit(k.Why))];

		List<Offer> up = done.Up;
		int listed = up.Count;
		int earned = up.Sum(static o => o.YouGetCents);

		if (listed > 0) {
			Log.Info(new Said("listed {0} item(s) · {1} if they all sell", listed, Money(earned, bot)), bot.Name);
		}

		StringBuilder sb = new($"{bot.Name}: listed {listed} of {offers.Count}, {Money(earned, bot)} to you if they all sell");

		if (keptSaid.Count > 0) {
			sb.Append(CultureInfo.InvariantCulture, $" - {keptSaid.Count} kept at today's price ({keptSaid[0].Why.ToEnglish()})");
		}

		if (done.Refusal != null) {
			sb.Append($" - Steam refused one: {done.Refusal}");
		}

		if (done.Stopped is { } stopped) {
			sb.Append(" - stopped part way: ").Append(stopped.ToEnglish());
		}

		if (listed == 0) {
			return sb.ToString();
		}

		if (!bot.CanConfirmTrades) {
			// Said where it can't be missed - the log line alone scrolled past while the phone sat waiting.
			Log.Attention(new Said("{0} market listing(s) to confirm in the Steam app", listed), bot.Name);

			return sb.Append(". They need confirming in the Steam app on your phone (or load this account's authenticator secrets and it'll do that itself).").ToString();
		}

		int confirmed = 0;

		List<Listing> now = await ListingsAsync(bot, ct).ConfigureAwait(false) ?? [];

		// 1.8.2: the confirmations list read once for the whole batch, not one to three times a listing.
		foreach (ulong listing in await bot.ConfirmManyAsync(MatchPending(now, up, started), ct).ConfigureAwait(false)) {
			confirmed++;
			// Confirmed by nocat.farm itself: up, never one you declined - and said, as one confirmed in the Steam app is.
			Listing live = now.First(l => l.Id == listing);
			OurListings.Mark(bot.Name, live);
			Log.Good(SpareRun.LiveSaid(live.Hash, live.BuyerCents, (int) bot.WalletCurrency), bot.Name);
		}

		return sb.Append($", {confirmed} confirmed on its authenticator.").ToString();
	}

	private static string? Message(string? body) {
		if (string.IsNullOrEmpty(body)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			return doc.RootElement.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
		} catch (JsonException) {
			return null;
		}
	}

	// ── listings already up ─────────────────────────────────────────────────
	/// <param name="BuyerCents">What a buyer pays: Steam's own price plus fee, 0 when it didn't say.</param>
	/// <param name="OnHold">Confirmed, but Steam is holding it off the market for a while (no authenticator for long enough).</param>
	public sealed record Listing(ulong Id, ulong AssetId, string Hash, int YouGetCents, DateTime Created, bool AwaitingConfirmation, int BuyerCents = 0, bool OnHold = false) {
		/// <summary>The card's game, from the "appid-Name" market name.</summary>
		public uint Game => (Hash.IndexOf('-') is int dash and > 0) && uint.TryParse(Hash.AsSpan(0, dash), out uint app) ? app : 0;
	}

	/// <summary>The account's market listings: live, held by Steam, and waiting on a confirmation. Null if Steam wouldn't say.</summary>
	public static async Task<List<Listing>?> ListingsAsync(Bot bot, CancellationToken ct) {
		string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, "/market/mylistings/render/?start=0&count=100&norender=1"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return null;
		}

		List<Listing>? found = ParseListings(json);

		// 1.8.2: an answer it can't read is "not known" - never "no listings", which would take every listing for gone.
		if (found == null) {
			Log.Debug($"market listings: Steam's answer had no listings in it: {Log.Scrub(json[..Math.Min(150, json.Length)])}", bot.Name);
		}

		return found;
	}

	/// <summary>
	/// Steam's answer to /market/mylistings/render/?norender=1, as Steam's own market page and the market libraries read it:
	/// "listings" (up for sale), "listings_on_hold" (confirmed, kept off the market for a while) and "listings_to_confirm"
	/// (waiting on the phone) - each listing with listingid, time_created, price (what the seller gets), fee (Steam's and the
	/// game's cut) and asset (id, market_hash_name). Null when none of the three lists is in it.
	/// </summary>
	/// <remarks>Read without "listings_on_hold", a held listing looked gone - judged declined, and never stepped down.</remarks>
	public static List<Listing>? ParseListings(string json) {
		JsonDocument doc;

		try {
			doc = JsonDocument.Parse(json);
		} catch (JsonException) {
			return null;
		}

		using (doc) {
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object)
				|| (!root.TryGetProperty("listings", out _) && !root.TryGetProperty("listings_on_hold", out _) && !root.TryGetProperty("listings_to_confirm", out _))) {
				return null;
			}

			List<Listing> found = [];

			foreach ((string key, bool waiting, bool held) in new[] { ("listings", false, false), ("listings_on_hold", false, true), ("listings_to_confirm", true, false) }) {
				if (!root.TryGetProperty(key, out JsonElement list) || (list.ValueKind != JsonValueKind.Array)) {
					continue;
				}

				foreach (JsonElement l in list.EnumerateArray()) {
					ulong.TryParse(InventoryContents.Text(l, "listingid"), out ulong id);
					JsonElement asset = l.TryGetProperty("asset", out JsonElement a) ? a : default;
					ulong assetId = 0;
					string hash = "";

					if (asset.ValueKind == JsonValueKind.Object) {
						ulong.TryParse(InventoryContents.Text(asset, "id"), out assetId);
						hash = InventoryContents.Text(asset, "market_hash_name");
					}

					int.TryParse(InventoryContents.Text(l, "price"), out int price);
					int buyer = int.TryParse(InventoryContents.Text(l, "fee"), out int fee) && (price > 0) && (fee > 0) ? price + fee : 0;
					long.TryParse(InventoryContents.Text(l, "time_created"), out long created);

					if (id != 0) {
						found.Add(new Listing(id, assetId, hash, price, DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime, waiting, buyer, held));
					}
				}
			}

			return found;
		}
	}

	/// <summary>
	/// The account's last hundred market events (/market/myhistory/render/?norender=1), read-only: what sold. Null if Steam
	/// wouldn't say - then no listing of ours is judged declined on that look.
	/// </summary>
	public static async Task<OurListings.History?> HistoryAsync(Bot bot, CancellationToken ct) {
		string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, "/market/myhistory/render/?query=&start=0&count=100&norender=1"), ct).ConfigureAwait(false);

		return ParseHistory(json);
	}

	/// <summary>Steam's market history event for a listing that sold (1 created, 2 cancelled, 3 sold, 4 bought).</summary>
	private const int EventSold = 3;

	/// <summary>
	/// Steam's answer to /market/myhistory/render/?norender=1: "events" (listingid, event_type, time_event), "listings" by
	/// listing id (price - what the seller gets - and asset: appid, contextid, id) and "assets" by app, context and id
	/// (market_hash_name). Each sold event is one <see cref="OurListings.Sale"/>. With fewer events here than Steam counts
	/// (total_count), anything listed before the oldest of them can't be told - <see cref="OurListings.History.Covers"/>.
	/// Null when it isn't such an answer.
	/// </summary>
	public static OurListings.History? ParseHistory(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		JsonDocument doc;

		try {
			doc = JsonDocument.Parse(json);
		} catch (JsonException) {
			return null;
		}

		using (doc) {
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)
				|| !root.TryGetProperty("events", out JsonElement events) || (events.ValueKind != JsonValueKind.Array)) {
				return null;
			}

			JsonElement listings = root.TryGetProperty("listings", out JsonElement ls) && (ls.ValueKind == JsonValueKind.Object) ? ls : default;
			JsonElement assets = root.TryGetProperty("assets", out JsonElement asx) && (asx.ValueKind == JsonValueKind.Object) ? asx : default;
			List<OurListings.Sale> sales = [];
			long oldest = long.MaxValue;
			int count = 0;

			foreach (JsonElement e in events.EnumerateArray()) {
				count++;
				long.TryParse(InventoryContents.Text(e, "time_event"), out long at);
				oldest = Math.Min(oldest, at);

				if (!int.TryParse(InventoryContents.Text(e, "event_type"), out int type) || (type != EventSold)) {
					continue;
				}

				string listingId = InventoryContents.Text(e, "listingid");
				ulong assetId = 0;
				string hash = "";
				int price = 0;

				if ((listings.ValueKind == JsonValueKind.Object) && listings.TryGetProperty(listingId, out JsonElement l) && (l.ValueKind == JsonValueKind.Object)) {
					int.TryParse(InventoryContents.Text(l, "original_price"), out price);

					if (price <= 0) {
						int.TryParse(InventoryContents.Text(l, "price"), out price);
					}

					if (l.TryGetProperty("asset", out JsonElement a) && (a.ValueKind == JsonValueKind.Object)) {
						string id = InventoryContents.Text(a, "id");
						ulong.TryParse(id, out assetId);
						hash = InventoryContents.Text(a, "market_hash_name");

						if ((hash.Length == 0) && (assets.ValueKind == JsonValueKind.Object)
							&& assets.TryGetProperty(InventoryContents.Text(a, "appid"), out JsonElement app) && (app.ValueKind == JsonValueKind.Object)
							&& app.TryGetProperty(InventoryContents.Text(a, "contextid"), out JsonElement context) && (context.ValueKind == JsonValueKind.Object)
							&& context.TryGetProperty(id, out JsonElement described) && (described.ValueKind == JsonValueKind.Object)) {
							hash = InventoryContents.Text(described, "market_hash_name");
						}
					}
				}

				sales.Add(new OurListings.Sale(assetId, hash, price, DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime));
			}

			int total = int.TryParse(InventoryContents.Text(root, "total_count"), out int t) ? t : count;
			DateTime covers = (total > count) && (oldest != long.MaxValue) ? DateTimeOffset.FromUnixTimeSeconds(oldest).UtcDateTime : DateTime.MinValue;

			return new OurListings.History(sales, covers);
		}
	}

	/// <summary>
	/// The listing ids of OUR new listings still waiting on a confirmation - nothing the owner listed themselves. Each card
	/// just listed matches at most one listing: by its asset id, or - when Steam has given the listed card a new id - by the
	/// same card at the same price, put up since this run began.
	/// </summary>
	/// <remarks>
	/// Matched by the old asset id alone, a card Steam re-numbered on listing was never found, so none of them were
	/// confirmed and the run said "0 confirmed" with the phone full of listings. The time bound and the one-listing-per-card
	/// rule keep a listing the owner made by hand, days ago or at another price, out of it.
	/// </remarks>
	internal static List<ulong> MatchPending(IEnumerable<Listing> listings, IReadOnlyList<Offer> listed, DateTime since) {
		List<Listing> waiting = [.. listings.Where(static l => l.AwaitingConfirmation && (l.Id != 0))];
		List<Offer> left = [.. listed];
		List<ulong> ours = [];

		void Take(Listing l, Func<Offer, bool> fits) {
			int at = left.FindIndex(o => fits(o));

			if (at >= 0) {
				ours.Add(l.Id);
				left.RemoveAt(at);
			}
		}

		foreach (Listing l in waiting.Where(static l => l.AssetId != 0)) {
			Take(l, o => o.AssetId == l.AssetId);
		}

		foreach (Listing l in waiting.Where(l => !ours.Contains(l.Id) && (l.Hash.Length > 0) && (l.Created >= since.AddMinutes(-2)))) {
			Take(l, o => (o.Hash == l.Hash) && (o.YouGetCents == l.YouGetCents));
		}

		return ours;
	}

	/// <summary>
	/// Whether a listing is worth taking down and putting up again: somebody sells the same item for less than a buyer would
	/// pay for ours, AND the selling price rule would now list it lower - by at least a cent of what you get. Under the
	/// default rule a listing can sit a cent under what it sells for, above a dump; taken down for that, it went straight
	/// back up at the same price every week, each time with a phone confirmation. No price for certain: left up.
	/// </summary>
	/// <param name="floorPct">"Never go lower than", for "a cent under the cheapest" (see <see cref="ListingPrice.UnderCheapest"/>).</param>
	public static bool Undercut(Listing listing, MarketQuote? quote, int strategy, int dumpPct, int floorPct = 0) {
		int lowest = quote?.Lowest ?? 0;

		if ((lowest <= 0) || (listing.YouGetCents + FeesFor(listing.YouGetCents) <= lowest)) {
			return false;
		}

		return (ListingPrice.For(quote!.Price, strategy, dumpPct, floorPct: floorPct).Cents is int buyer) && (ReceiveFor(buyer) is int receive and > 0) && (receive < listing.YouGetCents);
	}

	/// <summary>
	/// Take down listings that have sat unsold for a week while the market went under them. The cards come back to
	/// the inventory and the next sell lists them again at that day's price.
	/// </summary>
	/// <param name="oursOnly">Only listings nocat.farm put up itself (<see cref="OurListings"/>) - what Spare items does by
	/// itself. Without it ('sell relist', which you type) every one of the account's card listings counts.</param>
	public static async Task<(int Removed, int Stale)> RelistAsync(Bot bot, CancellationToken ct, int days = 7, bool oursOnly = false) {
		// 1.8.2: never taken down while the market is held - it couldn't go up again.
		if ((Modules.ScamWatch.HeldBack(bot) != null) || (MarketHold.SellBlocked(bot) != null)) {
			return (0, 0);
		}

		if ((WalletCurrency(bot) is not int currency) || (await ListingsAsync(bot, ct).ConfigureAwait(false) is not { } listings)) {
			return (0, 0);
		}

		if (oursOnly) {
			listings = OurListings.Ours(listings, OurListings.Load(bot.Name));
		}

		int removed = 0;
		List<Listing> stale = [];
		int lookups = 0;
		int strategy = ListingPrice.Choice(bot.Cfg.SpareSellPrice), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);
		int floorPct = Math.Clamp(bot.Cfg.SpareRelistFloorPct, 1, 100);

		// A week old (or the days set under Spare items), and somebody now sells the same card for less than the buyer would pay for ours.
		foreach (Listing l in listings.Where(l => !l.AwaitingConfirmation && !l.OnHold && (l.Game != 0) && (DateTime.UtcNow - l.Created > TimeSpan.FromDays(Math.Max(1, days))))) {
			if (lookups >= MaxLookups) {
				break;
			}

			// In the wallet's own currency - the listing's price is in it. A US-dollar search price read as Canadian dollars
			// took every week-old Canadian listing for undercut, and put it up again a quarter cheaper.
			lookups++;

			if (Undercut(l, await FetchForListingAsync(l.Hash, currency, ct).ConfigureAwait(false), strategy, dump, floorPct)) {
				stale.Add(l);
			}
		}

		foreach (Listing listing in stale) {
			await Task.Delay(Rng.Seconds(4, 10), ct).ConfigureAwait(false);

			if (MarketHold.SellBlocked(bot) != null) {
				break;   // held meanwhile: the rest stay up
			}

			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, $"/market/removelisting/{listing.Id}"), [],
				new Uri(WebSession.Community, "/market/"), ct).ConfigureAwait(false);

			if (body != null) {
				removed++;
			} else {
				Log.Debug($"couldn't take down stale market listing {listing.Id}", bot.Name);   // WebSession logged the why
			}
		}

		return (removed, stale.Count);
	}

	/// <summary>
	/// Spare items' own unsold listings, after <paramref name="days"/>: each taken down to go up again a step cheaper (see
	/// <see cref="SpareListings.StepTo"/>), the step remembered for when it's listed again, and each step logged -
	/// "Level ??? Ninja unsold 4 days - 0.31 -> 0.30 CAD". At its floor a listing stays up. Only listings nocat.farm put up
	/// itself (<see cref="OurListings"/>) - never one you listed by hand, nor one Steam is still holding off the market.
	/// Returns how many came down.
	/// </summary>
	/// <param name="stop">Asked before each one comes down: a reason to stop (Spare items turned off, scam watch, human mode), or null.</param>
	/// <param name="budget">A human-mode look's number and gap (see <see cref="LookBudget"/>); none on a robot account.</param>
	/// <param name="pause">The human-mode wait between two; a robot waits a few seconds.</param>
	/// <param name="gemsOf">1.8.2: what an item's gems are worth, by market name (null: not known) - a step that wouldn't pay more stays up.</param>
	public static async Task<int> StepDownAsync(Bot bot, int days, CancellationToken ct, Func<Said?>? stop = null, LookBudget? budget = null,
		Func<CancellationToken, Task<bool>>? pause = null, Func<string, decimal?>? gemsOf = null) {
		if ((Modules.ScamWatch.HeldBack(bot) != null) || (MarketHold.SellBlocked(bot) != null) || (stop?.Invoke() != null) || (WalletCurrency(bot) is not int currency)
			|| (await ListingsAsync(bot, ct).ConfigureAwait(false) is not { } listings)) {
			return 0;
		}

		int floorPct = Math.Clamp(bot.Cfg.SpareRelistFloorPct, 1, 100), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);
		DateTime now = DateTime.UtcNow;
		List<Step> steps = [];
		int lookups = 0;

		// The ones priced longest ago first: a listing already at its floor is asked about again, but never ahead of the rest.
		foreach ((Listing l, OurListings.Entry record) in OurListings.OursWithRecords(listings, OurListings.Load(bot.Name))
			.Where(p => !p.Listing.AwaitingConfirmation && !p.Listing.OnHold && (p.Listing.Game != 0) && (now - p.Listing.Created > TimeSpan.FromDays(Math.Max(1, days))))
			.OrderBy(p => MarketQuotes.Of(p.Listing.Hash, currency)?.At ?? 0)) {
			if (lookups++ >= MaxLookups) {
				break;
			}

			if (await FetchForListingAsync(l.Hash, currency, ct).ConfigureAwait(false) is not { } quote) {
				continue;
			}

			// What a buyer really pays for ours - as listed, not worked back from what we get (a cent off at times).
			int ours = OurListings.BuyerPrice(l, record);

			// 1.8.2: never a step that pays under "Only sell if you get at least", or no more than its gems are worth.
			decimal? gemsNow = gemsOf?.Invoke(l.Hash);

			if (SpareListings.StepTo(ours, quote.Price, floorPct, dump, SpareRules.From(bot.Cfg).MinSaleCents, gemsNow) is int to) {
				steps.Add(new Step(l, ours, to));
			} else if ((gemsNow is decimal gems) && (gems > 0) && (SpareListings.StepTo(ours, quote.Price, floorPct, dump) is int stepped)
				&& !Spare.SellingPays(gems, ReceiveFor(stepped))) {
				// 1.8.2: a cent lower would pay no more than its gems are worth now - taken down, and the next look turns it into gems.
				steps.Add(new Step(l, ours, 0));
			}
		}

		async Task<bool> Remove(Listing listing, CancellationToken token) {
			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, $"/market/removelisting/{listing.Id}"), [],
				new Uri(WebSession.Community, "/market/"), token).ConfigureAwait(false);

			if (body == null) {
				Log.Debug($"couldn't take down unsold market listing {listing.Id}", bot.Name);   // WebSession logged the why
			}

			return body != null;
		}

		List<Step> done = await StepDownEachAsync(steps, () => MarketHold.SellBlocked(bot) ?? stop?.Invoke(), budget,
			pause ?? (static async token => { await Task.Delay(Rng.Seconds(4, 10), token).ConfigureAwait(false); return true; }), Remove, ct).ConfigureAwait(false);

		foreach (Step s in done) {
			OurListings.Mark(bot.Name, s.Listing, settled: true);
			if (s.To == 0) {
				Log.Info(new Said("took down {0} - unsold {1} days, its gems are worth more now", CardName(s.Listing.Hash), (int) (now - s.Listing.Created).TotalDays), bot.Name);

				continue;
			}

			SpareListings memory = SpareListings.Load(bot.Name);
			memory.AddStep(s.Listing.Hash, s.From, s.To, DateTime.UtcNow);
			memory.Save();
			Log.Info(SpareListings.StepSaid(CardName(s.Listing.Hash), (int) (now - s.Listing.Created).TotalDays, s.From, s.To, currency), bot.Name);
		}

		return done.Count;
	}

	/// <summary>One unsold listing to take down: from what a buyer pays now, to what it goes up for next.</summary>
	public sealed record Step(Listing Listing, int From, int To);

	/// <summary>
	/// Taking the listings down, one by one - for the checks too. Before EACH: on a human-mode look its number and its
	/// minutes-long gap (<paramref name="budget"/>), on a robot a few seconds (<paramref name="pause"/>); then whether it may
	/// still go (<paramref name="stop"/>) - a 'spare hold', scam watch or the end of its day stops the rest. Returns those
	/// that came down.
	/// </summary>
	public static async Task<List<Step>> StepDownEachAsync(IReadOnlyList<Step> steps, Func<Said?> stop, LookBudget? budget,
		Func<CancellationToken, Task<bool>> pause, Func<Listing, CancellationToken, Task<bool>> remove, CancellationToken ct) {
		List<Step> done = [];

		foreach (Step s in steps) {
			bool waited = budget != null ? await budget.TakeAsync(pause, ct).ConfigureAwait(false) : await pause(ct).ConfigureAwait(false);

			if (!waited || (stop() != null)) {
				break;
			}

			if (await remove(s.Listing, ct).ConfigureAwait(false)) {
				done.Add(s);
			}
		}

		return done;
	}
}
