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
	public const string SteamBusy = "steam-busy";        // Steam wouldn't close, so nothing was changed
	public const string BackupFailed = "backup-failed";  // couldn't back the files up first, so nothing was changed

	/// <summary>Whether Open in Steam can work on this machine at all. Windows only for now.</summary>
	public static bool Supported => OperatingSystem.IsWindows();

	/// <summary>Is the button on for this account: the setting and its go-ahead together (like spare items).</summary>
	public static bool On(Config.BotConfig cfg, string account) => cfg.SteamOpenButton && SteamOpenLedger.Load(account).Approved;

	/// <summary>
	/// A test-only seam: when set, <see cref="Look"/> reads the Steam files through this instead of the real PC, so the
	/// checks never touch the machine's registry or Steam folder. Null in the running app.
	/// </summary>
	public static Func<Inputs>? ReaderForTests;

	/// <summary>What the Steam client's saved-accounts files say right now, read without changing anything.</summary>
	public sealed record Inputs(string? VdfText, string? AutoLoginUser, bool RememberPassword, uint RunningAppId, bool SteamInstalled);

	/// <summary>What a switch would change: the files to write, or just a code when there's nothing safe to do.</summary>
	public sealed record Plan(string Code, string? NewVdf, string? NewAutoLoginUser);

	/// <summary>The numbers the dashboard's Turn on? pop-up draws in its own words.</summary>
	public sealed record Numbers(bool Supported, bool SteamFound, bool Remembered, bool GameRunning, string Account) {
		public string Kind => "steamopen";
	}

	/// <summary>
	/// Work out, from what the files say, what signing the client into this account would change - without touching
	/// anything. The account is found by its SteamID64 (the block's own key) first, then by its Steam login name -
	/// never by the nocat.farm account name, which is only a label and may be nothing like the real login. Never gives
	/// away or edits another account's login; the only field it clears on others is MostRecent, the single "last used"
	/// pointer the client reads at startup, which by definition can belong to one account.
	/// </summary>
	/// <param name="login">The Steam login name (Cfg.SteamLogin).</param>
	/// <param name="steamId">The account's SteamID64, or 0 when it isn't known - then the login name alone is matched.</param>
	public static Plan Decide(string login, ulong steamId, Inputs inputs) {
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
		string steamId64 = steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);

		// First by the block's own key, the SteamID64 - the one thing that can't be two accounts.
		if (steamId != 0) {
			foreach (KeyValuePair<string, SteamVdf> block in users.Children) {
				if (block.Value.IsMap && string.Equals(block.Key, steamId64, StringComparison.Ordinal)) {
					target = block.Value;

					break;
				}
			}
		}

		// Then by the Steam login name, for an account we haven't signed in yet (no SteamID64 to go on).
		if ((target == null) && !string.IsNullOrWhiteSpace(login)) {
			foreach (KeyValuePair<string, SteamVdf> block in users.Children) {
				if (block.Value.IsMap && string.Equals(block.Value.Leaf("AccountName"), login, StringComparison.OrdinalIgnoreCase)) {
					target = block.Value;

					break;
				}
			}
		}

		if (target == null) {
			return new Plan(NotRemembered, null, null);
		}

		// Only when the client already has this account's password saved (RememberPassword "1"). Without it Steam would
		// stop at the password box, so it isn't really "remembered" - and we never turn RememberPassword on ourselves,
		// which would override the owner's own choice for the account.
		if (!string.Equals(target.Leaf("RememberPassword"), "1", StringComparison.Ordinal)) {
			return new Plan(NotRemembered, null, null);
		}

		// Point the client at this account: it is the most recent and allowed to sign in by itself. Every other block only
		// has MostRecent cleared - its name, its remembered flag and its token are left exactly as they were. The chosen
		// block's RememberPassword is left as the owner set it (already "1"); we only ever read it, never write it.
		foreach (KeyValuePair<string, SteamVdf> block in users.Children) {
			if (block.Value.IsMap) {
				block.Value.SetLeaf("MostRecent", ReferenceEquals(block.Value, target) ? "1" : "0");
			}
		}

		target.SetLeaf("AllowAutoLogin", "1");

		return new Plan(Ok, root.Serialize(), target.Leaf("AccountName") ?? login);
	}

	/// <summary>The outcome as a sentence for the log and the console, in plain words.</summary>
	public static Said Message(string code, string account) => code switch {
		Ok => new Said("signing your Steam app into {0} - it closes and opens again", account),
		Off => new Said("Open in Steam isn't turned on for {0} - turn it on in its settings first", account),
		NotWindows => new Said("Open in Steam only works on Windows for now"),
		NoSteam => new Said("Steam doesn't look installed on this PC, so there's nothing to open"),
		GameRunning => new Said("a game is open in Steam - close it first, then open {0}", account),
		NotRemembered => new Said("sign into {0} once in Steam with \"Remember me\" ticked, then this button works", account),
		SteamBusy => new Said("Steam wouldn't close, so nothing was changed - close Steam, then open {0} again", account),
		BackupFailed => new Said("couldn't back up Steam's saved accounts first, so nothing was changed"),
		_ => new Said("couldn't open {0} in Steam", account)
	};

	/// <summary>
	/// Sign the Steam client into <paramref name="bot"/>'s account now. Refuses unless the button is on for it, a game
	/// isn't running, and the client already remembers it. Windows only; anywhere else it says so and changes nothing.
	/// </summary>
	public static async Task<(string Code, string Said)> OpenAsync(Bot bot) {
		if (!On(bot.Cfg, bot.Name)) {
			return (Off, Message(Off, bot.Name).ToEnglish());
		}

		if (!OperatingSystem.IsWindows()) {
			return (NotWindows, Message(NotWindows, bot.Name).ToEnglish());
		}

		return await Windows.SteamClientSwitch.OpenAsync(bot.Cfg.SteamLogin, bot.SteamId, bot.Name).ConfigureAwait(false);
	}

	/// <summary>What a look found, for the preview - read-only, from the local files. Windows only (or a test fake).</summary>
	public static Numbers Look(Bot bot) => Look(bot.Cfg.SteamLogin, bot.SteamId, bot.Name);

	/// <summary>The same by its parts, so the window and the console can look without a live Bot.</summary>
	public static Numbers Look(string login, ulong steamId, string display) {
		if (ReaderForTests is { } fake) {
			return NumbersFrom(login, steamId, display, fake());
		}

		if (!OperatingSystem.IsWindows()) {
			return new Numbers(false, false, false, false, display);
		}

		return NumbersFrom(login, steamId, display, Windows.SteamClientSwitch.Read());
	}

	/// <summary>Turn what the files say into the preview's numbers, through <see cref="Decide"/> - no PC access of its own.</summary>
	internal static Numbers NumbersFrom(string login, ulong steamId, string display, Inputs inputs) {
		Plan plan = Decide(login, steamId, inputs);

		return new Numbers(
			Supported: true,
			SteamFound: inputs.SteamInstalled,
			Remembered: plan.Code is Ok,
			GameRunning: plan.Code is GameRunning,
			Account: display);
	}

	/// <summary>
	/// The Steam-client side of an attempt, as a handful of steps the caller supplies - so the order (close, then back up,
	/// then write, then start) and the "change nothing unless Steam really closed, and unless the backup worked" rule can be
	/// checked with fakes, away from a real PC. Windows fills these in with the real registry, files and process in
	/// <see cref="Windows.SteamClientSwitch"/>.
	/// </summary>
	/// <param name="Running">Is Steam running right now.</param>
	/// <param name="CloseAndWait">Ask Steam to close and wait for it; true when it's gone, false when it wouldn't.</param>
	/// <param name="Backup">Back up the files about to change; true on success, false when it couldn't (then change nothing).</param>
	/// <param name="Write">Write the plan's files and registry value.</param>
	/// <param name="Start">Start Steam again.</param>
	public sealed record Ops(Func<bool> Running, Func<Task<bool>> CloseAndWait, Func<bool> Backup, Action<Plan> Write, Action Start);

	/// <summary>
	/// Carry out an OK plan in the safe order: close Steam first and wait; only once it has really closed back the files up;
	/// only if that worked write the change; then start Steam. Returns the outcome code - <see cref="SteamBusy"/> if Steam
	/// wouldn't close and <see cref="BackupFailed"/> if the backup failed, each having changed nothing.
	/// </summary>
	public static async Task<string> ApplyAsync(Plan plan, Ops ops) {
		if (plan.Code is not Ok) {
			return plan.Code;
		}

		// Close first. A still-open client would read a half-written file, or overwrite our change when it saves on exit.
		if (ops.Running() && !await ops.CloseAndWait().ConfigureAwait(false)) {
			return SteamBusy;
		}

		if (!ops.Backup()) {
			return BackupFailed;
		}

		ops.Write(plan);
		ops.Start();

		return Ok;
	}
}
