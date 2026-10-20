using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The shared cheapest-sets list (1.8.2): every copy whose set finder is looking sends nocat.lol the whole sets it knows,
/// and takes back the cheapest ones every copy has seen - so each copy knows far more cheap sets than its own slow walk.
/// </summary>
/// <remarks>
/// What goes out is market prices only: each set's game id, its cards' names, each card's cheapest listing in US cents,
/// how many are listed and how many seconds ago it was seen - plus this install's random number (the user count's, see
/// <see cref="UserCount.InstallId"/>) and the app version. Nothing about any account. Only while "Count me as a user" and
/// "Share cheapest sets with other nocat.farm users" are both on; either off sends and fetches nothing.
///
/// What comes back is only ever a candidate (<see cref="SetFinder.Merge"/>): which games the finder reads first. Every
/// card is priced again on the real Steam market before a set is known, ranked or bought, so a wrong price there costs a
/// market read, never money. Gentle: one send and one fetch every 6 hours or so, while the finder is looking; a send only
/// when it knows something new (or a day has passed). No answer is one debug line and the next try is hours away.
/// </remarks>
public static class SetShare {
	public const string Endpoint = "https://nocat.lol/api/farm/sets";

	/// <summary>Between two goes (a send and a fetch).</summary>
	public static readonly TimeSpan Every = TimeSpan.FromHours(6);

	/// <summary>The same sets are sent again after this, so nocat.lol's copy of them doesn't age out.</summary>
	public static readonly TimeSpan SendAgainAfter = TimeSpan.FromHours(24);

	/// <summary>Sets sent at most - the cheapest. nocat.lol takes 300.</summary>
	public const int MaxSets = 300;

	/// <summary>Sets taken from the shared list at most.</summary>
	public const int MaxTaken = 2000;

	/// <summary>A Steam set is 5 to 15 cards.</summary>
	public const int MinCards = 5, MaxCards = 15;

	/// <summary>Whether this copy shares: "Count me as a user" and "Share cheapest sets with other nocat.farm users".</summary>
	public static bool Sharing(GlobalConfig g) => g.CountMeAsUser && g.ShareCheapestSets;

	/// <summary>
	/// Where it really goes: <see cref="Endpoint"/>, or NOCATFARM_SETS_URL when that points at this PC (a test stand-in).
	/// Anything else is ignored.
	/// </summary>
	public static string Target {
		get {
			string? over = Environment.GetEnvironmentVariable("NOCATFARM_SETS_URL");

			return Uri.TryCreate(over, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.IsLoopback
				? u.ToString()
				: Endpoint;
		}
	}

	private static readonly Lazy<HttpClient> Http = new(static () => MakeClient(new SocketsHttpHandler {
		AllowAutoRedirect = false,
		UseCookies = false,
		AutomaticDecompression = DecompressionMethods.All,
		PooledConnectionLifetime = TimeSpan.FromMinutes(15)
	}));

	/// <summary>The client: 30 seconds, a plain nocat.farm User-Agent, an answer of 8 MB at most.</summary>
	public static HttpClient MakeClient(HttpMessageHandler handler) {
		HttpClient http = new(handler, true) {
			Timeout = TimeSpan.FromSeconds(30),
			MaxResponseContentBufferSize = 8 * 1024 * 1024
		};
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "nocat.farm/" + Build.Version);

		return http;
	}

	// ── what goes out ────────────────────────────────────────────────────────
	/// <summary>A card's name: its market name without the "&lt;app&gt;-" in front. Null when it isn't that game's.</summary>
	private static string? NameOf(uint app, string hash) {
		string prefix = app.ToString(CultureInfo.InvariantCulture) + "-";

		return hash.StartsWith(prefix, StringComparison.Ordinal) && (hash.Length > prefix.Length) && (hash.Length - prefix.Length <= 160)
			&& !hash.Any(char.IsControl) ? hash[prefix.Length..] : null;
	}

	/// <summary>
	/// The sets that go out: the cheapest <see cref="MaxSets"/> whole ones it knows, each as nocat.lol takes it - 5 to 15
	/// cards of that game, each priced 1 cent to 1,000.00, listed, and seen in the last 48 hours. Anything else is left out.
	/// [app, [[name, US cents, listings, seconds since seen]...]].
	/// </summary>
	public static JsonArray Sets(IEnumerable<SetFinder.SetRecord> records, DateTime now) {
		JsonArray sets = [];

		foreach (SetFinder.SetRecord r in records.OrderBy(static r => r.UsdCents).ThenBy(static r => r.App)) {
			if (sets.Count >= MaxSets) {
				break;
			}

			if ((r.App == 0) || (r.Cards.Count is < MinCards or > MaxCards)) {
				continue;
			}

			JsonArray cards = [];

			foreach (SetFinder.RecordCard c in r.Cards) {
				long age = (long) Math.Floor((now - c.SeenUtc).TotalSeconds);

				if ((NameOf(r.App, c.Hash) is not { } name) || (c.UsdCents is < 1 or > 100_000) || (c.Listings < 1) || (age < 0) || (age >= SetFinder.CardLife.TotalSeconds)) {
					cards = [];

					break;
				}

				cards.Add(new JsonArray(name, c.UsdCents, c.Listings, age));
			}

			if (cards.Count == r.Cards.Count) {
				sets.Add(new JsonArray(r.App, cards));
			}
		}

		return sets;
	}

	/// <summary>The body sent: this install's random number, the version and the sets.</summary>
	public static string Body(JsonArray sets) =>
		new JsonObject { ["id"] = UserCount.InstallId(), ["v"] = Build.Version, ["s"] = sets.DeepClone() }.ToJsonString();

	/// <summary>What the sets say, without when each card was seen: the same prices sent again a minute later are no news.</summary>
	public static string Signature(JsonArray sets) {
		StringBuilder sb = new();

		foreach (JsonNode? set in sets) {
			sb.Append(set![0]!.ToJsonString()).Append(':');

			foreach (JsonNode? c in set[1]!.AsArray()) {
				sb.Append(c![0]!.ToJsonString()).Append('=').Append(c[1]!.ToJsonString()).Append(';');
			}

			sb.Append('|');
		}

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
	}

	// ── what comes back ──────────────────────────────────────────────────────
	/// <summary>
	/// The shared list as nocat.lol hands it out ({v: 1, s: [[app, [[name, US cents, listings, seconds since seen, ...]...]]...]}),
	/// as plain records for <see cref="SetFinder.Merge"/> - which checks each one again. A set with anything unreadable in it
	/// is left out; the list is read up to <see cref="MaxTaken"/> sets. Empty when it isn't that answer at all.
	/// </summary>
	public static List<SetFinder.SetRecord> Parse(string json, DateTime now) {
		List<SetFinder.SetRecord> records = [];

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("v", out JsonElement v) || (v.ValueKind != JsonValueKind.Number) || (v.GetInt32() != 1)
				|| !root.TryGetProperty("s", out JsonElement s) || (s.ValueKind != JsonValueKind.Array)) {
				return records;
			}

			foreach (JsonElement set in s.EnumerateArray()) {
				if (records.Count >= MaxTaken) {
					break;
				}

				if ((set.ValueKind != JsonValueKind.Array) || (set.GetArrayLength() != 2) || (set[0].ValueKind != JsonValueKind.Number) || !set[0].TryGetUInt32(out uint app) || (app == 0)
					|| (set[1].ValueKind != JsonValueKind.Array) || (set[1].GetArrayLength() is < MinCards or > MaxCards)) {
					continue;
				}

				List<SetFinder.RecordCard> cards = [];

				foreach (JsonElement c in set[1].EnumerateArray()) {
					if ((c.ValueKind != JsonValueKind.Array) || (c.GetArrayLength() < 4) || (c[0].ValueKind != JsonValueKind.String)
						|| (c[1].ValueKind != JsonValueKind.Number) || (c[2].ValueKind != JsonValueKind.Number) || (c[3].ValueKind != JsonValueKind.Number)
						|| !c[1].TryGetInt32(out int usd) || !c[2].TryGetInt32(out int listings) || !c[3].TryGetInt64(out long age) || (age < 0) || (age > 30L * 86400)) {
						break;
					}

					cards.Add(new SetFinder.RecordCard($"{app.ToString(CultureInfo.InvariantCulture)}-{c[0].GetString()}", usd, listings, now.AddSeconds(-age)));
				}

				if (cards.Count == set[1].GetArrayLength()) {
					records.Add(new SetFinder.SetRecord(SetFinder.RecordVersion, app, cards));
				}
			}
		} catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) {
			return [];
		}

		return records;
	}

	// ── one go ───────────────────────────────────────────────────────────────
	/// <summary>What it remembers between goes (config/state/setshare.json): when it last went, what it last sent and when.</summary>
	public sealed class State {
		public long TriedAt { get; set; }
		public long SentAt { get; set; }
		public string Sent { get; set; } = "";
	}

	/// <summary>What one go did: whether its sets went, how many came back and how many of those the finder took.</summary>
	public readonly record struct Go(bool Sent, int Got, int Taken);

	private static readonly Lock Gate = new();
	private static string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", "setshare.json");

	public static State Load() {
		try {
			if (File.Exists(StatePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) is { } st) {
				return st;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// Starts over: at worst one send and one fetch early.
		}

		return new State();
	}

	private static void Save(State st) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(st));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("set finder · couldn't save what it shared · {0}", Log.Describe(e)));
		}
	}

	/// <summary>
	/// One go, when it's due: the sets it knows sent (when there's something new, or a day has passed), then the shared list
	/// fetched and handed to the finder as candidates. Nothing at all while sharing is off or the finder isn't looking, and
	/// nothing before <see cref="Every"/> has passed since the last go - tried or not, so a failure never makes it ask again
	/// soon. Never throws.
	/// </summary>
	public static async Task<Go> TickAsync(SetFinder finder, HttpClient http, GlobalConfig g, bool looking, DateTime now, CancellationToken ct) {
		if (!Sharing(g) || !looking) {
			return default;
		}

		State st;

		lock (Gate) {
			st = Load();

			if (now - new DateTime(st.TriedAt, DateTimeKind.Utc) < Every) {
				return default;
			}

			st.TriedAt = now.Ticks;
			Save(st);
		}

		bool sent = false;
		int got = 0, taken = 0;

		try {
			JsonArray sets = Sets(finder.Records(now), now);
			string sig = Signature(sets);

			if ((sets.Count > 0) && ((sig != st.Sent) || (now - new DateTime(st.SentAt, DateTimeKind.Utc) >= SendAgainAfter))) {
				using HttpRequestMessage post = new(HttpMethod.Post, Target) { Content = new StringContent(Body(sets), Encoding.UTF8) };
				post.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
				using HttpResponseMessage resp = await http.SendAsync(post, ct).ConfigureAwait(false);
				string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

				if ((resp.StatusCode == HttpStatusCode.OK) && Up(text)) {
					sent = true;

					lock (Gate) {
						st.SentAt = now.Ticks;
						st.Sent = sig;
						Save(st);
					}
				}
			}

			using HttpResponseMessage get = await http.GetAsync(Target, ct).ConfigureAwait(false);

			if (get.StatusCode == HttpStatusCode.OK) {
				List<SetFinder.SetRecord> records = Parse(await get.Content.ReadAsStringAsync(ct).ConfigureAwait(false), now);
				got = records.Count;
				taken = got > 0 ? finder.Merge(records, now) : 0;
			}

			Log.Debug(new Said("set finder · shared lists · {0} sent, {1} taken from other copies", sent ? sets.Count : 0, taken));
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			// closing
		} catch (Exception e) {
			Log.Debug(new Said("set finder · nocat.lol didn't answer · {0}", Log.Describe(e)));
		}

		return new Go(sent, got, taken);
	}

	/// <summary>nocat.lol's answer to a send: {"up": true} when it took the sets.</summary>
	public static bool Up(string text) {
		try {
			using JsonDocument doc = JsonDocument.Parse(text);

			return (doc.RootElement.ValueKind == JsonValueKind.Object) && doc.RootElement.TryGetProperty("up", out JsonElement up) && (up.ValueKind == JsonValueKind.True);
		} catch (JsonException) {
			return false;
		}
	}

	/// <summary>Every 10 minutes it looks whether a go is due - the first 5 to 10 minutes after start.</summary>
	public static void Start(BotManager mgr) => _ = Task.Run(async () => {
		await Task.Delay(Rng.Minutes(5, 10)).ConfigureAwait(false);

		while (true) {
			try {
				SetFinder finder = SetFinder.Shared;
				await TickAsync(finder, Http.Value, Live.Global, finder.Looking(mgr.All, DateTime.UtcNow), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Debug(new Said("set finder · nocat.lol didn't answer · {0}", Log.Describe(e)));
			}

			await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
		}
	});
}
