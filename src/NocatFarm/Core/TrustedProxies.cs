using System.Net;
using System.Net.Sockets;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// "Trust forwarded addresses from" (WebTrustedProxies): reverse proxies that aren't on this PC, whose X-Forwarded-For
/// is believed the way a proxy on this PC's is.
/// </summary>
/// <remarks>
/// A proxy on this PC reaches the dashboard from loopback, and that has always been enough to read who really asked. In
/// Docker, Caddy (on the host or in a container of its own) reaches it from Docker's network instead - 172.17.0.1, say -
/// so every visitor from the internet looked like somebody at home: no sign-in code, nothing switching itself off, and one
/// lockout shared by everybody. Listing that address (or its range) here puts that right. Empty by default: a header
/// anyone can write is only believed from a proxy somebody chose to trust.
/// </remarks>
public static class TrustedProxies {
	private static readonly Lock Gate = new();
	private static string _parsedFrom = "";
	private static IPNetwork[] _parsed = [];

	/// <summary>The entries, however they were separated: commas, spaces or semicolons.</summary>
	public static string[] Entries(string? text) =>
		(text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>One address (172.17.0.1, ::1) or range (172.16.0.0/12, fd00::/8). A range's host bits are ignored, so 172.18.0.1/16 is 172.18.0.0/16.</summary>
	public static bool TryParse(string entry, out IPNetwork network) {
		network = default;
		string[] parts = entry.Split('/');

		if ((parts.Length > 2) || !IPAddress.TryParse(parts[0], out IPAddress? address)) {
			return false;
		}

		if (address.IsIPv4MappedToIPv6) {
			address = address.MapToIPv4();
		}

		int max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
		int bits = max;

		if ((parts.Length == 2) && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bits) || (bits > max))) {
			return false;
		}

		byte[] bytes = address.GetAddressBytes();

		for (int i = 0; i < bytes.Length; i++) {
			int keep = Math.Clamp(bits - (i * 8), 0, 8);
			bytes[i] &= (byte) (0xFF << (8 - keep));
		}

		network = new IPNetwork(new IPAddress(bytes), bits);

		return true;
	}

	/// <summary>The first entry that is neither an address nor a range, or null when every one reads.</summary>
	public static string? Unreadable(string? text) => Entries(text).FirstOrDefault(static e => !TryParse(e, out _));

	/// <summary>Whether <paramref name="ip"/> is one of the proxies in <paramref name="text"/>.</summary>
	public static bool Contains(string? text, IPAddress? ip) {
		if ((ip == null) || string.IsNullOrWhiteSpace(text)) {
			return false;
		}

		if (ip.IsIPv4MappedToIPv6) {
			ip = ip.MapToIPv4();
		}

		IPNetwork[] networks;

		lock (Gate) {
			if (!string.Equals(text, _parsedFrom, StringComparison.Ordinal)) {
				List<IPNetwork> read = [];

				foreach (string entry in Entries(text)) {
					if (TryParse(entry, out IPNetwork n)) {
						read.Add(n);
					}
				}

				_parsed = [.. read];
				_parsedFrom = text;
			}

			networks = _parsed;
		}

		foreach (IPNetwork n in networks) {
			if (n.Contains(ip)) {
				return true;
			}
		}

		return false;
	}

	/// <summary>Whether a request that arrived from <paramref name="ip"/> came through a proxy the settings trust.</summary>
	public static bool Trusted(IPAddress? ip) => Contains(Live.Global.WebTrustedProxies, ip);
}
