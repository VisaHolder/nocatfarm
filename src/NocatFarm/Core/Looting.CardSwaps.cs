using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NocatFarm.Core;

public static partial class Looting {
	/// <summary>1.8.2 card swaps: this account's own trade link token, read from its own trade privacy page. Null when it couldn't be.</summary>
	internal static Task<string?> OwnTradeTokenAsync(Bot bot, CancellationToken ct) => TradeTokenOfAsync(bot.SteamId, ct);

	/// <summary>1.8.2 card swaps: take back a swap offer this account sent. True when Steam says it's done.</summary>
	internal static async Task<bool> CancelSwapAsync(Bot bot, ulong offer, CancellationToken ct) {
		Dictionary<string, string> form = new() { ["sessionid"] = bot.Web.SessionId };
		string? body = await bot.Web.PostAsync(new Uri(WebSession.Community, $"/tradeoffer/{offer}/cancel"), form,
			new Uri(WebSession.Community, "/profiles/" + bot.SteamId + "/tradeoffers/sent/"), ct).ConfigureAwait(false);

		return Modules.Trading.OfferAnswered(body, offer);
	}

	/// <summary>What sending a swap offer came to: its id, and whether it still needs a mobile confirmation or Steam's email.</summary>
	public sealed record SwapSent(bool Ok, ulong OfferId, bool WaitsForPhone, string Message, bool ByEmail = false);

	/// <summary>1.8.2: a swap offer's <see cref="SwapSent.Message"/> when Steam gave no answer - it may have gone all the same.</summary>
	public const string NoAnswer = "Steam didn't answer";

	/// <summary>
	/// 1.8.2 card swaps: one offer to another nocat.farm user's account - these cards of ours for those of theirs, with the
	/// match's note on it so their copy can check it against what nocat.lol paired. Not confirmed here: the sending side
	/// first reads the offer Steam made back and checks it's exactly the match (Modules/CardSwaps.cs), then confirms it.
	/// </summary>
	/// <param name="giving">This account's cards, paired with <paramref name="taking"/> by position (the same game each).</param>
	internal static async Task<SwapSent> SendCardSwapAsync(Bot bot, ulong partner, string token, IReadOnlyList<Item> giving, IReadOnlyList<Item> taking, string note,
		CancellationToken ct) {
		if ((giving.Count == 0) || (giving.Count != taking.Count)) {
			return new SwapSent(false, 0, false, "a swap needs the same number of cards both ways");
		}

		// The token goes into the offer as it is: only ever Steam's own shape.
		if (!CardExchange.TokenOk(token)) {
			return new SwapSent(false, 0, false, "the partner's trade link isn't valid");
		}

		if (Modules.ScamWatch.HeldBack(bot) is { } held) {
			return new SwapSent(false, 0, false, held);
		}

		// 1.8.2: Steam's market blocked for the account (a trade ban, a new device...): no swap goes.
		if (MarketHold.Blocked(bot) is { } market) {
			return new SwapSent(false, 0, false, market.ToEnglish());
		}

		HashSet<ulong> mine = bot.ClaimItems(giving.Select(static i => i.AssetId));

		try {
			if (mine.Count < giving.Select(static i => i.AssetId).Distinct().Count()) {
				return new SwapSent(false, 0, false, "some of those cards are already being sent, sold or swapped");
			}

			if (!bot.IsOnline || !bot.Web.Ready) {
				return new SwapSent(false, 0, false, $"{bot.Name} isn't logged in");
			}

			static string Assets(IEnumerable<Item> items) => string.Join(",", items.Select(static i =>
				string.Create(CultureInfo.InvariantCulture, $"{{\"appid\":{i.App},\"contextid\":\"{i.Context}\",\"amount\":1,\"assetid\":\"{i.AssetId}\"}}")));

			string offer = "{\"newversion\":true,\"version\":2,"
				+ "\"me\":{\"assets\":[" + Assets(giving) + "],\"currency\":[],\"ready\":false},"
				+ "\"them\":{\"assets\":[" + Assets(taking) + "],\"currency\":[],\"ready\":false}}";
			uint partnerAccountId = (uint) (partner & 0xFFFFFFFF);
			Dictionary<string, string> form = new() {
				["sessionid"] = bot.Web.SessionId,
				["serverid"] = "1",
				["partner"] = partner.ToString(CultureInfo.InvariantCulture),
				["tradeoffermessage"] = note,
				["json_tradeoffer"] = offer,
				["captcha"] = "",
				["trade_offer_create_params"] = "{\"trade_offer_access_token\":\"" + token + "\"}"
			};

			Uri referer = new(WebSession.Community, $"/tradeoffer/new/?partner={partnerAccountId}&token={Uri.EscapeDataString(token)}");
			string? body = await bot.Web.PostAllowingFailureAsync(new Uri(WebSession.Community, "/tradeoffer/new/send"), form, referer, ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(body)) {
				return new SwapSent(false, 0, false, NoAnswer);
			}

			try {
				using JsonDocument doc = JsonDocument.Parse(body);

				if (doc.RootElement.TryGetProperty("strError", out JsonElement error)) {
					Log.Debug($"card swap offer refused. Steam said: {Log.Scrub(body)}", bot.Name);

					return new SwapSent(false, 0, false, Explain(error.GetString() ?? "refused"));
				}

				if (!doc.RootElement.TryGetProperty("tradeofferid", out JsonElement idEl)
					|| !ulong.TryParse(idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong offerId)) {
					return new SwapSent(false, 0, false, "Steam refused the offer");
				}

				bool phone = doc.RootElement.TryGetProperty("needs_mobile_confirmation", out JsonElement confirm)
					&& ((confirm.ValueKind == JsonValueKind.True) || ((confirm.ValueKind == JsonValueKind.Number) && (confirm.GetInt32() == 1)));
				bool email = doc.RootElement.TryGetProperty("needs_email_confirmation", out JsonElement mail)
					&& ((mail.ValueKind == JsonValueKind.True) || ((mail.ValueKind == JsonValueKind.Number) && (mail.GetInt32() == 1)));

				return new SwapSent(true, offerId, phone, "sent", email);
			} catch (JsonException) {
				Log.Debug($"card swap offer: Steam's answer wasn't JSON: {Log.Scrub(body[..Math.Min(150, body.Length)])}", bot.Name);

				return new SwapSent(false, 0, false, "Steam refused the offer");
			}
		} finally {
			bot.ReleaseItems(mine);
		}
	}
}
