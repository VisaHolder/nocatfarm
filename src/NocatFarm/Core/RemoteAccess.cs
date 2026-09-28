using System.Net;
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
			Fail(new Said("your router didn't answer - turn on UPnP in its settings, or forward port {0} to this PC by hand", port));

			return;
		}

		// An hour, renewed every half hour; some routers only take a forward that never runs out (error 725).
		string? refused = await SoapAsync(found.control, found.service, AddMapping, AddArgs(port, found.lanIp, 3600)).ConfigureAwait(false);

		if ((refused != null) && refused.Contains("725", StringComparison.Ordinal)) {
			refused = await SoapAsync(found.control, found.service, AddMapping, AddArgs(port, found.lanIp, 0)).ConfigureAwait(false);
		}

		if (refused != null) {
			Fail(new Said("your router refused to forward port {0} ({1}) - forward it to this PC by hand in the router's settings", port, refused));

			return;
		}

		string? reply = await SoapReplyAsync(found.control, found.service, AskExternalIp, "").ConfigureAwait(false);
		string ip = reply == null ? "" : ExternalIpRegex().Match(reply).Groups[1].Value;
		IPAddress.TryParse(ip, out IPAddress? ext);
		Said? unreachable = ext == null ? new Said("your router didn't say its internet address")
			: IsProviderShared(ext) ? new Said("your internet connection is shared with other homes (the provider's address {0}), so nothing from outside can reach this PC", ip)
			: IsPrivate(ext) ? new Said("your router is behind another router ({0}) - forward port {1} on that one too, or ask whoever runs it", ip, port)
			: null;

		if (unreachable is { } why) {
			Fail(why);
			await SoapAsync(found.control, found.service, DeleteMapping, DeleteArgs(port)).ConfigureAwait(false);

			return;
		}

		_mapped = (found.control, found.service, port, found.lanIp);
		_renewedAt = DateTime.UtcNow;
		ExternalIp = ip;
		Problem = null;

		if (first) {
			Log.Good(new Said("open from anywhere: your router forwards port {0} to this PC - the dashboard opens at {1} from anywhere (sign in with the dashboard password)", port, Link!));
		}
	}

	private static void Fail(Said why) {
		if (Problem != why.ToString()) {
			Log.Warn(new Said("open from anywhere: {0}", why));
		}

		Problem = why.ToString();
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
			? new Said("open from anywhere: off - your router no longer forwards port {0}", m.Port)
			: new Said("open from anywhere: off, but the router didn't take the forward away ({0}) - remove it in the router's settings", kept));
	}

	/// <summary>
	/// Closing: the forward goes, so nothing is left pointing at a PC where nocat.farm isn't running. The next start
	/// puts it back (a few seconds in). Waits a few seconds for the router at most.
	/// </summary>
	public static async Task StopAsync() {
		_timer?.Dispose();

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

		using UdpClient udp = new(AddressFamily.InterNetwork);
		udp.Client.ReceiveTimeout = 3000;

		byte[] ask = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n"
			+ "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
		await udp.SendAsync(ask, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

		using CancellationTokenSource wait = new(TimeSpan.FromSeconds(4));

		while (!wait.IsCancellationRequested) {
			UdpReceiveResult got;

			try {
				got = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			Match loc = LocationRegex().Match(Encoding.ASCII.GetString(got.Buffer));

			if (loc.Success && Uri.TryCreate(loc.Groups[1].Value.Trim(), UriKind.Absolute, out Uri? descUrl)
				&& (await ReadGatewayAsync(descUrl).ConfigureAwait(false) is { } found)) {
				return found;
			}
		}

		return null;
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
