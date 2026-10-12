using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Reading Steam's event pages and answers. Pure - no network, no clock of its own - so the checks can feed it saved pages.
/// </summary>
/// <remarks>
/// Since 2023 the Steam Awards are WebAPI calls, not the old form posts:
/// <list type="bullet">
/// <item>nominating: GET ISteamAwardsService/Nominate with input_json {category_id, nominated_id, source} - source 6 is
/// "from the games you played", what the page itself sends for a pick from that list. GetUserNominations says what's
/// already nominated, GetNominationRecommendations {category_id} the games this account played that fit a category.</item>
/// <item>voting: POST IStoreSalesService/SetVote with voteid, appid and sale_appid.</item>
/// <item>both pages carry their state in <c>#application_config</c>'s <c>data-steam_awards_config</c>: the categories
/// (<c>definitions.votes[]</c> with voteid, start_time, end_time and the finalists in app_discounts), this account's
/// votes (<c>user_votes[]</c>), the sale's app id and the nomination badge's tasks (<c>badge_progress.quests[]</c>).</item>
/// </list>
/// Voting is open while start_time &lt;= now &lt; end_time, the same test Steam's own page makes. Nominations come in the
/// weeks before: open while that year's categories are listed and their voting hasn't started yet.
/// </remarks>
public static class SaleEventPages {
	/// <summary>One award category: its id, when its voting opens and closes, and the finalists.</summary>
	public sealed record Category(int Id, DateTime? Start, DateTime? End, IReadOnlyList<uint> Finalists);

	/// <summary>What a Steam Awards page says.</summary>
	public sealed record Awards(IReadOnlyList<Category> Categories, IReadOnlyDictionary<int, uint> Votes, uint SaleAppId, IReadOnlyList<bool> Quests);

	/// <summary>Nominations open at most this long before the voting does.</summary>
	public static readonly TimeSpan NominationLead = TimeSpan.FromDays(45);

	/// <summary>The skip placeholder Steam's page sends for "no nomination" - never a real game, never nominated.</summary>
	public const uint SkipApp = 2215130;

	/// <summary>The JSON in an element's data-attribute (HTML-escaped as Steam writes it), or null.</summary>
	public static string? DataAttribute(string? html, string name) {
		if (string.IsNullOrEmpty(html)) {
			return null;
		}

		Match m = Regex.Match(html, "\\b" + Regex.Escape(name) + "=\"([^\"]*)\"", RegexOptions.CultureInvariant);

		return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
	}

	/// <summary>A Steam Awards page (nominations or voting), or null when it has no awards config - not running, or a sign-in page.</summary>
	public static Awards? ParseAwards(string? html) {
		string? json = DataAttribute(html, "data-steam_awards_config");

		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return null;
			}

			List<Category> categories = [];

			if (root.TryGetProperty("definitions", out JsonElement defs) && (defs.ValueKind == JsonValueKind.Object)
				&& defs.TryGetProperty("votes", out JsonElement votes) && (votes.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement v in votes.EnumerateArray()) {
					if (I32(v, "voteid") is not { } id || (id <= 0)) {
						continue;
					}

					List<uint> finalists = [];

					if (v.TryGetProperty("app_discounts", out JsonElement apps) && (apps.ValueKind == JsonValueKind.Array)) {
						foreach (JsonElement a in apps.EnumerateArray()) {
							if ((Int(a, "appid") is { } app) && (app > 0)) {
								finalists.Add((uint) app);
							}
						}
					}

					categories.Add(new Category(id, Time(v, "start_time"), Time(v, "end_time"), finalists));
				}
			}

			Dictionary<int, uint> mine = [];

			if (root.TryGetProperty("user_votes", out JsonElement uv) && (uv.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement v in uv.EnumerateArray()) {
					if ((I32(v, "voteid") is { } id) && (Int(v, "appid") is { } app) && (app > 0)) {
						mine[id] = (uint) app;
					}
				}
			}

			long sale = Int(root, "sale_appid") ?? Int(root, "appid") ?? 0;
			List<bool> quests = [];

			if (root.TryGetProperty("badge_progress", out JsonElement badge) && (badge.ValueKind == JsonValueKind.Object)
				&& badge.TryGetProperty("quests", out JsonElement qs) && (qs.ValueKind == JsonValueKind.Array)) {
				quests.AddRange(qs.EnumerateArray().Select(static q => q.TryGetProperty("completed", out JsonElement c) && (c.ValueKind == JsonValueKind.True)));
			}

			return new Awards(categories, mine, sale is > 0 and <= uint.MaxValue ? (uint) sale : 0, quests);
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Voting is on: the first category's voting has started and not ended - Steam's own test.</summary>
	public static bool VotingOpen(Awards? page, DateTime nowUtc) =>
		page is { Categories.Count: > 0 } && (page.Categories[0].Start is { } start) && (page.Categories[0].End is { } end) && (nowUtc >= start) && (nowUtc < end);

	/// <summary>Nominations are on: this year's categories are listed and their voting starts later, within <see cref="NominationLead"/>.</summary>
	public static bool NominationsOpen(Awards? page, DateTime nowUtc) =>
		page is { Categories.Count: > 0 } && (page.Categories.Min(static c => c.Start ?? DateTime.MinValue) is var start)
		&& (start > nowUtc) && (start - nowUtc <= NominationLead);

	/// <summary>category → nominated game, from GetUserNominations or Nominate's answer. Null when it isn't one.</summary>
	public static Dictionary<int, uint>? ParseNominations(string? json) {
		if (Response(json) is not { } body) {
			return null;
		}

		using JsonDocument _ = body;
		Dictionary<int, uint> done = [];

		if (body.RootElement.GetProperty("response").TryGetProperty("nominations", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
			foreach (JsonElement n in list.EnumerateArray()) {
				if ((I32(n, "category_id") is { } cat) && (Int(n, "appid") is { } app) && (app > 0) && (app != SkipApp)) {
					done[cat] = (uint) app;
				}
			}
		}

		return done;
	}

	/// <summary>voteid → game voted for, from SetVote's answer. Null when it isn't one.</summary>
	public static Dictionary<int, uint>? ParseVotes(string? json) {
		if (Response(json) is not { } body) {
			return null;
		}

		using JsonDocument _ = body;
		Dictionary<int, uint> done = [];

		if (body.RootElement.GetProperty("response").TryGetProperty("user_votes", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
			foreach (JsonElement v in list.EnumerateArray()) {
				if ((I32(v, "voteid") is { } id) && (Int(v, "appid") is { } app) && (app > 0)) {
					done[id] = (uint) app;
				}
			}
		}

		return done;
	}

	/// <summary>The games this account played that fit a category, with minutes played, from GetNominationRecommendations.</summary>
	public static List<(uint App, int Minutes)> ParsePlayed(string? json) {
		List<(uint, int)> played = [];

		if (Response(json) is not { } body) {
			return played;
		}

		using (body) {
			if (body.RootElement.GetProperty("response").TryGetProperty("played_app", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement a in list.EnumerateArray()) {
					if ((Int(a, "appid") is { } app) && (app > 0) && (app != SkipApp)) {
						played.Add(((uint) app, (int) Math.Clamp(Int(a, "playtime") ?? 0, 0, int.MaxValue)));
					}
				}
			}
		}

		return played;
	}

	/// <summary>
	/// When the event on a Steam sale page (Next Fest's) runs: the event in <c>data-partnereventstore</c> with its
	/// rtime32_start_time and rtime32_end_time. Null when the page has no such event.
	/// </summary>
	public static (DateTime Start, DateTime End)? ParseEventWindow(string? html) {
		string? json = DataAttribute(html, "data-partnereventstore");

		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			IEnumerable<JsonElement> events = doc.RootElement.ValueKind switch {
				JsonValueKind.Array => doc.RootElement.EnumerateArray(),
				JsonValueKind.Object => [doc.RootElement],
				_ => []
			};

			foreach (JsonElement e in events) {
				if ((Time(e, "rtime32_start_time") is { } start) && (Time(e, "rtime32_end_time") is { } end) && (end > start)) {
					return (start, end);
				}
			}
		} catch (JsonException) {
		}

		return null;
	}

	/// <summary>
	/// The game to nominate: <paramref name="mode"/> 0 the one played most, 1 any of them - always one this account played,
	/// and one not already nominated in another category while there's a choice.
	/// </summary>
	public static uint? PickNominee(IReadOnlyList<(uint App, int Minutes)> played, int mode, IReadOnlySet<uint> used, Random rng) {
		List<(uint App, int Minutes)> fresh = [.. played.Where(p => !used.Contains(p.App))];
		List<(uint App, int Minutes)> from = fresh.Count > 0 ? fresh : [.. played];

		if (from.Count == 0) {
			return null;
		}

		return mode == 1 ? from[rng.Next(from.Count)].App : from.OrderByDescending(static p => p.Minutes).ThenBy(static p => p.App).First().App;
	}

	/// <summary>The finalist to vote for: the one this account played most, or a random one when it played none of them.</summary>
	public static uint? PickVote(IReadOnlyList<uint> finalists, Func<uint, int> minutesOn, Random rng) {
		if (finalists.Count == 0) {
			return null;
		}

		(uint App, int Minutes) best = finalists.Select(a => (a, minutesOn(a))).OrderByDescending(static p => p.Item2).First();

		return best.Minutes > 0 ? best.App : finalists[rng.Next(finalists.Count)];
	}

	private static JsonDocument? Response(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			JsonDocument doc = JsonDocument.Parse(json);

			if ((doc.RootElement.ValueKind == JsonValueKind.Object) && doc.RootElement.TryGetProperty("response", out JsonElement r) && (r.ValueKind == JsonValueKind.Object)) {
				return doc;
			}

			doc.Dispose();
		} catch (JsonException) {
		}

		return null;
	}

	private static long? Int(JsonElement e, string name) {
		if ((e.ValueKind != JsonValueKind.Object) || !e.TryGetProperty(name, out JsonElement v)) {
			return null;
		}

		return v.ValueKind switch {
			JsonValueKind.Number when v.TryGetInt64(out long n) => n,
			JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n,
			_ => null
		};
	}

	private static int? I32(JsonElement e, string name) => Int(e, name) is { } n && (n is >= int.MinValue and <= int.MaxValue) ? (int) n : null;

	private static DateTime? Time(JsonElement e, string name) => Int(e, name) is { } unix && (unix > 0) && (unix < 32503680000)
		? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
		: null;
}

/// <summary>What the sale events need from Steam - a page, and the Web API. The module's comes from the account; the checks' is a fake.</summary>
public interface ISaleWeb {
	/// <summary>A store page by its path, e.g. "/steamawards". Null when it didn't load.</summary>
	Task<string?> PageAsync(string path, CancellationToken ct);

	Task<string?> ApiGetAsync(string service, string method, Dictionary<string, string>? args, CancellationToken ct);

	Task<string?> ApiPostAsync(string service, string method, Dictionary<string, string> form, CancellationToken ct);
}

/// <summary>The account's own web session.</summary>
internal sealed class BotSaleWeb(Bot bot) : ISaleWeb {
	public Task<string?> PageAsync(string path, CancellationToken ct) => bot.Web.GetAsync(new Uri(WebSession.Store, path), ct);

	public Task<string?> ApiGetAsync(string service, string method, Dictionary<string, string>? args, CancellationToken ct) => bot.Web.ApiGetAsync(service, method, args, ct);

	public Task<string?> ApiPostAsync(string service, string method, Dictionary<string, string> form, CancellationToken ct) => bot.Web.ApiPostAsync(service, method, form, ct);
}

/// <summary>
/// The Steam Awards and Next Fest for one account: looks at what's running, and nominates or votes - one step at a time,
/// so human mode can space the steps out. Everything it decides goes through here; the module only adds the timing.
/// </summary>
public sealed class SaleEventsEngine(ISaleWeb web, Func<BotConfig> cfg, Func<uint, int> minutesOn, Random rng) {
	/// <summary>Nominate source: picked from "games you played" on the nominations page.</summary>
	public const int SourcePlayed = 6;

	public SaleEventPages.Awards? Nominations { get; private set; }
	public SaleEventPages.Awards? Voting { get; private set; }
	public bool NominationsOpen { get; private set; }
	public bool VotingOpen { get; private set; }
	public (DateTime Start, DateTime End)? NextFest { get; private set; }
	public bool NextFestOn { get; private set; }

	/// <summary>What Steam says is nominated (category → game), or null before the first look.</summary>
	public Dictionary<int, uint>? Nominated { get; private set; }

	/// <summary>What Steam says is voted (category → game).</summary>
	public Dictionary<int, uint> Voted { get; private set; } = [];

	/// <summary>Categories Steam wouldn't take a nomination or a vote for this time round - not asked again until the next look.</summary>
	private readonly HashSet<string> _refused = [];

	public DateTime LookedAt { get; private set; } = DateTime.MinValue;

	/// <summary>Whether Steam answered the last look at all - so "nothing running" isn't said when nothing was read.</summary>
	public bool LookFailed { get; private set; }

	/// <summary>A refusal that ended this round early (e.g. a limited account), said once in the status.</summary>
	public string? Refusal { get; private set; }

	/// <summary>Read what's running. Only the pages for the kinds that are switched on.</summary>
	public async Task LookAsync(DateTime nowUtc, CancellationToken ct) {
		BotConfig c = cfg();
		_refused.Clear();
		Refusal = null;
		LookFailed = false;
		LookedAt = nowUtc;

		if (c.SaleAwardsNominate) {
			string? page = await web.PageAsync("/steamawards/nominations", ct).ConfigureAwait(false);
			LookFailed |= page == null;
			Nominations = SaleEventPages.ParseAwards(page);
			NominationsOpen = SaleEventPages.NominationsOpen(Nominations, nowUtc);

			if (NominationsOpen) {
				string? mine = await web.ApiGetAsync("ISteamAwardsService", "GetUserNominations", null, ct).ConfigureAwait(false);
				Nominated = SaleEventPages.ParseNominations(mine) ?? Nominated;
				LookFailed |= mine == null;
			}
		} else {
			Nominations = null;
			NominationsOpen = false;
		}

		if (c.SaleAwardsVote) {
			string? page = await web.PageAsync("/steamawards", ct).ConfigureAwait(false);
			LookFailed |= page == null;
			Voting = SaleEventPages.ParseAwards(page);
			VotingOpen = SaleEventPages.VotingOpen(Voting, nowUtc);
			Voted = Voting != null ? new Dictionary<int, uint>(Voting.Votes) : [];
		} else {
			Voting = null;
			VotingOpen = false;
		}

		if (c.SaleNextFest) {
			string? page = await web.PageAsync("/sale/nextfest", ct).ConfigureAwait(false);
			NextFest = SaleEventPages.ParseEventWindow(page);
			NextFestOn = NextFest is { } w && (nowUtc >= w.Start) && (nowUtc < w.End);
		} else {
			NextFest = null;
			NextFestOn = false;
		}
	}

	/// <summary>Categories still to nominate in, in the page's order.</summary>
	public IEnumerable<int> NominationsLeft() => !NominationsOpen || !cfg().SaleAwardsNominate || (Nominations == null) || (Nominated == null)
		? []
		: Nominations.Categories.Select(static c => c.Id).Where(id => !Nominated.ContainsKey(id) && !_refused.Contains("n" + id));

	/// <summary>Categories still to vote in, with finalists to vote for.</summary>
	public IEnumerable<SaleEventPages.Category> VotesLeft() => !VotingOpen || !cfg().SaleAwardsVote || (Voting == null) || (Voting.SaleAppId == 0)
		? []
		: Voting.Categories.Where(c => (c.Finalists.Count > 0) && !Voted.ContainsKey(c.Id) && !_refused.Contains("v" + c.Id));

	public bool AnythingLeft => NominationsLeft().Any() || VotesLeft().Any();

	/// <summary>
	/// One nomination or one vote. Returns what was done (for the log and 'events'), or null when there was nothing to do
	/// or Steam didn't take it.
	/// </summary>
	public async Task<Said?> StepAsync(CancellationToken ct) {
		foreach (int category in NominationsLeft().Take(1)) {
			return await NominateAsync(category, ct).ConfigureAwait(false);
		}

		foreach (SaleEventPages.Category category in VotesLeft().Take(1)) {
			return await VoteAsync(category, ct).ConfigureAwait(false);
		}

		return null;
	}

	private async Task<Said?> NominateAsync(int category, CancellationToken ct) {
		string ask = JsonSerializer.Serialize(new { category_id = category });
		List<(uint App, int Minutes)> played = SaleEventPages.ParsePlayed(await web.ApiGetAsync("ISteamAwardsService", "GetNominationRecommendations",
			new Dictionary<string, string> { ["input_json"] = ask }, ct).ConfigureAwait(false));

		HashSet<uint> used = Nominated == null ? [] : [.. Nominated.Values];

		if (SaleEventPages.PickNominee(played, cfg().SaleNominatePick, used, rng) is not { } app) {
			// Nothing it played fits this category: left empty rather than a game it never touched.
			_refused.Add("n" + category);

			return null;
		}

		string body = JsonSerializer.Serialize(new { category_id = category, nominated_id = app, source = SourcePlayed });
		Dictionary<int, uint>? after = SaleEventPages.ParseNominations(await web.ApiGetAsync("ISteamAwardsService", "Nominate",
			new Dictionary<string, string> { ["input_json"] = body }, ct).ConfigureAwait(false));

		if ((after == null) || !after.TryGetValue(category, out uint took) || (took != app)) {
			_refused.Add("n" + category);
			Refusal ??= "nomination";

			return null;
		}

		// Merged in, like the votes: an answer with only the new nomination left the earlier ones looking undone, and they
		// were nominated again and again.
		Nominated ??= [];

		foreach ((int id, uint nominated) in after) {
			Nominated[id] = nominated;
		}

		return new Said("nominated {0} for a Steam Award", GameNames.Of(app));
	}

	private async Task<Said?> VoteAsync(SaleEventPages.Category category, CancellationToken ct) {
		if (SaleEventPages.PickVote(category.Finalists, minutesOn, rng) is not { } app) {
			_refused.Add("v" + category.Id);

			return null;
		}

		Dictionary<int, uint>? after = SaleEventPages.ParseVotes(await web.ApiPostAsync("IStoreSalesService", "SetVote", new Dictionary<string, string> {
			["voteid"] = category.Id.ToString(CultureInfo.InvariantCulture),
			["appid"] = app.ToString(CultureInfo.InvariantCulture),
			["sale_appid"] = Voting!.SaleAppId.ToString(CultureInfo.InvariantCulture)
		}, ct).ConfigureAwait(false));

		if ((after == null) || !after.TryGetValue(category.Id, out uint took) || (took != app)) {
			_refused.Add("v" + category.Id);
			Refusal ??= "vote";

			return null;
		}

		foreach ((int id, uint voted) in after) {
			Voted[id] = voted;
		}

		return new Said("voted for {0} in the Steam Awards", GameNames.Of(app));
	}
}

/// <summary>
/// Steam events: the Steam Awards (nominations in late November, votes in the winter sale) and Steam Next Fest, next to
/// the daily sticker and the discovery queue that <see cref="EventItems"/> does - all under the "Sale events" settings.
/// </summary>
/// <remarks>
/// Does nothing outside an event: it looks at the event pages every few hours (SaleCheckHours) and only acts while one
/// is on. A robot does everything straight away, a few seconds apart. A human-mode account takes part only with
/// "Human-mode accounts too" on (off by default), one step at a time inside its own day, minutes apart.
/// </remarks>
public sealed class SaleEvents(Bot bot) : BotModule(bot) {
	private readonly SaleEventsEngine _engine = new(new BotSaleWeb(bot), () => bot.Cfg, app => bot.Library.MinutesOn(app), new Random());
	private DateTime _nextLook = DateTime.MinValue;
	private HumanGate? _gate;
	private Said _status = new("");
	private readonly List<(DateTime When, Said What)> _done = [];

	public override string Name => "sale events";
	public override string Status => _status;

	public SaleEventsEngine Engine => _engine;

	/// <summary>What it did this run, newest last - for 'events'.</summary>
	public IReadOnlyList<(DateTime When, Said What)> Done {
		get {
			lock (_done) {
				return [.. _done];
			}
		}
	}

	/// <summary>Whether this account takes part in the awards, the sale's extras and Next Fest: always for a robot, for a human-mode account only when allowed.</summary>
	public static bool Allowed(BotConfig c) => c.SaleEvents && (!c.LegitMode || c.SaleEventsHumanToo);

	/// <summary>Next Fest is on and its free item may be claimed - read by the sticker claim in <see cref="EventItems"/>.</summary>
	public static bool NextFestClaims(Bot bot) =>
		Allowed(bot.Cfg) && bot.Cfg.SaleNextFest && (BotManager.ModuleOf<SaleEvents>(bot)?.Engine.NextFestOn ?? false);

	/// <summary>The gap between two steps: seconds on a robot, the human gap on a human-mode account.</summary>
	public static (int Lo, int Hi) Gap(BotConfig c, bool human) {
		(int lo, int hi) = human ? (c.SaleHumanGapMinMinutes, c.SaleHumanGapMaxMinutes) : (c.SaleGapMinSeconds, c.SaleGapMaxSeconds);
		lo = Math.Max(0, lo);

		return (lo, Math.Max(lo, hi));
	}

	protected override async Task RunAsync(CancellationToken ct) {
		// Not in the first minutes after signing in, with everything else.
		if (!await Sleep(Rng.Minutes(3, 8), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			BotConfig c = Bot.Cfg;
			_gate ??= HumanGate.OwnDay(Bot);

			if (!c.SaleEvents) {
				_status = new Said("");
			} else if (!Allowed(c)) {
				_status = new Said("Steam events: off on this human-mode account");
			} else if (Bot.IsOnline && Bot.Web.Ready && !Bot.Paused) {
				try {
					await TurnAsync(c, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug($"sale events: {Log.Describe(e)}", Bot.Name);
					_nextLook = DateTime.UtcNow + Rng.Minutes(30, 60);
				}
			}

			if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private async Task TurnAsync(BotConfig c, CancellationToken ct) {
		if (DateTime.UtcNow >= _nextLook) {
			await _engine.LookAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
			bool on = _engine.NominationsOpen || _engine.VotingOpen || _engine.NextFestOn;

			// While something is on, a fresh look every hour or so (it changes); otherwise every few hours.
			_nextLook = DateTime.UtcNow + (on ? Rng.Minutes(45, 90) : TimeSpan.FromHours(Math.Clamp(c.SaleCheckHours, 1, 48)) + Rng.Minutes(0, 20));
		}

		bool human = c.LegitMode;

		if (_engine.AnythingLeft && _gate!.Open) {
			List<Said> did = await DoStepsAsync(_engine, human, Gap(c, human), static (wait, token) => Task.Delay(wait, token),
				() => Bot.IsOnline && !Bot.Paused, ct).ConfigureAwait(false);

			foreach (Said one in did) {
				Log.Reward(one, Bot.Name, topic: Topic.FreeStuff);

				lock (_done) {
					_done.Add((DateTime.UtcNow, one));

					if (_done.Count > 40) {
						_done.RemoveAt(0);
					}
				}
			}

			if (human) {
				(int lo, int hi) = Gap(c, human);
				_gate.Space(lo, hi);
			}
		}

		_status = Summary();
	}

	/// <summary>
	/// The steps due now: on a robot all of them, <paramref name="gap"/> seconds apart (picked at random in between); on a
	/// human-mode account just one - the gate spaces out the next by the human gap, in minutes.
	/// </summary>
	public static async Task<List<Said>> DoStepsAsync(SaleEventsEngine engine, bool human, (int Lo, int Hi) gap,
		Func<TimeSpan, CancellationToken, Task> wait, Func<bool> mayGoOn, CancellationToken ct) {
		List<Said> did = [];
		int tries = 0;

		while (engine.AnythingLeft && mayGoOn() && !ct.IsCancellationRequested && (tries++ < 100)) {
			if (await engine.StepAsync(ct).ConfigureAwait(false) is { } one) {
				did.Add(one);
			}

			if (human || !engine.AnythingLeft) {
				break;
			}

			await wait(TimeSpan.FromSeconds(Rng.Next(gap.Lo, gap.Hi + 1)), ct).ConfigureAwait(false);
		}

		return did;
	}

	/// <summary>The account card's line: what's on and how far it got, or nothing when no event is on.</summary>
	private Said Summary() {
		if (_engine.NominationsOpen && (_engine.Nominations is { } n)) {
			return new Said("Steam Awards nominations: {0} of {1} done", _engine.Nominated?.Count ?? 0, n.Categories.Count);
		}

		if (_engine.VotingOpen && (_engine.Voting is { } v)) {
			return new Said("Steam Awards votes: {0} of {1} done", _engine.Voted.Count, v.Categories.Count);
		}

		return _engine.NextFestOn ? new Said("Steam Next Fest is on") : new Said("");
	}

	private const string Indent = "  ";

	/// <summary>The 'events' command's lines for this account.</summary>
	public List<string> Report() {
		BotConfig c = Bot.Cfg;
		List<string> lines = [];
		string name = Bot.Name;

		if (!c.SaleEvents) {
			lines.Add(new Said("{0}: Steam events are off for this account (Sale events → Take part in Steam events)", name));

			return lines;
		}

		EventItems? items = BotManager.ModuleOf<EventItems>(Bot);
		lines.Add(items?.SaleOn ?? false ? new Said("{0}: a Steam sale is on", name) : new Said("{0}: no Steam sale seen right now", name));

		if (c.ClaimEventItems) {
			lines.Add(Indent + new Said("daily sticker: {0}", items?.StickerLine ?? new Said("not looked yet")));
		}

		if (c.DiscoveryQueue > 0) {
			lines.Add(items?.QueueDoneToday ?? false ? Indent + new Said("discovery queue: done today") : Indent + new Said("discovery queue: not done today"));
		}

		if (!Allowed(c)) {
			lines.Add(Indent + new Said("Steam Awards and Next Fest: off on this human-mode account (Sale events → Human-mode accounts too)"));

			return lines;
		}

		if (_engine.LookedAt == DateTime.MinValue) {
			lines.Add(Indent + new Said("Steam Awards and Next Fest: not looked yet - it looks a few minutes after signing in"));

			return lines;
		}

		if (c.SaleAwardsNominate) {
			lines.Add(_engine.NominationsOpen
				? Indent + new Said("Steam Awards nominations: open - {0} of {1} categories done", _engine.Nominated?.Count ?? 0, _engine.Nominations?.Categories.Count ?? 0)
				: Indent + new Said("Steam Awards nominations: not running"));
		}

		if (c.SaleAwardsVote) {
			lines.Add(_engine.VotingOpen
				? Indent + new Said("Steam Awards votes: open - {0} of {1} categories done", _engine.Voted.Count, _engine.Voting?.Categories.Count ?? 0)
				: Indent + new Said("Steam Awards votes: not running"));
		}

		if (c.SaleNextFest) {
			lines.Add(_engine.NextFestOn && (_engine.NextFest is { } w)
				? Indent + new Said("Steam Next Fest: on until {0}", Fmt.Clock(w.End))
				: Indent + new Said("Steam Next Fest: not on"));
		}

		if (_engine.Refusal != null) {
			lines.Add(Indent + new Said("Steam didn't take a nomination or vote - limited accounts (never spent $5) can't take part; it tries again at the next look"));
		}

		if (_engine.VotingOpen && (_engine.Voting is { SaleAppId: 0 })) {
			lines.Add(Indent + new Said("Steam Awards votes: it can't tell which sale they belong to, so it isn't voting"));
		}

		if (_engine.LookFailed) {
			lines.Add(Indent + new Said("Steam didn't answer every look - it tries again later"));
		}

		lines.Add(Indent + new Said("last look {0}", Fmt.Clock(_engine.LookedAt)));

		foreach ((DateTime when, Said what) in Done.TakeLast(5)) {
			lines.Add($"{Indent}{Fmt.Clock(when)}  {what}");
		}

		return lines;
	}
}
