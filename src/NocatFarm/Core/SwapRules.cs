namespace NocatFarm.Core;

/// <summary>
/// 1.8.2 card swaps with other nocat.farm users: the three rules every swap has to pass - checked by nocat.lol when it
/// pairs two accounts, again by the account that sends the offer, and once more by the one that accepts it.
/// </summary>
/// <remarks>
/// The set rule is ArchiSteamFarm's own, copied as it is (ArchiSteamFarm/Steam/Exchange/Trading.cs):
/// <see cref="OneForOne"/> is its IsFairExchange (within each game as many cards come in as go out) and
/// <see cref="NeutralOrBetter"/> its IsTradeNeutralOrBetter. For each game, the copies held are put in order, smallest
/// pile first, before and after the swap. Fewer different cards after is always a loss. Otherwise the two lists are
/// walked from the smallest pile up, adding up what each place gains or loses - and that sum may never drop below zero
/// at any point, because that would mean the set lost progress somewhere, even if it came out ahead by the end.
/// For example 3 1 0 → 2 1 1 passes (a card that was missing came in), 2 2 → 3 1 doesn't.
/// </remarks>
public static class SwapRules {
	/// <summary>One normal trading card: its game and which card it is (Steam's class id).</summary>
	public readonly record struct Card(uint Game, ulong ClassId);

	/// <summary>ASF's IsFairExchange for trading cards: in every game as many cards come in as go out, and something does.</summary>
	public static bool OneForOne(IReadOnlyCollection<Card> giving, IReadOnlyCollection<Card> receiving) {
		if ((giving.Count == 0) || (giving.Count != receiving.Count)) {
			return false;
		}

		foreach (uint game in giving.Select(static c => c.Game).Union(receiving.Select(static c => c.Game))) {
			if (giving.Count(c => c.Game == game) != receiving.Count(c => c.Game == game)) {
				return false;
			}
		}

		return true;
	}

	/// <summary>ASF's IsTradeNeutralOrBetter: no game this account gives in may lose set progress.</summary>
	/// <param name="held">Copies of each card held now - tradable or not, everything but what's already leaving.</param>
	public static bool NeutralOrBetter(IReadOnlyDictionary<Card, int> held, IReadOnlyCollection<Card> giving, IReadOnlyCollection<Card> receiving) {
		// ASF looks only at the sets it gives from: a game it only gets cards in can't lose anything.
		foreach (uint game in giving.Select(static c => c.Game).Distinct()) {
			Dictionary<ulong, int> after = held.Where(h => (h.Key.Game == game) && (h.Value > 0)).ToDictionary(static h => h.Key.ClassId, static h => h.Value);
			List<int> before = [.. after.Values.Order()];

			foreach (Card c in giving.Where(c => c.Game == game)) {
				if (after.GetValueOrDefault(c.ClassId) <= 0) {
					return false;   // giving a copy it doesn't have
				}

				after[c.ClassId]--;
			}

			foreach (Card c in receiving.Where(c => c.Game == game)) {
				after[c.ClassId] = after.GetValueOrDefault(c.ClassId) + 1;
			}

			List<int> afterAmounts = [.. after.Values.Where(static n => n > 0).Order()];

			// Fewer different cards is always worse (1 1 → 0 2).
			if (afterAmounts.Count < before.Count) {
				return false;
			}

			// Cards that were missing came in: they were piles of 0.
			while (before.Count < afterAmounts.Count) {
				before.Insert(0, 0);
			}

			int neutrality = 0;

			for (int i = 0; i < afterAmounts.Count; i++) {
				neutrality += afterAmounts[i] - before[i];

				if (neutrality < 0) {
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>The price rule: what comes in is worth at least what goes out, less the most a swap may lose.</summary>
	/// <param name="maxLossCents">"Most a swap may lose", in the same money. 0: a swap is never worth less.</param>
	public static bool PriceOk(long giveCents, long getCents, long maxLossCents) => getCents >= giveCents - Math.Max(0, maxLossCents);
}
