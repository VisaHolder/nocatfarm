using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NocatFarm.Core;

/// <summary>
/// The account's steamcommunity.com / store.steampowered.com session.
///
/// There is no "web login" step and no endpoint to call: Steam accepts a locally-built cookie pair. The access
/// token handed back by the SteamKit auth flow IS the credential, so a session is just
/// <c>steamLoginSecure = "&lt;steamID64&gt;||&lt;accessToken&gt;"</c> plus a client-chosen <c>sessionid</c>.
/// That same sessionid must also be echoed in the body of every POST - that is Steam's CSRF check.
///
/// Access tokens expire (usually 24h). When one does, Steam answers with a redirect to /login instead of an
/// error, so expiry is detected from the FINAL url of a response, then the token is re-minted over the live
/// Steam connection and the request is retried exactly once.
/// </summary>
public sealed class WebSession : IDisposable {
	public static readonly Uri Community = new("https://steamcommunity.com");
	public static readonly Uri Store = new("https://store.steampowered.com");
	public static readonly Uri Help = new("https://help.steampowered.com");
	public static readonly Uri Api = new("https://api.steampowered.com");

	private static readonly Uri[] Domains = [Community, Store, Help, Api, new Uri("https://checkout.steampowered.com")];

	private readonly Bot _bot;
	private readonly CookieContainer _cookies = new();
	private readonly HttpClient _http;
	private readonly SemaphoreSlim _refreshLock = new(1, 1);

	public bool Ready { get; private set; }
	public string SessionId { get; private set; } = "";

	/// <summary>The account's current access token, or empty. Only the API caller below needs it directly.</summary>
	private string _accessToken = "";

	/// <summary>When the current access token stops being usable. Refreshed a few minutes before this.</summary>
	public DateTime? TokenValidUntil { get; private set; }

	public WebSession(Bot bot) {
		_bot = bot;

		// Same proxy as the Steam client socket. Without this the badge scraping and comment posting - i.e. every
		// request that identifies the account - would go out directly while only the CM connection was proxied.
		HttpClientHandler handler = Core.Bot.BuildProxyHandler(bot.Cfg);
		handler.CookieContainer = _cookies;
		handler.UseCookies = true;
		handler.AllowAutoRedirect = true;
		handler.MaxAutomaticRedirections = 5;
		handler.AutomaticDecompression = DecompressionMethods.All;

		_http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
		_http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Browser.UserAgent);   // see Browser for why this matters
	}

	/// <summary>Build the cookies for an access token. Local only - no network call happens here.</summary>
	public void Init(ulong steamId, string accessToken) {
		string steamLoginSecure = $"{steamId}||{accessToken}";

		// 24 lowercase hex chars, which is the shape Steam's own pages use.
		SessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

		// Seconds east of UTC, then a literal escaped comma and "0" - Steam's shared_global.js writes exactly this.
		string timezone = $"{(int) DateTimeOffset.Now.Offset.TotalSeconds}{Uri.EscapeDataString(",")}0";

		foreach (Uri domain in Domains) {
			string host = "." + domain.Host;

			_cookies.Add(new Cookie("sessionid", SessionId, "/", host));
			_cookies.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", host));
			_cookies.Add(new Cookie("timezoneOffset", timezone, "/", host));
		}

		_accessToken = accessToken;
		TokenValidUntil = ReadJwtExpiry(accessToken);
		Ready = true;
	}

	/// <summary>
	/// Call a Steam Web API service, authenticated as this account.
	///
	/// Some of Steam's services simply are not answered over the client connection - ask the CM for
	/// Player.GetOwnedGames and the job times out with no reply at all - but the same method over api.steampowered
	/// .com with the account's own access token returns everything, private profile or not. The token is the one
	/// already minted for the web session, so this costs no extra login.
	/// </summary>
	public async Task<string?> ApiGetAsync(string service, string method, Dictionary<string, string>? args, CancellationToken ct = default) {
		// Always asked, not only when the session is marked broken: an API call carries the token in the URL, so an
		// expired one just fails - there's no login redirect to notice, the way the community site has. RefreshAsync
		// is a no-op while the token has life left and renews it when it's nearly spent.
		if (!await RefreshAsync(false, ct).ConfigureAwait(false)) {
			return null;
		}

		if (string.IsNullOrEmpty(_accessToken)) {
			return null;
		}

		List<string> query = [$"access_token={Uri.EscapeDataString(_accessToken)}"];

		foreach ((string key, string value) in args ?? []) {
			query.Add($"{key}={Uri.EscapeDataString(value)}");
		}

		return await SendAsync(new Uri(Api, $"/{service}/{method}/v1/?{string.Join('&', query)}"), null, null, true, ct).ConfigureAwait(false);
	}

	/// <summary>The same as <see cref="ApiGetAsync"/>, for the Web API methods that only take a POST.</summary>
	public async Task<string?> ApiPostAsync(string service, string method, Dictionary<string, string>? form, CancellationToken ct = default) {
		if (!await RefreshAsync(false, ct).ConfigureAwait(false)) {
			return null;
		}

		if (string.IsNullOrEmpty(_accessToken)) {
			return null;
		}

		return await SendAsync(new Uri(Api, $"/{service}/{method}/v1/?access_token={Uri.EscapeDataString(_accessToken)}"),
			form ?? [], new Uri(Store, "/"), true, ct).ConfigureAwait(false);
	}

	public void Invalidate() => Ready = false;

	/// <summary>
	/// A request's path and query, safe to write down: the API calls carry this account's access token in the query,
	/// and every failed or timed-out request used to log it in full to a file that is kept for weeks.
	/// </summary>
	private static string Loggable(Uri url) =>
		// k and p too: a mobile confirmation request carries its signature and the authenticator's device id in them.
		System.Text.RegularExpressions.Regex.Replace(url.PathAndQuery, "(?i)((?:access_token|key|token|password|webapi_token)=|[?&](?:k|p)=)[^&]*", "$1[hidden]");

	// ── requests ────────────────────────────────────────────────────────────
	public async Task<string?> GetAsync(Uri url, CancellationToken ct = default) => await SendAsync(url, null, null, true, ct).ConfigureAwait(false);

	/// <summary>
	/// POST a form. The sessionid is injected from the cookie jar per request rather than cached, so if Steam ever
	/// replaces the cookie the body and the cookie stay in agreement automatically.
	/// </summary>
	/// <param name="errorVerdict">Hand back a JSON body that came with an error status instead of null. The free
	/// licence endpoint answers a refusal with HTTP 500 and {"purchaseresultdetail":24} - the reason is the reply.</param>
	public async Task<string?> PostAsync(Uri url, Dictionary<string, string> form, Uri? referer = null, CancellationToken ct = default, bool errorVerdict = false) =>
		await SendAsync(url, form, referer, true, ct, errorVerdict: errorVerdict).ConfigureAwait(false);

	/// <summary>
	/// POST a form whose fields may repeat - Steam's "cid[]=1&amp;cid[]=2" lists. The sessionid is added the same way as
	/// <see cref="PostAsync"/> adds it.
	/// </summary>
	public async Task<string?> PostPairsAsync(Uri url, IEnumerable<KeyValuePair<string, string>> form, Uri? referer = null, CancellationToken ct = default) =>
		await SendAsync(url, form, referer, true, ct).ConfigureAwait(false);

	/// <summary>Whether a request actually left this PC - set the moment it's handed to the network.</summary>
	private sealed class SendNote {
		public bool Sent;
	}

	/// <summary>
	/// POST a form, and say whether it ever went out. A null body with Sent false means nothing reached Steam at all - no
	/// session, no sessionid cookie, the host shut for a rate limit - so whatever it was for didn't happen. With Sent true
	/// and no body, Steam may well have acted on it: the answer just never came back.
	/// </summary>
	public async Task<(string? Body, bool Sent)> PostTrackedAsync(Uri url, Dictionary<string, string> form, Uri? referer = null, CancellationToken ct = default) {
		SendNote note = new();
		string? body = await SendAsync(url, form, referer, true, ct, note: note).ConfigureAwait(false);

		return (body, note.Sent || (body != null));
	}

	private async Task<string?> SendAsync(Uri url, IEnumerable<KeyValuePair<string, string>>? form, Uri? referer, bool allowRetry, CancellationToken ct, bool skipReadyCheck = false, bool errorVerdict = false, SendNote? note = null) {
		if (!skipReadyCheck && !Ready && !await RefreshAsync(true, ct).ConfigureAwait(false)) {
			return null;
		}

		HttpResponseMessage? response = null;
		string? body = null;

		try {
			response = await Limiters.WebAsync(url.Host, async () => {
				using HttpRequestMessage request = new(form == null ? HttpMethod.Get : HttpMethod.Post, url);

				if (form != null) {
					string? cookieSession = CookieValue(url, "sessionid");

					if (string.IsNullOrEmpty(cookieSession)) {
						Log.Debug($"POST {Loggable(url)} not sent: no sessionid cookie for {url.Host}", _bot.Name);

						return null;
					}

					// The CSRF token is the sessionid, but the field casing Steam accepts is not consistent across
					// the community site: the comment endpoints read "sessionid", while the group-join endpoint reads
					// only "sessionID" and answers a lowercase-only POST with a 200 "invalid form session key" error
					// page that looks just like success. Sending both - the same value - satisfies either, so a caller
					// never has to know which a given endpoint wants.
					List<KeyValuePair<string, string>> payload = [
						.. form.Where(static f => f.Key is not ("sessionid" or "sessionID")),
						new("sessionid", cookieSession),
						new("sessionID", cookieSession)
					];
					request.Content = new FormUrlEncodedContent(payload);
				}

				if (referer != null) {
					request.Headers.Referrer = referer;
				}

				if (note != null) {
					note.Sent = true;   // from here on it may have reached Steam, whatever happens next
				}

				return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
			}).ConfigureAwait(false);

			if (response == null) {
				NoteSkippedForRateLimit(url);

				return null;
			}

			Uri final = response.RequestMessage?.RequestUri ?? url;

			// Steam redirects an expired session to the login page instead of failing the request.
			if (IsSessionExpired(final)) {
				Ready = false;

				if (!allowRetry) {
					Log.Debug($"{(form == null ? "GET" : "POST")} {Loggable(url)}: sent to the login page again, even with a fresh token", _bot.Name);

					return null;
				}

				Log.Debug("web session expired - re-minting the access token", _bot.Name);

				// remint: the cached token is the one Steam just turned away. Without it the "re-mint" rebuilt the
				// same rejected token into cookies and the retry bounced exactly as before.
				if (!await RefreshAsync(true, ct, remint: true).ConfigureAwait(false)) {
					return null;
				}

				return await SendAsync(url, form, referer, false, ct, errorVerdict: errorVerdict, note: note).ConfigureAwait(false);
			}

			if (!response.IsSuccessStatusCode) {
				// A 429 is not a fault to be retried on the caller's usual schedule - it is Steam saying this IP
				// has asked too often, and every further request while it stands makes it last longer. Shut the
				// whole host for everybody instead, and don't print the generic error page that comes with it.
				if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
					TimeSpan shut = Limiters.NoteRateLimited(url.Host);

					if (shut > TimeSpan.Zero) {
						Log.Warn(new Said("{0} is rate-limiting - all accounts wait {1}", url.Host, Fmt.Hm((int) shut.TotalMinutes)), _bot.Name);
						Log.Debug($"rate-limited on {url.AbsolutePath}", _bot.Name);   // which page tripped it, to see what to slow down
					}

					return null;
				}

				// The Web API says a token is no good with a 401 - the next call renews it rather than failing forever.
				if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized) && (url.Host == Api.Host)) {
					Ready = false;
				}

				// Steam puts the REASON in the body of a failed POST - "you cannot trade because...", "this
				// account is trade banned" - and throwing it away left every failure looking like a network
				// fault. The first couple of hundred characters is always enough to say what went wrong.
				string failure = "";

				try {
					failure = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
				} catch {
					// the status code on its own will have to do
				}

				Log.Debug(new Said("{0} {1} -> {2}", (form == null ? "GET" : "POST"), Loggable(url), (int) response.StatusCode)
					+ (failure.Length > 0 ? $"  {Log.Scrub(failure[..Math.Min(300, failure.Length)])}" : ""), _bot.Name);

				return errorVerdict && failure.StartsWith('{') ? failure : null;
			}

			Limiters.NoteWebOk(url.Host);
			body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;   // genuinely shutting down
		} catch (OperationCanceledException e) {
			// HttpClient reports its own 30s timeout as a cancellation with nobody having cancelled anything.
			// Rethrowing that killed the calling module's loop outright and looked exactly like a clean shutdown.
			Log.Debug(new Said("{0} {1} timed out: {2}", (form == null ? "GET" : "POST"), Loggable(url), Log.Describe(e)), _bot.Name);

			return null;
		} catch (Exception e) {
			Log.Debug(new Said("{0} {1} failed: {2}", (form == null ? "GET" : "POST"), Loggable(url), Log.Describe(e)), _bot.Name);

			return null;
		} finally {
			response?.Dispose();
		}

		return body;
	}

	/// <summary>
	/// POST and hand back the body whatever the status code was.
	///
	/// Steam explains itself in the BODY of a failed POST - a trade offer refusal comes back as a 500 carrying
	/// {"strError":"...(15)"} - so the ordinary path, which returns null on any non-2xx, throws away the only
	/// useful part of the answer and leaves the caller guessing at the cause.
	/// </summary>
	public async Task<string?> PostAllowingFailureAsync(Uri url, Dictionary<string, string> form, Uri? referer = null, CancellationToken ct = default) {
		if (!Ready && !await RefreshAsync(true, ct).ConfigureAwait(false)) {
			return null;
		}

		try {
			string? answer = await Limiters.WebAsync(url.Host, async () => {
				using HttpRequestMessage request = new(HttpMethod.Post, url);
				string? cookieSession = CookieValue(url, "sessionid");

				if (string.IsNullOrEmpty(cookieSession)) {
					Log.Debug($"POST {Loggable(url)} not sent: no sessionid cookie for {url.Host}", _bot.Name);

					return null;
				}

				Dictionary<string, string> payload = new(form, StringComparer.Ordinal) {
					["sessionid"] = cookieSession,
					["sessionID"] = cookieSession
				};
				request.Content = new FormUrlEncodedContent(payload);

				if (referer != null) {
					request.Headers.Referrer = referer;
				}

				using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

				// This path keeps the body of a failure on purpose, but a 429 carries nothing worth keeping and
				// still has to shut the host - otherwise the one caller that wants failure bodies is the one
				// caller that goes on hammering a live rate limit.
				if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
					TimeSpan shut = Limiters.NoteRateLimited(url.Host);

					if (shut > TimeSpan.Zero) {
						Log.Warn(new Said("{0} is rate-limiting - all accounts wait {1}", url.Host, Fmt.Hm((int) shut.TotalMinutes)), _bot.Name);
						Log.Debug($"rate-limited on {url.AbsolutePath}", _bot.Name);   // which page tripped it, to see what to slow down
					}

					return null;
				}

				// The body still goes back - the caller reads Steam's reason out of it - but the status itself was
				// never written down, so a refusal whose body the caller couldn't make sense of left no trace.
				if (!response.IsSuccessStatusCode) {
					Log.Debug($"POST {Loggable(url)} -> {(int) response.StatusCode}", _bot.Name);
				}

				Limiters.NoteWebOk(url.Host);

				return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			}).ConfigureAwait(false);

			if (answer == null) {
				NoteSkippedForRateLimit(url);
			}

			return answer;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug(new Said("POST {0} failed: {1}", Loggable(url), Log.Describe(e)), _bot.Name);

			return null;
		}
	}

	/// <summary>
	/// A request that came back with nothing because its host is serving a 429 wait - the limiter turns it away without
	/// asking, so nothing else says so. Once per host per account (an hour apart at most): while the wait lasts, every
	/// request of every module ends up here.
	/// </summary>
	private void NoteSkippedForRateLimit(Uri url) {
		if (Limiters.RateLimitedFor(url.Host) > TimeSpan.Zero) {
			Log.DebugOnChange($"ratelimited:{_bot.Name}:{url.Host}", $"requests to {url.Host} held back while it is rate-limited", _bot.Name);
		} else {
			Log.Recovered($"ratelimited:{_bot.Name}:{url.Host}");
		}
	}

	private static bool IsSessionExpired(Uri uri) =>
		uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
		|| uri.Host.Equals("lostauth", StringComparison.OrdinalIgnoreCase);

	private string? CookieValue(Uri url, string name) {
		foreach (Cookie c in _cookies.GetCookies(url).Cast<Cookie>()) {
			if (c.Name.Equals(name, StringComparison.Ordinal)) {
				return c.Value;
			}
		}

		return null;
	}

	// ── token lifecycle ─────────────────────────────────────────────────────
	/// <summary>
	/// Make sure there is a usable access token and cookies built from it. Cheap and idempotent when the current
	/// token still has life left in it.
	/// </summary>
	/// <param name="remint">
	/// Steam rejected the current token, so a new one has to be minted rather than the cached one rebuilt into
	/// cookies again. Kept apart from <paramref name="force"/> on purpose: force also runs after every reconnect
	/// (the session is marked not-ready), and minting there would be a new web session per reconnect - the exact
	/// thing that signs the owner out of Friends &amp; Chat.
	/// </param>
	public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default, bool remint = false) {
		bool parentalNeeded;

		await _refreshLock.WaitAsync(ct).ConfigureAwait(false);

		try {
			if (!force && Ready && (TokenValidUntil == null || TokenValidUntil > DateTime.UtcNow.AddMinutes(5))) {
				return true;
			}

			string? token = await _bot.GetAccessTokenAsync(remint).ConfigureAwait(false);

			if (string.IsNullOrEmpty(token)) {
				Ready = false;
				// Every web request asks, so this repeats for as long as the account is offline - once is enough.
				Log.DebugOnChange($"webtoken:{_bot.Name}", "web session not refreshed: no access token (account offline, or getting one failed)", _bot.Name);

				return false;
			}

			Log.Recovered($"webtoken:{_bot.Name}");
			Init(_bot.SteamId, token);
			parentalNeeded = !string.IsNullOrEmpty(_bot.Cfg.SteamParentalCode);
		} finally {
			_refreshLock.Release();
		}

		// OUTSIDE the lock, and on a path that can't re-enter it. Unlocking runs HTTP requests, and an HTTP
		// request whose session looks stale calls RefreshAsync - which would then wait on the semaphore this
		// same call stack is holding, and the account would hang here forever.
		if (parentalNeeded) {
			await UnlockParentalAsync(ct).ConfigureAwait(false);
		}

		return true;
	}

	/// <summary>Unlock Steam Family View on both the community and store hosts, so pages stop redirecting.</summary>
	private async Task UnlockParentalAsync(CancellationToken ct) {
		foreach (Uri service in new[] { Community, Store }) {
			try {
				Dictionary<string, string> form = new(StringComparer.Ordinal) { ["pin"] = _bot.Cfg.SteamParentalCode };
				// skipReadyCheck + no retry: this call must never re-enter RefreshAsync.
				string? body = await SendAsync(new Uri(service, "/parental/ajaxunlock"), form, service, false, ct, true).ConfigureAwait(false);

				if (body == null || body.Contains("\"success\":false", StringComparison.Ordinal)) {
					Log.Warn(new Said("{0} rejected the Family View PIN - farming may see nothing", service.Host), _bot.Name);
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("parental unlock on {0}: {1}", service.Host, Log.Describe(e)), _bot.Name);
			}
		}
	}

	/// <summary>Read the "exp" claim of any Steam JWT - used by the Bot to decide when its cached token is spent.</summary>
	public static DateTime? ReadJwtExpiryOf(string jwt) => ReadJwtExpiry(jwt);

	/// <summary>Read the "exp" claim without a JWT library - it's a base64url JSON payload between two dots.</summary>
	private static DateTime? ReadJwtExpiry(string jwt) {
		try {
			string[] parts = jwt.Split('.');

			if (parts.Length < 2) {
				return null;
			}

			string payload = parts[1].Replace('-', '+').Replace('_', '/');
			payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

			string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
			int at = json.IndexOf("\"exp\"", StringComparison.Ordinal);

			if (at < 0) {
				return null;
			}

			int colon = json.IndexOf(':', at);
			int i = colon + 1;

			while ((i < json.Length) && !char.IsAsciiDigit(json[i])) {
				i++;
			}

			int start = i;

			while ((i < json.Length) && char.IsAsciiDigit(json[i])) {
				i++;
			}

			return long.TryParse(json.AsSpan(start, i - start), out long unix)
				? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
				: null;
		} catch {
			return null;   // a token we can't read still works; we just refresh reactively instead
		}
	}

	public void Dispose() {
		_http.Dispose();
		_refreshLock.Dispose();
	}
}
