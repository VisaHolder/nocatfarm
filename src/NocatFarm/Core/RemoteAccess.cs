using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// "Open from anywhere": asks the router to forward the dashboard's port to this PC (UPnP - what Jellyfin, Plex and
/// torrent apps do), and learns the internet address, so the dashboard opens from outside the home with no other
/// tool. Off unless switched on, and never without a dashboard password long enough to hold up to the internet.
/// </summary>
/// <remarks>
/// The forward is renewed every half hour with a lease of an hour, so a PC that's switched off - or nocat.farm
/// removed - doesn't leave the port forwarded for ever. Routers that only take permanent forwards get one, and it's
/// taken away again when the switch is turned off. Turned off, the forward is removed straight away.
/// </remarks>
public static partial class RemoteAccess {
	/// <summary>The shortest dashboard password it opens to the internet with.</summary>
	public const int MinPasswordLength = 12;

	private const string Description = "nocat.farm dashboard";
	private const string AddMapping = "AddPortMapping";
	private const string DeleteMapping = "DeletePortMapping";
	private const string AskExternalIp = "GetExternalIPAddress";

	/// <summary>A router description to use instead of asking the network - for the tests' stand-in router only.</summary>
	internal static Uri? GatewayForTests { get; set; }
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

	private static Timer? _timer;
	private static int _busy;
	private static volatile bool _stopped;

	/// <summary>The forward in place: the router's control address, and the port forwarded.</summary>
	private static (Uri Control, string Service, int Port, string LanIp)? _mapped;

	private static DateTime _renewedAt = DateTime.MinValue;

	/// <summary>The internet address, once the router has said it.</summary>
	public static string? ExternalIp { get; private set; }

	/// <summary>Why it isn't open, in words, when it was asked to be and isn't. Null when it's working or not asked for.</summary>
	public static string? Problem { get; private set; }

	/// <summary>The dashboard's address from anywhere, while the forward is in place.</summary>
	public static string? Link => (_mapped is { } m) && (ExternalIp != null) ? $"http://{ExternalIp}:{m.Port}/" : null;

	public static void Start() {
		_timer = new Timer(static _ => _ = TickAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
	}

	/// <summary>Look now rather than at the next tick - after the switch or the password changed.</summary>
	public static void Poke() => _ = TickAsync();

	/// <summary>Why it can't be opened with the settings as they are, or null when it can.</summary>
	public static Said? Blocker(GlobalConfig g) =>
		string.IsNullOrEmpty(g.WebPassword) || (g.WebPassword.Length < MinPasswordLength)
			? new Said("needs a dashboard password of at least {0} characters first - anyone on the internet can try it", MinPasswordLength)
			: Platform.IsLoopback(g.WebHost ?? "")
				? new Said("needs 0.0.0.0 in Listen on (and a restart) first")
				: null;

	private static async Task TickAsync() {
		if (Interlocked.Exchange(ref _busy, 1) == 1) {
			return;
		}

		try {
			GlobalConfig g = Live.Global;
			bool wanted = g.WebRemoteAccess && g.WebEnabled;

			if (!wanted) {
				Problem = null;

				if (_mapped != null) {
					await RemoveAsync().ConfigureAwait(false);
				}

				return;
			}

			if (Blocker(g) is { } why) {
				if (Problem != why.ToString()) {
					Problem = why.ToString();
					Log.Warn(new Said("open from anywhere: {0}", why));
				}

				if (_mapped != null) {
					await RemoveAsync().ConfigureAwait(false);
				}

				return;
			}

			// A changed port means a new forward; otherwise renew every half hour.
			if ((_mapped is { } m) && (m.Port == g.WebPort) && (DateTime.UtcNow - _renewedAt < TimeSpan.FromMinutes(30))) {
				return;
			}

			if ((_mapped is { } old) && (old.Port != g.WebPort)) {
				await RemoveAsync().ConfigureAwait(false);
			}

			await MapAsync(g.WebPort).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Debug(new Said("open from anywhere: {0}", e.Message));
		} finally {
			Volatile.Write(ref _busy, 0);
		}
	}

	private static async Task MapAsync(int port) {
		bool first = _mapped == null;
		(Uri control, string service, string lanIp)? gw = await FindGatewayAsync().ConfigureAwait(false);

		if (gw is not { } found) {
			Fail(new Said("router didn't answer - turn on UPnP, or forward port {0}", port));

			return;
		}

		// An hour, renewed every half hour; some routers only take a forward that never runs out (error 725).
		string? refused = await SoapAsync(found.control, found.service, AddMapping, AddArgs(port, found.lanIp, 3600)).ConfigureAwait(false);

		if ((refused != null) && refused.Contains("725", StringComparison.Ordinal)) {
			refused = await SoapAsync(found.control, found.service, AddMapping, AddArgs(port, found.lanIp, 0)).ConfigureAwait(false);
		}

		if (refused != null) {
			Fail(new Said("router refused port {0} ({1}) - forward it by hand", port, refused));

			return;
		}

		string? reply = await SoapReplyAsync(found.control, found.service, AskExternalIp, "").ConfigureAwait(false);
		string ip = reply == null ? "" : ExternalIpRegex().Match(reply).Groups[1].Value;
		IPAddress.TryParse(ip, out IPAddress? ext);
		Said? unreachable = (ext == null) || Unusable(ext) ? new Said("your router didn't say its internet address")
			: IsProviderShared(ext) ? new Said("the provider shared address {0} can't be reached from outside", ip)
			: IsPrivate(ext) ? new Said("behind another router ({0}) - forward port {1} there too", ip, port)
			: null;

		if (unreachable is { } why) {
			Fail(why);
			await SoapAsync(found.control, found.service, DeleteMapping, DeleteArgs(port)).ConfigureAwait(false);
			_mapped = null;

			return;
		}

		// Closing while this was on its way: take it straight back off rather than leave it forwarded.
		if (_stopped) {
			await SoapAsync(found.control, found.service, DeleteMapping, DeleteArgs(port)).ConfigureAwait(false);

			return;
		}

		_mapped = (found.control, found.service, port, found.lanIp);
		_renewedAt = DateTime.UtcNow;
		ExternalIp = ip;
		Problem = null;

		if (first) {
			Log.Good(new Said("open from anywhere: on at {0}", Link!));
		}
	}

	private static void Fail(Said why) {
		if (Problem != why.ToString()) {
			Log.Warn(new Said("open from anywhere: {0}", why));
		}

		Problem = why.ToString();

		// No working link while it's failing - an old address must not keep being handed out as if it worked.
		ExternalIp = null;
	}

	private static async Task RemoveAsync(bool quietly = false) {
		if (_mapped is not { } m) {
			return;
		}

		_mapped = null;
		ExternalIp = null;

		string? kept = await SoapAsync(m.Control, m.Service, DeleteMapping, DeleteArgs(m.Port)).ConfigureAwait(false);

		if (quietly) {
			return;
		}

		Log.Info(kept == null
			? new Said("open from anywhere: off - port {0} no longer forwarded", m.Port)
			: new Said("open from anywhere: off - remove the forward in your router ({0})", kept));
	}

	/// <summary>
	/// Closing: the forward goes, so nothing is left pointing at a PC where nocat.farm isn't running. The next start
	/// puts it back (a few seconds in). Waits a few seconds for the router at most.
	/// </summary>
	public static async Task StopAsync() {
		_stopped = true;
		_timer?.Dispose();

		// A look already under way finishes first (a few seconds at most), and nothing starts after it.
		DateTime until = DateTime.UtcNow.AddSeconds(5);

		while ((Interlocked.CompareExchange(ref _busy, 1, 0) != 0) && (DateTime.UtcNow < until)) {
			await Task.Delay(100).ConfigureAwait(false);
		}

		try {
			await RemoveAsync(quietly: true).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		} catch {
			// the lease runs out by itself within the hour
		}
	}

	private static string AddArgs(int port, string lanIp, int lease) =>
		$"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>"
		+ $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{SecurityElement.Escape(lanIp)}</NewInternalClient><NewEnabled>1</NewEnabled>"
		+ $"<NewPortMappingDescription>{Description}</NewPortMappingDescription><NewLeaseDuration>{lease}</NewLeaseDuration>";

	private static string DeleteArgs(int port) =>
		$"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>";

	/// <summary>Not an internet address at all: 0.0.0.0, loopback, link-local, 192.0.0.x, multicast and above.</summary>
	private static bool Unusable(IPAddress ip) {
		byte[] b = ip.GetAddressBytes();

		return (ip.AddressFamily != AddressFamily.InterNetwork) || IPAddress.IsLoopback(ip) || (b[0] == 0) || (b[0] >= 224)
			|| ((b[0] == 169) && (b[1] == 254)) || ((b[0] == 192) && (b[1] == 0) && (b[2] == 0));
	}

	/// <summary>100.64.0.0/10 - an address the provider shares between customers, where a forward on the router is useless.</summary>
	private static bool IsProviderShared(IPAddress ip) {
		byte[] b = ip.GetAddressBytes();

		return (ip.AddressFamily == AddressFamily.InterNetwork) && (b[0] == 100) && (b[1] >= 64) && (b[1] <= 127);
	}

	/// <summary>A home-network address as the router's "internet" address: it sits behind another router.</summary>
	private static bool IsPrivate(IPAddress ip) {
		byte[] b = ip.GetAddressBytes();

		return (ip.AddressFamily == AddressFamily.InterNetwork) && ((b[0] == 10) || ((b[0] == 172) && (b[1] >= 16) && (b[1] <= 31)) || ((b[0] == 192) && (b[1] == 168)));
	}

	// ── UPnP ──

	/// <summary>Ask the network for an internet gateway, read its description, and find the service that forwards ports.</summary>
	private static async Task<(Uri, string, string)?> FindGatewayAsync() {
		if (GatewayForTests is { } test) {
			return await ReadGatewayAsync(test).ConfigureAwait(false);
		}

		byte[] ask = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n"
			+ "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");

		// Asked through every network card with a router behind it, not only the one Windows sends multicast out of by
		// default - a VirtualBox, Hyper-V, WSL or VPN adapter is often that one, and no router ever hears the question.
		List<UdpClient> askers = [];

		try {
			foreach ((IPAddress local, List<IPAddress> gateways) in LanAddresses()) {
				UdpClient? udp = null;

				try {
					udp = new(new IPEndPoint(local, 0));
					udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
					await udp.SendAsync(ask, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

					// And the card's router asked directly: some routers and mesh systems never answer the broadcast one.
					foreach (IPAddress gateway in gateways) {
						await udp.SendAsync(ask, new IPEndPoint(gateway, 1900)).ConfigureAwait(false);
					}

					askers.Add(udp);
				} catch (SocketException) {
					// that card can't send - the others still ask. Its socket is closed, not left open: this runs every
					// half minute while no forward is in place, and each failed card leaked one.
					udp?.Dispose();
				}
			}

			if (askers.Count == 0) {
				UdpClient any = new(AddressFamily.InterNetwork);
				await any.SendAsync(ask, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);
				askers.Add(any);
			}

			using CancellationTokenSource wait = new(TimeSpan.FromSeconds(4));
			Dictionary<Task<UdpReceiveResult>, UdpClient> listening = askers.ToDictionary(u => u.ReceiveAsync(wait.Token).AsTask(), u => u);

			while (listening.Count > 0) {
				Task<UdpReceiveResult> done = await Task.WhenAny(listening.Keys).ConfigureAwait(false);
				UdpClient from = listening[done];
				listening.Remove(done);
				UdpReceiveResult got;

				try {
					got = await done.ConfigureAwait(false);
				} catch (Exception e) when (e is OperationCanceledException or SocketException) {
					continue;   // that card is done listening
				}

				Match loc = LocationRegex().Match(Encoding.ASCII.GetString(got.Buffer));

				if (loc.Success && Uri.TryCreate(loc.Groups[1].Value.Trim(), UriKind.Absolute, out Uri? descUrl)
					&& (await ReadGatewayAsync(descUrl).ConfigureAwait(false) is { } found)) {
					return found;
				}

				// Not a gateway that forwards ports - keep listening on that card.
				listening[from.ReceiveAsync(wait.Token).AsTask()] = from;
			}

			return null;
		} finally {
			foreach (UdpClient u in askers) {
				u.Dispose();
			}
		}
	}

	/// <summary>This PC's IPv4 addresses on network cards that are up and have a router (a gateway) behind them, with those routers.</summary>
	private static List<(IPAddress Local, List<IPAddress> Gateways)> LanAddresses() {
		List<(IPAddress, List<IPAddress>)> found = [];

		try {
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
				if ((nic.OperationalStatus != OperationalStatus.Up) || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) {
					continue;
				}

				IPInterfaceProperties props = nic.GetIPProperties();

				List<IPAddress> gateways = [.. props.GatewayAddresses.Select(static g => g.Address)
					.Where(static g => (g.AddressFamily == AddressFamily.InterNetwork) && !g.Equals(IPAddress.Any))];

				if (gateways.Count == 0) {
					continue;
				}

				found.AddRange(props.UnicastAddresses.Select(static a => a.Address).Where(IsPrivate).Select(a => (a, gateways)));
			}
		} catch (NetworkInformationException) {
			// no list of cards - the default one still asks
		}

		return found;
	}

	/// <summary>A router's description: the service that forwards ports, and this PC's address as the router sees it.</summary>
	private static async Task<(Uri, string, string)?> ReadGatewayAsync(Uri descUrl) {
		try {
			XDocument desc = XDocument.Parse(await Http.GetStringAsync(descUrl).ConfigureAwait(false));
			XElement? svc = desc.Descendants().FirstOrDefault(static e => (e.Name.LocalName == "service")
				&& (e.Elements().FirstOrDefault(static x => x.Name.LocalName == "serviceType")?.Value is { } t)
				&& (t.Contains("WANIPConnection", StringComparison.Ordinal) || t.Contains("WANPPPConnection", StringComparison.Ordinal)));

			if (svc == null) {
				return null;
			}

			string type = svc.Elements().First(static x => x.Name.LocalName == "serviceType").Value.Trim();
			string control = svc.Elements().FirstOrDefault(static x => x.Name.LocalName == "controlURL")?.Value.Trim() ?? "";

			// The local end of a connection to the router - the address it forwards to.
			using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			probe.Connect(descUrl.Host, descUrl.Port);
			string lanIp = ((IPEndPoint) probe.LocalEndPoint!).Address.ToString();

			return (new Uri(descUrl, control), type, lanIp);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Xml.XmlException or SocketException or UriFormatException or InvalidOperationException) {
			return null;
		}
	}

	/// <summary>One UPnP action. Null when it worked, otherwise the router's error.</summary>
	private static async Task<string?> SoapAsync(Uri control, string service, string action, string args) {
		try {
			using HttpResponseMessage res = await PostAsync(control, service, action, args).ConfigureAwait(false);

			if (res.IsSuccessStatusCode) {
				return null;
			}

			string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
			Match code = ErrorCodeRegex().Match(body);
			Match text = ErrorTextRegex().Match(body);

			return code.Success ? $"{code.Groups[1].Value} {text.Groups[1].Value}".Trim() : $"HTTP {(int) res.StatusCode}";
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
			return e.Message;
		}
	}

	private static async Task<string?> SoapReplyAsync(Uri control, string service, string action, string args) {
		try {
			using HttpResponseMessage res = await PostAsync(control, service, action, args).ConfigureAwait(false);

			return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
			return null;
		}
	}

	private static Task<HttpResponseMessage> PostAsync(Uri control, string service, string action, string args) {
		string envelope = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
			+ "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
			+ $"<u:{action} xmlns:u=\"{service}\">{args}</u:{action}></s:Body></s:Envelope>";

		HttpRequestMessage req = new(HttpMethod.Post, control) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
		req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");

		return Http.SendAsync(req);
	}

	[GeneratedRegex(@"(?im)^LOCATION:\s*(\S+)")]
	private static partial Regex LocationRegex();

	[GeneratedRegex(@"<NewExternalIPAddress>([^<]*)<")]
	private static partial Regex ExternalIpRegex();

	[GeneratedRegex(@"<errorCode>(\d+)</errorCode>")]
	private static partial Regex ErrorCodeRegex();

	[GeneratedRegex(@"<errorDescription>([^<]*)</errorDescription>")]
	private static partial Regex ErrorTextRegex();
}
