using System.Net;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Who has been at the dashboard: every sign-in, wrong password and lockout, and every visitor from the internet turned
/// away while "Open from anywhere" is off - with where they came from and on what. The last 200 are kept in
/// state/visitors.json; 'visitors' and the Phone page show them. A sign-in from outside the home, a lockout, and the
/// internet brake also go to Telegram and Discord ("Send dashboard sign-ins"), so a stranger getting in - or trying hard
/// to - is never something found out weeks later.
/// </summary>
public static class Visitors {
	public const int Keep = 200;

	/// <summary>A visitor turned away is written down once per address in this long, not once per request.</summary>
	private static readonly TimeSpan TurnedAwayQuiet = TimeSpan.FromMinutes(10);

	/// <summary>
	/// At most this many visitors turned away are written down in any <see cref="TurnedAwayQuiet"/>, all addresses together.
	/// Once per address wasn't enough: somebody with a few hundred addresses (one IPv6 /56 is 256 /64s) wrote 200 in ten
	/// minutes and pushed everything else out of the 200 kept - a sign-in from outside with it - rewriting the file each time.
	/// </summary>
	public const int TurnedAwayPerQuiet = 20;

	private static readonly Queue<DateTime> TurnedAwayWritten = new();

	public enum What { SignedIn, WrongPassword, LockedOut, TurnedAway, Paused, CodeSent, WrongCode, ClosedToInternet }

	/// <summary>One visit. Where: "this PC", "home" or "internet".</summary>
	public sealed record Visit(DateTime When, string Ip, string Where, What What, string Device);

	private static readonly Lock Gate = new();
	private static List<Visit>? _visits;
	private static readonly Dictionary<string, DateTime> TurnedAwaySaid = [];

	private static string FilePath => Path.Combine(ConfigStore.ConfigDir, "state", "visitors.json");

	/// <summary>Where an address is: this PC, home (the home network, a VPN) or the internet.</summary>
	public static string WhereFrom(string ip, bool thisPc) =>
		thisPc ? "this PC" : RemoteAccess.IsInternet(ip) ? "internet" : "home";

	/// <summary>"iPhone · Safari", "Windows · Chrome" - enough to tell your own phone from somebody else's.</summary>
	public static string Device(string? userAgent) {
		string ua = userAgent ?? "";

		if (ua.Length == 0) {
			return "unknown";
		}

		string os = ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
			: ua.Contains("iPad", StringComparison.Ordinal) ? "iPad"
			: ua.Contains("Android", StringComparison.Ordinal) ? "Android"
			: ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
			: ua.Contains("Mac OS", StringComparison.Ordinal) ? "Mac"
			: ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
			: "";

		// Order matters: Edge and Opera say Chrome too, and Chrome says Safari.
		string browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
			: ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
			: ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS/", StringComparison.Ordinal) ? "Firefox"
			: ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
			: ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
			: "";

		string both = string.Join(" · ", new[] { os, browser }.Where(static s => s.Length > 0));

		return both.Length > 0 ? both : Log.Scrub(ua.Length > 40 ? ua[..40] : ua);
	}

	/// <summary>Write a visit down, and say it where it matters.</summary>
	public static void Note(What what, string ip, bool thisPc, string? userAgent, int count = 0, int minutes = 0) {
		string where = WhereFrom(ip, thisPc);
		string device = Device(userAgent);
		DateTime now = DateTime.UtcNow;

		lock (Gate) {
			if (what == What.TurnedAway) {
				// By /64 for IPv6: one phone or server hands itself a new address from the same /64 at will, and each one
				// would otherwise be written down - pushing the real visits out of the 200 kept.
				string key = Neighbourhood(ip);

				if (TurnedAwaySaid.TryGetValue(key, out DateTime said) && (now - said < TurnedAwayQuiet)) {
					return;
				}

				while (TurnedAwayWritten.TryPeek(out DateTime first) && (now - first >= TurnedAwayQuiet)) {
					TurnedAwayWritten.Dequeue();
				}

				if (TurnedAwayWritten.Count >= TurnedAwayPerQuiet) {
					return;
				}

				TurnedAwayWritten.Enqueue(now);
				TurnedAwaySaid[key] = now;

				if (TurnedAwaySaid.Count > 500) {
					foreach (string old in TurnedAwaySaid.Where(kv => now - kv.Value >= TurnedAwayQuiet).Select(static kv => kv.Key).ToArray()) {
						TurnedAwaySaid.Remove(old);
					}
				}
			}

			List<Visit> visits = LoadLocked();
			visits.Add(new Visit(now, ip, where, what, device));

			if (visits.Count > Keep) {
				visits.RemoveRange(0, visits.Count - Keep);
			}

			SaveLocked(visits);
		}

		bool outside = where == "internet";

		switch (what) {
			case What.SignedIn:
				Log.Info(new Said("dashboard: signed in from {0} ({1}, {2})", ip, WhereWords(where), device));

				if (outside) {
					Log.Publish(Topic.Security, "dashboard", new Said("signed in from outside your home: {0} ({1}). Not you? Send /visitors signout here (on Discord: /nocat visitors signout) - it signs every browser and phone out - then change the dashboard password.", ip, device));
				}

				break;
			case What.WrongPassword:
				Log.Info(new Said("dashboard: wrong password from {0} ({1}, {2})", ip, WhereWords(where), device));

				break;
			case What.LockedOut:
				// The warning itself is already in the log (WebHost says it with the numbers).
				if (!thisPc) {
					Log.Publish(Topic.BreakIn, "dashboard", new Said("{0} ({1}) got the password wrong {2} times and is locked out for {3} minutes.", ip, WhereWords(where), count, minutes));
				}

				break;
			case What.ClosedToInternet:
				Log.Warn(new Said("dashboard: {0} wrong passwords and codes from the internet today - Open from anywhere switched itself off", count));
				Log.Publish(Topic.BreakIn, "dashboard", new Said("{0} wrong passwords and codes from the internet today, so Open from anywhere switched itself off - nothing from outside gets in now. Home still works. Turn it on again on the Phone page, or send /anywhere on here - and change the dashboard password first if it might be known.", count));

				break;
			case What.CodeSent:
				Log.Info(new Said("dashboard: right password from {0} ({1}) - sign-in code sent to Telegram", ip, device));

				break;
			case What.WrongCode:
				Log.Info(new Said("dashboard: wrong sign-in code from {0} ({1})", ip, device));

				break;
			case What.TurnedAway:
				Log.Info(new Said("dashboard: turned away {0} from the internet - Open from anywhere is off", ip));

				break;
			case What.Paused:
				Log.Warn(new Said("dashboard: {0} wrong passwords from the internet in an hour - signing in from outside is paused for {1} minutes", count, minutes));
				Log.Publish(Topic.BreakIn, "dashboard", new Said("{0} wrong passwords from the internet in an hour, from different addresses - signing in from outside is paused for {1} minutes. Home still works.", count, minutes));

				break;
		}
	}

	/// <summary>An IPv4 address as it is; an IPv6 one as its /64, the block one household or server gets.</summary>
	public static string Neighbourhood(string ip) {
		if (!IPAddress.TryParse(ip, out IPAddress? a) || (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) || a.IsIPv4MappedToIPv6) {
			return ip;
		}

		byte[] b = a.GetAddressBytes();

		return Convert.ToHexStringLower(b, 0, 8) + "::/64";
	}

	private static Said WhereWords(string where) => where switch {
		"this PC" => new Said("this PC"),
		"internet" => new Said("the internet"),
		_ => new Said("home")
	};

	/// <summary>The newest first.</summary>
	public static List<Visit> Recent(int count) {
		lock (Gate) {
			return [.. LoadLocked().AsEnumerable().Reverse().Take(count)];
		}
	}

	/// <summary>'visitors': the last visits, newest first.</summary>
	public static string Describe(int count = 20) {
		List<Visit> recent = Recent(count);

		if (recent.Count == 0) {
			return "Nobody has signed in to the dashboard or tried to yet.";
		}

		IEnumerable<string> lines = recent.Select(static v =>
			$"  {v.When.ToLocalTime():MM-dd HH:mm}  {Words(v.What),-22} {v.Ip,-40} {WhereWords(v.Where),-12} {v.Device}");

		return "Who has been at the dashboard, newest first:" + Environment.NewLine + string.Join(Environment.NewLine, lines)
			+ Environment.NewLine + "'visitors signout' signs every browser and phone out.";
	}

	public static string Words(What what) => what switch {
		What.SignedIn => new Said("signed in").ToString(),
		What.WrongPassword => new Said("wrong password").ToString(),
		What.LockedOut => new Said("locked out").ToString(),
		What.TurnedAway => new Said("turned away").ToString(),
		What.CodeSent => new Said("code sent").ToString(),
		What.WrongCode => new Said("wrong code").ToString(),
		What.ClosedToInternet => new Said("closed to the internet").ToString(),
		_ => new Said("sign-in paused").ToString()
	};

	private static List<Visit> LoadLocked() {
		if (_visits != null) {
			return _visits;
		}

		try {
			_visits = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Visit>>(File.ReadAllText(FilePath)) ?? [] : [];
		} catch (Exception e) {
			// a broken file starts the log over - nothing depends on it
			Log.Failed("dashboard: reading the visitor log", e);
			_visits = [];
		}

		return _visits;
	}

	private static void SaveLocked(List<Visit> visits) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			AtomicFile.Write(FilePath, JsonSerializer.Serialize(visits));
		} catch (Exception e) {
			// kept in memory until the next one saves
			Log.Failed("dashboard: saving the visitor log", e);
		}
	}

	/// <summary>For the tests: forget what's in memory so the next read comes from the file (or a new folder).</summary>
	public static void Reload() {
		lock (Gate) {
			_visits = null;
			TurnedAwaySaid.Clear();
		}
	}
}
