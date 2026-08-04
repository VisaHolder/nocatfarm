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
	private bool _wasFarming;
	private bool _wokeUp;
	private bool _wasGrinding;
	private int _clearReads;

	public override string Name => "human";

	public override string Status {
		get {
			if (!Bot.Cfg.LegitMode) {
				return "off";
			}

			return _phase switch {
				Phase.WarmingUp => $"just signed in · settling for {Left(_readyAt)}",
				Phase.Playing => $"{GameName(_game)} · {Left(_sessionEnds)} left · {Fmt.Hm(_playedMinutesToday)}/{Fmt.Hm(_targetMinutes)} today",
				Phase.SwitchingGame => _switchingTo != 0 ? $"closing the game, then {GameName(_switchingTo)}" : "closing the game",
				Phase.ShortBreak => $"short break · back in {Left(_phaseEnds)}",
				Phase.MealBreak => $"meal break · back in {Left(_phaseEnds)}",
				Phase.NightIdle => $"asleep, banking hours quietly · up {NextWakeTime():HH:mm}",
				Phase.Asleep => $"asleep · up {NextWakeTime():HH:mm}",
				Phase.DoneForToday => $"done for today ({Fmt.Hm(_playedMinutesToday)}) · back {NextWakeTime():HH:mm}",
				Phase.DayOff => $"not playing today · back {NextWakeTime():HH:mm}",
				Phase.StoodDown => "standing down, you're using it",
				_ => "starting up"
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

	/// <summary>
	/// True once the post-login warm-up has finished - the card farmer waits on this so a human-mode account
	/// settles in first (gets online for a bit) and only then starts farming, exactly like it would before
	/// playing. Falls back to a time check for days it never enters the play warm-up (a day off), so the farmer
	/// is never stuck waiting.
	/// </summary>
	public bool WarmedUp => _warmedUp
		|| (Bot.OnlineSince is { } on && DateTime.UtcNow >= on.AddSeconds(SafetyGateSeconds).AddMinutes(Math.Max(1, Bot.Cfg.WarmUpMaxMinutes)));

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
		Bot.ClearPersonaOverride();

		// It's a manual "start now", so skip the random settle - but NOT the owner-safety check. Clearing the
		// timed part of the warm-up lets it start as soon as the clear-reads confirm the owner isn't mid-game,
		// rather than sitting through a fresh ~15-minute settle.
		_readyAt = DateTime.UtcNow;
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
	public string Doing => _phase switch {
		Phase.WarmingUp => "settling in",
		Phase.Playing => "playing " + GameName(_game),
		Phase.SwitchingGame => _switchingTo != 0 ? "closing, then " + GameName(_switchingTo) : "closing the game",
		Phase.ShortBreak => "on a break",
		Phase.MealBreak => "meal break",
		Phase.NightIdle => "asleep, banking hours",
		Phase.Asleep => "asleep",
		Phase.DoneForToday => "done for today",
		Phase.DayOff => "not playing today",
		Phase.StoodDown => "waiting - you're playing on this account",
		_ => "starting up"
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
			Log.Debug($"couldn't bank the session on shutdown: {e.Message}", Bot.Name);
		}

		await base.StopAsync().ConfigureAwait(false);
	}

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			if (!Bot.Cfg.LegitMode) {
				if (_phase != Phase.Off) {
					_phase = Phase.Off;
					Bot.HumanOwned = false;
					Bot.ClearPersonaOverride();
				}

				if (!await Sleep(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			try {
				StepAsync();
			} catch (Exception e) {
				Log.Warn($"human mode hiccup: {e.GetType().Name}: {e.Message}", Bot.Name);
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

		// The human always yields to the actual human - and stays out of the way for the courtesy delay it
		// promised, rather than reappearing on the next twenty-second tick after saying "picking back up in 8m".
		if (!Bot.CanPlay && !Bot.Paused && !Bot.PlayingBlocked) {
			return;
		}

		// A grind outranks the schedule. Banked first, so the hours it puts in still count toward the day
		// rather than vanishing, and the phase is dropped so the day resumes cleanly when it expires.
		// A grind outranks the schedule. On a legit account it doesn't slam over instantly: for the first short
		// beat (GrindStartsAt) the current game keeps playing and the day carries on normally, so it looks like a
		// person finishing up and then switching games. On a non-human account it starts immediately.
		if (Bot.Grinding && (!Bot.HumanOwned || (DateTime.UtcNow >= Bot.GrindStartsAt))) {
			if (!_wasGrinding) {
				_wasGrinding = true;

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
			Bot.ClearPersonaOverride();

			// Put the grind game on directly (idempotent - only re-sends when it isn't already the one running).
			// ReassertPlaying is keyed once-per-logon and would no-op mid-session, so the grind game would never
			// actually start except by the idler's slow backstop; this makes the switch happen on the next tick.
			if (!PlayingExactly([Bot.GrindGame])) {
				Bot.SetPlaying([Bot.GrindGame]);
			}

			return;
		}

		// Grind just finished: ease back into the day instead of snapping straight into a game. Re-arming the
		// warm-up gate makes the flow below take a short settle first, the way it does after waking.
		if (_wasGrinding) {
			_wasGrinding = false;
			_phase = Phase.Off;
			_gateArmedFor = default;
			Log.Info("done grinding - back to the usual day", Bot.Name);
		}

		if (Bot.Paused || Bot.PlayingBlocked) {
			if (_phase != Phase.StoodDown) {
				BankSession();
				_phase = Phase.StoodDown;
				_game = 0;
				_switchingTo = 0;
				Bot.ClearPersonaOverride();
			}

			return;
		}

		if (!InWakingHours(DateTime.Now)) {
			GoToBed();

			return;
		}

		// The card farmer owns the session while it has work - stand off completely. Human mode plays exactly
		// ONE game at a time; a second one on top of the farmer is the loudest bot tell there is. Resume the
		// day when the cards are done.
		if (Bot.IsFarming) {
			_wasFarming = true;
			BankSession();
			_game = 0;
			_switchingTo = 0;
			_phase = Phase.Off;
			Bot.ClearPersonaOverride();   // daytime farming/stand-off looks online, never carrying a night-dark or break-away persona

			return;
		}

		if (_targetMinutes == 0) {
			if (_phase != Phase.DayOff) {
				_phase = Phase.DayOff;
				_game = 0;
				_switchingTo = 0;
				Bot.StopPlaying();
				Bot.ClearPersonaOverride();
				Log.Info($"not playing today - online but idle until bed about {BedTime():HH:mm}", Bot.Name);
			}

			return;
		}

		if (_playedMinutesToday >= _targetMinutes) {
			if (_phase != Phase.DoneForToday) {
				BankSession();
				_phase = Phase.DoneForToday;
				_game = 0;
				_switchingTo = 0;
				Bot.StopPlaying();
				Bot.ClearPersonaOverride();
				Log.Info($"done for the day - {Fmt.Hm(_playedMinutesToday)} played, back around {WakeTime():HH:mm}", Bot.Name);
			}

			return;
		}

		if (_phase is Phase.ShortBreak or Phase.MealBreak or Phase.SwitchingGame) {
			if (DateTime.UtcNow < _phaseEnds) {
				return;
			}

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

		// Warmed up. If there are cards, hand off to the farmer (it takes priority) rather than starting a
		// weighted game on top of it - it will claim on its next tick now that the warm-up is done.
		if (Bot.CardsRemaining > 0) {
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

			// Put down whatever was running before settling in.
			//
			// Waking out of the overnight idle used to leave that game on: the persona went back to visible, the
			// board said "settling for 12m", and the friends list said "In-Game: Counter-Strike 2" the whole time.
			// Somebody who has just signed in is not in a game yet - that is the entire point of the settle.
			Bot.StopPlaying();
			Bot.ClearPersonaOverride();
		}

		if (!_announcedWarmUp) {
			_announcedWarmUp = true;
			int wait = (int) Math.Max(1, (_readyAt - DateTime.UtcNow).TotalMinutes);
			Log.Info(_wokeUp
				? $"awake for the day - warming up for ~{Fmt.Hm(wait)}"
				: $"warming up for ~{Fmt.Hm(wait)}", Bot.Name);
		}

		return false;
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
		if ((_dayStamp >= 0) && (DateTime.Now < WakeTime())) {
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

		_dayStamp = today;
		BotConfig cfg = Bot.Cfg;

		// A plan already rolled for today survives a restart. Without this, every restart handed the account a
		// brand-new target AND reset the minutes it had already played, so three restarts meant three days of
		// playing crammed into one - and a rolled day off could be restarted straight back into an eight-hour
		// session.
		if (HumanDay.Load(Bot.Name, DateTime.Now) is { } saved) {
			Restore(saved);
			Log.Info($"today's plan restored - {Fmt.Hm(_playedMinutesToday)}/{Fmt.Hm(_targetMinutes)} played so far, bed about {BedTime():HH:mm}", Bot.Name);

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
			Log.Info($"taking today off - back tomorrow around {WakeTime():HH:mm}", Bot.Name);

			return;
		}

		string mix = _otherBudget == 0
			? $"{GameName(MainGame())} only today"
			: $"{GameName(MainGame())} about {_mainSharePct}%, up to {Fmt.Hm(_otherBudget)} on the others";

		Log.Info($"today: around {Fmt.Hm(_targetMinutes)} of play, on about {WakeTime():HH:mm}, bed about {BedTime():HH:mm} - {mix}", Bot.Name);
	}

	private int WindowMinutes() {
		int start = _wakeMinuteOfDay;
		int bed = (_bedHour * 60) + _bedMinute;

		return bed > start ? bed - start : ((24 * 60) - start) + bed;
	}

	private DateTime WakeTime() => DateTime.Now.Date.AddMinutes(_wakeMinuteOfDay);

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
		bool banking = Bot.Cfg.OfflineIdleAtNight && (Bot.Cfg.OfflineIdleGames.Count > 0);
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
		if (Bot.IsFarming) {
			if (_phase != want) {
				_phase = want;
				_game = 0;
				_switchingTo = 0;
				Log.Info($"asleep until {WakeTime():HH:mm} - the card farmer keeps working through the night", Bot.Name);
			}

			return;
		}

		if (banking) {
			ReassertPlaying(Bot.Cfg.OfflineIdleGames);

			// Keep the overnight games actually on. After a night farming run ends, the farmer has left the
			// account on its farm game (or nothing) and the once-per-login ReassertPlaying won't fire again -
			// so without this it played nothing the rest of the night while still reading "asleep".
			if (!PlayingExactly(Bot.Cfg.OfflineIdleGames)) {
				Bot.SetPlaying(Bot.Cfg.OfflineIdleGames);

				if (_phase == want) {
					Log.Info($"back to banking hours quietly until {WakeTime():HH:mm}", Bot.Name);
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
			Log.Info($"asleep - banking hours quietly until {WakeTime():HH:mm}", Bot.Name);
		} else {
			Bot.StopPlaying();
			Log.Info($"asleep - back around {WakeTime():HH:mm}", Bot.Name);
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
			Log.Info($"reconnected - put {games.Count} game(s) back on", Bot.Name);
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

		if (_switchingTo != 0) {
			// The gap that just finished was for THIS game, so launch it rather than deciding all over again.
			//
			// Re-picking here was a livelock: the gap ends, a fresh pick lands on yet another game, which is
			// still different from the last one PLAYED (that only updates on a real start), so it goes straight
			// back into the gap. Watching it live, the account sat on "closing the game" for four minutes and
			// would have kept going round until a pick happened to match.
			game = _switchingTo;
			_switchingTo = 0;
		} else {
			// A real player does not change game every time they sit down. Often they carry straight on.
			game = (_lastGame != 0) && Chance(0.40) ? _lastGame : PickGame();

			// Once today's side-game allowance is spent it is the main game for the rest of the day.
			if ((game != main) && !_firstSessionOfDay && (_otherPlayed >= _otherBudget)) {
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

		int minutes = SessionLength(game, main);

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

		Log.Good($"playing {GameName(game)} for about {Fmt.Hm(minutes)}  ({Fmt.Hm(_playedMinutesToday)}/{Fmt.Hm(_targetMinutes)} today)", Bot.Name);
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
		if (_firstSessionOfDay) {
			length = Math.Min(length, Rng(12, 28));
		}

		int remaining = _targetMinutes - _playedMinutesToday;

		if (length > remaining) {
			length = Math.Max(20, remaining + Rng(-5, 15));
		}

		int untilBed = MinutesUntilBed();

		if ((untilBed > 0) && (length > untilBed)) {
			length = untilBed;
		}

		return Math.Max(5, length);
	}

	private void EndSession() {
		BankSession();
		_game = 0;

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
		StepAway(minutes, 3, "short break");   // Away - stepped away from the keyboard
	}

	private void MealBreak() {
		_mealsUsed++;
		int centre = Math.Max(10, Bot.Cfg.MealBreakMinutes);
		int minutes = Chance(0.70) ? Rng(centre - 5, centre + 10) : Rng(centre + 10, centre + 40);

		_phase = Phase.MealBreak;
		_phaseEnds = DateTime.UtcNow.AddMinutes(minutes);
		Bot.StopPlaying();
		StepAway(minutes, 4, "meal break", 2.0);   // Snooze - what Steam does to a real user who walks off
	}

	/// <summary>
	/// Sign out for the break, or just go Away. Real people close Steam sometimes, and an account that is online
	/// for eighteen unbroken hours a day is doing something no person does.
	/// </summary>
	private void StepAway(int minutes, int awayPersona, string what, double weight = 1.0) {
		int pct = Math.Clamp((int) (Bot.Cfg.SignOutOnBreakChancePct * weight), 0, 100);

		if (Percent(pct) && (_signOutsUsed < _signOutCap)) {
			_signOutsUsed++;
			Bot.SetPersonaOverride(Bot.PersonaDark);

			// "Dropped offline", not "signed out" - it stays connected and simply stops being visible, which to
			// everyone on the friends list is the same thing and costs nothing in login rate limit.
			Log.Info($"{what} - going offline, back in about {Fmt.Hm(minutes)}", Bot.Name);

			return;
		}

		Bot.SetPersonaOverride(awayPersona);
		Log.Info($"{what} - back in about {Fmt.Hm(minutes)}", Bot.Name);
	}

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

		if ((_game == 0) || (played == 0)) {
			return;
		}

		lock (_minutesByGame) {
			_minutesByGame[_game] = _minutesByGame.GetValueOrDefault(_game) + played;
		}

		if (_game != MainGame()) {
			_otherPlayed += played;
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

		return playable.Count > 0 ? playable : weights;
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

		if (weights.Count == 1) {
			return weights[0].Game;
		}

		// Lean toward something that still has card drops, before the weighted roll.
		//
		// This is what card farming looks like when it is humanised: no separate module seizing the account and
		// running games back to back, just a day that spends slightly more of its time on whatever still has
		// drops left. Returns 0 most of the time, and then the weights decide as normal.
		uint prefer = PreferDrops(weights);

		if (prefer != 0) {
			return prefer;
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
	public static bool AwakeFor(Bot bot) {
		if (!bot.Cfg.LegitMode || !bot.Cfg.ActOnlyWhileAwake) {
			return true;
		}

		HumanMode? human = bot.Modules.OfType<HumanMode>().FirstOrDefault();

		return human?.Current is not (Phase.Asleep or Phase.NightIdle);
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
