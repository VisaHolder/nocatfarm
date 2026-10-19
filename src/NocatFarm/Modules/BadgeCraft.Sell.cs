using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>1.8.2 smart levelling: before crafting, a normal set worth more on the market than the levels it'd buy is sold instead (<see cref="SetSale"/>).</summary>
public sealed partial class BadgeCraft {
	/// <summary>
	/// The sets that are weighed at all: every one (1.8.2 - foil sets and a game's several sets too), but a game on the keep
	/// list ("Never touch items from these games") is always crafted.
	/// </summary>
	internal static List<Craftable> SaleCandidates(IEnumerable<Craftable> ready, IReadOnlyCollection<uint> keep) =>
		[.. ready.Where(c => SetSale.Considered(c.Border, c.Levels) && !keep.Contains(c.AppId))];

	/// <summary>Lists a set sold instead of crafted - how many of its cards went up.</summary>
	private readonly Func<Bot, SetSale.Verdict, InventoryContents, CancellationToken, Task<int>> _listSet = SetSale.ListAsync;

	/// <summary>
	/// The sets still to craft once the ones better sold are listed (<see cref="SetSale.ChooseAsync"/>). Only with "Sell sets
	/// worth more than the levels they'd buy" and buying cards on. Every set it weighs gets a line saying what happened and
	/// why; one not known for sure whose cards are known to be worth something waits for the next look, not crafted.
	/// </summary>
	private async Task<List<Craftable>> SellInsteadAsync(List<Craftable> ready, InventoryContents inventory, CancellationToken ct) {
		if (SetSale.Applies(Bot.Cfg, CardBuyer.On(Bot)) && (SaleCandidates(ready, SpareRules.From(Bot.Cfg).KeepGames).Count > 0)) {
			_status = new Said("weighing sets against the levels they'd buy");
		}

		IReadOnlyList<SetSale.Pick> picks = await SetSale.ChooseAsync(Bot, [.. ready.Select(static c => new SetSale.Ready(c.AppId, c.Border, c.Levels))], inventory, ct).ConfigureAwait(false);
		List<Craftable> left = [.. ready];
		bool sold = false;

		foreach (SetSale.Pick pick in picks) {
			switch (pick.Choice) {
				case SetSale.Choice.Craft:
					if (!pick.Quiet) {
						Log.Info(new Said("{0}: crafted · {1}", pick.Game, pick.Reason), Bot.Name);
					}

					continue;
				case SetSale.Choice.Wait:
					// Not crafted: it may well be worth more sold. The next look weighs it again.
					left.RemoveAll(c => (c.AppId == pick.App) && (c.Border == pick.Border));
					_moreToCraft = true;

					// Its cards are being sold as worth more than their levels: nothing to say every look.
					if (pick.Quiet) {
						continue;
					}

					Log.Info(pick.Verdict is { Hold: true } ? new Said("{0}: not crafted yet · {1}", pick.Game, pick.Reason)
						: pick.Worth.Length == 0 ? new Said("{0}: not crafted yet · {1} · what its cards are worth isn't known", pick.Game, pick.Reason)
						: new Said("{0}: not crafted yet · {1} · a card is worth {2}", pick.Game, pick.Reason, pick.Worth), Bot.Name);

					continue;
			}

			SetSale.Verdict verdict = pick.Verdict!;

			// Not crafted this time either way: sold, or weighed again at the next look.
			left.RemoveAll(c => (c.AppId == verdict.App) && (c.Border == pick.Border));

			// 1.8.2: one set a sweep on a human-mode account - the next waits for a later sweep, not crafted meanwhile.
			if (Bot.Cfg.LegitMode && sold) {
				_moreToCraft = true;
				Log.Debug($"{verdict.Game}: sold at a later look - one set a look in human mode", Bot.Name);

				continue;
			}

			sold = true;
			Log.Info(verdict.Foil ? new Said("selling the {0} foil set instead of crafting: {1} buys about {2} levels", verdict.Game, verdict.Money, verdict.About)
				: verdict.Sets > 1 ? new Said("selling {0} {1} sets instead of crafting: {2} buys about {3} levels", verdict.Sets, verdict.Game, verdict.Money, verdict.About)
				: new Said("selling the {0} set instead of crafting: {1} buys about {2} levels", verdict.Game, verdict.Money, verdict.About), Bot.Name);
			int listed = await _listSet(Bot, verdict, inventory, ct).ConfigureAwait(false);

			// A normal set sold: its game's cards aren't bought back for a while (what's left of it is sold as dear cards).
			if ((listed > 0) && !verdict.Foil) {
				BuyLedger.Of(Bot.Name).AddSold(new BuyLedger.SoldSet(DateTime.UtcNow, verdict.App,
					Weighed: verdict.Prices.ToDictionary(static p => p.Hash, static p => p.Receive, StringComparer.Ordinal)));
			}

			int all = verdict.Prices.Count * Math.Max(1, verdict.Sets);

			if (listed < all) {
				Log.Debug(new Said("{0} set: {1} of {2} cards listed · the rest wait for the next look", verdict.Game, listed, all), Bot.Name);
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

		return (await SetSale.ChooseAsync(Bot, [.. ready.Select(static c => new SetSale.Ready(c.AppId, c.Border, c.Levels))], inventory, ct, preview: true).ConfigureAwait(false), "");
	}
}
