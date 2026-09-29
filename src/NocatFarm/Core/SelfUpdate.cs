using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Replacing this build with the newest release - when somebody asks, or at night if "Update by itself" says so.
/// </summary>
/// <remarks>
/// <see cref="UpdateCheck"/> looks and tells; this one acts. Deliberately two separate things, because the
/// decision to swap the binary belongs to whoever runs it. Plenty of people never want to update at all - a
/// working setup that farms all night is worth more to them than whatever is in the release notes, and an
/// update that lands unasked mid-session is how a night gets lost. So it happens when the command is typed or
/// the button is pressed - or, only if "Update by itself" is set to install at night, while every account is
/// asleep (see UpdateCheck.AutoInstallIfDue). There is no prompt that updates if you ignore it.
///
/// A new version that won't start is put back: the swap script waits for the new copy to say it came up fine
/// (NF_OK), and if that doesn't happen within three minutes - or it crashes first - the old files go back, the old
/// version starts again, and it says so and skips that version.
///
/// Windows will not let a running process overwrite its own exe, so the swap is done by a small script that
/// outlives us: wait for this PID to go, copy the staged files over the top, start the new one, delete itself.
/// Everything is staged and checked BEFORE anything is touched, so a download that fails or arrives truncated
/// leaves the installation exactly as it was. config/ and logs/ are never in the archive and never copied over.
///
/// Windows only, on purpose. On Linux the app is usually run by something that restarts it - Docker, where the
/// app is the container and a swap script dies with it (and the next pull replaces the files anyway), or
/// systemd, which kills everything the service started the moment it exits. A script that outlived us there
/// would either be killed half way through a copy or start a second copy nobody supervises. Updating by hand
/// (a new image, or the new zip extracted over the folder) is one step and can't half-happen, so off Windows
/// this says how and changes nothing.
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

	/// <summary>Whether this copy can replace itself. Windows only - see the remarks above for why.</summary>
	public static bool Supported => OperatingSystem.IsWindows();

	/// <summary>
	/// How to update where updating itself isn't possible: pull the new image in Docker, or extract the new Linux zip.
	/// <paramref name="tag"/> is the release, to name the exact file to download.
	/// </summary>
	public static Said ByHand(string? tag) {
		if (Platform.InContainer) {
			return new Said("nocat.farm doesn't update itself inside Docker - in the nocatfarm folder run git pull, then docker compose up -d --build. config/ and logs/ are kept");
		}

		return new Said("nocat.farm only updates itself on Windows - stop it, extract {0} from {1} over this folder (config/ and logs/ are kept) and start it again", ReleaseZipName(tag), ReleasesPage);
	}

	/// <summary>The release file for this machine off Windows: nocat.farm-v1.3.9_linux-x64.zip. A file name, not prose.</summary>
	private static string ReleaseZipName(string? tag) =>
		$"nocat.farm-{(string.IsNullOrEmpty(tag) ? "v*" : "v" + tag.TrimStart('v', 'V'))}_{Platform.ReleaseRid}.zip";

	/// <summary>
	/// Is this release asset the zip for this machine?
	///
	/// The Windows zip keeps the plain name it has always had (nocat.farm-v1.3.9.zip); the Linux ones carry their
	/// platform after an underscore (nocat.farm-v1.3.9_linux-x64.zip). The underscore is load-bearing: GitHub lists
	/// a release's files alphabetically, and every copy up to 1.3.8 installs simply the FIRST .zip in that list.
	/// "_" sorts after the "." of ".zip", so the Windows zip stays first and those copies keep updating properly -
	/// with "-linux" it would sort first and every one of them would download the Linux build and refuse it.
	/// </summary>
	internal static bool IsZipForThisMachine(string asset) {
		if (!asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
			return false;
		}

		if (OperatingSystem.IsWindows()) {
			return !asset.Contains("linux", StringComparison.OrdinalIgnoreCase) && !asset.Contains("osx", StringComparison.OrdinalIgnoreCase);
		}

		return asset.EndsWith("_" + Platform.ReleaseRid + ".zip", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>"from|to|MB|seconds|ticks", written just before the restart and read back by the new version.</summary>
	private static string NotePath => Path.Combine(ConfigStore.ConfigDir, "state", "updated.txt");

	/// <summary>Written by the swap script when copying the new files in failed: robocopy's exit code.</summary>
	private static string SwapFailedPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-failed.txt");

	/// <summary>
	/// A failure: in red, with the reason, and handed back for the command reply or the dashboard. Every way an
	/// update can go wrong goes through here, so none of them is a yellow line that looks like a note, or silence.
	/// </summary>
	private static string Fail(Said why) {
		Log.Error(why, topic: Topic.Installs);

		// Said before Telegram and Discord are listening (the first thing at start): kept to send once they are.
		if (!_notifierReady) {
			_heldBack = why;
		}

		LastFailure = why.ToString();

		return LastFailure;
	}

	private static bool _notifierReady;
	private static Said? _heldBack;

	/// <summary>Telegram and Discord are listening now: what an update said before they were goes out.</summary>
	public static void NotifierReady() {
		_notifierReady = true;

		if (_heldBack is { } why) {
			_heldBack = null;
			Log.Publish(Topic.Installs, "nocat.farm", why);
		}
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
			// The swap script leaves robocopy's exit code behind when it couldn't copy the new files in (and then
			// starts this old version back up), so the reason can be said rather than guessed - or "crashed <tag>"
			// when the new version was put back because it didn't start.
			string? swapCode = null;

			if (File.Exists(SwapFailedPath)) {
				swapCode = File.ReadAllText(SwapFailedPath).Trim();
				File.Delete(SwapFailedPath);
			}

			if (swapCode?.StartsWith("crashed", StringComparison.Ordinal) == true) {
				string bad = swapCode[7..].Trim();
				TryDelete(NotePath);
				TryDelete(NotesPath);

				// Skipped, or "Update by itself" would install the same broken version again the next night.
				if (bad.Length > 0) {
					UpdateCheck.Skipped = bad;
				}

				Fail(new Said("update undone: {0} didn't start properly, so {1} was put back - nothing else was changed. {0} is skipped now; 'update accept' tries it again", bad, Build.Version));

				return;
			}

			// Started by the swap script to be tried out: say "ok" once it has run half a minute, whatever the note says.
			// Tied to the note, a note that couldn't be written would have had a good update put back.
			VerifyIfAsked();

			if (!File.Exists(NotePath)) {
				return;
			}

			string[] p = File.ReadAllText(NotePath).Split('|');
			File.Delete(NotePath);

			// A note from long ago is some other start's business (an update that was interrupted, say).
			if ((p.Length < 5) || !long.TryParse(p[4], out long ticks) || (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(1))) {
				return;
			}

			if (p[1] == Build.Version) {
				Log.Good(new Said("update: done - now on {1} (was {0}) · downloaded {2}MB in {3}s", p[0], p[1], p[2], p[3]));

				string notes = "";

				try {
					notes = File.Exists(NotesPath) ? File.ReadAllText(NotesPath).Trim() : "";
				} catch {
					// the message goes without them
				}

				TryDelete(NotesPath);

				if (notes.Length > 0) {
					// One line in the log: "what's new in 1.4.6: Discord commands · Update by itself".
					Log.Info(new Said("what's new in {0}: {1}", p[1], string.Join(" · ", notes.Split('\n').Select(static l => l.TrimStart('-', ' ')).Where(static l => l.Length > 0))));
				}

				ConfirmWhenSettled(p[0], p[1], notes);
			} else if (swapCode != null) {
				UpdateCheck.NoteFailedInstall();
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

	/// <summary>The first lines of the new version's release notes, left by the old version for the new one to say.</summary>
	private static string NotesPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-notes.txt");

	/// <summary>The file the swap script is waiting for, while this start is the new version being tried out.</summary>
	private static string? _okFile;

	private static void TryDelete(string path) {
		try {
			File.Delete(path);
		} catch {
			// gone or not, nothing depends on it
		}
	}

	/// <summary>
	/// The new version tells the swap script it came up fine - after half a minute of running, so a crash on the
	/// way up is caught - and only then is "install complete" sent. If this process dies first, the script puts the
	/// old version back (see SwapScript). Also called on a normal exit, so closing it straight after an update
	/// isn't mistaken for a crash.
	/// </summary>
	private static void VerifyIfAsked() {
		string? ok = Environment.GetEnvironmentVariable("NF_OK");

		if (string.IsNullOrEmpty(ok) || !Directory.Exists(Path.GetDirectoryName(ok))) {
			return;
		}

		_okFile = ok;

		_ = Task.Run(async () => {
			await Task.Delay(Settle).ConfigureAwait(false);
			ConfirmStarted();
		});
	}

	/// <summary>How long the new version runs before it says it's fine.</summary>
	private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

	/// <summary>"Install complete", once the new version has run long enough to count as started.</summary>
	private static void ConfirmWhenSettled(string from, string to, string notes) {
		_ = Task.Run(async () => {
			await Task.Delay(Settle).ConfigureAwait(false);

			Said done = new Said("Install complete - now on {0} (was {1}).", to, from);
			Log.Publish(Topic.Installs, "nocat.farm", notes.Length > 0 ? new Said("{0}\n\n{1}", done, new Said("What's new:\n{0}", notes)) : done);
		});
	}

	/// <summary>Tell the swap script this version is fine. Once; nothing to do when this start wasn't an update.</summary>
	public static void ConfirmStarted() {
		_confirmed = true;
		Mark("ok");
	}

	private static volatile bool _confirmed;

	/// <summary>
	/// This start is a new version being tried out, and the swap script may still put the old one back. Until it has
	/// said it's fine, secrets the old version can't read (the Family View PIN, maFiles, the key queue) stay in the
	/// form they were in - so "nothing else was changed" is true if it goes back.
	/// </summary>
	public static bool OnTrial => !_confirmed && (Environment.GetEnvironmentVariable("NF_OK") is { Length: > 0 } ok)
		&& Directory.Exists(Path.GetDirectoryName(ok));

	/// <summary>A crash while the new version is being tried out: the swap script puts the old one back straight away.</summary>
	public static void ReportCrashed() => Mark("crashed");

	private static void Mark(string what) {
		string? ok = Interlocked.Exchange(ref _okFile, null);

		if (ok == null) {
			return;
		}

		try {
			File.WriteAllText(ok, what);
		} catch {
			// the script's own time limit covers it
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
		// Long enough to read the line and see it happen, even with nothing to sign out.
		int secs = online.Count == 0 ? 10 : Math.Clamp(12 + (online.Count * 7) + Rng.Next(0, 10), 15, 120);

		Log.Good(online.Count > 0
			? new Said("update: {0} is downloaded and ready - updating in {1}s, signing the accounts out one at a time first", tag, secs)
			: new Said("update: {0} is downloaded and ready - updating in {1}s", tag, secs));

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
				Log.Good(new Said("update: signing out {0} ({1} of {2}) - updating in {3}s", b.Name, next, order.Length, left));
				stopping.Add(b.StopAsync(graceful: b.Cfg.LegitMode));
			}

			// The countdown in the log too, not only on the dashboard: every 10s, then the last 5.
			if ((left < secs) && (((left % 10) == 0) || (left <= 5))) {
				Log.Info(new Said("update: updating in {0}s", left));
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
	/// <param name="byItself">"Update by itself" started it, at night - the message says so.</param>
	public static async Task<string?> ApplyAsync(CancellationToken ct, bool byItself = false) {
		if (Busy) {
			return "an update is already downloading - give it a minute";
		}

		// Not a failure - there is simply another way to do it here, and nothing was attempted.
		if (!Supported) {
			Said how = ByHand(UpdateCheck.Available);
			Log.Info(how);

			return how.ToString();
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
			string body = root.TryGetProperty("body", out JsonElement nb) ? nb.GetString() ?? "" : "";

			if (tag.Length == 0) {
				return Fail(new Said("update failed: GitHub didn't say which release is newest - try again in a few minutes. Nothing was changed"));
			}

			if (!UpdateCheck.IsNewerThanThisBuild(tag)) {
				return $"already on the newest release ({Build.Version}) - nothing to do";
			}

			// The zip for this machine - not the source tarballs GitHub adds to every release by itself, and not
			// the Linux builds that sit beside the Windows one.
			string? url = null;
			long size = 0;

			if (root.TryGetProperty("assets", out JsonElement assets)) {
				foreach (JsonElement asset in assets.EnumerateArray()) {
					string assetName = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";

					if (IsZipForThisMachine(assetName)) {
						url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
						size = asset.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0;

						break;
					}
				}
			}

			if (url == null) {
				return Fail(new Said("update failed: {0} has no download attached yet - try again later, or get it from {1}. Nothing was changed", tag, ReleasesPage));
			}

			// One folder per install: two copies updating at the same minute (both on "Update by itself") must never
			// share a swap script, a safety copy or the file the new version answers in.
			string work = Path.Combine(Path.GetTempPath(), "nocatfarm-update-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
				System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant())))[..10]);

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

				// A connection that dies without closing sends nothing, ever - and the read waited for ever with it,
				// the update stuck "busy" until a restart. Each read gets a minute; the clock restarts on every one.
				using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(ct);

				while ((read = await ReadWithin(from, buffer, stall).ConfigureAwait(false)) > 0) {
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
			} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
				return Fail(new Said("update failed: the download stalled - nothing arrived for a minute or more. Check the connection and try again. Nothing was changed"));
			} catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested) {
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
				AtomicFile.Write(NotesPath, UpdateCheck.Highlights(body));
			} catch {
				// only the announcement is lost
			}

			string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
			string script = Path.Combine(work, "swap.cmd");
			string backup = Path.Combine(work, "backup");

			// The safety copy: only the files the update is about to replace. Copying the whole install folder
			// meant, for somebody who unpacked the zip into Downloads, copying all of Downloads. Done here rather
			// than in the script, so a copy that can't be made stops the update while nothing has changed yet.
			//
			// And the list of files the new version ADDS, so putting the old version back can take them away again: left
			// behind, a new release's files can break the old one (an extra runtime file makes it look for the wrong .NET).
			try {
				List<string> added = [];

				foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)) {
					string rel = Path.GetRelativePath(payload, file);
					string current = Path.Combine(here, rel);

					if (File.Exists(current)) {
						Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(backup, rel))!);
						File.Copy(current, Path.Combine(backup, rel), true);
					} else {
						added.Add(rel);
					}
				}

				// Plain ASCII for cmd - the release's own file names, which are ASCII.
				File.WriteAllLines(Path.Combine(work, "added.txt"), added.Where(static r => r.All(static c => c < 128) && !r.Contains('"')));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return Fail(new Said("update failed: couldn't make a safety copy of the current version ({0}). Nothing was changed", e.Message));
			}

			await File.WriteAllTextAsync(script, SwapScript(Environment.ProcessId), ct).ConfigureAwait(false);

			Log.Publish(Topic.Installs, "nocat.farm", byItself
				? new Said("Downloaded {0} ({1}MB) - installing it now, by itself. The accounts sign out one at a time; back in about a minute.", tag, got / 1048576)
				: new Said("Downloaded {0} ({1}MB) - installing it now. The accounts sign out one at a time; back in about a minute.", tag, got / 1048576));

			await SignOutOneByOneAsync(tag, ct).ConfigureAwait(false);

			Progress = "restarting into " + tag;
			Log.Good(new Said("update: all accounts signed out - restarting into {0} now, back in a few seconds", tag));

			// Detached, and in its own window-less shell, so killing this process doesn't take it with us.
			//
			// The folders and the way it was started go in as environment variables, not written into the script.
			// cmd.exe reads a script in the old OEM code page, so a user folder like "José" came out as garbage and
			// the copy and the restart both missed; a "%" in a path was worse. A variable arrives exactly as it is.
			ProcessStartInfo swap = new() {
				FileName = "cmd.exe",
				Arguments = $"/c \"{script}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetTempPath()
			};

			swap.Environment["NF_HERE"] = here;
			swap.Environment["NF_WORK"] = work;
			swap.Environment["NF_STAGED"] = payload;
			swap.Environment["NF_BACKUP"] = backup;
			swap.Environment["NF_FAIL"] = SwapFailedPath;
			swap.Environment["NF_ARGS"] = RelaunchArgs();
			swap.Environment["NF_OK"] = Path.Combine(work, "started.txt");
			swap.Environment["NF_TAG"] = tag.TrimStart('v', 'V');
			Process.Start(swap);

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
	/// <remarks>
	/// Every path comes from an environment variable set on the process (NF_HERE, NF_WORK, NF_STAGED, NF_BACKUP,
	/// NF_FAIL), and NF_ARGS carries the command line nocat.farm was started with, so every restart - the new version
	/// or the old one put back - comes up with the same --path, --no-gui and so on. The script itself is plain ASCII.
	/// The safety copy is made by the app before this runs (see ApplyAsync).
	///
	/// After starting the new version it waits up to three minutes for it to write "ok" to NF_OK (it does, half a
	/// minute after starting - see ConfirmWhenSettled). "crashed" there, or nothing by then, and the new copy is
	/// stopped, the safety copy goes back, the files the new version added are deleted (added.txt), "crashed &lt;tag&gt;"
	/// is left in NF_FAIL, and the old version starts.
	/// NF_OK lives in the work folder, which is removed once it's all over - so a later crash has nowhere to write.
	/// </remarks>
	private static string SwapScript(int pid) =>
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
		robocopy "%NF_STAGED%" "%NF_HERE%" /E /R:3 /W:2 /NFL /NDL /NJH /NJS >nul
		set rc=%errorlevel%
		rem This window is hidden, so a "press a key" here waited for ever and nocat.farm never came back. On a
		rem failed copy: put every file back, leave the reason for the app to say, and start it again as it was.
		if %rc% GEQ 8 (
			robocopy "%NF_BACKUP%" "%NF_HERE%" /E /R:3 /W:2 /NFL /NDL /NJH /NJS >nul
			if exist "%NF_WORK%\added.txt" for /f "usebackq delims=" %%f in ("%NF_WORK%\added.txt") do del /f /q "%NF_HERE%\%%f" >nul 2>&1
			echo %rc%>"%NF_FAIL%"
			start "" /D "%NF_HERE%" "%NF_HERE%\nocatFarm.exe" %NF_ARGS%
			exit /b 1
		)
		echo Starting the new version...
		start "" /D "%NF_HERE%" "%NF_HERE%\nocatFarm.exe" %NF_ARGS%
		rem Wait for the new version to say it came up fine: "ok" within three minutes, or it goes back.
		set /a waited=0
		:verify
		if exist "%NF_OK%" goto verified
		if %waited% GEQ 180 goto undo
		rem ping, not timeout: timeout refuses to run without a console to read keys from, and the wait would be zero.
		ping -n 3 127.0.0.1 >nul
		set /a waited+=2
		goto verify
		:verified
		findstr /b "crashed" "%NF_OK%" >nul && goto undo
		goto finish
		:undo
		echo The new version didn't start properly - putting the old one back...
		powershell -NoProfile -Command "Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $env:NF_HERE 'nocatFarm.exe') | Stop-Process -Force" >nul 2>&1
		ping -n 4 127.0.0.1 >nul
		robocopy "%NF_BACKUP%" "%NF_HERE%" /E /R:5 /W:2 /NFL /NDL /NJH /NJS >nul
		if exist "%NF_WORK%\added.txt" for /f "usebackq delims=" %%f in ("%NF_WORK%\added.txt") do del /f /q "%NF_HERE%\%%f" >nul 2>&1
		echo crashed %NF_TAG%>"%NF_FAIL%"
		start "" /D "%NF_HERE%" "%NF_HERE%\nocatFarm.exe" %NF_ARGS%
		:finish
		rem Remove the staging folder, then this script, from a directory we are not standing in.
		cd /d "%TEMP%"
		rmdir /s /q "%NF_WORK%" >nul 2>&1
		""";

	/// <summary>One read of the download, given a minute. The clock is restarted every call, so a slow line is fine - only silence isn't.</summary>
	private static ValueTask<int> ReadWithin(Stream from, byte[] buffer, CancellationTokenSource stall) {
		stall.CancelAfter(TimeSpan.FromSeconds(60));

		return from.ReadAsync(buffer, stall.Token);
	}

	/// <summary>
	/// The arguments this run was started with, quoted for cmd, to hand to the restarted copy. Every start line used
	/// to run a bare nocatFarm.exe, so an install started with --path, --no-gui or --minimized came back up as a
	/// different setup - on the wrong config folder, or with a window on a machine meant to run headless.
	/// </summary>
	internal static string RelaunchArgs() => string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(QuoteArg));

	/// <summary>One argument, quoted when it has to be. A trailing backslash is doubled so it can't escape the closing quote.</summary>
	internal static string QuoteArg(string arg) {
		if ((arg.Length > 0) && (arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')']) < 0)) {
			return arg;
		}

		string inner = arg.Replace("\"", "\\\"");

		return "\"" + (inner.EndsWith('\\') ? inner + "\\" : inner) + "\"";
	}
}
