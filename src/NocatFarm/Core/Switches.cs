using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Spare items and Steam Points are each ONE switch: off is off (nothing happens, and the go-ahead is cleared), and on
/// means on - turned on only after its preview has been shown (the dashboard's confirm popup, or 'spare on' / 'points on'
/// at the console), and approved in the same step. There is no "on but waiting for a command".
/// </summary>
/// <remarks>
/// The go-ahead still lives beside the account's spending (SpareLedger, PointsLedger), never in its settings - so a settings
/// file or a backup can't bring it back. The switch in the settings is the one people see; <see cref="Settle"/> keeps the
/// two the same after every change, however it was made.
/// </remarks>
public static class Switches {
	public const string Spare = "SpareItems";
	public const string Points = "SpendPoints";

	/// <summary>Whether this account's spare items are on: the switch and its go-ahead together.</summary>
	public static bool SpareOn(BotConfig cfg, string account) => cfg.SpareItems && SpareLedger.Load(account).Approved;

	/// <summary>
	/// Turn one of the two on (switch and go-ahead together) or off (both cleared), and save. The caller has shown what it
	/// would do first. A line for the log and the reply.
	/// </summary>
	public static string Set(Bot bot, string feature, bool on) {
		lock (bot.CfgGate) {
			if (feature == Spare) {
				bot.Cfg.SpareItems = on;
				SpareLedger ledger = SpareLedger.Load(bot.Name);
				ledger.Approved = on;
				ledger.Save();
			} else {
				Modules.PointsLedger.Approved(bot);   // an old ok in the settings is moved over first, so this one has the last word
				bot.Cfg.SpendPoints = on;
				Modules.PointsLedger.Of(bot.Name).SetOk(on);
			}

			if (File.Exists(Path.Combine(ConfigStore.ConfigDir, bot.Name + ".json"))) {
				ConfigStore.SaveBot(bot.Name, bot.Cfg);
			}
		}

		Said said = (feature, on) switch {
			(Spare, true) => new Said("Spare items is on for {0}: it turns spare items into gems or sells them, within its limits", bot.Name),
			(Spare, false) => new Said("Spare items is off for {0}: nothing is turned into gems or sold", bot.Name),
			(_, true) => new Said("Spending Steam Points is on for {0}: it buys badge levels by itself, within its limits", bot.Name),
			_ => new Said("Spending Steam Points is off for {0}: nothing is spent", bot.Name)
		};

		Log.Info(said, bot.Name);

		return said.ToEnglish();
	}

	/// <summary>
	/// After any change to the settings (a dashboard save, 'set', a reload): a switch that's on without its go-ahead is put
	/// back off - it can only be turned on through its preview - and one that's off has its go-ahead cleared. True when the
	/// settings were changed (to be saved). Call with the account's settings lock held.
	/// </summary>
	public static bool Settle(BotConfig cfg, string account) {
		bool changed = false;
		SpareLedger spare = SpareLedger.Load(account);

		if (cfg.SpareItems && !spare.Approved) {
			cfg.SpareItems = false;
			changed = true;
		} else if (!cfg.SpareItems && spare.Approved) {
			spare.Approved = false;
			spare.Save();
		}

		Modules.PointsLedger points = Modules.PointsLedger.Of(account);
		bool ok = points.Ok || cfg.PointsOk;   // an old ok still in the settings counts until it's moved over

		if (cfg.SpendPoints && !ok) {
			cfg.SpendPoints = false;
			changed = true;
		} else if (!cfg.SpendPoints && ok) {
			points.SetOk(false);

			if (cfg.PointsOk) {
				cfg.PointsOk = false;
				changed = true;
			}
		}

		return changed;
	}

	/// <summary>
	/// The feature in the dashboard's preview address (/api/bots/{name}/switch/spare/preview): its word ('spare', 'points', as
	/// the page sends it) or its setting's name. Null for anything else.
	/// </summary>
	public static string? FeatureOfWord(string word) =>
		word.Equals("spare", StringComparison.OrdinalIgnoreCase) ? Spare : word.Equals("points", StringComparison.OrdinalIgnoreCase) ? Points : FeatureOf(word);

	/// <summary>The setting's name for the feature a word names ('spare', 'points'), or null.</summary>
	public static string? FeatureOf(string setting) =>
		setting.Equals(Spare, StringComparison.OrdinalIgnoreCase) ? Spare : setting.Equals(Points, StringComparison.OrdinalIgnoreCase) ? Points : null;
}
