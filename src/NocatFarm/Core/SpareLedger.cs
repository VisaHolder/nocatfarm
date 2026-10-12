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
/// came in - which on the very first look means everything counts as new, and nothing is spare for the first days. That
/// is the safe way round: an item that came in yesterday is never taken for an old one. An item that leaves and comes
/// back (a listing taken down) gets a new id from Steam and starts its days again.
/// </remarks>
public sealed class SpareLedger {
	/// <summary>Asset id -> unix seconds first seen.</summary>
	public Dictionary<string, long> Seen { get; set; } = new(StringComparer.Ordinal);

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
				// Started over: everything counts as new again, and gems ok has to be said again - the safe way round.
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

	/// <summary>Note the items held now: new ones are seen from <paramref name="now"/>, ones that left are forgotten.</summary>
	public void Observe(IEnumerable<ulong> held, DateTime now) {
		HashSet<string> ids = [.. held.Select(static id => id.ToString(CultureInfo.InvariantCulture))];
		long at = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeSeconds();

		lock (Gate) {
			foreach (string id in ids) {
				Seen.TryAdd(id, at);
			}

			foreach (string gone in Seen.Keys.Where(k => !ids.Contains(k)).ToList()) {
				Seen.Remove(gone);
			}
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
