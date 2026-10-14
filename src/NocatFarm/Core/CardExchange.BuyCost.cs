using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// Card swaps and buying cards, side by side (1.8.2): what a missing card would cost by a swap, so buying waits for a
/// swap only when one is really coming - and what each keeps away from the other.
/// </summary>
public static partial class CardExchange {
	/// <summary>
	/// Buying cards asks this (<see cref="SwapCost.Current"/>) for each card it's about to buy. Conservative: when it isn't
	/// sure a swap is coming, the answer is "no swap" and the card is bought.
	/// </summary>
	public sealed class BuyCosts : ICardSwaps {
		public Task<long?> SwapCostAsync(Bot? bot, uint app, string hash, CancellationToken ct) => Task.FromResult(bot == null ? null
			: CostOf(bot, app, hash, DateTime.UtcNow, h => WalletCents(h, bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency, DateTime.UtcNow)));
	}

	/// <summary>
	/// What getting this card by a swap would cost, in the wallet's currency, or null when no swap for it is likely soon. Only
	/// a match nocat.lol handed this account at its last sync counts - still open, with the card in it coming to this
	/// account, from a partner that never sent a bad offer - and only while card swaps are on, scam watch isn't holding its
	/// trades, and it isn't a game it bought cards of lately. A match with an offer already out costs 0: its card is on the
	/// way, and buying it would be a second copy. One not acted on yet costs what this account gives for it (its spare
	/// card's price); with no fresh price for that, null - not sure, so it's bought.
	/// </summary>
	/// <param name="price">A card's price in the wallet's currency from no more than a day ago, null when there's none.</param>
	public static long? CostOf(Bot bot, uint app, string hash, DateTime nowUtc, Func<string, int?> price) {
		// A game it bought cards of lately isn't swapped from (its cards stay for the craft), so no swap in it is coming.
		if (string.IsNullOrEmpty(hash) || (bot.SteamId == 0) || !On(bot.Cfg, bot.Name) || ScamWatch.HoldsTrades(bot) || BoughtGames(bot.Name, nowUtc).Contains(app)) {
			return null;
		}

		Ledger ledger = Ledger.Load(bot.Name);
		HashSet<string> open = [.. OpenIds(bot.Name, bot.SteamId, nowUtc)];
		long? best = null;

		foreach (Match m in MatchesOf(bot.SteamId).Where(m => open.Contains(m.Id) && !ledger.IsBlocked(m.You))) {
			foreach (Pair p in m.Pairs.Where(p => (p.Game == app) && p.GetName.Equals(hash, StringComparison.Ordinal))) {
				long? cost = ledger.Of(m.Id) is { State: "sent" } ? 0 : price(p.GiveName);

				if ((cost is long c) && ((best == null) || (c < best))) {
					best = c;
				}
			}
		}

		return best;
	}

	/// <summary>How long a game whose cards were bought for a badge is kept out of swaps and spare items: until it's crafted, a week at most.</summary>
	public static readonly TimeSpan BoughtFor = TimeSpan.FromDays(7);

	/// <summary>Games this account bought cards of for a badge in the last week: their cards stay for the craft.</summary>
	public static HashSet<uint> BoughtGames(string account, DateTime nowUtc) => BuyLedger.Of(account).GamesSince(nowUtc - BoughtFor);

	/// <summary>What never goes in a swap on this account now: its "never touch" lists, and the games it bought cards of for a badge.</summary>
	public static Rules RulesOf(Bot bot, DateTime nowUtc) {
		Rules rules = Rules.From(bot.Cfg);
		HashSet<uint> bought = BoughtGames(bot.Name, nowUtc);

		return bought.Count == 0 ? rules : rules with { KeepGames = [.. rules.KeepGames.Union(bought)] };
	}

	/// <summary>
	/// Items not to sell: those in a waiting trade (<paramref name="promised"/>) and this account's cards promised to an open
	/// card swap. Selling one would break the swap nocat.lol paired.
	/// </summary>
	public static HashSet<ulong> NotForSale(Bot bot, IEnumerable<ulong> promised, DateTime nowUtc) =>
		[.. promised, .. Reserved(bot.Name, bot.SteamId, nowUtc)];
}
