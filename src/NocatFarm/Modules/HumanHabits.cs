using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// How the owner plays on the account himself, kept for "Learn from how I play".
///
/// Steam tells us when another session on this account starts a game (that is what makes the account stand down), and
/// which game. Every time that happens this notes when he sat down, which game, and roughly for how long. Over a few
/// weeks that is a picture of his real routine - when he gets on, when he stops, what he plays - and a routine copied
/// from the person who owns the account is the most believable one there is.
///
/// Only WHEN and WHAT are learned. How long a day is still comes from the hours settings: a week of his late nights
/// shouldn't turn into ten-hour days.
/// </summary>
public sealed class OwnerHabits {
	/// <summary>One stretch of him playing: when it started (local clock), how long, and the game (0 when Steam didn't say).</summary>
	public sealed class Sitting {
		public DateTime Start { get; set; }
		public int Minutes { get; set; }
		public uint App { get; set; }
	}

	public List<Sitting> Sittings { get; set; } = [];

	/// <summary>Older than this is forgotten - a routine from last season isn't his routine now.</summary>
	internal const int KeepDays = 60;

	/// <summary>A week of days seen before any of it is used. Three days is a mood, not a routine.</summary>
	internal const int DaysNeeded = 7;

	/// <summary>
	/// Shorter than this isn't counted. Right after a restart Steam's first word can still describe the session that just
	/// ended - our own - for a moment, and a few minutes of that isn't him.
	/// </summary>
	internal const int ShortestCounted = 10;

	/// <summary>A "day" of his runs from 5am to 5am, so a game at 1am belongs to the evening before, the way he'd see it.</summary>
	internal const int DayStartsAtHour = 5;

	private readonly Lock _gate = new();

	/// <summary>Goes up with every change, so what's worked out from the list can be kept until it changes.</summary>
	internal int Version { get; private set; }

	internal static DateTime DayOf(DateTime local) => local.AddHours(-DayStartsAtHour).Date;

	private List<Sitting> Snapshot() {
		lock (_gate) {
			return [.. Sittings];
		}
	}

	/// <summary>How many different days he's been seen playing.</summary>
	public int DaysSeen => Snapshot().Select(static s => DayOf(s.Start)).Distinct().Count();

	/// <summary>Enough seen to go on.</summary>
	public bool Ready => DaysSeen >= DaysNeeded;

	/// <summary>
	/// Each day seen: when he first sat down and when he last got up, in minutes from that day's midnight - past 1440 is
	/// after midnight, the small hours of the same evening.
	/// </summary>
	public List<(int Start, int End)> Days() => [.. Snapshot()
		.GroupBy(static s => DayOf(s.Start))
		.OrderBy(static g => g.Key)
		.Select(static g => ((int) (g.Min(static s => s.Start) - g.Key).TotalMinutes, (int) g.Max(s => (s.Start.AddMinutes(s.Minutes) - g.Key).TotalMinutes)))];

	/// <summary>One of his days at random - the pull on a day is toward a real day of his, not an average nobody ever had.</summary>
	internal (int Start, int End)? SampleDay(Random rng) {
		List<(int Start, int End)> days = Days();

		return days.Count == 0 ? null : days[rng.Next(days.Count)];
	}

	/// <summary>Minutes on each game (the ones Steam named).</summary>
	public Dictionary<uint, int> MinutesByGame() => Snapshot().Where(static s => s.App != 0).GroupBy(static s => s.App)
		.ToDictionary(static g => g.Key, static g => g.Sum(static s => s.Minutes));

	/// <summary>For each hour of the clock, the share of his days he was playing in it.</summary>
	public double[] HourShare() {
		List<Sitting> all = Snapshot();
		HashSet<(DateTime Day, int Hour)> on = [];

		foreach (Sitting s in all) {
			DateTime end = s.Start.AddMinutes(s.Minutes);

			for (DateTime t = s.Start; t < end; t = t.AddMinutes(15)) {
				on.Add((DayOf(t), t.Hour));
			}
		}

		int days = Math.Max(1, all.Select(static s => DayOf(s.Start)).Distinct().Count());
		double[] share = new double[24];

		foreach ((DateTime _, int hour) in on) {
			share[hour] += 1.0 / days;
		}

		return share;
	}

	/// <summary>
	/// The hours he's usually on as a reader would say them - "19:00-01:00" - from the hours he plays on at least this share
	/// of his days. Empty when there are none.
	/// </summary>
	internal static string HoursText(double[] share, double atLeast = 0.4) {
		bool[] on = [.. share.Select(s => s >= atLeast)];

		if (on.All(static o => o)) {
			return "all day";
		}

		List<string> blocks = [];

		for (int h = 0; h < 24; h++) {
			if (!on[h] || on[(h + 23) % 24]) {
				continue;   // not the first hour of a block
			}

			int end = h;

			while (on[(end + 1) % 24]) {
				end++;
			}

			blocks.Add($"{h:00}:00-{(end + 1) % 24:00}:00");
		}

		return string.Join(", ", blocks);
	}

	/// <summary>The middle of a list - what a "usual" time is, without one odd night dragging it about the way an average would.</summary>
	internal static int Median(IEnumerable<int> values) {
		List<int> sorted = [.. values.Order()];

		return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
	}

	/// <summary>Minutes from midnight as a clock time - 1530 (past midnight) reads 01:30.</summary>
	internal static string Clock(int minutes) {
		int m = ((minutes % 1440) + 1440) % 1440;

		return $"{m / 60:00}:{m % 60:00}";
	}

	/// <summary>Keep a sitting, and let go of anything older than <see cref="KeepDays"/>.</summary>
	public void Add(Sitting sitting, DateTime now) {
		lock (_gate) {
			Sittings.Add(sitting);
			Prune(now);
			Version++;
		}
	}

	private void Prune(DateTime now) {
		DateTime cutoff = now.AddDays(-KeepDays);
		Sittings.RemoveAll(s => (s == null) || (s.Start < cutoff) || (s.Minutes <= 0) || (s.Minutes > 24 * 60) || (s.Start > now.AddDays(1)));

		// A hard cap too, so a file can never grow without end whatever lands in it.
		if (Sittings.Count > 3000) {
			Sittings.RemoveRange(0, Sittings.Count - 3000);
		}
	}

	private static string PathFor(string bot) => Path.Combine(ConfigStore.ConfigDir, "state", $"owner-{bot}.json");

	/// <summary>What's been learned so far. A missing or broken file is simply nothing learned yet - never a crash.</summary>
	public static OwnerHabits Load(string bot) {
		try {
			string path = PathFor(bot);

			if (!File.Exists(path)) {
				return new OwnerHabits();
			}

			OwnerHabits habits = JsonSerializer.Deserialize<OwnerHabits>(File.ReadAllText(path)) ?? new OwnerHabits();
			habits.Sittings ??= [];
			habits.Prune(DateTime.Now);

			return habits;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read what it learned from your play: {0}", Log.Describe(e)), bot);

			return new OwnerHabits();
		}
	}

	public void Save(string bot) {
		try {
			string path = PathFor(bot);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			string json;

			lock (_gate) {
				json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
			}

			AtomicFile.Write(path, json);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save what it learned from your play: {0}", Log.Describe(e)), bot);
		}
	}

	/// <summary>Throw it all away ('habits forget').</summary>
	public static void Forget(string bot) {
		try {
			File.Delete(PathFor(bot));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't forget what it learned from your play: {0}", Log.Describe(e)), bot);
		}
	}
}

/// <summary>
/// Turns "Steam says somebody else is playing on this account" - read every tick - into finished sittings.
/// </summary>
internal sealed class OwnerWatch {
	/// <summary>No word for this long (the PC asleep, nocat.farm closed) and the sitting ends where it was last seen.</summary>
	internal static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(30);

	private DateTime? _since;
	private uint _app;
	private DateTime _lastSeen;

	internal bool Watching => _since != null;

	internal void Reset() {
		_since = null;
		_app = 0;
	}

	/// <summary>One look: is he on, and on what. Hands back any sitting that just finished (long enough to count).</summary>
	internal List<OwnerHabits.Sitting> Observe(DateTime now, bool on, uint app) {
		List<OwnerHabits.Sitting> done = [];

		if ((_since is { } open) && (now - _lastSeen > LostAfter)) {
			Close(open, _lastSeen, done);
		}

		if (on) {
			if (_since == null) {
				_since = now;
				_app = app;
			} else if ((app != 0) && (_app != 0) && (app != _app)) {
				// Changed game: that's two sittings, each on its own game.
				Close(_since.Value, now, done);
				_since = now;
				_app = app;
			} else if (_app == 0) {
				_app = app;
			}

			_lastSeen = now;
		} else if (_since is { } started) {
			Close(started, now, done);
		}

		return done;
	}

	private void Close(DateTime from, DateTime to, List<OwnerHabits.Sitting> done) {
		int minutes = (int) (to - from).TotalMinutes;

		if (minutes >= OwnerHabits.ShortestCounted) {
			done.Add(new OwnerHabits.Sitting { Start = from, Minutes = minutes, App = _app });
		}

		_since = null;
		_app = 0;
	}
}

/// <summary>A day's longer rhythm: part of a quiet spell (shorter days, by how much), or a late night.</summary>
internal readonly record struct DayRhythm(bool Quiet, int QuietPct, bool LateNight, int LateMinutes);

/// <summary>What a day's roll takes on top of the settings: its rhythm, and his habits with how hard they pull.</summary>
internal sealed record DayExtras(DayRhythm Rhythm, OwnerHabits? Habits, double Pull);

/// <summary>A game that just arrived, being tried out: how strongly today, from when, for how many days.</summary>
internal readonly record struct Trial(uint App, double Strength, DateTime Start, int Days);

/// <summary>
/// The sums behind the four "life" extras of human mode - learning from you, new games, quiet spells and late nights,
/// and joining a friend. Kept apart from the day's loop so each is a plain function that can be checked on its own.
/// </summary>
internal static class HumanHabits {
	/// <summary>"Learn from how I play": how far toward his pattern - a little, or a lot. Never all the way.</summary>
	internal static double Pull(int setting) => setting switch {
		1 => 0.3,
		2 => 0.7,
		_ => 0
	};

	internal static int Nudge(int value, int toward, double pull) => value + (int) Math.Round((toward - value) * pull);

	/// <summary>
	/// The games list leaned toward how he splits his time between the SAME games. Only rows already there are
	/// reweighted: a game he plays that isn't in the list is never added, so nothing turns up on the account that you
	/// didn't put there yourself ('habits' names those so you can add them). Nothing he played in the list: unchanged.
	/// </summary>
	internal static List<(uint Game, int Weight)> Reweight(List<(uint Game, int Weight)> weights, IReadOnlyDictionary<uint, int> learned, double pull) {
		if ((weights.Count < 2) || (pull <= 0)) {
			return weights;
		}

		double seen = weights.Sum(w => (double) learned.GetValueOrDefault(w.Game));

		if (seen <= 0) {
			return weights;
		}

		List<double> shares = HumanMode.Shares(weights);
		List<(uint Game, int Weight)> leaned = [];

		for (int i = 0; i < weights.Count; i++) {
			double want = ((1 - pull) * shares[i]) + (pull * 100 * learned.GetValueOrDefault(weights[i].Game) / seen);

			// The main game's number IS its share; the side games split the rest by weight, so theirs just keep the proportion.
			leaned.Add((weights[i].Game, i == 0 ? Math.Clamp((int) Math.Round(want), 5, 95) : Math.Max(1, (int) Math.Round(want))));
		}

		return leaned;
	}

	// ── longer rhythms ──
	/// <summary>The chance a quiet spell starts on any given day - with 2-5 days each, one or two spells a month.</summary>
	internal const double QuietStartChance = 0.06;

	/// <summary>How often a Friday or Saturday night turns into a late one, on top of the usual later bed.</summary>
	internal const double LateNightChance = 0.3;

	/// <summary>
	/// A Random that gives the same numbers for the same account, day and purpose every time - so a day decided now, a
	/// restart later and 'human week' last Tuesday all agree. string.GetHashCode changes every run, so this is FNV.
	/// </summary>
	internal static Random Seeded(string account, DateTime day, int salt) {
		uint h = 2166136261;

		void Mix(uint x) {
			for (int i = 0; i < 4; i++) {
				h ^= (x >> (i * 8)) & 0xFF;
				h *= 16777619;
			}
		}

		foreach (char c in account.ToLowerInvariant()) {
			Mix(c);
		}

		Mix((uint) (day.Date - DateTime.UnixEpoch.Date).Days);
		Mix((uint) salt);

		return new Random((int) (h & 0x7FFFFFFF));
	}

	/// <summary>
	/// This day's rhythm for this account. A quiet spell can start on any day; a day is in one when a spell started on it
	/// or on one of the four before it and hasn't run out. Late nights only on a free night (Friday, Saturday), and never
	/// in a quiet spell.
	/// </summary>
	internal static DayRhythm RhythmFor(string account, DateTime date) {
		bool quiet = false;

		for (int back = 0; back < 5; back++) {
			Random start = Seeded(account, date.Date.AddDays(-back), 1);

			if ((start.NextDouble() < QuietStartChance) && (back < start.Next(2, 6))) {
				quiet = true;
			}
		}

		Random day = Seeded(account, date.Date, 2);
		int pct = day.Next(40, 66);
		bool free = date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;
		bool late = !quiet && free && (day.NextDouble() < LateNightChance);
		int lateMinutes = day.Next(60, 151);

		return new DayRhythm(quiet, pct, late, late ? lateMinutes : 0);
	}

	// ── new games ──
	/// <summary>How much of the day's sittings a brand-new game can take at most, on its first day.</summary>
	internal const double TrialPickChance = 0.35;

	/// <summary>Sittings a day given to new games at most - trying something, not abandoning the rest.</summary>
	internal const int TrialSittingsCap = 3;

	/// <summary>New games looked at, at once - the strongest few, not every free thing that arrived this week.</summary>
	internal const int TrialsAtOnce = 3;

	/// <summary>How many days this game is "new" for on this account: 3 to 10, the same every time it's asked.</summary>
	internal static int TrialDays(string account, uint app) => Seeded(account + ":" + app.ToString(CultureInfo.InvariantCulture), DateTime.UnixEpoch, 3).Next(3, 11);

	/// <summary>
	/// When the trying-out starts: the day it arrived - or, for a game refund protection is holding, the day that hold
	/// ends, since it can't be touched before then anyway.
	/// </summary>
	internal static DateTime TrialStart(AppOwnership owned, BotConfig cfg) =>
		cfg.SkipRefundableGames && owned.Refundable(cfg.ProtectGiftedGames) ? owned.Since.AddDays(Math.Max(1, cfg.RefundHoldDays)) : owned.Since;

	/// <summary>How keen on it today: all in on its first day, less each day after, nothing once its days are up.</summary>
	internal static double TrialStrength(DateTime start, DateTime now, int days) {
		if ((now < start) || (days <= 0)) {
			return 0;
		}

		int day = (int) (now - start).TotalDays;

		return day >= days ? 0 : (double) (days - day) / days;
	}

	// ── friends ──
	/// <summary>The chance a sitting goes to what a friend is playing, when there's one to join.</summary>
	internal const double FriendJoinChance = 0.2;

	/// <summary>Friends joined a day at most.</summary>
	internal const int FriendJoinsCap = 2;

	/// <summary>
	/// A friend to join, at random among those in a game it may play. Your own accounts are never a "friend", whatever
	/// they're on.
	/// </summary>
	internal static (string Name, uint App) PickFriendGame(IReadOnlyList<(ulong Id, string Name, uint App)> friends, IReadOnlySet<ulong> ours,
		Func<uint, bool> playable, Random rng) {
		List<(ulong Id, string Name, uint App)> fit = [.. friends.Where(f => (f.App != 0) && !ours.Contains(f.Id) && playable(f.App))];

		if (fit.Count == 0) {
			return default;
		}

		(ulong _, string name, uint app) = fit[rng.Next(fit.Count)];

		return (name, app);
	}
}
