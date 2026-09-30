using System.Globalization;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>
/// Steam's mobile confirmations, the way the Steam app shows them: trades, market listings, account changes - each
/// waiting for Confirm or Deny.
/// </summary>
/// <remarks>
/// Done exactly as the reference authenticators do it - ArchiSteamFarm (Steam/Security/MobileAuthenticator.cs),
/// steamguard-cli (steamguard/src/confirmation.rs) and node-steamcommunity (components/confirmations.js) all agree:
///   • every request is signed with the identity secret: HMAC-SHA1 over Steam's clock and the tag "conf", sent as
///     p (device id), a (SteamID), k (the signature), t (the time), m=react, tag=conf;
///   • the list is GET /mobileconf/getlist; one answer is GET /mobileconf/ajaxop with op=allow|cancel, cid, ck;
///     several are POST /mobileconf/multiajaxop with cid[] and ck[] repeated;
///   • Steam's clock, not this PC's - a PC a minute out signs keys Steam rejects - asked from ITwoFactorService/QueryTime;
///   • one request at a time per account, and never the same (time, tag) twice - ASF's TimeSemaphore does the same,
///     because Steam refuses a signature it has already seen.
/// The list is also kept a few seconds, so the page, the console and the trade module asking together cost one request.
/// </remarks>
public static class Confirmations {
	/// <summary>One waiting confirmation, as /mobileconf/getlist describes it.</summary>
	/// <param name="CreatorId">For a trade (type 2) this is the trade offer's id; for a market listing, the listing's.</param>
	public sealed record Item(ulong Id, ulong Nonce, ulong CreatorId, int Type, string TypeName, string Headline, List<string> Summary, string Icon, long Created);

	/// <summary>Steam's confirmation types.</summary>
	public const int Trade = 2;

	/// <summary>A market listing waiting to go up - its creator id is the listing's id.</summary>
	public const int MarketListing = 3;

	private static readonly HttpClient Plain = new() { Timeout = TimeSpan.FromSeconds(15) };
	private static long _offset;
	private static DateTime _alignedAt = DateTime.MinValue;
	private static readonly SemaphoreSlim AlignGate = new(1, 1);

	private static readonly Dictionary<string, (DateTime At, List<Item> Items)> Recent = [];

	/// <summary>One confirmation request at a time per account, each with a time of its own.</summary>
	private static readonly Dictionary<string, (SemaphoreSlim Gate, long LastTime)> Gates = [];

	private const string Tag = "conf";

	/// <summary>Wait for this account's turn and hand out a Steam time it hasn't signed with yet.</summary>
	private static async Task<(SemaphoreSlim Gate, long Time)> TurnAsync(Bot bot, CancellationToken ct) {
		SemaphoreSlim gate;

		lock (Gates) {
			if (!Gates.TryGetValue(bot.Name, out (SemaphoreSlim Gate, long LastTime) g)) {
				g = (new SemaphoreSlim(1, 1), 0);
				Gates[bot.Name] = g;
			}

			gate = g.Gate;
		}

		await gate.WaitAsync(ct).ConfigureAwait(false);

		try {
			long time = await SteamTimeAsync(ct).ConfigureAwait(false);
			long last;

			lock (Gates) {
				last = Gates[bot.Name].LastTime;
			}

			if (time <= last) {
				await Task.Delay(TimeSpan.FromSeconds(last - time + 1), ct).ConfigureAwait(false);
				time = last + 1;
			}

			lock (Gates) {
				Gates[bot.Name] = (gate, time);
			}

			return (gate, time);
		} catch {
			gate.Release();

			throw;
		}
	}

	/// <summary>Steam's clock, in unix seconds. Re-asked every hour; this PC's clock until the first answer.</summary>
	public static async Task<long> SteamTimeAsync(CancellationToken ct = default) {
		if (DateTime.UtcNow - _alignedAt > TimeSpan.FromHours(1)) {
			await AlignAsync(ct).ConfigureAwait(false);
		}

		return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _offset;
	}

	/// <summary>Steam's clock without waiting for a network answer - for drawing a code every second.</summary>
	public static long SteamTimeNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _offset;

	private static async Task AlignAsync(CancellationToken ct) {
		if (!await AlignGate.WaitAsync(0, ct).ConfigureAwait(false)) {
			return;   // someone else is asking right now
		}

		try {
			using HttpResponseMessage r = await Plain.PostAsync("https://api.steampowered.com/ITwoFactorService/QueryTime/v1/", new FormUrlEncodedContent([new("steamid", "0")]), ct).ConfigureAwait(false);
			using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

			if (doc.RootElement.TryGetProperty("response", out JsonElement resp) && resp.TryGetProperty("server_time", out JsonElement t)
				&& long.TryParse(t.ValueKind == JsonValueKind.String ? t.GetString() : t.GetRawText(), out long server)) {
				_offset = server - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
				_alignedAt = DateTime.UtcNow;
				Log.Recovered("steamclock");
			} else {
				Log.DebugOnChange("steamclock", $"Steam's clock: QueryTime answered {(int) r.StatusCode} with no server_time - using this PC's clock", "nocat.farm");
			}
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) {
			// Retried every five minutes while it fails - the same reason once an hour is plenty.
			Log.DebugOnChange("steamclock", $"Steam's clock: couldn't ask api.steampowered.com/ITwoFactorService/QueryTime: {Log.Describe(e)}", "nocat.farm");
			_alignedAt = DateTime.UtcNow - TimeSpan.FromMinutes(55);   // try again in five minutes, not every call
		} finally {
			AlignGate.Release();
		}
	}

	/// <summary>The device id Steam knows this authenticator by: the maFile's own, else one derived from the account.</summary>
	public static string DeviceId(Bot bot) =>
		MobileAuth.ReadMaFile(MaFiles.PathFor(bot.Name)).DeviceId is { Length: > 0 } fromFile ? fromFile : MobileAuth.DeviceId(bot.SteamId);

	private static List<KeyValuePair<string, string>> Query(Bot bot, string identity, long time) => [
		new("p", DeviceId(bot)),
		new("a", bot.SteamId.ToString(CultureInfo.InvariantCulture)),
		new("k", MobileAuth.Confirmation(identity, time, Tag) ?? ""),
		new("t", time.ToString(CultureInfo.InvariantCulture)),
		new("m", "react"),
		new("tag", Tag)
	];

	private static string QueryString(IEnumerable<KeyValuePair<string, string>> q) => string.Join('&', q.Select(static p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

	/// <summary>
	/// What's waiting on this account. Ok is false with a reason when Steam wouldn't list them; a fresh list is kept a
	/// few seconds so the page, the console and the trade module asking together cost one request.
	/// </summary>
	public static async Task<(bool Ok, string Error, List<Item> Items)> ListAsync(Bot bot, CancellationToken ct = default, bool fresh = false) {
		string? identity = bot.Secrets.Identity;

		if (string.IsNullOrWhiteSpace(identity)) {
			return (false, new Said("this account's authenticator isn't in nocat.farm").ToString(), []);
		}

		if ((bot.SteamId == 0) || !bot.IsOnline || !bot.Web.Ready) {
			return (false, new Said("it isn't logged in").ToString(), []);
		}

		lock (Recent) {
			if (!fresh && Recent.TryGetValue(bot.Name, out (DateTime At, List<Item> Items) hit) && (DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(8))) {
				return (true, "", hit.Items);
			}
		}

		(SemaphoreSlim gate, long time) = await TurnAsync(bot, ct).ConfigureAwait(false);
		string? body;

		try {
			body = await bot.Web.GetAsync(new Uri(WebSession.Community, "/mobileconf/getlist?" + QueryString(Query(bot, identity, time))), ct).ConfigureAwait(false);
		} finally {
			gate.Release();
		}

		if (string.IsNullOrWhiteSpace(body)) {
			return (false, new Said("Steam didn't answer").ToString(), []);
		}

		(bool ok, string error, List<Item> items) = Parse(body);

		if (ok) {
			Log.Recovered($"conflist:{bot.Name}");

			lock (Recent) {
				Recent[bot.Name] = (DateTime.UtcNow, items);
			}
		} else {
			// Not every caller shows the reason (the trade module's own confirm drops it), and the Authenticator page
			// asks again every few seconds - so written here, once per change.
			Log.DebugOnChange($"conflist:{bot.Name}", $"confirmations list refused: {Log.Scrub(error)}", bot.Name);
		}

		return (ok, error, items);
	}

	/// <summary>getlist's JSON: {"success":true,"conf":[{"type":2,"type_name":"Trade Offer","id":"..","nonce":"..",...}]}.</summary>
	public static (bool Ok, string Error, List<Item> Items) Parse(string body) {
		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.TryGetProperty("needauth", out JsonElement needAuth) && (needAuth.ValueKind == JsonValueKind.True)) {
				return (false, new Said("Steam wants the web session signed in again - it renews by itself, try in a minute").ToString(), []);
			}

			if (!root.TryGetProperty("success", out JsonElement success) || (success.ValueKind != JsonValueKind.True)) {
				string message = root.TryGetProperty("message", out JsonElement m) && (m.ValueKind == JsonValueKind.String) ? m.GetString() ?? "" : "";

				return (false, message.Length > 0 ? message : new Said("Steam refused - the authenticator secrets or this PC's clock may be off").ToString(), []);
			}

			List<Item> items = [];

			if (root.TryGetProperty("conf", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement c in list.EnumerateArray()) {
					ulong id = Number(c, "id"), nonce = Number(c, "nonce");

					if ((id == 0) || (nonce == 0)) {
						continue;
					}

					List<string> summary = c.TryGetProperty("summary", out JsonElement s) && (s.ValueKind == JsonValueKind.Array)
						? [.. s.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.String).Select(static x => x.GetString() ?? "")]
						: [];

					items.Add(new Item(id, nonce, Number(c, "creator_id") is > 0 and var creator ? creator : Number(c, "creatorid"),
						(int) Number(c, "type"), Text(c, "type_name"), Text(c, "headline"), summary, Text(c, "icon"), (long) Number(c, "creation_time")));
				}
			}

			return (true, "", [.. items.OrderBy(static i => i.Created).ThenBy(static i => i.Id)]);
		} catch (JsonException) {
			return (false, new Said("Steam answered with something that isn't the list - usually an expired session").ToString(), []);
		}
	}

	/// <summary>Confirm (accept) or deny these. One goes through ajaxop, several through multiajaxop - both as Steam's app does.</summary>
	public static async Task<bool> ActAsync(Bot bot, IReadOnlyList<Item> items, bool accept, CancellationToken ct = default) {
		string? identity = bot.Secrets.Identity;

		if ((items.Count == 0) || string.IsNullOrWhiteSpace(identity) || !bot.Web.Ready) {
			return false;
		}

		// "allow" / "cancel" - NOT "accept" / "reject". Steam dispatches on exactly these words and silently does nothing
		// for anything else. They go in op; the signature is over the "conf" tag like every other request.
		string op = accept ? "allow" : "cancel";
		(SemaphoreSlim gate, long time) = await TurnAsync(bot, ct).ConfigureAwait(false);
		string? body;

		try {
			List<KeyValuePair<string, string>> q = [.. Query(bot, identity, time), new("op", op)];

			if (items.Count == 1) {
				q.Add(new("cid", items[0].Id.ToString(CultureInfo.InvariantCulture)));
				q.Add(new("ck", items[0].Nonce.ToString(CultureInfo.InvariantCulture)));
				body = await bot.Web.GetAsync(new Uri(WebSession.Community, "/mobileconf/ajaxop?" + QueryString(q)), ct).ConfigureAwait(false);
			} else {
				foreach (Item i in items) {
					q.Add(new("cid[]", i.Id.ToString(CultureInfo.InvariantCulture)));
					q.Add(new("ck[]", i.Nonce.ToString(CultureInfo.InvariantCulture)));
				}

				body = await bot.Web.PostPairsAsync(new Uri(WebSession.Community, "/mobileconf/multiajaxop"), q, new Uri(WebSession.Community, "/mobileconf/conf"), ct).ConfigureAwait(false);
			}
		} finally {
			gate.Release();
		}

		lock (Recent) {
			Recent.Remove(bot.Name);   // whatever was listed has changed
		}

		if (body?.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase) == true) {
			return true;
		}

		// A null body was already logged by WebSession; a refusal comes back as a 200 saying success:false.
		if (body != null) {
			Log.Debug($"confirmation {op} of {items.Count} refused: {Log.Scrub(body[..Math.Min(150, body.Length)])}", bot.Name);
		}

		return false;
	}

	/// <summary>The sign-in code for right now, on Steam's clock, and the seconds it has left.</summary>
	public static (string? Code, int SecondsLeft) Code(Bot bot) {
		long now = SteamTimeNow;

		return (MobileAuth.GenerateCode(bot.Secrets.Shared, now), 30 - (int) (now % 30));
	}

	private static ulong Number(JsonElement e, string name) {
		if (!e.TryGetProperty(name, out JsonElement v)) {
			return 0;
		}

		return v.ValueKind switch {
			JsonValueKind.Number => v.TryGetUInt64(out ulong n) ? n : 0,
			JsonValueKind.String => ulong.TryParse(v.GetString(), out ulong s) ? s : 0,
			_ => 0
		};
	}

	private static string Text(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() ?? "" : "";
}
