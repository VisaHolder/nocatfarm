using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>1.8.2 smart levelling: before crafting, a normal set worth more on the market than the levels it'd buy is sold instead (<see cref="SetSale"/>).</summary>
public sealed partial class BadgeCraft {
	/// <summary>The sets that are weighed at all: normal ones, one level each - a foil set is always crafted.</summary>
	internal static List<Craftable> SaleCandidates(IEnumerable<Craftable> ready) => [.. ready.Where(static c => SetSale.Considered(c.Border, c.Levels))];

	/// <summary>Lists a set sold instead of crafted - how many of its cards went up.</summary>
	private readonly Func<Bot, SetSale.Verdict, InventoryContents, CancellationToken, Task<int>> _listSet = SetSale.ListAsync;

	/// <summary>
	/// The sets still to craft once the ones better sold are listed (<see cref="SetSale.ChooseAsync"/>). Only with "Sell sets
	/// worth more than the levels they'd buy" and buying cards on. Every set it weighs gets a line saying what happened and
	/// why; one not known for sure whose cards are known to be worth something waits for the next look, not crafted.
	/// </summary>
	private async Task<List<Craftable>> SellInsteadAsync(List<Craftable> ready, InventoryContents inventory, CancellationToken ct) {
		if (SetSale.Applies(Bot.Cfg, CardBuyer.On(Bot)) && (SaleCandidates(ready).Count > 0)) {
			_status = new Said("weighing sets against the levels they'd buy");
		}

		IReadOnlyList<SetSale.Pick> picks = await SetSale.ChooseAsync(Bot, [.. ready.Select(static c => new SetSale.Ready(c.AppId, c.Border, c.Levels))], inventory, ct).ConfigureAwait(false);
		List<Craftable> left = [.. ready];

		foreach (SetSale.Pick pick in picks) {
			switch (pick.Choice) {
				case SetSale.Choice.Craft:
					if (!pick.Quiet) {
						Log.Info(new Said("{0}: crafted · {1}", pick.Game, pick.Reason), Bot.Name);
					}

					continue;
				case SetSale.Choice.Wait:
					// Not crafted: it may well be worth more sold. The next look weighs it again.
					left.RemoveAll(c => (c.AppId == pick.App) && (c.Border == 0));
					_moreToCraft = true;
					Log.Info(pick.Verdict is { Hold: true } ? new Said("{0}: not crafted yet · {1}", pick.Game, pick.Reason)
						: pick.Worth.Length == 0 ? new Said("{0}: not crafted yet · {1} · what its cards are worth isn't known", pick.Game, pick.Reason)
						: new Said("{0}: not crafted yet · {1} · a card is worth {2}", pick.Game, pick.Reason, pick.Worth), Bot.Name);

					continue;
			}

			SetSale.Verdict verdict = pick.Verdict!;

			// Not crafted this time either way: sold, or weighed again at the next look.
			left.RemoveAll(c => (c.AppId == verdict.App) && (c.Border == 0));
			Log.Info(new Said("selling the {0} set instead of crafting: {1} buys about {2} levels", verdict.Game, verdict.Money, verdict.About), Bot.Name);
			int listed = await _listSet(Bot, verdict, inventory, ct).ConfigureAwait(false);

			if (listed > 0) {
				BuyLedger.Of(Bot.Name).AddSold(new BuyLedger.SoldSet(DateTime.UtcNow, verdict.App));
			}

			if (listed < verdict.Prices.Count) {
				Log.Info(new Said("{0} set: {1} of {2} cards listed · the rest wait for the next look", verdict.Game, listed, verdict.Prices.Count), Bot.Name);
				_moreToCraft = true;
			}
		}

		return left;
	}

	/// <summary>
	/// 'craft preview': each finished set and whether it would be crafted or sold, and why. Reads the badges pages and the
	/// inventory, and prices the sets it would weigh - nothing is crafted, listed or claimed. Problem: why it couldn't look.
	/// </summary>
	public async Task<(IReadOnlyList<SetSale.Pick> Picks, string Problem)> PreviewAsync(CancellationToken ct) {
		if (await ReadyAsync(ct).ConfigureAwait(false) is not { } ready) {
			return ([], "couldn't read the badges page - try again later");
		}

		if (ready.Count == 0) {
			return ([], "");
		}

		if (await Inventory.ReadAsync(Bot, 753, "6", ct).ConfigureAwait(false) is not { Complete: true } inventory) {
			return ([], "couldn't read all of its inventory - try again later");
		}

		return (await SetSale.ChooseAsync(Bot, [.. ready.Select(static c => new SetSale.Ready(c.AppId, c.Border, c.Levels))], inventory, ct).ConfigureAwait(false), "");
	}
}
