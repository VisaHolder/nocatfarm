using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Modules;
using NocatFarm.Rep4Rep;

namespace NocatFarm.Web;

/// <summary>
/// The dashboard.
///
/// Closed to everyone but this machine by default: with no password set it refuses anything that isn't
/// loopback, because "no password" plus "bound to 0.0.0.0" is how people hand their Steam accounts to the
/// internet. Two hard rules on the wire - PascalCase names matching the config files exactly, and every
/// SteamID64 and rep4rep id as a string, because JavaScript silently mangles 17-digit numbers.
/// </summary>
public sealed class WebHost : IAsyncDisposable {
	private const int MaxFailedLogins = 5;
	private const int LockoutMinutes = 60;

	/// <summary>
	/// From this PC itself: a minute, not an hour. Someone at this PC can already open the config folder, so an hour's
	/// lockout only ever locked out the owner mistyping their own password.
	/// </summary>
	private const int LockoutMinutesThisPc = 1;

	/// <summary>
	/// Who is signing in, for the lockout: the address, and whether it's this PC itself. A reverse proxy on this PC (Caddy
	/// in front for HTTPS) makes every visitor look like this PC - it says who really asked in X-Forwarded-For, so that
	/// address is used instead, and it counts as outside: the internet gets the full hour, and one person's wrong guesses
	/// don't lock everybody else out.
	///
	/// A proxy that isn't on this PC - Caddy in front of Docker arrives from Docker's own network - is believed the same way
	/// once it's listed in "Trust forwarded addresses from" (WebTrustedProxies). It is never "this PC" itself.
	/// </summary>
	private static (string Ip, bool ThisPc) WhoIsSigningIn(HttpContext ctx) {
		IPAddress from = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
		bool loopback = IPAddress.IsLoopback(from);

		if (!loopback && !TrustedProxies.Trusted(from)) {
			return (from.ToString(), false);
		}

		// What the proxy says, three ways: the LAST X-Forwarded-For entry (the proxy adds the one it really saw to the end;
		// the first is whatever the visitor chose to send), X-Real-IP, and RFC 7239's Forwarded: for=. Which one a proxy
		// fills in - and whether it passes on one the visitor made up - depends on its setup, so the most outside-looking
		// of them wins: a visitor can only make itself look MORE like the internet (which only earns it the stricter
		// rules), never like home. One that can't be read at all counts as the internet.
		List<string> said = [];
		HttpRequest r = ctx.Request;

		if (r.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } xff) {
			said.Add(xff.Split(',')[^1]);
		}

		if (r.Headers["X-Real-IP"].ToString() is { Length: > 0 } real) {
			said.Add(real);
		}

		if (r.Headers["Forwarded"].ToString() is { Length: > 0 } fwd
			&& fwd.Split(',')[^1].Split(';').Select(static p => p.Trim()).FirstOrDefault(static p => p.StartsWith("for=", StringComparison.OrdinalIgnoreCase)) is { } forPart) {
			said.Add(forPart[4..]);
		}

		if (said.Count == 0) {
			return (from.ToString(), loopback);
		}

		// Only a real address counts: anything else ("unknown", an obfuscated id, made-up text) is one shared "unknown" -
		// never a fresh key to dodge the lockout with, and never words of the visitor's choosing in the log or a message.
		List<string> clean = [.. said.Select(static s => IPAddress.TryParse(ForwardedAddress(s), out IPAddress? a) ? a.ToString() : "unknown")];

		return (clean.FirstOrDefault(IsInternet) ?? clean[0], false);
	}

	/// <summary>A forwarded address as it's written: 1.2.3.4, 1.2.3.4:5678, "[2001:db8::1]:443", quoted or not.</summary>
	internal static string ForwardedAddress(string text) {
		string t = text.Trim().Trim('"');

		if (t.StartsWith('[') && (t.IndexOf(']', StringComparison.Ordinal) is > 0 and int close)) {
			return t[1..close];
		}

		// One colon is IPv4 with a port; more is a bare IPv6 address.
		return (t.Count(static c => c == ':') == 1) ? t[..t.IndexOf(':', StringComparison.Ordinal)] : t;
	}

	/// <summary>Seconds this address is still locked out for after too many wrong passwords; 0 when it isn't.</summary>
	private int LockedFor(HttpContext ctx) {
		(string ip, bool thisPc) = WhoIsSigningIn(ctx);
		DateTime until = _failures.TryGetValue(ip, out (int Count, DateTime Until) f) && (f.Count >= MaxFailedLogins) ? f.Until : DateTime.MinValue;
		DateTime paused = PausedUntil;

		if (!thisPc && (paused > until) && IsInternet(ip)) {
			until = paused;
		}

		return until > DateTime.UtcNow ? (int) Math.Ceiling((until - DateTime.UtcNow).TotalSeconds) : 0;
	}

	/// <summary>Every sign-in lockout lifted - the 'unlock' command, for whoever is at this PC.</summary>
	public int ClearLockouts() {
		int n = _failures.Count(static f => f.Value.Count >= MaxFailedLogins && f.Value.Until > DateTime.UtcNow);
		_failures.Clear();

		lock (_brakeGate) {
			_internetMisses.Clear();
			_internetMissesToday.Clear();
			_internetPausedUntil = DateTime.MinValue;
		}

		// Switched itself off: unlock opens it again - the only way back that doesn't need Open from anywhere (a forward
		// set up by hand, or a proxy). With Open from anywhere off and no Public address, the internet stays out anyway.
		lock (_shutGate) {
			if (_internetShut) {
				Reopen();
				n++;
			}
		}

		SaveBrake();

		return n;
	}

	private readonly BotManager _mgr;

	/// <summary>
	/// ALWAYS the live config. Holding the instance handed in at construction meant that after the first save
	/// from the dashboard - which swaps in a new object - the password check, the session length and the
	/// "is a password set" answer all kept consulting the old one. A new dashboard password simply never applied.
	/// </summary>
	private GlobalConfig _cfg => _mgr.Global;

	/// <summary>Signed-in browsers: a hash of each one's token, and when it runs out. Kept on disk (see SaveSessions).</summary>
	private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _failures = new(StringComparer.Ordinal);

	/// <summary>
	/// Wrong passwords from the internet, from any address, in the last hour. Five per address stops one guesser; this
	/// stops many addresses sharing the guessing, which the per-address lockout never sees.
	/// </summary>
	private readonly Queue<DateTime> _internetMisses = new();
	private const int InternetMissesPerHour = 10;
	private const int InternetPauseMinutes = 60;
	private DateTime _internetPausedUntil = DateTime.MinValue;

	/// <summary>
	/// The internet brake - the misses in the last hour and the last day, when it's paused till, and the guesses from the
	/// internet being checked right now - read and changed under this one lock, so a burst of guesses can't all find room.
	/// </summary>
	private readonly Lock _brakeGate = new();

	/// <summary>Guesses from the internet let through and not yet found right or wrong. Under <see cref="_brakeGate"/>.</summary>
	private int _internetInFlight;

	/// <summary>When the internet brake lets go; in the past when it's off.</summary>
	private DateTime PausedUntil {
		get {
			lock (_brakeGate) {
				return _internetPausedUntil;
			}
		}
	}

	/// <summary>From the internet - and an address that can't be read counts as the internet, never as home.</summary>
	public static bool IsInternet(string ip) => RemoteAccess.IsInternet(ip);

	/// <summary>Wrong passwords and codes from the internet in the last 24 hours, for "Turn Open from anywhere off after".</summary>
	private readonly Queue<DateTime> _internetMissesToday = new();

	/// <summary>
	/// Shut to the internet after "Turn Open from anywhere off after" was reached - a Public address set by hand
	/// included, which switching the setting off alone wouldn't close. Until Open from anywhere is on again.
	/// </summary>
	private volatile bool _internetShut = File.Exists(ShutPath);

	/// <summary>Kept on disk, so a restart doesn't open it again behind the owner's back.</summary>
	private static string ShutPath => Path.Combine(ConfigStore.ConfigDir, "state", "internet-shut.txt");

	/// <summary>Shutting and opening again happen under this, so a request arriving mid-shut can't read it half done.</summary>
	private readonly Lock _shutGate = new();

	/// <summary>Shut, and not opened again since: turning Open from anywhere back on is the owner saying "open".</summary>
	private bool InternetShut() {
		if (!_internetShut) {
			return false;
		}

		lock (_shutGate) {
			if (_internetShut && _cfg.WebRemoteAccess) {
				Reopen();
			}

			return _internetShut;
		}
	}

	/// <summary>Open to the internet again (as far as the settings allow): the flag, and the file that keeps it over a restart.</summary>
	private void Reopen() {
		_internetShut = false;

		try {
			File.Delete(ShutPath);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("dashboard: forgetting that it was shut to the internet", e);
		}
	}

	/// <summary>
	/// A right password from the internet, waiting for the code sent to Telegram: the code, the address it's for, when
	/// it runs out, and the wrong tries so far. The password alone never lets anyone in from outside while
	/// "Code on Telegram for sign-ins from outside" is on and Telegram is connected.
	/// </summary>
	/// <remarks>Key is the password it was right for (see <see cref="KeyOf"/>): changed before the code comes back, the code signs nobody in.</remarks>
	private sealed record PendingCode(string Code, string Ip, DateTime Expires, int Tries, string Key);

	private readonly ConcurrentDictionary<string, PendingCode> _pendingCodes = new(StringComparer.Ordinal);
	private readonly ConcurrentQueue<DateTime> _codesSent = new();
	internal const int CodeMinutes = 5;
	private const int CodeTries = 5;

	/// <summary>Codes sent in an hour, all addresses together: somebody with the password can't flood Telegram.</summary>
	private const int CodesPerHour = 6;

	/// <summary>Whether a sign-in from here needs the Telegram code as well as the password.</summary>
	private bool NeedsCode(bool internet) => internet && _cfg.WebSignInCode && Notifier.CanSendPrivately;
	private readonly DateTime _started = DateTime.UtcNow;

	private WebApplication? _app;

	// rep4rep's points are polled, not pushed, so the answer is cached rather than fetched per page view. One object,
	// swapped whole: as a tuple, a page reading it while a poll wrote it could get the new points with the old time.
	private sealed record PointsSnapshot(int Points, int Pending, DateTime At);

	private volatile PointsSnapshot? _pointsCache;
	private bool _warnedAboutBalance;
	private readonly SemaphoreSlim _pointsGate = new(1, 1);

	/// <summary>A checked backup waiting for the page to say yes - one at a time, for a few minutes.</summary>
	private (string Token, byte[] Bytes, DateTime Until)? _staged;
	private readonly Lock _restoreGate = new();
	private DateTime _lastPointsAttempt = DateTime.MinValue;

	public string Url { get; private set; } = "";

	/// <summary>The address and port the dashboard really listens on - "Listen on" and "Port" only change it at a restart.</summary>
	private string _listening = "";

	public WebHost(BotManager mgr, GlobalConfig cfg) {
		_mgr = mgr;
		_ = cfg;   // the bind address is read from the live config at StartAsync
		Current = this;
		LoadSessions();
		LoadBrake();
	}

	/// <summary>The running dashboard, for the console's 'set WebPassword' - which has to sign browsers out too.</summary>
	public static WebHost? Current { get; private set; }

	/// <summary>Every browser signs in again. For a changed password, from wherever it was changed.</summary>
	public void SignOutAll() {
		_sessions.Clear();
		SaveSessions();
	}

	// ── sessions that outlive a restart ──
	//
	// "Stay signed in for 7 days" meant until the next restart: sessions lived only in memory, so every restart, every
	// update and every night's "Update by itself" sent every browser - a phone included - back to the password.
	// Only a hash of each token is written, with a fingerprint of the password it was issued under (a password
	// changed while the app was closed, in the config file, throws them all away) - and the whole file is encrypted
	// like the other secrets, so not even the fingerprint can be tried against guesses.

	private static string SessionsPath => Path.Combine(ConfigStore.ConfigDir, "state", "web-sessions.json");

	private sealed record SavedSessions(string Key, Dictionary<string, long> Sessions);

	private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private string PasswordKey => KeyOf(_cfg.WebPassword);

	/// <summary>A fingerprint of one dashboard password - which one a sign-in was checked against.</summary>
	private static string KeyOf(string password) => Hash("nocat.farm/" + password);

	private void LoadSessions() {
		try {
			if (string.IsNullOrEmpty(_cfg.WebPassword) || !File.Exists(SessionsPath)) {
				return;
			}

			string stored = File.ReadAllText(SessionsPath);

			// Written as plain JSON by 1.4.7 only; anything else has to decrypt.
			string json = Secrets.IsPlain(stored) && stored.TrimStart().StartsWith('{') ? stored : Secrets.Unprotect(stored);

			if ((json.Length == 0) || (JsonSerializer.Deserialize<SavedSessions>(json) is not { } saved) || (saved.Key != PasswordKey)) {
				return;
			}

			foreach ((string hash, long ticks) in saved.Sessions) {
				DateTime expires = new(ticks, DateTimeKind.Utc);

				if (expires > DateTime.UtcNow) {
					_sessions[hash] = expires;
				}
			}

			// The 1.4.7 file, rewritten encrypted.
			if (Secrets.IsPlain(stored) && Secrets.Available) {
				SaveSessions();
			}
		} catch (Exception e) {
			// everybody signs in again - nothing worse
			Log.Failed("dashboard: reading the saved sign-ins", e);
		}
	}

	private readonly Lock _sessionsFile = new();

	private void SaveSessions() {
		try {
			lock (_sessionsFile) {
				Directory.CreateDirectory(Path.GetDirectoryName(SessionsPath)!);
				AtomicFile.Write(SessionsPath, Secrets.Protect(JsonSerializer.Serialize(new SavedSessions(PasswordKey,
					_sessions.Where(static kv => kv.Value > DateTime.UtcNow).ToDictionary(static kv => kv.Key, static kv => kv.Value.Ticks))), "dashboard"));
			}
		} catch (Exception e) {
			// they last until the next restart, then
			Log.Failed("dashboard: saving the sign-ins", e);
		}
	}

	// ── the sign-in brake, kept over a restart ──
	//
	// Only "switched itself off" was kept. The lockouts, the hour's pause and the day's count of wrong guesses from the
	// internet lived in memory, so every restart - every night's "Update by itself" - handed a guesser a fresh set of tries.

	private static string BrakePath => Path.Combine(ConfigStore.ConfigDir, "state", "signin-brake.json");

	/// <summary>The brake on disk: the misses in the last hour and day, when the pause ends (ticks, 0 for none), and each
	/// address's wrong guesses still counting [count, until ticks].</summary>
	internal sealed record SavedBrake(List<long>? Hour, List<long>? Day, long PausedUntil, Dictionary<string, long[]>? Failures);

	private readonly Lock _brakeFile = new();

	private void LoadBrake() {
		try {
			if (!File.Exists(BrakePath) || (JsonSerializer.Deserialize<SavedBrake>(File.ReadAllText(BrakePath)) is not { } saved)) {
				return;
			}

			DateTime now = DateTime.UtcNow;
			static DateTime At(long ticks) => new(Math.Clamp(ticks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc);

			lock (_brakeGate) {
				foreach (long t in (saved.Hour ?? []).Order()) {
					_internetMisses.Enqueue(Saved(At(t)));
				}

				foreach (long t in (saved.Day ?? []).Order()) {
					_internetMissesToday.Enqueue(Saved(At(t)));
				}

				_internetPausedUntil = Until(At(saved.PausedUntil), InternetPauseMinutes);
				PruneMisses(now);
			}

			foreach ((string ip, long[] f) in saved.Failures ?? []) {
				if ((f is [long count, long until]) && (count > 0) && (Until(At(until), LockoutMinutes) > now)) {
					_failures[ip] = ((int) Math.Min(count, int.MaxValue), Until(At(until), LockoutMinutes));
				}
			}

			// Never longer than it could have been when it was written. With the clock put back since (a wrong clock at start-up
			// set right, a PC dual-booting with Linux), an hour's lockout read back as lasting that much longer, and the day's
			// wrong guesses counted towards switching off for days.
			DateTime Saved(DateTime t) => t > now ? now : t;
			DateTime Until(DateTime t, int minutes) => t <= now ? DateTime.MinValue : t > now.AddMinutes(minutes) ? now.AddMinutes(minutes) : t;
		} catch (Exception e) {
			// a fresh brake, as before - nothing worse
			Log.Failed("dashboard: reading the sign-in lockouts", e);
		}
	}

	private void SaveBrake() {
		try {
			// One save at a time, its picture taken inside: two at once could otherwise write the older picture last.
			lock (_brakeFile) {
				DateTime now = DateTime.UtcNow;
				SavedBrake snap;

				lock (_brakeGate) {
					snap = new([.. _internetMisses.Select(static t => t.Ticks)], [.. _internetMissesToday.Select(static t => t.Ticks)],
						_internetPausedUntil > now ? _internetPausedUntil.Ticks : 0,
						_failures.Where(kv => (kv.Value.Count > 0) && (kv.Value.Until > now))
							.ToDictionary(static kv => kv.Key, static kv => new[] { kv.Value.Count, kv.Value.Until.Ticks }, StringComparer.Ordinal));
				}

				Directory.CreateDirectory(Path.GetDirectoryName(BrakePath)!);
				AtomicFile.Write(BrakePath, JsonSerializer.Serialize(snap));
			}
		} catch (Exception e) {
			// kept until the next restart, then
			Log.Failed("dashboard: saving the sign-in lockouts", e);
		}
	}

	public Task<bool> StartAsync() => StartAsync(_cfg.WebHost, _cfg.WebPort);

	/// <summary>True when "Listen on" or "Port" were changed and the dashboard still listens the old way.</summary>
	public bool NeedsRestart => _listening != $"{_cfg.WebHost}:{_cfg.WebPort}";

	/// <summary>Why the last "Restart the dashboard" couldn't open on the new address, or null.</summary>
	public string? RestartProblem { get; private set; }

	private int _relistening;

	/// <summary>
	/// "Restart the dashboard now": stops listening and starts again with "Listen on" and "Port" as they are saved -
	/// the only settings that need it. Only the dashboard restarts; every account stays signed in. When the new
	/// address won't open (the port is taken, an address this PC doesn't have), it goes back to the old one, so the
	/// dashboard never just disappears, and says why.
	/// </summary>
	public async Task RelistenAsync() {
		if (Interlocked.Exchange(ref _relistening, 1) == 1) {
			return;
		}

		try {
			int colon = _listening.LastIndexOf(':');
			string oldHost = colon > 0 ? _listening[..colon] : _cfg.WebHost;
			int oldPort = (colon > 0) && int.TryParse(_listening[(colon + 1)..], out int p) ? p : _cfg.WebPort;
			string newHost = _cfg.WebHost;
			int newPort = _cfg.WebPort;

			await StopListeningAsync().ConfigureAwait(false);

			if (await StartAsync(newHost, newPort).ConfigureAwait(false)) {
				RestartProblem = null;

				return;
			}

			RestartProblem = new Said("It couldn't open on {0}:{1}, so it's back on {2}:{3} - see the Log.", newHost, newPort, oldHost, oldPort).ToString();
			await StopListeningAsync().ConfigureAwait(false);
			await StartAsync(oldHost, oldPort).ConfigureAwait(false);
		} finally {
			Volatile.Write(ref _relistening, 0);
		}
	}

	private async Task StopListeningAsync() {
		if (_app is not { } app) {
			return;
		}

		_app = null;

		try {
			await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
		} catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) {
			// a request that wouldn't finish - it goes with the old listener
		}

		await app.DisposeAsync().ConfigureAwait(false);
	}

	private async Task<bool> StartAsync(string listenHost, int listenPort) {
		try {
			// The pages live next to the exe, not wherever it was started from. Left to the default (the working
			// directory), a restart by the updater - which runs from the temp folder - served a 404 for everything.
			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
				ContentRootPath = AppContext.BaseDirectory,
				WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
			});
			builder.Logging.ClearProviders();   // our own log is the log; Kestrel's chatter would drown it
			_listening = $"{listenHost}:{listenPort}";
			// An IPv6 address goes in brackets ("::" is http://[::]:7242): bare, the colons read as a port and it never opened.
			builder.WebHost.UseUrls($"http://{Platform.UrlHost(listenHost)}:{listenPort}");
			builder.WebHost.ConfigureKestrel(static o => o.AddServerHeader = false);

			// PascalCase on the wire, matching the config FILES exactly. ASP.NET's default is camelCase, which
			// meant the settings page read every key as undefined - it showed empty boxes and would have SAVED
			// them back blank. Same names everywhere: C# property, JSON file, API payload, UI field.
			builder.Services.ConfigureHttpJsonOptions(static o => {
				o.SerializerOptions.PropertyNamingPolicy = null;
				o.SerializerOptions.PropertyNameCaseInsensitive = true;

				// SettingKind has to arrive as "AppIds", not 5. As a number every field silently fell through to
				// a plain text box - the form looked fine and quietly stopped being a form.
				o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
			});

			_app = builder.Build();

			// Before anything else, static files included - see Refusal for what this turns away and why.
			_app.Use(async (HttpContext ctx, RequestDelegate next) => {
				// Never inside another site's frame (clickjacking: a page laying its own buttons over Confirm or Remove),
				// never a guessed file type, and no address of this dashboard handed to the sites its links open.
				IHeaderDictionary h = ctx.Response.Headers;
				h.XFrameOptions = "DENY";
				h.ContentSecurityPolicy = "frame-ancestors 'none'";
				h.XContentTypeOptions = "nosniff";
				h["Referrer-Policy"] = "no-referrer";

				if (Refusal(ctx) is { } why) {
					ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
					ctx.Response.ContentType = "text/plain; charset=utf-8";
					await ctx.Response.WriteAsync(Loc.T(why)).ConfigureAwait(false);

					return;
				}

				await next(ctx).ConfigureAwait(false);
			});

			// A request that throws answers with a reason the page can show. The bare 500 it used to get had no body,
			// so the page failed reading it and the button just did nothing.
			_app.Use(async (HttpContext ctx, RequestDelegate next) => {
				try {
					await next(ctx).ConfigureAwait(false);
				} catch (Exception e) when (!ctx.RequestAborted.IsCancellationRequested && !ctx.Response.HasStarted) {
					Log.Warn(new Said("dashboard: {0} failed: {1}", ctx.Request.Path.Value ?? "", Log.Scrub(e.Message)));

					// The whole exception, scrubbed: it used to go in as one unscrubbed Debug line.
					Log.StackToFile(e);

					string error = new Said("that didn't work: {0}", e.Message).ToString();
					ctx.Response.Clear();
					await Results.Json(new { ok = false, error, Message = error }, statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(ctx).ConfigureAwait(false);
				}
			});

			_app.UseDefaultFiles();

			// Revalidate every time rather than letting the browser guess.
			//
			// With no Cache-Control header at all a browser falls back to heuristic caching, so after an update the
			// dashboard could keep running the previous app.js against the new style.css - a half-updated page that
			// looks broken and that no amount of normal refreshing fixes. "no-cache" still answers 304 when nothing
			// changed, so this costs a conditional request, not a re-download.
			_app.UseStaticFiles(new StaticFileOptions {
				OnPrepareResponse = static ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
			});

			MapApi(_app);

			await _app.StartAsync().ConfigureAwait(false);

			string host = listenHost.Trim('[', ']') is "0.0.0.0" or "*" or "+" or "::" ? "localhost" : Platform.UrlHost(listenHost);
			Url = $"http://{host}:{listenPort}/";

			// The parenthetical is a Said too. As a bare string it was a finished English phrase by the time the
			// sentence around it was translated, so a Chinese log read "仪表盘:http://... (this PC only - no
			// password set)" - which is the whole class of bug this pass exists to remove.
			Log.Good(new Said("dashboard: {0}{1}", Url,
				string.IsNullOrEmpty(_cfg.WebPassword) ? new Said("  (this PC only, no password)") : default));

			// Headless runs (Linux, Docker) are reached from another machine by definition, so a setup that can't
			// be is worth more than the one-line hint above. Nothing is exposed either way - with no password the
			// server lets in loopback only (see Authorised) - but Docker's port mapping arrives from the container's
			// gateway, not from loopback, so that dashboard turns everybody away and needs saying why.
			if (!OperatingSystem.IsWindows()) {
				if (!Platform.IsLoopback(listenHost) && string.IsNullOrEmpty(_cfg.WebPassword)) {
					Log.Warn(new Said("dashboard on {0} has no password - only this machine gets in", listenHost));
					Log.Info(new Said("set WebPassword (NOCATFARM_WEB_PASSWORD in Docker)"));
				} else if (Platform.InContainer && Platform.IsLoopback(listenHost)) {
					Log.Warn(new Said("dashboard on {0} can't be reached from outside Docker", listenHost));
					Log.Info(new Said("set NOCATFARM_WEB_HOST=0.0.0.0 to open it"));
				}
			}

			return true;
		} catch (Exception e) {
			Log.Error(new Said("couldn't start the dashboard on {0}:{1} - {2}", listenHost, listenPort, Log.Scrub(e.Message)));
			Log.Info("the console still works - change WebPort or turn WebEnabled off");

			return false;
		}
	}

	// ── auth ────────────────────────────────────────────────────────────────
	/// <summary>
	/// Why a request isn't even allowed to reach the dashboard, before any password question - or null when it is.
	///
	/// "No password" trusts anything that arrives from this PC - and a web page open in this PC's browser arrives
	/// from this PC. Two tricks use that. DNS rebinding: some other site points its own name at 127.0.0.1, and its
	/// page can then READ this dashboard (Steam Guard codes on the Authenticator page included) as if it were that
	/// site. The browser still sends that site's name as the Host, so only names that can't be pointed anywhere
	/// else are let in. And a plain cross-site POST: a page anywhere can submit to localhost and press Stop, Confirm
	/// or Remove without reading the answer. The browser always says where such a request came from, so anything
	/// that changes something has to come from this page itself.
	/// </summary>
	private string? Refusal(HttpContext ctx) {
		HttpRequest request = ctx.Request;

		// "Open from anywhere" off means the internet doesn't get in - checked here, on every request, and not only by
		// taking the router's forward away. A phone that had the page open on mobile data kept its connection through the
		// router after the forward was gone and carried on as if nothing had changed. "Public address" set by hand is
		// somebody's own forward, so that one still opens.
		//
		// Shut after too many wrong guesses, it's shut to the internet through a proxy too - the visitor the proxy names, as
		// the guesses were counted. By the connection alone, a proxy on this PC (or a trusted one) arrives from home: the
		// alert said "nothing from outside gets in now", and a browser already signed in from outside carried on regardless.
		bool direct = RemoteAccess.FromTheInternet(ctx.Connection.RemoteIpAddress);

		if ((direct && !_cfg.WebRemoteAccess && string.IsNullOrWhiteSpace(_cfg.WebPublicAddress)) || ((direct || FromOutside(ctx)) && InternetShut())) {
			ctx.Response.Headers.Connection = "close";
			Visitors.Note(Visitors.What.TurnedAway, direct ? ctx.Connection.RemoteIpAddress!.ToString() : WhoIsSigningIn(ctx).Ip, false, request.Headers.UserAgent.FirstOrDefault());

			return "Open from anywhere is off, so this dashboard only opens at home.";
		}

		// With a password, somebody who bound it to 0.0.0.0 opens it by the PC's LAN address or name, so any Host is
		// fine there - the password is what guards it. Without one, or while it only listens on this PC, it can only
		// honestly be called localhost.
		bool thisPcOnly = string.IsNullOrEmpty(_cfg.WebPassword) || Platform.IsLoopback(_cfg.WebHost);

		if (thisPcOnly && !IsLoopbackName(request.Host.Host)) {
			return "This dashboard only opens as http://localhost on the PC it runs on. To open it from another device, set a dashboard password and listen on 0.0.0.0.";
		}

		// With no password, "from this PC" is all that guards it - and a reverse proxy on this PC makes every visitor on the
		// internet arrive from this PC, with a Host of localhost when the proxy rewrites it (nginx does by default). A
		// request the proxy says it forwarded came from somewhere else.
		if (string.IsNullOrEmpty(_cfg.WebPassword)
			&& (request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("X-Real-IP") || request.Headers.ContainsKey("Forwarded"))) {
			return "This dashboard has no password, so it only opens on the PC it runs on - not through a proxy. Set a dashboard password first.";
		}

		// A GET from another website - an image or a script pointed at /api/... - carries no Origin to look at, and with no
		// password it counts as signed in from this PC. The import scan takes a path, and a network-share path there had
		// Windows sign in to somebody else's server with the user's login. The browser says where it came from here.
		if (CrossSiteApi(request)) {
			return "Refused: that came from another website, not from this dashboard.";
		}

		if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) {
			return null;
		}

		string? origin = request.Headers.Origin.FirstOrDefault();

		// No Origin at all is a script or an old browser on this PC, not a web page - pages always send one on a POST.
		bool fromHere = string.IsNullOrEmpty(origin)
			|| SameOrigin(origin, request.Host)
			|| (request.Headers["X-Forwarded-Host"].FirstOrDefault() is { Length: > 0 } forwarded && SameOrigin(origin, new HostString(forwarded)));

		return fromHere ? null : "Refused: that came from another website, not from this dashboard.";
	}

	/// <summary>A request to the dashboard's API that a page on another website made - as the browser itself says.</summary>
	public static bool CrossSiteApi(HttpRequest request) =>
		request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
		&& string.Equals(request.Headers["Sec-Fetch-Site"].FirstOrDefault(), "cross-site", StringComparison.OrdinalIgnoreCase);

	/// <summary>localhost, or any loopback address written as one (127.0.0.1, [::1]) - nothing a website can own.</summary>
	private static bool IsLoopbackName(string host) =>
		host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
		|| (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));

	/// <summary>
	/// Whether the page that sent a request is this dashboard: same name, same port.
	///
	/// The X-Forwarded-Host fallback is for a reverse proxy that rewrites Host to its own upstream name (nginx does by
	/// default). A page on another site can't forge that header - a custom header makes the browser ask first, and
	/// this server never says yes. A Host with no port means the default port of the Origin's scheme, so an https
	/// proxy that passes "farm.example.com" through still matches "https://farm.example.com".
	/// </summary>
	private static bool SameOrigin(string origin, HostString host) {
		if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? from) || !host.HasValue) {
			return false;   // "null" (a sandboxed frame, a file on disk) is never this page
		}

		return string.Equals(from.Host.Trim('[', ']'), host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
			&& (from.Port == (host.Port ?? (from.Scheme == Uri.UriSchemeHttps ? 443 : 80)));
	}

	private bool Authorised(HttpContext ctx) {
		IPAddress ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;

		if (string.IsNullOrEmpty(_cfg.WebPassword)) {
			// No password means local only. Never open a Steam account panel to the network by accident.
			return IPAddress.IsLoopback(ip);
		}

		string? token = ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.Ordinal)
			?? ctx.Request.Cookies["nocatfarm"];

		return !string.IsNullOrEmpty(token) && _sessions.TryGetValue(Hash(token), out DateTime expires) && (expires > DateTime.UtcNow);
	}

	/// <summary>
	/// Checks the password. Right from home: signed in (<paramref name="token"/>). Right from the internet with the code
	/// on: <paramref name="needsCode"/>, and no session yet. Wrong: counted towards the address's lockout and the
	/// internet brake.
	/// </summary>
	private bool TryLogin(HttpContext ctx, string password, out string token, out bool needsCode, out string passwordKey) {
		token = "";
		needsCode = false;
		passwordKey = "";
		(string ip, bool thisPc) = WhoIsSigningIn(ctx);
		bool internet = !thisPc && IsInternet(ip);
		string? device = ctx.Request.Headers.UserAgent.FirstOrDefault();

		// Not even tried while it's shut (or the internet brake is on - see Reserve): a right guess mid-attack would still
		// be a stranger's.
		if (internet && InternetShut()) {
			return false;
		}

		// The guess takes its place in the count before the password is looked at. Checked first and counted after, a
		// burst of guesses sent all at once all passed the lockout check before any of them had been counted.
		if (Reserve(ip, thisPc, internet, device) is not { } attempt) {
			return false;
		}

		// Constant-time compare: a check that returns early leaks the password one character at a time. Of the hashes,
		// so every character counts: padded and cut to 64, "pw  " passed for "pw" and a long password only needed its
		// first 64 characters. The password is read once, and the session is issued only while it is still this one.
		string current = _cfg.WebPassword;
		bool ok = CryptographicOperations.FixedTimeEquals(
			SHA256.HashData(Encoding.UTF8.GetBytes(password)),
			SHA256.HashData(Encoding.UTF8.GetBytes(current)));

		if (!ok) {
			CountFailure(attempt, Visitors.What.WrongPassword);

			return false;
		}

		passwordKey = KeyOf(current);

		// The wrong-guess count stays until the code is right too, so guessing codes adds to the same lockout. The right
		// password itself isn't a wrong guess: its place in the count is given back.
		if (NeedsCode(internet)) {
			GiveBack(attempt);
			needsCode = true;

			return true;
		}

		return SignIn(ctx, attempt, passwordKey, out token);
	}

	/// <summary>False when the password changed since it was checked - then nobody is signed in by it.</summary>
	private bool SignIn(HttpContext ctx, Attempt attempt, string passwordKey, out string token) {
		GiveBack(attempt);
		token = "";

		if (NewSession(ctx, passwordKey) is not { } fresh) {
			return false;
		}

		token = fresh;

		// On disk too: kept there, the wrong guesses before this right one came back with the next restart, and counted
		// towards a lockout again.
		if (_failures.TryRemove(attempt.Ip, out _)) {
			SaveBrake();
		}

		Visitors.Note(Visitors.What.SignedIn, attempt.Ip, attempt.ThisPc, attempt.Device);

		foreach (string stale in _sessions.Where(static kv => kv.Value < DateTime.UtcNow).Select(static kv => kv.Key).ToArray()) {
			_sessions.TryRemove(stale, out _);
		}

		return true;
	}

	/// <summary>
	/// One guess's place in the counts - this address's wrong guesses and, from the internet, the brake's - taken before
	/// the password or code is compared, and then either kept as a wrong one (<see cref="CountFailure"/>) or given back
	/// (<see cref="GiveBack"/>), once.
	/// </summary>
	private sealed class Attempt(string ip, bool thisPc, bool internet, string? device, int lockout, (int Count, DateTime Until)? before, (int Count, DateTime Until) taken) {
		public string Ip { get; } = ip;
		public bool ThisPc { get; } = thisPc;
		public bool Internet { get; } = internet;
		public string? Device { get; } = device;
		public int Lockout { get; } = lockout;

		/// <summary>This address's count as it was before this guess took its place; null when there wasn't one.</summary>
		public (int Count, DateTime Until)? Before { get; } = before;

		/// <summary>The count with this guess in it, as this guess wrote it.</summary>
		public (int Count, DateTime Until) Taken { get; } = taken;

		private int _settled;

		/// <summary>True the first time only: a guess is counted or given back once.</summary>
		public bool Settle() => Interlocked.Exchange(ref _settled, 1) == 0;
	}

	/// <summary>
	/// A place in the counts for one more guess from <paramref name="ip"/>, or null when there's none: that address is
	/// locked out, the internet brake is on, or enough guesses from the internet are already being checked to reach it.
	/// </summary>
	private Attempt? Reserve(string ip, bool thisPc, bool internet, string? device) {
		int lockout = thisPc ? LockoutMinutesThisPc : LockoutMinutes;

		if (internet) {
			lock (_brakeGate) {
				if (!InternetRoom(DateTime.UtcNow)) {
					return null;
				}

				_internetInFlight++;
			}
		}

		DateTime now = DateTime.UtcNow;
		bool locked = false;
		(int Count, DateTime Until)? before = null;

		// Counted atomically: guesses sent all at once each read the same count and wrote back the same "one more". A
		// locked-out address's count is left as it is - still locked, not locked for longer.
		(int Count, DateTime Until) taken = _failures.AddOrUpdate(ip,
			_ => {
				locked = false;
				before = null;

				return (1, now.AddMinutes(lockout));
			},
			(_, prev) => {
				before = prev;
				locked = (prev.Count >= MaxFailedLogins) && (prev.Until > now);

				return locked ? prev : (FailuresAfter(prev, now), now.AddMinutes(lockout));
			});

		if (locked) {
			if (internet) {
				lock (_brakeGate) {
					_internetInFlight = Math.Max(0, _internetInFlight - 1);
				}
			}

			return null;
		}

		return new Attempt(ip, thisPc, internet, device, lockout, before, taken);
	}

	/// <summary>Room for one more guess from the internet: the brake is off, and every guess being checked could be wrong without reaching it.</summary>
	private bool InternetRoom(DateTime now) {
		PruneMisses(now);

		if (_internetPausedUntil > now) {
			return false;
		}

		int offAfter = _cfg.WebRemoteOffAfter;

		if ((offAfter > 0) && (_internetInFlight > 0) && (_internetMissesToday.Count + _internetInFlight >= offAfter)) {
			return false;
		}

		return _internetMisses.Count + _internetInFlight < InternetMissesPerHour;
	}

	/// <summary>Misses older than the hour, and the day, dropped. Under <see cref="_brakeGate"/>.</summary>
	private void PruneMisses(DateTime now) {
		while (_internetMisses.TryPeek(out DateTime oldest) && (now - oldest > TimeSpan.FromHours(1))) {
			_internetMisses.Dequeue();
		}

		while (_internetMissesToday.TryPeek(out DateTime oldest) && (now - oldest > TimeSpan.FromHours(24))) {
			_internetMissesToday.Dequeue();
		}
	}

	/// <summary>A guess that wasn't a wrong one (the right password waiting for its code, a code that had already gone): out of the counts again.</summary>
	private void GiveBack(Attempt attempt) {
		if (!attempt.Settle()) {
			return;
		}

		if (attempt.Internet) {
			lock (_brakeGate) {
				_internetInFlight = Math.Max(0, _internetInFlight - 1);
			}
		}

		// The count as it was before, when nothing has changed it since; otherwise this one guess taken off it.
		bool restored = attempt.Before is { } was
			? _failures.TryUpdate(attempt.Ip, was, attempt.Taken)
			: _failures.TryRemove(new KeyValuePair<string, (int Count, DateTime Until)>(attempt.Ip, attempt.Taken));

		while (!restored && _failures.TryGetValue(attempt.Ip, out (int Count, DateTime Until) now)) {
			restored = _failures.TryUpdate(attempt.Ip, (Math.Max(0, now.Count - 1), now.Until), now);
		}
	}

	/// <summary>A wrong password or a wrong code: towards this address's lockout, and from the internet, the brake.</summary>
	private void CountFailure(Attempt attempt, Visitors.What what) {
		if (!attempt.Settle()) {
			return;
		}

		try {
			CountSettledFailure(attempt, what);
		} finally {
			SaveBrake();
		}
	}

	private void CountSettledFailure(Attempt attempt, Visitors.What what) {
		(string ip, bool thisPc, string? device) = (attempt.Ip, attempt.ThisPc, attempt.Device);

		// Already in the count - its place was taken before the compare (see Reserve).
		int count = attempt.Taken.Count;

		if (count >= MaxFailedLogins) {
			Log.Warn(new Said("dashboard: {0} failed logins from {1} - locked out for {2}m", count, ip, attempt.Lockout));
			Visitors.Note(Visitors.What.LockedOut, ip, thisPc, device, count, attempt.Lockout);

			// Locked out: whatever code it was waiting on is gone too.
			foreach (string id in _pendingCodes.Where(kv => kv.Value.Ip == ip).Select(static kv => kv.Key).ToArray()) {
				_pendingCodes.TryRemove(id, out _);
			}
		} else {
			Visitors.Note(what, ip, thisPc, device);
		}

		if (!attempt.Internet) {
			return;
		}

		DateTime now = DateTime.UtcNow;
		int shutWith = 0, pausedWith = 0;

		// Its place among the guesses being checked becomes a miss, and what that adds up to is decided in the same step.
		lock (_brakeGate) {
			_internetInFlight = Math.Max(0, _internetInFlight - 1);
			_internetMisses.Enqueue(now);
			_internetMissesToday.Enqueue(now);
			PruneMisses(now);

			int offAfter = _cfg.WebRemoteOffAfter;

			if ((offAfter > 0) && (_internetMissesToday.Count >= offAfter) && !_internetShut) {
				shutWith = _internetMissesToday.Count;
			} else if (_internetMisses.Count >= InternetMissesPerHour) {
				pausedWith = _internetMisses.Count;
				_internetMisses.Clear();
				_internetPausedUntil = now.AddMinutes(InternetPauseMinutes);
			}
		}

		if (shutWith > 0) {
			ShutToTheInternet(ip, thisPc, device, shutWith);

			return;
		}

		if (pausedWith > 0) {
			_pendingCodes.Clear();
			Visitors.Note(Visitors.What.Paused, ip, thisPc, device, pausedWith, InternetPauseMinutes);
		}
	}

	/// <summary>"Turn Open from anywhere off after" reached: the setting goes off (the router forward with it), and nothing
	/// from the internet gets in until it's turned on again.</summary>
	private void ShutToTheInternet(string ip, bool thisPc, string? device, int misses) {
		lock (_shutGate) {
			// Two last guesses at once both reach the limit: it shuts once.
			if (_internetShut) {
				return;
			}

			// The setting goes off BEFORE the flag goes up: "shut while Open from anywhere is on" is what reopening looks
			// for, so a request seeing the two the other way round mid-shut opened it again straight away.
			if (_cfg.WebRemoteAccess) {
				_cfg.WebRemoteAccess = false;
				ConfigStore.SaveGlobal(_cfg);
				RemoteAccess.Poke();
			}

			_internetShut = true;

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(ShutPath)!);
				File.WriteAllText(ShutPath, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// shut until the next restart, then - the setting itself is off and saved either way
				Log.Failed("dashboard: remembering that it's shut to the internet", e);
			}
		}

		lock (_brakeGate) {
			_internetMissesToday.Clear();
			_internetMisses.Clear();
		}

		_pendingCodes.Clear();
		Visitors.Note(Visitors.What.ClosedToInternet, ip, thisPc, device, misses);
	}

	/// <summary>
	/// Sends a fresh sign-in code to Telegram for this address and returns what the page quotes back with it, or null
	/// when it couldn't be sent (Telegram down, or too many codes this hour) - then nobody gets in from outside.
	/// </summary>
	private async Task<string?> SendCodeAsync(string ip, bool thisPc, string? device, string passwordKey, CancellationToken ct) {
		DateTime now = DateTime.UtcNow;

		while (_codesSent.TryPeek(out DateTime oldest) && (now - oldest > TimeSpan.FromHours(1))) {
			_codesSent.TryDequeue(out _);
		}

		if (_codesSent.Count >= CodesPerHour) {
			Log.Warn(new Said("dashboard: {0} sign-in codes sent this hour - no more until it's over", CodesPerHour));

			return null;
		}

		// One code per address at a time: a new sign-in replaces the last, and every stale one goes.
		foreach (string old in _pendingCodes.Where(kv => (kv.Value.Ip == ip) || (kv.Value.Expires < now)).Select(static kv => kv.Key).ToArray()) {
			_pendingCodes.TryRemove(old, out _);
		}

		string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
		string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		_codesSent.Enqueue(now);

		Said message = new("nocat.farm dashboard sign-in code: {0}\n\nSomebody at {1} ({2}) typed the right dashboard password from outside your home. The code works for {3} minutes.\n\nNot you? Don't give the code to anyone. Send /visitors signout here, then change the dashboard password.",
			code, ip, Visitors.Device(device), CodeMinutes);

		if (!await Notifier.SendPrivateNowAsync(message.ToString(), ct).ConfigureAwait(false)) {
			Log.Warn(new Said("dashboard: couldn't send the sign-in code to Telegram - nobody can sign in from outside until it can"));

			return null;
		}

		_pendingCodes[id] = new PendingCode(code, ip, now.AddMinutes(CodeMinutes), 0, passwordKey);
		Visitors.Note(Visitors.What.CodeSent, ip, thisPc, device);

		return id;
	}

	/// <summary>
	/// The wrong guesses to count after one more: counted afresh once the last one has had its time. Four typos last week
	/// and one today used to lock the owner out on the first slip, and after a lockout ran out a single mistyped password
	/// started a whole new one.
	/// </summary>
	internal static int FailuresAfter((int Count, DateTime Until)? previous, DateTime now) =>
		(previous is { } p && (p.Until > now) ? p.Count : 0) + 1;

	/// <summary>
	/// A fresh signed-in session for whoever sent this request: remembered here, and handed back as the cookie. Only under
	/// the password it was checked against (<paramref name="passwordKey"/>, see <see cref="KeyOf"/>) - null once that has
	/// changed.
	/// </summary>
	private string? NewSession(HttpContext ctx, string passwordKey) {
		string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
		string hash = Hash(token);
		DateTime expires = DateTime.UtcNow.AddDays(Math.Clamp(_cfg.WebSessionDays, 1, 90));
		_sessions[hash] = expires;

		// Looked at again once the session is in. The old password right, then the new one saved and everybody signed out
		// before this session existed: it outlived the change it was meant to end. Changed after this look, the sign-out
		// that follows every change takes it with the rest.
		if (!string.Equals(PasswordKey, passwordKey, StringComparison.Ordinal)) {
			_sessions.TryRemove(hash, out _);

			return null;
		}

		SaveSessions();

		ctx.Response.Cookies.Append("nocatfarm", token, new CookieOptions {
			HttpOnly = true,
			SameSite = SameSiteMode.Strict,
			Expires = expires
		});

		return token;
	}

	// ── endpoints ───────────────────────────────────────────────────────────
	private void MapApi(WebApplication app) {
		app.MapGet("/api/ping", (HttpContext ctx) => Results.Json(new {
			ok = true,
			needsPassword = !string.IsNullOrEmpty(_cfg.WebPassword),
			authorised = Authorised(ctx)
		}));

		// Who has been here, for the Phone page - and a way to sign every browser and phone out.
		app.MapGet("/api/visitors", (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			return Results.Json(new {
				Visits = Visitors.Recent(15).Select(static v => new { v.When, v.Ip, v.Where, What = Visitors.Words(v.What), Kind = v.What.ToString(), v.Device }),
				InternetPausedFor = PausedUntil is { } paused && (paused > DateTime.UtcNow) ? (int) Math.Ceiling((paused - DateTime.UtcNow).TotalSeconds) : 0
			});
		});

		app.MapPost("/api/visitors/signout", (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			SignOutAll();
			Log.Info(new Said("dashboard: every browser and phone signed out"));

			return Results.Json(new { ok = true });
		});

		app.MapPost("/api/login", async (HttpContext ctx) => {
			LoginRequest? body = await ReadJsonAsync<LoginRequest>(ctx).ConfigureAwait(false);

			if (body == null || !TryLogin(ctx, body.Password ?? "", out string token, out bool needsCode, out string passwordKey)) {
				// Shut to the internet isn't a wait - it opens when the owner turns it on again, so it says that instead.
				if (ShutToThis(ctx)) {
					return Closed();
				}

				// Locked out says so, with how long - "wrong password" for the right one while it waited was the most
				// confusing thing the page could say.
				int wait = LockedFor(ctx);

				return wait > 0
					? Results.Json(new { ok = false, error = "locked", seconds = wait, thisPc = WhoIsSigningIn(ctx).ThisPc }, statusCode: 429)
					: Results.Json(new { ok = false, error = "wrong password" }, statusCode: 401);
			}

			if (needsCode) {
				(string ip, bool thisPc) = WhoIsSigningIn(ctx);
				string? challenge = await SendCodeAsync(ip, thisPc, ctx.Request.Headers.UserAgent.FirstOrDefault(), passwordKey, ctx.RequestAborted).ConfigureAwait(false);

				return challenge == null
					? Results.Json(new { ok = false, error = "no code" }, statusCode: 503)
					: Results.Json(new { ok = false, error = "code", challenge, minutes = CodeMinutes }, statusCode: 401);
			}

			return Results.Json(new { ok = true, token });
		});

		// The second step from outside: the code Telegram got. Only for the address the code was sent for, five tries,
		// and every wrong one counts towards the same lockout and brake as a wrong password.
		app.MapPost("/api/login/code", async (HttpContext ctx) => {
			CodeRequest? body = await ReadJsonAsync<CodeRequest>(ctx).ConfigureAwait(false);
			(string ip, bool thisPc) = WhoIsSigningIn(ctx);
			string? device = ctx.Request.Headers.UserAgent.FirstOrDefault();

			if (ShutToThis(ctx)) {
				return Closed();
			}

			if (LockedFor(ctx) is > 0 and int wait) {
				return Results.Json(new { ok = false, error = "locked", seconds = wait, thisPc }, statusCode: 429);
			}

			if ((body == null) || !_pendingCodes.TryGetValue(body.Challenge ?? "", out PendingCode? pending)
				|| (pending.Ip != ip) || (pending.Expires < DateTime.UtcNow)) {
				return Results.Json(new { ok = false, error = "expired" }, statusCode: 401);
			}

			// Its place in the lockout and the brake, taken before the code is compared - as for a password.
			if (Reserve(ip, thisPc, !thisPc && IsInternet(ip), device) is not { } attempt) {
				return Results.Json(new { ok = false, error = "locked", seconds = Math.Max(1, LockedFor(ctx)), thisPc }, statusCode: 429);
			}

			string typed = new((body.Code ?? "").Where(char.IsAsciiDigit).ToArray());

			// The try is spent before the code is even looked at, and only by whoever swaps the record first: guesses sent
			// all at once each read "0 tries" and got five more each. A right code signs in only if this request is the one
			// that takes the record away - not after a lockout, the brake or another request already took it.
			int tries = pending.Tries + 1;
			PendingCode spent = pending with { Tries = tries };

			if (!_pendingCodes.TryUpdate(body.Challenge!, spent, pending)) {
				GiveBack(attempt);

				return Results.Json(new { ok = false, error = "expired" }, statusCode: 401);
			}

			if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed.PadRight(6)), Encoding.ASCII.GetBytes(pending.Code))) {
				if (!_pendingCodes.TryRemove(new KeyValuePair<string, PendingCode>(body.Challenge!, spent))) {
					GiveBack(attempt);

					return Results.Json(new { ok = false, error = "expired" }, statusCode: 401);
				}

				// Under the password the code was sent for: changed since, the code signs nobody in.
				if (!SignIn(ctx, attempt, pending.Key, out string token)) {
					return Results.Json(new { ok = false, error = "expired" }, statusCode: 401);
				}

				return Results.Json(new { ok = true, token });
			}

			if (tries >= CodeTries) {
				_pendingCodes.TryRemove(new KeyValuePair<string, PendingCode>(body.Challenge!, spent));
			}

			CountFailure(attempt, Visitors.What.WrongCode);

			return LockedFor(ctx) is > 0 and int locked
				? Results.Json(new { ok = false, error = "locked", seconds = locked, thisPc }, statusCode: 429)
				: Results.Json(new { ok = false, error = tries >= CodeTries ? "too many tries" : "wrong code", left = CodeTries - tries }, statusCode: 401);
		});

		// ── live state ──
		app.MapGet("/api/status", (HttpContext ctx) => Guard(ctx, () => Results.Json(BuildStatus())));

		// Opens only on the PC nocat.farm runs on, and only for someone sitting at it: from a phone it would pop up a
		// window on a screen nobody is looking at. Everyone gets the path.
		app.MapPost("/api/logs/open", (HttpContext ctx) => Guard(ctx, () => Results.Json(new {
			Path = Log.Folder,
			Opened = (Log.Folder is { } dir) && WhoIsSigningIn(ctx).ThisPc && Platform.OpenFolder(dir)
		})));

		app.MapGet("/api/log", (HttpContext ctx, int? n, long? since) => Guard(ctx, () => Results.Json(
			(since.HasValue ? Log.Since(since.Value) : Log.Recent(Math.Clamp(n ?? 200, 1, 1000)))
			.Select(static e => new {
				Seq = e.Seq,
				Time = e.When.ToString("HH:mm:ss"),
				Level = e.Level,
				Source = e.Source,
				Text = e.Text
			}))));

		// Day-by-day totals for the Overview's history charts: one slot per day, oldest first, ending today. Game names
		// come along for every appID in it; any not known yet are looked up in the background for the next poll.
		app.MapGet("/api/history", (HttpContext ctx, int? days) => Guard(ctx, () => {
			History.View history = History.Query(days ?? 30, _mgr.All.Select(static b => b.Name));
			ResolveNamesLater(history.Unknown);

			return Results.Json(history);
		}));

		app.MapGet("/api/commands", (HttpContext ctx) => Guard(ctx, () => Results.Json(Commands.All)));

		// ── importing from ArchiSteamFarm ──
		app.MapGet("/api/import/asf", (HttpContext ctx) => Guard(ctx, () => {
			string? dir = AsfImport.Detect();

			return Results.Json(new {
				Found = dir != null,
				Path = dir ?? "",
				Accounts = dir == null ? [] : AsfImport.Preview(dir).Select(static c => new {
					c.Name,
					c.SteamLogin,
					c.HasToken,
					c.HasPassword
				})
			});
		}));

		app.MapPost("/api/import/asf", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			ImportRequest? body = await ReadJsonAsync<ImportRequest>(ctx).ConfigureAwait(false);
			string? dir = string.IsNullOrWhiteSpace(body?.Path) ? AsfImport.Detect() : body.Path;

			if (dir == null || !Directory.Exists(dir)) {
				return Results.Json(new { ok = false, error = "Couldn't find an ArchiSteamFarm config folder there." });
			}

			AsfImport.Result result = AsfImport.Run(dir, _mgr.Global, body?.Overwrite ?? false, body?.Human);
			await _mgr.SyncFromDiskAsync().ConfigureAwait(false);
			Log.Good(new Said("imported {0} account(s) from ArchiSteamFarm", result.Imported));

			return Results.Json(new { ok = true, result.Imported, result.Skipped, result.Notes });
		});

		// ── importing from any idler: the list, a preview, then only what was confirmed ──
		// Every program's usual places, looked at now, and the "coming from" choice the installer left.
		app.MapGet("/api/import/tools", (HttpContext ctx) => Guard(ctx, () => {
			(string Tool, string? Path)? pending = IdlerImport.FromPending(PendingImport.Load());

			return Results.Json(new {
				Pending = pending == null ? null : new { pending.Value.Tool, Path = pending.Value.Path ?? "" },
				Tools = IdlerImport.Tools.Select(static t => {
					ImportScan scan = IdlerImport.Scan(t.Id, null);

					return new {
						t.Id, t.Name, scan.Found, Path = scan.Path ?? "", Accounts = scan.Accounts.Count, Tokens = scan.Accounts.Count(static a => a.Token != null),
						Settings = scan.Settings.Count, scan.Looked
					};
				})
			});
		}));

		app.MapGet("/api/import/scan", (HttpContext ctx, string? tool, string? path) => Guard(ctx, () =>
			Results.Json(ScanJson(IdlerImport.Scan(tool ?? IdlerImport.Auto, path)))));

		app.MapPost("/api/import/apply", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			ApplyImportRequest? body = await ReadJsonAsync<ApplyImportRequest>(ctx).ConfigureAwait(false);

			// Read again here rather than taking anything back from the browser: passwords and tokens never leave the server.
			ImportScan scan = IdlerImport.Scan(body?.Tool ?? "", body?.Path);

			if (!scan.Found) {
				return Results.Json(new { ok = false, error = new Said("Nothing to import was found there any more.").ToString() });
			}

			List<IdlerImport.Pick> picks = (body?.Accounts ?? []).Select(static a => new IdlerImport.Pick(a.Key ?? "", a.Human, a.SignIn, a.SteamLogin)).ToList();
			IdlerImport.Outcome outcome = IdlerImport.Apply(scan, picks, _mgr.Global, body?.Overwrite ?? false, body?.Settings);
			await _mgr.SyncFromDiskAsync().ConfigureAwait(false);
			PendingImport.Clear();
			Log.Good(new Said("imported {0} account(s) from {1}", outcome.Imported, scan.ToolName));

			return Results.Json(new {
				ok = true,
				outcome.Imported,
				outcome.Skipped,
				outcome.Names,
				outcome.Human,
				Notes = outcome.Notes.Select(static n => n.ToString())
			});
		});

		// Just the installer's hint, without looking anywhere - asked on every start of the dashboard.
		app.MapGet("/api/import/pending", (HttpContext ctx) => Guard(ctx, () => {
			(string Tool, string? Path)? pending = IdlerImport.FromPending(PendingImport.Load());

			return Results.Json(new { Pending = pending == null ? null : new { pending.Value.Tool, Path = pending.Value.Path ?? "" } });
		}));

		// The installer's "coming from" is a one-time hint: gone once imported, or once the first-run setup is closed.
		app.MapPost("/api/import/pending/clear", (HttpContext ctx) => Guard(ctx, () => {
			PendingImport.Clear();

			return Results.Json(new { ok = true });
		}));

		// ── the settings schema: why the form has real labels, tooltips, ranges and defaults with no duplication ──
		app.MapGet("/api/settings/schema", (HttpContext ctx) => Guard(ctx, () => Results.Json(new {
			Global = Settings.Global,
			Bot = Settings.Bot,
			GlobalDefaults = Settings.GlobalDefaults,
			BotDefaults = Settings.BotDefaults,

			// Sent rather than repeated in app.js. The same palette paints the window, the console board and
			// this page, and a second copy in JavaScript is precisely how the two "what is this account doing"
			// implementations drifted apart until one of them was silently hiding a warning.
			NameColours = Core.NameColour.All.Select(static c => c.Css).ToArray()
		})));

		// AppID -> real game name, so the weights editor reads "Counter-Strike 2" rather than "730". Unknown ones
		// are looked up once against Steam and then cached to disk forever, because names don't change.
		app.MapGet("/api/appnames", async (HttpContext ctx, string? ids) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			List<uint> wanted = [];

			foreach (string token in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
				if (uint.TryParse(token, out uint appId)) {
					wanted.Add(appId);
				}
			}

			if (wanted.Count > 0) {
				await GameNames.ResolveAsync(wanted, ctx.RequestAborted).ConfigureAwait(false);
			}

			return Results.Json(wanted.ToDictionary(static a => a.ToString(System.Globalization.CultureInfo.InvariantCulture), static a => GameNames.Of(a)));
		});

		// ── commands + prompts ──
		app.MapPost("/api/command", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			CommandRequest? body = await ReadJsonAsync<CommandRequest>(ctx).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(body?.Line)) {
				return Results.Json(new { output = "" });
			}

			string output = await Commands.RunAsync(_mgr, body.Line).ConfigureAwait(false);

			return Results.Json(new { output });
		});

		// Installing an update, only ever because the button was pressed. There is no GET here and no schedule
		// anywhere that reaches it - see SelfUpdate for why updating is never something that just happens.
		// What is installed, and the switch for each. Restart-gated on purpose: a plugin subscribes to events
		// during load, so flipping one on mid-run would give it a half-wired view of a fleet already running.
		app.MapGet("/api/plugins", (HttpContext ctx) => Guard(ctx, () => Results.Json(new {
			Enabled = Live.Global.PluginsEnabled,
			Folder = Plugins.PluginHost.Folder,
			Installed = Plugins.PluginHost.Discovered.Select(static p => new {
				p.Name, p.Version, p.File, Enabled = !Live.Global.DisabledPlugins.Contains(p.Name, StringComparer.OrdinalIgnoreCase),
				Settings = Plugins.PluginHost.SettingsOf(p.Name).Select(static x => new {
					x.Setting.Name, x.Setting.Label, x.Setting.Help,
					Kind = x.Setting.Kind.ToString(),
					x.Setting.Choices,
					Value = x.Value
				})
			}),
			Commands = Plugins.PluginHost.Commands.Select(static c => new { Verb = c.Key, c.Value.Usage, c.Value.Help })
		})));

		app.MapPost("/api/plugins/setting", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			PluginSettingChange? body = await ReadJsonAsync<PluginSettingChange>(ctx).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(body?.Plugin) || string.IsNullOrWhiteSpace(body.Name)) {
				return Results.Json(new { Ok = false, Message = Loc.T("which plugin, and which setting?") });
			}

			bool ok = Plugins.PluginHost.SetSetting(body.Plugin, body.Name, body.Value ?? "");

			// Ok says whether it took; the page used to toast "saved" whatever came back.
			return Results.Json(new { Ok = ok, Message = ok ? Loc.T("saved") : Loc.T("no such plugin setting") });
		});

		app.MapPost("/api/plugins/toggle", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			PluginToggle? body = await ReadJsonAsync<PluginToggle>(ctx).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(body?.Name)) {
				return Results.Json(new { Message = "which plugin?" });
			}

			// Copied, changed and put back under the lock every change to the live settings takes: two switches flipped at
			// once each copied the list before the other put its copy back, and one of them was lost.
			lock (GlobalSaveGate) {
				List<string> off = [.. Live.Global.DisabledPlugins];

				_ = body.Enabled
					? off.RemoveAll(n => string.Equals(n, body.Name, StringComparison.OrdinalIgnoreCase))
					: off.Contains(body.Name, StringComparer.OrdinalIgnoreCase) ? 0 : Add(off, body.Name);

				Live.Global.DisabledPlugins = off;
				ConfigStore.SaveGlobal(Live.Global);
			}
			Log.Info(body.Enabled
				? new Said("plugin {0} switched on - takes effect after a restart", body.Name)
				: new Said("plugin {0} switched off - takes effect after a restart", body.Name));

			// In the page's language - the toast shows it as it comes.
			return Results.Json(new { Message = (body.Enabled
				? new Said("{0} is on after a restart.", body.Name)
				: new Said("{0} is off after a restart.", body.Name)).ToString() });

			static int Add(List<string> list, string name) {
				list.Add(name);

				return 1;
			}
		});

		app.MapPost("/api/update", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			string? problem = await UpdateCheck.LookAsync(force: true, quiet: true).ConfigureAwait(false);

			if ((problem != null) && (UpdateCheck.Available == null)) {
				return Results.Json(new { Ok = false, Message = new Said("couldn't reach GitHub to check ({0}) - try again in a minute", problem).ToString() });
			}

			// Newer, but with no download for this machine yet - said so, not "you're on the newest".
			if ((UpdateCheck.Available == null) && (UpdateCheck.NoDownloadYet is { } coming)) {
				return Results.Json(new { Message = UpdateCheck.NoDownloadSaid(coming, queued: UpdateCheck.Queued != null).ToString() });
			}

			if (UpdateCheck.Available == null) {
				return Results.Json(new { Message = new Said("You're on the newest release ({0}).", Build.Version).ToString() });
			}

			// Pressing it is choosing that version after all, as 'update accept' is. Left skipped, a queued install
			// was dropped at bedtime without a word, after the button had said it would go in.
			if (UpdateCheck.Skipped != null) {
				UpdateCheck.Skipped = null;
			}

			// Updates set to "once everyone's asleep": the button queues it for when the accounts are asleep, like 'update accept'.
			if (Live.Global.UpdateClickWaits) {
				return Results.Json(new { Message = UpdateCheck.Queue(UpdateCheck.Available).ToString() });
			}

			string? failure = await SelfUpdate.ApplyAsync(CancellationToken.None).ConfigureAwait(false);

			// Ok says whether it's on its way, so the page can show a failure as one.
			return Results.Json(new {
				Ok = failure == null,
				Message = failure ?? Loc.T("Downloading. It restarts by itself when it lands.")
			});
		});

		// ── backup & restore ──
		// The download is a POST, not a link: it holds every saved login, so only this page (never another site sending
		// the browser here) may ask for it - POSTs are the ones checked for where they came from.
		app.MapPost("/api/backup", (HttpContext ctx) => Guard(ctx, () => {
			if (FromOutside(ctx)) {
				return HomeOnly();
			}

			byte[] zip = Backup.Create();
			Log.Info(new Said("backup downloaded from the dashboard ({0} KB)", (zip.Length + 1023) / 1024));

			return Results.File(zip, "application/zip", Backup.FileName(DateTime.Now));
		}));

		// Step one of a restore: the zip is checked and held here for a few minutes, and the page is told what's in it.
		// Nothing is written until step two, after the page has asked.
		app.MapPost("/api/restore/check", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (FromOutside(ctx)) {
				return HomeOnly();
			}

			if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) {
				limit.MaxRequestBodySize = Backup.MaxZipBytes + 1;
			}

			byte[] bytes;

			try {
				using MemoryStream ms = new();
				await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
				bytes = ms.ToArray();
			} catch (Exception e) when (e is BadHttpRequestException or IOException) {
				return Results.Json(new { Ok = false, Error = new Said("that file is too big to be a nocat.farm backup").ToString() });
			}

			Backup.Inspection check = Backup.Inspect(bytes);

			if (!check.Ok) {
				Log.Warn(new Said("dashboard: a backup was refused - {0}", check.Error!));

				return Results.Json(new { Ok = false, check.Error });
			}

			string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

			lock (_restoreGate) {
				_staged = (token, bytes, DateTime.UtcNow.AddMinutes(15));
			}

			Backup.Manifest m = check.Manifest!;

			return Results.Json(new {
				Ok = true,
				Token = token,
				Created = m.Created,
				m.Version,
				m.Machine,
				m.Windows,
				check.SameMachine,
				check.Accounts,
				Files = check.Files.Count,
				Kinds = check.Files.GroupBy(static f => Backup.Kind(f) ?? "").ToDictionary(static g => g.Key, static g => g.Count())
			});
		});

		// Step two: the page said yes. Every account stops, the files go back and everything starts again.
		app.MapPost("/api/restore/apply", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (FromOutside(ctx)) {
				return HomeOnly();
			}

			RestoreRequest? body = await ReadJsonAsync<RestoreRequest>(ctx).ConfigureAwait(false);
			byte[]? bytes = null;

			lock (_restoreGate) {
				if ((_staged is { } s) && (body?.Token is { Length: > 0 } t) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s.Token), Encoding.UTF8.GetBytes(t))
					&& (s.Until > DateTime.UtcNow)) {
					bytes = s.Bytes;
					_staged = null;
				}
			}

			if (bytes == null) {
				return Results.Json(new { Ok = false, Error = new Said("that restore has run out - pick the file again").ToString() });
			}

			string passwordBefore = _cfg.WebPassword;
			string message = await Backup.RestoreAsync(_mgr, bytes).ConfigureAwait(false);

			// A different dashboard password came back with it: everyone else signs in again, this browser carries on.
			string? fresh = null;
			string restored = _cfg.WebPassword;

			if (!string.Equals(passwordBefore, restored, StringComparison.Ordinal)) {
				SignOutAll();
				fresh = string.IsNullOrEmpty(restored) ? null : NewSession(ctx, KeyOf(restored));
			}

			return Results.Json(new { Ok = true, Message = message, Token = fresh });
		});

		// The Notifications section's test button: one message to each place that's set up, and what happened.
		app.MapPost("/api/notify/test", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			// Each line with whether it worked. The page used to decide that with an English regex over text that is
			// translated by the time it gets there, so in any other language a failure came up as a green toast.
			List<string> lines = await Notifier.TestAsync(ctx.RequestAborted).ConfigureAwait(false);

			return Results.Json(new {
				Results = lines.Select(static line => new {
					Ok = Loc.Is(line, "Discord: sent - check the channel") || Loc.Is(line, "Telegram: sent - check the chat"),
					Text = line
				})
			});
		});

		// The Connect Discord button: a fresh one-time code to send the bot with /connect, and where the bot stands. A
		// POST, as it changes something - the code before it stops working.
		app.MapPost("/api/discord/connect", (HttpContext ctx) => Guard(ctx, () => Results.Json(new {
			Online = Notifier.DiscordBotOnline,
			BotName = Notifier.DiscordBotName,
			Connected = Notifier.DiscordConnected,
			OwnerId = _mgr.Global.DiscordOwnerId,
			Owner = Notifier.DiscordConnected ? Notifier.DiscordOwner : "",
			InviteUrl = Notifier.DiscordInviteUrl,
			Code = Notifier.HasDiscordBot ? Notifier.NewDiscordCode() : null,
			Minutes = Notifier.DiscordCodeMinutes
		})));

		app.MapPost("/api/prompt", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			PromptRequest? body = await ReadJsonAsync<PromptRequest>(ctx).ConfigureAwait(false);

			return Results.Json(new { ok = Prompt.Answer(body?.Value ?? "") });
		});

		// ── config ──
		app.MapGet("/api/config", (HttpContext ctx) => Guard(ctx, () => {
			// Secrets never go to the browser. The UI shows "(set)" and an empty box means "leave it alone" on
			// save, so a round-trip can't quietly erase a password or an API token.
			// Driven off the registry rather than a hand-written list. Naming the secrets by hand here is how
			// three of them - the two authenticator seeds and the per-account proxy password - ended up being
			// served to the browser in clear AND reported as "not set", which is the worst of both: the value
			// leaves the machine and the UI tells you it was never stored.
			GlobalConfig global = Redact(Clone(_mgr.Global), Settings.Global, out List<string> globalSet);

			Dictionary<string, BotConfig> bots = [];
			Dictionary<string, string[]> botSecrets = [];

			foreach (Bot bot in _mgr.All) {
				bots[bot.Name] = Redact(Clone(bot.Cfg), Settings.Bot, out List<string> set);
				botSecrets[bot.Name] = [.. set];
			}

			return Results.Json(new {
				Global = global,
				Bots = bots,
				GlobalSecretsSet = globalSet,
				BotSecretsSet = botSecrets
			});
		}));

		app.MapPost("/api/config", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			JsonObject? sent = await ReadSentAsync(ctx).ConfigureAwait(false);
			GlobalSave? save = null;
			string savedPassword = "";

			// The live settings taken under the lock the save itself holds: read before it, a 'reload' in between left this
			// save writing onto the settings it had just thrown away.
			if (sent != null) {
				lock (GlobalSaveGate) {
					save = SaveFromPage(_mgr.Global, sent, JsonOptionsOf(ctx));
					savedPassword = _mgr.Global.WebPassword;
				}
			}

			if (save == null) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			if (save.Error is { } error) {
				return Results.Json(new { ok = false, error }, statusCode: 400);
			}

			List<string> adjusted = save.Adjusted;
			List<string> restartNeeded = save.RestartNeeded;
			bool passwordChanged = save.PasswordChanged;

			// Still put in force, but not called saved: a full disk or a damaged settings file used to come back "Saved."
			// and the change was gone at the next start.
			if (!save.Saved) {
				adjusted.Add(new Said("in use now, but not saved to disk - see the Log").ToString());
			}

			// The same object, changed in place - this only puts the token and the farming limit in step with it.
			_mgr.ApplyGlobal(_mgr.Global);
			GlobalConfig body = _mgr.Global;

			foreach (SettingDef def in Settings.Global) {
				Commands.ApplyGlobalSideEffects(_mgr, def);   // all global side effects are idempotent
			}

			Log.Info("global settings saved from the dashboard");

			// A changed password signs everybody out. Whoever knew the old one - or stole a session under it - kept
			// right on using the dashboard after it was changed, which is the one thing changing it is meant to stop.
			// The browser that made the change gets a fresh session, so saving doesn't lock out the person saving.
			string? fresh = null;

			if (passwordChanged) {
				SignOutAll();
				fresh = string.IsNullOrEmpty(savedPassword) ? null : NewSession(ctx, KeyOf(savedPassword));
				Log.Info(new Said("dashboard password changed - other browsers sign in again"));
			}

			return Results.Json(new { ok = true, RestartNeeded = restartNeeded, Adjusted = adjusted, Token = fresh });
		});

		app.MapPost("/api/bots/{name}/config", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { ok = false, error = "no such account" }, statusCode: 404);
			}

			// Read first and merged onto the account's settings as they are once it has arrived, not as they were when the
			// request came in - the same reason as the global save: a field the app wrote meanwhile (a game learned to be
			// banned during a send) isn't put back from the older copy. A Base from the page works here too.
			JsonObject? sent = await ReadSentAsync(ctx).ConfigureAwait(false);

			if (sent == null) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			if (SaveBotFromPage(bot, sent, JsonOptionsOf(ctx)) is not { } save) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			if (save.Error is { } error) {
				return Results.Json(new { ok = false, error }, statusCode: 400);
			}

			List<string> adjusted = save.Adjusted;
			List<SettingDef> changed = save.Changed;

			foreach (SettingDef def in changed) {
				Commands.ApplyBotSideEffects(bot, def);
			}

			Log.Info("settings saved from the dashboard", bot.Name);

			return Results.Json(new { ok = true, Adjusted = adjusted });
		});

		// ── account lifecycle ──
		// The walkthrough marks itself done. A one-line endpoint rather than a config round-trip, so finishing
		// the tutorial cannot accidentally write back a whole settings object the user never edited.
		// The dashboard theme, stored server-side so the toggle and the 'theme' command agree and the choice
		// follows you between browsers.
		// What the achievement pacer is doing, per game. Read-only.
		// Read an inventory again on demand. Prices are left alone - they're cached for a day and shared between
		// accounts, and re-fetching hundreds of them because somebody pressed a button is how you get refused.
		app.MapPost("/api/bots/{name}/inventory/refresh", (HttpContext ctx, string name) => Guard(ctx, () => {
			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { ok = false, error = "No such account." }, statusCode: 404);
			}

			bot.Inventory.ForceRefresh();

			return Results.Json(new { ok = true });
		}));

		// The QR code an account is waiting to have scanned, as a picture.
		app.MapGet("/api/bots/{name}/qr.svg", (HttpContext ctx, string name) => Guard(ctx, () =>
			_mgr.Get(name)?.QrChallenge is { } link ? Results.Text(Core.QrPicture.Svg(link), "image/svg+xml") : Results.NotFound()));

		app.MapGet("/api/bots/{name}/achievements", (HttpContext ctx, string name) => Guard(ctx, () => {
			Bot? bot = _mgr.Get(name);
			Modules.AchievementPacer? pacer = bot == null ? null : BotManager.ModuleOf<Modules.AchievementPacer>(bot);
			Modules.AchievementBoost? boost = bot == null ? null : BotManager.ModuleOf<Modules.AchievementBoost>(bot);
			(string Mode, string Now, List<Modules.AchievementBoost.PlanRow> Next, List<Modules.AchievementBoost.PlanRow> Out) plan =
				boost?.Plan() ?? ("off", "", [], []);

			return Results.Json(new {
				On = bot?.Cfg.UnlockAchievements ?? false,
				Games = pacer?.Snapshot() ?? [],
				// The games the owner left alone ('dlc leave'), so each can be taken back.
				DlcLeft = bot == null ? [] : DlcChoices.LeftPaused(bot),
				Recent = pacer?.Recent.Take(6) ?? [],
				Hunt = new {
					plan.Mode,
					plan.Now,
					Status = boost?.Status ?? "",
					Left = bot is { Grinding: true, GrindUntil: not null } ? (int) Math.Max(0, (bot.GrindUntil.Value - DateTime.UtcNow).TotalMinutes) : 0,
					Next = plan.Next.Take(8),
					NextCount = plan.Next.Count,
					OutReasons = Modules.AchievementBoost.Reasons(plan.Out).Take(5).Select(static r => new { r.Why, r.Count }),
					OutCount = plan.Out.Count
				}
			});
		}));

		// The owner's choice about one game with add-ons Steam doesn't explain: leave it alone, or take that back (the
		// dashboard's undo). 'carryon' is the old answer, still taken. Only the account's own choice, kept in its settings
		// file - nothing is unlocked by it.
		app.MapPost("/api/bots/{name}/achievements/dlc", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { ok = false, error = $"There is no account called {name}." }, statusCode: 404);
			}

			DlcAnswerRequest? body = await ReadJsonAsync<DlcAnswerRequest>(ctx).ConfigureAwait(false);

			if ((body == null) || (body.App == 0)) {
				return Results.Json(new { ok = false, error = "Which game?" }, statusCode: 400);
			}

			string answer = body.Answer?.Trim().ToLowerInvariant() ?? "";

			// Only a game with add-ons Steam doesn't explain, or one already left alone - not any number sent here.
			if ((answer == "leave") && !DlcChoices.Answerable(bot, body.App)) {
				return Results.Json(new { ok = false, error = new Said("{0}: as far as it has seen, {1} has no add-ons Steam doesn't explain, so there's nothing to leave alone.", bot.Name, GameNames.Of(body.App)).ToString() }, statusCode: 400);
			}

			Said? said = answer switch {
				"carryon" => DlcChoices.CarryOn(bot, body.App),
				"leave" => DlcChoices.Leave(bot, body.App),
				"undo" => DlcChoices.Undo(bot, body.App),
				_ => null
			};

			return said is { } done
				? Results.Json(new { ok = true, note = done.ToString() })
				: Results.Json(new { ok = false, error = "The answer is leave or undo." }, statusCode: 400);
		});

		app.MapPost("/api/theme", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			ThemeRequest? body = await ReadJsonAsync<ThemeRequest>(ctx).ConfigureAwait(false);
			string want = body?.Theme?.Trim().ToLowerInvariant() ?? "";

			if (want is not ("dark" or "light")) {
				return Results.Json(new { ok = false, error = "Theme is either dark or light." }, statusCode: 400);
			}

			lock (GlobalSaveGate) {
				Live.Global.Theme = want;
				ConfigStore.SaveGlobal(Live.Global);
			}

			return Results.Json(new { ok = true });
		});

		app.MapPost("/api/tutorial/done", (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			lock (GlobalSaveGate) {
				Live.Global.TutorialDone = true;
				ConfigStore.SaveGlobal(Live.Global);
			}

			return Results.Json(new { ok = true });
		});

		// Drag-to-arrange on the Accounts page. Names only - the order is a view preference, not account data.
		// Unlock every achievement across every owned game.
		//
		// Guarded hard in the browser, and guarded again here. An endpoint that does something irreversible must
		// not be one stray fetch away from doing it - a replayed request or a bookmarked URL would otherwise be
		// enough to permanently stamp several thousand achievements onto a profile.
		app.MapPost("/api/bots/{name}/achievements/unlock-all", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { ok = false, error = $"There is no account called {name}." }, statusCode: 404);
			}

			if (!bot.IsOnline) {
				return Results.Json(new { ok = false, error = $"{name} is not signed in, so its achievements cannot be read." }, statusCode: 400);
			}

			ConfirmRequest? body = await ReadJsonAsync<ConfirmRequest>(ctx).ConfigureAwait(false);

			if (!string.Equals(body?.Confirm?.Trim(), "confirm", StringComparison.OrdinalIgnoreCase)) {
				return Results.Json(new { ok = false, error = "Type the word confirm to do this." }, statusCode: 400);
			}

			if (!UnlockEverything.Start(bot)) {
				return Results.Json(new { ok = false, error = $"{name} is already unlocking everything." }, statusCode: 409);
			}

			return Results.Json(new { ok = true, note = "Started. Watch the log - it runs for a while." });
		});

		app.MapPost("/api/accounts/order", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			string[]? order = await ReadJsonAsync<string[]>(ctx).ConfigureAwait(false);

			if (order == null) {
				return Results.Json(new { ok = false, error = "No order was sent." }, statusCode: 400);
			}

			// Only names that exist, and each at most once. A stale name left in the file would silently push a
			// real account down the list by occupying a rank ahead of it.
			HashSet<string> known = new(_mgr.All.Select(static b => b.Name), StringComparer.OrdinalIgnoreCase);
			List<string> clean = [];

			foreach (string name in order) {
				if (known.Contains(name) && !clean.Contains(name, StringComparer.OrdinalIgnoreCase)) {
					clean.Add(name);
				}
			}

			lock (GlobalSaveGate) {
				Live.Global.AccountOrder = clean;
				ConfigStore.SaveGlobal(Live.Global);
			}

			return Results.Json(new { ok = true, order = clean });
		});

		app.MapPost("/api/bots", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			AddBotRequest? body = await ReadJsonAsync<AddBotRequest>(ctx).ConfigureAwait(false);

			if (body == null || string.IsNullOrWhiteSpace(body.Name) || (!body.Qr && string.IsNullOrWhiteSpace(body.SteamLogin))) {
				return Results.Json(new { ok = false, error = "A name and a Steam account name are both needed." }, statusCode: 400);
			}

			// The rule the message states, enforced here: the file-name check alone lets in spaces, quotes and
			// apostrophes, and a name like that turns into a different command the moment it's typed in the console.
			if (ConfigStore.NameProblem(body.Name) is { } problem) {
				return Results.Json(new { ok = false, error = problem.ToString() }, statusCode: 400);
			}

			if (!ConfigStore.IsPlainBotName(body.Name) || !ConfigStore.IsValidBotName(body.Name)) {
				return Results.Json(new { ok = false, error = Loc.T("Letters, numbers, dashes and underscores. 'nocatFarm' is taken by the global config.") }, statusCode: 400);
			}

			if (_mgr.Get(body.Name) != null) {
				return Results.Json(new { ok = false, error = $"'{body.Name}' already exists." }, statusCode: 400);
			}

			// Signing in by QR needs no account name - Steam says which account it was once the code is scanned.
			BotConfig cfg = body.Qr
				? new() { SteamLogin = body.Name, SignInWithQr = true }
				: new() { SteamLogin = body.SteamLogin!, SteamPassword = body.Password ?? "" };

			// Set before it's added, so its very first sign-in already behaves the way it was asked to - an
			// account switched to human mode a minute after a robot-style sign-in has already signed in like one.
			if (body.Human) {
				cfg.LegitMode = true;
				Settings.ApplyLegitMode(cfg, false);
			}

			cfg.IUseThisAccount = body.SelfSignIn;
			Bot? bot = await _mgr.AddAsync(body.Name, cfg).ConfigureAwait(false);

			return bot == null
				? Results.Json(new { ok = false, error = "Couldn't add that account." }, statusCode: 500)
				: Results.Json(new { ok = true });
		});

		app.MapDelete("/api/bots/{name}", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			return Results.Json(new { ok = await _mgr.RemoveAsync(name).ConfigureAwait(false) });
		});

		app.MapPost("/api/bots/{name}/{action}", async (HttpContext ctx, string name, string action) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { ok = false, error = "no such account" }, statusCode: 404);
			}

			switch (action.ToLowerInvariant()) {
				case "start":
					// Pressing Start on a disabled account has to mean "enable it", not start something the rest
					// of the UI will keep reporting as off while it quietly farms.
					if (!bot.Cfg.Enabled) {
						lock (bot.CfgGate) {
							bot.Cfg.Enabled = true;
							ConfigStore.SaveBot(bot.Name, bot.Cfg);
						}
					}

					await bot.StartAsync().ConfigureAwait(false);

					break;
				case "rep4repnow":
					BotManager.ModuleOf<Rep4RepModule>(bot)?.RunNow();

					break;
				case "stop":
					await bot.StopAsync(graceful: true).ConfigureAwait(false);

					break;
				case "pause":
					bot.Pause();

					break;
				case "resume":
					bot.Resume();
					BotManager.ModuleOf<Idler>(bot)?.Assert();

					break;
				default:
					return Results.Json(new { ok = false, error = "unknown action" }, statusCode: 400);
			}

			return Results.Json(new { ok = true });
		});

		// The account's own games, most played first - what the first-run setup offers as its main and side games.
		// Family-shared ones are left out: playing a borrowed game locks its owner out of it.
		// ── the authenticator page: codes and confirmations, like the Steam app ──
		// Where the dashboard opens from elsewhere, with a QR code of the phone link - scan it instead of typing.
		app.MapGet("/api/phone", (HttpContext ctx) => Guard(ctx, () => {
			Core.DashboardLinks.Links l = Core.DashboardLinks.For(Live.Global);
			string? scan = l.OpenAtHome && (l.Home.Count > 0) ? l.Home[0] : null;

			return Results.Json(new {
				l.Local, l.Home, l.OpenAtHome, l.HasPassword, l.ListensBeyondThisPc, l.Outside, l.FirewallBlocks, l.NeedsHomeAddress,
				RemoteOn = Live.Global.WebRemoteAccess, RemoteProblem = Core.RemoteAccess.Problem,
				SignInCode = Live.Global.WebSignInCode, CodeReady = Notifier.CanSendPrivately, OffAfter = Live.Global.WebRemoteOffAfter,
				// Saved, but still listening the old way until the dashboard restarts - the phone link won't answer yet.
				// Why the last restart didn't take - only while it still needs one, or an old failure outlives the fix.
				NeedsRestart, RestartProblem = NeedsRestart ? RestartProblem : null,
				Windows = OperatingSystem.IsWindows(), InDocker = Platform.InContainer,
				// The Public address typed by hand, rather than the one the router gave.
				ManualOutside = !string.IsNullOrWhiteSpace(Live.Global.WebPublicAddress),
				MinPassword = Core.RemoteAccess.MinPasswordLength,
				// Long enough to open it from anywhere - so the walkthrough can say so before a saved password it never sees fails.
				LongPassword = (Live.Global.WebPassword ?? "").Length >= Core.RemoteAccess.MinPasswordLength,
				// Through a reverse proxy on this PC every visitor arrives from loopback - WhoIsSigningIn knows the difference.
				OnThisPc = WhoIsSigningIn(ctx).ThisPc,
				Qr = scan == null ? null : Core.QrPicture.Svg(scan),
				QrOutside = l.OpenAtHome && (l.Outside != null) ? Core.QrPicture.Svg(l.Outside) : null
			});
		}));

		// "Restart the dashboard now", after Listen on or Port changed. The answer goes out first; the dashboard stops and
		// starts again a moment later on the new address, and the page waits for it to come back.
		app.MapPost("/api/phone/restart", (HttpContext ctx) => Guard(ctx, () => {
			if (!NeedsRestart) {
				return Results.Json(new { Ok = true, Needed = false, Port = _cfg.WebPort });
			}

			Log.Info(new Said("restarting the dashboard on {0}:{1} - accounts stay on", _cfg.WebHost, _cfg.WebPort));
			_ = Task.Run(async () => {
				await Task.Delay(500).ConfigureAwait(false);

				// Stopping the old listener can throw too, and nothing awaits this: the dashboard would be gone without a word.
				try {
					await RelistenAsync().ConfigureAwait(false);
				} catch (Exception e) {
					Log.Error(new Said("couldn't start the dashboard on {0}:{1} - {2}", _cfg.WebHost, _cfg.WebPort, Log.Scrub(e.Message)));
					Log.StackToFile(e);
				}
			});

			return Results.Json(new { Ok = true, Needed = true, Port = _cfg.WebPort });
		}));

		// "Allow through Windows Firewall": Windows' own prompt appears on this PC, so only this PC may ask for it.
		app.MapPost("/api/phone/firewall", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			// Asked the way the Log's "open the folder" asks: a visitor through a proxy on this PC arrives from loopback too.
			if (!OperatingSystem.IsWindows() || !WhoIsSigningIn(ctx).ThisPc) {
				return Results.Json(new { Ok = false, Error = new Said("only from the PC nocat.farm runs on").ToString() });
			}

			(bool ok, string why) = await NocatFarm.Windows.Firewall.AllowAsync(Live.Global.WebPort).ConfigureAwait(false);

			if (ok) {
				Log.Good(new Said("firewall opened port {0} for the dashboard on your network", Live.Global.WebPort));
			}

			return Results.Json(new { Ok = ok, Error = why });
		});

		app.MapGet("/api/auth", (HttpContext ctx) => Guard(ctx, () => Results.Json(new {
			Accounts = _mgr.All.Select(static b => {
				(string? code, int left) = Confirmations.Code(b);

				return new { b.Name, HasCode = b.HasAuthenticator, CanConfirm = b.CanConfirmTrades, Code = code, SecondsLeft = left };
			})
		})));

		app.MapGet("/api/auth/{name}/confirmations", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (_mgr.Get(name) is not { } bot) {
				return Results.Json(new { Ok = false, Error = "no such account" });
			}

			(bool ok, string error, List<Confirmations.Item> items) = await Confirmations.ListAsync(bot, ctx.RequestAborted).ConfigureAwait(false);
			List<object> rows = [];

			foreach (Confirmations.Item c in items) {
				object? offer = null;

				// A trade shows what goes and comes, with Steam's pictures - read from the offer the confirmation is for.
				if ((c.Type == Confirmations.Trade) && (c.CreatorId != 0)) {
					(bool answered, TradeOffers.Offer? o) = await TradeOffers.OneAsync(bot, c.CreatorId, ctx.RequestAborted).ConfigureAwait(false);

					if (answered && (o != null)) {
						offer = new {
							Give = o.Giving.Select(static i => new { i.Name, i.Icon, Amount = i.Amount }),
							Get = o.Receiving.Select(static i => new { i.Name, i.Icon, Amount = i.Amount })
						};
					}
				}

				rows.Add(new { Id = c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Type, c.TypeName, c.Headline, c.Summary, c.Icon, c.Created, Offer = offer });
			}

			Trading? trading = BotManager.ModuleOf<Trading>(bot);
			var rules = (trading?.Allowed() ?? []).Select(r => new {
				Who = _mgr.All.FirstOrDefault(b => b.SteamId == r.Key)?.Name ?? r.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
				Way = r.Value switch { Trading.Way.From => "only what they send", Trading.Way.To => "they can take items", _ => "both ways" }
			});

			return Results.Json(new { Ok = ok, Error = error, Items = rows, Rules = rules });
		});

		app.MapPost("/api/auth/{name}/confirmations", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			AuthActRequest? body = await ReadJsonAsync<AuthActRequest>(ctx).ConfigureAwait(false);

			if ((_mgr.Get(name) is not { } bot) || (body?.Ids is not { Count: > 0 } ids)) {
				return Results.Json(new { Ok = false, Error = "bad request" }, statusCode: 400);
			}

			(bool ok, string error, List<Confirmations.Item> items) = await Confirmations.ListAsync(bot, ctx.RequestAborted, fresh: true).ConfigureAwait(false);

			if (!ok) {
				return Results.Json(new { Ok = false, Error = error });
			}

			List<Confirmations.Item> picked = [.. items.Where(c => ids.Contains(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))];

			if (picked.Count == 0) {
				return Results.Json(new { Ok = false, Error = "already gone - the list has changed" });
			}

			bool done = await Confirmations.ActAsync(bot, picked, body.Accept, ctx.RequestAborted).ConfigureAwait(false);

			if (done) {
				Log.Trade(body.Accept
					? new Said("confirmed {0} from the Authenticator page: {1}", picked.Count, string.Join(", ", picked.Select(static c => $"{c.TypeName} {c.Headline}".Trim())))
					: new Said("denied {0} from the Authenticator page: {1}", picked.Count, string.Join(", ", picked.Select(static c => $"{c.TypeName} {c.Headline}".Trim()))), bot.Name, good: body.Accept);
			}

			return Results.Json(new { Ok = done, Done = done ? picked.Count : 0, Error = done ? "" : "Steam didn't take it - try again" });
		});

		// The first-run setup's picker: the 40 most played of its own games. With ?all=1, every game it can play - shared ones
		// too, marked - for the settings page's game lists to search by name. Filtered on the page, as it's typed: one fetch,
		// then nothing more to ask, however big the library.
		app.MapGet("/api/bots/{name}/library", (HttpContext ctx, string name, string? all) => Guard(ctx, () => {
			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { Ready = false, Games = Array.Empty<object>() });
			}

			if (all == "1") {
				return Results.Json(new {
					bot.Library.Ready,
					Games = bot.Library.Games.OrderByDescending(static g => g.MinutesPlayed)
						.Select(static g => new { g.AppId, g.Name, Minutes = g.MinutesPlayed, g.Shared }).ToList()
				});
			}

			return Results.Json(new {
				bot.Library.Ready,
				Games = bot.Library.Games.Where(static g => !g.Shared).OrderByDescending(static g => g.MinutesPlayed).Take(40)
					.Select(static g => new { g.AppId, g.Name, Minutes = g.MinutesPlayed }).ToList()
			});
		}));

		// Every account's games together, for the global game lists ("Never touch these (all accounts)"): one row a game,
		// with the hours of every account that has it added up.
		app.MapGet("/api/library", (HttpContext ctx) => Guard(ctx, () => {
			List<Bot> bots = [.. _mgr.All];

			return Results.Json(new {
				Ready = bots.Any(static b => b.Library.Ready),
				Games = bots.SelectMany(static b => b.Library.Games)
					.GroupBy(static g => g.AppId)
					.Select(static grp => new { AppId = grp.Key, grp.First().Name, Minutes = grp.Sum(static g => g.MinutesPlayed), Shared = grp.All(static g => g.Shared) })
					.OrderByDescending(static g => g.Minutes).ToList()
			});
		}));

		app.MapGet("/api/bots/{name}/cards", (HttpContext ctx, string name) => Guard(ctx, () => {
			Bot? bot = _mgr.Get(name);
			CardFarmer? farmer = bot == null ? null : BotManager.ModuleOf<CardFarmer>(bot);

			if (farmer == null) {
				return Results.Json(new { Status = "", Games = Array.Empty<object>() });
			}

			return Results.Json(new {
				Status = farmer.Status,
				Games = farmer.Queue.Select(static g => new {
					AppId = g.AppId,
					Name = g.GameName,
					Cards = g.CardsRemaining,
					Hours = Math.Round(g.HoursPlayed, 1)
				})
			});
		}));

		// ── rep4rep ──
		app.MapGet("/api/rep4rep", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (!_mgr.Rep4Rep.HasToken) {
				return Results.Json(new { Connected = false });
			}

			// Forced: somebody is looking straight at this number, and points move the moment they spend any.
			// The cheap cached value is fine for the overview tile; it is not fine here.
			(int Points, int PendingPoints)? user = await CachedPointsAsync(true).ConfigureAwait(false);

			return Results.Json(new {
				Connected = user != null,
				Error = user == null ? "rep4rep didn't accept that token, or it can't be reached right now." : null,
				Points = user?.Points ?? 0,
				Pending = user?.PendingPoints ?? 0,
				SyncedAt = _pointsCache?.At
			});
		});

		app.MapPost("/api/rep4rep/token", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			TokenRequest? body = await ReadJsonAsync<TokenRequest>(ctx).ConfigureAwait(false);
			string token = (body?.Token ?? "").Trim();

			if (token.Length == 0) {
				return Results.Json(new { ok = false, error = "Paste the token from rep4rep.com first." }, statusCode: 400);
			}

			// Validate BEFORE saving, so a typo can't quietly stop every account commenting - and on a client of its own. Put
			// on the shared one to be tried, every account's comments went out under the untested token while it was, and
			// putting the old one back afterwards undid a token saved from somewhere else in the meantime.
			(int Points, int PendingPoints)? user;

			using (Rep4RepApi trial = new() { Token = token }) {
				user = await trial.GetUserAsync(ctx.RequestAborted).ConfigureAwait(false);
			}

			if (user == null) {
				return Results.Json(new { ok = false, error = "That token isn't valid. Copy it again from rep4rep.com under Settings - it's the whole string, with no spaces." });
			}

			lock (GlobalSaveGate) {
				_mgr.Global.Rep4RepApiToken = token;
				_mgr.Rep4Rep.Token = token;
				ConfigStore.SaveGlobal(_mgr.Global);
			}
			RememberPoints(user.Value.Points, user.Value.PendingPoints);
			Log.Good(new Said("rep4rep connected - {0} points", user.Value.Points));

			return Results.Json(new { ok = true, Points = user.Value.Points, Pending = user.Value.PendingPoints });
		});

		app.MapGet("/api/rep4rep/profiles", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			List<(string Id, string SteamId)> registered = _mgr.Rep4Rep.HasToken
				? await _mgr.Rep4Rep.GetProfilesAsync().ConfigureAwait(false)
				: [];

			return Results.Json(_mgr.All.Select(bot => {
				Rep4RepModule? mod = BotManager.ModuleOf<Rep4RepModule>(bot);
				string steamId = bot.SteamId.ToString();
				(string Id, string SteamId) match = registered.FirstOrDefault(p => p.SteamId == steamId);

				return new {
					Account = bot.Name,
					SteamId = steamId,
					Rep4RepId = match.Id ?? "",
					Registered = match.Id != null,
					Enabled = bot.Cfg.Rep4Rep,
					Online = bot.IsOnline,
					Today = mod?.PostsToday ?? 0,
					Cap = mod?.Cap ?? bot.Cfg.Rep4RepDailyCap,
					Status = mod?.Status ?? "",
					LastPost = mod?.LastPost,
					NextSlot = mod?.NextSlot,
					CapIsSteamLimit = mod?.CapIsSteamLimit ?? false
				};
			}));
		});

		app.MapPost("/api/rep4rep/profiles/{name}/register", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? bot = _mgr.Get(name);

			if (bot == null || bot.SteamId == 0) {
				return Results.Json(new { ok = false, error = "That account has to be logged in before rep4rep can be told about it." });
			}

			string? id = await _mgr.Rep4Rep.ResolveProfileIdAsync(bot.SteamId, true).ConfigureAwait(false);

			return Results.Json(new {
				ok = id != null,
				error = id != null ? "" : "rep4rep wouldn't register that profile. Check the token, and that the Steam profile is public."
			});
		});

		app.MapPost("/api/rep4rep/tasks/{taskId}/post", async (HttpContext ctx, string taskId, string? bot) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? target = bot == null ? null : _mgr.Get(bot);
			Rep4RepModule? mod = target == null ? null : BotManager.ModuleOf<Rep4RepModule>(target);

			if (mod == null) {
				return Results.Json(new { ok = false, error = "no such account" }, statusCode: 404);
			}

			string result = await mod.PostNowAsync(taskId).ConfigureAwait(false);

			return Results.Json(new { ok = result.StartsWith("Posted", StringComparison.Ordinal), message = result });
		});

		app.MapGet("/api/rep4rep/tasks", async (HttpContext ctx, string? bot) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? target = bot == null ? _mgr.All.FirstOrDefault(static b => b.Cfg.Rep4Rep) : _mgr.Get(bot);

			if (target == null || !_mgr.Rep4Rep.HasToken) {
				return Results.Json(Array.Empty<object>());
			}

			string? profileId = await _mgr.Rep4Rep.ResolveProfileIdAsync(target.SteamId, false).ConfigureAwait(false);

			if (profileId == null) {
				return Results.Json(Array.Empty<object>());
			}

			List<Rep4RepTask> tasks = await _mgr.Rep4Rep.GetTasksAsync(profileId).ConfigureAwait(false);

			// rep4rep re-samples its task list on every request, so the ids on this page mean nothing once the
			// next request is made. Hand the batch to the module so its "Post now" can act on the row that was
			// actually clicked instead of hunting for an id that has already rotated away.
			BotManager.ModuleOf<Rep4RepModule>(target)?.Remember(tasks);

			return Results.Json(tasks.Select(static t => new {
				TaskId = t.TaskId,
				TargetSteamId = t.TargetSteamId.ToString(),
				TargetName = t.TargetName,
				Comment = t.CommentText
			}));
		});
	}

	/// <summary>
	/// Blank every SettingKind.Secret on a config clone, and report which ones actually held something.
	///
	/// The registry is the single source of truth for what counts as a secret, so declaring a new one in
	/// Settings.cs is enough - there is no second list here to forget to update, which is exactly how the two
	/// authenticator seeds and the per-account proxy password ended up being served to the browser in clear
	/// while the UI cheerfully reported them as "not set".
	/// </summary>
	private static T Redact<T>(T config, IReadOnlyList<SettingDef> defs, out List<string> wereSet) where T : class {
		wereSet = [];

		foreach (SettingDef def in defs) {
			if (def.Kind != SettingKind.Secret) {
				continue;
			}

			System.Reflection.PropertyInfo? property = typeof(T).GetProperty(def.Name);

			if ((property == null) || (property.PropertyType != typeof(string)) || !property.CanWrite) {
				continue;
			}

			if (property.GetValue(config) is string value && (value.Length > 0)) {
				wereSet.Add(def.Name);
			}

			property.SetValue(config, "");
		}

		return config;
	}

	/// <summary>
	/// Carry every unchanged secret across from the stored config, so an empty box never erases one.
	///
	/// Same reasoning as Redact: driven off the registry, because the by-hand version protected two secrets and
	/// would have silently wiped the other three on every save from the dashboard.
	/// </summary>
	private static void KeepSecrets<T>(T incoming, T existing, IReadOnlyList<SettingDef> defs) where T : class {
		foreach (SettingDef def in defs) {
			if (def.Kind != SettingKind.Secret) {
				continue;
			}

			System.Reflection.PropertyInfo? property = typeof(T).GetProperty(def.Name);

			if ((property == null) || (property.PropertyType != typeof(string)) || !property.CanWrite) {
				continue;
			}

			string sent = property.GetValue(incoming) as string ?? "";
			string stored = property.GetValue(existing) as string ?? "";
			property.SetValue(incoming, Keep(sent, stored));
		}
	}

	/// <summary>Empty means "unchanged"; the explicit sentinel the UI's Clear button sends means "erase it".</summary>
	private const string ClearSecret = "\0clear";

	/// <summary>What older pages sent for Clear. It was saved as the literal secret " clear" - a dashboard password
	/// nobody knew they had set - so it still means "erase it" for a page cached from before the fix.</summary>
	private const string OldClearSecret = " clear";

	private static string Keep(string incoming, string existing) =>
		incoming is ClearSecret or OldClearSecret ? "" : string.IsNullOrEmpty(incoming) ? existing : incoming;

	/// <summary>
	/// Clamp every numeric setting to the range the registry declares, so a hand-crafted POST (or a browser
	/// number box coercing an empty field to 0) can't set the comment pacing to zero and fire off ten comments
	/// back to back. The console already went through Settings.Apply; this closes the other door.
	/// </summary>
	private static List<string> Clamp(object config, IReadOnlyList<SettingDef> defs) {
		List<string> adjusted = [];

		foreach (SettingDef def in defs) {
			// Pick too (the language): an unknown code falls back to the default like a bad choice does.
			if (def.Kind is not (SettingKind.Int or SettingKind.Hour or SettingKind.Float or SettingKind.Choice or SettingKind.Pick)) {
				continue;
			}

			object? raw = Settings.Read(config, def.Name);

			if (raw == null) {
				continue;
			}

			string before = Settings.Show(config, def);
			string? error = Settings.Apply(config, def, Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "");

			if (error != null) {
				// Out of range or not a valid choice - put the default back rather than store nonsense.
				object? fallback = Settings.Read(defs == Settings.Global ? Settings.GlobalDefaults : Settings.BotDefaults, def.Name);
				Settings.Apply(config, def, Convert.ToString(fallback, System.Globalization.CultureInfo.InvariantCulture) ?? "");
				// A Said, as the other lines beside it are: the page shows it as it comes, in the page's language.
				adjusted.Add(new Said("{0} was {1}, reset to {2}", new Said(def.Label), before, Settings.Show(config, def)).ToString());
			}
		}

		return adjusted;
	}

	/// <summary>
	/// Run every game list that was changed through the same check the console uses, and say why the first bad one
	/// was refused - or null when they're all fine.
	///
	/// Refused rather than reset like Clamp does: resetting a list means emptying it, and throwing away somebody's
	/// forty games because one too many was added is worse than telling them. Only lists that CHANGED are checked,
	/// so one saved before this check existed can't block every other save on that account. Text goes through the same
	/// check as the console's 'set' too, so what the console refuses (a trusted proxy that isn't an address or a range)
	/// isn't "Saved." here and then quietly ignored; the text itself is kept exactly as it was sent.
	/// </summary>
	private static string? Invalid(object config, object current, IReadOnlyList<SettingDef> defs) {
		foreach (SettingDef def in defs) {
			if (Settings.Show(config, def) == Settings.Show(current, def)) {
				continue;
			}

			if ((def.Kind == SettingKind.AppIds) && (Settings.Read(config, def.Name) is IEnumerable<uint> apps)) {
				if (Settings.Apply(config, def, string.Join(',', apps)) is { } error) {
					return error;
				}
			} else if ((def.Kind == SettingKind.Text) && (Settings.Read(config, def.Name) is string text)) {
				string? error = Settings.Apply(config, def, text);
				config.GetType().GetProperty(def.Name)?.SetValue(config, text);

				if (error != null) {
					return error;
				}
			}
		}

		return null;
	}

	/// <summary>Every global field that isn't a setting, put back to what the app holds now.</summary>
	internal static void KeepServerFields(GlobalConfig body, GlobalConfig current) {
		foreach (PropertyInfo p in typeof(GlobalConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
			if (p.CanRead && p.CanWrite && (p.GetIndexParameters().Length == 0) && !Settings.Global.Any(d => d.Name == p.Name)) {
				p.SetValue(body, p.GetValue(current));
			}
		}
	}

	/// <summary>What a dashboard save of the global settings came to. Error set = refused, nothing changed.</summary>
	internal sealed record GlobalSave(string? Error, List<string> Adjusted, List<string> RestartNeeded, bool PasswordChanged, bool Saved);

	/// <summary>
	/// One dashboard save of the global settings at a time, from reading the live config to writing it back - the same lock
	/// as every other change to the live settings ('set', 'reload', a restore, an import), not one of the dashboard's own.
	/// </summary>
	private static Lock GlobalSaveGate => ConfigStore.GlobalEditGate;

	/// <summary>What a dashboard save of one account's settings came to. Error set = refused, nothing changed.</summary>
	internal sealed record BotSave(string? Error, List<string> Adjusted, List<SettingDef> Changed, bool Saved);

	/// <summary>
	/// A dashboard save of one account, put onto its live settings field by field under its lock (<see cref="Bot.CfgGate"/>),
	/// the way <see cref="SaveFromPage"/> does the global ones.
	/// </summary>
	/// <remarks>
	/// It used to merge onto a copy, save the copy and swap it in, with nothing held. Anything changed on the account in
	/// between - 'set', enable, play, a name, a game learned to be banned, a second tab's save - was undone in memory, and on
	/// disk too when its save landed first. Now every such change takes the same lock, and only what the page changed is
	/// written onto the settings the account runs on.
	/// </remarks>
	/// <returns>Null when the post couldn't be read as an account's settings.</returns>
	internal static BotSave? SaveBotFromPage(Bot bot, JsonObject sent, JsonSerializerOptions options) {
		lock (bot.CfgGate) {
			BotConfig current = Clone(bot.Cfg);

			if (Merge(current, sent, options) is not { } body) {
				return null;
			}

			KeepSecrets(body, current, Settings.Bot);

			if (Invalid(body, current, Settings.Bot) is { } error) {
				return new BotSave(error, [], [], false);
			}

			// Legit mode rewrites the config itself, so this has to happen before the diff and the save.
			// A skip changed in this same save is the owner's choice, and stays.
			Settings.ApplyLegitMode(body, current.LegitMode, skipChosen: body.AchievementSkipMultiplayer != current.AchievementSkipMultiplayer);

			List<string> adjusted = Clamp(body, Settings.Bot);

			// A max below the min would make every gap calculation nonsense; fix it rather than store it. This
			// covered only the rep4rep gap for years while seven other pairs went unchecked - it walks them all
			// now, from the same helper the console uses, so the two paths cannot drift apart again.
			// Whichever side was just changed keeps its number, as at the console.
			adjusted.AddRange(Settings.FixRanges(body, [.. Settings.Bot
				.Where(d => !Equals(Settings.Show(body, d), Settings.Show(current, d)))
				.Select(static d => d.Name)]).Select(static s => s.ToString()));

			// Only fire side effects for settings that ACTUALLY changed. Running them all meant editing a note
			// re-started an account the user had deliberately stopped.
			List<SettingDef> changed = Settings.Bot
				.Where(d => !Equals(Settings.Show(body, d), Settings.Show(current, d)))
				.ToList();

			CopyChanged(body, current, bot.Cfg);

			// The account's own name, not the one in the address: the lookup ignores case, and off Windows "MAIN" in the
			// address wrote MAIN.json beside main.json - two files for one account, either of which the next start read.
			bool saved = ConfigStore.SaveBot(bot.Name, bot.Cfg);

			if (!saved) {
				adjusted.Add(new Said("in use now, but not saved to disk - see the Log").ToString());
			}

			return new BotSave(null, adjusted, changed, saved);
		}
	}

	/// <summary>
	/// A dashboard save, put onto the live global config field by field: only what the page really changed is written.
	/// </summary>
	/// <remarks>
	/// The page posts its whole copy of the config. The app writes some settings itself - the Telegram chat and the
	/// Discord owner when you connect, the rep4rep hold when it runs out - and a copy taken before that, even
	/// milliseconds before, put the old value back: the chat was connected and then quietly gone again. Two things
	/// close that. The page sends the copy it loaded as "Base", and a field it sends back unchanged is left alone
	/// (see <see cref="Merge{T}"/>). And the result is written into the live config one changed field at a time rather
	/// than swapped in whole, so something the app writes while this runs isn't replaced by the copy this started from.
	/// </remarks>
	/// <returns>Null when the post couldn't be read as a config.</returns>
	internal static GlobalSave? SaveFromPage(GlobalConfig live, JsonObject sent, JsonSerializerOptions options) {
		lock (GlobalSaveGate) {
			GlobalConfig current = Clone(live);

			if (Merge(current, sent, options) is not { } body) {
				return null;
			}

			// An empty secret means "unchanged", never "erase it". Every Secret in the registry, not a list here.
			KeepSecrets(body, current, Settings.Global);

			// Only settings are changed here. A page without a Base (an old one, a script) still posts every field, so the
			// ones the app keeps for itself - the theme, the account order, the window's place - are put back regardless.
			KeepServerFields(body, current);

			if (Invalid(body, current, Settings.Global) is { } error) {
				return new GlobalSave(error, [], [], false, false);
			}

			List<string> adjusted = Clamp(body, Settings.Global);

			// The password is left out: the check reads the live config, so a new one is in force the moment it is
			// saved - telling people it waits for a restart had them leaving the old one "active" that wasn't.
			List<string> restartNeeded = Settings.Global
				.Where(d => d.NeedsRestart && (d.Name != nameof(GlobalConfig.WebPassword))
					&& !Equals(Settings.Read(body, d.Name)?.ToString(), Settings.Read(current, d.Name)?.ToString()))
				.Select(static d => d.Label)
				.ToList();

			bool passwordChanged = !string.Equals(body.WebPassword, current.WebPassword, StringComparison.Ordinal);

			CopyChanged(body, current, live);

			return new GlobalSave(null, adjusted, restartNeeded, passwordChanged, ConfigStore.SaveGlobal(live));
		}
	}

	/// <summary>Every field that differs between <paramref name="from"/> and <paramref name="before"/>, written onto <paramref name="onto"/>.</summary>
	private static void CopyChanged<T>(T from, T before, T onto) where T : class {
		foreach (PropertyInfo p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
			if (!p.CanRead || !p.CanWrite || (p.GetIndexParameters().Length > 0)) {
				continue;
			}

			// Compared as JSON, so a list with the same games in it counts as the same list.
			object? value = p.GetValue(from);

			if (JsonSerializer.Serialize(value, p.PropertyType) != JsonSerializer.Serialize(p.GetValue(before), p.PropertyType)) {
				p.SetValue(onto, value);
			}
		}
	}

	private static JsonSerializerOptions JsonOptionsOf(HttpContext ctx) => ctx.RequestServices
		.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

	/// <summary>A settings POST's body as JSON, or null when it isn't a JSON object.</summary>
	private static async Task<JsonObject?> ReadSentAsync(HttpContext ctx) {
		try {
			return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false) as JsonObject;
		} catch (Exception e) {
			// the page is told it didn't save; the why is here
			Log.Failed($"dashboard: reading the settings sent to {ctx.Request.Path.Value}", e);

			return null;
		}
	}

	/// <summary>The copy of the config the page loaded, sent along with a save so what it didn't change can be told apart.</summary>
	private const string PageBase = "Base";

	/// <summary>
	/// Read a settings POST as changes to the config it is saving, not as a whole new config. Null when it doesn't
	/// read as one.
	///
	/// Deserialising straight into a fresh object gave every field the page left out its default, so a POST with one
	/// key in it blanked the Steam login, the games and everything else. Now the current config is the starting
	/// point and only what was actually sent is written over it.
	/// </summary>
	internal static T? Merge<T>(T current, JsonObject sent, JsonSerializerOptions options) where T : class, new() {
		try {
			JsonObject merged = JsonSerializer.SerializeToNode(current, options)!.AsObject();

			// A field the page sends back exactly as it loaded it wasn't changed there. Its value is only as new as the
			// page, so writing it would undo whatever the app has put there since - it is left as the app has it.
			JsonObject? loaded = sent[PageBase] as JsonObject;

			foreach ((string key, JsonNode? value) in sent) {
				if ((key == PageBase) || ((loaded != null) && loaded.TryGetPropertyValue(key, out JsonNode? was) && JsonNode.DeepEquals(was, value))) {
					continue;
				}

				// Names are matched the way the endpoint always read them - ignoring case - so "steamlogin" replaces
				// SteamLogin instead of sitting beside it and losing.
				string name = merged.Select(static kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
				merged[name] = value?.DeepClone();
			}

			// A null sent for a list is the default list, as in a file: a null one crashed whatever read it next.
			return merged.Deserialize<T>(options) is { } read ? ConfigStore.FillNulls(read) : null;
		} catch (Exception e) {
			// A field of the wrong type (text where a number goes): the whole save is turned down - say which.
			Log.Failed($"dashboard: the {typeof(T).Name} settings sent don't fit", e);

			return null;
		}
	}

	/// <summary>How often a "forced" refresh may actually reach rep4rep. See below for why this exists.</summary>
	private static readonly TimeSpan ForcedRefreshFloor = TimeSpan.FromSeconds(45);

	/// <summary>The cached points as the page wants them, from one read of the cache; null when there are none yet.</summary>
	private (int Points, int PendingPoints)? CachedPoints() => _pointsCache is { } c ? (c.Points, c.Pending) : null;

	private async Task<(int Points, int PendingPoints)?> CachedPointsAsync(bool force) {
		int ttl = Math.Clamp(_mgr.Global.Rep4RepPointsRefreshMinutes, 1, 1440);

		if (_pointsCache is { } cached) {
			TimeSpan age = DateTime.UtcNow - cached.At;

			// "Force" means "prefer fresh", not "ask again right now, every time you are asked".
			//
			// The rep4rep panel polls on a timer, so forcing unconditionally turned one open browser tab into a
			// request to rep4rep every single second - which is both rude to somebody else's free API and how an
			// API token gets rate-limited or pulled. Forced refreshes are still near-live, just not unbounded.
			if (age < (force ? ForcedRefreshFloor : TimeSpan.FromMinutes(ttl))) {
				return (cached.Points, cached.Pending);
			}
		}

		// Only one request at a time, and a floor between attempts.
		//
		// The cache is only stamped on a BELIEVABLE answer, so while rep4rep is down or the token is wrong every
		// single dashboard poll went straight through to their API - a request every three seconds, forever.
		if (!await _pointsGate.WaitAsync(0).ConfigureAwait(false)) {
			return CachedPoints();
		}

		(int Points, int PendingPoints)? user;

		try {
			if (DateTime.UtcNow - _lastPointsAttempt < ForcedRefreshFloor) {
				return CachedPoints();
			}

			_lastPointsAttempt = DateTime.UtcNow;
			user = await _mgr.Rep4Rep.GetUserAsync().ConfigureAwait(false);
		} finally {
			_pointsGate.Release();
		}

		// A negative "still being verified" is not a number rep4rep should ever produce, and caching one pins
		// nonsense on screen for a quarter of an hour. Keep whatever was there and try again next time.
		if (!Believable(user)) {
			// Said once, not on every poll. rep4rep really does serve a negative pending balance sometimes, and a
			// line per request buried everything else in the log.
			if (!_warnedAboutBalance) {
				_warnedAboutBalance = true;
				Log.Debug(new Said("rep4rep is reporting an impossible balance ({0} points, {1} pending) - keeping the last sensible figure", user?.Points, user?.PendingPoints));
			}

			return CachedPoints();
		}

		_warnedAboutBalance = false;

		if (user != null) {
			RememberPoints(user.Value.Points, user.Value.PendingPoints);
		}

		return user;
	}

	/// <summary>
	/// Whether a balance rep4rep just handed us can be true.
	///
	/// Their API really does serve things like "-107 waiting to be verified" from time to time. Believing one
	/// puts a nonsense figure on screen; caching one keeps it there long after the API has recovered.
	/// </summary>
	private static bool Believable((int Points, int PendingPoints)? user) =>
		user is null or { Points: >= 0, PendingPoints: >= 0 };

	/// <summary>The only place the points cache is written, so nothing can slip past the check above.</summary>
	private void RememberPoints(int points, int pending) {
		if (Believable((points, pending))) {
			_pointsCache = new PointsSnapshot(points, pending, DateTime.UtcNow);
		}
	}

	private IResult Guard(HttpContext ctx, Func<IResult> action) => Authorised(ctx) ? action() : Unauthorised();

	private int _resolvingNames;

	/// <summary>Look up game names off the request, one batch at a time - the history is polled every minute, and a
	/// second lookup of the same names while the first is still running would only ask Steam twice.</summary>
	private void ResolveNamesLater(List<uint> appIds) {
		if ((appIds.Count == 0) || (Interlocked.Exchange(ref _resolvingNames, 1) == 1)) {
			return;
		}

		_ = Task.Run(async () => {
			try {
				await GameNames.ResolveAsync(appIds).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Debug(new Said("couldn't look up game names for the history: {0}", Log.Describe(e)));
			} finally {
				Volatile.Write(ref _resolvingNames, 0);
			}
		});
	}

	private static IResult Unauthorised() => Results.Json(new { ok = false, error = "unauthorised" }, statusCode: 401);

	/// <summary>
	/// From outside the home - directly, or through a proxy on this PC. A backup holds every saved login (and on Linux the
	/// key that opens them), and the away-from-home link is plain http: anyone on the way could read the zip.
	/// </summary>
	private static bool FromOutside(HttpContext ctx) {
		(string ip, bool thisPc) = WhoIsSigningIn(ctx);

		return !thisPc && IsInternet(ip);
	}

	/// <summary>Shut to the internet after too many wrong guesses, and this visitor is from the internet.</summary>
	private bool ShutToThis(HttpContext ctx) => FromOutside(ctx) && InternetShut();

	private static IResult Closed() => Results.Json(new { ok = false, error = "closed" }, statusCode: 403);

	private static IResult HomeOnly() =>
		Results.Json(new { ok = false, error = new Said("Backups and restores only work at home or on this PC - the zip holds every saved login.").ToString() }, statusCode: 403);

	private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) {
		try {
			return await ctx.Request.ReadFromJsonAsync<T>().ConfigureAwait(false);
		} catch (Exception e) {
			// The endpoint answers "bad request"; the why is here. The sign-in page reads this before any password, so
			// the same junk sent again and again is said once.
			Log.DebugOnChange($"web:read:{ctx.Request.Path.Value}", $"dashboard: reading the request to {ctx.Request.Path.Value}: {Log.Describe(e)}");

			return default;
		}
	}

	/// <summary>Round-trip clone, so a redacted copy can be handed out without touching the live config.</summary>
	private static T Clone<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))!;

	private object BuildStatus() {
		Bot[] bots = [.. _mgr.All];
		(int cards, int comments) = Stats.Totals(24);

		// Per-account card drops in the last 24h, for the Overview's "Today" table. Comments per account are
		// already carried as Rep4RepToday (PostsInLast24h). Computed once here, not once per bot.
		Dictionary<string, int> cardsByBot = Stats.Recent(24)
			.Where(static e => e.Kind == Stats.KindCard)
			.GroupBy(static e => e.Bot, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.OrdinalIgnoreCase);

		// Keep the points figure warm in the background so the rail and the Overview aren't stuck on 0 until
		// somebody happens to open the rep4rep tab. The TTL setting is what actually paces this.
		if (_mgr.Rep4Rep.HasToken) {
			_ = CachedPointsAsync(false);
		}

		return new {
			Version = Build.Version,   // read from the assembly, not typed in - it was stuck at 1.0.0 for two releases
			BootId = _started.Ticks,   // the browser resets its log buffer when this changes
			RefreshSeconds = Math.Clamp(_mgr.Global.WebRefreshSeconds, 1, 60),
			UptimeMinutes = (int) (DateTime.UtcNow - _started).TotalMinutes,
			// People using nocat.farm in the last 24 hours, from nocat.lol's answer to the hourly ping. Null while it isn't
			// known, is too old, or "Count me as a user" is off - the page then shows nothing for it.
			Users = UserCount.Current(),
			Prompt = Prompt.Pending,
			PromptSecret = Prompt.PendingSecret,
			Rep4RepEnabled = _mgr.Global.Rep4RepEnabled,
			Rep4RepToken = _mgr.Rep4Rep.HasToken,
			Rep4RepWanted = _mgr.Global.Rep4RepEnabled ? bots.Count(static b => b.Cfg.Rep4Rep) : 0,
			QrWaiting = bots.Where(static b => b.QrChallenge != null).Select(static b => new { b.Name, b.QrVersion }).ToList(),
			// Genuinely reachable from the network AND unprotected. With no password the server already refuses
			// everything that isn't loopback, so warning about that case was crying wolf.
			// Every way of writing this PC counts as this PC - "::1" was warned about as if it were the network.
			Exposed = !Platform.IsLoopback(_mgr.Global.WebHost ?? "") && !string.IsNullOrEmpty(_mgr.Global.WebPassword) && (_mgr.Global.WebPassword.Length < 8),
			LockedToThisPc = !Platform.IsLoopback(_mgr.Global.WebHost ?? "") && string.IsNullOrEmpty(_mgr.Global.WebPassword),
			Points = CachedPoints()?.Points ?? 0,
			PendingPoints = CachedPoints()?.PendingPoints ?? 0,
			CardsToday = cards,
			CommentsToday = comments,
			CardsLeft = bots.Sum(static b => b.CardsRemaining),
			InventoryValue = bots.Sum(static b => b.Inventory.Total),
			Currency = PriceBook.Symbol,
			UpdateAvailable = UpdateCheck.Available,
			UpdateWaits = Live.Global.UpdateClickWaits,   // the Update button's dialog says which it will do
			UpdateUrl = UpdateCheck.Url,
			// A newer version with no download for this machine yet: the version chip says so, and there's no button.
			UpdateNoDownloadYet = UpdateCheck.Available == null ? UpdateCheck.NoDownloadYet : null,
			// False in Docker and as a Linux service, where it can't swap itself: the page drops its update button then.
			CanSelfUpdate = SelfUpdate.Supported,
			// Which shape of folder path the page's examples take - C:\... or /... ("Can it update itself" said the wrong one
			// on a Linux desktop and a Mac, which can).
			Windows = OperatingSystem.IsWindows(),
			PluginsOn = Live.Global.PluginsEnabled,
			UpdateBusy = SelfUpdate.Busy,
			UpdateFailed = SelfUpdate.LastFailure,
			TelegramConnectLink = Notifier.TelegramConnectLink,
			TelegramConnected = Notifier.TelegramConnected,
			DiscordBotSet = Notifier.HasDiscordBot,
			DiscordBotOn = _mgr.Global.DiscordCommands,
			DiscordBotOnline = Notifier.DiscordBotOnline,
			DiscordBotName = Notifier.DiscordBotName,
			DiscordBotProblem = Notifier.DiscordBotProblem,
			DiscordInviteUrl = Notifier.DiscordInviteUrl,
			DiscordConnected = Notifier.DiscordConnected,
			DiscordOwner = Notifier.DiscordConnected ? Notifier.DiscordOwner : "",
			DiscordOwnerId = _mgr.Global.DiscordOwnerId,
			UpdateProgress = SelfUpdate.Progress,
			InventoryPending = bots.Sum(static b => b.Inventory.Pending),
			GamesLeft = bots.Sum(static b => b.GamesRemaining),
			Bots = bots.Select(b => {
				Rep4RepModule? r4r = BotManager.ModuleOf<Rep4RepModule>(b);

				return new {
					Name = b.Name,
					Login = b.Cfg.SteamLogin,
					Notes = b.Cfg.Notes,
					HasAuthenticator = b.HasAuthenticator,
					State = b.State.ToString(),
					Group = GroupOf(b),
					Status = Loc.T(Commands.StateWord(b)),
					Detail = Loc.T(b.StatusText),
					Persona = Loc.T(b.PersonaWord),
					// The dashboard dims a hidden persona. It used to test Persona === 'invisible', which is
					// the one thing that string stopped being the moment it was translated - the flag says what
					// was meant instead of asking the front end to know English.
					PersonaHidden = b.PersonaWord is "invisible" or "offline",
					Seen = b.PlayingAsSeen,
					NameNotShowing = b.CustomNameNotShowing,
					Bans = BotManager.ModuleOf<BanWatch>(b)?.Last is { Any: true } bans ? BanWatch.Summary(bans).ToString() : "",
					Online = b.IsOnline,
					Paused = b.Paused,
					Blocked = b.PlayingBlocked,
					SteamId = b.SteamId.ToString(),
					SteamName = b.SteamName,
					Avatar = b.AvatarUrl,
					UptimeMinutes = b.OnlineSince == null ? 0 : (int) (DateTime.UtcNow - b.OnlineSince.Value).TotalMinutes,
					Playing = b.Playing,
					Guard = b.GuardPrompt,
					Cards = b.CardsRemaining,
					Games = b.GamesRemaining,
					Rep4Rep = b.Cfg.Rep4Rep,
					Legit = b.Cfg.LegitMode,
					InventoryValue = b.Inventory.Total,
					InventoryChange = InventoryHistory.Since(b.Name, TimeSpan.FromHours(24))?.Change,
					InventoryChangePct = InventoryHistory.Since(b.Name, TimeSpan.FromHours(24))?.Percent,
					InventoryPending = b.Inventory.Pending,
					InventoryReady = b.Inventory.Ready,

					// Whether it is being valued AT ALL. Without this the dashboard cannot tell "still working
					// it out" from "switched off", and an account with pricing turned off sat showing the
					// still-pricing ellipsis for ever.
					InventoryOn = b.Cfg.ShowInventoryValue,
					InventoryByGame = b.Inventory.ByGame.Take(8).Select(static g => new { g.Game, g.Items, g.Value, g.Blocked }),
					Rep4RepToday = r4r?.PostsToday ?? 0,
					Rep4RepCap = r4r?.Cap ?? b.Cfg.Rep4RepDailyCap,
					CardsToday = cardsByBot.GetValueOrDefault(b.Name),
					// For the Discord card's preview, which can count hours instead of cards.
					MinutesWeek = (int) History.MinutesOver(7, [b.Name]),
					MinutesMonth = (int) History.MinutesOver(30, [b.Name]),
					// The idle rotation, for the account's "What it plays" panel: "idling 31 of 214 - next batch at 14:10".
					// Null while it isn't rotating (off, human mode, or everything fits at once).
					// The overnight games it picked itself (its most-played) while "Games to idle overnight" is empty, for the
					// Human mode panel - empty when the list has games or banking overnight is off.
					NightPicked = BotManager.ModuleOf<HumanMode>(b) is { NightPicked: { Count: > 0 } picked } nightHuman
						? nightHuman.NightPickedNames(picked)
						: "",
					// Whether its games have been read: until then an empty pick isn't "nothing it may play".
					LibraryReady = b.Library.Ready,
					// What "Learn from how I play" has picked up so far, for the Human mode panel - empty while it's off.
					Learned = BotManager.ModuleOf<HumanMode>(b)?.LearnedLine() ?? "",
					Rotation = BotManager.ModuleOf<Idler>(b)?.Rotating is { } rot
						? new { Idling = rot.Now.Count, rot.Total, Next = IdleRotation.When(rot.MovesAt) }
						: null,
					// Quiet is worked out HERE, against the translated words, because Status is localised now.
					// The dashboard used to filter these rows with m.Status !== 'off' && m.Status !== 'idle',
					// which silently stopped matching in every language but English and put a wall of idle
					// module rows on each card.
					Modules = b.Modules.Select(m => new {
						// The module label the card shows. Name stays English on the module - it is an identifier the
						// log and the command output use - so it is translated here, where it becomes a row label.
						Name = Loc.T(m.Name),
						Status = m.Status,
						Quiet = !b.Running || (m.Status.Length == 0) || Loc.Is(m.Status, "off") || Loc.Is(m.Status, "idle")
					})
				};
			})
		};
	}

	/// <summary>The one status vocabulary the whole app uses - kept in <see cref="BotStatus.Group"/> so the mini window's
	/// counts and the dashboard's chips can't disagree.</summary>
	private static string GroupOf(Bot b) => BotStatus.Group(b);

	private sealed class LoginRequest {
		public string? Password { get; set; }
	}

	private sealed class CodeRequest {
		public string? Challenge { get; set; }
		public string? Code { get; set; }
	}

	private sealed class RestoreRequest {
		public string? Token { get; set; }
	}

	private sealed class PluginSettingChange {
		public string Plugin { get; set; } = "";
		public string Name { get; set; } = "";
		public string? Value { get; set; }
	}

	private sealed class PluginToggle {
		public string Name { get; set; } = "";
		public bool Enabled { get; set; }
	}

	private sealed class AuthActRequest {
		public List<string>? Ids { get; set; }
		public bool Accept { get; set; }
	}

	private sealed class CommandRequest {
		public string? Line { get; set; }
	}

	private sealed class PromptRequest {
		public string? Value { get; set; }
	}

	private sealed class TokenRequest {
		public string? Token { get; set; }
	}

	private sealed class ImportRequest {
		public string? Path { get; set; }
		public bool Overwrite { get; set; }

		/// <summary>Accounts to bring across in human mode - the ones the walkthrough was told you play on.</summary>
		public List<string>? Human { get; set; }
	}

	private sealed class ApplyImportRequest {
		public string? Tool { get; set; }
		public string? Path { get; set; }
		public bool Overwrite { get; set; }
		public List<ImportPick>? Accounts { get; set; }

		/// <summary>Which of the scan's settings to bring, by index. Null brings them all.</summary>
		public List<int>? Settings { get; set; }
	}

	private sealed class ImportPick {
		public string? Key { get; set; }
		public bool Human { get; set; }
		public bool SignIn { get; set; }
		public string? SteamLogin { get; set; }
	}

	/// <summary>
	/// A scan as the dashboard's preview shows it: where it looked, and per account whether a password, a token and an
	/// authenticator are there - never the things themselves.
	/// </summary>
	private static object ScanJson(ImportScan scan) {
		Dictionary<string, BotConfig> bots = ConfigStore.LoadBots();
		HashSet<string> here = new(bots.Keys, StringComparer.OrdinalIgnoreCase);

		return new {
			scan.Tool,
			scan.ToolName,
			scan.Found,
			Path = scan.Path ?? "",
			scan.Looked,
			Accounts = scan.Accounts.Select(a => new {
				a.Key,
				a.Name,
				a.SteamLogin,
				a.SteamId,
				// Here already by the name, or signing in as the same Steam account under another - as the import decides.
				Exists = here.Contains(a.Name) || (IdlerImport.ExistingFor(bots, a.SteamLogin) != null),
				HasToken = a.Token != null,
				a.HasPassword,
				a.HasAuthenticator,
				CanBringSignIn = a.CredentialTarget != null,
				Brings = IdlerImport.Describe(a).Select(static s => s.ToString()),
				Notes = a.Notes.Select(static n => n.ToString())
			}),
			Settings = scan.Settings.Select(static (s, i) => new { Index = i, s.Name, Label = Settings.Find(s.Name)?.Label ?? s.Name, s.Value, s.EveryAccount }),
			Notes = scan.Notes.Select(static n => n.ToString())
		};
	}

	private sealed class ThemeRequest {
		public string? Theme { get; set; }
	}

	private sealed class ConfirmRequest {
		public string? Confirm { get; set; }
	}

	private sealed class DlcAnswerRequest {
		public uint App { get; set; }
		public string? Answer { get; set; }
	}

	private sealed class AddBotRequest {
		public string? Name { get; set; }
		public string? SteamLogin { get; set; }
		public string? Password { get; set; }
		public bool Qr { get; set; }

		/// <summary>Start it in human mode - the walkthrough's "my main, I play on it".</summary>
		public bool Human { get; set; }

		/// <summary>You sign into it from your own Steam client as well (<see cref="BotConfig.IUseThisAccount"/>).</summary>
		public bool SelfSignIn { get; set; }
	}

	public async ValueTask DisposeAsync() => await StopListeningAsync().ConfigureAwait(false);
}
