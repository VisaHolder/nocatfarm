using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

/// <summary>'booster plan': what automatic "Make booster packs" would pick next, and why - 1.8.1.</summary>
public static partial class Commands {
	/// <summary>The words after 'plan' in 'booster plan ...' or 'booster &lt;account&gt; plan' - null when it isn't that.</summary>
	private static string[]? PlanWords(BotManager mgr, string[] args) {
		if ((args.Length > 0) && args[0].Equals("plan", StringComparison.OrdinalIgnoreCase) && (mgr.Get(args[0]) == null)) {
			return args[1..];
		}

		if ((args.Length > 1) && (mgr.Get(args[0]) != null) && args[1].Equals("plan", StringComparison.OrdinalIgnoreCase)) {
			return [args[0]];
		}

		return null;
	}

	private static async Task<string> BoosterPlanAsync(BotManager mgr, string[] who) {
		if (Pick(mgr, who, out string? problem) is not { } bots) {
			return problem!;
		}

		return await SlowAsync(new Slow("booster", "working out which booster pack is next", Key: "booster plan"), bots, static async b => {
			if (await Boosters.ReadAsync(b).ConfigureAwait(false) is not { } page) {
				return $"{b.Name}: couldn't read the booster creator";
			}

			SpareBadges? badges = SpareBadges.Parse(await b.Web.ApiGetAsync("IPlayerService", "GetBadges", new Dictionary<string, string> {
				["steamid"] = b.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture)
			}).ConfigureAwait(false));

			if (badges == null) {
				return $"{b.Name}: Steam wouldn't say what its badges are - try again later";
			}

			if (await Inventory.ReadAsync(b, 753, "6", CancellationToken.None).ConfigureAwait(false) is not { Complete: true } inventory) {
				return $"{b.Name}: couldn't read all of its inventory - try again later";
			}

			Dictionary<uint, DateTime> made = BotManager.ModuleOf<Boosters>(b)?.AutoMemory.Made ?? BoosterPick.Memory.Load(b.Name).Made;
			BoosterPick.Ranking ranking = BoosterPick.Rank(page, badges.Levels, LevelPlanner.NormalCards(inventory), b.Cfg.BoosterNeverOpen, made, DateTime.UtcNow);
			bool dayOpen = HumanWaits(b, BotManager.ModuleOf<Boosters>(b)?.DayGate) == null;

			return BoosterPick.Explain(b.Name, b.Cfg.BoosterMake, page, ranking, b.Cfg.BoosterKeepGems, dayOpen, b.Cfg.CraftBadges, b.Cfg.OpenBoosterPacks);
		}, ReplyWithin, oneAtATime: true, cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
	}
}
