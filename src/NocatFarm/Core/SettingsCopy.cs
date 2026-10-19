using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2: "Copy settings from" - one account's settings onto another, so a new account doesn't have to be set up again
/// by hand. From the dashboard's Add account, the console's 'add ... from &lt;account&gt;', an account's Settings page
/// ("Copy settings from...") and 'copy settings &lt;from&gt; &lt;to&gt;'.
/// </summary>
/// <remarks>
/// <para>Every per-account setting comes across except what belongs to that account alone (<see cref="NeverCopied"/>) -
/// and the five switches that are turned on only after their preview (<see cref="Switches.All"/>) always end up off on
/// the account copied to: their go-ahead is the owner's, given on that account, and never travels.</para>
/// <para>The copy is the source account's settings, as they are, mode included: a robot copied onto an account you sign
/// into yourself stays a robot. Only a mode chosen in the same step (Add account asks human or robot) switches it, the way
/// a dashboard save switching human mode does (<see cref="Settings.ApplyLegitMode"/>). The rest of a dashboard save's
/// checks run as well: bad values clamped, ranges put in order.</para>
/// <para>Nothing here changes the account copied from. <see cref="Build"/> changes nothing at all - it's the preview.</para>
/// </remarks>
public static class SettingsCopy {
	/// <summary>Settings that belong to one account alone - never copied.</summary>
	public static readonly IReadOnlyList<string> NeverCopied = [
		// signing in
		nameof(BotConfig.SteamLogin), nameof(BotConfig.SteamPassword), nameof(BotConfig.SignInWithQr), nameof(BotConfig.SteamParentalCode),
		// its authenticator
		nameof(BotConfig.SharedSecret), nameof(BotConfig.IdentitySecret),
		// its own connection, and the device name Steam sees
		nameof(BotConfig.AccountProxy), nameof(BotConfig.AccountProxyUsername), nameof(BotConfig.AccountProxyPassword), nameof(BotConfig.MachineName),
		// what tells it apart here
		nameof(BotConfig.Notes), nameof(BotConfig.LogColour),
		// whether it runs, and whether you sign into it from your own Steam app
		nameof(BotConfig.Enabled), nameof(BotConfig.StartPaused), nameof(BotConfig.IUseThisAccount),
		// the other account's trade link token
		nameof(BotConfig.TradeMasterToken)
	];

	/// <summary>Settings naming accounts: an entry naming the account copied to (itself) is taken out.</summary>
	public static readonly IReadOnlyList<string> NamesAccounts = [
		nameof(BotConfig.AutoTradeWith), nameof(BotConfig.TradeMasters), nameof(BotConfig.CommandMasters),
		nameof(BotConfig.SpareGemsSendTo), nameof(BotConfig.BoosterSendTo)
	];

	/// <summary>Whether a setting comes across in a copy: not one of an account's own, not a secret, not a switch with a go-ahead.</summary>
	public static bool Copied(SettingDef def) =>
		(def.Kind != SettingKind.Secret) && !NeverCopied.Contains(def.Name) && (Switches.FeatureOf(def.Name) == null);

	/// <summary>One setting the copy changes, as it is and as it would be.</summary>
	public sealed record Change(string Name, string Label, object? Before, object? After, string BeforeText, string AfterText);

	/// <summary>
	/// What a copy comes to. <paramref name="Result"/>: the settings the account would have. <paramref name="LeftOff"/>: switches
	/// on in the source, left off here. <paramref name="TurnedOff"/>: switches on here before, off after. <paramref name="Cleared"/>:
	/// settings that named the account itself, with that taken out. Error set: refused, nothing would change.
	/// </summary>
	public sealed record Plan(BotConfig Result, List<Change> Changes, List<string> LeftOff, List<string> TurnedOff, List<string> Cleared,
		List<string> Adjusted, string? Error);

	/// <summary>
	/// The account's settings with the source's copied over and checked like a dashboard save - changing nothing: not the
	/// source, not the target, not a go-ahead. <paramref name="human"/>: a mode chosen in the same step (Add account), or null
	/// to take the source's.
	/// </summary>
	public static Plan Build(BotConfig source, BotConfig target, string targetName, ulong targetSteamId, bool? human = null) {
		BotConfig from = Clone(source);   // nothing in the result is shared with the source - a list changed later changes one account
		BotConfig result = Clone(target);

		foreach (SettingDef def in Settings.Bot.Where(Copied)) {
			Set(result, def.Name, Settings.Read(from, def.Name));
		}

		// Human mode's stash of the robot games goes with the mode, so turning it off later on this account puts back the
		// source's games, not whatever this one had before.
		result.LegitBackup = from.LegitBackup;

		// The switches with a go-ahead: always off after a copy, whatever either account had.
		List<string> leftOff = [.. Switches.All.Select(static k => k.Setting).Where(s => Settings.Read(source, s) is true)];
		List<string> turnedOff = [.. Switches.All.Select(static k => k.Setting).Where(s => Settings.Read(target, s) is true)];

		foreach (Switches.Kind k in Switches.All) {
			Set(result, k.Setting, false);
		}

		List<string> cleared = [.. NamesAccounts.Where(n => WithoutSelf(result, n, targetName, targetSteamId))];

		// A mode picked in the same step: the switch a dashboard save makes, from the source's mode.
		if ((human is bool h) && (h != result.LegitMode)) {
			result.LegitMode = h;
			Settings.ApplyLegitMode(result, !h);
		}

		// Automatic booster packs keep their gems loose - the check a save makes (Modules.BoosterPick.Settle), said here
		// rather than logged, since this may only be the preview.
		List<string> adjusted = [];

		if ((result.SpareGems == target.SpareGems) && (result.BoosterMake == Modules.BoosterPick.Auto) && (target.BoosterMake != Modules.BoosterPick.Auto)
			&& (result.SpareGems is 0 or 2)) {
			result.SpareGems = 1;
			adjusted.Add(new Said("{0}: automatic booster packs need loose gems, so \"Gems afterwards\" is now \"keep them loose\"", targetName).ToString());
		}

		(string? error, List<string> clamped) = Web.WebHost.CheckLikeASave(result, target);
		adjusted.AddRange(clamped);

		List<Change> changes = [.. Settings.Bot
			.Where(d => d.Kind != SettingKind.Secret)
			.Where(d => Settings.Show(result, d) != Settings.Show(target, d))
			.Select(d => new Change(d.Name, d.Label, Settings.Read(target, d.Name), Settings.Read(result, d.Name), Text(target, d), Text(result, d)))];

		return new Plan(result, changes, leftOff, turnedOff, cleared, adjusted, error);
	}

	/// <summary>What copying did.</summary>
	public sealed record Done(Plan Plan, bool Saved);

	/// <summary>
	/// Copy <paramref name="from"/>'s settings onto <paramref name="to"/>, an account already here: under its settings lock, only
	/// what changes is written onto the settings it runs on (as a dashboard save does), its switches with a go-ahead off and
	/// their go-aheads cleared, saved, and the changes put into effect. The source isn't changed. Error set: nothing changed.
	/// </summary>
	public static Done Copy(Bot from, Bot to) {
		if (from == to) {
			return new Done(Refused("Pick two different accounts."), false);
		}

		BotConfig source;

		// Each account's lock on its own, never one inside the other - two copies the other way round can't wait on each other.
		lock (from.CfgGate) {
			source = Clone(from.Cfg);
		}

		Plan plan;
		bool saved;
		List<SettingDef> changed;

		lock (to.CfgGate) {
			BotConfig current = Clone(to.Cfg);
			plan = Build(source, current, to.Name, to.SteamId);

			if (plan.Error != null) {
				return new Done(plan, false);
			}

			// Every switch is off in the result, so this clears their go-aheads - the same rule as after any save.
			Switches.Settle(plan.Result, to.Name);

			changed = [.. Settings.Bot.Where(d => Settings.Show(plan.Result, d) != Settings.Show(current, d))];

			foreach (SettingDef def in changed) {
				Set(to.Cfg, def.Name, Settings.Read(plan.Result, def.Name));
			}

			to.Cfg.LegitBackup = plan.Result.LegitBackup;
			saved = ConfigStore.SaveBot(to.Name, to.Cfg);
		}

		foreach (SettingDef def in changed) {
			Commands.ApplyBotSideEffects(to, def);
		}

		LogCopied(to.Name, from.Name, plan);

		return new Done(plan, saved);
	}

	/// <summary>
	/// A new account's settings, copied from <paramref name="from"/>: <paramref name="fresh"/> (its sign-in) with everything
	/// else from the source. Not saved - Add account saves it as it adds it, after <see cref="Switches.Settle"/>.
	/// </summary>
	public static Plan ForNewAccount(Bot from, BotConfig fresh, string name, bool? human) {
		BotConfig source;

		lock (from.CfgGate) {
			source = Clone(from.Cfg);
		}

		return Build(source, fresh, name, 0, human);
	}

	/// <summary>The log line for a copy: "settings copied from new · 3 switches left off for you to turn on".</summary>
	public static void LogCopied(string to, string from, Plan plan) {
		Said said = plan.LeftOff.Count switch {
			0 => new Said("settings copied from {0}", from),
			1 => new Said("settings copied from {0} · 1 switch left off for you to turn on", from),
			_ => new Said("settings copied from {0} · {1} switches left off for you to turn on", from, plan.LeftOff.Count)
		};

		Log.Info(said, to);
	}

	/// <summary>A switch's name in plain words, and the console command that turns it on after its preview.</summary>
	public static (string Name, string Command) SwitchWords(string setting) => setting switch {
		Switches.Spare => ("Spare items", "spare on"),
		Switches.Points => ("Steam Points", "points on"),
		Switches.Buy => ("Buying cards", "buy on"),
		Switches.Swaps => ("Card swaps", "swaps on"),
		Switches.SteamOpenButton => ("Open in Steam", "steam on"),
		_ => (setting, Switches.CommandOf(setting) + " on")
	};

	/// <summary>What the console says about a copy before it's made: every change, what's never copied, what stays off.</summary>
	public static string PreviewText(string from, string to, Plan plan) {
		if (plan.Changes.Count == 0) {
			return $"{to} already has {from}'s settings - copying changes nothing.";
		}

		List<string> lines = [$"Copying {from}'s settings onto {to} changes {Count(plan.Changes.Count, "setting")}:"];
		const int Shown = 40;

		foreach (Change c in plan.Changes.Take(Shown)) {
			lines.Add($"  {c.Label}: {Blank(c.BeforeText)} -> {Blank(c.AfterText)}");
		}

		if (plan.Changes.Count > Shown) {
			lines.Add($"  ...and {plan.Changes.Count - Shown} more");
		}

		lines.AddRange(Notes(to, plan));
		lines.Add($"Never copied: {to}'s sign-in, authenticator, proxy, device name, notes, log colour, whether it runs, whether you sign into it yourself, and the trade link token.");
		lines.Add($"{from}'s settings stay as they are.");

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>The lines about switches left off, names taken out and values put right - in the preview and after the copy.</summary>
	public static List<string> Notes(string to, Plan plan) {
		List<string> lines = [];

		foreach (string s in plan.LeftOff) {
			(string name, string command) = SwitchWords(s);
			lines.Add($"{name} stays off on {to}: turn it on yourself after its preview ('{command} {to}', or the switch in its settings).");
		}

		foreach (string s in plan.TurnedOff.Where(s => !plan.LeftOff.Contains(s))) {
			lines.Add($"{SwitchWords(s).Name} goes off on {to}, as it is on the account copied from.");
		}

		foreach (string n in plan.Cleared) {
			lines.Add($"{Settings.FindBot(n)?.Label ?? n}: {to} itself is taken out of it.");
		}

		lines.AddRange(plan.Adjusted.Select(static a => "Put right: " + a));

		return lines;
	}

	/// <summary>A value as the console says it: a switch is on or off.</summary>
	private static string Text(BotConfig cfg, SettingDef def) =>
		def.Kind == SettingKind.Bool ? (Settings.Read(cfg, def.Name) is true ? "on" : "off") : Settings.Show(cfg, def);

	private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

	private static string Blank(string text) => text.Length == 0 ? "(empty)" : text;

	private static Plan Refused(string error) => new(new BotConfig(), [], [], [], [], [], error);

	/// <summary>Take the account itself out of a setting that names accounts. True when something was taken out.</summary>
	private static bool WithoutSelf(BotConfig cfg, string setting, string name, ulong steamId) {
		if (Settings.Read(cfg, setting) is not string list || (list.Trim().Length == 0)) {
			return false;
		}

		string id = steamId == 0 ? "" : steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);

		bool Self(string entry) {
			int colon = entry.IndexOf(':');
			string who = (colon < 0 ? entry : entry[..colon]).Trim();

			return who.Equals(name, StringComparison.OrdinalIgnoreCase) || ((id.Length > 0) && (who == id));
		}

		string[] entries = list.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (!entries.Any(Self)) {
			return false;
		}

		Set(cfg, setting, string.Join(", ", entries.Where(e => !Self(e))));

		return true;
	}

	private static void Set(BotConfig cfg, string name, object? value) => typeof(BotConfig).GetProperty(name)?.SetValue(cfg, value);

	private static BotConfig Clone(BotConfig cfg) => JsonSerializer.Deserialize<BotConfig>(JsonSerializer.Serialize(cfg))!;
}
