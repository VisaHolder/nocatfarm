using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Says what each account is doing, on a timer.
///
/// Everything else in here only writes to the log when something HAPPENS - a session starts, a card drops, a
/// comment lands. That reads fine while you are watching, and tells you nothing at all when you come back six
/// hours later: a long quiet stretch looks identical whether the account played all night or silently stopped
/// doing anything at three in the morning. A steady "still on it" line answers "is it actually still farming?"
/// from the log alone.
///
/// Two intervals, because one does not fit: a line every five minutes is right while a game is open and is
/// pure noise across an eight-hour night. Asleep, on a break, paused or offline all use the slower one.
/// </summary>
public sealed class Heartbeat(Bot bot) : BotModule(bot) {
	private DateTime _next = DateTime.MinValue;
	private bool _armed;
	private DateTime _lastTick = DateTime.MinValue;

	public override string Name => "heartbeat";

	protected override async Task RunAsync(CancellationToken ct) {
		// A fresh start after a reconnect: the time the account was signed out is not time played.
		_lastTick = DateTime.MinValue;

		while (!ct.IsCancellationRequested) {
			try {
				Beat();
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				// Every 20 seconds: the same failure once an hour rather than three times a minute - and its stack with
				// it, since nothing in here talks to Steam and a throw is a bug.
				if (Log.DebugOnChange($"hiccup:{Name}:{Bot.Name}", $"heartbeat hiccup: {Log.Describe(e)}", Bot.Name)) {
					Log.StackToFile(e, Bot.Name);
				}
			}

			// Checked far more often than it prints, so a change of phase is picked up promptly rather than
			// being reported up to five minutes late.
			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>
	/// Minutes since the last tick that can count as played. The tick is every 20 seconds, so a gap of more than a few
	/// minutes is time this process wasn't running - the machine asleep, the clock moved - and none of it is played.
	/// Counting the whole gap credited a night of sleep as a night of farming: the first tick after waking still saw
	/// the games from before, because Steam hadn't dropped the connection yet.
	/// </summary>
	internal static double Creditable(DateTime last, DateTime now) {
		double since = last == DateTime.MinValue ? 0 : (now - last).TotalMinutes;

		return since is > 0 and <= 3 ? since : 0;
	}

	/// <summary>The account's own setting when it has one, otherwise the global. -1 means "never".</summary>
	private static int Pick(int perAccount, int global) => perAccount switch {
		< 0 => 0,            // explicitly silenced for this account
		0 => global,         // follow the global
		_ => perAccount
	};

	/// <summary>Whether this tick's time goes to the account's hours: its own games are on, and nobody else's session is playing.</summary>
	public static bool Banks(Bot bot) => (bot.PlayingApps.Count > 0) && (bot.OtherSessionApp == 0);

	private void Beat() {
		BotStatus status = BotStatus.Of(Bot);

		// Bank the lifetime total here because this is the one module that already ticks on a fixed cadence
		// whatever else is happening. Counted from what Steam was actually TOLD is running, so an account
		// signed in doing nothing accrues nothing, and only for time that genuinely elapsed - a machine that
		// slept for six hours must not wake up and credit six hours of farming.
		DateTime now = DateTime.UtcNow;
		double since = Creditable(_lastTick, now);
		_lastTick = now;

		// Not while you're playing on the account yourself: Steam takes our games-played and ignores it then. With "stand
		// down when I play" off our list stays as it was, and your evening was banked as the account's hours.
		if ((since > 0) && Banks(Bot)) {
			// Every real game counts, the way Steam credits them - a custom name on its own (app 0) isn't one.
			Lifetime.Add(Bot.Name, since, Bot.PlayingApps.Count(static a => a != 0));

			// And the same minutes by day and by game, for the dashboard's history charts.
			History.AddPlay(Bot.Name, since, Bot.PlayingApps);
		}

		// Per account first, global second.
		//
		// One rate across a whole fleet is wrong in both directions at once: the account you actually watch
		// wants a line every few minutes, and thirty boosting accounts reporting at the same rate is the log
		// scrolling past faster than anyone can read it. 0 means "whatever the global says", -1 means silence.
		int every = status.AtTheKeyboard
			? Pick(Bot.Cfg.StatusEveryMinutes, Live.Global.StatusEveryMinutes)
			: Pick(Bot.Cfg.StatusQuietEveryMinutes, Live.Global.StatusQuietEveryMinutes);

		if (every <= 0) {
			_armed = false;   // switching it back on waits a full interval rather than firing a stale beat

			return;
		}

		DateTime latest = now.AddMinutes(every);

		// Wait a full interval before the first report.
		//
		// Firing on the first tick meant every account logged "still online, nothing running" about one second
		// after logging on - before any module had decided anything, and in the log immediately ABOVE the line
		// saying what it had actually started doing. Nothing is "still" happening a second after login.
		if (!_armed) {
			_armed = true;
			_next = latest;

			return;
		}

		// Never sit on a deadline longer than the CURRENT interval allows. An account that logged in idle took
		// the quiet interval, so without this it would say nothing for half an hour after starting a game.
		if (_next > latest) {
			_next = latest;
		}

		if (now < _next) {
			return;
		}

		_next = now.AddMinutes(every);

		if (!Bot.IsOnline) {
			Log.Status(status.Doing, Bot.Name);

			return;
		}

		// The status alone - "playing Counter-Strike 2 · 8m left · 2h10m today" - with no "still" or "now" in front: the
		// window and the dashboard stack a run of them into one row updating in place (Core/LogStack.cs), so the row itself
		// tells a repeat from a fresh start. A Said, never Loc.T's finished text, so the row re-renders in a new language.
		// 1.8.2: asleep, done for the day, nothing to do - the same line again every interval says nothing new: said once,
		// and again only when it changes ("asleep · up 13:17" twenty minutes apart read like it went to sleep twice).
		// A break says "meal break · back in ~35m" when it starts and the next sitting says when it's over; a status line a
		// minute later only repeated it ("meal break · back in 33m").
		if (status.OnBreak) {
			return;
		}

		Said line = status.Line();
		string text = line.ToEnglish();

		if (status.QuietToday && string.Equals(text, _lastQuiet, StringComparison.Ordinal)) {
			return;
		}

		// 1.8.5: human mode already said it a moment ago as it went to sleep ("asleep · 8 games · up 13:33" at 02:11, and this
		// line again at 02:12) - the same words from this account in the last 15 minutes count as said.
		if (status.QuietToday && Log.Recent(60).Any(e => (e.Source == Bot.Name) && (DateTime.Now - e.When < TimeSpan.FromMinutes(15))
			&& string.Equals(e.Said.ToEnglish(), text, StringComparison.Ordinal))) {
			_lastQuiet = text;

			return;
		}

		_lastQuiet = status.QuietToday ? text : null;
		Log.Status(line, Bot.Name);
	}

	/// <summary>The quiet status said last (asleep, done for the day) - not said again until it changes.</summary>
	private string? _lastQuiet;
}
