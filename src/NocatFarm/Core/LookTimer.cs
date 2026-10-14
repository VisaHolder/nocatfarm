namespace NocatFarm.Core;

/// <summary>
/// When a "look every (hours)" job looks next - spare items, booster packs, Steam Points. The next look is worked out
/// from the setting each time it's asked, not fixed when the last look ended: changing "Look every" moved nothing before,
/// and a look planned 12 hours out stayed 12 hours out after setting 1.
/// </summary>
/// <param name="hours">The setting, read every time.</param>
/// <param name="lo">The shortest share of it a wait is (0.75 = three quarters of the hours).</param>
/// <param name="hi">The longest share.</param>
public sealed class LookTimer(Func<int> hours, double lo, double hi) {
	private DateTime? _last;
	private double _share = 1;
	private DateTime? _soon;

	/// <summary>A look just ended: the next is the hours set from now, give or take - picked once, then kept.</summary>
	public void Looked(DateTime utc) {
		_last = utc;
		_share = lo + (Random.Shared.NextDouble() * Math.Max(0, hi - lo));
		_soon = null;
	}

	/// <summary>Look again at <paramref name="utc"/>, whatever the setting says (Steam didn't answer, or there's more to do).</summary>
	public void LookAt(DateTime utc) => _soon = utc;

	/// <summary>When the next look is: never looked yet is now.</summary>
	public DateTime Next {
		get {
			if (_last is not DateTime last) {
				return DateTime.MinValue;
			}

			DateTime planned = last + TimeSpan.FromHours(Math.Max(1, hours()) * _share);

			return _soon is DateTime soon && (soon < planned) ? soon : planned;
		}
	}

	public bool Due(DateTime utc) => utc >= Next;

	/// <summary>How long to sleep before asking again: until the next look, never more than <paramref name="most"/> - so a
	/// changed setting is seen within that.</summary>
	public TimeSpan Wait(DateTime utc, TimeSpan most) {
		TimeSpan left = Next - utc;

		return left <= TimeSpan.Zero ? TimeSpan.Zero : left < most ? left : most;
	}
}
