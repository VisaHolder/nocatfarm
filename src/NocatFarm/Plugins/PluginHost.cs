using System.Reflection;
using System.Runtime.Loader;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Plugins;

/// <summary>
/// Finding, loading and running plugins.
/// </summary>
/// <remarks>
/// Every .dll directly inside <c>plugins/</c> is examined for public types implementing <see cref="INocatPlugin"/>.
/// One folder, no recursion, no manifest: a plugin is a DLL you put somewhere, and anything more elaborate is
/// ceremony for a feature most people will never turn on.
///
/// Off by default, and the switch says why. A plugin is somebody else's code running inside the process that
/// holds your Steam sessions - that is not a reason never to have plugins, it is a reason the operator should
/// have decided to run them rather than discovering they can.
///
/// Failures are contained at every step: a DLL that will not load, a type that will not construct, a plugin
/// that throws on load or in a handler. Each one costs that plugin and nothing else, because a farm that stops
/// at 3am because a third-party DLL threw is a worse outcome than the plugin simply not working.
/// </remarks>
public static class PluginHost {
	private static readonly List<Loaded> Plugins = [];
	private static readonly List<Host> Hosts = [];
	private static readonly Lock HostsGate = new();
	private static BotManager? _mgr;

	/// <summary>A loaded plugin. Its name and version as read once, at load: a plugin's own Name that threw or hung later took the
	/// Plugins page and every command line with it.</summary>
	private sealed record Loaded(INocatPlugin Plugin, string File, Host Host, string Name, string Version);

	/// <summary>What is loaded, for the `plugins` command.</summary>
	public static IReadOnlyList<(string Name, string Version, string File)> Running =>
		[.. Snapshot().Select(static p => (p.Name, p.Version, Path.GetFileName(p.File)))];

	/// <summary>
	/// The loaded plugins as they are this moment. Every read goes through this (and every write through the lock): the
	/// dashboard is up before the plugins load, and a look at the Plugins page while they were being added to the list
	/// could throw "collection was modified" - or read half of it.
	/// </summary>
	private static Loaded[] Snapshot() {
		lock (HostsGate) {
			return [.. Plugins];
		}
	}

	public static string Folder => Path.Combine(ConfigStore.Root, "plugins");

	public static async Task LoadAllAsync(BotManager mgr, CancellationToken ct) {
		// 1.9.0: never the end of the start. A plugins folder that couldn't be read or made - a file of that name, a folder
		// this user may not open - threw out of here, and nocat.farm crashed at every start until somebody found out why.
		try {
			await LoadEveryAsync(mgr, ct).ConfigureAwait(false);
		} catch (Exception e) when (e is not OperationCanceledException) {
			NocatFarm.Log.Warn(new Said("plugins couldn't be loaded from {0}: {1} - starting without them", Folder, NocatFarm.Log.Describe(e)));
			NocatFarm.Log.StackToFile(e);
		}
	}

	private static async Task LoadEveryAsync(BotManager mgr, CancellationToken ct) {
		_mgr = mgr;
		SettlePending(Folder);

		if (!Live.Global.PluginsEnabled) {
			return;
		}

		lock (HostsGate) {
			Seen.Clear();
		}

		Directory.CreateDirectory(Folder);

		string[] files = Directory.GetFiles(Folder, "*.dll", SearchOption.TopDirectoryOnly);

		if (files.Length == 0) {
			NocatFarm.Log.Debug(new Said("plugins: nothing in {0}", Folder));

			return;
		}

		_mgr = mgr;

		foreach (string file in files) {
			// An official add-on that is switched off isn't loaded at all: its name is known from the signed list, so its file
			// needn't be opened to find out - and a file never loaded can be updated or removed while the app runs.
			if (Addons.SwitchedOff(Path.GetFileName(file)) is { } off) {
				lock (HostsGate) {
					Seen.Add((off.Plugin, Addons.VersionOnDisk(file) ?? "", Path.GetFileName(file), false));
				}

				NocatFarm.Log.Info(new Said("plugin {0} is switched off - skipping it", off.Plugin));

				continue;
			}

			await LoadOneAsync(file, ct).ConfigureAwait(false);
		}

		if (Snapshot().Length is > 0 and int loaded) {
			NocatFarm.Log.Info(new Said("{0} plugin(s) loaded - type 'plugins' to see them", loaded));
		}
	}

	private static async Task LoadOneAsync(string file, CancellationToken ct) {
		try {
			// A collectible context per DLL, so a plugin's own dependencies do not collide with another's - and
			// so the whole thing is at least structurally unloadable later.
			PluginContext context = new(file);
			Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));

			// From here the file is mapped into the process (on Windows it can't be replaced or deleted until the app closes),
			// whatever comes of it - a plugin switched off, one that fails to load, a DLL with no plugin in it at all.
			MarkMapped(Path.GetFileName(file));

			foreach (Type type in assembly.GetTypes()) {
				if (!typeof(INocatPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) {
					continue;
				}

				await StartAsync(type, file, ct).ConfigureAwait(false);
			}
		} catch (ReflectionTypeLoadException e) {
			// The usual cause is a plugin built against a different nocat.farm. Say so rather than printing a
			// wall of loader exceptions.
			NocatFarm.Log.Warn(new Said("couldn't load plugin {0} - built for another version?", Path.GetFileName(file)));
			NocatFarm.Log.Debug(new Said("plugin {0}: {1}", Path.GetFileName(file), NocatFarm.Log.Scrub(e.LoaderExceptions.FirstOrDefault()?.Message)));
		} catch (Exception e) {
			NocatFarm.Log.Warn(new Said("plugins: couldn't load {0}: {1}: {2}", Path.GetFileName(file), e.GetType().Name, NocatFarm.Log.Scrub(e.Message)));
			NocatFarm.Log.StackToFile(e);
		}
	}

	/// <summary>How long a plugin has to construct itself and load - start-up waits on it.</summary>
	internal static TimeSpan LoadLimit = TimeSpan.FromSeconds(15);

	/// <summary>How long a plugin's own command has to answer before the command line is handed back.</summary>
	internal static TimeSpan CommandLimit = TimeSpan.FromMinutes(3);

	/// <summary>How long a plugin's page has to draw itself.</summary>
	internal static TimeSpan PageLimit = TimeSpan.FromSeconds(30);

	/// <summary>
	/// One plugin type: made, named, loaded. Every part of it is plugin code, and every part is bounded: made and loaded off
	/// this thread (<see cref="OffThread"/>), within <see cref="LoadLimit"/>, its name read once.
	/// </summary>
	/// <remarks>
	/// Awaited on this thread, a plugin whose constructor or OnLoadAsync blocked before its first await - a Thread.Sleep, a
	/// .Result on a website - held nocat.farm's start there for good: the 15 seconds only counted once a Task came back, which
	/// it never did. And an async void it started from OnLoadAsync that threw ended the process.
	/// </remarks>
	internal static async Task StartAsync(Type type, string file, CancellationToken ct) {
		string shown = type.Name;
		INocatPlugin? plugin;
		string name, version;

		try {
			(plugin, name, version) = await OffThread(shown, "start", () => {
				INocatPlugin? made = Activator.CreateInstance(type) as INocatPlugin;

				return Task.FromResult((made, made == null ? "" : NameOf(made.Name, type.Name), made == null ? "" : Clip(made.Version, 32)));
			}).WaitAsync(LoadLimit, ct).ConfigureAwait(false);
		} catch (Exception e) when ((e is not OperationCanceledException) || !ct.IsCancellationRequested) {
			NocatFarm.Log.Warn(new Said("plugin {0} failed to load: {1}: {2}", shown, Unwrapped(e).GetType().Name, NocatFarm.Log.Scrub(Unwrapped(e).Message)));
			NocatFarm.Log.StackToFile(e);

			return;
		}

		if (plugin == null) {
			return;
		}

		// Off individually, not just all-or-nothing: one plugin misbehaving should not mean turning the
		// whole feature off to be rid of it.
		bool off = Live.Global.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
		Loaded? twin = Snapshot().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

		lock (HostsGate) {
			Seen.Add((name, version, Path.GetFileName(file), !off));
		}

		if (off) {
			NocatFarm.Log.Info(new Said("plugin {0} is switched off - skipping it", name));

			return;
		}

		// The same plugin twice - a second copy of its DLL ("rep4rep (1).dll" from a browser): both ran, every account's loop
		// twice, both saving over one settings file. The first one found stays.
		if (twin != null) {
			NocatFarm.Log.Warn(new Said("plugin {0} is loaded already (from {1}) - the copy in {2} is left out", name, Path.GetFileName(twin.File), Path.GetFileName(file)));

			return;
		}

		try {
			Host host = new(_mgr!, name);

			// A time limit, because start-up waits on this: a plugin whose load hangs on a network call held
			// every account back from signing in, behind a window that looked perfectly fine.
			using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
			limit.CancelAfter(LoadLimit);
			await OffThread(name, nameof(INocatPlugin.OnLoadAsync), async () => {
				await plugin.OnLoadAsync(host, limit.Token).ConfigureAwait(false);

				return true;
			}).WaitAsync(LoadLimit, ct).ConfigureAwait(false);

			lock (HostsGate) {
				Hosts.Add(host);
				Plugins.Add(new Loaded(plugin, file, host, name, version));
			}

			NocatFarm.Log.Good(new Said("plugin loaded: {0} {1}", name, version));
		} catch (Exception e) {
			NocatFarm.Log.Warn(new Said("plugin {0} failed to load: {1}: {2}", name, e.GetType().Name, NocatFarm.Log.Scrub(e.Message)));
			NocatFarm.Log.StackToFile(e);   // where in the plugin, for whoever wrote it
		}
	}

	/// <summary>A plugin's name, fit to be one: its type's name when it gave none, no control characters, at most 64 long.</summary>
	internal static string NameOf(string? name, string fallback) {
		string n = new string([.. (name ?? "").Where(static c => !char.IsControl(c))]).Trim();

		return Clip(n.Length > 0 ? n : fallback, 64);
	}

	private static string Clip(string? text, int most) => string.IsNullOrEmpty(text) ? "" : text.Length <= most ? text : text[..most];

	private static Exception Unwrapped(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? inner : e;

	/// <summary>
	/// Plugin code, run on the thread pool under a <see cref="Host.PluginSync"/>: never on the caller's thread, so a plugin that
	/// blocks holds only itself up; and an async void it starts that throws is logged as its failure, not the end of the
	/// process. What it returns (or throws) comes back in the task - bound it with WaitAsync.
	/// </summary>
	internal static Task<T> OffThread<T>(string owner, string which, Func<Task<T>> run) =>
		Task.Run(() => {
			SynchronizationContext? was = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(new Host.PluginSync(owner, which));

			try {
				return run() ?? throw new InvalidOperationException("the plugin handed back no task");
			} finally {
				SynchronizationContext.SetSynchronizationContext(was);
			}
		});

	/// <summary>
	/// A plugin's own command, run as <see cref="OffThread"/> and given <see cref="CommandLimit"/> to answer. Past that the
	/// command line is handed back (the command may well carry on): one that never answered held the console, or a dashboard
	/// request, for good. What it answers is cut to a size a chat can take.
	/// </summary>
	internal static async Task<string> RunCommandAsync(string owner, string verb, Func<string[], Task<string>> handler, string[] words) {
		Task<string> running = OffThread(owner, verb, () => handler(words));

		try {
			string? said = await running.WaitAsync(CommandLimit).ConfigureAwait(false);

			return Clip(said, 64 * 1024);
		} catch (TimeoutException) {
			return new Said("'{0}' (plugin {1}) hasn't answered in {2} seconds - it may still be working on it", verb, owner, (int) CommandLimit.TotalSeconds).ToString();
		}
	}

	public static async Task UnloadAllAsync() {
		// Detach first, then tell each plugin. Accounts are still running at this point and keep raising events;
		// a plugin that has been told to unload must not get another one mid-teardown.
		Loaded[] unloading;

		lock (HostsGate) {
			unloading = [.. Plugins];
			Hosts.Clear();
		}

		foreach (Loaded loaded in unloading) {
			try {
				// Bounded: a plugin that hangs on the way out must not keep the whole app from closing.
				// Off this thread too: one that blocked before handing back a task held closing up for good.
				await OffThread(loaded.Name, nameof(INocatPlugin.OnUnloadAsync), async () => {
					await loaded.Plugin.OnUnloadAsync().ConfigureAwait(false);

					return true;
				}).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			} catch (TimeoutException) {
				NocatFarm.Log.Debug(new Said("plugin {0} took too long to unload - closing without it", loaded.Name));
			} catch (Exception e) {
				NocatFarm.Log.Debug(new Said("plugin {0} threw on unload: {1}", loaded.Name, NocatFarm.Log.Describe(e)));
			}
		}

		lock (HostsGate) {
			Plugins.Clear();
		}
	}

	// ── the events, raised by the app ─────────────────────────────────────────
	// Every one is a no-op when plugins are off, so the call sites do not have to care.

	public static void RaiseOnline(Bot bot) => Each(h => h.RaiseOnline(bot));
	public static void RaiseOffline(Bot bot) => Each(h => h.RaiseOffline(bot));
	public static void RaiseCardDropped(Bot bot, uint app, int left) => Each(h => h.RaiseCardDropped(bot, app, left));
	public static void RaiseTradeOffers(Bot bot, int waiting) => Each(h => h.RaiseTradeOffers(bot, waiting));

	private static void Each(Action<Host> raise) {
		// Snapshot: a handler is free to do anything, including something that ends up unloading a plugin.
		Host[] hosts;

		lock (HostsGate) {
			hosts = [.. Hosts];
		}

		foreach (Host host in hosts) {
			raise(host);
		}
	}

	/// <summary>A command a plugin added, kept apart from the built-ins.</summary>
	public static IReadOnlyDictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> Commands {
		get {
			Dictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> all = new(StringComparer.OrdinalIgnoreCase);

			Host[] hosts;

			lock (HostsGate) {
				hosts = [.. Hosts];
			}

			foreach (Host host in hosts) {
				foreach ((string verb, (string Usage, string Help, Func<string[], Task<string>> Run) command) in host.Commands) {
					all.TryAdd(verb, command);
				}
			}

			return all;
		}
	}

	/// <summary>
	/// Whether a command line is one of this plugin's own commands - what a button on its page may run. A page is drawn from
	/// whatever the plugin has (text from a website, say), so its buttons are kept to the plugin's own verbs: never 'set',
	/// 'send' or 'remove', whatever the page says.
	/// </summary>
	internal static bool OwnsCommand(string plugin, string line) => Find(plugin) is { } loaded && IsOwnCommand(loaded.Host, line);

	/// <summary>Whether the line's first word is a verb this plugin added (<see cref="OwnsCommand"/>).</summary>
	internal static bool IsOwnCommand(Host host, string? line) {
		string verb = NocatFarm.Commands.Unprefixed(NocatFarm.Commands.Words(line ?? "").FirstOrDefault() ?? "");

		return (verb.Length > 0) && host.Commands.ContainsKey(verb);
	}

	/// <summary>A plugin's own settings, with current values, for the dashboard (per-account ones once per account).</summary>
	public static IReadOnlyList<(PluginSetting Setting, string Value, string? Account)> SettingsOf(string plugin) =>
		Find(plugin)?.Host.SettingsView([.. _mgr?.All.Select(static b => b.Name) ?? []]) ?? [];

	private static Loaded? Find(string plugin) =>
		Snapshot().FirstOrDefault(p => string.Equals(p.Name, plugin, StringComparison.OrdinalIgnoreCase));

	/// <summary>Change one (for one account, when it is per account). Returns false when there is no such plugin, setting or account.</summary>
	public static bool SetSetting(string plugin, string name, string value, string? account = null) {
		Loaded? loaded = Find(plugin);
		PluginSetting? declared = loaded?.Host.Declared.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

		if ((loaded == null) || (declared == null)) {
			return false;
		}

		// A per-account one needs an account that exists - written under a made-up name it would sit in the file for nobody.
		if (declared.PerAccount != !string.IsNullOrEmpty(account)) {
			return false;
		}

		if (declared.PerAccount) {
			if (_mgr?.Get(account!) is not { } bot) {
				return false;
			}

			account = bot.Name;
		}

		loaded.Host.SetValue(declared.Name, value, account);

		// A secret's value stays out of the log.
		string shown = declared.Kind == PluginSettingKind.Secret ? (value.Length > 0 ? "••••••" : "") : value;
		NocatFarm.Log.Info(account == null
			? new Said("plugin {0}: {1} = {2}", plugin, name, shown)
			: new Said("plugin {0}: {1} = {2} for {3}", plugin, name, shown, account));

		return true;
	}

	/// <summary>1.9.0: every plugin that has a dashboard page, and its title.</summary>
	public static IReadOnlyList<(string Plugin, string Title)> Pages =>
		[.. Snapshot().Where(static p => p.Host.Page != null).Select(static p => (p.Name, p.Host.Page!.Value.Title))];

	/// <summary>A plugin's page as it is now. Null when there's no such page; a page that throws comes back as one line saying so.</summary>
	public static async Task<PluginPage?> RenderAsync(string plugin, string? view) {
		if (Find(plugin)?.Host.Page is not { } page) {
			return null;
		}

		try {
			// Off the dashboard's thread, as every other call into a plugin: a page that blocked held a request thread at each
			// look, and the dashboard's polling soon had none left.
			return Tidy(await OffThread(plugin, nameof(IPluginHost.AddPage), () => page.Render(view)).WaitAsync(PageLimit).ConfigureAwait(false));
		} catch (Exception e) {
			// A few times an hour at most: the page is looked at again every few seconds while its tab is open, and each look
			// wrote the warning and its whole stack again - the page itself says it each time.
			if (MaySay($"{plugin}:{nameof(IPluginHost.AddPage)}")) {
				NocatFarm.Log.Warn(new Said("plugin {0} failed on {1}: {2}: {3}", plugin, nameof(IPluginHost.AddPage), e.GetType().Name, NocatFarm.Log.Scrub(e.Message)));
				NocatFarm.Log.StackToFile(e);
			}

			return new PluginPage([PluginBlock.Line(new Said("this page couldn't be drawn: {0}", NocatFarm.Log.Describe(e)).ToString())]);
		}
	}

	/// <summary>How many of one plugin's failures of one kind (a page, an event) are written down in an hour; the rest only counted.</summary>
	internal const int FailuresSaidAnHour = 5;

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime From, int Said)> FailuresSaid = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Whether this failure of a plugin's is written to the log: <see cref="FailuresSaidAnHour"/> an hour for each plugin and kind
	/// of call. One that failed at every look or every event wrote a warning and its whole stack each time - the log was nothing
	/// else. Kept by plugin and kind only, so it stays as small as the list of plugins.
	/// </summary>
	internal static bool MaySay(string key) {
		DateTime now = DateTime.UtcNow;
		(DateTime From, int Said) next = FailuresSaid.AddOrUpdate(key, (now, 1),
			(_, was) => now - was.From >= TimeSpan.FromHours(1) ? (now, 1) : (was.From, was.Said + 1));

		return next.Said <= FailuresSaidAnHour;
	}

	/// <summary>The most blocks, rows and cells a plugin's page shows, and the longest text in one.</summary>
	internal const int MaxBlocks = 200, MaxRows = 2000, MaxCells = 20, MaxText = 4000;

	/// <summary>
	/// A plugin's page as the dashboard can draw it: nothing null where something was promised, and no bigger than the caps
	/// above. A page of a million rows - or a null list - was a dashboard tab that never came back, or a failure at every look.
	/// </summary>
	internal static PluginPage Tidy(PluginPage? page) {
		static string T(string? s) => Clip(s, MaxText);
		static PluginButton? B(PluginButton? b) => b == null ? null : new PluginButton(T(b.Label), T(b.Command), b.View == null ? null : T(b.View));

		List<PluginBlock> blocks = [];

		foreach (PluginBlock? b in (page?.Blocks ?? []).Take(MaxBlocks)) {
			if (b == null) {
				continue;
			}

			blocks.Add(new PluginBlock(b.Kind, T(b.Text),
				b.Columns == null ? null : [.. b.Columns.Take(MaxCells).Select(T)],
				b.Rows == null ? null : [.. b.Rows.Where(static r => r != null).Take(MaxRows).Select(r => new PluginRow([.. (r.Cells ?? []).Take(MaxCells).Select(T)], B(r.Button)))],
				b.Buttons == null ? null : [.. b.Buttons.Where(static x => x != null).Take(MaxCells).Select(x => B(x)!)],
				T(b.Url)));
		}

		return new PluginPage(blocks);
	}

	// ── 1.9.0: installs and removals that wait for a restart ─────────────────
	// A loaded plugin's DLL is held open (on Windows it can't be replaced or deleted while the app runs), so an install over
	// one goes in beside it as <file>.new and an uninstall leaves <file>.remove - both settled here, before anything loads.

	internal const string NewSuffix = ".new", RemoveSuffix = ".remove";

	/// <summary>Put waiting installs in place and carry out waiting removals. Safe to run with plugins off.</summary>
	internal static void SettlePending(string folder) {
		if (!Directory.Exists(folder)) {
			return;
		}

		foreach (string marker in Directory.GetFiles(folder, "*.dll" + RemoveSuffix, SearchOption.TopDirectoryOnly)) {
			string dll = marker[..^RemoveSuffix.Length];

			try {
				File.Delete(dll);
				File.Delete(dll + NewSuffix);
				File.Delete(marker);
				NocatFarm.Log.Info(new Said("add-on removed: {0}", Path.GetFileName(dll)));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				NocatFarm.Log.Warn(new Said("couldn't remove {0}: {1}", Path.GetFileName(dll), NocatFarm.Log.Scrub(e.Message)));
			}
		}

		// Half-written copies left by an install that was cut off (closed, killed, the power gone mid-download): never loaded,
		// never finished - only taking up room. The install that wrote one never said it was done, so it's simply asked for again.
		foreach (string half in Directory.GetFiles(folder, "*.dll*.tmp", SearchOption.TopDirectoryOnly)) {
			try {
				File.Delete(half);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// next start
			}
		}

		foreach (string waiting in Directory.GetFiles(folder, "*.dll" + NewSuffix, SearchOption.TopDirectoryOnly)) {
			string dll = waiting[..^NewSuffix.Length];

			try {
				File.Move(waiting, dll, true);
				NocatFarm.Log.Info(new Said("add-on installed: {0}", Path.GetFileName(dll)));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				NocatFarm.Log.Warn(new Said("couldn't put {0} in place: {1}", Path.GetFileName(dll), NocatFarm.Log.Scrub(e.Message)));
			}
		}
	}

	/// <summary>
	/// Whether a file of this name is held by the app right now (so it can't be replaced or deleted until a restart): every
	/// DLL loaded into a context, not only the plugins that started. A switched-off plugin's or a failed one's file is just as
	/// held - asked of the started ones alone, an update over it or its removal went straight at the file and was refused.
	/// </summary>
	internal static bool IsLoaded(string file) {
		lock (HostsGate) {
			if (Mapped.Contains(file)) {
				return true;
			}
		}

		return Snapshot().Any(p => string.Equals(Path.GetFileName(p.File), file, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>Every DLL file name loaded into a context this run. Never cleared: a collectible context isn't unloaded, so the file stays held.</summary>
	private static readonly HashSet<string> Mapped = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Note a file as loaded into the process (and the checks' way to say one is).</summary>
	internal static void MarkMapped(string file) {
		lock (HostsGate) {
			Mapped.Add(file);
		}
	}

	/// <summary>For the checks: forget which files were loaded.</summary>
	internal static void ForgetMapped() {
		lock (HostsGate) {
			Mapped.Clear();
		}
	}

	/// <summary>
	/// After a backup is restored: every loaded plugin reads its settings file again, so the restored values are what it sees -
	/// and the next change is made to them, not saved over them from what was in memory.
	/// </summary>
	internal static void ReloadSettings() {
		foreach (Loaded loaded in Snapshot()) {
			loaded.Host.ForgetValues();
		}
	}

	/// <summary>Every plugin found on disk, whether it is switched on or not - for the dashboard's list.</summary>
	public static IReadOnlyList<(string Name, string Version, string File, bool Enabled)> Discovered {
		get {
			lock (HostsGate) {
				return [.. Seen];
			}
		}
	}

	private static readonly List<(string Name, string Version, string File, bool Enabled)> Seen = [];

	private sealed class PluginContext(string file) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(file), true) {
		private readonly AssemblyDependencyResolver _resolver = new(file);

		protected override Assembly? Load(AssemblyName name) {
			// Anything the app already has - nocatFarm itself, SteamKit - must resolve to the LOADED copy, or a
			// plugin ends up holding types that are not the same types the app is using, and every cast fails
			// for reasons that look like magic. Asked first, not after the plugin's folder: with no .deps.json the resolver
			// takes every DLL beside the plugin as its own, and a copy of nocatFarm.dll left there by the build made the
			// plugin's INocatPlugin a different type from the app's - so it was never found, and nothing said why.
			if (AssemblyLoadContext.Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))) {
				return null;
			}

			string? path = _resolver.ResolveAssemblyToPath(name);

			return path == null ? null : LoadFromAssemblyPath(path);
		}
	}
}
