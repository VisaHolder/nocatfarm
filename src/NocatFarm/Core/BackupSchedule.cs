using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The daily locked backup (<see cref="LockedBackup"/>): once a day at the time set under Settings, Backups - a few
/// minutes later each day, by a fixed random amount - and as soon as nocat.farm is open again if the PC was off then.
/// </summary>
/// <remarks>
/// A failure (the folder missing, the disk full, no permission) is said in the log and sent as a notification - once per
/// reason a day, not every retry - and tried again after a while. When one works again after failing, that's said too.
/// What happened last is kept in config/state/backup-run.json, which is this PC's own and never goes in a backup.
/// </remarks>
public static class BackupSchedule {
	public sealed class RunState {
		/// <summary>The day (local, yyyy-MM-dd) the last backup that worked was due for - the day it was made, or the day before for one due just before midnight that ran after it. "" = never.</summary>
		public string LastDay { get; set; } = "";
		public DateTime? LastAt { get; set; }
		public string LastFile { get; set; } = "";
		public bool LastOk { get; set; } = true;
		public string LastError { get; set; } = "";
		public DateTime? LastTry { get; set; }

		/// <summary>Log files the last backup left out so it stays small enough to restore. 0 = none.</summary>
		public int LogsLeftOut { get; set; }

		/// <summary>The day and reason last sent as a failure, so a retry with the same problem isn't sent again.</summary>
		public string Warned { get; set; } = "";
	}

	private static readonly SemaphoreSlim Gate = new(1, 1);
	private static readonly Lock StateGate = new();
	private static RunState? _state;
	private static string _stateFor = "";

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052", Justification = "Held, never read: a timer nothing holds on to is collected and stops firing.")]
	private static Timer? _timer;

	private static string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", "backup-run.json");

	public static void Start() {
		_timer = new Timer(static _ => _ = TickAsync(DateTime.Now), null, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(1));
	}

	public static RunState State {
		get {
			lock (StateGate) {
				if ((_state == null) || (_stateFor != StatePath)) {
					_stateFor = StatePath;
					_state = new RunState();

					try {
						if (File.Exists(StatePath)) {
							_state = JsonSerializer.Deserialize<RunState>(File.ReadAllText(StatePath)) ?? new RunState();
						}
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
						Log.Failed("backup: reading when the last one ran", e);
					}
				}

				return _state;
			}
		}
	}

	private static void SaveState(RunState s) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(s));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("backup: noting when the last one ran", e);
		}
	}

	/// <summary>
	/// How many minutes after the hour today's backup runs: the same all day (a restart doesn't pick again), different
	/// from day to day and from PC to PC, never more than <paramref name="most"/>. <paramref name="seed"/> stands in for
	/// this PC's name (tests pick it, so a day's minutes don't depend on the machine they run on).
	/// </summary>
	public static int Jitter(DateTime day, int most, string? seed = null) {
		if (most <= 0) {
			return 0;
		}

		byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + (seed ?? Environment.MachineName)));

		return (int) (BitConverter.ToUInt32(h, 0) % (uint) (most + 1));
	}

	/// <summary>When a day's backup is due (local time). A late hour plus its few minutes later can land after midnight.</summary>
	public static DateTime DueOn(DateTime day, GlobalConfig g, string? seed = null) =>
		day.Date.AddHours(Math.Clamp(g.BackupHour, 0, 23)).AddMinutes(Jitter(day.Date, Math.Clamp(g.BackupJitterMinutes, 0, 180), seed));

	/// <summary>
	/// The day whose backup counts at <paramref name="now"/>: the last one whose time has come. A day that isn't done
	/// stays owed - past midnight, after the PC slept - until the next day's time, so none is skipped; after a long
	/// sleep only the latest is owed, so one catch-up runs, not one per day missed. A state that never ran (a new
	/// install) owes nothing from before today: the one to come, or one whose time came since midnight.
	/// </summary>
	public static DateTime SlotDay(DateTime now, GlobalConfig g, RunState? s = null, string? seed = null) {
		// A day's time is between its own midnight and 3 hours past the next one (hour 23 plus at most 180 minutes),
		// and each day's comes after the day before's - so the one owed is today, yesterday or the day before.
		DateTime owed = now.Date;

		while (DueOn(owed, g, seed) > now) {
			owed = owed.AddDays(-1);
		}

		bool neverRan = (s != null) && (s.LastDay.Length == 0) && (s.LastTry == null);

		return neverRan && (DueOn(owed, g, seed).Date != now.Date) ? owed.AddDays(1) : owed;
	}

	private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>The day of the last one that worked, if it's this day's or a later one's - never one for a day already done or before it.</summary>
	private static DateTime? DoneFrom(RunState s, DateTime slot) =>
		(string.CompareOrdinal(s.LastDay, Key(slot)) >= 0) && DateTime.TryParseExact(s.LastDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime last)
			? last
			: null;

	/// <summary>Whether a backup should run at <paramref name="now"/> - due, not done for its day, and not waiting to try again.</summary>
	public static bool Due(DateTime now, GlobalConfig g, RunState s, string? seed = null) {
		if (!g.BackupAuto) {
			return false;
		}

		DateTime slot = SlotDay(now, g, s, seed);
		DateTime due = DueOn(slot, g, seed);

		if ((DoneFrom(s, slot) != null) || (now < due)) {
			return false;
		}

		// Failed since it was due: again once the wait is over - or at the next day's time, with no wait set.
		if (!s.LastOk && (s.LastTry is { } tried) && (tried >= due)) {
			return (g.BackupRetryMinutes > 0) && (now >= tried.AddMinutes(g.BackupRetryMinutes));
		}

		return true;
	}

	/// <summary>When the next one runs, for the dashboard - null while daily backups are off.</summary>
	public static DateTime? Next(DateTime now, GlobalConfig g, string? seed = null) {
		if (!g.BackupAuto) {
			return null;
		}

		RunState s = State;
		DateTime slot = SlotDay(now, g, s, seed);
		DateTime due = DueOn(slot, g, seed);

		if (DoneFrom(s, slot) is { } done) {
			return DueOn(done.AddDays(1), g, seed);
		}

		if (!s.LastOk && (s.LastTry is { } tried) && (tried >= due)) {
			return g.BackupRetryMinutes > 0 ? tried.AddMinutes(g.BackupRetryMinutes) : DueOn(slot.AddDays(1), g, seed);
		}

		return due > now ? due : now;
	}

	private static async Task TickAsync(DateTime now, string? seed = null) {
		// A backup still being written (a slow network drive) - this minute is skipped, not queued up behind it.
		if (!Gate.Wait(0)) {
			return;
		}

		try {
			// Looked at inside the gate: one that just finished has done this day's.
			if (Due(now, Live.Global, State, seed)) {
				await RunLockedAsync(now, seed).ConfigureAwait(false);
			}
		} catch (Exception e) {
			// Never lets the timer die: the next minute looks again.
			Log.Failed("backup: the daily backup", e);
		} finally {
			Gate.Release();
		}
	}

	/// <summary>
	/// Make the locked backup now - the daily one, or 'backup now' - and say how it went: the log, the notification, the
	/// state the dashboard shows. True when it worked.
	/// </summary>
	public static async Task<(bool Ok, Said Said)> RunAsync(DateTime now, string? seed = null) {
		await Gate.WaitAsync().ConfigureAwait(false);

		try {
			return await RunLockedAsync(now, seed).ConfigureAwait(false);
		} finally {
			Gate.Release();
		}
	}

	private static async Task<(bool Ok, Said Said)> RunLockedAsync(DateTime now, string? seed) {
		GlobalConfig g = Live.Global;
		RunState s = State;
		bool wasFailing = !s.LastOk;
		LockedBackup.Written written;

		try {
			// The key derivation and the zip take a second or two: off whatever thread asked.
			written = await Task.Run(() => LockedBackup.WriteNow(g, now)).ConfigureAwait(false);
		} catch (LockedBackup.Failed f) {
			return (false, Failure(s, now, f.Why));
		} catch (Exception e) {
			// 1.8.6: any failure is a failed backup - said and counted (the day's retries and warnings), never a silent retry a minute
			return (false, Failure(s, now, new Said("couldn't make the backup: {0}", Log.Describe(e))));
		}

		string path = written.Path;

		lock (StateGate) {
			// The day it was due for: one due just before midnight that ran just after counts for that day. Never moves
			// back to an earlier day ('backup now' before a day's time), so a day already done isn't owed again.
			string day = Key(SlotDay(now, g, s, seed));

			if (string.CompareOrdinal(day, s.LastDay) > 0) {
				s.LastDay = day;
			}

			s.LastAt = now;
			s.LastFile = path;
			s.LastOk = true;
			s.LastError = "";
			s.LastTry = now;
			s.Warned = "";
			s.LogsLeftOut = written.LogsLeftOut;
			SaveState(s);
		}

		long kb = (new FileInfo(path).Length + 1023) / 1024;
		Said saved = new("backup saved: {0} ({1} KB)", path, kb);
		Log.Good(saved);

		// 1.8.5: the locked file to the Telegram chat too, off this thread - never holding up the next backup.
		if (g.BackupToTelegram && Notifier.TelegramConnected) {
			_ = Task.Run(() => Notifier.SendBackupAsync(path, CancellationToken.None));
		}

		if (wasFailing) {
			Log.Publish(Topic.Backups, "nocat.farm", new Said("backups work again - {0}", Path.GetFileName(path)));
		} else if (g.BackupMessages == 2) {
			Log.Publish(Topic.Backups, "nocat.farm", new Said("backup saved: {0} ({1} KB)", Path.GetFileName(path), kb));
		}

		return (true, saved);
	}

	/// <summary>Why the last one failed, in the language picked now - "" when it worked.</summary>
	public static string LastWhy => State.LastOk ? "" : (_lastWhy.IsEmpty ? State.LastError : _lastWhy.ToString());

	private static Said _lastWhy;

	private static Said Failure(RunState s, DateTime now, Said why) {
		Said said = new("backup failed: {0}", why);
		_lastWhy = why;
		Log.Warn(said);
		string key = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + why.ToEnglish();
		bool tell;

		lock (StateGate) {
			s.LastOk = false;
			s.LastError = why.ToEnglish();
			s.LastTry = now;
			tell = s.Warned != key;
			s.Warned = key;
			SaveState(s);
		}

		// Once per reason a day: a drive left unplugged all day is one message, not one an hour.
		if (tell) {
			Log.Publish(Topic.Backups, "nocat.farm", said);
		}

		return said;
	}

	/// <summary>Forget what's held in memory - the checks move between folders.</summary>
	public static void Reset() {
		lock (StateGate) {
			_state = null;
		}
	}
}
