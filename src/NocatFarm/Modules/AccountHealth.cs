using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Account health: one line per account - level, bans, trade hold, market access, limited account, Steam Guard and phone
/// - green, amber or red, and an alert the moment one of them gets worse (once per change, never repeated).
/// </summary>
/// <remarks>
/// No new snooping: the bans come from the ban watch, Steam Guard and the phone from scam watch's read of the account
/// details page, the trade hold from the same hold lookup trade alerts use, limited-account status from what Steam says
/// at sign-in, and the level from the 'level' lookup. The one thing of its own is a single look at the Community Market
/// page, which says whether the market can be used and if not, why and until when. Read-only, so fine on any account at
/// any hour. Checked a few minutes after signing in, then every few hours.
/// </remarks>
public sealed partial class AccountHealth(Bot bot) : BotModule(bot) {
	internal static readonly Uri MarketPage = new("https://steamcommunity.com/market/");

	/// <summary>Green, amber, red - or not known yet.</summary>
	public enum Level { Unknown = -1, Good = 0, Warn = 1, Bad = 2 }

	/// <summary>What the market page said: usable or not, the reason in a word, and until when.</summary>
	public sealed record Market(bool Allowed, string Why, DateTime? Until);

	/// <summary>Everything known about the account's health. A null or empty value is one not known (yet).</summary>
	public sealed record Facts(uint? SteamLevel, BanWatch.Bans? Bans, int? HoldDays, Market? Market, bool? Limited, string Guard, string Phone);

	/// <summary>One line of the panel: which item, how it is, and the words for it.</summary>
	public sealed record Item(string Key, Level Level, Said Words);

	/// <summary>The items in the order they're shown and alerted.</summary>
	public static readonly string[] Keys = ["bans", "tradehold", "market", "limited", "guard", "phone"];

	private sealed class Saved {
		public long CheckedTicks { get; set; }
		public Facts? Facts { get; set; }
		public Dictionary<string, int> Levels { get; set; } = [];

		/// <summary>Steam Guard has come from Steam's two-factor service at least once - its first answer was the Guard item's baseline.</summary>
		public bool GuardByApi { get; set; }
	}

	private Saved? _saved;
	private readonly Lock _gate = new();

	private Saved State {
		get {
			lock (_gate) {
				return _saved ??= Load() ?? new Saved();
			}
		}
	}

	public override string Name => "health";

	// The card has its own Health line; the module row stays quiet.
	public override string Status => "";

	/// <summary>What is known now - null before the first check.</summary>
	public Facts? Last => State.Facts;

	public DateTime? CheckedAt => State.CheckedTicks > 0 ? new DateTime(State.CheckedTicks, DateTimeKind.Utc) : null;

	private string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", $"health-{Bot.Name}.json");

	// ── test seams (no network in the tests) ──
	internal Func<Uri, CancellationToken, Task<string?>>? Fetch { get; set; }
	internal Func<Task<uint?>>? LevelOf { get; set; }
	internal Func<CancellationToken, Task<TimeSpan?>>? HoldOf { get; set; }

	private bool Ready => (Fetch != null) || (Bot.IsOnline && Bot.Web.Ready);

	protected override async Task RunAsync(CancellationToken ct) {
		// At sign-in - a few minutes in, after scam watch's first look has read the account details.
		if (!await Sleep(Rng.Minutes(3, 7), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.Cfg.WatchHealth && Ready) {
				try {
					await CheckAsync(ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("account health couldn't look: {0}", Log.Describe(e)), Bot.Name);
				}
			}

			(int lo, int hi) = Gap(Bot.Cfg);

			if (!await Sleep(Rng.Minutes(lo * 60, hi * 60), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>The wait between checks, in hours: the two settings, in order, within 1 and 48.</summary>
	public static (int Lo, int Hi) Gap(BotConfig cfg) {
		int lo = Math.Clamp(cfg.HealthMinHours, 1, 48);
		int hi = Math.Clamp(cfg.HealthMaxHours, 1, 48);

		return (Math.Min(lo, hi), Math.Max(lo, hi));
	}

	private Task<Facts>? _checking;

	/// <summary>Check now. One check at a time - a second caller joins the one running.</summary>
	public async Task<Facts> CheckAsync(CancellationToken ct = default) {
		Task<Facts> look;

		lock (_gate) {
			look = _checking ??= Task.Run(async () => {
				try {
					return await CheckOnceAsync(CancellationToken.None).ConfigureAwait(false);
				} finally {
					lock (_gate) {
						_checking = null;
					}
				}
			}, CancellationToken.None);
		}

		return await look.WaitAsync(ct).ConfigureAwait(false);
	}

	private async Task<Facts> CheckOnceAsync(CancellationToken ct) {
		uint? level = null;

		try {
			level = await (LevelOf ?? Bot.GetLevelAsync)().ConfigureAwait(false);
		} catch (Exception e) when (e is not OperationCanceledException) {
			Log.Failed("account health: reading the level", e, Bot.Name);
		}

		TimeSpan? hold = await (HoldOf ?? OwnHoldAsync)(ct).ConfigureAwait(false);
		string? marketPage = await (Fetch != null ? Fetch(MarketPage, ct) : Bot.Web.GetAsync(MarketPage, ct)).ConfigureAwait(false);
		NoteMarket(marketPage);
		Market? market = ParseMarket(marketPage, DateTime.UtcNow);

		// 1.8.2: the market hold - nothing bought, listed or swapped while the page says it's blocked (it tells that itself).
		MarketHold.FromMarketPage(Bot, market, DateTime.UtcNow);
		ScamWatch.Details? details = BotManager.ModuleOf<ScamWatch>(Bot) is { } scam ? await scam.DetailsAsync(TimeSpan.FromHours(2), ct).ConfigureAwait(false) : null;

		// The first Steam Guard from Steam's two-factor service is a baseline: the page's shield guess before it (or "not
		// known") isn't compared with it, so switching source never alerts.
		if ((details?.GuardFrom == "service") && !State.GuardByApi) {
			lock (_gate) {
				State.GuardByApi = true;
				State.Levels.Remove("guard");
			}
		}

		Facts now = new(level, BotManager.ModuleOf<BanWatch>(Bot)?.Last, hold is { } h ? (int) Math.Ceiling(h.TotalDays) : null, market, Bot.Limited,
			details?.Guard ?? "", details?.Phone ?? "");

		return Apply(now, DateTime.UtcNow);
	}

	/// <summary>
	/// A new reading: what wasn't read this time stays as it was, each item is compared with the last, and anything that
	/// got WORSE is said once - with an alert where its switch is on. Better is only logged. The first reading is the
	/// baseline: what's already wrong is said calmly, once.
	/// </summary>
	internal Facts Apply(Facts read, DateTime nowUtc) {
		Saved s = State;
		Facts merged = Merge(s.Facts, read);
		List<Item> items = Assess(merged);
		bool first = s.Facts == null;
		Dictionary<string, int> before = new(s.Levels);

		foreach (Item item in items.Where(static i => i.Level != Level.Unknown)) {
			int was = before.TryGetValue(item.Key, out int w) ? w : (int) Level.Unknown;

			if (was == (int) Level.Unknown) {
				if (first && (item.Level != Level.Good)) {
					Log.Info(new Said("account health: {0}", item.Words), Bot.Name);
				}
			} else if ((int) item.Level > was) {
				// The market blocked is told by the market hold, with what waits for it - not twice.
				if (Alerts(Bot.Cfg, item.Key) && !((item.Key == "market") && MarketHold.On(Bot.Name))) {
					Log.Alarm(new Said("account health got worse: {0}", item.Words), Bot.Name, Topic.Health, Live.Global.NotifyHealth, red: false);
				} else {
					Log.Warn(new Said("account health got worse: {0}", item.Words), Bot.Name);
				}
			} else if ((int) item.Level < was) {
				Log.Info(new Said("account health better: {0}", item.Words), Bot.Name);
			}

			s.Levels[item.Key] = (int) item.Level;
		}

		lock (_gate) {
			s.Facts = merged;
			s.CheckedTicks = nowUtc.Ticks;
		}

		Save();

		return merged;
	}

	/// <summary>
	/// A ban reading just taken (BanWatch): health alerts a new ban - applied now, so it's told at once and exactly once,
	/// whoever read it. False when health doesn't: health off, its ban alert off, no ban level known here yet to compare
	/// with, or bans already red - another ban then isn't "worse" to health, and the ban look's own lines are the alert.
	/// </summary>
	internal bool BansRead(BanWatch.Bans bans) {
		if (!Bot.Cfg.WatchHealth || !Alerts(Bot.Cfg, "bans") || !State.Levels.TryGetValue("bans", out int was)
			|| ((int) Assess(new Facts(null, bans, null, null, null, "", "")).First(static i => i.Key == "bans").Level <= was)) {
			return false;
		}

		Apply(new Facts(null, bans, null, null, null, "", ""), DateTime.UtcNow);

		return true;
	}

	/// <summary>
	/// Whether an item's getting worse is alerted (rather than only logged). A new ban too: since 1.8.2 this is the one ban
	/// alert - the ban watch only reads, and says it in the log.
	/// </summary>
	public static bool Alerts(BotConfig cfg, string key) => key switch {
		"bans" => cfg.HealthAlertBans,
		"tradehold" => cfg.HealthAlertTradeHold,
		"market" => cfg.HealthAlertMarket,
		"limited" => cfg.HealthAlertLimited,
		"guard" => cfg.HealthAlertGuard,
		"phone" => cfg.HealthAlertPhone,
		_ => false
	};

	private static Facts Merge(Facts? before, Facts now) => before == null ? now : new Facts(
		now.SteamLevel ?? before.SteamLevel,
		now.Bans ?? before.Bans,
		now.HoldDays ?? before.HoldDays,
		now.Market ?? before.Market,
		now.Limited ?? before.Limited,
		now.Guard.Length > 0 ? now.Guard : before.Guard,
		now.Phone.Length > 0 ? now.Phone : before.Phone);

	/// <summary>Each item, green/amber/red, in plain words.</summary>
	public static List<Item> Assess(Facts f, DateTime? nowUtc = null) {
		DateTime now = nowUtc ?? DateTime.UtcNow;
		List<Item> items = [];

		items.Add(f.Bans == null ? new Item("bans", Level.Unknown, default)
			: f.Bans.Any ? new Item("bans", Level.Bad, new Said("bans: {0}", BanWatch.Summary(f.Bans)))
			: new Item("bans", Level.Good, new Said("no bans")));

		items.Add(f.HoldDays switch {
			null => new Item("tradehold", Level.Unknown, default),
			<= 0 => new Item("tradehold", Level.Good, new Said("no trade hold")),
			var d => new Item("tradehold", Level.Warn, new Said("trade hold: {0} day(s)", d))
		});

		items.Add(f.Market switch {
			null => new Item("market", Level.Unknown, default),
			{ Allowed: true } => new Item("market", Level.Good, new Said("market: open")),
			{ Until: { } until } when until > now => new Item("market", Level.Warn,
				new Said("market: blocked for {0} day(s) ({1})", Math.Max(1, (int) Math.Ceiling((until - now).TotalDays)), Reason(f.Market.Why))),
			_ => new Item("market", Level.Bad, new Said("market: blocked ({0})", Reason(f.Market.Why)))
		});

		items.Add(f.Limited switch {
			null => new Item("limited", Level.Unknown, default),
			true => new Item("limited", Level.Warn, new Said("limited account")),
			false => new Item("limited", Level.Good, new Said("not limited"))
		});

		items.Add(f.Guard switch {
			"phone app" => new Item("guard", Level.Good, new Said("Steam Guard: phone app")),
			"email" => new Item("guard", Level.Warn, new Said("Steam Guard: email codes only")),
			"off" => new Item("guard", Level.Bad, new Said("Steam Guard: off")),
			_ => new Item("guard", Level.Unknown, default)
		});

		items.Add(f.Phone switch {
			"" => new Item("phone", Level.Unknown, default),
			"none" => new Item("phone", Level.Warn, new Said("no phone number")),
			var p => new Item("phone", Level.Good, new Said("phone ends in {0}", p))
		});

		return items;
	}

	/// <summary>The market's reason in plain words.</summary>
	public static Said Reason(string why) => why switch {
		"new device" => new Said("new device"),
		"new authenticator" => new Said("new phone authenticator"),
		"password reset" => new Said("password was reset"),
		"guard too new" => new Said("Steam Guard not on long enough"),
		"no purchase" => new Said("no recent purchase"),
		"limited" => new Said("limited account"),
		"banned" => new Said("trade or market ban"),
		_ => new Said("reason not given")
	};

	/// <summary>The worst of the items, for the line's colour. Unknown when nothing is known.</summary>
	public static Level Worst(List<Item> items) => items.Count == 0 ? Level.Unknown : items.Max(static i => i.Level);

	/// <summary>
	/// The card's Health line: "all good · level 12" when everything known is fine, or what isn't fine:
	/// "market: blocked for 7 day(s) (new device) · no phone number".
	/// </summary>
	public static Said Line(Facts f, DateTime? nowUtc = null) {
		List<Item> items = Assess(f, nowUtc);
		List<Item> wrong = [.. items.Where(static i => i.Level is Level.Warn or Level.Bad).OrderByDescending(static i => i.Level)];
		string level = f.SteamLevel is { } l ? new Said("level {0}", l).ToString() : "";

		if (wrong.Count == 0) {
			return items.All(static i => i.Level == Level.Unknown)
				? new Said("not checked yet")
				: level.Length > 0 ? new Said("all good · {0}", level) : new Said("all good");
		}

		return new Said("{0}", string.Join(" · ", wrong.Select(static i => i.Words.ToString())));
	}

	// ── the trade hold: the same lookup trade alerts use ──

	/// <summary>
	/// This account's own trade hold, from Steam's hold lookup against another of your accounts (the lookup needs a
	/// partner; the account's own part of the answer doesn't depend on who). With no other account here, the last answer
	/// trade alerts got, from up to a week ago. Null when nothing is known.
	/// </summary>
	private async Task<TimeSpan?> OwnHoldAsync(CancellationToken ct) {
		if ((BotManager.Instance?.All ?? []).FirstOrDefault(b => (b != Bot) && (b.SteamId != 0)) is { } partner && Ready) {
			await TradeOffers.HoldAsync(Bot, partner.SteamId, ct).ConfigureAwait(false);
		}

		return TradeOffers.OwnHolds.TryGetValue(Bot.Name, out var known) && (DateTime.UtcNow - known.At < TimeSpan.FromDays(7)) ? known.Hold : null;
	}

	// ── the market page ──

	/// <summary>
	/// The Community Market page: open, or blocked with the reason and the date it can be used again. Null when it isn't
	/// the market page signed in (a sign-in page, an error) - that says nothing either way.
	/// Blocked ONLY on Steam's own list of restrictions (ul.market_restrictions) or its "unable to use the Community Market"
	/// sentence: the warning box at the top of the page also carries notes that don't block anything, and the word
	/// "purchase" is all over an open market page. The reason comes from that list or sentence only.
	/// </summary>
	public static Market? ParseMarket(string? html, DateTime nowUtc) {
		if (string.IsNullOrEmpty(html) || html.Contains("market_login_link_ctn", StringComparison.Ordinal)) {
			return null;
		}

		List<string> reasons = Restrictions(html);
		bool noAccess = (reasons.Count == 0) && (NoAccessSentence(html) != null);

		if ((reasons.Count == 0) && !noAccess) {
			return html.Contains("myListings", StringComparison.Ordinal) || html.Contains("market_search", StringComparison.Ordinal) || html.Contains("g_rgWalletInfo", StringComparison.Ordinal)
				? new Market(true, "", null)
				: null;
		}

		DateTime? until = null;

		if (CanUseRegex().Match(html) is { Success: true } m) {
			until = When(m.Groups[1].Value);
		} else if (Html.Between(html, "market_timecanuse_header", "</") is { } header) {
			until = When(Html.Text("<" + header));   // the rest of the span's tag (`">`) is a tag to strip, not text
		}

		// Steam's "unable to use the Community Market" sentence on its own is the market ban line.
		string why = noAccess ? "banned" : reasons.Select(WhyOf).FirstOrDefault(static w => w.Length > 0) ?? "";

		return new Market(false, why, until > nowUtc ? until : null);
	}

	/// <summary>The lines of Steam's restriction list on the market page, as plain text. Empty when there is no list.</summary>
	public static List<string> Restrictions(string html) =>
		RestrictionListRegex().Match(html) is { Success: true } list
			? [.. ListItemRegex().Matches(list.Groups[1].Value).Select(static li => Html.Text(li.Groups[1].Value)).Where(static t => t.Length > 0)]
			: [];

	/// <summary>Steam's "...unable to use the Community Market." sentence in the page's visible text (not its scripts).</summary>
	private static string? NoAccessSentence(string html) =>
		NoAccessRegex().Match(Html.Text(ScriptRegex().Replace(html, " "))) is { Success: true } m ? m.Value.Trim() : null;

	/// <summary>
	/// What Steam's market page says about access, for the debug log: its restriction list or "unable to use" sentence, or
	/// else the warning box at the top. The account's names and any long number are taken out. Null when there's none.
	/// </summary>
	public static string? MarketNote(string? html, IEnumerable<string> names) {
		if (string.IsNullOrEmpty(html)) {
			return null;
		}

		List<string> reasons = Restrictions(html);
		string? text = reasons.Count > 0 ? string.Join(" / ", reasons)
			: NoAccessSentence(html) ?? (Box(html, "market_headertip_container_warning") is { } box ? Html.Text(ScriptRegex().Replace(box, " ")) : null);

		if (string.IsNullOrWhiteSpace(text)) {
			return null;
		}

		foreach (string name in names.Where(static n => n.Trim().Length >= 2)) {
			text = Regex.Replace(text, @"\b" + Regex.Escape(name.Trim()) + @"\b", "<account>", RegexOptions.IgnoreCase);
		}

		text = LongNumberRegex().Replace(text, "#");

		return text.Length > 400 ? text[..400] + "…" : text;
	}

	/// <summary>The whole &lt;div&gt; that carries <paramref name="marker"/> in its tag, nested divs included. Null when absent.</summary>
	private static string? Box(string html, string marker) {
		int at = html.IndexOf(marker, StringComparison.Ordinal);
		int start = at < 0 ? -1 : html.LastIndexOf("<div", at, StringComparison.OrdinalIgnoreCase);

		if (start < 0) {
			return null;
		}

		int depth = 0;

		for (int i = start; i < html.Length; i++) {
			if (string.Compare(html, i, "<div", 0, 4, StringComparison.OrdinalIgnoreCase) == 0) {
				depth++;
			} else if ((string.Compare(html, i, "</div", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) && (--depth == 0)) {
				return html[start..i];
			}
		}

		return html[start..];
	}

	private string? _marketNote;

	/// <summary>Steam's own words about market access go in the debug log once each time they change - so the real page can be checked.</summary>
	private void NoteMarket(string? html) {
		if (string.IsNullOrEmpty(html)) {
			return;   // a failed read changes nothing
		}

		// The login only: the account's own name is on every log line already, and a name like "old" is also a word in
		// Steam's sentence ("a year old").
		string? note = MarketNote(html, [Bot.Cfg.SteamLogin]);

		lock (_gate) {
			if (note == _marketNote) {
				return;
			}

			_marketNote = note;
		}

		if (note != null) {
			Log.Debug(new Said("the market page says: {0}", note), Bot.Name);
		}
	}

	/// <summary>The reason in one line of Steam's restriction list, as a word the panel translates. Empty when it isn't one we know.</summary>
	public static string WhyOf(string text) =>
		text.Contains("device", StringComparison.OrdinalIgnoreCase) ? "new device"
		: text.Contains("Mobile Authenticator", StringComparison.OrdinalIgnoreCase) ? "new authenticator"
		: text.Contains("password", StringComparison.OrdinalIgnoreCase) ? "password reset"
		: text.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) ? "guard too new"
		: text.Contains("purchase", StringComparison.OrdinalIgnoreCase) ? "no purchase"
		: text.Contains("limited", StringComparison.OrdinalIgnoreCase) ? "limited"
		: text.Contains("unable to use", StringComparison.OrdinalIgnoreCase) || text.Contains("ban", StringComparison.OrdinalIgnoreCase) ? "banned"
		: "";

	[GeneratedRegex(@"<ul[^>]*\bclass\s*=\s*[""'][^""']*\bmarket_restrictions\b[^""']*[""'][^>]*>(.*?)</ul>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex RestrictionListRegex();

	[GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ListItemRegex();

	[GeneratedRegex(@"[^.!?]{0,120}\bunable to use the Community Market\b[^.!?]{0,120}[.!?]?", RegexOptions.IgnoreCase)]
	private static partial Regex NoAccessRegex();

	[GeneratedRegex(@"<(script|style)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	private static partial Regex ScriptRegex();

	[GeneratedRegex(@"\d{5,}")]
	private static partial Regex LongNumberRegex();

	/// <summary>
	/// A date as Steam writes it on the market page - "Sat, 18 Oct 2026 17:00:00 +0000", "Oct 18, 2026 10:00:00 GMT-0700"
	/// or "2026-10-18T17:00:00Z" - in UTC. Null when it won't read.
	/// </summary>
	public static DateTime? When(string text) {
		string t = ZoneRegex().Replace(text.Replace("GMT", " ", StringComparison.Ordinal).Trim(), "$1:$2");

		while (t.Contains("  ", StringComparison.Ordinal)) {
			t = t.Replace("  ", " ", StringComparison.Ordinal);
		}

		return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) ? at.UtcDateTime : null;
	}

	[GeneratedRegex(@"([+-]\d{2})(\d{2})\s*$")]
	private static partial Regex ZoneRegex();

	/// <summary>The date Steam's page script says the market can be used from.</summary>
	[GeneratedRegex(@"dateCanUseMarket\s*=\s*new\s+Date\(\s*[""']([^""']+)[""']")]
	private static partial Regex CanUseRegex();

	// ── saving ──

	private Saved? Load() {
		try {
			return File.Exists(StatePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(StatePath)) : null;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the saved account health: {0}", Log.Describe(e)), Bot.Name);

			return null;
		}
	}

	private void Save() {
		try {
			string json;

			lock (_gate) {
				json = JsonSerializer.Serialize(State);
			}

			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, json);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the account health: {0}", Log.Describe(e)), Bot.Name);
		}
	}
}
