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

	/// <summary>Everything it owns that can be launched, with Steam's own playtime.</summary>
	IReadOnlyList<PluginGame> Library { get; }

	/// <summary>What its inventory is worth per game, at the market median. Empty until it has been priced.</summary>
	IReadOnlyList<PluginGameValue> InventoryByGame { get; }
}

/// <summary>One owned or family-shared game.</summary>
public readonly record struct PluginGame(uint AppId, string Name, int MinutesPlayed, bool Shared);

/// <summary>What one game's items are worth.</summary>
public readonly record struct PluginGameValue(uint AppId, string Game, int Items, decimal Value);

/// <summary>What kind of control a plugin setting gets on the page.</summary>
public enum PluginSettingKind {
	Text,
	Int,
	Bool,

	/// <summary>A dropdown. Put the options in <see cref="PluginSetting.Choices"/> as "value label" per entry.</summary>
	Choice
}

/// <summary>
/// One setting a plugin declares.
/// </summary>
/// <param name="Name">Short, unique within your plugin. This is what <c>Setting(name)</c> takes.</param>
/// <param name="Label">What the operator sees beside the control.</param>
/// <param name="Help">One sentence on hover. Say what it does and what happens if it is wrong.</param>
/// <param name="Kind">Which control to draw.</param>
/// <param name="Default">The value before anybody touches it.</param>
/// <param name="Choices">For <see cref="PluginSettingKind.Choice"/>: one "value label" per entry.</param>
public sealed record PluginSetting(
	string Name,
	string Label,
	string Help,
	PluginSettingKind Kind = PluginSettingKind.Text,
	string Default = "",
	IReadOnlyList<string>? Choices = null
);

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

	/// <summary>
	/// Read one of an account's settings, by the name it has in the config. Null if there is no such setting.
	///
	/// Writing is deliberately not here: use <see cref="RunCommandAsync"/> with a `set` line, so the change is
	/// validated, has its side effects applied and lands in the log exactly as a typed one would.
	/// </summary>
	string? GetSetting(string account, string setting);

	/// <summary>
	/// Declare a setting of your own. It appears on the Plugins page, under your plugin, and is edited there.
	///
	/// Call this during OnLoadAsync. The value is stored per plugin, survives restarts and updates, and is read
	/// back with <see cref="Setting"/> - so a plugin gets a real settings UI without needing to build one, and
	/// the operator edits it in the same place as everything else rather than in a file only you know about.
	/// </summary>
	void AddSetting(PluginSetting setting);

	/// <summary>
	/// The current value of one of your settings, as text. The declared default until somebody changes it.
	///
	/// Text because that is what a form gives back. Parse it yourself - you declared the kind, so you know what
	/// it should be, and a plugin that wants an int can say so and still cope with someone typing nonsense.
	/// </summary>
	string Setting(string name);

	/// <summary>
	/// Somewhere to keep your own state between restarts, saved as JSON under config/plugins/&lt;name&gt;.json.
	///
	/// Plugins should not be writing files next to the app's own - this is the sanctioned spot, it survives an
	/// update, and it is obvious to the operator what belongs to whom.
	/// </summary>
	Task SaveStateAsync(string json);

	/// <summary>Whatever was last saved, or null the first time.</summary>
	Task<string?> LoadStateAsync();

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
