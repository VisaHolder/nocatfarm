using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NocatFarm.Core;

namespace NocatFarm.Config;

// ─────────────────────────────────────────────────────────────────────────────
//  Configuration. Two kinds of file, both plain JSON so they can be hand-edited:
//    config/nocatFarm.json   - global settings
//    config/<bot>.json       - one per Steam account
//
//  Every property here has a matching entry in Settings.cs carrying its section and its tooltip; that one list
//  drives the console, the dashboard form and this file, so a setting is named and explained exactly once.
//  Everything has a sane default, so an empty file still boots.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class GlobalConfig {
	// ── dashboard ──
	public bool WebEnabled { get; set; } = true;
	public string WebHost { get; set; } = "127.0.0.1";
	public int WebPort { get; set; } = 7242;
	public string WebPassword { get; set; } = "";

	/// <summary>The dashboard's address from outside the home (a forwarded port), for 'dashboard' and /dashboard to show.</summary>
	public string WebPublicAddress { get; set; } = "";

	/// <summary>"Open from anywhere": the router forwards the dashboard's port to this PC (UPnP). See RemoteAccess.</summary>
	public bool WebRemoteAccess { get; set; }

	/// <summary>Reverse proxies not on this PC whose forwarded addresses are believed, like loopback's. See TrustedProxies.</summary>
	public string WebTrustedProxies { get; set; } = "";

	/// <summary>A sign-in from the internet also needs a 6-digit code sent to Telegram (when Telegram is connected).</summary>
	public bool WebSignInCode { get; set; } = true;

	/// <summary>Wrong passwords and codes from the internet in a day before Open from anywhere switches itself off. 0 = never.</summary>
	public int WebRemoteOffAfter { get; set; } = 5;
	// On by default. A brand new account only farms its cards until it is told what to play, and the dashboard
	// is the only place with a form for that - so starting up and showing nothing but a console was the wrong
	// first impression for the one screen people actually need.
	public bool OpenBrowserOnStart { get; set; } = true;
	public bool OpenDashboardAfterAdd { get; set; } = true;

	/// <summary>"dark" or "light". Set from the dashboard's toggle or the theme command, not typed into a form.</summary>
	public string Theme { get; set; } = "dark";

	/// <summary>
	/// The dashboard's language, as a file under wwwroot/lang. English is the default and the fallback: anything a
	/// translation hasn't covered falls back to the English string rather than showing a key.
	/// </summary>
	public string Language { get; set; } = "en";

	/// <summary>
	/// Whether the getting-started walkthrough has been seen.
	///
	/// Set by the dashboard, not by hand, so it has no entry in Settings.cs - the same treatment AccountOrder
	/// and the window size get.
	/// </summary>
	public bool TutorialDone { get; set; }

	/// <summary>
	/// Somebody chose the language - in the installer, or by changing it here (1.8.2). Then the walkthrough doesn't ask again:
	/// two language questions in a row was one too many. Set by itself, so it has no entry in Settings.cs.
	/// </summary>
	public bool LanguagePicked { get; set; }
	public int WebRefreshSeconds { get; set; } = 3;
	public int WebSessionDays { get; set; } = 7;

	// ── background / tray ──
	public bool Tray { get; set; } = true;
	public bool MinimizeToTray { get; set; } = true;
	public bool StartMinimized { get; set; }
	public bool StartWithWindows { get; set; }
	public bool KeepAwake { get; set; }
	public bool TrayNotifications { get; set; } = true;
	public bool NotifyEarnings { get; set; } = true;
	public bool NotifySocial { get; set; } = true;
	public bool NotifyProblems { get; set; } = true;

	/// <summary>A pop-up for every new trade offer, and when one is accepted, declined or needs confirming.</summary>
	public bool NotifyTrades { get; set; } = true;

	/// <summary>A pop-up for each scam-watch warning (1.8.2: one of the pop-up kinds, where it was its own switch).</summary>
	public bool NotifyScam { get; set; } = true;

	/// <summary>A pop-up when something about an account gets worse.</summary>
	public bool NotifyHealth { get; set; } = true;

	// ── rep4rep ──
	// Master switch for the whole rep4rep feature. Off = nothing rep4rep-related runs on any account, and the
	// dashboard hides its tab, points, per-account options and settings entirely.
	//
	// Defaults OFF: rep4rep is a third-party site most users won't touch, so a fresh install starts with it gone
	// and it's opt-in from Settings -> rep4rep account -> "Use rep4rep at all". Existing setups that DO use it
	// keep it by writing an explicit "Rep4RepEnabled": true into their config (a missing field takes this default).
	public bool Rep4RepEnabled { get; set; } = false;
	public string Rep4RepApiToken { get; set; } = "";
	public bool Rep4RepAutoAddProfiles { get; set; } = true;
	public int Rep4RepPointsRefreshMinutes { get; set; } = 15;

	/// <summary>How many HOURS to hold all commenting for. 0 is "not holding"; it clears itself when it expires.</summary>
	/// <remarks>
	/// Hours rather than days because "a day or two" is not the only shape a pause comes in - "27 hours" is a
	/// perfectly ordinary thing to want and a whole-days field simply cannot say it.
	/// </remarks>
	public int Rep4RepPauseHours { get; set; }

	/// <summary>
	/// When the hold ends, worked out once from Rep4RepPauseDays rather than counted down.
	/// </summary>
	/// <remarks>
	/// Stored rather than recomputed because "pause for two days" has to survive a restart, and a plain day
	/// counter would restart its two days every time the app came up - which is the one thing a hold must not
	/// do. Null means nothing is being held.
	/// </remarks>
	public DateTime? Rep4RepHoldUntil { get; set; }

	/// <summary>When the hold began, so its end can be recomputed without moving it.</summary>
	/// <remarks>
	/// Without this the deadline was worked out as "now + days" every time ANY global setting was saved, so
	/// changing the theme an hour into a two-day hold quietly restarted the two days. Anchored to a start,
	/// saving the same number over and over lands on the same end, and changing the number moves only the end.
	/// </remarks>
	public DateTime? Rep4RepHoldFrom { get; set; }

	// ── steam connection ──
	public int LoginStaggerSeconds { get; set; } = 12;
	public int ReconnectDelaySeconds { get; set; } = 10;
	// Two minutes: long enough that a slow Steam answer isn't taken for a dead connection.
	public int ConnectionTimeoutSeconds { get; set; } = 120;
	public int MaxConcurrentFarming { get; set; }
	// Half an hour: a login rate limit lifted early is cheaper than one hit again.
	public int LoginCooldownMinutes { get; set; } = 30;
	public int WebRequestGapMs { get; set; } = 400;
	public int SteamProtocol { get; set; }
	public string WebProxy { get; set; } = "";
	public string WebProxyUsername { get; set; } = "";
	public string WebProxyPassword { get; set; } = "";

	/// <summary>AppIDs no account ever touches, on top of each account's own list.</summary>
	public List<uint> GlobalBlacklistedGames { get; set; } = [];

	// ── notifications to Discord and Telegram (Core/Notifier.cs) ──
	/// <summary>A Discord channel webhook link. Encrypted on disk.</summary>
	public string DiscordWebhookUrl { get; set; } = "";

	/// <summary>A Telegram bot token from @BotFather. Encrypted on disk.</summary>
	public string TelegramBotToken { get; set; } = "";

	/// <summary>Where the bot posts - found by itself from the first message sent to the bot.</summary>
	public string TelegramChatId { get; set; } = "";

	/// <summary>Take commands from the connected Telegram chat - /status, /console and every console command.</summary>
	public bool TelegramCommands { get; set; } = true;

	/// <summary>A Discord bot token from the Developer Portal, for slash commands (Core/DiscordBot.cs). Encrypted on disk.</summary>
	public string DiscordBotToken { get; set; } = "";

	/// <summary>Take slash commands from Discord - in a private chat with the bot or in a server it's in.</summary>
	public bool DiscordCommands { get; set; } = true;

	/// <summary>The one Discord user the bot obeys - filled in by /connect with the code the dashboard shows.</summary>
	public string DiscordOwnerId { get; set; } = "";

	/// <summary>"Playing nocat.farm" on your Discord profile while it's open.</summary>
	public bool DiscordPresence { get; set; }

	/// <summary>Which accounts the Discord card is about: names, or "all". Empty = every account not in human mode.</summary>
	public string DiscordPresenceAccounts { get; set; } = "";

	/// <summary>The account the Discord card is about first: its avatar, its name leading, its Steam profile linked.</summary>
	public string DiscordFeatured { get; set; } = "";

	/// <summary>The two buttons Discord allows: "github", an account name (its Steam profile), "Label | https://...", or empty.</summary>
	public string DiscordButton1 { get; set; } = "github";

	public string DiscordButton2 { get; set; } = "";

	/// <summary>What else the Discord card shows. (The account names it could show went in 1.6.9 - see
	/// <see cref="ConfigStore.MigrateDiscordNames"/>.)</summary>
	public bool DiscordShowCounter { get; set; } = true;

	public bool DiscordShowAvatar { get; set; } = true;

	public bool DiscordShowTimer { get; set; } = true;

	/// <summary>What the card's second line counts: 0 cards today, 1 hours in the past week, 2 hours in the past month,
	/// 3 Steam accounts on nocat.farm (<see cref="Core.DiscordPresence.LineAccounts"/>, once "people using nocat.farm").</summary>
	public int DiscordSecondLine { get; set; } = Core.DiscordPresence.LineAccounts;

	/// <summary>The colour of "telegram" and "discord" in the log, from the same palette as the accounts'.</summary>
	public int TelegramLogColour { get; set; } = 6;

	public int DiscordLogColour { get; set; } = 5;

	public bool SendCardDrops { get; set; } = true;
	public bool SendFreeStuff { get; set; } = true;

	/// <summary>Badge levels bought with Steam Points (see Modules/PointsSpender.cs).</summary>
	public bool SendPoints { get; set; } = true;
	public bool SendTrades { get; set; } = true;
	public bool SendProblems { get; set; } = true;
	public bool SendUpdates { get; set; } = true;

	/// <summary>Off by default: downloaded and installing, installed, failed or undone.</summary>
	public bool SendInstalls { get; set; }

	/// <summary>On by default: a sign-in to the dashboard from outside the home, a lockout, the internet brake.</summary>
	public bool SendSignIns { get; set; } = true;

	/// <summary>On by default: lockouts, wrong codes, the internet brake, and Open from anywhere switching itself off.</summary>
	public bool SendBreakIns { get; set; } = true;

	/// <summary>Spare items and booster packs: items turned into gems or listed, packs opened, the first preview.</summary>
	public bool SendItems { get; set; } = true;
	public bool SendDailySummary { get; set; } = true;
	public bool SendComments { get; set; }
	public bool SendAchievements { get; set; }
	public bool SendRep4Rep { get; set; }

	// ── scam watch and account health - one chip each among the notifications (1.8.2), and devices that are yours ──
	/// <summary>Scam-watch warnings to Telegram and Discord, like every other kind: whichever is set up gets them.</summary>
	public bool SendScamWarnings { get; set; } = true;

	/// <summary>Account-health alerts (something got worse) to Telegram and Discord.</summary>
	public bool SendHealthAlerts { get; set; } = true;

	/// <summary>Device names on the authorised-devices list that are yours, comma separated - never reported as new.</summary>
	public string ScamWatchTrustedDevices { get; set; } = "";

	/// <summary>
	/// Retired per-channel switches (to 1.8.1 scam watch and account health had Telegram / Discord / pop-up each). Read so an
	/// old file carries over (<see cref="ConfigStore.MigrateAlertRouting"/>): the pop-ups become <see cref="NotifyScam"/> and
	/// <see cref="NotifyHealth"/> and go; a channel turned off on its own - the other one on - stays off for that channel
	/// until the chip is changed, and is written only while it does. Null: the chip decides.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? ScamAlertTelegram { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? ScamAlertDiscord { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? ScamAlertPopup { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? HealthAlertTelegram { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? HealthAlertDiscord { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? HealthAlertPopup { get; set; }

	/// <summary>Steam groups every account joins - links or short names, comma separated. Open groups only. Starts
	/// with the nocat.farm group; anyone can change or clear it.</summary>
	public string GroupsToJoin { get; set; } = "steamcommunity.com/groups/nocatfarm";

	public bool ExitWhenAllFinished { get; set; }

	// ── backups (Core/LockedBackup.cs) ──
	/// <summary>The daily password-locked backup. Off until the owner sets it up - it needs a password only they pick.</summary>
	public bool BackupAuto { get; set; }

	/// <summary>Where the daily backup goes (OneDrive, a USB drive). Empty: the backups folder next to config.</summary>
	public string BackupFolder { get; set; } = "";

	/// <summary>The password the daily backups are locked with - kept encrypted like every other secret, never sent anywhere.</summary>
	public string BackupPassword { get; set; } = "";
	public int BackupHour { get; set; } = 4;
	public int BackupJitterMinutes { get; set; } = 30;
	public int BackupKeep { get; set; } = 7;
	public bool BackupIncludeSettings { get; set; } = true;
	public bool BackupIncludeAccounts { get; set; } = true;
	public bool BackupIncludeAuthenticators { get; set; } = true;
	public bool BackupIncludeHistory { get; set; } = true;
	public bool BackupIncludeLogs { get; set; }

	/// <summary>Backup messages on Telegram and Discord (1.8.2: one choice where there were "Send backups" and "Tell me"):
	/// 0 none, 1 only when a backup fails, 2 after every backup too.</summary>
	public int BackupMessages { get; set; } = 1;

	/// <summary>1.8.5: each daily backup (the password-locked file) is also sent to the connected Telegram chat.</summary>
	public bool BackupToTelegram { get; set; } = true;
	public bool BackupLocalCopy { get; set; } = true;
	public int BackupRetryMinutes { get; set; } = 60;

	/// <summary>Retired "Tell me" (0 only when one fails, 1 after every backup) and "Send backups" - only read, so an old
	/// file carries over to <see cref="BackupMessages"/> (<see cref="ConfigStore.MigrateBackupMessages"/>). Never written now.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? BackupNotify { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? SendBackups { get; set; }

	// ── phone app (wwwroot/manifest.webmanifest, sw.js) ──
	/// <summary>The how-to on the Phone page for putting the dashboard on a phone's home screen.</summary>
	public bool PhoneInstallHint { get; set; } = true;

	// ── logging ──
	/// <summary>
	/// The order accounts are shown in, by name. Anything not listed falls in after them, alphabetically.
	///
	/// Set by dragging the cards on the Accounts page rather than by typing, so it has no entry in Settings.cs.
	/// </summary>
	public List<string> AccountOrder { get; set; } = [];

	// 0 means "never resized" - the window opens at its default size rather than at 0x0.
	public int WindowWidth { get; set; }
	public int WindowHeight { get; set; }

	// Where the window was left. int.MinValue = never moved - it opens in the middle of the main screen.
	public int WindowX { get; set; } = int.MinValue;
	public int WindowY { get; set; } = int.MinValue;

	// Mini mode: the window shrunk to a small always-on-top panel of the accounts. Where it was left is kept apart
	// from the full window's size, so switching back and forth puts each where it belongs. int.MinValue = not moved yet.
	public bool MiniMode { get; set; }
	public bool MiniOnTop { get; set; }
	public int MiniX { get; set; } = int.MinValue;
	public int MiniY { get; set; } = int.MinValue;

	/// <summary>Mini mode's list opened up (up to 10 rows, the rest by scrolling) rather than the first 5 and a "+N more"
	/// bar. Set by clicking that bar, so it has no entry in Settings.cs.</summary>
	public bool MiniExpanded { get; set; }

	/// <summary>The one figure beside the name in mini mode's title bar - see the choices in Settings.cs. Game-hours today
	/// unless changed (an install from before this setting existed gets that too).</summary>
	public int MiniStats { get; set; } = 1;

	public int StatusEveryMinutes { get; set; } = 5;
	public int StatusQuietEveryMinutes { get; set; } = 30;

	// ── daily report ──
	// A once-a-day plain-language summary of what the whole fleet banked in the last 24h - hours, cards,
	// comments, running total - written to the log/console at a chosen time (default 09:30). The per-minute
	// heartbeat says what an account is doing NOW; this answers "what did I get overnight?" in one place.
	public bool DailyReportEnabled { get; set; } = true;

	/// <summary>When it's written (and sent), in minutes from midnight - 09:30 is 570. 1.8.2: one time where there were an
	/// hour and a minute.</summary>
	public int DailyReportTime { get; set; } = (9 * 60) + 30;

	/// <summary>Retired hour and minute of the daily summary - only read, so an old file carries over to
	/// <see cref="DailyReportTime"/> (<see cref="ConfigStore.MigrateDailyReportTime"/>). Never written now.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? DailyReportHour { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? DailyReportMinute { get; set; }

	// ── weekly report ──
	// Off by default: once a week, this week next to last week - see Core/WeeklyReport.cs.
	public bool WeeklyReport { get; set; }

	/// <summary>0 Sunday ... 6 Saturday, like DayOfWeek.</summary>
	public int WeeklyReportDay { get; set; } = 1;
	public int WeeklyReportHour { get; set; } = 10;

	// ── stuck-account alarm (Core/StuckWatch.cs) ──
	// On by default: it only ever looks at time an account should have been banking, so a healthy one never trips it.
	public bool StuckAlarm { get; set; } = true;
	public int StuckAlarmHours { get; set; } = 3;

	// ── inventory value ──
	/// <summary>Steam's currency id for market prices. 1 USD, 2 GBP, 3 EUR, 20 CAD, 21 AUD - as the store uses.</summary>
	public int MarketCurrency { get; set; } = 1;

	/// <summary>Seconds between community-market price lookups. Higher is slower but stops Steam rate-limiting us.</summary>
	public int MarketGapSeconds { get; set; } = 10;

	/// <summary>How many hours a market price is trusted before it is looked up again.</summary>
	public int PriceCacheHours { get; set; } = 24;

	/// <summary>Retired "Notify if an update is available" - off is <see cref="Core.UpdateModes.Off"/> now
	/// (<see cref="ConfigStore.MigrateCheckForUpdates"/>). Only read; never written.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? CheckForUpdates { get; set; }

	/// <summary>Looks for new versions at all: every choice but "don't look for updates".</summary>
	[JsonIgnore]
	public bool LooksForUpdates => UpdateMode != Core.UpdateModes.Off;

	/// <summary>The hourly "this copy is running" to nocat.lol behind the user count - see Core/UserCount.cs. Off sends nothing.</summary>
	public bool CountMeAsUser { get; set; } = true;

	/// <summary>The ping also carries the hours banked, cards dropped, items turned into gems, badges crafted, packs opened and items sold since the last one - see Core/FarmTotals.cs. Only with CountMeAsUser on.</summary>
	public bool ShareFarmTotals { get; set; } = true;

	/// <summary>
	/// 1.8.2: the set finder sends nocat.lol the cheapest whole card sets it knows (market prices only) and takes back the
	/// ones every copy has seen, as candidates - see Core/SetShare.cs. Only with CountMeAsUser on; off sends and fetches nothing.
	/// </summary>
	public bool ShareCheapestSets { get; set; } = true;

	public bool UpdateReminders { get; set; } = true;

	public int UpdateCheckHours { get; set; } = 2;

	/// <summary>
	/// Retired "Update by itself" (0 tell me, 1 install at night) - only read, so an old file carries over to
	/// <see cref="UpdateMode"/> (see <see cref="ConfigStore.MigrateUpdates"/>). Never written now: it's always 0 once read.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int AutoUpdate { get; set; }

	/// <summary>
	/// The old "When I say update" (1 = 'update accept' and the Update button wait until the accounts are asleep). Not a
	/// setting any more: "once everyone's asleep" waits and "install when I click" doesn't, by themselves. Only "install by itself at
	/// night" still reads it, so an account that had both "install at night" and "when my accounts are asleep" keeps both -
	/// and choosing either of the other two clears it.
	/// </summary>
	/// <remarks>Declared before <see cref="UpdateMode"/> on purpose: a file is read in this order, and UpdateMode's setter
	/// clears it.</remarks>
	public int UpdateWhenAsked { get; set; }

	/// <summary>What happens when a new version is out - <see cref="Core.UpdateModes"/>. Install when I click by default.</summary>
	public int UpdateMode {
		get => _updateMode;
		set {
			_updateMode = value;
			_updateModeSet = true;

			if (value != Core.UpdateModes.AtNight) {
				UpdateWhenAsked = 0;
			}
		}
	}

	private int _updateMode = Core.UpdateModes.WhenIClick;

	/// <summary>
	/// UpdateMode was in the file (or has been chosen since): then it's what counts, and an AutoUpdate next to it is only the
	/// copy 1.6.5 to 1.6.9 wrote for the version before them - never read as a choice.
	/// </summary>
	[JsonIgnore]
	public bool UpdateModeSet => _updateModeSet;

	private bool _updateModeSet;

	/// <summary>Whether the Update button and 'update accept' wait for the accounts to be asleep instead of installing now.</summary>
	[JsonIgnore]
	public bool UpdateClickWaits => (UpdateMode == Core.UpdateModes.JustTellMe) || ((UpdateMode == Core.UpdateModes.AtNight) && (UpdateWhenAsked == 1));

	public int AutoUpdateFromHour { get; set; } = 3;

	public int AutoUpdateUntilHour { get; set; } = 6;

	public int AutoUpdateWaitHours { get; set; } = 2;

	/// <summary>Whether to load DLLs from plugins/. Off until somebody decides otherwise - see PluginHost.</summary>
	public bool PluginsEnabled { get; set; }

	/// <summary>Plugin names that are installed but switched off. Set from the dashboard, not typed by hand.</summary>
	public List<string> DisabledPlugins { get; set; } = [];

	public bool FileLogging { get; set; } = true;

	/// <summary>
	/// Whether debug detail is also shown on screen. The log FILE always keeps it either way - this decides
	/// only whether the window and the console show it too, and off is right: it is a wall of grey noise that
	/// scrolls everything worth reading off the top.
	/// </summary>
	public bool Debug { get; set; }
	public int LogRetentionDays { get; set; } = 14;

	// ── Show in the app's log (1.8.2) ── which kinds of line the window and the dashboard's Log tab show (Core/LogShow.cs).
	// All on: nothing is hidden until a chip is turned off. The log file keeps every line, and warnings and errors always show.
	public bool LogShowDoing { get; set; } = true;
	public bool LogShowDrops { get; set; } = true;
	public bool LogShowBadges { get; set; } = true;
	public bool LogShowBuying { get; set; } = true;
	public bool LogShowSelling { get; set; } = true;
	public bool LogShowTrades { get; set; } = true;
	public bool LogShowComments { get; set; } = true;
	public bool LogShowAchievements { get; set; } = true;
	public bool LogShowFree { get; set; } = true;
	public bool LogShowAnswers { get; set; } = true;
	public bool LogShowApp { get; set; } = true;

	// ── Open in Steam (1.8.2) ── the button on every account at once. Off by default; like each account's own switch,
	// its go-ahead lives outside the settings (SteamOpenLedger.Everyone), so this alone can't switch it on - the
	// preview's Turn on does.
	public bool SteamOpenEveryAccount { get; set; }
}

public sealed class BotConfig {
	/// <summary>Index into NameColour.All. 0 keeps the old colour-by-state behaviour.</summary>
	public int LogColour { get; set; }

	/// <summary>How often this account reports in while playing. 0 follows the global setting, -1 silences it.</summary>
	public int StatusEveryMinutes { get; set; }

	/// <summary>The same while resting. 0 follows the global setting, -1 silences it.</summary>
	public int StatusQuietEveryMinutes { get; set; }

	// ── account ──
	public bool Enabled { get; set; } = true;
	public string SteamLogin { get; set; } = "";
	public string SteamPassword { get; set; } = "";

	/// <summary>Sign in by scanning a QR code with the Steam app instead of a password.</summary>
	public bool SignInWithQr { get; set; }
	public int OnlineStatus { get; set; } = 1;

	/// <summary>
	/// Which kind of Steam client this pretends to be when it signs in.
	///
	/// Defaults to 7 (DesktopUI), which is what the real Steam client reports. The old default was Unknown (-1) - an
	/// unidentified session, which Steam then has to make its own mind up about. See the logon in Bot.cs for why that
	/// mattered.
	/// </summary>
	public int UIMode { get; set; } = 7;

	/// <summary>
	/// You sign into this account yourself, so nocatFarm must never touch its persona.
	///
	/// Setting the persona from a second session makes Steam sign the other one out of Friends and Chat. On an
	/// account you actually use, that is your own client being kicked off - repeatedly, every time the schedule
	/// decides it is bedtime.
	/// </summary>
	public bool IUseThisAccount { get; set; }
	public bool StartPaused { get; set; }
	public string Notes { get; set; } = "";
	public string SteamParentalCode { get; set; } = "";
	public string MachineName { get; set; } = "";

	// ── the mobile authenticator ──
	public string SharedSecret { get; set; } = "";
	public string IdentitySecret { get; set; } = "";

	// ── what it plays ──
	public List<uint> IdleGames { get; set; } = [];
	/// <summary>
	/// Whether to show the custom name at all. Off for a new account since 1.8.2 - on with no name typed it did nothing and
	/// read as if it did; emptying the name turns it off (Settings.Follow). An account from before keeps what it had
	/// (<see cref="ConfigStore.MigrateNewDefaults"/>).
	/// </summary>
	public bool CustomGameNameEnabled { get; set; }

	public string CustomGameName { get; set; } = "";
	public bool PlayWhileFarming { get; set; } = true;
	public int GameDevice { get; set; }

	/// <summary>Idle every game the account owns, after the ones in IdleGames. See <see cref="Modules.IdleRotation"/>.</summary>
	public bool IdleWholeLibrary { get; set; }

	/// <summary>A list longer than Steam plays at once is idled a batch at a time, the next batch every RotateEveryHours.</summary>
	public bool RotateIdleGames { get; set; }

	public int RotateEveryHours { get; set; } = Modules.IdleRotation.DefaultHours;

	// ── trading cards ──
	public bool FarmCards { get; set; } = true;
	public float HoursUntilCardDrops { get; set; } = 3;
	public int FarmingOrder { get; set; }
	public List<uint> PriorityGames { get; set; } = [];
	public bool FarmPriorityOnly { get; set; }
	public List<uint> BlacklistedGames { get; set; } = [];
	public bool SkipUnplayedGames { get; set; }

	/// <summary>
	/// "Protect refunds": bought and gifted games for Steam's 14 days, and games new to the family library for as long
	/// (<see cref="Core.RefundGuard"/>). It was this plus three more settings - gifted games too, family games too, and
	/// how many days - which are folded in now: their old keys in a file (ProtectGiftedGames, RefundHoldDays,
	/// HoldNewFamilyGames) aren't read, and are gone from it at its next save.
	/// On for a new account since 1.8.2: idling a just-bought game past Steam's 2 hours loses the refund. An account from
	/// before keeps what it had (<see cref="ConfigStore.MigrateNewDefaults"/>).
	/// </summary>
	public bool SkipRefundableGames { get; set; } = true;

	/// <summary>
	/// 1.8.2: "Protect refunds only for games over", in the wallet's own currency: a refundable game that cost this or less
	/// isn't held - it's farmed right away (<see cref="Core.RefundGuard"/>). What it cost is what was paid when the purchase
	/// history shows it, else the store's regular price; not known - protected. 0: every refundable game, as before.
	/// </summary>
	public float RefundProtectMinPrice { get; set; }

	/// <summary>When a human-mode account farms cards - <see cref="Modules.FarmWhen"/>. The day's sittings by default.</summary>
	public int FarmCardsWhen { get; set; }

	/// <summary>FarmCardsWhen = mixed: roughly this share of the day's sittings farm cards.</summary>
	public int CardSittingsPct { get; set; } = 40;

	/// <summary>Retired: only read so an old "farm only while asleep" carries over to <see cref="FarmCardsWhen"/>.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool FarmOnlyWhileAsleep { get; set; }
	public int FarmFromHour { get; set; }
	public int FarmUntilHour { get; set; }
	public int PostFarmWindDownMinMinutes { get; set; } = 15;
	public int PostFarmWindDownMaxMinutes { get; set; } = 20;
	public int LegitStopMaxSeconds { get; set; } = 30;
	// Drops arrive as a push, so the badge-page re-check is only a backstop - every 20 minutes is plenty.
	public int FarmingDelayMinutes { get; set; } = 20;

	// A game that hasn't dropped a card in 8 hours of farming isn't going to; it's set aside and retried later.
	public int MaxFarmingHoursPerGame { get; set; } = 8;
	public bool StopWhenFarmingDone { get; set; }
	public bool FarmOffline { get; set; }

	/// <summary>Farm in a few believable sittings a day instead of flat out. Works with or without human mode.</summary>
	public bool FarmInSittings { get; set; }

	/// <summary>Roughly how many hours a day legit farming spends farming. Jittered daily, longer at weekends.</summary>
	public int FarmHoursPerDay { get; set; } = 6;

	// ── achievements ──
	public bool UnlockAchievements { get; set; }
	public int AchievementGrindGapMinMinutes { get; set; } = 12;
	public int AchievementGrindGapMaxMinutes { get; set; } = 24;
	public int AchievementBoost { get; set; }              // 0 off, 1 games you pick, 2 every single-player game
	public List<uint> AchievementBoostGames { get; set; } = [];
	public int BoostSessionHours { get; set; } = 2;
	public int BoostWeight { get; set; } = 15;

	/// <summary>How many games the hunter has on the go at once, taking turns between them.</summary>
	public int BoostGamesInRotation { get; set; } = 3;

	/// <summary>The most it hunts in a day, in hours; 0 is no limit.</summary>
	public int BoostHoursPerDay { get; set; }

	/// <summary>Moves to another game somewhere between these, as a percentage of the game's achievements.</summary>
	public int BoostSwitchFromPct { get; set; } = 65;

	public int BoostSwitchToPct { get; set; } = 75;

	/// <summary>Comes back to a game it moved on from after this many days, somewhere in the range.</summary>
	public int BoostRestDaysMin { get; set; } = 3;

	public int BoostRestDaysMax { get; set; } = 14;

	/// <summary>Paces each game's achievements by how long that game really takes (see Core/Playtime).</summary>
	public bool AchievementRealLength { get; set; } = true;
	public bool IncludeFamilyLibrary { get; set; }
	public bool YieldToFamily { get; set; } = true;

	/// <summary>A family game whose copy is in use switches to another member's free copy (Steam's preferred lender for it). See Core/Family.cs.</summary>
	public bool BorrowFamilyCopy { get; set; } = true;

	/// <summary>Minutes a family game's copy is left alone after its player stops. 0: straight away.</summary>
	public int FamilyWaitMinutes { get; set; } = 20;
	public int BoostMinReviews { get; set; } = 200;
	public bool BoostOnlyPlayedGames { get; set; }
	public List<uint> AchievementGames { get; set; } = [];

	/// <summary>
	/// Games never to unlock in. Note that CS2 needs no entry here and never did: Valve keeps its achievements
	/// server-side, so the stats interface exposes a single settable one and no tool can touch the rest.
	/// </summary>
	public List<uint> AchievementNeverGames { get; set; } = [];

	/// <summary>
	/// Games the owner left alone ('dlc leave', or "Leave it paused" on the dashboard before): the game, and which of its
	/// DLC the account had for good then (as <see cref="Core.DlcAchievements.LicenceKey"/> writes it). Such a game is held
	/// as it used to be - nothing in it unlocked on a guess - whatever DLC it gets later, until 'dlc undo'. Not a setting.
	/// </summary>
	public Dictionary<uint, string> AchievementDlcLeft { get; set; } = [];

	/// <summary>Whether human mode's main game earns achievements like any other. On - it's where the hours are.</summary>
	public bool AchievementIncludeMainGame { get; set; } = true;

	/// <summary>
	/// Never unlock multiplayer, co-op, zombies, versus or ranked achievements - ones that need other players, whose matches
	/// the game and Steam keep a record of that an unlock can't match. On by default for a human-mode account and off for a
	/// robot: until it's chosen it follows human mode, and turning human mode on or off sets it to match
	/// (<see cref="Settings.ApplyLegitMode"/>). Once written to the file it stays what it says.
	/// </summary>
	public bool AchievementSkipMultiplayer {
		get => _skipMultiplayer ?? LegitMode;
		set => _skipMultiplayer = value;
	}

	private bool? _skipMultiplayer;

	/// <summary>0 careful, 1 normal, 2 brisk. Scales every gap the pacer waits.</summary>
	public int AchievementPace { get; set; } = 1;

	/// <summary>
	/// Only achievements at least this many percent of players have may come in a quick burst (a few minutes after the one
	/// before); rarer ones always come alone, with the full wait. 0 lets any one burst; 100 turns bursts off.
	/// </summary>
	public int AchievementBurstMinPct { get; set; } = 10;

	/// <summary>Hard cap on how much of any one game will ever be completed. 0 uses each game's own ceiling.</summary>
	public int AchievementMaxCompletionPct { get; set; } = 90;

	/// <summary>
	/// Retired "Watch for bans" (1.8.2): the ban look always runs now - it only reads, and the banned games it finds are
	/// left out of trades - and the alert for a new ban is account health's (<see cref="HealthAlertBans"/>). Off in an old
	/// file turns that alert off (<see cref="ConfigStore.MigrateWatchBans"/>). Only read; never written.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? WatchBans { get; set; }

	// ── scam watch ──
	/// <summary>Look for the signs of a stolen account or a trade scam (an API key, a new device, changed email/phone/Guard, every session signed out).</summary>
	public bool WatchForScams { get; set; } = true;
	public bool ScamWatchApiKey { get; set; } = true;
	public bool ScamWatchDevices { get; set; } = true;
	public bool ScamWatchDetails { get; set; } = true;
	public bool ScamWatchSessions { get; set; } = true;

	/// <summary>How often it looks, a random time between these two, in minutes.</summary>
	public int ScamWatchMinMinutes { get; set; } = 30;
	public int ScamWatchMaxMinutes { get; set; } = 60;

	/// <summary>On a warning: stop taking trades (and sending items) until 'scamwatch ok'.</summary>
	public bool ScamWatchStopTrades { get; set; } = true;

	/// <summary>On a warning: cancel the offers this account has sent that haven't gone through.</summary>
	public bool ScamWatchCancelOffers { get; set; } = true;

	/// <summary>On a NEW API key: press Revoke on Steam's key page. Off by default.</summary>
	public bool ScamWatchRevokeKey { get; set; }

	// ── account health ──
	public bool WatchHealth { get; set; } = true;
	public bool HealthAlertBans { get; set; } = true;
	public bool HealthAlertTradeHold { get; set; } = true;
	public bool HealthAlertMarket { get; set; } = true;
	public bool HealthAlertLimited { get; set; } = true;
	public bool HealthAlertGuard { get; set; } = true;
	public bool HealthAlertPhone { get; set; } = true;

	/// <summary>How often it looks, a random time between these two, in hours.</summary>
	public int HealthMinHours { get; set; } = 5;
	public int HealthMaxHours { get; set; } = 7;

	// ── inventory ──
	public bool ShowInventoryValue { get; set; } = true;
	public List<uint> InventoryIgnoreGames { get; set; } = [];

	// ── free games & badges ──
	/// <summary>
	/// Free games to claim: <see cref="Modules.FreeClaims"/> - off, games, or games and DLC (and a free DLC's game when
	/// that's free right now too).
	/// </summary>
	public int ClaimFree {
		get => _claimFree;
		set {
			_claimFree = value;
			_claimFreeSet = true;
		}
	}

	private int _claimFree;

	/// <summary>
	/// ClaimFree was in the file (or chosen since): the three old switches next to it are only the copy 1.6.5 to 1.6.9 wrote
	/// for the version before them - never read as a choice.
	/// </summary>
	[JsonIgnore]
	public bool ClaimFreeSet => _claimFreeSet;

	private bool _claimFreeSet;

	// The retired keys below are only read, so an old file carries over (ConfigStore.MigrateFreeGames). Never written now:
	// they're always off once read.

	/// <summary>Retired "Claim free games" - carried over to <see cref="ClaimFree"/>.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool ClaimFreeGames { get; set; }

	/// <summary>Retired "...free DLC too" - carried over to <see cref="ClaimFree"/>.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool ClaimFreeDlc { get; set; }

	/// <summary>Retired "...and its game, if that's free too" - always part of games and DLC now.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? ClaimFreeDlcBase { get; set; }
	public bool CraftBadges { get; set; }

	/// <summary>"Level-up line shows" (1.8.2, Core/LevelUps.cs): 3 the level and what it cost, 1 the level only, 0 no line.</summary>
	public int LevelUpLine { get; set; } = Core.LevelUps.LevelAndCost;

	/// <summary>"Also craft foil badges" (1.8.1): with crafting on, foil sets too. Off levels with normal badges only.</summary>
	public bool CraftFoilBadges { get; set; } = true;

	/// <summary>
	/// "Sell sets worth more than the levels they'd buy" (1.8.2, Modules/SetSale.cs): before crafting a normal badge, a set
	/// whose market price buys clearly more badge levels than the one its craft makes is listed for sale instead, and buying
	/// cards spends the money. On by default, but it does nothing unless buying cards is on (and turned on through its
	/// preview). 1.8.2: foil sets and a game's several sets are weighed too, and cards held for a badge that are worth more
	/// sold than the levels they'd make are sold (Modules/DearCards.cs).
	/// </summary>
	public bool SellSetsForLevels { get; set; } = true;

	/// <summary>
	/// Retired "Open booster packs" switch - carried over to <see cref="OpenBoosterPacks"/> (ConfigStore.MigrateSpare). Still
	/// written, as a mirror of it ("always" is on), so rolling back to 1.7.x keeps packs opening as they were; read only from
	/// a file without OpenBoosterPacks.
	/// </summary>
	public bool UnpackBoosterPacks {
		get => _openBoosterPacksSet ? (_openBoosterPacks == 2) : _unpackBoosterPacks;
		set => _unpackBoosterPacks = value;
	}

	private bool _unpackBoosterPacks;

	/// <summary>The old switch as a file from before 1.8.0 had it - for the carry-over only.</summary>
	[JsonIgnore]
	internal bool OldUnpackBoosterPacks => _unpackBoosterPacks;

	/// <summary>Games to make a booster pack for each day, from gems.</summary>
	public List<uint> BoosterPackGames {
		get => _boosterPackGames;
		set {
			_boosterPackGames = value;
			_boosterPackGamesSet = true;
		}
	}

	private List<uint> _boosterPackGames = [];

	/// <summary>
	/// BoosterPackGames was in the file (or set since): a BoosterGames next to it is only the copy 1.6.5 to 1.6.9 wrote for
	/// the version before them - never read as a choice.
	/// </summary>
	[JsonIgnore]
	public bool BoosterPackGamesSet => _boosterPackGamesSet;

	private bool _boosterPackGamesSet;

	/// <summary>Retired: the same list as text ("730, 440") - only read, and carried over to <see cref="BoosterPackGames"/>.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public string? BoosterGames { get; set; }

	/// <summary>Which gems booster packs are made from when there are both kinds: 0 tradable first, 1 untradable first.</summary>
	public int BoosterGems { get; set; }

	// ── making booster packs (1.8.1) ──
	/// <summary>
	/// "Make booster packs": 0 off, 1 the games in <see cref="BoosterPackGames"/>, 2 automatic - the game whose normal
	/// badge is closest to its next level (Modules.BoosterPick). A file from before has none: a list in it is "games I
	/// pick", an empty one off (ConfigStore.MigrateBoosterMake).
	/// </summary>
	public int BoosterMake {
		get => _boosterMake;
		set {
			_boosterMake = value;
			_boosterMakeSet = true;
		}
	}

	private int _boosterMake;

	/// <summary>BoosterMake was in the file (or set since).</summary>
	[JsonIgnore]
	public bool BoosterMakeSet => _boosterMakeSet;

	private bool _boosterMakeSet;

	/// <summary>Gems a booster pack never takes the account below.</summary>
	public int BoosterKeepGems { get; set; }

	/// <summary>
	/// 1.8.2 "Use gems for the most levels" (Modules/GemsChoice.cs): with buying cards on and "Make booster packs" automatic,
	/// gems go whichever way buys the most badge levels at today's prices - a sack sold, a pack sold sealed, a pack opened and
	/// its cards sold, or the closest badge - with a safety margin, and kept loose when it isn't sure. On by default; it does
	/// nothing while buying cards is off.
	/// </summary>
	public bool GemsForLevels { get; set; } = true;
	public bool ClaimEventItems { get; set; } = true;

	/// <summary>0 off, 1 during sales, 2 every day.</summary>
	public int DiscoveryQueue { get; set; } = 1;

	// ── Sale events (1.8.0) - see Modules/SaleEvents.cs ──
	/// <summary>Master switch for everything during Steam events: the awards, the sale's extras, Next Fest, the sticker and the queue.</summary>
	public bool SaleEvents { get; set; } = true;

	/// <summary>On a human-mode account, the awards, the sale's extras and Next Fest happen at all (inside its day). Robots always may.</summary>
	/// <remarks>
	/// On by default since 1.8.0 was finished. Every setting is written to the file, so the "false" earlier 1.8.0 builds
	/// wrote there (as "SaleEventsInHumanMode") is the old default, not a choice - the setting has a new name, so a file
	/// with only the old one gets the new default. An off chosen from now on is written under the new name and stays off.
	/// </remarks>
	public bool SaleEventsHumanToo { get; set; } = true;

	public bool SaleAwardsNominate { get; set; } = true;
	public bool SaleAwardsVote { get; set; } = true;

	/// <summary>The sale's extra item after the discovery queue (claimed the way the queue page claims it).</summary>
	public bool SaleQuests { get; set; } = true;

	public bool SaleNextFest { get; set; } = true;

	/// <summary>0 the game it played most, 1 any game it has played.</summary>
	public int SaleNominatePick { get; set; }

	public int SaleGapMinSeconds { get; set; } = 5;
	public int SaleGapMaxSeconds { get; set; } = 20;
	public int SaleHumanGapMinMinutes { get; set; } = 5;
	public int SaleHumanGapMaxMinutes { get; set; } = 30;
	public int SaleCheckHours { get; set; } = 6;
	// ── Steam Points into levels (see Modules/PointsSpender.cs) ──

	/// <summary>
	/// Spend Steam Points on badge levels by itself. Off by default on every account, robots too: nothing that uses up an
	/// account's things happens on a freshly added one until it's turned on. Switching human mode on or off never changes it
	/// (<see cref="Settings.ApplyLegitMode"/>): it was turned on through its popup, by its owner. Nothing is ever spent before
	/// spending has its go-ahead ('points on &lt;account&gt;', or this switch after its preview - Modules.PointsLedger).
	/// </summary>
	public bool SpendPoints { get; set; }

	/// <summary>Never spend below this many points.</summary>
	public int PointsKeepAtLeast { get; set; } = 1000;

	/// <summary>The most points spent in any 7 days. 0 spends nothing.</summary>
	public int PointsWeeklyCap { get; set; } = 3000;

	/// <summary>May buy levels of the Points Shop's current seasonal badge (the only thing there that gives XP outside events).</summary>
	public bool PointsBuySeasonalBadge { get; set; } = true;

	/// <summary>May buy levels of older seasonal badges the shop still lists. Off: Steam has refused some of those.</summary>
	public bool PointsBuyOlderBadges { get; set; }

	/// <summary>Stop each badge at this level (Steam's badges go to 40).</summary>
	public int PointsBadgeMaxLevel { get; set; } = 40;

	/// <summary>How often it looks at the Points Shop and the balance, in hours.</summary>
	public int PointsCheckEveryHours { get; set; } = 12;

	/// <summary>
	/// Where the go-ahead to spend Steam Points used to be kept. It lives with the account's Steam Points spending now
	/// (Modules.PointsLedger, state/points-&lt;account&gt;.json), so it never travels with a settings file or a backup: a
	/// true here is moved over once (PointsLedger.Approved) and then cleared, and never read again. Not a setting.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool PointsOk { get; set; }

	// ── Buying cards (see Modules/CardBuyer.cs) - 1.8.2 ──

	/// <summary>
	/// Buy the missing cards of the badges it's levelling, at the cheapest listing. Off by default on every account: it spends
	/// the wallet's money. Turned on only through its preview (Core/Switches.cs), its go-ahead kept beside its spending
	/// (Modules.BuyLedger), never here.
	/// </summary>
	public bool BuyCards { get; set; }

	/// <summary>
	/// Also buy the cheapest whole sets, for any game. On by default since 1.8.2's quick set finder - it only does anything
	/// while <see cref="BuyCards"/> is on; a settings file that already says keeps what it says.
	/// </summary>
	public bool BuyFullSets { get; set; } = true;

	/// <summary>The most it spends in any 24 hours, in the wallet's own currency. Below 0 (-1): no limit - whatever the wallet holds.</summary>
	public float BuyDailyLimit { get; set; } = 1.50f;

	/// <summary>1.8.5: "Always keep in the wallet" - buying never takes the wallet below this (wallet currency; 0: off).</summary>
	public float BuyKeepInWallet { get; set; }

	/// <summary>The most it spends in any 7 days, in the wallet's own currency. Below 0 (-1): no limit - whatever the wallet holds.</summary>
	public float BuyWeeklyLimit { get; set; } = 8.00f;

	/// <summary>
	/// The most it pays for one badge level (100 XP) - a whole set, or a started badge's missing cards - in the wallet's own
	/// currency. 0: automatic, a little over what a level usually costs (BuyPlan.UsualLevel). A dearer level waits.
	/// </summary>
	public float BuyMaxLevelPrice { get; set; }

	/// <summary>Buying stops once the account's Steam level is this or more. 0: never.</summary>
	public int BuyStopAtLevel { get; set; }

	/// <summary>Sets one look buys on a human-mode account (a robot buys every one that fits).</summary>
	public int BuyHumanSetsPerLook { get; set; } = 3;

	/// <summary>Minutes between two looks that buy on a human-mode account while money and cheap levels are left, picked at random in between, only in its day.</summary>
	public int BuyHumanGapMinMinutes { get; set; } = 30;
	public int BuyHumanGapMaxMinutes { get; set; } = 90;

	/// <summary>How often it looks, in hours.</summary>
	public int BuyCheckHours { get; set; } = 12;

	/// <summary>
	/// Retired "Sell duplicate cards" switch - carried over to <see cref="SpareItems"/> (ConfigStore.MigrateSpare). Still
	/// written, for a rollback to 1.7.x, whose seller lists cards straight away with no preview: true only when this version
	/// would sell on the account too - Spare items on, an action that sells, and its go-ahead given (worked out as it's saved,
	/// <see cref="SellMirror"/>). Read only from a file without SpareItems.
	/// </summary>
	public bool SellDuplicates {
		get => _spareItemsSet ? _sellMirror : _sellDuplicates;
		set => _sellDuplicates = value;
	}

	private bool _sellDuplicates, _sellMirror;

	/// <summary>What <see cref="SellDuplicates"/> is written as - set on the copy that goes to disk (ConfigStore.SaveBot), false otherwise.</summary>
	[JsonIgnore]
	internal bool SellMirror {
		set => _sellMirror = value;
	}

	/// <summary>Whether 1.7.x may sell on this account after a rollback: this version would sell too (needs the account's go-ahead).</summary>
	internal static bool SellsAfterRollback(BotConfig cfg, bool approved) => cfg.SpareItems && (cfg.SpareAction is 0 or 2) && approved;

	/// <summary>The old switch as a file from before 1.8.0 had it - for the carry-over only.</summary>
	[JsonIgnore]
	internal bool OldSellDuplicates => _sellDuplicates;

	public int SellPerRun { get; set; } = 5;

	// ── spare items (1.8.0) ──
	/// <summary>Spare items on: turn spare cards, backgrounds and emoticons into gems or sell them, once turned on after its preview.</summary>
	public bool SpareItems {
		get => _spareItems;
		set {
			_spareItems = value;
			_spareItemsSet = true;
		}
	}

	private bool _spareItems;

	/// <summary>SpareItems was in the file (or set since) - an old SellDuplicates beside it is never read as a choice.</summary>
	[JsonIgnore]
	public bool SpareItemsSet => _spareItemsSet;

	private bool _spareItemsSet;

	/// <summary>0 best of gems or selling, 1 gems only, 2 sell only, 3 keep everything (just report).</summary>
	public int SpareAction { get; set; }
	public bool SpareMaxedBadgeCards { get; set; } = true;
	public bool SpareExtraCards { get; set; } = true;
	/// <summary>Copies of each card, background and emoticon always kept (1.8.2: one number where there were three).</summary>
	public int SpareKeepCopies { get; set; } = 1;
	public bool SpareBackgrounds { get; set; } = true;
	public bool SpareEmoticons { get; set; } = true;

	/// <summary>Retired keep-counts of backgrounds and emoticons - only read: the largest of the three becomes
	/// <see cref="SpareKeepCopies"/> (<see cref="ConfigStore.MigrateSpareKeep"/>). Never written now.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? SpareKeepBackgrounds { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? SpareKeepEmoticons { get; set; }
	public bool SpareFoils { get; set; }

	/// <summary>Never touch items worth more than this, in the account's own currency.</summary>
	public float SpareMaxPrice { get; set; } = 0.25f;
	public int SpareNewDays { get; set; } = 7;
	public string SpareKeepItems { get; set; } = "";
	public List<uint> SpareKeepGames { get; set; } = [];
	public int SpareGemMarginPct { get; set; } = 20;
	public int SparePerRun { get; set; } = 5;
	public int SpareDailyItems { get; set; } = 10;
	public float SpareDailyValue { get; set; } = 1.00f;
	public int SpareRunHours { get; set; } = 12;
	public int SpareGapSeconds { get; set; } = 40;
	public int SparePriceMaxAgeHours { get; set; } = 24;

	/// <summary>0 a cent under the real price, 1 the same as the cheapest listing, 2 the median sale price, 3 a cent under the
	/// cheapest (back in 1.8.2, never following a dumped listing down - see ListingPrice.UnderCheapest). Anything else becomes 0
	/// (ConfigStore.MigrateSellPrice).</summary>
	public int SpareSellPrice { get; set; }

	/// <summary>A cheapest listing this many percent or more under the median sale price is a dump and doesn't set the price.</summary>
	public int SpareDumpPct { get; set; } = 50;
	public int SpareRelistDays { get; set; } = 7;

	/// <summary>0 automatic (sacks kept on a human-mode account, loose on the others), 1 loose, 2 sacks kept, 3 sacks sent, 4 sacks sold.</summary>
	public int SpareGems { get; set; }
	public string SpareGemsSendTo { get; set; } = "";
	public int SpareKeepGems { get; set; }

	// ── spare items (1.8.1) ──
	/// <summary>Only sell when the account would get at least this, in its own wallet currency - below it the item goes to
	/// gems when they're worth anything, and is kept otherwise.</summary>
	public float SpareMinSale { get; set; } = 0.05f;

	/// <summary>Days an item whose listing you declined on your phone isn't listed again; 0 never again.</summary>
	public int SpareDeclinedDays { get; set; } = 30;

	/// <summary>An unsold listing steps down a cent at a time, never under this percent of what it sold for.</summary>
	public int SpareRelistFloorPct { get; set; } = 80;

	/// <summary>Minutes between two items on a human-mode account, picked at random in between each time.</summary>
	public int SpareHumanGapMinMinutes { get; set; } = 3;
	public int SpareHumanGapMaxMinutes { get; set; } = 20;

	/// <summary>Items one look does on a human-mode account; the rest wait for a later look in its day.</summary>
	public int SpareHumanPerLook { get; set; } = 3;

	// ── booster packs (1.8.0) ──
	/// <summary>0 never, 1 only when the cards are worth more than the pack, 2 always.</summary>
	public int OpenBoosterPacks {
		get => _openBoosterPacks;
		set {
			_openBoosterPacks = value;
			_openBoosterPacksSet = true;
		}
	}

	/// <summary>Never, by default on every account: opening a pack gets rid of it, and nothing does that until it's chosen.</summary>
	private int _openBoosterPacks;

	/// <summary>OpenBoosterPacks was in the file (or set since).</summary>
	[JsonIgnore]
	public bool OpenBoosterPacksSet => _openBoosterPacksSet;

	private bool _openBoosterPacksSet;
	public int BoosterOpenNewDays { get; set; } = 7;
	public List<uint> BoosterNeverOpen { get; set; } = [];

	/// <summary>Packs it doesn't open: 0 keep, 1 send to another of your accounts, 2 sell.</summary>
	public int BoosterUnopened { get; set; }
	public string BoosterSendTo { get; set; } = "";
	public int BoosterCheckHours { get; set; } = 24;
	public bool ClearInventoryNotifications { get; set; } = true;

	// Everything else in Steam's notification tray - comments, gifts, help requests, friend invites. None of
	// these counters fall on their own, so on an account nobody signs into by hand they only ever climb.
	public bool ClearNotifications { get; set; } = true;

	// ── per-account proxy ──
	public string AccountProxy { get; set; } = "";
	public string AccountProxyUsername { get; set; } = "";
	public string AccountProxyPassword { get; set; } = "";

	// ── rep4rep ──
	public bool Rep4Rep { get; set; }
	public int Rep4RepDailyCap { get; set; } = 10;
	public int Rep4RepGapMinMinutes { get; set; } = 10;
	public int Rep4RepGapMaxMinutes { get; set; } = 25;
	public int Rep4RepStartHour { get; set; } = 10;
	public int Rep4RepEndHour { get; set; } = 23;
	public bool Rep4RepLearnCap { get; set; } = true;
	public bool Rep4RepRetryRefused { get; set; } = false;

	// ── human mode ──
	public bool LegitMode { get; set; }
	public string GameWeights { get; set; } = "";

	/// <summary>Hours to reach in given games, optionally by a date: "730:100@2026-12-01, 440:50".</summary>
	public string HourTargets { get; set; } = "";

	// how the day is shaped
	public int WeekdayHours { get; set; } = 6;
	public int WeekendHours { get; set; } = 9;

	/// <summary>1.8.3: how far a day's hours may stray from the weekday/weekend hours, in percent either way. 0: the usual
	/// spread (a quiet day 55-80%, a normal 80-105%, a long one 105-125%).</summary>
	public int HoursVaryPct { get; set; }
	public int DayOffChancePct { get; set; } = 5;
	// When it gets on and goes to bed: a time of day in minutes from midnight (11:30 is 690), and how far either way a
	// day may land - a hard limit, nothing the day's roll does takes it outside. Bed at or before the time it gets on is
	// the next morning's.
	//
	// DayStartHour and BedHour are the hour alone, from before minutes could be set. Still read, so a file written before
	// means what it meant (DayStartHour 11 is 11:00), and still written, so an older version put back reads the hour.
	// Declared before the time, so the time in the same file - read after it - keeps its minutes; setting the hour keeps
	// the minutes too, so the order never matters.
	public int DayStartHour {
		get => DayStartTime / 60;
		set => DayStartTime = (Math.Clamp(value, 0, 23) * 60) + (((DayStartTime % 60) + 60) % 60);
	}

	public int DayStartTime { get; set; } = 13 * 60;
	public int DayStartVaryMinutes { get; set; } = 120;

	public int BedHour {
		get => BedTime / 60;
		set => BedTime = (Math.Clamp(value, 0, 23) * 60) + (((BedTime % 60) + 60) % 60);
	}

	public int BedTime { get; set; } = 2 * 60;
	public int BedVaryMinutes { get; set; } = 60;
	public int LateNightExtraHours { get; set; } = 2;
	public int SleepMinHours { get; set; } = 6;
	public int SleepMaxHours { get; set; } = 9;

	// How the games are split.
	//
	// One number governs this now, and it lives in GameWeights where it is visible next to the games it
	// applies to. There used to be three: the share written against the main game in GameWeights (silently
	// discarded), MainGameSharePct (which actually decided it), and SideGameSharePct (a second, independent
	// cap on the same minutes). Two of the three could disagree with each other, and by default they did -
	// 70 implied a 30% side share while SideGameSharePct shipped at 18, so the picker aimed for one figure
	// and a budget it could not see cut it off at another.
	public int PureMainDayChancePct { get; set; } = 25;

	/// <summary>
	/// Retired, and read only so an existing config still means what it meant. A file written before the share
	/// moved into GameWeights carries the main game's percentage here, and the games list may well carry no
	/// number at all against the main game - loading that as-is would quietly re-cut somebody's whole schedule.
	/// <see cref="ConfigStore.MigrateGameShares"/> folds it into the list and zeroes this, at which point
	/// WhenWritingDefault drops it from the file for good.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int MainGameSharePct { get; set; }

	// A life, not just a day. All four are off unless switched on, so an account set up before them plays exactly as it did.

	/// <summary>Learn when you play on the account and what, and lean its day that way: 0 off, 1 a little, 2 a lot.</summary>
	public int LearnFromOwner { get; set; }

	/// <summary>How fast what it learned follows a change in your routine: 0 slowly, 1 normal, 2 quickly - see <see cref="Modules.OwnerHabits.HalfLife"/>.</summary>
	public int LearnFollow { get; set; } = 1;

	/// <summary>Learn your Saturdays and Sundays apart from the rest of the week.</summary>
	public bool LearnWeekends { get; set; } = true;

	/// <summary>A game that just arrived gets extra sittings for a few days, fewer each day.</summary>
	public bool NewGamesFirst { get; set; }

	/// <summary>Now and then a quiet spell of shorter days, and some Friday and Saturday nights a late one.</summary>
	public bool LongerRhythms { get; set; }

	/// <summary>Now and then a sitting on what a friend is playing right then.</summary>
	public bool JoinFriends { get; set; }

	/// <summary>Human mode's reaction waits and breaks: <see cref="Modules.ReactionSpeed"/> - normal (as set), quick (halved), relaxed (doubled).</summary>
	public int ReactionSpeed { get; set; }

	// settling in after a login
	public int WarmUpMinMinutes { get; set; } = 3;
	public int WarmUpMaxMinutes { get; set; } = 20;

	// sessions and breaks
	public int SessionMinMinutes { get; set; } = 30;
	public int SessionMaxMinutes { get; set; } = 150;
	public int BreakMinMinutes { get; set; } = 2;
	public int BreakMaxMinutes { get; set; } = 30;
	public int MealBreaksPerDay { get; set; } = 2;
	public int MealBreakMinutes { get; set; } = 35;
	public int SignOutOnBreakChancePct { get; set; } = 30;
	public int MaxSignOutsPerDay { get; set; } = 7;

	// overnight
	public bool OfflineIdleAtNight {
		get => _offlineIdleAtNight;
		set {
			_offlineIdleAtNight = value;
			_offlineIdleAtNightSet = true;
		}
	}

	private bool _offlineIdleAtNight = true;
	public List<uint> OfflineIdleGames { get; set; } = [];

	/// <summary>With the overnight list empty, this many of the account's most-played games bank the night instead.</summary>
	public int OfflineIdleTopGames {
		get => _offlineIdleTopGames;
		set {
			_offlineIdleTopGames = value;
			_offlineIdleTopGamesSet = true;
		}
	}

	private int _offlineIdleTopGames = 1;

	/// <summary>
	/// Which of the two were in the file: a file with the switch but not the top-games number is from before the number
	/// existed - when "on" with an empty list banked nothing (<see cref="ConfigStore.MigrateOvernight"/>).
	/// </summary>
	[JsonIgnore]
	public bool OfflineIdleAtNightSet => _offlineIdleAtNightSet;

	private bool _offlineIdleAtNightSet;

	[JsonIgnore]
	public bool OfflineIdleTopGamesSet => _offlineIdleTopGamesSet;

	private bool _offlineIdleTopGamesSet;

	/// <summary>Values overwritten when Legit mode was switched on, so switching it off restores them.</summary>
	public string LegitBackup { get; set; } = "";

	// ── friends & messages ──
	public bool AcceptFriendRequests { get; set; }
	public bool AcceptGroupInvites { get; set; }
	/// <summary>This account joins the groups in the global "Groups every account joins" list. On for a robot account; a
	/// new human-mode account starts with it off (<see cref="Settings.NewHumanAccount"/>) - a believable account isn't in a
	/// group named after the farming tool, on its public profile.</summary>
	public bool JoinGroup { get; set; } = true;

	/// <summary>Steam groups only this account joins, on top of the global list.</summary>
	public string ExtraGroupsToJoin { get; set; } = "";
	public bool IgnoreSuspiciousInvites { get; set; } = true;
	public bool RejectInvalidFriendInvites { get; set; }
	/// <summary>
	/// Whether to auto-reply at all. Off for a new account since 1.8.2 - on with no text it replied to nobody and read as
	/// if it did; emptying the text turns it off (Settings.Follow). An account from before keeps what it had
	/// (<see cref="ConfigStore.MigrateNewDefaults"/>).
	/// </summary>
	public bool AutoReplyEnabled { get; set; }

	public string AutoReply { get; set; } = "";
	public bool AutoReplyOncePerDay { get; set; } = true;
	public int AutoReplyDelaySeconds { get; set; } = 20;
	public int FriendRequestDelayMinMinutes { get; set; } = 1;
	public int FriendRequestDelayMaxMinutes { get; set; } = 45;
	public string CommandMasters { get; set; } = "";

	/// <summary>Hold every reaction - trades, replies, friend requests - until human mode says it is awake.</summary>
	public bool ActOnlyWhileAwake { get; set; } = true;

	/// <summary>Human mode: things nobody watches (badges, free games, boosters...) also wait for the waking day.</summary>
	public bool QuietThingsWaitForDay { get; set; }

	/// <summary>Human mode: after waking or signing in, how long before things other people see.</summary>
	public int WakeDelayMinMinutes { get; set; } = 10;
	public int WakeDelayMaxMinutes { get; set; } = 90;

	/// <summary>Human mode: after signing in, how long before things nobody watches.</summary>
	public int QuietDelayMinMinutes { get; set; } = 5;
	public int QuietDelayMaxMinutes { get; set; } = 60;

	/// <summary>Human mode: how far into a break its Away / offline status kicks in.</summary>
	public int BreakAwayAfterMinMinutes { get; set; } = 2;
	public int BreakAwayAfterMaxMinutes { get; set; } = 10;

	/// <summary>Human mode: answer one trade offer per look, the rest on later looks.</summary>
	public bool OneTradeAtATime { get; set; } = true;

	// ── trades ──
	public bool AcceptDonations { get; set; } = true;

	/// <summary>Human mode: donations still go through at night, while everything else waits for morning.</summary>
	public bool DonationsWhileAsleep { get; set; }
	public bool AcceptGifts { get; set; } = true;
	public bool AcceptGiftedGames { get; set; } = true;
	public int GiftDelayMinMinutes { get; set; } = 2;
	public int GiftDelayMaxMinutes { get; set; } = 15;
	public bool AcceptFairCardSwaps { get; set; }

	// ── 1.8.2: card swaps with other nocat.farm users (Core/CardExchange.cs) ──
	/// <summary>
	/// Swap spare normal cards one for one with other nocat.farm users' accounts. Only turned on through its preview: the
	/// go-ahead lives in config/state/cardswaps-&lt;account&gt;.json, never here (see <see cref="Core.Switches"/>).
	/// </summary>
	public bool CardSwaps { get; set; }

	/// <summary>The most a swap may be worth less than what it gives, in the wallet's currency. 0: never worth less.</summary>
	public float CardSwapMaxLoss { get; set; } = 0.03f;

	/// <summary>Never swap with an account when the trade would be held.</summary>
	public bool CardSwapSkipHeld { get; set; } = true;

	/// <summary>Swaps sent or accepted in a day, at most.</summary>
	public int CardSwapsPerDay { get; set; } = 5;

	/// <summary>
	/// Retired "Accept anything from your own accounts" (1.8.2): "Your other accounts" (<see cref="AutoTradeWith"/>) is the one
	/// list now. On in an old file with that list empty, the accounts in <see cref="TradeMasters"/> go into it both ways -
	/// exactly what it did (<see cref="ConfigStore.MigrateOwnAccounts"/>). Only read; never written.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? AcceptFromMasters { get; set; }

	/// <summary>
	/// Where items are sent ("Send items to"): account names or SteamID64s, the first one the receiver of
	/// <see cref="SendEveryHours"/>, <see cref="SendOnFarmingFinished"/> - and of the gem sacks and unopened packs when their
	/// own "send to" is empty. Sending only: nobody listed here may take anything (that's <see cref="AutoTradeWith"/>).
	/// </summary>
	public string TradeMasters { get; set; } = "";

	/// <summary>
	/// "Your other accounts": who this account trades with by itself, and which way - names or SteamID64s, each optionally
	/// ":from" (accept what they send), ":to" (let them take items - confirmed by itself when the authenticator is here) or
	/// ":both" (the default). Empty: nobody - every offer waits for you (trade accept / trade decline), donations aside.
	/// </summary>
	public string AutoTradeWith { get; set; } = "";
	public bool DeclineOtherTrades { get; set; }
	public int TradeDelayMinMinutes { get; set; } = 2;
	public int TradeDelayMaxMinutes { get; set; } = 15;
	public string SendItemTypes { get; set; } = "cards";
	public string TradeMasterToken { get; set; } = "";
	public bool SendOnFarmingFinished { get; set; }

	/// <summary>Send items to the first trade master every this many hours. 0 is off.</summary>
	public int SendEveryHours { get; set; }

	/// <summary>The hour of the day (0-23) the timed send goes out around; -1 for no set time (every SendEveryHours
	/// from when it was switched on).</summary>
	public int SendAroundHour { get; set; } = -1;

	// ── staying out of the way ──
	public bool PauseWhenYouPlay { get; set; } = true;
	public int ResumeDelayMinutes { get; set; } = 5;

	// ── Open in Steam (1.8.2) ── whether the one-click "sign my Steam app into this account" button is shown. Off by
	// default; its go-ahead lives outside the settings (SteamOpenLedger), so this alone can't switch it on - the
	// preview's Turn on does. Windows only.
	public bool SteamOpenButton { get; set; }
}

/// <summary>
/// The global config as it stands right now. A handful of things deep in the engine (reconnect timing, the
/// farming concurrency cap) need it without having it threaded through five constructors; this is that, and
/// <see cref="Core.BotManager.ApplyGlobal"/> is the only thing that writes it.
/// </summary>
public static class Live {
	public static GlobalConfig Global { get; set; } = new();
}

public static partial class ConfigStore {
	public static string Root { get; private set; } = AppContext.BaseDirectory;
	public static string ConfigDir => Path.Combine(Root, "config");
	public static string GlobalPath => Path.Combine(ConfigDir, "nocatFarm.json");

	private static readonly JsonSerializerOptions Json = new() {
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never
	};

	public static void UseRoot(string root) {
		// One spelling per folder. "--path data", "--path .\data\" and the full path are the same place, and the
		// single-instance lock is named after Root - left as typed, each spelling got its own lock and two copies
		// could run on one config at once.
		Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		Directory.CreateDirectory(ConfigDir);
	}

	/// <summary>
	/// What a file name can't have on Windows - the strictest of the three - used on every system. Linux only refuses "/"
	/// and a NUL, so an import there could make an account called "a:b" or "why?" that a backup then left out without a
	/// word, and that a config folder carried to Windows couldn't hold.
	/// </summary>
	private static readonly char[] NotInAName = [.. "\"<>|:*?\\/", ..Enumerable.Range(0, 32).Select(static c => (char) c)];

	/// <summary>A bot name has to be usable as a file name on every system and must not walk out of the config directory.</summary>
	public static bool IsValidBotName(string name) =>
		!string.IsNullOrWhiteSpace(name)
		&& (name[0] != '.')
		&& !name.Equals("nocatFarm", StringComparison.OrdinalIgnoreCase)
		&& (name.IndexOfAny(NotInAName) < 0)
		&& (Path.GetRelativePath(".", name) == name)
		&& !IsReservedName(name);

	private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase) {
		"CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
	};

	/// <summary>
	/// A name Windows keeps for a device - con, prn, aux, nul, com1-9, lpt1-9 - with or without an extension ("nul.txt"
	/// is nul too). config\con.json isn't a file there: saving the account went nowhere or failed, and it was gone at the
	/// next start - and a folder carried over from Linux with one in it couldn't be copied back.
	/// </summary>
	public static bool IsReservedName(string name) {
		string stem = name.Split('.')[0].TrimEnd(' ');

		return WindowsDevices.Contains(stem);
	}

	/// <summary>Why a name can't be one - for the add paths, which say so rather than a general "can't be used".</summary>
	public static Core.Said? NameProblem(string name) =>
		IsReservedName(name) ? new Core.Said("'{0}' is a name Windows keeps for itself (like con, nul or com1) - pick another", name)
		: name.Equals("all", StringComparison.OrdinalIgnoreCase) ? new Core.Said("'all' means every account in a command - pick another name")
		: null;

	/// <summary>
	/// Every list and text left null - "IdleGames": null typed into a file by hand, or sent that way - put back to its
	/// default. JSON takes an explicit null over the default, and a null list crashed whatever read it next: turning on
	/// human mode, an import's settings step half-way through, the idler.
	/// </summary>
	public static T FillNulls<T>(T config) where T : class, new() {
		T fresh = new();

		foreach (System.Reflection.PropertyInfo p in typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
			if (p.CanRead && p.CanWrite && (p.GetIndexParameters().Length == 0) && !p.PropertyType.IsValueType
				&& (p.GetValue(config) == null) && (p.GetValue(fresh) is { } fallback)) {
				p.SetValue(config, fallback);
			}
		}

		return config;
	}

	/// <summary>
	/// A name for a NEW account: letters, numbers, dashes and underscores only - anything else turns into a different
	/// command once typed in the console - and never "all", which every command reads as every account.
	/// </summary>
	public static bool IsPlainBotName(string name) =>
		(name.Length > 0) && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
		&& !name.Equals("all", StringComparison.OrdinalIgnoreCase);

	public static GlobalConfig LoadGlobal() {
		Directory.CreateDirectory(ConfigDir);
		LanguageFromComputer = null;

		if (!File.Exists(GlobalPath)) {
			GlobalConfig fresh = new();

			// A brand-new install starts in the computer's own language, written in like any other default. The installer's
			// pick (--setup Language=) goes on after this, so it still wins; an existing config never comes here.
			LanguageFromComputer = SystemLanguage.Probe();

			if (LanguageFromComputer != null) {
				fresh.Language = LanguageFromComputer;
			}

			SaveGlobal(fresh);

			return fresh;
		}

		try {
			string text = File.ReadAllText(GlobalPath);
			GlobalConfig loaded = FillNulls(JsonSerializer.Deserialize<GlobalConfig>(text, Json) ?? new GlobalConfig());

			// "pt-br" typed into the file by hand is pt-BR: the language packs are files, and off Windows a file name's case
			// counts - pt-br.json wasn't found, and the dashboard and the log stayed English.
			if ((Settings.FindGlobal("Language") is { } language)
				&& (Settings.ParsePicks(language).FirstOrDefault(p => p.Value.Equals(loaded.Language, StringComparison.OrdinalIgnoreCase)) is { Value: { } code })) {
				loaded.Language = code;
			}

			// Anything still in plain text - typed into the file by hand, or left by an older version - is written back
			// encrypted as soon as it has been read, not whenever a setting next happens to be saved.
			bool plain = new[] { loaded.DiscordWebhookUrl, loaded.TelegramBotToken, loaded.DiscordBotToken, loaded.WebPassword,
				loaded.Rep4RepApiToken, loaded.WebProxyPassword }.Any(Secrets.IsPlain);

			loaded.DiscordWebhookUrl = Secrets.Unprotect(loaded.DiscordWebhookUrl);
			loaded.TelegramBotToken = Secrets.Unprotect(loaded.TelegramBotToken);
			loaded.DiscordBotToken = Secrets.Unprotect(loaded.DiscordBotToken);

			// Unprotect hands plain text straight back, so a file from before these were encrypted - or one typed
			// in by hand - reads exactly as it always did, and is encrypted on the next save.
			loaded.WebPassword = Secrets.Unprotect(loaded.WebPassword);
			loaded.Rep4RepApiToken = Secrets.Unprotect(loaded.Rep4RepApiToken);
			loaded.WebProxyPassword = Secrets.Unprotect(loaded.WebProxyPassword);
			plain |= SecretSettings.Open(loaded);   // every other secret setting (the backup password), by its kind

			// It loads, so it can be saved again. A file that was broken once and fixed ('reload') used to stay refused for
			// the rest of the run: every change after that said it was saved and was gone at the next start.
			_globalBroken = false;
			_brokenSaveSaid = false;
			GlobalLoadProblem = null;

			bool migrated = MigrateUpdates(loaded);
			migrated |= MigrateDiscordNames(loaded, text);
			migrated |= MigrateShareTotals(loaded, text);
			// 1.8.2: settings that became one - the alert chips, backup messages, the summary's time, "don't look for updates".
			migrated |= MigrateAlertRouting(loaded);
			migrated |= MigrateBackupMessages(loaded);
			migrated |= MigrateDailyReportTime(loaded);
			migrated |= MigrateCheckForUpdates(loaded);

			// 1.8.2: "Open in Steam buttons on every account" on in the file without its go-ahead (a file copied from another
			// PC, typed in by hand) goes back off - it's turned on only through its preview.
			migrated |= Core.Switches.SettleEveryone(loaded);

			if ((plain && Secrets.Available) || migrated) {
				SaveGlobal(loaded);
			}

			return loaded;
		} catch (Exception e) {
			// Keep the broken file. Falling back to defaults and then saving over it would destroy the rep4rep
			// token and the dashboard password because of one stray comma.
			string kept = GlobalPath + ".broken";

			// Not always bad JSON: a file it isn't allowed to read, or one another program holds, lands here too - the
			// exception's type says which. Kept for Program to file once logging exists: at start this runs before it does.
			string why = $"config: {GlobalPath} didn't load: {Log.Describe(e)}";
			GlobalLoadProblem = why;
			Log.Debug(why);

			try {
				File.Copy(GlobalPath, kept, true);
			} catch (Exception x) {
				// best effort
				GlobalLoadProblem += $" (and the copy to {Path.GetFileName(kept)} failed: {Log.Describe(x)})";
				Log.Failed($"config: keeping a copy as {kept}", x);
			}

			Log.Error(new Said("config: {0} is not valid JSON ({1})", Path.GetFileName(GlobalPath), Log.Scrub(e.Message)));
			Log.Warn(new Said("a copy was kept as {0}; running on defaults until you fix it", Path.GetFileName(kept)));

			// And nothing is written over it while running like this - not a setting, not the window's position on
			// the way out - or a copy fixed by hand in the meantime would be replaced with these defaults.
			_globalBroken = true;

			return new GlobalConfig();
		}
	}

	/// <summary>The global config didn't load, so the app is on defaults and must not save them over the file.</summary>
	private static bool _globalBroken;

	/// <summary>The last read of the global config failed - what it returned is defaults, not the settings.</summary>
	public static bool GlobalBroken => _globalBroken;

	/// <summary>The language a brand-new config was given from this computer's own; null when there was a config already
	/// (or the computer didn't say). For Program to tell once logging exists.</summary>
	public static string? LanguageFromComputer { get; private set; }

	/// <summary>Why the global config last failed to load, as a log line; null when it loaded.</summary>
	public static string? GlobalLoadProblem { get; private set; }

	private static bool _brokenSaveSaid;

	/// <summary>
	/// Saves come from the window, the dashboard, the console and the modules, on their own threads. One at a time,
	/// and whole-or-nothing: a crash half way through a plain write left a truncated file that didn't load next time.
	/// </summary>
	private static readonly Lock SaveGate = new();

	/// <summary>
	/// Held by whatever changes the live global settings and saves them - the dashboard's save, 'set', a plugin's switch,
	/// an import - and by 'reload' and a restore while they put a new one in its place. Without it a change made while
	/// 'reload' swapped the object went onto the one being thrown away, and two changes to one list lost one of them.
	/// </summary>
	public static Lock GlobalEditGate { get; } = new();

	/// <summary>Which restore is putting files back right now (0 = none); see <see cref="BeginRestore"/>.</summary>
	private static int _restoring;

	private static int _restores;

	/// <summary>The restore this code is running for - only it may save while its files go back.</summary>
	private static readonly AsyncLocal<int> Restorer = new();

	/// <summary>
	/// A restore is putting its files back: until the handle is disposed, only the restore itself saves. A dashboard save
	/// or a module's save in the meantime wrote the old settings over the restored files - or its move collided with the
	/// restore's and the restore stopped half way. Taken under the save lock, so a save already writing finishes first.
	/// </summary>
	public static IDisposable BeginRestore() {
		int id = Interlocked.Increment(ref _restores);

		lock (SaveGate) {
			_restoring = id;
		}

		Restorer.Value = id;

		return new RestoreScope(id);
	}

	private sealed class RestoreScope(int id) : IDisposable {
		private int _done;

		public void Dispose() {
			if (Interlocked.Exchange(ref _done, 1) != 0) {
				return;
			}

			lock (SaveGate) {
				if (_restoring == id) {
					_restoring = 0;
				}
			}

			Restorer.Value = 0;
		}
	}

	/// <summary>A restore is writing its files and this isn't it: a save now would go over them. Read under the save lock.</summary>
	private static bool HeldByRestore => (_restoring != 0) && (Restorer.Value != _restoring);

	/// <summary>
	/// A restore is writing its files right now (and this isn't the restore asking). The state stores that save on their
	/// own timers - history, lifetime totals, the key queue - hold off: a save in that window put pre-restore numbers
	/// back over the restored file just before the restore read it in.
	/// </summary>
	public static bool RestoreWriting => HeldByRestore;

	/// <summary>Why the last global save didn't reach the disk, in plain words; null once one has.</summary>
	/// <remarks>For a caller with no log to read it from - the installer's --setup runs before logging exists.</remarks>
	public static string? LastSaveProblem { get; private set; }

	/// <returns>False when nothing reached the disk, so a caller that told somebody "saved" can say otherwise.</returns>
	public static bool SaveGlobal(GlobalConfig cfg) {
		if (_globalBroken) {
			Log.Debug("config: not saving nocatFarm.json - it didn't load, and saving now would put defaults over it");
			LastSaveProblem = $"{GlobalLoadProblem ?? $"config: {GlobalPath} didn't load"} - not saved over it; fix it or delete it";

			// Once, in plain words, the first time a change is lost this way - the warning at start has long scrolled by.
			if (!_brokenSaveSaid) {
				_brokenSaveSaid = true;
				Log.Warn(new Said("settings not saved - {0} didn't load; fix it or delete it", Path.GetFileName(GlobalPath)));
			}

			return false;
		}

		try {
			lock (SaveGate) {
				if (HeldByRestore) {
					Log.Debug("config: not saving nocatFarm.json - a backup is being restored over it");
					LastSaveProblem = "a backup is being restored - not saved over it";

					return false;
				}

				Directory.CreateDirectory(ConfigDir);

				// A copy with the notification secrets encrypted - the live config keeps them readable. Either one
				// can post as you (the webhook) or run your bot (the tokens), so none sits in the file in the clear.
				GlobalConfig onDisk = JsonSerializer.Deserialize<GlobalConfig>(JsonSerializer.Serialize(cfg, Json), Json)!;
				onDisk.DiscordWebhookUrl = Secrets.Protect(cfg.DiscordWebhookUrl, "global");
				onDisk.TelegramBotToken = Secrets.Protect(cfg.TelegramBotToken, "global");
				onDisk.DiscordBotToken = Secrets.Protect(cfg.DiscordBotToken, "global");

				// The same for the dashboard password, the rep4rep token and the proxy password - all three are
				// secrets settings, and they were the only ones still written out in the clear.
				onDisk.WebPassword = Secrets.Protect(cfg.WebPassword, "global");
				onDisk.Rep4RepApiToken = Secrets.Protect(cfg.Rep4RepApiToken, "global");
				onDisk.WebProxyPassword = Secrets.Protect(cfg.WebProxyPassword, "global");
				SecretSettings.Seal(onDisk, cfg);   // every other secret setting (the backup password), by its kind
				AtomicFile.Write(GlobalPath, JsonSerializer.Serialize(onDisk, Json));
			}

			LastSaveProblem = null;

			return true;
		} catch (Exception e) {
			LastSaveProblem = $"couldn't write {GlobalPath}: {Log.Describe(e)}";
			Log.Warn(new Said("config: couldn't save global config: {0}", Log.Describe(e)));

			return false;
		}
	}

	/// <summary>Every bot config in the config dir, keyed by bot name (the file name without .json).</summary>
	/// <summary>
	/// Carries a pre-split config across: folds the retired MainGameSharePct into the games list.
	///
	/// The main game's share used to live in its own box and the number written beside the game in GameWeights
	/// was ignored, so plenty of configs say "730, 440, 550" with no percentages at all, or carry percentages
	/// that never did anything. Read straight into the new scheme those become an even three-way split - an
	/// account set to 85% on one game would quietly drop to 33% and start playing things it rarely touched.
	/// Folding puts the old share where the scheduler now looks and rescales the side games around it, so the
	/// day the account wakes up to is the day it would have had.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	/// <summary>
	/// Carries a config across the retirement of the per-game achievement ceiling.
	///
	/// 0 used to mean "let every game roll its own ceiling out of a hardcoded range". There are no per-game
	/// ceilings any more, so 0 no longer means anything - and since the setting's floor is now 1, a stored 0
	/// would clamp to a 1% ceiling and stop almost every game dead after a single achievement. Anything that
	/// still says 0 is taking the old default meaning, so it takes the new default figure.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateAchievementCeiling(BotConfig cfg, string name) {
		if (cfg.AchievementMaxCompletionPct > 0) {
			return false;
		}

		cfg.AchievementMaxCompletionPct = 90;
		Log.Info("achievement ceiling is one figure now: 90%", name);

		return true;
	}

	/// <summary>
	/// Carries "only farm cards while asleep" over to the three-way "when to farm cards". Everyone else takes the new
	/// default - the day's sittings - which is the point of the change: nights belong to the overnight games.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateFarmWhen(BotConfig cfg, string name) {
		if (!cfg.FarmOnlyWhileAsleep) {
			return false;
		}

		cfg.FarmOnlyWhileAsleep = false;
		cfg.FarmCardsWhen = Modules.FarmWhen.Night;
		Log.Info("setting moved: when to farm cards = only at night", name);

		return true;
	}

	/// <summary>
	/// Carries "Update by itself" and "When I say update" over to the one choice, so nothing installs any differently:
	/// install at night stays install by itself at night (and keeps what a click did), tell me with "when my accounts are
	/// asleep" is once everyone's asleep, and tell me with "right away" is install when I click - the default, so nothing to do.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateUpdates(GlobalConfig g) {
		// Written by 1.6.5 or later: UpdateMode is what counts, and an AutoUpdate beside it is only the copy 1.6.5 to 1.6.9
		// wrote for the version before. Read as the old choice again, it would undo the one made since.
		if (g.UpdateModeSet) {
			g.AutoUpdate = 0;

			return false;
		}

		if (g.AutoUpdate == 1) {
			int clickWaited = g.UpdateWhenAsked;
			g.AutoUpdate = 0;
			g.UpdateMode = Core.UpdateModes.AtNight;
			g.UpdateWhenAsked = clickWaited;

			return true;
		}

		// Only a file from before: since then UpdateWhenAsked is only ever 1 next to "install by itself at night".
		if ((g.UpdateWhenAsked == 1) && (g.UpdateMode != Core.UpdateModes.AtNight)) {
			g.UpdateMode = Core.UpdateModes.JustTellMe;

			return true;
		}

		return false;
	}

	/// <summary>
	/// The Discord card's "Account names" second line is gone. A file still holding DiscordShowNames gets it taken out: on
	/// (the old default) takes the new default, Steam accounts on nocat.farm; off had picked one of the counts, which it keeps.
	/// A file without the key is left alone, so a count picked since is never touched.
	/// </summary>
	/// <returns>true when the config changed and should be written back (without the old key).</returns>
	public static bool MigrateDiscordNames(GlobalConfig g, string json) {
		bool? names = null;
		bool lineInFile = false;

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return false;
			}

			foreach (JsonProperty p in doc.RootElement.EnumerateObject()) {
				if (p.Name.Equals("DiscordShowNames", StringComparison.OrdinalIgnoreCase)) {
					names = p.Value.ValueKind switch {
						JsonValueKind.True => true,
						JsonValueKind.False => false,
						_ => names
					};
				} else if (p.Name.Equals(nameof(GlobalConfig.DiscordSecondLine), StringComparison.OrdinalIgnoreCase)) {
					lineInFile = true;
				}
			}
		} catch (JsonException) {
			return false;
		}

		if (names == null) {
			return false;
		}

		// Off with no second line written: it showed the old default, hours past week.
		g.DiscordSecondLine = names.Value ? Core.DiscordPresence.LineAccounts : lineInFile ? g.DiscordSecondLine : 1;

		return true;
	}

	/// <summary>
	/// "Share my hours and cards totals" (1.8.0) starts out as "Count me as a user" was. A file from before it has no
	/// ShareFarmTotals, and the default (on) would have someone who had counting off start sending totals the day they
	/// turn counting on just to be counted - something they never agreed to. A file that has the key is left alone.
	/// </summary>
	/// <returns>true when the key was missing, so the file gets it written.</returns>
	public static bool MigrateShareTotals(GlobalConfig g, string json) {
		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return false;
			}

			foreach (JsonProperty p in doc.RootElement.EnumerateObject()) {
				if (p.Name.Equals(nameof(GlobalConfig.ShareFarmTotals), StringComparison.OrdinalIgnoreCase)) {
					return false;
				}
			}
		} catch (JsonException) {
			return false;
		}

		g.ShareFarmTotals = g.CountMeAsUser;

		return true;
	}

	/// <summary>Carries the booster games typed as text ("730, 440") over to the game list.</summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateBoosterGames(BotConfig cfg) {
		if (cfg.BoosterGames == null) {
			return false;
		}

		// Written by 1.6.5 or later: the list is what counts, a text beside it only the copy 1.6.5 to 1.6.9 wrote for the
		// version before. Read in again, a game taken off the list here would come back from the copy.
		if (cfg.BoosterPackGamesSet) {
			cfg.BoosterGames = null;

			return false;
		}

		foreach (string token in cfg.BoosterGames.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)) {
			if (uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app != 0) && !cfg.BoosterPackGames.Contains(app)) {
				cfg.BoosterPackGames.Add(app);
			}
		}

		cfg.BoosterGames = null;

		return true;
	}

	/// <summary>
	/// 1.8.1: "Make booster packs" became one choice. A file without it made packs for its list, so a list in it is "games I
	/// pick" with that list, and an empty one is off. The list stays as it is either way.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateBoosterMake(BotConfig cfg) {
		if (cfg.BoosterMakeSet) {
			return false;
		}

		cfg.BoosterMake = cfg.BoosterPackGames.Any(static id => id != 0) ? 1 : 0;

		return true;
	}

	/// <summary>
	/// Carries the three free-game switches over to the one choice: "Claim free games" on is games, with "...free DLC too"
	/// on as well it's games and DLC. The third switch ("...and its game, if that's free too") is part of games and DLC
	/// now. "...free DLC too" on its own did nothing (it needed the first switch), so that stays off.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateFreeGames(BotConfig cfg, string name = "") {
		bool dlcBaseOff = cfg.ClaimFreeDlcBase == false;
		cfg.ClaimFreeDlcBase = null;

		// Written by 1.6.5 or later: ClaimFree is what counts, and switches beside it are only the copy 1.6.5 to 1.6.9 wrote
		// for the version before - read in again they'd say the same, or (edited by hand) undo a choice made here.
		if (cfg.ClaimFreeSet || (!cfg.ClaimFreeGames && !cfg.ClaimFreeDlc)) {
			cfg.ClaimFreeGames = false;
			cfg.ClaimFreeDlc = false;

			return false;
		}

		if (cfg.ClaimFreeGames) {
			cfg.ClaimFree = cfg.ClaimFreeDlc ? Modules.FreeClaims.GamesAndDlc : Modules.FreeClaims.Games;

			// "...and its game, if that's free too" is part of games and DLC now. Somebody who had it off gets it on - said
			// once, here, so a free game turning up in the library isn't a surprise.
			if (cfg.ClaimFreeDlc && dlcBaseOff) {
				Log.Info(new Said("{0}: Claim free games now also claims a free DLC's game when that's free too - that was its own switch, and it was off.", name), name);
			}
		}

		cfg.ClaimFreeGames = false;
		cfg.ClaimFreeDlc = false;

		return true;
	}

	/// <summary>
	/// 1.8.0: "Sell duplicate cards" became part of Spare items, and the "Open booster packs" switch a choice. Sell duplicate
	/// cards on doesn't carry over: Spare items stays off, and the log says once where to turn it on - its switch shows what
	/// it would do first, so nothing starts converting by itself. Open booster packs on is "always", as it was; off stays off on a human-mode account (never
	/// started silently there) and becomes "only when worth more" on the others.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateSpare(BotConfig cfg, string name = "") {
		bool changed = false;

		// The old names stay in the file as mirrors of the new ones (for a rollback to 1.7.x), so they only count from a file
		// that doesn't have the new ones yet - then the new ones are set, and the mirrors follow them from there.
		// Never "on but waiting for a command": Spare items stays off, and says once where to turn it on - the switch shows
		// what it would do first (Switches).
		if (!cfg.SpareItemsSet && cfg.OldSellDuplicates) {
			cfg.SpareItems = false;
			Log.Attention(new Said("{0}: Spare items replaced Sell duplicate cards - turn it on in Settings to see what it would do", name), name);
			changed = true;
		}

		if (!cfg.OpenBoosterPacksSet) {
			// On in 1.7.x is "always", as it was; off or missing is never - robots too.
			cfg.OpenBoosterPacks = cfg.OldUnpackBoosterPacks ? 2 : 0;
			changed = true;
		}

		return changed;
	}

	/// <summary>
	/// A selling price that isn't one of the choices (a hand edit, a newer version's) becomes "a cent under the real price",
	/// the safe default, said once. 3 is a choice again since 1.8.2: "a cent under the cheapest", which no longer follows a
	/// dumped listing down - 1.8.1 had turned every saved 3 into 0 already.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateSellPrice(BotConfig cfg, string name = "") {
		if (cfg.SpareSellPrice is >= 0 and <= 3) {
			return false;
		}

		cfg.SpareSellPrice = 0;

		if (name.Length > 0) {
			Log.Info(new Said("{0}: the selling price setting wasn't one of its choices - it now lists a cent under the real price", name), name);
		}

		return true;
	}

	/// <summary>
	/// Spare items or Steam Points left on without their go-ahead (an earlier 1.8.0 build, a hand edit): shown off, said once.
	/// Each is turned on through its preview only (<see cref="Core.Switches"/>).
	/// </summary>
	internal static bool SettleSwitches(BotConfig cfg, string name) {
		if (name.Length == 0) {
			return false;
		}

		bool spare = cfg.SpareItems, points = cfg.SpendPoints, buy = cfg.BuyCards;

		if (!Core.Switches.Settle(cfg, name)) {
			return false;
		}

		if (spare && !cfg.SpareItems) {
			Log.Attention(new Said("{0}: Spare items is off until you turn it on in Settings - it shows what it would do first", name), name);
		}

		if (points && !cfg.SpendPoints) {
			Log.Attention(new Said("{0}: spending Steam Points is off until you turn it on in Settings - it shows what it would buy first", name), name);
		}

		if (buy && !cfg.BuyCards) {
			Log.Attention(new Said("{0}: buying cards is off until you turn it on in Settings - it shows what it would buy first", name), name);
		}

		return true;
	}

	/// <summary>Where it's noted that an account's overnight setting has been looked at by this version - see ReadBot.</summary>
	internal static string OvernightSeen(string name) => Path.Combine(ConfigDir, "state", $"overnight-{name}.seen");

	/// <summary>
	/// Before the top-games number, "Bank hours overnight" on with nothing on the list banked nothing at all. Now an empty
	/// list idles the account's most-played games - which the owner wants for people who turn it on, not for every
	/// account that had the switch on (it's on by default) with an empty list. So a file from before - the switch in it,
	/// on, the top-games number not - with an empty list has the switch turned off: nothing changes overnight. Once: the
	/// number is in the file after the save, and a file without the switch at all (a new one) keeps the new default.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateOvernight(BotConfig cfg, string name, bool seenBefore = false) {
		if (seenBefore || cfg.OfflineIdleTopGamesSet || !cfg.OfflineIdleAtNightSet || !cfg.OfflineIdleAtNight || (cfg.OfflineIdleGames.Count > 0)) {
			return false;
		}

		cfg.OfflineIdleAtNight = false;
		Log.Info(new Said("{0}: Bank hours overnight was on with no games listed, which banked nothing - switched off so nothing changes. Turn it on to idle its most-played games overnight.", name), name);

		return true;
	}

	public static bool MigrateGameShares(BotConfig cfg, string name) {
		if (cfg.MainGameSharePct <= 0) {
			return false;   // already migrated, or written by a version that never had it
		}

		int main = Math.Clamp(cfg.MainGameSharePct, 5, 95);

		cfg.MainGameSharePct = 0;

		List<(uint Game, int Weight)> games = Modules.HumanMode.ParseWeights(cfg.GameWeights);

		if (games.Count == 0) {
			return true;   // nothing to fold it into, but the retired key still goes
		}

		if (games.Count == 1) {
			cfg.GameWeights = $"{games[0].Game}:100";

			return true;
		}

		int sideTotal = games.Skip(1).Sum(static g => Math.Max(1, g.Weight));
		int pool = 100 - main;
		List<string> parts = [$"{games[0].Game}:{main}"];
		int spent = 0;

		for (int i = 1; i < games.Count; i++) {
			// The last side game takes the remainder so the list lands on exactly 100 rather than 99 or 101.
			int share = i == games.Count - 1
				? Math.Max(1, pool - spent)
				: Math.Max(1, pool * Math.Max(1, games[i].Weight) / sideTotal);

			spent += share;
			parts.Add($"{games[i].Game}:{share}");
		}

		cfg.GameWeights = string.Join(", ", parts);
		Log.Info(new Said("game shares moved into the games list - now \"{0}\"", cfg.GameWeights), name);

		return true;
	}

	/// <summary>
	/// The accounts with a file in the config folder, by name - without reading any of them. For a reload, which reads each
	/// one under its account's lock (<see cref="LoadBot"/>): a read outside it held the file open while the account saved,
	/// Windows refused that save, and the reload then put the older file back.
	/// </summary>
	public static List<string> BotNames() {
		Directory.CreateDirectory(ConfigDir);

		return [.. Directory.GetFiles(ConfigDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
			.Where(static name => !string.Equals(name, "nocatFarm", StringComparison.OrdinalIgnoreCase) && (name.Length > 0) && (name[0] != '.'))];
	}

	public static Dictionary<string, BotConfig> LoadBots() {
		Dictionary<string, BotConfig> bots = new(StringComparer.OrdinalIgnoreCase);
		Directory.CreateDirectory(ConfigDir);

		foreach (string file in Directory.GetFiles(ConfigDir, "*.json")) {
			string name = Path.GetFileNameWithoutExtension(file);

			if (string.Equals(name, "nocatFarm", StringComparison.OrdinalIgnoreCase) || (name.Length == 0) || (name[0] == '.')) {
				continue;
			}

			if (ReadBot(file, name) is { } cfg) {
				bots[name] = cfg;
			}
		}

		return bots;
	}

	/// <summary>
	/// One account's file as it is on disk now, or null when it isn't there or doesn't load. For reading one account again
	/// under its lock (<see cref="Core.Bot.CfgGate"/>) - a copy read for every account at once and put in later was already
	/// older than a save made in between.
	/// </summary>
	public static BotConfig? LoadBot(string name) {
		string file = Path.Combine(ConfigDir, name + ".json");

		return (name.Length > 0) && (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) && File.Exists(file) ? ReadBot(file, name) : null;
	}

	private static BotConfig? ReadBot(string file, string name) {
		// Looked for before the file is read: a load at the same moment that reads the file first and then finds another's
		// mark would take a file the migration hasn't touched yet as one it has.
		string seen = OvernightSeen(name);
		bool seenBefore = File.Exists(seen);

		try {
			string text = ReadShared(file);
			BotConfig? cfg = JsonSerializer.Deserialize<BotConfig>(text, Json);

			if (cfg == null) {
				return null;
			}

			FillNulls(cfg);

			if (string.IsNullOrWhiteSpace(cfg.SteamLogin)) {
				cfg.SteamLogin = name;   // default the login to the file name
			}

			// Decrypt whatever was sealed. Plain text passes straight through, so a hand-edited file and a
			// config written by an older version both still work - and it's written back encrypted below.
			bool plain = new[] { cfg.SteamPassword, cfg.SharedSecret, cfg.IdentitySecret, cfg.AccountProxyPassword, cfg.SteamParentalCode }
				.Any(Secrets.IsPlain);

			cfg.SteamPassword = Secrets.Unprotect(cfg.SteamPassword);
			cfg.SharedSecret = Secrets.Unprotect(cfg.SharedSecret);
			cfg.IdentitySecret = Secrets.Unprotect(cfg.IdentitySecret);
			cfg.AccountProxyPassword = Secrets.Unprotect(cfg.AccountProxyPassword);
			cfg.SteamParentalCode = Secrets.Unprotect(cfg.SteamParentalCode);

			// Every migration runs, then one write - a config can need any of them, and none is worth two saves.
			bool migrated = plain && Secrets.Available;
			migrated |= MigrateGameShares(cfg, name);
			migrated |= MigrateAchievementCeiling(cfg, name);
			migrated |= MigrateFarmWhen(cfg, name);
			migrated |= MigrateFreeGames(cfg, name);
			migrated |= MigrateBoosterGames(cfg);
			migrated |= MigrateBoosterMake(cfg);
			migrated |= MigrateSpare(cfg, name);
			migrated |= MigrateSellPrice(cfg, name);
			// 1.8.2: settings that became one, and the defaults that changed (an account from before keeps its old ones).
			migrated |= MigrateWatchBans(cfg, name);
			migrated |= MigrateOwnAccounts(cfg, name);
			migrated |= MigrateSpareKeep(cfg);
			migrated |= FixSpendLimits(cfg, name);
			migrated |= MigrateNewDefaults(cfg, text);
			migrated |= SettleSwitches(cfg, name);
			// Marked as seen in config/state, where the version before can't take it away: going back to it by hand and
			// saving the account drops OfflineIdleTopGames from the file, and the next update read that as a file from
			// before - switching off overnight banking somebody had turned on since.
			bool night = MigrateOvernight(cfg, name, seenBefore);
			migrated |= night;
			// Not saved back once the file is gone: the account was removed while this read it, and the save brought it back.
			// Looked at under the save lock, which a remove takes too: looked at before it, a remove in between had its file
			// written back - every read saves while an update is on trial with a plain PIN (see Sealed), so not that rare.
			bool saved = !migrated || SaveBot(name, cfg, onlyIfThere: true);

			// Only once it's on disk: marked before a save that failed, the next start skipped the migration and banked
			// the most-played games all night on a file that still said nothing was chosen.
			if (!seenBefore && (!night || saved)) {
				try {
					Directory.CreateDirectory(Path.GetDirectoryName(seen)!);
					File.WriteAllText(seen, "");

					// And taken back if the account was removed while this read its file: written after the remove deleted the
					// account's files, the mark stayed - and an account added again by that name skipped the migration. The file
					// is deleted before the rest (see BotManager.RemoveAsync), so one still there now means the mark goes with them.
					if (!File.Exists(file)) {
						File.Delete(seen);
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed("noting that an account's overnight setting was looked at", e, name);
				}
			}

			return cfg;
		} catch (Exception e) {
			Log.Warn(new Said("config: {0} is not valid JSON ({1}) - skipped", Path.GetFileName(file), Log.Scrub(e.Message)));

			// Not always bad JSON - a file it may not read lands here too, and the type says which.
			Log.Failed($"config: loading {file}", e, name);

			return null;
		}
	}

	/// <returns>False when nothing reached the disk.</returns>
	public static bool SaveBot(string name, BotConfig cfg) => SaveBot(name, cfg, onlyIfThere: false);

	/// <param name="onlyIfThere">Only over a file that's still there, looked at under the lock - for a read saving what it
	/// read: a removed account isn't written back.</param>
	private static bool SaveBot(string name, BotConfig cfg, bool onlyIfThere) {
		try {
			Directory.CreateDirectory(ConfigDir);

			// The "Sell duplicate cards" mirror for a rollback: on only when this version would sell here too - which needs the
			// account's go-ahead, kept with its spare items (Core.SpareLedger), not in the settings.
			bool sellMirror = BotConfig.SellsAfterRollback(cfg, Core.SpareLedger.Load(name).Approved);

			lock (SaveGate) {
				if (HeldByRestore) {
					Log.Debug($"config: not saving {name}.json - a backup is being restored over it", name);

					return false;
				}

				if (onlyIfThere && !File.Exists(Path.Combine(ConfigDir, name + ".json"))) {
					return false;
				}

				// The secrets go to disk encrypted, but the config in memory stays readable - so a COPY is written
				// rather than the live object. Encrypting in place would leave every other part of the program
				// holding ciphertext where it expects a password.
				//
				// Copied inside the lock, like SaveGlobal. Copied before it, a module's save and a dashboard save that
				// ran together could write in the opposite order to the one they copied in - older settings last.
				BotConfig onDisk = Secrets.Available ? Sealed(cfg, name) : JsonSerializer.Deserialize<BotConfig>(JsonSerializer.Serialize(cfg, Json), Json)!;
				onDisk.SellMirror = sellMirror;
				AtomicFile.Write(Path.Combine(ConfigDir, name + ".json"), JsonSerializer.Serialize(onDisk, Json));
			}

			return true;
		} catch (Exception e) {
			Log.Warn(new Said("config: couldn't save {0}: {1}", name, Log.Describe(e)), name);

			return false;
		}
	}

	/// <summary>
	/// A copy with the credentials encrypted.
	///
	/// A Steam password and an authenticator's secrets are the account. Leaving them as plain text in a JSON file
	/// meant anything that could read the folder - a sync client, a backup, somebody looking over a shoulder -
	/// had the account. Hand-edited plain text still works: reading accepts either, and the next save seals it.
	/// </summary>
	private static BotConfig Sealed(BotConfig cfg, string name) {
		BotConfig copy = JsonSerializer.Deserialize<BotConfig>(JsonSerializer.Serialize(cfg, Json), Json)!;

		copy.SteamPassword = Secrets.Protect(cfg.SteamPassword, cfg.SteamLogin);
		copy.SharedSecret = Secrets.Protect(cfg.SharedSecret, cfg.SteamLogin);
		copy.IdentitySecret = Secrets.Protect(cfg.IdentitySecret, cfg.SteamLogin);
		copy.AccountProxyPassword = Secrets.Protect(cfg.AccountProxyPassword, cfg.SteamLogin);

		// The Family View PIN is a secret setting too - it was the one still written out in the clear.
		// Left plain while an update is on trial if the file has it plain - the version before reads it plain (see
		// SelfUpdate.OnTrial). One that was encrypted stays so: written plain, it sat in the clear in every backup and settings
		// copy made until the trial was over.
		copy.SteamParentalCode = Core.SelfUpdate.OnTrial && PinPlainOnDisk(name) ? cfg.SteamParentalCode : Secrets.Protect(cfg.SteamParentalCode, cfg.SteamLogin);

		return copy;
	}

	/// <summary>
	/// The Family View PIN in <paramref name="name"/>'s file is plain text now - or there's none yet, no file or no PIN in it.
	/// Written encrypted then, a PIN set during the trial was one the version before couldn't read if the trial failed.
	/// </summary>
	private static bool PinPlainOnDisk(string name) {
		try {
			string file = Path.Combine(ConfigDir, name + ".json");

			if (!File.Exists(file)) {
				return true;
			}

			string? pin = JsonSerializer.Deserialize<BotConfig>(ReadShared(file), Json)?.SteamParentalCode;

			return string.IsNullOrEmpty(pin) || Secrets.IsPlain(pin);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return false;   // encrypted, then - never plain on a guess
		}
	}

	/// <summary>
	/// An account's file as text, read so that it can be deleted or saved over while it's open. Read with File.ReadAllText,
	/// a reload that happened to be reading it made a 'remove' fail ("being used by another process"): the account went
	/// from the list, its file stayed, and it was back at the next start.
	/// </summary>
	internal static string ReadShared(string file) {
		using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader reader = new(fs);

		return reader.ReadToEnd();
	}

	/// <summary>
	/// Deletes <paramref name="path"/>, trying again a few times while something else has it open - a virus scanner or a
	/// sync client looking at a file that was just written lets go in a moment. Throws the last error if it never does.
	/// </summary>
	internal static void DeleteFile(string path) {
		for (int wait = 50; ; wait *= 2) {
			try {
				File.Delete(path);

				return;
			} catch (Exception e) when (e is (IOException or UnauthorizedAccessException) and not (FileNotFoundException or DirectoryNotFoundException) && (wait <= 400)) {
				Thread.Sleep(wait);
			}
		}
	}

	public static bool DeleteBot(string name) {
		try {
			string p = Path.Combine(ConfigDir, name + ".json");

			// Under the save lock, so a read saving the file back (see ReadBot) is either done before it goes or finds it gone.
			lock (SaveGate) {
				if (!File.Exists(p)) {
					return false;
				}

				DeleteFile(p);
			}

			return true;
		} catch (Exception e) {
			Log.Failed($"config: deleting {name}.json", e, name);

			return false;
		}
	}

	/// <summary>
	/// The files an account keeps besides its config, as (folder under config/, start, end): the account's name goes
	/// between the two. Every per-account file the app writes - a new one goes in here too (a test checks).
	/// </summary>
	/// <remarks>Not its authenticator (authenticators/*.maFile): that is the only copy of a Steam Guard secret.</remarks>
	public static readonly (string Dir, string Start, string End)[] AccountFiles = [
		("tokens", "", ".token"), ("tokens", "", ".access"),
		("state", "bans-", ".json"), ("state", "cardcheck-", ".json"), ("state", "cardpace-", ".json"), ("state", "cheevo-", ".json"),
		("state", "dropsfirst-", ".json"), ("state", "freegames-", ".json"), ("state", "freeitems-", ".json"), ("state", "grind-", ".json"),
		("state", "human-", ".json"), ("state", "hunt-", ".json"), ("state", "invvalue-", ".json"), ("state", "owner-", ".json"),
		("state", "ownersitting-", ".json"), ("state", "queue-", ".txt"), ("state", "rep4rep-", ".json"), ("state", "rotation-", ".json"), ("state", "send-", ".json"),
		("state", "trades-announced-", ".json"), ("state", "overnight-", ".seen"),
		("state", "scamwatch-", ".json"), ("state", "health-", ".json"),   // scam watch, account health
		("state", "points-", ".json"),   // Steam Points spent (Modules/PointsSpender.cs)
		("state", "spare-", ".json"),
		("state", "buy-", ".json"),   // money spent buying cards, and its go-ahead (Modules/CardBuyer.cs)
		("state", "listed-", ".json"),   // listings nocat.farm put up itself (Core/OurListings.cs)
		("state", "boosters-", ".json"),   // booster packs automatic made and still has to open (Modules/BoosterPick.cs)
		("state", "steamopen-", ".json"),   // 1.8.2: the Open in Steam button's go-ahead (Core/SteamOpenLedger.cs)
		("state", "cardswaps-", ".json"),   // 1.8.2 card swaps: its go-ahead, today's count, its matches (Core/CardExchange.cs)
		("state", "gems-", ".json"),   // 1.8.2 "Use gems for the most levels": what it still has to list (Modules/GemsChoice.cs)
		("state", "markethold-", ".json"),   // 1.8.2 the market hold (Core/MarketHold.cs)
		("state", "refundprices-", ".json")   // 1.8.2 what its refundable games cost (Core/RefundGuard.Price.cs)
	];

	/// <summary>
	/// Is <paramref name="file"/> (a file name, no folder) one of the account's own, in any case: "human-Main.json" is
	/// main's. Only exactly its name - "human-main2.json" and "human-main-alt.json" are other accounts'.
	/// </summary>
	public static bool IsAccountFile(string file, string name, string start, string end) =>
		(file.Length == start.Length + name.Length + end.Length)
		&& file.StartsWith(start, StringComparison.OrdinalIgnoreCase) && file.EndsWith(end, StringComparison.OrdinalIgnoreCase)
		&& file.AsSpan(start.Length, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Deletes a removed account's own files - its login tokens and its state - in any case. Left behind, they came back
	/// with the account if it was added again; and off Windows, added again as "Main" after "main", its state was there
	/// twice ("human-main.json" and "human-Main.json"), and every backup was refused as "in the zip twice". Returns how
	/// many went. Call after the account is stopped, so nothing writes them again.
	/// </summary>
	public static int DeleteAccountFiles(string name) {
		int deleted = 0;

		if (!IsValidBotName(name)) {
			return 0;
		}

		foreach (IGrouping<string, (string Dir, string Start, string End)> dir in AccountFiles.GroupBy(static f => f.Dir)) {
			string full = Path.Combine(ConfigDir, dir.Key);

			try {
				if (!Directory.Exists(full)) {
					continue;
				}

				foreach (string path in Directory.EnumerateFiles(full)) {
					string file = Path.GetFileName(path);

					if (!dir.Any(f => IsAccountFile(file, name, f.Start, f.End))) {
						continue;
					}

					try {
						DeleteFile(path);
						deleted++;
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						Log.Failed($"config: deleting {dir.Key}/{file}", e, name);
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed($"config: looking through {dir.Key}/", e, name);
			}
		}

		return deleted;
	}
}
