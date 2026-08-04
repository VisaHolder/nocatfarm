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

	public void Log(string message) => NocatFarm.Log.Info($"[plugin] {message}");

	public Task<string> RunCommandAsync(string line) => Commands_RunAsync(line);

	private async Task<string> Commands_RunAsync(string line) {
		try {
			return await NocatFarm.Commands.RunAsync(mgr, line).ConfigureAwait(false);
		} catch (Exception e) {
			return $"failed: {e.GetType().Name}: {e.Message}";
		}
	}

	private readonly List<PluginSetting> _declared = [];
	private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
	private bool _valuesRead;

	internal IReadOnlyList<PluginSetting> Declared => _declared;

	public void AddSetting(PluginSetting setting) {
		if (string.IsNullOrWhiteSpace(setting.Name) || _declared.Any(d => string.Equals(d.Name, setting.Name, StringComparison.OrdinalIgnoreCase))) {
			return;
		}

		_declared.Add(setting);
	}

	public string Setting(string name) {
		ReadValues();

		return _values.TryGetValue(name, out string? v)
			? v
			: _declared.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Default ?? "";
	}

	/// <summary>Everything the operator can edit for this plugin, with its current value - for the page.</summary>
	internal IReadOnlyList<(PluginSetting Setting, string Value)> SettingsView() {
		ReadValues();

		return [.. _declared.Select(d => (d, Setting(d.Name)))];
	}

	internal void SetValue(string name, string value) {
		ReadValues();
		_values[name] = value;

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
			File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(_values));
		} catch (Exception e) {
			NocatFarm.Log.Warn($"couldn't save {_owner}'s settings: {e.Message}");
		}
	}

	private string SettingsFile => Path.Combine(ConfigStore.ConfigDir, "plugins", $"{Sanitise(_owner)}.settings.json");

	private void ReadValues() {
		if (_valuesRead) {
			return;
		}

		_valuesRead = true;

		try {
			if (File.Exists(SettingsFile)) {
				_values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile))
					?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			}
		} catch (Exception e) {
			NocatFarm.Log.Warn($"couldn't read {_owner}'s settings: {e.Message}");
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
			await File.WriteAllTextAsync(StateFile, json).ConfigureAwait(false);
		} catch (Exception e) {
			NocatFarm.Log.Warn($"plugin {_owner} couldn't save its state: {e.Message}");
		}
	}

	public async Task<string?> LoadStateAsync() {
		try {
			return File.Exists(StateFile) ? await File.ReadAllTextAsync(StateFile).ConfigureAwait(false) : null;
		} catch (Exception e) {
			NocatFarm.Log.Warn($"plugin {_owner} couldn't read its state: {e.Message}");

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
			NocatFarm.Log.Warn($"plugins: a command called '{verb}' already exists - the plugin's version was ignored");

			return;
		}

		_commands[verb] = (usage, help, handler);
	}

	public event Action<IPluginAccount>? AccountOnline;
	public event Action<IPluginAccount>? AccountOffline;
	public event Action<IPluginAccount, uint, int>? CardDropped;
	public event Action<IPluginAccount, int>? TradeOffersWaiting;

	internal void RaiseOnline(Bot bot) => Safely(() => AccountOnline?.Invoke(new AccountView(bot)), nameof(AccountOnline));
	internal void RaiseOffline(Bot bot) => Safely(() => AccountOffline?.Invoke(new AccountView(bot)), nameof(AccountOffline));

	internal void RaiseCardDropped(Bot bot, uint app, int left) =>
		Safely(() => CardDropped?.Invoke(new AccountView(bot), app, left), nameof(CardDropped));

	internal void RaiseTradeOffers(Bot bot, int waiting) =>
		Safely(() => TradeOffersWaiting?.Invoke(new AccountView(bot), waiting), nameof(TradeOffersWaiting));

	private static void Safely(Action raise, string which) {
		try {
			raise();
		} catch (Exception e) {
			NocatFarm.Log.Warn($"a plugin threw handling {which} and was ignored: {e.GetType().Name}: {e.Message}");
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

		public IReadOnlyList<PluginGame> Library =>
			[.. bot.Library.Games.Select(static g => new PluginGame(g.AppId, g.Name, g.MinutesPlayed, g.Shared))];

		public IReadOnlyList<PluginGameValue> InventoryByGame =>
			[.. bot.Inventory.ByGame.Select(static v => new PluginGameValue(v.AppId, v.Game, v.Items, v.Value))];
	}
}
