using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>1.8.2 smart levelling: before crafting, a normal set worth more on the market than the levels it'd buy is sold instead (<see cref="SetSale"/>).</summary>
public sealed partial class BadgeCraft {
	/// <summary>The sets that are weighed at all: normal ones, one level each - a foil set is always crafted.</summary>
	internal static List<Craftable> SaleCandidates(IEnumerable<Craftable> ready) => [.. ready.Where(static c => SetSale.Considered(c.Border, c.Levels))];

	/// <summary>
	/// The sets still to craft once the ones better sold are listed. Only with "Sell sets worth more than the levels they'd
	/// buy" and buying cards on; anything not known for sure (no prices, Steam not letting it buy) is crafted as before.
	/// </summary>
	private async Task<List<Craftable>> SellInsteadAsync(List<Craftable> ready, InventoryContents inventory, CancellationToken ct) {
		if (!SetSale.Applies(Bot.Cfg, CardBuyer.On(Bot)) || (BotManager.ModuleOf<CardBuyer>(Bot) is not { } buyer)) {
			return ready;
		}

		List<Craftable> weigh = [.. SaleCandidates(ready).Take(SetSale.MaxSetsALook)];

		if (weigh.Count == 0) {
			return ready;
		}

		_status = new Said("weighing sets against the levels they'd buy");
		IReadOnlyList<SetSale.Verdict> verdicts = await buyer.WeighSalesAsync([.. weigh.Select(static c => c.AppId)], ct).ConfigureAwait(false);
		List<Craftable> left = [.. ready];

		foreach (SetSale.Verdict verdict in verdicts) {
			if (!verdict.Sell) {
				Log.Debug($"{verdict.Game}: crafted, not sold - {verdict.Why}", Bot.Name);

				continue;
			}

			// Not crafted this time either way: sold, or weighed again at the next look.
			left.RemoveAll(c => (c.AppId == verdict.App) && (c.Border == 0));
			Log.Info(new Said("selling the {0} set instead of crafting: {1} buys about {2} levels", verdict.Game, verdict.Money, verdict.About), Bot.Name);
			int listed = await SetSale.ListAsync(Bot, verdict, inventory, ct).ConfigureAwait(false);

			if (listed > 0) {
				buyer.Ledger.AddSold(new BuyLedger.SoldSet(DateTime.UtcNow, verdict.App));
			}

			if (listed < verdict.Prices.Count) {
				Log.Info(new Said("{0} set: {1} of {2} cards listed · the rest wait for the next look", verdict.Game, listed, verdict.Prices.Count), Bot.Name);
				_moreToCraft = true;
			}
		}

		return left;
	}
}
