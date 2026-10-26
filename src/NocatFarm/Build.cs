using System.Reflection;

namespace NocatFarm;

/// <summary>
/// The app version, read once from the assembly (set by &lt;Version&gt; in the csproj) so the banner, the About
/// command and the dashboard can never drift out of sync with each other or with the release again.
/// </summary>
public static class Build {
	public static readonly string Version =
		Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

	/// <summary>
	/// 1.8.6: when this copy started (UTC), noted first thing (<see cref="MarkStarted"/>). Process.StartTime is made up under
	/// proot - Termux on an Android box: 'status' said "up 0h 8m", then "0h 2m" a minute later.
	/// </summary>
	public static DateTime StartedUtc { get; private set; } = DateTime.UtcNow;

	public static void MarkStarted() => StartedUtc = DateTime.UtcNow;
}
