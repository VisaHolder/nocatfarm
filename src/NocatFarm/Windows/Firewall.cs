using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace NocatFarm.Windows;

/// <summary>
/// Whether Windows Firewall lets a phone reach the dashboard, and - only when asked, with Windows' own "allow this
/// app to make changes" prompt - a rule that lets it.
/// </summary>
/// <remarks>
/// Putting 0.0.0.0 in "Listen on" is not enough by itself: the firewall blocks connections from other devices, and
/// on many PCs it's set not to ask, so a phone just loads for ever with nothing on the PC saying why. The rule opens
/// only the dashboard's port, only on home and work networks (not public wifi), and is named so it can be found and
/// removed in Windows' own firewall settings.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Firewall {
	public const string RuleName = "nocat.farm dashboard";

	private const int Inbound = 1;
	private const int Allow = 1;
	private const int PrivateProfile = 2;

	/// <summary>
	/// True when an enabled inbound rule lets the port in on a home network, false when none does, null when it
	/// can't tell (the firewall's settings couldn't be read). Reading needs no administrator rights.
	/// </summary>
	public static bool? AllowsPort(int port) {
		try {
			if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type) {
				return null;
			}

			dynamic policy = Activator.CreateInstance(type)!;

			// The firewall switched off for home networks lets everything in.
			try {
				if (!(bool) policy.FirewallEnabled[PrivateProfile]) {
					return true;
				}
			} catch {
				// can't tell - look at the rules anyway
			}

			string exe = Environment.ProcessPath ?? "";

			foreach (dynamic rule in policy.Rules) {
				if (!(bool) rule.Enabled || ((int) rule.Direction != Inbound) || ((int) rule.Action != Allow)
					|| (((int) rule.Profiles & PrivateProfile) == 0) || ((int) rule.Protocol is not (Tcp or AnyProtocol))) {
					continue;
				}

				string app = (string?) rule.ApplicationName ?? "";
				string ports = (string?) rule.LocalPorts ?? "";

				// Only a rule that is plainly for us: this exe (any port), or this port for any program. Windows' own
				// rules for Store apps look like "everything, any program" but are tied to their app package, and
				// counting them said "not blocked" on a PC where the phone just loaded for ever.
				if (string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)
					|| ((app.Length == 0) && (ports.Length > 0) && (ports != "*") && PortMatches(ports, port) && !Scoped(rule))) {
					return true;
				}
			}

			return false;
		} catch {
			return null;
		}
	}

	private const int Tcp = 6;
	private const int AnyProtocol = 256;

	/// <summary>A rule that belongs to a Windows service or a Store app package, not to whatever program asks.</summary>
	private static bool Scoped(dynamic rule) {
		try {
			if (!string.IsNullOrEmpty((string?) rule.serviceName)) {
				return true;
			}
		} catch {
			// not every rule has it
		}

		try {
			return !string.IsNullOrEmpty((string?) rule.LocalAppPackageId);
		} catch {
			return false;
		}
	}

	/// <summary>"*", "7242", "80,7242" or "7000-8000".</summary>
	internal static bool PortMatches(string ports, int port) {
		foreach (string part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (part == "*") {
				return true;
			}

			string[] range = part.Split('-');

			if ((range.Length == 2) && int.TryParse(range[0], out int lo) && int.TryParse(range[1], out int hi) && (port >= lo) && (port <= hi)) {
				return true;
			}

			if (int.TryParse(part, out int one) && (one == port)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Add the rule, replacing an older one for a different port. Windows asks the person at this PC to allow it;
	/// saying no leaves everything as it was.
	/// </summary>
	public static async Task<(bool Ok, string Why)> AllowAsync(int port) {
		// One prompt for both: take away an older rule (a different port), then add this one.
		string args = $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul & netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain";

		try {
			using Process? p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args) {
				UseShellExecute = true,
				Verb = "runas",
				WindowStyle = ProcessWindowStyle.Hidden
			});

			if (p == null) {
				return (false, "Windows didn't start it");
			}

			await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);

			return AllowsPort(port) == false ? (false, "the rule didn't take") : (true, "");
		} catch (Win32Exception e) when (e.NativeErrorCode == 1223) {
			return (false, "you said no on the Windows prompt - nothing was changed");
		} catch (Exception e) {
			return (false, e.Message);
		}
	}
}
