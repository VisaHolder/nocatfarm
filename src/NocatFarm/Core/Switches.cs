using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Spare items, Steam Points, Open in Steam, buying cards and card swaps are each ONE switch (<see cref="All"/>): off is
/// off (nothing happens, and the go-ahead is cleared), and on means on - turned on only after its preview has been shown
/// (the dashboard's confirm popup, or 'spare on' / 'buy on' ... at the console), and approved in the same step. There is
/// no "on but waiting for a command".
/// </summary>
/// <remarks>
/// The go-ahead still lives beside the account's spending (SpareLedger, PointsLedger), never in its settings - so a settings
/// file or a backup can't bring it back. The switch in the settings is the one people see; <see cref="Settle"/> keeps the
/// two the same after every change, however it was made.
/// </remarks>
public static class Switches {
	public const string Spare = "SpareItems";
	public const string Points = "SpendPoints";

	// 1.8.2: the Open in Steam button is a third switch of the same shape - off is off, on only after its preview's
	// Turn on. Its go-ahead lives in SteamOpenLedger, so a settings file or a backup can't bring it back.
	public const string SteamOpenButton = "SteamOpenButton";

	/// <summary>Buying cards (1.8.2): its go-ahead lives beside its spending, in Modules.BuyLedger.</summary>
	public const string Buy = "BuyCards";

	/// <summary>1.8.2: card swaps with other nocat.farm users (Core/CardExchange.cs) - its go-ahead is in its own ledger.</summary>
	public const string Swaps = "CardSwaps";

	/// <summary>Whether this account's spare items are on: the switch and its go-ahead together.</summary>
	public static bool SpareOn(BotConfig cfg, string account) => cfg.SpareItems && SpareLedger.Load(account).Approved;

	/// <summary>
	/// Turn one on (switch and go-ahead together) or off (both cleared), and save. The caller has shown what it
	/// would do first. A line for the log and the reply.
	/// </summary>
	public static string Set(Bot bot, string feature, bool on) {
		// 1.8.2: the Open in Steam button. No spending and no items, so its go-ahead is a single flag in its ledger;
		// everything else below (spare items, Steam Points) is unchanged.
		if (feature == SteamOpenButton) {
			lock (bot.CfgGate) {
				bot.Cfg.SteamOpenButton = on;
				SteamOpenLedger ledger = SteamOpenLedger.Load(bot.Name);
				ledger.Approved = on;
				ledger.Save();

				if (File.Exists(Path.Combine(ConfigStore.ConfigDir, bot.Name + ".json"))) {
					ConfigStore.SaveBot(bot.Name, bot.Cfg);
				}
			}

			Said steamSaid = on
				? new Said("Open in Steam is on for {0}: a one-click button signs your Steam app into it", bot.Name)
				: new Said("Open in Steam is off for {0}: the button is hidden", bot.Name);
			Log.Info(steamSaid, bot.Name);

			return steamSaid.ToEnglish();
		}

		lock (bot.CfgGate) {
			if (feature == Swaps) {
				bot.Cfg.CardSwaps = on;
				CardExchange.Ledger.Load(bot.Name).Change(l => l.Approved = on);
			} else if (feature == Spare) {
				bot.Cfg.SpareItems = on;
				SpareLedger ledger = SpareLedger.Load(bot.Name);
				ledger.Approved = on;
				ledger.Save();
			} else if (feature == Buy) {
				bot.Cfg.BuyCards = on;
				Modules.BuyLedger.Of(bot.Name).SetOk(on, bot.Cfg.BuyDailyLimit, bot.Cfg.BuyWeeklyLimit);   // 1.8.2: with the limits it was shown
			} else {
				Modules.PointsLedger.Approved(bot);   // an old ok in the settings is moved over first, so this one has the last word
				bot.Cfg.SpendPoints = on;
				Modules.PointsLedger.Of(bot.Name).SetOk(on);
			}

			if (File.Exists(Path.Combine(ConfigStore.ConfigDir, bot.Name + ".json"))) {
				ConfigStore.SaveBot(bot.Name, bot.Cfg);
			}
		}

		// On or off, nocat.lol hears in a minute or two: an opt-out deletes this account's SteamID and trade link there.
		if (feature == Swaps) {
			CardExchange.Nudge();
		}

		Said said = (feature, on) switch {
			(Swaps, true) => new Said("Card swaps is on for {0}: it swaps spare cards with other nocat.farm users, within its limits", bot.Name),
			(Swaps, false) => new Said("Card swaps is off for {0}: nothing is swapped, and nocat.lol forgets it", bot.Name),
			(Spare, true) => new Said("Spare items is on for {0}: it turns spare items into gems or sells them, within its limits", bot.Name),
			(Spare, false) => new Said("Spare items is off for {0}: nothing is turned into gems or sold", bot.Name),
			(Buy, true) => new Said("Buying cards is on for {0}: it buys missing cards for its badges by itself, within its limits", bot.Name),
			(Buy, false) => new Said("Buying cards is off for {0}: nothing is bought", bot.Name),
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

		// 1.8.2: Open in Steam, the same rule - on without its go-ahead goes off, off clears any stale go-ahead.
		SteamOpenLedger steam = SteamOpenLedger.Load(account);

		if (cfg.SteamOpenButton && !steam.Approved) {
			cfg.SteamOpenButton = false;
			changed = true;
		} else if (!cfg.SteamOpenButton && steam.Approved) {
			steam.Approved = false;
			steam.Save();
		}

		SpareLedger spare = SpareLedger.Load(account);

		if (cfg.SpareItems && !spare.Approved) {
			cfg.SpareItems = false;
			changed = true;
		} else if (!cfg.SpareItems && spare.Approved) {
			spare.Approved = false;
			spare.Save();
		}

		CardExchange.Ledger swaps = CardExchange.Ledger.Load(account);

		if (cfg.CardSwaps && !swaps.Approved) {
			cfg.CardSwaps = false;
			changed = true;
		} else if (!cfg.CardSwaps && swaps.Approved) {
			swaps.Change(static l => l.Approved = false);
			CardExchange.Nudge();
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

		// Buying cards: the same rule, its go-ahead in its own spending list.
		Modules.BuyLedger buy = Modules.BuyLedger.Of(account);

		// 1.8.2: a spend limit turned to "No limit" since the go-ahead: off again - its preview shows what it may spend now, and
		// it's turned on again from there. Said plainly, once.
		if (cfg.BuyCards && buy.Ok && buy.NoLimitSince(cfg.BuyDailyLimit, cfg.BuyWeeklyLimit)) {
			buy.SetOk(false);
			Log.Attention(new Said("buying cards turned off · a spend limit was set to no limit - turn buying on again to see what it may spend and say yes"), account);
		}

		if (cfg.BuyCards && !buy.Ok) {
			cfg.BuyCards = false;
			changed = true;
		} else if (!cfg.BuyCards && buy.Ok) {
			buy.SetOk(false);
		}

		return changed;
	}

	/// <summary>One switch: its setting's name, the word the dashboard asks its preview by, and the console command that turns it on.</summary>
	public sealed record Kind(string Setting, string Word, string Command);

	/// <summary>Every switch turned on only through its preview - one row each, so the dashboard, the console and the API agree.</summary>
	public static readonly IReadOnlyList<Kind> All = [
		new(Spare, "spare", "spare"),
		new(Points, "points", "points"),
		new(SteamOpenButton, "steamopen", "steam"),
		new(Buy, "buy", "buy"),
		new(Swaps, "swaps", "swaps")
	];

	// ── 1.8.2: Open in Steam on every account - one global switch of the same shape ──
	/// <summary>The global setting "Open in Steam buttons on every account". Not in <see cref="All"/>: it isn't an account's.</summary>
	public const string SteamOpenEveryone = "SteamOpenEveryAccount";

	/// <summary>
	/// Turn the Open in Steam button on (setting and go-ahead together) or off (both cleared) for every account at once, and
	/// save. The caller has shown its preview first. An account's own switch is left as it is. A line for the log and the reply.
	/// </summary>
	public static string SetEveryone(GlobalConfig g, bool on) {
		// Under the lock every change to the global settings takes, so a dashboard save at the same moment can't undo it.
		lock (ConfigStore.GlobalEditGate) {
			g.SteamOpenEveryAccount = on;
			SteamOpenLedger ledger = SteamOpenLedger.Everyone();
			ledger.Approved = on;
			ledger.Save();
			ConfigStore.SaveGlobal(g);
		}

		Said said = on
			? new Said("Open in Steam is on for every account: each one shows the one-click button")
			: new Said("Open in Steam is off for every account: only accounts turned on one by one show it");
		Log.Info(said);

		return said.ToEnglish();
	}

	/// <summary>
	/// After a change to the global settings (a dashboard save, a load): on without its go-ahead goes back off, off clears a
	/// go-ahead left behind. True when the settings were changed (to be saved).
	/// </summary>
	public static bool SettleEveryone(GlobalConfig g) {
		SteamOpenLedger ledger = SteamOpenLedger.Everyone();

		if (g.SteamOpenEveryAccount && !ledger.Approved) {
			g.SteamOpenEveryAccount = false;

			return true;
		}

		if (!g.SteamOpenEveryAccount && ledger.Approved) {
			ledger.Approved = false;
			ledger.Save();
		}

		return false;
	}

	/// <summary>Whether a switch is on for this account: the setting and its go-ahead together.</summary>
	public static bool IsOn(Bot bot, string feature) => feature switch {
		Spare => SpareOn(bot.Cfg, bot.Name),
		SteamOpenButton => SteamOpen.AccountOn(bot.Cfg, bot.Name),
		Buy => Modules.CardBuyer.On(bot),
		Swaps => CardExchange.On(bot.Cfg, bot.Name),
		_ => bot.Cfg.SpendPoints && Modules.PointsLedger.Approved(bot)
	};

	/// <summary>The console command that turns a switch on ('spare on &lt;account&gt;', 'steam on &lt;account&gt;').</summary>
	public static string CommandOf(string feature) => All.FirstOrDefault(k => k.Setting == feature)?.Command ?? feature;

	/// <summary>
	/// The feature in the dashboard's preview address (/api/bots/{name}/switch/spare/preview): its word ('spare', 'points',
	/// 'steamopen', 'buy', 'swaps', as the page sends it) or its setting's name. Null for anything else.
	/// </summary>
	public static string? FeatureOfWord(string word) =>
		All.FirstOrDefault(k => k.Word.Equals(word, StringComparison.OrdinalIgnoreCase))?.Setting ?? FeatureOf(word);

	/// <summary>The switch a setting's name is ('SpareItems', 'BuyCards'...), or null.</summary>
	public static string? FeatureOf(string setting) =>
		All.FirstOrDefault(k => k.Setting.Equals(setting, StringComparison.OrdinalIgnoreCase))?.Setting;
}
