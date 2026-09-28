using System.Collections;
using System.Reflection;
using NocatFarm;

int fails = 0;
void Check(string name, bool ok, string detail = "") {
	fails += ok ? 0 : 1;
	Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
}

// ── Rng.HumanMinutes ─────────────────────────────────────────────────────────
const int N = 200_000;
double sum = 0, below = 0, min = double.MaxValue, max = double.MinValue;
for (int i = 0; i < N; i++) {
	double m = Rng.HumanMinutes(2, 15).TotalMinutes;
	sum += m; min = Math.Min(min, m); max = Math.Max(max, m);
	if (m < 8.5) below++;
}
double mean = sum / N, share = below / N;
Check("2-15: never outside the window", min >= 2 && max <= 15, $"min {min:0.00}, max {max:0.00}");
Check("2-15: uses the whole window", min < 2.3 && max > 14.0, $"min {min:0.00}, max {max:0.00}");
Check("2-15: leans early - mean ~7.42 (a flat pick would be 8.5)", Math.Abs(mean - 7.4167) < 0.05, $"mean {mean:0.000}");
Check("2-15: ~2/3 land in the first half", Math.Abs(share - 0.6667) < 0.01, $"{share:P1}");
Check("5-5: exactly 5", Rng.HumanMinutes(5, 5) == TimeSpan.FromMinutes(5));
Check("0-0: instant", Rng.HumanMinutes(0, 0) == TimeSpan.Zero);
bool negOk = true, invOk = true;
for (int i = 0; i < 10_000; i++) {
	TimeSpan a = Rng.HumanMinutes(-3, 2); if (a < TimeSpan.Zero || a > TimeSpan.FromMinutes(2)) negOk = false;
	if (Rng.HumanMinutes(10, 3) != TimeSpan.FromMinutes(10)) invOk = false;
}
Check("-3..2: clamped to 0..2", negOk);
Check("10..3 (longest below shortest): treated as 10", invOk);

// ── ReactionQueue<int> ───────────────────────────────────────────────────────
Type open = typeof(Rng).Assembly.GetType("NocatFarm.Core.ReactionQueue`1")!;
Type qt = open.MakeGenericType(typeof(int));
object q = Activator.CreateInstance(qt, nonPublic: true)!;
object? Call(string name, params object?[] args) => qt.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!.Invoke(q, args);
T Get<T>(string prop) => (T) qt.GetProperty(prop)!.GetValue(q)!;
List<int> DueAt(DateTime at) => ((IEnumerable) Call("Due", at)!).Cast<int>().ToList();

DateTime t0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
Func<TimeSpan> five = () => TimeSpan.FromMinutes(5), three = () => TimeSpan.FromMinutes(3);

Check("Add queues on hold", (bool) Call("Add", 1, null)! && Get<bool>("AnyHeld") && qt.GetProperty("Next")!.GetValue(q) == null);
Check("held: nothing is ever due", DueAt(t0.AddYears(1)).Count == 0);
var armed = ((IEnumerable) Call("Arm", t0, five)!).Cast<object>().ToList();
Check("Arm gives the held item its wait from now", armed.Count == 1 && (DateTime?) Call("DueOf", 1) == t0.AddMinutes(5) && !Get<bool>("AnyHeld"));
Check("not due before its wait runs out", DueAt(t0.AddMinutes(4)).Count == 0);
Check("due once it does", DueAt(t0.AddMinutes(5)).SequenceEqual([1]));
Check("Add of a queued item is refused, wait kept", !(bool) Call("Add", 1, t0)! && (DateTime?) Call("DueOf", 1) == t0.AddMinutes(5));
Call("Add", 2, t0.AddMinutes(1));
Check("Next is the soonest wait", (DateTime?) qt.GetProperty("Next")!.GetValue(q) == t0.AddMinutes(1));
Check("Due lists soonest first", DueAt(t0.AddMinutes(10)).SequenceEqual([2, 1]));
Call("Hold");
Check("asleep: everything back on hold, nothing due", Get<bool>("AnyHeld") && DueAt(t0.AddYears(1)).Count == 0);
DateTime woke = t0.AddHours(9);
var rearmed = ((IEnumerable) Call("Arm", woke, three)!).Cast<object>().ToList();
Check("woke: each gets a fresh wait from waking, not the old one", rearmed.Count == 2
	&& (DateTime?) Call("DueOf", 1) == woke.AddMinutes(3) && (DateTime?) Call("DueOf", 2) == woke.AddMinutes(3));
Check("woke: nothing fires the moment it's up", DueAt(woke).Count == 0);
Call("RetainOnly", new List<int> { 2 });
Check("RetainOnly drops what's gone", !(bool) Call("Contains", 1)! && (bool) Call("Contains", 2)! && Get<int>("Count") == 1);
Call("Remove", 2);
Check("Remove empties it", Get<int>("Count") == 0 && qt.GetProperty("Next")!.GetValue(q) == null);

// ── Gifts: telling a gifted game from a trial, against the live store ────────
Type gifts = typeof(Rng).Assembly.GetType("NocatFarm.Modules.Gifts")!;
const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
async Task<(bool? Game, string Name)> WhatIs(uint package, CancellationToken ct = default)
	=> await (Task<(bool?, string)>) gifts.GetMethod("WhatIsAsync", Priv)!.Invoke(null, [package, ct])!;

var steep = await WhatIs(142186);
Check("Steep (142186, priced) is a gifted game", steep == (true, "Steep"), $"{steep}");
var riders = await WhatIs(821602);
Check("Riders Republic (821602, priced) is a gifted game", riders == (true, "Riders Republic"), $"{riders}");
var missing = await WhatIs(1);
Check("a package the store doesn't sell is a definite trial, not 'unsure'", missing == (false, ""), $"{missing}");
var none = await WhatIs(0);
Check("no package named: taken as it arrived", none == (false, ""), $"{none}");

bool threw = false;
try { await WhatIs(142186, new CancellationToken(true)); } catch (OperationCanceledException) { threw = true; }
Check("our own cancellation still gets through", threw);

var trial = (System.Text.RegularExpressions.Regex) gifts.GetMethod("TrialName", Priv)!.Invoke(null, null)!;
string[] trials = ["Left 4 Dead 2 Guest Pass", "The Crew 2 - Free Trial", "Free Weekend", "Portal 2 Demo", "XYZ trial"];
string[] games = ["Trials Rising", "Trials Fusion", "Demolish & Build 2018", "Steep", "Guest Of Honour Pass"];
Check("trial names are recognised", trials.All(trial.IsMatch), string.Join(" | ", trials.Where(n => !trial.IsMatch(n))));
Check("games that only look like trials aren't", !games.Any(trial.IsMatch), string.Join(" | ", games.Where(n => trial.IsMatch(n))));

MethodInfo gap = gifts.GetMethod("RetryGap", Priv)!;
int[] gaps = [.. Enumerable.Range(1, 8).Select(n => (int) ((TimeSpan) gap.Invoke(null, [n])!).TotalMinutes)];
Check("store retries back off 2,4,8,16,32 then hourly", gaps.SequenceEqual([2, 4, 8, 16, 32, 60, 60, 60]), string.Join(",", gaps));

// ── Card farming estimate ────────────────────────────────────────────────────
Check("Rough: 45m / 2h30m / 9h / 3d4h", Fmt.Rough(45) == "45m" && Fmt.Rough(150) == "2h30m" && Fmt.Rough(540) == "9h" && Fmt.Rough(76 * 60) == "3d4h",
	$"{Fmt.Rough(45)} {Fmt.Rough(150)} {Fmt.Rough(540)} {Fmt.Rough(76 * 60)}");
Type paceT = typeof(Rng).Assembly.GetType("NocatFarm.Modules.CardPace")!;
string who = "harness-" + Guid.NewGuid().ToString("N")[..6];
object pace = Activator.CreateInstance(paceT, [who])!;
var queue = new List<NocatFarm.Modules.FarmTarget> {
	new() { AppId = 1, CardsRemaining = 5, HoursPlayed = 9 },
	new() { AppId = 2, CardsRemaining = 3, HoursPlayed = 5 },
	new() { AppId = 3, CardsRemaining = 2, HoursPlayed = 1.2f },
};
int Est() => (int) paceT.GetMethod("EstimateMinutes")!.Invoke(pace, [queue, 2f])!;
Check("fresh account: Steam's 30m a card + playtime still to build", Est() == (10 * 30) + 48, $"{Est()}");
paceT.GetMethod("Dropped")!.Invoke(pace, [1u, TimeSpan.FromMinutes(60), 1]);
Check("a timed drop teaches that game's pace, and the account's", Est() == (5 * 60) + (5 * 60) + 48, $"{Est()}");
paceT.GetMethod("Dropped")!.Invoke(pace, [2u, TimeSpan.FromMinutes(2), 1]);
Check("an impossible 2-minute drop is ignored", Est() == (5 * 60) + (5 * 60) + 48, $"{Est()}");
object reread = Activator.CreateInstance(paceT, [who])!;
Check("the account pace survives a restart", (double) paceT.GetProperty("Minutes")!.GetValue(reread)! == 60 && (bool) paceT.GetProperty("Learned")!.GetValue(reread)!);

// ── Fair swap: our own rule ──────────────────────────────────────────────────
Type fs = typeof(Rng).Assembly.GetType("NocatFarm.Modules.FairSwap")!;
Type cardT = fs.GetNestedType("Card", BindingFlags.NonPublic)!;
object Card(uint game, ulong cls) => Activator.CreateInstance(cardT, [game, cls])!;
object CardList(params object[] cards) { var l = (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(cardT))!; foreach (var c in cards) l.Add(c); return l; }
object Holdings(params (uint g, ulong c, int n)[] h) { var d = (IDictionary) Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(cardT, typeof(int)))!; foreach (var (g, c, n) in h) d[Card(g, c)] = n; return d; }
bool Fair(object give, object get, object ours) { var r = fs.GetMethod("Judge", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [give, get, ours])!; return (bool) r.GetType().GetField("Item1")!.GetValue(r)!; }
Check("swap: 3 of A, 0 of B -> give A for B is fair", Fair(CardList(Card(1, 10)), CardList(Card(1, 20)), Holdings((1, 10, 3))));
Check("swap: two full sets (A2 B2) -> A for B is refused", !Fair(CardList(Card(1, 10)), CardList(Card(1, 20)), Holdings((1, 10, 2), (1, 20, 2))));
Check("swap: giving the last copy is refused", !Fair(CardList(Card(1, 10)), CardList(Card(1, 20)), Holdings((1, 10, 1))));
Check("swap: 2 for 1 is refused", !Fair(CardList(Card(1, 10), Card(1, 11)), CardList(Card(1, 20)), Holdings((1, 10, 5), (1, 11, 5))));
Check("swap: one-for-one per game across two games is fair", Fair(CardList(Card(1, 10), Card(2, 30)), CardList(Card(1, 20), Card(2, 40)), Holdings((1, 10, 3), (2, 30, 2))));
Check("swap: asking for a card we don't hold is refused", !Fair(CardList(Card(1, 99)), CardList(Card(1, 20)), Holdings((1, 10, 3))));
Check("swap: 3,1 -> 2,2 evens out and is fair", Fair(CardList(Card(1, 10)), CardList(Card(1, 20)), Holdings((1, 10, 3), (1, 20, 1))));

// ── Booster page reader ──────────────────────────────────────────────────────
Type boost = typeof(Rng).Assembly.GetType("NocatFarm.Modules.Boosters")!;
object? Parse(string html) => boost.GetMethod("ParsePage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [html]);
string page = "<script>\r\n\tCBoosterCreatorPage.Init(\r\n\t\t[{\"appid\":730,\"name\":\"Counter-Strike (2), \\\"Global\\\"\",\"series\":1,\"price\":\"400\",\"unavailable\":false},{\"appid\":440,\"name\":\"Team Fortress 2\",\"series\":1,\"price\":\"520\",\"unavailable\":true,\"available_at_time\":\"Sep 28 @ 3:45pm\"}],\r\n\t\tparseFloat( \"1234\" ),\r\n\t\tparseFloat( \"1000\" ),\r\n\t\tparseFloat( \"234\" )\r\n\t);\r\n</script>";
object? parsed = Parse(page);
uint Prop(object o, string n) => (uint) o.GetType().GetProperty(n)!.GetValue(o)!;
Check("booster page: gems read", parsed != null && Prop(parsed, "Gems") == 1234 && Prop(parsed, "TradableGems") == 1000 && Prop(parsed, "UntradableGems") == 234);
var offers = parsed == null ? null : (IDictionary) parsed.GetType().GetProperty("Offers")!.GetValue(parsed)!;
Check("booster page: both games, name with commas and brackets intact", offers is { Count: 2 }
	&& (string) offers[730u]!.GetType().GetProperty("Name")!.GetValue(offers[730u])! == "Counter-Strike (2), \"Global\""
	&& (bool) offers[440u]!.GetType().GetProperty("Unavailable")!.GetValue(offers[440u])!);
Check("booster page: not the page -> null", Parse("<html>An error was encountered while processing your request</html>") == null);

// ── Free games from Steam's own store search (live) ──────────────────────────
Type fg = typeof(Rng).Assembly.GetType("NocatFarm.Modules.FreeGames")!;
var giveaways = await (Task<List<string>>) fg.GetMethod("StoreGiveawaysAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [CancellationToken.None])!;
Check("store search: reads giveaways (right now: the free Supporter Pack DLC)", giveaways.Count > 0 && giveaways.All(t => t.StartsWith("g/") || t.StartsWith("s/")), string.Join(", ", giveaways));
uint steepFree = await (Task<uint>) fg.GetMethod("FreeSubAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [460920u, CancellationToken.None])!;
Check("store: a normal paid game has no free package", steepFree == 0, $"{steepFree}");

// ── Hour targets ─────────────────────────────────────────────────────────────
{
	var ht = NocatFarm.Modules.HourTargets.Parse("730:100@2026-12-01, 440:50; junk, 0:5, 570:x, 730:9, 620:40@nope");
	Check("targets: parse, skip junk + dupes, bad date = no date", ht.Count == 3 && ht[0] == new NocatFarm.Modules.HourTargets.Target(730, 100, new DateTime(2026, 12, 2))
		&& ht[1] == new NocatFarm.Modules.HourTargets.Target(440, 50, null) && ht[2].AppId == 620 && ht[2].By == null, string.Join(" | ", ht));
	var now = new DateTime(2026, 11, 2);
	var p1 = NocatFarm.Modules.HourTargets.Of(ht[0], 70 * 60, now);
	Check("targets: 30h left over 30 days = 60 min a day", p1.MinutesLeft == 1800 && Math.Abs(p1.DailyMinutesNeeded - 60) < 0.01 && !p1.Done && !p1.Late, $"{p1}");
	var p2 = NocatFarm.Modules.HourTargets.Of(ht[0], 101 * 60, now);
	Check("targets: past the hours = done", p2.Done && p2.MinutesLeft == 0);
	var p3 = NocatFarm.Modules.HourTargets.Of(ht[0], 60, new DateTime(2026, 12, 3));
	Check("targets: after the date and short = late", p3.Late && !p3.Done);
	Check("targets: no date = no daily pace", NocatFarm.Modules.HourTargets.Of(ht[1], 0, now).DailyMinutesNeeded == 0);
}

// ── Level XP ─────────────────────────────────────────────────────────────────
Check("xp: level 10 = 1000, 11 = 1200, 70 = 28000, 71 = 28800, 72 = 29600",
	NocatFarm.Core.LevelPlanner.XpFor(10) == 1000 && NocatFarm.Core.LevelPlanner.XpFor(11) == 1200 && NocatFarm.Core.LevelPlanner.XpFor(70) == 28000
	&& NocatFarm.Core.LevelPlanner.XpFor(71) == 28800 && NocatFarm.Core.LevelPlanner.XpFor(72) == 29600);

// ── Market fees ──────────────────────────────────────────────────────────────
Check("fees: 100 received costs the buyer 115; 1 costs 3", NocatFarm.Core.Seller.FeesFor(100) == 15 && NocatFarm.Core.Seller.FeesFor(1) == 2);
Check("fees: buyer 3 -> 1, 10 -> 8, 115 -> 100, 2 -> 0", NocatFarm.Core.Seller.ReceiveFor(3) == 1 && NocatFarm.Core.Seller.ReceiveFor(10) == 8
	&& NocatFarm.Core.Seller.ReceiveFor(115) == 100 && NocatFarm.Core.Seller.ReceiveFor(2) == 0);
Check("undercut: a cent under, never below 3", NocatFarm.Core.Seller.UndercutFor(10) == 9 && NocatFarm.Core.Seller.UndercutFor(4) == 3 && NocatFarm.Core.Seller.UndercutFor(3) == 3);

// ── Own-fleet matching ───────────────────────────────────────────────────────
{
	var ba = new NocatFarm.Core.Bot("ma", new NocatFarm.Config.BotConfig());
	var bb = new NocatFarm.Core.Bot("mb", new NocatFarm.Config.BotConfig());
	var bc = new NocatFarm.Core.Bot("mc", new NocatFarm.Config.BotConfig());
	ulong nextAsset = 1000;
	List<NocatFarm.Core.Looting.Item> Cards(params (uint game, ulong cls, int n, bool tradable)[] h) {
		List<NocatFarm.Core.Looting.Item> l = [];
		foreach (var (g, c, n, t) in h) for (int i = 0; i < n; i++) l.Add(new NocatFarm.Core.Looting.Item(nextAsset++, c, 0, 1, "G Trading Card", "Card " + c, 753, 6, g, t));
		return l;
	}
	List<NocatFarm.Core.Matching.Swap> Plan(Dictionary<NocatFarm.Core.Bot, List<NocatFarm.Core.Looting.Item>> inv, HashSet<ulong>? busy = null) => NocatFarm.Core.Matching.Plan(inv, busy);

	var s1 = Plan(new() { [ba] = Cards((1, 10, 3, true)), [bb] = Cards((1, 20, 3, true)) });
	Check("match: 3 A vs 3 B -> one card each way, then even", s1.Count == 1 && s1[0].Give.Count == 1 && s1[0].Give[0].Item.ClassId == 10 && s1[0].Take[0].Item.ClassId == 20, $"{s1.Count} {s1.FirstOrDefault()?.Give.Count}");

	var inv2 = new Dictionary<NocatFarm.Core.Bot, List<NocatFarm.Core.Looting.Item>> { [ba] = Cards((1, 10, 3, true)), [bb] = Cards((1, 20, 3, true)) };
	var s2 = Plan(inv2, [.. inv2[ba].Select(static i => i.AssetId)]);
	Check("match: cards already on an offer are never planned", s2.Count == 0);

	var inv3 = new Dictionary<NocatFarm.Core.Bot, List<NocatFarm.Core.Looting.Item>> { [ba] = Cards((1, 10, 4, true)), [bb] = Cards((1, 20, 3, true)), [bc] = Cards((1, 30, 3, true)) };
	var p3 = NocatFarm.Core.Matching.PlanAll(inv3);
	var used = p3.Swaps.SelectMany(static s => s.Give.Concat(s.Take)).Select(static m => m.Item.AssetId).ToList();
	Check("match: each account in at most one swap per run, the rest deferred", p3.Swaps.Count == 1 && p3.Deferred >= 1 && used.Count == used.Distinct().Count(), $"{p3.Swaps.Count} swaps, {p3.Deferred} deferred");
	var p3b = NocatFarm.Core.Matching.PlanAll(inv3, null, new HashSet<(NocatFarm.Core.Bot, NocatFarm.Core.Bot)> { (ba, bb), (bb, ba) });
	Check("match: a pair with an offer already out is skipped", p3b.Swaps.All(s => !((s.From == ba && s.To == bb) || (s.From == bb && s.To == ba))) && p3b.Swaps.Count == 1, $"{p3b.Swaps.Count}");
	var s7 = Plan(new() { [ba] = Cards((1, 10, 3, true), (2, 40, 3, true)), [bb] = Cards((1, 20, 3, true), (2, 50, 3, true)) });
	Check("match: two games in one swap, each judged on its own", s7.Count == 1 && s7[0].Give.Select(m => m.Item.Game).Distinct().Count() == 2, $"{s7.FirstOrDefault()?.Give.Count}");

	var s4 = Plan(new() { [ba] = Cards((1, 10, 3, true)), [bb] = Cards((1, 20, 3, true), (1, 10, 2, false)) });
	Check("match: untradable copies count as held (B's 2 untradable A block the swap)", s4.Count == 0);

	var s5 = Plan(new() { [ba] = Cards((1, 10, 3, true)), [bb] = Cards((2, 20, 3, true)) });
	Check("match: never across games", s5.Count == 0);

	var s6 = Plan(new() { [ba] = Cards((1, 10, 6, true), (1, 11, 5, true)), [bb] = Cards((1, 20, 6, true), (1, 21, 5, true)) });
	bool bothFair = s6.All(s => Fair(CardList([.. s.Give.Select(m => Card(m.Item.Game, m.Item.ClassId))]), CardList([.. s.Take.Select(m => Card(m.Item.Game, m.Item.ClassId))]), Holdings((1, 10, 6), (1, 11, 5)))
		&& Fair(CardList([.. s.Take.Select(m => Card(m.Item.Game, m.Item.ClassId))]), CardList([.. s.Give.Select(m => Card(m.Item.Game, m.Item.ClassId))]), Holdings((1, 20, 6), (1, 21, 5))));
	Check("match: a many-card offer passes both sides' own fair-swap check", s6.Count == 1 && s6[0].Give.Count > 1 && bothFair, $"{s6.FirstOrDefault()?.Give.Count} fair={bothFair}");
}

// ── Trade offers from Steam's API ────────────────────────────────────────────
{
	Type to = typeof(Rng).Assembly.GetType("NocatFarm.Core.TradeOffers")!;
	string json = """{"response":{"trade_offers_received":[{"tradeofferid":"555","accountid_other":39734273,"trade_offer_state":2,"is_our_offer":false,"escrow_end_date":0,"items_to_give":[{"appid":753,"contextid":"6","assetid":"77","classid":"10","instanceid":"0","amount":"1"}],"items_to_receive":[{"appid":753,"contextid":"6","assetid":"88","classid":"20","instanceid":"0","amount":"1"}]},{"tradeofferid":"556","accountid_other":1,"trade_offer_state":2,"is_our_offer":false,"escrow_end_date":1790000000,"items_to_receive":[{"appid":730,"contextid":"2","assetid":"9","classid":"99","instanceid":"0","amount":"1"}]}],"descriptions":[{"classid":"10","instanceid":"0","type":"Game Trading Card","name":"Alpha","market_fee_app":"440"},{"classid":"20","instanceid":"0","type":"Game Trading Card","name":"Beta","market_fee_app":"440"}]}}""";
	using var doc = System.Text.Json.JsonDocument.Parse(json);
	var resp = doc.RootElement.GetProperty("response");
	var descs = to.GetMethod("Descriptions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [resp])!;
	var read = to.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!;
	var list = resp.GetProperty("trade_offers_received").EnumerateArray().Select(e => (NocatFarm.Core.TradeOffers.Offer?) read.Invoke(null, [e, descs])).ToList();
	var o1 = list[0]!; var o2 = list[1]!;
	Check("offers: partner comes from the account id (vanity URLs don't matter)", o1.Partner == 76561198000000001UL, $"{o1.Partner}");
	Check("offers: both sides, with each card's game", o1.Giving.Count == 1 && o1.Receiving.Count == 1 && o1.Giving[0].Game == 440 && o1.Receiving[0].Described && o1.Giving[0].AssetId == 77);
	Check("offers: a trade hold is seen; an undescribed item is marked unknown", o2.HoldUntil != null && o2.IsPureDonation && !o2.Receiving[0].Described);
	var cards = (ValueTuple<List<object>, List<object>>?) null;
	var cardsOf = fs.GetMethod("CardsOf", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [o1]);
	Check("offers: an all-card offer reads as cards for the swap check", cardsOf != null);
}

// ── drops on a human-mode account: goes first in the sittings, never a grind ─────────────────────────────
{
	var cfg = new NocatFarm.Config.BotConfig { LegitMode = true, FarmCards = false, FarmCardsWhen = NocatFarm.Modules.FarmWhen.Night };
	var bot = new NocatFarm.Core.Bot("droptest", cfg);
	bot.StopDropsFirst();   // a leftover state file from an earlier run
	Check("drops: nothing running by default", !bot.DropsFirstActive && bot.EffectiveFarmWhen == NocatFarm.Modules.FarmWhen.Night && !bot.EffectiveFarmCards);
	Check("drops: starts", bot.StartDropsFirst(460920, 2) && bot.DropsFirstActive && bot.DropsFirstApp == 460920 && bot.DropsFirstWant == 2);
	Check("drops: an account set to night-only (or off) farms in some sittings while it runs",
		bot.EffectiveFarmWhen == NocatFarm.Modules.FarmWhen.Mixed && bot.EffectiveFarmCards);
	Check("drops: it is NOT a grind", !bot.Grinding);
	bot.CountDropsFirst(730, 1);
	Check("drops: a card from another game doesn't count", bot.DropsFirstGot == 0);
	bot.CountDropsFirst(460920, 1);
	Check("drops: a card from the game counts", bot.DropsFirstGot == 1 && bot.DropsFirstActive);
	bot.CountDropsFirst(460920, 5);
	Check("drops: ends once they're all in, and hands back the account's own schedule",
		!bot.DropsFirstActive && bot.DropsFirstApp == 0 && bot.EffectiveFarmWhen == NocatFarm.Modules.FarmWhen.Night && !bot.EffectiveFarmCards);

	cfg.FarmCardsWhen = NocatFarm.Modules.FarmWhen.Day;
	bot.StartDropsFirst(460920, 1);
	Check("drops: an account farming in every sitting keeps doing that", bot.EffectiveFarmWhen == NocatFarm.Modules.FarmWhen.Day);
	bot.StopDropsFirst();
	Check("drops: off stops it", !bot.DropsFirstActive);
}

// ── ban watch: reading Steam's answers ────────────────────────────────────────────────────────────────────
{
	// The shapes below are cut from real profiles (old: 1 VAC + 1 game ban; kylro: 1 game ban; new: clean).
	const string oldHtml = """<div class="profile_header"></div><div class="profile_ban_status"> <div class="profile_ban">1 VAC ban on record<span class="profile_ban_info"> | <a href="https://help.steampowered.com/faqs/view/647C-5CC1-7EA9-3C29">Info</a></span></div> <div class="profile_ban">1 game ban on record<span class="profile_ban_info"> | <a href="#">Info</a></span></div> 1759 day(s) since last ban</div>""";
	const string kylroHtml = """<div class="profile_header"></div><div class="profile_ban_status"><div class="profile_ban">1 game ban on record<span class="profile_ban_info"> | <a href="#">Info</a></span></div>3138 day(s) since last ban</div>""";
	const string cleanHtml = """<div class="profile_header"></div><div class="commentthread_comment_text">lol he got community banned</div>""";
	const string multiHtml = """<div class="profile_header"></div><div class="profile_ban_status"><div class="profile_ban">Multiple VAC bans on record</div>Currently trade banned 12 day(s) since last ban</div>""";
	const string noticeHtml = """<div class="error_ctn"><h3>This account has been community banned.</h3></div>""";
	var bw = NocatFarm.Modules.BanWatch.ParseProfile(oldHtml);
	Check("bans: old - 1 VAC, 1 game ban, 1759 days", bw is { Vac: 1, Game: 1, Community: false, Economy: "none", DaysSinceLast: 1759 }, $"{bw}");
	var bk = NocatFarm.Modules.BanWatch.ParseProfile(kylroHtml);
	Check("bans: kylro - 1 game ban, no VAC", bk is { Vac: 0, Game: 1, DaysSinceLast: 3138 }, $"{bk}");
	var bc = NocatFarm.Modules.BanWatch.ParseProfile(cleanHtml);
	Check("bans: clean - and a comment saying 'community banned' is not a ban", bc is { Any: false, Community: false }, $"{bc}");
	var bm = NocatFarm.Modules.BanWatch.ParseProfile(multiHtml);
	Check("bans: 'Multiple' counts as at least 2; a trade ban is seen", bm is { Vac: 2, TradeBanned: true }, $"{bm}");
	Check("bans: Steam's community-ban notice page", NocatFarm.Modules.BanWatch.ParseProfile(noticeHtml) is { Community: true });
	Check("bans: a login page or error says nothing", NocatFarm.Modules.BanWatch.ParseProfile("<html>Sign In</html>") == null);

	Check("bans: summary reads plainly", NocatFarm.Modules.BanWatch.Summary(bw).ToString() == "1 VAC, 1 game ban(s) · last one 1759 day(s) ago", NocatFarm.Modules.BanWatch.Summary(bw).ToString());
}

// ── confirmations: reading Steam's list ─────────────────────────────────────────────────────────────────
{
	const string list = """{"success":true,"conf":[{"type":3,"type_name":"Market Listing","id":"13011","creator_id":"4444","nonce":"99001","creation_time":1790000100,"cancel":"Cancel","accept":"List","icon":"https://img/a.png","multi":false,"headline":"Rust card","summary":["$0.12 ($0.10)"]},{"type":2,"type_name":"Trade Offer","id":"13010","creator_id":"7788990011","nonce":"99000","creation_time":1790000000,"cancel":"Cancel","accept":"Send Offer","icon":"https://avatars/x.jpg","multi":false,"headline":"kylro","summary":["You will give up your 3 items","You will receive 1 item"]}]}""";
	var (ok, err, items) = NocatFarm.Core.Confirmations.Parse(list);
	Check("confirmations: both read, oldest first", ok && items.Count == 2 && items[0].Id == 13010 && items[1].Id == 13011, $"{err} {items.Count}");
	Check("confirmations: a trade carries its offer id, type and wording", items[0] is { Type: 2, CreatorId: 7788990011, Nonce: 99000, Headline: "kylro" } && items[0].Summary.Count == 2 && items[0].TypeName == "Trade Offer");
	Check("confirmations: a market listing carries its price line", items[1] is { Type: 3 } && items[1].Summary[0].Contains("0.12"));
	Check("confirmations: an empty list is fine", NocatFarm.Core.Confirmations.Parse("""{"success":true,"conf":[]}""") is (true, _, { Count: 0 }));
	Check("confirmations: needauth is a clear failure", NocatFarm.Core.Confirmations.Parse("""{"success":false,"needauth":true}""") is (false, var e1, _) && e1.Length > 0);
	Check("confirmations: a refusal keeps Steam's message", NocatFarm.Core.Confirmations.Parse("""{"success":false,"message":"Invalid authenticator"}""") is (false, "Invalid authenticator", _));
	Check("confirmations: an HTML page is not a list", NocatFarm.Core.Confirmations.Parse("<html>login</html>") is (false, _, _));
}

// ── authenticator maths, against the published test values of steamguard-cli and ArchiSteamFarm ─────────
{
	Check("2fa: confirmation key (steamguard-cli vector)", NocatFarm.Core.MobileAuth.Confirmation("GQP46b73Ws7gr8GmZFR0sDuau5c=", 1617591917, "conf") == "NaL8EIMhfy/7vBounJ0CvpKbrPk=");
	Check("2fa: confirmation key (ASF vector, t=1337)", NocatFarm.Core.MobileAuth.Confirmation("qrg+wW8/u/TDt2i/+FQuPhuVrmY=", 1337, "conf") == "mYbCKs8ZvsVN2odCMxpvidrIu1c=");
	Check("2fa: confirmation key (ASF vector, t=1723332288)", NocatFarm.Core.MobileAuth.Confirmation("qrg+wW8/u/TDt2i/+FQuPhuVrmY=", 1723332288, "conf") == "hpZUxyNgwBvtKPROvedjuvVPQiE=");
	string? c1 = NocatFarm.Core.MobileAuth.GenerateCode("KDHC3rsY8+CmiswnXJcE5e5dRfd=", 1337), c2 = NocatFarm.Core.MobileAuth.GenerateCode("KDHC3rsY8+CmiswnXJcE5e5dRfd=", 1723332288);
	Check("2fa: sign-in code (ASF vectors)", c1 == "47J4D" && c2 == "JQ3HQ", $"{c1} {c2}");
}

// ── which games an account is banned in (Steam's help site, the account's own view) ──────────────────────
{
	const string vacPage = """
<div class="help_issue_details"><div class="vac_ban_header">Bans applied by VAC or Valve Anti-Cheat</div>
<div class="refund_info_box"><div><span class="help_highlight_text">Counter-Strike 2</span></div><a href="https://help.steampowered.com/en/wizard/HelpWithGame/?appid=730">Help</a></div></div>
<div class="help_issue_details"><div class="vac_ban_header">Bans applied by the Game Developer</div>
<div class="refund_info_box"><div><span class="help_highlight_text">Counter-Strike 2</span></div><a href="https://help.steampowered.com/en/wizard/HelpWithGame/?appid=730&amp;issueid=131">Help</a></div>
<div class="refund_info_box"><div><span class="help_highlight_text">Some Game</span></div><a href="https://help.steampowered.com/en/wizard/HelpWithGame/?steamAppId=1234">Help</a></div>
<div class="refund_info_box"><div><span class="help_highlight_text">A BattlEye game</span></div><a href="https://www.battleye.com/support/">Visit BattlEye Support</a></div></div>
""";
	var bg = NocatFarm.Modules.BanGames.Parse(vacPage);
	Check("ban games: each banned game once (VAC and game ban on CS2 = one), BattlEye skipped", bg != null && bg.SequenceEqual([730u, 1234u]), string.Join(",", bg ?? []));
	Check("ban games: a clean account", NocatFarm.Modules.BanGames.Parse("""<div class="no_vac_bans_header">You have no bans</div>""") is { Count: 0 });
	Check("ban games: a login page says nothing", NocatFarm.Modules.BanGames.Parse("<html>Sign in</html>") == null);
}

// Optional: NOCAT_BANPAGES=<folder of saved prof_<steamid>.html pages> parses whole real profile pages too.
if (Environment.GetEnvironmentVariable("NOCAT_BANPAGES") is { Length: > 0 } banPages && Directory.Exists(banPages)) {
	foreach (string f in Directory.GetFiles(banPages, "prof_*.html")) {
		Console.WriteLine($"      real page {Path.GetFileName(f)}: {NocatFarm.Modules.BanWatch.ParseProfile(File.ReadAllText(f))}");
	}
}

// ── trades: who it trades with by itself, and how offers are described ───────────────────────────────────
{
	const ulong Kylro = 76561198000000001UL, Old = 76561198000000002UL, Stranger = 76561198000000123UL;
	NocatFarm.Modules.Trading Trades(string autoWith, bool fromMasters = false, string masters = "") =>
		new(new NocatFarm.Core.Bot("tradetest", new NocatFarm.Config.BotConfig { AutoTradeWith = autoWith, AcceptFromMasters = fromMasters, TradeMasters = masters }));
	NocatFarm.Core.TradeOffers.Item TCard(string name, uint n = 1) => new(753, "6", 1, 1, 0, n, "Trading Card", name, 440, true);
	NocatFarm.Core.TradeOffers.Offer Offer(ulong partner, List<NocatFarm.Core.TradeOffers.Item> give, List<NocatFarm.Core.TradeOffers.Item> get) => new(1, partner, NocatFarm.Core.TradeOffers.Active, false, null, give, get);
	var autoM = typeof(NocatFarm.Modules.Trading).GetMethod("Auto", BindingFlags.NonPublic | BindingFlags.Static)!;
	bool AutoT(NocatFarm.Modules.Trading t, NocatFarm.Core.TradeOffers.Offer o) => (bool) autoM.Invoke(null, [o, t.Allowed()])!;

	var legacy = Trades("", fromMasters: true, masters: $"{Kylro}, {Old}");
	Check("trades: empty list = your own accounts, both ways, when 'accept anything from your own accounts' is on",
		legacy.Allowed().Count == 2 && legacy.Allowed()[Kylro] == NocatFarm.Modules.Trading.Way.Both);
	Check("trades: ...and nobody at all when that setting is off", Trades("", fromMasters: false, masters: $"{Kylro}").Allowed().Count == 0);

	var ways = Trades($"{Kylro}:to, {Old}:from");
	Check("trades: ':to' lets them take items", AutoT(ways, Offer(Kylro, [TCard("A")], [])));
	Check("trades: ':from' accepts what they send...", AutoT(ways, Offer(Old, [], [TCard("A")])));
	Check("trades: ...but never lets them take anything", !AutoT(ways, Offer(Old, [TCard("A")], [TCard("B")])));
	Check("trades: someone not on the list waits for you", !AutoT(ways, Offer(Stranger, [], [TCard("A")])));
	Check("trades: no suffix means both ways", Trades($"{Kylro}").Allowed()[Kylro] == NocatFarm.Modules.Trading.Way.Both);

	Check("trades: items read plainly", NocatFarm.Modules.Trading.Items([TCard("Alpha", 2), TCard("Beta")]).ToString() == "3 item(s): Alpha x2, Beta", NocatFarm.Modules.Trading.Items([TCard("Alpha", 2), TCard("Beta")]));
	Check("trades: nothing is 'no items'", NocatFarm.Modules.Trading.Items([]).ToString() == "no items");
	// Short numbers: a number is never handed to a second offer, so a stale 'trade accept new 1' finds nothing.
	var numbering = Trades("");
	var keepM = typeof(NocatFarm.Modules.Trading).GetMethod("KeepNumbers", BindingFlags.NonPublic | BindingFlags.Instance)!;
	NocatFarm.Core.TradeOffers.Offer Live(ulong id) => new(id, Stranger, NocatFarm.Core.TradeOffers.Active, false, null, [], [TCard("A")]);
	void KeepLive(params ulong[] ids) => keepM.Invoke(numbering, [ids.Select(Live).ToList()]);
	KeepLive(9001);
	int first = numbering.NumberOf(9001);
	KeepLive();                 // it went - accepted in the Steam client, say
	KeepLive(9002);             // and a new one arrived
	int second = numbering.NumberOf(9002);
	Check("trades: the first offer is number 1", first == 1, $"{first}");
	Check("trades: a number is never reused after the list empties", second == 2, $"{second}");
	KeepLive(9004, 9003);
	Check("trades: live offers are numbered oldest first, carrying on from the last", numbering.NumberOf(9003) == 3 && numbering.NumberOf(9004) == 4,
		$"{numbering.NumberOf(9003)}, {numbering.NumberOf(9004)}");

	Check("trades: a long list is cut short",NocatFarm.Modules.Trading.Items([TCard("A"), TCard("B"), TCard("C"), TCard("D"), TCard("E")]).ToString() == "5 item(s): A, B, C and 2 more", NocatFarm.Modules.Trading.Items([TCard("A"), TCard("B"), TCard("C"), TCard("D"), TCard("E")]));
}

// ── market prices: which separator is the decimal point ───────────────────────────────────────────────────
{
	var moneyM = typeof(NocatFarm.PriceBook).GetMethod("Money", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)])!;
	decimal? Money(string text) => (decimal?) moneyM.Invoke(null, [text]);

	Check("money: $1,234.56", Money("$1,234.56") == 1234.56m, $"{Money("$1,234.56")}");
	Check("money: 1.234,56€", Money("1.234,56€") == 1234.56m, $"{Money("1.234,56€")}");
	Check("money: ¥ 1,234 has no cents - a thousands separator, not 1.234", Money("¥ 1,234") == 1234m, $"{Money("¥ 1,234")}");
	Check("money: 1.234 ₫ likewise", Money("1.234 ₫") == 1234m, $"{Money("1.234 ₫")}");
	Check("money: 0,03€", Money("0,03€") == 0.03m, $"{Money("0,03€")}");
	Check("money: $0.5 (one digit after)", Money("$0.5") == 0.5m, $"{Money("$0.5")}");
	Check("money: 12 345,67 руб.", Money("12 345,67 руб.") == 12345.67m, $"{Money("12 345,67 руб.")}");
	Check("money: 1,234,567 ₩", Money("1,234,567 ₩") == 1234567m, $"{Money("1,234,567 ₩")}");
	Check("money: plain 5", Money("5") == 5m);
	Check("money: nothing is null", Money("") == null && Money("--") == null);
}

// ── commands: aliases reach the right command, nothing claims a word twice ─────────────────────────────────
{
	string? Canon(string w) => NocatFarm.Commands.Resolve(w)?.Name;
	Check("commands: 'delete' is remove (so the confirm guards catch it)", Canon("delete") == "remove");
	Check("commands: 'quit' and 'q' are exit", Canon("quit") == "exit" && Canon("q") == "exit");
	Check("commands: 'wakeup' and 'skipsleep' are wake", Canon("wakeup") == "wake" && Canon("skipsleep") == "wake");
	Check("commands: 'bots' is status", Canon("bots") == "status");
	Check("commands: 'boost' and 'key' no longer reach hunt/redeem", Canon("boost") == null && Canon("key") == null);
	Check("commands: farm, transfer and report are gone", Canon("farm") == null && Canon("transfer") == null && Canon("report") == null);

	List<string> words = [.. NocatFarm.Commands.All.SelectMany(static c => c.Aliases.Split('|', StringSplitOptions.RemoveEmptyEntries).Prepend(c.Name)).Select(static w => w.ToLowerInvariant())];
	List<string> twice = [.. words.GroupBy(static w => w).Where(static g => g.Count() > 1).Select(static g => g.Key)];
	Check("commands: no word reaches two commands", twice.Count == 0, string.Join(", ", twice));

	Check("log: a secret setting's value is masked", NocatFarm.Commands.ForLog("set new SteamPassword hunter2") == "set new SteamPassword ***");
	Check("log: a global secret too", NocatFarm.Commands.ForLog("/set WebPassword abc def") == "/set WebPassword ***");
	Check("log: 'answer' is masked", NocatFarm.Commands.ForLog("answer hunter2") == "answer ***");
	Check("log: an ordinary setting is left alone", NocatFarm.Commands.ForLog("set new FarmCards on") == "set new FarmCards on");
	Check("log: other commands are left alone", NocatFarm.Commands.ForLog("status new") == "status new");
}

// ── reconnects: the wait grows, and stops at five minutes ─────────────────────────────────────────────────
{
	var grownM = typeof(NocatFarm.Core.SteamMaintenance).GetMethod("Grown", BindingFlags.NonPublic | BindingFlags.Static)!;
	TimeSpan Grown(int attempt, int firstSecs) => (TimeSpan) grownM.Invoke(null, [attempt, TimeSpan.FromSeconds(firstSecs)])!;

	Check("backoff: the first try waits the ordinary wait", Grown(1, 15) == TimeSpan.FromSeconds(15));
	Check("backoff: doubles each failure", Grown(2, 15) == TimeSpan.FromSeconds(30) && Grown(4, 15) == TimeSpan.FromSeconds(120));
	Check("backoff: never past five minutes", Grown(9, 15) == TimeSpan.FromMinutes(5) && Grown(500, 15) == TimeSpan.FromMinutes(5));
}

// ── self-update: the restarted copy gets the same arguments ───────────────────────────────────────────────
{
	var quoteM = typeof(NocatFarm.Core.SelfUpdate).GetMethod("QuoteArg", BindingFlags.NonPublic | BindingFlags.Static)!;
	string Q(string a) => (string) quoteM.Invoke(null, [a])!;

	Check("relaunch: a plain flag stays as it is", Q("--no-gui") == "--no-gui");
	Check("relaunch: a path with spaces is quoted", Q(@"C:\My Files\data") == "\"C:\\My Files\\data\"", Q(@"C:\My Files\data"));
	Check("relaunch: a trailing backslash can't eat the closing quote", Q(@"C:\My Files\") == "\"C:\\My Files\\\\\"", Q(@"C:\My Files\"));
	Check("relaunch: an empty argument survives", Q("") == "\"\"");
}

// ── Discord bot: the / menu, the replies, who may run what ────────────────────────────────────────────────
{
	Type notifier = typeof(NocatFarm.Core.Notifier);
	const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
	object? Invoke(string name, params object?[] args) => notifier.GetMethod(name, Any)!.Invoke(null, args);

	foreach (bool global in new[] { true, false }) {
		string where = global ? "global" : "server";
		string json = System.Text.Json.JsonSerializer.Serialize(Invoke("DiscordCommandSet", global));
		using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
		List<System.Text.Json.JsonElement> cmds = [.. doc.RootElement.EnumerateArray()];
		List<string> names = [.. cmds.Select(static c => c.GetProperty("name").GetString()!)];

		Check($"discord {where}: every name is 1-32 lowercase letters, digits, - or _",
			names.All(static n => System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z0-9_-]{1,32}$")), string.Join(", ", names));
		Check($"discord {where}: no name twice", names.Distinct().Count() == names.Count);
		Check($"discord {where}: Telegram's menu plus nocat and connect",
			new[] { "status", "dashboard", "cards", "human", "offers", "confirmations", "2fa", "stats", "update", "help", "nocat", "connect" }.All(names.Contains));
		Check($"discord {where}: descriptions 1-100 characters, options too", cmds.All(static c =>
			c.GetProperty("description").GetString()!.Length is > 0 and <= 100
			&& c.GetProperty("options").EnumerateArray().All(static o => o.GetProperty("description").GetString()!.Length is > 0 and <= 100
				&& System.Text.RegularExpressions.Regex.IsMatch(o.GetProperty("name").GetString()!, "^[a-z0-9_-]{1,32}$"))));
		Check($"discord {where}: nocat needs a command, connect needs a code", cmds.Any(static c => c.GetProperty("name").GetString() == "nocat"
				&& c.GetProperty("options")[0].GetProperty("name").GetString() == "command" && c.GetProperty("options")[0].GetProperty("required").GetBoolean())
			&& cmds.Any(static c => c.GetProperty("name").GetString() == "connect" && c.GetProperty("options")[0].GetProperty("required").GetBoolean()));
		Check($"discord {where}: the everyday ones take optional args", cmds.Where(static c => c.GetProperty("name").GetString() is not ("nocat" or "connect"))
			.All(static c => c.GetProperty("options")[0].GetProperty("name").GetString() == "args" && !c.GetProperty("options")[0].GetProperty("required").GetBoolean()));

		if (global) {
			Check("discord global: contexts for private chats and integration type, on every command", cmds.All(static c =>
				c.TryGetProperty("contexts", out var ctx) && ctx.EnumerateArray().Select(static x => x.GetInt32()).Contains(1)
				&& c.TryGetProperty("integration_types", out var it) && it.EnumerateArray().Select(static x => x.GetInt32()).SequenceEqual([0])));
		} else {
			Check("discord server: no contexts (servers get their own copy, so nothing shows twice)", cmds.All(static c => !c.TryGetProperty("contexts", out _)));
		}
	}

	List<string> Blocks(string text) => (List<string>) Invoke("DiscordBlocks", text)!;
	string big = string.Join("\n", Enumerable.Range(1, 900).Select(static i => $"line {i} " + new string('x', i % 70)));
	List<string> parts = Blocks(big);
	Check("discord replies: long output is split, every message within 2000", parts.Count > 1 && parts.All(static p => p.Length <= 2000), $"{parts.Count} parts, longest {parts.Max(static p => p.Length)}");
	Check("discord replies: each part is a whole code block", parts.All(static p => p.StartsWith("```text\n", StringComparison.Ordinal) && p.EndsWith("\n```", StringComparison.Ordinal)));
	Check("discord replies: nothing lost", string.Join("\n", parts.Select(static p => p[8..^4])) == big);
	List<string> oneLong = Blocks(new string('y', 5000));
	Check("discord replies: one huge line still fits", oneLong.All(static p => p.Length <= 2000));
	List<string> fence = Blocks("a ``` b");
	Check("discord replies: ``` in the output can't close the block early", fence.Count == 1 && fence[0].IndexOf("```", 8, StringComparison.Ordinal) == fence[0].Length - 3);

	string Gate(string cmd, string user, string owner) => Invoke("DiscordGate", cmd, user, owner)!.ToString()!;
	Check("discord gate: no owner yet - only /connect", Gate("status", "111", "") == "NotConnected" && Gate("connect", "111", "") == "Connect");
	Check("discord gate: the owner runs commands", Gate("status", "111", "111") == "Run" && Gate("nocat", "111", "111") == "Run");
	Check("discord gate: anyone else is refused", Gate("status", "222", "111") == "NotAllowed" && Gate("nocat", "", "111") == "NotAllowed");
	Check("discord gate: /connect is open to anyone (the code guards it)", Gate("connect", "222", "111") == "Connect");

	string code = (string) Invoke("NewDiscordCode")!;
	bool Use(string typed) => (bool) Invoke("UseDiscordCode", typed)!;
	Check("discord code: 8 characters, easy to read", code.Length == 8 && code.All(static c => "ABCDEFGHJKMNPQRSTUVWXYZ23456789".Contains(c)), code);
	Check("discord code: a wrong one fails", !Use("WRONG123"));
	Check("discord code: the right one works, typed in lower case", Use(code.ToLowerInvariant()));
	Check("discord code: and only once", !Use(code));
	string code2 = (string) Invoke("NewDiscordCode")!;
	for (int i = 0; i < 5; i++) {
		Use("NOPE" + i);
	}
	Check("discord code: five misses throw it away", !Use(code2));

	(string? Needs, string Args) Guard(string first, string rest) => ((string?, string)) Invoke("ConfirmGuard", first, rest)!;
	Check("confirm: remove without confirm is held", Guard("remove", "farm1") == ("remove", "farm1"));
	Check("confirm: 'delete' is held too (it's remove)", Guard("delete", "farm1").Needs == "remove");
	Check("confirm: exit and quit are held", Guard("exit", "").Needs == "exit" && Guard("quit", "").Needs == "exit");
	Check("confirm: with confirm it runs, confirm taken off", Guard("remove", "farm1 confirm") == (null, "farm1") && Guard("exit", "confirm") == (null, ""));
	Check("confirm: everything else runs as typed", Guard("pause", "kylro 30") == (null, "kylro 30"));

	TimeSpan Retry(string body, TimeSpan? header) => (TimeSpan) Invoke("DiscordRetryAfter", body, header)!;
	Check("discord 429: waits what Discord says", Retry("{\"retry_after\": 1.5}", null) == TimeSpan.FromSeconds(1.5));
	Check("discord 429: the header when the body has none", Retry("", TimeSpan.FromSeconds(4)) == TimeSpan.FromSeconds(4));
	Check("discord 429: never silly", Retry("{\"retry_after\": 9000}", null) == TimeSpan.FromSeconds(60) && Retry("{\"retry_after\": 0}", null) == TimeSpan.FromSeconds(0.5));
}

// ── updates: skip matching, release highlights, firewall ports ──────────────────────────────────────────────
{
	Type uc = typeof(Rng).Assembly.GetType("NocatFarm.Core.UpdateCheck")!;
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	// Set the skipped version in memory only - no file is written.
	void SkipOnly(string? v) { uc.GetField("_skipped", S)!.SetValue(null, v); uc.GetField("_skipRead", S)!.SetValue(null, true); }
	bool Skipped(string tag) => (bool) uc.GetMethod("IsSkipped", S)!.Invoke(null, [tag])!;
	SkipOnly("1.4.5");
	Check("skip: the swap script's 1.4.5 matches GitHub's v1.4.5", Skipped("v1.4.5") && Skipped("1.4.5") && Skipped("V1.4.5"));
	Check("skip: a newer version isn't skipped", !Skipped("v1.4.6"));
	SkipOnly(null);
	Check("skip: nothing skipped, nothing matches", !Skipped("v1.4.5"));

	string Highlights(string body) => (string) uc.GetMethod("Highlights", S)!.Invoke(null, [body])!;
	string h = Highlights("**Discord**\r\n- A **bot** with `/status`\n- Two\n- Three\n- Four\n- Five\nnot a bullet");
	Check("highlights: first four bullets, bold and code marks gone", h == "- A bot with /status\n- Two\n- Three\n- Four", h.Replace("\n", " | "));

	Type fw = typeof(Rng).Assembly.GetType("NocatFarm.Windows.Firewall")!;
	bool Ports(string ports, int port) => (bool) fw.GetMethod("PortMatches", S)!.Invoke(null, [ports, port])!;
	Check("firewall: one port, a list, a range", Ports("7242", 7242) && Ports("80, 7242", 7242) && Ports("7000-8000", 7242));
	Check("firewall: other ports don't count", !Ports("7243", 7242) && !Ports("80,443", 7242) && !Ports("8000-9000", 7242));
}

// ── open from anywhere: UPnP against a stand-in router (the real one is never touched) ─────────────────────────
{
	Type ra = typeof(Rng).Assembly.GetType("NocatFarm.Core.RemoteAccess")!;
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	System.Net.Sockets.TcpListener fake = new(System.Net.IPAddress.Loopback, 0);
	fake.Start();
	int fport = ((System.Net.IPEndPoint) fake.LocalEndpoint).Port;
	List<string> soap = [];
	string external = "203.0.113.7";
	bool refuseLease = false;
	CancellationTokenSource stopFake = new();

	_ = Task.Run(async () => {
		while (!stopFake.IsCancellationRequested) {
			System.Net.Sockets.TcpClient client;

			try {
				client = await fake.AcceptTcpClientAsync(stopFake.Token);
			} catch {
				break;
			}

			using (client) {
				System.Net.Sockets.NetworkStream ns = client.GetStream();
				List<byte> head = [];
				byte[] one = new byte[1];

				while (!((head.Count >= 4) && (head[^4] == 13) && (head[^3] == 10) && (head[^2] == 13) && (head[^1] == 10))) {
					if (await ns.ReadAsync(one) == 0) {
						break;
					}

					head.Add(one[0]);
				}

				string headers = System.Text.Encoding.ASCII.GetString([.. head]);
				System.Text.RegularExpressions.Match lenMatch = System.Text.RegularExpressions.Regex.Match(headers, @"(?im)^Content-Length:\s*(\d+)");
				byte[] body = new byte[lenMatch.Success ? int.Parse(lenMatch.Groups[1].Value) : 0];
				int got = 0;

				while (got < body.Length) {
					int n = await ns.ReadAsync(body.AsMemory(got));

					if (n == 0) {
						break;
					}

					got += n;
				}

				string request = System.Text.Encoding.UTF8.GetString(body);
				string path = headers.Split(' ')[1];
				string status = "200 OK";
				string reply;

				if (path == "/desc.xml") {
					reply = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceList><device><deviceList><device><serviceList>"
						+ "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl</controlURL></service>"
						+ "</serviceList></device></deviceList></device></deviceList></device></root>";
				} else {
					lock (soap) {
						soap.Add(headers + request);
					}

					if (refuseLease && request.Contains("AddPortMapping") && request.Contains("<NewLeaseDuration>3600<")) {
						status = "500 Internal Server Error";
						reply = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">"
							+ "<errorCode>725</errorCode><errorDescription>OnlyPermanentLeasesSupported</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
					} else if (request.Contains("GetExternalIPAddress")) {
						reply = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">"
							+ $"<NewExternalIPAddress>{external}</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
					} else {
						reply = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>";
					}
				}

				byte[] replyBytes = System.Text.Encoding.UTF8.GetBytes(reply);
				await ns.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/xml\r\nContent-Length: {replyBytes.Length}\r\nConnection: close\r\n\r\n"));
				await ns.WriteAsync(replyBytes);
			}
		}
	});

	ra.GetProperty("GatewayForTests", S)!.SetValue(null, new Uri($"http://127.0.0.1:{fport}/desc.xml"));
	async Task Map(int port) => await (Task) ra.GetMethod("MapAsync", S)!.Invoke(null, [port])!;
	async Task Unmap() => await (Task) ra.GetMethod("RemoveAsync", S)!.Invoke(null, [false])!;
	string? Link() => (string?) ra.GetProperty("Link", S)!.GetValue(null);
	string? Problem() => (string?) ra.GetProperty("Problem", S)!.GetValue(null);
	List<string> Sent() { lock (soap) { return [.. soap]; } }

	await Map(7242);
	Check("upnp: asks the router to forward the port to this PC, for an hour", Sent().Any(r => r.Contains("#AddPortMapping\"") && r.Contains("<NewExternalPort>7242<")
		&& r.Contains("<NewInternalPort>7242<") && r.Contains("<NewInternalClient>127.0.0.1<") && r.Contains("<NewLeaseDuration>3600<") && r.Contains("<NewProtocol>TCP<")));
	Check("upnp: the link is the router's internet address", Link() == "http://203.0.113.7:7242/", Link() ?? "null");
	await Unmap();
	Check("upnp: switched off, the forward is taken away", Sent()[^1].Contains("#DeletePortMapping\"") && Sent()[^1].Contains("<NewExternalPort>7242<") && (Link() == null));

	lock (soap) { soap.Clear(); }
	refuseLease = true;
	await Map(7242);
	Check("upnp: a router that only takes permanent forwards gets one", (Sent().Count(r => r.Contains("#AddPortMapping\"")) == 2) && Sent().Any(r => r.Contains("<NewLeaseDuration>0<")) && (Link() != null));
	await Unmap();
	refuseLease = false;

	lock (soap) { soap.Clear(); }
	external = "100.72.1.9";
	await Map(7242);
	Check("upnp: an address the provider shares is said, and the forward undone", (Link() == null) && (Problem() ?? "").Contains("shared") && Sent()[^1].Contains("#DeletePortMapping\""), Problem() ?? "no problem said");

	lock (soap) { soap.Clear(); }
	external = "192.168.1.20";
	await Map(7242);
	Check("upnp: a router behind another router is said", (Link() == null) && (Problem() ?? "").Contains("behind another router"), Problem() ?? "no problem said");

	stopFake.Cancel();
	fake.Stop();
	ra.GetProperty("GatewayForTests", S)!.SetValue(null, null);
}

// SETTINGSCOUNT
Console.WriteLine($"settings: {NocatFarm.Config.Settings.Global.Count} global ({NocatFarm.Config.Settings.Global.Count(d => !d.Advanced)} basic), {NocatFarm.Config.Settings.Bot.Count} per account ({NocatFarm.Config.Settings.Bot.Count(d => !d.Advanced)} basic)");
Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
return fails;
