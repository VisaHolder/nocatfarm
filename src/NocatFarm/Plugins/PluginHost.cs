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
	private static BotManager? _mgr;

	private sealed record Loaded(INocatPlugin Plugin, string File, Host Host);

	/// <summary>What is loaded, for the `plugins` command.</summary>
	public static IReadOnlyList<(string Name, string Version, string File)> Running =>
		[.. Plugins.Select(static p => (p.Plugin.Name, p.Plugin.Version, Path.GetFileName(p.File)))];

	public static string Folder => Path.Combine(ConfigStore.Root, "plugins");

	public static async Task LoadAllAsync(BotManager mgr, CancellationToken ct) {
		if (!Live.Global.PluginsEnabled) {
			return;
		}

		Seen.Clear();
		Directory.CreateDirectory(Folder);

		string[] files = Directory.GetFiles(Folder, "*.dll", SearchOption.TopDirectoryOnly);

		if (files.Length == 0) {
			NocatFarm.Log.Debug(new Said("plugins: nothing in {0}", Folder));

			return;
		}

		_mgr = mgr;

		foreach (string file in files) {
			await LoadOneAsync(file, ct).ConfigureAwait(false);
		}

		if (Plugins.Count > 0) {
			NocatFarm.Log.Info(new Said("{0} plugin(s) loaded - type 'plugins' to see them", Plugins.Count));
		}
	}

	private static async Task LoadOneAsync(string file, CancellationToken ct) {
		try {
			// A collectible context per DLL, so a plugin's own dependencies do not collide with another's - and
			// so the whole thing is at least structurally unloadable later.
			PluginContext context = new(file);
			Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));

			foreach (Type type in assembly.GetTypes()) {
				if (!typeof(INocatPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) {
					continue;
				}

				if (Activator.CreateInstance(type) is not INocatPlugin plugin) {
					continue;
				}

				// Off individually, not just all-or-nothing: one plugin misbehaving should not mean turning the
				// whole feature off to be rid of it.
				bool off = Live.Global.DisabledPlugins.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase);

				Seen.Add((plugin.Name, plugin.Version, Path.GetFileName(file), !off));

				if (off) {
					NocatFarm.Log.Info(new Said("plugin {0} is switched off - skipping it", plugin.Name));

					continue;
				}

				try {
					Host host = new(_mgr!, plugin.Name);

					await plugin.OnLoadAsync(host, ct).ConfigureAwait(false);
					Hosts.Add(host);
					Plugins.Add(new Loaded(plugin, file, host));
					NocatFarm.Log.Good(new Said("plugin loaded: {0} {1}", plugin.Name, plugin.Version));
				} catch (Exception e) {
					NocatFarm.Log.Warn(new Said("plugin {0} failed while loading and has been left out: {1}: {2}", plugin.Name, e.GetType().Name, e.Message));
				}
			}
		} catch (ReflectionTypeLoadException e) {
			// The usual cause is a plugin built against a different nocat.farm. Say so rather than printing a
			// wall of loader exceptions.
			NocatFarm.Log.Warn(new Said("plugins: couldn't read {0} - it was probably built against a different version. ({1})", Path.GetFileName(file), e.LoaderExceptions.FirstOrDefault()?.Message));
		} catch (Exception e) {
			NocatFarm.Log.Warn(new Said("plugins: couldn't load {0}: {1}: {2}", Path.GetFileName(file), e.GetType().Name, e.Message));
		}
	}

	public static async Task UnloadAllAsync() {
		foreach (Loaded loaded in Plugins) {
			try {
				await loaded.Plugin.OnUnloadAsync().ConfigureAwait(false);
			} catch (Exception e) {
				NocatFarm.Log.Debug(new Said("plugin {0} threw on unload: {1}", loaded.Plugin.Name, e.Message));
			}
		}

		Plugins.Clear();
		Hosts.Clear();
	}

	// ── the events, raised by the app ─────────────────────────────────────────
	// Every one is a no-op when plugins are off, so the call sites do not have to care.

	public static void RaiseOnline(Bot bot) => Each(h => h.RaiseOnline(bot));
	public static void RaiseOffline(Bot bot) => Each(h => h.RaiseOffline(bot));
	public static void RaiseCardDropped(Bot bot, uint app, int left) => Each(h => h.RaiseCardDropped(bot, app, left));
	public static void RaiseTradeOffers(Bot bot, int waiting) => Each(h => h.RaiseTradeOffers(bot, waiting));

	private static void Each(Action<Host> raise) {
		// Snapshot: a handler is free to do anything, including something that ends up unloading a plugin.
		foreach (Host host in Hosts.ToArray()) {
			raise(host);
		}
	}

	/// <summary>A command a plugin added, kept apart from the built-ins.</summary>
	public static IReadOnlyDictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> Commands {
		get {
			Dictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> all = new(StringComparer.OrdinalIgnoreCase);

			foreach (Host host in Hosts) {
				foreach ((string verb, (string Usage, string Help, Func<string[], Task<string>> Run) command) in host.Commands) {
					all.TryAdd(verb, command);
				}
			}

			return all;
		}
	}

	/// <summary>A plugin's own settings, with current values, for the dashboard.</summary>
	public static IReadOnlyList<(PluginSetting Setting, string Value)> SettingsOf(string plugin) =>
		Plugins.FirstOrDefault(p => string.Equals(p.Plugin.Name, plugin, StringComparison.OrdinalIgnoreCase))?.Host.SettingsView() ?? [];

	/// <summary>Change one. Returns false when there is no such plugin or setting.</summary>
	public static bool SetSetting(string plugin, string name, string value) {
		Loaded? loaded = Plugins.FirstOrDefault(p => string.Equals(p.Plugin.Name, plugin, StringComparison.OrdinalIgnoreCase));

		if ((loaded == null) || !loaded.Host.Declared.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) {
			return false;
		}

		loaded.Host.SetValue(name, value);
		NocatFarm.Log.Info(new Said("plugin {0}: {1} = {2}", plugin, name, value));

		return true;
	}

	/// <summary>Every plugin found on disk, whether it is switched on or not - for the dashboard's list.</summary>
	public static IReadOnlyList<(string Name, string Version, string File, bool Enabled)> Discovered => Seen;

	private static readonly List<(string Name, string Version, string File, bool Enabled)> Seen = [];

	private sealed class PluginContext(string file) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(file), true) {
		private readonly AssemblyDependencyResolver _resolver = new(file);

		protected override Assembly? Load(AssemblyName name) {
			// Anything the app already has - nocatFarm itself, SteamKit - must resolve to the LOADED copy, or a
			// plugin ends up holding types that are not the same types the app is using, and every cast fails
			// for reasons that look like magic.
			string? path = _resolver.ResolveAssemblyToPath(name);

			return path == null ? null : LoadFromAssemblyPath(path);
		}
	}
}
