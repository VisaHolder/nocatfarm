using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Custom trade rules, as the offer loop asks them: the account's JSON rules, the items valued, the hard gates, and the
/// verdict. Rules never confirm an offer for you, and never override a master, a donation, a nocat.lol swap or one of the
/// offers held for you.
/// </summary>
public sealed partial class Trading {
	/// <summary>What the custom rules said about an offer, said once - not every look while it waits.</summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> CustomSaid = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The custom rules' verdict on one offer. No rules, or none that apply: Abstain, and the usual behaviour continues.</summary>
	private async Task<TradeDecision> CustomRulesAsync(TradeOffers.Offer offer, CancellationToken ct) {
		TradeRuleSet set = TradeRuleCodec.Parse(Bot.Cfg.TradeRulesJson);

		// A broken rule set is said once, and otherwise changes nothing.
		if (set.Problems.Count > 0) {
			string problems = string.Join("; ", set.Problems);

			if (CustomSaid.TryAdd(Bot.Name + ":problems", problems)) {
				Log.Info(new Said("custom trade rules ignored, with problems: {0}", problems), Bot.Name);
			}
		}

		if (!set.Any) {
			return new TradeDecision(TradeVerdict.Abstain, null, "no custom rule applies", []);
		}

		IReadOnlyCollection<ulong> promised = PromisedAssets();
		TradeValues values = await TradeValues.ReadAsync(Bot, offer, promised, ct).ConfigureAwait(false);
		GateResult gates = TradeGates.Check(Bot, offer, values, promised);
		TradeDecision decision = await TradePolicy.DecideAsync(set, values, gates, ct).ConfigureAwait(false);

		// A rule that stopped at a missing or too-old price: ask the market once, then decide again with what it says. Still
		// unknown after that, the offer is left for the next look - never accepted or declined on a guess.
		if ((decision.Verdict == TradeVerdict.Unknown) && values.RefreshWanted && MayRefreshPrices(offer.Id, DateTime.UtcNow)) {
			TradeValues fresh = await TradeValues.ReadAsync(Bot, offer, promised, ct, TradeValueLookups.Stale).ConfigureAwait(false);
			GateResult gatesAgain = TradeGates.Check(Bot, offer, fresh, promised);
			TradeDecision again = await TradePolicy.DecideAsync(set, fresh, gatesAgain, ct).ConfigureAwait(false);

			Log.Debug(new Said("offer {0}: a custom rule needed a price - asked the market again, and it says {1}", NumberOf(offer.Id), again.Verdict), Bot.Name);

			return again;
		}

		return decision;
	}

	/// <summary>When each offer last had its prices read again, so one offer can't ask every look.</summary>
	private readonly Dictionary<ulong, DateTime> _refreshedAt = [];
	private readonly Lock _refreshGate = new();
	private DateTime _refreshWindow = DateTime.MinValue;
	private int _refreshesInWindow;

	/// <summary>
	/// Whether asking the market again is worth it for this offer: once every ten minutes for one offer, and at most three
	/// offers a minute - a burst of trades must not become a burst of market requests.
	/// </summary>
	private bool MayRefreshPrices(ulong id, DateTime now) {
		lock (_refreshGate) {
			if (_refreshedAt.TryGetValue(id, out DateTime at) && (now - at < TimeSpan.FromMinutes(10))) {
				return false;
			}

			if ((now - _refreshWindow) >= TimeSpan.FromMinutes(1)) {
				_refreshWindow = now;
				_refreshesInWindow = 0;
			}

			if (_refreshesInWindow >= 3) {
				return false;
			}

			_refreshesInWindow++;
			_refreshedAt[id] = now;

			DateTime cut = now - TimeSpan.FromHours(1);

			foreach (ulong old in _refreshedAt.Where(p => p.Value < cut).Select(static p => p.Key).ToList()) {
				_refreshedAt.Remove(old);
			}

			return true;
		}
	}

	/// <summary>Says once why a custom rule couldn't decide, so an offer left for you isn't silent.</summary>
	private void LogCustomOnce(TradeOffers.Offer offer, TradeDecision decision) {
		string key = $"{offer.Id}:{decision.Verdict}:{decision.Why}";

		if (!CustomSaid.TryAdd($"{Bot.Name}:offer:{key}", key)) {
			return;
		}

		Log.Info(decision.Verdict == TradeVerdict.Blocked
			? new Said("offer {0}: left for you - a custom rule is blocked ({1})", NumberOf(offer.Id), decision.Why)
			: new Said("offer {0}: left for you - a custom rule couldn't value it ({1})", NumberOf(offer.Id), decision.Why), Bot.Name);
	}

	/// <summary>Says which custom rule accepted an offer, once it answered it.</summary>
	private void LogCustomAccepted(TradeOffers.Offer offer, TradeDecision? decision) {
		if (decision?.RuleId is { Length: > 0 } id) {
			Log.Info(new Said("offer {0}: accepted by custom rule {1}", NumberOf(offer.Id), id), Bot.Name);
		}
	}
}
