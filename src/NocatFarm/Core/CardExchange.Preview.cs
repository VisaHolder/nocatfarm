namespace NocatFarm.Core;

public static partial class CardExchange {
	/// <summary>What turning card swaps on would offer now: games and spare cards it could swap, and cards still waiting for a price.</summary>
	public sealed record Spares(int Games, int Cards, int Unpriced);

	/// <summary>The counts for the preview, from the inventory and what's on offers - the same rules the list keeps to.</summary>
	public static Spares Count(IReadOnlyList<OwnCard> cards, IReadOnlySet<ulong> busy, Rules rules, Func<string, bool> priced) {
		Dictionary<SwapRules.Card, List<ulong>> give = Giveable(cards, busy, rules);
		Dictionary<SwapRules.Card, string> hashes = [];

		foreach (OwnCard c in cards) {
			hashes.TryAdd(new SwapRules.Card(c.Game, c.ClassId), c.Hash);
		}

		return new Spares(give.Keys.Select(static k => k.Game).Distinct().Count(), give.Values.Sum(static v => v.Count),
			give.Where(g => !priced(hashes.GetValueOrDefault(g.Key) ?? "")).Sum(static g => g.Value.Count));
	}

	/// <summary>The limits it keeps to, in English for the console.</summary>
	public static string Limits(Bot bot) {
		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;
		string loss = Spare.Money(MaxLossCents(bot.Cfg), currency);

		return $"It swaps normal cards only, one for one in the same game, at most {PerDay(bot.Cfg)} a day{(bot.Cfg.LegitMode ? " (in its own day, spread out)" : "")}, "
			+ $"each worth at most {loss} less than what it gives. Never a card your sets need, the last copy of one, or anything on your never-touch lists"
			+ $"{(bot.Cfg.CardSwapSkipHeld ? ", and never when Steam would hold the trade" : "")}. "
			+ (bot.CanConfirmTrades ? "Its offers are confirmed by its authenticator here. " : "Its offers wait for you to confirm them on your phone. ")
			+ "While it's on, nocat.lol keeps this account's SteamID and trade link; turning it off deletes them.";
	}

	/// <summary>One look for the preview: the console's text and the numbers, read from the inventory as it is now.</summary>
	public static async Task<(string Text, Spares? Spares, string Problem)> PreviewAsync(Bot bot, CancellationToken ct) {
		if (!bot.IsOnline || !bot.Web.Ready) {
			return ($"{bot.Name}: not signed in, so it can't look at the inventory yet.{Environment.NewLine}{Limits(bot)}", null, "");
		}

		if (await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false) is not { Complete: true } inventory) {
			return ($"{bot.Name}: couldn't read its inventory - try again in a minute.{Environment.NewLine}{Limits(bot)}", null, "couldn't read its inventory");
		}

		if (await TradeOffers.ActiveAsync(bot, received: true, sent: true, ct).ConfigureAwait(false) is not { } live) {
			return ($"{bot.Name}: Steam didn't say what's on its offers - try again in a minute.{Environment.NewLine}{Limits(bot)}", null, "Steam didn't say what's on its offers");
		}

		DateTime now = DateTime.UtcNow;
		HashSet<ulong> busy = [.. TradeOffers.Committed(live), .. Reserved(bot.Name, bot.SteamId, now)];
		Spares spares = Count(ReadCards(inventory), busy, RulesOf(bot, now),
			hash => (hash.IndexOf('-') is int dash and > 0) && uint.TryParse(hash.AsSpan(0, dash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint app)
				&& (SparePrices.Cached(app) is { } g) && (now - g.When <= PriceFor) && (SparePrices.UsdCents(hash) != null));
		string what = spares.Cards == 0
			? $"{bot.Name}: no spare cards to swap right now - nothing would happen until there are."
			: $"{bot.Name}: {spares.Cards} spare card(s) in {spares.Games} game(s) could be swapped{(spares.Unpriced > 0 ? $" ({spares.Unpriced} still need a price first)" : "")}.";

		return ($"{what}{Environment.NewLine}{Limits(bot)}", spares, "");
	}
}
