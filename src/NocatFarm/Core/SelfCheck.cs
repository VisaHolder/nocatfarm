namespace NocatFarm.Core;

/// <summary>
/// "Does this account look like a bot?" - scored from what somebody else could actually see: the profile's hours in
/// the past two weeks, the games it shows, its comments, the name in its status. Each tell costs points and comes
/// with the setting that fixes it; how much a tell costs depends on who can see it, so a private game list
/// takes most of the sting out of the hour-based ones.
/// </summary>
public static class SelfCheck {
	/// <summary>One thing that gives the account away: points off, what's seen, and what to change.</summary>
	public sealed record Tell(int Points, string What, string Fix);

	public sealed record Report(int Score, string Verdict, string Visibility, List<Tell> Tells);

	private const int TwoWeeks = 14 * 24 * 60;

	public static async Task<Report> RunAsync(Bot bot, CancellationToken ct = default) {
		Privacy.Settings? privacy = bot.IsOnline && bot.Web.Ready ? await Privacy.ReadAsync(bot, ct).ConfigureAwait(false) : null;

		return Score(bot, privacy);
	}

	/// <summary>
	/// How many games bank the night: the overnight list, or the most-played games human mode picks when it's empty (and an
	/// hour target that needs the night). Without human mode's own answer, just the list.
	/// </summary>
	public static int NightGames(Bot bot) => Core.BotManager.ModuleOf<Modules.HumanMode>(bot)?.NightGameCount ?? bot.Cfg.OfflineIdleGames.Count;

	/// <summary>The scoring itself, separate from the network so it can be tested.</summary>
	internal static Report Score(Bot bot, Privacy.Settings? privacy) {
		List<Tell> tells = [];

		// Who sees the hours: everyone, friends, or nobody. Unknown counts as public - the careful assumption.
		int seen = privacy == null ? 3 : privacy.Profile == 1 ? 1 : Math.Min(privacy.Profile, Math.Min(privacy.Games, privacy.Playtime));
		double hoursWeight = seen switch { 1 => 0.25, 2 => 0.5, _ => 1.0 };
		string visibility = privacy == null ? "couldn't read the privacy settings - scored as if public"
			: seen switch { 1 => "game hours are private - hour tells only count a quarter", 2 => "game hours are friends-only - hour tells count half", _ => "game hours are public" };

		void Add(int points, double weight, string what, string fix) {
			int p = (int) Math.Round(points * weight);

			if (p > 0) {
				tells.Add(new Tell(p, what, fix));
			}
		}

		// ── the two-week hours on the profile ────────────────────────────────
		IReadOnlyDictionary<uint, int> recent = bot.Library.LastTwoWeeks;
		int total = recent.Values.Sum();
		double perDay = total / 14.0 / 60;

		// Where the surplus comes from decides the fix: with human mode on it can only be the overnight games, which all
		// run at once all night - the list, or with the list empty the most-played games picked in its place.
		int nightGames = NightGames(bot);
		bool picked = bot.Cfg.OfflineIdleGames.Count == 0;
		bool nightBank = bot.Cfg.LegitMode && bot.Cfg.OfflineIdleAtNight && (nightGames > 0);
		// What the list (or the top-games number) itself puts on: the advice is about that. nightGames also counts a dated
		// hour target that needs the night, and with one of those and a list of one, it said to cut the list "down to one
		// or two" - which it already was.
		Modules.HumanMode? human = Core.BotManager.ModuleOf<Modules.HumanMode>(bot);
		int listGames = picked ? (human?.NightPicked.Count ?? 0) : (human?.ListedNightCount ?? bot.Cfg.OfflineIdleGames.Count);

		// More a day than today's settings could ever bank (every night game all day, plus the day's one game): the
		// hours are from before human mode, when it idled many games at once. On a robot account switched to human mode
		// this said "cut the night list down to one or two" when it was already one.
		bool fromBefore = bot.Cfg.LegitMode && (perDay > (24.0 * Math.Max(1, nightBank ? nightGames : 0)) + 24);
		string manyFix = fromBefore
			? "those hours are from before human mode, when it idled many games at once - they leave Steam's two-week count by themselves within 14 days"
			: nightBank
				? (listGames > 2
					? picked
						? $"it banks its {listGames} most-played games at once every night - set OfflineIdleTopGames to 1 or 2, or turn OfflineIdleAtNight off"
						: $"it banks {listGames} games at once every night - cut OfflineIdleGames down to one or two, or turn OfflineIdleAtNight off"
					: nightGames > listGames
						? "the overnight games, and the dated hour targets that need the night, bank hours all night - turn OfflineIdleAtNight off, or give the targets in HourTargets later dates"
						: "the overnight games bank hours all night - turn OfflineIdleAtNight off, or lower WeekdayHours / WeekendHours")
				: bot.Cfg.LegitMode ? "lower WeekdayHours / WeekendHours" : "idle fewer games at once, or turn on human mode (one game at a time)";

		if (total > TwoWeeks) {
			Add(35, hoursWeight, $"{total / 60}h across its games in the past two weeks - {perDay:0.#}h a day, more hours than a day has, which only running several games at once does", manyFix);
		} else if (perDay > 16) {
			Add(25, hoursWeight, $"{total / 60}h in the past two weeks - {perDay:0.#}h a day, with no time left to sleep",
				nightBank ? manyFix : bot.Cfg.LegitMode ? "lower WeekdayHours / WeekendHours" : "turn on human mode, or lower its daily hours");
		} else if (perDay > 12) {
			Add(12, hoursWeight, $"{total / 60}h in the past two weeks - {perDay:0.#}h a day, a lot for one person", "lower WeekdayHours / WeekendHours");
		}

		if (recent.Count > 0) {
			KeyValuePair<uint, int> top = recent.MaxBy(static r => r.Value);

			if (top.Value > 14 * 20 * 60) {
				Add(10, hoursWeight, $"{GameNames.Of(top.Key)} alone got {top.Value / 60}h in two weeks - running around the clock", "give it breaks and a bedtime (human mode)");
			}
		}

		// ── what the status shows ────────────────────────────────────────────
		if (!bot.Cfg.LegitMode && !string.IsNullOrWhiteSpace(bot.CustomName)) {
			Add(10, 1, $"friends see it \"playing\" the non-Steam game \"{bot.CustomName}\" whenever it idles - a well-known idler signature",
				"clear CustomGameName, or turn CustomGameNameEnabled off");
		}

		if (bot.PlayingApps.Count > 1) {
			Add(5, 1, $"it's in {bot.PlayingApps.Count} games at once right now - Steam only lets a real client run one", "human mode plays one game at a time");
		}

		if (!bot.Cfg.LegitMode && (bot.PlayingApps.Count > 0 || bot.Cfg.IdleGames.Count > 0)) {
			Add(15, 1, "no human rhythm: it plays for as long as nocat.farm runs - no breaks, no meals, no bedtime", "turn on human mode (LegitMode)");
		}

		if (!bot.Cfg.LegitMode && (bot.Cfg.FarmOffline || bot.Cfg.OnlineStatus is 0 or 7) && (bot.PlayingApps.Count > 0 || bot.Cfg.IdleGames.Count > 0)) {
			Add(5, hoursWeight, "its hours keep climbing while the profile says it's offline", "appear online while it plays, or let human mode handle the status");
		}

		if (nightBank && (total <= 16 * 14 * 60)) {
			Add(4, hoursWeight, "hours still grow overnight while it's \"asleep\" (Bank hours overnight)", "turn OfflineIdleAtNight off for a spotless night");
		}

		// ── the profile ──────────────────────────────────────────────────────
		if (bot.Cfg.Rep4Rep) {
			bool openComments = privacy == null || privacy.Comments == 1;
			Add(8, openComments ? 1 : 0.5, $"strangers leave ~{bot.Cfg.Rep4RepDailyCap} comments a day on its profile, and it comments back the same (rep4rep)",
				"lower Rep4RepDailyCap, or turn rep4rep off for this account");
		}

		// The card-farm footprint: a long tail of games with an hour or three each and nothing since.
		List<Library.Entry> played = [.. bot.Library.Games.Where(static g => !g.Shared && g.MinutesPlayed > 0)];
		int tail = played.Count(g => (g.MinutesPlayed is >= 60 and <= 300) && !recent.ContainsKey(g.AppId));

		if ((played.Count >= 15) && (tail * 2 >= played.Count)) {
			Add(8, hoursWeight, $"{tail} of its {played.Count} played games have 1-5 hours each and nothing since - the classic card-farm footprint",
				"spread some real time over a few of them (GameWeights / HourTargets)");
		}

		int score = Math.Clamp(100 - tells.Sum(static t => t.Points), 0, 100);
		string verdict = score switch {
			>= 85 => "looks like a person",
			>= 65 => "a few tells",
			>= 40 => "reads as an idler",
			_ => "obvious bot"
		};

		return new Report(score, verdict, visibility, [.. tells.OrderByDescending(static t => t.Points)]);
	}
}
