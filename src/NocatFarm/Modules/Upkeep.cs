using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// The slow background facts an account needs but nothing has to wait for: what's in its library, which games are
/// still inside their refund window, and what its inventory is worth.
///
/// A module of its own rather than a few lines in the heartbeat, because these take real time - pricing a large
/// inventory is hundreds of rate-limited market lookups - and the heartbeat is the one loop that must keep
/// ticking. Sharing it meant a status line could arrive three minutes late while a price sweep finished.
///
/// Everything here throttles itself, so the loop can be simple: ask often, and let each part decide it has
/// nothing to do yet.
/// </summary>
public sealed class Upkeep(Bot bot) : BotModule(bot) {
	public override string Name => "upkeep";

	/// <summary>Nothing to show - this module exists to keep other people's numbers true.</summary>
	public override string Status => "";

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			if (Bot.IsOnline) {
				try {
					await Bot.Library.RefreshIfStaleAsync(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
					await Bot.Refunds.RefreshAsync(ct).ConfigureAwait(false);
					await Bot.Inventory.RefreshIfStaleAsync(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception e) {
					Log.Debug($"upkeep hiccup: {e.GetType().Name}: {e.Message}", Bot.Name);
				}
			}

			if (!await Sleep(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}
}
