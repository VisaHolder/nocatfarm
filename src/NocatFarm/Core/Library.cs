using System.Text.Json;
using NocatFarm.Config;

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

		public double HoursPlayed => MinutesPlayed / 60.0;
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
		return _freeSince.TryGetValue(app, out DateTime free) && (DateTime.UtcNow - free < TimeSpan.FromMinutes(20));
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

		foreach (uint app in _familyBusy.Except(busy)) {
			_freeSince[app] = DateTime.UtcNow;   // they've just stopped - start the grace period
		}

		foreach (uint app in busy) {
			_freeSince.Remove(app);
		}

		_familyBusy = busy;
	}

	public Entry? Find(uint app) => _byApp.GetValueOrDefault(app);

	/// <summary>Minutes on record for a game, or 0 if we've never heard of it.</summary>
	public int MinutesOn(uint app) => Find(app)?.MinutesPlayed ?? 0;

	/// <summary>Ask Steam again, but only if what we have has gone stale.</summary>
	public async Task<bool> RefreshIfStaleAsync(TimeSpan maxAge, CancellationToken ct) =>
		(Ready && (DateTime.UtcNow - RefreshedAt < maxAge)) || await RefreshAsync(ct).ConfigureAwait(false);

	public async Task<bool> RefreshAsync(CancellationToken ct) {
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
				return false;
			}

			foreach (JsonElement g in games.EnumerateArray()) {
				if (!g.TryGetProperty("appid", out JsonElement id) || !id.TryGetUInt32(out uint app) || (app == 0)) {
					continue;
				}

				string name = g.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
				int minutes = g.TryGetProperty("playtime_forever", out JsonElement p) && p.TryGetInt32(out int m) ? m : 0;

				// Acquired stays unset for owned games on purpose: when a game was BOUGHT comes from the licence
				// list, which the refund guard reads directly. Working it out here would mean a PICS sweep of every
				// package on every library refresh, for a number only one caller wants.
				found.Add(new Entry(app, string.IsNullOrWhiteSpace(name) ? GameNames.Of(app) : name, Math.Max(0, minutes), DateTime.MinValue, 0));
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;   // the account is stopping - not a failure to report
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the library: {0}", e.Message), bot.Name);

			return false;
		}

		if (found.Count == 0) {
			return false;   // a blip, not an empty library - keep whatever we already had
		}

		if (bot.Cfg.IncludeFamilyLibrary) {
			try {
				found.AddRange(await SharedAsync(found, ct).ConfigureAwait(false));
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("couldn't read the family library: {0}", e.Message), bot.Name);
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
			DateTime acquired = app.TryGetProperty("rt_time_acquired", out JsonElement t) && t.TryGetInt64(out long secs) && (secs > 0)
				? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
				: DateTime.MinValue;

			shared.Add(new Entry(appId, string.IsNullOrWhiteSpace(name) ? GameNames.Of(appId) : name, Math.Max(0, minutes), acquired, owner));
		}

		return shared;
	}
}
