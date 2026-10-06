using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm;

/// <summary>
/// What Steam's community market says things sell for, cached to disk and shared by every account.
///
/// One price book for the whole program on purpose: three accounts holding the same cases and the same cards
/// would otherwise ask the market the same question three times, and the market's rate limit is the tightest
/// thing Steam has - roughly twenty questions a minute before it starts answering with nothing at all. So
/// lookups are spaced out, capped per sweep, and a price is kept for a day before it's asked again.
///
/// The market also changes what it will answer without a word, so there are fallbacks: two ways of asking (a refusal
/// from a way that hasn't answered lately sends the next request the other way), a short pause that grows while the
/// market keeps refusing, and single lookups when its searches are refused but those aren't. Every request, whichever
/// way and however many tries, goes through one queue at one pace.
///
/// Nothing here is ever load-bearing: an unknown price simply counts as zero and is looked up later, which is why
/// a fresh install shows a value that climbs for a few minutes and then settles - and while the market refuses, the
/// last prices it gave stand, and the dashboard and "inventory" say from when.
/// </summary>
public static partial class PriceBook {
	/// <summary>
	/// Seconds between market requests, on average - every one of them: both ways of asking, a second try, a search page,
	/// the level planner's.
	/// </summary>
	/// <remarks>
	/// Everything the app does on steamcommunity.com shares one budget, so a sweep running flat out doesn't just get
	/// itself refused, it starts costing the accounts' OWN requests: trade offers came back 429 while prices were being
	/// hammered. And five seconds was too fast for the market itself: twelve a minute, sustained for as long as it takes
	/// to price several hundred items, tripped its limit fourteen times in one day. Hence the setting, ten by default.
	/// </remarks>
	private static double GapSeconds => GapForTests ?? Math.Clamp(Live.Global.MarketGapSeconds, 1, 60);

	/// <summary>
	/// The gap before the next request: <see cref="GapSeconds"/> give or take a quarter, never under three quarters of it.
	/// </summary>
	/// <remarks>
	/// Requests exactly ten seconds apart for an hour are a machine's rhythm. The give and take is drawn in fours - two
	/// amounts, each used once longer and once shorter, in a random order - so every four gaps add up to exactly four:
	/// the rhythm wanders, and the pace never goes over one request per gap.
	/// </remarks>
	internal static TimeSpan NextGap() {
		double share;

		lock (Wobble) {
			if (Wobble.Count == 0) {
				double a = Rng.Next(0, 251) / 1000d, b = Rng.Next(0, 251) / 1000d;

				foreach (double d in ((double[]) [-a, a, -b, b]).OrderBy(static _ => Rng.Next(0, 1 << 20))) {
					Wobble.Enqueue(d);
				}
			}

			share = Wobble.Dequeue();
		}

		return TimeSpan.FromSeconds(GapSeconds * (1 + share));
	}

	/// <summary>The give and take still to come of the current four - see <see cref="NextGap"/>.</summary>
	private static readonly Queue<double> Wobble = new();

	/// <summary>The checks run thousands of pretend lookups; at a second each they would take an hour.</summary>
	internal static double? GapForTests { get; set; }

	/// <summary>How long a price is trusted before it is worth asking again. Fewer asks, fewer rate limits.</summary>
	private static TimeSpan MaxAge => TimeSpan.FromHours(Math.Clamp(Live.Global.PriceCacheHours, 1, 168));

	/// <summary>How long one price is trusted, which depends on what it is worth.</summary>
	/// <remarks>
	/// Measured on a real 929-item inventory: 47% of items had no market listing at all and another 48% were worth
	/// under 25 cents, yet every one of them was asked about again every 12 hours. With one lookup per 15 seconds
	/// that is pricing round the clock - about 1,800 lookups a day - and the market answered 429 about eleven times
	/// a day for it. The 31 items holding 98% of the value are what the setting is really for. Everything cheaper
	/// changes less and moves the total less, so it is asked about less often: around 85% fewer lookups, and the
	/// number that matters is exactly as fresh as it was.
	/// </remarks>
	private static TimeSpan AgeFor(decimal usd) {
		TimeSpan age = usd switch {
			<= 0m => TimeSpan.FromDays(7),   // no listing at all - that rarely changes
			< 0.25m => MaxAge * 6,
			< 2m => MaxAge * 2,
			_ => MaxAge
		};

		return age < MaxAge ? MaxAge : (age > TimeSpan.FromDays(14) ? TimeSpan.FromDays(14) : age);
	}

	private sealed class Price {
		public decimal Usd { get; set; }
		public long At { get; set; }   // unix seconds

		public DateTime When => DateTimeOffset.FromUnixTimeSeconds(At).UtcDateTime;
	}

	private static readonly Dictionary<string, Price> Cache = new(StringComparer.Ordinal);
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>
	/// Its own clients, signed in as nobody - for every price there is: priceoverview, search pages, the level planner's.
	///
	/// Prices are public - the market answers this question to anyone - and asking it through an account's
	/// session gets that SESSION rate-limited, which is both stricter and far more annoying: after a few hundred
	/// lookups Steam simply stopped answering the accounts, and every sweep after that gave up on its first item
	/// while the same URL fetched fine from anywhere else. Searches went through a robot account's session for a
	/// while, for a hundred results a page instead of ten, and the very first one was refused (429) while the same
	/// search signed out, from the same address at the same moment, was answered. Whether that was the account's own
	/// market standing or the browser string the session sent (which the market later refused outright, see
	/// <see cref="Browser.Market"/>) was never settled - either way a price must never depend on an account, and an
	/// account's session is never risked on the market for one. Nothing here needs to know who is asking.
	/// </summary>
	private static HttpClient Http = Browser.Market(TimeSpan.FromSeconds(20));   // not readonly: the checks answer for the market

	/// <summary>The second way of asking: as a desktop browser (<see cref="Browser.MarketAsBrowser"/>). Not readonly either.</summary>
	private static HttpClient HttpB = Browser.MarketAsBrowser(TimeSpan.FromSeconds(20));

	// ── two ways of asking ──────────────────────────────────────────────────
	// 0 is the app's own name (Http), 1 a desktop browser (HttpB). The market's rules change without a word: on
	// 2026-10-05 it was refusing every request with no Accept header, and a browser string it had taken before, while
	// the same requests written differently were answered. The next change won't be announced either. So a refusal
	// from a way that hasn't answered lately is followed at once by one asked the other way, and the way that last got
	// an answer is the one used - remembered across restarts, like the pause.
	//
	// But both ways come from the same internet connection, and the market's commonest refusal is its limit on that
	// connection: 870 lookups answered, then a 429, and the same item asked the other way eight seconds later was
	// refused too - and so on at every step of the pause, two requests into a limit that only grows with each. So a way
	// that was answering until a moment ago and is now refused is the limit, not the way: it pauses at once, and the one
	// request at the end of each pause goes the other way from the one before, so both are still tried.

	/// <summary>The way the next request goes.</summary>
	private static int _way;

	/// <summary>The way that last got an answer: where every fresh run of requests starts.</summary>
	private static int _goodWay;

	/// <summary>Requests refused in a row since the last answer. Two - one each way - is the market refusing.</summary>
	private static int _refusedInRow;

	/// <summary>When each way last got an answer, since the start. Never written down: a restart may be a new version,
	/// asking differently, and nothing it hasn't seen answered counts as answering.</summary>
	private static readonly DateTime[] AnsweredAt = [DateTime.MinValue, DateTime.MinValue];

	/// <summary>A way refused within this long of its last answer is the market's limit on the connection, not the way.</summary>
	private static readonly TimeSpan AnsweringLately = TimeSpan.FromMinutes(15);

	/// <summary>True when <paramref name="way"/> hasn't been answered lately - or at all since the start.</summary>
	private static bool Quiet(int way) => DateTime.UtcNow - AnsweredAt[way] > AnsweringLately;

	/// <summary>
	/// A search page was just refused this way (0 or 1; -1 when not): the next request is one item on its own, which tells
	/// searches refused (it is answered) from the market's limit (it is refused too) - see <see cref="SearchOff"/>.
	/// </summary>
	private static int _searchDoubt = -1;

	/// <summary>
	/// The pause after the market has refused, in seconds: ninety, then five minutes, fifteen, thirty, an hour, two, four -
	/// each a little longer at random, never shorter. Any answer starts it again from the top.
	/// </summary>
	/// <remarks>
	/// It used to start at fifteen minutes and double. But the refusals that kept it climbing were never the market's
	/// limit - they were how the request was written (see <see cref="Browser.Market"/>) - and each pause ended in the same
	/// refusal and a longer pause: one inventory sat at "931 still to price" for a whole day. A short first pause costs one
	/// request if the market really is busy, and gets on with it if it isn't.
	/// </remarks>
	private static readonly int[] Ladder = [90, 5 * 60, 15 * 60, 30 * 60, 60 * 60, 120 * 60, 240 * 60];

	/// <summary>
	/// The pause once the market has refused even after the longest step: asked twice a day from then on.
	/// </summary>
	/// <remarks>
	/// Before there were two ways it went like this for days: every four hours one lookup, that one refused, and it looked
	/// like Steam turning the whole internet connection away. It was the request - asked differently, the same connection
	/// got its answers (2026-10-05). So this is only reached when every step of the ladder has ended in a refusal, the
	/// ways taking turns, and past that a request into a limit still in force may only keep it in force.
	/// </remarks>
	private const int RefusedSeconds = 12 * 60 * 60;

	/// <summary>"The market is refusing" has been said - it isn't said again until the market has answered once.</summary>
	private static bool _saidRefused;

	private static DateTime _lastCall = DateTime.MinValue;
	private static DateTime _coolUntil = DateTime.MinValue;

	/// <summary>The ladder step the pause is on, in seconds - 0 when the market last answered.</summary>
	private static int _coolSeconds;

	/// <summary>
	/// True until the market has answered since the start, or since the last pause: the next request is then one wanted
	/// item on its own - the smallest real request there is, and its answer is a price. Never a request made only to see.
	/// </summary>
	private static bool _checkFirst = true;

	// ── searches refused, single lookups answered ───────────────────────────
	/// <summary>Until when search pages are left alone - see <see cref="SearchOff"/>.</summary>
	private static DateTime _searchOffUntil = DateTime.MinValue;

	/// <summary>How long searches were last left alone: thirty minutes, doubling to four hours while they stay refused.</summary>
	private static int _searchOffMinutes;

	/// <summary>
	/// True while search pages are being refused and single lookups answered: everything wanted is priced one item at a
	/// time, at the same pace, and searches are tried again when this runs out.
	/// </summary>
	/// <remarks>
	/// Told apart by the request after a refused search, which is one item on its own the same way: answered, it is the
	/// searches. Refused too, it is the market's limit on the connection - a pause, and searches stay on. It used to be
	/// "a single lookup answered in the last hour", and the limit hit on a search then switched searches off for half an
	/// hour and sent two more requests into it.
	/// </remarks>
	public static bool SearchOff => DateTime.UtcNow < _searchOffUntil;

	/// <summary>When searches are tried again, or null while they're in use - the level planner, which only searches, says so.</summary>
	public static DateTime? SearchesBackAt => SearchOff ? _searchOffUntil : null;

	/// <summary>The newest price in the book, unix seconds - what "prices from 14:20" says.</summary>
	private static long _newestAt;
	private static bool _coolLoaded;
	private static DateTime _lastSave = DateTime.MinValue;
	private static bool _loaded;
	private static readonly Lock LoadGate = new();

	/// <summary>One save at a time, from the snapshot to the file: the 30-second save and the shutdown one together could
	/// write in the wrong order, the older book landing last and the newest prices lost.</summary>
	private static readonly Lock SaveGate = new();

	private static string Path => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", "prices.json");

	/// <summary>Steam's currency id for everything here. Changing it makes every cached price a different key.</summary>
	private static int Currency => Math.Max(1, Live.Global.MarketCurrency);

	/// <summary>The same, for anything that keeps values over time and has to know which currency they were in.</summary>
	public static int CurrencyId => Currency;

	/// <summary>The symbol to print. Steam uses "$" for several of these, which is exactly what people expect.</summary>
	public static string Symbol => Currency switch {
		2 => "£",
		3 => "€",
		8 or 23 => "¥",
		5 => "₽",
		24 => "₹",
		_ => "$"
	};

	/// <summary>
	/// Market prices are per (game, item name, currency) - the same name in two games is two different things,
	/// and the same item in two currencies is two different numbers. Currency is part of the key so switching it
	/// re-prices from scratch instead of quietly mixing dollars into a euro total.
	/// </summary>
	private static string Key(uint app, string marketHashName) => $"{app}/{Currency}/{marketHashName}";

	/// <summary>A price we already hold, or null. Never touches the network.</summary>
	public static decimal? Known(uint app, string marketHashName) {
		Load();

		lock (Cache) {
			return Cache.TryGetValue(Key(app, marketHashName), out Price? p) ? p.Usd : null;
		}
	}

	/// <summary>True when the cached price is old enough to be worth asking about again.</summary>
	public static bool NeedsRefresh(uint app, string marketHashName) {
		Load();

		string key = Key(app, marketHashName);

		lock (Cache) {
			if (Cache.TryGetValue(key, out Price? p) && (DateTime.UtcNow - p.When <= AgeFor(p.Usd))) {
				return false;
			}
		}

		return !SetAside(key);   // set aside after twice no use: not due until its time is up
	}

	// ── asking the market ───────────────────────────────────────────────────
	// Which price: the LOWEST LISTING - what one would cost to buy right now - everywhere, never the median.
	//
	// It used to be the median, from priceoverview, one request per item: 931 items was 931 requests and two hours. The
	// market's search answers for a whole game's cards, backgrounds and emoticons at once, or every wear of a skin at
	// once, but it only knows the lowest listing - so that is the one number used, whichever way a price was asked.
	// Measured on the same items both ways it is the same figure (an AK-47 | Redline FT: $34.88 from the search and as
	// priceoverview's lowest, $34.42 median; a booster pack $0.13 all three), and a price that meant a different thing
	// depending on how it happened to be fetched would make a total of nothing in particular.

	/// <summary>Market requests made since the start - the checks count them.</summary>
	internal static int Asked;

	/// <summary>
	/// How many results a search page holds, as the last page Steam sent said (its "pagesize"): ten signed out, whatever
	/// count is asked for. Taken from the answer rather than assumed, so the estimates follow if Steam ever changes it.
	/// </summary>
	private static int _pageSize = 10;

	/// <summary>When the market's pause ends, or null when it isn't pausing - the dashboard says so instead of a time left.</summary>
	public static DateTime? PausedUntil => DateTime.UtcNow < _coolUntil ? _coolUntil : null;

	/// <summary>The remembered pause is read under this, and only marked read once it is: marked first, a second account
	/// pricing at the same moment went on with no pause at all and asked straight into the one a restart had kept.</summary>
	private static readonly Lock CoolGate = new();

	/// <summary>Write the market's pause down, with this version - so it binds a restart, but never a newer version.</summary>
	private static void SaveCool() => Limiters.Remember("market", _coolUntil, MinutesOf(_coolSeconds), Build.Version);

	private static void LoadCool() {
		if (Volatile.Read(ref _coolLoaded)) {
			return;
		}

		lock (CoolGate) {
			if (!_coolLoaded) {
				(DateTime until, int minutes, string? by) = Limiters.RememberedBy("market");   // a restart doesn't lift the market's limit

				// ...but a new version does. A pause is only as good as the requests that earned it: up to 1.6.9 every request
				// went without an Accept header and was refused for that alone, and 1.7.0 then sat out the twelve-hour pause
				// those refusals had built up before asking once. So a pause written by any other version - or by one too old
				// to write its version down - is dropped: the first start of a new version asks straight away, and if it is
				// refused the ladder starts again from the bottom. The way that last answered is still kept.
				if (by != Build.Version) {
					if ((minutes > 0) || (until > DateTime.UtcNow)) {
						Log.Debug($"market: a pause written by {by ?? "an older version"} is dropped - this version {Build.Version} asks straight away");
						Limiters.Remember("market", DateTime.MinValue, 0, Build.Version);
					}

					until = DateTime.MinValue;
					minutes = 0;
				}

				_coolUntil = until;
				_coolSeconds = minutes * 60;
				_saidRefused = _coolSeconds >= RefusedSeconds;                 // and it was said before the restart
				_goodWay = _way = Limiters.Remembered("market-way").Minutes == 1 ? 1 : 0;   // nor forget which way answered
				Volatile.Write(ref _coolLoaded, true);
			}
		}
	}

	/// <summary>
	/// Set while the market is refusing, and pausing, and the book has prices to fall back on: when its newest
	/// price came in, and when the market is asked again. The dashboard's tile and "inventory" say so with it; null otherwise.
	/// </summary>
	public static (DateTime From, DateTime RetryAt)? Stale {
		get {
			LoadCool();
			Load();
			DateTime until = _coolUntil;
			long newest = Interlocked.Read(ref _newestAt);

			return (_coolSeconds > 0) && (DateTime.UtcNow < until) && (newest > 0) ? (DateTimeOffset.FromUnixTimeSeconds(newest).UtcDateTime, until) : null;
		}
	}

	/// <summary>
	/// Ask the market what one item sells for, and remember the answer. Returns null if it wouldn't say - which
	/// includes items that simply have no market listing, so those are remembered as zero rather than asked
	/// about for ever.
	/// </summary>
	public static async Task<decimal?> FetchAsync(uint app, string marketHashName, CancellationToken ct) {
		LoadCool();

		if (DateTime.UtcNow < _coolUntil) {
			return null;   // the market told us to slow down; everyone waits it out together
		}

		await Gate.WaitAsync(ct).ConfigureAwait(false);

		try {
			// Refused by a way that hasn't answered lately: once more the other way, behind the same gap - never a third time.
			(decimal? price, bool again, _, _) = await FetchLockedAsync(app, marketHashName, ct).ConfigureAwait(false);

			return (price == null) && again ? (await FetchLockedAsync(app, marketHashName, ct).ConfigureAwait(false)).Price : price;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug(new Said("market lookup for {0} failed: {1}", marketHashName, Log.Describe(e)));

			return null;
		} finally {
			Gate.Release();
		}
	}

	/// <summary>
	/// Price everything in <paramref name="wanted"/> the cheapest way there is, at most <paramref name="maxAsks"/> requests.
	/// The list is in the order it should be priced; anything the book holds fresh by the time its turn comes - priced a
	/// moment ago for another account, or by a search for something else - is never asked about. False once the market
	/// stops answering.
	/// </summary>
	public static async Task<bool> PriceAsync(IReadOnlyList<(uint App, string Hash)> wanted, int maxAsks, CancellationToken ct) {
		Load();
		LoadCool();

		for (int asked = 0; asked < maxAsks; asked++) {
			if (DateTime.UtcNow < _coolUntil) {
				return false;
			}

			await Gate.WaitAsync(ct).ConfigureAwait(false);
			string what = "";

			try {
				// Planned with the gate held, from the book as it is NOW: two accounts holding the same cards queue here,
				// and the second finds them already priced instead of asking again.
				List<(uint App, string Hash)> left = [.. wanted.Where(static w => NeedsRefresh(w.App, w.Hash))];

				if (left.Count == 0) {
					return true;
				}

				// Something else first, after a 500: whether the market answers it is what says whose trouble that was.
				List<(uint App, string Hash)> next = SkipSuspects(left);

				// The first request since the start or a pause is one wanted item on its own (_checkFirst); after that, the
				// cheapest way to what's left.
				Ask ask = _checkFirst ? new Ask(next[0].App, next[0].Hash) : NextAsk(next);
				what = ask.Hash;
				DateTime coolBefore = _coolUntil;
				bool answered, again, fault, trouble;

				if (ask.Search == null) {
					(decimal? price, again, fault, trouble) = await FetchLockedAsync(ask.App, ask.Hash, ct).ConfigureAwait(false);
					answered = price != null;
				} else {
					(answered, again, fault, trouble) = await SearchLockedAsync(ask, ct).ConfigureAwait(false);
				}

				// Answered, but with nothing usable for THIS request: no price or no results list in it. The second time in
				// a row for the same item (or search) it is set aside, and the rest of the queue goes on in this same sweep.
				// The first time the sweep stops, as it always did: it may only have been a moment's trouble.
				if (fault) {
					if (!Faulted(ask)) {
						return false;
					}

					continue;
				}

				// A 500 or an empty answer: Steam's own trouble, and the short pause it started stands - unless this item is
				// the only thing failing while the market answers everything else, when it is set aside instead and the rest
				// of the queue goes on now, without the pause: the item's trouble, not the market's.
				if (trouble) {
					if (!Unwell(ask)) {
						return false;
					}

					if (_coolUntil > coolBefore) {
						_coolUntil = coolBefore;
						SaveCool();
					}

					continue;
				}

				// Refused by a way that hasn't answered lately with the other still to try, or a search refused with the
				// single lookup that tells why still to ask: the next turn goes on - through the same gap, counted against the
				// same cap. Otherwise the market has stopped answering: stop pushing and try again next sweep.
				if (!answered && !again) {
					return false;
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("market lookup for {0} failed: {1}", what, Log.Describe(e)));

				return false;
			} finally {
				Gate.Release();
			}
		}

		return true;
	}

	/// <summary>
	/// One item's price from priceoverview, whether to ask again at once if there's none (see <see cref="AskLockedAsync"/>),
	/// whether the answer was no use for this item (see <see cref="Faulted"/>), and whether it was Steam's own trouble -
	/// a 500, an empty answer (see <see cref="Unwell"/>). Gate held.
	/// </summary>
	private static async Task<(decimal? Price, bool AskAgain, bool Fault, bool Trouble)> FetchLockedAsync(uint app, string marketHashName, CancellationToken ct) {
		(string? json, bool again, bool trouble) = await AskLockedAsync($"/market/priceoverview/?appid={app}&currency={Currency}&market_hash_name={Uri.EscapeDataString(marketHashName)}", Key(app, marketHashName), ct).ConfigureAwait(false);

		if (json == null) {
			return (null, again, false, trouble);
		}

		JsonDocument doc;

		try {
			doc = JsonDocument.Parse(json);
		} catch (JsonException) {
			Log.Debug($"market lookup for {marketHashName}: no price in the answer");

			return (null, false, true, false);
		}

		using (doc) {
			// "null", a list, a bare number: answered, but nothing about this item - not even that it's worth nothing.
			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				Log.Debug($"market lookup for {marketHashName}: no price in the answer");

				return (null, false, true, false);
			}

			if (!doc.RootElement.TryGetProperty("success", out JsonElement ok) || (ok.ValueKind != JsonValueKind.True)) {
				Remember(app, marketHashName, 0);   // no market listing at all - genuinely worth nothing

				return (0, false, false, false);
			}

			// The lowest listing, the same number a search gives (see above); the median only when nothing is listed. Neither -
			// nobody selling, nothing sold - is worth nothing, kept like any price and not asked about again until it's due.
			decimal price = Money(Text(doc.RootElement, "lowest_price")) ?? Money(Text(doc.RootElement, "median_price")) ?? 0;
			Remember(app, marketHashName, price);

			return (price, false, false, false);
		}
	}

	/// <summary>One page of a market search, every price on it remembered - not only the ones asked for. Gate held.</summary>
	private static async Task<(bool Answered, bool AskAgain, bool Fault, bool Trouble)> SearchLockedAsync(Ask ask, CancellationToken ct) {
		// count=100 is only the most it may send: signed out, Steam sends ten whatever is asked, and the page says so.
		(string? json, bool again, bool trouble) = await AskLockedAsync($"{ask.Search}&start={ask.Start}&count=100&currency={Currency}&l=english", SearchKey(ask.PageKey), ct).ConfigureAwait(false);

		if (json == null) {
			return (false, again, false, trouble);
		}

		if (ParseSearch(json) is not { } page) {
			Log.Debug($"market search for {ask.Hash}: no results list in the answer");

			return (false, false, true, false);
		}

		if (page.PageSize > 0) {
			_pageSize = page.PageSize;
		}

		foreach ((string hash, int cents) in page.Prices) {
			Remember(ask.SearchApp, hash, cents / 100m);
		}

		lock (Pages) {
			// Nothing came back: there is nothing further on, whatever the total says.
			Pages[ask.PageKey] = (page.Count == 0 ? int.MaxValue : ask.Start + page.Count, page.Total, DateTime.UtcNow);
		}

		Cleared(SearchKey(ask.PageKey));

		return (true, false, false, false);
	}

	/// <summary>
	/// One market request for something other than an inventory value - the level planner's and the seller's card set
	/// prices - in the SAME queue: behind the same gate, the same gap after whatever was asked last, and nothing at all
	/// while the market's pause stands. Two separate paces each keeping to the limit add up to twice it. Signed out, like
	/// every price: a read needs no account. Json is null when it wasn't asked or wasn't answered; Asked says whether a
	/// request was actually sent - nothing is, while the market's pause stands or searches are being left alone.
	/// </summary>
	public static async Task<(string? Json, bool Asked)> MarketGetAsync(string pathAndQuery, CancellationToken ct) {
		LoadCool();

		if (DateTime.UtcNow < _coolUntil) {
			return (null, false);
		}

		await Gate.WaitAsync(ct).ConfigureAwait(false);
		int sentBefore = Volatile.Read(ref Asked);   // every request is sent with the gate held: a change since is this one's

		try {
			// Refused by a way that hasn't answered lately: once more the other way, behind the same gap. Not while searches
			// are being left alone, nor while a refused search waits on a single lookup to tell why.
			(string? json, bool again, _) = await AskLockedAsync(pathAndQuery, null, ct).ConfigureAwait(false);

			if ((json == null) && again) {
				json = (await AskLockedAsync(pathAndQuery, null, ct).ConfigureAwait(false)).Json;
			}

			return (json, Volatile.Read(ref Asked) != sentBefore);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug($"market search for a card set failed: {Log.Describe(e)}");

			return (null, Volatile.Read(ref Asked) != sentBefore);
		} finally {
			Gate.Release();
		}
	}

	/// <summary>
	/// Every price on a search page someone else asked for, kept in the book - when it is in the book's currency. The
	/// level planner's card set pages are exactly the pages pricing would ask for a game's cards, so they are never
	/// asked twice. Not the other way round: a set price needs to know the WHOLE set, which only the planner's own
	/// search (and its count) says.
	/// </summary>
	public static void Learn(uint app, string json, int currency) {
		if ((currency != Currency) || (ParseSearch(json) is not { } page)) {
			return;
		}

		Load();

		foreach ((string hash, int cents) in page.Prices) {
			Remember(app, hash, cents / 100m);
		}
	}

	/// <summary>
	/// One market request with the gate held, signed out: waits out the gap, honours a pause, and goes the way that last
	/// got an answer. Json is null when it wasn't asked or wasn't answered. AskAgain says a refusal still leaves something
	/// to try straight away - the other way, or a single lookup after a refused search - so the caller may go on (through
	/// the gap, like every request); false means stop. Trouble says the market answered with a 500 or nothing at all:
	/// Steam's own trouble, a short pause, and never by itself a fault of what was asked about (<see cref="Unwell"/>).
	/// Nor is a refusal: that is the market's limit, and the pause's business, not the item's. <paramref name="key"/> is
	/// the price (or search) key asked about, if any.
	/// </summary>
	private static async Task<(string? Json, bool AskAgain, bool Trouble)> AskLockedAsync(string pathAndQuery, string? key, CancellationToken ct) {
		// Asked again once it's our turn. Several accounts pricing at once queue here, and when the one ahead is told
		// 429, the rest used to go on and ask anyway - each refused in turn, each lengthening the pause.
		if (DateTime.UtcNow < _coolUntil) {
			return (null, false, false);
		}

		bool search = pathAndQuery.StartsWith("/market/search/", StringComparison.Ordinal);

		// The level planner's search, while searches are being refused - or while a refused one waits on the single
		// lookup that says whether it was the searches or the market's limit: another search would only be refused too.
		if (search && (SearchOff || (_searchDoubt >= 0))) {
			return (null, false, false);
		}

		// steamcommunity.com as a whole is serving a 429 wait (an account's own request tripped it): the market is the
		// same limit, so it waits too - asking into it only makes it last longer.
		if (Limiters.RateLimitedFor(WebSession.Community.Host) is { } shut && (shut > TimeSpan.Zero)) {
			_coolUntil = DateTime.UtcNow + shut;
			SaveCool();

			return (null, false, false);
		}

		TimeSpan gap = NextGap();
		TimeSpan since = DateTime.UtcNow - _lastCall;

		if (since < gap) {
			await Task.Delay(gap - since, ct).ConfigureAwait(false);

			// Asked again after the gap: an account's own 429 in those seconds shuts the host, and asking into it only
			// makes it last longer - nor is a request never sent a refusal that should lengthen the market's next pause.
			if (Limiters.RateLimitedFor(WebSession.Community.Host) is { } closed && (closed > TimeSpan.Zero)) {
				_coolUntil = DateTime.UtcNow + closed;
				SaveCool();

				return (null, false, false);
			}
		}

		_lastCall = DateTime.UtcNow;
		Interlocked.Increment(ref Asked);

		int way = _way;
		using HttpResponseMessage response = await (way == 0 ? Http : HttpB).GetAsync("https://steamcommunity.com" + pathAndQuery, ct).ConfigureAwait(false);
		string? body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;

		// Turned down: 429, 403, or a web page where the prices should be - what a firewall sends instead of an answer.
		if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden || ((body != null) && body.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))) {
			return (null, Refused(way, search), false);
		}

		// Not a refusal - Steam's own trouble, a 500 or a 502, or an answer with nothing in it: a couple of minutes, and no
		// step up the ladder. During Steam's maintenance every request answers like this, whatever it asks about: counted
		// against each item asked, eleven of thirteen were set aside for six hours one by one, the most valuable first.
		if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(body)) {
			_coolUntil = DateTime.UtcNow.AddMinutes(2);
			_searchDoubt = -1;   // nothing told by this one: searches stay on
			SaveCool();
			string until = (_coolUntil.ToLocalTime()).ToString("HH:mm");
			Log.Debug(response.IsSuccessStatusCode
				? new Said("the market's answer was empty - pausing price lookups until {0}", until)
				: new Said("the market answered {0} - pausing price lookups until {1}", (int) response.StatusCode, until));

			return (null, false, true);
		}

		Answered(way, search, key);

		return (body, false, false);
	}

	/// <summary>
	/// A request was turned down. From a way that hasn't answered lately, the next request goes the other way at once -
	/// the way may be what's refused. From one that was answering until a moment ago it is the market's limit on the
	/// connection, the same whichever way is asked: the pause steps up the <see cref="Ladder"/> straight away. So does a
	/// second refusal in a row, and the one request at the end of a pause; the next request then goes the other way, so
	/// the ways take turns across the steps. A refused search is followed by one item on its own instead (see
	/// <see cref="SearchOff"/>). True when the caller may ask again straight away. Gate held.
	/// </summary>
	private static bool Refused(int way, bool search) {
		bool doubted = _searchDoubt >= 0;
		_searchDoubt = -1;
		_refusedInRow++;

		// Not the request at the end of a pause (that one is only ever one), and nothing refused just before.
		if ((_coolSeconds == 0) && (_refusedInRow < 2)) {
			if (search) {
				// Searches refused, or everything? The next request is one item on its own: the same way when that way was
				// answering, the other way when it wasn't (the way may be what's refused).
				_searchDoubt = way;

				if (Quiet(way)) {
					_way = 1 - way;
				}

				Log.Debug(new Said("the market turned a search down - the next request asks for one item, to see whether it's only searches"));

				return true;
			}

			if (Quiet(way)) {
				_way = 1 - way;
				Log.Debug(new Said("the market turned a price lookup down - the next one asks the other way"));

				return true;
			}
		}

		// A pause. The request at its end goes the other way from this one - every step tries the way the last didn't.
		bool bothWays = (_refusedInRow >= 2) && !doubted;
		_refusedInRow = 0;
		_way = 1 - way;
		_coolSeconds = NextCoolSeconds(_coolSeconds);
		_coolUntil = DateTime.UtcNow.AddSeconds(_coolSeconds * (1 + (Rng.Next(0, 251) / 1000d)));
		_checkFirst = true;
		SaveCool();

		// Past the end of the ladder: once, in plain words, rather than the same pause line twice a day for days.
		bool refused = _coolSeconds >= RefusedSeconds;

		if (refused && !_saidRefused) {
			_saidRefused = true;
			Log.Info(new Said("Steam's market is refusing price lookups from this internet connection - inventory values keep the last prices it gave, and it is asked again twice a day"));
		} else if (!refused) {
			string until = (_coolUntil.ToLocalTime()).ToString("HH:mm");
			Log.Debug(bothWays
				? new Said("the market turned price lookups down both ways - pausing them until {0}", until)
				: new Said("the market turned a price lookup down - pausing them until {0}", until));
		}

		return false;
	}

	/// <summary>
	/// The market answered: the way it answered is the one used from now on (and remembered), the ladder starts again from
	/// its first step, and a search answered means searches are fine again. A single lookup answered the same way a search
	/// was just refused means it is the searches being refused: they're left alone for a while, and pricing goes on one
	/// item at a time. And whatever had a 500 meanwhile had it while the market answered - its own trouble after all
	/// (<see cref="Unwell"/>). <paramref name="key"/> is what was answered. Gate held.
	/// </summary>
	private static void Answered(int way, bool search, string? key) {
		_refusedInRow = 0;
		_checkFirst = false;
		AnsweredAt[way] = DateTime.UtcNow;

		if (way != _goodWay) {
			_goodWay = way;
			Limiters.Remember("market-way", DateTime.UtcNow, way);
		}

		if (search) {
			_searchOffMinutes = 0;
		} else if (_searchDoubt == way) {
			_searchOffMinutes = _searchOffMinutes <= 0 ? 30 : Math.Min(240, _searchOffMinutes * 2);
			_searchOffUntil = DateTime.UtcNow.AddMinutes(_searchOffMinutes);
			Log.Debug(new Said("the market turns its searches down but answers single lookups - pricing one item at a time until {0}", (_searchOffUntil.ToLocalTime()).ToString("HH:mm")));
		}

		_searchDoubt = -1;

		if (_coolSeconds != 0) {
			_coolSeconds = 0;
			Limiters.Remember("market", _coolUntil, 0, Build.Version);
		}

		if (_saidRefused) {
			_saidRefused = false;
			Log.Info(new Said("Steam's market answers price lookups again"));
		}

		Recovered(key);
	}

	/// <summary>
	/// A search page: how many listings match in all, how many it shows per page, how many came back, and the lowest
	/// listing of each one that is for sale (in cents). Null when the answer isn't a search result at all.
	/// </summary>
	public static (int Total, int PageSize, int Count, List<(string Hash, int Cents)> Prices)? ParseSearch(string json) {
		try {
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty("results", out JsonElement results) || (results.ValueKind != JsonValueKind.Array)
				|| (root.TryGetProperty("success", out JsonElement ok) && (ok.ValueKind == JsonValueKind.False))) {
				return null;
			}

			int Int(JsonElement node, string name) => node.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;

			List<(string Hash, int Cents)> prices = [];
			int count = 0;

			foreach (JsonElement r in results.EnumerateArray()) {
				count++;
				string? hash = Text(r, "hash_name");

				// Nothing listed means no lowest listing - left for priceoverview, which knows the difference
				// between "not listed right now" and "not on the market at all".
				if (!string.IsNullOrEmpty(hash) && (Int(r, "sell_listings") > 0) && (Int(r, "sell_price") > 0)) {
					prices.Add((hash, Int(r, "sell_price")));
				}
			}

			return (Int(root, "total_count"), Int(root, "pagesize"), count, prices);
		} catch (JsonException) {
			return null;
		}
	}

	// ── set aside: what the market answers, but never usefully ──────────────
	// One item - or one search - the market answers with a 500 every time, or with "null", or with no results list: every
	// sweep stopped on it, and the next started on it again, so nothing behind it in the queue was ever priced. Twice in a
	// row with nothing usable and it is set aside - six hours, then a day each time after - and the rest go on. Only
	// answers about the item count: a refusal (429, 403, a block page) is the market's limit, which the pause deals with,
	// and a 500 only counts once the market has answered something else meanwhile (see Unwell).

	/// <summary>Per price key (or <see cref="SearchKey"/>): no-use answers in a row since the last price, until when it is
	/// set aside (unix seconds, 0 if it isn't), and how many times it has been. Under its own lock, taken after Cache's.</summary>
	private static readonly Dictionary<string, (int Fails, long Until, int Times)> Troubled = new(StringComparer.Ordinal);

	/// <summary>How a search is told apart from an item in <see cref="Troubled"/>; an item's key starts with its app number.</summary>
	private static string SearchKey(string pageKey) => "search|" + pageKey;

	/// <summary>Kept in the price book's own file under this, so a restart doesn't ask straight back into them.</summary>
	private const string AsidePrefix = "aside/";

	/// <summary>True while <paramref name="key"/> is set aside.</summary>
	private static bool SetAside(string key) {
		lock (Troubled) {
			return (Troubled.Count > 0) && Troubled.TryGetValue(key, out (int Fails, long Until, int Times) t) && (t.Until > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		}
	}

	/// <summary>
	/// What <paramref name="ask"/> asked about got an answer with nothing usable in it, or a 500 while the market answered
	/// other things (<see cref="Unwell"/>). True when that set it aside: the
	/// second time in a row, or the first after it has been set aside before. Gate held.
	/// </summary>
	private static bool Faulted(Ask ask) {
		bool search = ask.Search != null;
		string key = search ? SearchKey(ask.PageKey) : Key(ask.App, ask.Hash);
		DateTime until;

		lock (Troubled) {
			(int fails, long _, int times) = Troubled.GetValueOrDefault(key);

			if (++fails < (times > 0 ? 1 : 2)) {
				Troubled[key] = (fails, 0, times);

				return false;
			}

			times++;
			until = DateTime.UtcNow.Add(times == 1 ? TimeSpan.FromHours(6) : TimeSpan.FromHours(24));
			Troubled[key] = (0, new DateTimeOffset(until).ToUnixTimeSeconds(), times);
		}

		if (search) {
			Log.Debug(new Said("a market search for {0} had no usable answer twice in a row - pricing those items one at a time until {1}", ask.Hash, (until.ToLocalTime()).ToString("HH:mm")));
		} else {
			Log.Debug(new Said("the market had no usable answer for {0} twice in a row - set aside until {1}", ask.Hash, (until.ToLocalTime()).ToString("HH:mm")));

			// Off every account's "still to price" now, at the price it is already counted at - not waiting on it for hours.
			Tell(ask.App, ask.Hash, Known(ask.App, ask.Hash) ?? 0, Currency);
		}

		Save();   // rare, and a restart shouldn't walk straight back into it

		return true;
	}

	/// <summary>An answer for <paramref name="key"/>: whatever trouble it had is forgotten.</summary>
	private static void Cleared(string key) {
		lock (Troubled) {
			if (Troubled.Count > 0) {
				Troubled.Remove(key);
			}
		}
	}

	// ── a 500: the market's trouble, or the item's ──────────────────────────
	// During Steam's maintenance every request answers 500 or 503, whatever it asks about. Counted against the item asked,
	// each sweep set one aside and went on to the next - eleven of thirteen sat out six hours, the knife first, counted at
	// nothing, and the inventory's history saved the total without them: a crash, then a recovery. So a 500 is the
	// market's trouble until the market answers something else. What had one is a suspect, the next request is for
	// something else, and only if that one is answered does the suspect's 500 count against it: it failed while the market
	// answered. More different things failing in a row than one inventory's bad items ever are, and it is the market.

	/// <summary>What had a 500 or an empty answer since the market last answered, by price (or search) key. Gate held.</summary>
	private static readonly Dictionary<string, Ask> Suspects = new(StringComparer.Ordinal);

	/// <summary>More different things failing in a row than this is the market failing, not any of them.</summary>
	private const int SuspectsAtMost = 3;

	/// <summary>Set once more than <see cref="SuspectsAtMost"/> different things failed in a row: nothing is a suspect, or counted
	/// against, until the market answers again. Gate held.</summary>
	private static bool _marketDown;

	private static string KeyOf(Ask ask) => ask.Search != null ? SearchKey(ask.PageKey) : Key(ask.App, ask.Hash);

	/// <summary>
	/// What <paramref name="ask"/> asked about had a 500 or an empty answer. Counted against it at once only when it is the
	/// one thing failing while the market answered within the hour, and failed before too - a second 500 in a row with
	/// nothing else to ask in between, or after one already counted, or once back from being set aside. Otherwise it is a
	/// suspect (see <see cref="Recovered"/>).
	/// True when that set it aside. Gate held.
	/// </summary>
	private static bool Unwell(Ask ask) {
		string key = KeyOf(ask);
		bool failedBefore;

		lock (Troubled) {
			failedBefore = Troubled.TryGetValue(key, out (int Fails, long Until, int Times) t) && ((t.Fails > 0) || (t.Times > 0));
		}

		bool alone = !_marketDown && Suspects.Keys.All(k => k == key) && (failedBefore || Suspects.ContainsKey(key))
			&& (DateTime.UtcNow - (AnsweredAt[0] > AnsweredAt[1] ? AnsweredAt[0] : AnsweredAt[1]) < TimeSpan.FromHours(1));

		if (alone) {
			Suspects.Remove(key);

			return Faulted(ask);
		}

		if (_marketDown || Suspects.ContainsKey(key)) {
			return false;
		}

		if (Suspects.Count >= SuspectsAtMost) {
			Suspects.Clear();
			_marketDown = true;
			Log.Debug(new Said("the market is failing whatever it is asked - nothing counts against any item until it answers again"));

			return false;
		}

		Suspects[key] = ask;

		return false;
	}

	/// <summary>
	/// The market answered (<paramref name="key"/>, if it was about something): whatever had a 500 since failed while the
	/// market answered, and it counts against each of them now. Gate held.
	/// </summary>
	private static void Recovered(string? key) {
		_marketDown = false;

		if (Suspects.Count == 0) {
			return;
		}

		List<Ask> failed = [.. Suspects.Where(s => s.Key != key).Select(static s => s.Value)];
		Suspects.Clear();

		foreach (Ask ask in failed) {
			Faulted(ask);
		}
	}

	/// <summary>What's left, less anything that had a 500 since the market last answered - unless that's everything. Gate held.</summary>
	private static List<(uint App, string Hash)> SkipSuspects(List<(uint App, string Hash)> left) {
		if (Suspects.Count == 0) {
			return left;
		}

		List<(uint App, string Hash)> rest = [.. left.Where(static l => !Suspects.ContainsKey(Key(l.App, l.Hash)))];

		return rest.Count > 0 ? rest : left;
	}

	// ── planning: the fewest requests for what's wanted ─────────────────────
	/// <summary>
	/// The next request. A single priceoverview, or one page of a search that covers a group of wanted items: one
	/// game's Steam items (its cards, foils, backgrounds, emoticons, booster), or every wear and StatTrak of one skin.
	/// </summary>
	public sealed record Ask(uint App, string Hash, string? Search = null, uint SearchApp = 0, int Start = 0) {
		public string PageKey => PageKeyOf(SearchApp, Search ?? "");
	}

	/// <summary>
	/// What each search has shown so far: the next start, the total, and when. A page of a search already read isn't
	/// read again, and a search read to the end hands what it didn't find to priceoverview.
	/// </summary>
	private static readonly Dictionary<string, (int Next, int Total, DateTime At)> Pages = new(StringComparer.Ordinal);

	private static string PageKeyOf(uint app, string search) => $"{Currency}|{app}|{search}";

	/// <summary>How long a search's progress stands. Within one pricing run; the next run, hours on, starts again.</summary>
	private static readonly TimeSpan PagesLast = TimeSpan.FromMinutes(30);

	/// <summary>Steam's own inventory: trading cards, backgrounds, emoticons, boosters - named "&lt;game&gt;-&lt;name&gt;".</summary>
	private const uint SteamItems = 753;

	private static readonly string[] Wears = [" (Factory New)", " (Minimal Wear)", " (Field-Tested)", " (Well-Worn)", " (Battle-Scarred)"];

	/// <summary>The search a wanted item belongs to - the same for every item one page of it can answer - or null.</summary>
	public static (string Key, bool Narrow)? GroupOf(uint app, string hash) {
		if (app == SteamItems) {
			int dash = hash.IndexOf('-');

			return (dash > 0) && uint.TryParse(hash.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint game) && (game != SteamItems)
				? ($"753:{game}", true)
				: null;
		}

		// A skin: every wear, StatTrak and Souvenir of it come back from one search for its plain name.
		string name = hash;

		foreach (string prefix in (string[]) ["★ ", "StatTrak™ ", "Souvenir "]) {
			if (name.StartsWith(prefix, StringComparison.Ordinal)) {
				name = name[prefix.Length..];
			}
		}

		string? wear = Wears.FirstOrDefault(w => name.EndsWith(w, StringComparison.Ordinal));

		// Only a skin that names a wear: anything else is one of a kind, and a search for it would find more than it.
		return wear == null ? null : ($"{app}:{name[..^wear.Length]}", true);
	}

	/// <summary>The kind of Steam item a name is, as the market's item_class tag - or 0 when it can't be told.</summary>
	private static int KindOf(string hash) {
		string name = hash[(hash.IndexOf('-') + 1)..];

		return name.EndsWith(" (Trading Card)", StringComparison.Ordinal) ? 20
			: name.EndsWith(" (Foil Trading Card)", StringComparison.Ordinal) ? 21
			: name.EndsWith(" (Profile Background)", StringComparison.Ordinal) ? 3
			: (name.Length > 2) && (name[0] == ':') && (name[^1] == ':') ? 4
			: name.EndsWith(" Booster Pack", StringComparison.Ordinal) ? 5
			: 0;
	}

	/// <summary>
	/// The search for a group, narrowed to the kinds of item wanted from it: a game's normal cards alone are one page
	/// where the whole game - foils, backgrounds and emoticons too - is four.
	/// </summary>
	private static (string Search, uint App) SearchFor(string key, List<(uint App, string Hash)> members) {
		if (key.StartsWith("753:", StringComparison.Ordinal)) {
			string search = $"/market/search/render/?norender=1&appid=753&category_753_Game[]=tag_app_{key[4..]}";
			List<int> kinds = [.. members.Select(static m => KindOf(m.Hash)).Distinct().Order()];

			if (!kinds.Contains(0)) {
				foreach (int cls in kinds.Select(static k => k >= 20 ? 2 : k).Distinct().Order()) {
					search += $"&category_753_item_class[]=tag_item_class_{cls}";
				}

				// The border only narrows cards - anything else has none, and would be filtered out with it.
				if (kinds.All(static k => k >= 20) && (kinds.Count == 1)) {
					search += $"&category_753_cardborder[]=tag_cardborder_{kinds[0] - 20}";
				}
			}

			return (search, SteamItems);
		}

		int colon = key.IndexOf(':');
		uint app = uint.Parse(key.AsSpan(0, colon), CultureInfo.InvariantCulture);

		// The search reads "|" as something else and finds nothing: "AK-47 | Redline" -> 0 results, "AK-47 Redline" -> 10.
		string query = string.Join(' ', key[(colon + 1)..].Replace('|', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

		return ($"/market/search/render/?norender=1&appid={app}&search_descriptions=0&query={Uri.EscapeDataString(query)}", app);
	}

	/// <summary>The cheapest next request for what's left, which is in priority order.</summary>
	public static Ask NextAsk(List<(uint App, string Hash)> left) {
		(uint app, string hash) = left[0];
		Ask single = new(app, hash);

		// Searches being refused while single lookups are answered: one at a time until they're tried again. And right after
		// a refused search, one item on its own - its answer says which it was.
		if (SearchOff || (_searchDoubt >= 0) || (GroupOf(app, hash) is not { } group)) {
			return single;
		}

		List<(uint App, string Hash)> members = [.. left.Where(l => GroupOf(l.App, l.Hash)?.Key == group.Key)];

		if (members.Count < 2) {
			return single;   // one item: priceoverview is exactly one request; a search page might not even hold it
		}

		(string search, uint searchApp) = SearchFor(group.Key, members);
		Ask page = new(app, hash, search, searchApp);

		if (SetAside(SearchKey(page.PageKey))) {
			return single;   // a search with no usable answer twice: its items one at a time until it is tried again
		}

		if ((Suspects.Count > 0) && Suspects.ContainsKey(SearchKey(page.PageKey))) {
			return single;   // a 500 for this search just now: something else next, to see whose trouble that was
		}

		lock (Pages) {
			if (!Pages.TryGetValue(page.PageKey, out (int Next, int Total, DateTime At) seen)) {
				return page;   // never searched: the first page says how big it is
			}

			// Read on only while the pages left are fewer than the items left - otherwise one each is cheaper. A search
			// read hours ago starts again from the top, but its size is known: a game with forty listings and two of
			// them wanted is two single asks, not a page that may well hold neither.
			bool current = DateTime.UtcNow - seen.At <= PagesLast;
			int pagesLeft = PagesLeft(current ? seen.Next : 0, seen.Total);

			return pagesLeft < members.Count ? page with { Start = current ? seen.Next : 0 } : single;
		}
	}

	/// <summary>Pages still to read of a search from <paramref name="next"/> on - none (int.MaxValue) once it has been read to
	/// the end, and one when a search read long ago showed nothing at all: it is asked again.</summary>
	private static int PagesLeft(int next, int total) =>
		next >= total ? (next == 0 ? 1 : int.MaxValue) : (int) Math.Ceiling((total - next) / (double) Math.Max(1, _pageSize));

	/// <summary>
	/// About how many requests pricing these will take - what the dashboard's "about 14 requests" and its time left are
	/// worked from. A guess where a search hasn't been read yet: half a page per wanted item's worth of listings.
	/// </summary>
	public static int RequestsFor(IEnumerable<(uint App, string Hash)> waiting) {
		int requests = 0;
		int size = Math.Max(1, _pageSize);
		bool singles = SearchOff;   // one request each while searches are being left alone
		Dictionary<string, List<(uint App, string Hash)>> groups = new(StringComparer.Ordinal);

		foreach ((uint app, string hash) in waiting) {
			if (!singles && (GroupOf(app, hash) is { } g)) {
				if (!groups.TryGetValue(g.Key, out List<(uint App, string Hash)>? list)) {
					groups[g.Key] = list = [];
				}

				list.Add((app, hash));
			} else {
				requests++;
			}
		}

		foreach ((string key, List<(uint App, string Hash)> members) in groups) {
			int n = members.Count;

			if (n < 2) {
				requests += n;

				continue;
			}

			(string search, uint searchApp) = SearchFor(key, members);

			if (SetAside(SearchKey(PageKeyOf(searchApp, search)))) {
				requests += n;   // set aside: one each, as NextAsk will ask them

				continue;
			}

			// Never searched: a guess - about three listings for every one wanted (a game's whole card set for the half
			// of it that dropped, every wear of a skin for the one or two held).
			int pages = (int) Math.Ceiling(n * 3.0 / size);

			lock (Pages) {
				if (Pages.TryGetValue(PageKeyOf(searchApp, search), out (int Next, int Total, DateTime At) seen)) {
					pages = PagesLeft(DateTime.UtcNow - seen.At <= PagesLast ? seen.Next : 0, seen.Total);
				}
			}

			requests += Math.Min(n, pages);
		}

		return requests;
	}

	/// <summary>
	/// The next step of the pause after both ways were refused, in seconds: the first step of the <see cref="Ladder"/> past
	/// the one it is on, and past its end <see cref="RefusedSeconds"/>. A pause remembered in whole minutes across a
	/// restart (ninety seconds kept as two minutes) lands on the step after it all the same.
	/// </summary>
	internal static int NextCoolSeconds(int current) {
		foreach (int step in Ladder) {
			if (step > current) {
				return step;
			}
		}

		return RefusedSeconds;
	}

	/// <summary>A pause step as the whole minutes it is remembered in across a restart.</summary>
	private static int MinutesOf(int seconds) => (seconds + 59) / 60;

	private static string? Text(JsonElement node, string name) =>
		node.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.String) ? v.GetString() : null;

	/// <summary>"$1,234.56" / "1.234,56 EUR" -> 1234.56. The symbol is noise; only the digits matter.</summary>
	private static decimal? Money(string? text) {
		if (string.IsNullOrWhiteSpace(text)) {
			return null;
		}

		// Trimmed of separators at either end: "12 345,67 руб." and "1,--€" leave one behind that is no part of the number.
		string digits = MoneyChars().Replace(text, "").Trim(',', '.');

		// The LAST separator is the decimal point - the only rule that works for both "1,234.56" and "1.234,56" -
		// but only when one or two digits follow it. Currencies with no cents ("¥ 1,234", "1.234 ₫") have a
		// thousands separator last, and reading that as a decimal point priced a 1,234 yen card at 1.234.
		int last = Math.Max(digits.LastIndexOf(','), digits.LastIndexOf('.'));
		bool hasDecimals = (last >= 0) && (digits.Length - last - 1) is 1 or 2;
		string whole = (hasDecimals ? digits[..last] : digits).Replace(",", "").Replace(".", "");

		digits = hasDecimals ? whole + "." + digits[(last + 1)..] : whole;

		return decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value) ? value : null;
	}

	private static void Remember(uint app, string marketHashName, decimal usd) {
		bool due;
		int currency = Currency;

		lock (Cache) {
			long at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			Cache[$"{app}/{currency}/{marketHashName}"] = new Price { Usd = usd, At = at };
			Interlocked.Exchange(ref _newestAt, Math.Max(Interlocked.Read(ref _newestAt), at));

			// Checked and claimed under the lock, so two accounts pricing together don't both save.
			due = DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(30);

			if (due) {
				_lastSave = DateTime.UtcNow;
			}
		}

		Cleared($"{app}/{currency}/{marketHashName}");   // priced: whatever trouble it had is over

		// Into every account's running total now, after the book holds it: an account recounting at the same moment
		// either already read this price or gets it here, never both (see InventoryValue.Priced).
		Tell(app, marketHashName, usd, currency);

		if (due) {
			Save();
		}
	}

	// ── who wants to hear about a new price ─────────────────────────────────
	// Every account valuing its inventory. Each price that lands moves their totals straight away - the one that asked
	// and any other holding the same item - instead of the number sitting still until the next full recount.
	// Weak, so an account that was removed isn't kept alive by being on this list.
	private static readonly List<WeakReference<InventoryValue>> Tallies = [];

	/// <summary>Have this account's running total moved by every price that lands from now on.</summary>
	public static void Watch(InventoryValue tally) {
		lock (Tallies) {
			Tallies.RemoveAll(static w => !w.TryGetTarget(out _));
			Tallies.Add(new WeakReference<InventoryValue>(tally));
		}
	}

	private static void Tell(uint app, string marketHashName, decimal price, int currency) {
		List<InventoryValue> now = [];

		lock (Tallies) {
			foreach (WeakReference<InventoryValue> w in Tallies) {
				if (w.TryGetTarget(out InventoryValue? tally)) {
					now.Add(tally);
				}
			}
		}

		// Outside the list's lock: each account takes its own.
		foreach (InventoryValue tally in now) {
			try {
				tally.Priced(app, marketHashName, price, currency);
			} catch (Exception e) {
				Log.Debug($"couldn't add a price to a running total: {Log.Describe(e)}");
			}
		}
	}

	private static void Load() {
		// The check and the read under one lock. Marked loaded first and read after, a second caller in the meantime went
		// on as if it were loaded - two accounts pricing at once: the second found it loaded while the file was still being
		// read, took every item for unpriced, and its save wrote a near-empty price book over the real one.
		lock (LoadGate) {
			if (_loaded) {
				return;
			}

			_loaded = true;

			try {
				if (!File.Exists(Path)) {
					return;
				}

				Dictionary<string, Price>? saved = JsonSerializer.Deserialize<Dictionary<string, Price>>(File.ReadAllText(Path));

				if (saved != null) {
					lock (Cache) {
						foreach ((string key, Price p) in saved) {
							// Anything a month old is either an item nobody holds any more or a currency nobody uses
							// any more - keeping either for ever is how a cache file quietly becomes a megabyte.
							if (DateTime.UtcNow - p.When >= TimeSpan.FromDays(30)) {
								continue;
							}

							if (key.StartsWith(AsidePrefix, StringComparison.Ordinal)) {
								// Set aside (see Faulted): how many times in Usd, until when in At.
								lock (Troubled) {
									Troubled[key[AsidePrefix.Length..]] = (0, p.At, (int) p.Usd);
								}
							} else {
								Cache[key] = p;
								Interlocked.Exchange(ref _newestAt, Math.Max(Interlocked.Read(ref _newestAt), p.At));
							}
						}
					}
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't read the price book: {0}", Log.Describe(e)));
			}
		}
	}

	/// <summary>Write the book out. Called on a timer and once on the way out.</summary>
	public static void Save() {
		try {
			lock (SaveGate) {
				Dictionary<string, Price> snapshot;

				lock (Cache) {
					snapshot = new Dictionary<string, Price>(Cache, StringComparer.Ordinal);

					// What is set aside rides along in the same file - a handful of entries, written when the book is.
					lock (Troubled) {
						foreach ((string key, (int _, long until, int times)) in Troubled) {
							if (times > 0) {
								snapshot[AsidePrefix + key] = new Price { Usd = times, At = until };
							}
						}
					}

					if (snapshot.Count == 0) {
						return;
					}
				}

				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
				AtomicFile.Write(Path, JsonSerializer.Serialize(snapshot));
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the price book: {0}", Log.Describe(e)));
		}
	}

	[GeneratedRegex(@"[^\d.,]")]
	private static partial Regex MoneyChars();
}
