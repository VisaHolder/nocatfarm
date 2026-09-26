using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;
using SteamKit2;
using SteamKit2.WebUI.Internal;

namespace NocatFarm.Modules;

/// <summary>
/// Picks up the free things Steam hands out: the daily sticker during a sale, and anything in the Points Shop
/// that costs no points. Neither costs anything, and both are gone if nobody collects them.
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
	private HashSet<uint>? _taken;
	private Said _status = new("");

	public override string Name => "event items";
	public override string Status => Bot.Cfg.ClaimEventItems ? _status : "";

	private string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", $"freeitems-{Bot.Name}.json");

	protected override async Task RunAsync(CancellationToken ct) {
		_taken ??= Load();

		// Not in the first minutes after signing in, with everything else.
		if (!await Sleep(Rng.Minutes(2, 6), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.Cfg.ClaimEventItems && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && HumanMode.AwakeFor(Bot)) {
				try {
					if (DateTime.UtcNow >= _nextSticker) {
						await StickerAsync(ct).ConfigureAwait(false);
					}

					if (DateTime.UtcNow >= _nextShop) {
						await ShopAsync(ct).ConfigureAwait(false);
					}
				} catch (OperationCanceledException) {
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
	public async Task<bool> StickerAsync(CancellationToken ct) {
		string? can = await Bot.Web.ApiGetAsync("ISaleItemRewardsService", "CanClaimItem", new Dictionary<string, string> { ["language"] = "english" }, ct).ConfigureAwait(false);

		if (can == null) {
			_nextSticker = DateTime.UtcNow.AddHours(1);

			return false;
		}

		using JsonDocument canDoc = JsonDocument.Parse(can);
		JsonElement canBody = canDoc.RootElement.TryGetProperty("response", out JsonElement r) ? r : default;
		bool claimable = (canBody.ValueKind == JsonValueKind.Object) && canBody.TryGetProperty("can_claim", out JsonElement c) && (c.ValueKind == JsonValueKind.True);

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
