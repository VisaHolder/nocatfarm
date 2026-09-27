using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>
/// Swapping duplicate trading cards between your OWN accounts, so sets finish instead of sitting half-built.
///
/// Farming leaves every account with the same shape of problem: three copies of one card in a set and none of
/// another, because drops are random and a badge needs one of each. Matching with strangers needs a third-party
/// service; this does it inside your own fleet, which needs no service, no account of yours in a public pool, and
/// no trust in anybody.
///
/// A swap only counts if it helps BOTH sides, by the same rule <see cref="Modules.FairSwap"/> judges an incoming
/// offer with: each account gives a card it holds MORE copies of than of the card it gets back (by at least two),
/// so every offer this plans is one the other account's own check accepts. Anything one-sided is just moving cards
/// around, and would quietly strip an account that happens to farm more than the others.
/// </summary>
public static class Matching {
	/// <summary>One card moving one way.</summary>
	public sealed record Move(uint App, string Game, string Card, Looting.Item Item);

	/// <summary>
	/// A pair of accounts and the cards they should exchange. <see cref="Give"/> and <see cref="Take"/> are paired by
	/// position, each pair from the same game.
	/// </summary>
	public sealed record Swap(Bot From, Bot To, List<Move> Give, List<Move> Take) {
		public int Cards => Math.Min(Give.Count, Take.Count);
	}

	/// <param name="Swaps">The swaps to send now - each account in at most one.</param>
	/// <param name="Deferred">More pairs that could swap once these are done.</param>
	public sealed record MatchPlan(List<Swap> Swaps, int Deferred);

	/// <summary>One offer's worth - Steam copes with more, but a big offer is harder to confirm and more to lose to one refusal.</summary>
	public const int MaxPairsPerSwap = 100;

	/// <summary>A card: its game and which one. The class id, not the name - two cards in a game can share a name.</summary>
	private readonly record struct Card(uint Game, ulong ClassId);

	/// <summary>
	/// The account's ordinary (non-foil) trading cards - just the Steam inventory, not every game's. Untradable copies
	/// are included: they count as held, they just can't be offered. Null when the inventory couldn't be read in full,
	/// because a half-read inventory makes the account look like it lacks cards it has.
	/// </summary>
	public static async Task<List<Looting.Item>?> CardsAsync(Bot bot, CancellationToken ct = default) {
		InventoryContents? inventory = await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory is not { Complete: true }) {
			return null;
		}

		List<Looting.Item> cards = [];

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			// Foils aren't matched: the swap check on the other side only takes ordinary cards, so a foil offer would sit.
			if (!type.EndsWith("Trading Card", StringComparison.Ordinal) || type.Contains("Foil", StringComparison.Ordinal)
				|| !uint.TryParse(InventoryContents.Text(d, "market_fee_app"), out uint game) || (game == 0)
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), out ulong assetId)) {
				continue;
			}

			ulong.TryParse(InventoryContents.Text(asset, "classid"), out ulong classId);
			ulong.TryParse(InventoryContents.Text(asset, "instanceid"), out ulong instanceId);
			uint amount = uint.TryParse(InventoryContents.Text(asset, "amount"), out uint n) ? Math.Max(1, n) : 1;
			bool tradable = InventoryContents.Text(d, "tradable") == "1";

			cards.Add(new Looting.Item(assetId, classId, instanceId, amount, type, InventoryContents.Text(d, "name"), 753, 6, game, tradable));
		}

		return cards;
	}

	/// <summary>The swaps worth making right now. See <see cref="PlanAll"/>.</summary>
	public static List<Swap> Plan(IReadOnlyDictionary<Bot, List<Looting.Item>> inventories, IReadOnlySet<ulong>? busy = null, IReadOnlySet<(Bot, Bot)>? busyPairs = null) =>
		PlanAll(inventories, busy, busyPairs).Swaps;

	/// <summary>
	/// Work out the swaps worth making across the fleet.
	/// </summary>
	/// <remarks>
	/// <para>Each account goes into at most ONE swap per run, planned and checked against the cards it really holds.
	/// Planning several at once against a picture where earlier swaps had already landed let a later offer hand over
	/// an account's only real copy of a card - fine if every offer went through, a lost set if one didn't. The pair
	/// with the most to swap goes first; the rest wait for the next run, once these are done.</para>
	/// <para>Cards on a live offer don't count at all - not as held, not as spare - and a pair that already has an
	/// offer waiting between them is left until it's settled, so running this twice can't plan the same swap again.</para>
	/// </remarks>
	/// <param name="busy">Assets on either side of any live offer.</param>
	/// <param name="busyPairs">Pairs of accounts with an offer already waiting between them (either way round).</param>
	public static MatchPlan PlanAll(IReadOnlyDictionary<Bot, List<Looting.Item>> inventories, IReadOnlySet<ulong>? busy = null, IReadOnlySet<(Bot, Bot)>? busyPairs = null) {
		List<Bot> bots = [.. inventories.Keys];
		Dictionary<Bot, Dictionary<Card, int>> held = [];
		Dictionary<Bot, Dictionary<Card, List<Looting.Item>>> spare = [];
		Dictionary<Card, string> names = [];

		foreach (Bot bot in bots) {
			held[bot] = [];
			spare[bot] = [];

			foreach (Looting.Item item in inventories[bot].Where(i => (i.Game != 0) && (busy?.Contains(i.AssetId) != true))) {
				Card card = new(item.Game, item.ClassId);
				held[bot][card] = held[bot].GetValueOrDefault(card) + (int) item.Amount;
				names.TryAdd(card, CardName(item.Name));

				if (item.Tradable) {
					if (!spare[bot].TryGetValue(card, out List<Looting.Item>? list)) {
						spare[bot][card] = list = [];
					}

					list.Add(item);
				}
			}
		}

		List<Swap> candidates = [];

		for (int i = 0; i < bots.Count; i++) {
			for (int j = i + 1; j < bots.Count; j++) {
				if ((busyPairs?.Contains((bots[i], bots[j])) == true) || (busyPairs?.Contains((bots[j], bots[i])) == true)) {
					continue;
				}

				if (PlanPair(bots[i], bots[j], held, spare, names) is { } swap) {
					candidates.Add(swap);
				}
			}
		}

		List<Swap> chosen = [];
		HashSet<Bot> used = [];

		foreach (Swap swap in candidates.OrderByDescending(static s => s.Give.Count)) {
			if (!used.Contains(swap.From) && !used.Contains(swap.To)) {
				chosen.Add(swap);
				used.Add(swap.From);
				used.Add(swap.To);
			}
		}

		return new MatchPlan(chosen, candidates.Count - chosen.Count);
	}

	/// <summary>One pair's swap against the real cards of both, or null when nothing would help both sides.</summary>
	private static Swap? PlanPair(Bot a, Bot b, Dictionary<Bot, Dictionary<Card, int>> realHeld,
		Dictionary<Bot, Dictionary<Card, List<Looting.Item>>> realSpare, Dictionary<Card, string> names) {
		// Working copies: moves are tried out on these, the real counts stay what the offer is judged against.
		Dictionary<Card, int> heldA = new(realHeld[a]);
		Dictionary<Card, int> heldB = new(realHeld[b]);
		Dictionary<Card, Queue<Looting.Item>> spareA = realSpare[a].ToDictionary(static s => s.Key, static s => new Queue<Looting.Item>(s.Value));
		Dictionary<Card, Queue<Looting.Item>> spareB = realSpare[b].ToDictionary(static s => s.Key, static s => new Queue<Looting.Item>(s.Value));

		List<Move> give = [];
		List<Move> take = [];

		foreach (uint game in heldA.Keys.Select(static c => c.Game).Intersect(heldB.Keys.Select(static c => c.Game)).ToList()) {
			List<Move> gameGive = [];
			List<Move> gameTake = [];

			while ((give.Count + gameGive.Count < MaxPairsPerSwap) && (Best(game) is ({ } x, { } y))) {
				Looting.Item fromA = spareA[x].Dequeue();
				Looting.Item fromB = spareB[y].Dequeue();

				heldA[x]--;
				heldA[y] = heldA.GetValueOrDefault(y) + 1;
				heldB[y]--;
				heldB[x] = heldB.GetValueOrDefault(x) + 1;

				gameGive.Add(new Move(game, GameNames.Of(game), names[x], fromA));
				gameTake.Add(new Move(game, GameNames.Of(game), names[y], fromB));
			}

			// Checked the way each side's own swap test will check it: the whole game's moves against the cards held
			// before the offer. Each game is judged on its own, so a game that doesn't pass is trimmed alone and
			// never costs the others their swaps.
			while ((gameGive.Count > 0) && !(PassesFor(gameGive, gameTake, realHeld[a]) && PassesFor(gameTake, gameGive, realHeld[b]))) {
				gameGive.RemoveAt(gameGive.Count - 1);
				gameTake.RemoveAt(gameTake.Count - 1);
			}

			give.AddRange(gameGive);
			take.AddRange(gameTake);
		}

		return give.Count > 0 ? new Swap(a, b, give, take) : null;

		// The exchange that helps both most: a gives x (a pile at least two bigger than its y), b gives y (likewise).
		(Card?, Card?) Best(uint game) {
			(Card?, Card?) best = (null, null);
			int bestScore = 0;

			foreach ((Card x, Queue<Looting.Item> xs) in spareA.Where(s => (s.Key.Game == game) && (s.Value.Count > 0))) {
				foreach ((Card y, Queue<Looting.Item> ys) in spareB.Where(s => (s.Key.Game == game) && (s.Key != x) && (s.Value.Count > 0))) {
					int forA = heldA.GetValueOrDefault(x) - heldA.GetValueOrDefault(y);
					int forB = heldB.GetValueOrDefault(y) - heldB.GetValueOrDefault(x);

					if ((forA > 1) && (forB > 1) && (forA + forB > bestScore)) {
						best = (x, y);
						bestScore = forA + forB;
					}
				}
			}

			return best;
		}
	}

	/// <summary>Whether an account holding <paramref name="before"/> would pass giving these for those.</summary>
	private static bool PassesFor(List<Move> giving, List<Move> getting, Dictionary<Card, int> before) =>
		Modules.FairSwap.Judge(
			[.. giving.Select(static m => new Modules.FairSwap.Card(m.Item.Game, m.Item.ClassId))],
			[.. getting.Select(static m => new Modules.FairSwap.Card(m.Item.Game, m.Item.ClassId))],
			before.ToDictionary(static kv => new Modules.FairSwap.Card(kv.Key.Game, kv.Key.ClassId), static kv => kv.Value)).Fair;

	/// <summary>"Zoe (Trading Card)" and "Zoe" are the same card.</summary>
	private static string CardName(string name) {
		foreach (string suffix in new[] { " (Trading Card)", " (Foil Trading Card)", " (Foil)" }) {
			if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
				return name[..^suffix.Length].Trim();
			}
		}

		return name.Trim();
	}
}
