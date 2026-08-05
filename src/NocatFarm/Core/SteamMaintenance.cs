namespace NocatFarm.Core;

/// <summary>
/// Valve's weekly maintenance window.
/// </summary>
/// <remarks>
/// Steam restarts its infrastructure once a week on a Tuesday evening US time - roughly 23:00 to 02:00 UTC,
/// usually done inside half an hour. Store, community, game servers and the connection managers all go with
/// it, so every account drops at once and nothing can log back in for a while.
///
/// This is NOT used to stop trying. Valve publishes no schedule and the timing drifts, so treating the window
/// as authoritative would mean an account sitting out a perfectly healthy Tuesday evening because a clock said
/// so. All it does is change the RESPONSE: back off further between attempts instead of hammering a server
/// that is deliberately down, and say plainly in the log that this is expected rather than logging a wall of
/// warnings that reads like something is broken.
/// </remarks>
public static class SteamMaintenance {
	/// <summary>Whether now falls in the window where a mass disconnect is the ordinary weekly event.</summary>
	public static bool Likely(DateTime utcNow) {
		// Tuesday 21:00 UTC through Wednesday 02:00 UTC.
		//
		// Published figures disagree - one source says 23:00-02:00 UTC, another 22:00-01:00 - so this covers
		// both with an hour of margin. The margin is not arbitrary: a real burst of disconnects on this very
		// machine landed at 22:06-22:10 UTC on a Tuesday, which the narrower 23:00 start would have missed
		// entirely and reported as a fault. Being early costs a slower retry on a quiet Tuesday evening; being
		// late costs a wall of warnings every single week.
		if ((utcNow.DayOfWeek == DayOfWeek.Tuesday) && (utcNow.Hour >= 21)) {
			return true;
		}

		return (utcNow.DayOfWeek == DayOfWeek.Wednesday) && (utcNow.Hour < 2);
	}

	public static bool LikelyNow => Likely(DateTime.UtcNow);

	/// <summary>
	/// How long to wait before trying again, given how many attempts have already failed.
	///
	/// During maintenance the connection managers are not merely refusing - they are gone, so a ten-second
	/// retry loop achieves nothing except a hundred log lines. Outside the window the ordinary backoff stands,
	/// because an ordinary disconnect usually clears in seconds.
	/// </summary>
	public static TimeSpan Backoff(int attempt, TimeSpan ordinary) {
		if (!LikelyNow) {
			return ordinary;
		}

		// 2, 4, 8 minutes, capped. Long enough to stop hammering, short enough to be back within a minute or
		// two of Steam returning.
		int minutes = Math.Min(8, 2 << Math.Clamp(attempt - 1, 0, 2));

		return TimeSpan.FromMinutes(minutes);
	}

	/// <summary>The line to log when an account drops, so a weekly restart does not read like a fault.</summary>
	public static Said Explain(TimeSpan wait) =>
		LikelyNow
			? new Said("disconnected - this is Steam's weekly maintenance, back in ~{0}m", (int) wait.TotalMinutes)
			: new Said("disconnected - reconnecting in ~{0}s", (int) wait.TotalSeconds);
}
