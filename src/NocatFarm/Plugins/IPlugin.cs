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
/// 1.9.0 adds one exception, for the rep4rep add-on and anything like it: a plugin may ask the app to post a profile
/// comment as an account (<see cref="IPluginHost.PostProfileCommentAsync"/>). The app decides whether and when - awake,
/// spaced out, under a daily ceiling, never on a paused account - so a plugin can ask, but can't make an account spam.
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

	/// <summary>What its inventory is worth per game, at the lowest market listing. Empty until it has been priced.</summary>
	IReadOnlyList<PluginGameValue> InventoryByGame { get; }

	/// <summary>1.9.0: paused, switched off or signing off - the operator has it standing still, so nothing should start on it.</summary>
	bool IsPaused { get; }
}

/// <summary>1.9.0: what came of <see cref="IPluginHost.PostProfileCommentAsync"/>.</summary>
public enum PluginCommentOutcome {
	/// <summary>Steam took it: the comment is on the profile.</summary>
	Posted,

	/// <summary>The app said no before anything was sent - see <see cref="PluginCommentResult.Detail"/> and RetryAt. Nothing was sent or counted.</summary>
	NotNow,

	/// <summary>The profile has no comment box for this account (private, or friends only). Nothing was sent; it says nothing about the account.</summary>
	CommentsClosed,

	/// <summary>It never left this PC (no web session, Steam unreachable). Nothing was posted or counted.</summary>
	NotSent,

	/// <summary>Sent, and Steam never said whether it took it. Counted as posted - never send the same one again.</summary>
	Unknown,

	/// <summary>Steam refused it for this profile (comments closed to strangers, and the like).</summary>
	Refused,

	/// <summary>Steam says the account is commenting too quickly - a spacing refusal, not the daily limit.</summary>
	RateLimited,

	/// <summary>Steam's own daily limit on comments to strangers is reached for this account.</summary>
	DailyLimit,

	/// <summary>Steam won't let this account comment at all right now (blocked, limited, banned).</summary>
	AccountBlocked
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">A line to show: why the app said no, in the app's language, or Steam's own words for a refusal. Can be empty.</param>
/// <param name="RetryAt">For <see cref="PluginCommentOutcome.NotNow"/>: the soonest the app expects to say yes (UTC), when it knows.</param>
public sealed record PluginCommentResult(PluginCommentOutcome Outcome, string Detail = "", DateTime? RetryAt = null);

/// <param name="Allowed">Whether a comment from this account would be let through right now.</param>
/// <param name="Reason">Why not, in the app's language. Empty when allowed.</param>
/// <param name="RetryAt">The soonest it might be allowed (UTC), when the app knows.</param>
/// <param name="PostedToday">Comments this account posted through add-ons in the last 24 hours.</param>
/// <param name="DailyLimit">The most the app lets one account post through add-ons in any 24 hours.</param>
public sealed record PluginCommentCheck(bool Allowed, string Reason, DateTime? RetryAt, int PostedToday, int DailyLimit);

/// <summary>1.9.0: what kind of block a <see cref="PluginBlock"/> is.</summary>
public enum PluginBlockKind { Heading, Text, Note, Tiles, Table, Buttons, Link }

/// <summary>A button on a plugin's page. It runs one of the plugin's own commands, and/or shows another view of the page.</summary>
/// <param name="Label">What it says.</param>
/// <param name="Command">A command line to run when it's pressed (through the same door as a typed one), or empty.</param>
/// <param name="View">The view to show afterwards (handed to the page's render), or null to stay on the one showing.</param>
public sealed record PluginButton(string Label, string Command = "", string? View = null);

/// <summary>One row of a <see cref="PluginBlockKind.Table"/>: its cells, and optionally a button at the end.</summary>
public sealed record PluginRow(IReadOnlyList<string> Cells, PluginButton? Button = null);

/// <summary>
/// 1.9.0: one piece of a plugin's dashboard page. Text only - the dashboard draws every kind itself, escaped, so a plugin
/// can't put script or markup on the page; buttons run the plugin's commands, links open https pages in a new tab.
/// </summary>
public sealed record PluginBlock(
	PluginBlockKind Kind,
	string Text = "",
	IReadOnlyList<string>? Columns = null,
	IReadOnlyList<PluginRow>? Rows = null,
	IReadOnlyList<PluginButton>? Buttons = null,
	string Url = ""
) {
	public static PluginBlock Heading(string text) => new(PluginBlockKind.Heading, text);
	public static PluginBlock Line(string text) => new(PluginBlockKind.Text, text);

	/// <summary>A muted line, for an explanation under something.</summary>
	public static PluginBlock Note(string text) => new(PluginBlockKind.Note, text);

	/// <summary>Big figures with a label under each: (value, label).</summary>
	public static PluginBlock Tiles(params (string Value, string Label)[] tiles) =>
		new(PluginBlockKind.Tiles, Columns: [.. tiles.Select(static t => t.Label)], Rows: [new PluginRow([.. tiles.Select(static t => t.Value)])]);

	public static PluginBlock Table(IReadOnlyList<string> columns, IReadOnlyList<PluginRow> rows) => new(PluginBlockKind.Table, Columns: columns, Rows: rows);
	public static PluginBlock ButtonRow(params PluginButton[] buttons) => new(PluginBlockKind.Buttons, Buttons: buttons);

	/// <summary>A link that opens in a new tab. Only https addresses are drawn.</summary>
	public static PluginBlock Link(string text, string url) => new(PluginBlockKind.Link, text, Url: url);
}

/// <summary>1.9.0: a plugin's dashboard page, as it is right now.</summary>
public sealed record PluginPage(IReadOnlyList<PluginBlock> Blocks);

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
	Choice,

	/// <summary>
	/// 1.9.0: a password, key or token. Kept encrypted on disk the way the app keeps its own, never sent back to the dashboard
	/// (it only shows whether one is set), and handed to the plugin as plain text by <c>Setting(name)</c>.
	/// </summary>
	Secret
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
/// <param name="PerAccount">1.9.0: one value per account rather than one for the plugin - read with <c>Setting(name, account)</c>.</param>
public sealed record PluginSetting(
	string Name,
	string Label,
	string Help,
	PluginSettingKind Kind = PluginSettingKind.Text,
	string Default = "",
	IReadOnlyList<string>? Choices = null,
	bool PerAccount = false
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
	/// Write to the log. Lines are tagged [plugin], so it is obvious they didn't come from nocat.farm itself.
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
	/// It works everywhere a command can be typed, and is listed by the `plugins` command and on the dashboard's Plugins page.
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

	// ── 1.9.0 ───────────────────────────────────────────────────────────────

	/// <summary>The app's language, as a code ("en", "de", "pt-BR", ...) - for a plugin that carries its own translations.</summary>
	string Language { get; }

	/// <summary>A per-account setting's value for one account (see <see cref="PluginSetting.PerAccount"/>); its default until changed.</summary>
	string Setting(string name, string account);

	/// <summary>
	/// Change one of your OWN declared settings (for one account, when it's per account) - for a command or a page button
	/// that switches something. Logged like a change on the Plugins page. False when there's no such setting or account.
	/// </summary>
	bool SetSetting(string name, string value, string? account = null);

	/// <summary>
	/// Whether the app would let this account post a profile comment right now, and if not why and until when. Asking
	/// never sends anything; the same rules are checked again by <see cref="PostProfileCommentAsync"/>.
	/// </summary>
	PluginCommentCheck CanComment(string account);

	/// <summary>
	/// Post one comment on a Steam profile as one of the accounts. The ONE thing a plugin can make an account do on Steam
	/// by itself, and the app keeps the say over it:
	///
	///   • never on an account that is offline, paused, switched off or signing off
	///   • in human mode, only while the account is up and settled in (asleep, on a break, you playing on it: no), and
	///     never sooner than a person's gap after its last comment
	///   • a hard ceiling per account in any 24 hours, counted across restarts and across every plugin
	///   • one comment at a time per account, and the profile is looked at first: no comment box, nothing is sent
	///
	/// When the app says no, nothing is sent and the result says why (<see cref="PluginCommentOutcome.NotNow"/>). Every
	/// comment that goes out is logged and counted like the app's own.
	/// </summary>
	/// <param name="account">The account's config name.</param>
	/// <param name="targetSteamId">The SteamID64 of the profile to comment on - never the account's own.</param>
	/// <param name="text">The comment, exactly as it should appear (1 to 1000 characters).</param>
	Task<PluginCommentResult> PostProfileCommentAsync(string account, ulong targetSteamId, string text, CancellationToken ct = default);

	/// <summary>
	/// Give the plugin a tab of its own on the dashboard. <paramref name="render"/> is asked whenever the tab is shown or
	/// refreshed, with the view a button asked for (null the first time). One page per plugin; call it during OnLoadAsync.
	/// </summary>
	void AddPage(string title, Func<string?, Task<PluginPage>> render);

	/// <summary>
	/// Send a line out the way the app's own news goes (Telegram, Discord, the tray), when "Send add-on messages" is on,
	/// and write it in the log. For things worth telling somebody about - not every step.
	/// </summary>
	void Notify(string message, string? account = null);

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
