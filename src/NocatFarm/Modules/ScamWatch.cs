using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;
using SteamKit2;

namespace NocatFarm.Modules;

/// <summary>
/// Scam watch: looks for the signs of an account being taken over, or set up for the API-key trade scam, and says so
/// at once - a red log line, a pop-up, Discord/Telegram, and a banner on the dashboard.
/// </summary>
/// <remarks>
/// Every half hour or so (jittered) it opens three of the account's own pages, the way a person would in a browser:
/// <list type="bullet">
/// <item>the Web API key page - a key appearing (or changing) that wasn't there before is the API-key trade scam: the
/// thief watches the account's offers and swaps them for fake ones. Only a fingerprint of the key is kept;</item>
/// <item>the authorized-devices page - a device that's new since the last look and isn't one of yours (nocat.farm's own
/// sign-ins carry this PC's device name, <see cref="Bot.DeviceName"/>);</item>
/// <item>the account details page - the masked email, the phone ending and Steam Guard, compared with the last look.</item>
/// </list>
/// And when the saved login is refused because every session was signed out (<see cref="SavedLoginRefused"/>).
///
/// On a hit, as set per account: trades stop being accepted (and items sent) until 'scamwatch ok &lt;account&gt;', the
/// account's own outgoing offers are cancelled, and - only when switched on, off by default - a new API key is revoked.
/// Nothing else is ever changed. The first look is only a baseline: what's already there was there before nocat.farm
/// was watching, so it isn't reported as new.
/// </remarks>
public sealed partial class ScamWatch(Bot bot) : BotModule(bot) {
	public const string ApiKeyPageUrl = "https://steamcommunity.com/dev/apikey";
	public const string DevicesPageUrl = "https://store.steampowered.com/account/authorizeddevices";
	public const string AccountPageUrl = "https://store.steampowered.com/account/";
	internal static readonly Uri ApiKeyPage = new(ApiKeyPageUrl);
	internal static readonly Uri RevokeKeyUrl = new("https://steamcommunity.com/dev/revokekey");
	internal static readonly Uri DevicesPage = new(DevicesPageUrl);
	internal static readonly Uri AccountPage = new(AccountPageUrl);

	// ── what the pages say ──

	/// <summary>What the API key page shows: no key, a key (its fingerprint only), no access, or couldn't tell.</summary>
	public enum KeyState { Unknown, None, Key, Denied }

	/// <summary>The API key page read: the state, and for a key its fingerprint - never the key.</summary>
	public sealed record KeyRead(KeyState State, string Fingerprint = "");

	/// <summary>
	/// One authorized device: an id (a fingerprint of Steam's token id), its name, where it was first seen, and whether it's
	/// the very sign-in reading the page - nocat.farm's own session, by definition.
	/// </summary>
	public sealed record Device(string Id, string Name, string Where, bool ThisSession = false);

	/// <summary>
	/// The account details page: the email hidden the way Steam hides it ("th***@gmail.com") with a fingerprint of the real
	/// one to compare by, the phone ending ("12", "none" for no phone), and Steam Guard ("phone app", "email", "off"). An
	/// empty value is one the page didn't show - never compared. The address itself is never kept. <paramref name="GuardFrom"/>
	/// says where Steam Guard came from: "service" (Steam's two-factor service), "page" (the page's shield), or "" (not known).
	/// </summary>
	public sealed record Details(string Email, string EmailId, string Phone, string Guard, string GuardFrom = "") {
		public bool Any => (EmailId.Length > 0) || (Phone.Length > 0) || (Guard.Length > 0);
	}

	/// <summary>One warning: what it was about, a detail (a device name, the new masked email), and when.</summary>
	public sealed record Hit(string Kind, string Detail, long AtTicks) {
		public DateTime At => new(AtTicks, DateTimeKind.Utc);
	}

	private sealed class Saved {
		public long CheckedTicks { get; set; }
		public bool KeyKnown { get; set; }
		public string KeyFingerprint { get; set; } = "";
		public List<string>? DeviceIds { get; set; }
		public Details? Details { get; set; }
		public List<Hit> Hits { get; set; } = [];

		/// <summary>The outgoing offers were cancelled for the warnings standing now - not again until 'scamwatch ok'.</summary>
		public bool OffersCancelled { get; set; }

		/// <summary>
		/// Steam's two-factor service has answered for this account at least once. From then on Steam Guard comes only from
		/// it: a read it doesn't answer is "not known this time", never the page's shield guess.
		/// </summary>
		public bool GuardByApi { get; set; }

		/// <summary>The last look at each page: "ok", or "couldn't read" - for the 'scamwatch' command.</summary>
		public Dictionary<string, string> Pages { get; set; } = [];
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

	public override string Name => "scam watch";

	public override string Status => Hits.Count > 0 ? Loc.T("WARNING - {0}", What(Hits[^1]).ToString()) : "";

	/// <summary>The warnings standing now, oldest first. Empty when all is well.</summary>
	public IReadOnlyList<Hit> Hits {
		get {
			lock (_gate) {
				return [.. State.Hits];
			}
		}
	}

	/// <summary>When it last looked. Null before the first look.</summary>
	public DateTime? CheckedAt => State.CheckedTicks > 0 ? new DateTime(State.CheckedTicks, DateTimeKind.Utc) : null;

	/// <summary>The last account-details read, for account health (Steam Guard, phone) - null until one has been read.</summary>
	public Details? LastDetails => State.Details;

	/// <summary>When <see cref="LastDetails"/> was read.</summary>
	public DateTime? DetailsAt { get; private set; }

	/// <summary>Whether this account is holding its trades because of a warning (and the setting says to).</summary>
	public bool HoldingTrades => Bot.Cfg.ScamWatchStopTrades && (Hits.Count > 0);

	/// <summary>True when scam watch is holding this account's trades - Trading and Sender ask before acting by themselves.</summary>
	public static bool HoldsTrades(Bot bot) => BotManager.ModuleOf<ScamWatch>(bot) is { HoldingTrades: true };

	// ── the hold: one gate for everything that moves items out ──

	/// <summary>A command you typed, running now - each held account it touches is warned about once.</summary>
	private sealed class YouAsked {
		public readonly HashSet<string> Warned = new(StringComparer.Ordinal);
	}

	private static readonly AsyncLocal<YouAsked?> AskedByYou = new();

	/// <summary>
	/// Runs something you asked for by a command ('send', 'sell do', 'gems do', 'booster open ... do', 'match do'): a scam
	/// watch hold doesn't stop it, it only says so. Only around those commands - never a whole command line, since 'start'
	/// sets an account's modules going from inside one, and they'd all have passed the hold for good.
	/// </summary>
	public static async Task<T> AsYouAskedAsync<T>(Func<Task<T>> work) {
		AskedByYou.Value = new YouAsked();   // in this call and what it starts only - an async method's change never reaches its caller

		return await work().ConfigureAwait(false);
	}

	/// <summary>
	/// The one gate every send, swap and market listing goes through (Looting, Seller, spare items and packs): null when it
	/// may go ahead, or the line saying why it's held. Something you asked for by a command goes ahead with a warning line.
	/// </summary>
	public static string? HeldBack(Bot bot) {
		if (!HoldsTrades(bot)) {
			return null;
		}

		if (AskedByYou.Value is { } you) {
			bool first;

			lock (you.Warned) {
				first = you.Warned.Add(bot.Name);
			}

			if (first) {
				Log.Warn(new Said("scam watch is holding {0}'s trades - doing this anyway because you asked. Type 'scamwatch ok {0}' once you've checked", bot.Name), bot.Name);
			}

			return null;
		}

		return $"{bot.Name}: {OnHold(bot.Name)}";
	}

	/// <summary>What a send or a sale held by scam watch says instead (filed under the account, so without its name in front).</summary>
	public static Said OnHold(string account) =>
		new Said("on hold after a scam watch warning - nothing is sent or sold by itself until 'scamwatch ok {0}'", account);

	private string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", $"scamwatch-{Bot.Name}.json");

	// ── test seams: the pages and the two things it can do, faked in the tests (no network) ──
	internal Func<Uri, CancellationToken, Task<string?>>? Fetch { get; set; }
	internal Func<Uri, Dictionary<string, string>, Uri, CancellationToken, Task<string?>>? Post { get; set; }
	internal Func<CancellationToken, Task<bool?>>? CancelSent { get; set; }
	internal Func<Dictionary<string, string>, CancellationToken, Task<string?>>? GuardQuery { get; set; }

	private bool Ready => (Fetch != null) || (Bot.IsOnline && Bot.Web.Ready);

	private Task<string?> GetAsync(Uri url, CancellationToken ct) => Fetch != null ? Fetch(url, ct) : Bot.Web.GetAsync(url, ct);

	protected override async Task RunAsync(CancellationToken ct) {
		// Not in the very first minutes after signing in, with everything else.
		if (!await Sleep(Rng.Minutes(1, 4), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.Cfg.WatchForScams && Ready) {
				try {
					await CheckAsync(ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("scam watch couldn't look: {0}", Log.Describe(e)), Bot.Name);
				}
			}

			(int lo, int hi) = Gap(Bot.Cfg);

			if (!await Sleep(Rng.Minutes(lo, hi), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>The wait between looks, in minutes: the two settings, in order, within 10 minutes and a day.</summary>
	public static (int Lo, int Hi) Gap(BotConfig cfg) {
		int lo = Math.Clamp(cfg.ScamWatchMinMinutes, 10, 1440);
		int hi = Math.Clamp(cfg.ScamWatchMaxMinutes, 10, 1440);

		return (Math.Min(lo, hi), Math.Max(lo, hi));
	}

	private Task<List<Hit>>? _checking;

	/// <summary>Look now. Returns the warnings this look raised (empty when all is well). One look at a time.</summary>
	public async Task<List<Hit>> CheckAsync(CancellationToken ct = default) {
		Task<List<Hit>> look;

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

	private async Task<List<Hit>> CheckOnceAsync(CancellationToken ct) {
		BotConfig cfg = Bot.Cfg;
		List<Hit> found = [];
		Saved s = State;
		long now = DateTime.UtcNow.Ticks;

		if (cfg.ScamWatchApiKey) {
			KeyRead key = ParseApiKey(await GetAsync(ApiKeyPage, ct).ConfigureAwait(false));
			MarkPage(s, "key", key.State is not KeyState.Unknown);

			// No access to the page (no validated email, or no phone authenticator) is "known, and no key": an account like
			// that can't have one, so a key showing up later is one that appeared - not the first look's baseline.
			if (key.State == KeyState.Denied) {
				key = new KeyRead(KeyState.None);
			}

			if (key.State is KeyState.None or KeyState.Key) {
				if (KeyNews(s.KeyKnown ? s.KeyFingerprint : null, key) is { } kind) {
					found.Add(new Hit(kind, "", now));
				} else if (!s.KeyKnown && (key.State == KeyState.Key)) {
					// The first look: a key that was already there. Said once, calmly - it may be one you made.
					Log.Warn(new Said("this account already has a Steam Web API key - if you didn't make it, revoke it at {0}", ApiKeyPage), Bot.Name);
				}

				s.KeyKnown = true;
				s.KeyFingerprint = key.Fingerprint;

				if ((found.Count > 0) && (found[^1].Kind is "apikey" or "apikey-changed") && cfg.ScamWatchRevokeKey) {
					await RevokeKeyAsync(s, ct).ConfigureAwait(false);
				}
			}
		}

		if (cfg.ScamWatchDevices) {
			List<Device>? devices = ParseDevices(await GetAsync(DevicesPage, ct).ConfigureAwait(false));
			MarkPage(s, "devices", devices != null);

			if (devices != null) {
				HashSet<string> ours = OwnDeviceNames(Bot);

				foreach (Device d in NewDevices(s.DeviceIds, devices, ours)) {
					found.Add(new Hit("device", string.IsNullOrWhiteSpace(d.Where) ? d.Name : $"{d.Name} ({d.Where})", now));
				}

				s.DeviceIds = [.. devices.Select(static d => d.Id)];
			}
		}

		if (cfg.ScamWatchDetails) {
			Details? page = ParseDetails(await GetAsync(AccountPage, ct).ConfigureAwait(false));
			MarkPage(s, "details", page != null);
			Details? details = WithGuard(page, await GuardFromServiceAsync(ct).ConfigureAwait(false));

			if (details != null) {
				Details? before = s.Details;

				// The first answer from Steam's two-factor service is a baseline: what the page's shield guessed before
				// (or "not known") isn't compared with it.
				if ((details.GuardFrom == "service") && !s.GuardByApi) {
					before = before == null ? null : before with { Guard = "", GuardFrom = "" };
					s.GuardByApi = true;
				}

				found.AddRange(DetailNews(before, details).Select(n => new Hit(n.Kind, n.Detail, now)));

				// Kept field by field: a value this read didn't show stays as it was, rather than being forgotten and then
				// "changed" at the next read that shows it again.
				s.Details = Merge(s.Details, details);
				DetailsAt = DateTime.UtcNow;
			}
		}

		s.CheckedTicks = now;
		Raise(s, found);
		Save();

		if (s.Hits.Count > 0) {
			await AfterHitAsync(s, ct).ConfigureAwait(false);
		}

		return found;
	}

	/// <summary>The account details page read now, for account health - the last read when it's fresher than <paramref name="maxAge"/>.</summary>
	public async Task<Details?> DetailsAsync(TimeSpan maxAge, CancellationToken ct = default) {
		if ((State.Details is { } d) && (DetailsAt is { } at) && (DateTime.UtcNow - at < maxAge)) {
			return d;
		}

		if (!Ready) {
			return State.Details;
		}

		// Only read here - scam watch's own look is what compares and warns, so a health check never swallows a change.
		Details? read = WithGuard(ParseDetails(await GetAsync(AccountPage, ct).ConfigureAwait(false)), await GuardFromServiceAsync(ct).ConfigureAwait(false));

		return read == null ? State.Details : Merge(State.Details, read);
	}

	/// <summary>
	/// Steam Guard from Steam's two-factor service (ITwoFactorService/QueryStatus, with the account's own access token):
	/// "phone app", "email", "off", or "" when it didn't answer. Never throws for a failed read.
	/// </summary>
	private async Task<string> GuardFromServiceAsync(CancellationToken ct) {
		Func<Dictionary<string, string>, CancellationToken, Task<string?>>? query = GuardQuery;

		if (query == null) {
			if (Fetch != null) {
				return "";   // a test with faked pages and no faked service: nothing to ask
			}

			query = (form, c) => Bot.Web.ApiPostAsync("ITwoFactorService", "QueryStatus", form, c);
		}

		if (Bot.SteamId == 0) {
			return "";
		}

		try {
			return ParseTwoFactor(await query(new Dictionary<string, string> { ["steamid"] = Bot.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false));
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Failed("scam watch: asking Steam's two-factor service", e, Bot.Name);

			return "";
		}
	}

	/// <summary>
	/// The account page's read with Steam Guard from the best source: Steam's two-factor service when it answered; the page's
	/// shield only while the service has never answered for this account; otherwise not known this time. Says which in a
	/// debug line (the state only - no token, no id).
	/// </summary>
	private Details? WithGuard(Details? page, string service) {
		if (service.Length > 0) {
			Log.Debug(new Said("Steam Guard read from Steam's two-factor service: {0}", GuardWords(service)), Bot.Name);

			return (page ?? new Details("", "", "", "")) with { Guard = service, GuardFrom = "service" };
		}

		if (!State.GuardByApi && (page is { Guard.Length: > 0 })) {
			Log.Debug(new Said("Steam Guard read from the account page: {0}", GuardWords(page.Guard)), Bot.Name);

			return page with { GuardFrom = "page" };
		}

		Log.Debug(new Said("Steam Guard not known this time - Steam's two-factor service didn't answer"), Bot.Name);

		if (page == null) {
			return null;
		}

		Details without = page with { Guard = "", GuardFrom = "" };

		return without.Any ? without : null;
	}

	private static void MarkPage(Saved s, string page, bool ok) => s.Pages[page] = ok ? "ok" : "couldn't read";

	/// <summary>A new warning: the red line, the pop-up, Discord/Telegram, then the account's own reaction.</summary>
	private void Raise(Saved s, List<Hit> found) {
		if (found.Count == 0) {
			return;
		}

		lock (_gate) {
			if (s.Hits.Count == 0) {
				s.OffersCancelled = false;
			}

			s.Hits.AddRange(found);
		}

		GlobalConfig g = Live.Global;

		// One red line per warning - in the window, the console and the log file, and to the chats: which account, what
		// changed, and what nocat.farm did about it. Never the key, the address or anything else secret: only the words.
		foreach (Hit h in found) {
			Log.Alarm(Line(Bot.Name, h, Bot.Cfg), Bot.Name, Topic.ScamWatch, g.ScamAlertPopup);
		}
	}

	/// <summary>The warning's red line: the account, what changed, what was done, and what to do.</summary>
	public static Said Line(string account, Hit h, BotConfig cfg) =>
		new Said("SCAM WATCH on {0}: {1} - {2}. If that wasn't you, change your Steam password now", account, What(h), Did(account, cfg));

	/// <summary>Steam Guard as read ("phone app", "email", "off") in words.</summary>
	public static Said GuardWords(string guard) => guard switch {
		"phone app" => new Said("the phone app"),
		"email" => new Said("email codes"),
		"off" => new Said("off"),
		_ => new Said("not known")
	};

	/// <summary>The phone as read ("12", "none") in words.</summary>
	public static Said PhoneWords(string phone) => phone switch {
		"" => new Said("not known"),
		"none" => new Said("none"),
		var p => new Said("ends in {0}", p)
	};

	/// <summary>What nocat.farm did about a warning, by the account's settings.</summary>
	public static Said Did(string account, BotConfig cfg) => (cfg.ScamWatchStopTrades, cfg.ScamWatchCancelOffers) switch {
		(true, true) => new Said("trade auto-accept and sending stopped, and its sent offers are being cancelled - type 'scamwatch ok {0}' when you've checked", account),
		(true, false) => new Said("trade auto-accept and sending stopped - type 'scamwatch ok {0}' when you've checked", account),
		(false, true) => new Said("its sent offers are being cancelled, trades carry on (stopping them is off) - type 'scamwatch ok {0}' to clear the warning", account),
		_ => new Said("nothing was stopped (both reactions are off) - type 'scamwatch ok {0}' to clear the warning", account)
	};

	/// <summary>The warning in words.</summary>
	public static Said What(Hit h) => h.Kind switch {
		"apikey" => new Said("a Steam Web API key appeared that wasn't there before"),
		"apikey-changed" => new Said("the Steam Web API key changed"),
		"device" => new Said("a new device was authorized: {0}", h.Detail),
		"email" => new Said("the email address changed (now {0})", h.Detail),
		"phone" => new Said("the phone number changed (now {0})", PhoneWords(h.Detail)),
		"guard" => new Said("Steam Guard changed (now {0})", GuardWords(h.Detail)),
		"sessions" => new Said("every session on the account was signed out"),
		_ => new Said("{0}", h.Kind)
	};

	/// <summary>What a warning does to the account, once it stands: its own sent offers taken back, when that's on.</summary>
	private async Task AfterHitAsync(Saved s, CancellationToken ct) {
		if (!Bot.Cfg.ScamWatchCancelOffers || s.OffersCancelled || !Ready) {
			return;
		}

		bool? done = await (CancelSent ?? CancelSentAsync)(ct).ConfigureAwait(false);

		if (done == true) {
			lock (_gate) {
				s.OffersCancelled = true;
			}

			Save();
		}
	}

	/// <summary>The word 'trade cancel' takes for every sent offer - a command word, not a sentence.</summary>
	private const string EveryOffer = "all";

	/// <summary>Takes back every offer this account sent that hasn't gone through. Null when Steam didn't answer (tried again next look).</summary>
	private async Task<bool?> CancelSentAsync(CancellationToken ct) {
		if (BotManager.ModuleOf<Trading>(Bot) is not { } trading) {
			return true;
		}

		if (await TradeOffers.ActiveAsync(Bot, received: false, sent: true, ct).ConfigureAwait(false) is not { } live) {
			return null;
		}

		if (!live.Any(static o => o.Ours && (o.State is TradeOffers.Active or TradeOffers.NeedsConfirmation))) {
			return true;
		}

		// Every one it sent - the same as typing 'trade cancel <account> all'.
		string said = await trading.CancelSentAsync(EveryOffer, ct).ConfigureAwait(false);
		Log.Alarm(new Said("SCAM WATCH on {0}: took back its sent offers - {1}", Bot.Name, said.ReplaceLineEndings("; ")), Bot.Name, Topic.ScamWatch, popup: false);

		return true;
	}

	/// <summary>Presses Revoke on the key page, then reads it again to be sure it's gone.</summary>
	private async Task RevokeKeyAsync(Saved s, CancellationToken ct) {
		Dictionary<string, string> form = new() { ["sessionid"] = Bot.Web.SessionId, ["Revoke"] = "Revoke" };

		if (Post != null) {
			await Post(RevokeKeyUrl, form, ApiKeyPage, ct).ConfigureAwait(false);
		} else {
			await Bot.Web.PostAsync(RevokeKeyUrl, form, ApiKeyPage, ct).ConfigureAwait(false);
		}

		KeyRead after = ParseApiKey(await GetAsync(ApiKeyPage, ct).ConfigureAwait(false));

		if (after.State == KeyState.None) {
			s.KeyFingerprint = "";
			Log.Alarm(new Said("SCAM WATCH on {0}: revoked the new API key", Bot.Name), Bot.Name, Topic.ScamWatch, popup: false);
		} else {
			Log.Alarm(new Said("SCAM WATCH on {0}: couldn't revoke the API key - revoke it yourself at {1}", Bot.Name, ApiKeyPage), Bot.Name, Topic.ScamWatch, Live.Global.ScamAlertPopup);
		}
	}

	/// <summary>
	/// The saved login was refused (Bot.cs): when it's because every session was signed out - the token revoked, not a
	/// reconnect or you playing - that's a warning. The account then needs its password again anyway.
	/// </summary>
	public static void SavedLoginRefused(Bot bot, EResult result, DateTime? tokenExpires) {
		if (!IsRevoked(result, tokenExpires, DateTime.UtcNow) || !bot.Cfg.WatchForScams || !bot.Cfg.ScamWatchSessions || (BotManager.ModuleOf<ScamWatch>(bot) is not { } watch)) {
			return;
		}

		watch.Revoked();
	}

	/// <summary>
	/// Whether the saved login was refused because it was revoked - not a hiccup, not "logged in elsewhere", and not simply
	/// old: a saved login lasts about 200 days, and one past its own end date is refused too ("Expired"), which nobody did.
	/// Steam answers a revoked one with any of AccessDenied, InvalidPassword, Revoked, InvalidSignature or Expired.
	/// </summary>
	public static bool IsRevoked(EResult result, DateTime? tokenExpires, DateTime now) {
		if ((tokenExpires is { } end) && (end <= now)) {
			return false;   // it ran out by itself
		}

		return result is EResult.AccessDenied or EResult.InvalidPassword or EResult.Revoked or EResult.InvalidSignature
			|| ((result == EResult.Expired) && (tokenExpires != null));
	}

	internal void Revoked() {
		Saved s = State;
		Raise(s, [new Hit("sessions", "", DateTime.UtcNow.Ticks)]);
		Save();
	}

	/// <summary>'scamwatch ok &lt;account&gt;': the warnings were you - clear them, and trades go back to normal.</summary>
	public int Clear() {
		int had;

		lock (_gate) {
			had = State.Hits.Count;
			State.Hits.Clear();
			State.OffersCancelled = false;
		}

		Save();

		if (had > 0) {
			Log.Info(new Said("scam watch: warnings cleared - trades back to normal"), Bot.Name);

			// The trade loop slept long while held (nothing to do) - it looks again now, not up to an hour and a half later.
			Bot.WakeTradeLoop();

			// Cards the hold kept back at the end of farming go now, if the account is still running.
			BotManager.ModuleOf<CardFarmer>(Bot)?.HoldLifted();
		}

		return had;
	}

	// ── the rules, each on its own so the tests can prove it ──

	/// <summary>"apikey" for a key that appeared, "apikey-changed" for a different one, null otherwise (first look included).</summary>
	public static string? KeyNews(string? before, KeyRead now) {
		if ((before == null) || (now.State != KeyState.Key)) {
			return null;   // the first look is a baseline; a key going away is not a scam
		}

		return before.Length == 0 ? "apikey" : before != now.Fingerprint ? "apikey-changed" : null;
	}

	/// <summary>Devices on the list now that weren't there at the last look and aren't yours. Nothing on the first look.</summary>
	public static List<Device> NewDevices(IReadOnlyCollection<string>? before, List<Device> now, IReadOnlySet<string> ours) {
		if (before == null) {
			return [];
		}

		HashSet<string> had = [.. before];

		return [.. now.Where(d => !had.Contains(d.Id) && !d.ThisSession && !IsOurs(d.Name, ours))];
	}

	/// <summary>
	/// Whether a device's name is one of yours: nocat.farm's own (this PC's device name - Steam may add a note in brackets
	/// after it, the way SteamKit's own default reads "PC (SteamKit2)") or one you trust.
	/// </summary>
	public static bool IsOurs(string name, IReadOnlySet<string> ours) {
		string n = name.Trim();

		if (ours.Contains(n)) {
			return true;
		}

		int bracket = n.IndexOf(" (", StringComparison.Ordinal);

		return (bracket > 0) && ours.Contains(n[..bracket]);
	}

	/// <summary>
	/// The names nocat.farm's own sign-ins carry - every account's <see cref="Bot.DeviceName"/> (this PC's name unless
	/// "device name" is set), which is what Steam lists them under - plus the trusted names from the setting.
	/// </summary>
	public static HashSet<string> OwnDeviceNames(Bot bot) {
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { bot.DeviceName.Trim(), Environment.MachineName };

		foreach (Bot other in BotManager.Instance?.All ?? []) {
			names.Add(other.DeviceName.Trim());
		}

		foreach (string trusted in Live.Global.ScamWatchTrustedDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			names.Add(trusted);
		}

		names.Remove("");

		return names;
	}

	/// <summary>What changed on the account details page, field by field. Nothing on the first look, nothing for a field not shown.</summary>
	public static List<(string Kind, string Detail)> DetailNews(Details? before, Details now) {
		List<(string, string)> news = [];

		if (before == null) {
			return news;
		}

		static bool Changed(string a, string b) => (a.Length > 0) && (b.Length > 0) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

		if (Changed(before.EmailId, now.EmailId)) {
			news.Add(("email", now.Email));
		}

		if (Changed(before.Phone, now.Phone)) {
			news.Add(("phone", now.Phone));
		}

		if (Changed(before.Guard, now.Guard)) {
			news.Add(("guard", now.Guard));
		}

		return news;
	}

	private static Details Merge(Details? before, Details now) => before == null ? now : new Details(
		now.EmailId.Length > 0 ? now.Email : before.Email,
		now.EmailId.Length > 0 ? now.EmailId : before.EmailId,
		now.Phone.Length > 0 ? now.Phone : before.Phone,
		now.Guard.Length > 0 ? now.Guard : before.Guard,
		now.Guard.Length > 0 ? now.GuardFrom : before.GuardFrom);

	// ── reading the pages ──

	/// <summary>
	/// Steam's two-factor service answer (ITwoFactorService/QueryStatus): "phone app" when the phone authenticator is on
	/// (state 1); otherwise by its Steam Guard scheme - 1 email codes, 0 off (the numbering steamguard-cli prints, and the
	/// one Steam's own authenticator removal falls back to as 1). "" for anything else: no answer, an error page, an empty
	/// response, or one that contradicts itself - unknown, never "off".
	/// </summary>
	public static string ParseTwoFactor(string? json) {
		if (string.IsNullOrWhiteSpace(json)) {
			return "";
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(json);

			if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("response", out JsonElement r) || (r.ValueKind != JsonValueKind.Object)) {
				return "";
			}

			uint? state = Number(r, "state");
			uint? scheme = Number(r, "steamguard_scheme");

			if (state == 1) {
				return "phone app";
			}

			if ((state is not null and not 0) || (scheme == null)) {
				return "";
			}

			return scheme switch {
				0 => "off",
				1 => "email",
				_ => ""   // the phone app's scheme with no authenticator on: contradicts itself
			};
		} catch (JsonException) {
			return "";
		}

		static uint? Number(JsonElement e, string name) =>
			!e.TryGetProperty(name, out JsonElement v) ? null
			: (v.ValueKind == JsonValueKind.Number) && v.TryGetUInt32(out uint n) ? n
			: (v.ValueKind == JsonValueKind.String) && uint.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint m) ? m
			: null;
	}

	/// <summary>A fingerprint of a key: enough to tell one key from another, useless for anything else.</summary>
	internal static string Fingerprint(string secret) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("nocat.farm scam watch|" + secret)))[..16];

	/// <summary>
	/// The Web API key page. Unknown when it isn't the key page at all (a sign-in page, an error): that says nothing.
	/// </summary>
	public static KeyRead ParseApiKey(string? html) {
		if (string.IsNullOrEmpty(html) || !(html.Contains("bodyContents_ex", StringComparison.Ordinal) || html.Contains("mainContents", StringComparison.Ordinal))) {
			return new KeyRead(KeyState.Unknown);
		}

		string text = Html.Text(html);

		if (KeyRegex().Match(text) is { Success: true } m) {
			return new KeyRead(KeyState.Key, Fingerprint(m.Groups[1].Value.ToUpperInvariant()));
		}

		if (text.Contains("Registering for a Steam Web API Key", StringComparison.OrdinalIgnoreCase)) {
			return new KeyRead(KeyState.None);
		}

		// No key and none allowed: no validated email, or Steam wants the phone authenticator first.
		if (text.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) || text.Contains("Validated email address required", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("requires Steam Guard Mobile Authenticator", StringComparison.OrdinalIgnoreCase)) {
			return new KeyRead(KeyState.Denied);
		}

		return new KeyRead(KeyState.Unknown);
	}

	[GeneratedRegex(@"Key:\s*([0-9A-Fa-f]{32})\b")]
	private static partial Regex KeyRegex();

	/// <summary>
	/// The authorized-devices page: Steam puts the list in the page as JSON, in an attribute of the page's settings block.
	/// Null when there's no list at all (a sign-in page) - that says nothing either way.
	/// </summary>
	public static List<Device>? ParseDevices(string? html) {
		if (string.IsNullOrEmpty(html) || (DevicesRegex().Match(html) is not { Success: true } m)) {
			return null;
		}

		// The token reading the page - nocat.farm's own session. Its id is said beside the list.
		string requesting = RequestingRegex().Match(html) is { Success: true } r ? WebUtility.HtmlDecode(r.Groups[1].Value).Trim('"') : "";

		try {
			using JsonDocument doc = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));

			if (doc.RootElement.ValueKind != JsonValueKind.Array) {
				return null;
			}

			List<Device> devices = [];

			foreach (JsonElement d in doc.RootElement.EnumerateArray()) {
				string id = Field(d, "token_id");

				if (id.Length == 0) {
					continue;
				}

				string where = "";

				if (d.TryGetProperty("first_seen", out JsonElement seen) && (seen.ValueKind == JsonValueKind.Object)) {
					where = string.Join(", ", new[] { Field(seen, "city"), Field(seen, "state"), Field(seen, "country") }.Where(static p => p.Length > 0));
				}

				devices.Add(new Device(Fingerprint("device|" + id), Field(d, "token_description"), where, (requesting.Length > 0) && (id == requesting)));
			}

			return devices;
		} catch (JsonException) {
			return null;
		}
	}

	private static string Field(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) ? v.ValueKind switch {
			JsonValueKind.String => v.GetString() ?? "",
			JsonValueKind.Number => v.GetRawText(),
			_ => ""
		} : "";

	[GeneratedRegex(@"data-active_devices=""([^""]*)""")]
	private static partial Regex DevicesRegex();

	[GeneratedRegex(@"data-requesting_token_id=""([^""]*)""")]
	private static partial Regex RequestingRegex();

	/// <summary>
	/// The account details page (store.steampowered.com/account): the email, the phone ending and Steam Guard. Null when it
	/// isn't that page (a sign-in page, an error). Steam Guard is read from the shield picture Steam draws for it - green for
	/// the phone app, amber for email codes, red for none - which reads the same in every language; the words are the
	/// fallback.
	/// </summary>
	public static Details? ParseDetails(string? html) {
		if (string.IsNullOrEmpty(html) || !html.Contains("account_setting", StringComparison.Ordinal)) {
			return null;
		}

		string text = Html.Text(html);
		// The address in one of the page's own data fields - not any address on the page (a help address in the footer).
		string address = DataFieldRegex().Matches(html).Select(static m => Html.Text(m.Groups[1].Value)).FirstOrDefault(static v => EmailRegex().IsMatch(v)) is { } field
			? EmailRegex().Match(field).Value
			: "";
		(string email, string emailId) = address.Length > 0 ? (Mask(address), Fingerprint("email|" + address.ToLowerInvariant())) : ("", "");
		string phone = "";

		if (Html.Between(html, "phone_header_description", "</div>") is { } phoneBlock && PhoneRegex().Match(Html.Text(">" + phoneBlock)) is { Success: true } p) {
			phone = p.Groups[1].Value;
		} else if (html.Contains("/phone/add", StringComparison.Ordinal)) {
			phone = "none";
		} else if (EndsInRegex().Match(text) is { Success: true } ends) {
			phone = ends.Groups[1].Value;
		}

		string guard = html.Contains("sg_good", StringComparison.Ordinal) ? "phone app"
			: html.Contains("sg_fair", StringComparison.Ordinal) ? "email"
			: html.Contains("sg_poor", StringComparison.Ordinal) ? "off"
			: text.Contains("Mobile Authenticator", StringComparison.OrdinalIgnoreCase) ? "phone app"
			: text.Contains("Steam Guard is not enabled", StringComparison.OrdinalIgnoreCase) || text.Contains("Not protected by Steam Guard", StringComparison.OrdinalIgnoreCase) ? "off"
			: text.Contains("Protected by Steam Guard", StringComparison.OrdinalIgnoreCase) ? "email"
			: "";
		Details d = new(email, emailId, phone, guard);

		return d.Any ? d : null;
	}

	/// <summary>An email address hidden the way Steam hides one: the first two letters, stars, then the domain. Already hidden stays as is.</summary>
	public static string Mask(string email) {
		int at = email.IndexOf('@');

		if ((at <= 0) || email.Contains('*', StringComparison.Ordinal)) {
			return email.ToLowerInvariant();
		}

		return (email[..Math.Min(2, at)] + "***" + email[at..]).ToLowerInvariant();
	}

	/// <summary>An email address, whole or hidden with stars, as it shows on the page.</summary>
	[GeneratedRegex(@"[A-Za-z0-9._%+*-]+@[A-Za-z0-9*.-]+\.[A-Za-z]{2,}")]
	private static partial Regex EmailRegex();

	[GeneratedRegex(@"class=""account_data_field""[^>]*>([^<]*)<")]
	private static partial Regex DataFieldRegex();

	/// <summary>The last digits of the phone number in its block - Steam shows only the end of it.</summary>
	[GeneratedRegex(@"(\d{2,4})\s*$")]
	private static partial Regex PhoneRegex();

	[GeneratedRegex(@"Ends\s+in\s+(\d{2,4})", RegexOptions.IgnoreCase)]
	private static partial Regex EndsInRegex();

	// ── saving ──

	private Saved? Load() {
		try {
			return File.Exists(StatePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(StatePath)) : null;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the saved scam watch: {0}", Log.Describe(e)), Bot.Name);

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
			Log.Debug(new Said("couldn't save the scam watch: {0}", Log.Describe(e)), Bot.Name);
		}
	}
}
