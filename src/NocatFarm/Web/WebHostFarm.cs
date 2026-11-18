using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>
/// 1.9.2: farm.gamer.mom - the dashboard served from that website, talking straight to this app (Web/FarmWeb.cs says what
/// it may do). Everything here sleeps while "Let farm.gamer.mom open this dashboard" is off: no timer, no file read, nothing
/// held, and requests from there are turned away exactly as before.
///
/// The way in: the page asks (a pair request), gets a 6-character code, and the person at the app approves the same code
/// in the dashboard here. The page then gets a token of its own - Bearer only, never a cookie - kept hashed on disk like
/// the dashboard's sign-ins, and taken back by 'visitors signout' or its Revoke button.
/// </summary>
public sealed partial class WebHost {
	/// <summary>Set on a request from farm.gamer.mom once it's let past the front door: a <see cref="FarmSession"/>, or <see cref="FarmAnon"/> on the open routes.</summary>
	internal const string FarmKey = "nocat.farm/farm.gamer.mom";

	/// <summary>A request carrying a paired page's token. Hash: the token's, as kept.</summary>
	internal sealed record FarmSession(string Hash);

	/// <summary>A request from there with no token, on the hello and pairing routes only.</summary>
	private static readonly object FarmAnon = new();

	/// <summary>A request that came from farm.gamer.mom (with or without a token).</summary>
	internal static bool IsFarm(HttpContext ctx) => ctx.Items.ContainsKey(FarmKey);

	/// <summary>Sent by the page at farm.gamer.mom (or, this release only, farm.nocat.lol), exactly, with the setting on. Anything else goes the way it always went.</summary>
	private bool FromFarm(HttpRequest request) =>
		_cfg.FarmWebAllowed && (FarmWeb.AllowedOrigin(request.Headers.Origin.ToString()) != null);

	// ── where "open the dashboard" goes ──

	/// <summary>The port the dashboard really listens on - set when it starts listening.</summary>
	private int _port;

	/// <summary>When the browser was first sent to farm.gamer.mom with nothing paired yet; null before. Under <see cref="_farmGate"/>.</summary>
	private DateTime? _farmSentAt;

	/// <summary>
	/// What the tray, the window's Dashboard button, "Open the browser on start", a second launch and 'add' open: farm.gamer.mom
	/// once it's on and an account is set up, otherwise this PC's own address - as it always was.
	/// </summary>
	public string OpenUrl => FarmOpens() ? FarmWeb.PageUrl(_port) : Url;

	/// <summary>
	/// Whether "open the dashboard" goes to farm.gamer.mom now. Never while it's off, and never for setting up: with no account,
	/// none signed in here yet, or one waiting on a Steam Guard code, a QR code or an answer, it's this PC's own dashboard -
	/// a Steam password, a code or a QR sign-in never goes near that page. And when the page was opened a few minutes ago
	/// and nothing has paired since (no internet, or the browser wouldn't let it in), this PC's own again. No timer: it's
	/// worked out when it's asked.
	/// </summary>
	internal bool FarmOpens() {
		if (!_cfg.FarmWebAllowed || (Url.Length == 0) || (_port == 0)) {
			return false;
		}

		Bot[] bots = [.. _mgr.All];

		if ((bots.Length == 0) || !bots.Any(static b => (b.SteamId != 0) || TokenStore.Has(b.Name)) || (Prompt.Pending != null)
			|| bots.Any(static b => (b.QrChallenge != null) || (b.GuardPrompt != null))) {
			return false;
		}

		lock (_farmGate) {
			bool paired = FarmTokensLocked().Count > 0;
			DateTime now = DateTime.UtcNow;

			if (!paired && (_farmSentAt is { } sent) && (now - sent > FarmWeb.GiveUpAfter) && (_farmPairs.Count == 0)) {
				return false;
			}

			if (!paired) {
				_farmSentAt ??= now;
			}

			return true;
		}
	}

	// ── the front door ──

	/// <summary>
	/// A request from farm.gamer.mom, before anything else: answered here (a preflight, a refusal) - true - or let on to the
	/// endpoint with <see cref="FarmKey"/> set - false. In place of <see cref="Refusal"/>, which turns every other website
	/// away: the same rules it has for a page on this PC hold here too (this PC only, never through a proxy), and the
	/// Origin is one of the exact websites (FarmWeb.Origins).
	/// </summary>
	private async Task<bool> FarmFrontAsync(HttpContext ctx) {
		HttpRequest request = ctx.Request;
		IHeaderDictionary h = ctx.Response.Headers;

		// The origin it came from, one of the allowed ones, said back exactly - never '*', and never Allow-Credentials: the
		// page has no cookie here to send.
		h.AccessControlAllowOrigin = FarmWeb.AllowedOrigin(request.Headers.Origin.ToString());
		h.Vary = "Origin";

		// Only a browser on this PC reaches 127.0.0.1. Not another device (the dashboard on 0.0.0.0), not a proxy, and only by
		// a name no website can own - the DNS-rebinding rule.
		IPAddress from = ctx.Connection.RemoteIpAddress ?? IPAddress.None;

		if (!IPAddress.IsLoopback(from) || !IsLoopbackName(request.Host.Host) || request.Headers.ContainsKey("X-Forwarded-For")
			|| request.Headers.ContainsKey("X-Real-IP") || request.Headers.ContainsKey("Forwarded") || request.Headers.ContainsKey("X-Forwarded-Host")) {
			await FarmAnswerAsync(ctx, StatusCodes.Status403Forbidden, "this-pc", new Said("farm.gamer.mom only opens nocat.farm on the same PC, at http://127.0.0.1.")).ConfigureAwait(false);

			return true;
		}

		if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) {
			await FarmAnswerAsync(ctx, StatusCodes.Status403Forbidden, "api-only", FarmWeb.InAppOnly).ConfigureAwait(false);

			return true;
		}

		// The browser asking first. Chrome also asks whether a public website may reach this PC (Private Network Access).
		if (HttpMethods.IsOptions(request.Method)) {
			if (!request.Headers.ContainsKey("Access-Control-Request-Method")) {
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;

				return true;
			}

			h.AccessControlAllowMethods = "GET, POST, DELETE";
			h.AccessControlAllowHeaders = "Authorization, Content-Type";
			h.AccessControlMaxAge = "600";

			if (string.Equals(request.Headers["Access-Control-Request-Private-Network"].ToString(), "true", StringComparison.OrdinalIgnoreCase)) {
				h["Access-Control-Allow-Private-Network"] = "true";
			}

			ctx.Response.StatusCode = StatusCodes.Status204NoContent;

			return true;
		}

		// By the route as it's mapped, so '/api/bots/new/../x' or a different spelling can't land somewhere unlisted.
		if (ctx.GetEndpoint() is not RouteEndpoint endpoint) {
			await FarmAnswerAsync(ctx, StatusCodes.Status404NotFound, "not-found", FarmWeb.InAppOnly).ConfigureAwait(false);

			return true;
		}

		string key = $"{request.Method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}";
		FarmWeb.Access access = FarmWeb.RouteAccess(request.Method, endpoint.RoutePattern.RawText ?? "");

		// What was sent, read once, whatever the route: a body that isn't JSON, is too big, or names a field twice (in any
		// case) is refused before anything reads it - the check and the endpoint can't then each read a different one.
		(bool readable, JsonObject? sent) = await FarmBodyAsync(ctx).ConfigureAwait(false);

		if (!readable) {
			await FarmAnswerAsync(ctx, StatusCodes.Status400BadRequest, "bad-request", FarmWeb.InAppOnly).ConfigureAwait(false);

			return true;
		}

		if (access == FarmWeb.Access.Open) {
			ctx.Items[FarmKey] = FarmAnon;

			return false;
		}

		if (FarmSessionOf(request) is not { } session) {
			await FarmAnswerAsync(ctx, StatusCodes.Status401Unauthorized, "unauthorised", new Said("This browser isn't paired with nocat.farm yet.")).ConfigureAwait(false);

			return true;
		}

		ctx.Items[FarmKey] = session;

		if ((access == FarmWeb.Access.Refused) || ((access == FarmWeb.Access.Checked) && !FarmBodyAllowed(ctx, key, sent))) {
			await FarmAnswerAsync(ctx, StatusCodes.Status403Forbidden, "in-app", FarmWeb.InAppOnly).ConfigureAwait(false);

			return true;
		}

		return false;
	}

	/// <summary>
	/// A JSON answer the page can read - the words under every field name its buttons look for (ok/error, Ok/Error, Message,
	/// output), so any of them shows the reason as it is. Why, as a word: <paramref name="reason"/>.
	/// </summary>
	private static Task FarmAnswerAsync(HttpContext ctx, int status, string reason, Said message) {
		string text = message.ToString();

		ctx.Response.StatusCode = status;
		ctx.Response.ContentType = "application/json; charset=utf-8";

		// A dictionary, not an anonymous object: 'ok' and 'Ok' side by side are one name too many for the serializer.
		return ctx.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object> {
			["ok"] = false, ["error"] = text, ["reason"] = reason, ["Ok"] = false, ["Error"] = text, ["Message"] = text, ["output"] = text
		}));
	}

	/// <summary>
	/// What a <see cref="FarmWeb.Access.Checked"/> route was sent: the command line, the settings it changes, the switch it
	/// flips. Read and put back, so the endpoint reads it as it came.
	/// </summary>
	private bool FarmBodyAllowed(HttpContext ctx, string key, JsonObject? sent) {
		// What can't be read is refused here: the endpoint would only have answered "bad request" anyway.
		if (sent == null) {
			return false;
		}

		try {
			switch (key) {
				case "POST /api/command":
					// The endpoint reads it into CommandRequest, names in any case - and no name comes twice (FarmBodyAsync).
					return FarmWeb.CommandAllowed(_mgr, Field(sent, "Line"));
				case "POST /api/bots/{name}/switch": {
					// Exactly as the endpoint reads them. Every switch, on or off - but card swaps only off: they send cards to
					// other people.
					string? feature = Switches.FeatureOf(sent["Feature"]?.ToString() ?? "");
					bool on = (sent["On"] is JsonValue value) && value.TryGetValue(out bool b) && b;

					return !((feature == nameof(BotConfig.CardSwaps)) && on);
				}
				case "POST /api/config":
					return FarmPasswordAllowed(ctx, sent) is { } rest && FarmSettingsAllowed(_mgr.Global, Settings.Global, rest, JsonOptionsOf(ctx), global: true);
				case "POST /api/bots/{name}/config":
					return (ctx.GetRouteValue("name") is string name) && (_mgr.Get(name) is { } bot) && FarmSettingsAllowed(bot.Cfg, Settings.Bot, sent, JsonOptionsOf(ctx), global: false);
				default:
					return false;
			}
		} catch (Exception) {
			// Never a bare 500 from here: whatever the check can't work out is refused.
			return false;
		}

		static string Field(JsonObject o, string name) =>
			o.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? "";
	}

	/// <summary>The most a request from farm.gamer.mom may send: a whole settings save is a few tens of KB.</summary>
	internal const long FarmMaxBody = 1024 * 1024;

	/// <summary>
	/// A request's body from farm.gamer.mom, read once and put back for the endpoint. Readable: nothing sent, or one JSON value
	/// no bigger than <see cref="FarmMaxBody"/> in which no object names a field twice - exactly or in another case. The
	/// endpoints read names in any case, some the first and some the last: a name sent twice could pass the check as one
	/// value and reach the endpoint as the other. The object, when it is one.
	/// </summary>
	private static async Task<(bool Readable, JsonObject? Sent)> FarmBodyAsync(HttpContext ctx) {
		HttpRequest request = ctx.Request;

		if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || (request.ContentLength == 0)) {
			return (true, null);
		}

		if (request.ContentLength > FarmMaxBody) {
			return (false, null);
		}

		if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) {
			limit.MaxRequestBodySize = FarmMaxBody;
		}

		request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: FarmMaxBody);

		try {
			using MemoryStream copy = new();
			await request.Body.CopyToAsync(copy, ctx.RequestAborted).ConfigureAwait(false);

			if (copy.Length == 0) {
				return (true, null);
			}

			JsonNode? node = JsonNode.Parse(copy.ToArray());

			return OnceEach(node) ? (true, node as JsonObject) : (false, null);
		} catch (Exception e) when (e is not OperationCanceledException) {
			// Not JSON, too big, or a name twice exactly (the parser throws on that one itself).
			return (false, null);
		} finally {
			request.Body.Position = 0;
		}

		static bool OnceEach(JsonNode? node) {
			switch (node) {
				case JsonObject o: {
					HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

					foreach ((string name, JsonNode? value) in o) {
						if (!seen.Add(name) || !OnceEach(value)) {
							return false;
						}
					}

					return true;
				}
				case JsonArray a:
					return a.All(OnceEach);
				default:
					return true;
			}
		}
	}

	/// <summary>
	/// The dashboard password in a global save from farm.gamer.mom: a first one only here (a swapped page setting one would lock the owner out), an existing one changed
	/// only with the current one beside it ("CurrentPassword", checked like a sign-in and counted towards the same lockout),
	/// and never removed from there. Null when that's refused; otherwise what was sent without the password, for the rest of
	/// the checks. The change signs every browser out, the pages at farm.gamer.mom too - they pair again.
	/// </summary>
	private JsonObject? FarmPasswordAllowed(HttpContext ctx, JsonObject sent) {
		string? key = sent.Select(static kv => kv.Key).FirstOrDefault(static k => k.Equals(nameof(GlobalConfig.WebPassword), StringComparison.OrdinalIgnoreCase));
		string typed = key == null ? "" : sent[key]?.ToString() ?? "";
		JsonObject rest = sent.DeepClone().AsObject();

		if (key != null) {
			rest.Remove(key);
		}

		if (typed.Length == 0) {
			return rest;   // an empty box is "leave it as it is"
		}

		if (typed is ClearSecret or OldClearSecret) {
			return null;   // removing it: only here
		}

		if (string.IsNullOrEmpty(_cfg.WebPassword)) {
			return null;   // a first one: only here - set from a page that was swapped, it would lock the owner out of their own dashboard
		}

		string current = sent.FirstOrDefault(static kv => kv.Key.Equals("CurrentPassword", StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? "";

		return FarmPasswordRight(ctx, current) ? rest : null;
	}

	/// <summary>The current dashboard password, checked as a sign-in is: constant time, and a wrong one counted towards the lockout.</summary>
	private bool FarmPasswordRight(HttpContext ctx, string typed) {
		(string ip, bool thisPc) = WhoIsSigningIn(ctx);

		if (Reserve(ip, thisPc, IsInternet(ip) && !thisPc, ctx.Request.Headers.UserAgent.FirstOrDefault()) is not { } attempt) {
			return false;
		}

		bool ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(typed)), SHA256.HashData(Encoding.UTF8.GetBytes(_cfg.WebPassword)));

		if (ok) {
			GiveBack(attempt);
		} else {
			CountFailure(attempt, Visitors.What.WrongPassword);
		}

		return ok;
	}

	/// <summary>
	/// A settings save from farm.gamer.mom: worked out as the endpoint will (merged onto the settings as they are, secrets
	/// kept), and every field it would change allowed (<see cref="FarmWeb.SettingAllowed"/>). A field that isn't a setting is
	/// refused on an account; on the global settings the save puts those back itself.
	/// </summary>
	private static bool FarmSettingsAllowed<T>(T live, IReadOnlyList<SettingDef> defs, JsonObject sent, JsonSerializerOptions options, bool global) where T : class, new() {
		T current = Clone(live);

		if (Merge(Clone(current), sent, options) is not { } body) {
			return false;
		}

		KeepSecrets(body, current, defs);

		foreach (System.Reflection.PropertyInfo p in typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
			if (!p.CanRead || !p.CanWrite || (p.GetIndexParameters().Length > 0)) {
				continue;
			}

			object? after = p.GetValue(body);

			if (JsonSerializer.Serialize(after, p.PropertyType) == JsonSerializer.Serialize(p.GetValue(current), p.PropertyType)) {
				continue;
			}

			SettingDef? def = defs.FirstOrDefault(d => d.Name == p.Name);

			if (def == null) {
				if (global) {
					continue;
				}

				return false;
			}

			if (!FarmWeb.ChangeAllowed(def, after)) {
				return false;
			}
		}

		return true;
	}

	// ── paired pages ──

	/// <summary>One paired page: when it was approved, when it runs out, and what it was opened in.</summary>
	internal sealed record FarmToken(DateTime Created, DateTime Expires, string Device);

	/// <summary>On disk: the password fingerprint they were issued under (as the sign-ins), and each token's hash.</summary>
	private sealed record SavedFarmTokens(string Key, Dictionary<string, FarmToken> Tokens);

	private static string FarmTokensPath => Path.Combine(ConfigStore.ConfigDir, "state", "farm-tokens.json");

	/// <summary>Hash of each token, read from disk the first time one is needed - never at start.</summary>
	private Dictionary<string, FarmToken>? _farmTokens;

	/// <summary>The tokens, the pair requests and their counts, all under this.</summary>
	private readonly Lock _farmGate = new();

	private Dictionary<string, FarmToken> FarmTokensLocked() {
		if (_farmTokens != null) {
			return _farmTokens;
		}

		_farmTokens = new(StringComparer.Ordinal);

		try {
			if (File.Exists(FarmTokensPath) && (Secrets.Unprotect(File.ReadAllText(FarmTokensPath)) is { Length: > 0 } json)
				&& (JsonSerializer.Deserialize<SavedFarmTokens>(json) is { } saved) && (saved.Key == PasswordKey)) {
				foreach ((string hash, FarmToken t) in saved.Tokens ?? []) {
					if (t.Expires > DateTime.UtcNow) {
						_farmTokens[hash] = t;
					}
				}
			}
		} catch (Exception e) {
			// every page pairs again - nothing worse
			Log.Failed("dashboard: reading the farm.gamer.mom pairings", e);
		}

		return _farmTokens;
	}

	private void SaveFarmTokensLocked() {
		try {
			Dictionary<string, FarmToken> live = (_farmTokens ?? []).Where(static kv => kv.Value.Expires > DateTime.UtcNow).ToDictionary(StringComparer.Ordinal);

			if (live.Count == 0) {
				if (File.Exists(FarmTokensPath)) {
					File.Delete(FarmTokensPath);
				}

				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(FarmTokensPath)!);
			AtomicFile.Write(FarmTokensPath, Secrets.Protect(JsonSerializer.Serialize(new SavedFarmTokens(PasswordKey, live)), "dashboard"));
		} catch (Exception e) {
			// they last until the next restart, then
			Log.Failed("dashboard: saving the farm.gamer.mom pairings", e);
		}
	}

	/// <summary>The paired page a request's Bearer token belongs to, or null. A cookie is never looked at.</summary>
	private FarmSession? FarmSessionOf(HttpRequest request) {
		string auth = request.Headers.Authorization.ToString();

		if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || (auth.Length < 20)) {
			return null;
		}

		string hash = Hash(auth[7..].Trim());

		lock (_farmGate) {
			return FarmTokensLocked().TryGetValue(hash, out FarmToken? t) && (t.Expires > DateTime.UtcNow) ? new FarmSession(hash) : null;
		}
	}

	/// <summary>Every paired page forgotten - 'visitors signout', a changed password. The file goes without being read.</summary>
	private void ForgetFarm() {
		lock (_farmGate) {
			_farmTokens = null;
			_farmPairs.Clear();

			try {
				if (File.Exists(FarmTokensPath)) {
					File.Delete(FarmTokensPath);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed("dashboard: forgetting the farm.gamer.mom pairings", e);
			}
		}
	}

	/// <summary>The paired pages, for the Phone page's list - read from disk only when it's asked for.</summary>
	private object[] FarmTokenList() {
		if (!_cfg.FarmWebAllowed && (_farmTokens == null) && !File.Exists(FarmTokensPath)) {
			return [];
		}

		lock (_farmGate) {
			return [.. FarmTokensLocked().Where(static kv => kv.Value.Expires > DateTime.UtcNow).OrderBy(static kv => kv.Value.Created)
				.Select(static kv => (object) new { Id = kv.Key[..12], kv.Value.Created, kv.Value.Expires, kv.Value.Device })];
		}
	}

	// ── pair requests ──

	/// <summary>One page asking to be let in. State: 0 waiting, 1 approved, 2 turned down.</summary>
	private sealed class FarmPair(string id, string code, string device, DateTime expires) {
		public string Id { get; } = id;
		public string Code { get; } = code;
		public string Device { get; } = device;
		public DateTime Expires { get; } = expires;
		public int State { get; set; }
	}

	/// <summary>At most <see cref="FarmWeb.MaxPending"/>, each gone after <see cref="FarmWeb.PairLifetime"/>. Under <see cref="_farmGate"/>.</summary>
	private readonly List<FarmPair> _farmPairs = [];

	/// <summary>When the last few were asked for, for <see cref="FarmWeb.PairsPerMinute"/>. Under <see cref="_farmGate"/>.</summary>
	private readonly Queue<DateTime> _farmAsks = new();

	/// <summary>The ones that have run out, dropped - on every look, so nothing has to keep time. Under <see cref="_farmGate"/>.</summary>
	private void PruneFarmLocked(DateTime now) {
		_farmPairs.RemoveAll(p => p.Expires <= now);

		while (_farmAsks.TryPeek(out DateTime oldest) && (now - oldest >= TimeSpan.FromMinutes(1))) {
			_farmAsks.Dequeue();
		}
	}

	/// <summary>What the dashboard here shows as its "approve this?" banner. Empty while the setting is off, and on the page at farm.gamer.mom.</summary>
	private object[] FarmPairsForPage(bool farm) {
		lock (_farmGate) {
			if (!_cfg.FarmWebAllowed) {
				_farmPairs.Clear();

				return [];
			}

			if (farm || (_farmPairs.Count == 0)) {
				return [];
			}

			DateTime now = DateTime.UtcNow;
			PruneFarmLocked(now);

			return [.. _farmPairs.Where(static p => p.State == 0)
				.Select(p => (object) new { p.Id, p.Code, p.Device, Seconds = (int) Math.Ceiling((p.Expires - now).TotalSeconds) })];
		}
	}

	private void MapFarm(WebApplication app) {
		// What the page looks for first: is nocat.farm there, and can it talk to this page. Nothing else - not whether this
		// browser is paired, not a name or an account.
		app.MapGet("/api/farm/hello", (HttpContext ctx) => IsFarm(ctx)
			? Results.Json(new { app = "nocat.farm", version = Build.Version, api = FarmWeb.ApiVersion, paired = false })
			: Results.NotFound());

		app.MapPost("/api/farm/pair", (HttpContext ctx) => {
			if (!IsFarm(ctx)) {
				return Results.NotFound();
			}

			DateTime now = DateTime.UtcNow;
			string device = Visitors.Device(ctx.Request.Headers.UserAgent.FirstOrDefault());
			FarmPair pair;

			lock (_farmGate) {
				PruneFarmLocked(now);

				if (_farmPairs.Count >= FarmWeb.MaxPending) {
					return Results.Json(new { ok = false, error = "busy", Message = new Said("Too many requests are waiting at nocat.farm. Answer them in the app, or wait 5 minutes.").ToString() }, statusCode: 429);
				}

				if (_farmAsks.Count >= FarmWeb.PairsPerMinute) {
					return Results.Json(new { ok = false, error = "slow", Message = new Said("Asked too often - wait a minute and try again.").ToString() }, statusCode: 429);
				}

				_farmAsks.Enqueue(now);
				pair = new FarmPair(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), FarmWeb.NewCode(), device, now + FarmWeb.PairLifetime);
				_farmPairs.Add(pair);
			}

			// Loud, for somebody doing it the first time: the log line, a pop-up from the tray, and Telegram/Discord when they're set
			// up - each saying where to approve it. Never an approve button anywhere but the dashboard on this PC.
			Log.Attention(new Said("dashboard: farm.gamer.mom ({0}) asks to open this dashboard - code {1}. Approve or deny it in the dashboard on this PC.", device, pair.Code),
				new Said("farm.gamer.mom asks to open your nocat.farm dashboard (code {0}, {1}). If that's you, approve it in the app's own dashboard on your PC. If not, do nothing - it runs out in 5 minutes.", pair.Code, device),
				new Said("farm.gamer.mom wants to open your dashboard"), Topic.Security);

			return Results.Json(new { ok = true, pair.Id, pair.Code, Seconds = (int) FarmWeb.PairLifetime.TotalSeconds });
		});

		// The page asking how its request went. Approved: its token, once - the request is gone after that.
		app.MapGet("/api/farm/pair/{id}", (HttpContext ctx, string id) => {
			if (!IsFarm(ctx)) {
				return Results.NotFound();
			}

			DateTime now = DateTime.UtcNow;

			lock (_farmGate) {
				PruneFarmLocked(now);
				FarmPair? pair = _farmPairs.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p.Id), Encoding.UTF8.GetBytes(id)));

				if (pair == null) {
					return Results.Json(new { State = "expired" });
				}

				if (pair.State == 0) {
					return Results.Json(new { State = "waiting", Seconds = (int) Math.Ceiling((pair.Expires - now).TotalSeconds) });
				}

				_farmPairs.Remove(pair);

				if (pair.State != 1) {
					return Results.Json(new { State = "denied" });
				}

				string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
				Dictionary<string, FarmToken> tokens = FarmTokensLocked();
				tokens[Hash(token)] = new FarmToken(now, now.AddDays(FarmWeb.TokenDays), pair.Device);

				// The oldest goes past the most kept: a page paired again and again never grows the list.
				foreach (string old in tokens.OrderByDescending(static kv => kv.Value.Created).Skip(FarmWeb.MaxTokens).Select(static kv => kv.Key).ToArray()) {
					tokens.Remove(old);
				}

				SaveFarmTokensLocked();

				return Results.Json(new { State = "approved", Token = token });
			}
		});

		// The banner's Approve and Deny. Only the dashboard here - on this PC, or signed in with the password - never farm.gamer.mom
		// itself (Routes refuses it before it gets here).
		app.MapPost("/api/farm/answer", async (HttpContext ctx) => {
			if (IsFarm(ctx) || !Authorised(ctx)) {
				return Unauthorised();
			}

			if (!AtThisPc(ctx) && string.IsNullOrEmpty(_cfg.WebPassword)) {
				return Results.Json(new { ok = false, error = new Said("only from the PC nocat.farm runs on").ToString() }, statusCode: 403);
			}

			FarmAnswerRequest? body = await ReadJsonAsync<FarmAnswerRequest>(ctx).ConfigureAwait(false);
			FarmPair? pair;

			lock (_farmGate) {
				PruneFarmLocked(DateTime.UtcNow);
				pair = _farmPairs.FirstOrDefault(p => (p.Id == body?.Id) && (p.State == 0));

				if ((pair != null) && (body != null)) {
					pair.State = body.Approve ? 1 : 2;
				}
			}

			if ((pair == null) || (body == null)) {
				return Results.Json(new { ok = false, error = new Said("That request has run out - ask again on farm.gamer.mom.").ToString() });
			}

			if (body.Approve) {
				Log.Good(new Said("dashboard: farm.gamer.mom ({0}) let in - code {1}", pair.Device, pair.Code));
			} else {
				Log.Info(new Said("dashboard: farm.gamer.mom ({0}) turned down - code {1}", pair.Device, pair.Code));
			}

			return Results.Json(new { ok = true });
		});

		// Taking a paired page back: any of them from here, only its own from farm.gamer.mom.
		app.MapPost("/api/farm/revoke", async (HttpContext ctx) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			FarmAnswerRequest? body = await ReadJsonAsync<FarmAnswerRequest>(ctx).ConfigureAwait(false);
			bool gone;

			lock (_farmGate) {
				Dictionary<string, FarmToken> tokens = FarmTokensLocked();
				string? hash = ctx.Items.TryGetValue(FarmKey, out object? who) && (who is FarmSession own)
					? own.Hash
					: tokens.Keys.FirstOrDefault(k => (body?.Id is { Length: 12 } id) && k.StartsWith(id, StringComparison.Ordinal));
				gone = (hash != null) && tokens.Remove(hash);

				if (gone) {
					SaveFarmTokensLocked();
				}
			}

			if (gone) {
				Log.Info(new Said("dashboard: a farm.gamer.mom pairing was taken back"));
			}

			return Results.Json(new { ok = gone });
		});
	}

	private sealed class FarmAnswerRequest {
		public string? Id { get; set; }
		public bool Approve { get; set; }
	}
}
