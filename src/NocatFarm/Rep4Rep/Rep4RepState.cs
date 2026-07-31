using System.Text.Json;
using NocatFarm.Config;

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
			Log.Warn($"can't read {Path.GetFileName(path)} ({e.Message}) - not commenting until it's readable, so the 24h limit stays honest", bot);

			return null;
		} finally {
			Gate.Release();
		}
	}

	public async Task SaveAsync(string bot) {
		long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
		long taskCutoff = DateTime.UtcNow.AddDays(-30).Ticks;
		long now = DateTime.UtcNow.Ticks;

		Posts.RemoveAll(t => t < cutoff);

		foreach (string k in PostedTasks.Where(kv => kv.Value < taskCutoff).Select(static kv => kv.Key).ToArray()) {
			PostedTasks.Remove(k);
		}

		foreach (string k in DeadTargets.Where(kv => kv.Value <= now).Select(static kv => kv.Key).ToArray()) {
			DeadTargets.Remove(k);
		}

		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			Directory.CreateDirectory(Dir);
			await File.WriteAllTextAsync(PathFor(bot), JsonSerializer.Serialize(this, Json)).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Warn($"couldn't save commenting state: {e.Message}", bot);
		} finally {
			Gate.Release();
		}
	}

	// ── rolling window ──────────────────────────────────────────────────────
	public int PostsInLast24h() {
		long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;

		return Posts.Count(t => t >= cutoff);
	}

	public DateTime? LastPost() {
		if (Posts.Count == 0) {
			return null;
		}

		return new DateTime(Posts.Max(), DateTimeKind.Utc);
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
		List<long> inWindow = Posts.Where(t => t >= cutoff).OrderBy(static t => t).ToList();

		if (inWindow.Count < cap) {
			return null;   // room now
		}

		return new DateTime(inWindow[inWindow.Count - cap], DateTimeKind.Utc).AddHours(24);
	}

	public bool IsBlocked => BlockedUntil > DateTime.UtcNow.Ticks;

	public bool IsDeadTarget(ulong steamId) =>
		DeadTargets.TryGetValue(steamId.ToString(), out long until) && (until > DateTime.UtcNow.Ticks);
}
