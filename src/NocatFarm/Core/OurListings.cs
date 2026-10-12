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
	public sealed record Entry(ulong AssetId, string Hash, int YouGetCents, DateTime At);

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

	/// <summary>Write down one listing nocat.farm just put up.</summary>
	public static void Add(string account, ulong assetId, string hash, int youGetCents, DateTime utc) {
		lock (Gate) {
			List<Entry> all = Load(account);
			all.RemoveAll(e => utc - e.At > Life);
			all.Add(new Entry(assetId, hash, youGetCents, utc));

			try {
				string path = PathOf(account);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				AtomicFile.Write(path, JsonSerializer.Serialize(all));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug(new Said("couldn't write down a listing nocat.farm made: {0}", Log.Describe(e)), account);
			}
		}
	}

	/// <summary>The listings among <paramref name="listings"/> that nocat.farm made: by item id, or the same item at the same price listed then.</summary>
	public static List<Seller.Listing> Ours(IEnumerable<Seller.Listing> listings, IReadOnlyList<Entry> ours) {
		List<Seller.Listing> all = [.. listings];
		List<Entry> left = [.. ours];
		List<Seller.Listing> found = [];

		foreach (Seller.Listing l in all.Where(static l => l.AssetId != 0)) {
			int at = left.FindIndex(e => e.AssetId == l.AssetId);

			if (at >= 0) {
				found.Add(l);
				left.RemoveAt(at);
			}
		}

		foreach (Seller.Listing l in all.Where(l => !found.Contains(l) && (l.Hash.Length > 0))) {
			int at = left.FindIndex(e => (e.Hash == l.Hash) && (e.YouGetCents == l.YouGetCents) && ((l.Created - e.At).Duration() <= Slack));

			if (at >= 0) {
				found.Add(l);
				left.RemoveAt(at);
			}
		}

		return found;
	}
}
