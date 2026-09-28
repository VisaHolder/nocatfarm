using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// With <c>SellDuplicates</c> on: every so often, while the account is awake, take down week-old listings the market
/// has gone under and list a few more spare cards. A handful at a time, like somebody tidying their inventory -
/// never the whole lot in one burst.
/// </summary>
public sealed class DuplicateSeller(Bot bot) : BotModule(bot) {
	private Said _status = new("");

	public override string Name => "seller";
	public override string Status => Bot.Cfg.SellDuplicates ? _status : "";

	private HumanGate? _gate;

	protected override async Task RunAsync(CancellationToken ct) {
		// Well clear of everything a sign-in does.
		if (!await Sleep(Rng.Minutes(15, 40), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			TimeSpan wait = TimeSpan.FromMinutes(10);

			// A listing carries the account's name and the time on the market, so a human-mode account lists in its own
			// day - not while it's asleep.
			_gate ??= HumanGate.OwnDay(Bot);

			if (Bot.Cfg.SellDuplicates && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && _gate.Open
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				try {
					await Seller.RelistAsync(Bot, ct).ConfigureAwait(false);
					Seller.Plan plan = await Seller.PlanAsync(Bot, Math.Clamp(Bot.Cfg.SellPerRun, 1, 25), ct).ConfigureAwait(false);

					if (plan.Offers.Count > 0) {
						await Seller.SellAsync(Bot, plan.Offers, ct).ConfigureAwait(false);
					}
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't sell duplicate cards: {0}", e.Message), Bot.Name);
				}

				wait = Rng.Minutes(8 * 60, 14 * 60);
				DateTime next = DateTime.Now + wait;
				_status = new Said("next look for spare cards to sell around {0}", (Func<string>) (() => Fmt.Clock(next.ToUniversalTime())));
			}

			if (!await Sleep(wait, ct).ConfigureAwait(false)) {
				return;
			}
		}
	}
}
