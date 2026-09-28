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
	public sealed record Links(string Local, IReadOnlyList<string> Home, bool OpenAtHome, bool HasPassword, bool ListensBeyondThisPc, string? Outside);

	public static Links For(GlobalConfig g) {
		int port = g.WebPort;
		string host = (g.WebHost ?? "").Trim();
		bool anyAddress = host is "0.0.0.0" or "*" or "+" or "::" or "[::]";
		bool oneAddress = !anyAddress && IPAddress.TryParse(host, out IPAddress? only) && !IPAddress.IsLoopback(only);
		bool hasPassword = !string.IsNullOrEmpty(g.WebPassword);

		// Listening on one particular address means that's the only one a phone can use.
		List<string> home = oneAddress ? [$"http://{host}:{port}/"] : [.. HomeAddresses().Select(ip => $"http://{ip}:{port}/")];

		return new Links($"http://127.0.0.1:{port}/", home, hasPassword && (anyAddress || oneAddress), hasPassword, anyAddress || oneAddress,
			Outside(g.WebPublicAddress, port));
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
		} catch (NetworkInformationException) {
			// No list of adapters - the local link still works.
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

	/// <summary>The links as the console and Steam chat show them - English, like every command reply.</summary>
	public static string Text(GlobalConfig g) {
		Links l = For(g);
		List<string> lines = ["The dashboard:", $"  on this PC:      {l.Local}"];

		if (l.OpenAtHome && (l.Home.Count > 0)) {
			lines.Add($"  on your phone:   {l.Home[0]}   (on the same wifi - sign in with the dashboard password)");
			lines.AddRange(l.Home.Skip(1).Select(static h => $"                   {h}"));
		} else {
			string phone = l.Home.Count > 0 ? l.Home[0] : $"http://<this PC's address>:{g.WebPort}/";
			lines.Add($"  on your phone:   not open to other devices yet. Set a Dashboard password{(l.ListensBeyondThisPc ? "" : " and put 0.0.0.0 in Listen on")}");
			lines.Add($"                   (Settings > Dashboard, Show advanced), restart, then open {phone}");
		}

		lines.Add(l.Outside != null
			? $"  from anywhere:   {l.Outside}   (anyone with this and the password controls every account)"
			: "  from anywhere:   not set up - forward the port on your router to this PC and put your address in Public address");

		return string.Join(Environment.NewLine, lines);
	}
}
