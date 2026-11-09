using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>One side of a rule's partner filter: names (case-insensitive) or SteamID64s. A deny always wins.</summary>
public sealed record TradePartners(IReadOnlyList<string> Allow, IReadOnlyList<string> Deny) {
	public static readonly TradePartners Anyone = new([], []);

	/// <summary>Whether an offer from this partner may be considered: on the allow list (or it's empty) and not denied.</summary>
	public bool Allows(ulong partner, string name) {
		if (Deny.Any(d => Matches(d, partner, name))) {
			return false;
		}

		return (Allow.Count == 0) || Allow.Any(a => Matches(a, partner, name));
	}

	private static bool Matches(string rule, ulong partner, string name) =>
		rule.Equals(name, StringComparison.OrdinalIgnoreCase)
		|| ulong.TryParse(rule, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && (id == partner);
}

/// <summary>What a rule applies to: kinds, games, exact market names, foil or not, and how many items. Empty means any.</summary>
public sealed record TradeMatch(IReadOnlyList<SpareKind> Kinds, IReadOnlyList<uint> Apps, IReadOnlyList<string> Hashes, IReadOnlyList<string> Names,
	bool? Foil, int? MinItems, int? MaxItems) {
	public static readonly TradeMatch Any = new([], [], [], [], null, null, null);

	public bool Matches(TradeOffers.Item item) {
		if ((Kinds.Count > 0) && !Kinds.Contains(item.Kind)) {
			return false;
		}

		if ((Apps.Count > 0) && !Apps.Contains(item.Game)) {
			return false;
		}

		if ((Hashes.Count > 0) && !Hashes.Contains(item.Hash, StringComparer.Ordinal)) {
			return false;
		}

		if ((Names.Count > 0) && !Names.Contains(item.Name, StringComparer.Ordinal)) {
			return false;
		}

		if (Foil is bool foil && (item.Kind is SpareKind.Card or SpareKind.FoilCard) && ((item.Kind == SpareKind.FoilCard) != foil)) {
			return false;
		}

		return true;
	}
}

/// <summary>What a gem-priced rule accepts as payment: the kinds named, and whether every payment item must be one of them.</summary>
public sealed record TradePayIn(IReadOnlyList<SpareKind> Kinds, bool Only) {
	public static readonly TradePayIn Anything = new([], false);
}

/// <summary>One side of a foil swap: how many items, foil or not, and which.</summary>
public sealed record TradeSwapSide(int Count, bool Foil, TradeMatch Match);

/// <summary>The kinds of rule the dashboard can build. No scripting: each one is a typed, reviewable decision.</summary>
public static class TradeRuleKind {
	public const string CardGems = "card-gems";
	public const string SetSizeGems = "set-size-gems";
	public const string ItemThreshold = "item-threshold";
	public const string BoosterValue = "booster-value";
	public const string FoilSwap = "foil-swap";
	public const string MarketProfit = "market-profit";
	public const string ValueRatio = "value-ratio";

	/// <summary>Any condition the typed kinds don't cover: a small, sandboxed formula over the offer's items and values.</summary>
	public const string Expression = "expression";

	public static readonly IReadOnlyList<string> All = [CardGems, SetSizeGems, ItemThreshold, BoosterValue, FoilSwap, MarketProfit, ValueRatio, Expression];
}

/// <summary>
/// One custom trade rule, as the dashboard edits it and the policy reads it. Untyped fields that don't belong to the rule's
/// kind are left at their defaults and never read - the codec only writes what the kind uses.
/// </summary>
public sealed record TradeRule {
	public string Id { get; init; } = "";
	public string Kind { get; init; } = "";
	public string Action { get; init; } = "accept";
	public bool Enabled { get; init; } = true;
	public int Priority { get; init; } = 100;
	public string Note { get; init; } = "";

	public TradePartners Partners { get; init; } = TradePartners.Anyone;
	public TradeMatch Match { get; init; } = TradeMatch.Any;
	public TradePayIn? PayIn { get; init; }

	// card-gems
	public string Worth { get; init; } = "gems";
	public string Fallback { get; init; } = "none";
	public bool SameGame { get; init; }
	public double MarginPct { get; init; }
	public long MinMarginGems { get; init; }

	// set-size-gems
	public string Per { get; init; } = "card";
	public IReadOnlyDictionary<int, long> Table { get; init; } = new Dictionary<int, long>();
	public long? DefaultPerCard { get; init; }
	public string OnUnknownSet { get; init; } = "unknown";

	// item-threshold
	public string Target { get; init; } = "";
	public string Mode { get; init; } = "";
	public string Basis { get; init; } = "gems";
	public long MinEach { get; init; }
	public long MinTotal { get; init; }
	public long MaxEach { get; init; }
	public long MaxTotal { get; init; }

	// booster-value
	public string Value { get; init; } = "expected";
	public string ValueIn { get; init; } = "gems";
	public long MinMargin { get; init; }
	public int MinSales { get; init; } = 3;
	public int MaxAgeHours { get; init; } = 24;
	public double UndercutPct { get; init; }
	public string FloorMode { get; init; } = "threeCheapestDistinct";
	public int Gems { get; init; }

	// foil-swap
	public TradeSwapSide? Give { get; init; }
	public TradeSwapSide? Get { get; init; }
	public string SwapMode { get; init; } = "exact";
	public int? MaxLossPct { get; init; }

	// market-profit
	public long MinProfitCents { get; init; } = 1;
	public double MinProfitPct { get; init; }
	public string PriceMode { get; init; } = "conservative";
	public string LooseGems { get; init; } = "sack";

	// value-ratio
	public string Left { get; init; } = "receiving";
	public string Right { get; init; } = "giving";
	public string Op { get; init; } = ">=";
	public double Factor { get; init; } = 1;
	public double Offset { get; init; }

	/// <summary>The condition of an <c>expression</c> rule - see <see cref="TradeExpression"/>.</summary>
	public string Expr { get; init; } = "";

	public bool Declines => Action.Equals("decline", StringComparison.OrdinalIgnoreCase);
	public bool IsCardLike => Kind is TradeRuleKind.CardGems or TradeRuleKind.SetSizeGems or TradeRuleKind.FoilSwap;
}

/// <summary>A parsed rule set: the rules in the order they decide, and everything that was wrong with the file.</summary>
public sealed record TradeRuleSet(IReadOnlyList<TradeRule> Rules, IReadOnlyList<string> Problems) {
	public static readonly TradeRuleSet Empty = new([], []);

	public bool Any => Rules.Count > 0;
}

/// <summary>
/// Reads and writes the rule JSON. Strict on the dashboard's save (any problem is returned, nothing applied), lenient at
/// trade time (the bad rules are skipped, the rest still work) - never an exception, never a half-applied rule.
/// </summary>
public static class TradeRuleCodec {
	public const int Version = 1, MaxRules = 100, MaxTable = 100, MaxNames = 256;
	public const int MaxBytes = 64 * 1024;

	/// <summary>Rules and problems in a saved JSON document. Null/blank is an empty set, never a problem.</summary>
	public static TradeRuleSet Parse(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return TradeRuleSet.Empty;
		}

		if (json.Length > MaxBytes) {
			return new TradeRuleSet([], [$"the rules are too long ({json.Length} characters, at most {MaxBytes})"]);
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return new TradeRuleSet([], ["the rules must be a JSON object"]);
			}

			return ParseObject(doc.RootElement);
		} catch (JsonException e) {
			return new TradeRuleSet([], [$"the rules aren't valid JSON: {e.Message}"]);
		}
	}

	/// <summary>The saved form: version 1 and the rules, kind-specific fields only.</summary>
	public static string Write(IReadOnlyList<TradeRule> rules) {
		using MemoryStream stream = new();
		using (Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true })) {
			w.WriteStartObject();
			w.WriteNumber("version", Version);
			w.WriteStartArray("rules");

			foreach (TradeRule r in rules) {
				WriteRule(w, r);
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static TradeRuleSet ParseObject(JsonElement root) {
		List<string> problems = [];
		int version = Int(root, "version") ?? 0;

		if (version != Version) {
			problems.Add($"unsupported rules version {version} - this program reads version {Version}");
			return new TradeRuleSet([], problems);
		}

		if (!root.TryGetProperty("rules", out JsonElement array) || (array.ValueKind != JsonValueKind.Array)) {
			problems.Add("the rules file has no \"rules\" array");
			return new TradeRuleSet([], problems);
		}

		List<TradeRule> rules = [];
		HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
		int index = 0;

		foreach (JsonElement element in array.EnumerateArray()) {
			index++;

			if (rules.Count >= MaxRules) {
				problems.Add($"only the first {MaxRules} rules are used");
				break;
			}

			if (element.ValueKind != JsonValueKind.Object) {
				problems.Add($"rule {index}: not an object");
				continue;
			}

			TradeRule? rule = ParseRule(element, index, problems);

			if (rule == null) {
				continue;
			}

			if (!ids.Add(rule.Id)) {
				problems.Add($"rule {index}: \"{rule.Id}\" is used by another rule - skipped");
				continue;
			}

			rules.Add(rule);
		}

		// Priority first, then the order they were written: a stable, reviewable decision.
		List<TradeRule> ordered = [.. rules.Select((rule, at) => (rule, at)).OrderBy(static x => x.rule.Priority).ThenBy(static x => x.at).Select(static x => x.rule)];

		return new TradeRuleSet(ordered, problems);
	}

	private static TradeRule? ParseRule(JsonElement e, int index, List<string> problems) {
		string id = Str(e, "id")?.Trim() is { Length: > 0 } given ? given : $"rule-{index}";
		string kind = (Str(e, "kind") ?? "").Trim().ToLowerInvariant();

		if (!TradeRuleKind.All.Contains(kind)) {
			problems.Add($"rule {index}: unknown kind \"{kind}\"");
			return null;
		}

		string action = (Str(e, "action") ?? "accept").Trim().ToLowerInvariant();

		if (action is not ("accept" or "decline")) {
			problems.Add($"rule {index}: action must be accept or decline");
			return null;
		}

		TradeMatch match = Match(e, index, problems);
		TradeRule r = new() {
			Id = id,
			Kind = kind,
			Action = action,
			Enabled = Bool(e, "enabled") ?? true,
			Priority = Math.Clamp(Int(e, "priority") ?? 100, int.MinValue, int.MaxValue),
			Note = Str(e, "note") ?? "",
			Partners = Partners(e),
			Match = match,
			PayIn = PayIn(e, index, problems)
		};

		return kind switch {
			TradeRuleKind.CardGems => CardGems(e, r, index, problems),
			TradeRuleKind.SetSizeGems => SetSizeGems(e, r, index, problems),
			TradeRuleKind.ItemThreshold => ItemThreshold(e, r, index, problems),
			TradeRuleKind.BoosterValue => BoosterValue(e, r, index, problems),
			TradeRuleKind.FoilSwap => FoilSwap(e, r, index, problems),
			TradeRuleKind.MarketProfit => MarketProfit(e, r, index, problems),
			TradeRuleKind.Expression => ExpressionRule(e, r, index, problems),
			TradeRuleKind.ValueRatio or _ => ValueRatio(e, r, index, problems)
		};
	}

	private static TradeRule? CardGems(JsonElement e, TradeRule r, int index, List<string> problems) {
		string worth = (Str(e, "worth") ?? "gems").Trim().ToLowerInvariant();
		string fallback = (Str(e, "fallback") ?? "none").Trim().ToLowerInvariant();

		if (worth is not ("gems" or "wallet")) {
			problems.Add($"rule {index}: worth must be gems or wallet");
			return null;
		}

		if (fallback is not ("none" or "gems" or "wallet") || (fallback == worth)) {
			problems.Add($"rule {index}: fallback must be none, or the other of gems and wallet");
			return null;
		}

		return r with {
			Worth = worth, Fallback = fallback, SameGame = Bool(e, "sameGame") ?? false,
			MarginPct = Number(e, "marginPct"), MinMarginGems = Math.Max(0, Int(e, "minMarginGems") ?? 0)
		};
	}

	private static TradeRule? SetSizeGems(JsonElement e, TradeRule r, int index, List<string> problems) {
		string per = (Str(e, "per") ?? "card").Trim().ToLowerInvariant();
		string unknown = (Str(e, "onUnknownSet") ?? "unknown").Trim().ToLowerInvariant();

		if (per is not ("card" or "completeSet")) {
			problems.Add($"rule {index}: per must be card or completeSet");
			return null;
		}

		if (unknown is not ("unknown" or "abstain")) {
			problems.Add($"rule {index}: onUnknownSet must be unknown or abstain");
			return null;
		}

		Dictionary<int, long> table = [];

		if (e.TryGetProperty("table", out JsonElement t) && (t.ValueKind == JsonValueKind.Object)) {
			foreach (JsonProperty p in t.EnumerateObject()) {
				if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || (size <= 0) || (p.Value.ValueKind != JsonValueKind.Number)
					|| !p.Value.TryGetInt64(out long gems) || (gems <= 0)) {
					problems.Add($"rule {index}: table entry \"{p.Name}\" must be a set size and a whole number of gems above 0");
					return null;
				}

				table[size] = gems;
			}
		}

		if (table.Count == 0) {
			problems.Add($"rule {index}: the set-size table is empty");
			return null;
		}

		if (table.Count > MaxTable) {
			problems.Add($"rule {index}: the table has more than {MaxTable} entries");
			return null;
		}

		long? def = Int(e, "defaultPerCard") is int d ? Math.Max(0, d) : null;

		return r with {
			Per = per, Table = table, DefaultPerCard = def, OnUnknownSet = unknown,
			MarginPct = Number(e, "marginPct"), MinMarginGems = Math.Max(0, Int(e, "minMarginGems") ?? 0)
		};
	}

	private static TradeRule? ItemThreshold(JsonElement e, TradeRule r, int index, List<string> problems) {
		string target = (Str(e, "target") ?? "").Trim().ToLowerInvariant();
		string mode = (Str(e, "mode") ?? "").Trim().ToLowerInvariant();
		string basis = (Str(e, "basis") ?? "gems").Trim().ToLowerInvariant();

		if (mode is not ("sell" or "buy") || (target != (mode == "sell" ? "giving" : "receiving"))) {
			problems.Add($"rule {index}: target must be {(mode == "sell" ? "giving for a sale" : "receiving for a purchase")}");
			return null;
		}

		if (basis is not ("gems" or "wallet")) {
			problems.Add($"rule {index}: basis must be gems or wallet");
			return null;
		}

		return r with {
			Target = target, Mode = mode, Basis = basis,
			MinEach = Math.Max(0, Int(e, "minEach") ?? 0), MinTotal = Math.Max(0, Int(e, "minTotal") ?? 0),
			MaxEach = Math.Max(0, Int(e, "maxEach") ?? 0), MaxTotal = Math.Max(0, Int(e, "maxTotal") ?? 0)
		};
	}

	private static TradeRule? BoosterValue(JsonElement e, TradeRule r, int index, List<string> problems) {
		string target = (Str(e, "target") ?? "").Trim().ToLowerInvariant();
		string mode = (Str(e, "mode") ?? "").Trim().ToLowerInvariant();
		string value = (Str(e, "value") ?? "expected").Trim().ToLowerInvariant();
		string valueIn = (Str(e, "valueIn") ?? "gems").Trim().ToLowerInvariant();
		string floor = (Str(e, "floorMode") ?? "threeCheapestDistinct").Trim().ToLowerInvariant();
		string loose = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant();

		if (mode is not ("sell" or "buy") || (target != (mode == "sell" ? "giving" : "receiving"))) {
			problems.Add($"rule {index}: target must be {(mode == "sell" ? "giving for a sale" : "receiving for a purchase")}");
			return null;
		}

		if (value is not ("expected" or "floor" or "setSize" or "fixed") || valueIn is not ("gems" or "wallet")) {
			problems.Add($"rule {index}: value must be expected, floor, setSize or fixed; valueIn gems or wallet");
			return null;
		}

		if ((value == "fixed") && ((Int(e, "gems") ?? 0) <= 0)) {
			problems.Add($"rule {index}: a fixed booster is worth a whole number of gems above 0");
			return null;
		}

		if (floor is not ("threeCheapestDistinct" or "threeTimesCheapest") || loose is not ("unknown" or "sack")) {
			problems.Add($"rule {index}: floorMode or looseGems isn't one this program knows");
			return null;
		}

		return r with {
			Target = target, Mode = mode, Value = value, ValueIn = valueIn,
			MinMargin = Math.Max(0, Int(e, "minMargin") ?? 0), MinSales = Math.Max(1, Int(e, "minSales") ?? 3),
			MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24), UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99),
			FloorMode = floor, Gems = Math.Max(0, Int(e, "gems") ?? 0), LooseGems = loose,
			MarginPct = Number(e, "marginPct")
		};
	}

	private static TradeRule? FoilSwap(JsonElement e, TradeRule r, int index, List<string> problems) {
		if (Side(e, "give", index, problems) is not { } give || Side(e, "get", index, problems) is not { } get) {
			return null;
		}

		string mode = (Str(e, "mode") ?? "exact").Trim();

		if (!mode.Equals("exact", StringComparison.OrdinalIgnoreCase) && !mode.Equals("atLeast", StringComparison.OrdinalIgnoreCase)) {
			problems.Add($"rule {index}: mode must be exact or atLeast");
			return null;
		}

		int? loss = null;

		if (e.TryGetProperty("maxLossPct", out JsonElement lossElement) && (lossElement.ValueKind != JsonValueKind.Null)) {
			if ((lossElement.ValueKind != JsonValueKind.Number) || !lossElement.TryGetInt32(out int pct) || (pct is < 0 or > 100)) {
				problems.Add($"rule {index}: maxLossPct must be 0 to 100, or null");
				return null;
			}

			loss = pct;
		}

		return r with { Give = give, Get = get, SwapMode = mode.ToLowerInvariant() == "atleast" ? "atLeast" : "exact", MaxLossPct = loss };
	}

	private static TradeSwapSide? Side(JsonElement e, string name, int index, List<string> problems) {
		if (!e.TryGetProperty(name, out JsonElement side) || (side.ValueKind != JsonValueKind.Object)) {
			problems.Add($"rule {index}: {name} is missing");
			return null;
		}

		int count = Int(side, "count") ?? 0;

		if (count < 0) {
			problems.Add($"rule {index}: {name}.count can't be negative");
			return null;
		}

		return new TradeSwapSide(count, Bool(side, "foil") ?? false, Match(side, index, problems));
	}

	private static TradeRule? MarketProfit(JsonElement e, TradeRule r, int index, List<string> problems) {
		string price = (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant();
		string loose = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant();

		if (price is not ("conservative" or "lowest" or "median") || loose is not ("unknown" or "sack")) {
			problems.Add($"rule {index}: priceMode or looseGems isn't one this program knows");
			return null;
		}

		return r with {
			MinProfitCents = Math.Max(0, Int(e, "minProfitCents") ?? 1), MinProfitPct = Math.Clamp(Number(e, "minProfitPct"), 0, 1_000_000),
			MinSales = Math.Max(1, Int(e, "minSales") ?? 3), MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24),
			PriceMode = price, UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99), LooseGems = loose
		};
	}

	/// <summary>An expression rule: any condition the formula language can state (Core/TradeExpression.cs).</summary>
	private static TradeRule? ExpressionRule(JsonElement e, TradeRule r, int index, List<string> problems) {
		string expr = (Str(e, "expr") ?? Str(e, "expression") ?? "").Trim();

		if (expr.Length == 0) {
			problems.Add($"rule {index}: an expression rule needs \"expr\"");

			return null;
		}

		if (TradeExpression.Validate(expr) is { } why) {
			problems.Add($"rule {index}: {why}");

			return null;
		}

		string price = (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant();
		string loose = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant();

		if (price is not ("conservative" or "lowest" or "median") || loose is not ("unknown" or "sack")) {
			problems.Add($"rule {index}: priceMode or looseGems isn't one this program knows");

			return null;
		}

		return r with {
			Expr = expr, MinSales = Math.Max(1, Int(e, "minSales") ?? 3), MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24),
			PriceMode = price, UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99), LooseGems = loose
		};
	}

	private static TradeRule? ValueRatio(JsonElement e, TradeRule r, int index, List<string> problems) {
		string basis = (Str(e, "basis") ?? "wallet").Trim().ToLowerInvariant();
		string left = (Str(e, "left") ?? "receiving").Trim().ToLowerInvariant();
		string right = (Str(e, "right") ?? "giving").Trim().ToLowerInvariant();
		string op = (Str(e, "op") ?? ">=").Trim();

		if (basis is not ("gems" or "wallet" or "cardCount" or "foilCount" or "setSize" or "itemCount")) {
			problems.Add($"rule {index}: basis must be gems, wallet, cardCount, foilCount, setSize or itemCount");
			return null;
		}

		if (left is not ("receiving" or "giving") || right is not ("receiving" or "giving")) {
			problems.Add($"rule {index}: left and right must be receiving or giving");
			return null;
		}

		if (op is not (">=" or "<=" or ">" or "<" or "==")) {
			problems.Add($"rule {index}: op must be >=, <=, >, < or ==");
			return null;
		}

		double factor = Number(e, "factor", 1), offset = Number(e, "offset");

		if (!double.IsFinite(factor) || !double.IsFinite(offset)) {
			problems.Add($"rule {index}: factor and offset must be numbers");
			return null;
		}

		return r with {
			Basis = basis, Left = left, Right = right, Op = op, Factor = factor, Offset = offset,
			MinSales = Math.Max(1, Int(e, "minSales") ?? 3), MaxAgeHours = Math.Max(1, Int(e, "maxAgeHours") ?? 24),
			PriceMode = (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant() is "lowest" or "median" ? (Str(e, "priceMode") ?? "conservative").Trim().ToLowerInvariant() : "conservative",
			UndercutPct = Math.Clamp(Number(e, "undercutPct"), 0, 99),
			LooseGems = (Str(e, "looseGems") ?? "sack").Trim().ToLowerInvariant() == "unknown" ? "unknown" : "sack"
		};
	}

	private static TradePartners Partners(JsonElement e) {
		if (!e.TryGetProperty("when", out JsonElement when) || (when.ValueKind != JsonValueKind.Object)
			|| !when.TryGetProperty("partners", out JsonElement p) || (p.ValueKind != JsonValueKind.Object)) {
			return TradePartners.Anyone;
		}

		return new TradePartners(Lines(p, "allow"), Lines(p, "deny"));
	}

	private static TradePayIn? PayIn(JsonElement e, int index, List<string> problems) {
		if (!e.TryGetProperty("payIn", out JsonElement p) || (p.ValueKind == JsonValueKind.Null)) {
			return null;
		}

		if (p.ValueKind != JsonValueKind.Object) {
			problems.Add($"rule {index}: payIn must be an object");
			return null;
		}

		List<SpareKind> kinds = [];

		foreach (string name in Lines(p, "kinds")) {
			if (!Enum.TryParse(name, true, out SpareKind kind) || !Enum.IsDefined(kind)) {
				problems.Add($"rule {index}: payIn kind \"{name}\" isn't one this program knows");
				return null;
			}

			kinds.Add(kind);
		}

		return new TradePayIn(kinds, Bool(p, "only") ?? false);
	}

	private static TradeMatch Match(JsonElement e, int index, List<string> problems) {
		if (!e.TryGetProperty("match", out JsonElement m) || (m.ValueKind == JsonValueKind.Null)) {
			return TradeMatch.Any;
		}

		if (m.ValueKind != JsonValueKind.Object) {
			problems.Add($"rule {index}: match must be an object");
			return TradeMatch.Any;
		}

		List<SpareKind> kinds = [];

		foreach (string name in Lines(m, "kinds")) {
			if (!Enum.TryParse(name, true, out SpareKind kind) || !Enum.IsDefined(kind)) {
				problems.Add($"rule {index}: match kind \"{name}\" isn't one this program knows");
				continue;
			}

			kinds.Add(kind);
		}

		List<uint> apps = [];

		foreach (string app in Lines(m, "apps")) {
			if (uint.TryParse(app, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) {
				apps.Add(id);
			} else {
				problems.Add($"rule {index}: app \"{app}\" isn't a number");
			}
		}

		List<string> hashes = [.. Lines(m, "hashes").Take(MaxNames)];
		List<string> names = [.. Lines(m, "names").Take(MaxNames)];

		if (hashes.Count == MaxNames) {
			problems.Add($"rule {index}: only the first {MaxNames} hashes are used");
		}

		if (names.Count == MaxNames) {
			problems.Add($"rule {index}: only the first {MaxNames} names are used");
		}

		int? min = Int(m, "minItems"), max = Int(m, "maxItems");
		bool? foil = m.TryGetProperty("foil", out JsonElement f) && (f.ValueKind is JsonValueKind.True or JsonValueKind.False) ? f.GetBoolean() : null;

		return new TradeMatch(kinds, apps, hashes, names, foil, min, max);
	}

	private static List<string> Lines(JsonElement e, string name) {
		if (!e.TryGetProperty(name, out JsonElement v)) {
			return [];
		}

		if (v.ValueKind == JsonValueKind.Array) {
			return [.. v.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.String).Select(static x => x.GetString() ?? "").Where(static x => x.Length > 0)];
		}

		return v.ValueKind == JsonValueKind.String ? [.. (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];
	}

	private static string? Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() : null;

	private static bool? Bool(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : null;

	private static int? Int(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetInt32(out int n) ? n : null;

	private static double Number(JsonElement e, string name, double fallback = 0) =>
		e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number) && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : fallback;

	// ── writing ────────────────────────────────────────────────────────────
	private static void WriteRule(Utf8JsonWriter w, TradeRule r) {
		w.WriteStartObject();
		w.WriteString("id", r.Id);
		w.WriteString("kind", r.Kind);
		w.WriteString("action", r.Action);
		w.WriteBoolean("enabled", r.Enabled);
		w.WriteNumber("priority", r.Priority);

		if (r.Note.Length > 0) {
			w.WriteString("note", r.Note);
		}

		if ((r.Partners.Allow.Count > 0) || (r.Partners.Deny.Count > 0)) {
			w.WriteStartObject("when");
			w.WriteStartObject("partners");
			WriteArray(w, "allow", r.Partners.Allow);
			WriteArray(w, "deny", r.Partners.Deny);
			w.WriteEndObject();
			w.WriteEndObject();
		}

		if (!Same(r.Match, TradeMatch.Any)) {
			w.WriteStartObject("match");
			WriteArray(w, "kinds", [.. r.Match.Kinds.Select(static k => k.ToString())]);
			WriteArray(w, "apps", [.. r.Match.Apps.Select(static a => a.ToString(CultureInfo.InvariantCulture))]);
			WriteArray(w, "hashes", r.Match.Hashes);
			WriteArray(w, "names", r.Match.Names);

			if (r.Match.Foil is bool foil) {
				w.WriteBoolean("foil", foil);
			}

			if (r.Match.MinItems is int min) {
				w.WriteNumber("minItems", min);
			}

			if (r.Match.MaxItems is int max) {
				w.WriteNumber("maxItems", max);
			}

			w.WriteEndObject();
		}

		if (r.PayIn is { } pay) {
			w.WriteStartObject("payIn");
			WriteArray(w, "kinds", [.. pay.Kinds.Select(static k => k.ToString())]);
			w.WriteBoolean("only", pay.Only);
			w.WriteEndObject();
		}

		switch (r.Kind) {
			case TradeRuleKind.CardGems:
				w.WriteString("worth", r.Worth);
				w.WriteString("fallback", r.Fallback);
				w.WriteBoolean("sameGame", r.SameGame);
				w.WriteNumber("marginPct", r.MarginPct);
				w.WriteNumber("minMarginGems", r.MinMarginGems);

				break;
			case TradeRuleKind.SetSizeGems:
				w.WriteString("per", r.Per);
				w.WriteStartObject("table");

				foreach ((int size, long gems) in r.Table.OrderBy(static t => t.Key)) {
					w.WriteNumber(size.ToString(CultureInfo.InvariantCulture), gems);
				}

				w.WriteEndObject();

				if (r.DefaultPerCard is long def) {
					w.WriteNumber("defaultPerCard", def);
				}

				w.WriteString("onUnknownSet", r.OnUnknownSet);
				w.WriteNumber("marginPct", r.MarginPct);
				w.WriteNumber("minMarginGems", r.MinMarginGems);

				break;
			case TradeRuleKind.ItemThreshold:
				w.WriteString("target", r.Target);
				w.WriteString("mode", r.Mode);
				w.WriteString("basis", r.Basis);
				w.WriteNumber(r.Mode == "buy" ? "maxEach" : "minEach", r.Mode == "buy" ? r.MaxEach : r.MinEach);
				w.WriteNumber(r.Mode == "buy" ? "maxTotal" : "minTotal", r.Mode == "buy" ? r.MaxTotal : r.MinTotal);

				break;
			case TradeRuleKind.BoosterValue:
				w.WriteString("target", r.Target);
				w.WriteString("mode", r.Mode);
				w.WriteString("value", r.Value);
				w.WriteString("valueIn", r.ValueIn);
				w.WriteNumber("marginPct", r.MarginPct);
				w.WriteNumber("minMargin", r.MinMargin);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("floorMode", r.FloorMode);

				if (r.Value == "fixed") {
					w.WriteNumber("gems", r.Gems);
				}

				if (r.ValueIn == "wallet") {
					w.WriteString("looseGems", r.LooseGems);
				}

				break;
			case TradeRuleKind.FoilSwap:
				WriteSide(w, "give", r.Give!);
				WriteSide(w, "get", r.Get!);
				w.WriteString("mode", r.SwapMode);

				if (r.MaxLossPct is int loss) {
					w.WriteNumber("maxLossPct", loss);
				} else {
					w.WriteNull("maxLossPct");
				}

				break;
			case TradeRuleKind.MarketProfit:
				w.WriteNumber("minProfitCents", r.MinProfitCents);
				w.WriteNumber("minProfitPct", r.MinProfitPct);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteString("priceMode", r.PriceMode);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("looseGems", r.LooseGems);

				break;
			case TradeRuleKind.Expression:
				w.WriteString("expr", r.Expr);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteString("priceMode", r.PriceMode);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("looseGems", r.LooseGems);

				break;
			default:
				w.WriteString("basis", r.Basis);
				w.WriteString("left", r.Left);
				w.WriteString("right", r.Right);
				w.WriteString("op", r.Op);
				w.WriteNumber("factor", r.Factor);
				w.WriteNumber("offset", r.Offset);
				w.WriteNumber("minSales", r.MinSales);
				w.WriteNumber("maxAgeHours", r.MaxAgeHours);
				w.WriteString("priceMode", r.PriceMode);
				w.WriteNumber("undercutPct", r.UndercutPct);
				w.WriteString("looseGems", r.LooseGems);

				break;
		}

		w.WriteEndObject();
	}

	private static void WriteSide(Utf8JsonWriter w, string name, TradeSwapSide side) {
		w.WriteStartObject(name);
		w.WriteNumber("count", side.Count);
		w.WriteBoolean("foil", side.Foil);

		if (!Same(side.Match, TradeMatch.Any)) {
			w.WriteStartObject("match");
			WriteArray(w, "kinds", [.. side.Match.Kinds.Select(static k => k.ToString())]);
			WriteArray(w, "apps", [.. side.Match.Apps.Select(static a => a.ToString(CultureInfo.InvariantCulture))]);
			WriteArray(w, "hashes", side.Match.Hashes);
			WriteArray(w, "names", side.Match.Names);
			w.WriteEndObject();
		}

		w.WriteEndObject();
	}

	private static void WriteArray(Utf8JsonWriter w, string name, IReadOnlyList<string> values) {
		w.WriteStartArray(name);

		foreach (string v in values) {
			w.WriteStringValue(v);
		}

		w.WriteEndArray();
	}

	private static bool Same(TradeMatch a, TradeMatch b) =>
		a.Kinds.SequenceEqual(b.Kinds) && a.Apps.SequenceEqual(b.Apps) && a.Hashes.SequenceEqual(b.Hashes) && a.Names.SequenceEqual(b.Names)
		&& (a.Foil == b.Foil) && (a.MinItems == b.MinItems) && (a.MaxItems == b.MaxItems);
}

/// <summary>One ready-made rule the dashboard offers, with the words it shows.</summary>
public sealed record TradePreset(string Id, string Label, TradeRule Rule);

/// <summary>The starting points the dashboard's preset gallery lists - one for each kind, plus the examples people ask for.</summary>
public static class TradePresets {
	public static IReadOnlyList<TradePreset> All() => [
		new("cards-worth", "Cards for their gem worth", new TradeRule {
			Id = "cards-worth", Kind = TradeRuleKind.CardGems, Note = "Give cards, take at least their GooValue in gems.",
			Match = new([SpareKind.Card], [], [], [], false, null, null), Worth = "gems", Fallback = "wallet", SameGame = true,
			PayIn = new([SpareKind.Gems, SpareKind.GemSack], true)
		}),
		new("set-size-table", "Gems by card-set size", new TradeRule {
			Id = "set-size-table", Kind = TradeRuleKind.SetSizeGems, Note = "A price table for each card of a set size.",
			Match = new([SpareKind.Card], [], [], [], false, null, null), Per = "card",
			Table = new Dictionary<int, long> { [5] = 15, [8] = 20, [10] = 25, [15] = 35 }
		}),
		new("sell-backgrounds", "Sell backgrounds for gems", new TradeRule {
			Id = "sell-backgrounds", Kind = TradeRuleKind.ItemThreshold, Note = "Give backgrounds, take gems.",
			Match = new([SpareKind.Background], [], [], [], null, null, null), Target = "giving", Mode = "sell", Basis = "gems", MinEach = 100,
			PayIn = new([SpareKind.Gems, SpareKind.GemSack], true)
		}),
		new("buy-backgrounds", "Buy backgrounds for gems", new TradeRule {
			Id = "buy-backgrounds", Kind = TradeRuleKind.ItemThreshold, Note = "Take backgrounds, give no more than this in gems.",
			Match = new([SpareKind.Background], [], [], [], null, null, null), Target = "receiving", Mode = "buy", Basis = "gems", MaxEach = 100
		}),
		new("sell-boosters-expected", "Sell boosters at their expected pull", new TradeRule {
			Id = "sell-boosters-expected", Kind = TradeRuleKind.BoosterValue, Note = "Give boosters, take at least the average of their 3 cards.",
			Match = new([SpareKind.BoosterPack], [], [], [], null, null, null), Target = "giving", Mode = "sell", Value = "expected", ValueIn = "gems", MarginPct = 10
		}),
		new("buy-boosters-floor", "Buy boosters below their worst pull", new TradeRule {
			Id = "buy-boosters-floor", Kind = TradeRuleKind.BoosterValue, Note = "Take boosters, give no more than the three cheapest cards are worth.",
			Match = new([SpareKind.BoosterPack], [], [], [], null, null, null), Target = "receiving", Mode = "buy", Value = "floor", ValueIn = "gems",
			FloorMode = "threeCheapestDistinct"
		}),
		new("normals-for-foils", "Normals for foils", new TradeRule {
			Id = "normals-for-foils", Kind = TradeRuleKind.FoilSwap, Note = "Three normal cards for one foil, never at a loss.",
			Give = new(3, false, new([SpareKind.Card], [], [], [], false, null, null)),
			Get = new(1, true, new([SpareKind.FoilCard], [], [], [], true, null, null)),
			SameGame = true, SwapMode = "exact", MaxLossPct = 0
		}),
		new("expression-gems", "Any trade: a tenth more in gems", new TradeRule {
			Id = "expression-gems", Kind = TradeRuleKind.Expression, Note = "Take at least a tenth more in gems than you give.",
			Expr = "receiving.gems >= giving.gems * 1.15", LooseGems = "sack"
		}),
		new("expression-foil-swap", "Any trade: 3 normal cards for a foil", new TradeRule {
			Id = "expression-foil-swap", Kind = TradeRuleKind.Expression, Note = "Three normal cards for a foil, never at a loss.",
			Expr = "count(\"giving\", \"Card\") == 3 && count(\"receiving\", \"FoilCard\") == 1 && receiving.wallet >= giving.wallet",
			LooseGems = "sack"
		}),
		new("market-profit", "Any trade with market profit", new TradeRule {
			Id = "market-profit", Kind = TradeRuleKind.MarketProfit, Note = "Take trades whose net market value is above what is given.",
			MinProfitCents = 1, MinProfitPct = 0, MinSales = 3, MaxAgeHours = 24, PriceMode = "conservative", LooseGems = "sack"
		}),
		new("exchange-gems", "Key/gem exchange: at least a tenth more in gems", new TradeRule {
			Id = "exchange-gems", Kind = TradeRuleKind.ValueRatio, Note = "Take any trade that brings at least 10% more gems than it gives - the rule a key/gem exchange bot runs on. Edit the factor to your spread.",
			Basis = "gems", Left = "receiving", Op = ">=", Right = "giving", Factor = 1.1, LooseGems = "sack"
		}),
		new("levelup-sets", "Level-up: sell a complete set for gems", new TradeRule {
			Id = "levelup-sets", Kind = TradeRuleKind.SetSizeGems, Note = "Give a complete card set, take the table's gems for its size. Edit the table to your price list.",
			Match = new([SpareKind.Card], [], [], [], false, null, null), Per = "completeSet",
			Table = new Dictionary<int, long> { [5] = 1000, [6] = 1000, [8] = 1200, [10] = 1500, [15] = 2000 },
			PayIn = new([SpareKind.Gems, SpareKind.GemSack], true)
		}),
		new("ratio", "Compare the two sides", new TradeRule {
			Id = "ratio", Kind = TradeRuleKind.ValueRatio, Note = "Take trades worth at least this much more than they give.",
			Basis = "wallet", Left = "receiving", Op = ">=", Right = "giving", Factor = 1.1, MinSales = 3, MaxAgeHours = 24, PriceMode = "conservative"
		})
	];
}
