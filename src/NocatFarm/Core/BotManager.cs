using System.Collections.Concurrent;
using NocatFarm.Config;
using NocatFarm.Modules;
using NocatFarm.Rep4Rep;

namespace NocatFarm.Core;

/// <summary>Owns every configured account and the wiring of their modules.</summary>
public sealed class BotManager : IAsyncDisposable {
	private readonly ConcurrentDictionary<string, Bot> _bots = new(StringComparer.OrdinalIgnoreCase);
	private readonly Lock _adding = new();

	/// <summary>Accounts being removed: gone from the list, their file not deleted yet. Only touched under <see cref="_adding"/>.</summary>
	private readonly HashSet<string> _removing = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Stands in for an account's <see cref="Bot.CfgGate"/> when its file has no account running on it.</summary>
	private static readonly Lock NoBotGate = new();

	/// <summary>
	/// The lock to hold while reading, changing and saving <paramref name="name"/>'s config: its account's own, or one
	/// shared by files nothing runs yet (an import writing a brand new one).
	/// </summary>
	public static Lock GateFor(string name) => Instance?.Get(name)?.CfgGate ?? NoBotGate;

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

		foreach (string name in onDisk.Keys) {
			if (_bots.TryGetValue(name, out Bot? existing)) {
				// Read again under the account's lock. The copy above was read for every account at once, before any of
				// them was put in, so a save made in between (a dashboard save, 'set', a learned ban) was already newer
				// than it - put in, it undid that change here and at the account's next save on disk too.
				lock (existing.CfgGate) {
					if (ConfigStore.LoadBot(existing.Name) is { } fresh) {
						existing.Reconfigure(fresh);
					}
				}

				continue;
			}

			// Under the same lock as AddAsync, so an account being added right now isn't replaced by a second copy - nor
			// one being removed brought back from the file it hasn't deleted yet.
			lock (_adding) {
				// And read again here: a 'remove' that finished after the read above has deleted the file this came from, and
				// an account put back for it would run with no config behind it.
				if (!_bots.ContainsKey(name) && !_removing.Contains(name) && (ConfigStore.LoadBot(name) is { } fresh)) {
					Bot bot = new(name, fresh);
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

	/// <summary>
	/// Every account thrown away and built again from the files on disk - for a restore. Reconfiguring the ones already
	/// here would keep what each has in memory (today's plan, the hunt, the rep4rep counts), and the next save would put
	/// that back over what was just restored. Nothing is deleted: this only lets go of the objects.
	/// </summary>
	public async Task ReplaceAllFromDiskAsync() {
		foreach (string name in _bots.Keys.ToArray()) {
			if (_bots.TryRemove(name, out Bot? b)) {
				await b.DisposeAsync().ConfigureAwait(false);
			}
		}

		await SyncFromDiskAsync().ConfigureAwait(false);
	}

	/// <summary>Flush anything held in memory. Called on the way out so a clean exit loses nothing.</summary>
	public static void Flush() {
		Lifetime.Save();
		GameCatalog.Flush();   // the store catalogue saves on a timer, so a clean exit shouldn't drop the tail of it
		PriceBook.Save();      // ditto the market prices, which are slow and rate-limited to re-fetch
		InventoryHistory.Save();
		History.Save();        // the day-by-day totals write once a minute; this keeps the last minute of them
		KeyQueue.Flush();      // keys pasted or used in the last moments, which are worth money
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
		&& All.All(IsFinished);

	/// <summary>
	/// Nothing more will happen on this account by itself. A failed account only counts once it has given up: a sign-in
	/// that failed and is about to retry is "failed" for a moment too, and closing the app right then ended a run that
	/// was a reconnect away from carrying on.
	/// </summary>
	internal static bool IsFinished(Bot b) => !b.Cfg.Enabled || (b.State == BotState.Stopped) || b.GaveUp;

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

	/// <summary>Add a brand new account: writes its config and brings it up.</summary>
	public async Task<Bot?> AddAsync(string name, BotConfig cfg) {
		Bot bot;

		// Check and add in one step. A double-click on "Add" (or the dashboard and a phone command together) both found
		// the name free and both started an account: the second replaced the first in the list, and the first carried on
		// signed in to the same Steam account with nothing left able to stop it.
		lock (_adding) {
			if (_bots.ContainsKey(name) || _removing.Contains(name)) {
				return null;
			}

			// Not added when it isn't on disk. A file it couldn't save over (open somewhere else) was still there, and the
			// account ran on whatever that old file said - and came back as that at the next start.
			if (!ConfigStore.SaveBot(name, cfg)) {
				Log.Warn(new Said("not added - its settings couldn't be saved, so nothing was started. Try again in a moment"), name);

				return null;
			}

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
		Bot? bot;

		// Taken out of the list and marked as going in one step, under the lock AddAsync and a reload use. In between, an
		// import or 'reload' found its file still there and brought it back, or an Add of the same name wrote a new file
		// that this then deleted - an account running with no config behind it.
		lock (_adding) {
			if (!_bots.TryRemove(name, out bot)) {
				return false;
			}

			_removing.Add(bot.Name);
		}

		try {
			await bot.DisposeAsync().ConfigureAwait(false);   // dispose, not just stop - frees its HttpClient/locks

			// Its own name, not however it was typed: the lookup ignores case, and off Windows "MAIN" left main.json and its
			// login token behind, so the account came back at the next start.
			lock (_adding) {
				TokenStore.Clear(bot.Name);
				// Its config before the rest: a reload reading it right now writes its overnight mark and then takes it back
				// if the config is gone (see ConfigStore.ReadBot) - and if it isn't gone yet, the mark is written before the
				// rest go, and goes with them.
				bool deleted = ConfigStore.DeleteBot(bot.Name);
				// And the rest of its own files, in any case - left, they came back with the account, and an account added
				// again as "Main" after "main" had its state twice, so backups were refused off Windows.
				ConfigStore.DeleteAccountFiles(bot.Name);

				return deleted;
			}
		} finally {
			lock (_adding) {
				_removing.Remove(bot.Name);
			}
		}
	}

	public async ValueTask DisposeAsync() {
		await StopAllAsync().ConfigureAwait(false);
		Rep4Rep.Dispose();
	}
}
