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
		public Task<long?> SwapCostAsync(Bot? bot, uint app, string hash, CancellationToken ct) => Task.FromResult(bot == null ? null : CostOf(bot, app, hash, DateTime.UtcNow));
	}

	/// <summary>
	/// What getting this card by a swap costs: 0 when it's already on its way, null when no swap is sure to bring it. Only a
	/// swap whose offer is out and that this account checked counts - the offer Steam made, with this very card coming in
	/// by Steam's own name for it (game and all), from a partner that never sent a bad offer - and only while card swaps
	/// are on, scam watch isn't holding its trades, and it isn't a game it bought cards of lately. A match not acted on
	/// yet doesn't: the names nocat.lol hands over are only what the partner said, and a partner could name any card.
	/// Conservative: not sure is "no swap", and the card is bought.
	/// </summary>
	public static long? CostOf(Bot bot, uint app, string hash, DateTime nowUtc) {
		// A game it bought cards of lately isn't swapped from (its cards stay for the craft), so no swap in it is coming.
		if (string.IsNullOrEmpty(hash) || !NameFits(hash, app) || (bot.SteamId == 0) || !On(bot.Cfg, bot.Name) || ScamWatch.HoldsTrades(bot)
			|| BoughtGames(bot.Name, nowUtc).Contains(app)) {
			return null;
		}

		Ledger ledger = Ledger.Load(bot.Name);
		HashSet<string> open = [.. OpenIds(bot.Name, bot.SteamId, nowUtc)];

		return MatchesOf(bot.SteamId).Any(m => open.Contains(m.Id) && !ledger.IsBlocked(m.You)
			&& (ledger.Of(m.Id) is { State: "sent" } r) && r.Gets.Contains(hash, StringComparer.Ordinal)) ? 0 : null;
	}

	/// <summary>
	/// How long a game whose cards were bought for a badge is kept out of swaps, spare items, sets sold and dear cards: until
	/// its badge is crafted after the last purchase - 1.8.2: a month at most (a week let a set bought for a badge be sold).
	/// </summary>
	public static readonly TimeSpan BoughtFor = TimeSpan.FromDays(30);

	/// <summary>Games this account bought cards of for a badge in the last month and hasn't crafted since: their cards stay for the craft.</summary>
	public static HashSet<uint> BoughtGames(string account, DateTime nowUtc) => BuyLedger.Of(account).BoughtSince(nowUtc - BoughtFor);

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
