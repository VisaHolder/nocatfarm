using System.Globalization;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Internal;

namespace NocatFarm.Core;

/// <summary>One achievement in a game, as Steam describes it plus what the world has done with it.</summary>
public sealed record Achievement {
	/// <summary>The API name, e.g. <c>ACH_WIN_ONE_GAME</c>. This is what you type at the console.</summary>
	public required string Name { get; init; }

	/// <summary>What a person sees on the profile.</summary>
	public required string Display { get; init; }

	/// <summary>The achievement's description - used to spot milestone/meta ones that need other achievements first.</summary>
	public string Description { get; init; } = "";

	/// <summary>
	/// Kept off the profile until it's earned, by the game's own choice: what games hide is mostly their story (a boss, an
	/// ending), so the order's wobble never steps onto one of these.
	/// </summary>
	public bool Hidden { get; init; }

	/// <summary>Which stat holds it, and which bit inside that stat. Together these are its address.</summary>
	public required uint StatId { get; init; }
	public required int Bit { get; init; }

	public required bool Unlocked { get; init; }

	/// <summary>
	/// Steam refuses to let a client set some achievements - they are awarded server-side only. Trying anyway
	/// gets the whole store request rejected, taking the achievable ones down with it.
	/// </summary>
	public required bool Protected { get; init; }

	/// <summary>
	/// The share of owners worldwide who have it, 0-100, or null when Steam has no figure.
	///
	/// This is the number that makes an unlock order believable. A real player earns the tutorial achievement
	/// that 90% of people have long before the one 0.4% of people have.
	/// </summary>
	public double? GlobalPercent { get; set; }

	public bool Settable => !Protected;

	/// <summary>
	/// The count an achievement with a progress bar is earned at ("Complete 100 parries" - 100), or 0 when it has none.
	/// Steam keeps the count in one of the game's own stats, named in the schema's "progress" block.
	/// </summary>
	public double ProgressMax { get; init; }

	/// <summary>Where that count stands on this account, read from the same stat. 0 when the stat was never set.</summary>
	public double ProgressNow { get; init; }

	/// <summary>
	/// A counted achievement whose count isn't there yet: "100 parries" with 37 on the counter. Unlocked like that, the
	/// profile shows it earned while the game's own counter - which Steam shows on the achievement too - says 37 of 100.
	/// Nothing writes the counter (that would be making up play), so such an achievement waits for the real count.
	/// A "progress" of 0-1 is only a done/not-done flag, and counts for nothing here.
	/// </summary>
	public bool CounterShort => (ProgressMax > 1) && (ProgressNow < ProgressMax);

	/// <summary>
	/// Which add-on it belongs to, as far as can be told: "" for the base game; the DLC that claims it in the game's DLC
	/// map; or "?" - in a game whose add-ons Steam doesn't place - for one outside the game's first block of achievements,
	/// or named "DLC". Worked out for each look at a game (see <see cref="DlcAchievements.AddOnParts"/>) and set here so
	/// the order rules can ask it: a DLC's ending comes after the game's own, and one part's steps never hold another
	/// part's ending back.
	/// </summary>
	public string AddOnPart { get; set; } = "";

	/// <summary>Belongs to an add-on (<see cref="AddOnPart"/>), owned or not.</summary>
	public bool AddOn => AddOnPart.Length > 0;
}

/// <summary>Everything known about one game's achievements, as read: each one's stat and bit, and the checksum a write
/// quotes back. A write reads the stat values fresh first (see SetCheckedAsync) - a copy kept here would go stale.</summary>
public sealed class AchievementSet {
	public required uint AppId { get; init; }
	public required List<Achievement> All { get; init; }

	/// <summary>
	/// The checksum Steam sent with the schema, which every write has to quote back.
	///
	/// It is how Steam knows the client is working from the schema it currently serves. Omitting it - sending
	/// zero - gets the store refused with InvalidParam no matter how correct the stats themselves are, which is
	/// a singularly unhelpful thing to be told.
	/// </summary>
	public required uint CrcStats { get; init; }

	public IEnumerable<Achievement> Locked => All.Where(static a => !a.Unlocked && a.Settable);
	public IEnumerable<Achievement> Unlocked => All.Where(static a => a.Unlocked);

	public int Total => All.Count;
	public int UnlockedCount => All.Count(static a => a.Unlocked);
}

/// <summary>
/// Reading and writing Steam achievements.
///
/// Steam keeps achievements as BITS inside numbered stats, and the map from "ACH_WIN_ONE_GAME" to "bit 3 of
/// stat 1" lives in a binary KeyValue schema the client has to ask for. So every unlock is: fetch the schema,
/// find the bit, flip it in the stat's current value, and store the whole stat back. Storing a stat you did not
/// first read would wipe every other achievement sharing it.
///
/// SteamKit does not surface these messages, so <see cref="UserStatsHandler"/> below sends them by hand.
/// </summary>
public static class Achievements {
	/// <summary>Steam's schema type for a stat whose bits are achievements. Every other type is a real statistic.</summary>
	private const int StatTypeBits = 4;

	/// <summary>
	/// Whether a stat node holds achievement bits rather than a real statistic.
	///
	/// The schema writes this type as the WORD "ACHIEVEMENTS", not as the number 4 the enum would suggest.
	/// Reading it as an integer quietly returned 0 for every stat, so every game came back with none at all -
	/// Team Fortress 2 reported 0 of 0 while holding 17 achievement stats and over five hundred achievements.
	/// Both spellings are accepted, because a schema that used the number would be just as valid.
	/// </summary>
	private static bool IsAchievementStat(KeyValue type) {
		string? word = type.AsString();

		if (!string.IsNullOrEmpty(word) && word.Equals("ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		return type.AsInteger() == StatTypeBits;
	}

	private static readonly HttpClient Http = Browser.Anonymous(TimeSpan.FromSeconds(20));
	private static readonly Dictionary<uint, Dictionary<string, double>> GlobalCache = [];

	/// <summary>
	/// What a refused write says about the achievement. <see cref="PutBack"/> and <see cref="NotSettable"/> are about the
	/// achievement itself; the pacer leaves one refused like that alone for a week (see AchievementPacer.ParkRefusal).
	/// </summary>
	public enum Refusal {
		/// <summary>Not refused: written, or nothing to write.</summary>
		None,

		/// <summary>The write had no answer, "not now", a timeout, or Fail - nothing learned.</summary>
		Transient,

		/// <summary>Steam answered no, in a way that doesn't say why (InvalidParam and the like).</summary>
		Answered,

		/// <summary>Steam said AccessDenied / InsufficientPrivilege: a client can't set it - or a borrowed game's owner is in it.</summary>
		NotSettable,

		/// <summary>
		/// Steam took the write and put the stat back as it was ("failed validation"): the game's own servers decide that
		/// one. Said about the achievement whoever owns the game - a family member starting it gets a refusal, not this.
		/// </summary>
		PutBack,

		/// <summary>
		/// Steam was never asked about the achievement: not connected, the read before the write didn't work, or what the
		/// account owns changed. Says nothing about the achievement - the pacer counts no strike for it.
		/// </summary>
		NotAsked
	}

	/// <summary>Steam's answers to a write that mean a client may not set it, whatever is tried.</summary>
	internal static bool MeansNotSettable(EResult result) => result is EResult.AccessDenied or EResult.InsufficientPrivilege;

	/// <summary>
	/// What kind of refusal a write's answer is - see <see cref="Refusal"/>. Fail is Steam's catch-all, and a family member
	/// taking a borrowed game back gets it too: nothing learned.
	/// </summary>
	internal static Refusal RefusalOf(EResult result) =>
		result == EResult.OK ? Refusal.None
		: NotNow(result) || (result is EResult.Fail or EResult.Invalid) ? Refusal.Transient
		: MeansNotSettable(result) ? Refusal.NotSettable
		: Refusal.Answered;

	/// <summary>Results that mean "ask again later", as opposed to an answer about the game itself.</summary>
	private static bool NotNow(EResult result) => result is EResult.Busy or EResult.ServiceUnavailable
		or EResult.TryAnotherCM or EResult.RateLimitExceeded or EResult.Timeout or EResult.NoConnection
		or EResult.Pending or EResult.LimitExceeded;

	public static async Task<AchievementSet?> GetAsync(Bot bot, uint appId, CancellationToken ct = default) {
		if (!bot.IsOnline || (bot.SteamId == 0) || (bot.Stats == null)) {
			return null;
		}

		CMsgClientGetUserStatsResponse? response = await bot.Stats.GetUserStatsAsync(appId, bot.SteamId, ct).ConfigureAwait(false);

		// Two very different "no" answers, which used to be the same null.
		//
		// No reply at all, or a reply that says "not now", is worth asking again shortly. A reply that says
		// there is nothing here - no stats, no schema - is final, and treating it as a hiccup meant a game with
		// no achievements (s&box is one) was re-asked every half hour of play, forever, and every screen that
		// showed it was stuck on "reading what it has so far". An empty set is what "has none" looks like to
		// every caller: the pacer backs off for most of a day, unlock-everything skips it, cheevo says so.
		// Steam answers Fail both for a game with no stats and for one the account doesn't own - the pacer only asks
		// about games being played, so for it the two mean the same thing; cheevo tells them apart by ownership.
		// Asked again every so often while the game plays, so the same "no" is written once, not every round.
		string askKey = $"achget:{bot.Name}:{appId}";

		if (response == null) {
			Log.DebugOnChange(askKey, $"achievement stats for {appId}: no answer from Steam in time", bot.Name);

			return null;
		}

		EResult result = (EResult) response.eresult;

		if (NotNow(result)) {
			Log.DebugOnChange(askKey, $"achievement stats for {appId}: Steam said {result} - asking again later", bot.Name);

			return null;
		}

		Log.Recovered(askKey);

		if ((result != EResult.OK) || (response.schema == null) || (response.schema.Length == 0)) {
			Log.Debug(new Said("Steam has no achievement stats for {0} ({1})", GameNames.Of(appId), result), bot.Name);

			return new AchievementSet { AppId = appId, All = [], CrcStats = 0 };
		}

		KeyValue schema = new();

		using (MemoryStream stream = new(response.schema)) {
			if (!schema.TryReadAsBinary(stream)) {
				Log.Warn(new Said("couldn't read the achievement list for {0}", GameNames.Of(appId)), bot.Name);

				return null;
			}
		}

		// Unlock state lives in the STAT VALUES, which is the same place writing an unlock puts it.
		//
		// The achievement_blocks in the response are a parallel view carrying unlock timestamps, indexed across
		// the whole game rather than per stat. Reading state from those and writing it to stat bits meant the two
		// halves disagreed: an account with every Team Fortress 2 achievement earned reported 0 of 520.
		Dictionary<uint, uint> statValues = [];

		foreach (CMsgClientGetUserStatsResponse.Stats stat in response.stats) {
			statValues[stat.stat_id] = stat.stat_value;
		}

		List<Achievement> all = [];

		// The layout is "<appid>" -> "stats" -> "<n>" -> { type, bits -> "<n>" -> { name, bit, display, permission } }
		//
		// TryReadAsBinary reads the top-level key INTO the KeyValue it is called on, so the root is already the
		// appID node - its children are "stats" and friends. Looking for a child called "440" underneath it found
		// nothing at all, and every game came back as 0/0 achievements.
		if (Log.DebugEnabled) {
			Log.Debug(new Said("schema root '{0}' with {1} child(ren): {2}", schema.Name, schema.Children.Count, string.Join(", ", schema.Children.Take(8).Select(static c => c.Name + "[" + c.Children.Count + "]"))), bot.Name);

			KeyValue statsNode = schema["stats"];
			Dictionary<string, int> byType = [];

			foreach (KeyValue n in statsNode.Children) {
				string t = n["type"].AsString() ?? "(none)";
				byType[t] = byType.GetValueOrDefault(t) + 1;
			}

			Log.Debug(new Said("stat types: {0}", string.Join(", ", byType.Select(static kv => kv.Key + " x" + kv.Value))), bot.Name);

			KeyValue first = statsNode.Children.FirstOrDefault() ?? new KeyValue();
			Log.Debug(new Said("first stat '{0}' children: {1}", first.Name, string.Join(", ", first.Children.Select(static c => c.Name + "=" + (c.Children.Count > 0 ? "[" + c.Children.Count + "]" : c.Value)))), bot.Name);
		}

		string appKey = appId.ToString(CultureInfo.InvariantCulture);
		KeyValue root = schema.Name == appKey ? schema : schema.Children.FirstOrDefault(k => k.Name == appKey) ?? schema;

		// The game's ordinary stats by name, for the progress bars: an achievement's "progress" block names the stat its
		// count lives in ("Medic.accum.iHealthPointsHealed"), not its number.
		Dictionary<string, (uint Id, bool Float)> statsByName = new(StringComparer.Ordinal);

		foreach (KeyValue statNode in root["stats"].Children) {
			if (uint.TryParse(statNode.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && (statNode["name"].AsString() is { Length: > 0 } statName)) {
				string type = statNode["type"].AsString() ?? "";
				statsByName.TryAdd(statName, (id, type is "2" or "3" or "FLOAT" or "AVGRATE"));
			}
		}

		foreach (KeyValue statNode in root["stats"].Children) {
			if (!IsAchievementStat(statNode["type"])) {
				continue;
			}

			if (!uint.TryParse(statNode.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint statId)) {
				continue;
			}

			foreach (KeyValue bitNode in statNode["bits"].Children) {
				string? apiName = bitNode["name"].AsString();

				if (string.IsNullOrEmpty(apiName)) {
					continue;
				}

				int bit = bitNode["bit"].AsInteger(-1);

				if (bit < 0) {
					// Older schemas key the bit by the node name rather than a "bit" child.
					if (!int.TryParse(bitNode.Name, NumberStyles.None, CultureInfo.InvariantCulture, out bit)) {
						continue;
					}
				}

				// Any permission bit means Steam keeps it to itself: 2 is awarded server-side, 1 is set by the game's own
				// trusted servers. A client write is refused either way - and takes the rest of the store request with it.
				bool locked = (bitNode["permission"].AsInteger() & 3) != 0;
				(double progressMax, double progressNow) = ProgressOf(bitNode["progress"], statsByName, statValues);

				string display = bitNode["display"]["name"].Children.FirstOrDefault(static k => k.Name == "english")?.Value
					?? bitNode["display"]["name"].AsString()
					?? apiName;

				string desc = bitNode["display"]["desc"].Children.FirstOrDefault(static k => k.Name == "english")?.Value
					?? bitNode["display"]["desc"].AsString()
					?? "";

				// The bit is an offset within THIS stat's value, not a global achievement index.
				bool unlocked = (statValues.GetValueOrDefault(statId) & (1u << (bit & 31))) != 0;

				all.Add(new Achievement {
					Name = apiName,
					Display = display,
					Description = desc,
					StatId = statId,
					Bit = bit,
					Unlocked = unlocked,
					Protected = locked,
					ProgressMax = progressMax,
					ProgressNow = progressNow,
					Hidden = bitNode["display"]["hidden"].AsInteger() != 0
				});
			}
		}

		AchievementSet set = new() { AppId = appId, All = all, CrcStats = response.crc_stats };
		await AddGlobalPercentagesAsync(set, bot.Name, ct).ConfigureAwait(false);

		return set;
	}

	/// <summary>
	/// An achievement's progress bar: the count it is earned at, and where the account's counter stands. The block is
	/// { min_val, max_val, value { operation "statvalue", operand1 "&lt;stat name&gt;" } }. No block, no count: (0, 0). A
	/// stat the account never set reads 0 - Steam leaves untouched stats out of its answer.
	/// </summary>
	internal static (double Max, double Now) ProgressOf(KeyValue progress, IReadOnlyDictionary<string, (uint Id, bool Float)> statsByName, IReadOnlyDictionary<uint, uint> values) {
		if ((progress == KeyValue.Invalid) || (progress.Children.Count == 0)
			|| !double.TryParse(progress["max_val"].AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double max) || (max <= 0)) {
			return (0, 0);
		}

		// A counter whose stat can't be found reads as nothing done: held, rather than taken as finished.
		if ((progress["value"]["operand1"].AsString() is not { Length: > 0 } name) || !statsByName.TryGetValue(name, out (uint Id, bool Float) stat)) {
			return (max, 0);
		}

		uint raw = values.GetValueOrDefault(stat.Id);

		// Integer stats are signed; float ones are the float's own bits.
		return (max, stat.Float ? BitConverter.Int32BitsToSingle(unchecked((int) raw)) : unchecked((int) raw));
	}

	/// <summary>
	/// Unlock (or re-lock) a set of achievements in one store request.
	///
	/// One request, not one per achievement: Steam rate-limits stat writes hard, and a hundred separate stores
	/// is both slower and far more likely to be refused halfway through, leaving the game in a state nobody
	/// asked for.
	///
	/// Changed is how many really changed. Ok with 0 is "already that way" - another write got there first (the pacer's
	/// pick against 'cheevo', say), read under the gate - and a caller counting Ok as "unlocked it" logged, notified and
	/// counted an achievement a second time.
	///
	/// <paramref name="dlc"/> is the DLC rule the unlocks were picked by. It was asked seconds ago - minutes, for a
	/// command that waited on it - so it is asked again under the gate, right before the write: the account's licences
	/// changed or the game was taken off the owner's vouched-for list since, and nothing is unlocked.
	/// </summary>
	public static async Task<(bool Ok, string Message, int Changed)> SetAsync(Bot bot, AchievementSet set, IEnumerable<Achievement> which, bool unlock, CancellationToken ct = default, DlcAchievements.View? dlc = null) {
		(bool ok, string message, int changed, Refusal _) = await SetCheckedAsync(bot, set, which, unlock, ct, dlc).ConfigureAwait(false);

		return (ok, message, changed);
	}

	/// <summary>
	/// <see cref="SetAsync"/>, also saying what kind of no Steam's answer was, if it was one (<see cref="Refusal"/>). The
	/// pacer remembers an achievement Steam says a client can't set rather than asking again every hour: Steam keeps some
	/// for itself without the schema saying so.
	/// </summary>
	public static async Task<(bool Ok, string Message, int Changed, Refusal Refused)> SetCheckedAsync(Bot bot, AchievementSet set, IEnumerable<Achievement> which, bool unlock, CancellationToken ct = default, DlcAchievements.View? dlc = null) {
		if (bot.Stats == null) {
			return (false, "not connected", 0, Refusal.NotAsked);
		}

		// One write per account and game at a time, worked out from the stats as they are NOW. A write stores whole
		// stat values, and the set it was worked out from can be many seconds old: the pacer's pick, 'cheevo lock all'
		// and unlock-everything each read a set, and whichever wrote second put its old copy of a shared stat back -
		// undoing the other's unlocks or re-locks. Under the gate, read again, then change only what still needs it.
		SemaphoreSlim gate = GateOf(bot, set.AppId);
		await gate.WaitAsync(ct).ConfigureAwait(false);

		try {
			if (unlock && (dlc != null) && !dlc.StillSo(bot)) {
				return (false, new Said("what this account owns changed a moment ago - nothing was unlocked, try again").ToString(), 0, Refusal.NotAsked);
			}

			return await SetLatestAsync(bot, set, which, unlock, ct).ConfigureAwait(false);
		} finally {
			gate.Release();
		}
	}

	/// <summary>The gate for one account's writes to one game's stats - see <see cref="SetAsync"/>.</summary>
	internal static SemaphoreSlim GateOf(Bot bot, uint appId) => Gates.GetOrAdd((bot.Name, appId), static _ => new SemaphoreSlim(1, 1));

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Bot, uint App), SemaphoreSlim> Gates = new();

	private static async Task<(bool Ok, string Message, int Changed, Refusal Refused)> SetLatestAsync(Bot bot, AchievementSet set, IEnumerable<Achievement> which, bool unlock, CancellationToken ct) {
		if (bot.Stats == null) {
			return (false, "not connected", 0, Refusal.NotAsked);
		}

		CMsgClientGetUserStatsResponse? latest = await bot.Stats.GetUserStatsAsync(set.AppId, bot.SteamId, ct).ConfigureAwait(false);

		if ((latest == null) || ((EResult) latest.eresult != EResult.OK)) {
			return (false, $"Steam didn't say what's unlocked right now ({(latest == null ? "no answer" : (EResult) latest.eresult)}) - try again shortly", 0, Refusal.NotAsked);
		}

		Dictionary<uint, uint> now = [];

		foreach (CMsgClientGetUserStatsResponse.Stats stat in latest.stats) {
			now[stat.stat_id] = stat.stat_value;
		}

		uint crc = latest.crc_stats != 0 ? latest.crc_stats : set.CrcStats;

		// ONLY the stats that actually change go in the request.
		//
		// Sending the whole set back - all 775 of them for Team Fortress 2 - gets the lot refused with
		// InvalidParam, because most of a game's stats are ordinary counters and many are marked increment-only.
		// Re-submitting those at their existing value is not a no-op to Steam, it is an invalid write.
		Dictionary<uint, uint> changed = [];
		Dictionary<uint, int> perStat = [];
		int touched = 0;

		foreach (Achievement achievement in which) {
			if (!achievement.Settable) {
				continue;
			}

			// Read-modify-write on the stat this achievement lives in. Writing a bare bit instead of the modified
			// current value would clear every other achievement sharing that stat.
			uint current = changed.TryGetValue(achievement.StatId, out uint pending) ? pending : now.GetValueOrDefault(achievement.StatId);
			uint mask = 1u << (achievement.Bit & 31);
			uint updated = unlock ? current | mask : current & ~mask;

			if (updated == current) {
				continue;
			}

			changed[achievement.StatId] = updated;
			perStat[achievement.StatId] = perStat.GetValueOrDefault(achievement.StatId) + 1;
			touched++;
		}

		if (touched == 0) {
			return (true, "nothing to change", 0, Refusal.None);
		}

		(EResult result, IReadOnlyCollection<uint> putBack) = await bot.Stats.StoreUserStatsAsync(set.AppId, bot.SteamId, changed, crc, ct).ConfigureAwait(false);

		if (result != EResult.OK) {
			Log.Debug($"achievement write for {set.AppId} refused: {result} ({touched} achievement(s), {changed.Count} stat(s))", bot.Name);

			return (false, $"Steam refused it ({result})", 0, RefusalOf(result));
		}

		// OK, but with stats Steam put back as they were ("failed validation"): the achievements in those weren't set - and
		// that is Steam saying a client can't set them. Taken as unlocked before, they were logged and counted while still
		// locked on the profile.
		int refused = putBack.Distinct().Where(perStat.ContainsKey).Sum(id => perStat[id]);

		if (refused > 0) {
			Log.Debug($"achievement write for {set.AppId}: Steam put back {refused} achievement(s) in {putBack.Count} stat(s)", bot.Name);
		}

		int done = touched - refused;

		return done > 0
			? (true, $"{done} achievement(s) {(unlock ? "unlocked" : "re-locked")}", done, Refusal.None)
			: (false, "Steam put it back - a client isn't allowed to set it", 0, Refusal.PutBack);
	}

	/// <summary>
	/// How many people worldwide have each achievement.
	///
	/// Public endpoint, no key, and the answer never really changes - so it is fetched once per app per run and
	/// kept. It is what lets the drip go easiest-first instead of alphabetically, which is the difference
	/// between a plausible unlock history and an obviously generated one.
	/// </summary>
	private static async Task AddGlobalPercentagesAsync(AchievementSet set, string source, CancellationToken ct) {
		Dictionary<string, double>? percentages;

		lock (GlobalCache) {
			GlobalCache.TryGetValue(set.AppId, out percentages);
		}

		if (percentages == null) {
			// Only a genuine answer from Steam gets cached. If the fetch throws or returns non-200, `fetched` stays
			// null and we DON'T cache - a single network blip must never bake an empty dict in for the whole
			// process, which used to silently disable the achievement pacer for that game forever (everything read
			// GlobalPercent == null, nothing cleared the rarity floor, and it quietly stopped unlocking).
			Dictionary<string, double>? fetched = null;

			try {
				string url = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={set.AppId}";
				using HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false);

				if (response.IsSuccessStatusCode) {
					// Read into a local and only hand it over once the whole answer has parsed. Assigning first meant
					// a 200 that didn't parse (a truncated body, an HTML error page) cached an empty list for the rest
					// of the run - the very thing the comment above says must never happen.
					Dictionary<string, double> read = [];
					using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

					if (doc.RootElement.TryGetProperty("achievementpercentages", out JsonElement wrapper)
						&& wrapper.TryGetProperty("achievements", out JsonElement rows)) {
						foreach (JsonElement row in rows.EnumerateArray()) {
							string? name = row.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;

							if (string.IsNullOrEmpty(name) || !row.TryGetProperty("percent", out JsonElement p)) {
								continue;
							}

							read[name] = p.ValueKind == JsonValueKind.Number ? p.GetDouble()
								: double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
						}

						fetched = read;
					} else {
						Log.DebugOnChange($"achrates:{source}:{set.AppId}", $"global achievement rates for {set.AppId}: the answer had no achievement list", source);
					}
				} else {
					// Not cached, so asked again on every read of this game - the same answer is said once.
					Log.DebugOnChange($"achrates:{source}:{set.AppId}", $"global achievement rates for {set.AppId}: HTTP {(int) response.StatusCode} from {Log.Where(response.RequestMessage?.RequestUri)}", source);
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't read global achievement rates for {0}: {1}", set.AppId, Log.Describe(e)), source);
			}

			percentages = fetched ?? [];

			if (fetched != null) {
				lock (GlobalCache) {
					GlobalCache[set.AppId] = fetched;
				}
			}
		}

		foreach (Achievement achievement in set.All) {
			if (percentages.TryGetValue(achievement.Name, out double percent)) {
				achievement.GlobalPercent = percent;
			}
		}
	}

}

/// <summary>
/// The two Steam messages achievements need, which SteamKit does not expose.
///
/// Both are job-based: the request carries a job id and the response quotes it back, so several games can be
/// read at once without their answers being confused for one another.
/// </summary>
public sealed class UserStatsHandler : ClientMsgHandler {
	private readonly Dictionary<JobID, TaskCompletionSource<CMsgClientGetUserStatsResponse>> _pendingGets = [];
	private readonly Dictionary<JobID, TaskCompletionSource<(EResult, IReadOnlyCollection<uint>)>> _pendingStores = [];

	public override void HandleMsg(IPacketMsg packetMsg) {
		ArgumentNullException.ThrowIfNull(packetMsg);

		switch (packetMsg.MsgType) {
			case EMsg.ClientGetUserStatsResponse: {
				ClientMsgProtobuf<CMsgClientGetUserStatsResponse> msg = new(packetMsg);

				lock (_pendingGets) {
					if (_pendingGets.Remove(packetMsg.TargetJobID, out TaskCompletionSource<CMsgClientGetUserStatsResponse>? waiting)) {
						waiting.TrySetResult(msg.Body);
					}
				}

				break;
			}

			case EMsg.ClientStoreUserStatsResponse: {
				ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> msg = new(packetMsg);

				// The stats Steam put back as they were come with the answer, even an OK one.
				List<uint> putBack = [.. (msg.Body.stats_failed_validation ?? []).Select(static f => f.stat_id)];

				lock (_pendingStores) {
					if (_pendingStores.Remove(packetMsg.TargetJobID, out TaskCompletionSource<(EResult, IReadOnlyCollection<uint>)>? waiting)) {
						waiting.TrySetResult(((EResult) msg.Body.eresult, putBack));
					}
				}

				break;
			}
		}
	}

	public async Task<CMsgClientGetUserStatsResponse?> GetUserStatsAsync(uint appId, ulong steamId, CancellationToken ct = default) {
		if (Client == null) {
			return null;
		}

		ClientMsgProtobuf<CMsgClientGetUserStats> request = new(EMsg.ClientGetUserStats) {
			SourceJobID = Client.GetNextJobID(),
			Body = {
				game_id = appId,
				steam_id_for_user = steamId,

				// -1 and 0 mean "send me the schema, I have nothing cached". Sending a real version number here
				// gets an answer with no schema at all, which is unreadable.
				schema_local_version = -1,
				crc_stats = 0
			}
		};

		TaskCompletionSource<CMsgClientGetUserStatsResponse> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		lock (_pendingGets) {
			_pendingGets[request.SourceJobID] = answer;
		}

		Client.Send(request);

		return (await WaitAsync(answer.Task, () => Forget(_pendingGets, request.SourceJobID), ct).ConfigureAwait(false)).Value;
	}

	/// <summary>Store the stats: Steam's answer, and the stats it put back as they were (refused one by one).</summary>
	public async Task<(EResult Result, IReadOnlyCollection<uint> PutBack)> StoreUserStatsAsync(uint appId, ulong steamId, Dictionary<uint, uint> statValues, uint crcStats, CancellationToken ct = default) {
		if (Client == null) {
			return (EResult.NoConnection, []);
		}

		ClientMsgProtobuf<CMsgClientStoreUserStats2> request = new(EMsg.ClientStoreUserStats2) {
			SourceJobID = Client.GetNextJobID(),
			Body = {
				game_id = appId,
				settor_steam_id = steamId,
				settee_steam_id = steamId,
				explicit_reset = false,

				// Quoted straight back from the read. Steam uses it to check we are working from the schema it
				// is currently serving, and refuses the whole write with InvalidParam when it is missing.
				crc_stats = crcStats
			}
		};

		foreach ((uint statId, uint value) in statValues) {
			request.Body.stats.Add(new CMsgClientStoreUserStats2.Stats { stat_id = statId, stat_value = value });
		}

		TaskCompletionSource<(EResult, IReadOnlyCollection<uint>)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		lock (_pendingStores) {
			_pendingStores[request.SourceJobID] = answer;
		}

		Client.Send(request);

		(bool ok, (EResult, IReadOnlyCollection<uint>) result) = await WaitAsync(answer.Task, () => Forget(_pendingStores, request.SourceJobID), ct).ConfigureAwait(false);

		return ok ? result : (EResult.Timeout, []);
	}

	/// <summary>Wait with a ceiling, and never leave the pending entry behind when it expires. Returns Ok=false
	/// on timeout - a bare sentinel value doesn't work here because T can be a value type (EResult), where
	/// default is a real, valid-looking value (Invalid), not a "didn't happen" marker.</summary>
	private static async Task<(bool Ok, T? Value)> WaitAsync<T>(Task<T> task, Action cleanUp, CancellationToken ct) {
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));

		try {
			return (true, await task.WaitAsync(timeout.Token).ConfigureAwait(false));
		} catch (Exception) {
			cleanUp();

			return (false, default);
		}
	}

	private void Forget<T>(Dictionary<JobID, T> pending, JobID job) {
		lock (pending) {
			pending.Remove(job);
		}
	}
}
