using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// Day-by-day totals for the dashboard's history charts: per account and per local calendar day, the cards that
/// dropped, the rep4rep comments posted, the minutes a game was running (and which games), and what the inventory
/// was worth when the day ended.
/// </summary>
/// <remarks>
/// Everything else remembers a day or so. Stats keeps raw events, the inventory history a month of hourly points,
/// the daily report a single snapshot - and none of it can answer "how did this week go next to last week". This
/// keeps daily totals for about 400 days, which is still only a few KB a month: one file per month under
/// config/state/history, so a month that goes bad costs that month and not the lot.
///
/// Recorded as things happen - a card drop, a comment, the heartbeat's playtime tick, an inventory re-price - held
/// in memory, written once a minute when something changed and again on the way out. Every write is atomic, and a
/// file that won't read is moved aside and started afresh: history is nice to have, and must never be the reason
/// the app won't start.
///
/// The day is the LOCAL calendar day, worked out when the thing happens, so midnight rolls over by itself.
/// </remarks>
public static class History {
	/// <summary>A little over a year, so "this time last year" is still there to look at.</summary>
	public const int KeepDays = 400;

	/// <summary>One account's totals for one day.</summary>
	public sealed class Day {
		public int Cards { get; set; }
		public int Comments { get; set; }

		/// <summary>Minutes with at least one game running - banked time, the same measure as <see cref="Lifetime"/>.</summary>
		public double Minutes { get; set; }

		/// <summary>Minutes per appID. Steam credits every game that is running, so several at once add up to more
		/// than <see cref="Minutes"/>.</summary>
		public Dictionary<string, double> Games { get; set; } = [];

		/// <summary>The last inventory value seen that day - so once the day is over, the day's closing value.</summary>
		public decimal? Value { get; set; }

		/// <summary>The Steam currency the value is in. A value in a currency other than today's is left off the chart
		/// rather than drawn as a crash or a spike.</summary>
		public int Currency { get; set; }
	}

	/// <summary>What /api/history hands the dashboard: one slot per day, oldest first, ending today.</summary>
	public sealed class View {
		public string[] Days { get; set; } = [];
		public string Currency { get; set; } = "$";

		/// <summary>The first day anything was recorded at all, or null when there is no history yet.</summary>
		public string? Since { get; set; }

		public List<AccountView> Accounts { get; set; } = [];
		public Dictionary<string, string> Names { get; set; } = [];

		/// <summary>AppIDs with no name yet, for the web host to look up in the background. Not sent.</summary>
		[JsonIgnore]
		public List<uint> Unknown { get; set; } = [];
	}

	public sealed class AccountView {
		public string Name { get; set; } = "";
		public int[] Cards { get; set; } = [];
		public int[] Comments { get; set; } = [];
		public int[] Minutes { get; set; } = [];
		public decimal?[] Value { get; set; } = [];

		/// <summary>The last reading before the window, so the line can start where the value actually was.</summary>
		public decimal? ValueBefore { get; set; }

		/// <summary>Playtime per game as [day index, appID, minutes] - sparse, because most games aren't played most days.</summary>
		public List<object[]> Games { get; set; } = [];
	}

	private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

	/// <summary>day ("yyyy-MM-dd") -> account -> totals. Sorted, so a month's days come out in order.</summary>
	private static readonly SortedDictionary<string, Dictionary<string, Day>> Days = new(StringComparer.Ordinal);

	/// <summary>Months ("yyyy-MM") changed since they were last written.</summary>
	private static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);

	private static readonly Lock Gate = new();
	private static readonly Lock SaveGate = new();
	private static bool _loaded;
	private static string _today = "";
	private static Timer? _timer;

	private static string Dir => Path.Combine(ConfigStore.ConfigDir, "state", "history");

	private static string FileFor(string month) => Path.Combine(Dir, month + ".json");

	private static string Key(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>Load what's on disk, fill in what the short-term records still know, and start the minute timer.
	/// Called before any account starts, so nothing is counted twice.</summary>
	public static void Start(BotManager mgr) {
		Load();
		BackfillToday(mgr);
		_timer ??= new Timer(static _ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
	}

	/// <summary>A card drop or a rep4rep comment. Called from <see cref="Stats.Record"/>, so every caller is covered.</summary>
	public static void Record(string kind, string bot, DateTime whenUtc) {
		if ((kind is not (Stats.KindCard or Stats.KindComment)) || string.IsNullOrEmpty(bot)) {
			return;
		}

		Load();

		string day = Key(whenUtc.ToLocalTime());

		lock (Gate) {
			Day d = Get(day, bot);

			if (kind == Stats.KindCard) {
				d.Cards++;
			} else {
				d.Comments++;
			}

			Dirty.Add(day[..7]);
		}
	}

	/// <summary>Time with games running, from the heartbeat - the same tick that feeds the lifetime total, with the
	/// same rule that anything over five minutes is a gap (a machine waking from sleep), not play.</summary>
	public static void AddPlay(string bot, double minutes, IReadOnlyList<uint> apps) {
		if ((minutes <= 0) || (minutes > 5) || (apps.Count == 0) || string.IsNullOrEmpty(bot)) {
			return;
		}

		Load();

		string day = Key(DateTime.Now);

		lock (Gate) {
			Day d = Get(day, bot);
			d.Minutes += minutes;

			foreach (uint app in apps) {
				if (app == 0) {
					continue;
				}

				string id = app.ToString(CultureInfo.InvariantCulture);
				d.Games[id] = d.Games.GetValueOrDefault(id) + minutes;
			}

			Dirty.Add(day[..7]);
		}
	}

	/// <summary>The inventory's latest value. Kept per day, overwritten all day, so what is left is the closing figure.</summary>
	public static void NoteValue(string bot, decimal value) {
		if ((value <= 0) || string.IsNullOrEmpty(bot)) {
			return;
		}

		Load();

		int currency = PriceBook.CurrencyId;
		string day = Key(DateTime.Now);

		lock (Gate) {
			Day d = Get(day, bot);

			if ((d.Value == value) && (d.Currency == currency)) {
				return;
			}

			d.Value = value;
			d.Currency = currency;
			Dirty.Add(day[..7]);
		}
	}

	/// <summary>The last <paramref name="days"/> days, today included, for every account the fleet has now plus any
	/// that has history in the window (a removed account's drops still happened).</summary>
	public static View Query(int days, IEnumerable<string> fleet) {
		Load();

		days = Math.Clamp(days, 1, KeepDays);
		DateTime today = DateTime.Now.Date;
		string[] keys = [.. Enumerable.Range(0, days).Select(i => Key(today.AddDays(i - days + 1)))];
		int currency = PriceBook.CurrencyId;
		View view = new() { Days = keys, Currency = PriceBook.Symbol };
		HashSet<uint> apps = [];

		lock (Gate) {
			view.Since = Days.Keys.FirstOrDefault();

			List<string> names = [.. fleet.Distinct(StringComparer.OrdinalIgnoreCase)];
			HashSet<string> listed = new(names, StringComparer.OrdinalIgnoreCase);
			SortedSet<string> extra = new(StringComparer.OrdinalIgnoreCase);

			foreach (string key in keys) {
				if (Days.TryGetValue(key, out Dictionary<string, Day>? bots)) {
					extra.UnionWith(bots.Keys.Where(b => !listed.Contains(b)));
				}
			}

			names.AddRange(extra);

			foreach (string name in names) {
				AccountView a = new() {
					Name = name,
					Cards = new int[days],
					Comments = new int[days],
					Minutes = new int[days],
					Value = new decimal?[days]
				};

				for (int i = 0; i < days; i++) {
					if (!Days.TryGetValue(keys[i], out Dictionary<string, Day>? bots) || !bots.TryGetValue(name, out Day? d)) {
						continue;
					}

					a.Cards[i] = d.Cards;
					a.Comments[i] = d.Comments;
					a.Minutes[i] = (int) Math.Round(d.Minutes);

					if (Usable(d, currency)) {
						a.Value[i] = d.Value;
					}

					foreach ((string app, double m) in d.Games) {
						if ((m >= 0.5) && uint.TryParse(app, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) {
							a.Games.Add([i, app, (int) Math.Round(m)]);
							apps.Add(id);
						}
					}
				}

				// The closing value before the window opened, newest first - so a 7-day chart of an inventory that
				// wasn't re-priced this week still has a line to draw instead of an empty box.
				foreach ((string key, Dictionary<string, Day> bots) in Days.Reverse()) {
					if ((string.CompareOrdinal(key, keys[0]) < 0) && bots.TryGetValue(name, out Day? d) && Usable(d, currency)) {
						a.ValueBefore = d.Value;

						break;
					}
				}

				view.Accounts.Add(a);
			}
		}

		foreach (uint id in apps) {
			view.Names[id.ToString(CultureInfo.InvariantCulture)] = GameNames.Of(id);

			if (!GameNames.IsKnown(id)) {
				view.Unknown.Add(id);
			}
		}

		return view;

		static bool Usable(Day d, int currency) => d.Value is > 0 && ((d.Currency == 0) || (d.Currency == currency));
	}

	/// <summary>Write every month that changed. Safe to call any time; called on the timer and on the way out.</summary>
	public static void Save() {
		if (!_loaded) {
			return;   // nothing was ever read, so there is nothing to write - and never an empty file over a real one
		}

		lock (SaveGate) {
			List<(string Month, Dictionary<string, Dictionary<string, Day>> Data)> work = [];

			lock (Gate) {
				foreach (string month in Dirty) {
					Dictionary<string, Dictionary<string, Day>> data = new(StringComparer.Ordinal);

					foreach ((string day, Dictionary<string, Day> bots) in Days) {
						if (day.StartsWith(month, StringComparison.Ordinal) && (bots.Count > 0)) {
							data[day] = bots.ToDictionary(static b => b.Key, static b => Copy(b.Value), StringComparer.OrdinalIgnoreCase);
						}
					}

					work.Add((month, data));
				}

				Dirty.Clear();
			}

			foreach ((string month, Dictionary<string, Dictionary<string, Day>> data) in work) {
				try {
					string path = FileFor(month);

					if (data.Count == 0) {
						// Everything in it has aged out.
						if (File.Exists(path)) {
							File.Delete(path);
						}

						continue;
					}

					Directory.CreateDirectory(Dir);
					AtomicFile.Write(path, JsonSerializer.Serialize(data, Json));
				} catch (Exception e) {
					lock (Gate) {
						Dirty.Add(month);   // try again next minute
					}

					Log.Debug(new Said("couldn't save the history: {0}", e.Message));
				}
			}
		}
	}

	private static void Tick() {
		try {
			lock (Gate) {
				string today = Key(DateTime.Now);

				if (today != _today) {
					_today = today;
					Prune();
				}
			}

			Save();
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the history: {0}", e.Message));
		}
	}

	/// <summary>Minutes banked by these accounts over the last <paramref name="days"/> local days, today included.</summary>
	public static double MinutesOver(int days, IEnumerable<string> bots) {
		Load();

		HashSet<string> want = new(bots, StringComparer.OrdinalIgnoreCase);
		DateTime today = DateTime.Now.Date;
		double sum = 0;

		lock (Gate) {
			for (int i = 0; i < days; i++) {
				if (Days.TryGetValue(Key(today.AddDays(-i)), out Dictionary<string, Day>? accounts)) {
					sum += accounts.Where(a => want.Contains(a.Key)).Sum(static a => a.Value.Minutes);
				}
			}
		}

		return sum;
	}

	private static Day Get(string day, string bot) {
		if (!Days.TryGetValue(day, out Dictionary<string, Day>? bots)) {
			Days[day] = bots = new Dictionary<string, Day>(StringComparer.OrdinalIgnoreCase);
		}

		if (!bots.TryGetValue(bot, out Day? d)) {
			bots[bot] = d = new Day();
		}

		return d;
	}

	/// <summary>Rounded on the way out: nobody needs a minute to fifteen decimal places, and it halves the file.</summary>
	private static Day Copy(Day d) => new() {
		Cards = d.Cards,
		Comments = d.Comments,
		Minutes = Math.Round(d.Minutes, 2),
		Games = d.Games.Where(static g => g.Value > 0).ToDictionary(static g => g.Key, static g => Math.Round(g.Value, 2)),
		Value = d.Value,
		Currency = d.Currency
	};

	private static void Prune() {
		string cutoff = Key(DateTime.Now.AddDays(-KeepDays));

		foreach (string day in Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) {
			Days.Remove(day);
			Dirty.Add(day[..7]);
		}
	}

	private static void Load() {
		lock (Gate) {
			if (_loaded) {
				return;
			}

			_loaded = true;
			_today = Key(DateTime.Now);

			try {
				if (Directory.Exists(Dir)) {
					foreach (string file in Directory.GetFiles(Dir, "????-??.json")) {
						ReadMonth(file);
					}
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't read the history: {0}", e.Message));
			}

			Prune();

			try {
				Backfill();
			} catch (Exception e) {
				Log.Debug(new Said("couldn't read the history: {0}", e.Message));
			}
		}
	}

	private static void ReadMonth(string file) {
		try {
			Dictionary<string, Dictionary<string, Day>>? month = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Day>>>(File.ReadAllText(file));

			if (month == null) {
				return;
			}

			foreach ((string day, Dictionary<string, Day>? bots) in month) {
				if ((bots == null) || !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) {
					continue;
				}

				foreach ((string bot, Day? d) in bots) {
					if ((d == null) || string.IsNullOrEmpty(bot)) {
						continue;
					}

					Day into = Get(day, bot);
					into.Cards = Math.Max(0, d.Cards);
					into.Comments = Math.Max(0, d.Comments);
					into.Minutes = Math.Max(0, d.Minutes);
					into.Games = d.Games ?? [];
					into.Value = d.Value;
					into.Currency = d.Currency;
				}
			}
		} catch (Exception e) {
			// Moved aside rather than overwritten, so whatever is in it can still be looked at - and so it isn't
			// read and failed on every start from now on. That month simply begins again from what's recorded now.
			Log.Debug(new Said("couldn't read the history file {0}, starting that month afresh: {1}", Path.GetFileName(file), e.Message));

			try {
				File.Move(file, file + ".bad", overwrite: true);
			} catch {
				// still unreadable next start, and still harmless
			}
		}
	}

	/// <summary>
	/// Fill in whatever the short-term records still know, so the charts aren't empty on the day this arrives.
	/// </summary>
	/// <remarks>
	/// Stats has every card and comment of the last 90 days, and the inventory history a month of hourly values.
	/// Merged with a max, never added: it runs on every start, and a day that is already right stays exactly as
	/// it is. It also mends the minute or so a crash can lose between history writes, because Stats is appended
	/// the moment each card lands.
	/// </remarks>
	private static void Backfill() {
		Dictionary<(string Day, string Bot), (int Cards, int Comments)> counts = [];

		foreach (Stats.Event e in Stats.Recent(24 * 90)) {
			if ((e.Kind is not (Stats.KindCard or Stats.KindComment)) || string.IsNullOrEmpty(e.Bot)) {
				continue;
			}

			(string, string) key = (Key(e.When.ToLocalTime()), e.Bot);
			(int cards, int comments) = counts.GetValueOrDefault(key);
			counts[key] = e.Kind == Stats.KindCard ? (cards + 1, comments) : (cards, comments + 1);
		}

		foreach (((string day, string bot), (int cards, int comments)) in counts) {
			Day d = Get(day, bot);

			if ((cards > d.Cards) || (comments > d.Comments)) {
				d.Cards = Math.Max(d.Cards, cards);
				d.Comments = Math.Max(d.Comments, comments);
				Dirty.Add(day[..7]);
			}
		}

		string cutoff = Key(DateTime.Now.AddDays(-KeepDays));
		int currency = PriceBook.CurrencyId;

		foreach ((string bot, DateTime date, decimal value) in InventoryHistory.DailyCloses()) {
			string day = Key(date);

			if (string.CompareOrdinal(day, cutoff) < 0) {
				continue;
			}

			Day d = Get(day, bot);

			if (d.Value is null or <= 0) {
				d.Value = value;
				d.Currency = currency;
				Dirty.Add(day[..7]);
			}
		}
	}

	/// <summary>
	/// Today's playtime so far, from the two places that already count it: human mode's plan for the day (minutes
	/// and per-game minutes) and the daily report's lifetime snapshot, when that was taken earlier today.
	/// </summary>
	/// <remarks>Yesterday's playtime was never kept per day by anything, so it can't be rebuilt - it starts here.</remarks>
	private static void BackfillToday(BotManager mgr) {
		try {
			DateTime now = DateTime.Now;
			string today = Key(now);
			(string fired, Dictionary<string, double> snapshot) = ReadReportSnapshot();
			List<(string Bot, double Minutes, Dictionary<string, int> Games)> found = [];

			foreach (Bot b in mgr.All) {
				double minutes = 0;
				Dictionary<string, int> games = [];

				if (HumanDay.Load(b.Name, now) is { } plan) {
					minutes = plan.PlayedMinutes;
					games = plan.ByGame ?? [];
				}

				if ((fired == today) && snapshot.TryGetValue(b.Name, out double before)) {
					minutes = Math.Max(minutes, Lifetime.For(b.Name) - before);
				}

				if ((minutes > 0) || (games.Count > 0)) {
					found.Add((b.Name, minutes, games));
				}
			}

			lock (Gate) {
				foreach ((string bot, double minutes, Dictionary<string, int> games) in found) {
					Day d = Get(today, bot);
					bool changed = false;

					if (minutes > d.Minutes) {
						d.Minutes = minutes;
						changed = true;
					}

					foreach ((string app, int m) in games) {
						if (uint.TryParse(app, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && (id != 0) && (m > d.Games.GetValueOrDefault(app))) {
							d.Games[app] = m;
							changed = true;
						}
					}

					if (changed) {
						Dirty.Add(today[..7]);
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the history: {0}", e.Message));
		}
	}

	/// <summary>The day the daily report last fired and the lifetime minutes it saw then. Read straight off its file;
	/// anything odd reads as "no snapshot".</summary>
	private static (string Fired, Dictionary<string, double> Snapshot) ReadReportSnapshot() {
		Dictionary<string, double> snapshot = new(StringComparer.OrdinalIgnoreCase);

		try {
			string path = Path.Combine(ConfigStore.ConfigDir, "state", "report.json");

			if (!File.Exists(path)) {
				return ("", snapshot);
			}

			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			string fired = doc.RootElement.TryGetProperty("LastFired", out JsonElement f) && (f.ValueKind == JsonValueKind.String) ? f.GetString() ?? "" : "";

			if (doc.RootElement.TryGetProperty("Lifetime", out JsonElement life) && (life.ValueKind == JsonValueKind.Object)) {
				foreach (JsonProperty p in life.EnumerateObject()) {
					if (p.Value.ValueKind == JsonValueKind.Number) {
						snapshot[p.Name] = p.Value.GetDouble();
					}
				}
			}

			return (fired, snapshot);
		} catch {
			return ("", snapshot);
		}
	}
}
