namespace NocatFarm.Core;

public static partial class Looting {
	/// <summary>
	/// Send exactly these Steam items - sacks of gems, sealed booster packs - to another of your own accounts, by its name
	/// here. Nothing else goes with them, and nothing on the market or in a waiting trade.
	/// </summary>
	internal static async Task<string> SendTheseAsync(Bot bot, string toAccount, IReadOnlyList<SpareItem> items, CancellationToken ct) {
		if (Modules.ScamWatch.HeldBack(bot) is { } held) {
			return held;
		}

		if ((BotManager.Instance?.Get(toAccount.Trim()) is not { } to) || (to.SteamId == 0)) {
			return new Said("{0}: '{1}' isn't one of your accounts here - nothing sent", bot.Name, toAccount).ToString();
		}

		if (to.SteamId == bot.SteamId) {
			return new Said("{0}: it can't send to itself - pick another account", bot.Name).ToString();
		}

		if (items.Count == 0) {
			return "";
		}

		HashSet<ulong> claimed = bot.ClaimItems(items.Select(static i => i.AssetId));

		try {
			if (await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is not { } promised) {
				return new Said("{0}: Steam wouldn't say which items are in a waiting trade - sending later", bot.Name).ToString();
			}

			List<Item> sending = [.. items.Where(i => claimed.Contains(i.AssetId) && !promised.Contains(i.AssetId))
				.Select(static i => new Item(i.AssetId, 0, 0, i.Amount, "", i.Name, SteamAppId, CommunityContext, i.Game, i.Tradable))];

			if (sending.Count == 0) {
				return "";
			}

			string token = await TradeTokenOfAsync(to.SteamId, ct).ConfigureAwait(false) ?? "";
			(bool ok, string message) = await SendOfferAsync(bot, to.SteamId, sending, token, ct).ConfigureAwait(false);

			return ok ? new Said("{0}: sent {1} item(s) to {2}", bot.Name, sending.Count, to.Name).ToString() : new Said("{0}: couldn't send - {1}", bot.Name, message).ToString();
		} finally {
			bot.ReleaseItems(claimed);
		}
	}
}
