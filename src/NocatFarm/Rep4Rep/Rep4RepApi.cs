using System.Text.Json;

namespace NocatFarm.Rep4Rep;

/// <summary>One comment task rep4rep has assigned to an account.</summary>
public sealed class Rep4RepTask {
	public string TaskId { get; init; } = "";
	public ulong TargetSteamId { get; init; }
	public string TargetName { get; init; } = "";
	public string CommentText { get; init; } = "";

	/// <summary>
	/// rep4rep's OWN comment-template id (the task's comment.id). POST /tasks wants this as `commentId` - never the Steam
	/// comment's id.
	/// </summary>
	public string RequiredCommentId { get; init; } = "";

	/// <summary>
	/// 1.8.9 (rep4rep's v1 API): the task's ticket. Sent with the comment, rep4rep takes it for up to an hour at the points
	/// shown, even if the request filled up meanwhile. Empty when there wasn't one.
	/// </summary>
	public string Ticket { get; init; } = "";
}

/// <summary>
/// Thin client for rep4rep's API, https://api.rep4rep.com/v1 (rep4rep.com/api).
///
/// The old https://rep4rep.com/pub-api was taken down in November 2026 - every call answered 404, and nocat.farm said
/// "rep4rep.com isn't answering" on every account for good. The v1 API takes the key in an Authorization header (never in
/// the address), wraps every answer in "data", addresses a profile by its SteamID64, and gives errors a real HTTP status.
/// </summary>
public sealed class Rep4RepApi : IDisposable {
	private const string Base = "https://api.rep4rep.com/v1";

	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

	private volatile string _token = "";

	/// <summary>The API token. A different one forgets whether rep4rep took the last one (see <see cref="UserAnswered"/>).</summary>
	public string Token {
		get => _token;
		set {
			if (!string.Equals(_token, value, StringComparison.Ordinal)) {
				_token = value;
				_userAnswered = 0;
			}
		}
	}

	// 0 = not asked yet with this token, 1 = the last /user answered with a balance, -1 = it didn't.
	private volatile int _userAnswered;

	/// <summary>
	/// Whether the last <see cref="GetUserAsync"/> with this token got a balance back (any balance, even an odd one):
	/// true yes, false no (a bad token, or rep4rep unreachable), null not asked yet. "Connected" is this, never the
	/// points figure - a negative pending made the dashboard call a working token "unlinked".
	/// </summary>
	public bool? UserAnswered => _userAnswered switch { 1 => true, -1 => false, _ => null };

	/// <summary>The points when rep4rep sends something that isn't a number - a balance that can't be true.</summary>
	public const int NotANumber = int.MinValue;

	public bool HasToken => !string.IsNullOrWhiteSpace(Token);

	public Rep4RepApi() => _http.DefaultRequestHeaders.Add("User-Agent", "nocatFarm");

	private static Uri Url(string path) => new($"{Base}{path}");

	/// <summary>The HTTP status of the last answer that wasn't a 2xx (0: none yet, or the last one was fine).</summary>
	private volatile int _lastRefusal;

	/// <summary>The last call was turned down for the key itself (401 or 403): rep4rep answered - the token is wrong.</summary>
	private bool KeyRefused => _lastRefusal is 401 or 403;

	/// <summary>A request with the key in the Authorization header - the v1 API's way; in the address it would reach logs.</summary>
	private HttpRequestMessage Request(HttpMethod method, Uri url) {
		HttpRequestMessage r = new(method, url);
		r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
		r.Headers.Accept.ParseAdd("application/json");

		return r;
	}

	private async Task<string?> GetAsync(string path, CancellationToken ct) {
		if (!HasToken) {
			return null;
		}

		Uri url = Url(path);

		try {
			using HttpRequestMessage request = Request(HttpMethod.Get, url);
			using HttpResponseMessage r = await _http.SendAsync(request, ct).ConfigureAwait(false);

			return Answered("GET", url, r) ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Trouble(url, $"rep4rep GET {Log.Where(url)} failed: {Log.Describe(e)}");

			return null;
		}
	}

	// ── when rep4rep doesn't answer ─────────────────────────────────────────
	// The API key rides in the query (and the form), so only ever Log.Where - host and path - goes in a line, never
	// the address itself, and never a body: rep4rep could echo the key back. The task list is asked for by every
	// account in a loop, so the same failure is written once per change (or hour), keyed by the path.

	/// <summary>True for a 2xx; otherwise writes the status down.</summary>
	private bool Answered(string method, Uri url, HttpResponseMessage r) {
		if (r.IsSuccessStatusCode) {
			_lastRefusal = 0;
			Log.Recovered($"r4r:{url.AbsolutePath}");

			return true;
		}

		_lastRefusal = (int) r.StatusCode;
		Trouble(url, $"rep4rep {method} {Log.Where(url)} -> {(int) r.StatusCode} {r.ReasonPhrase}");

		return false;
	}

	private static void Trouble(Uri url, string text) => Log.DebugOnChange($"r4r:{url.AbsolutePath}", text, "rep4rep");

	private static void Unreadable(string path, Exception e) =>
		Log.DebugOnChange($"r4r:{path}:parse", $"rep4rep {path}: couldn't read the answer: {Log.Describe(e)}", "rep4rep");

	private async Task<string?> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct) {
		if (!HasToken) {
			return null;
		}

		Uri url = Url(path);

		try {
			using HttpRequestMessage request = Request(HttpMethod.Post, url);
			request.Content = new FormUrlEncodedContent(form);
			using HttpResponseMessage r = await _http.SendAsync(request, ct).ConfigureAwait(false);

			return Answered("POST", url, r) ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Trouble(url, $"rep4rep POST {Log.Where(url)} failed: {Log.Describe(e)}");

			return null;
		}
	}

	/// <summary>Points currently on the rep4rep account, or null if it couldn't be read.</summary>
	public async Task<(int Points, int PendingPoints)?> GetUserAsync(CancellationToken ct = default) {
		string asked = Token;
		(int Points, int PendingPoints)? user = await ReadUserAsync(ct).ConfigureAwait(false);

		// Only for the token it was asked with: a token swapped in meanwhile starts again from "not asked yet".
		if (string.Equals(asked, Token, StringComparison.Ordinal) && (asked.Length > 0)) {
			_userAnswered = user == null ? -1 : 1;
		}

		return user;
	}

	private async Task<(int Points, int PendingPoints)?> ReadUserAsync(CancellationToken ct) {
		string? body = await GetAsync("/user", ct).ConfigureAwait(false);

		if (body == null) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			// v1: {"data": {...}}.
			if ((root.ValueKind == JsonValueKind.Object) && root.TryGetProperty("data", out JsonElement data)) {
				root = data;
			}

			if (root.ValueKind == JsonValueKind.Array) {
				root = root.EnumerateArray().FirstOrDefault();
			}

			if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out JsonElement user)) {
				root = user;
			}

			// rep4rep answers a bad token with a 200 and an error, not a balance: no "points" at all is no answer.
			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("points", out _)) {
				return null;
			}

			// A "points" that isn't a number is an answer, just an impossible one (see WebHost.Believable).
			int points = int.TryParse(ReadString(root, "points"), out int p) ? p : NotANumber;

			return (points, ReadInt(root, "pendingPoints"));
		} catch (Exception e) {
			Unreadable("/user", e);

			return null;
		}
	}

	/// <summary>
	/// Every Steam profile registered on the rep4rep account, as (the id rep4rep addresses it by, steamID64) - in the v1
	/// API both are the SteamID64.
	/// </summary>
	public async Task<List<(string Id, string SteamId)>> GetProfilesAsync(CancellationToken ct = default) {
		List<(string, string)> profiles = [];
		string? body = await GetAsync("/steam-profiles", ct).ConfigureAwait(false);

		if (body == null) {
			return profiles;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (JsonElement profile in Rows(doc.RootElement)) {
				string? steam = ReadString(profile, "steamId");

				if (!string.IsNullOrEmpty(steam)) {
					profiles.Add((steam, steam));
				}
			}
		} catch (Exception e) {
			// unparsable - treated as none registered
			Unreadable("/steam-profiles", e);
		}

		return profiles;
	}

	/// <summary>
	/// rep4rep's internal id for a Steam account. With <paramref name="autoAdd"/> it registers the profile if
	/// rep4rep hasn't seen it, which is the step people otherwise have to do by hand on the website.
	/// </summary>
	public async Task<string?> ResolveProfileIdAsync(ulong steamId, bool autoAdd = true, CancellationToken ct = default) =>
		(await ResolveAsync(steamId, autoAdd, ct).ConfigureAwait(false)).Id;

	/// <summary>
	/// <see cref="ResolveProfileIdAsync"/>, and whether rep4rep.com answered at all (Unanswered: down, a 5xx, no connection -
	/// not a "not found"). 1.8.6: said per lookup - one shared flag let another account's lookup clear it first, and the
	/// false "check the API token" warning came back.
	/// </summary>
	public async Task<(string? Id, bool Unanswered)> ResolveAsync(ulong steamId, bool autoAdd, CancellationToken ct) {
		(string? id, bool unanswered) = await FindProfileIdAsync(steamId, ct).ConfigureAwait(false);

		// 1.8.4: rep4rep.com down (a 503 after the box restarted) isn't "not registered" - nothing is added, nothing is blamed
		// on the token; the caller just looks again later.
		if (id != null || !autoAdd || unanswered) {
			return (id, unanswered);
		}

		await PostAsync("/steam-profiles", new Dictionary<string, string> { ["profile"] = steamId.ToString() }, ct).ConfigureAwait(false);

		return await FindProfileIdAsync(steamId, ct).ConfigureAwait(false);
	}

	private async Task<(string? Id, bool Unanswered)> FindProfileIdAsync(ulong steamId, CancellationToken ct) {
		string? body = await GetAsync("/steam-profiles", ct).ConfigureAwait(false);

		// The key turned down is rep4rep answering: not "down", so the token is what gets blamed.
		if ((body == null) && KeyRefused) {
			return (null, false);
		}

		if (body == null) {
			return (null, HasToken);
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			string wanted = steamId.ToString();

			foreach (JsonElement profile in Rows(doc.RootElement)) {
				if (ReadString(profile, "steamId") == wanted) {
					return (wanted, false);
				}
			}
		} catch (Exception e) {
			// unparsable response - treated as "not found", the caller retries later
			Unreadable("/steam-profiles", e);
		}

		return (null, false);
	}

	/// <summary>Every task currently assigned to a profile.</summary>
	public async Task<List<Rep4RepTask>> GetTasksAsync(string profileId, CancellationToken ct = default) {
		List<Rep4RepTask> tasks = [];
		string? body = await GetAsync($"/steam-profiles/{Uri.EscapeDataString(profileId)}/tasks?limit=25", ct).ConfigureAwait(false);

		if (body == null) {
			return tasks;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (JsonElement row in Rows(doc.RootElement)) {
				// v1: {"requestId", "target": {"steamId", "name"}, "comment": {"id", "text"}, "points", "ticket"}
				JsonElement target = Child(row, "target"), comment = Child(row, "comment");
				string? taskId = ReadString(row, "requestId");
				string? text = ReadString(comment, "text");
				string? commentId = ReadString(comment, "id");

				if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(commentId)
					|| !ulong.TryParse(ReadString(target, "steamId"), out ulong targetId) || (targetId == 0)) {
					continue;
				}

				tasks.Add(new Rep4RepTask {
					TaskId = taskId,
					TargetSteamId = targetId,
					TargetName = ReadString(target, "name") ?? targetId.ToString(),
					CommentText = text,
					RequiredCommentId = commentId,
					Ticket = ReadString(row, "ticket") ?? ""
				});
			}
		} catch (Exception e) {
			// leave the list empty; the module treats that as "nothing to do right now"
			Unreadable("/tasks", e);
		}

		return tasks;
	}

	/// <summary>
	/// Report a posted comment (POST /tasks): taken when rep4rep answers 2xx (201 with the task). It checks the comment on
	/// Steam about 5 minutes later and pays once it's found. A 409 (gone with no valid ticket, or this profile commented on
	/// that target in the last day) or any other refusal is said in the log by its status, and isn't credited.
	/// </summary>
	public async Task<bool> CompleteTaskAsync(string taskId, string requiredCommentId, string profileId, CancellationToken ct = default, string ticket = "") {
		Dictionary<string, string> form = new() {
			["steamId"] = profileId,
			["requestId"] = taskId,
			["commentId"] = requiredCommentId
		};

		if (ticket.Length > 0) {
			form["ticket"] = ticket;
		}

		if (await PostAsync("/tasks", form, ct).ConfigureAwait(false) == null) {
			Log.Debug($"rep4rep didn't take task {taskId} (HTTP {_lastRefusal})", "rep4rep");

			return false;
		}

		return true;
	}

	// ── json helpers ────────────────────────────────────────────────────────
	// rep4rep sometimes wraps results in an object and sometimes returns a bare array; ids arrive quoted in some
	// responses and unquoted in others. These smooth both over.
	private static IEnumerable<JsonElement> Rows(JsonElement root) {
		if (root.ValueKind == JsonValueKind.Array) {
			return root.EnumerateArray();
		}

		if (root.ValueKind != JsonValueKind.Object) {
			return [];
		}

		foreach (JsonProperty p in root.EnumerateObject()) {
			if (p.Value.ValueKind == JsonValueKind.Array) {
				return p.Value.EnumerateArray();
			}
		}

		return [root];
	}

	/// <summary>An object's child object, or an empty element when there isn't one.</summary>
	private static JsonElement Child(JsonElement e, string name) =>
		(e.ValueKind == JsonValueKind.Object) && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Object) ? v : default;

	private static string? ReadString(JsonElement e, string name) {
		if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)) {
			return null;
		}

		return v.ValueKind switch {
			JsonValueKind.String => v.GetString(),
			JsonValueKind.Number => v.ToString(),
			_ => null
		};
	}

	private static int ReadInt(JsonElement e, string name) {
		string? s = ReadString(e, name);

		return int.TryParse(s, out int v) ? v : 0;
	}

	public void Dispose() => _http.Dispose();
}
