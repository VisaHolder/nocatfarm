using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>
/// Selling duplicate trading cards on the market - the copies an account can't use, a few at a time, just under
/// the cheapest listing, with Steam's fees worked out so the price you're told is the price you get.
/// </summary>
/// <remarks>
/// What counts as a duplicate: with a game's whole set in hand, it keeps as many copies of each card as badge
/// levels it can still craft; with only part of the set it keeps one of each, so the set can still finish; once the
/// game's badge is maxed nothing is kept. Foils are never sold. Listings need confirming on the phone - with the
/// account's authenticator secrets loaded that happens by itself, otherwise the Steam app shows them.
/// </remarks>
public static class Seller {
	private const uint SteamApp = 753;
	private const int MaxBadgeLevel = 5;

	/// <summary>The market's floor: 3 cents to the buyer, of which the seller gets 1.</summary>
	private const int MinBuyerCents = 3;

	/// <summary>Games to price per run - each is a market request or two.</summary>
	private const int MaxLookups = 8;

	public sealed record Offer(ulong AssetId, string Game, string Card, int LowestCents, int BuyerCents, int YouGetCents);

	public sealed record Plan(List<Offer> Offers, int Duplicates, int Unpriced, string? Problem);

	/// <summary>Steam's cut on top of what the seller receives: 5% for Steam, 10% for the game, each at least a cent.</summary>
	public static int FeesFor(int receive) => Math.Max(1, receive * 5 / 100) + Math.Max(1, receive * 10 / 100);

	/// <summary>The most the seller can receive while the buyer pays no more than <paramref name="buyer"/>. 0 if nothing fits.</summary>
	public static int ReceiveFor(int buyer) {
		for (int receive = buyer - 2; receive >= 1; receive--) {
			if (receive + FeesFor(receive) <= buyer) {
				return receive;
			}
		}

		return 0;
	}

	/// <summary>A cent under the cheapest listing, never below the market's floor.</summary>
	public static int UndercutFor(int lowest) => lowest > MinBuyerCents ? lowest - 1 : MinBuyerCents;

	// ── deciding what to sell ────────────────────────────────────────────────
	/// <summary>
	/// The account's wallet currency, as Steam's currency id - a listing's price is read in it, so prices have to be
	/// looked up in it too. Null until Steam has said (just after signing in).
	/// </summary>
	private static int? WalletCurrency(Bot bot) => bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? null : (int) bot.WalletCurrency;

	/// <summary>Cents in the account's own wallet currency, e.g. "0.05 CAD".</summary>
	public static string Money(int cents, Bot bot) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture)
		+ (bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? "" : " " + bot.WalletCurrency);

	public static async Task<Plan> PlanAsync(Bot bot, int max, CancellationToken ct) {
		// Priced in US dollars and listed on a Canadian wallet, a card went up about a quarter cheaper than meant.
		if (WalletCurrency(bot) is not int currency) {
			return new Plan([], 0, 0, "Steam hasn't said this account's wallet currency yet - try again in a minute");
		}

		if (await LevelPlanner.BadgesAsync(bot, ct).ConfigureAwait(false) is not { } badges) {
			return new Plan([], 0, 0, "Steam wouldn't say what its badges are");
		}

		InventoryContents? inventory = await Inventory.ReadAsync(bot, SteamApp, "6", ct).ConfigureAwait(false);

		if (inventory == null) {
			return new Plan([], 0, 0, "couldn't read its inventory");
		}

		// Half an inventory is worse than none. With a page missing, the cards on it don't count towards a set, so
		// a set it could finish looks hopeless and its cards get listed as spares. Matching refuses the same way.
		if (!inventory.Complete) {
			return new Plan([], 0, 0, "couldn't read all of its inventory - Steam stopped part way; try again in a while");
		}

		// game -> card -> the copies held (asset ids), marketable normal cards only
		Dictionary<uint, Dictionary<string, List<ulong>>> cards = [];

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			if (!type.EndsWith("Trading Card", StringComparison.OrdinalIgnoreCase) || type.Contains("Foil", StringComparison.OrdinalIgnoreCase)
				|| (InventoryContents.Text(d, "marketable") != "1")
				|| !uint.TryParse(InventoryContents.Text(d, "market_fee_app"), out uint game) || (game == 0)
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), out ulong id)) {
				continue;
			}

			string hash = InventoryContents.Text(d, "market_hash_name");

			if (!cards.TryGetValue(game, out Dictionary<string, List<ulong>>? held)) {
				cards[game] = held = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
			}

			if (!held.TryGetValue(hash, out List<ulong>? copies)) {
				held[hash] = copies = [];
			}

			copies.Add(id);
		}

		List<Offer> offers = [];
		int duplicates = 0;
		int unpriced = 0;
		int lookups = 0;

		// The games with the most spare copies first - that's where a price lookup sells the most.
		foreach ((uint game, Dictionary<string, List<ulong>> held) in cards.OrderByDescending(static c => c.Value.Values.Sum(static v => v.Count - 1))) {
			int levelsLeft = MaxBadgeLevel - badges.GameLevels.GetValueOrDefault(game);
			int spare = held.Values.Sum(v => Math.Max(0, v.Count - Math.Max(1, levelsLeft)));

			if ((levelsLeft > 0) && (spare == 0)) {
				continue;   // nothing spare even on the generous reading - no need to ask the market
			}

			if (lookups >= MaxLookups) {
				unpriced += spare;

				continue;
			}

			(LevelPlanner.SetPrice? set, bool asked) = await LevelPlanner.PriceAsync(bot, game, ct, currency).ConfigureAwait(false);
			lookups += asked ? 1 : 0;

			if (set == null) {
				unpriced += spare;

				continue;
			}

			bool wholeSet = (set.Size > 0) && set.Cards.Keys.All(held.ContainsKey);
			int keep = levelsLeft <= 0 ? 0 : wholeSet ? levelsLeft : 1;

			foreach ((string hash, List<ulong> copies) in held) {
				foreach (ulong asset in copies.Skip(keep)) {
					duplicates++;
					int lowest = set.Cards.GetValueOrDefault(hash);

					if (lowest <= 0) {
						unpriced++;   // nobody is selling it, so there's no price to go under

						continue;
					}

					int buyer = UndercutFor(lowest);
					int receive = ReceiveFor(buyer);

					if (receive > 0) {
						offers.Add(new Offer(asset, set.Game, CardName(hash), lowest, buyer, receive));
					}
				}
			}
		}

		// Most valuable first: if only a few go today, they should be the ones worth the most.
		return new Plan([.. offers.OrderByDescending(static o => o.YouGetCents).Take(max)], duplicates, unpriced, null);
	}

	/// <summary>"391540-Sans" is the card "Sans".</summary>
	private static string CardName(string hash) => hash.IndexOf('-') is int dash and > 0 ? hash[(dash + 1)..] : hash;

	public static string Money(int cents) => PriceBook.Symbol + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

	// ── listing ──────────────────────────────────────────────────────────────
	/// <summary>List them, a little apart, then confirm them if this account can. Returns a summary.</summary>
	public static async Task<string> SellAsync(Bot bot, IReadOnlyList<Offer> offers, CancellationToken ct) {
		int listed = 0;
		int earned = 0;
		List<ulong> assets = [];
		string? refusal = null;
		bool first = true;

		foreach (Offer offer in offers) {
			if (!first) {
				await Task.Delay(Rng.Seconds(20, 60), ct).ConfigureAwait(false);
			}

			first = false;

			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, "/market/sellitem/"), new Dictionary<string, string> {
				["appid"] = SteamApp.ToString(CultureInfo.InvariantCulture),
				["contextid"] = "6",
				["assetid"] = offer.AssetId.ToString(CultureInfo.InvariantCulture),
				["amount"] = "1",
				["price"] = offer.YouGetCents.ToString(CultureInfo.InvariantCulture)
			}, new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/inventory/"), ct).ConfigureAwait(false);

			if (body?.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase) == true) {
				listed++;
				earned += offer.YouGetCents;
				assets.Add(offer.AssetId);

				continue;
			}

			// Steam says why in the body ("You have too many listings pending confirmation", a market ban, ...).
			refusal = Message(body) ?? "no answer";

			if (listed == 0) {
				break;   // the first one refused - the rest would be refused for the same reason
			}
		}

		if (listed > 0) {
			Log.Info(new Said("listed {0} duplicate card(s) - {1} if they all sell", listed, Money(earned, bot)), bot.Name);
		}

		StringBuilder sb = new($"{bot.Name}: listed {listed} of {offers.Count}, {Money(earned, bot)} to you if they all sell");

		if (refusal != null) {
			sb.Append($" - Steam refused one: {refusal}");
		}

		if (listed == 0) {
			return sb.ToString();
		}

		if (!bot.CanConfirmTrades) {
			// Said where it can't be missed - the log line alone scrolled past while the phone sat waiting.
			Log.Attention(new Said("{0} market listing(s) to confirm in the Steam app", listed), bot.Name);

			return sb.Append(". They need confirming in the Steam app on your phone (or load this account's authenticator secrets and it'll do that itself).").ToString();
		}

		int confirmed = 0;

		foreach (ulong listing in await PendingListingsAsync(bot, assets, ct).ConfigureAwait(false)) {
			await Task.Delay(Rng.Seconds(2, 5), ct).ConfigureAwait(false);
			confirmed += await bot.ConfirmMobileAsync(listing, true, ct).ConfigureAwait(false) ? 1 : 0;
		}

		return sb.Append($", {confirmed} confirmed on its authenticator.").ToString();
	}

	private static string? Message(string? body) {
		if (string.IsNullOrEmpty(body)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			return doc.RootElement.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
		} catch (JsonException) {
			return null;
		}
	}

	// ── listings already up ─────────────────────────────────────────────────
	public sealed record Listing(ulong Id, ulong AssetId, string Hash, int YouGetCents, DateTime Created, bool AwaitingConfirmation) {
		public string Card => CardName(Hash);

		/// <summary>The card's game, from the "appid-Name" market name.</summary>
		public uint Game => (Hash.IndexOf('-') is int dash and > 0) && uint.TryParse(Hash.AsSpan(0, dash), out uint app) ? app : 0;
	}

	/// <summary>The account's market listings, both live and waiting on a confirmation. Null if Steam wouldn't say.</summary>
	public static async Task<List<Listing>?> ListingsAsync(Bot bot, CancellationToken ct) {
		string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, "/market/mylistings/render/?start=0&count=100&norender=1"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return null;
		}

		using JsonDocument doc = JsonDocument.Parse(json);
		List<Listing> found = [];

		foreach ((string key, bool waiting) in new[] { ("listings", false), ("listings_to_confirm", true) }) {
			if (!doc.RootElement.TryGetProperty(key, out JsonElement list) || (list.ValueKind != JsonValueKind.Array)) {
				continue;
			}

			foreach (JsonElement l in list.EnumerateArray()) {
				ulong.TryParse(InventoryContents.Text(l, "listingid"), out ulong id);
				JsonElement asset = l.TryGetProperty("asset", out JsonElement a) ? a : default;
				ulong assetId = 0;
				string hash = "";

				if (asset.ValueKind == JsonValueKind.Object) {
					ulong.TryParse(InventoryContents.Text(asset, "id"), out assetId);
					hash = InventoryContents.Text(asset, "market_hash_name");
				}

				int.TryParse(InventoryContents.Text(l, "price"), out int price);
				long.TryParse(InventoryContents.Text(l, "time_created"), out long created);

				if (id != 0) {
					found.Add(new Listing(id, assetId, hash, price, DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime, waiting));
				}
			}
		}

		return found;
	}

	/// <summary>The listing ids of OUR new listings still waiting on a confirmation - nothing the owner listed themselves.</summary>
	private static async Task<List<ulong>> PendingListingsAsync(Bot bot, IReadOnlyCollection<ulong> assets, CancellationToken ct) =>
		(await ListingsAsync(bot, ct).ConfigureAwait(false) ?? []).Where(l => l.AwaitingConfirmation && assets.Contains(l.AssetId)).Select(static l => l.Id).ToList();

	/// <summary>
	/// Take down listings that have sat unsold for a week while the market went under them. The cards come back to
	/// the inventory and the next sell lists them again at that day's price.
	/// </summary>
	public static async Task<(int Removed, int Stale)> RelistAsync(Bot bot, CancellationToken ct) {
		if ((WalletCurrency(bot) is not int currency) || (await ListingsAsync(bot, ct).ConfigureAwait(false) is not { } listings)) {
			return (0, 0);
		}

		int removed = 0;
		List<Listing> stale = [];
		int lookups = 0;

		// A week old, and somebody now sells the same card for less than the buyer would pay for ours.
		foreach (Listing l in listings.Where(static l => !l.AwaitingConfirmation && (l.Game != 0) && (DateTime.UtcNow - l.Created > TimeSpan.FromDays(7)))) {
			if (lookups >= MaxLookups) {
				break;
			}

			(LevelPlanner.SetPrice? set, bool asked) = await LevelPlanner.PriceAsync(bot, l.Game, ct, currency).ConfigureAwait(false);
			lookups += asked ? 1 : 0;
			int lowest = set?.Cards.GetValueOrDefault(l.Hash) ?? 0;

			if ((lowest > 0) && (l.YouGetCents + FeesFor(l.YouGetCents) > lowest)) {
				stale.Add(l);
			}
		}

		foreach (Listing listing in stale) {
			await Task.Delay(Rng.Seconds(4, 10), ct).ConfigureAwait(false);

			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, $"/market/removelisting/{listing.Id}"), [],
				new Uri(WebSession.Community, "/market/"), ct).ConfigureAwait(false);

			if (body != null) {
				removed++;
			}
		}

		return (removed, stale.Count);
	}
}
