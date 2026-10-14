using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2 card swaps: proving to nocat.lol that this copy is signed in to an account, so that nobody else can put that
/// account on a list - nocat.lol pairs no account without it. The account signs in to nocat.lol through Steam ("Sign in
/// through Steam", OpenID 2.0) with its own web session, the way a person signs in to a trading site, and what Steam hands
/// back goes to nocat.lol with the list, which has Steam check it.
/// </summary>
/// <remarks>
/// Steam's answer only says "this is SteamID X, signed in just now, for this address". It can't sign in, trade or do
/// anything else, and it's made out to this install's own address on nocat.lol (the hash of the install's random number,
/// and the minute), so it's of no use to anybody else, or an hour later. No password, login token or cookie leaves this
/// PC. A proof lasts 30 days on nocat.lol; this copy sends a new one after 20, or when nocat.lol says it has none.
/// </remarks>
public static partial class CardExchange {
	/// <summary>Where Steam's answer is made out to - nocat.lol never needs to be visited there, the answer is the address.</summary>
	public const string ProofUrl = "https://nocat.lol/api/farm/swap/proof";

	private static readonly Uri OpenIdLogin = new("https://steamcommunity.com/openid/login");

	/// <summary>A proof is sent again after this long - nocat.lol keeps one for 30 days.</summary>
	public static readonly TimeSpan ProofFor = TimeSpan.FromDays(20);

	/// <summary>Test seam: proving an account, instead of asking Steam (null: Steam itself).</summary>
	internal static Func<Bot, CancellationToken, Task<string?>>? ProveSeam { get; set; }

	/// <summary>The install's random number as nocat.lol keeps it: the first 16 hex of its SHA-256 - never the number itself.</summary>
	public static string InstallHash(string installId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("nocat.farm swaps " + installId)))[..16];

	/// <summary>Steam's answer to this account signing in to nocat.lol, as the query nocat.lol checks - null when it couldn't be had.</summary>
	public static async Task<string?> ProveAsync(Bot bot, CancellationToken ct) {
		if (!bot.IsOnline || !bot.Web.Ready || (bot.SteamId == 0)) {
			return null;
		}

		string? proof = await ProofFromAsync((u, c) => bot.Web.GetAsync(u, c), (u, form, c) => bot.Web.PostForRedirectAsync(u, form, u, c),
			bot.SteamId, InstallHash(UserCount.InstallId()), DateTime.UtcNow, ct).ConfigureAwait(false);

		if (proof == null) {
			Log.DebugOnChange($"swapproof:{bot.Name}", "card swaps: couldn't sign in to nocat.lol through Steam - not paired until it can", bot.Name);
		}

		return proof;
	}

	/// <summary>
	/// Signing in through Steam: Steam's sign-in page for nocat.lol (signed in already, it's one form), the form posted back
	/// as Steam made it, and Steam's answer - where it sends the browser - checked to be for this account and this install.
	/// </summary>
	/// <param name="get">A page from Steam, signed in as the account.</param>
	/// <param name="post">A form posted to Steam, signed in as the account: where Steam redirects to, without going there.</param>
	public static async Task<string?> ProofFromAsync(Func<Uri, CancellationToken, Task<string?>> get, Func<Uri, IReadOnlyList<KeyValuePair<string, string>>, CancellationToken, Task<Uri?>> post,
		ulong steamId, string installHash, DateTime nowUtc, CancellationToken ct) {
		long minute = new DateTimeOffset(nowUtc.ToUniversalTime()).ToUnixTimeSeconds() / 60;
		string returnTo = string.Create(CultureInfo.InvariantCulture, $"{ProofUrl}?i={installHash}&t={minute}");

		static string Q(string k, string v) => Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v);

		Uri start = new(OpenIdLogin + "?" + string.Join("&", Q("openid.ns", "http://specs.openid.net/auth/2.0"), Q("openid.mode", "checkid_setup"),
			Q("openid.return_to", returnTo), Q("openid.realm", "https://nocat.lol/"),
			Q("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"), Q("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select")));

		// Not signed in, Steam's page changed: no form, no proof.
		if ((await get(start, ct).ConfigureAwait(false) is not { } page) || (OpenIdForm(page) is not { } form)) {
			return null;
		}

		if (await post(OpenIdLogin, form, ct).ConfigureAwait(false) is not { } back) {
			return null;
		}

		// Only an answer made out to this install's address, saying it's this very account.
		if (!back.AbsoluteUri.StartsWith(ProofUrl + "?i=" + installHash + "&", StringComparison.Ordinal)) {
			return null;
		}

		string query = back.Query.TrimStart('?');
		Dictionary<string, string> fields = QueryFields(query);

		return (fields.GetValueOrDefault("openid.mode") == "id_res")
			&& (fields.GetValueOrDefault("openid.claimed_id") == "https://steamcommunity.com/openid/id/" + steamId.ToString(CultureInfo.InvariantCulture))
			&& (query.Length <= 2048) ? query : null;
	}

	/// <summary>The hidden fields of Steam's sign-in form (id="openidForm"), as Steam wrote them; null when there's no such form.</summary>
	private static List<KeyValuePair<string, string>>? OpenIdForm(string page) {
		int at = page.IndexOf("id=\"openidForm\"", StringComparison.Ordinal);

		if (at < 0) {
			return null;
		}

		int end = page.IndexOf("</form>", at, StringComparison.OrdinalIgnoreCase);
		string form = end < 0 ? page[at..] : page[at..end];
		List<KeyValuePair<string, string>> fields = [];

		foreach (System.Text.RegularExpressions.Match input in Regex.Matches(form, "<input\\b[^>]*>", RegexOptions.IgnoreCase)) {
			if (!Regex.IsMatch(input.Value, "\\btype\\s*=\\s*\"hidden\"", RegexOptions.IgnoreCase) || (Attr(input.Value, "name") is not { Length: > 0 } name)) {
				continue;
			}

			fields.Add(new KeyValuePair<string, string>(name, WebUtility.HtmlDecode(Attr(input.Value, "value") ?? "")));
		}

		return fields.Any(static f => f.Key == "openidparams") && fields.Any(static f => f.Key == "nonce") ? fields : null;
	}

	private static string? Attr(string tag, string name) =>
		Regex.Match(tag, "\\s" + name + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

	private static Dictionary<string, string> QueryFields(string query) {
		Dictionary<string, string> fields = new(StringComparer.Ordinal);

		foreach (string part in query.Split('&')) {
			int eq = part.IndexOf('=');

			if (eq > 0) {
				fields.TryAdd(Uri.UnescapeDataString(part[..eq].Replace('+', ' ')), Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')));
			}
		}

		return fields;
	}
}
