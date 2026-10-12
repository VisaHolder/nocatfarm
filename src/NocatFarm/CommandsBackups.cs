using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm;

/// <summary>
/// 'backup now' and 'backup status' - the password-locked daily backup (<see cref="LockedBackup"/>). Replies in English,
/// like every typed command's.
/// </summary>
public static partial class Commands {
	private static async Task<string> BackupCommand(string[] rest) {
		string what = rest.Length > 0 ? rest[0].ToLowerInvariant() : "";

		if (what == "now") {
			if (Live.Global.BackupPassword.Length < LockedBackup.MinPassword) {
				return $"Set a backup password first (at least {LockedBackup.MinPassword} characters): Settings, Backups. Only you pick it, and you need it to restore.";
			}

			(bool ok, Said said) = await BackupSchedule.RunAsync(DateTime.Now).ConfigureAwait(false);

			return ok
				? $"{said.ToEnglish()}{Environment.NewLine}It's locked with your backup password - restore it from the dashboard: Settings, Backup & restore."
				: said.ToEnglish();
		}

		if (what == "status") {
			return BackupStatus(Live.Global, DateTime.Now);
		}

		return what.Length == 0 ? BackupNow() : "backup, backup now or backup status";
	}

	public static string BackupStatus(GlobalConfig g, DateTime now) {
		BackupSchedule.RunState s = BackupSchedule.State;
		List<string> lines = [
			g.BackupAuto
				? $"Daily backups: on, at {g.BackupHour:00}:00 into {LockedBackup.Folder(g)}, keeping the last {g.BackupKeep}."
				: "Daily backups: off - turn on \"Back up every day\" under Settings, Backups."
		];

		if (g.BackupPassword.Length < LockedBackup.MinPassword) {
			lines.Add($"No backup password yet - pick one (at least {LockedBackup.MinPassword} characters) under Settings, Backups.");
		}

		if (s.LastAt is { } at) {
			lines.Add(s.LastOk
				? $"Last backup: {at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, {s.LastFile}"
				: $"Last try failed: {s.LastError}");

			if (s.LastOk && (s.LogsLeftOut > 0)) {
				lines.Add($"{s.LogsLeftOut} older log file(s) were left out of it, so it stays small enough to restore.");
			}
		} else {
			lines.Add("No locked backup made yet - 'backup now' makes one.");
		}

		if (BackupSchedule.Next(now, g) is { } next) {
			lines.Add($"Next one: {next.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
