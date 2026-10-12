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

	/// <param name="Hash">The card's market name ("391540-Sans") - what a listing is recognised by once Steam has given the
	/// card a new asset id.</param>
	public sealed record Offer(ulong AssetId, string Game, string Card, int LowestCents, int BuyerCents, int YouGetCents, string Hash = "");

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

		// Cards promised in a trade that's still waiting aren't this account's to sell - and don't count towards its sets
		// either, since they're about to leave. Not knowing is a reason to wait, not to guess: listing one breaks the trade.
		if (await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is not { } promised) {
			return new Plan([], 0, 0, "Steam wouldn't say which cards are in a waiting trade - try again in a while");
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
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), out ulong id) || promised.Contains(id)) {
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

			(LevelPlanner.SetPrice? set, bool asked) = await LevelPlanner.PriceAsync(bot, game, ct).ConfigureAwait(false);
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

					// A shortlist only: the search answers in US dollars whatever currency is asked for. The price that's
					// listed is asked for below, in the wallet's own currency.
					offers.Add(new Offer(asset, set.Game, CardName(hash), lowest, 0, 0, hash));
				}
			}
		}

		// Most valuable first: if only a few go today, they should be the ones worth the most. Each priced in the wallet's own
		// currency, by the account's selling price rule - one market request a card, so only as many as are listed.
		List<Offer> priced = [];
		Reprice rule = OwnRule(bot, currency);
		Dictionary<string, Offer?> byCard = new(StringComparer.Ordinal);

		foreach (Offer o in offers.OrderByDescending(static o => o.LowestCents)) {
			if (priced.Count >= max) {
				break;
			}

			if (!byCard.TryGetValue(o.Hash, out Offer? same)) {
				(same, _) = await rule(o, ct).ConfigureAwait(false);
				byCard[o.Hash] = same;
			}

			if (same == null) {
				unpriced++;

				continue;
			}

			priced.Add(o with { LowestCents = same.LowestCents, BuyerCents = same.BuyerCents, YouGetCents = same.YouGetCents });
		}

		return new Plan([.. priced.OrderByDescending(static o => o.YouGetCents)], duplicates, unpriced, null);
	}

	/// <summary>"391540-Sans" is the card "Sans".</summary>
	private static string CardName(string hash) => hash.IndexOf('-') is int dash and > 0 ? hash[(dash + 1)..] : hash;

	// ── listing ──────────────────────────────────────────────────────────────
	/// <summary>
	/// Right before one listing goes up: the offer at today's price, in the account's own wallet currency - or null, with
	/// why it's kept instead.
	/// </summary>
	public delegate Task<(Offer? Offer, Said Why)> Reprice(Offer offer, CancellationToken ct);

	/// <summary>
	/// Price one offer again from the market, in <paramref name="currency"/> (the account's own wallet currency), as the
	/// selling price setting says (<see cref="ListingPrice"/>). Kept - null - when the market doesn't answer, the price
	/// can't be told for certain, it's now worth more than <paramref name="maxWorth"/>, or <paramref name="fits"/> says its
	/// worth no longer fits what was counted for it.
	/// </summary>
	public static async Task<(Offer? Offer, Said Why)> RepriceAsync(Offer offer, int currency, int strategy, int dumpPct, int? maxWorth,
		Func<int, bool>? fits, CancellationToken ct) {
		if ((currency <= 0) || (offer.Hash.Length == 0)) {
			return (null, new Said("no price in this account's own currency - kept"));
		}

		if (await MarketQuotes.FetchAsync(offer.Hash, currency, ct).ConfigureAwait(false) is not { } quote) {
			return (null, new Said("the market didn't say its price just now - kept"));
		}

		(int? list, Said why) = ListingPrice.For(quote.Price, strategy, dumpPct);

		if (list is not int buyer) {
			return (null, why);
		}

		int worth = ListingPrice.Worth(quote.Price);

		if ((maxWorth is int max) && (worth > max)) {
			return (null, new Said("worth {0} now - left for you to decide", Spare.Money(worth, currency)));
		}

		if (fits?.Invoke(worth) == false) {
			return (null, new Said("its price went up to {0} since it was counted - it waits for the next look", Spare.Money(worth, currency)));
		}

		int receive = ReceiveFor(buyer);

		return receive > 0 ? (offer with { LowestCents = quote.Lowest ?? 0, BuyerCents = buyer, YouGetCents = receive }, default)
			: (null, new Said("the market pays nothing for it"));
	}

	/// <summary>The account's own selling price rule, for anything that has no other: 'sell', and stale listings put up again.</summary>
	public static Reprice OwnRule(Bot bot, int currency) {
		int strategy = Math.Clamp(bot.Cfg.SpareSellPrice, 0, 3), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);

		return (o, ct) => RepriceAsync(o, currency, strategy, dump, null, null, ct);
	}

	/// <summary>List them, a little apart, then confirm them if this account can. Returns a summary.</summary>
	/// <param name="mayAct">Asked before each listing: false (a human-mode account's day ended, or its owner started
	/// playing) stops the rest. None: always.</param>
	/// <param name="reprice">Each offer priced again just before it goes up; the account's own rule when not given.</param>
	/// <param name="hold">Asked before each listing too: a reason to stop (spare items put back to preview only), or null.</param>
	public static async Task<string> SellAsync(Bot bot, IReadOnlyList<Offer> offers, CancellationToken ct, Func<bool>? mayAct = null,
		Reprice? reprice = null, Func<Said?>? hold = null, Action<Offer>? onListed = null) {
		// Held by scam watch: nothing goes on the market by itself (ScamWatch.HeldBack).
		if (Modules.ScamWatch.HeldBack(bot) is { } held) {
			return held;
		}

		// Priced in US dollars and listed on a Canadian wallet, a card went up about a quarter cheaper than meant: nothing is
		// listed before Steam has said which money this account's wallet is in.
		if (WalletCurrency(bot) is not int currency) {
			return new Said("{0}: Steam hasn't said this account's wallet currency yet - nothing listed", bot.Name).ToString();
		}

		// Claimed, then checked against the waiting trades once more. The plan read them a while ago (pricing takes
		// minutes), and a send that went out since would lose its whole offer to one of these listings - see
		// Bot.ClaimItems. A send starting from now on leaves the claimed ones out.
		HashSet<ulong> claimed = bot.ClaimItems(offers.Select(static o => o.AssetId));

		try {
			if (await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is not { } promised) {
				return new Said("{0}: Steam wouldn't say which cards are in a waiting trade - try again in a while", bot.Name).ToString();
			}

			return await SellClaimedAsync(bot, [.. offers.Where(o => claimed.Contains(o.AssetId) && !promised.Contains(o.AssetId))], ct, mayAct,
				reprice ?? OwnRule(bot, currency), hold, onListed).ConfigureAwait(false);
		} finally {
			bot.ReleaseItems(claimed);
		}
	}

	/// <summary>What one go at listing did: what went up, what was kept and why, Steam's refusal, and why it stopped.</summary>
	public sealed record Listed(List<Offer> Up, List<(Offer Offer, Said Why)> Kept, string? Refusal, Said? Stopped);

	/// <summary>
	/// The listing loop on its own, for the checks too: before EACH listing, after the pause, whether it may still go
	/// (<paramref name="stop"/>: human mode, scam watch, a hold) - then its price again - then whether it may still go once
	/// more, since a price is a market request's time later. A hold typed while a run lists stops it before the next one.
	/// </summary>
	public static async Task<Listed> ListEachAsync(IReadOnlyList<Offer> offers, Func<Said?> stop, Reprice reprice,
		Func<Offer, CancellationToken, Task<(bool Ok, string? Refusal)>> post, Func<CancellationToken, Task> pause, CancellationToken ct) {
		List<Offer> up = [];
		List<(Offer, Said)> kept = [];
		string? refusal = null;
		Said? stopped = null;
		bool first = true;

		foreach (Offer offer in offers) {
			if (!first) {
				await pause(ct).ConfigureAwait(false);
			}

			first = false;

			if (stop() is { } why) {
				stopped = why;

				break;
			}

			(Offer? priced, Said keep) = await reprice(offer, ct).ConfigureAwait(false);

			if (priced == null) {
				kept.Add((offer, keep));

				continue;
			}

			if (stop() is { } late) {
				stopped = late;

				break;
			}

			(bool ok, string? no) = await post(priced, ct).ConfigureAwait(false);

			if (ok) {
				up.Add(priced);

				continue;
			}

			refusal = no ?? "no answer";

			if (up.Count == 0) {
				break;   // the first one refused - the rest would be refused for the same reason
			}
		}

		return new Listed(up, kept, refusal, stopped);
	}

	private static async Task<string> SellClaimedAsync(Bot bot, IReadOnlyList<Offer> offers, CancellationToken ct, Func<bool>? mayAct, Reprice reprice, Func<Said?>? hold,
		Action<Offer>? onListed) {
		DateTime started = DateTime.UtcNow;

		Said? Stop() {
			if (mayAct?.Invoke() == false) {
				return new Said("human mode (its day ended or you're playing on it)");
			}

			if (Modules.ScamWatch.HeldBack(bot) != null) {
				return Modules.ScamWatch.OnHold(bot.Name);
			}

			return hold?.Invoke();
		}

		async Task<(bool, string?)> Post(Offer offer, CancellationToken token) {
			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, "/market/sellitem/"), new Dictionary<string, string> {
				["appid"] = SteamApp.ToString(CultureInfo.InvariantCulture),
				["contextid"] = "6",
				["assetid"] = offer.AssetId.ToString(CultureInfo.InvariantCulture),
				["amount"] = "1",
				["price"] = offer.YouGetCents.ToString(CultureInfo.InvariantCulture)
			}, new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/inventory/"), token).ConfigureAwait(false);

			if (body?.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase) == true) {
				Stats.Record(Stats.KindListed, bot.Name);
				// Written down as ours, so taking down stale listings later never touches one you listed yourself.
				OurListings.Add(bot.Name, offer.AssetId, offer.Hash, offer.YouGetCents, DateTime.UtcNow);

				// Each one the moment it's up, by name and price - the summary only came once the whole batch was done,
				// by which time the phone had been asking about listings nobody had been told of.
				Log.Good(new Said("listed {0} ({1}) for {2} - you get {3}", offer.Card, offer.Game, Money(offer.BuyerCents, bot), Money(offer.YouGetCents, bot)), bot.Name);
				onListed?.Invoke(offer);

				return (true, null);
			}

			// The automatic seller drops the summary this ends up in, so the reason is written down here as well.
			Log.Debug($"market listing of {offer.Card} ({offer.Game}, asset {offer.AssetId}) refused: {Log.Scrub(Message(body) ?? (body == null ? "no answer" : body[..Math.Min(150, body.Length)]))}", bot.Name);

			// Steam says why in the body ("You have too many listings pending confirmation", a market ban, ...).
			return (false, Message(body));
		}

		Listed done = await ListEachAsync(offers, Stop, reprice, Post, static token => Task.Delay(Rng.Seconds(20, 60), token), ct).ConfigureAwait(false);

		foreach ((Offer offer, Said why) in done.Kept) {
			Log.Info(new Said("kept {0} ({1}) instead of listing it: {2}", offer.Card, offer.Game, why), bot.Name);
		}

		List<Offer> up = done.Up;
		int listed = up.Count;
		int earned = up.Sum(static o => o.YouGetCents);

		if (listed > 0) {
			Log.Info(new Said("listed {0} duplicate card(s) - {1} if they all sell", listed, Money(earned, bot)), bot.Name);
		}

		StringBuilder sb = new($"{bot.Name}: listed {listed} of {offers.Count}, {Money(earned, bot)} to you if they all sell");

		if (done.Kept.Count > 0) {
			sb.Append(CultureInfo.InvariantCulture, $" - {done.Kept.Count} kept at today's price ({done.Kept[0].Why.ToEnglish()})");
		}

		if (done.Refusal != null) {
			sb.Append($" - Steam refused one: {done.Refusal}");
		}

		if (done.Stopped is { } stopped) {
			sb.Append(" - stopped part way: ").Append(stopped.ToEnglish());
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

		foreach (ulong listing in MatchPending(await ListingsAsync(bot, ct).ConfigureAwait(false) ?? [], up, started)) {
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

		if ((doc.RootElement.ValueKind != JsonValueKind.Object)
			|| (!doc.RootElement.TryGetProperty("listings", out _) && !doc.RootElement.TryGetProperty("listings_to_confirm", out _))) {
			Log.Debug($"market listings: Steam's answer had no listings in it: {Log.Scrub(json[..Math.Min(150, json.Length)])}", bot.Name);
		}

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

	/// <summary>
	/// The listing ids of OUR new listings still waiting on a confirmation - nothing the owner listed themselves. Each card
	/// just listed matches at most one listing: by its asset id, or - when Steam has given the listed card a new id - by the
	/// same card at the same price, put up since this run began.
	/// </summary>
	/// <remarks>
	/// Matched by the old asset id alone, a card Steam re-numbered on listing was never found, so none of them were
	/// confirmed and the run said "0 confirmed" with the phone full of listings. The time bound and the one-listing-per-card
	/// rule keep a listing the owner made by hand, days ago or at another price, out of it.
	/// </remarks>
	internal static List<ulong> MatchPending(IEnumerable<Listing> listings, IReadOnlyList<Offer> listed, DateTime since) {
		List<Listing> waiting = [.. listings.Where(static l => l.AwaitingConfirmation && (l.Id != 0))];
		List<Offer> left = [.. listed];
		List<ulong> ours = [];

		void Take(Listing l, Func<Offer, bool> fits) {
			int at = left.FindIndex(o => fits(o));

			if (at >= 0) {
				ours.Add(l.Id);
				left.RemoveAt(at);
			}
		}

		foreach (Listing l in waiting.Where(static l => l.AssetId != 0)) {
			Take(l, o => o.AssetId == l.AssetId);
		}

		foreach (Listing l in waiting.Where(l => !ours.Contains(l.Id) && (l.Hash.Length > 0) && (l.Created >= since.AddMinutes(-2)))) {
			Take(l, o => (o.Hash == l.Hash) && (o.YouGetCents == l.YouGetCents));
		}

		return ours;
	}

	/// <summary>
	/// Whether a listing is worth taking down and putting up again: somebody sells the same item for less than a buyer would
	/// pay for ours, AND the selling price rule would now list it lower - by at least a cent of what you get. Under the
	/// default rule a listing can sit a cent under what it sells for, above a dump; taken down for that, it went straight
	/// back up at the same price every week, each time with a phone confirmation. No price for certain: left up.
	/// </summary>
	public static bool Undercut(Listing listing, MarketQuote? quote, int strategy, int dumpPct) {
		int lowest = quote?.Lowest ?? 0;

		if ((lowest <= 0) || (listing.YouGetCents + FeesFor(listing.YouGetCents) <= lowest)) {
			return false;
		}

		return (ListingPrice.For(quote!.Price, strategy, dumpPct).Cents is int buyer) && (ReceiveFor(buyer) is int receive and > 0) && (receive < listing.YouGetCents);
	}

	/// <summary>
	/// Take down listings that have sat unsold for a week while the market went under them. The cards come back to
	/// the inventory and the next sell lists them again at that day's price.
	/// </summary>
	/// <param name="oursOnly">Only listings nocat.farm put up itself (<see cref="OurListings"/>) - what Spare items does by
	/// itself. Without it ('sell relist', which you type) every one of the account's card listings counts.</param>
	public static async Task<(int Removed, int Stale)> RelistAsync(Bot bot, CancellationToken ct, int days = 7, bool oursOnly = false) {
		if (Modules.ScamWatch.HeldBack(bot) != null) {
			return (0, 0);
		}

		if ((WalletCurrency(bot) is not int currency) || (await ListingsAsync(bot, ct).ConfigureAwait(false) is not { } listings)) {
			return (0, 0);
		}

		if (oursOnly) {
			listings = OurListings.Ours(listings, OurListings.Load(bot.Name));
		}

		int removed = 0;
		List<Listing> stale = [];
		int lookups = 0;
		int strategy = Math.Clamp(bot.Cfg.SpareSellPrice, 0, 3), dump = Math.Clamp(bot.Cfg.SpareDumpPct, 0, 95);

		// A week old (or the days set under Spare items), and somebody now sells the same card for less than the buyer would pay for ours.
		foreach (Listing l in listings.Where(l => !l.AwaitingConfirmation && (l.Game != 0) && (DateTime.UtcNow - l.Created > TimeSpan.FromDays(Math.Max(1, days))))) {
			if (lookups >= MaxLookups) {
				break;
			}

			// In the wallet's own currency - the listing's price is in it. A US-dollar search price read as Canadian dollars
			// took every week-old Canadian listing for undercut, and put it up again a quarter cheaper.
			lookups++;

			if (Undercut(l, await MarketQuotes.FetchAsync(l.Hash, currency, ct).ConfigureAwait(false), strategy, dump)) {
				stale.Add(l);
			}
		}

		foreach (Listing listing in stale) {
			await Task.Delay(Rng.Seconds(4, 10), ct).ConfigureAwait(false);

			string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, $"/market/removelisting/{listing.Id}"), [],
				new Uri(WebSession.Community, "/market/"), ct).ConfigureAwait(false);

			if (body != null) {
				removed++;
			} else {
				Log.Debug($"couldn't take down stale market listing {listing.Id}", bot.Name);   // WebSession logged the why
			}
		}

		return (removed, stale.Count);
	}
}
