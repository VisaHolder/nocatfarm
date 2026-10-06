using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Sends this account's items to your main account every so many hours, so cards don't pile up between runs.
/// </summary>
/// <remarks>
/// "Send when farming finishes" only fires at the end of a farming run, and an account that idles for weeks between
/// runs collects drops from events, badges and boosters the whole time. This sends on a timer instead, through
/// exactly the same path as the "send" command: same item types, same banned-game exclusions, same batching.
/// The next send time is kept on disk, so frequent restarts don't keep pushing it back.
/// </remarks>
public sealed class Sender(Bot bot) : BotModule(bot) {
	private HumanGate? _gate;

	private Said _status = new("off");
	private DateTime? _nextDue;
	private int _periodSeen;
	private int _hourSeen = -2;

	/// <summary>Sends in a row that found every wanted item busy with a sale or another send.</summary>
	private int _busyTries;

	/// <summary>How many of those are tried again a few minutes later before the send just waits for its next time.</summary>
	private const int BusyRetries = 6;

	/// <summary>
	/// When a send that found everything busy tries again: a few minutes on (3-8), up to <see cref="BusyRetries"/> times in a
	/// row - null once that's used up, or when it wasn't busy, and the send waits for its usual next time.
	/// </summary>
	/// <remarks>
	/// It used to wait the whole period - a day, typically - while the message it logged said "try again in a few minutes".
	/// A sale lists its cards a minute or so apart, so the cards are free again well inside that.
	/// </remarks>
	internal static TimeSpan? BusyRetry(bool busy, int tries) => busy && (tries <= BusyRetries) ? Rng.Minutes(3, 8) : null;

	/// <summary>
	/// One account's send at a time, a few minutes apart. Several accounts set to the same hour would otherwise read
	/// their inventories and post their offers in the same minute from the same IP - which is what Steam rate-limits.
	/// </summary>
	private static readonly SemaphoreSlim OneAtATime = new(1, 1);
	private static DateTime _lastSendUtc = DateTime.MinValue;

	public override string Name => "sending";
	public override string Status => Bot.Cfg.SendEveryHours > 0 ? _status : "";

	private string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", $"send-{Bot.Name}.json");

	protected override async Task RunAsync(CancellationToken ct) {
		_nextDue = Load();

		while (!ct.IsCancellationRequested) {
			int hours = Math.Clamp(Bot.Cfg.SendEveryHours, 0, 168);

			if (hours == 0) {
				_status = new Said("off");

				// Forget the schedule while it's off, so switching it back on waits a full period rather than
				// firing at once on a due time that went stale while it was off.
				if (_nextDue != null) {
					_nextDue = null;
					_periodSeen = 0;

					try {
						File.Delete(StatePath);
					} catch (IOException e) {
						// it will simply be overwritten next time
						Log.Failed("couldn't delete the send schedule", e, Bot.Name);
					}
				}

				if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			// Switched on, or the period changed: the first send is one period from now, not immediately - so turning
			// this on never fires a trade offer the same second.
			int atHour = Bot.Cfg.SendAroundHour is >= 0 and <= 23 ? Bot.Cfg.SendAroundHour : -1;

			if ((_nextDue == null) || (_periodSeen != 0 && _periodSeen != hours) || (_hourSeen != -2 && _hourSeen != atHour)) {
				Schedule(hours);
				DateTime first = _nextDue!.Value;
				Log.Info(new Said("sending items to your main every {0}, first around {1}", Fmt.Hm(hours * 60),
					(Func<string>) (() => Fmt.Clock(first))), Bot.Name);
			}

			_periodSeen = hours;
			_hourSeen = atHour;
			DateTime due = _nextDue!.Value;

			if (DateTime.UtcNow < due) {
				_status = new Said("next send around {0}", (Func<string>) (() => Fmt.Clock(due)));

				// A minute at a time, so switching it off or changing the period takes effect straight away.
				if (!await Sleep(Min(due - DateTime.UtcNow, TimeSpan.FromMinutes(1)), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (!Bot.IsOnline || !Bot.Web.Ready || Bot.Paused) {
				_status = new Said("waiting for the account");

				if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			// A trade offer from an account that is asleep on the friends list is the same tell as accepting one.
			// Between your own accounts nobody sees it, so any time of day - just not the moment it signs in.
			_gate ??= HumanGate.Quiet(Bot);

			if (!_gate.Open) {
				_status = new Said("send due - waiting until the account is awake");

				if (!await Sleep(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			await OneAtATime.WaitAsync(ct).ConfigureAwait(false);

			try {
				TimeSpan gap = (_lastSendUtc + Rng.Minutes(3, 6)) - DateTime.UtcNow;

				if (gap > TimeSpan.Zero) {
					_status = new Said("send due - waiting a few minutes after another account's send");

					if (!await Sleep(gap, ct).ConfigureAwait(false)) {
						return;
					}
				}

				(string text, bool busy) = await Looting.SendToMasterCheckedAsync(Bot, null, ct).ConfigureAwait(false);
				Looting.Report(Bot, text);
				_busyTries = busy ? _busyTries + 1 : 0;
			} finally {
				_lastSendUtc = DateTime.UtcNow;
				OneAtATime.Release();
			}

			if (BusyRetry(_busyTries > 0, _busyTries) is { } soon) {
				_nextDue = DateTime.UtcNow + soon;
				Save();
				DateTime retry = _nextDue.Value;
				Log.Info(new Said("its items are busy with a sale or another send - sending again around {0}", (Func<string>) (() => Fmt.Clock(retry))), Bot.Name);

				continue;
			}

			_busyTries = 0;
			Schedule(hours, justSent: true);
			DateTime again = _nextDue!.Value;
			Log.Debug(new Said("next send around {0}", (Func<string>) (() => Fmt.Clock(again))), Bot.Name);
		}
	}

	private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

	/// <summary>
	/// The next send. With a set hour: a random minute in that hour, on the first day far enough off for the period -
	/// every 24 hours is the next time that hour comes round, every 48 skips a day. Without one: one period from now,
	/// plus up to a tenth of it again so it never lands on the same minute.
	/// </summary>
	/// <param name="justSent">A send has just finished - see <see cref="NextDue"/>.</param>
	private void Schedule(int hours, bool justSent = false) {
		_nextDue = NextDue(DateTime.Now, hours, Bot.Cfg.SendAroundHour, Rng.Next(0, 60), Rng.Next(0, 101) / 1000.0, justSent).ToUniversalTime();
		Save();
	}

	/// <summary>When the next send is due, in local time - separate so it can be tested.</summary>
	/// <param name="justSent">
	/// A send has just finished. The next one is then at least half a day off, and with a longer period all but half a
	/// day of it - so after the 22:07 send, "around 22:00" is tomorrow.
	/// </param>
	/// <remarks>
	/// Picking a fresh random minute in the hour straight after the send found one still ahead today about half the
	/// time: sent at 22:07, next 22:12, then 22:27, then tomorrow - two or three offers a night instead of one. And
	/// every 48 hours could come round again in 24, when the new minute happened to land a little later than the last.
	/// </remarks>
	internal static DateTime NextDue(DateTime nowLocal, int hours, int atHour, int minute, double slack, bool justSent = false) {
		if (atHour is < 0 or > 23) {
			return nowLocal + TimeSpan.FromHours(hours * (1 + slack));
		}

		DateTime due = nowLocal.Date.AddHours(atHour).AddMinutes(minute);

		// At least the period less a day away: 24 hours is simply the next time the hour comes round - today, if it's
		// still ahead, the first time it's set. Right after a send, at least half a day (see justSent).
		TimeSpan least = TimeSpan.FromHours(justSent ? Math.Max(12, hours - 12) : Math.Max(0, hours - 24));

		while ((due <= nowLocal) || (due - nowLocal < least)) {
			due = due.AddDays(1);
		}

		return due;
	}

	private sealed record SendState(long NextDueTicks);

	private DateTime? Load() {
		try {
			if (File.Exists(StatePath) && JsonSerializer.Deserialize<SendState>(File.ReadAllText(StatePath)) is { } saved) {
				return new DateTime(saved.NextDueTicks, DateTimeKind.Utc);
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the send schedule: {0}", Log.Describe(e)), Bot.Name);
		}

		return null;
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(new SendState(_nextDue!.Value.Ticks)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the send schedule: {0}", Log.Describe(e)), Bot.Name);
		}
	}
}
