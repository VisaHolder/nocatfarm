using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Keeps the configured games "running" whenever nothing more important is. The card farmer outranks it, so
/// this only takes over once there is nothing left to farm.
///
/// It re-asserts on a timer rather than setting the games once and hoping: Steam quietly drops a played-games
/// session on a network hiccup, and a custom game name in particular stops showing if it is never re-sent.
/// </summary>
public sealed class Idler(Bot bot) : BotModule(bot) {
	private const int ReassertLowSeconds = 240;
	private const int ReassertHighSeconds = 420;

	public override string Name => "idle";

	public override string Status {
		get {
			if (Bot.Paused) {
				return Loc.T("paused");
			}

			if (Bot.Cfg.IdleGames.Count == 0 && !Bot.Cfg.IdleWholeLibrary && string.IsNullOrWhiteSpace(Bot.CustomName)) {
				return Loc.T("nothing configured");
			}

			if (Bot.HumanOwned) {
				return Loc.T("standing by (human mode)");
			}

			if (Bot.IsFarming) {
				return Loc.T("standing by (farming cards)");
			}

			if (Bot.PlayingBlocked) {
				return Loc.T("standing down (you're using it)");
			}

			if (string.IsNullOrEmpty(Bot.Playing)) {
				return Loc.T("idle");
			}

			return (Rotating is { } r) && !Bot.Grinding
				? new Said("idling {0} of {1} games - next batch at {2}", r.Now.Count, r.Total, IdleRotation.When(r.MovesAt)).ToString()
				: Bot.Playing;
		}
	}

	/// <summary>Where the idle rotation is, as of the last time it idled. Null while it isn't rotating.</summary>
	public sealed record RotationView(List<uint> Now, List<uint> Next, int Total, int Batch, int Batches, DateTime MovesAt);

	public RotationView? Rotating { get; private set; }

	/// <summary>Created on first use, so an account that never rotates never reads or writes a state file.</summary>
	public IdleRotation Rotation => _rotation ??= new IdleRotation(IdleRotation.PathFor(Bot.Name), Bot.Name);

	private IdleRotation? _rotation;

	/// <summary>
	/// What the last re-assert was worked out from. Turning "Idle my whole library" or "Rotate the idle list" on or
	/// off, or the library arriving, puts the new list on within 20 seconds instead of at the next 7-10 minute
	/// re-assert. Only Assert sets it: 'rotation &lt;account&gt;' works out a plan too, and used to count as done, so
	/// the account kept its old games until the next re-assert.
	/// </summary>
	private (bool Whole, bool Rotate, bool Ready)? _assertedFor;

	public bool OutOfDate => _assertedFor is { } was && (was != Now());

	private (bool Whole, bool Rotate, bool Ready) Now() =>
		(Bot.Cfg.IdleWholeLibrary, Bot.Cfg.RotateIdleGames, Bot.Cfg.IdleWholeLibrary && Bot.Library.Ready);

	protected override async Task RunAsync(CancellationToken ct) {
		// A brand new session is still settling; asserting games in the same instant as the logon is the one thing
		// a real client never does.
		if (!await Sleep(Rng.Seconds(8, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			Assert();

			// Wait for the next re-assert in short steps, watching for a grind to finish. One that runs out ends
			// between re-asserts - including one started halfway through a wait - and a 3-minute grind was seen
			// handing back after 338 seconds. Checking a flag every 20 seconds costs nothing.
			DateTime due = DateTime.UtcNow + Rng.Seconds(ReassertLowSeconds, ReassertHighSeconds);
			bool sawGrind = Bot.Grinding;

			while (DateTime.UtcNow < due) {
				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				if (Bot.Grinding) {
					sawGrind = true;
				} else if (sawGrind) {
					break;   // it just ended - put the normal games back now
				}

				// The rotation's batch is up (or 'rotation <account> next' asked), one of the two settings changed, or the
				// library just arrived for "Idle my whole library": no reason to sit out the rest of the wait. Only while the idler is the one
				// playing - farming, a grind or a pause would just bounce the re-assert straight back every 20 seconds.
				bool mine = Bot.CanPlay && !Bot.Grinding && !Bot.IsFarming && !Bot.HumanOwned && !Bot.Cfg.LegitMode;

				if (mine && (((Rotating is { } r) && (DateTime.UtcNow >= r.MovesAt)) || (_rotation?.MoveRequested ?? false)
					|| OutOfDate)) {
					break;
				}
			}
		}
	}

	/// <summary>When the current run of idling began, so the log can say how long it has been going.</summary>
	public DateTime? IdlingSince { get; private set; }

	/// <summary>Re-send what this account should be playing, unless something with a stronger claim owns it.</summary>
	public void Assert() {
		// Human mode runs its own grinds, after the owner-safety wait at logon. Playing the grind game from here
		// skipped that wait: a reconnect mid-grind put the game on seconds after logon, over the owner if he had
		// sat down in the meantime.
		if (Bot.HumanOwned || Bot.Cfg.LegitMode) {
			return;
		}

		if (!Bot.CanPlay) {
			return;
		}

		if (Bot.Grinding) {
			if (DateTime.UtcNow < Bot.GrindStartsAt) {
				return;   // grind is queued but not started - let whatever's playing keep running (legit switch-over)
			}

			Bot.SetPlaying([Bot.GrindGame]);
			IdlingSince ??= DateTime.UtcNow;

			return;   // the user asked for hours on one game; nothing else gets a say until it expires
		}

		if (Bot.IsFarming) {
			return;   // the card farmer decides what plays while it is working
		}

		_assertedFor = Now();
		List<uint> games = Plan(DateTime.UtcNow);

		if (games.Count == 0 && string.IsNullOrWhiteSpace(Bot.CustomName)) {
			Bot.StopPlaying();
			IdlingSince = null;

			return;
		}

		// With a custom name set and no games, Steam still shows the name - that combination is deliberate.
		Bot.SetPlaying(games);
		IdlingSince ??= DateTime.UtcNow;
	}

	/// <summary>
	/// The games to idle right now. With "Idle my whole library" and "Rotate the idle list" both off this is the
	/// idle list exactly as it always was; either one on can grow the list past what Steam plays at once, and then
	/// it is either cut to what fits (least played first) or taken a batch at a time.
	/// </summary>
	public List<uint> Plan(DateTime now) {
		// "Never touch these" has to mean never, not just never farm - the card farmer honoured the blacklist
		// while the idler happily played the same appIDs anyway. A game inside its refund window is the same
		// story: the farmer left it alone and the idler would have sat on it for days.
		bool Allowed(uint a) => !Bot.Cfg.BlacklistedGames.Contains(a) && !Live.Global.GlobalBlacklistedGames.Contains(a) && !Bot.Refunds.Holds(a);

		List<uint> games = Bot.Cfg.IdleGames.Where(Allowed).ToList();
		bool whole = Bot.Cfg.IdleWholeLibrary;

		if (!whole && !Bot.Cfg.RotateIdleGames) {
			Rotating = null;

			return games;
		}

		// The library is games only - no DLC, tools or soundtracks. Family-shared games come along only with
		// "Include family-shared games", and never one somebody in the family is playing right now.
		if (whole && Bot.Library.Ready) {
			games.AddRange(Bot.Library.Games
				.Where(g => !g.Shared || (Bot.Cfg.IncludeFamilyLibrary && !Bot.Library.FamilyIsPlaying(g.AppId)))
				.Select(static g => g.AppId)
				.Where(Allowed));
		}

		games = [.. games.Where(static a => a != 0).Distinct()];
		int slots = IdleRotation.Slots(!string.IsNullOrWhiteSpace(Bot.CustomName));

		if (games.Count <= slots) {
			Rotating = null;

			return games;
		}

		HashSet<uint> targeted = [.. HourTargets.Parse(Bot.Cfg.HourTargets)
			.Where(t => Bot.Library.MinutesOn(t.AppId) < t.Hours * 60)
			.Select(static t => t.AppId)];
		List<uint> ordered = IdleRotation.Order(games, whole ? Bot.Cfg.IdleGames : [], Bot.Library.MinutesOn, targeted.Contains);

		if (!Bot.Cfg.RotateIdleGames) {
			Rotating = null;

			return [.. ordered.Take(slots)];
		}

		TimeSpan every = TimeSpan.FromHours(Math.Max(1, Bot.Cfg.RotateEveryHours));
		List<uint> batch = Rotation.Current(ordered, slots, every, now);
		Rotating = new RotationView(batch, Rotation.Next(ordered, slots), ordered.Count, Rotation.Batch(slots), Rotation.Batches(slots), Rotation.MovesAt);

		return batch;
	}

	/// <summary>'rotation &lt;account&gt; next': on to the next batch now.</summary>
	public void MoveOn() {
		Rotation.MoveOn();
		Assert();
	}
}
