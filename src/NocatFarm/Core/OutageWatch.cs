namespace NocatFarm.Core;

/// <summary>
/// 1.8.5: the internet (or Steam) gone for every account at once, and back - said once, when it's over: "back online after
/// 14m offline · all 3 accounts signed back in". One account reconnecting on its own is ordinary and never said; nor is an
/// account stopped, paused or off on purpose - only those meant to be signed in count.
/// </summary>
public static class OutageWatch {
	/// <summary>How long every account has to be cut off before it counts as an outage.</summary>
	public static readonly TimeSpan AtLeast = TimeSpan.FromMinutes(5);

	/// <param name="Since">When every account was last seen cut off together (null: they aren't).</param>
	/// <param name="Long">It has lasted <see cref="AtLeast"/> - its end is said.</param>
	/// <param name="Up">Every account has been signed in at once since the start - the first sign-in (a slow one after a
	/// restart, say) is never taken for an outage.</param>
	public sealed record Watch(DateTime? Since, bool Long, bool Up = false);

	/// <summary>Cut off from Steam, and trying to get back (not stopped on purpose).</summary>
	public static bool Lost(BotState s) => s is BotState.Connecting or BotState.Reconnecting or BotState.Failed or BotState.LoggingIn;

	/// <summary>
	/// One look at the accounts meant to be signed in (stopped ones left out): the watch from here, and the line to say when
	/// a long outage just ended (every one of them signed in again).
	/// </summary>
	public static (Watch Next, Said? Line) Step(Watch w, IReadOnlyList<BotState> states, DateTime now) {
		List<BotState> meant = [.. states.Where(static s => s != BotState.Stopped)];

		if (!w.Up) {
			return ((meant.Count > 0) && meant.All(static s => s == BotState.Online) ? w with { Up = true } : w, null);
		}

		if ((meant.Count > 0) && meant.All(Lost)) {
			DateTime since = w.Since ?? now;

			return (new Watch(since, w.Long || (now - since >= AtLeast), true), null);
		}

		if (w.Long && (w.Since is { } from) && meant.All(static s => s == BotState.Online)) {
			return (new Watch(null, false, true), new Said("back online after {0} offline · all {1} account(s) signed back in",
				Fmt.Hm(Math.Max(1, (int) (now - from).TotalMinutes)), meant.Count));
		}

		// Some are back, the rest still on their way: a long outage waits for all of them; a short one is forgotten.
		return w.Long ? (w, null) : (new Watch(null, false, true), null);
	}

	private static Timer? _timer;
	private static Watch _watch = new(null, false);

	/// <summary>Looks every 30 seconds while nocat.farm runs.</summary>
	public static void Start(BotManager mgr) {
		_timer = new Timer(_ => {
			try {
				(Watch next, Said? line) = Step(_watch, [.. mgr.All.Where(static b => b.Cfg.Enabled && !b.Paused).Select(static b => b.State)], DateTime.UtcNow);
				_watch = next;

				if (line is { } said) {
					Log.Event(said, "nocat.farm", Topic.Problems);   // good news, green - under Problems, so Telegram and Discord get it
				}
			} catch (Exception e) {
				Log.Debug(new Said("couldn't look at the connection: {0}", Log.Describe(e)));
			}
		}, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
	}
}
