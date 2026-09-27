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
	private Said _status = new("off");
	private DateTime? _nextDue;
	private int _periodSeen;

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
			if ((_nextDue == null) || (_periodSeen != 0 && _periodSeen != hours)) {
				Schedule(hours);
				DateTime first = _nextDue!.Value;
				Log.Info(new Said("sending items to your main account every {0} - the first send is around {1}", Fmt.Hm(hours * 60),
					(Func<string>) (() => Fmt.Clock(first))), Bot.Name);
			}

			_periodSeen = hours;
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
			if (!HumanMode.AwakeFor(Bot)) {
				_status = new Said("send due - waiting until the account is awake");

				if (!await Sleep(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			string result = await Looting.SendToMasterAsync(Bot, ct).ConfigureAwait(false);

			// "Nothing to send" is the ordinary answer most of the time - worth a line in the file, not on screen.
			if (result.Contains(": sent ", StringComparison.Ordinal)) {
				Log.Info(result, Bot.Name);
			} else {
				Log.Debug(result, Bot.Name);
			}

			Schedule(hours);
		}
	}

	private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

	/// <summary>Next send one period from now, plus up to a tenth of it again so it never lands on the same minute.</summary>
	private void Schedule(int hours) {
		double slack = Rng.Next(0, 101) / 1000.0;
		_nextDue = DateTime.UtcNow + TimeSpan.FromHours(hours * (1 + slack));
		Save();
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
