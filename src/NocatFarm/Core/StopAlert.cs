using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// "Tell me if nocat.farm stops": while the switch is on, check in with nocat.lol every few minutes, so the site's
/// Telegram bot can say when this copy has gone quiet - it crashed, the PC is off or offline. A clean quit (or an
/// update restart) says goodbye first, so closing it on purpose never sends an alert.
/// </summary>
/// <remarks>
/// A check-in carries ONE thing: a random 128-bit number made for this install the first time it's needed. No
/// account names, no Steam IDs, nothing from the config - the site can't tell whose it is. The name the message uses
/// ("Call this PC") and the minutes go up only when they change, typed by the user for exactly that.
///
/// Linking is the site's: it hands out a code, the user opens t.me/&lt;bot&gt;?start=&lt;code&gt; and presses Start, and the
/// bot ties that code's install to the chat. From then on a ping answers linked: true.
/// </remarks>
public static class StopAlert {
	/// <summary>Where the site answers. A property so the tests can point it at nothing.</summary>
	internal static string Site { get; set; } = "https://nocat.lol/api/nocatfarm/alive/";

	internal static HttpClient Http { get; set; } = NewHttp();

	/// <summary>True once the site says a Telegram chat is linked; false when it says none is; null before it's asked.</summary>
	public static bool? Linked { get; private set; }

	public static DateTime? LastCheckIn { get; private set; }

	/// <summary>Why the last try failed, as a short code the dashboard translates: unreachable, notsetup, site, busy.</summary>
	public static string? Problem { get; private set; }

	/// <summary>The Telegram link from Connect, while it still works and nothing is linked yet.</summary>
	public static string? LinkUrl => (_link != null) && (DateTime.UtcNow < _linkUntil) && (Linked != true) ? _link : null;

	internal static int Failures { get; private set; }

	private static string? _link;
	private static DateTime _linkUntil;

	/// <summary>The name/zone/minutes the site last took, so they're only sent again when one changes.</summary>
	private static string? _sentCfg;

	/// <summary>Pinged while linked - so switching off or quitting owes the site a goodbye.</summary>
	private static bool _owesBye;

	private static bool _seenOn;
	private static bool _stopping;
	private static DateTime _lastPingAt = DateTime.MinValue;
	private static DateTime _nextPingAt = DateTime.MinValue;

	private static readonly SemaphoreSlim Wake = new(0, 1);

	/// <summary>One ping at a time, so the goodbye on the way out can't be overtaken by a check-in still in flight.</summary>
	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static readonly Lock IdLock = new();
	private static (string Path, string Id)? _id;

	/// <summary>The site refuses a second ping from one install inside a minute.</summary>
	private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(61);

	private static HttpClient NewHttp() {
		HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"nocat.farm/{Build.Version}");

		return http;
	}

	private static string IdPath => Path.Combine(ConfigStore.ConfigDir, "state", "stop-alert-id.txt");

	/// <summary>This install's random id: 32 hex characters, made once and kept in the state folder.</summary>
	internal static string InstallId {
		get {
			lock (IdLock) {
				string path = IdPath;

				if (_id is { } known && (known.Path == path)) {
					return known.Id;
				}

				string id = "";

				try {
					id = File.Exists(path) ? File.ReadAllText(path).Trim().ToLowerInvariant() : "";
				} catch (IOException) {
					// made again below; the old link then just stops answering linked
				}

				if (!IsId(id)) {
					id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

					try {
						Directory.CreateDirectory(Path.GetDirectoryName(path)!);
						AtomicFile.Write(path, id);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						Log.DebugOnChange("stopalert:id", $"stop alert: couldn't save this PC's id ({Log.Describe(e)}) - a new one next start");
					}
				}

				_id = (path, id);

				return id;
			}
		}
	}

	internal static bool IsId(string s) => (s.Length == 32) && s.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

	internal static string PcName(GlobalConfig g) {
		string name = (g.StopAlertPcName ?? "").Trim();

		return name.Length == 0 ? "my PC" : name.Length > 40 ? name[..40] : name;
	}

	internal static int After(GlobalConfig g) => Math.Clamp(g.StopAlertMinutes, 10, 1440);

	/// <summary>This PC's offset from UTC right now, in minutes - so "since 14:05" is the user's own 14:05.</summary>
	internal static int TzMinutes() => (int) TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;

	private static string CfgSignature(GlobalConfig g) => $"{PcName(g)}|{TzMinutes()}|{After(g)}";

	/// <summary>
	/// What a check-in sends: the install id, "bye" on the way out, and the three things the user typed for the
	/// message only when they changed. Nothing else, ever.
	/// </summary>
	internal static string PingBody(string id, bool bye, GlobalConfig? cfg) {
		JsonObject body = new() { ["id"] = id };

		if (bye) {
			body["bye"] = true;
		}

		if (cfg != null) {
			body["cfg"] = new JsonObject { ["name"] = PcName(cfg), ["tz"] = TzMinutes(), ["after"] = After(cfg) };
		}

		return body.ToJsonString();
	}

	/// <summary>
	/// How long until the next check-in. The site's own "next" when it went fine (kept to 1-15 minutes); a minute while
	/// waiting for Start to be pressed, so "linked" shows up quickly; on failure 1, 2, 4, 8, then 15 minutes; and when
	/// told to slow down, what it asked for. On but not linked (Connect never finished, or /stop in Telegram): every
	/// half hour, since there's nobody to tell - it only keeps the dashboard's "not linked" honest.
	/// </summary>
	internal static TimeSpan NextDelay(bool ok, int failures, int? next, int? retryAfter, bool waitingForStart, bool linked = true) {
		if (retryAfter is > 0) {
			return TimeSpan.FromSeconds(Math.Clamp(retryAfter.Value, 61, 3600));
		}

		if (!ok) {
			return TimeSpan.FromSeconds(Math.Min(900, 60 * (1 << Math.Clamp(failures - 1, 0, 4))));
		}

		return waitingForStart ? MinGap
			: !linked ? TimeSpan.FromMinutes(30)
			: TimeSpan.FromSeconds(Math.Clamp(next ?? 300, 61, 900));
	}

	// ── the loop ─────────────────────────────────────────────────────────────

	public static void Start() => Background.Loop(new Said("the stop alert"), LoopAsync);

	/// <summary>Look again now - the switch or a setting changed, or Connect was pressed.</summary>
	public static void Poke() {
		try {
			Wake.Release();
		} catch (SemaphoreFullException) {
			// already woken
		}
	}

	private static async Task LoopAsync() {
		while (!_stopping) {
			GlobalConfig g = Live.Global;
			DateTime now = DateTime.UtcNow;
			TimeSpan wait;

			if (!g.StopAlert) {
				// Switched off while linked: say goodbye, so switching it off isn't reported as a crash 20 minutes later.
				if (_owesBye) {
					await PingOnceAsync(true, CancellationToken.None).ConfigureAwait(false);
					_owesBye = false;
				}

				_seenOn = false;
				wait = TimeSpan.FromSeconds(30);
			} else {
				bool due = !_seenOn || (now >= _nextPingAt) || ((Linked == true) && (CfgSignature(g) != _sentCfg));

				if (due && (now - _lastPingAt < MinGap)) {
					wait = _lastPingAt + MinGap - now;
				} else if (due) {
					_seenOn = true;
					_lastPingAt = now;
					(bool ok, int? next, int? retryAfter) = await PingOnceAsync(false, CancellationToken.None).ConfigureAwait(false);
					_nextPingAt = DateTime.UtcNow + NextDelay(ok, Failures, next, retryAfter, LinkUrl != null, Linked == true);
					wait = _nextPingAt - DateTime.UtcNow;
				} else {
					wait = _nextPingAt - now;
				}
			}

			try {
				await Wake.WaitAsync(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait).ConfigureAwait(false);
			} catch (ObjectDisposedException) {
				return;
			}
		}
	}

	/// <summary>
	/// On the way out: tell the site this is on purpose. A few seconds at most - a quit is never held up by it. An
	/// update restart goes through here too, and the new copy's first check-in follows within a minute or so.
	/// </summary>
	/// <param name="quitting">True on the way out: the loop stops too. False for 'alert off', which says goodbye and
	/// lets the loop notice the switch.</param>
	public static async Task ByeAsync(bool quitting = true) {
		_stopping |= quitting;

		if (!Live.Global.StopAlert || !_owesBye) {
			return;
		}

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(4));

		try {
			await PingOnceAsync(true, cts.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// out of time - the site alerts after the user's minutes, the one thing a goodbye can't prevent
		}

		_owesBye = false;
	}

	/// <summary>One check-in. What happened goes into Linked, LastCheckIn, Problem and Failures.</summary>
	internal static async Task<(bool Ok, int? Next, int? RetryAfter)> PingOnceAsync(bool bye, CancellationToken ct) {
		await Gate.WaitAsync(ct).ConfigureAwait(false);

		try {
			GlobalConfig g = Live.Global;
			string sig = CfgSignature(g);
			bool withCfg = !bye && (_sentCfg != sig);
			Answer a = await PostAsync("ping", PingBody(InstallId, bye, withCfg ? g : null), ct).ConfigureAwait(false);

			if (a.Status == 429) {
				// Pinged too soon (a restart inside a minute): alive all the same, so not a failure.
				return (false, null, a.RetryAfter ?? 61);
			}

			if ((a.Status != 200) || (a.Json == null)) {
				return Failed(a.Status == 0 ? "unreachable" : "site", a.Error ?? $"HTTP {a.Status}");
			}

			bool linked = Bool(a.Json, "linked");

			if ((Linked == true) && !linked) {
				Log.Info(new Said("stop alert: Telegram is no longer linked - press Connect to link it again"));
			} else if ((Linked != true) && linked && (_link != null)) {
				Log.Info(new Said("stop alert: linked - Telegram will tell you if nocat.farm stops"));
			}

			Linked = linked;
			LastCheckIn = DateTime.UtcNow;
			Problem = null;
			Failures = 0;
			Log.Recovered("stopalert:ping");

			if (linked) {
				_link = null;
				_owesBye = !bye;

				if (withCfg) {
					_sentCfg = sig;
				}
			} else {
				_owesBye = false;
				_sentCfg = null;   // not stored anywhere yet: send it again once it's linked
			}

			return (true, Int(a.Json, "next"), null);
		} finally {
			Gate.Release();
		}
	}

	private static (bool, int?, int?) Failed(string problem, string why) {
		Failures++;
		Problem = problem;
		Log.DebugOnChange("stopalert:ping", $"stop alert: couldn't check in with nocat.lol ({why})");

		return (false, null, null);
	}

	// ── what the dashboard and the 'alert' command ask for ──────────────────

	/// <summary>A fresh Telegram link to press Start on. Error codes: notsetup, busy, site, unreachable.</summary>
	public static async Task<(string? Link, string? Error)> LinkAsync(CancellationToken ct = default) {
		GlobalConfig g = Live.Global;
		JsonObject body = new() { ["id"] = InstallId, ["name"] = PcName(g), ["tz"] = TzMinutes(), ["after"] = After(g) };
		Answer a = await PostAsync("link", body.ToJsonString(), ct).ConfigureAwait(false);

		if ((a.Status == 200) && (Str(a.Json, "link") is { } link) && link.StartsWith("https://t.me/", StringComparison.Ordinal)) {
			_link = link;
			_linkUntil = DateTime.UtcNow.AddMinutes(Math.Clamp(Int(a.Json, "minutes") ?? 15, 1, 60));

			if (Linked == true) {
				Linked = null;   // a new chat is about to take over; the next check-in says which
			}

			_nextPingAt = DateTime.MinValue;   // look every minute from now on, so "linked" shows soon after Start
			Poke();

			return (link, null);
		}

		return (null, ErrorOf(a));
	}

	/// <summary>Ask the site to send a test message. Codes: sent, notlinked, telegram, busy, notsetup, site, unreachable.</summary>
	public static async Task<string> TestAsync(CancellationToken ct = default) {
		Answer a = await PostAsync("test", PingBody(InstallId, false, null), ct).ConfigureAwait(false);

		return (a.Status == 200) && Bool(a.Json, "sent") ? "sent"
			: a.Status == 404 ? "notlinked"
			: a.Status == 502 ? "telegram"
			: ErrorOf(a);
	}

	/// <summary>Unlink this PC from its Telegram chat. Codes: done, busy, site, unreachable.</summary>
	public static async Task<string> UnlinkAsync(CancellationToken ct = default) {
		Answer a = await PostAsync("unlink", PingBody(InstallId, false, null), ct).ConfigureAwait(false);

		if (a.Status != 200) {
			return ErrorOf(a);
		}

		Linked = false;
		_owesBye = false;
		_sentCfg = null;
		_link = null;

		return "done";
	}

	private static string ErrorOf(Answer a) => a.Status switch {
		0 => "unreachable",
		429 => "busy",
		503 => "notsetup",
		_ => "site"
	};

	/// <summary>A problem or result code in plain English, for the console.</summary>
	internal static string Explain(string? code) => code switch {
		"sent" => "sent - check Telegram",
		"done" => "unlinked - no more alerts for this PC",
		"notlinked" => "not linked yet - 'alert link' gives the Telegram link",
		"telegram" => "Telegram didn't take the message - was the bot blocked, or the chat deleted? 'alert link' links it again",
		"busy" => "nocat.lol asked to slow down - try again in a minute",
		"notsetup" => "alerts aren't switched on at nocat.lol yet - try again later",
		"unreachable" => "couldn't reach nocat.lol - is this PC online?",
		"site" => "nocat.lol gave an unexpected answer - try again later",
		_ => code ?? ""
	};

	// ── HTTP ─────────────────────────────────────────────────────────────────

	private sealed record Answer(int Status, JsonObject? Json, int? RetryAfter, string? Error);

	private static async Task<Answer> PostAsync(string what, string body, CancellationToken ct) {
		try {
			using HttpRequestMessage req = new(HttpMethod.Post, Site + what) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
			using HttpResponseMessage res = await Http.SendAsync(req, ct).ConfigureAwait(false);
			string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			JsonObject? json = null;

			try {
				json = JsonNode.Parse(text) as JsonObject;
			} catch (JsonException) {
				// not JSON - a proxy's error page; the status says enough
			}

			int? retry = res.Headers.RetryAfter?.Delta is { } d ? (int) d.TotalSeconds : null;

			return new Answer((int) res.StatusCode, json, retry, null);
		} catch (HttpRequestException e) {
			return new Answer(0, null, null, Log.Describe(e));
		} catch (TaskCanceledException e) when (!ct.IsCancellationRequested) {
			// HttpClient's own timeout - not a cancel anyone asked for, and it must not end the loop
			return new Answer(0, null, null, Log.Describe(e));
		}
	}

	private static bool Bool(JsonObject? o, string key) => (o?[key] is JsonValue v) && v.TryGetValue(out bool b) && b;

	private static int? Int(JsonObject? o, string key) => (o?[key] is JsonValue v) && v.TryGetValue(out int i) ? i : null;

	private static string? Str(JsonObject? o, string key) => (o?[key] is JsonValue v) && v.TryGetValue(out string? s) ? s : null;

	/// <summary>For the tests: forget everything learned this run.</summary>
	internal static void ResetForTests() {
		Linked = null;
		LastCheckIn = null;
		Problem = null;
		Failures = 0;
		_link = null;
		_sentCfg = null;
		_owesBye = false;
		_stopping = false;
		_seenOn = false;
		_lastPingAt = DateTime.MinValue;
		_nextPingAt = DateTime.MinValue;
	}
}
