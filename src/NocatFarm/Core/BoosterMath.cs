namespace NocatFarm.Core;

/// <summary>
/// The two ways of putting a worth on a booster pack's 3 random cards - the guaranteed (worst) pull and the average pull -
/// shared by buying cards (<see cref="Modules.CardBuyer"/>) and selling packs (<see cref="Modules.GemsChoice"/> and
/// <see cref="Spare.PlanPacks"/>) so both mean the same thing. Each card is given AFTER Steam's fee - what the owner would
/// get (<see cref="Seller.ReceiveFor"/>), in whatever money the caller is using - and the answer is in that same money.
/// Pure numbers, no Steam.
/// </summary>
/// <remarks>
/// A booster pack is 3 random cards of the game's set. The two pulls:
/// <list type="bullet">
/// <item>the <b>guaranteed</b> pull is the worst it can do - the three cheapest cards of the set, three times the worth of
/// the cheapest (<see cref="Guarantee"/>); opening a pack always gets at least this;</item>
/// <item>the <b>average</b> pull is three random cards, each with a 1 in 100 chance of being a foil (<see cref="Average"/>) -
/// 99% a normal card of average worth, 1% a foil of average worth, three times.</item>
/// </list>
/// Nothing here reads the market or spends anything: it is the numbers behind a decision made elsewhere.
/// </remarks>
public static class BoosterMath {
	/// <summary>Chance each of a pack's three cards is a foil - Steam's one in a hundred, 1%.</summary>
	public const double FoilChance = 0.01;

	/// <summary>
	/// The guaranteed (worst) pull: three times the worth of the cheapest card. A card with no worth counts; zero when there
	/// are no cards at all.
	/// </summary>
	public static long Guarantee(IReadOnlyList<long> receive) => receive.Count == 0 ? 0 : 3L * receive.Min();

	/// <summary>
	/// The average pull: three random cards, each 99% a normal card and 1% a foil - three times the worth of each, the two
	/// averaged together. No foil worth known counts as none, so it never hopes for a foil it hasn't priced. Zero when no
	/// normal cards are known.
	/// </summary>
	public static long Average(IReadOnlyList<long> normalReceive, IReadOnlyList<long> foilReceive) {
		if (normalReceive.Count == 0) {
			return 0;
		}

		double normal = normalReceive.Average();
		double foil = foilReceive.Count > 0 ? foilReceive.Average() : normal;   // a foil no closer than its normal card, when not priced
		double ev = 3.0 * (((1.0 - FoilChance) * normal) + (FoilChance * foil));

		return (long) Math.Round(ev, MidpointRounding.ToZero);
	}
}