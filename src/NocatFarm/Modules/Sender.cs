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
					} catch (IOException) {
						// it will simply be overwritten next time
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

				Looting.Report(Bot, await Looting.SendToMasterAsync(Bot, ct).ConfigureAwait(false));
			} finally {
				_lastSendUtc = DateTime.UtcNow;
				OneAtATime.Release();
			}

			Schedule(hours);
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
	private void Schedule(int hours) {
		_nextDue = NextDue(DateTime.Now, hours, Bot.Cfg.SendAroundHour, Rng.Next(0, 60), Rng.Next(0, 101) / 1000.0).ToUniversalTime();
		Save();
	}

	/// <summary>When the next send is due, in local time - separate so it can be tested.</summary>
	internal static DateTime NextDue(DateTime nowLocal, int hours, int atHour, int minute, double slack) {
		if (atHour is < 0 or > 23) {
			return nowLocal + TimeSpan.FromHours(hours * (1 + slack));
		}

		DateTime due = nowLocal.Date.AddHours(atHour).AddMinutes(minute);

		// At least the period less a day away: 24 hours is simply the next time the hour comes round.
		while ((due <= nowLocal) || (due - nowLocal < TimeSpan.FromHours(Math.Max(0, hours - 24)))) {
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
			Log.Debug(new Said("couldn't read the send schedule: {0}", e.Message), Bot.Name);
		}

		return null;
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(new SendState(_nextDue!.Value.Ticks)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the send schedule: {0}", e.Message), Bot.Name);
		}
	}
}
