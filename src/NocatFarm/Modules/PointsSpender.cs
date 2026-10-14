using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;
using SteamKit2;
using SteamKit2.WebUI.Internal;

namespace NocatFarm.Modules;

/// <summary>
/// What the Points Shop sells that raises a Steam level, read from Steam's own answers.
/// </summary>
/// <remarks>
/// Checked against Steam itself in October 2026 rather than guessed:
/// <list type="bullet">
/// <item>The only thing in the Points Shop that gives XP is a levelled badge: a definition of reward type 2 and community item
/// class 1 (badge), with one <c>badge_data</c> entry per level. ILoyaltyRewardsService/QueryRewardItems with reward_types=[2]
/// lists them - "Summer Collection - 2026" (appid 4844660) and "Summer Collection - 2024" (appid 3027110) then. Profile items,
/// emoticons, backgrounds and bundles give none.</item>
/// <item>Each level costs <c>point_cost</c> (1,000) and is worth 100 XP (40 levels, 100 to 4,000 XP). The store page charges
/// <c>point_cost * (target - level owned)</c> and calls RedeemPointsForBadgeLevel with <c>num_levels</c> = that difference, at
/// least 1 - so <c>num_levels</c> is how many levels to ADD (Steam's loyaltystore.js, RedeemPointsForBadge; ArchiSteamFarm's
/// Actions.RedeemPoints sends a type-2 reward the same way).</item>
/// <item>The store's "current seasonal badge" is a fixed app in its script (4844660 then) - the newest of the type-2 badges.
/// Older ones may still be listed; whether Steam still sells their levels isn't promised, so buying them is a separate switch,
/// off by default.</item>
/// <item>The level owned is the account's community item for that app and item type, attribute 2 (IQuestService/
/// GetCommunityInventory with filter_appids - what the store page itself asks).</item>
/// </list>
/// </remarks>
public static class PointsShop {
	/// <summary>Steam's XP for one level of a Points Shop badge (100 for level 1 ... 4,000 for level 40).</summary>
	public const int XpPerLevel = 100;

	/// <summary>LoyaltyRewardDefinition.type of a badge whose levels are bought with points.</summary>
	public const int BadgeRewardType = 2;

	/// <summary>community_item_class of a badge.</summary>
	public const int BadgeClass = 1;

	/// <summary>The community item attribute holding a badge's level.</summary>
	public const int LevelAttribute = 2;

	/// <summary>One badge whose levels the shop sells. <paramref name="Current"/>: the newest - the store's seasonal badge.</summary>
	public sealed record Badge(uint DefId, uint AppId, string Name, long CostPerLevel, int MaxLevel, uint ItemType, DateTime Created, bool Current);

	/// <summary>
	/// The badges a QueryRewardItems answer (the Web API's JSON) sells levels of, newest first. Anything else in it - profile
	/// items, bundles, a badge not on sale now or with nothing to buy - is left out. Null when it isn't an answer at all.
	/// </summary>
	public static List<Badge>? ParseBadges(string? json, DateTime nowUtc) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("response", out JsonElement response) || (response.ValueKind != JsonValueKind.Object)) {
				return null;
			}

			List<Badge> badges = [];
			long now = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds();

			if (response.TryGetProperty("definitions", out JsonElement defs) && (defs.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement d in defs.EnumerateArray()) {
					if ((Num(d, "type") != BadgeRewardType) || (Num(d, "community_item_class") != BadgeClass) || !Flag(d, "active")) {
						continue;
					}

					long from = Num(d, "timestamp_available"), until = Num(d, "timestamp_available_end");

					if (((from > 0) && (from > now)) || ((until > 0) && (until <= now))) {
						continue;   // not on sale yet, or not any more
					}

					long cost = Num(d, "point_cost");
					uint defId = (uint) Math.Clamp(Num(d, "defid"), 0, uint.MaxValue);
					uint appId = (uint) Math.Clamp(Num(d, "appid"), 0, uint.MaxValue);
					int levels = 0;
					string name = "";

					if (d.TryGetProperty("community_item_data", out JsonElement data) && (data.ValueKind == JsonValueKind.Object)) {
						name = Text(data, "item_title") is { Length: > 0 } title ? title : Text(data, "item_name");

						if (data.TryGetProperty("badge_data", out JsonElement levelList) && (levelList.ValueKind == JsonValueKind.Array)) {
							levels = levelList.EnumerateArray().Select(static l => (int) Math.Clamp(Num(l, "level"), 0, 1000)).DefaultIfEmpty(0).Max();
						}
					}

					// A price of 0 is the free-item path's business (EventItems), and a badge with no levels has nothing to sell.
					if ((cost <= 0) || (levels <= 0) || (defId == 0) || (appId == 0)) {
						continue;
					}

					badges.Add(new Badge(defId, appId, name.Length > 0 ? name : $"app {appId}", cost, levels,
						(uint) Math.Clamp(Num(d, "community_item_type"), 0, uint.MaxValue),
						DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, Num(d, "timestamp_created"))).UtcDateTime, Current: false));
				}
			}

			badges = [.. badges.OrderByDescending(static b => b.Created).ThenByDescending(static b => b.AppId)];

			if (badges.Count > 0) {
				badges[0] = badges[0] with { Current = true };
			}

			return badges;
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>
	/// The level of every item the account owns in a GetCommunityInventory answer, by app and item type. A badge not in the
	/// list isn't owned - level 0. An owned item with no level in it is -1, unknown: nobody can say what a purchase of that
	/// badge would start from, so it isn't bought (other items of the app - a profile item, say - have no level, and don't
	/// matter). Null when the answer can't be read at all.
	/// </summary>
	public static Dictionary<(uint AppId, uint ItemType), int>? ParseLevels(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return null;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (!doc.RootElement.TryGetProperty("response", out JsonElement response) || (response.ValueKind != JsonValueKind.Object)) {
				return null;
			}

			Dictionary<(uint, uint), int> levels = [];

			if (!response.TryGetProperty("items", out JsonElement items)) {
				return levels;   // Steam leaves the list out altogether when there's nothing
			}

			if (items.ValueKind != JsonValueKind.Array) {
				return null;
			}

			foreach (JsonElement item in items.EnumerateArray()) {
				uint app = (uint) Math.Clamp(Num(item, "appid"), 0, uint.MaxValue);
				uint type = (uint) Math.Clamp(Num(item, "item_type"), 0, uint.MaxValue);
				int? level = null;

				if (item.TryGetProperty("attributes", out JsonElement attributes) && (attributes.ValueKind == JsonValueKind.Array)) {
					foreach (JsonElement a in attributes.EnumerateArray()) {
						if (Num(a, "attributeid") == LevelAttribute) {
							level = (int) Math.Clamp(Num(a, "value"), 0, 1000);
						}
					}
				}

				// The same item twice (it shouldn't be): the higher level; -1 only when neither says.
				levels[(app, type)] = levels.TryGetValue((app, type), out int seen) ? Math.Max(seen, level ?? -1) : level ?? -1;
			}

			return levels;
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>A JSON number Steam may send as a number or, for 64-bit values, as text. 0 when it's missing or neither.</summary>
	private static long Num(JsonElement e, string name) {
		if ((e.ValueKind != JsonValueKind.Object) || !e.TryGetProperty(name, out JsonElement v)) {
			return 0;
		}

		return v.ValueKind switch {
			JsonValueKind.Number when v.TryGetInt64(out long n) => n,
			JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => n,
			_ => 0
		};
	}

	private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True);

	private static string Text(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? (v.GetString() ?? "").Trim() : "";
}

/// <summary>Levels of one badge to buy: from the level owned, how many more, for how many points.</summary>
public sealed record PointsStep(PointsShop.Badge Badge, int From, int Levels) {
	public long Cost => Badge.CostPerLevel * Levels;
	public int To => From + Levels;
	public int Xp => Levels * PointsShop.XpPerLevel;
}

/// <summary>What an account's points would buy now, within its settings - and, when nothing, why.</summary>
public sealed class PointsPlan {
	public long Balance { get; init; }
	public long Keep { get; init; }
	public long WeekCap { get; init; }
	public long WeekSpent { get; init; }

	/// <summary>What may be spent now: above what's kept, and inside this week's limit.</summary>
	public long Spare { get; init; }

	public List<PointsStep> Steps { get; } = [];

	/// <summary>Why something wasn't bought, in plain words (English - console replies stay English).</summary>
	public List<string> Notes { get; } = [];

	public long Cost => Steps.Sum(static s => s.Cost);
	public int Xp => Steps.Sum(static s => s.Xp);

	/// <summary>
	/// The plan: the seasonal badge first, then (when allowed) older ones newest first, as many levels of each as the spare
	/// points pay for, up to the badge's last level and "Stop each badge at level". Nothing on a badge whose level owned
	/// isn't known.
	/// </summary>
	public static PointsPlan Make(BotConfig cfg, long balance, IReadOnlyList<PointsShop.Badge> badges,
		IReadOnlyDictionary<(uint AppId, uint ItemType), int>? owned, long weekSpent) {
		long keep = Math.Max(0, cfg.PointsKeepAtLeast);
		long cap = Math.Max(0, cfg.PointsWeeklyCap);
		long spare = Math.Max(0, Math.Min(balance - keep, cap - weekSpent));
		PointsPlan plan = new() { Balance = balance, Keep = keep, WeekCap = cap, WeekSpent = weekSpent, Spare = spare };
		long left = spare;

		if (badges.Count == 0) {
			plan.Notes.Add("nothing in the Points Shop gives levels right now - it sells a seasonal badge during the big Steam sales");

			return plan;
		}

		foreach (PointsShop.Badge badge in badges) {
			if (badge.Current ? !cfg.PointsBuySeasonalBadge : !cfg.PointsBuyOlderBadges) {
				if (badge.Current) {
					plan.Notes.Add($"{badge.Name}: buying levels of the seasonal badge is switched off (\"Buy levels of the seasonal badge\")");
				}

				continue;
			}

			// Not in a readable inventory is not owned; an unreadable inventory, or an owned badge with no level, is unknown.
			int level = owned == null ? -1 : owned.GetValueOrDefault((badge.AppId, badge.ItemType), 0);

			if (level < 0) {
				plan.Notes.Add($"{badge.Name}: couldn't read which level it has, so nothing is bought");

				break;   // nor an older badge in its place
			}

			int top = Math.Min(badge.MaxLevel, Math.Clamp(cfg.PointsBadgeMaxLevel, 1, 1000));

			if (level >= top) {
				plan.Notes.Add($"{badge.Name}: already at level {level}{(top < badge.MaxLevel ? $" (\"Stop each badge at level\" is {top})" : " - the last one")}");

				continue;
			}

			int afford = (int) Math.Min(int.MaxValue, left / badge.CostPerLevel);
			int levels = Math.Min(top - level, afford);

			if (levels <= 0) {
				plan.Notes.Add($"{badge.Name}: one level costs {N(badge.CostPerLevel)} points and {N(left)} can be spent{Why(plan)}");

				break;   // a cheaper older badge isn't bought ahead of the seasonal one
			}

			plan.Steps.Add(new PointsStep(badge, level, levels));
			left -= levels * badge.CostPerLevel;
		}

		return plan;
	}

	/// <summary>What holds the spare points down: the points kept, or this week's limit.</summary>
	private static string Why(PointsPlan p) =>
		p.WeekCap - p.WeekSpent <= p.Balance - p.Keep
			? $" (this week's limit is {N(p.WeekCap)}, {N(p.WeekSpent)} spent)"
			: $" (it keeps {N(p.Keep)} of {N(p.Balance)})";

	internal static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>
/// What spending has gone on, per account - for the weekly limit, and so it survives a restart - and whether the owner has
/// turned spending on for it (its go-ahead). Both in config/state/points-&lt;account&gt;.json.
/// </summary>
/// <remarks>
/// The ok lives here, not in the account's settings, so a settings file or a backup can never bring it with it: a restore
/// keeps this PC's own ok (Backup.WriteFiles). A file from before has only the list; an ok it had in the settings is moved
/// over once (<see cref="Approved"/>).
/// </remarks>
public sealed class PointsLedger(string path) {
	/// <param name="From">The level owned before the purchase.</param>
	/// <param name="Pending">Steam didn't answer: counted as spent until a later look sees the badge's level.</param>
	public sealed record Entry(DateTime At, long Points, uint DefId, int Levels, string Badge, int From = 0, bool Pending = false);

	/// <summary>The file as written now.</summary>
	internal sealed class Stored {
		public bool Ok { get; set; }

		/// <summary>An ok kept in the account's settings (before 1.8.0's fixes) has been moved here, or there was none.</summary>
		public bool Migrated { get; set; }

		public List<Entry> Entries { get; set; } = [];
	}

	private List<Entry>? _entries;
	private bool _ok, _migrated, _onDisk;
	private readonly Lock _gate = new();

	/// <summary>One per account for the whole program, so 'points on' typed while a look runs isn't saved over.</summary>
	private static readonly Dictionary<string, PointsLedger> Loaded = new(StringComparer.OrdinalIgnoreCase);

	public static PointsLedger Of(string account) {
		string file = Path.Combine(ConfigStore.ConfigDir, "state", $"points-{account}.json");

		lock (Loaded) {
			// Removed and added again by the same name: its file went with it, and so does its ok.
			if (Loaded.TryGetValue(file, out PointsLedger? held) && (!held._onDisk || File.Exists(file))) {
				return held;
			}

			PointsLedger fresh = new(file);
			Loaded[file] = fresh;

			return fresh;
		}
	}

	/// <summary>As if the program had just started (the checks, and a restore): every ledger is read from its file again.</summary>
	public static void ForgetLoaded() {
		lock (Loaded) {
			Loaded.Clear();
		}
	}

	/// <summary>
	/// Whether this account may really spend: the owner turned it on ('points on', or the switch after its preview). An ok still in its settings from before is
	/// moved over the first time, then cleared there - and a settings file saying it later (an old one, restored) is never
	/// read again.
	/// </summary>
	public static bool Approved(Bot bot) {
		PointsLedger ledger = Of(bot.Name);
		bool moved = false;

		lock (ledger._gate) {
			ledger.Load();

			if (!ledger._migrated) {
				bool legacy;

				lock (bot.CfgGate) {
					legacy = bot.Cfg.PointsOk;
				}

				// Looked at once, ok or not: with none to move, a PointsOk in the settings later (a hand edit and 'reload', a
				// rollback copy) was still taken as an ok the next time.
				ledger._ok |= legacy;
				ledger._migrated = true;
				ledger.Save();
				moved = legacy;
			}
		}

		if (moved) {
			lock (bot.CfgGate) {
				bot.Cfg.PointsOk = false;

				if (File.Exists(Path.Combine(ConfigStore.ConfigDir, bot.Name + ".json"))) {
					ConfigStore.SaveBot(bot.Name, bot.Cfg);
				}
			}
		}

		return ledger.Ok;
	}

	public bool Ok {
		get {
			lock (_gate) {
				Load();

				return _ok;
			}
		}
	}

	/// <summary>Spending turned on (true) or off (false) - 'points on|off' or the switch - saved.</summary>
	public void SetOk(bool ok) {
		lock (_gate) {
			Load();
			_ok = ok;
			_migrated = true;
			Save();
		}
	}

	public long SpentSince(DateTime utc) {
		lock (_gate) {
			return Load().Where(e => e.At >= utc).Sum(static e => e.Points);
		}
	}

	public IReadOnlyList<Entry> All {
		get {
			lock (_gate) {
				return [.. Load()];
			}
		}
	}

	public void Add(Entry entry) {
		lock (_gate) {
			List<Entry> all = Load();
			all.Add(entry);
			all.RemoveAll(static e => e.At < DateTime.UtcNow.AddDays(-400));   // a year and a bit is plenty
			Save();
		}
	}

	/// <summary>
	/// Purchases Steam didn't answer about, put right from the level the account owns now: there by the level bought - kept,
	/// no longer pending; still at the level it started from - taken off. <paramref name="levelOf"/> gives a badge's level by
	/// its defid, or null when it isn't known (then it stays pending, and counted). Returns what changed, for the log.
	/// </summary>
	public List<(Entry Was, int Level, bool Bought)> Settle(Func<uint, int?> levelOf) {
		List<(Entry, int, bool)> changed = [];

		lock (_gate) {
			List<Entry> all = Load();

			for (int i = all.Count - 1; i >= 0; i--) {
				Entry e = all[i];

				if (!e.Pending || (levelOf(e.DefId) is not int level) || (level < 0)) {
					continue;
				}

				int got = Math.Clamp(level - e.From, 0, e.Levels);

				if (got <= 0) {
					all.RemoveAt(i);
				} else {
					all[i] = e with { Pending = false, Levels = got, Points = e.Levels > 0 ? e.Points / e.Levels * got : e.Points };
				}

				changed.Add((e, level, got > 0));
			}

			if (changed.Count > 0) {
				Save();
			}
		}

		return changed;
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(new Stored { Ok = _ok, Migrated = _migrated, Entries = _entries ?? [] }));
			_onDisk = true;
		} catch (Exception e) {
			// Kept in memory either way, so this run's weekly limit still holds.
			Log.Debug(new Said("couldn't save the Steam Points spending list: {0}", Log.Describe(e)));
		}
	}

	/// <summary>The list and the ok from a file's text - the list alone (before), or both (now). Null when it can't be read.</summary>
	internal static Stored? Parse(string text) {
		string t = text.TrimStart();

		return t.StartsWith('[') ? new Stored { Entries = JsonSerializer.Deserialize<List<Entry>>(t) ?? [] }
			: JsonSerializer.Deserialize<Stored>(t) is { } s ? new Stored { Ok = s.Ok, Migrated = s.Migrated, Entries = s.Entries ?? [] } : null;
	}

	private List<Entry> Load() {
		if (_entries != null) {
			return _entries;
		}

		try {
			Stored? read = File.Exists(path) ? Parse(File.ReadAllText(path)) ?? throw new JsonException("empty") : null;
			_onDisk = read != null;
			_entries = read?.Entries ?? [];
			_ok = read?.Ok ?? false;
			_migrated = read?.Migrated ?? false;
		} catch (Exception e) {
			// A list that can't be read counts this week as already spent, rather than as nothing spent - and no ok.
			Log.Debug(new Said("couldn't read the Steam Points spending list: {0}", Log.Describe(e)));
			_entries = [new Entry(DateTime.UtcNow, long.MaxValue / 4, 0, 0, "unreadable spending list")];
			_ok = false;
			_migrated = true;
		}

		return _entries;
	}
}

/// <summary>The few things spending points needs from Steam. A fake in the tests - no test ever reaches Steam.</summary>
public interface IPointsSteam {
	/// <summary>Points it can spend now. Null when Steam wouldn't say.</summary>
	Task<long?> BalanceAsync(CancellationToken ct);

	/// <summary>QueryRewardItems for the badges whose levels are sold, as the Web API's JSON. Null when Steam didn't answer.</summary>
	Task<string?> BadgesAsync(CancellationToken ct);

	/// <summary>GetCommunityInventory for these apps, as the Web API's JSON. Null when Steam didn't answer.</summary>
	Task<string?> LevelsAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct);

	/// <summary>Buy <paramref name="levels"/> more levels of a badge. Permanent.</summary>
	Task<EResult> BuyLevelsAsync(uint defId, int levels, CancellationToken ct);
}

/// <summary>The real thing, over the account's own connection and web session.</summary>
internal sealed class SteamPoints(Bot bot) : IPointsSteam {
	public Task<long?> BalanceAsync(CancellationToken ct) => bot.GetPointsAsync();

	public Task<string?> BadgesAsync(CancellationToken ct) => bot.Web.ApiGetAsync("ILoyaltyRewardsService", "QueryRewardItems", new Dictionary<string, string> {
		["reward_types%5B0%5D"] = PointsShop.BadgeRewardType.ToString(CultureInfo.InvariantCulture),
		["community_item_classes%5B0%5D"] = PointsShop.BadgeClass.ToString(CultureInfo.InvariantCulture),
		["count"] = "100",
		["language"] = "english"
	}, ct);

	public Task<string?> LevelsAsync(IReadOnlyCollection<uint> appIds, CancellationToken ct) {
		Dictionary<string, string> args = [];
		int i = 0;

		foreach (uint app in appIds) {
			args[$"filter_appids%5B{i++}%5D"] = app.ToString(CultureInfo.InvariantCulture);
		}

		return bot.Web.ApiGetAsync("IQuestService", "GetCommunityInventory", args, ct);
	}

	public async Task<EResult> BuyLevelsAsync(uint defId, int levels, CancellationToken ct) {
		if (bot.Unified?.CreateService<LoyaltyRewards>() is not { } shop) {
			return EResult.NoConnection;
		}

		SteamUnifiedMessages.ServiceMethodResponse<CLoyaltyRewards_RedeemPoints_Response> answer = await shop
			.RedeemPointsForBadgeLevel(new CLoyaltyRewards_RedeemPointsForBadgeLevel_Request { defid = defId, num_levels = levels })
			.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

		return answer.Result;
	}
}

/// <summary>
/// Steam Points into levels: spends an account's points on levels of the Points Shop's seasonal badge (about 100 XP for
/// every 1,000 points), within its settings.
/// </summary>
/// <remarks>
/// Points spent are gone for good, so every way in is careful. A preview ('points spend preview') reads and buys nothing. An
/// account spends nothing at all until the owner turns it on ('points on &lt;account&gt;' or its switch) - until then each look is a preview, said
/// once in the log. It keeps "Always keep at least" points, spends no more than "Spend at most, a week" in any seven days,
/// and reads the balance, the price and the level again just before buying, so nothing that changed in between is bought
/// on an old figure. A human-mode account is off by default and, when on, only acts in its own day.
/// </remarks>
public sealed class PointsSpender(Bot bot, IPointsSteam? steam = null) : BotModule(bot) {
	/// <summary>How a look was asked for.</summary>
	public enum Mode {
		/// <summary>'points spend preview': read only, whatever the settings say.</summary>
		Preview,

		/// <summary>The account's own loop: only with "Spend Steam Points on levels" on.</summary>
		Auto,

		/// <summary>'points spend &lt;account&gt;': now, by hand - still only while spending is on.</summary>
		Now
	}

	/// <summary>What one look came to.</summary>
	public sealed class Result {
		public PointsPlan? Plan { get; set; }

		/// <summary>What was really bought.</summary>
		public List<PointsStep> Bought { get; } = [];

		/// <summary>Why it couldn't look or carry on (English, for the console). Empty when all went well.</summary>
		public string Problem { get; set; } = "";

		/// <summary>Spending would have happened, but the account hasn't had spending turned on yet.</summary>
		public bool WaitingForOk { get; set; }
	}

	private readonly IPointsSteam _steam = steam ?? new SteamPoints(bot);
	private readonly SemaphoreSlim _busy = new(1, 1);
	private HumanGate? _gate;
	/// <summary>When the next look is - from "Look every (hours)" as it's set now, so changing it moves a look already planned.</summary>
	internal LookTimer Timer { get; } = new(() => Math.Clamp(bot.Cfg.PointsCheckEveryHours, 1, 168), 1.0, 1.2);
	private string? _lastPreview;
	private Said _status = new("");

	public override string Name => "steam points";
	public override string Status => Bot.Cfg.SpendPoints ? _status : "";

	public PointsLedger Ledger => PointsLedger.Of(Bot.Name);

	/// <summary>The gate a human-mode account's spending waits on: its own day, settled in (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>Why spending can't happen now on a human-mode account - empty when it can (always, on a robot).</summary>
	public Said Holding() => Gate.Waiting();

	protected override async Task RunAsync(CancellationToken ct) {
		// Not in the first minutes after signing in, with everything else.
		if (!await Sleep(Rng.Minutes(5, 15), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.Cfg.SpendPoints && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && Timer.Due(DateTime.UtcNow) && Gate.Open) {
				Timer.Looked(DateTime.UtcNow);

				bool waiting = false;

				try {
					Result result = await LookAsync(Mode.Auto, ct).ConfigureAwait(false);
					waiting = result.WaitingForOk;

					if (result.Bought.Count > 0) {
						Gate.Space(30, 120);
					}

					if (result.Problem.Length > 0) {
						// Steam not answering is a "try again in an hour", not the long wait.
						Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
					}
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at the Steam Points: {0}", Log.Describe(e)), Bot.Name);
					Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 90));
				}

				// Said on the account's card only when there's something to say: an account with no points to spend and no
				// 'points ok' (most robots) shows nothing.
				_status = PointsLedger.Approved(Bot) ? new Said("Steam Points: next look around {0}", (Func<string>) (() => Fmt.Clock(Timer.Next)))
					: waiting ? new Said("Steam Points: spending is off - turn it on in Settings to see what it would buy")
					: new Said("");
			}

			if (!await Sleep(Rng.Minutes(2, 5), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>
	/// Look at the points and the shop, and - outside a preview, with 'points ok' given and the account's day open - buy what
	/// the plan says. One look at a time per account, from the loop or a command.
	/// </summary>
	public async Task<Result> LookAsync(Mode mode, CancellationToken ct) {
		await _busy.WaitAsync(ct).ConfigureAwait(false);

		try {
			return await LookLockedAsync(mode, ct).ConfigureAwait(false);
		} finally {
			_busy.Release();
		}
	}

	private async Task<Result> LookLockedAsync(Mode mode, CancellationToken ct) {
		Result result = new();

		if (await ReadPlanAsync(ct).ConfigureAwait(false) is not { } read) {
			result.Problem = _readProblem;

			return result;
		}

		PointsPlan plan = read.Plan;
		result.Plan = plan;

		if ((mode == Mode.Preview) || (plan.Steps.Count == 0)) {
			return result;
		}

		if (!PointsLedger.Approved(Bot)) {
			result.WaitingForOk = true;

			// Said once for each new plan, not every look.
			string said = string.Join(";", plan.Steps.Select(static s => $"{s.Badge.DefId}:{s.From}+{s.Levels}"));

			if ((mode == Mode.Auto) && (said != _lastPreview)) {
				_lastPreview = said;
				Log.Info(new Said("Steam Points: would spend {0} points on {1} level(s) of {2} (+{3} XP) - nothing is spent until spending is turned on in Settings",
					PointsPlan.N(plan.Cost), plan.Steps.Sum(static s => s.Levels), plan.Steps[0].Badge.Name, plan.Xp), Bot.Name);
			}

			return result;
		}

		// A human-mode account buys in its own day only - by hand too.
		if (!Gate.Open) {
			result.Problem = $"not now - {Gate.Waiting().ToEnglish()}";

			return result;
		}

		foreach (PointsStep planned in plan.Steps) {
			// A person picking the next badge, not a list being emptied - slower still on a human-mode account.
			if (result.Bought.Count > 0) {
				await Task.Delay(Bot.Cfg.LegitMode ? Rng.Seconds(20, 90) : Rng.Seconds(3, 10), ct).ConfigureAwait(false);
			}

			// Everything read again right before buying: the balance, the price and the level. Anything different from the plan
			// - the owner bought a level by hand, the price changed, points went elsewhere - and this run stops; the next look
			// plans from what's true then.
			if (await ReadPlanAsync(ct).ConfigureAwait(false) is not { } fresh) {
				result.Problem = _readProblem;

				break;
			}

			if (fresh.Plan.Steps.FirstOrDefault(s => s.Badge.DefId == planned.Badge.DefId) is not { } step
				|| (step.From != planned.From) || (step.Badge.CostPerLevel != planned.Badge.CostPerLevel)) {
				result.Problem = "the shop, the balance or the badge changed since the plan - it looks again next time";

				break;
			}

			int levels = Math.Min(step.Levels, planned.Levels);
			long cost = step.Badge.CostPerLevel * levels;

			// The rules once more, on the fresh figures - the last word before anything is spent.
			if ((levels < 1) || (fresh.Plan.Balance - cost < fresh.Plan.Keep) || (fresh.Plan.WeekSpent + cost > fresh.Plan.WeekCap)) {
				result.Problem = "that would go under the points kept or over this week's limit - nothing bought";

				break;
			}

			// Spending turned off while it read and waited ('points off', the switch in Settings): nothing more is bought.
			if (!PointsLedger.Approved(Bot)) {
				result.Problem = "spending Steam Points was turned off - stopped before the next badge";

				break;
			}

			// The day can end, or you can start playing, while it reads and waits - asked again right before buying.
			if (!Gate.Open) {
				result.Problem = $"not now - {Gate.Waiting().ToEnglish()}";

				break;
			}

			EResult answer;

			try {
				answer = await _steam.BuyLevelsAsync(step.Badge.DefId, levels, ct).ConfigureAwait(false);
			} catch (Exception e) {
				// No answer (a timeout, the connection gone) isn't a no: Steam may have bought it. Counted as spent until a later
				// look reads the badge's level and puts it right (PointsLedger.Settle) - never bought a second time meanwhile.
				Ledger.Add(new PointsLedger.Entry(DateTime.UtcNow, cost, step.Badge.DefId, levels, step.Badge.Name, step.From, Pending: true));
				Log.Warn(new Said("Steam Points: no answer from Steam about buying {0} level(s) of {1} - counted as spent until a later look sees the badge's level",
					levels, step.Badge.Name), Bot.Name);

				if ((e is OperationCanceledException) && ct.IsCancellationRequested) {
					throw;
				}

				result.Problem = $"Steam didn't answer about buying it ({Log.Describe(e)}) - counted as spent until a later look sees the badge's level";

				break;
			}

			if (answer != EResult.OK) {
				Log.Warn(new Said("Steam Points: buying {0} level(s) of {1} was turned down - Steam said {2}", levels, step.Badge.Name, answer), Bot.Name);
				result.Problem = $"Steam turned it down ({answer})";

				break;
			}

			PointsStep bought = step with { Levels = levels };
			result.Bought.Add(bought);
			Ledger.Add(new PointsLedger.Entry(DateTime.UtcNow, cost, step.Badge.DefId, levels, step.Badge.Name));
			Log.Reward(new Said("spent {0} Steam Points on {1} level(s) of {2}: level {3} now, +{4} XP. {5} points left.",
				PointsPlan.N(cost), levels, step.Badge.Name, bought.To, bought.Xp, PointsPlan.N(fresh.Plan.Balance - cost)), Bot.Name, Topic.Points);
		}

		return result;
	}

	private string _readProblem = "";

	private sealed record Read(PointsPlan Plan);

	/// <summary>Balance, shop and levels, read now, and the plan they make. Null (with <see cref="_readProblem"/>) when Steam didn't say.</summary>
	private async Task<Read?> ReadPlanAsync(CancellationToken ct) {
		if (await _steam.BalanceAsync(ct).ConfigureAwait(false) is not { } balance) {
			_readProblem = "Steam didn't say how many points it has";

			return null;
		}

		if (PointsShop.ParseBadges(await _steam.BadgesAsync(ct).ConfigureAwait(false), DateTime.UtcNow) is not { } badges) {
			_readProblem = "couldn't read the Points Shop";

			return null;
		}

		List<uint> apps = [.. badges.Where(b => b.Current ? Bot.Cfg.PointsBuySeasonalBadge : Bot.Cfg.PointsBuyOlderBadges).Select(static b => b.AppId).Distinct()];
		Dictionary<(uint, uint), int>? owned = apps.Count == 0 ? [] : PointsShop.ParseLevels(await _steam.LevelsAsync(apps, ct).ConfigureAwait(false));

		// A purchase Steam didn't answer about, put right from the level owned now - before the week's spending is added up.
		if (owned != null) {
			int? LevelOf(uint defId) => badges.FirstOrDefault(b => b.DefId == defId) is { } b && apps.Contains(b.AppId)
				? owned.GetValueOrDefault((b.AppId, b.ItemType), 0) : null;

			foreach ((PointsLedger.Entry was, int level, bool bought) in Ledger.Settle(LevelOf)) {
				Log.Info(bought
					? new Said("Steam Points: the {0} level(s) of {1} Steam didn't answer about were bought - level {2} now", Math.Clamp(level - was.From, 0, was.Levels), was.Badge, level)
					: new Said("Steam Points: the {0} level(s) of {1} Steam didn't answer about weren't bought - taken off this week's spending", was.Levels, was.Badge), Bot.Name);
			}
		}

		return new Read(PointsPlan.Make(Bot.Cfg, balance, badges, owned, Ledger.SpentSince(DateTime.UtcNow.AddDays(-7))));
	}
}
