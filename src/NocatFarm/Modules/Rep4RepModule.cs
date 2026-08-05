using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Rep4Rep;

namespace NocatFarm.Modules;

/// <summary>
/// Earns rep4rep points by posting the comments rep4rep assigns, on a human schedule, from every opted-in
/// account at once. One rep4rep account (a single API token) drives them all; each Steam account works its own
/// task queue and they all feed the same points pool.
///
/// Rapid templated comments on strangers' profiles are a real comment-ban vector, so the pacing is the feature:
///
///   • a hard ceiling per rolling 24h, counted across restarts - the state file is what makes it real
///   • a configurable minimum gap between one account's comments, jittered, never a burst
///   • a refused post gets ONE retry; still refused and that PROFILE is skipped for 26h and a different one is
///     tried. Only when three DIFFERENT profiles refuse in a row is the ACCOUNT treated as blocked - it then
///     sits out 24h while the other accounts carry on
///   • an UNKNOWN outcome is counted and never retried: Steam may well have posted it, and a retry would put a
///     second identical comment on the same profile
///   • a commenting window, because nobody leaves +rep on strangers at 4am
/// </summary>
public sealed class Rep4RepModule(Bot bot, Rep4RepApi api) : BotModule(bot) {
	private const int NoTaskRetryMinutes = 30;
	private const int RetryLowSeconds = 2 * 60;
	private const int RetryHighSeconds = 6 * 60;
	private const int CooldownSeconds = 24 * 3600;
	private const int DeadTargetSeconds = 26 * 3600;   // must outlast a 24h cooldown, or the dud is the first thing retried on wake
	private const int StrikesToBlock = 3;
	private const int RateLimitLowMinutes = 60;
	private const int RateLimitHighMinutes = 180;
	private const int CapFloor = 5;

	private enum Outcome { Posted, Refused, Unknown, RateLimited, AccountBlocked, DailyLimit }

	private readonly Rep4RepApi _api = api;

	private Rep4RepState? _state;
	private string? _profileId;
	private Said _status = new("off");
	private int _rateLimitRun;   // consecutive Steam rate-limits, reset on a good post

	public override string Name => "rep4rep";
	public override string Status => _status;

	/// <summary>Manual hold, set from the console or the web UI. Survives nothing - it's a "stop now" switch.</summary>
	public bool Paused { get; set; }

	public int PostsToday => _state?.PostsInLast24h() ?? 0;

	/// <summary>
	/// The number actually enforced. The configured cap is always an UPPER bound: a limit learned from Steam can
	/// only ever pull it down. Letting a learned value raise it would silently post more than the operator asked
	/// for, which is the exact failure that gets an account comment-banned.
	/// </summary>
	public int Cap {
		get {
			int configured = Math.Max(1, Bot.Cfg.Rep4RepDailyCap);
			int learned = _state?.Cap ?? 0;

			return learned > 0 ? Math.Min(configured, learned) : configured;
		}
	}
	public DateTime? LastPost => _state?.LastPost();

	/// <summary>When this account can next post, or null if it has room right now. The window frees up
	/// post-by-post as each comment ages past 24h, so this is "the soonest", not a fixed daily reset.</summary>
	public DateTime? NextSlot => _state?.NextSlotAt(Cap);

	/// <summary>The cap actually in force is a ceiling Steam handed us, lower than the number configured - so
	/// raising the configured cap will NOT lift it. This is what explains "I set 15 but it's stuck at 10".</summary>
	public bool CapIsSteamLimit => _state is { CapLearned: true, Cap: > 0 } && (_state.Cap < Math.Max(1, Bot.Cfg.Rep4RepDailyCap));

	/// <summary>Skip the wait and try a post right now. Never skips the daily cap.</summary>
	public void RunNow() {
		_forceNext = true;
		Wake();
	}

	private volatile bool _forceNext;

	/// <summary>
	/// Tripped to cut a wait short.
	///
	/// The loop can be part way through a thirty-minute sleep, so setting the flag alone left "post now" and
	/// "hold cleared" doing nothing visible for half an hour - which reads as broken, and is the opposite of
	/// what a command called "now" promises.
	/// </summary>
	private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private void Wake() => _wake.TrySetResult(true);

	/// <summary>Sleep, unless somebody asks for the next step sooner. True if the wait ran its course.</summary>
	private async Task<bool> SleepOrWake(TimeSpan wait, CancellationToken ct) {
		TaskCompletionSource<bool> woken = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_wake = woken;

		// Re-check after arming: a wake between the last step and this line would otherwise be missed.
		if (_forceNext) {
			return true;
		}

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

		Task delay = Task.Delay(wait, linked.Token);
		Task winner = await Task.WhenAny(woken.Task, delay).ConfigureAwait(false);

		await linked.CancelAsync().ConfigureAwait(false);

		return !ct.IsCancellationRequested;
	}

	// Commenting runs only when rep4rep is on globally AND for this account. The global master switch lets the
	// whole feature be turned off site-wide without touching every account.
	private bool CommentingOn => Live.Global.Rep4RepEnabled && Bot.Cfg.Rep4Rep;

	protected override async Task RunAsync(CancellationToken ct) {
		// The loop stays alive whether or not commenting is switched on, so flipping it on (or pasting the API
		// token) takes effect on its own instead of needing the account restarted.
		while (!ct.IsCancellationRequested && (!CommentingOn || !_api.HasToken)) {
			_status = !CommentingOn ? new Said("off") : new Said("waiting for the rep4rep API token");

			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}

		if (ct.IsCancellationRequested) {
			return;
		}

		_state = await Rep4RepState.LoadAsync(Bot.Name).ConfigureAwait(false);

		// Settle after login. Commenting in the same minute as the logon, every restart, is a pattern.
		//
		// Say so, too. The status was left reading "off" from the gate above for the whole of this wait - up to
		// ten minutes of a switched-ON account reporting the exact word it would use if the feature had been
		// switched off, which is indistinguishable from broken and sent me hunting a module that was fine.
		_status = new Said("settling in after signing in");

		if (!await Sleep(Rng.Seconds(90, 600), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (!CommentingOn) {
				_status = new Said("off");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			int waitSeconds;

			try {
				waitSeconds = await StepAsync(ct).ConfigureAwait(false);

				// The whole module can sit silent for a day when there is nothing to do, which is
				// indistinguishable from a module that has stopped running. One Debug line per pass says which.
				Log.Debug(new Said("rep4rep: {0} - next look in {1}", _status, Fmt.Hm(Math.Max(60, waitSeconds) / 60)), Bot.Name);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				// NEVER silent: a throwing step is indistinguishable from "nothing to do" - no post, no log, forever.
				Log.Warn(new Said("rep4rep step failed: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
				waitSeconds = 10 * 60;
			}

			if (!await SleepOrWake(TimeSpan.FromSeconds(Math.Max(60, waitSeconds)), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>One iteration. Returns how many seconds to wait before the next.</summary>
	private async Task<int> StepAsync(CancellationToken ct) {
		bool force = _forceNext;
		_forceNext = false;

		if (Paused || Bot.Paused) {
			_status = new Said("paused");

			return 5 * 60;
		}

		if (!Bot.IsOnline) {
			_status = new Said("offline");

			return 2 * 60;
		}

		// A state file we couldn't read means we don't know today's count. Retry the read; never guess zero.
		_state ??= await Rep4RepState.LoadAsync(Bot.Name).ConfigureAwait(false);

		if (_state == null) {
			_status = new Said("state unreadable - holding");

			return 10 * 60;
		}

		if (_state.IsBlocked) {
			TimeSpan left = new DateTime(_state.BlockedUntil, DateTimeKind.Utc) - DateTime.UtcNow;
			_status = new Said("resting {0} ({1})", Fmt.Hm((int) left.TotalMinutes), _state.BlockReason);

			return 10 * 60;
		}

		int posted = _state.PostsInLast24h();

		if (posted >= Cap) {
			_status = new Said("{0}/{1} today - done", posted, Cap);

			return 20 * 60;
		}

		if (!force) {
			int windowWait = WindowWaitSeconds();

			if (windowWait > 0) {
				_status = new Said("outside the window, opens in {0}", Fmt.Hm(windowWait / 60));

				return Math.Min(windowWait, 30 * 60);
			}

			// Spacing is enforced against the PERSISTED last post, not the loop timer: a restart resets the timer,
			// and without this two comments could land minutes apart across one.
			DateTime? last = _state.LastPost();

			if (last != null) {
				int sinceSeconds = (int) (DateTime.UtcNow - last.Value).TotalSeconds;
				int gapSeconds = Bot.Cfg.Rep4RepGapMinMinutes * 60;

				if (sinceSeconds < gapSeconds) {
					_status = new Said("{0}/{1} today - next in {2}", posted, Cap, Fmt.Hm((gapSeconds - sinceSeconds) / 60));

					return (gapSeconds - sinceSeconds) + Rng.Next(30, 180);
				}
			}
		}

		if (!Bot.Web.Ready && !await Bot.Web.RefreshAsync(false, ct).ConfigureAwait(false)) {
			_status = new Said("no Steam web session");

			return 5 * 60;
		}

		_profileId ??= await _api.ResolveProfileIdAsync(Bot.SteamId, Live.Global.Rep4RepAutoAddProfiles, ct).ConfigureAwait(false);

		if (_profileId == null) {
			_status = new Said("not registered on rep4rep");
			Log.Warn(Live.Global.Rep4RepAutoAddProfiles
				? new Said("rep4rep couldn't register this Steam account - check the API token is right; retrying in 30m")
				: new Said("this account isn't registered on rep4rep - add steamcommunity.com/profiles/{0} on rep4rep.com, or turn Rep4RepAutoAddProfiles on", Bot.SteamId), Bot.Name);

			return 30 * 60;
		}

		Rep4RepTask? task = await NextTaskAsync(_profileId, ct).ConfigureAwait(false);

		if (task == null) {
			_status = new Said("{0}/{1} today - no task available", posted, Cap);

			return NoTaskRetryMinutes * 60;
		}

		// Final cap check the instant before posting: the count above was taken before the window/gap/session and
		// task-fetch steps, and a dashboard "post now" could have spent a slot since. This is the one number that
		// gets an account comment-banned, so it's re-checked here too.
		if (_state.PostsInLast24h() >= Cap) {
			_status = new Said("{0}/{1} today - done", Cap, Cap);

			return 20 * 60;
		}

		_status = new Said("commenting on {0}", task.TargetName);
		(Outcome outcome, string? error) = await PostCommentAsync(task, ct).ConfigureAwait(false);

		switch (outcome) {
			case Outcome.RateLimited:
				return await OnRateLimitedAsync().ConfigureAwait(false);
			case Outcome.DailyLimit:
				return await OnDailyLimitAsync().ConfigureAwait(false);
			case Outcome.AccountBlocked:
				return await BlockAccountAsync("Steam refused: " + (error ?? "commenting blocked")).ConfigureAwait(false);
			case Outcome.Unknown:
				// Steam may well have posted it. A retry would put a SECOND identical comment on the same profile,
				// which is exactly the pattern being avoided - so count it, leave the task alone, carry on.
				await CountPostAsync(task).ConfigureAwait(false);
				Log.Warn(new Said("no reply from Steam for {0} - it may have posted, counting it (no retry)", task.TargetName), Bot.Name);

				return NextGapSeconds();
			case Outcome.Refused:
				return Bot.Cfg.Rep4RepRetryRefused
					? await RetryOnceAsync(task, error, ct).ConfigureAwait(false)
					: await OnTargetRefusedAsync(task).ConfigureAwait(false);
		}

		return await CreditAsync(task, ct).ConfigureAwait(false);
	}

	private async Task<int> RetryOnceAsync(Rep4RepTask task, string? error, CancellationToken ct) {
		int pause = Rng.Next(RetryLowSeconds, RetryHighSeconds + 1);
		Log.Warn(new Said("comment on {0} didn't go through{1} - trying once more in ~{2}", task.TargetName, (string.IsNullOrEmpty(error) ? "" : $" (\"{error}\")"), Fmt.Hm(Math.Max(1, pause / 60))), Bot.Name);

		if (!await Sleep(TimeSpan.FromSeconds(pause), ct).ConfigureAwait(false)) {
			return 60;
		}

		(Outcome outcome, string? retryError) = await PostCommentAsync(task, ct).ConfigureAwait(false);

		switch (outcome) {
			case Outcome.RateLimited:
				return await OnRateLimitedAsync().ConfigureAwait(false);
			case Outcome.DailyLimit:
				return await OnDailyLimitAsync().ConfigureAwait(false);
			case Outcome.AccountBlocked:
				return await BlockAccountAsync("Steam refused: " + (retryError ?? "commenting blocked")).ConfigureAwait(false);
			case Outcome.Unknown:
				await CountPostAsync(task).ConfigureAwait(false);
				Log.Warn(new Said("no reply on the retry for {0} - counting it, no further retry", task.TargetName), Bot.Name);

				return NextGapSeconds();
			case Outcome.Refused:
				return await OnTargetRefusedAsync(task).ConfigureAwait(false);
		}

		Log.Info(new Said("retry posted on {0}", task.TargetName), Bot.Name);

		return await CreditAsync(task, ct).ConfigureAwait(false);
	}

	/// <summary>The comment is live. Count it first, then tell rep4rep - in that order, so a failed credit can't double-post.</summary>
	private async Task<int> CreditAsync(Rep4RepTask task, CancellationToken ct) {
		_state!.Strikes = 0;
		await CountPostAsync(task).ConfigureAwait(false);

		int done = _state.PostsInLast24h();
		bool credited = await _api.CompleteTaskAsync(task.TaskId, task.RequiredCommentId, _profileId!, ct).ConfigureAwait(false);

		if (!credited) {
			// The comment is already live, so a transient API blip would waste the quota. One retry before giving up.
			if (await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				credited = await _api.CompleteTaskAsync(task.TaskId, task.RequiredCommentId, _profileId!, ct).ConfigureAwait(false);
			}
		}

		if (credited) {
			Log.Reward(new Said("commented on {0} and credited ({1}/{2} today)", task.TargetName, done, Cap), Bot.Name);
		} else {
			Log.Warn(new Said("commented on {0} but rep4rep didn't credit it ({1}/{2} today)", task.TargetName, done, Cap), Bot.Name);
		}

		_status = new Said("{0}/{1} today", done, Cap);

		if (done >= Cap) {
			string frees = NextSlot is { } t ? $"until ~{t.ToLocalTime():HH:mm}" : "for now";
			Log.Info(new Said("hit the {0}/24h cap - resting this account {1}, the others carry on", Cap, frees), Bot.Name);

			return 20 * 60;
		}

		return NextGapSeconds();
	}

	private async Task CountPostAsync(Rep4RepTask task) {
		_state!.RecordPost(task.TaskId);
		_rateLimitRun = 0;
		Stats.Record(Stats.KindComment, Bot.Name);
		await _state.SaveAsync(Bot.Name).ConfigureAwait(false);
	}

	private async Task<int> OnTargetRefusedAsync(Rep4RepTask task) {
		// One bad profile is not a bad account. Skip the target, try a different one, and only after three
		// DIFFERENT profiles refuse in a row conclude that it's the account.
		_state!.MarkDeadTarget(task.TargetSteamId, DateTime.UtcNow.AddSeconds(DeadTargetSeconds));
		_state.Strikes++;

		if (_state.Strikes < StrikesToBlock) {
			await _state.SaveAsync(Bot.Name).ConfigureAwait(false);
			Log.Warn(new Said("{0} refused - trying another profile ({1}/{2})", task.TargetName, _state.Strikes, StrikesToBlock), Bot.Name);

			return Rng.Next(3 * 60, 8 * 60);
		}

		return await BlockAccountAsync("comment-blocked, 24h cooldown").ConfigureAwait(false);
	}

	private async Task<int> BlockAccountAsync(string reason) {
		_state!.Strikes = 0;
		_state.BlockedUntil = DateTime.UtcNow.AddSeconds(CooldownSeconds + Rng.Next(0, 1800)).Ticks;
		_state.BlockReason = reason;
		await _state.SaveAsync(Bot.Name).ConfigureAwait(false);

		_status = new Said("resting 24h");
		Log.Attention(new Said("Steam won't take comments from this account - sitting out 24h ({0})", reason), Bot.Name);

		// Re-check every 10 min rather than sleeping the whole day, so 'rep4rep clear' can release it immediately.
		return 10 * 60;
	}

	/// <summary>
	/// Steam said "too frequently". That's OUR pacing, not a ban and not a dud target - no strike, no dead target.
	/// If it happened right at the ceiling it also tells us the real daily limit: N posts went through and N+1 was
	/// refused, so the limit is N.
	/// </summary>
	/// <summary>
	/// Steam's own daily ceiling on comments to non-friends (about 10 per rolling 24h). Unlike the spacing
	/// rate-limit this means "done for today", so it rests until the window clears rather than retrying in a couple
	/// of hours. It can be hit at a lower count than this app's own tally when comments were posted outside
	/// nocat.farm - Steam counts those, we never saw them - so the number here is only what WE posted today.
	/// </summary>
	private async Task<int> OnDailyLimitAsync() {
		int posted = _state!.PostsInLast24h();
		_state.BlockedUntil = DateTime.UtcNow.AddHours(24).Ticks;
		_state.BlockReason = "Steam's daily comment limit";
		await _state.SaveAsync(Bot.Name).ConfigureAwait(false);
		_rateLimitRun = 0;
		_status = new Said("Steam's daily comment limit - resting ~24h ({0} posted here today)", posted);
		Log.Attention(new Said("Steam's daily non-friend comment limit reached ({0} posted via nocat.farm today; any others were posted outside it) - resting until it clears in ~24h", posted), Bot.Name);

		return 10 * 60;
	}

	private async Task<int> OnRateLimitedAsync() {
		int posted = _state!.PostsInLast24h();
		int cap = Cap;

		// This message is about SPACING, not the daily ceiling.
		//
		// Steam says "you are commenting too frequently" when two comments were too close together. It says the
		// same thing whether that is the second comment of the day or the tenth, so it is not evidence about the
		// 24-hour limit at all. Inferring one from it was actively harmful: the old condition fired at posted >=
		// cap - 1, so a single pacing refusal at 9 of 10 permanently taught the account its limit was 9, and it
		// never posted a tenth comment again.
		//
		// Only a refusal AFTER the configured cap has already been reached says anything real, and by then we
		// have stopped posting anyway - so the honest answer is to learn nothing here and simply back off.
		if (Bot.Cfg.Rep4RepLearnCap && (posted > cap) && (posted > CapFloor)) {
			_state.Cap = posted;
			_state.CapLearned = true;
			Log.Info(new Said("daily limit found: {0}/24h", posted), Bot.Name);
		} else {
			Log.Info(new Said("rate-limited at {0}/{1} - that's the gap between comments, not the daily cap", posted, cap), Bot.Name);
		}

		await _state.SaveAsync(Bot.Name).ConfigureAwait(false);

		_rateLimitRun++;

		// Break the "stuck at 9/10 all day" loop. When Steam keeps refusing on spacing, chasing the last
		// slot every couple of hours just burns the day - so after two in a row, sit out until the whole 24h
		// window has cleared and come back at a clean baseline, like the account simply stopped for the day.
		if (_rateLimitRun >= 2) {
			DateTime clears = (_state.LastPost() ?? DateTime.UtcNow).AddHours(24);
			int rest = (int) Math.Max(60, (clears - DateTime.UtcNow).TotalMinutes);
			_state.BlockedUntil = clears.Ticks;
			_state.BlockReason = "rate-limited - resting to baseline";
			await _state.SaveAsync(Bot.Name).ConfigureAwait(false);
			_rateLimitRun = 0;
			_status = new Said("rate-limited - resting {0}, back at baseline", Fmt.Hm(rest));
			Log.Attention(new Said("Steam keeps rate-limiting comments at {0}/{1} - sitting out {2} until the window clears, then starting fresh", posted, cap, Fmt.Hm(rest)), Bot.Name);

			return 10 * 60;
		}

		int wait = Rng.Next(RateLimitLowMinutes, RateLimitHighMinutes + 1);
		_status = new Said("rate-limited, backing off {0}m", wait);
		Log.Warn(new Said("Steam rate-limit - backing off ~{0}m", wait), Bot.Name);

		return wait * 60;
	}

	/// <summary>Gap before the next comment. Tolerates a max below the min rather than throwing on every post.</summary>
	private int NextGapSeconds() {
		int lo = Math.Max(60, Bot.Cfg.Rep4RepGapMinMinutes * 60);
		int hi = Math.Max(lo, Bot.Cfg.Rep4RepGapMaxMinutes * 60);

		return Rng.Next(lo, hi + 1);
	}

	private async Task<Rep4RepTask?> NextTaskAsync(string profileId, CancellationToken ct) {
		foreach (Rep4RepTask task in await _api.GetTasksAsync(profileId, ct).ConfigureAwait(false)) {
			if (_state!.HasPostedTask(task.TaskId) || _state.IsDeadTarget(task.TargetSteamId)) {
				continue;
			}

			return task;
		}

		return null;
	}

	// ── posting ─────────────────────────────────────────────────────────────
	private async Task<(Outcome Outcome, string? Error)> PostCommentAsync(Rep4RepTask task, CancellationToken ct) {
		Uri url = new(WebSession.Community, $"/comment/Profile/post/{task.TargetSteamId}/-1/");
		Uri referer = new(WebSession.Community, $"/profiles/{task.TargetSteamId}");

		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["comment"] = task.CommentText,
			["count"] = "10",
			["feature2"] = "-1"
			// sessionid is injected by WebSession from the cookie jar
		};

		string? body;

		try {
			body = await Bot.Web.PostAsync(url, form, referer, ct).ConfigureAwait(false);
		} catch (Exception) {
			// Including a shutdown mid-request. Steam may well have taken the comment, and an uncounted live
			// comment is how an account quietly ends up at 11 in 24 hours.
			return (Outcome.Unknown, null);
		}

		if (body == null) {
			return (Outcome.Unknown, null);   // never saw a reply - Steam might still have posted it
		}

		if (body.Contains("\"success\":true", StringComparison.Ordinal)) {
			return (Outcome.Posted, null);
		}

		if (!body.Contains("\"success\":false", StringComparison.Ordinal)) {
			return (Outcome.Unknown, null);   // unrecognised body - unknown, NOT a refusal
		}

		string? error = ReadJsonString(body, "error");

		return (Classify(error), error);
	}

	/// <summary>
	/// Work out what Steam actually meant, matched on distinctive keywords rather than exact sentences - Steam
	/// rewords these. The raw text is always logged so the real wording can be folded in over time.
	/// </summary>
	private static Outcome Classify(string? error) {
		if (string.IsNullOrEmpty(error)) {
			return Outcome.Refused;
		}

		string e = error.ToLowerInvariant();

		// Steam's NON-FRIEND daily ceiling (10/24h) is a distinct message from the "posting too fast" spacing one.
		// Checked first, because it's the one that means "done for the day", not "wait a few minutes" - and it can
		// be reached at a lower count than expected when comments were posted outside nocat.farm (by hand, or
		// before a reset), which Steam counts but this app never saw. Treating it as a dud profile (the old
		// fall-through) just burned three targets and mislabelled it.
		// Both phrases appear together in Steam's real non-friend daily-limit text ("exceeded the maximum number
		// of comments ... not on your friends list"). Matching "not friends" ALONE would also catch an ordinary
		// friends-only privacy refusal and wrongly rest the whole account for a day, so it isn't used on its own.
		if (e.Contains("maximum number of comments", StringComparison.Ordinal)
			|| (e.Contains("exceeded", StringComparison.Ordinal) && e.Contains("comment", StringComparison.Ordinal))) {
			return Outcome.DailyLimit;
		}

		if (e.Contains("too frequently", StringComparison.Ordinal) || e.Contains("rate limit", StringComparison.Ordinal)
			|| e.Contains("try again later", StringComparison.Ordinal) || e.Contains("too many", StringComparison.Ordinal)) {
			return Outcome.RateLimited;
		}

		if (e.Contains("blocked", StringComparison.Ordinal) || e.Contains("not allowed", StringComparison.Ordinal)
			|| e.Contains("restricted", StringComparison.Ordinal) || e.Contains("limited account", StringComparison.Ordinal)
			|| e.Contains("banned", StringComparison.Ordinal)) {
			return Outcome.AccountBlocked;
		}

		return Outcome.Refused;   // private / friends-only / comments closed -> just a dud target
	}

	private static string? ReadJsonString(string json, string key) {
		string marker = $"\"{key}\"";
		int at = json.IndexOf(marker, StringComparison.Ordinal);

		if (at < 0) {
			return null;
		}

		int quote = json.IndexOf('"', at + marker.Length + 1);

		if (quote < 0) {
			return null;
		}

		int end = quote + 1;

		while (end < json.Length) {
			if (json[end] == '\\') {
				end += 2;

				continue;
			}

			if (json[end] == '"') {
				break;
			}

			end++;
		}

		return end <= json.Length && end > quote ? json[(quote + 1)..Math.Min(end, json.Length)].Replace("\\\"", "\"", StringComparison.Ordinal) : null;
	}

	// ── the daily window ────────────────────────────────────────────────────
	/// <summary>0 when we're inside the commenting window, otherwise seconds until it opens.</summary>
	private int WindowWaitSeconds() {
		int startHour = Math.Clamp(Bot.Cfg.Rep4RepStartHour, 0, 23);
		int endHour = Math.Clamp(Bot.Cfg.Rep4RepEndHour, 1, 24);

		if (startHour >= endHour) {
			return 0;   // a nonsensical window is treated as "no window" rather than a permanent stall
		}

		DateTime now = DateTime.Now;

		// A different minute per account per day. Keyed off the whole NAME, not its length - "old" and "new"
		// are both three characters, so a length-based stagger had them opening the window on the same minute
		// and posting in lockstep, which is the pattern this whole module exists to avoid.
		int hash = 17;

		foreach (char c in Bot.Name) {
			hash = ((hash * 31) + c) & 0x7FFFFFF;
		}

		int stagger = Math.Abs(hash + (now.DayOfYear * 7)) % 55;
		DateTime open = now.Date.AddHours(startHour).AddMinutes(stagger);
		DateTime close = now.Date.AddHours(endHour);

		if (now < open) {
			return (int) (open - now).TotalSeconds;
		}

		if (now >= close) {
			return (int) (now.Date.AddDays(1).AddHours(startHour).AddMinutes(stagger) - now).TotalSeconds;
		}

		return 0;
	}

	// ── the batch the user is looking at ────────────────────────────────────
	//
	// rep4rep re-samples its task list on every request, so a task id only means anything for as long as we
	// remember the batch it came from. Whatever was last handed to the dashboard is kept here so the "Post now"
	// button can act on exactly the row that was clicked.
	private readonly Dictionary<string, Rep4RepTask> _shown = [];
	private DateTime _shownAt = DateTime.MinValue;

	/// <summary>Remember a batch we just showed somebody, so its ids stay meaningful for a while.</summary>
	public void Remember(IEnumerable<Rep4RepTask> tasks) {
		lock (_shown) {
			// An hour is far longer than anyone leaves the page open, and the entries are tiny.
			if (DateTime.UtcNow - _shownAt > TimeSpan.FromHours(1)) {
				_shown.Clear();
			}

			foreach (Rep4RepTask task in tasks) {
				_shown[task.TaskId] = task;
			}

			_shownAt = DateTime.UtcNow;
		}
	}

	private Rep4RepTask? FindShown(string taskId) {
		lock (_shown) {
			return _shown.GetValueOrDefault(taskId);
		}
	}

	// ── operator controls ───────────────────────────────────────────────────
	/// <summary>
	/// Post one specific task right now, jumping the schedule. It still respects the daily cap - that ceiling is
	/// what keeps the account out of trouble, so nothing is allowed to skip it.
	/// </summary>
	public async Task<string> PostNowAsync(string taskId, CancellationToken ct = default) {
		if (!Bot.IsOnline) {
			return "That account isn't logged in.";
		}

		_state ??= await Rep4RepState.LoadAsync(Bot.Name).ConfigureAwait(false);

		if (_state == null) {
			return "Can't read this account's commenting history, so it won't post.";
		}

		if (_state.PostsInLast24h() >= Cap) {
			return $"Already at {Cap} comments in the last 24 hours - posting more is what gets accounts blocked.";
		}

		_profileId ??= await _api.ResolveProfileIdAsync(Bot.SteamId, Live.Global.Rep4RepAutoAddProfiles, ct).ConfigureAwait(false);

		if (_profileId == null) {
			return "That account isn't registered with rep4rep yet.";
		}

		// Look in the batch the user was actually shown FIRST.
		//
		// rep4rep's /tasks endpoint does not return a stable list - it hands back a fresh random sample every
		// single call. Two calls seconds apart share barely any ids. So re-fetching and matching on taskId, which
		// is what this used to do, missed almost every time and reported the task as gone when it was not.
		Rep4RepTask? task = FindShown(taskId);

		if (task == null) {
			// Not in the cache - a restart, or a page left open a very long time. A fresh batch is the only thing
			// left to try; the id usually will not be in it, which is exactly what the message below says.
			List<Rep4RepTask> batch = await _api.GetTasksAsync(_profileId, ct).ConfigureAwait(false);
			Remember(batch);

			task = batch.FirstOrDefault(t => t.TaskId == taskId);
		}

		if (task == null) {
			return "That one isn't in rep4rep's current batch any more. Refresh the list and pick another.";
		}

		(Outcome outcome, string? error) = await PostCommentAsync(task, ct).ConfigureAwait(false);

		if (outcome is Outcome.Refused or Outcome.AccountBlocked) {
			return $"Steam refused it{(string.IsNullOrEmpty(error) ? "" : $": {error}")}";
		}

		if (outcome == Outcome.RateLimited) {
			return "Steam is rate-limiting this account right now.";
		}

		// The scheduled path deliberately counts an unconfirmed post but never credits it, because Steam may
		// well have posted it and claiming the credit for something that might not exist is how an account's
		// numbers drift out of step with rep4rep's. The button has to behave the same way, not report success.
		if (outcome == Outcome.Unknown) {
			await CountPostAsync(task).ConfigureAwait(false);

			return $"Sent it to {task.TargetName}, but Steam never answered - it counts against the daily cap and hasn't been claimed with rep4rep. Check the profile.";
		}

		await CreditAsync(task, ct).ConfigureAwait(false);

		return $"Posted on {task.TargetName}.";
	}

	/// <summary>Release a cooldown / strike streak and let this account try again immediately.</summary>
	public async Task ClearHoldAsync() {
		if (_state == null) {
			return;
		}

		_state.BlockedUntil = 0;
		_state.BlockReason = "";
		_state.Strikes = 0;
		_state.ClearDeadTargets();
		await _state.SaveAsync(Bot.Name).ConfigureAwait(false);
		_status = new Said("hold cleared");
		_forceNext = true;
		Wake();
	}

	/// <summary>Pause commenting for a full 24h and come back at a clean baseline (rolling window emptied).</summary>
	public async Task RestFullDayAsync(string reason) {
		if (_state == null) {
			return;
		}

		_state.BlockedUntil = DateTime.UtcNow.AddHours(24).Ticks;
		_state.BlockReason = reason;
		_state.ClearWindow();   // baseline now - old comments won't count against tomorrow's fresh batch
		_state.Strikes = 0;
		await _state.SaveAsync(Bot.Name).ConfigureAwait(false);
		_rateLimitRun = 0;
		_status = new Said("resting a day ({0})", reason);
	}
}
