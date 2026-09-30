using NocatFarm.Config;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// The stuck-account alarm: an account that should be banking hours and hasn't for a few hours gets said, restarted
/// once, and said again if the restart didn't help.
/// </summary>
/// <remarks>
/// Everything else in here reports what HAPPENED. The one failure that says nothing is the account that quietly stops:
/// disconnected and never back, stuck on "connecting", a sign-in Steam turned down, or signed in with its games gone.
/// The log shows a calm "still ..." line or nothing at all, and the first anyone knows is a week of hours missing.
///
/// It only ever looks at time that should have been banked. A clock starts when an account that ought to be playing
/// isn't, and is thrown away the moment it plays again - or the moment it has a reason not to: stopped or disabled by
/// you, paused, you playing on it, human mode asleep / done for the day / a day off, Steam's weekly maintenance, or
/// the whole PC just back from sleep. So a healthy account can't reach the threshold, however long it sits.
///
/// One automatic restart per account in 12 hours, and after that it only says so. Restarting in a loop is how a
/// sign-in problem turns into Steam rate-limiting the whole machine.
/// </remarks>
public static class StuckWatch {
	/// <summary>What one account looks like to the alarm right now.</summary>
	internal sealed class Watch {
		/// <summary>When it last should have been banking and wasn't - null while it's fine or has a reason.</summary>
		public DateTime? StuckSince { get; set; }

		/// <summary>The lifetime minutes last seen, so any new minute counts as banked.</summary>
		public double LastBanked { get; set; } = -1;

		/// <summary>0 nothing said, 1 said (and maybe restarted), 2 said again - quiet until it recovers.</summary>
		public int Alarms { get; set; }

		public DateTime AlarmedAt { get; set; }

		/// <summary>When this account was last seen signed in - a human-mode account's resting phase goes stale while it's down.</summary>
		public DateTime? DownSince { get; set; }

		/// <summary>Why it isn't counting right now, for the 'stuck' command - null when it is.</summary>
		public Said Excused { get; set; }
	}

	/// <summary>An automatic restart at most this often per account.</summary>
	internal static readonly TimeSpan RestartEvery = TimeSpan.FromHours(12);

	/// <summary>
	/// A human-mode account that dropped while asleep still has "asleep" as its last phase, and nothing moves it on
	/// while it's down. After this long down that phase is taken as out of date, and the time counts.
	/// </summary>
	internal static readonly TimeSpan StaleRest = TimeSpan.FromHours(12);

	/// <summary>The tick is every minute: a longer gap is the PC asleep or the clock moved, not the account stuck.</summary>
	internal static readonly TimeSpan Gap = TimeSpan.FromMinutes(3);

	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, Watch> Watches = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Kept apart from the watch, which is thrown away whenever the account has a reason - the 12h cap isn't.</summary>
	private static readonly Dictionary<string, DateTime> Restarted = new(StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> Restarting = new(StringComparer.OrdinalIgnoreCase);
	private static DateTime _lastTick = DateTime.MinValue;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052", Justification = "Held, never read: a timer nothing holds on to is collected and stops firing.")]
	private static Timer? _timer;

	private static BotManager? _mgr;

	/// <summary>Minutes this account has banked in all - any rise means it played. Swappable so the checks can run without disk.</summary>
	internal static Func<Bot, double> BankedMinutes { get; set; } = static b => Lifetime.For(b.Name);

	/// <summary>How an account is restarted: the way 'restart' does it - a human-mode account finishes up first.</summary>
	internal static Func<Bot, Task> Restart { get; set; } = static async b => {
		await b.StopAsync(graceful: true).ConfigureAwait(false);
		await Task.Delay(1500).ConfigureAwait(false);
		await b.StartAsync().ConfigureAwait(false);
	};

	public static void Start(BotManager mgr) {
		_mgr = mgr;
		_timer = new Timer(static _ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
	}

	private static void Tick() {
		try {
			if (_mgr is { } mgr) {
				Step(mgr.All, mgr.Global, DateTime.UtcNow);
			}
		} catch (Exception e) {
			if (Log.DebugOnChange("stuck:tick", $"stuck-account check failed: {Log.Describe(e)}")) {
				Log.StackToFile(e);
			}
		}
	}

	/// <summary>One look at every account. Separate from the timer so the checks can drive it with their own clock.</summary>
	internal static void Step(IEnumerable<Bot> bots, GlobalConfig g, DateTime now) {
		List<(Bot Bot, Said Line, bool Restart)> said = [];
		List<Bot> recovered = [];

		lock (Gate) {
			// Back from sleep (or the clock jumped): every clock starts again. Steam hasn't even noticed the connections are
			// dead yet, and the reconnects that follow are the ordinary kind.
			bool woke = (_lastTick != DateTime.MinValue) && ((now - _lastTick > Gap) || (now < _lastTick));
			_lastTick = now;

			if (!g.StuckAlarm) {
				Watches.Clear();

				return;
			}

			TimeSpan threshold = TimeSpan.FromHours(Math.Clamp(g.StuckAlarmHours, 1, 48));
			HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

			foreach (Bot bot in bots) {
				seen.Add(bot.Name);

				// Mid-restart it's stopped for a moment - that's us, not you, so it mustn't reset anything.
				if (Restarting.Contains(bot.Name)) {
					continue;
				}

				if (!Watches.TryGetValue(bot.Name, out Watch? w)) {
					Watches[bot.Name] = w = new Watch();
				}

				double banked = BankedMinutes(bot);
				bool played = ((w.LastBanked >= 0) && (banked > w.LastBanked)) || (bot.IsOnline && bot.PlayingApps.Any(static a => a != 0));
				w.LastBanked = banked;

				if (bot.IsOnline) {
					w.DownSince = null;
				} else {
					w.DownSince ??= now;
				}

				Said excuse = woke ? new Said("the PC just woke up") : Excuse(bot, w, now);
				w.Excused = excuse;

				if (played || !excuse.IsEmpty) {
					if (played && (w.Alarms > 0)) {
						recovered.Add(bot);
					}

					w.StuckSince = null;
					w.Alarms = 0;

					continue;
				}

				w.StuckSince ??= now;
				TimeSpan stuck = now - w.StuckSince.Value;

				if (stuck < threshold) {
					continue;
				}

				Said state = StateOf(bot);
				string hm = Fmt.Hm((int) stuck.TotalMinutes);

				if (w.Alarms == 0) {
					w.Alarms = 1;
					w.AlarmedAt = now;

					if (bot.State == BotState.NeedsGuard) {
						// A code or a password it's waiting on - restarting would only throw away the question you may be answering.
						said.Add((bot, new Said("hasn't banked any hours for {0} - it's {1}; it needs you", hm, state), false));
					} else if (Restarted.TryGetValue(bot.Name, out DateTime last) && (now - last < RestartEvery)) {
						said.Add((bot, new Said("hasn't banked any hours for {0} - it's {1}; restarted {2} ago, so not again", hm, state, Fmt.Hm((int) (now - last).TotalMinutes)), false));
					} else {
						Restarted[bot.Name] = now;
						Restarting.Add(bot.Name);
						said.Add((bot, new Said("hasn't banked any hours for {0} - it's {1}; restarting it", hm, state), true));
					}
				} else if ((w.Alarms == 1) && (now - w.AlarmedAt >= threshold)) {
					w.Alarms = 2;
					said.Add((bot, new Said("still hasn't banked any hours for {0} - it's {1}; not restarting it again - have a look", hm, state), false));
				}
			}

			// A removed account's clock goes with it.
			foreach (string gone in Watches.Keys.Where(k => !seen.Contains(k)).ToArray()) {
				Watches.Remove(gone);
			}
		}

		// Outside the lock: saying it reaches the notifier, and a restart takes seconds.
		foreach (Bot bot in recovered) {
			Said back = new Said("banking hours again");
			Log.Good(back, bot.Name);
			Log.Publish(Topic.Problems, bot.Name, back);
		}

		foreach ((Bot bot, Said line, bool restart) in said) {
			Log.Attention(line, bot.Name);

			if (restart) {
				_ = RestartAsync(bot);
			}
		}
	}

	private static async Task RestartAsync(Bot bot) {
		try {
			await Restart(bot).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Failed("stuck alarm: restarting", e, bot.Name);
		} finally {
			lock (Gate) {
				Restarting.Remove(bot.Name);
			}
		}
	}

	/// <summary>
	/// Why this account isn't expected to be banking hours right now - empty when it is. Every "never fires" case lives
	/// here, and nowhere else.
	/// </summary>
	internal static Said Excuse(Bot bot, Watch w, DateTime now) {
		if (!bot.Cfg.Enabled) {
			return new Said("disabled");
		}

		if (bot.State == BotState.Stopped) {
			return new Said("stopped");
		}

		if (bot.Stopping) {
			return new Said("finishing up");
		}

		if (bot.Paused) {
			return new Said("paused");
		}

		if (bot.PlayingBlocked || bot.InResumeGrace) {
			return new Said("you're playing on it");
		}

		if (SteamMaintenance.Likely(now)) {
			return new Said("Steam's weekly maintenance");
		}

		// Human mode resting by its own plan. While the account is down that phase is whatever it was when it dropped,
		// so it's only believed for a while - an account that dropped asleep and never came back is still stuck.
		if (HumanMode.RestingByPlan(bot) && (bot.IsOnline || (w.DownSince is { } down && (now - down < StaleRest)))) {
			return new Said("human mode is resting");
		}

		// A robot with nothing to play: signed in and idle is all it's meant to be.
		if (bot.IsOnline && !bot.Cfg.LegitMode && (bot.Cfg.IdleGames.Count == 0) && !bot.IsFarming && !bot.Grinding && !bot.DropsFirstActive) {
			return new Said("nothing to play");
		}

		return default;
	}

	/// <summary>What the account is doing instead of banking, in a few words.</summary>
	internal static Said StateOf(Bot bot) => bot.State switch {
		BotState.Connecting or BotState.LoggingIn => new Said("stuck signing in"),
		BotState.Reconnecting => new Said("disconnected and not coming back"),
		BotState.NeedsGuard => new Said("waiting for a Steam Guard code or password"),
		BotState.Failed => new Said("failed: {0}", new Said(bot.StatusText)),
		BotState.Online => new Said("signed in but not playing anything"),
		_ => new Said(bot.StatusText)
	};

	/// <summary>The 'stuck' command: each account's last banked time as the alarm sees it, and what it would do.</summary>
	public static string Text(BotManager mgr) {
		GlobalConfig g = mgr.Global;
		DateTime now = DateTime.UtcNow;
		System.Text.StringBuilder sb = new();

		sb.AppendLine(g.StuckAlarm
			? $"Stuck-account alarm: on - says so and restarts an account once after {Math.Clamp(g.StuckAlarmHours, 1, 48)}h with no hours banked when it should be banking."
			: "Stuck-account alarm: off (StuckAlarm).");

		lock (Gate) {
			foreach (Bot bot in mgr.All) {
				Watches.TryGetValue(bot.Name, out Watch? w);
				string restarted = Restarted.TryGetValue(bot.Name, out DateTime last) ? $" · auto-restarted {Fmt.Hm((int) (now - last).TotalMinutes)} ago" : "";
				string line = w == null ? "not looked at yet"
					: Restarting.Contains(bot.Name) ? "restarting now"
					: w.StuckSince is { } since ? $"no hours banked for {Fmt.Hm((int) (now - since).TotalMinutes)} - {StateOf(bot).ToEnglish()}" + (w.Alarms > 0 ? " · alarm sent" : "")
					: !w.Excused.IsEmpty ? $"not counting - {w.Excused.ToEnglish()}"
					: "banking hours";

				sb.AppendLine($"  {bot.Name,-14} {line}{restarted}");
			}
		}

		return sb.ToString().TrimEnd();
	}

	/// <summary>Everything forgotten - for the checks, and after a restore swaps the accounts out.</summary>
	internal static void Reset() {
		lock (Gate) {
			Watches.Clear();
			Restarted.Clear();
			Restarting.Clear();
			_lastTick = DateTime.MinValue;
		}
	}
}
