using System.Globalization;
using System.Text.Json;
using NocatFarm.Core;
using SteamKit2;

namespace NocatFarm.Modules;

/// <summary>
/// Claims genuinely free Steam games as they appear, so the library - and therefore the card-farming income -
/// grows on its own.
///
/// WHAT IT TAKES: packages only ("s/" entries) - limited-time free-to-KEEP promos, i.e. normally-PAID games
/// being given away permanently. Those are worth having whether or not they drop cards: it's a real game for
/// nothing, and it counts on the profile.
/// WHAT IT LEAVES: every "a/" entry (permanently free-to-play apps - no farmable cards, they don't even show
/// in your game count until played), plus DLC, demos, unreleased titles, and anything already owned.
///
/// Source is the ASFinfo gist: plain text, one token per line. Reddit's own feed 403s datacenter IPs; the gist
/// is a GitHub CDN and doesn't.
///
/// Steam allows roughly 30 package activations per 90 minutes. This stays at 20, leaving room for anything you
/// redeem by hand without tripping the limit.
/// </summary>
public sealed class FreeGames(Bot bot) : BotModule(bot) {
	private const string FeedUrl = "https://gist.githubusercontent.com/C4illin/77a4bcb9a9a7a95e5f291badc93ec6cd/raw/Latest%2520Steam%2520Games";
	private const int PollLowMinutes = 55;
	private const int PollHighMinutes = 75;
	private const int ClaimGapLowSeconds = 6;
	private const int ClaimGapHighSeconds = 20;
	private const int MaxPerWindow = 20;
	private const int WindowMinutes = 90;

	private static readonly HttpClient Http = Browser.Anonymous(TimeSpan.FromSeconds(30));

	/// <summary>Feed entries decided for this run - "a/123" and "s/123" are different things with the same number.</summary>
	private readonly HashSet<string> _seen = [];
	private readonly List<DateTime> _claims = [];

	/// <summary>Packages that failed for a reason that might pass, and when each may be asked for again.</summary>
	/// <remarks>
	/// A failed claim used to be forgotten, so it was asked for again on the very next pass - about hourly, for as
	/// long as the package sat in the feed. Backing off 2h, 8h, then a day, and giving up after the fourth,
	/// turns thousands of doomed requests into a handful.
	/// </remarks>
	private readonly Dictionary<string, (int Tries, DateTime NotBefore)> _failed = [];

	/// <summary>What the store said each entry is. Shared by every account - the answer is the same for all of
	/// them, and asking once per account per pass is how the store API gets rate-limited.</summary>
	private static readonly Dictionary<string, (bool Worth, string Name)> Verdicts = new(StringComparer.Ordinal);

	/// <summary>Store lookups, one at a time and a little apart, whichever account is asking.</summary>
	private static readonly SemaphoreSlim StoreGate = new(1, 1);
	private static DateTime _lastStoreCall = DateTime.MinValue;

	/// <summary>Steam said stop. It means it for about an hour, for every package.</summary>
	private DateTime _quietUntil = DateTime.MinValue;

	private static readonly TimeSpan[] Backoff = [TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromHours(24)];
	private Said _status = new("off");
	private int _claimed;

	public override string Name => "free games";
	public override string Status => _status;

	public int ClaimedThisRun => _claimed;

	private string StatePath => Path.Combine(Config.ConfigStore.ConfigDir, "state", $"freegames-{Bot.Name}.json");

	/// <summary>What has been decided, kept across restarts.</summary>
	/// <remarks>
	/// Held only in memory, every restart asked Steam again for every giveaway it had already given up on - and three
	/// accounts doing that at once is enough to trip Steam's free-licence limit, which then blocks a real giveaway
	/// for the next hour. The rate-limit pause itself is kept for the same reason.
	/// </remarks>
	private sealed record SavedState(List<string> Seen, Dictionary<string, SavedFailure> Failed, long QuietUntilTicks);

	private sealed record SavedFailure(int Tries, long NotBeforeTicks);

	private void Load() {
		try {
			if (!File.Exists(StatePath) || (JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath)) is not { } saved)) {
				return;
			}

			_seen.UnionWith(saved.Seen ?? []);

			foreach ((string token, SavedFailure f) in saved.Failed ?? []) {
				_failed[token] = (f.Tries, new DateTime(f.NotBeforeTicks, DateTimeKind.Utc));
			}

			_quietUntil = new DateTime(saved.QuietUntilTicks, DateTimeKind.Utc);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the free-game state: {0}", e.Message), Bot.Name);
		}
	}

	private void Save() {
		try {
			// Change-feed finds are mostly free-to-play packages; don't let a year of them pile up.
			if (_seen.Count > 5000) {
				_seen.RemoveWhere(static t => t.StartsWith("p/", StringComparison.Ordinal));
			}

			SavedState state = new([.. _seen], _failed.ToDictionary(static kv => kv.Key, static kv => new SavedFailure(kv.Value.Tries, kv.Value.NotBefore.Ticks)), _quietUntil.Ticks);
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(state));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the free-game state: {0}", e.Message), Bot.Name);
		}
	}

	protected override async Task RunAsync(CancellationToken ct) {
		Load();

		// Stays alive when switched off, so turning it on doesn't need a restart.
		while (!ct.IsCancellationRequested) {
			if (!Bot.Cfg.ClaimFreeGames) {
				_status = new Said("off");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (Bot.Paused) {
				_status = new Said("paused");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (!Bot.IsOnline || !Bot.Web.Ready) {
				_status = new Said("waiting for the account");

				if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			try {
				await PicsWatch.PollAsync(Bot, ct).ConfigureAwait(false);
				int added = await CheckAsync(true, ct).ConfigureAwait(false);

				_status = _claimed == 0 ? new Said("watching for giveaways") : new Said("{0} claimed since start", _claimed);

				if (added > 0) {
					Log.Reward(new Said("claimed {0} free game(s) - the card farmer will pick them up", added), Bot.Name);
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Warn(new Said("free-game check failed: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
			}

			// Between passes over the list, Steam's own change feed every ten minutes or so - a giveaway found there
			// is claimed straight away rather than when the list next catches up.
			DateTime nextList = DateTime.UtcNow + Rng.Minutes(PollLowMinutes, PollHighMinutes);

			while ((DateTime.UtcNow < nextList) && Bot.Cfg.ClaimFreeGames) {
				TimeSpan left = nextList - DateTime.UtcNow;

				if (!await Sleep(left < TimeSpan.FromMinutes(12) ? left : Rng.Minutes(9, 12), ct).ConfigureAwait(false)) {
					return;
				}

				if (!Bot.IsOnline || !Bot.Web.Ready || Bot.Paused) {
					continue;
				}

				try {
					await PicsWatch.PollAsync(Bot, ct).ConfigureAwait(false);
					int added = await CheckAsync(false, ct).ConfigureAwait(false);

					if (added > 0) {
						Log.Reward(new Said("claimed {0} free game(s) - the card farmer will pick them up", added), Bot.Name);
					}
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("free-game check failed: {0}: {1}", e.GetType().Name, e.Message), Bot.Name);
				}
			}
		}
	}

	/// <summary>
	/// One pass over the giveaway list (when <paramref name="readList"/>) and whatever Steam's change feed has
	/// turned up. Returns how many licences were actually added.
	/// </summary>
	public async Task<int> CheckAsync(bool readList, CancellationToken ct) {
		if (DateTime.UtcNow < _quietUntil) {
			return 0;
		}

		List<string> tokens = [];

		if (readList && (await GetAsync(FeedUrl, ct).ConfigureAwait(false) is { } feed)) {
			tokens.AddRange(feed.Split('\n'));
		}

		// "p/" is a package the change feed found free. Its first app is what the store is asked about.
		Dictionary<uint, uint> picsApp = [];

		foreach (PicsWatch.Candidate c in PicsWatch.Candidates) {
			picsApp[c.SubId] = c.AppId;
			tokens.Add("p/" + c.SubId.ToString(CultureInfo.InvariantCulture));
		}

		int added = 0;

		foreach (string line in tokens) {
			ct.ThrowIfCancellationRequested();

			string token = line.Trim();

			// Both kinds. Packages ("s/") are claimed through the store; apps ("a/") over the Steam connection.
			// Apps used to be skipped outright on the theory that they are all permanently free-to-play, and most
			// are - but paid games given away turn up as apps too. A check of the live feed found three of them,
			// promos already over, that were never so much as looked at. The store says which is which.
			bool app = token.StartsWith("a/", StringComparison.Ordinal);
			bool pics = token.StartsWith("p/", StringComparison.Ordinal);

			if ((!app && !pics && !token.StartsWith("s/", StringComparison.Ordinal))
				|| !uint.TryParse(token.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || (id == 0)) {
				continue;
			}

			// Only skip what we have genuinely already decided about. Committing to _seen BEFORE the claim meant
			// a package that failed once (rate limit, network blip) was never looked at again this run. A game
			// borrowed through family sharing is not owned - taking it for real is exactly the point.
			bool owned = app ? Bot.Library.Find(id) is { SharedFrom: 0 }
				: Bot.OwnsPackage(id) || (pics && Bot.Library.Find(picsApp[id]) is { SharedFrom: 0 });

			if (_seen.Contains(token) || owned) {
				continue;
			}

			if (_failed.TryGetValue(token, out (int Tries, DateTime NotBefore) earlier) && (DateTime.UtcNow < earlier.NotBefore)) {
				continue;
			}

			if (RecentClaims() >= MaxPerWindow) {
				_status = new Said("paused - {0} activations this window", MaxPerWindow);
				Log.Info(new Said("hit {0} activations in {1}m - pausing so Steam doesn't start refusing", MaxPerWindow, WindowMinutes), Bot.Name);

				break;
			}

			(bool Worth, string Name) verdict;
			bool known;

			lock (Verdicts) {
				known = Verdicts.TryGetValue(token, out verdict);
			}

			if (!known) {
				// A change-feed find has to be a paid game on a 100% discount: free-on-demand packages are mostly
				// free-to-play games being updated, and a paid game that isn't discounted isn't being given away.
				(bool? told, string called) = app ? await AppWorthwhileAsync(id, false, ct).ConfigureAwait(false)
					: pics ? await AppWorthwhileAsync(picsApp[id], true, ct).ConfigureAwait(false)
					: await WorthwhileAsync(id, ct).ConfigureAwait(false);

				// The store didn't answer. That is not a "no" - a free promo seen during a network blip used to be
				// written off for the rest of the run, and promos don't last. Leave it for the next pass.
				if (told == null) {
					continue;
				}

				verdict = (told.Value, called);

				lock (Verdicts) {
					Verdicts[token] = verdict;
				}
			}

			(bool worth, string name) = verdict;

			if (!worth) {
				_seen.Add(token);   // a permanent "no" - free-to-play, DLC, demo, unreleased

				continue;
			}

			_claims.Add(DateTime.UtcNow);
			ClaimResult result = app
				? await AddAppAsync(Bot, id, ct).ConfigureAwait(false)
				: await AddPackageAsync(Bot, id, ct).ConfigureAwait(false);

			if (result.Added) {
				_seen.Add(token);
				_failed.Remove(token);
				added++;
				_claimed++;
				Log.Reward(new Said("claimed {0}", name), Bot.Name);
			} else if (result.RateLimited) {
				// Pressing on only lengthens it. Steam's own wording is "try again in an hour".
				_quietUntil = DateTime.UtcNow.AddMinutes(Rng.Next(62, 80));
				DateTime back = _quietUntil;
				Log.Info(new Said("Steam is rate-limiting free-game claims - trying again after {0}", (Func<string>) (() => Fmt.Clock(back))), Bot.Name);

				break;
			} else if (result.Final) {
				_seen.Add(token);
				Log.Debug(new Said("won't get {0} - {1}; not asking again", name, result.Reason), Bot.Name);
			} else {
				int tries = (_failed.TryGetValue(token, out (int Tries, DateTime NotBefore) before) ? before.Tries : 0) + 1;

				if (tries > Backoff.Length) {
					_seen.Add(token);
					_failed.Remove(token);
					Log.Debug(new Said("gave up on {0} after {1} tries - {2}", name, tries, result.Reason), Bot.Name);
				} else {
					DateTime next = DateTime.UtcNow + Backoff[tries - 1];
					_failed[token] = (tries, next);
					Log.Debug(new Said("couldn't claim {0} - {1}; trying again after {2}", name, result.Reason, (Func<string>) (() => Fmt.Clock(next))), Bot.Name);
				}
			}

			await Sleep(Rng.Seconds(ClaimGapLowSeconds, ClaimGapHighSeconds), ct).ConfigureAwait(false);
		}

		Save();

		return added;
	}

	/// <summary>
	/// A free-to-keep promo of a normally-paid game is worth taking whether or not it has cards. So the only
	/// things rejected are the ones that aren't a game you can actually play: DLC (useless without the base
	/// game), demos (Steam mass-removes those), and unreleased titles.
	/// </summary>
	/// <returns>Worth null when the store couldn't be asked - which is not the same as a no.</returns>
	private async Task<(bool? Worth, string Name)> WorthwhileAsync(uint subId, CancellationToken ct) {
		string name = "sub " + subId.ToString(CultureInfo.InvariantCulture);
		string? pkg = await StoreAsync($"https://store.steampowered.com/api/packagedetails?packageids={subId}", ct).ConfigureAwait(false);

		if (pkg == null) {
			return (null, name);
		}

		name = Json.Str(pkg, "name") ?? name;
		uint appId = FirstAppId(pkg);

		if (appId == 0) {
			return (false, name);
		}

		string? details = await StoreAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", ct).ConfigureAwait(false);

		if (details == null) {
			return (true, name);   // can't tell - a free paid game is still worth the activation
		}

		name = Json.Str(details, "name") ?? name;
		string? type = Json.Str(details, "type");

		if (type != null && !type.Equals("game", StringComparison.OrdinalIgnoreCase)) {
			return (false, name);
		}

		if (details.Contains("\"coming_soon\":true", StringComparison.OrdinalIgnoreCase)) {
			return (false, name);
		}

		return (true, name);
	}

	/// <summary>
	/// Is this app a game being given away, as opposed to one that is free to play for good?
	/// </summary>
	/// <remarks>
	/// The store marks a permanently free game "is_free". A paid game given away free-to-keep is not "is_free" -
	/// it shows its normal price, discounted 100%. That line is exactly what the setting promises: take the
	/// giveaways, leave the free-to-play shovelware, which is most of the feed. DLC, demos, soundtracks and
	/// unreleased games are left alone the same way they are for packages.
	/// </remarks>
	/// <returns>Worth null when the store couldn't be asked - which is not the same as a no.</returns>
	private async Task<(bool? Worth, string Name)> AppWorthwhileAsync(uint appId, bool mustBeGivenAway, CancellationToken ct) {
		string name = GameNames.Of(appId);
		string? details = await StoreAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic,price_overview", ct).ConfigureAwait(false);

		if (details == null) {
			return (null, name);
		}

		if (!details.Contains("\"success\":true", StringComparison.Ordinal)) {
			return (false, name);   // delisted, or not sold here
		}

		name = Json.Str(details, "name") ?? name;
		GameNames.Learn(appId, name);

		string? type = Json.Str(details, "type");

		if ((type != null) && !type.Equals("game", StringComparison.OrdinalIgnoreCase)) {
			return (false, name);
		}

		if (details.Contains("\"coming_soon\":true", StringComparison.OrdinalIgnoreCase)) {
			return (false, name);
		}

		bool freeToPlay = details.Contains("\"is_free\":true", StringComparison.Ordinal);
		bool givenAway = details.Contains("\"discount_percent\":100", StringComparison.Ordinal);

		return (mustBeGivenAway ? givenAway : !freeToPlay || givenAway, name);
	}

	/// <summary>
	/// Ask Steam for a free app's licence over the connection - the only way an app can be added at all, and the
	/// same request ArchiSteamFarm makes for "a/" entries. Shared with the addlicense command.
	/// </summary>
	public static async Task<ClaimResult> AddAppAsync(Bot bot, uint appId, CancellationToken ct) {
		if (bot.Apps == null) {
			return new ClaimResult(false, EPurchaseResultDetail.Timeout, new Said("not connected to Steam"));
		}

		try {
			SteamApps.FreeLicenseCallback answer = await bot.Apps.RequestFreeLicense(appId).ToTask()
				.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

			if (answer.GrantedApps.Contains(appId) || (answer.GrantedPackages.Count > 0)) {
				return new ClaimResult(true, EPurchaseResultDetail.NoDetail, default);
			}

			// Steam answers OK and grants nothing when the game is not free right now - most often a giveaway
			// that has already ended. Worth a later look, not a permanent no.
			// Rate limiting is its own case: it pauses every claim for the hour, the same as a package refused
			// for it. Filed as an ordinary failure, the next app in the feed was asked for straight away.
			return answer.Result switch {
				EResult.OK => new ClaimResult(false, EPurchaseResultDetail.NoDetail, new Said("not free right now, or already owned")),
				EResult.RateLimitExceeded => new ClaimResult(false, EPurchaseResultDetail.RateLimited, new Said("Steam is rate-limiting free-licence requests")),
				_ => new ClaimResult(false, EPurchaseResultDetail.ContactSupport, new Said("Steam refused it ({0})", answer.Result))
			};
		} catch (TimeoutException) {
			return new ClaimResult(false, EPurchaseResultDetail.Timeout, new Said("no usable answer from Steam"));
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			// A dropped connection faults the job rather than timing it out.
			return new ClaimResult(false, EPurchaseResultDetail.Timeout, new Said("the request failed ({0})", e.GetType().Name));
		}
	}

	/// <summary>One store lookup at a time across every account, at least a second and a half apart.</summary>
	private static async Task<string?> StoreAsync(string url, CancellationToken ct) {
		await StoreGate.WaitAsync(ct).ConfigureAwait(false);

		try {
			TimeSpan since = DateTime.UtcNow - _lastStoreCall;

			if (since < TimeSpan.FromSeconds(1.5)) {
				await Task.Delay(TimeSpan.FromSeconds(1.5) - since, ct).ConfigureAwait(false);
			}

			_lastStoreCall = DateTime.UtcNow;

			return await GetAsync(url, ct).ConfigureAwait(false);
		} finally {
			StoreGate.Release();
		}
	}

	/// <summary>The first appID inside a package, so the store lookup has something to describe.</summary>
	private static uint FirstAppId(string packageJson) {
		int apps = packageJson.IndexOf("\"apps\"", StringComparison.Ordinal);

		if (apps < 0) {
			return 0;
		}

		int idAt = packageJson.IndexOf("\"id\"", apps, StringComparison.Ordinal);

		if (idAt < 0) {
			return 0;
		}

		int p = packageJson.IndexOf(':', idAt) + 1;

		while ((p < packageJson.Length) && !char.IsAsciiDigit(packageJson[p])) {
			p++;
		}

		int start = p;

		while ((p < packageJson.Length) && char.IsAsciiDigit(packageJson[p])) {
			p++;
		}

		return (p > start) && uint.TryParse(packageJson.AsSpan(start, p - start), out uint appId) ? appId : 0;
	}

	/// <summary>What Steam said to one free-licence request.</summary>
	/// <param name="Added">The licence genuinely arrived on the account - checked, not taken on trust.</param>
	/// <param name="Detail">Steam's own verdict, where it gave one.</param>
	/// <param name="Reason">Why it failed, in words, for the log and the addlicense command.</param>
	public readonly record struct ClaimResult(bool Added, EPurchaseResultDetail Detail, Said Reason) {
		/// <summary>A refusal that will be the same next time, so there is no point asking again this run.</summary>
		public bool Final => Detail is EPurchaseResultDetail.InvalidPackage or EPurchaseResultDetail.AlreadyPurchased
			or EPurchaseResultDetail.RestrictedCountry or EPurchaseResultDetail.RegionNotSupported
			or EPurchaseResultDetail.DoesNotOwnRequiredApp or EPurchaseResultDetail.OwnsExcludedApp
			or EPurchaseResultDetail.AccountLocked or EPurchaseResultDetail.AcctIsBlocked
			or EPurchaseResultDetail.AcctNotVerified or EPurchaseResultDetail.InvalidAccount;

		public bool RateLimited => Detail == EPurchaseResultDetail.RateLimited;
	}

	/// <summary>
	/// Add one free package to an account. The `addlicense` command's whole implementation.
	///
	/// Static and taking a bot, because it is the same request whether the watcher found the package or
	/// somebody typed the subID - and duplicating a Steam POST is how the two drift apart.
	/// </summary>
	/// <remarks>
	/// Posted to /freelicense/addfreelicense/{sub}, the endpoint ArchiSteamFarm uses. The old
	/// /checkout/addfreelicense answered HTTP 200 to every request and granted nothing: in one 17-day run the three
	/// accounts asked for the same eight packages about four thousand times between them - a Free Starter Edition
	/// among them - without a single licence arriving, and without a single line saying why, because the reply
	/// was never read. Steam's verdict is in that reply, when it gives one, so it is read now.
	/// </remarks>
	public static async Task<ClaimResult> AddPackageAsync(Bot bot, uint subId, CancellationToken ct) {
		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["ajax"] = "true"
			// sessionid is injected by WebSession
		};

		string? body = await bot.Web.PostAsync(
			new Uri(WebSession.Store, $"/freelicense/addfreelicense/{subId.ToString(CultureInfo.InvariantCulture)}"),
			form,
			new Uri(WebSession.Store, $"/app/{subId}"),
			ct).ConfigureAwait(false);

		if (body == null) {
			return new ClaimResult(false, EPurchaseResultDetail.Timeout, new Said("no usable answer from Steam"));
		}

		// Steam answers with "[]", or {"purchaseresultdetail": n}, or {"error": "..."} - or something else
		// entirely. Only a number is a verdict worth acting on; anything else is decided by whether the licence
		// actually turns up.
		(EPurchaseResultDetail detail, string? error) = ReadVerdict(body);

		if (detail != EPurchaseResultDetail.NoDetail) {
			return new ClaimResult(false, detail, Explain(detail));
		}

		if (error != null) {
			return error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
				? new ClaimResult(false, EPurchaseResultDetail.RateLimited, Explain(EPurchaseResultDetail.RateLimited))
				: new ClaimResult(false, EPurchaseResultDetail.ContactSupport, new Said("Steam said: {0}", error));
		}

		// Steam pushes a fresh licence list on success. Six seconds was not always enough for it to land, and a
		// licence that arrives a moment after we stop looking still gets reported as a refusal.
		for (int i = 0; i < 15; i++) {
			if (bot.OwnsPackage(subId)) {
				return new ClaimResult(true, EPurchaseResultDetail.NoDetail, default);
			}

			try {
				await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}
		}

		Log.Debug(new Said("Steam took the request for sub {0} but no licence arrived - it answered: {1}", subId,
			body.Length > 200 ? body[..200] : body), bot.Name);

		return new ClaimResult(false, EPurchaseResultDetail.NoDetail, new Said("Steam took the request, but no licence arrived"));
	}

	private static (EPurchaseResultDetail Detail, string? Error) ReadVerdict(string body) {
		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return (EPurchaseResultDetail.NoDetail, null);
			}

			if (doc.RootElement.TryGetProperty("purchaseresultdetail", out JsonElement n) && n.TryGetInt32(out int code)) {
				return ((EPurchaseResultDetail) code, null);
			}

			if (doc.RootElement.TryGetProperty("error", out JsonElement e) && (e.ValueKind == JsonValueKind.String)) {
				string? text = e.GetString();

				return (EPurchaseResultDetail.NoDetail, string.IsNullOrWhiteSpace(text) ? null : text);
			}
		} catch (JsonException) {
			// an HTML page, most likely - the licence check below is the judge of it
		}

		return (EPurchaseResultDetail.NoDetail, null);
	}

	private static Said Explain(EPurchaseResultDetail detail) => detail switch {
		EPurchaseResultDetail.AlreadyPurchased => new Said("already owned"),
		EPurchaseResultDetail.InvalidPackage => new Said("not a package Steam will give away"),
		EPurchaseResultDetail.RestrictedCountry or EPurchaseResultDetail.RegionNotSupported => new Said("not available in this account's region"),
		EPurchaseResultDetail.DoesNotOwnRequiredApp => new Said("needs a game this account doesn't own"),
		EPurchaseResultDetail.OwnsExcludedApp => new Said("this account owns something that rules it out"),
		EPurchaseResultDetail.RateLimited => new Said("Steam is rate-limiting free-licence requests"),
		EPurchaseResultDetail.AccountLocked or EPurchaseResultDetail.AcctIsBlocked or EPurchaseResultDetail.AcctNotVerified or EPurchaseResultDetail.InvalidAccount
			=> new Said("Steam won't let this account take licences right now"),
		_ => new Said("Steam refused it ({0})", detail)
	};

	private int RecentClaims() {
		DateTime cutoff = DateTime.UtcNow.AddMinutes(-WindowMinutes);
		_claims.RemoveAll(t => t < cutoff);

		return _claims.Count;
	}

	private static async Task<string?> GetAsync(string url, CancellationToken ct) {
		try {
			using HttpResponseMessage r = await Http.GetAsync(url, ct).ConfigureAwait(false);

			return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch {
			return null;
		}
	}
}

/// <summary>Enough JSON reading for the two Steam store endpoints this needs, without a parser.</summary>
internal static class Json {
	public static string? Str(string json, string key) {
		int at = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);

		if (at < 0) {
			return null;
		}

		int colon = json.IndexOf(':', at);

		if (colon < 0) {
			return null;
		}

		int i = colon + 1;

		while ((i < json.Length) && char.IsWhiteSpace(json[i])) {
			i++;
		}

		if ((i >= json.Length) || (json[i] != '"')) {
			return null;
		}

		i++;
		System.Text.StringBuilder sb = new();

		while (i < json.Length) {
			if (json[i] == '\\' && i + 1 < json.Length) {
				sb.Append(json[i + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', char c => c });
				i += 2;

				continue;
			}

			if (json[i] == '"') {
				return sb.ToString();
			}

			sb.Append(json[i]);
			i++;
		}

		return null;
	}
}
