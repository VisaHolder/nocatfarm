using System.Text.Json;
using NocatFarm.Config;

using NocatFarm.Core;

namespace NocatFarm.Rep4Rep;

/// <summary>
/// Everything about an account's commenting that MUST survive a restart.
///
/// Without this, restarting resets the rolling 24h count to zero and the account can quietly sail past Steam's
/// comment ceiling - which is the one mistake here that gets an account comment-banned. A file that exists but
/// can't be read therefore stops commenting entirely rather than assuming zero: fail safe, not fail open.
/// </summary>
public sealed class Rep4RepState {
	/// <summary>UTC ticks of every comment posted, pruned to the last 24h on save.</summary>
	public List<long> Posts { get; set; } = [];

	/// <summary>UTC ticks until which this account sits out completely.</summary>
	public long BlockedUntil { get; set; }

	public string BlockReason { get; set; } = "";

	/// <summary>taskId -> when we commented for it. Stops a restart re-posting a task whose completion failed.</summary>
	public Dictionary<string, long> PostedTasks { get; set; } = [];

	/// <summary>target steamID -> skip until. Profiles that refuse comments (private, friends-only, closed).</summary>
	public Dictionary<string, long> DeadTargets { get; set; } = [];

	/// <summary>Consecutive DIFFERENT targets that refused. Three in a row means it's the account, not the profiles.</summary>
	public int Strikes { get; set; }

	/// <summary>This account's discovered daily ceiling, once Steam has told us what it is.</summary>
	public int Cap { get; set; }

	public bool CapLearned { get; set; }

	// Guards the in-memory collections (Posts / PostedTasks / DeadTargets) so the background commenting loop and a
	// dashboard/console action (post now, clear, rest) can't touch them at the same time - which otherwise throws
	// "collection modified" mid-enumeration or tears the 24h count the cap depends on.
	private readonly object _sync = new();

	/// <summary>Record a posted comment - into the rolling window and the task history - atomically.</summary>
	public void RecordPost(string taskId) {
		lock (_sync) {
			long now = DateTime.UtcNow.Ticks;
			Posts.Add(now);
			PostedTasks[taskId] = now;
		}
	}

	/// <summary>Empty the rolling window for a clean baseline (used by 'rep4rep rest').</summary>
	public void ClearWindow() {
		lock (_sync) {
			Posts.Clear();
		}
	}

	public void MarkDeadTarget(ulong steamId, DateTime until) {
		lock (_sync) {
			DeadTargets[steamId.ToString()] = until.Ticks;
		}
	}

	public void ClearDeadTargets() {
		lock (_sync) {
			DeadTargets.Clear();
		}
	}

	/// <summary>Wipe everything a hold should leave behind, so commenting restarts from nothing.</summary>
	/// <remarks>
	/// The rolling window would empty itself over a hold of a day or more, but the rest of it would not: a
	/// strike count, a block, and a list of dead targets would all still be sitting there when commenting
	/// came back, so "fresh" would have meant "fresh except for every reason it had to hold off".
	///
	/// The LEARNED cap is deliberately kept. It is not our state - it is a ceiling Steam handed us, and
	/// forgetting it means re-discovering it the only way there is, by having a comment refused. Everything
	/// else here is ours and goes.
	/// </remarks>
	public void ResetForFreshStart() {
		lock (_sync) {
			Posts.Clear();
			PostedTasks.Clear();
			DeadTargets.Clear();
			Strikes = 0;
			BlockedUntil = 0;
			BlockReason = "";
		}
	}

	public bool HasPostedTask(string taskId) {
		lock (_sync) {
			return PostedTasks.ContainsKey(taskId);
		}
	}

	// ── storage ─────────────────────────────────────────────────────────────
	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	private static string Dir => Path.Combine(ConfigStore.Root, "config", "state");

	private static string PathFor(string bot) => Path.Combine(Dir, $"rep4rep-{bot}.json");

	/// <summary>Load an account's state. Null means the file exists but is unreadable - do NOT post in that case.</summary>
	public static async Task<Rep4RepState?> LoadAsync(string bot) {
		string path = PathFor(bot);

		if (!File.Exists(path)) {
			return new Rep4RepState();   // genuine first run: starting at zero is correct
		}

		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			string body = await File.ReadAllTextAsync(path).ConfigureAwait(false);

			return JsonSerializer.Deserialize<Rep4RepState>(body, Json) ?? new Rep4RepState();
		} catch (Exception e) {
			Log.Warn(new Said("can't read {0} ({1}) - not commenting until it's readable, so the 24h limit stays honest", Path.GetFileName(path), e.Message), bot);

			return null;
		} finally {
			Gate.Release();
		}
	}

	public async Task SaveAsync(string bot) {
		long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
		long taskCutoff = DateTime.UtcNow.AddDays(-30).Ticks;
		long now = DateTime.UtcNow.Ticks;

		string body;

		lock (_sync) {
			Posts.RemoveAll(t => t < cutoff);

			foreach (string k in PostedTasks.Where(kv => kv.Value < taskCutoff).Select(static kv => kv.Key).ToArray()) {
				PostedTasks.Remove(k);
			}

			foreach (string k in DeadTargets.Where(kv => kv.Value <= now).Select(static kv => kv.Key).ToArray()) {
				DeadTargets.Remove(k);
			}

			body = JsonSerializer.Serialize(this, Json);   // snapshot under the lock, so nothing mutates mid-serialize
		}

		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			Directory.CreateDirectory(Dir);
			await AtomicFile.WriteAsync(PathFor(bot), body).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Warn(new Said("couldn't save commenting state: {0}", e.Message), bot);
		} finally {
			Gate.Release();
		}
	}

	// ── rolling window ──────────────────────────────────────────────────────
	public int PostsInLast24h() {
		long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;

		lock (_sync) {
			return Posts.Count(t => t >= cutoff);
		}
	}

	public DateTime? LastPost() {
		lock (_sync) {
			if (Posts.Count == 0) {
				return null;
			}

			return new DateTime(Posts.Max(), DateTimeKind.Utc);
		}
	}

	/// <summary>
	/// When the account can next post given <paramref name="cap"/>, or null if there is room right now.
	///
	/// This is a ROLLING 24h window, not a midnight reset: the count only drops as each comment ages past 24h.
	/// To get back under the cap, the oldest posts have to age out - the one that tips it is the (count - cap)th
	/// oldest, and it frees up exactly 24h after it went out. That is the soonest slot; the window keeps opening
	/// post-by-post after that.
	/// </summary>
	public DateTime? NextSlotAt(int cap) {
		cap = Math.Max(1, cap);
		long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;

		lock (_sync) {
			List<long> inWindow = Posts.Where(t => t >= cutoff).OrderBy(static t => t).ToList();

			if (inWindow.Count < cap) {
				return null;   // room now
			}

			return new DateTime(inWindow[inWindow.Count - cap], DateTimeKind.Utc).AddHours(24);
		}
	}

	public bool IsBlocked => BlockedUntil > DateTime.UtcNow.Ticks;

	public bool IsDeadTarget(ulong steamId) {
		lock (_sync) {
			return DeadTargets.TryGetValue(steamId.ToString(), out long until) && (until > DateTime.UtcNow.Ticks);
		}
	}
}
