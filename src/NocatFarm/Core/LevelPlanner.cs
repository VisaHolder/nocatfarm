using System.Globalization;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// "What does level N cost?" - worked out from this account's own badges and cards and the market's live prices.
/// </summary>
/// <remarks>
/// Every game badge crafted is 100 XP, up to five per game. So the plan is: how much XP is missing, how many of
/// those badge levels the account can craft from cards it already holds, how cheaply it can finish sets it has
/// started, and which games have the cheapest complete sets to buy for the rest. Prices are the market's lowest
/// listings, asked for gently (a few seconds apart) and kept for a day.
/// </remarks>
public static class LevelPlanner {
	/// <summary>Total XP to reach <paramref name="level"/>: each level costs 100 XP more for every ten already passed.</summary>
	public static int XpFor(int level) {
		int xp = 0;

		for (int n = 1; n <= level; n++) {
			xp += 100 * ((n + 9) / 10);
		}

		return xp;
	}

	/// <summary>Badge levels one game can still give: five normal ones, minus what's crafted.</summary>
	private const int MaxBadgeLevel = 5;

	/// <summary>Market requests one plan may make. The rest of the world keeps its prices until tomorrow.</summary>
	private const int MaxLookups = 24;

	private static readonly TimeSpan PriceLife = TimeSpan.FromHours(24);

	// ── what's been worked out ──────────────────────────────────────────────
	/// <summary>A worked-out answer. Once: shown the next time it's asked for and then dropped, not kept for half an hour -
	/// what the market didn't answer, it isn't a plan.</summary>
	private sealed record Finished(int Target, string Text, DateTime At, bool Once = false);

	private static readonly Dictionary<string, Finished> Plans = new(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> Working = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The command: a finished plan if there's a fresh one, otherwise start working one out in the background.</summary>
	public static string Ask(Bot bot, int target) {
		lock (Plans) {
			if (Plans.TryGetValue(bot.Name, out Finished? done) && (done.Target == target) && (DateTime.UtcNow - done.At < TimeSpan.FromMinutes(30))) {
				if (done.Once) {
					Plans.Remove(bot.Name);
				}

				return done.Text;
			}

			if (Working.Contains(bot.Name)) {
				return $"{bot.Name}: still pricing things on the market - try again in a minute.";
			}

			if (Limiters.RateLimitedFor(WebSession.Community.Host) > TimeSpan.Zero) {
				return "Steam is rate-limiting the community site right now - try again in a few minutes.";
			}

			// The market's pause asks nothing, and every price it didn't ask came back as "no set": a plan of "to buy: 0
			// badge level(s), about $0.00", kept for half an hour. Said instead, with when it can be asked.
			if (MarketClosed() is { } closed) {
				return $"{bot.Name}: {closed}";
			}

			Working.Add(bot.Name);
		}

		_ = Task.Run(async () => {
			string text;
			bool once = false;

			try {
				(text, bool whole) = await BuildAsync(bot, target, CancellationToken.None).ConfigureAwait(false);
				once = !whole;
			} catch (Exception e) {
				// Only shown if somebody asks again - written down here so it isn't lost when nobody does.
				Log.Failed("couldn't work the level plan out", e, bot.Name);
				text = $"{bot.Name}: couldn't work the plan out ({Log.Scrub(e.Message)})";
			}

			lock (Plans) {
				Plans[bot.Name] = new Finished(target, text, DateTime.UtcNow, once);
				Working.Remove(bot.Name);
			}

			Log.Info(new Said("level plan ready - 'levelup {0} {1}' shows it", bot.Name, target), bot.Name);
		});

		return $"{bot.Name}: working it out - reading its badges and cards, then pricing sets on the market a few seconds apart so Steam "
			+ $"doesn't mind (a minute or two). Type 'levelup {bot.Name} {target}' again when the log says it's ready.";
	}

	/// <summary>Why the market can't be asked right now and when it can, or null when it can.</summary>
	private static string? MarketClosed() {
		if (PriceBook.PausedUntil is { } paused) {
			return $"Steam asked the market lookups to wait - try again after {paused.ToLocalTime():HH:mm}.";
		}

		// Searches are all a plan asks for.
		return PriceBook.SearchesBackAt is { } back ? $"The market is turning its searches down - try again after {back.ToLocalTime():HH:mm}." : null;
	}

	/// <summary>What a plan says instead when the market stopped answering partway: no plan from half the prices.</summary>
	private static string Stopped(Bot bot) =>
		$"{bot.Name}: the market stopped answering partway through, so there's no plan yet. {MarketClosed() ?? "Try again in a few minutes."}";

	// ── the plan ─────────────────────────────────────────────────────────────
	/// <summary>The plan, and whether it is one: false when the market stopped answering partway (see <see cref="Finished"/>).</summary>
	private static async Task<(string Text, bool Whole)> BuildAsync(Bot bot, int target, CancellationToken ct) {
		if (await BadgesAsync(bot, ct).ConfigureAwait(false) is not { } badges) {
			return ($"{bot.Name}: Steam wouldn't say what its badges are - try again later.", false);
		}

		if (target <= badges.Level) {
			return ($"{bot.Name} is already level {badges.Level}.", true);
		}

		int xpNeeded = XpFor(target) - badges.Xp;
		int sets = (xpNeeded + 99) / 100;
		int Left(uint app) => MaxBadgeLevel - badges.GameLevels.GetValueOrDefault(app);

		StringBuilder sb = new();
		static string Money(int cents) => PriceBook.Symbol + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

		sb.AppendLine($"{bot.Name}: level {badges.Level} ({badges.Xp:N0} XP) -> {target} needs {xpNeeded:N0} XP = {sets} badge level(s), 100 XP each");

		int lookups = 0;
		int stillNeeded = sets;

		// ── from cards it already holds ─────────────────────────────────────
		Dictionary<uint, Dictionary<string, int>> held = await CardsAsync(bot, ct).ConfigureAwait(false);
		List<(string Game, int Sets)> craftable = [];
		List<(string Game, int Missing, int Cents)> finish = [];

		foreach ((uint app, Dictionary<string, int> cards) in held.Where(h => Left(h.Key) > 0).OrderByDescending(static h => h.Value.Count).ToList()) {
			if (lookups >= MaxLookups / 2) {
				break;   // half the budget for what it owns, half for what it could buy
			}

			(SetPrice? set, bool asked) = await PriceAsync(bot, app, ct).ConfigureAwait(false);
			lookups += asked ? 1 : 0;

			// No answer - paused, refused, Steam's trouble: everything after it would be no answer too, each counted as "no
			// set", and the plan would be built on nothing.
			if (set == null) {
				return (Stopped(bot), false);
			}

			if (set.Size == 0) {
				continue;
			}

			int complete = set.Cards.Keys.All(cards.ContainsKey) ? set.Cards.Keys.Min(c => cards[c]) : 0;
			int use = Math.Min(complete, Left(app));

			if (use > 0) {
				craftable.Add((set.Game, use));
				badges.GameLevels[app] = badges.GameLevels.GetValueOrDefault(app) + use;   // those levels are spoken for
				stillNeeded -= use;
			}

			// One more set from what it has, if the missing cards are all on the market.
			List<string> missing = [.. set.Cards.Keys.Where(c => cards.GetValueOrDefault(c) <= complete)];

			if ((Left(app) > 0) && (missing.Count > 0) && (missing.Count < set.Size) && missing.All(c => set.Cards[c] > 0)) {
				finish.Add((set.Game, missing.Count, missing.Sum(c => set.Cards[c])));
			}
		}

		if (craftable.Count > 0) {
			sb.AppendLine($"  craftable now from its own cards: {craftable.Sum(static c => c.Sets)} - " + string.Join(", ", craftable.Select(static c => c.Sets > 1 ? $"{c.Game} x{c.Sets}" : c.Game)));
		}

		stillNeeded = Math.Max(0, stillNeeded);

		if (stillNeeded == 0) {
			sb.AppendLine("  that's enough - craft those and it's there, for nothing.");

			return (sb.ToString().TrimEnd(), true);
		}

		// ── the cheapest way to the rest ────────────────────────────────────
		// Finishing a started set is often cheaper than any whole set, so those go in the same list.
		List<(string Game, int Levels, int Cents, string How)> options = [.. finish.Select(static f => (f.Game, 1, f.Cents, $"finish it: {f.Missing} card(s)"))];

		if (await CheapestGamesAsync(bot, ct).ConfigureAwait(false) is not { } cheapest) {
			return (Stopped(bot), false);
		}

		foreach (uint app in cheapest) {
			if (lookups >= MaxLookups) {
				break;
			}

			if ((Left(app) <= 0) || held.ContainsKey(app)) {
				continue;
			}

			(SetPrice? set, bool asked) = await PriceAsync(bot, app, ct).ConfigureAwait(false);
			lookups += asked ? 1 : 0;

			if (set == null) {
				return (Stopped(bot), false);
			}

			if ((set.Size > 0) && set.Cards.Values.All(static c => c > 0)) {
				options.Add((set.Game, Left(app), set.Cents, $"{set.Size} cards"));
			}
		}

		int bought = 0;
		int cost = 0;
		List<string> lines = [];

		foreach ((string game, int levels, int cents, string how) in options.OrderBy(static o => o.Cents)) {
			if (bought >= stillNeeded) {
				break;
			}

			int take = Math.Min(levels, stillNeeded - bought);
			bought += take;
			cost += take * cents;
			string name = game.Length > 30 ? game[..29] + "…" : game;
			lines.Add($"    {name,-30}  {take} x {Money(cents)}  ({how})");
		}

		sb.AppendLine($"  to buy: {bought} badge level(s), about {Money(cost)} at the market's lowest listings");
		sb.Append(string.Join(Environment.NewLine, lines.Take(15)));

		if (lines.Count > 15) {
			sb.Append(Environment.NewLine + $"    ... and {lines.Count - 15} more game(s)");
		}

		sb.AppendLine();

		if (bought < stillNeeded) {
			sb.AppendLine($"  {stillNeeded - bought} more still need pricing - ask again tomorrow and it'll look further down the list.");
		}

		sb.Append("  (buying several copies of a card usually costs a little over the lowest listing. Each craft also gives gems, a background or an emoticon, and a coupon.)");

		return (sb.ToString().TrimEnd(), true);
	}

	// ── badges ───────────────────────────────────────────────────────────────
	internal sealed record BadgeState(int Level, int Xp, Dictionary<uint, int> GameLevels);

	internal static async Task<BadgeState?> BadgesAsync(Bot bot, CancellationToken ct) {
		string? json = await bot.Web.ApiGetAsync("IPlayerService", "GetBadges", new Dictionary<string, string> {
			["steamid"] = bot.SteamId.ToString(CultureInfo.InvariantCulture)
		}, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return null;
		}

		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("response", out JsonElement res) || !res.TryGetProperty("player_xp", out JsonElement xp)) {
			Log.Debug("badges: Steam's GetBadges answer had no player_xp", bot.Name);

			return null;
		}

		Dictionary<uint, int> games = [];

		if (res.TryGetProperty("badges", out JsonElement list)) {
			foreach (JsonElement b in list.EnumerateArray()) {
				// Game badges carry an appid; border_color 1 is the foil badge, which is a separate, single level.
				if (b.TryGetProperty("appid", out JsonElement a) && a.TryGetUInt32(out uint app)
					&& (!b.TryGetProperty("border_color", out JsonElement border) || (border.GetInt32() == 0))
					&& b.TryGetProperty("level", out JsonElement lv)) {
					games[app] = lv.GetInt32();
				}
			}
		}

		return new BadgeState(res.GetProperty("player_level").GetInt32(), xp.GetInt32(), games);
	}

	// ── cards held ───────────────────────────────────────────────────────────
	/// <summary>Normal (not foil) trading cards in the account's Steam inventory: game -> market name -> copies.</summary>
	private static async Task<Dictionary<uint, Dictionary<string, int>>> CardsAsync(Bot bot, CancellationToken ct) {
		Dictionary<uint, Dictionary<string, int>> cards = [];
		InventoryContents? inventory = await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory == null) {
			return cards;
		}

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } d) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			if (!type.EndsWith("Trading Card", StringComparison.OrdinalIgnoreCase) || type.Contains("Foil", StringComparison.OrdinalIgnoreCase)
				|| !uint.TryParse(InventoryContents.Text(d, "market_fee_app"), out uint app) || (app == 0)) {
				continue;
			}

			string hash = InventoryContents.Text(d, "market_hash_name");
			int amount = int.TryParse(InventoryContents.Text(asset, "amount"), out int n) ? Math.Max(1, n) : 1;

			if (!cards.TryGetValue(app, out Dictionary<string, int>? game)) {
				cards[app] = game = new Dictionary<string, int>(StringComparer.Ordinal);
			}

			game[hash] = game.GetValueOrDefault(hash) + amount;
		}

		return cards;
	}

	// ── the market ───────────────────────────────────────────────────────────
	/// <summary>A game's normal card set: its size, each card's lowest listing in cents (0 = none listed), and the total.</summary>
	/// <param name="Currency">Steam's currency id the prices are in (1 = US dollar). Older cache entries default to 1.</param>
	internal sealed record SetPrice(uint App, string Game, int Size, Dictionary<string, int> Cards, long At, int Currency = 1) {
		public int Cents => Cards.Values.Sum();
	}

	private static Dictionary<string, SetPrice>? _sets;

	private static string Key(uint app, int currency) => $"{app}:{currency}";
	private static readonly object SetsGate = new();

	private static string SetsPath => Path.Combine(ConfigStore.ConfigDir, "state", "cardsets.json");

	/// <summary>Steam's own inventory, where trading cards live.</summary>
	private const uint SteamItems = 753;

	private const string Search = "/market/search/render/?norender=1&appid=753&category_753_item_class[]=tag_item_class_2&category_753_cardborder[]=tag_cardborder_0&l=english";

	/// <summary>A game's set price from the cache, or from the market if it's older than a day - null when the market
	/// didn't answer. Asked = a request was actually sent: none is while the market's pause stands.</summary>
	/// <param name="currency">Steam's currency id to price in; the global display currency when not given. Selling
	/// passes the account's own wallet currency - Steam reads a listing's price in that.</param>
	internal static async Task<(SetPrice? Set, bool Asked)> PriceAsync(Bot bot, uint app, CancellationToken ct, int? currency = null) {
		int cur = Math.Max(1, currency ?? Live.Global.MarketCurrency);

		lock (SetsGate) {
			_sets ??= LoadSets();

			if (_sets.TryGetValue(Key(app, cur), out SetPrice? known) && (DateTime.UtcNow - new DateTime(known.At, DateTimeKind.Utc) < PriceLife)) {
				return (known, false);
			}
		}

		Dictionary<string, int> cards = new(StringComparer.Ordinal);
		string game = GameNames.Of(app);
		int total = 0;
		bool asked = false;

		// The market hands out fewer than asked per page (ten, signed out), so page by what actually came back.
		for (int start = 0; start < 300; ) {   // nobody makes a set of 300 cards
			// In the price book's queue, not on a pace of its own: the same gap after whatever was asked last, and
			// nothing while the market's pause stands. Signed out, like every price - a read needs no account, and an
			// account's own market standing (a ban, no mobile authenticator) can get its session refused.
			(string? json, bool sent) = await PriceBook.MarketGetAsync($"{Search}&category_753_Game[]=tag_app_{app}&start={start}&count=100&currency={cur}", ct).ConfigureAwait(false);
			asked |= sent;

			if (string.IsNullOrEmpty(json)) {
				return (null, asked);
			}

			PriceBook.Learn(SteamItems, json, cur);   // the inventory value needs these same card prices

			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			total = root.TryGetProperty("total_count", out JsonElement t) ? t.GetInt32() : 0;
			int got = 0;

			if (!root.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)) {
				Log.Debug($"card set prices for {app}: the market search had no results list: {Log.Scrub(json[..Math.Min(150, json.Length)])}", bot.Name);

				break;
			}

			foreach (JsonElement r in results.EnumerateArray()) {
				string hash = r.TryGetProperty("hash_name", out JsonElement h) ? h.GetString() ?? "" : "";
				cards[hash] = r.TryGetProperty("sell_listings", out JsonElement l) && (l.GetInt32() > 0) && r.TryGetProperty("sell_price", out JsonElement p) ? p.GetInt32() : 0;
				got++;

				if (r.TryGetProperty("asset_description", out JsonElement d) && InventoryContents.Text(d, "type") is { Length: > 13 } type
					&& type.EndsWith(" Trading Card", StringComparison.Ordinal)) {
					game = type[..^13];
				}
			}

			start += got;

			if ((got == 0) || (start >= total)) {
				break;
			}
		}

		SetPrice set = new(app, game, cards.Count == total ? total : 0, cards, DateTime.UtcNow.Ticks, cur);

		lock (SetsGate) {
			_sets![Key(app, cur)] = set;
			SaveSets();
		}

		return (set, true);
	}

	/// <summary>Games whose cards are the cheapest on the market right now, cheapest first - null when the market didn't answer.</summary>
	private static async Task<List<uint>?> CheapestGamesAsync(Bot bot, CancellationToken ct) {
		List<uint> games = [];

		for (int start = 0, page = 0; (start < 200) && (page < 4); page++) {
			int cur = Math.Max(1, Live.Global.MarketCurrency);
			(string? json, _) = await PriceBook.MarketGetAsync($"{Search}&sort_column=price&sort_dir=asc&start={start}&count=100&currency={cur}", ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(json)) {
				return null;
			}

			PriceBook.Learn(SteamItems, json, cur);

			using JsonDocument doc = JsonDocument.Parse(json);
			int got = 0;

			if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)) {
				Log.Debug($"cheapest card sets: the market search had no results list: {Log.Scrub(json[..Math.Min(150, json.Length)])}", bot.Name);

				break;
			}

			foreach (JsonElement r in results.EnumerateArray()) {
				got++;
				string hash = r.TryGetProperty("hash_name", out JsonElement h) ? h.GetString() ?? "" : "";
				int dash = hash.IndexOf('-');

				if ((dash > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && !games.Contains(app)) {
					games.Add(app);
				}
			}

			if (got == 0) {
				break;
			}

			start += got;
		}

		return games;
	}

	private static Dictionary<string, SetPrice> LoadSets() {
		try {
			if (File.Exists(SetsPath) && JsonSerializer.Deserialize<List<SetPrice>>(File.ReadAllText(SetsPath)) is { } list) {
				Dictionary<string, SetPrice> sets = [];

				foreach (SetPrice s in list) {
					sets[Key(s.App, s.Currency)] = s;
				}

				return sets;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// Start over; prices are only a day's worth anyway.
			Log.Failed("couldn't read the saved card set prices", e);
		}

		return [];
	}

	private static void SaveSets() {
		try {
			DateTime cutoff = DateTime.UtcNow - PriceLife - PriceLife;
			AtomicFile.Write(SetsPath, JsonSerializer.Serialize(_sets!.Values.Where(s => new DateTime(s.At, DateTimeKind.Utc) > cutoff).ToList()));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// Best effort - it only saves asking the market again.
			Log.Failed("couldn't save the card set prices", e);
		}
	}
}
