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
	// On by default. A brand new account does nothing at all until it is told what to play, and the dashboard
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

	// ── steam connection ──
	public int LoginStaggerSeconds { get; set; } = 12;
	public int ReconnectDelaySeconds { get; set; } = 10;
	public int ConnectionTimeoutSeconds { get; set; } = 90;
	public int MaxConcurrentFarming { get; set; }
	public int LoginCooldownMinutes { get; set; } = 25;
	public int WebRequestGapMs { get; set; } = 400;
	public int SteamProtocol { get; set; }
	public string WebProxy { get; set; } = "";
	public string WebProxyUsername { get; set; } = "";
	public string WebProxyPassword { get; set; } = "";

	/// <summary>AppIDs no account ever touches, on top of each account's own list.</summary>
	public List<uint> GlobalBlacklistedGames { get; set; } = [];

	public bool ExitWhenAllFinished { get; set; }

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

	public int StatusEveryMinutes { get; set; } = 5;
	public int StatusQuietEveryMinutes { get; set; } = 30;

	// ── daily report ──
	// A once-a-day plain-language summary of what the whole fleet banked in the last 24h - hours, cards,
	// comments, running total - written to the log/console at a chosen time (default 09:30). The per-minute
	// heartbeat says what an account is doing NOW; this answers "what did I get overnight?" in one place.
	public bool DailyReportEnabled { get; set; } = true;
	public int DailyReportHour { get; set; } = 9;
	public int DailyReportMinute { get; set; } = 30;

	// ── inventory value ──
	/// <summary>Steam's currency id for market prices. 1 USD, 2 GBP, 3 EUR, 20 CAD, 21 AUD - as the store uses.</summary>
	public int MarketCurrency { get; set; } = 1;

	/// <summary>Seconds between community-market price lookups. Higher is slower but stops Steam rate-limiting us.</summary>
	public int MarketGapSeconds { get; set; } = 10;

	/// <summary>How many hours a market price is trusted before it is looked up again.</summary>
	public int PriceCacheHours { get; set; } = 24;

	public bool CheckForUpdates { get; set; } = true;

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
	public int OnlineStatus { get; set; } = 1;

	/// <summary>
	/// Which kind of Steam client this pretends to be when it signs in.
	///
	/// Defaults to 7 (DesktopUI), which is what the real Steam client reports and what a working ArchiSteamFarm
	/// setup uses. The old default was Unknown (-1) - an unidentified session, which Steam then has to make its
	/// own mind up about. See the logon in Bot.cs for why that mattered.
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
	/// Whether to show the custom name at all.
	///
	/// Defaults TRUE so configs written before this existed behave identically - for them the text box WAS the
	/// switch, and an empty name still shows nothing either way.
	/// </summary>
	public bool CustomGameNameEnabled { get; set; } = true;

	public string CustomGameName { get; set; } = "";
	public bool PlayWhileFarming { get; set; } = true;
	public int GameDevice { get; set; }

	// ── trading cards ──
	public bool FarmCards { get; set; } = true;
	public float HoursUntilCardDrops { get; set; } = 3;
	public int FarmingOrder { get; set; }
	public List<uint> PriorityGames { get; set; } = [];
	public bool FarmPriorityOnly { get; set; }
	public List<uint> BlacklistedGames { get; set; } = [];
	public bool SkipUnplayedGames { get; set; }
	public bool SkipRefundableGames { get; set; }
	public int RefundHoldDays { get; set; } = 14;
	public bool FarmOnlyWhileAsleep { get; set; }
	public int FarmFromHour { get; set; }
	public int FarmUntilHour { get; set; }
	public int PostFarmWindDownMinMinutes { get; set; } = 5;
	public int PostFarmWindDownMaxMinutes { get; set; } = 12;
	public int LegitStopMaxSeconds { get; set; } = 30;
	public int FarmingDelayMinutes { get; set; } = 15;
	public int MaxFarmingHoursPerGame { get; set; } = 10;
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
	public int MaxBoostGamesInARow { get; set; } = 3;
	public int BoostRestMinutesHuman { get; set; } = 120;
	public bool IncludeFamilyLibrary { get; set; }
	public bool HoldNewFamilyGames { get; set; } = true;
	public bool YieldToFamily { get; set; } = true;
	public int BoostMinReviews { get; set; } = 200;
	public bool BoostOnlyPlayedGames { get; set; }
	public List<uint> AchievementGames { get; set; } = [];

	/// <summary>
	/// Games never to unlock in. Note that CS2 needs no entry here and never did: Valve keeps its achievements
	/// server-side, so the stats interface exposes a single settable one and no tool can touch the rest.
	/// </summary>
	public List<uint> AchievementNeverGames { get; set; } = [];

	/// <summary>Whether human mode's main game earns achievements like any other. On - it's where the hours are.</summary>
	public bool AchievementIncludeMainGame { get; set; } = true;

	/// <summary>0 careful, 1 normal, 2 brisk. Scales every gap the pacer waits.</summary>
	public int AchievementPace { get; set; } = 1;

	/// <summary>Hard cap on how much of any one game will ever be completed. 0 uses each game's own ceiling.</summary>
	public int AchievementMaxCompletionPct { get; set; } = 90;

	// ── inventory ──
	public bool ShowInventoryValue { get; set; } = true;
	public List<uint> InventoryIgnoreGames { get; set; } = [];

	// ── free games & badges ──
	public bool ClaimFreeGames { get; set; }
	public bool CraftBadges { get; set; }
	public bool UnpackBoosterPacks { get; set; }
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
	public bool Rep4RepRetryRefused { get; set; } = true;

	// ── human mode ──
	public bool LegitMode { get; set; }
	public string GameWeights { get; set; } = "";

	// how the day is shaped
	public int WeekdayHours { get; set; } = 6;
	public int WeekendHours { get; set; } = 9;
	public int DayOffChancePct { get; set; } = 5;
	public int DayStartHour { get; set; } = 13;
	public int BedHour { get; set; } = 2;
	public int LateNightExtraHours { get; set; } = 2;

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
	public bool OfflineIdleAtNight { get; set; } = true;
	public List<uint> OfflineIdleGames { get; set; } = [];

	/// <summary>Values overwritten when Legit mode was switched on, so switching it off restores them.</summary>
	public string LegitBackup { get; set; } = "";

	// ── friends & messages ──
	public bool AcceptFriendRequests { get; set; }
	public bool AcceptGroupInvites { get; set; }
	public bool JoinGroup { get; set; } = true;
	public bool IgnoreSuspiciousInvites { get; set; } = true;
	public bool RejectInvalidFriendInvites { get; set; }
	/// <summary>
	/// Whether to auto-reply at all.
	///
	/// Defaults TRUE so accounts configured before this setting existed behave exactly as they did - for them
	/// the text box was the switch, and it still reads as one because an empty message replies to nobody.
	/// </summary>
	public bool AutoReplyEnabled { get; set; } = true;

	public string AutoReply { get; set; } = "";
	public bool AutoReplyOncePerDay { get; set; } = true;
	public int AutoReplyDelaySeconds { get; set; } = 20;
	public int FriendRequestDelayMinMinutes { get; set; } = 1;
	public int FriendRequestDelayMaxMinutes { get; set; } = 45;
	public string CommandMasters { get; set; } = "";

	/// <summary>Hold every reaction - trades, replies, friend requests - until human mode says it is awake.</summary>
	public bool ActOnlyWhileAwake { get; set; } = true;

	// ── trades ──
	public bool AcceptDonations { get; set; } = true;
	public bool AcceptFromMasters { get; set; }
	public string TradeMasters { get; set; } = "";
	public bool DeclineOtherTrades { get; set; }
	public int TradeDelayMinMinutes { get; set; } = 2;
	public int TradeDelayMaxMinutes { get; set; } = 15;
	public string SendItemTypes { get; set; } = "cards";
	public string TradeMasterToken { get; set; } = "";
	public bool SendOnFarmingFinished { get; set; }

	// ── staying out of the way ──
	public bool PauseWhenYouPlay { get; set; } = true;
	public int ResumeDelayMinutes { get; set; } = 5;
}

/// <summary>
/// The global config as it stands right now. A handful of things deep in the engine (reconnect timing, the
/// farming concurrency cap) need it without having it threaded through five constructors; this is that, and
/// <see cref="Core.BotManager.ApplyGlobal"/> is the only thing that writes it.
/// </summary>
public static class Live {
	public static GlobalConfig Global { get; set; } = new();
}

public static class ConfigStore {
	public static string Root { get; private set; } = AppContext.BaseDirectory;
	public static string ConfigDir => Path.Combine(Root, "config");
	public static string GlobalPath => Path.Combine(ConfigDir, "nocatFarm.json");

	private static readonly JsonSerializerOptions Json = new() {
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never
	};

	public static void UseRoot(string root) {
		Root = root;
		Directory.CreateDirectory(ConfigDir);
	}

	/// <summary>A bot name has to be usable as a file name and must not walk out of the config directory.</summary>
	public static bool IsValidBotName(string name) =>
		!string.IsNullOrWhiteSpace(name)
		&& (name[0] != '.')
		&& !name.Equals("nocatFarm", StringComparison.OrdinalIgnoreCase)
		&& (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
		&& (Path.GetRelativePath(".", name) == name);

	public static GlobalConfig LoadGlobal() {
		Directory.CreateDirectory(ConfigDir);

		if (!File.Exists(GlobalPath)) {
			GlobalConfig fresh = new();
			SaveGlobal(fresh);

			return fresh;
		}

		try {
			return JsonSerializer.Deserialize<GlobalConfig>(File.ReadAllText(GlobalPath), Json) ?? new GlobalConfig();
		} catch (Exception e) {
			// Keep the broken file. Falling back to defaults and then saving over it would destroy the rep4rep
			// token and the dashboard password because of one stray comma.
			string kept = GlobalPath + ".broken";

			try {
				File.Copy(GlobalPath, kept, true);
			} catch {
				// best effort
			}

			Log.Error($"config: {Path.GetFileName(GlobalPath)} is not valid JSON ({e.Message})");
			Log.Warn($"a copy was kept as {Path.GetFileName(kept)}; running on defaults until you fix it");

			return new GlobalConfig();
		}
	}

	public static void SaveGlobal(GlobalConfig cfg) {
		try {
			Directory.CreateDirectory(ConfigDir);
			File.WriteAllText(GlobalPath, JsonSerializer.Serialize(cfg, Json));
		} catch (Exception e) {
			Log.Warn($"config: couldn't save global config: {e.Message}");
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
		Log.Info("achievement ceiling is one figure now, not one per game - set to 90%", name);

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
		Log.Info($"game shares moved into the games list - now \"{cfg.GameWeights}\"", name);

		return true;
	}

	public static Dictionary<string, BotConfig> LoadBots() {
		Dictionary<string, BotConfig> bots = new(StringComparer.OrdinalIgnoreCase);
		Directory.CreateDirectory(ConfigDir);

		foreach (string file in Directory.GetFiles(ConfigDir, "*.json")) {
			string name = Path.GetFileNameWithoutExtension(file);

			if (string.Equals(name, "nocatFarm", StringComparison.OrdinalIgnoreCase) || (name.Length == 0) || (name[0] == '.')) {
				continue;
			}

			try {
				BotConfig? cfg = JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(file), Json);

				if (cfg == null) {
					continue;
				}

				if (string.IsNullOrWhiteSpace(cfg.SteamLogin)) {
					cfg.SteamLogin = name;   // default the login to the file name
				}

				// Decrypt whatever was sealed. Plain text passes straight through, so a hand-edited file and a
				// config written by an older version both still work.
				cfg.SteamPassword = Secrets.Unprotect(cfg.SteamPassword);
				cfg.SharedSecret = Secrets.Unprotect(cfg.SharedSecret);
				cfg.IdentitySecret = Secrets.Unprotect(cfg.IdentitySecret);
				cfg.AccountProxyPassword = Secrets.Unprotect(cfg.AccountProxyPassword);

				// Both migrations run, then one write - a config can need either, and neither is worth two saves.
				bool migrated = MigrateGameShares(cfg, name);
				migrated |= MigrateAchievementCeiling(cfg, name);

				if (migrated) {
					SaveBot(name, cfg);
				}

				bots[name] = cfg;
			} catch (Exception e) {
				Log.Warn($"config: {Path.GetFileName(file)} is not valid JSON ({e.Message}) - skipped");
			}
		}

		return bots;
	}

	public static void SaveBot(string name, BotConfig cfg) {
		try {
			Directory.CreateDirectory(ConfigDir);

			// The secrets go to disk encrypted, but the config in memory stays readable - so a COPY is written
			// rather than the live object. Encrypting in place would leave every other part of the program
			// holding ciphertext where it expects a password.
			BotConfig onDisk = Secrets.Available ? Sealed(cfg) : cfg;

			File.WriteAllText(Path.Combine(ConfigDir, name + ".json"), JsonSerializer.Serialize(onDisk, Json));
		} catch (Exception e) {
			Log.Warn($"config: couldn't save {name}: {e.Message}");
		}
	}

	/// <summary>
	/// A copy with the credentials encrypted.
	///
	/// A Steam password and an authenticator's secrets are the account. Leaving them as plain text in a JSON file
	/// meant anything that could read the folder - a sync client, a backup, somebody looking over a shoulder -
	/// had the account. Hand-edited plain text still works: reading accepts either, and the next save seals it.
	/// </summary>
	private static BotConfig Sealed(BotConfig cfg) {
		BotConfig copy = JsonSerializer.Deserialize<BotConfig>(JsonSerializer.Serialize(cfg, Json), Json)!;

		copy.SteamPassword = Secrets.Protect(cfg.SteamPassword, cfg.SteamLogin);
		copy.SharedSecret = Secrets.Protect(cfg.SharedSecret, cfg.SteamLogin);
		copy.IdentitySecret = Secrets.Protect(cfg.IdentitySecret, cfg.SteamLogin);
		copy.AccountProxyPassword = Secrets.Protect(cfg.AccountProxyPassword, cfg.SteamLogin);

		return copy;
	}

	public static bool DeleteBot(string name) {
		try {
			string p = Path.Combine(ConfigDir, name + ".json");

			if (!File.Exists(p)) {
				return false;
			}

			File.Delete(p);

			return true;
		} catch {
			return false;
		}
	}
}
