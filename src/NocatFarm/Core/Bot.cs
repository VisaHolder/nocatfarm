using System.Text.Json;
using NocatFarm.Config;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace NocatFarm.Core;

public enum BotState { Stopped, Connecting, LoggingIn, NeedsGuard, Online, Reconnecting, Failed }

/// <summary>
/// One Steam account: its connection, its callback pump, its web session, and whatever it is currently "playing".
///
/// Login is password-once: the credential flow hands back a refresh token, that gets stored, and every login
/// afterwards uses it - so Steam Guard is only ever asked for on the very first login of an account.
/// </summary>
public sealed class Bot : IAsyncDisposable {
	private const uint LoginId = 4242;   // lets this coexist with a running Steam client on the same machine
	private const int HeartbeatSeconds = 60;

	private static int ConnectionTimeoutSeconds => Math.Max(5, Live.Global.ConnectionTimeoutSeconds);

	public string Name { get; }
	public BotConfig Cfg { get; private set; }

	public BotState State { get; private set; } = BotState.Stopped;
	public string StatusText { get; private set; } = "stopped";
	public ulong SteamId { get; private set; }
	// Ticks (0 = not signed in): read by every module's thread, and a DateTime? can be read half-written.
	private long _onlineSinceTicks;

	public DateTime? OnlineSince {
		get => FromTicks(Volatile.Read(ref _onlineSinceTicks));
		private set => Volatile.Write(ref _onlineSinceTicks, ToTicks(value));
	}

	/// <summary>How many times it has signed in since nocat.farm started: past the first, a sign-in is one "again".</summary>
	public int SignIns {
		get => Volatile.Read(ref _signIns);
		private set => Volatile.Write(ref _signIns, value);
	}

	private int _signIns;

	public string Playing { get; private set; } = "";

	/// <summary>
	/// The appIDs Steam has actually been told this account is running, right now.
	///
	/// Playing is only a label for the screen. The achievement pacer needs to know what is GENUINELY running,
	/// because it credits a minute of playtime per tick and that playtime is what opens the rarity gate. Reading
	/// the configured idle list instead would hand out hours whenever the card farmer had claimed the account and
	/// those games were not running at all - which opens the gate on time nobody ever spent.
	/// </summary>
	public IReadOnlyList<uint> PlayingApps { get; private set; } = [];

	/// <summary>Set by the card farmer while it owns what the account plays, so the idler keeps its hands off.</summary>
	public bool IsFarming { get; internal set; }

	/// <summary>Set while human mode is driving. The idler stands off completely; the farmer plays one game.</summary>
	public bool HumanOwned { get; internal set; }

	/// <summary>
	/// A temporary "play this, and only this, until then" instruction from the grind command.
	///
	/// Deliberately outranks human mode. Human mode exists to make an account look unattended; grind is the
	/// user saying "I need hours on this game now", which is a decision only they can make. It expires by
	/// itself so a forgotten grind cannot quietly hold an account off its schedule forever.
	/// </summary>
	public uint GrindGame { get; private set; }

	// Kept as ticks (0 = none) so a read on another thread can never see half of a DateTime? being written.
	private long _grindUntilTicks;
	private long _grindStartsAtTicks;

	public DateTime? GrindUntil {
		get => FromTicks(Volatile.Read(ref _grindUntilTicks));
		private set => Volatile.Write(ref _grindUntilTicks, ToTicks(value));
	}

	/// <summary>When the grind game actually goes on. On a legit account this is a short beat after the command,
	/// so it finishes up its current game rather than snapping over instantly; on a non-human account it's now.</summary>
	public DateTime GrindStartsAt {
		get => new(Volatile.Read(ref _grindStartsAtTicks), DateTimeKind.Utc);
		private set => Volatile.Write(ref _grindStartsAtTicks, value.Ticks);
	}

	public bool Grinding => (GrindGame != 0) && (GrindUntil > DateTime.UtcNow);

	private static DateTime? FromTicks(long ticks) => ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

	private static long ToTicks(DateTime? when) => when is { } w ? w.Ticks : 0;

	/// <summary>
	/// Every change to the grind and the drop run - and the file each is saved to - happens under this, so a stop can't
	/// land in the middle of a count or a save: a stopped grind came back at the next start (the save after the stop had
	/// deleted the file wrote it again), an end time moved by the warm-up was overwritten, drops went to the wrong run.
	/// </summary>
	private readonly Lock _grindGate = new();

	/// <summary>
	/// Play one game and nothing else for a while. Returns false, having done nothing, if that game is inside its
	/// refund window - a grind is hours, and hours is exactly what would spend the refund.
	/// </summary>
	/// <summary>
	/// True when the achievement boost started the grind that is running.
	///
	/// Persisted alongside the grind itself. It used to live only in the boost module's memory, so a session
	/// that outlived a restart came back disowned - and switching the boost off then could not end it, because
	/// nothing left in the process knew the boost was what had started it.
	/// </summary>
	public bool GrindIsBoost { get; internal set; }

	/// <summary>
	/// Card drops still wanted from the grind game - a drop run (the <c>drops</c> command). The card farmer counts them
	/// down and ends the grind once they're in; the grind's own time is only the cap for a game that stops dropping.
	/// 0 for an ordinary grind.
	/// </summary>
	public int GrindDropsLeft { get; private set; }

	/// <summary>
	/// Which grind is running: a new number every time one starts (or comes back after a restart). What watches a grind -
	/// the card farmer's drop run - keeps the number it started with, so a grind started in its place while it was waiting
	/// on a badge page (the same game or another) is never counted down or ended by the old one.
	/// </summary>
	public long GrindRun { get; private set; }

	/// <summary>The grind game and its run number, read together.</summary>
	public (uint Game, long Run) CurrentGrind {
		get {
			lock (_grindGate) {
				return (GrindGame, GrindRun);
			}
		}
	}

	/// <summary>Knock drops off the running drop run, and keep that across a restart. True when they were counted.</summary>
	/// <param name="app">The game they dropped in, when known: a drop from a game other than the one the run is on
	/// (the run was stopped and another started meanwhile) isn't counted against it.</param>
	/// <param name="run">The <see cref="GrindRun"/> they were counted for, when known: the same, for a new run on the same game.</param>
	public bool CountGrindDrops(int drops, uint app = 0, long run = 0) {
		lock (_grindGate) {
			// Nothing running (stopped while the drops were being counted): nothing to count down, and nothing to save -
			// a save here is what put a stopped grind back on disk.
			if ((GrindGame == 0) || (GrindDropsLeft <= 0) || ((app != 0) && (app != GrindGame)) || ((run != 0) && (run != GrindRun))) {
				return false;
			}

			GrindDropsLeft = Math.Max(0, GrindDropsLeft - drops);
			SaveGrind();

			return true;
		}
	}

	/// <summary>
	/// A grind that hasn't started playing yet (a human-mode account still warming up): its start moves to now and its
	/// end with it, so the hours asked for are hours of play - a 1h grind used to lose its warm-up and play 45 minutes.
	/// </summary>
	public void HoldGrindStart() {
		lock (_grindGate) {
			if ((GrindGame == 0) || (GrindUntil is not { } until) || (DateTime.UtcNow <= GrindStartsAt)) {
				return;
			}

			TimeSpan late = DateTime.UtcNow - GrindStartsAt;
			GrindStartsAt = DateTime.UtcNow;
			GrindUntil = until.Add(late);
			SaveGrind();
		}
	}

	public bool StartGrind(uint app, TimeSpan how, TimeSpan delay = default, bool boost = false, int drops = 0) {
		if (Refunds.Holds(app)) {
			Log.Warn(new Said("not grinding {0} - still refundable (Protect refunds)", GameNames.Of(app)), Name);

			return false;
		}

		lock (_grindGate) {
			DateTime starts = DateTime.UtcNow.Add(delay);
			GrindGame = app;
			GrindStartsAt = starts;
			GrindUntil = starts.Add(how);   // the hours run from when it actually starts, not the command
			GrindIsBoost = boost;
			GrindDropsLeft = Math.Max(0, drops);
			GrindRun++;
			SaveGrind();
		}

		// A grind with no delay should start with NO DELAY.
		//
		// Nothing here launches games directly: a non-human account takes its games from the idler, which
		// re-asserts every four to seven minutes. So "instant" actually meant "some time in the next seven
		// minutes", while the log had already announced the grind as running - which reads as broken, and on a
		// short grind wastes a noticeable slice of it. Human-mode accounts are untouched: they get a deliberate
		// jittered hand-over so the switch doesn't look like a machine, and their own scheduler performs it.
		// The setting, not just the running flag: human mode only claims the account on its first tick, and a grind
		// in the seconds before that would otherwise skip the owner-safety wait - the idler checks the same way.
		if ((delay == TimeSpan.Zero) && !HumanOwned && !Cfg.LegitMode && CanPlay) {
			SetPlaying([app]);
		}

		return true;
	}

	public void StopGrind() {
		lock (_grindGate) {
			ClearGrind();
		}

		// Put the normal games back now. Left to the idler's own schedule, "grind kylro off" answered "back to
		// normal" and the account went on playing the one grind game for up to seven more minutes.
		BotManager.ModuleOf<Modules.Idler>(this)?.Assert();
	}

	/// <summary>
	/// Stop the grind only if it is still run <paramref name="run"/> (<see cref="GrindRun"/>). False, touching nothing,
	/// when another has started in its place - an old drop run finishing must not end the grind somebody started since.
	/// </summary>
	public bool StopGrind(long run) {
		lock (_grindGate) {
			if ((GrindGame == 0) || (GrindRun != run)) {
				return false;
			}

			ClearGrind();
		}

		BotManager.ModuleOf<Modules.Idler>(this)?.Assert();

		return true;
	}

	/// <summary>No grind. Under _grindGate.</summary>
	private void ClearGrind() {
		GrindGame = 0;
		GrindUntil = null;
		GrindIsBoost = false;
		GrindDropsLeft = 0;
		SaveGrind();
	}

	private string GrindPath => Path.Combine(ConfigStore.ConfigDir, "state", $"grind-{Name}.json");

	// ── drops first (the 'drops' command on a human-mode account) ──

	/// <summary>
	/// A drop run on a human-mode account. Where a robot account grinds the game flat out, a human-mode one puts it
	/// at the front of the card queue and plays it in the day's normal sittings - with the main game's share of
	/// them - so breaks, meals, bedtime and days off all stay. It ends once <see cref="DropsFirstWant"/> cards have
	/// dropped, or with 'drops &lt;account&gt; off'. 0 when none is running.
	/// </summary>
	public uint DropsFirstApp { get; private set; }

	public int DropsFirstWant { get; private set; }

	public int DropsFirstGot { get; private set; }

	public bool DropsFirstActive => (DropsFirstApp != 0) && (DropsFirstGot < DropsFirstWant);

	/// <summary>
	/// How card farming is scheduled right now. A drop run needs sittings to farm, so an account set to farm only
	/// at night - or not at all - farms in some of its sittings until the run is done.
	/// </summary>
	public int EffectiveFarmWhen => DropsFirstActive && !global::NocatFarm.Modules.FarmWhen.InSittings(Cfg.FarmCardsWhen) ? global::NocatFarm.Modules.FarmWhen.Mixed : Cfg.FarmCardsWhen;

	public bool EffectiveFarmCards => Cfg.FarmCards || DropsFirstActive;

	public bool StartDropsFirst(uint app, int want) {
		if (Refunds.Holds(app)) {
			Log.Warn(new Said("not putting {0} first - still refundable (Protect refunds)", GameNames.Of(app)), Name);

			return false;
		}

		lock (_grindGate) {
			DropsFirstApp = app;
			DropsFirstWant = Math.Max(1, want);
			DropsFirstGot = 0;
			SaveDropsFirst();
		}

		return true;
	}

	public void StopDropsFirst() {
		lock (_grindGate) {
			StopDropsFirstLocked();
		}
	}

	private void StopDropsFirstLocked() {
		DropsFirstApp = 0;
		DropsFirstWant = 0;
		DropsFirstGot = 0;
		SaveDropsFirst();
	}

	/// <summary>Cards that just dropped in <paramref name="app"/> - counted toward a drop run on it, if there is one.</summary>
	public void CountDropsFirst(uint app, int drops) {
		int want;

		// The check and the count as one step: a stop, or a new run on another game, landing in between had these drops
		// credited to a run that was no longer the one they dropped for.
		lock (_grindGate) {
			if ((drops <= 0) || (app != DropsFirstApp) || !DropsFirstActive) {
				return;
			}

			DropsFirstGot = Math.Min(DropsFirstWant, DropsFirstGot + drops);

			if (DropsFirstActive) {
				SaveDropsFirst();

				return;
			}

			want = DropsFirstWant;
			StopDropsFirstLocked();
		}

		Log.Good(new Said("{0}: all {1} card drop(s) in - back to normal", GameNames.Of(app), want), Name);
	}

	private string DropsFirstPath => Path.Combine(ConfigStore.ConfigDir, "state", $"dropsfirst-{Name}.json");

	private void SaveDropsFirst() {
		try {
			if (!DropsFirstActive) {
				if (File.Exists(DropsFirstPath)) {
					File.Delete(DropsFirstPath);
				}

				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(DropsFirstPath)!);
			AtomicFile.Write(DropsFirstPath, JsonSerializer.Serialize(new DropsFirstSave(DropsFirstApp, DropsFirstWant, DropsFirstGot)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the drop run: {0}", Log.Describe(e)), Name);
		}
	}

	private void LoadDropsFirst() {
		try {
			if (!File.Exists(DropsFirstPath) || (JsonSerializer.Deserialize<DropsFirstSave>(File.ReadAllText(DropsFirstPath)) is not { } saved)) {
				return;
			}

			DropsFirstApp = saved.App;
			DropsFirstWant = saved.Want;
			DropsFirstGot = saved.Got;

			if (DropsFirstActive) {
				Log.Info(new Said("still going for {0}: {1} of {2} card drop(s)", GameNames.Of(DropsFirstApp), DropsFirstGot, DropsFirstWant), Name);
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't resume the drop run: {0}", Log.Describe(e)), Name);
		}
	}

	private sealed record DropsFirstSave(uint App, int Want, int Got);

	/// <summary>Persist the current grind so it survives a restart, a crash, or the owner playing for a while. Under _grindGate.</summary>
	private void SaveGrind() {
		try {
			if ((GrindGame == 0) || (GrindUntil is not { } until)) {
				if (File.Exists(GrindPath)) {
					File.Delete(GrindPath);
				}

				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(GrindPath)!);
			AtomicFile.Write(GrindPath, JsonSerializer.Serialize(new GrindSave(GrindGame, until.Ticks, GrindIsBoost, GrindDropsLeft)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the grind: {0}", Log.Describe(e)), Name);
		}
	}

	/// <summary>Resume a grind that was still running when we last stopped. Expired ones are dropped.</summary>
	private void LoadGrind() {
		try {
			if (!File.Exists(GrindPath)) {
				return;
			}

			GrindSave? saved = JsonSerializer.Deserialize<GrindSave>(File.ReadAllText(GrindPath));

			if (saved == null) {
				return;
			}

			DateTime until = new(saved.UntilTicks, DateTimeKind.Utc);

			if (until <= DateTime.UtcNow) {
				File.Delete(GrindPath);   // it finished while we were off

				return;
			}

			GrindGame = saved.Game;
			GrindUntil = until;
			GrindIsBoost = saved.Boost;
			GrindDropsLeft = Math.Max(0, saved.DropsLeft);
			GrindRun++;
			GrindStartsAt = DateTime.UtcNow;   // resume now - no fresh switch-in delay on a resume
			Log.Info(new Said("resuming the grind of {0} - {1} left", GameNames.Of(GrindGame), Fmt.Hm((int) (until - DateTime.UtcNow).TotalMinutes)), Name);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't resume the grind: {0}", Log.Describe(e)), Name);
		}
	}

	// Boost defaults to false, so a file written by an older build reads back as a manual grind - which is
	// the safe way round: the boost declines to touch it rather than ending something you started by hand.
	private sealed record GrindSave(uint Game, long UntilTicks, bool Boost = false, int DropsLeft = 0);

	/// <summary>
	/// The custom name actually in effect - empty when the feature is switched off.
	///
	/// Six different places used to read Cfg.CustomGameName directly and decide for themselves whether a name
	/// was in play, which is one decision made six times. Two of them are not obvious: the mismatch warning
	/// would have nagged "Steam shows Rust" about a name deliberately turned off, and the card farmer reserves
	/// one of Steam's concurrent-game slots for the shortcut - so with the toggle off but that site unpatched it
	/// would silently farm one game fewer, forever, for no reason anybody could see.
	///
	/// Always empty on a human-mode account. A person plays one real game at a time; a made-up non-Steam name on
	/// top of it is the idler signature human mode exists to avoid, and the health check already scores it that way.
	/// </summary>
	public string CustomName => Cfg.CustomGameNameEnabled && !Cfg.LegitMode ? Cfg.CustomGameName : "";

	// ── things the social module listens for ────────────────────────────────
	/// <summary>Somebody sent this account a friend request. The SteamID64 of whoever it was.</summary>
	public event Action<ulong>? FriendRequest;

	/// <summary>This account was invited to a Steam group. The group's SteamID64.</summary>
	public event Action<ulong>? ClanInvite;

	/// <summary>Somebody messaged this account: who, and what they said.</summary>
	public event Action<ulong, string>? ChatMessage;

	/// <summary>The modern chat service handler, used to send friend messages (see SendChatMessage).</summary>
	internal SteamUnifiedMessages? Unified { get; private set; }

	private int? _personaOverride;

	/// <summary>
	/// Temporarily show a different persona than the configured one - invisible overnight, away on a break.
	/// Cleared with <see cref="ClearPersonaOverride"/>, which puts the account's own setting back.
	/// </summary>
	/// <summary>
	/// Appearing offline WITHOUT actually going offline.
	///
	/// Steam has two states that look identical on somebody else's friends list and are nothing alike from the
	/// inside. Offline (0) genuinely disconnects: no chat, no messages, no notifications. Invisible (7) shows
	/// the same grey "offline" to everyone while the account stays fully connected and can still be talked to.
	///
	/// Every place this program wants an account to LOOK offline wants invisible. Two of them said so in their
	/// comments - "it stays connected and simply stops being visible" - and then passed 0 anyway.
	/// </summary>
	/// <summary>
	/// How this account disappears while it carries on working - farming, idling, banking hours unseen.
	///
	/// Invisible (7), not Offline (0). Friends see the same thing either way, but the account's owner does not:
	/// Invisible hides him while leaving him able to read and send messages, whereas Offline genuinely cuts the
	/// account out of chat. On an account somebody signs into himself, that difference is the whole point - he
	/// asked to be hidden, not disconnected.
	///
	/// Neither value was ever what threw him off his friends list; a session that announces NO persona at all
	/// takes the account offline underneath everyone, and that is what was happening. See the logon.
	/// </summary>
	public const int PersonaDark = 7;

	/// <summary>
	/// The device name this session presents to Steam, for both the auth request and the logon itself.
	///
	/// Defaults to the PC's own name rather than nothing. Steam shows this in Settings > Security > Authorised
	/// Devices, so an account with several sessions on it is a list a person has to make sense of - and a blank
	/// entry there tells them nothing at all. A session that identifies itself the way every other client does is the
	/// one least likely to be mistaken for something that needs displacing.
	///
	/// Both places get the SAME value. Handing Steam one name while authorising and a different one while logging on
	/// describes two devices, and there is only ever one.
	/// </summary>
	public string DeviceName => string.IsNullOrWhiteSpace(Cfg.MachineName) ? Environment.MachineName : Cfg.MachineName;

	public void SetPersonaOverride(int state) {
		if (_personaOverride == state) {
			return;
		}

		_personaOverride = state;

		// An account its owner signs into always shows plain Online whatever the override says, so re-sending
		// it changes nothing we want and undoes whatever he picked in his own client.
		if (!Cfg.IUseThisAccount) {
			ApplyPersona();
		}
	}

	public void ClearPersonaOverride() {
		if (_personaOverride == null) {
			return;
		}

		_personaOverride = null;

		// An account its owner signs into always shows plain Online whatever the override says, so re-sending
		// it changes nothing we want and undoes whatever he picked in his own client.
		if (!Cfg.IUseThisAccount) {
			ApplyPersona();
		}
	}

	/// <summary>
	/// What this account looks like to your friends list right now: the override if something has taken it over
	/// (invisible overnight, away on a break), otherwise the configured status.
	///
	/// This is worth surfacing because it is the one thing you cannot check from inside the app - "it says it is
	/// offline idling" and "my friends can see it playing" were the same screen for far too long.
	/// </summary>
	public int EffectivePersona => State != BotState.Online ? 0 : Cfg.IUseThisAccount ? 1 : _personaOverride ?? Cfg.OnlineStatus;

	/// <summary>
	/// The persona Steam last reported for this account, which is not always the one we asked for.
	///
	/// A Steam persona belongs to the ACCOUNT, not to a session. Sign in to the same account from your own Steam
	/// client and there are two writers; the last one wins, and it is usually the real client. Reporting our own
	/// request as if it were the truth meant the board could say "invisible" while the friends list said online -
	/// which is the one thing this readout exists to stop.
	/// </summary>
	public int? PersonaAsSeen { get; private set; }

	/// <summary>The account's Steam name and full-size avatar, as Steam tells its friends - for the Discord card.</summary>
	public string SteamName { get; private set; } = "";

	public string AvatarUrl { get; private set; } = "";

	/// <summary>
	/// What the friends list shows. This is the persona WE set, deliberately.
	///
	/// Reading it back off Steam's echo instead was tried and was worse: the first persona callback after logon
	/// reports the pre-login state, and Steam only pushes another when something genuinely changes - so an
	/// account that was quietly online sat there being reported as "offline" from one stale callback, and the
	/// "another client is setting this" check built on it fired on the wrong account entirely. We are the client
	/// setting the persona; what we set is the honest answer, and the echo is kept only for diagnostics.
	/// </summary>
	public string PersonaWord => Word(EffectivePersona);

	/// <summary>
	/// The persona as a word, in English on purpose - plugins read it as a stable value, and the code tests it
	/// against "online" and "invisible". Anything showing it to a person must wrap it in a Said, or the word
	/// rides untranslated inside a translated sentence: "现在显示为 invisible".
	/// </summary>
	private static string Word(int persona) => persona switch {
		0 => "offline",
		1 => "online",
		2 => "busy",
		3 => "away",
		4 => "snooze",
		5 => "looking to trade",
		6 => "looking to play",
		7 => "invisible",
		_ => "online"
	};

	// ── the mobile authenticator ────────────────────────────────────────────
	/// <summary>The account's authenticator secrets: from its config, or from config/authenticators/&lt;bot&gt;.maFile.</summary>
	public (string? Shared, string? Identity) Secrets {
		get {
			if (!string.IsNullOrWhiteSpace(Cfg.SharedSecret) || !string.IsNullOrWhiteSpace(Cfg.IdentitySecret)) {
				return (Cfg.SharedSecret, Cfg.IdentitySecret);
			}

			(string? shared, string? identity, _) = MobileAuth.ReadMaFile(MaFiles.PathFor(Name));

			return (shared, identity);
		}
	}

	/// <summary>True once this account can answer its own Steam Guard prompts without anybody typing anything.</summary>
	public bool HasAuthenticator => !string.IsNullOrWhiteSpace(Secrets.Shared);

	/// <summary>True once it can also clear its own "confirm on your phone" prompts.</summary>
	public bool CanConfirmTrades => !string.IsNullOrWhiteSpace(Secrets.Identity);

	/// <summary>
	/// Clear the mobile confirmation sitting on a trade offer this account just accepted.
	///
	/// Steam does not let you confirm one thing by id directly - you fetch the list of everything pending, find
	/// the entry whose creator is this offer, and confirm that. So this reads the list, matches on the offer id,
	/// and acts on nothing else. An offer somebody else is waiting on is never touched by accident.
	/// </summary>
	/// <summary>
	/// The line for a confirmation just made - named for what it was. The seller confirms its market listings through
	/// here too, and each one went down as "confirmed trade offer 5123..." with the listing's number.
	/// </summary>
	internal static Said Confirmed(Confirmations.Item match) => match.Type == Confirmations.MarketListing
		? new Said("confirmed market listing {0} with its authenticator", match.CreatorId)
		: new Said("confirmed trade offer {0} with its authenticator", match.CreatorId);

	public async Task<bool> ConfirmMobileAsync(ulong tradeOfferId, bool accept, CancellationToken ct = default) {
		if (!CanConfirmTrades || (SteamId == 0) || !Web.Ready) {
			return false;
		}

		try {
			// Nobody confirms on their phone in the same second they accepted.
			if (Cfg.LegitMode) {
				await Task.Delay(Rng.Seconds(8, 60), ct).ConfigureAwait(false);
			}

			// Steam can take a few seconds to list a confirmation it has only just created, so an empty first look
			// is asked again rather than reported as "confirm it on your phone".
			Confirmations.Item? match = null;

			for (int attempt = 0; (attempt < 3) && (match == null); attempt++) {
				if (attempt > 0) {
					await Task.Delay(Rng.Seconds(3, 6), ct).ConfigureAwait(false);
				}

				(bool ok, _, List<Confirmations.Item> items) = await Confirmations.ListAsync(this, ct, fresh: true).ConfigureAwait(false);

				if (ok) {
					match = items.FirstOrDefault(i => i.CreatorId == tradeOfferId);
				}
			}

			if (match == null) {
				return false;   // nothing pending for this offer - it may already have gone through
			}

			if (await Confirmations.ActAsync(this, [match], accept, ct).ConfigureAwait(false)) {
				Log.Good(Confirmed(match), Name);

				return true;
			}

			return false;
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't confirm trade offer #{0}: {1}", tradeOfferId, Log.Describe(e)), Name);

			return false;
		}
	}

	/// <summary>
	/// Logged in but deliberately doing nothing. Every module checks this, so pausing is one flag rather than
	/// three separate stop switches that can disagree with each other.
	/// </summary>
	public bool Paused { get; private set; }

	/// <summary>When a timed pause lifts by itself. Null for an ordinary pause, which lasts until "resume".</summary>
	public DateTime? PausedUntil { get; private set; }

	public void Pause(TimeSpan? duration = null) {
		// The flag and the clear as one step under SetPlaying's lock: a SetPlaying already past its "paused" check can't
		// land its games after this cleared them.
		lock (_playGate) {
			Paused = true;
			PausedUntil = duration is { } d && (d > TimeSpan.Zero) ? DateTime.UtcNow + d : null;
			IsFarming = false;
			StopPlaying();
		}

		if (PausedUntil is { } until) {
			Log.Info(new Said("paused for {0} - picks back up by itself at {1}", Fmt.Hm((int) Math.Ceiling(duration!.Value.TotalMinutes)),
				(Func<string>) (() => Fmt.Clock(until))), Name);
		} else {
			Log.Info("paused - staying logged in, doing nothing", Name);
		}
	}

	public void Resume() {
		if (!Paused) {
			return;
		}

		Paused = false;
		PausedUntil = null;
		Log.Info("resumed", Name);

		// Pick straight back up instead of waiting for the idler's next scheduled re-assert, which is minutes
		// away. "resumed" followed by an account sitting there playing nothing reads as a fault, and for the
		// length of that gap it genuinely is one - the account really is banking no time.
		//
		// The idler stands off by itself when human mode or the card farmer owns the account, so this is safe to
		// call unconditionally; whoever actually owns it will assert its own choice on its next tick.
		foreach (IBotModule module in _modules) {
			if (module is Modules.Idler idler) {
				idler.Assert();

				break;
			}
		}
	}

	public int CardsRemaining { get; internal set; }
	public int GamesRemaining { get; internal set; }

	/// <summary>When the card farmer last read the badge pages - until then, CardsRemaining is 0 because nobody has looked.</summary>
	public DateTime? CardsCheckedAt { get; internal set; }

	/// <summary>Steam says this account can't play right now - the human is using it, or the library is locked.</summary>
	public bool PlayingBlocked { get; private set; }

	/// <summary>
	/// The game another session on this account is running - you, on your own PC - as Steam last said; 0 when none.
	/// Kept whether or not the account stands down for it, so human mode can learn when and what you play.
	/// </summary>
	public uint OtherSessionApp { get; private set; }

	public bool IsOnline => State == BotState.Online;

	/// <summary>Online, not paused, not standing down for the human, and past the courtesy delay.</summary>
	public bool CanPlay => IsOnline && !Paused && !PlayingBlocked && (DateTime.UtcNow >= _resumeAt);

	/// <summary>
	/// You've stopped playing (Steam freed the session) but the courtesy delay before we pick back up hasn't
	/// elapsed yet. In this window the account is NOT yours anymore, so the status must not still say "you're
	/// playing on this account" - that was the line that read as a contradiction right after "free again".
	/// </summary>
	public bool InResumeGrace => IsOnline && !Paused && !PlayingBlocked && (DateTime.UtcNow < _resumeAt);

	/// <summary>When the courtesy delay after you stop playing runs out - the "picking back up in 14m" the log said.</summary>
	public DateTime ResumesAt => _resumeAt;

	/// <summary>
	/// Does this account already have that package? Stops free-game claiming wasting an activation. Only its OWN
	/// licences count: Steam Families put the family's licences in the list too, and taking a borrowed free game
	/// for real is exactly the point.
	/// </summary>
	public bool OwnsPackage(uint packageId) {
		lock (_licenses) {
			return _licenses.TryGetValue(packageId, out Licence license) && license.Own;
		}
	}

	private readonly Dictionary<uint, Licence> _licenses = [];
	private Dictionary<uint, AppOwnership>? _appOwnedSince;
	private int _licenseGeneration;
	private long _licenseStamp;
	private DateTime _resumeAt = DateTime.MinValue;

	internal SteamClient Client { get; }
	internal SteamUser? User { get; private set; }
	internal SteamApps? Apps { get; private set; }
	internal SteamFriends? Friends { get; private set; }
	internal NocatHandler? Notifications { get; private set; }

	/// <summary>Achievement reads and writes. Its two Steam messages are not in SteamKit, so we send them.</summary>
	internal UserStatsHandler? Stats { get; private set; }
	internal WebSession Web { get; }

	/// <summary>Everything this account can launch, with playtime - owned, and borrowed from a Steam Family.</summary>
	public Library Library { get; }

	/// <summary>Games that must not be played yet because doing so would cost a refund.</summary>
	public RefundGuard Refunds { get; }

	/// <summary>What this account's inventory would fetch on the market, by game.</summary>
	public InventoryValue Inventory { get; }

	private readonly CallbackManager _cb;
	private readonly List<IBotModule> _modules = [];
	private readonly SemaphoreSlim _tokenLock = new(1, 1);
	private readonly SemaphoreSlim _stopGate = new(1, 1);
	private readonly SemaphoreSlim _startGate = new(1, 1);
	private int _loginFailures;

	private CancellationTokenSource? _cts;

	// The live run's token, kept apart from its source: a stop disposes the source, and reading .Token off a disposed one
	// throws - the reconnect path read it again after every wait. A stopped run's token stays cancelled, which is the point.
	private CancellationToken _runToken = new(true);
	private Task? _pump;
	private Timer? _heartbeat;
	private volatile bool _running;

	/// <summary>Started and not stopped - signed in, signing in, reconnecting or sitting out a cooldown alike.</summary>
	public bool Running => _running;

	/// <summary>
	/// Failed and not going to try again by itself: a sign-in that gave up (three refusals, no password, a QR code
	/// nobody scanned), a disabled account, a start that broke. A failed sign-in that's about to retry reads Failed
	/// for a moment too, but it's still running - that one isn't finished.
	/// </summary>
	public bool GaveUp => (State == BotState.Failed) && !_running;

	// Which run of this account is the live one (one per start), and which run opened the connection Steam last
	// reported on. A stop disconnects and SteamKit queues the "disconnected" for the callback pump - but the stop also
	// ends that pump, so the message could wait there and be read by the NEXT run's pump, which took it for its own
	// dropped connection: a moment of "reconnecting" and a login slot spent for nothing. A disconnect from a connection
	// an earlier run opened belonged to that run, and it's over.
	private long _session;
	private long _connectedSession;
	private string? _guardPrompt;
	private string? _refreshToken;

	// The web access token, cached and reused for its full life rather than re-minted on every connect. See
	// GetAccessTokenAsync and TokenStore for why that distinction is the whole ballgame for Friends & Chat.
	private string? _accessToken;
	private DateTime? _accessTokenValidUntil;
	private DateTime _lastRemint = DateTime.MinValue;

	// The last web-token mint that failed, and the sign-in it was for - see GetAccessTokenAsync and MintRetryAt.
	private DateTime? _mintFailedAt;
	private long _mintFailedGeneration;

	// Guards the two tokens and their files, and counts every time a sign-in (or a rejected one) replaced them. See
	// AdoptMinted for the race it closes.
	private readonly Lock _tokenSync = new();
	private long _tokenGeneration;

	// Last time the schedule's persona was re-asserted, so the heartbeat can keep it true without spamming.
	private DateTime _lastPersonaAssert;
	private DateTime _nextPersonaAssert;
	private DateTime _lastNameHeal;

	// The one re-send allowed while something else holds the persona has been spent. Cleared when it is ours again.
	private bool _contestRetried;

	// Last appear-as status and displayed game actually announced, so a CHANGE can be logged (and only a change -
	// the re-asserts that keep them steady stay silent). -1 / null means nothing announced yet this run.
	private int _lastLoggedPersona = -1;
	private string? _lastLoggedPlaying;

	/// <summary>Stamp connection liveness. Called by the network tap on every incoming packet.</summary>
	internal void NoteIncomingPacket() => _lastPacket = DateTime.UtcNow;

	/// <summary>When Steam last sent this session anything at all (UTC) - proof the connection is alive since then.</summary>
	internal DateTime LastInbound => _lastPacket;

	private string? _password;
	private int _loggingIn;

	/// <summary>Steam said no to the sign-in itself - the password, the Steam Guard code, a locked account - as opposed to
	/// the connection failing on the way.</summary>
	internal static bool IsSignInRefusal(Exception e) => e is AuthenticationException { Result: EResult.InvalidPassword or EResult.InvalidLoginAuthCode
		or EResult.ExpiredLoginAuthCode or EResult.TwoFactorCodeMismatch or EResult.AccountLogonDenied or EResult.AccessDenied or EResult.AccountDisabled
		or EResult.AccountLockedDown or EResult.AccountNotFound };

	/// <summary>Consecutive failed reconnects, so the wait can grow during Steam's weekly restart.</summary>
	private int _reconnectAttempts;

	/// <summary>When an unconfirmed stand-down should be announced, if it is still in force by then.</summary>
	private DateTime? _blockWarnDue;
	private EResult _lastLogOnResult = EResult.Invalid;
	private DateTime _lastPacket = DateTime.UtcNow;

	// Card drops arrive as a push from Steam. The farmer waits on this instead of re-scraping on a timer, so a
	// drop is noticed within a second or two rather than up to fifteen minutes later.
	private TaskCompletionSource<bool> _itemDrop = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _dropPending;

	/// <summary>When this sign-in asked Steam for its standing new-item count (ticks), until that answer has come in; 0 after.</summary>
	private long _itemCountAskedTicks;
	private uint _knownComments;
	private bool _commentBaselineSet;

	private int _tradeOffersWaiting = -1;
	private TaskCompletionSource<bool> _tradeOffer = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _tradeOfferPending;

	public Bot(string name, BotConfig cfg) {
		Name = name;
		Cfg = cfg;
		Paused = cfg.StartPaused;
		Client = new SteamClient(BuildSteamConfiguration(cfg));

		// Always attached: it stamps the last-packet time on every incoming message (connection liveness, see
		// HeartbeatAsync) and, only when NOCATFARM_NETLOG is set, also writes a packet log for diagnostics.
		Client.DebugNetworkListener = new NetLog(this);

		_cb = new CallbackManager(Client);
		Web = new WebSession(this);
		Library = new Library(this);
		Refunds = new RefundGuard(this);
		Inventory = new InventoryValue(this);

		Notifications = new NocatHandler();
		Client.AddHandler(Notifications);

		Stats = new UserStatsHandler();
		Client.AddHandler(Stats);

		// The modern chat service. Friend messages arrive and send through this once the account logs on with
		// NewSteamChat (which it does) - the legacy SteamFriends channel goes silent under it.
		Unified = Client.GetHandler<SteamUnifiedMessages>();

		// Register the CLIENT-side friend-messages service. This is the bit that makes receiving work: incoming
		// messages arrive as a "FriendMessagesClient.IncomingMessage" notification, and SteamKit only dispatches
		// it (raising our ServiceMethodNotification callback below) once that service is created. Without this the
		// callback is subscribed but nothing ever routes to it - the account silently never sees a word sent to it.
		Unified?.CreateService<FriendMessagesClient>();

		// Steam pushes "who in the family is running what" to every member. Without the service registered the
		// notification is never routed, and the hunter would only find out a shared game had been taken back by
		// being silently thrown out of it.
		Unified?.CreateService<FamilyGroupsClient>();

		_cb.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
		_cb.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
		_cb.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
		_cb.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
		_cb.Subscribe<SteamUser.PlayingSessionStateCallback>(OnPlayingSessionState);
		_cb.Subscribe<ItemAnnouncementsCallback>(OnItemAnnouncements);
		_cb.Subscribe<CommentNotificationsCallback>(OnCommentNotifications);
		_cb.Subscribe<TradeOfferNotificationCallback>(OnTradeOfferNotifications);
		_cb.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
		_cb.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
		_cb.Subscribe<SteamUnifiedMessages.ServiceMethodNotification<CFriendMessages_IncomingMessage_Notification>>(OnIncomingMessage);
		_cb.Subscribe<SteamUnifiedMessages.ServiceMethodNotification<CFamilyGroupsClient_NotifyRunningApps_Notification>>(OnFamilyRunningApps);
		_cb.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
		_cb.Subscribe<SteamUser.WalletInfoCallback>(OnWalletInfo);
		_cb.Subscribe<GiftNotificationCallback>(OnGiftNotification);
		_cb.Subscribe<SteamApps.GuestPassListCallback>(OnGuestPassList);
	}

	/// <summary>Raised when Steam says gifts or guest passes are waiting. The gifts module does the accepting.</summary>
	public event Action? GiftsWaiting;

	/// <summary>How many gifts Steam last said were waiting. -1 until it has said.</summary>
	public int GiftsWaitingCount { get; private set; } = -1;

	/// <summary>Guest passes Steam last said this account can redeem.</summary>
	public IReadOnlyList<ulong> GuestPasses { get; private set; } = [];

	/// <summary>The package on each waiting guest pass (0 when Steam didn't say) - a trial, or a gifted game.</summary>
	public IReadOnlyDictionary<ulong, uint> GuestPassPackages { get; private set; } = new Dictionary<ulong, uint>();

	private void OnGiftNotification(GiftNotificationCallback cb) {
		int previous = GiftsWaitingCount;
		GiftsWaitingCount = (int) cb.Waiting;

		if ((cb.Waiting > 0) && ((int) cb.Waiting != previous)) {
			Log.Debug(new Said("Steam says {0} gift(s) are waiting", cb.Waiting), Name);
			GiftsWaiting?.Invoke();
		}
	}

	/// <summary>
	/// Everything Steam said about a waiting gift or pass, for the log - except its access token, which is the pass's
	/// key and went into the file in full ("AccessToken=6842..."). Kept as "AccessToken=…" so the record still says one was there.
	/// </summary>
	internal static string PassForLog(KeyValue pass) =>
		string.Join(", ", pass.Children.Select(static c => c.Name + "=" + ((c.Name ?? "").Contains("token", StringComparison.OrdinalIgnoreCase) ? "…" : c.Value)));

	private void OnGuestPassList(SteamApps.GuestPassListCallback cb) {
		if (cb.CountGuestPassesToRedeem == 0) {
			GuestPasses = [];

			return;
		}

		GuestPasses = [.. cb.GuestPasses.Select(static p => p["gid"].AsUnsignedLong()).Where(static gid => gid != 0)];

		// A game a friend gifts arrives on this same list as a free-trial guest pass - they are one mechanism on
		// Steam's side. The package on each entry is what tells them apart, so keep it, and write down everything
		// Steam said about each one: that record is how the two kinds get told apart more precisely later.
		Dictionary<ulong, uint> packages = [];

		foreach (KeyValue pass in cb.GuestPasses) {
			ulong gid = pass["gid"].AsUnsignedLong();

			if (gid == 0) {
				continue;
			}

			packages[gid] = pass["packageid"].AsUnsignedInteger();
			Log.Debug(new Said("waiting gift or guest pass: {0}", PassForLog(pass)), Name);
		}

		GuestPassPackages = packages;

		if (GuestPasses.Count > 0) {
			Log.Debug(new Said("Steam says {0} guest pass(es) are waiting", GuestPasses.Count), Name);
			GiftsWaiting?.Invoke();
		}
	}

	/// <summary>Wallet balance in the currency's smallest unit (cents), as Steam last pushed it. Null until it has.</summary>
	public long? WalletCents { get; private set; }

	/// <summary>Part of the balance still pending - a recent purchase refund, a market sale in escrow.</summary>
	public long WalletPendingCents { get; private set; }

	public ECurrencyCode WalletCurrency { get; private set; } = ECurrencyCode.Invalid;

	/// <summary>The country Steam placed this sign-in in ("US", "CA"...) - what the store's own requests send. Empty until signed in.</summary>
	public string Country { get; private set; } = "";

	private void OnWalletInfo(SteamUser.WalletInfoCallback cb) {
		if (!cb.HasWallet) {
			WalletCents = null;

			return;
		}

		WalletCents = cb.LongBalance;
		WalletPendingCents = cb.LongBalanceDelayed;
		WalletCurrency = cb.Currency;
	}

	/// <summary>The account's Steam level, asked of Steam each time. Null if it wouldn't say.</summary>
	public async Task<uint?> GetLevelAsync() {
		if (Unified?.CreateService<Player>() is not { } player) {
			return null;
		}

		try {
			SteamUnifiedMessages.ServiceMethodResponse<CPlayer_GetGameBadgeLevels_Response> answer =
				await player.GetGameBadgeLevels(new CPlayer_GetGameBadgeLevels_Request()).ToTask().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

			if (answer.Result != EResult.OK) {
				Log.Debug($"Steam level refused: {answer.Result}", Name);

				return null;
			}

			return answer.Body.player_level;
		} catch (Exception e) when (e is TimeoutException or TaskCanceledException or AsyncJobFailedException) {
			Log.Failed("couldn't read the Steam level", e, Name);

			return null;
		}
	}

	/// <summary>Steam points available to spend in the Points Shop. Null if Steam wouldn't say.</summary>
	public async Task<long?> GetPointsAsync() {
		if ((SteamId == 0) || (Unified?.CreateService<SteamKit2.WebUI.Internal.LoyaltyRewards>() is not { } loyalty)) {
			return null;
		}

		try {
			SteamUnifiedMessages.ServiceMethodResponse<SteamKit2.WebUI.Internal.CLoyaltyRewards_GetSummary_Response> answer =
				await loyalty.GetSummary(new SteamKit2.WebUI.Internal.CLoyaltyRewards_GetSummary_Request { steamid = SteamId }).ToTask().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

			if (answer.Result != EResult.OK) {
				Log.Debug($"Steam points refused: {answer.Result}", Name);

				return null;
			}

			return answer.Body.summary?.points;
		} catch (Exception e) when (e is TimeoutException or TaskCanceledException or AsyncJobFailedException) {
			Log.Failed("couldn't read the Steam points", e, Name);

			return null;
		}
	}

	/// <summary>Change the name everybody sees on the profile and friends list. Not the custom game name.</summary>
	/// <returns>OK once Steam shows the new name, Pending when it hasn't within ten seconds, or null when the account
	/// isn't signed in. SteamKit sends the change with nothing to wait on, and it used to be taken as done - a name Steam
	/// turned down still came back "profile name is now ...". Steam echoes this account's own persona back to it, so
	/// that echo is the answer.</returns>
	public async Task<EResult?> SetProfileNameAsync(string name) {
		if ((Friends == null) || !IsOnline || string.IsNullOrWhiteSpace(name)) {
			return null;
		}

		string wanted = name.Trim();
		Friends.SetPersonaName(wanted);

		for (int i = 0; (i < 20) && !string.Equals(SteamName, wanted, StringComparison.Ordinal); i++) {
			await Task.Delay(500).ConfigureAwait(false);
		}

		if (string.Equals(SteamName, wanted, StringComparison.Ordinal)) {
			return EResult.OK;
		}

		Log.Debug($"Steam hasn't shown the new profile name yet - still \"{SteamName}\"", Name);

		return EResult.Pending;
	}

	/// <summary>
	/// Steam echoes this account's own persona back to it, and what it says is exactly what everybody else sees.
	///
	/// That is worth listening to, because up to now the dashboard showed what we ASKED for. When Steam decides
	/// to show something else - and with a custom game name plus a list of real games, it sometimes does - the
	/// app would happily report the custom name while the friends list showed a real game, and there was no way
	/// to tell from inside. Now the two are separate values and a mismatch is visible instead of invisible.
	/// </summary>
	/// <summary>A family member started or stopped a shared game. Hand the whole picture to the library.</summary>
	private void OnFamilyRunningApps(SteamUnifiedMessages.ServiceMethodNotification<CFamilyGroupsClient_NotifyRunningApps_Notification> cb) {
		try {
			Library.NoteFamilyRunning(cb.Body.running_apps
				.Select(static a => (a.appid, a.playing_members.Select(static m => m.member_steamid))));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the family's running games: {0}", Log.Describe(e)), Name);
		}
	}

	private void OnPersonaState(SteamFriends.PersonaStateCallback cb) {
		if ((SteamId == 0) || (cb.FriendID.ConvertToUInt64() != SteamId)) {
			return;   // somebody else on the friends list
		}

		// Steam's own word for the persona, which beats ours whenever another session is also setting it.
		PersonaAsSeen = (int) cb.State;

		if (!string.IsNullOrWhiteSpace(cb.Name)) {
			SteamName = cb.Name;
		}

		// An all-zero hash is Steam's "no avatar" - its default question-mark picture.
		if (cb.AvatarHash is { Length: > 0 } hash) {
			string hex = hash.All(static x => x == 0) ? "fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb" : Convert.ToHexString(hash).ToLowerInvariant();
			AvatarUrl = $"https://avatars.fastly.steamstatic.com/{hex}_full.jpg";
		}

		// And its own word for the DEVICE flags, which is the only way to know the Deck badge was accepted.
		//
		// We can set persona_state_flags to anything; whether Steam keeps them is another matter, and the badge
		// renders on other people's screens, not ours. This is the echo - what Steam is telling the friends list
		// about us - so a Deck that did not take shows up here rather than only being visible to somebody else.
		int flags = (int) cb.StateFlags;

		if (flags != _flagsAsSeen) {
			_flagsAsSeen = flags;

			if (Cfg.GameDevice > 0) {
				Log.Debug(new Said("Steam reports device flags {0}", flags)
					+ (flags == Cfg.GameDevice ? $" - matches the {DeviceLabel(Cfg.GameDevice)} we asked for"
						: $" - we asked for {Cfg.GameDevice} ({DeviceLabel(Cfg.GameDevice)})"),
					Name);
			}
		}

		// Same treatment the custom-name check gets: a disagreement has to PERSIST before it counts.
		//
		// The first echo after logon reports the pre-login state, so acting on a single reading is how the
		// earlier attempt at this ended up reporting the wrong thing on the wrong account. Ninety seconds is
		// long enough that only a real second writer survives it - your own Steam client, signed into the same
		// account, quietly winning every persona fight while this program reported what it had asked for.
		//
		// And that first echo is not counted as a fight at all. It describes the session before this one, so on an
		// account that signs in invisible it reliably said "something else is setting this account's persona -
		// backing off", followed a second later by "the persona is ours again": a fight with nobody, in the log after
		// every sign-in. A real second writer is still there on the next echo, and is caught then.
		bool firstEcho = !_personaEchoed;
		_personaEchoed = true;

		if (PersonaAsSeen == EffectivePersona) {
			if (PersonaFightSince != null) {
				PersonaFightSince = null;
				_contestRetried = false;
				Log.Debug("the persona is ours again - resuming the periodic re-assert", Name);
			}
		} else if ((PersonaFightSince == null) && !firstEcho) {
			PersonaFightSince = DateTime.UtcNow;

			// Backing off starts NOW, not after the ninety seconds the message waits for. Those ninety seconds
			// exist so a stale first echo cannot produce a wrong headline - they are not a licence to keep
			// signing somebody out of their friends list while we make up our mind.
			Log.Debug(new Said("something else is setting this account's persona (it says {0}, we asked for {1}) - backing off", new Said(Word((int) PersonaAsSeen)), new Said(Word(EffectivePersona))), Name);
		}

		string seen = cb.GameName ?? "";

		if (string.IsNullOrEmpty(seen) && (cb.GameID != 0)) {
			seen = GameNames.Of(cb.GameAppID != 0 ? cb.GameAppID : (uint) (cb.GameID & 0xFFFFFFFF));
		}

		if (seen == PlayingAsSeen) {
			return;
		}

		PlayingAsSeen = seen;

		// Whether the custom name took is only knowable after Steam has settled.
		//
		// The first persona echo lands a second after logon, before the shortcut has been processed, and it
		// reports whichever real game it saw first. Warning on that is crying wolf about something that corrects
		// itself moments later - which is exactly what it did. So the mismatch has to PERSIST to count.
		//
		// Measured against the label actually ANNOUNCED, not the configured name. The card farmer can deliberately
		// announce no label (PlayWhileFarming off); comparing against the setting then called that "slipped", the
		// heal put the name back, the farmer took it off again, and round it went.
		string wanted = _announcedLabel ?? "";
		bool mismatched = !string.IsNullOrWhiteSpace(wanted) && (seen.Length > 0) && (seen != wanted);

		if (!mismatched) {
			MismatchedSince = null;

			return;
		}

		MismatchedSince ??= DateTime.UtcNow;
	}

	/// <summary>
	/// True once Steam has been showing something other than the custom name for long enough to mean it.
	///
	/// The dashboard and the console board both read this rather than the raw comparison, so a two-second blip
	/// at login never gets reported as a problem.
	/// </summary>
	public bool CustomNameNotShowing =>
		(MismatchedSince is { } since) && (DateTime.UtcNow - since > TimeSpan.FromSeconds(90));

	// Ticks (0 = none) behind both: the callback pump writes them while the dashboard and the heartbeat read them, and a
	// DateTime? read on another thread could come back half-written - or null between the check and the .Value.
	private long _mismatchedSinceTicks;
	private long _personaFightSinceTicks;

	private DateTime? MismatchedSince {
		get => FromTicks(Volatile.Read(ref _mismatchedSinceTicks));
		set => Volatile.Write(ref _mismatchedSinceTicks, ToTicks(value));
	}

	private DateTime? PersonaFightSince {
		get => FromTicks(Volatile.Read(ref _personaFightSinceTicks));
		set => Volatile.Write(ref _personaFightSinceTicks, ToTicks(value));
	}

	/// <summary>This session has had its first persona echo - the stale one - so a mismatch from here on is real news.</summary>
	private bool _personaEchoed;

	/// <summary>
	/// Another session is setting this account's persona right now.
	///
	/// Separate from <see cref="PersonaOverridden"/> on purpose: that one waits ninety seconds before SAYING
	/// anything, so a stale echo cannot produce a wrong headline. This one is true immediately, because backing
	/// off has to happen at the first sign - every extra round of the fight is another sign-out.
	/// </summary>
	public bool PersonaContested => PersonaFightSince != null;

	/// <summary>
	/// Something else is setting this account's persona and winning.
	///
	/// In practice that means your own Steam client is signed into the same account. A Steam persona belongs to
	/// the ACCOUNT rather than to a session, and the real client wins - so nocatFarm can ask for invisible all
	/// night and the friends list will still show you online. Worth saying out loud, because the alternative is
	/// a dashboard confidently reporting "invisible" at something anybody can see is not.
	/// </summary>
	public bool PersonaOverridden =>
		(PersonaFightSince is { } since) && (DateTime.UtcNow - since > TimeSpan.FromSeconds(90));

	/// <summary>What the friends list is really showing, when that differs from what we asked for.</summary>
	public string PersonaReallyWord => PersonaAsSeen is { } seen ? Word(seen) : PersonaWord;

	/// <summary>
	/// What Steam says this account is playing, in Steam's own words - the string on your friends list.
	/// <see cref="Playing"/> is what nocatFarm asked for; this is what actually happened.
	/// </summary>
	public string PlayingAsSeen { get; private set; } = "";


	/// <summary>
	/// Steam re-sends the whole friends list on every change, with new entries flagged as incremental. Anything
	/// sitting at RequestRecipient is somebody waiting on this account to say yes.
	/// </summary>
	private void OnFriendsList(SteamFriends.FriendsListCallback cb) {
		foreach (SteamFriends.FriendsListCallback.Friend friend in cb.FriendList) {
			if (friend.Relationship != EFriendRelationship.RequestRecipient) {
				continue;
			}

			if (friend.SteamID.AccountType == EAccountType.Clan) {
				ClanInvite?.Invoke(friend.SteamID.ConvertToUInt64());
			} else {
				FriendRequest?.Invoke(friend.SteamID.ConvertToUInt64());
			}
		}
	}

	private void OnIncomingMessage(SteamUnifiedMessages.ServiceMethodNotification<CFriendMessages_IncomingMessage_Notification> cb) {
		CFriendMessages_IncomingMessage_Notification body = cb.Body;

		// Our own outgoing messages echo back down this same channel; ignore them, plus typing notifications
		// and anything that isn't actually typed words.
		if (body.local_echo || (body.chat_entry_type != (int) EChatEntryType.ChatMsg) || string.IsNullOrWhiteSpace(body.message)) {
			return;
		}

		ChatMessage?.Invoke(body.steamid_friend, body.message);
	}

	/// <summary>
	/// Send a friend chat message over the modern unified service. The legacy SteamFriends.SendChatMessage
	/// stopped being delivered once the account logs on with NewSteamChat, so this is the path that works.
	/// </summary>
	public void SendChatMessage(ulong steamId, string message) {
		if ((steamId == 0) || string.IsNullOrEmpty(message) || (Unified == null)) {
			return;
		}

		CFriendMessages_SendMessage_Request req = new() {
			steamid = steamId,
			chat_entry_type = (int) EChatEntryType.ChatMsg,
			message = message,
		};

		AsyncJob<SteamUnifiedMessages.ServiceMethodResponse<CFriendMessages_SendMessage_Response>> job =
			Unified.SendMessage<CFriendMessages_SendMessage_Request, CFriendMessages_SendMessage_Response>("FriendMessages.SendMessage#1", req);

		// Nobody waits on the answer, so a refused message would otherwise vanish without a trace.
		_ = Task.Run(async () => {
			try {
				SteamUnifiedMessages.ServiceMethodResponse<CFriendMessages_SendMessage_Response> answer = await job.ToTask().ConfigureAwait(false);

				if (answer.Result != EResult.OK) {
					Log.Debug($"chat message to {steamId} refused: {answer.Result}", Name);
				}
			} catch (Exception e) {
				Log.Failed($"chat message to {steamId} not sent", e, Name);
			}
		});
	}

	/// <summary>
	/// Connection settings that can only be chosen up front: which transport to use, how long to wait, and
	/// whether everything goes through a proxy.
	/// </summary>
	internal static SteamConfiguration BuildSteamConfiguration(BotConfig cfg) {
		GlobalConfig g = Live.Global;
		string proxy = string.IsNullOrWhiteSpace(cfg.AccountProxy) ? g.WebProxy : cfg.AccountProxy;

		return SteamConfiguration.Create(builder => {
			builder.WithConnectionTimeout(TimeSpan.FromSeconds(Math.Max(5, g.ConnectionTimeoutSeconds)));

			builder.WithProtocolTypes(g.SteamProtocol switch {
				1 => ProtocolTypes.WebSocket,
				2 => ProtocolTypes.Tcp,
				_ => ProtocolTypes.All
			});

			if (!string.IsNullOrWhiteSpace(proxy)) {
				builder.WithHttpClientFactory(_ => new HttpClient(BuildProxyHandler(cfg), true));
			}
		});
	}

	/// <summary>
	/// The account's own proxy if it has one, otherwise the global one, otherwise a direct connection.
	/// Per-account proxies are the point of proxies here: spreading logins across IPs is what stops one machine
	/// tripping Steam's per-IP rate limit and stops the accounts being trivially linked.
	/// </summary>
	internal static HttpClientHandler BuildProxyHandler(BotConfig? cfg) {
		GlobalConfig g = Live.Global;
		HttpClientHandler handler = new();

		bool own = cfg != null && !string.IsNullOrWhiteSpace(cfg.AccountProxy);
		string address = own ? cfg!.AccountProxy : g.WebProxy;
		string user = own ? cfg!.AccountProxyUsername : g.WebProxyUsername;
		string pass = own ? cfg!.AccountProxyPassword : g.WebProxyPassword;

		if (string.IsNullOrWhiteSpace(address)) {
			return handler;
		}

		try {
			System.Net.WebProxy proxy = new(address);

			if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass)) {
				proxy.Credentials = new System.Net.NetworkCredential(user, pass);
			} else {
				handler.UseDefaultCredentials = true;
			}

			handler.Proxy = proxy;
			handler.UseProxy = true;
		} catch (Exception e) {
			// Without what comes before the last '@': an address that won't parse is often one with a password typed into it -
			// 'user:pass@host:port', no scheme, which the log's scrubber leaves alone.
			string shown = ProxyShown(address) ?? (address.Contains('@', StringComparison.Ordinal) ? "***@" + address[(address.LastIndexOf('@') + 1)..] : address);
			Log.Warn(new Said("bad proxy '{0}' ({1}) - connecting directly", Log.Scrub(shown), Log.Scrub(e.Message)));
		}

		return handler;
	}

	/// <summary>
	/// Whether a proxy address can be used as one - checked when it's set, so one that can't is refused there and then rather
	/// than found at the next sign-in.
	/// </summary>
	internal static bool ProxyReadable(string address) {
		try {
			_ = new System.Net.WebProxy(address);

			return true;
		} catch (Exception e) when (e is UriFormatException or ArgumentException) {
			return false;
		}
	}

	/// <summary>
	/// A proxy address as the log may have it: scheme://host:port, a user name and password typed into it ('user:pass@host')
	/// taken off. Null when it won't read as an address.
	/// </summary>
	internal static string? ProxyShown(string address) {
		try {
			return new System.Net.WebProxy(address.Trim()).Address is { } u ? $"{u.Scheme}://{u.Host}:{u.Port}" : null;
		} catch (Exception e) when (e is UriFormatException or ArgumentException) {
			return null;
		}
	}

	/// <summary>Subscribe to one Steam callback for as long as the returned handle is held.</summary>
	internal IDisposable SubscribeCallback<T>(Action<T> handler) where T : CallbackMsg => _cb.Subscribe(handler);

	public void AddModule(IBotModule m) => _modules.Add(m);
	public IReadOnlyList<IBotModule> Modules => _modules;

	public Lock CfgGate { get; } = new();

	public void Reconfigure(BotConfig cfg) => Cfg = cfg;

	/// <summary>Prompt text when the account is waiting on a Steam Guard code (the web UI surfaces this).</summary>
	public string? GuardPrompt => _guardPrompt;

	// ── lifecycle ───────────────────────────────────────────────────────────
	public async Task StartAsync() {
		// Serialised against itself AND against stop. Without this, two starts arriving together (the tray's
		// "Start all" plus a dashboard click) both passed the _running check during the multi-second teardown
		// below and ended up with two callback pumps racing over one client.
		await _startGate.WaitAsync().ConfigureAwait(false);

		try {
			await StartCoreAsync().ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// Stopped while queueing for a login slot. Normal, and it must not escape - it used to abort
			// StartAllAsync partway through and leave the remaining accounts unstarted.
			State = BotState.Stopped;
			StatusText = "stopped";
		} catch (Exception e) {
			// Undone before it says so: a throw after the account was marked running left it marked running, and
			// every 'start' after that did nothing at all until somebody thought to 'stop' it first.
			await AbandonStartAsync().ConfigureAwait(false);
			State = BotState.Failed;
			StatusText = "couldn't start";
			Log.Error(new Said("couldn't start: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Name);
			Log.StackToFile(e, Name);
		} finally {
			_startGate.Release();
		}
	}

	/// <summary>A start that broke part way: not running, no pump, no token source - so the next 'start' starts.</summary>
	private async Task AbandonStartAsync() {
		await _stopGate.WaitAsync().ConfigureAwait(false);

		try {
			bool wasRunning = _running;
			_running = false;

			try {
				Client.Disconnect();
			} catch {
				// never connected
			}

			if (_cts != null) {
				await _cts.CancelAsync().ConfigureAwait(false);
			}

			if (_pump != null) {
				try {
					await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
				} catch {
					// the pump falls out on its own
				}
			}

			_cts?.Dispose();
			_cts = null;
			_pump = null;
			Log.Debug($"start failed part way (was marked running: {wasRunning}) - cleared, so 'start' works again", Name);
		} finally {
			_stopGate.Release();
		}
	}

	private async Task StartCoreAsync() {
		// Finishing up before it logs off still counts as running, and "start" then did nothing at all: the account
		// logged off a few seconds later and stayed off, while the reply said "signing in". It waits for that stop
		// below instead, then signs back in.
		if (_running && !Stopping) {
			return;
		}

		CancellationToken ct;
		long session;

		// The teardown and the setup as one step under the stop lock. Apart, a 'stop' clicked just after 'start' ran in
		// between: it cleared the running flag before the start set it again, then cancelled the start's wait - so the
		// account read "stopped" while still flagged running, and every later 'start' did nothing. Or it came before
		// the token source existed, cancelled nothing, and the account signed in after being told to stop.
		await _stopGate.WaitAsync().ConfigureAwait(false);

		try {
			// Removed or replaced while this start waited its turn: it stays that way.
			if (Volatile.Read(ref _disposed)) {
				Log.Debug("not starting - this account was removed", Name);

				return;
			}

			// A previous run that ended without StopAsync (a disabled account, a dead login) can still be holding a
			// token source and a callback pump. Tear it down or the second start runs two pumps on one client.
			await StopCoreAsync(false).ConfigureAwait(false);

			// A new run: anything Steam still says about the last one's connection is ignored from here on. And
			// nothing is announced yet - the last run's "disconnected" may never be read, so what it would have
			// cleared is cleared here.
			session = Interlocked.Increment(ref _session);
			ForgetConnection();

			_running = true;

			lock (_grindGate) {
				if (GrindGame == 0) {
					LoadGrind();   // pick a still-running grind back up after a restart or crash
					LoadDropsFirst();
				}
			}
			Paused = Cfg.StartPaused;   // re-applied per start, so 'restart' doesn't quietly un-pause the account
			// And a timed pause from before the restart is over. Left behind, it lifted a "Start paused" account by itself
			// at the old pause's end time.
			PausedUntil = null;
			PlayingBlocked = false;
			OtherSessionApp = 0;
			_resumeAt = DateTime.MinValue;
			_loginFailures = 0;
			_cts = new CancellationTokenSource();
			ct = _cts.Token;   // kept here: a stop from now on disposes the source, and reading its token then throws
			_runToken = ct;
			// Started clean: the loop runs for as long as the account does, and takes nothing along from whatever started it.
			// Started by a command typed at this PC, it carried "at this PC" on into its Steam chat - a master's 'update file'
			// there counted as typed here.
			AsyncFlowControl? clean = ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();

			try {
				_pump = Task.Run(() => Pump(ct), CancellationToken.None);
			} finally {
				clean?.Undo();
			}

			State = BotState.Connecting;
			StatusText = "waiting for a login slot";
		} finally {
			_stopGate.Release();
		}

		// One line: "starting up", with how long it waits for its turn when it has to - a quiet account in line doesn't
		// look stuck, and two lines per account filled a small window.
		bool said = false;

		await Limiters.WaitForLoginSlotAsync(ct, wait => {
			if (wait >= TimeSpan.FromSeconds(3)) {
				int secs = (int) Math.Ceiling(wait.TotalSeconds);
				Log.Info(said ? new Said("still waiting its turn to sign in - about {0}s", secs) : new Said("starting up - signs in in about {0}s (Gap between logins)", secs), Name);
				said = true;
			}
		}).ConfigureAwait(false);

		if (!said) {
			Log.Info("starting up", Name);
		}

		// The check and the connect under the stop lock. Apart, a stop landing between them disconnected nothing (there was
		// nothing yet) and the connect went ahead - the account signed in after being told to stop.
		await _stopGate.WaitAsync().ConfigureAwait(false);

		try {
			if (!_running || (Interlocked.Read(ref _session) != session)) {
				return;
			}

			StatusText = "connecting";
			_lastLogOnResult = EResult.Invalid;
			Interlocked.Exchange(ref _connectedSession, session);   // before Connect: its "disconnected" can come back at once
			Client.Connect();
		} finally {
			_stopGate.Release();
		}
	}

	/// <summary>
	/// A human-mode account finishing up before it logs off (a graceful stop, like the one an update does). Nothing new
	/// starts while this is set - the game it's in carries on, the way a person finishes a match and then quits - and
	/// every place that shows the account says "finishing up" instead of what human mode was about to do next.
	/// </summary>
	public bool Stopping { get; private set; }

	/// <summary>The last time Steam said you started or stopped playing on this account yourself (UTC).</summary>
	public DateTime YouPlayedAt { get; private set; } = DateTime.MinValue;

	/// <summary>
	/// Goes up with every stop asked for (and the dispose), the moment it's asked. Something that stops the account and
	/// means to start it again (the stuck alarm's restart) notes this first and doesn't start it if anybody else stopped
	/// it meanwhile - a 'stop' you gave during that restart used to be undone a second later.
	/// </summary>
	public long StopCount => Interlocked.Read(ref _stopCount);

	private long _stopCount;

	/// <summary>Disposed - removed, or swapped out by a restore. Nothing starts it again after that. Set under _stopGate.</summary>
	public bool Disposed => Volatile.Read(ref _disposed);

	private bool _disposed;

	public async Task StopAsync(bool graceful = false) {
		Interlocked.Increment(ref _stopCount);

		// Two callers arriving together used to double-dispose the token source and throw out of the middle of
		// StopAllAsync, leaving the rest of the accounts running.
		await _stopGate.WaitAsync().ConfigureAwait(false);

		try {
			await StopCoreAsync(graceful).ConfigureAwait(false);
		} finally {
			_stopGate.Release();
		}
	}

	private async Task StopCoreAsync(bool graceful) {
		// A legit account doesn't blink out mid-game the moment you hit stop - a person finishes up and logs
		// off a short, random beat later. Only for a genuine graceful stop of a running human-mode account:
		// never a restart teardown, a shutdown, a non-human account, or one that wasn't even online.
		if (graceful && _running && HumanOwned && IsOnline) {
			int max = Math.Max(0, NocatFarm.Modules.ReactionSpeed.One(Cfg, nameof(BotConfig.LegitStopMaxSeconds)));

			if (max > 0) {
				// Clamped: with the setting at 1 or 2 the three-second floor sat above the ceiling, Rng.Next threw,
				// and the stop - and any self-update waiting on it - failed.
				int lo = Math.Min(max, Math.Max(3, max * 2 / 5));
				int secs = Rng.Next(lo, max + 1);
				// The only StatusText with a value baked into it, so it cannot be looked up at render time like
				// the rest. Translated here instead; Loc.T on an already-translated string returns it untouched.
				StatusText = Loc.T("finishing up - logging off in ~{0}s", secs);
				Stopping = true;
				Log.Info(new Said("stopping - finishing up, logging off in about {0}s", secs), Name);

				try {
					await Task.Delay(TimeSpan.FromSeconds(secs)).ConfigureAwait(false);
				} catch {
					// fall through and log off now
				}
			}
		}

		bool wasPrompting = _guardPrompt != null;

		// Announce the stop only if this session was actually meant to be running. The teardown that StartCoreAsync
		// does before a (re)start reaches here with _running already false, so a routine start/restart doesn't log
		// a phantom "stopped"; a genuine user stop, a shutdown or a config removal does.
		bool wasRunning = _running;

		_running = false;
		Stopping = false;
		State = BotState.Stopped;
		StatusText = "stopped";
		Playing = "";
		IsFarming = false;
		_guardPrompt = null;

		if (wasPrompting) {
			Prompt.Cancel(Name);   // only OUR question - stopping one account must not answer another's prompt
		}

		await StopModulesAsync().ConfigureAwait(false);

		// Only once the modules have stopped: human mode banks the session in flight as it stops, measured from this
		// sign-in - cleared first, it found no sign-in to measure from and every stop lost up to a minute of play.
		OnlineSince = null;

		_heartbeat?.Dispose();
		_heartbeat = null;

		try {
			User?.LogOff();
		} catch {
			// already gone
		}

		try {
			Client.Disconnect();
		} catch {
			// already gone
		}

		if (_cts != null) {
			await _cts.CancelAsync().ConfigureAwait(false);
		}

		if (_pump != null) {
			try {
				await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			} catch {
				// the pump falls out on its own
			}
		}

		_cts?.Dispose();
		_cts = null;
		_pump = null;
		Web.Invalidate();

		if (wasRunning) {
			Log.Info("stopped - logged out", Name);
			Plugins.PluginHost.RaiseOffline(this);
		}
	}

	private async Task StopModulesAsync() {
		foreach (IBotModule m in _modules) {
			try {
				await m.StopAsync().ConfigureAwait(false);
			} catch (Exception e) {
				// a module must never block shutdown
				Log.Failed($"module {m.Name} didn't stop cleanly", e, Name);
			}
		}
	}

	private void Pump(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				_cb.RunWaitCallbacks(TimeSpan.FromSeconds(1));
			} catch (Exception e) {
				if (Log.DebugOnChange($"pump:{Name}", $"callback pump: {Log.Describe(e)}", Name)) {
					Log.StackToFile(e, Name);
				}
			}
		}
	}

	// ── connection ──────────────────────────────────────────────────────────
	/// <summary>
	/// SteamKit hands callbacks to an `async void`, which is the one signature whose exceptions nobody
	/// can catch: an escaped throw here goes to the thread pool and takes the process with it, with no
	/// log line to say why. The body lives in a Task below so it can be wrapped.
	/// </summary>
	private async void OnConnected(SteamClient.ConnectedCallback callback) {
		try {
			await OnConnectedAsync(callback).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Error(new Said("the connect handler failed: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Name);
			Log.StackToFile(e, Name);
		}
	}

	private async Task OnConnectedAsync(SteamClient.ConnectedCallback _) {
		_lastPacket = DateTime.UtcNow;

		// Which run this sign-in is for. It can wait a long time (a password typed, a code scanned); stopped - or stopped and
		// started again - meanwhile, it must neither sign in nor disconnect the newer run's connection when it fails.
		long session = Interlocked.Read(ref _session);
		CancellationToken ct = _runToken;

		User = Client.GetHandler<SteamUser>();
		Apps = Client.GetHandler<SteamApps>();
		Friends = Client.GetHandler<SteamFriends>();

		State = BotState.LoggingIn;
		StatusText = "logging in";

		try {
			await LogInAsync(ct).ConfigureAwait(false);
		} catch (Exception) when (ct.IsCancellationRequested || (Interlocked.Read(ref _session) != session)) {
			// The run it was for is over: nothing to report, and the connection may already be the next run's.
		} catch (Exception e) {
			// Keep the more telling "sign-in failed" the sign-in set when it gave up.
			if (State != BotState.Failed) {
				State = BotState.Failed;
				StatusText = "login failed";
			}

			Log.Error(new Said("login failed: {0}", Log.Scrub(e.Message)), Name);

			try {
				Client.Disconnect();   // OnDisconnected drives the retry
			} catch {
				// already gone
			}
		}
	}

	/// <summary>The link a QR sign-in is waiting on (what the code encodes), or null. The dashboard draws it.</summary>
	public string? QrChallenge { get; private set; }

	/// <summary>Goes up each time Steam hands out a fresh code, so the dashboard knows to redraw it.</summary>
	public int QrVersion { get; private set; }

	/// <summary>
	/// Sign in by QR: Steam gives a code, the owner scans it with the Steam mobile app and approves, and Steam hands
	/// back this account's login token - and its real account name, so nothing has to be typed at all. Steam
	/// refreshes the code now and then; each one is shown as it comes. Nobody scanning within five minutes stops the
	/// account rather than keeping a sign-in open indefinitely.
	/// </summary>
	/// <returns>Whether it signed in.</returns>
	private async Task<bool> SignInWithQrAsync(CancellationToken ct) {
		State = BotState.NeedsGuard;
		_guardPrompt = "scan the QR code with the Steam app";
		StatusText = "waiting for the QR code to be scanned";

		using CancellationTokenSource giveUp = new(TimeSpan.FromMinutes(5));

		// Stopping the account ends the wait too - it used to go on polling Steam for the rest of the five minutes.
		using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(giveUp.Token, ct);

		try {
			QrAuthSession session = await Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails {
				IsPersistentSession = true,
				DeviceFriendlyName = DeviceName
			}).ConfigureAwait(false);

			// Shown on the dashboard only - it's a picture to point a phone at, and Steam swaps it for a fresh one every
			// half minute or so, which drawn into the log was a wall of blocks. Said once here where to find it.
			ShowQr(session.ChallengeURL);
			session.ChallengeURLChanged = () => ShowQr(session.ChallengeURL);
			Log.Attention(new Said("sign-in: scan the QR on the dashboard (Steam app, Steam Guard)"), Name);

			AuthPollResult poll = await session.PollingWaitForResultAsync(waiting.Token).ConfigureAwait(false);
			QrChallenge = null;
			ct.ThrowIfCancellationRequested();   // scanned just as the account was stopped: that run is over

			// The account name comes from Steam - "add <name> qr" never asked for one. Changed and saved under the config
			// lock, like every other edit of an account's settings, so a settings save at the same moment can't lose it.
			lock (CfgGate) {
				BotConfig cfg = Cfg;

				if (!string.IsNullOrEmpty(poll.AccountName) && !string.Equals(cfg.SteamLogin, poll.AccountName, StringComparison.OrdinalIgnoreCase)) {
					cfg.SteamLogin = poll.AccountName;
					ConfigStore.SaveBot(Name, cfg);
				}
			}

			ReplaceTokens(poll.RefreshToken, poll.AccessToken);

			Log.Good(new Said("signed in with the QR code as {0} - saved for next time", poll.AccountName), Name);

			return true;
		} catch (OperationCanceledException) when (giveUp.IsCancellationRequested) {
			QrChallenge = null;
			_running = false;
			State = BotState.Failed;
			StatusText = "QR code not scanned";
			Log.Attention(new Said("QR code not scanned in 5 min - 'start {0}' for a new one", Name), Name);

			return false;
		} catch when (!_running || ct.IsCancellationRequested) {
			// Stopped or removed while the code was up - that ends the sign-in, it isn't a failure.
			QrChallenge = null;

			return false;
		} catch {
			QrChallenge = null;

			throw;   // like a failed password sign-in: the caller logs it and the reconnect tries again
		}
	}

	private void ShowQr(string url) {
		QrChallenge = url;
		QrVersion++;
	}

	private async Task LogInAsync(CancellationToken ct) {
		// Steam can drop an idle pre-login connection (TryAnotherCM) while somebody is still typing a password,
		// which reconnects and would otherwise queue a SECOND prompt behind the first. One attempt at a time.
		if (Interlocked.Exchange(ref _loggingIn, 1) == 1) {
			return;
		}

		try {
			lock (_tokenSync) {
				_refreshToken ??= TokenStore.Load(Name);

				// Pick up a still-valid access token from a previous run, so a restart reuses it instead of minting a
				// new web session on the spot. Nulls out silently if it is already past its life.
				if ((_accessToken == null) && TokenStore.LoadAccess(Name) is { } stored) {
					SetAccessToken(stored);
				}
			}

			// Signed in by scanning a QR code with the Steam app instead - no password at all.
			if (string.IsNullOrEmpty(_refreshToken) && Cfg.SignInWithQr && !await SignInWithQrAsync(ct).ConfigureAwait(false)) {
				return;
			}

			if (string.IsNullOrEmpty(_refreshToken)) {
				// Asked once per run, then kept: a reconnect must not mean re-typing the password.
				_password ??= Cfg.SteamPassword;

				if (string.IsNullOrEmpty(_password)) {
					State = BotState.NeedsGuard;
					_guardPrompt = "password required";
					_password = await Prompt.SecretAsync($"[{Name}] Steam password", Name).ConfigureAwait(false);

					// Stopped (or shutting down) while it asked: the question was cancelled, nobody left it empty.
					if (!_running) {
						return;
					}
				}

				if (string.IsNullOrEmpty(_password)) {
					State = BotState.Failed;
					StatusText = "no password";
					_running = false;   // nothing to retry with - don't spin on reconnects
					Log.Error("no password - set SteamPassword, or type it at the prompt", Name);

					return;
				}

				if (!Client.IsConnected) {
					return;   // the connection died while we were waiting; the reconnect retries with the cached password
				}

				State = BotState.NeedsGuard;
				_guardPrompt = "Steam Guard code required";

				AuthSessionDetails details = new() {
					Username = Cfg.SteamLogin,
					Password = _password,
					IsPersistentSession = true,
					Authenticator = new ConsoleGuard(Name, Secrets.Shared),
					DeviceFriendlyName = DeviceName
				};

				CredentialsAuthSession session;
				AuthPollResult poll;

				try {
					session = await Client.Authentication.BeginAuthSessionViaCredentialsAsync(details).ConfigureAwait(false);
					// The run's token: stopped while it waits on the phone, it stops waiting - and a stopped run's sign-in
					// can't go on to sign in on whatever connection is open by the time it's approved.
					poll = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
				} catch (Exception e) when (!ct.IsCancellationRequested) {
					// A wrong password fails here every time. Retrying it every few seconds forever is both
					// useless and exactly what makes Steam rate-limit the IP, so ask again after a few tries.
					// Only Steam turning the sign-in down counts: a dropped connection or a timeout is no reason to give
					// up on a password that's right - three of them in a bad minute stopped the account for good.
					if (IsSignInRefusal(e) && (++_loginFailures >= 3)) {
						// Clearing _password is not enough on its own: the next attempt reads it straight back out
						// of the config. Stop, and say why, rather than retrying a wrong password every 15 seconds
						// until Steam rate-limits the whole machine.
						_loginFailures = 0;
						_password = null;
						_running = false;
						State = BotState.Failed;
						StatusText = "sign-in failed";
						Log.Attention(new Said("sign-in failed 3 times ({0}) - fix SteamPassword, 'start {1}'", Log.Scrub(e.Message), Name), Name);
					} else {
						Log.Warn(new Said("sign-in attempt failed: {0}", Log.Scrub(e.Message)), Name);
					}

					throw;
				}

				_loginFailures = 0;

				// The login just handed us a web access token as well. Keep it. Using the token that came WITH the
				// session means we never have to mint a separate one, and a separately minted web token is what was
				// evicting the owner's Friends & Chat.
				ReplaceTokens(poll.RefreshToken, poll.AccessToken);

				Log.Good("signed in - login token saved, no password needed again", Name);
			}

			if (!Client.IsConnected || ct.IsCancellationRequested) {
				return;
			}

			State = BotState.LoggingIn;
			StatusText = "logging in";

			// NAMING TRAP: LogOnDetails.AccessToken wants the REFRESH token, not the access token.
			SteamUser.LogOnDetails logon = new() {
				Username = Cfg.SteamLogin,
				AccessToken = _refreshToken,
				LoginID = LoginId,
				MachineName = DeviceName,

				// ── Why these two fields exist ────────────────────────────────────────────────────────────
				//
				// Together they are why signing in here used to sign the owner out of Friends & Chat, every
				// time, within a couple of minutes. It was never the persona: invisible, online and offline all
				// did it, and no amount of care about WHEN the status was set made any difference, because the
				// eviction is caused by the logon itself and happens before a persona is ever sent.
				//
				// ChatMode is the one that matters. It defaults to Default, which means the LEGACY chat
				// protocol. Steam allows an account one chat session, so a second session asking for legacy
				// chat drags the account's chat over to legacy and the real client - which speaks the modern
				// protocol - gets dropped from Friends & Chat while staying signed in to Steam otherwise. That
				// is exactly the symptom, down to the fact that only chat broke. NewSteamChat is what every
				// current client asks for, so asking for it too means there is nothing to arbitrate.
				//
				// UIMode defaults to Unknown (-1), i.e. "I decline to say what I am". Steam is entitled to make
				// its own guess about an unidentified session and we would rather it did not have to. 7 is
				// DesktopUI. Named only from SteamKit 3.4 on - we are on 3.3, where the enum stops at 6 - but
				// the cast serialises as 7 regardless, which is all Steam sees.
				//
				// Verified rather than reasoned: a logon with both of these set, against the same account, did NOT
				// evict the owner's client, even while farming with the owner manually invisible. Ours set neither and
				// evicted every time. Those were the only two differences in the logon.
				ChatMode = SteamUser.ChatMode.NewSteamChat,
				UIMode = (EUIMode) Math.Clamp(Cfg.UIMode, 0, 7),

				// The device badge is decided at LOGON, not by the persona flags alone.
				//
				// "Play as if on a Steam Deck" sent the right persona_state_flags and did nothing, because the
				// session underneath still announced itself as Windows - and Steam will not badge a Windows
				// desktop session as a handheld running SteamOS however the flags are set. A Deck is a Linux
				// machine, so the logon has to say so. This is why the setting appears to do nothing until the
				// account signs in again: the flags can be re-sent at any time, this cannot.
				ShouldRememberPassword = true
			};

			if (DeviceOSType(Cfg.GameDevice) is { } os) {
				logon.ClientOSType = os;
			}

			User!.LogOn(logon);
		} finally {
			_guardPrompt = null;   // whatever happened, nothing is waiting on the operator any more
			Interlocked.Exchange(ref _loggingIn, 0);
		}
	}

	/// <summary>
	/// SteamKit hands callbacks to an `async void`, which is the one signature whose exceptions nobody
	/// can catch: an escaped throw here goes to the thread pool and takes the process with it, with no
	/// log line to say why. The body lives in a Task below so it can be wrapped.
	/// </summary>
	private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
		try {
			await OnLoggedOnAsync(callback).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Error(new Said("the logon handler failed: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Name);
			Log.StackToFile(e, Name);
		}
	}

	/// <summary>Goes up with every logon Steam answers. See OnLoggedOnAsync.</summary>
	private long _logonGen;

	/// <summary>
	/// This logon is still the latest, signed in, on a running account. A logon handler waits (for the web session) part
	/// way through; a reconnect meanwhile brings the next logon, and the older handler then used to carry on as if it were
	/// current - clearing the new one's drop latch and comment baseline, forgetting what it had just announced, sweeping
	/// the notifications a second time.
	/// </summary>
	internal bool LogonStillCurrent(long logon) => _running && (State == BotState.Online) && (Interlocked.Read(ref _logonGen) == logon);

	private async Task OnLoggedOnAsync(SteamUser.LoggedOnCallback cb) {
		long logon = Interlocked.Increment(ref _logonGen);
		_lastPacket = DateTime.UtcNow;
		_lastLogOnResult = cb.Result > EResult.OK ? cb.Result : EResult.Invalid;

		if (!string.IsNullOrEmpty(cb.IPCountryCode)) {
			Country = cb.IPCountryCode;
		}

		if (cb.Result != EResult.OK) {
			if (cb.Result == EResult.TryAnotherCM) {
				// Routine: Steam is asking us to talk to a different server. Not a failure, and not worth a line.
				Log.Debug("Steam asked for a different server - reconnecting", Name);
				StatusText = "reconnecting";

				return;
			}

			// A rejected token means the session was revoked (password change, "sign out everywhere") - not that the
			// account is bad. Drop it so the next attempt asks for the password again.
			if (cb.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired) {
				ReplaceTokens(null, null);   // the whole family is revoked - the cached web token is dead too
				Log.Warn(new Said("login token rejected ({0}) - needs the password again", cb.Result), Name);
			} else if (cb.Result is EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle) {
				Log.Warn("Steam is rate-limiting logins for this account", Name);
			} else {
				Log.Error(new Said("logon failed: {0}", cb.Result), Name);
			}

			State = BotState.Failed;
			StatusText = cb.Result.ToString();

			return;
		}

		SteamId = cb.ClientSteamID?.ConvertToUInt64() ?? 0;
		State = BotState.Online;
		SignIns++;
		OnlineSince = DateTime.UtcNow;
		StatusText = "online";
		_guardPrompt = null;
		Log.Good(new Said("logged on as {0} ({1})", Cfg.SteamLogin, SteamId), Name);
		Plugins.PluginHost.RaiseOnline(this);

		try {
			// ALWAYS, even when the state we want is the one we think we already have.
			//
			// This used to be skipped whenever the wanted state was plain Online, on the reasoning that a fresh
			// session "comes up Online by itself" so saying so again was a wasted packet. That reasoning is
			// wrong, and it is the bug that spent a day throwing this account's owner out of his own Friends &
			// Chat. A SteamKit session does NOT come up Online - it comes up OFFLINE, and stays offline until it
			// is told otherwise. Steam keeps one persona per ACCOUNT, not per session, so an unannounced session
			// does not sit quietly in the corner: it takes the account offline underneath every other session,
			// including the owner's own client, whose friends window then correctly reports "You are currently
			// offline". Nothing was ever evicted, which is why the client's connection log showed one unbroken
			// session across every occurrence and why a whole afternoon of changes to the LOGON did nothing.
			//
			// Announcing the persona on every logon is what a normal client does, and it's what fixed this.
			//
			// A human-mode account just started (nothing set yet) comes up invisible: announced Online, a restart at
			// 3am told the friends list it was up. Human mode shows it once its own day says so - moments later if
			// it's awake.
			if (Cfg.LegitMode && !Cfg.IUseThisAccount && (_personaOverride == null) && NocatFarm.Modules.HumanMode.DarkAtLogon(this)) {
				_personaOverride = PersonaDark;
			}

			ApplyPersona();
		} catch (Exception e) {
			// persona state is cosmetic - never let it stop the login
			Log.Failed("couldn't announce the persona at logon", e, Name);
		}

		// NOT forced. A forced refresh mints a brand-new web token on every single logon, and a new web token is a
		// new web session that shoves the account's own Steam client out of Friends & Chat. Passing false lets an
		// access token that is still good - the one from this very login, or the one a previous run persisted -
		// be reused as-is, so most logons create no new web session at all. That is the difference between coexisting
		// with the owner's client and evicting it.
		if (!await Web.RefreshAsync(false).ConfigureAwait(false)) {
			Log.Warn("no Steam web session yet - farming and comments will retry", Name);
		}

		// That can take a while (a new token, the Family View unlock). Stopped, removed or dropped in the meantime, the
		// heartbeat and every module used to be started anyway - on an account that was no longer running, with
		// nothing left to ever stop them. Or signed in again meanwhile: that logon does all this itself.
		if (!LogonStillCurrent(logon)) {
			return;
		}

		// Steam replays its standing unviewed-item count on request. That is not a drop that just happened, so
		// the latch is cleared here or every login would look like a card landed.
		Interlocked.Exchange(ref _dropPending, 0);
		_commentBaselineSet = false;
		_announcedApps = null;

		// Back to "Steam hasn't said yet". Carrying the old count across a reconnect would let an account skip
		// its one look at the offers page on the strength of an answer from before it dropped off.
		Volatile.Write(ref _tradeOffersWaiting, -1);
		Interlocked.Exchange(ref _tradeOfferPending, 0);

		try {
			// The answer to this arrives after the clear above, and set the latch all over again: an account whose new-items
			// counter was lit had a card "drop" a minute into every sign-in. Marked, so that one answer isn't taken for a drop.
			Interlocked.Exchange(ref _itemCountAskedTicks, DateTime.UtcNow.Ticks);
			Notifications?.RequestItemAnnouncements();
			Notifications?.RequestCommentNotifications();
		} catch (Exception e) {
			// not fatal - the push arrives anyway once something happens
			Log.Failed("couldn't ask Steam for the item and comment counts", e, Name);
		}

		// Sweep from HERE, not from the comment-notification callback. Steam only pushes that callback when it
		// has something to say, so an account with a clean comment counter but a tray full of gifts and friend
		// invites would never have swept at all - the one place the sweep is guaranteed to run is the login it
		// is supposed to run on.
		ClearAllNotifications();

		_reconnectAttempts = 0;   // a successful logon ends the backoff streak

		// Checked again and started under the stop lock. Unlocked, a stop between the check above and here stopped the
		// modules first and these then started them again - on an account that had been stopped, with nothing left to
		// stop them. Starting a module only kicks off its loop, so this holds the lock for a moment.
		await _stopGate.WaitAsync().ConfigureAwait(false);

		try {
			if (!LogonStillCurrent(logon)) {
				return;
			}

			StartHeartbeat();

			foreach (IBotModule m in _modules) {
				try {
					await m.StartAsync().ConfigureAwait(false);
				} catch (Exception e) {
					Log.Warn(new Said("module {0} failed to start: {1}", m.Name, Log.Scrub(e.Message)), Name);
					Log.StackToFile(e, Name);
				}
			}
		} finally {
			_stopGate.Release();
		}

		// Put the custom game name back up immediately on a non-human account, rather than leaving it showing
		// plain "online" (no game) for the idler's settle delay after every reconnect. A boosting account with a
		// custom name should never be seen without it - so re-assert it the instant it's back, not in
		// twenty seconds. Human mode owns its own accounts' timing, so this leaves those alone.
		//
		// Keyed on the CONFIG flag, not the runtime HumanOwned: on the very first logon after start/restart,
		// HumanOwned is still false for a legit account (its module hasn't ticked yet), and asserting here would
		// slam the multi-game idle list on for a beat and grab the session inside the owner-report lag the warm-up
		// gate exists to respect. LegitMode is known immediately, so this never fires on a human account.
		if (!Cfg.LegitMode && !PlayingBlocked && LogonStillCurrent(logon)) {
			BotManager.ModuleOf<Modules.Idler>(this)?.Assert();
		}
	}

	private void OnLoggedOff(SteamUser.LoggedOffCallback cb) {
		_lastLogOnResult = cb.Result > EResult.OK ? cb.Result : EResult.Invalid;

		if (cb.Result == EResult.LoggedInElsewhere) {
			// The owner just started playing on this account, so Steam handed them the session. Expected, not a
			// fault - so it reads as Info, in plain language, and says it will come back. The actual rejoin is
			// kept quiet in OnDisconnected so this is the only line the user sees for a normal step-aside.
			Log.Info("you're on this account - waiting until you're done", Name);

			if (Cfg.PauseWhenYouPlay) {
				PlayingBlocked = true;
			}
		} else {
			Log.Warn(new Said("logged off: {0}", cb.Result), Name);
		}

		State = BotState.Reconnecting;
		StatusText = cb.Result == EResult.LoggedInElsewhere ? "you're playing" : "reconnecting";

		// OnlineSince is left for the disconnect that follows, which clears it once the modules have stopped: human mode
		// banks the session in flight as it stops, measured from this sign-in, and with it cleared here it found nothing
		// to measure from and lost up to a minute of play every time.
	}

	/// <summary>
	/// SteamKit hands callbacks to an `async void`, which is the one signature whose exceptions nobody
	/// can catch: an escaped throw here goes to the thread pool and takes the process with it, with no
	/// log line to say why. The body lives in a Task below so it can be wrapped.
	/// </summary>
	private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
		long session = Interlocked.Read(ref _session);
		long opened = Interlocked.Read(ref _connectedSession);

		// A connection an earlier run opened: that run's stop already cleaned up after it, and this run hasn't
		// connected yet. Taken as ours, it read "reconnecting" and queued a second sign-in behind the real one.
		if (opened != session) {
			Log.Debug($"disconnected - an earlier run's connection (run {opened}, this is run {session}), ignored", Name);

			return;
		}

		// The on-screen line only says "reconnecting"; this is the why, for the file.
		if (_running) {
			Log.Debug($"disconnected - by us: {callback.UserInitiated}, Steam's last word: {_lastLogOnResult}", Name);
		}

		try {
			await OnDisconnectedAsync(session).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Error(new Said("the disconnect handler failed: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Name);
			Log.StackToFile(e, Name);
		}
	}

	/// <summary>What a connection that's gone leaves behind: nothing playing, nothing announced, no web session.</summary>
	private void ForgetConnection() {
		OnlineSince = null;
		Playing = "";
		PlayingApps = [];   // nothing is playing on a session that's gone - or a grind thinks its game is still on
		IsFarming = false;
		Web.Invalidate();

		// Nothing is announced on a socket that no longer exists, and Steam's last echo describes a session that
		// has ended. Forgetting both is what makes the next session announce itself properly instead of deciding
		// it already had - the "same set, Steam agrees" shortcut in SetPlaying leans on these being honest.
		_announcedApps = null;
		_announcedLabel = null;
		PlayingAsSeen = "";
		MismatchedSince = null;
		PersonaFightSince = null;   // the next session's own echo decides whether anybody else is writing it
		_contestRetried = false;
		_personaEchoed = false;      // and its first echo is stale again
	}

	private async Task OnDisconnectedAsync(long session) {
		// Read once. The source behind it is disposed by a stop, and reading .Token after every wait below threw then - or,
		// once a new run had started, handed this old reconnect the NEW run's token.
		CancellationToken ct = _runToken;

		if (Interlocked.Read(ref _session) != session) {
			return;   // a new run started in the moment since the disconnect was read
		}

		_heartbeat?.Dispose();
		_heartbeat = null;

		// Modules first, then the connection is forgotten: human mode banks the session in flight as it stops, measured from
		// this sign-in (OnlineSince) - forgotten first, it lost up to a minute of play on every disconnect.
		await StopModulesAsync().ConfigureAwait(false);
		ForgetConnection();

		if (!_running) {
			// A sign-in that gave up (three wrong passwords, no password, a QR code nobody scanned) stops the account AND
			// says why. Overwritten here, it read as a plain "stopped" and the dashboard filed it under off, not problems.
			if (State != BotState.Failed) {
				State = BotState.Stopped;
				StatusText = "stopped";
			}

			return;
		}

		State = BotState.Reconnecting;
		EResult reason = _lastLogOnResult;
		_lastLogOnResult = EResult.Invalid;

		try {
			switch (reason) {
				case EResult.AccountDisabled:
					// Permanent. Retrying forever would just log the same line every ten seconds.
					State = BotState.Failed;
					StatusText = "account disabled";
					Log.Error("Steam says this account is disabled - not reconnecting", Name);
					_running = false;

					return;
				case EResult.TryAnotherCM: {
					// Steam pointing us at a different server - the logon handler already calls this routine and says
					// "not worth a line". It then fell through to the ordinary backoff, which printed a yellow
					// "disconnected - reconnecting" warning on screen for something that is not a fault at all.
					// Inside the weekly maintenance window it is part of the outage, so it keeps the stretching wait.
					if (SteamMaintenance.LikelyNow) {
						await BackOffAndWaitAsync().ConfigureAwait(false);

						break;
					}

					int idle = Math.Max(1, Live.Global.ReconnectDelaySeconds);
					TimeSpan hop = TimeSpan.FromSeconds(Rng.Next(idle, idle * 2));
					Log.Debug(new Said("moving to another Steam server in ~{0}s", (int) hop.TotalSeconds), Name);
					await Task.Delay(hop, ct).ConfigureAwait(false);

					break;
				}
				case EResult.RateLimitExceeded:
				case EResult.AccountLoginDeniedThrottle:
				case EResult.ServiceUnavailable:
					// ServiceUnavailable is Steam telling the whole IP "too many logins, back off" - the exact
					// throttle a burst of restarts/reconnects trips. Left in the default case it retried every ~15s
					// per account and only dug the hole deeper (and can knock the owner's own client offline). Route
					// it through the SHARED cooldown so every account sits out together and the rate actually drops.
					//
					// It is also the one ambiguous result. Inside the weekly window it means nothing more than
					// "Steam is switched off", and every account gets it at once. Serving a fleet-wide 25m cooldown
					// for a restart that is usually over inside five keeps a perfectly healthy fleet down long after
					// Steam is back - and logs "Steam is rate-limiting logins" when no throttle exists, which is
					// exactly the wrong thing to read at 3am. The other two name the throttle outright, so they
					// are never reinterpreted.
					//
					// AccessDenied is NOT here. The logon handler reads it as a revoked token for this one account and
					// deletes it; treating the same result as a Steam-wide throttle as well sat every other account out
					// for half an hour, with a "rate-limiting" warning, because one account's password was changed.
					if (reason == EResult.ServiceUnavailable && SteamMaintenance.LikelyNow) {
						await BackOffAndWaitAsync().ConfigureAwait(false);

						break;
					}

					StatusText = "rate-limited";
					_reconnectAttempts = 0;   // a cooldown is not a failure streak; don't let backoff compound on top
					await Limiters.ServeLoginCooldownAsync(ct).ConfigureAwait(false);

					break;
				case EResult.LoggedInElsewhere: {
					// The owner is playing on this account - OnLoggedOff already said so in plain words. Rejoin
					// after a short wait and then stand down until they finish. Kept at Debug so the user does NOT
					// see a scary "disconnected - reconnecting" line for something working exactly as intended.
					int idle = Math.Max(1, Live.Global.ReconnectDelaySeconds);
					TimeSpan rejoin = TimeSpan.FromSeconds(Rng.Next(idle, idle * 2));
					StatusText = "you're playing";
					Log.Debug(new Said("rejoining in ~{0}s, then standing down until you're done", (int) rejoin.TotalSeconds), Name);
					await Task.Delay(rejoin, ct).ConfigureAwait(false);

					break;
				}

				default:
					await BackOffAndWaitAsync().ConfigureAwait(false);

					break;
			}

			// Stopped - or stopped and started again - while it waited: the run this reconnect was for is over.
			if (!_running || (Interlocked.Read(ref _session) != session)) {
				return;
			}

			// Never reconnect while a login attempt is still in flight - that's how you end up with two password
			// prompts stacked on top of each other.
			while (Volatile.Read(ref _loggingIn) == 1) {
				await Task.Delay(1000, ct).ConfigureAwait(false);
			}

			await Limiters.WaitForLoginSlotAsync(ct).ConfigureAwait(false);

			// Under the stop lock, like the first connect: a stop between the check and the connect signed a stopped account
			// back in.
			await _stopGate.WaitAsync(ct).ConfigureAwait(false);

			try {
				if (_running && (Interlocked.Read(ref _session) == session) && !Client.IsConnected) {
					Interlocked.Exchange(ref _connectedSession, session);
					Client.Connect();
				}
			} finally {
				_stopGate.Release();
			}
		} catch (OperationCanceledException) {
			// shutting down
		}

		return;

		// Steam restarts everything once a week and every account drops together. Retrying every ten seconds
		// through that achieves nothing but a hundred warnings that read like a fault, so the wait stretches and
		// the line says what is actually happening. The window is never trusted to mean "do not try" - Valve
		// publishes no schedule and it drifts.
		async Task BackOffAndWaitAsync() {
			StatusText = SteamMaintenance.LikelyNow ? "Steam maintenance" : "reconnecting";

			int wait = Math.Max(1, Live.Global.ReconnectDelaySeconds);
			_reconnectAttempts++;
			TimeSpan back = SteamMaintenance.Backoff(_reconnectAttempts, TimeSpan.FromSeconds(Rng.Next(wait, wait * 2)));

			// The first couple of failures are worth seeing; after that the same line every few minutes only buries
			// the log, and the board already says "reconnecting". The trace stays at debug.
			if (SteamMaintenance.LikelyNow || (_reconnectAttempts <= 2)) {
				Log.Warn(SteamMaintenance.Explain(back), Name);
			} else {
				Log.Debug(SteamMaintenance.Explain(back), Name);
			}

			await Task.Delay(back, ct).ConfigureAwait(false);
		}
	}

	// ── heartbeat ───────────────────────────────────────────────────────────
	/// <summary>
	/// A dropped TCP connection doesn't always raise a disconnect - the socket can just go quiet. Poking Steam once
	/// a minute turns that silence into a real reconnect instead of an account that looks online and does nothing.
	/// </summary>
	/// <summary>The longest gap between two persona re-asserts. The liveness check below is measured against it.</summary>
	private const int PersonaAssertMaxSeconds = 600;

	private void StartHeartbeat() {
		_heartbeat?.Dispose();
		_heartbeat = new Timer(_ => _ = HeartbeatSafeAsync(), null, TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
	}

	/// <summary>The timer drops whatever the heartbeat throws without a word; this at least leaves a line behind.</summary>
	private async Task HeartbeatSafeAsync() {
		try {
			await HeartbeatAsync().ConfigureAwait(false);
		} catch (Exception e) {
			// Every minute, so the same failure is written once - with its stack, since nothing in here should throw.
			if (Log.DebugOnChange($"heartbeat:{Name}", $"heartbeat: {Log.Describe(e)}", Name)) {
				Log.StackToFile(e, Name);
			}
		}
	}

	private async Task HeartbeatAsync() {
		if (!_running || State != BotState.Online || SteamId == 0) {
			return;
		}

		// A timed pause lifts itself. Checked here because the heartbeat is the one thing still ticking while an
		// account is paused - every module is standing off.
		if (Paused && (PausedUntil is { } liftAt) && (DateTime.UtcNow >= liftAt)) {
			Resume();
		}

		// A stand-down that was too fresh to trust at logon gets announced here, once, if it held.
		if (PlayingBlocked && _blockWarnDue is { } due && (DateTime.UtcNow >= due)) {
			_blockWarnDue = null;
			Log.Info("you're on this account - waiting until you're done", Name);
		}

		// Re-assert the schedule's persona so it actually holds: online during active hours, invisible while
		// asleep. Steam resets the persona on a game start or a reconnect, and a manual change sticks otherwise,
		// so without this the account drifts and gets left - as it did - showing Invisible in the middle of the
		// day when the schedule wanted it online.
		//
		// This was removed once, on the belief that re-asserting the persona was what signed the owner out of
		// Friends & Chat. That belief was wrong. The eviction came from the friend-data SELF-POLL (see the
		// disabled block below), not from setting a persona - a session can set its persona freely without evicting
		// anyone. Setting our own status is safe; asking the friends service about ourselves was not.
		//
		// Skipped while the owner is actually on the account (PlayingBlocked) - when they are using it, their
		// client owns the status and we do not fight it. Every ~60s is plenty; the heartbeat itself is far
		// tighter, hence the timestamp gate.
		//
		// Never on an account its owner signs into himself. The logon already announced plain Online once, which is
		// all that account needs; re-sending it every few minutes put him back to Online within a minute of picking
		// Invisible or Away in his own client.
		//
		// And not in a loop while something else is winning the persona (PersonaContested). One re-send is allowed,
		// because Steam itself flips the persona when a game starts and that one should be undone; if the other
		// writer still wins after it, we stop until the persona is ours again - that is the "backing off" the log
		// line promises. Like PlayingBlocked, that leaves liveness to inbound traffic alone, and an account with a
		// second writer on it is never quiet.
		bool drifted = (PersonaAsSeen is int seen) && (seen != EffectivePersona) && (DateTime.UtcNow.Subtract(_lastPersonaAssert).TotalSeconds >= 60);
		bool backedOff = Cfg.IUseThisAccount || (PersonaContested && _contestRetried);

		// Backing off still counts as the heartbeat getting here. The keepalive's "gone quiet" test pairs inbound
		// traffic with this stamp, and an account that never re-sends would be called dead after two quiet minutes -
		// the invisible-overnight false reconnect all over again. SteamKit's own heartbeat still catches a dead socket.
		if (backedOff) {
			_lastPersonaAssert = DateTime.UtcNow;
		}

		if (!PlayingBlocked && !backedOff && (drifted || (DateTime.UtcNow >= _nextPersonaAssert))) {
			_contestRetried = PersonaContested;
			_lastPersonaAssert = DateTime.UtcNow;
			_nextPersonaAssert = DateTime.UtcNow + Rng.Seconds(180, PersonaAssertMaxSeconds);

			try {
				ApplyPersona();
				Log.Recovered($"persona:{Name}");
			} catch (Exception e) {
				// cosmetic - never worth disturbing the heartbeat over
				Log.DebugOnChange($"persona:{Name}", $"couldn't re-assert the persona: {Log.Describe(e)}", Name);
			}
		}

		// Heal a custom name that has slipped off the top.
		//
		// Steam shows whichever game started MOST RECENTLY, and it non-deterministically leaves a real game there
		// instead of the shortcut, so the friends list shows "Counter-Strike 2" rather than the custom name. Once
		// that has PERSISTED (CustomNameNotShowing = 90s, so a login blip doesn't count), re-announce with force so
		// the shortcut is the newest thing again. The routine re-assert can't fix this on its own - it only
		// relaunches when the GAME LIST changes, and an idling account's list doesn't, so a slipped name stayed
		// slipped forever. Throttled, and only for an account that should be showing a custom name at all.
		if (CustomNameNotShowing && !PlayingBlocked && !Paused && !HumanOwned
			&& _announcedLabel is { Length: > 0 } announced && !string.IsNullOrWhiteSpace(announced)
			&& (DateTime.UtcNow.Subtract(_lastNameHeal).TotalSeconds >= 120)) {
			_lastNameHeal = DateTime.UtcNow;
			Log.Info(new Said("custom name slipped to {0} - setting it again", PlayingAsSeen), Name);

			try {
				// The same label that was announced - never the configured name over a farmer that chose none.
				SetPlaying(PlayingApps, announced, force: true);
			} catch (Exception e) {
				// next heartbeat tries again
				Log.DebugOnChange($"nameheal:{Name}", $"couldn't set the custom name again: {Log.Describe(e)}", Name);
			}
		}

		// DISABLED - this is what signs the account owner out of Friends & Chat, and it is the last thing left
		// that could.
		//
		// It asked Steam, once a minute, for this account's OWN friend/persona data, to keep the "what your
		// friends see" readout fresh. No real Steam client ever asks the friends service about ITSELF - you are
		// not your own friend. Requesting it from a second session
		// appears to make Steam treat that session as the account's active friends session and drop the real
		// client's, which is the eviction: the CM connection survives (only this friends request is involved),
		// the client's friends websocket goes quiet, and the panel shows "signed out". It was the only periodic,
		// account-specific, friends-subsystem message we sent - proven by packet capture with and without it.
		//
		// The readout it fed is cosmetic. Steam still pushes this account's persona on a real change (game start,
		// status change), and OnPersonaState already handles those, so the readback degrades to "updates when it
		// actually changes" rather than "polled every minute" - a fair price for not booting the owner offline.
		//
		// (Left as a comment rather than deleted so the next person does not helpfully add it back.)
		//   Friends?.RequestFriendInfo(SteamId, PlayerName | GameExtraInfo | Status);   // <- NEVER on our own id

		// Connection liveness, with nothing aimed at the friends service.
		//
		// _lastPacket is stamped by every incoming packet (see NetLog). A VISIBLE account receives server traffic
		// constantly, but an INVISIBLE one does not - Steam pushes an invisible session very little, so inbound can
		// legitimately go quiet for minutes on a perfectly healthy connection. Watching inbound ALONE therefore
		// reconnected the night-idle (invisible) accounts every couple of minutes for nothing - "connection went
		// quiet" over and over on an account that was fine.
		//
		// So liveness is inbound OR our own outbound heartbeat: the persona re-assert just above runs every 60s and
		// only gets there if this session can still reach Steam. Reconnect only when BOTH have been silent past the
		// timeout - a genuinely wedged, half-open socket. A truly dead socket is still caught quickly by SteamKit's own
		// heartbeat, which raises OnDisconnected.
		//
		// This used to PROBE by requesting our own account's profile info and awaiting the reply - a self-directed
		// friends/profile call, the same family of request that signed the owner out of Friends & Chat - so it is
		// gone for good. Watching traffic we already have detects the same dead connection without touching friends.
		// The re-assert runs every 3-10 minutes, not every minute as this was written for - so it only counts as
		// silent once it has missed its longest gap by the timeout. Comparing it to the bare timeout reconnected a
		// quiet (warming-up or invisible) account a few minutes after every sign-in, for nothing.
		if (WentQuiet(DateTime.UtcNow, _lastPacket, _lastPersonaAssert, PlayingBlocked)) {
			Log.Warn("connection went quiet - reconnecting", Name);

			try {
				Client.Disconnect();   // OnDisconnected drives the backoff + reconnect
			} catch {
				// already gone - OnDisconnected picks it up
			}
		}
	}

	/// <summary>
	/// Nothing in from Steam and no re-assert of ours past the timeout - a wedged socket worth reconnecting. Never while
	/// standing aside for you: the re-assert is skipped then, and a session you're playing on gets next to no traffic
	/// of its own - it was taken for dead two minutes after each sign-in and signed back into your account, twice
	/// in three minutes, in the middle of your game. SteamKit's own heartbeat still catches a dead socket then.
	/// </summary>
	internal static bool WentQuiet(DateTime now, DateTime lastInbound, DateTime lastAssert, bool standingAside) =>
		!standingAside
		&& ((now - lastInbound).TotalSeconds >= ConnectionTimeoutSeconds)
		&& ((now - lastAssert).TotalSeconds >= PersonaAssertMaxSeconds + ConnectionTimeoutSeconds);

	/// <summary>What Steam's playing-session report says, for the log - "another session" only when it isn't ours.</summary>
	internal static Said SessionStateLine(bool blocked, uint app) =>
		blocked ? new Said("Steam says: blocked - another session is playing app {0}", app)
		: app != 0 ? new Said("Steam says: not blocked - this session is playing app {0}", app)
		: new Said("Steam says: not blocked - nothing playing");

	// ── pushes ──────────────────────────────────────────────────────────────
	private void OnPlayingSessionState(SteamUser.PlayingSessionStateCallback cb) {
		_lastPacket = DateTime.UtcNow;
		NotePlayingSession(cb.PlayingBlocked, cb.PlayingAppID);
	}

	/// <summary>Steam's playing-session report: stand down for the owner, or stand back up once he's done.</summary>
	internal void NotePlayingSession(bool blocked, uint app) {
		// PlayingAppID is what the account's playing session is running. While blocked that is a session OTHER than
		// this one - Steam allows one playing session per account, so our games-played is accepted and then quietly
		// ignored, which from the inside looks identical to it never having been sent. While not blocked it is our
		// own: the line used to call nocat.farm's own game list "other session playing app 730".
		Log.Debug(SessionStateLine(blocked, app), Name);

		// Before anything below can return early: this is what you're playing, stood down for or not.
		OtherSessionApp = blocked ? app : 0;

		// The opt-out only suppresses standing DOWN. It must never suppress standing back up: doing that once
		// latched the flag on forever, so the setting that promises "don't stand down" was the one that made
		// standing down permanent.
		if (blocked && !Cfg.PauseWhenYouPlay) {
			return;
		}

		// The flag and what it clears as one step, under the lock SetPlaying decides under - so a SetPlaying already past
		// its "you're playing" check finishes first and is cleared here, and one after this sees the flag and sends nothing.
		// The wait before picking back up, rolled before the flag drops - dropped first, CanPlay read "free, no wait" for
		// the moment in between and the idler could jump straight back in.
		int delay = Math.Max(0, Cfg.ResumeDelayMinutes);
		DateTime resumeAt = DateTime.UtcNow + (Cfg.LegitMode && (delay > 0) ? Rng.HumanMinutes(delay, delay * 3) : TimeSpan.FromMinutes(delay));

		lock (_playGate) {
			if (blocked == PlayingBlocked) {
				return;
			}

			if (blocked) {
				PlayingBlocked = true;
				Playing = "";
				IsFarming = false;

				// Steam has taken our games off while you play, so nothing of ours is running. Left set, the lifetime total,
				// the history charts and the daily report went on counting your own play as the account's farming, and the
				// next announce once you'd finished was taken for a repeat rather than a fresh start.
				PlayingApps = [];
				_announcedApps = null;
				_announcedLabel = null;

				// And whatever SetPlaying left waiting to finish (its second half, a persona re-send) is superseded.
				Interlocked.Increment(ref _playSequence);
			} else {
				// A courtesy pause: coming straight back the instant Steam frees the session is what makes an idler
				// feel like it's fighting you for your own account.
				// Human mode takes its time about it - somewhere from the delay to three times it, not the same minute count every time.
				_resumeAt = resumeAt;
				PlayingBlocked = false;
			}
		}

		YouPlayedAt = DateTime.UtcNow;   // started or stopped: either way you were on it just now

		if (blocked) {
			// Don't cry wolf on the report that arrives WITH the logon.
			//
			// Steam's first PlayingSessionState after signing in still describes the session that just ENDED -
			// which, on a restart, is this program's own. The result was "you're using this account - standing
			// down" in the same second as every single logon, on an account nobody had touched. Three restarts
			// in a row in the log, three identical false alarms, is what gave it away.
			//
			// The FLAG is still set immediately, because standing down wrongly for a few seconds costs nothing
			// and the safety gate needs 180 seconds before anything could start anyway. Only the ANNOUNCEMENT
			// is held back - and held back, not dropped: this handler only runs on a change, so simply skipping
			// the line would mean a real stand-down right after logon was never reported at all. The heartbeat
			// says it a moment later if it turns out to be true.
			bool freshLogon = OnlineSince is { } since && (DateTime.UtcNow - since < TimeSpan.FromSeconds(20));

			if (freshLogon) {
				_blockWarnDue = DateTime.UtcNow.AddSeconds(25);
				Log.Debug(new Said("Steam reports another session on app {0} right after logon - probably our own, waiting before saying so", app), Name);
			} else {
				_blockWarnDue = null;
				Log.Info("you're on this account - waiting until you're done", Name);
			}
		} else {
			_blockWarnDue = null;

			// The wait it actually rolled, not the setting: on a human-mode account that's anywhere up to three times it, and
			// "in 5m" for a wait of 14 was simply wrong.
			if (Cfg.ResumeDelayMinutes > 0) {
				Log.Info(new Said("the account is free again - picking back up in {0}m", (int) Math.Ceiling((resumeAt - DateTime.UtcNow).TotalMinutes)), Name);
			} else {
				Log.Info("the account is free again - resuming", Name);
			}
		}
	}

	/// <summary>Goes up every time Steam sends the licence list - on sign-in, and whenever a game is added.</summary>
	public int LicenseGeneration {
		get {
			lock (_licenses) {
				return _licenseGeneration;
			}
		}
	}

	/// <summary>
	/// What this account's own licences are, as one number: the same list gives the same number, whenever and however
	/// often it's read; 0 until the list has arrived. <see cref="LicenseGeneration"/> starts again at every launch, so
	/// it can't be kept - this can. The achievement pacer keeps it with a game it has called done for DLC the account
	/// doesn't own, and looks again when it changes: a DLC bought, even while this app was closed, releases the game.
	/// </summary>
	public long LicenseStamp {
		get {
			lock (_licenses) {
				return _licenseStamp;
			}
		}
	}

	/// <summary>The stamp of a licence list: its own packages, in order, run through FNV-1a. Never 0.</summary>
	internal static long StampOf(IEnumerable<uint> ownPackages) {
		ulong hash = 14695981039346656037UL;

		foreach (uint package in ownPackages.Distinct().Order()) {
			for (int shift = 0; shift < 32; shift += 8) {
				hash ^= (package >> shift) & 0xFF;
				hash *= 1099511628211UL;
			}
		}

		return hash == 0 ? 1 : unchecked((long) hash);
	}

	private void OnLicenseList(SteamApps.LicenseListCallback cb) {
		if (cb.Result != EResult.OK) {
			Log.Debug($"licence list refused: {cb.Result}", Name);

			return;
		}

		lock (_licenses) {
			// Steam sends the whole list every time, so it replaces what we had. Only ever added to, a refunded or
			// revoked package went on counting as owned until the next restart - and a free game was never claimed back.
			_licenses.Clear();

			foreach (SteamApps.LicenseListCallback.License license in cb.LicenseList) {
				// A family member's licence carries their account ID; ours carries ours (or 0 on older licences).
				bool own = (license.OwnerAccountID == 0) || (license.OwnerAccountID == (uint) (SteamId & 0xFFFFFFFF));

				// The same package can be on the list twice: this account's own copy and a family member's. Whichever
				// came last used to win, so a family copy listed after ours marked the account's own game as "not
				// ours" - and the refund guard and the library filter both read exactly that. Ours always wins.
				if (_licenses.TryGetValue(license.PackageID, out Licence had) && !Replaces(had.Own, own)) {
					continue;
				}

				// A game somebody GIFTED arrives marked "guest pass" - not paid for by this account, but paid for by
				// the giver, who can still have it refunded under Steam's usual two hours / fourteen days. A real guest
				// pass (a timed trial) carries the same payment method, so what tells them apart is the licence
				// itself: a gift is a permanent single purchase with no time limit, a trial is not. Seen on real
				// gifts (Riders Republic, Steep): GuestPass, SinglePurchase, flags None, minute limit 0.
				_licenses[license.PackageID] = new Licence(license.TimeCreated, license.AccessToken, IsPaid(license.PaymentMethod), own,
					(license.PaymentMethod == EPaymentMethod.GuestPass) && (license.LicenseType == ELicenseType.SinglePurchase)
						&& (license.MinuteLimit == 0) && !license.LicenseFlags.HasFlag(ELicenseFlags.Expired),
					IsPermanent(license.LicenseFlags, license.MinuteLimit, license.LicenseType, license.PaymentMethod));
			}

			_appOwnedSince = null;   // the mapping is stale now
			_licenseGeneration++;

			// Only the licences that count as really having something. One that runs out, or goes from pending to
			// active, changes what the account owns for good - and has a game held for DLC looked at again.
			_licenseStamp = StampOf(_licenses.Where(static l => l.Value.Own && l.Value.Permanent).Select(static l => l.Key));
		}

		// Only what changed. Steam pushes the whole list again for all sorts of reasons - a game added anywhere in the
		// family, a guest pass, a reconnect - and writing every recent licence each time was a fifth of the log, the
		// same dozen lines over and over. The count when it moves; each recent licence once, the first time it's seen.
		List<SteamApps.LicenseListCallback.License> recent = [.. cb.LicenseList.Where(static l => DateTime.UtcNow - l.TimeCreated < TimeSpan.FromDays(14))];
		List<uint> fresh;

		lock (_licenses) {
			fresh = NewLicences(_licencesLogged, recent.Select(static l => l.PackageID));
			_licencesLogged = [.. recent.Select(static l => l.PackageID)];
		}

		if (cb.LicenseList.Count != _licenceCountLogged) {
			_licenceCountLogged = cb.LicenseList.Count;
			Log.Debug(new Said("{0} licence(s) known", cb.LicenseList.Count), Name);
		}

		// What the last fortnight's licences were, by how Steam says they were paid for - refund protection
		// decides from exactly this, so when it holds (or doesn't hold) a game the reason is in the log.
		foreach (SteamApps.LicenseListCallback.License license in recent.Where(l => fresh.Contains(l.PackageID))) {
			Log.Debug(new Said("recent licence: package {0}, {1}, {2}", license.PackageID, license.PaymentMethod, license.TimeCreated.ToString("d")) + $" · type {license.LicenseType}, flags {license.LicenseFlags}, minute limit {license.MinuteLimit}, used {license.MinutesUsed}", Name);
		}
	}

	/// <summary>The recent licences already written to the log, and the count last written - so a repeat push says nothing.</summary>
	private HashSet<uint> _licencesLogged = [];
	private int _licenceCountLogged = -1;

	/// <summary>The packages in <paramref name="now"/> that weren't in <paramref name="before"/>, each once, in order.</summary>
	internal static List<uint> NewLicences(IReadOnlySet<uint> before, IEnumerable<uint> now) =>
		[.. now.Where(p => !before.Contains(p)).Distinct()];

	/// <summary>One licence on the account's list, as kept here.</summary>
	/// <param name="Own">The account's own, not a family member's.</param>
	/// <param name="Gift">Gifted by somebody else and still refundable to them - see <see cref="OnLicenseList"/>.</param>
	/// <param name="Permanent">Owned for good - see <see cref="IsPermanent"/>.</param>
	private readonly record struct Licence(DateTime Created, ulong Token, bool Paid, bool Own, bool Gift, bool Permanent);

	/// <summary>
	/// A licence the account really has for good: not run out, not cancelled or refunded, not still waiting to go
	/// through, and not a timed one - a free weekend, a timed trial, a guest pass. Those put a game (or a DLC) on the
	/// licence list too, some of them for years after they ended, and taken as owning the DLC they let its achievements
	/// be unlocked on an account that never had it. A gifted game is a guest pass that is a plain single purchase with
	/// no time limit; any other guest pass is a trial.
	/// </summary>
	internal static bool IsPermanent(ELicenseFlags flags, int minuteLimit, ELicenseType type, EPaymentMethod method) =>
		((flags & (ELicenseFlags.Expired | ELicenseFlags.Pending | ELicenseFlags.CancelledByUser | ELicenseFlags.CancelledByAdmin
			| ELicenseFlags.CancelledByFriendlyFraudLock | ELicenseFlags.NotActivated)) == 0)
		&& (minuteLimit == 0)
		&& (type is not (ELicenseType.NoLicense or ELicenseType.SinglePurchaseLimitedUse or ELicenseType.RecurringChargeLimitedUse
			or ELicenseType.RecurringChargeLimitedUseWithOverages or ELicenseType.LimitedUseDelayedActivation))
		&& ((method != EPaymentMethod.GuestPass) || (type == ELicenseType.SinglePurchase));

	/// <summary>
	/// Whether a licence for a package already seen on this list takes its place. This account's own copy is never
	/// replaced by a family member's; otherwise the later one wins, as before.
	/// </summary>
	internal static bool Replaces(bool hadOwn, bool own) => own || !hadOwn;

	/// <summary>
	/// Money changed hands for this licence, so a refund is a thing that could be lost.
	///
	/// Free-to-play, claimed free promos, review copies and hardware bundles are all granted rather than bought;
	/// nothing about playing them can cost anybody anything, so refund protection must not hold them back. Steam
	/// still stamps them with today's date, which is exactly why the check is on payment and not only on age.
	/// </summary>
	private static bool IsPaid(EPaymentMethod method) => method is not (EPaymentMethod.None or EPaymentMethod.AutoGrant
		or EPaymentMethod.Complimentary or EPaymentMethod.Promotional or EPaymentMethod.HardwarePromo
		or EPaymentMethod.GuestPass or EPaymentMethod.OEMTicket or EPaymentMethod.MasterComp);

	/// <summary>
	/// When each owned app was first licensed to this account, whether it was paid for, and whether the account has it
	/// for good, worked out by asking Steam what is inside each owned package. Used by refund protection and to tell
	/// which DLC the account really owns, so it is built lazily and only when something asks - resolving thousands of
	/// packages on every login for a setting most people leave off would be rude.
	///
	/// Every licence counts here, run out or not: refund protection wants the earliest one. What the account really
	/// owns is <see cref="AppOwnership.Permanent"/>.
	///
	/// Returns empty on any failure, or when only part of the answer came: "no answer", never "owns nothing". Refund
	/// protection then holds nothing new but keeps what it already held; the DLC check holds the game.
	/// </summary>
	internal async Task<IReadOnlyDictionary<uint, AppOwnership>> GetAppOwnershipAsync() {
		Dictionary<uint, Licence> snapshot;
		int generation;

		lock (_licenses) {
			if (_appOwnedSince != null) {
				return _appOwnedSince;
			}

			snapshot = new Dictionary<uint, Licence>(_licenses);
			generation = _licenseGeneration;
		}

		Dictionary<uint, AppOwnership> map = [];

		if ((Apps == null) || (snapshot.Count == 0)) {
			return map;
		}

		try {
			List<SteamApps.PICSRequest> requests = snapshot.Select(kv => new SteamApps.PICSRequest(kv.Key, kv.Value.Token)).ToList();
			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet result = await Apps.PICSGetProductInfo([], requests, false).ToTask().ConfigureAwait(false);

			// A failed job hands back a default ResultSet, whose Results really is null.
			IReadOnlyList<SteamApps.PICSProductInfoCallback>? pages = result.Results;

			// Only part of the answer is no answer: a reply that stalls hands back the pages it had, with Complete false.
			// Taken as the whole list - and cached - a DLC whose page never came read as not owned for a week.
			if ((pages == null) || !result.Complete) {
				Log.Debug($"Steam didn't answer the package lookup for refund protection ({(result.Failed ? "job failed" : (pages == null) ? "no results" : "only part of it")}) - refund protection keeps what it holds and asks again in a few minutes", Name);

				return map;
			}

			foreach (SteamApps.PICSProductInfoCallback page in pages) {
				foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo package in page.Packages.Values) {
					if (!snapshot.TryGetValue(package.ID, out Licence license)) {
						continue;
					}

					List<KeyValue>? appIds = package.KeyValues["appids"]?.Children;

					foreach (KeyValue app in appIds ?? []) {
						uint appId = app.AsUnsignedInteger();

						if (appId == 0) {
							continue;
						}

						// The same app from more than one licence: see AppOwnership.Join. "For good" only from a licence
						// that is both the account's own and permanent - a family member's copy never makes it that.
						// A free weekend isn't for good either, whatever its licence says: a running free weekend's licence
						// looks like a plain purchase - no flags, no minute limit. Only "freeweekend", not "expirytime": that is
						// the end of the window to CLAIM a package in, and a free-to-keep giveaway claimed in it has one too.
						bool timed = package.KeyValues["extended"]["freeweekend"].AsInteger() != 0;
						AppOwnership next = new(license.Created, license.Paid, license.Own, license.Gift, license.Own && license.Permanent && !timed);
						map[appId] = map.TryGetValue(appId, out AppOwnership existing) ? existing.Join(next) : next;
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't work out when games were bought ({0}) - refund protection keeps what it holds and asks again soon", Log.Describe(e)), Name);

			return new Dictionary<uint, AppOwnership>();
		}

		lock (_licenses) {
			// A licence arrived while we were asking Steam - this map is already out of date, so hand it back
			// but don't cache it, or a game bought mid-lookup would never be refund-protected.
			if (generation == _licenseGeneration) {
				_appOwnedSince = map;
			}
		}

		return map;
	}

	private void OnItemAnnouncements(ItemAnnouncementsCallback cb) {
		_lastPacket = DateTime.UtcNow;
		bool standing = IsStandingCount(Interlocked.Exchange(ref _itemCountAskedTicks, 0), DateTime.UtcNow);

		if (cb.NewItems == 0) {
			return;
		}

		Log.Debug(new Said("Steam pushed {0} new item(s)", cb.NewItems), Name);

		if (!standing) {
			SignalItemDrop();
		}

		// A farming account trips Steam's green "new items" counter dozens of times a day and it stays lit
		// forever, which is both irritating and an obvious tell. Marking the inventory viewed clears it - at most
		// every 45 minutes: the inventory page is heavy, and loading it on every single card drop cost far more
		// than a counter that reads 3 for a while.
		if (Cfg.ClearInventoryNotifications && (Interlocked.Exchange(ref _inventoryVisitQueued, 1) == 0)) {
			long session = Interlocked.Read(ref _session);

			_ = Task.Run(async () => {
				try {
					// Human mode: an uneven 30-120 minutes apart, and a few minutes after the drop at the earliest.
					TimeSpan gap = Cfg.LegitMode ? TimeSpan.FromMinutes(Rng.Next(30, 121)) : TimeSpan.FromMinutes(45);
					TimeSpan wait = _inventoryVisitedAt + gap - DateTime.UtcNow;

					if (Cfg.LegitMode && (wait < TimeSpan.FromMinutes(3))) {
						wait = Rng.Minutes(3, 15);
					}

					if (wait > TimeSpan.Zero) {
						await Task.Delay(wait).ConfigureAwait(false);
					}

					// Stopped, signed out or started afresh during that wait (up to two hours): not this run's page to load.
					if (!_running || (State != BotState.Online) || (Interlocked.Read(ref _session) != session)) {
						return;
					}

					_inventoryVisitedAt = DateTime.UtcNow;
					await Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{SteamId}/inventory/")).ConfigureAwait(false);
				} catch (Exception e) {
					// cosmetic - never let it matter
					Log.Failed("couldn't load the inventory to clear its new-items counter", e, Name);
				} finally {
					Interlocked.Exchange(ref _inventoryVisitQueued, 0);
				}
			});
		}
	}

	private int _inventoryVisitQueued;
	private int _notificationSweepQueued;
	private DateTime _inventoryVisitedAt = DateTime.MinValue;

	/// <summary>
	/// How many trade offers Steam says are waiting, or -1 if it has not told us yet.
	///
	/// Steam pushes this on login and again the moment it changes, so an account with nothing waiting never has
	/// to open the trade offers page to find that out - which is what a five-minute poll was doing all day, on
	/// every account, until the community site started answering 429.
	/// </summary>
	public int TradeOffersWaiting => Volatile.Read(ref _tradeOffersWaiting);

	/// <summary>
	/// What actually reading the trade offers page found, which beats anything the counter said.
	///
	/// Only ever LOWERS the figure or confirms it. A push that arrives while the page is being read is the more
	/// recent truth, so this never overwrites a higher number with a stale zero.
	/// </summary>
	public void NoteTradeOffersSeen(int seen) {
		// Compare-and-swap: a push landing between reading the count and writing it used to be overwritten with this
		// older, lower figure - the very thing this promises not to do.
		while (true) {
			int current = Volatile.Read(ref _tradeOffersWaiting);

			if ((seen > current) && (current >= 0)) {
				return;
			}

			if (Interlocked.CompareExchange(ref _tradeOffersWaiting, seen, current) == current) {
				return;
			}
		}
	}

	private void OnTradeOfferNotifications(TradeOfferNotificationCallback cb) {
		_lastPacket = DateTime.UtcNow;

		int previous = Volatile.Read(ref _tradeOffersWaiting);
		Volatile.Write(ref _tradeOffersWaiting, (int) cb.Waiting);

		// The first one of these is worth a line even when it says none, because "none waiting" and "Steam has
		// not told us yet" mean opposite things to the trade module and otherwise look identical from outside.
		if (previous < 0) {
			Log.Debug(new Said("Steam's trade offer counter says {0} waiting", cb.Waiting), Name);
		}

		if ((cb.Waiting == 0) || (cb.Waiting == previous)) {
			return;
		}

		Log.Debug(new Said("Steam says {0} trade offer(s) are waiting", cb.Waiting), Name);
		Plugins.PluginHost.RaiseTradeOffers(this, (int) cb.Waiting);

		// Latched like the item drop: if the trade module is mid-check nobody is on the TCS, and the news would
		// be lost until the slow pass came round the better part of an hour later.
		Volatile.Write(ref _tradeOfferPending, 1);
		_tradeOffer.TrySetResult(true);
	}

	/// <summary>
	/// Wait for Steam to say a trade offer is waiting, or for <paramref name="timeout"/> to run out.
	///
	/// This is what lets the offers page go unread for an hour at a time without an offer sitting unanswered for
	/// an hour: the news arrives as a push, and the wait ends the moment it does.
	/// </summary>
	public async Task<bool> WaitForTradeOfferAsync(TimeSpan timeout, CancellationToken ct) {
		if (Interlocked.Exchange(ref _tradeOfferPending, 0) == 1) {
			return true;
		}

		TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_tradeOffer = tcs;

		if (Interlocked.Exchange(ref _tradeOfferPending, 0) == 1) {
			return true;
		}

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

		Task delay = Task.Delay(timeout, linked.Token);
		Task winner = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);

		await linked.CancelAsync().ConfigureAwait(false);

		if (winner != tcs.Task) {
			return false;
		}

		Interlocked.Exchange(ref _tradeOfferPending, 0);

		return true;
	}

	private void OnCommentNotifications(CommentNotificationsCallback cb) {
		_lastPacket = DateTime.UtcNow;

		// Steam replays the STANDING unread count when asked at login, so the first answer is a running total,
		// not news. Only an INCREASE means somebody actually just commented - otherwise every restart announced
		// "5 unread" for comments left weeks ago.
		//
		// The OWNER count, not the total: the total also rises when somebody posts in a thread this account merely
		// commented in - every profile rep4rep had it comment on - which announced "somebody commented on this
		// profile" for comments that were on somebody else's.
		uint previous = _knownComments;
		_knownComments = cb.NewOwnerComments;

		if (!_commentBaselineSet) {
			_commentBaselineSet = true;

			// Deliberately silent.
			//
			// This is Steam's own un-dismissed notification counter, NOT a count of comments on the profile. It
			// never falls on its own and nothing here can make it - a real load of /my/commentnotifications/ and
			// a mark-all-read over the client connection were both tried and verified reaching Steam, and the
			// number did not move. So it is unchanging, uncleanable and not actionable: three good reasons never
			// to print it. It was being restated on every single login, forever.
			//
			// A comment that genuinely arrives while we are connected still gets announced, below, because that
			// one IS news and the count going UP is how you can tell.
			//
			// The sweep itself lives on the login path, so it runs whether or not Steam bothers to send this.
			return;
		}

		if (cb.NewOwnerComments <= previous) {
			return;
		}

		// Steam only says THAT somebody commented, not who or what - the line used to be this profile's own address,
		// which told you nothing. So the newest comments are read off the profile and each new one is said in full.
		int arrived = (int) Math.Min(3, cb.NewOwnerComments - previous);
		long takenAt = Interlocked.Read(ref _lastCommentAt);
		Background.Run("couldn't read the new comment", () => AnnounceCommentsAsync(arrived, takenAt), Name);

		// Read it, so the counter goes back to zero rather than climbing for the life of the account.
		ClearAllNotifications();
	}

	/// <summary>Newest comment already announced, as Steam's own timestamp - so a comment is never said twice.</summary>
	private long _lastCommentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

	/// <summary>One comment on the profile: who wrote it, what it says, and when (Unix seconds).</summary>
	public sealed record ProfileComment(string Author, string Text, long At);

	/// <summary>One announcement at a time: two pushes close together read the same comments and said each one twice.</summary>
	private readonly SemaphoreSlim _commentGate = new(1, 1);

	/// <summary>The profile's newest comments, off Steam.</summary>
	private async Task<List<ProfileComment>> ReadProfileCommentsAsync() {
		string? json = await Web.PostAsync(new Uri(WebSession.Community, $"/comment/Profile/render/{SteamId}/-1/"),
			new Dictionary<string, string> { ["start"] = "0", ["count"] = "5" }).ConfigureAwait(false);

		if (json != null) {
			try {
				using JsonDocument doc = JsonDocument.Parse(json);

				if (doc.RootElement.TryGetProperty("comments_html", out JsonElement html) && (html.GetString() is { } markup)) {
					return ReadComments(markup);
				}
			} catch (JsonException e) {
				Log.Failed("the profile's comments weren't readable", e, Name);
			}
		}

		return [];
	}

	/// <summary>Who commented and what they wrote, for each comment newer than the last one announced.</summary>
	/// <param name="takenAt">The newest comment already announced when the push came in.</param>
	/// <param name="read">Where the comments come from - the profile, unless the checks hand in their own.</param>
	internal async Task AnnounceCommentsAsync(int arrived, long takenAt, Func<Task<List<ProfileComment>>>? read = null) {
		await _commentGate.WaitAsync().ConfigureAwait(false);

		try {
			List<ProfileComment> comments = await (read ?? ReadProfileCommentsAsync)().ConfigureAwait(false);
			long last = Interlocked.Read(ref _lastCommentAt);

			// Newer than the last one said; failing that (a clock off, a comment Steam dated oddly), the newest few.
			List<ProfileComment> fresh = [.. comments.Where(c => c.At > last)];

			if (fresh.Count == 0) {
				// Unless an announcement since this push already said what's new: its comments were this push's too.
				if (last != takenAt) {
					return;
				}

				fresh = [.. comments.Take(arrived)];
			}

			if (fresh.Count == 0) {
				Log.Event(new Said("new comment on the profile - Steam wouldn't show it, so look on the profile"), Name);

				return;
			}

			Interlocked.Exchange(ref _lastCommentAt, Math.Max(last, fresh.Max(static c => c.At)));

			foreach (ProfileComment c in Enumerable.Reverse(fresh)) {
				Log.Event(c.Text.StartsWith("This comment is awaiting analysis", StringComparison.Ordinal)
					? new Said("new comment from {0} - Steam is still checking what it says", c.Author)
					: new Said("new comment from {0}: \"{1}\"", c.Author, Columns.ClipChars(c.Text, 200, "...")), Name);
			}
		} finally {
			_commentGate.Release();
		}
	}

	/// <summary>The comments in Steam's comment-list markup, newest first, as the list gives them.</summary>
	public static List<ProfileComment> ReadComments(string html) {
		List<ProfileComment> found = [];

		foreach (string block in html.Split("class=\"commentthread_comment ", StringSplitOptions.None).Skip(1)) {
			int link = block.IndexOf("commentthread_author_link", StringComparison.Ordinal);
			int body = block.IndexOf("class=\"commentthread_comment_text\"", StringComparison.Ordinal);
			string? author = link < 0 ? null : Html.Between(block, "<bdi>", "</bdi>", link);
			string? text = body < 0 ? null : Html.Between(block, ">", "</div>", body);

			if ((author == null) || (text == null)) {
				continue;
			}

			long at = long.TryParse(Html.Between(block, "data-timestamp=\"", "\""), out long t) ? t : 0;
			found.Add(new ProfileComment(Html.Text(author), Html.Text(text), at));
		}

		return found;
	}

	/// <summary>
	/// Mark EVERY Steam notification read - comments, gifts, help requests, the lot.
	///
	/// Steam's tray counters never fall on their own; they sit lit until something reads them, so on an account
	/// nobody signs into by hand they only ever climb. This asks Steam to mark the whole lot read in one
	/// message, and also loads the two pages that clear the older per-type counters the tray does not cover.
	///
	/// Best effort and deliberately quiet. A counter staying lit is worth nothing to anybody, so this never
	/// announces success it cannot verify and never interferes with anything that matters.
	///
	/// One thing it does NOT shift: the legacy comment counter behind ClientCommentNotifications. Both the tray
	/// message and a real load of /my/commentnotifications/ (verified reaching Steam and returning the page)
	/// leave it exactly where it was. That number only ever seems to move for the Steam client itself. The log
	/// no longer repeats it, which was the part that actually mattered.
	/// </summary>
	private void ClearAllNotifications() {
		if (!Cfg.ClearNotifications) {
			return;
		}

		// On a human-mode account one sweep waits a little first - not within a second of signing in or of a comment
		// landing. Nobody sees notifications being read, so it isn't held for the account's day.
		bool human = Cfg.LegitMode;

		if (human && (Interlocked.Exchange(ref _notificationSweepQueued, 1) == 1)) {
			return;
		}

		_ = Task.Run(async () => {
			if (human) {
				try {
					await Task.Delay(Rng.HumanMinutes(2, 30)).ConfigureAwait(false);
				} finally {
					Interlocked.Exchange(ref _notificationSweepQueued, 0);
				}

				if (!_running || (State != BotState.Online)) {
					return;
				}
			}

			try {
				// The modern tray, in one shot.
				Unified?.CreateService<SteamKit2.WebUI.Internal.SteamNotification>()?.MarkNotificationsRead(new SteamKit2.WebUI.Internal.CSteamNotification_MarkNotificationsRead_Notification {
					mark_all_read = true
				});
			} catch (Exception e) {
				Log.Debug(new Said("couldn't mark notifications read: {0}", Log.Describe(e)), Name);
			}

			// The two older counters, which the tray message does not touch. Loading the page is what clears them.
			foreach (string page in (string[]) [$"/profiles/{SteamId}/commentnotifications/", $"/profiles/{SteamId}/inventory/"]) {
				try {
					await Web.GetAsync(new Uri(WebSession.Community, page)).ConfigureAwait(false);
				} catch (Exception e) {
					// cosmetic - never let it matter
					Log.Failed($"couldn't load {page} to clear its counter", e, Name);
				}
			}

			// Everything Steam had told us about is now swept, so what it said about trade offers is no longer
			// something we can rely on - we may have just zeroed an offer that was already waiting. Put the count
			// back to "don't know" and wake the trade module, so it takes exactly one look and finds anything
			// that was there. From that point on a genuinely new offer arrives as its own push, as before.
			Volatile.Write(ref _tradeOffersWaiting, -1);
			Volatile.Write(ref _tradeOfferPending, 1);
			_tradeOffer.TrySetResult(true);
		});
	}

	/// <summary>
	/// The first item count after a sign-in asked for it (at <paramref name="askedTicks"/>, 0 when it didn't) is the count
	/// that was already standing, not something new - within a minute of asking. Later, it's a real push.
	/// </summary>
	public static bool IsStandingCount(long askedTicks, DateTime now) => (askedTicks != 0) && (now.Ticks - askedTicks is >= 0 and < TimeSpan.TicksPerMinute);

	private void SignalItemDrop() {
		// Level-triggered, not edge-triggered. A drop that lands while the farmer is busy re-reading the badge
		// page has nobody waiting on the TCS; latching it means the very next wait returns immediately instead
		// of the drop being lost and the farmer sitting out a full re-check interval for nothing.
		Volatile.Write(ref _dropPending, 1);
		_itemDrop.TrySetResult(true);
	}

	/// <summary>
	/// Wait for Steam to push a new item, or for <paramref name="timeout"/> to run out. Returns true if a drop
	/// landed - including one that arrived while the caller was busy.
	/// </summary>
	public async Task<bool> WaitForItemDropAsync(TimeSpan timeout, CancellationToken ct) {
		if (Interlocked.Exchange(ref _dropPending, 0) == 1) {
			return true;
		}

		TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_itemDrop = tcs;

		// Re-check after arming, in case a drop landed between the exchange above and the assignment.
		if (Interlocked.Exchange(ref _dropPending, 0) == 1) {
			return true;
		}

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

		Task delay = Task.Delay(timeout, linked.Token);
		Task winner = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);

		await linked.CancelAsync().ConfigureAwait(false);

		if (winner != tcs.Task) {
			return false;
		}

		Interlocked.Exchange(ref _dropPending, 0);

		return true;
	}

	// ── items on the move ───────────────────────────────────────────────────
	private readonly HashSet<ulong> _itemsOnTheMove = [];

	/// <summary>
	/// Claim items for a send or a sale: the ones nothing else of this account is moving right now, held until
	/// <see cref="ReleaseItems"/>. By asset id - a clash between two inventories only holds one item back once.
	/// </summary>
	/// <remarks>
	/// A send and a sale each read the inventory and the waiting trades, then act on what they found. Run together -
	/// the timed send while the seller works through its listings, or two sends at once - both picked the same
	/// cards: the listing took a card out of the trade offer and Steam dropped the whole offer, or the same cards
	/// went out in two offers. Whoever claims a card first moves it; the other leaves it out.
	/// </remarks>
	internal HashSet<ulong> ClaimItems(IEnumerable<ulong> assets) {
		lock (_itemsOnTheMove) {
			return [.. assets.Where(_itemsOnTheMove.Add)];
		}
	}

	/// <summary>Hand back items claimed with <see cref="ClaimItems"/>, once the send or sale is done with them.</summary>
	internal void ReleaseItems(IEnumerable<ulong> assets) {
		lock (_itemsOnTheMove) {
			_itemsOnTheMove.ExceptWith(assets);
		}
	}

	// ── tokens ──────────────────────────────────────────────────────────────
	/// <summary>The web access token is treated as live for its whole span except the last few minutes.</summary>
	private const int AccessTokenSlackMinutes = 5;

	/// <summary>After a web-token mint fails, the next one waits this long - on that account.</summary>
	/// <remarks>
	/// Every web request that finds no usable token asks for one, so with nothing to hold it back a failed mint (Steam
	/// down, the connection half gone) was tried again on every page any module wanted - a mint request a second, and a
	/// warning each time. A sign-in since (new tokens) lifts the wait at once.
	/// </remarks>
	public static readonly TimeSpan MintRetryAfter = TimeSpan.FromMinutes(10);

	/// <summary>What <see cref="GetAccessTokenAsync"/> does next.</summary>
	public enum TokenStep {
		/// <summary>The token held is good for more than the last few minutes: hand it back, no network call.</summary>
		Reuse,

		/// <summary>Get a new one from Steam.</summary>
		Mint,

		/// <summary>A mint failed less than <see cref="MintRetryAfter"/> ago: no new try yet.</summary>
		Wait
	}

	/// <summary>
	/// Reuse the token held, mint a new one, or wait out a failed mint. A token within its last
	/// <see cref="AccessTokenSlackMinutes"/> minutes is renewed, and one Steam just turned away (<paramref name="rejected"/>)
	/// is replaced - both only once the wait after a failed mint is over.
	/// </summary>
	public static TokenStep NextTokenStep(bool rejected, string? access, DateTime? validUntil, DateTime? mintFailedAt, DateTime nowUtc) {
		if (!rejected && !string.IsNullOrEmpty(access) && (validUntil is { } until) && (until > nowUtc.AddMinutes(AccessTokenSlackMinutes))) {
			return TokenStep.Reuse;
		}

		return (mintFailedAt is { } failed) && (nowUtc - failed < MintRetryAfter) ? TokenStep.Wait : TokenStep.Mint;
	}

	/// <summary>
	/// While waiting out a failed mint: the token held if it still has life left and Steam hasn't turned it away - a
	/// renewal in the last few minutes that failed keeps using the old one until it really runs out - or null.
	/// </summary>
	public static string? HeldWhileWaiting(bool rejected, string? access, DateTime? validUntil, DateTime nowUtc) =>
		!rejected && !string.IsNullOrEmpty(access) && (validUntil is { } until) && (until > nowUtc) ? access : null;

	/// <summary>When a new web-token mint may be tried again after one failed, or null when none is being held back.</summary>
	public DateTime? MintRetryAt {
		get {
			lock (_tokenSync) {
				return (_mintFailedAt is { } failed) && (_mintFailedGeneration == _tokenGeneration) ? failed + MintRetryAfter : null;
			}
		}
	}

	/// <summary>A mint for the sign-in <paramref name="generation"/> failed: none again for <see cref="MintRetryAfter"/>.</summary>
	private void NoteMintFailed(long generation, DateTime nowUtc) {
		lock (_tokenSync) {
			_mintFailedAt = nowUtc;
			_mintFailedGeneration = generation;
		}
	}

	private void ClearMintFailed() {
		lock (_tokenSync) {
			_mintFailedAt = null;
		}
	}

	/// <summary>Record a web access token and read its expiry out of the JWT so reuse can be judged.</summary>
	private void SetAccessToken(string? token) {
		_accessToken = string.IsNullOrEmpty(token) ? null : token;
		_accessTokenValidUntil = _accessToken == null ? null : WebSession.ReadJwtExpiryOf(_accessToken);

		// A token we cannot read the expiry of is worse than useless - we would reuse it forever. Drop it.
		if ((_accessToken != null) && (_accessTokenValidUntil == null)) {
			_accessToken = null;
		}
	}

	/// <summary>A sign-in's tokens replace whatever was held - or, with nulls, a rejected one throws them all away.</summary>
	private void ReplaceTokens(string? refresh, string? access) {
		lock (_tokenSync) {
			_tokenGeneration++;
			_refreshToken = string.IsNullOrEmpty(refresh) ? null : refresh;

			if (_refreshToken == null) {
				TokenStore.Clear(Name);
				SetAccessToken(null);

				return;
			}

			TokenStore.Save(Name, _refreshToken);

			if (!string.IsNullOrEmpty(access)) {
				SetAccessToken(access);
				TokenStore.SaveAccess(Name, access);
			}
		}
	}

	/// <summary>Keep what a mint handed back - unless a sign-in replaced the tokens while the request was out.</summary>
	/// <remarks>
	/// The mint is sent with the refresh token held at the time and answered later. A drop and reconnect in between
	/// can have that token rejected (password changed - it is cleared and the password asked for) or a fresh sign-in
	/// store a new one. Writing the late answer over that put the old token back in memory and on disk, over the new
	/// one - so the next sign-in used a revoked token, or a restart asked for the password again. Now it is dropped.
	/// </remarks>
	/// <returns>The access token in force afterwards, or null when the answer was too late to use.</returns>
	private string? AdoptMinted(long generation, string? rotated, string? access) {
		lock (_tokenSync) {
			if (generation != _tokenGeneration) {
				Log.Debug("a web token arrived after the sign-in changed - not kept", Name);

				return null;
			}

			if (!string.IsNullOrEmpty(rotated) && (rotated != _refreshToken)) {
				_refreshToken = rotated;   // Steam rotated it; keeping the old one would lock us out
				TokenStore.Save(Name, rotated);
			}

			if (!string.IsNullOrEmpty(access)) {
				SetAccessToken(access);
				TokenStore.SaveAccess(Name, access);
			}

			return _accessToken;
		}
	}

	/// <summary>
	/// A usable web access token - the SAME one for its whole ~24h life, minting a new one only when the current
	/// one is spent.
	///
	/// This is the fix for the Friends & Chat sign-outs, and the reasoning is worth keeping. A minted web token
	/// is a fresh web SESSION as far as Steam is concerned, and creating one on every connect is what kept
	/// throwing the account's owner off his own friends list. Minting at most once a day and reusing the token in
	/// between does not evict the owner, so that is what this does.
	/// The token that arrives with the login is preferred over minting at all (see the auth flow), and whatever
	/// we end up with is persisted so a restart reuses it rather than minting afresh.
	/// </summary>
	/// <param name="remint">
	/// Steam has just REJECTED the cached token (the web session bounced to the login page), so handing it back again
	/// only fails the retry the same way. Mints a new one even though its clock says it has life left - at most
	/// once every ten minutes, so a page that keeps bouncing for some other reason can't mint a session a minute.
	/// </param>
	internal async Task<string?> GetAccessTokenAsync(bool remint = false) {
		if (State != BotState.Online || SteamId == 0) {
			return null;
		}

		await _tokenLock.WaitAsync().ConfigureAwait(false);

		long generation = 0;

		try {
			DateTime now = DateTime.UtcNow;
			bool rejected = remint && (now - _lastRemint > TimeSpan.FromMinutes(10));

			// Read together: a sign-in can be replacing them this moment, and half of each is a token with the wrong expiry.
			string? access;
			DateTime? validUntil;
			string? refresh;
			DateTime? mintFailedAt;

			lock (_tokenSync) {
				access = _accessToken;
				validUntil = _accessTokenValidUntil;
				refresh = _refreshToken;
				generation = _tokenGeneration;
				// A failed mint holds the next one back only while the sign-in it was for is still the one in force.
				mintFailedAt = _mintFailedGeneration == generation ? _mintFailedAt : null;
			}

			switch (NextTokenStep(rejected, access, validUntil, mintFailedAt, now)) {
				case TokenStep.Reuse:
					// Still good for more than the slack window - hand back exactly what we already have. No network call,
					// no new session, nothing for Steam to arbitrate against the owner's client.
					Log.Debug(new Said("reusing web token (good for {0}h) - no new web session", ((validUntil!.Value - now).TotalHours).ToString("0.#")), Name);

					return access;
				case TokenStep.Wait:
					// The last mint failed minutes ago: not again yet. Every web request comes through here, so this is
					// said once, not per request.
					string? held = HeldWhileWaiting(rejected, access, validUntil, now);
					Log.DebugOnChange($"mintwait:{Name}", held != null
						? "getting a new web token failed - using the old one until it runs out, trying again in 10 minutes"
						: "getting a web token failed - trying again in 10 minutes", Name);

					return held;
			}

			if (string.IsNullOrEmpty(refresh)) {
				Log.Debug("no web token: there is no login token to get one with", Name);

				return null;
			}

			// The ten minutes between two re-mints for a rejected token start when one is really sent.
			if (rejected) {
				_lastRemint = now;
			}

			Log.Info("daily web refresh - getting a new web token", Name);

			// Genuinely spent (or never had one). Mint a replacement. allowRenewal: true lets Steam rotate the
			// long-lived refresh token before it ages out, so an unattended farmer keeps
			// running for months without the password.
			AccessTokenGenerateResult result = await Client.Authentication.GenerateAccessTokenForAppAsync(SteamId, refresh, true).ConfigureAwait(false);

			if (string.IsNullOrEmpty(result.AccessToken)) {
				NoteMintFailed(generation, DateTime.UtcNow);
				Log.Debug("no web token: Steam sent none back - trying again in 10 minutes", Name);
			} else {
				ClearMintFailed();
				Log.Recovered($"mintwait:{Name}");
			}

			return AdoptMinted(generation, result.RefreshToken, result.AccessToken);
		} catch (Exception e) {
			NoteMintFailed(generation, DateTime.UtcNow);
			// Said once a try - and with the wait, a try is at most every 10 minutes, not every web request.
			Log.Warn(new Said("couldn't get a web token: {0}", Log.Scrub(e.Message)), Name);

			return null;
		} finally {
			_tokenLock.Release();
		}
	}

	// ── what the account is "playing" ───────────────────────────────────────
	/// <summary>
	/// Set what Steam thinks this account is playing.
	///
	/// With a custom name, a non-Steam SHORTCUT entry carrying that name goes FIRST and the real appIDs follow.
	/// Steam then shows the custom name on the profile and the friends list while the real games still accrue
	/// playtime. Order matters: the shortcut has to lead, or the real game's store name wins the display.
	/// </summary>
	public void SetPlaying(IReadOnlyCollection<uint> appIds, string? overrideName = null, bool force = false) {
		// One decision at a time, and never across a stand-down. Unlocked, a caller passed the "you're playing" check,
		// the stand-down for the owner cleared what was announced, and the caller then sent its games anyway - over the
		// owner's own session, with his play counted as ours. And two callers at once (the name heal and the idler) left
		// what we think we announced different from what Steam got. Everything in here sends without waiting.
		lock (_playGate) {
			SetPlayingLocked(appIds, overrideName, force);
		}
	}

	private void SetPlayingLocked(IReadOnlyCollection<uint> appIds, string? overrideName, bool force) {
		if (State != BotState.Online) {
			return;
		}

		// The guard lives HERE, not only in the callers. Steam allows one playing session per account, so
		// claiming a game while the human is in one is exactly what would throw them out of it. Clearing games
		// (an empty list with no name) is always allowed - that IS how we stand down.
		bool clearing = (appIds.Count == 0) && string.IsNullOrWhiteSpace(overrideName ?? CustomName);

		if (!clearing && (PlayingBlocked || Paused)) {
			Log.Debug(PlayingBlocked
					? new Said("games-played not sent - you're using the account")
					: new Said("games-played not sent - paused"), Name);

			return;
		}

		string label = overrideName ?? CustomName;
		List<uint> apps = appIds.Distinct().Where(static a => a != 0).ToList();
		PlayingApps = apps;

		// A re-assert that changes nothing is NOT free.
		//
		// Re-sending the same games-played to a session that is already running is precisely what knocks a custom
		// name off the friends list: relative to the shortcut - which has been running for minutes - the real games
		// have just (re)started, so Steam promotes one of them and friends see "Rust" instead of 💀nocat.lol💀.
		// The idler re-asserts every 4-7 minutes, so each one was a dice roll, and the heartbeat's heal spent all
		// day putting the name back only for the next re-assert to knock it off again.
		//
		// So when the set is unchanged AND Steam itself says it is already showing what we want, send nothing at
		// all. PlayingAsSeen is Steam's own echo, so this only stays quiet while it genuinely agrees: an empty
		// echo (never heard from Steam) or any disagreement falls through and re-asserts exactly as before.
		if (!force
			&& !string.IsNullOrWhiteSpace(label)
			&& (label == _announcedLabel)
			&& (_announcedApps != null)
			&& _announcedApps.SequenceEqual(apps)
			&& (PlayingAsSeen == label)) {
			return;
		}

		// Log a change in what friends actually see - the custom name, a real game, or nothing - once per change.
		// This makes "a boosting account never leaves its custom name" checkable: if the custom name ever lapses to a
		// real game or to nothing, there's a timestamped line for it instead of a silent flip nobody can trace.
		// The comparison stays on plain text; only what is SHOWN is a sentence. "nothing" passed as a value rode
		// untranslated inside the translated line, so it is a sentence of its own.
		string shown = !string.IsNullOrWhiteSpace(label) ? label : apps.Count > 0 ? GameNames.Of(apps[0]) : "";
		Said showing = shown.Length > 0 ? new Said("now showing {0}", shown) : new Said("now showing nothing");
		if (shown != _lastLoggedPlaying) {
			if (_lastLoggedPlaying != null) {
				// A human-mode account narrates every change itself - "short break - back in about 24m",
				// "playing X for about Ym" - so this lower-level "now showing nothing / <game>" line only stacks
				// noise on a clearer one. Keep it for the trace, at debug. Other accounts have no such narrator
				// (and this is the line that proves their custom name never lapsed), so there it stays visible.
				if (HumanOwned) {
					Log.Debug(showing, Name);
				} else {
					Log.Info(showing, Name);
				}
			}

			_lastLoggedPlaying = shown;
		}

		// Steam shows whichever game started MOST RECENTLY.
		//
		// At login the shortcut and the real games are announced together and the shortcut wins. But add a game
		// to a session that is already running and that appID is the newest thing playing, so Steam puts it on
		// the friends list and the custom name vanishes - which is exactly what you see if you add a game while
		// it is idling.
		//
		// The fix is to make the shortcut the newest thing. Announce the real games on their own first, then
		// re-announce with the shortcut: relative to that first message the shortcut has just started, so it is
		// the one Steam displays. Only needed when the game list actually changed - re-sending this on every
		// routine re-assert would make the friends list flicker for no reason.
		// Steam displays whichever game started MOST RECENTLY, so adding one to a running session puts that game
		// on the friends list in place of the custom name. At LOGIN the same message shows the custom name,
		// because everything starts at once and the shortcut is first in the list.
		//
		// So when the list changes, reproduce the login: stop everything, then announce the whole set fresh a
		// moment later. An earlier attempt sent games-without-the-shortcut as the first step, which was much
		// worse - the idler's own re-assert landed inside the gap, superseded the second half, and left the
		// account announcing nothing at all. Stopping first cannot do that: the worst case is two seconds of
		// nothing, and the sequence guard makes even that impossible to leave behind.
		// The comment above says the shortcut wins at login because everything starts at once. It does not,
		// reliably: Steam picks, and it picked Counter-Strike 2. The account signed in showing the real game
		// and only flipped to the custom name six seconds later, when the list happened to change and the
		// relaunch below fired for its own reasons. Every login had that window; it just took somebody
		// watching the friends list at the right moment to see it.
		//
		// So the FIRST announce of a session takes the same path as a changed list. There is nothing to stop
		// yet - the account only just logged on - so the empty message costs nothing, and the two-second gap
		// shows nothing rather than showing the wrong thing.
		bool relaunch = !string.IsNullOrWhiteSpace(label)
			&& (apps.Count > 0)
			&& (force
				|| (_announcedApps == null)                                       // first announce after logon
				|| !_announcedApps.SequenceEqual(apps)
				|| ((_announcedLabel != null) && (_announcedLabel != label)));   // name just turned on - put it on top

		// Captured before _announcedApps is overwritten below - the persona re-apply further down needs to
		// know whether this call actually CHANGED anything, and by then the record has already been updated.
		bool gamesChanged = (_announcedApps == null) || !_announcedApps.SequenceEqual(apps);

		int mine = Interlocked.Increment(ref _playSequence);
		_announcedApps = apps;
		_announcedLabel = label;

		if (string.IsNullOrWhiteSpace(label)) {
			MismatchedSince = null;   // nothing to slip when no name is being shown
		}

		if (relaunch) {
			Client.Send(BuildGamesPlayed(null, []));
			Log.Debug("game list changed - restarting the session so the custom name stays on top", Name);

			_ = Task.Run(async () => {
				try {
					await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

					// The check and the send together, under the same lock as the stand-down - or the owner could start
					// playing between the two and get these games sent over his session.
					lock (_playGate) {
						if ((Volatile.Read(ref _playSequence) != mine) || (State != BotState.Online) || PlayingBlocked || Paused) {
							return;   // superseded, or no longer ours to drive - whoever won will announce its own
						}

						// Cfg.GameDevice, same as the ordinary path below. Leaving it off defaulted the device to 0
						// ("desktop, legacy") on every re-announce, quietly discarding whatever the account was set
						// to - and now that a login takes this path too, that was every session.
						Client.Send(BuildGamesPlayed(label, apps, Cfg.GameDevice));

						// Same rule as the ordinary path below: on an account its owner signs into himself the status is his.
						// Sent here regardless, every game-list change - and every custom-name heal, two minutes apart - put
						// him back to Online after he had picked Invisible or Away in his own client.
						if (!Cfg.IUseThisAccount) {
							ApplyPersona();
						}
					}
				} catch (Exception e) {
					Log.Debug(new Said("couldn't re-announce after the game list changed: {0}", Log.Describe(e)), Name);
				}
			});

			Playing = label + $" (+{apps.Count})";

			return;
		}

		ClientMsgProtobuf<CMsgClientGamesPlayed> outgoing = BuildGamesPlayed(label, apps, Cfg.GameDevice);
		Client.Send(outgoing);

		Log.Debug(new Said("games-played sent: {0} entr(ies)", outgoing.Body.games_played.Count)
			+ (string.IsNullOrWhiteSpace(label) ? "" : ", custom name first")
			+ $" - apps [{string.Join(",", apps)}]", Name);

		// Steam flips the persona back to Online when a game STARTS, so the override has to be re-sent after
		// that - but ONLY then.
		//
		// This used to run on every call, and SetPlaying is called on a timer: the idler re-asserts every four
		// to seven minutes and human mode re-asserts every tick. Each one sent the persona again, and setting
		// the persona from a second session makes Steam sign the OTHER session out of Friends & Chat. That is
		// what was repeatedly kicking the account owner off their own friends list - not the heartbeat, which
		// was the first thing I removed and the wrong one.
		//
		// A re-assert of the SAME games changes nothing on Steam s side, so there is no reset to undo and no
		// reason to send anything. Only a genuine change needs it.
		// Not on an account its owner signs into himself: its status is his, announced once at logon and then left alone.
		if (gamesChanged && !PlayingBlocked && !Cfg.IUseThisAccount) {
			ApplyPersona();
			RePersonaShortly();
		}

		// Names, not raw appIDs. Without the GameNames.Of the dashboard showed "730" instead of "Counter-Strike 2"
		// whenever a real game was playing with no custom-name label over it (i.e. human mode mid-session).
		Playing = !string.IsNullOrWhiteSpace(label)
			? label + (apps.Count > 0 ? $" (+{apps.Count})" : "")
			: apps.Count == 0 ? "" : string.Join(", ", apps.Take(3).Select(GameNames.Of));
	}

	/// <summary>One games-played message: the custom-name shortcut first when there is one, then the real games.</summary>
	internal static ClientMsgProtobuf<CMsgClientGamesPlayed> BuildGamesPlayed(string? label, List<uint> apps, int device = 0) {
		ClientMsgProtobuf<CMsgClientGamesPlayed> msg = new(EMsg.ClientGamesPlayedWithDataBlob);

		if (!string.IsNullOrWhiteSpace(label)) {
			CMsgClientGamesPlayed.GamePlayed shortcut = new() {
				game_id = SteamIds.ShortcutGameId,
				game_extra_info = label
			};

			DescribeDevice(shortcut, device);
			msg.Body.games_played.Add(shortcut);
		}

		foreach (uint app in apps) {
			if (msg.Body.games_played.Count >= SteamIds.GamesAtOnce) {
				break;   // Steam ignores the whole message past this, so truncate rather than lose everything
			}

			CMsgClientGamesPlayed.GamePlayed played = new() { game_id = app };

			DescribeDevice(played, device);
			msg.Body.games_played.Add(played);
		}

		return msg;
	}

	/// <summary>
	/// Say HOW the game is running, not just that it is.
	///
	/// The persona flags claim a device; this is the evidence for the claim. Steam accepts the phone, Big
	/// Picture, VR and controller flags on their own, but it kept dropping LaunchTypeCompatTool - the half of
	/// the Deck flag that means "running under Proton" - because nothing in the games-played message mentioned
	/// a compat tool at all. The flag said Proton while the session said nothing, and Steam believed the
	/// session. So a Deck now sends what a Deck sends: a Proton tool id, the Linux platform it runs on, and the
	/// built-in controller it has.
	/// </summary>
	private static void DescribeDevice(CMsgClientGamesPlayed.GamePlayed played, int device) {
		if (device != SteamIds.DeviceSteamDeck) {
			return;
		}

		played.compat_tool_id = SteamIds.ProtonExperimental;
		played.compat_tool_cmd = "proton waitforexitandrun";
		played.game_os_platform = (int) EOSType.Linux6x;
		played.primary_controller_type = SteamIds.ControllerTypeSteamDeck;
		played.total_steam_controller_count = 1;
	}

	/// <summary>The real appIDs last announced, so a CHANGE can be told apart from a routine re-assert.</summary>
	private List<uint>? _announcedApps;

	/// <summary>The name last announced alongside them - null until the first announcement of this session.</summary>
	private string? _announcedLabel;

	/// <summary>Bumped by every SetPlaying, so a delayed re-announce knows it has been superseded.</summary>
	private int _playSequence;

	/// <summary>What is announced and whether it may be: SetPlaying, its delayed halves, and the stand-down for the owner.</summary>
	private readonly Lock _playGate = new();

	/// <summary>A delayed persona re-send is still wanted: nothing newer announced, signed in, and nobody else on the account.</summary>
	internal bool StillRePersona(int sequence) =>
		(Volatile.Read(ref _playSequence) == sequence) && (State == BotState.Online) && !PlayingBlocked && !Paused;

	public void StopPlaying() => SetPlaying([], "");

	/// <summary>
	/// Push the persona state AND the device flags in one message. The device flags are what put the little
	/// Steam Deck / phone / VR badge next to the name on a friends list - Steam has no other way to say it.
	/// </summary>
	/// <summary>
	/// Re-send the persona a moment after a game starts, twice.
	///
	/// Steam's reset lands somewhere after it processes the games-played message and there is no callback that
	/// says when. One fixed delay is a guess; two spaced attempts cost two packets and stop the guess mattering.
	/// Only fires when something is actually overriding the persona - an account that is simply online has
	/// nothing to protect.
	/// </summary>
	private void RePersonaShortly() {
		// Not while another session is on the account. PlayingBlocked is the one reliable signal we get that
		// somebody else is logged in, and re-sending the persona underneath them would sign them out.
		if ((_personaOverride == null) || PlayingBlocked) {
			return;
		}

		int mine = Volatile.Read(ref _playSequence);

		_ = Task.Run(async () => {
			try {
				foreach (TimeSpan wait in (TimeSpan[]) [Rng.Seconds(2, 4), Rng.Seconds(3, 6)]) {
					await Task.Delay(wait).ConfigureAwait(false);

					// Something newer took the session - it will apply its own. Or the owner started playing (or it was
					// paused) during the wait: a persona sent from here under him signs him out of Friends & Chat.
					lock (_playGate) {
						if (!StillRePersona(mine)) {
							return;
						}

						ApplyPersona();
					}
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't re-apply the persona: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Name);
			}
		});
	}

	/// <summary>
	/// What kind of machine the logon should claim to be, for the device badge to be believed.
	///
	/// Only the Deck needs this: a Deck is a Linux handheld, and Steam cross-checks the badge against what the
	/// session said it was running on. Phone, Big Picture and VR are all things a Windows install genuinely
	/// does, so those keep the real OS and work from the persona flags alone.
	/// </summary>
	private int _flagsAsSeen = -1;

	/// <summary>The device names, for saying which one out loud rather than printing a bitmask.</summary>
	internal static string DeviceLabel(int device) => device switch {
		512 => "phone",
		1024 => "Big Picture",
		2048 => "VR",
		SteamIds.DeviceSteamDeck => "Steam Deck",
		0 => "PC",
		_ => "device " + device
	};

	private static EOSType? DeviceOSType(int device) =>
		device == SteamIds.DeviceSteamDeck ? EOSType.Linux6x : null;   // SteamOS 3 is Arch on a 6.x kernel

	public void ApplyPersona() {
		if (State != BotState.Online) {
			return;
		}

		// Never on an account its owner also signs into.
		//
		// Gated here rather than at the six call sites, because a rule with six copies is a rule that will grow
		// a seventh that forgets. Steam resolves two sessions setting one persona by signing the other out of
		// Friends and Chat - so on an account you use, every write is you being kicked off your own friends
		// list, and no amount of care about WHEN we write it makes that acceptable.
		// On an account its owner also signs into, announce plain Online and nothing else - but DO announce it.
		//
		// Returning early here looks like the polite thing to do and is the opposite. A session that announces
		// no persona is not a session with no opinion; it is an offline one, and it takes the account offline
		// with it. So the account this setting exists to protect was the account it broke. Online is the state
		// that leaves a signed-in owner where he already was, and the schedule's own moods - Away, Snooze, dark
		// for the night - are dropped rather than imposed on somebody who is sitting there using it.
		int state = Cfg.IUseThisAccount ? 1 : _personaOverride ?? Cfg.OnlineStatus;

		// Log a genuine appear-as change, once, so "did it go offline / come back online" is answerable from the
		// log. The first announce of a run is silent (that is just the login, already logged); only real
		// transitions after that - online -> invisible for the night, back again, a manual override - get a line.
		if (state != _lastLoggedPersona) {
			if (_lastLoggedPersona >= 0) {
				// Human mode already says WHY the appearance changed - a break, a meal, bedtime - in one clear
				// line of its own, so on that account this bare "now appearing away / invisible" only clutters it.
				// Debug keeps the trace; other accounts (no narrator) keep it visible.
				if (HumanOwned) {
					Log.Debug(new Said("now appearing {0}", new Said(Word(state))), Name);
				} else {
					Log.Info(new Said("now appearing {0}", new Said(Word(state))), Name);
				}
			}

			_lastLoggedPersona = state;
		}

		// Worked out the hard way, after this cost somebody most of a day of being thrown off his own friends list.
		// Two things were more assertive than they needed to be:
		//
		//   1. persona_set_by_user = true. This says "the HUMAN set this, on THIS session" - a claim to be the
		//      account's real client, which Steam honours by demoting the actual one. It's never sent now.
		//
		//   2. Both calls, every time. SteamFriends.SetPersonaState already sets the state; the raw message
		//      exists only to carry device flags (the Steam Deck / phone badge) that the former cannot express, so
		//      it's sent only when there are flags to carry.
		//
		// Offline is NOT filtered out here, and the reasoning that used to filter it was backwards. It said
		// announcing offline from a second session was the most aggressive form of the claim. The opposite is
		// true, and it is the single thing that was causing the sign-outs: a session that goes dark with plain
		// Offline (0) sits alongside its owner's own client for hours without disturbing it, while this program
		// used Invisible (7) and evicted him within two minutes of every start. Invisible is the state a
		// present user hides behind, so setting it is a claim to BE the session; Offline claims nothing. To
		// everyone on the friends list the two look the same, which is why the difference went unnoticed for so
		// long. See PersonaDark.
		try {
			Friends?.SetPersonaState((EPersonaState) state);
		} catch (Exception e) {
			// non-fatal - but re-sent every few minutes, so said once rather than every time
			Log.DebugOnChange($"setpersona:{Name}", $"couldn't set the persona: {Log.Describe(e)}", Name);
		}

		if (Cfg.GameDevice <= 0) {
			return;
		}

		ClientMsgProtobuf<CMsgClientChangeStatus> msg = new(EMsg.ClientChangeStatus) {
			Body = {
				persona_state = (uint) state,
				persona_state_flags = (uint) Cfg.GameDevice
			}
		};

		Client.Send(msg);
	}

	public async ValueTask DisposeAsync() {
		Interlocked.Increment(ref _stopCount);

		// Marked under the stop lock, the same lock a start sets itself up under - so a start queued behind this (the stuck
		// alarm's restart, a start already on its way when the account was removed or a restore replaced it) finds it
		// disposed and does nothing. Before, it signed the removed account back in: a ghost nothing could stop, and after a
		// restore two sessions of one account kicking each other off.
		await _stopGate.WaitAsync().ConfigureAwait(false);

		try {
			Volatile.Write(ref _disposed, true);
			await StopCoreAsync(false).ConfigureAwait(false);
		} finally {
			_stopGate.Release();
		}

		// _tokenLock (and the web session's own lock) are deliberately NOT disposed: a token being minted when this runs
		// releases its lock afterwards, and a disposed semaphore throws there. Neither holds anything that needs freeing.
		Web.Dispose();
	}
}

/// <summary>When an app first appeared on the account, and whether it was actually bought.</summary>
/// <param name="Own">At least one of the licences is the account's own, not a family member's.</param>
/// <param name="Permanent">
/// At least one of the account's OWN licences for it is for good (<see cref="Bot.IsPermanent"/>): not a free weekend,
/// a timed trial, a guest pass, or one that has run out, been cancelled or not gone through yet. What "does this
/// account really own it" asks - a DLC's achievements are only unlocked on this.
/// </param>
public readonly record struct AppOwnership(DateTime Since, bool Paid, bool Own, bool Gift = false, bool Permanent = false) {
	/// <summary>Somebody could lose money if it's played: bought by this account, or gifted and still refundable to the giver.</summary>
	public bool Refundable => Paid || Gift;

	/// <summary>
	/// The same app from two licences. Earliest licence wins - that's when you really got it. A game can also arrive
	/// twice (a free weekend, then the purchase), and if EITHER licence was paid for the refund clock is real; if either
	/// is the account's own and for good, it owns it for good.
	/// </summary>
	public AppOwnership Join(AppOwnership other) => new(
		other.Since < Since ? other.Since : Since,
		Paid || other.Paid,
		Own || other.Own,
		Gift || other.Gift,
		Permanent || other.Permanent);
}

public static class SteamIds {
	/// <summary>GameID layout: bits 0-23 appID, 24-31 type, 32-63 modID. Type 2 = Shortcut, i.e. a non-Steam game.</summary>
	public const ulong ShortcutGameId = (2UL << 24) | (0xFFFFFFFFUL << 32);

	/// <summary>LaunchTypeGamepad | LaunchTypeCompatTool - the pair Steam reads as "this is a Deck".</summary>
	public const int DeviceSteamDeck = 12288;

	/// <summary>Proton Experimental's appID - what a Deck reports as the compat tool for a Windows title.</summary>
	public const uint ProtonExperimental = 1493710;

	/// <summary>ESteamInputType for the Deck's own built-in controller.</summary>
	public const int ControllerTypeSteamDeck = 13;

	/// <summary>Steam's own ceiling on simultaneous games. Send more and it drops the message.</summary>
	public const int GamesAtOnce = 32;
}

/// <summary>
/// Steam Guard prompts.
///
/// If the account's authenticator secret is on this machine, the code is generated here and nobody is asked
/// anything - which is the whole point of an unattended farmer. Otherwise it falls back to asking: the console
/// first, and the web UI reads <see cref="Bot.GuardPrompt"/> to put the same question on the dashboard.
/// </summary>
public sealed class ConsoleGuard(string botName, string? sharedSecret = null) : IAuthenticator {
	private string? _lastGenerated;

	public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) {
		if (previousCodeWasIncorrect) {
			Log.Attention("that Steam Guard code was wrong, try again", botName);
		}

		// Only auto-generate once per attempt. If our own code was refused, generating the same one again just
		// burns retries - at that point the secret is wrong and a person has to look at it.
		if (!previousCodeWasIncorrect || (_lastGenerated == null)) {
			string? code = MobileAuth.GenerateCode(sharedSecret);

			if (code != null) {
				_lastGenerated = code;
				Log.Info("Steam Guard code sent from its authenticator", botName);

				return code;
			}
		} else {
			Log.Warn("Steam rejects its authenticator codes - check the secret", botName);
		}

		return await Prompt.LineAsync($"[{botName}] Steam Guard code (mobile app)", botName).ConfigureAwait(false);
	}

	public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) {
		if (previousCodeWasIncorrect) {
			Log.Warn("that email code was wrong, try again", botName);
		}

		return await Prompt.LineAsync($"[{botName}] Steam Guard code emailed to {email}", botName).ConfigureAwait(false);
	}

	public Task<bool> AcceptDeviceConfirmationAsync() {
		// With the authenticator's secret here, answer with a code instead. True tells SteamKit to wait for somebody to
		// tap Approve on the phone and never ask for a code at all - so an account whose secret we hold still sat
		// waiting on the phone at every password sign-in, and the code above was never used.
		if (MobileAuth.GenerateCode(sharedSecret) != null) {
			return Task.FromResult(false);
		}

		Log.Attention("approve the login in the Steam app, or type the code below", botName);

		return Task.FromResult(true);
	}
}

/// <summary>Refresh tokens on disk, so the password is only ever asked for once per account.</summary>
public static class TokenStore {
	private static string Dir => Path.Combine(ConfigStore.Root, "config", "tokens");

	private static string PathFor(string bot) => Path.Combine(Dir, bot + ".token");

	// The web access token is kept beside the refresh token so it survives a restart.
	//
	// Not an afterthought: it is the difference between minting one web token a day and minting one on every
	// single connect. A freshly minted web token is a new web session in Steam's eyes, and minting one on every
	// login is what threw the account's owner out of his own Friends & Chat. Kept on disk so it can be reused for its
	// full ~24h life and leave the owner's session alone.
	private static string AccessPathFor(string bot) => Path.Combine(Dir, bot + ".access");

	// These files hold credentials, so they are encrypted at rest - see Secrets. A plain-text file an older version
	// wrote is read as-is and rewritten encrypted right then, so upgrading logs nobody out and leaves nothing in the
	// clear.
	public static string? Load(string bot) => Read(PathFor(bot));

	public static string? LoadAccess(string bot) => Read(AccessPathFor(bot));

	private static string? Read(string path) {
		try {
			if (!File.Exists(path)) {
				return null;
			}

			// Read so a 'remove' clearing it at the same moment still can - see ConfigStore.ReadShared.
			string stored = ConfigStore.ReadShared(path).Trim();
			string plain = Secrets.Unprotect(stored);

			if (Secrets.IsPlain(stored) && Secrets.Available && (plain.Length > 0)) {
				// Its own try: a file that can't be written (read-only, locked, someone else's volume) still holds a
				// good token - losing it would mean typing the password again.
				try {
					string bot = Path.GetFileNameWithoutExtension(path);
					AtomicFile.Write(path, Secrets.Protect(plain, bot));
					Log.Info(new Said("login token encrypted - an older version saved it as plain text"), bot);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Debug(new Said("couldn't encrypt a login token in place: {0}", Log.Describe(e)), Path.GetFileNameWithoutExtension(path));
				}
			}

			return plain.Length > 0 ? plain : null;
		} catch (Exception e) {
			// Out loud: what follows is a sign-in with the password (or a Steam Guard code), and nothing else says why.
			Log.Warn(new Said("couldn't read the saved sign-in: {0}", Log.Scrub(e.Message)), Path.GetFileNameWithoutExtension(path));

			return null;
		}
	}

	public static void Save(string bot, string token) {
		try {
			Directory.CreateDirectory(Dir);
			AtomicFile.Write(PathFor(bot), Secrets.Protect(token, bot));
		} catch (Exception e) {
			Log.Warn(new Said("couldn't store the login token: {0}", Log.Scrub(e.Message)), bot);
		}
	}

	public static void SaveAccess(string bot, string accessToken) {
		try {
			Directory.CreateDirectory(Dir);
			AtomicFile.Write(AccessPathFor(bot), Secrets.Protect(accessToken, bot));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't store the access token: {0}", Log.Describe(e)), bot);
		}
	}

	public static void Clear(string bot) {
		foreach (string path in new[] { PathFor(bot), AccessPathFor(bot) }) {
			try {
				if (File.Exists(path)) {
					ConfigStore.DeleteFile(path);
				}
			} catch (Exception e) {
				// nothing to do - but a revoked token left on disk is tried again at the next start, so say so
				Log.Failed($"couldn't delete {Path.GetFileName(path)}", e, bot);
			}
		}
	}
}
