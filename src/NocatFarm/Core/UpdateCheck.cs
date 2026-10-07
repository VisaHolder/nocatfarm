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
/// <summary>What happens when a new version is out - the UpdateMode setting.</summary>
public static class UpdateModes {
	/// <summary>Says so; the Update button and 'update accept' install it once the accounts are asleep.</summary>
	public const int JustTellMe = 0;

	/// <summary>Says so; the Update button and 'update accept' install it right away. The default.</summary>
	public const int WhenIClick = 1;

	/// <summary>Installs by itself at a quiet time in the night hours.</summary>
	public const int AtNight = 2;
}

public static class UpdateCheck {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

	/// <summary>The newest version seen, if it is newer than this build. Null when up to date or unchecked.</summary>
	public static string? Available { get; private set; }

	public static string? Url { get; private set; }

	/// <summary>
	/// A newer version that has no download for this machine yet: tagged, with the Mac zips (or this CPU's) still to
	/// come. Kept apart from <see cref="Available"/>, which is announced and which 'update accept' installs - here there is
	/// nothing to install yet. Folded into Available as null, 'update' said "you're on the newest release" on a Mac for the
	/// half hour between the tag and its zips, and a queued 'update accept' was tried, failed and forgotten.
	/// Null when there is no such version, or when this copy can't install itself anyway (Docker, a service): there it's
	/// Available, since updating by hand needs no zip made for this machine.
	/// </summary>
	public static string? NoDownloadYet { get; private set; }

	private static DateTime _lastLooked = DateTime.MinValue;

	/// <summary>When the newest release was first seen - "Wait after a release for" counts from here.</summary>
	private static DateTime _seenAt = DateTime.MinValue;

	/// <summary>The first lines of the release notes, for the message that says it's out.</summary>
	private static string _notes = "";

	private static DateTime _autoTriedAt = DateTime.MinValue;

	private static string SkipPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-skip.txt");

	private static string? _skipped;
	private static bool _skipRead;

	/// <summary>
	/// The skipped version's reads and writes, one at a time. Unguarded, the first read (on the timer's thread) could
	/// finish after an 'update skip' on another and put the file's old value back over the one just typed - and two
	/// skips writing the file together could leave the older one in it.
	/// </summary>
	private static readonly Lock SkipGate = new();

	/// <summary>
	/// The version 'update skip' was typed for - or that was put back after it wouldn't start. Nothing is said about
	/// it and it never installs by itself; the next version after it is announced as usual. Kept across restarts.
	/// </summary>
	public static string? Skipped {
		get {
			Exception? failed = null;
			string? skipped;

			lock (SkipGate) {
				if (!_skipRead) {
					_skipRead = true;

					try {
						_skipped = File.Exists(SkipPath) ? File.ReadAllText(SkipPath).Trim() : null;
					} catch (Exception e) {
						failed = e;
						_skipped = null;
					}
				}

				skipped = _skipped;
			}

			// Said outside the lock: a log line can call back into the app.
			if (failed != null) {
				// a skipped version forgotten would be offered (or installed by itself) again
				Log.Failed("update: reading the skipped version", failed);
			}

			return string.IsNullOrEmpty(skipped) ? null : skipped;
		}
		set {
			Exception? failed = null;

			lock (SkipGate) {
				_skipped = value;
				_skipRead = true;

				try {
					Directory.CreateDirectory(Path.GetDirectoryName(SkipPath)!);

					if (string.IsNullOrEmpty(value)) {
						File.Delete(SkipPath);
					} else {
						AtomicFile.Write(SkipPath, value);
					}
				} catch (Exception e) {
					failed = e;
				}
			}

			if (failed != null) {
				// it only lasts until the next launch, then
				Log.Failed("update: saving the skipped version", failed);
			}
		}
	}

	/// <summary>
	/// An install that failed just now, told by the copy that started back up - so "Update by itself" waits its hours
	/// before trying again, instead of starting over the moment the restart forgot it had tried.
	/// </summary>
	internal static void NoteFailedInstall() => _autoTriedAt = DateTime.UtcNow;

	/// <summary>Is the newest version one that was skipped?</summary>
	/// <remarks>Without the "v": GitHub's tag is "v1.4.6", the swap script's is "1.4.6" - compared as they came, a version
	/// that had just been put back didn't count as skipped, and "Update by itself" installed it again minutes later.</remarks>
	internal static bool IsSkipped(string? tag) => (tag != null) && (Skipped is { } s)
		&& string.Equals(s.TrimStart('v', 'V'), tag.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

	private static DateTime _heldLoggedAt = DateTime.MinValue;

	private static DateTime _remindedAt = DateTime.MinValue;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052", Justification = "Held, never read: a timer nothing holds on to is collected and stops firing.")]
	private static Timer? _timer;
	private static int _ticking;

	/// <summary>
	/// A look shortly after starting, then once a minute: each part decides for itself whether it's time -
	/// "Look for updates every", the hourly reminder, and "Update by itself". It used to ride on an account's
	/// background loop, so with no account signed in nothing was ever checked.
	/// </summary>
	public static void Start(BotManager mgr) {
		if (Commands.ExitRequested) {
			return;   // closing already - no timer to leave behind
		}

		Timer timer = new(_ => _ = TickAsync(mgr), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
		Interlocked.Exchange(ref _timer, timer)?.Dispose();

		// Closed while it was being made: Stop may have run before there was a timer to stop.
		if (Commands.ExitRequested) {
			Stop();
		}
	}

	/// <summary>
	/// On the way out: the timer goes, so no look, reminder or install by itself starts while the app closes. A tick
	/// already running finds <see cref="Commands.ExitRequested"/> up and stops at its next step.
	/// </summary>
	public static void Stop() => Interlocked.Exchange(ref _timer, null)?.Dispose();

	private static async Task TickAsync(BotManager mgr) {
		if (Commands.ExitRequested || (Interlocked.Exchange(ref _ticking, 1) == 1)) {
			return;
		}

		try {
			await LookAsync().ConfigureAwait(false);
			RemindIfDue();
			AutoInstallIfDue(mgr);
			QueuedInstallIfDue(mgr);
		} catch (Exception e) {
			// LookAsync catches its own - this is a bug in the rest, and it would come back every minute: once, with its stack.
			if (Log.DebugOnChange("update:tick", $"update: the once-a-minute update tick failed: {Log.Describe(e)}")) {
				Log.StackToFile(e);
			}
		} finally {
			Volatile.Write(ref _ticking, 0);
		}
	}

	/// <summary>
	/// Say it again, once an hour, while a newer release is out - unless 'update ignore' was typed this launch or the
	/// reminders are switched off. A notice said once at three in the morning is a notice nobody saw.
	/// </summary>
	public static void RemindIfDue() {
		if ((Available == null) || IsSkipped(Available) || SelfUpdate.Busy || !Live.Global.CheckForUpdates || !Live.Global.UpdateReminders
			|| (DateTime.UtcNow - _remindedAt < TimeSpan.FromHours(1))) {
			return;
		}

		_remindedAt = DateTime.UtcNow;
		// The log only: an hourly pop-up (or Telegram message) would be spam - the first one already said it there.
		// With "Update by itself" on, it says it's going to - "'update accept' installs it" read as if nothing would happen
		// without you.
		Log.Attention(SelfUpdate.Supported && (Live.Global.UpdateMode == UpdateModes.AtNight)
				? new Said("reminder: nocat.farm {0} is out - it installs by itself at a quiet time ('update accept' does it now)", Available)
			: SelfUpdate.Supported
				? new Said("reminder: nocat.farm {0} is out - 'update accept' installs it", Available)
				: new Said("reminder: nocat.farm {0} is out - 'update' shows how", Available),
			UpdateBrief(Available), UpdateTitle(Available), Topic.Updates, loud: false);
	}

	/// <summary>The pop-up: short, no links, one thing to do.</summary>
	private static Said UpdateTitle(string tag) => new("nocat.farm {0} is ready", tag);

	// Off Windows it can't install itself (see SelfUpdate), so the one thing to do is the manual step. What's new comes
	// first - a version number alone gives nobody a reason to update.
	private static Said UpdateBrief(string tag) {
		Said how = SelfUpdate.Supported
			? Live.Global.UpdateMode == UpdateModes.AtNight
				? new("{0} is out - you have {1}. It installs by itself tonight, or type update accept (on Telegram: /update accept) to install it now.", tag, Build.Version)
				: new("{0} is out - you have {1}. Type update accept in the nocat.farm window, or /update accept here, to install it.", tag, Build.Version)
			// Docker (no image is published - it's built from the source) or a service: the same steps 'update' gives.
			: new("{0} is out - you have {1}.\n{2}", tag, Build.Version, SelfUpdate.ByHand(tag));

		return _notes.Length > 0 ? new Said("{0}\n\n{1}", how, _notes) : how;
	}

	static UpdateCheck() {
		// GitHub refuses anonymous requests without one.
		Http.DefaultRequestHeaders.Add("User-Agent", "nocat.farm/" + Build.Version);
	}

	/// <summary>
	/// Look, at most every "Look for updates every" hours. Safe to call whenever.
	///
	/// <paramref name="force"/> skips both gates, for when somebody has actually asked - the daily timer is
	/// there to keep the background check quiet, not to make "check now" mean "check tomorrow". A release
	/// published five minutes after startup is otherwise invisible for a day, which is exactly when somebody
	/// goes looking for it.
	/// </summary>
	/// <param name="quiet">Somebody asked - the 'update' command or the dashboard's button - and gets the answer right
	/// there. Announcing "a new version is out" on top of that sent a Telegram message saying so at the very moment
	/// 'update accept' was installing it.</param>
	/// <returns>Null when GitHub answered (or it wasn't time to ask), otherwise why it couldn't be asked.</returns>
	public static async Task<string?> LookAsync(CancellationToken ct = default, bool force = false, bool quiet = false) {
		TimeSpan every = TimeSpan.FromHours(Math.Clamp(Live.Global.UpdateCheckHours, 1, 24));

		if (!force && (!Live.Global.CheckForUpdates || (DateTime.UtcNow - _lastLooked < every))) {
			return null;
		}

		_lastLooked = DateTime.UtcNow;

		try {
			string json = await Http.GetStringAsync(SelfUpdate.Feed, ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(json);

			string tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
			string page = doc.RootElement.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? "" : "";
			string body = doc.RootElement.TryGetProperty("body", out JsonElement nb) ? nb.GetString() ?? "" : "";

			// Otherwise it reads as "up to date" - a changed feed or an error object would hide every release.
			if (tag.Length == 0) {
				Log.Debug($"update check: no tag_name in the answer from {FeedWhere()}");
			} else {
				Latest = tag;
			}

			if (!IsNewer(tag.TrimStart('v', 'V'), Build.Version)) {
				Available = null;
				NoDownloadYet = null;

				return null;
			}

			// Out only once there's something to install on this machine. A release is up the moment its tag is, and the Mac
			// zips are built and added a while later: a Mac was told "update accept installs it", and it failed with "no
			// download yet". Looked at again at the next check - and said plainly by 'update' and the dashboard meanwhile.
			// Only where it installs itself: Docker and a service update by hand, from the source or the zip for another
			// machine, so a CPU with no zip of its own (a Raspberry Pi's 32-bit Docker) was never told of any update at all.
			if (SelfUpdate.Supported && (SelfUpdate.ZipForThisMachine(doc.RootElement) == null)) {
				Log.DebugOnChange("update:no-zip", $"update check: {tag} has no zip for {Platform.ReleaseRid} yet - not announced until it has");
				Available = null;
				NoDownloadYet = tag;
				Url = page;

				return null;
			}

			NoDownloadYet = null;

			if (Available != tag) {
				_seenAt = DateTime.UtcNow;
				_notes = Highlights(body);
			}

			// Said once when it's first seen; after that the hourly reminder carries it.
			if ((Available != tag) && !quiet && !IsSkipped(tag)) {
				Log.Attention(SelfUpdate.Supported && (Live.Global.UpdateMode == UpdateModes.AtNight)
						? new Said("nocat.farm {0} is out - it installs by itself at a quiet time ('update accept' does it now)", tag)
					: SelfUpdate.Supported
						? new Said("nocat.farm {0} is out - 'update accept' installs it", tag)
						: new Said("nocat.farm {0} is out - 'update' shows how to install it", tag),
					UpdateBrief(tag), UpdateTitle(tag), Topic.Updates);
				Log.Info(new Said("'update skip' skips it · {0}", page));
				_remindedAt = DateTime.UtcNow;
			}

			Available = tag;
			Url = page;

			return null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// GitHub's refusal (a rate limit's 403) arrives as the HttpRequestException's message, status included.
			Log.Debug($"couldn't check for updates at {FeedWhere()}: {Log.Describe(e)}");

			// Asked again in 15 minutes, not after the full gap: a blip just after starting hid a new release for a day.
			_lastLooked = DateTime.UtcNow - every + TimeSpan.FromMinutes(15);

			return Log.Scrub(e.Message);
		}
	}

	/// <summary>The release feed for the log: host and path, no query.</summary>
	internal static string FeedWhere() => Log.Where(Uri.TryCreate(SelfUpdate.Feed, UriKind.Absolute, out Uri? feed) ? feed : null);

	/// <summary>The release notes' first few bullet points, plainly - enough to say what's new in one message.</summary>
	internal static string Highlights(string body) {
		List<string> lines = [.. body.Replace("\r", "").Split('\n')
			.Select(static l => l.Trim())
			.Where(static l => l.StartsWith("- ", StringComparison.Ordinal))
			.Select(static l => "- " + l[2..].Replace("*", "").Replace("`", ""))
			.Take(4)];

		string text = string.Join("\n", lines);

		return text.Length > 600 ? text[..600].TrimEnd() + "..." : text;
	}

	/// <summary>
	/// "Update by itself" set to install at night: installs the newest version when it's a quiet time - inside the
	/// hours set, a while after the release came out, no human-mode account awake, and nobody playing on any account.
	/// Called from the upkeep loop; the first account to get here does it, and a failure waits a few hours to retry.
	/// </summary>
	public static void AutoInstallIfDue(BotManager mgr) {
		GlobalConfig g = Live.Global;

		if (Commands.ExitRequested || (g.UpdateMode != UpdateModes.AtNight) || !g.CheckForUpdates || !SelfUpdate.Supported || SelfUpdate.Busy || (Available == null) || IsSkipped(Available)
			|| (DateTime.UtcNow - _seenAt < TimeSpan.FromHours(Math.Max(0, g.AutoUpdateWaitHours)))
			|| (DateTime.UtcNow - _autoTriedAt < TimeSpan.FromHours(4))) {
			return;
		}

		int hour = DateTime.Now.Hour;
		int from = Math.Clamp(g.AutoUpdateFromHour, 0, 23);
		int until = Math.Clamp(g.AutoUpdateUntilHour, 0, 24);
		bool inWindow = from < until ? (hour >= from) && (hour < until) : (hour >= from) || (hour < until);

		(bool quietNow, string why) = Quiet(mgr);

		if (!inWindow || !quietNow) {
			if (inWindow) {
				Log.DebugOnChange("update:held", $"auto-update waiting: {why}");
			}

			return;
		}

		// A trade offer or a gift sitting out its wait would lose it to the restart and start over, or be answered
		// in a burst afterwards - so the update waits for them. Said once in a while, so a night without an update
		// isn't a mystery.
		(string Account, int Trades, int Gifts)? busy = mgr.All
			.Where(static b => b.IsOnline)
			.Select(static b => (b.Name, Trades: BotManager.ModuleOf<Modules.Trading>(b)?.WaitingCount ?? 0, Gifts: BotManager.ModuleOf<Modules.Gifts>(b)?.WaitingCount ?? 0))
			.Where(static x => (x.Trades + x.Gifts) > 0)
			.Select(static x => ((string, int, int)?) (x.Name, x.Trades, x.Gifts))
			.FirstOrDefault();

		if (busy is { } wait) {
			if (DateTime.UtcNow - _heldLoggedAt > TimeSpan.FromHours(1)) {
				_heldLoggedAt = DateTime.UtcNow;
				Log.Info(new Said("auto-update waiting: {0} has {1} offer(s), {2} gift(s) queued", wait.Account, wait.Trades, wait.Gifts));
			}

			return;
		}

		_autoTriedAt = DateTime.UtcNow;
		// Says why it was clear, so an update at an odd moment can be checked against what it saw.
		Log.Good(new Said("installing {0} by itself - all clear: {1}", Available, why));

		_ = Task.Run(async () => {
			try {
				await SelfUpdate.ApplyAsync(CancellationToken.None, byItself: true).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", Log.Scrub(e.Message)));
				Log.StackToFile(e);
			}
		});
	}

	/// <summary>
	/// A version 'update accept' was asked for, waiting for the accounts to be asleep ("When I say update"). Null when
	/// nothing is waiting. Forgotten on restart - asking again is one command.
	/// </summary>
	public static string? Queued {
		get => Volatile.Read(ref _queued);
		private set => Volatile.Write(ref _queued, value);
	}

	private static string? _queued;

	/// <summary>A queued install on its way (1), so the next tick can't start it a second time before it has claimed Busy.</summary>
	private static int _queuedInstalling;

	private static DateTime _queuedSaidAt = DateTime.MinValue;

	/// <summary>
	/// What 'update' and the dashboard say when a newer version is out with no download for this machine yet (see
	/// <see cref="NoDownloadYet"/>) - instead of "you're on the newest release", which it isn't.
	/// </summary>
	public static Said NoDownloadSaid(string tag, bool queued = false) => queued
		? new Said("{0} is out, and you have {1}. There's no download for this computer yet. Your update accept still stands: it installs later, once the download is up.", tag, Build.Version)
		: new Said("{0} is out, and you have {1}. There's no download for this computer yet. It usually comes within the hour.", tag, Build.Version);

	/// <summary>'update accept' when a click waits for the accounts to sleep ("once everyone's asleep"): remember it, and say how it will go.</summary>
	public static Said Queue(string tag) {
		Queued = tag;
		_queuedSaidAt = DateTime.UtcNow;

		return new Said("{0} will install when your accounts are asleep - 'update now' installs it right away", tag);
	}

	/// <summary>
	/// A queued update, installed at the first quiet moment: no human-mode account awake, nobody playing on an account,
	/// no trade or gift waiting. Robot accounts that stay on all night don't count - with none in human mode at all,
	/// "asleep" means the night hours of Update by itself.
	/// </summary>
	public static void QueuedInstallIfDue(BotManager mgr) {
		GlobalConfig g = Live.Global;

		if ((Queued == null) || !SelfUpdate.Supported || SelfUpdate.Busy || Commands.ExitRequested || (Volatile.Read(ref _queuedInstalling) != 0)) {
			return;
		}

		// A newer version came out while it waited: that's the one to install.
		if ((Available != null) && (Available != Queued)) {
			Queued = Available;
		}

		// Newer still, but with no download for this machine yet: an install now would ask GitHub for the newest, find no
		// zip, fail, and forget it had been asked for. It waits for the download instead, and installs that.
		if (NoDownloadYet != null) {
			return;
		}

		// 'update skip' after 'update accept' means don't: it answered "it won't install by itself", and then it did.
		if (IsSkipped(Queued)) {
			Queued = null;

			return;
		}

		bool anyHuman = mgr.All.Any(static b => b.Cfg.Enabled && b.Cfg.LegitMode);
		(bool quietNow, string why) = Quiet(mgr);

		if (!anyHuman) {
			int hour = DateTime.Now.Hour;
			int from = Math.Clamp(g.AutoUpdateFromHour, 0, 23);
			int until = Math.Clamp(g.AutoUpdateUntilHour, 0, 24);
			quietNow &= from < until ? (hour >= from) && (hour < until) : (hour >= from) || (hour < until);
		}

		bool waitingTurn = mgr.All.Where(static b => b.IsOnline).Any(static b =>
			((BotManager.ModuleOf<Modules.Trading>(b)?.WaitingCount ?? 0) + (BotManager.ModuleOf<Modules.Gifts>(b)?.WaitingCount ?? 0)) > 0);

		if (!quietNow || waitingTurn) {
			// Now and then, so a version that hasn't gone in yet isn't a mystery.
			if (DateTime.UtcNow - _queuedSaidAt > TimeSpan.FromHours(3)) {
				_queuedSaidAt = DateTime.UtcNow;
				Log.Info(new Said("{0} waits for bedtime - 'update now' installs it now", Queued));
			}

			return;
		}

		string? tag = Queued;

		// Claimed once, by this tick and no other.
		if ((tag == null) || (Interlocked.CompareExchange(ref _queuedInstalling, 1, 0) != 0)) {
			return;
		}

		Log.Good(new Said("installing {0} - all clear: {1}", tag, why));

		// Still queued while it installs, and cleared once the attempt is over - not before it starts. Cleared first,
		// an install that never got going (another had just started, or the app was closing) forgot it had been asked
		// for, and the dashboard stopped saying it was waiting before anything had begun.
		_ = Task.Run(async () => {
			try {
				await SelfUpdate.ApplyAsync(CancellationToken.None, byItself: true).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", Log.Scrub(e.Message)));
				Log.StackToFile(e);
			} finally {
				// Only the version this was for: an 'update accept' for a newer one while it ran stays queued.
				Interlocked.CompareExchange(ref _queued, null, tag);
				Volatile.Write(ref _queuedInstalling, 0);
			}
		});
	}

	/// <summary>
	/// Whether it's clear to sign everything out for an update, and in words why - or what's holding it.
	/// </summary>
	/// <remarks>
	/// Clear means: nobody on any account, and no human-mode account awake. Two holes are closed here. An account that
	/// was only signing back in (a dropped connection, 20 seconds) used to count as "not online, so quiet" - and an update
	/// went in while you were playing on it. And an account you'd just stopped playing on counted as clear the minute
	/// Steam freed it. So a running account that isn't online yet holds it, and so does one you were on in the last
	/// quarter of an hour. An account stopped by you, switched off, or given up signing in doesn't.
	/// </remarks>
	internal static (bool Quiet, string Why) Quiet(BotManager mgr, DateTime? nowUtc = null) {
		DateTime now = nowUtc ?? DateTime.UtcNow;
		List<string> clear = [];

		// Closing is never a quiet moment: its accounts are signing out because it's closing, not because they're
		// asleep - counted as clear, an install by itself could start as the app went down.
		if (Commands.ExitRequested) {
			return (false, new Said("nocat.farm is closing").ToString());
		}

		foreach (Bot b in mgr.All) {
			// An account waiting for a Steam Guard code waits for you, not for Steam - it would have held every update until
			// somebody typed the code. A restart asks for it again.
			if (!b.Cfg.Enabled || !b.Running || (b.State is BotState.Failed or BotState.NeedsGuard)) {
				continue;   // nothing running there to disturb
			}

			if (b.PlayingBlocked) {
				return (false, new Said("you're playing on {0}", b.Name).ToString());
			}

			if (now - b.YouPlayedAt < TimeSpan.FromMinutes(15)) {
				return (false, new Said("you were on {0} a few minutes ago", b.Name).ToString());
			}

			if (!b.IsOnline) {
				return (false, new Said("{0} is signing in", b.Name).ToString());
			}

			if (b.Cfg.LegitMode && Modules.HumanMode.UpAndAbout(b)) {
				return (false, new Said("{0} is awake", b.Name).ToString());
			}

			clear.Add((b.Cfg.LegitMode ? new Said("{0} asleep", b.Name) : b.IsFarming ? new Said("{0} farming", b.Name) : new Said("{0} idling", b.Name)).ToString());
		}

		clear.Add(new Said("nobody playing").ToString());

		return (true, string.Join(", ", clear));
	}

	/// <summary>Is this release tag newer than what is running? Used by the updater before it downloads.</summary>
	public static bool IsNewerThanThisBuild(string tag) => IsNewer(tag.TrimStart('v', 'V'), Build.Version);

	/// <summary>Is this release tag older than what is running? Going back to one saves the settings first (see <see cref="Rollback"/>).</summary>
	public static bool IsOlderThanThisBuild(string tag) => IsNewer(Build.Version, tag.TrimStart('v', 'V'));

	/// <summary>Two versions, "v" or not: below zero when <paramref name="a"/> is older, zero when they're the same, above when it's newer.</summary>
	internal static int Compare(string a, string b) {
		string x = a.TrimStart('v', 'V'), y = b.TrimStart('v', 'V');

		return IsNewer(x, y) ? 1 : IsNewer(y, x) ? -1 : 0;
	}

	/// <summary>A version as it's typed and tagged: 1.7.1, three numbers.</summary>
	internal static bool IsVersion(string text) => System.Text.RegularExpressions.Regex.IsMatch(text.TrimStart('v', 'V'), @"^\d{1,4}\.\d{1,4}\.\d{1,4}\z");

	/// <summary>
	/// The newest release GitHub has named, newer than this build or not - Available only holds one that's newer. Going
	/// back to an older version holds this one off too when it's in between (see <see cref="Rollback.HoldOff"/>).
	/// </summary>
	internal static string? Latest { get; private set; }

	/// <summary>The releases last listed and when, so the dashboard opening its list again doesn't ask GitHub every time.</summary>
	private static (DateTime At, List<string> Versions)? _releases;

	/// <summary>
	/// Every version on GitHub when the releases were last listed, without the "v" - with a zip for this computer or not. A
	/// version there with none (yet: the Mac zips go up a while after the rest) is "no download for this computer", not gone.
	/// </summary>
	internal static IReadOnlyList<string> Tagged { get; private set; } = [];

	/// <summary>
	/// The versions on GitHub this computer can install, newest first and without the "v": every release kept there (the
	/// last five) that has a zip for this machine and isn't too old to go in that way - or, where it doesn't update itself
	/// (Docker, a service), every one, since those are updated by hand. Drafts and test releases are left out, as GitHub's
	/// "latest" leaves them out.
	/// </summary>
	/// <param name="fresh">Somebody asked ('update versions', 'update to'): asked again now. Otherwise a list from the last
	/// ten minutes is used - GitHub lets a PC ask only 60 times an hour.</param>
	/// <returns>The versions, or null and why GitHub couldn't be asked - a Said, so it's in the language of the sentence it goes in.</returns>
	internal static async Task<(List<string>? Versions, Said? Problem)> ReleasesAsync(bool fresh, CancellationToken ct = default) {
		if (!fresh && (_releases is { } known) && (DateTime.UtcNow - known.At < TimeSpan.FromMinutes(10))) {
			return (known.Versions, null);
		}

		try {
			string json = await Http.GetStringAsync(SelfUpdate.ListFeed, ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Array) {
				Log.Debug($"update: the list of releases from {Log.Where(Uri.TryCreate(SelfUpdate.ListFeed, UriKind.Absolute, out Uri? odd) ? odd : null)} isn't a list");

				return (null, new Said("GitHub gave no list of releases"));
			}

			List<string> found = [], tagged = [];
			string? newest = null;

			foreach (JsonElement release in doc.RootElement.EnumerateArray()) {
				string tag = release.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";

				if (!IsVersion(tag) || (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
					|| (release.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)) {
					continue;
				}

				if ((newest == null) || (Compare(tag, newest) > 0)) {
					newest = tag;
				}

				tagged.Add(tag.TrimStart('v', 'V'));

				// Nor one too old to say it started (see SelfUpdate.TooOld): 'update to' refuses it, so it isn't offered either.
				if (!SelfUpdate.Supported || ((SelfUpdate.ZipForThisMachine(release) != null) && (SelfUpdate.TooOld(tag) == null))) {
					found.Add(tag.TrimStart('v', 'V'));
				}
			}

			List<string> versions = [.. found.Distinct().OrderByDescending(static v => v, Comparer<string>.Create(Compare))];

			if (newest != null) {
				Latest = newest;
			}

			_releases = (DateTime.UtcNow, versions);
			Tagged = [.. tagged.Distinct()];

			return (versions, null);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) {
			// GitHub's refusal (a rate limit's 403) arrives in the message, status included.
			Log.Debug($"update: asking {Log.Where(Uri.TryCreate(SelfUpdate.ListFeed, UriKind.Absolute, out Uri? list) ? list : null)} for the releases failed: {Log.Describe(e)}");

			return (null, new Said("{0}", Log.Scrub(e.Message)));
		}
	}

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
