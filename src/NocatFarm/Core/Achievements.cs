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
}

/// <summary>Everything known about one game's achievements, and the raw stat values needed to write them back.</summary>
public sealed class AchievementSet {
	public required uint AppId { get; init; }
	public required List<Achievement> All { get; init; }

	/// <summary>Current value of every stat, keyed by stat id. Writing an achievement is a bit flip in here.</summary>
	public required Dictionary<uint, uint> StatValues { get; init; }

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

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
	private static readonly Dictionary<uint, Dictionary<string, double>> GlobalCache = [];

	public static async Task<AchievementSet?> GetAsync(Bot bot, uint appId, CancellationToken ct = default) {
		if (!bot.IsOnline || (bot.SteamId == 0) || (bot.Stats == null)) {
			return null;
		}

		CMsgClientGetUserStatsResponse? response = await bot.Stats.GetUserStatsAsync(appId, bot.SteamId, ct).ConfigureAwait(false);

		if ((response == null) || (response.eresult != (int) EResult.OK) || (response.schema == null)) {
			return null;
		}

		KeyValue schema = new();

		using (MemoryStream stream = new(response.schema)) {
			if (!schema.TryReadAsBinary(stream)) {
				Log.Warn($"couldn't read the achievement schema for app {appId}", bot.Name);

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
			Log.Debug($"schema root '{schema.Name}' with {schema.Children.Count} child(ren): {string.Join(", ", schema.Children.Take(8).Select(static c => c.Name + "[" + c.Children.Count + "]"))}", bot.Name);

			KeyValue statsNode = schema["stats"];
			Dictionary<string, int> byType = [];

			foreach (KeyValue n in statsNode.Children) {
				string t = n["type"].AsString() ?? "(none)";
				byType[t] = byType.GetValueOrDefault(t) + 1;
			}

			Log.Debug($"stat types: {string.Join(", ", byType.Select(static kv => kv.Key + " x" + kv.Value))}", bot.Name);

			KeyValue first = statsNode.Children.FirstOrDefault() ?? new KeyValue();
			Log.Debug($"first stat '{first.Name}' children: {string.Join(", ", first.Children.Select(static c => c.Name + "=" + (c.Children.Count > 0 ? "[" + c.Children.Count + "]" : c.Value)))}", bot.Name);
		}

		string appKey = appId.ToString(CultureInfo.InvariantCulture);
		KeyValue root = schema.Name == appKey ? schema : schema.Children.FirstOrDefault(k => k.Name == appKey) ?? schema;

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

				// permission 2 means Steam awards it server-side and refuses a client write.
				bool locked = (bitNode["permission"].AsInteger() & 2) != 0;

				string display = bitNode["display"]["name"].Children.FirstOrDefault(static k => k.Name == "english")?.Value
					?? bitNode["display"]["name"].AsString()
					?? apiName;

				// The bit is an offset within THIS stat's value, not a global achievement index.
				bool unlocked = (statValues.GetValueOrDefault(statId) & (1u << (bit & 31))) != 0;

				all.Add(new Achievement {
					Name = apiName,
					Display = display,
					StatId = statId,
					Bit = bit,
					Unlocked = unlocked,
					Protected = locked
				});
			}
		}

		AchievementSet set = new() { AppId = appId, All = all, StatValues = statValues, CrcStats = response.crc_stats };
		await AddGlobalPercentagesAsync(set, ct).ConfigureAwait(false);

		return set;
	}

	/// <summary>
	/// Unlock (or re-lock) a set of achievements in one store request.
	///
	/// One request, not one per achievement: Steam rate-limits stat writes hard, and a hundred separate stores
	/// is both slower and far more likely to be refused halfway through, leaving the game in a state nobody
	/// asked for.
	/// </summary>
	public static async Task<(bool Ok, string Message)> SetAsync(Bot bot, AchievementSet set, IEnumerable<Achievement> which, bool unlock, CancellationToken ct = default) {
		if (bot.Stats == null) {
			return (false, "not connected");
		}

		// ONLY the stats that actually change go in the request.
		//
		// Sending the whole set back - all 775 of them for Team Fortress 2 - gets the lot refused with
		// InvalidParam, because most of a game's stats are ordinary counters and many are marked increment-only.
		// Re-submitting those at their existing value is not a no-op to Steam, it is an invalid write.
		Dictionary<uint, uint> changed = [];
		int touched = 0;

		foreach (Achievement achievement in which) {
			if (!achievement.Settable) {
				continue;
			}

			// Read-modify-write on the stat this achievement lives in. Writing a bare bit instead of the modified
			// current value would clear every other achievement sharing that stat.
			uint current = changed.TryGetValue(achievement.StatId, out uint pending) ? pending : set.StatValues.GetValueOrDefault(achievement.StatId);
			uint mask = 1u << (achievement.Bit & 31);
			uint updated = unlock ? current | mask : current & ~mask;

			if (updated == current) {
				continue;
			}

			changed[achievement.StatId] = updated;
			touched++;
		}

		if (touched == 0) {
			return (true, "nothing to change");
		}

		EResult result = await bot.Stats.StoreUserStatsAsync(set.AppId, bot.SteamId, changed, set.CrcStats, ct).ConfigureAwait(false);

		return result == EResult.OK
			? (true, $"{touched} achievement(s) {(unlock ? "unlocked" : "re-locked")}")
			: (false, $"Steam refused it ({result})");
	}

	/// <summary>
	/// How many people worldwide have each achievement.
	///
	/// Public endpoint, no key, and the answer never really changes - so it is fetched once per app per run and
	/// kept. It is what lets the drip go easiest-first instead of alphabetically, which is the difference
	/// between a plausible unlock history and an obviously generated one.
	/// </summary>
	private static async Task AddGlobalPercentagesAsync(AchievementSet set, CancellationToken ct) {
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
					fetched = [];
					using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

					if (doc.RootElement.TryGetProperty("achievementpercentages", out JsonElement wrapper)
						&& wrapper.TryGetProperty("achievements", out JsonElement rows)) {
						foreach (JsonElement row in rows.EnumerateArray()) {
							string? name = row.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;

							if (string.IsNullOrEmpty(name) || !row.TryGetProperty("percent", out JsonElement p)) {
								continue;
							}

							fetched[name] = p.ValueKind == JsonValueKind.Number ? p.GetDouble()
								: double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
						}
					}
				}
			} catch (Exception e) {
				Log.Debug($"couldn't read global achievement rates for {set.AppId}: {e.Message}");
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
	private readonly Dictionary<JobID, TaskCompletionSource<EResult>> _pendingStores = [];

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

				lock (_pendingStores) {
					if (_pendingStores.Remove(packetMsg.TargetJobID, out TaskCompletionSource<EResult>? waiting)) {
						waiting.TrySetResult((EResult) msg.Body.eresult);
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

	public async Task<EResult> StoreUserStatsAsync(uint appId, ulong steamId, Dictionary<uint, uint> statValues, uint crcStats, CancellationToken ct = default) {
		if (Client == null) {
			return EResult.NoConnection;
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

		TaskCompletionSource<EResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		lock (_pendingStores) {
			_pendingStores[request.SourceJobID] = answer;
		}

		Client.Send(request);

		(bool ok, EResult result) = await WaitAsync(answer.Task, () => Forget(_pendingStores, request.SourceJobID), ct).ConfigureAwait(false);

		return ok ? result : EResult.Timeout;
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
