using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// How many people use nocat.farm: once an hour this copy tells nocat.lol it's running, and nocat.lol answers with how
/// many different copies did the same in the last 24 hours.
/// </summary>
/// <remarks>
/// What goes out is all there is: a random number made for this install (<see cref="InstallId"/>), the app version and
/// the platform. No Steam account, name, setting or address of anything. "Count me as a user" off sends nothing at all,
/// and the count isn't shown either.
///
/// The random number lives in config/state/install-id.txt, which a backup leaves out on purpose - a backup restored on a
/// second PC would otherwise make the two count as one. It is never written to the log.
///
/// It never touches Steam, and nothing waits on it: no answer, a bad answer or a timeout is one debug line, and it tries
/// again at the next hour, not before.
/// </remarks>
public static class UserCount {
	/// <summary>Where the ping goes. HTTPS only.</summary>
	public const string Endpoint = "https://nocat.lol/api/farm/ping";

	/// <summary>A count older than this isn't shown - three missed hours in a row means it's no longer today's.</summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

	/// <summary>Between two pings, give or take <see cref="Jitter"/>.</summary>
	public static readonly TimeSpan Every = TimeSpan.FromMinutes(60);

	public static readonly TimeSpan Jitter = TimeSpan.FromMinutes(4);

	private static readonly Lock Gate = new();
	private static int? _users;
	private static DateTime _readAt = DateTime.MinValue;
	private static DateTime _next = DateTime.MaxValue;
	private static CancellationTokenSource? _stop;

	/// <summary>"Count me as a user" as it was at the last look: only switching it ON brings the ping forward.</summary>
	private static bool _wasOn;

	/// <summary>The number made this run when it couldn't be kept on disk, so a read-only folder still counts once a run.</summary>
	private static string? _unsaved;

	private static readonly Lazy<HttpClient> Http = new(static () => MakeClient(new SocketsHttpHandler {
		AllowAutoRedirect = false,
		UseCookies = false,   // nothing set by the site ever comes back on a later ping: each one is only the three fields
		PooledConnectionLifetime = TimeSpan.FromMinutes(15)
	}));

	/// <summary>The client the ping is sent with: 10 seconds and no more, a plain nocat.farm User-Agent, a small answer.</summary>
	public static HttpClient MakeClient(HttpMessageHandler handler) {
		HttpClient http = new(handler, true) {
			Timeout = TimeSpan.FromSeconds(10),
			MaxResponseContentBufferSize = 64 * 1024
		};
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "nocat.farm/" + Build.Version);

		return http;
	}

	public static string IdPath => Path.Combine(ConfigStore.ConfigDir, "state", "install-id.txt");

	/// <summary>32 lowercase hex characters: what an install id looks like.</summary>
	public static bool IsId(string? s) => s is { Length: 32 } && s.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

	/// <summary>This install's random number, made the first time it's needed and kept after that.</summary>
	public static string InstallId() {
		lock (Gate) {
			string path = IdPath;

			try {
				if (File.Exists(path) && File.ReadAllText(path).Trim() is { } kept && IsId(kept)) {
					return kept;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug($"user count: couldn't read the install's number: {Log.Describe(e)}");
			}

			if (_unsaved != null) {
				return _unsaved;
			}

			string made = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				AtomicFile.Write(path, made);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				_unsaved = made;
				Log.Debug($"user count: couldn't keep the install's number: {Log.Describe(e)}");
			}

			return made;
		}
	}

	/// <summary>"windows", "linux", "mac" or "docker" - Docker the way the rest of the app tells it, from inside the container.</summary>
	public static string Os => Platform.InContainer ? "docker"
		: OperatingSystem.IsWindows() ? "windows"
		: OperatingSystem.IsMacOS() ? "mac"
		: "linux";

	/// <summary>
	/// Where the ping really goes: <see cref="Endpoint"/>, or NOCATFARM_PING_URL when that points at this PC - for the
	/// browser test, which answers it itself rather than counting a test copy on nocat.lol. Anything else is ignored.
	/// </summary>
	public static string Target {
		get {
			string? over = Environment.GetEnvironmentVariable("NOCATFARM_PING_URL");

			return Uri.TryCreate(over, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.IsLoopback
				? u.ToString()
				: Endpoint;
		}
	}

	/// <summary>The ping goes to a stand-in on this PC (the browser test's): the first one then comes in seconds, not minutes.</summary>
	private static bool ToTestStub => Target != Endpoint;

	/// <summary>When the first ping (or the one after switching it on) is due: 1-3 minutes from now.</summary>
	private static DateTime Soon() => DateTime.UtcNow.AddSeconds(ToTestStub ? 5 : Random.Shared.Next(60, 181));

	/// <summary>The body sent: the install's number, the version and the platform, nothing else.</summary>
	public static string Body() => JsonSerializer.Serialize(new { id = InstallId(), v = Build.Version, os = Os });

	/// <summary>The count in nocat.lol's answer: a 200 with {"users":N}. Anything else - another status, not JSON, no
	/// number, nobody - is null, "unknown".</summary>
	public static int? ParseReply(HttpStatusCode status, string body) {
		if (status != HttpStatusCode.OK) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			return (doc.RootElement.ValueKind == JsonValueKind.Object)
				&& doc.RootElement.TryGetProperty("users", out JsonElement u)
				&& (u.ValueKind == JsonValueKind.Number) && u.TryGetInt32(out int n) && (n > 0)
				? n
				: null;
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>
	/// One ping: says this copy is running and keeps the count that comes back. Nothing at all with "Count me as a user"
	/// off. Never throws - a failure is one debug line and false, and the next try is the next hour's.
	/// </summary>
	public static async Task<bool> PingAsync(HttpClient http, GlobalConfig g, CancellationToken ct = default) {
		if (!g.CountMeAsUser) {
			return false;
		}

		try {
			using HttpRequestMessage req = new(HttpMethod.Post, Target) { Content = new StringContent(Body(), Encoding.UTF8) };
			req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

			using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
			string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			if (ParseReply(resp.StatusCode, text) is not { } users) {
				Log.Debug($"user count: no count in nocat.lol's answer (HTTP {(int) resp.StatusCode}) - trying again in an hour");

				return false;
			}

			Remember(users, DateTime.UtcNow);

			return true;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			// closing
			return false;
		} catch (Exception e) {
			// A timeout lands here too: HttpClient's own timeout is a cancellation nobody asked for.
			Log.Debug($"user count: nocat.lol didn't answer ({Log.Describe(e)}) - trying again in an hour");

			return false;
		}
	}

	/// <summary>Keeps a count and when it was read.</summary>
	public static void Remember(int users, DateTime atUtc) {
		lock (Gate) {
			_users = users;
			_readAt = atUtc;
		}
	}

	/// <summary>Drops the count kept - for the checks.</summary>
	public static void Forget() {
		lock (Gate) {
			_users = null;
			_readAt = DateTime.MinValue;
		}
	}

	/// <summary>The count to show, or null: "Count me as a user" off, nothing read yet, or the last one older than <see cref="StaleAfter"/>.</summary>
	public static int? Current(GlobalConfig g, DateTime nowUtc) {
		if (!g.CountMeAsUser) {
			return null;
		}

		lock (Gate) {
			return (_users is { } n) && (nowUtc - _readAt <= StaleAfter) ? n : null;
		}
	}

	public static int? Current() => Current(Live.Global, DateTime.UtcNow);

	/// <summary>The gap to the next ping: an hour, give or take a few minutes.</summary>
	public static TimeSpan NextGap() => Every + TimeSpan.FromSeconds(Random.Shared.Next(-(int) Jitter.TotalSeconds, (int) Jitter.TotalSeconds + 1));

	/// <summary>Starts the hourly ping: the first one 1-3 minutes after start, then every hour or so.</summary>
	public static void Start() {
		CancellationToken ct;

		lock (Gate) {
			if (_stop != null) {
				return;
			}

			_stop = new CancellationTokenSource();
			ct = _stop.Token;
			_next = Soon();
			_wasOn = Live.Global.CountMeAsUser;
		}

		_ = Task.Run(() => LoopAsync(ct), CancellationToken.None);
	}

	public static void Stop() {
		lock (Gate) {
			_stop?.Cancel();
		}
	}

	/// <summary>
	/// "Count me as a user" was just switched on: with no count to show, the next ping comes in 1-3 minutes rather than up
	/// to an hour from now. Nothing when it was on already - the dashboard runs this on every save of any setting, and
	/// each one used to bring the ping forward again, so with nocat.lol not answering every save sent another. Nothing
	/// either when a count is showing or one is due soon anyway.
	/// </summary>
	public static void Nudge() => Nudge(Live.Global.CountMeAsUser, DateTime.UtcNow);

	/// <returns>true when the next ping was brought forward.</returns>
	public static bool Nudge(bool on, DateTime nowUtc) {
		lock (Gate) {
			bool switchedOn = on && !_wasOn;
			_wasOn = on;

			if (!switchedOn || (_stop == null) || ((_users != null) && (nowUtc - _readAt <= StaleAfter))) {
				return false;
			}

			DateTime soon = Soon();

			if (_next <= soon) {
				return false;
			}

			_next = soon;

			return true;
		}
	}

	private static async Task LoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

				lock (Gate) {
					if (DateTime.UtcNow < _next) {
						continue;
					}

					_next = DateTime.UtcNow + NextGap();
				}

				await PingAsync(Http.Value, Live.Global, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception e) {
				Log.Debug($"user count: {Log.Describe(e)}");
			}
		}
	}
}
