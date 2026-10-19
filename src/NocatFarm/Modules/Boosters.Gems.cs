using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// "Use gems for the most levels" (1.8.2), acted on: with buying cards on and "Make booster packs" automatic, each look the
/// gems maths (<see cref="GemsChoice"/>) picks what 1,000 gems are best turned into - a sack sold, a pack sold sealed, a
/// pack opened and its cards sold, or (as always) a pack opened for the closest badge - and automatic does that.
/// </summary>
/// <remarks>
/// Kept to the same rules as everything else that lists: one thing at a time, inside the account's day on human mode, at
/// most <see cref="GemsChoice.ListingsADay"/> listings a day, each priced again right before it goes up (a cent under the
/// cheapest) and never for less than its gems are worth - and each listing confirmed in the Steam app like any other (or
/// by this account's authenticator, as every listing is). Only tradable gems go to the market; untradable ones still make
/// packs for the closest badge. Prices are read through the price book's queue, as the background's share.
/// </remarks>
public sealed partial class Boosters {
	/// <summary>What one gems step did: dealt with it, or left the look to automatic - with any gems, or only untradable ones.</summary>
	private enum GemsGo { Handled, Badge, BadgeUntradable }

	/// <summary>The gems maths' last word on this account (null until it has weighed once).</summary>
	internal GemsChoice.Choice? LastGems { get; private set; }

	private int WalletCurrency => Bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) Bot.WalletCurrency;

	/// <summary>Prices for the gems maths, as the app reads them: held, or asked through the price book's queue as background reads.</summary>
	internal sealed class LiveMarket(Bot bot) : GemsChoice.IMarket {
		public MarketQuote? Known(string hash, int currency) => MarketQuotes.Of(hash, currency);

		public async Task<MarketQuote?> QuoteAsync(string hash, int currency, CancellationToken ct) {
			PriceBook.Background.Value = true;

			return await MarketQuotes.FetchAsync(hash, currency, ct).ConfigureAwait(false);
		}

		private Dictionary<uint, SetFinder.KnownSet>? _known;

		public CardSet? KnownSet(uint app) =>
			(_known ??= SetFinder.Shared.Known(DateTime.UtcNow).ToDictionary(static k => k.App)).TryGetValue(app, out SetFinder.KnownSet? k) ? k.ToCardSet() : null;

		public async Task<(CardSet? Set, bool Asked)> SetAsync(uint app, CancellationToken ct) {
			PriceBook.Background.Value = true;
			(LevelPlanner.SetPrice? set, bool asked) = await LevelPlanner.PriceAsync(bot, app, ct).ConfigureAwait(false);

			return ((set == null) || (set.Size == 0) ? null : new CardSet(app, set.Game, [.. set.Cards.Keys.Order(StringComparer.Ordinal)], set.Cards), asked);
		}

		public double? Rate(int currency) => WalletRate.ForDeciding(currency);
	}

	/// <summary>What the next badge levels cost for this account - noted by buying cards lately, or priced now.</summary>
	private async Task<IReadOnlyList<long>?> LevelCostsAsync(int currency, InventoryContents inventory, CancellationToken ct) =>
		GemsChoice.CostsOf(Bot.Name, currency, DateTime.UtcNow)
		?? (BotManager.ModuleOf<CardBuyer>(Bot) is { } buyer ? await buyer.LevelCostsAsync(currency, LevelPlanner.NormalCards(inventory), ct).ConfigureAwait(false) : null);

	/// <summary>Everything one weighing needs.</summary>
	private GemsChoice.Inputs GemsInputs(Page page, BoosterPick.Ranking ranking, IReadOnlyList<long>? costs) =>
		new(WalletCurrency, page, ranking, costs, Math.Clamp(Bot.Cfg.SpareDumpPct, 0, 95), Bot.Cfg.BoosterNeverOpen, DateTime.UtcNow);

	/// <summary>
	/// One look's gems step: first whatever it made to sell and hasn't listed yet, then the maths and what it says. Handled:
	/// it did something (or waits); Badge: automatic makes its pack as always; BadgeUntradable: the same, with untradable
	/// gems only (the tradable ones go to the market).
	/// </summary>
	private async Task<GemsGo> GemsStepAsync(Page page, BoosterPick.Ranking ranking, InventoryContents inventory, CancellationToken ct) {
		GemsChoice.Memory mem = GemsChoice.Memory.Of(Bot.Name);
		int cur = WalletCurrency;

		if (await SellMadeAsync(inventory, mem, cur, ct).ConfigureAwait(false)) {
			_autoNext = DateTime.UtcNow + Rng.Minutes(10, 20);

			return GemsGo.Handled;
		}

		IReadOnlyList<long>? costs = await LevelCostsAsync(cur, inventory, ct).ConfigureAwait(false);
		LiveMarket market = new(Bot);
		GemsChoice.Inputs x = GemsInputs(page, ranking, costs);
		GemsChoice.Choice choice = await GemsChoice.WeighAsync(x, market, GemsChoice.LearnALook, ct).ConfigureAwait(false);
		LastGems = choice;
		mem.Said(GemsChoice.Line(choice), DateTime.UtcNow);
		mem.Save(Bot.Name);
		Log.Debug($"gems: {GemsChoice.Line(choice)}", Bot.Name);

		if (choice.Way == GemsChoice.Way.Badge) {
			return GemsGo.Badge;
		}

		SavingFor = 0;
		int keep = Math.Max(0, Bot.Cfg.BoosterKeepGems);

		// Untradable gems can only ever make packs for a badge: they go there, whatever the tradable ones do.
		bool untradableToBadge = (ranking.Ranked.Count > 0) && (page.UntradableGems >= ranking.Ranked[0].Price) && (page.Gems - (long) ranking.Ranked[0].Price >= keep);

		switch (choice.Way) {
			case GemsChoice.Way.Sack:
				return await PackSackAsync(page, inventory, mem, choice.Pick!, keep, ct).ConfigureAwait(false) || !untradableToBadge ? GemsGo.Handled : GemsGo.BadgeUntradable;
			case GemsChoice.Way.PackSell or GemsChoice.Way.PackOpen:
				return await MakeToSellAsync(page, inventory, x, choice, market, mem, keep, ct).ConfigureAwait(false) || !untradableToBadge ? GemsGo.Handled : GemsGo.BadgeUntradable;
			default:
				_autoStatus = new Said("gems kept loose · not sure yet what they're worth in levels");
				_autoNext = Later(2, 4);

				return untradableToBadge ? GemsGo.BadgeUntradable : GemsGo.Handled;
		}
	}

	/// <summary>(a): 1,000 tradable gems packed into a sack, to be listed at a later look (once the inventory shows it). True when it acted.</summary>
	private async Task<bool> PackSackAsync(Page page, InventoryContents inventory, GemsChoice.Memory mem, GemsChoice.Option pick, int keep, CancellationToken ct) {
		string levels = pick.Levels.ToString("0.0", CultureInfo.InvariantCulture);
		SpareItem? stack = Spare.Read(inventory).Where(static i => (i.Kind == SpareKind.Gems) && i.Tradable).OrderByDescending(static i => i.Amount).FirstOrDefault();

		if ((stack == null) || (stack.Amount < GemsChoice.Per) || (page.Gems - (long) GemsChoice.Per < keep)) {
			_autoStatus = new Said("saving tradable gems for a sack to sell");
			_autoNext = Later(6, 10);

			return false;
		}

		if (mem.LeftToday(DateTime.Now) <= 0) {
			_autoStatus = new Said("gems: today's listings are done · more tomorrow");
			_autoNext = Later(3, 6);

			return true;
		}

		LiveSpareSteam steam = new(Bot, () => DayGate.Open);
		(bool ok, string? why) = await steam.PackSacksAsync(stack, 1, ct).ConfigureAwait(false);

		if (!ok) {
			Log.Info(new Said("couldn't pack gems into sacks: {0}", why ?? "-"), Bot.Name);
			_autoNext = Later(1, 2);

			return true;
		}

		mem.Sacks++;
		mem.Save(Bot.Name);
		Log.Info(new Said("packed 1,000 gems into a sack to sell · about {0} levels per 1,000 gems", levels), Bot.Name);
		_autoStatus = new Said("packed 1,000 gems into a sack to sell · about {0} levels per 1,000 gems", levels);
		_autoNext = DateTime.UtcNow + Rng.Minutes(5, 15);
		_dayGate!.Space(5, 30);

		return true;
	}

	/// <summary>(b) and (c): a pack made from tradable gems - to sell sealed, or to open and sell its cards. True when it acted.</summary>
	private async Task<bool> MakeToSellAsync(Page page, InventoryContents inventory, GemsChoice.Inputs x, GemsChoice.Choice choice, GemsChoice.IMarket market,
		GemsChoice.Memory mem, int keep, CancellationToken ct) {
		GemsChoice.Option pick = choice.Pick!;
		bool sealedPack = pick.Way == GemsChoice.Way.PackSell;

		if (!page.Offers.TryGetValue(pick.App, out Offer? offer) || offer.Unavailable) {
			_autoNext = Later(1, 2);

			return false;
		}

		if ((page.TradableGems < offer.Price) || (page.Gems - (long) offer.Price < keep)) {
			_autoStatus = new Said("saving tradable gems for a {0} pack to sell", offer.Name);
			_autoNext = Later(6, 10);

			return false;
		}

		if (mem.LeftToday(DateTime.Now) <= 0) {
			_autoStatus = new Said("gems: today's listings are done · more tomorrow");
			_autoNext = Later(3, 6);

			return true;
		}

		// Its price read again right before the gems are spent: anything else than this pack now, and it waits.
		GemsChoice.Choice sure = await GemsChoice.ConfirmAsync(choice, x, market, ct).ConfigureAwait(false);

		if ((sure.Way != pick.Way) || (sure.Pick?.App != pick.App)) {
			Log.Debug($"gems: {sure.Why}", Bot.Name);
			_autoStatus = new Said("the {0} pack's price moved · weighed again later", offer.Name);
			_autoNext = Later(1, 2);

			return true;
		}

		string levels = sure.Pick!.Levels.ToString("0.0", CultureInfo.InvariantCulture);
		ulong after = inventory.Assets.Select(static a => ulong.TryParse(InventoryContents.Text(a, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0).DefaultIfEmpty(0ul).Max();
		await Task.Delay(Rng.Seconds(4, 12), ct).ConfigureAwait(false);

		// Tradable gems only: a pack made from untradable ones couldn't be sold, nor could its cards.
		Made made = await MakeAsync(Bot, offer, page.TradableGems, page.UntradableGems, ct, prefer: "1").ConfigureAwait(false);
		int purpose = sealedPack ? BoosterPick.ToSell : BoosterPick.ToOpenAndSell;

		if (!made.Ok && made.NoAnswer) {
			AutoMemory.NoteMade(offer.AppId, DateTime.UtcNow);
			AutoMemory.Add(new BoosterPick.OwnPack(offer.AppId, offer.Name, 0, after, DateTime.UtcNow, Maybe: true, SetSize: BoosterPick.SetSize(offer.Price), For: purpose));
			AutoMemory.Save(Bot.Name);
			Log.Info(new Said("Steam didn't answer about the {0} booster pack - looking for it in the inventory before making another", offer.Name), Bot.Name);
			_openNext = DateTime.UtcNow + Rng.Minutes(1, 3);
			_autoNext = DateTime.UtcNow + Rng.Minutes(4, 8);

			return true;
		}

		if (!made.Ok) {
			Log.Info(new Said("couldn't make a {0} booster pack: {1}", offer.Name, made.Why ?? "-"), Bot.Name);
			AutoMemory.NoteMade(offer.AppId, DateTime.UtcNow);
			AutoMemory.Save(Bot.Name);
			_autoNext = Later(1, 2);

			return true;
		}

		BoosterPick.OwnPack pack = new(offer.AppId, offer.Name, made.ItemId, after, DateTime.UtcNow, SetSize: BoosterPick.SetSize(offer.Price), For: purpose);
		AutoMemory.NoteMade(offer.AppId, DateTime.UtcNow);
		AutoMemory.Add(pack);
		AutoMemory.AddMade(pack);
		AutoMemory.Save(Bot.Name);
		Log.Reward(MadeSaid(offer.Name, made.Gems), Bot.Name);
		Said why = sealedPack ? new Said("{0} pack made to sell · about {1} levels per 1,000 gems", offer.Name, levels)
			: new Said("{0} pack made to open and sell its cards · about {1} levels per 1,000 gems", offer.Name, levels);
		Log.Info(why, Bot.Name);
		_autoStatus = why;

		// A sealed one is listed at a later look, once the inventory shows it; one to open is opened like automatic's own.
		if (!sealedPack) {
			_openNext = DateTime.UtcNow + (Bot.Cfg.LegitMode ? Rng.HumanMinutes(3, 20) : Rng.Minutes(1, 3));
		}

		_autoNext = DateTime.UtcNow + Rng.Minutes(5, 15);
		_dayGate!.Space(5, 30);

		return true;
	}

	/// <summary>
	/// What it made to sell and hasn't listed: one kind a look - its sacks, its sealed packs, or the cards of packs it opened
	/// to sell. Each priced again right before it goes up and never for less than its gems are worth. True when it tried.
	/// </summary>
	private async Task<bool> SellMadeAsync(InventoryContents inventory, GemsChoice.Memory mem, int cur, CancellationToken ct) {
		List<BoosterPick.OwnPack> packs = [.. AutoMemory.Snapshot().Where(static o => o.For == BoosterPick.ToSell)];
		List<GemsChoice.Memory.OpenedCard> cards = mem.CardsNow();

		if ((mem.Sacks <= 0) && (packs.Count == 0) && (cards.Count == 0)) {
			return false;
		}

		int left = mem.LeftToday(DateTime.Now);

		if ((left <= 0) || (cur <= 0)) {
			return false;
		}

		// What a sack pays now: what every listing here has to beat to keep the rule that gems never lose money. 1.8.2: only a
		// price at most SackFresh old - read again when it's older; with none that fresh, nothing is listed this look (an old
		// price set the minimums of packs and cards).
		MarketQuote? sackQuote = MarketQuotes.Of(SparePrices.SackHash, cur);

		if ((sackQuote == null) || (DateTime.UtcNow - sackQuote.When > GemsChoice.SackFresh)) {
			sackQuote = await new LiveMarket(Bot).QuoteAsync(SparePrices.SackHash, cur, ct).ConfigureAwait(false);
		}

		int sack = (sackQuote != null) && (DateTime.UtcNow - sackQuote.When <= GemsChoice.SackFresh) ? GemsChoice.Receive(sackQuote, Math.Clamp(Bot.Cfg.SpareDumpPct, 0, 95)) : 0;

		if (sack <= 0) {
			Log.DebugOnChange($"gemsack:{Bot.Name}", "gems: no fresh price for a sack of gems - nothing listed this look", Bot.Name);

			return false;
		}

		List<SpareItem> items = Spare.Read(inventory);
		int floor = Math.Clamp(Bot.Cfg.SpareRelistFloorPct, 1, 100), dump = Math.Clamp(Bot.Cfg.SpareDumpPct, 0, 95);

		Seller.Reprice Priced(int minReceive, int? maxWorth = null) => (o, token) => Seller.RepriceAsync(o, cur, 3, dump, maxWorth, null, token, minReceive: minReceive, floorPct: floor,
			ours: OurListings.UpFor(OurListings.Load(Bot.Name), o.Hash));

		async Task<int> ListAsync(IReadOnlyList<Seller.Offer> offers, Seller.Reprice reprice, Action<Seller.Offer>? each = null) {
			int listed = 0;
			await Seller.SellAsync(Bot, offers, ct, mayAct: () => DayGate.Open, reprice: reprice, onListed: o => { listed++; each?.Invoke(o); }).ConfigureAwait(false);
			mem.Listed(listed);
			mem.Save(Bot.Name);

			return listed;
		}

		// Its sacks: a sack is the gems' own worth, so only the market's floor. Only as many as it packed - and a listing
		// whose answer wasn't a clear yes may be up all the same: the listings are looked at before another goes up.
		if (mem.Sacks > 0) {
			if (mem.SackTriedAt > 0) {
				if (await Seller.ListingsAsync(Bot, ct).ConfigureAwait(false) is not { } up) {
					return true;   // not known whether the last one went up: nothing more this look
				}

				DateTime tried = new(mem.SackTriedAt, DateTimeKind.Utc);
				mem.SackTriedAt = 0;

				if (up.Any(l => (l.Hash == SparePrices.SackHash) && (l.Created >= tried.AddMinutes(-2)))) {
					mem.Sacks--;   // it went up after all
					mem.Save(Bot.Name);

					return true;
				}

				mem.Save(Bot.Name);
			}

			SpareItem? held = items.FirstOrDefault(static i => (i.Kind == SpareKind.GemSack) && i.Tradable && i.Marketable);

			if (held == null) {
				// Not in the inventory (yet, or sent or sold by hand): looked for once more, then forgotten.
				Log.Debug($"gems: no sack of gems to list in the inventory ({mem.Sacks} noted)", Bot.Name);
				mem.Sacks = 0;
				mem.Save(Bot.Name);

				return false;
			}

			mem.SackTriedAt = DateTime.UtcNow.Ticks;
			mem.Save(Bot.Name);

			if (await ListAsync([new Seller.Offer(held.AssetId, "Steam", "Sack of Gems", 0, 0, 0, SparePrices.SackHash)], Priced(1)).ConfigureAwait(false) > 0) {
				mem.Sacks--;
				mem.SackTriedAt = 0;
				mem.Save(Bot.Name);
			}

			_dayGate!.Space(5, 30);

			return true;
		}

		// Its sealed packs: never for less than their gems would pay as a sack.
		if (packs.Count > 0) {
			if (AutoMemory.Locate(BadgeCraft.Packs(inventory, []))) {
				AutoMemory.Save(Bot.Name);
				packs = [.. AutoMemory.Snapshot().Where(static o => o.For == BoosterPick.ToSell)];
			}

			if (sack <= 0) {
				return false;   // what the gems are worth isn't known right now: the next look
			}

			foreach (BoosterPick.OwnPack own in packs) {
				if ((own.Asset == 0) || (items.FirstOrDefault(i => i.AssetId == own.Asset) is not { Marketable: true } item)) {
					continue;
				}

				// What its gems would have paid as a sack, and a cent more (a pack costs 6000 / its set's cards gems).
				if (BoosterPick.PackGems(own.SetSize) is not (int gems and > 0)) {
					continue;
				}

				int worth = (int) Math.Ceiling(Spare.GemsWorth(gems, sack)) + 1;

				if (await ListAsync([new Seller.Offer(own.Asset, own.Name, item.Name, 0, 0, 0, item.Hash)], Priced(worth)).ConfigureAwait(false) > 0) {
					AutoMemory.Remove(own);
					AutoMemory.Save(Bot.Name);
				}

				_dayGate!.Space(5, 30);

				return true;
			}

			return false;
		}

		// The cards of packs it opened to sell: one copy per card it got, each never under its own gems' worth. 1.8.2: by the
		// spare items rules too - never a card of a game (or a card) on the keep list, never one worth more than "Never touch
		// items worth more than", one whose gem value isn't known is kept for now, and never a card of a set the account is
		// levelling or buying cards for (cards of it held besides the pack's, bought for a badge, or packs working on it).
		HashSet<ulong> taken = [];
		List<(Seller.Offer Offer, GemsChoice.Memory.OpenedCard Card, int Min)> rows = [];
		SpareRules rules = SpareRules.From(Bot.Cfg);
		HashSet<uint> working = [.. CardExchange.BoughtGames(Bot.Name, DateTime.UtcNow), .. BoosterPick.Busy(Bot)];

		foreach (GemsChoice.Memory.OpenedCard card in cards) {
			SpareItem? item = items.FirstOrDefault(i => (i.Kind == SpareKind.Card) && (i.Game == card.App) && i.Marketable && !taken.Contains(i.AssetId)
				&& i.Name.Equals(card.Name, StringComparison.Ordinal));

			if (item == null) {
				mem.RemoveCard(card);   // crafted, swapped or sold since: nothing to list
				continue;
			}

			if (rules.KeepGames.Contains(card.App) || rules.KeepNames.Contains(item.Name) || rules.KeepNames.Contains(item.Hash)
				|| Levelling(card.App, items, cards, working)) {
				mem.RemoveCard(card);   // kept: a spare card like any other from now on
				Log.Debug($"gems: {item.Name} ({GameNames.Of(card.App)}) kept - on the keep list, or its set is being levelled", Bot.Name);
				continue;
			}

			if (SteamGems.KnownValue(item) is not int goo) {
				continue;   // what its gems are worth isn't known yet: not sold for now
			}

			taken.Add(item.AssetId);
			int min = goo > 0 ? (int) Math.Ceiling(Spare.GemsWorth(goo, sack)) + 1 : 1;
			rows.Add((new Seller.Offer(item.AssetId, GameNames.Of(card.App), item.Name, 0, 0, 0, item.Hash), card, min));
		}

		mem.Save(Bot.Name);

		if (rows.Count == 0) {
			return false;
		}

		rows = [.. rows.Take(left)];
		Dictionary<ulong, int> mins = rows.ToDictionary(static r => r.Offer.AssetId, static r => r.Min);
		await ListAsync([.. rows.Select(static r => r.Offer)], (o, token) => Priced(mins.GetValueOrDefault(o.AssetId, 1), rules.MaxPriceCents)(o, token),
			o => { if (rows.FirstOrDefault(r => r.Offer.AssetId == o.AssetId) is { Card: { } c }) { mem.RemoveCard(c); } }).ConfigureAwait(false);
		mem.Save(Bot.Name);
		_dayGate!.Space(5, 30);

		return true;
	}

	/// <summary>
	/// 1.8.2: whether the account is levelling a game's set, so a card of it from a pack opened to sell is needed: cards of it
	/// held besides the ones from packs opened to sell, cards of it bought for a badge, or automatic's packs working on it.
	/// </summary>
	internal static bool Levelling(uint app, IReadOnlyList<SpareItem> items, IReadOnlyList<GemsChoice.Memory.OpenedCard> opened, IReadOnlySet<uint> working) =>
		working.Contains(app) || (items.Count(i => (i.Kind == SpareKind.Card) && (i.Game == app)) > opened.Count(c => c.App == app));

	/// <summary>A pack automatic opened to sell its cards: the cards noted, to be listed at the next looks.</summary>
	internal static void OpenedToSell(Bot bot, BoosterPick.OwnPack own, IReadOnlyList<string> cards) {
		if (own.For != BoosterPick.ToOpenAndSell) {
			return;
		}

		GemsChoice.Memory mem = GemsChoice.Memory.Of(bot.Name);
		mem.AddCards(own.App, cards, DateTime.UtcNow);
		mem.Save(bot.Name);
	}

	// ── 'booster plan' and 'buy preview' ────────────────────────────────────
	/// <summary>
	/// The gems maths for the previews, in one line - or why it isn't in play. Reads the booster creator, the badges and the
	/// inventory when the caller hasn't; prices a few things it doesn't know yet. Nothing is made, listed or bought.
	/// </summary>
	public static async Task<string> GemsPreviewAsync(Bot bot, Page? page, BoosterPick.Ranking? ranking, InventoryContents? inventory, CancellationToken ct) {
		if (!bot.Cfg.GemsForLevels) {
			return "Use gems for the most levels: off - gems go to automatic's closest badge as before.";
		}

		if (bot.Cfg.BoosterMake != BoosterPick.Auto) {
			return "Use gems for the most levels: does nothing while 'Make booster packs' isn't automatic.";
		}

		if (!CardBuyer.On(bot)) {
			return "Use gems for the most levels: does nothing while buying cards is off - money from gems would have nothing to buy.";
		}

		page ??= await ReadAsync(bot, ct).ConfigureAwait(false);

		if (page == null) {
			return "Use gems for the most levels: couldn't read the booster creator - try again later.";
		}

		inventory ??= await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory is not { Complete: true }) {
			return "Use gems for the most levels: couldn't read all of its inventory - try again later.";
		}

		if (ranking == null) {
			SpareBadges? badges = SpareBadges.Parse(await bot.Web.ApiGetAsync("IPlayerService", "GetBadges", new Dictionary<string, string> {
				["steamid"] = bot.SteamId.ToString(CultureInfo.InvariantCulture)
			}, ct).ConfigureAwait(false));

			if (badges == null) {
				return "Use gems for the most levels: Steam wouldn't say what its badges are - try again later.";
			}

			Dictionary<uint, DateTime> made = BotManager.ModuleOf<Boosters>(bot)?.AutoMemory.Made ?? BoosterPick.Memory.Load(bot.Name).Made;
			ranking = BoosterPick.Rank(page, badges.Levels, LevelPlanner.NormalCards(inventory), bot.Cfg.BoosterNeverOpen, made, DateTime.UtcNow);
		}

		int cur = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;
		IReadOnlyList<long>? costs = GemsChoice.CostsOf(bot.Name, cur, DateTime.UtcNow)
			?? (BotManager.ModuleOf<CardBuyer>(bot) is { } buyer ? await buyer.LevelCostsAsync(cur, LevelPlanner.NormalCards(inventory), ct).ConfigureAwait(false) : null);
		GemsChoice.Inputs x = new(cur, page, ranking, costs, Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95), bot.Cfg.BoosterNeverOpen, DateTime.UtcNow);
		GemsChoice.Choice choice = await GemsChoice.WeighAsync(x, new LiveMarket(bot), GemsChoice.LearnALook, ct).ConfigureAwait(false);

		return $"Use gems for the most levels ({page.TradableGems:N0} tradable gems): {GemsChoice.Line(choice)}";
	}
}
