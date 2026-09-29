using System.Text.Json;

namespace NocatFarm.Config;

/// <summary>
/// "coming from" in the setup: which idler the accounts were on before (asf, idlemaster, other) and where it was
/// found, for the dashboard's first-run setup to open on bringing them over. Gone once that's done or skipped.
/// </summary>
public static class PendingImport {
	public sealed record Choice(string From, string? Path);

	private static string File => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "import-pending.json");

	public static void Save(string from, string? path) {
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
		AtomicFile.Write(File, JsonSerializer.Serialize(new Choice(from, path)));
	}

	public static Choice? Load() {
		try {
			return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Choice>(System.IO.File.ReadAllText(File)) : null;
		} catch {
			return null;
		}
	}

	public static void Clear() {
		try {
			System.IO.File.Delete(File);
		} catch {
			// it's only a hint
		}
	}
}
