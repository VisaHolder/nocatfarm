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

		// The one account this key is for, when 'redeem <account> ...' named one. Null means whichever account can
		// use it. A file written before this existed has no such field and reads back as null - the old behaviour.
		public string? Account { get; set; }

		// Accounts that already answered "not for this account" - they own it, it's another region's, it needs a game they
		// haven't got. Asked again, each would only say so again and spend one of its failed activations doing it.
		public List<string>? RefusedBy { get; set; }
	}

	private static readonly List<Entry> Pending = [];
	private static readonly Lock Gate = new();
	private static readonly Random Rng = new();
	private static bool _loaded;
	private static readonly Lock LoadGate = new();

	/// <summary>
	/// One save at a time, from the snapshot to the file. Taken before the snapshot, the order the queue changed in is
	/// the order it reaches the disk: snapshot under Gate and written after, a 'redeem' adding keys and the worker
	/// finishing one saved at once - and whichever older snapshot landed last lost the new keys or brought a used key
	/// back to be activated again.
	/// </summary>
	private static readonly Lock SaveGate = new();

	/// <summary>
	/// Set when keys.json existed but could not be read. Saving then would write the in-memory queue - missing
	/// every key in the file - over the only copy of them, so Save refuses until the next run reads it cleanly.
	/// </summary>
	private static bool _loadFailed;
	private static bool _saveRefusedSaid;

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

	/// <summary>
	/// Queue keys for the background worker. Duplicates are ignored rather than tried twice. With
	/// <paramref name="account"/> they are only ever tried on that account - an activation cannot be undone, so a
	/// key meant for one account must never land on another just because it was part of a big batch.
	/// </summary>
	public static int Add(IEnumerable<string> keys, string? account = null) {
		Load();
		int added = 0;

		lock (Gate) {
			foreach (string key in keys) {
				string trimmed = key.Trim();

				if (trimmed.Length == 0) {
					continue;
				}

				// Already queued: a named account now pins it, or the key could still go to another account.
				if (Pending.Find(e => string.Equals(e.Key, trimmed, StringComparison.OrdinalIgnoreCase)) is { } queued) {
					if (account != null) {
						queued.Account = account;
					}

					continue;
				}

				Pending.Add(new Entry { Key = trimmed, AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Account = account });
				added++;
			}
		}

		Save();

		return added;
	}

	/// <summary>
	/// The next key that is allowed to be tried right now, and the account it is for (null = any), or null.
	/// <paramref name="usable"/> says whether a key for that account can be tried at all right now - so a key for
	/// an account that is offline waits without holding up the keys behind it.
	/// </summary>
	/// <param name="usable">Given the key's account and the accounts that already turned it down for good.</param>
	public static (string Key, string? Account, IReadOnlyList<string> RefusedBy)? Next(Func<string?, IReadOnlyList<string>, bool> usable) {
		Load();
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		lock (Gate) {
			return Pending.Find(e => (e.NotBefore <= now) && usable(e.Account, e.RefusedBy ?? [])) is { } next
				? (next.Key, next.Account, [.. next.RefusedBy ?? []])
				: null;
		}
	}

	/// <summary>
	/// This account can't use this key and never will - it owns the game, the key is another region's. It isn't asked
	/// again. True when every one of <paramref name="accounts"/> has now said so: nobody is left to try it.
	/// </summary>
	public static bool RefusedOn(string key, string account, IEnumerable<string> accounts) {
		bool everyone;

		lock (Gate) {
			if (Pending.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } entry) {
				return false;
			}

			entry.RefusedBy ??= [];

			if (!entry.RefusedBy.Contains(account, StringComparer.OrdinalIgnoreCase)) {
				entry.RefusedBy.Add(account);
			}

			everyone = accounts.All(a => entry.RefusedBy.Contains(a, StringComparer.OrdinalIgnoreCase));
		}

		Save();

		return everyone;
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
				Log.Warn(new Said("gave up on a key after {0} tries - no account could use it", entry.Tries));
			} else {
				Pending.Remove(entry);
				Pending.Add(entry);   // to the back, so one stubborn key doesn't block the rest
			}
		}

		Save();
	}

	/// <summary>Everything still waiting, newest last, for the 'keys' command.</summary>
	public static List<(string Key, int Tries, DateTime NotBefore, string? Account)> Snapshot() {
		Load();

		lock (Gate) {
			return [.. Pending.Select(static e => (
				e.Key,
				e.Tries,
				e.NotBefore > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.NotBefore).UtcDateTime : DateTime.MinValue,
				e.Account))];
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

	/// <summary>Forget the queue in memory and read keys.json again - after a restore put a different one in its place.</summary>
	internal static void Reload() {
		lock (LoadGate) {
			lock (Gate) {
				Pending.Clear();
			}

			_loaded = false;
			_loadFailed = false;
		}

		Load();
	}

	private static void Load() {
		// The check and the read under one lock. Marked loaded first and read after, a second caller in the meantime went
		// on as if it were loaded - the Upkeep driver's first look and a 'redeem' together: the redeem found it loaded
		// while the file was still being read, and saved a queue holding only its own key over every key waiting in
		// keys.json.
		lock (LoadGate) {
			if (_loaded) {
				return;
			}

			_loaded = true;

			try {
				if (!File.Exists(Path)) {
					return;
				}

				// Steam keys are worth money, so the queue is encrypted like the other secrets. A plain file from before
				// that is read as-is and written back encrypted.
				string stored = File.ReadAllText(Path);
				bool plain = Secrets.IsPlain(stored) && stored.TrimStart().StartsWith('[');
				List<Entry>? saved = JsonSerializer.Deserialize<List<Entry>>(plain ? stored : Secrets.Unprotect(stored));

				if (saved != null) {
					lock (Gate) {
						Pending.AddRange(saved);
					}
				}

				if (plain && Secrets.Available && !SelfUpdate.OnTrial) {
					Save();
				}
			} catch (Exception e) {
				_loadFailed = true;
				// Not the error text: for an encrypted file it's the file itself, a screen of gibberish.
				Log.Warn(new Said("key queue unreadable (newer version or damaged) - left as is"));
				Log.Debug(new Said("key queue: {0}", Log.Describe(e)));
			}
		}
	}

	/// <summary>Write the queue out now, for a shutdown - a no-op until it's been read, so an unread queue never empties the file.</summary>
	public static void Flush() {
		if (_loaded) {
			Save();
		}
	}

	/// <summary>
	/// The queue on disk is plain text now - left so by a version from before it was encrypted - or there's none yet. Only
	/// then is it written plain while an update is on trial: an encrypted one written plain sat in the clear in every backup
	/// and settings copy made until the trial was over, and the version before reads it encrypted anyway. One that wasn't
	/// there, written encrypted, was a queue the version before might not read if the trial failed.
	/// </summary>
	private static bool PlainOnDisk() {
		try {
			string stored = File.Exists(Path) ? File.ReadAllText(Path) : "";

			return (stored.Trim().Length == 0) || (Secrets.IsPlain(stored) && stored.TrimStart().StartsWith('['));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;   // encrypted, then - never plain on a guess
		}
	}

	public static void Save() {
		if (ConfigStore.RestoreWriting) {
			return;   // see ConfigStore.RestoreWriting: the restored queue is about to be read in
		}

		if (_loadFailed) {
			// Said once per run, not on every key the worker touches.
			if (!_saveRefusedSaid) {
				_saveRefusedSaid = true;
				Log.Warn(new Said("key queue changes aren't saved - fix or move keys.json, restart"));
			}

			return;
		}

		try {
			lock (SaveGate) {
				string json;

				// Serialized under Gate too: the entries themselves change (a try counted, an account pinned), not just the list.
				lock (Gate) {
					json = JsonSerializer.Serialize(Pending);
				}

				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
				AtomicFile.Write(Path, SelfUpdate.OnTrial && PlainOnDisk() ? json : Secrets.Protect(json, "keys"));   // see SelfUpdate.OnTrial
			}
		} catch (Exception e) {
			Log.Warn(new Said("couldn't save the key queue: {0}", Log.Scrub(e.Message)));
		}
	}
}
