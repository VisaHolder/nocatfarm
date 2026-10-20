using System.Net;

namespace NocatFarm.Core;

public sealed partial class WebSession {
	/// <summary>The account's own client, but one that never follows a redirect - made the first time it's needed.</summary>
	private HttpClient? _noRedirect;

	private readonly Lock _noRedirectGate = new();

	/// <summary>
	/// 1.8.2 card swaps: POST a form to Steam as this account and hand back where Steam sends it, without going there.
	/// Signing in to nocat.lol through Steam (OpenID) ends in such a redirect, and its address IS Steam's answer. The
	/// account's own cookies and proxy. No redirect: the status Steam answered with (0 for no answer) and its page, so the
	/// caller can say why - kept in memory only, never logged.
	/// </summary>
	internal async Task<Redirected> PostForRedirectAsync(Uri url, IEnumerable<KeyValuePair<string, string>> form, Uri? referer, CancellationToken ct) {
		if (NeedsRefresh && !await RefreshAsync(!Ready, ct).ConfigureAwait(false)) {
			return new Redirected(null, 0, null);
		}

		HttpClient http;

		lock (_noRedirectGate) {
			http = _noRedirect ??= NoRedirectClient();
		}

		try {
			using HttpResponseMessage? response = await Limiters.WebAsync(url.Host, async () => {
				using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = Multipart(form) };

				if (referer != null) {
					request.Headers.Referrer = referer;
				}

				return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			}).ConfigureAwait(false);

			if (response == null) {
				return new Redirected(null, 0, null);
			}

			if (((int) response.StatusCode is >= 300 and < 400) && (response.Headers.Location is { } where)) {
				return new Redirected(where.IsAbsoluteUri ? where : new Uri(url, where), (int) response.StatusCode, null);
			}

			Log.Debug($"POST {url.AbsolutePath} -> {(int) response.StatusCode}, not sent on anywhere", _bot.Name);

			return new Redirected(null, (int) response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"POST {url.AbsolutePath} failed: {Log.Describe(e)}", _bot.Name);

			return new Redirected(null, 0, null);
		}
	}

	/// <summary>What a form post came to: where Steam sent it (null when nowhere), the status, and the page when it didn't redirect.</summary>
	public readonly record struct Redirected(Uri? To, int Status, string? Page);

	/// <summary>
	/// The form as multipart/form-data - how Steam's own sign-in page posts it. Sent url-encoded, Steam's OpenID login only
	/// showed the page again (live, 2026-10-14, both test accounts); ASF's Sign in with Steam posts it this way too.
	/// </summary>
	public static MultipartFormDataContent Multipart(IEnumerable<KeyValuePair<string, string>> form) {
		MultipartFormDataContent content = [];

		foreach ((string key, string value) in form) {
			content.Add(new StringContent(value), key);
		}

		return content;
	}

	private HttpClient NoRedirectClient() {
		HttpClientHandler handler = Core.Bot.BuildProxyHandler(_bot.Cfg);
		handler.CookieContainer = _cookies;
		handler.UseCookies = true;
		handler.AllowAutoRedirect = false;
		handler.AutomaticDecompression = DecompressionMethods.All;

		HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Browser.UserAgent);

		return http;
	}
}
