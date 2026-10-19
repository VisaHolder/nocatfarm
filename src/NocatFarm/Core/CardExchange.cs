using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2 card swaps with other nocat.farm users: spare NORMAL trading cards swapped one for one, same game, with accounts
/// on other people's nocat.farm - so sets finish that farming alone leaves half done. Opt-in per account, off by default.
/// </summary>
/// <remarks>
/// <para>How it goes. Every 12 hours or so this copy tells nocat.lol what each opted-in account could swap
/// (<see cref="BuildList"/>): per game where it has a spare copy, every normal card of that game it holds, its price,
/// and the copies it may give - never the last copy of a card, never more than "Keep this many of each card" allows,
/// nothing on the "never touch" lists, nothing on an offer, no foils, backgrounds or emoticons. With it go its steamid
/// and trade link (both deleted from nocat.lol the moment it opts out), the most a swap may lose, and whether it skips
/// partners whose trades would be held. nocat.lol pairs accounts once an hour, the way ArchiSteamFarm's MatchActively
/// does (see <see cref="SwapRules"/>), and hands each copy its matches.</para>
/// <para>One side sends the offer (<see cref="Modules.CardSwaps"/>), with "nocat.farm card swap #&lt;match&gt;" as its
/// note. Before sending it checks everything again against its own inventory and prices; the other side checks it all
/// once more before accepting (<see cref="Verify"/>): that the offer is exactly what was paired (or part of it) and gives
/// nothing else, that its sets lose nothing, and that it doesn't lose more than its limit by its own prices. An offer
/// that isn't what was paired is never accepted, it's reported to nocat.lol, and that account is never swapped with
/// again.</para>
/// </remarks>
public static partial class CardExchange {
	/// <summary>The note on every swap offer, before the match's id: "nocat.farm card swap #a1b2c3d4e5f6".</summary>
	public const string Tag = "nocat.farm card swap #";

	/// <summary>How long a match is acted on - after that it's left, and nocat.lol closes it.</summary>
	public static readonly TimeSpan MatchFor = TimeSpan.FromDays(2);

	/// <summary>A swap offer nobody accepted in this long is taken back.</summary>
	public static readonly TimeSpan OfferFor = TimeSpan.FromDays(2);

	/// <summary>A price older than this is no price: no swap on it.</summary>
	public static readonly TimeSpan PriceFor = TimeSpan.FromHours(24);

	/// <summary>nocat.lol's caps on a list: games an account, cards a game, card rows an account, copies to give a card.</summary>
	public const int MaxGames = 400, MaxCards = 40, MaxRows = 6000, MaxIds = 20, MaxAccounts = 20;

	/// <summary>One card pair of a match, from this account's side: what it gives and what it gets, US cents as nocat.lol had them.</summary>
	public sealed record Pair(uint Game, ulong Give, string GiveName, ulong GiveAsset, int GiveUsd, ulong Get, string GetName, ulong GetAsset, int GetUsd);

	/// <summary>A match nocat.lol made: this account (<paramref name="Me"/>), its partner, whether this side sends the offer
	/// (and then the partner's trade token), and the pairs.</summary>
	public sealed record Match(string Id, DateTime Made, ulong Me, ulong You, bool Send, string Token, List<Pair> Pairs);

	// ── what each account remembers ──────────────────────────────────────────

	/// <summary>One match's fate on this account.</summary>
	public sealed class Record {
		/// <summary>"sent" (an offer is out), "done" (it went through), "dropped" (it didn't happen), "bad" (a bad offer came).</summary>
		public string State { get; set; } = "";
		public ulong Offer { get; set; }
		public ulong Partner { get; set; }

		/// <summary>When it last changed, UTC ticks.</summary>
		public long At { get; set; }

		/// <summary>Looks that couldn't decide (no price yet, Steam didn't answer).</summary>
		public int Tries { get; set; }

		/// <summary>
		/// The cards coming in, by Steam's own market names ("440-Scout"), once this account checked the offer Steam made -
		/// never the names nocat.lol was handed. What buying cards waits for.
		/// </summary>
		public List<string> Gets { get; set; } = [];
	}

	/// <summary>
	/// An account's card swaps: the go-ahead (never in its settings, so a settings file or a backup can't bring it back),
	/// today's count, what happened to each match, and the accounts that sent a bad offer. config/state/cardswaps-&lt;account&gt;.json.
	/// </summary>
	public sealed class Ledger {
		public bool Approved { get; set; }

		/// <summary>The day <see cref="Today"/> is for (local date, yyyy-MM-dd).</summary>
		public string Day { get; set; } = "";

		/// <summary>Swaps sent or accepted that day.</summary>
		public int Today { get; set; }

		/// <summary>Not before then (UTC ticks): a human-mode account spaces its swaps out.</summary>
		public long NextAt { get; set; }

		public Dictionary<string, Record> Records { get; set; } = new(StringComparer.Ordinal);

		/// <summary>Accounts that sent a bad swap offer: never swapped with again.</summary>
		public List<ulong> Blocked { get; set; } = [];

		/// <summary>Bad offers still to tell nocat.lol about: "match|partner".</summary>
		public List<string> Reports { get; set; } = [];

		[System.Text.Json.Serialization.JsonIgnore]
		public string Account { get; private set; } = "";

		private static readonly Lock Gate = new();
		private static readonly Dictionary<string, Ledger> Loaded = new(StringComparer.OrdinalIgnoreCase);

		private static string PathOf(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"cardswaps-{account}.json");

		public static Ledger Load(string account) {
			lock (Gate) {
				string path = PathOf(account);

				if (Loaded.TryGetValue(path, out Ledger? held)) {
					return held;
				}

				Ledger? read = null;

				try {
					if (File.Exists(path)) {
						read = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path));
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
					// Started over: off, until it's turned on again through its preview.
					Log.Failed("couldn't read what card swaps remembers", e, account);
				}

				read ??= new Ledger();
				read.Account = account;
				read.Records = new Dictionary<string, Record>(read.Records ?? [], StringComparer.Ordinal);

				foreach (Record r in read.Records.Values) {
					r.Gets ??= [];
				}

				read.Blocked ??= [];
				read.Reports ??= [];
				Loaded[path] = read;

				return read;
			}
		}

		/// <summary>Only the checks: forget every ledger read.</summary>
		public static void Reset() {
			lock (Gate) {
				Loaded.Clear();
			}
		}

		/// <summary>Change it and save, under its lock.</summary>
		public void Change(Action<Ledger> change) {
			lock (Gate) {
				change(this);

				// Old records go: a week is far past any match's life.
				long old = DateTime.UtcNow.AddDays(-7).Ticks;

				foreach (string id in Records.Where(r => r.Value.At < old).Select(static r => r.Key).ToList()) {
					Records.Remove(id);
				}

				try {
					Directory.CreateDirectory(Path.GetDirectoryName(PathOf(Account))!);
					AtomicFile.Write(PathOf(Account), JsonSerializer.Serialize(this));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed("couldn't save what card swaps remembers", e, Account);
				}
			}
		}

		public Record? Of(string match) {
			lock (Gate) {
				return Records.GetValueOrDefault(match);
			}
		}

		/// <summary>Swaps done today (local day), 0 on a new day.</summary>
		public int DoneToday(DateTime nowUtc) {
			lock (Gate) {
				return Day == DayOf(nowUtc) ? Today : 0;
			}
		}

		/// <summary>The matches in one state ("sent": an offer out), as they are now.</summary>
		public List<(string Id, Record Record)> InState(string state) {
			lock (Gate) {
				return [.. Records.Where(r => r.Value.State == state).Select(static r => (r.Key, r.Value))];
			}
		}

		/// <summary>The bad offers still to tell nocat.lol about.</summary>
		public List<string> PendingReports() {
			lock (Gate) {
				return [.. Reports];
			}
		}

		public bool IsBlocked(ulong partner) {
			lock (Gate) {
				return Blocked.Contains(partner);
			}
		}

		/// <summary>A swap was sent or accepted: today's count goes up and, on a human-mode account, the next waits a while.</summary>
		public void Counted(DateTime nowUtc, TimeSpan gap) => Change(l => {
			string day = DayOf(nowUtc);
			l.Today = l.Day == day ? l.Today + 1 : 1;
			l.Day = day;
			l.NextAt = (nowUtc + gap).Ticks;
		});

		/// <param name="gets">The cards coming in, by Steam's own names, once the offer Steam made was checked.</param>
		public void SetState(string match, string state, ulong partner, DateTime nowUtc, ulong offer = 0, IEnumerable<string>? gets = null) => Change(l => {
			Record r = l.Records.TryGetValue(match, out Record? had) ? had : l.Records[match] = new Record();
			r.State = state;
			r.Partner = partner;
			r.At = nowUtc.Ticks;

			if (offer != 0) {
				r.Offer = offer;
			}

			if (gets != null) {
				r.Gets = [.. gets];
			}
		});

		/// <summary>The cards coming in, by Steam's own names, once the offer Steam made was checked - nothing else of it changes.</summary>
		public void SetGets(string match, IEnumerable<string> gets) => Change(l => {
			if (l.Records.TryGetValue(match, out Record? r)) {
				r.Gets = [.. gets];
			}
		});

		private static string DayOf(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	}

	/// <summary>Whether this account swaps cards: the switch and its go-ahead together.</summary>
	public static bool On(BotConfig cfg, string account) => cfg.CardSwaps && Ledger.Load(account).Approved;

	/// <summary>Swaps a day, at most - the setting, kept to 1-50.</summary>
	public static int PerDay(BotConfig cfg) => Math.Clamp(cfg.CardSwapsPerDay, 1, 50);

	/// <summary>"Most a swap may lose" in cents of the wallet's currency (0 or more).</summary>
	public static int MaxLossCents(BotConfig cfg) => Math.Clamp(SpareRules.Cents(Math.Clamp(cfg.CardSwapMaxLoss, 0f, 1_000_000f)), 0, 100_000_000);

	// ── the matches this copy was handed ─────────────────────────────────────

	private static readonly ConcurrentDictionary<ulong, List<Match>> Known = new();

	/// <summary>The matches nocat.lol handed this copy last, replacing those before (only the checks pass a list by hand).</summary>
	public static void Remember(IEnumerable<Match> matches) {
		Dictionary<ulong, List<Match>> by = matches.GroupBy(static m => m.Me).ToDictionary(static g => g.Key, static g => g.ToList());

		foreach (ulong gone in Known.Keys.Where(k => !by.ContainsKey(k)).ToList()) {
			Known.TryRemove(gone, out _);
		}

		foreach ((ulong me, List<Match> list) in by) {
			Known[me] = list;
		}
	}

	public static IReadOnlyList<Match> MatchesOf(ulong steamId) => Known.TryGetValue(steamId, out List<Match>? m) ? m : [];

	public static Match? Find(ulong steamId, string id) => MatchesOf(steamId).FirstOrDefault(m => m.Id == id);

	/// <summary>
	/// The matches still open on this account, for nocat.lol: not acted on yet and not too old, or with an offer out. One
	/// that went through, was dropped or turned bad is finished - nocat.lol closes it once both sides say so.
	/// </summary>
	public static List<string> OpenIds(string account, ulong steamId, DateTime nowUtc) {
		Ledger ledger = Ledger.Load(account);

		return [.. MatchesOf(steamId).Where(m => ledger.Of(m.Id) is not { } r ? nowUtc - m.Made < MatchFor : r.State is "sent" or "sending").Select(static m => m.Id)];
	}

	/// <summary>
	/// This account's copies promised to an open match - kept off its list, and never sold or turned into gems meanwhile
	/// (spare items leaves them alone).
	/// </summary>
	/// <param name="except">A match whose own copies aren't counted - the one being sent or checked now.</param>
	public static HashSet<ulong> Reserved(string account, ulong steamId, DateTime nowUtc, string? except = null) {
		if (steamId == 0) {
			return [];
		}

		HashSet<string> open = [.. OpenIds(account, steamId, nowUtc)];

		return [.. MatchesOf(steamId).Where(m => open.Contains(m.Id) && (m.Id != except)).SelectMany(static m => m.Pairs).Select(static p => p.GiveAsset)];
	}

	/// <summary>A trade link token as Steam makes them: 6-16 letters, digits, "-" or "_". Anything else is never used.</summary>
	public static bool TokenOk(string? token) => token is { Length: >= 6 and <= 16 } && token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

	/// <summary>Whether a card's market name is its own game's, the way Steam writes them: "440-Scout" for game 440.</summary>
	public static bool NameFits(string hash, uint game) {
		string prefix = game.ToString(CultureInfo.InvariantCulture) + "-";

		return (game != 0) && (hash.Length > prefix.Length) && hash.StartsWith(prefix, StringComparison.Ordinal);
	}

	/// <summary>The match an offer's note names - "nocat.farm card swap #a1b2c3d4e5f6" - or null when it isn't a swap offer.</summary>
	public static string? MatchIdOf(string message) {
		string m = (message ?? "").Trim();

		if (!m.StartsWith(Tag, StringComparison.Ordinal)) {
			return null;
		}

		string id = m[Tag.Length..].Trim();

		return (id.Length == 12) && id.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? id : null;
	}

	// ── the account's own cards ──────────────────────────────────────────────

	/// <summary>One normal trading card copy (or stack) in the inventory.</summary>
	public sealed record OwnCard(ulong AssetId, ulong ClassId, uint Game, string Hash, string Name, bool Tradable, uint Amount);

	/// <summary>The inventory's normal trading cards - no foils, backgrounds, emoticons or anything else.</summary>
	public static List<OwnCard> ReadCards(InventoryContents inventory) {
		List<OwnCard> cards = [];

		foreach (JsonElement asset in inventory.Assets) {
			if ((inventory.DescriptionOf(asset) is not { } d) || (Spare.KindOf(d) != SpareKind.Card)) {
				continue;
			}

			string type = InventoryContents.Text(d, "type");

			// Doubly sure: a foil is never a normal card, whatever its tags say.
			if (type.Contains("Foil", StringComparison.OrdinalIgnoreCase)
				|| !uint.TryParse(InventoryContents.Text(d, "market_fee_app"), NumberStyles.None, CultureInfo.InvariantCulture, out uint game) || (game == 0)
				|| !ulong.TryParse(InventoryContents.Text(asset, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || (id == 0)
				|| !ulong.TryParse(InventoryContents.Text(asset, "classid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong cls) || (cls == 0)) {
				continue;
			}

			uint amount = uint.TryParse(InventoryContents.Text(asset, "amount"), NumberStyles.None, CultureInfo.InvariantCulture, out uint n) ? Math.Max(1, n) : 1;

			cards.Add(new OwnCard(id, cls, game, InventoryContents.Text(d, "market_hash_name"), InventoryContents.Text(d, "name"),
				InventoryContents.Text(d, "tradable") == "1", amount));
		}

		return cards;
	}

	/// <summary>Copies of each card held, all of them, tradable or not - less what's already leaving (on an offer, promised to a match).</summary>
	public static Dictionary<SwapRules.Card, int> Held(IEnumerable<OwnCard> cards, IReadOnlySet<ulong> leaving) {
		Dictionary<SwapRules.Card, int> held = [];

		foreach (OwnCard c in cards.Where(c => !leaving.Contains(c.AssetId))) {
			SwapRules.Card key = new(c.Game, c.ClassId);
			held[key] = held.GetValueOrDefault(key) + (int) c.Amount;
		}

		return held;
	}

	/// <summary>What never goes in a swap: the keep counts and "never touch" lists of Spare items.</summary>
	/// <param name="Keep">Copies of each card always kept - at least 1, so the last copy never goes.</param>
	public sealed record Rules(int Keep, IReadOnlyCollection<uint> KeepGames, IReadOnlyCollection<string> KeepNames) {
		public static Rules From(BotConfig c) => new(Math.Max(1, Math.Clamp(c.SpareKeepCopies, 0, 10)), [.. c.SpareKeepGames], Spare.Names(c.SpareKeepItems));

		/// <summary>Whether this copy may go at all - tradable, one item, not on a keep list, not leaving already.</summary>
		public bool MayGive(OwnCard c, IReadOnlySet<ulong> busy) => c.Tradable && (c.Amount == 1) && !busy.Contains(c.AssetId) && !Keeps(c.Game, c.Name, c.Hash);

		/// <summary>A card that never goes: its game or its name on a keep list (games it bought cards of for a badge are on it too).</summary>
		public bool Keeps(uint game, string name, string hash) => KeepGames.Contains(game) || KeepNames.Contains(name) || KeepNames.Contains(hash);
	}

	/// <summary>The copies of each card this account may give: never more than held less the copies kept.</summary>
	public static Dictionary<SwapRules.Card, List<ulong>> Giveable(IReadOnlyList<OwnCard> cards, IReadOnlySet<ulong> busy, Rules rules) {
		Dictionary<SwapRules.Card, int> held = Held(cards, busy);
		Dictionary<SwapRules.Card, List<ulong>> give = [];

		foreach (IGrouping<SwapRules.Card, OwnCard> copies in cards.Where(c => rules.MayGive(c, busy)).GroupBy(static c => new SwapRules.Card(c.Game, c.ClassId))) {
			int may = Math.Max(0, held.GetValueOrDefault(copies.Key) - rules.Keep);

			if (may > 0) {
				give[copies.Key] = [.. copies.Select(static c => c.AssetId).Order().Take(may)];
			}
		}

		return give;
	}

	/// <summary>One card of a game on the list: copies held, its US price (0: not known), its market name, the copies it may give.</summary>
	public sealed record CardRow(ulong ClassId, int Held, int Usd, string Hash, List<ulong> Give);

	/// <summary>One game on the list: its set's size (0: not known), the badge level, and every normal card of it held.</summary>
	public sealed record GameRow(uint Game, int SetSize, int Level, List<CardRow> Cards);

	/// <summary>
	/// What an account could swap: every game where it may give a copy, with every card of that game it holds - nocat.lol
	/// needs the whole set to tell what helps it. A game with more cards than nocat.lol takes is left off; past its caps,
	/// the games with the most to give go first.
	/// </summary>
	/// <param name="busy">On an offer, or promised to an open match: neither held nor given.</param>
	public static List<GameRow> BuildList(IReadOnlyList<OwnCard> cards, IReadOnlySet<ulong> busy, Rules rules, Func<string, int?> usd,
		Func<uint, int> setSize, Func<uint, int> level) {
		Dictionary<SwapRules.Card, int> held = Held(cards, busy);
		Dictionary<SwapRules.Card, List<ulong>> give = Giveable(cards, busy, rules);
		Dictionary<SwapRules.Card, string> hashes = [];

		foreach (OwnCard c in cards) {
			hashes.TryAdd(new SwapRules.Card(c.Game, c.ClassId), c.Hash.Length > 0 ? c.Hash : $"{c.Game}-{c.Name}");
		}

		List<GameRow> rows = [];

		foreach (IGrouping<uint, KeyValuePair<SwapRules.Card, int>> game in held.Where(static h => h.Value > 0).GroupBy(static h => h.Key.Game)) {
			if (!game.Any(h => give.ContainsKey(h.Key)) || (game.Count() > MaxCards)) {
				continue;
			}

			List<CardRow> list = [.. game.OrderBy(static h => h.Key.ClassId).Select(h => {
				string hash = hashes[h.Key];
				// Never the last copy, whatever happened above: at most one fewer to give than held.
				List<ulong> ids = [.. (give.GetValueOrDefault(h.Key) ?? []).Take(Math.Min(MaxIds, h.Value - 1))];

				return new CardRow(h.Key.ClassId, h.Value, usd(hash) is int u and > 0 ? u : 0, hash.Length > 160 ? hash[..160] : hash, ids);
			})];

			rows.Add(new GameRow(game.Key, Math.Clamp(setSize(game.Key), 0, 100), Math.Clamp(level(game.Key), 0, 10_000), list));
		}

		List<GameRow> kept = [];
		int total = 0;

		foreach (GameRow row in rows.OrderByDescending(static r => r.Cards.Sum(static c => c.Give.Count)).ThenBy(static r => r.Game)) {
			if ((kept.Count >= MaxGames) || (total + row.Cards.Count > MaxRows)) {
				continue;
			}

			kept.Add(row);
			total += row.Cards.Count;
		}

		return [.. kept.OrderBy(static r => r.Game)];
	}

	/// <summary>"Most a swap may lose" in US cents, for nocat.lol to pair by - rounded down, and 0 while the rate isn't known.</summary>
	public static int UsdLoss(int walletCents, int currency, double? walletPerDollar) =>
		walletCents <= 0 ? 0
		: currency == WalletRate.UsDollar ? walletCents
		: walletPerDollar is double rate && (rate > 0) ? (int) Math.Floor(walletCents / rate)
		: 0;

	/// <summary>One account as it goes on the list nocat.lol pairs from.</summary>
	/// <param name="ownHoldDays">Its own trade hold in days; -1 when not known.</param>
	public static JsonObject AccountJson(ulong steamId, string token, int lossUsd, bool skipHeld, int ownHoldDays, bool confirms, IEnumerable<string> open, List<GameRow> rows) {
		JsonArray games = [];

		foreach (GameRow g in rows) {
			JsonArray cards = [];

			foreach (CardRow c in g.Cards) {
				JsonArray ids = [];

				foreach (ulong id in c.Give) {
					ids.Add(id.ToString(CultureInfo.InvariantCulture));
				}

				cards.Add(new JsonArray(c.ClassId.ToString(CultureInfo.InvariantCulture), c.Held, c.Usd, c.Hash, ids));
			}

			games.Add(new JsonArray(g.Game, g.SetSize, g.Level, cards));
		}

		JsonArray o = [];

		foreach (string id in open.Take(50)) {
			o.Add(id);
		}

		return new JsonObject {
			["s"] = steamId.ToString(CultureInfo.InvariantCulture), ["t"] = token, ["l"] = Math.Clamp(lossUsd, 0, 100_000), ["nh"] = skipHeld ? 1 : 0,
			["eh"] = Math.Clamp(ownHoldDays, -1, 30), ["c"] = confirms ? 1 : 0, ["o"] = o, ["g"] = games
		};
	}

	// ── prices ────────────────────────────────────────────────────────────────

	/// <summary>
	/// A card's price in the account's own wallet currency, from no more than a day ago: its own market price in that
	/// currency, else its US price from its game's market search at the rate learned from the market. Null when there's
	/// no fresh one, or nobody sells it - no swap on a price that isn't known.
	/// </summary>
	public static int? WalletCents(string hash, int currency, DateTime nowUtc) {
		if ((currency <= 0) || string.IsNullOrEmpty(hash)) {
			return null;
		}

		if ((MarketQuotes.Of(hash, currency) is { Lowest: > 0 } quote) && (nowUtc - quote.When <= PriceFor)) {
			return quote.Lowest;
		}

		uint game = (hash.IndexOf('-') is int dash and > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) ? app : 0;

		if ((SparePrices.Cached(game) is not { } shortlist) || (nowUtc - shortlist.When > PriceFor) || (SparePrices.UsdCents(hash) is not int usd)) {
			return null;
		}

		return currency == WalletRate.UsDollar ? usd
			: WalletRate.ForDeciding(currency) is double rate ? Math.Max(1, WalletRate.ToWallet(usd, rate))
			: null;
	}

	// ── the sending side's own check ─────────────────────────────────────────

	/// <summary>One pair going in the offer: the copy of ours that goes, and both cards' prices in the wallet's currency.</summary>
	public sealed record Chosen(Pair Pair, ulong Asset, int GivePrice, int GetPrice);

	/// <summary>
	/// What the sending side puts in the offer: the pairs of the match it can still do, checked against its own cards
	/// and prices now. Pairs whose card of ours is gone (or may not go now) are left out, and so are pairs with a card
	/// that has no fresh price. Then, as long as the rest would set a set back (ASF's test) or lose more than the limit,
	/// the pair that costs most comes out. Empty when nothing is left; <paramref name="unpriced"/> lists the cards with no
	/// price (worth pricing before giving up).
	/// </summary>
	public static List<Chosen> Choose(Match match, IReadOnlyList<OwnCard> cards, IReadOnlySet<ulong> busy, Rules rules, Func<string, int?> price, long maxLossCents,
		out Said why, out List<string> unpriced) {
		Dictionary<SwapRules.Card, int> held = Held(cards, busy);
		Dictionary<SwapRules.Card, List<ulong>> free = Giveable(cards, busy, rules);
		Dictionary<SwapRules.Card, string> hashes = [];

		foreach (OwnCard c in cards) {
			hashes.TryAdd(new SwapRules.Card(c.Game, c.ClassId), c.Hash);
		}

		List<Chosen> chosen = [];
		unpriced = [];
		why = default;

		foreach (Pair p in match.Pairs) {
			SwapRules.Card mine = new(p.Game, p.Give);

			if ((free.GetValueOrDefault(mine) is not { Count: > 0 } copies) || (p.Give == p.Get)) {
				why = new Said("its cards have changed since");

				continue;
			}

			ulong asset = copies.Contains(p.GiveAsset) ? p.GiveAsset : copies[0];
			string giveHash = hashes.GetValueOrDefault(mine) ?? p.GiveName;
			int? give = price(giveHash), get = price(p.GetName);

			if ((give is not int g) || (get is not int r)) {
				unpriced.AddRange(new[] { give == null ? giveHash : null, get == null ? p.GetName : null }.OfType<string>());
				why = new Said("no fresh price for some of its cards");

				continue;
			}

			copies.Remove(asset);
			chosen.Add(new Chosen(p, asset, g, r));
		}

		unpriced = [.. unpriced.Distinct()];

		while (chosen.Count > 0) {
			List<SwapRules.Card> giving = [.. chosen.Select(static c => new SwapRules.Card(c.Pair.Game, c.Pair.Give))];
			List<SwapRules.Card> getting = [.. chosen.Select(static c => new SwapRules.Card(c.Pair.Game, c.Pair.Get))];

			if (!SwapRules.NeutralOrBetter(held, giving, getting)) {
				why = new Said("it would set back a set now");
				chosen.RemoveAt(chosen.Count - 1);

				continue;
			}

			if (!SwapRules.PriceOk(chosen.Sum(static c => (long) c.GivePrice), chosen.Sum(static c => (long) c.GetPrice), maxLossCents)) {
				why = new Said("it would lose more than your limit");
				chosen.Remove(chosen.MaxBy(static c => c.GivePrice - c.GetPrice)!);

				continue;
			}

			break;
		}

		return chosen;
	}

	// ── checking an offer that came in ───────────────────────────────────────

	/// <summary>An ordinary (not foil) trading card, one item, that Steam said what it is.</summary>
	private static bool Normal(TradeOffers.Item i) => (i.App == 753) && (i.Context == "6") && (i.Amount == 1) && (i.Game != 0) && i.Described
		&& i.Type.EndsWith("Trading Card", StringComparison.Ordinal) && !i.Type.Contains("Foil", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Whether an offer is the match - all of it or some of its pairs, nothing else - from this account's side: every item a
	/// normal card, what goes out among what it gives in the match, what comes in among what it gets, one for one in every
	/// game. The sending side checks its own offer with it before confirming, the receiving side before accepting.
	/// </summary>
	public static bool AsPaired(TradeOffers.Offer offer, Match match) {
		if ((offer.Giving.Count == 0) || !offer.Giving.All(Normal) || !offer.Receiving.All(Normal)) {
			return false;
		}

		List<SwapRules.Card> giving = [.. offer.Giving.Select(static i => new SwapRules.Card(i.Game, i.ClassId))];
		List<SwapRules.Card> receiving = [.. offer.Receiving.Select(static i => new SwapRules.Card(i.Game, i.ClassId))];

		static bool Within(List<SwapRules.Card> side, IEnumerable<SwapRules.Card> allowed) {
			Dictionary<SwapRules.Card, int> left = allowed.GroupBy(static c => c).ToDictionary(static g => g.Key, static g => g.Count());

			foreach (SwapRules.Card c in side) {
				if (left.GetValueOrDefault(c) <= 0) {
					return false;
				}

				left[c]--;
			}

			return true;
		}

		return Within(giving, match.Pairs.Select(static p => new SwapRules.Card(p.Game, p.Give)))
			&& Within(receiving, match.Pairs.Select(static p => new SwapRules.Card(p.Game, p.Get)))
			&& SwapRules.OneForOne(giving, receiving);
	}

	public enum Verdict {
		/// <summary>It passes everything: accept it.</summary>
		Accept,

		/// <summary>Can't tell yet (no fresh price): looked at again later.</summary>
		Wait,

		/// <summary>What was paired, but not good for this account now (its sets, its prices): declined.</summary>
		Decline,

		/// <summary>Not what was paired - something extra or different: never accepted, reported, that account blocked.</summary>
		Bad,

		/// <summary>Says it's a swap, but no match this copy knows: left for you, nobody blamed.</summary>
		Unknown
	}

	/// <summary>
	/// The check of a swap offer by either side, on the offer Steam itself made - by the receiving side before it's
	/// accepted, by the sending side before it's confirmed: it matches what nocat.lol paired (the same cards, or some of the
	/// pairs - never anything else), with the account that was paired, one for one in every game; nothing in it is promised
	/// to another swap or offer; this account keeps "Keep this many of each card" (never its last copy) and its sets lose
	/// nothing (ASF's own test); and by its own prices - of the cards as Steam names them, never as nocat.lol was told -
	/// it gets at least what it gives, less the most a swap may lose.
	/// </summary>
	/// <param name="held">This account's cards now, less what's leaving in other swaps and offers.</param>
	/// <param name="price">A card's price in this account's wallet currency, null when there's no fresh one.</param>
	/// <param name="rules">What never goes from this account (<see cref="RulesOf"/>): a swap giving one is declined.</param>
	/// <param name="nowUtc">The receiving side: a match paired more than 2 days ago is declined.</param>
	/// <param name="busy">This account's copies on another offer or promised to another match: an offer asking for one is declined.</param>
	public static (Verdict Verdict, Said Why, long Give, long Get) Verify(TradeOffers.Offer offer, Match? match, IReadOnlyDictionary<SwapRules.Card, int> held,
		Func<TradeOffers.Item, int?> price, long maxLossCents, Rules? rules = null, DateTime? nowUtc = null, IReadOnlySet<ulong>? busy = null) {
		if ((match == null) || (match.Send != offer.Ours) || (offer.Partner != match.You)) {
			return (Verdict.Unknown, new Said("not a swap nocat.lol paired with this account"), 0, 0);
		}

		if ((nowUtc is DateTime now) && (now - match.Made > MatchFor)) {
			return (Verdict.Decline, new Said("it was paired more than 2 days ago"), 0, 0);
		}

		// Steam sometimes leaves descriptions out: that's "don't know yet", never "not a card".
		if (offer.Giving.Concat(offer.Receiving).Any(static i => !i.Described || (i.Hash.Length == 0))) {
			return (Verdict.Wait, new Said("Steam didn't say what every item is"), 0, 0);
		}

		if ((offer.Giving.Count == 0) || !offer.Giving.All(Normal) || !offer.Receiving.All(Normal)) {
			return (Verdict.Bad, new Said("it has something that isn't a normal trading card"), 0, 0);
		}

		// Priced by Steam's own name for each card, which starts with the card's own game: never a name from elsewhere.
		if (offer.Giving.Concat(offer.Receiving).Any(static i => !NameFits(i.Hash, i.Game))) {
			return (Verdict.Decline, new Said("Steam's name for a card in it doesn't fit its game"), 0, 0);
		}

		List<SwapRules.Card> giving = [.. offer.Giving.Select(static i => new SwapRules.Card(i.Game, i.ClassId))];
		List<SwapRules.Card> receiving = [.. offer.Receiving.Select(static i => new SwapRules.Card(i.Game, i.ClassId))];

		if (!AsPaired(offer, match)) {
			return (Verdict.Bad, new Said("it isn't the swap nocat.lol paired"), 0, 0);
		}

		if ((busy != null) && offer.Giving.Any(i => busy.Contains(i.AssetId))) {
			return (Verdict.Decline, new Said("a card in it is already on another offer or swap"), 0, 0);
		}

		// Kept since it was paired - put on a never-touch list, or its game's cards bought for a badge that isn't crafted yet.
		if ((rules != null) && offer.Giving.Any(i => rules.Keeps(i.Game, i.Name, i.Hash))) {
			return (Verdict.Decline, new Said("it gives a card this account keeps"), 0, 0);
		}

		// "Keep this many of each card" - and never the last copy, whatever it's set to.
		int keep = Math.Max(1, rules?.Keep ?? 1);

		if (giving.GroupBy(static c => c).FirstOrDefault(g => held.GetValueOrDefault(g.Key) - g.Count() < keep) is { } tooFew) {
			string name = offer.Giving.First(i => (i.Game == tooFew.Key.Game) && (i.ClassId == tooFew.Key.ClassId)).Name;

			return (Verdict.Decline, new Said("it would leave fewer than {0} of {1}", keep, name), 0, 0);
		}

		if (!SwapRules.NeutralOrBetter(held, giving, receiving)) {
			uint game = giving.Select(static c => c.Game).First(g => !SwapRules.NeutralOrBetter(held, [.. giving.Where(c => c.Game == g)], [.. receiving.Where(c => c.Game == g)]));

			return (Verdict.Decline, new Said("it would set back the {0} set", GameNames.Of(game)), 0, 0);
		}

		long give = 0, get = 0;

		foreach (TradeOffers.Item i in offer.Giving) {
			if (price(i) is not int p) {
				return (Verdict.Wait, new Said("no fresh price yet for {0}", i.Name), 0, 0);
			}

			give += p;
		}

		foreach (TradeOffers.Item i in offer.Receiving) {
			if (price(i) is not int p) {
				return (Verdict.Wait, new Said("no fresh price yet for {0}", i.Name), 0, 0);
			}

			get += p;
		}

		if (!SwapRules.PriceOk(give, get, maxLossCents)) {
			return (Verdict.Decline, new Said("it's worth less than this account gives"), give, get);
		}

		return (Verdict.Accept, default, give, get);
	}
}
