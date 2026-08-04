using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Whether there is a newer release, and where to get it.
///
/// It CHECKS and it TELLS you. It does not download anything, replace anything, or restart anything - a program
/// holding the credentials to somebody's Steam accounts should not be able to swap its own binary out from under
/// them on a schedule, and an update that lands mid-farm is how a session gets lost. The link is one click.
///
/// Off is a setting. Nothing here ever blocks startup: a check that fails is a debug line and nothing else.
/// </summary>
public static class UpdateCheck {
	private const string Releases = "https://api.github.com/repos/VisaHolder/nocatfarm/releases/latest";

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

	/// <summary>The newest version seen, if it is newer than this build. Null when up to date or unchecked.</summary>
	public static string? Available { get; private set; }

	public static string? Url { get; private set; }

	private static DateTime _lastLooked = DateTime.MinValue;

	static UpdateCheck() {
		// GitHub refuses anonymous requests without one.
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	/// <summary>
	/// Look, at most once a day. Safe to call whenever.
	///
	/// <paramref name="force"/> skips both gates, for when somebody has actually asked - the daily timer is
	/// there to keep the background check quiet, not to make "check now" mean "check tomorrow". A release
	/// published five minutes after startup is otherwise invisible for a day, which is exactly when somebody
	/// goes looking for it.
	/// </summary>
	public static async Task LookAsync(CancellationToken ct = default, bool force = false) {
		if (!force && (!Live.Global.CheckForUpdates || (DateTime.UtcNow - _lastLooked < TimeSpan.FromHours(24)))) {
			return;
		}

		_lastLooked = DateTime.UtcNow;

		try {
			string json = await Http.GetStringAsync(Releases, ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(json);

			string tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
			string page = doc.RootElement.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? "" : "";

			if (!IsNewer(tag.TrimStart('v', 'V'), Build.Version)) {
				Available = null;

				return;
			}

			Available = tag;
			Url = page;
			Log.Attention($"nocat.farm {tag} is out - you have {Build.Version}. {page}");
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"couldn't check for updates: {e.Message}");
		}
	}

	/// <summary>Is this release tag newer than what is running? Used by the updater before it downloads.</summary>
	public static bool IsNewerThanThisBuild(string tag) => IsNewer(tag.TrimStart('v', 'V'), Build.Version);

	/// <summary>Compares 1.2.10 against 1.2.9 properly, which a string comparison does not.</summary>
	private static bool IsNewer(string candidate, string current) {
		int[] a = Parts(candidate);
		int[] b = Parts(current);

		for (int i = 0; i < 3; i++) {
			if (a[i] != b[i]) {
				return a[i] > b[i];
			}
		}

		return false;
	}

	private static int[] Parts(string version) {
		int[] parts = [0, 0, 0];
		string[] split = version.Split('.', StringSplitOptions.RemoveEmptyEntries);

		for (int i = 0; (i < 3) && (i < split.Length); i++) {
			int.TryParse(new string([.. split[i].TakeWhile(char.IsDigit)]), out parts[i]);
		}

		return parts;
	}
}
