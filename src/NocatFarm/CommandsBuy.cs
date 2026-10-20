using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

/// <summary>
/// 'buy': buying missing cards (Modules/CardBuyer.cs) - 1.8.2. It spends money, so it previews first, and an account buys
/// nothing until buying is turned on ('buy on &lt;account&gt;', or its switch after the preview).
/// </summary>
public static partial class Commands {
	internal const string BuyUsage = "buy [account|all] | buy preview [account|all] | buy on|off|do <account>";

	/// <summary>What a 'buy' line asks for.</summary>
	public enum BuyAsk { List, Preview, On, Off, Do, Bad }

	/// <summary>
	/// Read a 'buy' line's words, in any order - from Steam chat the account's name comes straight after 'buy'. preview, on,
	/// off and do ask; anything else is the account (or 'all').
	/// </summary>
	public static (BuyAsk Ask, string? Who) ParseBuy(string[] args) {
		BuyAsk ask = BuyAsk.List;
		bool all = false;
		string? who = null;

		foreach (string word in args) {
			BuyAsk? said = word.ToLowerInvariant() switch {
				"preview" => BuyAsk.Preview,
				"on" => BuyAsk.On,
				"off" => BuyAsk.Off,
				"do" or "now" => BuyAsk.Do,
				_ => null
			};

			if (said is { } a) {
				if (ask != BuyAsk.List) {
					return (BuyAsk.Bad, null);
				}

				ask = a;
			} else if (word.Equals("all", StringComparison.OrdinalIgnoreCase) && !all && (who == null)) {
				all = true;
			} else if ((who == null) && !all) {
				who = word;
			} else {
				return (BuyAsk.Bad, null);
			}
		}

		// Turning it on, off or buying now: one account by name, never every account at once.
		if ((ask is BuyAsk.On or BuyAsk.Off or BuyAsk.Do) && (all || (who == null))) {
			return (BuyAsk.Bad, null);
		}

		return (ask, who);
	}

	/// <summary>
	/// Turning buying on, or buying now, is the owner's word - not something a Steam-chat master gives for them. Looking and
	/// turning it off are fine from anywhere.
	/// </summary>
	internal static bool BuyNeedsOwner(string[] args) =>
		args.Any(static a => a.ToLowerInvariant() is "on" or "do" or "now");

	internal const string CraftUsage = "craft now <account> | craft preview <account>";

	/// <summary>'craft now &lt;account&gt;': the badge look right away. 'craft preview &lt;account&gt;': what it would craft or sell, and why.</summary>
	private static async Task<string> CraftAsync(BotManager mgr, string[] args) {
		bool preview = args.Any(static a => a.Equals("preview", StringComparison.OrdinalIgnoreCase));
		string[] who = [.. args.Where(static a => !a.Equals("now", StringComparison.OrdinalIgnoreCase) && !a.Equals("preview", StringComparison.OrdinalIgnoreCase))];

		// One account, and at most one of 'now' or 'preview' ('craft <account>' alone is 'now').
		if ((who.Length != 1) || (args.Length > 2)) {
			return CraftUsage + " - one account.";
		}

		if (mgr.Get(who[0]) is not { } bot) {
			return NoSuchAccount(mgr, who[0]);
		}

		if (!bot.Cfg.CraftBadges) {
			return $"{bot.Name}: crafting badges is off.";
		}

		if (BotManager.ModuleOf<Modules.BadgeCraft>(bot) is not { } craft) {
			return $"{bot.Name}: not running.";
		}

		if (preview) {
			return await SlowAsync(new Slow("craft", "looking at its finished sets", Key: "craft preview"), [bot], async b => {
				(IReadOnlyList<SetSale.Pick> picks, string problem) = await craft.PreviewAsync(CancellationToken.None).ConfigureAwait(false);

				return problem.Length > 0 ? $"{b.Name}: {problem}" : CraftPreviewSaid(b.Name, picks);
			}, ReplyWithin, oneAtATime: true, cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
		}

		craft.LookNow();

		return $"{bot.Name}: looking at its badges now - the log says what it does.";
	}

	/// <summary>'craft preview', written out: each finished set, craft or sell or wait, and why. English, like every console reply.</summary>
	public static string CraftPreviewSaid(string name, IReadOnlyList<SetSale.Pick> picks) {
		if (picks.Count == 0) {
			return $"{name}: no finished sets to craft.";
		}

		// 1.8.2: a foil set is said as one, several sets of a game sold together say so, and a game whose cards are being sold
		// as worth more than their levels waits for that.
		List<string> lines = [.. picks.Select(static p => p.Border > 0 ? p with { Game = $"{p.Game} (foil)" } : p).Select(p => p.Choice switch {
			SetSale.Choice.Sell when p.Verdict is { Sets: > 1 } v => $"{name}: {p.Game}: sell all {v.Sets} sets - {p.Reason.ToEnglish()}",
			SetSale.Choice.Sell => $"{name}: {p.Game}: sell - {p.Reason.ToEnglish()}",
			SetSale.Choice.Wait when p.Quiet => $"{name}: {p.Game}: wait - {p.Reason.ToEnglish()}; not crafted while they sell",
			SetSale.Choice.Wait when p.Verdict is { Hold: true } => $"{name}: {p.Game}: wait - {p.Reason.ToEnglish()}; not crafted - it's worth more sold, so it waits for its cards to sell more",
			SetSale.Choice.Wait when p.Worth.Length == 0 => $"{name}: {p.Game}: wait - {p.Reason.ToEnglish()}, and what its cards are worth isn't known; not crafted until it's priced",
			SetSale.Choice.Wait => $"{name}: {p.Game}: wait - {p.Reason.ToEnglish()}, and a card is worth {p.Worth}; not crafted until it's priced",
			_ => $"{name}: {p.Game}: craft - {p.Reason.ToEnglish()}"
		})];
		lines.Add($"{name}: nothing was crafted or listed - 'craft now {name}' does it.");

		return string.Join(Environment.NewLine, lines);
	}

	private static async Task<string> BuyCardsAsync(BotManager mgr, string[] args) {
		(BuyAsk ask, string? who) = ParseBuy(args);

		if (ask == BuyAsk.Bad) {
			return BuyUsage;
		}

		if (ask is BuyAsk.On or BuyAsk.Off or BuyAsk.Do) {
			if (mgr.Get(who!) is not { } bot) {
				return NoSuchAccount(mgr, who!);
			}

			if (ask == BuyAsk.Off) {
				Switches.Set(bot, Switches.Buy, false);

				return $"{bot.Name}: buying cards is off - nothing is bought.";
			}

			if (ask == BuyAsk.On) {
				Switches.Set(bot, Switches.Buy, true);

				return string.Join(Environment.NewLine,
					$"{bot.Name}: buying cards is on - it buys missing cards for its badges by itself (every {Math.Clamp(bot.Cfg.BuyCheckHours, 1, 168)}h or so{(bot.Cfg.LegitMode ? ", in its day" : "")}), within its limits.",
					BuyLimits(bot),
					$"{bot.Name}: 'buy preview {bot.Name}' shows what it would buy; 'buy do {bot.Name}' buys the next ones now; 'buy off {bot.Name}' turns it off.");
			}

			// do
			if (!CardBuyer.On(bot)) {
				return $"{bot.Name}: buying cards is off. 'buy preview {bot.Name}' shows what it would buy; 'buy on {bot.Name}' turns it on.";
			}

			if (bot.Paused) {
				return $"{bot.Name}: it's paused - nothing is bought. 'resume {bot.Name}' first.";
			}

			if (HumanWaits(bot, BotManager.ModuleOf<CardBuyer>(bot)?.Gate) is { } waits) {
				return waits;
			}

			return await BuyLookAsync([bot], CardBuyer.Mode.Now).ConfigureAwait(false);
		}

		if (Pick(mgr, who == null ? [] : [who], out string? problem) is not { } bots) {
			return problem!;
		}

		if (ask == BuyAsk.Preview) {
			return await BuyLookAsync(bots, CardBuyer.Mode.Preview).ConfigureAwait(false);
		}

		return string.Join(Environment.NewLine, bots.Select(static b => $"{b.Name}: {BuyingState(b)}"));
	}

	/// <summary>Whether and how an account buys cards, in a few words.</summary>
	public static string BuyingState(Bot b) {
		if (!CardBuyer.On(b)) {
			return "buying cards is off";
		}

		BuyLedger ledger = BuyLedger.Of(b.Name);

		if (ledger.Unreadable) {
			return ledger.UnreadableText;
		}

		int cur = b.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) b.WalletCurrency;
		DateTime now = DateTimeOffset.FromUnixTimeSeconds(Confirmations.SteamTimeNow).UtcDateTime;   // the limits go by Steam's clock

		string level = CardBuyer.LevelPriceOf(b.Name);
		string goal = (ledger.Goal is { } g) && (BotManager.ModuleOf<CardBuyer>(b) is { } buyer) ? " · " + buyer.GoalSaid(g).ToEnglish() : "";

		return $"buys cards: {BuyPlan.Money(ledger.SpentSince(now.AddDays(-1)), cur)} of {BuyPlan.LimitSaid(BuyPlan.Limit(b.Cfg.BuyDailyLimit), cur)} today, "
			+ $"{BuyPlan.Money(ledger.SpentSince(now.AddDays(-7)), cur)} of {BuyPlan.LimitSaid(BuyPlan.Limit(b.Cfg.BuyWeeklyLimit), cur)} this week"
			+ (level.Length > 0 ? $" · {level} at the last look" : "") + goal;
	}

	/// <summary>The limits it keeps to, in one line.</summary>
	internal static string BuyLimits(Bot bot) {
		int cur = bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency;

		string perLevel = bot.Cfg.BuyMaxLevelPrice > 0 ? $"at most {BuyPlan.Money(BuyPlan.Cents(bot.Cfg.BuyMaxLevelPrice), cur)} a level" : "a level at most a little over what one usually costs";
		string stop = bot.Cfg.BuyStopAtLevel > 0 ? $", until Steam level {bot.Cfg.BuyStopAtLevel}" : "";

		return $"{bot.Name}: it spends at most {BuyPlan.LimitSaid(BuyPlan.Limit(bot.Cfg.BuyDailyLimit), cur)} a day and {BuyPlan.LimitSaid(BuyPlan.Limit(bot.Cfg.BuyWeeklyLimit), cur)} a week, "
			+ $"{perLevel}{stop}, never more than the wallet holds, and only whole sets{(bot.Cfg.BuyFullSets ? " - the cheapest full sets too" : "")}{(bot.Cfg.LegitMode ? ", only during its day" : "")}.";
	}

	/// <summary>'buy preview' and 'buy do': each account's look, said plainly.</summary>
	private static Task<string> BuyLookAsync(IReadOnlyList<Bot> bots, CardBuyer.Mode mode) {
		async Task<string> Look(Bot b) {
			CardBuyer buyer = BotManager.ModuleOf<CardBuyer>(b)!;
			// Buying now runs on the module's own run (stopped with the account), never beside a look already going.
			CardBuyer.Result result = mode == CardBuyer.Mode.Preview
				? await buyer.LookAsync(mode, CancellationToken.None).ConfigureAwait(false)
				: await ScamWatch.AsYouAskedAsync(buyer.BuyNowAsync).ConfigureAwait(false);

			List<string> lines = BuySaid(b, mode, result);

			// 1.8.2: the gems maths beside what it would buy - only while it's in play (its setting, automatic packs, buying on).
			if ((mode == CardBuyer.Mode.Preview) && b.Cfg.GemsForLevels && (b.Cfg.BoosterMake == BoosterPick.Auto) && CardBuyer.On(b)) {
				lines.Insert(lines.Count - 1, $"{b.Name}: {await Boosters.GemsPreviewAsync(b, null, null, null, CancellationToken.None).ConfigureAwait(false)}");
			}

			return string.Join(Environment.NewLine, lines);
		}

		Slow job = mode == CardBuyer.Mode.Preview ? new("buy", "looking at which cards to buy", Key: "buy preview") : new("buy", "buying cards", Key: "buy do");

		return SlowAsync(job, bots, Look, ReplyWithin, oneAtATime: true,
			cannot: static b => !b.IsOnline || !b.Web.Ready || (BotManager.ModuleOf<CardBuyer>(b) is null) ? $"{b.Name}: not logged in" : null);
	}

	/// <summary>One account's look in plain words: what it would buy or bought, for how much, the limits, and why not when nothing.</summary>
	public static List<string> BuySaid(Bot b, CardBuyer.Mode mode, CardBuyer.Result result) {
		string name = b.Name;
		List<string> lines = [];

		if (result.Plan is not { } plan || (plan.Problem.Length > 0)) {
			lines.Add(result.Lasting ? $"{name}: {result.Problem}" : $"{name}: {(result.Problem.Length > 0 ? result.Problem : "couldn't look")} - try again in a minute");
			lines.Add(BuyLimits(b));

			return lines;
		}

		int cur = plan.Currency;
		string M(long cents) => BuyPlan.Money(cents, cur);

		// 1.8.2: what stops all buying comes first (billing details, email confirmations) - nothing is listed it couldn't buy.
		if (plan.Blocked.Length > 0) {
			lines.Add($"{name}: {plan.Blocked}");
		}

		lines.Add($"{name}: wallet {M(plan.Wallet)} · {M(plan.DaySpent)} of {BuyPlan.LimitSaid(plan.DayLimit, cur)} spent today · {M(plan.WeekSpent)} of {BuyPlan.LimitSaid(plan.WeekLimit, cur)} this week - {M(plan.Room)} can be spent now");

		// 1.8.2: one number for what a level costs now - the cheapest priced.
		if (plan.LevelPriceSaid.Length > 0) {
			lines.Add($"{name}: level price: {plan.LevelPriceSaid}");
		}

		if (result.Confirmed > 0) {
			lines.Add($"{name}: {result.Confirmed} purchase(s) confirmed in the Steam app - bought");
		}

		if ((result.Bought.Count > 0) || (result.Waiting.Count > 0)) {
			lines.AddRange(result.Bought.GroupBy(static x => x.Step.Game).Select(g => $"{name}: bought {g.Key} · {g.Count()} card(s) · {M(g.Sum(static x => x.Paid))}"));   // each card was its own line as it was bought
			lines.AddRange(result.Finished.Select(s => $"{name}: {s.Game} set complete - badge crafting makes it level {s.Level + 1}"));
			lines.AddRange(result.Waiting.Select(x => $"{name}: {x.Card.Name} ({x.Step.Game}) - confirm the purchase in the Steam app on your phone; it's bought at the next look"));
		} else {
			string verb = mode == CardBuyer.Mode.Preview ? "would buy" : "would have bought";

			foreach (BuyStep s in plan.Steps) {
				string what = s.FullSet ? $"a full set of {s.Game} ({s.Cards.Count} cards)" : $"{s.Cards.Count} of {s.SetSize} cards for {s.Game}";
				lines.Add($"{name}: {verb} {what} for {M(s.Cost)} - level {s.Level} to {s.Level + 1}, about {s.XpPerUnit:N0} XP per 1.00");
				lines.Add($"{name}:   {string.Join(", ", s.Cards.Select(c => $"{c.Name} {M(c.Price)}"))}");
			}
		}

		// Smart levelling: whole sets worth more sold than crafted (badge crafting lists them; nothing is listed here).
		foreach (SetSale.Verdict v in plan.Sales) {
			lines.Add(v.Sell
				? $"{name}: would sell the {v.Game} set instead of crafting it - {v.Money} after Steam's fee buys about {v.About} badge levels, more than the 1 crafting makes"
				: $"{name}: {v.Game} set: crafted, not sold - {v.Why}");
		}

		lines.AddRange(plan.Notes.Select(n => $"{name}: {n}"));

		if (!b.Cfg.CraftBadges) {
			lines.Add($"{name}: \"Craft badges from card sets\" is off - nothing is bought until it's on, or the cards would only sit there.");
		}

		if (result.Problem.Length > 0) {
			lines.Add($"{name}: {result.Problem}");
		}

		if ((plan.Steps.Count > 0) && !CardBuyer.On(b)) {
			lines.Add($"{name}: preview only - nothing is bought while buying cards is off. 'buy on {name}' (or the switch in its settings) turns it on.");
		}

		lines.Add(BuyLimits(b));

		return lines;
	}
}
