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
				return "paused";
			}

			if (Bot.Cfg.IdleGames.Count == 0 && string.IsNullOrWhiteSpace(Bot.CustomName)) {
				return "nothing configured";
			}

			if (Bot.HumanOwned) {
				return "standing by (human mode)";
			}

			if (Bot.IsFarming) {
				return "standing by (farming cards)";
			}

			return Bot.PlayingBlocked ? "standing down (you're using it)" : string.IsNullOrEmpty(Bot.Playing) ? "idle" : Bot.Playing;
		}
	}

	protected override async Task RunAsync(CancellationToken ct) {
		// A brand new session is still settling; asserting games in the same instant as the logon is the one thing
		// a real client never does.
		if (!await Sleep(Rng.Seconds(8, 25), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			Assert();

			if (!await Sleep(Rng.Seconds(ReassertLowSeconds, ReassertHighSeconds), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>When the current run of idling began, so the log can say how long it has been going.</summary>
	public DateTime? IdlingSince { get; private set; }

	/// <summary>Re-send what this account should be playing, unless something with a stronger claim owns it.</summary>
	public void Assert() {
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

		if (Bot.HumanOwned) {
			return;   // human mode is running the day - it decides what plays and when
		}

		if (!Bot.CanPlay) {
			return;
		}

		// "Never touch these" has to mean never, not just never farm - the card farmer honoured the blacklist
		// while the idler happily played the same appIDs anyway.
		List<uint> games = Bot.Cfg.IdleGames
			.Where(a => !Bot.Cfg.BlacklistedGames.Contains(a) && !Live.Global.GlobalBlacklistedGames.Contains(a))
			.ToList();

		if (games.Count == 0 && string.IsNullOrWhiteSpace(Bot.CustomName)) {
			Bot.StopPlaying();
			IdlingSince = null;

			return;
		}

		// With a custom name set and no games, Steam still shows the name - that combination is deliberate.
		Bot.SetPlaying(games);
		IdlingSince ??= DateTime.UtcNow;
	}
}
