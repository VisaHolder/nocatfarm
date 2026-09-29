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
	string json = """{"response":{"trade_offers_received":[{"tradeofferid":"555","accountid_other":193381173,"trade_offer_state":2,"is_our_offer":false,"escrow_end_date":0,"items_to_give":[{"appid":753,"contextid":"6","assetid":"77","classid":"10","instanceid":"0","amount":"1"}],"items_to_receive":[{"appid":753,"contextid":"6","assetid":"88","classid":"20","instanceid":"0","amount":"1"}]},{"tradeofferid":"556","accountid_other":1,"trade_offer_state":2,"is_our_offer":false,"escrow_end_date":1790000000,"items_to_receive":[{"appid":730,"contextid":"2","assetid":"9","classid":"99","instanceid":"0","amount":"1"}]}],"descriptions":[{"classid":"10","instanceid":"0","type":"Game Trading Card","name":"Alpha","market_fee_app":"440"},{"classid":"20","instanceid":"0","type":"Game Trading Card","name":"Beta","market_fee_app":"440"}]}}""";
	using var doc = System.Text.Json.JsonDocument.Parse(json);
	var resp = doc.RootElement.GetProperty("response");
	var descs = to.GetMethod("Descriptions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [resp])!;
	var read = to.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!;
	var list = resp.GetProperty("trade_offers_received").EnumerateArray().Select(e => (NocatFarm.Core.TradeOffers.Offer?) read.Invoke(null, [e, descs])).ToList();
	var o1 = list[0]!; var o2 = list[1]!;
	Check("offers: partner comes from the account id (vanity URLs don't matter)", o1.Partner == 76561198153646901UL, $"{o1.Partner}");
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
	const ulong Kylro = 76561198153646901UL, Old = 76561199047096760UL, Stranger = 76561198000000123UL;
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
	string shown = string.Join("\n", parts.Select(static p => p[8..^4]));
	Check("discord replies: two messages at most, the start of the reply word for word, then how much was left off",
		(parts.Count == 2) && big.StartsWith(shown[..shown.LastIndexOf('\n')], StringComparison.Ordinal) && shown.EndsWith("the whole reply is in the dashboard's Console", StringComparison.Ordinal));
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

// ── secrets at rest: nothing saved in plain text, and plain text left by older versions gets encrypted ──────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-secrets-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	string cfgDir = NocatFarm.Config.ConfigStore.ConfigDir;
	bool Clear(string file, params string[] plain) { string text = File.ReadAllText(file); return plain.All(x => !text.Contains(x, StringComparison.Ordinal)); }

	try {
		File.WriteAllText(NocatFarm.Config.ConfigStore.GlobalPath, """
			{ "WebPassword": "plain-dash-pass-123", "TelegramBotToken": "123456789:plain-telegram-token", "DiscordBotToken": "plain-discord-bot",
			  "DiscordWebhookUrl": "https://discord.com/api/webhooks/1/plain-hook", "Rep4RepApiToken": "plain-r4r", "WebProxyPassword": "plain-proxy" }
			""");
		NocatFarm.Config.GlobalConfig g = NocatFarm.Config.ConfigStore.LoadGlobal();
		Check("secrets: global values still read right", (g.WebPassword == "plain-dash-pass-123") && (g.TelegramBotToken == "123456789:plain-telegram-token") && (g.WebProxyPassword == "plain-proxy"));
		Check("secrets: plain text in the global file is encrypted on reading", Clear(NocatFarm.Config.ConfigStore.GlobalPath,
			"plain-dash-pass-123", "plain-telegram-token", "plain-discord-bot", "plain-hook", "plain-r4r", "plain-proxy"));

		string botFile = Path.Combine(cfgDir, "alt.json");
		File.WriteAllText(botFile, """
			{ "SteamLogin": "altlogin", "SteamPassword": "plain-steam-pass", "SteamParentalCode": "7391", "SharedSecret": "plainShared=",
			  "IdentitySecret": "plainIdentity=", "AccountProxyPassword": "plain-acct-proxy" }
			""");
		Dictionary<string, NocatFarm.Config.BotConfig> bots = NocatFarm.Config.ConfigStore.LoadBots();
		Check("secrets: account values still read right", bots.TryGetValue("alt", out NocatFarm.Config.BotConfig? alt)
			&& (alt.SteamPassword == "plain-steam-pass") && (alt.SteamParentalCode == "7391") && (alt.SharedSecret == "plainShared="));
		Check("secrets: plain text in an account file is encrypted on reading (Family View PIN too)", Clear(botFile,
			"plain-steam-pass", "\"7391\"", "plainShared=", "plainIdentity=", "plain-acct-proxy"));
		NocatFarm.Config.ConfigStore.SaveBot("alt", bots["alt"]);
		Check("secrets: saving an account keeps them encrypted", Clear(botFile, "plain-steam-pass", "\"7391\"", "plainShared="));

		Directory.CreateDirectory(Path.Combine(tmpRoot, "config", "tokens"));
		string tokenFile = Path.Combine(tmpRoot, "config", "tokens", "alt.token");
		File.WriteAllText(tokenFile, "eyAidHlwIjog-plain-refresh-token");
		Check("secrets: an old plain login token still works", NocatFarm.Core.TokenStore.Load("alt") == "eyAidHlwIjog-plain-refresh-token");
		Check("secrets: ...and is encrypted the moment it's read", Clear(tokenFile, "plain-refresh-token") && (NocatFarm.Core.TokenStore.Load("alt") == "eyAidHlwIjog-plain-refresh-token"));

		Directory.CreateDirectory(NocatFarm.Core.MaFiles.Dir);
		string maFile = NocatFarm.Core.MaFiles.PathFor("alt");
		const string maJson = """{"shared_secret":"c2hhcmVkLXNlY3JldA==","identity_secret":"aWRlbnRpdHk=","device_id":"android:1234"}""";
		File.WriteAllText(maFile, maJson);
		var first = NocatFarm.Core.MobileAuth.ReadMaFile(maFile);
		var again = NocatFarm.Core.MobileAuth.ReadMaFile(maFile);
		Check("secrets: an authenticator file in nocat.farm's folder is encrypted and still reads", (first.Shared == "c2hhcmVkLXNlY3JldA==")
			&& Clear(maFile, "c2hhcmVkLXNlY3JldA==", "aWRlbnRpdHk=") && (again.Shared == "c2hhcmVkLXNlY3JldA==") && (again.DeviceId == "android:1234"));

		string elsewhere = Path.Combine(tmpRoot, "asf", "alt.maFile");
		Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
		File.WriteAllText(elsewhere, maJson);
		NocatFarm.Core.MobileAuth.ReadMaFile(elsewhere);
		Check("secrets: a maFile anywhere else (ASF's folder) is never touched", File.ReadAllText(elsewhere) == maJson);

		string keysFile = Path.Combine(cfgDir, "state", "keys.json");
		Directory.CreateDirectory(Path.GetDirectoryName(keysFile)!);
		File.WriteAllText(keysFile, """[{"Key":"PLAIN-KEY12-ABCDE","AddedAt":1,"Tries":0,"NotBefore":0}]""");
		Check("secrets: queued Steam keys still load, and the file is encrypted", (NocatFarm.Core.KeyQueue.Count == 1) && Clear(keysFile, "PLAIN-KEY12-ABCDE"));
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch {
			// the temp folder empties itself eventually
		}
	}
}

// ── achievement order: every game - tiers, written numbers, story order, difficulty ──────────────────────────
{
	Type pacer = typeof(Rng).Assembly.GetType("NocatFarm.Modules.AchievementPacer")!;
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	NocatFarm.Core.Achievement A(string name, string display, string desc, bool unlocked = false) =>
		new() { Name = name, Display = display, Description = desc, StatId = 1, Bit = 0, Unlocked = unlocked, Protected = false };
	bool Held(string rule, NocatFarm.Core.Achievement a, List<NocatFarm.Core.Achievement> all) =>
		(bool) pacer.GetMethod(rule, S)!.Invoke(null, [a, all])!;
	bool AnyHeld(NocatFarm.Core.Achievement a, List<NocatFarm.Core.Achievement> all) =>
		Held("TierBlocked", a, all) || Held("DifficultyBlocked", a, all) || Held("StoryEndBlocked", a, all);

	var k5 = A("K5", "Sharpshooter", "Get 5 kills");
	var k10 = A("K10", "Marksman", "Get 10 kills");
	var k1000 = A("K1000", "Legend", "Get 1,000 kills");
	var kills = new List<NocatFarm.Core.Achievement> { k5, k10, k1000 };
	Check("order: 10 kills waits for 5 kills (numbers only in the description)", AnyHeld(k10, kills) && AnyHeld(k1000, kills) && !AnyHeld(k5, kills));
	kills[0] = A("K5", "Sharpshooter", "Get 5 kills", unlocked: true);
	Check("order: ...and goes once 5 kills is earned", !AnyHeld(k10, kills) && AnyHeld(k1000, kills));

	var c1 = A("C1", "Chapter One", "Finish the first chapter");
	var c2 = A("C2", "Chapter Two", "Finish the second chapter");
	Check("order: 'Chapter Two' waits for 'Chapter One' (written numbers)", AnyHeld(c2, [c1, c2]) && !AnyHeld(c1, [c1, c2]));

	var act1 = A("A1", "Act I", "Complete Act I");
	var act2 = A("A2", "Act II", "Complete Act II");
	Check("order: 'Act II' waits for 'Act I' (roman numerals)", AnyHeld(act2, [act1, act2]) && !AnyHeld(act1, [act1, act2]));

	var ch3 = A("CH3", "Into the Dark", "Complete Chapter 3");
	var end = A("END", "Credits", "Finish the game");
	Check("order: 'finish the game' waits for the chapters before it", AnyHeld(end, [ch3, end]));
	Check("order: ...and goes once they're done", !AnyHeld(end, [A("CH3", "Into the Dark", "Complete Chapter 3", unlocked: true), end]));
	var lastMission = A("M9", "The End", "Complete the final mission");
	Check("order: 'the final mission' waits for a locked 'Mission 4'", AnyHeld(lastMission, [A("M4", "Mission 4", "Complete mission 4"), lastMission]));

	var normal = A("N", "Survivor", "Complete the game on Normal");
	var hard = A("H", "Veteran Survivor", "Complete the game on Hard");
	Check("order: 'on Hard' waits for 'on Normal'", Held("DifficultyBlocked", hard, [normal, hard]) && !Held("DifficultyBlocked", normal, [normal, hard]));

	var pistol = A("P", "Gunslinger", "Kill 5 enemies with a pistol");
	var shotgun = A("SG", "Boomstick", "Kill 10 enemies with a shotgun");
	Check("order: different things with different numbers are NOT a ladder", !AnyHeld(shotgun, [pistol, shotgun]));

	var w10 = A("W10", "Quick", "Win 10 matches in under 5 minutes");
	var w20 = A("W20", "Quicker", "Win 20 matches in under 5 minutes");
	Check("order: two numbers in a line still ladder (10 before 20)", AnyHeld(w20, [w10, w20]) && !AnyHeld(w10, [w10, w20]));
	var p5k = A("P5", "Scorer", "Score 5,000 points");
	var p10k = A("P10", "High Scorer", "Score 10k points");
	Check("order: '10k' waits for '5,000'", AnyHeld(p10k, [p5k, p10k]));
}

// ── achievement hunter: rotation between games, real game length ─────────────────────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	MethodInfo rotate = typeof(NocatFarm.Modules.AchievementBoost).GetMethod("Rotate", S)!;
	List<uint> list = [10, 20, 30, 40, 50];
	(List<uint> Picks, int Index) Run(int size, int turns, Func<uint, bool> usable) {
		object[] a = [list, size, usable, 0, 0u];
		List<uint> picks = [];
		for (int i = 0; i < turns; i++) {
			picks.Add((uint) rotate.Invoke(null, a)!);
			a[4] = picks[^1];
		}
		return (picks, (int) a[3]);
	}
	var rot3 = Run(3, 9, _ => true).Picks;
	Check("hunter: 3 in rotation takes turns between the first 3 only", rot3.Distinct().OrderBy(x => x).SequenceEqual([10u, 20u, 30u]), string.Join(",", rot3));
	Check("hunter: never the same game twice in a row", rot3.Zip(rot3.Skip(1)).All(p => p.First != p.Second), string.Join(",", rot3));
	var resting = Run(3, 9, app => app != 20).Picks;
	Check("hunter: a resting game makes room for the next on the list", resting.Distinct().OrderBy(x => x).SequenceEqual([10u, 30u, 40u]), string.Join(",", resting));
	var one = Run(1, 4, _ => true).Picks;
	Check("hunter: 1 in rotation stays on one game", one.All(x => x == 10), string.Join(",", one));
	Check("hunter: every game resting picks nothing", Run(3, 2, _ => false).Picks.All(x => x == 0));

	MethodInfo fromMin = typeof(NocatFarm.Core.Playtime).GetMethod("FromMinutes", S)!;
	double H(long med, long avg) => (double) fromMin.Invoke(null, [med, avg])!;
	Check("playtime: the median player's hours", H(600, 600) == 10, $"{H(600, 600)}");
	Check("playtime: 60% of the average when the median undercounts a story game", H(60, 2000) == 20, $"{H(60, 2000)}");
	Check("playtime: no figures is unknown, silly ones are capped", H(0, 0) == 0 && H(1, 1) == 0.5 && H(99999, 0) == 400);

	Type pacer = typeof(Rng).Assembly.GetType("NocatFarm.Modules.AchievementPacer")!;
	MethodInfo story = pacer.GetMethod("StoryTimeAllows", S)!;
	NocatFarm.Core.Achievement A(string name, string display, string desc) =>
		new() { Name = name, Display = display, Description = desc, StatId = 1, Bit = 0, Unlocked = false, Protected = false };
	bool Ok(NocatFarm.Core.Achievement a, List<NocatFarm.Core.Achievement> all, double hours, double? typical) =>
		(bool) story.Invoke(null, [a, all, hours, typical])!;
	var end = A("END", "Credits", "Finish the game");
	Check("real length: 'finish the game' never 3 hours into a 20 hour game", !Ok(end, [end], 3, 20) && Ok(end, [end], 17, 20));
	var chapters = Enumerable.Range(1, 10).Select(i => A("C" + i, "Chapter " + i, "Complete chapter " + i)).ToList();
	Check("real length: chapter 5 of 10 needs about 40% of a 20 hour game", !Ok(chapters[4], chapters, 7, 20) && Ok(chapters[4], chapters, 8.5, 20));
	Check("real length: chapter 1 is fine early", Ok(chapters[0], chapters, 1.7, 20));
	Check("real length: unknown length holds nothing back", Ok(end, [end], 0.5, null));
	var kills = A("K", "Marksman", "Get 100 kills");
	Check("real length: non-story achievements aren't held by it", Ok(kills, [kills], 0.5, 20));
}

// ── importing from other idlers: every reader fed realistic files in a temp folder, and the one writer ──────────
// Nothing real is looked at: every place the importers search is pointed into the temp folder first, Steam's
// loginusers.vdf is a sample, and the Windows credential store is a fake that records what was asked of it.
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmp = Path.Combine(Path.GetTempPath(), "nf-import-" + Guid.NewGuid().ToString("N"));
	var places = (NocatFarm.Config.ImportPlaces.LocalAppData, NocatFarm.Config.ImportPlaces.AppData,
		NocatFarm.Config.ImportPlaces.Desktop, NocatFarm.Config.ImportPlaces.Documents, NocatFarm.Config.ImportPlaces.Downloads,
		NocatFarm.Config.ImportPlaces.SteamPath, NocatFarm.Config.ImportPlaces.ReadCredential);
	List<string> credentialAsks = [];
	string credentialJwt = "";

	string Dir(params string[] parts) { string d = Path.Combine([tmp, .. parts]); Directory.CreateDirectory(d); return d; }
	void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
	static string B64Url(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	static string Jwt(string sub, long exp) => B64Url("{\"alg\":\"EdDSA\",\"typ\":\"JWT\"}") + "." + B64Url($"{{\"iss\":\"steam\",\"sub\":\"{sub}\",\"aud\":[\"client\",\"web\"],\"exp\":{exp}}}") + ".c2ln";
	long future = DateTimeOffset.UtcNow.AddDays(150).ToUnixTimeSeconds();
	Dictionary<string, string> Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
		.ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
	bool Clear(string file, params string[] plain) { string text = File.ReadAllText(file); return plain.All(x => !text.Contains(x, StringComparison.Ordinal)); }
	NocatFarm.Config.ImportedAccount Acct(NocatFarm.Config.ImportScan s, string key) => s.Accounts.First(a => a.Key == key);

	try {
		NocatFarm.Config.ConfigStore.UseRoot(Dir("app"));
		NocatFarm.Config.ImportPlaces.LocalAppData = Dir("local");
		NocatFarm.Config.ImportPlaces.AppData = Dir("roaming");
		NocatFarm.Config.ImportPlaces.Desktop = Dir("desktop");
		NocatFarm.Config.ImportPlaces.Documents = Dir("documents");
		NocatFarm.Config.ImportPlaces.Downloads = Dir("downloads");
		NocatFarm.Config.ImportPlaces.SteamPath = () => Path.Combine(tmp, "steam");
		NocatFarm.Config.ImportPlaces.ReadCredential = target => {
			credentialAsks.Add(target);
			return target == "sgiuser.com.zevnda.steam-game-idler.agent"
				? System.Text.Encoding.Unicode.GetBytes(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentialJwt)))
				: null;
		};
		NocatFarm.Config.GlobalConfig global = NocatFarm.Config.ConfigStore.LoadGlobal();

		Put(Path.Combine(tmp, "steam", "config", "loginusers.vdf"), """
			"users"
			{
				"76561198000000009"
				{
					"AccountName"		"imelogin"
					"PersonaName"		"ime person"
					"MostRecent"		"1"
				}
				"76561198000000010"
				{
					"AccountName"		"someoneelse"
				}
			}
			""");

		// ── ArchiSteamFarm, pointed at its own folder (the installer and most people do) ──
		string asfRoot = Dir("desktop", "ASF-win-x64");
		string asf = Dir("desktop", "ASF-win-x64", "config");
		string mainToken = Jwt("76561198000000001", future);
		Put(Path.Combine(asf, "ASF.json"), """{ "Blacklist": [440, 570], "WebProxy": "http://127.0.0.1:8080", "WebProxyUsername": "pu", "WebProxyPassword": "pp", "SteamOwnerID": 0 }""");
		Put(Path.Combine(asf, "IPC.config"), "{}");
		Put(Path.Combine(asf, "main.json"), """
			{ "SteamLogin": "mainlogin", "SteamPassword": "plain-asf-pass", "PasswordFormat": 0, "Enabled": true,
			  "GamesPlayedWhileIdle": [730], "CustomGamePlayedWhileIdle": "nocat.lol", "FarmingOrders": [3], "FarmingPreferences": 32,
			  "HumanIdlerRep4RepToken": "r4r-from-asf", "GamingDeviceType": 544, "SteamUserPermissions": { "76561198000000077": 3 } }
			""");
		Put(Path.Combine(asf, "main.db"), $$"""
			{ "BackingRefreshToken": "{{mainToken}}", "FarmingBlacklistAppIDs": [10], "FarmingPriorityQueueAppIDs": [20, 30],
			  "CachedSteamParentalCode": "4321",
			  "_MobileAuthenticator": { "shared_secret": "bWFpbi1zaGFyZWQtc2VjcmV0MTI=", "identity_secret": "bWFpbi1pZGVudGl0eS1zZWNyZXQ=" } }
			""");

		// PasswordFormat 1: ASF's own AES - the IV encrypted with ECB, then CBC, under SHA-256("ArchiSteamFarm").
		string AsfAes(string plain, string key) {
			using System.Security.Cryptography.Aes aes = System.Security.Cryptography.Aes.Create();
			aes.Key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
			byte[] iv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
			byte[] head = aes.EncryptEcb(iv, System.Security.Cryptography.PaddingMode.None);
			byte[] body = aes.EncryptCbc(System.Text.Encoding.UTF8.GetBytes(plain), iv, System.Security.Cryptography.PaddingMode.PKCS7);
			return Convert.ToBase64String([.. head, .. body]);
		}

		Put(Path.Combine(asf, "aes.json"), $$"""{ "SteamLogin": "aeslogin", "SteamPassword": "{{AsfAes("aes-pass-ü1", "ArchiSteamFarm")}}", "PasswordFormat": 1 }""");
		Put(Path.Combine(asf, "customkey.json"), $$"""{ "SteamLogin": "customlogin", "SteamPassword": "{{AsfAes("secret-pass", "my own crypt key")}}", "PasswordFormat": 1 }""");
		Environment.SetEnvironmentVariable("NF_IMPORT_TEST_PW", "env-pass");
		Put(Path.Combine(asf, "env.json"), """{ "SteamLogin": "envlogin", "SteamPassword": "NF_IMPORT_TEST_PW", "PasswordFormat": 3 }""");
		Put(Path.Combine(asf, "noenv.json"), """{ "SteamLogin": "noenvlogin", "SteamPassword": "NF_IMPORT_TEST_UNSET", "PasswordFormat": 3 }""");
		Put(Path.Combine(asfRoot, "secrets", "pw.txt"), "file-pass\n");
		Put(Path.Combine(asf, "file.json"), """{ "SteamLogin": "filelogin", "SteamPassword": "secrets/pw.txt", "PasswordFormat": 4 }""");

		if (OperatingSystem.IsWindows()) {
			string dp = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes("dpapi-pass"),
				System.Text.Encoding.UTF8.GetBytes("ArchiSteamFarm"), System.Security.Cryptography.DataProtectionScope.CurrentUser));
			Put(Path.Combine(asf, "dpapi.json"), $$"""{ "SteamLogin": "dpapilogin", "SteamPassword": "{{dp}}", "PasswordFormat": 2 }""");
		}

		// A maFile named by SteamID the way Steam Desktop Authenticator names them, and one half-way through ASF's own setup.
		string sdaToken = Jwt("76561198000000002", future);
		const string sdaMa = """{"shared_secret":"c2RhLXNoYXJlZC1zZWNyZXQxMjM=","identity_secret":"c2RhLWlkZW50aXR5","device_id":"android:sda","account_name":"sdalogin"}""";
		Put(Path.Combine(asf, "sda.json"), """{ "SteamLogin": "sdalogin" }""");
		Put(Path.Combine(asf, "sda.db"), $$"""{ "BackingRefreshToken": "{{sdaToken}}" }""");
		Put(Path.Combine(asf, "76561198000000002.maFile"), sdaMa);
		Put(Path.Combine(asf, "fresh.json"), """{ "SteamLogin": "freshlogin" }""");
		Put(Path.Combine(asf, "fresh.maFile.NEW"), """{"shared_secret":"ZnJlc2gtc2hhcmVkLXNlY3JldDE=","identity_secret":"ZnJlc2g=","account_name":"freshlogin"}""");

		// ── Idle Master Extended: two versions of its user.config; the newer one is the one in use ──
		string imeBase = Dir("local", "IdleMasterExtended", "IdleMasterExtended.exe_Url_4qdzmn3c1yj0mj4iuz0nqzqdm1r1cuxc");
		string UserConfig(string section, string settings) => $"""
			<?xml version="1.0" encoding="utf-8"?>
			<configuration>
			    <userSettings>
			        <{section}>
			{settings}
			        </{section}>
			    </userSettings>
			</configuration>
			""";
		Put(Path.Combine(imeBase, "1.0.0.0", "user.config"), UserConfig("IdleMasterExtended.Properties.Settings", """
			            <setting name="sort" serializeAs="String"><value>mostcards</value></setting>
			"""));
		Put(Path.Combine(imeBase, "1.2.0.0", "user.config"), UserConfig("IdleMasterExtended.Properties.Settings", """
			            <setting name="blacklist" serializeAs="Xml">
			                <value>
			                    <ArrayOfString xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
			                        <string>440</string>
			                        <string />
			                        <string>570</string>
			                    </ArrayOfString>
			                </value>
			            </setting>
			            <setting name="whitelist" serializeAs="Xml">
			                <value><ArrayOfString><string>730</string></ArrayOfString></value>
			            </setting>
			            <setting name="sort" serializeAs="String"><value>leastcards</value></setting>
			            <setting name="IdleOnlyPlayed" serializeAs="String"><value>True</value></setting>
			            <setting name="steamLoginSecure" serializeAs="String"><value>76561198000000009%7C%7CeyJhbGciOiJFZERTQSJ9.e30.sig</value></setting>
			"""));
		string imeExeDir = Dir("desktop", "IdleMasterExtended");
		Put(Path.Combine(imeExeDir, "IdleMasterExtended.exe"), "MZ");

		// ── the original Idle Master, installed by ClickOnce ──
		Put(Path.Combine(tmp, "local", "Apps", "2.0", "Data", "AB12CD34.EF5", "GH67IJ89.KL0", "idle..tion_0000000000000000_0001.0000_abcdef0123456789",
			"Data", "1.0.0.0", "user.config"), UserConfig("IdleMaster.Properties.Settings", """
			            <setting name="sort" serializeAs="String"><value>mostcards</value></setting>
			            <setting name="blacklist" serializeAs="Xml"><value><ArrayOfString><string>220</string></ArrayOfString></value></setting>
			"""));

		// ── HourBoostr, unzipped a folder down on the Desktop ──
		Put(Path.Combine(tmp, "desktop", "tools", "HourBoostr", "Settings.json"), """
			{ "Accounts": [
			    { "Details": { "Username": "boostme", "Password": "hb-pass", "LoginKey": "old-login-key" }, "ShowOnlineStatus": false, "IgnoreAccount": false, "Games": [730, 440] },
			    { "Details": { "Username": "resting", "Password": "", "LoginKey": "" }, "ShowOnlineStatus": true, "IgnoreAccount": true, "Games": [] } ],
			  "CheckForUpdates": true }
			""");
		Put(Path.Combine(tmp, "desktop", "tools", "HourBoostr", "Sentryfiles", "boostme.sentry"), "binary");

		// ── SingleBoostr ──
		Put(Path.Combine(tmp, "documents", "SingleBoostr", "Settings.json"), """
			{ "BlacklistedCardGames": [1000, 2000], "OnlyIdleGamesWithCertainMinutes": true, "NumOnlyIdleGamesWithCertainMinutes": 120, "WebSession": { "SessionID": "x" } }
			""");

		// ── Steam Game Idler 6.x ──
		string sgi = Dir("roaming", "com.zevnda.steam-game-idler", "cache");
		Put(Path.Combine(sgi, "settings.json"), """{ "agentAccounts": { "sgiuser": "SgiUser" }, "agentAccountSteamIds": { "sgiuser": "76561198000000003" }, "theme": "dark" }""");
		string sgiAcct = Dir("roaming", "com.zevnda.steam-game-idler", "cache", "76561198000000003");
		Put(Path.Combine(sgiAcct, "auto_idle.json"), """{ "games": [ { "appId": 730, "name": "CS2", "enabled": true }, { "appId": 440, "name": "TF2", "enabled": false } ] }""");
		Put(Path.Combine(sgiAcct, "card_farming_blacklist.json"), """{ "blacklist": [ { "appId": 570, "name": "Dota 2" } ] }""");
		Put(Path.Combine(sgiAcct, "card_farming_settings.json"), """{ "hoursUntilFarmable": 2, "skipNoPlaytime": true, "skipRefundableGames": true }""");
		Put(Path.Combine(sgiAcct, "max_playtime_settings.json"), """{ "per_game_max_playtime": { "730": 90, "440": 600 } }""");
		Put(Path.Combine(sgiAcct, "presence_settings.json"), """{ "customIdleStatus": "just vibing" }""");
		credentialJwt = Jwt("76561198000000003", future);

		// ── 3urobeat's steam-idler ──
		string idlerDir = Dir("downloads", "steam-idler-main");
		string secret20 = Convert.ToBase64String(Enumerable.Range(1, 20).Select(static i => (byte) i).ToArray());
		Put(Path.Combine(idlerDir, "accounts.txt"), $"// username:password:shared_secret\nidle1:pw-one\nidle2:pass:with:colons:{secret20}\n\n");
		Put(Path.Combine(idlerDir, "config.json"), """{ "playingGames": ["Idling for nocat", 730, 440], "onlinestatus": 7 }""");

		Dictionary<string, string> before = Hashes(tmp).Where(p => !p.Key.StartsWith(Path.Combine(tmp, "app"), StringComparison.OrdinalIgnoreCase))
			.ToDictionary(p => p.Key, p => p.Value);

		// ── ASF ──
		var asfScan = NocatFarm.Config.IdlerImport.Scan("asf", asfRoot);
		Check("import asf: the exe folder leads to its config folder", asfScan.Found && (asfScan.Path == asf), asfScan.Path ?? "not found");
		Check("import asf: every bot, not ASF.json or IPC.config", asfScan.Accounts.Count == (OperatingSystem.IsWindows() ? 9 : 8), string.Join(",", asfScan.Accounts.Select(a => a.Key)));
		var main = Acct(asfScan, "main");
		Check("import asf: login token, password and the authenticator ASF kept in its database", (main.Token == mainToken) && (main.Config.SteamPassword == "plain-asf-pass")
			&& (main.Config.SharedSecret == "bWFpbi1zaGFyZWQtc2VjcmV0MTI=") && (main.Config.IdentitySecret == "bWFpbi1pZGVudGl0eS1zZWNyZXQ=") && (main.SteamId == "76561198000000001"));
		Check("import asf: games, farming order, blacklist and priority from the database", main.Config.IdleGames.SequenceEqual([730u]) && (main.Config.FarmingOrder == 2)
			&& main.Config.SkipUnplayedGames && main.Config.BlacklistedGames.SequenceEqual([10u]) && main.Config.PriorityGames.SequenceEqual([20u, 30u]));
		Check("import asf: the Family View PIN ASF worked out itself", main.Config.SteamParentalCode == "4321");
		Check("import asf: custom name kept, and like ASF the real game shows while farming", (main.Config.CustomGameName == "nocat.lol") && !main.Config.PlayWhileFarming);
		Check("import asf: a device it can't map is said, not guessed", main.Notes.Any(n => n.ToString().Contains("Play as if on")) && (main.Config.GameDevice == 0));
		Check("import asf: PasswordFormat 1 (ASF's AES) opens", Acct(asfScan, "aes").Config.SteamPassword == "aes-pass-ü1", Acct(asfScan, "aes").Config.SteamPassword);
		var custom = Acct(asfScan, "customkey");
		Check("import asf: a custom crypt key is refused gracefully, not copied scrambled", (custom.Config.SteamPassword == "") && custom.Notes.Any(n => n.ToString().Contains("key of its own")));
		Check("import asf: PasswordFormat 3 reads the environment variable", Acct(asfScan, "env").Config.SteamPassword == "env-pass");
		Check("import asf: ...and says so when it isn't set", (Acct(asfScan, "noenv").Config.SteamPassword == "") && Acct(asfScan, "noenv").Notes.Any(n => n.ToString().Contains("NF_IMPORT_TEST_UNSET")));
		Check("import asf: PasswordFormat 4 reads the file, relative to ASF's folder", Acct(asfScan, "file").Config.SteamPassword == "file-pass");
		if (OperatingSystem.IsWindows()) {
			Check("import asf: PasswordFormat 2 (Windows protection with ASF's entropy) opens", Acct(asfScan, "dpapi").Config.SteamPassword == "dpapi-pass");
		}
		Check("import asf: a maFile named by SteamID (from the token) is found", Acct(asfScan, "sda").MaFile == sdaMa);
		Check("import asf: a .maFile.NEW is found", Acct(asfScan, "fresh").MaFile?.Contains("ZnJlc2gtc2hhcmVkLXNlY3JldDE=") == true);
		Check("import asf: global blacklist, proxy and the rep4rep token offered as settings",
			asfScan.Settings.Select(s => s.Name).Order().SequenceEqual(["GlobalBlacklistedGames", "Rep4RepApiToken", "WebProxy"]), string.Join(",", asfScan.Settings.Select(s => s.Name)));
		Check("import asf: the old preview still answers", NocatFarm.Config.AsfImport.Preview(asf).Any(c => (c.Name == "main") && c.HasToken && c.HasPassword));

		var asfOut = NocatFarm.Config.IdlerImport.Apply(asfScan, [new("main", Human: true), new("sda"), new("aes")], global, settings: [0, 1, 2]);
		string mainFile = Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "main.json");
		Check("import asf: only the ticked accounts are written", (asfOut.Imported == 3) && File.Exists(mainFile) && !File.Exists(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "env.json")));
		Check("import asf: password, authenticator secrets and PIN are encrypted on disk", Clear(mainFile, "plain-asf-pass", "bWFpbi1zaGFyZWQtc2VjcmV0MTI=", "bWFpbi1pZGVudGl0eS1zZWNyZXQ=", "\"4321\""));
		var loaded = NocatFarm.Config.ConfigStore.LoadBots();
		Check("import asf: ...and read back right, in human mode as ticked", (loaded["main"].SteamPassword == "plain-asf-pass") && (loaded["main"].SharedSecret == "bWFpbi1zaGFyZWQtc2VjcmV0MTI=")
			&& loaded["main"].LegitMode && (loaded["main"].GameWeights == "730:70") && (asfOut.Human.SequenceEqual(["main"])));
		Check("import asf: the login token is stored (encrypted)", (NocatFarm.Core.TokenStore.Load("main") == mainToken)
			&& Clear(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "tokens", "main.token"), mainToken));
		Check("import asf: the SteamID-named maFile kept, encrypted", File.Exists(NocatFarm.Core.MaFiles.PathFor("sda"))
			&& Clear(NocatFarm.Core.MaFiles.PathFor("sda"), "c2RhLXNoYXJlZC1zZWNyZXQxMjM=") && (NocatFarm.Core.MobileAuth.ReadMaFile(NocatFarm.Core.MaFiles.PathFor("sda")).DeviceId == "android:sda"));
		Check("import asf: global settings merged in", global.GlobalBlacklistedGames.Contains(440u) && global.GlobalBlacklistedGames.Contains(570u)
			&& (global.WebProxy == "http://127.0.0.1:8080") && (global.WebProxyPassword == "pp") && (global.Rep4RepApiToken == "r4r-from-asf"));
		var again = NocatFarm.Config.IdlerImport.Apply(asfScan, [new("main")], global);
		Check("import asf: importing twice leaves the account alone", (again.Imported == 0) && (again.Skipped == 1) && NocatFarm.Config.ConfigStore.LoadBots()["main"].LegitMode);
		var legacy = NocatFarm.Config.AsfImport.Run(asf, global, overwrite: false);
		Check("import asf: the old one-call import still works", (legacy.Imported == asfScan.Accounts.Count - 3) && (legacy.Skipped == 3), $"{legacy.Imported}/{legacy.Skipped}");

		// ── Idle Master Extended ──
		var ime = NocatFarm.Config.IdlerImport.Scan("ime", null);
		Check("import ime: found in its profile folder, the newest version", ime.Found && ime.Path!.Contains("1.2.0.0"), ime.Path ?? string.Join(" | ", ime.Looked));
		var imeAcct = ime.Accounts.Single();
		Check("import ime: account name found through Steam's own list, from the cookie's SteamID only", (imeAcct.SteamLogin == "imelogin") && (imeAcct.SteamId == "76561198000000009")
			&& (imeAcct.Token == null) && !imeAcct.HasPassword);
		Check("import ime: blacklist (empty entries skipped), whitelist, order, played-only", imeAcct.Config.BlacklistedGames.SequenceEqual([440u, 570u])
			&& imeAcct.Config.IdleGames.SequenceEqual([730u]) && (imeAcct.Config.FarmingOrder == 2) && imeAcct.Config.SkipUnplayedGames);
		var fromInstaller = NocatFarm.Config.IdlerImport.FromPending(new NocatFarm.Config.PendingImport.Choice("idlemaster", imeExeDir));
		Check("import ime: the installer's 'Idle Master' with the exe folder opens Idle Master Extended", fromInstaller?.Tool == "ime", fromInstaller?.Tool ?? "null");
		Check("import ime: the exe folder still finds the settings in the profile", NocatFarm.Config.IdlerImport.Scan("ime", imeExeDir).Path == ime.Path);

		// ── the original Idle Master ──
		var im = NocatFarm.Config.IdlerImport.Scan("idlemaster", null);
		Check("import idlemaster: found under ClickOnce's Apps\\2.0", im.Found && im.Path!.Contains("idle..tion"), im.Path ?? string.Join(" | ", im.Looked));
		Check("import idlemaster: blacklist and order, no sign-in", im.Accounts.Single().Config.BlacklistedGames.SequenceEqual([220u])
			&& (im.Accounts.Single().Config.FarmingOrder == 3) && (im.Accounts.Single().SteamLogin == ""));
		var imOut = NocatFarm.Config.IdlerImport.Apply(im, [new(im.Accounts.Single().Key, SteamLogin: "typed_login")], global);
		var typed = NocatFarm.Config.ConfigStore.LoadBots().GetValueOrDefault("typed_login");
		Check("import idlemaster: a typed account name is used, and it asks for the password as usual", (imOut.Imported == 1) && (typed?.SteamLogin == "typed_login") && (typed?.SignInWithQr == false));
		var imQr = NocatFarm.Config.IdlerImport.Apply(NocatFarm.Config.IdlerImport.Scan("idlemaster", null), [new("idlemaster")], global);
		Check("import idlemaster: no name at all signs in with a QR code", (imQr.Imported == 1) && (NocatFarm.Config.ConfigStore.LoadBots().GetValueOrDefault("idlemaster")?.SignInWithQr == true));

		// ── HourBoostr ──
		var hb = NocatFarm.Config.IdlerImport.Scan("hourboostr", null);
		Check("import hourboostr: found a folder down on the Desktop", hb.Found && (hb.Accounts.Count == 2), hb.Path ?? string.Join(" | ", hb.Looked));
		var boostme = Acct(hb, "boostme");
		Check("import hourboostr: login, password, games, hidden status", (boostme.Config.SteamPassword == "hb-pass") && boostme.Config.IdleGames.SequenceEqual([730u, 440u]) && (boostme.Config.OnlineStatus == 7));
		Check("import hourboostr: an ignored account arrives switched off", !Acct(hb, "resting").Config.Enabled);
		Check("import hourboostr: the dead login keys are left behind, and it says so", hb.Notes.Any(n => n.ToString().Contains("login keys")) && (boostme.Token == null));

		// ── SingleBoostr ──
		var sb = NocatFarm.Config.IdlerImport.Scan("singleboostr", null);
		Check("import singleboostr: settings only, no account", sb.Found && (sb.Accounts.Count == 0) && (sb.Settings.Count == 2));
		var sbOut = NocatFarm.Config.IdlerImport.Apply(sb, [], global);
		Check("import singleboostr: blacklist into the global list, 120 minutes onto every account as 2 hours",
			global.GlobalBlacklistedGames.Contains(1000u) && NocatFarm.Config.ConfigStore.LoadBots().Values.All(b => Math.Abs(b.HoursUntilCardDrops - 2) < 0.01));

		// ── Steam Game Idler ──
		credentialAsks.Clear();
		var sg = NocatFarm.Config.IdlerImport.Scan("sgi", null);
		var sgUser = sg.Accounts.Single();
		Check("import sgi: account, games switched on only, blacklist, farming choices, caps, status", (sgUser.SteamLogin == "SgiUser") && sgUser.Config.IdleGames.SequenceEqual([730u])
			&& sgUser.Config.BlacklistedGames.SequenceEqual([570u]) && (Math.Abs(sgUser.Config.HoursUntilCardDrops - 2) < 0.01) && sgUser.Config.SkipUnplayedGames
			&& sgUser.Config.SkipRefundableGames && (sgUser.Config.HourTargets == "730:2, 440:10") && (sgUser.Config.CustomGameName == "just vibing"), sgUser.Config.HourTargets);
		Check("import sgi: looking never touches the credential store", credentialAsks.Count == 0);
		if (OperatingSystem.IsWindows()) {
			var sgPlain = NocatFarm.Config.IdlerImport.Apply(sg, [new("sgiuser")], global);
			Check("import sgi: not ticked - its sign-in is not read", (credentialAsks.Count == 0) && (NocatFarm.Core.TokenStore.Load("SgiUser") == null));
			var sgSigned = NocatFarm.Config.IdlerImport.Apply(sg, [new("sgiuser", SignIn: true)], global, overwrite: true);
			Check("import sgi: ticked - read once, from its own credential, and kept as the login token",
				credentialAsks.SequenceEqual(["sgiuser.com.zevnda.steam-game-idler.agent"]) && (NocatFarm.Core.TokenStore.Load("SgiUser") == credentialJwt));
		}

		// ── steam-idler ──
		var si = NocatFarm.Config.IdlerImport.Scan("steamidler", null);
		Check("import steam-idler: every line but the comment", si.Found && (si.Accounts.Count == 2), si.Path ?? string.Join(" | ", si.Looked));
		var idle2 = Acct(si, "idle2");
		Check("import steam-idler: a password with colons and the shared secret after it", (idle2.Config.SteamPassword == "pass:with:colons") && (idle2.Config.SharedSecret == secret20)
			&& (Acct(si, "idle1").Config.SteamPassword == "pw-one") && (Acct(si, "idle1").Config.SharedSecret == ""));
		Check("import steam-idler: games and the custom status from config.json", idle2.Config.IdleGames.SequenceEqual([730u, 440u]) && (idle2.Config.CustomGameName == "Idling for nocat") && (idle2.Config.OnlineStatus == 7));
		NocatFarm.Config.IdlerImport.Apply(si, NocatFarm.Config.IdlerImport.All(si), global);
		Check("import steam-idler: password and secret encrypted on disk", Clear(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "idle2.json"), "pass:with:colons", secret20));

		// ── a folder, whichever program it is ──
		(string Folder, string Tool)[] folders = [(asfRoot, "asf"), (Path.Combine(tmp, "desktop", "tools", "HourBoostr"), "hourboostr"),
			(Path.Combine(tmp, "documents", "SingleBoostr"), "singleboostr"), (Path.Combine(tmp, "roaming", "com.zevnda.steam-game-idler"), "sgi"),
			(idlerDir, "steamidler"), (Path.Combine(tmp, "local", "IdleMasterExtended"), "ime")];
		string wrong = string.Join(", ", folders.Where(f => NocatFarm.Config.IdlerImport.Scan("auto", f.Folder).Tool != f.Tool).Select(f => f.Tool));
		Check("import auto: each folder is recognised as the right program", wrong.Length == 0, wrong);
		var nothing = NocatFarm.Config.IdlerImport.Scan("auto", Dir("empty"));
		Check("import auto: an unrelated folder finds nothing, and says where it looked", !nothing.Found && nothing.Looked.SequenceEqual([Path.Combine(tmp, "empty")]));
		var notThere = NocatFarm.Config.IdlerImport.Scan("hourboostr", Dir("empty"));
		Check("import: nothing found still lists where it looked", !notThere.Found && (notThere.Looked.Count == 1));

		// ── and not one byte of any of the other programs' files changed ──
		Dictionary<string, string> after = Hashes(tmp).Where(p => before.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
		Check("import: the other idlers' files are exactly as they were", (after.Count == before.Count) && before.All(p => after[p.Key] == p.Value)
			&& Hashes(tmp).Keys.Where(k => !k.StartsWith(Path.Combine(tmp, "app"), StringComparison.OrdinalIgnoreCase)).Count() == before.Count);
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		(NocatFarm.Config.ImportPlaces.LocalAppData, NocatFarm.Config.ImportPlaces.AppData, NocatFarm.Config.ImportPlaces.Desktop,
			NocatFarm.Config.ImportPlaces.Documents, NocatFarm.Config.ImportPlaces.Downloads, NocatFarm.Config.ImportPlaces.SteamPath,
			NocatFarm.Config.ImportPlaces.ReadCredential) = places;
		Environment.SetEnvironmentVariable("NF_IMPORT_TEST_PW", null);

		try {
			Directory.Delete(tmp, true);
		} catch {
			// the temp folder empties itself eventually
		}
	}
}

// ── start with Windows: a copy only ever removes its own entry ───────────────────────────────────────────
{
	MethodInfo exeOf = typeof(Rng).Assembly.GetType("NocatFarm.Windows.WindowsIntegration")!.GetMethod("ExeOf", BindingFlags.Static | BindingFlags.NonPublic)!;
	string E(string c) => (string) exeOf.Invoke(null, [c])!;
	Check("startup entry: the program in a quoted command", E("\"C:/Users/me/nocat farm/nocatFarm.exe\" --minimized") == "C:/Users/me/nocat farm/nocatFarm.exe");
	Check("startup entry: and in an unquoted one", E("C:/nf/nocatFarm.exe --minimized") == "C:/nf/nocatFarm.exe");
}

// ── chat replies: a long answer is two messages and a note, not a wall ────────────────────────────────────
{
	MethodInfo chat = typeof(NocatFarm.Core.Notifier).GetMethod("ChatChunks", BindingFlags.Static | BindingFlags.NonPublic)!;
	string longReply = string.Join(Environment.NewLine, Enumerable.Range(1, 520).Select(static i => $"  [x]  achievement number {i} with a long enough name"));
	var parts = (List<string>) chat.Invoke(null, [longReply, 3500])!;
	Check("chat: 520 lines arrive as 2 messages, not a dozen", parts.Count == 2, $"{parts.Count}");
	Check("chat: ...and say how much was left off", parts[^1].Contains("more line(s)"), parts[^1][^80..]);
	Check("chat: a short reply is untouched", ((List<string>) chat.Invoke(null, ["kylro: level 72", 3500])!).SequenceEqual(["kylro: level 72"]));
}

// ── review fixes: two endings, chat limits in any language ─────────────────────────────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type pacer = typeof(Rng).Assembly.GetType("NocatFarm.Modules.AchievementPacer")!;
	NocatFarm.Core.Achievement A(string name, string display, string desc) =>
		new() { Name = name, Display = display, Description = desc, StatId = 1, Bit = 0, Unlocked = false, Protected = false };
	bool EndHeld(NocatFarm.Core.Achievement a, List<NocatFarm.Core.Achievement> all) => (bool) pacer.GetMethod("StoryEndBlocked", S)!.Invoke(null, [a, all])!;
	var w1 = A("W1", "Boss One", "Defeat the final boss in World 1");
	var w2 = A("W2", "Boss Two", "Defeat the final boss in World 2");
	Check("order: two endings don't hold each other for ever", !EndHeld(w1, [w1, w2]) && !EndHeld(w2, [w1, w2]));

	MethodInfo chat = typeof(NocatFarm.Core.Notifier).GetMethod("ChatChunks", S)!;
	var rnd = new Random(7);
	string noisy = string.Join(Environment.NewLine, Enumerable.Range(0, 900).Select(i => new string('x', rnd.Next(1, 120))));
	var parts = (List<string>) chat.Invoke(null, [noisy, 1900])!;
	Check("chat: the 'more lines' note still fits under Discord's limit", parts.All(static p => p.Length + 200 <= 2000 + 100), $"longest {parts.Max(static p => p.Length)}");
}

// ── the phone: what's missing, and the links in words - the Phone page, 'dashboard' and /dashboard say the same ───
{
	Type dl = typeof(NocatFarm.Core.DashboardLinks);
	string[] Todo(NocatFarm.Core.DashboardLinks.Links l) => [.. NocatFarm.Core.DashboardLinks.Todo(l).Select(static s => s.English)];
	string TextOf(NocatFarm.Core.DashboardLinks.Links l) => (string) dl.GetMethod("Text", BindingFlags.Static | BindingFlags.NonPublic, [typeof(NocatFarm.Core.DashboardLinks.Links)])!.Invoke(null, [l])!;
	const string Home = "http://192.168.1.50:7242/";

	NocatFarm.Core.DashboardLinks.Links fresh = new("http://127.0.0.1:7242/", [Home], false, false, false, null);
	Check("phone: a fresh install needs a password and to open up, in that order",
		Todo(fresh).SequenceEqual(["set a dashboard password", "turn on Open to other devices"]), string.Join(" | ", Todo(fresh)));
	Check("phone: ...and isn't ready at home", !fresh.ReadyAtHome);

	NocatFarm.Core.DashboardLinks.Links saved = new("http://127.0.0.1:7242/", [Home], true, true, true, null, NeedsRestart: true);
	Check("phone: saved but not restarted - the restart is all that's left", Todo(saved).SequenceEqual(["restart the dashboard, so the change takes effect"]));
	Check("phone: ...and it isn't ready until it has", !saved.ReadyAtHome);

	NocatFarm.Core.DashboardLinks.Links walled = new("http://127.0.0.1:7242/", [Home], true, true, true, null, FirewallBlocks: true);
	Check("phone: the firewall is the last thing in the way", Todo(walled).SequenceEqual(["let it through Windows Firewall"]) && !walled.ReadyAtHome);

	NocatFarm.Core.DashboardLinks.Links docker = new("http://127.0.0.1:7242/", [], true, true, true, null, NeedsHomeAddress: true);
	Check("phone: in Docker it asks for NOCATFARM_HOME_ADDRESS, never a wrong address",
		Todo(docker).Count() == 1 && Todo(docker)[0].Contains("NOCATFARM_HOME_ADDRESS") && !docker.ReadyAtHome);

	NocatFarm.Core.DashboardLinks.Links ready = new("http://127.0.0.1:7242/", [Home, "http://10.0.0.5:7242/"], true, true, true, "http://203.0.113.7:7242/", RemoteOn: true);
	Check("phone: all done - nothing left, ready at home", Todo(ready).Length == 0 && ready.ReadyAtHome);

	List<NocatFarm.Core.DashboardLinks.Row> rows = NocatFarm.Core.DashboardLinks.Rows(ready, 12);
	Check("phone: rows are at home, away, this PC - with their links",
		rows.Select(static r => r.Label.English).SequenceEqual(["At home (same Wi-Fi):", "Away from home:", "On the PC itself:"])
		&& rows[0].Link == Home && rows[1].Link == "http://203.0.113.7:7242/" && rows[2].Link == "http://127.0.0.1:7242/");
	Check("phone: a working away link says to test it on mobile data", rows[1].Note.English.Contains("mobile data") && rows[0].Note.IsEmpty);

	string text = TextOf(ready);
	Check("phone: the console lists the second home address under the first", text.Contains("http://10.0.0.5:7242/") && !text.Contains("Still to do"), text.Replace(Environment.NewLine, " | "));

	NocatFarm.Core.DashboardLinks.Row awayOff = NocatFarm.Core.DashboardLinks.Rows(fresh, 12)[1];
	Check("phone: away off says how to turn it on, with the password length",
		awayOff.Link == null && awayOff.Note.ToEnglish().Contains("Open from anywhere") && awayOff.Note.ToEnglish().Contains("12+"), awayOff.Note.ToEnglish());
	Check("phone: a home link that doesn't work yet says so", NocatFarm.Core.DashboardLinks.Rows(saved, 12)[0].Note.English == "not ready yet - see below");

	NocatFarm.Core.DashboardLinks.Row asking = NocatFarm.Core.DashboardLinks.Rows(fresh with { RemoteOn = true }, 12)[1];
	NocatFarm.Core.DashboardLinks.Row refused = NocatFarm.Core.DashboardLinks.Rows(fresh with { RemoteOn = true, RemoteProblem = "your router doesn't do UPnP" }, 12)[1];
	Check("phone: away on - asking the router, then the router's answer",
		asking.Note.English.StartsWith("asking your router", StringComparison.Ordinal) && refused.Note.ToEnglish() == "not working - your router doesn't do UPnP", refused.Note.ToEnglish());

	string todoText = TextOf(fresh);
	Check("phone: the console says what's missing, one line each",
		todoText.Contains("Still to do") && todoText.Contains("- set a dashboard password") && todoText.Contains("- turn on Open to other devices"), todoText.Replace(Environment.NewLine, " | "));

	Check("said: English with the values in, whatever the language", new NocatFarm.Core.Said("{0} of {1} - {{kept}}", 3, "x").ToEnglish() == "3 of x - {kept}");

	NocatFarm.Config.GlobalConfig g = new() { WebHost = "127.0.0.1", WebPort = 7242 };
	NocatFarm.Core.DashboardLinks.Links local = NocatFarm.Core.DashboardLinks.For(g);
	Check("phone: listening on this PC only isn't open at home", !local.OpenAtHome && !local.ListensBeyondThisPc && local.Local == "http://127.0.0.1:7242/");
	g.WebHost = "0.0.0.0";
	g.WebPassword = "a-long-test-password";
	g.WebPublicAddress = "myname.duckdns.org";
	NocatFarm.Core.DashboardLinks.Links opened = NocatFarm.Core.DashboardLinks.For(g);
	Check("phone: 0.0.0.0 with a password is open at home, and a typed public address gets the port",
		opened.OpenAtHome && opened.ListensBeyondThisPc && opened.Outside == "http://myname.duckdns.org:7242/", opened.Outside ?? "null");

	// /dashboard on Telegram and Discord: the same three rows and the same list, in their own dress.
	MethodInfo tg = typeof(NocatFarm.Core.Notifier).GetMethod("DashboardHtml", BindingFlags.Static | BindingFlags.NonPublic)!;
	MethodInfo dc = typeof(NocatFarm.Core.Notifier).GetMethod("DashboardMarkdown", BindingFlags.Static | BindingFlags.NonPublic)!;
	string tgText = (string) tg.Invoke(null, null)!, dcText = (string) dc.Invoke(null, null)!;
	Check("phone: Telegram's /dashboard gives home, away, this PC and a link to tap",
		tgText.Contains("At home (same Wi-Fi):") && tgText.Contains("Away from home:") && tgText.Contains("On the PC itself:") && tgText.Contains("<a href=\"http://127.0.0.1:"), tgText.Replace("\n", " | "));
	Check("phone: Discord's /dashboard says the same", dcText.Contains("At home") && dcText.Contains("Away from home:") && dcText.Contains("- set a dashboard password") && dcText.Contains("<http://127.0.0.1:"), dcText.Replace("\n", " | "));
}

// ── tray icon: one id per copy, so a second copy can't take the running one's icon ────────────────────────
{
	MethodInfo idFor = typeof(Rng).Assembly.GetType("NocatFarm.Windows.TrayIcon")!.GetMethod("IconIdFor", BindingFlags.Static | BindingFlags.NonPublic)!;
	Guid Id(string exe) => (Guid) idFor.Invoke(null, [exe])!;
	Check("tray: two copies in different folders get different icons", Id("C:/a/nocatFarm.exe") != Id("C:/b/nocatFarm.exe"));
	Check("tray: the same copy always gets the same one (so its own leftover icon is cleaned up)", Id("C:/a/nocatFarm.exe") == Id("c:/A/NOCATFARM.EXE"));
}

// ── order: named missions come before "finish the campaign" (Call of Duty names them, it doesn't number them) ──
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type pacer = typeof(Rng).Assembly.GetType("NocatFarm.Modules.AchievementPacer")!;
	NocatFarm.Core.Achievement A(string name, string display, string desc, bool unlocked = false) =>
		new() { Name = name, Display = display, Description = desc, StatId = 1, Bit = 0, Unlocked = unlocked, Protected = false };
	bool EndHeld(NocatFarm.Core.Achievement a, List<NocatFarm.Core.Achievement> all) => (bool) pacer.GetMethod("StoryEndBlocked", S)!.Invoke(null, [a, all])!;
	var finish = A("spfinish", "Time for Pints", "Finish the Campaign on any difficulty.");
	var feud = A("contract", "Talent Acquisition", "Complete Blood Feud in Campaign on any difficulty");
	var prestige = A("dynasty", "New Dynasty", "Enter Prestige in Black Ops 7");
	var kills = A("deathrow", "Death Row", "Kill 12 enemies while descending in the panopticon in 'Operation 627'");
	Check("order: 'finish the campaign' waits for a locked named mission", EndHeld(finish, [feud, finish]));
	Check("order: ...and goes once it's done", !EndHeld(finish, [A("contract", "Talent Acquisition", "Complete Blood Feud in Campaign on any difficulty", unlocked: true), finish]));
	Check("order: multiplayer and side challenges don't hold the ending", !EndHeld(finish, [prestige, kills, finish]));
}

// ── order, checked against 77 real games' achievement lists (the wording below is theirs) ─────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type pacer = typeof(Rng).Assembly.GetType("NocatFarm.Modules.AchievementPacer")!;
	NocatFarm.Core.Achievement A(string display, string desc, double? pct = null, bool unlocked = false) =>
		new() { Name = display.ToUpperInvariant().Replace(' ', '_'), Display = display, Description = desc, StatId = 1, Bit = 0, Unlocked = unlocked, Protected = false, GlobalPercent = pct };
	bool Held(NocatFarm.Core.Achievement a, params NocatFarm.Core.Achievement[] others) {
		List<NocatFarm.Core.Achievement> all = [a, .. others];
		return new[] { "TierBlocked", "DifficultyBlocked", "StoryEndBlocked", "VariantBlocked" }.Any(r => (bool) pacer.GetMethod(r, S)!.Invoke(null, [a, all])!);
	}

	// A collection's label is a name: Halo 4's never waits for Halo 2's, and the 4 is not a rung.
	var h4 = A("You Had it Coming", "Halo 4: Beat the par score on every Halo 4 level.", 1.9);
	var h2 = A("Arcade Owner", "Halo 2: Beat the par score on every Halo 2 level.", 0.9);
	Check("order: 'Halo 4: ...every Halo 4 level' doesn't wait for Halo 2's (a label is a name, not a rung)", !Held(h4, h2));
	var h2easy = A("Big Green Style", "Halo 2: Complete every level of the game on Easy difficulty.", 19.6);
	var h2normal = A("Warrior", "Halo 2: Complete every level of the game on Normal difficulty.", 17.2);
	var h3easy = A("Training Wheels", "Halo 3: Complete every level of the game on Easy difficulty.", 19.4);
	Check("order: ...but Halo 2 on Normal still waits for Halo 2 on Easy", Held(h2normal, h2easy) && !Held(h3easy, h2easy, h2normal));
	var ch3 = A("Chapter III: The Descent", "Complete Chapter III: The Descent", 40);
	var ch4 = A("Chapter IV: The Bridge", "Complete Chapter IV: The Bridge", 38);
	Check("order: 'Chapter IV: The Bridge' is a step with a title, not a label - it waits for Chapter III, and holds the ending",
		Held(ch4, ch3) && !Held(ch3, ch4) && Held(A("The End", "Finish the story", 30), ch4));
	var me1 = A("Medal of Honor", "ME1: Complete the game on any difficulty", 38.4);
	Check("order: ME2's steps don't hold ME1's ending", !Held(me1, A("The Archangel", "ME2: Successfully recruit Archangel", 35.8), A("Suicide Mission", "ME2: Complete Horizon", 30)));

	// A player's level is not a story level.
	var doom = A("Knee-Deep in the Dead", "Complete the campaign on 'I'm Too Young to Die', 'Hurt Me Plenty', 'Ultra Violence', or 'Nightmare'.", 32.3);
	Check("order: 'Reach Level 5 in Multiplayer' doesn't hold 'Complete the campaign'", !Held(doom, A("Combat tested", "Reach Level 5 in Multiplayer", 8.9)));
	Check("order: 'Upgrade any weapon to level 10' doesn't hold 'Complete the game'", !Held(me1, A("Gunsmith", "ME3: Upgrade any weapon to level 10.", 4.3)));
	var tomb = A("A Survivor Is Born", "Complete the game.", 42.2);
	Check("order: 'Reach level 60 in multiplayer' doesn't hold 'Complete the game'", !Held(tomb, A("True Commitment", "Reach level 60 in multiplayer.", 1.8)));
	Check("order: ...while 'Complete Level 3' still does", Held(tomb, A("Level Three", "Complete Level 3", 50)));

	// A time limit counts down - and two ladders that disagree must not hold each other for ever.
	var sr1 = A("Speedrun 1", "Complete the game in under 10 hours", 6.6);
	var sr2 = A("Speedrun 2", "Complete the game in under 5 hours", 5.4);
	Check("order: 'in under 5 hours' waits for 'in under 10 hours', never the other way (Hollow Knight held both for ever)", Held(sr2, sr1) && !Held(sr1, sr2));
	var fast15 = A("Power Trip", "Capture the final control point within 15 seconds of your team capturing the previous control point.", 4.7);
	var fast5 = A("Five the Fast Way", "Capture the final control point within five seconds of your team capturing the previous control point.", 7.9);
	Check("order: 'within five seconds' waits for 'within 15 seconds'", Held(fast5, fast15) && !Held(fast15, fast5));

	// Named steps of the story hold its ending; side content, co-op and DLC don't.
	var ilos = A("Meritorious Service Medal", "ME1: Complete Ilos", 38.3);
	Check("order: 'ME1: Complete Ilos' holds 'ME1: Complete the game'", Held(me1, ilos) && !Held(me1, ilos with { Unlocked = true }));
	var cuphead = A("Souls Saved", "Complete the game on Normal", 17.6);
	Check("order: 'Complete the Casino' holds 'Complete the game on Normal'", Held(cuphead, A("Casino Night", "Complete the Casino", 18.6)));
	var witcher = A("Passed the Trial", "Finish the game on any difficulty.", 22.0);
	Check("order: a side contract, a trade quest, a co-op mission don't hold the ending",
		!Held(witcher, A("Ashes to Ashes", "Complete the contract on Therazane.", 19.7))
		&& !Held(A("Destiny", "Complete the Game", 37.5), A("Mark of the Trader", "Complete the Trade Sequence Quest", 21.8))
		&& !Held(A("Case Closed", "Complete the Campaign on any difficulty", 1.5), A("A Familiar Face", "Complete Exposure in Co-Op Campaign", 0.7)));
	Check("order: 'Complete all Safehouse Puzzles in Campaign' is a collection, not a step", !Held(A("Never Bury Your Enemies Alive", "Complete the campaign", 1.9), A("The Puzzles, Mason", "Complete all Safehouse Puzzles in Campaign", 1.2)));
	Check("order: 'Complete all Side Missions' is not the ending", !Held(A("Friendly Neighbourhood Spider-Man", "Complete all Side Missions", 27.1), A("Mission", "Complete Mission 5", 40)));
	Check("order: a challenge run isn't held by a named step (as likely a DLC chapter)", !Held(A("Just Get Me Outta Here", "Complete the game within 4 hours.", 7.1), A("Sleepless in Dulvey", "Complete Night Terror.", 2.9)));

	// Something from another mode, much rarer than the ending, or under the lowest rarity floor, is no step of the story:
	// Call of Duty's campaign ending waited for ever on these.
	var pints = A("Time for Pints", "Finish the Campaign on any difficulty.", 5.3);
	var campaign = A("Never Bury Your Enemies Alive", "Complete the campaign", 1.9);
	var mwz = A("The End?", "Complete Act III in MWZ", 0.7);
	var zone = A("Checking Boxes", "Complete 10 Assignments in Zone IV in Co-Op Campaign", 0.2);
	Check("order: a zombies act and a co-op zone never hold the campaign's ending", !Held(pints, mwz, zone) && !Held(campaign, mwz, zone));
	Check("order: ...while the campaign's own missions still do", Held(pints, A("Talent Acquisition", "Complete Blood Feud in Campaign on any difficulty", 2.9)));

	// Ladders: all is the top rung, a numeral at the end of a name is a tier, punctuation doesn't split one.
	var c10 = A("Off the Beaten Path", "Find 10 Collectibles", 30.5);
	var c25 = A("Collector", "Find 25 Collectibles", 4.6);
	var call = A("Every Nook and Cranny", "Find All Collectibles", 2.2);
	Check("order: 'Find All Collectibles' waits for 'Find 25 Collectibles'", Held(call, c25) && Held(call, c10) && !Held(c25, call) && Held(c25, c10));
	Check("order: 'Tome of Curses III' waits for 'Tome of Curses II'", Held(A("Tome of Curses III", "Sell the Tome of Curses III.", 17.6), A("Tome of Curses II", "Sell the Tome of Curses II.", 17.5)));
	Check("order: 'Win 142 rounds' waits for 'Win 139 Rounds.'", Held(A("Competitive Spirit", "Win 142 rounds", 5.4), A("Stand and Deliver", "Win 139 Rounds.", 5.2)));
	Check("order: 'Clear stage 1-10 ...!' waits for 'Clear stage 1-1 ....'",
		Held(A("TBH, This Game Slaps", "Clear stage 1-10 for the first time!", 100), A("Hero Out of the Taskbar", "Clear stage 1-1 for the first time.", 100)));
	Check("order: 'The D20' is a name, not rung 20 after 'The D6'", !Held(A("The D20", "The D20", 19), A("The D6", "The D6", 31.9)));

	// The same thing, harder, waits for the plain one.
	var core = A("Containment", "Contain the Citadel core.", 7.6);
	Check("order: 'Contain the Citadel core without killing any stalkers' waits for 'Contain the Citadel core'", Held(A("Pacifist", "Contain the Citadel core without killing any stalkers.", 2.0), core));
	Check("order: 'Complete the Game in Under 4 Hours' waits for 'Complete the Game'", Held(A("Look at the Time", "Complete the Game in Under 4 Hours", 1.7), A("Destiny", "Complete the Game", 37.5)));
	Check("order: 'Halo 2: Complete Delta Halo without entering a vehicle' waits for 'Halo 2: Complete Delta Halo.'",
		Held(A("Just Like Old Times", "Halo 2: Complete Delta Halo without entering a vehicle on Heroic or Legendary.", 2.4), A("Delta Halo", "Halo 2: Complete Delta Halo.", 24.8)));
	Check("order: ...but a region's 'all named locations' doesn't wait for the whole world's",
		!Held(A("Highlands Explorer", "Discovered all named locations in The Highlands, Thousand Cuts, and Wildlife Exploitation Preserve.", 8.0), A("World Traveler", "Discovered all named locations.", 3.8)));
	Check("order: a DLC's ending waits for the game's", Held(A("WHISTLEBLOWER", "Finish the Whistleblower DLC", 12.1), A("PUNISHED", "Finish the game", 25.2)) && !Held(A("PUNISHED", "Finish the game", 25.2), A("WHISTLEBLOWER", "Finish the Whistleblower DLC", 12.1)));
	Check("order: prestige waits for the level it takes", Held(A("Return of the King", "Enter Prestige 1", 5.7), A("The First Step", "Reach Level 55", 9.6)));

	// Difficulty names and medals from real games.
	Check("order: Titanfall 2's 'on Master' waits for 'on Regular'", Held(A("Legendary Pilot", "Complete the Campaign on Master", 4.0), A("Certified Pilot", "Complete the Campaign on Regular", 42.5)));
	Check("order: a gold medal waits for silver", Held(A("KILLING SPREE", "Earn a gold medal on every official survival map.", 1.5), A("LIKE LAMBS TO THE SLAUGHTER", "Earn a silver medal on every official survival map.", 1.4)));

	// Not endings at all.
	MethodInfo isEnding = pacer.GetMethod("IsEnding", S)!;
	bool Ending(string desc) => (bool) isEnding.Invoke(null, [A("x", desc)])!;
	Check("order: 'Play a complete game on 2Fort...' and 'Complete the game intro' are not endings",
		!Ending("Play a complete game on 2Fort, Dustbowl, Granary, Gravel Pit, Hydro, and Well (CP).") && !Ending("Complete the game intro by liberating Dutch’s island (Solo Campaign only).")
		&& Ending("Complete every level of the game on Easy difficulty.") && Ending("Witnessed the Epilogue"));
}

// ── import: an idler unzipped a folder or two down is found (the Docker /import mount works the same way) ──────
{
	string root = Path.Combine(Path.GetTempPath(), "nf-walk-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(root, "asf", "config"));
	File.WriteAllText(Path.Combine(root, "asf", "config", "ASF.json"), "{}");
	Type files = typeof(NocatFarm.Config.AsfImport).Assembly.GetType("NocatFarm.Config.ImportFiles")!;
	MethodInfo folders = files.GetMethod("Folders", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
	List<string> hits = [.. (IEnumerable<string>) folders.Invoke(null, [new[] { root }, 2, (Func<string, bool>) (d => File.Exists(Path.Combine(d, "config", "ASF.json")))])!];
	Check("import: an ASF folder one level down is found", hits.Count == 1 && hits[0].EndsWith("asf", StringComparison.Ordinal), string.Join(", ", hits) + " attrs=" + File.GetAttributes(Path.Combine(root, "asf")));
	// Linux: a lower-case "asf" next to the usual "ASF" guess must survive the de-duplication.
	var cmp = (StringComparer) files.GetProperty("PathComparer")!.GetValue(null)!;
	Check("import: paths differing only in case are different folders off Windows", OperatingSystem.IsWindows() ? cmp.Equals("/a/ASF", "/a/asf") : !cmp.Equals("/a/ASF", "/a/asf"));
	try { Directory.Delete(root, true); } catch { }
}

// ── sending items at a set hour ──────────────────────────────────────────────────────────────────────────────
{
	MethodInfo nextDue = typeof(NocatFarm.Modules.Sender).GetMethod("NextDue", BindingFlags.NonPublic | BindingFlags.Static)!;
	DateTime Due(DateTime now, int hours, int at) => (DateTime) nextDue.Invoke(null, [now, hours, at, 10, 0.0])!;
	DateTime nine = new(2026, 9, 29, 21, 0, 0);
	Check("send: every 24h around 4 - tonight at 21:00 it's tomorrow 04:10", Due(nine, 24, 4) == new DateTime(2026, 9, 30, 4, 10, 0));
	Check("send: every 24h around 22 - at 21:00 it's in an hour, today", Due(nine, 24, 22) == new DateTime(2026, 9, 29, 22, 10, 0));
	Check("send: every 48h around 4 - skips a day", Due(nine, 48, 4) == new DateTime(2026, 10, 1, 4, 10, 0));
	Check("send: every 6h with no hour set - 6 hours from now", Due(nine, 6, -1) == nine.AddHours(6));
}

// SETTINGSCOUNT
Console.WriteLine($"settings: {NocatFarm.Config.Settings.Global.Count} global ({NocatFarm.Config.Settings.Global.Count(d => !d.Advanced)} basic), {NocatFarm.Config.Settings.Bot.Count} per account ({NocatFarm.Config.Settings.Bot.Count(d => !d.Advanced)} basic)");
Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
return fails;
