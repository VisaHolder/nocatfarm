using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Going back to an older version without losing a setting.
/// </summary>
/// <remarks>
/// An older version reads the settings files, knows only the settings it had, and writes them back the next time anything
/// is saved - without every setting that came later. Coming forward again, those read as never set: back to their
/// defaults, whatever they had been. So before an older version goes in ('update to', or an older zip with 'update file
/// ... force') the whole config folder is saved, the same files a backup takes, as
/// config/backups/before-1.7.1-from-1.7.3-20261006-143000.zip. And on every start, a version that knows at least as much as
/// the one the copy came from puts back every setting that is MISSING now - only those. A setting that's there is left
/// as it is, so a change made on the older version is kept; an account removed in between isn't brought back, and one
/// added in between is left alone. Then the copy is marked as done (-applied), so it happens once. A version in between
/// the two fills in the ones it knows and leaves the copy for a version that knows them all. Nothing missing on the way back
/// from the older version - it knew every setting - and the copy is done all the same (see <see cref="CameForward"/>).
/// </remarks>
public static partial class Rollback {
	/// <summary>Where the copies go: config/backups, so they travel with the settings they were taken from.</summary>
	public static string Folder => Path.Combine(ConfigStore.ConfigDir, "backups");

	/// <summary>The end of a copy's name once its settings have been put back.</summary>
	internal const string Applied = "-applied";

	/// <summary>How many copies that are done are kept, newest first - the rest go. One going back is rare, and each is a few MB at most.</summary>
	private const int KeepApplied = 5;

	/// <summary>The older version's part of a copy's name.</summary>
	private const string OldPart = "old";

	// before-<the older version>-from-<the version going back>-<when>.zip, and -applied on the end once it's done.
	[GeneratedRegex(@"^before-(?<old>\d+\.\d+\.\d+)-from-(?<from>\d+\.\d+\.\d+)-(?<at>\d{8}-\d{6})(?<applied>-applied)?\.zip\z", RegexOptions.CultureInvariant)]
	private static partial Regex Name();

	/// <summary>
	/// Save the whole config folder before <paramref name="older"/> goes in - every file a backup takes. Throws when it
	/// can't be written, and then the update stops with nothing changed.
	/// </summary>
	/// <returns>Where it went.</returns>
	public static string Snapshot(string older) => Snapshot(older, !SelfUpdate.Supported);

	/// <param name="older">The version going in.</param>
	/// <param name="byHand">Docker or a service: the older version goes in by hand, later or never, and nothing here knows
	/// which. 'update to' typed again there took another copy each time, every one with every saved login in it and read
	/// again at every start - so a copy for the same two versions that's still waiting is replaced by this one, which is
	/// newer. Where it updates itself, a copy whose going back stops is gone straight away (see <see cref="Discard"/>).</param>
	internal static string Snapshot(string older, bool byHand) {
		string old = older.TrimStart('v', 'V');
		Backup.OwnerOnlyFolder(Folder);

		string path = Path.Combine(Folder, $"before-{old}-from-{Build.Version}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip");
		string tmp = $"{path}.{Guid.NewGuid():N}.tmp";

		try {
			// Owner-only off Windows from the first byte, like a backup: it has every saved login in it.
			Backup.WriteOwnerOnly(tmp, Backup.Create());
			File.Move(tmp, path, overwrite: true);
		} catch {
			try {
				File.Delete(tmp);
			} catch {
				// nothing more to do
			}

			throw;
		}

		if (byHand) {
			try {
				foreach (string before in Directory.EnumerateFiles(Folder, $"before-{old}-from-{Build.Version}-*.zip")
					.Where(f => !f.Equals(path, StringComparison.Ordinal) && Name().Match(Path.GetFileName(f)) is { Success: true } m
						&& !m.Groups["applied"].Success && (m.Groups[OldPart].Value == old) && (m.Groups["from"].Value == Build.Version))
					.ToList()) {
					File.Delete(before);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// Kept: two copies are only one more to read at the start - the newest wins anyway.
				Log.Failed($"rollback: replacing the older copies for {old} in {Folder}", e);
			}
		}

		Log.Info(new Said("saved your settings before going back to {0}: {1}", old, Path.GetFileName(path)));

		return path;
	}

	/// <summary>
	/// The going back that <paramref name="path"/> was taken for didn't happen - closed while the accounts signed out, a skip
	/// just before the hand-over, something unexpected: the copy goes. Kept, nothing older ever went in to drop a setting, so
	/// it waited for good, with every saved login in it - and every try that stopped added another.
	/// </summary>
	internal static void Discard(string path) {
		try {
			File.Delete(path);
			Log.Debug($"rollback: {Path.GetFileName(path)} removed - the older version didn't go in");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// Left as it was before: read again at the next start, and the one after.
			Log.Failed($"rollback: removing {Path.GetFileName(path)}", e);
		}
	}

	/// <summary>
	/// Going back to <paramref name="target"/> started but didn't go in - another copy running, the older version not
	/// starting, its files not copied in - and this version is running again: the copies taken for it go, as for one that
	/// never started (<see cref="Discard"/>). Only the ones this start's <see cref="RestoreMissing()"/> read through and found
	/// nothing missing for (see <see cref="_unneeded"/>): one whose settings were needed has been used and marked as done
	/// already, and one it couldn't read - not a zip, held open by antivirus or OneDrive, a file of this install's that didn't
	/// read, or the whole look stopped - was promised another try at the next start. Removed here, that try never came.
	/// </summary>
	internal static void DiscardPending(string target) {
		string old = target.TrimStart('v', 'V');
		List<string> unneeded;

		lock (PendingGate) {
			unneeded = [.. _unneeded];
		}

		foreach (string copy in unneeded.Where(f => Name().Match(Path.GetFileName(f)) is { Success: true } m
			&& !m.Groups["applied"].Success && (m.Groups[OldPart].Value == old) && (m.Groups["from"].Value == Build.Version) && File.Exists(f))) {
			Discard(copy);
		}
	}

	/// <summary>
	/// This version is in again, updated from <paramref name="from"/>, which is older: the older version has been. A copy taken
	/// for going to it that this start found nothing missing for is done - the older version knew every setting, and dropped
	/// none. Left waiting for a missing setting that never comes, it was read at every start, with every saved login in it,
	/// and each round trip added another. Only the copies for that pair (before-&lt;from&gt;-from-&lt;this&gt;), and only the
	/// ones read through whole (<see cref="_unneeded"/>): one that didn't read waits for its try at the next start. On trial,
	/// marked once the trial is over (<see cref="ConfirmApplied"/>) - put back, the older version runs again.
	/// </summary>
	internal static void CameForward(string from) {
		string old = from.TrimStart('v', 'V');
		List<string> unneeded;

		lock (PendingGate) {
			unneeded = [.. _unneeded];
		}

		foreach (string copy in unneeded.Where(f => Name().Match(Path.GetFileName(f)) is { Success: true } m
			&& !m.Groups["applied"].Success && (m.Groups[OldPart].Value == old) && (m.Groups["from"].Value == Build.Version) && File.Exists(f))) {
			if (SelfUpdate.OnTrial) {
				lock (PendingGate) {
					Pending.Add(copy);
				}

				continue;
			}

			try {
				MarkApplied(copy);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// Not marked: it waits, as it did before.
				Log.Failed($"rollback: marking {Path.GetFileName(copy)} as done", e);
			}
		}
	}

	/// <summary>
	/// The copies the last <see cref="RestoreMissing(string)"/> read through whole and found nothing missing for - the only
	/// ones <see cref="DiscardPending"/> may remove, or <see cref="CameForward"/> mark as done. Set once that look is over; empty
	/// when it stopped part way, or hasn't run.
	/// </summary>
	private static List<string> _unneeded = [];

	/// <summary>
	/// The version to hold off once <paramref name="target"/> is in: the one it came from - unless the newest release is
	/// older than that (this is a build tried out from a file) and still newer than the target, when it's that release,
	/// or the older version would offer it straight away. One name, with its "v" as GitHub tags it: the older version
	/// reads the same skip file and knows only "this one".
	/// </summary>
	/// <returns>Null when the newest release is newer than the version it came from: the older version offers that one,
	/// not this, so nothing needs holding off - and a version skipped by hand (1.7.4, while on 1.7.3) stays skipped. Written
	/// over, going back to 1.7.1 offered 1.7.4 and installed it by itself in the night.</returns>
	public static string? HoldOff(string target) {
		string came = Build.Version;

		if (UpdateCheck.Latest is not { } latest) {
			return "v" + came;
		}

		if (UpdateCheck.Compare(latest, came) > 0) {
			return null;
		}

		return UpdateCheck.Compare(latest, target) > 0 ? "v" + latest.TrimStart('v', 'V') : "v" + came;
	}

	/// <summary>
	/// What a start's look at the copies has to say, for once the log is open - it runs before there is one. A line to show
	/// (a problem or not), or with <paramref name="Detail"/> only a line for the log file.
	/// </summary>
	public sealed record Note(bool Problem, Said Line, string? Detail = null);

	/// <summary>
	/// On every start, before the settings are read: put back what an older version left out. See the remarks above.
	/// </summary>
	public static List<Note> RestoreMissing() => RestoreMissing(Build.Version);

	/// <param name="running">The version doing it - this one, or another in the checks.</param>
	public static List<Note> RestoreMissing(string running) {
		List<Note> notes = [];
		List<string> unneeded = [];

		// Forgotten first: a look that stops part way vouches for nothing.
		lock (PendingGate) {
			_unneeded = [];
		}

		if (!Directory.Exists(Folder)) {
			return notes;
		}

		List<(string Path, Match Name)> copies = [];

		try {
			foreach (string file in Directory.EnumerateFiles(Folder, "before-*.zip")) {
				if (Name().Match(Path.GetFileName(file)) is { Success: true } m) {
					copies.Add((file, m));
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			notes.Add(new Note(false, default, $"rollback: couldn't look in {Folder}: {Log.Describe(e)}"));

			return notes;
		}

		// Newest first: only missing settings are filled in, so the first copy to have one wins - and the newest has the
		// value it had most lately.
		foreach ((string path, Match name) in copies.Where(static c => !c.Name.Groups["applied"].Success)
			.OrderByDescending(static c => c.Name.Groups["at"].Value, StringComparer.Ordinal)) {
			string old = name.Groups[OldPart].Value;

			// Still on a version older than the one the copy came from - one in between: it fills in what it knows (Known
			// keeps it to those), and the copy waits for a version that knows the rest. Passed over whole, 1.7.2 between 1.7.1
			// and 1.7.3 never got back the settings 1.7.1 had dropped that 1.7.2 itself has.
			int since = UpdateCheck.Compare(running, name.Groups["from"].Value);
			bool between = since < 0;

			try {
				(int count, List<string> where, int skipped) = FillIn(path, notes);

				// The version the copy came from, starting again with nothing missing: the older one hasn't been yet. 'update to'
				// in Docker or as a service takes the copy and then says how to go back by hand - and a restart before that
				// (the PC rebooting, the container recreated) marked it as done, so the settings the older version then dropped
				// never came back. It waits, filling in nothing, until something is missing - the older version has been - or a
				// newer version starts.
				bool notYet = (since == 0) && (count == 0);

				if (count > 0) {
					notes.Add(new Note(false, count == 1
						? new Said("brought back 1 setting that {0} didn't know", old)
						: new Said("brought back {0} settings that {1} didn't know", count, old)));
					notes.Add(new Note(false, default, $"rollback: from {Path.GetFileName(path)} - {string.Join("; ", where)}"));
				} else {
					notes.Add(new Note(false, default, $"rollback: {Path.GetFileName(path)} - no setting was missing"));
				}

				// Not done while a file of this install's couldn't be read (it tries again next start), nor on a version in between,
				// nor before the older version has been. Nor yet while this version is on trial: marked done and then put back, the
				// version before drops the settings again and nothing would ever bring them back - so it's marked once the trial
				// is over (ConfirmApplied).
				if ((skipped == 0) && !between && !notYet) {
					if (SelfUpdate.OnTrial) {
						lock (PendingGate) {
							Pending.Add(path);
						}
					} else {
						MarkApplied(path);
					}
				} else if ((skipped == 0) && notYet) {
					// Read through, every file of this install's with it, and nothing was missing.
					unneeded.Add(path);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException) {
				// Not marked as done: the next start tries again - only missing settings are ever put in, so twice is harmless.
				notes.Add(new Note(true, new Said("couldn't bring back the settings from {0} ({1}) - it tries again next start", Path.GetFileName(path), Log.Scrub(e.Message))));
				notes.Add(new Note(false, default, $"rollback: {Path.GetFileName(path)}: {Log.Describe(e)}"));
			}
		}

		Tidy(notes);

		lock (PendingGate) {
			_unneeded = unneeded;
		}

		return notes;
	}

	private static void MarkApplied(string path) => File.Move(path, path[..^".zip".Length] + Applied + ".zip", overwrite: true);

	/// <summary>Copies filled in on a start that was a new version on trial, to be marked as done once it has said it's fine.</summary>
	private static readonly List<string> Pending = [];

	private static readonly Lock PendingGate = new();

	/// <summary>
	/// The trial is over and this version stays: the copies it filled in are done now (see <see cref="RestoreMissing(string)"/>).
	/// Called by <see cref="SelfUpdate.ConfirmStarted"/>.
	/// </summary>
	internal static void ConfirmApplied() {
		List<string> done;

		lock (PendingGate) {
			done = [.. Pending];
			Pending.Clear();
		}

		foreach (string path in done) {
			try {
				MarkApplied(path);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// Not marked: the next start fills in again, which only ever puts in what's missing.
				Log.Failed($"rollback: marking {Path.GetFileName(path)} as done", e);
			}
		}
	}

	/// <summary>
	/// Every setting in the copy's settings files that is missing from the same file now, put in. Only files that are
	/// there now: an account removed in between stays removed. Only settings this version reads: anything else would go
	/// again at the next save, and isn't a setting it can bring back.
	/// </summary>
	/// <returns>How many, and per file which ones, for the log file - and how many of this install's files couldn't be read.
	/// A file in the copy that doesn't read is passed over and not counted: it never will read, and counted, the copy was tried
	/// again at every start for good, keeping every saved login in it.</returns>
	private static (int Count, List<string> Where, int Skipped) FillIn(string path, List<Note> notes) {
		int count = 0, skipped = 0;
		List<string> where = [];
		string root = Path.GetFullPath(ConfigStore.ConfigDir) + Path.DirectorySeparatorChar;

		using ZipArchive zip = ZipFile.OpenRead(path);

		foreach (ZipArchiveEntry entry in zip.Entries) {
			if (!entry.FullName.StartsWith("config/", StringComparison.Ordinal)) {
				continue;
			}

			string rel = entry.FullName["config/".Length..];
			Type? shape = Backup.Kind(rel) switch {
				"settings" => typeof(GlobalConfig),
				"account" => typeof(BotConfig),
				_ => null
			};

			string now = Path.GetFullPath(Path.Combine(root, rel));

			// Settings files only, and only inside config/ - Kind has already turned away any name that's a path in disguise.
			if ((shape == null) || !now.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
				|| !File.Exists(now)) {
				continue;
			}

			// One file at a time: a file that doesn't read - not JSON, or a name in it twice, which reads and then throws once
			// it's gone through - is passed over, and the rest still come back. It used to stop the whole copy, and a name in
			// it twice escaped altogether: nocat.farm closed at every start.
			JsonObject? saved;

			try {
				// A name in it twice, at any depth, is turned away as it's read - not later, half way through filling in.
				using (Stream s = entry.Open()) {
					saved = JsonNode.Parse(s, documentOptions: OneOfEach) as JsonObject;
				}
			} catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException or IOException) {
				// Damaged in the copy too - a broken header, packed bytes that don't unpack: it never will read. Escaping, it was
				// a red "tries again next start" at every start for good, and the copy was never done.
				notes.Add(new Note(false, default, $"rollback: {Path.GetFileName(path)}: passed over {rel} in the copy, it doesn't read - nothing to bring back from it: {Log.Describe(e)}"));

				continue;
			}

			try {
				// A file that doesn't read now is the settings loader's to report and keep; nothing is written over it.
				if ((saved == null) || (JsonNode.Parse(File.ReadAllText(now)) is not JsonObject current)) {
					continue;
				}

				HashSet<string> known = Known(shape);
				HashSet<string> there = new(current.Select(static p => p.Key), StringComparer.OrdinalIgnoreCase);
				List<string> added = [];

				// The top level only: a list or a table (games, add-ons left alone) is one setting. Filled in item by item, an
				// entry taken out on the older version would come back.
				foreach ((string key, JsonNode? value) in saved) {
					if (known.Contains(key) && !there.Contains(key)) {
						current[key] = value?.DeepClone();
						added.Add(key);
					}
				}

				if (added.Count > 0) {
					AtomicFile.Write(now, current.ToJsonString(Indented));
					count += added.Count;
					where.Add($"{rel}: {string.Join(", ", added)}");
				}
			} catch (Exception e) when (e is JsonException or ArgumentException) {
				skipped++;
				notes.Add(new Note(false, default, $"rollback: {Path.GetFileName(path)}: passed over {rel}, it doesn't read: {Log.Describe(e)}"));
			}
		}

		return (count, where, skipped);
	}

	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	/// <summary>Reading a copy's file: a name in it twice is an error, not the last one winning.</summary>
	private static readonly JsonDocumentOptions OneOfEach = new() { AllowDuplicateProperties = false };

	/// <summary>The names a settings file has for this version: every property it reads and writes.</summary>
	private static HashSet<string> Known(Type shape) => new(shape.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(static p => p.CanRead && p.CanWrite && (p.GetIndexParameters().Length == 0)
			&& (p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always }))
		.Select(static p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name), StringComparer.OrdinalIgnoreCase);

	/// <summary>Copies that are done, past the newest few, go - each holds every saved login, and none is needed any more.</summary>
	private static void Tidy(List<Note> notes) {
		try {
			foreach (string old in Directory.EnumerateFiles(Folder, "before-*" + Applied + ".zip")
				.Select(static f => (File: f, Name: Name().Match(Path.GetFileName(f))))
				.Where(static c => c.Name.Success)
				.OrderByDescending(static c => c.Name.Groups["at"].Value, StringComparer.Ordinal)
				.Skip(KeepApplied)
				.Select(static c => c.File)
				.ToList()) {
				File.Delete(old);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			notes.Add(new Note(false, default, $"rollback: couldn't tidy up old copies in {Folder}: {Log.Describe(e)}"));
		}
	}
}
