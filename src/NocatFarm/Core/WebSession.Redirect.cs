using System.Net;

namespace NocatFarm.Core;

public sealed partial class WebSession {
	/// <summary>The account's own client, but one that never follows a redirect - made the first time it's needed.</summary>
	private HttpClient? _noRedirect;

	private readonly Lock _noRedirectGate = new();

	/// <summary>
	/// 1.8.2 card swaps: POST a form to Steam as this account and hand back where Steam sends it, without going there.
	/// Signing in to nocat.lol through Steam (OpenID) ends in such a redirect, and its address IS Steam's answer. The
	/// account's own cookies and proxy; null when Steam didn't redirect, or didn't answer.
	/// </summary>
	internal async Task<Uri?> PostForRedirectAsync(Uri url, IEnumerable<KeyValuePair<string, string>> form, Uri? referer, CancellationToken ct) {
		if (NeedsRefresh && !await RefreshAsync(!Ready, ct).ConfigureAwait(false)) {
			return null;
		}

		HttpClient http;

		lock (_noRedirectGate) {
			http = _noRedirect ??= NoRedirectClient();
		}

		try {
			using HttpResponseMessage? response = await Limiters.WebAsync(url.Host, async () => {
				using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };

				if (referer != null) {
					request.Headers.Referrer = referer;
				}

				return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			}).ConfigureAwait(false);

			if (response == null) {
				return null;
			}

			if (((int) response.StatusCode is >= 300 and < 400) && (response.Headers.Location is { } where)) {
				return where.IsAbsoluteUri ? where : new Uri(url, where);
			}

			Log.Debug($"POST {url.AbsolutePath} -> {(int) response.StatusCode}, not sent on anywhere", _bot.Name);

			return null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"POST {url.AbsolutePath} failed: {Log.Describe(e)}", _bot.Name);

			return null;
		}
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
