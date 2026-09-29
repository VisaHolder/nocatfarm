using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
	/// </summary>
	private static (string Ip, bool ThisPc) WhoIsSigningIn(HttpContext ctx) {
		IPAddress from = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
		string? forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? ctx.Request.Headers["X-Real-IP"].FirstOrDefault();

		// The LAST address in the list: the proxy adds the one it really saw to the end. The first is whatever the visitor
		// chose to send, so reading that let anybody give a fresh made-up address with every guess and never be locked out.
		if (IPAddress.IsLoopback(from) && !string.IsNullOrWhiteSpace(forwarded)) {
			return (forwarded.Split(',')[^1].Trim(), false);
		}

		return (from.ToString(), IPAddress.IsLoopback(from));
	}

	/// <summary>Seconds this address is still locked out for after too many wrong passwords; 0 when it isn't.</summary>
	private int LockedFor(HttpContext ctx) {
		string ip = WhoIsSigningIn(ctx).Ip;

		return _failures.TryGetValue(ip, out (int Count, DateTime Until) f) && (f.Count >= MaxFailedLogins) && (f.Until > DateTime.UtcNow)
			? (int) Math.Ceiling((f.Until - DateTime.UtcNow).TotalSeconds)
			: 0;
	}

	/// <summary>Every sign-in lockout lifted - the 'unlock' command, for whoever is at this PC.</summary>
	public int ClearLockouts() {
		int n = _failures.Count(static f => f.Value.Count >= MaxFailedLogins && f.Value.Until > DateTime.UtcNow);
		_failures.Clear();

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
	private readonly DateTime _started = DateTime.UtcNow;

	private WebApplication? _app;

	// rep4rep's points are polled, not pushed, so the answer is cached rather than fetched per page view.
	private (int Points, int Pending, DateTime At)? _pointsCache;
	private bool _warnedAboutBalance;
	private readonly SemaphoreSlim _pointsGate = new(1, 1);
	private DateTime _lastPointsAttempt = DateTime.MinValue;

	public string Url { get; private set; } = "";

	/// <summary>The address and port the dashboard really listens on - "Listen on" and "Port" only change it at a restart.</summary>
	private string _listening = "";

	public WebHost(BotManager mgr, GlobalConfig cfg) {
		_mgr = mgr;
		_ = cfg;   // the bind address is read from the live config at StartAsync
		Current = this;
		LoadSessions();
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

	private string PasswordKey => Hash("nocat.farm/" + _cfg.WebPassword);

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
		} catch {
			// everybody signs in again - nothing worse
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
		} catch {
			// they last until the next restart, then
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
			builder.WebHost.UseUrls($"http://{listenHost}:{listenPort}");
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
					Log.Warn(new Said("dashboard: {0} failed: {1}", ctx.Request.Path.Value ?? "", e.Message));
					Log.Debug($"dashboard: {e}");

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

			string host = listenHost is "0.0.0.0" or "*" or "+" ? "localhost" : listenHost;
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
			Log.Error(new Said("couldn't start the dashboard on {0}:{1} - {2}", listenHost, listenPort, e.Message));
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

	private bool TryLogin(HttpContext ctx, string password, out string token) {
		token = "";
		(string ip, bool thisPc) = WhoIsSigningIn(ctx);
		int lockout = thisPc ? LockoutMinutesThisPc : LockoutMinutes;

		if (_failures.TryGetValue(ip, out (int Count, DateTime Until) fail) && (fail.Count >= MaxFailedLogins) && (fail.Until > DateTime.UtcNow)) {
			return false;
		}

		// Constant-time compare: a check that returns early leaks the password one character at a time. Of the hashes,
		// so every character counts: padded and cut to 64, "pw  " passed for "pw" and a long password only needed its
		// first 64 characters.
		bool ok = CryptographicOperations.FixedTimeEquals(
			SHA256.HashData(Encoding.UTF8.GetBytes(password)),
			SHA256.HashData(Encoding.UTF8.GetBytes(_cfg.WebPassword)));

		if (!ok) {
			int count = (_failures.TryGetValue(ip, out (int Count, DateTime Until) prev) ? prev.Count : 0) + 1;
			_failures[ip] = (count, DateTime.UtcNow.AddMinutes(lockout));

			if (count >= MaxFailedLogins) {
				Log.Warn(new Said("dashboard: {0} failed logins from {1} - locked out for {2}m", count, ip, lockout));
			}

			return false;
		}

		_failures.TryRemove(ip, out _);
		token = NewSession(ctx);

		foreach (string stale in _sessions.Where(static kv => kv.Value < DateTime.UtcNow).Select(static kv => kv.Key).ToArray()) {
			_sessions.TryRemove(stale, out _);
		}

		return true;
	}

	/// <summary>A fresh signed-in session for whoever sent this request: remembered here, and handed back as the cookie.</summary>
	private string NewSession(HttpContext ctx) {
		string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
		DateTime expires = DateTime.UtcNow.AddDays(Math.Clamp(_cfg.WebSessionDays, 1, 90));
		_sessions[Hash(token)] = expires;
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

		app.MapPost("/api/login", async (HttpContext ctx) => {
			LoginRequest? body = await ReadJsonAsync<LoginRequest>(ctx).ConfigureAwait(false);

			if (body == null || !TryLogin(ctx, body.Password ?? "", out string token)) {
				// Locked out says so, with how long - "wrong password" for the right one while it waited was the most
				// confusing thing the page could say.
				int wait = LockedFor(ctx);

				return wait > 0
					? Results.Json(new { ok = false, error = "locked", seconds = wait, thisPc = WhoIsSigningIn(ctx).ThisPc }, statusCode: 429)
					: Results.Json(new { ok = false, error = "wrong password" }, statusCode: 401);
			}

			return Results.Json(new { ok = true, token });
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
				p.Name, p.Version, p.File, p.Enabled,
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

			List<string> off = [.. Live.Global.DisabledPlugins];

			_ = body.Enabled
				? off.RemoveAll(n => string.Equals(n, body.Name, StringComparison.OrdinalIgnoreCase))
				: off.Contains(body.Name, StringComparer.OrdinalIgnoreCase) ? 0 : Add(off, body.Name);

			Live.Global.DisabledPlugins = off;
			ConfigStore.SaveGlobal(Live.Global);
			Log.Info(body.Enabled
				? new Said("plugin {0} switched on - takes effect after a restart", body.Name)
				: new Said("plugin {0} switched off - takes effect after a restart", body.Name));

			return Results.Json(new { Message = $"{body.Name} is {(body.Enabled ? "on" : "off")} after a restart." });

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
				return Results.Json(new { Message = new Said("couldn't reach GitHub to check ({0}) - try again in a minute", problem).ToString() });
			}

			if (UpdateCheck.Available == null) {
				return Results.Json(new { Message = $"You're on the newest release ({Build.Version})." });
			}

			// Pressing it is choosing that version after all, as 'update accept' is. Left skipped, a queued install
			// was dropped at bedtime without a word, after the button had said it would go in.
			if (UpdateCheck.Skipped != null) {
				UpdateCheck.Skipped = null;
			}

			// "When I say update" set to wait: the button queues it for when the accounts are asleep, like 'update accept'.
			if (Live.Global.UpdateWhenAsked == 1) {
				return Results.Json(new { Message = UpdateCheck.Queue(UpdateCheck.Available).ToString() });
			}

			string? failure = await SelfUpdate.ApplyAsync(CancellationToken.None).ConfigureAwait(false);

			return Results.Json(new {
				Message = failure ?? "Downloading. It restarts by itself when it lands."
			});
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

			GlobalConfig current = _mgr.Global;
			GlobalConfig? body = await ReadMergedAsync(ctx, current).ConfigureAwait(false);

			if (body == null) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			// An empty secret means "unchanged", never "erase it". Every Secret in the registry, not a list here.
			KeepSecrets(body, current, Settings.Global);

			// Only settings are changed here. The page posts its whole copy of the config, so the fields the app keeps
			// for itself - the theme, the account order, the window's place, a rep4rep hold, the connected chat - came
			// back as they were when the page loaded, undoing whatever had changed them since.
			KeepServerFields(body, current);

			if (Invalid(body, current, Settings.Global) is { } error) {
				return Results.Json(new { ok = false, error }, statusCode: 400);
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

			// Still put in force, but not called saved: a full disk or a damaged settings file used to come back "Saved."
			// and the change was gone at the next start.
			if (!ConfigStore.SaveGlobal(body)) {
				adjusted.Add(new Said("in use now, but not saved to disk - see the Log").ToString());
			}

			_mgr.ApplyGlobal(body);

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
				fresh = string.IsNullOrEmpty(body.WebPassword) ? null : NewSession(ctx);
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

			BotConfig? body = await ReadMergedAsync(ctx, bot.Cfg).ConfigureAwait(false);

			if (body == null) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			KeepSecrets(body, bot.Cfg, Settings.Bot);

			if (Invalid(body, bot.Cfg, Settings.Bot) is { } error) {
				return Results.Json(new { ok = false, error }, statusCode: 400);
			}

			// Legit mode rewrites the config itself, so this has to happen before the diff and the save.
			Settings.ApplyLegitMode(body, bot.Cfg.LegitMode);

			List<string> adjusted = Clamp(body, Settings.Bot);

			// A max below the min would make every gap calculation nonsense; fix it rather than store it. This
			// covered only the rep4rep gap for years while seven other pairs went unchecked - it walks them all
			// now, from the same helper the console uses, so the two paths cannot drift apart again.
			// Whichever side was just changed keeps its number, as at the console.
			adjusted.AddRange(Settings.FixRanges(body, [.. Settings.Bot
				.Where(d => !Equals(Settings.Show(body, d), Settings.Show(bot.Cfg, d)))
				.Select(static d => d.Name)]));

			// Only fire side effects for settings that ACTUALLY changed. Running them all meant editing a note
			// re-started an account the user had deliberately stopped.
			List<SettingDef> changed = Settings.Bot
				.Where(d => !Equals(Settings.Show(body, d), Settings.Show(bot.Cfg, d)))
				.ToList();

			// The account's own name, not the one in the address: the lookup ignores case, and off Windows "MAIN" in the
			// address wrote MAIN.json beside main.json - two files for one account, either of which the next start read.
			if (!ConfigStore.SaveBot(bot.Name, body)) {
				adjusted.Add(new Said("in use now, but not saved to disk - see the Log").ToString());
			}

			bot.Reconfigure(body);

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

		app.MapPost("/api/theme", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			ThemeRequest? body = await ReadJsonAsync<ThemeRequest>(ctx).ConfigureAwait(false);
			string want = body?.Theme?.Trim().ToLowerInvariant() ?? "";

			if (want is not ("dark" or "light")) {
				return Results.Json(new { ok = false, error = "Theme is either dark or light." }, statusCode: 400);
			}

			Live.Global.Theme = want;
			ConfigStore.SaveGlobal(Live.Global);

			return Results.Json(new { ok = true });
		});

		app.MapPost("/api/tutorial/done", (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Live.Global.TutorialDone = true;
			ConfigStore.SaveGlobal(Live.Global);

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

			Live.Global.AccountOrder = clean;
			ConfigStore.SaveGlobal(Live.Global);

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
			bool plain = body.Name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

			if (!plain || !ConfigStore.IsValidBotName(body.Name)) {
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
						bot.Cfg.Enabled = true;
						ConfigStore.SaveBot(bot.Name, bot.Cfg);
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
				// Saved, but still listening the old way until the dashboard restarts - the phone link won't answer yet.
				// Why the last restart didn't take - only while it still needs one, or an old failure outlives the fix.
				NeedsRestart, RestartProblem = NeedsRestart ? RestartProblem : null,
				Windows = OperatingSystem.IsWindows(), InDocker = Platform.InContainer,
				// The Public address typed by hand, rather than the one the router gave.
				ManualOutside = !string.IsNullOrWhiteSpace(Live.Global.WebPublicAddress),
				MinPassword = Core.RemoteAccess.MinPasswordLength,
				// Long enough to open it from anywhere - so the walkthrough can say so before a saved password it never sees fails.
				LongPassword = (Live.Global.WebPassword ?? "").Length >= Core.RemoteAccess.MinPasswordLength,
				OnThisPc = ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip),
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
				await RelistenAsync().ConfigureAwait(false);
			});

			return Results.Json(new { Ok = true, Needed = true, Port = _cfg.WebPort });
		}));

		// "Allow through Windows Firewall": Windows' own prompt appears on this PC, so only this PC may ask for it.
		app.MapPost("/api/phone/firewall", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (!OperatingSystem.IsWindows() || (ctx.Connection.RemoteIpAddress is not { } ip) || !System.Net.IPAddress.IsLoopback(ip)) {
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

		app.MapGet("/api/bots/{name}/library", (HttpContext ctx, string name) => Guard(ctx, () => {
			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { Ready = false, Games = Array.Empty<object>() });
			}

			return Results.Json(new {
				bot.Library.Ready,
				Games = bot.Library.Games.Where(static g => !g.Shared).OrderByDescending(static g => g.MinutesPlayed).Take(40)
					.Select(static g => new { g.AppId, g.Name, Minutes = g.MinutesPlayed }).ToList()
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

			// Validate BEFORE saving, so a typo can't quietly stop every account commenting.
			string previous = _mgr.Rep4Rep.Token;
			_mgr.Rep4Rep.Token = token;
			(int Points, int PendingPoints)? user = await _mgr.Rep4Rep.GetUserAsync().ConfigureAwait(false);

			if (user == null) {
				_mgr.Rep4Rep.Token = previous;

				return Results.Json(new { ok = false, error = "That token isn't valid. Copy it again from rep4rep.com under Settings - it's the whole string, with no spaces." });
			}

			_mgr.Global.Rep4RepApiToken = token;
			ConfigStore.SaveGlobal(_mgr.Global);
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
			if (def.Kind is not (SettingKind.Int or SettingKind.Float or SettingKind.Choice or SettingKind.Pick)) {
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
				adjusted.Add($"{def.Label} was {before}, reset to {Settings.Show(config, def)}");
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
	/// so one saved before this check existed can't block every other save on that account. Plain text is left
	/// alone - the console accepts any text too, so there is nothing to check it against.
	/// </summary>
	private static string? Invalid(object config, object current, IReadOnlyList<SettingDef> defs) {
		foreach (SettingDef def in defs) {
			if ((def.Kind != SettingKind.AppIds) || (Settings.Read(config, def.Name) is not IEnumerable<uint> apps)
				|| (Settings.Show(config, def) == Settings.Show(current, def))) {
				continue;
			}

			if (Settings.Apply(config, def, string.Join(',', apps)) is { } error) {
				return error;
			}
		}

		return null;
	}

	/// <summary>
	/// Read a settings POST as changes to the config it is saving, not as a whole new config.
	///
	/// Deserialising straight into a fresh object gave every field the page left out its default, so a POST with one
	/// key in it blanked the Steam login, the games and everything else. Now the current config is the starting
	/// point and only what was actually sent is written over it.
	/// </summary>
	/// <summary>Every global field that isn't a setting, put back to what the app holds now.</summary>
	internal static void KeepServerFields(GlobalConfig body, GlobalConfig current) {
		foreach (PropertyInfo p in typeof(GlobalConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
			if (p.CanRead && p.CanWrite && (p.GetIndexParameters().Length == 0) && !Settings.Global.Any(d => d.Name == p.Name)) {
				p.SetValue(body, p.GetValue(current));
			}
		}
	}

	private static async Task<T?> ReadMergedAsync<T>(HttpContext ctx, T current) where T : class {
		JsonSerializerOptions options = ctx.RequestServices
			.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

		try {
			if (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false) is not JsonObject sent) {
				return null;
			}

			JsonObject merged = JsonSerializer.SerializeToNode(current, options)!.AsObject();

			foreach ((string key, JsonNode? value) in sent) {
				// Names are matched the way the endpoint always read them - ignoring case - so "steamlogin" replaces
				// SteamLogin instead of sitting beside it and losing.
				string name = merged.Select(static kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
				merged[name] = value?.DeepClone();
			}

			return merged.Deserialize<T>(options);
		} catch {
			return null;
		}
	}

	/// <summary>How often a "forced" refresh may actually reach rep4rep. See below for why this exists.</summary>
	private static readonly TimeSpan ForcedRefreshFloor = TimeSpan.FromSeconds(45);

	private async Task<(int Points, int PendingPoints)?> CachedPointsAsync(bool force) {
		int ttl = Math.Clamp(_mgr.Global.Rep4RepPointsRefreshMinutes, 1, 1440);

		if (_pointsCache.HasValue) {
			TimeSpan age = DateTime.UtcNow - _pointsCache.Value.At;

			// "Force" means "prefer fresh", not "ask again right now, every time you are asked".
			//
			// The rep4rep panel polls on a timer, so forcing unconditionally turned one open browser tab into a
			// request to rep4rep every single second - which is both rude to somebody else's free API and how an
			// API token gets rate-limited or pulled. Forced refreshes are still near-live, just not unbounded.
			if (age < (force ? ForcedRefreshFloor : TimeSpan.FromMinutes(ttl))) {
				return (_pointsCache.Value.Points, _pointsCache.Value.Pending);
			}
		}

		// Only one request at a time, and a floor between attempts.
		//
		// The cache is only stamped on a BELIEVABLE answer, so while rep4rep is down or the token is wrong every
		// single dashboard poll went straight through to their API - a request every three seconds, forever.
		if (!await _pointsGate.WaitAsync(0).ConfigureAwait(false)) {
			return _pointsCache.HasValue ? (_pointsCache.Value.Points, _pointsCache.Value.Pending) : null;
		}

		(int Points, int PendingPoints)? user;

		try {
			if (DateTime.UtcNow - _lastPointsAttempt < ForcedRefreshFloor) {
				return _pointsCache.HasValue ? (_pointsCache.Value.Points, _pointsCache.Value.Pending) : null;
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

			return _pointsCache.HasValue ? (_pointsCache.Value.Points, _pointsCache.Value.Pending) : null;
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
			_pointsCache = (points, pending, DateTime.UtcNow);
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
				Log.Debug(new Said("couldn't look up game names for the history: {0}", e.Message));
			} finally {
				Volatile.Write(ref _resolvingNames, 0);
			}
		});
	}

	private static IResult Unauthorised() => Results.Json(new { ok = false, error = "unauthorised" }, statusCode: 401);

	private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) {
		try {
			return await ctx.Request.ReadFromJsonAsync<T>().ConfigureAwait(false);
		} catch {
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
			Prompt = Prompt.Pending,
			PromptSecret = Prompt.PendingSecret,
			Rep4RepEnabled = _mgr.Global.Rep4RepEnabled,
			Rep4RepToken = _mgr.Rep4Rep.HasToken,
			Rep4RepWanted = _mgr.Global.Rep4RepEnabled ? bots.Count(static b => b.Cfg.Rep4Rep) : 0,
			QrWaiting = bots.Where(static b => b.QrChallenge != null).Select(static b => new { b.Name, b.QrVersion }).ToList(),
			// Genuinely reachable from the network AND unprotected. With no password the server already refuses
			// everything that isn't loopback, so warning about that case was crying wolf.
			Exposed = _mgr.Global.WebHost is not ("127.0.0.1" or "localhost") && !string.IsNullOrEmpty(_mgr.Global.WebPassword) && (_mgr.Global.WebPassword.Length < 8),
			LockedToThisPc = _mgr.Global.WebHost is not ("127.0.0.1" or "localhost") && string.IsNullOrEmpty(_mgr.Global.WebPassword),
			Points = _pointsCache?.Points ?? 0,
			PendingPoints = _pointsCache?.Pending ?? 0,
			CardsToday = cards,
			CommentsToday = comments,
			CardsLeft = bots.Sum(static b => b.CardsRemaining),
			InventoryValue = bots.Sum(static b => b.Inventory.Total),
			Currency = PriceBook.Symbol,
			UpdateAvailable = UpdateCheck.Available,
			UpdateUrl = UpdateCheck.Url,
			// False on Linux and in Docker, where it can't swap itself: the page drops its update button then.
			CanSelfUpdate = SelfUpdate.Supported,
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

	/// <summary>The one status vocabulary the whole app uses - the rail chips, the filters and the cards agree.</summary>
	private static string GroupOf(Bot b) {
		if (!b.Cfg.Enabled || b.State == BotState.Stopped) {
			return "off";
		}

		if (b.State == BotState.Failed) {
			return "problem";
		}

		if (b.GuardPrompt != null || b.State == BotState.NeedsGuard) {
			return "needsyou";
		}

		if (b.State != BotState.Online) {
			return "connecting";
		}

		if (b.IsFarming) {
			return "farming";
		}

		// Same as the console: a grind is not idling, and saying so contradicted the detail line beside it.
		// Grouped with "playing" rather than given a chip of its own - it IS playing one game deliberately,
		// which is exactly what that chip means, and the row's own text names the game and the time left.
		// Paused, or you're on it: the grind's game is off, and "playing" counted the account as working.
		if (b.Grinding && !b.Paused && !b.PlayingBlocked) {
			return "playing";
		}

		// Finishing up before it logs off - between things, not whatever human mode was about to start.
		if (b.Stopping) {
			return "break";
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(b);

		if (human is { Current: not HumanMode.Phase.Off }) {
			return human.Current switch {
				HumanMode.Phase.Playing => "playing",
				HumanMode.Phase.ShortBreak or HumanMode.Phase.MealBreak => "break",
				HumanMode.Phase.NightIdle => "nightidle",
				HumanMode.Phase.Asleep or HumanMode.Phase.DoneForToday => "asleep",

				// Settling in and switching games are both "between things", which is what a break already means
				// on the dashboard - and far more honest than the "online" they used to fall through to.
				HumanMode.Phase.WarmingUp or HumanMode.Phase.SwitchingGame => "break",
				HumanMode.Phase.DayOff => "asleep",
				_ => "online"
			};
		}

		return string.IsNullOrEmpty(b.Playing) ? "online" : "idling";
	}

	private sealed class LoginRequest {
		public string? Password { get; set; }
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
		HashSet<string> here = new(ConfigStore.LoadBots().Keys, StringComparer.OrdinalIgnoreCase);

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
				Exists = here.Contains(a.Name),
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
