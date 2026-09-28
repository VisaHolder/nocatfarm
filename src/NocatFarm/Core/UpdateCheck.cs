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

	/// <summary>'update ignore' was typed: no more reminders until the next launch.</summary>
	public static bool Ignored { get; set; }

	private static DateTime _remindedAt = DateTime.MinValue;

	/// <summary>
	/// Say it again, once an hour, while a newer release is out - unless 'update ignore' was typed this launch or the
	/// reminders are switched off. A notice said once at three in the morning is a notice nobody saw.
	/// </summary>
	public static void RemindIfDue() {
		if ((Available == null) || Ignored || !Live.Global.CheckForUpdates || !Live.Global.UpdateReminders
			|| (DateTime.UtcNow - _remindedAt < TimeSpan.FromHours(1))) {
			return;
		}

		_remindedAt = DateTime.UtcNow;
		Log.Attention(new Said("reminder: nocat.farm {0} is out - you have {1}. 'update accept' installs it and restarts; 'update ignore' stops these reminders until the next launch", Available, Build.Version), topic: Topic.Updates);
	}

	static UpdateCheck() {
		// GitHub refuses anonymous requests without one.
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	/// <summary>
	/// Look, at most every six hours. Safe to call whenever.
	///
	/// <paramref name="force"/> skips both gates, for when somebody has actually asked - the daily timer is
	/// there to keep the background check quiet, not to make "check now" mean "check tomorrow". A release
	/// published five minutes after startup is otherwise invisible for a day, which is exactly when somebody
	/// goes looking for it.
	/// </summary>
	public static async Task LookAsync(CancellationToken ct = default, bool force = false) {
		if (!force && (!Live.Global.CheckForUpdates || (DateTime.UtcNow - _lastLooked < TimeSpan.FromHours(6)))) {
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

			// Said once when it's first seen; after that the hourly reminder carries it.
			if (Available != tag) {
				Log.Attention(new Said("nocat.farm {0} is out - you have {1}. {2}  -  'update accept' installs it and restarts; 'update ignore' stops the hourly reminders until the next launch", tag, Build.Version, page), topic: Topic.Updates);
				_remindedAt = DateTime.UtcNow;
			}

			Available = tag;
			Url = page;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't check for updates: {0}", e.Message));
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
