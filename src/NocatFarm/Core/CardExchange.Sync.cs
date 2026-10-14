using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Talking to nocat.lol for card swaps: once every 30-60 minutes this copy asks for its matches, and about every 12 hours
/// (sooner when an account is turned on or off) it sends the list of what its opted-in accounts could swap. With no
/// account opted in it says nothing at all - only, once, that its list is to be deleted.
/// </summary>
/// <remarks>
/// Sent: the random number made for this install (the same as the user count's, never logged), the app version, and per
/// opted-in account what <see cref="BuildList"/> makes plus its steamid and trade link token. No account name, password,
/// login token or anything else. Never waits on nocat.lol: no answer is one debug line, and the next try is the next
/// look's.
/// </remarks>
public static partial class CardExchange {
	public const string Endpoint = "https://nocat.lol/api/farm/swap";

	/// <summary>The biggest list nocat.lol takes is 256 KB; this leaves room.</summary>
	public const int MaxBody = 240 * 1024;

	/// <summary>A list is sent about this often (nocat.lol takes one at most every 3 hours).</summary>
	public static readonly TimeSpan UploadEvery = TimeSpan.FromHours(12);

	private static readonly Lock Gate = new();
	private static CancellationTokenSource? _stop;
	private static DateTime _next = DateTime.MaxValue;

	/// <summary>What this copy last told nocat.lol: which accounts are on its list, and when the next list is due.</summary>
	public sealed class Sent {
		public List<string> Listed { get; set; } = [];
		public long NextUpload { get; set; }
	}

	private static string SentPath => Path.Combine(ConfigStore.ConfigDir, "state", "cardswaps.json");

	internal static Sent LoadSent() {
		try {
			return File.Exists(SentPath) ? JsonSerializer.Deserialize<Sent>(File.ReadAllText(SentPath)) ?? new Sent() : new Sent();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Debug($"card swaps: couldn't read what was sent: {Log.Describe(e)}");

			return new Sent();
		}
	}

	private static void SaveSent(Sent sent) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(SentPath)!);
			AtomicFile.Write(SentPath, JsonSerializer.Serialize(sent));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug($"card swaps: couldn't save what was sent: {Log.Describe(e)}");
		}
	}

	/// <summary>Where it goes: <see cref="Endpoint"/>, or NOCATFARM_SWAP_URL when that points at this PC (a test's stand-in).</summary>
	public static string Target {
		get {
			string? over = Environment.GetEnvironmentVariable("NOCATFARM_SWAP_URL");

			return Uri.TryCreate(over, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.IsLoopback
				? u.ToString()
				: Endpoint;
		}
	}

	private static readonly Lazy<HttpClient> Http = new(static () => {
		HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(15) }, true) {
			Timeout = TimeSpan.FromSeconds(30),
			MaxResponseContentBufferSize = 2 * 1024 * 1024
		};
		http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "nocat.farm/" + Build.Version);

		return http;
	});

	public static void Start() {
		CancellationToken ct;

		lock (Gate) {
			if (_stop != null) {
				return;
			}

			_stop = new CancellationTokenSource();
			ct = _stop.Token;
			_next = DateTime.UtcNow.AddSeconds(Random.Shared.Next(60, 181));
		}

		_ = Task.Run(() => LoopAsync(ct), CancellationToken.None);
	}

	public static void Stop() {
		lock (Gate) {
			_stop?.Cancel();
		}
	}

	/// <summary>An account was turned on or off: the next sync in a minute or two, so an opt-out is deleted from nocat.lol then.</summary>
	public static void Nudge() {
		lock (Gate) {
			DateTime soon = DateTime.UtcNow.AddSeconds(Random.Shared.Next(30, 120));

			if (_next > soon) {
				_next = soon;
			}
		}
	}

	private static async Task LoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

				lock (Gate) {
					if (DateTime.UtcNow < _next) {
						continue;
					}

					_next = DateTime.UtcNow + Rng.Minutes(30, 60);
				}

				await SyncAsync(Http.Value, BotManager.Instance?.All ?? [], BuildAccountAsync, DateTime.UtcNow, ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception e) {
				Log.Debug($"card swaps: {Log.Describe(e)}");
			}
		}
	}

	/// <summary>
	/// One sync: the reports of bad offers still to go, then this copy's list when one is due, and its matches back. Nothing
	/// at all when no account is opted in and nothing is listed. Never throws for nocat.lol's sake - false and a debug line.
	/// </summary>
	/// <param name="build">Makes one account's entry, or null when it can't just now (the checks pass a stand-in).</param>
	public static async Task<bool> SyncAsync(HttpClient http, IReadOnlyCollection<Bot> bots, Func<Bot, CancellationToken, Task<JsonObject?>> build, DateTime nowUtc,
		CancellationToken ct) {
		List<Bot> opted = [.. bots.Where(static b => (b.SteamId != 0) && On(b.Cfg, b.Name))];
		Sent sent = LoadSent();

		if ((opted.Count == 0) && (sent.Listed.Count == 0)) {
			Remember([]);

			return true;   // nothing on nocat.lol, nothing to ask
		}

		foreach (Bot bot in opted) {
			await ReportAsync(http, bot, ct).ConfigureAwait(false);
		}

		HashSet<string> want = [.. opted.Select(static b => b.SteamId.ToString(CultureInfo.InvariantCulture))];
		bool due = !want.SetEquals(sent.Listed) || (nowUtc.Ticks >= sent.NextUpload);
		JsonArray? acc = null;
		List<string> listing = [];
		bool skipped = false;

		if (opted.Count == 0) {
			acc = [];   // every account turned off: the list - steamids, trade links - is deleted
		} else if (due) {
			acc = [];

			foreach (Bot bot in opted.Take(MaxAccounts)) {
				JsonObject? one = null;

				try {
					one = await build(bot, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug($"card swaps: couldn't make the list: {Log.Describe(e)}", bot.Name);
				}

				if (one == null) {
					skipped = true;

					continue;
				}

				acc.Add(one);
				listing.Add(bot.SteamId.ToString(CultureInfo.InvariantCulture));
			}

			// Nothing could be read on any of them: no list this time (it would delete the one there), just the matches.
			if (acc.Count == 0) {
				acc = null;
			}
		}

		JsonObject body = new() { ["id"] = UserCount.InstallId(), ["v"] = Build.Version, ["op"] = "sync" };

		if (acc != null) {
			body["acc"] = acc;

			// nocat.lol takes 256 KB at most: past that, the games of the account listing most go, from the end.
			while (Encoding.UTF8.GetByteCount(body.ToJsonString()) > MaxBody) {
				JsonArray? games = acc.Select(static a => a?["g"] as JsonArray).Where(static g => g is { Count: > 0 }).MaxBy(static g => g!.Count);

				if (games == null) {
					break;
				}

				games.RemoveAt(games.Count - 1);
			}
		}

		try {
			using HttpRequestMessage req = new(HttpMethod.Post, Target) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8) };
			req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
			string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

			if ((resp.StatusCode != HttpStatusCode.OK) || (ParseReply(text) is not { } reply)) {
				Log.Debug($"card swaps: nocat.lol answered HTTP {(int) resp.StatusCode} - trying again later");

				if (acc != null) {
					sent.NextUpload = (nowUtc + TimeSpan.FromHours(1)).Ticks;
					SaveSent(sent);
				}

				return false;
			}

			HashSet<ulong> mine = [.. opted.Select(static b => b.SteamId)];
			Remember(reply.Matches.Where(m => mine.Contains(m.Me)));

			if (acc != null) {
				if (reply.Up) {
					sent.Listed = listing;
					sent.NextUpload = (nowUtc + (skipped ? TimeSpan.FromHours(1) : UploadEvery + TimeSpan.FromMinutes(Random.Shared.Next(-60, 61)))).Ticks;
					Log.Debug(acc.Count == 0 ? "card swaps: the list on nocat.lol is deleted" : $"card swaps: sent the list of {acc.Count} account(s)");
				} else {
					sent.NextUpload = (nowUtc + TimeSpan.FromSeconds(Math.Clamp(reply.Wait, 60, 24 * 3600))).Ticks;
				}

				SaveSent(sent);
			}

			return true;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"card swaps: nocat.lol didn't answer ({Log.Describe(e)}) - trying again later");

			return false;
		}
	}

	/// <summary>nocat.lol's answer: the matches, whether the list was taken, and how long until one would be.</summary>
	public sealed record Reply(List<Match> Matches, bool Up, int Wait);

	public static Reply? ParseReply(string text) {
		try {
			using JsonDocument doc = JsonDocument.Parse(text);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("m", out JsonElement list) || (list.ValueKind != JsonValueKind.Array)) {
				return null;
			}

			List<Match> matches = [];

			foreach (JsonElement m in list.EnumerateArray()) {
				if (ParseMatch(m) is { } match) {
					matches.Add(match);
				}
			}

			bool up = root.TryGetProperty("up", out JsonElement u) && (u.ValueKind == JsonValueKind.True);
			int wait = root.TryGetProperty("wait", out JsonElement w) && w.TryGetInt32(out int s) ? s : 0;

			return new Reply(matches, up, wait);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>One match as nocat.lol hands it over, or null when anything in it can't be read (it's skipped, never guessed at).</summary>
	private static Match? ParseMatch(JsonElement m) {
		static string S(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() ?? "" : "";
		static ulong U(string s) => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong n) ? n : 0;

		if (m.ValueKind != JsonValueKind.Object) {
			return null;
		}

		string id = S(m, "id");
		ulong me = U(S(m, "me")), you = U(S(m, "you"));
		bool send = m.TryGetProperty("send", out JsonElement sendEl) && (sendEl.ValueKind == JsonValueKind.True);
		string token = S(m, "tok");

		if ((MatchIdOf(Tag + id) == null) || (me == 0) || (you == 0) || (me == you) || !m.TryGetProperty("t", out JsonElement t) || !t.TryGetInt64(out long made)
			|| !m.TryGetProperty("p", out JsonElement rows) || (rows.ValueKind != JsonValueKind.Array) || (send && (token.Length == 0))) {
			return null;
		}

		List<Pair> pairs = [];

		foreach (JsonElement r in rows.EnumerateArray()) {
			if ((r.ValueKind != JsonValueKind.Array) || (r.GetArrayLength() != 9) || !r[0].TryGetUInt32(out uint game) || !r[4].TryGetInt32(out int giveUsd)
				|| !r[8].TryGetInt32(out int getUsd) || (r[1].ValueKind != JsonValueKind.String) || (r[2].ValueKind != JsonValueKind.String)
				|| (r[3].ValueKind != JsonValueKind.String) || (r[5].ValueKind != JsonValueKind.String) || (r[6].ValueKind != JsonValueKind.String)
				|| (r[7].ValueKind != JsonValueKind.String)) {
				return null;
			}

			Pair p = new(game, U(r[1].GetString()!), r[2].GetString()!, U(r[3].GetString()!), giveUsd, U(r[5].GetString()!), r[6].GetString()!, U(r[7].GetString()!), getUsd);

			if ((p.Give == 0) || (p.Get == 0) || (p.GiveAsset == 0) || (p.GetAsset == 0) || (p.Give == p.Get)) {
				return null;
			}

			pairs.Add(p);
		}

		return pairs.Count == 0 ? null : new Match(id, DateTimeOffset.FromUnixTimeMilliseconds(made).UtcDateTime, me, you, send, token, pairs);
	}

	/// <summary>Tells nocat.lol about the bad offers this account got and hasn't told yet. Kept for the next sync when it didn't answer.</summary>
	private static async Task ReportAsync(HttpClient http, Bot bot, CancellationToken ct) {
		Ledger ledger = Ledger.Load(bot.Name);

		foreach (string report in ledger.PendingReports()) {
			string[] parts = report.Split('|');
			bool done = false;

			try {
				JsonObject body = new() { ["id"] = UserCount.InstallId(), ["v"] = Build.Version, ["op"] = "bad", ["m"] = parts[0], ["s"] = bot.SteamId.ToString(CultureInfo.InvariantCulture) };
				using HttpRequestMessage req = new(HttpMethod.Post, Target) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8) };
				req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
				using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);

				// Taken, or nocat.lol has no such match any more: either way it's done. Anything else goes again next time.
				done = resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound;
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug($"card swaps: couldn't report a bad offer: {Log.Describe(e)}", bot.Name);
			}

			if (done) {
				ledger.Change(l => l.Reports.Remove(report));
			}
		}
	}

	/// <summary>
	/// One opted-in account's entry on the list: its trade link token, its cards (<see cref="BuildList"/>, pricing a few
	/// games whose prices are old first), and how it swaps. Null when any of it can't be read just now.
	/// </summary>
	public static async Task<JsonObject?> BuildAccountAsync(Bot bot, CancellationToken ct) {
		if (!bot.IsOnline || !bot.Web.Ready || (bot.SteamId == 0)) {
			return null;
		}

		if (await Looting.OwnTradeTokenAsync(bot, ct).ConfigureAwait(false) is not { Length: > 0 } token) {
			Log.DebugOnChange($"swaptoken:{bot.Name}", "card swaps: couldn't read its own trade link - not listed this time", bot.Name);

			return null;
		}

		if (await Inventory.ReadAsync(bot, 753, "6", ct).ConfigureAwait(false) is not { Complete: true } inventory) {
			return null;
		}

		if (await TradeOffers.ActiveAsync(bot, received: true, sent: true, ct).ConfigureAwait(false) is not { } live) {
			return null;
		}

		DateTime now = DateTime.UtcNow;
		HashSet<ulong> busy = [.. TradeOffers.Committed(live), .. Reserved(bot.Name, bot.SteamId, now)];
		List<OwnCard> cards = ReadCards(inventory);
		Rules rules = Rules.From(bot.Cfg);

		// A few games a list whose market prices are old or missing are priced first - a search page each, through the
		// market's own queue. The rest are priced on later lists; a card without a price is never paired.
		int priced = 0;

		foreach (uint game in Giveable(cards, busy, rules).Keys.Select(static k => k.Game).Distinct()) {
			if ((SparePrices.Cached(game) is { } g) && (now - g.When <= PriceFor)) {
				continue;
			}

			if ((priced++ >= 8) || !await SparePrices.PriceGameAsync(game, ct).ConfigureAwait(false)) {
				break;
			}
		}

		SpareBadges? badges = SpareBadges.Parse(await bot.Web.ApiGetAsync("IPlayerService", "GetBadges", new Dictionary<string, string> {
			["steamid"] = bot.SteamId.ToString(CultureInfo.InvariantCulture)
		}, ct).ConfigureAwait(false));

		List<GameRow> rows = BuildList(cards, busy, rules,
			usd: hash => (hash.IndexOf('-') is int dash and > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint app)
				&& (SparePrices.Cached(app) is { } g) && (now - g.When <= PriceFor) ? SparePrices.UsdCents(hash) : null,
			setSize: game => SparePrices.Cached(game)?.Items.Values.Count(static r => r.Kind == SpareKind.Card) ?? 0,
			level: game => badges?.Levels.GetValueOrDefault(game) ?? 0);

		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;
		int lossUsd = UsdLoss(MaxLossCents(bot.Cfg), currency, WalletRate.ForDeciding(currency));
		int holdDays = TradeOffers.OwnHolds.TryGetValue(bot.Name, out (TimeSpan Hold, DateTime At) own) && (now - own.At < TimeSpan.FromDays(1))
			? (int) Math.Ceiling(own.Hold.TotalDays) : -1;

		return AccountJson(bot.SteamId, token, lossUsd, bot.Cfg.CardSwapSkipHeld, holdDays, bot.CanConfirmTrades, OpenIds(bot.Name, bot.SteamId, now), rows);
	}
}
