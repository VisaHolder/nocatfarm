using System.Text.Json;

namespace NocatFarm.Addons.Rep4Rep;

/// <summary>One comment task rep4rep has assigned to an account.</summary>
public sealed class Rep4RepTask {
	public string TaskId { get; init; } = "";
	public ulong TargetSteamId { get; init; }
	public string TargetName { get; init; } = "";
	public string CommentText { get; init; } = "";

	/// <summary>rep4rep's OWN comment-template id (the task's comment.id). POST /tasks wants this as `commentId`.</summary>
	public string RequiredCommentId { get; init; } = "";

	/// <summary>The task's ticket: sent with the comment, rep4rep takes it for up to an hour at the points shown. Can be empty.</summary>
	public string Ticket { get; init; } = "";
}

/// <summary>
/// Thin client for rep4rep's API, https://api.rep4rep.com/v1 (rep4rep.com/api).
///
/// The key goes in an Authorization header (never in the address), every answer is wrapped in "data", a profile is
/// addressed by its SteamID64, and errors come with a real HTTP status - 401 when the key is wrong or revoked.
/// </summary>
public sealed class Rep4RepApi : IDisposable {
	private const string Base = "https://api.rep4rep.com/v1";

	private readonly HttpClient _http;

	private volatile string _token = "";

	/// <summary>Where a failed request is told - once per change of what went wrong, not once per try.</summary>
	public Action<string>? Trouble { get; set; }

	/// <summary>The API key. A different one forgets whether rep4rep took the last one.</summary>
	public string Token {
		get => _token;
		set {
			value ??= "";

			if (!string.Equals(_token, value.Trim(), StringComparison.Ordinal)) {
				_token = value.Trim();
				_userAnswered = 0;
				_lastRefusal = 0;
			}
		}
	}

	// 0 = not asked yet with this key, 1 = the last /user answered with a balance, -1 = it didn't.
	private volatile int _userAnswered;

	/// <summary>Whether the last /user with this key got a balance back: true, false, or null when not asked yet.</summary>
	public bool? UserAnswered => _userAnswered switch { 1 => true, -1 => false, _ => null };

	/// <summary>The points when rep4rep sends something that isn't a number - a balance that can't be true.</summary>
	public const int NotANumber = int.MinValue;

	public bool HasToken => !string.IsNullOrWhiteSpace(Token);

	public Rep4RepApi(HttpMessageHandler? handler = null) {
		_http = handler == null ? new HttpClient() : new HttpClient(handler);
		_http.Timeout = TimeSpan.FromSeconds(30);
		_http.DefaultRequestHeaders.Add("User-Agent", "nocatFarm-rep4rep");
	}

	private static Uri Url(string path) => new($"{Base}{path}");

	/// <summary>The HTTP status of the last answer that wasn't a 2xx (0: none yet, or the last one was fine).</summary>
	private volatile int _lastRefusal;

	/// <summary>The last call was turned down for the key itself (401 or 403): rep4rep answered - the key is wrong.</summary>
	public bool KeyRefused => _lastRefusal is 401 or 403;

	/// <summary>rep4rep said 401: the key is invalid (revoked, or never right). Says so until a call succeeds or the key changes.</summary>
	public bool KeyInvalid => _lastRefusal == 401;

	private HttpRequestMessage Request(HttpMethod method, Uri url) {
		HttpRequestMessage r = new(method, url);
		r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
		r.Headers.Accept.ParseAdd("application/json");

		return r;
	}

	private readonly Dictionary<string, string> _said = [];

	/// <summary>A failure, said once per change per path - the task list is asked for by every account in a loop.</summary>
	private void Say(string key, string text) {
		lock (_said) {
			if (_said.TryGetValue(key, out string? was) && (was == text)) {
				return;
			}

			_said[key] = text;
		}

		Trouble?.Invoke(text);
	}

	private void Recovered(string key) {
		lock (_said) {
			_said.Remove(key);
		}
	}

	/// <summary>Only the host and path in a line - never the address with anything in it, and never a body.</summary>
	private static string Where(Uri url) => url.Host + url.AbsolutePath;

	private bool Answered(string method, Uri url, HttpResponseMessage r) {
		if (r.IsSuccessStatusCode) {
			_lastRefusal = 0;
			Recovered(url.AbsolutePath);

			return true;
		}

		_lastRefusal = (int) r.StatusCode;
		Say(url.AbsolutePath, $"rep4rep {method} {Where(url)} -> {(int) r.StatusCode} {r.ReasonPhrase}");

		return false;
	}

	private async Task<string?> SendAsync(HttpMethod method, string path, Dictionary<string, string>? form, CancellationToken ct) =>
		(await SendWithStatusAsync(method, path, form, ct).ConfigureAwait(false)).Body;

	/// <summary>The answer's body on a 2xx (null otherwise), and its HTTP status - 0 when nothing came back at all.</summary>
	private async Task<(string? Body, int Status)> SendWithStatusAsync(HttpMethod method, string path, Dictionary<string, string>? form, CancellationToken ct) {
		if (!HasToken) {
			return (null, 0);
		}

		Uri url = Url(path);

		try {
			using HttpRequestMessage request = Request(method, url);

			if (form != null) {
				request.Content = new FormUrlEncodedContent(form);
			}

			using HttpResponseMessage r = await _http.SendAsync(request, ct).ConfigureAwait(false);

			return (Answered(method.Method, url, r) ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null, (int) r.StatusCode);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Say(url.AbsolutePath, $"rep4rep {method.Method} {Where(url)} failed: {e.GetType().Name}: {e.Message}");

			return (null, 0);
		}
	}

	private Task<string?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

	private Task<string?> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct) => SendAsync(HttpMethod.Post, path, form, ct);

	private void Unreadable(string path, Exception e) => Say(path + ":parse", $"rep4rep {path}: couldn't read the answer: {e.GetType().Name}: {e.Message}");

	/// <summary>Points currently on the rep4rep account, or null if it couldn't be read.</summary>
	public async Task<(int Points, int PendingPoints)?> GetUserAsync(CancellationToken ct = default) {
		string asked = Token;
		(int Points, int PendingPoints)? user = await ReadUserAsync(ct).ConfigureAwait(false);

		// Only for the key it was asked with: a key swapped in meanwhile starts again from "not asked yet".
		if (string.Equals(asked, Token, StringComparison.Ordinal) && (asked.Length > 0)) {
			_userAnswered = user == null ? -1 : 1;
		}

		return user;
	}

	private async Task<(int Points, int PendingPoints)?> ReadUserAsync(CancellationToken ct) {
		string? body = await GetAsync("/user", ct).ConfigureAwait(false);

		return body == null ? null : ReadUser(body, this);
	}

	/// <summary>A /user answer's balance: v1 wraps it in "data". No "points" at all is no answer.</summary>
	internal static (int Points, int PendingPoints)? ReadUser(string body, Rep4RepApi? api = null) {
		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind == JsonValueKind.Object) && root.TryGetProperty("data", out JsonElement data)) {
				root = data;
			}

			if (root.ValueKind == JsonValueKind.Array) {
				root = root.EnumerateArray().FirstOrDefault();
			}

			if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out JsonElement user)) {
				root = user;
			}

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("points", out _)) {
				return null;
			}

			int points = int.TryParse(ReadString(root, "points"), out int p) ? p : NotANumber;

			return (points, ReadInt(root, "pendingPoints"));
		} catch (JsonException e) {
			api?.Unreadable("/user", e);

			return null;
		}
	}

	/// <summary>Every Steam profile registered on the rep4rep account, by SteamID64.</summary>
	public async Task<List<string>> GetProfilesAsync(CancellationToken ct = default) {
		string? body = await GetAsync("/steam-profiles", ct).ConfigureAwait(false);

		return body == null ? [] : ReadProfiles(body, this);
	}

	internal static List<string> ReadProfiles(string body, Rep4RepApi? api = null) {
		List<string> profiles = [];

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (JsonElement profile in Rows(doc.RootElement)) {
				if (ReadString(profile, "steamId") is { Length: > 0 } steam) {
					profiles.Add(steam);
				}
			}
		} catch (JsonException e) {
			api?.Unreadable("/steam-profiles", e);
		}

		return profiles;
	}

	/// <summary>
	/// rep4rep's id for a Steam account (its SteamID64), and whether rep4rep answered at all (Unanswered: down, a 5xx, no
	/// connection - not a "not found"). With <paramref name="autoAdd"/> it registers the profile when rep4rep hasn't got it.
	/// </summary>
	public async Task<(string? Id, bool Unanswered)> ResolveAsync(ulong steamId, bool autoAdd, CancellationToken ct = default) {
		(string? id, bool unanswered, _) = await ResolveDetailedAsync(steamId, autoAdd, ct).ConfigureAwait(false);

		return (id, unanswered);
	}

	/// <summary>
	/// <see cref="ResolveAsync"/>, and whether rep4rep turned the add down because the Steam profile is on ANOTHER rep4rep
	/// account (409: "a profile that's linked to another account can only be added on the website, by signing in through
	/// Steam"). That one only the owner can fix, on rep4rep.com - asking again changes nothing.
	/// </summary>
	public async Task<(string? Id, bool Unanswered, bool LinkedElsewhere)> ResolveDetailedAsync(ulong steamId, bool autoAdd, CancellationToken ct = default) {
		(string? id, bool unanswered) = await FindProfileIdAsync(steamId, ct).ConfigureAwait(false);

		// rep4rep.com down isn't "not registered" - nothing is added, nothing is blamed on the key.
		if (id != null || !autoAdd || unanswered || KeyRefused) {
			return (id, unanswered, false);
		}

		(_, int status) = await SendWithStatusAsync(HttpMethod.Post, "/steam-profiles", new Dictionary<string, string> { ["profile"] = steamId.ToString() }, ct).ConfigureAwait(false);

		if (status == 409) {
			return (null, false, true);
		}

		(id, unanswered) = await FindProfileIdAsync(steamId, ct).ConfigureAwait(false);

		return (id, unanswered, false);
	}

	private async Task<(string? Id, bool Unanswered)> FindProfileIdAsync(ulong steamId, CancellationToken ct) {
		string? body = await GetAsync("/steam-profiles", ct).ConfigureAwait(false);

		if ((body == null) && KeyRefused) {
			return (null, false);   // rep4rep answered - the key is what's wrong
		}

		if (body == null) {
			return (null, HasToken);
		}

		string wanted = steamId.ToString();

		return (ReadProfiles(body, this).Contains(wanted) ? wanted : null, false);
	}

	/// <summary>Every task currently assigned to a profile.</summary>
	public async Task<List<Rep4RepTask>> GetTasksAsync(string profileId, CancellationToken ct = default) {
		string? body = await GetAsync($"/steam-profiles/{Uri.EscapeDataString(profileId)}/tasks?limit=25", ct).ConfigureAwait(false);

		return body == null ? [] : ReadTasks(body, this);
	}

	/// <summary>v1: {"requestId", "target": {"steamId", "name"}, "comment": {"id", "text"}, "points", "ticket"} per row.</summary>
	internal static List<Rep4RepTask> ReadTasks(string body, Rep4RepApi? api = null) {
		List<Rep4RepTask> tasks = [];

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (JsonElement row in Rows(doc.RootElement)) {
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
		} catch (JsonException e) {
			api?.Unreadable("/tasks", e);
		}

		return tasks;
	}

	/// <summary>
	/// Report a posted comment (POST /tasks): taken when rep4rep answers 2xx. It checks the comment on Steam about 5 minutes
	/// later and pays once it's found. A 409 (gone, or this profile commented on that target in the last day) isn't credited.
	/// </summary>
	public async Task<bool> CompleteTaskAsync(Rep4RepTask task, string profileId, CancellationToken ct = default) {
		Dictionary<string, string> form = new() {
			["steamId"] = profileId,
			["requestId"] = task.TaskId,
			["commentId"] = task.RequiredCommentId
		};

		if (task.Ticket.Length > 0) {
			form["ticket"] = task.Ticket;
		}

		return await PostAsync("/tasks", form, ct).ConfigureAwait(false) != null;
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

	private static int ReadInt(JsonElement e, string name) => int.TryParse(ReadString(e, name), out int v) ? v : 0;

	public void Dispose() => _http.Dispose();
}
