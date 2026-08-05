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
			try {
				await RunAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				// normal shutdown
			} catch (Exception e) {
				// never silent: a module dying quietly is the worst failure mode there is
				Log.Error(new Said("module '{0}' stopped: {1}: {2}", Name, e.GetType().Name, e.Message), Bot.Name);
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

	/// <summary>Stop and start again, for a setting that turns the whole module on or off.</summary>
	public async Task RestartAsync() {
		await StopAsync().ConfigureAwait(false);
		await Task.Delay(50).ConfigureAwait(false);
		await StartAsync().ConfigureAwait(false);
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
