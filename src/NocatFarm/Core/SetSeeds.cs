using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NocatFarm.Core;

/// <summary>
/// Public "cheapest sets" lists, read once a day only to say which games the set finder (<see cref="SetFinder"/>) reads
/// first. Their prices are never kept or trusted: only each game's id goes on (in the list's order), and the finder prices
/// every card on the Steam market itself before a set is known, ranked or bought.
/// </summary>
/// <remarks>
/// Polite by rule: each site's robots.txt is read first and followed (any answer but a 200 there, and the site is left
/// alone that day); at most once a day; under an honest name, "nocat.farm set finder"; and a site that doesn't answer,
/// answers anything but a 200, or sends a list that can't be read reliably is skipped with one debug line - the finder
/// carries on by itself. Checked October 2026:
/// <list type="bullet">
/// <item>steamcardexchange.net: robots.txt only turns two SEO crawlers away. Its badge price list comes from
/// /api/request.php?GetBadgePrices_Guest (every game: its id, name, cards in the set, a price and whether it's on the
/// market).</item>
/// <item>steam.tools: its set list comes from cdn.steam.tools/data/set_data.json, and that host's robots.txt answers 403 -
/// so it's left alone (steam.tools' own robots.txt keeps crawlers off /cards/ pages too).</item>
/// </list>
/// </remarks>
public static class SetSeeds {
	/// <summary>Games taken from the lists, at most - the cheapest sets in each.</summary>
	public const int MaxGames = 1000;

	/// <summary>Fewer games than this in a list and it isn't read reliably: skipped.</summary>
	public const int MinRows = 50;

	/// <summary>How often the lists are read, at most.</summary>
	public static readonly TimeSpan Every = TimeSpan.FromDays(1);

	/// <summary>What robots.txt calls this reader.</summary>
	public const string RobotName = "nocat.farm";

	/// <summary>The name it reads under.</summary>
	public static string Agent => $"nocat.farm set finder/{Build.Version}";

	/// <summary>A list: the site's name, where its list is, and how it's read - the games, the cheapest set first, at most so many.</summary>
	public sealed record Source(string Name, Uri List, Func<string, int, IReadOnlyList<uint>?> Parse);

	/// <summary>The lists it reads.</summary>
	public static readonly IReadOnlyList<Source> Sources = [
		new("steamcardexchange.net", new Uri("https://www.steamcardexchange.net/api/request.php?GetBadgePrices_Guest"), ParseCardExchange),
		new("steam.tools", new Uri("https://cdn.steam.tools/data/set_data.json"), ParseSteamTools)
	];

	/// <summary>One GET: the status (0 when it didn't answer at all) and the body when it's a 200.</summary>
	public delegate Task<(int Status, string? Body)> Fetch(Uri url, CancellationToken ct);

	private static readonly HttpClient Http = MakeHttp();

	private static HttpClient MakeHttp() {
		HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);

		return http;
	}

	/// <summary>The web, for real.</summary>
	public static async Task<(int Status, string? Body)> WebAsync(Uri url, CancellationToken ct) {
		try {
			using HttpResponseMessage r = await Http.GetAsync(url, ct).ConfigureAwait(false);

			return ((int) r.StatusCode, r.StatusCode == HttpStatusCode.OK ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) {
			return (0, null);
		}
	}

	/// <summary>
	/// Once a day: every list read politely, and the games handed to the set finder as the ones to read first. How many
	/// games it was given. A site that won't do is skipped with one debug line; nothing else is ever asked of it that day.
	/// </summary>
	public static async Task<int> ReadAsync(SetFinder finder, Fetch fetch, DateTime now, CancellationToken ct, IReadOnlyList<Source>? sources = null) {
		if (!finder.SeedsDue(now)) {
			return 0;
		}

		finder.SeedsRead(now);   // tried today, whatever comes of it - never asked again in a loop
		List<uint> games = [];
		List<string> from = [];

		foreach (Source s in sources ?? Sources) {
			(int status, string? robots) = await fetch(new Uri(s.List, "/robots.txt"), ct).ConfigureAwait(false);

			if ((status != 200) || (robots == null)) {
				Log.Debug(new Said("set finder · {0} skipped · its robots.txt didn't answer ({1})", s.Name, status));

				continue;
			}

			if (!Allowed(robots, RobotName, s.List.PathAndQuery)) {
				Log.Debug(new Said("set finder · {0} skipped · its robots.txt doesn't allow it", s.Name));

				continue;
			}

			(status, string? body) = await fetch(s.List, ct).ConfigureAwait(false);

			if ((status != 200) || (body == null)) {
				Log.Debug(new Said("set finder · {0} skipped · it didn't answer ({1})", s.Name, status));

				continue;
			}

			IReadOnlyList<uint>? list = null;

			try {
				list = s.Parse(body, MaxGames);
			} catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) {
				// Said below, like any list it can't read.
			}

			if ((list == null) || (list.Count < MinRows)) {
				Log.Debug(new Said("set finder · {0} skipped · its list couldn't be read", s.Name));

				continue;
			}

			games.AddRange(list);
			from.Add(s.Name);
		}

		if (games.Count == 0) {
			return 0;
		}

		int given = finder.Seed([.. games.Distinct().Take(MaxGames)], now);
		Log.Debug(new Said("set finder · {0} games to look at first, from {1}", given, string.Join(", ", from)));

		return given;
	}

	// ── robots.txt ───────────────────────────────────────────────────────────
	/// <summary>
	/// Whether robots.txt lets <paramref name="robot"/> read <paramref name="pathAndQuery"/>: the groups naming it (or, when
	/// none does, the "*" groups); the longest matching Allow or Disallow wins, Allow on a tie; * matches anything and a
	/// closing $ the end. Nothing matching: allowed.
	/// </summary>
	public static bool Allowed(string robots, string robot, string pathAndQuery) {
		List<(List<string> Agents, List<(bool Allow, string Pattern)> Rules)> groups = [];
		(List<string> Agents, List<(bool, string)> Rules)? current = null;
		bool lastWasAgent = false;

		foreach (string raw in robots.Split('\n')) {
			string line = (raw.Contains('#') ? raw[..raw.IndexOf('#')] : raw).Trim();
			int colon = line.IndexOf(':');

			if (colon <= 0) {
				continue;
			}

			string key = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim();

			if (key == "user-agent") {
				if ((current == null) || !lastWasAgent) {
					current = ([], []);
					groups.Add(current.Value);
				}

				current.Value.Agents.Add(value.ToLowerInvariant());
				lastWasAgent = true;
			} else if ((key is "allow" or "disallow") && (current != null)) {
				current.Value.Rules.Add((key == "allow", value));
				lastWasAgent = false;
			} else {
				lastWasAgent = false;
			}
		}

		string me = robot.ToLowerInvariant();
		List<(bool Allow, string Pattern)> rules = [.. groups.Where(g => g.Agents.Any(a => (a != "*") && me.StartsWith(a, StringComparison.Ordinal))).SelectMany(static g => g.Rules)];

		if (!groups.Any(g => g.Agents.Any(a => (a != "*") && me.StartsWith(a, StringComparison.Ordinal)))) {
			rules = [.. groups.Where(static g => g.Agents.Contains("*")).SelectMany(static g => g.Rules)];
		}

		(bool Allow, int Length)? best = null;

		foreach ((bool allow, string pattern) in rules) {
			if ((pattern.Length == 0) || !Matches(pattern, pathAndQuery)) {
				continue;   // "Disallow:" with nothing after it allows everything
			}

			if ((best == null) || (pattern.Length > best.Value.Length) || ((pattern.Length == best.Value.Length) && allow)) {
				best = (allow, pattern.Length);
			}
		}

		return best?.Allow ?? true;
	}

	private static bool Matches(string pattern, string path) {
		bool end = pattern.EndsWith('$');
		string body = end ? pattern[..^1] : pattern;
		string regex = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (end ? "$" : "");

		return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
	}

	// ── the lists ────────────────────────────────────────────────────────────
	/// <summary>Money as these lists write it ("$3.49", "0.48", "$1,234.56"): only to put the list in order. Null when it isn't.</summary>
	private static decimal? Price(string? text) =>
		decimal.TryParse((text ?? "").Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) && (d > 0) ? d : null;

	/// <summary>
	/// steamcardexchange.net's badge price list: {"data":[[["603750","Name"],5,"$3.49",1792385898,1],...]} - the game, cards in
	/// its set, a price, when, and 1 when it's on the market. Games on the market, the cheapest first; null when it isn't one.
	/// </summary>
	public static IReadOnlyList<uint>? ParseCardExchange(string json, int max) {
		using JsonDocument doc = JsonDocument.Parse(json);

		if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Array)) {
			return null;
		}

		List<(uint App, decimal Price)> rows = [];

		foreach (JsonElement r in data.EnumerateArray()) {
			if ((r.ValueKind != JsonValueKind.Array) || (r.GetArrayLength() < 5) || (r[0].ValueKind != JsonValueKind.Array) || (r[0].GetArrayLength() < 1)) {
				continue;
			}

			bool stocked = r[4].ValueKind == JsonValueKind.Number && r[4].TryGetInt32(out int s) && (s == 1);

			if (stocked && uint.TryParse(r[0][0].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app > 0)
				&& (r[2].ValueKind == JsonValueKind.String) && (Price(r[2].GetString()) is decimal price)) {
				rows.Add((app, price));
			}
		}

		return [.. rows.OrderBy(static r => r.Price).ThenBy(static r => r.App).Select(static r => r.App).Distinct().Take(max)];
	}

	/// <summary>
	/// steam.tools' set list: {"sets":[{"appid":"221540","normal":{"count":9,"price":"0.48",...},...}]}. The cheapest normal
	/// sets first; null when it isn't one.
	/// </summary>
	public static IReadOnlyList<uint>? ParseSteamTools(string json, int max) {
		using JsonDocument doc = JsonDocument.Parse(json);

		if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("sets", out JsonElement sets) || (sets.ValueKind != JsonValueKind.Array)) {
			return null;
		}

		List<(uint App, decimal Price)> rows = [];

		foreach (JsonElement s in sets.EnumerateArray()) {
			if ((s.ValueKind == JsonValueKind.Object) && s.TryGetProperty("appid", out JsonElement a)
				&& uint.TryParse(a.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app > 0)
				&& s.TryGetProperty("normal", out JsonElement n) && (n.ValueKind == JsonValueKind.Object) && n.TryGetProperty("price", out JsonElement p)
				&& (Price(p.ToString()) is decimal price)) {
				rows.Add((app, price));
			}
		}

		return [.. rows.OrderBy(static r => r.Price).ThenBy(static r => r.App).Select(static r => r.App).Distinct().Take(max)];
	}
}
