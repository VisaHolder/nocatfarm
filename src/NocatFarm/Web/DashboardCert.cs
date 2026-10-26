using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>
/// 1.8.6: the dashboard's own certificates, for "Open from anywhere" over HTTPS - made by nocat.farm itself, no outside
/// service or name (reap turned down Let's Encrypt, sslip.io and Tailscale: it has to work for anybody, on their own PC
/// or box). A small authority ("nocat.farm dashboard", 10 years) is made once; its public part goes to the phone once
/// and the phone trusts the dashboard from then on - no warning page. The certificate the dashboard shows is signed by
/// it and made again by itself when the box's address changes or it nears its end, without anything new on the phone.
/// </summary>
/// <remarks>
/// config/state/dashboard-ca.pfx and dashboard-cert.pfx hold the keys - owner-only, never in a backup (Backup's pattern
/// doesn't take them). dashboard-ca.crt is the public part sent to the phone.
/// </remarks>
public static class DashboardCert {
	private static readonly Lock Gate = new();
	private static DateTime _checkedAt;
	private static X509Certificate2? _ca, _leaf;

	private static string State => Path.Combine(ConfigStore.ConfigDir, "state");
	internal static string CaPath => Path.Combine(State, "dashboard-ca.pfx");
	internal static string LeafPath => Path.Combine(State, "dashboard-cert.pfx");

	/// <summary>The public part of the authority, for the phone.</summary>
	public static string CrtPath => Path.Combine(State, "dashboard-ca.crt");

	/// <summary>The authority was made by this start - its certificate still has to go to the phone (Telegram).</summary>
	public static bool MadeNow { get; private set; }

	/// <summary>The authority: read, or made the first time.</summary>
	public static X509Certificate2 Authority() {
		lock (Gate) {
			if (_ca != null) {
				return _ca;
			}

			if (Load(CaPath) is { HasPrivateKey: true } had && (had.NotAfter > DateTime.Now.AddDays(30))) {
				return _ca = had;
			}

			using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			CertificateRequest req = new("CN=nocat.farm dashboard, O=nocat.farm", key, HashAlgorithmName.SHA256);
			req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
			req.CertificateExtensions.Add(NameConstraints());   // 1.8.6: it can only vouch for the dashboard's own names
			req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
			req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
			X509Certificate2 made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
			Save(CaPath, made);
			File.WriteAllText(CrtPath, made.ExportCertificatePem() + "\n");
			MadeNow = true;
			_leaf = null;   // a new authority: the old certificate can't be trusted by it

			if (Load(CaPath) is { } saved) {
				made.Dispose();   // macOS keeps a key in a temporary keychain file until its certificate is let go

				return _ca = saved;
			}

			return _ca = made;
		}
	}

	/// <summary>
	/// The certificate the dashboard shows: read, or made again when it doesn't cover every name and address now, isn't the
	/// authority's, or ends within 30 days. At most about 2 years (what Apple accepts for a certificate a phone was given).
	/// </summary>
	public static X509Certificate2 Current() {
		lock (Gate) {
			// 1.8.6: asked on every https connection - the names are only looked at again every few minutes, never per handshake
			if ((_leaf != null) && (DateTime.UtcNow - _checkedAt < TimeSpan.FromMinutes(5))) {
				return _leaf;
			}

			_checkedAt = DateTime.UtcNow;
			X509Certificate2 ca = Authority();
			HashSet<string> names = Names();

			X509Certificate2? had = _leaf ?? Load(LeafPath);

			if (had is { HasPrivateKey: true } && (had.NotAfter > DateTime.Now.AddDays(30)) && (had.Issuer == ca.Subject) && Covers(had, names)) {
				return _leaf = had;
			}

			// One read from disk and not used is let go at once (macOS keeps each key in a temporary keychain file). The one in use
			// stays - a handshake under way may still hold it; a new one is made only when the box's address changes.
			if ((had != null) && !ReferenceEquals(had, _leaf)) {
				had.Dispose();
			}

			using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			CertificateRequest req = new("CN=nocat.farm", key, HashAlgorithmName.SHA256);
			SubjectAlternativeNameBuilder san = new();

			foreach (string n in names) {
				if (IPAddress.TryParse(n, out IPAddress? ip)) {
					san.AddIpAddress(ip);
				} else {
					san.AddDnsName(n);
				}
			}

			req.CertificateExtensions.Add(san.Build());
			req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
			req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
			req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // a server
			req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
			byte[] serial = RandomNumberGenerator.GetBytes(16);
			serial[0] &= 0x7F;

			using X509Certificate2 signed = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(800), serial);
			X509Certificate2 made = signed.CopyWithPrivateKey(key);
			Save(LeafPath, made);
			X509Certificate2 kept = Load(LeafPath) ?? made;

			if (!ReferenceEquals(kept, made)) {
				made.Dispose();
			}

			return _leaf = kept;
		}
	}

	private static string SentPath => Path.Combine(State, "dashboard-ca.sent");

	/// <summary>
	/// Make sure the phone can get the authority: made if it isn't yet, and sent to the Telegram chat once per authority
	/// (the fingerprint written down once it went). Without Telegram, 'dashboard cert' says where the file is.
	/// </summary>
	public static async Task EnsureSentAsync(CancellationToken ct) {
		X509Certificate2 ca;

		try {
			Current();
			ca = Authority();
		} catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException) {
			Log.Warn(new Said("couldn't make the dashboard's certificate: {0}", Log.Describe(e)));

			return;
		}

		string print = Fingerprint(ca);

		if ((File.Exists(SentPath) && (File.ReadAllText(SentPath).Trim() == print)) || !Notifier.TelegramConnected) {
			return;
		}

		if (await SendAsync(ct).ConfigureAwait(false)) {
			AtomicFile.Write(SentPath, print);
		}
	}

	/// <summary>The authority's public part and fingerprint to the Telegram chat, with how to install it - 'dashboard cert' too.</summary>
	public static Task<bool> SendAsync(CancellationToken ct) {
		X509Certificate2 ca = Authority();

		if (!File.Exists(CrtPath)) {
			File.WriteAllText(CrtPath, ca.ExportCertificatePem() + "\n");
		}

		return Notifier.SendFileAsync(CrtPath, InstallSaid(Fingerprint(ca)).ToString(), ct);
	}

	/// <summary>How to install it on a phone, plainly, with the fingerprint to check.</summary>
	public static Said InstallSaid(string fingerprint) => new(
		"nocat.farm dashboard certificate - install it once and your phone trusts the dashboard over https (no warning page). Android: open the file, or Settings > Security > Encryption & credentials > Install a certificate > CA certificate. iPhone: open the file, Settings > Profile Downloaded > Install, then Settings > General > About > Certificate Trust Settings > turn it on. Check the fingerprint (SHA-256): {0}",
		fingerprint);

	/// <summary>Forget what's loaded - the checks, and a reset.</summary>
	internal static void Forget() {
		lock (Gate) {
			_ca?.Dispose();
			_leaf?.Dispose();
			_ca = _leaf = null;
			_checkedAt = default;
			MadeNow = false;
		}
	}

	/// <summary>"AB:CD:..." - SHA-256 of the authority, to check against what the phone shows.</summary>
	public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData)).Chunk(2).Select(static c => new string(c))
		.Aggregate(static (a, b) => a + ":" + b);

	/// <summary>
	/// Every name and address the dashboard may be opened by: localhost, nocat.farm, this machine's name, its own
	/// addresses (Wi-Fi, cable), the address the router shows the internet (when known) and a public address set by hand.
	/// </summary>
	internal static HashSet<string> Names() {
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "localhost", "nocat.farm", "127.0.0.1", "::1" };

		if (Environment.MachineName is { Length: > 0 } machine && Uri.CheckHostName(machine) == UriHostNameType.Dns) {
			names.Add(machine);
		}

		try {
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces().Where(static n => n.OperationalStatus == OperationalStatus.Up)) {
				foreach (UnicastIPAddressInformation a in nic.GetIPProperties().UnicastAddresses) {
					if ((a.Address.AddressFamily == AddressFamily.InterNetwork) && !IPAddress.IsLoopback(a.Address)) {
						names.Add(a.Address.ToString());
					}
				}
			}
		} catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException) {
			// the addresses can't be listed here - localhost and the names still go in
		}

		if (RemoteAccess.ExternalIp is { Length: > 0 } outside) {
			names.Add(outside);
		}

		if (DashboardLinks.Outside(Live.Global.WebPublicAddress, 1) is { } typed && Uri.TryCreate(typed, UriKind.Absolute, out Uri? pub) && (pub.DnsSafeHost.Length > 0)) {
			names.Add(pub.DnsSafeHost);   // without an IPv6 address's brackets, as the certificate holds it
		}

		return names;
	}

	/// <summary>Whether a certificate names every one of these.</summary>
	internal static bool Covers(X509Certificate2 cert, IEnumerable<string> names) {
		HashSet<string> has = new(StringComparer.OrdinalIgnoreCase);

		foreach (X509SubjectAlternativeNameExtension san in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>()) {
			has.UnionWith(san.EnumerateDnsNames());
			has.UnionWith(san.EnumerateIPAddresses().Select(static ip => ip.ToString()));
		}

		return names.All(has.Contains);
	}

	private static X509Certificate2? Load(string path) {
		try {
			return File.Exists(path) ? X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Secrets.Unprotect(File.ReadAllText(path).Trim())), null, X509KeyStorageFlags.Exportable) : null;
		} catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or FormatException) {
			Log.Debug(new Said("dashboard certificate {0} couldn't be read - a new one is made: {1}", Path.GetFileName(path), Log.Describe(e)));

			return null;
		}
	}

	/// <summary>
	/// 1.8.6: the keys sealed like every other secret (Windows' own protection, or secret.key elsewhere), written once and
	/// whole - never a half-written key that quietly makes a new authority and every phone install it again.
	/// </summary>
	private static void Save(string path, X509Certificate2 cert) {
		Directory.CreateDirectory(State);
		AtomicFile.Write(path, Secrets.Protect(Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12)), "nocat.farm"));

		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // and nobody else on the machine
		}
	}

	/// <summary>
	/// 1.8.6: Name Constraints on the authority - it can only vouch for nocat.farm, localhost, this machine's name and a
	/// public address set by hand (and any IP address, which is how it's opened from outside). Stolen, its key still can't
	/// make a phone that trusts it believe a bank or a mail site.
	/// </summary>
	internal static X509Extension NameConstraints() {
		System.Formats.Asn1.AsnWriter w = new(System.Formats.Asn1.AsnEncodingRules.DER);

		using (w.PushSequence()) {
			using (w.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 0, true))) {
				HashSet<string> dns = new(StringComparer.OrdinalIgnoreCase) { "nocat.farm", "localhost" };

				if (Environment.MachineName is { Length: > 0 } machine && Uri.CheckHostName(machine) == UriHostNameType.Dns) {
					dns.Add(machine);
				}

				if (DashboardLinks.Outside(Live.Global.WebPublicAddress, 1) is { } typed && Uri.TryCreate(typed, UriKind.Absolute, out Uri? pub)
					&& (Uri.CheckHostName(pub.DnsSafeHost) == UriHostNameType.Dns)) {
					dns.Add(pub.DnsSafeHost);
				}

				foreach (string name in dns) {
					using (w.PushSequence()) {
						w.WriteCharacterString(System.Formats.Asn1.UniversalTagNumber.IA5String, name.ToLowerInvariant(),
							new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 2));
					}
				}

				// every IPv4 and IPv6 address: address and mask, all zero
				foreach (int length in new[] { 8, 32 }) {
					using (w.PushSequence()) {
						w.WriteOctetString(new byte[length], new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 7));
					}
				}
			}
		}

		return new X509Extension(new Oid("2.5.29.30"), w.Encode(), true);
	}
}
