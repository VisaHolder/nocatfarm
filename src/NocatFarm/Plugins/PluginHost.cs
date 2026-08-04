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
	private static Host? _host;

	private sealed record Loaded(INocatPlugin Plugin, string File);

	/// <summary>What is loaded, for the `plugins` command.</summary>
	public static IReadOnlyList<(string Name, string Version, string File)> Running =>
		[.. Plugins.Select(static p => (p.Plugin.Name, p.Plugin.Version, Path.GetFileName(p.File)))];

	public static string Folder => Path.Combine(ConfigStore.Root, "plugins");

	public static async Task LoadAllAsync(BotManager mgr, CancellationToken ct) {
		if (!Live.Global.PluginsEnabled) {
			return;
		}

		Directory.CreateDirectory(Folder);

		string[] files = Directory.GetFiles(Folder, "*.dll", SearchOption.TopDirectoryOnly);

		if (files.Length == 0) {
			NocatFarm.Log.Debug($"plugins: nothing in {Folder}");

			return;
		}

		_host = new Host(mgr);

		foreach (string file in files) {
			await LoadOneAsync(file, ct).ConfigureAwait(false);
		}

		if (Plugins.Count > 0) {
			NocatFarm.Log.Info($"{Plugins.Count} plugin(s) loaded - type 'plugins' to see them");
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

				try {
					await plugin.OnLoadAsync(_host!, ct).ConfigureAwait(false);
					Plugins.Add(new Loaded(plugin, file));
					NocatFarm.Log.Good($"plugin loaded: {plugin.Name} {plugin.Version}");
				} catch (Exception e) {
					NocatFarm.Log.Warn($"plugin {plugin.Name} failed while loading and has been left out: {e.GetType().Name}: {e.Message}");
				}
			}
		} catch (ReflectionTypeLoadException e) {
			// The usual cause is a plugin built against a different nocat.farm. Say so rather than printing a
			// wall of loader exceptions.
			NocatFarm.Log.Warn($"plugins: couldn't read {Path.GetFileName(file)} - it was probably built against a different version. ({e.LoaderExceptions.FirstOrDefault()?.Message})");
		} catch (Exception e) {
			NocatFarm.Log.Warn($"plugins: couldn't load {Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
		}
	}

	public static async Task UnloadAllAsync() {
		foreach (Loaded loaded in Plugins) {
			try {
				await loaded.Plugin.OnUnloadAsync().ConfigureAwait(false);
			} catch (Exception e) {
				NocatFarm.Log.Debug($"plugin {loaded.Plugin.Name} threw on unload: {e.Message}");
			}
		}

		Plugins.Clear();
	}

	// ── the events, raised by the app ─────────────────────────────────────────
	// Every one is a no-op when plugins are off, so the call sites do not have to care.

	public static void RaiseOnline(Bot bot) => _host?.RaiseOnline(bot);
	public static void RaiseOffline(Bot bot) => _host?.RaiseOffline(bot);
	public static void RaiseCardDropped(Bot bot, uint app, int left) => _host?.RaiseCardDropped(bot, app, left);
	public static void RaiseTradeOffers(Bot bot, int waiting) => _host?.RaiseTradeOffers(bot, waiting);

	/// <summary>A command a plugin added, kept apart from the built-ins.</summary>
	public static IReadOnlyDictionary<string, (string Usage, string Help, Func<string[], Task<string>> Run)> Commands =>
		_host?.Commands ?? new Dictionary<string, (string, string, Func<string[], Task<string>>)>(StringComparer.OrdinalIgnoreCase);

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
