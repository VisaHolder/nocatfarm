using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Keeps an eye on the account's bans - VAC, game bans, a trade (economy) ban, a community ban - and says so straight
/// away when a NEW one appears: a red line in the log, a pop-up, and Discord/Telegram under "Needs you".
/// </summary>
/// <remarks>
/// Read-only: it looks, it never changes anything on the account. The numbers come from the ban lines on the account's
/// community profile ("1 VAC ban on record", "1 game ban on record", "Currently trade banned") - Steam's ban API
/// needs a Web API key, and a user shouldn't need one of those. Which games come from Steam's help site
/// (<see cref="BanGames"/>). What was seen is kept on disk, so a ban the account already had is reported once,
/// calmly, the first time - never again as "new".
/// </remarks>
public sealed partial class BanWatch(Bot bot) : BotModule(bot) {
	/// <summary>What Steam says about the account's bans.</summary>
	public sealed record Bans(int Vac, int Game, bool Community, string Economy, int DaysSinceLast) {
		public bool TradeBanned => Economy is "banned";

		public bool Any => (Vac > 0) || (Game > 0) || Community || (Economy is "banned" or "probation");
	}

	private sealed record Saved(int Vac, int Game, bool Community, string Economy, int DaysSinceLast, long CheckedTicks, List<uint>? Games);

	private Saved? _seen;
	private bool _loaded;

	/// <summary>
	/// The last reading - from disk the first time it's asked for. It used to be read when the module started, which is
	/// only once the account has signed in: an account waiting for its turn to sign in (the last one, with the gap
	/// between logins) or stopped showed no bans on its card at all, though they were saved.
	/// </summary>
	private Saved? Seen {
		get {
			if (!_loaded) {
				_loaded = true;
				_seen ??= Load();
			}

			return _seen;
		}
	}

	public override string Name => "bans";

	// The account card already shows bans in red (the Bans field of the status), so the module row stays quiet.
	public override string Status => "";

	/// <summary>The last reading, for the dashboard and the 'bans' command. Null until the first look.</summary>
	public Bans? Last => Seen is { } s ? new Bans(s.Vac, s.Game, s.Community, s.Economy, s.DaysSinceLast) : null;

	/// <summary>When it last looked.</summary>
	public DateTime? CheckedAt => Seen is { CheckedTicks: > 0 } s ? new DateTime(s.CheckedTicks, DateTimeKind.Utc) : null;

	/// <summary>The games Steam lists this account as banned in, when it could read them.</summary>
	public IReadOnlyList<uint> BannedGames => Seen?.Games ?? [];

	private string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", $"bans-{Bot.Name}.json");

	protected override async Task RunAsync(CancellationToken ct) {
		// Not in the first minutes after signing in, with everything else.
		if (!await Sleep(Rng.Minutes(3, 9), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.Cfg.WatchBans && Bot.IsOnline && Bot.Web.Ready) {
				try {
					await CheckAsync(ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't check for bans: {0}", e.Message), Bot.Name);
				}
			}

			// Every few hours, never on the dot.
			if (!await Sleep(Rng.Minutes(180, 330), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>Look now. Returns the reading, or null when neither way of looking worked.</summary>
	public async Task<Bans?> CheckAsync(CancellationToken ct = default) {
		Bans? now = await FromProfileAsync(ct).ConfigureAwait(false);

		if (now == null) {
			return null;
		}

		List<uint>? games = now.Any ? await BanGames.ReadAsync(Bot, ct).ConfigureAwait(false) : [];
		Saved? before = Seen;
		_seen = new Saved(now.Vac, now.Game, now.Community, now.Economy, now.DaysSinceLast, DateTime.UtcNow.Ticks, games ?? before?.Games);
		Save();

		if (before == null) {
			// The first look: whatever is there was there before nocat.farm was watching - say it once, calmly.
			if (now.Any) {
				Log.Info(new Said("bans on record: {0}", Summary(now)), Bot.Name);
			}
		} else {
			foreach (Said news in News(before, now)) {
				Log.Attention(news, Bot.Name, Topic.Problems);
			}
		}

		if (games is { Count: > 0 }) {
			LeaveOutOfTrades(games);
		}

		return now;
	}

	/// <summary>What changed for the worse since the last look, one line each.</summary>
	private static IEnumerable<Said> News(Saved before, Bans now) {
		if (now.Vac > before.Vac) {
			yield return new Said("NEW VAC ban - {0} on record now (was {1})", now.Vac, before.Vac);
		}

		if (now.Game > before.Game) {
			yield return new Said("NEW game ban - {0} on record now (was {1})", now.Game, before.Game);
		}

		if (now.Community && !before.Community) {
			yield return new Said("NEW community ban - Steam has community banned this account");
		}

		if ((now.Economy != before.Economy) && (now.Economy is "banned" or "probation")) {
			yield return now.Economy == "banned"
				? new Said("NEW trade ban - this account can't trade or use the market")
				: new Said("NEW trade probation - trading is restricted");
		}
	}

	/// <summary>
	/// Games the account is banned in can never trade their items - Steam refuses the whole offer if one is in it. So
	/// they go on the account's "leave out" list, which every send, swap and the inventory value already respect.
	/// Trading cards and other Steam items aren't game items and keep trading.
	/// </summary>
	private void LeaveOutOfTrades(List<uint> games) {
		List<uint> added = [.. games.Where(g => (g != 753) && !Bot.Cfg.InventoryIgnoreGames.Contains(g))];

		if (added.Count == 0) {
			return;
		}

		Bot.Cfg.InventoryIgnoreGames.AddRange(added);
		ConfigStore.SaveBot(Bot.Name, Bot.Cfg);
		Log.Info(new Said("banned in {0} - skipping its items (cards still trade)",
			string.Join(", ", added.Select(GameNames.Of))), Bot.Name);
	}

	// ── the two ways of looking ──

	/// <summary>The ban lines on the account's own community profile.</summary>
	private async Task<Bans?> FromProfileAsync(CancellationToken ct) {
		string? html = await Bot.Web.GetAsync(new Uri($"https://steamcommunity.com/profiles/{Bot.SteamId}/"), ct).ConfigureAwait(false);

		return html == null ? null : ParseProfile(html);
	}

	/// <summary>
	/// The profile page's ban block: "1 VAC ban on record", "Multiple game bans on record", "123 day(s) since last ban",
	/// "Currently trade banned". A profile with none has no ban block at all. Community banned accounts show Steam's
	/// "community banned" notice instead of a profile.
	/// </summary>
	public static Bans? ParseProfile(string html) {
		// Something other than a profile (a login page, an error) says nothing about bans either way.
		bool profile = html.Contains("profile_header", StringComparison.Ordinal) || html.Contains("g_rgProfileData", StringComparison.Ordinal);

		// Only Steam's own notice page counts - never a profile, where "community banned" could be anybody's comment.
		bool community = !profile && html.Contains("community banned", StringComparison.OrdinalIgnoreCase);

		if (!profile && !community) {
			return null;
		}

		string text = html.Contains("profile_ban_status", StringComparison.Ordinal) ? TagRegex().Replace(html, " ") : "";
		int vac = Count(VacRegex().Match(text));
		int game = Count(GameRegex().Match(text));
		Match days = DaysRegex().Match(text);
		string economy = text.Contains("Currently trade banned", StringComparison.OrdinalIgnoreCase) ? "banned"
			: text.Contains("trade probation", StringComparison.OrdinalIgnoreCase) ? "probation" : "none";

		return new Bans(vac, game, community, economy, days.Success ? int.Parse(days.Groups[1].Value) : 0);
	}

	/// <summary>"1" is one, "Multiple" is at least two - Steam stops counting there.</summary>
	private static int Count(Match m) => !m.Success ? 0 : int.TryParse(m.Groups[1].Value, out int n) ? n : 2;

	[GeneratedRegex("<[^>]+>")]
	private static partial Regex TagRegex();

	[GeneratedRegex(@"(\d+|Multiple)\s+VAC\s+bans?\s+on\s+record", RegexOptions.IgnoreCase)]
	private static partial Regex VacRegex();

	[GeneratedRegex(@"(\d+|Multiple)\s+game\s+bans?\s+on\s+record", RegexOptions.IgnoreCase)]
	private static partial Regex GameRegex();

	[GeneratedRegex(@"(\d+)\s+day\(s\)\s+since\s+last\s+ban", RegexOptions.IgnoreCase)]
	private static partial Regex DaysRegex();

	/// <summary>"1 VAC, 1 game ban · 1203 days ago" - empty for a clean account.</summary>
	public static Said Summary(Bans? b) {
		if (b is not { Any: true }) {
			return b == null ? new Said("") : new Said("no bans");
		}

		List<string> parts = [];

		if (b.Vac > 0) {
			parts.Add(new Said("{0} VAC", b.Vac).ToString());
		}

		if (b.Game > 0) {
			parts.Add(new Said("{0} game ban(s)", b.Game).ToString());
		}

		if (b.Community) {
			parts.Add(new Said("community ban").ToString());
		}

		if (b.Economy == "banned") {
			parts.Add(new Said("trade ban").ToString());
		} else if (b.Economy == "probation") {
			parts.Add(new Said("trade probation").ToString());
		}

		return b.DaysSinceLast > 0
			? new Said("{0} · last one {1} day(s) ago", string.Join(", ", parts), b.DaysSinceLast)
			: new Said("{0}", string.Join(", ", parts));
	}

	private Saved? Load() {
		try {
			return File.Exists(StatePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(StatePath)) : null;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the saved ban check: {0}", e.Message), Bot.Name);

			return null;
		}
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			AtomicFile.Write(StatePath, JsonSerializer.Serialize(_seen));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the ban check: {0}", e.Message), Bot.Name);
		}
	}
}
