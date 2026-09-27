using System.Collections.Concurrent;

namespace NocatFarm.Core;

/// <summary>
/// Process-wide pacing. Steam rate-limits per IP, not per account, so every bot has to queue behind the
/// same gates or three accounts starting together look exactly like an attack.
///
/// Each gate remembers the next moment it may be used. Taking a turn books the later of "now" and that moment, and
/// moves it on by the gap - so the first caller goes straight away and only the ones behind it wait, in order.
/// </summary>
public static class Limiters {
	/// <summary>Spacing between two logins from this machine: a few seconds, never the same twice.</summary>
	private static TimeSpan LoginGap => TimeSpan.FromMilliseconds(Rng.Next(8_000, 15_001));

	/// <summary>How long to sit out when Steam answers a login with a rate-limit. Configurable.</summary>
	public static int LoginCooldownMinutes => Math.Max(1, Config.Live.Global.LoginCooldownMinutes);

	/// <summary>Minimum spacing between two requests to the same Steam host. Configurable.</summary>
	private static int WebDelayMs => Math.Max(0, Config.Live.Global.WebRequestGapMs);

	/// <summary>Shortest and longest time to stay off a host that has answered 429.</summary>
	private const int BackoffMinMinutes = 5;
	private const int BackoffMaxMinutes = 40;

	/// <summary>A gate's next free moment.</summary>
	private sealed class Gate {
		public DateTime Next = DateTime.MinValue;
	}

	private static readonly Gate LoginGate = new();
	private static readonly SemaphoreSlim LoginCooldownLatch = new(1, 1);

	/// <summary>Per host: one request at a time, and each one waits for the host's next free moment.</summary>
	private static readonly ConcurrentDictionary<string, (SemaphoreSlim One, Gate Gate)> Hosts = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Book a turn at <paramref name="gate"/>, <paramref name="gap"/> after the last one, and wait for it.</summary>
	private static async Task TurnAsync(Gate gate, TimeSpan gap, CancellationToken ct) {
		DateTime now = DateTime.UtcNow;
		DateTime at;

		lock (gate) {
			at = gate.Next > now ? gate.Next : now;
			gate.Next = at + gap;
		}

		if (at > now) {
			await Task.Delay(at - now, ct).ConfigureAwait(false);
		}
	}
	private static readonly ConcurrentDictionary<string, Backoff> WebBackoff = new(StringComparer.OrdinalIgnoreCase);

	private sealed class Backoff {
		public DateTime Until;
		public int Minutes;
	}

	// ── remembered across restarts ──────────────────────────────────────────
	// A rate limit is Steam's, not this process's: restarting didn't lift it, it just forgot it and walked straight
	// back into it - a few restarts in a row were enough to get steamcommunity.com shut for everyone. So every
	// backoff is written down (state/backoff.json) and a fresh start picks the wait up where the last one left it.
	private static readonly object RememberGate = new();   // Monitor: Remember re-enters it through Remembered
	private static Dictionary<string, (DateTime Until, int Minutes)>? _remembered;

	private static string RememberPath => Path.Combine(Config.ConfigStore.ConfigDir, "state", "backoff.json");

	/// <summary>The backoff last written for <paramref name="key"/> - a host, or "market" - or nothing.</summary>
	public static (DateTime Until, int Minutes) Remembered(string key) {
		lock (RememberGate) {
			if (_remembered == null) {
				_remembered = new Dictionary<string, (DateTime, int)>(StringComparer.OrdinalIgnoreCase);

				try {
					if (File.Exists(RememberPath)) {
						foreach (string line in File.ReadAllLines(RememberPath)) {
							string[] part = line.Split('|');

							if ((part.Length == 3) && long.TryParse(part[1], out long ticks) && int.TryParse(part[2], out int minutes) && (ticks > 0)) {
								_remembered[part[0]] = (new DateTime(ticks, DateTimeKind.Utc), minutes);
							}
						}
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// Nothing remembered - the worst case is one early request.
				}
			}

			return _remembered.GetValueOrDefault(key);
		}
	}

	/// <summary>Write a backoff down so a restart keeps to it. Only called when it changes.</summary>
	public static void Remember(string key, DateTime until, int minutes) {
		lock (RememberGate) {
			Remembered(key);   // loads the file once

			if (_remembered!.TryGetValue(key, out (DateTime Until, int Minutes) old) && (old.Until == until) && (old.Minutes == minutes)) {
				return;
			}

			_remembered[key] = (until, minutes);

			// Anything long over and reset has nothing left to say.
			DateTime stale = DateTime.UtcNow.AddDays(-1);

			try {
				AtomicFile.Write(RememberPath, string.Join(Environment.NewLine, _remembered
					.Where(r => (r.Value.Minutes > 0) || (r.Value.Until > stale))
					.Select(static r => $"{r.Key}|{r.Value.Until.Ticks}|{r.Value.Minutes}")));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// Best effort: it only matters if the process restarts inside the wait.
			}
		}
	}

	private static Backoff NewBackoff(string host) {
		(DateTime until, int minutes) = Remembered(host);

		return new Backoff { Until = until, Minutes = minutes };
	}

	/// <summary>
	/// Steam answered 429 for <paramref name="host"/>. That limit is per IP, so it is no good backing off only the
	/// account that happened to ask - every account has to come off the host together, and every further request
	/// while it is hot only extends the ban. The wait doubles on each repeat and resets on the next answer.
	/// </summary>
	/// <returns>How long the host is now closed for, or <see cref="TimeSpan.Zero"/> if somebody just closed it.</returns>
	public static TimeSpan NoteRateLimited(string host) {
		DateTime now = DateTime.UtcNow;
		Backoff state = WebBackoff.GetOrAdd(host, NewBackoff);

		lock (state) {
			// Three accounts in flight will each collect their own 429 off the same limit. Only the first one
			// lengthens the wait; the rest are the same event arriving three times.
			if (state.Until > now) {
				return TimeSpan.Zero;
			}

			state.Minutes = state.Minutes <= 0 ? BackoffMinMinutes : Math.Min(BackoffMaxMinutes, state.Minutes * 2);
			state.Until = now.AddMinutes(state.Minutes);
			Remember(host, state.Until, state.Minutes);

			return TimeSpan.FromMinutes(state.Minutes);
		}
	}

	/// <summary>A request to <paramref name="host"/> came back fine, so whatever tripped the limit is over.</summary>
	public static void NoteWebOk(string host) {
		if (!WebBackoff.TryGetValue(host, out Backoff? state)) {
			return;
		}

		lock (state) {
			if ((state.Until <= DateTime.UtcNow) && (state.Minutes != 0)) {
				state.Minutes = 0;
				Remember(host, state.Until, 0);
			}
		}
	}

	/// <summary>How much longer <paramref name="host"/> is closed for. Zero when it is open.</summary>
	public static TimeSpan RateLimitedFor(string host) {
		Backoff state = WebBackoff.GetOrAdd(host, NewBackoff);

		lock (state) {
			TimeSpan left = state.Until - DateTime.UtcNow;

			return left > TimeSpan.Zero ? left : TimeSpan.Zero;
		}
	}

	/// <summary>Wait for this caller's turn to log in. The first login goes straight away.</summary>
	public static async Task WaitForLoginSlotAsync(CancellationToken ct = default) {
		await TurnAsync(LoginGate, LoginGap, ct).ConfigureAwait(false);

		// Blocks only while somebody is serving a rate-limit cooldown; otherwise it's a free pass through.
		await LoginCooldownLatch.WaitAsync(ct).ConfigureAwait(false);
		LoginCooldownLatch.Release();
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
			Log.Warn(new Said("Steam is rate-limiting logins - every account waits {0}m", LoginCooldownMinutes));
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

		(SemaphoreSlim one, Gate gate) = Hosts.GetOrAdd(host, static _ => (new SemaphoreSlim(1, 1), new Gate()));

		await one.WaitAsync().ConfigureAwait(false);

		try {
			// Waits out the gap since the last request to this host FINISHED, then goes.
			await TurnAsync(gate, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);

			// Asked again after the wait: a 429 can land while this request queues, and every queued request going
			// out anyway is exactly what keeps the limit alive.
			if (RateLimitedFor(host) > TimeSpan.Zero) {
				return default;
			}

			return await request().ConfigureAwait(false);
		} finally {
			lock (gate) {
				gate.Next = DateTime.UtcNow.AddMilliseconds(WebDelayMs);
			}

			one.Release();
		}
	}
}
