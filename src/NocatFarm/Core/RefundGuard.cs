using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Games that must not be touched, because playing them would burn a refund.
///
/// Steam refunds a game bought in the last fourteen days that has under two hours on it. Two hours is nothing to
/// an idler - a single boost session is two hours - so an automated account can quietly spend a refund the owner
/// was still deciding about. The card farmer already refused to farm one of these; nothing else knew to care, so
/// the same game was fair game for the idler, a grind and the achievement hunter.
///
/// This is that check in one place, asked by all of them. It holds a game while ALL of these are true:
///   - the account is set to protect refunds (<c>SkipRefundableGames</c>, per account),
///   - the account BOUGHT it - free-to-play, claimed freebies and family-shared games are never held,
///   - it was bought less than <c>RefundHoldDays</c> ago, and
///   - it has under two hours on it, which is the line Steam actually draws.
///
/// The hold lifts on its own: buy a game today and the hunter picks it up in a fortnight, or the moment you have
/// played two hours of it yourself. Fails OPEN by design - if Steam won't say what a licence is, nothing is held,
/// because holding an entire library over one failed lookup is worse than the thing it protects against.
/// </summary>
public sealed class RefundGuard(Bot bot) {
	/// <summary>Steam's own line. Not configurable, because it isn't ours to move.</summary>
	private const double RefundHours = 2.0;

	/// <summary>"Hades", or "Hades, Tunic and 3 more" - a list a person can read at a glance.</summary>
	private string Names(IReadOnlyList<uint> apps) => apps.Count switch {
		1 => Name(apps[0]),
		2 => $"{Name(apps[0])} and {Name(apps[1])}",
		_ => $"{Name(apps[0])}, {Name(apps[1])} and {apps.Count - 2} more"
	};

	/// <summary>The library's name for a game beats the built-in table, which only knows the popular ones.</summary>
	private string Name(uint app) => bot.Library.Find(app)?.Name ?? GameNames.Of(app);

	private DateTime Since(uint app, IReadOnlyDictionary<uint, AppOwnership> owned) =>
		owned.TryGetValue(app, out AppOwnership o) ? o.Since : bot.Library.Find(app)?.Acquired ?? DateTime.UtcNow;

	private HashSet<uint> _held = [];
	private DateTime _refreshedAt = DateTime.MinValue;

	/// <summary>Would playing this game risk a refund? Sync on purpose - every caller is on a hot path.</summary>
	public bool Holds(uint app) => bot.Cfg.SkipRefundableGames && _held.Contains(app);

	/// <summary>
	/// Recheck. Cheap when nothing has changed - the licence map is cached until a new licence arrives, and the
	/// library is on its own long timer.
	/// </summary>
	public async Task RefreshAsync(CancellationToken ct) {
		if (!bot.Cfg.SkipRefundableGames) {
			if (_held.Count > 0) {
				_held = [];
			}

			return;
		}

		if (!bot.IsOnline || (DateTime.UtcNow - _refreshedAt < TimeSpan.FromMinutes(20))) {
			return;
		}

		// Everything below is decided from the LIBRARY - the list of things this account can actually launch.
		//
		// Working from licences instead was wrong twice over. A licence covers a package, and a package contains
		// DLC, demos, soundtracks and tools as well as the game, so the hold list filled up with appIDs nobody
		// could play if they tried ("app 4637140 is inside its refund window"). And the library is where playtime
		// comes from: without it, a game with 40 hours on it looked like a fresh purchase and got held for nothing.
		if (!bot.Library.Ready) {
			return;   // no _refreshedAt stamp: try again on the next tick, once the library has landed
		}

		_refreshedAt = DateTime.UtcNow;

		try {
			IReadOnlyDictionary<uint, AppOwnership> owned = await bot.GetAppOwnershipAsync().ConfigureAwait(false);

			ct.ThrowIfCancellationRequested();

			int days = Math.Max(1, bot.Cfg.RefundHoldDays);
			HashSet<uint> held = [];

			foreach (Library.Entry game in bot.Library.Games) {
				// Borrowed games are never held.
				//
				// A family-shared game was bought by somebody else, and the only two numbers we can see about it
				// are when it appeared in THIS account's library and how long THIS account has played it - neither
				// of which says anything about the owner's refund window. Judging it on those got it flatly wrong:
				// a game the owner had sixteen hours in was reported as "still refundable" because it was new to
				// the shared library and this account had never launched it. Guessing about somebody else's
				// purchase with the wrong data is worse than not guessing.
				if (game.Shared) {
					continue;
				}

				// Past two hours Steam won't refund it anyway, so there is nothing left to protect.
				if (game.MinutesPlayed >= RefundHours * 60) {
					continue;
				}

				if (owned.TryGetValue(game.AppId, out AppOwnership own) && own.Paid && ((DateTime.UtcNow - own.Since).TotalDays < days)) {
					held.Add(game.AppId);
				}
			}

			if (!held.SetEquals(_held)) {
				List<uint> fresh = [.. held.Except(_held)];
				List<uint> freed = [.. _held.Except(held)];

				// One line, not one per game: a fortnight's worth of purchases is a sentence, not a wall.
				if (fresh.Count > 0) {
					Log.Info($"leaving {Names(fresh)} alone - still refundable{(fresh.Count == 1 ? $" until {Since(fresh[0], owned).AddDays(days):d}" : "")}", bot.Name);
				}

				if (freed.Count > 0) {
					Log.Info($"{Names(freed)} {(freed.Count == 1 ? "is" : "are")} past the refund window - free to play again", bot.Name);
				}

				_held = held;
			}

			// A grind started before the game was bought - or before protection was switched on - is the one way
			// a held game can already be running. Hours is precisely what a grind puts on it, so it stops here.
			if ((bot.GrindGame != 0) && Holds(bot.GrindGame)) {
				Log.Warn($"stopping the {Name(bot.GrindGame)} grind - that game is inside its refund window", bot.Name);
				bot.StopGrind();
			}
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Log.Debug($"couldn't check refund windows: {e.Message}", bot.Name);
		}
	}
}
