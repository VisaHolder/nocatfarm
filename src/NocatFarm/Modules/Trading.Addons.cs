using NocatFarm.Core;
using NocatFarm.Plugins;

namespace NocatFarm.Modules;

/// <summary>
/// 1.9.1: add-ons answering trade offers (<see cref="IPluginHost.AddTradeJudge"/>).
/// </summary>
/// <remarks>
/// Only what the app leaves undecided is handed over: never your own accounts, a donation, a card swap nocat.lol paired, or
/// a fair swap the app takes itself - and only on an account whose owner switched "Let add-ons answer trade offers" on.
/// Whatever an add-on says, the app keeps the say over what leaves the account: an Accept that would give away something
/// on a keep list or from a kept game, something promised to another offer, or something Steam doesn't call tradable -
/// or that Steam would hold - is refused, and the offer left for the owner. When the app can't tell, it's left too.
/// The offer then waits like any other, and is judged again right before it's answered: a changed answer leaves it.
/// </remarks>
public sealed partial class Trading {
	/// <summary>
	/// What the add-ons said about each offer while it waits - the answer it was queued on, compared with theirs right before
	/// it's answered. Under the <see cref="_fair"/> lock.
	/// </summary>
	private readonly Dictionary<ulong, PluginHost.Judged> _addonSaid = [];

	/// <summary>Offers an add-on wanted accepted that the app refused (a keep list, a hold, ...): left for you for as long as
	/// they stand, and never asked about again. Under the <see cref="_fair"/> lock.</summary>
	private readonly HashSet<ulong> _addonLeft = [];

	/// <summary>
	/// Every asset on its way out, of any kind - on the giving side of offers accepted and waiting on a confirmation, and of
	/// this account's own offers still out. Rebuilt from Steam's list at every look (<see cref="CheckAsync"/>), so a restart
	/// can't forget one. Under the <see cref="_promised"/> lock.
	/// </summary>
	private HashSet<ulong> _promisedAssets = [];

	/// <summary>Test seam: the add-ons' answer on an offer, instead of asking the loaded plugins.</summary>
	internal Func<PluginTradeOffer, CancellationToken, Task<PluginHost.Judged>>? AskAddons { get; set; }

	/// <summary>Whether add-ons are asked about this offer at all: switched on for this account, a judge loaded, and not
	/// one of your own accounts' offers or a donation (those have their own settings).</summary>
	private bool AddonsMayJudge(TradeOffers.Offer offer, Dictionary<ulong, Way> allowed, HashSet<ulong> fleet) =>
		Bot.Cfg.AddonTrades && ((AskAddons != null) || PluginHost.HasTradeJudges)
		&& !offer.IsPureDonation && !fleet.Contains(offer.Partner) && !allowed.ContainsKey(offer.Partner);

	/// <summary>
	/// What the add-ons say about an offer the app leaves undecided. While it waits, the answer it was queued on stands -
	/// they're asked again right before it's answered (<see cref="AddonStillAsync"/>), not at every look. An Accept the app
	/// won't allow comes back <see cref="PluginHost.JudgedKind.Unsure"/>, and the offer is left for you for good.
	/// </summary>
	private async Task<PluginHost.Judged> AddonJudgeAsync(TradeOffers.Offer offer, CancellationToken ct) {
		lock (_fair) {
			if (_waiting.Contains(offer.Id) && _addonSaid.TryGetValue(offer.Id, out PluginHost.Judged? queued)) {
				return queued;
			}
		}

		PluginHost.Judged said = await AskAddonsNowAsync(offer, ct).ConfigureAwait(false);

		if (said.Kind == PluginHost.JudgedKind.Unsure) {
			Log.DebugOnChange($"addon:{Bot.Name}:{offer.Id}", $"trade offer #{offer.Id}: left alone this time - {said.Why}", Bot.Name);

			return said;
		}

		if ((said.Kind == PluginHost.JudgedKind.Accept) && (AcceptBlocked(offer) is { } blocked)) {
			LeaveForYou(offer, blocked);

			return said with { Kind = PluginHost.JudgedKind.Unsure };
		}

		lock (_fair) {
			if (said.Kind == PluginHost.JudgedKind.None) {
				_addonSaid.Remove(offer.Id);
			} else {
				_addonSaid[offer.Id] = said;
			}
		}

		return said;
	}

	/// <summary>
	/// Right before an offer an add-on answered is acted on: asked again, and for an Accept the app's rules looked at again
	/// (an item may have been promised to another offer since). True when the answer is still the same.
	/// </summary>
	private async Task<bool> AddonStillAsync(TradeOffers.Offer offer, PluginHost.Judged queued, CancellationToken ct) {
		PluginHost.Judged now = await AskAddonsNowAsync(offer, ct).ConfigureAwait(false);
		Said? blocked = (now.Kind == PluginHost.JudgedKind.Accept) ? AcceptBlocked(offer) : null;

		if ((now.Kind == queued.Kind) && (blocked == null)) {
			lock (_fair) {
				_addonSaid[offer.Id] = now;
			}

			return true;
		}

		_waiting.Remove(offer.Id);

		lock (_fair) {
			_addonSaid.Remove(offer.Id);
		}

		if (blocked is { } refused) {
			LeaveForYou(offer, refused);
		} else if (now.Kind == PluginHost.JudgedKind.Unsure) {
			Log.DebugOnChange($"addon:{Bot.Name}:{offer.Id}", $"trade offer #{offer.Id}: left alone this time - {now.Why}", Bot.Name);
		} else {
			Log.Info(new Said("offer {0} · left alone · the add-on's answer changed", NumberOf(offer.Id)), Bot.Name);
		}

		return false;
	}

	/// <summary>The add-ons asked now - the test seam's answer instead, when there is one.</summary>
	private Task<PluginHost.Judged> AskAddonsNowAsync(TradeOffers.Offer offer, CancellationToken ct) =>
		AskAddons?.Invoke(ForAddons(offer), ct) ?? PluginHost.JudgeTradeAsync(() => ForAddons(offer), ct);

	/// <summary>An add-on wanted it accepted and the app won't: left for you, said once.</summary>
	private void LeaveForYou(TradeOffers.Offer offer, Said why) {
		_waiting.Remove(offer.Id);
		bool first;

		lock (_fair) {
			_addonSaid.Remove(offer.Id);
			first = _addonLeft.Add(offer.Id);
		}

		if (first) {
			Log.Info(new Said("offer {0} · left for you · an add-on wanted it accepted, but {1}", NumberOf(offer.Id), why), Bot.Name);
		}
	}

	/// <summary>
	/// Whether the app refuses an add-on's Accept, and why - null when it allows it. Whatever the add-on said: nothing on a
	/// keep list or from a kept game (one whose cards were bought for a badge), nothing promised to another offer still
	/// waiting, nothing Steam doesn't describe as tradable - on either side - and no trade Steam already holds. What Steam
	/// didn't say is refused, never assumed.
	/// </summary>
	private Said? AcceptBlocked(TradeOffers.Offer offer) {
		HashSet<ulong> promised;

		lock (_promised) {
			promised = [.. _promisedAssets];
		}

		return AcceptBlocked(offer, CardExchange.RulesOf(Bot, DateTime.UtcNow), promised);
	}

	/// <inheritdoc cref="AcceptBlocked(TradeOffers.Offer)"/>
	internal static Said? AcceptBlocked(TradeOffers.Offer offer, CardExchange.Rules keep, IReadOnlySet<ulong> promised) {
		if (offer.HoldUntil != null) {
			return new Said("the trade would be held");
		}

		foreach (TradeOffers.Item item in offer.Giving) {
			object shown = ShownName(item);

			if (!item.Described || !item.Tradable) {
				return new Said("{0} can't be traded", shown);
			}

			if (promised.Contains(item.AssetId)) {
				return new Said("{0} is promised to another offer", shown);
			}

			if (keep.Keeps(item.Game, item.Name, item.Hash)) {
				return new Said("{0} is on a \"never touch\" list, or its game's cards were bought for a badge", shown);
			}
		}

		// Their side can't hold anything that can't be traded either: if Steam says so, its answer about the offer is off.
		foreach (TradeOffers.Item item in offer.Receiving) {
			if (!item.Described || !item.Tradable) {
				return new Said("{0} can't be traded", ShownName(item));
			}
		}

		return null;
	}

	/// <summary>An item as the log names it: its name, or its asset id when Steam gave none.</summary>
	private static object ShownName(TradeOffers.Item item) => item.Name.Length > 0 ? item.Name : item.AssetId;

	/// <summary>
	/// Whether Steam would hold the trade, right before an add-on's Accept: true no hold, false held (or Steam won't say,
	/// three looks running) - left for you - and null not known yet, asked again at the next look.
	/// </summary>
	private async Task<bool?> AddonHoldAsync(TradeOffers.Offer offer, CancellationToken ct) {
		TimeSpan? hold = await (ReadHold?.Invoke(offer.Partner, "", ct) ?? TradeOffers.HoldAsync(Bot, offer.Partner, ct)).ConfigureAwait(false);

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

			LeaveForYou(offer, new Said("Steam won't say whether the trade would be held"));

			return false;
		}

		lock (_fair) {
			_holdUnknown.Remove(offer.Id);
		}

		if (hold > TimeSpan.Zero) {
			LeaveForYou(offer, new Said("the trade would be held"));

			return false;
		}

		return true;
	}

	/// <summary>Forget what was said about offers that are gone. Called under the <see cref="_fair"/> lock.</summary>
	private void ForgetAddonAnswers(HashSet<ulong> live) {
		foreach (ulong gone in _addonSaid.Keys.Where(k => !live.Contains(k)).ToList()) {
			_addonSaid.Remove(gone);
		}

		_addonLeft.IntersectWith(live);
	}

	/// <summary>The offer as an add-on is handed it: a copy of what the app read, nothing more asked of Steam.</summary>
	internal PluginTradeOffer ForAddons(TradeOffers.Offer offer) => ForAddons(Bot.Name, offer, DateTime.UtcNow);

	/// <inheritdoc cref="ForAddons(TradeOffers.Offer)"/>
	internal static PluginTradeOffer ForAddons(string account, TradeOffers.Offer offer, DateTime nowUtc) =>
		new(account, offer.Id, offer.Partner, offer.Message, offer.Created,
			offer.HoldUntil is { } until ? (int) Math.Max(0, Math.Ceiling((until - nowUtc).TotalDays)) : null,
			Array.AsReadOnly([.. offer.Giving.Select(ForAddons)]), Array.AsReadOnly([.. offer.Receiving.Select(ForAddons)]));

	private static PluginTradeItem ForAddons(TradeOffers.Item i) =>
		new(i.App, i.Context, i.AssetId, i.ClassId, i.InstanceId, i.Amount, i.Name, i.Hash, i.Type, KindOf(i.Kind), i.Tradable, i.Marketable, i.Game, i.Described);

	private static PluginTradeItemKind KindOf(SpareKind kind) => kind switch {
		SpareKind.Card => PluginTradeItemKind.Card,
		SpareKind.FoilCard => PluginTradeItemKind.FoilCard,
		SpareKind.Background => PluginTradeItemKind.Background,
		SpareKind.Emoticon => PluginTradeItemKind.Emoticon,
		SpareKind.BoosterPack => PluginTradeItemKind.BoosterPack,
		SpareKind.Gems => PluginTradeItemKind.Gems,
		SpareKind.GemSack => PluginTradeItemKind.GemSack,
		_ => PluginTradeItemKind.Other
	};
}
