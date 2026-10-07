using System.Buffers.Binary;
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
/// leaves the installation exactly as it was. config/, logs/ and backups/ are never in the archive and never copied over.
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

	/// <summary>Every release kept on GitHub - the last five - for 'update versions'.</summary>
	private const string ReleaseList = "https://api.github.com/repos/VisaHolder/nocatfarm/releases";

	/// <summary>
	/// Where to ask for every release: GitHub - or, while <see cref="Feed"/> is a copy on this machine, the same copy under
	/// /releases, so the tests can hand it old versions too.
	/// </summary>
	internal static string ListFeed => Feed == Releases ? ReleaseList : new Uri(Feed).GetLeftPart(UriPartial.Authority) + "/releases";

	/// <summary>One release by its version, for 'update to': .../releases/tags/v1.7.1, from the same place as <see cref="ListFeed"/>.</summary>
	internal static string TagFeed(string version) => $"{ListFeed}/tags/v{version.TrimStart('v', 'V')}";

	/// <summary>The program's own file: nocatFarm.exe on Windows, nocatFarm everywhere else.</summary>
	private static string ExeName => ExeNameFor(OperatingSystem.IsWindows());

	private static string ExeNameFor(bool windows) => windows ? "nocatFarm.exe" : "nocatFarm";

	/// <summary>The program itself, on every platform - the version is read from it.</summary>
	private const string ProgramDll = "nocatFarm.dll";

	/// <summary>Before the version in the swap script's tag when it was installed from a file ('update file').</summary>
	internal const string FromFileMark = "file:";

	/// <summary>Before the version in the swap script's tag when it's an older one, gone back to ('update to').</summary>
	internal const string BackMark = "back:";

	/// <summary>Before the version in the swap script's tag when it's a newer one asked for by name ('update to').</summary>
	internal const string ToMark = "to:";

	/// <summary>
	/// The oldest version that can go in this way: 1.4.6 is the first to tell the swap script it came up fine (NF_OK). One
	/// older never says "ok" - every account was signed out, it ran three minutes, and was put back as "didn't start".
	/// </summary>
	private const string OldestThatAnswers = "1.4.6";

	/// <summary>
	/// The same on a Mac started from start.command: the new version comes up in a Terminal window of its own, which starts
	/// clean, and finds where to answer in update-verify.txt - read from 1.5.4 on.
	/// </summary>
	private const string OldestThatAnswersInTerminal = "1.5.4";

	/// <summary>Started from start.command on a Mac: a version put in comes up in a Terminal window of its own.</summary>
	private static bool OpensInTerminal => OperatingSystem.IsMacOS() && (Environment.GetEnvironmentVariable("NOCATFARM_STARTER") == "start.command");

	/// <summary>The oldest version this copy can put in by itself.</summary>
	internal static string OldestInstallable => OpensInTerminal ? OldestThatAnswersInTerminal : OldestThatAnswers;

	/// <summary>
	/// Why <paramref name="version"/> can't go in this way - too old to say it started - or null when it can. Asked before
	/// anything is downloaded, saved or signed out ('update file', 'update to', and the install itself).
	/// </summary>
	internal static Said? TooOld(string version) => TooOld(version, OpensInTerminal);

	/// <param name="version">The version going in.</param>
	/// <param name="inTerminal">Started from start.command on a Mac - this copy, or another in the checks.</param>
	internal static Said? TooOld(string version, bool inTerminal) {
		string oldest = inTerminal ? OldestThatAnswersInTerminal : OldestThatAnswers;

		return UpdateCheck.Compare(version, oldest) < 0
			? new Said("{0} is too old to install this way - the oldest that can is {1}", version.TrimStart('v', 'V'), oldest)
			: null;
	}

	/// <summary>Where a person gets it by hand when updating itself can't.</summary>
	private const string ReleasesPage = "https://github.com/VisaHolder/nocatfarm/releases/latest";

	/// <summary>One release's page, where its notes are: https://github.com/VisaHolder/nocatfarm/releases/tag/v1.6.2.</summary>
	public static string ReleasePage(string version) => $"https://github.com/VisaHolder/nocatfarm/releases/tag/v{version.TrimStart('v', 'V')}";

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
	/// Closing takes it too (Commands.RequestExit), for the same reason - see <see cref="HandOver"/>.
	/// </summary>
	internal static readonly Lock HandoverGate = new();

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
	/// <remarks>Nor once nocat.farm is closing: the flag goes up before anything is cancelled, so an install that got
	/// this far while the app was being closed stops here rather than starting the swap - which would install the new
	/// version and start nocat.farm again after it had been closed. The flag goes up under this same lock, so closing
	/// lands either before the check (nothing is started) or after the swap has been (too late, as for a skip) - never
	/// between the two, where it was read as still open and the swap then started anyway.</remarks>
	/// <param name="tag">The version going in, for a skip of it to win - null for one from a file, which a skip doesn't stop.</param>
	internal static bool HandOver(string? tag, Action start) {
		lock (HandoverGate) {
			if (UpdateCheck.IsSkipped(tag) || Commands.ExitRequested) {
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

	/// <summary>
	/// 'update to' where updating itself isn't possible: how to put in <paramref name="version"/> by hand - that version, not
	/// the newest. In Docker its tag in the checkout; as a service its own release page and zip. "git pull" and the newest
	/// zip, said for every version, put the newest in instead - forward again, when going back was the point.
	/// </summary>
	public static Said ByHandTo(string version) {
		string v = version.TrimStart('v', 'V');

		if (Platform.InContainer) {
			return new Said("nocat.farm doesn't update itself inside Docker - in the nocatfarm folder run git fetch --tags, then git checkout v{0}, then docker compose up -d --build. config/ and logs/ are kept; to come back: git checkout main, then git pull, then docker compose up -d --build", v);
		}

		return new Said("running as a service, nocat.farm doesn't update itself - stop it, extract {0} from {1} over this folder (config/ and logs/ are kept) and start it again", ReleaseZipName(v), ReleasePage(v));
	}

	/// <summary>The release file for this machine off Windows: nocat.farm-v1.3.9_linux-x64.zip. A file name, not prose.</summary>
	private static string ReleaseZipName(string? tag) =>
		$"nocat.farm-{(string.IsNullOrEmpty(tag) ? "v*" : "v" + tag.TrimStart('v', 'V'))}_{Platform.ReleaseRid}.zip";

	/// <summary>What the zip for <paramref name="rid"/> is called: nocat.farm-v&lt;version&gt;-portable.zip on Windows,
	/// nocat.farm-v&lt;version&gt;_linux-x64.zip and so on elsewhere. A file name, not prose.</summary>
	private static string ZipNameFor(string rid) =>
		rid.StartsWith("win-", StringComparison.Ordinal) ? "nocat.farm-v<version>-portable.zip" : $"nocat.farm-v<version>_{rid}.zip";

	/// <summary>
	/// Is this release asset the zip for this machine?
	///
	/// The Windows zip has no platform in its name (nocat.farm-v1.3.9.zip, nocat.farm-v1.4.9-portable.zip); the Linux ones carry their
	/// platform after an underscore (nocat.farm-v1.3.9_linux-x64.zip). The underscore is load-bearing: GitHub lists
	/// a release's files alphabetically, and every copy up to 1.3.8 installs simply the FIRST .zip in that list.
	/// "_" sorts after the "." of ".zip", so the Windows zip stays first and those copies keep updating properly -
	/// with "-linux" it would sort first and every one of them would download the Linux build and refuse it.
	/// </summary>
	internal static bool IsZipForThisMachine(string asset) => IsZipFor(asset, Platform.ReleaseRid);

	/// <param name="rid">The machine it's for, as a release names it: this one, or another in the checks.</param>
	private static bool IsZipFor(string asset, string rid) {
		if (!asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
			return false;
		}

		if (rid.StartsWith("win-", StringComparison.Ordinal)) {
			return !asset.Contains("linux", StringComparison.OrdinalIgnoreCase) && !asset.Contains("osx", StringComparison.OrdinalIgnoreCase);
		}

		return asset.EndsWith("_" + rid + ".zip", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The download for this machine in a release as GitHub describes it: its address and size, or null when it has none
	/// (yet - the Mac zips are added a while after the rest, and a release is announced the moment its tag exists).
	/// </summary>
	internal static (string Url, long Size)? ZipForThisMachine(JsonElement release) {
		if (!release.TryGetProperty("assets", out JsonElement assets) || (assets.ValueKind != JsonValueKind.Array)) {
			return null;
		}

		foreach (JsonElement asset in assets.EnumerateArray()) {
			string assetName = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";

			if (IsZipForThisMachine(assetName) && asset.TryGetProperty("browser_download_url", out JsonElement u) && (u.GetString() is { Length: > 0 } url)) {
				return (url, asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long size) ? size : 0);
			}
		}

		return null;
	}

	/// <summary>
	/// 'update file': the version in a nocat.farm zip on this PC, or why it can't go in here - checked the way the updater
	/// checks a download, before anything is signed out: a zip for this machine, readable, and the program at the top of it
	/// (or inside its one folder, like releases up to 1.2.6). The version is read from the program itself, not the file
	/// name, which anybody can rename. Which kind of computer it's for: on Windows by its name, the way an update picks its
	/// download; on Linux and a Mac by the program in it. Told by the name there too, a download the browser saved as
	/// "nocat.farm-v1.7.4_linux-x64 (1).zip" - or one renamed to anything - was refused as for another kind of computer.
	/// </summary>
	internal static (string? Version, Said Problem) LookInZip(string path) => LookInZipFor(path, Platform.ReleaseRid);

	/// <param name="rid">The machine it's for, as a release names it (win-x64, linux-arm64, osx-arm64): this one, or another
	/// in the checks.</param>
	internal static (string? Version, Said Problem) LookInZipFor(string path, string rid) {
		string name = Path.GetFileName(path);
		bool windows = rid.StartsWith("win-", StringComparison.Ordinal);
		string exeName = ExeNameFor(windows);
		string zipName = ZipNameFor(rid);
		Said another = new("{0} is a nocat.farm for another kind of computer - this one needs {1}", name, zipName);

		if (!File.Exists(path)) {
			return (null, new Said("there's no file at {0}", path));
		}

		if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
			return (null, new Said("{0} isn't a zip - it needs a nocat.farm zip like {1}", name, zipName));
		}

		if (windows && (name.Contains("linux", StringComparison.OrdinalIgnoreCase) || name.Contains("osx", StringComparison.OrdinalIgnoreCase))) {
			return (null, another);
		}

		try {
			using ZipArchive zip = ZipFile.OpenRead(path);

			// Flat, or everything inside one folder - the same two layouts the updater takes.
			string[] tops = [.. zip.Entries.Select(static e => e.FullName.Replace('\\', '/')).Where(static n => n.Length > 0)
				.Select(static n => n.Contains('/') ? n[..(n.IndexOf('/') + 1)] : n).Distinct()];
			string folder = tops.Contains(exeName) ? "" : (tops is [string only] && only.EndsWith('/') ? only : "");
			ZipArchiveEntry? Entry(string file) => zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == folder + file);

			if (Entry(exeName) is not { } exe) {
				// Off Windows, the Windows zip: its program is nocatFarm.exe.
				return (null, !windows && (Entry(ExeNameFor(true)) != null)
					? another
					: new Said("{0} isn't a nocat.farm zip for this computer - there's no {1} in it", name, exeName));
			}

			if (!windows) {
				byte[] head = new byte[4096];
				int got;

				using (Stream from = exe.Open()) {
					got = from.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
				}

				switch (RunsOn(head.AsSpan(0, got), rid, name)) {
					case null:
						return (null, new Said("{0} isn't a nocat.farm zip - its {1} isn't a program", name, exeName));
					case false:
						return (null, another);
				}
			}

			if (Entry(ProgramDll) is not { } program) {
				return (null, new Said("{0} isn't a nocat.farm zip - there's no {1} in it", name, ProgramDll));
			}

			using MemoryStream copy = new();

			using (Stream from = program.Open()) {
				from.CopyTo(copy);
			}

			copy.Position = 0;
			using System.Reflection.PortableExecutable.PEReader pe = new(copy);

			if (!pe.HasMetadata) {
				return (null, new Said("{0} isn't a nocat.farm zip - its {1} isn't a program", name, ProgramDll));
			}

			System.Reflection.Metadata.MetadataReader meta = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
			System.Reflection.Metadata.AssemblyDefinition assembly = meta.GetAssemblyDefinition();

			if (!meta.GetString(assembly.Name).Equals(Path.GetFileNameWithoutExtension(exeName), StringComparison.Ordinal)) {
				return (null, new Said("{0} isn't a nocat.farm zip - its {1} is another program", name, ProgramDll));
			}

			Version v = assembly.Version;

			return ($"{v.Major}.{v.Minor}.{v.Build}", default);
		} catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException) {
			return (null, new Said("couldn't read {0} as a zip ({1})", name, Log.Scrub(e.Message)));
		}
	}

	/// <summary>
	/// Whether a program file runs on <paramref name="rid"/>'s kind of computer, from its first bytes: Linux's ELF says its
	/// processor at byte 18, a Mac's Mach-O right after its magic number - or, in a universal one, in its list of the ones it
	/// has. Null when it's neither.
	/// </summary>
	/// <param name="name">The zip's name: for a chip not in the list below, the name says it, as an update picks its download.
	/// Without it every zip was refused there - "needs nocat.farm-v&lt;version&gt;_linux-riscv64.zip", the very name it had.</param>
	private static bool? RunsOn(ReadOnlySpan<byte> head, string rid, string name) {
		bool linux = rid.StartsWith("linux-", StringComparison.Ordinal), mac = rid.StartsWith("osx-", StringComparison.Ordinal);
		string arch = rid[(rid.IndexOf('-') + 1)..];

		if ((head.Length >= 20) && head[..4].SequenceEqual("\u007FELF"u8)) {
			// In the file's own byte order: 1 little-endian, 2 big.
			ushort machine = head[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(head[18..]) : BinaryPrimitives.ReadUInt16LittleEndian(head[18..]);

			// Every chip .NET runs Linux on, by the number ELF gives it.
			int? want = arch switch {
				"x64" => 62, "arm64" => 183, "arm" => 40, "x86" => 3, "riscv64" => 243, "loongarch64" => 258, "s390x" => 22, "ppc64le" => 21, _ => null
			};

			return linux && (want is { } w ? machine == w : IsZipFor(name, rid));
		}

		if (head.Length < 8) {
			return null;
		}

		uint cpu = arch switch { "x64" => 0x01000007, "arm64" => 0x0100000C, "arm" => 12, "x86" => 7, _ => 0 };

		// Apple silicon runs an Intel Mac's program too (Rosetta) - and a trial that fails puts the old version back. Called
		// "for another kind of computer", the Intel zip was refused on a Mac that runs it.
		bool Runs(uint c) => (c == cpu) || ((arch == "arm64") && (c == 0x01000007));

		switch (BinaryPrimitives.ReadUInt32LittleEndian(head)) {
			case 0xFEEDFACF or 0xFEEDFACE:
				return mac && Runs(BinaryPrimitives.ReadUInt32LittleEndian(head[4..]));
			case 0xCFFAEDFE or 0xCEFAEDFE:
				return mac && Runs(BinaryPrimitives.ReadUInt32BigEndian(head[4..]));
		}

		// Universal: a list of the ones it has, 20 bytes each - or 32 in the 64-bit list (0xCAFEBABF), which was taken for no
		// program at all.
		if (BinaryPrimitives.ReadUInt32BigEndian(head) is 0xCAFEBABE or 0xCAFEBABF) {
			int each = BinaryPrimitives.ReadUInt32BigEndian(head) == 0xCAFEBABF ? 32 : 20;
			uint count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);

			for (int i = 0; (i < count) && (8 + (each * i) + 4 <= head.Length); i++) {
				if (Runs(BinaryPrimitives.ReadUInt32BigEndian(head[(8 + (each * i))..]))) {
					return mac;
				}
			}

			return false;
		}

		return null;
	}

	/// <summary>
	/// Other copies of nocat.farm running from this very folder (with a config folder of their own, or the lock would have
	/// stopped them). Off Windows only: an update replaces the files they're running, putting the old version back would
	/// stop them, and the check for "the new version is up" would see them instead. Their process ids; empty when none.
	/// </summary>
	internal static List<int> OthersRunningHere() {
		List<int> found = [];

		if (OperatingSystem.IsWindows()) {
			return found;
		}

		string mine = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
		HashSet<string> same = new(StringComparer.Ordinal) {
			mine,
			Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ExeName)),
			File.ResolveLinkTarget(mine, true)?.FullName ?? mine
		};

		foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))) {
			using (p) {
				if (p.Id == Environment.ProcessId) {
					continue;
				}

				try {
					string? exe = OperatingSystem.IsLinux() ? new FileInfo($"/proc/{p.Id}/exe").LinkTarget : p.MainModule?.FileName;

					if ((exe != null) && same.Contains(exe)) {
						found.Add(p.Id);
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
					or NotSupportedException or System.ComponentModel.Win32Exception) {
					// another user's, or gone - not one of ours
				}
			}
		}

		return found;
	}

	/// <summary>
	/// The program file that is this copy of nocat.farm, for the swap script to know copies from this folder by: the one
	/// running, unless that's the .NET host - started as "dotnet nocatFarm.dll", the running program is dotnet itself, and
	/// the script took every .NET program on the machine for another nocat.farm in this folder and refused to update.
	/// </summary>
	internal static string ProgramPath(string? processPath, string here, string exeName) =>
		(processPath != null) && Path.GetFileName(processPath).Equals(exeName, StringComparison.Ordinal) ? processPath : Path.Combine(here, exeName);

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

		// Said before Telegram and Discord are listening (the first thing at start): kept to send once they are. Under
		// the lock NotifierReady takes: checked and kept on one side of it, a failure landing just as they started
		// listening was kept after NotifierReady had already looked - and never sent.
		lock (HeldBackGate) {
			if (!_notifierReady) {
				_heldBack = why;
			}
		}

		LastFailure = why.ToString();

		return LastFailure;
	}

	private static readonly Lock HeldBackGate = new();
	private static bool _notifierReady;
	private static Said? _heldBack;

	/// <summary>Telegram and Discord are listening now: what an update said before they were goes out.</summary>
	public static void NotifierReady() {
		Said? why;

		lock (HeldBackGate) {
			_notifierReady = true;
			why = _heldBack;
			_heldBack = null;
		}

		if (why is { } said) {
			Log.Publish(Topic.Installs, "nocat.farm", said);
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

			// Another copy started from this folder while the accounts were signing out: nothing was copied.
			if (swapCode?.StartsWith("busy", StringComparison.Ordinal) == true) {
				string? noted = NotedTarget();
				PutSkipBack(noted);
				Gone(noted);
				TryDelete(NotePath);
				TryDelete(NotesPath);
				Fail(new Said("update stopped - another nocat.farm is running from this folder (process {0}); close it first, nothing changed", swapCode[4..].Trim()));

				return;
			}

			if (swapCode?.StartsWith("crashed", StringComparison.Ordinal) == true) {
				string bad = swapCode[7..].Trim();
				TryDelete(NotePath);
				TryDelete(NotesPath);

				// From a file ('update file'): put back, and not skipped - it's the file that didn't start, and the release
				// with that number, when it comes, is a different build. An older zip ('update file ... force') went back too, and
				// its tag says "file:" first: the skip goes back to what it was, as for 'update to'. Left out, the version held
				// off stayed skipped. A newer file never matches the kept pair, and a note left from before goes.
				if (bad.StartsWith(FromFileMark, StringComparison.Ordinal)) {
					PutSkipBack(bad[FromFileMark.Length..]);
					Gone(bad[FromFileMark.Length..]);
					Fail(new Said("update undone: nocat.farm {0} from the file didn't start, back on {1}", bad[FromFileMark.Length..], Build.Version));

					return;
				}

				// Gone back to an older version that didn't start: not skipped either - an older version is never offered, and
				// "'update accept' retries" would install the newest, not the one asked for. And the skip goes back to what it
				// was before going back held a version off: still on this version, there's nothing to hold off.
				if (bad.StartsWith(BackMark, StringComparison.Ordinal)) {
					PutSkipBack(bad[BackMark.Length..]);
					Gone(bad[BackMark.Length..]);
					Fail(new Said("going back didn't work: nocat.farm {0} didn't start, so you're on {1} again", bad[BackMark.Length..], Build.Version));

					return;
				}

				// A newer version asked for by name ('update to') that didn't start: put back, the skip as it was before, and then
				// this one skipped - unless a newer one already is. Skipped over that, a skip typed by hand for a newer one (1.7.5,
				// while 1.7.4 was asked for) was written over, and that one installed by itself the same night. Left as it was, a
				// version asked for when it was the newest, with nothing skipped, was installed again by itself that night - and
				// crashed again. 'update to' asks for it by name, skipped or not; 'update accept' would install the newest.
				if (bad.StartsWith(ToMark, StringComparison.Ordinal)) {
					string asked = bad[ToMark.Length..];
					PutSkipBack(asked);

					if ((asked.Length > 0) && !((UpdateCheck.Skipped is { } kept) && UpdateCheck.IsVersion(kept) && (UpdateCheck.Compare(kept, asked) > 0))) {
						UpdateCheck.Skipped = asked;
					}

					UpdateCheck.NoteFailedInstall();
					Fail(new Said("update undone: {0} didn't start, back on {1} - 'update to {0}' tries it again", asked, Build.Version));

					return;
				}

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

			// The version noted, and the swap left nothing behind. The same version from a file ('update file ... force') whose files
			// didn't copy in is this version too - read as done, it said "updated 1.7.3 → 1.7.3" and "Install complete".
			if ((p[1] == Build.Version) && (swapCode == null)) {
				Log.Good(new Said("updated {0} → {1} · {2}MB in {3}s", p[0], p[1], p[2], p[3]));

				// Back from an older version: a settings copy taken for going to it, with nothing it didn't know, is done now
				// (see Rollback.CameForward).
				if (UpdateCheck.IsVersion(p[0]) && UpdateCheck.IsOlderThanThisBuild(p[0])) {
					Rollback.CameForward(p[0]);
				}

				string notes = "";

				try {
					notes = File.Exists(NotesPath) ? File.ReadAllText(NotesPath).Trim() : "";
				} catch (Exception e) {
					// the message goes without them
					Log.Failed("update: reading the new version's release notes", e);
				}

				TryDelete(NotesPath);

				if (notes.Length > 0) {
					// One short line and where to read the rest. Every change strung onto one line ran across the whole
					// window and off the end of it once a release had more than two or three.
					Log.Info(new Said("what's new in {0}: {1}", p[1], ReleasePage(p[1])));
				}

				ConfirmWhenSettled(p[0], p[1], notes);
			} else if (swapCode != null) {
				PutSkipBack(p[1]);
				Gone(p[1]);
				UpdateCheck.NoteFailedInstall();
				// The swap put every file back the way it was before starting this version again, so "nothing was
				// changed" is true - see SwapScript.
				Fail(SwapFailure(swapCode, OperatingSystem.IsWindows()));
			} else {
				PutSkipBack(p[1]);
				Gone(p[1]);
				Fail(new Said("update failed: {0} didn't start - still on {1}, try again", p[1], Build.Version));
			}
		} catch (Exception e) {
			Log.Failed("couldn't read the update note", e);
		}
	}

	/// <summary>
	/// The skip as it was before going back to an older version held one off: "1.7.3|1.7.1|v1.7.4" - the version it was on,
	/// the one it went back to, and what was skipped then (nothing after the last |, when nothing was). Read by this same
	/// version if the older one didn't go in after all.
	/// </summary>
	private static string SkipBeforePath => Path.Combine(ConfigStore.ConfigDir, "state", "update-skip-before.txt");

	/// <summary>Going back to <paramref name="target"/>: the skip as it is now, kept for <see cref="PutSkipBack"/>.</summary>
	private static void KeepSkipBefore(string target) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(SkipBeforePath)!);
			AtomicFile.Write(SkipBeforePath, string.Join('|', Build.Version, target.TrimStart('v', 'V'), UpdateCheck.Skipped ?? ""));
		} catch (Exception e) {
			// Gone rather than left as it was: an older one for the same two versions would put back the wrong skip.
			TryDelete(SkipBeforePath);
			Log.Failed("update: keeping the skipped version to put back", e);
		}
	}

	/// <summary>
	/// Going back to <paramref name="target"/> didn't happen, and this version is running again: the skip goes back to what
	/// it was before going back held a version off. Left as that, a version skipped by hand was forgotten, and this very
	/// version - or a newer one tried out from a file - stayed skipped. Only for this same pair of versions.
	/// </summary>
	private static void PutSkipBack(string? target) {
		if ((target == null) || !File.Exists(SkipBeforePath)) {
			return;
		}

		try {
			string[] kept = File.ReadAllText(SkipBeforePath).Trim().Split('|', 3);

			if ((kept.Length == 3) && (kept[0] == Build.Version) && (UpdateCheck.Compare(kept[1], target) == 0)) {
				UpdateCheck.Skipped = kept[2].Length > 0 ? kept[2] : null;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("update: putting the skipped version back", e);
		}

		TryDelete(SkipBeforePath);
	}

	/// <summary>
	/// Going to <paramref name="target"/> didn't happen: a settings copy taken for going back to it goes (see
	/// <see cref="Rollback.DiscardPending"/>). Kept, it waited for good with every saved login in it - only a swap that never
	/// started got rid of its copy. Nothing for a newer version, which never had one.
	/// </summary>
	private static void Gone(string? target) {
		if (target != null) {
			Rollback.DiscardPending(target);
		}
	}

	/// <summary>The version the update note says was going in, or null when there's no note to read.</summary>
	private static string? NotedTarget() {
		try {
			return File.Exists(NotePath) && (File.ReadAllText(NotePath).Split('|') is [_, string to, ..]) ? to : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("update: reading the update note", e);

			return null;
		}
	}

	/// <summary>
	/// Why copying the new files in failed, from what the swap script left in NF_FAIL.
	/// </summary>
	/// <remarks>
	/// On Windows that's robocopy's exit code, whose flags add up: 8 and up with 16 unset means some files failed (in use,
	/// access denied); 16 and up means it couldn't work in the folder at all. 11 = 8 + extra files + copied. The sh script
	/// on Linux and a Mac writes a plain 8 for any copy that failed - and there a file in use is never why (a rename over a
	/// running program works), while a folder this user can't write to (unzipped with sudo, or in /opt) or a full disk is.
	/// Read as robocopy's 8 it blamed "a 2nd copy? antivirus?", neither of which it can be there.
	/// </remarks>
	internal static Said SwapFailure(string code, bool windows) =>
		code.StartsWith("backup", StringComparison.Ordinal)
			? new Said("update failed: backup copy error {0} - disk full? Nothing changed", code[6..].Trim())
			: windows && int.TryParse(code, out int rc) && (rc is >= 8 and < 16)
				? new Said("update failed: files in use (a 2nd copy? antivirus?) - try again")
				: new Said("update failed: copy error {0} (read-only? disk full?) - nothing changed", code);

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
		bool trial = OnTrial;
		_confirmed = true;
		Mark("ok");

		// Tried out and staying: there's no skip left to put back - and nothing left to keep plain for the version before.
		if (trial) {
			TryDelete(SkipBeforePath);
			Reseal();
		}

		// The settings brought back on this start stay now: their copies are done (see Rollback.RestoreMissing).
		Rollback.ConfirmApplied();
	}

	/// <summary>
	/// The trial is over: what was left plain during it (see <see cref="OnTrial"/>) is written again, encrypted - the key
	/// queue and every account's file. Left alone, a Family View PIN or a key queue the version before had written plain
	/// stayed plain until something saved it again, and so did every backup and settings copy made meanwhile.
	/// </summary>
	private static void Reseal() {
		KeyQueue.Flush();

		string[] files;

		try {
			files = Directory.GetFiles(ConfigStore.ConfigDir, "*.json");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("update: encrypting the accounts' secrets again", e);

			return;
		}

		foreach (string file in files) {
			string name = Path.GetFileNameWithoutExtension(file);

			if (string.Equals(name, "nocatFarm", StringComparison.OrdinalIgnoreCase) || (name.Length == 0) || (name[0] == '.')) {
				continue;
			}

			// Under the account's lock, like every save of it. A running account's own settings are saved; a file nothing runs
			// on is read again, which writes a plain secret back encrypted.
			Bot? bot = BotManager.Instance?.Get(name);

			lock (bot?.CfgGate ?? BotManager.GateFor(name)) {
				if (bot == null) {
					ConfigStore.LoadBot(name);
				} else if (File.Exists(file)) {
					ConfigStore.SaveBot(bot.Name, bot.Cfg);
				}
			}
		}
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
			// Closed during the countdown: nobody else is signed out, and the caller stops the update (ExitRequested).
			if (Commands.ExitRequested) {
				return;
			}

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

	/// <summary>Starts the swap script. Swappable so the checks can see what it would be started with, and nothing is replaced.</summary>
	internal static Action<ProcessStartInfo> StartSwap { get; set; } = static swap => Process.Start(swap)?.Dispose();

	/// <summary>Closes nocat.farm once the swap script has it. Swappable for the same reason.</summary>
	internal static Action ExitForSwap { get; set; } = Commands.RequestExit;

	/// <summary>Where it got to, for the dashboard to show.</summary>
	public static string Progress { get; private set; } = "";

	/// <summary>
	/// Download the newest release, stage it, and hand over to the swap script.
	///
	/// Returns a message to print. On success it does not return in any meaningful sense - the app is asked to
	/// shut down and the script takes over - so the caller should treat a null return as "we're going down".
	/// </summary>
	/// <param name="byItself">"Update by itself" started it, at night - the message says so.</param>
	/// <param name="fromFile">'update file': a nocat.farm zip on this PC, installed instead of the newest release - checked
	/// with <see cref="LookInZip"/> first, then everything after the download is the same: unpacked, the safety copy, the
	/// accounts signed out one at a time, the swap, the half-minute check and putting the old version back.</param>
	/// <param name="toVersion">'update to': that release instead of the newest - an older one too, which is the point. Its
	/// own zip for this machine, picked the way an update picks one.</param>
	/// <remarks>Any version older than this one - from 'update to' or a file - saves the settings first and holds off the
	/// version it came from, so that one isn't put straight back by itself (see <see cref="Rollback"/>).</remarks>
	public static async Task<string?> ApplyAsync(CancellationToken ct, bool byItself = false, string? fromFile = null, string? toVersion = null) {
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

		// The download's own folder, gone again if the update stops before the swap script takes it over.
		string? work = null;

		// Going back: the copy of the settings taken first - gone again if the swap never starts. It holds every saved login,
		// and left behind it waited for good: nothing older ever went in to drop a setting, so every start read it again, and
		// every try that stopped added another.
		string? copy = null;
		bool swapStarted = false;

		try {
			string tag, body = "", url = "";
			long size = 0;

			if (fromFile != null) {
				// Looked at again here, not only when the command was typed: the file could have changed in between.
				(string? inZip, Said problem) = LookInZip(fromFile);

				if (inZip == null) {
					return Fail(problem);
				}

				tag = inZip;
				Progress = $"installing {tag} from a file";
				Log.Good(new Said("installing nocat.farm {0} from {1}", tag, Path.GetFileName(fromFile)));
			} else {
				// Looked at again now it's this install's turn. The bedtime queue and "Update by itself" check for a skip and
				// then start this on another thread, so an 'update skip' typed in between used to be too late.
				if ((toVersion == null) && (UpdateCheck.Available is { } known) && UpdateCheck.IsSkipped(known)) {
					return SkippedStop(known);
				}

				Progress = toVersion != null ? $"asking GitHub for {toVersion}" : "asking GitHub what's newest";
				Log.Good(toVersion != null ? new Said("update: asking GitHub for {0}", toVersion) : new Said("update: asking GitHub what's newest"));

				string json;

				try {
					json = await Http.GetStringAsync(toVersion != null ? TagFeed(toVersion) : Feed, ct).ConfigureAwait(false);
				} catch (HttpRequestException e) when ((toVersion != null) && (e.StatusCode == System.Net.HttpStatusCode.NotFound)) {
					// Only the last five releases are kept: one deleted since the list was read.
					return Fail(new Said("update failed: {0} isn't on GitHub any more - 'update versions' lists what is", toVersion));
				} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
					// Where it asked, for the file: GitHub's refusal (a rate limit's 403) is in the message, status included.
					Log.Debug($"update: asking {UpdateCheck.FeedWhere()} failed: {Log.Describe(e)}");

					return Fail(new Said("update failed: couldn't reach GitHub ({0}) - nothing changed", Log.Scrub(e.Message)));
				}

				using JsonDocument doc = JsonDocument.Parse(json);
				JsonElement root = doc.RootElement;

				tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
				body = root.TryGetProperty("body", out JsonElement nb) ? nb.GetString() ?? "" : "";

				if (tag.Length == 0) {
					return Fail(toVersion != null
						? new Said("update failed: GitHub gave nothing for {0} - try again soon", toVersion)
						: new Said("update failed: GitHub gave no latest release - try again soon"));
				}

				// A version asked for by name is that version, newer or older - only not the one running.
				if (toVersion != null) {
					if (UpdateCheck.Compare(tag, Build.Version) == 0) {
						return $"already on {Build.Version} - nothing to do";
					}
				} else if (!UpdateCheck.IsNewerThanThisBuild(tag)) {
					return $"already on the newest release ({Build.Version}) - nothing to do";
				}

				if (UpdateCheck.IsSkipped(tag)) {
					return SkippedStop(tag);
				}

				// Closed while GitHub was asked: nothing downloaded for an app on its way out.
				if (Commands.ExitRequested) {
					return Fail(new Said("update stopped - nocat.farm was closed first; nothing changed"));
				}

				// The zip for this machine - not the source tarballs GitHub adds to every release by itself, and not
				// the Linux builds that sit beside the Windows one.
				if (ZipForThisMachine(root) is not ({ } found, long length)) {
					return Fail(new Said("update failed: {0} has no download yet - try again later", tag));
				}

				(url, size) = (found, length);
			}

			// Too old to tell the swap script it started: stopped here, before the settings are saved or an account signs out.
			if (TooOld(tag) is { } tooOld) {
				return Fail(tooOld);
			}

			// Another copy running from this folder (on a config folder of its own): replacing the files under it, and
			// stopping "the new version" if it didn't come up, would take that one down too. Said now, before a download.
			if (OthersRunningHere() is [int other, ..]) {
				return Fail(new Said("update stopped - another nocat.farm is running from this folder (process {0}); close it first, nothing changed", other));
			}

			// One folder per install, made fresh with a random name that only this user can open (mkdtemp): two copies
			// updating at the same minute never share a swap script, and nobody else on the machine can guess the name in
			// /tmp and put something of their own there first.
			// What an earlier update left behind - a swap that failed before it could tidy up, a machine switched off half way:
			// with a random name no later update can find its own again, so each would stay in temp for good.
			SweepOldWork(Path.GetTempPath(), TimeSpan.FromDays(1));
			work = Directory.CreateTempSubdirectory("nocatfarm-update-").FullName;

			string zip = Path.Combine(work, "release.zip");

			// When it started, for the "in 3s" the new version says it took.
			DateTime started = DateTime.UtcNow;

			if (fromFile != null) {
				// Copied into the update's own folder, like a download: the file the command named can be moved, changed or
				// deleted while the accounts sign out, and the swap script must still find exactly what was checked.
				try {
					File.Copy(fromFile, zip, true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return Fail(new Said("update failed: couldn't read {0} ({1}) - nothing changed", Path.GetFileName(fromFile), Log.Scrub(e.Message)));
				}
			} else {
				Progress = $"downloading {tag}";
				Log.Good(new Said("update: downloading {0} ({1}MB)", tag, size / 1048576));

				// By hand rather than CopyToAsync, so it can say how far along it is. A 50MB download on a slow line
				// takes minutes, and "downloading" followed by silence looked exactly like a hang.
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
			}

			// A truncated download extracts to a broken install. Check before touching anything.
			long got = new FileInfo(zip).Length;

			if ((size > 0) && (got != size)) {
				return Fail(new Said("update failed: download stopped at {0} of {1}MB - try again", got / 1048576, size / 1048576));
			}

			Log.Good(fromFile != null ? new Said("update: {0}MB copied - unpacking", got / 1048576) : new Said("update: {0}MB downloaded - unpacking", got / 1048576));
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

			// config/, logs/ and backups/ are this install's own and never go in from a zip (see the remarks above). A release
			// has none of them, but a zip made from somebody's folder ('update file') can: copied over, its settings, saved
			// logins and logs replaced these - and putting the old version back didn't bring them back. A backup is named by its
			// day, so theirs wrote over today's here. Taken out before the safety copy and the list of added files, so neither
			// knows they were there.
			try {
				foreach (string dir in Directory.GetDirectories(payload)) {
					string top = Path.GetFileName(dir);

					if (top.Equals("config", StringComparison.OrdinalIgnoreCase) || top.Equals("logs", StringComparison.OrdinalIgnoreCase)
						|| top.Equals("backups", StringComparison.OrdinalIgnoreCase)) {
						Directory.Delete(dir, true);
						Log.Info(new Said("update: left out the {0} folder in the zip - yours stays as it is", top + "/"));
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return Fail(new Said("update failed: couldn't unpack ({0}) - disk full?", Log.Scrub(e.Message)));
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

			await File.WriteAllTextAsync(script, OperatingSystem.IsWindows() ? WindowsScript(Environment.ProcessId) : UnixScript, ct).ConfigureAwait(false);

			// Skipped while it downloaded: stopped here, before a single account is signed out for nothing. Not a file: that's
			// chosen by hand, skipped or not - and a skip of the release with its number stays as it is, whatever happens to it.
			if ((fromFile == null) && UpdateCheck.IsSkipped(tag)) {
				return SkippedStop(tag);
			}

			// An older version rewrites the settings files without the settings it doesn't know. Everything is saved first -
			// here, before a single account is signed out, so a copy that can't be made stops the update with nothing changed -
			// and the missing ones come back once a version that knows them starts again (Rollback.RestoreMissing).
			bool goingBack = UpdateCheck.IsOlderThanThisBuild(tag);

			if (goingBack) {
				try {
					copy = Rollback.Snapshot(tag);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Failed("update: saving the settings before going back", e);

					return Fail(new Said("update stopped - couldn't save your settings first ({0}); nothing changed", Log.Scrub(e.Message)));
				}
			}

			Log.Publish(Topic.Installs, "nocat.farm", fromFile != null
				? new Said("Installing nocat.farm {0} from {1}. The accounts sign out one at a time; back in about a minute.", tag, Path.GetFileName(fromFile))
				: goingBack
					? new Said("Downloaded {0} ({1}MB) - going back to it now. The accounts sign out one at a time; back in about a minute.", tag, got / 1048576)
				: byItself
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
					// /s with a second pair of quotes round the path: without it cmd keeps the quotes only when nothing in the path
					// is one of ( ) & ^ @ - a Windows user called "Paul (Home)" has a temp folder like that, cmd ran "C:\Users\Paul",
					// the script never started and nocat.farm closed for good. /d: the user's own cmd start-up hooks stay out of it.
					Arguments = SwapArguments(script),
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetTempPath()
				};
			} else {
				// In a session of its own (setsid - or perl's, on a Mac, which has no setsid command) and in the background, so
				// neither this process ending nor its Terminal window or SSH session closing reaches the script or the new
				// version it starts: closing a terminal is a clean shutdown now (SIGHUP), and it mustn't stop those two. nohup
				// on top, and on its own where neither is there. It waits for this PID, like the Windows one.
				//
				// The options to start with go to the script as its own arguments, each exactly as it is. Split on spaces, a
				// --path to "nocat.farm (1)" came back as two and it started on the wrong folder.
				swap = new("/bin/sh") { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
				swap.ArgumentList.Add("-c");
				swap.ArgumentList.Add(DetachedStart);
				swap.ArgumentList.Add(script);

				foreach (string arg in RestartArgs([.. Environment.GetCommandLineArgs().Skip(1)], ConfigStore.Root)) {
					swap.ArgumentList.Add(arg);
				}

				swap.Environment["NF_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
				swap.Environment["NF_EXE"] = ProgramPath(Environment.ProcessPath, here, ExeName);

				// Started from start.command on a Mac: the new version opens the same way, in a Terminal window of its own.
				if (OpensInTerminal) {
					swap.Environment["NF_TERMINAL"] = "1";
				}
			}

			swap.Environment["NF_HERE"] = here;
			swap.Environment["NF_WORK"] = work;
			swap.Environment["NF_STAGED"] = payload;
			swap.Environment["NF_BACKUP"] = backup;
			swap.Environment["NF_FAIL"] = SwapFailedPath;
			if (OperatingSystem.IsWindows()) {
				swap.Environment["NF_ARGS"] = RelaunchArgs();
			}
			swap.Environment["NF_OK"] = okFile;
			// From a file, marked as such: if it doesn't start, it's put back like any update - but a version from a file isn't
			// skipped afterwards, or a test build that crashed would hide the real release with the same number. An older
			// version gone back to isn't skipped either, and is said as what it was - nor a newer one asked for by name, whose
			// skip goes back to what it was.
			swap.Environment["NF_TAG"] = (fromFile != null ? FromFileMark : goingBack ? BackMark : (toVersion != null) ? ToMark : "") + tag.TrimStart('v', 'V');

			// The last moment a skip can still win: the notes and the swap go under the same lock 'update skip' takes, so it
			// lands either before them and nothing is started, or after and is told it's too late - never in between.
			bool swapped = HandOver(fromFile == null ? tag : null, () => {
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

				StartSwap(swap);
				swapStarted = true;

				// Going back: the version it came from is skipped, so the older one doesn't offer it - or put it straight back
				// by itself in the night - the moment it starts. The older version reads the same file. A release newer than
				// that one is announced as usual, and 'update accept' brings this one back. Once the swap has really started:
				// one that couldn't be started leaves nothing skipped.
				// Not when the newest release is newer than this one: that's the one the older version offers, and a skip already
				// there (typed for it) stays. The skip as it was is kept, to go back if the older version doesn't go in.
				// Any version asked for by name keeps it too: one that doesn't start puts the skip back as it was.
				if (goingBack || (toVersion != null)) {
					KeepSkipBefore(tag);
				}

				if (goingBack && (Rollback.HoldOff(tag) is { } hold)) {
					UpdateCheck.Skipped = hold;
				}
			});

			if (!swapped) {
				// Closed in the moment between the check above and the handover: stopped, and nothing is signed back in.
				if (Commands.ExitRequested) {
					return Fail(new Said("update stopped - nocat.farm was closed first; nothing changed"));
				}

				SignBackIn(signedOut);

				return SkippedStop(tag);
			}

			handedOver = true;   // stays busy from here: the swap script owns the folder until this copy has gone

			ExitForSwap();

			return null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// Everything expected has its own catch above: this one is a surprise, so its stack goes in the file.
			Log.StackToFile(e);
			SignBackIn(signedOut);

			return Fail(new Said("update failed: {0} - nothing changed", Log.Scrub(e.Message)));
		} finally {
			if ((copy != null) && !swapStarted) {
				Rollback.Discard(copy);
			}

			if (!handedOver) {
				Volatile.Write(ref _busy, 0);

				if (work != null) {
					try {
						Directory.Delete(work, true);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						// the system's own temp cleaning gets it
					}
				}
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
	/// <summary>cmd's command line for the swap script: the path quoted inside the quotes /s strips, so it stays whole.</summary>
	public static string SwapArguments(string script) => $"/d /s /c \"\"{script}\"\"";

	/// <summary>
	/// The Windows swap script as cmd gets it: \r\n on every line, whatever this file's line endings were when it was built.
	/// From a checkout with \n only, cmd can miss a label it jumps to (:verify, :undo) - the mirror of 1.6.4's Unix script.
	/// </summary>
	public static string WindowsScript(int pid) => SwapScript(pid).ReplaceLineEndings("\r\n");

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
			rem The download, the unpacked copy and the safety copy go too: nothing else knows this folder's name.
			cd /d "%TEMP%"
			rmdir /s /q "%NF_WORK%" >nul 2>&1
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
		rem Only an answer of 1 means gone: PowerShell missing or blocked (9009, or a policy's refusal) isn't the new version
		rem crashing, and taking it for one put a good update back and skipped that version for good.
		if %waited% LSS 10 goto verify
		if not %check%==0 goto verify
		powershell -NoProfile -Command "exit [int](-not (Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $env:NF_HERE 'nocatFarm.exe')))" >nul 2>&1
		if errorlevel 2 goto verify
		if errorlevel 1 goto gone
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
	/// The swap on Linux and a Mac - the same steps as the Windows script, in sh. Waits for this process (NF_PID), puts the
	/// new files in, starts the new version with the options it was given (its own arguments, "$@"), and waits for its
	/// "ok". A copy that fails, "crashed", no answer in three minutes, or the new version gone without a word after ten
	/// seconds: the safety copy goes back, the files the new version added go, "crashed &lt;tag&gt;" is left in NF_FAIL,
	/// and the old version starts again. Another copy running from this folder by then: nothing is touched, "busy &lt;pid&gt;"
	/// is left in NF_FAIL, and the old version starts again.
	/// Started from start.command on a Mac (NF_TERMINAL), a version comes up in a Terminal window of its own; otherwise
	/// in the background, the dashboard being where it's seen.
	/// </summary>
	/// <remarks>
	/// Each file goes in as a copy beside it and then a rename over the old one, never written over in place. Overwritten
	/// in place, a library another process has loaded changes under it, and a Mac kills a signed program whose file changed
	/// under the signature it checked ("Killed: 9"); renamed, the old file lives on for whatever still has it open.
	///
	/// The new version is known by its process id ($!), never by a pattern: pgrep -f read the folder as a regular
	/// expression, so "nocat.farm (1)" never matched itself - a good update was taken for a crash and put back - and
	/// pkill -f couldn't stop anything there, or stopped another copy's.
	/// </remarks>
	/// <summary>
	/// The swap script as the shell gets it: one \n per line, whatever this file's own line endings were when it was
	/// built. The releases are built on Windows, where the source can have \r\n - and 1.6.4's script did: sh read every
	/// line with a \r on the end, failed on its first command, and the app it had just closed never came back.
	/// </summary>
	internal static string UnixScript => UnixSwapScript.ReplaceLineEndings("\n");

	private const string UnixSwapScript = """
		#!/bin/sh
		# nocat.farm self-update. Written by the app, run once. The options to start with are this script's arguments.
		while kill -0 "$NF_PID" 2>/dev/null; do sleep 1; done

		# Process ids of copies of nocat.farm running from this folder: by the program each one runs (/proc on Linux), or
		# on a Mac by the program its command line starts with - compared as text, never as a pattern.
		from_here() {
			if [ -e /proc/self/exe ]; then
				for d in /proc/[0-9]*; do
					t=$(readlink "$d/exe" 2>/dev/null) || continue
					if [ "$t" = "$NF_EXE" ] || [ "$t" = "$NF_HERE/nocatFarm" ]; then echo "${d#/proc/}"; fi
				done
			else
				ps -A -ww -o pid= -o args= 2>/dev/null | awk '{
					pid = $1; sub(/^[ \t]*[0-9]+[ \t]+/, "")
					for (i = 1; i <= 2; i++) {
						e = (i == 1) ? ENVIRON["NF_HERE"] "/nocatFarm" : ENVIRON["NF_EXE"]
						if (e != "" && substr($0, 1, length(e)) == e && (length($0) == length(e) || substr($0, length(e) + 1, 1) == " ")) { print pid; break }
					}
				}'
			fi
		}

		# In a Terminal window of its own, from start.command, when that's how it was started. open can't hand a program
		# any options, so they go in restart-args.txt beside it, one per line, for start.command to read back - dropped,
		# a copy started with --path came back up on the wrong folder, never said "ok", and was put back. Only a
		# start.command that knows the file gets them: an older one (the version just put back) starts in the background.
		start_it() {
			rm -f "$NF_WORK/started.pid" "$NF_HERE/restart-args.txt"
			if [ -n "$NF_TERMINAL" ] && [ -x "$NF_HERE/start.command" ] && { [ "$#" -eq 0 ] || grep -q 'restart-args.txt' "$NF_HERE/start.command" 2>/dev/null; }; then
				if [ "$#" -gt 0 ]; then printf '%s\n' "$@" > "$NF_HERE/restart-args.txt"; fi
				open -a Terminal "$NF_HERE/start.command"
			else
				# In a subshell that ends at once, so the new copy is nobody's child here: gone, it's gone - not a zombie
				# that kill -0 still finds.
				( cd "$NF_HERE" || exit 1; nohup "$NF_HERE/nocatFarm" "$@" </dev/null >/dev/null 2>&1 & echo $! > "$NF_WORK/started.pid" )
			fi
		}

		running() {
			if [ -s "$NF_WORK/started.pid" ]; then
				kill -0 "$(cat "$NF_WORK/started.pid")" 2>/dev/null
			else
				[ -n "$(from_here)" ]
			fi
		}

		# Asked to stop, then waited for. SIGTERM is a clean shutdown now, which can take a while, and the new copy holds the
		# config, its state and instance.lock until it has gone: after a fixed three seconds the old version started, found
		# the lock taken, and left - nothing running at all. A minute to go by itself, then it's killed outright.
		stop_new() {
			if [ -s "$NF_WORK/started.pid" ]; then
				p=$(cat "$NF_WORK/started.pid")
				kill "$p" 2>/dev/null
				n=0
				while kill -0 "$p" 2>/dev/null && [ "$n" -lt 60 ]; do sleep 1; n=$((n + 1)); done
				kill -9 "$p" 2>/dev/null
				n=0
				while kill -0 "$p" 2>/dev/null && [ "$n" -lt 5 ]; do sleep 1; n=$((n + 1)); done
			else
				for p in $(from_here); do kill "$p" 2>/dev/null; done
				n=0
				while [ -n "$(from_here)" ] && [ "$n" -lt 60 ]; do sleep 1; n=$((n + 1)); done
				for p in $(from_here); do kill -9 "$p" 2>/dev/null; done
				n=0
				while [ -n "$(from_here)" ] && [ "$n" -lt 5 ]; do sleep 1; n=$((n + 1)); done
			fi
		}

		# Every file in a folder into this one, each copied beside its place and then renamed over it.
		install_from() {
			(cd "$1" && find . \( -type f -o -type l \)) | while IFS= read -r f; do
				f=${f#./}
				mkdir -p "$(dirname "$NF_HERE/$f")" || exit 1
				rm -f "$NF_HERE/$f.nf-new"
				if ! { cp -pP "$1/$f" "$NF_HERE/$f.nf-new" && mv -f "$NF_HERE/$f.nf-new" "$NF_HERE/$f"; }; then
					rm -f "$NF_HERE/$f.nf-new"
					exit 1
				fi
			done
		}

		put_back() {
			stop_new
			install_from "$NF_BACKUP"
			if [ -f "$NF_WORK/added.txt" ]; then
				while IFS= read -r f; do [ -n "$f" ] && rm -f "$NF_HERE/$f"; done < "$NF_WORK/added.txt"
			fi
			chmod +x "$NF_HERE/nocatFarm" 2>/dev/null
		}

		# Started from this folder while the accounts were signing out: replacing its files would pull them out from under it.
		other=$(from_here | head -n 1)
		if [ -n "$other" ]; then
			echo "busy $other" > "$NF_FAIL"
			start_it "$@"
			cd / && rm -rf "$NF_WORK"
			exit 1
		fi

		if ! install_from "$NF_STAGED"; then
			put_back
			echo 8 > "$NF_FAIL"
			start_it "$@"
			# The download, the unpacked copy and the safety copy go too: nothing else knows this folder's name.
			cd / && rm -rf "$NF_WORK"
			exit 1
		fi
		chmod +x "$NF_HERE/nocatFarm" "$NF_HERE/start.command" 2>/dev/null

		start_it "$@"
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
			start_it "$@"
		fi

		cd / && rm -rf "$NF_WORK"
		""";

	/// <summary>
	/// Delete the update folders (nocatfarm-update-*) in <paramref name="temp"/> untouched for longer than
	/// <paramref name="age"/>. A swap runs for minutes, so one that old is finished with; one this user can't delete (another
	/// user's, in a shared /tmp) is left alone. How many went.
	/// </summary>
	public static int SweepOldWork(string temp, TimeSpan age) {
		int removed = 0;

		try {
			foreach (DirectoryInfo old in new DirectoryInfo(temp).EnumerateDirectories("nocatfarm-update-*")) {
				try {
					// A link someone else made is never followed, and one still in use isn't touched.
					if (((old.Attributes & FileAttributes.ReparsePoint) != 0) || (DateTime.UtcNow - old.LastWriteTimeUtc < age)) {
						continue;
					}

					old.Delete(true);
					removed++;
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.Debug($"update: couldn't remove the old update folder {old.FullName}: {Log.Describe(e)}");
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug($"update: couldn't look for old update folders in {temp}: {Log.Describe(e)}");
		}

		return removed;
	}

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
	internal static string RelaunchArgs() => string.Join(' ', RestartArgs([.. Environment.GetCommandLineArgs().Skip(1)], ConfigStore.Root).Select(QuoteArg));

	/// <summary>
	/// The arguments for the restarted copy: the same ones, with --path's folder made absolute. The restart starts in the
	/// install folder, so "--path data" typed in another folder came back up on an empty data folder next to the program -
	/// every account and setting gone, as far as it could see.
	/// </summary>
	internal static List<string> RestartArgs(IReadOnlyList<string> args, string root) {
		List<string> list = [.. args];

		for (int i = 0; i < list.Count - 1; i++) {
			if (list[i].Equals("--path", StringComparison.OrdinalIgnoreCase)) {
				list[++i] = root;
			}
		}

		return list;
	}

	/// <summary>
	/// Starts the swap script ($0, with the options to restart with as its arguments) detached: its own session where
	/// there's a way to give it one, in the background, and deaf to SIGHUP.
	/// </summary>
	/// <remarks>
	/// One \n per line, like <see cref="UnixScript"/>: built on Windows, the text below has \r\n, and sh read the \r at the
	/// end of each line as a command of its own, and printed "not found" errors on the console at every update.
	/// </remarks>
	internal static string DetachedStart => DetachedStartScript.ReplaceLineEndings("\n");

	private const string DetachedStartScript = """
		if command -v setsid >/dev/null 2>&1; then setsid nohup /bin/sh "$0" "$@" </dev/null >/dev/null 2>&1 &
		elif command -v perl >/dev/null 2>&1; then perl -MPOSIX -e 'POSIX::setsid(); exec @ARGV or exit 1' nohup /bin/sh "$0" "$@" </dev/null >/dev/null 2>&1 &
		else nohup /bin/sh "$0" "$@" </dev/null >/dev/null 2>&1 &
		fi
		""";

	/// <summary>One argument, quoted when it has to be. A trailing backslash is doubled so it can't escape the closing quote.</summary>
	internal static string QuoteArg(string arg) {
		if ((arg.Length > 0) && (arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')']) < 0)) {
			return arg;
		}

		string inner = arg.Replace("\"", "\\\"");

		return "\"" + (inner.EndsWith('\\') ? inner + "\\" : inner) + "\"";
	}
}
