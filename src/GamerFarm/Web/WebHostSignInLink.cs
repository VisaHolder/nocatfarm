using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GamerFarm.Core;

namespace GamerFarm.Web;

/// <summary>
/// 2.0.2: one-time sign-in links. Something already signed in - a dashboard session, or a program holding one as a Bearer
/// token (a home server's own dashboard) - asks POST /api/signin-link, and a browser opening /?signin=&lt;code&gt; gets the
/// same session a right password from its address would get, with no password typed. 'dashboard link' makes one from the
/// console, Telegram and Discord.
///
/// A code is 256 random bits, works once, for 60 seconds, and only a few wait at once. A wrong, used or run-out one counts
/// towards the address's lockout like a wrong password. From outside the home it follows the password's rules: shut, paused or
/// locked, it doesn't open; with "Code on Telegram for sign-ins from outside" on, the Telegram code is still asked for - the
/// link stands in for the password, never for the code (see <see cref="SignInByLinkAsync"/>).
/// </summary>
public sealed partial class WebHost {
	internal const int SignInLinkSeconds = 60;

	/// <summary>Links waiting to be opened at once; a new one past this drops the oldest.</summary>
	internal const int SignInLinksKept = 5;

	/// <summary>One link: a hash of its code (the code itself is never kept), when it runs out, and the password it was made under.</summary>
	private sealed record SignInLink(byte[] Hash, DateTime Expires, string Key);

	private readonly List<SignInLink> _signInLinks = [];
	private readonly Lock _signInLinksGate = new();

	private void MapSignInLink(WebApplication app) {
		// Only for whoever is already signed in here - never a page paired at farm.gamer.mom (its token isn't a dashboard
		// session, and the front door there refuses this route too: FarmWeb.Routes).
		app.MapPost("/api/signin-link", (HttpContext ctx) => {
			if (IsFarm(ctx) || !Authorised(ctx)) {
				return Unauthorised();
			}

			string code = NewSignInCode();

			return Results.Json(new { code, path = "/?signin=" + code, expiresIn = SignInLinkSeconds });
		});
	}

	/// <summary>A fresh code for a sign-in link: 32 random bytes, URL-safe. Works once, for <see cref="SignInLinkSeconds"/>.</summary>
	public string NewSignInCode() {
		string code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
		DateTime now = DateTime.UtcNow;

		lock (_signInLinksGate) {
			_signInLinks.RemoveAll(l => l.Expires <= now);

			while (_signInLinks.Count >= SignInLinksKept) {
				_signInLinks.RemoveAt(0);
			}

			_signInLinks.Add(new SignInLink(CodeHash(code), now.AddSeconds(SignInLinkSeconds), PasswordKey));
		}

		return code;
	}

	/// <summary>
	/// 'dashboard link': a fresh link for each address the dashboard opens at - at home, from outside, on this PC - as the
	/// 'dashboard' command lists them. One code for all of them: whichever is opened first uses it.
	/// </summary>
	public string SignInLinkText() {
		DashboardLinks.Links l = DashboardLinks.For(_cfg);
		string code = NewSignInCode();
		string Add(string url) => url.TrimEnd('/') + "/?signin=" + code;
		List<string> lines = [new Said("A sign-in link for the dashboard - it works once, for {0} seconds:", SignInLinkSeconds).ToEnglish()];

		if (l.ReadyAtHome) {
			lines.Add($"  {new Said("At home (same Wi-Fi):").ToEnglish()} {Add(l.Home[0])}");
		}

		if (l.Outside != null) {
			lines.Add($"  {new Said("Away from home:").ToEnglish()} {Add(l.Outside)}");
		}

		lines.Add($"  {new Said("On the PC itself:").ToEnglish()} {Add(l.Local)}");

		if (string.IsNullOrEmpty(_cfg.WebPassword)) {
			lines.Add("  " + new Said("There's no dashboard password, so it opens on this PC without one anyway.").ToEnglish());
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>Every link not opened yet, gone - with "sign out everywhere" and a changed password.</summary>
	private void ForgetSignInLinks() {
		lock (_signInLinksGate) {
			_signInLinks.Clear();
		}
	}

	private static byte[] CodeHash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

	/// <summary>
	/// The link this code belongs to, taken away so it never works again - or null when it's wrong, used or run out. Every
	/// waiting link is compared, each in constant time (of the hashes, so the length typed says nothing either).
	/// </summary>
	private SignInLink? TakeSignInCode(string typed) {
		byte[] hash = CodeHash(typed);
		DateTime now = DateTime.UtcNow;

		lock (_signInLinksGate) {
			SignInLink? found = null;

			foreach (SignInLink l in _signInLinks) {
				if (CryptographicOperations.FixedTimeEquals(l.Hash, hash)) {
					found = l;
				}
			}

			if (found != null) {
				_signInLinks.Remove(found);
			}

			_signInLinks.RemoveAll(l => l.Expires <= now);

			return (found != null) && (found.Expires > now) ? found : null;
		}
	}

	/// <summary>A GET of the page itself with ?signin= on it.</summary>
	private static bool IsSignInLink(HttpRequest request) =>
		(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
		&& request.Path.Value is "/" or "/index.html"
		&& request.Query.ContainsKey("signin");

	/// <summary>
	/// /?signin=&lt;code&gt;. Right: the session a right password from this address gets (cookie and all), then a redirect to /,
	/// so the code is in neither the address bar nor the history. Wrong, used or run out: the sign-in page, saying so, and
	/// one more wrong guess towards the lockout.
	///
	/// From outside the home it's the password's rules, all of them - nothing a link opens that the password wouldn't. Open from
	/// anywhere off, or shut after too many wrong guesses: turned away before this (Refusal), as the password is. Locked out
	/// or the internet brake on: not even looked at (Reserve). And with "Code on Telegram for sign-ins from outside" on, the
	/// right link gets as far as the right password does - the code goes to Telegram and the page asks for it. Whoever made
	/// the link was signed in, but a link is something anyone holding it can open from anywhere, like a password; the code
	/// is what makes "only the owner, from outside" true, and the design is that knowing something never skips it.
	/// </summary>
	private async Task SignInByLinkAsync(HttpContext ctx) {
		HttpResponse response = ctx.Response;
		response.Headers.CacheControl = "no-store";

		// A HEAD (a link checker, a preview) neither uses the code nor counts against anyone.
		if (HttpMethods.IsHead(ctx.Request.Method)) {
			response.ContentType = "text/html; charset=utf-8";

			return;
		}

		string typed = ctx.Request.Query["signin"].ToString();
		(string ip, bool thisPc) = WhoIsSigningIn(ctx);
		bool internet = !thisPc && IsInternet(ip);
		string? device = ctx.Request.Headers.UserAgent.FirstOrDefault();

		if (internet && InternetShut()) {
			await SignInPageAsync(ctx, "closed").ConfigureAwait(false);

			return;
		}

		// Its place in the lockout and the brake, taken before the code is looked at - as for a password.
		if (Reserve(ip, thisPc, internet, device) is not { } attempt) {
			await SignInPageAsync(ctx, $"locked:{Math.Max(1, LockedFor(ctx))}:{(thisPc ? 1 : 0)}").ConfigureAwait(false);

			return;
		}

		if (TakeSignInCode(typed) is not { } link) {
			CountFailure(attempt, Visitors.What.WrongCode, link: true);

			string why = ShutToThis(ctx) ? "closed" : LockedFor(ctx) is > 0 and int wait ? $"locked:{wait}:{(thisPc ? 1 : 0)}" : "expired";
			await SignInPageAsync(ctx, why).ConfigureAwait(false);

			return;
		}

		if (NeedsCode(internet)) {
			GiveBack(attempt);
			string? challenge = await SendCodeAsync(ip, thisPc, device, link.Key, ctx.RequestAborted, link: true).ConfigureAwait(false);
			await SignInPageAsync(ctx, challenge == null ? "nocode" : $"code:{challenge}:{CodeMinutes}").ConfigureAwait(false);

			return;
		}

		// Under the password the link was made under: changed since, it signs nobody in.
		if (!SignIn(ctx, attempt, link.Key, out _, link: true)) {
			await SignInPageAsync(ctx, "expired").ConfigureAwait(false);

			return;
		}

		response.Redirect("/");
	}

	/// <summary>
	/// The sign-in page as it always is, with what the link came to on it (data-signin on &lt;html&gt;) for the page to say -
	/// it takes the code off the address too.
	/// </summary>
	private static async Task SignInPageAsync(HttpContext ctx, string said) {
		string index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
		string html = File.Exists(index) ? await File.ReadAllTextAsync(index, ctx.RequestAborted).ConfigureAwait(false) : "<!DOCTYPE html>\n<html>\n<body></body>\n</html>\n";
		int at = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);

		if (at >= 0) {
			html = html.Insert(at + "<html".Length, $" data-signin=\"{System.Net.WebUtility.HtmlEncode(said)}\"");
		}

		ctx.Response.ContentType = "text/html; charset=utf-8";
		await ctx.Response.WriteAsync(html, ctx.RequestAborted).ConfigureAwait(false);
	}
}
