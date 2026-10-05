using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NocatFarm.Config;

/// <summary>
/// The computer's own language, for a brand-new install's first config: a German Windows starts in German without
/// anybody finding the setting. Only ever asked when there's no config yet - an existing one keeps what it says, and the
/// Windows installer's own pick (--setup Language=) is applied after this, so it still wins.
/// </summary>
/// <remarks>
/// InvariantGlobalization is on, so CultureInfo can't say it: Windows is asked directly, everything else reads the
/// usual locale variables.
/// </remarks>
public static class SystemLanguage {
	/// <summary>The languages there's a pack for (and "en", which needs none).</summary>
	public static readonly string[] Packs = ["en", "es", "pt-BR", "ru", "de", "fr", "zh-CN", "tr", "pl", "ja", "ko"];

	/// <summary>The locale variables, strongest first - as POSIX reads them for messages.</summary>
	public static readonly string[] Variables = ["LC_ALL", "LC_MESSAGES", "LANG"];

	/// <summary>What this computer is set to, as a pack code; null when it doesn't say. Tests put their own here.</summary>
	public static Func<string?> Probe { get; set; } = Detect;

	/// <summary>
	/// A locale name - "de-DE" from Windows, "de_DE.UTF-8" from LANG, "zh-Hans-CN" from a Mac - as a pack code. Null for
	/// nothing at all; "en" for a language there's no pack for, and for "C" and "POSIX".
	/// </summary>
	public static string? FromLocale(string? locale) {
		if (string.IsNullOrWhiteSpace(locale)) {
			return null;
		}

		// The codeset and modifier aren't the language: "de_DE.UTF-8", "sr_RS@latin". A colon list takes its first.
		string name = locale.Trim().Trim('"').Split(':')[0];
		int cut = name.IndexOfAny(['.', '@']);

		if (cut >= 0) {
			name = name[..cut];
		}

		if (name.Length == 0 || name.Equals("C", StringComparison.OrdinalIgnoreCase) || name.Equals("POSIX", StringComparison.OrdinalIgnoreCase)) {
			return "en";
		}

		string[] parts = name.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
		string lang = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

		switch (lang) {
			case "pt":
				return "pt-BR";
			case "zh":
				// Simplified: zh-Hans, zh-CN, zh-SG, and a bare "zh". Traditional (zh-Hant, zh-TW, zh-HK, zh-MO) has no pack
				// of its own, and English reads better there than the other script. A script said outright beats the region:
				// a Mac set to Simplified Chinese in Hong Kong is "zh-Hans-HK", and that got English.
				string[] rest = [.. parts.Skip(1).Select(static p => p.ToUpperInvariant())];

				return rest.Contains("HANS") ? "zh-CN"
					: rest.Contains("HANT") || rest.Contains("TW") || rest.Contains("HK") || rest.Contains("MO") ? "en" : "zh-CN";
		}

		return Packs.FirstOrDefault(p => p.Equals(lang, StringComparison.OrdinalIgnoreCase)) ?? "en";
	}

	/// <summary>
	/// LC_ALL, then LC_MESSAGES, then LANG: the first one that's set decides, even on a language with no pack (LC_ALL
	/// says what the user wants over LANG). Null when none is set.
	/// </summary>
	public static string? FromEnvironment(Func<string, string?> read) {
		foreach (string variable in Variables) {
			if (FromLocale(read(variable)) is { } code) {
				return code;
			}
		}

		return null;
	}

	/// <summary>The first language of `defaults read -g AppleLanguages` - a list like ( "de-DE", en ) - as a pack code.</summary>
	public static string? FromAppleLanguages(string? output) {
		if (string.IsNullOrWhiteSpace(output)) {
			return null;
		}

		foreach (string line in output.Split('\n')) {
			string item = line.Trim().TrimEnd(',').Trim().Trim('"');

			if (item.Length > 0 && item is not "(" and not ")") {
				return FromLocale(item);
			}
		}

		return null;
	}

	/// <summary>A pack code as the language calls itself - "Deutsch" for de - from the Language setting's own list.</summary>
	public static string NameOf(string code) =>
		Settings.FindGlobal("Language") is { } def
			&& Settings.ParsePicks(def).FirstOrDefault(p => p.Value.Equals(code, StringComparison.OrdinalIgnoreCase)) is { Label: { } name }
			? name : code;

	/// <summary>This computer's language as a pack code, or null when it doesn't say. Never throws.</summary>
	public static string? Detect() {
		try {
			if (OperatingSystem.IsWindows()) {
				return FromLocale(WindowsUiLocale());
			}

			return FromEnvironment(Environment.GetEnvironmentVariable)
				?? (OperatingSystem.IsMacOS() ? FromAppleLanguages(AppleLanguages()) : null);
		} catch {
			// a language guess is never worth a failed start
			return null;
		}
	}

	[SupportedOSPlatform("windows")]
	private static string? WindowsUiLocale() {
		char[] name = new char[85];   // LOCALE_NAME_MAX_LENGTH
		int written = LCIDToLocaleName(GetUserDefaultUILanguage(), name, name.Length, 0);

		return written > 1 ? new string(name, 0, written - 1) : null;
	}

	/// <summary>The Mac's language list. A couple of seconds at most - a first start never waits on it longer.</summary>
	private static string? AppleLanguages() {
		const string defaults = "/usr/bin/defaults";

		if (!File.Exists(defaults)) {
			return null;
		}

		using Process? p = Process.Start(new ProcessStartInfo(defaults, ["read", "-g", "AppleLanguages"]) {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		});

		if (p == null) {
			return null;
		}

		Task<string> output = p.StandardOutput.ReadToEndAsync();
		_ = p.StandardError.ReadToEndAsync();

		if (!p.WaitForExit(2000)) {
			try {
				p.Kill();
			} catch {
				// it ended on its own in the meantime
			}

			return null;
		}

		return (p.ExitCode == 0) && output.Wait(500) ? output.Result : null;
	}

	[DllImport("kernel32.dll")]
	private static extern ushort GetUserDefaultUILanguage();

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int LCIDToLocaleName(uint locale, [Out] char[] name, int length, uint flags);
}
