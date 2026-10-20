using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2: one market hold per account - Steam's market can't be used (the market page says it's blocked and until when, a
/// trade or market ban, or Steam refusing a listing or purchase because the account isn't allowed). While it lasts nothing
/// is bought, listed, taken down or swapped by itself; purchases the owner already confirmed on the phone still finish.
/// Told once when it starts (pop-up, Discord/Telegram), the market page read again once its date has passed, and told once
/// when it's over. Kept in config/state/markethold-&lt;account&gt;.json, so a restart neither forgets it nor tells it again.
/// </summary>
/// <remarks>
/// Besides the hold, a short pause on listing only: Steam saying too many listings wait for a confirmation (2 hours), that
/// the wallet would go over its most (a day, told once) or that the market is unavailable just now (1 hour). And the
/// "said once" memories other parts use for Steam's refusals, so a restart doesn't say them again.
/// </remarks>
public static class MarketHold {
	/// <summary>What Steam's refusal of a listing or purchase says.</summary>
	public enum Refusal {
		/// <summary>No reason given.</summary>
		None,

		/// <summary>A reason that isn't a lasting one (this item, try again, ...).</summary>
		Other,

		/// <summary>The account isn't allowed to use the Community Market: the market hold.</summary>
		NotAllowed,

		/// <summary>The market is unavailable just now: listing waits an hour.</summary>
		Unavailable,

		/// <summary>Too many listings wait for their confirmation: listing waits for them.</summary>
		TooManyPending,

		/// <summary>The wallet would go over its most: selling waits a day.</summary>
		WalletMax
	}

	/// <summary>How long listing waits after each kind of refusal, and how long a hold from a refusal lasts at least.</summary>
	public static readonly TimeSpan PendingWait = TimeSpan.FromHours(2), WalletWait = TimeSpan.FromHours(24), UnavailableWait = TimeSpan.FromHours(1),
		RefusedHold = TimeSpan.FromHours(6);

	/// <summary>The market page is read again this soon after a hold's date has passed - and this often while it hasn't one.</summary>
	public static readonly TimeSpan RecheckAfterDate = TimeSpan.FromMinutes(15), RecheckNoDate = TimeSpan.FromHours(6);

	/// <summary>The stored hold. <see cref="From"/> empty: no hold.</summary>
	public sealed class State {
		/// <summary>"market" (the market page), "ban" (a trade ban), "refused" (Steam refused a listing or purchase); "" none.</summary>
		public string From { get; set; } = "";

		/// <summary>The reason as a word (<see cref="AccountHealth.Reason"/>'s, or "refused").</summary>
		public string Why { get; set; } = "";

		/// <summary>Until when (UTC ticks), 0 when not known.</summary>
		public long Until { get; set; }

		/// <summary>The date came from Steam (the market page) - only then is it said.</summary>
		public bool DateKnown { get; set; }

		/// <summary>Told already (once a hold).</summary>
		public bool Told { get; set; }

		/// <summary>When the market page was last read for it (UTC ticks).</summary>
		public long CheckedAt { get; set; }

		/// <summary>Listing only waits until then (UTC ticks), and why: "pending", "wallet", "unavailable".</summary>
		public long SellUntil { get; set; }

		public string SellWhy { get; set; } = "";

		/// <summary>Things said once, by key, with when (UTC ticks) - forgotten when they no longer apply.</summary>
		public Dictionary<string, long> Said { get; set; } = new(StringComparer.Ordinal);
	}

	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, State> Loaded = new(StringComparer.OrdinalIgnoreCase);

	private static string PathOf(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"markethold-{account}.json");

	/// <summary>Test seam: the market page, instead of asking Steam.</summary>
	internal static Func<Bot, CancellationToken, Task<string?>>? FetchPage { get; set; }

	/// <summary>Only the checks: forget what was read, as if the program had just started.</summary>
	public static void ForgetLoaded() {
		lock (Gate) {
			Loaded.Clear();
		}
	}

	private static State Of(string account) {
		string path = PathOf(account);

		if (Loaded.TryGetValue(path, out State? held)) {
			return held;
		}

		State read = new();

		try {
			if (File.Exists(path)) {
				read = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			Log.Failed("couldn't read the market hold", e, account);
		}

		read.Said = new Dictionary<string, long>(read.Said ?? [], StringComparer.Ordinal);
		read.From ??= "";
		read.Why ??= "";
		read.SellWhy ??= "";
		Loaded[path] = read;

		return read;
	}

	private static void Save(string account, State s) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(PathOf(account))!);
			AtomicFile.Write(PathOf(account), JsonSerializer.Serialize(s));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Failed("couldn't save the market hold", e, account);
		}
	}

	/// <summary>A copy of the account's hold as it is now (for the checks and the dashboard).</summary>
	public static State Snapshot(string account) {
		lock (Gate) {
			State s = Of(account);

			return new State {
				From = s.From, Why = s.Why, Until = s.Until, DateKnown = s.DateKnown, Told = s.Told, CheckedAt = s.CheckedAt, SellUntil = s.SellUntil, SellWhy = s.SellWhy,
				Said = new Dictionary<string, long>(s.Said, StringComparer.Ordinal)
			};
		}
	}

	/// <summary>Whether the account's market is held.</summary>
	public static bool On(string account) {
		lock (Gate) {
			return Of(account).From.Length > 0;
		}
	}

	// ── asking ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Why nothing may be bought, listed or swapped on this account now - or null when it may. Past the hold's date (or every
	/// few hours without one) the market page is read again in the background, and the hold ends when it says the market is
	/// open.
	/// </summary>
	public static Said? Blocked(Bot bot) {
		DateTime now = DateTime.UtcNow;
		bool recheck;
		Said said;

		lock (Gate) {
			State s = Of(bot.Name);

			if (s.From.Length == 0) {
				return null;
			}

			recheck = RecheckDue(s, now);

			if (recheck) {
				s.CheckedAt = now.Ticks;
			}

			said = HeldSaid(s);
		}

		if (recheck && (FetchPage != null || (bot.IsOnline && bot.Web.Ready))) {
			_ = Task.Run(async () => {
				try {
					await RecheckAsync(bot, CancellationToken.None).ConfigureAwait(false);
				} catch (Exception e) {
					Log.Debug($"market hold: couldn't read the market page again: {Log.Describe(e)}", bot.Name);
				}
			});
		}

		return said;
	}

	/// <summary>Why nothing may be listed on this account now (the hold, or a short pause on listing) - or null when it may.</summary>
	public static Said? SellBlocked(Bot bot) {
		if (Blocked(bot) is { } held) {
			return held;
		}

		lock (Gate) {
			State s = Of(bot.Name);

			if (DateTime.UtcNow.Ticks >= s.SellUntil) {
				return null;
			}

			DateTime until = new(s.SellUntil, DateTimeKind.Utc);

			return s.SellWhy switch {
				"wallet" => new Said("the wallet would go over its most if more sold - selling waits until about {0}", (Func<string>) (() => Fmt.Clock(until))),
				"pending" => new Said("too many listings wait for a confirmation in the Steam app - no more listed until about {0}", (Func<string>) (() => Fmt.Clock(until))),
				_ => new Said("Steam's market is unavailable just now - listing waits until about {0}", (Func<string>) (() => Fmt.Clock(until)))
			};
		}
	}

	/// <summary>Whether the market page is due to be read again for this hold: its date passed, or none and a while gone by.</summary>
	internal static bool RecheckDue(State s, DateTime now) {
		if (s.From is "" or "ban") {
			return false;   // a ban is ended by the ban look, never by the market page
		}

		// Steam refused, and the page hasn't been read for it yet: read now, for the reason and the date.
		if ((s.From == "refused") && (s.CheckedAt == 0)) {
			return true;
		}

		bool past = (s.Until > 0) && (now.Ticks >= s.Until);

		// Past its date: read once straight away (not read since the date), then every quarter of an hour while still blocked.
		return past ? (s.CheckedAt < s.Until) || (now.Ticks - s.CheckedAt >= RecheckAfterDate.Ticks) :(s.Until == 0) && (now.Ticks - s.CheckedAt >= RecheckNoDate.Ticks);
	}

	/// <summary>The hold in plain words, for a module's status and a command's answer.</summary>
	private static Said HeldSaid(State s) {
		Said reason = ReasonSaid(s.Why);

		if (s.DateKnown && (s.Until > 0) && (DateTime.UtcNow.Ticks < s.Until)) {
			DateTime until = new(s.Until, DateTimeKind.Utc);

			return new Said("Steam's market is blocked until {0} ({1}) - buying, selling and swaps wait until then", (Func<string>) (() => Fmt.Clock(until)), reason);
		}

		// 1.8.2: no recent purchase, or a limited account - it lasts until a purchase is made on Steam, so that's what's said.
		return s.Why is "no purchase" or "limited"
			? new Said("Steam's market is blocked ({0}) - buying, selling and swaps wait until Steam lifts it, which takes a purchase on Steam on this account", reason)
			: new Said("Steam's market is blocked ({0}) - buying, selling and swaps wait until it opens again", reason);
	}

	/// <summary>The reason in plain words: the market page's, or Steam's refusal.</summary>
	public static Said ReasonSaid(string why) => why == "refused" ? new Said("Steam said this account isn't allowed to use the market") : AccountHealth.Reason(why);

	// ── what sets and ends it ───────────────────────────────────────────────

	/// <summary>What the market page said (account health's look, or the hold's own): blocked starts or updates the hold; open ends one from the page or a refusal whose time is up.</summary>
	public static void FromMarketPage(Bot bot, AccountHealth.Market? market, DateTime nowUtc) {
		if (market == null) {
			return;   // a sign-in page or an error says nothing either way
		}

		if (!market.Allowed) {
			Set(bot, "market", market.Why.Length > 0 ? market.Why : "", market.Until, nowUtc);

			return;
		}

		bool clear;

		lock (Gate) {
			State s = Of(bot.Name);
			s.CheckedAt = nowUtc.Ticks;
			clear = (s.From == "market") || ((s.From == "refused") && (nowUtc.Ticks >= s.Until));
			Save(bot.Name, s);
		}

		if (clear) {
			Clear(bot);
		}
	}

	/// <summary>A ban reading (the ban look): a trade ban holds the market; one gone ends a hold it started.</summary>
	public static void FromBans(Bot bot, BanWatch.Bans? bans, DateTime nowUtc) {
		if (bans == null) {
			return;
		}

		if (bans.TradeBanned) {
			Set(bot, "ban", "banned", null, nowUtc);

			return;
		}

		bool clear;

		lock (Gate) {
			clear = Of(bot.Name).From == "ban";
		}

		if (clear) {
			Clear(bot);
		}
	}

	/// <summary>
	/// Steam refused a listing or a purchase (<paramref name="selling"/>): not allowed - the hold (at least
	/// <see cref="RefusedHold"/>, and the market page read for its date); otherwise a pause on listing only.
	/// </summary>
	public static void Refused(Bot bot, Refusal kind, DateTime nowUtc, bool selling = true) {
		switch (kind) {
			case Refusal.NotAllowed:
				Set(bot, "refused", "refused", nowUtc + RefusedHold, nowUtc);

				return;
			case Refusal.TooManyPending when selling:
				PauseSelling(bot, "pending", nowUtc + PendingWait, nowUtc);
				Log.Info(new Said("too many listings wait for a confirmation in the Steam app - no more listed until they're confirmed"), bot.Name);

				return;
			case Refusal.WalletMax when selling:
				PauseSelling(bot, "wallet", nowUtc + WalletWait, nowUtc);

				if (Once(bot.Name, "wallet", TimeSpan.FromDays(7))) {
					Log.Attention(new Said("Steam won't list more - the wallet would go over its most. Selling waits a day; spending some of the wallet lets it sell again"), bot.Name, Topic.Health);
				}

				return;
			case Refusal.Unavailable when selling:
				PauseSelling(bot, "unavailable", nowUtc + UnavailableWait, nowUtc);
				Log.Info(new Said("Steam's market is unavailable just now - listing waits an hour"), bot.Name);

				return;
		}
	}

	private static void PauseSelling(Bot bot, string why, DateTime until, DateTime nowUtc) {
		lock (Gate) {
			State s = Of(bot.Name);

			if ((s.SellUntil > nowUtc.Ticks) && (s.SellUntil >= until.Ticks)) {
				return;
			}

			s.SellUntil = until.Ticks;
			s.SellWhy = why;
			Save(bot.Name, s);
		}
	}

	/// <summary>Strongest first: a ban outranks the market page, which outranks a refusal.</summary>
	private static int Rank(string from) => from switch { "ban" => 3, "market" => 2, "refused" => 1, _ => 0 };

	private static void Set(Bot bot, string from, string why, DateTime? until, DateTime nowUtc) {
		bool tell;
		State told;

		lock (Gate) {
			State s = Of(bot.Name);

			// A weaker reason never replaces a stronger one; a refusal while the page's hold stands changes nothing.
			if ((s.From.Length > 0) && (Rank(s.From) > Rank(from))) {
				return;
			}

			bool had = s.From.Length > 0;
			s.From = from;
			s.Why = why;
			s.Until = until?.Ticks ?? 0;
			s.DateKnown = (from == "market") && (until != null);
			s.CheckedAt = from == "refused" ? 0 : nowUtc.Ticks;   // a refusal: the page is read for its date straight away

			if (!had) {
				s.Told = false;
			}

			tell = !s.Told;
			s.Told = true;
			Save(bot.Name, s);
			told = s;
		}

		Log.Debug($"market hold: {from} ({why}){(until is { } u ? $" until {u.ToString("u", System.Globalization.CultureInfo.InvariantCulture)}" : "")}", bot.Name);

		if (tell) {
			Log.Attention(HeldSaid(told), bot.Name, Topic.Health);
			CardExchange.Nudge();   // nocat.lol takes it off the swap list
		}

		if (from == "refused") {
			_ = Blocked(bot);   // reads the market page for the reason and the date
		}
	}

	private static void Clear(Bot bot) {
		lock (Gate) {
			State s = Of(bot.Name);

			if (s.From.Length == 0) {
				return;
			}

			s.From = "";
			s.Why = "";
			s.Until = 0;
			s.DateKnown = false;
			s.Told = false;
			Save(bot.Name, s);
		}

		Log.Good(new Said("Steam's market is open again - buying, selling and swaps go on"), bot.Name);
		Log.Publish(Topic.Health, bot.Name, new Said("Steam's market is open again - buying, selling and swaps go on"));
		CardExchange.Nudge();
	}

	/// <summary>The market page read again for the hold (no other request): what it says sets or ends it.</summary>
	public static async Task RecheckAsync(Bot bot, CancellationToken ct) {
		string? html = await (FetchPage?.Invoke(bot, ct) ?? bot.Web.GetAsync(AccountHealth.MarketPage, ct)).ConfigureAwait(false);
		FromMarketPage(bot, AccountHealth.ParseMarket(html, DateTime.UtcNow), DateTime.UtcNow);
	}

	// ── Steam's refusals ────────────────────────────────────────────────────

	/// <summary>
	/// What a refusal of a listing or a purchase says, from Steam's own message (English, as Steam answers the app):
	/// "You are not allowed to use the Community Market", "Your account is currently unable to use the Community Market",
	/// "You cannot sell items ..." (not "until your previous action completes", nor about one item); "You have too many
	/// listings pending confirmation"; "... your wallet would exceed its maximum balance"; "The market is unavailable".
	/// </summary>
	public static Refusal Classify(string? message) {
		if (string.IsNullOrWhiteSpace(message)) {
			return Refusal.None;
		}

		string m = message.ToLowerInvariant();

		if (m.Contains("pending confirmation", StringComparison.Ordinal) && (m.Contains("too many", StringComparison.Ordinal) || m.Contains("listings", StringComparison.Ordinal))) {
			return Refusal.TooManyPending;
		}

		if (m.Contains("wallet", StringComparison.Ordinal) && (m.Contains("maximum", StringComparison.Ordinal) || m.Contains("exceed", StringComparison.Ordinal))) {
			return Refusal.WalletMax;
		}

		if (m.Contains("community market", StringComparison.Ordinal) && (m.Contains("not allowed", StringComparison.Ordinal) || m.Contains("unable to use", StringComparison.Ordinal)
			|| m.Contains("cannot use", StringComparison.Ordinal) || m.Contains("can't use", StringComparison.Ordinal) || m.Contains("restricted", StringComparison.Ordinal)
			|| m.Contains("locked", StringComparison.Ordinal))) {
			return Refusal.NotAllowed;
		}

		if (m.Contains("trade ban", StringComparison.Ordinal) || m.Contains("market ban", StringComparison.Ordinal)) {
			return Refusal.NotAllowed;
		}

		if (m.Contains("unavailable", StringComparison.Ordinal) && m.Contains("market", StringComparison.Ordinal)) {
			return Refusal.Unavailable;
		}

		if ((m.Contains("you cannot sell", StringComparison.Ordinal) || m.Contains("you can't sell", StringComparison.Ordinal))
			&& !m.Contains("previous action", StringComparison.Ordinal) && !m.Contains("this item", StringComparison.Ordinal) && !m.Contains("that item", StringComparison.Ordinal)) {
			return Refusal.NotAllowed;
		}

		return Refusal.Other;
	}

	/// <summary>Whether a refusal means there's no point trying the rest now.</summary>
	public static bool Lasting(Refusal kind) => kind is Refusal.NotAllowed or Refusal.Unavailable or Refusal.TooManyPending or Refusal.WalletMax;

	// ── said once ───────────────────────────────────────────────────────────

	/// <summary>
	/// True the first time <paramref name="key"/> is asked for this account - and again only once it's been forgotten
	/// (<see cref="Forget"/>) or <paramref name="forgetAfter"/> has passed. Kept on disk, so a restart doesn't say it again.
	/// </summary>
	public static bool Once(string account, string key, TimeSpan? forgetAfter = null) {
		DateTime now = DateTime.UtcNow;

		lock (Gate) {
			State s = Of(account);

			if (s.Said.TryGetValue(key, out long at) && ((forgetAfter == null) || (now.Ticks - at < forgetAfter.Value.Ticks))) {
				return false;
			}

			s.Said[key] = now.Ticks;

			// Old ones go: a month is far past anything said once.
			foreach (string old in s.Said.Where(p => now.Ticks - p.Value > TimeSpan.FromDays(30).Ticks).Select(static p => p.Key).ToList()) {
				s.Said.Remove(old);
			}

			Save(account, s);

			return true;
		}
	}

	/// <summary>Said again next time: what <paramref name="key"/> was about is over.</summary>
	public static void Forget(string account, string key) {
		lock (Gate) {
			State s = Of(account);

			if (s.Said.Remove(key)) {
				Save(account, s);
			}
		}
	}
}
