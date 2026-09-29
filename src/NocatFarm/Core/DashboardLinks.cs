using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Where the dashboard can be opened from: this PC, the home network (a phone on the same wifi), and outside the home
/// when a public address has been set up. For the 'dashboard' command, Telegram's /dashboard and the phone QR code.
/// </summary>
public static class DashboardLinks {
	/// <param name="Local">On this PC.</param>
	/// <param name="Home">On the home network - this PC's addresses there. Only reachable when <paramref name="OpenAtHome"/>.</param>
	/// <param name="OpenAtHome">Listening beyond this PC, with a password - both are needed for another device to get in.</param>
	/// <param name="Outside">The public address typed into "Public address", or null.</param>
	/// <param name="FirewallBlocks">Windows Firewall has no rule letting other devices in - they'd load for ever.
	/// Only ever true on Windows, listening beyond this PC, when it could be read.</param>
	/// <param name="NeedsHomeAddress">In Docker, with no NOCATFARM_HOME_ADDRESS: the phone link can't be worked out from inside.</param>
	/// <param name="NeedsRestart">"Listen on" or "Port" changed, and the dashboard still listens the old way until it restarts.</param>
	/// <param name="RemoteOn">"Open from anywhere" is switched on.</param>
	/// <param name="RemoteProblem">Why "Open from anywhere" isn't working, or null.</param>
	public sealed record Links(string Local, IReadOnlyList<string> Home, bool OpenAtHome, bool HasPassword, bool ListensBeyondThisPc, string? Outside,
		bool FirewallBlocks = false, bool NeedsHomeAddress = false, bool NeedsRestart = false, bool RemoteOn = false, string? RemoteProblem = null) {
		/// <summary>A phone on the same wifi can open <see cref="Home"/> right now - nothing is left to do.</summary>
		public bool ReadyAtHome => OpenAtHome && (Home.Count > 0) && !NeedsRestart && !FirewallBlocks && !NeedsHomeAddress;
	}

	/// <summary>What the phone still needs before it can open the dashboard at home, in the order to do it - the Phone
	/// page's checklist, the 'dashboard' command and /dashboard on Telegram and Discord all say the same.</summary>
	public static List<Said> Todo(Links l) {
		List<Said> todo = [];

		if (!l.HasPassword) {
			todo.Add(new Said("set a dashboard password"));
		}

		if (!l.ListensBeyondThisPc) {
			todo.Add(new Said("turn on Open to other devices"));
		}

		if (l.NeedsRestart) {
			todo.Add(new Said("restart the dashboard, so the change takes effect"));
		}

		if (l.OpenAtHome && l.NeedsHomeAddress) {
			todo.Add(new Said("set NOCATFARM_HOME_ADDRESS in docker-compose.yml to this computer's address"));
		}

		if (l.FirewallBlocks) {
			todo.Add(new Said("let it through Windows Firewall"));
		}

		return todo;
	}

	/// <summary>One line of the links: what it's for, the link (when there is one) and a note after it.</summary>
	public sealed record Row(Said Label, string? Link, Said Note);

	/// <summary>
	/// The links in words, for the console and the chats: at home, away from home, on this PC - then what's missing.
	/// Each caller only dresses it (plain text, Telegram's HTML, Discord's markdown), so they can't drift apart.
	/// </summary>
	public static List<Row> Rows(Links l, int minPassword) {
		Said notReady = new Said("not ready yet - see below");
		List<Row> rows = [];

		if (l.OpenAtHome && l.NeedsHomeAddress) {
			rows.Add(new Row(new Said("At home (same Wi-Fi):"), null, new Said("this computer's address, with the port Docker publishes")));
		} else {
			rows.Add(new Row(new Said("At home (same Wi-Fi):"), l.Home.Count > 0 ? l.Home[0] : null, l.ReadyAtHome ? default : notReady));
		}

		rows.Add(l.Outside != null
			? new Row(new Said("Away from home:"), l.Outside, l.ReadyAtHome ? new Said("test it on mobile data, with Wi-Fi off") : notReady)
			: new Row(new Said("Away from home:"), null, l.RemoteProblem is { } why ? new Said("not working - {0}", why)
				: l.RemoteOn ? new Said("asking your router to forward the port...")
				: new Said("off - turn on Open from anywhere on the Phone page (it needs a password of {0}+ characters)", minPassword)));

		rows.Add(new Row(new Said("On the PC itself:"), l.Local, default));

		return rows;
	}

	private static (int Port, bool? Allows, DateTime At) _firewall = (0, null, DateTime.MinValue);

	/// <summary>Windows Firewall's answer, remembered for a few seconds: reading every rule isn't free, and the Phone
	/// page asks every few seconds.</summary>
	private static bool? FirewallAllows(int port) {
		(int Port, bool? Allows, DateTime At) seen = _firewall;

		if ((seen.Port == port) && (DateTime.UtcNow - seen.At < TimeSpan.FromSeconds(3))) {
			return seen.Allows;
		}

		bool? now = OperatingSystem.IsWindows() ? Windows.Firewall.AllowsPort(port) : null;
		_firewall = (port, now, DateTime.UtcNow);

		return now;
	}

	public static Links For(GlobalConfig g) {
		int port = g.WebPort;
		string host = (g.WebHost ?? "").Trim();
		bool anyAddress = host is "0.0.0.0" or "*" or "+" or "::" or "[::]";
		bool oneAddress = !anyAddress && IPAddress.TryParse(host, out IPAddress? only) && !IPAddress.IsLoopback(only);
		bool hasPassword = !string.IsNullOrEmpty(g.WebPassword);

		// Listening on one particular address means that's the only one a phone can use. In a container, the addresses
		// it can see are Docker's own (172.x) - never the computer's - so only the one it was told is any use.
		List<string> home = Platform.HomeAddress is { } told ? (Outside(told, port) is { } h ? [h] : [])
			: Platform.InContainer ? []
			: oneAddress ? [$"http://{host}:{port}/"]
			: [.. HomeAddresses().Select(ip => $"http://{ip}:{port}/")];

		return new Links($"http://127.0.0.1:{port}/", home, hasPassword && (anyAddress || oneAddress), hasPassword, anyAddress || oneAddress,
			Outside(g.WebPublicAddress, port) ?? RemoteAccess.Link,
			OperatingSystem.IsWindows() && !Platform.IsLoopback(g.WebHost ?? "") && (FirewallAllows(port) == false),
			Platform.InContainer && (Platform.HomeAddress == null),
			Web.WebHost.Current?.NeedsRestart ?? false,
			g.WebRemoteAccess, g.WebRemoteAccess ? RemoteAccess.Problem : null);
	}

	/// <summary>
	/// This PC's addresses on private networks (192.168.x.x, 10.x.x.x, 172.16-31.x.x), the ones with a router first -
	/// a VPN or virtual adapter's address is no use to a phone.
	/// </summary>
	private static IEnumerable<string> HomeAddresses() {
		List<(string Ip, bool Routed)> found = [];

		try {
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
				if ((nic.OperationalStatus != OperationalStatus.Up) || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) {
					continue;
				}

				IPInterfaceProperties props = nic.GetIPProperties();
				bool routed = props.GatewayAddresses.Any(static gw => (gw.Address.AddressFamily == AddressFamily.InterNetwork) && !gw.Address.Equals(IPAddress.Any));

				foreach (UnicastIPAddressInformation a in props.UnicastAddresses) {
					if ((a.Address.AddressFamily == AddressFamily.InterNetwork) && IsPrivate(a.Address)) {
						found.Add((a.Address.ToString(), routed));
					}
				}
			}
		} catch (NetworkInformationException e) {
			// No list of adapters - the local link still works. The Phone page asks every few seconds: said once.
			Log.DebugOnChange("links:cards", $"dashboard links: couldn't list the network cards: {Log.Describe(e)}");
		}

		return found.OrderByDescending(static f => f.Routed).Select(static f => f.Ip).Distinct();
	}

	private static bool IsPrivate(IPAddress ip) {
		byte[] b = ip.GetAddressBytes();

		return (b[0] == 10) || ((b[0] == 172) && (b[1] >= 16) && (b[1] <= 31)) || ((b[0] == 192) && (b[1] == 168));
	}

	/// <summary>"myname.duckdns.org" becomes http://myname.duckdns.org:7242/ - the port is added when it wasn't typed.</summary>
	private static string? Outside(string? typed, int port) {
		string t = (typed ?? "").Trim().TrimEnd('/');

		if (t.Length == 0) {
			return null;
		}

		int at = t.IndexOf("://", StringComparison.Ordinal);
		string scheme = at < 0 ? "http" : t[..at];
		string rest = at < 0 ? t : t[(at + 3)..];

		if (!rest.Contains(':')) {
			rest += ":" + port;
		}

		string link = $"{scheme}://{rest}/";

		return Uri.TryCreate(link, UriKind.Absolute, out _) ? link : null;
	}

	/// <summary>The one line above the list of what's missing.</summary>
	public static Said TodoHeading => new Said("Still to do - on the PC, open the dashboard's Phone page, each is one tap there:");

	/// <summary>The line under it all: the page with the codes to scan.</summary>
	public static Said ScanHint => new Said("Or scan it: the dashboard's Phone page has QR codes.");

	/// <summary>The links as the console and Steam chat show them - English, like every command reply.</summary>
	public static string Text(GlobalConfig g) => Text(For(g));

	internal static string Text(Links l) {
		List<Row> rows = Rows(l, RemoteAccess.MinPasswordLength);
		int width = rows.Max(static r => r.Label.ToEnglish().Length) + 2;
		List<string> lines = ["The dashboard:"];

		foreach (Row r in rows) {
			string note = r.Note.IsEmpty ? "" : r.Link == null ? r.Note.ToEnglish() : $"   ({r.Note.ToEnglish()})";
			lines.Add($"  {r.Label.ToEnglish().PadRight(width)}{r.Link}{note}");

			// The other addresses of this PC on the home network - a second adapter, say.
			if ((rows.IndexOf(r) == 0) && (r.Link != null)) {
				lines.AddRange(l.Home.Skip(1).Select(h => $"  {"".PadRight(width)}{h}"));
			}
		}

		List<Said> todo = Todo(l);

		if (todo.Count > 0) {
			lines.Add("");
			lines.Add($"  {TodoHeading.ToEnglish()}");
			lines.AddRange(todo.Select(static t => $"    - {t.ToEnglish()}"));
		}

		lines.Add($"  {ScanHint.ToEnglish()}");

		return string.Join(Environment.NewLine, lines);
	}
}
