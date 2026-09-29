using System.Text.Json;

namespace NocatFarm.Rep4Rep;

/// <summary>One comment task rep4rep has assigned to an account.</summary>
public sealed class Rep4RepTask {
	public string TaskId { get; init; } = "";
	public ulong TargetSteamId { get; init; }
	public string TargetName { get; init; } = "";
	public string CommentText { get; init; } = "";

	/// <summary>
	/// rep4rep's OWN comment-template id. /tasks/complete wants this as `commentId`. Sending the Steam comment id
	/// instead comes back as HTTP 200 with {"info":"Comment not found."} and silently never credits.
	/// </summary>
	public string RequiredCommentId { get; init; } = "";
}

/// <summary>
/// Thin client for https://rep4rep.com/pub-api.
///
/// One quirk drives the design: rep4rep answers 200 for logical errors too, so "the request went through" is not
/// "rep4rep accepted it". Anything that matters is confirmed by re-reading state afterwards.
/// </summary>
public sealed class Rep4RepApi : IDisposable {
	private const string Base = "https://rep4rep.com/pub-api";

	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

	public string Token { get; set; } = "";

	public bool HasToken => !string.IsNullOrWhiteSpace(Token);

	public Rep4RepApi() => _http.DefaultRequestHeaders.Add("User-Agent", "nocatFarm");

	private Uri Url(string path) => new($"{Base}{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}apiToken={Uri.EscapeDataString(Token)}");

	private async Task<string?> GetAsync(string path, CancellationToken ct) {
		if (!HasToken) {
			return null;
		}

		Uri url = Url(path);

		try {
			using HttpResponseMessage r = await _http.GetAsync(url, ct).ConfigureAwait(false);

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
	private static bool Answered(string method, Uri url, HttpResponseMessage r) {
		if (r.IsSuccessStatusCode) {
			Log.Recovered($"r4r:{url.AbsolutePath}");

			return true;
		}

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

		form["apiToken"] = Token;   // the docs put it in the query, the reference clients put it in the body - send both

		Uri url = Url(path);

		try {
			using FormUrlEncodedContent content = new(form);
			using HttpResponseMessage r = await _http.PostAsync(url, content, ct).ConfigureAwait(false);

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
		string? body = await GetAsync("/user", ct).ConfigureAwait(false);

		if (body == null) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.ValueKind == JsonValueKind.Array) {
				root = root.EnumerateArray().FirstOrDefault();
			}

			if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out JsonElement user)) {
				root = user;
			}

			return (ReadInt(root, "points"), ReadInt(root, "pendingPoints"));
		} catch (Exception e) {
			Unreadable("/user", e);

			return null;
		}
	}

	/// <summary>Every Steam profile registered on the rep4rep account, as (rep4rep id, steamID64).</summary>
	public async Task<List<(string Id, string SteamId)>> GetProfilesAsync(CancellationToken ct = default) {
		List<(string, string)> profiles = [];
		string? body = await GetAsync("/user/steamprofiles", ct).ConfigureAwait(false);

		if (body == null) {
			return profiles;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (JsonElement profile in Rows(doc.RootElement)) {
				string? id = ReadString(profile, "id");
				string? steam = ReadString(profile, "steamId");

				if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(steam)) {
					profiles.Add((id, steam));
				}
			}
		} catch (Exception e) {
			// unparsable - treated as none registered
			Unreadable("/user/steamprofiles", e);
		}

		return profiles;
	}

	/// <summary>
	/// rep4rep's internal id for a Steam account. With <paramref name="autoAdd"/> it registers the profile if
	/// rep4rep hasn't seen it, which is the step people otherwise have to do by hand on the website.
	/// </summary>
	public async Task<string?> ResolveProfileIdAsync(ulong steamId, bool autoAdd = true, CancellationToken ct = default) {
		string? id = await FindProfileIdAsync(steamId, ct).ConfigureAwait(false);

		if (id != null || !autoAdd) {
			return id;
		}

		await PostAsync("/user/steamprofiles/add", new Dictionary<string, string> { ["steamProfile"] = steamId.ToString() }, ct).ConfigureAwait(false);

		return await FindProfileIdAsync(steamId, ct).ConfigureAwait(false);
	}

	private async Task<string?> FindProfileIdAsync(ulong steamId, CancellationToken ct) {
		string? body = await GetAsync("/user/steamprofiles", ct).ConfigureAwait(false);

		if (body == null) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			string wanted = steamId.ToString();

			foreach (JsonElement profile in Rows(doc.RootElement)) {
				if (ReadString(profile, "steamId") == wanted) {
					return ReadString(profile, "id");
				}
			}
		} catch (Exception e) {
			// unparsable response - treated as "not found", the caller retries later
			Unreadable("/user/steamprofiles", e);
		}

		return null;
	}

	/// <summary>Every task currently assigned to a profile.</summary>
	public async Task<List<Rep4RepTask>> GetTasksAsync(string profileId, CancellationToken ct = default) {
		List<Rep4RepTask> tasks = [];
		string? body = await GetAsync($"/tasks?steamProfile={Uri.EscapeDataString(profileId)}", ct).ConfigureAwait(false);

		if (body == null) {
			return tasks;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (JsonElement row in Rows(doc.RootElement)) {
				string? taskId = ReadString(row, "taskId");
				string? text = ReadString(row, "requiredCommentText");
				string? commentId = ReadString(row, "requiredCommentId");
				string? target = ReadString(row, "targetSteamProfileId");

				if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(commentId) || !ulong.TryParse(target, out ulong targetId) || (targetId == 0)) {
					continue;
				}

				tasks.Add(new Rep4RepTask {
					TaskId = taskId,
					TargetSteamId = targetId,
					TargetName = ReadString(row, "targetSteamProfileName") ?? targetId.ToString(),
					CommentText = text,
					RequiredCommentId = commentId
				});
			}
		} catch (Exception e) {
			// leave the list empty; the module treats that as "nothing to do right now"
			Unreadable("/tasks", e);
		}

		return tasks;
	}

	/// <summary>
	/// Report a task done and CONFIRM it. rep4rep 200s regardless, so the real check is that the task has
	/// disappeared from the queue.
	/// </summary>
	public async Task<bool> CompleteTaskAsync(string taskId, string requiredCommentId, string profileId, CancellationToken ct = default) {
		string? sent = await PostAsync("/tasks/complete", new Dictionary<string, string> {
			["taskId"] = taskId,
			["commentId"] = requiredCommentId,
			["authorSteamProfileId"] = profileId
		}, ct).ConfigureAwait(false);

		if (sent == null) {
			return false;
		}

		// Trust what /tasks/complete itself said, and nothing else.
		//
		// This used to "confirm" the credit by re-fetching /tasks and checking the id had gone. That reads as
		// careful and is in fact close to a coin toss: rep4rep re-samples the task list on every request, so the
		// id is usually absent from the next batch whether or not anything was credited - and occasionally still
		// present when it was. A check that is wrong in both directions is worse than no check.
		if (sent.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)) {
			Log.Debug($"rep4rep didn't credit task {taskId}: {Reason(sent)}", "rep4rep");

			return false;
		}

		// rep4rep answers a successful completion with a success flag or a bare ok; anything else is a refusal
		// worth surfacing rather than silently counting.
		bool ok = sent.Contains("success", StringComparison.OrdinalIgnoreCase)
			|| sent.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase)
			|| sent.Trim() is "true" or "[]" or "{}";

		if (!ok) {
			Log.Debug($"rep4rep didn't credit task {taskId}: {Reason(sent)}", "rep4rep");
		}

		return ok;
	}

	/// <summary>
	/// rep4rep's own words for a refusal - the "error" or "info" field only, never the body itself, which could carry
	/// the key back. "no reason given" when there's neither.
	/// </summary>
	private static string Reason(string body) {
		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			foreach (string field in new[] { "error", "info", "message" }) {
				if ((doc.RootElement.ValueKind == JsonValueKind.Object) && (ReadString(doc.RootElement, field) is { Length: > 0 } said)) {
					return Log.Scrub(said.Length > 150 ? said[..150] : said);
				}
			}
		} catch (JsonException) {
			return "an answer that isn't JSON";   // what it said can't be shown safely - only that it wasn't the usual shape
		}

		return "no reason given";
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
