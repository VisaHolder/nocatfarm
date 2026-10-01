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

	/// <summary>
	/// Minutes today's card-farming sittings have played - their own thing, kept apart from the main and side games.
	///
	/// They used to count as side-game time whenever the card game wasn't the main one, so with cards farmed in
	/// "some sittings" two or three of them used up the whole side allowance and the side games and the hunt all but
	/// vanished for the rest of the day, while the cards lasted.
	/// </summary>
	private int _farmPlayed;
	private int _signOutCap;
	private int _signOutsUsed;
	private int _mealCap;
	private int _mealsUsed;
	private int _wakeMinuteOfDay;
	private int _bedHour;
	private int _bedMinute;

	/// <summary>'wake' after bedtime, or after the day's hours are played: up for one more sitting, then bed.</summary>
	private DateTime _stayUpUntil = DateTime.MinValue;
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

	// ── a life, not just a day (each off unless its setting is on) ──
	/// <summary>What it has learned from you playing on the account - read from disk the first time it's wanted.</summary>
	private OwnerHabits? _habits;
	private readonly OwnerWatch _ownerWatch = new();

	/// <summary>His minutes per game, worked out once per change to what's learned rather than on every pick.</summary>
	private Dictionary<uint, int> _learnedMinutes = [];
	private int _learnedVersion = -1;

	private bool _quietDay;
	private bool _lateNight;
	private int _friendJoins;
	private int _newGameSittings;
	private string _joinedToday = "";

	/// <summary>Games that just arrived, worked out in the background every half hour or on a new licence.</summary>
	private List<Trial> _trials = [];
	private DateTime _trialsDue = DateTime.MinValue;
	private int _trialsGeneration = -1;

	// ── settling in ──
	// Nobody signs in and launches a game in the same second. This is when the account is allowed to start, and
	// it is re-armed on every fresh login and every time human mode takes the account over.
	private DateTime _playingAssertedFor = DateTime.MinValue;
	private bool _assertedOnce;
	private DateTime _readyAt = DateTime.MinValue;
	private DateTime _gateArmedFor = DateTime.MinValue;
	private bool _announcedWarmUp;
	private bool _warmedUp;

	/// <summary>
	/// The next settle counts from now, not from the sign-in: getting up in the morning, or coming off a grind. The
	/// account has been signed in all night (or all grind), so counting from the sign-in put the whole settle in the past.
	/// </summary>
	private bool _settleFromNow;

	/// <summary>When it went to bed tonight (local clock), so the clock going back an hour doesn't get it up again.</summary>
	private DateTime _inBedSince = DateTime.MinValue;

	/// <summary>The card farmer has the account for the night - what the night status says instead of banking hours.</summary>
	private bool _nightFarming;

	/// <summary>The sitting in progress is a card-farming one: the farmer plays, this keeps the day's shape around it.</summary>
	private bool _farmSession;

	/// <summary>
	/// The sitting in progress was picked FOR its cards (a farming sitting), rather than being one of the usual games
	/// that happens to have some. Decides how long it runs and whose minutes it is - and it stays set through the
	/// play-on after the last card, which is the tail of the same sitting.
	/// </summary>
	private bool _farmSitting;

	/// <summary>The "closing the game" gap in progress is for a farming sitting.</summary>
	private bool _switchingFarm;

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

		int share = CardShare(Bot.Cfg, Bot.DropsFirstActive, _mainSharePct);

		if ((Bot.EffectiveFarmWhen == FarmWhen.Mixed) && !Chance(share / 100.0)) {
			return 0;
		}

		if (Bot.DropsFirstActive && farmer.Queue.Any(g => (g.AppId == Bot.DropsFirstApp) && (g.CardsRemaining > 0))) {
			return Bot.DropsFirstApp;
		}

		return farmer.NextGame;
	}

	/// <summary>
	/// The share of sittings (percent) that farm cards with "mixed". A drop run ('drops' on this account) gets real weight:
	/// the main game's share of the sittings, or the card-sittings share if that's higher - still one sitting at a time,
	/// with every break and bedtime. That card-sittings share counts only where "mixed" was chosen: on an account set to
	/// farm at night or any time it's hidden (it shows only under "mixed"), and a number nobody can see shouldn't decide
	/// how much of the day a drop run takes - there it's the main game's share.
	/// </summary>
	public static int CardShare(BotConfig cfg, bool dropsRun, int mainSharePct) {
		int share = Math.Clamp(cfg.CardSittingsPct, 5, 95);

		if (!dropsRun) {
			return share;
		}

		int main = Math.Clamp(mainSharePct > 0 ? mainSharePct : 65, 5, 95);

		return cfg.FarmCardsWhen == FarmWhen.Mixed ? Math.Max(share, main) : main;
	}

	/// <summary>How many clear reads in a row, one per tick, before a game may go on.</summary>
	private const int SafetyReadsNeeded = 4;

	/// <summary>The sign-in the owner-safety reads belong to, and how many clear ones it has had in a row.</summary>
	private DateTime _safetyArmedFor = DateTime.MinValue;
	private int _safetyReads;

	/// <summary>
	/// Ticks come every twenty seconds. A gap this long between two of them means the process wasn't running - the PC
	/// was asleep - and the connection it had then may well be dead by now, without anything having noticed yet.
	/// </summary>
	private static readonly TimeSpan SleptGap = TimeSpan.FromMinutes(2);

	/// <summary>After that, the longest it waits for Steam to prove the connection before taking it as fine.</summary>
	private static readonly TimeSpan ProveWithin = TimeSpan.FromMinutes(3);

	private DateTime _lastStepAt = DateTime.MinValue;

	/// <summary>When a gap was noticed, and which sign-in it was on - MinValue when the connection needs no proving.</summary>
	private DateTime _proveSince = DateTime.MinValue;
	private DateTime? _proveLogon;

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
				// Games it picked itself are named, so nobody wonders what is banking all night.
				Phase.NightIdle when NightPicked is { Count: > 0 } picked => Loc.T("asleep, no games chosen - idling your {0} most-played game(s): {1} · up {2}", picked.Count, NightPickedNames(picked), (NextWakeTime()).ToString("HH:mm")),
				Phase.NightIdle => Loc.T("asleep, banking hours quietly on {0} game(s) · up {1}", NightGames().Count, (NextWakeTime()).ToString("HH:mm")),
				// Banking overnight is on by default; with nothing it may play, say so rather than sleep quietly.
				Phase.Asleep when NothingToBank => Loc.T("asleep · up {0} · nothing to bank overnight - add games to the overnight list", (NextWakeTime()).ToString("HH:mm")),
				Phase.Asleep => Loc.T("asleep · up {0}", (NextWakeTime()).ToString("HH:mm")),
				Phase.DoneForToday => Loc.T("done for today ({0}) · back {1}", Fmt.Hm(_playedMinutesToday), (NextWakeTime()).ToString("HH:mm")),
				Phase.DayOff => Loc.T("not playing today · back {0}", (NextWakeTime()).ToString("HH:mm")),
				// Standing down is also what a pause and the wait after you stop look like from in here - say which.
				Phase.StoodDown => Bot.Paused ? Loc.T("paused")
					: Bot.InResumeGrace ? Loc.T("picking back up in a moment")
					: Loc.T("standing down, you're using it"),
				_ => Loc.T("starting up")
			};
		}
	}

	public Phase Current => Bot.Cfg.LegitMode ? _phase : Phase.Off;

	/// <summary>"Bank hours overnight" is on, but there's nothing to bank: no overnight games, no most-played game it may play, and no hour target needing the night.</summary>
	private bool NothingToBank => Bot.Cfg.OfflineIdleAtNight && (NightGames().Count == 0);

	/// <summary>
	/// The games the night banks on when the overnight list is empty - the account's most-played - or none when the list
	/// has games (the list always wins) or banking overnight is off.
	/// </summary>
	public IReadOnlyList<uint> NightPicked => Bot.Cfg.LegitMode && Bot.Cfg.OfflineIdleAtNight && (Bot.Cfg.OfflineIdleGames.Count == 0) ? TopForNight() : [];

	/// <summary>"Counter-Strike 2, Dota 2" - the library's names, for the status and the dashboard.</summary>
	public string NightPickedNames(IReadOnlyList<uint> picked) => string.Join(", ", picked.Select(a => Bot.Library.Find(a)?.Name ?? GameNames.Of(a)));

	/// <summary>How many games bank the night: the list or the most-played picked in its place, and any hour target that needs it.</summary>
	public int NightGameCount => NightGames().Count;
	public int PlayedMinutesToday => _playedMinutesToday;
	public int TargetMinutesToday => _targetMinutes;
	public uint PlayingNow => _phase == Phase.Playing ? _game : 0;

	/// <summary>The account's headline game. The achievement pacer deliberately never writes to this one.</summary>
	public uint MainGameId => MainGame();

	/// <summary>What it has learned from you - for 'human week', which rolls its days the way the real ones are.</summary>
	public OwnerHabits LearnedHabits => Habits;

	/// <summary>
	/// In bed: invisible on the friends list, and nobody can see what it is running.
	///
	/// This is the one window where the ordinary "look like a person" rules stop applying, because there is
	/// nobody to look. During the day the account plays one game at a time because forty at once is the oldest
	/// tell there is - but at 4am, invisible, that argument does not hold, and the card farmer can work
	/// properly instead of the account sitting on a fixed list of games chosen months ago.
	/// </summary>
	public bool InBed => _phase is Phase.Asleep or Phase.NightIdle;

	/// <summary>The date of the day being lived - wake to bed, so still yesterday's in the small hours before bed.
	/// Null before the first day is rolled.</summary>
	public DateTime? PlanDay => _dayStamp < 0 ? null : PlanDate(_dayStamp, DateTime.Now);

	/// <summary>A day-of-year back to a date: one later in the year than today is last year's (New Year's Eve's plan
	/// is still in force just after midnight).</summary>
	public static DateTime PlanDate(int dayOfYear, DateTime now) {
		int year = dayOfYear > now.DayOfYear ? now.Year - 1 : now.Year;

		return new DateTime(year, 1, 1).AddDays(Math.Min(dayOfYear, DateTime.IsLeapYear(year) ? 366 : 365) - 1);
	}

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
	// Not while it's actually settling in (the morning's settle, say): signed in since last night, the time fallback
	// said "warmed up" the minute it woke, and trades and the farmer went ahead while the board said settling in.
	public bool WarmedUp => (_warmedUp && (Bot.OnlineSince is { } armed) && (_gateArmedFor == armed))
		|| ((_phase != Phase.WarmingUp) && SafeToPlay && (Bot.OnlineSince is { } on) && (DateTime.UtcNow >= on.AddSeconds(SafetyGateSeconds).AddMinutes(Math.Max(1, WarmUp.Max))));

	/// <summary>Settling in after a sign-in, as configured and scaled by the reaction speed. The safety gate is on top, never scaled.</summary>
	private (int Min, int Max) WarmUp => ReactionSpeed.Range(Bot.Cfg, nameof(BotConfig.WarmUpMinMinutes), nameof(BotConfig.WarmUpMaxMinutes));

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

			// Whole minutes gone by, the same as this module's own "settling for 4m", so the two lines agree.
			double mins = (_readyAt - DateTime.UtcNow).TotalMinutes;

			return mins <= 0 ? 0 : (int) mins;
		}
	}

	/// <summary>
	/// Skip the rest of the night and start the day now. Pulls today's wake time up to this minute so it counts as
	/// awake, drops the invisible-for-night persona, and falls back to the ordinary wake path (a short settle,
	/// then play/farm) on the next tick. Bed time is untouched, so it still turns in at its usual hour - unless that
	/// has already gone by, or the day's hours are played: then it stays up for one ordinary sitting and goes back to
	/// bed after it. Before, a late 'wake' got up and went straight back to bed, and looked like it did nothing.
	/// </summary>
	public void WakeNow() {
		// One step at a time - see StepAsync.
		lock (_stepGate) {
			// Today's plan first, if it hasn't been rolled yet. It normally waits for the earliest wake time, so 'wake' at 8am
			// ran on last night's plan - its hours already played, so "done for today" - and then at 9:30 today's plan rolled
			// a wake time hours away and sent the account back to bed.
			//
			// But not in the small hours nearer last night's bedtime than the morning: that is still last night, and 'wake'
			// is one more sitting on it. Rolling today there started a whole new day at 1am - hours of play through the
			// night and "done for today" by breakfast - where 'wake' at 23:50 got the one sitting.
			DateTime now = DateTime.Now;
			bool lastNight = (_dayStamp >= 0) && (_dayStamp != now.DayOfYear) && StillLastNight(now, PlanBed(), EarliestWakeToday());

			if (!lastNight && (_dayStamp != now.DayOfYear)) {
				RollNewDayIfNeeded(wakingNow: true);
			}

			int nowMin = (int) (now - now.Date).TotalMinutes;

			if (!lastNight && (_wakeMinuteOfDay > nowMin)) {
				_wakeMinuteOfDay = nowMin;
				Persist();   // or a restart puts the old wake time back and the account to bed
			}

			// Past bedtime, or the day already played (a day off included): one more sitting, like somebody who couldn't sleep.
			if (lastNight || !InPlannedHours(now) || (_playedMinutesToday >= _targetMinutes)) {
				int min = Math.Max(5, Bot.Cfg.SessionMinMinutes);
				int max = Math.Max(min + 5, Bot.Cfg.SessionMaxMinutes);
				_stayUpUntil = now.AddMinutes(Rng(min, max));

				// In its planned hours (a day off, or the day's hours played) it doesn't go to bed after it - it's done for today.
				Log.Info(lastNight || !InPlannedHours(now)
					? new Said("up late - one more sitting, then bed around {0}", _stayUpUntil.ToString("HH:mm"))
					: new Said("up for one sitting until about {0}, then done for today", _stayUpUntil.ToString("HH:mm")), Bot.Name);
				Persist();   // a restart during the sitting keeps it, rather than putting the account back to bed
			}

			_wokeUp = true;
			_phase = Phase.Off;
			ClearBreakState();

			// The overnight games off first. Made visible with them still running, the friends list showed the whole
			// night's list for up to a tick before the wake-up settle put them down.
			if (!Bot.IsFarming) {
				Bot.StopPlaying();
			}

			ShowAs(null);

			// It's a manual "start now", so skip the random settle - but NOT the owner-safety check. Clearing the
			// timed part of the warm-up lets it start as soon as the clear-reads confirm the owner isn't mid-game,
			// rather than sitting through a fresh ~15-minute settle.
			//
			// Armed here for this sign-in, or SettledIn re-arms (bedtime clears the stamp) and rolls a full settle
			// anyway - which is what 'wake' always quietly did.
			DateTime loggedOn = Bot.OnlineSince ?? DateTime.UtcNow;
			DateTime safety = loggedOn.AddSeconds(SafetyGateSeconds);

			_gateArmedFor = loggedOn;
			_settleFromNow = false;
			_clearReads = 0;
			_announcedWarmUp = false;
			_warmedUp = false;
			_readyAt = safety > DateTime.UtcNow ? safety : DateTime.UtcNow;

			// Rep4rep sits the night out until the wake time it was given; that time has just moved to now.
			BotManager.ModuleOf<Rep4RepModule>(Bot)?.DayMoved();
		}
	}

	/// <summary>
	/// 'wake' in the small hours, before today could start and nearer last night's bedtime than the earliest morning:
	/// that is still last night.
	/// </summary>
	internal static bool StillLastNight(DateTime now, DateTime lastBed, DateTime earliestWake) =>
		(now < earliestWake) && (now < lastBed + ((earliestWake - lastBed) / 2));

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
		Phase.NightIdle => new Said("asleep, idling {0} game(s)", NightGames().Count),
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
			// The sign-in time is still there when the modules stop (the Bot clears it only after them, on a stop and on a
			// disconnect alike), so this credits the minutes since the last tick against that sign-in. Already gone means
			// a disconnect stopped the modules and banked this sign-in then - see BankSessionLocked.
			lock (_stepGate) {
				BankSessionOnStop();
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't bank the session on shutdown: {0}", Log.Describe(e)), Bot.Name);
			Log.StackToFile(e, Bot.Name);   // nothing in here talks to Steam - a throw is a bug
		}

		await base.StopAsync().ConfigureAwait(false);
	}

	protected override async Task RunAsync(CancellationToken ct) {
		// The gap watch starts with this run: the module sits stopped for as long as the account is signed out, and that
		// time is not the PC asleep.
		_lastStepAt = DateTime.UtcNow;

		while (!ct.IsCancellationRequested) {
			if (!Bot.Cfg.LegitMode) {
				_lastStepAt = DateTime.UtcNow;   // nor is time spent with human mode switched off
				// Release on the flag as well as the phase. Human mode sits in Off for as long as the card farmer
				// works - hours, sometimes - and switching legit mode off during that left HumanOwned set for good:
				// the idler kept standing aside, the farmer kept to one game at a time, and the account's own games
				// never came back until a restart.
				if ((_phase != Phase.Off) || Bot.HumanOwned) {
					_phase = Phase.Off;
					Bot.HumanOwned = false;
					ShowAs(null);

					// It is an ordinary idling account from here, and those switch straight away. Left to the idler's
					// own re-assert it sat on nothing (or the last human game) for up to seven minutes.
					BotManager.ModuleOf<Idler>(Bot)?.Assert();
				}

				// Switched off while you were on the account: the break's or the night's look it left comes off once you're done.
				if (_personaHeld && !Bot.PlayingBlocked) {
					ShowAs(null);
				}

				if (!await Sleep(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			try {
				await RefreshTrialsAsync().ConfigureAwait(false);
				StepAsync();
			} catch (Exception e) {
				Log.Warn(new Said("human mode hiccup: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);

				// where it broke, once per new kind of failure - the message says what, only the stack says where
				if (Log.DebugOnChange($"hiccup:{Name}:{Bot.Name}", $"{Name}: {Log.Describe(e)}", Bot.Name)) {
					Log.StackToFile(e, Bot.Name);
				}
			}

			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private void StepAsync() {
		// Under the step gate: 'wake', 'human reroll' and 'habits forget' come from the console, the dashboard and the tray,
		// and changed the day (its phase, its plan, the game) halfway through a step that was working from the old one.
		lock (_stepGate) {
			StepLocked();
		}
	}

	private void StepLocked() {
		// Back from the PC being asleep: the sign-in still reads as online until Steam's side of it is noticed gone, so
		// the first tick after waking used to carry on as if nothing happened - "playing Counter-Strike 2 for ~1h40m" in
		// the log, sent down a connection that was already dead, and then the reconnect a minute later. Nothing happens
		// until the connection shows it's alive: something from Steam since, or a fresh sign-in.
		DateTime tick = DateTime.UtcNow;

		if ((_lastStepAt != DateTime.MinValue) && (tick - _lastStepAt > SleptGap)) {
			_proveSince = tick;
			_proveLogon = Bot.OnlineSince;
			Log.Debug(new Said("nothing ran for {0} (the PC was asleep?) - waiting to hear from Steam before doing anything", Fmt.Hm((int) (tick - _lastStepAt).TotalMinutes)), Bot.Name);
		}

		_lastStepAt = tick;

		// Finishing up before logging off: nothing new starts. It started a two-hour sitting two seconds before an
		// update signed it out.
		if (!Bot.IsOnline || Bot.Stopping) {
			return;
		}

		// Before anything that stands down for you: you playing is exactly what it's watching for.
		WatchOwner(DateTime.Now);

		if (_proveSince != DateTime.MinValue) {
			if (!LiveAgain(_proveSince, _proveLogon, Bot.OnlineSince, Bot.LastInbound, tick)) {
				return;
			}

			_proveSince = DateTime.MinValue;
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

				// Back to no clear reads. The count isn't kept while standing down, so the one from before you sat down
				// was still there after you got up, and a single read then was enough to put a game on.
				_clearReads = 0;
			}

			// Paused in the night, it keeps the night's invisible look (and gets up with the morning). Clearing it put a
			// sleeping account up online at 3am, doing nothing, for as long as the pause lasted. While you're the one on
			// the account this sends nothing at all (ShowAs holds it) - it used to drop the break's Away or the night's
			// invisible here, a status change sent to Steam underneath you while you were signed in on your own client.
			ShowAs(Bot.Paused && !InWakingHours(DateTime.Now) ? Bot.PersonaDark : null);

			return;
		}

		// Done standing down, and past the wait after it: the look it should have now goes on - whatever was held while you
		// were on the account.
		if ((_phase == Phase.StoodDown) || _personaHeld) {
			ShowAs(InWakingHours(DateTime.Now) ? null : Bot.PersonaDark);
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
				// Not playing it yet, so the grind's clock waits too - the hours asked for are hours played.
				if (!_wasGrinding) {
					Bot.HoldGrindStart();
				}

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
				ShowAs(null);
			} else {
				ShowAs(Bot.PersonaDark);
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
			_settleFromNow = true;
			Log.Info("done grinding - back to the usual day", Bot.Name);
		}

		// In bed and the clock has gone back behind bedtime (summer time ending repeats an hour): still in bed. It got
		// up again at the second 1am and played until bedtime came round a second time.
		if (!InWakingHours(DateTime.Now) || (InBed && (DateTime.Now < _inBedSince))) {
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
			ShowAs(null);   // daytime farming/stand-off looks online, never carrying a night-dark or break-away persona

			return;
		}

		// Up for one sitting after a 'wake' (see WakeNow) plays it, day off or not - it said so, then sat idle.
		if ((_targetMinutes == 0) && (DateTime.Now >= _stayUpUntil)) {
			if (_phase != Phase.DayOff) {
				BankSession();   // the sitting a 'wake' gave it ends here
				_phase = Phase.DayOff;
				_farmSession = false;
				_game = 0;
				_switchingTo = 0;
				ClearBreakState();
				Bot.StopPlaying();
				ShowAs(null);
				Log.Info(new Said("day off - online, idle until bed ~{0}", (PlanBed()).ToString("HH:mm")), Bot.Name);
			}

			return;
		}

		if ((_playedMinutesToday >= _targetMinutes) && (DateTime.Now >= _stayUpUntil)) {
			if (_phase != Phase.DoneForToday) {
				BankSession();
				_phase = Phase.DoneForToday;
				_farmSession = false;
				_game = 0;
				_switchingTo = 0;
				ClearBreakState();
				Bot.StopPlaying();
				ShowAs(null);
				Log.Info(new Said("done for the day - {0} played, back around {1}", Fmt.Hm(_playedMinutesToday), (NextWakeTime()).ToString("HH:mm")), Bot.Name);
			}

			return;
		}

		if (_phase is Phase.ShortBreak or Phase.MealBreak or Phase.SwitchingGame) {
			// A real client only turns Away after a few idle minutes - not in the second the game closes.
			if ((_breakPersona is int persona) && (DateTime.UtcNow >= _breakPersonaAt)) {
				_breakPersona = null;
				_breakPersonaSet = true;
				_offlineBreak = persona == Bot.PersonaDark;
				ShowAs(persona);

				// Saved there and then, or a restart during the break gave the day that sign-out back.
				if (_offlineBreak) {
					_signOutsUsed++;
					Persist();
				}
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
				ShowAs(null);
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
				// Read once: the list is swapped whole by other threads, and read twice it could be one game, then none.
				if (Bot.IsFarming && (Bot.PlayingApps is [uint only])) {
					_game = only;
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
			ShowAs(null);

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

	/// <summary>
	/// How long until this sign-in's warm-up is over - what a grind asked for now waits for on top of finishing up.
	/// Before the warm-up is rolled, the longest it could be.
	/// </summary>
	public TimeSpan SettleLeft {
		get {
			DateTime loggedOn = Bot.OnlineSince ?? DateTime.UtcNow;
			DateTime ready = _gateArmedFor == loggedOn ? _readyAt : loggedOn.AddMinutes(Math.Max(0, WarmUp.Max));
			TimeSpan left = ready - DateTime.UtcNow;

			return left > TimeSpan.Zero ? left : TimeSpan.Zero;
		}
	}

	private bool SettledIn() {
		DateTime loggedOn = Bot.OnlineSince ?? DateTime.UtcNow;

		// Re-arm on a fresh login, and on the first tick after human mode takes the account over.
		if (_gateArmedFor != loggedOn) {
			_gateArmedFor = loggedOn;
			_clearReads = 0;
			_announcedWarmUp = false;
			_warmedUp = false;

			(int warmLo, int warmHi) = WarmUp;
			int lo = Math.Max(0, warmLo);
			int hi = Math.Max(lo, warmHi);

			// Counted from the sign-in itself, not from when human mode first looked: an account that has been online
			// a while (done for the day, then a grind asked for) is long past its warm-up, and was made to sit through
			// a fresh 3-20 minutes anyway.
			//
			// But from now when it's getting up in the morning or coming off a grind. Signed in since last night, the
			// sign-in's settle was hours in the past, and it went from asleep straight into a game.
			DateTime from = _settleFromNow && InWakingHours(DateTime.Now) ? DateTime.UtcNow : loggedOn;
			_settleFromNow = false;
			DateTime safety = loggedOn.AddSeconds(SafetyGateSeconds);
			DateTime settled = from.AddMinutes(Rng(lo, hi));
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
			// Not a settle in the night (before a grind asked for at 3am) - that said "awake for the day".
			_wokeUp = (_phase is Phase.NightIdle or Phase.Asleep) && InWakingHours(DateTime.Now);
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
				ShowAs(null);
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

	/// <summary>A look human mode wanted while you were on the account, and didn't send.</summary>
	private bool _personaHeld;

	/// <summary>
	/// Put a look on the account - invisible, Away, Snooze - or take it off (null). Every one human mode sends goes through
	/// here, so none of them reaches Steam while you're on the account: a persona sent from this session while you're
	/// signed in on your own client can sign one of the two out of Friends and Chat. It's held instead, and the right
	/// one for the time goes on once the stand-down is over (see StepAsync).
	/// </summary>
	private void ShowAs(int? persona) {
		if (Bot.PlayingBlocked) {
			_personaHeld = true;

			return;
		}

		_personaHeld = false;

		if (persona is int state) {
			Bot.SetPersonaOverride(state);
		} else {
			Bot.ClearPersonaOverride();
		}
	}

	// ═══ the day ════════════════════════════════════════════════════════════
	/// <summary>
	/// Throw today's plan away and roll a new one from the settings as they stand now.
	///
	/// A day is rolled once, at wake time, and then persisted so restarts don't hand out a fresh eight hours
	/// every time. That is right, but it means changing the hours, the bedtime or the weights does nothing
	/// visible until tomorrow - the setting saves, the log says nothing, and the account carries on to a target
	/// rolled against the old numbers. This is the way to say "apply it now" without waiting a day.
	/// </summary>
	public void RerollToday() {
		// One step at a time - see StepAsync.
		lock (_stepGate) {
			DateTime now = DateTime.Now;

			// The day being lived is the one rolled again - which in the small hours before last night's bedtime is still
			// last night. Rolled as today instead, the new plan's morning was hours away and the account went to bed in the
			// middle of its evening.
			DateTime date = (_dayStamp >= 0) && (_dayStamp != now.DayOfYear) && (now < PlanEnds()) ? PlanDate(_dayStamp, now) : now.Date;
			DateTime lastBed = (_dayStamp >= 0) && (PlanDate(_dayStamp, now) < date) ? PlanEnds() : DateTime.MinValue;

			// Up and about, it stays up: a new plan whose wake time happened to land later sent it back to bed at noon.
			bool wasUp = (_dayStamp >= 0) && InWakingHours(now);

			if (_phase == Phase.Playing) {
				BankSession();
				Bot.StopPlaying();
			}

			HumanDay.Forget(Bot.Name);

			if (_breakPersonaSet) {
				ShowAs(null);
			}

			ClearBreakState();
			_phase = Phase.Off;
			_dayStamp = -1;
			_game = 0;
			_lastGame = 0;
			_switchingTo = 0;

			Log.Info("new plan for today from the current settings", Bot.Name);
			RollFor(date, lastBed);

			if (wasUp && (now < PlanWake())) {
				_wakeMinuteOfDay = Math.Clamp((int) (now - PlanDate(_dayStamp, now)).TotalMinutes, 0, (23 * 60) + 59);
				Persist();
			}
		}
	}

	/// <param name="wakingNow">'wake': today starts now, so today's plan is wanted even before its earliest wake time.</param>
	private void RollNewDayIfNeeded(bool wakingNow = false) {
		DateTime now = DateTime.Now;

		if (_dayStamp == now.DayOfYear) {
			return;
		}

		// Not at calendar midnight. A wake->bed cycle routinely runs past it (e.g. bed 02:48), and rolling at 00:00 split
		// it - the post-midnight hours got counted into THIS morning's total, so the account read "4h played" at noon
		// having woken at 11. The new day starts once last night is over - its bedtime, or the 'wake' sitting after it -
		// AND it's past the EARLIEST today's wake could be. Never yesterday's wake minute: keyed to that, a day that rolled
		// an earlier wake was already "awake" the moment it rolled, so on about half of all days the account got up at
		// exactly the same minute as the day before. And not the earliest wake alone: with the day starting at 2 or
		// earlier that is midnight, and the new day took over while last night's bedtime was still to come.
		// (_dayStamp < 0 means a fresh start with no plan yet - that must still roll immediately.)
		if (!wakingNow && (_dayStamp >= 0) && !NewDayMayStart(now, EarliestWakeToday(), PlanEnds())) {
			return;
		}

		// Close out whatever is in flight FIRST - a grind, or a 'wake' sitting ending right on the change of day. Zeroing
		// the counters underneath it lost its time and left Steam playing a game the scheduler had already forgotten about.
		if (_phase == Phase.Playing) {
			BankSession();

			// Not a grind's game, though: the grind carries on into the new day, and closing it here only had the grind
			// put it straight back on in the same tick - the game closed and reopened for nothing.
			if (!_wasGrinding) {
				Bot.StopPlaying();
			}

			_phase = Phase.Off;
		}

		// When last night really ended. Today's wake is never before it, plus a night's sleep.
		DateTime lastBed = _dayStamp >= 0 ? PlanEnds() : DateTime.MinValue;

		// Started after midnight but before last night was over: its plan is the one in force. Rolling a fresh day here
		// counted the rest of last night against today - the split that waiting above exists to prevent, lost on every
		// restart in the small hours.
		if (!wakingNow && (_dayStamp < 0) && (HumanDay.Load(Bot.Name, now.AddDays(-1)) is { } lastNight)) {
			Restore(lastNight);
			_dayStamp = now.AddDays(-1).DayOfYear;

			if (!NewDayMayStart(now, EarliestWakeToday(), PlanEnds())) {
				Log.Info(new Said("last night's plan restored - {0}/{1} played, up about {2}", Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes), (NextWakeTime()).ToString("HH:mm")), Bot.Name);

				return;
			}

			lastBed = PlanEnds();
		}

		RollFor(now.Date, lastBed);
	}

	/// <summary>The plan for <paramref name="date"/>: the one already saved for it, or a fresh roll.</summary>
	/// <param name="lastBed">When the night before really ended - MinValue when that isn't known.</param>
	private void RollFor(DateTime date, DateTime lastBed) {
		_dayStamp = date.DayOfYear;
		_stayUpUntil = DateTime.MinValue;   // last night's 'wake' sitting doesn't carry into a new day's plan

		// A plan already rolled for today survives a restart. Without this, every restart handed the account a
		// brand-new target AND reset the minutes it had already played, so three restarts meant three days of
		// playing crammed into one - and a rolled day off could be restarted straight back into an eight-hour
		// session.
		if (HumanDay.Load(Bot.Name, date) is { } saved) {
			Restore(saved);
			Log.Info(new Said("today's plan restored - {0}/{1} played, bed about {2}", Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes), (PlanBed()).ToString("HH:mm")), Bot.Name);

			return;
		}

		// The main game's own written share is the centre of today's roll. It used to be ignored outright in
		// favour of a separate box, so the number typed beside the main game did nothing at all and only its
		// position in the list carried meaning.
		List<(uint Game, int Weight)> spread = Weights();
		DayRoll roll = RollDayWith(Bot.Cfg, date, Math.Clamp(spread.Count > 0 ? spread[0].Weight : 70, 5, 95), spread.Count >= 2, lastBed, _rng, Extras(date));

		_wakeMinuteOfDay = roll.WakeMinute;
		_bedHour = roll.BedHour;
		_bedMinute = roll.BedMinute;
		_bedIsTomorrow = roll.BedIsTomorrow;
		_targetMinutes = roll.Target;
		_mainSharePct = roll.MainSharePct;
		_otherBudget = roll.OtherBudget;
		_signOutCap = roll.SignOutCap;
		_mealCap = roll.MealCap;
		_quietDay = roll.Quiet;
		_lateNight = roll.LateNight;
		_friendJoins = 0;
		_newGameSittings = 0;
		_joinedToday = "";

		_playedMinutesToday = 0;
		_otherPlayed = 0;
		_farmPlayed = 0;
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
			Log.Info(new Said("taking today off - back tomorrow around {0}", (NextWakeTime()).ToString("HH:mm")), Bot.Name);

			return;
		}

		Said mix = _otherBudget == 0
			? new Said("{0} only today", GameName(MainGame()))
			: new Said("{0} about {1}%, around {2} on the others", GameName(MainGame()), _mainSharePct, Fmt.Hm(SideExpected(_targetMinutes, _mainSharePct)));

		Log.Info(new Said("today: ~{0} of play, up {1}, bed {2}", Fmt.Hm(_targetMinutes), (PlanWake()).ToString("HH:mm"), (PlanBed()).ToString("HH:mm")), Bot.Name);
		Log.Debug(new Said("today's mix: {0}", mix), Bot.Name);

		if (_quietDay) {
			Log.Info(new Said("a quiet spell - shorter days for a while"), Bot.Name);
		} else if (_lateNight) {
			Log.Info(new Said("a late one tonight - bed around {0}", (PlanBed()).ToString("HH:mm")), Bot.Name);
		}
	}

	/// <summary>A mixed day's side-game allowance, as a multiple of the side games' share of it (see RollDay).</summary>
	internal const int SideAllowanceTimes = 3;

	/// <summary>
	/// What the side games can expect on a mixed day: their share of it. The allowance is a cap well above that, so it is
	/// not the number to show anybody - the day's log line said "up to 2h54m on the others" for a day that, like 'human week'
	/// and 'human' said, would put about an hour and a half on them.
	/// </summary>
	internal static int SideExpected(int target, int mainSharePct) => Math.Max(20, target * Math.Clamp(100 - mainSharePct, 1, 95) / 100);

	/// <summary>One day's plan as rolled: getting up and going to bed (on the plan's own date), how long it plays, and the caps.</summary>
	internal readonly record struct DayRoll(int WakeMinute, int BedHour, int BedMinute, bool BedIsTomorrow, int Target, int MainSharePct, int OtherBudget,
		int SignOutCap, int MealCap, bool Quiet = false, bool LateNight = false);

	/// <summary>
	/// What today's roll takes on top of the settings: its place in a quiet spell or a late night ("Quiet spells and late
	/// nights"), and your own habits with how hard they pull ("Learn from how I play"). Null when neither is on - the day is
	/// then rolled exactly as it always was.
	/// </summary>
	private DayExtras? Extras(DateTime date) {
		double pull = HumanHabits.Pull(Bot.Cfg.LearnFromOwner);
		OwnerHabits? habits = LearningInUse ? Habits : null;

		return !Bot.Cfg.LongerRhythms && (habits == null) ? null
			: new DayExtras(Bot.Cfg.LongerRhythms ? HumanHabits.RhythmFor(Bot.Name, date) : default, habits, pull);
	}

	/// <summary>
	/// Roll one day. A real week is not flat: weekday gaming is mostly evenings, weekends start earlier and run
	/// longer, and Friday and Saturday nights go late because nothing is on tomorrow. The real day and 'human week'
	/// both roll here, so the preview can't drift from what the day actually does, as it had before.
	/// </summary>
	/// <param name="date">The plan's own calendar day; its weekday sets the shape.</param>
	/// <param name="mainCentre">The main game's written share, the centre of today's roll.</param>
	/// <param name="hasSides">Whether there's anything to play besides the main game.</param>
	/// <param name="lastBed">When the night before really ended - MinValue when that isn't known.</param>
	internal static DayRoll RollDay(BotConfig cfg, DateTime date, int mainCentre, bool hasSides, DateTime lastBed, Random rng) =>
		RollDayWith(cfg, date, mainCentre, hasSides, lastBed, rng, null);

	/// <summary>
	/// <see cref="RollDay"/> with the day's extras. Every one of them only touches the dice when it's switched on, so with
	/// <paramref name="extras"/> null this is the very roll it always was, number for number.
	/// </summary>
	internal static DayRoll RollDayWith(BotConfig cfg, DateTime date, int mainCentre, bool hasSides, DateTime lastBed, Random rng, DayExtras? extras) {
		int Roll(int lo, int hi) => hi <= lo ? lo : rng.Next(lo, hi + 1);   // inclusive, like the plugin's helper
		bool Pct(int pct) => rng.Next(100) < pct;

		DayOfWeek dow = date.DayOfWeek;
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
		int rolled = freeNight ? Roll(bed + 1, bed + 1 + later) : Pct(40) ? Roll(bed, bed + 1) : Roll(bed - 1, bed);
		int bedHour = ((rolled % 24) + 24) % 24;
		int bedMinute = Roll(0, 59);

		// GETTING ON. Averaging two rolls gives a natural bell around the configured hour instead of a flat pick,
		// and the minute is always jittered so it is never 13:00:00 sharp two days running.
		int start = Math.Clamp(cfg.DayStartHour, 0, 23);
		int lo = Math.Max(0, start - 1) * 60;
		int hi = Math.Min(23, start + 3) * 60;
		int wake = (Roll(lo, hi) + Roll(lo, hi)) / 2;

		// Weekends start earlier - by no more than two thirds of the way back to midnight, though. With the day starting at
		// 2 or earlier the full head start went past midnight, and every one of those weekend days got up at 00:00 sharp.
		if (weekend) {
			wake -= Math.Min(Roll(45, 150), wake * 2 / 3);
		}

		// Learning from you: one of your real days, and today leans part of the way toward when you got on then. A real
		// day of yours rather than your average, and a little jitter on top, so it never settles on one minute.
		(int Start, int End)? yours = (extras?.Habits is { } habits) && (extras.Pull > 0) ? habits.SampleDay(rng) : null;

		if (yours is { } learned) {
			wake = HumanHabits.Nudge(wake, learned.Start, extras!.Pull) + Roll(-20, 20);
		}

		// Never up before last night is over, nor straight after it: a night's sleep first, a different length every
		// time. With the day starting at 2, a late Friday rolled a Saturday that got up before Friday had gone to bed.
		if (lastBed != DateTime.MinValue) {
			wake = Math.Max(wake, (int) Math.Ceiling((lastBed.AddMinutes(Roll(240, 360)) - date.Date).TotalMinutes));
		}

		wake = Math.Clamp(wake, 0, (23 * 60) + 59);

		// Whether bedtime belongs to tomorrow is a property of the SETTINGS, not of what the dice did. Deriving
		// it from the rolled values meant that when the two rolls crossed - easy whenever bed and wake are within
		// a few hours of each other - bedtime jumped a full day and the account counted itself awake round the
		// clock, playing its whole target from midnight.
		bool bedIsTomorrow = (rolled >= 24) || (bedHour <= start);

		// Your usual stopping time pulls bedtime the same way, and a late night pushes it on. Worked on the clock as minutes
		// from this morning's midnight, then put back into hour, minute and day - never less than an hour and a half after
		// getting up, and never so late it runs into the next day's start hour.
		bool late = extras?.Rhythm.LateNight == true;

		if ((yours != null) || late) {
			int bedAt = (bedIsTomorrow ? 24 * 60 : 0) + (bedHour * 60) + bedMinute;

			if (yours is { } learned2) {
				bedAt = HumanHabits.Nudge(bedAt, learned2.End, extras!.Pull) + Roll(-20, 20);
			}

			if (late) {
				bedAt += extras!.Rhythm.LateMinutes;
			}

			bedAt = Math.Clamp(bedAt, wake + 90, Math.Max(wake + 90, ((start + 23) * 60) + 30));
			bedIsTomorrow = bedAt >= 24 * 60;
			bedHour = bedAt / 60 % 24;
			bedMinute = bedAt % 60;
		}

		if (!bedIsTomorrow) {
			wake = Math.Min(wake, Math.Max(0, ((bedHour * 60) + bedMinute) - 60));
		}

		// HOURS. Not a flat grind - an enthusiast who is around most days, has quiet days and the odd day off.
		int hours = Math.Clamp(weekend ? cfg.WeekendHours : cfg.WeekdayHours, 0, 20);
		int full = hours * 60;
		int target;

		if ((full <= 0) || Pct(Math.Clamp(cfg.DayOffChancePct, 0, 100))) {
			target = 0;
		} else {
			int roll = Roll(0, 99);

			// a quiet day · a normal day · a long one
			target = roll < 20 ? Roll(full * 55 / 100, full * 80 / 100)
				: roll < 65 ? Roll(full * 80 / 100, full * 105 / 100)
				: Roll(full * 105 / 100, full * 125 / 100);

			// A quiet spell: a noticeably shorter day, for a few days running. After the day off, before the window cap -
			// both still have their say.
			if (extras?.Rhythm.Quiet == true) {
				target = Math.Max(30, target * extras.Rhythm.QuietPct / 100);
			}
		}

		// A day cannot hold more play than the hours it is awake. Four fifths of the window leaves room for the
		// breaks, the meals and the gaps between games.
		if (target > 0) {
			int fits = WindowMinutes(wake, bedHour, bedMinute, bedIsTomorrow) * 4 / 5;

			if (target > fits) {
				target = Math.Max(30, fits);
			}
		}

		int mainShare = Math.Clamp(Roll(mainCentre - 10, mainCentre + 10), 5, 95);
		int otherBudget;

		// SIDE GAMES COME IN BURSTS. Most days are pure main game; sometimes there is a real sitting on something
		// else. A guaranteed slice of every side game every single day is a pattern, not a person.
		if (Pct(Math.Clamp(cfg.PureMainDayChancePct, 0, 100)) || !hasSides) {
			otherBudget = 0;
		} else {
			// Derived from the very share the picker is rolling against, with room on top. A budget set from its own
			// independent number was a second limit on the same minutes: whichever happened to be tighter won, and the
			// weights quietly became fiction whenever it was this one.
			//
			// Three times the side games' share, not an eighth over it. A day is only a handful of sittings, so its
			// side-game time swings a lot either way; a cap that close cut off every day that swung up while nothing made
			// up for the days that swung down, and a main game set to 70% played 76-79% of the time. Twice still did it on
			// a smaller scale - a point over on the defaults, more on a main game set high, where one side-game sitting is
			// already most of the day's side time. At three times it still stops a runaway day of side games, and the
			// average stays where the number says.
			int side = Math.Clamp(100 - mainShare, 1, 95);
			otherBudget = Math.Max(20, target * side / 100 * SideAllowanceTimes);
		}

		int signOuts = Math.Clamp(cfg.MaxSignOutsPerDay, 0, 40);
		// Both at most what the setting says. Rng includes its top end, so "+ 3" let "Drop offline at most 1" drop offline
		// four times, and "Meals a day 2" take three.
		int signOutCap = signOuts == 0 ? 0 : Roll(Math.Max(1, signOuts - 3), signOuts);
		int mealCap = cfg.MealBreaksPerDay <= 0 ? 0 : Roll(1, cfg.MealBreaksPerDay);

		return new DayRoll(wake, bedHour, bedMinute, bedIsTomorrow, target, mainShare, otherBudget, signOutCap, mealCap,
			extras?.Rhythm.Quiet == true, late);
	}

	/// <summary>Minutes from getting up to going to bed.</summary>
	/// <remarks>
	/// Worked out from whether bed is tomorrow, not from which clock reading is bigger. A Friday up at 4am with bed at
	/// 5am the next morning read as a one-hour day (5 comes after 4), and its target was cut to half an hour.
	/// </remarks>
	internal static int WindowMinutes(int wake, int bedHour, int bedMinute, bool bedIsTomorrow) {
		int bed = (bedHour * 60) + bedMinute;

		return bedIsTomorrow ? ((24 * 60) - wake) + bed : Math.Max(0, bed - wake);
	}

	/// <summary>A plan's wake time, on the plan's own date.</summary>
	internal static DateTime WakeOn(DateTime planDate, int wakeMinute) => planDate.Date.AddMinutes(wakeMinute);

	/// <summary>A plan's bedtime, on the plan's own date - the next day's small hours when bed is "tomorrow".</summary>
	internal static DateTime BedOn(DateTime planDate, int bedHour, int bedMinute, bool bedIsTomorrow) =>
		planDate.Date.AddDays(bedIsTomorrow ? 1 : 0).AddHours(bedHour).AddMinutes(bedMinute);

	/// <summary>A new day may start once last night is over and it's past the earliest its wake could be.</summary>
	internal static bool NewDayMayStart(DateTime now, DateTime earliestWake, DateTime lastNightEnds) => (now >= earliestWake) && (now >= lastNightEnds);

	/// <summary>
	/// Inside a plan's own hours: from its wake time to its bedtime, both on the plan's own date.
	///
	/// These were read off today's calendar date, with "before today's wake time, last night may still be up" patched
	/// on top. Once a plan was rolled before its wake time, the patch took the NEW plan's bedtime for last night's - so
	/// with the day starting at 2 and a plan rolled at midnight, an account that went to bed at 23:40 was back up at
	/// 00:10. Last night's own plan stays in force until its own bedtime now, so there is nothing to patch.
	/// </summary>
	internal static bool InPlan(DateTime now, DateTime planDate, int wakeMinute, int bedHour, int bedMinute, bool bedIsTomorrow) =>
		(now >= WakeOn(planDate, wakeMinute)) && (now < BedOn(planDate, bedHour, bedMinute, bedIsTomorrow));

	private DateTime PlanWake() => WakeOn(PlanDate(_dayStamp, DateTime.Now), _wakeMinuteOfDay);

	private DateTime PlanBed() => BedOn(PlanDate(_dayStamp, DateTime.Now), _bedHour, _bedMinute, _bedIsTomorrow);

	/// <summary>When the plan in force is over: its bedtime, or the end of a 'wake' sitting after it.</summary>
	private DateTime PlanEnds() {
		DateTime bed = PlanBed();

		return _stayUpUntil > bed ? _stayUpUntil : bed;
	}

	/// <summary>The earliest today's wake roll can land: an hour before the day-start hour, less the weekend's head start.</summary>
	private DateTime EarliestWakeToday() => DateTime.Now.Date.AddMinutes(Math.Max(0, (Math.Clamp(Bot.Cfg.DayStartHour, 0, 23) - 1) * 60 - 150));

	/// <summary>
	/// The next time this account gets up, which is tomorrow once today's has been and gone.
	///
	/// Today's plan's own wake time while that is still ahead. Once it has been and gone - an account that finished at
	/// half nine in the evening is not getting up at this morning's time - tomorrow's plan isn't rolled yet, so today's
	/// time of day stands in for it. Reading this morning's as "how long until it wakes" clamped a negative eleven hours
	/// to zero and reported "any moment", all night.
	/// </summary>
	private DateTime NextWakeTime() {
		DateTime now = DateTime.Now;
		DateTime planned = PlanWake();

		if (planned > now) {
			return planned;
		}

		DateTime wake = now.Date.AddMinutes(_wakeMinuteOfDay);

		return wake > now ? wake : wake.AddDays(1);
	}

	/// <summary>True when the games Steam is actually running are exactly this set (order-independent).</summary>
	private bool PlayingExactly(IReadOnlyCollection<uint> games) {
		IReadOnlyList<uint> now = Bot.PlayingApps;

		return (now.Count == games.Count) && games.All(g => now.Contains(g));
	}

	private bool InWakingHours(DateTime now) => InPlannedHours(now) || (now < _stayUpUntil);

	/// <summary>Today's plan alone: from the wake time to bedtime.</summary>
	private bool InPlannedHours(DateTime now) => (_dayStamp >= 0) && InPlan(now, PlanDate(_dayStamp, now), _wakeMinuteOfDay, _bedHour, _bedMinute, _bedIsTomorrow);

	private int MinutesUntilBed() {
		// Up late after a 'wake': bed is when that sitting ends.
		if (_stayUpUntil > DateTime.Now) {
			return (int) (_stayUpUntil - DateTime.Now).TotalMinutes;
		}

		// The plan's own bedtime, on its own date - so a sitting started at 01:30 with bed at 02:30 reads an hour to go,
		// is shortened to fit, and ends with the usual break rather than being cut off by bedtime.
		return (int) Math.Max(0, (PlanBed() - DateTime.Now).TotalMinutes);
	}

	private void GoToBed() {
		// Re-arm the warm-up gate for the morning. It's keyed on the login timestamp, which doesn't change
		// across an overnight sleep, so without this the account snaps straight from asleep into a game at wake
		// with no settle. Cleared here (only reached when asleep), it re-arms on the first waking tick.
		_gateArmedFor = DateTime.MinValue;
		_settleFromNow = true;
		_settlingAfterGrind = false;   // the morning's settle is getting up, not coming off last night's grind

		if (!InBed) {
			_inBedSince = DateTime.Now;
		}

		BankSession();
		_farmSession = false;
		ClearBreakState();   // bedtime ends a break too; the night's invisible look goes on just below

		// Cards farm in the day, so bedtime ends the sitting. The farmer lets go within seconds of it closing; the
		// overnight games go on once it has, rather than both fighting over what's playing.
		if (FarmInDay && Bot.IsFarming) {
			ShowAs(Bot.PersonaDark);

			return;
		}
		List<uint> night = NightGames();
		bool banking = Bot.Cfg.OfflineIdleAtNight && (night.Count > 0);
		Phase want = banking ? Phase.NightIdle : Phase.Asleep;

		// Invisible for the night. This is a no-op once it's already set - the override is remembered on the bot
		// and re-applied by the logon handler, which is what carries it across a reconnect. Steam resetting the
		// persona when a game starts is handled inside SetPlaying, which re-applies it after the games message.
		ShowAs(Bot.PersonaDark);

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
				Log.Info(new Said("asleep until {0} - card farming through the night", (NextWakeTime()).ToString("HH:mm")), Bot.Name);
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
					Log.Info(new Said("back to banking hours quietly until {0}", (NextWakeTime()).ToString("HH:mm")), Bot.Name);
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
			Log.Info(NightPicked is { Count: > 0 } picked
				? new Said("asleep - no games chosen, so banking hours quietly on your most-played until {0}: {1}", (NextWakeTime()).ToString("HH:mm"), NightPickedNames(picked))
				: new Said("asleep - banking hours quietly until {0}", (NextWakeTime()).ToString("HH:mm")), Bot.Name);
		} else {
			Bot.StopPlaying();
			Log.Info(NothingToBank
				? new Said("asleep - back around {0}. Nothing to bank overnight: add games to the overnight list", (NextWakeTime()).ToString("HH:mm"))
				: new Said("asleep - back around {0}", (NextWakeTime()).ToString("HH:mm")), Bot.Name);
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
				ShowAs(null);   // never leave a break's Away/Snooze stuck on for the rest of the day
				Log.Warn("human mode has no games - fill in \"Games and how often\"", Bot.Name);
			}

			return;
		}

		uint game;
		bool farmPick;
		uint farm = FarmGameNow();

		if (_switchingTo != 0) {
			// The gap that just finished was for THIS game, so launch it rather than deciding all over again.
			//
			// Re-picking here was a livelock: the gap ends, a fresh pick lands on yet another game, which is
			// still different from the last one PLAYED (that only updates on a real start), so it goes straight
			// back into the gap. Watching it live, the account sat on "closing the game" for four minutes and
			// would have kept going round until a pick happened to match.
			game = _switchingTo;
			farmPick = _switchingFarm;
			_switchingTo = 0;
		} else if (farm != 0) {
			// Cards first: in a farming sitting, the card game is what the sitting plays.
			game = farm;
			farmPick = true;
		} else {
			// The hour targets, the side allowance and carrying on with the same game are all part of the pick now, so they
			// can be counted in the shares. Carrying on used to be decided out here, ahead of the pick and outside its sums.
			game = PickGame();
			farmPick = false;
		}

		// Closing one game and launching another is not instant - when there is a game to close. After a break, a
		// settle-in or a stand-down nothing is running (the break already put it down), and the board said "closing the
		// game" for up to four minutes over nothing at all. The break was the gap.
		if (NeedsSwitchGap(game, Bot.PlayingApps)) {
			_switchingTo = game;
			_switchingFarm = farmPick;
			_phase = Phase.SwitchingGame;
			_phaseEnds = DateTime.UtcNow.AddMinutes(Rng(1, 4));
			_game = 0;
			Bot.StopPlaying();

			return;
		}

		// The farmer has the sitting whenever its game still has cards: a farming sitting, or one of the usual games that
		// happens to have some, where it only keeps count of the drops.
		_farmSession = FarmInDay && (Bot.CardsRemaining > 0) && (farmPick
			|| (BotManager.ModuleOf<CardFarmer>(Bot)?.Queue.Any(g => (g.AppId == game) && (g.CardsRemaining > 0)) ?? false));

		// A farming sitting runs like a main-game one - real sittings, not the short dips side games get. One of the
		// usual games runs as what it is: a side game that happened to have cards used to get the main game's long
		// sittings, and far more than its share of the day.
		_farmSitting = farmPick;
		// A new game being tried out gets a real sitting too, not a side game's dip - that's what trying something is.
		int minutes = SessionLength(game, farmPick || IsTargetGame(game) || IsTrialGame(game) ? game : main);

		_game = game;
		_lastGame = game;
		_firstSessionOfDay = false;
		_phase = Phase.Playing;
		_sessionStarted = DateTime.UtcNow;
		_sessionEnds = _sessionStarted.AddMinutes(minutes);
		_bankedTo = _sessionStarted;
		_bankedForLogon = Bot.OnlineSince ?? DateTime.UtcNow;

		ShowAs(null);
		Bot.SetPlaying([game]);   // ONE game. Six at once is the tell.
		_playingAssertedFor = Bot.OnlineSince ?? DateTime.UtcNow;

		Log.Good(_farmSession
			? new Said("farming cards on {0} for ~{1} ({2}/{3} today)", GameName(game), Fmt.Hm(minutes), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes))
			: new Said("playing {0} for ~{1} ({2}/{3} today)", GameName(game), Fmt.Hm(minutes), Fmt.Hm(_playedMinutesToday), Fmt.Hm(_targetMinutes)), Bot.Name);
	}

	/// <summary>
	/// Whether starting <paramref name="next"/> means closing another game first: only when something else is really
	/// running. Nothing running is nothing to close.
	/// </summary>
	internal static bool NeedsSwitchGap(uint next, IReadOnlyCollection<uint> running) => (running.Count > 0) && !((running.Count == 1) && running.Contains(next));

	/// <summary>
	/// The side games' allowance for the part of the day that isn't card farming. The allowance is their share of the
	/// day; farming sittings are their own thing, so what they take comes off the day that share is of - rather than out
	/// of the side games' own minutes, which is what used to happen.
	/// </summary>
	internal static int SideBudgetNow(int otherBudget, int target, int farmPlayed) =>
		(otherBudget <= 0) || (target <= 0) ? otherBudget : (int) ((long) otherBudget * Math.Max(0, target - farmPlayed) / target);

	/// <summary>
	/// Whether a side game may have this sitting: while today's side allowance lasts, and never on a main-game-only day.
	/// The first sitting of the day used to be let off, and that was the only sitting it changed - so a day announced
	/// as "Counter-Strike 2 only today" opened on a side game about one time in three.
	/// </summary>
	internal static bool SideGameAllowed(int otherPlayed, int otherBudget) => otherPlayed < otherBudget;

	/// <summary>
	/// How long this sitting runs. The main game gets real gaming sessions - mostly a couple of hours, sometimes a
	/// quick one, sometimes an all-evening one. Side games get shorter dips that can never blow the day's side
	/// allowance. Both are then bounded by what is left of the target and by bedtime.
	/// </summary>
	private int SessionLength(uint game, uint main) {
		// Up for one more sitting after a 'wake': it runs to the time it said. What's left of the day's hours is nothing by
		// then, so it was cut to 5-15 minutes, and then another and another with breaks between until that time came.
		if (_stayUpUntil > DateTime.Now) {
			return Math.Max(5, MinutesUntilBed());
		}

		int min = Math.Max(5, Bot.Cfg.SessionMinMinutes);
		int max = Math.Max(min + 5, Bot.Cfg.SessionMaxMinutes);
		(int Lo, int Hi, int Pct)[] bands = LengthBands(game == main, min, max);
		int roll = Rng(0, 99);
		int band = 0;

		for (int below = bands[0].Pct; (roll >= below) && (band < bands.Length - 1); below += bands[band].Pct) {
			band++;
		}

		int length = Rng(bands[band].Lo, bands[band].Hi);

		if (game != main) {
			int left = SideBudgetNow(_otherBudget, _targetMinutes, _farmPlayed) - _otherPlayed;

			if ((left > 0) && (length > left)) {
				length = Math.Max(15, left);
			}
		}

		// The hunt game stops inside "Hunt at most, hours a day". It only left the games once that was used up, so a
		// sitting started with ten minutes to go ran its full length past it.
		if ((BotManager.ModuleOf<AchievementBoost>(Bot) is { } boost) && (game == boost.HuntTargetNow) && (boost.HuntMinutesLeftToday is int huntLeft)
			&& (length > huntLeft)) {
			length = Math.Max(15, huntLeft);
		}

		// The first sitting of the day is a short one - checking in, not settling down for four hours.
		//
		// Short RELATIVE to the configured range, never an absolute 12-28. That absolute ignored
		// SessionMinMinutes completely, so an account told "at least 30 minutes" opened its day with a
		// twelve-minute sitting - a minimum that was not a minimum. The floor wins; the cap only shortens
		// what is above it.
		//
		// And no absolute 28 left in it either. Under a minimum of 30 (the default) the cap came out at exactly 30, so
		// every single day opened with a sitting of exactly thirty minutes - the same number every morning is a pattern.
		if (_firstSessionOfDay) {
			length = Math.Min(length, Rng(min, FirstSittingCap(min, max)));
		}

		int remaining = _targetMinutes - _playedMinutesToday;

		if (length > remaining) {
			// Another hard-coded floor that could sit under a configured minimum. What is left of the target
			// is a real limit, so it still caps the sitting - but where there IS room for a full-length one,
			// the account's own minimum is what decides, not the number 20.
			length = Math.Max(Math.Min(min, remaining), remaining + Rng(-5, 15));
		}

		// The longest sitting is the longest sitting. The few minutes over what's left of the target (above), and the
		// quarter-hour floors for a side game's or the hunt's last minutes, could take the day's last sitting up to a
		// quarter of an hour past it.
		length = Math.Min(length, max);

		int untilBed = MinutesUntilBed();

		if ((untilBed > 0) && (length > untilBed)) {
			length = untilBed;
		}

		return Math.Max(5, length);
	}

	/// <summary>The longest the day's first sitting runs: a little way above the shortest sitting, always with room to vary.</summary>
	internal static int FirstSittingCap(int min, int max) => Math.Min(max, min + Math.Max(5, (max - min) * 15 / 100));

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
		Log.Info(new Said("last card from {0} - playing on ~{1}, then a break", GameName(_game), Fmt.Hm(minutes)), Bot.Name);
	}

	private void EndSession() {
		BankSession();
		_game = 0;
		_farmSession = false;

		// The day's hours played (a day off's 'wake' sitting included), or bedtime here: that sitting was the last one,
		// so it ends there rather than on a break nobody comes back from. The log said "short break - back in about
		// 12m" and twenty seconds later "done for the day", or "asleep".
		DateTime soon = DateTime.Now.AddMinutes(1);

		if (((_playedMinutesToday >= _targetMinutes) && (soon >= _stayUpUntil)) || !InWakingHours(soon)) {
			_phase = Phase.Off;
			Bot.StopPlaying();

			return;
		}

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
		(int breakLo, int breakHi) = ReactionSpeed.Range(Bot.Cfg, nameof(BotConfig.BreakMinMinutes), nameof(BotConfig.BreakMaxMinutes));
		int min = Math.Max(1, breakLo);
		int max = Math.Max(min + 2, breakHi);
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
		Persist();   // or a restart during it hands the day an extra meal - the count was only saved with the next minute played

		int centre = Math.Max(10, ReactionSpeed.One(Bot.Cfg, nameof(BotConfig.MealBreakMinutes)));
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
		(int awayLo, int awayHi) = ReactionSpeed.Range(Bot.Cfg, nameof(BotConfig.BreakAwayAfterMinMinutes), nameof(BotConfig.BreakAwayAfterMaxMinutes));
		int after = Rng(Math.Max(0, awayLo), Math.Max(awayLo, awayHi));

		if (minutes <= after + 2) {
			Log.Info(new Said("{0} - back in about {1}", what, Fmt.Hm(minutes)), Bot.Name);

			return;
		}

		_breakPersonaAt = DateTime.UtcNow.AddMinutes(after);

		// Counted when it actually goes offline (see StepAsync), not here: a break cut short first by bedtime, you or a
		// grind never went offline, yet used one up.
		if (Percent(pct) && (_signOutsUsed < _signOutCap)) {
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

	/// <summary>When the session was last banked - banking runs every 20-second tick while it plays.</summary>
	private DateTime _lastBankAt = DateTime.MinValue;

	/// <summary>
	/// The banking cursor, moved past a gap in the ticks. A session banks every 20 seconds, so minutes since the last
	/// bank (or since the cursor was set, when that is later) mean the process wasn't running: none of it is played.
	/// </summary>
	internal static DateTime SkipGap(DateTime bankedTo, DateTime lastBank, DateTime now) {
		DateTime since = lastBank > bankedTo ? lastBank : bankedTo;

		return now - since > TimeSpan.FromMinutes(3) ? now : bankedTo;
	}

	/// <summary>
	/// One bank at a time. A stop banks from whatever thread stopped the account while the loop's tick may be banking
	/// at that very moment: both read the same cursor before either moved it, and up to a minute went in twice.
	/// </summary>
	private readonly Lock _bankGate = new();

	/// <summary>A tick of the day and the commands that change the day ('wake', 'human reroll', 'habits forget') never run
	/// at the same time. Taken before <see cref="_bankGate"/>, never after it.</summary>
	private readonly Lock _stepGate = new();

	/// <summary>
	/// Whether the connection has shown it's alive since a gap in the ticks: Steam sent something since, or it signed in
	/// again. Failing both, a connection that is still up a few minutes on is taken as fine - by then a dead one has been
	/// noticed and dropped (the keepalive goes after a long quiet at once).
	/// </summary>
	internal static bool LiveAgain(DateTime since, DateTime? logonThen, DateTime? logonNow, DateTime lastInbound, DateTime now) =>
		((logonNow is { } on) && (on != logonThen)) || (lastInbound > since) || (now - since >= ProveWithin);

	/// <summary>Credit the running session's real elapsed time, once, and never a minute more than it played.</summary>
	private void BankSession() {
		// Read, count and move the cursor as one step, so the second of two banks at once finds the cursor already
		// moved and credits nothing.
		lock (_bankGate) {
			BankSessionLocked(false);
		}
	}

	/// <summary>The bank as the module stops - see <see cref="StopAsync"/>.</summary>
	private void BankSessionOnStop() {
		lock (_bankGate) {
			BankSessionLocked(true);
		}
	}

	/// <param name="stopping">The module is stopping - see <see cref="StopAsync"/>.</param>
	private void BankSessionLocked(bool stopping) {
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
		//
		// Stopping with no sign-in time: it was banked already. The Bot clears the sign-in only after the modules have
		// stopped, so a stop with it gone is the second of two - a disconnect stopped them and banked (sign-in still set),
		// then the stop or the restart that follows stops them again. Measured from the sign-in it recorded, that second
		// bank credited the time since the disconnect - up to three minutes signed out, counted as played.
		if (stopping && (Bot.OnlineSince == null)) {
			return;
		}

		DateTime logon = Bot.OnlineSince ?? DateTime.UtcNow;

		if (_bankedForLogon != logon) {
			_bankedForLogon = logon;
			_bankedTo = DateTime.UtcNow;   // start counting from now, not from before the gap
			_lastBankAt = DateTime.UtcNow;

			return;
		}

		// Nor is time the machine spent asleep. The sign-in survives a sleep until Steam notices the connection has
		// gone, so the first tick after waking banked the whole night as played - and put the account into "done
		// for today" having played none of it.
		_bankedTo = SkipGap(_bankedTo, _lastBankAt, DateTime.UtcNow);
		_lastBankAt = DateTime.UtcNow;

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

			// A farming sitting's minutes are its own - side-game time would have used up the side games' allowance.
			if (_farmSitting) {
				_farmPlayed += played;
			} else if (_game != MainGame()) {
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

			// The plan's own year: New Year's Eve's plan is still in force after midnight, and saved under the new year it
			// was never found again - a restart in the small hours of 1 January lost the night and rolled a fresh day.
			Year = _dayStamp > DateTime.Now.DayOfYear ? DateTime.Now.Year - 1 : DateTime.Now.Year,
			TargetMinutes = _targetMinutes,
			PlayedMinutes = _playedMinutesToday,
			MainSharePct = _mainSharePct,
			OtherBudget = _otherBudget,
			OtherPlayed = _otherPlayed,
			FarmPlayed = _farmPlayed,
			SignOutCap = _signOutCap,
			SignOutsUsed = _signOutsUsed,
			MealCap = _mealCap,
			MealsUsed = _mealsUsed,
			WakeMinuteOfDay = _wakeMinuteOfDay,
			BedHour = _bedHour,
			BedMinute = _bedMinute,
			BedIsTomorrow = _bedIsTomorrow,
			LastGame = _lastGame,
			StayUpUntil = _stayUpUntil > DateTime.Now ? _stayUpUntil : DateTime.MinValue,
			Quiet = _quietDay,
			LateNight = _lateNight,
			FriendJoins = _friendJoins,
			NewGameSittings = _newGameSittings,
			JoinedToday = _joinedToday,
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
		_farmPlayed = saved.FarmPlayed;
		_signOutCap = saved.SignOutCap;
		_signOutsUsed = saved.SignOutsUsed;
		_mealCap = saved.MealCap;
		_mealsUsed = saved.MealsUsed;
		_wakeMinuteOfDay = saved.WakeMinuteOfDay;
		_bedHour = saved.BedHour;
		_bedMinute = saved.BedMinute;
		_bedIsTomorrow = saved.BedIsTomorrow;
		_lastGame = saved.LastGame;
		_stayUpUntil = saved.StayUpUntil > DateTime.Now ? saved.StayUpUntil : DateTime.MinValue;
		_quietDay = saved.Quiet;
		_lateNight = saved.LateNight;
		_friendJoins = saved.FriendJoins;
		_newGameSittings = saved.NewGameSittings;
		_joinedToday = saved.JoinedToday ?? "";

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

	// ═══ a life, not just a day ══════════════════════════════════════════════
	// Four extras, each off until its setting is switched on: learning from you, new games tried out, quiet spells and late
	// nights, and now and then joining a friend. The sums are in HumanHabits; this is where they meet the account.

	private OwnerHabits Habits => _habits ??= OwnerHabits.Load(Bot.Name);

	/// <summary>"Learn from how I play" is on and has seen enough of you to go on.</summary>
	private bool LearningInUse => (Bot.Cfg.LearnFromOwner > 0) && Habits.Ready;

	private Dictionary<uint, int> LearnedMinutes() {
		if (_learnedVersion != Habits.Version) {
			_learnedMinutes = Habits.MinutesByGame();
			_learnedVersion = Habits.Version;
		}

		return _learnedMinutes;
	}

	/// <summary>
	/// Every tick: are you playing on the account yourself, and what. A finished sitting of yours is kept. Watching stops
	/// (and nothing is kept) while the setting is off.
	/// </summary>
	private void WatchOwner(DateTime now) {
		if (Bot.Cfg.LearnFromOwner <= 0) {
			_ownerWatch.Reset();

			return;
		}

		bool on = Bot.PlayingBlocked || (Bot.OtherSessionApp != 0);

		foreach (OwnerHabits.Sitting sitting in _ownerWatch.Observe(now, on, Bot.OtherSessionApp)) {
			bool wasReady = Habits.Ready;
			Habits.Add(sitting, now);
			Habits.Save(Bot.Name);

			Log.Debug(sitting.App != 0
				? new Said("noted you playing {0} for {1}", GameName(sitting.App), Fmt.Hm(sitting.Minutes))
				: new Said("noted you on the account for {0}", Fmt.Hm(sitting.Minutes)), Bot.Name);

			if (!wasReady && Habits.Ready) {
				Log.Info(new Said("seen you play on {0} days - its day now leans toward yours", Habits.DaysSeen), Bot.Name);
			}
		}
	}

	/// <summary>Throw away everything learned from you ('habits forget').</summary>
	public void ForgetHabits() {
		// One step at a time - see StepAsync.
		lock (_stepGate) {
			OwnerHabits.Forget(Bot.Name);
			_habits = new OwnerHabits();
			_ownerWatch.Reset();
			_learnedVersion = -1;
		}
	}

	/// <summary>A game's name from the library - a game that just arrived or a friend's pick is rarely in the built-in list.</summary>
	private string LibName(uint app) => Bot.Library.Find(app)?.Name is { Length: > 0 } name ? name : GameName(app);

	/// <summary>Games your other accounts are playing right now - never picked here, so two of yours aren't on one game by chance.</summary>
	private HashSet<uint> OwnAccountsRunning() {
		HashSet<uint> busy = [];

		foreach (Bot other in BotManager.Instance?.All ?? []) {
			if (other == Bot) {
				continue;
			}

			busy.UnionWith(other.PlayingApps);

			if (other.GrindGame != 0) {
				busy.Add(other.GrindGame);
			}
		}

		return busy;
	}

	/// <summary>Every one of your accounts, by SteamID - none of them is ever a "friend" to join.</summary>
	private HashSet<ulong> OwnSteamIds() {
		HashSet<ulong> ids = [.. (BotManager.Instance?.All ?? []).Select(static b => b.SteamId).Where(static id => id != 0)];

		if (Bot.SteamId != 0) {
			ids.Add(Bot.SteamId);
		}

		return ids;
	}

	/// <summary>
	/// Whether an extra (a friend's game, a new one) may be played here: in the library, not blacklisted, not held for its
	/// refund, and not a family game unless shared games are allowed and nobody in the family is on it.
	/// </summary>
	private bool MayPlayExtra(uint app, bool allowShared) =>
		(Bot.Library.Find(app) is { } entry) && (!entry.Shared || (allowShared && !Bot.Library.FamilyIsPlaying(app)))
		&& !Bot.Cfg.BlacklistedGames.Contains(app) && !Live.Global.GlobalBlacklistedGames.Contains(app) && !Bot.Refunds.Holds(app);

	/// <summary>
	/// A sitting for a friend's game or a new one, or 0 for the usual pick. A friend first - they're on it now and gone in an
	/// hour, where a new game is still new tomorrow.
	/// </summary>
	private uint PickExtra() {
		if (Bot.Cfg.JoinFriends && (_friendJoins < HumanHabits.FriendJoinsCap) && (FriendsGame() is { App: not 0 } friend)
			&& Chance(HumanHabits.FriendJoinChance)) {
			_friendJoins++;
			_joinedToday = $"{friend.Name} in {LibName(friend.App)}";
			Persist();
			Log.Info(new Said("{0} is playing {1} - joining in for a sitting", friend.Name, LibName(friend.App)), Bot.Name);

			return friend.App;
		}

		if (Bot.Cfg.NewGamesFirst && (_newGameSittings < HumanHabits.TrialSittingsCap) && (TrialsNow() is { Count: > 0 } trials)
			&& Chance(HumanHabits.TrialPickChance * trials.Max(static t => t.Strength))) {
			uint game = trials.Count == 1 ? trials[0].App
				: WeightedPick([.. trials.Select(static t => (t.App, Math.Max(1, (int) Math.Round(t.Strength * 100))))]);
			_newGameSittings++;
			Persist();
			Log.Info(new Said("trying out {0} - it's new here", LibName(game)), Bot.Name);

			return game;
		}

		return 0;
	}

	/// <summary>
	/// What your Steam friends are playing that this account could join, one at random. Steam pushes friends' games to the
	/// session as they change; this only reads that, it never asks.
	/// </summary>
	private (string Name, uint App) FriendsGame() {
		HashSet<uint> busy = OwnAccountsRunning();
		uint main = MainGame();

		// Not the main game: that already has its share, and "joining" it would only tip that share over its number.
		return HumanHabits.PickFriendGame(FriendsFeed?.Invoke() ?? FriendsPlaying(), OwnSteamIds(),
			app => (app != main) && !busy.Contains(app) && MayPlayExtra(app, allowShared: true), _rng);
	}

	/// <summary>Who's playing what, in place of Steam's friends list - only the tests set this.</summary>
	internal Func<List<(ulong Id, string Name, uint App)>>? FriendsFeed { get; set; }

	private List<(ulong Id, string Name, uint App)> FriendsPlaying() {
		List<(ulong Id, string Name, uint App)> playing = [];

		if (Bot.Friends is not { } friends) {
			return playing;
		}

		try {
			int count = friends.GetFriendCount();

			for (int i = 0; i < count; i++) {
				SteamKit2.SteamID id = friends.GetFriendByIndex(i);

				if (!id.IsIndividualAccount || (friends.GetFriendRelationship(id) != SteamKit2.EFriendRelationship.Friend)) {
					continue;
				}

				// A non-Steam shortcut or a mod isn't a game this account could own.
				if ((friends.GetFriendGamePlayed(id) is not { IsSteamApp: true } game) || (game.AppID == 0)) {
					continue;
				}

				string? name = friends.GetFriendPersonaName(id);
				playing.Add((id.ConvertToUInt64(), string.IsNullOrWhiteSpace(name) ? id.ConvertToUInt64().ToString(CultureInfo.InvariantCulture) : name, game.AppID));
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read what friends are playing: {0}", Log.Describe(e)), Bot.Name);

			return [];
		}

		return playing;
	}

	/// <summary>The new games that may be tried right now, with how keen on each it is today.</summary>
	private List<Trial> TrialsNow() {
		if (_trials.Count == 0) {
			return [];
		}

		DateTime now = DateTime.UtcNow;
		HashSet<uint> busy = OwnAccountsRunning();
		HashSet<uint> listed = [.. ParseWeights(Bot.Cfg.GameWeights).Select(static w => w.Game)];
		uint main = MainGame();
		List<Trial> ready = [];

		foreach (Trial trial in _trials) {
			double strength = HumanHabits.TrialStrength(trial.Start, now, trial.Days);

			// One already in "Games and how often" has its own share; one your other accounts are on waits for them.
			if ((strength > 0) && (trial.App != main) && !listed.Contains(trial.App) && !busy.Contains(trial.App) && MayPlayExtra(trial.App, allowShared: false)) {
				ready.Add(trial with { Strength = strength });
			}
		}

		return ready;
	}

	private bool IsTrialGame(uint app) => Bot.Cfg.NewGamesFirst && _trials.Exists(t => t.App == app);

	/// <summary>
	/// Which games arrived lately, from the licence list - when each one was really got, and whether it was bought. Its
	/// own licences only: a family member's game isn't new here. Every half hour, or at once when a licence arrives.
	/// </summary>
	private async Task RefreshTrialsAsync() {
		if (!Bot.Cfg.LegitMode || !Bot.Cfg.NewGamesFirst) {
			_trials = [];

			return;
		}

		// Not while you're on the account: nothing goes to Steam from here underneath you, not even a question.
		if (!Bot.IsOnline || Bot.PlayingBlocked || !Bot.Library.Ready || ((DateTime.UtcNow < _trialsDue) && (Bot.LicenseGeneration == _trialsGeneration))) {
			return;
		}

		_trialsDue = DateTime.UtcNow.AddMinutes(30);
		_trialsGeneration = Bot.LicenseGeneration;

		try {
			IReadOnlyDictionary<uint, AppOwnership> owned = await Bot.GetAppOwnershipAsync().ConfigureAwait(false);
			DateTime now = DateTime.UtcNow;
			List<Trial> fresh = [];

			foreach (Library.Entry game in Bot.Library.Games) {
				if (game.Shared || !owned.TryGetValue(game.AppId, out AppOwnership own) || !own.Own) {
					continue;
				}

				DateTime start = HumanHabits.TrialStart(own, Bot.Cfg);
				int days = HumanHabits.TrialDays(Bot.Name, game.AppId);
				double strength = HumanHabits.TrialStrength(start, now, days);

				if (strength > 0) {
					fresh.Add(new Trial(game.AppId, strength, start, days));
				}
			}

			_trials = [.. fresh.OrderByDescending(static t => t.Strength).Take(HumanHabits.TrialsAtOnce)];
		} catch (Exception e) {
			Log.Debug(new Said("couldn't look for new games: {0}", Log.Describe(e)), Bot.Name);
		}
	}

	/// <summary>What the four extras are up to, for 'human' - only the ones switched on, so nothing changes for anybody else.</summary>
	public List<string> ExtrasReport() {
		List<string> lines = [];

		if (Bot.Cfg.LearnFromOwner > 0) {
			OwnerHabits h = Habits;
			string how = Bot.Cfg.LearnFromOwner >= 2 ? "a lot" : "a little";
			string hours = OwnerHabits.HoursText(h.HourShare());

			lines.Add(h.Ready
				? $"learning from you   in use ({how}) - {h.DaysSeen} days seen" + (hours.Length > 0 ? $", usually on {hours}" : "")
				: $"learning from you   watching - {h.DaysSeen} of {OwnerHabits.DaysNeeded} days seen, not used yet");
		}

		if (Bot.Cfg.NewGamesFirst) {
			List<Trial> trials = TrialsNow();
			DateTime now = DateTime.UtcNow;

			lines.Add(trials.Count == 0
				? "new games           nothing new to try right now"
				: "new games           trying " + string.Join(", ", trials.Select(t => $"{LibName(t.App)} (day {(int) (now - t.Start).TotalDays + 1} of {t.Days})"))
					+ $" - {_newGameSittings} of {HumanHabits.TrialSittingsCap} sittings today");
		}

		if (Bot.Cfg.LongerRhythms) {
			lines.Add(_quietDay ? "rhythm              a quiet spell - a shorter day today"
				: _lateNight ? $"rhythm              a late night - bed around {PlanBed():HH:mm}"
				: "rhythm              an ordinary day");
		}

		if (Bot.Cfg.JoinFriends) {
			lines.Add(_joinedToday.Length > 0
				? $"friends             joined {_joinedToday} ({_friendJoins} of {HumanHabits.FriendJoinsCap} today)"
				: "friends             nobody joined today");
		}

		return lines;
	}

	/// <summary>What 'habits' prints: what it has learned from you, and whether it's being used.</summary>
	public List<string> HabitsReport() {
		OwnerHabits h = Habits;
		List<string> lines = [];
		int days = h.DaysSeen;

		lines.Add(Bot.Cfg.LearnFromOwner <= 0
			? "  \"Learn from how I play\" is off - it isn't watching. Switch it on under Human mode."
			: h.Ready
				? $"  in use ({(Bot.Cfg.LearnFromOwner >= 2 ? "a lot" : "a little")}) - its day leans toward yours"
				: $"  watching - {days} of {OwnerHabits.DaysNeeded} days seen, used once it has {OwnerHabits.DaysNeeded}");

		if (days == 0) {
			lines.Add("  nothing seen yet - it learns from the times you play on this account yourself");

			return lines;
		}

		List<(int Start, int End)> seen = h.Days();
		string hours = OwnerHabits.HoursText(h.HourShare());
		lines.Add($"  days seen     {days} (the last {OwnerHabits.KeepDays} days are kept)");
		lines.Add($"  usually       on around {OwnerHabits.Clock(OwnerHabits.Median(seen.Select(static d => d.Start)))}, off around {OwnerHabits.Clock(OwnerHabits.Median(seen.Select(static d => d.End)))}"
			+ (hours.Length > 0 ? $"; mostly {hours}" : ""));

		Dictionary<uint, int> games = h.MinutesByGame();

		if (games.Count > 0) {
			int total = Math.Max(1, games.Values.Sum());
			HashSet<uint> listed = [.. ParseWeights(Bot.Cfg.GameWeights).Select(static w => w.Game)];

			lines.Add("  top games     " + string.Join(", ", games.OrderByDescending(static g => g.Value).Take(5)
				.Select(g => $"{LibName(g.Key)} {g.Value * 100 / total}% ({Fmt.Hm(g.Value)})")));

			List<uint> notListed = [.. games.OrderByDescending(static g => g.Value).Select(static g => g.Key).Where(g => !listed.Contains(g)).Take(5)];

			if (notListed.Count > 0) {
				lines.Add("  not in \"Games and how often\" (add them there if you want them in its day): " + string.Join(", ", notListed.Select(LibName)));
			}
		}

		return lines;
	}

	// ═══ which game ═════════════════════════════════════════════════════════
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
				bool first;

				// Locked: this runs from the status line and the board (dashboard, tray, heartbeat threads) as well as the loop.
				lock (_targetsReached) {
					first = _targetsReached.Add(target.AppId);
				}

				if (first) {
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
		List<uint> games = Bot.Cfg.OfflineIdleGames.Count > 0 ? [.. Bot.Cfg.OfflineIdleGames] : TopForNight();

		foreach (HourTargets.Progress p in OpenTargets()) {
			if ((p.Target.By != null) && (p.DailyMinutesNeeded > Math.Max(30, _targetMinutes * 0.6)) && !games.Contains(p.Target.AppId)) {
				games.Add(p.Target.AppId);
			}
		}

		// The games typed into the list (and the hour targets) go by the same rules as the ones it picks itself: a game
		// held for its refund idled all night on the list, where the most-played pick already left it out.
		HashSet<uint> banned = BannedNow();
		games = [.. games.Where(a => MayBankAtNight(a, banned))];

		return games.Count > Core.SteamIds.GamesAtOnce ? games[..Core.SteamIds.GamesAtOnce] : games;
	}

	/// <summary>
	/// With nothing on the overnight list, the account's own most-played games - "...or, with none chosen, its top games"
	/// of them. Only games it may play anyway (<see cref="MayBankAtNight"/>).
	/// </summary>
	private List<uint> TopForNight() {
		HashSet<uint> banned = BannedNow();

		return TopPlayed(Bot.Library.Games, Bot.Cfg.OfflineIdleTopGames, a => MayBankAtNight(a, banned));
	}

	private HashSet<uint> BannedNow() => [.. BotManager.ModuleOf<BanWatch>(Bot)?.BannedGames ?? []];

	/// <summary>
	/// How many games on the overnight list it may really bank - what selfcheck's advice is about. The raw list counted
	/// games the rules leave out (blacklisted, held for a refund), and said "it banks 3 at once" of a night that ran one.
	/// </summary>
	public int ListedNightCount {
		get {
			HashSet<uint> banned = BannedNow();

			return Math.Min(Core.SteamIds.GamesAtOnce, Bot.Cfg.OfflineIdleGames.Count(a => MayBankAtNight(a, banned)));
		}
	}

	/// <summary>
	/// May this game bank hours overnight: not blacklisted, not held for a refund, not one Steam has it banned in, and not
	/// a family game unless shared games are allowed and nobody in the family is on it. A game typed into the list that
	/// the library doesn't show (a free game never played, say) still goes on - only a family game is known to be one.
	/// </summary>
	private bool MayBankAtNight(uint app, HashSet<uint> banned) =>
		!banned.Contains(app) && !Bot.Cfg.BlacklistedGames.Contains(app) && !Live.Global.GlobalBlacklistedGames.Contains(app) && !Bot.Refunds.Holds(app)
		&& ((Bot.Library.Find(app) is not { Shared: true }) || (Bot.Cfg.IncludeFamilyLibrary && !Bot.Library.FamilyIsPlaying(app)));

	/// <summary>The most-played games that are allowed, most minutes first - only ones it has actually played.</summary>
	public static List<uint> TopPlayed(IEnumerable<Library.Entry> games, int count, Func<uint, bool> allowed) =>
		[.. games.Where(static g => g.MinutesPlayed > 0).Where(g => allowed(g.AppId))
			.OrderByDescending(static g => g.MinutesPlayed).ThenBy(static g => g.AppId)
			.Take(Math.Clamp(count, 1, 10)).Select(static g => g.AppId)];

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

	/// <summary>What it really plays from: the games list as filtered here, with the hunt's game on the end when there is one.</summary>
	public List<(uint Game, int Weight)> Rotation => Weights();

	/// <summary>
	/// Each game's share of a mixed day, the way <see cref="PickGame"/> splits it: the main game's own number IS its
	/// share (5-95), and everything after it - the hunt's game too - splits the rest by weight. 'human' used to show
	/// each weight over the total, so "730:70, 440:10" read 88/12 while the day really went 70/30.
	/// </summary>
	public static List<double> Shares(List<(uint Game, int Weight)> weights) {
		if (weights.Count <= 1) {
			return [.. weights.Select(static _ => 100.0)];
		}

		double main = Math.Clamp(weights[0].Weight, 5, 95);
		double sides = weights.Skip(1).Sum(static w => Math.Max(1, w.Weight));

		return [main, .. weights.Skip(1).Select(w => (100 - main) * Math.Max(1, w.Weight) / sides)];
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

		// The main game dropped out (refundable, blacklisted): the first side game is read as the main one now, and
		// its small number would be taken as its share - "730:70, 440:15, 550:15" with 730 held played 550 85% of
		// the day. What's left splits by weight instead, as the side games already did between themselves.
		if ((list != weights) && (weights.Count > 0) && (list[0].Game != weights[0].Game)) {
			int left = Math.Max(1, list.Sum(static w => Math.Max(1, w.Weight)));
			list[0] = (list[0].Game, Math.Max(1, (int) Math.Round(100.0 * Math.Max(1, list[0].Weight) / left)));
		}

		// Learning from you: the same games, leaned toward how you split your own time between them.
		if (LearningInUse) {
			list = HumanHabits.Reweight(list, LearnedMinutes(), HumanHabits.Pull(Bot.Cfg.LearnFromOwner));
		}

		// The game the achievement hunter is on joins the rotation as one more side game, at its own weight - played
		// in ordinary sittings like any other, instead of the hunter taking the account over and cutting a sitting
		// short. Last in the list, so it is never mistaken for the main game.
		uint hunt = BotManager.ModuleOf<AchievementBoost>(Bot)?.HuntTargetNow ?? 0;   // not once today's hunting is used up

		if ((hunt != 0) && (list.Count > 0) && !list.Exists(w => w.Game == hunt)) {
			list = [.. list, (hunt, Math.Max(1, Bot.Cfg.BoostWeight))];
		}

		return list;
	}

	/// <summary>How often a side-game sitting straight after another carries on with that same side game.</summary>
	internal const double CarryOnChance = 0.40;

	/// <summary>
	/// The game for an ordinary sitting: an hour target if one wants it, otherwise a weighted pick where the MAIN game's
	/// share is pinned to today's roll instead of being left to its raw weight. Fixed weights meant every side game you
	/// added quietly diluted the main one; sizing the main weight against the side total holds it at its share however
	/// many side games are configured.
	/// </summary>
	private uint PickGame() {
		// No lean toward games that still have card drops any more. That was card farming's humanised form from before
		// "Farm cards" had its own when (FarmCardsWhen): two sittings in three went to a game with drops whenever one of
		// these had any, so on top of the farming sittings the usual games' shares were fiction while cards lasted -
		// and "only at night" farmed in the day after all. Cards are farmed when that setting says; this is the rest.

		// An hour target, if one wants this sitting.
		uint target = PreferTarget();

		if (target != 0) {
			return target;
		}

		// A friend's game, or a new one being tried out - each only when its setting is on, and only then touching the dice,
		// so with them off the pick below is exactly what it was.
		uint extra = PickExtra();

		if (extra != 0) {
			return extra;
		}

		List<(uint Game, int Weight)> weights = Weights();

		// A real player does not change game every time they sit down; often they carry straight on. That used to be a
		// flat 40% ahead of the pick, on whatever was played last, and it tilted the day toward the main game: the day's
		// first sitting is a short one, so the main game is picked for more of those, and 40% of them carried that into a
		// full sitting the pick would have given a side game more often - a main game set to 70% came out at 71.5%. It
		// also strung main-game sittings together four and five at a time, so one mixed day in five went by without a
		// single side game.
		//
		// Now the pick says main game or side games at the odds that give each its share of the time, whatever was
		// played last - the main game follows itself exactly as often as those odds say, never more. Carrying on is
		// WHICH side game: straight after one, it's the same one again 40% of the time rather than a fresh pick between
		// them. That can't move the main game's share (the odds below count the carried-on game's own sittings), and it
		// leaves the side games' split as their weights say, since the game carried on from was picked by weight too.
		uint carryOn = (_lastGame != 0) && weights.Skip(1).Any(w => w.Game == _lastGame) ? _lastGame : 0;
		List<(uint Game, double Odds)> odds = PickOdds(weights, carryOn);

		if (odds.Count == 0) {
			return 0;
		}

		uint game = Draw(odds);

		return (game != odds[0].Game) && (carryOn != 0) && Chance(CarryOnChance) ? carryOn : game;
	}

	/// <summary>
	/// Each game's odds for this sitting, adding up to 1: the main game weighted so it gets its share of the TIME, the
	/// side games splitting the rest by weight - or the main game alone once today's side allowance is spent.
	/// </summary>
	/// <param name="carryOn">The side game a side-game sitting may carry on with (0: none), whose length counts for more.</param>
	private List<(uint Game, double Odds)> PickOdds(List<(uint Game, int Weight)> weights, uint carryOn) {
		if (weights.Count == 0) {
			return [];
		}

		// Once today's side-game allowance is spent it is the main game for the rest of the day.
		if ((weights.Count == 1) || !SideGameAllowed(_otherPlayed, SideBudgetNow(_otherBudget, _targetMinutes, _farmPlayed))) {
			return [(weights[0].Game, 1.0)];
		}

		int sideTotal = weights.Skip(1).Sum(static w => Math.Max(1, w.Weight));
		double mainMean = 1, sideMean = 1;

		// Up for one more sitting after a 'wake': it runs to the stay-up time whatever it's on, so the sittings are alike.
		if (_stayUpUntil <= DateTime.Now) {
			// How long a sitting on each would really run THIS time - the day's first is a short one whatever it's on, the
			// last is cut to what's left of the hours, a side game's to what's left of its allowance. Weighed on the plain
			// ranges alone, with the first and last sittings patched on as a rough cap, the main game came out a point or
			// so off its share.
			int min = Math.Max(5, Bot.Cfg.SessionMinMinutes);
			int max = Math.Max(min + 5, Bot.Cfg.SessionMaxMinutes);
			int sideLeft = SideBudgetNow(_otherBudget, _targetMinutes, _farmPlayed) - _otherPlayed;
			int first = _firstSessionOfDay ? FirstSittingCap(min, max) : 0;
			int remaining = _targetMinutes - _playedMinutesToday;
			int untilBed = MinutesUntilBed();
			AchievementBoost? boost = BotManager.ModuleOf<AchievementBoost>(Bot);

			// A side game that is also an hour target runs the main game's long sittings (see StartSession).
			double Mean(uint game, bool main) => MeanLength(main || IsTargetGame(game), min, max, sideLeft,
				(boost != null) && (game == boost.HuntTargetNow) && (boost.HuntMinutesLeftToday is int huntLeft) ? huntLeft : -1, first, remaining, untilBed);

			mainMean = Mean(weights[0].Game, true);
			sideMean = weights.Skip(1).Sum(w => Math.Max(1, w.Weight) * Mean(w.Game, false)) / sideTotal;

			if (carryOn != 0) {
				sideMean = (CarryOnChance * Mean(carryOn, false)) + ((1 - CarryOnChance) * sideMean);
			}
		}

		double mainWeight = MainWeight(sideTotal, _mainSharePct, mainMean, sideMean);
		double all = mainWeight + sideTotal;

		return [(weights[0].Game, mainWeight / all), .. weights.Skip(1).Select(w => (w.Game, Math.Max(1, w.Weight) / all))];
	}

	/// <summary>Rolls one game from odds that add up to 1 (or near enough - the last one takes any rounding).</summary>
	private uint Draw(List<(uint Game, double Odds)> odds) {
		double roll = _rng.NextDouble();
		double running = 0;

		foreach ((uint game, double p) in odds) {
			running += p;

			if (roll < running) {
				return game;
			}
		}

		return odds[^1].Game;
	}

	/// <summary>
	/// The main game's weight in the pick, so it gets its share of the day's TIME, not just of its sittings. Its sittings
	/// run far longer than a side game's short dips (about 80 minutes against 47 with the default 30-150), so picked
	/// for 70% of the sittings it got about 80% of the day - not the number written beside it.
	/// </summary>
	/// <param name="mainMean">How long a main-game sitting would run this time, on average (<see cref="MeanLength"/>).</param>
	/// <param name="sideMean">The same for the side games, weighted between them.</param>
	internal static double MainWeight(int sideTotal, int mainPct, double mainMean, double sideMean) =>
		Math.Max(0.01, sideTotal * mainPct / (double) Math.Max(1, 100 - mainPct) * Math.Max(1, sideMean) / Math.Max(1, mainMean));

	/// <summary>
	/// The ranges a sitting's length is rolled from, with how often each: the main game's real gaming sessions - mostly
	/// a couple of hours, sometimes a quick one, sometimes an all-evening one - or a side game's shorter dip. One list for
	/// both the roll and the pick's sums, so the two can't drift apart.
	/// </summary>
	internal static (int Lo, int Hi, int Pct)[] LengthBands(bool main, int min, int max) {
		int span = max - min;

		return main
			? [(min, min + (span * 21 / 100), 30), (min + (span * 28 / 100), min + (span * 64 / 100), 50), (min + (span * 71 / 100), max, 20)]
			: [(min, min + (span * 29 / 100), 100)];
	}

	/// <summary>
	/// The average length <see cref="SessionLength"/> comes out at, worked out exactly from the same ranges and the same
	/// limits in the same order: what's left of a side game's allowance and of the hunt, the day's short first sitting,
	/// what's left of the day's hours, the longest sitting and bedtime.
	/// </summary>
	/// <param name="sideLeft">What's left of the side allowance - a side game's sitting stops there. 0 or less: no limit.</param>
	/// <param name="huntLeft">What's left of today's hunting, for the hunt's game. -1: no limit.</param>
	/// <param name="first">The first sitting's cap (<see cref="FirstSittingCap"/>), or 0 when it isn't the day's first.</param>
	internal static double MeanLength(bool main, int min, int max, int sideLeft, int huntLeft, int first, int remaining, int untilBed) {
		double[] odds = new double[max + 16];   // nothing runs past the longest sitting, bar a few minutes on the way

		foreach ((int lo, int hi, int pct) in LengthBands(main, min, max)) {
			int top = Math.Max(lo, hi);

			for (int x = lo; x <= top; x++) {
				odds[x] += pct / 100.0 / (top - lo + 1);
			}
		}

		if (!main && (sideLeft > 0)) {
			odds = Remap(odds, x => x > sideLeft ? Math.Max(15, sideLeft) : x);
		}

		if (huntLeft >= 0) {
			odds = Remap(odds, x => x > huntLeft ? Math.Max(15, huntLeft) : x);
		}

		if (first > 0) {
			odds = RemapRolled(odds, min, first, static (x, cap) => Math.Min(x, cap));
		}

		if (remaining < odds.Length) {
			odds = RemapRolled(odds, -5, 15, (x, over) => x > remaining ? Math.Max(Math.Min(min, remaining), remaining + over) : x);
		}

		odds = Remap(odds, x => Math.Max(5, Math.Min(Math.Min(x, max), untilBed > 0 ? untilBed : int.MaxValue)));

		double mean = 0;

		for (int x = 0; x < odds.Length; x++) {
			mean += x * odds[x];
		}

		return mean;
	}

	/// <summary>Every length moved to what <paramref name="to"/> makes of it.</summary>
	private static double[] Remap(double[] odds, Func<int, int> to) => RemapRolled(odds, 0, 0, (x, _) => to(x));

	/// <summary>Every length moved to what <paramref name="to"/> makes of it with a number rolled evenly from lo to hi, the way Rng rolls it.</summary>
	private static double[] RemapRolled(double[] odds, int lo, int hi, Func<int, int, int> to) {
		double[] next = new double[odds.Length];
		int top = Math.Max(lo, hi);
		double each = 1.0 / (top - lo + 1);

		for (int x = 0; x < odds.Length; x++) {
			if (odds[x] <= 0) {
				continue;
			}

			for (int r = lo; r <= top; r++) {
				next[Math.Clamp(to(x, r), 0, odds.Length - 1)] += odds[x] * each;
			}
		}

		return next;
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

	/// <summary>
	/// Up and about - not asleep, not idling the night away - whatever "Only react while awake" says. AwakeFor answers
	/// "may it react", which is always yes with that switch off; "Update by itself" needs to know if it's up.
	/// </summary>
	public static bool UpAndAbout(Bot bot) {
		HumanMode? human = bot.Modules.OfType<HumanMode>().FirstOrDefault();

		return (human == null) || (human._ticked && (human.Current is not (Phase.Asleep or Phase.NightIdle)) && !human.NightGrind);
	}

	/// <summary>
	/// Human mode has the account resting - asleep with nothing running, done for today, or taking the day off - and
	/// which. Null while it's doing something, or on an account that isn't in human mode.
	/// </summary>
	public static Phase? RestingPhase(Bot bot) {
		if (!bot.Cfg.LegitMode || bot.IsFarming || bot.Grinding || (bot.Modules.OfType<HumanMode>().FirstOrDefault() is not { } human)) {
			return null;
		}

		return human.Current is Phase.Asleep or Phase.DoneForToday or Phase.DayOff ? human.Current : null;
	}

	/// <summary>
	/// Resting by its own plan, so no hours are due: asleep with nothing banking overnight, done for today, a day off,
	/// or standing down for you. False before human mode has looked at the clock this run - it has no plan to go by.
	/// For the stuck-account alarm (<see cref="StuckWatch"/>), which must never call a planned rest "stuck".
	/// </summary>
	public static bool RestingByPlan(Bot bot) {
		if (!bot.Cfg.LegitMode || bot.IsFarming || bot.Grinding || (bot.Modules.OfType<HumanMode>().FirstOrDefault() is not { _ticked: true } human)) {
			return false;
		}

		return human._phase switch {
			Phase.Asleep => !human._nightFarming,
			Phase.DoneForToday or Phase.DayOff or Phase.StoodDown => true,
			_ => false
		};
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

		// Finishing up before it logs off isn't the moment to answer a trade or a gift - a few seconds later it's gone.
		return !bot.Stopping && AwakeFor(bot) && ((human == null) || (human.WarmedUp && !human._offlineBreak));
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

		return !bot.Stopping && ((human == null) || (human._ticked && (human.Current is not (Phase.Asleep or Phase.NightIdle)) && !human.NightGrind
			&& human.WarmedUp && !human._offlineBreak));
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
	/// <param name="rotation">What the day really picks from - the hunt's game included (<see cref="Rotation"/>). Without
	/// it the preview left the hunt out, so a main game plus a hunt read as main-game-only days.</param>
	/// <param name="account">The account's name: its quiet spells and late nights are decided per account and day, so with
	/// it the preview shows the very ones the real days will have.</param>
	/// <param name="habits">What it has learned from you, when "Learn from how I play" is using it.</param>
	public static List<string> PreviewWeek(BotConfig cfg, List<(uint Game, int Weight)>? rotation = null, string? account = null, OwnerHabits? habits = null) {
		List<string> lines = [];
		Random rng = new();
		List<(uint Game, int Weight)> weights = rotation is { Count: > 0 } ? rotation : ParseWeights(cfg.GameWeights);

		if (weights.Count == 0) {
			return ["no games set"];
		}

		int mainCentre = Math.Clamp(weights[0].Weight, 5, 95);
		DateTime lastBed = DateTime.MinValue;

		for (int day = 0; day < 7; day++) {
			// Rolled by the very roller the day uses, each night's bedtime feeding the next morning - so the preview is
			// the real thing's shape, not a copy of it that had already drifted twice (no target cap, no hunt).
			DateTime date = DateTime.Now.Date.AddDays(day);
			bool rhythms = cfg.LongerRhythms && (account != null);
			OwnerHabits? learned = (cfg.LearnFromOwner > 0) && (habits?.Ready == true) ? habits : null;
			DayExtras? extras = !rhythms && (learned == null) ? null
				: new DayExtras(rhythms ? HumanHabits.RhythmFor(account!, date) : default, learned, HumanHabits.Pull(cfg.LearnFromOwner));
			DayRoll roll = RollDayWith(cfg, date, mainCentre, weights.Count >= 2, lastBed, rng, extras);
			lastBed = BedOn(date, roll.BedHour, roll.BedMinute, roll.BedIsTomorrow);

			string when = $"{date:ddd}";
			string note = roll.Quiet ? "   (a quiet spell)" : roll.LateNight ? "   (a late night)" : "";

			if (roll.Target == 0) {
				lines.Add($"{when}  not playing{note}");

				continue;
			}

			// What the others can expect - their share of the day - rather than the cap on them, which is three times that.
			string mix = roll.OtherBudget == 0
				? GameName(weights[0].Game) + " only"
				: $"mostly {GameName(weights[0].Game)}, about {Fmt.Hm(SideExpected(roll.Target, roll.MainSharePct))} on the others";

			lines.Add($"{when}  {Fmt.Hm(roll.Target),-7} from {roll.WakeMinute / 60:00}:{roll.WakeMinute % 60:00} to {roll.BedHour:00}:xx   {mix}{note}");
		}

		return lines;
	}
}
