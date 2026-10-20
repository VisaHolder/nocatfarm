using NocatFarm.Core;

namespace NocatFarm;

/// <summary>Open in Steam ('steam') - 1.8.2. Signs this PC's Steam desktop client into an account in one click.</summary>
public static partial class Commands {
	private static async Task<string> SteamAsync(BotManager mgr, string[] args) {
		string verb = "status";
		string? account = null;

		// The verb and the account in either order, as 'spare' reads them - a word that is also an account's name is the account.
		foreach (string a in args) {
			string w = a.ToLowerInvariant();

			if ((w is "open" or "on" or "off" or "status" or "preview") && (mgr.Get(a) == null)) {
				verb = w == "preview" ? "status" : w;
			} else {
				account ??= a;
			}
		}

		if (verb == "status") {
			if (account == null) {
				if (mgr.All.Count == 0) {
					return "No accounts are set up yet.";
				}

				if (!SteamOpen.Supported) {
					return "Open in Steam only works on Windows for now.";
				}

				return string.Join(Environment.NewLine, mgr.All.Select(b => {
					SteamOpen.Numbers n = SteamOpen.Look(b);
					string state = !SteamOpen.On(b.Cfg, b.Name) ? "button off"
						: !n.SteamFound ? "Steam not found"
						: n.GameRunning ? "a game is open in Steam"
						: n.Remembered ? "ready"
						: n.SignIn ? "ready - opens Steam's sign-in the first time" : "sign in once with Remember me";

					return $"{b.Name}: {state}";
				}).Prepend(SteamOpen.EveryoneOn(mgr.Global) ? "Open in Steam is on for every account ('steam off all' hides them)." : "")
					.Where(static l => l.Length > 0));
			}

			if (EveryAccount(account) && (mgr.Get(account) == null)) {
				return SteamEveryonePreview(mgr);
			}

			if (mgr.Get(account) is not { } who) {
				return NoSuchAccount(mgr, account);
			}

			return await SwitchPreviewAsync(who, Switches.SteamOpenButton, CancellationToken.None).ConfigureAwait(false);
		}

		if (account == null) {
			return $"steam {verb} <account> - one account at a time";
		}

		// 1.8.2: 'steam on all' / 'steam off all' - the one switch for every account ('steam open' is still one at a time).
		if (EveryAccount(account) && (mgr.Get(account) == null)) {
			if (verb == "open") {
				return "steam open <account> - one account at a time";
			}

			string said = Switches.SetEveryone(mgr.Global, verb == "on");

			return verb == "on"
				? $"{said}. 'steam open <account>' signs your Steam app into one; 'steam off all' hides them again."
				: $"{said}.";
		}

		if (mgr.Get(account) is not { } bot) {
			return NoSuchAccount(mgr, account);
		}

		if (verb == "off") {
			Switches.Set(bot, Switches.SteamOpenButton, false);

			return SteamOpen.EveryoneOn(mgr.Global)
				? $"{bot.Name}: its own Open in Steam switch is off - but the button still shows, because it's on for every account ('steam off all' hides them all)."
				: $"{bot.Name}: Open in Steam is off - the button is hidden.";
		}

		if (verb == "on") {
			Switches.Set(bot, Switches.SteamOpenButton, true);

			return $"{bot.Name}: Open in Steam is on - the button shows on its card and in the window. 'steam open {bot.Name}' signs your Steam app into it; 'steam off {bot.Name}' hides it again.";
		}

		// open
		(string code, string opened) = await SteamOpen.OpenAsync(bot).ConfigureAwait(false);

		// 1.8.2: on the sign-in, where its Steam Guard code is - never the code itself here, since replies go in the log.
		return (code == SteamOpen.SignIn) && bot.HasAuthenticator
			? $"{bot.Name}: {opened}{Environment.NewLine}Its Steam Guard code is on its card in the dashboard, or type: 2fa {bot.Name}"
			: $"{bot.Name}: {opened}";
	}

	/// <summary>"all" (or "everyone"): the switch for every account, in 'steam' and 'set'.</summary>
	private static bool EveryAccount(string word) => word.Equals("all", StringComparison.OrdinalIgnoreCase) || word.Equals("everyone", StringComparison.OrdinalIgnoreCase);

	/// <summary>1.8.2: the preview of Open in Steam on every account, and how to turn it on.</summary>
	private static string SteamEveryonePreview(BotManager mgr) {
		(string text, _) = SwitchPreview.LookEveryone(mgr.All);
		string state = SteamOpen.EveryoneOn(mgr.Global)
			? "It's on for every account now. To turn it off, type: steam off all"
			: "It's off. To turn it on for every account, type: steam on all";

		return text + Environment.NewLine + state;
	}
}
