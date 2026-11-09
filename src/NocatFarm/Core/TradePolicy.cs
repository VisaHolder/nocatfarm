using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>What a rule, or the whole rule set, says about an offer.</summary>
public enum TradeVerdict { Abstain, Accept, Decline, Unknown, Blocked }

/// <summary>A rule's own answer, with the reason in plain words.</summary>
public sealed record RuleHit(string RuleId, TradeVerdict Verdict, string Why);

/// <summary>The whole answer: verdict, the rule that decided it (if any), why, and every rule that was tried.</summary>
public sealed record TradeDecision(TradeVerdict Verdict, string? RuleId, string Why, IReadOnlyList<RuleHit> Hits) {
	public bool Leaves => Verdict is TradeVerdict.Unknown or TradeVerdict.Blocked;
}

/// <summary>What the hard gates say before any rule runs.</summary>
public enum GateBlock { None, Blocked, Unknown }

public sealed record GateResult(GateBlock Block, string Why) {
	public static readonly GateResult Allowed = new(GateBlock.None, "");
	public static GateResult Blocked(string why) => new(GateBlock.Blocked, why);
	public static GateResult Unknown(string why) => new(GateBlock.Unknown, why);
}

/// <summary>
/// The gates no custom rule may overrule: keep lists, badge games, items already promised, and untradable items. A gate
/// result of Blocked or Unknown stops the offer here and leaves it for the owner.
/// </summary>
public static class TradeGates {
	public static GateResult Check(Bot bot, TradeOffers.Offer offer, TradeValues values, IReadOnlyCollection<ulong> committed) {
		SpareRules spare = SpareRules.From(bot.Cfg);
		CardExchange.Rules exchange = CardExchange.Rules.From(bot.Cfg);

		foreach (TradeOffers.Item item in offer.Giving) {
			if (!item.Tradable) {
				return GateResult.Blocked($"{item.Name} can't be traded (Steam's trade hold, or it isn't tradable)");
			}

			if (committed.Contains(item.AssetId)) {
				return GateResult.Blocked($"{item.Name} is already promised in another live offer");
			}

			if (spare.KeepGames.Contains(item.Game) || spare.KeepNames.Any(k => Same(k, item.Name) || Same(k, item.Hash))) {
				return GateResult.Blocked($"{item.Name} is on your keep list");
			}

			if (exchange.Keeps(item.Game, item.Name, item.Hash)) {
				return GateResult.Blocked($"{item.Name} is kept - its game was bought for its badge");
			}
		}

		foreach (TradeOffers.Item item in offer.Receiving) {
			if (!item.Tradable) {
				return GateResult.Unknown($"{item.Name} isn't tradable - Steam's answer about the offer can't be trusted");
			}
		}

		return GateResult.Allowed;
	}

	private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Runs the custom rules in order. Decline vetoes win first, then the first rule that accepts; an Unknown leaves the
/// offer for the next look, and no rule at all is Abstain (the account's ordinary behaviour continues).
/// </summary>
public static class TradePolicy {
	public static TradeDecision Decide(TradeRuleSet set, TradeValues values, GateResult gates) =>
		DecideAsync(set, values, gates, CancellationToken.None).GetAwaiter().GetResult();

	public static async Task<TradeDecision> DecideAsync(TradeRuleSet set, TradeValues values, GateResult gates, CancellationToken ct) {
		if (gates.Block != GateBlock.None) {
			return new TradeDecision(gates.Block == GateBlock.Blocked ? TradeVerdict.Blocked : TradeVerdict.Unknown, null, gates.Why, []);
		}

		if (!set.Any) {
			return new TradeDecision(TradeVerdict.Abstain, null, "no custom rule applies", []);
		}

		List<RuleHit> hits = [];
		RuleHit? unknown = null;

		// Shape and partner vetoes first: a matching decline always wins.
		foreach (TradeRule rule in set.Rules.Where(static r => r.Declines)) {
			RuleHit veto = await VetoAsync(rule, values, ct).ConfigureAwait(false);

			if (veto.Verdict == TradeVerdict.Decline) {
				return new TradeDecision(TradeVerdict.Decline, rule.Id, veto.Why, [.. hits, veto]);
			}

			if ((veto.Verdict == TradeVerdict.Unknown) && (unknown == null)) {
				unknown = veto;
			}

			hits.Add(veto);
		}

		foreach (TradeRule rule in set.Rules.Where(static r => !r.Declines)) {
			if (!rule.Enabled) {
				continue;
			}

			RuleHit hit;

			try {
				(TradeVerdict verdict, string why) = await EvaluateAsync(rule, values, ct).ConfigureAwait(false);
				hit = new RuleHit(rule.Id, verdict, why);
			} catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) {
				hit = new RuleHit(rule.Id, TradeVerdict.Unknown, $"couldn't weigh the offer: {Log.Describe(e)}");
			}

			hits.Add(hit);

			if (hit.Verdict == TradeVerdict.Accept) {
				return new TradeDecision(TradeVerdict.Accept, rule.Id, hit.Why, hits);
			}

			if ((hit.Verdict == TradeVerdict.Unknown) && (unknown == null)) {
				unknown = hit;
			}
		}

		return unknown is { } u
			? new TradeDecision(TradeVerdict.Unknown, u.RuleId, u.Why, hits)
			: new TradeDecision(TradeVerdict.Abstain, null, "no custom rule applies", hits);
	}

	/// <summary>A decline rule is a shape and partner veto, or an expression that declines when it is true.</summary>
	private static async Task<RuleHit> VetoAsync(TradeRule rule, TradeValues values, CancellationToken ct) {
		if (!Partners(rule, values)) {
			return new RuleHit(rule.Id, TradeVerdict.Abstain, "from somebody this rule doesn't name");
		}

		if (rule.Kind == TradeRuleKind.Expression) {
			(double? value, string why) = await ExpressionValueAsync(rule, values, ct).ConfigureAwait(false);

			return value is not double yes ? new RuleHit(rule.Id, TradeVerdict.Unknown, why)
				: yes != 0 ? new RuleHit(rule.Id, TradeVerdict.Decline, rule.Note.Length > 0 ? rule.Note : "the offer matches a rule that declines")
				: new RuleHit(rule.Id, TradeVerdict.Abstain, "the offer doesn't match");
		}

		return Shape(rule, values)
			? new RuleHit(rule.Id, TradeVerdict.Decline, rule.Note.Length > 0 ? rule.Note : "the offer matches a rule that declines")
			: new RuleHit(rule.Id, TradeVerdict.Abstain, "the offer doesn't match");
	}

	private static async Task<(TradeVerdict, string)> EvaluateAsync(TradeRule rule, TradeValues values, CancellationToken ct) {
		if (!Partners(rule, values)) {
			return (TradeVerdict.Abstain, "from somebody this rule doesn't name");
		}

		return rule.Kind switch {
			TradeRuleKind.CardGems => await CardGemsAsync(rule, values, ct).ConfigureAwait(false),
			TradeRuleKind.SetSizeGems => await SetSizeGemsAsync(rule, values, ct).ConfigureAwait(false),
			TradeRuleKind.ItemThreshold => await ItemThresholdAsync(rule, values, ct).ConfigureAwait(false),
			TradeRuleKind.BoosterValue => await BoosterValueAsync(rule, values, ct).ConfigureAwait(false),
			TradeRuleKind.FoilSwap => await FoilSwapAsync(rule, values, ct).ConfigureAwait(false),
			TradeRuleKind.MarketProfit => await MarketProfitAsync(rule, values, ct).ConfigureAwait(false),
			TradeRuleKind.Expression => await ExpressionAsync(rule, values, ct).ConfigureAwait(false),
			_ => await ValueRatioAsync(rule, values, ct).ConfigureAwait(false)
		};
	}

	/// <summary>An expression rule: true accepts, false abstains, and a value it can't know leaves the offer.</summary>
	private static async Task<(TradeVerdict, string)> ExpressionAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		(double? value, string why) = await ExpressionValueAsync(r, v, ct).ConfigureAwait(false);

		return value is not double yes ? (TradeVerdict.Unknown, why)
			: yes != 0 ? (TradeVerdict.Accept, r.Note.Length > 0 ? r.Note : "a custom expression accepts it")
			: (TradeVerdict.Abstain, "the expression isn't met");
	}

	/// <summary>The expression's value, or null with why when it can't be worked out.</summary>
	private static async Task<(double? Value, string Why)> ExpressionValueAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		if (!TradeExpression.TryParse(r.Expr, out TradeExpression? expr, out string? problem) || (expr == null)) {
			return (null, problem ?? "the expression isn't valid");
		}

		TradeExpressionContext context = new(v, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems);
		double? value = await expr.EvaluateAsync(context, ct).ConfigureAwait(false);

		return value is null ? (null, "the expression needs a value that isn't known") : (value, "");
	}

	// ── the rules ─────────────────────────────────────────────────────────
	private static async Task<(TradeVerdict, string)> CardGemsAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		if ((v.Giving.Count == 0) || !AllMatch(v.Giving, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely cards this rule matches");
		}

		if (r.SameGame && (v.Giving.Select(static i => i.Game).Distinct().Count() > 1)) {
			return (TradeVerdict.Abstain, "the cards are from more than one game");
		}

		if (!Pays(r, v.Receiving)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		long worth = 0;

		foreach (TradeOffers.Item item in v.Giving) {
			(int? gems, string? problem) = await CardWorthAsync(r, item, v, ct).ConfigureAwait(false);

			if (gems is not int g) {
				return (TradeVerdict.Unknown, problem ?? "a card's worth isn't known");
			}

			worth += (long) g * Copies(item);
		}

		long required = Required(worth, r.MarginPct, r.MinMarginGems);
		long? payment = await PaymentAsync(r, v, ct).ConfigureAwait(false);

		return Compare(payment, required, "gems", "the cards are worth");
	}

	private static async Task<(TradeVerdict, string)> SetSizeGemsAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		if ((v.Giving.Count == 0) || !AllMatch(v.Giving, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely cards this rule matches");
		}

		if (!Pays(r, v.Receiving)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		long required = 0;

		if (r.Per == "completeSet") {
			foreach (IGrouping<uint, TradeOffers.Item> set in v.Giving.GroupBy(static i => i.Game)) {
				int? size = await SizeAsync(set.First(), v, ct).ConfigureAwait(false);
				long? per = Table(r, size);

				if ((size is null) || (per is null)) {
					return r.OnUnknownSet == "abstain"
						? (TradeVerdict.Abstain, "a card's set size isn't known")
						: (TradeVerdict.Unknown, "a card's set size isn't known");
				}

				if (set.Sum(static i => Copies(i)) != size.Value) {
					return (TradeVerdict.Abstain, "the offer isn't a whole set");
				}

				required += per.Value;
			}
		} else {
			foreach (TradeOffers.Item item in v.Giving) {
				int? size = await SizeAsync(item, v, ct).ConfigureAwait(false);
				long? per = Table(r, size);

				if (per is null) {
					return r.OnUnknownSet == "abstain"
						? (TradeVerdict.Abstain, "a card's set size isn't known")
						: (TradeVerdict.Unknown, "a card's set size isn't known, and the table has no default");
				}

				required += per.Value * Copies(item);
			}
		}

		required = Required(required, r.MarginPct, r.MinMarginGems);
		long? payment = await PaymentAsync(r, v, ct).ConfigureAwait(false);

		return Compare(payment, required, "gems", "the cards' table price");
	}

	private static async Task<(TradeVerdict, string)> ItemThresholdAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		IReadOnlyList<TradeOffers.Item> target = r.Target == "giving" ? v.Giving : v.Receiving;
		IReadOnlyList<TradeOffers.Item> paymentSide = r.Target == "giving" ? v.Receiving : v.Giving;

		if ((target.Count == 0) || !AllMatch(target, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely items this rule matches");
		}

		if (!Pays(r, paymentSide)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		int count = CountOf(target);
		long? payment = await ValueAsync(r, paymentSide, r.Target != "giving", r.Basis, v, ct).ConfigureAwait(false);

		return r.Mode == "sell"
			? Compare(payment, (r.MinEach * count) + r.MinTotal, r.Basis, "the items are worth at least")
			: CompareAtMost(payment, (r.MaxEach * count) + r.MaxTotal, r.Basis, "the most it may give");
	}

	private static async Task<(TradeVerdict, string)> BoosterValueAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		IReadOnlyList<TradeOffers.Item> target = r.Target == "giving" ? v.Giving : v.Receiving;
		IReadOnlyList<TradeOffers.Item> paymentSide = r.Target == "giving" ? v.Receiving : v.Giving;

		if ((target.Count == 0) || !AllMatch(target, r.Match)) {
			return (TradeVerdict.Abstain, "the offer isn't entirely booster packs");
		}

		if (!Pays(r, paymentSide)) {
			return (TradeVerdict.Abstain, "the payment isn't in the kinds this rule takes");
		}

		long value = 0;

		foreach (IGrouping<uint, TradeOffers.Item> game in target.GroupBy(static i => i.Game)) {
			long? each = await PackWorthAsync(r, game.Key, v, ct).ConfigureAwait(false);

			if (each is not long worth) {
				return (TradeVerdict.Unknown, "a booster pack's worth isn't known");
			}

			value += worth * game.Sum(static i => Copies(i));
		}

		long? payment = await ValueAsync(r, paymentSide, r.Target != "giving", r.ValueIn, v, ct).ConfigureAwait(false);

		return r.Mode == "sell"
			? Compare(payment, (long) Math.Ceiling(value * (1 + (r.MarginPct / 100.0))) + r.MinMargin, r.ValueIn, "the packs are worth")
			: CompareAtMost(payment, Math.Max(0, (long) Math.Floor(value * (1 - (r.MarginPct / 100.0))) - r.MinMargin), r.ValueIn,
				"the most it may give for the packs");
	}

	private static async Task<(TradeVerdict, string)> FoilSwapAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		if (r.Give is not { } give || r.Get is not { } get) {
			return (TradeVerdict.Abstain, "the rule has no shape");
		}

		if (!SideMatches(v.Giving, give) || !SideMatches(v.Receiving, get)) {
			return (TradeVerdict.Abstain, "the offer isn't that many normals for that many foils");
		}

		if (r.SameGame && (v.Giving.Concat(v.Receiving).Select(static i => i.Game).Distinct().Count() > 1)) {
			return (TradeVerdict.Abstain, "the cards are from more than one game");
		}

		if (r.MaxLossPct is not int loss) {
			return (TradeVerdict.Accept, "the shape matches");
		}

		long? given = await v.WalletOfAsync(v.Giving, giving: true, 1, 24 * 30, 0, "unknown", ct).ConfigureAwait(false);
		long? got = await v.WalletOfAsync(v.Receiving, giving: false, 1, 24 * 30, 0, "unknown", ct).ConfigureAwait(false);

		if ((given is not long g) || (got is not long f)) {
			return (TradeVerdict.Unknown, "a card's market value isn't known");
		}

		return (f * 100) >= (g * (100 - Math.Clamp(loss, 0, 100)))
			? (TradeVerdict.Accept, "the foils are worth close enough to the normals")
			: (TradeVerdict.Decline, "the foils are worth too much less than the normals");
	}

	private static async Task<(TradeVerdict, string)> MarketProfitAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		if ((r.LooseGems != "sack") && v.Giving.Concat(v.Receiving).Any(static i => i.Kind == SpareKind.Gems)) {
			return (TradeVerdict.Unknown, "loose gems have no value to go by (looseGems is \"unknown\", or there's no sack price)");
		}

		long? give = await v.WalletOfAsync(v.Giving, giving: true, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct).ConfigureAwait(false);
		long? receive = await v.WalletOfAsync(v.Receiving, giving: false, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct).ConfigureAwait(false);

		if (give is not long g) {
			return (TradeVerdict.Unknown, "what is given isn't priced well enough");
		}

		if (receive is not long got) {
			return (TradeVerdict.Unknown, "what is received isn't priced well enough");
		}

		if (g == 0) {
			return (TradeVerdict.Abstain, "nothing is given - a donation, not a market trade");
		}

		long profit = got - g;

		if (profit < r.MinProfitCents) {
			return (TradeVerdict.Abstain, $"the profit is below {r.MinProfitCents} cent(s)");
		}

		return (r.MinProfitPct <= 0) || (profit * 100 >= g * r.MinProfitPct)
			? (TradeVerdict.Accept, $"it makes {profit} cent(s) after Steam's fee")
			: (TradeVerdict.Abstain, $"the profit is below {r.MinProfitPct:0.#}% of what is given");
	}

	private static async Task<(TradeVerdict, string)> ValueRatioAsync(TradeRule r, TradeValues v, CancellationToken ct) {
		double? left = await RatioSideAsync(r, r.Left, v, ct).ConfigureAwait(false);

		if (left is not double l) {
			return (TradeVerdict.Unknown, "the receiving side can't be valued");
		}

		double? right = await RatioSideAsync(r, r.Right, v, ct).ConfigureAwait(false);

		if (right is not double w) {
			return (TradeVerdict.Unknown, "the giving side can't be valued");
		}

		double target = (w * r.Factor) + r.Offset;
		bool passes = r.Op switch {
			">=" => l >= target,
			"<=" => l <= target,
			">" => l > target,
			"<" => l < target,
			_ => Math.Abs(l - target) < 0.005
		};

		return passes
			? (TradeVerdict.Accept, $"the {r.Left} side is {l:0.##} against {target:0.##}")
			: (TradeVerdict.Abstain, $"the {r.Left} side is {l:0.##}, not {r.Op} {target:0.##}");
	}

	private static async Task<double?> RatioSideAsync(TradeRule r, string side, TradeValues v, CancellationToken ct) {
		IReadOnlyList<TradeOffers.Item> items = side == "giving" ? v.Giving : v.Receiving;

		switch (r.Basis) {
			case "gems":
				return await v.GemsOfAsync(items, ct).ConfigureAwait(false);
			case "wallet":
				return await v.WalletOfAsync(items, giving: side == "giving", r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct).ConfigureAwait(false);
			case "cardCount":
				return items.Where(static i => i.Kind is SpareKind.Card or SpareKind.FoilCard).Sum(static i => Copies(i));
			case "foilCount":
				return items.Where(static i => i.Kind == SpareKind.FoilCard).Sum(static i => Copies(i));
			case "itemCount":
				return items.Sum(static i => Copies(i));
			default:
				double total = 0;

				foreach (TradeOffers.Item item in items) {
					int? size = await SizeAsync(item, v, ct).ConfigureAwait(false);

					if (size is not int s) {
						return null;
					}

					total += s * Copies(item);
				}

				return total;
		}
	}

	// ── shared bits ───────────────────────────────────────────────────────
	private static bool Partners(TradeRule rule, TradeValues values) => rule.Partners.Allows(values.Partner, "");

	private static bool Shape(TradeRule rule, TradeValues values) {
		if (rule.Kind == TradeRuleKind.FoilSwap) {
			return rule.Give is { } g && rule.Get is { } t && SideMatches(values.Giving, g) && SideMatches(values.Receiving, t);
		}

		return AllMatch(values.Giving, rule.Match) && AllMatch(values.Receiving, rule.Match);
	}

	private static bool SideMatches(IReadOnlyList<TradeOffers.Item> side, TradeSwapSide want) {
		if (!AllMatch(side, want.Match)) {
			return false;
		}

		if (side.Any(i => (i.Kind is SpareKind.Card or SpareKind.FoilCard) && ((i.Kind == SpareKind.FoilCard) != want.Foil))) {
			return false;
		}

		return CountOf(side) >= want.Count;
	}

	private static bool AllMatch(IReadOnlyList<TradeOffers.Item> side, TradeMatch match) {
		int count = CountOf(side);

		if ((match.MinItems is int min) && (count < min)) {
			return false;
		}

		if ((match.MaxItems is int max) && (count > max)) {
			return false;
		}

		return side.All(match.Matches);
	}

	private static int Copies(TradeOffers.Item item) => (int) Math.Min(int.MaxValue, Math.Max(1u, item.Amount));

	private static int CountOf(IReadOnlyList<TradeOffers.Item> side) => side.Sum(static i => Copies(i));

	private static bool Pays(TradeRule r, IReadOnlyList<TradeOffers.Item> side) =>
		r.PayIn is not { Only: true } pay || side.All(i => pay.Kinds.Contains(i.Kind));

	private static Task<long?> PaymentAsync(TradeRule r, TradeValues v, CancellationToken ct) =>
		(r.Worth == "wallet") || (r.Basis == "wallet") || (r.ValueIn == "wallet")
			? v.WalletOfAsync(v.Receiving, giving: false, r.MinSales, r.MaxAgeHours, r.UndercutPct, "unknown", ct)
			: v.GemsOfAsync(v.Receiving, ct);

	private static Task<long?> ValueAsync(TradeRule r, IReadOnlyList<TradeOffers.Item> side, bool giving, string basis, TradeValues v, CancellationToken ct) =>
		basis == "wallet"
			? v.WalletOfAsync(side, giving, r.MinSales, r.MaxAgeHours, r.UndercutPct, r.LooseGems, ct)
			: v.GemsOfAsync(side, ct);

	private static async Task<int?> SizeAsync(TradeOffers.Item item, TradeValues v, CancellationToken ct) =>
		(await v.SetAsync(item, ct).ConfigureAwait(false)).SetSize;

	private static long? Table(TradeRule r, int? size) => size is int s ? r.Table.TryGetValue(s, out long per) ? per : r.DefaultPerCard : r.DefaultPerCard;

	private static long Required(long worth, double marginPct, long minMargin) =>
		(long) Math.Ceiling(worth * (1 + (Math.Max(0, marginPct) / 100.0))) + Math.Max(0, minMargin);

	private static async Task<(int? Gems, string? Problem)> CardWorthAsync(TradeRule r, TradeOffers.Item item, TradeValues v, CancellationToken ct) {
		ValuedItem gems = await v.GemsAsync(item, ct).ConfigureAwait(false);
		ValuedItem wallet = await v.WalletAsync(item, giving: true, r.MinSales, r.MaxAgeHours, r.UndercutPct, "unknown", ct).ConfigureAwait(false);
		int? fromWallet = (wallet.WalletCents is int cents) && (v.Source.SackNetCents() is int sack and > 0) ? (int) ((long) cents * 1000 / sack) : null;

		if (r.Worth == "wallet") {
			return fromWallet is int w ? (w, null)
				: r.Fallback == "gems" && gems.Gems is int g ? (g, null)
				: (null, wallet.Problem ?? "the card's market value can't be turned into gems");
		}

		return gems.Gems is int value ? (value, null)
			: r.Fallback == "wallet" && fromWallet is int f ? (f, null)
			: (null, gems.Problem ?? "the card's gem value isn't known");
	}

	private static async Task<long?> PackWorthAsync(TradeRule r, uint app, TradeValues v, CancellationToken ct) {
		CardSet? set = await v.SetOfAsync(app, ct).ConfigureAwait(false);

		if (set is not { Cards.Count: > 0 }) {
			return r.Value == "fixed" ? r.Gems : null;
		}

		long value;

		if (r.Value == "fixed") {
			value = r.Gems;
		} else if (r.Value == "setSize") {
			value = BoosterPick.PackGems(set.Cards.Count);
		} else {
			List<long> nets = [];

			foreach (string hash in set.Cards) {
				MarketQuote? quote = await v.QuoteAsync(hash, ct).ConfigureAwait(false);

				if ((quote == null) || (quote.Lowest is not > 0) || (quote.Sales < Math.Max(1, r.MinSales))
					|| (DateTime.UtcNow - quote.When > TimeSpan.FromHours(Math.Clamp(r.MaxAgeHours, 1, 24 * 30)))) {
					return null;
				}

				int? net = ListingPrice.For(quote.Price, 3, (int) Math.Clamp(r.UndercutPct, 0, 99)).Cents is int buyer ? Seller.ReceiveFor(buyer) : null;

				if (net is not int n) {
					return null;
				}

				nets.Add(n);
			}

			if (nets.Count == 0) {
				return null;
			}

			if (r.Value == "floor") {
				value = r.FloorMode == "threeTimesCheapest" ? 3 * nets.Min() : nets.Order().Take(3).Sum();
			} else {
				value = 3 * (long) Math.Round(nets.Average(), MidpointRounding.ToZero);

				if (value == 0) {
					return null;
				}
			}
		}

		bool inGems = r.Value is "setSize" or "fixed";

		if (r.ValueIn == "gems") {
			return inGems ? value : v.Source.SackNetCents() is int sack and > 0 ? value * 1000 / sack : null;
		}

		return inGems ? v.Source.SackNetCents() is int s and > 0 ? value * s / 1000 : null : value;
	}

	private static (TradeVerdict, string) Compare(long? payment, long required, string unit, string what) =>
		payment is not long paid ? (TradeVerdict.Unknown, "the payment's value isn't known")
			: paid >= required ? (TradeVerdict.Accept, $"{what} at least {required} {unit}")
			: (TradeVerdict.Abstain, $"{what} {required} {unit}, but the offer pays {paid}");

	private static (TradeVerdict, string) CompareAtMost(long? payment, long allowed, string unit, string what) =>
		payment is not long paid ? (TradeVerdict.Unknown, "the payment's value isn't known")
			: paid <= allowed ? (TradeVerdict.Accept, $"{what} is {allowed} {unit}")
			: (TradeVerdict.Abstain, $"{what} is {allowed} {unit}, but the offer gives {paid}");
}
