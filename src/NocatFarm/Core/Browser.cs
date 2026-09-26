using System.Net;

namespace NocatFarm.Core;

/// <summary>How this app presents itself to Steam's web servers - one identity, kept in one place.</summary>
/// <remarks>
/// Steam put a web application firewall in front of the community site in July 2026, and ArchiSteamFarm had to
/// start dressing up as a browser for inventories, trade offers and confirmations. Every account request here
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
}
