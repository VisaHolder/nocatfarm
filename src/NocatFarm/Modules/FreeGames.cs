using System.Globalization;
using NocatFarm.Core;

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

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

	private readonly HashSet<uint> _seen = [];
	private readonly List<DateTime> _claims = [];
	private string _status = "off";
	private int _claimed;

	public override string Name => "free games";
	public override string Status => _status;

	public int ClaimedThisRun => _claimed;

	protected override async Task RunAsync(CancellationToken ct) {
		// Stays alive when switched off, so turning it on doesn't need a restart.
		while (!ct.IsCancellationRequested) {
			if (!Bot.Cfg.ClaimFreeGames) {
				_status = "off";

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (Bot.Paused) {
				_status = "paused";

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (!Bot.IsOnline || !Bot.Web.Ready) {
				_status = "waiting for the account";

				if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			try {
				int added = await CheckAsync(ct).ConfigureAwait(false);

				_status = _claimed == 0 ? "watching for giveaways" : $"{_claimed} claimed since start";

				if (added > 0) {
					Log.Reward($"claimed {added} free game(s) - the card farmer will pick them up", Bot.Name);
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Warn($"free-game check failed: {e.GetType().Name}: {e.Message}", Bot.Name);
			}

			if (!await Sleep(Rng.Minutes(PollLowMinutes, PollHighMinutes), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>One pass over the feed. Returns how many licences were actually added.</summary>
	public async Task<int> CheckAsync(CancellationToken ct) {
		string? feed = await GetAsync(FeedUrl, ct).ConfigureAwait(false);

		if (feed == null) {
			return 0;
		}

		int added = 0;

		foreach (string line in feed.Split('\n')) {
			ct.ThrowIfCancellationRequested();

			string token = line.Trim();

			// Packages only. An "a/" entry is a permanently free-to-play app: no farmable cards, no profile
			// presence until played - library noise with no income.
			if (!token.StartsWith("s/", StringComparison.Ordinal)) {
				continue;
			}

			if (!uint.TryParse(token.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out uint subId) || (subId == 0)) {
				continue;
			}

			// Only skip what we have genuinely already decided about. Committing to _seen BEFORE the claim meant
			// a package that failed once (rate limit, network blip) was never looked at again this run.
			if (_seen.Contains(subId) || Bot.OwnsPackage(subId)) {
				continue;
			}

			if (RecentClaims() >= MaxPerWindow) {
				_status = $"paused - {MaxPerWindow} activations this window";
				Log.Info($"hit {MaxPerWindow} activations in {WindowMinutes}m - pausing so Steam doesn't start refusing", Bot.Name);

				break;
			}

			(bool worth, string name) = await WorthwhileAsync(subId, ct).ConfigureAwait(false);

			if (!worth) {
				_seen.Add(subId);   // a permanent "no" - DLC, demo, unreleased

				continue;
			}

			_claims.Add(DateTime.UtcNow);
			bool ok = await ClaimAsync(subId, ct).ConfigureAwait(false);

			if (ok) {
				_seen.Add(subId);
				added++;
				_claimed++;
				Log.Reward($"claimed {name}", Bot.Name);
			} else {
				Log.Debug($"couldn't claim {name} (sub {subId})", Bot.Name);
			}

			await Sleep(Rng.Seconds(ClaimGapLowSeconds, ClaimGapHighSeconds), ct).ConfigureAwait(false);
		}

		return added;
	}

	/// <summary>
	/// A free-to-keep promo of a normally-paid game is worth taking whether or not it has cards. So the only
	/// things rejected are the ones that aren't a game you can actually play: DLC (useless without the base
	/// game), demos (Steam mass-removes those), and unreleased titles.
	/// </summary>
	private async Task<(bool Worth, string Name)> WorthwhileAsync(uint subId, CancellationToken ct) {
		string name = "sub " + subId.ToString(CultureInfo.InvariantCulture);
		string? pkg = await GetAsync($"https://store.steampowered.com/api/packagedetails?packageids={subId}", ct).ConfigureAwait(false);

		if (pkg == null) {
			return (false, name);
		}

		name = Json.Str(pkg, "name") ?? name;
		uint appId = FirstAppId(pkg);

		if (appId == 0) {
			return (false, name);
		}

		string? details = await GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", ct).ConfigureAwait(false);

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

	/// <summary>
	/// Activate a package through the store, then confirm it by watching for the licence to actually arrive -
	/// the endpoint answers cheerfully whether or not anything was added.
	/// </summary>
	private async Task<bool> ClaimAsync(uint subId, CancellationToken ct) {
		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["ajax"] = "true",
			["subid"] = subId.ToString(CultureInfo.InvariantCulture)
			// sessionid is injected by WebSession
		};

		string? body = await Bot.Web.PostAsync(
			new Uri(WebSession.Store, "/checkout/addfreelicense"),
			form,
			new Uri(WebSession.Store, $"/app/{subId}"),
			ct).ConfigureAwait(false);

		if (body == null) {
			return false;
		}

		// Steam pushes a fresh licence list on success; give it a moment and check for real.
		for (int i = 0; i < 6; i++) {
			if (Bot.OwnsPackage(subId)) {
				return true;
			}

			if (!await Sleep(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) {
				return false;
			}
		}

		return false;
	}

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
