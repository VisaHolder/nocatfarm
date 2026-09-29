using System.Globalization;
using System.Text.Json;

namespace NocatFarm.Core;

/// <summary>
/// Trade offers as Steam's own API describes them - IEconService/GetTradeOffers, asked with the account's login
/// token rather than a Web API key.
/// </summary>
/// <remarks>
/// This replaced reading the trade offers PAGE. The page named the other side only by a profile link, and all
/// three accounts here have custom URLs, so an offer linked as /id/somebody was skipped without a word - donations
/// and offers from your own accounts included. The API gives the other account's id outright, every item on both
/// sides, the offer's state and whether it would sit in a trade hold.
/// </remarks>
public static class TradeOffers {
	/// <summary>Steam's offer states that matter here.</summary>
	public const int Active = 2;
	public const int NeedsConfirmation = 9;
	public const int InEscrow = 11;

	/// <summary>One item in an offer, with what Steam says it is.</summary>
	/// <param name="Game">The game a community item belongs to (market_fee_app); 0 when Steam didn't say.</param>
	/// <param name="Icon">Steam's picture for it - https://community.cloudflare.steamstatic.com/economy/image/&lt;Icon&gt;/96fx96f.</param>
	public sealed record Item(uint App, string Context, ulong AssetId, ulong ClassId, ulong InstanceId, uint Amount, string Type, string Name, uint Game, bool Described, string Icon = "");

	/// <param name="Giving">What this account would hand over.</param>
	/// <param name="Receiving">What this account would get.</param>
	/// <param name="HoldUntil">When the hold ends on a trade already sitting in one (state 11). Steam only fills this in
	/// once a trade is held - whether an offer WOULD be held is <see cref="HoldAsync"/>.</param>
	/// <param name="ConfirmationMethod">0 none; otherwise it's accepted (or sent) and waiting on an email (1) or the
	/// phone (2) - so it isn't to be accepted again, and the cards on its giving side are already spoken for.</param>
	public sealed record Offer(ulong Id, ulong Partner, int State, bool Ours, DateTime? HoldUntil, List<Item> Giving, List<Item> Receiving, int ConfirmationMethod = 0) {
		public bool IsPureDonation => (Giving.Count == 0) && (Receiving.Count > 0);

		/// <summary>Accepted by this account, waiting on the phone or an email before anything moves.</summary>
		public bool AwaitingConfirmation => !Ours && (State == Active) && (ConfirmationMethod != 0);

		public string Describe => $"+{Receiving.Sum(static i => i.Amount)} / -{Giving.Sum(static i => i.Amount)}";
	}

	private const ulong AccountBase = 76561197960265728UL;

	/// <summary>The account's live offers (active, awaiting confirmation or in a hold). Null when Steam didn't answer.</summary>
	public static async Task<List<Offer>?> ActiveAsync(Bot bot, bool received, bool sent, CancellationToken ct = default) {
		List<Offer> offers = [];
		string cursor = "";

		// "active_only" also returns every offer that changed since the cutoff - with none given, that's all of
		// history. Now as the cutoff leaves just the live ones. A long list comes in pages.
		for (int page = 0; page < 5; page++) {
			Dictionary<string, string> args = new() {
				["get_received_offers"] = received ? "1" : "0",
				["get_sent_offers"] = sent ? "1" : "0",
				["active_only"] = "1",
				["time_historical_cutoff"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
				["get_descriptions"] = "1",
				["language"] = "english"
			};

			if (cursor.Length > 0) {
				args["cursor"] = cursor;
			}

			string? json = await bot.Web.ApiGetAsync("IEconService", "GetTradeOffers", args, ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(json)) {
				return null;
			}

			using JsonDocument doc = JsonDocument.Parse(json);

			if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("response", out JsonElement response)
				|| (response.ValueKind != JsonValueKind.Object)) {
				// Polled every few minutes by the trading loop - the same odd answer once is plenty.
				Log.DebugOnChange($"offers:{bot.Name}", "trade offers: Steam's answer had no response object", bot.Name);

				return null;
			}

			Dictionary<string, JsonElement> descriptions = Descriptions(response);

			foreach (string list in new[] { "trade_offers_received", "trade_offers_sent" }) {
				if (response.TryGetProperty(list, out JsonElement items) && (items.ValueKind == JsonValueKind.Array)) {
					foreach (JsonElement o in items.EnumerateArray()) {
						// Only live ones, whatever else Steam includes.
						if (Read(o, descriptions) is not { } offer) {
							Log.DebugOnChange($"offerread:{bot.Name}", $"trade offers: skipped offer {Text(o, "tradeofferid")} - couldn't read its id, partner or items", bot.Name);
						} else if (offer.State is Active or NeedsConfirmation or InEscrow) {
							offers.Add(offer);
						}
					}
				}
			}

			cursor = response.TryGetProperty("next_cursor", out JsonElement next) ? Text(response, "next_cursor") : "";

			if ((cursor.Length == 0) || (cursor == "0")) {
				break;
			}
		}

		Log.Recovered($"offers:{bot.Name}");

		return offers;
	}

	/// <summary>One offer by id. Answered is false when Steam didn't answer at all; Offer is null when it answered but
	/// has no such offer for this account.</summary>
	/// <summary>
	/// The items this account has promised in a trade offer still waiting - one it sent, or one it was sent that asks
	/// for them. Null when Steam wouldn't say. Selling or sending one of these breaks that trade: Steam quietly takes
	/// the item out of the offer the moment it moves.
	/// </summary>
	public static async Task<HashSet<ulong>?> PromisedAsync(Bot bot, CancellationToken ct = default) {
		List<Offer>? offers = await ActiveAsync(bot, received: true, sent: true, ct).ConfigureAwait(false);

		return offers == null ? null
			: [.. offers.Where(static o => o.State is Active or NeedsConfirmation).SelectMany(static o => o.Giving).Select(static i => i.AssetId)];
	}

	public static async Task<(bool Answered, Offer? Offer)> OneAsync(Bot bot, ulong id, CancellationToken ct = default) {
		string? json = await bot.Web.ApiGetAsync("IEconService", "GetTradeOffer", new Dictionary<string, string> {
			["tradeofferid"] = id.ToString(CultureInfo.InvariantCulture),
			["get_descriptions"] = "1",
			["language"] = "english"
		}, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return (false, null);
		}

		using JsonDocument doc = JsonDocument.Parse(json);

		if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("response", out JsonElement response)
			|| (response.ValueKind != JsonValueKind.Object) || !response.TryGetProperty("offer", out JsonElement offer)) {
			return (true, null);
		}

		return (true, Read(offer, Descriptions(response)));
	}

	/// <summary>
	/// How long a trade with <paramref name="partner"/> would be held, both sides together. Zero when it would go
	/// through at once; null when Steam didn't say. This is the only way to know BEFORE accepting - an offer's own
	/// hold date is only set once it's already held.
	/// </summary>
	public static async Task<TimeSpan?> HoldAsync(Bot bot, ulong partner, CancellationToken ct = default) {
		string? json = await bot.Web.ApiGetAsync("IEconService", "GetTradeHoldDurations", new Dictionary<string, string> {
			["steamid_target"] = partner.ToString(CultureInfo.InvariantCulture)
		}, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(json)) {
			return null;
		}

		using JsonDocument doc = JsonDocument.Parse(json);

		if ((doc.RootElement.ValueKind != JsonValueKind.Object) || !doc.RootElement.TryGetProperty("response", out JsonElement response)
			|| (response.ValueKind != JsonValueKind.Object)) {
			Log.Debug($"trade hold check: Steam's answer had no response object (partner {partner})", bot.Name);

			return null;
		}

		static long Seconds(JsonElement r, string side) =>
			r.TryGetProperty(side, out JsonElement e) && (e.ValueKind == JsonValueKind.Object)
			&& long.TryParse(Text(e, "escrow_end_duration_seconds"), out long s) ? s : -1;

		long both = Seconds(response, "both_escrow");
		long longest = both >= 0 ? both : Math.Max(Seconds(response, "my_escrow"), Seconds(response, "their_escrow"));

		if (longest < 0) {
			Log.Debug($"trade hold check: Steam's answer had no hold durations (partner {partner})", bot.Name);

			return null;
		}

		return TimeSpan.FromSeconds(longest);
	}

	/// <summary>
	/// Every asset on either side of a live offer - ours and the partner's. Not to be planned, offered or sold again:
	/// the partner can't even see an offer of ours still waiting on our confirmation, so only this side knows.
	/// </summary>
	public static HashSet<ulong> Committed(IEnumerable<Offer> offers) => [.. offers.SelectMany(static o => o.Giving.Concat(o.Receiving)).Select(static i => i.AssetId)];

	/// <summary>Steam's description list, keyed the way an item refers to it: classid_instanceid.</summary>
	private static Dictionary<string, JsonElement> Descriptions(JsonElement response) {
		Dictionary<string, JsonElement> found = new(StringComparer.Ordinal);

		if (response.TryGetProperty("descriptions", out JsonElement list) && (list.ValueKind == JsonValueKind.Array)) {
			foreach (JsonElement d in list.EnumerateArray()) {
				found.TryAdd($"{Text(d, "classid")}_{Text(d, "instanceid")}", d.Clone());
			}
		}

		return found;
	}

	private static Offer? Read(JsonElement o, Dictionary<string, JsonElement> descriptions) {
		if (!ulong.TryParse(Text(o, "tradeofferid"), out ulong id) || (id == 0)
			|| !uint.TryParse(Text(o, "accountid_other"), out uint account) || (account == 0)) {
			return null;
		}

		int.TryParse(Text(o, "trade_offer_state"), out int state);
		long.TryParse(Text(o, "escrow_end_date"), out long escrow);
		int.TryParse(Text(o, "confirmation_method"), out int confirmation);
		bool ours = o.TryGetProperty("is_our_offer", out JsonElement mine) && (mine.ValueKind == JsonValueKind.True);

		// A side that's there but isn't a list is unreadable - never mistaken for "nothing on that side", which
		// would make any offer look like a donation.
		if ((Items(o, "items_to_give", descriptions) is not { } giving) || (Items(o, "items_to_receive", descriptions) is not { } receiving)) {
			return null;
		}

		return new Offer(id, AccountBase + account, state, ours, escrow > 0 ? DateTimeOffset.FromUnixTimeSeconds(escrow).UtcDateTime : null,
			giving, receiving, confirmation);
	}

	private static List<Item>? Items(JsonElement o, string side, Dictionary<string, JsonElement> descriptions) {
		List<Item> items = [];

		if (!o.TryGetProperty(side, out JsonElement list)) {
			return items;   // nothing on that side
		}

		if (list.ValueKind != JsonValueKind.Array) {
			return null;
		}

		foreach (JsonElement i in list.EnumerateArray()) {
			uint.TryParse(Text(i, "appid"), out uint app);
			ulong.TryParse(Text(i, "assetid"), out ulong asset);
			ulong.TryParse(Text(i, "classid"), out ulong classId);
			ulong.TryParse(Text(i, "instanceid"), out ulong instanceId);
			uint amount = uint.TryParse(Text(i, "amount"), out uint n) ? n : 1;
			bool described = descriptions.TryGetValue($"{Text(i, "classid")}_{Text(i, "instanceid")}", out JsonElement d);
			uint game = described && uint.TryParse(Text(d, "market_fee_app"), out uint g) ? g : 0;

			items.Add(new Item(app, Text(i, "contextid"), asset, classId, instanceId, Math.Max(1, amount),
				described ? Text(d, "type") : "", described ? Text(d, "name") : "", game, described, described ? Text(d, "icon_url") : ""));
		}

		return items;
	}

	private static string Text(JsonElement e, string name) => InventoryContents.Text(e, name);
}
