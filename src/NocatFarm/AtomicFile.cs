namespace NocatFarm;

/// <summary>
/// Write-then-rename, so a crash or power cut mid-write can never leave a half-written file behind.
///
/// The state files (today's plan, rep4rep counts, achievement progress, login tokens, lifetime totals) were
/// overwritten in place - a kill during the write left a truncated file that failed to parse next start, which
/// at best re-rolls the day and at worst throws away a login token and forces a password re-prompt. Writing to a
/// sibling ".tmp" and moving it over the target is atomic on the same volume, so a reader only ever sees the old
/// file or the whole new one, never a half.
/// </summary>
public static class AtomicFile {
	public static void Write(string path, string content) {
		string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
		File.WriteAllText(tmp, content);
		File.Move(tmp, path, overwrite: true);
	}

	public static async Task WriteAsync(string path, string content) {
		string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
		await File.WriteAllTextAsync(tmp, content).ConfigureAwait(false);
		File.Move(tmp, path, overwrite: true);
	}
}
