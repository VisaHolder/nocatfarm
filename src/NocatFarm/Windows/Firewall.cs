using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using NocatFarm.Core;

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
	private const int DomainProfile = 1;
	private const int PrivateProfile = 2;
	private const int PublicProfile = 4;
	private const int Block = 0;

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
			int current = CurrentProfiles(policy);

			// The firewall switched off for the network in use lets everything in.
			try {
				bool on = false;

				foreach (int profile in new[] { DomainProfile, PrivateProfile, PublicProfile }) {
					bool enabled = policy.FirewallEnabled[profile];
					on |= ((current & profile) != 0) && enabled;
				}

				if (!on) {
					return true;
				}
			} catch {
				// can't tell - look at the rules anyway
			}

			string exe = Environment.ProcessPath ?? "";
			bool allowed = false;

			foreach (dynamic rule in policy.Rules) {
				// A rule only counts for the network Windows is on right now - a home-network rule does nothing on a
				// network it has marked Public.
				if (!(bool) rule.Enabled || ((int) rule.Direction != Inbound)
					|| (((int) rule.Profiles & current) == 0) || ((int) rule.Protocol is not (Tcp or AnyProtocol))) {
					continue;
				}

				string app = (string?) rule.ApplicationName ?? "";
				string ports = (string?) rule.LocalPorts ?? "";

				// Only a rule that is plainly for us: this exe (any port), or this port for any program. Windows' own
				// rules for Store apps look like "everything, any program" but are tied to their app package, and
				// counting them said "not blocked" on a PC where the phone just loaded for ever.
				if (!string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)
					&& !((app.Length == 0) && (ports.Length > 0) && (ports != "*") && PortMatches(ports, port) && !Scoped(rule))) {
					continue;
				}

				// A Block rule beats every Allow rule - the one Windows leaves after "Cancel" on its own prompt, say.
				if ((int) rule.Action == Block) {
					return false;
				}

				allowed = true;
			}

			return allowed;
		} catch {
			return null;
		}
	}

	/// <summary>The network kinds in use now (domain 1, private 2, public 4); private when it can't be read.</summary>
	private static int CurrentProfiles(dynamic policy) {
		try {
			return (int) policy.CurrentProfileTypes;
		} catch {
			return PrivateProfile;
		}
	}

	/// <summary>True when Windows has the network marked Public - the rule is for home networks only, on purpose.</summary>
	public static bool OnPublicNetwork() {
		try {
			// Every active network's type at once: a VPN or a virtual adapter marked Public doesn't count while the home
			// network (Private, or a work Domain) is there too.
			if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type) {
				return false;
			}

			int profiles = CurrentProfiles(Activator.CreateInstance(type)!);

			return ((profiles & PublicProfile) != 0) && ((profiles & (PrivateProfile | DomainProfile)) == 0);
		} catch {
			return false;
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
		// On a network Windows calls Public the rule wouldn't apply - saying so beats "done" and a phone that still can't.
		if (OnPublicNetwork()) {
			return (false, new Said("Windows has this network set to Public, where the rule doesn't apply. Set it to Private (Settings > Network & internet > your network), then press it again.").ToString());
		}

		// One prompt for all of it: clear this program's own inbound rules (a Block rule left by "Cancel" on Windows'
		// prompt wins over any Allow), take away an older rule for a different port, then add this one.
		string exe = Environment.ProcessPath ?? "";
		string clearOwn = exe.Length > 0 ? $"netsh advfirewall firewall delete rule name=all dir=in program=\"{exe}\" >nul & " : "";
		string args = $"/c {clearOwn}netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul & netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain";

		try {
			using Process? p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args) {
				UseShellExecute = true,
				Verb = "runas",
				WindowStyle = ProcessWindowStyle.Hidden
			});

			if (p == null) {
				return (false, new Said("Windows didn't start it").ToString());
			}

			await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);

			return AllowsPort(port) == false ? (false, new Said("the rule didn't take").ToString()) : (true, "");
		} catch (Win32Exception e) when (e.NativeErrorCode == 1223) {
			return (false, new Said("you said no on the Windows prompt - nothing was changed").ToString());
		} catch (Exception e) {
			return (false, e.Message);
		}
	}
}
