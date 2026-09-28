using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Plays like a person instead of like a bot.
///
/// What makes an account look real is not one setting, it is the shape of a WEEK:
///
///   • Weekdays are evenings; weekends start earlier and run longer, and Friday and Saturday nights go late
///     because there is nothing on tomorrow.
///   • Some days it does not play at all. Somebody who games exactly every single day is a bot.
///   • Side games arrive in BURSTS, not a daily drip. Most days are pure main game; occasionally there is a real
///     sitting on something else. A guaranteed 15% of the same side game every single day is a signature.
///   • Sessions are one continuous block in one game. Breaks are usually a quick tab-out, sometimes a proper step
///     away, and meals land around actual meal times.
///   • It signs out for some of those breaks, because real people close Steam.
///
/// Every number here is rolled fresh each morning, inside the bands the settings give it. Nothing is on a fixed
/// schedule, and two accounts with identical settings will not have the same day.
///
/// Overnight it can still bank hours: invisible, so nobody sees it, and with as many games at once as you like,
/// because there is nobody to see how many. That is the one place "one game at a time" buys nothing.
/// </summary>
public sealed class HumanMode(Bot bot) : BotModule(bot) {
	public enum Phase { Off, WarmingUp, Playing, ShortBreak, MealBreak, SwitchingGame, Asleep, NightIdle, DoneForToday, DayOff, StoodDown }

	/// <summary>
	/// The hard floor before a game may be launched, however short the warm-up is set.
	///
	/// This is a safety gate, not a disguise. Steam reports back that the OWNER of the account has started
	/// playing with a lag of up to about two and a half minutes; launch inside that window and nocatFarm can
	/// take the session off a real person who just sat down at their own PC. Three minutes clears the observed
	/// lag with room to spare, and it is the one number here that no setting is allowed to shorten.
	/// </summary>
	private const int SafetyGateSeconds = 180;

	private readonly Random _rng = new();

	// ── today's plan, re-rolled every morning ──
	private int _dayStamp = -1;
	private int _targetMinutes;          // minutes it means to actually PLAY today. 0 = a day off
	private int _mainSharePct;           // the main game's share of today
	private int _otherBudget;            // today's whole allowance for side games. 0 = a pure main-game day
	private int _otherPlayed;
	private int _signOutCap;
	private int _signOutsUsed;
	private int _mealCap;
	private int _mealsUsed;
	private int _wakeMinuteOfDay;
	private int _bedHour;
	private int _bedMinute;
	private bool _bedIsTomorrow;

	// ── the session in progress ──
	private Phase _phase = Phase.Off;
	private uint _game;

	/// <summary>
	/// The last game it actually played, which outlives the session.
	///
	/// <see cref="_game"/> means "launched right now" and every break, bedtime and stand-down clears it - so
	/// reading it to decide whether to carry on with the same game always said "no game", and the 40% carry-on
	/// and the switching-games gap could never fire. A person carries on across a break; this remembers that.
	/// </summary>
	private uint _lastGame;

	/// <summary>
	/// The game the current "closing the game" gap is FOR.
	///
	/// The gap has to commit to a decision. Without this the gap was a re-decision point, and a decision that can
	/// send you back to the same decision is a loop, not a delay.
	/// </summary>
	private uint _switchingTo;

	private DateTime _sessionStarted;
	private DateTime _sessionEnds;

	/// <summary>
	/// How far the day's minute count has already been credited.
	///
	/// Deliberately NOT _sessionStarted. Banking runs on every tick and has to advance a cursor by the whole
	/// minutes it just credited, or partial minutes are thrown away and the day never accrues. But
	/// _sessionStarted is also what the board reads to say "23m of 41m" - so while the two shared one field,
	/// banking dragged the start forward to roughly now and the board read "0m of 36m" forever, with the
	/// second number counting DOWN as the start crept toward the end.
	/// </summary>
	private DateTime _bankedTo;

	/// <summary>Which logon the banking cursor belongs to, so an outage is never credited as play.</summary>
	private DateTime _bankedForLogon = DateTime.MinValue;
	private DateTime _phaseEnds;
	private int _playedMinutesToday;
	private bool _firstSessionOfDay = true;
	private readonly Dictionary<uint, int> _minutesByGame = [];

	// ── settling in ──
	// Nobody signs in and launches a game in the same second. This is when the account is allowed to start, and
	// it is re-armed on every fresh login and every time human mode takes the account over.
	private DateTime _playingAssertedFor = DateTime.MinValue;
	private bool _assertedOnce;
	private DateTime _readyAt = DateTime.MinValue;
	private DateTime _gateArmedFor = DateTime.MinValue;
	private bool _announcedWarmUp;
	private bool _warmedUp;

	/// <summary>The card farmer has the account for the night - what the night status says instead of banking hours.</summary>
	private bool _nightFarming;

	/// <summary>The sitting in progress is a card-farming one: the farmer plays, this keeps the day's shape around it.</summary>
	private bool _farmSession;

	/// <summary>Playing on after the last card, and the game still has to be confirmed running once the farmer lets go.</summary>
	private bool _playOnPending;

	/// <summary>Whether it's waking hours right now, whatever phase it's in - night farming hands back at this.</summary>
	public bool AwakeHoursNow => InWakingHours(DateTime.Now);

	/// <summary>A grind running in the night: the account looks asleep (invisible), so it acts asleep too.</summary>
	private bool NightGrind => _wasGrinding && (_phase == Phase.Playing) && (_game == 0) && !InWakingHours(DateTime.Now);

	/// <summary>Cards farm in this day's sittings (the default for a human-mode account), not flat out.</summary>
	private bool FarmInDay => Bot.EffectiveFarmCards && FarmWhen.InSittings(Bot.EffectiveFarmWhen);

	/// <summary>
	/// A card-farming sitting is open, so the farmer may play. It closes for every break, meal, bedtime and stand-down,
	/// and the farmer hands the account back within seconds when it does.
	/// </summary>
	public bool FarmSittingOpen => Bot.Cfg.LegitMode && (_phase == Phase.Playing) && _farmSession;

	/// <summary>The game the next sitting farms, or 0 when this sitting should play the usual games.</summary>
	/// Mixed rolls this per sitting: CardSittingsPct of them farm, the rest play the usual games.
	private uint FarmGameNow() {
		if (!FarmInDay || (Bot.CardsRemaining <= 0) || (BotManager.ModuleOf<CardFarmer>(Bot) is not { InFarmWindowNow: true } farmer)) {
			return 0;
		}

		// A drop run ('drops' on this account) gets real weight: the main game's share of the sittings, or the
		// card-sittings share if that's higher - still one sitting at a time, with every break and bedtime.
		int share = Math.Clamp(Bot.Cfg.CardSittingsPct, 5, 95);

		if (Bot.DropsFirstActive) {
			share = Math.Max(share, Math.Clamp(_mainSharePct > 0 ? _mainSharePct : 65, 5, 95));
		}

		if ((Bot.EffectiveFarmWhen == FarmWhen.Mixed) && !Chance(share / 100.0)) {
			return 0;
		}

		if (Bot.DropsFirstActive && farmer.Queue.Any(g => (g.AppId == Bot.DropsFirstApp) && (g.CardsRemaining > 0))) {
			return Bot.DropsFirstApp;
		}

		return farmer.NextGame;
	}

	/// <summary>How many clear reads in a row, one per tick, before a game may go on.</summary>
	private const int SafetyReadsNeeded = 4;

	/// <summary>The sign-in the owner-safety reads belong to, and how many clear ones it has had in a row.</summary>
	private DateTime _safetyArmedFor = DateTime.MinValue;
	private int _safetyReads;

	private bool _wasFarming;
	private bool _wokeUp;
	private bool _wasGrinding;
	private bool _settlingAfterGrind;   // the settle in progress follows a grind, not a login or a wake-up
	private int _clearReads;

	public override string Name => "human";

	public override string Status {
		get {
			if (!Bot.Cfg.LegitMode) {
				return Loc.T("off");
			}

			return _phase switch {
				Phase.WarmingUp => _settlingAfterGrind ? Loc.T("done grinding · settling for {0}", Left(_readyAt))
					: _wokeUp ? Loc.T("awake for the day · settling for {0}", Left(_readyAt))
					: Loc.T("just signed in · settling for {0}", Left(_readyAt)),
				// A grind keeps the Playing phase so its hours count toward the day, but with no game of its own - so
				// this read "nothing · 1m left" for the whole grind. Name what is actually running.
				// Keyed on the grind human mode is still handling rather than on Bot.Grinding, which goes false the
				// moment the timer runs out - up to a tick before the "done grinding" step below takes over.
				Phase.Playing when (_game == 0) && _wasGrinding && (Bot.GrindGame != 0) =>Loc.T("grinding {0} · {1} left · {2}/{3} today", GameName(Bot.GrindGame), Left(_sessionEnds), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes)),
				Phase.Playing when _farmSession => Loc.T("farming cards on {0} · {1} left · {2}/{3} today", GameName(_game), Left(_sessionEnds), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes)),
				Phase.Playing => Loc.T("{0} · {1} left · {2}/{3} today", GameName(_game), Left(_sessionEnds), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes)),
				Phase.SwitchingGame => _switchingTo != 0 ? Loc.T("closing the game, then {0}", GameName(_switchingTo)) : Loc.T("closing the game"),
				Phase.ShortBreak => Loc.T("short break · back in {0}", Left(_phaseEnds)),
				Phase.MealBreak => Loc.T("meal break · back in {0}", Left(_phaseEnds)),
				Phase.NightIdle or Phase.Asleep when _nightFarming => Loc.T("asleep, the card farmer is working · up {0}", (NextWakeTime()).ToString("HH:mm")),
				Phase.NightIdle => Loc.T("asleep, banking hours quietly on {0} game(s) · up {1}", NightGames().Count, (NextWakeTime()).ToString("HH:mm")),
				Phase.Asleep => Loc.T("asleep · up {0}", (NextWakeTime()).ToString("HH:mm")),
				Phase.DoneForToday => Loc.T("done for today ({0}) · back {1}", Fmt.Hm(_playedMinutesToday), (NextWakeTime()).ToString("HH:mm")),
				Phase.DayOff => Loc.T("not playing today · back {0}", (NextWakeTime()).ToString("HH:mm")),
				Phase.StoodDown => Loc.T("standing down, you're using it"),
				_ => Loc.T("starting up")
			};
		}
	}

	public Phase Current => Bot.Cfg.LegitMode ? _phase : Phase.Off;
	public int PlayedMinutesToday => _playedMinutesToday;
	public int TargetMinutesToday => _targetMinutes;
	public uint PlayingNow => _phase == Phase.Playing ? _game : 0;

	/// <summary>The account's headline game. The achievement pacer deliberately never writes to this one.</summary>
	public uint MainGameId => MainGame();

	/// <summary>
	/// In bed: invisible on the friends list, and nobody can see what it is running.
	///
	/// This is the one window where the ordinary "look like a person" rules stop applying, because there is
	/// nobody to look. During the day the account plays one game at a time because forty at once is the oldest
	/// tell there is - but at 4am, invisible, that argument does not hold, and the card farmer can work
	/// properly instead of the account sitting on a fixed list of games chosen months ago.
	/// </summary>
	public bool InBed => _phase is Phase.Asleep or Phase.NightIdle;

	/// <summary>Minutes until tonight's bedtime while it's awake; null outside waking hours.</summary>
	public int? MinutesToBed {
		get {
			if (!InWakingHours(DateTime.Now)) {
				return null;
			}

			// After midnight BedTime() (built from today's date) can land a day late - bring it back into range.
			int minutes = (int) (BedTime() - DateTime.Now).TotalMinutes;

			return minutes > 20 * 60 ? minutes - (24 * 60) : minutes;
		}
	}

	/// <summary>
	/// True once the post-login warm-up has finished - the card farmer waits on this so a human-mode account
	/// settles in first (gets online for a bit) and only then starts farming, exactly like it would before
	/// playing. Falls back to a time check for days it never enters the play warm-up (a day off), so the farmer
	/// is never stuck waiting.
	/// </summary>
	/// <summary>Human mode has run at least once since starting, so its phase says something (not the Off it starts in).</summary>
	private bool _ticked;

	/// <summary>A break it's spending offline: to the friends list the account is signed out, so it does nothing visible.</summary>
	private bool _offlineBreak;

	/// <remarks>
	/// Only counts for the sign-in it was earned on. A day off or a finished day never runs the warm-up, so the flag
	/// set that morning survived a reconnect and the farmer started the moment the account came back - inside the
	/// window where Steam may not yet have said the owner is playing. The time fallback needs the owner-safety
	/// check to have passed on this sign-in too.
	/// </remarks>
	public bool WarmedUp => (_warmedUp && (Bot.OnlineSince is { } armed) && (_gateArmedFor == armed))
		|| (SafeToPlay && (Bot.OnlineSince is { } on) && (DateTime.UtcNow >= on.AddSeconds(SafetyGateSeconds).AddMinutes(Math.Max(1, Bot.Cfg.WarmUpMaxMinutes))));

	/// <summary>
	/// The owner-safety half of settling in on its own: three minutes past this sign-in and several clear reads in a
	/// row that the owner isn't playing. Nothing about how it looks - the night uses this, because at night nobody
	/// sees the account, but a game started there can still take the session off somebody who just sat down.
	/// </summary>
	public bool SafeToPlay => (Bot.OnlineSince is { } on) && (_safetyArmedFor == on) && (_safetyReads >= SafetyReadsNeeded)
		&& (DateTime.UtcNow >= on.AddSeconds(SafetyGateSeconds));

	/// <summary>Rough minutes until the post-login warm-up finishes, for status display (0 once it's done).</summary>
	public int WarmUpMinutesLeft {
		get {
			if (_warmedUp) {
				return 0;
			}

			double mins = (_readyAt - DateTime.UtcNow).TotalMinutes;

			return mins <= 0 ? 0 : (int) Math.Ceiling(mins);
		}
	}

	/// <summary>
	/// Skip the rest of the night and start the day now. Pulls today's wake time up to this minute so it counts as
	/// awake, drops the invisible-for-night persona, and falls back to the ordinary wake path (a short settle,
	/// then play/farm) on the next tick. Bed time is untouched, so it still turns in at its usual hour.
	/// </summary>
	public void WakeNow() {
		int nowMin = (int) (DateTime.Now - DateTime.Now.Date).TotalMinutes;

		if (_wakeMinuteOfDay > nowMin) {
			_wakeMinuteOfDay = nowMin;
		}

		_wokeUp = true;
		_phase = Phase.Off;
		ClearBreakState();

		// The overnight games off first. Made visible with them still running, the friends list showed the whole
		// night's list for up to a tick before the wake-up settle put them down.
		if (!Bot.IsFarming) {
			Bot.StopPlaying();
		}

		Bot.ClearPersonaOverride();

		// It's a manual "start now", so skip the random settle - but NOT the owner-safety check. Clearing the
		// timed part of the warm-up lets it start as soon as the clear-reads confirm the owner isn't mid-game,
		// rather than sitting through a fresh ~15-minute settle.
		//
		// Armed here for this sign-in, or SettledIn re-arms (bedtime clears the stamp) and rolls a full settle
		// anyway - which is what 'wake' always quietly did.
		DateTime loggedOn = Bot.OnlineSince ?? DateTime.UtcNow;
		DateTime safety = loggedOn.AddSeconds(SafetyGateSeconds);

		_gateArmedFor = loggedOn;
		_clearReads = 0;
		_announcedWarmUp = false;
		_warmedUp = false;
		_readyAt = safety > DateTime.UtcNow ? safety : DateTime.UtcNow;
	}

	/// <summary>How long the current sitting has been running, and how long it is meant to run.</summary>
	public (int Elapsed, int Total) Session {
		get {
			if (_phase != Phase.Playing) {
				return (0, 0);
			}

			int total = (int) Math.Max(1, (_sessionEnds - _sessionStarted).TotalMinutes);
			int elapsed = (int) Math.Clamp((DateTime.UtcNow - _sessionStarted).TotalMinutes, 0, total);

			return (elapsed, total);
		}
	}

	/// <summary>What it is doing, in two or three words, with no timings - the board adds those in its own columns.</summary>
	/// <remarks>
	/// This is the main line on every account card, the console board and the tray window, and it was the last
	/// piece of the status readout still in English in every language. The pass that translated statuses looked
	/// for `_status = ...` and `override string Status`; nothing was looking for a property called Doing.
	/// </remarks>
	public Said Doing => _phase switch {
		Phase.WarmingUp => new Said("settling in"),
		Phase.Playing when _farmSession => new Said("farming cards on {0}", GameName(_game)),
		Phase.Playing => new Said("playing {0}", GameName(_game)),
		Phase.SwitchingGame => _switchingTo != 0 ? new Said("closing, then {0}", GameName(_switchingTo)) : new Said("closing the game"),
		Phase.ShortBreak => new Said("on a break"),
		Phase.MealBreak => new Said("meal break"),
		Phase.NightIdle or Phase.Asleep when _nightFarming => new Said("asleep, the card farmer is working"),
		Phase.NightIdle => new Said("asleep, banking hours on {0} game(s)", NightGames().Count),
		Phase.Asleep => new Said("asleep"),
		Phase.DoneForToday => new Said("done for today"),
		Phase.DayOff => new Said("not playing today"),
		Phase.StoodDown => new Said("waiting - you're playing on this account"),
		_ => new Said("starting up")
	};

	/// <summary>When it next expects to be doing something else - the end of a break, or getting up.</summary>
	public DateTime? NextChange => _phase switch {
		Phase.Playing => _sessionEnds,
		Phase.ShortBreak or Phase.MealBreak or Phase.SwitchingGame => _phaseEnds,
		Phase.WarmingUp => _readyAt,
		Phase.NightIdle or Phase.Asleep or Phase.DoneForToday or Phase.DayOff => NextWakeTime().ToUniversalTime(),
		_ => null
	};

	/// <summary>Today so far, biggest first - what the console's <c>human</c> command prints.</summary>
	public IEnumerable<(uint Game, int Minutes)> TodayByGame() {
		// Copied under the lock. This is read from the console and the web thread while the module loop is
		// writing to and clearing the same dictionary, and enumerating it live throws rather than printing.
		lock (_minutesByGame) {
			return _minutesByGame.OrderByDescending(static kv => kv.Value).Select(static kv => (kv.Key, kv.Value)).ToList();
		}
	}

	private int Rng(int lo, int hi) => hi <= lo ? lo : _rng.Next(lo, hi + 1);   // inclusive, like the plugin's helper
	private bool Chance(double p) => _rng.NextDouble() < p;
	private bool Percent(int pct) => _rng.Next(100) < pct;
	private static string Left(DateTime until) => Fmt.Hm((int) Math.Max(0, (until - DateTime.UtcNow).TotalMinutes));

	/// <summary>Bank and persist whatever is in flight before the module goes away.</summary>
	public override async Task StopAsync() {
		try {
			BankSession();
		} catch (Exception e) {
			Log.Debug(new Said("couldn't bank the session on shutdown: {0}", e.Message), Bot.Name);
		}

		await base.StopAsync().ConfigureAwait(false);
	}

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			if (!Bot.Cfg.LegitMode) {
				// Release on the flag as well as the phase. Human mode sits in Off for as long as the card farmer
				// works - hours, sometimes - and switching legit mode off during that left HumanOwned set for good:
				// the idler kept standing aside, the farmer kept to one game at a time, and the account's own games
				// never came back until a restart.
				if ((_phase != Phase.Off) || Bot.HumanOwned) {
					_phase = Phase.Off;
					Bot.HumanOwned = false;
					Bot.ClearPersonaOverride();

					// It is an ordinary idling account from here, and those switch straight away. Left to the idler's
					// own re-assert it sat on nothing (or the last human game) for up to seven minutes.
					BotManager.ModuleOf<Idler>(Bot)?.Assert();
				}

				if (!await Sleep(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			try {
				StepAsync();
			} catch (Exception e) {
				Log.Warn(new Said("human mode hiccup: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
			}

			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private void StepAsync() {
		if (!Bot.IsOnline) {
			return;
		}

		Bot.HumanOwned = true;
		RollNewDayIfNeeded();
		_ticked = true;

		// Every tick, whatever the phase, so the owner-safety reads are already counted when the night needs them.
		SafetyClear();

		// The human always yields to the actual human - and stays out of the way for the courtesy delay it
		// promised, rather than reappearing on the next twenty-second tick after saying "picking back up in 8m".
		if (!Bot.CanPlay && !Bot.Paused && !Bot.PlayingBlocked) {
			return;
		}

		// You, or a pause, before anything else - a grind included. A grind that ran on through it used to keep
		// banking every tick, so the day's hours were spent on time the account wasn't playing at all.
		if (Bot.Paused || Bot.PlayingBlocked) {
			if (_phase != Phase.StoodDown) {
				BankSession();
				_farmSession = false;
				_wasGrinding = false;   // a grind still running afterwards picks itself back up from scratch
				_phase = Phase.StoodDown;
				_game = 0;
				_switchingTo = 0;
				ClearBreakState();
				Bot.ClearPersonaOverride();
			}

			return;
		}

		// A grind outranks the schedule. Banked first, so the hours it puts in still count toward the day
		// rather than vanishing, and the phase is dropped so the day resumes cleanly when it expires.
		// A grind outranks the schedule. On a legit account it doesn't slam over instantly: for the first short
		// beat (GrindStartsAt) the current game keeps playing and the day carries on normally, so it looks like a
		// person finishing up and then switching games. On a non-human account it starts immediately.
		if (Bot.Grinding && (!Bot.HumanOwned || (DateTime.UtcNow >= Bot.GrindStartsAt))) {
			// Not settled in (a reconnect mid-grind, say): wait for it here. Falling through took the "grind just
			// finished" branch below, which said "done grinding" and re-rolled the day while the grind was still on.
			if (!SettledIn()) {
				return;
			}

			// The settle after a reconnect puts the phase to warming up, so the grind's playing phase is set up again
			// once it's done - or its hours stopped counting and the status read "settling" for the rest of it.
			if (!_wasGrinding || (_phase != Phase.Playing)) {
				_wasGrinding = true;
				ClearBreakState();

				if (_phase != Phase.Off) {
					BankSession();
				}

				// A grind IS playing, so the day's total has to keep counting through it.
				//
				// Handing the account over used to stop the clock: human mode banked what it had, went to Off and
				// accrued nothing until the grind ended. That made a two-hour achievement session invisible to the
				// schedule, so an account with a six-hour day could put in six hours of weighted play PLUS three
				// two-hour hunts and still believe it had done its six. Keeping the Playing phase (with no game of
				// its own) leaves the per-tick banking below running, so hunting comes OUT of the day's budget
				// instead of being stacked on top of it - which is what a person's evening actually looks like.
				_phase = Phase.Playing;
				_game = 0;
				_switchingTo = 0;
				_sessionEnds = Bot.GrindUntil ?? DateTime.UtcNow.AddHours(2);
				_bankedTo = DateTime.UtcNow;
				_bankedForLogon = Bot.OnlineSince ?? DateTime.UtcNow;
			}

			BankSession();

			// Outside waking hours a grind (a 'drops' run included) keeps the account's night look - invisible - so
			// friends don't see it in a game at 3am while it's meant to be asleep. Back to normal the moment it's
			// morning.
			if (InWakingHours(DateTime.Now)) {
				Bot.ClearPersonaOverride();
			} else {
				Bot.SetPersonaOverride(Bot.PersonaDark);
			}

			// Put the grind game on directly (idempotent - only re-sends when it isn't already the one running).
			// ReassertPlaying is keyed once-per-logon and would no-op mid-session, so the grind game would never
			// actually start except by the idler's slow backstop; this makes the switch happen on the next tick.
			// Another game still running is closed first, and the grind game follows a minute or few later -
			// closing one game and opening another isn't instant.
			if (!PlayingExactly([Bot.GrindGame])) {
				if ((Bot.PlayingApps.Count > 0) && !Bot.PlayingApps.Contains(Bot.GrindGame)) {
					Bot.StopPlaying();
					_grindLaunchAt = DateTime.UtcNow.AddSeconds(Rng(60, 240));
				} else if (DateTime.UtcNow >= _grindLaunchAt) {
					Bot.SetPlaying([Bot.GrindGame]);
				}
			}

			return;
		}

		// Grind just finished: ease back into the day instead of snapping straight into a game. Re-arming the
		// warm-up gate makes the flow below take a short settle first, the way it does after waking.
		if (_wasGrinding) {
			_wasGrinding = false;
			_settlingAfterGrind = true;
			_phase = Phase.Off;
			_gateArmedFor = default;
			Log.Info("done grinding - back to the usual day", Bot.Name);
		}

		if (!InWakingHours(DateTime.Now)) {
			GoToBed();

			return;
		}

		// Cards farming in the day's sittings: the farmer plays inside one of ours, and the day goes on around it
		// (the Playing branch below). Farming outside an open sitting is the farmer still letting go at a boundary -
		// wait for it rather than start a second game over the top.
		if (Bot.IsFarming && FarmInDay) {
			if (!FarmSittingOpen) {
				return;
			}
		} else if (Bot.IsFarming && Bot.EffectiveFarmCards && (Bot.EffectiveFarmWhen == FarmWhen.Night)) {
			// Farming only at night, and it's morning: the farmer hands the account back within seconds. Waited for,
			// rather than treated as a farming run that just ended - that took a break first thing and showed the
			// farm game with the night's invisible look already dropped.
			return;
		} else if (Bot.IsFarming) {
			// Cards farming flat out owns the session while it has work - stand off completely. Human mode plays
			// exactly ONE game at a time; a second one on top of the farmer is the loudest bot tell there is. Resume
			// the day when the cards are done.
			_wasFarming = true;
			BankSession();
			_game = 0;
			_switchingTo = 0;
			_phase = Phase.Off;
			ClearBreakState();
			Bot.ClearPersonaOverride();   // daytime farming/stand-off looks online, never carrying a night-dark or break-away persona

			return;
		}

		if (_targetMinutes == 0) {
			if (_phase != Phase.DayOff) {
				_phase = Phase.DayOff;
				_game = 0;
				_switchingTo = 0;
				ClearBreakState();
				Bot.StopPlaying();
				Bot.ClearPersonaOverride();
				Log.Info(new Said("not playing today - online but idle until bed about {0}", (BedTime()).ToString("HH:mm")), Bot.Name);
			}

			return;
		}

		if (_playedMinutesToday >= _targetMinutes) {
			if (_phase != Phase.DoneForToday) {
				BankSession();
				_phase = Phase.DoneForToday;
				_farmSession = false;
				_game = 0;
				_switchingTo = 0;
				ClearBreakState();
				Bot.StopPlaying();
				Bot.ClearPersonaOverride();
				Log.Info(new Said("done for the day - {0} played, back around {1}", Fmt.Hm(_playedMinutesToday), (WakeTime()).ToString("HH:mm")), Bot.Name);
			}

			return;
		}

		if (_phase is Phase.ShortBreak or Phase.MealBreak or Phase.SwitchingGame) {
			// A real client only turns Away after a few idle minutes - not in the second the game closes.
			if ((_breakPersona is int persona) && (DateTime.UtcNow >= _breakPersonaAt)) {
				_breakPersona = null;
				_breakPersonaSet = true;
				_offlineBreak = persona == Bot.PersonaDark;
				Bot.SetPersonaOverride(persona);
			}

			if (DateTime.UtcNow < _phaseEnds) {
				return;
			}

			// Back from a break it spent Away or offline: back online first, and the game a little after - not
			// both in the same second.
			if (_breakPersonaSet) {
				_breakPersonaSet = false;
				_offlineBreak = false;
				_breakPersona = null;
				Bot.ClearPersonaOverride();
				_phaseEnds = DateTime.UtcNow.AddSeconds(Rng(20, 180));

				return;
			}

			_breakPersona = null;
			_phase = Phase.Off;   // fall through into the next session
		}

		if (_phase == Phase.Playing) {
			// A reconnect has to pass the safety gate again before the game goes back on.
			//
			// The phase survives a disconnect, so this branch used to re-assert the game the instant the account
			// came back - the log shows "logged on" and "still playing Counter-Strike 2" on the SAME SECOND. That
			// is wrong twice over. It does not look like a person, who would not be back in a match a second
			// after their connection returned. And it is unsafe: Steam's report of whether the owner is playing
			// lags by up to about two and a half minutes, so in that window PlayingBlocked is still stale-false
			// and the account would claim the session out from under somebody who sat down during the outage.
			//
			// SettledIn() re-arms itself whenever OnlineSince changes, so simply asking it here is enough: a
			// brief blip that did not produce a new logon passes straight through, and a real reconnect waits
			// for four clear reads before the game comes back.
			if (!SettledIn()) {
				BankSession();

				return;
			}

			// Bank as we go. Closing the app mid-session used to lose the whole sitting: nothing was credited
			// and nothing written, so a restart replayed those hours on top of a target that had already been
			// partly spent - which is the exact double-day the saved plan exists to prevent.
			BankSession();

			// A card-farming sitting: the farmer puts the game on and watches the drops; this keeps the clock. Follows
			// the game it's actually on, and ends the sitting early once the cards run out - with a break, like any
			// other sitting, before the usual games take over.
			if (_farmSession) {
				if (Bot.IsFarming && (Bot.PlayingApps.Count == 1)) {
					_game = Bot.PlayingApps[0];
					_lastGame = _game;
				}

				// This sitting's game just gave its last card: play on for a bit, like anybody who got the last drop
				// and kept going for a few minutes - then the usual break. The next card game waits for the next
				// sitting, rather than being switched to the minute this one ran out. A game that simply dropped off
				// the list (a partial badge read) ends the sitting the ordinary way, with no "last card" about it.
				if (CardsCheckedThisLogin() && (_game != 0) && (BotManager.ModuleOf<CardFarmer>(Bot) is { } farmer)
					&& !farmer.Queue.Any(g => (g.AppId == _game) && (g.CardsRemaining > 0))) {
					if (farmer.FinishedGame == _game) {
						PlayOnAfterLastCard();
					} else {
						EndSession();
					}

					return;
				}

				if (DateTime.UtcNow >= _sessionEnds) {
					EndSession();
				}

				return;
			}

			// Playing on after the last card: once the farmer has let go, make sure that game is what's running.
			if (_playOnPending && !Bot.IsFarming) {
				_playOnPending = false;

				if (!PlayingExactly([_game])) {
					Bot.SetPlaying([_game]);
				}
			}

			// Re-assert after a reconnect. What an account is "playing" is per-session state that Steam throws
			// away the instant the connection drops, and modules are not rebuilt on reconnect - so without this
			// the phase stays Playing, BankSession keeps crediting the minutes, the status keeps saying
			// "Counter-Strike 2, 2h10m left", and the account is in fact playing nothing at all.
			ReassertPlaying([_game]);

			if (DateTime.UtcNow >= _sessionEnds) {
				EndSession();
			}

			return;
		}

		if (!SettledIn()) {
			return;
		}

		// The farmer reads the badge pages only once the warm-up is done, so at this moment "no cards" usually just
		// means it hasn't looked yet. Starting a game here played it for a minute until the farmer found cards and
		// switched to one of them - a game swap nobody makes. Wait for that first look, which is a minute or so,
		// but not for ever: badge pages that won't load mustn't keep the account idle.
		if (Bot.Cfg.FarmCards && !CardsCheckedThisLogin() && (DateTime.UtcNow < _readyAt.AddMinutes(5))) {
			return;
		}

		// Warmed up. If there are cards, hand off to the farmer (it takes priority) rather than starting a
		// weighted game on top of it - it will claim on its next tick now that the warm-up is done. But only when
		// it is actually going to farm: with farming switched off, or the farmer waiting for bedtime, its window or
		// its next sitting, standing aside left the account online and idle until then. The day carries on
		// instead, and the farmer takes the session over the moment it starts (the IsFarming check above).
		if (!FarmInDay && (Bot.CardsRemaining > 0) && Bot.Cfg.FarmCards && (BotManager.ModuleOf<CardFarmer>(Bot)?.HoldingBack != true)) {
			_phase = Phase.Off;
			Bot.ClearPersonaOverride();

			return;
		}

		// Just came off a farming run: don't snap into a weighted game. Step away first like a person who just
		// finished - a short break, occasionally a meal - and the normal schedule resumes when the break ends.
		if (_wasFarming) {
			_wasFarming = false;
			EndSession();

			return;
		}

		StartSession();
	}

	/// <summary>
	/// Whether it is allowed to launch anything yet.
	///
	/// Two separate reasons to wait, and both have to pass:
	///
	///   • It must not steal the session. Steam reports the account's real owner starting a game with a lag of
	///     up to about two and a half minutes, so for the first three minutes after login nocatFarm cannot tell
	///     an idle account from one whose owner just sat down. It also wants several consecutive clear reads,
	///     not one - a single lucky poll inside the lag window proves nothing.
	///
	///   • It must not look like a bot. Signing in and being in a game the same second is the single most
	///     mechanical thing an account can do, and it is what you see the moment you switch human mode on.
	///     A person opens Steam, looks at something, and gets round to it.
	/// </summary>
	private bool CardsCheckedThisLogin() => (Bot.CardsCheckedAt is { } at) && (Bot.OnlineSince is { } on) && (at >= on);

	private bool SettledIn() {
		DateTime loggedOn = Bot.OnlineSince ?? DateTime.UtcNow;

		// Re-arm on a fresh login, and on the first tick after human mode takes the account over.
		if (_gateArmedFor != loggedOn) {
			_gateArmedFor = loggedOn;
			_clearReads = 0;
			_announcedWarmUp = false;
			_warmedUp = false;

			int lo = Math.Max(0, Bot.Cfg.WarmUpMinMinutes);
			int hi = Math.Max(lo, Bot.Cfg.WarmUpMaxMinutes);

			DateTime safety = loggedOn.AddSeconds(SafetyGateSeconds);
			DateTime settled = DateTime.UtcNow.AddMinutes(Rng(lo, hi));
			_readyAt = settled > safety ? settled : safety;
		}

		// Several clear reads in a row, not one. Each tick is 20 seconds apart, so this is genuinely spread out.
		_clearReads = Bot.PlayingBlocked ? 0 : _clearReads + 1;

		bool stillSettling = DateTime.UtcNow < _readyAt;

		if (!stillSettling && (_clearReads >= 4)) {
			_warmedUp = true;
			_settlingAfterGrind = false;

			return true;
		}

		// Only claim to be settling in when that is actually what is happening. The clear-reads half of the gate
		// also runs after an ordinary mid-afternoon break, and reporting "just signed in" then - hours after the
		// login, between two sessions - is simply untrue. That wait is a couple of ticks and needs no narration.
		if (!stillSettling) {
			return false;
		}

		if (_phase != Phase.WarmingUp) {
			// Coming out of the overnight sleep reads as "waking up", not "just signed in" - which is what it said
			// at the wake time even though the account had been online all night.
			_wokeUp = _phase is Phase.NightIdle or Phase.Asleep;
			_phase = Phase.WarmingUp;
			_game = 0;
			_switchingTo = 0;
			ClearBreakState();

			// Put down whatever was running before settling in.
			//
			// Waking out of the overnight idle used to leave that game on: the persona went back to visible, the
			// board said "settling for 12m", and the friends list said "In-Game: Counter-Strike 2" the whole time.
			// Somebody who has just signed in is not in a game yet - that is the entire point of the settle.
			Bot.StopPlaying();

			// Only in the day. A settle at night (a grind asked for at 3am) keeps the night's invisible look rather
			// than popping the account up online on everybody's friends list while it waits.
			if (InWakingHours(DateTime.Now)) {
				Bot.ClearPersonaOverride();
			}
		}

		if (!_announcedWarmUp) {
			_announcedWarmUp = true;
			int wait = (int) Math.Max(1, (_readyAt - DateTime.UtcNow).TotalMinutes);
			Log.Info(_wokeUp
				? new Said("awake for the day - warming up for ~{0}", Fmt.Hm(wait))
				: new Said("warming up for ~{0}", Fmt.Hm(wait)), Bot.Name);
		}

		return false;
	}

	/// <summary>
	/// Count one owner-safety read for this sign-in: re-armed on every new sign-in, and back to none whenever Steam
	/// says the owner is playing. Changes nothing else - no phase, no persona, no games.
	/// </summary>
	private bool SafetyClear() {
		if (Bot.OnlineSince is not { } loggedOn) {
			return false;
		}

		if (_safetyArmedFor != loggedOn) {
			_safetyArmedFor = loggedOn;
			_safetyReads = 0;
		}

		_safetyReads = Bot.PlayingBlocked ? 0 : _safetyReads + 1;

		return SafeToPlay;
	}

	/// <summary>
	/// Forget the break in progress. A break cut short - by you, a grind, bedtime, the farmer, a day off - used to
	/// leave this behind: an "offline" break still set kept every visible thing waiting and brought a daytime
	/// reconnect up invisible, and an Away still pending went straight on at the start of the next break. Whoever
	/// calls this sets the persona it wants next.
	/// </summary>
	private void ClearBreakState() {
		_offlineBreak = false;
		_breakPersonaSet = false;
		_breakPersona = null;
	}

	// ═══ the day ════════════════════════════════════════════════════════════
	/// <summary>
	/// Roll today. A real week is not flat: weekday gaming is mostly evenings, weekends start earlier and run
	/// longer, and Friday and Saturday nights go late because nothing is on tomorrow.
	/// </summary>
	/// <summary>
	/// Throw today's plan away and roll a new one from the settings as they stand now.
	///
	/// A day is rolled once, at wake time, and then persisted so restarts don't hand out a fresh eight hours
	/// every time. That is right, but it means changing the hours, the bedtime or the weights does nothing
	/// visible until tomorrow - the setting saves, the log says nothing, and the account carries on to a target
	/// rolled against the old numbers. This is the way to say "apply it now" without waiting a day.
	/// </summary>
	public void RerollToday() {
		if (_phase == Phase.Playing) {
			BankSession();
			Bot.StopPlaying();
		}

		HumanDay.Forget(Bot.Name);

		if (_breakPersonaSet) {
			Bot.ClearPersonaOverride();
		}

		ClearBreakState();
		_phase = Phase.Off;
		_dayStamp = -1;         // < 0 rolls immediately, even before wake time
		_game = 0;
		_lastGame = 0;
		_switchingTo = 0;

		Log.Info("today's plan thrown away - rolling a fresh one from the current settings", Bot.Name);
		RollNewDayIfNeeded();
	}

	private void RollNewDayIfNeeded() {
		int today = DateTime.Now.DayOfYear;

		if (_dayStamp == today) {
			return;
		}

		// Don't roll a new day at calendar midnight while last night's session is still finishing. A wake->bed
		// cycle routinely runs past midnight (e.g. bed 02:48), and rolling at 00:00 splits it - the post-midnight
		// hours get counted into THIS morning's total, so the account reads "4h played" at noon having woken at 11.
		// Roll when it actually reaches its wake time instead, so each day is one clean wake->bed cycle. (_dayStamp
		// < 0 means a fresh start with no plan yet - that must still roll immediately, even before wake.)
		// Rolled once it's past the EARLIEST today's wake could possibly be, never yesterday's wake minute: keyed to
		// yesterday's, a day that rolled an earlier wake was already "awake" the moment it rolled, so on about half
		// of all days the account got up at exactly the same minute as the day before.
		if ((_dayStamp >= 0) && (DateTime.Now < EarliestWakeToday())) {
			return;
		}

		// Close out whatever is in flight FIRST. The default day runs past midnight, so a session is usually
		// still running when this fires; zeroing the counters underneath it lost the evening's time and left
		// Steam playing a game the scheduler had already forgotten about.
		if (_phase == Phase.Playing) {
			BankSession();
			Bot.StopPlaying();
			_phase = Phase.Off;
		}

		// Started after midnight but before getting up: last night is still going, and its plan is the one in force.
		// Rolling a fresh day here counted the rest of last night against today - the split that waiting for the
		// wake time above exists to prevent, lost on every restart in the small hours.
		if ((_dayStamp < 0) && (HumanDay.Load(Bot.Name, DateTime.Now.AddDays(-1)) is { } lastNight)) {
			Restore(lastNight);

			if (DateTime.Now < WakeTime()) {
				_dayStamp = DateTime.Now.AddDays(-1).DayOfYear;
				Log.Info(new Said("last night's plan restored - {0}/{1} played, up about {2}", Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes), (WakeTime()).ToString("HH:mm")), Bot.Name);

				return;
			}
		}

		_dayStamp = today;
		BotConfig cfg = Bot.Cfg;

		// A plan already rolled for today survives a restart. Without this, every restart handed the account a
		// brand-new target AND reset the minutes it had already played, so three restarts meant three days of
		// playing crammed into one - and a rolled day off could be restarted straight back into an eight-hour
		// session.
		if (HumanDay.Load(Bot.Name, DateTime.Now) is { } saved) {
			Restore(saved);
			Log.Info(new Said("today's plan restored - {0}/{1} played so far, bed about {2}", Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes), (BedTime()).ToString("HH:mm")), Bot.Name);

			return;
		}
		DayOfWeek dow = DateTime.Now.DayOfWeek;
		bool weekend = dow is DayOfWeek.Saturday or DayOfWeek.Sunday;
		bool freeNight = dow is DayOfWeek.Friday or DayOfWeek.Saturday;   // no work or school tomorrow

		// BED. Late on a free night; otherwise mostly the configured hour, sometimes an hour earlier.
		int bed = Math.Clamp(cfg.BedHour, 0, 23);
		int later = Math.Clamp(cfg.LateNightExtraHours, 0, 6);
		// Roll first, THEN decide which day it lands on, and only then wrap it onto the clock.
		//
		// Deciding from the configured hour instead of the rolled one broke every late-evening bedtime: with
		// BedHour 23 a free-night roll of 24-26 wrapped to 00-02, but the "is it tomorrow" answer still came
		// from 23, so bedtime was read as TODAY just after midnight - and the whole day collapsed to a
		// ~48-minute window. The mirror case (BedHour 0 rolling -1 to 23) never went to bed at all.
		int rolled = freeNight ? Rng(bed + 1, bed + 1 + later) : Percent(40) ? Rng(bed, bed + 1) : Rng(bed - 1, bed);
		_bedHour = ((rolled % 24) + 24) % 24;
		_bedMinute = Rng(0, 59);

		// GETTING ON. Averaging two rolls gives a natural bell around the configured hour instead of a flat pick,
		// and the minute is always jittered so it is never 13:00:00 sharp two days running.
		int wake = Math.Clamp(cfg.DayStartHour, 0, 23);
		int lo = Math.Max(0, wake - 1) * 60;
		int hi = Math.Min(23, wake + 3) * 60;
		_wakeMinuteOfDay = (Rng(lo, hi) + Rng(lo, hi)) / 2;

		if (weekend) {
			_wakeMinuteOfDay -= Rng(45, 150);   // weekends start earlier
		}

		_wakeMinuteOfDay = Math.Clamp(_wakeMinuteOfDay, 0, (23 * 60) + 59);

		// Whether bedtime belongs to tomorrow is a property of the SETTINGS, not of what the dice did. Deriving
		// it from the rolled values meant that when the two rolls crossed - easy whenever bed and wake are within
		// a few hours of each other - bedtime jumped a full day and the account counted itself awake round the
		// clock, playing its whole target from midnight.
		_bedIsTomorrow = (rolled >= 24) || (_bedHour <= wake);

		if (!_bedIsTomorrow) {
			_wakeMinuteOfDay = Math.Min(_wakeMinuteOfDay, Math.Max(0, ((_bedHour * 60) + _bedMinute) - 60));
		}

		// HOURS. Not a flat grind - an enthusiast who is around most days, has quiet days and the odd day off.
		int hours = Math.Clamp(weekend ? cfg.WeekendHours : cfg.WeekdayHours, 0, 20);
		int full = hours * 60;

		if ((full <= 0) || Percent(Math.Clamp(cfg.DayOffChancePct, 0, 100))) {
			_targetMinutes = 0;
		} else {
			int roll = Rng(0, 99);

			// a quiet day · a normal day · a long one
			_targetMinutes = roll < 20 ? Rng(full * 55 / 100, full * 80 / 100)
				: roll < 65 ? Rng(full * 80 / 100, full * 105 / 100)
				: Rng(full * 105 / 100, full * 125 / 100);
		}

		// A day cannot hold more play than the hours it is awake. Four fifths of the window leaves room for the
		// breaks, the meals and the gaps between games.
		if (_targetMinutes > 0) {
			int fits = WindowMinutes() * 4 / 5;

			if (_targetMinutes > fits) {
				_targetMinutes = Math.Max(30, fits);
			}
		}

		// The main game's own written share is the centre of today's roll. It used to be ignored outright in
		// favour of a separate box, so the number typed beside the main game did nothing at all and only its
		// position in the list carried meaning.
		List<(uint Game, int Weight)> spread = Weights();
		int mainCentre = Math.Clamp(spread.Count > 0 ? spread[0].Weight : 70, 5, 95);
		_mainSharePct = Math.Clamp(Rng(mainCentre - 10, mainCentre + 10), 5, 95);

		// SIDE GAMES COME IN BURSTS. Most days are pure main game; sometimes there is a real sitting on something
		// else. A guaranteed slice of every side game every single day is a pattern, not a person.
		if (Percent(Math.Clamp(cfg.PureMainDayChancePct, 0, 100)) || (spread.Count < 2)) {
			_otherBudget = 0;
		} else {
			// Derived from the very share the picker is rolling against, with a little slack on top. A budget
			// set from its own independent number was a second limit on the same minutes: whichever happened to
			// be tighter won, and the weights quietly became fiction whenever it was this one. Slack keeps it a
			// backstop against a run of side-game rolls rather than the thing that decides the day.
			int side = Math.Clamp(100 - _mainSharePct, 1, 95);
			int budget = _targetMinutes * side / 100;
			_otherBudget = Math.Max(20, budget + (budget / 8));
		}

		int signOuts = Math.Clamp(cfg.MaxSignOutsPerDay, 0, 40);
		_signOutCap = signOuts == 0 ? 0 : Rng(Math.Max(1, signOuts - 3), signOuts + 3);
		_mealCap = cfg.MealBreaksPerDay <= 0 ? 0 : Rng(1, cfg.MealBreaksPerDay + 1);

		_playedMinutesToday = 0;
		_otherPlayed = 0;
		_signOutsUsed = 0;
		_mealsUsed = 0;
		_wasFarming = false;
		_firstSessionOfDay = true;
		_game = 0;
		_lastGame = 0;
		_switchingTo = 0;

		lock (_minutesByGame) {
			_minutesByGame.Clear();
		}

		Persist();

		if (_targetMinutes == 0) {
			Log.Info(new Said("taking today off - back tomorrow around {0}", (WakeTime()).ToString("HH:mm")), Bot.Name);

			return;
		}

		Said mix = _otherBudget == 0
			? new Said("{0} only today", GameName(MainGame()))
			: new Said("{0} about {1}%, up to {2} on the others", GameName(MainGame()), _mainSharePct, Fmt.Hm(_otherBudget));

		Log.Info(new Said("today: around {0} of play, on about {1}, bed about {2} - {3}", Fmt.Hm(_targetMinutes), (WakeTime()).ToString("HH:mm"), (BedTime()).ToString("HH:mm"), mix), Bot.Name);
	}

	private int WindowMinutes() {
		int start = _wakeMinuteOfDay;
		int bed = (_bedHour * 60) + _bedMinute;

		return bed > start ? bed - start : ((24 * 60) - start) + bed;
	}

	private DateTime WakeTime() => DateTime.Now.Date.AddMinutes(_wakeMinuteOfDay);

	/// <summary>The earliest today's wake roll can land: an hour before the day-start hour, less the weekend's head start.</summary>
	private DateTime EarliestWakeToday() => DateTime.Now.Date.AddMinutes(Math.Max(0, (Math.Clamp(Bot.Cfg.DayStartHour, 0, 23) - 1) * 60 - 150));

	/// <summary>
	/// The next time this account gets up, which is tomorrow once today's has been and gone.
	///
	/// <see cref="WakeTime"/> is deliberately TODAY's - the waking-hours test needs that - but an account that
	/// finished at half nine in the evening is not getting up at this morning's time. Reading it as "how long
	/// until it wakes" clamped a negative eleven hours to zero and reported "any moment", all night.
	/// </summary>
	private DateTime NextWakeTime() {
		DateTime wake = WakeTime();

		return wake > DateTime.Now ? wake : wake.AddDays(1);
	}

	private DateTime BedTime() {
		DateTime bed = DateTime.Now.Date.AddHours(_bedHour).AddMinutes(_bedMinute);

		// A configured bed hour at or before the configured wake hour means the small hours of the NEXT day.
		return _bedIsTomorrow ? bed.AddDays(1) : bed;
	}

	/// <summary>True when the games Steam is actually running are exactly this set (order-independent).</summary>
	private bool PlayingExactly(IReadOnlyCollection<uint> games) {
		IReadOnlyList<uint> now = Bot.PlayingApps;

		return (now.Count == games.Count) && games.All(g => now.Contains(g));
	}

	private bool InWakingHours(DateTime now) {
		if (now < WakeTime()) {
			// Before today's start hour, last night's session may legitimately still be running.
			return now < BedTime().AddDays(-1);
		}

		return now < BedTime();
	}

	private int MinutesUntilBed() => (int) Math.Max(0, (BedTime() - DateTime.Now).TotalMinutes);

	private void GoToBed() {
		// Re-arm the warm-up gate for the morning. It's keyed on the login timestamp, which doesn't change
		// across an overnight sleep, so without this the account snaps straight from asleep into a game at wake
		// with no settle. Cleared here (only reached when asleep), it re-arms on the first waking tick.
		_gateArmedFor = DateTime.MinValue;
		BankSession();
		_farmSession = false;
		ClearBreakState();   // bedtime ends a break too; the night's invisible look goes on just below

		// Cards farm in the day, so bedtime ends the sitting. The farmer lets go within seconds of it closing; the
		// overnight games go on once it has, rather than both fighting over what's playing.
		if (FarmInDay && Bot.IsFarming) {
			Bot.SetPersonaOverride(Bot.PersonaDark);

			return;
		}
		List<uint> night = NightGames();
		bool banking = Bot.Cfg.OfflineIdleAtNight && (night.Count > 0);
		Phase want = banking ? Phase.NightIdle : Phase.Asleep;

		// Invisible for the night. This is a no-op once it's already set - the override is remembered on the bot
		// and re-applied by the logon handler, which is what carries it across a reconnect. Steam resetting the
		// persona when a game starts is handled inside SetPlaying, which re-applies it after the games message.
		Bot.SetPersonaOverride(Bot.PersonaDark);

		// Same reconnect problem as a live session, and worse here: a drop at 1am used to mean the account banked
		// nothing for the rest of the night while still reporting "asleep, banking hours quietly". Re-asserting
		// runs BEFORE the phase check so it happens on every tick, not only on the way into bed.
		// Hand the night to the card farmer when it has work.
		//
		// It knows which games still have drops; the overnight list is whatever was typed into a settings box
		// once. Asserting over the top of it would take the session straight back off it every tick.
		//
		// Said on the farmer taking over, not only on the way into bed: an account that went to bed a minute before
		// the farmer started was already in its night phase, so the handover went unsaid and the status went on
		// reading "banking hours" while the farmer was the one playing.
		if (Bot.IsFarming) {
			if ((_phase != want) || !_nightFarming) {
				_phase = want;
				_game = 0;
				_switchingTo = 0;
				_nightFarming = true;
				Log.Info(new Said("asleep until {0} - the card farmer keeps working through the night", (WakeTime()).ToString("HH:mm")), Bot.Name);
			}

			return;
		}

		_nightFarming = false;

		// Nobody sees the account at night, but the owner can still sit down at it. Right after a sign-in Steam's
		// "the owner is playing" can be minutes late, so the overnight games wait for the same safety check the day
		// does - three minutes and several clear reads - rather than going on the first tick after logging on.
		if (banking && SafeToPlay) {
			ReassertPlaying(night);

			// Keep the overnight games actually on. After a night farming run ends, the farmer has left the
			// account on its farm game (or nothing) and the once-per-login ReassertPlaying won't fire again -
			// so without this it played nothing the rest of the night while still reading "asleep".
			if (!PlayingExactly(night)) {
				Bot.SetPlaying(night);

				if (_phase == want) {
					Log.Info(new Said("back to banking hours quietly until {0}", (WakeTime()).ToString("HH:mm")), Bot.Name);
				}
			}
		}

		if (_phase == want) {
			return;
		}

		_phase = want;
		_game = 0;
		_switchingTo = 0;

		if (banking) {
			Log.Info(new Said("asleep - banking hours quietly until {0}", (WakeTime()).ToString("HH:mm")), Bot.Name);
		} else {
			Bot.StopPlaying();
			Log.Info(new Said("asleep - back around {0}", (WakeTime()).ToString("HH:mm")), Bot.Name);
		}
	}

	/// <summary>
	/// Put the games back after a reconnect, and only then.
	///
	/// Steam's "currently playing" is per-session: a dropped connection clears it, and because modules are
	/// created once per account and merely stopped and restarted, every field in here survives the reconnect
	/// looking perfectly healthy. Keyed on the login timestamp so it fires exactly once per session rather than
	/// re-sending the same message every twenty seconds.
	/// </summary>
	private void ReassertPlaying(IReadOnlyCollection<uint> games) {
		DateTime? loggedOn = Bot.OnlineSince;

		if ((loggedOn == null) || (_playingAssertedFor == loggedOn)) {
			return;
		}

		_playingAssertedFor = loggedOn.Value;

		if (games.Count == 0) {
			return;
		}

		Bot.SetPlaying(games);

		if (_assertedOnce) {
			Log.Info(new Said("reconnected - put {0} game(s) back on", games.Count), Bot.Name);
		}

		_assertedOnce = true;
	}

	// ═══ sessions ═══════════════════════════════════════════════════════════
	private void StartSession() {
		uint main = MainGame();

		if (main == 0) {
			if (_phase != Phase.DoneForToday) {
				_phase = Phase.DoneForToday;
				Bot.ClearPersonaOverride();   // never leave a break's Away/Snooze stuck on for the rest of the day
				Log.Warn("human mode is on but no games are set - fill in \"Games and how often\"", Bot.Name);
			}

			return;
		}

		uint game;
		uint farm = FarmGameNow();

		if (_switchingTo != 0) {
			// The gap that just finished was for THIS game, so launch it rather than deciding all over again.
			//
			// Re-picking here was a livelock: the gap ends, a fresh pick lands on yet another game, which is
			// still different from the last one PLAYED (that only updates on a real start), so it goes straight
			// back into the gap. Watching it live, the account sat on "closing the game" for four minutes and
			// would have kept going round until a pick happened to match.
			game = _switchingTo;
			_switchingTo = 0;
		} else if (farm != 0) {
			// Cards first: while there are any, the card game is what the day plays - and going to it from another
			// game still takes the moment it takes to close one and launch the other.
			game = farm;

			if ((game != _lastGame) && (_lastGame != 0)) {
				_switchingTo = game;
				_phase = Phase.SwitchingGame;
				_phaseEnds = DateTime.UtcNow.AddMinutes(Rng(1, 4));
				_game = 0;
				Bot.StopPlaying();

				return;
			}
		} else {
			// A real player does not change game every time they sit down. Often they carry straight on.
			game = (_lastGame != 0) && Chance(0.40) ? _lastGame : PickGame();

			// Once today's side-game allowance is spent it is the main game for the rest of the day - an hour target
			// excepted, since reaching it is the point.
			if ((game != main) && !_firstSessionOfDay && (_otherPlayed >= _otherBudget) && !IsTargetGame(game)) {
				game = main;
			}

			// Closing one game and launching another is not instant.
			if ((game != _lastGame) && (_lastGame != 0)) {
				_switchingTo = game;
				_phase = Phase.SwitchingGame;
				_phaseEnds = DateTime.UtcNow.AddMinutes(Rng(1, 4));
				_game = 0;
				Bot.StopPlaying();

				return;
			}
		}

		// A card-farming sitting runs like a main-game one - real sittings, not the short dips side games get.
		_farmSession = FarmInDay && (Bot.CardsRemaining > 0) && ((game == farm)
			|| (BotManager.ModuleOf<CardFarmer>(Bot)?.Queue.Any(g => (g.AppId == game) && (g.CardsRemaining > 0)) ?? false));
		int minutes = SessionLength(game, _farmSession || IsTargetGame(game) ? game : main);

		_game = game;
		_lastGame = game;
		_firstSessionOfDay = false;
		_phase = Phase.Playing;
		_sessionStarted = DateTime.UtcNow;
		_sessionEnds = _sessionStarted.AddMinutes(minutes);
		_bankedTo = _sessionStarted;
		_bankedForLogon = Bot.OnlineSince ?? DateTime.UtcNow;

		Bot.ClearPersonaOverride();
		Bot.SetPlaying([game]);   // ONE game. Six at once is the tell.
		_playingAssertedFor = Bot.OnlineSince ?? DateTime.UtcNow;

		Log.Good(_farmSession
			? new Said("farming cards on {0} for about {1}  ({2}/{3} today)", GameName(game), Fmt.Hm(minutes), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes))
			: new Said("playing {0} for about {1}  ({2}/{3} today)", GameName(game), Fmt.Hm(minutes), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes)), Bot.Name);
	}

	/// <summary>
	/// How long this sitting runs. The main game gets real gaming sessions - mostly a couple of hours, sometimes a
	/// quick one, sometimes an all-evening one. Side games get shorter dips that can never blow the day's side
	/// allowance. Both are then bounded by what is left of the target and by bedtime.
	/// </summary>
	private int SessionLength(uint game, uint main) {
		int min = Math.Max(5, Bot.Cfg.SessionMinMinutes);
		int max = Math.Max(min + 5, Bot.Cfg.SessionMaxMinutes);
		int span = max - min;
		int length;

		if (game == main) {
			int roll = Rng(0, 99);

			length = roll < 30 ? Rng(min, min + (span * 21 / 100))
				: roll < 80 ? Rng(min + (span * 28 / 100), min + (span * 64 / 100))
				: Rng(min + (span * 71 / 100), max);
		} else {
			length = Rng(min, min + (span * 29 / 100));
			int left = _otherBudget - _otherPlayed;

			if ((left > 0) && (length > left)) {
				length = Math.Max(15, left);
			}
		}

		// The first sitting of the day is a short one - checking in, not settling down for four hours.
		//
		// Short RELATIVE to the configured range, never an absolute 12-28. That absolute ignored
		// SessionMinMinutes completely, so an account told "at least 30 minutes" opened its day with a
		// twelve-minute sitting - a minimum that was not a minimum. The floor wins; the cap only shortens
		// what is above it.
		if (_firstSessionOfDay) {
			int shortCap = Math.Max(min, Math.Min(28, min + (span * 15 / 100)));
			length = Math.Min(length, Rng(min, shortCap));
		}

		int remaining = _targetMinutes - _playedMinutesToday;

		if (length > remaining) {
			// Another hard-coded floor that could sit under a configured minimum. What is left of the target
			// is a real limit, so it still caps the sitting - but where there IS room for a full-length one,
			// the account's own minimum is what decides, not the number 20.
			length = Math.Max(Math.Min(min, remaining), remaining + Rng(-5, 15));
		}

		int untilBed = MinutesUntilBed();

		if ((untilBed > 0) && (length > untilBed)) {
			length = untilBed;
		}

		return Math.Max(5, length);
	}

	/// <summary>
	/// Turn the card-farming sitting into an ordinary one on the same game, for the "keep playing after the last card"
	/// minutes (PostFarmWindDown, 15-20 by default), then it ends with a break like any sitting. 0 = stop right away.
	/// </summary>
	private void PlayOnAfterLastCard() {
		_farmSession = false;   // closes the farmer's sitting - it lets go within seconds and starts nothing else

		int lo = Math.Max(0, Bot.Cfg.PostFarmWindDownMinMinutes);
		int hi = Math.Max(lo, Bot.Cfg.PostFarmWindDownMaxMinutes);
		int minutes = Rng(lo, hi);

		if (minutes <= 0) {
			EndSession();

			return;
		}

		_sessionEnds = DateTime.UtcNow.AddMinutes(minutes);
		_playOnPending = true;   // the game is (re)put on once the farmer has let go
		Log.Info(new Said("got the last card from {0} - playing on for about {1}, then a break", GameName(_game), Fmt.Hm(minutes)), Bot.Name);
	}

	private void EndSession() {
		BankSession();
		_game = 0;
		_farmSession = false;

		// Meals land at real meal times - dinner, and a lunch - rather than at a flat chance any hour of the day.
		// Not near zero off-hours either: this is a night gamer, late snacks are normal.
		int hour = DateTime.Now.Hour;
		bool mealtime = ((hour >= 18) && (hour <= 21)) || ((hour >= 12) && (hour <= 14));

		if ((_mealsUsed < _mealCap) && Chance(mealtime ? 0.52 : 0.16)) {
			MealBreak();

			return;
		}

		ShortBreak();
	}

	/// <summary>
	/// Usually a quick tab-out, sometimes longer, occasionally a proper step away. Right-skewed on purpose - a
	/// flat 8-25 minutes every single time is its own signature.
	/// </summary>
	private void ShortBreak() {
		int min = Math.Max(1, Bot.Cfg.BreakMinMinutes);
		int max = Math.Max(min + 2, Bot.Cfg.BreakMaxMinutes);
		int span = max - min;
		int roll = Rng(0, 99);

		int minutes = roll < 55 ? Rng(min, min + (span * 15 / 100))
			: roll < 90 ? Rng(min + (span * 21 / 100), min + (span * 57 / 100))
			: Rng(min + (span * 57 / 100), max);

		_phase = Phase.ShortBreak;
		_phaseEnds = DateTime.UtcNow.AddMinutes(minutes);
		Bot.StopPlaying();
		StepAway(minutes, 3, new Said("short break"));   // Away - stepped away from the keyboard
	}

	private void MealBreak() {
		_mealsUsed++;
		int centre = Math.Max(10, Bot.Cfg.MealBreakMinutes);
		int minutes = Chance(0.70) ? Rng(centre - 5, centre + 10) : Rng(centre + 10, centre + 40);

		_phase = Phase.MealBreak;
		_phaseEnds = DateTime.UtcNow.AddMinutes(minutes);
		Bot.StopPlaying();
		StepAway(minutes, 4, new Said("meal break"), 2.0);   // Snooze - what Steam does to a real user who walks off
	}

	/// <summary>
	/// Sign out for the break, or just go Away. Real people close Steam sometimes, and an account that is online
	/// for eighteen unbroken hours a day is doing something no person does.
	/// </summary>
	/// <remarks>
	/// `what` is a Said, not a string. As a string the phase name was already finished text by the time the
	/// sentence around it was translated, so a Chinese log read "short break —— 约 20m 后回来" - the frame
	/// translated, the words inside it not.
	/// </remarks>
	private void StepAway(int minutes, int awayPersona, Said what, double weight = 1.0) {
		ClearBreakState();   // nothing left over from an earlier break may land on this one

		int pct = Math.Clamp((int) (Bot.Cfg.SignOutOnBreakChancePct * weight), 0, 100);

		// The status changes a few minutes in, the way a client goes Away once nobody's touched it for a bit - and a
		// break too short for that just stays online.
		int after = Rng(Math.Max(0, Bot.Cfg.BreakAwayAfterMinMinutes), Math.Max(Bot.Cfg.BreakAwayAfterMinMinutes, Bot.Cfg.BreakAwayAfterMaxMinutes));

		if (minutes <= after + 2) {
			Log.Info(new Said("{0} - back in about {1}", what, Fmt.Hm(minutes)), Bot.Name);

			return;
		}

		_breakPersonaAt = DateTime.UtcNow.AddMinutes(after);

		if (Percent(pct) && (_signOutsUsed < _signOutCap)) {
			_signOutsUsed++;
			_breakPersona = Bot.PersonaDark;

			// "Dropped offline", not "signed out" - it stays connected and simply stops being visible, which to
			// everyone on the friends list is the same thing and costs nothing in login rate limit.
			Log.Info(new Said("{0} - going offline, back in about {1}", what, Fmt.Hm(minutes)), Bot.Name);

			return;
		}

		_breakPersona = awayPersona;
		Log.Info(new Said("{0} - back in about {1}", what, Fmt.Hm(minutes)), Bot.Name);
	}

	/// <summary>When a grind's game may go on, after the game before it was closed.</summary>
	private DateTime _grindLaunchAt = DateTime.MinValue;

	/// <summary>The Away / Snooze / offline status a break is about to switch to, and when.</summary>
	private int? _breakPersona;
	private DateTime _breakPersonaAt;
	private bool _breakPersonaSet;

	/// <summary>Credit the running session's real elapsed time, once, and never a minute more than it played.</summary>
	private void BankSession() {
		if (_phase != Phase.Playing) {
			return;
		}

		// Time spent signed OUT is not time played.
		//
		// The phase survives a disconnect, so the first bank after reconnecting measured from a cursor left
		// behind before the drop and credited the whole outage. It is visible in the log: a session at
		// 19:26 reading 1h05m today, four minutes offline, and 19:40 reading 1h18m - thirteen minutes of credit
		// for ten minutes of connection. A stop at 14:00 and a start at 20:00 would have credited six hours in
		// one tick and put the account into "done for today" having played almost none of it.
		DateTime logon = Bot.OnlineSince ?? DateTime.UtcNow;

		if (_bankedForLogon != logon) {
			_bankedForLogon = logon;
			_bankedTo = DateTime.UtcNow;   // start counting from now, not from before the gap

			return;
		}

		int played = (int) Math.Max(0, (DateTime.UtcNow - _bankedTo).TotalMinutes);

		// Advance by what was BANKED, not to "now".
		//
		// The count is whole minutes, so resetting the cursor to now threw away every partial minute. That was
		// invisible while banking only happened at session boundaries, but it makes per-tick banking impossible:
		// at twenty-second ticks `played` is always 0 and the reset would discard the elapsed time forever, so
		// the day would never accrue a single minute.
		_bankedTo = _bankedTo.AddMinutes(played);
		_playedMinutesToday += played;

		if (played == 0) {
			return;
		}

		// A grind plays with no game of its own - its minutes still count, and still have to reach the saved plan, or
		// a restart after one replayed those hours on top of the day.
		if (_game != 0) {
			lock (_minutesByGame) {
				_minutesByGame[_game] = _minutesByGame.GetValueOrDefault(_game) + played;
			}

			if (_game != MainGame()) {
				_otherPlayed += played;
			}
		}

		Persist();
	}

	/// <summary>Write today's plan and progress out, so a restart carries on rather than starting over.</summary>
	private void Persist() {
		if (_dayStamp < 0) {
			return;
		}

		new HumanDay {
			DayOfYear = _dayStamp,
			Year = DateTime.Now.Year,
			TargetMinutes = _targetMinutes,
			PlayedMinutes = _playedMinutesToday,
			MainSharePct = _mainSharePct,
			OtherBudget = _otherBudget,
			OtherPlayed = _otherPlayed,
			SignOutCap = _signOutCap,
			SignOutsUsed = _signOutsUsed,
			MealCap = _mealCap,
			MealsUsed = _mealsUsed,
			WakeMinuteOfDay = _wakeMinuteOfDay,
			BedHour = _bedHour,
			BedMinute = _bedMinute,
			BedIsTomorrow = _bedIsTomorrow,
			LastGame = _lastGame,
			ByGame = ByGameSnapshot()
		}.Save(Bot.Name);
	}

	private Dictionary<string, int> ByGameSnapshot() {
		lock (_minutesByGame) {
			return _minutesByGame.ToDictionary(static kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), static kv => kv.Value);
		}
	}

	private void Restore(HumanDay saved) {
		_targetMinutes = saved.TargetMinutes;
		_playedMinutesToday = saved.PlayedMinutes;
		_mainSharePct = saved.MainSharePct;
		_otherBudget = saved.OtherBudget;
		_otherPlayed = saved.OtherPlayed;
		_signOutCap = saved.SignOutCap;
		_signOutsUsed = saved.SignOutsUsed;
		_mealCap = saved.MealCap;
		_mealsUsed = saved.MealsUsed;
		_wakeMinuteOfDay = saved.WakeMinuteOfDay;
		_bedHour = saved.BedHour;
		_bedMinute = saved.BedMinute;
		_bedIsTomorrow = saved.BedIsTomorrow;
		_lastGame = saved.LastGame;

		lock (_minutesByGame) {
			_minutesByGame.Clear();

			foreach ((string game, int minutes) in saved.ByGame) {
				if (uint.TryParse(game, out uint appId)) {
					_minutesByGame[appId] = minutes;
				}
			}
		}

		// The session itself does not survive - only the day's shape and what it has already banked.
		_game = 0;
		_switchingTo = 0;
		_firstSessionOfDay = _playedMinutesToday == 0;
		_wasFarming = false;
		_phase = Phase.Off;
	}

	// ═══ which game ═════════════════════════════════════════════════════════
	/// <summary>
	/// Nudge the pick toward a game that still has card drops waiting.
	///
	/// This is the humanised version of card farming: rather than a separate module seizing the account and
	/// running games back to back, the day's ordinary sessions simply lean toward whatever still has drops in
	/// it, and the cards arrive as a by-product of playing. Only a nudge - the weights still decide, or every
	/// account would visibly play its farmable games in a block, which is the tell farming always was.
	/// </summary>
	private uint PreferDrops(List<(uint Game, int Weight)> candidates) {
		if (candidates.Count < 2) {
			return candidates.Count == 1 ? candidates[0].Game : 0;
		}

		CardFarmer? farmer = BotManager.ModuleOf<CardFarmer>(Bot);

		if (farmer == null) {
			return 0;
		}

		HashSet<uint> withDrops = farmer.Queue.Where(static g => g.CardsRemaining > 0).Select(static g => g.AppId).ToHashSet();
		List<(uint Game, int Weight)> farmable = candidates.Where(c => withDrops.Contains(c.Game)).ToList();

		// Two in three, not always. A player who owns four games does not play only the ones that happen to
		// still be dropping cards, and an account that did would be reading as a farmer again.
		if ((farmable.Count == 0) || (Rng(0, 100) >= 66)) {
			return 0;
		}

		// Weighted, not uniform. Picking flat among whatever still has drops threw the weights away entirely for
		// two sessions in three - a 5%-of-the-week game and the main game became equally likely the moment both
		// had cards left, which is the opposite of what the list is for. The lean toward drops is preserved; it
		// just happens in proportion now.
		return WeightedPick(farmable);
	}

	// ── hour targets ────────────────────────────────────────────────────────
	/// <summary>Targets already reached this run, so "done" is said once.</summary>
	private readonly HashSet<uint> _targetsReached = [];

	/// <summary>The account's hour targets that are still to reach, with where each stands. Empty until the library is read.</summary>
	private List<HourTargets.Progress> OpenTargets() {
		if (!Bot.Library.Ready) {
			return [];
		}

		List<HourTargets.Progress> open = [];
		DateTime now = DateTime.Now;

		foreach (HourTargets.Target target in HourTargets.Parse(Bot.Cfg.HourTargets)) {
			// Only games it can play: owned, not blacklisted, not inside their refund window.
			if ((Bot.Library.Find(target.AppId) == null) || Bot.Cfg.BlacklistedGames.Contains(target.AppId)
				|| Live.Global.GlobalBlacklistedGames.Contains(target.AppId) || Bot.Refunds.Holds(target.AppId)) {
				continue;
			}

			HourTargets.Progress progress = HourTargets.Of(target, Bot.Library.MinutesOn(target.AppId), now);

			if (progress.Done) {
				if (_targetsReached.Add(target.AppId)) {
					Log.Good(new Said("reached {0}h in {1} - that hour target is done", target.Hours, GameName(target.AppId)), Bot.Name);
				}

				continue;
			}

			open.Add(progress);
		}

		return open;
	}

	private bool IsTargetGame(uint app) => OpenTargets().Any(p => p.Target.AppId == app);

	/// <summary>
	/// Maybe give this sitting to an hour target. A target without a date is a steady lean; a dated one pulls harder
	/// the more of an ordinary day it needs to finish on time - up to nine sittings in ten, never all of them, so the
	/// day still looks like the account's own.
	/// </summary>
	private uint PreferTarget() {
		List<(uint Game, int Weight)> pull = [];

		foreach (HourTargets.Progress p in OpenTargets()) {
			int weight = p.Target.By == null
				? 30
				: (int) Math.Clamp(p.DailyMinutesNeeded * 100 / Math.Max(60, _targetMinutes), 20, 90);
			pull.Add((p.Target.AppId, weight));
		}

		if ((pull.Count == 0) || (Rng(0, 99) >= pull.Max(static p => p.Weight))) {
			return 0;
		}

		return WeightedPick(pull);
	}

	/// <summary>
	/// What banks hours overnight: the overnight list, plus any dated hour target that day play alone won't finish in
	/// time. Only matters with night banking switched on - that stays the owner's call.
	/// </summary>
	private List<uint> NightGames() {
		List<uint> games = [.. Bot.Cfg.OfflineIdleGames];

		foreach (HourTargets.Progress p in OpenTargets()) {
			if ((p.Target.By != null) && (p.DailyMinutesNeeded > Math.Max(30, _targetMinutes * 0.6)) && !games.Contains(p.Target.AppId)) {
				games.Add(p.Target.AppId);
			}
		}

		return games.Count > Core.SteamIds.GamesAtOnce ? games[..Core.SteamIds.GamesAtOnce] : games;
	}

	/// <summary>The 'hours' command: each target, where it stands, and whether it's on course.</summary>
	public List<string> TargetReport() {
		List<string> lines = [];
		DateTime now = DateTime.Now;

		foreach (HourTargets.Target target in HourTargets.Parse(Bot.Cfg.HourTargets)) {
			HourTargets.Progress p = HourTargets.Of(target, Bot.Library.MinutesOn(target.AppId), now);
			string name = GameName(target.AppId);
			string have = Fmt.Hm(p.MinutesNow);

			if (Bot.Library.Find(target.AppId) == null) {
				lines.Add($"{name}: not in this account's library - skipped");
			} else if (Bot.Cfg.BlacklistedGames.Contains(target.AppId) || Live.Global.GlobalBlacklistedGames.Contains(target.AppId)) {
				lines.Add($"{name}: blacklisted - skipped");
			} else if (p.Done) {
				lines.Add($"{name}: {have} of {target.Hours}h - done");
			} else if (target.By is { } by) {
				string pace = p.Late ? "past its date"
					: p.DailyMinutesNeeded > 20 * 60 ? $"about {Fmt.Hm((int) Math.Ceiling(p.DailyMinutesNeeded))} a day needed - it can't make that date"
					: $"about {Fmt.Hm((int) Math.Ceiling(p.DailyMinutesNeeded))} a day needed";
				lines.Add($"{name}: {have} of {target.Hours}h by {by.AddDays(-1):d MMM} - {Fmt.Hm(p.MinutesLeft)} to go, {pace}");
			} else {
				lines.Add($"{name}: {have} of {target.Hours}h - {Fmt.Hm(p.MinutesLeft)} to go, no date");
			}
		}

		return lines;
	}

	/// <summary>Rolls one game from a weighted list. A zero or negative weight still counts as one, never none.</summary>
	private uint WeightedPick(List<(uint Game, int Weight)> pool) {
		int total = pool.Sum(static p => Math.Max(1, p.Weight));
		int roll = _rng.Next(0, total);
		int running = 0;

		foreach ((uint game, int weight) in pool) {
			running += Math.Max(1, weight);

			if (roll < running) {
				return game;
			}
		}

		return pool[^1].Game;
	}

	private uint MainGame() {
		List<(uint Game, int Weight)> weights = Weights();

		return weights.Count > 0 ? weights[0].Game : 0;
	}

	private List<(uint Game, int Weight)> Weights() {
		List<(uint Game, int Weight)> weights = ParseWeights(Bot.Cfg.GameWeights);

		if (weights.Count == 0) {
			foreach (uint app in Bot.Cfg.IdleGames) {
				weights.Add((app, 1));
			}
		}

		// Two things this list must respect, and until now didn't.
		//
		// "Never touch these games" meant never for the card farmer and the idler, while human mode - the one that
		// actually decides what a legit account plays all day - happily rolled a blacklisted game into its
		// schedule. And a game bought in the last fortnight and barely played is one the owner can still get their
		// money back for, until this account spends two hours of it for them; that drops out entirely (main game
		// included) until the window closes.
		//
		// If filtering would leave nothing at all, the unfiltered list stands: an account with nothing to play and
		// no explanation is worse than either, and "no games are set" would be a lie.
		List<(uint Game, int Weight)> playable = weights
			.Where(w => !Bot.Cfg.BlacklistedGames.Contains(w.Game) && !Live.Global.GlobalBlacklistedGames.Contains(w.Game) && !Bot.Refunds.Holds(w.Game))
			.ToList();

		List<(uint Game, int Weight)> list = playable.Count > 0 ? playable : weights;

		// The game the achievement hunter is on joins the rotation as one more side game, at its own weight - played
		// in ordinary sittings like any other, instead of the hunter taking the account over and cutting a sitting
		// short. Last in the list, so it is never mistaken for the main game.
		uint hunt = BotManager.ModuleOf<AchievementBoost>(Bot)?.HuntTarget ?? 0;

		if ((hunt != 0) && (list.Count > 0) && !list.Exists(w => w.Game == hunt)) {
			list = [.. list, (hunt, Math.Max(1, Bot.Cfg.BoostWeight))];
		}

		return list;
	}

	/// <summary>
	/// A weighted pick where the MAIN game's share is pinned to today's roll instead of being left to its raw
	/// weight. Fixed weights meant every side game you added quietly diluted the main one; sizing the main weight
	/// against the side total holds it at its share however many side games are configured.
	/// </summary>
	private uint PickGame() {
		List<(uint Game, int Weight)> weights = Weights();

		if (weights.Count == 0) {
			return 0;
		}

		// Lean toward something that still has card drops, before the weighted roll.
		//
		// This is what card farming looks like when it is humanised: no separate module seizing the account and
		// running games back to back, just a day that spends slightly more of its time on whatever still has
		// drops left. Returns 0 most of the time, and then the weights decide as normal.
		uint prefer = weights.Count > 1 ? PreferDrops(weights) : 0;

		if (prefer != 0) {
			return prefer;
		}

		// Then an hour target, if one wants this sitting.
		uint target = PreferTarget();

		if (target != 0) {
			return target;
		}

		if (weights.Count == 1) {
			return weights[0].Game;
		}

		int sideTotal = 0;
		List<(uint Game, int Weight)> today = [(weights[0].Game, 0)];

		for (int i = 1; i < weights.Count; i++) {
			int w = Math.Max(1, weights[i].Weight);
			today.Add((weights[i].Game, w));
			sideTotal += w;
		}

		today[0] = (weights[0].Game, sideTotal > 0 ? Math.Max(1, sideTotal * _mainSharePct / Math.Max(1, 100 - _mainSharePct)) : 100);

		return WeightedPick(today);
	}

	/// <summary>
	/// "730:70, 440:20, 550:10" -> a weighted list, first entry being the main game. Anything without a percent
	/// shares out whatever is left over, so "730:70, 440, 550" gives the two side games 15% each.
	/// </summary>
	public static List<(uint Game, int Weight)> ParseWeights(string spec) {
		List<(uint Game, int Weight)> parsed = [];

		if (string.IsNullOrWhiteSpace(spec)) {
			return parsed;
		}

		// A weight left out entirely shares whatever is spare; a weight written as an explicit 0 means "not this
		// one". Collapsing those two into the same value meant "440:0" - the obvious way to bench a game without
		// deleting it - handed it a share of the week instead.
		List<int> blanks = [];

		foreach (string entry in spec.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			string[] halves = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			if ((halves.Length == 0) || !uint.TryParse(halves[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint app) || (app == 0)) {
				continue;
			}

			// The same game listed twice would be picked as if it were two games, quietly inflating its share.
			if (parsed.Any(p => p.Game == app)) {
				continue;
			}

			bool stated = halves.Length > 1;
			int weight = 0;

			if (stated) {
				int.TryParse(halves[1].TrimEnd('%', ' '), out weight);

				if (weight <= 0) {
					continue;   // explicitly benched
				}
			} else {
				blanks.Add(parsed.Count);
			}

			parsed.Add((app, Math.Max(0, weight)));
		}

		if (blanks.Count > 0) {
			int given = parsed.Sum(static p => p.Weight);
			int spare = Math.Max(blanks.Count, 100 - given);
			int each = Math.Max(1, spare / blanks.Count);

			foreach (int i in blanks) {
				parsed[i] = (parsed[i].Game, each);
			}
		}

		return parsed;
	}

	private static string GameName(uint appId) => appId == 0 ? "nothing" : GameNames.Of(appId);

	/// <summary>
	/// Whether this account should be reacting to the outside world right now.
	///
	/// Everything that answers something - a trade, a message, a friend request - asks this first. An account
	/// that is asleep on your friends list and still accepting trades at four in the morning is worse than one
	/// that never pretended to sleep at all, because it proves nobody is there.
	///
	/// Always true when human mode is off, or when the account was told to react around the clock.
	/// </summary>
	/// <summary>
	/// Should a sign-in come up invisible? Yes on a fresh start (human mode hasn't looked at the clock yet - a
	/// restart at 3am mustn't announce the account), while it's asleep or banking the night, and on a break it's
	/// spending offline. NOT on an ordinary daytime reconnect: that came up invisible too, and nothing turned it
	/// back until the next sitting started - an hour of looking signed out in the middle of its day.
	/// </summary>
	public static bool DarkAtLogon(Bot bot) {
		HumanMode? human = bot.Modules.OfType<HumanMode>().FirstOrDefault();

		return (human == null) || !human._ticked || !human.InWakingHours(DateTime.Now) || human._offlineBreak || human.NightGrind
			|| (human.Current is Phase.Asleep or Phase.NightIdle);
	}

	public static bool AwakeFor(Bot bot) {
		if (!bot.Cfg.LegitMode || !bot.Cfg.ActOnlyWhileAwake) {
			return true;
		}

		HumanMode? human = bot.Modules.OfType<HumanMode>().FirstOrDefault();

		// Right after a start (a restart at 3am, say) the phase is still the Off it starts in - which read as awake.
		return (human == null) || (human._ticked && (human.Current is not (Phase.Asleep or Phase.NightIdle)) && !human.NightGrind);
	}

	/// <summary>
	/// Ready to do something other people can see: awake, settled in after signing in or waking, and not in a break
	/// it's spending offline. Every visible action on a human-mode account waits for this - and then its own
	/// random delay on top (<see cref="HumanGate"/>). Always true without human mode.
	/// </summary>
	public static bool ReadyFor(Bot bot) {
		if (!bot.Cfg.LegitMode) {
			return true;
		}

		HumanMode? human = bot.Modules.OfType<HumanMode>().FirstOrDefault();

		return AwakeFor(bot) && ((human == null) || (human.WarmedUp && !human._offlineBreak));
	}

	/// <summary>
	/// Up and about in its own day: awake, settled in, and not on a break it's spending offline - whatever "act only
	/// while awake" says. For what the account starts by itself (<see cref="HumanGate.OwnDay"/>); that switch is about
	/// answering other people while asleep, and joining a group or listing cards at 4am is neither. Always true
	/// without human mode.
	/// </summary>
	public static bool UpFor(Bot bot) {
		if (!bot.Cfg.LegitMode) {
			return true;
		}

		HumanMode? human = bot.Modules.OfType<HumanMode>().FirstOrDefault();

		return (human == null) || (human._ticked && (human.Current is not (Phase.Asleep or Phase.NightIdle)) && !human.NightGrind
			&& human.WarmedUp && !human._offlineBreak);
	}

	// ═══ showing your work ══════════════════════════════════════════════════
	/// <summary>
	/// Roll the next seven days the same way the real scheduler does and describe them.
	///
	/// The point of this is that you cannot eyeball a set of probabilities and know whether the result looks like
	/// a person. You can look at a week. If every day is the same length, or the main game is 70% of every single
	/// day, or it never takes a day off, that is visible here in one screen and nowhere else.
	///
	/// It is a sample, not a schedule: the real days are rolled on the day, so tomorrow will differ.
	/// </summary>
	public static List<string> PreviewWeek(BotConfig cfg) {
		List<string> lines = [];
		Random rng = new();
		List<(uint Game, int Weight)> weights = ParseWeights(cfg.GameWeights);

		if (weights.Count == 0) {
			return ["no games set"];
		}

		int Roll(int lo, int hi) => hi <= lo ? lo : rng.Next(lo, hi + 1);
		bool Pct(int pct) => rng.Next(100) < pct;

		for (int day = 0; day < 7; day++) {
			DateTime date = DateTime.Now.Date.AddDays(day);
			bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
			bool freeNight = date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;

			int bed = Math.Clamp(cfg.BedHour, 0, 23);
			int later = Math.Clamp(cfg.LateNightExtraHours, 0, 6);
			int bedHour = freeNight ? Roll(bed + 1, bed + 1 + later) : Pct(40) ? Roll(bed, bed + 1) : Roll(bed - 1, bed);
			bedHour = ((bedHour % 24) + 24) % 24;

			int wake = Math.Clamp(cfg.DayStartHour, 0, 23);
			int lo = Math.Max(0, wake - 1) * 60;
			int hi = Math.Min(23, wake + 3) * 60;
			int wakeAt = (Roll(lo, hi) + Roll(lo, hi)) / 2;

			if (weekend) {
				wakeAt -= Roll(45, 150);
			}

			wakeAt = Math.Clamp(wakeAt, 0, (23 * 60) + 59);

			int hours = Math.Clamp(weekend ? cfg.WeekendHours : cfg.WeekdayHours, 0, 20);
			int full = hours * 60;
			int target;

			if ((full <= 0) || Pct(Math.Clamp(cfg.DayOffChancePct, 0, 100))) {
				target = 0;
			} else {
				int roll = Roll(0, 99);

				target = roll < 20 ? Roll(full * 55 / 100, full * 80 / 100)
					: roll < 65 ? Roll(full * 80 / 100, full * 105 / 100)
					: Roll(full * 105 / 100, full * 125 / 100);
			}

			// The real roller caps the target at four fifths of the waking window. Leaving that out here meant
			// `human week` could promise ten hours on a day the scheduler would hard-limit to six.
			int windowMinutes = bedHour * 60 > wakeAt ? (bedHour * 60) - wakeAt : ((24 * 60) - wakeAt) + (bedHour * 60);
			int fits = windowMinutes * 4 / 5;

			if (target > fits) {
				target = Math.Max(30, fits);
			}

			string when = $"{date:ddd}";

			if (target == 0) {
				lines.Add($"{when}  not playing");

				continue;
			}

			bool pureMain = Pct(Math.Clamp(cfg.PureMainDayChancePct, 0, 100)) || (weights.Count < 2);
			string mix;

			if (pureMain) {
				mix = GameName(weights[0].Game) + " only";
			} else {
				// Mirrors the real roller: the main game's own written share, jittered, decides how much is left
				// for everything else. Reading a separate box here made the forecast disagree with the day.
				int centre = Math.Clamp(weights[0].Weight, 5, 95);
				int mainToday = Math.Clamp(Roll(centre - 10, centre + 10), 5, 95);
				int budget = target * Math.Clamp(100 - mainToday, 1, 95) / 100;
				mix = $"mostly {GameName(weights[0].Game)}, about {Fmt.Hm(Math.Max(20, budget + (budget / 8)))} on the others";
			}

			lines.Add($"{when}  {Fmt.Hm(target),-7} from {wakeAt / 60:00}:{wakeAt % 60:00} to {bedHour:00}:xx   {mix}");
		}

		return lines;
	}
}
