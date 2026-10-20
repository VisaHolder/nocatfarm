using System.Text.Json;
using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>
/// The 1.8.2 settings clean-up: settings that became one, carried over from an old file so nothing behaves differently.
/// Each runs as the file is read; the old key is read once and then dropped (written only while it still means something).
/// </summary>
public static partial class ConfigStore {
	/// <summary>
	/// Scam watch and account health had a Telegram, a Discord and a pop-up switch each; now each is one chip among the
	/// notifications, and one pop-up kind. Sent anywhere before (Telegram or Discord on) is the chip on; the pop-up switch is
	/// the pop-up kind. A channel turned off on its own - the other one on - stays off for that channel until the chip is
	/// changed (it keeps its old key until then); both off is the chip off.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateAlertRouting(GlobalConfig g) {
		bool changed = false;

		if ((g.ScamAlertTelegram != null) || (g.ScamAlertDiscord != null) || (g.ScamAlertPopup != null)) {
			(bool send, bool? tg, bool? dc) = Routing(g.ScamAlertTelegram, g.ScamAlertDiscord);
			g.SendScamWarnings = send;
			(g.ScamAlertTelegram, g.ScamAlertDiscord) = (tg, dc);

			if (g.ScamAlertPopup is { } popup) {
				g.NotifyScam = popup;
				g.ScamAlertPopup = null;
			}

			changed = true;
		}

		if ((g.HealthAlertTelegram != null) || (g.HealthAlertDiscord != null) || (g.HealthAlertPopup != null)) {
			(bool send, bool? tg, bool? dc) = Routing(g.HealthAlertTelegram, g.HealthAlertDiscord);
			g.SendHealthAlerts = send;
			(g.HealthAlertTelegram, g.HealthAlertDiscord) = (tg, dc);

			if (g.HealthAlertPopup is { } popup) {
				g.NotifyHealth = popup;
				g.HealthAlertPopup = null;
			}

			changed = true;
		}

		return changed;
	}

	/// <summary>The chip, and what's left of the two channel switches: only a lone "off" next to an "on".</summary>
	private static (bool Send, bool? Telegram, bool? Discord) Routing(bool? telegram, bool? discord) {
		bool tg = telegram ?? true, dc = discord ?? true;

		return (tg || dc) switch {
			false => (false, null, null),
			true when tg && dc => (true, null, null),
			_ => (true, tg ? null : false, dc ? null : false)
		};
	}

	/// <summary>
	/// The scam or health chip was changed: a channel left off on its own from before goes back to following it.
	/// </summary>
	public static void FollowAlertChips(GlobalConfig g, IReadOnlyCollection<string> changed) {
		if (changed.Contains(nameof(GlobalConfig.SendScamWarnings))) {
			(g.ScamAlertTelegram, g.ScamAlertDiscord) = (null, null);
		}

		if (changed.Contains(nameof(GlobalConfig.SendHealthAlerts))) {
			(g.HealthAlertTelegram, g.HealthAlertDiscord) = (null, null);
		}
	}

	/// <summary>"Send backups" off is no messages; on, "Tell me" decides: only when one fails, or after every backup too.</summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateBackupMessages(GlobalConfig g) {
		if ((g.SendBackups == null) && (g.BackupNotify == null)) {
			return false;
		}

		g.BackupMessages = g.SendBackups == false ? 0 : (g.BackupNotify == 1 ? 2 : 1);
		g.SendBackups = null;
		g.BackupNotify = null;

		return true;
	}

	/// <summary>The daily summary's hour and minute become one time of day: 9 and 30 is 09:30. One left out of the file is
	/// what it was by default then - 9, 30.</summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateDailyReportTime(GlobalConfig g) {
		if ((g.DailyReportHour == null) && (g.DailyReportMinute == null)) {
			return false;
		}

		g.DailyReportTime = (Math.Clamp(g.DailyReportHour ?? 9, 0, 23) * 60) + Math.Clamp(g.DailyReportMinute ?? 30, 0, 59);
		g.DailyReportHour = null;
		g.DailyReportMinute = null;

		return true;
	}

	/// <summary>"Notify if an update is available" off is the fourth update choice, "don't look for updates".</summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateCheckForUpdates(GlobalConfig g) {
		if (g.CheckForUpdates == null) {
			return false;
		}

		if (g.CheckForUpdates == false) {
			g.UpdateMode = UpdateModes.Off;
		}

		g.CheckForUpdates = null;

		return true;
	}

	/// <summary>
	/// "Watch for bans" off turns account health's ban alert off - the one ban alert now. The ban look itself always runs:
	/// it only reads, and keeps banned games out of trades.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateWatchBans(BotConfig cfg, string name = "") {
		if (cfg.WatchBans == null) {
			return false;
		}

		if (cfg.WatchBans == false) {
			cfg.HealthAlertBans = false;
		}

		// Ban alerts are account health's now: with health off, there are none - said once, so it's no surprise.
		if ((cfg.WatchBans == true) && !cfg.WatchHealth && (name.Length > 0)) {
			Log.Info(new Said("{0}: alerts about bans are part of account health now, which is off on this account - turn it on to get them", name), name);
		}

		cfg.WatchBans = null;

		return true;
	}

	/// <summary>
	/// "Accept anything from your own accounts" with "Your own accounts" listed was the same as those accounts in "Your other
	/// accounts" both ways - so that's where they go when that list is empty. Off, nothing goes anywhere: an empty list is
	/// nobody, as it was. A list already written wins, as it did.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateOwnAccounts(BotConfig cfg, string name = "") {
		if (cfg.AcceptFromMasters == null) {
			return false;
		}

		if ((cfg.AcceptFromMasters == true) && (cfg.AutoTradeWith.Trim().Length == 0)) {
			HashSet<ulong> ids = Modules.Social.ParseIds(cfg.TradeMasters);

			if (ids.Count > 0) {
				cfg.AutoTradeWith = string.Join(", ", ids.Select(static id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

				if (name.Length > 0) {
					Log.Info(new Said("{0}: your own accounts are in \"Your other accounts\" now, both ways - as before", name), name);
				}
			}
		}

		cfg.AcceptFromMasters = null;

		return true;
	}

	/// <summary>
	/// 1.8.2: a spending limit below 0 means "no limit" only when it's exactly -1, as the dashboard writes it. Any other number
	/// below 0 (a typo in the file, -5, -0.01) became "no limit" too - spending whatever the wallet holds. Now it's 0: nothing
	/// spent until it's set; said once, as the file is fixed.
	/// </summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool FixSpendLimits(BotConfig cfg, string name) {
		bool changed = false;

		if ((cfg.BuyDailyLimit < 0) && (cfg.BuyDailyLimit != -1f)) {
			cfg.BuyDailyLimit = 0;
			changed = true;
		}

		if ((cfg.BuyWeeklyLimit < 0) && (cfg.BuyWeeklyLimit != -1f)) {
			cfg.BuyWeeklyLimit = 0;
			changed = true;
		}

		if (changed) {
			Log.Info(new Said("buying cards: a spending limit below 0 that wasn't -1 (no limit) is 0 now - nothing is bought until it's set"), name);
		}

		return changed;
	}

	/// <summary>One keep-count for every kind of spare item: the largest of the three, the one that keeps the most.</summary>
	/// <returns>true when the config changed and should be written back.</returns>
	public static bool MigrateSpareKeep(BotConfig cfg) {
		if ((cfg.SpareKeepBackgrounds == null) && (cfg.SpareKeepEmoticons == null)) {
			return false;
		}

		cfg.SpareKeepCopies = Math.Max(cfg.SpareKeepCopies, Math.Max(cfg.SpareKeepBackgrounds ?? 0, cfg.SpareKeepEmoticons ?? 0));
		cfg.SpareKeepBackgrounds = null;
		cfg.SpareKeepEmoticons = null;

		return true;
	}

	/// <summary>
	/// Settings whose default changed in 1.8.2, with the default they had before - a new account takes the new one, an
	/// account from before keeps what it ran on. Every file is written with all its settings, so a key is missing only from
	/// a file older than the setting or written by hand.
	/// </summary>
	internal static readonly (string Name, object Was)[] DefaultsBefore182 = [
		(nameof(BotConfig.SkipRefundableGames), false),
		(nameof(BotConfig.AutoReplyEnabled), true),
		(nameof(BotConfig.CustomGameNameEnabled), true)
	];

	/// <summary>An account file from before without one of those keys keeps the old default for it, rather than the new.</summary>
	/// <returns>true when the config changed and should be written back (with the key, from now on).</returns>
	public static bool MigrateNewDefaults(BotConfig cfg, string json) {
		HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return false;
			}

			foreach (JsonProperty p in doc.RootElement.EnumerateObject()) {
				keys.Add(p.Name);
			}
		} catch (JsonException) {
			return false;
		}

		bool changed = false;

		foreach ((string name, object was) in DefaultsBefore182) {
			if (!keys.Contains(name)) {
				typeof(BotConfig).GetProperty(name)!.SetValue(cfg, was);
				changed = true;
			}
		}

		return changed;
	}
}
