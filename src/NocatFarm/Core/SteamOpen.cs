namespace NocatFarm.Core;

/// <summary>
/// "Open in Steam": sign the ordinary Steam desktop client into one of your accounts in a click, no password typing.
///
/// It does what the open-source account switchers do, and nothing riskier: it points Steam's own saved-accounts files
/// at the account (<c>config/loginusers.vdf</c> and the <c>AutoLoginUser</c> registry value), closes Steam and starts
/// it again. Steam then signs in from the login it already remembers for that account. nocat.farm never reads, writes
/// or hand-crafts a login token - if the client doesn't already remember the account, it says to sign in once with
/// "Remember me" ticked and leaves everything alone.
/// </summary>
/// <remarks>
/// Signing the client in does NOT stop nocat.farm farming the account: Steam allows many sign-ins at once and only one
/// <em>playing</em> session, so the farm keeps its session until you actually launch a game - which is the same
/// "you're playing" step-aside the app already handles. Windows only for now; the reading and writing of the Windows
/// files lives in <see cref="Windows.SteamClientSwitch"/>, and everything decided without touching the PC is here so
/// it can be tested with fake files.
/// </remarks>
public static class SteamOpen {
	// The outcome of a look or an attempt, as a short code the UI and the log turn into a sentence.
	public const string Ok = "ok";
	public const string Off = "off";                     // the button isn't turned on for this account
	public const string NotWindows = "not-windows";
	public const string NoSteam = "no-steam";            // Steam isn't installed as far as we can see
	public const string GameRunning = "game-running";    // a game is open in the client - don't yank it out from under it
	public const string NotRemembered = "not-remembered"; // the client doesn't remember this account yet

	/// <summary>Whether Open in Steam can work on this machine at all. Windows only for now.</summary>
	public static bool Supported => OperatingSystem.IsWindows();

	/// <summary>Is the button on for this account: the setting and its go-ahead together (like spare items).</summary>
	public static bool On(Config.BotConfig cfg, string account) => cfg.SteamOpenButton && SteamOpenLedger.Load(account).Approved;

	/// <summary>What the Steam client's saved-accounts files say right now, read without changing anything.</summary>
	public sealed record Inputs(string? VdfText, string? AutoLoginUser, bool RememberPassword, uint RunningAppId, bool SteamInstalled);

	/// <summary>What a switch would change: the files to write, or just a code when there's nothing safe to do.</summary>
	public sealed record Plan(string Code, string? NewVdf, string? NewAutoLoginUser);

	/// <summary>The numbers the dashboard's Turn on? pop-up draws in its own words.</summary>
	public sealed record Numbers(bool Supported, bool SteamFound, bool Remembered, bool GameRunning, string Account) {
		public string Kind => "steamopen";
	}

	/// <summary>
	/// Work out, from what the files say, what switching to <paramref name="account"/> would change - without touching
	/// anything. Never gives away or edits another account's login; the only field it clears on others is MostRecent,
	/// the single "last used" pointer the client reads at startup, which by definition can belong to one account.
	/// </summary>
	public static Plan Decide(string account, Inputs inputs) {
		if (!inputs.SteamInstalled) {
			return new Plan(NoSteam, null, null);
		}

		if (inputs.RunningAppId != 0) {
			return new Plan(GameRunning, null, null);
		}

		if (string.IsNullOrWhiteSpace(inputs.VdfText)) {
			return new Plan(NotRemembered, null, null);
		}

		SteamVdf root;

		try {
			root = SteamVdf.Parse(inputs.VdfText);
		} catch (FormatException) {
			return new Plan(NotRemembered, null, null);
		}

		SteamVdf? users = root.Child("users");

		if (users is not { IsMap: true }) {
			return new Plan(NotRemembered, null, null);
		}

		SteamVdf? target = null;

		foreach (KeyValuePair<string, SteamVdf> block in users.Children) {
			if (block.Value.IsMap && string.Equals(block.Value.Leaf("AccountName"), account, StringComparison.OrdinalIgnoreCase)) {
				target = block.Value;

				break;
			}
		}

		if (target == null) {
			return new Plan(NotRemembered, null, null);
		}

		// Point the client at this account: it is the most recent, remembered, allowed to sign in by itself. Every other
		// block only has MostRecent cleared - its name, its remembered flag and its token are left exactly as they were.
		foreach (KeyValuePair<string, SteamVdf> block in users.Children) {
			if (block.Value.IsMap) {
				block.Value.SetLeaf("MostRecent", ReferenceEquals(block.Value, target) ? "1" : "0");
			}
		}

		target.SetLeaf("RememberPassword", "1");
		target.SetLeaf("AllowAutoLogin", "1");

		return new Plan(Ok, root.Serialize(), target.Leaf("AccountName") ?? account);
	}

	/// <summary>The outcome as a sentence for the log and the console, in plain words.</summary>
	public static Said Message(string code, string account) => code switch {
		Ok => new Said("signing your Steam app into {0} - it closes and opens again", account),
		Off => new Said("Open in Steam isn't turned on for {0} - turn it on in its settings first", account),
		NotWindows => new Said("Open in Steam only works on Windows for now"),
		NoSteam => new Said("Steam doesn't look installed on this PC, so there's nothing to open"),
		GameRunning => new Said("a game is open in Steam - close it first, then open {0}", account),
		NotRemembered => new Said("sign into {0} once in Steam with \"Remember me\" ticked, then this button works", account),
		_ => new Said("couldn't open {0} in Steam", account)
	};

	/// <summary>
	/// Sign the Steam client into <paramref name="account"/> now. Refuses unless the button is on for it, a game isn't
	/// running, and the client already remembers it. Windows only; anywhere else it says so and changes nothing.
	/// </summary>
	public static async Task<(string Code, string Said)> OpenAsync(Bot bot) {
		if (!On(bot.Cfg, bot.Name)) {
			return (Off, Message(Off, bot.Name).ToEnglish());
		}

		if (!OperatingSystem.IsWindows()) {
			return (NotWindows, Message(NotWindows, bot.Name).ToEnglish());
		}

		return await Windows.SteamClientSwitch.OpenAsync(bot.Name).ConfigureAwait(false);
	}

	/// <summary>What a look found, for the preview - read-only, from the local files. Windows only.</summary>
	public static Numbers Look(string account) {
		if (!OperatingSystem.IsWindows()) {
			return new Numbers(false, false, false, false, account);
		}

		return Windows.SteamClientSwitch.Look(account);
	}
}
