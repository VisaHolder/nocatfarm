using System.Collections.Concurrent;
using NocatFarm.Config;
using NocatFarm.Modules;
using NocatFarm.Rep4Rep;

namespace NocatFarm.Core;

/// <summary>Owns every configured account and the wiring of their modules.</summary>
public sealed class BotManager : IAsyncDisposable {
	private readonly ConcurrentDictionary<string, Bot> _bots = new(StringComparer.OrdinalIgnoreCase);
	private readonly Lock _adding = new();

	public GlobalConfig Global { get; private set; }

	/// <summary>One rep4rep client for the whole process - one account, one token, many Steam profiles.</summary>
	public Rep4RepApi Rep4Rep { get; } = new();

	/// <summary>
	/// The running manager, for the few jobs that belong to the FLEET rather than to one account - working the
	/// shared key queue, say, which several accounts racing each other would only turn into wasted activations.
	/// </summary>
	public static BotManager? Instance { get; private set; }

	public BotManager(GlobalConfig global) {
		Instance = this;
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

		// Only accounts whose file is really gone. A file that is there but didn't parse was skipped by LoadBots with a
		// warning; removing the account for that would stop it over one stray comma.
		foreach (string gone in _bots.Keys.Where(k => !onDisk.ContainsKey(k) && !File.Exists(Path.Combine(ConfigStore.ConfigDir, k + ".json"))).ToArray()) {
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

			// Under the same lock as AddAsync, so an account being added right now isn't replaced by a second copy.
			lock (_adding) {
				if (!_bots.ContainsKey(name)) {
					Bot bot = new(name, cfg);
					Wire(bot);
					_bots[name] = bot;
				}
			}
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
		bot.AddModule(new Sender(bot));
		bot.AddModule(new Gifts(bot));
		bot.AddModule(new Boosters(bot));
		bot.AddModule(new EventItems(bot));
		bot.AddModule(new BanWatch(bot));
		bot.AddModule(new DuplicateSeller(bot));
		bot.AddModule(new AchievementPacer(bot));
		bot.AddModule(new AchievementBoost(bot));
		bot.AddModule(new Upkeep(bot));
		bot.AddModule(new Heartbeat(bot));
	}

	/// <summary>Flush anything held in memory. Called on the way out so a clean exit loses nothing.</summary>
	public static void Flush() {
		Lifetime.Save();
		GameCatalog.Flush();   // the store catalogue saves on a timer, so a clean exit shouldn't drop the tail of it
		PriceBook.Save();      // ditto the market prices, which are slow and rate-limited to re-fetch
		InventoryHistory.Save();
		History.Save();        // the day-by-day totals write once a minute; this keeps the last minute of them
	}

	/// <summary>Start every enabled bot, staggered so several logins don't hit Steam at once.</summary>
	public async Task StartAllAsync() {
		// Every account starts now and queues for its login slot (Limiters.WaitForLoginSlotAsync), spaced by "Gap
		// between logins" in list order. Started one after another, each waited out the one before it silently -
		// the first two said "starting up" together and the third only half a minute later, which looked broken.
		List<Task> starting = [];

		foreach (Bot bot in All) {
			if (!bot.Cfg.Enabled) {
				Log.Info("disabled in config - not starting", bot.Name);

				continue;
			}

			starting.Add(bot.StartAsync());

			// A moment apart, so they join the queue in list order.
			await Task.Delay(100).ConfigureAwait(false);
		}

		await Task.WhenAll(starting).ConfigureAwait(false);
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
		Bot bot;

		// Check and add in one step. A double-click on "Add" (or the dashboard and a phone command together) both found
		// the name free and both started an account: the second replaced the first in the list, and the first carried on
		// signed in to the same Steam account with nothing left able to stop it.
		lock (_adding) {
			if (_bots.ContainsKey(name)) {
				return null;
			}

			ConfigStore.SaveBot(name, cfg);
			bot = new(name, cfg);
			Wire(bot);
			_bots[name] = bot;
		}

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

		// Its own name, not however it was typed: the lookup ignores case, and off Windows "MAIN" left main.json and its
		// login token behind, so the account came back at the next start.
		TokenStore.Clear(bot.Name);

		return ConfigStore.DeleteBot(bot.Name);
	}

	public async ValueTask DisposeAsync() {
		await StopAllAsync().ConfigureAwait(false);
		Rep4Rep.Dispose();
	}
}
