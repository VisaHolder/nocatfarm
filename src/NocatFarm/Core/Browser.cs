using System.Net;

namespace NocatFarm.Core;

/// <summary>How this app presents itself to Steam's web servers - one identity, kept in one place.</summary>
/// <remarks>
/// Steam put a web application firewall in front of the community site in July 2026, and anything that didn't look
/// like a browser started failing on inventories, trade offers and confirmations. Every account request here
/// already carried a browser User-Agent, which is why none of those broke. But five of the app's HTTP clients sent
/// no User-Agent at all - the market price lookups among them, on the very domain behind that firewall - and the
/// one browser string there was claimed a two-year-old Chrome, written the way no Chrome writes it: "Chrome/126.0",
/// where the real thing always sends four parts. Neither is a good thing to hand a firewall.
///
/// When the version ages, change it here. Nothing else needs to know.
/// </remarks>
public static class Browser {
	public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

	/// <summary>
	/// A client for Steam's public pages - prices, store details, global rarity - where no account is involved.
	/// </summary>
	/// <remarks>
	/// English is asked for explicitly because these answers are read by code: a store page or a price answered in
	/// the machine's own language would be text the parsers were never written for. Compression because a browser
	/// always offers it, and a client that never does stands out.
	/// </remarks>
	public static HttpClient Anonymous(TimeSpan timeout) {
		HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = timeout };
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
		http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

		return http;
	}

	/// <summary>
	/// The client for market prices - priceoverview and search pages, signed out - under the app's own name.
	/// </summary>
	/// <remarks>
	/// The market turned the browser string above away: with it, every price lookup and every search page from this
	/// app came back 429, the first request after every pause included, while the same address asking the same thing
	/// as "nocat.farm/1.7.0", as curl, or as an older Chrome got its answer each time (2026-10-05, 6 of 6 and 12 of 12).
	/// That is why one inventory sat at "931 still to price" for a whole day: each pause ended in another 429 and a
	/// longer pause. The rest of the community site is fine with the browser string, and the firewall there is why
	/// <see cref="UserAgent"/> stays for everything else.
	///
	/// The name alone wasn't it: a request with no Accept header at all - which is what HttpClient sends unless told -
	/// was refused every time whatever it called itself (4 of 4), and the same request with one answered (4 of 4).
	/// Every browser and every tool sends one; a market request without it is what a bot looks like.
	/// </remarks>
	public static HttpClient Market(TimeSpan timeout) {
		HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = timeout };
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", MarketAgent);
		http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
		http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

		return http;
	}

	/// <summary>The name market requests go under: the app and its version, nothing pretended.</summary>
	public static string MarketAgent => "nocat.farm/" + Build.Version;

	/// <summary>
	/// The second way of asking the market: as a desktop Firefox would - the ESR that is current, never a version that
	/// isn't out yet - with the headers one sends. The price book turns to it when <see cref="Market"/> is turned down.
	/// </summary>
	/// <remarks>
	/// Two ways of asking because the market's rules change and nothing announces it: a browser string was fine until
	/// it wasn't, a missing Accept header was fine until it wasn't. When one way is refused the next request goes the
	/// other way, and whichever last got an answer is the one used. Both carry an Accept header - the one thing every
	/// refused request had in common. Live, 2026-10-05: this one answered 4 of 4, priceoverview and search pages both.
	/// Accept-Encoding is spelled out to match what the handler decodes (no zstd, which it can't).
	/// </remarks>
	public static HttpClient MarketAsBrowser(TimeSpan timeout) {
		HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = timeout };
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", MarketBrowserAgent);
		http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
		http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");
		http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

		return http;
	}

	/// <summary>Firefox 140 ESR on Windows, written exactly as it writes itself.</summary>
	public const string MarketBrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";
}
