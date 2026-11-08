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
internal sealed class Host(BotManager mgr, string owner) : IPluginHost {
	private readonly string _owner = owner;

	private readonly Dictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> _commands =
		new(StringComparer.OrdinalIgnoreCase);

	internal IReadOnlyDictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> Commands => _commands;

	public string AppVersion => Build.Version;

	public IReadOnlyList<IPluginAccount> Accounts => [.. mgr.All.Select(static b => new AccountView(b))];

	public IPluginAccount? Account(string name) => mgr.Get(name) is { } bot ? new AccountView(bot) : null;

	public void Log(string message) => NocatFarm.Log.Info(new Said("[plugin] {0}", message));

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
		lock (_valuesGate) {
			if (string.IsNullOrWhiteSpace(setting.Name) || _declared.Any(d => string.Equals(d.Name, setting.Name, StringComparison.OrdinalIgnoreCase))) {
				return;
			}

			_declared.Add(setting);
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

				foreach ((string key, string value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) ?? []) {
					read[key] = value;
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
		if (string.IsNullOrWhiteSpace(verb)) {
			return;
		}

		verb = verb.Trim().ToLowerInvariant();

		// A plugin must not be able to shadow a built-in. Quietly winning the name would mean `stop` doing
		// something other than stopping, which is the worst possible surprise.
		if (NocatFarm.Commands.All.Any(c => c.Matches(verb)) || _commands.ContainsKey(verb)) {
			NocatFarm.Log.Warn(new Said("plugin command '{0}' ignored - that name is taken", verb));

			return;
		}

		_commands[verb] = (usage, help, handler);
	}

	// ── 1.9.0 ───────────────────────────────────────────────────────────────
	public string Language => Live.Global.Language ?? "en";

	public PluginCommentCheck CanComment(string account) => Comments.Check(mgr.Get(account));

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
		if (!string.IsNullOrWhiteSpace(message)) {
			NocatFarm.Log.Reward(new Said("[plugin] {0}", message), string.IsNullOrEmpty(account) ? "plugins" : account, Topic.Plugins);
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
	private void Safely<T>(T? handlers, Action<T> raise, string which) where T : Delegate {
		if (handlers == null) {
			return;
		}

		PluginSync sync = new(_owner, which);

		foreach (T handler in handlers.GetInvocationList().Cast<T>()) {
			sync.Run(_ => raise(handler), null);
		}
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
				NocatFarm.Log.Warn(new Said("plugin {0} failed on {1}: {2}: {3}", owner, which, e.GetType().Name, NocatFarm.Log.Scrub(e.Message)));

				// Where in the plugin, for whoever wrote it.
				NocatFarm.Log.StackToFile(e);
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
