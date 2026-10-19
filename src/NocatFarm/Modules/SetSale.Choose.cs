using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Each finished set: crafted, sold instead, or - not known for sure and its cards known to be worth something - left for
/// the next look rather than crafted. Badge crafting acts on it; 'craft preview' only shows it.
/// </summary>
public static partial class SetSale {
	public enum Choice { Craft, Sell, Wait }

	/// <summary>A finished set as the badges page offers it: the game, foil or not, and how many levels one craft makes.</summary>
	public sealed record Ready(uint App, int Border, int Levels);

	/// <summary>
	/// The word on one finished set and why. <paramref name="Worth"/>: for one left waiting, its dearest card's known price
	/// (empty: nothing is known about what its cards are worth).
	/// <paramref name="Quiet"/>: selling instead isn't in play at all (its setting, buying cards off), the game is on the keep list,
	/// or its cards are being sold anyway (<see cref="DearCards"/>) - nothing worth a log line.
	/// <paramref name="Border"/>: 1.8.2, the set's border - 0 normal, 1 foil.
	/// </summary>
	public sealed record Pick(uint App, string Game, Choice Choice, Said Reason, Verdict? Verdict = null, string Worth = "", bool Quiet = false, int Border = 0);

	/// <summary>
	/// What to do with each finished set. Weighed only with "Sell sets worth more than the levels they'd buy" and buying cards
	/// on, <see cref="MaxSetsALook"/> a look. Weighed on the cards in
	/// <paramref name="inventory"/> - the one badge crafting just read - never a second read of it a second later, which Steam
	/// refuses as a duplicate ("The request is a duplicate"), and every set was crafted unweighed. Anything not known for
	/// sure (no price, the market or the wallet not answering) is crafted only when its cards are known to be worth little -
	/// none worth more than "Never touch items worth more than"; one worth more, or nothing known about what they're worth
	/// (no price kept, the wallet's money not known yet), waits for the next look. Reads only: nothing is listed or
	/// crafted here. 1.8.2: foil sets and a game's several sets are weighed too; a game whose cards are being sold as
	/// worth more than their levels (<see cref="DearCards"/>) isn't crafted meanwhile. <paramref name="preview"/>: nothing is
	/// written down either (how long a set has waited).
	/// </summary>
	public static async Task<IReadOnlyList<Pick>> ChooseAsync(Bot bot, IReadOnlyList<Ready> ready, InventoryContents inventory, CancellationToken ct, bool preview = false) {
		Pick[] picks = new Pick[ready.Count];   // in the order the badges page gave them
		Pick Craft(Ready r, Said why, bool quiet = false) => new(r.App, GameOf(r.App, inventory), Choice.Craft, why, Quiet: quiet, Border: r.Border);

		Said? off = !bot.Cfg.SellSetsForLevels ? new Said("selling sets instead of crafting is off")
			: !CardBuyer.On(bot) ? new Said("buying cards is off")
			: null;

		if (off is { } none) {
			return [.. ready.Select(r => Craft(r, none, quiet: true))];
		}

		List<int> weigh = [];
		IReadOnlyCollection<uint> keep = SpareRules.From(bot.Cfg).KeepGames;
		IReadOnlyCollection<uint> dear = DearCards.Marked(bot);
		HashSet<uint> bought = CardExchange.BoughtGames(bot.Name, DateTime.UtcNow);

		for (int i = 0; i < ready.Count; i++) {
			Ready r = ready[i];

			if (keep.Contains(r.App)) {
				// A game on the keep list ("Never touch items from these games"): its set is crafted, never sold.
				picks[i] = Craft(r, new Said("on your keep list - its set is crafted, never sold"), quiet: true);
			} else if ((r.Border == 0) && bought.Contains(r.App)) {
				// 1.8.2: buying cards bought the cards it was missing - for the level, so it's crafted, never sold.
				picks[i] = Craft(r, new Said("cards were bought to finish it - crafted, never sold"));
			} else if ((r.Border == 0) && dear.Contains(r.App)) {
				// Its cards are worth more sold than the levels they'd make: buying cards' look is selling them - never crafted meanwhile.
				picks[i] = new Pick(r.App, GameOf(r.App, inventory), Choice.Wait, new Said("its cards are being sold - worth more than the levels they'd make"), Quiet: true, Border: r.Border);
			} else if (!Considered(r.Border, r.Levels)) {
				picks[i] = Craft(r, new Said("{0} sets ready, crafted together", r.Levels));
			} else {
				weigh.Add(i);
			}
		}

		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;
		long most = BuyPlan.Cents(bot.Cfg.SpareMaxPrice);

		// Not known for sure: crafted only when its cards are known to be worth little. A card worth more than the price limit,
		// or nothing known at all about what they're worth (no price kept, the wallet's money not known): the next look.
		Pick Unsure(Ready r, Said why, Verdict? v) {
			int cur = v is { Currency: > 0 } ? v.Currency : currency;
			long top = cur <= 0 ? 0 : Math.Max(v?.Top ?? 0, KnownTop(r.App, inventory, cur, r.Border > 0));
			string game = v?.Game ?? GameOf(r.App, inventory);

			return top <= 0 ? new Pick(r.App, game, Choice.Wait, why, v, Border: r.Border)
				: top > most ? new Pick(r.App, game, Choice.Wait, why, v, BuyPlan.Money(top, cur), Border: r.Border)
				: new Pick(r.App, game, Choice.Craft, why, v, Border: r.Border);
		}

		List<int> now = [.. weigh.Take(MaxSetsALook)];

		foreach (int i in weigh.Skip(MaxSetsALook)) {
			picks[i] = Unsure(ready[i], new Said("only {0} sets are weighed a look", MaxSetsALook), null);
		}

		if (now.Count == 0) {
			return picks;
		}

		IReadOnlyList<Verdict> verdicts = [];

		if (BotManager.ModuleOf<CardBuyer>(bot) is not { } buyer) {
			foreach (int i in now) {
				picks[i] = Unsure(ready[i], new Said("buying cards isn't running"), null);
			}

			return picks;
		}

		try {
			verdicts = await buyer.WeighSalesAsync([.. now.Select(i => ready[i])], ct, inventory, note: !preview).ConfigureAwait(false);
		} catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested)) {
			Log.Debug($"couldn't weigh the sets: {Log.Describe(e)}", bot.Name);
		}

		foreach (int i in now) {
			Ready r = ready[i];
			Verdict? v = verdicts.FirstOrDefault(x => (x.App == r.App) && (x.Foil == (r.Border > 0)));

			picks[i] = v switch {
				null => Unsure(r, new Said("couldn't price its cards"), null),
				{ Sell: true } => new Pick(r.App, v.Game, Choice.Sell, v.Reason, v, Border: r.Border),
				{ Hold: true } => new Pick(r.App, v.Game, Choice.Wait, v.Reason, v, BuyPlan.Money(v.Top, v.Currency), Border: r.Border),
				{ Unsure: true } => Unsure(r, v.Reason, v),
				_ => new Pick(r.App, v.Game, Choice.Craft, v.Reason, v, Border: r.Border)
			};
		}

		return picks;
	}

	/// <summary>
	/// The dearest card of the game's set known without asking the market, in <paramref name="currency"/>: its last price in
	/// that currency, or else its last US-dollar price at the rate known for it. 0 when nothing is known.
	/// </summary>
	internal static long KnownTop(uint app, InventoryContents inventory, int currency, bool foil = false) {
		if (currency <= 0) {
			return 0;
		}

		double? rate = WalletRate.ForShowing(currency);
		Dictionary<string, int>? usdSet = LevelPlanner.KnownSet(app);
		long top = 0;

		foreach (string hash in Copies(inventory, app, foil).Keys) {
			long cents = MarketQuotes.Of(hash, currency)?.Lowest
				?? ((rate is double r) && ((SparePrices.UsdCents(hash) ?? usdSet?.GetValueOrDefault(hash)) is int usd and > 0) ? WalletRate.ToWallet(usd, r) : 0);
			top = Math.Max(top, cents);
		}

		return top;
	}

	/// <summary>The game's name - from the cards themselves ("Arcane Raise Trading Card") when the app list doesn't know it.</summary>
	internal static string GameOf(uint app, InventoryContents inventory) {
		if (GameNames.IsKnown(app)) {
			return GameNames.Of(app);
		}

		string id = app.ToString(System.Globalization.CultureInfo.InvariantCulture);

		foreach (System.Text.Json.JsonElement d in inventory.Descriptions.Values) {
			string type = InventoryContents.Text(d, "type");

			if ((InventoryContents.Text(d, "market_fee_app") == id) && type.EndsWith(" Trading Card", StringComparison.Ordinal) && !type.Contains("Foil", StringComparison.Ordinal)) {
				return type[..^" Trading Card".Length];
			}
		}

		return GameNames.Of(app);
	}
}
