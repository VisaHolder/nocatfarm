using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm.Plugins;

/// <summary>
/// 1.9.0: the one thing a plugin can make an account do on Steam - post a profile comment - and the rules the app keeps
/// over it, whatever the plugin asks (<see cref="IPluginHost.PostProfileCommentAsync"/>).
/// </summary>
/// <remarks>
/// rep4rep used to live inside the app and kept its own pacing. As an add-on it can only ask; everything that keeps an
/// account out of trouble is here, so no plugin - the official one or somebody else's - can make an account spam:
///
///   • online, not paused, not switched off, not signing off
///   • human mode: only while the account is up and settled in (<see cref="HumanGate"/> - asleep, on a break offline,
///     you playing on it, just signed in or woken: no), and a person's gap since its last comment
///   • without human mode, still never two comments closer than <see cref="PlainGapMinutes"/>
///   • at most <see cref="DailyLimit"/> in any rolling 24 hours per account, across every plugin, counted on disk so a
///     restart can't reset it - and a count that can't be read stops comments rather than assuming none
///   • one at a time per account; the profile is looked at first, and with no comment box nothing is sent
/// </remarks>
public static partial class Comments {
	/// <summary>The most comments one account posts through plugins in any 24 hours. The old rep4rep setting's own ceiling.</summary>
	public const int DailyLimit = 25;

	/// <summary>A human-mode account's shortest gap between two comments, in minutes (what rep4rep kept in human mode).</summary>
	public const int HumanGapMinutes = 35;

	/// <summary>The shortest gap between two comments from one account without human mode, in minutes.</summary>
	public const int PlainGapMinutes = 2;

	/// <summary>After this many profiles in a row with no comment box, the next is posted to anyway - see <see cref="Account.ClosedRun"/>.</summary>
	internal const int ClosedRunToDoubt = 5;

	/// <summary>A comment Steam would take: 1 to 1000 characters.</summary>
	internal const int MaxLength = 1000;

	/// <summary>The lowest SteamID64 of an individual account (universe public, type individual, instance desktop).</summary>
	private const ulong FirstIndividual = 76561197960265728UL;

	/// <summary>One account's side of it: the 24h record, the one-at-a-time lock and the human-mode gate.</summary>
	internal sealed class Account {
		public readonly SemaphoreSlim Posting = new(1, 1);
		public readonly Lock Gate = new();
		public List<long>? Posts;
		public bool Unreadable;

		/// <summary>The last write of the count failed: no comments until one succeeds (it's tried again at each look).</summary>
		public bool Unsaved;

		/// <summary>
		/// The human-mode gate, the account object it was made for, and whether that one was in human mode then. A gate reads
		/// the Bot it was made with, so it's made again when the account is a new object (a backup restored, the account
		/// removed and added again) or its human mode changed - kept, it read the old account: comments stuck, or human mode's
		/// checks skipped on an account that had it on.
		/// </summary>
		public HumanGate? Human;
		public Bot? HumanFor;
		public bool HumanLegit;

		/// <summary>
		/// Profiles in a row that showed no box. An account Steam won't let comment at all may see no box anywhere, and
		/// skipping those without a word would skip forever - so after a few, one is posted to and its answer settles it.
		/// </summary>
		public int ClosedRun;
	}

	private static readonly Dictionary<string, Account> Accounts = new(StringComparer.OrdinalIgnoreCase);

	internal static Account Of(string bot) {
		lock (Accounts) {
			if (!Accounts.TryGetValue(bot, out Account? a)) {
				Accounts[bot] = a = new Account();
			}

			return a;
		}
	}

	/// <summary>Forget what is in memory, so the next look reads the files again - after a backup is restored, and for the checks.</summary>
	internal static void Forget() {
		lock (Accounts) {
			Accounts.Clear();
		}

		_inUse = default;
	}

	internal static string PathFor(string bot) => Path.Combine(ConfigStore.ConfigDir, "state", $"comments-{bot}.json");

	private static (DateTime At, string Dir, bool Used) _inUse;

	/// <summary>A loaded plugin has asked about commenting or posted a comment this run (see <see cref="PluginComments"/>).</summary>
	private static volatile bool _pluginComments;

	/// <summary>How long after its last comment an install still counts as one that comments.</summary>
	internal static readonly TimeSpan InUseFor = TimeSpan.FromDays(7);

	/// <summary>Called by the plugin API whenever a plugin asks whether an account may comment, or posts one.</summary>
	internal static void PluginComments() => _pluginComments = true;

	/// <summary>
	/// Whether this install posts comments at all (through an add-on - rep4rep's): a plugin that comments is loaded (the
	/// rep4rep add-on, or any that has asked about commenting this run), or an account posted one in the last 7 days (its
	/// count on disk was written then). What the reports and the dashboard show comment figures by - what 1.8.x's "Use
	/// rep4rep" did. An add-on removed a week ago and an old count file no longer count. The files are looked at once a minute.
	/// </summary>
	public static bool InUse {
		get {
			if (_pluginComments || PluginHost.Running.Any(static p => string.Equals(p.Name, ConfigStore.Rep4RepPlugin, StringComparison.OrdinalIgnoreCase))) {
				return true;
			}

			string dir = Path.Combine(ConfigStore.ConfigDir, "state");

			if ((DateTime.UtcNow - _inUse.At < TimeSpan.FromMinutes(1)) && (_inUse.Dir == dir)) {
				return _inUse.Used;
			}

			bool used;

			try {
				DateTime since = DateTime.UtcNow - InUseFor;
				used = Directory.Exists(dir) && Directory.EnumerateFiles(dir, "comments-*.json").Any(f => File.GetLastWriteTimeUtc(f) >= since);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				used = false;
			}

			_inUse = (DateTime.UtcNow, dir, used);

			return used;
		}
	}

	private sealed class Ledger {
		public List<long> Posts { get; set; } = [];
	}

	/// <summary>The account's comments of the last 24 hours, read from disk the first time. Null: the file is there and unreadable.</summary>
	private static List<long>? Posts(string bot, Account a) {
		lock (a.Gate) {
			if (a.Posts != null) {
				return a.Posts;
			}

			string path = PathFor(bot);

			try {
				a.Posts = File.Exists(path) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))?.Posts ?? [] : [];
				a.Unreadable = false;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				if (!a.Unreadable) {
					Log.Warn(new Said("can't read {0} ({1}) - no comments until it's fixed", Path.GetFileName(path), Log.Describe(e)), bot);
				}

				a.Unreadable = true;

				return null;
			}

			return a.Posts;
		}
	}

	/// <summary>Count one comment, and write the 24 hours down straight away - the count is what a restart must not lose.</summary>
	/// <remarks>
	/// Written while the lock is held: snapshot first and written after, two counts at once could land in the wrong order -
	/// the older one, missing the newest comment, last - and after a restart the count was short.
	/// </remarks>
	internal static void Count(string bot, DateTime at) {
		Account a = Of(bot);

		lock (a.Gate) {
			List<long> posts = Posts(bot, a) ?? (a.Posts = []);
			posts.Add(at.Ticks);
			long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
			posts.RemoveAll(t => t < cutoff);
			Save(bot, a, posts);
		}
	}

	/// <summary>
	/// Write the count down; called under the account's lock. A write that fails stops comments from the account until one
	/// succeeds - a count only in memory is gone at the next restart, and the account starts again from zero over its limit.
	/// </summary>
	private static bool Save(string bot, Account a, List<long> posts) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(PathFor(bot))!);
			AtomicFile.Write(PathFor(bot), JsonSerializer.Serialize(new Ledger { Posts = [.. posts] }));

			if (a.Unsaved) {
				Log.Info(new Said("commenting state saved again - comments can go on"), bot);
			}

			a.Unsaved = false;

			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			if (!a.Unsaved) {
				Log.Warn(new Said("couldn't save commenting state: {0} - no comments until it can be saved", Log.Describe(e)), bot);
			}

			a.Unsaved = true;

			return false;
		}
	}

	/// <summary>Whether the account's count is on disk - written again here when the last write failed.</summary>
	private static bool Saved(string bot) {
		Account a = Of(bot);

		lock (a.Gate) {
			return !a.Unsaved || ((a.Posts is { } posts) && Save(bot, a, posts));
		}
	}

	/// <summary>Comments in the last 24 hours, and the newest - -1 when the count can't be read.</summary>
	internal static (int Count, DateTime? Last) Window(string bot) {
		Account a = Of(bot);

		lock (a.Gate) {
			if (Posts(bot, a) is not { } posts) {
				return (-1, null);
			}

			long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
			List<long> inWindow = [.. posts.Where(t => t >= cutoff).Order()];

			return (inWindow.Count, inWindow.Count > 0 ? new DateTime(inWindow[^1], DateTimeKind.Utc) : null);
		}
	}

	/// <summary>When the oldest comment that keeps the account at the limit ages out of the 24 hours.</summary>
	private static DateTime? LimitFrees(string bot) {
		Account a = Of(bot);

		lock (a.Gate) {
			if (Posts(bot, a) is not { } posts) {
				return null;
			}

			long cutoff = DateTime.UtcNow.AddHours(-24).Ticks;
			List<long> inWindow = [.. posts.Where(t => t >= cutoff).Order()];

			return inWindow.Count < DailyLimit ? null : new DateTime(inWindow[inWindow.Count - DailyLimit], DateTimeKind.Utc).AddHours(24);
		}
	}

	/// <summary>
	/// Every rule but the profile itself, for <see cref="IPluginHost.CanComment"/> and again right before a post. The human
	/// gate is asked as the loop of a module would ask it, so a wait that hasn't started starts now.
	/// </summary>
	internal static PluginCommentCheck Check(Bot? bot) {
		if (bot == null) {
			return new PluginCommentCheck(false, new Said("there's no account by that name").ToString(), null, 0, DailyLimit);
		}

		(int posted, DateTime? last) = Window(bot.Name);
		DateTime now = DateTime.UtcNow;

		PluginCommentCheck No(Said why, DateTime? retry) => new(false, why.ToString(), retry, Math.Max(0, posted), DailyLimit);

		if (!bot.Cfg.Enabled || bot.Paused) {
			return No(new Said("it's paused or switched off"), null);
		}

		if (bot.Stopping) {
			return No(new Said("it's signing off"), null);
		}

		if (!bot.IsOnline) {
			return No(new Said("it isn't signed in"), now.AddMinutes(2));
		}

		if (posted < 0) {
			return No(new Said("can't read this account's comment count, so it won't post"), now.AddMinutes(10));
		}

		if (!Saved(bot.Name)) {
			return No(new Said("can't save this account's comment count, so it won't post"), now.AddMinutes(10));
		}

		if (posted >= DailyLimit) {
			DateTime? frees = LimitFrees(bot.Name);

			return No(new Said("at most {0} comments from one account in 24 hours - the next can go from {1}", DailyLimit,
				(Func<string>) (() => Fmt.Clock(frees ?? now.AddHours(1)))), frees);
		}

		if (last is { } lastPost) {
			bool human = bot.Cfg.LegitMode;
			DateTime next = lastPost.AddMinutes(human ? HumanGapMinutes : PlainGapMinutes);

			if (now < next) {
				return No(human
					? new Said("human mode spaces comments out - this account can post the next one from {0}", Fmt.Clock(next))
					: new Said("one comment every {0} minutes at most from one account - the next can go from {1}", PlainGapMinutes, Fmt.Clock(next)), next);
			}
		}

		Account a = Of(bot.Name);
		HumanGate gate;

		lock (a.Gate) {
			if ((a.Human == null) || !ReferenceEquals(a.HumanFor, bot) || (a.HumanLegit != bot.Cfg.LegitMode)) {
				a.Human = new HumanGate(bot);
				a.HumanFor = bot;
				a.HumanLegit = bot.Cfg.LegitMode;
			}

			gate = a.Human;
		}

		if (!gate.Open) {
			HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);
			DateTime? retry = (human?.Current is HumanMode.Phase.Asleep or HumanMode.Phase.NightIdle) && (human.NextChange is { } wakes) && (wakes > now)
				? wakes
				: now.AddMinutes(5);

			Said why = gate.Waiting();

			return No(why.IsEmpty ? new Said("waiting for the account's day") : why, retry);
		}

		return new PluginCommentCheck(true, "", null, posted, DailyLimit);
	}

	/// <summary>Whether a SteamID64 is somebody's profile this account may comment on: an individual account, and not its own.</summary>
	internal static bool IsTarget(ulong target, ulong self) =>
		(target >= FirstIndividual) && (target < FirstIndividual + 0x1_0000_0000UL) && (target != self);

	/// <summary>Text Steam would take as a comment: not blank, not over the limit, no control characters but line breaks.</summary>
	internal static bool IsText(string? text) =>
		!string.IsNullOrWhiteSpace(text) && (text.Length <= MaxLength) && !text.Any(static c => char.IsControl(c) && (c is not ('\n' or '\r' or '\t')));

	/// <summary>The whole post, rules first - see the class remarks.</summary>
	internal static async Task<PluginCommentResult> PostAsync(Bot? bot, string plugin, ulong target, string text, CancellationToken ct) {
		PluginCommentCheck check = Check(bot);

		if (!check.Allowed || (bot == null)) {
			return new PluginCommentResult(PluginCommentOutcome.NotNow, check.Reason, check.RetryAt);
		}

		if (!IsTarget(target, bot.SteamId)) {
			return new PluginCommentResult(PluginCommentOutcome.NotNow, new Said("that isn't a profile it can comment on").ToString());
		}

		if (!IsText(text)) {
			return new PluginCommentResult(PluginCommentOutcome.NotNow, new Said("the comment is empty or too long").ToString());
		}

		Account a = Of(bot.Name);

		// Told rather than queued: a comment that goes out minutes after it was asked for, into whatever the count is by then,
		// isn't what was asked for.
		if (!await a.Posting.WaitAsync(0, ct).ConfigureAwait(false)) {
			return new PluginCommentResult(PluginCommentOutcome.NotNow, new Said("another comment from this account is going out right now").ToString(), DateTime.UtcNow.AddMinutes(2));
		}

		try {
			// Again, holding the lock: the first look was before another comment from this account could have gone out.
			check = Check(bot);

			if (!check.Allowed) {
				return new PluginCommentResult(PluginCommentOutcome.NotNow, check.Reason, check.RetryAt);
			}

			if (!bot.Web.Ready && !await bot.Web.RefreshAsync(false, ct).ConfigureAwait(false)) {
				return new PluginCommentResult(PluginCommentOutcome.NotSent, new Said("no Steam web session").ToString());
			}

			// Look at the profile first, as a person would. No box for this account means Steam would only refuse it.
			if (await ClosedAsync(bot, a, target, ct).ConfigureAwait(false)) {
				return new PluginCommentResult(PluginCommentOutcome.CommentsClosed);
			}

			// Stopped while it looked - paused, asleep, signing off: nothing goes out.
			check = Check(bot);

			if (!check.Allowed) {
				return new PluginCommentResult(PluginCommentOutcome.NotNow, check.Reason, check.RetryAt);
			}

			(PluginCommentOutcome outcome, string? error) = await SendAsync(bot, target, text, ct).ConfigureAwait(false);

			if (outcome is PluginCommentOutcome.Posted or PluginCommentOutcome.Unknown) {
				// Unknown counts too: Steam may well have posted it, and an uncounted live comment is how an account ends up past its limit.
				Count(bot.Name, DateTime.UtcNow);
				Stats.Record(Stats.KindComment, bot.Name);
				a.ClosedRun = 0;
			} else if (outcome != PluginCommentOutcome.NotSent) {
				a.ClosedRun = 0;
			}

			Log.Debug($"plugin {plugin}: comment on {target} - {outcome}{(string.IsNullOrEmpty(error) ? "" : $" ({Log.Scrub(error)})")}", bot.Name);

			return new PluginCommentResult(outcome, error ?? "");
		} finally {
			a.Posting.Release();
		}
	}

	// ── is the comment box there? ───────────────────────────────────────────
	/// <summary>What a target's profile page, as this account sees it, says about leaving a comment on it.</summary>
	internal enum CommentBox { Open, Closed, Unreadable }

	/// <summary>Read a profile page for the comment entry box.</summary>
	/// <remarks>
	/// Steam builds the page for whoever is looking at it. The comment thread's entry - "commentthread_entry", holding the
	/// textarea "commentthread_Profile_&lt;steamid&gt;_textarea" - is only on it when that viewer may post: comments open to
	/// all, or friends-only and the viewer is a friend. Private comments, a private profile, or friends-only to a stranger
	/// show the comments (if any) and no box.
	///
	/// A signed-out page shows nobody the box, so its absence only counts on a page signed in as THIS account (g_steamID)
	/// about THIS target (g_rgProfileData). Anything else - an error page, a sign-in page, someone else's profile - is
	/// Unreadable, and the comment goes ahead.
	/// </remarks>
	internal static CommentBox ReadCommentBox(string? page, ulong target, ulong viewer) {
		if (string.IsNullOrEmpty(page)) {
			return CommentBox.Unreadable;
		}

		Match owner = ProfileOwnerRx().Match(page);

		if (!owner.Success || (owner.Groups[1].Value != target.ToString(System.Globalization.CultureInfo.InvariantCulture))) {
			return CommentBox.Unreadable;
		}

		Match who = ViewerRx().Match(page);

		if (!who.Success || ((viewer != 0) && (who.Groups[1].Value != viewer.ToString(System.Globalization.CultureInfo.InvariantCulture)))) {
			return CommentBox.Unreadable;   // signed out, or another session - says nothing about this account
		}

		return page.Contains($"commentthread_Profile_{target}_textarea", StringComparison.Ordinal)
			|| page.Contains("class=\"commentthread_textarea\"", StringComparison.Ordinal)
			? CommentBox.Open
			: CommentBox.Closed;
	}

	[GeneratedRegex("""g_rgProfileData\s*=\s*\{[^\n]*?"steamid"\s*:\s*"(\d+)"(?#owner)""", RegexOptions.CultureInvariant)]
	private static partial Regex ProfileOwnerRx();

	[GeneratedRegex("""g_steamID\s*=\s*"(\d+)"(?#viewer)""", RegexOptions.CultureInvariant)]
	private static partial Regex ViewerRx();

	/// <summary>Whether a run of closed profiles has gone on long enough that the next one is tried anyway.</summary>
	internal static bool Doubts(int closedRun) => closedRun >= ClosedRunToDoubt;

	private static async Task<bool> ClosedAsync(Bot bot, Account a, ulong target, CancellationToken ct) {
		string? page;

		try {
			page = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{target}/"), ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"couldn't look at {target}'s profile first: {Log.Describe(e)}", bot.Name);
			page = null;
		}

		CommentBox box = ReadCommentBox(page, target, bot.SteamId);

		if ((box == CommentBox.Closed) && Doubts(a.ClosedRun)) {
			Log.Debug($"{a.ClosedRun} profiles in a row showed no comment box - posting on {target} anyway, to see whether it's this account", bot.Name);
			box = CommentBox.Open;
		}

		if (box == CommentBox.Closed) {
			a.ClosedRun++;

			return true;
		}

		if (box == CommentBox.Unreadable) {
			Log.Debug($"couldn't read {target}'s profile ({(page == null ? "no page" : WebSession.FailureText(page))}) - commenting anyway", bot.Name);
		}

		// The few seconds a person takes between the page loading and the comment going in.
		await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(4, 13)), ct).ConfigureAwait(false);

		return false;
	}

	// ── posting ─────────────────────────────────────────────────────────────
	private static async Task<(PluginCommentOutcome Outcome, string? Error)> SendAsync(Bot bot, ulong target, string text, CancellationToken ct) {
		Uri url = new(WebSession.Community, $"/comment/Profile/post/{target}/-1/");
		Uri referer = new(WebSession.Community, $"/profiles/{target}");

		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["comment"] = text,
			["count"] = "10",
			["feature2"] = "-1"
			// sessionid is injected by WebSession from the cookie jar
		};

		string? body;
		bool sent;

		try {
			(body, sent) = await bot.Web.PostTrackedAsync(url, form, referer, ct).ConfigureAwait(false);
		} catch (Exception e) {
			// Including a shutdown mid-request. Steam may well have taken the comment.
			if (!ct.IsCancellationRequested) {
				Log.Failed($"posting the comment on {target}", e, bot.Name);
			}

			return (PluginCommentOutcome.Unknown, null);
		}

		return Read(body, sent);
	}

	/// <summary>What Steam's answer to a comment post means. <paramref name="sent"/>: the request left this PC.</summary>
	internal static (PluginCommentOutcome Outcome, string? Error) Read(string? body, bool sent) {
		if (body == null) {
			// Never sent at all - no session, no cookie: nothing was posted. Sent and no reply: Steam might still have posted it.
			return (sent ? PluginCommentOutcome.Unknown : PluginCommentOutcome.NotSent, null);
		}

		if (body.Contains("\"success\":true", StringComparison.Ordinal)) {
			return (PluginCommentOutcome.Posted, null);
		}

		if (!body.Contains("\"success\":false", StringComparison.Ordinal)) {
			return (PluginCommentOutcome.Unknown, null);   // unrecognised body - unknown, NOT a refusal
		}

		string? error = ReadJsonString(body, "error");

		return (Classify(error), error);
	}

	/// <summary>
	/// What Steam meant, matched on distinctive keywords rather than exact sentences - Steam rewords these.
	/// </summary>
	internal static PluginCommentOutcome Classify(string? error) {
		if (string.IsNullOrEmpty(error)) {
			return PluginCommentOutcome.Refused;
		}

		string e = error.ToLowerInvariant();

		// Steam's NON-FRIEND daily ceiling (about 10 in 24h) is a different message from the "posting too fast" one, and
		// checked first: it means "done for the day". "not friends" alone would also catch a friends-only refusal.
		if (e.Contains("maximum number of comments", StringComparison.Ordinal)
			|| (e.Contains("exceeded", StringComparison.Ordinal) && e.Contains("comment", StringComparison.Ordinal))) {
			return PluginCommentOutcome.DailyLimit;
		}

		if (e.Contains("too frequently", StringComparison.Ordinal) || e.Contains("rate limit", StringComparison.Ordinal)
			|| e.Contains("try again later", StringComparison.Ordinal) || e.Contains("too many", StringComparison.Ordinal)) {
			return PluginCommentOutcome.RateLimited;
		}

		if (e.Contains("blocked", StringComparison.Ordinal) || e.Contains("not allowed", StringComparison.Ordinal)
			|| e.Contains("restricted", StringComparison.Ordinal) || e.Contains("limited account", StringComparison.Ordinal)
			|| e.Contains("banned", StringComparison.Ordinal)) {
			return PluginCommentOutcome.AccountBlocked;
		}

		return PluginCommentOutcome.Refused;   // private / friends-only / comments closed -> just a dud target
	}

	private static string? ReadJsonString(string json, string key) {
		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out JsonElement v) && (v.ValueKind == JsonValueKind.String)
				? v.GetString()
				: null;
		} catch (JsonException) {
			return null;
		}
	}
}
