using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// What spare items remembers per account, in config/state: when each item was first seen (for the "came in within N
/// days" rule), whether you've said <c>spare ok</c>, whether the first preview went out, and today's count and value.
/// </summary>
/// <remarks>
/// Steam's inventory doesn't say when an item arrived. So the first time nocat.farm sees an item is taken as when it
/// came in. Except on the very first look at an account: everything already there counts as old - months-old cards
/// waited a week otherwise - and only what comes in after that waits its days. After an update from 1.8.0, what its own
/// first look saw counts as old the same way, and what it saw come in later keeps the day it came. An item that leaves and comes back (a listing taken down) gets a new id from Steam and
/// starts its days again - unless it's one of ours, back to be listed a cent cheaper (<see cref="MarkOld"/>).
/// </remarks>
public sealed class SpareLedger {
	/// <summary>Asset id -> unix seconds first seen.</summary>
	public Dictionary<string, long> Seen { get; set; } = new(StringComparer.Ordinal);

	/// <summary>
	/// The first look has been made: everything held then counts as old, and only items seen after it wait. False in a
	/// ledger from 1.8.0, whose first look counted everything as new - its next look counts that first look's items as old.
	/// </summary>
	public bool FirstLookDone { get; set; }

	/// <summary>You typed <c>spare ok</c> for this account: it may act, not only preview.</summary>
	public bool Approved { get; set; }

	/// <summary>The first preview has gone to the log and your chats - once is enough until something changes.</summary>
	public bool PreviewPosted { get; set; }

	/// <summary>The day the counts below are for (local date, yyyy-MM-dd).</summary>
	public string Day { get; set; } = "";

	public int DoneItems { get; set; }
	public int DoneCents { get; set; }

	[System.Text.Json.Serialization.JsonIgnore]
	public string Account { get; private set; } = "";

	/// <summary>Everything here is changed and saved under this one lock.</summary>
	private static readonly Lock Gate = new();

	/// <summary>
	/// One ledger per account for the whole program, by file. Each look used to read its own copy: a 'gems ok' typed while
	/// a look was pricing (minutes) was then saved over by the look's older copy, and quietly undone.
	/// </summary>
	private static readonly Dictionary<string, SpareLedger> Loaded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>It came from the file or has been written to it - so a file gone since means the account was removed.</summary>
	private bool _onDisk;

	private static string PathOf(string name) => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", $"spare-{name}.json");

	public static SpareLedger Load(string account) {
		lock (Gate) {
			string path = PathOf(account);

			// Removed and added again by the same name: its file went with it, and so does what was said for it (gems ok).
			if (Loaded.TryGetValue(path, out SpareLedger? held) && (!held._onDisk || File.Exists(path))) {
				return held;
			}

			SpareLedger? read = null;

			try {
				if (File.Exists(path)) {
					read = JsonSerializer.Deserialize<SpareLedger>(File.ReadAllText(path));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				// Started over: the next look counts what's held as old, as a first look does, and it has to be turned on again.
				Log.Failed("couldn't read what spare items remembers", e, account);
			}

			bool fromFile = read != null;
			read ??= new SpareLedger();
			read.Account = account;
			read.Seen ??= new Dictionary<string, long>(StringComparer.Ordinal);
			read._onDisk = fromFile;
			Loaded[path] = read;

			return read;
		}
	}

	/// <summary>Only the checks: as if the program had just started - every ledger is read from its file again.</summary>
	public static void ForgetLoaded() {
		lock (Gate) {
			Loaded.Clear();
		}
	}

	public void Save() {
		if (Account.Length == 0) {
			return;
		}

		lock (Gate) {
			try {
				string path = PathOf(Account);
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
				AtomicFile.Write(path, JsonSerializer.Serialize(this));
				_onDisk = true;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed("couldn't save what spare items remembers", e, Account);
			}
		}
	}

	/// <summary>When an item counts as seen long ago: 1 January 2000, older than any "new" rule.</summary>
	private const long LongAgo = 946_684_800;

	/// <summary>Seconds after the earliest time in a 1.8.0 ledger that still count as its first look (one look writes one time).</summary>
	private const long FirstLookSpread = 60;

	/// <summary>
	/// Note the items held now: new ones are seen from <paramref name="now"/>, ones that left are forgotten. On the first
	/// look (<see cref="FirstLookDone"/>) every item held counts as old.
	/// </summary>
	public void Observe(IEnumerable<ulong> held, DateTime now) {
		HashSet<string> ids = [.. held.Select(static id => id.ToString(CultureInfo.InvariantCulture))];
		long at = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds();

		lock (Gate) {
			// A ledger from 1.8.0: only what its own first look saw (the earliest time in it - one look, one time) counts as
			// old. What it saw come in after that keeps its day, and what came in since its last look is new from now. Every
			// item it held was made old once, and a card 1.8.0 had rightly seen arrive yesterday was spare straight away.
			long? firstBatch = !FirstLookDone && (Seen.Count > 0) ? Seen.Values.Min() : null;

			foreach (string id in ids) {
				if (FirstLookDone) {
					Seen.TryAdd(id, at);
				} else if (firstBatch is not long first) {
					Seen[id] = LongAgo;
				} else if (!Seen.TryGetValue(id, out long seen)) {
					Seen[id] = at;
				} else if (seen - first <= FirstLookSpread) {
					Seen[id] = LongAgo;
				}
			}

			FirstLookDone = true;

			foreach (string gone in Seen.Keys.Where(k => !ids.Contains(k)).ToList()) {
				Seen.Remove(gone);
			}
		}
	}

	/// <summary>Count this item as seen long ago: one of ours, back from a listing taken down to go up cheaper.</summary>
	public void MarkOld(ulong asset) {
		lock (Gate) {
			Seen[asset.ToString(CultureInfo.InvariantCulture)] = LongAgo;
		}
	}

	/// <summary>When this item was first seen, or null when it hasn't been.</summary>
	public DateTime? Received(ulong asset) {
		lock (Gate) {
			return Seen.TryGetValue(asset.ToString(CultureInfo.InvariantCulture), out long at) ? DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime : null;
		}
	}

	private void Roll(DateTime nowLocal) {
		string today = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		if (Day != today) {
			Day = today;
			DoneItems = 0;
			DoneCents = 0;
		}
	}

	/// <summary>Whether one more item worth <paramref name="cents"/> still fits under today's caps.</summary>
	public bool Fits(int cents, int maxItems, int maxCents, DateTime nowLocal) {
		lock (Gate) {
			Roll(nowLocal);

			return (DoneItems < maxItems) && (DoneCents + Math.Max(0, cents) <= maxCents);
		}
	}

	/// <summary>
	/// Check and count in one step, under the lock: true (and counted) when one more item worth <paramref name="cents"/>
	/// still fits under today's caps. A look and a command acting at the same moment each checked the caps and then
	/// counted after their own Steam call - both fit, and the day went over. <see cref="Unreserve"/> gives it back when
	/// Steam refuses.
	/// </summary>
	public bool TryReserve(int cents, int maxItems, int maxCents, DateTime nowLocal) {
		lock (Gate) {
			Roll(nowLocal);

			if ((DoneItems >= maxItems) || (DoneCents + Math.Max(0, cents) > maxCents)) {
				return false;
			}

			DoneItems++;
			DoneCents += Math.Max(0, cents);

			return true;
		}
	}

	/// <summary>
	/// Count <paramref name="extraCents"/> more against today's value cap for an item already counted - its price went up
	/// between the look and the listing - when it still fits. Under the same lock, so it can't race a reserve.
	/// </summary>
	public bool TryRaise(int extraCents, int maxCents, DateTime nowLocal) {
		lock (Gate) {
			Roll(nowLocal);

			if (DoneCents + Math.Max(0, extraCents) > maxCents) {
				return false;
			}

			DoneCents += Math.Max(0, extraCents);

			return true;
		}
	}

	/// <summary>Give back one item reserved today that Steam refused - only on the same day it was counted.</summary>
	public void Unreserve(int cents, DateTime nowLocal) {
		lock (Gate) {
			if (Day != nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) {
				return;
			}

			DoneItems = Math.Max(0, DoneItems - 1);
			DoneCents = Math.Max(0, DoneCents - Math.Max(0, cents));
		}
	}

	/// <summary>Count one item done today.</summary>
	public void Count(int cents, DateTime nowLocal) {
		lock (Gate) {
			Roll(nowLocal);
			DoneItems++;
			DoneCents += Math.Max(0, cents);
		}
	}
}
