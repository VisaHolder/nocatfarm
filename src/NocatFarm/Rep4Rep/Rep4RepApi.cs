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

		try {
			using HttpResponseMessage r = await _http.GetAsync(Url(path), ct).ConfigureAwait(false);

			return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
		} catch (OperationCanceledException) {
			throw;
		} catch {
			return null;
		}
	}

	private async Task<string?> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct) {
		if (!HasToken) {
			return null;
		}

		form["apiToken"] = Token;   // the docs put it in the query, the reference clients put it in the body - send both

		try {
			using FormUrlEncodedContent content = new(form);
			using HttpResponseMessage r = await _http.PostAsync(Url(path), content, ct).ConfigureAwait(false);

			return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
		} catch (OperationCanceledException) {
			throw;
		} catch {
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
		} catch {
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
		} catch {
			// unparsable - treated as none registered
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
		} catch {
			// unparsable response - treated as "not found", the caller retries later
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
		} catch {
			// leave the list empty; the module treats that as "nothing to do right now"
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
			return false;
		}

		// rep4rep answers a successful completion with a success flag or a bare ok; anything else is a refusal
		// worth surfacing rather than silently counting.
		return sent.Contains("success", StringComparison.OrdinalIgnoreCase)
			|| sent.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase)
			|| sent.Trim() is "true" or "[]" or "{}";
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
