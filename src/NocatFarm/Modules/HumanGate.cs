using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// When a human-mode account may do one kind of thing. Without human mode it's always open.
/// </summary>
/// <remarks>
/// Three kinds. A gate that follows the day (the default) is for things other people see as they happen - a trade
/// accepted, a comment posted, an achievement unlocked: it waits until the account is awake and settled in (see
/// <see cref="HumanMode.ReadyFor"/>), then a random human delay of its own. An <see cref="OwnDay"/> gate is for
/// things the account starts itself that show up on it afterwards - a group joined, a badge crafted, a card listed,
/// a free game added: the same, but it keeps to the account's day even where "act only while awake" is off, because
/// that switch is about answering other people, not about joining a group at 4am. A <see cref="Quiet"/> gate is for
/// things nobody sees at all - cards sent to your own account, a notification read: it ignores sleep entirely and
/// only waits a short random while after signing in, so a sign-in isn't ten things hitting Steam in the same second.
/// </remarks>
public sealed class HumanGate(Bot bot, bool followsDay = true, bool ownDay = false) {
	private bool _ready;
	private DateTime _openAt = DateTime.MaxValue;
	private DateTime? _signedIn;

	/// <summary>What the wait running now started from, and when - for saying why it's shut (see <see cref="Waiting"/>).</summary>
	private Start _start = Start.SignedIn;
	private DateTime _startedAt;

	private enum Start { SignedIn, Woke, YouStopped, Back, Spaced }

	/// <summary>
	/// Answering other people waits: the account is finishing up before it logs off, or you're playing on a human-mode
	/// account yourself. For what doesn't go through a gate - trades, gifts, queued keys - which read only whether the
	/// account was awake, and that stays true while you play: an offer was accepted and confirmed mid-match.
	/// </summary>
	public static bool StandsBack(Bot bot) => bot.Stopping || (bot.Cfg.LegitMode && bot.PlayingBlocked);

	/// <summary>For things nobody watches: any time of day, just not the moment it signs in.</summary>
	public static HumanGate Quiet(Bot bot) => new(bot, followsDay: false);

	/// <summary>For things the account does by itself that show on it afterwards: only in its own day, settled in.</summary>
	public static HumanGate OwnDay(Bot bot) => new(bot, followsDay: true, ownDay: true);

	/// <summary>Whether it may act now. Each time the account becomes ready again (signed in, woke up), a fresh wait starts.</summary>
	public bool Open {
		get {
			if (!bot.Cfg.LegitMode) {
				return true;
			}

			// A new sign-in is a fresh start, whether or not anybody asked while it was gone. Every module stops while the
			// account is signed out, so the gate was never asked then - and after a reconnect it was still open from the
			// last session, letting a send or a booster pack go in the first seconds of the new one.
			if (bot.OnlineSince != _signedIn) {
				_signedIn = bot.OnlineSince;
				_ready = false;
				_start = Start.SignedIn;
			}

			// Behind-the-scenes things follow the day too when the account is set up that way.
			bool day = followsDay || bot.Cfg.QuietThingsWaitForDay;

			// Finishing up before it logs off: nothing new starts, quiet things included - they wait for the next sign-in.
			// You're on the account yourself: nothing that shows on it starts behind your back - a comment posted, a group
			// joined, a card listed while you're in the middle of a match. It waits till you're done, then a fresh wait.
			// Things nobody sees (a Quiet gate - items sent to your own account) carry on.
			bool youOnIt = followsDay && bot.PlayingBlocked;

			if (bot.Stopping || youOnIt || (ownDay ? !HumanMode.UpFor(bot) : day ? !HumanMode.ReadyFor(bot) : !bot.IsOnline)) {
				// Shut for a reason of its own, so the wait that follows starts from that - not from the sign-in it began with.
				// Settling in after a sign-in keeps the sign-in as the start; a break spent offline is coming back - known by the
				// break itself, not only by a spacing having run before it, or the wait after one read "signed in again".
				_start = youOnIt ? Start.YouStopped : HumanMode.Asleep(bot) ? Start.Woke
					: (HumanMode.OnOfflineBreak(bot) || (_start == Start.Spaced)) ? Start.Back : _start;

				_ready = false;

				return false;
			}

			if (!_ready) {
				_ready = true;
				_startedAt = _start == Start.SignedIn ? (bot.OnlineSince ?? DateTime.UtcNow) : DateTime.UtcNow;
				(int lo, int hi) = followsDay
					? ReactionSpeed.Range(bot.Cfg, nameof(BotConfig.WakeDelayMinMinutes), nameof(BotConfig.WakeDelayMaxMinutes))
					: ReactionSpeed.Range(bot.Cfg, nameof(BotConfig.QuietDelayMinMinutes), nameof(BotConfig.QuietDelayMaxMinutes));

				if ((lo <= 0) && (hi <= 0)) {
					(lo, hi) = (0, 0);
				} else if (hi < lo) {
					hi = lo;
				}

				DateTime at = DateTime.UtcNow + Rng.HumanMinutes(lo, hi);

				// A spacing still running from before (Space) isn't cut short by a fresh wait that ends sooner.
				_openAt = (_openAt != DateTime.MaxValue) && (_openAt > at) ? _openAt : at;
			}

			return DateTime.UtcNow >= _openAt;
		}
	}

	/// <summary>A human-mode account has to wait a while longer before the next one (no effect without human mode).</summary>
	public void Space(int lo, int hi) {
		if (bot.Cfg.LegitMode) {
			_openAt = DateTime.UtcNow + Rng.HumanMinutes(lo, hi);
			_start = Start.Spaced;
			_startedAt = DateTime.UtcNow;
		}
	}

	/// <summary>
	/// Why it's shut, in plain words with the times - asked as the loop asks, so a wait that hasn't started yet starts now,
	/// as it would have at the loop's next look. Empty when it's open. The account's own reasons (asleep, settling in, on a
	/// break, you playing) come first; then the random wait itself, and what it started from: a sign-in - a restart signs
	/// every account in again, so "only just woken up" said of an account up for hours was wrong - waking, or you
	/// finishing on it.
	/// </summary>
	public Said Waiting() {
		if (Open) {
			return default;
		}

		if (bot.Stopping) {
			return new Said("it's signing off");
		}

		if (followsDay && bot.PlayingBlocked) {
			return new Said("you're playing on it - human mode waits until you're done");
		}

		if (ownDay ? !HumanMode.UpFor(bot) : (followsDay || bot.Cfg.QuietThingsWaitForDay) ? !HumanMode.ReadyFor(bot) : !bot.IsOnline) {
			return HumanMode.NotUpWhy(bot);
		}

		string from = Fmt.Clock(_startedAt);
		string until = Fmt.Clock(_openAt);

		return _start switch {
			Start.Woke => new Said("it woke up at {0} - human mode waits a little before doing things that show on the account; it can go ahead from about {1}", from, until),
			Start.YouStopped => new Said("you stopped playing on it at {0} - human mode waits a little before doing things that show on the account; it can go ahead from about {1}", from, until),
			Start.Back => new Said("it was back from a break at {0} - human mode waits a little before doing things that show on the account; it can go ahead from about {1}", from, until),
			Start.Spaced => new Said("it did something at {0} - human mode spaces things out; it can go ahead from about {1}", from, until),
			// "Again" only past this run's first sign-in: the first after starting nocat.farm said it too.
			_ when bot.SignIns > 1 => new Said("it signed in again at {0} - human mode waits a little before doing things that show on the account; it can go ahead from about {1}", from, until),
			_ => new Said("it signed in at {0} - human mode waits a little before doing things that show on the account; it can go ahead from about {1}", from, until)
		};
	}
}
