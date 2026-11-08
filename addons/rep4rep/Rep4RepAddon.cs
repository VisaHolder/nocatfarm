using System.Globalization;
using System.Text;
using NocatFarm.Plugins;
using static NocatFarm.Addons.Rep4Rep.Texts;

namespace NocatFarm.Addons.Rep4Rep;

/// <summary>
/// The official rep4rep add-on for nocat.farm: earns rep4rep points by posting the profile comments rep4rep hands out, on a
/// human schedule, from every account switched on for it. One rep4rep account (one API key) drives them all.
/// </summary>
/// <remarks>
/// Until 1.9.0 this was part of nocat.farm itself. The app moved every 1.8.x setting and record across the first time it
/// started as 1.9.0 (Config/MigrateRep4Rep.cs): the key (still encrypted), each account's on/off, cap, gaps and hours, the
/// hold, and the comment counts - so installing this picks up exactly where it was.
/// </remarks>
public sealed class Rep4RepAddon : INocatPlugin {
	public string Name => "rep4rep";

	public string Version { get; } = typeof(Rep4RepAddon).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

	/// <summary>The nocat.farm this needs at least: the comment and page abilities came in 1.9.0.</summary>
	internal const string MinAppVersion = "1.9.0";

	// ── setting names (the app's migration writes these - keep them) ──────────
	internal const string SetKey = "ApiKey", SetAutoAdd = "AutoAddProfiles", SetPointsEvery = "PointsRefreshMinutes",
		SetBlockList = "BlockList", SetBlockLinks = "BlockLinks";

	internal const string SetOn = "On", SetDailyCap = "DailyCap", SetGapMin = "GapMinMinutes", SetGapMax = "GapMaxMinutes",
		SetStartHour = "StartHour", SetEndHour = "EndHour", SetLearnCap = "LearnCap", SetRetryRefused = "RetryRefused";

	internal IPluginHost Host { get; private set; } = null!;
	internal Rep4RepApi Api { get; private set; } = new();

	/// <summary>Null while what was saved can't be read: nothing is posted, and nothing is saved over it.</summary>
	internal AddonState? State { get; private set; }

	private readonly Dictionary<string, Commenter> _accounts = new(StringComparer.OrdinalIgnoreCase);
	private readonly CancellationTokenSource _stop = new();
	private readonly SemaphoreSlim _saving = new(1, 1);

	public async Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
		Host = host;
		Texts.Language = () => host.Language;

		if (!System.Version.TryParse(host.AppVersion, out System.Version? app) || (app < System.Version.Parse(MinAppVersion))) {
			host.Log(T("rep4rep needs nocat.farm {0} or newer - this is {1}; not starting", MinAppVersion, host.AppVersion));

			return;
		}

		Declare(host);

		string? saved = await host.LoadStateAsync().ConfigureAwait(false);
		State = AddonState.Read(saved);

		if (State == null) {
			host.Log(T("can't read rep4rep's saved state (config/plugins/rep4rep.json) - no comments until it's fixed"));
		}

		Api.Trouble = line => Debug(null, line);
		Api.Token = host.Setting(SetKey);

		AddCommands(host);
		host.AddPage("rep4rep", RenderAsync);

		foreach (IPluginAccount account in host.Accounts) {
			Start(account.Name);
		}

		// An account added later gets its own loop the first time it signs in.
		host.AccountOnline += a => Start(a.Name);
	}

	public async Task OnUnloadAsync() {
		await _stop.CancelAsync().ConfigureAwait(false);
		await SaveAsync().ConfigureAwait(false);
		Api.Dispose();
	}

	private void Start(string account) {
		Commenter c;

		lock (_accounts) {
			if (_accounts.ContainsKey(account)) {
				return;
			}

			_accounts[account] = c = new Commenter(this, account);
		}

		_ = Task.Run(() => c.RunAsync(_stop.Token));
	}

	internal Commenter? Of(string account) {
		lock (_accounts) {
			return _accounts.GetValueOrDefault(account);
		}
	}

	private Commenter[] All() {
		lock (_accounts) {
			return [.. _accounts.Values.OrderBy(static c => c.Account, StringComparer.OrdinalIgnoreCase)];
		}
	}

	// ── settings ────────────────────────────────────────────────────────────
	private static void Declare(IPluginHost host) {
		host.AddSetting(new PluginSetting(SetKey, T("API key"),
			T("Your rep4rep API key, from rep4rep.com under Settings → API key. One key covers all your Steam accounts. No account yet? rep4rep.com/?r=reap"),
			PluginSettingKind.Secret));
		host.AddSetting(new PluginSetting(SetAutoAdd, T("Register accounts automatically"),
			T("Adds each Steam account to rep4rep the first time it logs in, so you don't have to add them on the website."), PluginSettingKind.Bool, "true"));
		host.AddSetting(new PluginSetting(SetPointsEvery, T("Check points every"),
			T("How often to check your rep4rep points total, in minutes. Avoid very low values for long stretches, since rep4rep has no published limit."),
			PluginSettingKind.Int, "15"));
		host.AddSetting(new PluginSetting(SetBlockList, T("Don't post comments with"),
			T("Words or phrases, separated by commas. A task whose comment has one of them is skipped and another is picked - nothing is posted. Whole words, any case: \"add\" skips \"add me +rep\" but not \"address\"."),
			PluginSettingKind.Text, BlockList.Default));
		host.AddSetting(new PluginSetting(SetBlockLinks, T("Don't post comments with links"),
			T("Skips a task whose comment has a web address or a link in it."), PluginSettingKind.Bool, "true"));

		host.AddSetting(new PluginSetting(SetOn, T("Post rep4rep comments"),
			T("Lets this account post the profile comments rep4rep hands out. All accounts earn into the same rep4rep points."),
			PluginSettingKind.Bool, "false", PerAccount: true));
		host.AddSetting(new PluginSetting(SetDailyCap, T("Comments a day, at most"),
			T("The most comments this account posts in any 24 hours. Steam allows about 10 on non-friends, and going over can get the account comment banned."),
			PluginSettingKind.Int, "10", PerAccount: true));
		host.AddSetting(new PluginSetting(SetGapMin, T("Shortest gap between comments"),
			T("The shortest time between two comments from this account, in minutes."), PluginSettingKind.Int, "10", PerAccount: true));
		host.AddSetting(new PluginSetting(SetGapMax, T("Longest gap between comments"),
			T("The longest time between comments, in minutes. Each gap is picked at random between the two."), PluginSettingKind.Int, "25", PerAccount: true));
		host.AddSetting(new PluginSetting(SetStartHour, T("Only post from"),
			T("The earliest hour of the day this account comments. Comments in the middle of the night are an easy bot giveaway."), PluginSettingKind.Int, "10", PerAccount: true));
		host.AddSetting(new PluginSetting(SetEndHour, T("Only post until"),
			T("The latest hour of the day this account comments. Set it the same as \"Only post from\" to comment around the clock."), PluginSettingKind.Int, "23", PerAccount: true));
		host.AddSetting(new PluginSetting(SetLearnCap, T("Lower the daily cap when Steam says it's reached"),
			T("When Steam says the account has hit its daily comment limit before the number set above, that becomes its limit (never below 5). A \"commenting too frequently\" refusal is about the gap between comments and never changes it."), PluginSettingKind.Bool, "true", PerAccount: true));
		host.AddSetting(new PluginSetting(SetRetryRefused, T("Retry a refused comment"),
			T("Gives a refused comment one more try before skipping that profile for a day."), PluginSettingKind.Bool, "false", PerAccount: true));
	}

	internal bool Bool(string name, string? account = null) =>
		string.Equals(account == null ? Host.Setting(name) : Host.Setting(name, account), "true", StringComparison.OrdinalIgnoreCase);

	internal int Int(string name, string? account, int fallback) =>
		int.TryParse(account == null ? Host.Setting(name) : Host.Setting(name, account), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

	/// <summary>The key, read fresh each time it's needed - pasted on the Plugins page, it takes effect at once.</summary>
	private void RefreshKey() => Api.Token = Host.Setting(SetKey);

	/// <summary>Why this comment is skipped (the word, or "link"), or null when it may be posted.</summary>
	internal string? Blocked(string comment) => BlockList.Blocks(comment, BlockList.Parse(Host.Setting(SetBlockList)), Bool(SetBlockLinks));

	// ── saving ──────────────────────────────────────────────────────────────
	internal async Task SaveAsync() {
		if (State is not { } state) {
			return;   // never over a file that couldn't be read
		}

		await _saving.WaitAsync().ConfigureAwait(false);

		try {
			await Host.SaveStateAsync(state.Write(DateTime.UtcNow)).ConfigureAwait(false);
		} finally {
			_saving.Release();
		}
	}

	// ── log and news ────────────────────────────────────────────────────────
	internal void Info(string? account, string text) => Host.Log(account == null ? $"rep4rep: {text}" : $"rep4rep · {account}: {text}");

	/// <summary>Worth telling somebody: out on Telegram / Discord / the tray when "Send add-on messages" is on (and in the log).</summary>
	internal void Notify(string account, string text) => Host.Notify($"rep4rep: {text}", account);

	private readonly Dictionary<string, string> _debugSaid = [];

	/// <summary>A detail - written once per change, so a loop that hits the same thing every pass doesn't fill the log.</summary>
	internal void Debug(string? account, string text) {
		lock (_debugSaid) {
			string key = account ?? "";

			if (_debugSaid.TryGetValue(key, out string? was) && (was == text)) {
				return;
			}

			_debugSaid[key] = text;
		}

		Info(account, text);
	}

	private bool _keyWarned;

	/// <summary>The words for a key rep4rep turned down (HTTP 401).</summary>
	internal static string KeyInvalidText() => T("rep4rep says the key is invalid - make a new key at rep4rep.com Settings → API key, and paste it under Plugins → rep4rep → API key");

	internal void WarnKeyInvalid() {
		if (!_keyWarned) {
			_keyWarned = true;
			Host.Notify(KeyInvalidText());
		}
	}

	// ── the hold ────────────────────────────────────────────────────────────
	internal bool HoldActive => (State?.HoldUntil is { } until) && (DateTime.UtcNow < until);

	private DateTime? _settledFor;
	private readonly HashSet<string> _settledAccounts = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>A hold that has passed: each account back to a clean slate once, then the hold tidied away.</summary>
	internal void SettleHold(Commenter c) {
		if ((State?.HoldUntil is not { } end) || (DateTime.UtcNow < end)) {
			return;
		}

		lock (_settledAccounts) {
			if (_settledFor != end) {
				_settledFor = end;
				_settledAccounts.Clear();
			}

			if (!_settledAccounts.Add(c.Account)) {
				return;
			}
		}

		c.FreshStart();

		// Everyone has had theirs: tidy it away.
		bool all;

		lock (_settledAccounts) {
			all = All().All(x => _settledAccounts.Contains(x.Account));
		}

		if (all && (State != null)) {
			State.HoldUntil = null;
			State.HoldFrom = null;
		}

		_ = SaveAsync();
	}

	internal async Task<string> HoldAsync(int hours) {
		if (State == null) {
			return T("can't read rep4rep's saved state (config/plugins/rep4rep.json) - no comments until it's fixed");
		}

		State.HoldFrom = hours == 0 ? null : DateTime.UtcNow;
		State.HoldUntil = hours == 0 ? null : DateTime.UtcNow.AddHours(hours);
		await SaveAsync().ConfigureAwait(false);

		foreach (Commenter c in All()) {
			c.Wake();
		}

		if (hours == 0) {
			Info(null, T("rep4rep hold lifted - commenting resumes"));

			return T("rep4rep: no hold - commenting carries on");
		}

		Info(null, T("rep4rep commenting held for {0}h - back on {1}", hours, Clock(State.HoldUntil!.Value)));

		return T("rep4rep: commenting held on every account for {0}h", hours);
	}

	// ── points ──────────────────────────────────────────────────────────────
	private (int Points, int Pending, DateTime At)? _points;
	private DateTime _pointsTried = DateTime.MinValue;
	private readonly SemaphoreSlim _pointsGate = new(1, 1);

	/// <summary>
	/// Whether a balance can be true: spendable points that are a number and not below 0. A NEGATIVE pending is not
	/// impossible - rep4rep serves -16 and the like - it's read as 0 waiting.
	/// </summary>
	internal static bool Believable((int Points, int PendingPoints)? user) => user is { Points: >= 0 };

	/// <summary>The points, cached for the setting's minutes; a forced look still waits 45 seconds since the last one.</summary>
	internal async Task<(int Points, int Pending, DateTime At)?> PointsAsync(bool force) {
		RefreshKey();

		if (!Api.HasToken) {
			return null;
		}

		int ttl = Math.Clamp(Int(SetPointsEvery, null, 15), 1, 1440);

		if ((_points is { } c) && (DateTime.UtcNow - c.At < (force ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(ttl)))) {
			return c;
		}

		if (!await _pointsGate.WaitAsync(0).ConfigureAwait(false)) {
			return _points;
		}

		try {
			if (DateTime.UtcNow - _pointsTried < TimeSpan.FromSeconds(45)) {
				return _points;
			}

			_pointsTried = DateTime.UtcNow;
			(int Points, int PendingPoints)? user = await Api.GetUserAsync().ConfigureAwait(false);

			if (Api.KeyInvalid) {
				WarnKeyInvalid();
			}

			if (Believable(user)) {
				_keyWarned = false;
				_points = (user!.Value.Points, Math.Max(0, user.Value.PendingPoints), DateTime.UtcNow);
			}

			return _points;
		} finally {
			_pointsGate.Release();
		}
	}

	// ── commands ────────────────────────────────────────────────────────────
	private void AddCommands(IPluginHost host) {
		const string Usage = "[status|points|profiles] | tasks <account> | on|off|now|pause|resume|clear|rest <account|all> | post <account> <task> | hold <hours>|off";
		string help = T("Everything rep4rep (the add-on). Run it bare for a summary. 'on'/'off' switch commenting for an account, 'now' tries a post at once (the app's rules still apply), 'hold 12' pauses every account for 12 hours.");

		host.AddCommand("rep4rep", Usage, help, CommandAsync);
		host.AddCommand("r4r", Usage, help, CommandAsync);
	}

	internal async Task<string> CommandAsync(string[] args) {
		RefreshKey();
		string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
		string? who = args.Length > 1 ? args[1] : null;

		switch (sub) {
			case "hold": {
				int hours = who == null ? -1
					: who.Equals("off", StringComparison.OrdinalIgnoreCase) ? 0
					: int.TryParse(who, NumberStyles.None, CultureInfo.InvariantCulture, out int h) ? h : -1;

				return hours is < 0 or > 720 ? T("rep4rep hold <hours> - 1 to 720, or 'rep4rep hold off' to end it") : await HoldAsync(hours).ConfigureAwait(false);
			}

			case "points": {
				if (!Api.HasToken) {
					return T("No rep4rep API key set. Get one at rep4rep.com under Settings → API key, and paste it under Plugins → rep4rep → API key.");
				}

				(int Points, int Pending, DateTime At)? p = await PointsAsync(force: true).ConfigureAwait(false);

				if (Api.KeyInvalid) {
					return KeyInvalidText();
				}

				return p is { } got
					? T("rep4rep: {0} points you can spend, {1} still being verified.", got.Points, got.Pending)
					: T("rep4rep didn't answer. Check the key is right and that you can reach rep4rep.com.");
			}

			case "profiles": {
				if (!Api.HasToken) {
					return T("No rep4rep API key set.");
				}

				List<string> profiles = await Api.GetProfilesAsync().ConfigureAwait(false);

				if (Api.KeyInvalid) {
					return KeyInvalidText();
				}

				if (profiles.Count == 0) {
					return T("rep4rep has no Steam profiles registered yet. They get added automatically the first time each account needs one.");
				}

				StringBuilder sb = new();

				foreach (string steamId in profiles) {
					IPluginAccount? owner = Host.Accounts.FirstOrDefault(a => a.SteamId.ToString(CultureInfo.InvariantCulture) == steamId);
					sb.AppendLine($"  {steamId}  {owner?.Name ?? T("(not one of yours)")}");
				}

				return sb.ToString().TrimEnd();
			}

			case "tasks": {
				if (who == null) {
					return "rep4rep tasks <account>";
				}

				(IPluginAccount? acc, string? problem) = Find(who);

				if (acc == null) {
					return problem!;
				}

				List<Rep4RepTask>? tasks = await TasksAsync(acc).ConfigureAwait(false);

				if (tasks == null) {
					return Commenter(acc.Name).LinkedElsewhere ? Commenter(acc.Name).LinkedText()
						: Api.KeyInvalid ? KeyInvalidText() : T("{0} isn't registered with rep4rep yet.", acc.Name);
				}

				if (tasks.Count == 0) {
					return T("No tasks waiting for {0} right now. rep4rep hands them out in batches - check back later.", acc.Name);
				}

				StringBuilder sb = new();
				sb.AppendLine(T("{0} task(s) waiting for {1}:", tasks.Count, acc.Name));

				foreach (Rep4RepTask t in tasks.Take(15)) {
					string skip = Blocked(t.CommentText) is { } why ? "  " + T("(skipped: \"{0}\")", why) : "";
					sb.AppendLine($"  {t.TaskId,-10} {t.TargetName,-24} \"{t.CommentText}\"{skip}");
				}

				return sb.ToString().TrimEnd();
			}

			case "status":
				return Summary();

			case "post": {
				if ((who == null) || (args.Length < 3)) {
					return "rep4rep post <account> <task>";
				}

				(IPluginAccount? acc, string? problem) = Find(who);

				return acc == null ? problem! : await Commenter(acc.Name).PostNowAsync(args[2]).ConfigureAwait(false);
			}
		}

		if (sub is not ("now" or "pause" or "resume" or "clear" or "rest" or "on" or "off")) {
			return "rep4rep " + "[status|points|profiles] | tasks <account> | on|off|now|pause|resume|clear|rest <account|all> | post <account> <task> | hold <hours>|off";
		}

		if (who == null) {
			return $"rep4rep {sub} <account|all>";
		}

		if (who.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			int n = 0;

			foreach (IPluginAccount a in Host.Accounts) {
				Commenter c = Commenter(a.Name);

				// Only accounts that comment at all - resting one with commenting off would block it the moment it's switched on.
				if ((sub is not ("on" or "off")) && !c.On) {
					continue;
				}

				if (await ActAsync(sub, c).ConfigureAwait(false)) {
					n++;
				}
			}

			return T("rep4rep {0}: {1} account(s)", sub, n);
		}

		(IPluginAccount? target, string? why2) = Find(who);

		if (target == null) {
			return why2!;
		}

		Commenter one = Commenter(target.Name);
		bool did = await ActAsync(sub, one).ConfigureAwait(false);

		return sub switch {
			"on" => T("{0}: rep4rep commenting on", target.Name),
			"off" => T("{0}: rep4rep commenting off", target.Name),
			"now" when (one.PostsToday >= one.Cap) && (one.NextSlot is { } slot) =>
				T("{0}: at its cap ({1}/{2}) - the next slot frees at {3}, it'll post then.", target.Name, one.PostsToday, one.Cap, Clock(slot)),
			"now" => T("{0}: queued - it'll post as soon as the app lets it.", target.Name),
			"pause" => T("{0}: rep4rep paused", target.Name),
			"resume" => T("{0}: rep4rep resumed", target.Name),
			"clear" => did ? T("{0}: hold cleared, refused profiles forgotten", target.Name) : T("can't read rep4rep's saved state (config/plugins/rep4rep.json) - no comments until it's fixed"),
			_ => did ? T("{0}: rep4rep resting a full day, back at baseline after", target.Name) : T("can't read rep4rep's saved state (config/plugins/rep4rep.json) - no comments until it's fixed")
		};
	}

	private async Task<bool> ActAsync(string sub, Commenter c) {
		switch (sub) {
			case "on":
			case "off":
				bool ok = Host.SetSetting(SetOn, sub == "on" ? "true" : "false", c.Account);
				c.Wake();

				return ok;
			case "now": c.RunNow(); return true;
			case "pause": c.Paused = true; return true;
			case "resume": c.Paused = false; c.Wake(); return true;
			case "clear": return await c.ClearHoldAsync().ConfigureAwait(false);
			default: return await c.RestFullDayAsync(R("manual reset")).ConfigureAwait(false);
		}
	}

	private Commenter Commenter(string account) {
		Start(account);

		return Of(account)!;
	}

	private (IPluginAccount? Account, string? Problem) Find(string name) =>
		Host.Account(name) is { } a ? (a, null) : (null, T("there's no account called '{0}'", name));

	private async Task<List<Rep4RepTask>?> TasksAsync(IPluginAccount acc) {
		Commenter c = Commenter(acc.Name);

		if (await c.ProfileIdAsync(acc.SteamId, CancellationToken.None).ConfigureAwait(false) is not { } id) {
			return null;
		}

		List<Rep4RepTask> tasks = await Api.GetTasksAsync(id).ConfigureAwait(false);
		c.Remember(tasks);

		return tasks;
	}

	private string Summary() {
		StringBuilder sb = new();
		sb.AppendLine(Api.KeyInvalid ? KeyInvalidText() : Api.HasToken ? T("API key: set") : T("API key: MISSING - paste it under Plugins → rep4rep → API key"));

		if (HoldActive) {
			sb.AppendLine(T("on hold until {0}", Clock(State!.HoldUntil!.Value)));
		}

		foreach (IPluginAccount a in Host.Accounts) {
			Commenter c = Commenter(a.Name);
			string last = c.LastPost is { } lp ? Ago(lp) : T("never");
			string steam = c.CapIsSteamLimit ? " " + T("(Steam's)") : "";
			string frees = (c.PostsToday >= c.Cap) && (c.NextSlot is { } ns) ? "   " + T("frees {0}", Clock(ns)) : "";
			sb.AppendLine($"{a.Name,-14}{(c.On ? T("on") : T("off")),-4} {c.PostsToday}/{c.Cap}{steam}   {T("last {0}", last),-16} {c.Status}{frees}");
		}

		return sb.ToString().TrimEnd();
	}

	// ── the dashboard tab ───────────────────────────────────────────────────
	internal async Task<PluginPage> RenderAsync(string? view) {
		RefreshKey();
		List<PluginBlock> blocks = [];

		if (view is { } v && v.StartsWith("tasks:", StringComparison.Ordinal) && (Host.Account(v[6..]) is { } acc)) {
			await TasksPageAsync(blocks, acc).ConfigureAwait(false);

			return new PluginPage(blocks);
		}

		if (!Api.HasToken) {
			blocks.Add(PluginBlock.Heading(T("Set up rep4rep")));
			blocks.Add(PluginBlock.Line(T("1. Make a rep4rep account (free).")));
			blocks.Add(PluginBlock.Link(T("Sign up for rep4rep"), "https://rep4rep.com/?r=reap"));
			blocks.Add(PluginBlock.Line(T("2. On rep4rep.com, open Settings and make an API key.")));
			blocks.Add(PluginBlock.Link(T("rep4rep.com → Settings"), "https://rep4rep.com/dashboard/settings"));
			blocks.Add(PluginBlock.Line(T("3. Paste the key under Plugins → rep4rep → API key, then switch commenting on for your accounts here.")));
			blocks.Add(PluginBlock.Note(T("nocat.farm registers your Steam accounts with rep4rep by itself. You don't have to add anything on their website.")));

			return new PluginPage(blocks);
		}

		(int Points, int Pending, DateTime At)? points = await PointsAsync(force: false).ConfigureAwait(false);
		Commenter[] all = [.. Host.Accounts.Select(a => Commenter(a.Name))];

		if (Api.KeyInvalid) {
			blocks.Add(PluginBlock.Heading(T("The API key doesn't work")));
			blocks.Add(PluginBlock.Line(KeyInvalidText()));
			blocks.Add(PluginBlock.Link(T("rep4rep.com → Settings"), "https://rep4rep.com/dashboard/settings"));
		}

		blocks.Add(PluginBlock.Tiles(
			(points is { } p ? p.Points.ToString(CultureInfo.InvariantCulture) : "-", T("Points you can spend")),
			(points is { } q ? q.Pending.ToString(CultureInfo.InvariantCulture) : "-", T("Waiting to be verified")),
			(all.Sum(static c => c.PostsToday).ToString(CultureInfo.InvariantCulture), T("Comments in the last 24 hours"))));

		if (points is { } at) {
			blocks.Add(PluginBlock.Note(T("as of {0}", Clock(at.At))));
		}

		// A profile on another rep4rep account can only be moved on the website - said here until it's done.
		foreach (Commenter linked in all.Where(static c => c.On && c.LinkedElsewhere)) {
			blocks.Add(PluginBlock.Line(linked.LinkedText()));
		}

		if (all.Any(static c => c.On && c.LinkedElsewhere)) {
			blocks.Add(PluginBlock.Link(T("Open rep4rep.com"), "https://rep4rep.com/dashboard/"));
		}

		blocks.Add(PluginBlock.Heading(T("Your accounts")));
		blocks.Add(PluginBlock.Table(
			[T("Account"), T("Commenting"), T("Last 24 hours"), T("Last comment"), T("Doing")],
			[.. all.Select(c => new PluginRow(
				[c.Account, c.On ? T("on") : T("off"), $"{c.PostsToday}/{c.Cap}{(c.CapIsSteamLimit ? " " + T("(Steam's)") : "")}",
					c.LastPost is { } lp ? Ago(lp) : T("never"), c.Status],
				c.On ? new PluginButton(T("Turn off"), $"rep4rep off {c.Account}") : new PluginButton(T("Turn on"), $"rep4rep on {c.Account}")))]));

		int off = all.Count(static c => !c.On);
		List<PluginButton> buttons = [];

		if (off > 0) {
			buttons.Add(new PluginButton(T("Turn it on for all {0}", all.Length), "rep4rep on all"));
		}

		buttons.AddRange(all.Where(static c => c.On).Select(static c => new PluginButton(T("Tasks for {0}", c.Account), View: "tasks:" + c.Account)));
		blocks.Add(PluginBlock.ButtonRow([.. buttons]));

		blocks.Add(PluginBlock.Heading(T("Pause rep4rep for...")));

		if (HoldActive) {
			blocks.Add(PluginBlock.Line(T("on hold until {0}", Clock(State!.HoldUntil!.Value))));
			blocks.Add(PluginBlock.ButtonRow(new PluginButton(T("Resume now"), "rep4rep hold off")));
		} else {
			blocks.Add(PluginBlock.ButtonRow([.. new[] { 6, 12, 24, 48 }.Select(static h => new PluginButton(T("{0} hours", h), $"rep4rep hold {h}"))]));
		}

		blocks.Add(PluginBlock.Heading(T("Pacing")));
		blocks.Add(PluginBlock.Note(T("Steam lets an account leave about 10 comments a day on people who aren't its friends. Go past that and Steam quietly stops accepting comments from that account for around a day. nocat.farm keeps every account under the line, spaces the comments out at random, only posts during the hours you allow - and in human mode only while the account is up. Each account's cap, gaps and hours are under Plugins → rep4rep.")));
		blocks.Add(PluginBlock.Note(T("These live on rep4rep.com, because their API can't do them: removing a Steam profile · editing your custom comment list · task history · buying points · referrals.")));
		blocks.Add(PluginBlock.Link(T("Spend your points"), "https://rep4rep.com/dashboard/"));

		return new PluginPage(blocks);
	}

	private async Task TasksPageAsync(List<PluginBlock> blocks, IPluginAccount acc) {
		blocks.Add(PluginBlock.Heading(T("Tasks for {0}", acc.Name)));
		blocks.Add(PluginBlock.ButtonRow(new PluginButton(T("Back"), View: ""), new PluginButton(T("Refresh"), View: "tasks:" + acc.Name)));

		List<Rep4RepTask>? tasks = await TasksAsync(acc).ConfigureAwait(false);

		if (tasks == null) {
			blocks.Add(PluginBlock.Line(Api.KeyInvalid ? KeyInvalidText() : T("{0} isn't registered with rep4rep yet.", acc.Name)));

			return;
		}

		if (tasks.Count == 0) {
			blocks.Add(PluginBlock.Line(T("No tasks for this account right now. rep4rep hands them out in batches - check back later.")));

			return;
		}

		blocks.Add(PluginBlock.Note(T("nocat.farm posts these for you on a schedule. There's nothing to do here - this is just what's waiting. rep4rep checks the text matches exactly, so these are posted character for character.")));
		blocks.Add(PluginBlock.Table([T("Profile"), T("Comment"), ""],
			[.. tasks.Select(t => Blocked(t.CommentText) is { } why
				? new PluginRow([t.TargetName, t.CommentText, T("skipped: \"{0}\"", why)])
				: new PluginRow([t.TargetName, t.CommentText, ""], new PluginButton(T("Post now"), $"rep4rep post {acc.Name} {t.TaskId}", "tasks:" + acc.Name)))]));
	}

	// ── words for times ─────────────────────────────────────────────────────
	internal static string Hm(int minutes) => minutes >= 60 ? $"{minutes / 60}h{minutes % 60:00}m" : $"{Math.Max(0, minutes)}m";

	internal static string Clock(DateTime utc) {
		DateTime local = utc.ToLocalTime();

		string hm = local.ToString("HH:mm", CultureInfo.InvariantCulture);

		return local.Date == DateTime.Now.Date ? hm
			: local.Date == DateTime.Now.Date.AddDays(1) ? T("{0} tomorrow", hm)
			: local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
	}

	internal static string Ago(DateTime utc) => T("{0} ago", Hm((int) (DateTime.UtcNow - utc).TotalMinutes));
}
