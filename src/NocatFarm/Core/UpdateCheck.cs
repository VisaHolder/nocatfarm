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

	/// <summary>When the newest release was first seen - "Wait after a release for" counts from here.</summary>
	private static DateTime _seenAt = DateTime.MinValue;

	/// <summary>The first lines of the release notes, for the message that says it's out.</summary>
	private static string _notes = "";

	private static DateTime _autoTriedAt = DateTime.MinValue;

	private static string SkipPath => Path.Combine(ConfigStore.ConfigDir, "state", "update-skip.txt");

	private static string? _skipped;
	private static bool _skipRead;

	/// <summary>
	/// The version 'update skip' was typed for - or that was put back after it wouldn't start. Nothing is said about
	/// it and it never installs by itself; the next version after it is announced as usual. Kept across restarts.
	/// </summary>
	public static string? Skipped {
		get {
			if (!_skipRead) {
				_skipRead = true;

				try {
					_skipped = File.Exists(SkipPath) ? File.ReadAllText(SkipPath).Trim() : null;
				} catch {
					_skipped = null;
				}
			}

			return string.IsNullOrEmpty(_skipped) ? null : _skipped;
		}
		set {
			_skipped = value;
			_skipRead = true;

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(SkipPath)!);

				if (string.IsNullOrEmpty(value)) {
					File.Delete(SkipPath);
				} else {
					AtomicFile.Write(SkipPath, value);
				}
			} catch {
				// it only lasts until the next launch, then
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
	private static bool IsSkipped(string? tag) => (tag != null) && (Skipped is { } s)
		&& string.Equals(s.TrimStart('v', 'V'), tag.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

	private static DateTime _heldLoggedAt = DateTime.MinValue;

	private static DateTime _remindedAt = DateTime.MinValue;

	private static Timer? _timer;
	private static int _ticking;

	/// <summary>
	/// A look shortly after starting, then once a minute: each part decides for itself whether it's time -
	/// "Look for updates every", the hourly reminder, and "Update by itself". It used to ride on an account's
	/// background loop, so with no account signed in nothing was ever checked.
	/// </summary>
	public static void Start(BotManager mgr) {
		_timer = new Timer(_ => _ = TickAsync(mgr), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
	}

	private static async Task TickAsync(BotManager mgr) {
		if (Interlocked.Exchange(ref _ticking, 1) == 1) {
			return;
		}

		try {
			await LookAsync().ConfigureAwait(false);
			RemindIfDue();
			AutoInstallIfDue(mgr);
			QueuedInstallIfDue(mgr);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't check for updates: {0}", e.Message));
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
		Log.Attention(SelfUpdate.Supported
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
			? Live.Global.AutoUpdate == 1
				? new("{0} is out - you have {1}. It installs by itself tonight, or type update accept (on Telegram: /update accept) to install it now.", tag, Build.Version)
				: new("{0} is out - you have {1}. Type update accept in the nocat.farm window, or /update accept here, to install it.", tag, Build.Version)
			: Platform.InContainer ? new("{0} is out - you have {1}. Pull the new image and recreate the container to update.", tag, Build.Version)
			: new("{0} is out - you have {1}. Download the new Linux zip from the releases page to update.", tag, Build.Version);

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
			string json = await Http.GetStringAsync(Releases, ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(json);

			string tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
			string page = doc.RootElement.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? "" : "";
			string body = doc.RootElement.TryGetProperty("body", out JsonElement nb) ? nb.GetString() ?? "" : "";

			if (!IsNewer(tag.TrimStart('v', 'V'), Build.Version)) {
				Available = null;

				return null;
			}

			if (Available != tag) {
				_seenAt = DateTime.UtcNow;
				_notes = Highlights(body);
			}

			// Said once when it's first seen; after that the hourly reminder carries it.
			if ((Available != tag) && !quiet && !IsSkipped(tag)) {
				Log.Attention(SelfUpdate.Supported
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
			Log.Debug(new Said("couldn't check for updates: {0}", e.Message));

			// Asked again in 15 minutes, not after the full gap: a blip just after starting hid a new release for a day.
			_lastLooked = DateTime.UtcNow - every + TimeSpan.FromMinutes(15);

			return e.Message;
		}
	}

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

		if ((g.AutoUpdate != 1) || !g.CheckForUpdates || !SelfUpdate.Supported || SelfUpdate.Busy || (Available == null) || IsSkipped(Available)
			|| (DateTime.UtcNow - _seenAt < TimeSpan.FromHours(Math.Max(0, g.AutoUpdateWaitHours)))
			|| (DateTime.UtcNow - _autoTriedAt < TimeSpan.FromHours(4))) {
			return;
		}

		int hour = DateTime.Now.Hour;
		int from = Math.Clamp(g.AutoUpdateFromHour, 0, 23);
		int until = Math.Clamp(g.AutoUpdateUntilHour, 0, 24);
		bool inWindow = from < until ? (hour >= from) && (hour < until) : (hour >= from) || (hour < until);

		// Quiet means quiet for every account: nobody at the keyboard on one of them, and no human-mode account
		// awake - an update signs everything out for a minute, which an awake account would notice.
		bool quietNow = mgr.All.All(static b => !b.IsOnline || (!b.PlayingBlocked && !(b.Cfg.LegitMode && Modules.HumanMode.UpAndAbout(b))));

		if (!inWindow || !quietNow) {
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
		Log.Good(new Said("installing {0} by itself - quiet time (Update by itself)", Available));

		_ = Task.Run(async () => {
			try {
				await SelfUpdate.ApplyAsync(CancellationToken.None, byItself: true).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", e.Message));
			}
		});
	}

	/// <summary>
	/// A version 'update accept' was asked for, waiting for the accounts to be asleep ("When I say update"). Null when
	/// nothing is waiting. Forgotten on restart - asking again is one command.
	/// </summary>
	public static string? Queued { get; private set; }

	private static DateTime _queuedSaidAt = DateTime.MinValue;

	/// <summary>'update accept' with "When I say update" set to wait: remember it, and say how it will go.</summary>
	public static Said Queue(string tag) {
		Queued = tag;
		_queuedSaidAt = DateTime.UtcNow;

		return new Said("{0} will install when your accounts are asleep (When I say update) - 'update now' installs it right away", tag);
	}

	/// <summary>
	/// A queued update, installed at the first quiet moment: no human-mode account awake, nobody playing on an account,
	/// no trade or gift waiting. Robot accounts that stay on all night don't count - with none in human mode at all,
	/// "asleep" means the night hours of Update by itself.
	/// </summary>
	public static void QueuedInstallIfDue(BotManager mgr) {
		GlobalConfig g = Live.Global;

		if ((Queued == null) || !SelfUpdate.Supported || SelfUpdate.Busy) {
			return;
		}

		// A newer version came out while it waited: that's the one to install.
		if ((Available != null) && (Available != Queued)) {
			Queued = Available;
		}

		// 'update skip' after 'update accept' means don't: it answered "it won't install by itself", and then it did.
		if (IsSkipped(Queued)) {
			Queued = null;

			return;
		}

		bool anyHuman = mgr.All.Any(static b => b.Cfg.Enabled && b.Cfg.LegitMode);
		bool quietNow = mgr.All.All(static b => !b.IsOnline || (!b.PlayingBlocked && !(b.Cfg.LegitMode && Modules.HumanMode.UpAndAbout(b))));

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

		string tag = Queued;
		Queued = null;
		Log.Good(new Said("installing {0} - the accounts are asleep", tag));

		_ = Task.Run(async () => {
			try {
				await SelfUpdate.ApplyAsync(CancellationToken.None, byItself: true).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Error(new Said("update failed: {0} - nothing was changed", e.Message));
			}
		});
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
