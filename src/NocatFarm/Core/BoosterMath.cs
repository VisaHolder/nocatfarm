namespace NocatFarm.Core;

/// <summary>
/// The numbers behind booster packs, shared by selling packs (<see cref="Modules.GemsChoice"/> and <see cref="Spare.PlanPacks"/>)
/// and buying them (<see cref="Modules.BuyPlan"/>) so both mean the same thing. Pure numbers, no Steam.
/// </summary>
/// <remarks>
/// A booster pack is 3 random cards of the game's whole set, each card as likely as the next. For a pack held:
/// <list type="bullet">
/// <item>the <b>guaranteed</b> pull is the worst it can do - three times the cheapest card (<see cref="Guarantee"/>);</item>
/// <item>the <b>average</b> pull is three random cards (<see cref="Average"/>), with a 1 in 100 chance a PACK holds one
/// foil in place of a normal card - counted per pack, not per card, so it never opens a pack on hope.</item>
/// </list>
/// For a pack bought to finish a badge (<see cref="CheapestPacks"/>): what the packs and the cards they're expected to leave
/// still missing cost together, against buying the missing cards straight.
/// </remarks>
public static class BoosterMath {
	/// <summary>Chance a pack holds a foil (one of its 3 cards foil instead of normal) - 1%, once per pack.</summary>
	public const double FoilChance = 0.01;

	/// <summary>How much cheaper (percent) packs must be expected to come out than buying the missing cards straight.</summary>
	public const int PackWinPct = 10;

	/// <summary>
	/// The guaranteed (worst) pull: three times the worth of the cheapest card. Zero when there are no cards - the caller
	/// makes sure every card of the set is priced, or it isn't the worst pull at all.
	/// </summary>
	public static long Guarantee(IReadOnlyList<long> receive) => receive.Count == 0 ? 0 : 3L * receive.Min();

	/// <summary>
	/// The average pull: three random cards, and once per pack a 1% chance one of them is a foil instead - three times the
	/// average card, plus 1% of what a foil adds over a normal one (or takes away, a cheap foil). No foil worth known counts as none, so it never hopes for
	/// a foil it hasn't priced. Zero when no normal cards are known.
	/// </summary>
	public static long Average(IReadOnlyList<long> normalReceive, IReadOnlyList<long> foilReceive) {
		if (normalReceive.Count == 0) {
			return 0;
		}

		double normal = normalReceive.Average();
		double foil = foilReceive.Count > 0 ? foilReceive.Average() : normal;   // a foil no dearer than its normal card, when not priced
		double ev = (3.0 * normal) + (FoilChance * (foil - normal));

		return (long) Math.Round(ev, MidpointRounding.ToZero);
	}

	/// <summary>
	/// The chance a given card of an N-card set comes out of <paramref name="packs"/> packs: 3 draws a pack, each card as
	/// likely as the next, with repeats - 1 - (1 - 1/N)^(3 x packs).
	/// </summary>
	public static double HitChance(int setSize, int packs) =>
		(setSize <= 0) || (packs <= 0) ? 0 : 1.0 - Math.Pow(1.0 - (1.0 / setSize), 3.0 * packs);

	/// <summary>Packs bought instead of cards: how many, and what's expected - the cards still missing after them and what those cost.</summary>
	/// <param name="Packs">Packs to buy.</param>
	/// <param name="PacksCost">What the packs cost, each with its tax.</param>
	/// <param name="LeftCards">Missing cards expected to still be missing after the packs.</param>
	/// <param name="LeftCost">What those are expected to cost to buy afterwards (with their tax).</param>
	/// <param name="Straight">What buying the missing cards straight costs, with their tax.</param>
	public sealed record PackBuy(int Packs, long PacksCost, double LeftCards, double LeftCost, long Straight) {
		/// <summary>The expected cost of the level this way: the packs, and the cards they're expected to leave.</summary>
		public double Expected => PacksCost + LeftCost;

		/// <summary>Whether it beats buying the cards straight by <see cref="PackWinPct"/> percent or more.</summary>
		public bool Wins => (Packs > 0) && (Straight > 0) && (Expected * 100 <= Straight * (100.0 - PackWinPct));
	}

	/// <summary>The most packs ever tried for one level, whatever the set.</summary>
	public const int MaxPacks = 10;

	/// <summary>
	/// The cheapest number of packs for a badge level missing the cards in <paramref name="missingCosts"/> (each its price and
	/// tax) of an <paramref name="setSize"/>-card set, with each pack at <paramref name="packCost"/> (its price and tax): 1 up
	/// to ceil(missing / 3) + 2 packs (never over <see cref="MaxPacks"/>), each weighed by its expected total - the packs, and
	/// every missing card at its price times the chance no pack brings it. A repeat, or a card already held, is worth nothing
	/// here - it only ever makes packs look dearer than they are. Null when there's nothing to weigh (no card missing, no
	/// pack price, a card missing that isn't priced, or more cards missing than the set has).
	/// </summary>
	public static PackBuy? CheapestPacks(int setSize, IReadOnlyList<long> missingCosts, long packCost) {
		int k = missingCosts.Count;

		if ((k == 0) || (setSize <= 0) || (k > setSize) || (packCost <= 0) || missingCosts.Any(static c => c <= 0)) {
			return null;
		}

		long straight = missingCosts.Sum();
		int most = Math.Min(MaxPacks, ((k + 2) / 3) + 2);
		PackBuy? best = null;

		for (int n = 1; n <= most; n++) {
			double miss = 1.0 - HitChance(setSize, n);
			PackBuy here = new(n, n * packCost, k * miss, missingCosts.Sum(c => c * miss), straight);

			if ((best == null) || (here.Expected < best.Expected)) {
				best = here;
			}
		}

		return best;
	}
}
