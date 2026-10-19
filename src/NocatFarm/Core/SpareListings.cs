using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// What spare items remembers about its own market listings, per account (config/state/spare-listings-&lt;account&gt;.json):
/// the items whose listing you declined on your phone, and the unsold listings it took down to put up a cent cheaper.
/// </summary>
/// <remarks>
/// Both are kept by market name ("730520-Level ??? Ninja"), every copy of it: a listing taken down or declined gives the
/// item back with a new id from Steam, so its old id says nothing once it's back.
/// </remarks>
public sealed class SpareListings {
	/// <summary>A listing nocat.farm made that you declined (or that came down before it ever went up), and when that was seen.</summary>
	public sealed record Declined(string Hash, ulong AssetId, DateTime At);

	/// <summary>An unsold listing taken down at <paramref name="FromCents"/> to go up again at <paramref name="ToCents"/> (what a buyer pays).</summary>
	public sealed record Step(string Hash, int FromCents, int ToCents, DateTime At);

	public List<Declined> Declines { get; set; } = [];
	public List<Step> Steps { get; set; } = [];

	/// <summary>A step not used within this long is forgotten: the item goes up at that day's price again.</summary>
	private static readonly TimeSpan StepLife = TimeSpan.FromDays(14);

	[System.Text.Json.Serialization.JsonIgnore]
	public string Account { get; private set; } = "";

	private static readonly Lock Gate = new();

	private static string PathOf(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"spare-listings-{account}.json");

	public static SpareListings Load(string account) {
		lock (Gate) {
			SpareListings? read = null;

			try {
				string path = PathOf(account);

				if (File.Exists(path)) {
					read = JsonSerializer.Deserialize<SpareListings>(File.ReadAllText(path));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				Log.Debug(new Said("couldn't read which listings you declined: {0}", Log.Describe(e)), account);
			}

			read ??= new SpareListings();
			read.Account = account;
			read.Declines ??= [];
			read.Steps ??= [];

			return read;
		}
	}

	public void Save() {
		if (Account.Length == 0) {
			return;
		}

		lock (Gate) {
			try {
				string path = PathOf(Account);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				Steps.RemoveAll(s => DateTime.UtcNow - s.At > StepLife);
				AtomicFile.Write(path, JsonSerializer.Serialize(this));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug(new Said("couldn't write down which listings you declined: {0}", Log.Describe(e)), Account);
			}
		}
	}

	// ── declined ─────────────────────────────────────────────────────────────
	public void AddDecline(string hash, ulong assetId, DateTime now) {
		if (hash.Length > 0) {
			Declines.Add(new Declined(hash, assetId, now));
		}
	}

	/// <summary>
	/// Why this item isn't listed now - its listing (this copy or another of the same item) was declined within
	/// <paramref name="days"/>, or ever with 0 - or null when it may be.
	/// </summary>
	public Said? DeclinedWhy(SpareItem item, int days, DateTime now) {
		Declined? last = Declines.Where(d => ((d.Hash.Length > 0) && (d.Hash == item.Hash)) || ((d.AssetId != 0) && (d.AssetId == item.AssetId)))
			.OrderByDescending(static d => d.At).FirstOrDefault();

		if (last == null) {
			return null;
		}

		if (days <= 0) {
			return new Said("you declined its listing on your phone - never listed again");
		}

		TimeSpan left = last.At.AddDays(days) - now;

		return left > TimeSpan.Zero ? new Said("you declined its listing on your phone - not listed again for {0} more day(s)", (int) Math.Ceiling(left.TotalDays)) : null;
	}

	// ── stepping down ────────────────────────────────────────────────────────
	/// <summary>
	/// What an unsold listing of ours goes up for next, a buyer's price - or null when it stays up as it is. A cent under our
	/// own last price, or a cent under a cheaper real listing when there is one (a dumped one, this far under what it sold
	/// for, doesn't count). Never under <paramref name="floorPct"/> percent of what it sold for over the last day, nor the
	/// market's 3 cents: at that floor it stays up, so it never goes round and round. No sale lately: no real price to
	/// keep to, so it isn't lowered at all. 1.8.2: nor when the step would pay under <paramref name="minReceive"/> ("Only sell
	/// if you get at least") or no more than <paramref name="gemsWorth"/> (its gems' worth, null: not known) - then it stays
	/// up as it is.
	/// </summary>
	public static int? StepTo(int ourCents, MarketPrice price, int floorPct, int dumpPct, int minReceive = 0, decimal? gemsWorth = null) {
		int median = price.Median ?? 0;
		int lowest = price.Cents;

		if ((median <= 0) || (ourCents <= 0)) {
			return null;
		}

		bool dump = (dumpPct > 0) && (lowest > 0) && (lowest * 100L < median * (100L - Math.Clamp(dumpPct, 0, 99)));
		int to = ourCents - 1;

		if ((lowest > 0) && !dump && (lowest < ourCents)) {
			to = Math.Min(to, lowest - 1);
		}

		int floor = Math.Max(3, (int) Math.Ceiling(median * Math.Clamp(floorPct, 1, 100) / 100m));
		to = Math.Max(to, floor);

		if (to >= ourCents) {
			return null;
		}

		int receive = Seller.ReceiveFor(to);

		return (receive < minReceive) || ((gemsWorth is decimal gems) && (gems > 0) && !Spare.SellingPays(gems, receive)) ? null : to;
	}

	/// <summary>The log line for one step: "lowered Level ??? Ninja from 0.31 CAD to 0.30 CAD - unsold 4 days".</summary>
	public static Said StepSaid(string name, int days, int fromCents, int toCents, int currency) =>
		new("lowered {0} from {2} to {3} - unsold {1} days", name, days, Spare.Money(fromCents, currency), Spare.Money(toCents, currency));

	public void AddStep(string hash, int fromCents, int toCents, DateTime now) {
		Steps.RemoveAll(s => s.Hash == hash);
		Steps.Add(new Step(hash, fromCents, toCents, now));
	}

	/// <summary>The most this item may go up for now: the step its unsold listing was given, while that's waiting to be used.</summary>
	public int? StepCap(string hash, DateTime now) => StepOf(hash, now)?.ToCents;

	/// <summary>The step this item's unsold listing was given, while that's waiting to be used - or null.</summary>
	public Step? StepOf(string hash, DateTime now) =>
		Steps.Where(s => (s.Hash == hash) && (now - s.At <= StepLife)).OrderByDescending(static s => s.At).FirstOrDefault();

	/// <summary>
	/// What an item with a step goes up for, given <paramref name="normal"/> - what the selling price rule lists it at today.
	/// The step only holds while today's price is still at or under the price the unsold listing had (<see cref="Step.FromCents"/>):
	/// then the lower of the two, but never under <paramref name="floorPct"/> percent of what it sold for over the last day,
	/// nor the market's 3 cents. Null when the market has gone up since - the step is stale, and today's price is the price.
	/// </summary>
	/// <remarks>A step kept as a plain ceiling put a card that had climbed from 0.31 to 0.50 back up at 0.30.</remarks>
	public static int? Capped(int normal, MarketPrice price, Step step, int floorPct) {
		if ((normal <= 0) || (normal > step.FromCents)) {
			return null;
		}

		int median = price.Median ?? 0;
		int floor = median > 0 ? Math.Max(3, (int) Math.Ceiling(median * Math.Clamp(floorPct, 1, 100) / 100m)) : 3;

		return Math.Min(normal, Math.Max(step.ToCents, floor));
	}

	/// <summary>The market went up since this item's step: the step is forgotten, and it goes up at that day's price.</summary>
	public static void Forget(string account, string hash) => Listed(account, hash);

	/// <summary>Whether an item first seen at <paramref name="seen"/> is one of ours, back from a listing taken down to step it.</summary>
	public bool CameBack(string hash, DateTime? seen) =>
		(seen is DateTime at) && Steps.Any(s => (s.Hash == hash) && (at >= s.At.AddMinutes(-5)) && (at - s.At <= StepLife));

	/// <summary>Listed again: the step is used.</summary>
	public static void Listed(string account, string hash) {
		lock (Gate) {
			SpareListings mine = Load(account);

			if (mine.Steps.RemoveAll(s => s.Hash == hash) > 0) {
				mine.Save();
			}
		}
	}
}
