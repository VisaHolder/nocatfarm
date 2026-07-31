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

	private static readonly SemaphoreSlim LoginSlot = new(1, 1);
	private static readonly SemaphoreSlim LoginCooldownLatch = new(1, 1);
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> WebSlots = new(StringComparer.OrdinalIgnoreCase);

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

	/// <summary>Run a web request against <paramref name="host"/>, spaced out from every other request to that host.</summary>
	public static async Task<T> WebAsync<T>(string host, Func<Task<T>> request) {
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
