namespace NocatFarm.Config;

/// <summary>
/// Settings that follow another one as it's changed (1.8.2) - from the dashboard and from 'set' alike, so the two can't
/// drift apart.
/// </summary>
public static partial class Settings {
	/// <summary>
	/// What goes with a change: "Clear Steam's notification counters" is one switch for Steam's two counters; an auto-reply
	/// or a custom game name emptied switches its switch off (on with nothing to say does nothing, and read as if it did);
	/// the scam or health chip changed takes back a channel left off on its own from before 1.8.2.
	/// </summary>
	/// <param name="config">A <see cref="BotConfig"/> or a <see cref="GlobalConfig"/>, already changed.</param>
	/// <param name="changed">The names of the settings that changed.</param>
	public static void Follow(object config, IReadOnlyCollection<string> changed) {
		if (config is GlobalConfig global) {
			ConfigStore.FollowAlertChips(global, changed);

			// A language chosen here - the walkthrough's own pick, or Settings - is picked: the walkthrough doesn't ask again.
			if (changed.Contains(nameof(GlobalConfig.Language))) {
				global.LanguagePicked = true;
			}

			return;
		}

		if (config is not BotConfig bot) {
			return;
		}

		if (changed.Contains(nameof(BotConfig.ClearNotifications))) {
			bot.ClearInventoryNotifications = bot.ClearNotifications;
		}

		if (changed.Contains(nameof(BotConfig.AutoReply)) && string.IsNullOrWhiteSpace(bot.AutoReply)) {
			bot.AutoReplyEnabled = false;
		}

		if (changed.Contains(nameof(BotConfig.CustomGameName)) && string.IsNullOrWhiteSpace(bot.CustomGameName)) {
			bot.CustomGameNameEnabled = false;
		}
	}

	/// <summary>
	/// A new account added as a human-mode one (the walkthrough, Add account, 'add ... human', an import): it doesn't join
	/// "Groups every account joins" - the nocat.farm group by default - so a believable account isn't publicly in a group
	/// named after the farming tool. Robot accounts still join. Only new accounts: one that's there keeps what it has.
	/// </summary>
	public static void NewHumanAccount(BotConfig cfg) => cfg.JoinGroup = false;
}
