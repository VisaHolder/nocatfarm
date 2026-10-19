using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// The set finder (1.8.2): the cheapest whole card sets on the market - for any game, played or not, owned or not (a
/// badge can be crafted without the game). One for the whole app, shared by every account.
/// </summary>
/// <remarks>
/// Buying cards used to look only at the 200 cheapest single cards and the games they belong to, and so missed a cheap
/// SET whose cards weren't individually the very cheapest. This walks much deeper, a little at a time:
/// <list type="bullet">
/// <item>The walk: the market's normal trading card search (Steam's own items, normal cards, cheapest first, signed out,
/// US dollars) a few pages a batch, up to <see cref="DailyCards"/> cards a day, starting from the top again each day.
/// Every card it sees is written down with its cheapest listing, how many are listed and when.</item>
/// <item>The sets: a game's whole set is read with the game's own search (its size and every card), the most promising
/// games first - the cheapest the walk says they can be - a couple a batch, at most <see cref="FillsADay"/> a day.</item>
/// <item>A set is known only when every card of it was seen in the last <see cref="CardLife"/>, every one listed: its
/// price is the sum of each card's cheapest listing. An older table only says which games to read first, never a price.</item>
/// </list>
/// Every request goes through the price book's queue (<see cref="PriceBook.MarketGetAsync"/>): the same gap, the hourly
/// cap, the background's share of it, and nothing while the market says slow down. Nothing here is ever bought on:
/// buying cards prices every card again on the real market, in the wallet's currency, right before it buys.
///
/// Public "cheapest sets" lists (<see cref="SetSeeds"/>, once a day, robots.txt followed) and sets from outside
/// (<see cref="Merge"/>) only say which games to read first (<see cref="ToFill"/>): their prices are never kept as a set's.
/// </remarks>
public sealed class SetFinder {
	/// <summary>Cards the walk reads a day, at most.</summary>
	public const int DailyCards = 3000;

	/// <summary>Search pages a batch reads of the walk, at most.</summary>
	public const int PagesABatch = 8;

	/// <summary>Whole sets a batch reads, at most - and a day.</summary>
	public const int FillsABatch = 2, FillsADay = 100;

	/// <summary>The fewest cards a set has.</summary>
	internal const int MinSetSize = 5;

	/// <summary>How long a card's price counts for a known set.</summary>
	public static readonly TimeSpan CardLife = TimeSpan.FromHours(48);

	/// <summary>A set read longer ago than this is read again (when it's still among the cheapest).</summary>
	public static readonly TimeSpan RefillAfter = TimeSpan.FromHours(20);

	/// <summary>How long a game with no card seen lately is kept for its size.</summary>
	public static readonly TimeSpan SizeLife = TimeSpan.FromDays(30);

	/// <summary>How long 'sets' keeps the finder looking when no account buys full sets.</summary>
	public static readonly TimeSpan AskedFor = TimeSpan.FromDays(1);

	/// <summary>The time between batches.</summary>
	public static TimeSpan BatchGap() => Rng.Minutes(15, 30);

	/// <summary>The market's normal trading card search: Steam's own items, trading cards, normal (not foil), in English.</summary>
	private const string Search = "/market/search/render/?norender=1&appid=753&category_753_item_class[]=tag_item_class_2&category_753_cardborder[]=tag_cardborder_0&l=english";

	/// <summary>One page of the walk: cheapest first, US dollars (the only currency the search answers in).</summary>
	internal static string WalkPage(int start) => $"{Search}&sort_column=price&sort_dir=asc&start={start}&count=100&currency=1";

	/// <summary>One page of a game's own search: its whole set.</summary>
	internal static string GamePage(uint app, int start) => $"{Search}&category_753_Game[]=tag_app_{app}&start={start}&count=100&currency=1";

	/// <summary>A known whole set: its cards, each one's cheapest listing in US cents, the fewest listings of any card, and when the oldest was seen.</summary>
	public sealed record KnownSet(uint App, string Game, IReadOnlyList<string> Cards, IReadOnlyDictionary<string, int> Usd, int Depth, DateTime Seen) {
		public int UsdCents => Usd.Values.Sum();

		public CardSet ToCardSet() => new(App, Game, Cards, Usd);
	}

	/// <summary>One line of 'sets': a known set for an account, in the wallet's money at the learned rate (null: no rate yet - US cents).</summary>
	public sealed record Row(uint App, string Game, int Cards, int Level, int UsdCents, long? Wallet) {
		/// <summary>The price shown: the wallet's money when there's a rate, else US cents.</summary>
		public long Shown => Wallet ?? UsdCents;

		/// <summary>XP for every whole unit of money: a level is 100 XP.</summary>
		public long XpPerUnit => Shown > 0 ? 100L * 100 / Shown : 0;
	}

	/// <summary>How far it has got: cards read today, of how many on the market, whole sets known, and the last look.</summary>
	public sealed record Coverage(int Walked, int MarketTotal, int Known, DateTime? LastLook);

	internal sealed class Card {
		public int Usd { get; set; }
		public int Listings { get; set; }
		public long At { get; set; }
	}

	internal sealed class Game {
		public string Name { get; set; } = "";

		/// <summary>Cards in its set, from its own search - 0 while not known.</summary>
		public int Size { get; set; }

		public long SizeAt { get; set; }

		/// <summary>When its own search was last read.</summary>
		public long FilledAt { get; set; }

		public Dictionary<string, Card> Cards { get; set; } = new(StringComparer.Ordinal);
	}

	internal sealed class Table {
		/// <summary>The day the walk is on (UTC, yyyyMMdd): a new day starts it from the top again.</summary>
		public int Day { get; set; }

		/// <summary>Cards read today - where the next page starts.</summary>
		public int Walked { get; set; }

		/// <summary>The dearest card the walk has read today, in US cents: every card it hasn't read costs at least this.</summary>
		public int Frontier { get; set; }

		/// <summary>Normal trading cards on the market, as the search last said.</summary>
		public int MarketTotal { get; set; }

		public int FillsToday { get; set; }
		public long LastLook { get; set; }

		/// <summary>When 'sets' last asked - it keeps looking for a day after.</summary>
		public long AskedAt { get; set; }

		public Dictionary<uint, Game> Games { get; set; } = [];

		/// <summary>Sets from outside (<see cref="Merge"/>): only which games to read first - never a known set or a price.</summary>
		public Dictionary<uint, Hint> Hints { get; set; } = [];

		/// <summary>When the public lists were last read (<see cref="SetSeeds"/>).</summary>
		public long SeedsAt { get; set; }
	}

	/// <summary>
	/// A game to read: an outside set's price (US cents) and when it was seen there - or, from a public list, no price at all,
	/// only its place in the list (<see cref="Rank"/>). A candidate, nothing more.
	/// </summary>
	internal sealed class Hint {
		public int UsdCents { get; set; }
		public long At { get; set; }
		public int Rank { get; set; }
	}

	// ── what it knows, as plain records - and outside ones taken in as candidates only ──
	/// <summary>The version of <see cref="SetRecord"/>: a record of another version is never taken in.</summary>
	public const int RecordVersion = 1;

	/// <summary>One card of a <see cref="SetRecord"/>: its market name, its cheapest listing in US cents, how many are listed, and when that was seen (UTC).</summary>
	public sealed record RecordCard(string Hash, int UsdCents, int Listings, DateTime SeenUtc);

	/// <summary>
	/// A game's whole normal set as the market showed it - market prices only, nothing about any account. What it knows is
	/// handed out as these (<see cref="Records"/>), and outside ones come in through <see cref="Merge"/>.
	/// </summary>
	public sealed record SetRecord(int Version, uint App, IReadOnlyList<RecordCard> Cards) {
		public int UsdCents => Cards.Sum(static c => c.UsdCents);
	}

	/// <summary>Whether the public lists are due to be read again: at most once a day.</summary>
	public bool SeedsDue(DateTime now) {
		lock (_gate) {
			return now - new DateTime(Load().SeedsAt, DateTimeKind.Utc) >= SetSeeds.Every;
		}
	}

	/// <summary>The public lists were tried now.</summary>
	public void SeedsRead(DateTime now) {
		lock (_gate) {
			Load().SeedsAt = now.Ticks;
			Save();
		}
	}

	/// <summary>
	/// Games from a public list, the cheapest set first: only which games to read first, in that order - no price is kept,
	/// a game already offered with a price keeps it. How many were taken.
	/// </summary>
	public int Seed(IReadOnlyList<uint> games, DateTime now) {
		lock (_gate) {
			Table t = Load();
			int rank = 0;

			foreach (uint app in games.Where(static a => a > 0).Distinct()) {
				rank++;

				if (!t.Hints.TryGetValue(app, out Hint? h) || (h.UsdCents == 0)) {
					t.Hints[app] = new Hint { At = now.Ticks, Rank = rank };
				}
			}

			Save();

			return rank;
		}
	}

	/// <summary>Every set it knows whole now, as plain records, the cheapest first.</summary>
	public IReadOnlyList<SetRecord> Records(DateTime now) {
		lock (_gate) {
			return [.. Load().Games.Where(g => Whole(g.Value, now))
				.Select(g => new SetRecord(RecordVersion, g.Key, [.. g.Value.Cards.OrderBy(static c => c.Key, StringComparer.Ordinal)
					.Select(static c => new RecordCard(c.Key, c.Value.Usd, c.Value.Listings, new DateTime(c.Value.At, DateTimeKind.Utc)))]))
				.OrderBy(static r => r.UsdCents).ThenBy(static r => r.App)];
		}
	}

	/// <summary>
	/// Sets from outside (another copy's finder, say), taken in as candidates only: each says which game to read next and
	/// nothing more - never a known set, never a price anything is bought at (buying cards prices every card on the real
	/// market before it buys, and only the finder's own reads make a set known). A record is left out unless it's this
	/// version, every card is this game's ("&lt;app&gt;-"), there are 5 to 300 of them, each priced 1 cent to 1,000.00 and seen
	/// in the last <see cref="CardLife"/> (never in the future). How many were taken in.
	/// </summary>
	public int Merge(IEnumerable<SetRecord> outside, DateTime now) {
		int taken = 0;

		lock (_gate) {
			Table t = Load();

			foreach (SetRecord r in outside) {
				if ((r.Version != RecordVersion) || (r.App == 0) || (r.Cards.Count < MinSetSize) || (r.Cards.Count > 300)
					|| (r.Cards.Select(static c => c.Hash).Distinct(StringComparer.Ordinal).Count() != r.Cards.Count)
					|| !r.Cards.All(c => c.Hash.StartsWith($"{r.App}-", StringComparison.Ordinal) && (c.UsdCents is > 0 and <= 100_000)
						&& (c.SeenUtc <= now.AddMinutes(5)) && (now - c.SeenUtc < CardLife))) {
					continue;
				}

				t.Hints[r.App] = new Hint { UsdCents = r.UsdCents, At = r.Cards.Min(static c => c.SeenUtc).Ticks };
				taken++;
			}

			Save();
		}

		return taken;
	}

	private readonly string _path;
	private readonly Func<string, CancellationToken, Task<string?>> _get;
	private readonly Lock _gate = new();
	private readonly SemaphoreSlim _one = new(1, 1);
	private Table? _table;

	/// <param name="path">Its state file.</param>
	/// <param name="get">One market request: the answer, or null when it wasn't asked or answered. The price book's queue unless the checks give their own.</param>
	public SetFinder(string path, Func<string, CancellationToken, Task<string?>>? get = null) {
		_path = path;
		_get = get ?? MarketAsync;
	}

	private static SetFinder? _shared;
	private static string? _sharedFor;
	private static readonly Lock SharedGate = new();

	/// <summary>The one for the whole app, in config/state/setfinder.json.</summary>
	public static SetFinder Shared {
		get {
			string path = Path.Combine(ConfigStore.ConfigDir, "state", "setfinder.json");

			lock (SharedGate) {
				if ((_shared == null) || (_sharedFor != path)) {
					_shared = new SetFinder(path);
					_sharedFor = path;
				}

				return _shared;
			}
		}
	}

	/// <summary>
	/// A request in the price book's queue, as part of the background's share of the hour (like the inventory value), and
	/// its prices kept in the book - the inventory value needs these same card prices.
	/// </summary>
	private static async Task<string?> MarketAsync(string pathAndQuery, CancellationToken ct) {
		PriceBook.Background.Value = true;
		(string? json, _) = await PriceBook.MarketGetAsync(pathAndQuery, ct).ConfigureAwait(false);

		if (json != null) {
			PriceBook.Learn(CardMarket.SteamItems, json, LevelPlanner.UsDollar);
		}

		return json;
	}

	// ── the background look ─────────────────────────────────────────────────
	/// <summary>The look, every 15 to 30 minutes, for as long as the app runs.</summary>
	public static void Start(BotManager mgr) => _ = Task.Run(async () => {
		await Task.Delay(Rng.Minutes(3, 8)).ConfigureAwait(false);

		while (true) {
			try {
				SetFinder finder = Shared;

				if (finder.Looking(mgr.All, DateTime.UtcNow)) {
					// The public cheapest-sets lists, once a day: which games to read first.
					await SetSeeds.ReadAsync(finder, SetSeeds.WebAsync, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
				}

				if (finder.Looking(mgr.All, DateTime.UtcNow) && (PriceBook.PausedUntil == null) && (PriceBook.SearchesBackAt == null)) {
					await finder.BatchAsync(DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
				}
			} catch (Exception e) {
				Log.Debug(new Said("set finder · couldn't look · {0}", Log.Describe(e)));
			}

			await Task.Delay(BatchGap()).ConfigureAwait(false);
		}
	});

	/// <summary>Whether it looks now: an account buys the cheapest full sets, or 'sets' was asked in the last day.</summary>
	public bool Looking(IEnumerable<Bot> bots, DateTime now) {
		if (bots.Any(static b => b.Cfg.BuyCards && b.Cfg.BuyFullSets)) {
			return true;
		}

		lock (_gate) {
			return now - new DateTime(Load().AskedAt, DateTimeKind.Utc) < AskedFor;
		}
	}

	/// <summary>'sets' asked: it looks for a day from now even when no account buys full sets.</summary>
	public void Asked(DateTime now) {
		lock (_gate) {
			Load().AskedAt = now.Ticks;
			Save();
		}
	}

	/// <summary>What one batch did: walk pages and sets read, and whether the market stopped answering partway.</summary>
	public readonly record struct Batch(int Pages, int Sets, bool Stopped);

	/// <summary>
	/// One batch: up to <see cref="PagesABatch"/> pages further down the walk (until the day's <see cref="DailyCards"/>),
	/// then up to <see cref="FillsABatch"/> of the most promising games' whole sets. The first request the market doesn't
	/// answer ends the batch - the market's pause is the price book's business; the next batch goes on from there.
	/// </summary>
	public async Task<Batch> BatchAsync(DateTime now, CancellationToken ct) {
		await _one.WaitAsync(ct).ConfigureAwait(false);

		try {
			int pages = 0, sets = 0;
			bool stopped = false;

			lock (_gate) {
				Table t = Load();
				int day = Day(now);

				if (t.Day != day) {
					t.Day = day;
					t.Walked = 0;
					t.Frontier = 0;
					t.FillsToday = 0;
				}
			}

			// ── the walk ──
			while ((pages < PagesABatch) && (Walked() < DailyCards)) {
				string? json = await _get(WalkPage(Walked()), ct).ConfigureAwait(false);

				if ((json == null) || !ReadWalk(json, now)) {
					stopped = true;

					break;
				}

				pages++;
			}

			// ── the most promising sets, read whole ──
			if (!stopped) {
				foreach (uint app in ToFill(now).Take(FillsABatch)) {
					if (!await FillAsync(app, now, ct).ConfigureAwait(false)) {
						stopped = true;

						break;
					}

					sets++;
				}
			}

			lock (_gate) {
				Table t = Load();

				if (pages + sets > 0) {
					t.LastLook = now.Ticks;
				}

				Prune(t, now);
				Save();
			}

			if (pages + sets > 0) {
				Coverage c = Covered(now);
				Log.Debug(new Said("set finder · {0} cards looked at today · {1} whole sets known", c.Walked, c.Known));
			}

			return new Batch(pages, sets, stopped);
		} finally {
			_one.Release();
		}
	}

	private int Walked() {
		lock (_gate) {
			return Load().Walked;
		}
	}

	private static int Day(DateTime now) => (now.Year * 10000) + (now.Month * 100) + now.Day;

	/// <summary>A JSON number, or 0.</summary>
	private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetInt32(out int n) ? n : 0;

	/// <summary>A card's game, from its market name ("&lt;app&gt;-Name"). 0 when it has none.</summary>
	private static uint AppOf(string hash) {
		int dash = hash.IndexOf('-');

		return (dash > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) ? app : 0;
	}

	/// <summary>One page of the walk written down. False when it isn't a search answer.</summary>
	private bool ReadWalk(string json, DateTime now) {
		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)) {
				return false;
			}

			lock (_gate) {
				Table t = Load();
				int got = 0;

				if (Int(root, "total_count") is int total and > 0) {
					t.MarketTotal = total;
				}

				foreach (JsonElement r in results.EnumerateArray()) {
					got++;
					string hash = r.TryGetProperty("hash_name", out JsonElement h) && (h.ValueKind == JsonValueKind.String) ? h.GetString() ?? "" : "";

					// Only a normal card of a game, for sale: anything else (or a card nobody sells) says nothing about a set.
					if ((AppOf(hash) is not (uint app and > 0)) || (LevelPlanner.SetCard(r, app) is not { Price: > 0 } card)) {
						continue;
					}

					if (!t.Games.TryGetValue(app, out Game? game)) {
						t.Games[app] = game = new Game();
					}

					game.Name = card.Game;
					game.Cards[card.Hash] = new Card { Usd = card.Price, Listings = Int(r, "sell_listings"), At = now.Ticks };
					t.Frontier = Math.Max(t.Frontier, card.Price);
				}

				// Nothing came back: the end of the market, whatever the total says - nothing more today.
				t.Walked = got == 0 ? DailyCards : t.Walked + got;
			}

			return true;
		} catch (JsonException) {
			return false;
		}
	}

	/// <summary>
	/// A game's whole set read with its own search: its size and every card. False when the market didn't answer. A set the
	/// search doesn't hand back whole (something else came with it, or it stopped short) stays not known.
	/// </summary>
	private async Task<bool> FillAsync(uint app, DateTime now, CancellationToken ct) {
		Dictionary<string, Card> cards = new(StringComparer.Ordinal);
		string name = "";
		int total = 0, others = 0;

		for (int start = 0; start < 300;) {   // nobody makes a set of 300 cards
			string? json = await _get(GamePage(app, start), ct).ConfigureAwait(false);

			if (json == null) {
				return false;
			}

			int got = 0;

			try {
				using JsonDocument doc = JsonDocument.Parse(json);
				JsonElement root = doc.RootElement;

				if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)) {
					return false;
				}

				total = Int(root, "total_count");

				foreach (JsonElement r in results.EnumerateArray()) {
					got++;

					if (LevelPlanner.SetCard(r, app) is not { } card) {
						others++;

						continue;
					}

					cards[card.Hash] = new Card { Usd = card.Price, Listings = card.Price > 0 ? Int(r, "sell_listings") : 0, At = now.Ticks };
					name = card.Game;
				}
			} catch (JsonException) {
				return false;
			}

			start += got;

			if ((got == 0) || (start >= total)) {
				break;
			}
		}

		lock (_gate) {
			Table t = Load();
			t.FillsToday++;

			if (!t.Games.TryGetValue(app, out Game? game)) {
				t.Games[app] = game = new Game();
			}

			game.FilledAt = now.Ticks;
			t.Hints.Remove(app);   // read here now: whatever an outside list said of it is done with

			if ((cards.Count > 0) && (cards.Count + others == total)) {
				game.Size = cards.Count;
				game.SizeAt = now.Ticks;
				game.Cards = cards;
				game.Name = name;
			}
		}

		return true;
	}

	/// <summary>
	/// The one place it picks which games' whole sets to read next, the most promising first: at least what the walk says it
	/// costs - the cards it has seen, and every card it hasn't at the walk's dearest card or more - or, when it was known
	/// before, what it cost then, if that's more; a set from outside (<see cref="Merge"/>) at what it was said to cost
	/// there. Not one known now and read lately, nor one read in the last <see cref="RefillAfter"/>.
	/// </summary>
	public IReadOnlyList<uint> ToFill(DateTime now) {
		lock (_gate) {
			Table t = Load();

			if (t.FillsToday >= FillsADay) {
				return [];
			}

			List<(uint App, long Bound, int Rank)> wanted = [];

			foreach ((uint app, Game g) in t.Games) {
				if ((g.FilledAt > 0) && (now - new DateTime(g.FilledAt, DateTimeKind.Utc) < RefillAfter)) {
					continue;
				}

				List<Card> fresh = [.. g.Cards.Values.Where(c => Fresh(c, now))];

				// Known now, and every card seen lately: the walk keeps it up to date by itself.
				if (Whole(g, now) && g.Cards.Values.All(c => now - new DateTime(c.At, DateTimeKind.Utc) < RefillAfter)) {
					continue;
				}

				// A card seen fresh with nobody selling it: the set can't be bought whole.
				if (fresh.Any(static c => c.Usd <= 0)) {
					continue;
				}

				bool before = (g.Size > 0) && (g.Cards.Count == g.Size) && g.Cards.Values.All(static c => c.Usd > 0);

				if ((fresh.Count == 0) && !before) {
					continue;   // nothing seen lately, and never known: nothing to go on
				}

				int unseen = Math.Max(0, (g.Size > 0 ? g.Size : MinSetSize) - fresh.Count);
				long bound = fresh.Sum(static c => (long) c.Usd) + ((long) unseen * Math.Max(1, t.Frontier));

				// Known before: what it cost then orders it - only orders it, it's never a price anything is bought at.
				if (before) {
					bound = Math.Max(bound, g.Cards.Values.Sum(static c => (long) c.Usd));
				}

				wanted.Add((app, bound, int.MaxValue));
			}

			// Sets from outside: only the order games are read in.
			foreach ((uint app, Hint h) in t.Hints) {
				if (t.Games.TryGetValue(app, out Game? g) && (((g.FilledAt > 0) && (now - new DateTime(g.FilledAt, DateTimeKind.Utc) < RefillAfter))
					|| Whole(g, now) || g.Cards.Values.Any(c => Fresh(c, now) && (c.Usd <= 0)))) {
					continue;
				}

				int at = wanted.FindIndex(w => w.App == app);

				// A price from outside orders it; a public list's game, with no price, is at least a smallest set of cards the
				// walk hasn't reached today - and among those, the list's own order.
				long bound = h.UsdCents > 0 ? h.UsdCents : (long) MinSetSize * Math.Max(1, t.Frontier);

				if (at < 0) {
					wanted.Add((app, bound, h.Rank));
				} else if ((h.UsdCents > 0) && (h.UsdCents < wanted[at].Bound)) {
					wanted[at] = (app, h.UsdCents, wanted[at].Rank);
				}
			}

			return [.. wanted.OrderBy(static w => w.Bound).ThenBy(static w => w.Rank).ThenBy(static w => w.App).Select(static w => w.App)];
		}
	}

	private static bool Fresh(Card c, DateTime now) => now - new DateTime(c.At, DateTimeKind.Utc) < CardLife;

	/// <summary>Known whole now: its size known, every card of it seen in the last <see cref="CardLife"/>, and every one listed.</summary>
	private static bool Whole(Game g, DateTime now) =>
		(g.Size > 0) && (g.Cards.Count == g.Size) && g.Cards.Values.All(c => (c.Usd > 0) && Fresh(c, now));

	/// <summary>Every set known whole now, the cheapest first (US cents).</summary>
	public IReadOnlyList<KnownSet> Known(DateTime now) {
		lock (_gate) {
			return [.. Load().Games.Where(g => Whole(g.Value, now))
				.Select(g => new KnownSet(g.Key, g.Value.Name.Length > 0 ? g.Value.Name : GameNames.Of(g.Key), [.. g.Value.Cards.Keys.Order(StringComparer.Ordinal)],
					g.Value.Cards.ToDictionary(static c => c.Key, static c => c.Value.Usd, StringComparer.Ordinal),
					g.Value.Cards.Values.Min(static c => c.Listings), new DateTime(g.Value.Cards.Values.Min(static c => c.At), DateTimeKind.Utc)))
				.OrderBy(static k => k.UsdCents).ThenBy(static k => k.App)];
		}
	}

	/// <summary>
	/// The cheapest known sets for an account that could be bought whole: badges under level 5 (<paramref name="levels"/>;
	/// null: every game), every card with 2 or more on the market, each in
	/// the wallet's money at the rate learned from the market (<see cref="WalletRate"/>) - US cents when there's none yet.
	/// </summary>
	public IReadOnlyList<Row> Cheapest(IReadOnlyDictionary<uint, int>? levels, int currency, int count, DateTime now) {
		double? rate = currency > 0 ? WalletRate.Learned(currency, now) : null;

		return [.. Known(now).Where(k => (k.Depth >= 2) && ((levels?.GetValueOrDefault(k.App) ?? 0) < BuyPlan.MaxLevel)).Take(count)
			.Select(k => new Row(k.App, k.Game, k.Cards.Count, levels?.GetValueOrDefault(k.App) ?? 0, k.UsdCents, rate is double r ? WalletRate.ToWallet(k.UsdCents, r) : null))];
	}

	/// <summary>How far it has got.</summary>
	public Coverage Covered(DateTime now) {
		int known = Known(now).Count;

		lock (_gate) {
			Table t = Load();
			bool today = t.Day == Day(now);

			return new Coverage(today ? t.Walked : 0, t.MarketTotal, known, t.LastLook > 0 ? new DateTime(t.LastLook, DateTimeKind.Utc) : null);
		}
	}

	/// <summary>Cards not seen for a while go; a game with none left goes once its size is old too.</summary>
	private static void Prune(Table t, DateTime now) {
		foreach ((uint app, Hint h) in t.Hints.ToList()) {
			if (now - new DateTime(h.At, DateTimeKind.Utc) >= CardLife) {
				t.Hints.Remove(app);
			}
		}

		foreach ((uint app, Game g) in t.Games.ToList()) {
			foreach ((string hash, Card c) in g.Cards.ToList()) {
				if (now - new DateTime(c.At, DateTimeKind.Utc) >= CardLife + CardLife) {
					g.Cards.Remove(hash);
				}
			}

			if ((g.Cards.Count == 0) && (now - new DateTime(Math.Max(g.SizeAt, g.FilledAt), DateTimeKind.Utc) >= SizeLife)) {
				t.Games.Remove(app);
			}
		}
	}

	// ── its file ─────────────────────────────────────────────────────────────
	private Table Load() {
		if (_table != null) {
			return _table;
		}

		try {
			_table = File.Exists(_path) ? JsonSerializer.Deserialize<Table>(File.ReadAllText(_path)) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// Start over: it's only what the market said lately, and it reads it again.
			Log.Debug(new Said("set finder · couldn't read its list, starting over · {0}", Log.Describe(e)));
		}

		_table ??= new Table();

		return _table;
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			AtomicFile.Write(_path, JsonSerializer.Serialize(_table ?? new Table()));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("set finder · couldn't save its list · {0}", Log.Describe(e)));
		}
	}
}
