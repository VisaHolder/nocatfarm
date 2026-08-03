using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>
/// What this account's Steam inventory is worth, broken down by game.
///
/// The account's own logged-in session is what makes this possible at all: a private profile hides an inventory
/// from everybody else, but never from itself, so the value is readable without making anything public.
///
/// Everything in there is priced, whether or not this particular copy could be sold this minute. Trade holds,
/// fresh purchases and bans all make an item temporarily unsellable without making it worth any less, and the
/// question being answered is what the inventory is worth - not what it could be cashed out for today. Items
/// with no market listing at all price at zero by themselves.
///
/// Prices come from the shared <see cref="PriceBook"/>, which is rate-limited and cached for a day, so the first
/// valuation of a large inventory fills in over a few minutes and every one after that is instant.
/// </summary>
public sealed partial class InventoryValue(Bot bot) {
	/// <summary>How many item names to price per sweep. The rest are picked up on the next one.</summary>
	private const int PricesPerSweep = 60;

	/// <summary>Inventories to look at, largest first. Nobody has thirty games worth of tradables.</summary>
	private const int MaxInventories = 12;

	/// <summary>Steam's own inventory - trading cards, backgrounds, emoticons. Thousands of items, pennies each.</summary>
	private const uint SteamCommunityApp = 753;

	public sealed record GameValue(uint AppId, string Game, int Items, decimal Value, bool Blocked);

	private List<GameValue> _byGame = [];

	/// <summary>Total US dollars, at the market's median price, across every game.</summary>
	public decimal Total { get; private set; }

	/// <summary>Per-game totals, most valuable first.</summary>
	public IReadOnlyList<GameValue> ByGame => _byGame;

	/// <summary>Item names still waiting on a price. While this is above zero the total is still climbing.</summary>
	public int Pending { get; private set; }

	public bool Ready { get; private set; }

	public DateTime RefreshedAt { get; private set; }

	/// <summary>Every item with a market name, keyed by game, gathered from the last inventory read.</summary>
	private readonly Dictionary<uint, (string Game, Dictionary<string, int> Items, bool Blocked)> _holdings = [];

	private DateTime _readAt = DateTime.MinValue;

	public async Task RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken ct) {
		if (!bot.IsOnline || !bot.Cfg.ShowInventoryValue) {
			return;
		}

		// The inventory itself changes slowly; prices change daily and are fetched a few at a time, so the two
		// are on separate clocks - a sweep that only prices things doesn't re-download every inventory.
		if (DateTime.UtcNow - _readAt > maxAge) {
			await ReadInventoriesAsync(ct).ConfigureAwait(false);
			Recount();   // whatever is already priced counts NOW, rather than after a several-minute price sweep
		}

		await PriceSomeAsync(ct).ConfigureAwait(false);
		Recount();
	}

	// ── reading what's in there ──────────────────────────────────────────────
	private async Task ReadInventoriesAsync(CancellationToken ct) {
		if (!bot.Web.Ready && !await bot.Web.RefreshAsync(false, ct).ConfigureAwait(false)) {
			return;
		}

		// The inventory page carries a JS object listing every game this account holds items in, with a count -
		// which is how we avoid guessing at appIDs or asking about games with an empty inventory.
		string? page = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/inventory/"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(page)) {
			return;
		}

		List<(uint App, string Name, string Context)> inventories = ParseContexts(page);

		if (inventories.Count == 0) {
			_readAt = DateTime.UtcNow;   // nothing held anywhere: a real answer, not a failure
			Ready = true;

			return;
		}

		foreach ((uint app, string name, string context) in inventories.Take(MaxInventories)) {
			ct.ThrowIfCancellationRequested();

			try {
				await ReadOneAsync(app, name, context, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Debug($"couldn't read the {name} inventory: {e.Message}", bot.Name);
			}
		}

		_readAt = DateTime.UtcNow;
		Ready = true;
	}

	private async Task ReadOneAsync(uint app, string game, string context, CancellationToken ct) {
		string? json = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/inventory/{bot.SteamId}/{app}/{context}?l=english&count=2000"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return;
		}

		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("descriptions", out JsonElement descriptions)
			|| !doc.RootElement.TryGetProperty("assets", out JsonElement assets)) {
			return;
		}

		// classid identifies the KIND of item; the assets list is the actual copies held.
		//
		// Everything with a market name is priced, whether or not this copy can be sold today. What a thing is
		// WORTH and whether you happen to be able to sell it are different questions - a trade hold, a fresh
		// purchase or a ban makes an item unsellable for a while without making it worthless - and the honest
		// answer to "what is in there" is the market value of what is in there. Items with no market listing at
		// all price at zero on their own, which is the correct answer for them.
		Dictionary<string, string> named = [];
		int sellable = 0;
		int kinds = 0;

		foreach (JsonElement d in descriptions.EnumerateArray()) {
			string? hash = d.TryGetProperty("market_hash_name", out JsonElement h) ? h.GetString() : null;
			string? classId = d.TryGetProperty("classid", out JsonElement c) ? c.GetString() : null;

			if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(classId)) {
				continue;
			}

			named[classId] = hash;
			kinds++;

			if (d.TryGetProperty("marketable", out JsonElement m) && (m.ValueKind == JsonValueKind.Number) && (m.GetInt32() == 1)) {
				sellable++;
			}
		}

		// A whole inventory in which NOTHING can be sold is what a ban looks like from here.
		//
		// Steam does not publish which game an account is banned in, but it does mark every item from that game
		// unsellable - so an inventory with items in it and not one sellable line is the signal, and it needs no
		// ban list to consult. Individual unsellable items mean nothing (trade holds, fresh purchases) and are
		// still priced normally; it is only the all-or-nothing case that counts as blocked.
		bool blocked = (kinds > 0) && (sellable == 0);

		if (blocked) {
			Log.Debug($"{game}: nothing in that inventory can be sold - counting it as blocked", bot.Name);
		}

		Dictionary<string, int> counts = new(StringComparer.Ordinal);

		foreach (JsonElement a in assets.EnumerateArray()) {
			string? classId = a.TryGetProperty("classid", out JsonElement c) ? c.GetString() : null;

			if ((classId == null) || !named.TryGetValue(classId, out string? hash)) {
				continue;
			}

			int amount = a.TryGetProperty("amount", out JsonElement q) && int.TryParse(q.GetString(), out int n) ? Math.Max(1, n) : 1;
			counts[hash] = counts.GetValueOrDefault(hash) + amount;
		}

		lock (_holdings) {
			if (counts.Count == 0) {
				_holdings.Remove(app);
			} else {
				_holdings[app] = (game, counts, blocked);
			}
		}
	}

	/// <summary>Pull the appIDs and context IDs out of the inventory page's g_rgAppContextData blob.</summary>
	private static List<(uint App, string Name, string Context)> ParseContexts(string page) {
		List<(uint, string, string)> found = [];

		Match blob = ContextData().Match(page);

		if (!blob.Success) {
			return found;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(blob.Groups[1].Value);

			foreach (JsonProperty app in doc.RootElement.EnumerateObject()) {
				if (!uint.TryParse(app.Name, out uint appId) || !app.Value.TryGetProperty("rgContexts", out JsonElement contexts)) {
					continue;
				}

				string name = app.Value.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? GameNames.Of(appId) : GameNames.Of(appId);

				foreach (JsonProperty context in contexts.EnumerateObject()) {
					int assets = context.Value.TryGetProperty("asset_count", out JsonElement a) && a.TryGetInt32(out int count) ? count : 0;

					if (assets > 0) {
						found.Add((appId, name, context.Name));
					}
				}
			}
		} catch (Exception e) {
			Log.Debug($"couldn't read the inventory list: {e.Message}");
		}

		return found;
	}

	// ── pricing ──────────────────────────────────────────────────────────────
	private async Task PriceSomeAsync(CancellationToken ct) {
		List<(uint App, string Hash)> wanted = [];

		lock (_holdings) {
			foreach ((uint app, (string _, Dictionary<string, int> items, bool blocked)) in _holdings) {
				if (blocked) {
					continue;   // banned game: nothing here can be sold, so there is nothing worth asking about
				}

				foreach (string hash in items.Keys) {
					if (PriceBook.NeedsRefresh(app, hash)) {
						wanted.Add((app, hash));
					}
				}
			}
		}

		Pending = wanted.Count;

		// Order matters more than it looks.
		//
		// The market answers about one item every few seconds, so a large inventory takes the best part of an hour
		// to price - and what gets asked about FIRST decides what the number looks like for that hour. Left in
		// whatever order they came out of the inventory, nine hundred trading cards worth a penny each were
		// consuming the whole rate limit while fifty skins worth four figures sat unpriced, so an account with
		// $1,500 of CS2 read as $13. Game inventories go first (app 753 is Steam's own cards, backgrounds and
		// emoticons - thousands of items, pennies each), then anything never priced, then the stalest.
		foreach ((uint app, string hash) in wanted
			.OrderBy(static w => w.App == SteamCommunityApp)
			.ThenBy(w => PriceBook.Known(w.App, w.Hash).HasValue)
			.Take(PricesPerSweep)) {
			ct.ThrowIfCancellationRequested();

			if (await PriceBook.FetchAsync(bot.Web, app, hash, ct).ConfigureAwait(false) == null) {
				break;   // the market has stopped answering - stop pushing and try again next sweep
			}

			Pending--;
		}
	}

	private void Recount() {
		List<GameValue> byGame = [];

		lock (_holdings) {
			foreach ((uint app, (string game, Dictionary<string, int> items, bool blocked)) in _holdings) {
				decimal value = 0;
				int count = 0;

				foreach ((string hash, int amount) in items) {
					value += blocked ? 0 : (PriceBook.Known(app, hash) ?? 0) * amount;
					count += amount;
				}

				if (count > 0) {
					byGame.Add(new GameValue(app, game, count, decimal.Round(value, 2), blocked));
				}
			}
		}

		decimal was = Total;

		_byGame = [.. byGame.OrderByDescending(static g => g.Value)];
		Total = decimal.Round(_byGame.Sum(static g => g.Value), 2);
		RefreshedAt = DateTime.UtcNow;

		if (Total != was) {
			Log.Debug($"inventory now ${Total:0.00} across {_byGame.Count} game(s), {Pending} item(s) still to price", bot.Name);
		}
	}

	[GeneratedRegex(@"g_rgAppContextData\s*=\s*(\{.*?\})\s*;\s*\n", RegexOptions.Singleline)]
	private static partial Regex ContextData();
}
