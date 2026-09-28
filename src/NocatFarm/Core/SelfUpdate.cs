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

	/// <summary>Where a person gets it by hand when updating itself can't.</summary>
	private const string ReleasesPage = "https://github.com/VisaHolder/nocatfarm/releases/latest";

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

	static SelfUpdate() {
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	/// <summary>True while a download is in flight, so a second press doesn't start a second one.</summary>
	public static bool Busy { get; private set; }

	/// <summary>"from|to|MB|seconds|ticks", written just before the restart and read back by the new version.</summary>
	private static string NotePath => Path.Combine(ConfigStore.ConfigDir, "state", "updated.txt");

	/// <summary>Written by the swap script when copying the new files in failed: robocopy's exit code.</summary>
	private static string SwapFailedPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-failed.txt");

	/// <summary>
	/// A failure: in red, with the reason, and handed back for the command reply or the dashboard. Every way an
	/// update can go wrong goes through here, so none of them is a yellow line that looks like a note, or silence.
	/// </summary>
	private static string Fail(Said why) {
		Log.Error(why, topic: Topic.Updates);
		LastFailure = why.ToString();

		return LastFailure;
	}

	/// <summary>The last update that failed and why, for a red alert on the dashboard. Cleared by the next try.</summary>
	public static string? LastFailure { get; private set; }

	/// <summary>[██████░░░░] - ten blocks, drawn in the log's monospace font.</summary>
	private static string Bar(int pct) {
		int full = Math.Clamp(pct / 10, 0, 10);

		return "[" + new string('█', full) + new string('░', 10 - full) + "]";
	}

	/// <summary>
	/// First thing after starting: if this start is the end of an update, say so - "updated from 1.3.3 to 1.3.4" -
	/// or that it didn't take. Once, then the note is gone.
	/// </summary>
	public static void AnnounceIfJustUpdated() {
		try {
			if (!File.Exists(NotePath)) {
				return;
			}

			string[] p = File.ReadAllText(NotePath).Split('|');
			File.Delete(NotePath);

			// The swap script leaves robocopy's exit code behind when it couldn't copy the new files in (and then
			// starts this old version back up), so the reason can be said rather than guessed.
			string? swapCode = null;

			if (File.Exists(SwapFailedPath)) {
				swapCode = File.ReadAllText(SwapFailedPath).Trim();
				File.Delete(SwapFailedPath);
			}

			// A note from long ago is some other start's business (an update that was interrupted, say).
			if ((p.Length < 5) || !long.TryParse(p[4], out long ticks) || (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(1))) {
				return;
			}

			if (p[1] == Build.Version) {
				Said done = new("update: done - now on {1} (was {0}) · downloaded {2}MB in {3}s", p[0], p[1], p[2], p[3]);
				Log.Good(done);
				Log.Publish(Topic.Updates, "nocat.farm", done);
			} else if (swapCode != null) {
				// The swap put every file back the way it was before starting this version again, so "nothing was
				// changed" is true - see SwapScript.
				Fail(swapCode.StartsWith("backup", StringComparison.Ordinal)
					? new Said("update failed: couldn't make a safety copy of the current files first (copy error {0}) - is the disk full? Still on {1}, nothing was changed", swapCode[6..].Trim(), Build.Version)
					// robocopy adds flags together: 8 and up with 16 unset means some files failed (in use, access
					// denied); 16 and up means it couldn't work in the folder at all. 11 = 8 + extra files + copied.
					: int.TryParse(swapCode, out int rc) && (rc is >= 8 and < 16)
						? new Said("update failed: the new files couldn't be copied in - some were in use (another copy of nocat.farm, or an antivirus scan?). Still on {0}, nothing was changed - try 'update accept' again", Build.Version)
						: new Said("update failed: the new files couldn't be copied into this folder (copy error {0}) - is it read-only or out of space? Still on {1}, nothing was changed", swapCode, Build.Version));
			} else {
				Fail(new Said("update failed: {0} didn't start after the download - still on {1}. Try 'update accept' again, or get it from the releases page", p[1], Build.Version));
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the update note: {0}", e.Message));
		}
	}

	/// <summary>The accounts, so an update can sign them out one at a time before restarting. Set at startup.</summary>
	public static Func<IEnumerable<Bot>>? Fleet { get; set; }

	/// <summary>
	/// Before the restart: every signed-in account logs out at its own random moment, a few seconds apart and in a
	/// random order, over a countdown - not all in the same second. Several accounts dropping off Steam at once from
	/// one PC is a pattern of its own, and human mode exists to avoid exactly that. Human-mode accounts finish up
	/// the way they do when stopped by hand.
	/// </summary>
	private static async Task SignOutOneByOneAsync(string tag, CancellationToken ct) {
		List<Bot> online = [.. (Fleet?.Invoke() ?? []).Where(static b => b.State is not (BotState.Stopped or BotState.Failed))];
		int secs = online.Count == 0 ? 3 : Math.Clamp(12 + (online.Count * 7) + Rng.Next(0, 10), 15, 120);

		Log.Good(online.Count > 0
			? new Said("update: {0} downloaded - restarting in {1}s, signing the accounts out one at a time first", tag, secs)
			: new Said("update: {0} downloaded - restarting in {1}s", tag, secs));

		// Random moments, at least 3s apart, all done 3s before the restart.
		List<int> at = [];
		foreach (Bot _ in online) {
			at.Add(Rng.Next(2, Math.Max(3, secs - 3)));
		}

		at.Sort();

		for (int i = 1; i < at.Count; i++) {
			at[i] = Math.Max(at[i], at[i - 1] + 3);
		}

		Bot[] order = [.. online.OrderBy(static _ => Rng.Next(0, 1_000_000))];
		List<Task> stopping = [];
		DateTime start = DateTime.UtcNow;
		int next = 0;

		for (int left = secs; left > 0; left--) {
			Progress = $"restarting in {left}s";

			while ((next < order.Length) && ((DateTime.UtcNow - start).TotalSeconds >= at[next])) {
				Bot b = order[next++];
				stopping.Add(b.StopAsync(graceful: b.Cfg.LegitMode));
			}

			await Task.Delay(1000, ct).ConfigureAwait(false);
		}

		// Anything the countdown didn't reach (a crowded window), and every graceful finish-up, before going down.
		while (next < order.Length) {
			stopping.Add(order[next++].StopAsync());
		}

		try {
			await Task.WhenAll(stopping).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		} catch (TimeoutException) {
			// the shutdown that follows stops whatever is left
		}
	}

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
			return Fail(new Said("update failed: updating itself only works on Windows - get the new release from {0}", ReleasesPage));
		}

		Busy = true;
		LastFailure = null;

		try {
			Progress = "asking GitHub what's newest";
			Log.Good("update: asking GitHub what's newest");

			string json;

			try {
				json = await Http.GetStringAsync(Releases, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
				return Fail(new Said("update failed: couldn't reach GitHub ({0}) - check the internet connection and try again. Nothing was changed", e.Message));
			}

			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";

			if (tag.Length == 0) {
				return Fail(new Said("update failed: GitHub didn't say which release is newest - try again in a few minutes. Nothing was changed"));
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
				return Fail(new Said("update failed: {0} has no download attached yet - try again later, or get it from {1}. Nothing was changed", tag, ReleasesPage));
			}

			string work = Path.Combine(Path.GetTempPath(), "nocatfarm-update");

			// A half-finished attempt from last time would otherwise be extracted over the new one.
			if (Directory.Exists(work)) {
				Directory.Delete(work, true);
			}

			Directory.CreateDirectory(work);

			string zip = Path.Combine(work, "release.zip");

			Progress = $"downloading {tag}";
			Log.Good(new Said("update: downloading {0} ({1}MB)", tag, size / 1048576));

			// By hand rather than CopyToAsync, so it can say how far along it is. A 50MB download on a slow line
			// takes minutes, and "downloading" followed by silence looked exactly like a hang.
			DateTime started = DateTime.UtcNow;

			try {
			await using (Stream from = await Http.GetStreamAsync(url, ct).ConfigureAwait(false))
			await using (FileStream to = File.Create(zip)) {
				byte[] buffer = new byte[81920];
				long done = 0;
				int lastTenth = 0;
				int read;

				while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
					await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
					done += read;

					if (size <= 0) {
						continue;
					}

					int pct = (int) (done * 100 / size);
					double secs = (DateTime.UtcNow - started).TotalSeconds;
					// Time left only once there's a minute or more of it - "about 1m left" a second before the end is noise.
					double leftSecs = (secs > 2) && (done > 0) ? (size - done) * secs / done : 0;
					int leftMin = leftSecs >= 60 ? (int) Math.Round(leftSecs / 60) : 0;
					Progress = $"downloading {tag} - {pct}%";

					// Into the log every tenth, so the window and the console show it moving too.
					if ((pct / 10 > lastTenth) && (pct < 100)) {
						lastTenth = pct / 10;
						Log.Good(leftMin > 0
							? new Said("update: {0} {1}% · {2} of {3}MB · about {4}m left", Bar(pct), pct, done / 1048576, size / 1048576, leftMin)
							: new Said("update: {0} {1}% · {2} of {3}MB", Bar(pct), pct, done / 1048576, size / 1048576));
					}
				}
			}
			} catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested) {
				return Fail(new Said("update failed: the download broke off ({0}) - check the connection and try again. Nothing was changed", e.Message));
			}

			// A truncated download extracts to a broken install. Check before touching anything.
			long got = new FileInfo(zip).Length;

			if ((size > 0) && (got != size)) {
				return Fail(new Said("update failed: the download stopped at {0} of {1}MB - the connection probably dropped. Try again. Nothing was changed", got / 1048576, size / 1048576));
			}

			Log.Good(new Said("update: {0} 100% · {1}MB downloaded - unpacking", Bar(100), got / 1048576));
			Progress = "unpacking";
			string staged = Path.Combine(work, "staged");

			try {
				ZipFile.ExtractToDirectory(zip, staged, true);
			} catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
				return Fail(new Said("update failed: couldn't unpack the download ({0}) - is the disk full? Nothing was changed", e.Message));
			}

			// Releases up to 1.2.6 put everything inside one nocat.farm/ folder; later ones are flat. Take either:
			// looking only at the top level refused every foldered release outright, so the update button could
			// never install one.
			string payload = staged;

			if (!File.Exists(Path.Combine(payload, "nocatFarm.exe")) && (Directory.GetFiles(staged).Length == 0)
				&& (Directory.GetDirectories(staged) is [string only]) && File.Exists(Path.Combine(only, "nocatFarm.exe"))) {
				payload = only;
			}

			string exe = Path.Combine(payload, "nocatFarm.exe");

			if (!File.Exists(exe)) {
				return Fail(new Said("update failed: the download doesn't contain nocatFarm.exe - get it from {0}. Nothing was changed", ReleasesPage));
			}

			// A note for the version that comes back up, so its first line can say what just happened. The window
			// that showed the download closes a moment later and the new one starts empty - on a quick download
			// the whole thing was over before anyone saw it, and nothing afterwards said an update had happened.
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(NotePath)!);
				AtomicFile.Write(NotePath, string.Join('|', Build.Version, tag.TrimStart('v', 'V'), got / 1048576,
					(int) Math.Max(1, (DateTime.UtcNow - started).TotalSeconds), DateTime.UtcNow.Ticks));
			} catch {
				// only the announcement is lost
			}

			string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
			string script = Path.Combine(work, "swap.cmd");

			await File.WriteAllTextAsync(script, SwapScript(Environment.ProcessId, payload, here, work, SwapFailedPath), ct).ConfigureAwait(false);

			await SignOutOneByOneAsync(tag, ct).ConfigureAwait(false);

			Progress = "restarting into " + tag;
			Log.Good(new Said("update: all accounts signed out - restarting into {0} now, back in a few seconds", tag));

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
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			return Fail(new Said("update failed: {0} - nothing was changed. The release page is {1}", e.Message, ReleasesPage));
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
	///
	/// A copy that fails part-way leaves a mix of old and new files - a broken install that might even believe it
	/// updated. So the program files (not config, logs or plugins) are copied aside first, and put back if the new
	/// ones don't all go in. Either way nocat.farm is started again: the window running this is hidden, so the only
	/// place a failure can be SEEN is the app itself, whose first line then says, in red, what went wrong.
	/// </summary>
	private static string SwapScript(int pid, string staged, string here, string work, string failFile) =>
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
		echo Making a safety copy...
		robocopy "{here}" "{work}\backup" /E /XD "{here}\config" "{here}\logs" "{here}\plugins" /R:1 /W:1 /NFL /NDL /NJH /NJS >nul
		set brc=%errorlevel%
		if %brc% GEQ 8 (
			echo backup %brc%>"{failFile}"
			start "" /D "{here}" "{here}\nocatFarm.exe"
			exit /b 1
		)
		echo Updating...
		robocopy "{staged}" "{here}" /E /R:3 /W:2 /NFL /NDL /NJH /NJS >nul
		set rc=%errorlevel%
		rem This window is hidden, so a "press a key" here waited for ever and nocat.farm never came back. On a
		rem failed copy: put every file back, leave the reason for the app to say, and start it again as it was.
		if %rc% GEQ 8 (
			robocopy "{work}\backup" "{here}" /E /R:3 /W:2 /NFL /NDL /NJH /NJS >nul
			echo %rc%>"{failFile}"
			start "" /D "{here}" "{here}\nocatFarm.exe"
			exit /b 1
		)
		echo Starting the new version...
		start "" /D "{here}" "{here}\\nocatFarm.exe"
		rem Remove the staging folder, then this script, from a directory we are not standing in.
		cd /d "%TEMP%"
		rmdir /s /q "{work}" >nul 2>&1
		""";
}
