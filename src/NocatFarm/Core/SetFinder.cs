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
/// A card belongs to a set, and one search of a game (its own search, <see cref="GamePage"/>) hands back the whole set with
/// each card's cheapest price and how many are listed: a set costs one request (two for a set of more than 10 cards - a
/// signed-out search answers 10 a page). So it reads whole sets, the most promising games first:
/// <list type="bullet">
/// <item>Which games: the public cheapest-sets list (<see cref="SetSeeds"/>, once a day, robots.txt followed - up to
/// 1,000 games, cheapest first) and the shared list from other copies (<see cref="SetShare"/>). Neither price is ever
/// kept as a set's: they only put the games in order (<see cref="ToFill"/>).</item>
/// <item>How fast: while any of the <see cref="KeepFresh"/> cheapest candidates is due (never read, or read longer ago than
/// <see cref="RefillAfter"/>), a batch of up to <see cref="RequestsABatch"/> requests every 12 to 18 minutes
/// (<see cref="CatchUpGap"/>) - about 64 an hour, 80 at the very most: under a quarter of the app's market budget (360
/// an hour, <see cref="PriceBook.DefaultHourCap"/>) and inside the background's quarter of it (90). That reads about 50
/// sets an hour: the 100 cheapest in about two hours, the 300 cheapest in about six. After that, small batches
/// (<see cref="RequestsABatchIdle"/>, every 15 to 30 minutes) keep them fresh, cheapest first. At most
/// <see cref="FillsADay"/> sets a day (about 780 requests, under a tenth of a day's budget).</item>
/// <item>The walk - the market's cheapest single cards, page by page - is only a slow extra now (<see cref="PagesABatch"/>
/// a batch, <see cref="DailyCards"/> a day): it finds a cheap set no list has yet. Reading 10 cards a request, it was far
/// too slow to be the main way (1,200 cards and 25 sets in six hours on 2026-10-19).</item>
/// <item>A set is known only when every card of it was seen in the last <see cref="CardLife"/>, every one listed: its
/// price is the sum of each card's cheapest listing.</item>
/// </list>
/// Every request goes through the price book's queue (<see cref="PriceBook.MarketGetAsync"/>): its gap (about 10 seconds,
/// give or take a quarter), the hourly cap, the background's share of it, and nothing while the market says slow down -
/// the first request it doesn't answer ends the batch. Nothing here is ever bought on: buying cards prices every card
/// again on the real market, in the wallet's currency, right before it buys.
/// </remarks>
public sealed class SetFinder {
	/// <summary>Cards the walk reads a day, at most - a slow extra (60 requests a day).</summary>
	public const int DailyCards = 600;

	/// <summary>Search pages a batch reads of the walk, at most - with what the sets leave of the batch.</summary>
	public const int PagesABatch = 2;

	/// <summary>Market requests a batch makes at most while the cheapest sets are due - whole sets first, then the walk.</summary>
	public const int RequestsABatch = 40;   // 1.8.3: was 16 - about 160 an hour while catching up, inside the background's half of the cap

	/// <summary>Market requests a batch makes at most once the cheapest sets are fresh.</summary>
	public const int RequestsABatchIdle = 4;

	/// <summary>Whole sets read a day, at most.</summary>
	public const int FillsADay = 2400;   // 1.8.5: was 600 - at the 1.8.3 pace that was used up by 09:30 and nothing more was read all day

	/// <summary>The cheapest candidate sets kept fresh at the quick pace.</summary>
	public const int KeepFresh = 300;

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

	/// <summary>The time between batches once the cheapest sets are fresh.</summary>
	public static TimeSpan BatchGap() => Rng.Minutes(15, 30);

	/// <summary>The time between batches while the cheapest sets are still being read.</summary>
	public static TimeSpan CatchUpGap() => Rng.Minutes(12, 18);

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

	/// <summary>
	/// How far it has got: cards the walk read today, of how many on the market, whole sets known, the last look - and of the
	/// cheapest sets the lists name (<paramref name="Candidates"/>), how many were read on the market in the last 24 hours.
	/// </summary>
	public sealed record Coverage(int Walked, int MarketTotal, int Known, DateTime? LastLook, int Checked = 0, int Candidates = 0);

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

		/// <summary>1.8.3: its cards came from another copy's read (<see cref="Merge"/>), not this one's - never sent back as its own.</summary>
		public bool Outside { get; set; }
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

		/// <summary>Sets from outside (<see cref="Merge"/>): which games to read first.</summary>
		public Dictionary<uint, Hint> Hints { get; set; } = [];

		/// <summary>When the public lists were last read (<see cref="SetSeeds"/>).</summary>
		public long SeedsAt { get; set; }
	}

	/// <summary>
	/// A game to read: an outside set's price (US cents) and when it was seen there - or, from a public list, no price at all,
	/// only its place in the list (<see cref="Rank"/>). A candidate, nothing more.
	/// </summary>
	internal sealed class Hint {
		/// <summary>An outside set's price (US cents) - only what orders it; 0 when none.</summary>
		public int UsdCents { get; set; }

		/// <summary>When the outside price was seen.</summary>
		public long At { get; set; }

		/// <summary>Its place in the public list (1 the cheapest); 0 when it isn't in it.</summary>
		public int Rank { get; set; }

		/// <summary>When the public list said so.</summary>
		public long RankAt { get; set; }
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
	/// a game already offered with a price keeps it. The list before this one is done with. How many were taken.
	/// </summary>
	public int Seed(IReadOnlyList<uint> games, DateTime now) {
		lock (_gate) {
			Table t = Load();
			int rank = 0;

			foreach ((uint app, Hint h) in t.Hints.ToList()) {
				h.Rank = 0;

				if (h.UsdCents == 0) {
					t.Hints.Remove(app);
				}
			}

			foreach (uint app in games.Where(static a => a > 0).Distinct()) {
				rank++;

				if (!t.Hints.TryGetValue(app, out Hint? h)) {
					t.Hints[app] = h = new Hint();
				}

				h.Rank = rank;
				h.RankAt = now.Ticks;
			}

			Save();

			return rank;
		}
	}

	/// <summary>Every set it knows whole now, as plain records, the cheapest first. <paramref name="own"/>: only the ones it read itself.</summary>
	public IReadOnlyList<SetRecord> Records(DateTime now, bool own = false) {
		lock (_gate) {
			return [.. Load().Games.Where(g => Whole(g.Value, now) && !(own && g.Value.Outside))
				.Select(g => new SetRecord(RecordVersion, g.Key, [.. g.Value.Cards.OrderBy(static c => c.Key, StringComparer.Ordinal)
					.Select(static c => new RecordCard(c.Key, c.Value.Usd, c.Value.Listings, new DateTime(c.Value.At, DateTimeKind.Utc)))]))
				.OrderBy(static r => r.UsdCents).ThenBy(static r => r.App)];
		}
	}

	/// <summary>
	/// Sets from outside (another copy's finder, say). Each orders which game to read next; one read in the last
	/// <see cref="RefillAfter"/> is also known here as it is (1.8.3), so copies don't all read the same sets - never a price
	/// anything is bought at (buying cards prices every card on the real market before it buys). A record is left out unless it's this
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

				if (!t.Hints.TryGetValue(r.App, out Hint? h)) {
					t.Hints[r.App] = h = new Hint();
				}

				h.UsdCents = r.UsdCents;
				h.At = r.Cards.Min(static c => c.SeenUtc).Ticks;
				taken++;

				// 1.8.3: read by another copy lately - known here as it is, so this copy doesn't read the same set again (most
				// prices barely move in a day). Safe: buying prices every card on the real market before it buys, so a wrong
				// price costs a market read, never money. Its own read, if newer, is kept.
				DateTime oldest = r.Cards.Min(static c => c.SeenUtc);

				if ((now - oldest < RefillAfter) && (r.Cards.Count <= 15)) {
					t.Games.TryGetValue(r.App, out Game? mine);

					if ((mine == null) || !Whole(mine, now) || (mine.FilledAt < oldest.Ticks)) {
						Game g = mine ?? new Game();
						g.Name = (mine?.Name is { Length: > 0 } known) ? known : GameNames.IsKnown(r.App) ? GameNames.Of(r.App) : "";
						g.Size = r.Cards.Count;
						g.SizeAt = now.Ticks;
						g.FilledAt = oldest.Ticks;
						g.Outside = true;
						g.Cards = r.Cards.ToDictionary(static c => c.Hash, static c => new Card { Usd = c.UsdCents, Listings = c.Listings, At = c.SeenUtc.Ticks }, StringComparer.Ordinal);
						t.Games[r.App] = g;
					}
				}
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
	/// <summary>
	/// The look, for as long as the app runs: every 12 to 18 minutes while the cheapest sets are still being read, every 15
	/// to 30 once they're fresh.
	/// </summary>
	public static void Start(BotManager mgr) => _ = Task.Run(async () => {
		await Task.Delay(Rng.Minutes(3, 8)).ConfigureAwait(false);

		while (true) {
			bool more = false;

			try {
				SetFinder finder = Shared;

				if (finder.Looking(mgr.All, DateTime.UtcNow)) {
					// The public cheapest-sets lists, once a day: which games to read first.
					await SetSeeds.ReadAsync(finder, SetSeeds.WebAsync, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
				}

				if (finder.Looking(mgr.All, DateTime.UtcNow) && (PriceBook.PausedUntil == null) && (PriceBook.SearchesBackAt == null)) {
					more = (await finder.BatchAsync(DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false)).More;
				}
			} catch (Exception e) {
				Log.Debug(new Said("set finder · couldn't look · {0}", Log.Describe(e)));
			}

			await Task.Delay(more ? CatchUpGap() : BatchGap()).ConfigureAwait(false);
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

	/// <summary>Started badges whose sets aren't known - read before anything else (<see cref="Want"/>).</summary>
	private HashSet<uint> _wanted = [];

	/// <summary>
	/// 1.8.3: an account holds cards of these games but the finder doesn't know their sets: they're read first, so buying
	/// can price the few cards each is missing - often the cheapest level there is. Replaces the last call's list.
	/// </summary>
	public void Want(IEnumerable<uint> apps) {
		lock (_gate) {
			_wanted = [.. apps];
		}
	}

	/// <summary>'sets' asked: it looks for a day from now even when no account buys full sets.</summary>
	public void Asked(DateTime now) {
		lock (_gate) {
			Load().AskedAt = now.Ticks;
			Save();
		}
	}

	/// <summary>
	/// What one batch did: walk pages and sets read, market requests made, whether the market stopped answering partway, and
	/// whether the cheapest sets are still being read (the next batch comes sooner).
	/// </summary>
	public readonly record struct Batch(int Pages, int Sets, bool Stopped, int Requests = 0, bool More = false);

	/// <summary>
	/// One batch: the most promising games' whole sets, cheapest first (<see cref="ToFill"/>) - up to
	/// <see cref="RequestsABatch"/> requests while one of the <see cref="KeepFresh"/> cheapest is due, else
	/// <see cref="RequestsABatchIdle"/> - leaving <see cref="PagesABatch"/> of them to the walk, which takes what the sets
	/// don't use (until the day's <see cref="DailyCards"/>). The first request the market doesn't answer ends the batch -
	/// the market's pause is the price book's business; the next batch goes on from there.
	/// </summary>
	public async Task<Batch> BatchAsync(DateTime now, CancellationToken ct) {
		await _one.WaitAsync(ct).ConfigureAwait(false);

		try {
			int pages = 0, sets = 0, requests = 0;
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

			int budget = CatchingUp(now) ? RequestsABatch : RequestsABatchIdle;
			bool walking = Walked() < DailyCards;

			// ── the most promising sets, read whole: one request each (two past 10 cards) ──
			foreach (uint app in ToFill(now)) {
				if ((requests >= budget - (walking ? PagesABatch : 0)) || (FillsToday() >= FillsADay)) {
					break;
				}

				int used = await FillAsync(app, now, ct).ConfigureAwait(false);

				if (used < 0) {
					requests -= used;
					stopped = true;

					break;
				}

				requests += used;
				sets++;
			}

			// ── the walk, with what's left ──
			while (!stopped && (pages < PagesABatch) && (requests < budget) && (Walked() < DailyCards)) {
				string? json = await _get(WalkPage(Walked()), ct).ConfigureAwait(false);
				requests++;

				if ((json == null) || !ReadWalk(json, now)) {
					stopped = true;

					break;
				}

				pages++;
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
				Log.Debug(new Said("set finder · {0} of the {1} cheapest sets checked in the last 24 hours · {2} whole sets known", c.Checked, c.Candidates, c.Known));
			}

			return new Batch(pages, sets, stopped, requests, !stopped && CatchingUp(now));
		} finally {
			_one.Release();
		}
	}

	private int FillsToday() {
		lock (_gate) {
			return Load().FillsToday;
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
	/// A game's whole set read with its own search: its size and every card. The requests it took - minus them when the
	/// market didn't answer. A set the search doesn't hand back whole (something else came with it, or it stopped short)
	/// stays not known.
	/// </summary>
	private async Task<int> FillAsync(uint app, DateTime now, CancellationToken ct) {
		Dictionary<string, Card> cards = new(StringComparer.Ordinal);
		string name = "";
		int total = 0, others = 0, asked = 0;

		for (int start = 0; start < 300;) {   // nobody makes a set of 300 cards
			string? json = await _get(GamePage(app, start), ct).ConfigureAwait(false);
			asked++;

			if (json == null) {
				return -asked;
			}

			int got = 0;

			try {
				using JsonDocument doc = JsonDocument.Parse(json);
				JsonElement root = doc.RootElement;

				if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)) {
					return -asked;
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
				return -asked;
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
			// Read here now: whatever price an outside list said of it is done with - its place in the public list stays.
			if (t.Hints.TryGetValue(app, out Hint? h)) {
				h.UsdCents = 0;

				if (h.Rank == 0) {
					t.Hints.Remove(app);
				}
			}

			if ((cards.Count > 0) && (cards.Count + others == total)) {
				game.Outside = false;
				game.Size = cards.Count;
				game.SizeAt = now.Ticks;
				game.Cards = cards;
				game.Name = name;
			}
		}

		return asked;
	}

	/// <summary>
	/// The one place it picks which games' whole sets to read next, the most promising first (<see cref="Candidates"/>) - only
	/// those due: not read in the last <see cref="RefillAfter"/>, and not known now with every card seen lately. Nothing once
	/// the day's <see cref="FillsADay"/> are read.
	/// </summary>
	public IReadOnlyList<uint> ToFill(DateTime now) {
		lock (_gate) {
			Table t = Load();

			return t.FillsToday >= FillsADay ? [] : [.. Candidates(t, now, _wanted).Where(static c => c.Due).Select(static c => c.App)];
		}
	}

	/// <summary>Whether one of the <see cref="KeepFresh"/> cheapest candidates is due, and the day's sets aren't all read: the quick pace.</summary>
	public bool CatchingUp(DateTime now) {
		lock (_gate) {
			Table t = Load();

			return (t.FillsToday < FillsADay) && Candidates(t, now, _wanted).Take(KeepFresh).Any(static c => c.Due);
		}
	}

	/// <summary>
	/// Every game worth reading, the cheapest it's likely to be first - only to put them in order, never a price anything is
	/// bought at. Read whole before: what it cost then. Not yet: what the walk says it costs at least (the cards it has seen,
	/// every one it hasn't at the walk's dearest card), or what another copy said it costs (<see cref="Merge"/>), whichever is
	/// less; a game only the public list names (no price kept) is put where its place in the list says - about what the
	/// nearest cheaper-placed game it has read costs (nothing read yet: first of all, in the list's order). A game with a card
	/// seen lately that nobody sells is left out.
	/// </summary>
	private static List<(uint App, long Key, int Rank, bool Due)> Candidates(Table t, DateTime now, IReadOnlyCollection<uint>? wanted = null) {
		Dictionary<uint, (long Key, bool Has)> keys = [];
		HashSet<uint> left = [];

		foreach ((uint app, Game g) in t.Games) {
			List<Card> fresh = [.. g.Cards.Values.Where(c => Fresh(c, now))];

			// A card seen lately with nobody selling it: the set can't be bought whole.
			if (fresh.Any(static c => c.Usd <= 0)) {
				left.Add(app);

				continue;
			}

			if (Before(g)) {
				keys[app] = (g.Cards.Values.Sum(static c => (long) c.Usd), true);
			} else if (fresh.Count > 0) {
				int unseen = Math.Max(0, (g.Size > 0 ? g.Size : MinSetSize) - fresh.Count);
				keys[app] = (fresh.Sum(static c => (long) c.Usd) + ((long) unseen * Math.Max(1, t.Frontier)), false);
			}
		}

		foreach ((uint app, Hint h) in t.Hints) {
			if ((h.UsdCents > 0) && !left.Contains(app) && (!keys.TryGetValue(app, out (long Key, bool Has) k) || (!k.Has && (h.UsdCents < k.Key)))) {
				keys[app] = (h.UsdCents, false);
			}
		}

		// The public list's games with nothing else to go by: about what the nearest cheaper-placed game read costs.
		long nearest = 0;

		foreach ((uint app, Hint h) in t.Hints.Where(static h => h.Value.Rank > 0).OrderBy(static h => h.Value.Rank)) {
			if (left.Contains(app)) {
				continue;
			}

			if (keys.TryGetValue(app, out (long Key, bool Has) k)) {
				if (k.Has) {
					nearest = k.Key;
				}

				continue;
			}

			keys[app] = (nearest, false);
		}

		// 1.8.3: the public list's games never read whole go first, in the list's order. A game the walk saw one cheap card
		// of was guessed at its seen cards plus the walk's cheapest price for each unseen one - 16 cents for most, below any
		// real set - so the day's reads went on hundreds of those guesses (each turning out 40 cents or more) and the list's
		// own top sets (Torchlight: Infinite at 0.32, 4th on it) were never read at all.
		// Badges an account has started but whose set isn't known (Want): ahead of everything - their missing cards can be the
		// cheapest level of all, and nothing can be planned on them until the set is read.
		foreach (uint app in wanted ?? []) {
			if (!left.Contains(app)) {
				keys.TryAdd(app, (0, false));
			}
		}

		return [.. keys.Select(k => (App: k.Key, k.Value.Key, Rank: t.Hints.TryGetValue(k.Key, out Hint? h) && (h.Rank > 0) ? h.Rank : int.MaxValue, Due: Due(t, k.Key, now)))
			.OrderBy(c => (wanted?.Contains(c.App) == true) && !(t.Games.TryGetValue(c.App, out Game? g) && Whole(g, now)) ? 0
				: (c.Rank != int.MaxValue) && !(t.Games.TryGetValue(c.App, out Game? g2) && Before(g2)) ? 1 : 2)
			.ThenBy(static c => c.Key).ThenBy(static c => c.Rank).ThenBy(static c => c.App)];
	}

	/// <summary>Read whole before, every card listed then - its price then (maybe stale) orders it.</summary>
	private static bool Before(Game g) => (g.Size > 0) && (g.Cards.Count == g.Size) && g.Cards.Values.All(static c => c.Usd > 0);

	/// <summary>A game to read again: not read in the last <see cref="RefillAfter"/>, and not known now with every card seen lately (the walk keeps that one up to date).</summary>
	private static bool Due(Table t, uint app, DateTime now) {
		if (!t.Games.TryGetValue(app, out Game? g)) {
			return true;
		}

		if ((g.FilledAt > 0) && (now - new DateTime(g.FilledAt, DateTimeKind.Utc) < RefillAfter)) {
			return false;
		}

		return !(Whole(g, now) && g.Cards.Values.All(c => now - new DateTime(c.At, DateTimeKind.Utc) < RefillAfter));
	}

	private static bool Fresh(Card c, DateTime now) => now - new DateTime(c.At, DateTimeKind.Utc) < CardLife;

	/// <summary>Known whole now: its size known, every card of it seen in the last <see cref="CardLife"/>, and every one listed.</summary>
	private static bool Whole(Game g, DateTime now) =>
		(g.Size > 0) && (g.Cards.Count == g.Size) && g.Cards.Values.All(c => (c.Usd > 0) && Fresh(c, now));

	/// <summary>
	/// 1.8.5: a set's game name - the one written down, unless it's only the "app 1085220" stand-in and the real name is known
	/// by now ('sets' showed "app 1085220" for Figment 2: Creed Valley).
	/// </summary>
	public static string Named(uint app, string stored) =>
		(stored.Length > 0) && (stored != "app " + app.ToString(System.Globalization.CultureInfo.InvariantCulture)) ? stored : GameNames.Of(app);

	/// <summary>Every set known whole now, the cheapest first (US cents).</summary>
	public IReadOnlyList<KnownSet> Known(DateTime now) {
		lock (_gate) {
			return [.. Load().Games.Where(g => Whole(g.Value, now))
				.Select(g => new KnownSet(g.Key, Named(g.Key, g.Value.Name), [.. g.Value.Cards.Keys.Order(StringComparer.Ordinal)],
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

	/// <summary>The cheapest candidates the lists name that buying waits for the finder to check (<see cref="WarmUp"/>).</summary>
	public const int WarmUpSets = 30;

	/// <summary>
	/// 1.8.2, warming up: of the <see cref="WarmUpSets"/> cheapest candidates the lists name (all of them when fewer), how
	/// many were read on the market in the last 24 hours. Needed 0: no list names any yet - nothing to wait for.
	/// </summary>
	public (int Checked, int Needed) WarmUp(DateTime now) {
		lock (_gate) {
			Table t = Load();
			HashSet<uint> listed = [.. t.Hints.Where(static h => (h.Value.Rank > 0) || (h.Value.UsdCents > 0)).Select(static h => h.Key)];
			List<uint> cheapest = [.. Candidates(t, now).OrderBy(static c => c.Key).ThenBy(static c => c.Rank).ThenBy(static c => c.App)   // by what each likely costs, not the reading order
				.Select(static c => c.App).Where(listed.Contains).Take(WarmUpSets)];
			int done = cheapest.Count(a => t.Games.TryGetValue(a, out Game? g) && (g.FilledAt > 0) && (now - new DateTime(g.FilledAt, DateTimeKind.Utc) < TimeSpan.FromHours(24)));

			return (done, cheapest.Count);
		}
	}

	/// <summary>How far it has got.</summary>
	public Coverage Covered(DateTime now) {
		int known = Known(now).Count;

		lock (_gate) {
			Table t = Load();
			bool today = t.Day == Day(now);

			// The cheapest sets the lists name, and how many of them were read on the market in the last 24 hours.
			List<uint> listed = [.. t.Hints.Where(static h => (h.Value.Rank > 0) || (h.Value.UsdCents > 0)).Select(static h => h.Key)];
			int checkedLately = listed.Count(a => t.Games.TryGetValue(a, out Game? g) && (g.FilledAt > 0) && (now - new DateTime(g.FilledAt, DateTimeKind.Utc) < TimeSpan.FromHours(24)));

			return new Coverage(today ? t.Walked : 0, t.MarketTotal, known, t.LastLook > 0 ? new DateTime(t.LastLook, DateTimeKind.Utc) : null, checkedLately, listed.Count);
		}
	}

	/// <summary>Cards not seen for a while go; a game with none left goes once its size is old too.</summary>
	private static void Prune(Table t, DateTime now) {
		// An outside price goes once it's old; a place in the public list once the list hasn't said it for two days.
		foreach ((uint app, Hint h) in t.Hints.ToList()) {
			if ((h.UsdCents > 0) && (now - new DateTime(h.At, DateTimeKind.Utc) >= CardLife)) {
				h.UsdCents = 0;
			}

			if ((h.Rank > 0) && (now - new DateTime(h.RankAt, DateTimeKind.Utc) >= TimeSpan.FromDays(2))) {
				h.Rank = 0;
			}

			if ((h.UsdCents == 0) && (h.Rank == 0)) {
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

		// Before 1.8.2's quick sets: a place in the public list was dated by At.
		foreach (Hint h in _table.Hints.Values.Where(static h => (h.Rank > 0) && (h.RankAt == 0))) {
			h.RankAt = h.At;
		}

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
