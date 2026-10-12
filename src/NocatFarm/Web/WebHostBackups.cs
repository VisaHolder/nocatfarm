using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>
/// The dashboard's side of the password-locked backups (<see cref="LockedBackup"/>, <see cref="BackupSchedule"/>).
/// </summary>
/// <remarks>
/// A locked backup restores through the same two steps as a 'backup' zip: /api/restore/check takes the file and, seeing
/// it's locked, holds it and asks for the password; /api/restore/unlock opens it and holds the result as a checked zip
/// for /api/restore/apply, after the page has shown what's in it. Neither the password nor anything opened with it is
/// ever written to the log.
/// </remarks>
public sealed partial class WebHost {
	/// <summary>A locked backup waiting for its password, and how many wrong ones it has had.</summary>
	private (string Token, byte[] Bytes, DateTime Until, int Wrong)? _locked;

	/// <summary>Wrong passwords for one staged file before it's dropped and has to be picked again.</summary>
	private const int WrongPasswordsAllowed = 10;

	/// <summary>Log files from an opened locked backup, by the token its checked zip is staged under.</summary>
	private readonly ConcurrentDictionary<string, List<(string Name, byte[] Data)>> _stagedLogs = new(StringComparer.Ordinal);

	/// <summary>One locked backup opened at a time - each try costs the 600,000 rounds.</summary>
	private readonly SemaphoreSlim _unlockGate = new(1, 1);

	private IResult StageLocked(byte[] bytes) {
		string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

		lock (_restoreGate) {
			_locked = (token, bytes, DateTime.UtcNow.AddMinutes(15), 0);
		}

		return Results.Json(new { Ok = true, Locked = true, Token = token });
	}

	private void PutBackStagedLogs(string? token) {
		if ((token != null) && _stagedLogs.TryRemove(token, out List<(string Name, byte[] Data)>? logs) && (logs.Count > 0)) {
			int put = LockedBackup.PutBackLogs(logs);

			if (put > 0) {
				Log.Info(new Said("restore: {0} log file(s) from the backup put back in the logs folder", put));
			}
		}
	}

	private static bool Same(string a, string? b) =>
		(b != null) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

	private void MapBackups(WebApplication app) {
		// ── locked backups ──
		// Step one and a half: the password for a locked backup the check is holding. Right, and it's opened, checked like a
		// 'backup' zip and staged for the usual Restore - with what's in it for the page to show first.
		app.MapPost("/api/restore/unlock", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (FromOutside(ctx)) {
				return HomeOnly();
			}

			UnlockRequest? body = await ReadJsonAsync<UnlockRequest>(ctx).ConfigureAwait(false);
			byte[]? bytes = null;

			lock (_restoreGate) {
				if ((_locked is { } l) && Same(l.Token, body?.Token) && (l.Until > DateTime.UtcNow)) {
					bytes = l.Bytes;
				}
			}

			if (bytes == null) {
				return Results.Json(new { Ok = false, Error = new Said("that restore has run out - pick the file again").ToString() });
			}

			LockedBackup.Opened opened;
			await _unlockGate.WaitAsync().ConfigureAwait(false);

			try {
				string password = body?.Password ?? "";
				opened = await Task.Run(() => LockedBackup.Open(bytes, password)).ConfigureAwait(false);
			} finally {
				_unlockGate.Release();
			}

			if (!opened.Ok) {
				bool again = opened.Problem == LockedBackup.Problem.WrongPassword;

				lock (_restoreGate) {
					if ((_locked is { } l) && Same(l.Token, body?.Token)) {
						// A wrong password may be typed again, a few times; anything else is the file, and that's that.
						_locked = again && (l.Wrong + 1 < WrongPasswordsAllowed) ? l with { Wrong = l.Wrong + 1 } : null;
					}
				}

				Log.Warn(new Said("dashboard: a backup was refused - {0}", opened.Error!));

				return Results.Json(new { Ok = false, opened.Error, WrongPassword = again });
			}

			Backup.Inspection check = Backup.Inspect(opened.Zip!);

			if (!check.Ok) {
				return Results.Json(new { Ok = false, check.Error });
			}

			string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

			lock (_restoreGate) {
				_locked = null;
				_staged = (token, opened.Zip!, DateTime.UtcNow.AddMinutes(15));
			}

			_stagedLogs.Clear();
			_stagedLogs[token] = opened.Logs;
			LockedBackup.Manifest m = opened.Manifest!;

			return Results.Json(new {
				Ok = true,
				Token = token,
				Locked = true,
				m.Created,
				m.Version,
				m.Machine,
				Windows = OperatingSystem.IsWindows(),
				SameMachine = true,   // its logins were opened, and are sealed again for this PC: they sign straight in
				check.Accounts,
				Files = check.Files.Count,
				Logs = opened.Logs.Count,
				Kinds = check.Files.GroupBy(static f => Backup.Kind(f) ?? "").ToDictionary(static g => g.Key, static g => g.Count())
			});
		});

		// What the daily backup is doing: for the Backups settings.
		app.MapGet("/api/backups/status", (HttpContext ctx) => Guard(ctx, () => {
			GlobalConfig g = Live.Global;
			BackupSchedule.RunState s = BackupSchedule.State;
			string folder = LockedBackup.Folder(g);
			int kept = 0;

			try {
				kept = Directory.Exists(folder) ? Directory.GetFiles(folder, "*" + LockedBackup.Extension).Count(static f => LockedBackup.IsOurs(Path.GetFileName(f))) : 0;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				kept = 0;
			}

			return Results.Json(new {
				On = g.BackupAuto,
				PasswordSet = g.BackupPassword.Length >= LockedBackup.MinPassword,
				Folder = folder,
				Next = BackupSchedule.Next(DateTime.Now, g),
				s.LastAt,
				s.LastOk,
				LastError = BackupSchedule.LastWhy,
				LastFile = s.LastFile.Length > 0 ? Path.GetFileName(s.LastFile) : "",
				Kept = kept
			});
		}));

		// "Back up now": the locked backup, now, wherever the daily one goes. Nothing leaves the PC, so from anywhere.
		app.MapPost("/api/backups/now", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			(bool ok, Said said) = await BackupSchedule.RunAsync(DateTime.Now).ConfigureAwait(false);

			return Results.Json(new { Ok = ok, Message = said.ToString() });
		});
	}

	private sealed class UnlockRequest {
		public string? Token { get; set; }
		public string? Password { get; set; }
	}
}
