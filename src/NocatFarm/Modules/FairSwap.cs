using System.Globalization;
using System.Text.Json;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Is this trade offer a fair swap of trading cards - one that can only bring this account's sets closer to done?
/// </summary>
/// <remarks>
/// Steam Trade Matcher users send offers like this all day: your duplicate of card A for their duplicate of card
/// B, same game, one for one. ArchiSteamFarm accepts them under its SteamTradeMatcher setting, and this is the same
/// test. Every item on both sides must be an ordinary trading card; each game must get back exactly as many cards
/// as it gives; and, game by game, the card counts sorted lowest first must never go down at any point along the
/// way - so a trade can even out duplicates (3 of A and 0 of B becoming 2 and 1) but never take away the last copy
/// of a card to give you a third of another.
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

		Dictionary<uint, int> given = giving.GroupBy(static c => c.Game).ToDictionary(static g => g.Key, static g => g.Count());
		Dictionary<uint, int> got = receiving.GroupBy(static c => c.Game).ToDictionary(static g => g.Key, static g => g.Count());

		if ((given.Count != got.Count) || given.Any(g => !got.TryGetValue(g.Key, out int n) || (n != g.Value))) {
			return (false, new Said("not one for one within each game"));
		}

		foreach (uint game in given.Keys) {
			HashSet<ulong> cards = [.. ours.Keys.Where(c => c.Game == game).Select(static c => c.ClassId)];
			cards.UnionWith(giving.Where(c => c.Game == game).Select(static c => c.ClassId));
			cards.UnionWith(receiving.Where(c => c.Game == game).Select(static c => c.ClassId));

			List<int> before = [];
			List<int> after = [];

			foreach (ulong card in cards) {
				int have = ours.TryGetValue(new Card(game, card), out int n) ? n : 0;
				int now = have - giving.Count(c => (c.Game == game) && (c.ClassId == card)) + receiving.Count(c => (c.Game == game) && (c.ClassId == card));

				if (now < 0) {
					return (false, new Said("asks for cards this account doesn't have"));
				}

				before.Add(have);
				after.Add(now);
			}

			before.Sort();
			after.Sort();

			int ahead = 0;

			for (int i = 0; i < before.Count; i++) {
				ahead += after[i] - before[i];

				if (ahead < 0) {
					return (false, new Said("it would set back the {0} set", GameNames.Of(game)));
				}
			}
		}

		return (true, new Said(""));
	}

	/// <summary>
	/// Read the offer and this account's cards, and judge it. Anything that can't be read, or isn't all ordinary
	/// trading cards, is not fair - an offer this can't account for is never accepted on this rule.
	/// </summary>
	internal static async Task<(bool Fair, Said Why)> CheckAsync(Bot bot, ulong offerId, CancellationToken ct) {
		string? json = await bot.Web.ApiGetAsync("IEconService", "GetTradeOffer", new Dictionary<string, string> {
			["tradeofferid"] = offerId.ToString(CultureInfo.InvariantCulture),
			["get_descriptions"] = "1",
			["language"] = "english"
		}, ct).ConfigureAwait(false);

		if (json == null) {
			return (false, new Said("Steam didn't answer"));
		}

		List<Card> giving;
		List<Card> receiving;

		using (JsonDocument doc = JsonDocument.Parse(json)) {
			if (!doc.RootElement.TryGetProperty("response", out JsonElement response) || !response.TryGetProperty("offer", out JsonElement offer)) {
				return (false, new Said("Steam has no such offer for this account"));
			}

			// What each item is, by class and instance.
			Dictionary<string, (bool Card, uint Game)> kinds = [];

			if (response.TryGetProperty("descriptions", out JsonElement descriptions)) {
				foreach (JsonElement d in descriptions.EnumerateArray()) {
					string type = Text(d, "type");
					bool card = type.EndsWith("Trading Card", StringComparison.Ordinal) && !type.Contains("Foil", StringComparison.Ordinal);
					kinds[$"{Text(d, "classid")}_{Text(d, "instanceid")}"] = (card, Num(Text(d, "market_fee_app")));
				}
			}

			List<Card>? Side(string name) {
				List<Card> side = [];

				if (!offer.TryGetProperty(name, out JsonElement items)) {
					return side;   // a side with nothing on it is left out of the answer entirely
				}

				foreach (JsonElement item in items.EnumerateArray()) {
					bool community = (Num(Text(item, "appid")) == 753) && (Text(item, "contextid") == "6");
					string key = $"{Text(item, "classid")}_{Text(item, "instanceid")}";

					if (!community || (Num(Text(item, "amount")) != 1) || !kinds.TryGetValue(key, out (bool Card, uint Game) kind) || !kind.Card || (kind.Game == 0)) {
						return null;
					}

					side.Add(new Card(kind.Game, ulong.Parse(Text(item, "classid"), CultureInfo.InvariantCulture)));
				}

				return side;
			}

			if ((Side("items_to_give") is not { } give) || (Side("items_to_receive") is not { } receive)) {
				return (false, new Said("not all ordinary trading cards"));
			}

			giving = give;
			receiving = receive;
		}

		// Quick no before reading the whole inventory.
		if ((giving.Count == 0) || (giving.Count != receiving.Count)) {
			return (false, new Said("not one for one"));
		}

		InventoryContents? inventory = await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory is not { Complete: true }) {
			return (false, new Said("couldn't read this account's cards"));
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

		return Judge(giving, receiving, ours);
	}

	private static string Text(JsonElement e, string name) => InventoryContents.Text(e, name);

	private static uint Num(string s) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) ? n : 0;

	private static ulong Num64(string s) => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n) ? n : 0;
}
