namespace NocatFarm.Plugins;

/// <summary>
/// What a plugin is handed, and everything it can reach.
/// </summary>
/// <remarks>
/// This file is the whole contract. A plugin references nocatFarm.dll, implements <see cref="INocatPlugin"/>,
/// and is given an <see cref="IPluginHost"/> - nothing else is public to it.
///
/// The narrowness is the point. A plugin runs IN THIS PROCESS, so it could in principle do anything the app can
/// do, including reading the Steam refresh tokens under config/. Nothing here can stop a determined DLL - only
/// not running it can - but an API that hands out the Bot object makes reaching those tokens the path of least
/// resistance, and an API that hands out a read-only view of an account makes it something you have to go out
/// of your way to do. So: no Steam client, no web session, no config object, no tokens. Accounts appear as
/// names and states; actions happen by asking the host, which does them the same way a typed command would and
/// logs them the same way too.
///
/// Plugins are off by default and stay off until somebody turns them on, having been told plainly what running
/// one means.
/// </remarks>
public interface INocatPlugin {
	/// <summary>Shown in the log and by the `plugins` command. Keep it short.</summary>
	string Name { get; }

	/// <summary>Your own version, for your own sake when somebody reports a bug.</summary>
	string Version { get; }

	/// <summary>
	/// Called once, after the app has started and before any account signs in.
	///
	/// Register commands and subscribe to events here. Throwing is caught and disables the plugin rather than
	/// taking the app down - a broken plugin should cost you the plugin, not the night's farming.
	/// </summary>
	Task OnLoadAsync(IPluginHost host, CancellationToken ct);

	/// <summary>Called on shutdown. Optional - the default does nothing.</summary>
	Task OnUnloadAsync() => Task.CompletedTask;
}

/// <summary>One account, as a plugin sees it: what it is and what it's doing, never how to become it.</summary>
public interface IPluginAccount {
	/// <summary>The name in the config, e.g. "main". Not the Steam login.</summary>
	string Name { get; }

	ulong SteamId { get; }
	bool IsOnline { get; }

	/// <summary>The persona Steam is showing for it, or empty when it isn't signed in.</summary>
	string Persona { get; }

	/// <summary>What it is doing in one line, the same text the status table shows.</summary>
	string Status { get; }

	/// <summary>AppIDs it is currently telling Steam it is playing.</summary>
	IReadOnlyList<uint> Playing { get; }

	/// <summary>Cards still to drop across the library, or -1 when it hasn't looked yet.</summary>
	int CardsRemaining { get; }
}

/// <summary>Everything a plugin can ask the app to do.</summary>
public interface IPluginHost {
	/// <summary>Which nocat.farm this is, so a plugin can refuse to load against one it doesn't know.</summary>
	string AppVersion { get; }

	/// <summary>Every configured account, whether signed in or not.</summary>
	IReadOnlyList<IPluginAccount> Accounts { get; }

	/// <summary>One account by config name, or null.</summary>
	IPluginAccount? Account(string name);

	/// <summary>
	/// Write to the log, tagged with your plugin's name so it is obvious where a line came from.
	/// </summary>
	void Log(string message);

	/// <summary>
	/// Run a command exactly as if it had been typed, and get back what would have been printed.
	///
	/// This is deliberately the ONLY way a plugin changes anything. Everything the app can do already has a
	/// command, every command already validates its input and logs what it did, and routing plugins through
	/// the same door means a plugin cannot reach a state the operator could not have reached themselves - and
	/// that whatever it did is in the log, in the operator's own vocabulary.
	/// </summary>
	Task<string> RunCommandAsync(string line);

	/// <summary>
	/// Add a command of your own. The verb must not already exist.
	///
	/// It shows up in `help` alongside the built-ins, prefixed so nobody wonders where it came from.
	/// </summary>
	void AddCommand(string verb, string usage, string help, Func<string[], Task<string>> handler);

	/// <summary>An account finished signing in.</summary>
	event Action<IPluginAccount>? AccountOnline;

	/// <summary>An account went offline, whether deliberately or not.</summary>
	event Action<IPluginAccount>? AccountOffline;

	/// <summary>A trading card dropped. The appID it dropped from, and how many are left in that game.</summary>
	event Action<IPluginAccount, uint, int>? CardDropped;

	/// <summary>
	/// Steam says trade offers are waiting on this account, and how many.
	///
	/// A count, not an offer - that is all Steam's push carries. Reading the offers themselves is a web request
	/// the app makes on its own schedule, so a plugin that wants the detail should react to this by asking, not
	/// expect it to be handed over.
	/// </summary>
	event Action<IPluginAccount, int>? TradeOffersWaiting;
}
