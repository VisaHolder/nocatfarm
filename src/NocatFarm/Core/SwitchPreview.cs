using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// What turning Spare items or Steam Points on would do, as numbers the dashboard's "Turn on?" pop-up draws in its own
/// words - "12 to gems (0.70 CAD) · 8 to sell (1.90 CAD) · 629 kept", the limits as a list, "still pricing 146 items" -
/// beside the console's English text of the same look. One look makes both, so the pop-up never reads Steam twice.
/// </summary>
/// <remarks>
/// Only numbers, money already written out ("0.70 CAD") and setting values go in here: the page puts them into
/// translated sentences. Notes the Points planner writes (English) go as they are.
/// </remarks>
public static class SwitchPreview {
	/// <summary>Spare items: what a look found and the limits it keeps to.</summary>
	public sealed record SpareNumbers(
		bool SignedIn,
		string Problem,
		int GemItems,
		long Gems,
		string GemsWorth,
		int SellItems,
		string SellGet,
		int Pricing,
		string Currency,
		int PerRun,
		int DailyItems,
		string DailyValue,
		string MaxPrice,
		int NewDays,
		int Action,
		int Packs,
		string PacksTo,
		int Sacks,
		string SacksTo
	) {
		public string Kind => "spare";
	}

	/// <summary>Steam Points: the balance, what it would buy and the limits.</summary>
	public sealed record PointsNumbers(
		bool SignedIn,
		string Problem,
		long Balance,
		long Keep,
		long WeekSpent,
		long WeekCap,
		long CanSpend,
		long Cost,
		int Levels,
		int Xp,
		IReadOnlyList<PointsBuy> Buys,
		IReadOnlyList<string> Notes,
		bool OwnDayOnly,
		bool Seasonal,
		bool Older,
		int MaxLevel
	) {
		public string Kind => "points";
	}

	public sealed record PointsBuy(string Badge, int From, int To, int Levels, long Cost, int Xp);

	/// <summary>The numbers of one spare-items look: counted from its plan, the limits from the account's settings.</summary>
	public static SpareNumbers Spare(SparePlan? plan, BotConfig cfg, int currency, MarketPrice? sack, bool signedIn, string problem) {
		SpareRules r = SpareRules.From(cfg);
		List<SpareDecision> all = plan?.Decisions ?? [];
		List<SpareDecision> gems = [.. all.Where(static d => d.Verdict == SpareVerdict.Gems)];
		List<SpareDecision> sell = [.. all.Where(static d => d.Verdict == SpareVerdict.Sell)];
		int pricing = plan == null ? 0 : SpareRun.StillToPrice(plan, currency, r);
		long gemCount = gems.Sum(static d => (long) d.GemValue);
		string gemsWorth = (sack is { Cents: > 0 } s) && (gemCount > 0) ? Core.Spare.Money(Core.Spare.GemsWorth((int) Math.Min(int.MaxValue, gemCount), s.Cents), currency) : "";
		string sellGet = sell.Count > 0 ? Core.Spare.Money(sell.Sum(static d => (long) d.ReceiveCents), currency) : "";
		int sacks = SpareRun.GemsChoice(cfg);

		// Only what it would do: never how many it keeps, or how many are over the price limit.
		return new SpareNumbers(signedIn, problem, gems.Count, gemCount, gemsWorth, sell.Count, sellGet, pricing,
			currency > 0 ? MarketQuotes.Code(currency) : "",
			r.PerRun, r.DailyItems, Core.Spare.Money(r.DailyValueCents, currency), Core.Spare.Money(r.MaxPriceCents, currency), r.NewDays, r.Action,
			Math.Clamp(cfg.BoosterUnopened, 0, 2), cfg.BoosterSendTo?.Trim() ?? "",
			sacks is 3 or 4 ? sacks : 0, cfg.SpareGemsSendTo?.Trim() ?? "");
	}

	/// <summary>The numbers of one Steam Points look.</summary>
	public static PointsNumbers Points(PointsPlan? plan, BotConfig cfg, bool signedIn, string problem) {
		List<PointsBuy> buys = plan == null ? [] : [.. plan.Steps.Select(static s => new PointsBuy(s.Badge.Name, s.From, s.To, s.Levels, s.Cost, s.Xp))];

		return new PointsNumbers(signedIn, problem,
			plan?.Balance ?? 0, plan?.Keep ?? Math.Max(0, cfg.PointsKeepAtLeast), plan?.WeekSpent ?? 0, plan?.WeekCap ?? Math.Max(0, cfg.PointsWeeklyCap), plan?.Spare ?? 0,
			plan?.Cost ?? 0, buys.Sum(static b => b.Levels), plan?.Xp ?? 0, buys, plan?.Notes.ToList() ?? [], cfg.LegitMode,
			cfg.PointsBuySeasonalBadge, cfg.PointsBuyOlderBadges, Math.Clamp(cfg.PointsBadgeMaxLevel, 1, 40));
	}

	/// <summary>
	/// One look for the pop-up: the console's text (exactly what 'set &lt;account&gt; SpareItems on' prints) and the numbers.
	/// </summary>
	public static async Task<(string Text, object Data)> LookAsync(Bot bot, string feature, CancellationToken ct) {
		bool signedIn = bot.IsOnline && bot.Web.Ready;

		if (feature == Switches.Spare) {
			string limits = SpareRun.Limits(bot);
			int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

			if (!signedIn) {
				return ($"{bot.Name}: not signed in, so it can't look at the inventory yet.{Environment.NewLine}{limits}",
					Spare(null, bot.Cfg, currency, null, false, ""));
			}

			(SpareRun.Look? look, string? problem) = await SpareRun.ReadAsync(bot, ct, priceThem: false).ConfigureAwait(false);

			if (look == null) {
				return ($"{bot.Name}: {problem}{Environment.NewLine}{limits}", Spare(null, bot.Cfg, currency, null, true, problem ?? ""));
			}

			return (SpareRun.Preview(bot.Name, look) + Environment.NewLine + limits,
				Spare(look.Plan, bot.Cfg, look.Currency, SparePrices.Sack(look.Currency), true, ""));
		}

		string keeps = $"It keeps at least {PointsPlan.N(bot.Cfg.PointsKeepAtLeast)} points and spends at most {PointsPlan.N(bot.Cfg.PointsWeeklyCap)} a week{(bot.Cfg.LegitMode ? ", only during its day" : "")}. Points spent are gone for good.";

		if (!signedIn || (BotManager.ModuleOf<PointsSpender>(bot) is not { } spender)) {
			return ($"{bot.Name}: not signed in, so it can't look at its points yet.{Environment.NewLine}{keeps}", Points(null, bot.Cfg, false, ""));
		}

		PointsSpender.Result result = await spender.LookAsync(PointsSpender.Mode.Preview, ct).ConfigureAwait(false);

		return (string.Join(Environment.NewLine, Commands.PointsSaid(bot, PointsSpender.Mode.Preview, result).Append(keeps)),
			Points(result.Plan, bot.Cfg, true, result.Plan == null ? (result.Problem.Length > 0 ? result.Problem : "couldn't look") : result.Problem));
	}
}
