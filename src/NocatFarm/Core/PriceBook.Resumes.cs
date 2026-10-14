using NocatFarm.Core;

namespace NocatFarm;

public static partial class PriceBook {
	/// <summary>
	/// When price lookups go on again, while Steam has asked them to slow down - the market's pause, or a wait on
	/// steamcommunity.com as a whole (which the market keeps to as well). Null when nothing is holding them. Read from what a
	/// restart kept too: a pause written before it still holds, and "about 7 min" was said all through one.
	/// </summary>
	public static DateTime? ResumesAt {
		get {
			LoadCool();
			DateTime now = DateTime.UtcNow, until = _coolUntil;
			TimeSpan shut = Limiters.RateLimitedFor(WebSession.Community.Host);

			if ((shut > TimeSpan.Zero) && (now + shut > until)) {
				until = now + shut;
			}

			return until > now ? until : null;
		}
	}
}
