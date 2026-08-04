using System.Collections.Concurrent;

namespace NocatFarm.Core;

/// <summary>
/// Process-wide pacing. Steam rate-limits per IP, not per account, so every bot has to queue behind the
/// same gates or three accounts starting together look exactly like an attack.
///
/// The pattern is ASF's and it is deliberate: the slot is taken immediately and RELEASED ON A TIMER from a
/// detached task, so the caller proceeds now and the *next* caller is the one that waits. A plain
/// "await Delay then act" would make every login pay the delay, including the first.
/// </summary>
public static class Limiters {
	/// <summary>Minimum spacing between two logins from this machine.</summary>
	public const int LoginDelaySeconds = 10;

	/// <summary>How long to sit out when Steam answers a login with a rate-limit. Configurable.</summary>
	public static int LoginCooldownMinutes => Math.Max(1, Config.Live.Global.LoginCooldownMinutes);

	/// <summary>Minimum spacing between two requests to the same Steam host. Configurable.</summary>
	private static int WebDelayMs => Math.Max(0, Config.Live.Global.WebRequestGapMs);

	/// <summary>Shortest and longest time to stay off a host that has answered 429.</summary>
	private const int BackoffMinMinutes = 5;
	private const int BackoffMaxMinutes = 40;

	private static readonly SemaphoreSlim LoginSlot = new(1, 1);
	private static readonly SemaphoreSlim LoginCooldownLatch = new(1, 1);
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> WebSlots = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, Backoff> WebBackoff = new(StringComparer.OrdinalIgnoreCase);

	private sealed class Backoff {
		public DateTime Until;
		public int Minutes;
	}

	/// <summary>
	/// Steam answered 429 for <paramref name="host"/>. That limit is per IP, so it is no good backing off only the
	/// account that happened to ask - every account has to come off the host together, and every further request
	/// while it is hot only extends the ban. The wait doubles on each repeat and resets on the next answer.
	/// </summary>
	/// <returns>How long the host is now closed for, or <see cref="TimeSpan.Zero"/> if somebody just closed it.</returns>
	public static TimeSpan NoteRateLimited(string host) {
		DateTime now = DateTime.UtcNow;
		Backoff state = WebBackoff.GetOrAdd(host, static _ => new Backoff());

		lock (state) {
			// Three accounts in flight will each collect their own 429 off the same limit. Only the first one
			// lengthens the wait; the rest are the same event arriving three times.
			if (state.Until > now) {
				return TimeSpan.Zero;
			}

			state.Minutes = state.Minutes <= 0 ? BackoffMinMinutes : Math.Min(BackoffMaxMinutes, state.Minutes * 2);
			state.Until = now.AddMinutes(state.Minutes);

			return TimeSpan.FromMinutes(state.Minutes);
		}
	}

	/// <summary>A request to <paramref name="host"/> came back fine, so whatever tripped the limit is over.</summary>
	public static void NoteWebOk(string host) {
		if (!WebBackoff.TryGetValue(host, out Backoff? state)) {
			return;
		}

		lock (state) {
			if (state.Until <= DateTime.UtcNow) {
				state.Minutes = 0;
			}
		}
	}

	/// <summary>How much longer <paramref name="host"/> is closed for. Zero when it is open.</summary>
	public static TimeSpan RateLimitedFor(string host) {
		if (!WebBackoff.TryGetValue(host, out Backoff? state)) {
			return TimeSpan.Zero;
		}

		lock (state) {
			TimeSpan left = state.Until - DateTime.UtcNow;

			return left > TimeSpan.Zero ? left : TimeSpan.Zero;
		}
	}

	/// <summary>Take a login slot. Returns as soon as it is this caller's turn.</summary>
	public static async Task WaitForLoginSlotAsync(CancellationToken ct = default) {
		await LoginSlot.WaitAsync(ct).ConfigureAwait(false);

		try {
			// Blocks only while somebody is serving a rate-limit cooldown; otherwise it's a free pass through.
			await LoginCooldownLatch.WaitAsync(ct).ConfigureAwait(false);
			LoginCooldownLatch.Release();
		} finally {
			// Detached: this caller goes now, the next one waits LoginDelaySeconds.
			_ = Task.Run(async () => {
				try {
					await Task.Delay(TimeSpan.FromSeconds(LoginDelaySeconds), CancellationToken.None).ConfigureAwait(false);
				} finally {
					LoginSlot.Release();
				}
			}, CancellationToken.None);
		}
	}

	/// <summary>
	/// Serve a login rate-limit cooldown for everybody. If another bot is already serving one this returns
	/// immediately - there is no point in three accounts each waiting 25 minutes in series.
	/// </summary>
	public static async Task ServeLoginCooldownAsync(CancellationToken ct = default) {
		if (!await LoginCooldownLatch.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) {
			return;   // somebody else already holds it
		}

		try {
			Log.Warn($"Steam is rate-limiting logins - every account waits {LoginCooldownMinutes}m");
			await Task.Delay(TimeSpan.FromMinutes(LoginCooldownMinutes), ct).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// shutting down
		} finally {
			LoginCooldownLatch.Release();
		}
	}

	/// <summary>
	/// Run a web request against <paramref name="host"/>, spaced out from every other request to that host.
	///
	/// Returns default without asking anything while the host is serving a 429 backoff. Requests made during one
	/// are not merely wasted - they are what keeps the limit alive - so the caller is told no locally instead.
	/// </summary>
	public static async Task<T?> WebAsync<T>(string host, Func<Task<T>> request) {
		if (RateLimitedFor(host) > TimeSpan.Zero) {
			return default;
		}

		SemaphoreSlim slot = WebSlots.GetOrAdd(host, static _ => new SemaphoreSlim(1, 1));
		int gap = WebDelayMs;

		await slot.WaitAsync().ConfigureAwait(false);

		try {
			return await request().ConfigureAwait(false);
		} finally {
			if (gap <= 0) {
				slot.Release();
			} else {
				_ = Task.Run(async () => {
					try {
						await Task.Delay(gap, CancellationToken.None).ConfigureAwait(false);
					} finally {
						slot.Release();
					}
				}, CancellationToken.None);
			}
		}
	}
}
