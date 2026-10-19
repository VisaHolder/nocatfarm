using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm;

/// <summary>1.8.2: 'copy settings &lt;from&gt; &lt;to&gt;' and 'add ... from &lt;account&gt;' - one account's settings onto another (Core/SettingsCopy.cs).</summary>
public static partial class Commands {
	private const string CopyUsage = "copy settings <from> <to> [yes] - shows what copying <from>'s settings onto <to> would change; 'yes' copies them";

	private static string CopySettings(BotManager mgr, string[] args) {
		// 'settings' is the word that says what's copied; 'copy new new2' is read the same.
		string[] words = args.Length > 0 && args[0].Equals("settings", StringComparison.OrdinalIgnoreCase) && (mgr.Get(args[0]) == null) ? args[1..] : args;
		bool yes = (words.Length > 2) && words[2].ToLowerInvariant() is "yes" or "y" or "confirm";

		if ((words.Length < 2) || ((words.Length > 2) && !yes) || (words.Length > 3)) {
			return CopyUsage;
		}

		if (mgr.Get(words[0]) is not { } from) {
			return NoSuchAccount(mgr, words[0]);
		}

		if (mgr.Get(words[1]) is not { } to) {
			return NoSuchAccount(mgr, words[1]);
		}

		if (from == to) {
			return "Pick two different accounts.";
		}

		if (!yes) {
			BotConfig source, target;

			lock (from.CfgGate) {
				source = Clone(from.Cfg);
			}

			lock (to.CfgGate) {
				target = Clone(to.Cfg);
			}

			SettingsCopy.Plan plan = SettingsCopy.Build(source, target, to.Name, to.SteamId);

			if (plan.Error != null) {
				return $"Can't copy {from.Name}'s settings onto {to.Name}: {plan.Error}";
			}

			return SettingsCopy.PreviewText(from.Name, to.Name, plan) + Environment.NewLine
				+ (plan.Changes.Count == 0 ? "Nothing to copy." : $"To copy them, type: copy settings {from.Name} {to.Name} yes");
		}

		SettingsCopy.Done done = SettingsCopy.Copy(from, to);

		if (done.Plan.Error != null) {
			return $"Not copied: {done.Plan.Error}";
		}

		List<string> lines = [$"{to.Name}: settings copied from {from.Name} - {(done.Plan.Changes.Count == 1 ? "1 setting" : $"{done.Plan.Changes.Count} settings")} changed."];
		lines.AddRange(SettingsCopy.Notes(to.Name, done.Plan));

		if (!done.Saved) {
			lines.Add("In use now, but not saved to disk - see the Log.");
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>What 'add' starts an account with. Wrong set: not added, and what to say instead.</summary>
	public sealed record NewAccount(BotConfig? Cfg, string? Kind, string? CopiedFrom, SettingsCopy.Plan? Copied, string? Wrong);

	/// <summary>
	/// The settings 'add &lt;name&gt; &lt;login|qr&gt; [human|robot] [from &lt;account&gt;]' starts an account with - nothing added or
	/// saved yet. With 'from', the other account's settings (Core/SettingsCopy.cs), its switches with a go-ahead off and any
	/// go-ahead left under this name from before cleared; 'human' or 'robot' as well picks the mode, as a save switching it does.
	/// </summary>
	public static NewAccount AddConfig(BotManager mgr, string name, string[] args) {
		(string? kind, string? copyFrom, string? wrong) = AddWords(args);

		if (wrong != null) {
			return new NewAccount(null, null, null, null, wrong);
		}

		bool qr = args[1].Equals("qr", StringComparison.OrdinalIgnoreCase);
		BotConfig cfg = qr ? new BotConfig { SteamLogin = name, SignInWithQr = true } : new BotConfig { SteamLogin = args[1] };

		if (copyFrom == null) {
			cfg.LegitMode = kind == "human";

			if (cfg.LegitMode) {
				Settings.NewHumanAccount(cfg);
			}

			return new NewAccount(cfg, kind, null, null, null);
		}

		if (mgr.Get(copyFrom) is not { } source) {
			return new NewAccount(null, kind, null, null, NoSuchAccount(mgr, copyFrom));
		}

		SettingsCopy.Plan copied = SettingsCopy.ForNewAccount(source, cfg, name, kind == null ? null : kind == "human");

		if (copied.Error != null) {
			return new NewAccount(null, kind, source.Name, copied, $"Not added: {copied.Error}");
		}

		Switches.Settle(copied.Result, name);

		return new NewAccount(copied.Result, kind, source.Name, copied, null);
	}

	/// <summary>The words after 'add &lt;name&gt; &lt;login&gt;': human or robot, and 'from &lt;account&gt;'. Wrong set: what to say instead.</summary>
	private static (string? Kind, string? From, string? Wrong) AddWords(string[] args) {
		string? kind = null, from = null;

		for (int i = 2; i < args.Length; i++) {
			string w = args[i].ToLowerInvariant();

			if (w == "from") {
				if (i + 1 >= args.Length) {
					return (null, null, "add <name> <steamLogin|qr> from <account> - the account whose settings it starts with");
				}

				from = args[++i];
			} else if ((w is "human" or "robot") && (kind == null)) {
				kind = w;
			} else if (i == 2) {
				return (null, null, "The last word is 'human' (your main - human mode) or 'robot' (a farm account, full speed).");
			}

			// Anything else further on is left out, as it always was - a password typed on the end included.
		}

		return (kind, from, null);
	}

	/// <summary>What 'add ... from' says about the settings it started with.</summary>
	private static string CopiedNote(string name, string from, SettingsCopy.Plan plan) {
		string mode = plan.Result.LegitMode ? "It's in human mode." : "It's a robot account (full speed).";
		List<string> notes = SettingsCopy.Notes(name, plan);

		return $" Its settings come from {from}. {mode}" + string.Concat(notes.Select(static n => Environment.NewLine + n));
	}

	private static BotConfig Clone(BotConfig cfg) => System.Text.Json.JsonSerializer.Deserialize<BotConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
}
