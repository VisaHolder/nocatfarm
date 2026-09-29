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
/// On Linux and a Mac the same happens with a shell script (see UnixSwapScript): run from a terminal, a desktop or
/// start.command it updates itself exactly like Windows does, safety copy and putting back included. Not in two
/// places, on purpose: Docker, where the app is the container and a swap script dies with it (and the next build
/// replaces the files anyway), and a systemd service, where systemd kills everything the service started the moment
/// it exits. A script that outlived us there would either be killed half way through a copy or start a second copy
/// nobody supervises. Updating by hand (a new image, or the new zip over the folder) is one step and can't
/// half-happen, so there it says how and changes nothing.
/// </remarks>
public static class SelfUpdate {
	private const string Releases = "https://api.github.com/repos/VisaHolder/nocatfarm/releases/latest";

	/// <summary>
	/// Where to ask what's newest. GitHub - unless NOCATFARM_UPDATE_FEED points at a copy on this same machine, which is
	/// how the tests hand it a broken release to see it put the old version back. Nothing but this machine is taken.
	/// </summary>
	internal static string Feed => Environment.GetEnvironmentVariable("NOCATFARM_UPDATE_FEED") is { Length: > 0 } feed
		&& (feed.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) || feed.StartsWith("http://localhost:", StringComparison.Ordinal))
			? feed : Releases;

	/// <summary>The program's own file: nocatFarm.exe on Windows, nocatFarm everywhere else.</summary>
	private static string ExeName => OperatingSystem.IsWindows() ? "nocatFarm.exe" : "nocatFarm";

	/// <summary>Where a person gets it by hand when updating itself can't.</summary>
	private const string ReleasesPage = "https://github.com/VisaHolder/nocatfarm/releases/latest";

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

	static SelfUpdate() {
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	/// <summary>True while a download is in flight, so a second press doesn't start a second one.</summary>
	public static bool Busy => Volatile.Read(ref _busy) != 0;

	// Claimed with one atomic step in ApplyAsync. A plain check-then-set let two installs through together: the timer
	// starts "Update by itself" and a queued 'update accept' in the same tick, each on its own thread, and both read
	// "not busy" before either set it - two downloads into one folder, and two sign-out countdowns.
	private static int _busy;

	/// <summary>
	/// The handover to the swap script and 'update skip' take turns on this. An install decided on a moment before a
	/// skip - the queued one at bedtime, the night's "Update by itself" - went ahead anyway: it had already looked.
	/// </summary>
	private static readonly Lock HandoverGate = new();

	/// <summary>The swap script has been started: from here the new version goes in whatever is typed.</summary>
	private static bool _handedOver;

	/// <summary>
	/// 'update skip': this version never installs by itself. False when it's too late - the swap script already has it.
	/// </summary>
	public static bool Skip(string tag) {
		lock (HandoverGate) {
			if (_handedOver) {
				return false;
			}

			UpdateCheck.Skipped = tag;

			return true;
		}
	}

	/// <summary>
	/// Start the swap - unless 'update skip' got there first. Under the same lock as <see cref="Skip"/>, so a skip either
	/// lands before this and nothing is started, or finds the swap already going and is told it's too late.
	/// </summary>
	internal static bool HandOver(string tag, Action start) {
		lock (HandoverGate) {
			if (UpdateCheck.IsSkipped(tag)) {
				return false;
			}

			start();
			_handedOver = true;

			return true;
		}
	}

	/// <summary>An install that found its version skipped: not a failure, the skip simply won.</summary>
	private static string SkippedStop(string tag) {
		Said why = new Said("update stopped - {0} was skipped; nothing changed", tag);
		Log.Info(why);

		return why.ToString();
	}

	/// <summary>Whether this copy can replace itself: everywhere but Docker and a systemd service - see the remarks above.</summary>
	public static bool Supported => OperatingSystem.IsWindows()
		|| ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !Platform.InContainer && !RunAsService());

	/// <summary>
	/// Started by systemd as a service. systemd gives the service INVOCATION_ID and, with the log going to the journal,
	/// JOURNAL_STREAM naming the journal's socket - which is then what this process's output really is. A terminal
	/// started from a desktop can hand INVOCATION_ID down too, so that alone isn't enough; systemd as the parent is.
	/// </summary>
	internal static bool RunAsService() {
		if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"))) {
			return false;
		}

		try {
			if (Environment.GetEnvironmentVariable("JOURNAL_STREAM") is { Length: > 0 } stream && (stream.Split(':') is [_, string inode])
				&& (new FileInfo("/proc/self/fd/1").LinkTarget is { } output) && (output == $"socket:[{inode}]")) {
				return true;
			}

			string[] stat = File.ReadAllText("/proc/self/stat").Split(' ');
			string parent = stat.Length > 3 ? File.ReadAllText($"/proc/{stat[3]}/comm").Trim() : "";

			return parent == "systemd";
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// Asked whenever Supported is: said once, not every time.
			Log.DebugOnChange("update:service", $"update: couldn't tell whether this runs as a service, so it updates by hand: {Log.Describe(e)}");

			return true;   // can't tell - the safe answer is "update by hand"
		}
	}

	/// <summary>
	/// How to update where updating itself isn't possible: pull the new image in Docker, or extract the new Linux zip.
	/// <paramref name="tag"/> is the release, to name the exact file to download.
	/// </summary>
	public static Said ByHand(string? tag) {
		if (Platform.InContainer) {
			return new Said("nocat.farm doesn't update itself inside Docker - in the nocatfarm folder run git pull, then docker compose up -d --build. config/ and logs/ are kept");
		}

		return new Said("running as a service, nocat.farm doesn't update itself - stop it, extract {0} from {1} over this folder (config/ and logs/ are kept) and start it again", ReleaseZipName(tag), ReleasesPage);
	}

	/// <summary>The release file for this machine off Windows: nocat.farm-v1.3.9_linux-x64.zip. A file name, not prose.</summary>
	private static string ReleaseZipName(string? tag) =>
		$"nocat.farm-{(string.IsNullOrEmpty(tag) ? "v*" : "v" + tag.TrimStart('v', 'V'))}_{Platform.ReleaseRid}.zip";

	/// <summary>
	/// Is this release asset the zip for this machine?
	///
	/// The Windows zip has no platform in its name (nocat.farm-v1.3.9.zip, nocat.farm-v1.4.9-portable.zip); the Linux ones carry their
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

				Fail(new Said("update undone: {0} didn't start, back on {1} - 'update accept' retries", bad, Build.Version));

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
				Log.Good(new Said("updated {0} → {1} · {2}MB in {3}s", p[0], p[1], p[2], p[3]));

				string notes = "";

				try {
					notes = File.Exists(NotesPath) ? File.ReadAllText(NotesPath).Trim() : "";
				} catch (Exception e) {
					// the message goes without them
					Log.Failed("update: reading the new version's release notes", e);
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
					? new Said("update failed: backup copy error {0} - disk full? Nothing changed", swapCode[6..].Trim())
					// robocopy adds flags together: 8 and up with 16 unset means some files failed (in use, access
					// denied); 16 and up means it couldn't work in the folder at all. 11 = 8 + extra files + copied.
					: int.TryParse(swapCode, out int rc) && (rc is >= 8 and < 16)
						? new Said("update failed: files in use (a 2nd copy? antivirus?) - try again")
						: new Said("update failed: copy error {0} (read-only? disk full?) - nothing changed", swapCode));
			} else {
				Fail(new Said("update failed: {0} didn't start - still on {1}, try again", p[1], Build.Version));
			}
		} catch (Exception e) {
			Log.Failed("couldn't read the update note", e);
		}
	}

	/// <summary>The first lines of the new version's release notes, left by the old version for the new one to say.</summary>
	private static string NotesPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-notes.txt");

	/// <summary>The file the swap script is waiting for, while this start is the new version being tried out.</summary>
	private static string? _okFile;

	private static void TryDelete(string path) {
		try {
			File.Delete(path);
		} catch (Exception e) {
			// gone or not, nothing depends on it - but one left behind can be read again by the next start
			if (e is not DirectoryNotFoundException) {
				Log.Failed($"update: deleting {Path.GetFileName(path)}", e);
			}
		}
	}

	/// <summary>
	/// The new version tells the swap script it came up fine - after half a minute of running, so a crash on the
	/// way up is caught - and only then is "install complete" sent. If this process dies first, the script puts the
	/// old version back (see SwapScript). Also called on a normal exit, so closing it straight after an update
	/// isn't mistaken for a crash.
	/// </summary>
	private static void VerifyIfAsked() {
		if (TrialOkFile is not { } ok) {
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
	public static bool OnTrial => !_confirmed && (TrialOkFile != null);

	/// <summary>Left by the old version: the file its swap script waits on (see ApplyAsync).</summary>
	private static string VerifyPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-verify.txt");

	/// <summary>
	/// The file to answer "ok" in when this start is a new version being tried out: handed over in NF_OK, or - when the
	/// new version was started somewhere that starts clean, like a new Terminal window - in the file the old one left.
	/// Only while the script is still there to read it: its folder goes when it's done.
	/// </summary>
	private static string? TrialOkFile {
		get {
			_trialOk ??= FindTrialOkFile() ?? "";

			return _trialOk.Length > 0 ? _trialOk : null;
		}
	}

	private static string? _trialOk;

	private static string? FindTrialOkFile() {
		string? ok = Environment.GetEnvironmentVariable("NF_OK");

		try {
			if (string.IsNullOrEmpty(ok) && File.Exists(VerifyPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(VerifyPath) < TimeSpan.FromMinutes(15))) {
				ok = File.ReadAllText(VerifyPath).Trim();
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// Unread, the new version never answers "ok" and the swap script puts the old one back.
			Log.Failed("update: reading which file to answer the swap script in", e);

			return null;
		}

		return !string.IsNullOrEmpty(ok) && Directory.Exists(Path.GetDirectoryName(ok)) ? ok : null;
	}

	/// <summary>A crash while the new version is being tried out: the swap script puts the old one back straight away.</summary>
	public static void ReportCrashed() => Mark("crashed");

	private static void Mark(string what) {
		string? ok = Interlocked.Exchange(ref _okFile, null);

		if (ok == null) {
			return;
		}

		TryDelete(VerifyPath);

		try {
			File.WriteAllText(ok, what);
		} catch (Exception e) {
			// the script's own time limit covers it - which for "ok" means putting the old version back
			Log.Failed($"update: telling the swap script \"{what}\"", e);
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
	/// <param name="signedOut">Filled with each account as it's signed out, so a failure after this can sign them back in.</param>
	private static async Task SignOutOneByOneAsync(string tag, List<Bot> signedOut, CancellationToken ct) {
		List<Bot> online = [.. (Fleet?.Invoke() ?? []).Where(static b => b.State is not (BotState.Stopped or BotState.Failed))];
		// Long enough to read the line and see it happen, even with nothing to sign out.
		int secs = online.Count == 0 ? 10 : Math.Clamp(12 + (online.Count * 7) + Rng.Next(0, 10), 15, 120);

		Log.Good(online.Count > 0
			? new Said("update: {0} ready - signing accounts out, restart in {1}s", tag, secs)
			: new Said("update: {0} ready - restarting in {1}s", tag, secs));

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
				signedOut.Add(b);
				stopping.Add(b.StopAsync(graceful: b.Cfg.LegitMode));
			}

			// The countdown in the log too, not only on the dashboard: every 10s, and once at 5s. A line a second
			// for the last five was a wall of near-identical lines right under the sign-out ones.
			if ((left < secs) && (((left % 10) == 0) || (left == 5))) {
				Log.Info(new Said("update: updating in {0}s", left));
			}

			await Task.Delay(1000, ct).ConfigureAwait(false);
		}

		// Anything the countdown didn't reach (a crowded window), and every graceful finish-up, before going down.
		while (next < order.Length) {
			signedOut.Add(order[next]);
			stopping.Add(order[next++].StopAsync());
		}

		try {
			await Task.WhenAll(stopping).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		} catch (TimeoutException) {
			// the shutdown that follows stops whatever is left
			Log.Debug($"update: {stopping.Count(static s => !s.IsCompleted)} of {stopping.Count} account(s) hadn't finished signing out after 30s - the shutdown stops them");
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
		// Not a failure - there is simply another way to do it here, and nothing was attempted.
		if (!Supported) {
			Said how = ByHand(UpdateCheck.Available);
			Log.Info(how);

			return how.ToString();
		}

		// Closing counts as busy: after a handover the app takes a few seconds to shut down, and an install started in
		// them (the timer, a click) deleted the folder the swap script was about to copy the new version from.
		if (Commands.ExitRequested || (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)) {
			return "an update is already downloading - give it a minute";
		}

		bool handedOver = false;
		LastFailure = null;

		// Who the update signed out, so a failure after that can sign them back in.
		List<Bot> signedOut = [];

		try {
			// Looked at again now it's this install's turn. The bedtime queue and "Update by itself" check for a skip and
			// then start this on another thread, so an 'update skip' typed in between used to be too late.
			if ((UpdateCheck.Available is { } known) && UpdateCheck.IsSkipped(known)) {
				return SkippedStop(known);
			}

			Progress = "asking GitHub what's newest";
			Log.Good("update: asking GitHub what's newest");

			string json;

			try {
				json = await Http.GetStringAsync(Feed, ct).ConfigureAwait(false);
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
				// Where it asked, for the file: GitHub's refusal (a rate limit's 403) is in the message, status included.
				Log.Debug($"update: asking {UpdateCheck.FeedWhere()} failed: {Log.Describe(e)}");

				return Fail(new Said("update failed: couldn't reach GitHub ({0}) - nothing changed", Log.Scrub(e.Message)));
			}

			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
			string body = root.TryGetProperty("body", out JsonElement nb) ? nb.GetString() ?? "" : "";

			if (tag.Length == 0) {
				return Fail(new Said("update failed: GitHub gave no latest release - try again soon"));
			}

			if (!UpdateCheck.IsNewerThanThisBuild(tag)) {
				return $"already on the newest release ({Build.Version}) - nothing to do";
			}

			if (UpdateCheck.IsSkipped(tag)) {
				return SkippedStop(tag);
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
				return Fail(new Said("update failed: {0} has no download yet - try again later", tag));
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
				DateTime lastSaid = DateTime.UtcNow;   // the first progress line only once it has taken 15 seconds
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

					// Into the log on a slow download only - a quarter at a time, at least 15 seconds apart. Every tenth
					// was eleven lines for a download that takes five seconds, most of a small window. The dashboard shows
					// Progress live either way.
					if ((pct / 25 > lastTenth) && (pct < 100) && (DateTime.UtcNow - lastSaid > TimeSpan.FromSeconds(15))) {
						lastTenth = pct / 25;
						lastSaid = DateTime.UtcNow;
						Log.Good(leftMin > 0
							? new Said("update: {0} {1}% · {2} of {3}MB · about {4}m left", Bar(pct), pct, done / 1048576, size / 1048576, leftMin)
							: new Said("update: {0} {1}% · {2} of {3}MB", Bar(pct), pct, done / 1048576, size / 1048576));
					}
				}
			}
			} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
				return Fail(new Said("update failed: download stalled for a minute - try again"));
			} catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested) {
				// Which address, for the file: a refused download (a 404 for a release still uploading) says its status in the message.
				Log.Debug($"update: downloading {Log.Where(Uri.TryCreate(url, UriKind.Absolute, out Uri? address) ? address : null)} failed: {Log.Describe(e)}");

				return Fail(new Said("update failed: download broke off ({0}) - try again", Log.Scrub(e.Message)));
			}

			// A truncated download extracts to a broken install. Check before touching anything.
			long got = new FileInfo(zip).Length;

			if ((size > 0) && (got != size)) {
				return Fail(new Said("update failed: download stopped at {0} of {1}MB - try again", got / 1048576, size / 1048576));
			}

			Log.Good(new Said("update: {0}MB downloaded - unpacking", got / 1048576));
			Progress = "unpacking";
			string staged = Path.Combine(work, "staged");

			try {
				ZipFile.ExtractToDirectory(zip, staged, true);
			} catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
				return Fail(new Said("update failed: couldn't unpack ({0}) - disk full?", Log.Scrub(e.Message)));
			}

			// Releases up to 1.2.6 put everything inside one nocat.farm/ folder; later ones are flat. Take either:
			// looking only at the top level refused every foldered release outright, so the update button could
			// never install one.
			string payload = staged;

			if (!File.Exists(Path.Combine(payload, ExeName)) && (Directory.GetFiles(staged).Length == 0)
				&& (Directory.GetDirectories(staged) is [string only]) && File.Exists(Path.Combine(only, ExeName))) {
				payload = only;
			}

			string exe = Path.Combine(payload, ExeName);

			if (!File.Exists(exe)) {
				return Fail(new Said("update failed: the download has no {0}", ExeName));
			}

			// The shell script restarts it with the same options, split on spaces: an option with a space in it (a --path
			// to "My Folder") would come back as two, and it would start on the wrong folder. Said now, before anything.
			if (!OperatingSystem.IsWindows() && Environment.GetCommandLineArgs().Skip(1).Any(static a => a.Any(char.IsWhiteSpace))) {
				return Fail(new Said("update failed: it was started with an option that has a space in it - update by hand this time"));
			}

			string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
			string script = Path.Combine(work, OperatingSystem.IsWindows() ? "swap.cmd" : "swap.sh");
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
				return Fail(new Said("update failed: couldn't back up the current version ({0})", Log.Scrub(e.Message)));
			}

			await File.WriteAllTextAsync(script, OperatingSystem.IsWindows() ? SwapScript(Environment.ProcessId) : UnixSwapScript, ct).ConfigureAwait(false);

			// Skipped while it downloaded: stopped here, before a single account is signed out for nothing.
			if (UpdateCheck.IsSkipped(tag)) {
				return SkippedStop(tag);
			}

			Log.Publish(Topic.Installs, "nocat.farm", byItself
				? new Said("Downloaded {0} ({1}MB) - installing it now, by itself. The accounts sign out one at a time; back in about a minute.", tag, got / 1048576)
				: new Said("Downloaded {0} ({1}MB) - installing it now. The accounts sign out one at a time; back in about a minute.", tag, got / 1048576));

			await SignOutOneByOneAsync(tag, signedOut, ct).ConfigureAwait(false);

			// Closed while the accounts were signing out: the update stops there. Carried on, the swap went ahead as
			// the app shut down, installed the new version and started nocat.farm again after it had been closed.
			if (Commands.ExitRequested) {
				return Fail(new Said("update stopped - nocat.farm was closed first; nothing changed"));
			}

			string okFile = Path.Combine(work, "started.txt");

			// Detached, and in its own window-less shell, so killing this process doesn't take it with us.
			//
			// The folders and the way it was started go in as environment variables, not written into the script.
			// cmd.exe reads a script in the old OEM code page, so a user folder like "José" came out as garbage and
			// the copy and the restart both missed; a "%" in a path was worse. A variable arrives exactly as it is.
			ProcessStartInfo swap;

			if (OperatingSystem.IsWindows()) {
				swap = new() {
					FileName = "cmd.exe",
					Arguments = $"/c \"{script}\"",
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetTempPath()
				};
			} else {
				// nohup and in the background, so neither this process ending nor its Terminal window closing takes the
				// script with it. It waits for this PID, like the Windows one.
				swap = new("/bin/sh") { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
				swap.ArgumentList.Add("-c");
				swap.ArgumentList.Add("nohup /bin/sh \"$0\" >/dev/null 2>&1 &");
				swap.ArgumentList.Add(script);
				swap.Environment["NF_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

				// Started from start.command on a Mac: the new version opens the same way, in a Terminal window of its own.
				if (OperatingSystem.IsMacOS() && (Environment.GetEnvironmentVariable("NOCATFARM_STARTER") == "start.command")) {
					swap.Environment["NF_TERMINAL"] = "1";
				}
			}

			swap.Environment["NF_HERE"] = here;
			swap.Environment["NF_WORK"] = work;
			swap.Environment["NF_STAGED"] = payload;
			swap.Environment["NF_BACKUP"] = backup;
			swap.Environment["NF_FAIL"] = SwapFailedPath;
			swap.Environment["NF_ARGS"] = OperatingSystem.IsWindows() ? RelaunchArgs() : string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
			swap.Environment["NF_OK"] = okFile;
			swap.Environment["NF_TAG"] = tag.TrimStart('v', 'V');

			// The last moment a skip can still win: the notes and the swap go under the same lock 'update skip' takes, so it
			// lands either before them and nothing is started, or after and is told it's too late - never in between.
			bool swapped = HandOver(tag, () => {
				// A note for the version that comes back up, so its first line can say what just happened. The window
				// that showed the download closes a moment later and the new one starts empty - on a quick download
				// the whole thing was over before anyone saw it, and nothing afterwards said an update had happened.
				// Written only now the swap is really going ahead: written before the backup and the sign-outs, an update
				// that stopped in either left it behind, and the next start said "update failed" about one never tried.
				try {
					Directory.CreateDirectory(Path.GetDirectoryName(NotePath)!);

					// Where the new version answers "ok", for when it can't be handed over in the environment: on a Mac
					// started from start.command, the new version comes up in a fresh Terminal window, which starts clean.
					AtomicFile.Write(VerifyPath, okFile);
					AtomicFile.Write(NotePath, string.Join('|', Build.Version, tag.TrimStart('v', 'V'), got / 1048576,
						(int) Math.Max(1, (DateTime.UtcNow - started).TotalSeconds), DateTime.UtcNow.Ticks));
					AtomicFile.Write(NotesPath, UpdateCheck.Highlights(body));
				} catch (Exception e) {
					// only the announcement is lost
					Log.Failed("update: writing the note for the new version", e);
				}

				Progress = "restarting into " + tag;
				Log.Good(new Said("update: all signed out - restarting into {0}", tag));

				Process.Start(swap);
			});

			if (!swapped) {
				SignBackIn(signedOut);

				return SkippedStop(tag);
			}

			handedOver = true;   // stays busy from here: the swap script owns the folder until this copy has gone

			Commands.RequestExit();

			return null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// Everything expected has its own catch above: this one is a surprise, so its stack goes in the file.
			Log.StackToFile(e);
			SignBackIn(signedOut);

			return Fail(new Said("update failed: {0} - nothing changed", Log.Scrub(e.Message)));
		} finally {
			if (!handedOver) {
				Volatile.Write(ref _busy, 0);
			}

			Progress = "";
		}
	}

	/// <summary>
	/// The update stopped after the accounts were signed out (the swap couldn't be started, say). "Nothing changed" has to
	/// be true of them too: they used to stay signed out until somebody noticed - all night, for an update by itself.
	/// </summary>
	private static void SignBackIn(List<Bot> signedOut) {
		if ((signedOut.Count == 0) || Commands.ExitRequested) {
			return;
		}

		Log.Info(new Said("update: signing the accounts back in"));

		foreach (Bot b in signedOut.Where(static b => b.Cfg.Enabled)) {
			Background.Run("couldn't start", b.StartAsync, b.Name);
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
			rem The redirect first: "echo 8>file" is read as handle 8, and the file came out empty for codes 8 and 9.
			>"%NF_FAIL%" echo %rc%
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
		rem Gone without a word (it crashed on the way up, before it could say so): no point waiting out the three minutes.
		set /a check=waited %% 10
		if %waited% GEQ 10 if %check%==0 powershell -NoProfile -Command "exit [int](-not (Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $env:NF_HERE 'nocatFarm.exe')))" >nul 2>&1 || goto gone
		goto verify
		:gone
		if exist "%NF_OK%" goto verified
		goto undo
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

	/// <summary>
	/// The swap on Linux and a Mac - the same steps as the Windows script, in sh. Waits for this process (NF_PID), copies
	/// the new files over the top, starts the new version, and waits for its "ok". A copy that fails, "crashed", no answer
	/// in three minutes, or the new version gone without a word after ten seconds: the safety copy goes back, the files
	/// the new version added go, "crashed &lt;tag&gt;" is left in NF_FAIL, and the old version starts again.
	/// Started from start.command on a Mac (NF_TERMINAL), a version comes up in a Terminal window of its own; otherwise
	/// in the background, the dashboard being where it's seen.
	/// </summary>
	private const string UnixSwapScript = """
		#!/bin/sh
		# nocat.farm self-update. Written by the app, run once.
		while kill -0 "$NF_PID" 2>/dev/null; do sleep 1; done

		start_it() {
			if [ -n "$NF_TERMINAL" ] && [ -x "$NF_HERE/start.command" ]; then
				open -a Terminal "$NF_HERE/start.command"
			else
				cd "$NF_HERE" && nohup "$NF_HERE/nocatFarm" $NF_ARGS >/dev/null 2>&1 &
			fi
		}

		running() { pgrep -f "$NF_HERE/nocatFarm" >/dev/null 2>&1; }

		put_back() {
			pkill -f "$NF_HERE/nocatFarm" 2>/dev/null
			sleep 3
			cp -Rf "$NF_BACKUP"/. "$NF_HERE"/
			if [ -f "$NF_WORK/added.txt" ]; then
				while IFS= read -r f; do [ -n "$f" ] && rm -f "$NF_HERE/$f"; done < "$NF_WORK/added.txt"
			fi
			chmod +x "$NF_HERE/nocatFarm" 2>/dev/null
		}

		if ! cp -Rf "$NF_STAGED"/. "$NF_HERE"/; then
			put_back
			echo 8 > "$NF_FAIL"
			start_it
			exit 1
		fi
		chmod +x "$NF_HERE/nocatFarm" "$NF_HERE/start.command" 2>/dev/null

		start_it
		waited=0
		while [ ! -f "$NF_OK" ]; do
			sleep 2
			waited=$((waited + 2))
			if [ "$waited" -ge 180 ]; then break; fi
			if [ "$waited" -ge 10 ] && ! running; then break; fi
		done

		if [ ! -f "$NF_OK" ] || grep -q '^crashed' "$NF_OK"; then
			put_back
			echo "crashed $NF_TAG" > "$NF_FAIL"
			start_it
		fi

		cd /tmp && rm -rf "$NF_WORK"
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
