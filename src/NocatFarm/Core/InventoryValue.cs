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
	private readonly Dictionary<uint, (string Game, Dictionary<string, Held> Items, bool Blocked)> _holdings = [];

	/// <summary>How many are held, and how promising it looks - rank 0 is a knife, 7 is grey junk.</summary>
	private readonly record struct Held(int Count, int Rank);

	private DateTime _readAt = DateTime.MinValue;

	/// <summary>
	/// False while the last read is missing a game Steam didn't answer for, and there was no earlier read of it to
	/// fall back on. Such a total is short by that whole game - CS2's skins, say - so it is shown but never banked:
	/// banked, it read as a 96% crash one day and a full recovery the next.
	/// </summary>
	private bool _complete = true;

	/// <summary>
	/// Read the inventories again on the next pass, whatever the timer says.
	///
	/// Prices are untouched - they are cached for a day and shared, and re-fetching hundreds of them because
	/// somebody pressed a button is how you get rate-limited. This picks up what has CHANGED: items traded away,
	/// items received, a case opened.
	/// </summary>
	public void ForceRefresh() => _readAt = DateTime.MinValue;

	public async Task RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken ct) {
		if (!bot.IsOnline || !bot.Cfg.ShowInventoryValue) {
			return;
		}

		if (!_snapshotTried) {
			_snapshotTried = true;
			LoadSnapshot(maxAge);
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

		// Not the list at all (an error page, a layout change): the last picture stands rather than being wiped.
		if (ParseContexts(page) is not { } inventories) {
			Log.DebugOnChange($"invvalue:{bot.Name}", "inventory value: the inventory page had no readable list of games - kept the last picture", bot.Name);

			return;
		}

		Log.Recovered($"invvalue:{bot.Name}");

		Dictionary<uint, (string Game, Dictionary<string, Held> Items, bool Blocked)> previous;

		lock (_holdings) {
			previous = new(_holdings);
			_holdings.Clear();   // a fresh read replaces the old picture; contexts merge into THIS one, not the last
		}

		if (inventories.Count == 0) {
			_readAt = DateTime.UtcNow;   // nothing held anywhere: a real answer, not a failure
			_complete = true;
			Ready = true;

			return;
		}

		bool first = true;
		HashSet<uint> failed = [];

		foreach ((uint app, string name, string context) in inventories.Take(MaxInventories)) {
			ct.ThrowIfCancellationRequested();

			// A gap between games, not only between pages: a dozen inventories fired back to back - times three
			// accounts starting together - is exactly what got steamcommunity.com shut for everybody.
			if (!first) {
				await Task.Delay(Rng.Seconds(3, 6), ct).ConfigureAwait(false);
			}

			first = false;

			try {
				if (!await ReadOneAsync(app, name, context, ct).ConfigureAwait(false)) {
					failed.Add(app);
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				failed.Add(app);
				Log.Debug(new Said("couldn't read the {0} inventory: {1}", name, Log.Describe(e)), bot.Name);
			}
		}

		// A game Steam didn't answer for keeps what the last read found, whole - not dropped from the total, and not
		// half-merged with whichever of its contexts did come back. Only one never read before leaves a gap.
		bool complete = true;

		lock (_holdings) {
			foreach (uint app in failed) {
				if (previous.TryGetValue(app, out (string Game, Dictionary<string, Held> Items, bool Blocked) old)) {
					_holdings[app] = old;
				} else {
					complete = false;
				}
			}
		}

		_readAt = DateTime.UtcNow;
		_complete = complete;
		Ready = true;

		if (complete) {
			SaveSnapshot();
		}
	}

	// ── the last read, kept across restarts ─────────────────────────────────
	// Without it every start re-downloaded every inventory, however recently it had been read - the single biggest
	// burst of requests a start makes. Holdings only; prices have their own day-long cache.
	private bool _snapshotTried;

	private sealed record Snapshot(long ReadAt, List<GameSnapshot> Games);

	private sealed record GameSnapshot(uint App, string Game, Dictionary<string, int[]> Items);

	private string SnapshotPath => Path.Combine(Config.ConfigStore.ConfigDir, "state", $"invvalue-{bot.Name}.json");

	private void SaveSnapshot() {
		try {
			List<GameSnapshot> games;

			lock (_holdings) {
				games = [.. _holdings.Select(static h => new GameSnapshot(h.Key, h.Value.Game,
					h.Value.Items.ToDictionary(static i => i.Key, static i => new[] { i.Value.Count, i.Value.Rank })))];
			}

			AtomicFile.Write(SnapshotPath, JsonSerializer.Serialize(new Snapshot(_readAt.Ticks, games)));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// Next start just reads the inventories again.
			Log.Failed("couldn't save the inventory value snapshot", e, bot.Name);
		}
	}

	private void LoadSnapshot(TimeSpan maxAge) {
		try {
			if (!File.Exists(SnapshotPath) || (JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath)) is not { } snap)) {
				return;
			}

			DateTime readAt = new(snap.ReadAt, DateTimeKind.Utc);

			if ((DateTime.UtcNow - readAt > maxAge) || (readAt > DateTime.UtcNow)) {
				return;
			}

			lock (_holdings) {
				_holdings.Clear();

				foreach (GameSnapshot g in snap.Games) {
					// Whether a game is skipped comes from the settings as they are NOW, not as they were then.
					_holdings[g.App] = (g.Game, g.Items.Where(static i => i.Value.Length == 2)
						.ToDictionary(static i => i.Key, static i => new Held(i.Value[0], i.Value[1]), StringComparer.Ordinal),
						bot.Cfg.InventoryIgnoreGames.Contains(g.App));
				}
			}

			_readAt = readAt;
			Ready = true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// A bad file is the same as none.
			Log.Failed("couldn't read the inventory value snapshot", e, bot.Name);
		}
	}

	/// <summary>Read one game's inventory into the holdings. False when Steam gave no answer - an empty inventory is true.</summary>
	private async Task<bool> ReadOneAsync(uint app, string game, string context, CancellationToken ct) {
		InventoryContents? inventory = await Inventory.ReadAsync(bot, app, context, ct).ConfigureAwait(false);

		if (inventory == null) {
			return false;
		}

		// classid identifies the KIND of item; the assets list is the actual copies held.
		//
		// Everything with a market name is priced, whether or not this copy can be sold today. What a thing is
		// WORTH and whether you happen to be able to sell it are different questions - a trade hold, a fresh
		// purchase or a ban makes an item unsellable for a while without making it worthless - and the honest
		// answer to "what is in there" is the market value of what is in there. Items with no market listing at
		// all price at zero on their own, which is the correct answer for them.
		Dictionary<string, (string Hash, int Rank)> named = [];

		foreach (JsonElement d in inventory.Descriptions.Values) {
			string? hash = d.TryGetProperty("market_hash_name", out JsonElement h) ? h.GetString() : null;
			string? classId = d.TryGetProperty("classid", out JsonElement c) ? c.GetString() : null;

			if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(classId)) {
				continue;
			}

			string? colour = d.TryGetProperty("name_color", out JsonElement n) ? n.GetString() : null;
			named[classId] = (hash, RankOf(hash, colour));
		}

		// Which games to skip is TOLD to us, not guessed at.
		//
		// The guess was "an inventory where nothing is marketable must be a banned game". It was wrong in both
		// directions on live accounts: it flagged Steam's own trading cards on every account (hundreds of items,
		// all perfectly sellable) while missing the two accounts that genuinely are CS2-banned, whose skins still
		// come back marked marketable. Steam does not publish which game an account is banned in, and no signal in
		// the inventory stands in for it - so this is a list you fill in, and it does exactly what it says.
		bool blocked = bot.Cfg.InventoryIgnoreGames.Contains(app);

		Dictionary<string, Held> counts = new(StringComparer.Ordinal);

		foreach (JsonElement a in inventory.Assets) {
			string? classId = a.TryGetProperty("classid", out JsonElement c) ? c.GetString() : null;

			if ((classId == null) || !named.TryGetValue(classId, out (string Hash, int Rank) item)) {
				continue;
			}

			int amount = a.TryGetProperty("amount", out JsonElement q) && int.TryParse(q.GetString(), out int n) ? Math.Max(1, n) : 1;
			counts[item.Hash] = new Held(counts.GetValueOrDefault(item.Hash).Count + amount, item.Rank);
		}

		lock (_holdings) {
			if (counts.Count == 0) {
				return true;
			}

			// MERGED, not replaced: one game can have several inventory contexts - Steam's own has three, for
			// cards, backgrounds and emoticons - and writing each one over the last meant only the final context
			// counted. Everything but the last few hundred items simply vanished from the total.
			if (_holdings.TryGetValue(app, out (string Game, Dictionary<string, Held> Items, bool Blocked) existing)) {
				foreach ((string hash, Held held) in counts) {
					existing.Items[hash] = new Held(existing.Items.GetValueOrDefault(hash).Count + held.Count, held.Rank);
				}
			} else {
				_holdings[app] = (game, counts, blocked);
			}
		}

		return true;
	}

	/// <summary>
	/// Pull the appIDs and context IDs out of the inventory page's g_rgAppContextData blob, the fullest first - or null
	/// when the page doesn't carry the blob at all. An error page Steam serves with a 200 used to read as "holds nothing"
	/// and wiped the total to $0 until the next read; and unsorted, the inventories past the cap could be the one holding
	/// almost all of the value.
	/// </summary>
	private static List<(uint App, string Name, string Context)>? ParseContexts(string page) {
		List<(uint App, string Name, string Context, int Assets)> found = [];

		Match blob = ContextData().Match(page);

		if (!blob.Success) {
			// An inventory with nothing in it at all writes the list as an empty [] - a real answer, not a missing one.
			return Regex.IsMatch(page, @"g_rgAppContextData\s*=\s*\[\s*\]") ? [] : null;
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
						found.Add((appId, name, context.Name, assets));
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the inventory list: {0}", Log.Describe(e)));

			return null;
		}

		return [.. found.OrderByDescending(static f => f.Assets).Select(static f => (f.App, f.Name, f.Context))];
	}

	// ── pricing ──────────────────────────────────────────────────────────────
	private async Task PriceSomeAsync(CancellationToken ct) {
		List<(uint App, string Hash, int Rank)> wanted = [];

		lock (_holdings) {
			foreach ((uint app, (string _, Dictionary<string, Held> items, bool blocked)) in _holdings) {
				if (blocked) {
					continue;   // a game you've told it to skip - no point asking what any of it sells for
				}

				foreach ((string hash, Held held) in items) {
					if (PriceBook.NeedsRefresh(app, hash)) {
						wanted.Add((app, hash, held.Rank));
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
		int done = 0;

		// $1,500 of CS2 read as $13. Two rules fix it. Steam's own inventory goes LAST - app 753 is cards,
		// backgrounds and emoticons, thousands of items worth pennies each - and the games themselves are worked
		// through ROUND-ROBIN, one item from each in turn.
		//
		// Round-robin rather than any cleverer ordering because every proxy for "where the money is" turns out to
		// be wrong somewhere: ordering by fewest distinct names put Team Fortress (many hats, few names) ahead of
		// CS2 (few skins, every one unique) and starved exactly the inventory that mattered. Taking turns needs no
		// guess - every game's total starts climbing immediately, and none of them can be starved by another.
		foreach ((uint app, string hash) in RoundRobin(wanted).Take(PricesPerSweep)) {
			ct.ThrowIfCancellationRequested();

			if (await PriceBook.FetchAsync(app, hash, ct).ConfigureAwait(false) == null) {
				break;   // the market has stopped answering - stop pushing and try again next sweep
			}

			Pending--;
			done++;

			// Re-total every so often rather than only at the end of the sweep. Sixty lookups is three and a half
			// minutes of one account's turn, and with several accounts queued behind the same rate limit a total
			// could sit unchanged for ten - which reads as broken, not as busy.
			if (done % 10 == 0) {
				Recount();
			}
		}
	}

	/// <summary>
	/// How promising an item looks before anybody has priced it. Lower is better.
	///
	/// Steam hands the answer over in the inventory itself: the star marks knives and gloves, and name_color is
	/// the rarity every skin game colours its items by - red is Covert, pink Classified, purple Restricted, and
	/// so on down to grey. That is enough to ask about the gloves and the knives FIRST and leave the blue rifles
	/// until later, instead of working through a CS2 inventory in whatever order it happened to arrive.
	/// </summary>
	private static int RankOf(string marketHashName, string? nameColour) {
		if (marketHashName.Contains('★')) {
			return 0;   // knives and gloves
		}

		return nameColour?.ToLowerInvariant() switch {
			"e4ae39" => 1,   // contraband
			"eb4b4b" => 2,   // covert
			"d32ce6" => 3,   // classified
			"8650ac" => 3,   // unusual, over in Team Fortress
			"8847ff" => 4,   // restricted
			"4b69ff" => 5,   // mil-spec
			"5e98d9" => 6,   // industrial
			"b0c3d9" => 7,   // consumer
			_ => 5           // anything unrecognised sits in the middle rather than at either end
		};
	}

	/// <summary>
	/// One item from each game in turn, Steam's own inventory last, and within each game the most promising items
	/// first. Taking turns is what stops one big inventory starving the others; the rank is what stops a game
	/// spending its turns on grey junk while a knife waits.
	/// </summary>
	private static IEnumerable<(uint App, string Hash)> RoundRobin(List<(uint App, string Hash, int Rank)> wanted) {
		List<List<(uint App, string Hash)>> queues = [.. wanted
			.GroupBy(static w => w.App)
			.OrderBy(static g => g.Key == SteamCommunityApp)
			.Select(static g => g
				.OrderBy(static w => w.Rank)
				.ThenBy(w => PriceBook.Known(w.App, w.Hash).HasValue)
				.Select(static w => (w.App, w.Hash))
				.ToList())];

		for (int round = 0; queues.Count > 0; round++) {
			bool any = false;

			foreach (List<(uint App, string Hash)> queue in queues) {
				if (round < queue.Count) {
					any = true;

					yield return queue[round];
				}
			}

			if (!any) {
				yield break;
			}
		}
	}

	private void Recount() {
		List<GameValue> byGame = [];

		lock (_holdings) {
			foreach ((uint app, (string game, Dictionary<string, Held> items, bool blocked)) in _holdings) {
				decimal value = 0;
				int count = 0;

				foreach ((string hash, Held held) in items) {
					value += blocked ? 0 : (PriceBook.Known(app, hash) ?? 0) * held.Count;
					count += held.Count;
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

		// Only banked once the whole thing has a price. A total that is still filling in would otherwise be
		// recorded as a genuine drop and then a genuine rise, and the day's percentage would be fiction.
		if ((Pending == 0) && _complete) {
			InventoryHistory.Note(bot.Name, Total);
		}

		if (Total != was) {
			Log.Debug(new Said("inventory now {0}{1} across {2} game(s), {3} item(s) still to price", PriceBook.Symbol, (Total).ToString("0.00"), _byGame.Count, Pending), bot.Name);
		}
	}

	[GeneratedRegex(@"g_rgAppContextData\s*=\s*(\{.*?\})\s*;\s*\n", RegexOptions.Singleline)]
	private static partial Regex ContextData();
}
