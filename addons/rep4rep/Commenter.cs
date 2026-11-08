using NocatFarm.Plugins;
using static NocatFarm.Addons.Rep4Rep.Texts;

namespace NocatFarm.Addons.Rep4Rep;

/// <summary>
/// One account's rep4rep commenting: fetch a task, ask the app to post it, tell rep4rep. Ported from nocat.farm 1.8.9's
/// Rep4RepModule with every behaviour kept - but the post itself, and the rules that keep an account out of trouble (awake,
/// spaced, under a ceiling, not paused), are now the app's (<see cref="IPluginHost.PostProfileCommentAsync"/>). This keeps
/// its own, stricter ones on top: the account's daily cap, its gap, its window hours, the hold, refused-profile strikes.
/// </summary>
internal sealed class Commenter(Rep4RepAddon addon, string account) {
	private const int NoTaskRetryMinutes = 30;
	private const int RetryLowSeconds = 2 * 60;
	private const int RetryHighSeconds = 6 * 60;
	private const int CooldownSeconds = 24 * 3600;
	private const int DeadTargetSeconds = 26 * 3600;   // must outlast a 24h cooldown, or the dud is the first thing retried
	private const int StrikesToBlock = 3;
	private const int RateLimitLowMinutes = 60;
	private const int RateLimitHighMinutes = 180;
	internal const int CapFloor = 5;

	/// <summary>A human-mode account's gap between comments, in minutes: ten of them across a day of 12 or so waking hours.</summary>
	internal const int HumanGapMinMinutes = 35, HumanGapMaxMinutes = 110;

	internal static readonly Random Rng = Random.Shared;

	public string Account { get; } = account;

	private IPluginHost Host => addon.Host;
	private Rep4RepApi Api => addon.Api;

	/// <summary>The account's record, or null while the add-on's saved state can't be read (nothing is posted then).</summary>
	private AccountState? State => addon.State?.For(Account);

	private volatile string _status = "";
	public string Status => _status;

	/// <summary>Manual hold, from 'rep4rep pause'. Survives nothing - it's a "stop now" switch.</summary>
	public bool Paused { get; set; }

	private string? _profileId;
	private bool _unanswered;

	/// <summary>
	/// rep4rep said this Steam profile is on another rep4rep account (409): it can only be moved on rep4rep.com, signing in
	/// through Steam. Said once; from then on only looked for (GET), never added again, until the list has it.
	/// </summary>
	public bool LinkedElsewhere { get; private set; }

	private bool _linkedSaid;
	private int _rateLimitRun, _notSentRun;
	private DateTime _capNoticed = DateTime.MinValue;

	/// <summary>One comment at a time from this account - the loop and 'rep4rep post' alike (the app keeps one too).</summary>
	private readonly SemaphoreSlim _posting = new(1, 1);

	// ── settings, per account ───────────────────────────────────────────────
	public bool On => addon.Bool(Rep4RepAddon.SetOn, Account);
	private bool Human => string.Equals(Host.GetSetting(Account, "LegitMode"), "True", StringComparison.OrdinalIgnoreCase);
	private int ConfiguredCap => Math.Clamp(addon.Int(Rep4RepAddon.SetDailyCap, Account, 10), 1, 25);
	private int GapMin => Math.Clamp(addon.Int(Rep4RepAddon.SetGapMin, Account, 10), 1, 720);
	private int GapMax => Math.Clamp(addon.Int(Rep4RepAddon.SetGapMax, Account, 25), 1, 1440);

	/// <summary>The cap in force: the configured one, pulled down - never up - by a limit Steam taught us.</summary>
	public int Cap {
		get {
			int learned = State?.Cap ?? 0;

			return learned > 0 ? Math.Min(ConfiguredCap, learned) : ConfiguredCap;
		}
	}

	public bool CapIsSteamLimit => State is { CapLearned: true, Cap: > 0 } s && (s.Cap < ConfiguredCap);
	public int PostsToday => State?.PostsInLast24h(DateTime.UtcNow) ?? 0;
	public DateTime? LastPost => State?.LastPost();
	public DateTime? NextSlot => State?.NextSlotAt(Cap, DateTime.UtcNow);

	/// <summary>Whether a daily-limit refusal at <paramref name="posted"/> comments should become the account's cap.</summary>
	internal static bool LearnsDailyLimit(bool on, int posted, int cap) => on && (posted >= CapFloor) && (posted < cap);

	/// <summary>After a comment that never went out: 5, 10, 20, 40, then 60 minutes, give or take.</summary>
	internal static int NotSentWaitSeconds(int run) => (Math.Min(60, 5 << Math.Clamp(run - 1, 0, 4)) * 60) + Rng.Next(0, 120);

	// ── the loop ────────────────────────────────────────────────────────────
	private volatile bool _forceNext;
	private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _wakePending;

	/// <summary>Skip this add-on's own wait and try now. Never skips the cap - nor any of the app's rules.</summary>
	public void RunNow() {
		_forceNext = true;
		Wake();
	}

	public void Wake() {
		Interlocked.Exchange(ref _wakePending, 1);
		Volatile.Read(ref _wake).TrySetResult(true);
	}

	private async Task<bool> SleepOrWake(TimeSpan wait, CancellationToken ct) {
		TaskCompletionSource<bool> woken = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Interlocked.Exchange(ref _wake, woken);

		if ((Interlocked.Exchange(ref _wakePending, 0) == 1) || _forceNext) {
			return !ct.IsCancellationRequested;
		}

		try {
			Task winner = await Task.WhenAny(woken.Task, Task.Delay(wait, ct)).ConfigureAwait(false);

			if (winner == woken.Task) {
				Interlocked.Exchange(ref _wakePending, 0);
			}
		} catch (OperationCanceledException) {
			// shutting down
		}

		return !ct.IsCancellationRequested;
	}

	private static async Task<bool> Sleep(TimeSpan wait, CancellationToken ct) {
		try {
			await Task.Delay(wait, ct).ConfigureAwait(false);

			return true;
		} catch (OperationCanceledException) {
			return false;
		}
	}

	public async Task RunAsync(CancellationToken ct) {
		bool settled = false;

		while (!ct.IsCancellationRequested) {
			int wait;

			if (!On) {
				_status = T("off");
				wait = 20;
			} else if (!Api.HasToken) {
				_status = T("waiting for the rep4rep API key");
				wait = 20;
			} else if (addon.HoldActive) {
				_status = T("on hold until {0}", Rep4RepAddon.Clock(addon.State?.HoldUntil ?? DateTime.UtcNow));
				wait = 60;
			} else if (!settled) {
				// Settle after starting: commenting in the same minute as every restart is a pattern.
				settled = true;
				_status = T("settling in after signing in");
				wait = Rng.Next(90, 601);
			} else {
				addon.SettleHold(this);

				try {
					wait = await StepAsync(ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					return;
				} catch (Exception e) {
					// Never silent: a throwing step is indistinguishable from "nothing to do".
					addon.Info(Account, T("rep4rep step failed: {0}: {1}", e.GetType().Name, e.Message));
					wait = 10 * 60;
				}
			}

			if (!await SleepOrWake(TimeSpan.FromSeconds(Math.Max(20, wait)), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>How long to leave it when the app says not now: until it said, give or take, never past 12 hours.</summary>
	internal static int NotNowWaitSeconds(DateTime? retryAt, DateTime now, int usual, int afterWaking) {
		if (retryAt is not { } at || (at <= now)) {
			return usual;
		}

		// Asleep till the morning: a few minutes after it gets up, so the comment isn't the first thing it does.
		double until = (at - now).TotalSeconds + ((at - now) > TimeSpan.FromMinutes(30) ? afterWaking : Rng.Next(10, 90));

		return (int) Math.Clamp(until, 60, TimeSpan.FromHours(12).TotalSeconds);
	}

	/// <summary>One look. Returns how many seconds to wait before the next.</summary>
	private async Task<int> StepAsync(CancellationToken ct) {
		bool force = _forceNext;
		_forceNext = false;

		IPluginAccount? me = Host.Account(Account);

		if (me == null) {
			_status = T("not an account any more");

			return 30 * 60;
		}

		if (Paused || me.IsPaused) {
			_status = T("paused");

			return 5 * 60;
		}

		if (!me.IsOnline) {
			_status = T("offline");

			return 2 * 60;
		}

		// The app's rules first: asleep, on a break, you playing on it, just signed in, its own ceiling.
		PluginCommentCheck check = Host.CanComment(Account);

		if (!check.Allowed) {
			_status = check.Reason;

			return NotNowWaitSeconds(check.RetryAt, DateTime.UtcNow, Rng.Next(4 * 60, 10 * 60), Rng.Next(60, 15 * 60));
		}

		if (State is not { } state) {
			_status = T("state unreadable - holding");

			return 10 * 60;
		}

		DateTime now = DateTime.UtcNow;

		if (state.IsBlocked(now)) {
			TimeSpan left = new DateTime(state.BlockedUntil, DateTimeKind.Utc) - now;
			_status = T("resting {0} ({1})", Rep4RepAddon.Hm((int) left.TotalMinutes), T(state.BlockReason));

			return 10 * 60;
		}

		int posted = state.PostsInLast24h(now);
		DateTime? lastSeen = state.LastPost();

		if (posted >= Cap) {
			_status = T("{0}/{1} today - done", posted, Cap);

			return CapWaitSeconds();
		}

		if (!force) {
			int windowWait = WindowWaitSeconds();

			if (windowWait > 0) {
				_status = T("outside the window, opens in {0}", Rep4RepAddon.Hm(windowWait / 60));

				return Math.Min(windowWait, 30 * 60);
			}

			// Against the SAVED last post, not the loop timer: a restart resets the timer.
			if (lastSeen is { } last) {
				int since = (int) (now - last).TotalSeconds;
				int gap = Math.Max(GapMin, Human ? HumanGapMinMinutes : 0) * 60;

				if (since < gap) {
					_status = T("{0}/{1} today - next in {2}", posted, Cap, Rep4RepAddon.Hm((gap - since) / 60));

					return (gap - since) + Rng.Next(30, 180);
				}
			}
		}

		if (await ProfileIdAsync(me.SteamId, ct).ConfigureAwait(false) is not { } profileId) {
			return NotRegistered();
		}

		Rep4RepTask? task = await NextTaskAsync(profileId, state, ct).ConfigureAwait(false);

		if (task == null) {
			_status = T("{0}/{1} today - no task available", posted, Cap);

			return NoTaskRetryMinutes * 60;
		}

		await _posting.WaitAsync(ct).ConfigureAwait(false);

		try {
			// Stopped or spent while this step was fetching - paused, held, switched off, a 'post' went out.
			if (Stopped(state) || (state.LastPost() != lastSeen) || state.HasPostedTask(task.TaskId)) {
				return 60;
			}

			return await PostAsync(task, profileId, state, retried: false, ct).ConfigureAwait(false);
		} finally {
			_posting.Release();
		}
	}

	private int NotRegistered() {
		if (LinkedElsewhere) {
			_status = LinkedText();

			if (!_linkedSaid) {
				_linkedSaid = true;
				addon.Notify(Account, LinkedText());
			}

			return 30 * 60;
		}

		if (Api.KeyInvalid) {
			_status = Rep4RepAddon.KeyInvalidText();
			addon.WarnKeyInvalid();

			return 30 * 60;
		}

		if (_unanswered) {
			_status = T("rep4rep.com isn't answering - trying again in 15m");

			return 15 * 60;
		}

		_status = T("not registered on rep4rep");
		addon.Info(Account, addon.Bool(Rep4RepAddon.SetAutoAdd)
			? T("rep4rep won't add this account - check the API key")
			: T("not on rep4rep yet - add it there, or turn on Register accounts automatically"));

		return 30 * 60;
	}

	/// <summary>What to do about a profile that's on another rep4rep account - said once, and on the tab.</summary>
	internal string LinkedText() => T("{0}'s Steam profile is on another rep4rep account - add it at rep4rep.com (Add Steam profile → sign in through Steam), then it carries on", Account);

	/// <summary>Anything that says no comment may go out right now from this add-on's side (the app checks its own again).</summary>
	private bool Stopped(AccountState state) =>
		Paused || !On || addon.HoldActive || state.IsBlocked(DateTime.UtcNow) || (state.PostsInLast24h(DateTime.UtcNow) >= Cap) || (Host.Account(Account)?.IsPaused != false);

	private Task<(string? Id, bool Unanswered, bool LinkedElsewhere)>? _resolving;
	private readonly Lock _resolveGate = new();

	internal async Task<string?> ProfileIdAsync(ulong steamId, CancellationToken ct) {
		if (_profileId is { } have) {
			return have;
		}

		Task<(string? Id, bool Unanswered, bool LinkedElsewhere)> lookup;

		lock (_resolveGate) {
			// Once rep4rep has said it's on another account, adding it again only gets the same 409 - it's looked for instead.
			bool autoAdd = addon.Bool(Rep4RepAddon.SetAutoAdd) && !LinkedElsewhere;
			lookup = _resolving ??= Task.Run(() => Api.ResolveDetailedAsync(steamId, autoAdd, CancellationToken.None));
		}

		try {
			(string? id, _unanswered, bool linked) = await lookup.WaitAsync(ct).ConfigureAwait(false);

			if (id != null) {
				_profileId = id;

				// Moved over on rep4rep.com: carries on by itself.
				if (LinkedElsewhere) {
					LinkedElsewhere = false;
					_linkedSaid = false;
					addon.Info(Account, T("rep4rep has {0}'s Steam profile now - commenting carries on", Account));
				}
			} else if (linked) {
				LinkedElsewhere = true;
			}

			return id;
		} finally {
			if (lookup.IsCompleted) {
				lock (_resolveGate) {
					if (ReferenceEquals(_resolving, lookup)) {
						_resolving = null;
					}
				}
			}
		}
	}

	/// <summary>The first task worth doing: not done before, not on a profile that refused, not a comment on the block list.</summary>
	private async Task<Rep4RepTask?> NextTaskAsync(string profileId, AccountState state, CancellationToken ct) {
		List<Rep4RepTask> tasks = await Api.GetTasksAsync(profileId, ct).ConfigureAwait(false);
		Remember(tasks);

		foreach (Rep4RepTask task in tasks) {
			if (state.HasPostedTask(task.TaskId) || state.IsDeadTarget(task.TargetSteamId, DateTime.UtcNow)) {
				continue;
			}

			if (addon.Blocked(task.CommentText) is { } why) {
				addon.Debug(Account, $"skipped a task on {task.TargetName}: its comment has \"{why}\" (block list)");

				continue;
			}

			return task;
		}

		return null;
	}

	/// <summary>Post one, deal with what came of it. Called holding <see cref="_posting"/>.</summary>
	private async Task<int> PostAsync(Rep4RepTask task, string profileId, AccountState state, bool retried, CancellationToken ct) {
		_status = T("commenting on {0}", task.TargetName);
		PluginCommentResult result = await Host.PostProfileCommentAsync(Account, task.TargetSteamId, task.CommentText, ct).ConfigureAwait(false);

		switch (result.Outcome) {
			case PluginCommentOutcome.NotNow:
				// The app said no before anything went out - nothing counted.
				_status = result.Detail;

				return NotNowWaitSeconds(result.RetryAt, DateTime.UtcNow, Rng.Next(4 * 60, 10 * 60), Rng.Next(60, 15 * 60));
			case PluginCommentOutcome.CommentsClosed:
				// No comment box for this account: set aside like a refused one, with no strike - it says nothing about the account.
				state.MarkDeadTarget(task.TargetSteamId, DateTime.UtcNow.AddSeconds(DeadTargetSeconds));
				await addon.SaveAsync().ConfigureAwait(false);
				addon.Info(Account, T("{0} doesn't take comments · skipped", task.TargetName));

				return Rng.Next(60, 4 * 60);
			case PluginCommentOutcome.NotSent:
				return OnNotSent(task);
			case PluginCommentOutcome.RateLimited:
				return await OnRateLimitedAsync(state).ConfigureAwait(false);
			case PluginCommentOutcome.DailyLimit:
				return await OnDailyLimitAsync(state).ConfigureAwait(false);
			case PluginCommentOutcome.AccountBlocked:
				return await BlockAccountAsync(state, R("Steam refused"), result.Detail).ConfigureAwait(false);
			case PluginCommentOutcome.Unknown:
				// Steam may well have posted it. A retry would put a SECOND identical comment on the same profile.
				await CountPostAsync(state, task).ConfigureAwait(false);
				addon.Info(Account, retried ? T("no reply on retry for {0} - counting it", task.TargetName) : T("no reply for {0} - counting it as posted", task.TargetName));

				return NextGapSeconds();
			case PluginCommentOutcome.Refused:
				if (!retried && addon.Bool(Rep4RepAddon.SetRetryRefused, Account)) {
					int pause = Rng.Next(RetryLowSeconds, RetryHighSeconds + 1);
					addon.Info(Account, T("comment on {0} failed{1} - retrying in ~{2}", task.TargetName,
						string.IsNullOrEmpty(result.Detail) ? "" : $" (\"{result.Detail}\")", Rep4RepAddon.Hm(Math.Max(1, pause / 60))));

					if (!await Sleep(TimeSpan.FromSeconds(pause), ct).ConfigureAwait(false) || Stopped(state)) {
						return 60;
					}

					return await PostAsync(task, profileId, state, retried: true, ct).ConfigureAwait(false);
				}

				return await OnTargetRefusedAsync(state, task).ConfigureAwait(false);
		}

		if (retried) {
			addon.Info(Account, T("retry posted on {0}", task.TargetName));
		}

		return await CreditAsync(state, task, profileId, ct).ConfigureAwait(false);
	}

	private int OnNotSent(Rep4RepTask task) {
		_notSentRun++;
		int wait = NotSentWaitSeconds(_notSentRun);
		_status = T("couldn't reach Steam - trying again in {0}", Rep4RepAddon.Hm(wait / 60));
		addon.Info(Account, T("comment on {0} wasn't sent (couldn't reach Steam) - not counted, trying again in {1}", task.TargetName, Rep4RepAddon.Hm(wait / 60)));

		return wait;
	}

	/// <summary>The comment is live. Count it first, then tell rep4rep - in that order, so a failed credit can't double-post.</summary>
	private async Task<int> CreditAsync(AccountState state, Rep4RepTask task, string profileId, CancellationToken ct) {
		state.Strikes = 0;
		await CountPostAsync(state, task).ConfigureAwait(false);

		int done = state.PostsInLast24h(DateTime.UtcNow);
		bool credited = await Api.CompleteTaskAsync(task, profileId, ct).ConfigureAwait(false);

		// The comment is already up, so a passing hiccup at rep4rep would waste it: one more try.
		if (!credited && await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
			credited = await Api.CompleteTaskAsync(task, profileId, ct).ConfigureAwait(false);
		}

		if (credited) {
			addon.Notify(Account, T("commented on {0} and credited ({1}/{2} today)", task.TargetName, done, Cap));
		} else {
			addon.Info(Account, T("commented on {0}, not credited ({1}/{2} today)", task.TargetName, done, Cap));
		}

		_status = T("{0}/{1} today", done, Cap);

		if (done < Cap) {
			return NextGapSeconds();
		}

		// The window is ROLLING: once full, every slot that frees is filled and the cap hit again - said once in 12 hours.
		if (DateTime.UtcNow - _capNoticed > TimeSpan.FromHours(12)) {
			_capNoticed = DateTime.UtcNow;
			addon.Info(Account, NextSlot is { } t ? T("rep4rep {0}/{0} · next ~{1}", Cap, Rep4RepAddon.Clock(t)) : T("rep4rep {0}/{0} · resting for now", Cap));
		}

		return CapWaitSeconds();
	}

	/// <summary>At the cap: wait for the oldest comment to age out of the 24 hours, then an ordinary gap on top.</summary>
	private int CapWaitSeconds() =>
		NextSlot is not { } frees ? 20 * 60 : (int) Math.Max(60, (frees - DateTime.UtcNow).TotalSeconds) + NextGapSeconds();

	private async Task CountPostAsync(AccountState state, Rep4RepTask task) {
		state.RecordPost(task.TaskId, DateTime.UtcNow);
		_rateLimitRun = 0;
		_notSentRun = 0;
		await addon.SaveAsync().ConfigureAwait(false);
	}

	private async Task<int> OnTargetRefusedAsync(AccountState state, Rep4RepTask task) {
		// One bad profile is not a bad account: three DIFFERENT profiles refusing in a row is.
		state.MarkDeadTarget(task.TargetSteamId, DateTime.UtcNow.AddSeconds(DeadTargetSeconds));
		state.Strikes++;

		if (state.Strikes < StrikesToBlock) {
			await addon.SaveAsync().ConfigureAwait(false);
			addon.Info(Account, T("{0} refused - trying another profile ({1}/{2})", task.TargetName, state.Strikes, StrikesToBlock));

			return Rng.Next(3 * 60, 8 * 60);
		}

		return await BlockAccountAsync(state, R("comment-blocked, 24h cooldown")).ConfigureAwait(false);
	}

	/// <param name="reason">One of a fixed set of English keys. Stored as-is and translated when shown.</param>
	private async Task<int> BlockAccountAsync(AccountState state, string reason, string? detail = null) {
		state.Strikes = 0;
		state.BlockedUntil = DateTime.UtcNow.AddSeconds(CooldownSeconds + Rng.Next(0, 1800)).Ticks;
		state.BlockReason = reason;
		await addon.SaveAsync().ConfigureAwait(false);

		_status = T("resting 24h");
		addon.Notify(Account, T("Steam blocks its comments - resting 24h ({0})", T(reason)));

		if (!string.IsNullOrWhiteSpace(detail)) {
			addon.Debug(Account, $"Steam's exact words: {detail}");
		}

		return 10 * 60;
	}

	/// <summary>Steam's own daily ceiling on comments to strangers: done for today, and maybe the account's real limit.</summary>
	internal async Task<int> OnDailyLimitAsync(AccountState state) {
		int posted = state.PostsInLast24h(DateTime.UtcNow);

		if (LearnsDailyLimit(addon.Bool(Rep4RepAddon.SetLearnCap, Account), posted, Cap)) {
			state.Cap = posted;
			state.CapLearned = true;
			addon.Info(Account, T("daily limit found: {0}/24h", posted));
		}

		state.BlockedUntil = DateTime.UtcNow.AddHours(24).Ticks;
		state.BlockReason = R("Steam's daily comment limit");
		await addon.SaveAsync().ConfigureAwait(false);
		_rateLimitRun = 0;
		_status = T("Steam's daily comment limit - resting ~24h ({0} posted here today)", posted);
		addon.Notify(Account, T("daily comment limit hit ({0} posted here) - resting ~24h", posted));

		return 10 * 60;
	}

	/// <summary>"Too frequently" is about SPACING, not the daily ceiling: learn nothing from it, back off.</summary>
	private async Task<int> OnRateLimitedAsync(AccountState state) {
		int posted = state.PostsInLast24h(DateTime.UtcNow);
		int cap = Cap;
		addon.Info(Account, T("rate-limited at {0}/{1} - comment gap, not the daily cap", posted, cap));
		_rateLimitRun++;

		// Twice in a row: sit out until the whole 24h has cleared, like an account that stopped for the day.
		if (_rateLimitRun >= 2) {
			DateTime clears = (state.LastPost() ?? DateTime.UtcNow).AddHours(24);
			int rest = (int) Math.Max(60, (clears - DateTime.UtcNow).TotalMinutes);
			state.BlockedUntil = clears.Ticks;
			state.BlockReason = R("rate-limited - resting to baseline");
			await addon.SaveAsync().ConfigureAwait(false);
			_rateLimitRun = 0;
			_status = T("rate-limited - resting {0}, back at baseline", Rep4RepAddon.Hm(rest));
			addon.Notify(Account, T("still rate-limited at {0}/{1} - resting {2}, then starting fresh", posted, cap, Rep4RepAddon.Hm(rest)));

			return 10 * 60;
		}

		await addon.SaveAsync().ConfigureAwait(false);
		int wait = Rng.Next(RateLimitLowMinutes, RateLimitHighMinutes + 1);
		_status = T("rate-limited, backing off {0}m", wait);

		return wait * 60;
	}

	/// <summary>Gap before the next comment. In human mode the day's comments spread over the day.</summary>
	private int NextGapSeconds() {
		if (Human) {
			int min = Math.Max(GapMin, HumanGapMinMinutes);
			int max = Math.Max(min + 10, Math.Max(GapMax, HumanGapMaxMinutes));

			// Leaning early, like a person: the square of a flat pick.
			double r = Rng.NextDouble();

			return (int) ((min + ((max - min) * r * r)) * 60);
		}

		int lo = Math.Max(60, GapMin * 60);
		int hi = Math.Max(lo, GapMax * 60);

		return Rng.Next(lo, hi + 1);
	}

	// ── the daily window ────────────────────────────────────────────────────
	private int _windowDay = -1, _openStagger, _closeEarly;

	/// <summary>0 inside the commenting window, otherwise seconds until it opens.</summary>
	private int WindowWaitSeconds() {
		DateTime now = DateTime.Now;

		if (_windowDay != now.DayOfYear) {
			_windowDay = now.DayOfYear;
			_openStagger = Rng.Next(0, 56);
			_closeEarly = Rng.Next(0, 41);
		}

		return WindowWait(now, addon.Int(Rep4RepAddon.SetStartHour, Account, 10), addon.Int(Rep4RepAddon.SetEndHour, Account, 23), _openStagger, _closeEarly);
	}

	/// <summary>
	/// The window: from a random minute after the start hour to a random few minutes before the end hour, each eating no more
	/// than its share of a short window. A start at or after the end is no window at all, rather than a permanent stall.
	/// </summary>
	internal static int WindowWait(DateTime now, int startHour, int endHour, int openStagger, int closeEarly) {
		startHour = Math.Clamp(startHour, 0, 23);
		endHour = Math.Clamp(endHour, 1, 24);

		if (startHour >= endHour) {
			return 0;
		}

		int span = (endHour - startHour) * 60;
		int stagger = Math.Min(openStagger, span / 2);
		DateTime open = now.Date.AddHours(startHour).AddMinutes(stagger);
		DateTime close = now.Date.AddHours(endHour).AddMinutes(-Math.Min(closeEarly, span / 3));

		if (now < open) {
			return (int) (open - now).TotalSeconds;
		}

		if (now >= close) {
			return (int) (now.Date.AddDays(1).AddHours(startHour).AddMinutes(stagger) - now).TotalSeconds;
		}

		return 0;
	}

	// ── the batch somebody is looking at ────────────────────────────────────
	// rep4rep re-samples its task list on every request, so a task id only means anything while the batch it came from is
	// remembered: the page's "Post now" acts on exactly the row that was pressed.
	private readonly Dictionary<string, Rep4RepTask> _shown = [];
	private DateTime _shownAt = DateTime.MinValue;

	public void Remember(IEnumerable<Rep4RepTask> tasks) {
		lock (_shown) {
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

	/// <summary>
	/// Post one particular task now ('rep4rep post', the page's button). Never past this add-on's cap or a rest - and the
	/// app's own rules still decide whether it goes at all.
	/// </summary>
	public async Task<string> PostNowAsync(string taskId, CancellationToken ct = default) {
		IPluginAccount? me = Host.Account(Account);

		if (me is not { IsOnline: true }) {
			return T("That account isn't logged in.");
		}

		if (!await _posting.WaitAsync(0, ct).ConfigureAwait(false)) {
			return T("this account is posting a comment right now - try again in a few minutes");
		}

		try {
			if (State is not { } state) {
				return T("Can't read this account's commenting history, so it won't post.");
			}

			if (state.PostsInLast24h(DateTime.UtcNow) >= Cap) {
				return T("Already at {0} comments in the last 24 hours - posting more is what gets accounts blocked.", Cap);
			}

			if (state.IsBlocked(DateTime.UtcNow)) {
				return T("this account is resting from comments until {0} - 'rep4rep clear {1}' lifts it", Rep4RepAddon.Clock(new DateTime(state.BlockedUntil, DateTimeKind.Utc)), Account);
			}

			if (Human && (state.LastPost() is { } lastPost) && (DateTime.UtcNow < lastPost.AddMinutes(Math.Max(GapMin, HumanGapMinMinutes)))) {
				return T("human mode spaces comments out - this account can post the next one from {0}", Rep4RepAddon.Clock(lastPost.AddMinutes(Math.Max(GapMin, HumanGapMinMinutes))));
			}

			if (await ProfileIdAsync(me.SteamId, ct).ConfigureAwait(false) is not { } profileId) {
				return LinkedElsewhere ? LinkedText() : Api.KeyInvalid ? Rep4RepAddon.KeyInvalidText() : T("That account isn't registered with rep4rep yet.");
			}

			Rep4RepTask? task = FindShown(taskId);

			if (task == null) {
				List<Rep4RepTask> batch = await Api.GetTasksAsync(profileId, ct).ConfigureAwait(false);
				Remember(batch);
				task = batch.FirstOrDefault(t => t.TaskId == taskId);
			}

			if (task == null) {
				return T("That one isn't in rep4rep's current batch any more. Refresh the list and pick another.");
			}

			if (state.HasPostedTask(task.TaskId)) {
				return T("already commented for that task - pick another");
			}

			if (addon.Blocked(task.CommentText) is { } why) {
				return T("that comment has \"{0}\", which is on the block list - pick another", why);
			}

			PluginCommentResult result = await Host.PostProfileCommentAsync(Account, task.TargetSteamId, task.CommentText, ct).ConfigureAwait(false);

			switch (result.Outcome) {
				case PluginCommentOutcome.NotNow:
					return T("not now: {0}", result.Detail);
				case PluginCommentOutcome.CommentsClosed:
					state.MarkDeadTarget(task.TargetSteamId, DateTime.UtcNow.AddSeconds(DeadTargetSeconds));
					await addon.SaveAsync().ConfigureAwait(false);

					return T("{0} doesn't take comments · skipped", task.TargetName);
				case PluginCommentOutcome.NotSent:
					return T("couldn't reach Steam - nothing was posted or counted; try again in a few minutes");
				case PluginCommentOutcome.Refused or PluginCommentOutcome.AccountBlocked:
					return T("Steam refused it{0}", string.IsNullOrEmpty(result.Detail) ? "" : ": " + result.Detail);
				case PluginCommentOutcome.RateLimited:
					return T("Steam is rate-limiting this account right now.");
				case PluginCommentOutcome.DailyLimit:
					await OnDailyLimitAsync(state).ConfigureAwait(false);

					return T("Steam's daily limit on comments to non-friends is reached - nothing was posted, and this account rests ~24h");
				case PluginCommentOutcome.Unknown:
					await CountPostAsync(state, task).ConfigureAwait(false);

					return T("Sent it to {0}, but Steam never answered - it counts against the daily cap and hasn't been claimed with rep4rep. Check the profile.", task.TargetName);
			}

			await CreditAsync(state, task, profileId, ct).ConfigureAwait(false);

			return T("Posted on {0}.", task.TargetName);
		} finally {
			_posting.Release();
		}
	}

	/// <summary>Release a cooldown / strike streak. False when the add-on's state can't be read.</summary>
	public async Task<bool> ClearHoldAsync() {
		if (State is not { } state) {
			return false;
		}

		state.BlockedUntil = 0;
		state.BlockReason = "";
		state.Strikes = 0;
		state.ClearDeadTargets();
		await addon.SaveAsync().ConfigureAwait(false);
		_status = T("hold cleared");

		// A human-mode account still waits for its day and its spacing - and the app's rules hold either way.
		_forceNext = !Human;
		Wake();

		return true;
	}

	/// <summary>Rest a full day. The comments of the last 24h are kept, so lifting it early can't make room for a second day's.</summary>
	public async Task<bool> RestFullDayAsync(string reason) {
		if (State is not { } state) {
			return false;
		}

		state.BlockedUntil = DateTime.UtcNow.AddHours(24).Ticks;
		state.BlockReason = reason;
		state.Strikes = 0;
		await addon.SaveAsync().ConfigureAwait(false);
		_rateLimitRun = 0;
		_status = T("resting a day ({0})", T(reason));

		return true;
	}

	/// <summary>A hold ran out: back to a clean slate (the learned cap and the last 24h stay).</summary>
	internal void FreshStart() {
		State?.ResetForFreshStart();
		addon.Info(Account, T("the rep4rep hold has run out - back on at {0}/{1}", PostsToday, Cap));
	}
}
