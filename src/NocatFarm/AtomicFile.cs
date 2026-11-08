namespace NocatFarm;

/// <summary>
/// Write-then-rename, so a crash or power cut mid-write can never leave a half-written file behind.
///
/// The state files (today's plan, comment counts, achievement progress, login tokens, lifetime totals) were
/// overwritten in place - a kill during the write left a truncated file that failed to parse next start, which
/// at best re-rolls the day and at worst throws away a login token and forces a password re-prompt. Writing to a
/// sibling ".tmp" and moving it over the target is atomic on the same volume, so a reader only ever sees the old
/// file or the whole new one, never a half.
///
/// 1.8.5: and the new copy is on the disk before the rename. Without that a power cut right after it - a TV box being
/// unplugged - left the renamed file full of zeros (the name moved, the data never got written), and the achievement hunt
/// started over: "couldn't read the achievement state: '0x00'".
/// </summary>
public static class AtomicFile {
	public static void Write(string path, string content) {
		string tmp = $"{path}.{Guid.NewGuid():N}.tmp";

		try {
			using (FileStream f = new(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
				byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(content);
				f.Write(bytes);
				f.Flush(flushToDisk: true);
			}

			MoveOver(tmp, path);
		} catch {
			TryDelete(tmp);   // a half-written copy of a secret must not be left lying next to it

			throw;
		}
	}

	/// <summary>
	/// The rename, tried again for a moment while something has the file open: Windows refuses to replace a file another
	/// reader has open ("access denied"), even one opened to allow it - a reload, the dashboard, a virus scanner. A settings
	/// save landing while one read failed, and the change was lost. Gives up (throws the last error) after about a second.
	/// </summary>
	private static void MoveOver(string tmp, string path) {
		for (int attempt = 1; ; attempt++) {
			try {
				File.Move(tmp, path, overwrite: true);

				return;
			} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && (e is not FileNotFoundException and not DirectoryNotFoundException) && (attempt < 100)) {
				Thread.Sleep(attempt < 10 ? 1 : 10);
			}
		}
	}

	/// <summary>
	/// 1.9.0: the half-written copies a stop mid-write left behind (a power cut, a kill) - "&lt;file&gt;.&lt;32 hex&gt;.tmp" - deleted at
	/// start, before anything writes, everywhere under <paramref name="folder"/>. Never read and never finished, they only piled
	/// up: one more for every power cut that landed in a save. Returns how many went.
	/// </summary>
	public static int SweepLeftovers(string folder) {
		int gone = 0;

		try {
			if (!Directory.Exists(folder)) {
				return 0;
			}

			foreach (string tmp in Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories)) {
				string name = Path.GetFileName(tmp);

				// Only this class's own: the name of a file, a dot, 32 hex digits, ".tmp".
				if ((name.Length > 37) && (name[^37] == '.') && name[^36..^4].All(Uri.IsHexDigit)) {
					try {
						File.Delete(tmp);
						gone++;
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						// held or not ours to delete - next start
					}
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// a folder that can't be looked through is said by whatever reads it
		}

		return gone;
	}

	private static void TryDelete(string path) {
		try {
			File.Delete(path);
		} catch {
			// nothing more to do
		}
	}

	public static Task WriteAsync(string path, string content) => WriteAsync(path, new System.Text.UTF8Encoding(false).GetBytes(content));

	/// <summary>1.9.0: the same for bytes - an add-on DLL, so a half-downloaded one is never left where it would load.</summary>
	public static async Task WriteAsync(string path, byte[] bytes) {
		string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
		try {
			await using (FileStream f = new(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true)) {
				await f.WriteAsync(bytes).ConfigureAwait(false);
				f.Flush(flushToDisk: true);
			}

			MoveOver(tmp, path);
		} catch {
			TryDelete(tmp);

			throw;
		}
	}
}
