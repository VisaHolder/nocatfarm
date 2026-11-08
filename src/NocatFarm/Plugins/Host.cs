using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Plugins;

/// <summary>
/// The <see cref="IPluginHost"/> a plugin actually gets.
/// </summary>
/// <remarks>
/// Two jobs: hand out read-only views of the accounts, and take everything else through the command line. A
/// plugin never holds a <see cref="Bot"/>, so it never holds the Steam client, the web session or the config -
/// and every change it makes goes through the same validation and logging a typed command does.
///
/// Handlers are wrapped so a throwing plugin cannot bring down the caller. These fire from Steam callback
/// threads and from module loops; an unhandled exception there would take out whatever was in flight.
/// </remarks>
internal sealed partial class Host(BotManager mgr, string owner) : IPluginHost {
	private readonly string _owner = owner;

	private readonly Dictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> _commands =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>A copy, taken under the lock <see cref="AddCommand"/> writes under.</summary>
	internal IReadOnlyDictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> Commands {
		get {
			lock (_commandsGate) {
				return new Dictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)>(_commands, StringComparer.OrdinalIgnoreCase);
			}
		}
	}

	public string AppVersion => Build.Version;

	public IReadOnlyList<IPluginAccount> Accounts => [.. mgr.All.Select(static b => new AccountView(b))];

	public IPluginAccount? Account(string name) => mgr.Get(name) is { } bot ? new AccountView(bot) : null;

	public void Log(string message) {
		if (MayWrite()) {
			NocatFarm.Log.Info(new Said("[plugin] {0}", Clip(message)));
		}
	}

	// ── 1.9.0 stress fixes: what one plugin may add, and write ──────────────────
	/// <summary>The most commands one plugin adds. rep4rep has two; thousands made every help page and command line slow.</summary>
	internal const int MaxCommands = 50;

	/// <summary>The most settings one plugin adds. rep4rep has 13; thousands made its page (and every save) crawl.</summary>
	internal const int MaxSettings = 200;

	/// <summary>The most lines (log and notifications together) one plugin writes in a minute - the rest are dropped.</summary>
	internal const int MaxLinesPerMinute = 300;

	/// <summary>The longest line a plugin writes; past it, cut off.</summary>
	internal const int MaxLineLength = 2000;

	/// <summary>Events waiting for one plugin's handlers; past it, new ones are dropped (its handler is stuck).</summary>
	internal const int MaxQueuedEvents = 1000;

	private readonly Lock _writeGate = new();
	private DateTime _minuteFrom;
	private int _linesThisMinute;
	private bool _tooManyCommandsSaid, _tooManySettingsSaid, _queueFullSaid;

	/// <summary>
	/// Whether this plugin may write another line this minute. A plugin logging in a tight loop filled the disk with the day's
	/// log (and the window and the dashboard with nothing else): past the limit its lines are dropped, said once a minute.
	/// </summary>
	private bool MayWrite() {
		bool first;

		lock (_writeGate) {
			DateTime now = DateTime.UtcNow;

			if (now - _minuteFrom >= TimeSpan.FromMinutes(1)) {
				_minuteFrom = now;
				_linesThisMinute = 0;
			}

			_linesThisMinute++;

			if (_linesThisMinute <= MaxLinesPerMinute) {
				return true;
			}

			first = _linesThisMinute == MaxLinesPerMinute + 1;
		}

		if (first) {
			NocatFarm.Log.Warn(new Said("plugin {0} is writing too much - its lines are dropped for the rest of the minute", _owner));
		}

		return false;
	}

	internal static string Clip(string? text) =>
		string.IsNullOrEmpty(text) ? "" : text.Length <= MaxLineLength ? text : string.Concat(text.AsSpan(0, MaxLineLength), "…");

	public Task<string> RunCommandAsync(string line) => Commands_RunAsync(line);

	private async Task<string> Commands_RunAsync(string line) {
		try {
			return await NocatFarm.Commands.RunLoggedAsync(mgr, line, "plugin").ConfigureAwait(false);
		} catch (Exception e) {
			// Handed back to the plugin, which may well not say it anywhere: a command throwing is a bug, so it goes in the file.
			NocatFarm.Log.Failed($"plugin {_owner}: running a command", e);
			NocatFarm.Log.StackToFile(e);

			return $"failed: {NocatFarm.Log.Describe(e)}";
		}
	}

	private readonly List<PluginSetting> _declared = [];
	private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
	private bool _valuesRead;

	/// <summary>
	/// The settings, read and written from the plugin's own threads, the dashboard and the Steam callbacks all at once.
	/// Unguarded, a first save racing the first read wrote only the one new value - over every other setting the plugin had.
	/// </summary>
	private readonly Lock _valuesGate = new();

	internal IReadOnlyList<PluginSetting> Declared {
		get {
			lock (_valuesGate) {
				return [.. _declared];
			}
		}
	}

	public void AddSetting(PluginSetting setting) {
		bool tooMany = false;

		lock (_valuesGate) {
			// A name that can't be one: none, a colon (the per-account key's own), a line break - or one that would fill the page.
			if ((setting == null) || string.IsNullOrWhiteSpace(setting.Name) || (setting.Name.Length > 64) || setting.Name.Any(static c => (c == ':') || char.IsControl(c))
				|| _declared.Any(d => string.Equals(d.Name, setting.Name, StringComparison.OrdinalIgnoreCase))) {
				return;
			}

			if (_declared.Count >= MaxSettings) {
				tooMany = !_tooManySettingsSaid;
				_tooManySettingsSaid = true;
			} else {
				_declared.Add(setting with { Label = Clip(setting.Label), Help = Clip(setting.Help), Default = setting.Default ?? "" });
			}
		}

		if (tooMany) {
			NocatFarm.Log.Warn(new Said("plugin {0} added more than {1} settings - the rest are left out", _owner, MaxSettings));
		}
	}

	public string Setting(string name) => Value(name, null);

	public string Setting(string name, string account) => Value(name, account);

	/// <summary>The key a value is kept under: the name, or "account:name" for one account's (a name can't hold a colon).</summary>
	internal static string Key(string name, string? account) => string.IsNullOrEmpty(account) ? name : $"{account}:{name}";

	private PluginSetting? Declaration(string name) => _declared.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

	private string Value(string name, string? account) {
		lock (_valuesGate) {
			ReadValues();
			PluginSetting? d = Declaration(name);

			if (!_values.TryGetValue(Key(name, account), out string? v)) {
				return d?.Default ?? "";
			}

			// 1.9.0: a secret is kept encrypted, like the app's own, and handed over as plain text.
			return d?.Kind == PluginSettingKind.Secret ? Secrets.Unprotect(v) : v;
		}
	}

	/// <summary>
	/// Everything the operator can edit for this plugin, with its current value - for the page. A secret's value is never
	/// handed out: "set" or empty. Per-account settings come once per account, with the account's name.
	/// </summary>
	internal IReadOnlyList<(PluginSetting Setting, string Value, string? Account)> SettingsView(IReadOnlyList<string> accounts) {
		lock (_valuesGate) {
			ReadValues();
			List<(PluginSetting, string, string?)> view = [];

			foreach (PluginSetting d in _declared) {
				foreach (string? account in d.PerAccount ? accounts.Select(static a => (string?) a) : [null]) {
					string value = Value(d.Name, account);
					view.Add((d, d.Kind == PluginSettingKind.Secret ? (value.Length > 0 ? "set" : "") : value, account));
				}
			}

			return view;
		}
	}

	internal bool IsPerAccount(string name) {
		lock (_valuesGate) {
			return Declaration(name)?.PerAccount == true;
		}
	}

	internal void SetValue(string name, string value, string? account = null) {
		lock (_valuesGate) {
			ReadValues();
			_values[Key(name, account)] = Declaration(name)?.Kind == PluginSettingKind.Secret ? Secrets.Protect(value, "plugin") : value;

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
				AtomicFile.Write(SettingsFile, System.Text.Json.JsonSerializer.Serialize(_values));
			} catch (Exception e) {
				NocatFarm.Log.Warn(new Said("couldn't save {0}'s settings: {1}", _owner, NocatFarm.Log.Scrub(e.Message)));
			}
		}
	}

	/// <summary>Read the settings file again at the next look - after a restore put another one there.</summary>
	internal void ForgetValues() {
		lock (_valuesGate) {
			_values = new(StringComparer.OrdinalIgnoreCase);
			_valuesRead = false;
		}
	}

	private string SettingsFile => Path.Combine(ConfigStore.ConfigDir, "plugins", $"{Sanitise(_owner)}.settings.json");

	/// <summary>Called under <see cref="_valuesGate"/>. Marked read only once the values are in: marked first, a second caller
	/// went on with the empty set while the file was still being read.</summary>
	private void ReadValues() {
		if (_valuesRead) {
			return;
		}

		try {
			if (File.Exists(SettingsFile)) {
				// Read into one that ignores case, as it was made: a plain deserialise gave back one that didn't, so a setting
				// asked for as "every" found "Every" until the first restart and never after.
				Dictionary<string, string> read = new(StringComparer.OrdinalIgnoreCase);

				foreach ((string key, string? value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(SettingsFile)) ?? []) {
					// A null (a hand edit) is no value: the default, not a null handed to the plugin as its text.
					if (value != null) {
						read[key] = value;
					}
				}

				_values = read;
			}
		} catch (Exception e) {
			NocatFarm.Log.Warn(new Said("couldn't read {0}'s settings: {1}", _owner, NocatFarm.Log.Scrub(e.Message)));

			// Kept aside, because the next change saves over it - and would save only that one setting.
			try {
				File.Copy(SettingsFile, SettingsFile + ".broken", true);
			} catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) {
				// the warning above still stands - but the copy the next save would overwrite isn't kept
				NocatFarm.Log.Failed($"plugin {_owner}: keeping the unreadable settings aside", copy);
			}
		} finally {
			_valuesRead = true;
		}
	}

	public string? GetSetting(string account, string setting) {
		Bot? bot = mgr.Get(account);
		SettingDef? def = Config.Settings.FindBot(setting);

		return (bot == null) || (def == null) ? null : Config.Settings.Show(bot.Cfg, def)?.ToString();
	}

	private string StateFile => Path.Combine(ConfigStore.ConfigDir, "plugins", $"{Sanitise(_owner)}.json");

	public async Task SaveStateAsync(string json) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
			await AtomicFile.WriteAsync(StateFile, json).ConfigureAwait(false);
		} catch (Exception e) {
			NocatFarm.Log.Warn(new Said("plugin {0} couldn't save its state: {1}", _owner, NocatFarm.Log.Scrub(e.Message)));
		}
	}

	public async Task<string?> LoadStateAsync() {
		try {
			return File.Exists(StateFile) ? await File.ReadAllTextAsync(StateFile).ConfigureAwait(false) : null;
		} catch (Exception e) {
			NocatFarm.Log.Warn(new Said("plugin {0} couldn't read its state: {1}", _owner, NocatFarm.Log.Scrub(e.Message)));

			return null;
		}
	}

	/// <summary>A plugin's name becomes a file name, so it must not be able to escape the folder.</summary>
	private static string Sanitise(string name) {
		foreach (char bad in Path.GetInvalidFileNameChars()) {
			name = name.Replace(bad, '_');
		}

		return name.Length == 0 ? "plugin" : name;
	}

	public void AddCommand(string verb, string usage, string help, Func<string[], Task<string>> handler) {
		if (string.IsNullOrWhiteSpace(verb) || (handler == null)) {
			return;
		}

		verb = verb.Trim().ToLowerInvariant();

		// One word that can be typed: a space, a line break or a thousand characters in it could never be, and went on every
		// help page as it was.
		if (!VerbRx().IsMatch(verb)) {
			NocatFarm.Log.Warn(new Said("plugin command '{0}' ignored - a command is one word of letters, digits and dashes", Clip(verb.Length > 40 ? verb[..40] : verb)));

			return;
		}

		bool tooMany = false;

		// Under the lock the reads take: a plugin adding commands from its own thread while a command line looked them up
		// threw "collection was modified" at whoever was typing - or broke the dictionary for good.
		lock (_commandsGate) {
			// A plugin must not be able to shadow a built-in. Quietly winning the name would mean `stop` doing
			// something other than stopping, which is the worst possible surprise.
			if (NocatFarm.Commands.All.Any(c => c.Matches(verb)) || _commands.ContainsKey(verb)) {
				NocatFarm.Log.Warn(new Said("plugin command '{0}' ignored - that name is taken", verb));

				return;
			}

			if (_commands.Count >= MaxCommands) {
				tooMany = !_tooManyCommandsSaid;
				_tooManyCommandsSaid = true;
			} else {
				string owner = _owner, name = verb;
				_commands[verb] = (Clip(usage), Clip(help), words => PluginHost.RunCommandAsync(owner, name, handler, words));
			}
		}

		if (tooMany) {
			NocatFarm.Log.Warn(new Said("plugin {0} added more than {1} commands - the rest are left out", _owner, MaxCommands));
		}
	}

	private readonly Lock _commandsGate = new();

	[System.Text.RegularExpressions.GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}_-]{0,31}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex VerbRx();

	// ── 1.9.0 ───────────────────────────────────────────────────────────────
	public string Language => Live.Global.Language ?? "en";

	public PluginCommentCheck CanComment(string account) {
		Comments.PluginComments();

		return Comments.Check(mgr.Get(account));
	}

	public bool SetSetting(string name, string value, string? account = null) {
		PluginSetting? declared;

		lock (_valuesGate) {
			declared = Declaration(name);
		}

		if ((declared == null) || (declared.PerAccount == string.IsNullOrEmpty(account))) {
			return false;
		}

		string? whose = null;

		if (declared.PerAccount) {
			if (mgr.Get(account!) is not { } bot) {
				return false;
			}

			whose = bot.Name;
		}

		SetValue(declared.Name, value ?? "", whose);
		NocatFarm.Log.Info(whose == null
			? new Said("plugin {0}: {1} = {2}", _owner, declared.Name, declared.Kind == PluginSettingKind.Secret ? "••••••" : value)
			: new Said("plugin {0}: {1} = {2} for {3}", _owner, declared.Name, declared.Kind == PluginSettingKind.Secret ? "••••••" : value, whose));

		return true;
	}

	public async Task<PluginCommentResult> PostProfileCommentAsync(string account, ulong targetSteamId, string text, CancellationToken ct = default) {
		Comments.PluginComments();

		try {
			return await Comments.PostAsync(mgr.Get(account), _owner, targetSteamId, text, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// Handed back, not thrown: a plugin's loop that didn't expect it would stop for good.
			NocatFarm.Log.Failed($"plugin {_owner}: posting a comment", e, account);
			NocatFarm.Log.StackToFile(e, account);

			return new PluginCommentResult(PluginCommentOutcome.Unknown, NocatFarm.Log.Describe(e));
		}
	}

	internal (string Title, Func<string?, Task<PluginPage>> Render)? Page { get; private set; }

	public void AddPage(string title, Func<string?, Task<PluginPage>> render) {
		if (!string.IsNullOrWhiteSpace(title) && (render != null)) {
			Page = (title.Trim(), render);
		}
	}

	public void Notify(string message, string? account = null) {
		if (!string.IsNullOrWhiteSpace(message) && MayWrite()) {
			NocatFarm.Log.Reward(new Said("[plugin] {0}", Clip(message)), string.IsNullOrEmpty(account) ? "plugins" : Clip(account), Topic.Plugins);
		}
	}

	public event Action<IPluginAccount>? AccountOnline;
	public event Action<IPluginAccount>? AccountOffline;
	public event Action<IPluginAccount, uint, int>? CardDropped;
	public event Action<IPluginAccount, int>? TradeOffersWaiting;

	internal void RaiseOnline(Bot bot) => Safely(AccountOnline, h => h(new AccountView(bot)), nameof(AccountOnline));
	internal void RaiseOffline(Bot bot) => Safely(AccountOffline, h => h(new AccountView(bot)), nameof(AccountOffline));

	internal void RaiseCardDropped(Bot bot, uint app, int left) =>
		Safely(CardDropped, h => h(new AccountView(bot), app, left), nameof(CardDropped));

	internal void RaiseTradeOffers(Bot bot, int waiting) =>
		Safely(TradeOffersWaiting, h => h(new AccountView(bot), waiting), nameof(TradeOffersWaiting));

	/// <summary>How many plugin failures were caught and logged rather than let loose - for the tests.</summary>
	internal static int Caught;

	/// <summary>
	/// Every handler on its own, each under a <see cref="PluginSync"/>. One after another, so one that throws doesn't
	/// keep the rest from hearing it.
	/// </summary>
	/// <remarks>
	/// The events are plain Actions, so <c>host.AccountOnline += async a =&gt; { await ...; throw ...; }</c> compiles to
	/// async void - and an async void that throws after its first await rethrows on the thread pool, which ends the
	/// process: every account dropped, and a crash the updater takes for a bad release. The try/catch here only ever
	/// saw the part before the await. An async void hands its failure to the SynchronizationContext it started under,
	/// so it starts under one that catches, and says whose plugin it was. Nothing changes for the plugin.
	/// </remarks>
	/// <remarks>
	/// 1.9.0: and not on the caller's thread. These are raised from an account's Steam callbacks and its module loops, and a
	/// handler that blocked - a Thread.Sleep, a .Result on a slow website - held that account still: no more callbacks, no
	/// more drops, an account that looked signed in and did nothing. Each plugin's events now go one after another, in the
	/// order they happened, on a thread of their own; a plugin stuck in one stops hearing new ones (dropped past
	/// <see cref="MaxQueuedEvents"/>, said once) and nothing else waits for it.
	/// </remarks>
	private void Safely<T>(T? handlers, Action<T> raise, string which) where T : Delegate {
		if (handlers == null) {
			return;
		}

		if (Interlocked.Increment(ref _queuedEvents) > MaxQueuedEvents) {
			Interlocked.Decrement(ref _queuedEvents);

			if (!_queueFullSaid) {
				_queueFullSaid = true;
				NocatFarm.Log.Warn(new Said("plugin {0} hasn't finished with its events for a long time - new ones are dropped until it does", _owner));
			}

			return;
		}

		PluginSync sync = new(_owner, which);
		T[] each = [.. handlers.GetInvocationList().Cast<T>()];

		lock (_eventsGate) {
			_events = _events.ContinueWith(_ => {
				try {
					foreach (T handler in each) {
						sync.Run(_ => raise(handler), null);
					}
				} finally {
					if (Interlocked.Decrement(ref _queuedEvents) == 0) {
						_queueFullSaid = false;
					}
				}
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
		}
	}

	private readonly Lock _eventsGate = new();
	private Task _events = Task.CompletedTask;
	private int _queuedEvents;

	/// <summary>For the tests: until every event raised so far has been handled (or the time is up).</summary>
	internal bool EventsSettled(TimeSpan within) {
		Task last;

		lock (_eventsGate) {
			last = _events;
		}

		return last.Wait(within);
	}

	/// <summary>Where a plugin's handlers - and whatever an async one does after its awaits - run: failures logged, never thrown.</summary>
	internal sealed class PluginSync(string owner, string which) : SynchronizationContext {
		public override void Post(SendOrPostCallback d, object? state) => ThreadPool.UnsafeQueueUserWorkItem(_ => Run(d, state), null);

		public override void Send(SendOrPostCallback d, object? state) => Run(d, state);

		public override SynchronizationContext CreateCopy() => this;

		internal void Run(SendOrPostCallback d, object? state) {
			SynchronizationContext? was = Current;
			SetSynchronizationContext(this);

			try {
				d(state);
			} catch (Exception e) {
				Interlocked.Increment(ref Caught);

				// A few times an hour at most: a handler that throws at every event (every card, every sign-in) wrote its
				// warning and whole stack each time.
				if (PluginHost.MaySay($"{owner}:{which}")) {
					NocatFarm.Log.Warn(new Said("plugin {0} failed on {1}: {2}: {3}", owner, which, e.GetType().Name, NocatFarm.Log.Scrub(e.Message)));

					// Where in the plugin, for whoever wrote it.
					NocatFarm.Log.StackToFile(e);
				}
			} finally {
				SetSynchronizationContext(was);
			}
		}
	}

	/// <summary>A snapshot of one account. Deliberately a copy of facts, not a handle on the thing itself.</summary>
	private sealed class AccountView(Bot bot) : IPluginAccount {
		public string Name => bot.Name;
		public ulong SteamId => bot.SteamId;
		public bool IsOnline => bot.IsOnline;
		public string Persona => bot.PersonaWord;
		public string Status => bot.StatusText ?? "";
		public IReadOnlyList<uint> Playing => bot.PlayingApps;
		public int CardsRemaining => bot.CardsRemaining;
		public bool IsPaused => bot.Paused || !bot.Cfg.Enabled || bot.Stopping;

		public IReadOnlyList<PluginGame> Library =>
			[.. bot.Library.Games.Select(static g => new PluginGame(g.AppId, g.Name, g.MinutesPlayed, g.Shared))];

		public IReadOnlyList<PluginGameValue> InventoryByGame =>
			[.. bot.Inventory.ByGame.Select(static v => new PluginGameValue(v.AppId, v.Game, v.Items, v.Value))];
	}
}
