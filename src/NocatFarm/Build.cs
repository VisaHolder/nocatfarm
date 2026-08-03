using System.Reflection;

namespace NocatFarm;

/// <summary>
/// The app version, read once from the assembly (set by &lt;Version&gt; in the csproj) so the banner, the About
/// command and the dashboard can never drift out of sync with each other or with the release again.
/// </summary>
public static class Build {
	public static readonly string Version =
		Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
