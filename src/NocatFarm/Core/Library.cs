using System.Text.Json;
using SteamKit2;

namespace NocatFarm.Core;

/// <summary>
/// What this account can actually play, and how much of it has already been played.
///
/// Two Steam services answer that: <c>IPlayerService/GetOwnedGames</c> for the library, with playtime per game,
/// and <c>IFamilyGroupsService</c> for anything shared into it by a Steam Family. Both are asked over the web API
/// with the account's own access token - the same one the web session already holds - because neither is answered
/// over the client connection (ask a CM for GetOwnedGames and the job times out with no reply). Being the
/// account's own token, a private profile makes no difference to any of it.
///
/// This matters because the achievement hunter used to work from a list of owned APPS, which is not the same
/// thing as a list of games: a licence covers a package, and a package contains DLC, demos, soundtracks and
/// tools. GetOwnedGames returns games. It also returns playtime, which is what separates a game somebody loves
/// from bundle filler that has never once been launched.
///
/// Refreshed on a long timer: a library changes when you buy something, not by the minute.
/// </summary>
public sealed class Library(Bot bot) {
	/// <summary>One game the account can launch. Minutes are Steam's own total, across every device.</summary>
	public sealed record Entry(uint AppId, string Name, int MinutesPlayed, DateTime Acquired, ulong SharedFrom) {
		/// <summary>Borrowed through a Steam Family rather than owned outright.</summary>
		public bool Shared => SharedFrom != 0;
	}

	private List<Entry> _games = [];
	private Dictionary<uint, Entry> _byApp = [];

	public IReadOnlyList<Entry> Games => _games;

	/// <summary>Never refreshed yet - callers that need real data should wait rather than act on an empty list.</summary>
	public bool Ready { get; private set; }

	public DateTime RefreshedAt { get; private set; }

	// ── who else in the family is playing what ───────────────────────────────
	private HashSet<uint> _familyBusy = [];
	private readonly Dictionary<uint, DateTime> _freeSince = [];

	/// <summary>
	/// A family member is playing this shared game right now, so it is not ours to touch.
	///
	/// Steam lends a shared game to one person at a time and the OWNER always wins: start hunting something they
	/// then launch and the account is thrown out of it mid-session, left "playing" a game it no longer has. This
	/// is the polite version of the same rule - don't take a game somebody is using, and don't pounce the instant
	/// they put it down either.
	/// </summary>
	public bool FamilyIsPlaying(uint app) {
		if (_familyBusy.Contains(app)) {
			return true;
		}

		// A grace period after they stop. Somebody who just quit is quite likely to start it up again, and an
		// account that grabs the game four seconds after they close it is not behaving like a housemate.
		// Locked: Steam's push writes this on its own thread while the modules read it, and a Dictionary read in the
		// middle of a write can throw.
		lock (_freeSince) {
			return _freeSince.TryGetValue(app, out DateTime free) && (DateTime.UtcNow - free < TimeSpan.FromMinutes(20));
		}
	}

	/// <summary>
	/// Steam's live "who in the family is running what" push. It carries the WHOLE current picture each time, so
	/// the set is replaced rather than added to.
	///
	/// This account appears in it too, playing whatever it is playing - counting that would mean reading our own
	/// hunt as somebody else's and standing down from it immediately, so we are filtered out by SteamID.
	/// </summary>
	internal void NoteFamilyRunning(IEnumerable<(uint App, IEnumerable<ulong> Members)> running) {
		HashSet<uint> busy = [];

		foreach ((uint app, IEnumerable<ulong> members) in running) {
			if (members.Any(m => m != bot.SteamId)) {
				busy.Add(app);
			}
		}

		lock (_freeSince) {
			foreach (uint app in _familyBusy.Except(busy)) {
				_freeSince[app] = DateTime.UtcNow;   // they've just stopped - start the grace period
			}

			foreach (uint app in busy) {
				_freeSince.Remove(app);
			}
		}

		_familyBusy = busy;
	}

	public Entry? Find(uint app) => _byApp.GetValueOrDefault(app);

	private Dictionary<uint, int> _twoWeeks = [];

	/// <summary>Minutes on each game in the last two weeks, as Steam counts them - what a profile shows as "past 2 weeks".</summary>
	public IReadOnlyDictionary<uint, int> LastTwoWeeks => _twoWeeks;

	/// <summary>Minutes on record for a game, or 0 if we've never heard of it.</summary>
	public int MinutesOn(uint app) => Find(app)?.MinutesPlayed ?? 0;

	/// <summary>
	/// GetOwnedGames doesn't always list everything the account plays. It left out 2,757 hours of Team Fortress 2 on
	/// an account that picked TF2 up after it went free - while listing Warframe and other free games just fine. The
	/// Steam client itself works from <c>ClientGetLastPlayedTimes</c>, which has every game with time on it, so that
	/// fills the gaps: a game there but missing here joins the library if the account still holds a licence for it
	/// (so a refund, a free weekend or a family member's game doesn't) and Steam calls it a game - the play history
	/// also carries SDKs, dedicated servers and SteamVR, which have "playtime" too. The higher playtime of the two wins.
	/// </summary>
	private async Task AddPlayedAsync(List<Entry> found, CancellationToken ct) {
		string? json = await bot.Web.ApiGetAsync("IPlayerService", "ClientGetLastPlayedTimes", new Dictionary<string, string> {
			["min_last_played"] = "0"
		}, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return;
		}

		Dictionary<uint, (int Forever, int TwoWeeks)> played = [];

		using (JsonDocument doc = JsonDocument.Parse(json)) {
			if (!doc.RootElement.TryGetProperty("response", out JsonElement res) || !res.TryGetProperty("games", out JsonElement games)) {
				Log.Debug("play history: Steam's answer had no games list", bot.Name);

				return;
			}

			foreach (JsonElement g in games.EnumerateArray()) {
				if (g.TryGetProperty("appid", out JsonElement id) && id.TryGetUInt32(out uint app) && (app != 0)) {
					int forever = g.TryGetProperty("playtime_forever", out JsonElement f) && f.TryGetInt32(out int fm) ? fm : 0;
					int twoWeeks = g.TryGetProperty("playtime_2weeks", out JsonElement t) && t.TryGetInt32(out int tm) ? tm : 0;
					played[app] = (Math.Max(0, forever), Math.Max(0, twoWeeks));
				}
			}
		}

		_twoWeeks = played.Where(static p => p.Value.TwoWeeks > 0).ToDictionary(static p => p.Key, static p => p.Value.TwoWeeks);

		for (int i = 0; i < found.Count; i++) {
			if (played.TryGetValue(found[i].AppId, out (int Forever, int TwoWeeks) time) && (time.Forever > found[i].MinutesPlayed)) {
				found[i] = found[i] with { MinutesPlayed = time.Forever };
			}
		}

		HashSet<uint> have = [.. found.Select(static g => g.AppId)];
		List<uint> missing = played.Where(p => (p.Value.Forever > 0) && !have.Contains(p.Key)).Select(static p => p.Key).ToList();

		if (missing.Count == 0) {
			return;
		}

		IReadOnlyDictionary<uint, AppOwnership> licensed = await bot.GetAppOwnershipAsync().ConfigureAwait(false);
		// Its own licence, not a family member's (those are the family library's business, marked as borrowed), and one
		// it has for good: a game played in a free weekend or a timed trial stays in the play history long after, and
		// isn't the account's to idle or earn achievements in. Never Spacewar either - Valve's test app, which other
		// games borrow, and whose "achievements" are test entries.
		missing = [.. missing.Where(app => (app != 480) && licensed.TryGetValue(app, out AppOwnership o) && o.Permanent)];

		if ((missing.Count == 0) || (bot.Apps is not { } apps)) {
			return;
		}

		// What each one is, from Steam's own app info. Nothing is added if that can't be read: guessing would put
		// tools in front of the achievement hunter.
		SteamApps.PICSTokensCallback tokens = await apps.PICSGetAccessTokens(missing, []).ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = await apps
			.PICSGetProductInfo(missing.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id))), [], false)
			.ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

		if (info.Failed) {
			Log.Debug($"play history: Steam's app info request failed part-way ({info.Results?.Count ?? 0} page(s) back for {missing.Count} app(s))", bot.Name);
		}

		List<uint> added = [];

		foreach (SteamApps.PICSProductInfoCallback page in info.Results ?? []) {
			foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo app in page.Apps.Values) {
				KeyValue common = app.KeyValues["common"];

				if (!string.Equals(common["type"].AsString(), "game", StringComparison.OrdinalIgnoreCase) || added.Contains(app.ID)) {
					continue;
				}

				GameNames.Learn(app.ID, common["name"].AsString());
				found.Add(new Entry(app.ID, GameNames.Of(app.ID), played[app.ID].Forever, DateTime.MinValue, 0));
				added.Add(app.ID);
			}
		}

		if ((added.Count > 0) && !Ready) {
			Log.Debug(new Said("library: {0} played game(s) Steam's owned list left out, added from the play history ({1})", added.Count,
				string.Join(", ", added.Take(6).Select(GameNames.Of)) + (added.Count > 6 ? ", ..." : "")), bot.Name);
		}
	}

	/// <summary>Ask Steam again, but only if what we have has gone stale.</summary>
	public async Task<bool> RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken ct) =>
		(Ready && (DateTime.UtcNow - RefreshedAt < maxAge)) || await RefreshAsync(ct).ConfigureAwait(false);

	/// <summary>The read of the library that is under way, if one is - everybody who asks meanwhile waits for that one.</summary>
	private Task<bool>? _refreshing;
	private readonly Lock _refreshGate = new();

	/// <summary>
	/// Read the library from Steam - or, when a read is already on its way, wait for that one.
	/// </summary>
	/// <remarks>
	/// Two modules ask at sign-in (the upkeep pass and the achievement hunter), a second apart, and both found the
	/// library stale - so every call behind it (owned games, the play history, the family's library) went to Steam
	/// twice, and the log said everything twice. One read answers both.
	/// </remarks>
	public Task<bool> RefreshAsync(CancellationToken ct) {
		lock (_refreshGate) {
			if (_refreshing is { IsCompleted: false } running) {
				return JoinAsync(running, ct);
			}

			return _refreshing = ReadAsync(ct);
		}
	}

	/// <summary>Wait for a read somebody else started. Theirs being stopped is not ours failing - it just didn't get there.</summary>
	private static async Task<bool> JoinAsync(Task<bool> running, CancellationToken ct) {
		try {
			return await running.WaitAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
			return false;
		}
	}

	private async Task<bool> ReadAsync(CancellationToken ct) {
		if (!bot.IsOnline || (bot.SteamId == 0)) {
			return false;
		}

		List<Entry> found = [];

		try {
			string? json = await bot.Web.ApiGetAsync("IPlayerService", "GetOwnedGames", new Dictionary<string, string> {
				["steamid"] = bot.SteamId.ToString(),
				["include_appinfo"] = "true",
				["include_played_free_games"] = "true",
				["skip_unvetted_apps"] = "false"
			}, ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(json)) {
				return false;
			}

			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("response", out JsonElement res) || !res.TryGetProperty("games", out JsonElement games)) {
				// Callers keep asking while the library isn't ready, so the same answer would be written every time.
				Log.DebugOnChange($"library:{bot.Name}", "library: Steam's owned-games answer had no games list", bot.Name);

				return false;
			}

			foreach (JsonElement g in games.EnumerateArray()) {
				if (!g.TryGetProperty("appid", out JsonElement id) || !id.TryGetUInt32(out uint app) || (app == 0)) {
					continue;
				}

				string name = g.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
				int minutes = g.TryGetProperty("playtime_forever", out JsonElement p) && p.TryGetInt32(out int m) ? m : 0;

				// Kept for everything else that says a game's name - the rotation's "next" list said "app 582660" for
				// games whose name had only ever arrived here.
				GameNames.Learn(app, name);

				// Acquired stays unset for owned games on purpose: when a game was BOUGHT comes from the licence
				// list, which the refund guard reads directly. Working it out here would mean a PICS sweep of every
				// package on every library refresh, for a number only one caller wants.
				found.Add(new Entry(app, string.IsNullOrWhiteSpace(name) ? GameNames.Of(app) : name, Math.Max(0, minutes), DateTime.MinValue, 0));
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;   // the account is stopping - not a failure to report
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the library: {0}", Log.Describe(e)), bot.Name);

			return false;
		}

		if (found.Count == 0) {
			Log.DebugOnChange($"library:{bot.Name}", "library: Steam listed no owned games - kept what we had", bot.Name);

			return false;   // a blip, not an empty library - keep whatever we already had
		}

		Log.Recovered($"library:{bot.Name}");

		try {
			await AddPlayedAsync(found, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the play history: {0}", Log.Describe(e)), bot.Name);
		}

		if (bot.Cfg.IncludeFamilyLibrary) {
			try {
				found.AddRange(await SharedAsync(found, ct).ConfigureAwait(false));
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("couldn't read the family library: {0}", Log.Describe(e)), bot.Name);
			}
		}

		int shared = found.Count(static g => g.Shared);
		bool first = !Ready;

		_byApp = found.GroupBy(static g => g.AppId).ToDictionary(static g => g.Key, static g => g.First());
		_games = found;
		Ready = true;
		RefreshedAt = DateTime.UtcNow;

		if (first) {
			Log.Debug(shared > 0
				? new Said("library: {0} game(s) owned, {1} shared by the family", found.Count - shared, shared)
				: new Said("library: {0} game(s) owned", found.Count - shared), bot.Name);
		}

		return true;
	}

	/// <summary>
	/// Games lent to this account by a Steam Family.
	///
	/// Asked for with non-games excluded at the source, so DLC, soundtracks, tools and demos never even reach the
	/// catalogue - the last thing anybody wants is an account "playing" a soundtrack. Anything the family has
	/// marked excluded is dropped too, as are games this account already owns: those are not borrowed, and
	/// counting them twice would let one game be picked twice.
	/// </summary>
	private async Task<List<Entry>> SharedAsync(List<Entry> owned, CancellationToken ct) {
		List<Entry> shared = [];

		string? groupJson = await bot.Web.ApiGetAsync("IFamilyGroupsService", "GetFamilyGroupForUser",
			new Dictionary<string, string> { ["steamid"] = bot.SteamId.ToString() }, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(groupJson)) {
			return shared;
		}

		using JsonDocument groupDoc = JsonDocument.Parse(groupJson);

		if (!groupDoc.RootElement.TryGetProperty("response", out JsonElement group)
			|| !group.TryGetProperty("family_groupid", out JsonElement idNode)) {
			return shared;   // not in a family - nothing borrowed
		}

		string familyId = idNode.ValueKind == JsonValueKind.String ? idNode.GetString() ?? "0" : idNode.ToString();

		if (string.IsNullOrEmpty(familyId) || (familyId == "0")) {
			return shared;
		}

		string? json = await bot.Web.ApiGetAsync("IFamilyGroupsService", "GetSharedLibraryApps", new Dictionary<string, string> {
			["family_groupid"] = familyId,
			["steamid"] = bot.SteamId.ToString(),
			["include_own"] = "false",
			["include_excluded"] = "false",
			["include_non_games"] = "false",
			["max_apps"] = "5000"
		}, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return shared;
		}

		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("response", out JsonElement res) || !res.TryGetProperty("apps", out JsonElement apps)) {
			Log.Debug("family library: Steam's answer had no apps list", bot.Name);

			return shared;
		}

		HashSet<uint> already = owned.Select(static g => g.AppId).ToHashSet();

		foreach (JsonElement app in apps.EnumerateArray()) {
			if (!app.TryGetProperty("appid", out JsonElement id) || !id.TryGetUInt32(out uint appId) || (appId == 0) || already.Contains(appId)) {
				continue;
			}

			if (app.TryGetProperty("exclude_reason", out JsonElement excluded) && excluded.TryGetInt32(out int reason) && (reason != 0)) {
				continue;   // the family has taken this one out of sharing
			}

			ulong owner = 0;

			if (app.TryGetProperty("owner_steamids", out JsonElement owners) && (owners.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement o in owners.EnumerateArray()) {
					if (ulong.TryParse(o.ValueKind == JsonValueKind.String ? o.GetString() : o.ToString(), out ulong parsed) && (parsed != 0)) {
						owner = parsed;

						break;
					}
				}
			}

			if (owner == 0) {
				continue;   // nobody to borrow it from
			}

			string name = app.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
			int minutes = app.TryGetProperty("rt_playtime", out JsonElement p) && p.TryGetInt32(out int mins) ? mins : 0;
			GameNames.Learn(appId, name);
			DateTime acquired = app.TryGetProperty("rt_time_acquired", out JsonElement t) && t.TryGetInt64(out long secs) && (secs > 0)
				? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
				: DateTime.MinValue;

			shared.Add(new Entry(appId, string.IsNullOrWhiteSpace(name) ? GameNames.Of(appId) : name, Math.Max(0, minutes), acquired, owner));
		}

		return shared;
	}
}
