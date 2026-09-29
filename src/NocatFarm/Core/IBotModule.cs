namespace NocatFarm.Core;

/// <summary>
/// A feature that hangs off a bot (card farming, rep4rep, idling, ...).
/// Modules own their own loops; the bot only starts and stops them.
/// </summary>
public interface IBotModule {
	string Name { get; }

	/// <summary>One short line for the dashboard, e.g. "4 games / 12 cards left".</summary>
	string Status { get; }

	Task StartAsync();
	Task StopAsync();
}

/// <summary>Base class handling the loop/cancellation boilerplate every module repeats.</summary>
public abstract class BotModule(Bot bot) : IBotModule {
	private readonly Lock _gate = new();

	protected Bot Bot { get; } = bot;
	protected CancellationTokenSource? Cts { get; private set; }

	public abstract string Name { get; }
	public virtual string Status => "";

	/// <summary>
	/// Start the loop, unless it is already running.
	///
	/// Start and stop share a lock, and the loop clears its own handle when it ends. A plain null check let two
	/// callers both win the race and start a second loop that nothing could ever cancel - for the rep4rep module
	/// that means two loops posting comments, which is how an account blows past Steam's daily ceiling.
	/// </summary>
	public Task StartAsync() {
		CancellationToken ct;

		lock (_gate) {
			if (Cts != null) {
				return Task.CompletedTask;   // already running
			}

			Cts = new CancellationTokenSource();
			ct = Cts.Token;
		}

		_ = Task.Run(async () => {
			int crashes = 0;

			try {
				while (true) {
					DateTime began = DateTime.UtcNow;

					try {
						await RunAsync(ct).ConfigureAwait(false);

						return;   // it ended by itself (switched off, nothing to do) - that is not a crash
					} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
						// normal shutdown - and only that: a request that timed out is an OperationCanceledException too, and
						// taking one of those for a shutdown is how a module used to stop without a single word in the log
						return;
					} catch (Exception e) {
						// And not for good. One odd answer from Steam used to switch the feature off until the next restart.
						// Waits 1, 2, 4 ... up to 30 minutes, so a module that keeps failing can't fill the log; a run that
						// lasted an hour first counts as a fresh start.
						crashes = DateTime.UtcNow - began > TimeSpan.FromHours(1) ? 1 : crashes + 1;

						// never silent: a module dying quietly is the worst failure mode there is. An error (and a pop-up,
						// and a message to your phone) the first time; the same thing again is only a warning.
						if (crashes == 1) {
							Log.Error(new Said("module '{0}' stopped: {1}: {2}", Name, e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);
						} else {
							Log.Warn(new Said("module '{0}' stopped: {1}: {2}", Name, e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);
						}

						// Where it broke, for the file: the message says what, only the stack says where.
						Log.StackToFile(e, Bot.Name);
					}

					int minutes = Math.Min(30, 1 << Math.Min(crashes - 1, 5));
					Log.Info(new Said("'{0}' starts again in {1}m", Name, minutes), Bot.Name);

					if (!await Sleep(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false)) {
						return;
					}
				}
			} finally {
				// Release the handle whichever way the loop ended, so a module that returned early (feature
				// switched off) can genuinely be started again later instead of looking permanently running.
				lock (_gate) {
					if (Cts != null && Cts.Token == ct) {
						Cts.Dispose();
						Cts = null;
					}
				}
			}
		}, ct);

		return Task.CompletedTask;
	}

	/// <summary>
	/// Stop the loop. Virtual so a module can flush state that only it knows about before it goes away - human
	/// mode has to bank the minutes of the session in flight, or a restart replays them.
	/// </summary>
	public virtual Task StopAsync() {
		CancellationTokenSource? cts;

		lock (_gate) {
			cts = Cts;
			Cts = null;
		}

		if (cts == null) {
			return Task.CompletedTask;
		}

		try {
			cts.Cancel();
			cts.Dispose();
		} catch {
			// already gone
		}

		return Task.CompletedTask;
	}

	protected abstract Task RunAsync(CancellationToken ct);

	/// <summary>Cancellable sleep that returns false when the module is shutting down.</summary>
	protected static async Task<bool> Sleep(TimeSpan wait, CancellationToken ct) {
		try {
			await Task.Delay(wait, ct).ConfigureAwait(false);

			return true;
		} catch (OperationCanceledException) {
			return false;
		}
	}
}
