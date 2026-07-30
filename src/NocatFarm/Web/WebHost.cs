using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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

	private readonly BotManager _mgr;

	/// <summary>
	/// ALWAYS the live config. Holding the instance handed in at construction meant that after the first save
	/// from the dashboard - which swaps in a new object - the password check, the session length and the
	/// "is a password set" answer all kept consulting the old one. A new dashboard password simply never applied.
	/// </summary>
	private GlobalConfig _cfg => _mgr.Global;

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

	public WebHost(BotManager mgr, GlobalConfig cfg) {
		_mgr = mgr;
		_ = cfg;   // the bind address is read from the live config at StartAsync
	}

	public async Task<bool> StartAsync() {
		try {
			WebApplicationBuilder builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();   // our own log is the log; Kestrel's chatter would drown it
			builder.WebHost.UseUrls($"http://{_cfg.WebHost}:{_cfg.WebPort}");
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

			string host = _cfg.WebHost is "0.0.0.0" or "*" or "+" ? "localhost" : _cfg.WebHost;
			Url = $"http://{host}:{_cfg.WebPort}/";

			Log.Good($"dashboard: {Url}{(string.IsNullOrEmpty(_cfg.WebPassword) ? "  (this PC only - no password set)" : "")}");

			return true;
		} catch (Exception e) {
			Log.Error($"couldn't start the dashboard on {_cfg.WebHost}:{_cfg.WebPort} - {e.Message}");
			Log.Info("the console still works. Change WebPort in config/nocatFarm.json, or set WebEnabled false.");

			return false;
		}
	}

	// ── auth ────────────────────────────────────────────────────────────────
	private bool Authorised(HttpContext ctx) {
		IPAddress ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;

		if (string.IsNullOrEmpty(_cfg.WebPassword)) {
			// No password means local only. Never open a Steam account panel to the network by accident.
			return IPAddress.IsLoopback(ip);
		}

		string? token = ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.Ordinal)
			?? ctx.Request.Cookies["nocatfarm"];

		return !string.IsNullOrEmpty(token) && _sessions.TryGetValue(token, out DateTime expires) && (expires > DateTime.UtcNow);
	}

	private bool TryLogin(HttpContext ctx, string password, out string token) {
		token = "";
		string ip = (ctx.Connection.RemoteIpAddress ?? IPAddress.None).ToString();

		if (_failures.TryGetValue(ip, out (int Count, DateTime Until) fail) && (fail.Count >= MaxFailedLogins) && (fail.Until > DateTime.UtcNow)) {
			return false;
		}

		// Constant-time compare: a check that returns early leaks the password one character at a time.
		bool ok = CryptographicOperations.FixedTimeEquals(
			Encoding.UTF8.GetBytes(password.PadRight(64)[..64]),
			Encoding.UTF8.GetBytes(_cfg.WebPassword.PadRight(64)[..64]));

		if (!ok) {
			int count = (_failures.TryGetValue(ip, out (int Count, DateTime Until) prev) ? prev.Count : 0) + 1;
			_failures[ip] = (count, DateTime.UtcNow.AddMinutes(LockoutMinutes));

			if (count >= MaxFailedLogins) {
				Log.Warn($"dashboard: {count} failed logins from {ip} - locked out for {LockoutMinutes}m");
			}

			return false;
		}

		_failures.TryRemove(ip, out _);
		token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
		_sessions[token] = DateTime.UtcNow.AddDays(Math.Clamp(_cfg.WebSessionDays, 1, 90));

		foreach (string stale in _sessions.Where(static kv => kv.Value < DateTime.UtcNow).Select(static kv => kv.Key).ToArray()) {
			_sessions.TryRemove(stale, out _);
		}

		return true;
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
				return Results.Json(new { ok = false, error = "wrong password" }, statusCode: 401);
			}

			ctx.Response.Cookies.Append("nocatfarm", token, new CookieOptions {
				HttpOnly = true,
				SameSite = SameSiteMode.Strict,
				Expires = DateTimeOffset.UtcNow.AddDays(Math.Clamp(_cfg.WebSessionDays, 1, 90))
			});

			return Results.Json(new { ok = true, token });
		});

		// ── live state ──
		app.MapGet("/api/status", (HttpContext ctx) => Guard(ctx, () => Results.Json(BuildStatus())));

		app.MapGet("/api/log", (HttpContext ctx, int? n, long? since) => Guard(ctx, () => Results.Json(
			(since.HasValue ? Log.Since(since.Value) : Log.Recent(Math.Clamp(n ?? 200, 1, 1000)))
			.Select(static e => new {
				Seq = e.Seq,
				Time = e.When.ToString("HH:mm:ss"),
				Level = e.Level,
				Source = e.Source,
				Text = e.Text
			}))));

		app.MapGet("/api/stats", (HttpContext ctx, int? hours) => Guard(ctx, () => {
			int window = Math.Clamp(hours ?? 24, 1, 168);
			(int cards, int comments) = Stats.Totals(window);

			return Results.Json(new {
				Hours = window,
				Cards = cards,
				Comments = comments,
				Buckets = Stats.ByHour(window).Select(static b => new { Hour = b.Hour.ToString("HH:mm"), b.Cards, b.Comments })
			});
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

			AsfImport.Result result = AsfImport.Run(dir, _mgr.Global, body?.Overwrite ?? false);
			await _mgr.SyncFromDiskAsync().ConfigureAwait(false);
			Log.Good($"imported {result.Imported} account(s) from ArchiSteamFarm");

			return Results.Json(new { ok = true, result.Imported, result.Skipped, result.Notes });
		});

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

			GlobalConfig? body = await ReadJsonAsync<GlobalConfig>(ctx).ConfigureAwait(false);

			if (body == null) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			// An empty secret means "unchanged", never "erase it". Every Secret in the registry, not a list here.
			KeepSecrets(body, _mgr.Global, Settings.Global);

			List<string> adjusted = Clamp(body, Settings.Global);

			List<string> restartNeeded = Settings.Global
				.Where(d => d.NeedsRestart && !Equals(Settings.Read(body, d.Name)?.ToString(), Settings.Read(_mgr.Global, d.Name)?.ToString()))
				.Select(static d => d.Label)
				.ToList();

			ConfigStore.SaveGlobal(body);
			_mgr.ApplyGlobal(body);

			foreach (SettingDef def in Settings.Global) {
				Commands.ApplyGlobalSideEffects(_mgr, def);   // all global side effects are idempotent
			}

			Log.Info("global settings saved from the dashboard");

			return Results.Json(new { ok = true, RestartNeeded = restartNeeded, Adjusted = adjusted });
		});

		app.MapPost("/api/bots/{name}/config", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			Bot? bot = _mgr.Get(name);

			if (bot == null) {
				return Results.Json(new { ok = false, error = "no such account" }, statusCode: 404);
			}

			BotConfig? body = await ReadJsonAsync<BotConfig>(ctx).ConfigureAwait(false);

			if (body == null) {
				return Results.Json(new { ok = false, error = "bad request" }, statusCode: 400);
			}

			KeepSecrets(body, bot.Cfg, Settings.Bot);

			// Legit mode rewrites the config itself, so this has to happen before the diff and the save.
			Settings.ApplyLegitMode(body, bot.Cfg.LegitMode);

			List<string> adjusted = Clamp(body, Settings.Bot);

			// A max below the min would make every gap calculation nonsense; fix it rather than store it.
			if (body.Rep4RepGapMaxMinutes < body.Rep4RepGapMinMinutes) {
				body.Rep4RepGapMaxMinutes = body.Rep4RepGapMinMinutes;
				adjusted.Add("Longest gap can't be shorter than the shortest gap - both set the same");
			}

			// Only fire side effects for settings that ACTUALLY changed. Running them all meant editing a note
			// re-started an account the user had deliberately stopped.
			List<SettingDef> changed = Settings.Bot
				.Where(d => !Equals(Settings.Show(body, d), Settings.Show(bot.Cfg, d)))
				.ToList();

			ConfigStore.SaveBot(name, body);
			bot.Reconfigure(body);

			foreach (SettingDef def in changed) {
				Commands.ApplyBotSideEffects(bot, def);
			}

			Log.Info("settings saved from the dashboard", name);

			return Results.Json(new { ok = true, Adjusted = adjusted });
		});

		// ── account lifecycle ──
		// The walkthrough marks itself done. A one-line endpoint rather than a config round-trip, so finishing
		// the tutorial cannot accidentally write back a whole settings object the user never edited.
		// The dashboard theme, stored server-side so the toggle and the 'theme' command agree and the choice
		// follows you between browsers.
		// What the achievement pacer is doing, per game. Read-only.
		app.MapGet("/api/bots/{name}/achievements", (HttpContext ctx, string name) => Guard(ctx, () => {
			Bot? bot = _mgr.Get(name);
			Modules.AchievementPacer? pacer = bot == null ? null : BotManager.ModuleOf<Modules.AchievementPacer>(bot);

			return Results.Json(new {
				On = bot?.Cfg.UnlockAchievements ?? false,
				Games = pacer?.Snapshot() ?? []
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

			if (body == null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.SteamLogin)) {
				return Results.Json(new { ok = false, error = "A name and a Steam account name are both needed." }, statusCode: 400);
			}

			if (!ConfigStore.IsValidBotName(body.Name)) {
				return Results.Json(new { ok = false, error = "Letters, numbers, dashes and underscores. 'nocatFarm' is taken by the global config." }, statusCode: 400);
			}

			if (_mgr.Get(body.Name) != null) {
				return Results.Json(new { ok = false, error = $"'{body.Name}' already exists." }, statusCode: 400);
			}

			BotConfig cfg = new() { SteamLogin = body.SteamLogin, SteamPassword = body.Password ?? "" };
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
					await bot.StopAsync().ConfigureAwait(false);

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
			Log.Good($"rep4rep connected - {user.Value.Points} points");

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
	private const string ClearSecret = " clear";

	private static string Keep(string incoming, string existing) =>
		incoming == ClearSecret ? "" : string.IsNullOrEmpty(incoming) ? existing : incoming;

	/// <summary>
	/// Clamp every numeric setting to the range the registry declares, so a hand-crafted POST (or a browser
	/// number box coercing an empty field to 0) can't set the comment pacing to zero and fire off ten comments
	/// back to back. The console already went through Settings.Apply; this closes the other door.
	/// </summary>
	private static List<string> Clamp(object config, IReadOnlyList<SettingDef> defs) {
		List<string> adjusted = [];

		foreach (SettingDef def in defs) {
			if (def.Kind is not (SettingKind.Int or SettingKind.Float or SettingKind.Choice)) {
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
				Log.Debug($"rep4rep is reporting an impossible balance ({user.Value.Points} points, {user.Value.PendingPoints} pending) - keeping the last sensible figure");
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

		// Keep the points figure warm in the background so the rail and the Overview aren't stuck on 0 until
		// somebody happens to open the rep4rep tab. The TTL setting is what actually paces this.
		if (_mgr.Rep4Rep.HasToken) {
			_ = CachedPointsAsync(false);
		}

		return new {
			Version = "1.0.0",
			BootId = _started.Ticks,   // the browser resets its log buffer when this changes
			RefreshSeconds = Math.Clamp(_mgr.Global.WebRefreshSeconds, 1, 60),
			UptimeMinutes = (int) (DateTime.UtcNow - _started).TotalMinutes,
			Prompt = Prompt.Pending,
			PromptSecret = Prompt.PendingSecret,
			Rep4RepEnabled = _mgr.Global.Rep4RepEnabled,
			Rep4RepToken = _mgr.Rep4Rep.HasToken,
			Rep4RepWanted = _mgr.Global.Rep4RepEnabled ? bots.Count(static b => b.Cfg.Rep4Rep) : 0,
			// Genuinely reachable from the network AND unprotected. With no password the server already refuses
			// everything that isn't loopback, so warning about that case was crying wolf.
			Exposed = _mgr.Global.WebHost is not ("127.0.0.1" or "localhost") && !string.IsNullOrEmpty(_mgr.Global.WebPassword) && (_mgr.Global.WebPassword.Length < 8),
			LockedToThisPc = _mgr.Global.WebHost is not ("127.0.0.1" or "localhost") && string.IsNullOrEmpty(_mgr.Global.WebPassword),
			DebugOn = Log.DebugEnabled,
			Points = _pointsCache?.Points ?? 0,
			PendingPoints = _pointsCache?.Pending ?? 0,
			CardsToday = cards,
			CommentsToday = comments,
			CardsLeft = bots.Sum(static b => b.CardsRemaining),
			GamesLeft = bots.Sum(static b => b.GamesRemaining),
			Bots = bots.Select(static b => {
				Rep4RepModule? r4r = BotManager.ModuleOf<Rep4RepModule>(b);

				return new {
					Name = b.Name,
					Login = b.Cfg.SteamLogin,
					Notes = b.Cfg.Notes,
					Enabled = b.Cfg.Enabled,
					State = b.State.ToString(),
					Group = GroupOf(b),
					Status = Commands.StateWord(b),
					Detail = b.StatusText,
					Persona = b.PersonaWord,
					Seen = b.PlayingAsSeen,
					NameNotShowing = b.CustomNameNotShowing,
					Online = b.IsOnline,
					Paused = b.Paused,
					Blocked = b.PlayingBlocked,
					SteamId = b.SteamId.ToString(),
					UptimeMinutes = b.OnlineSince == null ? 0 : (int) (DateTime.UtcNow - b.OnlineSince.Value).TotalMinutes,
					Playing = b.Playing,
					Guard = b.GuardPrompt,
					Cards = b.CardsRemaining,
					Games = b.GamesRemaining,
					CustomName = b.Cfg.CustomGameName,
					Rep4Rep = b.Cfg.Rep4Rep,
				Legit = b.Cfg.LegitMode,
				HumanPhase = BotManager.ModuleOf<HumanMode>(b)?.Current.ToString() ?? "Off",
				HumanPlayedMinutes = BotManager.ModuleOf<HumanMode>(b)?.PlayedMinutesToday ?? 0,
				HumanTargetMinutes = BotManager.ModuleOf<HumanMode>(b)?.TargetMinutesToday ?? 0,
					Rep4RepToday = r4r?.PostsToday ?? 0,
					Rep4RepCap = r4r?.Cap ?? b.Cfg.Rep4RepDailyCap,
					Modules = b.Modules.Select(static m => new { Name = m.Name, Status = m.Status })
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
	}

	public async ValueTask DisposeAsync() {
		if (_app != null) {
			await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			await _app.DisposeAsync().ConfigureAwait(false);
			_app = null;
		}
	}
}
