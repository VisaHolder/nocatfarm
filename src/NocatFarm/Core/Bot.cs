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
	public DateTime? OnlineSince { get; private set; }
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

	public DateTime? GrindUntil { get; private set; }

	/// <summary>When the grind game actually goes on. On a legit account this is a short beat after the command,
	/// so it finishes up its current game rather than snapping over instantly; on a non-human account it's now.</summary>
	public DateTime GrindStartsAt { get; private set; }

	public bool Grinding => (GrindGame != 0) && (GrindUntil > DateTime.UtcNow);

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

	/// <summary>Knock drops off the running drop run, and keep that across a restart.</summary>
	public void CountGrindDrops(int drops) {
		GrindDropsLeft = Math.Max(0, GrindDropsLeft - drops);
		SaveGrind();
	}

	public bool StartGrind(uint app, TimeSpan how, TimeSpan delay = default, bool boost = false, int drops = 0) {
		if (Refunds.Holds(app)) {
			Log.Warn(new Said("not grinding {0} - it's still inside its refund window (turn off \"Protect refundable games\" to override)", GameNames.Of(app)), Name);

			return false;
		}

		GrindGame = app;
		GrindStartsAt = DateTime.UtcNow.Add(delay);
		GrindUntil = GrindStartsAt.Add(how);   // the hours run from when it actually starts, not the command
		GrindIsBoost = boost;
		GrindDropsLeft = Math.Max(0, drops);
		SaveGrind();

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
		GrindGame = 0;
		GrindUntil = null;
		GrindIsBoost = false;
		GrindDropsLeft = 0;
		SaveGrind();

		// Put the normal games back now. Left to the idler's own schedule, "grind kylro off" answered "back to
		// normal" and the account went on playing the one grind game for up to seven more minutes.
		BotManager.ModuleOf<Modules.Idler>(this)?.Assert();
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
			Log.Warn(new Said("not putting {0} first - it's still inside its refund window (turn off \"Protect refundable games\" to override)", GameNames.Of(app)), Name);

			return false;
		}

		DropsFirstApp = app;
		DropsFirstWant = Math.Max(1, want);
		DropsFirstGot = 0;
		SaveDropsFirst();

		return true;
	}

	public void StopDropsFirst() {
		DropsFirstApp = 0;
		DropsFirstWant = 0;
		DropsFirstGot = 0;
		SaveDropsFirst();
	}

	/// <summary>Cards that just dropped in <paramref name="app"/> - counted toward a drop run on it, if there is one.</summary>
	public void CountDropsFirst(uint app, int drops) {
		if ((drops <= 0) || (app != DropsFirstApp) || !DropsFirstActive) {
			return;
		}

		DropsFirstGot = Math.Min(DropsFirstWant, DropsFirstGot + drops);

		if (DropsFirstActive) {
			SaveDropsFirst();

			return;
		}

		Log.Good(new Said("{0}: all {1} card drop(s) in - back to its usual mix of games", GameNames.Of(app), DropsFirstWant), Name);
		StopDropsFirst();
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
			Log.Debug(new Said("couldn't save the drop run: {0}", e.Message), Name);
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
				Log.Info(new Said("still going for {0}: {1} of {2} card drop(s) so far, in its sittings", GameNames.Of(DropsFirstApp), DropsFirstGot, DropsFirstWant), Name);
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't resume the drop run: {0}", e.Message), Name);
		}
	}

	private sealed record DropsFirstSave(uint App, int Want, int Got);

	/// <summary>Persist the current grind so it survives a restart, a crash, or the owner playing for a while.</summary>
	private void SaveGrind() {
		try {
			if ((GrindGame == 0) || (GrindUntil == null)) {
				if (File.Exists(GrindPath)) {
					File.Delete(GrindPath);
				}

				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(GrindPath)!);
			AtomicFile.Write(GrindPath, JsonSerializer.Serialize(new GrindSave(GrindGame, GrindUntil.Value.Ticks, GrindIsBoost, GrindDropsLeft)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the grind: {0}", e.Message), Name);
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
			GrindStartsAt = DateTime.UtcNow;   // resume now - no fresh switch-in delay on a resume
			Log.Info(new Said("resuming the grind of {0} - {1} left", GameNames.Of(GrindGame), Fmt.Hm((int) (until - DateTime.UtcNow).TotalMinutes)), Name);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't resume the grind: {0}", e.Message), Name);
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
				Log.Good(new Said("confirmed trade offer #{0} on this account's own authenticator", tradeOfferId), Name);

				return true;
			}

			return false;
		} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't confirm trade offer #{0}: {1}", tradeOfferId, e.Message), Name);

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
		Paused = true;
		PausedUntil = duration is { } d && (d > TimeSpan.Zero) ? DateTime.UtcNow + d : null;
		IsFarming = false;
		StopPlaying();

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

	public bool IsOnline => State == BotState.Online;

	/// <summary>Online, not paused, not standing down for the human, and past the courtesy delay.</summary>
	public bool CanPlay => IsOnline && !Paused && !PlayingBlocked && (DateTime.UtcNow >= _resumeAt);

	/// <summary>
	/// You've stopped playing (Steam freed the session) but the courtesy delay before we pick back up hasn't
	/// elapsed yet. In this window the account is NOT yours anymore, so the status must not still say "you're
	/// playing on this account" - that was the line that read as a contradiction right after "free again".
	/// </summary>
	public bool InResumeGrace => IsOnline && !Paused && !PlayingBlocked && (DateTime.UtcNow < _resumeAt);

	/// <summary>
	/// Does this account already have that package? Stops free-game claiming wasting an activation. Only its OWN
	/// licences count: Steam Families put the family's licences in the list too, and taking a borrowed free game
	/// for real is exactly the point.
	/// </summary>
	public bool OwnsPackage(uint packageId) {
		lock (_licenses) {
			return _licenses.TryGetValue(packageId, out (DateTime Created, ulong Token, bool Paid, bool Own, bool Gift) license) && license.Own;
		}
	}

	private readonly Dictionary<uint, (DateTime Created, ulong Token, bool Paid, bool Own, bool Gift)> _licenses = [];
	private Dictionary<uint, AppOwnership>? _appOwnedSince;
	private int _licenseGeneration;
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
	private Task? _pump;
	private Timer? _heartbeat;
	private volatile bool _running;

	/// <summary>Started and not stopped - signed in, signing in, reconnecting or sitting out a cooldown alike.</summary>
	public bool Running => _running;
	private string? _guardPrompt;
	private string? _refreshToken;

	// The web access token, cached and reused for its full life rather than re-minted on every connect. See
	// GetAccessTokenAsync and TokenStore for why that distinction is the whole ballgame for Friends & Chat.
	private string? _accessToken;
	private DateTime? _accessTokenValidUntil;
	private DateTime _lastRemint = DateTime.MinValue;

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

	private string? _password;
	private int _loggingIn;

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
			Log.Debug(new Said("waiting gift or guest pass: {0}", string.Join(", ", pass.Children.Select(static c => c.Name + "=" + c.Value))), Name);
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

			return answer.Result == EResult.OK ? answer.Body.player_level : null;
		} catch (Exception e) when (e is TimeoutException or TaskCanceledException or AsyncJobFailedException) {
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

			return answer.Result == EResult.OK ? answer.Body.summary?.points : null;
		} catch (Exception e) when (e is TimeoutException or TaskCanceledException or AsyncJobFailedException) {
			return null;
		}
	}

	/// <summary>Change the name everybody sees on the profile and friends list. Not the custom game name.</summary>
	public bool SetProfileName(string name) {
		if ((Friends == null) || !IsOnline || string.IsNullOrWhiteSpace(name)) {
			return false;
		}

		Friends.SetPersonaName(name.Trim());

		return true;
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
			Log.Debug(new Said("couldn't read the family's running games: {0}", e.Message), Name);
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
		if (PersonaAsSeen == EffectivePersona) {
			if (_personaFightSince != null) {
				_personaFightSince = null;
				_contestRetried = false;
				Log.Debug("the persona is ours again - resuming the periodic re-assert", Name);
			}
		} else if (_personaFightSince == null) {
			_personaFightSince = DateTime.UtcNow;

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
			_mismatchedSince = null;

			return;
		}

		_mismatchedSince ??= DateTime.UtcNow;
	}

	/// <summary>
	/// True once Steam has been showing something other than the custom name for long enough to mean it.
	///
	/// The dashboard and the console board both read this rather than the raw comparison, so a two-second blip
	/// at login never gets reported as a problem.
	/// </summary>
	public bool CustomNameNotShowing =>
		(_mismatchedSince != null) && (DateTime.UtcNow - _mismatchedSince.Value > TimeSpan.FromSeconds(90));

	private DateTime? _mismatchedSince;
	private DateTime? _personaFightSince;

	/// <summary>
	/// Another session is setting this account's persona right now.
	///
	/// Separate from <see cref="PersonaOverridden"/> on purpose: that one waits ninety seconds before SAYING
	/// anything, so a stale echo cannot produce a wrong headline. This one is true immediately, because backing
	/// off has to happen at the first sign - every extra round of the fight is another sign-out.
	/// </summary>
	public bool PersonaContested => _personaFightSince != null;

	/// <summary>
	/// Something else is setting this account's persona and winning.
	///
	/// In practice that means your own Steam client is signed into the same account. A Steam persona belongs to
	/// the ACCOUNT rather than to a session, and the real client wins - so nocatFarm can ask for invisible all
	/// night and the friends list will still show you online. Worth saying out loud, because the alternative is
	/// a dashboard confidently reporting "invisible" at something anybody can see is not.
	/// </summary>
	public bool PersonaOverridden =>
		(_personaFightSince != null) && (DateTime.UtcNow - _personaFightSince.Value > TimeSpan.FromSeconds(90));

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

		Unified.SendMessage<CFriendMessages_SendMessage_Request, CFriendMessages_SendMessage_Response>("FriendMessages.SendMessage#1", req);
	}

	/// <summary>
	/// Connection settings that can only be chosen up front: which transport to use, how long to wait, and
	/// whether everything goes through a proxy.
	/// </summary>
	private static SteamConfiguration BuildSteamConfiguration(BotConfig cfg) {
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
			Log.Warn(new Said("proxy '{0}' isn't a usable address ({1}) - connecting directly instead", address, e.Message));
		}

		return handler;
	}

	/// <summary>Subscribe to one Steam callback for as long as the returned handle is held.</summary>
	internal IDisposable SubscribeCallback<T>(Action<T> handler) where T : CallbackMsg => _cb.Subscribe(handler);

	public void AddModule(IBotModule m) => _modules.Add(m);
	public IReadOnlyList<IBotModule> Modules => _modules;

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
			State = BotState.Failed;
			StatusText = "couldn't start";
			Log.Error(new Said("couldn't start: {0}: {1}", e.GetType().Name, e.Message), Name);
		} finally {
			_startGate.Release();
		}
	}

	private async Task StartCoreAsync() {
		if (_running) {
			return;
		}

		// A previous run that ended without StopAsync (a disabled account, a dead login) can still be holding a
		// token source and a callback pump. Tear it down or the second start runs two pumps on one client.
		await StopAsync().ConfigureAwait(false);

		_running = true;
		Log.Info("starting up", Name);

		if (GrindGame == 0) {
			LoadGrind();   // pick a still-running grind back up after a restart or crash
			LoadDropsFirst();
		}
		Paused = Cfg.StartPaused;   // re-applied per start, so 'restart' doesn't quietly un-pause the account
		PlayingBlocked = false;
		_resumeAt = DateTime.MinValue;
		_loginFailures = 0;
		_cts = new CancellationTokenSource();
		_pump = Task.Run(() => Pump(_cts.Token), CancellationToken.None);

		State = BotState.Connecting;
		StatusText = "waiting for a login slot";

		// In line behind another account's sign-in: said, so a quiet account doesn't look stuck.
		await Limiters.WaitForLoginSlotAsync(_cts.Token, wait => {
			if (wait >= TimeSpan.FromSeconds(3)) {
				Log.Info(new Said("waiting its turn to sign in - about {0}s (Gap between logins)", (int) Math.Ceiling(wait.TotalSeconds)), Name);
			}
		}).ConfigureAwait(false);

		if (!_running) {
			return;
		}

		StatusText = "connecting";
		_lastLogOnResult = EResult.Invalid;
		Client.Connect();
	}

	public async Task StopAsync(bool graceful = false) {
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
			int max = Math.Max(0, Cfg.LegitStopMaxSeconds);

			if (max > 0) {
				// Clamped: with the setting at 1 or 2 the three-second floor sat above the ceiling, Rng.Next threw,
				// and the stop - and any self-update waiting on it - failed.
				int lo = Math.Min(max, Math.Max(3, max * 2 / 5));
				int secs = Rng.Next(lo, max + 1);
				// The only StatusText with a value baked into it, so it cannot be looked up at render time like
				// the rest. Translated here instead; Loc.T on an already-translated string returns it untouched.
				StatusText = Loc.T("finishing up - logging off in ~{0}s", secs);
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
		State = BotState.Stopped;
		StatusText = "stopped";
		OnlineSince = null;
		Playing = "";
		IsFarming = false;
		_guardPrompt = null;

		if (wasPrompting) {
			Prompt.Cancel(Name);   // only OUR question - stopping one account must not answer another's prompt
		}

		await StopModulesAsync().ConfigureAwait(false);

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
			} catch {
				// a module must never block shutdown
			}
		}
	}

	private void Pump(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				_cb.RunWaitCallbacks(TimeSpan.FromSeconds(1));
			} catch (Exception e) {
				Log.Debug(new Said("callback pump: {0}", e.Message), Name);
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
			Log.Error(new Said("the connect handler failed: {0}: {1}", e.GetType().Name, e.Message), Name);
		}
	}

	private async Task OnConnectedAsync(SteamClient.ConnectedCallback _) {
		_lastPacket = DateTime.UtcNow;

		User = Client.GetHandler<SteamUser>();
		Apps = Client.GetHandler<SteamApps>();
		Friends = Client.GetHandler<SteamFriends>();

		State = BotState.LoggingIn;
		StatusText = "logging in";

		try {
			await LogInAsync().ConfigureAwait(false);
		} catch (Exception e) {
			State = BotState.Failed;
			StatusText = "login failed";
			Log.Error(new Said("login failed: {0}", e.Message), Name);

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
	private async Task<bool> SignInWithQrAsync() {
		State = BotState.NeedsGuard;
		_guardPrompt = "scan the QR code with the Steam app";
		StatusText = "waiting for the QR code to be scanned";

		using CancellationTokenSource giveUp = new(TimeSpan.FromMinutes(5));

		try {
			QrAuthSession session = await Client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails {
				IsPersistentSession = true,
				DeviceFriendlyName = DeviceName
			}).ConfigureAwait(false);

			// Shown on the dashboard only - it's a picture to point a phone at, and Steam swaps it for a fresh one every
			// half minute or so, which drawn into the log was a wall of blocks. Said once here where to find it.
			ShowQr(session.ChallengeURL);
			session.ChallengeURLChanged = () => ShowQr(session.ChallengeURL);
			Log.Attention(new Said("{0} is waiting to be signed in: open the dashboard and scan the QR code there with the Steam app on your phone (Steam Guard tab)", Name), Name);

			AuthPollResult poll = await session.PollingWaitForResultAsync(giveUp.Token).ConfigureAwait(false);
			QrChallenge = null;

			// The account name comes from Steam - "add <name> qr" never asked for one.
			if (!string.IsNullOrEmpty(poll.AccountName) && !string.Equals(Cfg.SteamLogin, poll.AccountName, StringComparison.OrdinalIgnoreCase)) {
				Cfg.SteamLogin = poll.AccountName;
				ConfigStore.SaveBot(Name, Cfg);
			}

			_refreshToken = poll.RefreshToken;
			TokenStore.Save(Name, _refreshToken);

			if (!string.IsNullOrEmpty(poll.AccessToken)) {
				SetAccessToken(poll.AccessToken);
				TokenStore.SaveAccess(Name, poll.AccessToken);
			}

			Log.Good(new Said("signed in with the QR code as {0} - the login token is stored, nothing to type next time", poll.AccountName), Name);

			return true;
		} catch (OperationCanceledException) when (giveUp.IsCancellationRequested) {
			QrChallenge = null;
			_running = false;
			State = BotState.Failed;
			StatusText = "QR code not scanned";
			Log.Attention(new Said("nobody scanned the QR code within 5 minutes - stopping. 'start {0}' shows a fresh one", Name), Name);

			return false;
		} catch when (!_running) {
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

	private async Task LogInAsync() {
		// Steam can drop an idle pre-login connection (TryAnotherCM) while somebody is still typing a password,
		// which reconnects and would otherwise queue a SECOND prompt behind the first. One attempt at a time.
		if (Interlocked.Exchange(ref _loggingIn, 1) == 1) {
			return;
		}

		try {
			_refreshToken ??= TokenStore.Load(Name);

			// Pick up a still-valid access token from a previous run, so a restart reuses it instead of minting a
			// new web session on the spot. Nulls out silently if it is already past its life.
			if ((_accessToken == null) && TokenStore.LoadAccess(Name) is { } stored) {
				SetAccessToken(stored);
			}

			// Signed in by scanning a QR code with the Steam app instead - no password at all.
			if (string.IsNullOrEmpty(_refreshToken) && Cfg.SignInWithQr && !await SignInWithQrAsync().ConfigureAwait(false)) {
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
					Log.Error("no password - put SteamPassword in the config, or type it at the prompt", Name);

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
					poll = await session.PollingWaitForResultAsync().ConfigureAwait(false);
				} catch (Exception e) {
					// A wrong password fails here every time. Retrying it every few seconds forever is both
					// useless and exactly what makes Steam rate-limit the IP, so ask again after a few tries.
					if (++_loginFailures >= 3) {
						// Clearing _password is not enough on its own: the next attempt reads it straight back out
						// of the config. Stop, and say why, rather than retrying a wrong password every 15 seconds
						// until Steam rate-limits the whole machine.
						_loginFailures = 0;
						_password = null;
						_running = false;
						State = BotState.Failed;
						StatusText = "sign-in failed";
						Log.Attention(new Said("couldn't sign in after 3 tries ({0}) - stopping. Fix SteamPassword, then 'start {1}'", e.Message, Name), Name);
					} else {
						Log.Warn(new Said("sign-in attempt failed: {0}", e.Message), Name);
					}

					throw;
				}

				_loginFailures = 0;
				_refreshToken = poll.RefreshToken;
				TokenStore.Save(Name, _refreshToken);

				// The login just handed us a web access token as well. Keep it. Using the token that came WITH the
				// session means we never have to mint a separate one, and a separately minted web token is what was
				// evicting the owner's Friends & Chat.
				if (!string.IsNullOrEmpty(poll.AccessToken)) {
					SetAccessToken(poll.AccessToken);
					TokenStore.SaveAccess(Name, poll.AccessToken);
				}

				Log.Good("authenticated - token stored, the password won't be asked for again", Name);
			}

			if (!Client.IsConnected) {
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
			Log.Error(new Said("the logon handler failed: {0}: {1}", e.GetType().Name, e.Message), Name);
		}
	}

	private async Task OnLoggedOnAsync(SteamUser.LoggedOnCallback cb) {
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
				TokenStore.Clear(Name);
				_refreshToken = null;
				SetAccessToken(null);   // the whole family is revoked - the cached web token is dead too
				Log.Warn(new Said("stored login token rejected ({0}) - the password will be asked for again", cb.Result), Name);
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
		} catch {
			// persona state is cosmetic - never let it stop the login
		}

		// NOT forced. A forced refresh mints a brand-new web token on every single logon, and a new web token is a
		// new web session that shoves the account's own Steam client out of Friends & Chat. Passing false lets an
		// access token that is still good - the one from this very login, or the one a previous run persisted -
		// be reused as-is, so most logons create no new web session at all. That is the difference between coexisting
		// with the owner's client and evicting it.
		if (!await Web.RefreshAsync(false).ConfigureAwait(false)) {
			Log.Warn("couldn't establish a Steam web session - card farming and commenting will retry", Name);
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
			Notifications?.RequestItemAnnouncements();
			Notifications?.RequestCommentNotifications();
		} catch {
			// not fatal - the push arrives anyway once something happens
		}

		// Sweep from HERE, not from the comment-notification callback. Steam only pushes that callback when it
		// has something to say, so an account with a clean comment counter but a tray full of gifts and friend
		// invites would never have swept at all - the one place the sweep is guaranteed to run is the login it
		// is supposed to run on.
		ClearAllNotifications();

		_reconnectAttempts = 0;   // a successful logon ends the backoff streak
		StartHeartbeat();

		foreach (IBotModule m in _modules) {
			try {
				await m.StartAsync().ConfigureAwait(false);
			} catch (Exception e) {
				Log.Warn(new Said("module {0} failed to start: {1}", m.Name, e.Message), Name);
			}
		}

		// Put the custom game name back up immediately on a non-human account, rather than leaving it showing
		// plain "online" (no game) for the idler's settle delay after every reconnect. A boosting account like
		// old/kylro should never be seen off its 💀nocat.lol💀 - so re-assert it the instant it's back, not in
		// twenty seconds. Human mode owns its own accounts' timing, so this leaves those alone.
		//
		// Keyed on the CONFIG flag, not the runtime HumanOwned: on the very first logon after start/restart,
		// HumanOwned is still false for a legit account (its module hasn't ticked yet), and asserting here would
		// slam the multi-game idle list on for a beat and grab the session inside the owner-report lag the warm-up
		// gate exists to respect. LegitMode is known immediately, so this never fires on a human account.
		if (!Cfg.LegitMode && !PlayingBlocked) {
			BotManager.ModuleOf<Modules.Idler>(this)?.Assert();
		}
	}

	private void OnLoggedOff(SteamUser.LoggedOffCallback cb) {
		_lastLogOnResult = cb.Result > EResult.OK ? cb.Result : EResult.Invalid;

		if (cb.Result == EResult.LoggedInElsewhere) {
			// The owner just started playing on this account, so Steam handed them the session. Expected, not a
			// fault - so it reads as Info, in plain language, and says it will come back. The actual rejoin is
			// kept quiet in OnDisconnected so this is the only line the user sees for a normal step-aside.
			Log.Info("you're on this account now - standing aside until you're done", Name);

			if (Cfg.PauseWhenYouPlay) {
				PlayingBlocked = true;
			}
		} else {
			Log.Warn(new Said("logged off: {0}", cb.Result), Name);
		}

		State = BotState.Reconnecting;
		StatusText = cb.Result == EResult.LoggedInElsewhere ? "you're playing" : "reconnecting";
		OnlineSince = null;
	}

	/// <summary>
	/// SteamKit hands callbacks to an `async void`, which is the one signature whose exceptions nobody
	/// can catch: an escaped throw here goes to the thread pool and takes the process with it, with no
	/// log line to say why. The body lives in a Task below so it can be wrapped.
	/// </summary>
	private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
		try {
			await OnDisconnectedAsync(callback).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Error(new Said("the disconnect handler failed: {0}: {1}", e.GetType().Name, e.Message), Name);
		}
	}

	private async Task OnDisconnectedAsync(SteamClient.DisconnectedCallback cb) {
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
		_mismatchedSince = null;
		_personaFightSince = null;   // the next session's own echo decides whether anybody else is writing it
		_contestRetried = false;

		_heartbeat?.Dispose();
		_heartbeat = null;

		await StopModulesAsync().ConfigureAwait(false);

		if (!_running) {
			State = BotState.Stopped;
			StatusText = "stopped";

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
					await Task.Delay(hop, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

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
					await Limiters.ServeLoginCooldownAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

					break;
				case EResult.LoggedInElsewhere: {
					// The owner is playing on this account - OnLoggedOff already said so in plain words. Rejoin
					// after a short wait and then stand down until they finish. Kept at Debug so the user does NOT
					// see a scary "disconnected - reconnecting" line for something working exactly as intended.
					int idle = Math.Max(1, Live.Global.ReconnectDelaySeconds);
					TimeSpan rejoin = TimeSpan.FromSeconds(Rng.Next(idle, idle * 2));
					StatusText = "you're playing";
					Log.Debug(new Said("rejoining in ~{0}s, then standing down until you're done", (int) rejoin.TotalSeconds), Name);
					await Task.Delay(rejoin, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

					break;
				}

				default:
					await BackOffAndWaitAsync().ConfigureAwait(false);

					break;
			}

			if (!_running) {
				return;
			}

			// Never reconnect while a login attempt is still in flight - that's how you end up with two password
			// prompts stacked on top of each other.
			while (Volatile.Read(ref _loggingIn) == 1) {
				await Task.Delay(1000, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
			}

			await Limiters.WaitForLoginSlotAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

			if (_running && !Client.IsConnected) {
				Client.Connect();
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

			await Task.Delay(back, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
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
		_heartbeat = new Timer(_ => _ = HeartbeatAsync(), null, TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
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
			Log.Info("you're on this account now - standing aside until you're done", Name);
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
			} catch {
				// cosmetic - never worth disturbing the heartbeat over
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
			Log.Info(new Said("custom name slipped (Steam shows {0}) - re-asserting it", PlayingAsSeen), Name);

			try {
				// The same label that was announced - never the configured name over a farmer that chose none.
				SetPlaying(PlayingApps, announced, force: true);
			} catch {
				// next heartbeat tries again
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
		// timeout - a genuinely wedged, half-open socket. A PlayingBlocked account skips the persona re-assert, so
		// there _lastPacket carries it alone, which is right: a session its owner is actively using is never idle.
		// A truly dead socket is still caught quickly by SteamKit's own heartbeat, which raises OnDisconnected.
		//
		// This used to PROBE by requesting our own account's profile info and awaiting the reply - a self-directed
		// friends/profile call, the same family of request that signed the owner out of Friends & Chat - so it is
		// gone for good. Watching traffic we already have detects the same dead connection without touching friends.
		// The re-assert runs every 3-10 minutes, not every minute as this was written for - so it only counts as
		// silent once it has missed its longest gap by the timeout. Comparing it to the bare timeout reconnected a
		// quiet (warming-up or invisible) account a few minutes after every sign-in, for nothing.
		if ((DateTime.UtcNow.Subtract(_lastPacket).TotalSeconds >= ConnectionTimeoutSeconds)
			&& (DateTime.UtcNow.Subtract(_lastPersonaAssert).TotalSeconds >= PersonaAssertMaxSeconds + ConnectionTimeoutSeconds)) {
			Log.Warn("connection went quiet - reconnecting", Name);

			try {
				Client.Disconnect();   // OnDisconnected drives the backoff + reconnect
			} catch {
				// already gone - OnDisconnected picks it up
			}
		}
	}

	// ── pushes ──────────────────────────────────────────────────────────────
	private void OnPlayingSessionState(SteamUser.PlayingSessionStateCallback cb) {
		_lastPacket = DateTime.UtcNow;

		// PlayingAppID is what a session OTHER than this one claims to be running. Steam allows one playing
		// session per account, so a non-zero value here means our games-played is accepted and then quietly
		// ignored - which from the inside looks identical to it never having been sent.
		Log.Debug(new Said("Steam says: blocked={0}, other session playing app {1}", cb.PlayingBlocked, cb.PlayingAppID), Name);

		// The opt-out only suppresses standing DOWN. It must never suppress standing back up: doing that once
		// latched the flag on forever, so the setting that promises "don't stand down" was the one that made
		// standing down permanent.
		if (cb.PlayingBlocked && !Cfg.PauseWhenYouPlay) {
			return;
		}

		if (cb.PlayingBlocked == PlayingBlocked) {
			return;
		}

		PlayingBlocked = cb.PlayingBlocked;

		if (PlayingBlocked) {
			Playing = "";
			IsFarming = false;

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
				Log.Debug(new Said("Steam reports another session on app {0} right after logon - probably our own, waiting before saying so", cb.PlayingAppID), Name);
			} else {
				_blockWarnDue = null;
				Log.Info("you're on this account now - standing aside until you're done", Name);
			}
		} else {
			_blockWarnDue = null;
			// A courtesy pause: coming straight back the instant Steam frees the session is what makes an idler
			// feel like it's fighting you for your own account.
			// Human mode takes its time about it - somewhere from the delay to three times it, not the same minute count every time.
			int delay = Math.Max(0, Cfg.ResumeDelayMinutes);
			_resumeAt = DateTime.UtcNow + (Cfg.LegitMode && (delay > 0) ? Rng.HumanMinutes(delay, delay * 3) : TimeSpan.FromMinutes(delay));

			if (Cfg.ResumeDelayMinutes > 0) {
				Log.Info(new Said("the account is free again - picking back up in {0}m", Cfg.ResumeDelayMinutes), Name);
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

	private void OnLicenseList(SteamApps.LicenseListCallback cb) {
		if (cb.Result != EResult.OK) {
			return;
		}

		lock (_licenses) {
			foreach (SteamApps.LicenseListCallback.License license in cb.LicenseList) {
				// A family member's licence carries their account ID; ours carries ours (or 0 on older licences).
				bool own = (license.OwnerAccountID == 0) || (license.OwnerAccountID == (uint) (SteamId & 0xFFFFFFFF));
				// A game somebody GIFTED arrives marked "guest pass" - not paid for by this account, but paid for by
				// the giver, who can still have it refunded under Steam's usual two hours / fourteen days. A real guest
				// pass (a timed trial) carries the same payment method, so what tells them apart is the licence
				// itself: a gift is a permanent single purchase with no time limit, a trial is not. Seen on real
				// gifts (Riders Republic, Steep): GuestPass, SinglePurchase, flags None, minute limit 0.
				_licenses[license.PackageID] = (license.TimeCreated, license.AccessToken, IsPaid(license.PaymentMethod), own,
					(license.PaymentMethod == EPaymentMethod.GuestPass) && (license.LicenseType == ELicenseType.SinglePurchase)
						&& (license.MinuteLimit == 0) && !license.LicenseFlags.HasFlag(ELicenseFlags.Expired));
			}

			_appOwnedSince = null;   // the mapping is stale now
			_licenseGeneration++;
		}

		Log.Debug(new Said("{0} licence(s) known", cb.LicenseList.Count), Name);

		// What the last fortnight's licences were, by how Steam says they were paid for - refund protection
		// decides from exactly this, so when it holds (or doesn't hold) a game the reason is in the log.
		foreach (SteamApps.LicenseListCallback.License license in cb.LicenseList.Where(static l => DateTime.UtcNow - l.TimeCreated < TimeSpan.FromDays(14))) {
			Log.Debug(new Said("recent licence: package {0}, {1}, {2}", license.PackageID, license.PaymentMethod, license.TimeCreated.ToString("d")) + $" · type {license.LicenseType}, flags {license.LicenseFlags}, minute limit {license.MinuteLimit}, used {license.MinutesUsed}", Name);
		}
	}

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
	/// When each owned app was first licensed to this account and whether it was paid for, worked out by asking
	/// Steam what is inside each owned package. Only used for refund protection, so it is built lazily and only
	/// when something asks - resolving thousands of packages on every login for a setting most people leave off
	/// would be rude.
	///
	/// Returns empty on any failure, which means "don't skip anything" rather than "skip everything".
	/// </summary>
	internal async Task<IReadOnlyDictionary<uint, AppOwnership>> GetAppOwnershipAsync() {
		Dictionary<uint, (DateTime Created, ulong Token, bool Paid, bool Own, bool Gift)> snapshot;
		int generation;

		lock (_licenses) {
			if (_appOwnedSince != null) {
				return _appOwnedSince;
			}

			snapshot = new Dictionary<uint, (DateTime Created, ulong Token, bool Paid, bool Own, bool Gift)>(_licenses);
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

			if (pages == null) {
				return map;
			}

			foreach (SteamApps.PICSProductInfoCallback page in pages) {
				foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo package in page.Packages.Values) {
					if (!snapshot.TryGetValue(package.ID, out (DateTime Created, ulong Token, bool Paid, bool Own, bool Gift) license)) {
						continue;
					}

					List<KeyValue>? appIds = package.KeyValues["appids"]?.Children;

					foreach (KeyValue app in appIds ?? []) {
						uint appId = app.AsUnsignedInteger();

						if (appId == 0) {
							continue;
						}

						// Earliest licence wins - that's when you really got it. A game can also arrive twice (a free
						// weekend, then the purchase), and if EITHER licence was paid for the refund clock is real.
						if (!map.TryGetValue(appId, out AppOwnership existing)) {
							map[appId] = new AppOwnership(license.Created, license.Paid, license.Own, license.Gift);
						} else {
							map[appId] = new AppOwnership(
								license.Created < existing.Since ? license.Created : existing.Since,
								existing.Paid || license.Paid,
								existing.Own || license.Own,
								existing.Gift || license.Gift);
						}
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't work out when games were bought ({0}) - refund protection is off this round", e.Message), Name);

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

		if (cb.NewItems == 0) {
			return;
		}

		Log.Debug(new Said("Steam pushed {0} new item(s)", cb.NewItems), Name);
		SignalItemDrop();

		// A farming account trips Steam's green "new items" counter dozens of times a day and it stays lit
		// forever, which is both irritating and an obvious tell. Marking the inventory viewed clears it - at most
		// every 45 minutes: the inventory page is heavy, and loading it on every single card drop cost far more
		// than a counter that reads 3 for a while.
		if (Cfg.ClearInventoryNotifications && (Interlocked.Exchange(ref _inventoryVisitQueued, 1) == 0)) {
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


					_inventoryVisitedAt = DateTime.UtcNow;
					await Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{SteamId}/inventory/")).ConfigureAwait(false);
				} catch {
					// cosmetic - never let it matter
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
		if (seen <= Volatile.Read(ref _tradeOffersWaiting)) {
			Volatile.Write(ref _tradeOffersWaiting, seen);
		} else if (Volatile.Read(ref _tradeOffersWaiting) < 0) {
			Volatile.Write(ref _tradeOffersWaiting, seen);
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

		Log.Event(new Said("somebody just commented on this profile - steamcommunity.com/profiles/{0}", SteamId), Name);

		// Read it, so the counter goes back to zero rather than climbing for the life of the account.
		ClearAllNotifications();
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
				Log.Debug(new Said("couldn't mark notifications read: {0}", e.Message), Name);
			}

			// The two older counters, which the tray message does not touch. Loading the page is what clears them.
			foreach (string page in (string[]) [$"/profiles/{SteamId}/commentnotifications/", $"/profiles/{SteamId}/inventory/"]) {
				try {
					await Web.GetAsync(new Uri(WebSession.Community, page)).ConfigureAwait(false);
				} catch {
					// cosmetic - never let it matter
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

	// ── tokens ──────────────────────────────────────────────────────────────
	/// <summary>The web access token is treated as live for its whole span except the last few minutes.</summary>
	private const int AccessTokenSlackMinutes = 5;

	/// <summary>Record a web access token and read its expiry out of the JWT so reuse can be judged.</summary>
	private void SetAccessToken(string? token) {
		_accessToken = string.IsNullOrEmpty(token) ? null : token;
		_accessTokenValidUntil = _accessToken == null ? null : WebSession.ReadJwtExpiryOf(_accessToken);

		// A token we cannot read the expiry of is worse than useless - we would reuse it forever. Drop it.
		if ((_accessToken != null) && (_accessTokenValidUntil == null)) {
			_accessToken = null;
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

		try {
			bool rejected = remint && (DateTime.UtcNow - _lastRemint > TimeSpan.FromMinutes(10));

			if (rejected) {
				_lastRemint = DateTime.UtcNow;
			}

			// Still good for more than the slack window - hand back exactly what we already have. No network call,
			// no new session, nothing for Steam to arbitrate against the owner's client.
			if (!rejected && !string.IsNullOrEmpty(_accessToken) && _accessTokenValidUntil.HasValue
				&& (_accessTokenValidUntil.Value > DateTime.UtcNow.AddMinutes(AccessTokenSlackMinutes))) {
				Log.Debug(new Said("reusing web token (good for {0}h) - no new web session", ((_accessTokenValidUntil.Value - DateTime.UtcNow).TotalHours).ToString("0.#")), Name);

				return _accessToken;
			}

			if (string.IsNullOrEmpty(_refreshToken)) {
				return null;
			}

			Log.Info("minting a new web token (the old one is spent) - this is the once-a-day web refresh", Name);

			// Genuinely spent (or never had one). Mint a replacement. allowRenewal: true lets Steam rotate the
			// long-lived refresh token before it ages out, so an unattended farmer keeps
			// running for months without the password.
			AccessTokenGenerateResult result = await Client.Authentication.GenerateAccessTokenForAppAsync(SteamId, _refreshToken, true).ConfigureAwait(false);

			if (!string.IsNullOrEmpty(result.RefreshToken) && result.RefreshToken != _refreshToken) {
				_refreshToken = result.RefreshToken;   // Steam rotated it; keeping the old one would lock us out
				TokenStore.Save(Name, _refreshToken);
			}

			if (!string.IsNullOrEmpty(result.AccessToken)) {
				SetAccessToken(result.AccessToken);
				TokenStore.SaveAccess(Name, result.AccessToken);
			}

			return _accessToken;
		} catch (Exception e) {
			Log.Warn(new Said("couldn't get a web token: {0}", e.Message), Name);

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
		// This makes "old/kylro should never leave 💀nocat.lol💀" checkable: if the custom name ever lapses to a
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
			_mismatchedSince = null;   // nothing to slip when no name is being shown
		}

		if (relaunch) {
			Client.Send(BuildGamesPlayed(null, []));
			Log.Debug("game list changed - restarting the session so the custom name stays on top", Name);

			_ = Task.Run(async () => {
				try {
					await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

					if ((Volatile.Read(ref _playSequence) != mine) || (State != BotState.Online) || PlayingBlocked || Paused) {
						return;   // superseded, or no longer ours to drive - whoever won will announce its own
					}

					// Cfg.GameDevice, same as the ordinary path below. Leaving it off defaulted the device to 0
					// ("desktop, legacy") on every re-announce, quietly discarding whatever the account was set
					// to - and now that a login takes this path too, that was every session.
					Client.Send(BuildGamesPlayed(label, apps, Cfg.GameDevice));
					ApplyPersona();
				} catch (Exception e) {
					Log.Debug(new Said("couldn't re-announce after the game list changed: {0}", e.Message), Name);
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
	private static ClientMsgProtobuf<CMsgClientGamesPlayed> BuildGamesPlayed(string? label, List<uint> apps, int device = 0) {
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

					// Something newer took the session - it will apply its own.
					if ((Volatile.Read(ref _playSequence) != mine) || (State != BotState.Online)) {
						return;
					}

					ApplyPersona();
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't re-apply the persona: {0}: {1}", e.GetType().Name, e.Message), Name);
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
		} catch {
			// non-fatal
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
		await StopAsync().ConfigureAwait(false);
		Web.Dispose();
		_tokenLock.Dispose();
	}
}

/// <summary>When an app first appeared on the account, and whether it was actually bought.</summary>
/// <param name="Own">At least one of the licences is the account's own, not a family member's.</param>
public readonly record struct AppOwnership(DateTime Since, bool Paid, bool Own, bool Gift = false) {
	/// <summary>Somebody could lose money if it's played: bought by this account, or gifted and still refundable to the giver.</summary>
	public bool Refundable(bool gifts) => Paid || (Gift && gifts);
}

public static class SteamIds {
	/// <summary>GameID layout: bits 0-23 appID, 24-31 type, 32-63 modID. Type 2 = Shortcut, i.e. a non-Steam game.</summary>
	public const ulong ShortcutGameId = (2UL << 24) | (0xFFFFFFFFUL << 32);

	/// <summary>Steam's own ceiling on simultaneous games. Send more and it drops the message.</summary>
	/// <summary>LaunchTypeGamepad | LaunchTypeCompatTool - the pair Steam reads as "this is a Deck".</summary>
	public const int DeviceSteamDeck = 12288;

	/// <summary>Proton Experimental's appID - what a Deck reports as the compat tool for a Windows title.</summary>
	public const uint ProtonExperimental = 1493710;

	/// <summary>ESteamInputType for the Deck's own built-in controller.</summary>
	public const int ControllerTypeSteamDeck = 13;

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
				Log.Info("answered Steam Guard from this account's own authenticator", botName);

				return code;
			}
		} else {
			Log.Warn("the authenticator secret for this account isn't producing codes Steam accepts - check it", botName);
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
		Log.Attention("approve this login in the Steam mobile app (or type the code below)", botName);

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

			string stored = File.ReadAllText(path).Trim();
			string plain = Secrets.Unprotect(stored);

			if (Secrets.IsPlain(stored) && Secrets.Available && (plain.Length > 0)) {
				// Its own try: a file that can't be written (read-only, locked, someone else's volume) still holds a
				// good token - losing it would mean typing the password again.
				try {
					string bot = Path.GetFileNameWithoutExtension(path);
					AtomicFile.Write(path, Secrets.Protect(plain, bot));
					Log.Info(new Said("encrypted a login token that was stored as plain text by an older version"), bot);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Debug(new Said("couldn't encrypt a login token in place: {0}", e.Message));
				}
			}

			return plain.Length > 0 ? plain : null;
		} catch {
			return null;
		}
	}

	public static void Save(string bot, string token) {
		try {
			Directory.CreateDirectory(Dir);
			AtomicFile.Write(PathFor(bot), Secrets.Protect(token, bot));
		} catch (Exception e) {
			Log.Warn(new Said("couldn't store the login token: {0}", e.Message), bot);
		}
	}

	public static void SaveAccess(string bot, string accessToken) {
		try {
			Directory.CreateDirectory(Dir);
			AtomicFile.Write(AccessPathFor(bot), Secrets.Protect(accessToken, bot));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't store the access token: {0}", e.Message), bot);
		}
	}

	public static void Clear(string bot) {
		foreach (string path in new[] { PathFor(bot), AccessPathFor(bot) }) {
			try {
				if (File.Exists(path)) {
					File.Delete(path);
				}
			} catch {
				// nothing to do
			}
		}
	}
}
