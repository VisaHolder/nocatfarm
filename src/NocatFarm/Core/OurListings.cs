using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The market listings nocat.farm put up itself, per account (config/state/listed-&lt;account&gt;.json) - so taking down
/// stale listings (Spare items) only ever touches its own, never a card the owner listed by hand.
/// </summary>
/// <remarks>
/// Steam doesn't say who made a listing, so each one is written down as it goes up: the item, its market name, what the
/// seller gets and when. Steam can give a listed item a new id; then the same item at the same price, listed within a few
/// minutes of the record, is it. Each record matches one listing at most.
/// </remarks>
public static class OurListings {
	/// <param name="Tracked">Written by 1.8.1 or later, which follows each listing until it's gone - see <see cref="Judge"/>.
	/// An older record is never taken for a declined listing: whether it was ever up isn't known.</param>
	/// <param name="Active">Seen up on the market (not waiting on a confirmation), or confirmed by nocat.farm itself.</param>
	/// <param name="Settled">Gone from the market, and already judged - sold, taken down, or declined.</param>
	/// <param name="BuyerCents">What a buyer paid for it as listed - the price a step down starts from. 0 in a record from
	/// before 1.8.1.</param>
	public sealed record Entry(ulong AssetId, string Hash, int YouGetCents, DateTime At, bool Tracked = false, bool Active = false, bool Settled = false,
		int BuyerCents = 0);

	/// <summary>One sale in the account's market history: the item, what the seller got (0 when Steam didn't say) and when.</summary>
	public sealed record Sale(ulong AssetId, string Hash, int YouGetCents, DateTime At);

	/// <summary>
	/// What the account's market history says sold. <paramref name="Covers"/>: the history only reaches back this far (Steam
	/// keeps more than one page) - a listing of ours from before it can't be told sold or not. MinValue when it's all there.
	/// </summary>
	public sealed record History(IReadOnlyList<Sale> Sales, DateTime Covers);

	/// <summary>A listing this new may not show in Steam's list yet - it isn't judged before then.</summary>
	internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

	/// <summary>How far a listing's own time may be from the record's and still be the same one.</summary>
	private static readonly TimeSpan Slack = TimeSpan.FromMinutes(30);

	/// <summary>A record is kept this long - a listing still up after that is long past any relisting.</summary>
	private static readonly TimeSpan Life = TimeSpan.FromDays(400);

	private static readonly Lock Gate = new();

	private static string PathOf(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"listed-{account}.json");

	public static List<Entry> Load(string account) {
		lock (Gate) {
			try {
				string path = PathOf(account);

				return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [] : [];
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				// Nothing known as ours: then nothing is taken down - the safe way round.
				Log.Debug(new Said("couldn't read which listings nocat.farm made: {0}", Log.Describe(e)), account);

				return [];
			}
		}
	}

	/// <summary>Write down one listing nocat.farm just put up, with what a buyer pays for it.</summary>
	public static void Add(string account, ulong assetId, string hash, int youGetCents, DateTime utc, int buyerCents = 0) {
		lock (Gate) {
			List<Entry> all = Load(account);
			all.RemoveAll(e => utc - e.At > Life);
			all.Add(new Entry(assetId, hash, youGetCents, utc, Tracked: true, BuyerCents: buyerCents));
			Write(account, all);
		}
	}

	private static bool Write(string account, List<Entry> all) {
		try {
			string path = PathOf(account);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(all));

			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("couldn't write down a listing nocat.farm made: {0}", Log.Describe(e)), account);

			return false;
		}
	}

	/// <summary>
	/// Each listing of ours paired with its record: by item id, or the same item at the same price listed then. Each record
	/// pairs with one listing at most; records already settled pair with none.
	/// </summary>
	internal static Dictionary<int, Seller.Listing> Pair(IEnumerable<Seller.Listing> listings, IReadOnlyList<Entry> ours) {
		List<Seller.Listing> all = [.. listings];
		Dictionary<int, Seller.Listing> paired = [];
		HashSet<Seller.Listing> used = [];

		foreach (Seller.Listing l in all.Where(static l => l.AssetId != 0)) {
			int at = Free(e => e.AssetId == l.AssetId);

			if (at >= 0) {
				paired[at] = l;
				used.Add(l);
			}
		}

		foreach (Seller.Listing l in all.Where(l => !used.Contains(l) && (l.Hash.Length > 0))) {
			int at = Free(e => (e.Hash == l.Hash) && (e.YouGetCents == l.YouGetCents) && ((l.Created - e.At).Duration() <= Slack));

			if (at >= 0) {
				paired[at] = l;
				used.Add(l);
			}
		}

		return paired;

		int Free(Func<Entry, bool> fits) {
			for (int i = 0; i < ours.Count; i++) {
				if (!ours[i].Settled && !paired.ContainsKey(i) && fits(ours[i])) {
					return i;
				}
			}

			return -1;
		}
	}

	/// <summary>
	/// Follow each listing nocat.farm made against what's on the market now. Seen up (or held by Steam): noted. Gone after
	/// being up: sold or taken down, settled. Gone without ever being seen up: confirmed on the phone and sold before the
	/// next look - found in the market <paramref name="history"/> - or declined in the Steam app, or taken down while it still
	/// waited on its confirmation: settled, and returned as declined. Steam's confirmation list doesn't keep a declined one,
	/// and the listing simply vanishes (as ASF and node-steamcommunity see it too). Nothing is judged from a list that may
	/// not be whole (<paramref name="complete"/> false), without the history (null - Steam wouldn't say), nor a listing younger
	/// than <see cref="Grace"/>. Older than the history reaches back: it can't be told, and counts as declined, the safe way round.
	/// </summary>
	public static (List<Entry> Entries, List<Entry> Declined) Judge(IReadOnlyList<Entry> ours, IReadOnlyList<Seller.Listing> listings, bool complete, History? history,
		DateTime now) {
		(List<Entry> entries, List<Entry> declined, _) = JudgeAll(ours, listings, complete, history, now);

		return (entries, declined);
	}

	/// <summary>
	/// <see cref="Judge"/>, and the listings the market history says sold - each told once, as it's found: one gone after it
	/// was up is looked for in the history too (with none, it's settled as before, unsaid).
	/// </summary>
	public static (List<Entry> Entries, List<Entry> Declined, List<Entry> Sold) JudgeAll(IReadOnlyList<Entry> ours, IReadOnlyList<Seller.Listing> listings, bool complete,
		History? history, DateTime now) {
		Dictionary<int, Seller.Listing> paired = Pair(listings, ours);
		List<Entry> entries = [];
		List<Entry> declined = [];
		List<Entry> sold = [];
		HashSet<int> used = [];

		for (int i = 0; i < ours.Count; i++) {
			Entry e = ours[i];

			if (e.Settled) {
				entries.Add(e);

				continue;
			}

			if (paired.TryGetValue(i, out Seller.Listing? l)) {
				entries.Add(l.AwaitingConfirmation ? e : e with { Active = true });

				continue;
			}

			if (!complete || (now - e.At < Grace)) {
				entries.Add(e);

				continue;
			}

			if (e.Tracked && !e.Active) {
				if (history == null) {
					entries.Add(e);   // whether it sold isn't known yet - the next look tells

					continue;
				}

				if (SoldAs(e) is int sale) {
					used.Add(sale);
					entries.Add(e with { Active = true, Settled = true });
					sold.Add(Told(e, history.Sales[sale]));

					continue;
				}

				declined.Add(e);
			} else if (e.Tracked && (history == null)) {
				entries.Add(e);   // up, now gone: sold or taken down - the history tells which, next look

				continue;
			} else if (e.Tracked && (SoldAs(e) is int sale)) {
				used.Add(sale);
				sold.Add(Told(e, history!.Sales[sale]));
			}

			entries.Add(e with { Settled = true });
		}

		return (entries, declined, sold);

		// What the seller got as the history says it, when it says it.
		static Entry Told(Entry e, Sale s) => s.YouGetCents > 0 ? e with { YouGetCents = s.YouGetCents } : e;

		// A sale of this very item, or of the same item at the same price after it was listed - each sale counts once.
		int? SoldAs(Entry e) {
			for (int j = 0; j < history!.Sales.Count; j++) {
				Sale s = history.Sales[j];

				if (!used.Contains(j) && (s.At >= e.At - Slack) && (((s.AssetId != 0) && (s.AssetId == e.AssetId))
					|| ((s.Hash.Length > 0) && (s.Hash == e.Hash) && ((s.YouGetCents == 0) || (s.YouGetCents == e.YouGetCents))))) {
					return j;
				}
			}

			return null;
		}
	}

	/// <summary><see cref="Judge"/> on the account's own records, saved. Returns the declined ones.</summary>
	public static List<Entry> Review(string account, IReadOnlyList<Seller.Listing> listings, History? history, DateTime now) =>
		ReviewAll(account, listings, history, now).Declined;

	/// <summary>
	/// The listings that went up between two judgings: waiting on their confirmation before, up on the market now - one
	/// confirmed in the Steam app. <paramref name="after"/> is <see cref="JudgeAll"/>'s entries for <paramref name="before"/>,
	/// in the same order.
	/// </summary>
	public static List<Entry> WentLive(IReadOnlyList<Entry> before, IReadOnlyList<Entry> after) =>
		[.. after.Where((e, i) => (i < before.Count) && e is { Active: true, Settled: false } && before[i] is { Active: false, Settled: false })];

	/// <summary><see cref="JudgeAll"/> on the account's own records, saved. Returns the declined ones, the sold ones, and
	/// the ones that went up on the market since (<see cref="WentLive"/>).</summary>
	public static (List<Entry> Declined, List<Entry> Sold, List<Entry> Live) ReviewAll(string account, IReadOnlyList<Seller.Listing> listings, History? history, DateTime now) {
		lock (Gate) {
			List<Entry> all = Load(account);

			if (all.Count == 0) {
				return ([], [], []);
			}

			// Steam's list shows a hundred listings: with that many up, one of ours may only be off the page.
			bool complete = (listings.Count(static l => !l.AwaitingConfirmation && !l.OnHold) < 100) && (listings.Count(static l => l.OnHold) < 100);
			(List<Entry> entries, List<Entry> declined, List<Entry> sold) = JudgeAll(all, listings, complete, history, now);

			bool saved = entries.SequenceEqual(all) || Write(account, entries);

			// Each sale counted once, in the history and the live stats on nocat.lol (1.8.1): only once it's written down
			// as told - one that couldn't be is told again next look, and counted then.
			if (saved) {
				foreach (Entry _ in sold) {
					NocatFarm.Core.History.Note(FarmTotals.Extra.Sold, account, now);
				}
			}

			return (declined, sold, WentLive(all, entries));
		}
	}

	/// <summary>
	/// Whether a listing of ours that was up is gone from the market now (sold, or taken down) - then the look reads the
	/// market history (one request) to tell a sale. Only listings this version follows, past <see cref="Grace"/>.
	/// </summary>
	public static bool Gone(IReadOnlyList<Entry> ours, IReadOnlyList<Seller.Listing> listings, DateTime now) {
		Dictionary<int, Seller.Listing> paired = Pair(listings, ours);

		return ours.Select(static (e, i) => (e, i)).Any(x => x.e is { Tracked: true, Active: true, Settled: false } && !paired.ContainsKey(x.i) && (now - x.e.At >= Grace));
	}

	/// <summary>
	/// Mark the record of this listing as up - confirmed by nocat.farm on the account's authenticator - or, with
	/// <paramref name="settled"/>, as gone: taken down by nocat.farm itself, never a declined one.
	/// </summary>
	public static void Mark(string account, Seller.Listing listing, bool settled = false) {
		lock (Gate) {
			List<Entry> all = Load(account);

			if (Pair([listing], all) is not { Count: 1 } p) {
				return;
			}

			int at = p.Keys.Single();
			all[at] = all[at] with { Active = true, Settled = all[at].Settled || settled };
			Write(account, all);
		}
	}

	/// <summary>
	/// Whether a look has listings of ours to judge that may have sold before it ever saw them up - then the market history
	/// is read (one request), otherwise not.
	/// </summary>
	public static bool Undecided(string account) => Load(account).Any(static e => e.Tracked && !e.Active && !e.Settled);

	/// <summary>
	/// The items with a listing of ours still waiting on the phone, or not judged yet - no other copy of one is listed until
	/// it is: a copy declined minutes ago, not yet judged, was otherwise listed again by the next look.
	/// </summary>
	public static Func<SpareItem, Said?> WaitingWhy(IReadOnlyList<Entry> ours) {
		HashSet<string> waiting = [.. ours.Where(static e => e.Tracked && !e.Active && !e.Settled && (e.Hash.Length > 0)).Select(static e => e.Hash)];

		return item => (item.Hash.Length > 0) && waiting.Contains(item.Hash) ? new Said("a listing of it is still waiting on your phone - no other copy is listed until that's settled") : null;
	}

	/// <summary>
	/// What a buyer paid for this listing of ours as it went up - where a step down starts from: as written down when it was
	/// listed, else Steam's own price plus fee, else worked out from what the seller gets (a cent off at times).
	/// </summary>
	public static int BuyerPrice(Seller.Listing listing, Entry? ours) =>
		ours is { BuyerCents: > 0 } e ? e.BuyerCents
		: listing.BuyerCents > 0 ? listing.BuyerCents
		: listing.YouGetCents + Seller.FeesFor(listing.YouGetCents);

	/// <summary>
	/// What a buyer pays for our own cheapest listing of an item that's still up or waiting on the phone, by market name - 0
	/// when there's none. "A cent under the cheapest" keeps level with it instead of going under it (1.8.2).
	/// </summary>
	public static int UpFor(IReadOnlyList<Entry> ours, string hash) =>
		hash.Length == 0 ? 0
		: ours.Where(e => e.Tracked && !e.Settled && (e.BuyerCents > 0) && (e.Hash == hash)).Select(static e => e.BuyerCents).DefaultIfEmpty(0).Min();

	/// <summary>Each listing of ours with its record.</summary>
	public static List<(Seller.Listing Listing, Entry Entry)> OursWithRecords(IEnumerable<Seller.Listing> listings, IReadOnlyList<Entry> ours) =>
		[.. Pair(listings, ours).Select(p => (p.Value, ours[p.Key]))];

	/// <summary>The listings among <paramref name="listings"/> that nocat.farm made: by item id, or the same item at the same price listed then.</summary>
	public static List<Seller.Listing> Ours(IEnumerable<Seller.Listing> listings, IReadOnlyList<Entry> ours) {
		List<Seller.Listing> all = [.. listings];
		HashSet<Seller.Listing> mine = [.. Pair(all, ours).Values];

		return [.. all.Where(mine.Contains)];
	}
}
