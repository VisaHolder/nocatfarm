using System.Globalization;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Turns completed card sets into badges, which is what actually raises your Steam level - farming cards and
/// then leaving the sets sitting in the inventory earns the level nothing.
///
/// Two things about this are load-bearing:
///
/// 1. The craftable state is ONLY visible to the logged-in owner. The public badges page contains no craft
///    markup at all, so this has to go through the account's own session.
/// 2. Steam rate-limits crafting hard, and the limit EXTENDS if you keep knocking. So: read the page once,
///    craft everything it listed, and never re-read after each craft. Steam's own UI reloads every time -
///    do not copy that.
/// </summary>
public sealed class BadgeCraft(Bot bot) : BotModule(bot) {
	private const int SweepLowHours = 22;
	private const int SweepHighHours = 26;
	private const int CraftGapLowSeconds = 6;
	private const int CraftGapHighSeconds = 14;
	private const int UnpackGapLowSeconds = 3;
	private const int UnpackGapHighSeconds = 12;
	private const int BackoffHours = 6;
	private const int MaxBadgePages = 20;

	private Said _status = new("off");
	private int _crafted;

	public override string Name => "badges";
	public override string Status => _status;

	public int CraftedThisRun => _crafted;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			if (!Bot.Cfg.CraftBadges) {
				_status = new Said("off");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (Bot.Paused) {
				_status = new Said("paused");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (!Bot.IsOnline || !Bot.Web.Ready) {
				_status = new Said("waiting for the account");

				if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			TimeSpan wait = Rng.Minutes(SweepLowHours * 60, SweepHighHours * 60);

			try {
				int made = await SweepAsync(ct).ConfigureAwait(false);

				if (made < 0) {
					wait = TimeSpan.FromHours(BackoffHours);   // Steam refused - back off rather than knock again
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Warn(new Said("badge sweep failed: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
			}

			if (!await Sleep(wait, ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>
	/// Open any booster packs sitting in the inventory. A sealed pack is three cards missing from the set count,
	/// so leaving them shut quietly weakens the crafting this module exists to do.
	/// </summary>
	private async Task<int> UnpackBoostersAsync(CancellationToken ct) {
		string? inv = await Bot.Web.GetAsync(
			new Uri(WebSession.Community, $"/inventory/{Bot.SteamId}/753/6?l=english&count=200"), ct).ConfigureAwait(false);

		if (inv == null) {
			return 0;
		}

		int opened = 0;

		// Descriptions carry the type; assets carry the id. Match them up by classid.
		foreach (string classId in BoosterClassIds(inv)) {
			// market_fee_app depends only on classId, so read it once per class, not once per asset. Match the
			// FULL "classid":"<id>" including the closing quote - without it, classid 123 also matches 1234, so we
			// would read the appid from the wrong description, POST the wrong appid, and Steam refuses the unpack.
			int at = inv.IndexOf("\"classid\":\"" + classId + "\"", StringComparison.Ordinal);
			string? appId = at < 0 ? null : Json.Str(inv[at..], "market_fee_app");

			if (appId == null) {
				continue;
			}

			foreach (ulong assetId in AssetIdsFor(inv, classId)) {
				ct.ThrowIfCancellationRequested();

				string? body = await Bot.Web.PostAsync(
					new Uri(WebSession.Community, "/my/ajaxunpackbooster/"),
					new Dictionary<string, string>(StringComparer.Ordinal) {
						["appid"] = appId,
						["communityitemid"] = assetId.ToString()
					},
					new Uri(WebSession.Community, "/my/inventory/"), ct).ConfigureAwait(false);

				if (body != null && !body.Contains("\"success\":false", StringComparison.Ordinal)) {
					opened++;
				}

				if (!await Sleep(Rng.Seconds(UnpackGapLowSeconds, UnpackGapHighSeconds), ct).ConfigureAwait(false)) {
					return opened;
				}
			}
		}

		if (opened > 0) {
			Log.Reward(new Said("opened {0} booster pack(s)", opened), Bot.Name);
		}

		return opened;
	}

	private static IEnumerable<string> BoosterClassIds(string inventoryJson) {
		HashSet<string> ids = [];
		int i = 0;

		while (true) {
			int at = inventoryJson.IndexOf("Booster Pack", i, StringComparison.Ordinal);

			if (at < 0) {
				return ids;
			}

			i = at + 12;

			// Walk back to this description's classid.
			int classAt = inventoryJson.LastIndexOf("\"classid\":\"", at, StringComparison.Ordinal);

			if (classAt < 0) {
				continue;
			}

			int start = classAt + "\"classid\":\"".Length;
			int end = inventoryJson.IndexOf('"', start);

			if (end > start) {
				ids.Add(inventoryJson[start..end]);
			}
		}
	}

	private static IEnumerable<ulong> AssetIdsFor(string inventoryJson, string classId) {
		List<ulong> assets = [];
		int i = 0;

		while (true) {
			int at = inventoryJson.IndexOf("\"classid\":\"" + classId + "\"", i, StringComparison.Ordinal);

			if (at < 0) {
				return assets;
			}

			i = at + classId.Length;
			int idAt = inventoryJson.LastIndexOf("\"assetid\":\"", at, StringComparison.Ordinal);

			if (idAt < 0) {
				continue;
			}

			int start = idAt + "\"assetid\":\"".Length;
			int end = inventoryJson.IndexOf('"', start);

			if ((end > start) && ulong.TryParse(inventoryJson[start..end], out ulong asset) && !assets.Contains(asset)) {
				assets.Add(asset);
			}
		}
	}

	/// <summary>
	/// One sweep. Returns how many badges were crafted, or -1 if Steam refused and we should back off.
	/// </summary>
	public async Task<int> SweepAsync(CancellationToken ct) {
		if (Bot.Cfg.UnpackBoosterPacks) {
			_status = new Said("opening booster packs");

			try {
				await UnpackBoostersAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("booster unpack: {0}", e.Message), Bot.Name);
			}
		}

		_status = new Said("checking for completed sets");

		string? html = await Bot.Web.GetAsync(new Uri(WebSession.Community, "/my/badges/?l=english&p=1"), ct).ConfigureAwait(false);

		if (html == null) {
			_status = new Said("couldn't read the badges page");

			return 0;
		}

		List<Craftable> ready = Parse(html);

		// Steam paginates at ~60 badges. Reading only page 1 meant a big library never crafted anything past it.
		int pages = Math.Min(MaxBadgePages, CardFarmer.ParseMaxPages(html));

		for (int page = 2; page <= pages; page++) {
			string? more = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/my/badges/?l=english&p={page}"), ct).ConfigureAwait(false);

			if (more == null) {
				break;
			}

			foreach (Craftable c in Parse(more)) {
				if (!ready.Exists(existing => existing.AppId == c.AppId)) {
					ready.Add(c);
				}
			}
		}

		if (ready.Count == 0) {
			_status = new Said("nothing ready to craft");

			return 0;
		}

		Log.Info(new Said("{0} completed card set(s) ready to craft", ready.Count), Bot.Name);
		int made = 0;

		// Deliberately NOT re-reading the page between crafts: the list we already have is accurate, and every
		// extra request during a craft run is what pushes Steam into extending the rate limit.
		foreach (Craftable c in ready) {
			ct.ThrowIfCancellationRequested();
			_status = new Said("crafting ({0}/{1})", made, ready.Count);

			if (!await CraftAsync(c, ct).ConfigureAwait(false)) {
				Log.Warn(new Said("craft refused - backing off {0}h rather than retrying", BackoffHours), Bot.Name);
				_status = new Said("rate-limited, backing off");

				return made > 0 ? made : -1;
			}

			made++;
			_crafted++;
			Stats.Record(Stats.KindBadge, Bot.Name);

			if (!await Sleep(Rng.Seconds(CraftGapLowSeconds, CraftGapHighSeconds), ct).ConfigureAwait(false)) {
				break;
			}
		}

		if (made > 0) {
			Log.Reward(new Said("crafted {0} badge(s) - Steam level up", made), Bot.Name);
		}

		_status = new Said("{0} crafted since start", _crafted);

		return made;
	}

	private async Task<bool> CraftAsync(Craftable c, CancellationToken ct) {
		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["appid"] = c.AppId.ToString(CultureInfo.InvariantCulture),
			["series"] = c.Series.ToString(CultureInfo.InvariantCulture),
			["border_color"] = c.Border.ToString(CultureInfo.InvariantCulture),
			["levels"] = Math.Max(1, c.Levels).ToString(CultureInfo.InvariantCulture)
			// sessionid is injected by WebSession
		};

		string? body = await Bot.Web.PostAsync(
			new Uri(WebSession.Community, "/my/ajaxcraftbadge/"),
			form,
			new Uri(WebSession.Community, "/my/badges/"),
			ct).ConfigureAwait(false);

		if (body == null) {
			return false;
		}

		return !body.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
			&& !body.Contains("\"success\":false", StringComparison.Ordinal);
	}

	internal readonly record struct Craftable(uint AppId, int Series, int Border, int Levels);

	/// <summary>
	/// Every craftable set on the page. Steam renders a craft button whose onclick is
	/// <c>CraftBadge( appid, series, border, levels )</c> - the numbers live in the MARKUP, not the text, so
	/// this reads attributes rather than rendered content.
	/// </summary>
	internal static List<Craftable> Parse(string html) {
		List<Craftable> found = [];
		int i = 0;

		while (true) {
			int at = html.IndexOf("CraftBadge", i, StringComparison.OrdinalIgnoreCase);

			if (at < 0) {
				return found;
			}

			i = at + "CraftBadge".Length;
			int open = html.IndexOf('(', at);
			int close = open < 0 ? -1 : html.IndexOf(')', open);

			if ((open < 0) || (close < 0) || (close - open > 200)) {
				continue;
			}

			List<int> nums = Numbers(html[(open + 1)..close]);

			if ((nums.Count == 0) || (nums[0] <= 0)) {
				continue;
			}

			uint appId = (uint) nums[0];

			if (found.Exists(f => f.AppId == appId)) {
				continue;
			}

			found.Add(new Craftable(
				appId,
				nums.Count >= 2 ? nums[1] : 1,
				nums.Count >= 3 ? nums[2] : 0,
				nums.Count >= 4 ? nums[3] : 1));
		}
	}

	private static List<int> Numbers(string s) {
		List<int> nums = [];
		int i = 0;

		while (i < s.Length) {
			if (!char.IsAsciiDigit(s[i])) {
				i++;

				continue;
			}

			int start = i;

			while ((i < s.Length) && char.IsAsciiDigit(s[i])) {
				i++;
			}

			if (int.TryParse(s.AsSpan(start, i - start), out int v)) {
				nums.Add(v);
			}
		}

		return nums;
	}
}
