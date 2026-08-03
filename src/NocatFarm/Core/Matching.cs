using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>
/// Swapping duplicate trading cards between your OWN accounts, so sets finish instead of sitting half-built.
///
/// Farming leaves every account with the same shape of problem: three copies of one card in a set and none of
/// another, because drops are random and a badge needs one of each. ArchiSteamFarm answers this by matching you
/// with strangers through a third-party service; this does it inside your own fleet, which needs no service, no
/// account of yours in a public pool, and no trust in anybody.
///
/// A swap only counts if it helps BOTH sides: each account hands over a card it has spare and receives one it is
/// missing. Anything one-sided is just moving cards around, and would quietly strip an account that happens to
/// farm more than the others.
/// </summary>
public static partial class Matching {
	/// <summary>One card moving one way.</summary>
	public sealed record Move(uint App, string Game, string Card, Looting.Item Item);

	/// <summary>A pair of accounts and the cards they should exchange, one for one.</summary>
	public sealed record Swap(Bot From, Bot To, List<Move> Give, List<Move> Take) {
		public int Cards => Math.Min(Give.Count, Take.Count);
	}

	/// <summary>
	/// What each account holds, per game: how many of each card, so duplicates and gaps are both visible.
	///
	/// Steam does not say which cards a set contains - only what you own - so "missing" here means "a card
	/// another of your accounts has that this one does not", which is exactly the set of swaps that can be made
	/// without asking anybody anything.
	/// </summary>
	private static Dictionary<uint, Dictionary<string, List<Looting.Item>>> CardsOf(IEnumerable<Looting.Item> items) {
		Dictionary<uint, Dictionary<string, List<Looting.Item>>> byGame = [];

		foreach (Looting.Item item in items) {
			if (!item.Type.Contains("Trading Card", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			// Foils are their own set and are worth many times a normal card - never swap one for a plain card.
			uint key = item.Type.Contains("Foil", StringComparison.OrdinalIgnoreCase) ? item.App + 1_000_000 : item.App;

			if (!byGame.TryGetValue(key, out Dictionary<string, List<Looting.Item>>? cards)) {
				byGame[key] = cards = new Dictionary<string, List<Looting.Item>>(StringComparer.OrdinalIgnoreCase);
			}

			string name = CardName(item.Name);

			if (!cards.TryGetValue(name, out List<Looting.Item>? held)) {
				cards[name] = held = [];
			}

			held.Add(item);
		}

		return byGame;
	}

	/// <summary>"Zoe (Trading Card)" and "Zoe" are the same card.</summary>
	private static string CardName(string name) => Trailing().Replace(name, "").Trim();

	/// <summary>
	/// Work out every worthwhile exchange across the fleet.
	///
	/// Greedy and deliberately simple: for each pair of accounts and each game, hand over what one has spare and
	/// the other lacks, and take back the reverse, one for one, until one side runs out. A cleverer optimiser
	/// would gain very little - the constraint is what the accounts actually hold, not how the swaps are chosen.
	/// </summary>
	public static List<Swap> Plan(IReadOnlyDictionary<Bot, List<Looting.Item>> inventories) {
		List<Swap> swaps = [];
		List<Bot> bots = [.. inventories.Keys];

		Dictionary<Bot, Dictionary<uint, Dictionary<string, List<Looting.Item>>>> cards =
			bots.ToDictionary(static b => b, b => CardsOf(inventories[b]));

		for (int i = 0; i < bots.Count; i++) {
			for (int j = i + 1; j < bots.Count; j++) {
				Bot a = bots[i];
				Bot b = bots[j];
				List<Move> aGives = [];
				List<Move> bGives = [];

				foreach ((uint game, Dictionary<string, List<Looting.Item>> aCards) in cards[a]) {
					if (!cards[b].TryGetValue(game, out Dictionary<string, List<Looting.Item>>? bCards)) {
						continue;   // b has nothing from this game, so nothing here is a two-way trade
					}

					// Spare on one side, absent on the other - in both directions.
					List<Move> fromA = [.. Spares(aCards, bCards, game)];
					List<Move> fromB = [.. Spares(bCards, aCards, game)];

					int pairs = Math.Min(fromA.Count, fromB.Count);

					if (pairs > 0) {
						aGives.AddRange(fromA.Take(pairs));
						bGives.AddRange(fromB.Take(pairs));
					}
				}

				if (aGives.Count > 0) {
					swaps.Add(new Swap(a, b, aGives, bGives));
				}
			}
		}

		return swaps;
	}

	/// <summary>Cards the giver has more than one of and the receiver has none of.</summary>
	private static IEnumerable<Move> Spares(
		Dictionary<string, List<Looting.Item>> giver,
		Dictionary<string, List<Looting.Item>> receiver,
		uint game) {
		foreach ((string card, List<Looting.Item> held) in giver) {
			if ((held.Count < 2) || receiver.ContainsKey(card)) {
				continue;   // not spare, or they already have one
			}

			// Only ever the surplus. Handing over the last copy would break the giver's own set to fix somebody
			// else's, which is not a match - it is a donation.
			for (int copy = 1; copy < held.Count; copy++) {
				yield return new Move(held[copy].App, GameNames.Of(held[copy].App), card, held[copy]);
			}
		}
	}

	[GeneratedRegex(@"\s*\((?:Trading Card|Foil Trading Card|Foil)\)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex Trailing();
}
