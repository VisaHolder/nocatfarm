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

		return await TradePolicy.DecideAsync(set, values, gates, ct).ConfigureAwait(false);
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
