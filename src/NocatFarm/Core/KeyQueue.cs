using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Keys waiting for a turn.
///
/// Steam locks an account out of activations for about an hour once it has seen a few failures, so a hundred keys
/// pasted in at once cannot simply be worked through: somewhere around the tenth, every remaining key comes back
/// "rate limited", and redeeming them there and then would burn the lot for nothing.
///
/// So they queue instead. Anything that hits a rate limit goes back on the queue rather than being counted as
/// tried, the queue is retried on a slow timer, and it is written to disk - which is the whole point, because
/// the alternative is a crash halfway through a batch losing every key that had not been reached yet.
///
/// The queue holds keys, which are worth money, so it is written atomically and never cleared on a failure path.
/// </summary>
public static class KeyQueue {
	/// <summary>How long to leave an account alone after Steam says it has had enough.</summary>
	private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(65);

	private sealed class Entry {
		public string Key { get; set; } = "";
		public long AddedAt { get; set; }
		public int Tries { get; set; }
		public long NotBefore { get; set; }   // unix seconds; 0 means "any time"
	}

	private static readonly List<Entry> Pending = [];
	private static readonly Lock Gate = new();
	private static readonly Random Rng = new();
	private static bool _loaded;

	/// <summary>Earliest moment the next activation may go out - jittered, so the queue doesn't tick like a clock.</summary>
	private static DateTime _nextAllowed = DateTime.MinValue;

	/// <summary>
	/// May another key go now?
	///
	/// A queue that fires every thirty seconds on the dot is a machine typing, and activations are exactly the
	/// sort of thing Steam counts. Ninety seconds to five minutes between them, rolled fresh each time, is both
	/// well under any limit and shaped like somebody working through a list rather than a script.
	/// </summary>
	public static bool DueNow() => DateTime.UtcNow >= _nextAllowed;

	/// <summary>Called after each attempt, successful or not.</summary>
	public static void Spent() => _nextAllowed = DateTime.UtcNow.AddSeconds(Rng.Next(90, 301));

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "keys.json");

	public static int Count {
		get {
			Load();

			lock (Gate) {
				return Pending.Count;
			}
		}
	}

	/// <summary>Queue keys for the background worker. Duplicates are ignored rather than tried twice.</summary>
	public static int Add(IEnumerable<string> keys) {
		Load();
		int added = 0;

		lock (Gate) {
			foreach (string key in keys) {
				string trimmed = key.Trim();

				if ((trimmed.Length == 0) || Pending.Exists(e => string.Equals(e.Key, trimmed, StringComparison.OrdinalIgnoreCase))) {
					continue;
				}

				Pending.Add(new Entry { Key = trimmed, AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
				added++;
			}
		}

		Save();

		return added;
	}

	/// <summary>The next key that is allowed to be tried right now, or null.</summary>
	public static string? Next() {
		Load();
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		lock (Gate) {
			return Pending.Find(e => e.NotBefore <= now)?.Key;
		}
	}

	/// <summary>It worked, or it is dead. Either way it never comes back.</summary>
	public static void Done(string key) {
		lock (Gate) {
			Pending.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
		}

		Save();
	}

	/// <summary>
	/// Nobody could take it right now. Put it to the back with a cooldown - and after enough goes, give up on it
	/// so a permanently unusable key cannot occupy the queue for ever.
	/// </summary>
	public static void Defer(string key) {
		Load();

		lock (Gate) {
			Entry? entry = Pending.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

			if (entry == null) {
				return;
			}

			entry.Tries++;
			entry.NotBefore = DateTimeOffset.UtcNow.Add(Cooldown).ToUnixTimeSeconds();

			if (entry.Tries >= 8) {
				Pending.Remove(entry);
				Log.Warn(new Said("giving up on a key after {0} tries - no account could activate it", entry.Tries));
			} else {
				Pending.Remove(entry);
				Pending.Add(entry);   // to the back, so one stubborn key doesn't block the rest
			}
		}

		Save();
	}

	/// <summary>Everything still waiting, newest last, for the 'keys' command.</summary>
	public static List<(string Key, int Tries, DateTime NotBefore)> Snapshot() {
		Load();

		lock (Gate) {
			return [.. Pending.Select(static e => (
				e.Key,
				e.Tries,
				e.NotBefore > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.NotBefore).UtcDateTime : DateTime.MinValue))];
		}
	}

	public static int Clear() {
		Load();
		int had;

		lock (Gate) {
			had = Pending.Count;
			Pending.Clear();
		}

		Save();

		return had;
	}

	private static void Load() {
		if (_loaded) {
			return;
		}

		_loaded = true;

		try {
			if (!File.Exists(Path)) {
				return;
			}

			List<Entry>? saved = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path));

			if (saved != null) {
				lock (Gate) {
					Pending.AddRange(saved);
				}
			}
		} catch (Exception e) {
			Log.Warn(new Said("couldn't read the key queue: {0}", e.Message));
		}
	}

	public static void Save() {
		try {
			List<Entry> snapshot;

			lock (Gate) {
				snapshot = [.. Pending];
			}

			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
			AtomicFile.Write(Path, JsonSerializer.Serialize(snapshot));
		} catch (Exception e) {
			Log.Warn(new Said("couldn't save the key queue: {0}", e.Message));
		}
	}
}
