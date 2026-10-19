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

	/// <summary>Buying cards (1.8.2): what it would buy next, for how much, and the limits.</summary>
	public sealed record BuyNumbers(
		bool SignedIn,
		string Problem,
		int Blocked,
		string Currency,
		string Wallet,
		string DayLimit,
		string DaySpent,
		string WeekLimit,
		string WeekSpent,
		string Cost,
		int Cards,
		IReadOnlyList<BuySet> Sets,
		IReadOnlyList<string> Notes,
		bool FullSets,
		bool CraftOff,
		bool OwnDayOnly,
		IReadOnlyList<SellSet>? Sells = null
	) {
		public string Kind => "buy";
	}

	/// <summary>One set it would buy: the game, the cards, its cost and XP for each whole unit of money.</summary>
	public sealed record BuySet(string Game, bool FullSet, int Level, int Cards, int SetSize, string Cost, long XpPerUnit, IReadOnlyList<string> Names);

	/// <summary>1.8.2 smart levelling: a whole set it would sell instead of crafting - what it brings in after Steam's fee, and about how many levels that buys.</summary>
	public sealed record SellSet(string Game, string Money, int Levels);

	/// <summary>The numbers of one buying-cards look.</summary>
	public static BuyNumbers Buy(BuyPlan? plan, BotConfig cfg, bool signedIn, string problem) {
		int cur = plan?.Currency ?? 0;
		string M(long cents) => BuyPlan.Money(cents, cur);
		List<BuySet> sets = plan == null ? [] : [.. plan.Steps.Select(s => new BuySet(s.Game, s.FullSet, s.Level, s.Cards.Count, s.SetSize, M(s.Cost), s.XpPerUnit, [.. s.Cards.Select(static c => c.Name)]))];

		return new BuyNumbers(signedIn, problem, (int) (plan?.Block ?? BuyBlock.None), cur > 0 ? MarketQuotes.Code(cur) : "", plan == null ? "" : M(plan.Wallet),
			M(BuyPlan.Cents(cfg.BuyDailyLimit)), M(plan?.DaySpent ?? 0), M(BuyPlan.Cents(cfg.BuyWeeklyLimit)), M(plan?.WeekSpent ?? 0),
			M(plan?.Cost ?? 0), plan?.CardCount ?? 0, sets, plan?.Notes.ToList() ?? [], cfg.BuyFullSets, !cfg.CraftBadges, cfg.LegitMode,
			plan == null ? [] : [.. plan.Sales.Where(static v => v.Sell).Select(static v => new SellSet(v.Game, v.Money, v.About))]);
	}

	/// <summary>1.8.2 card swaps: spare cards it could swap now, and the limits it keeps to.</summary>
	public sealed record SwapNumbers(bool SignedIn, string Problem, int Games, int Cards, int Unpriced, string Currency, string MaxLoss, int PerDay,
		bool SkipHeld, bool Confirms, bool Human) {
		public string Kind => "swaps";
	}

	public static SwapNumbers Swaps(CardExchange.Spares? spares, Bot bot, bool signedIn, string problem) {
		int currency = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

		return new SwapNumbers(signedIn, problem, spares?.Games ?? 0, spares?.Cards ?? 0, spares?.Unpriced ?? 0, currency > 0 ? MarketQuotes.Code(currency) : "",
			Core.Spare.Money(CardExchange.MaxLossCents(bot.Cfg), currency), CardExchange.PerDay(bot.Cfg), bot.Cfg.CardSwapSkipHeld, bot.CanConfirmTrades, bot.Cfg.LegitMode);
	}

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

	/// <summary>1.8.2 Open in Steam on every account: what each account's button would do, for its Turn on? pop-up.</summary>
	public sealed record SteamOpenAllNumbers(bool Supported, bool SteamFound, bool GameRunning, bool AsksWhichAccount, IReadOnlyList<SteamOpen.Numbers> Accounts) {
		public string Kind => "steamopenall";
	}

	/// <summary>One account's Open in Steam look in a line, for the console.</summary>
	public static string SteamOpenLine(SteamOpen.Numbers n, string account) {
		string line = !n.Supported ? $"{account}: Open in Steam only works on Windows for now."
			: !n.SteamFound ? $"{account}: Steam doesn't look installed on this PC."
			: n.GameRunning ? $"{account}: a game is open in Steam right now - the button won't switch while one is running."
			: n.Remembered ? $"{account}: your Steam app remembers this account, so the button signs it in with no password."
			: n.SignIn ? $"{account}: your Steam app doesn't remember this account yet - the button opens Steam's sign-in with its name filled in. Type its password once (and its Steam Guard code if asked) with \"Remember me\" ticked; after that it's one click."
			: $"{account}: no Steam login name is set for this account. Sign into it once in Steam with \"Remember me\" ticked, then the button works.";

		return n.AsksWhichAccount
			? line + $"{Environment.NewLine}Steam is set to ask which account to use each time it starts - the button turns that off, or Steam would ignore the account it's pointed at."
			: line;
	}

	/// <summary>1.8.2: the look for "Open in Steam buttons on every account" - every account's, read-only from the local Steam files.</summary>
	public static (string Text, SteamOpenAllNumbers Data) LookEveryone(IEnumerable<Bot> bots) {
		List<SteamOpen.Numbers> each = [.. bots.Select(SteamOpen.Look)];
		SteamOpen.Numbers first = each.FirstOrDefault() ?? SteamOpen.Look("", 0, "");
		var data = new SteamOpenAllNumbers(first.Supported, first.SteamFound, first.GameRunning, first.AsksWhichAccount, each);

		if (!first.Supported) {
			return ("Open in Steam only works on Windows for now.", data);
		}

		List<string> lines = ["Open in Steam on every account: each account shows the one-click button that signs your Steam app into it."];
		lines.AddRange(each.Select(n => "  " + SteamOpenLine(n with { AsksWhichAccount = false }, n.Account)));

		if (first.AsksWhichAccount) {
			lines.Add("Steam is set to ask which account to use each time it starts - the button turns that off, or Steam would ignore the account it's pointed at.");
		}

		lines.Add("Pressing a button closes Steam, points it at that account, and starts Steam again. It never stops nocat.farm farming.");

		return (string.Join(Environment.NewLine, lines), data);
	}

	/// <summary>
	/// One look for the pop-up: the console's text (exactly what 'set &lt;account&gt; SpareItems on' prints) and the numbers.
	/// </summary>
	public static async Task<(string Text, object Data)> LookAsync(Bot bot, string feature, CancellationToken ct) {
		bool signedIn = bot.IsOnline && bot.Web.Ready;

		// 1.8.2: Open in Steam - a read-only look at the local Steam client (no Steam network call), then the plain text
		// for the console and the same facts as numbers for the pop-up.
		if (feature == Switches.SteamOpenButton) {
			SteamOpen.Numbers n = SteamOpen.Look(bot);

			return (SteamOpenLine(n, bot.Name), n);
		}

		if (feature == Switches.Swaps) {
			(string text, CardExchange.Spares? spares, string problem) = await CardExchange.PreviewAsync(bot, ct).ConfigureAwait(false);

			return (text, Swaps(spares, bot, signedIn, problem));
		}

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

		if (feature == Switches.Buy) {
			if (!signedIn || (BotManager.ModuleOf<CardBuyer>(bot) is not { } buyer)) {
				return ($"{bot.Name}: not signed in, so it can't look at the market yet.{Environment.NewLine}{Commands.BuyLimits(bot)}", Buy(null, bot.Cfg, false, ""));
			}

			CardBuyer.Result look = await buyer.LookAsync(CardBuyer.Mode.Preview, ct).ConfigureAwait(false);

			return (string.Join(Environment.NewLine, Commands.BuySaid(bot, CardBuyer.Mode.Preview, look)), Buy(look.Plan, bot.Cfg, true, look.Problem));
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
