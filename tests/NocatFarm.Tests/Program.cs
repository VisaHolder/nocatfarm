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
// How many the store itself says it has - giveaways come and go, and "none right now" read as none is right.
int storeHas = -1;
try {
	using HttpClient web = new() { Timeout = TimeSpan.FromSeconds(20) };
	string search = await web.GetStringAsync("https://store.steampowered.com/search/results/?maxprice=free&specials=1&infinite=1&count=50&l=english");
	storeHas = System.Text.Json.JsonDocument.Parse(search).RootElement.GetProperty("total_count").GetInt32();
} catch (Exception) {
	// the store didn't answer - judged on what the reader found
}
Check("store search: reads what the store is giving away", storeHas == 0
	? giveaways.Count == 0
	: giveaways.Count > 0 && giveaways.All(t => t.StartsWith("g/") || t.StartsWith("s/")),
	storeHas == 0 ? "the store has no giveaways right now" : string.Join(", ", giveaways));
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
	Check("commands: farm and transfer are gone", Canon("farm") == null && Canon("transfer") == null);

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
		(parts.Count == 2) && big.StartsWith(shown[..shown.LastIndexOf('\n')], StringComparison.Ordinal) && shown.EndsWith("type it in the dashboard's Console for the whole reply", StringComparison.Ordinal));
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
	Check("confirm: 'confirm' has to be a word of its own - '/remove autoconfirm' is held, not run as 'remove auto'", Guard("remove", "autoconfirm") == ("remove", "autoconfirm"));

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
	MethodInfo meanLength = ht.GetMethod("MeanLength", BindingFlags.NonPublic | BindingFlags.Static)!;
	double MeanOf(bool main) => (double) meanLength.Invoke(null, [main, 30, 150, 0, -1, 0, int.MaxValue, 0])!;
	double w = (double) mainWeight.Invoke(null, [30, 70, MeanOf(true), MeanOf(false)])!;
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
		bool isMain = pick.NextDouble() * (w + 30) < w;
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
	Set(fh, "_otherBudget", 1_000_000);     // with side-game time left to give
	double mw = (double) Static("MainWeight", 30, 70, (double) Static("MeanLength", true, 30, 150, 0, -1, 0, int.MaxValue, 0)!,
		(double) Static("MeanLength", false, 30, 150, 0, -1, 0, int.MaxValue, 0)!)!;
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

	// Sittings: years of days through the real session picker and the real banking, cards farmed in some sittings every
	// other day. The clock is kept out of it (up round the clock) - this is about the mix. Several shapes of settings,
	// because a lean that cancels out on the defaults can still show on a main game set to 50 or 85, or on short
	// sittings where the day's first and last are most of it.
	MethodInfo rollMix = ht.GetMethod("RollDay", Stat)!;
	(double Plain, double Card, double Farm, int CardSittings, int DidntStart, int Longest, int OverDay, double NoSideDays, double MainRun) Simulate(string weights,
		int centre, Action<NocatFarm.Config.BotConfig> tweak, int days, int seed) {
		string simName = "harness-sim-" + Guid.NewGuid().ToString("N")[..6];
		var simCfg = new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = weights, PureMainDayChancePct = 0,
			FarmCardsWhen = NocatFarm.Modules.FarmWhen.Mixed, CardSittingsPct = 40, FarmFromHour = 0, FarmUntilHour = 0 };
		tweak(simCfg);
		var simBot = new NocatFarm.Core.Bot(simName, simCfg);
		var sim = new NocatFarm.Modules.HumanMode(simBot);
		var simFarmer = new NocatFarm.Modules.CardFarmer(simBot);
		simBot.AddModule(sim);
		HF("_rng").SetValue(sim, new Random(seed));   // the same sittings every run
		simBot.AddModule(simFarmer);
		DateTime simLogon = DateTime.UtcNow.AddHours(-1);
		typeof(NocatFarm.Core.Bot).GetProperty("OnlineSince")!.SetValue(simBot, simLogon);
		typeof(NocatFarm.Modules.CardFarmer).GetField("_nextGame", Inst)!.SetValue(simFarmer, 999u);
		Random dayRng = new(seed * 811);
		double[] mainMin = [0, 0], sideMin = [0, 0];   // [no cards, cards]
		int cardDaySittings = 0, farmSittings = 0, longest = 0, didntStart = 0, overDay = 0, plainDays = 0, noSideDays = 0, runs = 0, runSittings = 0;
		NocatFarm.Log.Suppressed = true;

		for (int day = 0; day < days; day++) {
			int cards = day % 2;
			typeof(NocatFarm.Core.Bot).GetProperty("CardsRemaining")!.SetValue(simBot, cards == 1 ? 50 : 0);
			object plan = rollMix.Invoke(null, [simCfg, new DateTime(2026, 3, 2).AddDays(day), centre, true, DateTime.MinValue, dayRng])!;
			int P(string n) => (int) plan.GetType().GetProperty(n)!.GetValue(plan)!;
			int target = P("Target");

			if (target == 0) {
				continue;
			}

			// No plan in force: nothing is saved (writing the day to disk after every sitting was most of the time years of
			// sittings took), and with no bedtime nothing is cut short by one - which is the point here.
			Set(sim, "_dayStamp", -1);
			Set(sim, "_stayUpUntil", DateTime.MinValue);
			Set(sim, "_targetMinutes", target);
			Set(sim, "_mainSharePct", P("MainSharePct"));
			Set(sim, "_otherBudget", P("OtherBudget"));
			Set(sim, "_playedMinutesToday", 0);
			Set(sim, "_otherPlayed", 0);
			Set(sim, "_farmPlayed", 0);
			Set(sim, "_firstSessionOfDay", true);
			Set(sim, "_lastGame", 0u);
			Set(sim, "_switchingTo", 0u);
			int run = 0;
			double sideToday = 0;

			while (HGet<int>(sim, "_playedMinutesToday") < target) {
				Set(sim, "_phase", NocatFarm.Modules.HumanMode.Phase.Off);
				HCall(sim, "StartSession");

				if ((NocatFarm.Modules.HumanMode.Phase) HF("_phase").GetValue(sim)! != NocatFarm.Modules.HumanMode.Phase.Playing) {
					didntStart++;

					break;
				}

				uint game = HGet<uint>(sim, "_game");
				int m = (int) Math.Round((HGet<DateTime>(sim, "_sessionEnds") - HGet<DateTime>(sim, "_sessionStarted")).TotalMinutes);
				bool farming = HGet<bool>(sim, "_farmSitting");
				cardDaySittings += cards;
				farmSittings += farming ? 1 : 0;
				longest = Math.Max(longest, m);

				// Banked by the real banking, as the day's ticks would.
				Set(sim, "_bankedForLogon", simLogon);
				Set(sim, "_bankedTo", DateTime.UtcNow.AddMinutes(-m));
				Set(sim, "_lastBankAt", DateTime.UtcNow.AddSeconds(-10));
				HCall(sim, "BankSession");

				if (farming) {
					continue;
				}

				// Main-game sittings in a row, counted on the days without cards.
				if (game == 730) {
					mainMin[cards] += m;
					run += 1 - cards;
				} else {
					sideMin[cards] += m;
					sideToday += m;
					runSittings += run;
					runs += run > 0 ? 1 : 0;
					run = 0;
				}
			}

			runSittings += run;
			runs += run > 0 ? 1 : 0;
			overDay += HGet<int>(sim, "_playedMinutesToday") > target + 15 ? 1 : 0;

			if (cards == 0) {
				plainDays++;
				noSideDays += sideToday == 0 ? 1 : 0;
			}
		}

		NocatFarm.Log.Suppressed = false;
		NocatFarm.Modules.HumanDay.Forget(simName);
		simBot.DisposeAsync().AsTask().GetAwaiter().GetResult();

		return (mainMin[0] / (mainMin[0] + sideMin[0]), mainMin[1] / (mainMin[1] + sideMin[1]), (double) farmSittings / Math.Max(1, cardDaySittings), cardDaySittings,
			didntStart, longest, overDay, (double) noSideDays / Math.Max(1, plainDays), (double) runSittings / Math.Max(1, runs));
	}

	// The main game's share, over a long run, within half a point of what it's set to - whatever the settings look like.
	// Forty thousand days each: the days are lumpy (a handful of sittings, each a big piece of the day), so a shorter
	// run wanders by more than the half point being checked.
	(string Name, string Weights, int Centre, Action<NocatFarm.Config.BotConfig> Tweak)[] mixes = [
		("70/20/10, the defaults", "730:70, 440:20, 550:10", 70, static _ => { }),
		("50/30/20", "730:50, 440:30, 550:20", 50, static _ => { }),
		("85/15", "730:85, 440:15", 85, static _ => { }),
		("70/30, short sittings (20-60m)", "730:70, 440:30", 70, static c => { c.SessionMinMinutes = 20; c.SessionMaxMinutes = 60; }),
		("60/25/15, long sittings (45-240m) on short days", "730:60, 440:25, 550:15", 60, static c => { c.SessionMinMinutes = 45; c.SessionMaxMinutes = 240; c.WeekdayHours = 3; c.WeekendHours = 5; }),
	];

	foreach ((string mixName, string mixWeights, int centre, Action<NocatFarm.Config.BotConfig> tweak) in mixes) {
		var s = Simulate(mixWeights, centre, tweak, 40_000, 5);
		double want = centre / 100.0;

		if (mixName.EndsWith("the defaults", StringComparison.Ordinal)) {
			Check("simulated sittings: every one starts, none longer than 'longest sitting' (150m)", (s.DidntStart == 0) && (s.Longest <= 150), $"{s.DidntStart} didn't start, longest {s.Longest}m");
			Check("simulated sittings: a day never runs more than a few minutes past its hours", s.OverDay == 0, $"{s.OverDay} days over");
			Check("simulated sittings: about 40% of the sittings farm cards on a card day (CardSittingsPct)", Math.Abs(s.Farm - 0.40) < 0.03, $"{s.Farm:P1} of {s.CardSittings}");
		}

		Check($"simulated sittings, {mixName}: the main game's share is within half a point of its {centre}%", Math.Abs(s.Plain - want) < 0.005,
			$"{s.Plain:P2}, {s.NoSideDays:P1} of the days with no side game, main-game sittings {s.MainRun:0.00} in a row");
		Check($"simulated sittings, {mixName}: ...on days cards farm in some sittings too", Math.Abs(s.Card - want) < 0.005, $"{s.Card:P2}");
	}

	// What the pick's sums are built on: the average sitting worked out from the ranges is what the real roll gives - the
	// day's short first sitting, the last one cut to what's left of the day, and a side game near the end of its allowance.
	string lenName = "harness-len-" + Guid.NewGuid().ToString("N")[..6];
	var lenBot = new NocatFarm.Core.Bot(lenName, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30" });
	var lenHuman = new NocatFarm.Modules.HumanMode(lenBot);
	lenBot.AddModule(lenHuman);
	HF("_rng").SetValue(lenHuman, new Random(3));
	Set(lenHuman, "_dayStamp", -1);
	Set(lenHuman, "_stayUpUntil", DateTime.MinValue);
	int lenOff = 0;
	string lenWorst = "";

	foreach ((bool first, int remaining, int sideLeft) in new[] { (false, 10_000, 0), (true, 10_000, 0), (false, 70, 0), (false, 20, 0), (false, 10_000, 40), (true, 55, 25) }) {
		Set(lenHuman, "_firstSessionOfDay", first);
		Set(lenHuman, "_targetMinutes", 20_000);
		Set(lenHuman, "_playedMinutesToday", 20_000 - remaining);
		Set(lenHuman, "_otherBudget", sideLeft > 0 ? sideLeft : 1_000_000);
		Set(lenHuman, "_farmPlayed", 0);
		Set(lenHuman, "_otherPlayed", 0);

		foreach (bool main in new[] { true, false }) {
			double rolled = 0;

			for (int i = 0; i < 40_000; i++) {
				rolled += (int) HCall(lenHuman, "SessionLength", main ? 730u : 440u, 730u)!;
			}

			rolled /= 40_000;
			double worked = (double) ht.GetMethod("MeanLength", Stat)!.Invoke(null, [main, 30, 150, sideLeft > 0 ? sideLeft : 0, -1,
				first ? (int) ht.GetMethod("FirstSittingCap", Stat)!.Invoke(null, [30, 150])! : 0, remaining, 0])!;

			if (Math.Abs(rolled - worked) > 0.5) {
				lenOff++;
				lenWorst = $"{(main ? "main" : "side")} first={first} left={remaining} side left={sideLeft}: rolled {rolled:0.0}m, worked out {worked:0.0}m";
			}
		}
	}

	Check("the pick's sums: the average sitting worked out from the ranges is what the roll really gives, every limit included", lenOff == 0, lenWorst);

	// Carrying on never keeps it on the main game: after a main-game sitting the next is the main game exactly as often as
	// after a side game, or with nothing before it - it's which side game that carries on.
	Set(lenHuman, "_firstSessionOfDay", false);
	Set(lenHuman, "_targetMinutes", 1_000_000);
	Set(lenHuman, "_playedMinutesToday", 0);
	Set(lenHuman, "_otherBudget", 1_000_000);
	Set(lenHuman, "_mainSharePct", 70);
	lenBot.Cfg.GameWeights = "730:70, 440:20, 550:10";
	const int CarryPicks = 40_000;
	int[] mainAfter = new int[3];
	int again440 = 0, sides440 = 0, again550 = 0, sides550 = 0;
	uint[] lastOnes = [0, 730, 440];

	for (int which = 0; which < 3; which++) {
		for (int i = 0; i < CarryPicks; i++) {
			Set(lenHuman, "_lastGame", lastOnes[which]);
			uint picked = (uint) HCall(lenHuman, "PickGame")!;
			mainAfter[which] += picked == 730 ? 1 : 0;

			if ((which == 2) && (picked != 730)) {
				sides440++;
				again440 += picked == 440 ? 1 : 0;
			}
		}
	}

	for (int i = 0; i < CarryPicks; i++) {
		Set(lenHuman, "_lastGame", 550u);
		uint picked = (uint) HCall(lenHuman, "PickGame")!;

		if (picked != 730) {
			sides550++;
			again550 += picked == 550 ? 1 : 0;
		}
	}

	double[] mainOdds = [.. mainAfter.Select(static n => (double) n / CarryPicks)];
	Check("carry on: after a main-game sitting the main game comes round no more often than after a side game, or a fresh start",
		(Math.Abs(mainOdds[1] - mainOdds[0]) < 0.012) && (Math.Abs(mainOdds[1] - mainOdds[2]) < 0.012), $"{mainOdds[0]:P1} fresh, {mainOdds[1]:P1} after main, {mainOdds[2]:P1} after a side game");

	// 440 is two thirds of the side games by weight and 550 a third: 40% carry on, the rest by weight.
	double keep440 = (double) again440 / sides440, keep550 = (double) again550 / sides550;
	Check("carry on: a side-game sitting straight after one is the same side game again about 40% more often than a fresh pick",
		(Math.Abs(keep440 - (0.4 + (0.6 * 2 / 3))) < 0.015) && (Math.Abs(keep550 - (0.4 + (0.6 / 3))) < 0.015), $"440 again {keep440:P1}, 550 again {keep550:P1}");
	NocatFarm.Modules.HumanDay.Forget(lenName);
	await lenBot.DisposeAsync();
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

// ── help: a setting that's both whole-app and per-account explains both ─────────────────────────────────────
{
	string Help(params string[] args) => (string) typeof(NocatFarm.Commands).GetMethod("Help", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [args])!;
	string both = Help("StatusEveryMinutes");
	Check("help: StatusEveryMinutes explains the whole-app one and the per-account one", both.Contains("For the whole app") && both.Contains("Per account"), both.Length > 120 ? both[..120] : both);
	string one = Help("LegitMode");
	Check("help: a per-account-only setting is explained once, as before", !one.Contains("For the whole app") && one.StartsWith("LegitMode", StringComparison.Ordinal), one.Length > 80 ? one[..80] : one);
}
// ── lifecycle edges: an old run's disconnect, a start that breaks, a failed account that will retry ──────────
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	Type bt = typeof(NocatFarm.Core.Bot);

	// Held for the whole block, so nothing here ever connects to Steam: every start waits at it for its login slot.
	var latch = (SemaphoreSlim) typeof(NocatFarm.Core.Limiters).GetField("LoginCooldownLatch", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
	bool tookLatch = latch.Wait(0);
	Check("edges: the login latch is held, so nothing below can sign in for real", latch.CurrentCount == 0);

	var bot = new NocatFarm.Core.Bot("harness-edge-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig());
	FieldInfo session = bt.GetField("_session", Inst)!, opened = bt.GetField("_connectedSession", Inst)!;
	MethodInfo onDisconnected = bt.GetMethod("OnDisconnected", Inst)!;
	var dropped = (SteamKit2.SteamClient.DisconnectedCallback) System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamKit2.SteamClient.DisconnectedCallback));

	// Run 7 had a connection and was stopped; its "disconnected" is still waiting in SteamKit's queue. Run 8 starts and
	// queues for its login slot - and then the new pump reads the old message.
	session.SetValue(bot, 7L);
	opened.SetValue(bot, 7L);
	Task run = bot.StartAsync();
	await Task.Delay(300);
	bool waiting = bot.Running && (bot.State == NocatFarm.Core.BotState.Connecting);
	onDisconnected.Invoke(bot, [dropped]);
	await Task.Delay(300);
	Check("stale disconnect: an earlier run's connection dropping is ignored - no 'reconnecting', no second sign-in queued",
		waiting && (bot.State == NocatFarm.Core.BotState.Connecting) && (bot.StatusText == "waiting for a login slot"), $"{bot.State} / {bot.StatusText}");

	// The same message about this run's own connection is a real drop, and reconnects.
	opened.SetValue(bot, session.GetValue(bot));
	onDisconnected.Invoke(bot, [dropped]);
	await Task.Delay(300);
	Check("stale disconnect: ...while this run's own connection dropping still reconnects", (long) session.GetValue(bot)! == 8L
		&& (bot.State == NocatFarm.Core.BotState.Reconnecting), $"run {session.GetValue(bot)}, {bot.State}");
	await bot.StopAsync();
	await Task.WhenAny(run, Task.Delay(5000));
	Check("stale disconnect: stops cleanly after", run.IsCompleted && !bot.Running && (bot.State == NocatFarm.Core.BotState.Stopped), $"{bot.State}");

	// A start that throws after the account is marked running: here the config is gone, so reading "Start paused" does.
	var broken = new NocatFarm.Core.Bot("harness-edge-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig());
	broken.Reconfigure(null!);
	Task first = broken.StartAsync();
	bool ended = first.Wait(5000);
	Check("broken start: not left marked running, and says it failed", ended && !broken.Running && (broken.State == NocatFarm.Core.BotState.Failed)
		&& (bt.GetField("_cts", Inst)!.GetValue(broken) == null) && (bt.GetField("_pump", Inst)!.GetValue(broken) == null), $"running {broken.Running}, {broken.State}");
	broken.Reconfigure(new NocatFarm.Config.BotConfig());
	Task second = broken.StartAsync();
	await Task.Delay(300);
	Check("broken start: ...so the next 'start' starts instead of doing nothing", broken.Running && (broken.State == NocatFarm.Core.BotState.Connecting), $"{broken.State}");
	await broken.StopAsync();
	await Task.WhenAny(second, Task.Delay(5000));

	// "Close when everything's done": a failed account only counts once it has given up.
	MethodInfo finished = typeof(NocatFarm.Core.BotManager).GetMethod("IsFinished", BindingFlags.NonPublic | BindingFlags.Static)!;
	var acct = new NocatFarm.Core.Bot("harness-edge-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig());
	bool Finished() => (bool) finished.Invoke(null, [acct])!;
	FieldInfo running = bt.GetField("_running", Inst)!;
	bt.GetProperty("State")!.SetValue(acct, NocatFarm.Core.BotState.Failed);
	running.SetValue(acct, true);
	bool retrying = Finished() || acct.GaveUp;
	running.SetValue(acct, false);
	bool gaveUp = Finished();
	Check("close when done: a failed sign-in that's about to retry isn't finished", !retrying);
	Check("close when done: ...one that gave up for good is", gaveUp && acct.GaveUp);
	bt.GetProperty("State")!.SetValue(acct, NocatFarm.Core.BotState.Stopped);
	bool stopped = Finished();
	bt.GetProperty("State")!.SetValue(acct, NocatFarm.Core.BotState.Online);
	running.SetValue(acct, true);
	bool online = Finished();
	acct.Cfg.Enabled = false;
	Check("close when done: stopped or switched off is finished, online isn't", stopped && !online && Finished());

	if (tookLatch) {
		latch.Release();
	}
}

// ── the installer's --setup: a save that fails says so, with an exit code and a reason ─────────────────────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-setup-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	string cfgFile = NocatFarm.Config.ConfigStore.GlobalPath;
	string LastLogLine() => File.Exists(NocatFarm.Config.SetupChoices.LogPath) ? File.ReadAllLines(NocatFarm.Config.SetupChoices.LogPath).LastOrDefault() ?? "" : "";

	try {
		// A config that doesn't load isn't saved over - and that used to come back as 0, "saved".
		File.WriteAllText(cfgFile, "{ not json");
		int broken = NocatFarm.Config.SetupChoices.Run(["WebPort=7301"]);
		Check("setup: a config it can't save over is exit 4, not 'saved'", broken == NocatFarm.Config.SetupChoices.SaveFailed, $"exit {broken}");
		Check("setup: ...and the reason is in logs\\setup.log for the installer", LastLogLine().Contains("--setup exit 4: ") && LastLogLine().Contains("didn't load"), LastLogLine());
		Check("setup: ...and nothing was written over the file", File.ReadAllText(cfgFile) == "{ not json");

		// A file it can't write. Read-only is enough on Windows; on Linux and Mac the save writes a new file and swaps it
		// in, which a read-only file doesn't stop - there, the folder itself is made read-only.
		File.WriteAllText(cfgFile, "{}");
		string cfgDir = Path.GetDirectoryName(cfgFile)!;
		File.SetAttributes(cfgFile, FileAttributes.ReadOnly);
		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(cfgDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		}
		int locked = NocatFarm.Config.SetupChoices.Run(["WebPort=7301"]);
		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(cfgDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		File.SetAttributes(cfgFile, FileAttributes.Normal);
		Check("setup: a config file it can't write is exit 4 with why", (locked == NocatFarm.Config.SetupChoices.SaveFailed) && LastLogLine().Contains("couldn't write"), $"exit {locked}: {LastLogLine()}");

		// The refusal keeps its own code, and a refused value never lands in the file - it could be a password.
		int refused = NocatFarm.Config.SetupChoices.Run(["WebPassword=hunter2-secret"]);
		Check("setup: a setting it doesn't take is still exit 2", refused == 2, $"exit {refused}");
		Check("setup: ...named in the log, its value left out", LastLogLine().Contains("--setup exit 2: 'WebPassword'") && !File.ReadAllText(NocatFarm.Config.SetupChoices.LogPath).Contains("hunter2"), LastLogLine());
	} finally {
		// Loaded once more from a good file, so the "didn't load - don't save" flag is off for everything after.
		try {
			File.SetAttributes(cfgFile, FileAttributes.Normal);
			File.WriteAllText(cfgFile, "{}");
			NocatFarm.Config.ConfigStore.LoadGlobal();
		} catch {
			// best effort
		}

		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch {
			// the temp folder empties itself eventually
		}
	}
}

// ── farming and trading sweep: held swaps, market pauses, playtime, badges, boosters, listings, keys ────────────
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	NocatFarm.Log.Suppressed = true;

	try {
		// A fair swap that would sit in a trade hold is left for you - and stays left, instead of being queued, waited
		// out and turned down again at every look.
		var tbot = new NocatFarm.Core.Bot("harness-heldswap", new NocatFarm.Config.BotConfig { AcceptFairCardSwaps = true });
		var trades = new NocatFarm.Modules.Trading(tbot);
		Type tt = typeof(NocatFarm.Modules.Trading);
		var verdicts = (IDictionary) tt.GetField("_fair", Inst)!.GetValue(trades)!;
		MethodInfo handle = tt.GetMethod("HandleAsync", Inst)!;
		NocatFarm.Core.TradeOffers.Item SCard(string name) => new(753, "6", 1, 1, 0, 1, "Trading Card", name, 440, true);
		NocatFarm.Core.TradeOffers.Offer Swap(ulong id) => new(id, 76561198000000123UL, NocatFarm.Core.TradeOffers.Active, false, null, [SCard("A")], [SCard("B")]);
		async Task<(bool, bool)> Handle(ulong id) => await (Task<(bool, bool)>) handle.Invoke(trades,
			[Swap(id), new Dictionary<ulong, NocatFarm.Modules.Trading.Way>(), new HashSet<ulong>(), false, CancellationToken.None])!;

		verdicts[2UL] = (true, DateTime.UtcNow);
		(bool queuedActed, _) = await Handle(2);
		Check("held swap: an ordinary fair swap is queued to be answered", queuedActed && (trades.WaitingCount == 1), $"{trades.WaitingCount} waiting");
		tt.GetMethod("LeaveHeldSwap", Inst)!.Invoke(trades, [1UL]);
		verdicts[1UL] = (true, DateTime.UtcNow);   // what the next look's judging finds: the cards still fit
		(bool heldActed, bool heldSwapped) = await Handle(1);
		Check("held swap: one that would be held isn't queued again at the next look", !heldActed && !heldSwapped && (trades.WaitingCount == 1), $"{trades.WaitingCount} waiting");
		await tbot.DisposeAsync();

		// The market said 429: the lookups already queued behind that one wait it out too, instead of each asking anyway.
		Type pb = typeof(NocatFarm.PriceBook);
		FieldInfo PF(string n) => pb.GetField(n, Stat)!;
		var priceGate = (SemaphoreSlim) PF("Gate").GetValue(null)!;
		PF("_coolLoaded").SetValue(null, true);
		PF("_coolUntil").SetValue(null, DateTime.MinValue);
		PF("_lastCall").SetValue(null, DateTime.MinValue);
		await priceGate.WaitAsync();
		Task<decimal?> queuedLookup = NocatFarm.PriceBook.FetchAsync(753, "harness-no-such-item", CancellationToken.None);
		await Task.Delay(200);
		bool waitedItsTurn = !queuedLookup.IsCompleted;
		PF("_coolUntil").SetValue(null, DateTime.UtcNow.AddMinutes(15));   // the one ahead of it was just refused
		priceGate.Release();
		bool answered = await Task.WhenAny(queuedLookup, Task.Delay(5000)) == queuedLookup;
		Check("prices: a lookup queued behind a refused one doesn't ask the market", waitedItsTurn && answered && (queuedLookup.Result == null)
			&& ((DateTime) PF("_lastCall").GetValue(null)! == DateTime.MinValue));
		PF("_coolUntil").SetValue(null, DateTime.MinValue);

		// Building playtime: the hours go onto the queue's own copies, so the next look (reusing the queue) farms the game.
		var fbot = new NocatFarm.Core.Bot("harness-bump", new NocatFarm.Config.BotConfig());
		var bumper = new NocatFarm.Modules.CardFarmer(fbot);
		var bumpQueue = (List<NocatFarm.Modules.FarmTarget>) typeof(NocatFarm.Modules.CardFarmer).GetField("_queue", Inst)!.GetValue(bumper)!;
		bumpQueue.Add(new NocatFarm.Modules.FarmTarget { AppId = 400, HoursPlayed = 2.9f, CardsRemaining = 3 });
		bumpQueue.Add(new NocatFarm.Modules.FarmTarget { AppId = 620, HoursPlayed = 1.0f, CardsRemaining = 4 });
		typeof(NocatFarm.Modules.CardFarmer).GetMethod("NoteBumped", Stat)!.Invoke(null, [bumper.Queue, TimeSpan.FromMinutes(7)]);
		Check("bump: 7 minutes on a game at 2.9h puts it over a 3h line in the queue itself", bumper.Queue[0].HoursPlayed >= 3.0f, $"{bumper.Queue[0].HoursPlayed:0.00}h");
		Check("bump: every game in the batch got the time", Math.Abs(bumper.Queue[1].HoursPlayed - 1.1167f) < 0.01f, $"{bumper.Queue[1].HoursPlayed:0.000}h");
		await fbot.DisposeAsync();

		// Badges: a game's foil set and its plain one are two crafts, not one.
		Type bcT = typeof(NocatFarm.Modules.BadgeCraft);
		Type craftT = bcT.GetNestedType("Craftable", BindingFlags.NonPublic)!;
		var readyList = (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(craftT))!;
		MethodInfo toRead = bcT.GetMethod("CardPagesToRead", Stat)!;
		List<(uint App, bool Foil)> Pages() => (List<(uint App, bool Foil)>) toRead.Invoke(null, [new List<(uint App, bool Foil)> { (730, false), (730, true), (730, false) }, readyList])!;
		List<(uint App, bool Foil)> both = Pages();
		Check("craft: both the plain and the foil card page of one game are read", both.Count == 2 && both.Contains((730u, false)) && both.Contains((730u, true)), string.Join(" ", both));
		readyList.Add(Activator.CreateInstance(craftT, [730u, 1, 0, 1])!);
		List<(uint App, bool Foil)> foilOnly = Pages();
		Check("craft: with the plain set already read, the foil one still is", foilOnly.Count == 1 && foilOnly[0] == (730u, true), string.Join(" ", foilOnly));
		var craftButtons = (IList) bcT.GetMethod("Parse", Stat)!.Invoke(null, ["onclick=\"CraftBadge( 730, 1, 0, 1 )\" ... onclick=\"CraftBadge( 730, 1, 1, 1 )\" ... onclick=\"CraftBadge( 730, 1, 0, 1 )\""])!;
		Check("craft: craft buttons for a game's plain and foil sets are both kept", craftButtons.Count == 2, $"{craftButtons.Count}");

		// A sweep that finds nothing to craft doesn't inherit "more to craft" from the one before and come back in hours.
		var cbot = new NocatFarm.Core.Bot("harness-craftmore", new NocatFarm.Config.BotConfig { CraftBadges = true });
		var crafter = new NocatFarm.Modules.BadgeCraft(cbot);
		FieldInfo moreToCraft = bcT.GetField("_moreToCraft", Inst)!;
		moreToCraft.SetValue(crafter, true);
		int made = await crafter.SweepAsync(CancellationToken.None);
		Check("craft: a sweep that read nothing isn't 'more to craft' because the last one was", (made == 0) && !(bool) moreToCraft.GetValue(crafter)!);
		await cbot.DisposeAsync();

		// Booster packs in a waiting trade offer (a timed send of boosters, say) aren't opened out of it.
		var packInv = new NocatFarm.Core.InventoryContents { Complete = true };
		void AddItem(ulong id, string cls, string type, string name, string app) {
			packInv.Assets.Add(System.Text.Json.JsonDocument.Parse($"{{\"assetid\":\"{id}\",\"classid\":\"{cls}\",\"instanceid\":\"0\"}}").RootElement);
			packInv.Descriptions[cls + "_0"] = System.Text.Json.JsonDocument.Parse($"{{\"type\":\"{type}\",\"name\":\"{name}\",\"market_fee_app\":\"{app}\"}}").RootElement;
		}
		AddItem(7, "p", "Booster Pack", "Dota 2 Booster Pack", "570");
		AddItem(8, "p", "Booster Pack", "Dota 2 Booster Pack", "570");
		AddItem(9, "c", "Dota 2 Trading Card", "Axe", "570");
		var packs = (List<(string App, ulong Asset)>) bcT.GetMethod("Packs", Stat)!.Invoke(null, [packInv, new HashSet<ulong> { 8 }])!;
		Check("boosters: the packs are found, and one promised in a waiting trade is left shut", packs.Count == 1 && packs[0] == ("570", 7ul), string.Join(" ", packs));

		// A market listing confirmed through the authenticator is called a market listing, not a trade offer.
		MethodInfo confirmedLine = typeof(NocatFarm.Core.Bot).GetMethod("Confirmed", Stat)!;
		string Line(int type, ulong creator) => confirmedLine.Invoke(null, [new NocatFarm.Core.Confirmations.Item(1, 2, creator, type, "", "", [], "", 0)])!.ToString()!;
		Check("confirm: a market listing says so", Line(3, 5123) == "confirmed market listing 5123 with its authenticator", Line(3, 5123));
		Check("confirm: a trade offer still says trade offer", Line(2, 77) == "confirmed trade offer 77 with its authenticator", Line(2, 77));

		// A queued key an account can never use (already owned, another region's) isn't tried on it again - and once every
		// account has said so, the key goes.
		Check("keys: owned already is for good, a rate limit isn't",
			new NocatFarm.Core.RedeemResult(SteamKit2.EPurchaseResultDetail.AlreadyPurchased, "", []).NotForThisAccount
			&& !new NocatFarm.Core.RedeemResult(SteamKit2.EPurchaseResultDetail.RateLimited, "", []).NotForThisAccount
			&& !new NocatFarm.Core.RedeemResult(SteamKit2.EPurchaseResultDetail.BadActivationCode, "", []).NotForThisAccount);
		string realRoot = NocatFarm.Config.ConfigStore.Root;
		string keyRoot = Path.Combine(Path.GetTempPath(), "nf-keys-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(keyRoot, "config"));
		NocatFarm.Config.ConfigStore.UseRoot(keyRoot);

		try {
			Type kq = typeof(NocatFarm.Core.KeyQueue);
			NocatFarm.Core.KeyQueue.Clear();
			const string Key = "HARNS-KEYQU-EUE01";
			NocatFarm.Core.KeyQueue.Add([Key]);
			bool allAfterA = NocatFarm.Core.KeyQueue.RefusedOn(Key, "a", ["a", "b"]);
			var forA = NocatFarm.Core.KeyQueue.Next((account, refused) => (account == null) && !refused.Contains("a", StringComparer.OrdinalIgnoreCase));
			var forB = NocatFarm.Core.KeyQueue.Next((account, refused) => (account == null) && !refused.Contains("b", StringComparer.OrdinalIgnoreCase));
			Check("keys: an account that refused a key for good isn't handed it again; the others still are", !allAfterA && (forA == null) && (forB?.Key == Key));

			// Kept across a restart: read back from the file.
			kq.GetField("_loaded", Stat)!.SetValue(null, false);
			((IList) kq.GetField("Pending", Stat)!.GetValue(null)!).Clear();
			var readBack = NocatFarm.Core.KeyQueue.Next(static (_, _) => true);
			Check("keys: who refused it survives a restart", readBack is { } r && r.RefusedBy.SequenceEqual(new[] { "a" }), string.Join(",", readBack?.RefusedBy ?? Array.Empty<string>()));
			Check("keys: once every account has refused it, nobody is left to try", NocatFarm.Core.KeyQueue.RefusedOn(Key, "B", ["a", "b"]));
			NocatFarm.Core.KeyQueue.Clear();
		} finally {
			NocatFarm.Config.ConfigStore.UseRoot(realRoot);

			try {
				Directory.Delete(keyRoot, true);
			} catch (IOException) {
				// a temp folder - the OS clears it
			}
		}
	} finally {
		NocatFarm.Log.Suppressed = false;
	}
}

// ── only what Steam confirms counts: listings, crafts, packs, declines, comments, busy sends, guest passes ─────────
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	NocatFarm.Log.Suppressed = true;

	try {
		// Listings to confirm: by asset id, or - re-numbered by Steam - the same card at the same price, listed this run.
		DateTime runStart = DateTime.UtcNow.AddMinutes(-5);
		List<NocatFarm.Core.Seller.Offer> listed = [new(1, "Game", "A", 6, 5, 3, "440-A"), new(2, "Game", "B", 9, 8, 6, "440-B")];
		List<NocatFarm.Core.Seller.Listing> onMarket = [
			new(10, 1, "440-A", 3, DateTime.UtcNow, true),                      // ours, same asset id
			new(11, 99, "440-B", 6, DateTime.UtcNow, true),                     // ours, given a new asset id
			new(12, 98, "440-B", 6, DateTime.UtcNow.AddDays(-2), true),         // the owner's own, from days ago
			new(13, 97, "440-C", 6, DateTime.UtcNow, true),                     // another card
			new(14, 96, "440-B", 6, DateTime.UtcNow, false)];                   // already live - nothing to confirm
		var pending = (List<ulong>) typeof(NocatFarm.Core.Seller).GetMethod("MatchPending", Stat)!.Invoke(null, [onMarket, listed, runStart])!;
		Check("listings: ours are found by asset id or, re-numbered, by card and price - nothing else", pending.Order().SequenceEqual(new ulong[] { 10, 11 }), string.Join(",", pending));
		List<NocatFarm.Core.Seller.Listing> twoLike = [new(20, 90, "440-B", 6, DateTime.UtcNow, true), new(21, 91, "440-B", 6, DateTime.UtcNow, true)];
		var one = (List<ulong>) typeof(NocatFarm.Core.Seller).GetMethod("MatchPending", Stat)!.Invoke(null, [twoLike, listed, runStart])!;
		Check("listings: one card listed confirms one listing, not every one that looks like it", one.Count == 1, string.Join(",", one));

		// Crafts and packs: only an explicit yes.
		MethodInfo succeeded = typeof(NocatFarm.Modules.BadgeCraft).GetMethod("Succeeded", Stat)!;
		bool Ok(string? body, string field) => (bool) succeeded.Invoke(null, [body, field])!;
		Check("craft/pack: success 1 or true is a yes", Ok("""{"success":1,"rgDroppedItems":[]}""", "rgDroppedItems") && Ok("""{"success":true}""", "rgItems"));
		Check("craft/pack: no success flag but the result that only comes on success is a yes", Ok("""{"rgItems":[{"name":"x"}]}""", "rgItems"));
		Check("craft/pack: a numeric failure code, false, an HTML page or nothing is a no",
			!Ok("""{"success":2}""", "rgItems") && !Ok("""{"success":16,"rgItems":[]}""", "rgItems") && !Ok("""{"success":false}""", "rgItems")
			&& !Ok("<html>too many requests</html>", "rgItems") && !Ok(null, "rgItems") && !Ok("{}", "rgDroppedItems"));

		// Declines and cancels: only when Steam hands the offer back, or says success.
		MethodInfo answered = typeof(NocatFarm.Modules.Trading).GetMethod("OfferAnswered", Stat)!;
		bool Answered(string? body, ulong id) => (bool) answered.Invoke(null, [body, id])!;
		Check("decline/cancel: Steam handing the offer's id back is done", Answered("""{"tradeofferid":"7123"}""", 7123) && Answered("""{"tradeofferid":7123}""", 7123));
		Check("decline/cancel: another id, an error, an HTML page or nothing isn't",
			!Answered("""{"tradeofferid":"999"}""", 7123) && !Answered("""{"strError":"There was an error (28)"}""", 7123) && !Answered("<html>Sign In</html>", 7123) && !Answered(null, 7123));

		// Rep4rep: a comment that never left this PC isn't counted or credited, and the next try backs off.
		var rbot = new NocatFarm.Core.Bot("harness-r4r-notsent-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig());
		typeof(NocatFarm.Core.Bot).GetProperty("State")!.SetValue(rbot, NocatFarm.Core.BotState.Online);   // "online", but with no web session to send with
		using var rapi = new NocatFarm.Rep4Rep.Rep4RepApi();
		var r4r = new NocatFarm.Modules.Rep4RepModule(rbot, rapi);
		Type rt = typeof(NocatFarm.Modules.Rep4RepModule);
		var rstate = new NocatFarm.Rep4Rep.Rep4RepState();
		rt.GetField("_state", Inst)!.SetValue(r4r, rstate);
		rt.GetField("_profileId", Inst)!.SetValue(r4r, "harness-profile");
		r4r.Remember([new NocatFarm.Rep4Rep.Rep4RepTask { TaskId = "task-ns", TargetSteamId = 76561198000000001, TargetName = "someone", CommentText = "+rep" }]);
		string notSent = await r4r.PostNowAsync("task-ns");
		Check("rep4rep: a comment that couldn't be sent isn't counted as posted", notSent.Contains("couldn't reach Steam", StringComparison.Ordinal)
			&& (rstate.PostsInLast24h() == 0) && !rstate.HasPostedTask("task-ns"), notSent);
		MethodInfo notSentWait = rt.GetMethod("NotSentWaitSeconds", Stat)!;
		int W(int run) => (int) notSentWait.Invoke(null, [run])!;
		Check("rep4rep: not sent backs off - 5, 10, 20, 40, then 60 minutes at most", (W(1) / 60 is >= 5 and < 7) && (W(2) / 60 is >= 10 and < 12) && (W(4) / 60 is >= 40 and < 42) && (W(9) / 60 is >= 60 and < 62),
			$"{W(1) / 60} {W(2) / 60} {W(4) / 60} {W(9) / 60}");
		await rbot.DisposeAsync();

		// A timed send that found every item busy says so - and tries again in minutes, not at the next period.
		var sbot = new NocatFarm.Core.Bot("harness-busysend", new NocatFarm.Config.BotConfig());
		MethodInfo sendClaimed = typeof(NocatFarm.Core.Looting).GetMethod("SendClaimedAsync", Stat)!;
		List<NocatFarm.Core.Looting.Item> noItems = [];
		async Task<(string, bool)> SendWith(int busyCount) => await (Task<(string, bool)>) sendClaimed.Invoke(null,
			[sbot, 76561198000000002UL, "", "cards", noItems, noItems, new HashSet<uint>(), 0, noItems, busyCount, CancellationToken.None])!;
		(string busyText, bool isBusy) = await SendWith(3);
		(_, bool notBusy) = await SendWith(0);
		Check("send: everything busy is told apart from nothing to send", isBusy && !notBusy && busyText.Contains("being sent or sold", StringComparison.Ordinal), busyText);
		MethodInfo busyRetry = typeof(NocatFarm.Modules.Sender).GetMethod("BusyRetry", Stat)!;
		TimeSpan? Retry(bool busy, int tries) => (TimeSpan?) busyRetry.Invoke(null, [busy, tries]);
		Check("send: busy tries again in 3-8 minutes, a few times, then waits for its usual time",
			(Retry(true, 1) is { } r1) && (r1 >= TimeSpan.FromMinutes(3)) && (r1 <= TimeSpan.FromMinutes(8)) && (Retry(true, 6) != null) && (Retry(true, 7) == null) && (Retry(false, 0) == null));
		await sbot.DisposeAsync();

		// Guest passes: a no that may pass is tried again later; a lasting one, or the fourth, is given up on.
		MethodInfo passRetry = typeof(NocatFarm.Modules.Gifts).GetMethod("PassRetry", Stat)!;
		TimeSpan? Pass(SteamKit2.EResult result, int tries) => (TimeSpan?) passRetry.Invoke(null, [result, tries]);
		Check("gifts: a busy Steam is asked again in 30m, 2h, 8h", (Pass(SteamKit2.EResult.Busy, 1) == TimeSpan.FromMinutes(30))
			&& (Pass(SteamKit2.EResult.Fail, 2) == TimeSpan.FromHours(2)) && (Pass(SteamKit2.EResult.ServiceUnavailable, 3) == TimeSpan.FromHours(8)));
		Check("gifts: ...then given up; an expired or already-redeemed pass isn't asked again", (Pass(SteamKit2.EResult.Busy, 4) == null)
			&& (Pass(SteamKit2.EResult.Expired, 1) == null) && (Pass(SteamKit2.EResult.AlreadyRedeemed, 1) == null));
	} finally {
		NocatFarm.Log.Suppressed = false;
	}
}

// ── the dashboard's sign-in lockout and the "Public address" link ────────────────────────────────────────────────
{
	const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
	DateTime now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
	int After((int, DateTime)? previous) => (int) typeof(NocatFarm.Web.WebHost).GetMethod("FailuresAfter", Internal)!.Invoke(null, [previous, now])!;
	Check("sign-in: the first wrong guess counts one", After(null) == 1);
	Check("sign-in: a wrong guess inside the window adds to the count", After((3, now.AddMinutes(40))) == 4);
	Check("sign-in: old guesses whose time is up don't count - a typo today after four last week is one, not a lockout", After((4, now.AddDays(-4))) == 1);
	Check("sign-in: one typo after a lockout ran out doesn't start another", After((5, now.AddSeconds(-1))) == 1);

	string? Out(string typed) => (string?) typeof(NocatFarm.Core.DashboardLinks).GetMethod("Outside", Internal)!.Invoke(null, [typed, 7242]);
	Check("public address: a bare name gets the port", Out("myname.duckdns.org") == "http://myname.duckdns.org:7242/", Out("myname.duckdns.org") ?? "null");
	Check("public address: a port typed is kept", Out("http://1.2.3.4:8080") == "http://1.2.3.4:8080/", Out("http://1.2.3.4:8080") ?? "null");
	Check("public address: https (a proxy in front) is left on its own port", Out("https://farm.example.com") == "https://farm.example.com/", Out("https://farm.example.com") ?? "null");
	Check("public address: the port goes after the name, not after a path", Out("example.com/farm") == "http://example.com:7242/farm/", Out("example.com/farm") ?? "null");
	Check("public address: nothing typed is no link", Out("  ") == null);

	// ── settings: a choice by its label, and a number that isn't one ──
	var cfgC = new NocatFarm.Config.BotConfig { OnlineStatus = 1, HoursUntilCardDrops = 3 };
	var persona = NocatFarm.Config.Settings.FindBot("OnlineStatus")!;
	Check("choice: nothing typed isn't the first choice (it set Appear as to offline)", (NocatFarm.Config.Settings.Apply(cfgC, persona, "") != null) && (cfgC.OnlineStatus == 1));
	Check("choice: the start of two labels ('o' - offline, online) is refused, not the first one listed", (NocatFarm.Config.Settings.Apply(cfgC, persona, "o") != null) && (cfgC.OnlineStatus == 1));
	Check("choice: a whole label, and the start of just one, still work",
		(NocatFarm.Config.Settings.Apply(cfgC, persona, "online") == null) && (cfgC.OnlineStatus == 1) && (NocatFarm.Config.Settings.Apply(cfgC, persona, "invis") == null) && (cfgC.OnlineStatus == 7));
	var hoursDef = NocatFarm.Config.Settings.FindBot("HoursUntilCardDrops")!;
	Check("float: 'nan' is refused - it passed the range check and then no save of that account could be written",
		(NocatFarm.Config.Settings.Apply(cfgC, hoursDef, "nan") != null) && (NocatFarm.Config.Settings.Apply(cfgC, hoursDef, "Infinity") != null) && (cfgC.HoursUntilCardDrops == 3f));

	// ── config files: an explicit null is the default, not a crash later ──
	var nulled = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Config.BotConfig>("""{ "IdleGames": null, "GameWeights": null, "SteamLogin": "x" }""")!;
	NocatFarm.Config.ConfigStore.FillNulls(nulled);
	Check("config: a list or text written as null comes back as its default", (nulled.IdleGames != null) && (nulled.GameWeights != null) && (nulled.SteamLogin == "x"));

	// ── names: what a new account may be called ──
	Check("names: 'all' can't be an account - every command reads it as every account", !NocatFarm.Config.ConfigStore.IsPlainBotName("all") && !NocatFarm.Config.ConfigStore.IsPlainBotName("ALL"));
	Check("names: letters, numbers, dashes and underscores - nothing else", NocatFarm.Config.ConfigStore.IsPlainBotName("farm-1_b") && !NocatFarm.Config.ConfigStore.IsPlainBotName("o'brien") && !NocatFarm.Config.ConfigStore.IsPlainBotName("two words"));

	// ── Steam chat: a master of one account commands that account, not the app ──
	bool Refuses(string verb) => (bool) typeof(NocatFarm.Commands).GetMethod("SteamChatRefuses", Internal)!.Invoke(null, [verb])!;
	Check("steam chat: the app's settings, the dashboard, updates and files on this PC are refused", Refuses("set") && Refuses("anywhere") && Refuses("dashboard") && Refuses("update") && Refuses("import") && Refuses("redeem"));
	Check("steam chat: the account's own everyday commands still work", !Refuses("pause") && !Refuses("status") && !Refuses("2fa") && !Refuses("offers"));

	NocatFarm.Config.GlobalConfig liveBefore = NocatFarm.Config.Live.Global;
	var chatMgr = new NocatFarm.Core.BotManager(liveBefore);
	string one = "harness-chat-" + Guid.NewGuid().ToString("N")[..6], other = one + "b";
	await chatMgr.AddAsync(one, new NocatFarm.Config.BotConfig { Enabled = false });
	await chatMgr.AddAsync(other, new NocatFarm.Config.BotConfig { Enabled = false });
	bool Past(string line) => (bool) typeof(NocatFarm.Commands).GetMethod("ReachesPast", Internal)!.Invoke(null, [chatMgr, line, one])!;
	Check("steam chat: another account named, or all of them, is refused", Past($"confirm {other} all") && Past("2fa all") && Past($"trade accept {other} 1") && Past($"send {other} to {one}"));
	Check("steam chat: this account itself, and its items sent to another, are fine",
		!Past($"pause {one}") && !Past($"confirm {one} all") && !Past($"trade accept {one} all") && !Past($"send {one} to {other}") && !Past("status"));
	await chatMgr.RemoveAsync(one);
	await chatMgr.RemoveAsync(other);
	NocatFarm.Config.Live.Global = liveBefore;

	// ── Telegram: a big block is cut a whole line at a time, and stays HTML Telegram can read ──
	Type notifier = typeof(NocatFarm.Core.Notifier);
	Type blockType = notifier.GetNestedType("Block", BindingFlags.NonPublic | BindingFlags.Public)!;
	string Part(NocatFarm.Topic topic, List<string> lines) => (string) notifier.GetMethod("TelegramPart", Internal)!.Invoke(null, [Activator.CreateInstance(blockType, topic, "report", lines)])!;
	string summary = Part(NocatFarm.Topic.Summary, [.. Enumerable.Range(1, 400).Select(static i => $"account{i} & co  banked 3h12m · 4 card(s)")]);
	Check("telegram: a long daily summary stays under the limit and keeps its closing </pre>", (summary.Length <= 3900) && summary.EndsWith("</pre>", StringComparison.Ordinal), $"{summary.Length}");
	Check("telegram: ...cut between lines, never through an &amp;", !summary.Contains("&am<", StringComparison.Ordinal) && summary.Contains("\n…</pre>", StringComparison.Ordinal));
	string lone = Part(NocatFarm.Topic.Cards, [new string('<', 5000)]);
	Check("telegram: one line too long on its own is cut short, still whole entities", (lone.Length <= 3900) && lone.EndsWith("&lt;…", StringComparison.Ordinal), $"{lone.Length}");

	// ── ASF: what a bot file leaves out means what it means to ASF ──
	string asfDir = Path.Combine(Path.GetTempPath(), "nf-asf-" + Guid.NewGuid().ToString("N")[..8]);
	Directory.CreateDirectory(asfDir);
	static string Url64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	string expired = Url64("{\"alg\":\"EdDSA\",\"typ\":\"JWT\"}") + "." + Url64("{\"iss\":\"steam\",\"sub\":\"76561198000000009\",\"exp\":1000}") + ".c2ln";
	File.WriteAllText(Path.Combine(asfDir, "ASF.json"), """{ "SteamOwnerID": 76561198000000042 }""");
	File.WriteAllText(Path.Combine(asfDir, "plain.json"), """{ "SteamPassword": "pw", "SendTradePeriod": 24 }""");
	File.WriteAllText(Path.Combine(asfDir, "plain.db"), $$"""{ "BackingRefreshToken": "{{expired}}" }""");
	File.WriteAllText(Path.Combine(asfDir, "half.json"), """{ "SteamLogin": "halflogin", "Enabled": true, "SteamParentalCode": "0", "SteamUserPermissions": { "76561198000000077": 3.0 }, "FarmingPreferences": 128 }""");
	File.WriteAllText(Path.Combine(asfDir, "half.maFile.PENDING"), """{"shared_secret":"cGVuZGluZy1zaGFyZWQtc2VjcmV0MTI=","account_name":"halflogin"}""");
	var asfRead = NocatFarm.Config.AsfImport.Read(asfDir);
	var plainBot = asfRead.Accounts.First(static a => a.Key == "plain");
	var halfBot = asfRead.Accounts.First(static a => a.Key == "half");
	Check("import asf: a bot whose file leaves Enabled out comes in off, as ASF has it", !plainBot.Config.Enabled && halfBot.Config.Enabled);
	Check("import asf: no SteamLogin is no login - not the bot's own name", plainBot.SteamLogin.Length == 0, plainBot.SteamLogin);
	Check("import asf: a login token that has run out isn't brought (it was tried first, and failed the account)",
		(plainBot.Token == null) && plainBot.Notes.Any(static n => n.ToString().Contains("run out", StringComparison.Ordinal)));
	Check("import asf: an unfinished authenticator (.maFile.PENDING) isn't taken for a real one", halfBot.MaFile == null);
	Check("import asf: no Master on the bot - its items go to ASF's owner, as ASF sends them", plainBot.Config.TradeMasters == "76561198000000042", plainBot.Config.TradeMasters);
	Check("import asf: a permission level written 3.0 still counts, and doesn't stop the read", halfBot.Config.TradeMasters == "76561198000000077", halfBot.Config.TradeMasters);
	Check("import asf: a Family View PIN of 0 isn't a PIN", halfBot.Config.SteamParentalCode.Length == 0);
	Check("import asf: the sale event (AutoSteamSaleEvent) only where ASF had it on, and the badge clearing only if asked",
		halfBot.Config is { ClaimEventItems: true, DiscoveryQueue: 1 } && plainBot.Config is { ClaimEventItems: false, DiscoveryQueue: 0, ClearInventoryNotifications: false });

	try {
		Directory.Delete(asfDir, true);
	} catch (IOException) {
		// a temp folder - the OS clears it
	}

	// ── any import: the same Steam account under another name is that account, not a second copy ──
	string had = "harness-dupe-" + Guid.NewGuid().ToString("N")[..6];
	NocatFarm.Config.ConfigStore.SaveBot(had, new NocatFarm.Config.BotConfig { SteamLogin = "Dupe-Login-X", Enabled = false });
	var dupeScan = new NocatFarm.Config.ImportScan { Tool = "asf", ToolName = "ArchiSteamFarm" };
	dupeScan.Accounts.Add(new NocatFarm.Config.ImportedAccount { Key = "k1", Name = had + "-main", SteamLogin = "dupe-login-x", Config = new NocatFarm.Config.BotConfig { SteamLogin = "dupe-login-x", Enabled = false } });
	dupeScan.Accounts.Add(new NocatFarm.Config.ImportedAccount { Key = "k2", Name = had + "-new", SteamLogin = "dupe-login-y", Config = new NocatFarm.Config.BotConfig { SteamLogin = "dupe-login-y", Enabled = false } });
	dupeScan.Accounts.Add(new NocatFarm.Config.ImportedAccount { Key = "k3", Name = had + "-again", SteamLogin = "DUPE-LOGIN-Y", Config = new NocatFarm.Config.BotConfig { SteamLogin = "DUPE-LOGIN-Y", Enabled = false } });
	var dupeOut = NocatFarm.Config.IdlerImport.Apply(dupeScan, [new("k1"), new("k2"), new("k3")], new NocatFarm.Config.GlobalConfig());
	Check("import: an account already here under another name is left alone, not added again", !File.Exists(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, had + "-main.json"))
		&& dupeOut.Notes.Any(n => n.ToString().Contains($"already here as {had}", StringComparison.Ordinal)));
	Check("import: the same login twice in one import comes in once", (dupeOut.Imported == 1) && File.Exists(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, had + "-new.json"))
		&& !File.Exists(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, had + "-again.json")), $"{dupeOut.Imported} imported");

	foreach (string f in new[] { had, had + "-new" }) {
		File.Delete(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, f + ".json"));
	}

	// ── names Windows keeps for devices ──
	Check("names: con, nul, com1, lpt9 - with or without an extension - are refused",
		new[] { "con", "NUL", "com1", "Lpt9", "aux.txt", "prn.json" }.All(static n => NocatFarm.Config.ConfigStore.IsReservedName(n) && !NocatFarm.Config.ConfigStore.IsValidBotName(n))
		&& NocatFarm.Config.ConfigStore.NameProblem("con") is not null && NocatFarm.Config.ConfigStore.NameProblem("farm1") is null);
	Check("names: ones that only look like them are fine", !NocatFarm.Config.ConfigStore.IsReservedName("console") && !NocatFarm.Config.ConfigStore.IsReservedName("com10") && !NocatFarm.Config.ConfigStore.IsReservedName("nulls"));
	string NameFor(string login) => (string) typeof(NocatFarm.Config.AsfImport).Assembly.GetType("NocatFarm.Config.ImportFiles")!
		.GetMethod("NameFor", Internal)!.Invoke(null, [login, "x", new List<string>()])!;
	Check("names: an import's name from a login like 'con' or 'all' gets a 1 on the end", (NameFor("con") == "con1") && (NameFor("all") == "all1"), $"{NameFor("con")} {NameFor("all")}");

	// ── ASF: the whole FarmingOrders list, in turn ──
	var orderBot = new NocatFarm.Config.BotConfig();
	var orderAcct = new NocatFarm.Config.ImportedAccount { Key = "o", Name = "o", Config = orderBot };
	typeof(NocatFarm.Config.AsfImport).GetMethod("ApplyFarmingOrders", Internal)!.Invoke(null, [new List<int> { 0, 10, 3, 5 }, orderBot, orderAcct]);
	Check("import asf: the first order with a match wins, not just the first one (BadgeLevels first, then CardDrops)", orderBot.FarmingOrder == 2, $"{orderBot.FarmingOrder}");
	Check("import asf: ...and it says which ones it couldn't match, and which came after", orderAcct.Notes.Any(static n => n.ToString().Contains("BadgeLevelsAscending", StringComparison.Ordinal))
		&& orderAcct.Notes.Any(static n => n.ToString().Contains("HoursAscending", StringComparison.Ordinal)), string.Join(" | ", orderAcct.Notes));

	// ── plugins: an async handler that throws after its await is logged, not the end of the app ──
	Type hostType = typeof(NocatFarm.Plugins.IPluginHost).Assembly.GetType("NocatFarm.Plugins.Host")!;
	var plugMgr = new NocatFarm.Core.BotManager(NocatFarm.Config.Live.Global);
	var host = (NocatFarm.Plugins.IPluginHost) Activator.CreateInstance(hostType, [plugMgr, "harness-plugin"])!;
	var plugBot = new NocatFarm.Core.Bot("harness-plug", new NocatFarm.Config.BotConfig());
	bool heard = false;
	host.AccountOnline += static _ => throw new InvalidOperationException("thrown straight away");
	host.AccountOnline += async _ => { await Task.Delay(20); throw new InvalidOperationException("thrown after an await"); };
	host.AccountOnline += _ => heard = true;
	FieldInfo caught = hostType.GetField("Caught", BindingFlags.NonPublic | BindingFlags.Static)!;
	int caughtBefore = (int) caught.GetValue(null)!;
	hostType.GetMethod("RaiseOnline", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, [plugBot]);
	SpinWait.SpinUntil(() => (int) caught.GetValue(null)! >= caughtBefore + 2, 3000);
	Check("plugins: a handler that throws - before or after an await - is caught and logged, and the others still hear it",
		heard && ((int) caught.GetValue(null)! == caughtBefore + 2), $"{(int) caught.GetValue(null)! - caughtBefore} caught");
	await plugBot.DisposeAsync();
}

// ── human mode: nothing it sends reaches Steam while you're on the account, and the day's log line says what to expect ──
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	Type ht = typeof(NocatFarm.Modules.HumanMode);
	Type bt = typeof(NocatFarm.Core.Bot);
	FieldInfo HF(string f) => ht.GetField(f, Inst) ?? throw new MissingFieldException(f);
	void Set(object h, string f, object? v) => HF(f).SetValue(h, v);
	void BotSet(NocatFarm.Core.Bot b, string p, object? v) => bt.GetProperty(p)!.SetValue(b, v);
	object? BotField(NocatFarm.Core.Bot b, string f) => bt.GetField(f, Inst)!.GetValue(b);

	// On a break it had gone Away, and then you sat down at your own PC and started a game on the account.
	string oname = "harness-owner-" + Guid.NewGuid().ToString("N")[..6];
	var obot = new NocatFarm.Core.Bot(oname, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30" });
	var oh = new NocatFarm.Modules.HumanMode(obot);
	obot.AddModule(oh);
	BotSet(obot, "State", NocatFarm.Core.BotState.Online);
	BotSet(obot, "OnlineSince", DateTime.UtcNow.AddHours(-2));
	Set(oh, "_dayStamp", DateTime.Now.DayOfYear);   // up from midnight until a day and more away
	Set(oh, "_wakeMinuteOfDay", 0);
	Set(oh, "_bedHour", 23);
	Set(oh, "_bedMinute", 59);
	Set(oh, "_bedIsTomorrow", true);
	Set(oh, "_targetMinutes", 300);
	Set(oh, "_lastStepAt", DateTime.UtcNow);
	Set(oh, "_phase", NocatFarm.Modules.HumanMode.Phase.ShortBreak);
	Set(oh, "_phaseEnds", DateTime.UtcNow.AddMinutes(10));
	Set(oh, "_breakPersonaSet", true);
	obot.SetPersonaOverride(3);   // Away, sent while the account was its own
	int sentBefore = (int) BotField(obot, "_lastLoggedPersona")!;
	BotSet(obot, "PlayingBlocked", true);
	NocatFarm.Log.Suppressed = true;

	for (int i = 0; i < 3; i++) {
		oh.GetType().GetMethod("StepAsync", Inst)!.Invoke(oh, null);
		Set(oh, "_lastStepAt", DateTime.UtcNow);
	}

	oh.WakeNow();   // and 'wake' from the console while you're on it
	oh.GetType().GetMethod("StepAsync", Inst)!.Invoke(oh, null);
	Set(oh, "_lastStepAt", DateTime.UtcNow);
	NocatFarm.Log.Suppressed = false;
	bool stoodDown = oh.Current == NocatFarm.Modules.HumanMode.Phase.StoodDown;
	bool untouched = (BotField(obot, "_personaOverride") is int held) && (held == 3) && ((int) BotField(obot, "_lastLoggedPersona")! == sentBefore);
	Check("you on the account: standing down sends no status change - the break's Away is left as it was, 'wake' included", stoodDown && untouched,
		$"{oh.Current}, override {BotField(obot, "_personaOverride") ?? "none"}");

	// You stop, and the wait after it runs out: the look it should have now goes on - online, in the day.
	BotSet(obot, "PlayingBlocked", false);
	bt.GetField("_resumeAt", Inst)!.SetValue(obot, DateTime.UtcNow.AddSeconds(-1));
	NocatFarm.Log.Suppressed = true;
	oh.GetType().GetMethod("StepAsync", Inst)!.Invoke(oh, null);
	NocatFarm.Log.Suppressed = false;
	Check("you off the account again: the held status goes on then - back online in the day, nothing left held",
		(BotField(obot, "_personaOverride") == null) && ((int) BotField(obot, "_lastLoggedPersona")! == obot.Cfg.OnlineStatus) && !(bool) HF("_personaHeld").GetValue(oh)!,
		$"override {BotField(obot, "_personaOverride") ?? "none"}");
	BotSet(obot, "State", NocatFarm.Core.BotState.Stopped);
	NocatFarm.Modules.HumanDay.Forget(oname);
	await obot.DisposeAsync();

	// The day's mix in the log: what the side games can expect, the same as 'human week' shows - not the allowance.
	Check("side games' expected time: their share of the day, the same sum 'human week' uses", (int) ht.GetMethod("SideExpected", Stat)!.Invoke(null, [300, 70])! == 90);
	string mixRoot = Path.Combine(Path.GetTempPath(), "nf-mixlog-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(mixRoot);
	string mname = "harness-mix-" + Guid.NewGuid().ToString("N")[..6];
	var mbot = new NocatFarm.Core.Bot(mname, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30", PureMainDayChancePct = 0, DayOffChancePct = 0 });
	var mh = new NocatFarm.Modules.HumanMode(mbot);
	mbot.AddModule(mh);
	NocatFarm.Log.Suppressed = true;
	NocatFarm.Log.Configure(fileLogging: true, debug: true, mixRoot, retentionDays: 14);
	mh.RerollToday();
	NocatFarm.Log.Configure(fileLogging: false, debug: false, mixRoot);
	NocatFarm.Log.Suppressed = false;
	string mixFile = Path.Combine(mixRoot, "logs", $"nocatFarm-{DateTime.Now:yyyy-MM-dd}.log");
	string mixLine = File.Exists(mixFile) ? File.ReadAllLines(mixFile).LastOrDefault(l => l.Contains("today's mix")) ?? "" : "";
	int target = (int) HF("_targetMinutes").GetValue(mh)!, mixShare = (int) HF("_mainSharePct").GetValue(mh)!, allowance = (int) HF("_otherBudget").GetValue(mh)!;
	string expected = NocatFarm.Fmt.Hm((int) ht.GetMethod("SideExpected", Stat)!.Invoke(null, [target, mixShare])!);
	Check("the day's log line: 'around X on the others' is what they can expect, not the allowance",
		mixLine.EndsWith($"around {expected} on the others", StringComparison.Ordinal) && !mixLine.Contains("up to", StringComparison.Ordinal) && (allowance > target * (100 - mixShare) / 100), mixLine);
	NocatFarm.Modules.HumanDay.Forget(mname);
	await mbot.DisposeAsync();

	try {
		Directory.Delete(mixRoot, true);
	} catch (IOException) {
		// a temp folder - the OS clears it
	}
}

// ── chat: Steam's own messages (a trade offer, a game invite) aren't somebody talking ─────────────────────────────
{
	var offer = NocatFarm.Modules.Social.SteamEmbed("[tradeoffer sender=39734274 id=9396234175][/tradeoffer]");
	Check("chat: a trade offer's message is Steam's own, not chat", offer?.Kind == "tradeoffer", $"{offer}");
	var lobby = NocatFarm.Modules.Social.SteamEmbed("[lobbyinvite appid=\"730\" lobbyid=\"109775243346442957\"][/lobbyinvite]");
	Check("chat: a lobby invite is Steam's own, with its game", (lobby?.Kind == "lobbyinvite") && (lobby.AppId == "730"), $"{lobby}");
	Check("chat: a person's message isn't, even with a tag in it", (NocatFarm.Modules.Social.SteamEmbed("hey check this [tradeoffer] lol") == null)
		&& (NocatFarm.Modules.Social.SteamEmbed("gg") == null) && (NocatFarm.Modules.Social.SteamEmbed("/status") == null));
}

// ── from the real logs: the auto-reply, the keepalive, the log file, names ─────────────────────────────────────────
{
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	const ulong owner = 76561198000000003, kylro = 76561198000000001, stranger = 76561198000000004;
	HashSet<ulong> masters = [owner];
	Check("chat: the auto-reply never goes to you (on the command list) or to one of your own accounts",
		!NocatFarm.Modules.Social.AutoReplyGoesTo(owner, false, masters) && !NocatFarm.Modules.Social.AutoReplyGoesTo(kylro, true, masters)
		&& NocatFarm.Modules.Social.AutoReplyGoesTo(stranger, false, masters) && !NocatFarm.Modules.Social.AutoReplyGoesTo(0, false, masters));

	MethodInfo quiet = typeof(NocatFarm.Core.Bot).GetMethod("WentQuiet", Stat)!;
	DateTime qnow = DateTime.UtcNow;
	bool Quiet(double inboundSecs, double assertSecs, bool aside) => (bool) quiet.Invoke(null, [qnow, qnow.AddSeconds(-inboundSecs), qnow.AddSeconds(-assertSecs), aside])!;
	Check("keepalive: a session you're playing on isn't taken for dead and signed back in mid-game",
		!Quiet(600, 3 * 3600, true) && Quiet(600, 3 * 3600, false) && !Quiet(5, 3 * 3600, false) && !Quiet(600, 30, false));

	string oneRoot = Path.Combine(Path.GetTempPath(), "nf-oneline-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(oneRoot);
	NocatFarm.Log.Suppressed = true;
	NocatFarm.Log.Configure(fileLogging: true, debug: true, oneRoot, retentionDays: 14);
	NocatFarm.Log.Debug("GET /inventory/ -> 502  <!DOCTYPE html>\n<html lang=\"en\">\r\n<head>oneline-marker", "harness");
	NocatFarm.Log.Configure(fileLogging: false, debug: false, oneRoot);
	NocatFarm.Log.Suppressed = false;
	string oneFile = Path.Combine(oneRoot, "logs", $"nocatFarm-{DateTime.Now:yyyy-MM-dd}.log");
	string[] oneLines = File.Exists(oneFile) ? File.ReadAllLines(oneFile) : [];
	Check("log file: one event is one line, even with an HTML page in it",
		oneLines.Count(l => l.Contains("|harness|", StringComparison.Ordinal)) == 1 && oneLines.Any(l => l.Contains("|harness|", StringComparison.Ordinal) && l.EndsWith("oneline-marker", StringComparison.Ordinal))
		&& !oneLines.Any(l => l.StartsWith("<", StringComparison.Ordinal)), string.Join(" / ", oneLines));

	try {
		Directory.Delete(oneRoot, true);
	} catch (IOException) {
		// a temp folder - the OS clears it
	}

	MethodInfo failure = typeof(NocatFarm.Core.WebSession).GetMethod("FailureText", Stat)!;
	string errPage = (string) failure.Invoke(null, ["<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <title id=\"title\">Error</title>\n<style>* { padding: 0; }</style>"])!;
	string reason = (string) failure.Invoke(null, ["{\"strError\":\"There was an error sending your trade offer.\"}"])!;
	Check("web: a failed request's error page is named by its title, Steam's own reason kept as it is",
		(errPage == "(an HTML page: \"Error\")") && (reason == "{\"strError\":\"There was an error sending your trade offer.\"}"), $"{errPage} | {reason}");

	MethodInfo unprefixed = typeof(NocatFarm.Core.Looting).GetMethod("Unprefixed", Stat)!;
	Check("sending items: the log line filed under old doesn't say 'old: ' again",
		((string) unprefixed.Invoke(null, ["old", "old: sent 26 item(s) to new"])! == "sent 26 item(s) to new")
		&& ((string) unprefixed.Invoke(null, ["old", "kylro: sent 1 item(s) to new"])! == "kylro: sent 1 item(s) to new"));

	SteamKit2.KeyValue pass = new("0");
	pass.Children.Add(new SteamKit2.KeyValue("gid", "222521133245705365"));
	pass.Children.Add(new SteamKit2.KeyValue("packageid", "821602"));
	pass.Children.Add(new SteamKit2.KeyValue("AccessToken", "6842360938661594872"));
	string passLine = (string) typeof(NocatFarm.Core.Bot).GetMethod("PassForLog", Stat)!.Invoke(null, [pass])!;
	Check("gifts: a waiting pass goes in the log without its access token",
		passLine.Contains("packageid=821602", StringComparison.Ordinal) && passLine.Contains("AccessToken=…", StringComparison.Ordinal) && !passLine.Contains("6842360938661594872", StringComparison.Ordinal), passLine);

	MethodInfo storeName = typeof(NocatFarm.Modules.FreeGames).GetMethod("StoreName", Stat)!;
	Check("free games: the store's name comes without the space it sometimes ends in",
		((string) storeName.Invoke(null, ["{\"name\":\"Train Sim World® 7 \"}", "sub 1768268"])! == "Train Sim World® 7")
		&& ((string) storeName.Invoke(null, ["{\"name\":\"  \"}", "sub 1768268"])! == "sub 1768268"));
}

// ── you on the account: what shows on it waits, what nobody sees carries on ─────────────────────────────────────
{
	var cfg = new NocatFarm.Config.BotConfig { LegitMode = true, QuietDelayMinMinutes = 0, QuietDelayMaxMinutes = 0 };
	var bot = new NocatFarm.Core.Bot("harness-youonit", cfg);
	void SetProp(string name, object? value) => typeof(NocatFarm.Core.Bot).GetProperty(name)!.SetValue(bot, value);
	SetProp("State", NocatFarm.Core.BotState.Online);
	SetProp("OnlineSince", DateTime.UtcNow.AddHours(-2));
	SetProp("PlayingBlocked", true);
	var quiet = NocatFarm.Modules.HumanGate.Quiet(bot);
	_ = quiet.Open;
	Check("you on the account: a send to your own account still goes (nobody sees it)", quiet.Open);
	var shows = new NocatFarm.Modules.HumanGate(bot);
	Check("you on the account: a comment or a group join waits", !shows.Open);
	await bot.DisposeAsync();
}

// ── updating by itself: never in a reconnect gap, never just after you played, and it says whyNow it was clear ──────
{
	var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig());
	var bots = (System.Collections.Concurrent.ConcurrentDictionary<string, NocatFarm.Core.Bot>?) typeof(NocatFarm.Core.BotManager).GetField("_bots", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mgr);
	MethodInfo quiet = typeof(NocatFarm.Core.UpdateCheck).GetMethod("Quiet", BindingFlags.NonPublic | BindingFlags.Static)!;
	(bool, string) Quiet() { var r = quiet.Invoke(null, [mgr, null])!; return ((bool) r.GetType().GetField("Item1")!.GetValue(r)!, (string) r.GetType().GetField("Item2")!.GetValue(r)!); }
	var robot = new NocatFarm.Core.Bot("harness-quiet-robot", new NocatFarm.Config.BotConfig());
	void Set(NocatFarm.Core.Bot b, string prop, object? v) => typeof(NocatFarm.Core.Bot).GetProperty(prop)!.SetValue(b, v);
	typeof(NocatFarm.Core.Bot).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(robot, true);
	Set(robot, "State", NocatFarm.Core.BotState.Online);
	bots?.TryAdd(robot.Name, robot);
	if (bots != null) {
		(bool clearNow, string whyNow) = Quiet();
		Check("update quiet: a robot idling is clear, and it says so", clearNow && whyNow.Contains("harness-quiet-robot idling") && whyNow.Contains("nobody playing"), whyNow);
		Set(robot, "State", NocatFarm.Core.BotState.Reconnecting);
		(clearNow, whyNow) = Quiet();
		Check("update quiet: an account signing back in holds it (it went in during a 20s reconnect)", !clearNow && whyNow.Contains("signing in"), whyNow);
		Set(robot, "State", NocatFarm.Core.BotState.Online);
		Set(robot, "YouPlayedAt", DateTime.UtcNow.AddMinutes(-5));
		(clearNow, whyNow) = Quiet();
		Check("update quiet: an account you were on five minutes ago holds it", !clearNow && whyNow.Contains("a few minutes ago"), whyNow);
		Set(robot, "YouPlayedAt", DateTime.UtcNow.AddMinutes(-30));
		Set(robot, "State", NocatFarm.Core.BotState.Failed);
		(clearNow, whyNow) = Quiet();
		Check("update quiet: an account that gave up signing in doesn't hold it forever", clearNow, whyNow);
		bots.TryRemove(robot.Name, out _);
	} else {
		Check("update quiet: could reach the account list", false, "no _bots field");
	}
	await robot.DisposeAsync();
}

// ── idle rotation: a list longer than Steam plays at once, a batch at a time ───────────────────────────────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig realGlobal = NocatFarm.Config.Live.Global;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-rotation-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		Check("rotation: 32 at once, 31 beside a custom name", (NocatFarm.Modules.IdleRotation.Slots(false) == 32) && (NocatFarm.Modules.IdleRotation.Slots(true) == 31));

		// Order: an hour target still to reach, then the listed games as listed, then least played, appID breaking ties.
		Dictionary<uint, int> mins = new() { [10] = 500, [20] = 0, [30] = 90, [40] = 90, [50] = 9000, [60] = 5 };
		List<uint> order = NocatFarm.Modules.IdleRotation.Order([10, 20, 30, 40, 50, 60, 60, 0], [50, 10], a => mins.GetValueOrDefault(a), a => a == 60);
		Check("rotation order: target first, then the listed games as listed, then least played (ties by appID)",
			order.SequenceEqual<uint>([60, 50, 10, 20, 30, 40]), string.Join(",", order));

		// 100 games, 31 at a time, a batch every 24h.
		List<uint> hundred = [.. Enumerable.Range(1, 100).Select(static i => (uint) i)];
		TimeSpan day = TimeSpan.FromHours(24);
		DateTime r0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
		string path = NocatFarm.Modules.IdleRotation.PathFor("rot-a");
		var rot = new NocatFarm.Modules.IdleRotation(path, "rot-a");
		List<uint> b1 = rot.Current(hundred, 31, day, r0);
		Check("rotation: the first batch is the first 31 in order", b1.SequenceEqual(hundred.Take(31)) && (rot.MovesAt == r0 + day), string.Join(",", b1.Take(5)));
		Check("rotation: before its time is up the batch stays", rot.Current(hundred, 31, day, r0.AddHours(23)).SequenceEqual(b1) && (rot.MovesAt == r0 + day));
		List<uint> b2 = rot.Current(hundred, 31, day, r0 + day);
		Check("rotation: once it's up, the next 31", b2.SequenceEqual(hundred.Skip(31).Take(31)) && (rot.MovesAt == r0 + day + day) && (rot.Batch(31) == 2) && (rot.Batches(31) == 4));
		Check("rotation: the preview is the batch after", rot.Next(hundred, 31).SequenceEqual(hundred.Skip(62).Take(31)));

		// Persistence: a restart carries on with the same batch and the same clock.
		var again = new NocatFarm.Modules.IdleRotation(path, "rot-a");
		Check("rotation: after a restart it's the same batch, moving on at the same time",
			again.Current(hundred, 31, day, r0 + day + TimeSpan.FromHours(1)).SequenceEqual(b2) && (again.MovesAt == r0 + day + day), again.MovesAt.ToString("o"));
		Check("rotation: the state file is written whole (no .tmp left behind)", File.Exists(path)
			&& Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0);

		// Three days switched off: one batch on, not three - the missed batches aren't skipped.
		List<uint> b3 = again.Current(hundred, 31, day, r0 + TimeSpan.FromDays(5));
		Check("rotation: days late moves on ONE batch, not several", b3.SequenceEqual(hundred.Skip(62).Take(31)) && (again.MovesAt == r0 + TimeSpan.FromDays(6)));

		// The end of the lap: the last 7 topped up from the start, then a fresh lap.
		List<uint> b4 = again.Current(hundred, 31, day, r0 + TimeSpan.FromDays(6));
		Check("rotation: the last batch is the last 7 topped up with the first 24", (b4.Count == 31)
			&& b4.SequenceEqual(hundred.Skip(93).Concat(hundred.Take(24))) && (b4.Distinct().Count() == 31), string.Join(",", b4.Take(9)));
		HashSet<uint> lap = [.. b1, .. b2, .. b3, .. b4];
		Check("rotation: one lap gives every one of the 100 games a turn", lap.SetEquals(hundred), $"{lap.Count}");
		List<uint> reordered = [.. hundred.AsEnumerable().Reverse()];   // playtime moved on - a fresh lap takes the new order
		Check("rotation: the next batch preview at the end of a lap is the fresh lap's start", again.Next(reordered, 31).SequenceEqual(reordered.Take(31)));
		List<uint> b5 = again.Current(reordered, 31, day, r0 + TimeSpan.FromDays(7));
		Check("rotation: after the last batch a new lap starts, in the new order", b5.SequenceEqual(reordered.Take(31)) && (again.Batch(31) == 1));

		// List changes mid-lap.
		string pathB = NocatFarm.Modules.IdleRotation.PathFor("rot-b");
		var rb = new NocatFarm.Modules.IdleRotation(pathB, "rot-b");
		List<uint> forty = [.. Enumerable.Range(1, 40).Select(static i => (uint) i)];
		rb.Current(forty, 10, day, r0);
		rb.Current(forty, 10, day, r0 + day);   // batch 2: 11-20
		List<uint> shrunk = [.. forty.Where(static a => a is not (3 or 15))];
		List<uint> afterRemove = rb.Current(shrunk, 10, day, r0 + day + TimeSpan.FromHours(1));
		Check("rotation: a game taken off drops out, and the batch doesn't jump", !afterRemove.Contains(15u) && !afterRemove.Contains(3u)
			&& afterRemove.SequenceEqual<uint>([11, 12, 13, 14, 16, 17, 18, 19, 20, 21]), string.Join(",", afterRemove));
		Check("rotation: still batch 2 after a game before it went (counted rounding up)", rb.Batch(10) == 2, $"{rb.Batch(10)}");
		List<uint> grown = [999, .. shrunk];   // new, never played: a fresh lap would put it first
		rb.Current(grown, 10, day, r0 + day + TimeSpan.FromHours(2));
		List<uint> seen = [];
		for (int i = 2; i < 6; i++) {
			seen.AddRange(rb.Current(grown, 10, day, r0 + TimeSpan.FromDays(i)));
		}

		Check("rotation: a game added mid-lap joins the end of this lap, not the next one", seen.Contains(999u), string.Join(",", seen.TakeLast(10)));
		Check("rotation: nothing plays twice in one batch", seen.Chunk(10).All(static c => c.Distinct().Count() == c.Length));

		// Shortening the period brings the next move in; 'next' moves on now.
		string pathC = NocatFarm.Modules.IdleRotation.PathFor("rot-c");
		var rc = new NocatFarm.Modules.IdleRotation(pathC, "rot-c");
		rc.Current(forty, 10, TimeSpan.FromHours(48), r0);
		rc.Current(forty, 10, TimeSpan.FromHours(6), r0.AddHours(1));
		Check("rotation: 'Rotate every' shortened pulls the next move in", rc.MovesAt == r0.AddHours(7), rc.MovesAt.ToString("o"));
		rc.MoveOn();
		Check("rotation: 'next' moves on straight away", rc.MoveRequested && rc.Current(forty, 10, TimeSpan.FromHours(6), r0.AddHours(2)).SequenceEqual(forty.Skip(10).Take(10))
			&& !rc.MoveRequested && (rc.MovesAt == r0.AddHours(8)));

		// A bad state file: starts fresh instead of throwing, and writes a good one.
		string pathD = NocatFarm.Modules.IdleRotation.PathFor("rot-d");
		File.WriteAllText(pathD, "{ \"Lap\": [1, 2, oops");
		var rd = new NocatFarm.Modules.IdleRotation(pathD, "rot-d");
		Check("rotation: a broken state file starts it fresh", rd.Current(forty, 10, day, r0).SequenceEqual(forty.Take(10)) && File.ReadAllText(pathD).Contains("\"Pos\":0", StringComparison.Ordinal));
		File.WriteAllText(pathD, "{\"Lap\":[5,5,0,6,7],\"Pos\":-4,\"MovesAt\":\"2099-01-01T00:00:00Z\"}");
		var rd2 = new NocatFarm.Modules.IdleRotation(pathD, "rot-d");
		List<uint> odd = rd2.Current(forty, 10, day, r0);
		Check("rotation: a nonsense file (repeats, a 0, a negative spot, a date decades out) is made sense of", (odd.Count == 10) && (odd.Distinct().Count() == 10)
			&& !odd.Contains(0u) && (rd2.MovesAt == r0 + day), string.Join(",", odd));

		// ── the idler: what it actually plays ──
		NocatFarm.Config.Live.Global = new NocatFarm.Config.GlobalConfig { GlobalBlacklistedGames = [777] };
		List<uint> OldWay(NocatFarm.Core.Bot b) => [.. b.Cfg.IdleGames.Where(a => !b.Cfg.BlacklistedGames.Contains(a) && !NocatFarm.Config.Live.Global.GlobalBlacklistedGames.Contains(a) && !b.Refunds.Holds(a))];

		// old/kylro's shape: a custom name and 8 real games. Both settings off: exactly the old list, no state file.
		var robotCfg = new NocatFarm.Config.BotConfig { CustomGameName = "nocat.lol", IdleGames = [730, 440, 570, 550, 252490, 578080, 4000, 271590], BlacklistedGames = [4000] };
		var robot = new NocatFarm.Core.Bot("rot-robot", robotCfg);
		var robotIdler = new NocatFarm.Modules.Idler(robot);
		robot.AddModule(robotIdler);
		Check("idler, both off: exactly the old list (blacklist still honoured)", robotIdler.Plan(r0).SequenceEqual(OldWay(robot)) && !robotIdler.Plan(r0).Contains(4000u)
			&& (robotIdler.Rotating == null) && !File.Exists(NocatFarm.Modules.IdleRotation.PathFor("rot-robot")));
		robotCfg.IdleGames = [.. forty, 777];   // hand-edited past 32 (or left long when rotation was switched off)
		// Cut to what Steam plays at once - 31 beside the custom name. Handed on whole, the account counted 40 as playing
		// while Steam played 32, and banked 40 game-minutes a minute.
		Check("idler, both off: a 40-game list is cut to the first 31 beside a custom name", robotIdler.Plan(r0).SequenceEqual(OldWay(robot).Take(31)) && (robotIdler.Plan(r0).Count == 31)
			&& (robotIdler.Rotating == null) && !File.Exists(NocatFarm.Modules.IdleRotation.PathFor("rot-robot")), $"{robotIdler.Plan(r0).Count}");
		robotCfg.CustomGameNameEnabled = false;
		Check("idler, both off: ...and to 32 with no custom name", robotIdler.Plan(r0).SequenceEqual(OldWay(robot).Take(32)), $"{robotIdler.Plan(r0).Count}");
		robotCfg.CustomGameNameEnabled = true;

		// Rotation on, the same 40: a batch of 31 beside the custom name, 32 without.
		robotCfg.BlacklistedGames = [];
		robotCfg.RotateIdleGames = true;
		List<uint> withName = robotIdler.Plan(r0);
		Check("idler, rotating: 31 beside a custom name", (withName.Count == 31) && (robotIdler.Rotating is { Total: 40, Batches: 2 }) && !withName.Contains(777u), $"{withName.Count}");
		Check("idler, rotating: the card shows 'idling 31 of 40 games - next batch at ...'", StatusWith(robot, robotIdler).Contains("31 of 40", StringComparison.Ordinal), StatusWith(robot, robotIdler));
		robotCfg.CustomGameNameEnabled = false;
		Check("idler, rotating: 32 with no custom name", robotIdler.Plan(r0).Count == 32);
		robotCfg.CustomGameNameEnabled = true;
		robotCfg.IdleGames = [730, 440, 570];
		Check("idler, rotating: a list that fits plays whole, no rotation", robotIdler.Plan(r0).SequenceEqual<uint>([730, 440, 570]) && (robotIdler.Rotating == null));

		// Turning a setting on puts the new games on at the next 20-second look - even after 'rotation <account>'
		// has worked out a plan (on his kylro that plan counted as done and the old 8 stayed for up to 7 minutes).
		var asserted = typeof(NocatFarm.Modules.Idler).GetField("_assertedFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
		robotCfg.RotateIdleGames = false;
		asserted.SetValue(robotIdler, (false, false, false));
		Check("idler: nothing changed since the last re-assert -> nothing to do early", !robotIdler.OutOfDate);
		robotCfg.RotateIdleGames = true;
		Check("idler: rotation switched on -> the new games go on early", robotIdler.OutOfDate);
		robotIdler.Plan(r0);
		Check("idler: ...and still after 'rotation <account>' worked out a plan", robotIdler.OutOfDate);
		asserted.SetValue(robotIdler, null);
		Check("idler: before its first re-assert it doesn't jump the logon wait", !robotIdler.OutOfDate);

		// Persistence through the idler: a new idler on the same account picks up the same batch.
		robotCfg.IdleGames = forty;
		var r1 = new NocatFarm.Modules.Idler(robot);
		r1.Plan(r0);
		List<uint> secondBatch = r1.Plan(r0 + day);
		var r2 = new NocatFarm.Modules.Idler(robot);
		Check("idler: after a restart the same batch carries on", r2.Plan(r0 + day + TimeSpan.FromMinutes(5)).SequenceEqual(secondBatch) && (r2.Rotating!.Batch == 2));

		// The whole library: owned games, the listed ones first, never blacklisted, refundable or (by default) shared.
		var libCfg = new NocatFarm.Config.BotConfig { IdleWholeLibrary = true, IdleGames = [9001, 9002], BlacklistedGames = [5], SkipRefundableGames = true, CustomGameNameEnabled = false };
		var libBot = new NocatFarm.Core.Bot("rot-lib", libCfg);
		var libIdler = new NocatFarm.Modules.Idler(libBot);
		libBot.AddModule(libIdler);
		Check("whole library: until the library is read, just the idle list", libIdler.Plan(r0).SequenceEqual<uint>([9001, 9002]));
		typeof(NocatFarm.Modules.Idler).GetField("_assertedFor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(libIdler, (true, false, false));
		NocatFarm.Core.Library library = libBot.Library;
		Type lt = typeof(NocatFarm.Core.Library);
		List<NocatFarm.Core.Library.Entry> owned = [.. Enumerable.Range(1, 60).Select(static i => new NocatFarm.Core.Library.Entry((uint) i, $"g{i}", 1000 - i, DateTime.MinValue, 0))];
		owned.Add(new NocatFarm.Core.Library.Entry(777, "globally blacklisted", 0, DateTime.MinValue, 0));
		owned.Add(new NocatFarm.Core.Library.Entry(8001, "family game", 0, DateTime.MinValue, 76561198000000001));
		lt.GetField("_games", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(library, owned);
		lt.GetField("_byApp", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(library, owned.ToDictionary(static g => g.AppId));
		lt.GetProperty("Ready")!.SetValue(library, true);
		typeof(NocatFarm.Core.RefundGuard).GetField("_held", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(libBot.Refunds, new HashSet<uint> { 7 });
		Check("whole library: the library arriving puts the whole list on early", libIdler.OutOfDate);
		List<uint> cut = libIdler.Plan(r0);
		Check("whole library, no rotation: the listed games first, then the least played, cut to 32",
			(cut.Count == 32) && cut.Take(2).SequenceEqual<uint>([9001, 9002]) && (cut[2] == 60) && (cut[3] == 59) && (libIdler.Rotating == null), string.Join(",", cut.Take(5)));
		libCfg.RotateIdleGames = true;
		libCfg.HourTargets = "30:100";   // 970 minutes played - a target still to reach goes first
		libIdler.Plan(r0);
		NocatFarm.Modules.Idler.RotationView view = libIdler.Rotating!;
		HashSet<uint> all = [.. view.Now, .. view.Next];
		Check("whole library: every owned game on the list, minus blacklisted, global blacklist, refundable and shared",
			(view.Total == 60) && !all.Contains(5u) && !all.Contains(777u) && !all.Contains(7u) && !all.Contains(8001u) && all.Contains(9001u), $"{view.Total}");
		Check("whole library: an hour target still to reach goes first", view.Now[0] == 30, string.Join(",", view.Now.Take(4)));
		libCfg.IncludeFamilyLibrary = true;
		libIdler.Plan(r0);
		Check("whole library: family-shared games only with 'Include family-shared games'", libIdler.Rotating!.Total == 61);

		// ── who else has the account: card farming and human mode come first ──
		void SetProp(NocatFarm.Core.Bot b, string name, object? value) => typeof(NocatFarm.Core.Bot).GetProperty(name)!.SetValue(b, value);
		var farmCfg = new NocatFarm.Config.BotConfig { RotateIdleGames = true, IdleGames = forty, CustomGameName = "nocat.lol" };
		var farmBot = new NocatFarm.Core.Bot("rot-farm", farmCfg);
		var farmIdler = new NocatFarm.Modules.Idler(farmBot);
		farmBot.AddModule(farmIdler);
		SetProp(farmBot, "State", NocatFarm.Core.BotState.Online);
		SetProp(farmBot, "IsFarming", true);
		farmIdler.Assert();
		Check("card farming first: the idler stands off and the rotation doesn't start", (farmIdler.Rotating == null) && (farmBot.PlayingApps.Count == 0)
			&& !File.Exists(NocatFarm.Modules.IdleRotation.PathFor("rot-farm")));
		Check("card farming first: its card says standing by", farmIdler.Status == NocatFarm.Core.Loc.T("standing by (farming cards)"), farmIdler.Status);

		var humanCfg = new NocatFarm.Config.BotConfig { LegitMode = true, RotateIdleGames = true, IdleWholeLibrary = true, IdleGames = forty, GameWeights = "730:70, 440:30" };
		var humanBot = new NocatFarm.Core.Bot("rot-human", humanCfg);
		var humanIdler = new NocatFarm.Modules.Idler(humanBot);
		humanBot.AddModule(humanIdler);
		SetProp(humanBot, "State", NocatFarm.Core.BotState.Online);
		humanIdler.Assert();
		Check("human mode: never rotates, never idles a list", (humanIdler.Rotating == null) && (humanBot.PlayingApps.Count == 0)
			&& !File.Exists(NocatFarm.Modules.IdleRotation.PathFor("rot-human")));
		Check("human mode: both settings are hidden there (rage-only)", new[] { "IdleWholeLibrary", "RotateIdleGames", "RotateEveryHours" }
			.All(static n => NocatFarm.Config.Settings.FindBot(n) is { Mode: "rage" }));

		// ── settings: off by default, and a list past 32 only with the rotation on ──
		var fresh = new NocatFarm.Config.BotConfig();
		Check("settings: both off by default, 24h", !fresh.IdleWholeLibrary && !fresh.RotateIdleGames && (fresh.RotateEveryHours == 24)
			&& NocatFarm.Config.Settings.FindBot("RotateEveryHours") is { Advanced: true, Min: 1.0 });
		string fortyText = string.Join(",", Enumerable.Range(1, 40));
		string? refused = NocatFarm.Config.Settings.Apply(fresh, NocatFarm.Config.Settings.FindBot("IdleGames")!, fortyText);
		fresh.RotateIdleGames = true;
		string? taken = NocatFarm.Config.Settings.Apply(fresh, NocatFarm.Config.Settings.FindBot("IdleGames")!, fortyText);
		Check("settings: 40 games to idle refused with the rotation off (saying how), taken with it on",
			(refused != null) && refused.Contains("Rotate the idle list", StringComparison.Ordinal) && (taken == null) && (fresh.IdleGames.Count == 40), refused ?? "");
		Check("settings: the overnight list stays capped at 32", NocatFarm.Config.Settings.Apply(fresh, NocatFarm.Config.Settings.FindBot("OfflineIdleGames")!, fortyText) != null);

		// ── the command ──
		var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false });
		var bots = (System.Collections.Concurrent.ConcurrentDictionary<string, NocatFarm.Core.Bot>) typeof(NocatFarm.Core.BotManager)
			.GetField("_bots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mgr)!;
		var cmdCfg = new NocatFarm.Config.BotConfig { RotateIdleGames = true, IdleGames = forty, CustomGameName = "nocat.lol", RotateEveryHours = 12 };
		var cmdBot = new NocatFarm.Core.Bot("rotcmd", cmdCfg);
		var cmdIdler = new NocatFarm.Modules.Idler(cmdBot);
		cmdBot.AddModule(cmdIdler);
		bots["rotcmd"] = cmdBot;
		var offBot = new NocatFarm.Core.Bot("rotoff", new NocatFarm.Config.BotConfig { IdleGames = [730] });
		offBot.AddModule(new NocatFarm.Modules.Idler(offBot));
		bots["rotoff"] = offBot;
		bots["rot-human"] = humanBot;

		string shown = await Commands.RunAsync(mgr, "rotation rotcmd");
		Check("command: rotation shows on, list size, batch size, which batch, when, and the next batch",
			shown.Contains("every 12h", StringComparison.Ordinal) && shown.Contains("40 games", StringComparison.Ordinal) && shown.Contains("31 at once", StringComparison.Ordinal)
			&& shown.Contains("batch 1 of 2", StringComparison.Ordinal) && shown.Contains("next batch at", StringComparison.Ordinal) && shown.Contains("next:", StringComparison.Ordinal), shown);
		string moved = await Commands.RunAsync(mgr, "rotation rotcmd next");
		Check("command: 'next' on an account that isn't idling says it moves on once it is", moved.Contains("as soon as it is", StringComparison.Ordinal) && cmdIdler.Rotation.MoveRequested, moved);
		Check("command: ...and it does, on the next idle", cmdIdler.Plan(DateTime.UtcNow).SequenceEqual(forty.Skip(31).Concat(forty.Take(22))) && (cmdIdler.Rotating!.Batch == 2));
		string off = await Commands.RunAsync(mgr, "rotation rotoff");
		Check("command: says when rotation is off, and how to turn it on", off.Contains("rotation is off", StringComparison.Ordinal) && off.Contains("RotateIdleGames true", StringComparison.Ordinal), off);
		string human = await Commands.RunAsync(mgr, "rotation rot-human");
		Check("command: a human-mode account has no rotation", human.Contains("human mode", StringComparison.Ordinal), human);
		Check("command: listed with help", Commands.All.Any(static c => (c.Name == "rotation") && c.Help.Length > 20));
		await farmBot.DisposeAsync();
		await humanBot.DisposeAsync();
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		NocatFarm.Config.Live.Global = realGlobal;

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// temp - it goes when Windows tidies up
		}
	}

	static string StatusWith(NocatFarm.Core.Bot b, NocatFarm.Modules.Idler idler) {
		// Status reads what is playing; outside a real session nothing is, so put the custom name's line there.
		typeof(NocatFarm.Core.Bot).GetProperty("Playing")!.SetValue(b, "nocat.lol (+31)");

		return idler.Status;
	}
}

// ── a life, not just a day: learning from you, new games, quiet spells and late nights, joining a friend ──────────
{
	const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
	const BindingFlags Stat = BindingFlags.NonPublic | BindingFlags.Static;
	const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
	Type ht = typeof(NocatFarm.Modules.HumanMode);
	Type hh = typeof(NocatFarm.Modules.HumanMode).Assembly.GetType("NocatFarm.Modules.HumanHabits")!;
	Type watchT = typeof(NocatFarm.Modules.HumanMode).Assembly.GetType("NocatFarm.Modules.OwnerWatch")!;
	Type rhythmT = typeof(NocatFarm.Modules.HumanMode).Assembly.GetType("NocatFarm.Modules.DayRhythm")!;
	Type extrasT = typeof(NocatFarm.Modules.HumanMode).Assembly.GetType("NocatFarm.Modules.DayExtras")!;
	Type trialT = typeof(NocatFarm.Modules.HumanMode).Assembly.GetType("NocatFarm.Modules.Trial")!;
	FieldInfo HF(string f) => ht.GetField(f, Inst) ?? throw new MissingFieldException(f);
	T HGet<T>(object h, string f) => (T) HF(f).GetValue(h)!;
	void Set(object h, string f, object? v) => HF(f).SetValue(h, v);
	object? HCall(object h, string m, params object?[] args) => ht.GetMethod(m, Inst)!.Invoke(h, args);
	object? HH(string m, params object?[] args) => hh.GetMethod(m, Stat)!.Invoke(null, args);
	void BotProp(NocatFarm.Core.Bot b, string p, object? v) => typeof(NocatFarm.Core.Bot).GetProperty(p, AnyInst)!.SetValue(b, v);
	MethodInfo rollDay = ht.GetMethod("RollDay", Stat)!;
	MethodInfo rollWith = ht.GetMethod("RollDayWith", Stat)!;
	int RI(object d, string n) => (int) d.GetType().GetProperty(n)!.GetValue(d)!;
	bool RB(object d, string n) => (bool) d.GetType().GetProperty(n)!.GetValue(d)!;
	DateTime BedOf(object d, DateTime date) => date.AddDays(RB(d, "BedIsTomorrow") ? 1 : 0).AddHours(RI(d, "BedHour")).AddMinutes(RI(d, "BedMinute"));
	object Rhythm(bool quiet, int pct, bool late, int lateMin) => Activator.CreateInstance(rhythmT, quiet, pct, late, lateMin)!;
	object Extras(object rhythm, NocatFarm.Modules.OwnerHabits? habits, double pull) => Activator.CreateInstance(extrasT, rhythm, habits, pull)!;
	NocatFarm.Modules.OwnerHabits.Sitting Sit(DateTime start, int minutes, uint app) => new() { Start = start, Minutes = minutes, App = app };

	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-life-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	NocatFarm.Config.GlobalConfig liveBefore = NocatFarm.Config.Live.Global;

	try {
		// ── the settings: four of them, in Human mode, all off, and an old config loads with them off ──
		string[] extrasNames = ["LearnFromOwner", "NewGamesFirst", "LongerRhythms", "JoinFriends"];
		var defs = extrasNames.Select(NocatFarm.Config.Settings.FindBot).ToList();
		Check("life settings: all four are per-account settings in the Human mode section, human-mode only",
			defs.All(static d => (d != null) && (d.Section == NocatFarm.Config.Settings.SecHuman) && (d.Mode == "legit")));
		var fresh = new NocatFarm.Config.BotConfig();
		Check("life settings: every one is off by default", (fresh.LearnFromOwner == 0) && !fresh.NewGamesFirst && !fresh.LongerRhythms && !fresh.JoinFriends);
		var old = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Config.BotConfig>("{\"LegitMode\":true,\"GameWeights\":\"730:70, 440:30\",\"WeekdayHours\":5}")!;
		Check("life settings: a config written before them loads unchanged, with all four off",
			old.LegitMode && (old.WeekdayHours == 5) && (old.LearnFromOwner == 0) && !old.NewGamesFirst && !old.LongerRhythms && !old.JoinFriends);
		var learnDef = NocatFarm.Config.Settings.FindBot("LearnFromOwner")!;
		var lc = new NocatFarm.Config.BotConfig();
		Check("life settings: 'Learn from how I play' takes off / a little / a lot, by name or number",
			(NocatFarm.Config.Settings.Apply(lc, learnDef, "a lot") == null) && (lc.LearnFromOwner == 2)
			&& (NocatFarm.Config.Settings.Apply(lc, learnDef, "1") == null) && (lc.LearnFromOwner == 1)
			&& (NocatFarm.Config.Settings.Apply(lc, learnDef, "3") != null) && (NocatFarm.Config.Settings.Apply(lc, learnDef, "off") == null) && (lc.LearnFromOwner == 0));

		// ── with all four off, the day is exactly what it was ──
		var plain = new NocatFarm.Config.BotConfig();
		int differ = 0, differEmpty = 0;
		DateTime lb1 = DateTime.MinValue, lb2 = DateTime.MinValue, lb3 = DateTime.MinValue;
		Random ra = new(42), rb = new(42), rc = new(42);

		for (DateTime date = new(2026, 1, 1); date < new DateTime(2028, 1, 1); date = date.AddDays(1)) {
			object a = rollDay.Invoke(null, [plain, date, 70, true, lb1, ra])!;
			object b = rollWith.Invoke(null, [plain, date, 70, true, lb2, rb, null])!;
			object c = rollWith.Invoke(null, [plain, date, 70, true, lb3, rc, Extras(Rhythm(false, 50, false, 0), null, 0.0)])!;
			differ += a.Equals(b) ? 0 : 1;
			differEmpty += a.Equals(c) ? 0 : 1;
			lb1 = BedOf(a, date);
			lb2 = BedOf(b, date);
			lb3 = BedOf(c, date);
		}

		Check("all off: two years of days roll number for number as before", differ == 0, $"{differ} days differ");
		Check("all off: an ordinary rhythm and no habits change nothing either", differEmpty == 0, $"{differEmpty} days differ");

		var offBot = new NocatFarm.Core.Bot("harness-life-off", new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:20, 550:10" });
		var offMode = new NocatFarm.Modules.HumanMode(offBot);
		offBot.AddModule(offMode);
		Check("all off: the day's roll gets no extras at all", HCall(offMode, "Extras", DateTime.Today) == null);
		Set(offMode, "_rng", new Random(77));
		Set(offMode, "_trials", Activator.CreateInstance(typeof(List<>).MakeGenericType(trialT)));
		bool noneExtra = true;

		for (int i = 0; i < 2000; i++) {
			noneExtra &= (uint) HCall(offMode, "PickExtra")! == 0;
		}

		Check("all off: no friend or new-game sitting, and the dice aren't touched", noneExtra && (HGet<Random>(offMode, "_rng").Next() == new Random(77).Next()));
		Check("all off: 'human' shows nothing new", offMode.ExtrasReport().Count == 0);
		List<(uint Game, int Weight)> offRotation = offMode.Rotation;
		Check("all off: the games list is exactly as written", offRotation.SequenceEqual(NocatFarm.Modules.HumanMode.ParseWeights("730:70, 440:20, 550:10")));
		List<string> offWeek = NocatFarm.Modules.HumanMode.PreviewWeek(offBot.Cfg, offRotation, offBot.Name, null);
		Check("all off: 'human week' marks no quiet spells or late nights", offWeek.All(static l => !l.Contains("(a quiet spell)") && !l.Contains("(a late night)")));

		// ── learning from you: watching ──
		object watch = Activator.CreateInstance(watchT, nonPublic: true)!;
		List<NocatFarm.Modules.OwnerHabits.Sitting> Obs(DateTime at, bool on, uint app) =>
			((IEnumerable) watchT.GetMethod("Observe", Inst)!.Invoke(watch, [at, on, app])!).Cast<NocatFarm.Modules.OwnerHabits.Sitting>().ToList();
		// Looked at every five minutes here (the real tick is twenty seconds).
		List<NocatFarm.Modules.OwnerHabits.Sitting> On(DateTime from, int minutes, uint app) {
			List<NocatFarm.Modules.OwnerHabits.Sitting> done = [];

			for (int m = 0; m < minutes; m += 5) {
				done.AddRange(Obs(from.AddMinutes(m), true, app));
			}

			return done;
		}

		DateTime w0 = new(2026, 9, 1, 19, 0, 0);
		On(w0, 95, 730);
		var sat = Obs(w0.AddMinutes(95), false, 0);
		Check("learning: a sitting of yours is kept - when, how long, which game", (sat.Count == 1) && (sat[0].Start == w0) && (sat[0].Minutes == 95) && (sat[0].App == 730),
			sat.Count == 1 ? $"{sat[0].Start:HH:mm} {sat[0].Minutes}m {sat[0].App}" : $"{sat.Count}");
		On(w0.AddHours(3), 5, 730);
		var brief = Obs(w0.AddHours(3).AddMinutes(5), false, 0);
		Check("learning: a few minutes (Steam still describing our own last session after a restart) isn't counted", brief.Count == 0);
		On(w0.AddHours(4), 40, 730);
		var switched = On(w0.AddHours(4).AddMinutes(40), 60, 440);
		var second = Obs(w0.AddHours(5).AddMinutes(40), false, 0);
		Check("learning: changing game makes two sittings, each on its own game",
			(switched.Count == 1) && (switched[0].App == 730) && (switched[0].Minutes == 40) && (second.Count == 1) && (second[0].App == 440) && (second[0].Minutes == 60),
			string.Join(", ", switched.Concat(second).Select(static x => $"{x.App} {x.Minutes}m")));
		On(w0.AddHours(8), 10, 0);
		On(w0.AddHours(8).AddMinutes(10), 15, 570);
		var lostGap = Obs(w0.AddHours(10), true, 570);
		Check("learning: an unnamed game takes the name Steam gives it next; a long silence ends the sitting where it was last seen",
			(lostGap.Count == 1) && (lostGap[0].App == 570) && (lostGap[0].Minutes == 20), string.Join(", ", lostGap.Select(static x => $"{x.App} {x.Minutes}m")));

		// ── learning: keeping it ──
		var habits = new NocatFarm.Modules.OwnerHabits();
		DateTime today = DateTime.Now.Date;

		for (int d = 1; d <= 6; d++) {
			habits.Add(Sit(today.AddDays(-d).AddHours(19), 120, 730), DateTime.Now);
		}

		Check("learning: six days seen isn't enough", !habits.Ready && (habits.DaysSeen == 6));
		habits.Add(Sit(today.AddDays(-7).AddHours(18).AddMinutes(30), 90, 440), DateTime.Now);
		habits.Add(Sit(today.AddDays(-7).AddHours(20), 60, 730), DateTime.Now);   // same day, a second sitting
		Check("learning: a week is - and two sittings on one day count as one day", habits.Ready && (habits.DaysSeen == 7));
		habits.Add(Sit(today.AddDays(-8).AddHours(1), 60, 730), DateTime.Now);    // 1am belongs to the evening before
		Check("learning: a game at 1am belongs to the evening before", (habits.DaysSeen == 8) && habits.Days().Any(static d => (d.Start == 25 * 60) && (d.End == 26 * 60)));
		habits.Add(Sit(today.AddDays(-90).AddHours(19), 60, 730), DateTime.Now);
		Check("learning: only the last 60 days are kept", habits.Sittings.All(static s => s.Start > DateTime.Now.AddDays(-61)));
		var byGame = habits.MinutesByGame();
		Check("learning: minutes per game", (byGame[730] == (6 * 120) + 60 + 60) && (byGame[440] == 90));
		double[] hourShare = habits.HourShare();
		string Hours(double[] sh) => (string) typeof(NocatFarm.Modules.OwnerHabits).GetMethod("HoursText", Stat)!.Invoke(null, [sh, 0.4])!;
		Check("learning: its usual hours read as a clock range", Hours(hourShare) == "19:00-21:00", Hours(hourShare));
		double[] late = new double[24];
		late[22] = late[23] = late[0] = 0.9;
		Check("learning: ...across midnight too", Hours(late) == "22:00-01:00", Hours(late));

		string hname = "harness-habits-" + Guid.NewGuid().ToString("N")[..6];
		habits.Save(hname);
		var back = NocatFarm.Modules.OwnerHabits.Load(hname);
		Check("learning: saved and read back whole", (back.DaysSeen == habits.DaysSeen) && (back.Sittings.Count == habits.Sittings.Count) && back.Ready);
		string habitsFile = Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "state", $"owner-{hname}.json");
		File.WriteAllText(habitsFile, "{ this is not json");
		NocatFarm.Log.Suppressed = true;
		var broken = NocatFarm.Modules.OwnerHabits.Load(hname);
		File.WriteAllText(habitsFile, "{\"Sittings\":[null,{\"Start\":\"2026-01-01T19:00:00\",\"Minutes\":-5,\"App\":730}]}");
		var odd = NocatFarm.Modules.OwnerHabits.Load(hname);
		NocatFarm.Log.Suppressed = false;
		Check("learning: a broken file is nothing learned yet, never a crash", (broken.DaysSeen == 0) && (odd.DaysSeen == 0));
		NocatFarm.Modules.OwnerHabits.Forget(hname);
		Check("learning: forgetting deletes it", !File.Exists(habitsFile) && (NocatFarm.Modules.OwnerHabits.Load(hname).DaysSeen == 0));

		// ── learning: the games list leans toward how you split your time between the same games ──
		List<(uint Game, int Weight)> listed = [(730, 70), (440, 20), (550, 10)];
		Dictionary<uint, int> played = new() { [730] = 300, [440] = 700, [999] = 5000 };   // 999: yours, but not in the list
		var little = (List<(uint Game, int Weight)>) HH("Reweight", listed, played, 0.3)!;
		var lot = (List<(uint Game, int Weight)>) HH("Reweight", listed, played, 0.7)!;
		List<double> ls = NocatFarm.Modules.HumanMode.Shares(little), ll = NocatFarm.Modules.HumanMode.Shares(lot);
		Check("learning: a little moves each game's share about 30% of the way to yours (main 70 -> 58)", (little[0].Weight == 58) && (Math.Abs(ls[1] - 35) < 1.5) && (Math.Abs(ls[2] - 7) < 1.5),
			string.Join(" ", ls.Select(static s => s.ToString("0.0"))));
		Check("learning: a lot about 70% of the way (main 70 -> 42)", (lot[0].Weight == 42) && (Math.Abs(ll[1] - 55) < 1.5) && (Math.Abs(ll[2] - 3) < 1.5),
			string.Join(" ", ll.Select(static s => s.ToString("0.0"))));
		Check("learning: a game of yours that isn't in the list is never added", little.Concat(lot).All(static w => w.Game != 999) && (little.Count == 3));
		Check("learning: nothing of yours in the list, or 'off': the list as written",
			((List<(uint Game, int Weight)>) HH("Reweight", listed, new Dictionary<uint, int> { [999] = 100 }, 0.7)!).SequenceEqual(listed)
			&& ((List<(uint Game, int Weight)>) HH("Reweight", listed, played, 0.0)!).SequenceEqual(listed));

		// ── learning: the day leans toward yours - but never breaks a night's sleep, bed after getting up, or the hours ──
		var owner = new NocatFarm.Modules.OwnerHabits();

		for (int d = 1; d <= 14; d++) {
			owner.Add(Sit(today.AddDays(-d).AddHours(18).AddMinutes(d % 3 * 10), 330, 730), DateTime.Now);   // on ~18:00-18:20, off ~23:30-23:50
		}

		(double Wake, double Bed, double Target, int Bad) Year(NocatFarm.Config.BotConfig c, double pull, int seed) {
			Random r = new(seed);
			DateTime last = DateTime.MinValue;
			double wakeSum = 0, bedSum = 0, targetSum = 0;
			int n = 0, bad = 0;

			for (DateTime date = new(2026, 1, 1); date < new DateTime(2027, 1, 1); date = date.AddDays(1)) {
				object? extras = pull > 0 ? Extras(Rhythm(false, 50, false, 0), owner, pull) : null;
				object d = rollWith.Invoke(null, [c, date, 70, true, last, r, extras])!;
				DateTime up = date.AddMinutes(RI(d, "WakeMinute"));
				DateTime bed = BedOf(d, date);
				int window = (int) (bed - up).TotalMinutes;
				bad += (bed <= up) || ((last != DateTime.MinValue) && (up < last.AddHours(4))) || (RI(d, "Target") > Math.Max(30, window * 4 / 5)) ? 1 : 0;
				wakeSum += RI(d, "WakeMinute");
				bedSum += (bed - date).TotalMinutes;
				targetSum += RI(d, "Target");
				n++;
				last = bed;
			}

			return (wakeSum / n, bedSum / n, targetSum / n, bad);
		}

		var dayCfg = new NocatFarm.Config.BotConfig { WeekdayHours = 4, WeekendHours = 5, DayOffChancePct = 0 };
		var baseY = Year(dayCfg, 0, 9);
		var littleY = Year(dayCfg, 0.3, 9);
		var lotY = Year(dayCfg, 0.7, 9);
		double yourUp = 18 * 60 + 10, yourOff = 23 * 60 + 40;
		double upMoved(double y) => (y - baseY.Wake) / (yourUp - baseY.Wake);
		double bedMoved(double y) => (y - baseY.Bed) / (yourOff - baseY.Bed);
		Check("learning: a little gets it up about 30% of the way toward when you get on", Math.Abs(upMoved(littleY.Wake) - 0.3) < 0.08,
			$"{upMoved(littleY.Wake):P0} (up {littleY.Wake / 60:0.0}h vs {baseY.Wake / 60:0.0}h, you {yourUp / 60:0.0}h)");
		Check("learning: a lot about 70% of the way", Math.Abs(upMoved(lotY.Wake) - 0.7) < 0.1, $"{upMoved(lotY.Wake):P0}");
		Check("learning: bedtime leans toward when you stop, as much", (Math.Abs(bedMoved(littleY.Bed) - 0.3) < 0.08) && (Math.Abs(bedMoved(lotY.Bed) - 0.7) < 0.1),
			$"{bedMoved(littleY.Bed):P0}, {bedMoved(lotY.Bed):P0}");
		Check("learning: every day still has bed after getting up, a night's sleep, and hours that fit", (littleY.Bad == 0) && (lotY.Bad == 0), $"{littleY.Bad}, {lotY.Bad}");
		Check("learning: how long it plays is still the hours setting's", Math.Abs(lotY.Target - baseY.Target) < baseY.Target * 0.05, $"{lotY.Target:0} vs {baseY.Target:0} min");

		var noSame = new NocatFarm.Modules.OwnerHabits();

		for (int d = 1; d <= 10; d++) {
			noSame.Add(Sit(today.AddDays(-d).AddHours(20), 120, 730), DateTime.Now);   // exactly 20:00 every day
		}

		Random sr = new(3);
		DateTime slb = DateTime.MinValue;
		int lastWake = -1, sameWake = 0;

		for (DateTime date = new(2026, 1, 1); date < new DateTime(2027, 1, 1); date = date.AddDays(1)) {
			object d = rollWith.Invoke(null, [plain, date, 70, true, slb, sr, Extras(Rhythm(false, 50, false, 0), noSame, 0.7)])!;
			sameWake += RI(d, "WakeMinute") == lastWake ? 1 : 0;
			lastWake = RI(d, "WakeMinute");
			slb = BedOf(d, date);
		}

		Check("learning: even following somebody who always starts at 20:00 sharp, it isn't up at one minute every day", sameWake < 365 / 50, $"{sameWake} repeats");

		// ── learning: in the account ──
		var lbot = new NocatFarm.Core.Bot("harness-life-learn", new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30", LearnFromOwner = 2 });
		var lmode = new NocatFarm.Modules.HumanMode(lbot);
		lbot.AddModule(lmode);
		Set(lmode, "_habits", habits);
		List<double> learnedShares = NocatFarm.Modules.HumanMode.Shares(lmode.Rotation);
		Check("learning: in use, the account's games lean toward yours (you: 730 more than 440)", learnedShares[0] > 70, string.Join(" ", learnedShares.Select(static s => s.ToString("0"))));
		Check("learning: in use, the day's roll gets your habits", HCall(lmode, "Extras", DateTime.Today) != null);
		lbot.Cfg.LearnFromOwner = 0;
		Check("learning: switched off, the games are as written again even with a week learned", NocatFarm.Modules.HumanMode.Shares(lmode.Rotation)[0] == 70);
		lbot.Cfg.LearnFromOwner = 1;
		Set(lmode, "_habits", new NocatFarm.Modules.OwnerHabits());
		Check("learning: under a week seen, nothing changes yet", (NocatFarm.Modules.HumanMode.Shares(lmode.Rotation)[0] == 70) && (HCall(lmode, "Extras", DateTime.Today) == null)
			&& lmode.ExtrasReport().Any(static l => l.Contains("watching - 0 of 7")));

		// Watching you, tick by tick: nothing is sent, nothing changes phase - it only writes down.
		BotProp(lbot, "State", NocatFarm.Core.BotState.Online);
		BotProp(lbot, "OnlineSince", DateTime.UtcNow.AddHours(-3));
		var phaseBefore = lmode.Current;
		DateTime t1 = DateTime.Now.AddHours(-2);
		BotProp(lbot, "PlayingBlocked", true);
		BotProp(lbot, "OtherSessionApp", 730u);
		HCall(lmode, "WatchOwner", t1);
		HCall(lmode, "WatchOwner", t1.AddMinutes(20));
		BotProp(lbot, "PlayingBlocked", false);
		BotProp(lbot, "OtherSessionApp", 0u);
		HCall(lmode, "WatchOwner", t1.AddMinutes(45));
		var kept = NocatFarm.Modules.OwnerHabits.Load(lbot.Name);
		Check("learning: you playing on the account is written down (and saved)", (kept.Sittings.Count == 1) && (kept.Sittings[0].App == 730) && (kept.Sittings[0].Minutes == 45));
		Check("learning: ...without it doing anything else", (lmode.Current == phaseBefore) && lbot.PlayingApps.Count == 0);
		lbot.Cfg.LearnFromOwner = 0;
		BotProp(lbot, "OtherSessionApp", 440u);
		HCall(lmode, "WatchOwner", t1.AddMinutes(50));
		BotProp(lbot, "OtherSessionApp", 0u);
		HCall(lmode, "WatchOwner", t1.AddMinutes(90));
		Check("learning: with the setting off it isn't watching", NocatFarm.Modules.OwnerHabits.Load(lbot.Name).Sittings.Count == 1);
		lmode.ForgetHabits();
		Check("learning: 'habits forget' wipes it", NocatFarm.Modules.OwnerHabits.Load(lbot.Name).DaysSeen == 0);
		await lbot.DisposeAsync();

		// ── quiet spells and late nights ──
		object R(string account, DateTime date) => hh.GetMethod("RhythmFor", Stat)!.Invoke(null, [account, date])!;
		bool RQ(object r) => (bool) rhythmT.GetProperty("Quiet")!.GetValue(r)!;
		bool RL(object r) => (bool) rhythmT.GetProperty("LateNight")!.GetValue(r)!;
		Check("rhythm: the same account and day always get the same rhythm", Enumerable.Range(0, 400).All(i => R("new", new DateTime(2026, 1, 1).AddDays(i)).Equals(R("new", new DateTime(2026, 1, 1).AddDays(i)))));

		int spells = 0, quietDays = 0, lateNights = 0, freeNights = 0, lateWrongDay = 0, both = 0, longRuns = 0, days = 0;
		List<int> runs = [];

		foreach (string acct in (string[]) ["new", "old", "kylro", "someone", "reap."]) {
			int run = 0;
			bool clear = false;   // a spell already running on the first day is only partly seen - start counting after it

			for (DateTime date = new(2026, 1, 1); date < new DateTime(2029, 1, 1); date = date.AddDays(1)) {
				object r = R(acct, date);
				days++;
				bool free = date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;
				freeNights += free ? 1 : 0;
				lateNights += RL(r) ? 1 : 0;
				lateWrongDay += RL(r) && !free ? 1 : 0;
				both += RL(r) && RQ(r) ? 1 : 0;

				if (RQ(r)) {
					quietDays++;
					run += clear ? 1 : 0;
				} else if (run == 0) {
					clear = true;
				} else {
					spells++;
					runs.Add(run);
					longRuns += run > 5 ? 1 : 0;
					run = 0;
				}
			}
		}

		double perMonth = spells / (days / 30.4);
		runs.Sort();
		Check("rhythm: quiet spells come once or twice a month", (perMonth > 0.9) && (perMonth < 2.4), $"{perMonth:0.00} a month");
		Check("rhythm: a spell is 2 to 5 days (longer only where two happen to run together)", (runs[0] >= 2) && (runs[runs.Count / 2] is >= 2 and <= 5) && (longRuns < spells / 4),
			$"shortest {runs[0]}, median {runs[runs.Count / 2]}, {longRuns} of {spells} longer");
		Check("rhythm: late nights only on Friday and Saturday, never in a quiet spell, now and then", (lateWrongDay == 0) && (both == 0) && (lateNights > freeNights / 6) && (lateNights < freeNights / 2),
			$"{lateNights} of {freeNights} free nights");
		Check("rhythm: different accounts don't share their quiet spells",
			Enumerable.Range(0, 365).Count(i => RQ(R("new", new DateTime(2026, 1, 1).AddDays(i))) != RQ(R("old", new DateTime(2026, 1, 1).AddDays(i)))) > 20);

		// Through the real day roll: quiet days are shorter, late nights later - and the day off, the caps and the night's sleep still hold.
		double qT = 0, nT = 0, lateBed = 0, freeBed = 0;
		int qN = 0, nN = 0, lateN = 0, freeN = 0, offs = 0, rollDays = 0, broke = 0, qMarked = 0;

		foreach (var shape in new NocatFarm.Config.BotConfig[] { new(), new() { DayStartHour = 6, BedHour = 22, LateNightExtraHours = 6 }, new() { DayStartHour = 16, BedHour = 6 }, new() { DayStartHour = 0, BedHour = 20 } }) {
			shape.LongerRhythms = true;
			Random r = new(11);
			DateTime last = DateTime.MinValue;

			for (DateTime date = new(2026, 1, 1); date < new DateTime(2028, 1, 1); date = date.AddDays(1)) {
				object rh = R("new", date);
				object d = rollWith.Invoke(null, [shape, date, 70, true, last, r, Extras(rh, null, 0.0)])!;
				DateTime up = date.AddMinutes(RI(d, "WakeMinute"));
				DateTime bed = BedOf(d, date);
				int target = RI(d, "Target");
				rollDays++;
				offs += target == 0 ? 1 : 0;
				qMarked += RB(d, "Quiet") == RQ(rh) ? 0 : 1;
				broke += (bed <= up) || ((last != DateTime.MinValue) && (up < last.AddHours(4))) || (target > Math.Max(30, (int) (bed - up).TotalMinutes * 4 / 5))
					|| (RI(d, "SignOutCap") > shape.MaxSignOutsPerDay) || (RI(d, "MealCap") > shape.MealBreaksPerDay) ? 1 : 0;

				if ((target > 0) && (shape.DayStartHour == 13)) {
					if (RQ(rh)) {
						qT += target;
						qN++;
					} else {
						nT += target;
						nN++;
					}
				}

				if ((date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) && (shape.DayStartHour == 13)) {
					if (RL(rh)) {
						lateBed += (bed - date).TotalHours;
						lateN++;
					} else if (!RQ(rh)) {
						freeBed += (bed - date).TotalHours;
						freeN++;
					}
				}

				last = bed;
			}
		}

		double ratio = qT / qN / (nT / nN);
		Check("rhythm: a quiet spell's days are noticeably shorter - about half", (ratio > 0.4) && (ratio < 0.7), $"{ratio:P0} of an ordinary day, {qN} quiet days");
		Check("rhythm: a late night goes to bed later than the other Friday and Saturday nights", lateBed / lateN > (freeBed / freeN) + 0.8, $"{lateBed / lateN:0.0}h vs {freeBed / freeN:0.0}h");
		Check("rhythm: the day off still comes about one day in twenty", Math.Abs(((double) offs / rollDays) - 0.05) < 0.015, $"{(double) offs / rollDays:P1}");
		Check("rhythm: bed after getting up, a night's sleep, hours that fit and the caps - on every shape of day", broke == 0, $"{broke} of {rollDays}");
		Check("rhythm: the roll says which days are quiet", qMarked == 0);

		// 'human week' shows the very spells and late nights the real days will have.
		string? acctWith = null;

		for (int i = 0; (i < 2000) && (acctWith == null); i++) {
			string a = "harness-rhythm-" + i;

			if (Enumerable.Range(0, 7).Any(d => RQ(R(a, DateTime.Now.Date.AddDays(d)))) && Enumerable.Range(0, 7).Any(d => RL(R(a, DateTime.Now.Date.AddDays(d))))) {
				acctWith = a;
			}
		}

		var weekCfg = new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30", LongerRhythms = true };
		List<string> week = NocatFarm.Modules.HumanMode.PreviewWeek(weekCfg, null, acctWith, null);
		bool weekMatches = acctWith != null;

		for (int d = 0; (d < 7) && weekMatches; d++) {
			object rh = R(acctWith!, DateTime.Now.Date.AddDays(d));
			weekMatches = (week[d].Contains("(a quiet spell)") == RQ(rh)) && (week[d].Contains("(a late night)") == RL(rh));
		}

		Check("rhythm: 'human week' marks the same quiet days and late nights the account will have", weekMatches, string.Join(" | ", week));

		// Saved with the day's plan, and read back after a restart.
		string pname = "harness-life-plan-" + Guid.NewGuid().ToString("N")[..6];
		var pbot = new NocatFarm.Core.Bot(pname, new NocatFarm.Config.BotConfig { LegitMode = true });
		var pmode = new NocatFarm.Modules.HumanMode(pbot);
		Set(pmode, "_dayStamp", DateTime.Now.DayOfYear);
		Set(pmode, "_quietDay", true);
		Set(pmode, "_lateNight", false);
		Set(pmode, "_friendJoins", 2);
		Set(pmode, "_newGameSittings", 1);
		Set(pmode, "_joinedToday", "Bob in Portal 2");
		HCall(pmode, "Persist");
		var pagain = new NocatFarm.Modules.HumanMode(pbot);
		HCall(pagain, "Restore", NocatFarm.Modules.HumanDay.Load(pname, DateTime.Now)!);
		Check("rhythm and caps: a restart keeps the quiet day, the friends joined and the new-game sittings used",
			HGet<bool>(pagain, "_quietDay") && (HGet<int>(pagain, "_friendJoins") == 2) && (HGet<int>(pagain, "_newGameSittings") == 1) && (HGet<string>(pagain, "_joinedToday") == "Bob in Portal 2"));
		var oldPlan = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Modules.HumanDay>("{\"DayOfYear\":5,\"Year\":2026,\"TargetMinutes\":300}")!;
		Check("rhythm and caps: a plan saved before these existed reads as an ordinary day with nothing used", !oldPlan.Quiet && !oldPlan.LateNight && (oldPlan.FriendJoins == 0) && (oldPlan.NewGameSittings == 0) && (oldPlan.JoinedToday == ""));
		NocatFarm.Modules.HumanDay.Forget(pname);
		await pbot.DisposeAsync();

		// ── new games ──
		int TD(string a, uint app) => (int) hh.GetMethod("TrialDays", Stat)!.Invoke(null, [a, app])!;
		double TS(DateTime start, DateTime now, int d) => (double) hh.GetMethod("TrialStrength", Stat)!.Invoke(null, [start, now, d])!;
		DateTime TStart(NocatFarm.Core.AppOwnership o, NocatFarm.Config.BotConfig c) => (DateTime) hh.GetMethod("TrialStart", Stat)!.Invoke(null, [o, c])!;
		List<int> lengths = [.. Enumerable.Range(1, 3000).Select(i => TD("new", (uint) i))];
		Check("new games: tried for 3 to 10 days, the same every time for one game, different between games",
			lengths.All(static l => l is >= 3 and <= 10) && (lengths.Distinct().Count() == 8) && Enumerable.Range(1, 50).All(i => TD("new", (uint) i) == lengths[i - 1]));
		DateTime got = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
		List<double> taper = [.. Enumerable.Range(0, 7).Select(d => TS(got, got.AddDays(d).AddHours(1), 5))];
		Check("new games: keenest on the first day, less every day after, nothing once its days are up",
			(taper[0] == 1.0) && taper.Take(5).Zip(taper.Skip(1).Take(4)).All(static p => p.First > p.Second) && (taper[4] > 0) && (taper[5] == 0) && (taper[6] == 0) && (TS(got, got.AddDays(-1), 5) == 0),
			string.Join(" ", taper.Select(static t => t.ToString("0.00"))));
		var guarded = new NocatFarm.Config.BotConfig { SkipRefundableGames = true, RefundHoldDays = 14 };
		Check("new games: a bought game refund protection is holding is tried once the hold ends; a free one straight away",
			(TStart(new NocatFarm.Core.AppOwnership(got, true, true), guarded) == got.AddDays(14)) && (TStart(new NocatFarm.Core.AppOwnership(got, false, true), guarded) == got)
			&& (TStart(new NocatFarm.Core.AppOwnership(got, true, true), new NocatFarm.Config.BotConfig()) == got));

		// A day's sittings with a brand-new game about.
		var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false });
		var bots = (System.Collections.Concurrent.ConcurrentDictionary<string, NocatFarm.Core.Bot>) typeof(NocatFarm.Core.BotManager)
			.GetField("_bots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mgr)!;
		var nbot = new NocatFarm.Core.Bot("harness-life-new", new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:70, 440:30", NewGamesFirst = true, PureMainDayChancePct = 0 });
		var other = new NocatFarm.Core.Bot("harness-life-other", new NocatFarm.Config.BotConfig());
		var nmode = new NocatFarm.Modules.HumanMode(nbot);
		nbot.AddModule(nmode);
		bots[nbot.Name] = nbot;
		bots[other.Name] = other;
		BotProp(nbot, "SteamId", 76561190000000001UL);
		BotProp(other, "SteamId", 76561190000000002UL);

		// Its library: the two usual games, a new one (999), a new family-shared one (998), and the friends' games below.
		NocatFarm.Core.Library lib = nbot.Library;
		var entries = new List<NocatFarm.Core.Library.Entry> {
			new(730, "CS2", 50000, DateTime.MinValue, 0), new(440, "TF2", 9000, DateTime.MinValue, 0), new(999, "Brand New", 0, DateTime.MinValue, 0),
			new(998, "Borrowed", 0, DateTime.MinValue, 12345), new(620, "Portal 2", 300, DateTime.MinValue, 0), new(570, "Dota 2", 10, DateTime.MinValue, 0),
			new(550, "L4D2", 100, DateTime.MinValue, 0)
		};
		typeof(NocatFarm.Core.Library).GetField("_games", Inst)!.SetValue(lib, entries);
		typeof(NocatFarm.Core.Library).GetField("_byApp", Inst)!.SetValue(lib, entries.ToDictionary(static e => e.AppId));
		typeof(NocatFarm.Core.Library).GetProperty("Ready")!.SetValue(lib, true);

		object MakeTrials(params (uint App, double Days)[] ts) {
			var list = (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(trialT))!;

			foreach ((uint app, double ago) in ts) {
				list.Add(Activator.CreateInstance(trialT, app, 1.0, DateTime.UtcNow.AddDays(-ago), 6)!);
			}

			return list;
		}

		(int Sittings, int Days, int MaxDay, double Share) NewDays(object trials, int days, int seed) {
			Set(nmode, "_trials", trials);
			Set(nmode, "_rng", new Random(seed));
			NocatFarm.Log.Suppressed = true;
			int onNew = 0, maxDay = 0, all = 0;
			double newMin = 0, allMin = 0;

			for (int day = 0; day < days; day++) {
				Set(nmode, "_dayStamp", -1);
				Set(nmode, "_stayUpUntil", DateTime.MinValue);
				Set(nmode, "_targetMinutes", 360);
				Set(nmode, "_mainSharePct", 70);
				Set(nmode, "_otherBudget", 330);
				Set(nmode, "_playedMinutesToday", 0);
				Set(nmode, "_otherPlayed", 0);
				Set(nmode, "_farmPlayed", 0);
				Set(nmode, "_newGameSittings", 0);
				Set(nmode, "_friendJoins", 0);
				Set(nmode, "_firstSessionOfDay", true);
				Set(nmode, "_lastGame", 0u);
				Set(nmode, "_switchingTo", 0u);
				int today2 = 0;

				for (int s = 0; (s < 30) && (HGet<int>(nmode, "_playedMinutesToday") < 360); s++) {
					Set(nmode, "_phase", NocatFarm.Modules.HumanMode.Phase.Off);
					HCall(nmode, "StartSession");
					uint game = HGet<uint>(nmode, "_game");
					int m = (int) Math.Round((HGet<DateTime>(nmode, "_sessionEnds") - HGet<DateTime>(nmode, "_sessionStarted")).TotalMinutes);
					Set(nmode, "_playedMinutesToday", HGet<int>(nmode, "_playedMinutesToday") + m);
					all++;
					allMin += m;

					if (game is 999 or 998) {
						today2++;
						newMin += m;
					}
				}

				onNew += today2;
				maxDay = Math.Max(maxDay, today2);
			}

			NocatFarm.Log.Suppressed = false;

			return (onNew, days, maxDay, newMin / Math.Max(1, allMin));
		}

		var fresh1 = NewDays(MakeTrials((999, 0.2)), 400, 1);
		Check("new games: a brand-new game gets real sittings, at most 3 a day", (fresh1.Sittings > 400) && (fresh1.MaxDay <= 3), $"{fresh1.Sittings} sittings over {fresh1.Days} days, most {fresh1.MaxDay} in a day, {fresh1.Share:P0} of the time");
		var later = NewDays(MakeTrials((999, 4.2)), 400, 1);
		Check("new games: fewer as the days go by", later.Sittings < fresh1.Sittings * 0.6, $"day 5: {later.Sittings}, day 1: {fresh1.Sittings}");
		Check("new games: a family-shared game is never 'new here'", NewDays(MakeTrials((998, 0.2)), 200, 2).Sittings == 0);
		nbot.Cfg.BlacklistedGames = [999];
		Check("new games: a blacklisted one is left alone", NewDays(MakeTrials((999, 0.2)), 200, 3).Sittings == 0);
		nbot.Cfg.BlacklistedGames = [];
		nbot.Cfg.SkipRefundableGames = true;
		typeof(NocatFarm.Core.RefundGuard).GetField("_held", Inst)!.SetValue(nbot.Refunds, new HashSet<uint> { 999 });
		Check("new games: one still refundable is left alone", NewDays(MakeTrials((999, 0.2)), 200, 4).Sittings == 0);
		typeof(NocatFarm.Core.RefundGuard).GetField("_held", Inst)!.SetValue(nbot.Refunds, new HashSet<uint>());
		nbot.Cfg.SkipRefundableGames = false;
		BotProp(other, "PlayingApps", (IReadOnlyList<uint>) [999u]);
		Check("new games: not while another of your accounts is playing it", NewDays(MakeTrials((999, 0.2)), 200, 5).Sittings == 0);
		BotProp(other, "PlayingApps", (IReadOnlyList<uint>) []);
		nbot.Cfg.GameWeights = "730:70, 440:20, 999:10";
		Check("new games: one already in 'Games and how often' just has its share", NewDays(MakeTrials((999, 0.2)), 200, 6).Sittings < 200 * 0.8);
		nbot.Cfg.GameWeights = "730:70, 440:30";
		Check("new games: 'human' names the game being tried", nmode.ExtrasReport().Any(static l => l.Contains("trying Brand New (day 1 of 6)")), string.Join(" | ", nmode.ExtrasReport()));
		nbot.Cfg.NewGamesFirst = false;
		Check("new games: switched off, none", NewDays(MakeTrials((999, 0.2)), 200, 7).Sittings == 0);

		// ── joining a friend ──
		var pick = hh.GetMethod("PickFriendGame", Stat)!;
		List<(ulong Id, string Name, uint App)> fl = [(1, "Bob", 620), (2, "Me2", 570), (3, "Idle", 0), (4, "Cara", 777)];
		HashSet<ulong> ours = [2];
		Func<uint, bool> owns = static app => app is 620 or 570;
		var picks = Enumerable.Range(0, 300).Select(i => ((string Name, uint App)) pick.Invoke(null, [fl, ours, owns, new Random(i)])!).ToList();
		Check("friends: only a friend in a game it may play - never your own accounts, never a game it doesn't own",
			picks.All(static p => (p.Name == "Bob") && (p.App == 620)));
		Check("friends: nobody to join is nobody", ((string Name, uint App)) pick.Invoke(null, [fl, new HashSet<ulong> { 1, 2 }, owns, new Random(1)])! == default((string, uint)));

		nbot.Cfg.JoinFriends = true;
		List<(ulong Id, string Name, uint App)> feed = [];
		ht.GetProperty("FriendsFeed", Inst)!.SetValue(nmode, (Func<List<(ulong Id, string Name, uint App)>>) (() => feed));
		(int Joins, int MaxDay, HashSet<uint> Games, HashSet<string> Names) Joined(int days, int seed) {
			Set(nmode, "_trials", MakeTrials());
			Set(nmode, "_rng", new Random(seed));
			int joins = 0, maxDay = 0;
			HashSet<uint> games = [];
			HashSet<string> names = [];
			NocatFarm.Log.Suppressed = true;

			for (int day = 0; day < days; day++) {
				Set(nmode, "_dayStamp", -1);
				Set(nmode, "_friendJoins", 0);
				Set(nmode, "_newGameSittings", 0);
				Set(nmode, "_otherPlayed", 0);
				Set(nmode, "_otherBudget", 330);
				Set(nmode, "_targetMinutes", 360);
				Set(nmode, "_playedMinutesToday", 0);
				Set(nmode, "_mainSharePct", 70);
				int todayJoins = 0;

				for (int s = 0; s < 8; s++) {
					uint g = (uint) HCall(nmode, "PickGame")!;

					if (HGet<int>(nmode, "_friendJoins") > todayJoins) {
						todayJoins++;
						games.Add(g);
						names.Add(HGet<string>(nmode, "_joinedToday"));
					}
				}

				joins += todayJoins;
				maxDay = Math.Max(maxDay, todayJoins);
			}

			NocatFarm.Log.Suppressed = false;

			return (joins, maxDay, games, names);
		}

		feed = [(76561190000000009UL, "Bob", 620)];
		var bob = Joined(300, 1);
		Check("friends: a friend in a game it owns is joined now and then, at most twice a day", (bob.Joins > 100) && (bob.MaxDay <= 2) && bob.Games.SetEquals([620u]),
			$"{bob.Joins} joins over 300 days, most {bob.MaxDay} in a day");
		Check("friends: 'human' says who it joined and in what", nmode.ExtrasReport().Any(static l => l.Contains("joined Bob in")) && bob.Names.All(static n => n.StartsWith("Bob in", StringComparison.Ordinal)));
		feed = [(76561190000000002UL, "Your other account", 620)];
		Check("friends: your own other account is never a friend to join", Joined(200, 2).Joins == 0);
		feed = [(76561190000000009UL, "Bob", 730)];
		Check("friends: a friend on the main game doesn't count as a join (it has its share)", Joined(200, 3).Joins == 0);
		feed = [(76561190000000009UL, "Bob", 12345)];
		Check("friends: not a game this account doesn't own", Joined(200, 4).Joins == 0);
		feed = [(76561190000000009UL, "Bob", 998)];
		Check("friends: a family game only while nobody in the family is on it", Joined(50, 5).Joins > 0);
		typeof(NocatFarm.Core.Library).GetField("_familyBusy", Inst)!.SetValue(lib, new HashSet<uint> { 998 });
		Check("friends: ...and not while somebody is", Joined(200, 6).Joins == 0);
		feed = [(76561190000000009UL, "Bob", 550)];
		BotProp(other, "PlayingApps", (IReadOnlyList<uint>) [550u]);
		Check("friends: not a game one of your accounts is running", Joined(200, 7).Joins == 0);
		BotProp(other, "PlayingApps", (IReadOnlyList<uint>) []);
		nbot.Cfg.BlacklistedGames = [550];
		Check("friends: not a blacklisted game", Joined(200, 8).Joins == 0);
		nbot.Cfg.BlacklistedGames = [];
		nbot.Cfg.SkipRefundableGames = true;
		typeof(NocatFarm.Core.RefundGuard).GetField("_held", Inst)!.SetValue(nbot.Refunds, new HashSet<uint> { 550 });
		Check("friends: not a refundable game", Joined(200, 9).Joins == 0);
		nbot.Cfg.SkipRefundableGames = false;
		nbot.Cfg.JoinFriends = false;
		feed = [(76561190000000009UL, "Bob", 620)];
		Check("friends: switched off, never", Joined(200, 10).Joins == 0);
		ht.GetProperty("FriendsFeed", Inst)!.SetValue(nmode, null);

		// ── the commands ──
		Check("commands: 'habits' is a command", NocatFarm.Commands.Resolve("habits")?.Name == "habits");
		nbot.Cfg.LearnFromOwner = 1;
		nbot.Cfg.LongerRhythms = true;
		nbot.Cfg.JoinFriends = true;
		nbot.Cfg.NewGamesFirst = true;
		Set(nmode, "_habits", habits);
		Set(nmode, "_dayStamp", DateTime.Now.DayOfYear);
		string humanOut = await Commands.RunAsync(mgr, $"human {nbot.Name}");
		Check("human: says what each of the four is doing today when they're on",
			humanOut.Contains("learning from you   in use (a little)") && humanOut.Contains("new games") && humanOut.Contains("rhythm") && humanOut.Contains("friends"), humanOut);
		nbot.Cfg.GameWeights = "730:70, 620:30";
		string habitsOut = await Commands.RunAsync(mgr, $"habits {nbot.Name}");
		Check("habits: days seen, usual hours, top games, and games you play that aren't in the list",
			habitsOut.Contains("days seen     8") && habitsOut.Contains("19:00-21:00") && habitsOut.Contains("top games     CS2 90%") && habitsOut.Contains("in use")
			&& habitsOut.Contains("not in \"Games and how often\"") && habitsOut.Contains("its day): TF2"), habitsOut);
		nbot.Cfg.GameWeights = "730:70, 440:30";
		string forgetOut = await Commands.RunAsync(mgr, $"habits {nbot.Name} forget");
		Check("habits forget: wiped", forgetOut.Contains("forgot everything") && (await Commands.RunAsync(mgr, $"habits {nbot.Name}")).Contains("nothing seen yet"), forgetOut);
		nbot.Cfg.LearnFromOwner = 0;
		nbot.Cfg.LongerRhythms = false;
		nbot.Cfg.JoinFriends = false;
		nbot.Cfg.NewGamesFirst = false;
		string plainOut = await Commands.RunAsync(mgr, $"human {nbot.Name}");
		Check("human: with them off, none of that is printed", !plainOut.Contains("learning from you") && !plainOut.Contains("rhythm ") && !plainOut.Contains("friends   "), plainOut);

		NocatFarm.Modules.HumanDay.Forget(nbot.Name);
		await nbot.DisposeAsync();
		await other.DisposeAsync();
		await offBot.DisposeAsync();
	} finally {
		NocatFarm.Config.Live.Global = liveBefore;
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
	}
}

// ── stuck-account alarm: says so and restarts once when it should be banking - and never on a healthy account ──
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
	Type sw = typeof(NocatFarm.Core.StuckWatch);
	Dictionary<string, double> bankedMins = new(StringComparer.OrdinalIgnoreCase);
	List<string> restarts = [];
	PropertyInfo bankedProp = sw.GetProperty("BankedMinutes", S)!;
	PropertyInfo restartProp = sw.GetProperty("Restart", S)!;
	object oldBanked = bankedProp.GetValue(null)!, oldRestart = restartProp.GetValue(null)!;
	bankedProp.SetValue(null, (Func<NocatFarm.Core.Bot, double>) (b => bankedMins.GetValueOrDefault(b.Name)));
	restartProp.SetValue(null, (Func<NocatFarm.Core.Bot, Task>) (b => { restarts.Add(b.Name); return Task.CompletedTask; }));
	MethodInfo step = sw.GetMethod("Step", S)!;
	void Reset() { sw.GetMethod("Reset", S)!.Invoke(null, null); restarts.Clear(); bankedMins.Clear(); }
	var g = new NocatFarm.Config.GlobalConfig { StuckAlarm = true, StuckAlarmHours = 3 };

	NocatFarm.Core.Bot MakeBot(string name, NocatFarm.Core.BotState state, NocatFarm.Config.BotConfig? cfg = null) {
		var b = new NocatFarm.Core.Bot(name, cfg ?? new NocatFarm.Config.BotConfig { IdleGames = [730], CustomGameNameEnabled = false });
		typeof(NocatFarm.Core.Bot).GetProperty("State")!.SetValue(b, state);
		return b;
	}
	void Set(NocatFarm.Core.Bot b, string prop, object? v) => typeof(NocatFarm.Core.Bot).GetProperty(prop)!.SetValue(b, v);
	int AlarmsOf(string name) {
		var watches = (System.Collections.IDictionary) sw.GetField("Watches", S)!.GetValue(null)!;
		object? w = watches[name];
		return w == null ? -1 : (int) w.GetType().GetProperty("Alarms")!.GetValue(w)!;
	}
	// Minute by minute, the way the timer does it; "tick" can change things along the way.
	DateTime Run(IEnumerable<NocatFarm.Core.Bot> bots, DateTime from, int minutes, Action<int>? tick = null) {
		for (int i = 0; i < minutes; i++) {
			tick?.Invoke(i);
			step.Invoke(null, [bots, g, from.AddMinutes(i)]);
		}
		return from.AddMinutes(minutes);
	}

	DateTime thu = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);   // a Thursday, well clear of Steam's maintenance
	var stuck = MakeBot("sw-stuck", NocatFarm.Core.BotState.Reconnecting);

	try {
		// Healthy robot: online, games on, for two whole days.
		Reset();
		var healthy = MakeBot("sw-healthy", NocatFarm.Core.BotState.Online);
		Set(healthy, "PlayingApps", new List<uint> { 730 });
		Run([healthy], thu, 48 * 60);
		Check("stuck alarm: a robot playing for two days never trips it", restarts.Count == 0 && AlarmsOf("sw-healthy") == 0);

		// Banking seen only through the lifetime total (between re-asserts nothing shows as playing) still counts.
		Reset();
		var quiet = MakeBot("sw-banking", NocatFarm.Core.BotState.Online);
		Run([quiet], thu, 24 * 60, i => bankedMins["sw-banking"] = i);
		Check("stuck alarm: minutes rising in the lifetime total count as banking", restarts.Count == 0 && AlarmsOf("sw-banking") == 0);

		// Stuck reconnecting: nothing before the threshold, one restart at it, a second word after another threshold, no loop.
		Reset();
		Run([stuck], thu, 179);
		Check("stuck alarm: nothing said before 3h", restarts.Count == 0 && AlarmsOf("sw-stuck") == 0);
		DateTime t = Run([stuck], thu.AddMinutes(179), 2);
		Check("stuck alarm: at 3h it's said and restarted once", restarts.SequenceEqual(["sw-stuck"]) && AlarmsOf("sw-stuck") == 1);
		t = Run([stuck], t, 3 * 60);
		Check("stuck alarm: still stuck 3h later - said again, not restarted again", restarts.Count == 1 && AlarmsOf("sw-stuck") == 2);
		t = Run([stuck], t, 12 * 60);
		Check("stuck alarm: then quiet - no restart loop over the next 12h", restarts.Count == 1 && AlarmsOf("sw-stuck") == 2);

		// It recovers, then gets stuck again inside 12h of the restart: said, but not restarted.
		Reset();
		t = Run([stuck], thu, 181);
		bankedMins["sw-stuck"] = 5;
		t = Run([stuck], t, 1);
		Check("stuck alarm: banking again clears it", AlarmsOf("sw-stuck") == 0);
		t = Run([stuck], t, 181);
		Check("stuck alarm: stuck again within 12h of a restart - said, not restarted", restarts.Count == 1 && AlarmsOf("sw-stuck") == 1);
		t = Run([stuck], t, 12 * 60, _ => bankedMins["sw-stuck"] += 1);
		t = Run([stuck], t, 181);
		Check("stuck alarm: 12h later a fresh stuck spell may restart again", restarts.Count == 2);

		// Every "never" case, each held for a whole day.
		void Never(string what, NocatFarm.Core.Bot b, DateTime from, int minutes = 24 * 60) {
			Reset();
			Run([b], from, minutes);
			Check($"stuck alarm never fires: {what}", restarts.Count == 0 && AlarmsOf(b.Name) <= 0, $"restarts {restarts.Count}, alarms {AlarmsOf(b.Name)}");
		}

		Never("disabled account", MakeBot("sw-off", NocatFarm.Core.BotState.Reconnecting, new NocatFarm.Config.BotConfig { Enabled = false, IdleGames = [730] }), thu);
		Never("account you stopped", MakeBot("sw-stopped", NocatFarm.Core.BotState.Stopped), thu);
		var paused = MakeBot("sw-paused", NocatFarm.Core.BotState.Online);
		Set(paused, "Paused", true);
		Never("paused", paused, thu);
		var blocked = MakeBot("sw-blocked", NocatFarm.Core.BotState.Online);
		Set(blocked, "PlayingBlocked", true);
		Never("you playing on it", blocked, thu);
		var finishing = MakeBot("sw-finishing", NocatFarm.Core.BotState.Online);
		Set(finishing, "Stopping", true);
		Never("finishing up", finishing, thu);
		Never("robot with nothing to play", MakeBot("sw-nothing", NocatFarm.Core.BotState.Online, new NocatFarm.Config.BotConfig { CustomGameNameEnabled = false }), thu);
		g.StuckAlarm = false;
		Never("the alarm switched off", MakeBot("sw-disabledalarm", NocatFarm.Core.BotState.Reconnecting), thu);
		g.StuckAlarm = true;

		// Steam's weekly maintenance (Tuesday 21:00 UTC to Wednesday 02:00): five hours down, never counted.
		DateTime tue = new(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc);
		Never("Steam's weekly maintenance", MakeBot("sw-maint", NocatFarm.Core.BotState.Reconnecting), tue, 5 * 60);
		Reset();
		var maint = MakeBot("sw-maint2", NocatFarm.Core.BotState.Reconnecting);
		t = Run([maint], tue, 5 * 60 + 179);
		Check("stuck alarm: after maintenance the clock starts again from zero", restarts.Count == 0);
		Run([maint], t, 2);
		Check("stuck alarm: ...and a full threshold after it, it fires", restarts.Count == 1);

		// The PC asleep: 2h50m stuck, then a 40-minute gap - that's the machine, not the account.
		Reset();
		var sleeper = MakeBot("sw-sleep", NocatFarm.Core.BotState.Reconnecting);
		t = Run([sleeper], thu, 170);
		t = Run([sleeper], t.AddMinutes(40), 170);
		Check("stuck alarm: the PC waking from sleep starts every clock again", restarts.Count == 0 && AlarmsOf("sw-sleep") == 0);
		Run([sleeper], t, 20);
		Check("stuck alarm: ...counting from the wake", restarts.Count == 1);

		// A Steam Guard code it's waiting on: said, but never restarted over the question.
		Reset();
		Run([MakeBot("sw-guard", NocatFarm.Core.BotState.NeedsGuard)], thu, 4 * 60);
		Check("stuck alarm: waiting for a code - said, not restarted", restarts.Count == 0 && AlarmsOf("sw-guard") == 1);

		// Human mode: resting by its own plan never counts; a day it should be playing does.
		NocatFarm.Core.Bot Human(string name, NocatFarm.Core.BotState state, NocatFarm.Modules.HumanMode.Phase phase) {
			var b = MakeBot(name, state, new NocatFarm.Config.BotConfig { LegitMode = true, GameWeights = "730:100", CustomGameNameEnabled = false });
			var hm = new NocatFarm.Modules.HumanMode(b);
			b.AddModule(hm);
			typeof(NocatFarm.Modules.HumanMode).GetField("_ticked", I)!.SetValue(hm, true);
			typeof(NocatFarm.Modules.HumanMode).GetField("_phase", I)!.SetValue(hm, phase);
			return b;
		}

		Never("human mode asleep", Human("sw-asleep", NocatFarm.Core.BotState.Online, NocatFarm.Modules.HumanMode.Phase.Asleep), thu);
		Never("human mode done for today", Human("sw-done", NocatFarm.Core.BotState.Online, NocatFarm.Modules.HumanMode.Phase.DoneForToday), thu);
		Never("human mode day off", Human("sw-dayoff", NocatFarm.Core.BotState.Online, NocatFarm.Modules.HumanMode.Phase.DayOff), thu);
		Never("human mode standing down for you", Human("sw-stood", NocatFarm.Core.BotState.Online, NocatFarm.Modules.HumanMode.Phase.StoodDown), thu);
		Never("human mode asleep and disconnected for a night", Human("sw-asleepdown", NocatFarm.Core.BotState.Reconnecting, NocatFarm.Modules.HumanMode.Phase.Asleep), thu, 11 * 60);

		Reset();
		Run([Human("sw-stale", NocatFarm.Core.BotState.Reconnecting, NocatFarm.Modules.HumanMode.Phase.Asleep)], thu, 12 * 60 + 3 * 60 + 2);
		Check("stuck alarm: asleep but down for 12h+ - the old phase isn't trusted, and it fires", restarts.Count == 1);

		Reset();
		Run([Human("sw-dayshift", NocatFarm.Core.BotState.Online, NocatFarm.Modules.HumanMode.Phase.Playing)], thu, 3 * 60 + 2);
		Check("stuck alarm: human mode meant to be playing, nothing running for 3h - fires", restarts.Count == 1);

		Reset();
		Run([Human("sw-nightidle", NocatFarm.Core.BotState.Reconnecting, NocatFarm.Modules.HumanMode.Phase.NightIdle)], thu, 3 * 60 + 2);
		Check("stuck alarm: human mode banking the night, disconnected 3h - fires", restarts.Count == 1);

		// The whole fleet at once: only the stuck one is touched.
		Reset();
		var fine = MakeBot("sw-fine", NocatFarm.Core.BotState.Online);
		Set(fine, "PlayingApps", new List<uint> { 440 });
		Run([fine, stuck, MakeBot("sw-stopped2", NocatFarm.Core.BotState.Stopped)], thu, 3 * 60 + 2);
		Check("stuck alarm: in a fleet only the stuck account is restarted", restarts.SequenceEqual(["sw-stuck"]));

		var text = (string) typeof(NocatFarm.Core.StuckWatch).GetMethod("Text")!.Invoke(null, [new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false })])!;
		Check("stuck alarm: 'stuck' answers with the alarm's state", text.StartsWith("Stuck-account alarm: on", StringComparison.Ordinal), text);
		Check("stuck alarm: on by default, 3h", new NocatFarm.Config.GlobalConfig().StuckAlarm && new NocatFarm.Config.GlobalConfig().StuckAlarmHours == 3);
	} finally {
		bankedProp.SetValue(null, oldBanked);
		restartProp.SetValue(null, oldRestart);
		Reset();
	}
}

// ── weekly report: off by default, once a week on the day, this week next to last ────────────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type wr = typeof(NocatFarm.Core.WeeklyReport);
	DateTime LastDue(DateTime now, int day, int hour) => (DateTime) wr.GetMethod("LastDue", S)!.Invoke(null, [now, day, hour])!;
	DateTime? Due(DateTime now, int day, int hour, string last) => (DateTime?) wr.GetMethod("Due", S)!.Invoke(null, [now, day, hour, last]);

	Check("weekly report: off by default", !new NocatFarm.Config.GlobalConfig().WeeklyReport);
	DateTime wed = new(2026, 9, 30, 15, 0, 0);   // a Wednesday
	Check("weekly report: the last Monday 10:00 before a Wednesday", LastDue(wed, 1, 10) == new DateTime(2026, 9, 28, 10, 0, 0));
	Check("weekly report: on the day but before the hour, it's last week's", LastDue(new DateTime(2026, 9, 28, 9, 0, 0), 1, 10) == new DateTime(2026, 9, 21, 10, 0, 0));
	Check("weekly report: switched on mid-week, nothing is sent at once", Due(wed, 1, 10, "") == null);
	Check("weekly report: switched on on the day after the hour, it goes", Due(new DateTime(2026, 9, 28, 11, 0, 0), 1, 10, "") == new DateTime(2026, 9, 28));
	Check("weekly report: sent this week - not again", Due(wed, 1, 10, "2026-09-28") == null);
	Check("weekly report: last sent a week ago - due", Due(new DateTime(2026, 10, 5, 10, 0, 0), 1, 10, "2026-09-28") == new DateTime(2026, 10, 5));
	Check("weekly report: the PC off on the day - it goes when it's back", Due(new DateTime(2026, 10, 7, 8, 0, 0), 1, 10, "2026-09-28") == new DateTime(2026, 10, 5));

	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-weekly-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config", "state", "history"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		// Two weeks of history for one account: 2h a day this week, 1h a day last week, 3 cards and a value going 10 -> 12.50.
		DateTime today = DateTime.Now.Date;
		int cur = NocatFarm.PriceBook.CurrencyId;
		Dictionary<string, Dictionary<string, Dictionary<string, object>>> months = [];
		void Day(DateTime d, double minutes, int cards, decimal? value) {
			string m = d.ToString("yyyy-MM"), k = d.ToString("yyyy-MM-dd");
			if (!months.TryGetValue(m, out var days)) months[m] = days = [];
			days[k] = new() { ["wk-a"] = new Dictionary<string, object?> { ["Minutes"] = minutes, ["Cards"] = cards, ["Value"] = value, ["Currency"] = cur } };
		}
		for (int i = 1; i <= 7; i++) Day(today.AddDays(-i), 120, i == 3 ? 3 : 0, i == 1 ? 12.5m : null);
		for (int i = 8; i <= 14; i++) Day(today.AddDays(-i), 60, 0, i == 8 ? 10m : null);
		foreach (var (m, days) in months) {
			File.WriteAllText(Path.Combine(tmpRoot, "config", "state", "history", m + ".json"), System.Text.Json.JsonSerializer.Serialize(days));
		}
		typeof(NocatFarm.Core.History).GetMethod("Reload", S)!.Invoke(null, null);

		var bot = new NocatFarm.Core.Bot("wk-a", new NocatFarm.Config.BotConfig());
		var built = wr.GetMethod("Build", S)!.Invoke(null, [new[] { bot }, false, today])!;
		var lines = (List<NocatFarm.Core.Said>) built.GetType().GetField("Item1")!.GetValue(built)!;
		string row = lines[0].ToEnglish(), fleet = lines[^1].ToEnglish();
		Check("weekly report: this week's hours from the history", row.Contains("14h00m", StringComparison.Ordinal), row);
		Check("weekly report: next to last week's", row.Contains("(last week 7h00m)", StringComparison.Ordinal), row);
		Check("weekly report: cards dropped this week", row.Contains("3 card(s)", StringComparison.Ordinal), row);
		Check("weekly report: the inventory's change over the week", row.Contains("value +" + NocatFarm.PriceBook.Symbol + "2.50", StringComparison.Ordinal), row);
		Check("weekly report: a fleet line last", fleet.StartsWith("  fleet: 14h00m banked (last week 7h00m)", StringComparison.Ordinal), fleet);
		Check("weekly report: no rep4rep, no comments column", !row.Contains("comment", StringComparison.Ordinal));

		// A week earlier, the history starts inside "this week": nothing before it is a dash, not "0m" (his first
		// report read "last week 0m" for accounts that had run all week - hours were only recorded from 1.5.5).
		built = wr.GetMethod("Build", S)!.Invoke(null, [new[] { bot }, false, today.AddDays(-7)])!;
		lines = (List<NocatFarm.Core.Said>) built.GetType().GetField("Item1")!.GetValue(built)!;
		Check("weekly report: no hours on record before the week -> last week is a dash",
			lines[0].ToEnglish().Contains("7h00m    banked (last week —)", StringComparison.Ordinal) && lines[^1].ToEnglish().Contains("(last week —)", StringComparison.Ordinal), lines[0].ToEnglish());

		NocatFarm.Stats.Record(NocatFarm.Stats.KindListed, "wk-a");
		built = wr.GetMethod("Build", S)!.Invoke(null, [new[] { bot }, true, today.AddDays(1)])!;
		lines = (List<NocatFarm.Core.Said>) built.GetType().GetField("Item1")!.GetValue(built)!;
		Check("weekly report: cards listed for sale are counted", lines[0].ToEnglish().Contains("1 listed", StringComparison.Ordinal), lines[0].ToEnglish());
		Check("weekly report: rep4rep on adds the comments", lines[0].ToEnglish().Contains("comment(s)", StringComparison.Ordinal));
		Check("weekly report: nothing known about the value - a dash, not a made-up change",
			(string) wr.GetMethod("Money", S)!.Invoke(null, [null])! == "—");
		await bot.DisposeAsync();
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		typeof(NocatFarm.Core.History).GetMethod("Reload", S)!.Invoke(null, null);

		try {
			Directory.Delete(tmpRoot, true);
		} catch {
			// a temp folder
		}
	}
}

// ── backup & restore: every file round-trips, anything else is refused ──────────────────────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type bk = typeof(NocatFarm.Core.Backup);
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig realGlobal = NocatFarm.Config.Live.Global;
	string rootA = Path.Combine(Path.GetTempPath(), "nf-backup-a-" + Guid.NewGuid().ToString("N"));
	string rootB = Path.Combine(Path.GetTempPath(), "nf-backup-b-" + Guid.NewGuid().ToString("N"));
	string rootC = Path.Combine(Path.GetTempPath(), "nf-backup-c-" + Guid.NewGuid().ToString("N"));
	string? Kind(string rel) => (string?) bk.GetMethod("Kind", S)!.Invoke(null, [rel]);
	int WriteFiles(byte[] zip) => (int) bk.GetMethod("WriteFiles", S)!.Invoke(null, [zip])!;

	try {
		NocatFarm.Config.ConfigStore.UseRoot(rootA);
		string cfg = NocatFarm.Config.ConfigStore.ConfigDir;
		void Put(string rel, string text) { string p = Path.Combine(cfg, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }

		NocatFarm.Config.ConfigStore.SaveGlobal(new NocatFarm.Config.GlobalConfig { WebEnabled = false, WebPassword = "backup-test-pass", StatusEveryMinutes = 7 });
		NocatFarm.Config.ConfigStore.SaveBot("alt", new NocatFarm.Config.BotConfig { Enabled = false, SteamLogin = "altlogin", SteamPassword = "pw-alt", IdleGames = [730] });
		string[] ours = [
			"tokens/alt.token", "tokens/alt.access", "authenticators/alt.maFile", "state/secret.key", "state/history/2026-09.json",
			"state/lifetime.json", "state/lifetime-games.json", "state/human-alt.json", "state/hunt-alt.json", "state/rep4rep-alt.json",
			"state/keys.json", "state/report.json", "state/weekly-report.json"];
		foreach (string rel in ours) {
			Put(rel, rel switch {
				"state/lifetime.json" => """{"alt": 1234}""",
				"state/lifetime-games.json" => """{"alt": 5678}""",
				"state/history/2026-09.json" => """{"2026-09-01":{"alt":{"Minutes":60,"Cards":2}}}""",
				"state/keys.json" => "[]",
				_ => "{\"x\":\"" + rel + "\"}"
			});
		}
		string[] notOurs = ["state/prices.json", "state/gamecatalog.json", ".appnames.json", "state/web-sessions.json", "nocatFarm.json.broken",
			"alt.json.1234.tmp", "state/import-pending.json", "plugins/x.json", "state/cheevo-alt.json"];
		foreach (string rel in notOurs) {
			Put(rel, "{}");
		}
		Directory.CreateDirectory(Path.Combine(rootA, "logs"));
		File.WriteAllText(Path.Combine(rootA, "logs", "nocatFarm-2026-09-30.log"), "a log line");

		byte[] zip = NocatFarm.Core.Backup.Create();
		NocatFarm.Core.Backup.Inspection look = NocatFarm.Core.Backup.Inspect(zip);
		string[] expected = [.. ours, "nocatFarm.json", "alt.json"];
		Check("backup: a fresh one checks out", look.Ok, look.Error ?? "");
		Check("backup: holds every file it should", expected.All(look.Files.Contains) && look.Files.Count == expected.Length, string.Join(",", look.Files));
		Check("backup: nothing it shouldn't - caches, logs, sessions, temp files", !look.Files.Any(f => notOurs.Contains(f)));
		Check("backup: knows the account and says it was made here", look.Accounts.SequenceEqual(["alt"]) && look.SameMachine);
		using (var za = new System.IO.Compression.ZipArchive(new MemoryStream(zip))) {
			Check("backup: has a manifest and a README", za.GetEntry("manifest.json") != null && za.GetEntry("README.txt") != null);
			using var sr = new StreamReader(za.GetEntry("README.txt")!.Open());
			Check("backup: the README says how to restore", sr.ReadToEnd().Contains("How to restore", StringComparison.Ordinal));
		}
		Check("backup: named by the day", NocatFarm.Core.Backup.FileName(new DateTime(2026, 9, 30)) == "nocat.farm-backup-2026-09-30.zip");

		// Restored into an empty config folder, every file comes back byte for byte.
		NocatFarm.Config.ConfigStore.UseRoot(rootB);
		int wrote = WriteFiles(zip);
		string cfgB = NocatFarm.Config.ConfigStore.ConfigDir;
		bool same = expected.All(rel => File.Exists(Path.Combine(cfgB, rel)) && File.ReadAllBytes(Path.Combine(cfgB, rel)).SequenceEqual(File.ReadAllBytes(Path.Combine(cfg, rel))));
		Check("restore: every file round-trips byte for byte", same && wrote == expected.Length, $"wrote {wrote}");
		Check("restore: nothing else appears", !notOurs.Any(rel => File.Exists(Path.Combine(cfgB, rel))) && !Directory.Exists(Path.Combine(rootB, "logs")));
		var bots = NocatFarm.Config.ConfigStore.LoadBots();
		Check("restore: the account and its secrets read back (same Windows user)", bots.TryGetValue("alt", out var alt) && alt.SteamPassword == "pw-alt" && alt.IdleGames.SequenceEqual([730u]));
		Check("restore: the settings read back, dashboard password included", NocatFarm.Config.ConfigStore.LoadGlobal() is { StatusEveryMinutes: 7, WebPassword: "backup-test-pass" });

		// The whole restore, in the running app: a manager on an empty folder picks up the account and the totals.
		NocatFarm.Config.ConfigStore.UseRoot(rootC);
		var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false });
		string said = await NocatFarm.Core.Backup.RestoreAsync(mgr, zip);
		Check("restore: in the running app - says what it did", said.StartsWith("Restored 15 file(s)", StringComparison.Ordinal), said);
		Check("restore: the account is there, as a fresh object from the file", mgr.Get("alt") is { } restored && restored.Cfg.SteamLogin == "altlogin" && !restored.Cfg.Enabled);
		Check("restore: the lifetime totals in memory are the restored ones", NocatFarm.Core.Lifetime.For("alt") == 1234 && NocatFarm.Core.Lifetime.GamesFor("alt") == 5678);
		Check("restore: the settings in use are the restored ones", mgr.Global.StatusEveryMinutes == 7);
		Check("restore: what was there before is kept as a backup first",
			Directory.GetFiles(Path.Combine(rootC, "backups"), "*-before-restore.zip").Length == 1);
		await mgr.DisposeAsync();

		// Secrets that can't be opened here keep the ones in use.
		var restoredCfg = new NocatFarm.Config.GlobalConfig { WebPassword = "" };
		bool kept = (bool) bk.GetMethod("KeepSecrets", S)!.Invoke(null, [restoredCfg, new NocatFarm.Config.GlobalConfig { WebPassword = "still-mine" }])!;
		Check("restore: a dashboard password that won't open here keeps the current one", kept && restoredCfg.WebPassword == "still-mine");

		// ── zips it must refuse, and write nothing from ──
		byte[] Zip(params (string Name, byte[] Data)[] entries) {
			using MemoryStream ms = new();
			using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) {
				foreach (var (name, data) in entries) {
					using Stream s = z.CreateEntry(name).Open();
					s.Write(data);
				}
			}
			return ms.ToArray();
		}
		byte[] manifest = System.Text.Encoding.UTF8.GetBytes("""{"App":"nocat.farm","Format":1,"Version":"1.0.0","Created":"2026-09-30T00:00:00Z","Machine":"x","Windows":true,"Accounts":[],"Files":1}""");
		byte[] small = System.Text.Encoding.UTF8.GetBytes("{}");
		string rootD = Path.Combine(Path.GetTempPath(), "nf-backup-d-" + Guid.NewGuid().ToString("N"));
		NocatFarm.Config.ConfigStore.UseRoot(rootD);

		void Refused(string what, byte[] bad) {
			var r = NocatFarm.Core.Backup.Inspect(bad);
			bool threw = false;
			try { WriteFiles(bad); } catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { threw = true; }
			bool untouched = Directory.GetFileSystemEntries(NocatFarm.Config.ConfigStore.ConfigDir, "*", SearchOption.AllDirectories).Length == 0
				&& !File.Exists(Path.Combine(rootD, "evil.json")) && !File.Exists(Path.Combine(Path.GetTempPath(), "evil.json"));
			Check($"restore refuses: {what}", !r.Ok && threw && untouched, r.Error ?? "accepted!");
		}

		Refused("zip-slip with ../", Zip(("manifest.json", manifest), ("config/../../evil.json", small)));
		Refused("a path starting at the top", Zip(("manifest.json", manifest), ("../evil.json", small)));
		Refused("an absolute path", Zip(("manifest.json", manifest), ("/etc/evil.json", small)));
		Refused("a drive letter", Zip(("manifest.json", manifest), ("C:/evil.json", small)));
		Refused("backslashes", Zip(("manifest.json", manifest), ("config\\..\\..\\evil.json", small)));
		Refused("an unknown file", Zip(("manifest.json", manifest), ("config/state/prices.json", small)));
		Refused("a file outside config/", Zip(("manifest.json", manifest), ("run.bat", small)));
		Refused("a Windows device name as an account", Zip(("manifest.json", manifest), ("config/con.json", small)));
		Refused("a token for a name that walks out", Zip(("manifest.json", manifest), ("config/tokens/..token", small)));
		Refused("an oversized file (a zip bomb)", Zip(("manifest.json", manifest), ("config/alt.json", new byte[21 * 1024 * 1024])));
		Refused("too much in all", Zip([("manifest.json", manifest), .. Enumerable.Range(0, 6).Select(i => ($"config/a{i}.json", new byte[19 * 1024 * 1024]))]));
		Refused("the same file twice", Zip(("manifest.json", manifest), ("config/alt.json", small), ("config/alt.json", small)));
		Refused("no manifest", Zip(("config/alt.json", small)));
		Refused("somebody else's manifest", Zip(("manifest.json", System.Text.Encoding.UTF8.GetBytes("""{"App":"asf","Format":1}""")), ("config/alt.json", small)));
		Refused("a newer backup format", Zip(("manifest.json", System.Text.Encoding.UTF8.GetBytes("""{"App":"nocat.farm","Format":99}""")), ("config/alt.json", small)));
		Refused("not a zip at all", System.Text.Encoding.UTF8.GetBytes("this is not a zip"));

		Check("backup allow-list: history months only", Kind("state/history/2026-09.json") == "history" && Kind("state/history/notes.json") == null);
		Check("backup allow-list: a dot-file is no account", Kind(".appnames.json") == null && Kind("nocatFarm.json") == "settings");

		try {
			Directory.Delete(rootD, true);
		} catch {
			// a temp folder
		}
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		NocatFarm.Config.Live.Global = realGlobal;
		typeof(NocatFarm.Core.Lifetime).GetMethod("Reload", S)!.Invoke(null, null);
		typeof(NocatFarm.Core.History).GetMethod("Reload", S)!.Invoke(null, null);
		typeof(NocatFarm.Core.Secrets).GetMethod("ForgetKey", S)!.Invoke(null, null);

		foreach (string r in new[] { rootA, rootB, rootC }) {
			try {
				Directory.Delete(r, true);
			} catch {
				// a temp folder
			}
		}
	}
}

// ── old configs load unchanged: no new setting changes what a config written before them does ───────────────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-oldcfg-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		File.WriteAllText(NocatFarm.Config.ConfigStore.GlobalPath, """{ "WebEnabled": false, "DailyReportHour": 8, "SendDailySummary": true }""");
		var g = NocatFarm.Config.ConfigStore.LoadGlobal();
		Check("old config: loads with the weekly report off and the alarm at its default", !g.WeeklyReport && g.StuckAlarm && g.StuckAlarmHours == 3 && g.DailyReportHour == 8);
		NocatFarm.Config.GlobalConfig live = NocatFarm.Config.Live.Global;
		NocatFarm.Config.Live.Global = g;
		bool quiet = !NocatFarm.Core.Notifier.Wanted(NocatFarm.Topic.Weekly);
		NocatFarm.Config.Live.Global = new NocatFarm.Config.GlobalConfig { WeeklyReport = true };
		bool sent = NocatFarm.Core.Notifier.Wanted(NocatFarm.Topic.Weekly);
		NocatFarm.Config.Live.Global = live;
		Check("old config: nothing weekly is sent unless the weekly report is switched on", quiet && sent);
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch {
			// a temp folder
		}
	}
}

// ── help with only the start of a name: every command and setting that begins with it ────────────────────────────
{
	string HelpOf(params string[] args) => (string) typeof(NocatFarm.Commands).GetMethod("Help", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [args])!;
	string rot = HelpOf("rot");
	Check("help rot: finds the rotation command and the Rotate settings", rot.Contains("rotation") && rot.Contains("RotateIdleGames") && rot.Contains("'help <name>' explains one"), rot);
	string one = HelpOf("habi");
	Check("help habi: one command on its own is explained in full", one.StartsWith("habits [account]", StringComparison.Ordinal) && !one.Contains("starting with"), one);
	string slash = HelpOf("/rot");
	Check("help /rot: a leading slash (Telegram habit) is fine", slash.Contains("rotation"), slash);
	string nothing = HelpOf("zzqx");
	Check("help zzqx: nothing starts with it, and it says so", nothing.StartsWith("Nothing starts with 'zzqx'", StringComparison.Ordinal), nothing);
	string whole = HelpOf("rotation");
	Check("help rotation: a full command name still explains just that command", whole.StartsWith("rotation", StringComparison.Ordinal) && !whole.Contains("Settings starting with"), whole);
	string label = HelpOf("libr");
	Check("help libr: finds settings by a word of their label too", label.Contains("Idle my whole library"), label);
}

// ── a settings file from 1.5.9 still loads: the removed "PC is off" alert left 3 lines behind ──────────────────────
{
	var json = (System.Text.Json.JsonSerializerOptions) typeof(NocatFarm.Config.ConfigStore).GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
	var old = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Config.GlobalConfig>(
		"{\"AutoUpdate\": 1, \"StopAlert\": false, \"StopAlertPcName\": \"my PC\", \"StopAlertMinutes\": 20, \"WeeklyReport\": true}", json)!;
	Check("1.5.9 settings file: the old alert lines are ignored and everything else is read", old.AutoUpdate == 1 && old.WeeklyReport);
	Check("1.5.9 settings file: they're gone on the next save", !System.Text.Json.JsonSerializer.Serialize(old, json).Contains("StopAlert", StringComparison.Ordinal));
	Check("the alert is gone: no 'alert' command and no StopAlert setting",
		!NocatFarm.Commands.All.Any(static c => c.Matches("alert")) && NocatFarm.Config.Settings.FindGlobal("StopAlert") == null);
}

// ── "Open from anywhere" off: the internet is turned away on every request, home and VPNs are not ────────────────────
{
	bool Inet(string a) => NocatFarm.Core.RemoteAccess.FromTheInternet(System.Net.IPAddress.Parse(a));
	Check("internet check: a phone on mobile data is from the internet", Inet("134.41.250.88") && Inet("8.8.8.8") && Inet("2001:4860:4860::8888"));
	Check("internet check: this PC, home Wi-Fi and link-local are not",
		!Inet("127.0.0.1") && !Inet("::1") && !Inet("192.168.2.40") && !Inet("10.0.0.5") && !Inet("172.20.1.1") && !Inet("169.254.3.3") && !Inet("fe80::1") && !Inet("fd12:3456::1"));
	Check("internet check: Tailscale / VPN addresses (100.64.0.0/10) are not", !Inet("100.101.102.103") && Inet("100.128.0.1"));
	Check("internet check: an IPv4 address written as IPv6 is read as IPv4", !Inet("::ffff:192.168.2.40") && Inet("::ffff:134.41.250.88"));
	Check("internet check: no address at all (an in-process host) is not", !NocatFarm.Core.RemoteAccess.FromTheInternet(null));
	string app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "wwwroot", "app.js"));
	Check("phone page: 'Open to other devices' is a label, so clicking the switch reaches it",
		app.Contains("const openFix = `<label class=\"switch\"", StringComparison.Ordinal) && !app.Contains("<span class=\"switch\" ${lockedOn", StringComparison.Ordinal));
}

// ── the window's command box: up and down through what was typed, like a terminal ─────────────────────────────────
{
	var h = new NocatFarm.Windows.CommandHistory();
	Check("history: nothing typed yet - up does nothing", h.Step(true, "") == null && h.Step(false, "") == null);
	h.Add("status");
	h.Add("cards");
	h.Add("cards");   // twice in a row is kept once
	h.Add("version");
	Check("history: up gives the newest, then older", h.Step(true, "half-typed") == "version" && h.Step(true, "") == "cards" && h.Step(true, "") == "status");
	Check("history: up at the oldest stays there", h.Step(true, "") == null);
	Check("history: down comes forward again", h.Step(false, "") == "cards" && h.Step(false, "") == "version");
	Check("history: down past the newest gives back what was being typed", h.Step(false, "") == "half-typed" && h.Step(false, "") == null);
	h.Add("stuck");
	Check("history: after a new command, up starts from it", h.Step(true, "") == "stuck");
	for (int i = 0; i < 150; i++) {
		h.Add("c" + i);
	}

	int n = 0;
	h.Reset();

	while (h.Step(true, "") != null) {
		n++;
	}

	Check("history: keeps the last 100", n == NocatFarm.Windows.CommandHistory.Keep, n.ToString());
	string mw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Windows", "MainWindow.cs"));
	Check("history: an answer to a question (a password, a Steam Guard code) or a masked line is never remembered",
		mw.Contains("Prompt.Question? question = Prompt.Current;", StringComparison.Ordinal)
		&& mw.Contains("if ((question == null) && (Commands.ForLog(line) == line)) {", StringComparison.Ordinal));
}

// ── who has been at the dashboard ──────────────────────────────────────────────────────────────────────────────────
{
	Check("visitors: an iPhone on Safari", NocatFarm.Core.Visitors.Device("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1") == "iPhone · Safari");
	Check("visitors: Android Chrome, Windows Edge, a Mac on Firefox",
		NocatFarm.Core.Visitors.Device("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36") == "Android · Chrome"
		&& NocatFarm.Core.Visitors.Device("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 Edg/129.0") == "Windows · Edge"
		&& NocatFarm.Core.Visitors.Device("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.5; rv:130.0) Gecko/20100101 Firefox/130.0") == "Mac · Firefox");
	Check("visitors: no browser name at all", NocatFarm.Core.Visitors.Device(null) == "unknown" && NocatFarm.Core.Visitors.Device("curl/8.4.0") == "curl/8.4.0");
	Check("visitors: where - this PC, home, the internet",
		NocatFarm.Core.Visitors.WhereFrom("127.0.0.1", true) == "this PC" && NocatFarm.Core.Visitors.WhereFrom("192.168.2.40", false) == "home"
		&& NocatFarm.Core.Visitors.WhereFrom("134.41.250.88", false) == "internet" && NocatFarm.Core.Visitors.WhereFrom("not an address", false) == "internet");

	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-visitors-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	var published = new List<string>();
	Action<NocatFarm.Topic, string, string> onPub = (topic, src, text) => { if (topic is NocatFarm.Topic.Security or NocatFarm.Topic.BreakIn) published.Add(text); };
	NocatFarm.Log.Published += onPub;

	try {
		NocatFarm.Core.Visitors.Reload();
		Check("visitors: nothing yet says so", NocatFarm.Core.Visitors.Describe().StartsWith("Nobody", StringComparison.Ordinal));
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.SignedIn, "192.168.2.40", false, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Safari/604.1");
		Check("visitors: a sign-in at home is written down but not sent anywhere", (NocatFarm.Core.Visitors.Recent(5).Count == 1) && (published.Count == 0));
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.SignedIn, "134.41.250.88", false, "Mozilla/5.0 (Linux; Android 14) Chrome/129.0 Mobile Safari/537.36");
		Check("visitors: a sign-in from outside the home goes to Telegram and Discord, with the address and the device",
			(published.Count == 1) && published[0].Contains("134.41.250.88", StringComparison.Ordinal) && published[0].Contains("Android · Chrome", StringComparison.Ordinal)
			&& published[0].Contains("Send /visitors signout here", StringComparison.Ordinal), string.Join(" | ", published));
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.LockedOut, "8.8.8.8", false, null, 5, 60);
		Check("visitors: a lockout is sent too", (published.Count == 2) && published[1].Contains("locked out for 60 minutes", StringComparison.Ordinal), string.Join(" | ", published));
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.LockedOut, "127.0.0.1", true, null, 5, 1);
		Check("visitors: you mistyping on this PC is not", published.Count == 2);
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.Paused, "9.9.9.9", false, null, 10, 60);
		Check("visitors: the internet brake is sent", (published.Count == 3) && published[2].Contains("paused for 60 minutes", StringComparison.Ordinal));
		int before = NocatFarm.Core.Visitors.Recent(500).Count;
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.TurnedAway, "1.2.3.4", false, null);
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.TurnedAway, "1.2.3.4", false, null);
		NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.TurnedAway, "5.6.7.8", false, null);
		Check("visitors: the same address turned away over and over is written down once (per 10 minutes)", NocatFarm.Core.Visitors.Recent(500).Count == before + 2);
		List<NocatFarm.Core.Visitors.Visit> recent = NocatFarm.Core.Visitors.Recent(3);
		Check("visitors: newest first", (recent[0].Ip == "5.6.7.8") && (recent[1].Ip == "1.2.3.4"));
		NocatFarm.Core.Visitors.Reload();
		Check("visitors: kept on disk across a restart", NocatFarm.Core.Visitors.Recent(500).Count == before + 2);
		for (int i = 0; i < 250; i++) {
			NocatFarm.Core.Visitors.Note(NocatFarm.Core.Visitors.What.WrongPassword, "10.0.0." + (i % 250), false, null);
		}

		Check("visitors: only the last 200 are kept", NocatFarm.Core.Visitors.Recent(1000).Count == NocatFarm.Core.Visitors.Keep);
		string said = NocatFarm.Core.Visitors.Describe(3);
		Check("visitors: the command lists them and says how to sign everything out", said.Contains("wrong password", StringComparison.Ordinal) && said.Contains("visitors signout", StringComparison.Ordinal), said);
	} finally {
		NocatFarm.Log.Published -= onPub;
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		NocatFarm.Core.Visitors.Reload();

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// best effort
		}
	}

	Check("visitors: 'Send dashboard sign-ins' is on by default", new NocatFarm.Config.GlobalConfig().SendSignIns);
	Check("visitors: 'visitors' is a command, 'who' too", NocatFarm.Commands.All.Any(static c => c.Matches("visitors")) && NocatFarm.Commands.All.Any(static c => c.Matches("who")));
}

// ── a code on Telegram for signing in from outside ─────────────────────────────────────────────────────────────────
{
	Check("sign-in code: on by default", new NocatFarm.Config.GlobalConfig().WebSignInCode);
	var def = NocatFarm.Config.Settings.FindGlobal("WebSignInCode");
	Check("sign-in code: a setting in the Dashboard section, not hidden behind Show advanced", (def != null) && !def.Advanced && def.Section == NocatFarm.Config.Settings.FindGlobal("WebRemoteAccess")!.Section);
	Check("sign-in code: nothing to send it to without Telegram (then the password is enough, as before)",
		!NocatFarm.Core.Notifier.CanSendPrivately || (NocatFarm.Config.Live.Global.TelegramBotToken.Length > 0));
	string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Web", "WebHost.cs"));
	Check("sign-in code: only asked from the internet, and only when Telegram can take it",
		host.Contains("private bool NeedsCode(bool internet) => internet && _cfg.WebSignInCode && Notifier.CanSendPrivately;", StringComparison.Ordinal));
	Check("sign-in code: compared in constant time, bound to the address it was sent for, five tries",
		host.Contains("CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed.PadRight(6))", StringComparison.Ordinal)
		&& host.Contains("(pending.Ip != ip)", StringComparison.Ordinal) && host.Contains("private const int CodeTries = 5;", StringComparison.Ordinal));
	Check("sign-in code: a wrong code counts towards the same lockout and brake as a wrong password",
		host.Contains("Visitors.What.WrongCode);", StringComparison.Ordinal) && host.Contains("private void CountFailure(", StringComparison.Ordinal));
}

// ── who is asking: a proxy's forwarded address, read so nobody outside can pass for home ────────────────────────
{
	var whoFn = typeof(NocatFarm.Web.WebHost).GetMethod("WhoIsSigningIn", BindingFlags.NonPublic | BindingFlags.Static)!;
	(string Ip, bool ThisPc) Ask(string from, params (string Name, string Value)[] headers) {
		var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
		ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(from);
		foreach ((string n, string v) in headers) {
			ctx.Request.Headers[n] = v;
		}

		return ((string, bool)) whoFn.Invoke(null, [ctx])!;
	}

	Check("forwarded: a direct visitor is who they are", Ask("134.41.250.88") == ("134.41.250.88", false) && Ask("192.168.2.40") == ("192.168.2.40", false));
	Check("forwarded: this PC with no proxy is this PC", Ask("127.0.0.1") == ("127.0.0.1", true));
	Check("forwarded: the last X-Forwarded-For entry, not the first (the visitor writes the first)",
		Ask("127.0.0.1", ("X-Forwarded-For", "192.168.1.5, 134.41.250.88")) == ("134.41.250.88", false));
	Check("forwarded: a made-up home address in X-Real-IP can't hide an internet one the proxy added",
		Ask("127.0.0.1", ("X-Forwarded-For", "134.41.250.88"), ("X-Real-IP", "192.168.1.5")).Ip == "134.41.250.88");
	Check("forwarded: RFC 7239 Forwarded: for= is read, port and brackets and all",
		Ask("127.0.0.1", ("Forwarded", "for=\"[2001:db8:cafe::17]:4711\";proto=https")).Ip == "2001:db8:cafe::17"
		&& Ask("127.0.0.1", ("X-Real-IP", "203.0.113.9:5555")).Ip == "203.0.113.9");
	Check("forwarded: made-up text is one shared 'unknown' - no fresh key per guess, no words of the visitor's in the log",
		Ask("127.0.0.1", ("X-Forwarded-For", "<b>hi</b>")) == ("unknown", false) && Ask("127.0.0.1", ("X-Real-IP", "obfuscated_7f3a")).Ip == "unknown");
	Check("visitors: 'unknown' is logged as the internet, like the checks treat it", NocatFarm.Core.Visitors.WhereFrom("unknown", false) == "internet");
	Check("forwarded: an address that can't be read counts as the internet, never home",
		NocatFarm.Web.WebHost.IsInternet("unknown") && NocatFarm.Web.WebHost.IsInternet("") && !NocatFarm.Web.WebHost.IsInternet("10.1.2.3"));
	Check("visitors: IPv6 turned-away visitors are grouped by /64",
		NocatFarm.Core.Visitors.Neighbourhood("2001:db8:1:2::aaaa") == NocatFarm.Core.Visitors.Neighbourhood("2001:db8:1:2:ffff::1")
		&& NocatFarm.Core.Visitors.Neighbourhood("2001:db8:1:3::1") != NocatFarm.Core.Visitors.Neighbourhood("2001:db8:1:2::1")
		&& NocatFarm.Core.Visitors.Neighbourhood("134.41.250.88") == "134.41.250.88");
}

// ── Steam-chat masters can't see the dashboard's visitors or take a backup ───────────────────────────────────────────
{
	Check("steam chat: visitors, who, backup, report and stuck are refused",
		new[] { "visitors", "backup", "report", "stuck" }.All(NocatFarm.Commands.SteamChatRefuses));
	string cmds = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Commands.cs"));
	Check("steam chat: refused by the command an alias reaches ('who' is 'visitors')", cmds.Contains("if (Resolve(verb)?.Name is { } name && SteamChatRefuses(name)) {", StringComparison.Ordinal));
}

// ── backups only at home; break-in alerts; switching itself off ─────────────────────────────────────────────────────
{
	string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Web", "WebHost.cs"));
	Check("backup: the download and both restore steps refuse the internet",
		System.Text.RegularExpressions.Regex.Matches(host, @"if \(FromOutside\(ctx\)\) \{\s+return HomeOnly\(\);").Count == 3);
	Check("break-ins: their own switch, on by default, and lockouts use it",
		new NocatFarm.Config.GlobalConfig().SendBreakIns && (NocatFarm.Config.Settings.FindGlobal("SendBreakIns") != null)
		&& NocatFarm.Core.Notifier.Wanted(NocatFarm.Topic.BreakIn) == NocatFarm.Config.Live.Global.SendBreakIns);
	var off = NocatFarm.Config.Settings.FindGlobal("WebRemoteOffAfter");
	Check("switching itself off: 5 wrong passwords or codes from the internet by default, 0 = never",
		(new NocatFarm.Config.GlobalConfig().WebRemoteOffAfter == 5) && (off != null) && (off.Min == 0));
	Check("switching itself off: it shuts a Public address set by hand too, until Open from anywhere is on again",
		host.Contains("if ((InternetShut() || (!_cfg.WebRemoteAccess", StringComparison.Ordinal) && host.Contains("if (_internetShut && _cfg.WebRemoteAccess) {", StringComparison.Ordinal));
	string upd = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "UpdateCheck.cs"));
	Check("switching itself off: the setting goes off before the flag goes up, both under one lock",
		host.IndexOf("_cfg.WebRemoteAccess = false;", host.IndexOf("private void ShutToTheInternet(", StringComparison.Ordinal), StringComparison.Ordinal)
			< host.IndexOf("_internetShut = true;", host.IndexOf("private void ShutToTheInternet(", StringComparison.Ordinal), StringComparison.Ordinal)
		&& host.Contains("lock (_shutGate) {", StringComparison.Ordinal));
	Check("sign-in code: a try is spent (swapped in) before the code is compared, and only the request that takes the record signs in",
		host.Contains("if (!_pendingCodes.TryUpdate(body.Challenge!, spent, pending)) {", StringComparison.Ordinal)
		&& host.Contains("if (!_pendingCodes.TryRemove(new KeyValuePair<string, PendingCode>(body.Challenge!, spent))) {", StringComparison.Ordinal));
	Check("wrong guesses: counted atomically", host.Contains("_failures.AddOrUpdate(ip,", StringComparison.Ordinal));
	Check("updates: an account waiting for a Steam Guard code doesn't hold them for ever", upd.Contains("(b.State is BotState.Failed or BotState.NeedsGuard)", StringComparison.Ordinal));
}

// ── what the owner's log said wrong: idle count, trade names, doubled calls, licence spam, stale echoes, waits ─────
{
	string Src(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", rel)).Replace("\r\n", "\n");
	const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

	// "no cards left - idling 💀name💀 (+8)" on an account really idling 31.
	MethodInfo idleSummary = typeof(NocatFarm.Modules.CardFarmer).GetMethod("IdleSummary", Any)!;
	string IdleSays(string? name, int playing) => ((NocatFarm.Core.Said) idleSummary.Invoke(null, [name, playing])!).ToEnglish();
	Check("no cards left: the custom name with what's really playing", IdleSays("💀nocat.lol💀", 31) == "💀nocat.lol💀 (+31)", IdleSays("💀nocat.lol💀", 31));
	Check("no cards left: no name - the count playing; nothing - 'your games'", IdleSays(null, 31) == "31 game(s)" && IdleSays("", 0) == "your games" && IdleSays("x", 0) == "x");
	string farmer = Src(Path.Combine("Modules", "CardFarmer.cs"));
	int assertAt = farmer.IndexOf("BotManager.ModuleOf<Idler>(Bot)?.Assert();", StringComparison.Ordinal);
	int saidAt = farmer.IndexOf("new Said(\"no cards left - idling {0}{1}\"", StringComparison.Ordinal);
	Check("no cards left: said after the idler has put its games on, from what is playing",
		(assertAt > 0) && (saidAt > assertAt) && farmer.Contains("int idling = Bot.CanPlay ? Bot.PlayingApps.Count : Bot.Cfg.IdleGames.Count;", StringComparison.Ordinal));

	// Trades: the name is read before Finish forgets it.
	string trading = Src(Path.Combine("Modules", "Trading.cs"));
	Check("trades: 'accepted offer N from' uses the name read before Finish",
		trading.Contains("string who = Who(offer);\n\t\t\t\tFinish(offer.Id);", StringComparison.Ordinal)
		&& trading.Contains("NumberOf(offer.Id), who, offer.Receiving.Sum(", StringComparison.Ordinal)
		&& trading.Contains("Log.Trade(NeedsConfirming(offer, result, who), Bot.Name);", StringComparison.Ordinal));
	Check("trades: 'confirm it...' never reads Who after Finish",
		!trading.Contains("Finish(offer.Id);\n\t\t\t\t\t\tlines.Add(NeedsConfirming(", StringComparison.Ordinal)
		&& trading.Contains("Said confirm = NeedsConfirming(offer, result, Who(offer));", StringComparison.Ordinal));

	// Library: two modules asking at sign-in share one read.
	var lbot = new NocatFarm.Core.Bot("harness-lib-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig());
	NocatFarm.Core.Library lib = lbot.Library;
	FieldInfo refreshing = typeof(NocatFarm.Core.Library).GetField("_refreshing", Any)!;
	var inFlight = new TaskCompletionSource<bool>();
	refreshing.SetValue(lib, inFlight.Task);
	Task<bool> first = lib.RefreshIfStaleAsync(TimeSpan.FromHours(6), CancellationToken.None);
	Task<bool> second = lib.RefreshAsync(CancellationToken.None);
	Check("library: a second caller waits for the read already going, doesn't start its own",
		!first.IsCompleted && !second.IsCompleted && ReferenceEquals(refreshing.GetValue(lib), inFlight.Task));
	inFlight.SetResult(true);
	Check("library: both get that read's answer", first.Result && second.Result);
	var stopped = new TaskCompletionSource<bool>();
	refreshing.SetValue(lib, stopped.Task);
	Task<bool> joined = lib.RefreshAsync(CancellationToken.None);
	stopped.SetCanceled();
	Check("library: the first caller stopping is 'not read' for the one waiting, not a crash", !joined.Result);
	Check("library: with no read going (and signed out) it reads itself - and says no", !lib.RefreshAsync(CancellationToken.None).Result);
	await lbot.DisposeAsync();

	// Licences: each recent one once; the account's own copy wins over a family member's.
	MethodInfo newLic = typeof(NocatFarm.Core.Bot).GetMethod("NewLicences", Any)!;
	List<uint> NewLic(uint[] before, uint[] now) => (List<uint>) newLic.Invoke(null, [new HashSet<uint>(before), now])!;
	Check("licences: first push - every recent one", NewLic([], [1, 2, 3]).SequenceEqual([1u, 2u, 3u]));
	Check("licences: the same list again - nothing", NewLic([1, 2, 3], [1, 2, 3]).Count == 0);
	Check("licences: one added - only that one", NewLic([1, 2, 3], [1, 2, 3, 4]).SequenceEqual([4u]));
	MethodInfo replaces = typeof(NocatFarm.Core.Bot).GetMethod("Replaces", Any)!;
	bool Replaces(bool hadOwn, bool own) => (bool) replaces.Invoke(null, [hadOwn, own])!;
	Check("licences: a family copy never replaces the account's own", !Replaces(true, false));
	Check("licences: the account's own replaces a family copy; same kind, the later one", Replaces(false, true) && Replaces(true, true) && Replaces(false, false));
	string botSrc = Src(Path.Combine("Core", "Bot.cs"));
	Check("licences: the count and the recent list are only written when they change",
		botSrc.Contains("if (cb.LicenseList.Count != _licenceCountLogged) {", StringComparison.Ordinal)
		&& botSrc.Contains("recent.Where(l => fresh.Contains(l.PackageID))", StringComparison.Ordinal));

	// Gifts: the two pushes for one gift are one look.
	MethodInfo settle = typeof(NocatFarm.Modules.Gifts).GetMethod("SettleAsync", Any)!;
	var poke = new SemaphoreSlim(0);
	poke.Release();   // the gift count
	Task settling = (Task) settle.Invoke(null, [poke, TimeSpan.FromMilliseconds(300), CancellationToken.None])!;
	poke.Release();   // the guest-pass list, a moment later
	await settling;
	Check("gifts: a second wake-up inside the settle is the same look", poke.CurrentCount == 0);

	// Persona: the first echo after signing in is the last session's, not a fight.
	var pbot = new NocatFarm.Core.Bot("harness-persona-" + Guid.NewGuid().ToString("N")[..6], new NocatFarm.Config.BotConfig { OnlineStatus = 7 });
	void PSet(string name, object? value) => typeof(NocatFarm.Core.Bot).GetProperty(name)!.SetValue(pbot, value);
	const ulong me = 76561198000000001UL;
	PSet("State", NocatFarm.Core.BotState.Online);
	PSet("SteamId", me);

	void Put(object o, string name, object? value) {
		PropertyInfo? p = o.GetType().GetProperty(name, Any);

		if (p?.GetSetMethod(true) is { } set) {
			set.Invoke(o, [value]);
		} else {
			o.GetType().GetField($"<{name}>k__BackingField", Any)!.SetValue(o, value);
		}
	}

	object Echo(SteamKit2.EPersonaState state) {
		object cb = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamKit2.SteamFriends.PersonaStateCallback));
		Put(cb, "FriendID", new SteamKit2.SteamID(me));
		Put(cb, "State", state);
		Put(cb, "GameID", new SteamKit2.GameID(0));

		return cb;
	}

	MethodInfo onPersona = typeof(NocatFarm.Core.Bot).GetMethod("OnPersonaState", Any)!;
	onPersona.Invoke(pbot, [Echo(SteamKit2.EPersonaState.Online)]);
	Check("persona: the stale first echo after sign-in isn't a fight", !pbot.PersonaContested);
	onPersona.Invoke(pbot, [Echo(SteamKit2.EPersonaState.Online)]);
	Check("persona: still not ours on the next echo - that is one", pbot.PersonaContested);
	typeof(NocatFarm.Core.Bot).GetMethod("ForgetConnection", Any)!.Invoke(pbot, []);
	onPersona.Invoke(pbot, [Echo(SteamKit2.EPersonaState.Online)]);
	Check("persona: a new sign-in's first echo is stale again", !pbot.PersonaContested);
	await pbot.DisposeAsync();

	// The status says the real wait after you stop playing.
	MethodInfo picking = typeof(NocatFarm.Core.BotStatus).GetMethod("PickingBackUp", Any)!;

	string Picking(double minutes) {
		DateTime now = DateTime.UtcNow;

		return ((NocatFarm.Core.Said) picking.Invoke(null, [now.AddMinutes(minutes), now])!).ToEnglish();
	}

	Check("status: 'picking back up in 14m' while 14 minutes are left", Picking(13.5) == "picking back up in 14m", Picking(13.5));
	Check("status: 'in a moment' only for the last minute", Picking(0.5) == "picking back up in a moment" && Picking(-1) == "picking back up in a moment");
	Check("status: reads the Bot's own resume time", Src(Path.Combine("Core", "BotStatus.cs")).Contains("PickingBackUp(bot.ResumesAt, DateTime.UtcNow)", StringComparison.Ordinal));

	// "other session" only when it is another session.
	MethodInfo stateLine = typeof(NocatFarm.Core.Bot).GetMethod("SessionStateLine", Any)!;
	string StateLine(bool blocked, uint app) => ((NocatFarm.Core.Said) stateLine.Invoke(null, [blocked, app])!).ToEnglish();
	Check("session report: our own game list isn't 'another session'",
		!StateLine(false, 730).Contains("other", StringComparison.Ordinal) && StateLine(false, 730).Contains("this session is playing app 730", StringComparison.Ordinal));
	Check("session report: blocked is another session; nothing is nothing",
		StateLine(true, 730).Contains("another session is playing app 730", StringComparison.Ordinal) && (StateLine(false, 0) == "Steam says: not blocked - nothing playing"));

	// Rep4rep sleeps through the account's night instead of looking every few minutes.
	MethodInfo dayWait = typeof(NocatFarm.Modules.Rep4RepModule).GetMethod("DayWaitSeconds", Any)!;
	int DayWait(NocatFarm.Modules.HumanMode.Phase phase, DateTime? wakes, DateTime now) => (int) dayWait.Invoke(null, [phase, wakes, now, 300, 120])!;
	DateTime night = new(2026, 9, 30, 1, 0, 0, DateTimeKind.Utc);
	Check("rep4rep: asleep - until it gets up, and a little more", DayWait(NocatFarm.Modules.HumanMode.Phase.Asleep, night.AddHours(8), night) == (8 * 3600) + 120);
	Check("rep4rep: overnight idling is asleep too", DayWait(NocatFarm.Modules.HumanMode.Phase.NightIdle, night.AddHours(2), night) == (2 * 3600) + 120);
	Check("rep4rep: never past twelve hours", DayWait(NocatFarm.Modules.HumanMode.Phase.Asleep, night.AddHours(20), night) == 12 * 3600);
	Check("rep4rep: awake, or no wake time - the usual few minutes",
		(DayWait(NocatFarm.Modules.HumanMode.Phase.WarmingUp, night.AddHours(8), night) == 300) && (DayWait(NocatFarm.Modules.HumanMode.Phase.Asleep, null, night) == 300)
		&& (DayWait(NocatFarm.Modules.HumanMode.Phase.Asleep, night.AddMinutes(-1), night) == 300));
	Check("rep4rep: 'wake' cuts the night's wait short", Src(Path.Combine("Modules", "HumanMode.cs")).Contains("BotManager.ModuleOf<Rep4RepModule>(Bot)?.DayMoved();", StringComparison.Ordinal));

	// The market: doubling to four hours, then twice a day, said once.
	MethodInfo cool = typeof(NocatFarm.PriceBook).GetMethod("NextCoolMinutes", Any)!;
	int Cool(int now) => (int) cool.Invoke(null, [now])!;
	Check("market: 15, 30 ... up to 240 minutes", (Cool(0) == 15) && (Cool(15) == 30) && (Cool(120) == 240) && (Cool(200) == 240));
	Check("market: refused after four hours' rest - twice a day from then on", (Cool(240) == 720) && (Cool(720) == 720));
	string book = Src(Path.Combine("Core", "PriceBook.cs"));
	Check("market: the refusal is said once, plainly, and the pause line isn't repeated with it",
		book.Contains("if (refused && !_saidRefused) {", StringComparison.Ordinal) && book.Contains("} else if (!refused) {", StringComparison.Ordinal)
		&& book.Contains("_saidRefused = _coolMinutes >= RefusedMinutes;", StringComparison.Ordinal));
}

// ── dashboard, idler, backup and stuck-alarm fixes ────────────────────────────────────────────────────────────────────
{
	string web = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string app = File.ReadAllText(Path.Combine(web, "wwwroot", "app.js"));
	string indexPage = File.ReadAllText(Path.Combine(web, "wwwroot", "index.html"));
	string host = File.ReadAllText(Path.Combine(web, "Web", "WebHost.cs"));
	string idlerSrc = File.ReadAllText(Path.Combine(web, "Modules", "Idler.cs"));
	// The body of one function in app.js, up to the next top-level function - enough to check what it does.
	string Fn(string head) {
		int at = app.IndexOf(head, StringComparison.Ordinal);
		if (at < 0) {
			return "";
		}

		int end = app.IndexOf("\nfunction ", at + head.Length, StringComparison.Ordinal);
		int endAsync = app.IndexOf("\nasync function ", at + head.Length, StringComparison.Ordinal);
		end = (end < 0) ? endAsync : (endAsync < 0) ? end : Math.Min(end, endAsync);
		return end < 0 ? app[at..] : app[at..end];
	}

	// 1. The plugin switch shows the saved choice, not what was loaded at start-up.
	Check("plugins: the switch reads the saved choice", host.Contains("Enabled = !Live.Global.DisabledPlugins.Contains(p.Name, StringComparer.OrdinalIgnoreCase)", StringComparison.Ordinal)
		&& !host.Contains("p.Name, p.Version, p.File, p.Enabled,", StringComparison.Ordinal));

	// 2. A settings switch redraws the form (Human mode brings in the human settings), keeping what was typed elsewhere.
	string editBool = Fn("function editBool(");
	Check("settings: an on/off switch redraws the form", editBool.Contains("editAndRender(name, el.checked);", StringComparison.Ordinal)
		&& !editBool.Contains("    edit(name, el.checked);", StringComparison.Ordinal));
	Check("settings: the redraw keeps other unsaved edits (it draws from pending)", Fn("function editAndRender(").Contains("pending[name] = value;", StringComparison.Ordinal)
		&& Fn("function liveValue(").Contains("pending[name] !== undefined ? pending[name] : values[name]", StringComparison.Ordinal));

	// 3. Settings on an account added since the indexPage read its settings opens that account, not Global.
	string openSet = Fn("async function openBotSettings(");
	Check("settings: an account's Settings button reads the settings again when that account is missing",
		openSet.Contains("await loadConfig()", StringComparison.Ordinal) && openSet.Contains("selectSettings(name);", StringComparison.Ordinal)
		&& app.Contains("case 'settings': closeModal(); openBotSettings(name); break;", StringComparison.Ordinal));
	Check("settings: adding an account (dialog or welcome) reads the settings again",
		Fn("async function createBot(").Contains("loadConfig()", StringComparison.Ordinal) && Fn("async function createFirstBot(").Contains("loadConfig()", StringComparison.Ordinal));

	// 4. The inventory Value button says what the server said.
	string inv = Fn("async function refreshInventory(");
	Check("inventory: Value shows the error instead of always 'reading'", inv.Contains("res.ok", StringComparison.Ordinal) && inv.Contains("res.error", StringComparison.Ordinal));

	// 5. A plugin's on/off setting stored as "True" shows as on.
	Check("plugins: a bool setting is read without caring about case", app.Contains("String(sett.Value).toLowerCase() === 'true'", StringComparison.Ordinal)
		&& !app.Contains("sett.Value === 'true'", StringComparison.Ordinal));

	// 6. Tab hotkeys: not with a modifier held, not behind a dialog or the walkthrough.
	int hk = app.IndexOf("// 1-9 are the tabs as they're shown", StringComparison.Ordinal);
	string hotkeys = hk < 0 ? "" : app[Math.Max(0, hk - 900)..hk];
	Check("hotkeys: ignored with Ctrl/Alt/Cmd, behind a dialog, the walkthrough or the sign-in screen",
		hotkeys.Contains("e.ctrlKey || e.altKey || e.metaKey", StringComparison.Ordinal) && hotkeys.Contains("tutorialOpen || !$('modal').classList.contains('hidden')", StringComparison.Ordinal));

	// 7. Everything clickable that isn't a button or a link with an address can be reached with Tab and pressed with Enter/Space.
	List<string> unreachable = [];
	foreach ((string file, string text) in new[] { ("app.js", app), ("index.html", indexPage) }) {
		foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"<(?:span|div|a|b|li|td|i|label)\b[^<>]*?onclick=")) {
			if (!m.Value.Contains("role=\"button\"", StringComparison.Ordinal) && !m.Value.Contains("href=", StringComparison.Ordinal)) {
				unreachable.Add(file + ": " + m.Value[..Math.Min(60, m.Value.Length)]);
			}
		}
	}

	Check("keyboard: no clickable span/div/link without role=button and tabindex", unreachable.Count == 0, string.Join(" | ", unreachable.Take(5)));
	Check("keyboard: the walkthrough links, account-type cards, auth picker and jump links are reachable",
		indexPage.Contains("role=\"button\" tabindex=\"0\" onclick=\"go('phone')\"", StringComparison.Ordinal) && indexPage.Contains("role=\"button\" tabindex=\"0\" onclick=\"startTutorial()\"", StringComparison.Ordinal)
		&& indexPage.Contains("id=\"w-type-human\" role=\"button\" tabindex=\"0\"", StringComparison.Ordinal) && app.Contains("id=\"a-type-robot\" role=\"button\" tabindex=\"0\"", StringComparison.Ordinal)
		&& app.Contains("data-auth-pick=\"${esc(a.Name)}\" role=\"button\" tabindex=\"0\"", StringComparison.Ordinal) && app.Contains("<a class=\"j\" role=\"button\" tabindex=\"0\" data-jump=", StringComparison.Ordinal));
	Check("keyboard: Enter and Space press anything with role=button (walkthrough cards included)",
		app.Contains("((e.key !== 'Enter') && (e.key !== ' ')) || e.isComposing || e.defaultPrevented", StringComparison.Ordinal)
		&& app.Contains("(el.getAttribute('role') !== 'button') || (el.tagName === 'BUTTON')) return;", StringComparison.Ordinal));

	// 8. rep4rep "Post now" says what the account can really do.
	string postNow = Fn("async function postNow(");
	Check("rep4rep: Post now reads the account's row and says off / signed out / at its cap",
		postNow.Contains("/api/rep4rep/profiles", StringComparison.Ordinal) && postNow.Contains("!p.Enabled", StringComparison.Ordinal)
		&& postNow.Contains("!p.Online", StringComparison.Ordinal) && postNow.Contains("p.Today >= p.Cap", StringComparison.Ordinal)
		&& !postNow.Contains("will post as soon as the daily cap allows", StringComparison.Ordinal));

	// 9. Clear, then Undo, leaves nothing to save.
	Check("secrets: Undo after Clear takes the change away", Fn("function undoClearSecret(").Contains("delete pending[name];", StringComparison.Ordinal)
		&& app.Contains("onclick=\"undoClearSecret('${def.Name}')\"", StringComparison.Ordinal) && !app.Contains("onclick=\"editAndRender('${def.Name}','')\">${esc(t('Undo'))}", StringComparison.Ordinal));

	// 10. A code that ran out is fetched once, not once a second until the answer lands.
	string codes = Fn("async function loadAuthCodes(");
	Check("authenticator: one code request at a time", app.Contains("let authCodesLoading = false;", StringComparison.Ordinal)
		&& codes.Contains("if (authCodesLoading) return;", StringComparison.Ordinal) && codes.Contains("finally { authCodesLoading = false; }", StringComparison.Ordinal));

	// 11. Searching finds the Notifications and Discord chips.
	string render = Fn("function renderSettings(");
	Check("settings search: a chip setting's panel shows when the search matches it", render.Contains("PANEL_ROWS.has(d.Name) && found(d) && (!onlyChanged || isChanged(d, values, defaults))", StringComparison.Ordinal)
		&& render.Contains("!fields.length && !panelHit &&", StringComparison.Ordinal));

	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig realGlobal = NocatFarm.Config.Live.Global;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-uifix-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config", "state"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	NocatFarm.Config.Live.Global = new NocatFarm.Config.GlobalConfig();

	try {
		List<uint> forty = [.. Enumerable.Range(1, 40).Select(static i => (uint) i)];

		// 12. Both settings off: still no more than Steam plays at once.
		var cfg = new NocatFarm.Config.BotConfig { CustomGameName = "nocat.lol", IdleGames = [.. forty] };
		var bot = new NocatFarm.Core.Bot("uifix-idle", cfg);
		var idler = new NocatFarm.Modules.Idler(bot);
		bot.AddModule(idler);
		Check("idler, both off: 40 listed -> 31 beside a custom name, in list order", idler.Plan(DateTime.UtcNow).SequenceEqual(forty.Take(31)), $"{idler.Plan(DateTime.UtcNow).Count}");
		cfg.CustomGameNameEnabled = false;
		Check("idler, both off: 40 listed -> 32 with no custom name", idler.Plan(DateTime.UtcNow).SequenceEqual(forty.Take(32)));
		cfg.IdleGames = [730, 440, 730];
		Check("idler, both off: a short list plays whole (a repeat once)", idler.Plan(DateTime.UtcNow).SequenceEqual<uint>([730, 440]));

		// 13. The rotation card goes when something else owns the account.
		cfg.IdleGames = [.. forty];
		cfg.RotateIdleGames = true;
		idler.Plan(DateTime.UtcNow);
		bool wasRotating = idler.Rotating != null;
		cfg.LegitMode = true;
		idler.Assert();
		Check("idler: human mode owning the account clears the rotation card", wasRotating && (idler.Rotating == null));
		cfg.LegitMode = false;
		idler.Plan(DateTime.UtcNow);
		typeof(NocatFarm.Core.Bot).GetProperty("State")!.SetValue(bot, NocatFarm.Core.BotState.Online);
		typeof(NocatFarm.Core.Bot).GetProperty("IsFarming")!.SetValue(bot, true);
		idler.Assert();
		Check("idler: farming clears the rotation card", idler.Rotating == null);
		typeof(NocatFarm.Core.Bot).GetProperty("IsFarming")!.SetValue(bot, false);
		typeof(NocatFarm.Core.Bot).GetProperty("State")!.SetValue(bot, NocatFarm.Core.BotState.Stopped);
		int grind = idlerSrc.IndexOf("if (Bot.Grinding) {", idlerSrc.IndexOf("public void Assert()", StringComparison.Ordinal), StringComparison.Ordinal);
		Check("idler: a grind clears the rotation card", (grind > 0) && idlerSrc[grind..(grind + 400)].Contains("Rotating = null;", StringComparison.Ordinal));

		// 14. The rotation is made once, however many threads ask first.
		var fresh = new NocatFarm.Modules.Idler(new NocatFarm.Core.Bot("uifix-race", new NocatFarm.Config.BotConfig()));
		var made = new System.Collections.Concurrent.ConcurrentBag<NocatFarm.Modules.IdleRotation>();
		using (var go = new ManualResetEventSlim()) {
			Thread[] threads = [.. Enumerable.Range(0, 16).Select(_ => new Thread(() => { go.Wait(); made.Add(fresh.Rotation); }))];
			foreach (Thread th in threads) {
				th.Start();
			}

			go.Set();
			foreach (Thread th in threads) {
				th.Join();
			}
		}

		Check("idler: the rotation is created once when two threads ask together", (made.Count == 16) && made.All(r => ReferenceEquals(r, made.First()))
			&& idlerSrc.Contains("lock (_rotationGate) {", StringComparison.Ordinal));

		// 15. The backup keeps what "Learn from how I play" learned and where the rotation is.
		MethodInfo kind = typeof(NocatFarm.Core.Backup).GetMethod("Kind", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
		string? Kind(string rel) => (string?) kind.Invoke(null, [rel]);
		Check("backup: state/owner-<account>.json and state/rotation-<account>.json go in",
			(Kind("state/owner-new.json") == "state") && (Kind("state/rotation-kylro.json") == "state") && (Kind("state/owner-../x.json") == null));
		string ownerPath = (string) typeof(NocatFarm.Modules.OwnerHabits)
			.GetMethod("PathFor", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["new"])!;
		string rotPath = NocatFarm.Modules.IdleRotation.PathFor("new");
		string Rel(string full) => Path.GetRelativePath(NocatFarm.Config.ConfigStore.ConfigDir, full).Replace('\\', '/');
		Check("backup: ...under the names OwnerHabits and IdleRotation really use", (Kind(Rel(ownerPath)) == "state") && (Kind(Rel(rotPath)) == "state"), $"{Rel(ownerPath)}, {Rel(rotPath)}");

		// 16. "Idle my whole library" with an empty list has games to play - not banking is worth an alarm.
		Type sw = typeof(NocatFarm.Core.StuckWatch);
		Type watchT = sw.GetNestedType("Watch", BindingFlags.NonPublic | BindingFlags.Public)!;
		MethodInfo excuse = sw.GetMethod("Excuse", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
		string ExcuseOf(NocatFarm.Config.BotConfig c) {
			var b = new NocatFarm.Core.Bot("uifix-stuck", c);
			typeof(NocatFarm.Core.Bot).GetProperty("State")!.SetValue(b, NocatFarm.Core.BotState.Online);
			return ((NocatFarm.Core.Said) excuse.Invoke(null, [b, Activator.CreateInstance(watchT, true)!, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)])!).ToEnglish();
		}

		Check("stuck alarm: an empty list is still 'nothing to play'", ExcuseOf(new NocatFarm.Config.BotConfig { CustomGameNameEnabled = false }) == "nothing to play");
		string whole = ExcuseOf(new NocatFarm.Config.BotConfig { CustomGameNameEnabled = false, IdleWholeLibrary = true });
		Check("stuck alarm: an empty list with 'Idle my whole library' is not excused", whole.Length == 0, whole);
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

// ── 'report' says what it covers: "last 24h" only when it really is ───────────────────────────────────────────────
{
	Type dr = typeof(NocatFarm.Core.DailyReport);
	var stateField = dr.GetField("_state", BindingFlags.NonPublic | BindingFlags.Static)!;
	object saved = stateField.GetValue(null)!;
	object fresh = Activator.CreateInstance(saved.GetType())!;
	stateField.SetValue(null, fresh);
	DateTime now = DateTime.UtcNow;

	try {
		saved.GetType().GetProperty("LastAt")!.SetValue(fresh, now.AddHours(-3).AddMinutes(-47));
		string early = NocatFarm.Core.DailyReport.Header(false, now).ToEnglish();
		Check("report: asked 3h47m after the summary, it says since when - not 'last 24h'", early.Contains("since the daily summary at", StringComparison.Ordinal) && early.Contains("3h47m", StringComparison.Ordinal), early);
		saved.GetType().GetProperty("LastAt")!.SetValue(fresh, now.AddHours(-24));
		Check("report: the summary a day later is 'last 24h'", NocatFarm.Core.DailyReport.Header(false, now).ToEnglish().Contains("last 24h", StringComparison.Ordinal));
		saved.GetType().GetProperty("LastAt")!.SetValue(fresh, now.AddHours(-30));
		Check("report: a summary late after the PC was off says it covers more than a day", NocatFarm.Core.DailyReport.Header(false, now).ToEnglish().Contains("(30h", StringComparison.Ordinal),
			NocatFarm.Core.DailyReport.Header(false, now).ToEnglish());
		Check("report: the very first one says it starts counting now", NocatFarm.Core.DailyReport.Header(true, now).ToEnglish().Contains("starts counting now", StringComparison.Ordinal));
	} finally {
		stateField.SetValue(null, saved);
	}
}

// ── after an update: one short "what's new" line with where to read it, not every change strung across the window ──
{
	Check("what's new: the release's own page", NocatFarm.Core.SelfUpdate.ReleasePage("1.6.2") == "https://github.com/VisaHolder/nocatfarm/releases/tag/v1.6.2"
		&& NocatFarm.Core.SelfUpdate.ReleasePage("v1.6.2") == NocatFarm.Core.SelfUpdate.ReleasePage("1.6.2"));
	string su = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "SelfUpdate.cs"));
	Check("what's new: the log line is the version and the link, not the notes", su.Contains("Log.Info(new Said(\"what's new in {0}: {1}\", p[1], ReleasePage(p[1])));", StringComparison.Ordinal));
}

// ── links in the log are blue and open in the browser - in the window and on the dashboard ─────────────────────────
{
	string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string mw = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.cs"));
	Check("window: log links are drawn blue and underlined, remembered for the click", mw.Contains("DrawLinks(dc, text, TextX, y, textRoom, shownTo);", StringComparison.Ordinal)
		&& mw.Contains("_fontMonoLink = CreateFont(13, 0, 0, 0, 400, 0, 1,", StringComparison.Ordinal) && mw.Contains("_links.Add((from, y, visible, shownH, url));", StringComparison.Ordinal));
	// A click on the "scrolled back" strip, or the question's, opened a link on the log line painted over underneath.
	Check("window: a link under the scrolled-back strip or the question's can't be clicked",
		mw.Contains("shownTo = Math.Min(shownTo, (top + height) - (LineH + 2));", StringComparison.Ordinal)
		&& mw.Contains("shownTo = Math.Min(shownTo, bottom - 20);", StringComparison.Ordinal)
		&& mw.Contains("int shownH = Math.Min(LineH, shownTo - y);", StringComparison.Ordinal) && mw.Contains("if (shownH > 0) {", StringComparison.Ordinal));
	Check("window: a click on one opens it, and the cursor is a hand over it", mw.Contains("if (LinkAt(mx, my) is { } link) {", StringComparison.Ordinal)
		&& mw.Contains("case WmSetCursor when _overLink", StringComparison.Ordinal));
	Check("window: only web addresses are links", mw.Contains("[System.Text.RegularExpressions.GeneratedRegex(@\"https?://", StringComparison.Ordinal));
	string js = File.ReadAllText(Path.Combine(root, "wwwroot", "app.js"));
	Check("dashboard: the log, the recent list and command replies link their web addresses",
		js.Contains("<span class=\"m\">${linked(l.Text, q)}</span>", StringComparison.Ordinal) && js.Contains("<span>${linked(l.Text)}</span>", StringComparison.Ordinal)
		&& js.Contains("pushLocal(`<div class=\"reply\">${linked(res.output)}</div>`)", StringComparison.Ordinal));
	Check("dashboard: links open in a new tab with no way back to the page", js.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", StringComparison.Ordinal));
}

// ── races in the account core: what's announced, the owner's session, restarts, comments, grinds, logons ──────────
{
	const BindingFlags RI = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
	Type bt = typeof(NocatFarm.Core.Bot);
	string botCs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "Bot.cs")).Replace("\r\n", "\n");
	string stuckCs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "StuckWatch.cs")).Replace("\r\n", "\n");
	string Between(string src, string from, string to) {
		int a = src.IndexOf(from, StringComparison.Ordinal);
		int b = a < 0 ? -1 : src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
		return (a < 0) || (b < 0) ? "" : src[a..b];
	}
	object? Invoke(object target, string method, params object?[] args) => bt.GetMethod(method, RI)!.Invoke(target, args);
	T Field<T>(object target, string name) => (T) bt.GetField(name, RI)!.GetValue(target)!;
	void SetField(object target, string name, object? value) => bt.GetField(name, RI)!.SetValue(target, value);
	void SetProp(object target, string name, object? value) => bt.GetProperty(name, RI)!.SetValue(target, value);
	void Session(NocatFarm.Core.Bot b, bool blocked, uint app = 0) => Invoke(b, "NotePlayingSession", blocked, app);
	NocatFarm.Core.Bot Online(string name) {
		var b = new NocatFarm.Core.Bot(name, new NocatFarm.Config.BotConfig { CustomGameNameEnabled = false, ResumeDelayMinutes = 0 });
		SetProp(b, "State", NocatFarm.Core.BotState.Online);
		return b;
	}
	bool wasSuppressed = NocatFarm.Log.Suppressed;
	NocatFarm.Log.Suppressed = true;

	try {
		// A. games sent over the owner's own session: SetPlaying racing the stand-down never leaves our games "on" while he plays.
		var owner = Online("race-owner");
		int overOwner = 0, mismatched = 0;
		for (int i = 0; i < 400; i++) {
			Session(owner, false);
			using Barrier go = new(2);
			Task play = Task.Run(() => { go.SignalAndWait(); owner.SetPlaying([730, 440]); });
			Task stand = Task.Run(() => { go.SignalAndWait(); Session(owner, true, 730); });
			Task.WaitAll(play, stand);
			if (owner.PlayingBlocked && (owner.PlayingApps.Count != 0)) {
				overOwner++;
			}
		}
		Check("race A: a SetPlaying racing the stand-down never leaves games announced over the owner's session", overOwner == 0, $"{overOwner} of 400");
		Session(owner, false);
		for (int i = 0; i < 400; i++) {
			using Barrier go = new(2);
			Task one = Task.Run(() => { go.SignalAndWait(); owner.SetPlaying([1, 2]); });
			Task two = Task.Run(() => { go.SignalAndWait(); owner.SetPlaying([3, 4]); });
			Task.WaitAll(one, two);
			var announced = Field<List<uint>?>(owner, "_announcedApps");
			if ((announced == null) || !announced.SequenceEqual(owner.PlayingApps)) {
				mismatched++;
			}
		}
		Check("race A: two SetPlaying at once leave what we think we announced equal to what we sent last", mismatched == 0, $"{mismatched} of 400");
		Session(owner, true, 730);
		owner.SetPlaying([730]);
		Check("race A: after the stand-down nothing new is announced", owner.PlayingApps.Count == 0 && owner.Playing.Length == 0);
		Session(owner, false);
		Check("race A: the courtesy wait is set before the flag drops", Between(botCs, "internal void NotePlayingSession(", "YouPlayedAt = DateTime.UtcNow;")
			.Contains("_resumeAt = resumeAt;\n\t\t\t\tPlayingBlocked = false;", StringComparison.Ordinal));
		Check("race A: SetPlaying decides under the play lock, and so does its delayed second half",
			botCs.Contains("lock (_playGate) {\n\t\t\tSetPlayingLocked(appIds, overrideName, force);", StringComparison.Ordinal)
			&& Between(botCs, "private void SetPlayingLocked(", "public void StopPlaying()").Contains("lock (_playGate) {\n\t\t\t\t\t\tif ((Volatile.Read(ref _playSequence) != mine)", StringComparison.Ordinal));

		// B. the persona re-sent under the owner a few seconds after he started playing.
		var persona = Online("race-persona");
		int seq = Field<int>(persona, "_playSequence");
		bool free = (bool) Invoke(persona, "StillRePersona", seq)!;
		SetProp(persona, "PlayingBlocked", true);
		bool underOwner = (bool) Invoke(persona, "StillRePersona", seq)!;
		SetProp(persona, "PlayingBlocked", false);
		SetProp(persona, "Paused", true);
		bool whilePaused = (bool) Invoke(persona, "StillRePersona", seq)!;
		SetProp(persona, "Paused", false);
		Session(persona, true, 730);
		Session(persona, false);
		bool afterStandDown = (bool) Invoke(persona, "StillRePersona", seq)!;
		Check("race B: the delayed persona re-send goes ahead only while the account is still ours",
			free && !underOwner && !whilePaused && !afterStandDown, $"free {free}, owner {underOwner}, paused {whilePaused}, after a stand-down {afterStandDown}");
		Check("race B: the re-send checks that under the play lock", Between(botCs, "private void RePersonaShortly()", "private int _flagsAsSeen")
			.Contains("lock (_playGate) {\n\t\t\t\t\t\tif (!StillRePersona(mine)) {", StringComparison.Ordinal));

		// C. a removed account signing back in; a stop during the stuck alarm's restart undone.
		var ghost = new NocatFarm.Core.Bot("race-ghost", new NocatFarm.Config.BotConfig());
		await ghost.DisposeAsync();
		Task late = ghost.StartAsync();
		bool quick = await Task.WhenAny(late, Task.Delay(5000)) == late;
		Check("race C: a start after the dispose does nothing - no ghost sign-in", quick && ghost.Disposed && !ghost.Running && ghost.State == NocatFarm.Core.BotState.Stopped,
			$"returned {quick}, running {ghost.Running}, {ghost.State}");
		Type sw = typeof(NocatFarm.Core.StuckWatch);
		const BindingFlags SS = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
		var restarting = (HashSet<string>) sw.GetField("Restarting", SS)!.GetValue(null)!;
		object swGate = sw.GetField("Gate", SS)!.GetValue(null)!;
		void Restarting(string name, bool on) {
			lock (swGate) {
				if (on) { restarting.Add(name); } else { restarting.Remove(name); }
			}
		}
		MethodInfo still = sw.GetMethod("StillRestarting", SS)!;
		var restarted = new NocatFarm.Core.Bot("race-restart", new NocatFarm.Config.BotConfig { Enabled = true });
		Restarting(restarted.Name, true);
		long stops = restarted.StopCount;
		await restarted.StopAsync();   // the restart's own stop
		bool goesAhead = (bool) still.Invoke(null, [restarted, stops + 1])!;
		await restarted.StopAsync();   // somebody's 'stop' during the 1.5s
		bool afterUserStop = (bool) still.Invoke(null, [restarted, stops + 1])!;
		Restarting(restarted.Name, false);   // what Reset does after a restore
		bool afterReset = (bool) still.Invoke(null, [restarted, restarted.StopCount])!;
		Restarting(restarted.Name, true);
		restarted.Cfg.Enabled = false;
		bool afterDisable = (bool) still.Invoke(null, [restarted, restarted.StopCount])!;
		restarted.Cfg.Enabled = true;
		await restarted.DisposeAsync();
		bool afterDispose = (bool) still.Invoke(null, [restarted, restarted.StopCount])!;
		Restarting(restarted.Name, false);
		Check("race C: the stuck alarm starts it again only if nothing stopped, switched off, removed or called it off meanwhile",
			goesAhead && !afterUserStop && !afterReset && !afterDisable && !afterDispose,
			$"own stop {goesAhead}, user stop {afterUserStop}, reset {afterReset}, disabled {afterDisable}, disposed {afterDispose}");
		Check("race C: the restart asks before starting", Between(stuckCs, "internal static Func<Bot, Task> Restart", "internal static bool StillRestarting")
			.Contains("if (!StillRestarting(b, stops + 1)) {", StringComparison.Ordinal));
		Check("race C: the dispose is marked under the stop lock, and a start checks it there", Between(botCs, "public async ValueTask DisposeAsync()", "public readonly record struct AppOwnership")
			.Contains("Volatile.Write(ref _disposed, true);", StringComparison.Ordinal)
			&& Between(botCs, "private async Task StartCoreAsync()", "public bool Stopping").Contains("if (Volatile.Read(ref _disposed)) {", StringComparison.Ordinal));

		// D. two pushes, one comment - said once.
		var talky = Online("race-comments");
		SetField(talky, "_lastCommentAt", 50L);
		int said = 0;
		void Count(NocatFarm.Log.Entry e) {
			if ((e.Source == "race-comments") && e.Said.ToEnglish().StartsWith("new comment", StringComparison.Ordinal)) {
				Interlocked.Increment(ref said);
			}
		}
		NocatFarm.Log.Written += Count;
		try {
			List<NocatFarm.Core.Bot.ProfileComment> onProfile = [new("amy", "hi there", 100)];
			Func<Task<List<NocatFarm.Core.Bot.ProfileComment>>> read = async () => { await Task.Delay(100); return [.. onProfile]; };
			MethodInfo announce = bt.GetMethod("AnnounceCommentsAsync", RI)!;
			Task a1 = (Task) announce.Invoke(talky, [1, 50L, read])!;
			Task a2 = (Task) announce.Invoke(talky, [1, 50L, read])!;
			await Task.WhenAll(a1, a2);
			Check("race D: two pushes for one comment announce it once", said == 1, $"{said} lines");
			onProfile.Insert(0, new("bob", "second", 200));
			await (Task) announce.Invoke(talky, [1, 100L, read])!;
			Check("race D: a genuinely new comment after that is still announced", said == 2, $"{said} lines");
		} finally {
			NocatFarm.Log.Written -= Count;
		}

		// E. grind and drop run: a stop mid-count doesn't come back, counts aren't lost.
		var grinder = new NocatFarm.Core.Bot("race-grind", new NocatFarm.Config.BotConfig());
		string grindPath = (string) bt.GetProperty("GrindPath", RI)!.GetValue(grinder)!;
		int resurrected = 0;
		for (int i = 0; i < 200; i++) {
			grinder.StartGrind(460920, TimeSpan.FromHours(1), drops: 50);
			using Barrier go = new(3);
			Task counting = Task.Run(() => { go.SignalAndWait(); for (int k = 0; k < 5; k++) { grinder.CountGrindDrops(1); } });
			Task holding = Task.Run(() => { go.SignalAndWait(); grinder.HoldGrindStart(); });
			Task stopping = Task.Run(() => { go.SignalAndWait(); grinder.StopGrind(); });
			Task.WaitAll(counting, holding, stopping);
			if ((grinder.GrindGame != 0) || (grinder.GrindUntil != null) || File.Exists(grindPath)) {
				resurrected++;
			}
		}
		Check("race E: a grind stopped while drops were being counted stays stopped, on disk too", resurrected == 0, $"{resurrected} of 200");
		grinder.StartGrind(460920, TimeSpan.FromHours(1), drops: 3);
		grinder.CountGrindDrops(1, 730);
		Check("race E: drops from another game don't count against the run", grinder.GrindDropsLeft == 3);
		grinder.StopGrind();
		grinder.CountGrindDrops(1);
		Check("race E: counting after the stop writes nothing back", grinder.GrindGame == 0 && !File.Exists(grindPath));
		grinder.StartDropsFirst(460920, 1000);
		Parallel.For(0, 4, _ => { for (int k = 0; k < 100; k++) { grinder.CountDropsFirst(460920, 1); } });
		Check("race E: drops counted from four threads at once are all counted", grinder.DropsFirstGot == 400, $"{grinder.DropsFirstGot}");
		grinder.StopDropsFirst();
		DateTime until = DateTime.UtcNow.AddHours(2);
		SetProp(grinder, "GrindUntil", until);
		Check("race E: the grind's end is kept as ticks and reads back the same", grinder.GrindUntil == until && grinder.GrindUntil?.Kind == DateTimeKind.Utc);
		SetProp(grinder, "GrindUntil", null);
		Check("race E: ...and none reads back as none", grinder.GrindUntil == null && !grinder.Grinding);

		// F. an older logon's handler carrying on after a newer one.
		var relog = Online("race-logon");
		SetField(relog, "_running", true);
		SetField(relog, "_logonGen", 5L);
		bool current = (bool) Invoke(relog, "LogonStillCurrent", 5L)!;
		bool stale = (bool) Invoke(relog, "LogonStillCurrent", 4L)!;
		SetProp(relog, "State", NocatFarm.Core.BotState.Reconnecting);
		bool dropped = (bool) Invoke(relog, "LogonStillCurrent", 5L)!;
		SetField(relog, "_running", false);
		Check("race F: only the latest logon, on a running signed-in account, carries on", current && !stale && !dropped, $"current {current}, stale {stale}, dropped {dropped}");
		string logonBody = Between(botCs, "private async Task OnLoggedOnAsync(", "private void OnLoggedOff(");
		Check("race F: the logon takes its number first and checks it after the wait and before starting the modules",
			logonBody.StartsWith("private async Task OnLoggedOnAsync(SteamUser.LoggedOnCallback cb) {\n\t\tlong logon = Interlocked.Increment(ref _logonGen);", StringComparison.Ordinal)
			&& (logonBody.Split("if (!LogonStillCurrent(logon)) {").Length == 3)
			&& logonBody.Contains("await _stopGate.WaitAsync().ConfigureAwait(false);", StringComparison.Ordinal));

		// G. human mode banks the session in flight as it stops - the sign-in time must still be there.
		var banker = Online("race-bank");
		var probe = new RaceProbeModule(banker);
		banker.AddModule(probe);
		SetProp(banker, "OnlineSince", DateTime.UtcNow.AddMinutes(-3));
		await banker.StopAsync();
		Check("race G: a stop stops the modules while the sign-in time is still there, then clears it", probe.SawSignIn == true && banker.OnlineSince == null, $"saw {probe.SawSignIn}");
		probe.SawSignIn = null;
		SetProp(banker, "State", NocatFarm.Core.BotState.Online);
		SetProp(banker, "OnlineSince", DateTime.UtcNow.AddMinutes(-3));
		await (Task) Invoke(banker, "OnDisconnectedAsync", Field<long>(banker, "_session"))!;
		Check("race G: a disconnect does the same", probe.SawSignIn == true && banker.OnlineSince == null, $"saw {probe.SawSignIn}");
		Check("race G: a Steam log-off leaves the sign-in time for the disconnect to clear",
			!Between(botCs, "private void OnLoggedOff(", "private async void OnDisconnected(").Contains("OnlineSince = null;", StringComparison.Ordinal));
		await banker.DisposeAsync();

		// The rest: the reconnect's token read once, connects under the stop lock, sign-in waits tied to the run, a trade count
		// that can't overwrite a newer push, a stable web sessionid, no disposed lock to trip over.
		Check("race: the reconnect reads the run's token once", !botCs.Contains("_cts?.Token", StringComparison.Ordinal)
			&& Between(botCs, "private async Task OnDisconnectedAsync(long session)", "// ── heartbeat").Contains("CancellationToken ct = _runToken;", StringComparison.Ordinal));
		Check("race: both connects happen under the stop lock, after checking the run is still on",
			Between(botCs, "private async Task StartCoreAsync()", "public bool Stopping").Contains("await _stopGate.WaitAsync().ConfigureAwait(false);\n\n\t\ttry {\n\t\t\tif (!_running || (Interlocked.Read(ref _session) != session)) {", StringComparison.Ordinal)
			&& Between(botCs, "private async Task OnDisconnectedAsync(long session)", "// ── heartbeat").Contains("await _stopGate.WaitAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal));
		Check("race: a password or QR sign-in stops waiting when its run is stopped",
			botCs.Contains("poll = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal)
			&& botCs.Contains("CancellationTokenSource.CreateLinkedTokenSource(giveUp.Token, ct);", StringComparison.Ordinal)
			&& botCs.Contains("} catch (Exception) when (ct.IsCancellationRequested || (Interlocked.Read(ref _session) != session)) {", StringComparison.Ordinal));
		Check("race: the QR sign-in's account name is saved under the config lock",
			Between(botCs, "private async Task<bool> SignInWithQrAsync(", "private void ShowQr(").Contains("lock (CfgGate) {", StringComparison.Ordinal)
			&& botCs.Contains("public Lock CfgGate { get; } = new();", StringComparison.Ordinal));
		var trades = new NocatFarm.Core.Bot("race-trades", new NocatFarm.Config.BotConfig());
		trades.NoteTradeOffersSeen(3);
		bool fromUnknown = trades.TradeOffersWaiting == 3;
		trades.NoteTradeOffersSeen(5);
		bool notRaised = trades.TradeOffersWaiting == 3;
		trades.NoteTradeOffersSeen(1);
		Check("race: the trade count read off the page only ever lowers (or fills in) the pushed one", fromUnknown && notRaised && trades.TradeOffersWaiting == 1);
		object web = bt.GetProperty("Web", RI)!.GetValue(trades)!;
		MethodInfo init = web.GetType().GetMethod("Init")!;
		init.Invoke(web, [76561198000000000UL, "a.eyJleHAiOjk5OTk5OTk5OTl9.c"]);
		string sid1 = (string) web.GetType().GetProperty("SessionId")!.GetValue(web)!;
		init.Invoke(web, [76561198000000000UL, "b.eyJleHAiOjk5OTk5OTk5OTl9.d"]);
		string sid2 = (string) web.GetType().GetProperty("SessionId")!.GetValue(web)!;
		Check("race: a token refresh keeps the web sessionid, so a POST on its way still matches its cookie", (sid1.Length == 24) && (sid1 == sid2));
		await trades.DisposeAsync();
		var tokenLock = Field<SemaphoreSlim>(trades, "_tokenLock");
		bool released;
		try {
			await tokenLock.WaitAsync();
			tokenLock.Release();
			released = true;
		} catch (ObjectDisposedException) {
			released = false;
		}
		Check("race: a token mint still running at the dispose can release its lock", released);
		string inventory = Between(botCs, "private void OnItemAnnouncements(", "private int _inventoryVisitQueued;");
		Check("race: the delayed inventory visit checks the account is still signed in, same run",
			inventory.Contains("if (!_running || (State != BotState.Online) || (Interlocked.Read(ref _session) != session)) {", StringComparison.Ordinal));
		await owner.DisposeAsync();
		await persona.DisposeAsync();
		await talky.DisposeAsync();
		await grinder.DisposeAsync();
		await relog.DisposeAsync();
	} finally {
		NocatFarm.Log.Suppressed = wasSuppressed;
	}
}

// ── races: claims, saves in order, one-at-a-time work, settings and wake-ups ────────────────────────────────────────
{
	string Src(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", rel)).Replace("\r\n", "\n");
	const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-race3-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config", "state"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		Type botT = typeof(NocatFarm.Core.Bot);
		MethodInfo claimM = botT.GetMethod("ClaimItems", Any)!;
		MethodInfo releaseM = botT.GetMethod("ReleaseItems", Any)!;
		HashSet<ulong> Claim(NocatFarm.Core.Bot b, params ulong[] ids) => (HashSet<ulong>) claimM.Invoke(b, [ids])!;
		void Release(NocatFarm.Core.Bot b, params ulong[] ids) => releaseM.Invoke(b, [ids]);

		// 1. A swap claims its cards on both sides and hands them back.
		var giver = new NocatFarm.Core.Bot("race3-a", new NocatFarm.Config.BotConfig());
		var taker = new NocatFarm.Core.Bot("race3-b", new NocatFarm.Config.BotConfig());
		NocatFarm.Core.Looting.Item SwapCard(ulong id) => new(id, 1, 0, 1, "Trading Card", "card " + id, 440, 6);
		Claim(taker, 2);
		var busy = await NocatFarm.Core.Looting.SwapAsync(giver, taker, [SwapCard(1)], [SwapCard(2)]);
		Check("swap: a card the other side is already moving stops the swap", !busy.Ok && busy.Message.Contains("already being sent, sold or swapped", StringComparison.Ordinal), busy.Message);
		Check("swap: ...and its own card is handed back", Claim(giver, 1).Count == 1);
		Release(giver, 1);
		Release(taker, 2);
		var swaps = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => NocatFarm.Core.Looting.SwapAsync(giver, taker, [SwapCard(1)], [SwapCard(2)]))));
		Check("swap: 32 at once, signed out - each refused, none leaves a card claimed", swaps.All(static s => !s.Ok) && (Claim(giver, 1).Count == 1) && (Claim(taker, 2).Count == 1));
		Release(giver, 1);
		Release(taker, 2);
		Check("swap: the claim comes before anything else; learned bans are saved under the account's config gate",
			Src(Path.Combine("Core", "Looting.cs")).Contains("HashSet<ulong> mine = bot.ClaimItems(giving.Select(static i => i.AssetId));", StringComparison.Ordinal)
			&& Src(Path.Combine("Core", "Looting.cs")).Contains("lock (bot.CfgGate) {", StringComparison.Ordinal) && Src(Path.Combine("Modules", "BanWatch.cs")).Contains("lock (Bot.CfgGate) {", StringComparison.Ordinal));

		// 2. Achievement writes: one per account and game, worked out from the stats as they are then.
		MethodInfo gateOf = typeof(NocatFarm.Core.Achievements).GetMethod("GateOf", Any)!;
		var gates = new System.Collections.Concurrent.ConcurrentBag<object>();
		Parallel.For(0, 64, _ => gates.Add(gateOf.Invoke(null, [giver, 440u])!));
		Check("achievements: every writer to one game gets the same gate", gates.Distinct().Count() == 1 && !ReferenceEquals(gateOf.Invoke(null, [giver, 730u]), gates.First())
			&& !ReferenceEquals(gateOf.Invoke(null, [taker, 440u]), gates.First()));
		string ach = Src(Path.Combine("Core", "Achievements.cs"));
		Check("achievements: the write waits its turn and re-reads the stats before changing them",
			ach.Contains("await gate.WaitAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal) && ach.Contains("CMsgClientGetUserStatsResponse? latest = await bot.Stats.GetUserStatsAsync(set.AppId, bot.SteamId, ct)", StringComparison.Ordinal)
			&& ach.Contains(": now.GetValueOrDefault(achievement.StatId);", StringComparison.Ordinal) && !ach.Contains("set.StatValues.GetValueOrDefault(achievement.StatId)", StringComparison.Ordinal));

		// 3. The key queue: what's on disk is what's in memory, however many change it at once.
		typeof(NocatFarm.Core.KeyQueue).GetMethod("Reload", Any)!.Invoke(null, []);
		Parallel.For(0, 16, t => {
			for (int i = 0; i < 20; i++) {
				NocatFarm.Core.KeyQueue.Add([$"RACE{t:00}-{i:000}"]);

				if (i % 3 == 0) {
					NocatFarm.Core.KeyQueue.Done($"RACE{t:00}-{i:000}");
				} else if (i % 5 == 0) {
					NocatFarm.Core.KeyQueue.Defer($"RACE{t:00}-{i:000}");
				}
			}
		});
		List<string> inMemory = [.. NocatFarm.Core.KeyQueue.Snapshot().Select(static k => k.Key).Order()];
		typeof(NocatFarm.Core.KeyQueue).GetMethod("Reload", Any)!.Invoke(null, []);
		List<string> onDisk = [.. NocatFarm.Core.KeyQueue.Snapshot().Select(static k => k.Key).Order()];
		Check("key queue: 16 threads adding, using and deferring keys - the file holds exactly what memory does", (inMemory.Count == 16 * 13) && inMemory.SequenceEqual(onDisk), $"{inMemory.Count} in memory, {onDisk.Count} on disk");
		Check("key queue: a Flush for shutdown, and saves one at a time", typeof(NocatFarm.Core.KeyQueue).GetMethod("Flush", Any) != null
			&& Src(Path.Combine("Core", "KeyQueue.cs")).Contains("lock (SaveGate) {", StringComparison.Ordinal));

		// 4. Rep4rep's count: the last save written is the newest.
		var r4r = new NocatFarm.Rep4Rep.Rep4RepState();
		await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () => {
			r4r.RecordPost($"task{i}");
			await r4r.SaveAsync("race3");
		})));
		NocatFarm.Rep4Rep.Rep4RepState? back = await NocatFarm.Rep4Rep.Rep4RepState.LoadAsync("race3");
		Check("rep4rep: 200 posts saved at once - all 200 are in the file", back?.PostsInLast24h() == 200, $"{back?.PostsInLast24h()}");

		// 5. One key worker at a time.
		var working = (SemaphoreSlim) typeof(NocatFarm.Core.Redeeming).GetField("Working", Any)!.GetValue(null)!;
		working.Wait();
		Task queued = NocatFarm.Core.Redeeming.WorkQueueAsync([]);
		bool leftAtOnce = queued.IsCompleted && (working.CurrentCount == 0);
		working.Release();
		await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => NocatFarm.Core.Redeeming.WorkQueueAsync([]))));
		Check("keys: a second worker while one is working goes straight away, and the gate is free after", leftAtOnce && (working.CurrentCount == 1));

		// 6. Saves in order; a refresh pressed mid-read isn't swallowed; the total is read whole.
		typeof(NocatFarm.Core.Lifetime).GetMethod("Reload", Any)!.Invoke(null, []);
		Parallel.For(0, 32, t => {
			for (int i = 0; i < 25; i++) {
				NocatFarm.Core.Lifetime.Add("race3", 1);

				if (i % 5 == 0) {
					NocatFarm.Core.Lifetime.Save();
				}
			}
		});
		NocatFarm.Core.Lifetime.Save();
		typeof(NocatFarm.Core.Lifetime).GetMethod("Reload", Any)!.Invoke(null, []);
		Check("lifetime: 32 threads adding and saving - the file has every minute", NocatFarm.Core.Lifetime.For("race3") == 800, $"{NocatFarm.Core.Lifetime.For("race3")}");
		Check("lifetime, inventory history, price book: each save holds its gate from the snapshot to the file",
			Src(Path.Combine("Core", "Lifetime.cs")).Contains("lock (SaveGate) {\n\t\t\t\tWriteLocked();", StringComparison.Ordinal)
			&& Src(Path.Combine("Core", "InventoryHistory.cs")).Contains("lock (SaveGate) {\n\t\t\t\tDictionary<string, List<Point>> snapshot;", StringComparison.Ordinal)
			&& Src(Path.Combine("Core", "PriceBook.cs")).Contains("lock (SaveGate) {\n\t\t\t\tDictionary<string, Price> snapshot;", StringComparison.Ordinal)
			&& Src(Path.Combine("Core", "InventoryHistory.cs")).Contains("due = DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(5);", StringComparison.Ordinal)
			&& Src(Path.Combine("Core", "PriceBook.cs")).Contains("due = DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(30);", StringComparison.Ordinal));

		NocatFarm.Core.InventoryValue inv = giver.Inventory;
		Type invT = typeof(NocatFarm.Core.InventoryValue);
		FieldInfo readAt = invT.GetField("_readAt", Any)!;
		MethodInfo markRead = invT.GetMethod("MarkRead", Any)!;
		int refreshesBefore = (int) invT.GetField("_refreshes", Any)!.GetValue(inv)!;
		inv.ForceRefresh();   // pressed while a read that began before it is still going
		markRead.Invoke(inv, [refreshesBefore]);
		bool keptAsked = (DateTime) readAt.GetValue(inv)! == DateTime.MinValue;
		markRead.Invoke(inv, [(int) invT.GetField("_refreshes", Any)!.GetValue(inv)!]);
		Check("inventory value: refresh pressed mid-read gets its own read; a read with no press counts as fresh",
			keptAsked && (DateTime.UtcNow - (DateTime) readAt.GetValue(inv)! < TimeSpan.FromMinutes(1)));
		Check("inventory value: the total and its games are published as one object", Src(Path.Combine("Core", "InventoryValue.cs")).Contains("private volatile Figures _figures", StringComparison.Ordinal)
			&& Src(Path.Combine("Core", "InventoryValue.cs")).Contains("public decimal Total => _figures.Total;", StringComparison.Ordinal));

		// 7. Human mode.
		Lock reentry = new();
		bool again = false;

		lock (reentry) {
			lock (reentry) {
				again = true;
			}
		}

		Check("human: the step gate can be taken again by the thread holding it (a step may reroll the day)", again);
		string hm = Src(Path.Combine("Modules", "HumanMode.cs"));
		int Body(string sig) => hm.IndexOf(sig, StringComparison.Ordinal);
		Check("human: a tick and 'wake' / 'human reroll' / 'habits forget' never run together",
			hm.Contains("lock (_stepGate) {\n\t\t\tStepLocked();", StringComparison.Ordinal)
			&& new[] { "public void WakeNow() {", "public void RerollToday() {", "public void ForgetHabits() {" }.All(s => (Body(s) > 0) && hm[Body(s)..(Body(s) + 200)].Contains("lock (_stepGate) {", StringComparison.Ordinal)));
		Check("human: the hour targets reached are locked, the farm game read once",
			hm.Contains("lock (_targetsReached) {", StringComparison.Ordinal) && hm.Contains("(Bot.PlayingApps is [uint only])", StringComparison.Ordinal) && !hm.Contains("_game = Bot.PlayingApps[0];", StringComparison.Ordinal));

		var hmBot = new NocatFarm.Core.Bot("race3-human", new NocatFarm.Config.BotConfig());
		Type hmT = typeof(NocatFarm.Modules.HumanMode);
		NocatFarm.Modules.HumanMode Playing10() {
			var h = new NocatFarm.Modules.HumanMode(hmBot);
			hmT.GetField("_phase", Any)!.SetValue(h, NocatFarm.Modules.HumanMode.Phase.Playing);
			hmT.GetField("_dayStamp", Any)!.SetValue(h, -1);   // nothing written to disk
			hmT.GetField("_bankedForLogon", Any)!.SetValue(h, DateTime.UtcNow.AddHours(-1));
			hmT.GetField("_bankedTo", Any)!.SetValue(h, DateTime.UtcNow.AddMinutes(-10).AddSeconds(-5));
			hmT.GetField("_lastBankAt", Any)!.SetValue(h, DateTime.UtcNow.AddSeconds(-30));

			return h;
		}

		// The Bot clears the sign-in time only once the modules have stopped, so a stop that finds it gone is the second of
		// two: a disconnect stopped them and banked, and this is the stop (or restart) after. Crediting from the sign-in it
		// had recorded counted the minutes since the disconnect - signed out - as played.
		NocatFarm.Modules.HumanMode stopped = Playing10();
		await stopped.StopAsync();
		NocatFarm.Modules.HumanMode ticked = Playing10();
		hmT.GetMethod("BankSession", Any)!.Invoke(ticked, null);
		Check("human: a stop after a disconnect already banked the sign-in credits nothing more", stopped.PlayedMinutesToday == 0, $"{stopped.PlayedMinutesToday}");
		DateTime hmLogon = DateTime.UtcNow.AddHours(-1);
		typeof(NocatFarm.Core.Bot).GetProperty("OnlineSince")!.SetValue(hmBot, hmLogon);
		NocatFarm.Modules.HumanMode signedIn = Playing10();
		hmT.GetField("_bankedForLogon", Any)!.SetValue(signedIn, hmLogon);
		await signedIn.StopAsync();
		typeof(NocatFarm.Core.Bot).GetProperty("OnlineSince")!.SetValue(hmBot, null);
		Check("human: ...while a stop with the sign-in time still there banks the minutes in flight", signedIn.PlayedMinutesToday == 10, $"{signedIn.PlayedMinutesToday}");
		Check("human: ...while an ordinary tick with no sign-in time still credits nothing (an outage isn't play)", ticked.PlayedMinutesToday == 0, $"{ticked.PlayedMinutesToday}");

		// 8. Card farming: one count of slots, whatever the limit becomes.
		Type cf = typeof(NocatFarm.Modules.CardFarmer);
		MethodInfo take = cf.GetMethod("TakeSlotAsync", Any)!, give = cf.GetMethod("ReleaseSlot", Any)!;
		Task<bool> Take(CancellationToken c = default) => (Task<bool>) take.Invoke(null, [c])!;
		void Give() => give.Invoke(null, []);
		NocatFarm.Modules.CardFarmer.ApplyConcurrencyLimit(2);
		int inside = 0, most = 0;
		await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () => {
			bool got = await Take();
			int n = Interlocked.Increment(ref inside);
			int seen;

			while (((seen = Volatile.Read(ref most)) < n) && (Interlocked.CompareExchange(ref most, n, seen) != seen)) {
				// someone else raised it - look again
			}

			await Task.Delay(5);
			Interlocked.Decrement(ref inside);

			if (got) {
				Give();
			}
		})));
		Check("farming slots: 24 accounts, limit 2 - never more than 2 at once", most is 1 or 2, $"{most}");
		bool a1 = await Take(), a2 = await Take();
		NocatFarm.Modules.CardFarmer.ApplyConcurrencyLimit(1);
		Task<bool> third = Take();
		await Task.Delay(100);
		bool heldBack = !third.IsCompleted;
		Give();
		await Task.Delay(100);
		bool stillHeld = !third.IsCompleted;   // one still farming on the old limit of 2 fills the new limit of 1
		Give();
		bool thirdGot = await third.WaitAsync(TimeSpan.FromSeconds(5));
		Check("farming slots: lowered from 2 to 1 with 2 farming - the next waits until both are done", a1 && a2 && heldBack && stillHeld && thirdGot);
		Give();
		NocatFarm.Modules.CardFarmer.ApplyConcurrencyLimit(0);
		Check("farming slots: no limit - nothing to take", !await Take());
		string farmer = Src(Path.Combine("Modules", "CardFarmer.cs"));
		int finished = farmer.IndexOf("\t\t\t\tFinishedGame = game.AppId;\n\t\t\t}\n\n\t\t\tgame.CardsRemaining = fresh.CardsRemaining;", StringComparison.Ordinal);
		Check("farming: the last card's game is named before its count goes to 0, and a farm game knocked off is put back",
			(finished > 0) && farmer.Contains("&& Bot.PlayingApps.Contains(game.AppId)) {", StringComparison.Ordinal));
		string idlerSrc3 = Src(Path.Combine("Modules", "Idler.cs"));
		Check("idler: the grind game is read once and 0 is never played", idlerSrc3.Contains("uint grind = Bot.GrindGame;", StringComparison.Ordinal)
			&& idlerSrc3.Contains("Bot.SetPlaying([grind]);", StringComparison.Ordinal) && !idlerSrc3.Contains("Bot.SetPlaying([Bot.GrindGame]);", StringComparison.Ordinal));

		// 9. Rep4rep: a wake before the sleep isn't lost; nothing posts after "stop".
		using (var api = new NocatFarm.Rep4Rep.Rep4RepApi()) {
			var r4m = new NocatFarm.Modules.Rep4RepModule(giver, api);
			Type rt = typeof(NocatFarm.Modules.Rep4RepModule);
			rt.GetMethod("Wake", Any)!.Invoke(r4m, []);   // 'wake' lands between two sleeps
			var sleep = (Task<bool>) rt.GetMethod("SleepOrWake", Any)!.Invoke(r4m, [TimeSpan.FromMinutes(10), CancellationToken.None])!;
			bool wokeFirst = await Task.WhenAny(sleep, Task.Delay(3000)) == sleep;
			var sleep2 = (Task<bool>) rt.GetMethod("SleepOrWake", Any)!.Invoke(r4m, [TimeSpan.FromMinutes(10), CancellationToken.None])!;
			await Task.Delay(100);
			bool keptSleeping = !sleep2.IsCompleted;
			rt.GetMethod("Wake", Any)!.Invoke(r4m, []);
			bool wokeAgain = await Task.WhenAny(sleep2, Task.Delay(3000)) == sleep2;
			Check("rep4rep: a wake-up that arrives between sleeps cuts the next one short - once", wokeFirst && keptSleeping && wokeAgain);
			r4m.Paused = true;
			Check("rep4rep: paused means no post", (bool) rt.GetMethod("StoppedPosting", Any)!.Invoke(r4m, [])!);
		}

		string r4 = Src(Path.Combine("Modules", "Rep4RepModule.cs"));
		int retry = r4.IndexOf("private async Task<int> RetryOnceAsync(", StringComparison.Ordinal);
		Check("rep4rep: the retry looks again after its wait, the post after taking its turn, the profile id is looked up once",
			(retry > 0) && r4[retry..(retry + 1400)].Contains("if (StoppedPosting()) {", StringComparison.Ordinal)
			&& r4.Contains("try {\n\t\t\t// Stopped while this step was fetching or waiting its turn", StringComparison.Ordinal)
			&& r4.Contains("lookup = _resolving ??= Task.Run(", StringComparison.Ordinal) && !r4.Contains("_profileId ??= await", StringComparison.Ordinal));

		// 10. Social: what was waiting at sign-in is answered.
		var social = new NocatFarm.Modules.Social(giver);
		typeof(NocatFarm.Modules.Social).GetMethod("CatchUpWaiting", Any)!.Invoke(social, []);   // signed out: no friends list yet, nothing to do, nothing thrown
		string so = Src(Path.Combine("Modules", "Social.cs"));
		Check("social: requests and invites already waiting are walked once the handlers are on",
			so.Contains("Bot.ChatMessage += OnChatMessage;\n\n\t\ttry {\n\t\t\tCatchUpWaiting();", StringComparison.Ordinal)
			&& so.Contains("EFriendRelationship.RequestRecipient", StringComparison.Ordinal) && so.Contains("EClanRelationship.Invited) {\n\t\t\t\t\tgroups.Add", StringComparison.Ordinal)
			&& so.Contains("CancellationToken ct = _runToken;", StringComparison.Ordinal) && !so.Contains("Cts?.Token", StringComparison.Ordinal));

		// 11. Event items: one of sticker / shop / queue at a time.
		var events = new NocatFarm.Modules.EventItems(giver);
		var eventsBusy = (SemaphoreSlim) typeof(NocatFarm.Modules.EventItems).GetField("_busy", Any)!.GetValue(events)!;
		eventsBusy.Wait();   // the loop is in the shop
		using var giveUp = new CancellationTokenSource(300);
		Task<int> command = events.ShopAsync(giveUp.Token);
		bool waitedItsTurn = false;

		try {
			await command;
		} catch (OperationCanceledException) {
			waitedItsTurn = true;
		}

		eventsBusy.Release();
		Check("event items: 'freeitems' while the loop is in the shop waits its turn", waitedItsTurn && (eventsBusy.CurrentCount == 1));

		// 12. Plugin settings: a first save racing the first read keeps every other setting.
		Directory.CreateDirectory(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "plugins"));
		File.WriteAllText(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "plugins", "race3.settings.json"), "{\"keep\":\"1\",\"also\":\"2\"}");
		Type hostT = typeof(NocatFarm.Core.Bot).Assembly.GetType("NocatFarm.Plugins.Host")!;
		object host = Activator.CreateInstance(hostT, Any, null, [null, "race3"], null)!;
		MethodInfo setValue = hostT.GetMethod("SetValue", Any)!, setting = hostT.GetMethod("Setting", Any)!;
		Parallel.For(0, 16, i => {
			setValue.Invoke(host, [$"k{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
			_ = setting.Invoke(host, ["keep"]);
		});
		string saved = File.ReadAllText(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "plugins", "race3.settings.json"));
		Check("plugin settings: 16 first saves at once - the file keeps the plugin's other settings and every new one",
			saved.Contains("\"keep\":\"1\"", StringComparison.Ordinal) && saved.Contains("\"also\":\"2\"", StringComparison.Ordinal) && Enumerable.Range(0, 16).All(i => saved.Contains($"\"k{i}\"", StringComparison.Ordinal)), saved);

		// 13. Bans: one look at a time, a second caller joins it.
		var bans = new NocatFarm.Modules.BanWatch(giver);
		FieldInfo checking = typeof(NocatFarm.Modules.BanWatch).GetField("_checking", Any)!;
		var inFlight = new TaskCompletionSource<NocatFarm.Modules.BanWatch.Bans?>();
		checking.SetValue(bans, inFlight.Task);
		Task<NocatFarm.Modules.BanWatch.Bans?> first = bans.CheckAsync(), second = bans.CheckAsync();
		await Task.Delay(50);
		bool joined = !first.IsCompleted && !second.IsCompleted;
		inFlight.SetResult(null);
		Check("bans: 'bans' while the timed look runs joins it rather than reading twice", joined && (await first == null) && (await second == null));
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// temp - it goes when Windows tidies up
		}
	}
}

// ── "Who's been here": a visit from outside isn't drawn 480px tall by the log box's .out rule ──────────────────────
{
	string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "wwwroot");
	string js = File.ReadAllText(Path.Combine(root, "app.js"));
	string css = File.ReadAllText(Path.Combine(root, "style.css"));
	Check("visitors card: an outside visit is marked 'away', not 'out' (the log box's class)",
		js.Contains("x.Where === 'internet' ? 'away' : ''", StringComparison.Ordinal) && css.Contains(".ph-visit.away.ok", StringComparison.Ordinal) && !css.Contains(".ph-visit.out", StringComparison.Ordinal));
}

// ── races: an account's settings - a dashboard save and every other change take the account's lock ─────────────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig realGlobal = NocatFarm.Config.Live.Global;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-cfggate-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		var web = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };
		MethodInfo saveBot = typeof(NocatFarm.Web.WebHost).GetMethod("SaveBotFromPage", BindingFlags.NonPublic | BindingFlags.Static)!;
		var gated = new NocatFarm.Core.Bot("gated", new NocatFarm.Config.BotConfig { Notes = "0" });
		NocatFarm.Config.ConfigStore.SaveBot("gated", gated.Cfg);
		const int Rounds = 200;

		// The dashboard saving one field while 'set'/enable/a learned ban change another, all at once.
		Task pages = Task.Run(() => Parallel.For(0, Rounds, i =>
			saveBot.Invoke(null, [gated, new System.Text.Json.Nodes.JsonObject { ["CustomGameName"] = $"page-{i}" }, web])));
		Task edits = Task.Run(() => Parallel.For(0, Rounds, _ => {
			lock (gated.CfgGate) {
				gated.Cfg.Notes = (int.Parse(gated.Cfg.Notes) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
				NocatFarm.Config.ConfigStore.SaveBot("gated", gated.Cfg);
			}
		}));
		await Task.WhenAll(pages, edits);
		NocatFarm.Config.BotConfig? onDisk = NocatFarm.Config.ConfigStore.LoadBot("gated");
		Check("account lock: 200 changes made beside 200 dashboard saves are all still there, in memory", gated.Cfg.Notes == "200", gated.Cfg.Notes);
		Check("account lock: ...and on disk", onDisk?.Notes == "200", onDisk?.Notes ?? "no file");
		Check("account lock: the dashboard's own change landed, and disk says what memory says",
			gated.Cfg.CustomGameName.StartsWith("page-", StringComparison.Ordinal) && (onDisk?.CustomGameName == gated.Cfg.CustomGameName), $"{gated.Cfg.CustomGameName} / {onDisk?.CustomGameName}");

		string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Web", "WebHost.cs"));
		string cmds = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Commands.cs"));
		Check("account lock: the dashboard's save writes onto the live settings under it, not a swapped-in copy",
			host.Contains("lock (bot.CfgGate) {", StringComparison.Ordinal) && host.Contains("CopyChanged(body, current, bot.Cfg);", StringComparison.Ordinal)
			&& !host.Contains("bot.Reconfigure(body);", StringComparison.Ordinal));
		Check("account lock: enable, play, name, persona and 'set' change and save under it",
			System.Text.RegularExpressions.Regex.Matches(cmds, @"lock \(bot\.CfgGate\) \{").Count >= 5);

		// 'reload' / an import reading every file while an account's settings are being changed and saved.
		var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false });
		await mgr.AddAsync("synced", new NocatFarm.Config.BotConfig { Enabled = false, Notes = "0" });
		using (CancellationTokenSource stop = new()) {
			Task reloads = Task.Run(async () => {
				while (!stop.IsCancellationRequested) {
					await mgr.SyncFromDiskAsync();
				}
			});
			Task changes = Task.Run(() => {
				for (int i = 0; i < 150; i++) {
					NocatFarm.Core.Bot b = mgr.Get("synced")!;
					lock (b.CfgGate) {
						b.Cfg.Notes = (int.Parse(b.Cfg.Notes) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
						NocatFarm.Config.ConfigStore.SaveBot(b.Name, b.Cfg);
					}
				}
			});
			await changes;
			stop.Cancel();
			await reloads;
		}
		Check("reload: 150 changes made while the files are read again and again - none put back by an older read",
			(mgr.Get("synced")?.Cfg.Notes == "150") && (NocatFarm.Config.ConfigStore.LoadBot("synced")?.Notes == "150"), mgr.Get("synced")?.Cfg.Notes ?? "gone");
		string idler = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Config", "Import", "IdlerImport.cs")).Replace("\r\n", "\n");
		Check("import: every account's setting is read again, changed and saved under that account's lock",
			idler.Contains("lock (BotManager.GateFor(name)) {\n\t\t\t\t\tif (ConfigStore.LoadBot(name) is { } cfg) {", StringComparison.Ordinal));

		// Remove, add and reload of one name all at once: an account is running exactly when its file is there.
		int mismatched = 0;
		for (int i = 0; i < 25; i++) {
			await mgr.AddAsync("churn", new NocatFarm.Config.BotConfig { Enabled = false });
			await Task.WhenAll(Task.Run(() => mgr.RemoveAsync("churn")), Task.Run(() => mgr.AddAsync("churn", new NocatFarm.Config.BotConfig { Enabled = false })),
				Task.Run(() => mgr.SyncFromDiskAsync()));
			bool running = mgr.Get("churn") != null, file = File.Exists(Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "churn.json"));
			mismatched += running == file ? 0 : 1;
			await mgr.RemoveAsync("churn");
		}
		Check("remove: never an account left running with its file deleted (or a file with nothing running it)", mismatched == 0, $"{mismatched} of 25");
		await mgr.RemoveAsync("synced");
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		NocatFarm.Config.Live.Global = realGlobal;

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// a temp folder - left for the OS
		}
	}
}

// ── races: an import's global blacklist is a new list, never added to in place ───────────────────────────────────
{
	string asfDir = Path.Combine(Path.GetTempPath(), "nf-asfbl-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(asfDir);

	try {
		File.WriteAllText(Path.Combine(asfDir, "ASF.json"), "{\"Blacklist\":[10,20]}");
		NocatFarm.Config.ImportSetting? blacklist = NocatFarm.Config.AsfImport.Read(asfDir).Settings.FirstOrDefault(static s => s.Name == "GlobalBlacklistedGames");
		var g = new NocatFarm.Config.GlobalConfig { GlobalBlacklistedGames = [10] };
		List<uint> before = g.GlobalBlacklistedGames;
		bool blThrew = false;

		// A save serialising the live list over and over while the import adds to it.
		using (CancellationTokenSource stop = new()) {
			Task reader = Task.Run(() => {
				while (!stop.IsCancellationRequested) {
					try {
						_ = System.Text.Json.JsonSerializer.Serialize(g);
					} catch (InvalidOperationException) {
						blThrew = true;
					}
				}
			});
			blacklist?.ToGlobal?.Invoke(g);
			stop.Cancel();
			await reader;
		}
		Check("import blacklist: the live list is replaced, not added to while a save reads it",
			(blacklist != null) && !ReferenceEquals(before, g.GlobalBlacklistedGames) && before.SequenceEqual([10u]) && g.GlobalBlacklistedGames.SequenceEqual([10u, 20u]) && !blThrew,
			string.Join(",", g.GlobalBlacklistedGames));
		string single = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Config", "Import", "SingleBoostrImport.cs"));
		Check("import blacklist: SingleBoostr's too", single.Contains("g.GlobalBlacklistedGames = [.. g.GlobalBlacklistedGames,", StringComparison.Ordinal)
			&& !single.Contains("GlobalBlacklistedGames.Add(", StringComparison.Ordinal));
	} finally {
		try {
			Directory.Delete(asfDir, true);
		} catch (IOException) {
			// a temp folder - left for the OS
		}
	}
}

// ── races: sign-in guesses sent all at once - each takes its place in the count before the password is compared ──
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig realGlobal = NocatFarm.Config.Live.Global;
	PropertyInfo currentWeb = typeof(NocatFarm.Web.WebHost).GetProperty("Current")!;
	object? realWeb = currentWeb.GetValue(null);
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-burst-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		const string Right = "burst-right-password-1234";
		var mgr = new NocatFarm.Core.BotManager(new NocatFarm.Config.GlobalConfig { WebEnabled = false, WebPassword = Right, WebRemoteOffAfter = 0, WebSignInCode = false });
		var web = new NocatFarm.Web.WebHost(mgr, mgr.Global);
		const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;
		MethodInfo tryLogin = typeof(NocatFarm.Web.WebHost).GetMethod("TryLogin", Inst)!;
		var failures = (System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime Until)>) typeof(NocatFarm.Web.WebHost).GetField("_failures", Inst)!.GetValue(web)!;
		var sessions = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>) typeof(NocatFarm.Web.WebHost).GetField("_sessions", Inst)!.GetValue(web)!;
		bool Guess(string from, string password) {
			var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
			ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(from);

			return (bool) tryLogin.Invoke(web, [ctx, password, null, null, null])!;
		}

		Parallel.For(0, 60, _ => Guess("127.0.0.1", "wrong"));
		Check("sign-in burst: 60 wrong passwords at once from one address - only 5 were tried", failures.TryGetValue("127.0.0.1", out var one) && (one.Count == 5), $"{one.Count}");
		Check("sign-in burst: ...and the right one waits out the lockout like any other", !Guess("127.0.0.1", Right) && sessions.IsEmpty);
		web.ClearLockouts();
		Check("sign-in burst: unlocked, the right one signs in", Guess("127.0.0.1", Right) && (sessions.Count == 1) && !failures.ContainsKey("127.0.0.1"));

		Parallel.For(0, 60, i => Guess($"203.0.113.{i + 1}", "wrong"));
		int tried = failures.Keys.Count(static k => k.StartsWith("203.0.113.", StringComparison.Ordinal));
		Check("internet brake: 60 wrong passwords at once from 60 addresses - no more than 10 were tried", (tried > 0) && (tried <= 10), $"{tried}");
		Check("internet brake: ...and it's on", !Guess("203.0.113.200", Right));
		web.ClearLockouts();

		// The code step: its try is counted before the code is compared, as a password's is.
		string src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Web", "WebHost.cs"));
		int code = src.IndexOf("app.MapPost(\"/api/login/code\"", StringComparison.Ordinal);
		Check("sign-in code: its place in the lockout and brake is taken before the code is compared",
			(code > 0) && (src.IndexOf("Reserve(ip, thisPc, !thisPc && IsInternet(ip), device)", code, StringComparison.Ordinal) is > 0 and int reserved)
			&& (reserved < src.IndexOf("CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed.PadRight(6))", code, StringComparison.Ordinal)));

		// A password change while sign-ins with the old one are half way: none of them keeps a session.
		MethodInfo keyOf = typeof(NocatFarm.Web.WebHost).GetMethod("KeyOf", BindingFlags.NonPublic | BindingFlags.Static)!;
		MethodInfo newSession = typeof(NocatFarm.Web.WebHost).GetMethod("NewSession", Inst)!;
		web.SignOutAll();
		mgr.Global.WebPassword = "burst-second-password-5678";
		string? stale = (string?) newSession.Invoke(web, [new Microsoft.AspNetCore.Http.DefaultHttpContext(), keyOf.Invoke(null, [Right])]);
		Check("password change: a sign-in checked against the old password gets no session", (stale == null) && sessions.IsEmpty, $"{sessions.Count}");
		Check("password change: ...one checked against the new one does",
			newSession.Invoke(web, [new Microsoft.AspNetCore.Http.DefaultHttpContext(), keyOf.Invoke(null, ["burst-second-password-5678"])]) is string && (sessions.Count == 1));

		mgr.Global.WebPassword = Right;
		web.SignOutAll();
		web.ClearLockouts();
		int survivors = 0;
		for (int round = 0; round < 20; round++) {
			mgr.Global.WebPassword = Right;
			web.ClearLockouts();
			Task logins = Task.Run(() => Parallel.For(0, 8, i => Guess($"127.0.0.{i + 2}", Right)));
			Task change = Task.Run(() => {
				Thread.SpinWait(2000 * round);
				mgr.Global.WebPassword = "burst-changed-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture);
				web.SignOutAll();
			});
			await Task.WhenAll(logins, change);
			survivors += sessions.Count;
			web.SignOutAll();
		}
		Check("password change: sign-ins with the old password racing the change - not one session outlives it", survivors == 0, $"{survivors}");
	} finally {
		currentWeb.SetValue(null, realWeb);
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		NocatFarm.Config.Live.Global = realGlobal;

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// a temp folder - left for the OS
		}
	}
}

// ── races: a restore's files aren't saved over while it puts them back, and the accounts always start again ──────
{
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-restorehold-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);

	try {
		NocatFarm.Config.ConfigStore.SaveBot("held", new NocatFarm.Config.BotConfig { Notes = "restored" });
		TaskCompletionSource begun = new(TaskCreationOptions.RunContinuationsAsynchronously), done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		bool restorerSaves = false;
		Task restore = Task.Run(async () => {
			using (NocatFarm.Config.ConfigStore.BeginRestore()) {
				restorerSaves = NocatFarm.Config.ConfigStore.SaveBot("held", new NocatFarm.Config.BotConfig { Notes = "restored" });
				begun.SetResult();
				await done.Task;
			}
		});
		await begun.Task;
		bool[] others = new bool[16];
		Parallel.For(0, others.Length, i => others[i] = NocatFarm.Config.ConfigStore.SaveBot("held", new NocatFarm.Config.BotConfig { Notes = "old" })
			|| NocatFarm.Config.ConfigStore.SaveGlobal(new NocatFarm.Config.GlobalConfig()));
		Check("restore: while it writes, every other save is refused and the restored file stays",
			restorerSaves && others.All(static o => !o) && (NocatFarm.Config.ConfigStore.LoadBot("held")?.Notes == "restored"));
		done.SetResult();
		await restore;
		Check("restore: once it's done, saves work again", NocatFarm.Config.ConfigStore.SaveBot("held", new NocatFarm.Config.BotConfig { Notes = "after" })
			&& (NocatFarm.Config.ConfigStore.LoadBot("held")?.Notes == "after"));

		string bk = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "Backup.cs")).Replace("\r\n", "\n");
		int restoreAt = bk.IndexOf("public static async Task<string> RestoreAsync(", StringComparison.Ordinal);
		int beginAt = bk.IndexOf("using (ConfigStore.BeginRestore()) {", restoreAt, StringComparison.Ordinal);
		Check("restore: the files are written, read again and the accounts rebuilt inside the hold",
			(beginAt > 0) && (bk.IndexOf("written = WriteFiles(bytes);", restoreAt, StringComparison.Ordinal) > beginAt)
			&& (bk.IndexOf("await mgr.ReplaceAllFromDiskAsync().ConfigureAwait(false);\n\t\t\t\t\treplaced = true;", restoreAt, StringComparison.Ordinal) > beginAt));
		Check("restore: the accounts start again whatever happened (in a finally), after a failed one reads the files again",
			bk.Contains("} finally {\n\t\t\t\t// Whatever happened, the accounts come back", StringComparison.Ordinal)
			&& bk.Contains("if (wrote && !replaced) {", StringComparison.Ordinal) && bk.Contains("\t\t\t\t_ = mgr.StartAllAsync();\n\t\t\t}", StringComparison.Ordinal));
		Check("restore: History is read again after the files are written", bk.IndexOf("History.Reload();", restoreAt, StringComparison.Ordinal) > bk.IndexOf("written = WriteFiles(bytes);", restoreAt, StringComparison.Ordinal));

		// Two backups into the folder at once: each its own temporary file.
		string[] paths = new string[6];
		Exception? backupFailed = null;
		Parallel.For(0, paths.Length, i => {
			try {
				paths[i] = NocatFarm.Core.Backup.WriteToFolder("-race");
			} catch (Exception e) {
				backupFailed = e;
			}
		});
		bool allLanded = (backupFailed == null) && paths.All(File.Exists) && (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length)
			&& !Directory.GetFiles(NocatFarm.Core.Backup.Folder, "*.tmp").Any();
		Check("backup: six written at once all land, each under its own name, none trips over another's temporary file", allLanded,
			allLanded ? "" : backupFailed?.ToString() ?? $"returned {string.Join(", ", paths.Select(static p => Path.GetFileName(p ?? "(null)")))}; in the folder {string.Join(", ", Directory.GetFiles(NocatFarm.Core.Backup.Folder).Select(Path.GetFileName))}");
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch (IOException) {
			// a temp folder - left for the OS
		}
	}
}

// ── races: the live global settings - plugin switches, 'reload', 'set', rep4rep's token, the points, the catalogue ──
{
	string host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Web", "WebHost.cs")).Replace("\r\n", "\n");
	string cmds = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Commands.cs")).Replace("\r\n", "\n");
	string catalog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "GameCatalog.cs")).Replace("\r\n", "\n");
	Check("plugins: two switches at once - the list is copied, changed and put back under the settings lock",
		host.Contains("lock (GlobalSaveGate) {\n\t\t\t\tList<string> off = [.. Live.Global.DisabledPlugins];", StringComparison.Ordinal)
		&& host.Contains("private static Lock GlobalSaveGate => ConfigStore.GlobalEditGate;", StringComparison.Ordinal));
	Check("reload: the settings are read and swapped under the same lock as a dashboard save",
		cmds.Contains("lock (ConfigStore.GlobalEditGate) {\n\t\t\tpasswordBefore = mgr.Global.WebPassword;\n\t\t\tGlobalConfig loaded = ConfigStore.LoadGlobal();", StringComparison.Ordinal)
		&& host.Contains("lock (GlobalSaveGate) {\n\t\t\t\t\tsave = SaveFromPage(_mgr.Global, sent, JsonOptionsOf(ctx));", StringComparison.Ordinal));
	Check("set: a global setting is changed and saved under that lock too",
		cmds.Contains("lock (ConfigStore.GlobalEditGate) {\n\t\t\tpasswordBefore = mgr.Global.WebPassword;\n\t\t\tstring? failure = Settings.Apply(mgr.Global,", StringComparison.Ordinal));
	int trialAt = host.IndexOf("user = await trial.GetUserAsync(ctx.RequestAborted)", StringComparison.Ordinal);
	Check("rep4rep token: tried on a client of its own, put on the shared one only once it's good",
		(trialAt > 0) && (host.IndexOf("_mgr.Rep4Rep.Token = token;", StringComparison.Ordinal) > trialAt) && !host.Contains("_mgr.Rep4Rep.Token = previous;", StringComparison.Ordinal));
	FieldInfo points = typeof(NocatFarm.Web.WebHost).GetField("_pointsCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
	Check("rep4rep points: the cache is one object swapped whole, never a tuple read half written",
		points.FieldType.IsClass && !points.FieldType.IsGenericType && host.Contains("_pointsCache = new PointsSnapshot(points, pending, DateTime.UtcNow);", StringComparison.Ordinal));
	Check("game catalogue: the way-out save and a lookup's save take turns, the copy taken inside",
		catalog.Contains("await SaveGate.WaitAsync().ConfigureAwait(false);\n\n\t\ttry {\n\t\t\tDictionary<uint, Entry> snapshot;", StringComparison.Ordinal));
}

// ── closing stops an update: every way out raises "closing" first, and the updater reads it ──────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	string Src(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", rel)).Replace("\r\n", "\n");
	FieldInfo exitFlag = typeof(Commands).GetField("_exitRequested", S)!;
	Action? handlerBefore = Commands.ExitHandler;
	bool flagSeenByHandler = false;
	int handled = 0;

	try {
		exitFlag.SetValue(null, false);
		Commands.ExitHandler = () => {
			flagSeenByHandler = Commands.ExitRequested;
			handled++;
		};
		Commands.RequestExit();
		Check("exit: the 'closing' flag is up BEFORE the host is told to cancel", flagSeenByHandler && Commands.ExitRequested && (handled == 1));
		Commands.RequestExit();
		Check("exit: asking twice is harmless", Commands.ExitRequested && (handled == 2));

		// The update's last step refuses once closing: no swap started, so nothing installs and restarts after the quit.
		bool started = false;
		bool handed = (bool) typeof(NocatFarm.Core.SelfUpdate).GetMethod("HandOver", S)!.Invoke(null, ["v99.0.0", (Action) (() => started = true)])!;
		Check("update: the handover to the swap script is refused while closing", !handed && !started);

		// Signing out one by one stops at once rather than counting down for ten seconds and more.
		bool quietBefore = NocatFarm.Log.Suppressed;
		NocatFarm.Log.Suppressed = true;
		var sw = System.Diagnostics.Stopwatch.StartNew();
		await (Task) typeof(NocatFarm.Core.SelfUpdate).GetMethod("SignOutOneByOneAsync", S)!.Invoke(null, ["v99.0.0", new List<NocatFarm.Core.Bot>(), CancellationToken.None])!;
		NocatFarm.Log.Suppressed = quietBefore;
		Check("update: the sign-out countdown stops as soon as the app is closing", sw.Elapsed < TimeSpan.FromSeconds(2), $"{sw.Elapsed.TotalSeconds:0.0}s");

		// Closing is never "all clear" for an install by itself (the manager isn't even looked at).
		var quiet = ((bool Quiet, string Why)) typeof(NocatFarm.Core.UpdateCheck).GetMethod("Quiet", S)!.Invoke(null, [null, null])!;
		Check("update: a closing app is never a quiet moment to install", !quiet.Quiet && (quiet.Why == "nocat.farm is closing"), quiet.Why);

		// No update timer is started once closing, and Stop takes one away.
		FieldInfo timerField = typeof(NocatFarm.Core.UpdateCheck).GetField("_timer", S)!;
		NocatFarm.Core.UpdateCheck.Start(null!);
		Check("update: no update timer is started once closing", timerField.GetValue(null) == null);
		exitFlag.SetValue(null, false);
		NocatFarm.Core.UpdateCheck.Start(null!);
		bool made = timerField.GetValue(null) != null;
		NocatFarm.Core.UpdateCheck.Stop();
		Check("update: Stop disposes the update timer at shutdown", made && (timerField.GetValue(null) == null));
	} finally {
		Commands.ExitHandler = handlerBefore;
		exitFlag.SetValue(null, false);
		typeof(NocatFarm.Core.UpdateCheck).GetField("_timer", S)!.SetValue(null, null);
	}

	string prog = Src("Program.cs");
	Check("exit: tray, Ctrl+C, SIGTERM, ProcessExit, the window and 'all finished' all go through Quit",
		prog.Contains("\tctx.Cancel = true;\n\tQuit();", StringComparison.Ordinal) && prog.Contains("\te.Cancel = true;\n\tQuit();", StringComparison.Ordinal)
		&& prog.Contains("AppDomain.CurrentDomain.ProcessExit += (_, _) => Quit();", StringComparison.Ordinal)
		&& prog.Contains("new MainWindow(manager, () => web?.Url ?? \"\", Quit);", StringComparison.Ordinal)
		&& prog.Contains("() => _ = mgr.StopAllAsync(),\n\t\tQuit)", StringComparison.Ordinal)
		&& prog.Contains("closing down as configured\");\n\t\t\tQuit();", StringComparison.Ordinal)
		&& prog.Contains("static void Quit() => Commands.RequestExit();", StringComparison.Ordinal));
	Check("exit: nothing cancels the shutdown token without the flag (Register), and only ExitHandler cancels it directly",
		prog.Contains("shutdown.Token.Register(static () => Commands.RequestExit());", StringComparison.Ordinal)
		&& (System.Text.RegularExpressions.Regex.Matches(prog, @"shutdown\.Cancel(Async)?\(").Count == 1));
	Check("exit: the update timer is stopped on the way out", prog.Contains("Log.Info(\"shutting down...\");\n\n// No more update looks, reminders or installs by itself from here - the timer is gone, not just ignored.\nNocatFarm.Core.UpdateCheck.Stop();", StringComparison.Ordinal));
	Check("exit: closed while accounts start, the timers, the port forward and the browser tab aren't started",
		prog.Contains("if (!shutdown.IsCancellationRequested) {\n\t// Once-a-day", StringComparison.Ordinal) && prog.Contains("!NocatFarm.Core.SelfUpdate.OnTrial && !shutdown.IsCancellationRequested) {", StringComparison.Ordinal));
}

// ── the way out waits (a little) for replies on their way - "/exit confirm" gets its answer ─────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type notifier = typeof(NocatFarm.Core.Notifier);
	var tgLane = (NocatFarm.Core.CommandLane) notifier.GetField("TelegramLane", S)!.GetValue(null)!;
	var dcLane = (NocatFarm.Core.CommandLane) notifier.GetField("DiscordLane", S)!.GetValue(null)!;
	Func<TimeSpan, Task<bool>> settle = within => (Task<bool>) notifier.GetMethod("SettleAsync", S)!.Invoke(null, [within])!;

	// A reply on its way (the exit command's own): the way out waits for it, and goes the moment it's sent.
	var replying = tgLane.Take();
	Task stopping = NocatFarm.Core.Notifier.StopAsync();
	await Task.Delay(400);
	bool heldBack = !stopping.IsCompleted;
	var sw = System.Diagnostics.Stopwatch.StartNew();
	replying.Done();
	await stopping.WaitAsync(TimeSpan.FromSeconds(3));
	Check("notifications: closing waits for a command's reply still being sent", heldBack && (sw.Elapsed < TimeSpan.FromSeconds(1)), $"{sw.ElapsedMilliseconds}ms after the reply");

	// One that never finishes (a long '/start all') holds it up for a few seconds at most.
	var stuck = dcLane.Take();
	bool settled = await settle(TimeSpan.FromMilliseconds(200));
	stuck.Done();
	Check("notifications: a command that goes on doesn't hold closing up for ever", !settled && await settle(TimeSpan.FromSeconds(1)));

	// A batch taken off the queue counts too, until it has gone.
	FieldInfo sending = notifier.GetField("_sending", S)!;
	sending.SetValue(null, 1);
	bool waitsForBatch = !await settle(TimeSpan.FromMilliseconds(150));
	sending.SetValue(null, 0);
	Check("notifications: closing waits for a batch that is mid-send", waitsForBatch && await settle(TimeSpan.FromSeconds(1)));

	string nt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "Notifier.cs")).Replace("\r\n", "\n");
	Check("notifications: the cancel comes after the wait, and every flush is counted", nt.Contains("await SettleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);\n\n\t\t_stop?.Cancel();", StringComparison.Ordinal)
		&& nt.Contains("Interlocked.Increment(ref _sending);\n\n\t\ttry {\n\t\t\tawait SendBatchAsync(ct)", StringComparison.Ordinal));
}

// ── Rich Presence stays down once stopped, even when a connect was half-way ───────────────────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	Type dp = typeof(NocatFarm.Core.DiscordPresence);
	FieldInfo stoppedField = dp.GetField("_stopped", S)!;
	Func<string, Task<bool>> connect = path => (Task<bool>) dp.GetMethod("ConnectSocketAsync", S)!.Invoke(null, [path])!;
	bool HasConnection() => (bool) dp.GetProperty("HasConnection", S)!.GetValue(null)!;

	static byte[] Frame(int op, string json) {
		byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
		byte[] f = new byte[8 + body.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(f, op);
		System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(4), body.Length);
		body.CopyTo(f, 8);
		return f;
	}

	static async Task<string> ReadFrame(System.Net.Sockets.NetworkStream s) {
		byte[] h = new byte[8];
		await s.ReadExactlyAsync(h);
		byte[] b = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(4))];
		await s.ReadExactlyAsync(b);
		return System.Text.Encoding.UTF8.GetString(b);
	}

	// A stand-in Discord on a local socket: takes the handshake, answers READY when told to, then records what it's sent.
	async Task<(bool Connected, bool Kept, string Heard)> Run(bool stopMidway) {
		string path = Path.Combine(Path.GetTempPath(), "nf-ipc-" + Guid.NewGuid().ToString("N")[..8]);
		using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
		listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
		listener.Listen(1);
		TaskCompletionSource handshake = new(), answer = new();
		string heard = "";

		Task server = Task.Run(async () => {
			using var conn = await listener.AcceptAsync();
			using var s = new System.Net.Sockets.NetworkStream(conn, true);
			await ReadFrame(s);
			handshake.TrySetResult();
			await answer.Task;
			await s.WriteAsync(Frame(1, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}"));

			try {
				heard = await ReadFrame(s).WaitAsync(TimeSpan.FromSeconds(3));
			} catch (Exception) {
				// closed without a word
			}
		});

		Task<bool> connecting = connect(path);
		await handshake.Task.WaitAsync(TimeSpan.FromSeconds(3));

		if (stopMidway) {
			NocatFarm.Core.DiscordPresence.Stop();   // closing, while the handshake waits for READY
		}

		answer.TrySetResult();
		bool connected = await connecting.WaitAsync(TimeSpan.FromSeconds(6));
		bool kept = HasConnection();

		if (!stopMidway) {
			NocatFarm.Core.DiscordPresence.Stop();   // closing with the card up: it's taken down
		}

		await server.WaitAsync(TimeSpan.FromSeconds(6));

		try {
			File.Delete(path);
		} catch (IOException) {
			// the socket file goes with the temp folder
		}

		return (connected, kept, heard);
	}

	try {
		stoppedField.SetValue(null, false);
		var normal = await Run(stopMidway: false);
		Check("presence: a connect that finishes becomes the connection; stopping clears the card and closes it",
			normal.Connected && normal.Kept && !HasConnection() && normal.Heard.Contains("\"activity\":null", StringComparison.Ordinal), normal.Heard);
		stoppedField.SetValue(null, false);
		var midway = await Run(stopMidway: true);
		Check("presence: stopped while connecting, the connection is closed, not kept - no card after shutdown",
			!midway.Connected && !midway.Kept && !HasConnection() && !midway.Heard.Contains("SET_ACTIVITY", StringComparison.Ordinal), midway.Heard);
	} catch (Exception e) {
		Check("presence: the stand-in Discord ran", false, e.GetType().Name + ": " + e.Message);
	} finally {
		stoppedField.SetValue(null, false);
	}
}

// ── chat commands run in the order they were sent - a slow one doesn't hold the rest up ────────────────────────────
{
	List<string> order = [];
	void Did(string what) { lock (order) { order.Add(what); } }

	// "/pause kylro" (a moment's work) then "/resume kylro" (instant): resume can't overtake pause.
	var lane = new NocatFarm.Core.CommandLane(TimeSpan.FromSeconds(3));
	var pause = lane.Take();
	var resume = lane.Take();
	Task tResume = Task.Run(async () => { await resume.WaitTurnAsync(); Did("resume"); resume.Done(); });
	Task tPause = Task.Run(async () => { await pause.WaitTurnAsync(); await Task.Delay(300); Did("pause"); pause.Done(); });
	bool twoPending = lane.Pending == 2;
	await Task.WhenAll(tPause, tResume).WaitAsync(TimeSpan.FromSeconds(5));
	Check("chat: two commands run in the order sent, whichever thread gets going first", string.Join(",", order) == "pause,resume" && twoPending && (lane.Pending == 0), string.Join(",", order));

	// '/start all' (minutes) then '/stop all': the stop goes after a short wait, not after the start finishes.
	order.Clear();
	var quick = new NocatFarm.Core.CommandLane(TimeSpan.FromMilliseconds(300));
	TaskCompletionSource startAllMayEnd = new();
	Task tStart = quick.RunAsync(async () => { await startAllMayEnd.Task; Did("start all"); });
	var sw = System.Diagnostics.Stopwatch.StartNew();
	await quick.RunAsync(() => { Did("stop all"); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(3));
	TimeSpan stopAfter = sw.Elapsed;
	startAllMayEnd.SetResult();
	await tStart;
	Check("chat: a long command lets the next one go after a few seconds", (string.Join(",", order) == "stop all,start all") && (stopAfter < TimeSpan.FromSeconds(2)), $"{stopAfter.TotalMilliseconds:0}ms");

	// One turned away without running (not the owner) doesn't let the one after it jump the one before it.
	order.Clear();
	var third = new NocatFarm.Core.CommandLane(TimeSpan.FromSeconds(3));
	var a = third.Take();
	var refused = third.Take();
	var c = third.Take();
	refused.Done();
	Task tC = Task.Run(async () => { await c.WaitTurnAsync(); Did("c"); c.Done(); });
	await Task.Delay(150);
	Did("a");
	a.Done();
	await tC.WaitAsync(TimeSpan.FromSeconds(3));
	Check("chat: a command turned away keeps the rest in order", string.Join(",", order) == "a,c", string.Join(",", order));

	string tg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "TelegramCommands.cs")).Replace("\r\n", "\n");
	string dc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "DiscordBot.cs")).Replace("\r\n", "\n");
	Check("chat: Telegram takes each command's place as it's read, and /console is toggled in turn",
		tg.Contains("CommandLane.Slot slot = TelegramLane.Take();\n\n\t\t\t\t\t\t_ = Task.Run(async () => {\n\t\t\t\t\t\t\ttry {\n\t\t\t\t\t\t\t\tawait slot.WaitTurnAsync()", StringComparison.Ordinal)
		&& tg.Contains("private static volatile bool _consoleMode;", StringComparison.Ordinal));
	Check("chat: Discord says 'thinking' at once, then runs the command in its turn",
		dc.Contains("CommandLane.Slot slot = DiscordLane.Take();\n\t\t\t\t_ = Task.Run(() => OnDiscordInteractionAsync(copy, slot, ct)", StringComparison.Ordinal)
		&& (dc.IndexOf("new { type = 5, data = new { flags } }", StringComparison.Ordinal) < dc.IndexOf("await slot.WaitTurnAsync()", StringComparison.Ordinal))
		&& dc.Contains("} finally {\n\t\t\t// Answered, turned away, or failed: the next command may go", StringComparison.Ordinal));
}

// ── the log file: switching file logging while lines are written never throws into the caller ─────────────────────
{
	string logRoot = Path.Combine(Path.GetTempPath(), "nf-lograce-" + Guid.NewGuid().ToString("N")[..8]);
	Directory.CreateDirectory(logRoot);
	string? folderBefore = NocatFarm.Log.Folder;
	bool debugBefore = NocatFarm.Log.DebugEnabled;
	bool suppressedBefore = NocatFarm.Log.Suppressed;
	NocatFarm.Log.Suppressed = true;
	int thrown = 0;
	string firstThrow = "";
	using CancellationTokenSource enough = new(TimeSpan.FromSeconds(1.5));

	Task[] writers = [.. Enumerable.Range(0, 4).Select(w => Task.Run(() => {
		while (!enough.IsCancellationRequested) {
			try {
				NocatFarm.Log.Debug($"log race {w}");
				NocatFarm.Log.StackToFile(new InvalidOperationException("log race"));
			} catch (Exception e) {
				if (Interlocked.Increment(ref thrown) == 1) {
					firstThrow = e.GetType().Name;
				}
			}
		}
	}))];

	Task toggler = Task.Run(() => {
		bool on = false;

		while (!enough.IsCancellationRequested) {
			NocatFarm.Log.Configure(on = !on, true, logRoot);
		}
	});

	await Task.WhenAll([.. writers, toggler]);
	NocatFarm.Log.Configure(folderBefore != null, debugBefore, folderBefore != null ? Path.GetDirectoryName(folderBefore)! : logRoot);
	NocatFarm.Log.Suppressed = suppressedBefore;
	Check("log: file logging switched on and off under four writers - no line throws into the caller", thrown == 0, $"{thrown} thrown, first {firstThrow}");

	try {
		Directory.Delete(logRoot, true);
	} catch (IOException) {
		// the temp folder has it
	}

	string lg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Log.cs")).Replace("\r\n", "\n");
	Check("log: today's file is read once into a local, and Configure changes it under the lock",
		lg.Contains("string? dir = _logDir;\n\n\t\tif (dir == null) {\n\t\t\treturn null;\n\t\t}\n\n\t\tDateTime today = DateTime.Now.Date;\n\t\tDayFile? known = _today;", StringComparison.Ordinal)
		&& lg.Contains("lock (FileLock) {\n\t\t\t\t_logDir = null;", StringComparison.Ordinal));
}

// ── the console board: backfill under the lock, the question on its own line, the start lines before it ────────────
{
	string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string lc = File.ReadAllText(Path.Combine(root, "Windows", "LiveConsole.cs")).Replace("\r\n", "\n");
	string prog = File.ReadAllText(Path.Combine(root, "Program.cs")).Replace("\r\n", "\n");
	Check("board: the lines it starts with are added under the lock the live lines take, in order",
		lc.Contains("lock (_recent) {\n\t\t\tHashSet<long> have = [.. _recent.Select(static e => e.Seq)];", StringComparison.Ordinal)
		&& lc.Contains("_recent.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));", StringComparison.Ordinal));
	Check("board: a waiting question is painted on its own line above the input, and repaints when it changes",
		lc.Contains("Prompt.Question? asked = Prompt.Current;", StringComparison.Ordinal) && lc.Contains("Prompt.Changed += Repaint;", StringComparison.Ordinal)
		&& lc.Contains("Prompt.Changed -= Repaint;", StringComparison.Ordinal));
	int readyAt = prog.IndexOf("\tReady(web?.Url, manager.All.Count);\n\n\tif (manager.All.Count == 0) {\n\t\tFirstRunHint(web?.Url);\n\t}\n\n\tboard = new LiveConsole(manager);\n\tboard.Start();", StringComparison.Ordinal);
	Check("board: the 'where everything is' block and the first-run hint are written before the board starts painting", readyAt > 0);
}

// ── a question: asked on the board or the console, answered only as itself ─────────────────────────────────────────
{
	bool suppressedBefore = NocatFarm.Log.Suppressed;
	TextWriter outBefore = Console.Out;
	StringWriter screen = new();
	// Said once the console is back - Check writes to it, and it's swapped for a stand-in screen below.
	List<(string Name, bool Ok, string Detail)> results = [];
	void Note(string name, bool ok, string detail = "") => results.Add((name, ok, detail));
	List<string?> seen = [];
	void Changed() { lock (seen) { seen.Add(Prompt.Current?.Text); } }
	Prompt.Changed += Changed;

	try {
		// The board owns the screen: the question goes to it, not straight onto the console where it's painted over.
		NocatFarm.Log.Suppressed = true;
		Console.SetOut(screen);
		Task<string> first = Prompt.LineAsync("[race] Steam Guard code (mobile app)", "race");
		await Task.Delay(100);
		Prompt.Question? q1 = Prompt.Current;
		Note("prompt: the board hears the question go up, and nothing is written under it", (q1?.Text == "[race] Steam Guard code (mobile app)")
			&& seen.Contains(q1!.Text) && !screen.ToString().Contains("Steam Guard", StringComparison.Ordinal), screen.ToString());

		// Answered from the dashboard; the line typed for it in the window arrives just after, when the next is up.
		Prompt.Answer("AB12C");
		string got1 = await first.WaitAsync(TimeSpan.FromSeconds(2));
		Task<string> second = Prompt.SecretAsync("[race] Steam password", "race2");
		await Task.Delay(100);
		bool staleTaken = Prompt.Answer(q1!, "AB12C");
		Prompt.Question? q2 = Prompt.Current;
		Note("prompt: a line typed for an answered question doesn't answer the next one", (got1 == "AB12C") && !staleTaken && !second.IsCompleted && (q2?.Secret == true));
		Prompt.Answer(q2!, "hunter2");
		Note("prompt: the next question gets its own answer", await second.WaitAsync(TimeSpan.FromSeconds(2)) == "hunter2");
		await Task.Delay(50);
		Note("prompt: the board hears it come down", (Prompt.Current == null) && (seen.LastOrDefault() == null));

		// Nothing else owns the screen (Linux, --no-gui with no board): straight onto the console, as before.
		NocatFarm.Log.Suppressed = false;
		Task<string> plain = Prompt.LineAsync("[race] plain question", "race3");
		await Task.Delay(100);
		Prompt.Answer("x");
		await plain.WaitAsync(TimeSpan.FromSeconds(2));
		Note("prompt: with no board or window the question is written to the console", screen.ToString().Contains("[race] plain question: ", StringComparison.Ordinal));
	} finally {
		Console.SetOut(outBefore);
		NocatFarm.Log.Suppressed = suppressedBefore;
		Prompt.Changed -= Changed;
	}

	foreach ((string name, bool ok, string detail) in results) {
		Check(name, ok, detail);
	}

	string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string mw = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.cs")).Replace("\r\n", "\n");
	string prog = File.ReadAllText(Path.Combine(root, "Program.cs")).Replace("\r\n", "\n");
	Check("prompt: the window and the console answer the question they read, not 'whatever is up now'",
		mw.Contains("Prompt.Question? question = Prompt.Current;", StringComparison.Ordinal) && mw.Contains("Prompt.Answer(question, line);", StringComparison.Ordinal)
		&& prog.Contains("Prompt.Answer(question, line);", StringComparison.Ordinal) && !mw.Contains("Prompt.Answer(line)", StringComparison.Ordinal));
}

// ── the tray: no icon means the window shows, whichever of the two threads gets there first ────────────────────────
{
	string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string prog = File.ReadAllText(Path.Combine(root, "Program.cs")).Replace("\r\n", "\n");
	string tray = File.ReadAllText(Path.Combine(root, "Windows", "TrayIcon.cs")).Replace("\r\n", "\n");
	string mw = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.cs")).Replace("\r\n", "\n");
	Check("tray: only the tray thread says whether there's an icon", !prog.Contains("Commands.TrayPresent", StringComparison.Ordinal));
	Check("tray: no icon (or no tray thread) says so, fences, then asks for the window",
		tray.Contains("Commands.TrayPresent = false;\n\t\tUnavailable = true;\n\t\tInterlocked.MemoryBarrier();\n\t\tCommands.Window?.ShowIfReady();", StringComparison.Ordinal)
		&& (System.Text.RegularExpressions.Regex.Matches(tray, @"\bNoIcon\(\);").Count == 3));
	Check("tray: the window says it's ready, fences, then shows itself if there's no icon",
		mw.Contains("_ready = true;\n\t\tInterlocked.MemoryBarrier();\n\n\t\tif (_showOnCreate || TrayIcon.Unavailable) {", StringComparison.Ordinal));
}

// ── languages: two quick changes end on the last one picked ─────────────────────────────────────────────────────────
{
	string webDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
	string langDir = Path.Combine(webDir, "lang");
	bool madeWeb = !Directory.Exists(webDir), madeLang = !Directory.Exists(langDir);
	string de = Path.Combine(langDir, "de.json"), fr = Path.Combine(langDir, "fr.json");
	bool hadDe = File.Exists(de), hadFr = File.Exists(fr);
	string? langBefore = NocatFarm.Config.Live.Global.Language;

	if (!hadDe && !hadFr) {
		Directory.CreateDirectory(langDir);
		// German slow to read (a big pack), French quick - so the German load is still going when French is picked.
		System.Text.StringBuilder big = new("{\"ui\":{\"loc race check\":\"DE\"");
		for (int i = 0; i < 300_000; i++) {
			big.Append(",\"filler ").Append(i).Append("\":\"x\"");
		}
		File.WriteAllText(de, big.Append("}}").ToString());
		File.WriteAllText(fr, "{\"ui\":{\"loc race check\":\"FR\"}}");
		int wrong = 0;

		try {
			for (int round = 0; round < 5; round++) {
				NocatFarm.Config.Live.Global.Language = "en";
				NocatFarm.Core.Loc.T("loc race check");
				NocatFarm.Config.Live.Global.Language = "de";
				Task german = Task.Run(() => NocatFarm.Core.Loc.T("loc race check"));
				await Task.Delay(15);
				NocatFarm.Config.Live.Global.Language = "fr";
				Task french = Task.Run(() => NocatFarm.Core.Loc.T("loc race check"));
				await Task.WhenAll(german, french);

				if (NocatFarm.Core.Loc.T("loc race check") != "FR") {
					wrong++;
				}
			}

			Check("language: German then French in quick succession ends in French, every time", wrong == 0, $"{wrong} of 5 rounds ended in the wrong language");
		} finally {
			NocatFarm.Config.Live.Global.Language = langBefore;
			File.Delete(de);
			File.Delete(fr);

			if (madeWeb) {
				Directory.Delete(webDir, true);
			} else if (madeLang) {
				Directory.Delete(langDir, true);
			}

			NocatFarm.Core.Loc.T("loc race check");
		}
	} else {
		Check("language: (skipped - real packs in the test folder)", true);
	}
}

// ── the rest: skip file, held-back update failure, "open from anywhere" pokes, the bedtime queue, the mini row ──────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");

	// 'update skip' from several places at once: what's remembered is what's in the file.
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmp = Path.Combine(Path.GetTempPath(), "nf-skip-" + Guid.NewGuid().ToString("N")[..8]);
	Directory.CreateDirectory(Path.Combine(tmp, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmp);
	FieldInfo skipRead = typeof(NocatFarm.Core.UpdateCheck).GetField("_skipRead", S)!;
	FieldInfo skipped = typeof(NocatFarm.Core.UpdateCheck).GetField("_skipped", S)!;
	object? skipBefore = skipped.GetValue(null);
	object? readBefore = skipRead.GetValue(null);
	int mismatched = 0;

	try {
		string file = Path.Combine(NocatFarm.Config.ConfigStore.ConfigDir, "state", "update-skip.txt");

		for (int round = 0; round < 40; round++) {
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, "1.0.0");
			skipRead.SetValue(null, false);
			Task[] all = [.. Enumerable.Range(0, 6).Select(i => Task.Run(() => {
				if ((i % 2) == 0) {
					_ = NocatFarm.Core.UpdateCheck.Skipped;
				} else {
					NocatFarm.Core.UpdateCheck.Skipped = $"1.{round}.{i}";
				}
			}))];
			await Task.WhenAll(all);

			if (NocatFarm.Core.UpdateCheck.Skipped != File.ReadAllText(file).Trim()) {
				mismatched++;
			}
		}
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);
		skipped.SetValue(null, skipBefore);
		skipRead.SetValue(null, readBefore);

		try {
			Directory.Delete(tmp, true);
		} catch (IOException) {
			// the temp folder has it
		}
	}

	Check("update skip: read and written from several threads, the skipped version is the one in the file", mismatched == 0, $"{mismatched} of 40 differed");

	// An update failure said just as Telegram and Discord start listening is sent, never kept back for ever.
	Type su = typeof(NocatFarm.Core.SelfUpdate);
	FieldInfo ready = su.GetField("_notifierReady", S)!, held = su.GetField("_heldBack", S)!;
	MethodInfo fail = su.GetMethod("Fail", S)!;
	object? readyBefore = ready.GetValue(null);
	bool suppressedBefore = NocatFarm.Log.Suppressed;
	NocatFarm.Log.Suppressed = true;
	int lost = 0;

	try {
		for (int round = 0; round < 300; round++) {
			ready.SetValue(null, false);
			held.SetValue(null, null);
			using Barrier go = new(2);
			Task a = Task.Run(() => { go.SignalAndWait(); fail.Invoke(null, [new NocatFarm.Core.Said("race test failure")]); });
			Task b = Task.Run(() => { go.SignalAndWait(); NocatFarm.Core.SelfUpdate.NotifierReady(); });
			await Task.WhenAll(a, b);

			if (held.GetValue(null) != null) {
				lost++;
			}
		}
	} finally {
		ready.SetValue(null, readyBefore);
		held.SetValue(null, null);
		su.GetProperty("LastFailure", S)!.SetValue(null, null);
		NocatFarm.Log.Suppressed = suppressedBefore;
	}

	Check("update: a failure landing as the notifier starts is never left held back", lost == 0, $"{lost} of 300 left behind");

	// A poke while a look is running is remembered, so that look goes round again with the new settings.
	Type ra = typeof(NocatFarm.Core.RemoteAccess);
	FieldInfo busy = ra.GetField("_busy", S)!, again = ra.GetField("_again", S)!;
	object? busyBefore = busy.GetValue(null);
	busy.SetValue(null, 1);
	again.SetValue(null, 0);
	NocatFarm.Core.RemoteAccess.Poke();
	bool remembered = (int) again.GetValue(null)! == 1;
	busy.SetValue(null, busyBefore);
	again.SetValue(null, 0);
	string ras = File.ReadAllText(Path.Combine(root, "Core", "RemoteAccess.cs")).Replace("\r\n", "\n");
	Check("anywhere: a poke during a look is kept, and the look goes round again", remembered
		&& ras.Contains("if (Interlocked.Exchange(ref _again, 0) == 0) {\n\t\t\t\treturn;", StringComparison.Ordinal));

	string uc = File.ReadAllText(Path.Combine(root, "Core", "UpdateCheck.cs")).Replace("\r\n", "\n");
	Check("update: a queued install stays queued until its attempt is over, and starts once",
		uc.Contains("Interlocked.CompareExchange(ref _queuedInstalling, 1, 0) != 0", StringComparison.Ordinal)
		&& uc.Contains("} finally {\n\t\t\t\t// Only the version this was for: an 'update accept' for a newer one while it ran stays queued.\n\t\t\t\tInterlocked.CompareExchange(ref _queued, null, tag);", StringComparison.Ordinal)
		&& !uc.Contains("Queued = null;\n\t\tLog.Good(new Said(\"installing {0} - all clear", StringComparison.Ordinal));

	string mw = File.ReadAllText(Path.Combine(root, "Windows", "MainWindow.cs")).Replace("\r\n", "\n");
	Check("mini window: what an account plays is read once per row", mw.Contains("IReadOnlyList<uint> playing = bot.PlayingApps;", StringComparison.Ordinal)
		&& !mw.Contains("GameNames.Of(bot.PlayingApps[0])", StringComparison.Ordinal));
}

// ── a restore isn't undone by a timer save; keys pasted in the last moments are saved on the way out ────────────────
{
	string core = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core");
	Check("restore: history, lifetime totals and the key queue hold off while a restore writes its files",
		new[] { "History.cs", "Lifetime.cs", "KeyQueue.cs" }.All(f => File.ReadAllText(Path.Combine(core, f)).Contains("if (ConfigStore.RestoreWriting) {", StringComparison.Ordinal)));
	Check("shutdown: the key queue is saved with everything else", File.ReadAllText(Path.Combine(core, "BotManager.cs")).Contains("KeyQueue.Flush();", StringComparison.Ordinal));
	Check("restore: nothing is being restored now", !NocatFarm.Config.ConfigStore.RestoreWriting);
}

// ── Linux, Mac and Docker: one copy per folder, restarts on the same folder, names, env vars, proxies, updates ────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-platform-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	string root = NocatFarm.Config.ConfigStore.Root;
	string srcDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string Src(string rel) => File.ReadAllText(Path.Combine(srcDir, rel)).Replace("\r\n", "\n");

	try {
		// One copy per folder: a second claim (from another thread, as a mutex is the owning thread's own) is refused while
		// the first holds it, and goes through once it's let go. Off Windows that's a locked config/state/instance.lock.
		// On a thread of its own, never Task.Run(...).Result: a task waited on can be run inline on the waiting thread, and a
		// mutex taken there is the same thread's own - the second claim then went through and the check failed now and then.
		T OnItsOwnThread<T>(Func<T> work) {
			T result = default!;
			Thread t = new(() => result = work());
			t.Start();
			t.Join();

			return result;
		}

		IDisposable? first = NocatFarm.Core.AppInstance.TryClaim(root);
		IDisposable? second = OnItsOwnThread(() => NocatFarm.Core.AppInstance.TryClaim(root));
		Check("one copy per folder: a second copy on the same folder is refused", (first != null) && (second == null));
		Check("one copy per folder: off Windows the lock file says which process has it",
			OperatingSystem.IsWindows() || (NocatFarm.Core.AppInstance.HolderPid(root) == Environment.ProcessId));
		Check("one copy per folder: another folder isn't affected", OnItsOwnThread(() => {
			string other = Path.Combine(tmpRoot, "other");
			using IDisposable? claim = NocatFarm.Core.AppInstance.TryClaim(other);
			return claim != null;
		}));
		first?.Dispose();
		IDisposable? third = OnItsOwnThread(() => NocatFarm.Core.AppInstance.TryClaim(root));
		Check("one copy per folder: let go when the first one closes", third != null);
		// A mutex has to be let go by the thread that took it, so the one taken on the other thread is simply closed.
		third?.Dispose();
		Check("one copy per folder: the old per-session named mutex is gone from startup",
			Src("Program.cs").Contains("using IDisposable? singleInstance = AppInstance.TryClaim(ConfigStore.Root);", StringComparison.Ordinal)
			&& !Src("Program.cs").Contains("using Mutex singleInstance", StringComparison.Ordinal));

		// The restarted copy: --path made absolute, whatever the case of the flag; everything else as it was.
		var restartArgs = typeof(NocatFarm.Core.SelfUpdate).GetMethod("RestartArgs", S)!;
		List<string> Restart(string[] a, string r) => (List<string>) restartArgs.Invoke(null, [a, r])!;
		Check("restart: a relative --path comes back as the absolute folder",
			Restart(["--no-gui", "--path", "data"], "/home/me/nocat.farm (1)/data").SequenceEqual(["--no-gui", "--path", "/home/me/nocat.farm (1)/data"]));
		Check("restart: --PATH too, and nothing else is changed",
			Restart(["--PATH", @"..\data", "--minimized"], @"C:\farm\data").SequenceEqual(["--PATH", @"C:\farm\data", "--minimized"])
			&& Restart(["--no-web"], "/x").SequenceEqual(["--no-web"]) && Restart(["--path"], "/x").SequenceEqual(["--path"]));
		Check("restart: Windows gets the same absolute --path (quoted), Linux and a Mac each argument as it is",
			Src("Core/SelfUpdate.cs").Contains("RestartArgs([.. Environment.GetCommandLineArgs().Skip(1)], ConfigStore.Root).Select(QuoteArg)", StringComparison.Ordinal)
			&& Src("Core/SelfUpdate.cs").Contains("foreach (string arg in RestartArgs([.. Environment.GetCommandLineArgs().Skip(1)], ConfigStore.Root)) {\n\t\t\t\t\tswap.ArgumentList.Add(arg);", StringComparison.Ordinal));

		string swap = (string) typeof(NocatFarm.Core.SelfUpdate).GetField("UnixSwapScript", S)!.GetValue(null)!;
		string detached = (string) typeof(NocatFarm.Core.SelfUpdate).GetField("DetachedStart", S)!.GetValue(null)!;
		Check("swap script: no pattern matching on the folder (pgrep -f / pkill -f), the new copy known by its pid",
			!swap.Contains("pgrep", StringComparison.Ordinal) && !swap.Contains("pkill", StringComparison.Ordinal)
			&& swap.Contains("echo $! > \"$NF_WORK/started.pid\"", StringComparison.Ordinal) && swap.Contains("kill -0 \"$(cat \"$NF_WORK/started.pid\")\"", StringComparison.Ordinal));
		Check("swap script: files go in by copy-then-rename, never overwritten in place",
			!swap.Contains("cp -Rf", StringComparison.Ordinal) && swap.Contains("mv -f \"$NF_HERE/$f.nf-new\" \"$NF_HERE/$f\"", StringComparison.Ordinal));
		Check("swap script: another copy running from the folder stops it before anything is touched",
			swap.IndexOf("echo \"busy $other\"", StringComparison.Ordinal) is > 0 and int busyAt && busyAt < swap.IndexOf("install_from \"$NF_STAGED\"", StringComparison.Ordinal));
		Check("swap script: started with the options as its own arguments, in a session of its own",
			swap.Contains("nohup \"$NF_HERE/nocatFarm\" \"$@\"", StringComparison.Ordinal) && !swap.Contains("$NF_ARGS", StringComparison.Ordinal)
			&& detached.Contains("setsid nohup /bin/sh \"$0\" \"$@\"", StringComparison.Ordinal) && detached.Contains("POSIX::setsid()", StringComparison.Ordinal));
		Check("update: the download folder is a fresh private one (mkdtemp), not a guessable name in /tmp",
			Src("Core/SelfUpdate.cs").Contains("work = Directory.CreateTempSubdirectory(\"nocatfarm-update-\").FullName;", StringComparison.Ordinal));
		Check("update: no other copy of nocat.farm is running from this test's folder", ((List<int>) typeof(NocatFarm.Core.SelfUpdate).GetMethod("OthersRunningHere", S)!.Invoke(null, [])!).Count == 0);

		// Names: one set of characters that can't be in one, on every system.
		Check("names: a : * ? \" < > | \\ / or a control character is refused on every system",
			new[] { "a:b", "why?", "st*r", "a\"b", "a<b", "a>b", "a|b", @"a\b", "a/b", "tab\there" }.All(static n => !NocatFarm.Config.ConfigStore.IsValidBotName(n)));
		Check("names: ordinary ones still pass", new[] { "main", "alt_2", "Old-Farm", "ünï" }.All(static n => NocatFarm.Config.ConfigStore.IsValidBotName(n)));

		// Environment variables: the public address and trusted proxies are applied and saved; a bad proxy list isn't.
		NocatFarm.Config.GlobalConfig g = new();
		Environment.SetEnvironmentVariable("NOCATFARM_PUBLIC_ADDRESS", " farm.example.com ");
		Environment.SetEnvironmentVariable("NOCATFARM_TRUSTED_PROXIES", "172.16.0.0/12, 10.0.0.5");
		NocatFarm.Core.Platform.ApplyEnvironment(g);
		Check("env: NOCATFARM_PUBLIC_ADDRESS sets the public address", g.WebPublicAddress == "farm.example.com", g.WebPublicAddress);
		Check("env: NOCATFARM_TRUSTED_PROXIES sets the trusted proxies", g.WebTrustedProxies == "172.16.0.0/12, 10.0.0.5", g.WebTrustedProxies);
		Check("env: and both are saved to the settings file", File.ReadAllText(NocatFarm.Config.ConfigStore.GlobalPath).Contains("farm.example.com", StringComparison.Ordinal)
			&& File.ReadAllText(NocatFarm.Config.ConfigStore.GlobalPath).Contains("172.16.0.0/12", StringComparison.Ordinal));
		NocatFarm.Config.GlobalConfig g2 = new();
		Environment.SetEnvironmentVariable("NOCATFARM_PUBLIC_ADDRESS", null);
		Environment.SetEnvironmentVariable("NOCATFARM_TRUSTED_PROXIES", "172.16.0.0/12, the-proxy");
		NocatFarm.Core.Platform.ApplyEnvironment(g2);
		Check("env: a proxy list with something unreadable in it is left out, not half-applied", g2.WebTrustedProxies == "" && g2.WebPublicAddress == "");
		Environment.SetEnvironmentVariable("NOCATFARM_TRUSTED_PROXIES", null);

		// Trusted proxies: addresses and ranges, host bits ignored, IPv4-mapped too; the setting refuses what it can't read.
		Check("proxies: a range with host bits set is the range", NocatFarm.Core.TrustedProxies.TryParse("172.18.0.1/16", out System.Net.IPNetwork n16) && n16.ToString() == "172.18.0.0/16", n16.ToString());
		Check("proxies: an address, a range, and an IPv4-mapped visitor all match",
			NocatFarm.Core.TrustedProxies.Contains("10.0.0.5 172.16.0.0/12", System.Net.IPAddress.Parse("172.20.3.4"))
			&& NocatFarm.Core.TrustedProxies.Contains("10.0.0.5;fd00::/8", System.Net.IPAddress.Parse("10.0.0.5"))
			&& NocatFarm.Core.TrustedProxies.Contains("172.16.0.0/12", System.Net.IPAddress.Parse("::ffff:172.17.0.1"))
			&& NocatFarm.Core.TrustedProxies.Contains("fd00::/8", System.Net.IPAddress.Parse("fd12::1")));
		Check("proxies: nothing listed trusts nothing; outside the range isn't trusted",
			!NocatFarm.Core.TrustedProxies.Contains("", System.Net.IPAddress.Parse("172.17.0.1"))
			&& !NocatFarm.Core.TrustedProxies.Contains("172.16.0.0/12", System.Net.IPAddress.Parse("192.168.1.1")));
		Check("proxies: an unreadable entry is named", NocatFarm.Core.TrustedProxies.Unreadable("172.16.0.0/12, bogus") == "bogus"
			&& NocatFarm.Core.TrustedProxies.Unreadable("1.2.3.4/33") == "1.2.3.4/33" && NocatFarm.Core.TrustedProxies.Unreadable("172.16.0.0/12 ::1") == null);
		NocatFarm.Config.SettingDef proxiesDef = NocatFarm.Config.Settings.FindGlobal("WebTrustedProxies")!;
		NocatFarm.Config.GlobalConfig g3 = new();
		Check("proxies: 'set WebTrustedProxies' refuses what isn't an address or range, and takes what is",
			(NocatFarm.Config.Settings.Apply(g3, proxiesDef, "not-an-ip") != null) && (g3.WebTrustedProxies == "")
			&& (NocatFarm.Config.Settings.Apply(g3, proxiesDef, "172.16.0.0/12") == null) && (g3.WebTrustedProxies == "172.16.0.0/12"));

		var whoFn = typeof(NocatFarm.Web.WebHost).GetMethod("WhoIsSigningIn", BindingFlags.NonPublic | BindingFlags.Static)!;
		(string Ip, bool ThisPc) Ask(string from, string? xff = null) {
			var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
			ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(from);
			if (xff != null) {
				ctx.Request.Headers["X-Forwarded-For"] = xff;
			}

			return ((string, bool)) whoFn.Invoke(null, [ctx])!;
		}

		string proxiesBefore = NocatFarm.Config.Live.Global.WebTrustedProxies;

		try {
			NocatFarm.Config.Live.Global.WebTrustedProxies = "";
			Check("proxies: untrusted, Docker's gateway is who asked - its X-Forwarded-For isn't believed",
				Ask("172.18.0.1", "134.41.250.88") == ("172.18.0.1", false));
			NocatFarm.Config.Live.Global.WebTrustedProxies = "172.16.0.0/12";
			Check("proxies: trusted, the visitor Caddy passes on is who asked - the internet, not home",
				Ask("172.18.0.1", "192.168.1.5, 134.41.250.88") == ("134.41.250.88", false) && NocatFarm.Web.WebHost.IsInternet(Ask("172.18.0.1", "134.41.250.88").Ip));
			Check("proxies: a trusted proxy is never 'this PC', even with nothing forwarded", Ask("172.18.0.1") == ("172.18.0.1", false));
			Check("proxies: loopback is still believed with none listed, and is still this PC without headers",
				Ask("127.0.0.1") == ("127.0.0.1", true) && Ask("127.0.0.1", "203.0.113.9") == ("203.0.113.9", false));
		} finally {
			NocatFarm.Config.Live.Global.WebTrustedProxies = proxiesBefore;
		}

		// Language: "pt-br" typed by hand is pt-BR, the pack's own file name, which off Windows is the only one that opens.
		File.WriteAllText(NocatFarm.Config.ConfigStore.GlobalPath, """{"Language":"pt-br"}""");
		Check("language: a code in the wrong case is read as the pack's own", NocatFarm.Config.ConfigStore.LoadGlobal().Language == "pt-BR");
		File.WriteAllText(NocatFarm.Config.ConfigStore.GlobalPath, """{"Language":"ZH-cn"}""");
		Check("language: ...zh-CN too", NocatFarm.Config.ConfigStore.LoadGlobal().Language == "zh-CN");

		// Backups: owner-only off Windows - the file, the folder, and what a restore writes.
		string backupFile = Path.Combine(tmpRoot, "b.zip");
		typeof(NocatFarm.Core.Backup).GetMethod("WriteOwnerOnly", S)!.Invoke(null, [backupFile, new byte[] { 1, 2, 3 }]);
		typeof(NocatFarm.Core.Backup).GetMethod("OwnerOnlyFolder", S)!.Invoke(null, [Path.Combine(tmpRoot, "backups")]);
		Check("backups: the zip is 0600 and the folder 0700 off Windows", OperatingSystem.IsWindows()
			|| ((File.GetUnixFileMode(backupFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite))
				&& (File.GetUnixFileMode(Path.Combine(tmpRoot, "backups")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))),
			OperatingSystem.IsWindows() ? "" : File.GetUnixFileMode(backupFile).ToString());
		Check("backups: the 'backup' command and a restore both write owner-only", Src("Core/Backup.cs").Contains("WriteOwnerOnly(tmp, zip);", StringComparison.Ordinal)
			&& Src("Core/Backup.cs").Contains("WriteOwnerOnly(tmp, data);", StringComparison.Ordinal) && Src("Core/Backup.cs").Contains("OwnerOnlyFolder(Folder);", StringComparison.Ordinal));

		// SIGHUP: the same clean shutdown as SIGTERM, unless started to ignore it (nohup).
		Check("SIGHUP: closing the terminal is a clean shutdown, through Quit", Src("Program.cs").Contains(
			"using PosixSignalRegistration? sighup = OperatingSystem.IsWindows() || Platform.HangupIgnored() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => {\n\tctx.Cancel = true;\n\tQuit();", StringComparison.Ordinal));
		bool ignored = NocatFarm.Core.Platform.HangupIgnored();
		Check("SIGHUP: whether it's ignored can be asked (never on Windows)", !OperatingSystem.IsWindows() || !ignored);

		// Docker: backups kept outside the container, a time zone from .env, the public address and proxies documented.
		string repo = Path.Combine(srcDir, "..", "..");
		string compose = File.ReadAllText(Path.Combine(repo, "docker-compose.example.yml"));
		string dockerfile = File.ReadAllText(Path.Combine(repo, "Dockerfile"));
		Check("docker: ./backups is mounted, and /data/backups is a volume of the image",
			compose.Contains("- ./backups:/data/backups", StringComparison.Ordinal) && dockerfile.Contains("\"/data/backups\"]", StringComparison.Ordinal)
			&& dockerfile.Contains("mkdir -p /data/config /data/logs /data/backups", StringComparison.Ordinal));
		Check("docker: the time zone comes from .env, UTC without it", compose.Contains("TZ: ${TZ:-Etc/UTC}", StringComparison.Ordinal) && !compose.Contains("TZ: Europe/London", StringComparison.Ordinal));
		Check("docker: the public address and trusted proxies are in the compose file", compose.Contains("# NOCATFARM_PUBLIC_ADDRESS:", StringComparison.Ordinal)
			&& compose.Contains("# NOCATFARM_TRUSTED_PROXIES: 172.16.0.0/12", StringComparison.Ordinal));
		Check("docker: a health check that needs no curl", dockerfile.Contains("\"--ping\"]", StringComparison.Ordinal));
		Check("docker: the update message says git pull and a rebuild, not an image to pull",
			!Src("Core/UpdateCheck.cs").Contains("Pull the new image", StringComparison.Ordinal) && Src("Core/UpdateCheck.cs").Contains("SelfUpdate.ByHand(tag)", StringComparison.Ordinal));

		// An update is only out once it has a zip for this machine: a fake GitHub on this PC answers the update check.
		System.Net.Sockets.TcpListener feed = new(System.Net.IPAddress.Loopback, 0);
		feed.Start();
		int feedPort = ((System.Net.IPEndPoint) feed.LocalEndpoint).Port;
		string answer = "";
		CancellationTokenSource stopFeed = new();

		_ = Task.Run(async () => {
			while (!stopFeed.IsCancellationRequested) {
				System.Net.Sockets.TcpClient client;

				try {
					client = await feed.AcceptTcpClientAsync(stopFeed.Token);
				} catch {
					break;
				}

				using (client) {
					System.Net.Sockets.NetworkStream ns = client.GetStream();
					byte[] buffer = new byte[8192];
					string head = "";

					while (!head.Contains("\r\n\r\n", StringComparison.Ordinal)) {
						int read = await ns.ReadAsync(buffer);
						if (read <= 0) {
							break;
						}

						head += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
					}

					byte[] body = System.Text.Encoding.UTF8.GetBytes(answer);
					byte[] reply = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
					await ns.WriteAsync(reply);
					await ns.WriteAsync(body);
				}
			}
		});

		string mine = OperatingSystem.IsWindows() ? "nocat.farm-v99.0.0-portable.zip" : $"nocat.farm-v99.0.0_{NocatFarm.Core.Platform.ReleaseRid}.zip";
		string notMine = OperatingSystem.IsWindows() ? "nocat.farm-v99.0.0_osx-arm64.zip" : "nocat.farm-v99.0.0-portable.zip";
		string Release(string tag, params string[] assets) => System.Text.Json.JsonSerializer.Serialize(new {
			tag_name = tag, html_url = "https://example.invalid/", body = "- a test",
			assets = assets.Select(a => new { name = a, browser_download_url = $"http://127.0.0.1:{feedPort}/{a}", size = 1234 }).ToArray()
		});

		Environment.SetEnvironmentVariable("NOCATFARM_UPDATE_FEED", $"http://127.0.0.1:{feedPort}/latest.json");

		try {
			answer = Release("v99.0.0", notMine, "nocat.farm-v99.0.0.tar.gz");
			string? problem = NocatFarm.Core.UpdateCheck.LookAsync(force: true, quiet: true).Result;
			Check("update check: a release with no zip for this machine yet isn't announced", (problem == null) && (NocatFarm.Core.UpdateCheck.Available == null), problem ?? NocatFarm.Core.UpdateCheck.Available ?? "");
			// ...but it isn't "you're on the newest" either: 'update' said exactly that on a Mac between the tag and its zips.
			Check("update check: ...it's remembered as out with no download here yet", NocatFarm.Core.UpdateCheck.NoDownloadYet == "v99.0.0", NocatFarm.Core.UpdateCheck.NoDownloadYet ?? "null");
			string updateSaid = await (Task<string>) typeof(Commands).GetMethod("Update", S)!.Invoke(null, [Array.Empty<string>()])!;
			Check("update: says a newer version is out with no download for this computer yet, not that this is the newest",
				updateSaid.Contains("v99.0.0", StringComparison.Ordinal) && updateSaid.Contains("no download for this computer yet", StringComparison.Ordinal)
				&& !updateSaid.Contains("newest release", StringComparison.Ordinal), updateSaid);
			NocatFarm.Core.UpdateCheck.Queue("v98.0.0");
			NocatFarm.Core.UpdateCheck.QueuedInstallIfDue(null!);   // returns before it would look at any account
			Check("update: an 'update accept' waiting for bedtime waits for the download too, not dropped", NocatFarm.Core.UpdateCheck.Queued != null);
			typeof(NocatFarm.Core.UpdateCheck).GetField("_queued", S)!.SetValue(null, null);
			answer = Release("v99.0.0", notMine, mine);
			problem = NocatFarm.Core.UpdateCheck.LookAsync(force: true, quiet: true).Result;
			Check("update check: ...and is, once its zip is there", (problem == null) && (NocatFarm.Core.UpdateCheck.Available == "v99.0.0") && (NocatFarm.Core.UpdateCheck.NoDownloadYet == null),
				problem ?? NocatFarm.Core.UpdateCheck.Available ?? "null");
			answer = Release("v99.0.1");
			problem = NocatFarm.Core.UpdateCheck.LookAsync(force: true, quiet: true).Result;
			Check("update check: a newer tag with nothing uploaded yet takes the announcement back", (problem == null) && (NocatFarm.Core.UpdateCheck.Available == null)
				&& (NocatFarm.Core.UpdateCheck.NoDownloadYet == "v99.0.1"));
			using System.Text.Json.JsonDocument rel = System.Text.Json.JsonDocument.Parse(Release("v99.0.0", notMine, mine));
			object? zip = typeof(NocatFarm.Core.SelfUpdate).GetMethod("ZipForThisMachine", S)!.Invoke(null, [rel.RootElement]);
			Check("update: the zip it installs is this machine's", zip?.ToString()?.Contains(mine, StringComparison.Ordinal) == true, zip?.ToString() ?? "null");
		} finally {
			answer = Release("v0.0.1");
			_ = NocatFarm.Core.UpdateCheck.LookAsync(force: true, quiet: true).Result;
			Environment.SetEnvironmentVariable("NOCATFARM_UPDATE_FEED", null);
			stopFeed.Cancel();
			feed.Stop();
		}
	} finally {
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch {
			// a temp folder
		}
	}
}

// ── the games-played message and the custom name, the same bytes on every OS ─────────────────────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	MethodInfo build = typeof(NocatFarm.Core.Bot).GetMethod("BuildGamesPlayed", S)!;
	SteamKit2.ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed> Games(string? label, List<uint> apps, int device = 0) =>
		(SteamKit2.ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>) build.Invoke(null, [label, apps, device])!;
	SteamKit2.Internal.CMsgClientGamesPlayed RoundTrip(SteamKit2.ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed> msg) =>
		new SteamKit2.ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(new SteamKit2.PacketClientMsgProtobuf(msg.MsgType, msg.Serialize())).Body;
	static bool Has(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle) >= 0;

	const string skull = "\U0001F480nocat.lol\U0001F480";
	var sent = Games(skull, [730, 440]);
	var back = RoundTrip(sent);
	Check("games-played: the custom name leads, as a non-Steam shortcut, then the real games in order",
		back.games_played.Count == 3 && back.games_played[0].game_id == NocatFarm.Core.SteamIds.ShortcutGameId
		&& back.games_played.Skip(1).Select(static g => g.game_id).SequenceEqual([730UL, 440UL]), string.Join(",", back.games_played.Select(static g => g.game_id)));
	Check("games-played: an emoji name survives the wire exactly (UTF-8 F0 9F 92 80, not ? or U+FFFD)",
		back.games_played[0].game_extra_info == skull && Has(sent.Serialize(), [0xF0, 0x9F, 0x92, 0x80]) && !Has(sent.Serialize(), [0xEF, 0xBF, 0xBD]),
		back.games_played[0].game_extra_info);
	Check("games-played: half an emoji goes to Steam as U+FFFD - why the console's backspace must take the whole pair",
		Has(Games("nocat\uD83D", [730]).Serialize(), [0xEF, 0xBF, 0xBD]));
	Check("games-played: no name, just the games; nothing at all is an empty message (the stand-down)",
		RoundTrip(Games(null, [730])).games_played.Single().game_id == 730 && RoundTrip(Games(null, [])).games_played.Count == 0
		&& RoundTrip(Games("   ", [])).games_played.Count == 0);
	var many = RoundTrip(Games(skull, [.. Enumerable.Range(1, 40).Select(static i => (uint) i * 10)]));
	Check("games-played: cut at Steam's 32, the name counting as one", many.games_played.Count == NocatFarm.Core.SteamIds.GamesAtOnce
		&& many.games_played[0].game_extra_info == skull && many.games_played[^1].game_id == 310, $"{many.games_played.Count}");
	var deck = RoundTrip(Games(skull, [730], NocatFarm.Core.SteamIds.DeviceSteamDeck));
	Check("games-played: a Deck says Proton on every entry, the name too", deck.games_played.All(static g =>
		g.compat_tool_id == NocatFarm.Core.SteamIds.ProtonExperimental && g.game_os_platform == (int) SteamKit2.EOSType.Linux6x));

	// The connection an account is made with, as the settings say - the self-test connects with exactly this.
	int protocolWas = NocatFarm.Config.Live.Global.SteamProtocol;
	MethodInfo configure = typeof(NocatFarm.Core.Bot).GetMethod("BuildSteamConfiguration", S)!;
	SteamKit2.ProtocolTypes Transport(int setting) {
		NocatFarm.Config.Live.Global.SteamProtocol = setting;

		return ((SteamKit2.SteamConfiguration) configure.Invoke(null, [new NocatFarm.Config.BotConfig()])!).ProtocolTypes;
	}

	Check("connection: 'Connect using' picks the transport (either / WebSocket / TCP)",
		Transport(0) == SteamKit2.ProtocolTypes.All && Transport(1) == SteamKit2.ProtocolTypes.WebSocket && Transport(2) == SteamKit2.ProtocolTypes.Tcp);
	NocatFarm.Config.Live.Global.SteamProtocol = protocolWas;

	// The console's backspace: the whole emoji, and the right number of columns rubbed out.
	(string Left, int Columns) Back(string typed, bool masked = false) {
		System.Text.StringBuilder line = new(typed);
		int columns = NocatFarm.TypedLine.Backspace(line, masked);

		return (line.ToString(), columns);
	}

	Check("backspace: plain text loses one char, one column", Back("name kylro") == ("name kylr", 1));
	Check("backspace: an emoji goes whole - no half surrogate left behind", Back("nocat\U0001F480") == ("nocat", 2), Back("nocat\U0001F480").Left.Length.ToString());
	Check("backspace: an emoji with its variation selector goes whole", Back("x☠️") == ("x", 2));
	Check("backspace: a letter with its accent goes whole, one column", Back("caf" + "é") == ("caf", 1));
	Check("backspace: a masked answer rubs out one star per char it echoed", Back("pw\U0001F480", masked: true) == ("pw", 2));
	Check("backspace: an empty line stays empty", Back("") == ("", 0));
	static bool NoHalves(string s) {
		for (int i = 0; i < s.Length; i++) {
			if (char.IsHighSurrogate(s[i]) && (i + 1 < s.Length) && char.IsLowSurrogate(s[i + 1])) {
				i++;
			} else if (char.IsSurrogate(s[i])) {
				return false;
			}
		}

		return true;
	}

	System.Text.StringBuilder typing = new(skull);
	bool whole = true;
	int presses = 0;

	while (typing.Length > 0) {
		NocatFarm.TypedLine.Backspace(typing, false);
		whole &= NoHalves(typing.ToString());
		presses++;
	}

	Check("backspace: all the way through 💀nocat.lol💀, never half an emoji on the line - 11 presses for 11 things typed", whole && (presses == 11), $"{presses} presses");
}

// ── found testing on real accounts on Linux: grind takes "1h", and the self-check's advice fits the account ─────────
{
	bool H(string text, double want) => NocatFarm.Commands.TryHours(text, out double got) && Math.Abs(got - want) < 0.0001;
	Check("grind: hours as people type them - 6, 1.5, 1,5, 1h, 90m, 1h30m, 2h 15m",
		H("6", 6) && H("1.5", 1.5) && H("1,5", 1.5) && H("1h", 1) && H("90m", 1.5) && H("1h30m", 1.5) && H("2h 15m", 2.25) && H("45min", 0.75) && H("2 hours", 2));
	Check("grind: nonsense, zero and negatives are refused",
		!NocatFarm.Commands.TryHours("soon", out _) && !NocatFarm.Commands.TryHours("0", out _) && !NocatFarm.Commands.TryHours("-2", out _) && !NocatFarm.Commands.TryHours("", out _)
		&& !NocatFarm.Commands.TryHours("1h30", out _));
	Check("grind: '90m' is an hour and a half, not 9 hours; '45min' is 45 minutes, not 4 hours", H("90m", 1.5) && H("45min", 0.75) && H("10m", 1.0 / 6));
	var prev = System.Globalization.CultureInfo.CurrentCulture;
	System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
	Check("grind: '1.5' is an hour and a half on a German PC too", H("1.5", 1.5));
	System.Globalization.CultureInfo.CurrentCulture = prev;
	string sc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "Core", "SelfCheck.cs"));
	Check("self-check: hours from before human mode are said to be from before, not blamed on a one-game night list",
		sc.Contains("bool fromBefore = bot.Cfg.LegitMode", StringComparison.Ordinal) && sc.Contains("(nightGames > 2", StringComparison.Ordinal));
}

// ── round 2: a new grind isn't ended by an old drop run, restores keep the environment, env values go with it, ─────
// ── the dashboard checks trusted proxies, an unwritable folder isn't a crash, update folders are tidied, CI checks fail ──
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	string realRoot = NocatFarm.Config.ConfigStore.Root;
	NocatFarm.Config.GlobalConfig liveBefore = NocatFarm.Config.Live.Global;
	string tmpRoot = Path.Combine(Path.GetTempPath(), "nf-round2-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
	NocatFarm.Config.ConfigStore.UseRoot(tmpRoot);
	string srcDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string repo = Path.Combine(srcDir, "..", "..");
	string Src(string rel) => File.ReadAllText(Path.Combine(srcDir, rel)).Replace("\r\n", "\n");
	string[] envNames = ["NOCATFARM_PUBLIC_ADDRESS", "NOCATFARM_TRUSTED_PROXIES", "NOCATFARM_WEB_HOST", "NOCATFARM_WEB_PORT", "NOCATFARM_WEB_PASSWORD", "NOCATFARM_WEB_PASSWORD_FILE"];
	Dictionary<string, string?> envBefore = envNames.ToDictionary(static n => n, static n => Environment.GetEnvironmentVariable(n));

	void Env(string? publicAddress, string? proxies) {
		foreach (string n in envNames) {
			Environment.SetEnvironmentVariable(n, null);
		}

		Environment.SetEnvironmentVariable("NOCATFARM_PUBLIC_ADDRESS", publicAddress);
		Environment.SetEnvironmentVariable("NOCATFARM_TRUSTED_PROXIES", proxies);
	}

	try {
		// 3. Grinds by run: a drop run waiting on a badge page while another grind starts neither counts that one down nor ends it.
		var gb = new NocatFarm.Core.Bot("round2-grind", new NocatFarm.Config.BotConfig());
		string gbPath = (string) typeof(NocatFarm.Core.Bot).GetProperty("GrindPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gb)!;
		gb.StartGrind(460920, TimeSpan.FromHours(1), drops: 3);
		long runA = gb.GrindRun;
		gb.StartGrind(730, TimeSpan.FromHours(2), drops: 2);
		long runB = gb.GrindRun;
		Check("grind runs: each start is a new run", runB != runA && gb.CurrentGrind == (730u, runB));
		Check("grind runs: a drop the old run counts (its own game) doesn't come off the new one", !gb.CountGrindDrops(1, 460920, runA) && gb.GrindDropsLeft == 2);
		Check("grind runs: the old drop run finishing doesn't end the grind started since", !gb.StopGrind(runA) && gb.GrindGame == 730 && gb.Grinding);
		gb.StartGrind(730, TimeSpan.FromHours(2), drops: 2);
		long runC = gb.GrindRun;
		Check("grind runs: nor does it on a new run of the same game", !gb.CountGrindDrops(1, 730, runB) && gb.GrindDropsLeft == 2 && !gb.StopGrind(runB) && gb.GrindGame == 730);
		Check("grind runs: its own drops are counted", gb.CountGrindDrops(1, 730, runC) && gb.GrindDropsLeft == 1);
		gb.StartGrind(730, TimeSpan.FromHours(1));   // a plain grind, no drops wanted
		long runD = gb.GrindRun;
		Check("grind runs: a plain grind on the drop run's game isn't ended by the drop run", !gb.StopGrind(runC) && gb.GrindGame == 730 && File.Exists(gbPath));
		Check("grind runs: the running one stops by its own number, on disk too", gb.StopGrind(runD) && gb.GrindGame == 0 && !File.Exists(gbPath) && !gb.StopGrind(runD));
		string farmer = Src("Modules/CardFarmer.cs");
		int dropRunAt = farmer.IndexOf("private async Task DropRunAsync(", StringComparison.Ordinal);
		int dropRunEnd = farmer.IndexOf("// ── bumping playtime", dropRunAt, StringComparison.Ordinal);
		string dropRun = farmer[dropRunAt..dropRunEnd];
		Check("grind runs: the drop run counts with its game and run, and only ever stops its own run",
			dropRun.Contains("(uint app, long run) = Bot.CurrentGrind;", StringComparison.Ordinal) && dropRun.Contains("Bot.CountGrindDrops(dropped, app, run)", StringComparison.Ordinal)
			&& !dropRun.Contains("Bot.StopGrind()", StringComparison.Ordinal) && !dropRun.Contains("Bot.GrindGame == app", StringComparison.Ordinal));

		// 8. Values from the environment go again once the variable does, unless changed in Settings since.
		Env("farm.example.com", "172.16.0.0/12");
		NocatFarm.Config.GlobalConfig e1 = new();
		NocatFarm.Core.Platform.ApplyEnvironment(e1);
		Check("env: applied, and remembered as the environment's", e1.WebPublicAddress == "farm.example.com" && e1.WebTrustedProxies == "172.16.0.0/12"
			&& File.ReadAllText(NocatFarm.Core.Platform.EnvAppliedPath).Contains("farm.example.com", StringComparison.Ordinal));
		Env(null, null);
		NocatFarm.Config.GlobalConfig e2 = NocatFarm.Config.ConfigStore.LoadGlobal();
		Check("env: (the settings file had them)", e2.WebPublicAddress == "farm.example.com" && e2.WebTrustedProxies == "172.16.0.0/12");
		NocatFarm.Core.Platform.ApplyEnvironment(e2);
		NocatFarm.Config.GlobalConfig e2File = NocatFarm.Config.ConfigStore.LoadGlobal();
		Check("env: NOCATFARM_PUBLIC_ADDRESS taken away - the internet is refused again, in memory and in the file",
			e2.WebPublicAddress == "" && e2File.WebPublicAddress == "", $"{e2.WebPublicAddress} / {e2File.WebPublicAddress}");
		Check("env: NOCATFARM_TRUSTED_PROXIES taken away - the proxy isn't trusted any more", e2.WebTrustedProxies == "" && e2File.WebTrustedProxies == "");
		Check("env: nothing left remembered", !File.Exists(NocatFarm.Core.Platform.EnvAppliedPath));
		Env("farm.example.com", "10.0.0.0/8");
		NocatFarm.Config.GlobalConfig e3 = NocatFarm.Config.ConfigStore.LoadGlobal();
		NocatFarm.Core.Platform.ApplyEnvironment(e3);
		e3.WebPublicAddress = "mine.example.com";   // typed in Settings afterwards
		NocatFarm.Config.ConfigStore.SaveGlobal(e3);
		Env(null, "not-a-range");
		NocatFarm.Config.GlobalConfig e4 = NocatFarm.Config.ConfigStore.LoadGlobal();
		NocatFarm.Core.Platform.ApplyEnvironment(e4);
		Check("env: an address changed in Settings since is the owner's own and stays", e4.WebPublicAddress == "mine.example.com", e4.WebPublicAddress);
		Check("env: a proxy list that can't be read changes nothing - the last good one stays", e4.WebTrustedProxies == "10.0.0.0/8", e4.WebTrustedProxies);
		Check("docker: the compose file says taking the address away closes it again",
			File.ReadAllText(Path.Combine(repo, "docker-compose.example.yml")).Contains("Taken away again, the next start clears it.", StringComparison.Ordinal));

		// 1. A restore puts the environment's values back on top of the backup's, as every start does.
		Env(null, null);
		NocatFarm.Config.ConfigStore.SaveGlobal(new NocatFarm.Config.GlobalConfig { WebEnabled = false, StatusEveryMinutes = 9 });   // a PC's settings: no public address, no proxy
		byte[] pcBackup = NocatFarm.Core.Backup.Create();
		Env("farm.example.com", "172.16.0.0/12");
		NocatFarm.Config.GlobalConfig vps = new() { WebEnabled = false };
		NocatFarm.Core.Platform.ApplyEnvironment(vps);
		var restoreMgr = new NocatFarm.Core.BotManager(vps);
		string restored = await NocatFarm.Core.Backup.RestoreAsync(restoreMgr, pcBackup);
		NocatFarm.Config.GlobalConfig afterFile = NocatFarm.Config.ConfigStore.LoadGlobal();
		Check("restore: the backup's settings are in use", restored.StartsWith("Restored", StringComparison.Ordinal) && restoreMgr.Global.StatusEveryMinutes == 9, restored);
		Check("restore: ...with the environment's public address and trusted proxies still on top, in memory and in the file",
			restoreMgr.Global.WebPublicAddress == "farm.example.com" && restoreMgr.Global.WebTrustedProxies == "172.16.0.0/12"
			&& afterFile.WebPublicAddress == "farm.example.com" && afterFile.WebTrustedProxies == "172.16.0.0/12",
			$"{restoreMgr.Global.WebPublicAddress} {restoreMgr.Global.WebTrustedProxies} / {afterFile.WebPublicAddress} {afterFile.WebTrustedProxies}");
		await restoreMgr.DisposeAsync();
		Env(null, null);

		// 2. The dashboard refuses a trusted proxy it can't read, as the console does.
		var web = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };
		MethodInfo save = typeof(NocatFarm.Web.WebHost).GetMethod("SaveFromPage", BindingFlags.NonPublic | BindingFlags.Static)!;
		var pageLive = new NocatFarm.Config.GlobalConfig { WebEnabled = false, WebTrustedProxies = "10.0.0.5" };
		string? SaveProxies(string value) {
			var sent = System.Text.Json.JsonSerializer.SerializeToNode(pageLive, web)!.AsObject();
			sent["Base"] = sent.DeepClone();
			sent["WebTrustedProxies"] = value;
			object result = save.Invoke(null, [pageLive, sent, web])!;
			return (string?) result.GetType().GetProperty("Error")!.GetValue(result);
		}
		string? rangeError = SaveProxies("172.16.0.0-172.31.255.255");
		string? maskError = SaveProxies("172.16.0.0/33");
		Check("dashboard save: a trusted proxy that isn't an address or a range is refused, with the console's words",
			(rangeError?.Contains("isn't an address or a range like 172.16.0.0/12", StringComparison.Ordinal) == true) && (maskError != null) && (pageLive.WebTrustedProxies == "10.0.0.5"),
			$"{rangeError} / {maskError} / {pageLive.WebTrustedProxies}");
		Check("dashboard save: a good one is saved", (SaveProxies("172.16.0.0/12") == null) && (pageLive.WebTrustedProxies == "172.16.0.0/12"), pageLive.WebTrustedProxies);

		// 4. The instance claim: a folder that can't be written isn't "already running", and isn't a crash.
		MethodInfo heldByAnother = typeof(NocatFarm.Core.AppInstance).GetMethod("IsHeldByAnother", S)!;
		int wouldBlock = OperatingSystem.IsLinux() ? 11 : 35;
		Check("instance: a lock another process has is told apart from a read-only disk or a missing file",
			(bool) heldByAnother.Invoke(null, [new IOException("held", wouldBlock)])!
			&& !(bool) heldByAnother.Invoke(null, [new IOException("read-only file system", 30)])!
			&& !(bool) heldByAnother.Invoke(null, [new FileNotFoundException("gone")])!);
		Check("instance: making the folder and opening the lock are both inside the try, and a refusal isn't a crash",
			Src("Core/AppInstance.cs").Contains("try {\n\t\t\tDirectory.CreateDirectory(Path.GetDirectoryName(path)!);\n\t\t\theld = new FileStream(", StringComparison.Ordinal)
			&& Src("Core/AppInstance.cs").Contains("catch (Exception e) when (e is UnauthorizedAccessException or IOException) {", StringComparison.Ordinal));

		if (!OperatingSystem.IsWindows() && (Environment.UserName != "root")) {
			string ro = Path.Combine(tmpRoot, "made-by-root");
			Directory.CreateDirectory(Path.Combine(ro, "config"));
			File.SetUnixFileMode(Path.Combine(ro, "config"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

			try {
				IDisposable? a = null, b = null;
				Exception? crash = null;

				try {
					a = NocatFarm.Core.AppInstance.TryClaim(ro);
					b = NocatFarm.Core.AppInstance.TryClaim(ro);
				} catch (Exception e) {
					crash = e;
				}

				Check("instance: a config folder this user can't write runs on (so the chown fix is shown), not crash, not 'already running'",
					(crash == null) && (a != null) && (b != null), crash?.GetType().Name ?? "");
				a?.Dispose();
				b?.Dispose();
			} finally {
				File.SetUnixFileMode(Path.Combine(ro, "config"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}

		// 5. Update folders: an old one left by a failed swap goes at the next update; a fresh one, and anything else, stays.
		string temp = Path.Combine(tmpRoot, "temp");
		string oldWork = Path.Combine(temp, "nocatfarm-update-old123");
		string newWork = Path.Combine(temp, "nocatfarm-update-new456");
		string notOurs = Path.Combine(temp, "something-else");
		foreach (string d in new[] { oldWork, newWork, notOurs }) {
			Directory.CreateDirectory(Path.Combine(d, "staged"));
			File.WriteAllText(Path.Combine(d, "release.zip"), "zip");
			Directory.SetLastWriteTimeUtc(d, d == newWork ? DateTime.UtcNow : DateTime.UtcNow.AddDays(-3));
		}
		int swept = NocatFarm.Core.SelfUpdate.SweepOldWork(temp, TimeSpan.FromDays(1));
		Check("update: a work folder a failed update left behind is removed at the next one; a fresh one and other folders stay",
			(swept == 1) && !Directory.Exists(oldWork) && Directory.Exists(newWork) && Directory.Exists(notOurs), $"{swept}");
		string upd = Src("Core/SelfUpdate.cs");
		Check("update: ApplyAsync tidies old ones before making its own",
			upd.IndexOf("SweepOldWork(Path.GetTempPath(), TimeSpan.FromDays(1));", StringComparison.Ordinal) is > 0 and int sweepAt
			&& sweepAt < upd.IndexOf("work = Directory.CreateTempSubdirectory(\"nocatfarm-update-\").FullName;", StringComparison.Ordinal));
		string winSwap = (string) typeof(NocatFarm.Core.SelfUpdate).GetMethod("SwapScript", S)!.Invoke(null, [12345])!;
		int winFail = winSwap.IndexOf("if %rc% GEQ 8 (", StringComparison.Ordinal);
		int winExit = winSwap.IndexOf("exit /b 1", winFail, StringComparison.Ordinal);
		Check("update: Windows - a copy that fails removes its work folder before it exits",
			(winFail > 0) && (winSwap.IndexOf("rmdir /s /q \"%NF_WORK%\"", winFail, StringComparison.Ordinal) is > 0 and int rmAt) && (rmAt < winExit));

		if (!OperatingSystem.IsWindows()) {
			// The real Unix swap script, on a copy that fails: an unreadable file in the new version.
			string unixSwap = (string) typeof(NocatFarm.Core.SelfUpdate).GetField("UnixSwapScript", S)!.GetValue(null)!;
			string here = Path.Combine(tmpRoot, "swap here"), work = Path.Combine(tmpRoot, "swap work");
			Directory.CreateDirectory(here);
			Directory.CreateDirectory(Path.Combine(work, "staged"));
			Directory.CreateDirectory(Path.Combine(work, "backup"));
			File.WriteAllText(Path.Combine(work, "release.zip"), "zip");
			string unreadable = Path.Combine(work, "staged", "locked.dll");
			File.WriteAllText(unreadable, "x");
			File.SetUnixFileMode(unreadable, UnixFileMode.None);
			string scriptPath = Path.Combine(work, "swap.sh");
			File.WriteAllText(scriptPath, unixSwap.Replace("\r\n", "\n"));
			string failFile = Path.Combine(tmpRoot, "swap-failed.txt");
			var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh", [scriptPath]) { RedirectStandardOutput = true, RedirectStandardError = true };
			psi.Environment["NF_PID"] = "999999";
			psi.Environment["NF_HERE"] = here;
			psi.Environment["NF_EXE"] = Path.Combine(here, "nocatFarm");
			psi.Environment["NF_WORK"] = work;
			psi.Environment["NF_STAGED"] = Path.Combine(work, "staged");
			psi.Environment["NF_BACKUP"] = Path.Combine(work, "backup");
			psi.Environment["NF_FAIL"] = failFile;
			psi.Environment["NF_OK"] = Path.Combine(work, "started.txt");
			psi.Environment["NF_TAG"] = "v9.9.9";
			psi.Environment.Remove("NF_TERMINAL");
			using var sh = System.Diagnostics.Process.Start(psi)!;
			bool ended = sh.WaitForExit(60_000);
			Check("update: Linux/Mac - a copy that fails says so, and removes its work folder (the download and both copies)",
				ended && (sh.ExitCode == 1) && File.Exists(failFile) && (File.ReadAllText(failFile).Trim() == "8") && !Directory.Exists(work),
				ended ? $"exit {sh.ExitCode}, {sh.StandardError.ReadToEnd().Trim()}, work there: {Directory.Exists(work)}" : "still running");
		}

		// 7. The setup: when a kept config folder can't be set aside, nothing is copied into it - so nothing of it is deleted.
		string iss = File.ReadAllText(Path.Combine(repo, "tools", "installer", "code.iss")).Replace("\r\n", "\n");
		int moveAt = iss.IndexOf("function MoveIn: Boolean;", StringComparison.Ordinal);
		int renameAt = iss.IndexOf("if not RenameFile(ExpandConstant('{app}\\config'), Aside) then begin", moveAt, StringComparison.Ordinal);
		int copyAt = iss.IndexOf("Result := CopyTree(Portable + '\\config', ExpandConstant('{app}\\config'));", moveAt, StringComparison.Ordinal);
		int stopAt = iss.IndexOf("Result := False;", renameAt, StringComparison.Ordinal);
		int exitAt = iss.IndexOf("Exit;", renameAt, StringComparison.Ordinal);
		Check("setup: a kept config that can't be set aside stops the move before any copy (and so before any delete)",
			(moveAt > 0) && (renameAt > moveAt) && (stopAt > renameAt) && (exitAt > stopAt) && (exitAt < copyAt)
			&& !iss[moveAt..copyAt].Contains("then Aside := '';", StringComparison.Ordinal));

		// 6. The CI checks can fail: no bare "! cmd" and no "a && b" as a check - bash -e / sh -e stop for neither.
		List<string> cantFail = [];
		IEnumerable<string> scripts = Directory.GetFiles(Path.Combine(repo, "tests"), "*.sh")
			.Concat(Directory.Exists(Path.Combine(repo, ".github", "workflows")) ? Directory.GetFiles(Path.Combine(repo, ".github", "workflows"), "*.yml") : []);
		System.Text.RegularExpressions.Regex chained = new(@"^\s*(test|\[)\s.*&&\s*(test|\[|!)\s");
		foreach (string file in scripts) {
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++) {
				string line = lines[i];
				if (line.TrimStart().StartsWith("! ", StringComparison.Ordinal) || (chained.IsMatch(line) && !line.Contains("||", StringComparison.Ordinal))) {
					cantFail.Add($"{Path.GetFileName(file)}:{i + 1}");
				}
			}
		}
		Check("CI: every check in the workflows and test scripts can fail", cantFail.Count == 0, string.Join(", ", cantFail));
	} finally {
		foreach ((string n, string? v) in envBefore) {
			Environment.SetEnvironmentVariable(n, v);
		}

		NocatFarm.Config.Live.Global = liveBefore;
		NocatFarm.Config.ConfigStore.UseRoot(realRoot);

		try {
			Directory.Delete(tmpRoot, true);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// a temp folder - left for the OS
		}
	}
}

// ── the console's command box: Tab completes what's typed, and an empty box lets the keyboard move on ──────────────
{
	string js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm", "wwwroot", "app.js"));
	int at = js.IndexOf("$('cmd').addEventListener('keydown'", StringComparison.Ordinal);
	string handler = at < 0 ? "" : js[at..Math.Min(js.Length, at + 1800)];
	// It took every Tab: once the box had the focus, Tab and Shift+Tab never left it again (a keyboard trap).
	Check("console: Tab completes only when something is typed, and Shift+Tab always moves on",
		handler.Contains("e.key === 'Tab' && !e.shiftKey && input.value.trim()", StringComparison.Ordinal)
		&& !System.Text.RegularExpressions.Regex.IsMatch(handler, @"e\.key === 'Tab'\)\s*\{\s*e\.preventDefault\(\)"), at < 0 ? "handler not found" : "");
}

// ── the Windows startup entry stays with the copy that has it; the main game can be taken out of the weights ─────
{
	string src = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NocatFarm");
	string program = File.ReadAllText(Path.Combine(src, "Program.cs"));
	string wi = File.ReadAllText(Path.Combine(src, "Windows", "WindowsIntegration.cs"));
	int keep = wi.IndexOf("public static void KeepInStep(bool enabled)", StringComparison.Ordinal);
	string keepBody = keep < 0 ? "" : wi[keep..Math.Min(wi.Length, keep + 1200)];
	// Read, not run: a test that writes the real Run key is how a test copy took the owner's startup entry.
	// Every start pointed the entry at itself, so a test copy made from the real settings took it, and once its
	// folder was deleted the entry pointed at nothing and was cleared.
	Check("startup: a start leaves another copy's entry alone, and only takes one that's missing or points at nothing",
		program.Contains("WindowsIntegration.KeepInStep(global.StartWithWindows)", StringComparison.Ordinal)
		&& !program.Contains("WindowsIntegration.SetStartWithWindows(global.StartWithWindows)", StringComparison.Ordinal)
		&& keepBody.Contains("!StartsThisExe(current) && File.Exists(ExeOf(current))", StringComparison.Ordinal)
		&& keepBody.IndexOf("File.Exists", StringComparison.Ordinal) < keepBody.IndexOf("SetStartWithWindows(true)", StringComparison.Ordinal),
		keep < 0 ? "KeepInStep not found" : "");

	string js = File.ReadAllText(Path.Combine(src, "wwwroot", "app.js"));
	// No × on the main row: once the list had a game, nothing on the page could empty it again.
	Check("weights: the main game has a remove button, and the next game up keeps its share",
		js.Contains("onclick=\"dropWeight(0)\"", StringComparison.Ordinal)
		&& js.Contains("if (index === 0) rows[0].weight = before[0].weight;", StringComparison.Ordinal));
}

// ── round three: grind lengths, closing during a handover, the swap script, the self-test's clock ─────────────────
{
	const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
	string repo = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
	string Src(string rel) => File.ReadAllText(Path.Combine(repo, "src", "NocatFarm", rel)).Replace("\r\n", "\n");

	// Full-width digits: \d took them and double.Parse threw on them.
	bool digitsThrew = false;
	bool fullWidth = true;

	try {
		fullWidth = NocatFarm.Commands.TryHours("\uFF13", out _) || NocatFarm.Commands.TryHours("\uFF11h\uFF13\uFF10m", out _) || NocatFarm.Commands.TryHours("\u0663", out _);
	} catch (FormatException) {
		digitsThrew = true;
	}

	Check("grind: full-width or other scripts' digits are refused, not thrown on", !digitsThrew && !fullWidth);
	Check("grind: under a minute is refused - 0.5m, 0.01 hours, 30 seconds' worth",
		!NocatFarm.Commands.TryHours("0.5m", out _) && !NocatFarm.Commands.TryHours("0.01", out _) && !NocatFarm.Commands.TryHours("0.0001h", out _));
	Check("grind: ...and a minute exactly still counts", NocatFarm.Commands.TryHours("1m", out double oneMinute) && (Math.Abs(oneMinute - (1.0 / 60)) < 1e-9)
		&& NocatFarm.Commands.TryHours("1min", out _) && NocatFarm.Commands.TryHours("0.5h", out _));

	// Closing goes up under the handover's lock: while a handover holds it, the flag waits, so it can't slip in between
	// the check and the swap starting.
	FieldInfo exitFlag = typeof(Commands).GetField("_exitRequested", S)!;
	bool exitBefore = Commands.ExitRequested;
	Action? handlerBefore = Commands.ExitHandler;
	bool flagWhileHeld = true;

	try {
		exitFlag.SetValue(null, false);
		Commands.ExitHandler = null;
		using ManualResetEventSlim holding = new(), letGo = new();
		System.Threading.Lock gate = (System.Threading.Lock) typeof(NocatFarm.Core.SelfUpdate).GetField("HandoverGate", S)!.GetValue(null)!;
		Thread holder = new(() => {
			lock (gate) {
				holding.Set();
				letGo.Wait(TimeSpan.FromSeconds(10));
			}
		});
		holder.Start();
		holding.Wait(TimeSpan.FromSeconds(10));
		Task closing = Task.Run(Commands.RequestExit);
		Thread.Sleep(200);
		flagWhileHeld = Commands.ExitRequested;
		letGo.Set();
		holder.Join();
		await closing.WaitAsync(TimeSpan.FromSeconds(10));
		Check("exit: closing waits for a handover in progress, then goes up", !flagWhileHeld && Commands.ExitRequested);
	} finally {
		exitFlag.SetValue(null, exitBefore);
		Commands.ExitHandler = handlerBefore;
	}

	// The swap script: a stopped copy is waited for, then killed; the Terminal route keeps the options.
	string swap = ((string) typeof(NocatFarm.Core.SelfUpdate).GetField("UnixSwapScript", S)!.GetValue(null)!).Replace("\r\n", "\n");
	int stopAt = swap.IndexOf("stop_new() {", StringComparison.Ordinal);
	string stopBody = stopAt < 0 ? "" : swap[stopAt..swap.IndexOf("\n}\n", stopAt, StringComparison.Ordinal)];
	Check("swap script: a copy being put back is waited for (up to a minute), then killed, before the old one starts",
		stopBody.Contains("while kill -0 \"$p\" 2>/dev/null && [ \"$n\" -lt 60 ]; do sleep 1;", StringComparison.Ordinal) && stopBody.Contains("kill -9 \"$p\"", StringComparison.Ordinal)
		&& stopBody.Contains("while [ -n \"$(from_here)\" ] && [ \"$n\" -lt 60 ]", StringComparison.Ordinal) && stopBody.Contains("for p in $(from_here); do kill -9", StringComparison.Ordinal)
		&& !swap.Contains("sleep 3", StringComparison.Ordinal));
	string startCommand = File.ReadAllText(Path.Combine(repo, "tools", "macos", "start.command"));
	Check("swap script: the Terminal route hands its options over in restart-args.txt, and start.command reads them",
		swap.Contains("printf '%s\\n' \"$@\" > \"$NF_HERE/restart-args.txt\"", StringComparison.Ordinal)
		&& swap.Contains("grep -q 'restart-args.txt' \"$NF_HERE/start.command\"", StringComparison.Ordinal)
		&& startCommand.Contains("done < restart-args.txt", StringComparison.Ordinal) && startCommand.Contains("rm -f restart-args.txt", StringComparison.Ordinal)
		&& (startCommand.IndexOf("restart-args.txt", StringComparison.Ordinal) < startCommand.IndexOf("exec \"$PWD/nocatFarm\" \"$@\"", StringComparison.Ordinal)));

	// The self-test out of time before it began: one line saying so, not "after 3 attempts".
	List<(bool Ok, string What)> said = [];
	using (CancellationTokenSource spent = new()) {
		spent.Cancel();
		SteamKit2.SteamConfiguration config = SteamKit2.SteamConfiguration.Create(static _ => { });
		await (Task) typeof(NocatFarm.Core.SteamSelfTest).GetMethod("SessionAsync", S)!.Invoke(null, [config, "tcp", (Action<bool, string>) ((ok, what) => said.Add((ok, what))), spent.Token])!;
	}
	Check("self-test: out of time is said once, as that, with no attempt counted that never ran",
		(said.Count == 1) && !said[0].Ok && said[0].What.Contains("out of time", StringComparison.Ordinal) && !said[0].What.Contains("3 attempts", StringComparison.Ordinal),
		string.Join(" | ", said.Select(static s => s.What)));
	string selfTest = Src("Core/SteamSelfTest.cs");
	Check("self-test: the log-off runs on a short clock of its own, not the spent budget",
		selfTest.Contains("using CancellationTokenSource own = new(TimeSpan.FromSeconds(20));", StringComparison.Ordinal) && selfTest.Contains("bool steamClosed = LogOff(user);", StringComparison.Ordinal));

	// What changed, not what was asked: an achievement already unlocked by the time the write came is not unlocked twice.
	Check("achievements: the write says how many really changed, and 0 isn't taken as an unlock",
		Src("Core/Achievements.cs").Contains("return (true, \"nothing to change\", 0);", StringComparison.Ordinal)
		&& Src("Modules/AchievementPacer.cs").Contains("if (changed == 0) {", StringComparison.Ordinal)
		&& Src("Core/UnlockEverything.cs").Contains("unlocked += changed;", StringComparison.Ordinal) && !Src("Core/UnlockEverything.cs").Contains("unlocked += locked.Count;", StringComparison.Ordinal));
	Check("cards: turning farming off clears the settling countdown", Src("Modules/CardFarmer.cs").Contains("_settling = false;\n\t\t\t\t_status = new Said(\"off\");", StringComparison.Ordinal));
	Check("console: the board clears below the prompt, so a shorter board leaves no old row behind",
		Src("Windows/LiveConsole.cs").Contains(".Append(_input).Append(\"\\x1b[J\");", StringComparison.Ordinal));
	Check("import: global settings go onto the live ones as they are under the lock, not the copy from the start",
		Src("Config/Import/IdlerImport.cs").Contains("GlobalConfig target = live ? Live.Global : global;", StringComparison.Ordinal));
}

// ── DLC achievements: never one from DLC the account doesn't own ─────────────
{
	string repo = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
	string Src(string rel) => File.ReadAllText(Path.Combine(repo, "src", "NocatFarm", rel)).Replace("\r\n", "\n");
	List<int> Range(int from, int count) => [.. Enumerable.Range(from, count)];
	List<int?> Anchored(int from, int count) => [.. Enumerable.Range(from, count).Select(static i => (int?) i)];
	const NocatFarm.Core.DlcAchievements.Hold None = NocatFarm.Core.DlcAchievements.Hold.None;
	const NocatFarm.Core.DlcAchievements.Hold NotOwned = NocatFarm.Core.DlcAchievements.Hold.NotOwned;
	const NocatFarm.Core.DlcAchievements.Hold Checking = NocatFarm.Core.DlcAchievements.Hold.Checking;
	const NocatFarm.Core.DlcAchievements.Hold Unmapped = NocatFarm.Core.DlcAchievements.Hold.Unmapped;
	NocatFarm.Core.DlcAchievements.Hold HoldOf(NocatFarm.Core.DlcAchievements.Map? m, string name, params uint[] owned) =>
		NocatFarm.Core.DlcAchievements.HoldOf(m, name, new HashSet<uint>(owned));

	// Call of Duty (1938090), as seen on the store: 157 achievements; Modern Warfare II has 24 and its ten highlighted
	// names are positions 0-9, Modern Warfare III has 39 and its names are 24-33.
	(List<int> mw2, bool mw2Exact) = NocatFarm.Core.DlcAchievements.Block(Anchored(0, 10), 24, 157);
	(List<int> mw3, bool mw3Exact) = NocatFarm.Core.DlcAchievements.Block(Anchored(24, 10), 39, 157);
	Check("dlc block: Modern Warfare II is 0-23", mw2Exact && mw2.SequenceEqual(Range(0, 24)), $"{mw2.FirstOrDefault()}-{mw2.LastOrDefault()} ({mw2.Count})");
	Check("dlc block: Modern Warfare III is 24-62", mw3Exact && mw3.SequenceEqual(Range(24, 39)), $"{mw3.FirstOrDefault()}-{mw3.LastOrDefault()} ({mw3.Count})");

	// Names that don't line up: every block any of them says, never the likeliest one.
	(List<int> stray, bool strayExact) = NocatFarm.Core.DlcAchievements.Block([10, 11, null, 50], 5, 100);
	Check("dlc block: a name missing and one far off - the block the others agree on AND the one the stray one says",
		!strayExact && stray.SequenceEqual([.. Range(10, 5), .. Range(47, 5)]), string.Join(",", stray));
	(List<int> wide, bool wideExact) = NocatFarm.Core.DlcAchievements.Block([0, 9], 5, 100);
	Check("dlc block: two names that disagree - both blocks, 0-4 and 8-12",
		!wideExact && wide.SequenceEqual([.. Range(0, 5), .. Range(8, 5)]), string.Join(",", wide));
	(List<int> offEnd, bool offEndExact) = NocatFarm.Core.DlcAchievements.Block(Anchored(95, 3), 10, 100);
	Check("dlc block: a block running off the end stops at the last one, not exact", !offEndExact && offEnd.SequenceEqual(Range(95, 5)), string.Join(",", offEnd));
	(List<int> nowhere, bool nowhereExact) = NocatFarm.Core.DlcAchievements.Block([null, null], 12, 100);
	Check("dlc block: no name found - no block", (nowhere.Count == 0) && !nowhereExact);
	List<bool> hiddenBefore = [.. Enumerable.Range(0, 30).Select(static i => i is 18 or 19)];
	(List<int> withHidden, _) = NocatFarm.Core.DlcAchievements.Block(Anchored(20, 3), 5, 30, hiddenBefore);
	Check("dlc block: hidden achievements just in front are taken in too", withHidden.SequenceEqual(Range(18, 7)), string.Join(",", withHidden));

	// The first name missing: the block starts where the others say, not at the first one found. 10-33, not 11-34.
	(List<int> firstMissing, bool firstMissingExact) = NocatFarm.Core.DlcAchievements.Block([null, .. Anchored(11, 9)], 24, 100);
	Check("dlc block: the first name missing - still 10-33, 10 included",
		!firstMissingExact && Range(10, 24).All(firstMissing.Contains) && !firstMissing.Contains(9), $"{firstMissing.FirstOrDefault()}-{firstMissing.LastOrDefault()} ({firstMissing.Count})");

	// A name the base game uses too: "Completionist" at 3 in the base game and at 40 in the DLC, whose block is 40-59.
	List<string> dupList = [.. Enumerable.Range(0, 100).Select(static i => i switch { 3 or 40 => "Completionist", _ => $"Achievement {i}" })];
	List<string> dupHighlighted = ["Completionist", .. Enumerable.Range(41, 9).Select(static i => $"Achievement {i}")];
	(List<int> dup, bool dupExact) = NocatFarm.Core.DlcAchievements.Locate(NocatFarm.Core.DlcAchievements.Places(dupList, dupHighlighted), 20, 100);
	Check("dlc block: a name also in the base game earlier is taken where the other names are - 40-59, not from 3",
		dup.SequenceEqual(Range(40, 20)) && !dupExact, $"{dup.FirstOrDefault()}-{dup.LastOrDefault()} ({dup.Count})");
	List<int?> dupAnchors = NocatFarm.Core.DlcAchievements.Anchors(dupList, dupHighlighted);
	Check("dlc anchors: the repeated name is the one the others agree with", dupAnchors[0] == 40, string.Join(",", dupAnchors));

	// Only one name found, and it's in two places: nothing to say which - both blocks.
	(List<int> either, _) = NocatFarm.Core.DlcAchievements.Locate([[3, 40]], 5, 100);
	Check("dlc block: one name in two places and nothing else - both places' blocks", either.SequenceEqual([.. Range(3, 5), .. Range(40, 5)]), string.Join(",", either));

	// Names are found in the list whatever the quotes and marks - and a name used twice is taken where the others agree.
	List<string> names = ["Intro", "Don’t Stop", "Completionist", "Other", "Completionist"];
	List<int?> anchors = NocatFarm.Core.DlcAchievements.Anchors(names, ["Other", "completionist", "Don't Stop", "Nowhere"]);
	Check("dlc anchors: quotes and case don't matter, a repeated name is the agreeing one, a missing one is null",
		anchors.SequenceEqual([3, 4, 1, null]), string.Join(",", anchors.Select(static a => a?.ToString() ?? "null")));

	// The whole map, Call of Duty's shape.
	List<NocatFarm.Core.DlcAchievements.Entry> cod = [.. Enumerable.Range(0, 157).Select(static i => new NocatFarm.Core.DlcAchievements.Entry($"ACH_{i}", $"Achievement {i}", "Do a thing.", false))];
	List<NocatFarm.Core.DlcAchievements.Dlc> codDlc = NocatFarm.Core.DlcAchievements.Merge(1938090, [
		new(1962660, 3595230, "Call of Duty®: Modern Warfare® II", 24, [.. Enumerable.Range(0, 10).Select(static i => $"Achievement {i}")]),
		new(2519060, 3595270, "Call of Duty®: Modern Warfare® III", 39, [.. Enumerable.Range(24, 10).Select(static i => $"Achievement {i}")]),
		// Black Ops 6's campaign DLC opens the hub's own page: those figures are the whole game's, not the DLC's.
		new(2933620, 1938090, "Call of Duty®", 157, [.. Enumerable.Range(0, 10).Select(static i => $"Achievement {i}")], Type: "game"),
		// No figures, and nothing in its name says it's only looks: it may well add achievements.
		new(2127791, 2127791, "Call of Duty®: Black Ops 6 - Vault Edition Upgrade", 0, [], Type: "dlc"),
		// No figures, but plainly a skin pack: it holds nothing.
		new(2127790, 2127790, "Call of Duty®: Modern Warfare® II - Desert Rogue: Pro Pack", 0, [], Type: "dlc")
	]);
	Check("dlc merge: a page that is the base game's own says nothing about the DLC - and that DLC can't be placed",
		codDlc.Single(static d => d.App == 2933620) is { Total: 0, Highlighted.Count: 0, Unsure: NocatFarm.Core.DlcAchievements.Doubt.BasePage });
	Check("dlc merge: a DLC with no achievement figures can't be placed either",
		codDlc.Single(static d => d.App == 2127791).Unsure == NocatFarm.Core.DlcAchievements.Doubt.NoFigures);
	Check("dlc merge: a skin pack with no figures holds nothing",
		codDlc.Single(static d => d.App == 2127790).Unsure == NocatFarm.Core.DlcAchievements.Doubt.None);
	Check("dlc merge: owning the DLC or its store app is owning it", codDlc.Single(static d => d.App == 1962660).Ids.SequenceEqual([1962660u, 3595230u]));
	NocatFarm.Core.DlcAchievements.Map codMap = NocatFarm.Core.DlcAchievements.Assemble(1938090, "Call of Duty®", cod, codDlc, 96, 80, DateTime.UtcNow);
	Check("dlc map: Call of Duty - 63 placed with Modern Warfare II and III, two more DLC that can't be placed",
		(codMap.Owners.Count == 63) && (codMap.Groups.Count(static g => g.Unsure == NocatFarm.Core.DlcAchievements.Doubt.None) == 2)
		&& (codMap.Groups.Count(static g => g.Unsure != NocatFarm.Core.DlcAchievements.Doubt.None) == 2) && codMap.StoreSays
		&& codMap.Owners["ach_0"].SequenceEqual([1962660u]) && codMap.Owners["ach_62"].SequenceEqual([2519060u]) && !codMap.Owners.ContainsKey("ach_63")
		&& (codMap.Names?.Count == 157),
		$"{codMap.Owners.Count} owned, {codMap.Groups.Count} groups");

	// The rule.
	HashSet<uint> ownsNothing = NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, static _ => false);
	HashSet<uint> ownsMw2 = NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, static id => id == 3595230);
	HashSet<uint> ownsAllButMw = NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, static id => id is 2933620 or 2127791);
	Check("dlc rule: no map yet - checking, nothing unlocked", HoldOf(null, "ACH_100") == Checking);
	Check("dlc rule: from DLC it doesn't own - held, whatever the case of the name",
		(HoldOf(codMap, "ACH_0") == NotOwned) && (HoldOf(codMap, "ach_30") == NotOwned));

	// Fix: Black Ops 6's achievements (63-156) used to count as the base game's once its page turned out to be the
	// game's. Without it - or without the pro pack nothing says anything about - none of them is unlocked.
	Check("dlc rule: a DLC it doesn't own can't be placed - everything outside owned blocks is held (Black Ops 6's 63-156 too)",
		(HoldOf(codMap, "ACH_63") == Unmapped) && (HoldOf(codMap, "ACH_100") == Unmapped) && (HoldOf(codMap, "ACH_156") == Unmapped)
		&& (HoldOf(codMap, "ACH_100", 2933620) == Unmapped));
	Check("dlc rule: owning every DLC that can't be placed - the rest is the base game's again",
		(HoldOf(codMap, "ACH_100", [.. ownsAllButMw]) == None) && (HoldOf(codMap, "ACH_0", [.. ownsAllButMw]) == NotOwned));
	Check("dlc rule: owning the DLC (by its store app) - its block is fine, only that DLC's, the rest still held",
		ownsMw2.SetEquals([1962660u])
		&& (HoldOf(codMap, "ACH_5", [.. ownsMw2]) == None)
		&& (HoldOf(codMap, "ACH_30", [.. ownsMw2]) == NotOwned)
		&& (HoldOf(codMap, "ACH_100", [.. ownsMw2]) == Unmapped));
	Check("dlc rule: ownership that couldn't be read is owning nothing",
		NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, new Dictionary<uint, NocatFarm.Core.AppOwnership>().ContainsKey).Count == 0);
	Check("dlc rule: a family member's copy isn't this account's",
		NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, id => new Dictionary<uint, NocatFarm.Core.AppOwnership> {
			[1962660] = new(DateTime.UtcNow, true, Own: false)
		}.TryGetValue(id, out NocatFarm.Core.AppOwnership o) && o.Own).Count == 0);

	NocatFarm.Core.Achievement Ach(string name, bool unlocked = false) =>
		new() { Name = name, Display = name, StatId = 1, Bit = 0, Unlocked = unlocked, Protected = false };
	NocatFarm.Core.DlcAchievements.View view = new() { Map = codMap, Owned = ownsNothing };
	NocatFarm.Core.DlcAchievements.View viewAll = new() { Map = codMap, Owned = ownsAllButMw };
	NocatFarm.Core.DlcAchievements.View unknown = new() { Map = null };
	Check("dlc view: a pick from DLC it doesn't own is refused, and in a game it can't map so is a 'base' one",
		!view.Allows(Ach("ACH_3")) && !view.Allows(Ach("ACH_140")) && view.Unmapped && (view.Of(Ach("ACH_140")) == Unmapped));
	Check("dlc view: owning what can't be placed, a base one is fine again", viewAll.Allows(Ach("ACH_140")) && !viewAll.Allows(Ach("ACH_3")) && !viewAll.Unmapped);
	Check("dlc view: before the game is worked out, nothing is allowed", !unknown.Known && !unknown.Allows(Ach("ACH_140")));
	Check("dlc view: everything left alone is counted - 63 from DLC plus the 94 it can't place",
		(view.NotOwnedCount == 157) && (viewAll.NotOwnedCount == 63), $"{view.NotOwnedCount}, {viewAll.NotOwnedCount}");
	Check("dlc view: the DLC it's missing that stop the game being mapped are named",
		view.Unplaceable.Count == 2 && view.Missing(Ach("ACH_140")).Count == 2 && viewAll.Unplaceable.Count == 0);

	// Two DLC could both claim one: both have to be owned.
	NocatFarm.Core.DlcAchievements.Map two = new() { App = 1, Names = ["both", "base"], Groups = [new() { App = 10, Ids = [10] }, new() { App = 20, Ids = [20] }], Owners = new() { ["both"] = [10, 20] } };
	Check("dlc rule: claimed by two DLC - needs both",
		(HoldOf(two, "BOTH", 10) == NotOwned) && (HoldOf(two, "BOTH", 10, 20) == None) && (HoldOf(two, "BASE") == None));

	// What counts as reachable: 157, 63 of them from DLC it doesn't own, 10 base ones unlocked.
	List<(bool, NocatFarm.Core.DlcAchievements.Hold)> reach = [.. Enumerable.Range(0, 157).Select(i => (i >= 100 && i < 110, i < 63 ? NotOwned : None))];
	(int reachUnlocked, int reachable, int reachHeld) = NocatFarm.Core.DlcAchievements.Reach(reach);
	Check("dlc reach: the 63 it can never have don't count - 10 of 94", (reachUnlocked == 10) && (reachable == 94) && (reachHeld == 63), $"{reachUnlocked}/{reachable}, {reachHeld} held");
	reach[0] = (true, NotOwned);
	(int u2, int r2, int h2) = NocatFarm.Core.DlcAchievements.Reach(reach);
	Check("dlc reach: one it has anyway counts as had, and as reachable", (u2 == 11) && (r2 == 95) && (h2 == 62), $"{u2}/{r2}, {h2} held");
	(int u3, int r3, int h3) = NocatFarm.Core.DlcAchievements.Reach([(true, None), (false, Unmapped), (false, Unmapped), (false, Checking)]);
	Check("dlc reach: held for a game it can't map counts as held; still checking doesn't", (u3 == 1) && (r3 == 2) && (h3 == 2), $"{u3}/{r3}, {h3} held");

	MethodInfo ceilingCount = typeof(NocatFarm.Modules.AchievementPacer).GetMethod("CeilingCount", BindingFlags.Static | BindingFlags.NonPublic)!;
	int Ceiling(int reachableCount, int pct, int onboard) => (int) ceilingCount.Invoke(null, [reachableCount, pct, onboard])!;
	Check("dlc ceiling: the ceiling is of what it can reach - 60% of 94 is 56, not 94 of 157", (Ceiling(94, 60, 3) == 56) && (Ceiling(157, 60, 3) == 94), $"{Ceiling(94, 60, 3)}, {Ceiling(157, 60, 3)}");
	Check("dlc ceiling: at 100% it is done at 94, not stuck short of 157 for ever", Ceiling(94, 100, 3) == 94);

	// The older games: the store says nothing, and the wording is all there is.
	Check("dlc name: \"Fallout New Vegas: Dead Money\" is \"dead money\"", NocatFarm.Core.DlcAchievements.DistinctName("Fallout New Vegas: Dead Money", "Fallout: New Vegas") == "dead money");
	Check("dlc name: packaging words and the game's own name leave nothing",
		(NocatFarm.Core.DlcAchievements.DistinctName("Borderlands 2 Season Pass", "Borderlands 2") == null)
		&& (NocatFarm.Core.DlcAchievements.DistinctName("Call of Duty®: Modern Warfare® II - Desert Rogue: Pro Pack", "Call of Duty®") == null)
		&& (NocatFarm.Core.DlcAchievements.DistinctName("Borderlands 2 Ultra HD Texture Pack", "Borderlands 2") == null));
	Check("dlc name: \"Civilization V - Babylon (Nebuchadnezzar II)\" is \"babylon\"",
		NocatFarm.Core.DlcAchievements.DistinctName("Civilization V - Babylon (Nebuchadnezzar II)", "Sid Meier's Civilization® V") == "babylon");
	List<NocatFarm.Core.DlcAchievements.Entry> fnv = [
		new("A01", "New Kid", "Reached 10th level.", false),
		new("A64", "Outsmarted", "Complete Old World Blues.", false),
		new("A72", "Master of the Arsenal", "Caused 10,000 damage with Gun Runners’ Arsenal (GRA) Weapons.", false),
		new("A68", "Hometown Hero", "Completed Lonesome Road.", false),
		new("A11", "You Run Barter Town", "Sell 10,000 caps worth of goods.", false)
	];
	NocatFarm.Core.DlcAchievements.Map fnvMap = NocatFarm.Core.DlcAchievements.Assemble(22380, "Fallout: New Vegas", fnv, NocatFarm.Core.DlcAchievements.Merge(22380, [
		new(72750, 72750, "Fallout New Vegas: Old World Blues", 0, [], Type: "dlc"),
		new(72840, 72840, "Fallout New Vegas®: Gun Runners' Arsenal™", 0, [], Type: "dlc"),
		new(72760, 72760, "Fallout New Vegas®: Lonesome Road™", 0, [], Type: "dlc"),
		new(461160, 461160, "Fallout New Vegas - Soundtrack", 0, [], Type: "music")
	]), 9, 7, DateTime.UtcNow);
	Check("dlc map: with nothing from the store, an achievement naming a DLC is that DLC's",
		!fnvMap.StoreSays && fnvMap.Owners["a64"].SequenceEqual([72750u]) && fnvMap.Owners["a72"].SequenceEqual([72840u]) && fnvMap.Owners["a68"].SequenceEqual([72760u])
		&& !fnvMap.Owners.ContainsKey("a01") && !fnvMap.Owners.ContainsKey("a11") && fnvMap.Groups.All(static g => g.ByName),
		string.Join(", ", fnvMap.Owners.Select(static kv => kv.Key + "=" + string.Join("+", kv.Value))));
	Check("dlc map: but that can't be all of them - an older game without all its DLC is left alone, the soundtrack doesn't count",
		(HoldOf(fnvMap, "A01") == Unmapped) && (HoldOf(fnvMap, "A01", 72750, 72840) == Unmapped) && (HoldOf(fnvMap, "A01", 72750, 72840, 72760) == None)
		&& fnvMap.Groups.All(static g => g.App != 461160));

	// Fix 2: DLC the store gives no achievement numbers for. Cuphead's The Delicious Last Course has achievements, but its
	// page has no figures - just the "Steam Achievements" tag, which every DLC page carries (Borderlands 2's gun packs do).
	List<NocatFarm.Core.DlcAchievements.Entry> cup = [.. Enumerable.Range(0, 30).Select(static i => new NocatFarm.Core.DlcAchievements.Entry($"C{i}", $"Cup {i}", "Do a cup thing.", false))];
	NocatFarm.Core.DlcAchievements.Map cupMap = NocatFarm.Core.DlcAchievements.Assemble(268910, "Cuphead", cup, NocatFarm.Core.DlcAchievements.Merge(268910, [
		new(1117850, 1117850, "Cuphead - The Delicious Last Course", 0, [], Type: "dlc"),
		new(1117851, 1117851, "Cuphead - Official Soundtrack", 0, [], Type: "dlc")
	]), 2, 2, DateTime.UtcNow);
	Check("dlc no figures: Cuphead without The Delicious Last Course - the whole game left alone; with it, fine",
		(HoldOf(cupMap, "C0") == Unmapped) && (HoldOf(cupMap, "C29") == Unmapped) && (HoldOf(cupMap, "C0", 1117850) == None)
		&& (cupMap.Groups.Single().Unsure == NocatFarm.Core.DlcAchievements.Doubt.NoFigures));
	Check("dlc harmless: a soundtrack, an artbook, wallpapers never hold a game - a name with anything more does",
		NocatFarm.Core.DlcAchievements.Harmless("music", "Anything") && NocatFarm.Core.DlcAchievements.Harmless("dlc", "Cuphead - Official Soundtrack")
		&& NocatFarm.Core.DlcAchievements.Harmless("dlc", "Hollow Knight Wallpapers") && NocatFarm.Core.DlcAchievements.Harmless("dlc", "Game - Digital Artbook")
		&& !NocatFarm.Core.DlcAchievements.Harmless("dlc", "Deluxe Edition Upgrade (Soundtrack + Artbook)")
		&& !NocatFarm.Core.DlcAchievements.Harmless("dlc", "Gunzerker Madness Pack") && !NocatFarm.Core.DlcAchievements.Harmless("dlc", "The Lost Levels"));
	List<NocatFarm.Core.DlcAchievements.Dlc> odd = NocatFarm.Core.DlcAchievements.Merge(5, [
		new(51, 0, "", 0, [], Listed: false),
		new(52, 52, "Some Pack", 3, [], Type: "dlc"),
		new(53, 53, "Far Away", 3, ["Not Here", "Nor Here"], Type: "dlc"),
		new(54, 0, "Old Soundtrack", 0, [], Listed: false)
	]);
	NocatFarm.Core.DlcAchievements.Map oddMap = NocatFarm.Core.DlcAchievements.Assemble(5, "Game", cup, odd, 4, 2, DateTime.UtcNow);
	Check("dlc unsure: no store page, a count with no names, names found nowhere - each can't be placed; a delisted soundtrack is still harmless",
		(odd.Single(static d => d.App == 51).Unsure == NocatFarm.Core.DlcAchievements.Doubt.Delisted)
		&& (odd.Single(static d => d.App == 52).Unsure == NocatFarm.Core.DlcAchievements.Doubt.NoFigures)
		&& (oddMap.Groups.Single(static g => g.App == 53).Unsure == NocatFarm.Core.DlcAchievements.Doubt.NotFound)
		&& (odd.Single(static d => d.App == 54).Unsure == NocatFarm.Core.DlcAchievements.Doubt.None) && oddMap.Groups.All(static g => g.App != 54)
		&& (HoldOf(oddMap, "C3", 51, 52) == Unmapped) && (HoldOf(oddMap, "C3", 51, 52, 53) == None));

	// Fix 3: the map knows which achievements it was built from. One it has never seen waits; so does every one on a map
	// saved before this was kept. A game with no DLC at all still has its list kept.
	NocatFarm.Core.DlcAchievements.Map noDlc = NocatFarm.Core.DlcAchievements.Assemble(7, "Plain", cup, [], 0, 0, DateTime.UtcNow);
	NocatFarm.Core.DlcAchievements.Map oldSaved = new() { App = 7, BuiltAt = DateTime.UtcNow };
	Check("dlc names: a game with no DLC - its own are fine, one added since isn't until it's worked out again",
		!noDlc.HasDlc && (HoldOf(noDlc, "C4") == None) && (HoldOf(noDlc, "NEW_DLC_ACH") == Checking) && (HoldOf(codMap, "ACH_157", [.. ownsAllButMw]) == Checking));
	Check("dlc names: a map saved before the names were kept holds everything", HoldOf(oldSaved, "C4") == Checking);
	NocatFarm.Core.DlcAchievements.View fresh = new() { Map = noDlc };
	Check("dlc names: a view says 'checking' for one it has never seen", fresh.Known && fresh.Allows(Ach("C4")) && (fresh.Of(Ach("NEW_ONE")) == Checking) && !fresh.Allows(Ach("NEW_ONE")));

	// Fix 5: licences that couldn't be read are "checking", never "owns none of its DLC".
	NocatFarm.Core.DlcAchievements.View unread = new() { Map = codMap, OwnershipRead = false };
	NocatFarm.Core.DlcAchievements.View unreadPlain = new() { Map = noDlc, OwnershipRead = false };
	Check("dlc licences: couldn't read them - a game with DLC is checking (nothing unlocked, not done); one without is fine",
		!unread.Known && (unread.Of(Ach("ACH_140")) == Checking) && !unread.Unmapped
		&& unreadPlain.Known && unreadPlain.Allows(Ach("C1")));

	MethodInfo heldForDlc = typeof(NocatFarm.Modules.AchievementPacer).GetMethod("HeldForDlc", BindingFlags.Static | BindingFlags.NonPublic)!;
	MethodInfo dlcChanged = typeof(NocatFarm.Modules.AchievementPacer).GetMethod("DlcChanged", BindingFlags.Static | BindingFlags.NonPublic)!;
	bool HeldForDlc(NocatFarm.Modules.AchievementPacer.Outcome o) => (bool) heldForDlc.Invoke(null, [o])!;
	bool DlcChanged(List<uint>? before, params uint[] now) => (bool) dlcChanged.Invoke(null, [before, new HashSet<uint>(now)])!;
	Check("dlc licences: done for DLC, can't-map and capped are what a licence change can let go - finished and Steam-only aren't",
		HeldForDlc(NocatFarm.Modules.AchievementPacer.Outcome.DlcOnly) && HeldForDlc(NocatFarm.Modules.AchievementPacer.Outcome.DlcUnmapped)
		&& HeldForDlc(NocatFarm.Modules.AchievementPacer.Outcome.Capped) && !HeldForDlc(NocatFarm.Modules.AchievementPacer.Outcome.Complete)
		&& !HeldForDlc(NocatFarm.Modules.AchievementPacer.Outcome.SteamOnly));
	Check("dlc licences: a game is let go when the DLC it owns for it changed - not when some other licence did",
		DlcChanged([10], 10, 20) && DlcChanged([10, 20], 10) && !DlcChanged([20, 10], 10, 20) && !DlcChanged(null) && DlcChanged(null, 10));
	MethodInfo stampOf = typeof(NocatFarm.Core.Bot).GetMethod("StampOf", BindingFlags.Static | BindingFlags.NonPublic)!;
	long Stamp(params uint[] packages) => (long) stampOf.Invoke(null, [packages.AsEnumerable()])!;
	Check("dlc licences: the stamp is the same for the same licences in any order, different when one is added, never 0",
		(Stamp(3, 1, 2) == Stamp(1, 2, 3)) && (Stamp(1, 2, 3) != Stamp(1, 2, 3, 4)) && (Stamp() != 0) && (Stamp(1) != Stamp(2)));

	// Kept on disk: the map reads back the same.
	NocatFarm.Core.DlcAchievements.Map? back = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Core.DlcAchievements.Map>(System.Text.Json.JsonSerializer.Serialize(codMap));
	Check("dlc map: saved and read back, it says the same",
		(back != null) && (back.Owners.Count == 63) && (HoldOf(back, "ACH_1") == NotOwned) && (HoldOf(back, "ACH_100") == Unmapped)
		&& (HoldOf(back, "ACH_100", [.. ownsAllButMw]) == None) && (back.Names?.Count == 157));

	// Every way an achievement gets set asks first.
	string pacerSrc = Src("Modules/AchievementPacer.cs");
	Check("dlc: the pacer only picks what the DLC rule allows, and holds a game it hasn't worked out",
		pacerSrc.Contains("!IsSpecialGlobal(a) && dlc.Allows(a) && ((a.GlobalPercent ?? floor) >= floor)", StringComparison.Ordinal)
		&& pacerSrc.Contains("if (!dlc.Known) {\n\t\t\tlock (_gate) {\n\t\t\t\tg.Last = Outcome.DlcChecking;", StringComparison.Ordinal)
		&& pacerSrc.Contains("Outcome.Complete or Outcome.SteamOnly or Outcome.Capped or Outcome.DlcOnly or Outcome.DlcUnmapped", StringComparison.Ordinal));
	Check("dlc: the pacer keeps the licence stamp and the DLC owned with what it concluded, saved, and re-reads them every tick",
		pacerSrc.Contains("g.Licences = dlc.Licences;\n\t\t\tg.DlcOwned = [.. dlc.Owned.Order()];", StringComparison.Ordinal)
		&& pacerSrc.Contains("\t\tawait RecheckLicencesAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal)
		&& pacerSrc.Contains("Licences = s.Licences", StringComparison.Ordinal) && pacerSrc.Contains("DlcOwned = kv.Value.DlcOwned", StringComparison.Ordinal));
	Check("dlc: an achievement the map has never seen keeps the game 'checking', not done",
		pacerSrc.Contains("if (set.All.Any(a => !a.Unlocked && (dlc.Of(a) == DlcAchievements.Hold.Checking))) {", StringComparison.Ordinal));
	string unlockSrc = Src("Core/UnlockEverything.cs");
	Check("dlc: unlock-everything leaves them, skips a game it can't work out, and doesn't say Steam sets what's held",
		unlockSrc.Contains("locked = [.. locked.Where(dlc.Allows)];", StringComparison.Ordinal)
		&& unlockSrc.Contains("if (!dlc.Known) {\n\t\t\t\tunsure++;", StringComparison.Ordinal)
		&& unlockSrc.Contains("Log.Good(!heldBack && (steamOnly > 0)", StringComparison.Ordinal));
	string commandsSrc = Src("Commands.cs");
	Check("dlc: 'cheevo unlock' refuses one, and 'unlock all' leaves them out",
		commandsSrc.Contains("if ((dlc != null) && !dlc.Allows(one)) {", StringComparison.Ordinal)
		&& commandsSrc.Contains("chosen = [.. chosen.Where(dlc.Allows)];", StringComparison.Ordinal)
		&& commandsSrc.Contains("return new Said(\"Still checking which of {0}'s achievements come with DLC", StringComparison.Ordinal));
	Check("dlc: dlcach never says a game's DLC have no achievements when the store just doesn't say, and says why a game is held",
		!commandsSrc.Contains("None of its DLC have achievements in its list.", StringComparison.Ordinal)
		&& commandsSrc.Contains("\"Held: {0} is missing DLC, and Steam doesn't list which achievements they add", StringComparison.Ordinal));
	string dlcSrc = Src("Core/DlcAchievements.cs");
	Check("dlc: app info answered without its access token is 'couldn't ask', never 'no DLC'",
		dlcSrc.Contains("if (product.MissingToken) {", StringComparison.Ordinal)
		&& dlcSrc[dlcSrc.IndexOf("if (product.MissingToken) {", StringComparison.Ordinal)..].Split("\n\t\t\t\t\t}")[0].Contains("return null;", StringComparison.Ordinal));
	Check("dlc: the game's list is read for every game, DLC or none",
		dlcSrc.Contains("List<Entry>? schema = await SchemaAsync(app, ct).ConfigureAwait(false);", StringComparison.Ordinal)
		&& !dlcSrc.Contains("if (listed.Count > 0) {", StringComparison.Ordinal));
	Check("dlc: dlcach is in the command list", NocatFarm.Commands.All.Any(static c => c.Name == "dlcach"));

	// Pure cosmetics never hold a game. Call of Duty has close to a hundred DLC, nearly all skins and points, and every
	// one of them used to hold the whole game on an account without it. Real store names.
	string[] cosmetic = [
		"Call of Duty®: Modern Warfare® II - Desert Rogue: Pro Pack",
		"Call of Duty®: Modern Warfare® II - Dune Stalker: Starter Pack",
		"Call of Duty®: Modern Warfare® II - BlackCell (Season 03)",
		"Call of Duty®: Black Ops 7 or Call of Duty®: Warzone™ Points",
		"Call of Duty®: Modern Warfare® III - Vault Edition Pack",
		"Call of Duty®: Modern Warfare® II - Tracer Pack: Dark Energy Ultra Skin",
		"Call of Duty®: Modern Warfare® II - Operator Bundle",
		"Call of Duty®: Black Ops 6 - BlackCell Battle Pass",
		"Game - 1,100 Gold",
		"Game - Weapon Camo and Calling Card Set",
		"Game - Supporter Pack",
		"Game - Avatars"
	];
	string[] content = [
		"Call of Duty®: Modern Warfare® II - Campaign",
		"Call of Duty®: Black Ops 6 - Vault Edition Upgrade",
		"Borderlands 2: Ultimate Vault Hunter Upgrade Pack 2",
		"Borderlands 2: Commander Lilith & the Fight for Sanctuary",
		"Borderlands 2: Gunzerker Madness Pack",
		"Borderlands 2: Headhunter 3: Mercenary Day",
		"Borderlands 2 Season Pass",
		"Cuphead - The Delicious Last Course",
		"Game - Zombies Skin Pack",
		"Game - Skins and Map Pack",
		"Game - DLC 2 Outfits",
		"Game - Season Pass Skin Bundle",
		"Game - Tracer Pack Bundle",
		"Game - Gold Edition",
		"Game - Complete Costume Collection",
		"Game - Story Expansion Cosmetic Pack",
		"Game - Deluxe Pack",
		"Game - Starter Bundle",
		"Game - Add-on Content: Skins"
	];
	Check("dlc cosmetic: skins, tracer packs, BlackCell, COD Points, a Vault Edition Pack never hold a game",
		cosmetic.All(static n => NocatFarm.Core.DlcAchievements.Cosmetic(n) && NocatFarm.Core.DlcAchievements.Harmless("dlc", n)),
		string.Join(" | ", cosmetic.Where(static n => !NocatFarm.Core.DlcAchievements.Cosmetic(n))));
	Check("dlc cosmetic: a campaign, an upgrade pack, a story DLC, a season pass - or a pack that doesn't say what's in it - still can",
		!content.Any(static n => NocatFarm.Core.DlcAchievements.Cosmetic(n) || NocatFarm.Core.DlcAchievements.Harmless("dlc", n)),
		string.Join(" | ", content.Where(static n => NocatFarm.Core.DlcAchievements.Harmless("dlc", n))));
	Check("dlc cosmetic: whole words, any case - 'Skinwalker' and 'Pointsman' aren't skins or points, 'SKINS' is",
		!NocatFarm.Core.DlcAchievements.Cosmetic("Game - Skinwalker") && !NocatFarm.Core.DlcAchievements.Cosmetic("Game - The Pointsman")
		&& NocatFarm.Core.DlcAchievements.Cosmetic("GAME - SKINS"));

	// A DLC the store gives figures for is placed by them, whatever it's called; a count with no names holds the game.
	List<NocatFarm.Core.DlcAchievements.Dlc> named = NocatFarm.Core.DlcAchievements.Merge(9, [
		new(91, 91, "Game - Skin Pack", 3, ["Cup 4", "Cup 5", "Cup 6"], Type: "dlc"),
		new(92, 92, "Game - Operator Bundle", 2, [], Type: "dlc"),
		new(93, 0, "Game - Outfits", 0, [], Listed: false)
	]);
	NocatFarm.Core.DlcAchievements.Map namedMap = NocatFarm.Core.DlcAchievements.Assemble(9, "Game", cup, named, 3, 2, DateTime.UtcNow);
	Check("dlc figures win: a 'skin pack' the store gives achievements for is placed by them; one with a count and no names holds the game; a delisted skin pack doesn't",
		namedMap.Owners["c4"].SequenceEqual([91u]) && namedMap.Owners["c6"].SequenceEqual([91u]) && !namedMap.Owners.ContainsKey("c7")
		&& (named.Single(static d => d.App == 92).Unsure == NocatFarm.Core.DlcAchievements.Doubt.NoFigures)
		&& (named.Single(static d => d.App == 93).Unsure == NocatFarm.Core.DlcAchievements.Doubt.None)
		&& (HoldOf(namedMap, "C0", 91) == Unmapped) && (HoldOf(namedMap, "C0", 91, 92) == None) && (HoldOf(namedMap, "C5", 92) == NotOwned));
	Check("dlc map: Call of Duty's skin pack isn't a group at all - only what could hold the game is",
		codMap.Groups.All(static g => g.App != 2127790) && (codMap.Rule == NocatFarm.Core.DlcAchievements.RuleNow));

	// A game the owner vouched for: every DLC counted as owned, placed or not. Only that game.
	HashSet<uint> vouched = NocatFarm.Core.DlcAchievements.Counted(codMap, ownsNothing, trusted: true);
	NocatFarm.Core.DlcAchievements.View trustedView = new() { Map = codMap, Licensed = ownsNothing, Owned = vouched, Trusted = true };
	Check("dlc trusted: every DLC counted as owned - Modern Warfare II's block and the base game both unlockable",
		vouched.SetEquals(codMap.Groups.Select(static g => g.App)) && (HoldOf(codMap, "ACH_0", [.. vouched]) == None)
		&& (HoldOf(codMap, "ACH_30", [.. vouched]) == None) && (HoldOf(codMap, "ACH_140", [.. vouched]) == None));
	Check("dlc trusted: the view holds nothing and isn't 'can't map'; an achievement the map has never seen still waits",
		trustedView.Known && !trustedView.Unmapped && (trustedView.NotOwnedCount == 0) && trustedView.Allows(Ach("ACH_3"))
		&& trustedView.Allows(Ach("ACH_140")) && (trustedView.Of(Ach("ACH_157")) == Checking) && (trustedView.Unplaceable.Count == 0));
	Check("dlc trusted: not vouched for, the licences are all that count; no map, nothing is counted",
		NocatFarm.Core.DlcAchievements.Counted(codMap, ownsMw2, trusted: false).SetEquals(ownsMw2)
		&& (NocatFarm.Core.DlcAchievements.Counted(null, ownsNothing, trusted: true).Count == 0));
	NocatFarm.Core.DlcAchievements.View trustedUnread = new() { Map = codMap, OwnershipRead = false, Trusted = true };
	Check("dlc trusted: licences that couldn't be read are still 'checking' - vouching doesn't skip that",
		!trustedUnread.Known && (trustedUnread.Of(Ach("ACH_140")) == Checking));
	Check("dlc trusted: 'Games I own all the DLC for' is a per-account list, empty by default",
		NocatFarm.Config.Settings.Bot.Any(static d => (d.Name == "AchievementDlcTrusted") && (d.Kind == NocatFarm.Config.SettingKind.AppIds) && (d.Label == "Games I own all the DLC for"))
		&& (new NocatFarm.Config.BotConfig().AchievementDlcTrusted.Count == 0));

	MethodInfo trustKey = typeof(NocatFarm.Modules.AchievementPacer).GetMethod("TrustKey", BindingFlags.Static | BindingFlags.NonPublic)!;
	string TrustKey(List<uint>? apps) => (string) trustKey.Invoke(null, [apps])!;
	Check("dlc trusted: the list is the same in any order - a change is a game added or taken off",
		(TrustKey([10, 20]) == TrustKey([20, 10, 20])) && (TrustKey(null) == TrustKey([])) && (TrustKey([10]) != TrustKey([10, 20])));
	Check("dlc trusted: vouching lets a held game go - the pacer re-reads held games when the list changes, the view reads it each time",
		pacerSrc.Contains("bool trustChanged = trusted != _trustSeen;", StringComparison.Ordinal)
		&& pacerSrc.Contains("if (!trustChanged && (licences == stamp) && !mapChanged) {", StringComparison.Ordinal)
		&& dlcSrc.Contains("bool trusted = bot.Cfg.AchievementDlcTrusted.Contains(app);", StringComparison.Ordinal)
		&& dlcSrc.Contains("Owned = Counted(map, licensed, trusted)", StringComparison.Ordinal));
	Check("dlc trusted: dlcach says a game is vouched for, and a held game says how to vouch for it",
		commandsSrc.Contains("\"Trusted: you listed it as owning all its DLC", StringComparison.Ordinal)
		&& commandsSrc.Contains("lines.Add(\"  \" + VouchHint(bot, app));", StringComparison.Ordinal)
		&& commandsSrc.Contains("+ \" \" + VouchHint(bot, appId)", StringComparison.Ordinal));
	Check("dlc rules: a map built by older rules is built again",
		dlcSrc.Contains("(map.Rule < RuleNow)", StringComparison.Ordinal));

	// ── round 6 ──────────────────────────────────────────────────────────────

	// 1. Only a licence the account has for good is owning a DLC: not a free weekend, a timed trial, a guest pass, or
	// one that ran out, was cancelled or hasn't gone through yet.
	MethodInfo isPermanent = typeof(NocatFarm.Core.Bot).GetMethod("IsPermanent", BindingFlags.Static | BindingFlags.NonPublic)!;
	bool Permanent(SteamKit2.ELicenseFlags flags, int minutes = 0, SteamKit2.ELicenseType type = SteamKit2.ELicenseType.SinglePurchase,
		SteamKit2.EPaymentMethod method = SteamKit2.EPaymentMethod.CreditCard) => (bool) isPermanent.Invoke(null, [flags, minutes, type, method])!;
	Check("dlc own: a plain bought licence, one that renews, a subscription and a gift are for good",
		Permanent(SteamKit2.ELicenseFlags.None) && Permanent(SteamKit2.ELicenseFlags.Renew)
		&& Permanent(SteamKit2.ELicenseFlags.None, type: SteamKit2.ELicenseType.RecurringCharge)
		&& Permanent(SteamKit2.ELicenseFlags.None, method: SteamKit2.EPaymentMethod.GuestPass)
		&& Permanent(SteamKit2.ELicenseFlags.None, method: SteamKit2.EPaymentMethod.Complimentary));
	Check("dlc own: expired, pending, cancelled (by the user, by Steam, for fraud) and not activated are not",
		!Permanent(SteamKit2.ELicenseFlags.Expired) && !Permanent(SteamKit2.ELicenseFlags.Pending)
		&& !Permanent(SteamKit2.ELicenseFlags.CancelledByUser) && !Permanent(SteamKit2.ELicenseFlags.CancelledByAdmin)
		&& !Permanent(SteamKit2.ELicenseFlags.CancelledByFriendlyFraudLock) && !Permanent(SteamKit2.ELicenseFlags.NotActivated)
		&& !Permanent(SteamKit2.ELicenseFlags.Renew | SteamKit2.ELicenseFlags.Expired));
	Check("dlc own: a free weekend or timed trial (a minute limit, a limited-use licence, a guest pass that isn't a gift) is not",
		!Permanent(SteamKit2.ELicenseFlags.None, minutes: 120)
		&& !Permanent(SteamKit2.ELicenseFlags.None, type: SteamKit2.ELicenseType.SinglePurchaseLimitedUse)
		&& !Permanent(SteamKit2.ELicenseFlags.None, type: SteamKit2.ELicenseType.LimitedUseDelayedActivation)
		&& !Permanent(SteamKit2.ELicenseFlags.None, type: SteamKit2.ELicenseType.RecurringChargeLimitedUse)
		&& !Permanent(SteamKit2.ELicenseFlags.None, type: SteamKit2.ELicenseType.NoLicense)
		&& !Permanent(SteamKit2.ELicenseFlags.None, type: SteamKit2.ELicenseType.RecurringCharge, method: SteamKit2.EPaymentMethod.GuestPass));
	DateTime early = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), late = early.AddYears(1);
	NocatFarm.Core.AppOwnership ownExpired = new(late, true, Own: true, Permanent: false);
	NocatFarm.Core.AppOwnership familyCopy = new(early, true, Own: false, Permanent: false);
	NocatFarm.Core.AppOwnership ownForGood = new(late, false, Own: true, Permanent: true);
	NocatFarm.Core.AppOwnership joinedFamily = ownExpired.Join(familyCopy), joinedGood = familyCopy.Join(ownForGood);
	Check("dlc own: two licences for one app - earliest date, paid if either, for good only from the account's own licence for good",
		joinedFamily is { Own: true, Permanent: false, Paid: true } && (joinedFamily.Since == early)
		&& joinedGood is { Own: true, Permanent: true, Paid: true } && (joinedGood.Since == early));
	Dictionary<uint, NocatFarm.Core.AppOwnership> expiredMw2 = new() { [1962660] = ownExpired, [3595230] = familyCopy };
	Dictionary<uint, NocatFarm.Core.AppOwnership> boughtMw2 = new() { [3595230] = ownForGood };
	Check("dlc own: a free-weekend or run-out Modern Warfare II isn't owning it; bought, it is",
		(NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, id => expiredMw2.TryGetValue(id, out NocatFarm.Core.AppOwnership o) && o.Permanent).Count == 0)
		&& NocatFarm.Core.DlcAchievements.OwnedGroups(codMap, id => boughtMw2.TryGetValue(id, out NocatFarm.Core.AppOwnership o) && o.Permanent).SetEquals([1962660u]));
	string botSrc = Src("Core/Bot.cs");
	Check("dlc own: the DLC rule and the library's play history ask 'for good', and the licence stamp counts only those",
		dlcSrc.Contains("licences.TryGetValue(id, out AppOwnership o) && o.Permanent", StringComparison.Ordinal)
		&& Src("Core/Library.cs").Contains("licensed.TryGetValue(app, out AppOwnership o) && o.Permanent", StringComparison.Ordinal)
		&& botSrc.Contains("_licenseStamp = StampOf(_licenses.Where(static l => l.Value.Own && l.Value.Permanent)", StringComparison.Ordinal)
		&& botSrc.Contains("IsPermanent(license.LicenseFlags, license.MinuteLimit, license.LicenseType, license.PaymentMethod)", StringComparison.Ordinal)
		&& Src("Core/RefundGuard.cs").Contains("own.Refundable(bot.Cfg.ProtectGiftedGames)", StringComparison.Ordinal));

	// 2. A block drawn generously holds back; only its certain part lets an achievement go in a game that can't be
	// mapped. Base 0-4, a DLC nobody can place at 5-7 (hidden), an owned DLC at 8-11.
	NocatFarm.Core.DlcAchievements.Entry Row(int i, bool hide = false) => new($"E{i}", $"Probe {i}", "Do a probe thing.", hide);
	List<NocatFarm.Core.DlcAchievements.Entry> probe = [.. Enumerable.Range(0, 12).Select(i => Row(i, i is >= 5 and <= 7))];
	NocatFarm.Core.DlcAchievements.Map probeMap = NocatFarm.Core.DlcAchievements.Assemble(70, "Probe", probe, NocatFarm.Core.DlcAchievements.Merge(70, [
		new(100, 100, "Probe - Owned Story", 4, ["Probe 8", "Probe 9", "Probe 10", "Probe 11"], Type: "dlc"),
		new(200, 200, "Probe - Mystery Content", 0, [], Type: "dlc")
	], "Probe"), 2, 2, DateTime.UtcNow);
	(List<int> generous, bool probeExact, List<int> strictBlock) = NocatFarm.Core.DlcAchievements.LocateBoth(
		NocatFarm.Core.DlcAchievements.Places([.. probe.Select(static e => e.Display)], ["Probe 8", "Probe 9", "Probe 10", "Probe 11"]), 4, 12, [.. probe.Select(static e => e.Hidden)]);
	Check("dlc strict: the owned DLC's block is 5-11 to hold back, 8-11 for certain",
		probeExact && generous.SequenceEqual(Range(5, 7)) && strictBlock.SequenceEqual(Range(8, 4)), $"{string.Join(",", generous)} / {string.Join(",", strictBlock)}");
	Check("dlc strict: owning A but not the unplaceable B - B's hidden ones just before A are held, A's own are fine, the base game held",
		(HoldOf(probeMap, "E5", 100) == Unmapped) && (HoldOf(probeMap, "E6", 100) == Unmapped) && (HoldOf(probeMap, "E7", 100) == Unmapped)
		&& (HoldOf(probeMap, "E8", 100) == None) && (HoldOf(probeMap, "E11", 100) == None) && (HoldOf(probeMap, "E0", 100) == Unmapped));
	Check("dlc strict: without A the whole generous block is held; owning both, the game can be mapped and it's all fine",
		(HoldOf(probeMap, "E5") == NotOwned) && (HoldOf(probeMap, "E8") == NotOwned)
		&& (HoldOf(probeMap, "E5", 100, 200) == None) && (HoldOf(probeMap, "E0", 100, 200) == None));
	NocatFarm.Core.DlcAchievements.View probeView = new() { Map = probeMap, Owned = [100] };
	Check("dlc strict: a held one in an owned block names the DLC that can't be placed as the reason",
		probeView.Missing(Ach("E6")).SequenceEqual(["Probe - Mystery Content"]) && !probeView.Allows(Ach("E6")) && probeView.Allows(Ach("E9")));
	NocatFarm.Core.DlcAchievements.Map? probeBack = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Core.DlcAchievements.Map>(System.Text.Json.JsonSerializer.Serialize(probeMap));
	NocatFarm.Core.DlcAchievements.Map probeOld = System.Text.Json.JsonSerializer.Deserialize<NocatFarm.Core.DlcAchievements.Map>(System.Text.Json.JsonSerializer.Serialize(probeMap))!;
	probeOld.Sure = null;
	Check("dlc strict: kept on disk; a map saved before it was kept lets nothing go in a game it can't map",
		(probeBack != null) && (HoldOf(probeBack, "E8", 100) == None) && (HoldOf(probeBack, "E6", 100) == Unmapped)
		&& (HoldOf(probeOld, "E8", 100) == Unmapped) && (HoldOf(probeOld, "E8", 100, 200) == None));

	// Avatar: Frontiers of Pandora's shape: a run of hidden base-game ones right before an owned DLC's block, another DLC
	// placed after it, and one that can't be placed.
	List<NocatFarm.Core.DlcAchievements.Entry> pandora = [.. Enumerable.Range(0, 33).Select(i => Row(i, i is >= 20 and <= 22))];
	List<NocatFarm.Core.DlcAchievements.Dlc> pandoraDlc = NocatFarm.Core.DlcAchievements.Merge(2840770, [
		new(2840780, 2840780, "Avatar: Frontiers of Pandora™ – The Sky Breaker", 5, [.. Enumerable.Range(23, 5).Select(static i => $"Probe {i}")], Type: "dlc"),
		new(2840790, 2840790, "Avatar: Frontiers of Pandora™ – Secrets of The Spires", 5, [.. Enumerable.Range(28, 5).Select(static i => $"Probe {i}")], Type: "dlc"),
		new(2840800, 2840800, "Avatar: Frontiers of Pandora™ – Resistance Pack", 0, [], Type: "dlc")
	], "Avatar: Frontiers of Pandora™");
	NocatFarm.Core.DlcAchievements.Map pandoraMap = NocatFarm.Core.DlcAchievements.Assemble(2840770, "Avatar: Frontiers of Pandora™", pandora, pandoraDlc, 3, 3, DateTime.UtcNow);
	Check("dlc strict: Avatar-shaped - owning The Sky Breaker only: the hidden run before it held, its block fine, Spires' held, base held",
		(pandoraDlc.Single(static d => d.App == 2840800).Unsure == NocatFarm.Core.DlcAchievements.Doubt.NoFigures)
		&& (HoldOf(pandoraMap, "E20", 2840780) == Unmapped) && (HoldOf(pandoraMap, "E22", 2840780) == Unmapped)
		&& (HoldOf(pandoraMap, "E23", 2840780) == None) && (HoldOf(pandoraMap, "E27", 2840780) == None)
		&& (HoldOf(pandoraMap, "E28", 2840780) == NotOwned) && (HoldOf(pandoraMap, "E0", 2840780) == Unmapped));
	Check("dlc strict: Avatar-shaped - owning the unplaceable one too, the hidden run is the base game's again; without The Sky Breaker it's held",
		(HoldOf(pandoraMap, "E21", 2840780, 2840800) == None) && (HoldOf(pandoraMap, "E0", 2840780, 2840800) == None)
		&& (HoldOf(pandoraMap, "E21", 2840800) == NotOwned) && (HoldOf(pandoraMap, "E28", 2840780, 2840800) == NotOwned));

	// 3. Cosmetic: the game's own name is taken off first, words that can mean something else need a pack, more words
	// say there's something to play, and a name these lists can't read isn't called cosmetic.
	bool Cos(string n, string game = "") => NocatFarm.Core.DlcAchievements.Cosmetic(n, game);
	string[] notCosmetic = [
		"Survival Points: Arena Challenge", "Operator Pack: Heist Adventure", "Skin Pack + New Arena", "Coins & New Adventure",
		"Closing Credits", "Charm City", "The Profile of Evil", "Season 2 Gold Edition", "Skin Pack 新しいマップ",
		"Game - Second Skin", "Game - Tales of the Island", "Game - Act II", "Game - Operation Blackout", "Game - Trials Pack",
		"Game - Avatar", "Game - Emblem", "Game - 2,000 Gold Edition", "Game - Скины", "Game - Points of No Return"
	];
	Check("dlc cosmetic: round 6 probes - none of them is cosmetic",
		!notCosmetic.Any(n => Cos(n) || NocatFarm.Core.DlcAchievements.Harmless("dlc", n)), string.Join(" | ", notCosmetic.Where(n => Cos(n))));
	Check("dlc cosmetic: Avatar: Frontiers of Pandora's story DLC aren't avatar packs - with the game's name or without",
		!Cos("Avatar: Frontiers of Pandora™ – The Sky Breaker", "Avatar: Frontiers of Pandora")
		&& !Cos("Avatar: Frontiers of Pandora™ – Secrets of The Spires", "Avatar: Frontiers of Pandora™")
		&& !Cos("Avatar: Frontiers of Pandora™ – The Sky Breaker") && !Cos("Avatar: Frontiers of Pandora™ – Secrets of The Spires")
		&& (pandoraDlc.Single(static d => d.App == 2840780).Unsure == NocatFarm.Core.DlcAchievements.Doubt.None));
	string[] codCosmetic = [
		"Call of Duty®: Modern Warfare® II - Desert Rogue: Pro Pack",
		"Call of Duty®: Modern Warfare® II - BlackCell (Season 03)",
		"Call of Duty®: Black Ops 7 or Call of Duty®: Warzone™ Points",
		"Call of Duty®: Modern Warfare® II - Tracer Pack: Dark Energy Ultra Skin",
		"Call of Duty®: Modern Warfare® III - Vault Edition Pack",
		"Call of Duty®: Modern Warfare® II - Khaled Al-Asad Operator Bundle",
		"Call of Duty®: Modern Warfare® III - 2,400 Call of Duty® Points",
		"Game - Charm Pack", "Game - Weapon Charms", "Game - Profile Backgrounds", "Game - Profile Pack", "Game - Avatar Pack",
		"Game - 500 Credits", "Game - Currency Pack", "Game - Skin Pack", "Game - Legendary Skin", "Game - Emblem Pack"
	];
	Check("dlc cosmetic: Call of Duty's skins, tracer packs, BlackCell, Points, operator bundles still are - with the game's name taken off too",
		codCosmetic.All(n => Cos(n) && Cos(n, "Call of Duty®") && NocatFarm.Core.DlcAchievements.Harmless("dlc", n, "Call of Duty®")),
		string.Join(" | ", codCosmetic.Where(n => !Cos(n) || !Cos(n, "Call of Duty®"))));
	// A number and a money word only make a currency pack at the end of the name or before "pack" - not in a story's title.
	Check("dlc cosmetic: \"1849 Gold Rush\" and \"2 Gems of Darkness\" are stories, \"1,100 Gold\" and \"500 Credits Pack\" are currency",
		!Cos("Game - 1849 Gold Rush") && !Cos("Game - 2 Gems of Darkness") && !Cos("Game - 300 Points of Light")
		&& !Cos("Game - 500 Gold Setup") && !Cos("Game - 300 Coins Settlers") && !Cos("Game - 1000 Credits Package")
		&& !Cos("Game - The 7 Gems") && !Cos("Game - Lost 3 Gold")
		&& Cos("Game - 1,100 Gold") && Cos("Game - 500 Credits Pack") && Cos("Game - 500 Credits") && Cos("Game - 2,000 Gems Bundle"));
	Check("dlc cosmetic: the game's own name comes off word by word, whatever its marks and separators",
		(NocatFarm.Core.DlcAchievements.WithoutGame("Avatar: Frontiers of Pandora™ – The Sky Breaker", "Avatar: Frontiers of Pandora") == "the sky breaker")
		&& (NocatFarm.Core.DlcAchievements.WithoutGame("Fallout New Vegas: Dead Money", "Fallout: New Vegas") == "dead money")
		&& (NocatFarm.Core.DlcAchievements.WithoutGame("Skins", "Zombie Game") == "skins")
		&& !Cos("Zombie Army 4", "Zombie Army 4") && Cos("Zombie Army 4: Skin Pack", "Zombie Army 4") && !Cos("Zombie Army 4: Skin Pack"));
	NocatFarm.Core.DlcAchievements.Map ruleTwo = new() { App = 7, BuiltAt = DateTime.UtcNow, Rule = 2, Names = ["c4"] };
	Check("dlc cosmetic: the rules moved on - a map built by the old ones is built again",
		(NocatFarm.Core.DlcAchievements.RuleNow == 5) && NocatFarm.Core.DlcAchievements.Stale(ruleTwo) && !NocatFarm.Core.DlcAchievements.Stale(noDlc));

	// 4. A game with DLC held back and some only Steam can award is held for DLC - so buying the DLC lets it go.
	Check("dlc steam-only: held for DLC whenever any are held, and a game saved as Steam-only with some held is moved over",
		pacerSrc.Contains("bool dlcOnly = held > 0;", StringComparison.Ordinal)
		&& pacerSrc.Contains("if ((g.Last == Outcome.SteamOnly) && (g.DlcHeld > 0)) {\n\t\t\t\t\t\tg.Last = g.Unmapped ? Outcome.DlcUnmapped : Outcome.DlcOnly;", StringComparison.Ordinal)
		&& pacerSrc.Contains("\"{0}: {1}/{2} - {3} are from DLC this account doesn't own, and only Steam can award the rest\"", StringComparison.Ordinal)
		&& Src("wwwroot/app.js").Contains("live.DlcHeld < live.Total - live.Unlocked", StringComparison.Ordinal));

	// 5. The map changing lets a held game go too, and a map built without every name is built again in an hour.
	NocatFarm.Core.DlcAchievements.Map hurriedNew = new() { App = 7, BuiltAt = DateTime.UtcNow.AddMinutes(-20), Rule = NocatFarm.Core.DlcAchievements.RuleNow, Names = ["c4"], Hurried = true };
	NocatFarm.Core.DlcAchievements.Map hurriedOld = new() { App = 7, BuiltAt = DateTime.UtcNow.AddMinutes(-70), Rule = NocatFarm.Core.DlcAchievements.RuleNow, Names = ["c4"], Hurried = true };
	NocatFarm.Core.DlcAchievements.Map dayOld = new() { App = 7, BuiltAt = DateTime.UtcNow.AddDays(-2), Rule = NocatFarm.Core.DlcAchievements.RuleNow, Names = ["c4"] };
	NocatFarm.Core.DlcAchievements.Map weekOld = new() { App = 7, BuiltAt = DateTime.UtcNow.AddDays(-8), Rule = NocatFarm.Core.DlcAchievements.RuleNow, Names = ["c4"] };
	Check("dlc map age: built without every name - again after an hour; otherwise after a week",
		!NocatFarm.Core.DlcAchievements.Stale(hurriedNew) && NocatFarm.Core.DlcAchievements.Stale(hurriedOld)
		&& !NocatFarm.Core.DlcAchievements.Stale(dayOld) && NocatFarm.Core.DlcAchievements.Stale(weekOld) && NocatFarm.Core.DlcAchievements.Stale(null));
	List<NocatFarm.Core.DlcAchievements.Dlc> nameless = NocatFarm.Core.DlcAchievements.Merge(268910, [new(1117851, 0, "", 0, [], Listed: false)]);
	List<NocatFarm.Core.DlcAchievements.Dlc> namedNow = NocatFarm.Core.DlcAchievements.Merge(268910, [new(1117851, 0, "Cuphead - Official Soundtrack", 0, [], Listed: false)]);
	NocatFarm.Core.DlcAchievements.Map before = NocatFarm.Core.DlcAchievements.Assemble(268910, "Cuphead", cup, nameless, 1, 0, DateTime.UtcNow.AddHours(-2));
	NocatFarm.Core.DlcAchievements.Map rebuiltSame = NocatFarm.Core.DlcAchievements.Assemble(268910, "Cuphead", cup, nameless, 1, 0, DateTime.UtcNow);
	NocatFarm.Core.DlcAchievements.Map rebuiltNamed = NocatFarm.Core.DlcAchievements.Assemble(268910, "Cuphead", cup, namedNow, 1, 0, DateTime.UtcNow);
	Check("dlc map change: a new build is a new stamp; what it says is the same when nothing changed, different once the DLC could be named",
		(NocatFarm.Core.DlcAchievements.Stamp(before) != NocatFarm.Core.DlcAchievements.Stamp(rebuiltSame)) && (NocatFarm.Core.DlcAchievements.Stamp(null) == 0)
		&& (NocatFarm.Core.DlcAchievements.HoldKey(before, new HashSet<uint>()) == NocatFarm.Core.DlcAchievements.HoldKey(rebuiltSame, new HashSet<uint>()))
		&& (NocatFarm.Core.DlcAchievements.HoldKey(before, new HashSet<uint>()) != NocatFarm.Core.DlcAchievements.HoldKey(rebuiltNamed, new HashSet<uint>()))
		&& (HoldOf(before, "C1") == Unmapped) && (HoldOf(rebuiltNamed, "C1") == None)
		&& (NocatFarm.Core.DlcAchievements.HoldKey(before, new HashSet<uint>()) != NocatFarm.Core.DlcAchievements.HoldKey(before, new HashSet<uint> { 1117851 })));
	Check("dlc map change: a held game is looked at again when its map is built again and says something else, and its map is kept fresh",
		pacerSrc.Contains("? Bot.Cfg.AchievementDlcTrusted.Contains(app) || ((licences != 0) && DlcChanged(before, view.Owned))\n\t\t\t\t: (DlcChanged(before, view.Owned) || (key != holdKey));", StringComparison.Ordinal)
		// Held before any of this was recorded: noted down, not let go - and not asked for urgently every minute while a
		// rebuild is under way; a stale map whose rebuild failed is still looked at.
		&& pacerSrc.Contains("bool first = holdKey == 0;", StringComparison.Ordinal)
		&& pacerSrc.Contains("if ((map == null) || (DlcAchievements.Stale(map) && DlcAchievements.IsPending(app))) {", StringComparison.Ordinal)
		// ...and a vouch made while it waits isn't marked as seen until the game has been looked at.
		&& pacerSrc.Contains("if (trustChanged && (map != null)) {\n\t\t\t\t\tall = false;\n\t\t\t\t}\n\n\t\t\t\tcontinue;", StringComparison.Ordinal)
		// A free weekend's licence looks like a purchase: the package says it's a free weekend. Not its end date - a
		// free-to-keep giveaway has one too.
		&& Src("Core/Bot.cs").Contains("bool timed = package.KeyValues[\"extended\"][\"freeweekend\"].AsInteger() != 0;", StringComparison.Ordinal)
		&& !Src("Core/Bot.cs").Contains("[\"expirytime\"]", StringComparison.Ordinal)
		// Part of Steam's answer is no answer, for ownership and for DLC names alike.
		&& Src("Core/Bot.cs").Contains("if ((pages == null) || !result.Complete) {", StringComparison.Ordinal)
		&& Src("Core/DlcAchievements.cs").Contains("if (info.Failed || (info.Results == null) || !info.Complete) {", StringComparison.Ordinal)
		// No ownership answer keeps refund protection's holds rather than letting every game go.
		&& Src("Core/RefundGuard.cs").Contains("bool unanswered = owned.Count == 0;", StringComparison.Ordinal)
		&& Src("Core/RefundGuard.cs").Contains("if (unanswered) {\n\t\t\t\t\tif (_held.Contains(game.AppId)) {\n\t\t\t\t\t\theld.Add(game.AppId);", StringComparison.Ordinal)
		// ...while a family-shared game, judged from the library alone, is still held without that answer.
		&& (Src("Core/RefundGuard.cs").IndexOf("if (game.Shared) {", StringComparison.Ordinal) < Src("Core/RefundGuard.cs").IndexOf("if (unanswered) {\n\t\t\t\t\tif", StringComparison.Ordinal))
		&& !Src("Core/RefundGuard.cs").Contains("TimeSpan.FromMinutes(17);\n\n\t\t\t\treturn;", StringComparison.Ordinal)
		// A change to the vouched-for list stays unseen until a whole pass has seen it.
		&& pacerSrc.Contains("} else if (trustChanged) {\n\t\t\t_trustSeen = null;", StringComparison.Ordinal)
		&& pacerSrc.Contains("if (DlcAchievements.Stale(map)) {\n\t\t\t\tDlcAchievements.Request(Bot, app);", StringComparison.Ordinal)
		&& pacerSrc.Contains("g.MapStamp = mapStamp;\n\t\t\tg.HoldKey = holdKey;", StringComparison.Ordinal)
		&& pacerSrc.Contains("MapStamp = s.MapStamp,\n\t\t\t\t\t\tHoldKey = s.HoldKey", StringComparison.Ordinal)
		&& dlcSrc.Contains("map.Hurried = !answered;", StringComparison.Ordinal));

	// 6. Two pages that come to the same DLC are one, whichever came first.
	NocatFarm.Core.DlcAchievements.Page redirect = new(100, 200, "Game - Story One", 5, ["Cup 1", "Cup 2"], Type: "dlc");
	NocatFarm.Core.DlcAchievements.Page gone = new(200, 0, "", 0, [], Listed: false);
	NocatFarm.Core.DlcAchievements.Dlc one = NocatFarm.Core.DlcAchievements.Merge(9, [redirect, gone]).Single();
	NocatFarm.Core.DlcAchievements.Dlc other = NocatFarm.Core.DlcAchievements.Merge(9, [gone, redirect]).Single();
	Check("dlc merge: a page another DLC's page sends to - either order, every id kept, the figures kept, the doubt kept",
		one.Ids.Order().SequenceEqual([100u, 200u]) && other.Ids.Order().SequenceEqual([100u, 200u])
		&& (one.Total == 5) && (other.Total == 5) && (one.Highlighted.Count == 2) && (other.Highlighted.Count == 2)
		&& (one.Unsure == NocatFarm.Core.DlcAchievements.Doubt.Delisted) && (other.Unsure == NocatFarm.Core.DlcAchievements.Doubt.Delisted)
		&& (other.Name == "Game - Story One"), $"{string.Join(",", one.Ids)} {one.Total} {one.Unsure} / {string.Join(",", other.Ids)} {other.Total} {other.Unsure}");
	NocatFarm.Core.DlcAchievements.Dlc basePageAfter = NocatFarm.Core.DlcAchievements.Merge(9, [new(100, 300, "X", 4, ["Cup 3"], Type: "dlc"), new(300, 9, "Game", 30, [], Type: "game")]).Single();
	Check("dlc merge: a page that is the game's own, landing on one with figures, keeps both ids and can't be placed",
		basePageAfter.Ids.Order().SequenceEqual([100u, 300u]) && (basePageAfter.Unsure == NocatFarm.Core.DlcAchievements.Doubt.BasePage));

	// Harden: the DLC rule is asked again under the write's gate.
	string achSrc = Src("Core/Achievements.cs");
	Check("dlc write: licences and the vouched-for list are asked again right before the write, by every caller",
		achSrc.Contains("if (unlock && (dlc != null) && !dlc.StillSo(bot)) {", StringComparison.Ordinal)
		&& (achSrc.IndexOf("!dlc.StillSo(bot)", StringComparison.Ordinal) > achSrc.IndexOf("await gate.WaitAsync(ct)", StringComparison.Ordinal))
		&& pacerSrc.Contains("SetAsync(Bot, set, [pick], true, ct, dlc)", StringComparison.Ordinal)
		&& commandsSrc.Contains("SetAsync(bot, set, chosen, unlock, dlc: dlc)", StringComparison.Ordinal)
		&& unlockSrc.Contains("SetAsync(bot, set, locked, true, dlc: dlc)", StringComparison.Ordinal)
		&& new NocatFarm.Core.DlcAchievements.View { Map = null }.StillSo(null!) && new NocatFarm.Core.DlcAchievements.View { Map = noDlc }.StillSo(null!));
}

// SETTINGSCOUNT
Console.WriteLine($"settings: {NocatFarm.Config.Settings.Global.Count} global ({NocatFarm.Config.Settings.Global.Count(d => !d.Advanced)} basic), {NocatFarm.Config.Settings.Bot.Count} per account ({NocatFarm.Config.Settings.Bot.Count(d => !d.Advanced)} basic)");
Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
return fails;

/// <summary>A module that notes whether the account still had its sign-in time when it was stopped.</summary>
sealed class RaceProbeModule(NocatFarm.Core.Bot bot) : NocatFarm.Core.IBotModule {
	public bool? SawSignIn { get; set; }
	public string Name => "race-probe";
	public string Status => "";
	public Task StartAsync() => Task.CompletedTask;

	public Task StopAsync() {
		SawSignIn = bot.OnlineSince != null;
		return Task.CompletedTask;
	}
}
