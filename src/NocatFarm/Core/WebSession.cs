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
		_http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
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
		if (!Ready && !await RefreshAsync(false, ct).ConfigureAwait(false)) {
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

	public void Invalidate() => Ready = false;

	// ── requests ────────────────────────────────────────────────────────────
	public async Task<string?> GetAsync(Uri url, CancellationToken ct = default) => await SendAsync(url, null, null, true, ct).ConfigureAwait(false);

	/// <summary>
	/// POST a form. The sessionid is injected from the cookie jar per request rather than cached, so if Steam ever
	/// replaces the cookie the body and the cookie stay in agreement automatically.
	/// </summary>
	public async Task<string?> PostAsync(Uri url, Dictionary<string, string> form, Uri? referer = null, CancellationToken ct = default) =>
		await SendAsync(url, form, referer, true, ct).ConfigureAwait(false);

	private async Task<string?> SendAsync(Uri url, Dictionary<string, string>? form, Uri? referer, bool allowRetry, CancellationToken ct, bool skipReadyCheck = false) {
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
						return null;
					}

					// The CSRF token is the sessionid, but the field casing Steam accepts is not consistent across
					// the community site: the comment endpoints read "sessionid", while the group-join endpoint reads
					// only "sessionID" and answers a lowercase-only POST with a 200 "invalid form session key" error
					// page that looks just like success. Sending both - the same value - satisfies either, so a caller
					// never has to know which a given endpoint wants.
					Dictionary<string, string> payload = new(form, StringComparer.Ordinal) {
						["sessionid"] = cookieSession,
						["sessionID"] = cookieSession
					};
					request.Content = new FormUrlEncodedContent(payload);
				}

				if (referer != null) {
					request.Headers.Referrer = referer;
				}

				return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
			}).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			Uri final = response.RequestMessage?.RequestUri ?? url;

			// Steam redirects an expired session to the login page instead of failing the request.
			if (IsSessionExpired(final)) {
				Ready = false;

				if (!allowRetry) {
					return null;
				}

				Log.Debug("web session expired - re-minting the access token", _bot.Name);

				if (!await RefreshAsync(true, ct).ConfigureAwait(false)) {
					return null;
				}

				return await SendAsync(url, form, referer, false, ct).ConfigureAwait(false);
			}

			if (!response.IsSuccessStatusCode) {
				// Steam puts the REASON in the body of a failed POST - "you cannot trade because...", "this
				// account is trade banned" - and throwing it away left every failure looking like a network
				// fault. The first couple of hundred characters is always enough to say what went wrong.
				string failure = "";

				try {
					failure = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
				} catch {
					// the status code on its own will have to do
				}

				Log.Debug($"{(form == null ? "GET" : "POST")} {url.PathAndQuery} -> {(int) response.StatusCode}"
					+ (failure.Length > 0 ? $"  {failure[..Math.Min(300, failure.Length)]}" : ""), _bot.Name);

				return null;
			}

			body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;   // genuinely shutting down
		} catch (OperationCanceledException e) {
			// HttpClient reports its own 30s timeout as a cancellation with nobody having cancelled anything.
			// Rethrowing that killed the calling module's loop outright and looked exactly like a clean shutdown.
			Log.Debug($"{(form == null ? "GET" : "POST")} {url.PathAndQuery} timed out: {e.Message}", _bot.Name);

			return null;
		} catch (Exception e) {
			Log.Debug($"{(form == null ? "GET" : "POST")} {url.PathAndQuery} failed: {e.Message}", _bot.Name);

			return null;
		} finally {
			response?.Dispose();
		}

		return body;
	}

	/// <summary>Same as <see cref="PostAsync"/> but reports the transport outcome separately from the body.</summary>
	public async Task<(bool Reached, string? Body)> PostRawAsync(Uri url, Dictionary<string, string> form, Uri? referer = null, CancellationToken ct = default) {
		string? body = await PostAsync(url, form, referer, ct).ConfigureAwait(false);

		return (body != null, body);
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
			return await Limiters.WebAsync(url.Host, async () => {
				using HttpRequestMessage request = new(HttpMethod.Post, url);
				string? cookieSession = CookieValue(url, "sessionid");

				if (string.IsNullOrEmpty(cookieSession)) {
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

				return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			}).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"POST {url.PathAndQuery} failed: {e.Message}", _bot.Name);

			return null;
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
	public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default) {
		bool parentalNeeded;

		await _refreshLock.WaitAsync(ct).ConfigureAwait(false);

		try {
			if (!force && Ready && (TokenValidUntil == null || TokenValidUntil > DateTime.UtcNow.AddMinutes(5))) {
				return true;
			}

			string? token = await _bot.GetAccessTokenAsync().ConfigureAwait(false);

			if (string.IsNullOrEmpty(token)) {
				Ready = false;

				return false;
			}

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
					Log.Warn($"Family View PIN wasn't accepted by {service.Host} - card farming may see nothing", _bot.Name);
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Debug($"parental unlock on {service.Host}: {e.Message}", _bot.Name);
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
