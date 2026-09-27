using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// When a human-mode account may do one kind of thing. Without human mode it's always open.
/// </summary>
/// <remarks>
/// Two kinds. A gate that follows the day (the default) is for things other people see as they happen - a trade
/// accepted, a comment posted, an achievement unlocked: it waits until the account is awake and settled in (see
/// <see cref="HumanMode.ReadyFor"/>), then a random human delay of its own. A <see cref="Quiet"/> gate is for things
/// nobody watches - a badge crafted, a free game added, a notification read: it ignores sleep entirely and only
/// waits a short random while after signing in, so a sign-in isn't ten things hitting Steam in the same second.
/// </remarks>
public sealed class HumanGate(Bot bot, bool followsDay = true) {
	private bool _ready;
	private DateTime _openAt = DateTime.MaxValue;

	/// <summary>For things nobody watches: any time of day, just not the moment it signs in.</summary>
	public static HumanGate Quiet(Bot bot) => new(bot, followsDay: false);

	/// <summary>Whether it may act now. Each time the account becomes ready again (signed in, woke up), a fresh wait starts.</summary>
	public bool Open {
		get {
			if (!bot.Cfg.LegitMode) {
				return true;
			}

			// Behind-the-scenes things follow the day too when the account is set up that way.
			bool day = followsDay || bot.Cfg.QuietThingsWaitForDay;

			if (day ? !HumanMode.ReadyFor(bot) : !bot.IsOnline) {
				_ready = false;

				return false;
			}

			if (!_ready) {
				_ready = true;
				(int lo, int hi) = followsDay
					? (bot.Cfg.WakeDelayMinMinutes, bot.Cfg.WakeDelayMaxMinutes)
					: (bot.Cfg.QuietDelayMinMinutes, bot.Cfg.QuietDelayMaxMinutes);

				if ((lo <= 0) && (hi <= 0)) {
					(lo, hi) = (0, 0);
				} else if (hi < lo) {
					hi = lo;
				}

				_openAt = DateTime.UtcNow + Rng.HumanMinutes(lo, hi);
			}

			return DateTime.UtcNow >= _openAt;
		}
	}

	/// <summary>A human-mode account has to wait a while longer before the next one (no effect without human mode).</summary>
	public void Space(int lo, int hi) {
		if (bot.Cfg.LegitMode) {
			_openAt = DateTime.UtcNow + Rng.HumanMinutes(lo, hi);
		}
	}
}
