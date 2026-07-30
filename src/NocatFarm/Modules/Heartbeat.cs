using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Says what each account is doing, on a timer.
///
/// Everything else in here only writes to the log when something HAPPENS - a session starts, a card drops, a
/// comment lands. That reads fine while you are watching, and tells you nothing at all when you come back six
/// hours later: a long quiet stretch looks identical whether the account played all night or silently stopped
/// doing anything at three in the morning. ArchiSteamFarm printed a steady "still on it" line for exactly this
/// reason, and its absence is why "is it actually still farming?" was unanswerable from the log alone.
///
/// Two intervals, because one does not fit: a line every five minutes is right while a game is open and is
/// pure noise across an eight-hour night. Asleep, on a break, paused or offline all use the slower one.
/// </summary>
public sealed class Heartbeat(Bot bot) : BotModule(bot) {
	private DateTime _next = DateTime.MinValue;
	private bool _armed;
	private DateTime _lastTick = DateTime.MinValue;

	/// <summary>What was reported last, so the first line about a NEW activity does not claim it is continuing.</summary>
	private string _lastDoing = "";

	public override string Name => "heartbeat";

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				Beat();
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Debug($"heartbeat hiccup: {e.GetType().Name}: {e.Message}", Bot.Name);
			}

			// Checked far more often than it prints, so a change of phase is picked up promptly rather than
			// being reported up to five minutes late.
			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>The account's own setting when it has one, otherwise the global. -1 means "never".</summary>
	private static int Pick(int perAccount, int global) => perAccount switch {
		< 0 => 0,            // explicitly silenced for this account
		0 => global,         // follow the global
		_ => perAccount
	};

	private void Beat() {
		BotStatus status = BotStatus.Of(Bot);

		// Bank the lifetime total here because this is the one module that already ticks on a fixed cadence
		// whatever else is happening. Counted from what Steam was actually TOLD is running, so an account
		// signed in doing nothing accrues nothing, and only for time that genuinely elapsed - a machine that
		// slept for six hours must not wake up and credit six hours of farming.
		DateTime now = DateTime.UtcNow;
		double since = _lastTick == DateTime.MinValue ? 0 : (now - _lastTick).TotalMinutes;
		_lastTick = now;

		if ((since > 0) && (Bot.PlayingApps.Count > 0)) {
			Lifetime.Add(Bot.Name, since);
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
			Log.Info(status.Doing, Bot.Name);

			return;
		}

		// "still" only when it IS still.
		//
		// The word is what makes a heartbeat read as a progress report rather than a fresh event - but on the
		// first report after the activity changes there is nothing to be still doing, and "still playing
		// Counter-Strike 2" as the very first line about a session that just began reads as though something
		// was missed. The first one says what started; every one after it says it is continuing.
		bool same = status.Doing == _lastDoing;
		_lastDoing = status.Doing;

		Log.Info((same ? "still " : "now ") + status.Line(), Bot.Name);
	}
}
