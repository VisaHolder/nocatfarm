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
internal sealed class Host(BotManager mgr) : IPluginHost {
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
	}
}
