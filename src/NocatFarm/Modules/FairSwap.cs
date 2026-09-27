using System.Globalization;
using System.Text.Json;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Is this trade offer a fair swap of trading cards - one that can only bring this account's sets closer to done?
/// </summary>
/// <remarks>
/// Card-swapping sites send offers like this all day: a spare copy of one card for a card you lack, same game, one
/// for one. The rule here is our own and deliberately simple to check by hand. Every item on both sides must be an
/// ordinary trading card, each game must get back as many cards as it gives, and - game by game - every card given
/// away must still have MORE copies afterwards than any card coming in had before. Copies only ever move from a
/// bigger pile to a smaller one, which means no card can drop to nothing, the number of different cards held never
/// shrinks, and the number of complete sets (the smallest pile) can never go down.
/// </remarks>
internal static class FairSwap {
	/// <summary>One trading card: the game it's from and which card it is.</summary>
	internal readonly record struct Card(uint Game, ulong ClassId);

	/// <summary>The rule itself, on its own so it can be tested without Steam.</summary>
	/// <param name="ours">How many of each card this account holds, tradable or not.</param>
	internal static (bool Fair, Said Why) Judge(IReadOnlyList<Card> giving, IReadOnlyList<Card> receiving, IReadOnlyDictionary<Card, int> ours) {
		if ((giving.Count == 0) || (receiving.Count == 0)) {
			return (false, new Said("one side is empty"));
		}

		foreach (uint game in giving.Select(static c => c.Game).Union(receiving.Select(static c => c.Game))) {
			// What the trade does to each card of this game: positive comes in, negative goes out.
			Dictionary<ulong, int> change = [];

			foreach (Card c in giving.Where(c => c.Game == game)) {
				change[c.ClassId] = (change.TryGetValue(c.ClassId, out int n) ? n : 0) - 1;
			}

			foreach (Card c in receiving.Where(c => c.Game == game)) {
				change[c.ClassId] = (change.TryGetValue(c.ClassId, out int n) ? n : 0) + 1;
			}

			if (change.Values.Sum() != 0) {
				return (false, new Said("not one for one within each game"));
			}

			int Have(ulong card) => ours.TryGetValue(new Card(game, card), out int n) ? n : 0;

			List<int> goingAfter = [];
			List<int> comingBefore = [];

			foreach ((ulong card, int delta) in change) {
				if (delta < 0) {
					int after = Have(card) + delta;

					if (after < 0) {
						return (false, new Said("asks for cards this account doesn't have"));
					}

					goingAfter.Add(after);
				} else if (delta > 0) {
					comingBefore.Add(Have(card));
				}
			}

			// Copies only from a bigger pile to a smaller one.
			if ((goingAfter.Count > 0) && (comingBefore.Count > 0) && (goingAfter.Min() <= comingBefore.Max())) {
				return (false, new Said("it would set back the {0} set", GameNames.Of(game)));
			}
		}

		return (true, new Said(""));
	}

	/// <summary>
	/// The offer's two sides as cards, or null when anything on it is not an ordinary (non-foil) trading card with a
	/// known game - a background, an emoticon, a foil, gems, a CS2 skin.
	/// </summary>
	internal static (List<Card> Giving, List<Card> Receiving)? CardsOf(TradeOffers.Offer offer) {
		static bool IsCard(TradeOffers.Item i) => (i.App == 753) && (i.Context == "6") && (i.Amount == 1) && (i.Game != 0)
			&& i.Type.EndsWith("Trading Card", StringComparison.Ordinal) && !i.Type.Contains("Foil", StringComparison.Ordinal);

		if (!offer.Giving.All(IsCard) || !offer.Receiving.All(IsCard)) {
			return null;
		}

		return ([.. offer.Giving.Select(static i => new Card(i.Game, i.ClassId))], [.. offer.Receiving.Select(static i => new Card(i.Game, i.ClassId))]);
	}

	/// <summary>The ordinary trading cards an offer would take out of this account - whatever else is on it.</summary>
	internal static List<Card> CardsLeaving(TradeOffers.Offer offer) => [.. offer.Giving
		.Where(static i => (i.App == 753) && (i.Context == "6") && (i.Game != 0)
			&& i.Type.EndsWith("Trading Card", StringComparison.Ordinal) && !i.Type.Contains("Foil", StringComparison.Ordinal))
		.Select(static i => new Card(i.Game, i.ClassId))];

	/// <summary>
	/// Judge an offer against this account's cards as they are right now. Null when that couldn't be worked out -
	/// looked at again rather than decided on.
	/// </summary>
	/// <param name="alsoLeaving">Cards already promised to offers accepted but still waiting on a confirmation. They
	/// are still in the inventory, but they are going - counting them as held would let two swaps spend one card.</param>
	internal static async Task<(bool? Fair, Said Why)> JudgeAsync(Bot bot, TradeOffers.Offer offer, IReadOnlyCollection<Card> alsoLeaving, CancellationToken ct) {
		if (offer.State != TradeOffers.Active) {
			return (false, new Said("it isn't an active offer any more"));
		}

		// Steam sometimes leaves descriptions out. That is not "not a card", it's "don't know yet".
		if (offer.Giving.Concat(offer.Receiving).Any(static i => !i.Described)) {
			return (null, new Said("Steam didn't say what every item is"));
		}

		if (CardsOf(offer) is not { } cards) {
			return (false, new Said("not all ordinary trading cards"));
		}

		// Quick no before reading the whole inventory.
		if ((cards.Giving.Count == 0) || (cards.Giving.Count != cards.Receiving.Count)) {
			return (false, new Said("not one for one"));
		}

		InventoryContents? inventory = await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory is not { Complete: true }) {
			return (null, new Said("couldn't read this account's cards"));
		}

		Dictionary<Card, int> ours = [];

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = Text(d, "type");

			if (!type.EndsWith("Trading Card", StringComparison.Ordinal) || type.Contains("Foil", StringComparison.Ordinal)) {
				continue;
			}

			Card card = new(Num(Text(d, "market_fee_app")), Num64(Text(asset, "classid")));
			int amount = (int) Math.Max(1, Num(Text(asset, "amount")));
			ours[card] = (ours.TryGetValue(card, out int n) ? n : 0) + amount;
		}

		foreach (Card leaving in alsoLeaving) {
			if (ours.TryGetValue(leaving, out int n)) {
				ours[leaving] = Math.Max(0, n - 1);
			}
		}

		return Judge(cards.Giving, cards.Receiving, ours);
	}

	/// <summary>The same test by offer id - what the 'fairswap' command asks.</summary>
	internal static async Task<(bool? Fair, Said Why)> CheckAsync(Bot bot, ulong offerId, CancellationToken ct) {
		(bool answered, TradeOffers.Offer? found) = await TradeOffers.OneAsync(bot, offerId, ct).ConfigureAwait(false);

		if (!answered) {
			return (null, new Said("Steam didn't answer"));
		}

		if (found is not { } offer) {
			return (false, new Said("Steam has no such offer for this account"));
		}

		if (offer.Ours) {
			return (false, new Said("that's an offer this account sent"));
		}

		return await JudgeAsync(bot, offer, [], ct).ConfigureAwait(false);
	}

	private static string Text(JsonElement e, string name) => InventoryContents.Text(e, name);

	private static uint Num(string s) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) ? n : 0;

	private static ulong Num64(string s) => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n) ? n : 0;
}
