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
	=> await (Task<(bool?, string)>) gifts.GetMethod("WhatIsAsync", Priv)!.Invoke(null, [package, "tests", ct])!;

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
// Live, so up to three tries a few seconds apart: one blank answer from the store isn't a broken reader.
List<string> giveaways = [];
for (int attempt = 0; (attempt < 3) && (giveaways.Count == 0); attempt++) {
	if (attempt > 0) {
		await Task.Delay(5000);
		fg.GetField("_giveaways", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, (DateTime.MinValue, new List<string>()));   // not the cached empty answer
	}
	giveaways = await (Task<List<string>>) fg.GetMethod("StoreGiveawaysAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [CancellationToken.None])!;
}
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

		Put(Path.Combine(asf, "aes.json"), $$"""{ "SteamLogin": "aeslogin", "SteamPassword": "{{AsfAes("aes-pass-ü1", "ArchiSteamFarm")}}", "PasswordFormat": 1, "TradingPreferences": 3, "AcceptGifts": true }""");
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
		// What ASF did, copied - and a setting its file leaves out is ASF's default (off), not nocat.farm's.
		Check("import asf: donations, card swaps and gifts copied when ASF has them on", Acct(asfScan, "aes").Config is { AcceptDonations: true, AcceptFairCardSwaps: true, AcceptGifts: true, AcceptGiftedGames: true });
		Check("import asf: ...and off when ASF's file leaves them out, like ASF", main.Config is { AcceptDonations: false, AcceptFairCardSwaps: false, AcceptGifts: false, AcceptGiftedGames: false });
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
	Check("import: paths differing only in case are different folders on Linux (the same on Windows and macOS)",
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? cmp.Equals("/a/ASF", "/a/asf") : !cmp.Equals("/a/ASF", "/a/asf"));
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

// ── card farming sittings: one that runs past midnight isn't cut off when the day turns over ──────────────────
{
	Type cf = typeof(NocatFarm.Modules.CardFarmer);
	MethodInfo roll = cf.GetMethod("RollSittings", BindingFlags.NonPublic | BindingFlags.Static)!;
	MethodInfo on = cf.GetMethod("SittingsOn", BindingFlags.NonPublic | BindingFlags.Static)!;
	List<(DateTime From, DateTime To)> Roll(DateTime d) => (List<(DateTime From, DateTime To)>) roll.Invoke(null, ["kylro", d, 12])!;
	List<(DateTime From, DateTime To)> On(DateTime d) => (List<(DateTime From, DateTime To)>) on.Invoke(null, ["kylro", d, 12])!;
	DateTime day = new(2026, 9, 1);
	(DateTime From, DateTime To) late = default;

	for (int i = 0; (i < 400) && (late == default); i++) {
		late = Roll(day).FirstOrDefault(w => w.To > day.AddDays(1));

		if (late == default) {
			day = day.AddDays(1);
		}
	}

	Check("sittings: the same account and date roll the same day", Roll(day).SequenceEqual(Roll(day)));
	Check("sittings: some days run past midnight", late != default, $"{late.From:MM-dd HH:mm}-{late.To:MM-dd HH:mm}");
	List<(DateTime From, DateTime To)> next = On(day.AddDays(1));
	Check("sittings: the next day keeps the rest of last night's sitting", next.Contains(late), string.Join(", ", next.Take(3).Select(w => $"{w.From:dd HH:mm}-{w.To:dd HH:mm}")));
	Check("sittings: and nothing else of yesterday's", next.All(w => w.To > day.AddDays(1)) && (next.Count(w => w.From < day.AddDays(1)) == 1));
}

// ── lifetime/history minutes: a machine that slept doesn't wake up with the night credited as played ───────────
{
	MethodInfo credit = typeof(NocatFarm.Modules.Heartbeat).GetMethod("Creditable", BindingFlags.NonPublic | BindingFlags.Static)!;
	double Credit(DateTime last, DateTime now) => (double) credit.Invoke(null, [last, now])!;
	DateTime t = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
	Check("played minutes: a normal 20s tick counts", Math.Abs(Credit(t, t.AddSeconds(20)) - (1 / 3.0)) < 1e-9);
	Check("played minutes: the first tick counts nothing", Credit(DateTime.MinValue, t) == 0);
	Check("played minutes: six hours asleep count nothing", Credit(t, t.AddHours(6)) == 0);
	Check("played minutes: a clock set back counts nothing", Credit(t, t.AddMinutes(-5)) == 0);
}

// ── hours banked: every running game counts, the way Steam credits them - 32 games for an hour is 32 hours ─────
{
	string hrsAcct = "harness-hours-" + Guid.NewGuid().ToString("N")[..6];
	uint[] apps = [.. Enumerable.Range(1, 32).Select(static i => (uint) (440 + i))];
	for (int i = 0; i < 12; i++) {
		NocatFarm.Core.Lifetime.Add(hrsAcct, 5, apps.Length);   // an hour, 32 games
		NocatFarm.Core.History.AddPlay(hrsAcct, 5, apps);
	}
	Check("hours banked: the clock total is an hour", NocatFarm.Core.Lifetime.For(hrsAcct) == 60, NocatFarm.Core.Lifetime.For(hrsAcct).ToString());
	Check("hours banked: the game total is 32 hours", NocatFarm.Core.Lifetime.GamesFor(hrsAcct) == 32 * 60, NocatFarm.Core.Lifetime.GamesFor(hrsAcct).ToString());
	Check("hours banked: the day's history banks 32 hours", Math.Abs(NocatFarm.Core.History.MinutesOver(1, [hrsAcct]) - (32 * 60)) < 0.01, NocatFarm.Core.History.MinutesOver(1, [hrsAcct]).ToString());
	(double clock, double banked) = NocatFarm.Core.History.Recent(2, hrsAcct);
	Check("hours banked: history keeps the clock time beside it", (Math.Abs(clock - 60) < 0.01) && (Math.Abs(banked - (32 * 60)) < 0.01), $"{clock} / {banked}");
	NocatFarm.Core.Lifetime.Add(hrsAcct, 5, 0);   // a custom name on its own - no real game
	Check("hours banked: a custom name alone banks no game time", NocatFarm.Core.Lifetime.GamesFor(hrsAcct) == 32 * 60);
	var day = new NocatFarm.Core.History.Day { Minutes = 90 };
	Check("hours banked: a day with no per-game record still counts its clock time", NocatFarm.Core.History.Banked(day) == 90);
}

// ── human mode's day: a machine that slept doesn't bank the night as played ────────────────────────────────────
{
	MethodInfo skip = typeof(NocatFarm.Modules.HumanMode).GetMethod("SkipGap", BindingFlags.NonPublic | BindingFlags.Static)!;
	DateTime Skip(DateTime bankedTo, DateTime last, DateTime now) => (DateTime) skip.Invoke(null, [bankedTo, last, now])!;
	DateTime t = new(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc);
	Check("human bank: ticking along, the cursor stays", Skip(t, t.AddSeconds(40), t.AddSeconds(60)) == t);
	Check("human bank: a session just started (cursor newer than the last bank) isn't a gap", Skip(t, t.AddHours(-2), t.AddSeconds(20)) == t);
	Check("human bank: eight hours asleep since the last tick - none of it banked", Skip(t, t.AddSeconds(30), t.AddHours(8)) == t.AddHours(8));
}

// ── sign-in: an account whose authenticator secret is here answers with a code, not a wait for the phone ────────
{
	var guard = new NocatFarm.Core.ConsoleGuard("harness", "KDHC3rsY8+CmiswnXJcE5e5dRfd=");
	Check("sign-in: with the secret here it asks for a code rather than waiting on the phone", !guard.AcceptDeviceConfirmationAsync().Result);
}

// ── gates: a reconnect starts a fresh wait, even if nothing asked while it was signed out ──────────────────────
{
	var cfg = new NocatFarm.Config.BotConfig { LegitMode = true, QuietDelayMinMinutes = 30, QuietDelayMaxMinutes = 30 };
	var bot = new NocatFarm.Core.Bot("harness-gate", cfg);
	void SetProp(string name, object? value) => typeof(NocatFarm.Core.Bot).GetProperty(name)!.SetValue(bot, value);
	SetProp("State", NocatFarm.Core.BotState.Online);
	SetProp("OnlineSince", DateTime.UtcNow.AddHours(-2));
	var gate = NocatFarm.Modules.HumanGate.Quiet(bot);
	FieldInfo openAt = typeof(NocatFarm.Modules.HumanGate).GetField("_openAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
	Check("gate: shut for its wait after signing in", !gate.Open);
	openAt.SetValue(gate, DateTime.UtcNow.AddSeconds(-1));
	Check("gate: open once the wait is over", gate.Open);
	SetProp("OnlineSince", DateTime.UtcNow);   // dropped and signed back in, with nobody asking in between
	Check("gate: shut again after a reconnect", !gate.Open);
	await bot.DisposeAsync();
}

// ── human mode's day: "at most" means at most, and 'wake' in the morning starts today's plan ───────────────────
{
	var cfg = new NocatFarm.Config.BotConfig { LegitMode = true, MaxSignOutsPerDay = 1, MealBreaksPerDay = 2, WeekdayHours = 4, WeekendHours = 4, DayOffChancePct = 0 };
	string name = "harness-human-" + Guid.NewGuid().ToString("N")[..6];
	var bot = new NocatFarm.Core.Bot(name, cfg);
	var human = new NocatFarm.Modules.HumanMode(bot);
	Type ht = typeof(NocatFarm.Modules.HumanMode);
	FieldInfo F(string f) => ht.GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!;
	MethodInfo roll = ht.GetMethod("RollNewDayIfNeeded", BindingFlags.NonPublic | BindingFlags.Instance)!;
	int worstSignOuts = 0, worstMeals = 0;

	for (int i = 0; i < 40; i++) {
		NocatFarm.Modules.HumanDay.Forget(name);
		F("_dayStamp").SetValue(human, -1);
		roll.Invoke(human, [false]);
		worstSignOuts = Math.Max(worstSignOuts, (int) F("_signOutCap").GetValue(human)!);
		worstMeals = Math.Max(worstMeals, (int) F("_mealCap").GetValue(human)!);
	}

	Check("human day: 'Drop offline at most 1' is at most 1", worstSignOuts == 1, $"{worstSignOuts}");
	Check("human day: 'Meals a day 2' is at most 2", worstMeals <= 2, $"{worstMeals}");

	// Last night's plan still in force (yesterday's stamp, wake time late in the day, target already met).
	F("_dayStamp").SetValue(human, DateTime.Now.AddDays(-1).DayOfYear);
	F("_wakeMinuteOfDay").SetValue(human, (23 * 60) + 59);
	F("_playedMinutesToday").SetValue(human, 999);
	F("_targetMinutes").SetValue(human, 60);
	F("_bedHour").SetValue(human, 0);          // last night's bed long gone, so 'wake' at any hour is the morning
	F("_bedMinute").SetValue(human, 0);
	F("_bedIsTomorrow").SetValue(human, false);
	human.WakeNow();
	int nowMin = (int) (DateTime.Now - DateTime.Now.Date).TotalMinutes;
	Check("wake: today's plan is rolled, not last night's", ((int) F("_dayStamp").GetValue(human)! == DateTime.Now.DayOfYear) && ((int) F("_playedMinutesToday").GetValue(human)! == 0));
	Check("wake: and it's up from now", (int) F("_wakeMinuteOfDay").GetValue(human)! <= nowMin);

	// 'wake' in the small hours is one more sitting on last night, not a new day started at 1am.
	MethodInfo lastNight = ht.GetMethod("StillLastNight", BindingFlags.NonPublic | BindingFlags.Static)!;
	bool LastNight(DateTime now, DateTime bed, DateTime earliest) => (bool) lastNight.Invoke(null, [now, bed, earliest])!;
	DateTime d = new(2026, 9, 29);
	Check("wake at 03:00, bed was 01:30: still last night", LastNight(d.AddHours(3), d.AddHours(1.5), d.AddHours(9.5)));
	Check("wake at 00:10, bed was 23:40: still last night", LastNight(d.AddMinutes(10), d.AddMinutes(-20), d.AddHours(9.5)));
	Check("wake at 07:00, bed was 01:30: that's the morning", !LastNight(d.AddHours(7), d.AddHours(1.5), d.AddHours(9.5)));
	Check("wake at 10:00: the morning", !LastNight(d.AddHours(10), d.AddHours(1.5), d.AddHours(9.5)));

	// Up late after a 'wake' with the day's hours played: one sitting to the time it said, not 5-minute ones.
	MethodInfo length = ht.GetMethod("SessionLength", BindingFlags.NonPublic | BindingFlags.Instance)!;
	F("_stayUpUntil").SetValue(human, DateTime.Now.AddMinutes(100));
	F("_playedMinutesToday").SetValue(human, 999);
	F("_targetMinutes").SetValue(human, 60);
	int upLate = (int) length.Invoke(human, [730u, 730u])!;
	Check("wake after the day's played: the sitting runs to the stay-up time", upLate >= 98 && upLate <= 100, $"{upLate}m");
	F("_stayUpUntil").SetValue(human, DateTime.MinValue);

	// The main game's number is its share of the TIME: main sittings are longer, so it's picked less often to match.
	MethodInfo mainWeight = ht.GetMethod("MainWeight", BindingFlags.NonPublic | BindingFlags.Static)!;
	int w = (int) mainWeight.Invoke(null, [30, 70, 30, 150, int.MaxValue])!;
	F("_wakeMinuteOfDay").SetValue(human, 0);
	F("_bedHour").SetValue(human, 23);
	F("_bedMinute").SetValue(human, 59);
	F("_bedIsTomorrow").SetValue(human, true);   // bed a day and more away, so it caps nothing
	F("_targetMinutes").SetValue(human, 1_000_000);
	F("_playedMinutesToday").SetValue(human, 0);
	F("_otherBudget").SetValue(human, 1_000_000);
	F("_otherPlayed").SetValue(human, 0);
	F("_firstSessionOfDay").SetValue(human, false);
	Random pick = new(7);
	double mainMin = 0, allMin = 0;

	for (int i = 0; i < 40_000; i++) {
		bool isMain = pick.Next(w + 30) < w;
		int m = (int) length.Invoke(human, [isMain ? 730u : 440u, 730u])!;
		allMin += m;
		mainMin += isMain ? m : 0;
	}

	Check("main game 70%: about 70% of the time played, not ~80%", Math.Abs((mainMin / allMin) - 0.70) < 0.015, $"{mainMin / allMin:P1}, weight {w} vs 30");

	// A main-game-only day has no side-game allowance, first sitting included.
	MethodInfo sideOk = ht.GetMethod("SideGameAllowed", BindingFlags.NonPublic | BindingFlags.Static)!;
	Check("main-only day: no side game, not even the first sitting", !(bool) sideOk.Invoke(null, [0, 0])!);
	Check("a mixed day: side games while the allowance lasts", (bool) sideOk.Invoke(null, [10, 60])! && !(bool) sideOk.Invoke(null, [60, 60])!);

	// The hunt game's sitting stops inside "Hunt at most, hours a day".
	cfg.BoostHoursPerDay = 1;
	var hunter = new NocatFarm.Modules.AchievementBoost(bot);
	Type bt = typeof(NocatFarm.Modules.AchievementBoost);
	bt.GetProperty("HuntTarget")!.SetValue(hunter, 550u);
	bt.GetField("_today", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hunter, DateTime.Today);
	bt.GetField("_todayMinutes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hunter, 40.0);
	bot.AddModule(hunter);
	int huntMost = 0;

	for (int i = 0; i < 500; i++) {
		huntMost = Math.Max(huntMost, (int) length.Invoke(human, [550u, 730u])!);
	}

	Check("hunt: 20 minutes left of the hour a day - no sitting on it runs past that", huntMost <= 20, $"longest {huntMost}m");
	NocatFarm.Modules.HumanDay.Forget(name);
	await bot.DisposeAsync();
}

// ── login cooldown: it's everybody's, so stopping the account that hit it doesn't end it ───────────────────────
{
	Type lim = typeof(NocatFarm.Core.Limiters);
	var latch = (SemaphoreSlim) lim.GetField("LoginCooldownLatch", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
	using CancellationTokenSource account = new();
	Task serve = NocatFarm.Core.Limiters.ServeLoginCooldownAsync(account.Token);
	bool returned = await Task.WhenAny(serve, Task.Delay(10_000)) == serve;
	account.Cancel();   // that account is stopped
	await Task.Delay(200);
	Check("login cooldown: the account that hit it goes back to waiting its turn", returned);
	Check("login cooldown: still in force after that account stops", latch.CurrentCount == 0);
}

// ── rep4rep: a hold that runs out doesn't wipe the last 24h's comments (a short hold would double the day) ────
{
	var state = new NocatFarm.Rep4Rep.Rep4RepState { Strikes = 2, BlockedUntil = DateTime.UtcNow.AddHours(1).Ticks };
	state.RecordPost("task-1");
	state.ResetForFreshStart();
	Check("rep4rep hold: strikes and blocks go", (state.Strikes == 0) && !state.IsBlocked);
	Check("rep4rep hold: the day's comments still count, and a posted task isn't posted again", (state.PostsInLast24h() == 1) && state.HasPostedTask("task-1"));
}

// ── settings: on/off in any casing, and a word that's neither is refused rather than read as off ──────────────
{
	var cfg = new NocatFarm.Config.BotConfig();
	NocatFarm.Config.SettingDef def = NocatFarm.Config.Settings.FindBot("Rep4Rep")!;
	Check("set: ON is on", (NocatFarm.Config.Settings.Apply(cfg, def, "ON") == null) && cfg.Rep4Rep);
	Check("set: No is off", (NocatFarm.Config.Settings.Apply(cfg, def, "No") == null) && !cfg.Rep4Rep);
	Check("set: true/false still work", (NocatFarm.Config.Settings.Apply(cfg, def, "true") == null) && cfg.Rep4Rep && (NocatFarm.Config.Settings.Apply(cfg, def, "False") == null) && !cfg.Rep4Rep);
	cfg.Rep4Rep = true;
	Check("set: 'enabled' is refused and changes nothing", (NocatFarm.Config.Settings.Apply(cfg, def, "enabled") != null) && cfg.Rep4Rep);
}

// ── updates: 'update skip' after 'update accept' stops the queued install ─────────────────────────────────────
{
	string? skippedBefore = NocatFarm.Core.UpdateCheck.Skipped;
	NocatFarm.Core.UpdateCheck.Skipped = "v99.9.9";
	typeof(NocatFarm.Core.UpdateCheck).GetProperty("Queued")!.SetValue(null, "v99.9.9");
	NocatFarm.Core.UpdateCheck.QueuedInstallIfDue(null!);
	// Only Windows updates itself; elsewhere nothing is ever queued, so there's nothing to drop.
	Check("update skip: a skipped version waiting to install is dropped", !OperatingSystem.IsWindows() || (NocatFarm.Core.UpdateCheck.Queued == null));
	NocatFarm.Core.UpdateCheck.Skipped = skippedBefore;
}

// ── settings file: fixed after a bad edit, it saves again ──────────────────────────────────────────────────────
{
	string path = Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "nocatFarm.json");
	string? before = File.Exists(path) ? File.ReadAllText(path) : null;
	File.WriteAllText(path, "{ \"WebPort\": 7242, ");
	NocatFarm.Config.ConfigStore.LoadGlobal();
	bool brokenSeen = NocatFarm.Config.ConfigStore.GlobalBroken;
	File.WriteAllText(path, "{ \"WebPort\": 7242 }");
	NocatFarm.Config.GlobalConfig fixedCfg = NocatFarm.Config.ConfigStore.LoadGlobal();
	Check("settings file: a broken one is noticed", brokenSeen);
	Check("settings file: once it loads again, saving works again", !NocatFarm.Config.ConfigStore.GlobalBroken && NocatFarm.Config.ConfigStore.SaveGlobal(fixedCfg));

	if (before != null) {
		File.WriteAllText(path, before);
	} else {
		File.Delete(path);
	}

	try { File.Delete(path + ".broken"); } catch { }
}

// ── Discord notifications: a busy batch is split under Discord's 6000 characters a message ─────────────────────
{
	Type notifier = typeof(NocatFarm.Core.Notifier);
	Type blockT = notifier.GetNestedType("Block", BindingFlags.NonPublic)!;
	object cards = NocatFarm.Topic.Cards;
	var blocks = (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(blockT))!;

	for (int i = 0; i < 6; i++) {
		blocks.Add(Activator.CreateInstance(blockT, [cards, $"acct{i}", Enumerable.Range(0, 15).Select(n => new string('x', 80)).ToList()])!);
	}

	var chunks = (IEnumerable) notifier.GetMethod("EmbedChunks", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [blocks])!;
	List<int> sizes = [.. chunks.Cast<IList>().Select(static c => c.Count)];
	Check("discord: six 15-line blocks go as more than one message", sizes.Count > 1 && sizes.Sum() == 6, string.Join("+", sizes));
}

// ── inventory value: an error page isn't read as "holds nothing", and the fullest inventories come first ───────
{
	MethodInfo parse = typeof(NocatFarm.Core.InventoryValue).GetMethod("ParseContexts", BindingFlags.NonPublic | BindingFlags.Static)!;
	object? ParseInv(string html) => parse.Invoke(null, [html]);
	Check("inventory: an error page is 'couldn't read', not an empty inventory", ParseInv("<html>Sorry! An error was encountered</html>") == null);
	Check("inventory: an empty inventory is empty", ParseInv("var g_rgAppContextData = [];\n") is IList { Count: 0 });
	string invPage = "var g_rgAppContextData = {\"440\":{\"name\":\"TF2\",\"rgContexts\":{\"2\":{\"asset_count\":3}}},\"730\":{\"name\":\"CS2\",\"rgContexts\":{\"2\":{\"asset_count\":900}}}};\n";
	var list = ((IEnumerable) ParseInv(invPage)!).Cast<object>().Select(static t => t.ToString()!).ToList();
	Check("inventory: the fullest first", list.Count == 2 && list[0].Contains("730", StringComparison.Ordinal), string.Join(" | ", list));
}

// ── dashboard: saving settings doesn't put back the app's own fields from the page's old copy ─────────────────
{
	var current = new NocatFarm.Config.GlobalConfig { Theme = "dark", TutorialDone = true, AccountOrder = ["b", "a"], Rep4RepHoldUntil = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
	var sent = new NocatFarm.Config.GlobalConfig { Theme = "light", TutorialDone = false, AccountOrder = ["a", "b"], Rep4RepHoldUntil = null, Language = "de" };
	typeof(NocatFarm.Web.WebHost).GetMethod("KeepServerFields", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [sent, current]);
	Check("dashboard save: theme, tutorial, account order and a hold stay as the app has them",
		sent.Theme == "dark" && sent.TutorialDone && sent.AccountOrder.SequenceEqual(["b", "a"]) && sent.Rep4RepHoldUntil == current.Rep4RepHoldUntil);
	Check("dashboard save: a setting is still changed", sent.Language == "de");
}

// ── dashboard sign-in: behind a proxy, the address the proxy saw - not the one the visitor wrote ──────────────
{
	var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
	ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
	ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 203.0.113.9";
	var signer = ((string Ip, bool ThisPc)) typeof(NocatFarm.Web.WebHost).GetMethod("WhoIsSigningIn", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [ctx])!;
	Check("sign-in lockout: the proxy's own entry (the last) is who asked", signer.Ip == "203.0.113.9" && !signer.ThisPc, signer.Ip);
}

// ── badge crafting: the list links to each card page, and the card page has the numbers ───────────────────────
{
	Type bc = typeof(NocatFarm.Modules.BadgeCraft);
	var links = ((IEnumerable) bc.GetMethod("ReadyLinks", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
		["<div class=\"badge_row\"><a class=\"badge_row_overlay\" href=\"https://steamcommunity.com/id/me/gamecards/570/\"></a></div>"
		+ "<div class=\"badge_row\"><div class=\"badge_craft_button\"><a href=\"https://steamcommunity.com/id/me/gamecards/730/?border=1\">Ready</a></div></div>"])!).Cast<object>().Select(static l => l.ToString()!).ToList();
	Check("craft: a Ready button's card page is found, foil included, and a plain row isn't", links.Count == 1 && links[0] == "(730, True)", string.Join(" ", links));
	object? c = bc.GetMethod("ParseCardsPage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
		["<a class=\"badge_craft_button\" href=\"javascript:void(0)\" onclick=\"Profile_CraftGameBadge( 'https://steamcommunity.com/profiles/76561198000000000/', 730, 1, 0, 1 );\">Craft Badge</a>"]);
	Check("craft: the card page's numbers are read after the profile address", c?.ToString() == "Craftable { AppId = 730, Series = 1, Border = 0, Levels = 1 }", c?.ToString() ?? "null");
}

// ── sign-in: only Steam refusing counts toward giving up, not a dropped connection ─────────────────────────────
{
	MethodInfo refusal = typeof(NocatFarm.Core.Bot).GetMethod("IsSignInRefusal", BindingFlags.NonPublic | BindingFlags.Static)!;
	bool Refused(Exception e) => (bool) refusal.Invoke(null, [e])!;
	Check("sign-in: a wrong password counts", Refused(new SteamKit2.Authentication.AuthenticationException("no", SteamKit2.EResult.InvalidPassword)));
	Check("sign-in: a timeout or a dropped connection doesn't", !Refused(new TaskCanceledException()) && !Refused(new IOException("reset"))
		&& !Refused(new SteamKit2.Authentication.AuthenticationException("gone", SteamKit2.EResult.ServiceUnavailable)));
}

// ── rep4rep: a daily-limit refusal below the cap is the account's real limit ──────────────────────────────────
{
	MethodInfo learns = typeof(NocatFarm.Modules.Rep4RepModule).GetMethod("LearnsDailyLimit", BindingFlags.NonPublic | BindingFlags.Static)!;
	bool Learns(bool on, int posted, int cap) => (bool) learns.Invoke(null, [on, posted, cap])!;
	Check("rep4rep learn: cut off at 10 with the cap at 15 - 10 is the limit", Learns(true, 10, 15));
	Check("rep4rep learn: not when off, not below 5, not at or over the cap", !Learns(false, 10, 15) && !Learns(true, 3, 15) && !Learns(true, 15, 15));
}

// ── lifecycle: finishing up before logging off starts nothing new, and 'start' then signs back in ──────────────
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	Type bt = typeof(NocatFarm.Core.Bot);

	// Held for the whole block, so no account here ever gets as far as connecting to Steam: every start waits at it.
	var latch = (SemaphoreSlim) typeof(NocatFarm.Core.Limiters).GetField("LoginCooldownLatch", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
	bool tookLatch = latch.Wait(0);
	Check("lifecycle: the login latch is held, so nothing below can sign in for real", latch.CurrentCount == 0);

	var cfg = new NocatFarm.Config.BotConfig { LegitMode = true, StartPaused = true };
	var bot = new NocatFarm.Core.Bot("harness-life-" + Guid.NewGuid().ToString("N")[..6], cfg);
	void SetProp(string name, object? value) => bt.GetProperty(name)!.SetValue(bot, value);
	FieldInfo running = bt.GetField("_running", Inst)!;
	var stopGate = (SemaphoreSlim) bt.GetField("_stopGate", Inst)!.GetValue(bot)!;
	var startGate = (SemaphoreSlim) bt.GetField("_startGate", Inst)!.GetValue(bot)!;

	async Task Settle() {
		for (int i = 0; (i < 50) && ((startGate.CurrentCount == 0) || (stopGate.CurrentCount == 0)); i++) {
			await Task.Delay(100);
		}
	}

	// A graceful stop in its finishing-up delay: still running, Stopping, holding the stop gate.
	await stopGate.WaitAsync();
	running.SetValue(bot, true);
	Check("lifecycle: an account that's simply running ignores 'start'", bot.StartAsync().Wait(2000));
	SetProp("Stopping", true);
	Task start = bot.StartAsync();
	await Task.Delay(300);
	Check("lifecycle: 'start' while it's finishing up waits for the sign-out instead of doing nothing", !start.IsCompleted);

	// The finishing-up ends: signed out, with a timed pause left over from before.
	running.SetValue(bot, false);
	SetProp("Stopping", false);
	SetProp("Paused", true);
	SetProp("PausedUntil", DateTime.UtcNow.AddMinutes(30));
	stopGate.Release();
	await Task.Delay(500);
	Check("lifecycle: ...then signs back in", bot.Running && (bot.State == NocatFarm.Core.BotState.Connecting), $"{bot.State}");
	Check("lifecycle: a timed pause from before a restart doesn't lift a 'Start paused' account later", bot.Paused && (bot.PausedUntil == null));
	await bot.StopAsync();
	await Task.WhenAny(start, Task.Delay(5000));
	await Settle();
	Check("lifecycle: and stops cleanly", start.IsCompleted && !bot.Running && (bot.State == NocatFarm.Core.BotState.Stopped), $"{bot.State}");

	// Status: a pause outlives a stop, but a signed-out account isn't "paused".
	SetProp("Paused", true);
	Check("status: a stopped account that was paused doesn't say 'paused'", NocatFarm.Commands.StateWord(bot) != "paused", NocatFarm.Commands.StateWord(bot));

	// 'enable' on an account whose sign-in gave up signs it in.
	SetProp("State", NocatFarm.Core.BotState.Failed);
	NocatFarm.Commands.ApplyBotSideEffects(bot, NocatFarm.Config.Settings.FindBot("Enabled")!);
	await Task.Delay(500);
	Check("enable: a failed account that gave up signs in again", bot.Running && (bot.State == NocatFarm.Core.BotState.Connecting), $"{bot.State}");
	await bot.StopAsync();
	await Settle();

	// An update that failed after signing everyone out signs them back in - enabled ones only.
	MethodInfo signBack = typeof(NocatFarm.Core.SelfUpdate).GetMethod("SignBackIn", BindingFlags.NonPublic | BindingFlags.Static)!;
	signBack.Invoke(null, [new List<NocatFarm.Core.Bot> { bot }]);
	await Task.Delay(500);
	Check("update failed after signing out: the accounts sign back in", bot.Running, $"{bot.State}");
	await bot.StopAsync();
	await Settle();
	cfg.Enabled = false;
	signBack.Invoke(null, [new List<NocatFarm.Core.Bot> { bot }]);
	await Task.Delay(300);
	Check("update failed after signing out: a disabled account stays off", !bot.Running);
	cfg.Enabled = true;

	// Online and finishing up: the gates close, the farmer puts no new game on.
	SetProp("State", NocatFarm.Core.BotState.Online);
	SetProp("OnlineSince", DateTime.UtcNow.AddHours(-2));
	SetProp("Paused", false);
	var gate = NocatFarm.Modules.HumanGate.Quiet(bot);
	FieldInfo openAt = typeof(NocatFarm.Modules.HumanGate).GetField("_openAt", Inst)!;
	_ = gate.Open;
	openAt.SetValue(gate, DateTime.UtcNow.AddSeconds(-1));
	bool openBefore = gate.Open;
	bool readyBefore = NocatFarm.Modules.HumanMode.ReadyFor(bot) && NocatFarm.Modules.HumanMode.UpFor(bot);
	var farmer = new NocatFarm.Modules.CardFarmer(bot);
	MethodInfo mayFarm = typeof(NocatFarm.Modules.CardFarmer).GetMethod("MayFarmNow", Inst)!;
	bool farmBefore = (bool) mayFarm.Invoke(farmer, null)!;
	SetProp("Stopping", true);
	Check("finishing up: a gate that was open shuts", openBefore && !gate.Open);
	Check("finishing up: not ready for trades, gifts or its own day", readyBefore && !NocatFarm.Modules.HumanMode.ReadyFor(bot) && !NocatFarm.Modules.HumanMode.UpFor(bot));
	Check("finishing up: the card farmer starts no new game", farmBefore && !(bool) mayFarm.Invoke(farmer, null)!);
	SetProp("Stopping", false);

	// A grind on a paused account isn't playing, whatever screen asks.
	MethodInfo groupOf = typeof(NocatFarm.Web.WebHost).GetMethod("GroupOf", BindingFlags.NonPublic | BindingFlags.Static)!;
	SetProp("GrindGame", 730u);
	SetProp("GrindUntil", DateTime.UtcNow.AddHours(1));
	string groupGrinding = (string) groupOf.Invoke(null, [bot])!;
	SetProp("Paused", true);
	string groupPaused = (string) groupOf.Invoke(null, [bot])!;
	Check("status: a grind shows as playing, but not while paused", (groupGrinding == "playing") && (groupPaused != "playing"), $"{groupGrinding} / {groupPaused}");
	Check("status: a paused account with a grind says paused, not grinding", NocatFarm.Core.BotStatus.Of(bot).Doing.ToString() == "paused", NocatFarm.Core.BotStatus.Of(bot).Doing.ToString());
	SetProp("GrindGame", 0u);
	SetProp("GrindUntil", null);
	SetProp("State", NocatFarm.Core.BotState.Stopped);

	// A friend request whose wait ran out while the account was signed out: not "accepted" into nothing, and not
	// marked handled - the list Steam sends at the next sign-in brings it back.
	var social = new NocatFarm.Modules.Social(bot);
	var handled = (HashSet<ulong>) typeof(NocatFarm.Modules.Social).GetField("_handledInvites", Inst)!.GetValue(social)!;
	MethodInfo gone = typeof(NocatFarm.Modules.Social).GetMethod("GoneMeanwhile", Inst)!;
	handled.Add(42);
	Check("social: signed out when the wait ran out - nothing sent, and it's looked at again later", (bool) gone.Invoke(social, [42UL])! && !handled.Contains(42));
	SetProp("State", NocatFarm.Core.BotState.Online);
	handled.Add(43);
	Check("social: signed in - it goes ahead", !(bool) gone.Invoke(social, [43UL])! && handled.Contains(43));
	SetProp("State", NocatFarm.Core.BotState.Stopped);

	await bot.DisposeAsync();

	if (tookLatch) {
		latch.Release();
	}
}

// ── rep4rep: "Post now" and the loop never post at the same time, and a task already done isn't done again ────
{
	string name = "harness-r4r-" + Guid.NewGuid().ToString("N")[..6];
	var bot = new NocatFarm.Core.Bot(name, new NocatFarm.Config.BotConfig());
	typeof(NocatFarm.Core.Bot).GetProperty("State")!.SetValue(bot, NocatFarm.Core.BotState.Online);
	using var api = new NocatFarm.Rep4Rep.Rep4RepApi();
	var r4r = new NocatFarm.Modules.Rep4RepModule(bot, api);
	Type rt = typeof(NocatFarm.Modules.Rep4RepModule);
	FieldInfo RF(string f) => rt.GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!;
	var posting = (SemaphoreSlim) RF("_posting").GetValue(r4r)!;
	var state = new NocatFarm.Rep4Rep.Rep4RepState();
	RF("_state").SetValue(r4r, state);
	RF("_profileId").SetValue(r4r, "harness-profile");
	r4r.Remember([new NocatFarm.Rep4Rep.Rep4RepTask { TaskId = "task-1", TargetSteamId = 76561198000000001, TargetName = "someone" }]);

	posting.Wait();   // the loop is part way through posting a comment
	string busy = await r4r.PostNowAsync("task-1");
	Check("rep4rep: 'post now' while the loop posts says so and posts nothing", busy.Contains("right now", StringComparison.Ordinal) && (state.PostsInLast24h() == 0), busy);
	posting.Release();

	state.RecordPost("task-1");   // the first click went through
	string again = await r4r.PostNowAsync("task-1");
	Check("rep4rep: a second click on the same task doesn't comment twice", again.Contains("already commented", StringComparison.Ordinal) && (state.PostsInLast24h() == 1), again);
	Check("rep4rep: the button hands the posting turn back", posting.CurrentCount == 1);
	await bot.DisposeAsync();
}

// ── sign-in token: a web token minted before a sign-in changed the tokens isn't written over the new ones ─────
{
	string name = "harness-token-" + Guid.NewGuid().ToString("N")[..6];
	var bot = new NocatFarm.Core.Bot(name, new NocatFarm.Config.BotConfig());
	Type bt = typeof(NocatFarm.Core.Bot);
	MethodInfo replace = bt.GetMethod("ReplaceTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;
	MethodInfo adopt = bt.GetMethod("AdoptMinted", BindingFlags.NonPublic | BindingFlags.Instance)!;
	FieldInfo gen = bt.GetField("_tokenGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
	FieldInfo refresh = bt.GetField("_refreshToken", BindingFlags.NonPublic | BindingFlags.Instance)!;

	replace.Invoke(bot, ["refresh-1", null]);
	long asked = (long) gen.GetValue(bot)!;              // a mint goes out with refresh-1...
	replace.Invoke(bot, ["refresh-2", null]);             // ...the connection drops, and a fresh sign-in stores refresh-2
	object? late = adopt.Invoke(bot, [asked, "refresh-1-rotated", null]);
	Check("token: a mint answered after a new sign-in is dropped", (late == null) && ((string?) refresh.GetValue(bot) == "refresh-2")
		&& (NocatFarm.Core.TokenStore.Load(name) == "refresh-2"), $"{refresh.GetValue(bot)} / {NocatFarm.Core.TokenStore.Load(name)}");
	adopt.Invoke(bot, [(long) gen.GetValue(bot)!, "refresh-2-rotated", null]);
	Check("token: a mint in step with the sign-in keeps Steam's rotated token", ((string?) refresh.GetValue(bot) == "refresh-2-rotated")
		&& (NocatFarm.Core.TokenStore.Load(name) == "refresh-2-rotated"));
	asked = (long) gen.GetValue(bot)!;
	replace.Invoke(bot, [null, null]);                    // the token was rejected on a reconnect
	adopt.Invoke(bot, [asked, "revoked-rotated", null]);
	Check("token: a rejected token stays gone", (refresh.GetValue(bot) == null) && (NocatFarm.Core.TokenStore.Load(name) == null));
	NocatFarm.Core.TokenStore.Clear(name);
	await bot.DisposeAsync();
}

// ── items: a send and a sale of the same account never move the same card ──────────────────────────────────────
{
	var bot = new NocatFarm.Core.Bot("harness-items", new NocatFarm.Config.BotConfig());
	Type bt = typeof(NocatFarm.Core.Bot);
	HashSet<ulong> Claim(params ulong[] ids) => (HashSet<ulong>) bt.GetMethod("ClaimItems", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bot, [ids])!;
	void Release(params ulong[] ids) => bt.GetMethod("ReleaseItems", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(bot, [ids]);
	HashSet<ulong> sending = Claim(1, 2, 3);
	HashSet<ulong> selling = Claim(2, 3, 4);
	Check("items: the send gets all it asked for", sending.SetEquals([1, 2, 3]));
	Check("items: the sale meanwhile gets only what the send isn't moving", selling.SetEquals([4]));
	Release(1, 2, 3);
	Check("items: once the send is done they can be claimed again", Claim(2).SetEquals([2]));
	await bot.DisposeAsync();
}

// ── accounts: the same account added twice at once is added once ───────────────────────────────────────────────
{
	NocatFarm.Config.GlobalConfig liveBefore = NocatFarm.Config.Live.Global;
	var mgr = new NocatFarm.Core.BotManager(liveBefore);
	string name = "harness-add-" + Guid.NewGuid().ToString("N")[..6];
	var adds = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => mgr.AddAsync(name, new NocatFarm.Config.BotConfig { Enabled = false }))));
	Check("add: two at once - one account, not two signed in to the same Steam account", adds.Count(static b => b != null) == 1, $"{adds.Count(static b => b != null)} added");
	await mgr.RemoveAsync(name);
	NocatFarm.Config.Live.Global = liveBefore;
}

// ── trades: an offer the loop is answering isn't answered again by 'trade accept|decline' ─────────────────────
{
	var bot = new NocatFarm.Core.Bot("harness-claim", new NocatFarm.Config.BotConfig());
	var trading = new NocatFarm.Modules.Trading(bot);
	Type tt = typeof(NocatFarm.Modules.Trading);
	bool Claim(ulong id) => (bool) tt.GetMethod("TryClaim", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(trading, [id])!;
	void Invoke(string m, ulong id) => tt.GetMethod(m, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(trading, [id]);
	Check("trade: the loop takes offer 7", Claim(7));
	Check("trade: a command meanwhile can't", !Claim(7));
	Invoke("Unclaim", 7);
	Check("trade: free again if the loop didn't answer it", Claim(7));
	Invoke("Finish", 7);
	Invoke("Unclaim", 7);
	Check("trade: once answered, nobody answers it again", !Claim(7));
	await bot.DisposeAsync();
}

// ── updates: an install already under way turns the next one away (the timer's two kinds, a click) ─────────────
{
	FieldInfo busy = typeof(NocatFarm.Core.SelfUpdate).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Static)!;
	busy.SetValue(null, 1);   // one install has claimed it

	if (NocatFarm.Core.SelfUpdate.Supported) {
		string? second = await NocatFarm.Core.SelfUpdate.ApplyAsync(CancellationToken.None);
		Check("update: a second install while one runs is turned away", NocatFarm.Core.SelfUpdate.Busy && (second?.Contains("already downloading", StringComparison.Ordinal) == true), second ?? "null");
	}

	busy.SetValue(null, 0);
	Check("update: free again once it's released", !NocatFarm.Core.SelfUpdate.Busy);
}

// ── human mode: each game's share, the way the day picks ──────────────────────────────────────────────────────
{
	List<double> Shares(params (uint, int)[] w) => NocatFarm.Modules.HumanMode.Shares([.. w]);
	Check("shares: the main game's number IS its share, the side game gets the rest", Shares((730, 70), (440, 10)).SequenceEqual([70.0, 30.0]));
	Check("shares: the hunt splits the rest with the side games by weight", Shares((730, 70), (440, 15), (220, 15)).SequenceEqual([70.0, 15.0, 15.0]));
	Check("shares: one game is all of it, and a main game over 95 is held at 95", Shares((730, 70)).SequenceEqual([100.0]) && (Shares((730, 100), (440, 5))[0] == 95));
}

// ── shortest/longest pairs: the side just changed keeps its number, from the dashboard too ────────────────────
{
	NocatFarm.Config.BotConfig Pair(int lo, int hi) => new() { BreakMinMinutes = lo, BreakMaxMinutes = hi };
	NocatFarm.Config.BotConfig a = Pair(10, 3);
	NocatFarm.Config.Settings.FixRanges(a, ["BreakMaxMinutes", "Notes"]);
	Check("ranges: a longest lowered on the dashboard stays, the shortest comes down", (a.BreakMinMinutes == 3) && (a.BreakMaxMinutes == 3), $"{a.BreakMinMinutes}-{a.BreakMaxMinutes}");
	NocatFarm.Config.BotConfig b = Pair(10, 3);
	NocatFarm.Config.Settings.FixRanges(b, ["BreakMinMinutes", "BreakMaxMinutes"]);
	Check("ranges: both changed - the longest gives way", (b.BreakMinMinutes == 10) && (b.BreakMaxMinutes == 10), $"{b.BreakMinMinutes}-{b.BreakMaxMinutes}");
	NocatFarm.Config.BotConfig c = Pair(10, 3);
	NocatFarm.Config.Settings.FixRanges(c, "BreakMaxMinutes");
	Check("ranges: the console's one-name form still works", (c.BreakMinMinutes == 3) && (c.BreakMaxMinutes == 3), $"{c.BreakMinMinutes}-{c.BreakMaxMinutes}");
}

// ── commands: name, human, anywhere say what really happens ───────────────────────────────────────────────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig realGlobal = NocatFarm.Config.Live.Global;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-cmds-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false });
		var bots = (System.Collections.Concurrent.ConcurrentDictionary<string, NocatFarm.Core.Bot>) typeof(NocatFarm.Core.BotManager)
			.GetField("_bots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mgr)!;

		// Not started and never signed in - only its config and its human-mode module.
		var robot = new NocatFarm.Core.Bot("robo", new NocatFarm.Config.BotConfig { CustomGameNameEnabled = false });
		var person = new NocatFarm.Core.Bot("person", new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:10", PureMainDayChancePct = 0 });
		person.AddModule(new NocatFarm.Modules.HumanMode(person));
		bots["robo"] = robot;
		bots["person"] = person;

		string named = await Commands.RunAsync(mgr, "name robo nocat.lol");
		Check("name: naming it switches the custom name on, so 'now showing' is true", robot.Cfg.CustomGameNameEnabled && (robot.CustomName == "nocat.lol"), named);
		string legit = await Commands.RunAsync(mgr, "name person nocat.lol");
		Check("name: a human-mode account doesn't claim to be showing it", !legit.Contains("now showing", StringComparison.Ordinal), legit);

		string human = await Commands.RunAsync(mgr, "human person");
		Check("human: 730:70, 440:10 reads 70/30, as the day splits it (not 88/12)", human.Contains(" 70%", StringComparison.Ordinal) && human.Contains(" 30%", StringComparison.Ordinal), human.Split('\n').FirstOrDefault(static l => l.Contains("set to play", StringComparison.Ordinal)) ?? human);

		string slashed = await Commands.RunAsync(mgr, "/set robo Rep4Rep");
		Check("commands: a leading slash works like the chats' habit, and a missing value says so", slashed.Contains("value is missing", StringComparison.Ordinal), slashed);

		// The dispatcher takes slashes off now, so the chats' guards have to as well.
		NocatFarm.Core.BotManager? host = Commands.Host;
		Commands.Host = mgr;
		string steamExit = await Commands.RunAsync("/exit", "robo");
		Commands.Host = host;
		Check("guards: '//exit' from Steam chat is refused like 'exit'", !Commands.ExitRequested && steamExit.Contains("PC", StringComparison.Ordinal), steamExit);
		Type notifier = typeof(NocatFarm.Core.Bot).Assembly.GetType("NocatFarm.Core.Notifier")!;
		var guard = ((string? Needs, string Args)) notifier.GetMethod("ConfirmGuard", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["/exit", ""])!;
		Check("guards: '//exit' on Telegram or Discord still asks for confirm", guard.Needs == "exit", guard.Needs ?? "null");

		var held = new NocatFarm.Core.Bot("held", new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:15, 550:15", BlacklistedGames = [730] });
		var heldMode = new NocatFarm.Modules.HumanMode(held);
		held.AddModule(heldMode);
		List<double> heldShares = NocatFarm.Modules.HumanMode.Shares(heldMode.Rotation);
		Check("human: the main game left out, what's left splits by weight (not 15/85)", heldShares.SequenceEqual([50.0, 50.0]), string.Join("/", heldShares));

		var watch = System.Diagnostics.Stopwatch.StartNew();
		string anywhere = await Commands.RunAsync(mgr, "anywhere on");
		Check("anywhere: with the dashboard off it says so at once, not 'still asking your router' 15s later",
			(watch.Elapsed < TimeSpan.FromSeconds(3)) && !anywhere.Contains("router", StringComparison.Ordinal) && !mgr.Global.WebRemoteAccess, anywhere);
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		NocatFarm.Config.Live.Global = realGlobal;

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// temp - it goes when Windows tidies up
		}
	}
}

// ── 1.5.6: a late 'wake' sitting survives a restart, the hunt's day is human mode's day, the week preview has the hunt ──
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	Type ht = typeof(NocatFarm.Modules.HumanMode);
	string name = "harness-stayup-" + Guid.NewGuid().ToString("N")[..6];
	var cfg = new NocatFarm.Config.BotConfig { LegitMode = true };
	var bot = new NocatFarm.Core.Bot(name, cfg);
	var human = new NocatFarm.Modules.HumanMode(bot);
	DateTime now = DateTime.Now;
	ht.GetField("_dayStamp", Inst)!.SetValue(human, now.DayOfYear);
	DateTime until = now.AddMinutes(40);
	ht.GetField("_stayUpUntil", Inst)!.SetValue(human, until);
	ht.GetMethod("Persist", Inst)!.Invoke(human, null);

	var again = new NocatFarm.Modules.HumanMode(bot);
	ht.GetMethod("Restore", Inst)!.Invoke(again, [NocatFarm.Modules.HumanDay.Load(name, now)!]);
	DateTime back = (DateTime) ht.GetField("_stayUpUntil", Inst)!.GetValue(again)!;
	Check("wake late: a restart during the extra sitting keeps it", Math.Abs((back - until).TotalSeconds) < 1, $"{back:HH:mm:ss} vs {until:HH:mm:ss}");

	ht.GetField("_stayUpUntil", Inst)!.SetValue(human, now.AddMinutes(-5));
	ht.GetMethod("Persist", Inst)!.Invoke(human, null);
	ht.GetMethod("Restore", Inst)!.Invoke(again, [NocatFarm.Modules.HumanDay.Load(name, now)!]);
	Check("wake late: a sitting that's already over isn't brought back", (DateTime) ht.GetField("_stayUpUntil", Inst)!.GetValue(again)! == DateTime.MinValue);
	NocatFarm.Modules.HumanDay.Forget(name);

	Check("plan day: today's day of the year is today", NocatFarm.Modules.HumanMode.PlanDate(272, new DateTime(2026, 9, 29, 15, 0, 0)) == new DateTime(2026, 9, 29));
	Check("plan day: New Year's Eve's plan just after midnight is still 31 December", NocatFarm.Modules.HumanMode.PlanDate(365, new DateTime(2027, 1, 1, 0, 30, 0)) == new DateTime(2026, 12, 31));
	ht.GetField("_dayStamp", Inst)!.SetValue(human, now.AddDays(-1).DayOfYear);
	Check("plan day: in the small hours before bed it's still yesterday", human.PlanDay == now.Date.AddDays(-1), $"{human.PlanDay:yyyy-MM-dd}");
	await bot.DisposeAsync();

	var wcfg = new NocatFarm.Config.BotConfig { GameWeights = "730:80", DayOffChancePct = 0, PureMainDayChancePct = 0, WeekdayHours = 6, WeekendHours = 6 };
	List<string> without = NocatFarm.Modules.HumanMode.PreviewWeek(wcfg);
	List<string> with = NocatFarm.Modules.HumanMode.PreviewWeek(wcfg, [(730, 80), (4000, 20)]);
	Check("human week: one game and no hunt is main-game-only days", without.All(static l => l.Contains(" only")), without.FirstOrDefault() ?? "");
	Check("human week: with the hunt in the rotation, days aren't main-game-only", with.All(static l => !l.Contains(" only")), with.FirstOrDefault() ?? "");
}

// ── races: a dashboard save, 'update skip', badge crafting and human mode's banking each take their turn ─────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-races-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		// Dashboard save: Telegram connected between the page loading its copy and the page saving it.
		var web = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };
		MethodInfo save = typeof(NocatFarm.Web.WebHost).GetMethod("SaveFromPage", BindingFlags.NonPublic | BindingFlags.Static)!;
		var live = new NocatFarm.Config.GlobalConfig { Language = "en", WebPassword = "pw" };
		System.Text.Json.Nodes.JsonObject Page() {
			var copy = System.Text.Json.JsonSerializer.SerializeToNode(live, web)!.AsObject();
			copy["WebPassword"] = "";   // secrets reach the page blank
			return copy;
		}
		System.Text.Json.Nodes.JsonObject loaded = Page();
		live.TelegramChatId = "12345";   // connected a moment after the page loaded
		var sent = (System.Text.Json.Nodes.JsonObject) loaded.DeepClone();
		sent["Language"] = "de";
		sent["Base"] = loaded.DeepClone();
		object? saved = save.Invoke(null, [live, sent, web]);
		Check("dashboard save: a chat connected while the page was open stays connected", (saved != null) && (live.TelegramChatId == "12345"), live.TelegramChatId);
		Check("dashboard save: what the page changed is saved, onto the live settings, and the password is kept", (live.Language == "de") && (live.WebPassword == "pw"), $"{live.Language} / {live.WebPassword.Length}");
		System.Text.Json.Nodes.JsonObject old = Page();   // an old page: no Base, every field sent
		old["Language"] = "fr";
		Check("dashboard save: a page without a Base still saves", (save.Invoke(null, [live, old, web]) != null) && (live.Language == "fr") && (live.WebPassword == "pw"), live.Language);

		// 'update skip': the install looks again once it has the busy flag, and the swap and a skip take turns.
		PropertyInfo available = typeof(NocatFarm.Core.UpdateCheck).GetProperty("Available")!;
		MethodInfo handOver = typeof(NocatFarm.Core.SelfUpdate).GetMethod("HandOver", BindingFlags.NonPublic | BindingFlags.Static)!;
		FieldInfo handedOver = typeof(NocatFarm.Core.SelfUpdate).GetField("_handedOver", BindingFlags.NonPublic | BindingFlags.Static)!;
		string? availableBefore = NocatFarm.Core.UpdateCheck.Available;
		available.SetValue(null, "v9.9.9");
		NocatFarm.Core.UpdateCheck.Skipped = "9.9.9";

		if (NocatFarm.Core.SelfUpdate.Supported) {
			string? stopped = await NocatFarm.Core.SelfUpdate.ApplyAsync(CancellationToken.None, byItself: true);
			Check("update skip: an install started just before the skip stops at its turn, nothing downloaded",
				(stopped?.Contains("was skipped", StringComparison.Ordinal) == true) && !NocatFarm.Core.SelfUpdate.Busy, stopped ?? "null");
		}

		bool swapStarted = false;
		bool handed = (bool) handOver.Invoke(null, ["v9.9.9", (Action) (() => swapStarted = true)])!;
		Check("update skip: skipped before the handover - the swap never starts", !handed && !swapStarted);
		NocatFarm.Core.UpdateCheck.Skipped = null;
		handed = (bool) handOver.Invoke(null, ["v9.9.9", (Action) (() => swapStarted = true)])!;
		Check("update skip: after the handover it's too late, and says so", handed && swapStarted && !NocatFarm.Core.SelfUpdate.Skip("v9.9.9") && (NocatFarm.Core.UpdateCheck.Skipped == null));
		handedOver.SetValue(null, false);
		available.SetValue(null, availableBefore);

		// Badge crafting: the set's cards are claimed like a send's, all or none.
		var inv = new NocatFarm.Core.InventoryContents { Complete = true };
		void AddCard(ulong id, string cls, string type, string app) {
			inv.Assets.Add(System.Text.Json.JsonDocument.Parse($"{{\"assetid\":\"{id}\",\"classid\":\"{cls}\",\"instanceid\":\"0\"}}").RootElement);
			inv.Descriptions[cls + "_0"] = System.Text.Json.JsonDocument.Parse($"{{\"type\":\"{type}\",\"market_fee_app\":\"{app}\"}}").RootElement;
		}
		AddCard(1, "a", "Dota 2 Trading Card", "570");
		AddCard(2, "a", "Dota 2 Trading Card", "570");   // a second copy of the same card
		AddCard(3, "b", "Dota 2 Trading Card", "570");
		AddCard(4, "f", "Dota 2 Foil Trading Card", "570");
		AddCard(5, "c", "Portal Trading Card", "400");
		Type bc = typeof(NocatFarm.Modules.BadgeCraft);
		var set = (List<ulong>) bc.GetMethod("SetCards", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [inv, 570u, false])!;
		Check("craft: every copy of the set's cards, not the foils or another game's", set.Order().SequenceEqual([1ul, 2ul, 3ul]), string.Join(",", set));
		var owner = new NocatFarm.Core.Bot("harness-craft", new NocatFarm.Config.BotConfig());
		Type ob = typeof(NocatFarm.Core.Bot);
		HashSet<ulong> Claim(params ulong[] ids) => (HashSet<ulong>) ob.GetMethod("ClaimItems", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [ids])!;
		void Release(params ulong[] ids) => ob.GetMethod("ReleaseItems", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, [ids]);
		MethodInfo claimSet = bc.GetMethod("ClaimSet", BindingFlags.NonPublic | BindingFlags.Static)!;
		Claim(2);   // a send has one copy
		Check("craft: a set with a card a send is moving waits", claimSet.Invoke(null, [owner, set]) == null);
		Check("craft: and holds none of the others meanwhile", Claim(1, 3).Count == 2);
		Release(1, 2, 3);
		var crafting = (HashSet<ulong>?) claimSet.Invoke(null, [owner, set]);
		Check("craft: claimed first, a send or a listing leaves the set's cards out", (crafting?.Count == 3) && Claim(1, 2, 3, 5).SetEquals([5ul]));
		await owner.DisposeAsync();

		// Human mode: a stop banking at the same moment as a tick counts each minute once.
		var player = new NocatFarm.Core.Bot("harness-bank-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig { LegitMode = true });
		var mode = new NocatFarm.Modules.HumanMode(player);
		Type hm = typeof(NocatFarm.Modules.HumanMode);
		FieldInfo HF(string f) => hm.GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!;
		DateTime logon = DateTime.UtcNow.AddHours(-1);
		ob.GetProperty("OnlineSince")!.SetValue(player, logon);
		HF("_phase").SetValue(mode, NocatFarm.Modules.HumanMode.Phase.Playing);
		HF("_dayStamp").SetValue(mode, -1);   // nothing written to disk
		HF("_bankedForLogon").SetValue(mode, logon);
		HF("_bankedTo").SetValue(mode, DateTime.UtcNow.AddMinutes(-5).AddSeconds(-10));
		HF("_lastBankAt").SetValue(mode, DateTime.UtcNow.AddSeconds(-10));
		MethodInfo bank = hm.GetMethod("BankSession", BindingFlags.NonPublic | BindingFlags.Instance)!;
		using (var together = new Barrier(8)) {
			await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() => { together.SignalAndWait(); bank.Invoke(mode, null); }, TaskCreationOptions.LongRunning)));
		}
		bank.Invoke(mode, null);
		int banked = (int) HF("_playedMinutesToday").GetValue(mode)!;
		Check("human bank: eight banks at once and one after - five minutes played is five banked", banked == 5, $"{banked}");
		await player.DisposeAsync();
	} finally {
		NocatFarm.Core.UpdateCheck.Skipped = null;
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// temp - it goes when Windows tidies up
		}
	}
}

// ── human mode: farming sittings keep to themselves, a dead connection starts nothing, the day's edges, a finished day ──
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	Type ht = typeof(NocatFarm.Modules.HumanMode);
	Type bt = typeof(NocatFarm.Core.Bot);
	FieldInfo HF(string f) => ht.GetField(f, Inst) ?? throw new MissingFieldException(f);
	T HGet<T>(object h, string f) => (T) HF(f).GetValue(h)!;
	void Set(object h, string f, object? v) => HF(f).SetValue(h, v);
	object? HCall(object h, string m, params object?[] args) => ht.GetMethod(m, Inst)!.Invoke(h, args);
	object? Static(string m, params object?[] args) => ht.GetMethod(m, Stat)!.Invoke(null, args);
	void BotSet(NocatFarm.Core.Bot b, string p, object? v) => bt.GetProperty(p)!.SetValue(b, v);
	NocatFarm.Modules.HumanMode.Phase PhaseOf(object h) => (NocatFarm.Modules.HumanMode.Phase) HF("_phase").GetValue(h)!;
	const NocatFarm.Modules.HumanMode.Phase Off = NocatFarm.Modules.HumanMode.Phase.Off, Playing = NocatFarm.Modules.HumanMode.Phase.Playing;

	// A plan in force right now, up from midnight until a day and more away - so the time of day the tests run at
	// doesn't come into it.
	void AwakeAllDay(object h) {
		Set(h, "_dayStamp", DateTime.Now.DayOfYear);
		Set(h, "_wakeMinuteOfDay", 0);
		Set(h, "_bedHour", 23);
		Set(h, "_bedMinute", 59);
		Set(h, "_bedIsTomorrow", true);
		Set(h, "_stayUpUntil", DateTime.MinValue);
	}

	// (a) Farming sittings are their own thing - they don't use up the side games' allowance.
	Check("farming sittings: they come off the day the side share is of, not out of the side games' minutes",
		((int) Static("SideBudgetNow", 240, 480, 0)! == 240) && ((int) Static("SideBudgetNow", 240, 480, 240)! == 120));
	Check("farming sittings: a main-game-only day still has no side allowance", (int) Static("SideBudgetNow", 0, 480, 100)! == 0);

	string fname = "harness-farm-" + Guid.NewGuid().ToString("N")[..6];
	var fcfg = new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30", FarmCardsWhen = NocatFarm.Modules.FarmWhen.Mixed };
	var fbot = new NocatFarm.Core.Bot(fname, fcfg);
	var fh = new NocatFarm.Modules.HumanMode(fbot);
	fbot.AddModule(fh);
	DateTime logon = DateTime.UtcNow.AddHours(-3);
	BotSet(fbot, "OnlineSince", logon);
	AwakeAllDay(fh);
	Set(fh, "_targetMinutes", 600);

	void Bank(object h, uint game, bool farming, int minutes, DateTime signedIn) {
		Set(h, "_phase", Playing);
		Set(h, "_game", game);
		Set(h, "_farmSitting", farming);
		Set(h, "_bankedForLogon", signedIn);
		Set(h, "_bankedTo", DateTime.UtcNow.AddMinutes(-minutes));
		Set(h, "_lastBankAt", DateTime.UtcNow.AddSeconds(-10));
		HCall(h, "BankSession");
	}

	Bank(fh, 999, true, 40, logon);   // a farming sitting on a card game that isn't one of its games
	Check("farming sittings: 40 minutes of farming are farming, not side-game time", (HGet<int>(fh, "_farmPlayed") == 40) && (HGet<int>(fh, "_otherPlayed") == 0),
		$"farm {HGet<int>(fh, "_farmPlayed")}, side {HGet<int>(fh, "_otherPlayed")}");
	Bank(fh, 440, false, 25, logon);
	Check("farming sittings: an ordinary side-game sitting is still side-game time", HGet<int>(fh, "_otherPlayed") == 25);
	NocatFarm.Modules.HumanDay? fsaved = NocatFarm.Modules.HumanDay.Load(fname, DateTime.Now);
	Check("farming sittings: their minutes survive a restart", (fsaved?.FarmPlayed == 40) && (fsaved.OtherPlayed == 25));

	// With cards about, the usual games keep their shares: no more two sittings in three to a game with drops.
	var ffarmer = new NocatFarm.Modules.CardFarmer(fbot);
	fbot.AddModule(ffarmer);
	var fqueue = (List<NocatFarm.Modules.FarmTarget>) typeof(NocatFarm.Modules.CardFarmer).GetField("_queue", Inst)!.GetValue(ffarmer)!;
	fqueue.Add(new NocatFarm.Modules.FarmTarget { AppId = 440, CardsRemaining = 5, HoursPlayed = 9 });
	Set(fh, "_mainSharePct", 70);
	Set(fh, "_firstSessionOfDay", false);   // an ordinary sitting, the day's first being short whatever it's on
	int mw = (int) Static("MainWeight", 30, 70, 30, 150, int.MaxValue)!;
	int picked440 = 0;
	const int Picks = 20_000;

	for (int i = 0; i < Picks; i++) {
		picked440 += (uint) HCall(fh, "PickGame")! == 440 ? 1 : 0;
	}

	double share440 = (double) picked440 / Picks, want440 = 30.0 / (mw + 30);
	Check("cards about: a usual game with drops gets its own share of the picks, not two in three", Math.Abs(share440 - want440) < 0.02, $"{share440:P1} vs {want440:P1}");

	// Carrying on is with one of its own games, and after a break nothing is being "closed".
	Set(fh, "_otherBudget", 1_000_000);
	Set(fh, "_otherPlayed", 0);
	Set(fh, "_farmPlayed", 0);
	Set(fh, "_playedMinutesToday", 0);
	Set(fh, "_targetMinutes", 1_000_000);
	int carriedFarmGame = 0, closing = 0;
	NocatFarm.Log.Suppressed = true;

	for (int i = 0; i < 160; i++) {
		Set(fh, "_phase", Off);
		Set(fh, "_switchingTo", 0u);
		Set(fh, "_lastGame", i % 2 == 0 ? 999u : 440u);   // last sitting: a farming one's card game, or a side game
		HCall(fh, "StartSession");
		carriedFarmGame += HGet<uint>(fh, "_game") == 999 ? 1 : 0;
		closing += PhaseOf(fh) == NocatFarm.Modules.HumanMode.Phase.SwitchingGame ? 1 : 0;
	}

	NocatFarm.Log.Suppressed = false;
	Check("carry on: never an ordinary sitting on the last farming sitting's card game", carriedFarmGame == 0, $"{carriedFarmGame} of 80");

	// (c) After a break the next game starts - nothing is running, so there is nothing to be "closing".
	Check("after a break: a different game goes straight on, no 'closing the game' over nothing", closing == 0, $"{closing} of 160");
	Check("closing the game: only when something else is really running", !(bool) Static("NeedsSwitchGap", 440u, new List<uint>())!
		&& (bool) Static("NeedsSwitchGap", 440u, new List<uint> { 730 })! && !(bool) Static("NeedsSwitchGap", 440u, new List<uint> { 440 })!);

	// Audit: the first sitting of the day isn't thirty minutes on the dot every morning.
	Check("first sitting: its cap sits above a 30-minute minimum, with room to vary", ((int) Static("FirstSittingCap", 30, 150)! == 48) && ((int) Static("FirstSittingCap", 5, 10)! == 10));
	HashSet<int> firsts = [];

	for (int i = 0; i < 300; i++) {
		Set(fh, "_firstSessionOfDay", true);
		firsts.Add((int) HCall(fh, "SessionLength", 730u, 730u)!);
	}

	Check("first sitting: a different length from day to day, inside 30-48m", (firsts.Count > 5) && (firsts.Min() >= 30) && (firsts.Max() <= 48), string.Join(",", firsts.Order()));

	// Audit: the day's last sitting stays inside the longest sitting.
	Set(fh, "_firstSessionOfDay", false);
	Set(fh, "_targetMinutes", 1000);
	int lastLongest = 0;

	for (int i = 0; i < 2000; i++) {
		Set(fh, "_playedMinutesToday", 1000 - 149);   // 149 left, and "what's left" gets a few minutes' slack
		lastLongest = Math.Max(lastLongest, (int) HCall(fh, "SessionLength", 730u, 730u)!);
	}

	Check("the day's last sitting: never past 'longest sitting' (150m)", lastLongest <= 150, $"{lastLongest}m");

	// Audit: the day's last sitting ends the day - not on a "short break - back in 12m" nobody comes back from.
	NocatFarm.Log.Suppressed = true;
	AwakeAllDay(fh);
	Set(fh, "_targetMinutes", 60);
	Set(fh, "_playedMinutesToday", 60);
	Bank(fh, 730, false, 0, logon);
	HCall(fh, "EndSession");
	NocatFarm.Modules.HumanMode.Phase lastEnds = PhaseOf(fh);
	Set(fh, "_playedMinutesToday", 10);
	Bank(fh, 730, false, 0, logon);
	HCall(fh, "EndSession");
	NocatFarm.Modules.HumanMode.Phase otherEnds = PhaseOf(fh);
	NocatFarm.Log.Suppressed = false;
	Check("the day's last sitting ends the day, not on a break", lastEnds == Off, $"{lastEnds}");
	Check("...and any other sitting still ends on a break", otherEnds is NocatFarm.Modules.HumanMode.Phase.ShortBreak or NocatFarm.Modules.HumanMode.Phase.MealBreak, $"{otherEnds}");

	// Audit: a meal and an offline break are saved when they happen, so a restart can't hand them out again - and an
	// offline break counts when it really goes offline, not when it was decided (bedtime can cut it short first).
	Set(fh, "_mealCap", 3);
	Set(fh, "_mealsUsed", 0);
	NocatFarm.Log.Suppressed = true;
	HCall(fh, "MealBreak");
	NocatFarm.Log.Suppressed = false;
	Check("a meal is saved as it starts, so a restart can't give the day another", NocatFarm.Modules.HumanDay.Load(fname, DateTime.Now)?.MealsUsed == 1);

	fcfg.SignOutOnBreakChancePct = 100;
	fcfg.BreakAwayAfterMinMinutes = 2;
	fcfg.BreakAwayAfterMaxMinutes = 2;
	Set(fh, "_signOutCap", 5);
	Set(fh, "_signOutsUsed", 0);
	NocatFarm.Log.Suppressed = true;
	HCall(fh, "StepAway", 30, 3, new NocatFarm.Core.Said("short break"), 1.0);
	NocatFarm.Log.Suppressed = false;
	bool decidedDark = HF("_breakPersona").GetValue(fh) is int decided && (decided == NocatFarm.Core.Bot.PersonaDark);
	Check("offline break: not counted when it's decided", decidedDark && (HGet<int>(fh, "_signOutsUsed") == 0));
	BotSet(fbot, "State", NocatFarm.Core.BotState.Online);
	Set(fh, "_phase", NocatFarm.Modules.HumanMode.Phase.ShortBreak);
	Set(fh, "_breakPersonaAt", DateTime.UtcNow.AddSeconds(-1));
	Set(fh, "_phaseEnds", DateTime.UtcNow.AddMinutes(20));
	Set(fh, "_targetMinutes", 300);
	Set(fh, "_lastStepAt", DateTime.UtcNow);
	HCall(fh, "StepAsync");
	Check("offline break: counted - and saved - the moment it really goes offline", (HGet<int>(fh, "_signOutsUsed") == 1) && HGet<bool>(fh, "_offlineBreak")
		&& (NocatFarm.Modules.HumanDay.Load(fname, DateTime.Now)?.SignOutsUsed == 1));
	BotSet(fbot, "State", NocatFarm.Core.BotState.Stopped);
	NocatFarm.Modules.HumanDay.Forget(fname);
	await fbot.DisposeAsync();

	// (b) Back from the PC sleeping: nothing starts until Steam shows the connection is alive.
	DateTime t = new(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc);
	DateTime on1 = t.AddHours(-5);
	bool LiveAgain(DateTime? logonNow, DateTime inbound, DateTime at) => (bool) Static("LiveAgain", t, on1, logonNow, inbound, at)!;
	Check("woke from sleep: same sign-in, nothing from Steam since - wait", !LiveAgain(on1, t.AddHours(-2), t.AddSeconds(20)));
	Check("woke from sleep: Steam has sent something since - go on", LiveAgain(on1, t.AddSeconds(5), t.AddSeconds(20)));
	Check("woke from sleep: signed in again - go on", LiveAgain(t.AddSeconds(30), t.AddHours(-2), t.AddSeconds(40)));
	Check("woke from sleep: still connected three minutes on - taken as fine", LiveAgain(on1, t.AddHours(-2), t.AddMinutes(3)));

	string sname = "harness-slept-" + Guid.NewGuid().ToString("N")[..6];
	var sbot = new NocatFarm.Core.Bot(sname, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730" });
	var sh = new NocatFarm.Modules.HumanMode(sbot);
	sbot.AddModule(sh);
	BotSet(sbot, "State", NocatFarm.Core.BotState.Online);
	BotSet(sbot, "OnlineSince", DateTime.UtcNow.AddHours(-6));
	AwakeAllDay(sh);
	Set(sh, "_targetMinutes", 300);
	Set(sh, "_phase", NocatFarm.Modules.HumanMode.Phase.ShortBreak);
	Set(sh, "_phaseEnds", DateTime.UtcNow.AddHours(-2));   // the break ran out while the PC slept...
	Set(sh, "_lastStepAt", DateTime.UtcNow.AddHours(-3));  // ...and nothing ran for three hours
	NocatFarm.Log.Suppressed = true;
	HCall(sh, "StepAsync");
	NocatFarm.Log.Suppressed = false;
	Check("woke from sleep: no sitting 'started' on a connection nobody has heard from", (PhaseOf(sh) == NocatFarm.Modules.HumanMode.Phase.ShortBreak) && (HGet<uint>(sh, "_game") == 0),
		$"{PhaseOf(sh)}");
	Thread.Sleep(50);
	bt.GetMethod("NoteIncomingPacket", Inst)!.Invoke(sbot, null);   // Steam says something...
	Set(sh, "_clearReads", 9);
	BotSet(sbot, "PlayingBlocked", true);                            // ...that you're on the account
	NocatFarm.Log.Suppressed = true;
	HCall(sh, "StepAsync");
	NocatFarm.Log.Suppressed = false;
	Check("woke from sleep: once Steam has spoken, the day carries on", (HGet<DateTime>(sh, "_proveSince") == DateTime.MinValue) && (PhaseOf(sh) == NocatFarm.Modules.HumanMode.Phase.StoodDown),
		$"{PhaseOf(sh)}");
	Check("standing down for you: the clear reads start again from none, so one read after you leave isn't enough", HGet<int>(sh, "_clearReads") == 0);
	BotSet(sbot, "State", NocatFarm.Core.BotState.Stopped);
	NocatFarm.Modules.HumanDay.Forget(sname);
	await sbot.DisposeAsync();

	// (d) The day's edges with the day starting at 2 or earlier, and bed after midnight.
	DateTime fri = new(2026, 10, 2);   // a Friday: up at 03:00, bed Saturday 00:40
	Check("day boundary: a plan rolled at midnight doesn't get the account back up at 00:10", !(bool) Static("InPlan", fri.AddMinutes(10), fri, 180, 0, 40, true)!);
	Check("day boundary: up at its own wake time, and on to its own bedtime after midnight", (bool) Static("InPlan", fri.AddHours(3.5), fri, 180, 0, 40, true)!
		&& (bool) Static("InPlan", fri.AddDays(1).AddMinutes(30), fri, 180, 0, 40, true)! && !(bool) Static("InPlan", fri.AddDays(1).AddMinutes(50), fri, 180, 0, 40, true)!);
	Check("day boundary: Friday doesn't start while Thursday's 00:40 bedtime is still to come",
		!(bool) Static("NewDayMayStart", fri.AddMinutes(10), fri, fri.AddMinutes(40))! && (bool) Static("NewDayMayStart", fri.AddMinutes(50), fri, fri.AddMinutes(40))!);
	Check("day boundary: up at 4 with bed at 5 the next morning is a 25-hour window, not one hour",
		((int) Static("WindowMinutes", 240, 5, 0, true)! == 25 * 60) && ((int) Static("WindowMinutes", 600, 22, 0, false)! == 12 * 60));

	MethodInfo rollDay = ht.GetMethod("RollDay", Stat)!;
	(int Wake, int BedHour, int BedMinute, bool BedTomorrow, int Target, int Share, int Budget, int SignOuts, int Meals) Roll(NocatFarm.Config.BotConfig c, DateTime date, DateTime lastBed, Random r) {
		object d = rollDay.Invoke(null, [c, date, 70, true, lastBed, r])!;
		int I(string n) => (int) d.GetType().GetProperty(n)!.GetValue(d)!;
		return (I("WakeMinute"), I("BedHour"), I("BedMinute"), (bool) d.GetType().GetProperty("BedIsTomorrow")!.GetValue(d)!, I("Target"), I("MainSharePct"), I("OtherBudget"), I("SignOutCap"), I("MealCap"));
	}

	DateTime satDate = new(2026, 10, 3), friBed = satDate.AddMinutes(90);   // Friday went to bed at 01:30
	var upAt1 = new NocatFarm.Config.BotConfig { DayStartHour = 1, BedHour = 23 };
	Random rr = new(11);
	int tooSoon = 0;

	for (int i = 0; i < 2000; i++) {
		tooSoon += satDate.AddMinutes(Roll(upAt1, satDate, friBed, rr).Wake) < friBed.AddHours(4) ? 1 : 0;
	}

	Check("day boundary: with the day starting at 1, Saturday never gets up before Friday's 01:30 bedtime and a night's sleep", tooSoon == 0, $"{tooSoon} of 2000");

	string dname = "harness-boundary-" + Guid.NewGuid().ToString("N")[..6];
	var dbot = new NocatFarm.Core.Bot(dname, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730", DayStartHour = 0, BedHour = 20 });
	var dh = new NocatFarm.Modules.HumanMode(dbot);
	dbot.AddModule(dh);
	NocatFarm.Modules.HumanDay.Forget(dname);
	DateTime today = DateTime.Now.Date;
	Set(dh, "_dayStamp", today.AddDays(-1).DayOfYear);   // last night's plan: bed 23:00 yesterday...
	Set(dh, "_wakeMinuteOfDay", 60);
	Set(dh, "_bedHour", 23);
	Set(dh, "_bedMinute", 0);
	Set(dh, "_bedIsTomorrow", false);
	Set(dh, "_stayUpUntil", DateTime.Now.AddHours(1));   // ...and a 'wake' sitting after it, still going
	NocatFarm.Log.Suppressed = true;
	HCall(dh, "RollNewDayIfNeeded", false);
	NocatFarm.Log.Suppressed = false;
	Check("day boundary: last night's 'wake' sitting still going - the new day waits for it", HGet<int>(dh, "_dayStamp") == today.AddDays(-1).DayOfYear);
	Set(dh, "_stayUpUntil", DateTime.MinValue);
	NocatFarm.Log.Suppressed = true;
	HCall(dh, "RollNewDayIfNeeded", false);
	NocatFarm.Log.Suppressed = false;
	int newWake = HGet<int>(dh, "_wakeMinuteOfDay");
	Check("day boundary: once last night is over the day starts - up no sooner than a night's sleep after it, the 'wake' sitting gone",
		(HGet<int>(dh, "_dayStamp") == today.DayOfYear) && (today.AddMinutes(newWake) >= today.AddHours(-1).AddHours(4)) && (HGet<DateTime>(dh, "_stayUpUntil") == DateTime.MinValue),
		$"up {newWake / 60:00}:{newWake % 60:00}");
	NocatFarm.Modules.HumanDay.Forget(dname);
	await dbot.DisposeAsync();

	// Audit: 'human reroll' on an account that's up keeps it up, whatever wake time the new plan rolled.
	string rname = "harness-reroll-" + Guid.NewGuid().ToString("N")[..6];
	var rbot = new NocatFarm.Core.Bot(rname, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730", DayOffChancePct = 0 });
	var rh = new NocatFarm.Modules.HumanMode(rbot);
	rbot.AddModule(rh);
	AwakeAllDay(rh);
	NocatFarm.Log.Suppressed = true;
	rh.RerollToday();
	NocatFarm.Log.Suppressed = false;
	Check("human reroll: an account that's up stays up", (bool) HCall(rh, "InWakingHours", DateTime.Now)!);
	NocatFarm.Modules.HumanDay.Forget(rname);
	await rbot.DisposeAsync();

	// (e) Done for today and a day off are what they are - online, not asleep - on every screen.
	string ename = "harness-done-" + Guid.NewGuid().ToString("N")[..6];
	var ebot = new NocatFarm.Core.Bot(ename, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730" });
	var eh = new NocatFarm.Modules.HumanMode(ebot);
	ebot.AddModule(eh);
	BotSet(ebot, "State", NocatFarm.Core.BotState.Online);
	BotSet(ebot, "OnlineSince", DateTime.UtcNow.AddHours(-2));
	MethodInfo groupOf = typeof(NocatFarm.Web.WebHost).GetMethod("GroupOf", BindingFlags.NonPublic | BindingFlags.Static)!;
	MethodInfo cardLine = typeof(NocatFarm.Core.DiscordPresence).GetMethod("Details", Stat)!;
	(string Group, string State, string Card, string Doing) Shown(NocatFarm.Modules.HumanMode.Phase p) {
		Set(eh, "_phase", p);
		return ((string) groupOf.Invoke(null, [ebot])!, Commands.StateWord(ebot), cardLine.Invoke(null, [new[] { ebot }])!.ToString()!, NocatFarm.Core.BotStatus.Of(ebot).Doing.ToString());
	}

	var done = Shown(NocatFarm.Modules.HumanMode.Phase.DoneForToday);
	var dayOff = Shown(NocatFarm.Modules.HumanMode.Phase.DayOff);
	var asleep = Shown(NocatFarm.Modules.HumanMode.Phase.Asleep);
	var sitting = Shown(Playing);
	Check("done for today: its own group, not 'asleep' - the dashboard, status, the Discord card and the board agree",
		done == ("done", "done today", "Done for today", "done for today"), $"{done}");
	Check("day off: its own group, not 'asleep'", dayOff == ("dayoff", "day off", "Day off", "not playing today"), $"{dayOff}");
	Check("asleep is still asleep, and a sitting still reads as working on the Discord card", (asleep.Group == "asleep") && (asleep.Card == "Asleep") && (sitting.Card == "Idling games"),
		$"{asleep} / {sitting}");
	BotSet(ebot, "State", NocatFarm.Core.BotState.Stopped);
	await ebot.DisposeAsync();

	// Every group the server can send has a chip on the dashboard - one without took the accounts page down with it.
	string? appJs = null;

	for (DirectoryInfo? dir = new(AppContext.BaseDirectory); (dir != null) && (appJs == null); dir = dir.Parent) {
		string candidate = Path.Combine(dir.FullName, "src", "NocatFarm", "wwwroot", "app.js");
		appJs = File.Exists(candidate) ? File.ReadAllText(candidate) : null;
	}

	int metaAt = appJs?.IndexOf("const STATUS_META", StringComparison.Ordinal) ?? -1;
	string meta = (metaAt < 0) || (appJs == null) ? "" : appJs[metaAt..appJs.IndexOf("};", metaAt, StringComparison.Ordinal)];
	Check("dashboard: 'done' and 'dayoff' have their own chips", meta.Contains("\n  done:", StringComparison.Ordinal) && meta.Contains("\n  dayoff:", StringComparison.Ordinal),
		appJs == null ? "app.js not found" : "");

	// Audit: achievements don't pop at 4am, whatever "Only react while awake" says - unlock times are public.
	var pbot = new NocatFarm.Core.Bot("harness-pacer", new NocatFarm.Config.BotConfig { LegitMode = true, ActOnlyWhileAwake = false });
	var ph = new NocatFarm.Modules.HumanMode(pbot);
	pbot.AddModule(ph);
	Set(ph, "_ticked", true);
	MethodInfo mayUnlock = typeof(NocatFarm.Modules.AchievementPacer).GetMethod("MayUnlockNow", Stat)!;
	Set(ph, "_phase", NocatFarm.Modules.HumanMode.Phase.Asleep);
	bool unlocksAsleep = (bool) mayUnlock.Invoke(null, [pbot])!;
	Set(ph, "_phase", Playing);
	bool unlocksUp = (bool) mayUnlock.Invoke(null, [pbot])!;
	Check("achievements: nothing unlocks while it's asleep, even with 'Only react while awake' off", !unlocksAsleep && unlocksUp);
	await pbot.DisposeAsync();
}

// ── human mode, simulated: a year of days and months of sittings keep every promise the settings make ──────────
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	Type ht = typeof(NocatFarm.Modules.HumanMode);
	FieldInfo HF(string f) => ht.GetField(f, Inst) ?? throw new MissingFieldException(f);
	T HGet<T>(object h, string f) => (T) HF(f).GetValue(h)!;
	void Set(object h, string f, object? v) => HF(f).SetValue(h, v);
	object? HCall(object h, string m, params object?[] args) => ht.GetMethod(m, Inst)!.Invoke(h, args);
	MethodInfo rollDay = ht.GetMethod("RollDay", Stat)!;
	(int Wake, int BedHour, int BedMinute, bool BedTomorrow, int Target, int Share, int Budget, int SignOuts, int Meals) Roll(NocatFarm.Config.BotConfig c, DateTime date, DateTime lastBed, Random r) {
		object d = rollDay.Invoke(null, [c, date, 70, true, lastBed, r])!;
		int I(string n) => (int) d.GetType().GetProperty(n)!.GetValue(d)!;
		return (I("WakeMinute"), I("BedHour"), I("BedMinute"), (bool) d.GetType().GetProperty("BedIsTomorrow")!.GetValue(d)!, I("Target"), I("MainSharePct"), I("OtherBudget"), I("SignOutCap"), I("MealCap"));
	}

	// Days: every setting shape, a dozen seeds, a year and a bit each - both clock changes and a New Year included.
	(string Name, NocatFarm.Config.BotConfig Cfg)[] shapes = [
		("the defaults", new()),
		("early bird, very late weekends", new() { DayStartHour = 6, BedHour = 22, LateNightExtraHours = 6 }),
		("night shift", new() { DayStartHour = 1, BedHour = 17 }),
		("up at 2, bed at 11", new() { DayStartHour = 2, BedHour = 23 }),
		("up at midnight, long days", new() { DayStartHour = 0, BedHour = 20, WeekdayHours = 10, WeekendHours = 14 }),
		("owl", new() { DayStartHour = 16, BedHour = 6 }),
	];

	foreach ((string name, NocatFarm.Config.BotConfig cfg) in shapes) {
		int days = 0, offs = 0, backwards = 0, overlaps = 0, shortNights = 0, overWindow = 0, sameWake = 0, overCaps = 0, shareOut = 0;
		double freeBed = 0, schoolBed = 0;
		int freeNights = 0, schoolNights = 0;

		for (int seed = 1; seed <= 12; seed++) {
			Random r = new(seed * 7919);
			DateTime lastBed = DateTime.MinValue;
			int lastWake = -1;

			for (DateTime date = new(2026, 1, 1); date < new DateTime(2027, 1, 12); date = date.AddDays(1)) {
				var d = Roll(cfg, date, lastBed, r);
				DateTime up = date.AddMinutes(d.Wake);
				DateTime bed = date.AddDays(d.BedTomorrow ? 1 : 0).AddHours(d.BedHour).AddMinutes(d.BedMinute);
				int window = (int) (bed - up).TotalMinutes;
				days++;
				offs += d.Target == 0 ? 1 : 0;
				backwards += bed <= up ? 1 : 0;
				overlaps += (lastBed != DateTime.MinValue) && (up < lastBed) ? 1 : 0;
				shortNights += (lastBed != DateTime.MinValue) && (up < lastBed.AddHours(4)) ? 1 : 0;
				overWindow += (d.Target > window) || (d.Target > Math.Max(30, window * 4 / 5)) ? 1 : 0;
				sameWake += d.Wake == lastWake ? 1 : 0;
				overCaps += (d.SignOuts > cfg.MaxSignOutsPerDay) || (d.Meals > cfg.MealBreaksPerDay) ? 1 : 0;
				shareOut += (d.Share < 60) || (d.Share > 80) ? 1 : 0;

				double bedAfter = (bed - date).TotalHours;

				if (date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) {
					freeBed += bedAfter;
					freeNights++;
				} else {
					schoolBed += bedAfter;
					schoolNights++;
				}

				lastBed = bed;
				lastWake = d.Wake;
			}
		}

		double offRate = (double) offs / days;
		Check($"simulated year, {name}: bed always comes after getting up", backwards == 0, $"{backwards} of {days}");
		Check($"simulated year, {name}: never up before last night's bedtime, and always a night's sleep (4h+) between", (overlaps == 0) && (shortNights == 0),
			$"{overlaps} overlaps, {shortNights} short nights of {days}");
		Check($"simulated year, {name}: the day's hours always fit its waking window", overWindow == 0, $"{overWindow} of {days}");
		Check($"simulated year, {name}: a day off about one day in twenty (5%)", Math.Abs(offRate - 0.05) < 0.015, $"{offRate:P1}");
		Check($"simulated year, {name}: never up at the same minute two days running more than now and then", sameWake < days / 50, $"{sameWake} of {days}");
		Check($"simulated year, {name}: sign-outs and meals within 'at most', main game's share within 10 of its number",
			(overCaps == 0) && (shareOut == 0), $"{overCaps} over caps, {shareOut} shares out");
		Check($"simulated year, {name}: Friday and Saturday nights go later", freeBed / freeNights > schoolBed / schoolNights,
			$"{freeBed / freeNights:0.0}h vs {schoolBed / schoolNights:0.0}h after the day's midnight");
	}

	// Sittings: twenty months of days through the real session picker and the real banking, cards farmed in some
	// sittings every other day. The clock is kept out of it (up round the clock) - this is about the mix.
	string simName = "harness-sim-" + Guid.NewGuid().ToString("N")[..6];
	var simCfg = new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:20, 550:10", PureMainDayChancePct = 0,
		FarmCardsWhen = NocatFarm.Modules.FarmWhen.Mixed, CardSittingsPct = 40, FarmFromHour = 0, FarmUntilHour = 0 };
	var simBot = new NocatFarm.Core.Bot(simName, simCfg);
	var sim = new NocatFarm.Modules.HumanMode(simBot);
	var simFarmer = new NocatFarm.Modules.CardFarmer(simBot);
	simBot.AddModule(sim);
	HF("_rng").SetValue(sim, new Random(5));   // the same sittings every run
	simBot.AddModule(simFarmer);
	DateTime simLogon = DateTime.UtcNow.AddHours(-1);
	typeof(NocatFarm.Core.Bot).GetProperty("OnlineSince")!.SetValue(simBot, simLogon);
	typeof(NocatFarm.Modules.CardFarmer).GetField("_nextGame", Inst)!.SetValue(simFarmer, 999u);
	Random dayRng = new(4242);
	double[] mainMin = [0, 0], sideMin = [0, 0];   // [no cards, cards]
	double farmMin = 0;
	int sittings = 0, cardDaySittings = 0, farmSittings = 0, longest = 0, didntStart = 0, overDay = 0;
	NocatFarm.Log.Suppressed = true;

	for (int day = 0; day < 600; day++) {
		int cards = day % 2;
		typeof(NocatFarm.Core.Bot).GetProperty("CardsRemaining")!.SetValue(simBot, cards == 1 ? 50 : 0);
		var plan = Roll(simCfg, new DateTime(2026, 3, 2).AddDays(day), DateTime.MinValue, dayRng);

		if (plan.Target == 0) {
			continue;
		}

		Set(sim, "_dayStamp", DateTime.Now.DayOfYear);
		Set(sim, "_wakeMinuteOfDay", 0);
		Set(sim, "_bedHour", 23);
		Set(sim, "_bedMinute", 59);
		Set(sim, "_bedIsTomorrow", true);
		Set(sim, "_stayUpUntil", DateTime.MinValue);
		Set(sim, "_targetMinutes", plan.Target);
		Set(sim, "_mainSharePct", plan.Share);
		Set(sim, "_otherBudget", plan.Budget);
		Set(sim, "_playedMinutesToday", 0);
		Set(sim, "_otherPlayed", 0);
		Set(sim, "_farmPlayed", 0);
		Set(sim, "_firstSessionOfDay", true);
		Set(sim, "_lastGame", 0u);
		Set(sim, "_switchingTo", 0u);

		while (HGet<int>(sim, "_playedMinutesToday") < plan.Target) {
			Set(sim, "_phase", NocatFarm.Modules.HumanMode.Phase.Off);
			HCall(sim, "StartSession");

			if ((NocatFarm.Modules.HumanMode.Phase) HF("_phase").GetValue(sim)! != NocatFarm.Modules.HumanMode.Phase.Playing) {
				didntStart++;

				break;
			}

			uint game = HGet<uint>(sim, "_game");
			int m = (int) Math.Round((HGet<DateTime>(sim, "_sessionEnds") - HGet<DateTime>(sim, "_sessionStarted")).TotalMinutes);
			bool farming = HGet<bool>(sim, "_farmSitting");
			sittings++;
			cardDaySittings += cards;
			farmSittings += farming ? 1 : 0;
			longest = Math.Max(longest, m);

			// Banked by the real banking, as the day's ticks would.
			Set(sim, "_bankedForLogon", simLogon);
			Set(sim, "_bankedTo", DateTime.UtcNow.AddMinutes(-m));
			Set(sim, "_lastBankAt", DateTime.UtcNow.AddSeconds(-10));
			HCall(sim, "BankSession");

			if (farming) {
				farmMin += m;
			} else if (game == 730) {
				mainMin[cards] += m;
			} else {
				sideMin[cards] += m;
			}
		}

		overDay += HGet<int>(sim, "_playedMinutesToday") > plan.Target + 15 ? 1 : 0;
	}

	NocatFarm.Log.Suppressed = false;
	NocatFarm.Modules.HumanDay.Forget(simName);
	await simBot.DisposeAsync();

	double plainMain = mainMin[0] / (mainMin[0] + sideMin[0]), cardMain = mainMin[1] / (mainMin[1] + sideMin[1]);
	double farmShare = (double) farmSittings / cardDaySittings;
	Check("simulated sittings: every one starts, none longer than 'longest sitting' (150m)", (didntStart == 0) && (longest <= 150), $"{didntStart} didn't start, longest {longest}m");
	Check("simulated sittings: a day never runs more than a few minutes past its hours", overDay == 0, $"{overDay} days over");
	Check("simulated sittings: the main game gets about its 70% of the usual games' time", Math.Abs(plainMain - 0.70) < 0.05, $"{plainMain:P1}");
	Check("simulated sittings: ...on days cards farm in some sittings too - farming doesn't eat the side games' share", Math.Abs(cardMain - 0.70) < 0.05, $"{cardMain:P1}");
	Check("simulated sittings: about 40% of the sittings farm cards on a card day (CardSittingsPct)", Math.Abs(farmShare - 0.40) < 0.06, $"{farmShare:P1} of {cardDaySittings}");
}

// ── Discord card: the counter fits on the line Discord shows, or goes short ────────────────────────────────────
{
	string Line(string l, int c, int t) => NocatFarm.Core.DiscordPresence.WithCounter(l, c, t);
	int fits = NocatFarm.Core.DiscordPresence.LineFits;
	Check("discord counter: room for it - the long words", Line("12 hrs past week", 3, 3) == "12 hrs past week · 3 accounts linked" || Line("12 hrs past week", 3, 3).Length <= fits, Line("12 hrs past week", 3, 3));
	Check("discord counter: 591 hrs past week · 3 accounts linked fits whole", Line("591 hrs past week", 3, 3) == "591 hrs past week · 3 accounts linked", Line("591 hrs past week", 3, 3));
	Check("discord counter: a longer line goes short instead of being cut off", Line("1,591 hrs past month", 3, 3) == "1,591 hrs past month · 3 linked", Line("1,591 hrs past month", 3, 3));
	Check("discord counter: 2 of 3, short when it has to be", Line("591 hrs past week", 2, 3) == "591 hrs past week · 2 of 3 linked", Line("591 hrs past week", 2, 3));
	Check("discord counter: the long form is only used when it fits", new[] { ("a", 3, 3), ("kylro · old", 2, 3), ("8 cards today", 1, 1) }.All(x => { string r = Line(x.Item1, x.Item2, x.Item3); return !r.Contains("accounts") || r.Length <= fits; }));
}

// ── failures in the log: the reason is written down, the secrets never are ─────────────────────────────────────
{
	string api = NocatFarm.Log.Scrub("GET /IPlayerService/GetOwnedGames/v1/?access_token=eyJhbGciOiJFZERTQSJ9.eyJpc3MiOiJyOjE.c2ln&steamid=7656 -> 401");
	Check("scrub: a Web API access_token is hidden", !api.Contains("eyJ") && api.Contains("access_token=[hidden]") && api.Contains("steamid=7656"), api);
	string jwt = NocatFarm.Log.Scrub("token was eyJhbGciOiJFZERTQSJ9.eyJpc3MiOiJyOjE.c2lnbmF0dXJl, refused");
	Check("scrub: a bare Steam JWT is hidden", !jwt.Contains("eyJpc3M") && jwt.Contains("[hidden]") && jwt.EndsWith(", refused"), jwt);
	string cookie = NocatFarm.Log.Scrub("Cookie: steamLoginSecure=76561198000000000%7C%7CeyJabc; sessionid=0123456789abcdef01234567");
	Check("scrub: cookies are hidden", !cookie.Contains("76561198000000000") && !cookie.Contains("0123456789abcdef"), cookie);
	string auth = NocatFarm.Log.Scrub("Authorization: Bot MTIzNDU2Nzg5MDEyMzQ1Njc4OTA.GhIjKl.abcdefghijklmnopqrstuvwxyz");
	Check("scrub: a Discord bot token in a header is hidden", !auth.Contains("MTIzNDU2") && auth.Contains("Bot [hidden]"), auth);
	string form = NocatFarm.Log.Scrub("POST apiToken body key=ABCDEF0123456789&password=hunter2&steamid=1");
	Check("scrub: key= and password= are hidden", !form.Contains("ABCDEF0123456789") && !form.Contains("hunter2") && form.Contains("steamid=1"), form);
	string r4rKey = NocatFarm.Log.Scrub("rep4rep said no to /pub-api/tasks?apiToken=abcdef123456&steamProfile=7656");
	Check("scrub: rep4rep's apiToken is hidden", !r4rKey.Contains("abcdef123456") && r4rKey.Contains("steamProfile=7656"), r4rKey);
	string proxy = NocatFarm.Log.Scrub("bad proxy 'http://farmer:s3cret@10.0.0.2:8080' (UriFormatException)");
	Check("scrub: a proxy's user and password are hidden, the address kept", !proxy.Contains("s3cret") && !proxy.Contains("farmer") && proxy.Contains("http://[hidden]@10.0.0.2:8080"), proxy);
	string plain = "trade offer #123 refused: AccessDenied (15) on steamcommunity.com/tradeoffer/123/accept";
	Check("scrub: an ordinary failure line is left alone", NocatFarm.Log.Scrub(plain) == plain, NocatFarm.Log.Scrub(plain));

	string tg = NocatFarm.Log.Where(new Uri("https://api.telegram.org/bot123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw/getUpdates?offset=5&timeout=50"));
	Check("where: a Telegram URL loses the bot token and the query", tg == "api.telegram.org/bot[hidden]/getUpdates", tg);
	string hook = NocatFarm.Log.Where(new Uri("https://discord.com/api/webhooks/1234567890/abcDEF-ghi_jkl.mno"));
	Check("where: a Discord webhook loses its secret", hook == "discord.com/api/webhooks/1234567890/[hidden]", hook);
	string r4r = NocatFarm.Log.Where(new Uri("https://rep4rep.com/pub-api/tasks?apiToken=secret123&steamProfile=7656"));
	Check("where: host and path, never the query", r4r == "rep4rep.com/pub-api/tasks", r4r);
	Check("where: null says so rather than throwing", NocatFarm.Log.Where(null) == "?");

	// The file itself: a caught failure, a crash with its stack, and a background task nobody awaited.
	string logRoot = Path.Combine(Path.GetTempPath(), "nf-logfail-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(logRoot);
	NocatFarm.Log.Suppressed = true;
	NocatFarm.Log.Configure(fileLogging: true, debug: false, logRoot, retentionDays: 14);
	string logFile = Path.Combine(logRoot, "logs", $"nocatFarm-{DateTime.Now:yyyy-MM-dd}.log");

	NocatFarm.Log.Failed("couldn't save the drop run", new IOException("disk full, token=abc123def"), "acct1");
	int written = 0;
	for (int i = 0; i < 5; i++) {
		written += NocatFarm.Log.DebugOnChange("test:acct1", "PICS asked too often: RateLimitExceeded", "acct1") ? 1 : 0;
	}
	int changed = NocatFarm.Log.DebugOnChange("test:acct1", "PICS asked too often: Timeout", "acct1") ? 1 : 0;
	NocatFarm.Log.Recovered("test:acct1");
	int afterRecover = NocatFarm.Log.DebugOnChange("test:acct1", "PICS asked too often: Timeout", "acct1") ? 1 : 0;

	try {
		throw new InvalidOperationException("boom from the crash test");
	} catch (InvalidOperationException e) {
		NocatFarm.Log.Crash(new NocatFarm.Core.Said("crashed: {0}", NocatFarm.Log.Describe(e)), e);
	}

	TaskScheduler.UnobservedTaskException += NocatFarm.Log.OnUnobservedTask;
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	static void Abandon() {
		Task t = Task.Run(static () => throw new FormatException("nobody awaited this"));
		SpinWait.SpinUntil(() => t.IsCompleted, 5000);   // IsCompleted doesn't observe the exception - it is left for the GC
	}
	Abandon();
	for (int i = 0; i < 3; i++) {
		GC.Collect();
		GC.WaitForPendingFinalizers();
	}
	TaskScheduler.UnobservedTaskException -= NocatFarm.Log.OnUnobservedTask;

	NocatFarm.Log.Configure(fileLogging: false, debug: false, logRoot);
	NocatFarm.Log.Suppressed = false;
	string[] lines = File.Exists(logFile) ? File.ReadAllLines(logFile) : [];

	Check("log file: a caught failure says what, whose, the type and the reason", lines.Any(l => l.Contains("|DEBUG|acct1|couldn't save the drop run: IOException: disk full, token=[hidden]")), string.Join(" / ", lines.Take(3)));
	Check("log file: the same failure in a loop is written once", (written == 1) && (lines.Count(l => l.Contains("RateLimitExceeded")) == 1), $"{written} written");
	Check("log file: a different failure, or the same one after a recovery, is written again", (changed == 1) && (afterRecover == 1) && (lines.Count(l => l.Contains("PICS asked too often: Timeout")) == 2));
	Check("log file: a crash is an ERROR line with the reason", lines.Any(l => l.Contains("|ERROR|nocat.farm|crashed: InvalidOperationException: boom from the crash test")));
	Check("log file: ...followed by its stack trace", lines.Any(l => l.Contains("|DEBUG|nocat.farm|   at ") && l.Contains("Program")));
	Check("log file: a background task nobody awaited is written down with its stack", lines.Any(l => l.Contains("|ERROR|nocat.farm|a background task failed: FormatException: nobody awaited this"))
		&& lines.Any(l => l.Contains("|DEBUG|nocat.farm|") && l.Contains("FormatException: nobody awaited this") && !l.Contains("|ERROR|")), $"{lines.Length} lines");

	try {
		Directory.Delete(logRoot, true);
	} catch (IOException) {
		// a temp folder - the OS clears it
	}
}

// ── a new comment: who wrote it and what it says, read off Steam's comment list ──────────────────────────────────
{
	string html = "\t<div class=\"commentthread_comment responsive_body_text   \" id=\"comment_1\"><div class=\"commentthread_comment_author\">"
		+ "<a class=\"hoverunderline commentthread_author_link\" href=\"https://steamcommunity.com/profiles/1\"><bdi>AEZAKMI</bdi></a>"
		+ "<div class=\"commentthread_comment_timestamp\" title=\"x\" data-timestamp=\"1790720459\">12 minutes ago&nbsp;</div></div>"
		+ "<div class=\"commentthread_comment_text\" id=\"comment_content_1\">\n\t\t\t\t+rep pretty good player &amp; nice\t\t\t</div></div>"
		+ "<div class=\"commentthread_comment responsive_body_text   \" id=\"comment_2\"><div class=\"commentthread_comment_author\">"
		+ "<a class=\"hoverunderline commentthread_author_link\" href=\"x\"><bdi>-thats cute</bdi></a>"
		+ "<div class=\"commentthread_comment_timestamp\" data-timestamp=\"1790533206\">x</div></div>"
		+ "<div class=\"commentthread_comment_text\" id=\"comment_content_2\">This comment is awaiting analysis by our automated content check system.</div></div>";
	List<NocatFarm.Core.Bot.ProfileComment> c = NocatFarm.Core.Bot.ReadComments(html);
	Check("comments: both read, newest first", c.Count == 2 && c[0].At == 1790720459 && c[1].At == 1790533206, string.Join(" | ", c));
	Check("comments: who wrote it and what it says, as plain text", (c.Count > 0) && (c[0].Author == "AEZAKMI") && (c[0].Text == "+rep pretty good player & nice"), c.Count > 0 ? $"{c[0].Author}: {c[0].Text}" : "");
	Check("comments: nothing there reads as none, not a crash", NocatFarm.Core.Bot.ReadComments("<div>no comments</div>").Count == 0);
}

// SETTINGSCOUNT
Console.WriteLine($"settings: {NocatFarm.Config.Settings.Global.Count} global ({NocatFarm.Config.Settings.Global.Count(d => !d.Advanced)} basic), {NocatFarm.Config.Settings.Bot.Count} per account ({NocatFarm.Config.Settings.Bot.Count(d => !d.Advanced)} basic)");
Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
return fails;
