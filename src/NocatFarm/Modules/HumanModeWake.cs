using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// The next day's wake time, picked when the day ends, and no break that runs into bed.
///
/// The next day used to be rolled only at its wake, so all night the account could only say the window it would get up
/// in - "asleep · up 13:15-14:45". Now the wake time is picked as the day ends (bedtime, done for the day, a day off),
/// by the same rules the day's roll uses, kept in the saved day, and used when that day starts: "asleep · up 13:52", and
/// it gets up at 13:52. The rest of the day (its hours, bedtime) is still rolled at wake.
///
/// A break that wouldn't be over well before bedtime isn't taken: it goes to bed instead. At 01:47 it said "short break
/// · back in ~21m", went to bed at 01:57 inside the break, and was never back.
/// </summary>
public sealed partial class HumanMode {
	/// <summary>
	/// The next day's wake time (local), picked as the day ended - <see cref="DateTime.MinValue"/> when there isn't one yet
	/// (the first start, or the plan just changed). Kept in the saved day, so a restart keeps it.
	/// </summary>
	private DateTime _nextWake = DateTime.MinValue;

	/// <summary>A break has to be over at least this long before bedtime; nearer than that, it goes to bed instead.</summary>
	public const int BedNearMinutes = 12;

	/// <summary>
	/// Whether what it says about getting up is about the next day: the day is played (done for today), it's a day off, or
	/// its bedtime has been. Asleep before today's wake, it's today's time.
	/// </summary>
	private bool NextDayAhead(DateTime now) =>
		(_dayStamp >= 0) && ((_phase is Phase.DoneForToday or Phase.DayOff) || (_targetMinutes == 0) || (now >= PlanEnds()));

	/// <summary>
	/// The day after <paramref name="planDate"/> - the next one it plays - or, when that one's window is long gone too
	/// (signed out for days), the next one from now.
	/// </summary>
	public static DateTime NextDayFor(DateTime planDate, DateTime now, BotConfig cfg) {
		int hi = WakeWindow(cfg).Hi;
		DateTime day = planDate.Date.AddDays(1);

		if (day.AddMinutes(hi) <= now) {
			day = now.Date.AddMinutes(hi) > now ? now.Date : now.Date.AddDays(1);
		}

		return day;
	}

	/// <summary>
	/// Whether a picked wake time is the one for <paramref name="day"/>: on that day, and inside "Gets on at" give or take
	/// "Varies by up to" as set now - changed since, it's picked again.
	/// </summary>
	public static bool PickHolds(DateTime pick, DateTime day, BotConfig cfg) {
		if ((pick == DateTime.MinValue) || (pick.Date != day.Date)) {
			return false;
		}

		(int lo, int hi) = WakeWindow(cfg);
		int minute = (int) (pick - day.Date).TotalMinutes;

		return (minute >= lo) && (minute <= hi);
	}

	/// <summary>The next day's picked wake time, while what it says about getting up is about the next day - else null.</summary>
	private DateTime? PickedWake(DateTime now) =>
		NextDayAhead(now) && PickHolds(_nextWake, NextDayFor(PlanDate(_dayStamp, now), now, Bot.Cfg), Bot.Cfg) ? _nextWake : null;

	/// <summary>
	/// Pick the next day's wake time, if the day is over and it isn't picked yet: the day's own roll for that date
	/// (<see cref="RollDayWith"/> - the bell in the window, weekends earlier, learning from you, a night's sleep after this
	/// bedtime), of which only the wake time is kept. Saved at once.
	/// </summary>
	private void PickNextWake() {
		DateTime now = DateTime.Now;

		if (!NextDayAhead(now) || (PickedWake(now) != null)) {
			return;
		}

		DateTime day = NextDayFor(PlanDate(_dayStamp, now), now, Bot.Cfg);

		// The wake time doesn't depend on the games, so the main game's share and the side games are left at their defaults.
		_nextWake = WakeOn(day, RollDayWith(Bot.Cfg, day, 70, true, PlanEnds(), _rng, Extras(day)).WakeMinute);
		Persist();
	}

	/// <summary>The picked wake time in minutes of <paramref name="date"/>, when it was picked for that day - else null.</summary>
	private int? PickedWakeFor(DateTime date) => PickHolds(_nextWake, date, Bot.Cfg) ? (int) (_nextWake - date.Date).TotalMinutes : null;

	/// <summary>When it next gets up, in words: the picked time ("13:52"), else that day's window ("13:15-14:45").</summary>
	public static string NextUpText(DateTime? picked, DateTime planDate, DateTime now, BotConfig cfg) {
		if (picked is { } at) {
			return at.ToString("HH:mm", CultureInfo.InvariantCulture);
		}

		DateTime day = NextDayFor(planDate, now, cfg);
		(int lo, int hi) = WakeWindow(cfg);

		return WindowWords(day.AddMinutes(lo), day.AddMinutes(hi));
	}

	/// <summary>
	/// Whether a break may start: it has to be over at least <see cref="BedNearMinutes"/> before bedtime. Otherwise it goes
	/// to bed instead - and in the last minutes before bedtime that's always.
	/// </summary>
	public static bool BreakFitsBeforeBed(int breakMinutes, double minutesToBed) => minutesToBed - breakMinutes >= BedNearMinutes;

	/// <summary>Whether a break ends after bedtime (both UTC) - then it isn't coming back, it's off for the night.</summary>
	public static bool BreakRunsIntoBed(DateTime breakEndsUtc, DateTime bedUtc) => breakEndsUtc > bedUtc;

	/// <summary>On a break that bedtime comes in the middle of (a plan rolled again during it, say): "then off for the night".</summary>
	public bool BreakIntoBed => (_dayStamp >= 0) && (_phase is Phase.ShortBreak or Phase.MealBreak) && BreakRunsIntoBed(_phaseEnds, PlanEnds().ToUniversalTime());

	/// <summary>
	/// Go to bed now, a little before the planned bedtime, rather than start a break that would run into it. Bedtime moves
	/// to now in the saved day, so the night and the next day's wake count from when it really went.
	/// </summary>
	private void BedNow() {
		DateTime now = DateTime.Now;

		if (_stayUpUntil > now) {
			_stayUpUntil = now;
		}

		if (PlanBed() > now) {
			int minute = (int) (now - PlanDate(_dayStamp, now)).TotalMinutes;
			_bedIsTomorrow = minute >= 24 * 60;
			_bedHour = minute / 60 % 24;
			_bedMinute = minute % 60;
		}

		_nextWake = DateTime.MinValue;   // picked against the old bedtime
		Persist();
		GoToBed();
	}
}
