using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocatFarm.Addons.Rep4Rep;

/// <summary>
/// One account's commenting record - everything that MUST survive a restart. The property names are the ones nocat.farm
/// 1.8.x wrote to config/state/rep4rep-&lt;account&gt;.json, so the app's move of those files into this add-on's state
/// (Config/MigrateRep4Rep.cs) reads straight in.
/// </summary>
public sealed class AccountState {
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

	internal readonly Lock Sync = new();

	public void RecordPost(string taskId, DateTime at) {
		lock (Sync) {
			Posts.Add(at.Ticks);
			PostedTasks[taskId] = at.Ticks;
		}
	}

	public void MarkDeadTarget(ulong steamId, DateTime until) {
		lock (Sync) {
			DeadTargets[steamId.ToString()] = until.Ticks;
		}
	}

	public void ClearDeadTargets() {
		lock (Sync) {
			DeadTargets.Clear();
		}
	}

	/// <summary>Wipe what a hold should leave behind. The learned cap and the comments of the last 24h stay - see 1.8.x's notes.</summary>
	public void ResetForFreshStart() {
		lock (Sync) {
			DeadTargets.Clear();
			Strikes = 0;
			BlockedUntil = 0;
			BlockReason = "";
		}
	}

	public bool HasPostedTask(string taskId) {
		lock (Sync) {
			return PostedTasks.ContainsKey(taskId);
		}
	}

	public int PostsInLast24h(DateTime now) {
		long cutoff = now.AddHours(-24).Ticks;

		lock (Sync) {
			return Posts.Count(t => t >= cutoff);
		}
	}

	public DateTime? LastPost() {
		lock (Sync) {
			return Posts.Count == 0 ? null : new DateTime(Posts.Max(), DateTimeKind.Utc);
		}
	}

	/// <summary>When the account can next post under <paramref name="cap"/>, or null if there is room now (a ROLLING 24h).</summary>
	public DateTime? NextSlotAt(int cap, DateTime now) {
		cap = Math.Max(1, cap);
		long cutoff = now.AddHours(-24).Ticks;

		lock (Sync) {
			List<long> inWindow = [.. Posts.Where(t => t >= cutoff).Order()];

			return inWindow.Count < cap ? null : new DateTime(inWindow[inWindow.Count - cap], DateTimeKind.Utc).AddHours(24);
		}
	}

	public bool IsBlocked(DateTime now) => BlockedUntil > now.Ticks;

	public bool IsDeadTarget(ulong steamId, DateTime now) {
		lock (Sync) {
			return DeadTargets.TryGetValue(steamId.ToString(), out long until) && (until > now.Ticks);
		}
	}

	/// <summary>Drop what's too old to matter: posts past 24h, tasks past 30 days, dead targets that are alive again.</summary>
	public void Prune(DateTime now) {
		lock (Sync) {
			long day = now.AddHours(-24).Ticks, month = now.AddDays(-30).Ticks;
			Posts.RemoveAll(t => t < day);

			foreach (string k in PostedTasks.Where(kv => kv.Value < month).Select(static kv => kv.Key).ToArray()) {
				PostedTasks.Remove(k);
			}

			foreach (string k in DeadTargets.Where(kv => kv.Value <= now.Ticks).Select(static kv => kv.Key).ToArray()) {
				DeadTargets.Remove(k);
			}
		}
	}
}

/// <summary>Everything the add-on keeps, saved as one file through the host (config/plugins/rep4rep.json).</summary>
public sealed class AddonState {
	public Dictionary<string, AccountState> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>A hold on every account's commenting: until when (UTC), and when it began.</summary>
	public DateTime? HoldUntil { get; set; }

	public DateTime? HoldFrom { get; set; }

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

	/// <summary>Read what was saved. Null text is a first run (empty state); text that can't be read is null - hold, never guess.</summary>
	public static AddonState? Read(string? text) {
		if (string.IsNullOrWhiteSpace(text)) {
			return new AddonState();
		}

		try {
			AddonState? read = JsonSerializer.Deserialize<AddonState>(text, Json);

			if (read == null) {
				return null;
			}

			// Case-insensitive names, as the app has them.
			read.Accounts = new Dictionary<string, AccountState>(read.Accounts ?? [], StringComparer.OrdinalIgnoreCase);

			return read;
		} catch (JsonException) {
			return null;
		}
	}

	public AccountState For(string account) {
		lock (Accounts) {
			if (!Accounts.TryGetValue(account, out AccountState? s)) {
				Accounts[account] = s = new AccountState();
			}

			return s;
		}
	}

	public string Write(DateTime now) {
		lock (Accounts) {
			foreach (AccountState s in Accounts.Values) {
				s.Prune(now);
			}

			Dictionary<string, AccountState> copy = new(Accounts, StringComparer.OrdinalIgnoreCase);
			List<(AccountState S, Lock L)> locks = [.. copy.Values.Select(static s => (s, s.Sync))];

			foreach ((_, Lock l) in locks) {
				l.Enter();
			}

			try {
				return JsonSerializer.Serialize(new AddonState { Accounts = copy, HoldUntil = HoldUntil, HoldFrom = HoldFrom }, Json);
			} finally {
				foreach ((_, Lock l) in locks) {
					l.Exit();
				}
			}
		}
	}
}

/// <summary>
/// Comments not to post (GitHub issue VisaHolder/nocatfarm#1): a task whose comment has one of these words or phrases, or a
/// link, is skipped - no comment goes out, and rep4rep hands out another. Whole words, any case: "add" catches "add me +rep",
/// not "address".
/// </summary>
public static partial class BlockList {
	/// <summary>What a fresh install skips: comments that ask for something or advertise.</summary>
	public const string Default = "add me, add, free, trade, gift, check my profile";

	[GeneratedRegex(@"https?://|www\.|\b[a-z0-9-]+\.(?:com|net|org|gg|io|ru|me|tv|ly|xyz|co|info|biz|link|site|shop|store|app)\b|steamcommunity\b|\bs\.team\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex LinkRx();

	/// <summary>The words and phrases of a setting's text: comma or new-line separated, trimmed, empties dropped.</summary>
	public static IReadOnlyList<string> Parse(string? setting) =>
		[.. (setting ?? "").Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>Why this comment is skipped - the word that matched, or "a link" - or null when it may be posted.</summary>
	public static string? Blocks(string comment, IReadOnlyList<string> words, bool links) {
		if (links && LinkRx().IsMatch(comment)) {
			return "link";
		}

		foreach (string word in words) {
			// \b only where the phrase starts or ends with a letter or digit: "+rep" or "<3" still match as written.
			string start = char.IsLetterOrDigit(word[0]) ? @"\b" : "";
			string end = char.IsLetterOrDigit(word[^1]) ? @"\b" : "";
			string pattern = start + string.Join(@"\s+", word.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) + end;

			if (Regex.IsMatch(comment, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) {
				return word;
			}
		}

		return null;
	}
}
