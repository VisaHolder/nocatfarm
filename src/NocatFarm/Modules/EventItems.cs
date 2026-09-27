using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;
using SteamKit2;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;

namespace NocatFarm.Modules;

/// <summary>
/// Picks up the free things Steam hands out: the daily sticker during a sale, anything in the Points Shop that
/// costs no points, and the daily discovery queue - which during a sale is what earns the event's items and badge.
/// </summary>
/// <remarks>
/// The sale sticker is one claim a day while a sale is on, and Steam says exactly when the next one opens - so
/// that's when it asks again, and outside a sale it asks a few times a day to notice one starting. The Points
/// Shop has no "free" list of its own: every item in it is read (once for every account - it's the same shop)
/// and the ones at 0 points are taken, remembering which ones each account already has.
/// </remarks>
public sealed class EventItems(Bot bot) : BotModule(bot) {
	private static readonly SemaphoreSlim ShopGate = new(1, 1);
	private static List<(uint DefId, uint AppId)> _freeInShop = [];
	private static DateTime _shopReadAt = DateTime.MinValue;

	private DateTime _nextSticker = DateTime.MinValue;
	private DateTime _nextShop = DateTime.MinValue;

	/// <summary>When Steam last said a sale was on - the sticker check is how it knows.</summary>
	private DateTime _saleSeen = DateTime.MinValue;

	/// <summary>The Steam day the queue was last gone through (Steam's day turns over at 10:00 Pacific).</summary>
	private string? _queueDay;
	private DateTime _queueAt = DateTime.MinValue;
	private HashSet<uint>? _taken;
	private Said _status = new("");

	public override string Name => "event items";
	public override string Status => Bot.Cfg.ClaimEventItems || (Bot.Cfg.DiscoveryQueue > 0) ? _status : "";

	private string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", $"freeitems-{Bot.Name}.json");

	protected override async Task RunAsync(CancellationToken ct) {
		_taken ??= Load();

		// Not in the first minutes after signing in, with everything else.
		if (!await Sleep(Rng.Minutes(2, 6), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			bool wanted = Bot.Cfg.ClaimEventItems || (Bot.Cfg.DiscoveryQueue > 0);

			if (wanted && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && HumanMode.AwakeFor(Bot)) {
				try {
					// The sticker check also tells us whether a sale is on, which the queue needs to know.
					if (DateTime.UtcNow >= _nextSticker) {
						await StickerAsync(ct, claim: Bot.Cfg.ClaimEventItems).ConfigureAwait(false);
					}

					if (Bot.Cfg.ClaimEventItems && (DateTime.UtcNow >= _nextShop)) {
						await ShopAsync(ct).ConfigureAwait(false);
					}

					if (QueueDue()) {
						await QueueAsync(ct).ConfigureAwait(false);
					}
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't check for free event items: {0}", e.Message), Bot.Name);
				}

				DateTime next = _nextSticker < _nextShop ? _nextSticker : _nextShop;
				_status = new Said("next look for free items around {0}", (Func<string>) (() => Fmt.Clock(next)));
			}

			if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>The daily sale sticker. Returns whether one was claimed.</summary>
	/// <param name="claim">False only looks - to learn whether a sale is on - without taking anything.</param>
	public async Task<bool> StickerAsync(CancellationToken ct, bool claim = true) {
		string? can = await Bot.Web.ApiGetAsync("ISaleItemRewardsService", "CanClaimItem", new Dictionary<string, string> { ["language"] = "english" }, ct).ConfigureAwait(false);

		if (can == null) {
			_nextSticker = DateTime.UtcNow.AddHours(1);

			return false;
		}

		using JsonDocument canDoc = JsonDocument.Parse(can);
		JsonElement canBody = canDoc.RootElement.TryGetProperty("response", out JsonElement r) ? r : default;
		bool claimable = (canBody.ValueKind == JsonValueKind.Object) && canBody.TryGetProperty("can_claim", out JsonElement c) && (c.ValueKind == JsonValueKind.True);

		// Outside a sale Steam gives neither a claim nor a time for the next one.
		if (claimable || (NextClaim(canBody) != null)) {
			_saleSeen = DateTime.UtcNow;
		}

		if (!claim) {
			_nextSticker = NextClaim(canBody) ?? DateTime.UtcNow + Rng.Minutes(180, 300);

			return false;
		}

		if (!claimable) {
			// During a sale Steam says when the next one opens; outside one it says nothing, and a sale starting
			// is noticed on one of the looks a few hours apart.
			_nextSticker = NextClaim(canBody) ?? DateTime.UtcNow + Rng.Minutes(180, 300);

			return false;
		}

		await Task.Delay(Rng.Seconds(3, 10), ct).ConfigureAwait(false);
		string? claimed = await Bot.Web.ApiPostAsync("ISaleItemRewardsService", "ClaimItem", new Dictionary<string, string> { ["language"] = "english" }, ct).ConfigureAwait(false);

		if (claimed == null) {
			_nextSticker = DateTime.UtcNow.AddHours(1);

			return false;
		}

		using JsonDocument doc = JsonDocument.Parse(claimed);
		JsonElement body = doc.RootElement.TryGetProperty("response", out JsonElement b) ? b : default;
		string? item = null;

		if ((body.ValueKind == JsonValueKind.Object) && body.TryGetProperty("reward_item", out JsonElement reward)
			&& reward.TryGetProperty("community_item_data", out JsonElement data) && data.TryGetProperty("item_name", out JsonElement name)) {
			item = name.GetString();
		}

		if (item != null) {
			Log.Reward(new Said("claimed the free sale item: {0}", item), Bot.Name);
		} else {
			Log.Info(new Said("claimed the free sale item"), Bot.Name);
		}

		_nextSticker = NextClaim(body) ?? DateTime.UtcNow + Rng.Minutes(180, 300);

		return true;
	}

	private static DateTime? NextClaim(JsonElement body) {
		if ((body.ValueKind != JsonValueKind.Object) || !body.TryGetProperty("next_claim_time", out JsonElement next)) {
			return null;
		}

		long unix = next.ValueKind == JsonValueKind.Number ? next.GetInt64() : long.TryParse(next.GetString(), out long n) ? n : 0;

		if (unix <= 0) {
			return null;
		}

		DateTime at = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.AddMinutes(Rng.Next(2, 40));

		return at > DateTime.UtcNow ? at : DateTime.UtcNow.AddMinutes(Rng.Next(5, 20));
	}

	// ── the discovery queue ──────────────────────────────────────────────────
	/// <summary>Steam's day, which turns over at 10:00 Pacific - 17:00 UTC is close enough all year round.</summary>
	private static string SteamDay() => DateTime.UtcNow.AddHours(-17).ToString("yyyy-MM-dd");

	private string QueuePath => Path.Combine(ConfigStore.ConfigDir, "state", $"queue-{Bot.Name}.txt");

	private bool QueueDue() {
		bool on = Bot.Cfg.DiscoveryQueue switch {
			1 => DateTime.UtcNow - _saleSeen < TimeSpan.FromHours(30),
			2 => true,
			_ => false
		};

		if (!on || (DateTime.UtcNow < _queueAt)) {
			return false;
		}

		if (_queueDay == null) {
			try {
				_queueDay = File.Exists(QueuePath) ? File.ReadAllText(QueuePath).Trim() : "";
			} catch (IOException) {
				_queueDay = "";
			}
		}

		return _queueDay != SteamDay();
	}

	/// <summary>
	/// Go through today's discovery queue: each game looked at for a few seconds, the way somebody clicking "next in
	/// queue" does, never the dozen at once a script would. Returns how many were seen, or -1 if Steam wouldn't answer.
	/// </summary>
	public async Task<int> QueueAsync(CancellationToken ct) {
		// Somewhere in the day rather than on the stroke of waking: a person gets to it when they get to it.
		_queueAt = DateTime.UtcNow + Rng.Minutes(20, 90);

		if (Bot.Unified?.CreateService<Store>() is not { } store) {
			return -1;
		}

		// The store's own page always says which country it's asking for; without one Steam just answers Fail. A
		// refusal gets one more try with a freshly built queue, the store's "start a new queue" button.
		string country = Bot.Country.Length == 2 ? Bot.Country : "US";
		SteamUnifiedMessages.ServiceMethodResponse<CStore_GetDiscoveryQueue_Response> queue = await store
			.GetDiscoveryQueue(new CStore_GetDiscoveryQueue_Request { queue_type = EStoreDiscoveryQueueType.k_EStoreDiscoveryQueueTypeNew, country_code = country, rebuild_queue_if_stale = true })
			.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

		if (queue.Result != EResult.OK) {
			await Task.Delay(Rng.Seconds(3, 8), ct).ConfigureAwait(false);
			queue = await store
				.GetDiscoveryQueue(new CStore_GetDiscoveryQueue_Request { queue_type = EStoreDiscoveryQueueType.k_EStoreDiscoveryQueueTypeNew, country_code = country, rebuild_queue = true })
				.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		}

		if (queue.Result != EResult.OK) {
			Log.Debug(new Said("the discovery queue wasn't available - Steam said {0}", queue.Result), Bot.Name);

			return -1;
		}

		int seen = 0;

		foreach (uint app in queue.Body.appids) {
			await Task.Delay(Rng.Seconds(6, 25), ct).ConfigureAwait(false);

			SteamUnifiedMessages.ServiceMethodResponse<CStore_SkipDiscoveryQueueItem_Response> skip = await store
				.SkipDiscoveryQueueItem(new CStore_SkipDiscoveryQueueItem_Request { queue_type = EStoreDiscoveryQueueType.k_EStoreDiscoveryQueueTypeNew, appid = app })
				.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

			if (skip.Result == EResult.OK) {
				seen++;
			}
		}

		if ((seen > 0) || (queue.Body.appids.Count == 0)) {
			_queueDay = SteamDay();

			try {
				AtomicFile.Write(QueuePath, _queueDay);
			} catch (IOException) {
				// Worst case it goes through the queue again after a restart.
			}

			Log.Info(new Said("went through the discovery queue ({0} game(s))", seen), Bot.Name);
		}

		return seen;
	}

	/// <summary>Take whatever the Points Shop has at 0 points. Returns how many were taken.</summary>
	public async Task<int> ShopAsync(CancellationToken ct) {
		_nextShop = DateTime.UtcNow + Rng.Minutes(8 * 60, 12 * 60);

		if (Bot.Unified?.CreateService<LoyaltyRewards>() is not { } shop) {
			return 0;
		}

		List<(uint DefId, uint AppId)> free = await FreeInShopAsync(shop, ct).ConfigureAwait(false);
		_taken ??= Load();
		int got = 0;

		foreach ((uint defId, uint appId) in free.Where(f => !_taken.Contains(f.DefId))) {
			await Task.Delay(Rng.Seconds(3, 9), ct).ConfigureAwait(false);

			SteamUnifiedMessages.ServiceMethodResponse<CLoyaltyRewards_RedeemPoints_Response> answer = await shop
				.RedeemPoints(new CLoyaltyRewards_RedeemPoints_Request { defid = defId, expected_points_cost = 0 })
				.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

			if (answer.Result == EResult.OK) {
				got++;
				Log.Reward(new Said("took a free Points Shop item from {0}", GameNames.Of(appId)), Bot.Name);
			} else if (answer.Result is EResult.Timeout or EResult.ServiceUnavailable or EResult.Busy or EResult.TryAnotherCM or EResult.RateLimitExceeded) {
				continue;   // try it again next time
			} else {
				Log.Debug(new Said("Points Shop item {0} not taken - Steam said {1}", defId, answer.Result), Bot.Name);
			}

			// Taken, already owned, or refused for good - either way, not asked for again.
			_taken.Add(defId);
		}

		Save();

		return got;
	}

	/// <summary>
	/// Every 0-point item in the Points Shop. Read by whichever account asks first and shared, since the shop
	/// is the same for everyone - reading it page by page for each account would be the same answer three times.
	/// </summary>
	private static async Task<List<(uint DefId, uint AppId)>> FreeInShopAsync(LoyaltyRewards shop, CancellationToken ct) {
		await ShopGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			if (DateTime.UtcNow - _shopReadAt < TimeSpan.FromHours(6)) {
				return _freeInShop;
			}

			List<(uint, uint)> free = [];
			string? cursor = null;
			uint now = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();

			for (int page = 0; page < 500; page++) {
				CLoyaltyRewards_QueryRewardItems_Request request = new() { count = 1000, include_direct_purchase_disabled = true, language = "english" };

				if (cursor != null) {
					request.cursor = cursor;
				}

				SteamUnifiedMessages.ServiceMethodResponse<CLoyaltyRewards_QueryRewardItems_Response> answer = await shop.QueryRewardItems(request)
					.ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

				if (answer.Result != EResult.OK) {
					return _freeInShop;   // keep the last good read rather than a partial one
				}

				foreach (LoyaltyRewardDefinition d in answer.Body.definitions) {
					bool noPoints = (d.point_cost == 0) || (d.timestamp_free_until > now);

					if (noPoints && d.active && (d.bundle_defids.Count == 0) && (d.defid != 0)) {
						free.Add((d.defid, d.appid));
					}
				}

				cursor = answer.Body.next_cursor;

				if (string.IsNullOrEmpty(cursor) || (answer.Body.definitions.Count == 0)) {
					break;
				}

				await Task.Delay(TimeSpan.FromMilliseconds(Rng.Next(700, 1500)), ct).ConfigureAwait(false);
			}

			_freeInShop = free;
			_shopReadAt = DateTime.UtcNow;
			Log.Debug(new Said("the Points Shop has {0} item(s) at 0 points", free.Count));

			return free;
		} finally {
			ShopGate.Release();
		}
	}

	private HashSet<uint> Load() {
		try {
			if (File.Exists(StatePath) && JsonSerializer.Deserialize<List<uint>>(File.ReadAllText(StatePath)) is { } saved) {
				return [.. saved];
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the free-item list: {0}", e.Message), Bot.Name);
		}

		return [];
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(_taken?.Order().ToList() ?? []));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the free-item list: {0}", e.Message), Bot.Name);
		}
	}
}
