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
	private const int GamesPerLook = 20;

	/// <summary>Gem values asked for in one look.</summary>
	private const int GemValuesPerLook = 20;

	/// <summary>What one go did. <paramref name="Refused"/>: 1.8.2 - Steam refused a listing, and the look ended there.</summary>
	public sealed record Done(int GemItems, int Gems, int Sold, string? SellSaid, Said? Stopped, bool Refused = false);

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

		// One message for the whole look, when it ends - however it ends (see ItemsNote).
		ItemsNote note = new(account);

		string? sold = null;
		int listed = 0;
		bool limited = false;   // a look stopped by today's limit - see the end of the pass
		bool refused = false;   // Steam refused a listing: the look ends there

		// Passes over the plan: one, unless sales held back at the market free slots today's limit was short of.
		while (true) {
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
					limited = false;

					break;
				}

				// 'spare hold' typed while the look works: nothing more from this one.
				if (Hold(ledger, account) is { } hold) {
					stopped = hold;
					limited = false;

					break;
				}

				// Held by scam watch: nothing goes on the market, and nothing is counted against today's caps for it.
				if ((d.Verdict == SpareVerdict.Sell) && steam.Held) {
					stopped = Modules.ScamWatch.OnHold(account);

					continue;
				}

				// 1.8.2: Steam's market blocked for the account (or listing paused after a refusal): the same - kept, not counted.
				if ((d.Verdict == SpareVerdict.Sell) && (steam.MarketHeld is { } market)) {
					stopped = market;

					continue;
				}

				// Checked and counted in one step: a look and a command at the same moment can't both fit under the cap.
				if (!ledger.TryReserve(d.PriceCents, rules.DailyItems, rules.DailyValueCents, nowLocal())) {
					// Not done, so not handled: a sale held back further down frees its slot, and this one gets it.
					handled.Remove(d.Item.AssetId);
					limited = true;

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
					limited = false;

					break;
				}

				// Again after the wait between items.
				if (!steam.MayAct) {
					ledger.Unreserve(d.PriceCents, nowLocal());
					stopped = HumanStopped;
					limited = false;

					break;
				}

				if (Hold(ledger, account) is { } held) {
					ledger.Unreserve(d.PriceCents, nowLocal());
					stopped = held;
					limited = false;

					break;
				}

				// The settings as they are now, right before it goes: turned off, paused, on the keep list, over the price limit.
				if (steam.StillOk(d.Item, d.PriceCents) is { } no) {
					ledger.Unreserve(d.PriceCents, nowLocal());

					if (no.All) {
						stopped = no.Why;
						limited = false;

						break;
					}

					Log.Info(new Said("{0} ({1}): {2}", d.Item.Name, GameNames.Of(d.Item.Game), no.Why), account);

					continue;
				}

				// 1.8.2: an item that had no price at all (nobody selling it, no sale lately) is priced now, in the wallet's money,
				// before it becomes gems: no answer, worth more than the price limit, or selling now paying more than its gems -
				// it's kept.
				if (await steam.BeforeGemsAsync(d, ct).ConfigureAwait(false) is { } wait) {
					ledger.Unreserve(d.PriceCents, nowLocal());
					Log.Info(new Said("{0} ({1}): {2}", d.Item.Name, GameNames.Of(d.Item.Game), wait), account);

					continue;
				}

				HashSet<ulong> claimed = steam.Claim([d.Item.AssetId]);

				if (claimed.Count == 0) {
					ledger.Unreserve(d.PriceCents, nowLocal());

					continue;   // being sent or sold this very moment
				}

				(bool ok, int got, string? why) result;

				// Why, in the debug log only: the line you read is the item and its gems ("Metro (The Division) → 80 gems").
				Log.Debug(new Said("turning {0} ({1}) into gems - {2}", d.Item.Name, GameNames.Of(d.Item.Game), d.Why), account);

				try {
					result = await steam.GrindAsync(d.Item, d.GemValue, ct).ConfigureAwait(false);

					// No clear yes - no answer, a timeout, an error: the inventory says whether it went through all the same,
					// before it's called a failure. Asked while the item is still claimed, so nothing else touches it meanwhile.
					if (!result.ok && (await steam.GemsIfGoneAsync(d.Item, d.GemValue, ct).ConfigureAwait(false) is int landed)) {
						Log.Debug(new Said("Steam's answer about {0} wasn't a yes ({1}) - but it's gone from the inventory and the gems are in, so it's done", d.Item.Name, result.why ?? "-"), account);
						result = (true, landed, null);
					}
				} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
					// Steam never said it was done (the connection dropped): the inventory decides, as above. Not there: not
					// counted against today's caps, and the error comes through.
					int? landed = null;

					try {
						landed = await steam.GemsIfGoneAsync(d.Item, d.GemValue, ct).ConfigureAwait(false);
					} catch (Exception again) when (again is not OperationCanceledException || !ct.IsCancellationRequested) {
						Log.Debug(new Said("couldn't check whether {0} went through: {1}", d.Item.Name, Log.Describe(again)), account);
					}

					if (landed is not int got) {
						ledger.Unreserve(d.PriceCents, nowLocal());
						ledger.Save();
						steam.Release(claimed);
						note.Send();

						throw;
					}

					Log.Debug(new Said("Steam's answer about {0} wasn't a yes ({1}) - but it's gone from the inventory and the gems are in, so it's done", d.Item.Name, Log.Describe(e)), account);
					result = (true, got, null);
				} catch {
					// Closing: not counted against today's caps.
					ledger.Unreserve(d.PriceCents, nowLocal());
					ledger.Save();
					steam.Release(claimed);
					note.Send();

					throw;
				}

				steam.Release(claimed);
				done++;

				if (!result.ok) {
					// Given back: a failure never counts against today's item or value limit.
					ledger.Unreserve(d.PriceCents, nowLocal());
					Log.Info(new Said("couldn't turn {0} into gems: {1}", d.Item.Name, result.why ?? "-"), account);
					stopped = new Said("Steam refused one - trying again next time");
					limited = false;

					break;   // the next would be refused for the same reason
				}

				ledger.Save();
				gemItems++;
				gems += result.got;
				note.Gems(d.Item, result.got);
				Log.Earned(GemsSaid(d.Item, result.got), account);
			}

			int reserved = toSell.Count, listedNow = 0;

			// The first listing waits after the last gems too, as each item waits after the one before.
			if ((toSell.Count > 0) && (gemItems > 0) && !await steam.PauseAsync(ct).ConfigureAwait(false)) {
				foreach (SpareDecision d in toSell) {
					ledger.Unreserve(d.PriceCents, nowLocal());
				}

				toSell.Clear();
				limited = false;   // the wait was cut short: the look ends here
			}

			if ((toSell.Count > 0) && (!steam.MayAct || (Hold(ledger, account) != null))) {
				// Nothing listed after all: what was counted for it goes back.
				foreach (SpareDecision d in toSell) {
					ledger.Unreserve(d.PriceCents, nowLocal());
				}

				toSell.Clear();
				stopped = Hold(ledger, account) ?? HumanStopped;
				limited = false;
			}

			if (toSell.Count > 0) {
				foreach (SpareDecision d in toSell) {
					// The line you read is the listing itself ("listed X (game) for ..."), once it's up - see Seller.
					Log.Debug(new Said("selling {0} ({1}) - {2}", d.Item.Name, GameNames.Of(d.Item.Game), d.Why), account);
				}

				(listedNow, string said) = await steam.SellAsync(toSell, ct).ConfigureAwait(false);
				sold = sold == null ? said : $"{sold}{Environment.NewLine}{said}";
				listed += listedNow;
				GiveBack(ledger, toSell, listedNow, nowLocal);

				// 1.8.2: Steam refused a listing - the look ends here. Its slots went back above, and a second pass only listed
				// the same way into the same refusal, up to six times a look.
				if (steam.Refused != null) {
					refused = true;
					limited = false;
					stopped = new Said("Steam refused a listing - nothing more listed this look");
				}

				foreach (SpareDecision d in toSell) {
					if (steam.ListedFor(d.Item.AssetId) is { } at) {
						note.Listed(d.Item, at.Price, steam.ListedCents(d.Item.AssetId));
					}
				}
			}

			// Sales held back at the market (kept at today's price, refused, stopped) gave their slots back above: an item
			// today's limit stopped can have one, in this same look. Live: two held-back sales kept 2 of 50, and the look ended
			// "today's limit of 50 item(s) is reached" with nothing done.
			int freed = reserved - Math.Clamp(listedNow, 0, reserved);
			done -= freed;
			toSell.Clear();

			if (!limited || (freed <= 0)) {
				break;
			}

			limited = false;
			stopped = null;
		}

		ledger.Save();

		note.Send();

		return new Done(gemItems, gems, listed, sold, stopped, refused);
	}

	/// <summary>An item turned into gems, as the log says it: "Metro (Tom Clancy's The Division) → 80 gems" - why is a DEBUG line.</summary>
	public static Said GemsSaid(SpareItem item, int gems) => new(LogStack.GemsLine, item.Name, GameNames.Of(item.Game), gems);

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
	/// 1.8.2: why the account must stop now, asked right before each turn into gems, listing or send - a look takes minutes
	/// and the owner can change things meanwhile: Spare items turned off, the account paused, or scam watch holding it. Null
	/// while it may go on.
	/// </summary>
	public static Said? Halted(Bot bot) =>
		!Switches.SpareOn(bot.Cfg, bot.Name) ? new Said("Spare items was turned off on {0} - stopped before the next item", bot.Name)
			: bot.Paused ? new Said("{0} was paused - stopped before the next item", bot.Name)
			: Modules.ScamWatch.HeldBack(bot) != null ? Modules.ScamWatch.OnHold(bot.Name)
			: null;

	/// <summary>
	/// 1.8.2: whether this one item may still go, by the settings as they are NOW, not as they were when the look planned
	/// it: <see cref="Halted"/> (All: the look stops), then the keep list (items and games), then - for cards, backgrounds
	/// and emoticons - the price limit against <paramref name="worthCents"/>, the freshest price known. Null when it may go.
	/// </summary>
	public static (Said Why, bool All)? StillOk(Bot bot, SpareItem item, int worthCents) {
		if (Halted(bot) is { } halted) {
			return (halted, true);
		}

		SpareRules now = SpareRules.From(bot.Cfg);

		if (now.KeepGames.Contains(item.Game) || now.KeepNames.Contains(item.Name) || now.KeepNames.Contains(item.Hash)) {
			return (new Said("on your keep list now - skipped"), false);
		}

		if ((item.Kind is SpareKind.Card or SpareKind.FoilCard or SpareKind.Background or SpareKind.Emoticon or SpareKind.BoosterPack) && (worthCents > now.MaxPriceCents)) {
			return (new Said("worth {0}, over your price limit now - skipped", Spare.Money(worthCents, (int) bot.WalletCurrency)), false);
		}

		return null;
	}

	/// <summary>
	/// 1.8.2: whether an item about to become gems may, with its price read again right before (<paramref name="now"/>, in the
	/// wallet's money): no answer - kept until it has one; worth more than the price limit - kept; selling now paying at least
	/// a cent more than its gems (<see cref="Spare.SellingPays"/>) - kept, no fresh sack price to tell - kept. Null when it
	/// may become gems. Asked for an item the look had no price for at all, and one whose price was only an estimate from the
	/// dollar search or over an hour old (<see cref="NeedsPriceBeforeGems"/>) - a single dumped listing on the search made a
	/// 0.30 card look like gems. Ones that can't go to the market aren't asked about.
	/// </summary>
	public static Said? BeforeGems(SpareDecision d, MarketQuote? now, MarketPrice? sack, SpareRules rules, int currency, DateTime utcNow) {
		if (!d.Item.Sellable || (d.Verdict != SpareVerdict.Gems)) {
			return null;
		}

		if ((now == null) || (now.Currency != currency)) {
			return new Said("no price right now - kept until it has one");
		}

		MarketPrice price = now.Price;
		int worth = ListingPrice.Worth(price);

		if (worth <= 0) {
			return null;   // still nobody selling it, and no sale lately: gems
		}

		if (worth > rules.MaxPriceCents) {
			return new Said("worth {0} now - over your price limit, kept", Spare.Money(worth, currency));
		}

		// What selling would pay now: at the price it would list for, or - with no sure price - the most it's worth, so it's
		// never turned into gems on a low guess.
		int sale = price.Cents > 0 && (ListingPrice.For(price, rules.SellPrice, rules.DumpPct, 0, rules.FloorPct).Cents is int list) ? Seller.ReceiveFor(list) : Seller.ReceiveFor(worth);

		if ((sale <= 0) || (d.GemValue <= 0)) {
			return null;
		}

		if ((sack is not { Cents: > 0 } s) || (utcNow - s.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))) {
			return new Said("the price of gems isn't known yet - kept");
		}

		decimal gems = Spare.GemsWorth(d.GemValue, s.Cents);

		return Spare.SellingPays(gems, sale)
			? new Said("selling now pays {0}, more than its {1} gems are worth ({2}) - kept", Spare.Money(sale, currency), d.GemValue, Spare.Money(gems, currency))
			: null;
	}

	/// <summary>
	/// 1.8.2: what an unsold listing's gems are worth, for stepping it down (<see cref="SpareListings.StepTo"/>) - by a copy of
	/// the same item still in the inventory, its gem value as Steam said it, at a sack's price no older than "Trust a price
	/// for". Null when not known, or with "sell only" (gems aren't weighed then).
	/// </summary>
	public static Func<string, decimal?>? StepGems(IReadOnlyList<SpareItem> items, SpareRules rules, int currency) {
		if ((rules.Action != 0) || (SparePrices.SackPays(currency, rules.DumpPct) is not { Cents: > 0 } sack) || (DateTime.UtcNow - sack.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))) {
			return null;
		}

		Dictionary<string, SpareItem> byHash = new(StringComparer.Ordinal);

		foreach (SpareItem i in items.Where(static i => i.GooType > 0)) {
			byHash.TryAdd(i.Hash, i);
		}

		return hash => byHash.TryGetValue(hash, out SpareItem? item) && (SteamGems.KnownValue(item) is int goo and > 0) ? Spare.GemsWorth(goo, sack.Cents) : null;
	}

	/// <summary>
	/// 1.8.2: whether an item about to become gems has its price read again first (<see cref="BeforeGems"/>): one that can go to
	/// the market, unless its own price in the wallet's money was read in the last hour.
	/// </summary>
	public static bool NeedsPriceBeforeGems(SpareDecision d, int currency, DateTime utcNow) =>
		d.Item.Sellable && (d.Verdict == SpareVerdict.Gems)
		&& ((d.PriceCents <= 0) || (MarketQuotes.Of(d.Item.Hash, currency) is not { } own) || (utcNow - own.When > Modules.GemsChoice.ActFresh) || (utcNow < own.When.AddMinutes(-5)));

	/// <summary>
	/// 1.8.2: each listing by the settings as they are right then, not as they were when the look planned it: the keep list
	/// first (<see cref="StillOk"/>), then priced by <paramref name="priced"/> with the rules read again for this one - so
	/// the price limit is the one set now, against the fresh price. Turned off, paused or held stops the listing loop itself.
	/// </summary>
	public static Seller.Reprice Guarded(Bot bot, IReadOnlyList<SpareDecision> items, Func<SpareRules, Seller.Reprice> priced) {
		Dictionary<ulong, SpareItem> byAsset = [];

		foreach (SpareDecision d in items) {
			byAsset[d.Item.AssetId] = d.Item;
		}

		return (offer, ct) => byAsset.TryGetValue(offer.AssetId, out SpareItem? item) && (StillOk(bot, item, 0) is { All: false } no)
			? Task.FromResult<(Seller.Offer?, Said)>((null, no.Why))
			: priced(SpareRules.From(bot.Cfg))(offer, ct);
	}

	/// <summary>
	/// How each spare-items listing is priced just before it goes up: again from the market, in the account's own wallet
	/// currency, by the selling price setting. Kept instead when it can't be priced for certain, when a card, background or
	/// emoticon is now worth more than the price limit, or when it's now worth more than was counted for it against the
	/// day's value cap.
	/// </summary>
	/// <param name="stepOf">The step an unsold listing of ours was given, by market name - it goes up at that while the market
	/// hasn't gone up since (see <see cref="SpareListings.Capped"/>).</param>
	/// <param name="stepGone">The market went up since that step, by market name: the step is forgotten.</param>
	/// <param name="ledger">Today's caps: a price that went up since it was counted is listed while their value has room
	/// for it (<see cref="Fits"/>). None: only at or under what was counted.</param>
	/// <param name="oursOf">Our own cheapest listing of an item still up, by market name (0 none): "a cent under the cheapest"
	/// keeps level with it when it's the cheapest (see <see cref="OurListings.UpFor"/>).</param>
	/// <param name="sack">1.8.2: a sack of gems' price, as the look weighs gems by: with "best of gems or selling", an item
	/// with gems is listed only when today's price pays at least a cent more than its gems are worth - else it's kept, and
	/// the next look turns it into gems by the same rule. No fresh sack price: kept.</param>
	/// <param name="paidOf">1.8.2: the most paid for one copy of a card bought for a badge, by market name (0: none) - a copy of it is
	/// never listed to pay less (buying cards' list, config/state/buy-&lt;account&gt;.json).</param>
	public static Seller.Reprice Repricer(IReadOnlyList<SpareDecision> items, SpareRules rules, int currency, Func<string, SpareListings.Step?>? stepOf = null,
		Action<string>? stepGone = null, SpareLedger? ledger = null, Func<string, int>? oursOf = null, MarketPrice? sack = null, Func<string, long>? paidOf = null) {
		Dictionary<ulong, SpareDecision> byAsset = [];

		foreach (SpareDecision d in items) {
			byAsset[d.Item.AssetId] = d;
		}

		return (offer, ct) => {
			SpareDecision? d = byAsset.GetValueOrDefault(offer.AssetId);
			bool limited = d?.Item.Kind is SpareKind.Card or SpareKind.FoilCard or SpareKind.Background or SpareKind.Emoticon or SpareKind.BoosterPack;
			int counted = d?.PriceCents ?? 0;
			decimal? gemsWorth = null;

			if ((rules.Action == 0) && (d?.GemValue is int gems and > 0)) {
				if ((sack is not { Cents: > 0 } s) || (DateTime.UtcNow - s.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))) {
					return Task.FromResult<(Seller.Offer?, Said)>((null, new Said("the price of gems isn't known yet - kept")));
				}

				gemsWorth = Spare.GemsWorth(gems, s.Cents);
			}

			return Seller.RepriceAsync(offer, currency, rules.SellPrice, rules.DumpPct, limited ? rules.MaxPriceCents : null, worth => Fits(ledger, rules, counted, worth, DateTime.Now), ct,
				stepOf?.Invoke(offer.Hash), (int) Math.Clamp(Math.Max(rules.MinSaleCents, paidOf?.Invoke(offer.Hash) ?? 0), 0, int.MaxValue), rules.FloorPct,
				stepGone == null ? null : () => stepGone(offer.Hash), oursOf?.Invoke(offer.Hash) ?? 0, gemsWorth);
		};
	}

	/// <summary>
	/// Whether an item counted at <paramref name="counted"/> may still be listed at <paramref name="worth"/>: at or under,
	/// always; over, when today's value limit has room for the difference - which is then counted. The price limit is
	/// checked on its own, before this (<see cref="Seller.RepriceAsync"/>).
	/// </summary>
	public static bool Fits(SpareLedger? ledger, SpareRules rules, int counted, int worth, DateTime nowLocal) =>
		(worth <= counted) || ((ledger != null) && ledger.TryRaise(worth - counted, rules.DailyValueCents, nowLocal));

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
	/// sold, as chosen. Untradable gems stay loose - a sack of them couldn't go anywhere either. Nothing at all while Spare
	/// items is off on the account, scam watch holds it, or human mode says not now - asked again before each step, since a
	/// human-mode wait is minutes. On a human-mode account each step counts against the look's number and waits its gap
	/// (<paramref name="budget"/>).
	/// </summary>
	public static async Task<List<Said>> GemsAfterAsync(IReadOnlyList<SpareItem> items, int choice, int keepGems, string sendTo, ISpareSteam steam,
		Func<SpareItem, SpareDecision?> sackSale, SpareRules rules, SpareLedger ledger, Func<DateTime> nowLocal, string account, CancellationToken ct,
		LookBudget? budget = null) {
		List<Said> said = [];
		List<SpareItem> sacksHeld = [.. items.Where(static i => (i.Kind == SpareKind.GemSack) && i.Tradable)];

		if ((choice <= 1) || !ledger.Approved || !steam.MayAct) {
			return said;
		}

		if (steam.Held) {
			// Said only when there was something to move: every timed look said it again with no sack in sight.
			if (((choice == 3) && (sacksHeld.Count > 0) && (sendTo.Trim().Length > 0)) || ((choice == 4) && sacksHeld.Any(static s => s.Marketable))) {
				said.Add(Modules.ScamWatch.OnHold(account));
			}

			return Told(said, account);
		}

		// The look's number, the wait before it, then whether it may still go: off, held, or human mode's day over.
		// 1.8.2: and the settings as they are now (turned off, paused, the keep list) - right before it goes.
		async Task<bool> MayGo(SpareItem item) => ((budget == null) || await budget.TakeAsync(steam.PauseAsync, ct).ConfigureAwait(false))
			&& ledger.Approved && steam.MayAct && !steam.Held && (steam.StillOk(item, 0) is null);

		if (items.Where(static i => (i.Kind == SpareKind.Gems) && i.Tradable).OrderByDescending(static i => i.Amount).FirstOrDefault() is { } stack) {
			int sacks = (int) ((Math.Max(0, (long) stack.Amount - Math.Max(0, keepGems))) / 1000);

			if (sacks > 0) {
				if (!await MayGo(stack).ConfigureAwait(false)) {
					return Told(said, account);
				}

				(bool ok, string? why) = await steam.PackSacksAsync(stack, sacks, ct).ConfigureAwait(false);
				said.Add(ok ? new Said("packed {0} gems into {1} sack(s) of gems", sacks * 1000, sacks) : new Said("couldn't pack gems into sacks: {0}", why ?? "-"));
			}
		}

		if ((choice == 3) && (sacksHeld.Count > 0) && (sendTo.Trim().Length > 0)) {
			if (await MayGo(sacksHeld[0]).ConfigureAwait(false)) {
				string sent = await steam.SendAsync(sacksHeld, sendTo, ct).ConfigureAwait(false);

				if (sent.Length > 0) {
					said.Add(new Said("sacks of gems: {0}", sent));
				}
			}
		} else if ((choice == 4) && (sacksHeld.FirstOrDefault(static s => s.Marketable) is { } sack) && (sackSale(sack) is { } sale) && await MayGo(sack).ConfigureAwait(false)
			&& ledger.TryReserve(sale.PriceCents, rules.DailyItems, rules.DailyValueCents, nowLocal())) {
			ledger.Save();
			(int listed, string text) = await steam.SellAsync([sale], ct).ConfigureAwait(false);
			GiveBack(ledger, [sale], listed, nowLocal);
			// What it really went up for: priced again in the wallet's currency just before listing, not the plan's figure.
			(string Price, string YouGet) at = steam.ListedFor(sack.AssetId) ?? (Spare.Money(sale.ListCents), Spare.Money(sale.ReceiveCents));
			said.Add(listed > 0 ? new Said("listed a sack of gems for {0}, you get {1}", at.Price, at.YouGet)
				: new Said("couldn't list a sack of gems: {0}", text));
		}

		return Told(said, account);
	}

	/// <summary>Each line logged, then the lines.</summary>
	private static List<Said> Told(List<Said> said, string account) {

		foreach (Said s in said) {
			Log.Info(s, account);
		}

		return said;
	}

	/// <summary>
	/// Why what follows a look's items (gems into sacks, a sack sent or listed, an unsold listing taken down) must not
	/// happen now - Spare items turned off, scam watch holding the account, human mode's day over or you playing - or null.
	/// </summary>
	internal static Said? AfterStop(SpareLedger ledger, ISpareSteam steam, string account) =>
		Hold(ledger, account) ?? (!steam.MayAct ? HumanStopped : steam.Held ? Modules.ScamWatch.OnHold(account) : null);

	// ── booster packs ────────────────────────────────────────────────────────
	/// <summary>
	/// Open the packs the plan says to, one at a time with a pause, each logged with what came out - and told as one
	/// message when they're done (<see cref="ItemsNote"/>).
	/// </summary>
	/// <param name="held">The names of the normal cards of a game already held - for "(1 new for the badge)".</param>
	/// <param name="gone">True for a pack something else has opened meanwhile (automatic's own, opened by automatic): skipped.</param>
	/// <param name="onOpened">Told each pack opened, while it's still claimed.</param>
	public static async Task<int> OpenAsync(IReadOnlyList<Spare.PackDecision> decisions, ISpareSteam steam, string account, int max, CancellationToken ct,
		Func<uint, IReadOnlySet<string>>? held = null, Func<ulong, bool>? gone = null, Action<ulong>? onOpened = null) {
		int opened = 0;
		HashSet<ulong> handled = [];
		ItemsNote note = new(account);

		// The cards held per game, with what each pack opened here added: two packs of a game, the second's are counted
		// against the first's too.
		Dictionary<uint, HashSet<string>> have = [];

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

			if (gone?.Invoke(d.Pack.AssetId) == true) {
				continue;
			}

			HashSet<ulong> claimed = steam.Claim([d.Pack.AssetId]);

			if (claimed.Count == 0) {
				continue;
			}

			(bool ok, List<string> cards, string? why) result;

			try {
				result = await steam.UnpackAsync(d.Pack, ct).ConfigureAwait(false);

				if (result.ok) {
					onOpened?.Invoke(d.Pack.AssetId);
				}
			} catch {
				note.Send();

				throw;
			} finally {
				steam.Release(claimed);
			}

			if (!result.ok) {
				Log.Info(new Said("couldn't open {0}: {1}", d.Pack.Name, result.why ?? "-"), account);

				break;
			}

			opened++;
			note.Opened(d.Pack.Name, result.cards);

			int? fresh = null;

			if (held != null) {
				if (!have.TryGetValue(d.Pack.Game, out HashSet<string>? cardsHeld)) {
					have[d.Pack.Game] = cardsHeld = new HashSet<string>(held(d.Pack.Game), StringComparer.OrdinalIgnoreCase);
				}

				fresh = NewForBadge(cardsHeld, result.cards);
				cardsHeld.UnionWith(result.cards);
			}

			Log.Earned(OpenedSaid(d.Pack.Name, result.cards, fresh), account);

			// Why, in the debug log: "made to level the s&box badge" on the line itself ran it past the edge of the window.
			Log.Debug(new Said("why it opened {0}: {1}", d.Pack.Name, d.Why), account);
		}

		note.Send();

		return opened;
	}

	/// <summary>The reason a pack made to level a badge is opened (Spare.PlanPacks) - said on its "opened" line.</summary>
	internal const string LevelWhy = "made to level the {0} badge";

	/// <summary>
	/// "opened s&amp;box pack: 3× Flying Contraption (1 new)": what came out - the same card three times said once, and
	/// the game in brackets after a card left off when the pack already names it - and how many of them the account didn't
	/// have (when known). Why it was opened is a DEBUG line of its own.
	/// </summary>
	public static Said OpenedSaid(string pack, IReadOnlyList<string> cards, int? newForBadge) {
		Said line = cards.Count > 0 ? new Said("opened {0}: {1}", PackSaid(pack), CardsSaid(pack, cards)) : new Said("opened {0} · Steam didn't say what was in it", PackSaid(pack));

		if ((cards.Count > 0) && (newForBadge is int n)) {
			line = new Said("{0} ({1})", line, n > 0 ? new Said("{0} new", n) : new Said("none new"));
		}

		return line;
	}

	/// <summary>"s&amp;box pack" for Steam's "s&amp;box Booster Pack" - any other name as it is.</summary>
	public static Said PackSaid(string pack) => pack.EndsWith(" Booster Pack", StringComparison.OrdinalIgnoreCase) && (pack.Length > 13)
		? new Said("{0} pack", pack[..^13]) : new Said("{0}", pack);

	/// <summary>"3× Flying Contraption, Skull": each card once, with how many came when it's more than one, in the order they came.</summary>
	public static string CardsSaid(string pack, IReadOnlyList<string> cards) =>
		string.Join(", ", cards.Select(c => Short(pack, c)).GroupBy(static c => c, StringComparer.OrdinalIgnoreCase)
			.Select(static g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key));

	/// <summary>"Skull (Arcane Raise)" out of "- Arcane Raise - Booster Pack" is "Skull": the pack already says which game. A foil keeps its "(foil)".</summary>
	private static string Short(string pack, string card) {
		int open = card.LastIndexOf(" (", StringComparison.Ordinal);

		if ((open <= 0) || !card.EndsWith(')') || card.EndsWith(" (foil)", StringComparison.Ordinal)) {
			return card;
		}

		string game = card[(open + 2)..^1].Trim();

		return (game.Length > 0) && pack.Contains(game, StringComparison.OrdinalIgnoreCase) ? card[..open] : card;
	}

	/// <summary>
	/// How many different normal cards out of a pack the account didn't hold: foils left out, a card twice counted once. A
	/// name is matched with or without a game in brackets after it ("Skull (Arcane Raise)" is "Skull").
	/// </summary>
	public static int NewForBadge(IReadOnlySet<string> held, IReadOnlyList<string> cards) {
		HashSet<string> have = new(held.Select(Plain), StringComparer.OrdinalIgnoreCase);

		return cards.Where(static c => !c.EndsWith(" (foil)", StringComparison.Ordinal)).Select(Plain).Distinct(StringComparer.OrdinalIgnoreCase).Count(c => !have.Contains(c));

		static string Plain(string name) {
			string n = name.Trim();
			int open = n.LastIndexOf(" (", StringComparison.Ordinal);

			return (open > 0) && n.EndsWith(')') ? n[..open] : n;
		}
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
			return Logged(new Said("unopened booster packs: {0}", Modules.ScamWatch.OnHold(account)), account);
		}

		if (choice == 1) {
			string sent = await steam.SendAsync(packs, sendTo, ct).ConfigureAwait(false);

			return sent.Length > 0 ? Logged(new Said("unopened booster packs: {0}", sent), account) : null;
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

		return Logged(new Said("unopened booster packs: listed {0} - {1}", listed, text), account);
	}

	/// <summary>Every pack sent or listed is one line in the log, whoever asked - said here, so no caller can forget it.</summary>
	private static Said Logged(Said said, string account) {
		Log.Info(said, account);

		return said;
	}

	// ── one whole look, for the module and the commands ─────────────────────
	/// <summary>What a look found: the plan, the account's currency, and the inventory it was made from.</summary>
	/// <param name="StillPricing">Spare items still waiting for their first price in the wallet's currency, after this look.</param>
	/// <param name="Spread">How a human-mode account's items are spread through its day (null on a robot account).</param>
	/// <param name="PricingMinutes">About how long the pricing still takes (<see cref="PricingMinutesLeft"/>); 0 for "not known".</param>
	public sealed record Look(SparePlan Plan, int Currency, IReadOnlyList<SpareItem> Items, IReadOnlySet<ulong> Busy, SpareLedger Ledger, SpareRules Rules,
		int StillPricing = 0, SpareSpread.Plan? Spread = null, int PricingMinutes = 0) {
		public MarketPrice? Price(SpareItem item) => SparePrices.Of(item, Currency);
	}

	/// <summary>
	/// A selling price for one item at today's prices in the account's own wallet currency and the selling price rule, or
	/// null when it has none for certain.
	/// </summary>
	internal static SpareDecision? SaleOf(SpareItem item, int currency, SpareRules rules, Said why) {
		MarketPrice? p = SparePrices.Of(item, currency);

		if ((p is not { Cents: > 0 } price) || (DateTime.UtcNow - price.At > TimeSpan.FromHours(rules.PriceMaxAgeHours))
			|| (ListingPrice.For(price, rules.SellPrice, rules.DumpPct, rules.MinSaleCents, rules.FloorPct).Cents is not int list)) {
			return null;
		}

		int receive = Seller.ReceiveFor(list);

		// Under "Only sell if you get at least": kept.
		return (receive > 0) && (receive >= rules.MinSaleCents) ? new SpareDecision(item, SpareVerdict.Sell, why, ListingPrice.Worth(price), 0, list, receive) : null;
	}

	/// <summary>
	/// Read everything a decision needs - badges, the whole inventory, what's on the market or in a waiting trade - price
	/// what's spare (a few games a look), and decide. A problem instead when any of it couldn't be read: half an answer
	/// is a reason to wait, never to guess.
	/// </summary>
	public static async Task<(Look? Look, string? Problem)> ReadAsync(Bot bot, CancellationToken ct, bool priceThem = true, int quotes = QuotesAsked) {
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

		// 1.8.2: cards promised to a card swap nocat.lol paired stay for it - never sold or turned into gems meanwhile.
		HashSet<ulong> busy = [.. CardExchange.NotForSale(bot, promised, DateTime.UtcNow), .. listings.Where(static l => l.AssetId != 0).Select(static l => l.AssetId)];
		List<SpareItem> items = Spare.Read(inventory);
		DateTime now = DateTime.UtcNow;
		SpareLedger ledger = SpareLedger.Load(bot.Name);
		ledger.Observe(items.Select(static i => i.AssetId), now);

		// Our own listings against the market now: one declined on the phone (or taken down before it ever went up) isn't
		// listed again for the days set. And an item back from an unsold listing taken down to go up cheaper isn't "new".
		SpareListings memory = SpareListings.Load(bot.Name);
		SpareRules rules = SpareRules.From(bot.Cfg);

		// A human-mode account spreads its items through its day: about what's left today over the looks left in it.
		SpareSpread.Plan? spread = SpareSpread.Of(bot, ledger, rules);

		if (spread != null) {
			rules = rules with { PerRun = spread.PerLook };
		}

		// A listing of ours gone before it was ever seen up: sold quickly, or declined - the market history says which. And
		// one gone after it was up: sold, or taken down - the history says whether it sold, so a sale is told. Not read,
		// nothing is judged this look.
		await JudgeListingsAsync(bot, listings, currency, rules, memory, now, ct).ConfigureAwait(false);

		foreach (SpareItem item in items.Where(i => memory.CameBack(i.Hash, ledger.Received(i.AssetId)))) {
			ledger.MarkOld(item.AssetId);
		}

		ledger.Save();

		// Names for the games the store doesn't know (any more): one store lookup each, best effort - the items' own tags
		// already gave most of them.
		await GameNames.ResolveAsync(items.Select(static i => i.Game).Where(static g => !GameNames.IsKnown(g)), ct).ConfigureAwait(false);

		LiveSpareSteam steam = new(bot);
		Func<SpareItem, Said?> waiting = OurListings.WaitingWhy(OurListings.Load(bot.Name));
		HashSet<uint> bought = CardExchange.BoughtGames(bot.Name, DateTime.UtcNow);
		SparePlan Plan() => Decide(items, badges, rules, ledger, busy, currency, memory, waiting, bought);
		SparePlan plan = priceThem
			? await PriceAsync(Plan, items, rules, currency, quotes, bot.Cfg.OpenBoosterPacks == 1, (rules.Action is 0 or 1) || (GemsChoice(bot.Cfg) == 4), steam, ct).ConfigureAwait(false)
			: Plan();

		return (new Look(plan, currency, items, busy, ledger, rules, StillToPrice(plan, currency, rules), spread, PricingMinutesLeft(plan, currency, rules)), null);
	}

	/// <summary>
	/// Price what a look needs and decide again: the dollar shortlists first (one search a game, a few games a look), a few
	/// items priced both ways when the wallet's rate isn't known yet, then in the wallet's own currency only the items whose
	/// converted price is too near the price limit to trust - everything else is decided from the shortlist at the learned
	/// rate (<see cref="SparePrices.Estimate"/>). A big inventory in another currency is decided in a handful of requests,
	/// where it used to be one request an item. A sale is priced again in its real currency right before it's listed.
	/// </summary>
	/// <param name="plan">Decides from the prices as they're held now.</param>
	/// <param name="packGames">Booster packs are opened "only when the cards are worth more": their games' shortlists too.</param>
	/// <param name="sackWanted">A sack of gems' price is needed (best of gems or selling, gems only - gems are never made of what sells for more - or selling sacks).</param>
	public static async Task<SparePlan> PriceAsync(Func<SparePlan> plan, IReadOnlyList<SpareItem> items, SpareRules rules, int currency, int quotes, bool packGames,
		bool sackWanted, ISpareSteam steam, CancellationToken ct) {
		SparePlan now = plan();

		// The shortlist first: one US-dollar search per game says which of its items anybody sells and what they go for -
		// oldest first, a few games a look.
		List<uint> games = [.. now.Decisions.Where(static d => d.Verdict is SpareVerdict.Unpriced or SpareVerdict.Gems or SpareVerdict.Sell or SpareVerdict.Keep or SpareVerdict.OverLimit)
			.Select(static d => d.Item.Game)
			.Concat(packGames ? items.Where(static i => i.Kind == SpareKind.BoosterPack).Select(static i => i.Game) : [])
			.Distinct()
			.Where(g => Stale(SparePrices.Cached(g)?.When, rules))
			.OrderBy(g => SparePrices.Cached(g)?.At ?? 0)
			.Take(GamesPerLook)];

		foreach (uint game in games) {
			if (!await SparePrices.PriceGameAsync(game, ct).ConfigureAwait(false)) {
				break;   // the market isn't answering - the rest wait
			}
		}

		// No rate for the wallet's currency in the last day: a few items priced in it too - the dearest the shortlists know,
		// where a cent either way matters least - and the rate is learned from them (MarketQuotes, WalletRate).
		if (WalletRate.NeedsSamples(currency, DateTime.UtcNow)) {
			foreach (SpareItem item in RateSamples(items, currency)) {
				if (!await SparePrices.QuoteAsync(item, currency, ct).ConfigureAwait(false)) {
					break;
				}
			}
		}

		now = plan();

		// Then the few whose converted price is too near the limit to trust, in the wallet's own currency - one request each.
		int quoted = 0;

		foreach (SpareItem item in PricingOrder(now, currency, rules)) {
			if (quoted++ >= quotes) {
				break;
			}

			if (!await SparePrices.QuoteAsync(item, currency, ct).ConfigureAwait(false)) {
				break;
			}
		}

		// Best of both weighs gems by a sack's price, and selling the sacks needs it too - in this account's currency.
		if (sackWanted && Stale(SparePrices.Sack(currency)?.At, rules)) {
			await SparePrices.PriceSackAsync(currency, ct).ConfigureAwait(false);
		}

		now = plan();

		// Gem values a few a look (kept on disk since 1.8.2, so a restart doesn't start over), the ones the most items wait on
		// first. One Steam didn't answer waits a while, so the same few never take every turn while the rest wait.
		if (rules.Action is 0 or 1) {
			int asked = 0;
			DateTime at = DateTime.UtcNow;

			foreach (SpareItem item in now.Decisions.Where(static d => d.Verdict is SpareVerdict.Unpriced or SpareVerdict.Gems or SpareVerdict.Sell or SpareVerdict.Keep)
				.Select(static d => d.Item).Where(i => (i.GooType > 0) && SteamGems.Ask(i, at)).GroupBy(static i => i.GooKey)
				.OrderByDescending(static g => g.Count()).Select(static g => g.First())) {
				if (asked++ >= GemValuesPerLook) {
					break;
				}

				await steam.GemValueAsync(item, ct).ConfigureAwait(false);
			}
		}

		return plan();
	}

	/// <summary>Items to price both ways for the wallet's rate: one per market name, the dearest on the shortlists first.</summary>
	private static IEnumerable<SpareItem> RateSamples(IReadOnlyList<SpareItem> items, int currency) =>
		items.Where(static i => i.Marketable && (SparePrices.UsdCents(i.Hash) is >= 5)).DistinctBy(static i => i.Hash)
			.Where(i => MarketQuotes.Of(i.Hash, currency) is not { } q || (DateTime.UtcNow - q.When >= WalletRate.Life))
			.OrderByDescending(static i => SparePrices.UsdCents(i.Hash)).Take(WalletRate.SamplesNeeded + 1);

	/// <summary>
	/// Spare items still waiting for a price: their game's shortlist not read yet, or near the limit and not asked about in
	/// their own currency yet - what "still pricing" counts.
	/// </summary>
	public static int StillToPrice(SparePlan plan, int currency, SpareRules rules) =>
		PricingOrder(plan, currency, rules).Count
		+ plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Unpriced).Select(static d => d.Item)
			.Where(i => Stale(SparePrices.Cached(i.Game)?.When, rules)).DistinctBy(static i => i.Hash).Count();

	/// <summary>
	/// The price an item is decided at: its own price in the wallet's currency while that's fresh (or the shortlist saying
	/// nobody sells it), else its dollar shortlist price at the learned rate (<see cref="SparePrices.Estimate"/>).
	/// </summary>
	public static Func<SpareItem, MarketPrice?> PriceFor(int currency, SpareRules rules) {
		TimeSpan maxAge = TimeSpan.FromHours(rules.PriceMaxAgeHours);

		return i => SparePrices.Of(i, currency) is { } own && (DateTime.UtcNow - own.At <= maxAge) ? own
			: SparePrices.Estimate(i, currency, rules.MaxPriceCents) ?? SparePrices.Of(i, currency);
	}

	/// <summary>
	/// "sold Ninja (Spacebase DF-9) for 0.30 CAD - you got 0.26 CAD" - the log line for a listing of ours the market history
	/// says sold. What a buyer paid only when it was written down as it went up.
	/// </summary>
	public static Said SoldSaid(OurListings.Entry sale, int currency) {
		string card = Seller.CardName(sale.Hash);
		string game = (sale.Hash.IndexOf('-') is int dash and > 0) && uint.TryParse(sale.Hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app)
			? GameNames.Of(app) : "-";

		return sale.BuyerCents > 0
			? new Said("sold {0} ({1}) for {2} · you got {3}", card, game, Spare.Money(sale.BuyerCents, currency), Spare.Money(sale.YouGetCents, currency))
			: new Said("sold {0} ({1}) · you got {2}", card, game, Spare.Money(sale.YouGetCents, currency));
	}

	/// <summary>Booster packs one look opens: on a human-mode account "Items per look, human mode", like somebody clicking
	/// through a few; on the others all of them (up to 50).</summary>
	public static int PacksPerLook(BotConfig cfg) => cfg.LegitMode ? Math.Clamp(cfg.SpareHumanPerLook, 1, 50) : 50;

	/// <summary>
	/// Our own listings against the market now, each change said as it's found: one that went up ("X is on the market ·
	/// 0.22 CAD"), sold, or declined (and not listed again for the days set). The market history - one more request - only
	/// when something needs it to be told.
	/// </summary>
	internal static async Task JudgeListingsAsync(Bot bot, IReadOnlyList<Seller.Listing> listings, int currency, SpareRules rules, SpareListings memory, DateTime now,
		CancellationToken ct) {
		List<OurListings.Entry> ours = OurListings.Load(bot.Name);
		OurListings.History? history = OurListings.Undecided(bot.Name) || OurListings.Gone(ours, listings, now) ? await Seller.HistoryAsync(bot, ct).ConfigureAwait(false) : null;
		(List<OurListings.Entry> declined, List<OurListings.Entry> soldNow, List<OurListings.Entry> live) = OurListings.ReviewAll(bot.Name, listings, history, now);

		// A few each on a line of their own; more at once (a batch confirmed in the Steam app) is one line - each one is
		// still in the log file.
		foreach (OurListings.Entry e in live) {
			Say(live.Count > EachOwnLine, LiveSaid(e.Hash, e.BuyerCents, currency), bot.Name);
		}

		if (live.Count > EachOwnLine) {
			Log.Good(LiveAllSaid(live, currency), bot.Name);
		}

		foreach (OurListings.Entry s in soldNow) {
			Say(soldNow.Count > EachOwnLine, SoldSaid(s, currency), bot.Name);
		}

		if (soldNow.Count > EachOwnLine) {
			Log.Good(SoldAllSaid(soldNow, currency), bot.Name);
		}

		foreach (OurListings.Entry d in declined) {
			// 1.8.2: one that was up on the market and came down unsold was taken down - by you, not turned down on the phone: it
			// may be listed again ("Don't list declined items again" is for listings never confirmed).
			if (d.Active) {
				Log.Info(new Said("{0}: listing taken down · it can be listed again", Seller.CardName(d.Hash)), bot.Name);

				continue;
			}

			memory.AddDecline(d.Hash, d.AssetId, now);
			Log.Info(DeclinedSaid(d.Hash, rules.DeclinedDays), bot.Name);
		}

		if (declined.Count > 0) {
			memory.Save();
		}
	}

	/// <summary>
	/// While a listing of ours waits on its confirmation in the Steam app: a light look at the market (one request, and the
	/// history only when it's needed), so the log says when it's up or declined - minutes after, not at the next look hours
	/// later. False when there was nothing waiting, or Steam didn't say.
	/// </summary>
	public static async Task<bool> CheckPendingAsync(Bot bot, CancellationToken ct) {
		if (!OurListings.Undecided(bot.Name) || (bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid)
			|| (await Seller.ListingsAsync(bot, ct).ConfigureAwait(false) is not { } listings)) {
			return false;
		}

		SpareListings memory = SpareListings.Load(bot.Name);
		await JudgeListingsAsync(bot, listings, (int) bot.WalletCurrency, SpareRules.From(bot.Cfg), memory, DateTime.UtcNow, ct).ConfigureAwait(false);

		return true;
	}

	/// <summary>One listing's line: in the window, or - one of many said together - only in the log file.</summary>
	private static void Say(bool many, Said said, string account) {
		if (many) {
			Log.Debug(said, account);
		} else {
			Log.Good(said, account);
		}
	}

	/// <summary>Listings gone up (or sold) at once that still get a line each; more than this are one line.</summary>
	public const int EachOwnLine = 3;

	/// <summary>"29 listings on the market · 1.04 CAD" - many gone up at once, what buyers pay for them all.</summary>
	public static Said LiveAllSaid(IReadOnlyCollection<OurListings.Entry> live, int currency) => live.All(static e => e.BuyerCents > 0)
		? new Said("{0} listings on the market · {1}", live.Count, Spare.Money(live.Sum(static e => (long) e.BuyerCents), currency))
		: new Said("{0} listings on the market", live.Count);

	/// <summary>"sold 5 items · you got 0.85 CAD" - many sold since the last look.</summary>
	public static Said SoldAllSaid(IReadOnlyCollection<OurListings.Entry> sold, int currency) =>
		new("sold {0} items · you got {1}", sold.Count, Spare.Money(sold.Sum(static e => (long) e.YouGetCents), currency));

	/// <summary>"Starry Background 1 is on the market · 0.22 CAD" - a listing of ours up for buyers, at what they pay.</summary>
	public static Said LiveSaid(string hash, int buyerCents, int currency) => buyerCents > 0
		? new Said("{0} is on the market · {1}", Seller.CardName(hash), Spare.Money(buyerCents, currency))
		: new Said("{0} is on the market", Seller.CardName(hash));

	/// <summary>"Level ??? Ninja (Spacebase DF-9): ..." - the log line for a listing found declined.</summary>
	public static Said DeclinedSaid(string hash, int days) {
		string card = Seller.CardName(hash);
		string game = (hash.IndexOf('-') is int dash and > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app)
			? GameNames.Of(app) : "-";

		return days <= 0
			? new Said("{0} ({1}): listing declined or taken down · never listed again", card, game)
			: new Said("{0} ({1}): listing declined or taken down · not listed again for {2} day(s)", card, game, days);
	}

	/// <summary>
	/// The spare items to price in the wallet's own currency, one market request each: those whose converted price is too
	/// near the price limit to trust (<see cref="SparePrices.NeedsOwnPrice"/>), cheapest first, then those the search pages
	/// didn't have at all (<see cref="SparePrices.NotListed"/>) - one per market name. Everything else is decided from the
	/// dollar shortlist at the learned rate, and an item is priced in its own currency right before it's listed anyway.
	/// </summary>
	public static List<SpareItem> PricingOrder(SparePlan plan, int currency, SpareRules rules) =>
		[.. plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Unpriced).Select(static d => d.Item)
			.Where(i => (SparePrices.NeedsOwnPrice(i, currency, rules.MaxPriceCents) || (SparePrices.NotListed(i) && !Stale(SparePrices.Cached(i.Game)?.When, rules)))
				&& Stale(SparePrices.Of(i, currency)?.At, rules)).DistinctBy(static i => i.Hash)
			.OrderBy(static i => SparePrices.NotListed(i)).ThenBy(static i => SparePrices.ShortlistCents(i))];

	/// <summary>
	/// About how long pricing still takes, in minutes: the games whose dollar search isn't read yet go <see cref="GamesPerLook"/>
	/// a look, a look every 5-10 minutes while pricing, and the items that need their own price go one request each at the
	/// market's pace. It used to count every item as one request - "about 208 min" for what took under an hour.
	/// </summary>
	public static int PricingMinutesLeft(SparePlan plan, int currency, SpareRules rules) {
		int games = plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Unpriced).Select(static d => d.Item.Game)
			.Where(g => Stale(SparePrices.Cached(g)?.When, rules)).Distinct().Count();
		int own = PricingOrder(plan, currency, rules).Count;
		double looks = Math.Ceiling(games / (double) GamesPerLook) * 7.5;
		double quotes = PriceBook.EtaSeconds(own) / 60.0;

		return (int) Math.Ceiling(looks + quotes);
	}

	/// <summary>"still pricing 146 item(s) - about 20 min": one market request each, at the market's own gap.</summary>
	public static Said PricingSaid(int items, double gapSeconds) =>
		new("still pricing {0} item(s) - about {1} min", items, Math.Max(1, (int) Math.Ceiling(items * gapSeconds / 60)));

	private static bool Stale(DateTime? at, SpareRules rules) => (at == null) || (DateTime.UtcNow - at.Value > TimeSpan.FromHours(rules.PriceMaxAgeHours * 0.8));

	/// <summary>Items priced one by one in the wallet's currency in one look: a command asks a few, the background look many.</summary>
	public const int QuotesAsked = 20, QuotesBackground = 60;

	private static SparePlan Decide(IReadOnlyList<SpareItem> items, SpareBadges badges, SpareRules rules, SpareLedger ledger, IReadOnlySet<ulong> busy,
		int currency, SpareListings memory, Func<SpareItem, Said?> waiting, IReadOnlySet<uint> bought) {
		DateTime now = DateTime.UtcNow;

		return Spare.Plan(new SpareInput {
			Items = items,
			Badges = badges,
			Rules = rules,
			Received = ledger.Received,
			Busy = busy,
			Price = PriceFor(currency, rules),
			Currency = currency,
			Sack = SparePrices.SackPays(currency, rules.DumpPct),
			GemValue = static i => i.GooType > 0 ? SteamGems.KnownValue(i) : 0,
			Declined = i => memory.DeclinedWhy(i, rules.DeclinedDays, now) ?? waiting(i),
			StepOf = i => memory.StepOf(i.Hash, now),
			BoughtGames = bought
		});
	}

	/// <summary>A look, and - when it may act - doing it. Returns what it would do (preview) or did, in English, for a command.</summary>
	public static async Task<string> RunAsync(Bot bot, bool act, CancellationToken ct) => (await LookAndDoAsync(bot, act, QuotesAsked, ct).ConfigureAwait(false)).Said;

	/// <summary>What a look said, and whether a sooner look is wanted: items left that a later look in its day can do, or
	/// items still waiting for their first price. Spacing: on a human-mode account, the time to its next look that does
	/// items (see <see cref="SpareSpread"/>). Busy: another look was already working on the account - nothing was done.</summary>
	/// <param name="Refused">1.8.2: Steam refused a listing in it - the next look waits an hour or two.</param>
	public sealed record Ran(string Said, bool MoreToDo, bool StillPricing, TimeSpan? Spacing = null, bool Busy = false, bool Refused = false);

	// ── one look at a time ──────────────────────────────────────────────────
	/// <summary>The accounts a look is working on right now - a 'spare do' or the timed look, never both.</summary>
	private static readonly HashSet<string> Working = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Claims the account for one look that acts: null while another is working on it. Live, a 'spare do' and the timed
	/// look ran side by side on the same items - one asked for an item the other had just turned into gems ("no answer"),
	/// and the log mixed two looks' lines under one summary.
	/// </summary>
	public static IDisposable? Acting(string account) {
		lock (Working) {
			return Working.Add(account) ? new Claimed(account) : null;
		}
	}

	private sealed class Claimed(string account) : IDisposable {
		private int _done;

		public void Dispose() {
			if (Interlocked.Exchange(ref _done, 1) == 0) {
				lock (Working) {
					Working.Remove(account);
				}
			}
		}
	}

	/// <summary>What a look answers while another is working on the account.</summary>
	private static string AlreadyWorking(string account) =>
		$"{account}: already dealing with spare items - one look at a time; the log says what each item did";

	/// <summary>A look's answer as one line, for the debug log: "a · b · c".</summary>
	public static string OneLine(string said) =>
		System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(said.Trim(), @"\s*\r?\n\s*", " · "), " {2,}", " ");

	/// <summary>A look, and - when it may act - doing it, pricing up to <paramref name="quotes"/> items one by one.</summary>
	public static async Task<Ran> LookAndDoAsync(Bot bot, bool act, int quotes, CancellationToken ct) {
		if (!act) {
			return await LookAndDoClaimedAsync(bot, act, quotes, ct).ConfigureAwait(false);
		}

		// Read and done under the claim: a look that waited would otherwise decide from an inventory the other look was
		// still emptying.
		using IDisposable? mine = Acting(bot.Name);

		return mine == null ? new Ran(AlreadyWorking(bot.Name), false, false, Busy: true) : await LookAndDoClaimedAsync(bot, act, quotes, ct).ConfigureAwait(false);
	}

	private static async Task<Ran> LookAndDoClaimedAsync(Bot bot, bool act, int quotes, CancellationToken ct) {
		(Look? look, string? problem) = await ReadAsync(bot, ct, quotes: quotes).ConfigureAwait(false);

		if (look == null) {
			return new Ran($"{bot.Name}: {problem}", false, false);
		}

		bool pricing = look.StillPricing > 0;

		if (LeftAlone(look.Plan) is { Length: > 0 } left) {
			Log.Debug($"spare items left alone: {Columns.ClipChars(left, LeftAloneMax, "...")}", bot.Name);
		}

		// 1.8.2: items over the price limit are never touched - the owner's rule. Said once a day, in one line with what
		// they're worth together, so raising the limit is a choice; every hour it was a line nobody needed again.
		if ((OverLimitSaid(look.Plan, look.Rules.MaxPriceCents, look.Currency) is { } over) && look.Ledger.OverLimitDue(DateTime.Now)) {
			look.Ledger.Save();
			Log.Info(over, bot.Name);
		}

		if (!act) {
			Briefs[bot.Name] = Brief(look, null);

			return new Ran(Preview(bot.Name, look), false, pricing);
		}

		if (!look.Ledger.Approved) {
			PostPreviewOnce(bot.Name, look);
			Briefs[bot.Name] = Brief(look, null);

			return new Ran(Preview(bot.Name, look), false, pricing);
		}

		// Its gaps between items spread them over the time to the next look, on a human-mode account.
		// The loose gems it starts with: an answer that isn't a clear yes is checked against them (GemsIfGoneAsync).
		long gemsAtStart = look.Items.Where(static i => i.Kind == SpareKind.Gems).Sum(static i => (long) i.Amount);
		LiveSpareSteam steam = new(bot, () => StillMayAct(bot, SpareGate(bot)), look.Spread, gemsAtStart);
		Done done = await ActAsync(look.Plan, look.Rules, look.Ledger, steam, bot.Name, static () => DateTime.Now, ct).ConfigureAwait(false);

		// What follows the items on a human-mode account counts against the same "items a look" and waits the same gaps.
		LookBudget? budget = LookBudget.After(bot.Cfg.LegitMode, look.Rules.PerRun, done.GemItems + done.Sold);

		// Gems afterwards: from the inventory as it was - packed gems and sacks are read again next look.
		List<Said> gems = await GemsAfterAsync(look.Items, GemsChoice(bot.Cfg), Math.Max(0, bot.Cfg.SpareKeepGems), Looting.SendToName(bot.Cfg, bot.Cfg.SpareGemsSendTo, bot.Name), steam,
			s => SaleOf(s, look.Currency, look.Rules, new Said("sack of gems")), look.Rules, look.Ledger, static () => DateTime.Now, bot.Name, ct, budget).ConfigureAwait(false);

		// Selling, where it's chosen: its own listings unsold after the days set come down, to go up again a step cheaper -
		// never under the floor. Only listings nocat.farm put up itself - never one you listed by hand. Asked again before
		// each one: turned off, held or human mode's day over part way stops the rest.
		if ((look.Rules.Action is 0 or 2) && (AfterStop(look.Ledger, steam, bot.Name) == null)) {
			await Seller.StepDownAsync(bot, Math.Clamp(bot.Cfg.SpareRelistDays, 1, 60), ct, () => AfterStop(look.Ledger, steam, bot.Name), budget,
				bot.Cfg.LegitMode ? steam.PauseAsync : null, StepGems(look.Items, look.Rules, look.Currency)).ConfigureAwait(false);
		}

		bool more = (look.Plan.Actions.Count > done.GemItems + done.Sold) && look.Ledger.Fits(0, look.Rules.DailyItems, look.Rules.DailyValueCents, DateTime.Now);
		Briefs[bot.Name] = Brief(look, done);

		return new Ran(Answer(bot.Name, look, done, gems), more, pricing, look.Spread?.Spacing, Refused: done.Refused);
	}

	/// <summary>
	/// Each account's last look in one line (<see cref="Brief"/>) - what the log says when a 'spare preview' or 'spare do'
	/// answers after the command line was given its "the log says when it's done". The whole answer, item by item, was 25
	/// INFO lines in the log; it's in the debug log, and the command answers with it when it answers straight away.
	/// </summary>
	public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Briefs = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// A look in one line: "spare: 29 → gems, 1 to sell · today 26/50", or what it did - "spare: 18 → 510 gems, 0 listed ·
	/// today 44/50". English, as a command's answer is.
	/// </summary>
	public static string Brief(Look look, Done? done) {
		List<string> parts = [];
		int gems = look.Plan.Count(SpareVerdict.Gems), sell = look.Plan.Count(SpareVerdict.Sell);

		if (done is { } d && ((d.GemItems + d.Sold > 0) || (d.Stopped != null))) {
			parts.Add(string.Create(CultureInfo.InvariantCulture, $"{d.GemItems} → {d.Gems:N0} gems, {d.Sold} listed{(d.Stopped != null ? " (stopped part way)" : "")}"));
		} else if (gems + sell > 0) {
			parts.Add(string.Join(", ", new[] { gems > 0 ? $"{gems} → gems" : "", sell > 0 ? $"{sell} to sell" : "" }.Where(static p => p.Length > 0)));
		} else {
			parts.Add("nothing to do");

			// Only when that's all there is to say: it's why nothing is ready yet.
			if (look.StillPricing > 0) {
				parts.Add($"still pricing {look.StillPricing}");
			}
		}

		SpareLedger l = look.Ledger;
		bool today = l.Day == DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		if (gems + sell + (today ? l.DoneItems : 0) > 0) {
			parts.Add($"today {(today ? l.DoneItems : 0)}/{look.Rules.DailyItems}");
		}

		return "spare: " + string.Join(" · ", parts);
	}

	/// <summary>
	/// What a look that acted says: what it would do, what it did, then today's count - counted after the run, so the
	/// items it just did are in it (it said "today: 0 of 10" right after doing five).
	/// </summary>
	public static string Answer(string account, Look look, Done done, IEnumerable<Said> after) {
		bool acted = (done.GemItems + done.Sold > 0) || (done.Stopped != null);

		// Nothing done and nothing in the way: the preview says it all. Once it did something, the answer is what it did -
		// the "turned X into N gems" lines are already in the log, one each, and the plan's list said again after them
		// ("next 20 it would do", items it had just done) read as more to come.
		StringBuilder sb = acted ? new() : new(Preview(account, look, footer: false));

		if (acted) {
			sb.Append(CultureInfo.InvariantCulture, $"{account}: done - {done.GemItems} item(s) into {done.Gems.ToString("N0", CultureInfo.InvariantCulture)} gems, {done.Sold} listed");
		}

		if (done.Stopped is { } why) {
			sb.Append(" (").Append(why.ToEnglish()).Append(')');
		}

		if (done.SellSaid is { Length: > 0 } sold) {
			sb.AppendLine().Append(sold);
		}

		foreach (Said s in after) {
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{account}: {s.ToEnglish()}");
		}

		if (acted && (look.Spread is { LeftToday: > 0 } spread)) {
			sb.AppendLine().Append("  ").Append(SpareSpread.Said(spread).ToEnglish());
		}

		if (acted && (look.StillPricing > 0)) {
			sb.AppendLine().Append("  ").Append(PricingLine(look.StillPricing, look.PricingMinutes).ToEnglish());
		}

		return sb.Append(Footer(account, look)).ToString();
	}

	/// <summary>The limits a run keeps to, in this account's own wallet currency - for the confirm popup and 'set'.</summary>
	public static string Limits(Bot bot) {
		SpareRules r = SpareRules.From(bot.Cfg);
		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

		string limits = $"Limits: at most {r.PerRun} {(r.PerRun == 1 ? "item" : "items")} a look and {r.DailyItems} a day, {Spare.Money(r.DailyValueCents, currency)} in value a day; never anything worth more than {Spare.Money(r.MaxPriceCents, currency)}, {NewItems(r.NewDays)}, never a card a badge still needs. Prices in this account's own wallet currency{(currency == 0 ? " (not known yet - nothing is priced or sold until it is)" : "")}.";

		// What else turning it on starts, by the account's other settings: packs it doesn't open and gem sacks leave too.
		string also = (Math.Clamp(bot.Cfg.BoosterUnopened, 0, 2) switch {
			1 => $"{Environment.NewLine}Also: booster packs it doesn't open are sent to {Named(Looting.SendToName(bot.Cfg, bot.Cfg.BoosterSendTo, bot.Name))}.",
			2 => $"{Environment.NewLine}Also: booster packs it doesn't open are sold, within these limits.",
			_ => ""
		}) + (GemsChoice(bot.Cfg) switch {
			3 => $"{Environment.NewLine}Also: gems are packed into sacks of 1000 and the sacks sent to {Named(Looting.SendToName(bot.Cfg, bot.Cfg.SpareGemsSendTo, bot.Name))}.",
			4 => $"{Environment.NewLine}Also: gems are packed into sacks of 1000 and one sack is sold each look, within these limits.",
			_ => ""
		});

		return limits + also;

		static string Named(string account) => string.IsNullOrWhiteSpace(account) ? "(no account set yet - nothing is sent until one is)" : account.Trim();
	}

	/// <summary>The new-items rule in words: "nothing that came in during the last 7 days" - never "within 0 day(s)".</summary>
	public static string NewItems(int days) => days switch {
		<= 0 => "new items aren't held back",
		1 => "nothing that came in during the last day",
		_ => $"nothing that came in during the last {days} days"
	};

	/// <summary>What a look would do, from the prices already held - quick, for the confirm popup. English.</summary>
	public static async Task<string> QuickPreviewAsync(Bot bot, CancellationToken ct) {
		(Look? look, string? problem) = await ReadAsync(bot, ct, priceThem: false).ConfigureAwait(false);

		return look == null ? $"{bot.Name}: {problem}" : Preview(bot.Name, look);
	}

	/// <summary>The first look on an account only shows what it would do, in the log and your chats - once, until you say ok.</summary>
	public static void PostPreviewOnce(string account, Look look) {
		// Nothing it would do: nothing to tell yet - it's told the first time there is.
		if (look.Ledger.PreviewPosted || (look.Plan.Actions.Count == 0)) {
			return;
		}

		SparePlan plan = look.Plan;
		Log.Attention(new Said("spare items preview for {0}: {1} to gems, {2} to sell. Nothing happens until you turn Spare items on in its settings - 'spare preview {0}' shows each one.",
			account, plan.Count(SpareVerdict.Gems), plan.Count(SpareVerdict.Sell)), account, Topic.Items);
		look.Ledger.PreviewPosted = true;
		look.Ledger.Save();
	}

	/// <summary>Exactly what it would do and why, in English (a command's answer).</summary>
	public static string Preview(string account, Look look, bool footer = true) {
		// Only what it will do: items to gems and items to sell, with the gems and the money - never the items it leaves
		// (a copy kept, needed for a badge, no price, worth more than the limit). Those are only in the debug log.
		SparePlan plan = look.Plan;
		SpareRules r = look.Rules;
		StringBuilder sb = new();
		List<SpareDecision> gems = [.. plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Gems)];
		List<SpareDecision> sell = [.. plan.Decisions.Where(static d => d.Verdict == SpareVerdict.Sell)];

		if (gems.Count + sell.Count == 0) {
			sb.Append(CultureInfo.InvariantCulture, $"{account}: nothing to turn into gems or sell right now");

			if (look.StillPricing > 0) {
				sb.AppendLine().Append("  ").Append(PricingLine(look.StillPricing, look.PricingMinutes).ToEnglish());
			}

			return sb.ToString();
		}

		List<string> parts = [];

		if (gems.Count > 0) {
			parts.Add($"{gems.Count} to gems ({gems.Sum(static d => (long) d.GemValue).ToString("N0", CultureInfo.InvariantCulture)} gems)");
		}

		if (sell.Count > 0) {
			parts.Add($"{sell.Count} to sell (you get {Spare.Money(sell.Sum(static d => (long) d.ReceiveCents), look.Currency)})");
		}

		sb.Append(CultureInfo.InvariantCulture, $"{account}: spare items - {string.Join(", ", parts)}")
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

		// A human-mode account: how the rest are spread through its day.
		if (look.Spread is { LeftToday: > 0 } spread) {
			sb.AppendLine().Append("  ").Append(SpareSpread.Said(spread).ToEnglish());
		}

		// Pricing is one market request an item: how far it has to go, so a long wait isn't mistaken for nothing happening.
		if (look.StillPricing > 0) {
			sb.AppendLine().Append("  ").Append(PricingLine(look.StillPricing, look.PricingMinutes).ToEnglish());
		}

		if (footer) {
			sb.Append(Footer(account, look));
		}

		return sb.ToString();
	}

	/// <summary>
	/// What "still pricing" says: how long it has to go - or, while Steam has asked price lookups to slow down, when they go
	/// on again. "About 7 min" all through a half-hour pause read as if it was nearly done.
	/// </summary>
	public static Said PricingLine(int items, int minutes = 0) => PriceBook.ResumesAt is DateTime at
		? new Said("still pricing {0} item(s) - Steam asked to slow down, pricing resumes around {1}", items, Fmt.Clock(at))
		: minutes > 0 ? new Said("still pricing {0} item(s) - about {1} min", items, minutes) : PricingSaid(items, PricingPace(items));

	/// <summary>Seconds a lookup, on average, for this many - the market's gap, and its hourly cap past the first hour's worth.</summary>
	private static double PricingPace(int items) => (double) PriceBook.EtaSeconds(items) / Math.Max(1, items);

	/// <summary>
	/// The items a look leaves, by reason, for the debug log only: "12 needed for the badge, 3 no fresh price yet ...". What
	/// you read says only what it does. Those kept for having no price - nobody sells it, no sale in the last day - are
	/// named too (up to 15 each), so the log shows which: "93 nobody sells it" once turned out to be items a search page
	/// simply hadn't shown.
	/// </summary>
	public static string LeftAlone(SparePlan plan) =>
		string.Join(", ", plan.Decisions.Where(static d => d.Verdict is not (SpareVerdict.Gems or SpareVerdict.Sell))
			.GroupBy(static d => d.Why.ToEnglish()).OrderByDescending(static g => g.Count()).Select(static g => $"{g.Count()} {g.Key}{Named(g)}"));

	/// <summary>
	/// 1.8.2: "2 item(s) worth 3.54 CAD are over your 0.75 price limit - raise it to sell them": the items a look never
	/// touches for being worth more than "Never touch items worth more than", in one line - null when there are none.
	/// </summary>
	public static Said? OverLimitSaid(SparePlan plan, int maxCents, int currency) {
		List<SpareDecision> over = [.. plan.Decisions.Where(static d => d.Verdict == SpareVerdict.OverLimit)];

		return over.Count == 0 ? null
			: new Said("{0} item(s) worth {1} are over your {2} price limit - raise it to sell them", over.Count,
				Spare.Money(over.Sum(static d => (long) d.PriceCents), currency), Spare.Money(maxCents));
	}

	/// <summary>The "spare items left alone" debug line is cut at this many characters - it ran to 2,500 with every name.</summary>
	public const int LeftAloneMax = 300;

	/// <summary>The reasons whose items the debug line names.</summary>
	private static readonly string[] NamedReasons = ["nobody sells it, so there's no price - kept", "no sale in the last day, so no real price - kept", "no sale in the last day, so no median - kept"];

	/// <summary>" [Ninja (Spacebase DF-9); Scout (Team Fortress 2); ... +5 more]" for a reason that names its items, else nothing.</summary>
	private static string Named(IGrouping<string, SpareDecision> group) {
		if (!NamedReasons.Contains(group.First().Why.English, StringComparer.Ordinal)) {
			return "";
		}

		List<string> names = [.. group.Select(static d => d.Item).DistinctBy(static i => i.Hash).Select(static i => $"{i.Name} ({GameNames.Of(i.Game)})")];
		string more = names.Count > 15 ? $" +{names.Count - 15} more" : "";

		return $" [{string.Join("; ", names.Take(15))}{more}]";
	}

	/// <summary>Today's count and what to type next - read when it's said, so it counts whatever a run just did.</summary>
	private static string Footer(string account, Look look) {
		SpareLedger l = look.Ledger;
		SpareRules r = look.Rules;
		bool today = l.Day == DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		StringBuilder sb = new();
		sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  today: {(today ? l.DoneItems : 0)} of {r.DailyItems} item(s), {Spare.Money(today ? l.DoneCents : 0)} of {Spare.Money(r.DailyValueCents)} in value");
		sb.AppendLine().Append(l.Approved
			? $"  'spare do {account}' does the next ones now."
			: $"  Nothing happens on {account} until Spare items is turned on.");

		return sb.ToString();
	}

	// ── booster packs, a whole look ──────────────────────────────────────────
	/// <summary>Decide every sealed pack - and open them when <paramref name="act"/>. An English answer, for a command.</summary>
	public static async Task<string> PacksAsync(Bot bot, bool act, CancellationToken ct) {
		// One opening look at a time on an account, as for spare items: 'booster open do' and the timed look at once both
		// tried the same packs.
		using IDisposable? mine = act ? Acting("packs:" + bot.Name) : null;

		if (act && (mine == null)) {
			return $"{bot.Name}: already opening booster packs - one look at a time; the log says what came out";
		}

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
		// The packs nocat.farm made itself: never held back as new. The ones automatic "Make booster packs" made to level a
		// badge are opened at the next chance - by it (Modules/Boosters.Auto.cs) or here, whichever comes first - and never
		// sent or sold. Found in the inventory first, so one Steam didn't give the id of yet isn't taken for a new pack.
		Modules.BoosterPick.Memory memory = BotManager.ModuleOf<Modules.Boosters>(bot)?.AutoMemory ?? Modules.BoosterPick.Memory.Load(bot.Name);

		if (memory.Locate([.. look.Items.Where(static i => i.Kind == SpareKind.BoosterPack).Select(static i => (i.Game.ToString(CultureInfo.InvariantCulture), i.AssetId))])) {
			memory.Save(bot.Name);
		}

		memory.ReleaseAndSay(cfg.BoosterMake, cfg.BoosterNeverOpen, DateTime.UtcNow, bot.Name);
		HashSet<ulong> own = memory.Ids();
		// 1.8.2: packs automatic made to sell (Modules/Boosters.Gems.cs) are left to it, like ones on the market.
		HashSet<ulong> forSale = memory.ForSaleIds();
		List<Spare.PackDecision> packs = Spare.PlanPacks(look.Items, choice, Math.Clamp(cfg.BoosterOpenNewDays, 0, 30), cfg.BoosterNeverOpen,
			look.Ledger.Received, forSale.Count == 0 ? look.Busy : new HashSet<ulong>(look.Busy.Concat(forSale)), SparePrices.Pack, look.Rules.PriceMaxAgeHours, DateTime.UtcNow, madeHere: memory.MadeIds(), own: own);

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
		// Automatic's own: skipped if automatic opened it meanwhile, and off its list once opened here.
		int opened = await OpenAsync(packs, steam, bot.Name, PacksPerLook(cfg), ct,
			held: game => look.Items.Where(i => (i.Kind == SpareKind.Card) && (i.Game == game)).Select(static i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
			gone: id => own.Contains(id) && !memory.Ids().Contains(id),
			onOpened: id => {
				if (own.Contains(id) && memory.RemoveAsset(id)) {
					memory.Save(bot.Name);
				}
			}).ConfigureAwait(false);
		sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{bot.Name}: opened {opened}");

		Said? unopened = await UnopenedAsync(packs, Math.Clamp(cfg.BoosterUnopened, 0, 2), Looting.SendToName(cfg, cfg.BoosterSendTo, bot.Name), steam,
			p => SaleOf(p, look.Currency, look.Rules, new Said("worth more sealed")), look.Rules, look.Ledger, static () => DateTime.Now, ct, bot.Name).ConfigureAwait(false);

		if (unopened is { } u) {
			sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{bot.Name}: {u.ToEnglish()}");
		}

		return sb.ToString();
	}
}
