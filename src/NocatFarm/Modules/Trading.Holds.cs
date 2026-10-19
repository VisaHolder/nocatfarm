using NocatFarm.Core;

namespace NocatFarm.Modules;

public sealed partial class Trading {
	/// <summary>Test seam: Steam's trade hold lookup for a partner (with its trade link token, or ""), instead of asking Steam.</summary>
	internal Func<ulong, string, CancellationToken, Task<TimeSpan?>>? ReadHold { get; set; }

	/// <summary>Hold lookups Steam didn't answer, per offer: after 3 the swap is left for you. Under the <see cref="_fair"/> lock.</summary>
	private readonly Dictionary<ulong, int> _holdUnknown = [];

	/// <summary>
	/// Whether a swap can go as far as trade holds go: true none, false it would be held - left for you - and null not
	/// known yet, asked again at the next look. Steam only answers about an account that isn't a friend with its trade
	/// link, which an offer doesn't carry - 1.8.2: a card swap nocat.lol paired is asked with the trade link token the match
	/// came with, when it came with one. For a stranger it may never say: after 3 looks without an answer the swap is left
	/// for you too, and the log says why - never accepted on a guess, never asked about for ever.
	/// </summary>
	private async Task<bool?> HoldVerdictAsync(TradeOffers.Offer offer, bool exchange, CancellationToken ct) {
		// A card swap nocat.lol paired looks only when "Skip swaps Steam would hold" is on.
		if (exchange && !Bot.Cfg.CardSwapSkipHeld) {
			return true;
		}

		string token = exchange && (CardExchange.MatchIdOf(offer.Message) is { } id) && (CardExchange.Find(Bot.SteamId, id) is { } match)
			&& (match.You == offer.Partner) && CardExchange.TokenOk(match.Token) ? match.Token : "";
		TimeSpan? hold = await (ReadHold?.Invoke(offer.Partner, token, ct) ?? TradeOffers.HoldAsync(Bot, offer.Partner, ct, token)).ConfigureAwait(false);

		if (hold == null) {
			int tries;

			lock (_fair) {
				tries = _holdUnknown[offer.Id] = _holdUnknown.GetValueOrDefault(offer.Id) + 1;

				if (tries >= 3) {
					_holdUnknown.Remove(offer.Id);
				}
			}

			if (tries < 3) {
				return null;
			}

			LeaveHeldSwap(offer.Id);
			Log.Info(new Said("offer {0} · left for you · Steam won't say whether it would be held", NumberOf(offer.Id)), Bot.Name);

			return false;
		}

		lock (_fair) {
			_holdUnknown.Remove(offer.Id);
		}

		if (hold > TimeSpan.Zero) {
			LeaveHeldSwap(offer.Id);
			Log.Info(new Said("offer {0} no longer a fair swap ({1}) - left for you", NumberOf(offer.Id), new Said("the cards would sit in a trade hold")), Bot.Name);

			return false;
		}

		return true;
	}
}
