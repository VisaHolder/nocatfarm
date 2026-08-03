using System.Collections.Concurrent;
using NocatFarm.Config;
using NocatFarm.Modules;
using NocatFarm.Rep4Rep;

namespace NocatFarm.Core;

/// <summary>Owns every configured account and the wiring of their modules.</summary>
public sealed class BotManager : IAsyncDisposable {
	private readonly ConcurrentDictionary<string, Bot> _bots = new(StringComparer.OrdinalIgnoreCase);

	public GlobalConfig Global { get; private set; }

	/// <summary>One rep4rep client for the whole process - one account, one token, many Steam profiles.</summary>
	public Rep4RepApi Rep4Rep { get; } = new();

	public BotManager(GlobalConfig global) {
		Global = global;
		ApplyGlobal(global);   // one path, so nothing is applied only on a later save
	}

	/// <summary>
	/// Every account, in the order the user arranged them.
	///
	/// Sorting here rather than in each view is what makes one drag on the Accounts page reorder the window's
	/// account sheet, the console board and the dashboard together - all four read this. Anything missing from
	/// the saved order (a brand new account) sorts after the arranged ones, alphabetically, so it appears at the
	/// end rather than jumping into the middle.
	/// </summary>
	public IReadOnlyCollection<Bot> All {
		get {
			List<string> order = Config.Live.Global.AccountOrder;

			if (order.Count == 0) {
				return _bots.Values.OrderBy(static b => b.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			}

			Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < order.Count; i++) {
				rank.TryAdd(order[i], i);
			}

			return _bots.Values
				.OrderBy(b => rank.TryGetValue(b.Name, out int i) ? i : int.MaxValue)
				.ThenBy(static b => b.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
	}

	public Bot? Get(string name) => _bots.TryGetValue(name, out Bot? b) ? b : null;

	public void ApplyGlobal(GlobalConfig g) {
		Global = g;
		Live.Global = g;
		Rep4Rep.Token = g.Rep4RepApiToken;
		CardFarmer.ApplyConcurrencyLimit(g.MaxConcurrentFarming);
	}

	/// <summary>Find a module of a given type on a bot, e.g. its rep4rep module.</summary>
	public static T? ModuleOf<T>(Bot bot) where T : class, IBotModule => bot.Modules.OfType<T>().FirstOrDefault();

	/// <summary>Load every bot config from disk, creating and removing bots to match.</summary>
	public async Task SyncFromDiskAsync() {
		Dictionary<string, BotConfig> onDisk = ConfigStore.LoadBots();

		foreach (string gone in _bots.Keys.Where(k => !onDisk.ContainsKey(k)).ToArray()) {
			if (_bots.TryRemove(gone, out Bot? b)) {
				Log.Info("config removed - stopping", gone);
				await b.DisposeAsync().ConfigureAwait(false);   // dispose, not just stop - frees its HttpClient/locks
			}
		}

		foreach ((string name, BotConfig cfg) in onDisk) {
			if (_bots.TryGetValue(name, out Bot? existing)) {
				existing.Reconfigure(cfg);

				continue;
			}

			Bot bot = new(name, cfg);
			Wire(bot);
			_bots[name] = bot;
		}
	}

	private void Wire(Bot bot) {
		// Order is the order they appear in the dashboard, not a priority: the card farmer claims the account by
		// setting Bot.IsFarming, and the idler stands off while that flag is up.
		bot.AddModule(new CardFarmer(bot));
		bot.AddModule(new HumanMode(bot));
		bot.AddModule(new Idler(bot));
		bot.AddModule(new FreeGames(bot));
		bot.AddModule(new BadgeCraft(bot));
		bot.AddModule(new Rep4RepModule(bot, Rep4Rep));
		bot.AddModule(new Social(bot));
		bot.AddModule(new GroupJoin(bot));
		bot.AddModule(new Trading(bot));
		bot.AddModule(new AchievementPacer(bot));
		bot.AddModule(new AchievementBoost(bot));
		bot.AddModule(new Heartbeat(bot));
	}

	/// <summary>Flush anything held in memory. Called on the way out so a clean exit loses nothing.</summary>
	public static void Flush() => Lifetime.Save();

	/// <summary>Start every enabled bot, staggered so several logins don't hit Steam at once.</summary>
	public async Task StartAllAsync() {
		bool first = true;

		foreach (Bot bot in All) {
			if (!bot.Cfg.Enabled) {
				Log.Info("disabled in config - not starting", bot.Name);

				continue;
			}

			if (!first) {
				await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Global.LoginStaggerSeconds))).ConfigureAwait(false);
			}

			first = false;
			await bot.StartAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// True when every configured account is enabled-but-stopped or disabled, i.e. there is nothing left to do.
	/// Used by ExitWhenAllFinished so a finite run can actually end instead of idling in the tray.
	/// </summary>
	public bool AllFinished =>
		(_bots.Count > 0)
		&& All.All(static b => !b.Cfg.Enabled || b.State is Core.BotState.Stopped or Core.BotState.Failed);

	public async Task StopAllAsync(bool graceful = false) {
		if (graceful) {
			// Wind the legit accounts down together, not one after another, so a "stop all" doesn't take the
			// sum of every account's finishing-up delay.
			await Task.WhenAll(All.Select(b => b.StopAsync(true))).ConfigureAwait(false);

			return;
		}

		foreach (Bot bot in All) {
			await bot.StopAsync().ConfigureAwait(false);
		}
	}

	public async Task<bool> StartAsync(string name) {
		Bot? b = Get(name);

		if (b == null) {
			return false;
		}

		await b.StartAsync().ConfigureAwait(false);

		return true;
	}

	public async Task<bool> StopAsync(string name) {
		Bot? b = Get(name);

		if (b == null) {
			return false;
		}

		await b.StopAsync().ConfigureAwait(false);

		return true;
	}

	/// <summary>Add a brand new account: writes its config and brings it up.</summary>
	public async Task<Bot?> AddAsync(string name, BotConfig cfg) {
		if (_bots.ContainsKey(name)) {
			return null;
		}

		ConfigStore.SaveBot(name, cfg);
		Bot bot = new(name, cfg);
		Wire(bot);
		_bots[name] = bot;

		if (cfg.Enabled) {
			await bot.StartAsync().ConfigureAwait(false);
		}

		return bot;
	}

	public async Task<bool> RemoveAsync(string name) {
		if (!_bots.TryRemove(name, out Bot? bot)) {
			return false;
		}

		await bot.DisposeAsync().ConfigureAwait(false);   // dispose, not just stop - frees its HttpClient/locks
		TokenStore.Clear(name);

		return ConfigStore.DeleteBot(name);
	}

	public async ValueTask DisposeAsync() {
		await StopAllAsync().ConfigureAwait(false);
		Rep4Rep.Dispose();
	}
}
