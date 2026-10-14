namespace NocatFarm.Core;

/// <summary>
/// "Open in Steam": sign the ordinary Steam desktop client into one of your accounts in a click, no password typing.
///
/// It does what the open-source account switchers do, and nothing riskier: it points Steam's own saved-accounts files
/// at the account (<c>config/loginusers.vdf</c> and the <c>AutoLoginUser</c> registry value), closes Steam and starts
/// it again. Steam then signs in from the login it already remembers for that account. nocat.farm never reads, writes
/// or hand-crafts a login token - if the client doesn't already remember the account, it says to sign in once with
/// "Remember me" ticked and leaves everything alone. 1.8.2: an account Steam doesn't remember yet still opens - on
/// Steam's own sign-in, with its login name filled in (only the AutoLoginUser registry value points at it).
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

	// 1.8.2: an account Steam doesn't remember yet still opens - on Steam's sign-in, with its login name filled in.
	public const string SignIn = "sign-in";

	// ── how Steam is closed and started (1.8.2) ──
	// nocat.farm never touches the Steam Client Service, never repairs or installs anything, and never runs anything
	// elevated: no "runas", no admin rights asked for. It only asks the running Steam to close (steam.exe -shutdown, as
	// Steam documents) and starts Steam again exactly as a double-click on it does.

	/// <summary>
	/// 1.8.2: how Steam is started - the way a double-click does: <c>explorer.exe "&lt;steam.exe&gt;"</c>, through the shell
	/// (ShellExecute, no handles inherited). Explorer starts Steam, so Steam's parent is Explorer, not nocat.farm, and it
	/// shares nothing with nocat.farm's process. Started as nocat.farm's own child, Steam showed "Steam Service Error: the
	/// Steam Service on your machine requires some maintenance". Nothing is passed to Steam itself - no login, no switches.
	/// </summary>
	/// <param name="steamExe">The full path of steam.exe.</param>
	/// <param name="windowsDir">The Windows folder, where explorer.exe is - a full path, never looked up on PATH.</param>
	public static System.Diagnostics.ProcessStartInfo StartInfo(string steamExe, string windowsDir) =>
		new(Path.Combine(windowsDir, "explorer.exe"), "\"" + steamExe + "\"") { UseShellExecute = true, Verb = "" };

	/// <summary>
	/// 1.8.2: how the running Steam is asked to close - <c>steam.exe -shutdown</c>, Steam's own documented switch, through the
	/// shell (ShellExecute, no handles inherited, never elevated). This second steam.exe only tells the running one to close
	/// and exits; it never runs Steam itself.
	/// </summary>
	public static System.Diagnostics.ProcessStartInfo ShutdownInfo(string steamExe) =>
		new(steamExe, "-shutdown") { UseShellExecute = true, Verb = "" };

	/// <summary>Whether Open in Steam can work on this machine at all. Windows only for now.</summary>
	public static bool Supported => OperatingSystem.IsWindows();

	/// <summary>
	/// Does this account show the button: its own switch, or the one for every account (1.8.2) - each the setting and its
	/// go-ahead together (like spare items).
	/// </summary>
	public static bool On(Config.BotConfig cfg, string account) => AccountOn(cfg, account) || EveryoneOn(Config.Live.Global);

	/// <summary>This account's own switch: the setting and its go-ahead together.</summary>
	public static bool AccountOn(Config.BotConfig cfg, string account) => cfg.SteamOpenButton && SteamOpenLedger.Load(account).Approved;

	/// <summary>1.8.2: "Open in Steam buttons on every account" - the global setting and its go-ahead together.</summary>
	public static bool EveryoneOn(Config.GlobalConfig g) => g.SteamOpenEveryAccount && SteamOpenLedger.Everyone().Approved;

	/// <summary>
	/// A test-only seam: when set, <see cref="Look"/> reads the Steam files through this instead of the real PC, so the
	/// checks never touch the machine's registry or Steam folder. Null in the running app.
	/// </summary>
	public static Func<Inputs>? ReaderForTests;

	/// <summary>
	/// What the Steam client's saved-accounts files say right now, read without changing anything. 1.8.2:
	/// <paramref name="ConfigText"/> is <c>config/config.vdf</c>, read only for Steam's "Ask which account to use each
	/// time Steam starts" (AlwaysShowUserChooser).
	/// </summary>
	public sealed record Inputs(string? VdfText, string? AutoLoginUser, bool RememberPassword, uint RunningAppId, bool SteamInstalled, string? ConfigText = null);

	/// <summary>
	/// What a switch would change: the files to write, or just a code when there's nothing safe to do. 1.8.2:
	/// <paramref name="NewConfig"/> is config.vdf with only the account picker turned off (null: config.vdf is left alone).
	/// </summary>
	public sealed record Plan(string Code, string? NewVdf, string? NewAutoLoginUser, string? NewConfig = null);

	/// <summary>The numbers the dashboard's Turn on? pop-up draws in its own words.</summary>
	/// <param name="SignIn">1.8.2: Steam doesn't remember it yet - the button opens Steam's sign-in with its name filled in.</param>
	/// <param name="AsksWhichAccount">1.8.2: Steam's "Ask which account to use each time Steam starts" is on - the button turns it off.</param>
	public sealed record Numbers(bool Supported, bool SteamFound, bool Remembered, bool GameRunning, string Account, bool SignIn = false, bool AsksWhichAccount = false) {
		public string Kind => "steamopen";
	}

	/// <summary>Steam's "Ask which account to use each time Steam starts", as config.vdf keeps it.</summary>
	public const string ChooserKey = "AlwaysShowUserChooser";

	/// <summary>
	/// config.vdf with the account picker turned off ("1" to "0") and every other byte as it was; null when it's already
	/// off, isn't there, or the file can't be read (then it's left alone). With the picker on, Steam shows "Who's playing?"
	/// and ignores the account it was pointed at.
	/// </summary>
	public static string? ChooserOff(string? configText) =>
		string.IsNullOrEmpty(configText) ? null : SteamVdf.ReplaceLeafInPlace(configText, ChooserKey, "1", "0");

	/// <summary>The log line when it turned the picker off.</summary>
	public static Said ChooserLine => new("account picker · turned off so Steam signs in by itself");


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

		// The same in both cases below: Steam's "Ask which account to use each time Steam starts" off, or it would show
		// its "Who's playing?" picker and ignore the account it's pointed at.
		string? newConfig = ChooserOff(inputs.ConfigText);

		if (string.IsNullOrWhiteSpace(inputs.VdfText)) {
			return SignInPlan(null, login, newConfig);
		}

		SteamVdf root;

		try {
			root = SteamVdf.Parse(inputs.VdfText);
		} catch (FormatException) {
			return SignInPlan(null, login, newConfig);
		}

		SteamVdf? users = root.Child("users");

		if (users is not { IsMap: true }) {
			return SignInPlan(null, login, newConfig);
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
			return SignInPlan(null, login, newConfig);
		}

		// Only when the client already has this account's password saved (RememberPassword "1"). Without it Steam would
		// stop at the password box, so it isn't really "remembered" - and we never turn RememberPassword on ourselves,
		// which would override the owner's own choice for the account. 1.8.2: then it opens Steam's sign-in for it instead.
		if (!string.Equals(target.Leaf("RememberPassword"), "1", StringComparison.Ordinal)) {
			return SignInPlan(target, login, newConfig);
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

		return new Plan(Ok, root.Serialize(), target.Leaf("AccountName") ?? login, newConfig);
	}

	/// <summary>
	/// 1.8.2: an account Steam doesn't remember (or remembers without its password): only the registry's AutoLoginUser is
	/// pointed at its Steam login, so Steam opens its sign-in with the name filled in. loginusers.vdf is left exactly as it
	/// is - no block is added for it - and no password or token is written anywhere. With no login name to go on there's
	/// nothing to fill in, so it says to sign in once instead.
	/// </summary>
	private static Plan SignInPlan(SteamVdf? known, string login, string? newConfig) {
		string? name = known?.Leaf("AccountName") is { Length: > 0 } saved ? saved : login;

		return string.IsNullOrWhiteSpace(name)
			? new Plan(NotRemembered, null, null)
			: new Plan(SignIn, null, name.Trim(), newConfig);
	}

	/// <summary>The outcome as a sentence for the log and the console, in plain words.</summary>
	/// <param name="login">The Steam login name, for the sign-in case (Steam shows it on its sign-in); the account otherwise.</param>
	public static Said Message(string code, string account, string? login = null) => code switch {
		Ok => new Said("signing your Steam app into {0} - it closes and opens again", account),
		SignIn => new Said("Steam is on the sign-in for {0}: type its password (and Steam Guard code if asked), keep Remember me ticked - next time it's one click", login ?? account),
		Off => new Said("Open in Steam isn't turned on for {0} - turn it on in its settings first", account),
		NotWindows => new Said("Open in Steam only works on Windows for now"),
		NoSteam => new Said("Steam doesn't look installed on this PC, so there's nothing to open"),
		GameRunning => new Said("a game is open in Steam - close it first, then open {0}", account),
		NotRemembered => new Said("sign into {0} once in Steam with \"Remember me\" ticked, then this button works", account),
		SteamBusy => new Said("Steam wouldn't close - if it shows a message box, close it, then try again"),
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

		(string code, string said) = await Windows.SteamClientSwitch.OpenAsync(bot.Cfg.SteamLogin, bot.SteamId, bot.Name).ConfigureAwait(false);
		Opened(bot.Name, code);

		return (code, said);
	}

	/// <summary>
	/// 1.8.2: when the button last left Steam on the sign-in for an account (UTC), so its card can show the Steam Guard code
	/// for a while. Cleared when it signs Steam in by itself.
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> SignInSince = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How long the card offers the code after the button opened the sign-in.</summary>
	public static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(10);

	/// <summary>Note what a press did: the sign-in case starts the card's code window; signing straight in ends it.</summary>
	public static void Opened(string account, string code, DateTime? now = null) {
		if (code == SignIn) {
			SignInSince[account] = now ?? DateTime.UtcNow;
		} else if (code == Ok) {
			SignInSince.TryRemove(account, out _);
		}
	}

	/// <summary>Is Steam (as far as we know) on this account's sign-in: the button opened it in the last ten minutes.</summary>
	public static bool OnSignIn(string account, DateTime? now = null) =>
		SignInSince.TryGetValue(account, out DateTime since) && ((now ?? DateTime.UtcNow) - since < SignInWindow);

	/// <summary>
	/// 1.8.2: the account's Steam Guard code right now and the seconds it has left, from its authenticator in nocat.farm
	/// (maFile or the secret in its settings) - the same code the Authenticator page shows. Only the 5-character code ever
	/// leaves here, never a secret. Null when nocat.farm has no authenticator for it.
	/// </summary>
	public static (string Code, int SecondsLeft)? GuardCode(Bot bot) {
		if (!bot.HasAuthenticator) {
			return null;
		}

		(string? code, int left) = Confirmations.Code(bot);

		return code is { Length: 5 } ? (code, left) : null;
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
			Account: display,
			SignIn: plan.Code is SignIn,
			AsksWhichAccount: ChooserOff(inputs.ConfigText) != null);
	}

	/// <summary>
	/// The Steam-client side of an attempt, as a handful of steps the caller supplies - so the order (close, then back up,
	/// then write, then start) and the "change nothing unless Steam really closed, and unless the backup worked" rule can be
	/// checked with fakes, away from a real PC. Windows fills these in with the real registry, files and process in
	/// <see cref="Windows.SteamClientSwitch"/>.
	/// </summary>
	/// <param name="Running">Is Steam running right now.</param>
	/// <param name="CloseAndWait">Ask Steam to close and wait for it; true when it's gone, false when it wouldn't.</param>
	/// <param name="Backup">Back up the files the plan changes; true on success, false when it couldn't (then change nothing).</param>
	/// <param name="Write">Write the plan's files and registry value.</param>
	/// <param name="Start">Start Steam again.</param>
	/// <param name="Replan">
	/// 1.8.2: read the files again once Steam has closed and decide again - Steam saves its files as it closes, so the
	/// change is made to what it saved, not to what was there before. Null: the first plan stands.
	/// </param>
	public sealed record Ops(Func<bool> Running, Func<Task<bool>> CloseAndWait, Func<Plan, bool> Backup, Action<Plan> Write, Action Start, Func<Plan>? Replan = null);

	/// <summary>
	/// Carry out an OK (or 1.8.2 sign-in) plan in the safe order: close Steam first and wait; only once it has really closed
	/// back the files up; only if that worked write the change; then start Steam. Returns the plan carried out, its code the
	/// outcome - <see cref="SteamBusy"/> if Steam wouldn't close and <see cref="BackupFailed"/> if the backup failed, each
	/// having changed nothing.
	/// </summary>
	public static async Task<Plan> ApplyAsync(Plan plan, Ops ops) {
		if (plan.Code is not (Ok or SignIn)) {
			return plan;
		}

		// Close first. A still-open client would read a half-written file, or overwrite our change when it saves on exit.
		if (ops.Running() && !await ops.CloseAndWait().ConfigureAwait(false)) {
			return plan with { Code = SteamBusy };
		}

		// Steam has closed and saved: the change is worked out again from what it saved.
		if (ops.Replan?.Invoke() is { } again) {
			plan = again;

			if (plan.Code is not (Ok or SignIn)) {
				return plan;
			}
		}

		if (!ops.Backup(plan)) {
			return plan with { Code = BackupFailed };
		}

		ops.Write(plan);
		ops.Start();

		return plan;
	}
}
