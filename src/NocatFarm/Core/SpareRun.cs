using System.Globalization;
using System.Text;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Doing what <see cref="Spare"/> decided: a few items a look, inside the day's caps, every one logged with its reason -
/// and only once you've typed <c>spare ok</c> for the account. Then what happens to the gems, and the booster packs.
/// </summary>
public static class SpareRun {
	/// <summary>Games priced in one look - each a market search or a few. The rest wait for the next look.</summary>
	private const int GamesPerLook = 8;

	/// <summary>Gem values asked for in one look.</summary>
	private const int GemValuesPerLook = 20;

	/// <summary>What one go did.</summary>
	public sealed record Done(int GemItems, int Gems, int Sold, string? SellSaid, Said? Stopped);

	/// <summary>
	/// Do the plan's gems and sales, most valuable first: at most the per-look number, and nothing past today's item or
	/// value cap. Nothing at all before <c>spare ok</c>. Each item is handled once, whatever the plan says twice.
	/// </summary>
	public static async Task<Done> ActAsync(SparePlan plan, SpareRules rules, SpareLedger ledger, ISpareSteam steam, string account,
		Func<DateTime> nowLocal, CancellationToken ct) {
		if (!ledger.Approved) {
			return new Done(0, 0, 0, null, new Said("Spare items is off on {0} - it only shows what it would do", account));
		}

		HashSet<ulong> handled = [];
		List<SpareDecision> toSell = [];
		int done = 0, gemItems = 0, gems = 0;
		Said? stopped = null;

		foreach (SpareDecision d in plan.Actions) {
			if (done >= rules.PerRun) {
				break;
			}

			if (!handled.Add(d.Item.AssetId)) {
				continue;   // never twice, whatever asked
			}

			// A human-mode account's day can end, or you can start playing on it, while a look prices and works - minutes. So
			// it's asked again before every item, and the look stops there.
			if (!steam.MayAct) {
				stopped = HumanStopped;

				break;
			}

			// 'spare hold' typed while the look works: nothing more from this one.
			if (Hold(ledger, account) is { } hold) {
				stopped = hold;

				break;
			}

			// Held by scam watch: nothing goes on the market, and nothing is counted against today's caps for it.
			if ((d.Verdict == SpareVerdict.Sell) && steam.Held) {
				stopped = Modules.ScamWatch.OnHold(account);

				continue;
			}

			// Checked and counted in one step: a look and a command at the same moment can't both fit under the cap.
			if (!ledger.TryReserve(d.PriceCents, rules.DailyItems, rules.DailyValueCents, nowLocal())) {
				if (ledger.DoneItems >= rules.DailyItems) {
					stopped = new Said("today's limit of {0} item(s) is reached", rules.DailyItems);

					break;
				}

				stopped = new Said("today's limit of {0} in value is reached", Spare.Money(rules.DailyValueCents));

				continue;   // a cheaper one may still fit
			}

			if (d.Verdict == SpareVerdict.Sell) {
				// Counted now, and saved now: the listing goes with the others at the end, and a cap is a cap even if Steam
				// refuses one - or the program restarts while it lists, and the same day would list its whole cap again.
				toSell.Add(d);
				ledger.Save();
				done++;

				continue;
			}

			if ((gemItems > 0) && !await steam.PauseAsync(ct).ConfigureAwait(false)) {
				ledger.Unreserve(d.PriceCents, nowLocal());

				break;
			}

			// Again after the wait between items.
			if (!steam.MayAct) {
				ledger.Unreserve(d.PriceCents, nowLocal());
				stopped = HumanStopped;

				break;
			}

			if (Hold(ledger, account) is { } held) {
				ledger.Unreserve(d.PriceCents, nowLocal());
				stopped = held;

				break;
			}

			HashSet<ulong> claimed = steam.Claim([d.Item.AssetId]);

			if (claimed.Count == 0) {
				ledger.Unreserve(d.PriceCents, nowLocal());

				continue;   // being sent or sold this very moment
			}

			(bool ok, int got, string? why) result;

			try {
				result = await steam.GrindAsync(d.Item, d.GemValue, ct).ConfigureAwait(false);
			} catch {
				// Steam never said it was done (no answer, the connection dropped, closing): not counted against today's caps.
				ledger.Unreserve(d.PriceCents, nowLocal());
				ledger.Save();

				throw;
			} finally {
				steam.Release(claimed);
			}

			done++;

			if (!result.ok) {
				ledger.Unreserve(d.PriceCents, nowLocal());
				Log.Info(new Said("couldn't turn {0} into gems: {1}", d.Item.Name, result.why ?? "-"), account);
				stopped = new Said("Steam refused one - trying again next time");

				break;   // the next would be refused for the same reason
			}

			ledger.Save();
			gemItems++;
			gems += result.got;
			Log.Reward(new Said("turned {0} ({1}) into {2} gems - {3}", d.Item.Name, GameNames.Of(d.Item.Game), result.got, d.Why), account, Topic.Items);
		}

		string? sold = null;
		int listed = 0;

		if ((toSell.Count > 0) && (!steam.MayAct || (Hold(ledger, account) != null))) {
			// Nothing listed after all: what was counted for it goes back.
			foreach (SpareDecision d in toSell) {
				ledger.Unreserve(d.PriceCents, nowLocal());
			}

			toSell.Clear();
			stopped = Hold(ledger, account) ?? HumanStopped;
		}

		if (toSell.Count > 0) {
			foreach (SpareDecision d in toSell) {
				Log.Info(new Said("selling {0} ({1}) - {2}", d.Item.Name, GameNames.Of(d.Item.Game), d.Why), account);
			}

			(listed, sold) = await steam.SellAsync(toSell, ct).ConfigureAwait(false);
			GiveBack(ledger, toSell, listed, nowLocal);
		}

		ledger.Save();

		return new Done(gemItems, gems, listed, sold, stopped);
	}

	/// <summary>
	/// The reservations for the items a sale didn't list - stopped part way (human mode, a scam watch hold), claimed or in a
	/// trade by then, or refused - given back. Steam only says how many went up, so the cheapest are given back: what stays
	/// counted is never less than what was really listed.
	/// </summary>
	internal static void GiveBack(SpareLedger ledger, IReadOnlyList<SpareDecision> reserved, int listed, Func<DateTime> nowLocal) {
		foreach (SpareDecision d in reserved.OrderBy(static d => d.PriceCents).Take(reserved.Count - Math.Clamp(listed, 0, reserved.Count))) {
			ledger.Unreserve(d.PriceCents, nowLocal());
		}

		ledger.Save();
	}

	/// <summary>
	/// Why a run must stop now that <c>spare hold</c> took the ok back, or null while it may go on. Asked before every item,
	/// and before every listing (see <see cref="Seller.ListEachAsync"/>): live, one more item went up a second after a hold.
	/// </summary>
	public static Said? Hold(SpareLedger ledger, string account) =>
		ledger.Approved ? null : new Said("Spare items was turned off on {0} - stopped before the next item", account);

	/// <summary>
	/// How each spare-items listing is priced just before it goes up: again from the market, in the account's own wallet
	/// currency, by the selling price setting. Kept instead when it can't be priced for certain, when a card, background or
	/// emoticon is now worth more than the price limit, or when it's now worth more than was counted for it against the
	/// day's value cap.
	/// </summary>
	public static Seller.Reprice Repricer(IReadOnlyList<SpareDecision> items, SpareRules rules, int currency) {
		Dictionary<ulong, SpareDecision> byAsset = [];

		foreach (SpareDecision d in items) {
			byAsset[d.Item.AssetId] = d;
		}

		return (offer, ct) => {
			SpareDecision? d = byAsset.GetValueOrDefault(offer.AssetId);
			bool limited = d?.Item.Kind is SpareKind.Card or SpareKind.FoilCard or SpareKind.Background or SpareKind.Emoticon;
			int counted = d?.PriceCents ?? 0;

			return Seller.RepriceAsync(offer, currency, rules.SellPrice, rules.DumpPct, limited ? rules.MaxPriceCents : null, worth => worth <= counted, ct);
		};
	}

	/// <summary>Why a look stopped part way on a human-mode account.</summary>
	private static Said HumanStopped => new("stopped part way - human mode: its day ended or you're playing on it; the rest waits for a later look");

	/// <summary>
	/// Whether this account may still act now: always without human mode; with it, only while its own-day gate (the
	/// module's, so a command and the module agree) is open - its day, settled in, you not playing on it.
	/// </summary>
	internal static bool StillMayAct(Bot bot, Modules.HumanGate? gate) =>
		gate?.Open ?? (!bot.Cfg.LegitMode || (!bot.Stopping && !bot.PlayingBlocked && Modules.HumanMode.UpFor(bot)));

	/// <summary>The gate spare items keep to on this account: the module's own, or none (in a check without the module).</summary>
	internal static Modules.HumanGate? SpareGate(Bot bot) => BotManager.ModuleOf<Modules.SpareItems>(bot)?.Gate;

	/// <summary>The gate opening booster packs keeps to on this account.</summary>
	internal static Modules.HumanGate? PacksGate(Bot bot) => BotManager.ModuleOf<Modules.PackOpener>(bot)?.Gate;

	// ── gems afterwards ──────────────────────────────────────────────────────
	/// <summary>The gems choice with "automatic" worked out: sacks kept on a human-mode account, loose for booster packs on the others.</summary>
	public static int GemsChoice(BotConfig cfg) => cfg.SpareGems switch {
		0 => cfg.LegitMode ? 2 : 1,
		var c => Math.Clamp(c, 1, 4)
	};

	/// <summary>
	/// After a look: loose tradable gems into sacks of 1000 (keeping the number set loose), then the sacks sent or one
	/// sold, as chosen. Untradable gems stay loose - a sack of them couldn't go anywhere either.
	/// </summary>
	public static async Task<List<Said>> GemsAfterAsync(IReadOnlyList<SpareItem> items, int choice, int keepGems, string sendTo, ISpareSteam steam,
		Func<SpareItem, SpareDecision?> sackSale, SpareRules rules, SpareLedger ledger, Func<DateTime> nowLocal, string account, CancellationToken ct) {
		List<Said> said = [];

		if ((choice <= 1) || !steam.MayAct) {
			return said;
		}

		if (items.Where(static i => (i.Kind == SpareKind.Gems) && i.Tradable).OrderByDescending(static i => i.Amount).FirstOrDefault() is { } stack) {
			int sacks = (int) ((Math.Max(0, (long) stack.Amount - Math.Max(0, keepGems))) / 1000);

			if (sacks > 0) {
				(bool ok, string? why) = await steam.PackSacksAsync(stack, sacks, ct).ConfigureAwait(false);
				said.Add(ok ? new Said("packed {0} gems into {1} sack(s) of gems", sacks * 1000, sacks) : new Said("couldn't pack gems into sacks: {0}", why ?? "-"));
			}
		}

		List<SpareItem> sacksHeld = [.. items.Where(static i => (i.Kind == SpareKind.GemSack) && i.Tradable)];

		if (!steam.MayAct) {
			// its day ended (or you started playing) while it packed - the sacks wait for a later look
		} else if (steam.Held) {
			// Said only when there was something to move: every timed look said it again with no sack in sight.
			if (((choice == 3) && (sacksHeld.Count > 0) && (sendTo.Trim().Length > 0)) || ((choice == 4) && sacksHeld.Any(static s => s.Marketable))) {
				said.Add(Modules.ScamWatch.OnHold(account));
			}
		} else if ((choice == 3) && (sacksHeld.Count > 0) && (sendTo.Trim().Length > 0)) {
			string sent = await steam.SendAsync(sacksHeld, sendTo, ct).ConfigureAwait(false);

			if (sent.Length > 0) {
				said.Add(new Said("sacks of gems: {0}", sent));
			}
		} else if ((choice == 4) && (sacksHeld.FirstOrDefault(static s => s.Marketable) is { } sack) && (sackSale(sack) is { } sale)
			&& ledger.TryReserve(sale.PriceCents, rules.DailyItems, rules.DailyValueCents, nowLocal())) {
			ledger.Save();
			(int listed, string text) = await steam.SellAsync([sale], ct).ConfigureAwait(false);
			GiveBack(ledger, [sale], listed, nowLocal);
			// What it really went up for: priced again in the wallet's currency just before listing, not the plan's figure.
			(string Price, string YouGet) at = steam.ListedFor(sack.AssetId) ?? (Spare.Money(sale.ListCents), Spare.Money(sale.ReceiveCents));
			said.Add(listed > 0 ? new Said("listed a sack of gems for {0}, you get {1}", at.Price, at.YouGet)
				: new Said("couldn't list a sack of gems: {0}", text));
		}

		foreach (Said s in said) {
			Log.Info(s, account);
		}

		return said;
	}

	// ── booster packs ────────────────────────────────────────────────────────
	/// <summary>Open the packs the plan says to, one at a time with a pause, each logged with what came out.</summary>
	public static async Task<int> OpenAsync(IReadOnlyList<Spare.PackDecision> decisions, ISpareSteam steam, string account, int max, CancellationToken ct) {
		int opened = 0;
		HashSet<ulong> handled = [];

		foreach (Spare.PackDecision d in decisions.Where(static d => d.Open)) {
			if ((opened >= max) || !handled.Add(d.Pack.AssetId)) {
				continue;
			}

			if ((handled.Count > 1) && !await steam.PauseAsync(ct).ConfigureAwait(false)) {
				break;
			}

			// A human-mode account's day can end, or you can start playing, between two packs.
			if (!steam.MayAct) {
				break;
			}

			HashSet<ulong> claimed = steam.Claim([d.Pack.AssetId]);

			if (claimed.Count == 0) {
				continue;
			}

			(bool ok, List<string> cards, string? why) result;

			try {
				result = await steam.UnpackAsync(d.Pack, ct).ConfigureAwait(false);
			} finally {
				steam.Release(claimed);
			}

			if (!result.ok) {
				Log.Info(new Said("couldn't open {0}: {1}", d.Pack.Name, result.why ?? "-"), account);

				break;
			}

			opened++;
			Log.Reward(result.cards.Count > 0
				? new Said("opened {0}: {1} - {2}", d.Pack.Name, string.Join(", ", result.cards), d.Why)
				: new Said("opened {0} - Steam didn't say what was in it - {1}", d.Pack.Name, d.Why), account, Topic.Items);
		}

		return opened;
	}

	/// <summary>
	/// Packs kept for a reason "Packs it doesn't open" covers: sent to another of your accounts, or one listed - inside the
	/// spare-items caps, and only after <c>spare ok</c>. Keep (the default) does nothing.
	/// </summary>
	public static async Task<Said?> UnopenedAsync(IReadOnlyList<Spare.PackDecision> decisions, int choice, string sendTo, ISpareSteam steam,
		Func<SpareItem, SpareDecision?> sale, SpareRules rules, SpareLedger ledger, Func<DateTime> nowLocal, CancellationToken ct, string account = "") {
		List<SpareItem> packs = [.. decisions.Where(static d => !d.Open && d.Disposable && d.Pack.Tradable).Select(static d => d.Pack)];

		if ((choice == 0) || (packs.Count == 0) || !ledger.Approved || !steam.MayAct) {
			return null;
		}

		// Nothing to send to, or nothing that could be listed: nothing is held back either, so nothing to say.
		if (((choice == 1) && (sendTo.Trim().Length == 0)) || ((choice != 1) && !packs.Any(static p => p.Marketable))) {
			return null;
		}

		if (steam.Held) {
			return new Said("unopened booster packs: {0}", Modules.ScamWatch.OnHold(account));
		}

		if (choice == 1) {
			string sent = await steam.SendAsync(packs, sendTo, ct).ConfigureAwait(false);

			return sent.Length > 0 ? new Said("unopened booster packs: {0}", sent) : null;
		}

		List<SpareDecision> selling = [];

		foreach (SpareItem pack in packs.Where(static p => p.Marketable)) {
			if (selling.Count >= rules.PerRun) {
				break;
			}

			if ((sale(pack) is { } s) && ledger.TryReserve(s.PriceCents, rules.DailyItems, rules.DailyValueCents, nowLocal())) {
				selling.Add(s);
			}
		}

		ledger.Save();

		if (selling.Count == 0) {
			return null;
		}

		(int listed, string text) = await steam.SellAsync(selling, ct).ConfigureAwait(false);
		GiveBack(ledger, selling, listed, nowLocal);

		return new Said("unopened booster packs: listed {0} - {1}", listed, text);
	}

	// ── one whole look, for the module and the commands ─────────────────────
	/// <summary>What a look found: the plan, the account's currency, and the inventory it was made from.</summary>
	public sealed record Look(SparePlan Plan, int Currency, IReadOnlyList<SpareItem> Items, IReadOnlySet<ulong> Busy, SpareLedger Ledger, SpareRules Rules) {
		public MarketPrice? Price(SpareItem item) => SparePrices.Of(item, Currency);
	}

	/// <summary>
	/// A selling price for one item at today's prices in the account's own wallet currency and the selling price rule, or
	/// null when it has none for certain.
	/// </summary>
	internal static SpareDecision? SaleOf(SpareItem item, int currency, SpareRules rules, Said why) {
		MarketPrice? p = SparePrices.Of(item, currency);

		if ((p is not { Cents: > 0 } price) || (DateTime.UtcNow - price.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))
			|| (ListingPrice.For(price, rules.SellPrice, rules.DumpPct).Cents is not int list)) {
			return null;
		}

		int receive = Seller.ReceiveFor(list);

		return receive > 0 ? new SpareDecision(item, SpareVerdict.Sell, why, ListingPrice.Worth(price), 0, list, receive) : null;
	}

	/// <summary>
	/// Read everything a decision needs - badges, the whole inventory, what's on the market or in a waiting trade - price
	/// what's spare (a few games a look), and decide. A problem instead when any of it couldn't be read: half an answer
	/// is a reason to wait, never to guess.
	/// </summary>
	public static async Task<(Look? Look, string? Problem)> ReadAsync(Bot bot, CancellationToken ct, bool priceThem = true) {
		if (bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid) {
			return (null, "Steam hasn't said this account's wallet currency yet - try again in a minute");
		}

		int currency = (int) bot.WalletCurrency;
		SpareBadges? badges = SpareBadges.Parse(await bot.Web.ApiGetAsync("IPlayerService", "GetBadges", new Dictionary<string, string> {
			["steamid"] = bot.SteamId.ToString(CultureInfo.InvariantCulture)
		}, ct).ConfigureAwait(false));

		if (badges == null) {
			return (null, "Steam wouldn't say what its badges are");
		}

		InventoryContents? inventory = await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory is not { Complete: true }) {
			return (null, inventory == null ? "couldn't read its inventory" : "couldn't read all of its inventory - Steam stopped part way; try again in a while");
		}

		if (await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is not { } promised) {
			return (null, "Steam wouldn't say which items are in a waiting trade - try again in a while");
		}

		if (await Seller.ListingsAsync(bot, ct).ConfigureAwait(false) is not { } listings) {
			return (null, "Steam wouldn't say what's on the market - try again in a while");
		}

		HashSet<ulong> busy = [.. promised, .. listings.Where(static l => l.AssetId != 0).Select(static l => l.AssetId)];
		List<SpareItem> items = Spare.Read(inventory);
		SpareLedger ledger = SpareLedger.Load(bot.Name);
		ledger.Observe(items.Select(static i => i.AssetId), DateTime.UtcNow);
		ledger.Save();

		SpareRules rules = SpareRules.From(bot.Cfg);
		LiveSpareSteam steam = new(bot);
		SparePlan plan = Decide(items, badges, rules, ledger, busy, currency);

		if (priceThem) {
			// The shortlist first: one US-dollar search per game says which of its items anybody sells and which are worth
			// the most - oldest first, a few games a look. Never a price anything is decided or listed at.
			List<uint> games = [.. plan.Decisions.Where(static d => d.Verdict is SpareVerdict.Unpriced or SpareVerdict.Gems or SpareVerdict.Sell or SpareVerdict.Keep or SpareVerdict.OverLimit)
				.Select(static d => d.Item.Game)
				.Concat(bot.Cfg.OpenBoosterPacks == 1 ? items.Where(static i => i.Kind == SpareKind.BoosterPack).Select(static i => i.Game) : [])
				.Distinct()
				.Where(g => Stale(SparePrices.Cached(g)?.When, rules))
				.OrderBy(g => SparePrices.Cached(g)?.At ?? 0)
				.Take(GamesPerLook)];

			foreach (uint game in games) {
				if (!await SparePrices.PriceGameAsync(game, ct).ConfigureAwait(false)) {
					break;   // the market isn't answering - the rest wait
				}
			}

			plan = Decide(items, badges, rules, ledger, busy, currency);

			// Then each spare item waiting on a price, priced in this account's own wallet currency - most valuable first,
			// a few a look. What nobody sells needs no price: it's kept anyway.
			int quoted = 0;

			foreach (SpareItem item in plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Unpriced).Select(static d => d.Item)
				.Where(i => SparePrices.MaySell(i) && Stale(SparePrices.Of(i, currency)?.At, rules)).DistinctBy(static i => i.Hash)
				.OrderByDescending(static i => SparePrices.ShortlistCents(i))) {
				if (quoted++ >= QuotesPerLook(rules)) {
					break;
				}

				if (!await SparePrices.QuoteAsync(item, currency, ct).ConfigureAwait(false)) {
					break;
				}
			}

			// Best of both weighs gems by a sack's price, and selling the sacks needs it too - in this account's currency.
			if (((rules.Action == 0) || (GemsChoice(bot.Cfg) == 4)) && Stale(SparePrices.Sack(currency)?.At, rules)) {
				await SparePrices.PriceSackAsync(currency, ct).ConfigureAwait(false);
			}

			plan = Decide(items, badges, rules, ledger, busy, currency);

			if (rules.Action is 0 or 1) {
				int asked = 0;

				foreach (SpareItem item in plan.Decisions.Where(static d => d.Verdict is SpareVerdict.Unpriced or SpareVerdict.Gems or SpareVerdict.Sell or SpareVerdict.Keep)
					.Select(static d => d.Item).Where(static i => (i.GooType > 0) && (SteamGems.KnownValue(i) == null)).DistinctBy(static i => i.GooKey)) {
					if (asked++ >= GemValuesPerLook) {
						break;
					}

					await steam.GemValueAsync(item, ct).ConfigureAwait(false);
				}
			}

			plan = Decide(items, badges, rules, ledger, busy, currency);
		}

		return (new Look(plan, currency, items, busy, ledger, rules), null);
	}

	private static bool Stale(DateTime? at, SpareRules rules) => (at == null) || (DateTime.UtcNow - at.Value > TimeSpan.FromHours(rules.PriceMaxAgeHours * 0.8));

	/// <summary>Items priced one by one in the wallet's currency in one look: twice what one look may do, 4 to 20.</summary>
	private static int QuotesPerLook(SpareRules rules) => Math.Clamp(rules.PerRun * 2, 4, 20);

	private static SparePlan Decide(IReadOnlyList<SpareItem> items, SpareBadges badges, SpareRules rules, SpareLedger ledger, IReadOnlySet<ulong> busy,
		int currency) => Spare.Plan(new SpareInput {
			Items = items,
			Badges = badges,
			Rules = rules,
			Received = ledger.Received,
			Busy = busy,
			Price = i => SparePrices.Of(i, currency),
			Currency = currency,
			Sack = SparePrices.Sack(currency),
			GemValue = static i => i.GooType > 0 ? SteamGems.KnownValue(i) : 0
		});

	/// <summary>A look, and - when it may act - doing it. Returns what it would do (preview) or did, in English, for a command.</summary>
	public static async Task<string> RunAsync(Bot bot, bool act, CancellationToken ct) {
		(Look? look, string? problem) = await ReadAsync(bot, ct).ConfigureAwait(false);

		if (look == null) {
			return $"{bot.Name}: {problem}";
		}

		StringBuilder sb = new(Preview(bot.Name, look));

		if (!act) {
			return sb.ToString();
		}

		if (!look.Ledger.Approved) {
			PostPreviewOnce(bot.Name, look);

			return sb.ToString();
		}

		LiveSpareSteam steam = new(bot, () => StillMayAct(bot, SpareGate(bot)));
		Done done = await ActAsync(look.Plan, look.Rules, look.Ledger, steam, bot.Name, static () => DateTime.Now, ct).ConfigureAwait(false);
		sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{bot.Name}: done - {done.GemItems} item(s) into {done.Gems} gems, {done.Sold} listed");

		if (done.Stopped is { } why) {
			sb.Append(" (").Append(why.ToEnglish()).Append(')');
		}

		if (done.SellSaid is { Length: > 0 } sold) {
			sb.AppendLine().Append(sold);
		}

		// Gems afterwards: from the inventory as it was - packed gems and sacks are read again next look.
		List<Said> gems = await GemsAfterAsync(look.Items, GemsChoice(bot.Cfg), Math.Max(0, bot.Cfg.SpareKeepGems), bot.Cfg.SpareGemsSendTo, steam,
			s => SaleOf(s, look.Currency, look.Rules, new Said("sack of gems")), look.Rules, look.Ledger, static () => DateTime.Now, bot.Name, ct).ConfigureAwait(false);

		foreach (Said s in gems) {
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{bot.Name}: {s.ToEnglish()}");
		}

		// Selling, where it's chosen: its own week-old listings the market has gone under come down, and are listed again at
		// today's price. Only listings nocat.farm put up itself - never one you listed by hand.
		if ((look.Rules.Action is 0 or 2) && steam.MayAct && !steam.Held) {
			await Seller.RelistAsync(bot, ct, Math.Clamp(bot.Cfg.SpareRelistDays, 1, 60), oursOnly: true).ConfigureAwait(false);
		}

		return sb.ToString();
	}

	/// <summary>The limits a run keeps to, in this account's own wallet currency - for the confirm popup and 'set'.</summary>
	public static string Limits(Bot bot) {
		SpareRules r = SpareRules.From(bot.Cfg);
		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

		string limits = $"Limits: at most {r.PerRun} item(s) a look and {r.DailyItems} a day, {Spare.Money(r.DailyValueCents, currency)} in value a day; never anything worth more than {Spare.Money(r.MaxPriceCents, currency)}, nothing that came in within {r.NewDays} day(s), never a card a badge still needs. Prices in this account's own wallet currency{(currency == 0 ? " (not known yet - nothing is priced or sold until it is)" : "")}.";

		// What else turning it on starts, by the account's other settings: packs it doesn't open and gem sacks leave too.
		string also = (Math.Clamp(bot.Cfg.BoosterUnopened, 0, 2) switch {
			1 => $"{Environment.NewLine}Also: booster packs it doesn't open are sent to {Named(bot.Cfg.BoosterSendTo)}.",
			2 => $"{Environment.NewLine}Also: booster packs it doesn't open are sold, within these limits.",
			_ => ""
		}) + (GemsChoice(bot.Cfg) switch {
			3 => $"{Environment.NewLine}Also: gems are packed into sacks of 1000 and the sacks sent to {Named(bot.Cfg.SpareGemsSendTo)}.",
			4 => $"{Environment.NewLine}Also: gems are packed into sacks of 1000 and one sack is sold each look, within these limits.",
			_ => ""
		});

		return limits + also;

		static string Named(string account) => string.IsNullOrWhiteSpace(account) ? "(no account set yet - nothing is sent until one is)" : account.Trim();
	}

	/// <summary>What a look would do, from the prices already held - quick, for the confirm popup. English.</summary>
	public static async Task<string> QuickPreviewAsync(Bot bot, CancellationToken ct) {
		(Look? look, string? problem) = await ReadAsync(bot, ct, priceThem: false).ConfigureAwait(false);

		return look == null ? $"{bot.Name}: {problem}" : Preview(bot.Name, look);
	}

	/// <summary>The first look on an account only shows what it would do, in the log and your chats - once, until you say ok.</summary>
	public static void PostPreviewOnce(string account, Look look) {
		if (look.Ledger.PreviewPosted) {
			return;
		}

		SparePlan plan = look.Plan;
		Log.Attention(new Said("spare items preview for {0}: {1} to gems, {2} to sell, {3} left for you to decide. Nothing happens until you turn Spare items on in its settings - 'spare preview {0}' shows each item and why.",
			account, plan.Count(SpareVerdict.Gems), plan.Count(SpareVerdict.Sell), plan.Count(SpareVerdict.OverLimit)), account, Topic.Items);
		look.Ledger.PreviewPosted = true;
		look.Ledger.Save();
	}

	/// <summary>Exactly what it would do and why, in English (a command's answer).</summary>
	public static string Preview(string account, Look look) {
		SparePlan plan = look.Plan;
		SpareRules r = look.Rules;
		StringBuilder sb = new();
		sb.Append(CultureInfo.InvariantCulture, $"{account}: spare items - {plan.Count(SpareVerdict.Gems)} to gems, {plan.Count(SpareVerdict.Sell)} to sell, ")
			.Append(CultureInfo.InvariantCulture, $"{plan.Count(SpareVerdict.OverLimit)} left for you to decide, {plan.Count(SpareVerdict.Unpriced)} waiting for a price")
			.Append(r.On ? "" : " (Spare items is off for this account - this is only a look)");

		void Lines(string title, IEnumerable<SpareDecision> list, int max = 15) {
			List<SpareDecision> all = [.. list];

			if (all.Count == 0) {
				return;
			}

			sb.AppendLine().Append("  ").Append(title).Append(':');

			foreach (SpareDecision d in all.Take(max)) {
				sb.AppendLine().Append(CultureInfo.InvariantCulture, $"    {Columns.Fit(d.Item.Name, 28)} {Columns.Fit(GameNames.Of(d.Item.Game), 22)} {d.Why.ToEnglish()}");
			}

			if (all.Count > max) {
				sb.AppendLine().Append(CultureInfo.InvariantCulture, $"    ...and {all.Count - max} more");
			}
		}

		List<SpareDecision> next = [.. plan.Actions.Take(r.PerRun)];
		Lines($"next {next.Count} it would do (at most {r.PerRun} a look)", next);
		Lines("left for you to decide", plan.Decisions.Where(static d => d.Verdict == SpareVerdict.OverLimit).OrderByDescending(static d => d.PriceCents));
		Lines("spare but kept", plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Keep));

		// The rest only as counts, by reason.
		foreach (IGrouping<string, SpareDecision> g in plan.Decisions.Where(static d => d.Verdict is SpareVerdict.NotSpare or SpareVerdict.Protected or SpareVerdict.Unpriced)
			.GroupBy(static d => d.Why.ToEnglish()).OrderByDescending(static g => g.Count())) {
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  {g.Count()} kept: {g.Key}");
		}

		SpareLedger l = look.Ledger;
		bool today = l.Day == DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  today: {(today ? l.DoneItems : 0)} of {r.DailyItems} item(s), {Spare.Money(today ? l.DoneCents : 0)} of {Spare.Money(r.DailyValueCents)} in value");
		sb.AppendLine().Append(l.Approved
			? $"  'spare do {account}' does the next ones now."
			: $"  Nothing happens on {account} until Spare items is turned on.");

		return sb.ToString();
	}

	// ── booster packs, a whole look ──────────────────────────────────────────
	/// <summary>Decide every sealed pack - and open them when <paramref name="act"/>. An English answer, for a command.</summary>
	public static async Task<string> PacksAsync(Bot bot, bool act, CancellationToken ct) {
		(Look? look, string? problem) = await ReadAsync(bot, ct, priceThem: false).ConfigureAwait(false);

		if (look == null) {
			return $"{bot.Name}: {problem}";
		}

		BotConfig cfg = bot.Cfg;
		int choice = Math.Clamp(cfg.OpenBoosterPacks, 0, 2);
		List<uint> games = [.. look.Items.Where(static i => i.Kind == SpareKind.BoosterPack).Select(static i => i.Game).Distinct()
			.Where(g => Stale(SparePrices.Cached(g)?.When, look.Rules)).Take(GamesPerLook)];

		if ((choice == 1) || (cfg.BoosterUnopened == 2)) {
			foreach (uint game in games) {
				if (!await SparePrices.PriceGameAsync(game, ct).ConfigureAwait(false)) {
					break;
				}
			}
		}

		// Whether a pack is worth more than its cards: both sides from one US-dollar shortlist, so the answer doesn't depend
		// on the currency. What one is LISTED at is priced in this account's own currency, below.
		List<Spare.PackDecision> packs = Spare.PlanPacks(look.Items, choice, Math.Clamp(cfg.BoosterOpenNewDays, 0, 30), cfg.BoosterNeverOpen,
			look.Ledger.Received, look.Busy, SparePrices.Pack, look.Rules.PriceMaxAgeHours, DateTime.UtcNow);

		if (act && (cfg.BoosterUnopened == 2)) {
			int quoted = 0;

			foreach (SpareItem pack in packs.Where(static p => !p.Open && p.Disposable && p.Pack.Tradable && p.Pack.Marketable).Select(static p => p.Pack)
				.DistinctBy(static p => p.Hash).Where(p => Stale(SparePrices.Of(p, look.Currency)?.At, look.Rules))) {
				if ((quoted++ >= look.Rules.PerRun) || !await SparePrices.QuoteAsync(pack, look.Currency, ct).ConfigureAwait(false)) {
					break;
				}
			}
		}

		StringBuilder sb = new();
		sb.Append(CultureInfo.InvariantCulture, $"{bot.Name}: {packs.Count} booster pack(s) - {packs.Count(static p => p.Open)} to open");

		foreach (Spare.PackDecision p in packs.Take(25)) {
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  {(p.Open ? "open" : "keep")}  {Columns.Fit(p.Pack.Name, 36)} {p.Why.ToEnglish()}");
		}

		if (packs.Count > 25) {
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  ...and {packs.Count - 25} more");
		}

		if (!act) {
			if (packs.Any(static p => p.Open)) {
				sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  'booster open {bot.Name} do' opens them now.");
			}

			return sb.ToString();
		}

		LiveSpareSteam steam = new(bot, () => StillMayAct(bot, PacksGate(bot)));
		// A human-mode account opens a few at a time, like somebody clicking through them; the others all of them.
		int opened = await OpenAsync(packs, steam, bot.Name, cfg.LegitMode ? Math.Clamp(cfg.SparePerRun, 1, 50) : 50, ct).ConfigureAwait(false);
		sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{bot.Name}: opened {opened}");

		Said? unopened = await UnopenedAsync(packs, Math.Clamp(cfg.BoosterUnopened, 0, 2), cfg.BoosterSendTo, steam,
			p => SaleOf(p, look.Currency, look.Rules, new Said("worth more sealed")), look.Rules, look.Ledger, static () => DateTime.Now, ct, bot.Name).ConfigureAwait(false);

		if (unopened is { } u) {
			Log.Info(u, bot.Name);
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{bot.Name}: {u.ToEnglish()}");
		}

		return sb.ToString();
	}
}
