using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Replacing this build with the newest release, only ever because somebody asked.
/// </summary>
/// <remarks>
/// <see cref="UpdateCheck"/> looks and tells; this one acts. Deliberately two separate things, because the
/// decision to swap the binary belongs to whoever runs it. Plenty of people never want to update at all - a
/// working setup that farms all night is worth more to them than whatever is in the release notes, and an
/// update that lands unasked mid-session is how a night gets lost. So there is no schedule, no prompt that
/// updates if you ignore it, and no setting that turns updating on: it happens when the command is typed or
/// the button is pressed, and never otherwise.
///
/// Windows will not let a running process overwrite its own exe, so the swap is done by a small script that
/// outlives us: wait for this PID to go, copy the staged files over the top, start the new one, delete itself.
/// Everything is staged and checked BEFORE anything is touched, so a download that fails or arrives truncated
/// leaves the installation exactly as it was. config/ and logs/ are never in the archive and never copied over.
/// </remarks>
public static class SelfUpdate {
	private const string Releases = "https://api.github.com/repos/VisaHolder/nocatfarm/releases/latest";

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

	static SelfUpdate() {
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	/// <summary>True while a download is in flight, so a second press doesn't start a second one.</summary>
	public static bool Busy { get; private set; }

	/// <summary>Where it got to, for the dashboard to show.</summary>
	public static string Progress { get; private set; } = "";

	/// <summary>
	/// Download the newest release, stage it, and hand over to the swap script.
	///
	/// Returns a message to print. On success it does not return in any meaningful sense - the app is asked to
	/// shut down and the script takes over - so the caller should treat a null return as "we're going down".
	/// </summary>
	public static async Task<string?> ApplyAsync(CancellationToken ct) {
		if (Busy) {
			return "an update is already downloading - give it a minute";
		}

		if (!OperatingSystem.IsWindows()) {
			return "self-update only knows how to do this on Windows - grab the release by hand";
		}

		Busy = true;

		try {
			Progress = "asking GitHub what's newest";
			Log.Info("update: asking GitHub what's newest");

			string json = await Http.GetStringAsync(Releases, ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";

			if (tag.Length == 0) {
				return "update: GitHub didn't name a release - try again later";
			}

			if (!UpdateCheck.IsNewerThanThisBuild(tag)) {
				return $"already on the newest release ({Build.Version}) - nothing to do";
			}

			// The zip, not the source tarballs GitHub adds to every release by itself.
			string? url = null;
			long size = 0;

			if (root.TryGetProperty("assets", out JsonElement assets)) {
				foreach (JsonElement asset in assets.EnumerateArray()) {
					string assetName = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";

					if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
						url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
						size = asset.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0;

						break;
					}
				}
			}

			if (url == null) {
				return $"update: {tag} has no download attached to it - grab it by hand from the releases page";
			}

			string work = Path.Combine(Path.GetTempPath(), "nocatfarm-update");

			// A half-finished attempt from last time would otherwise be extracted over the new one.
			if (Directory.Exists(work)) {
				Directory.Delete(work, true);
			}

			Directory.CreateDirectory(work);

			string zip = Path.Combine(work, "release.zip");

			Progress = $"downloading {tag}";
			Log.Info(new Said("update: downloading {0} ({1}MB)", tag, size / 1048576));

			await using (Stream from = await Http.GetStreamAsync(url, ct).ConfigureAwait(false))
			await using (FileStream to = File.Create(zip)) {
				await from.CopyToAsync(to, ct).ConfigureAwait(false);
			}

			// A truncated download extracts to a broken install. Check before touching anything.
			long got = new FileInfo(zip).Length;

			if ((size > 0) && (got != size)) {
				return $"update: the download came up short ({got / 1048576}MB of {size / 1048576}MB) - nothing has been changed";
			}

			Progress = "unpacking";
			string staged = Path.Combine(work, "staged");
			ZipFile.ExtractToDirectory(zip, staged, true);

			string exe = Path.Combine(staged, "nocatFarm.exe");

			if (!File.Exists(exe)) {
				return "update: that archive has no nocatFarm.exe in it - nothing has been changed";
			}

			string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
			string script = Path.Combine(work, "swap.cmd");

			await File.WriteAllTextAsync(script, SwapScript(Environment.ProcessId, staged, here, work), ct).ConfigureAwait(false);

			Progress = "restarting into " + tag;
			Log.Attention(new Said("update: {0} is ready - restarting into it now", tag));

			// Detached, and in its own window-less shell, so killing this process doesn't take it with us.
			Process.Start(new ProcessStartInfo {
				FileName = "cmd.exe",
				Arguments = $"/c \"{script}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetTempPath()
			});

			Commands.RequestExit();

			return null;
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Log.Warn(new Said("update failed: {0}: {1} - nothing has been changed", e.GetType().Name, e.Message));

			return $"update failed: {e.Message}. Nothing has been changed - the release page is {Releases}";
		} finally {
			Busy = false;
			Progress = "";
		}
	}

	/// <summary>
	/// The script that does the swap once we are gone.
	///
	/// robocopy /E and NOT /MIR: mirroring would delete everything in the install folder that isn't in the
	/// archive, which is config/, logs/ and the Steam login tokens - i.e. all of it. /E only adds and replaces.
	/// Exit codes below 8 are robocopy's various flavours of success.
	/// </summary>
	private static string SwapScript(int pid, string staged, string here, string work) =>
		$"""
		@echo off
		rem nocat.farm self-update. Written by the app, run once, deletes itself.
		echo Waiting for nocat.farm to close...
		:wait
		tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
		if not errorlevel 1 (
			timeout /t 1 /nobreak >nul
			goto wait
		)
		echo Updating...
		robocopy "{staged}" "{here}" /E /R:3 /W:2 /NFL /NDL /NJH /NJS >nul
		if errorlevel 8 (
			echo Update failed. Your old version is untouched.
			pause
			exit /b 1
		)
		echo Starting the new version...
		start "" "{here}\\nocatFarm.exe"
		rem Remove the staging folder, then this script, from a directory we are not standing in.
		cd /d "%TEMP%"
		rmdir /s /q "{work}" >nul 2>&1
		""";
}
