using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Turns gems into booster packs for the games you list - the BoosterCreator plugin for ArchiSteamFarm.
/// </summary>
/// <remarks>
/// Steam lets an account make one booster pack per game a day, for gems, for games it has card drops in. The
/// booster creator page says which games qualify, what each costs, how many gems there are, and when a game
/// made recently can be made again - so the schedule comes from Steam's own answer, and nothing has to be
/// kept on disk. A pack made here is opened by "Open booster packs" if that's on, like any other.
/// </remarks>
public sealed partial class Boosters(Bot bot) : BotModule(bot) {
	/// <summary>One game on the booster creator page.</summary>
	public sealed record Offer(uint AppId, string Name, uint Series, uint Price, bool Unavailable, string AvailableAt);

	/// <summary>What the booster creator page says: gems in total, tradable and not, and the games on offer.</summary>
	public sealed record Page(uint Gems, uint TradableGems, uint UntradableGems, IReadOnlyDictionary<uint, Offer> Offers);

	private readonly Dictionary<uint, DateTime> _next = [];
	private Said _status = new("");

	public override string Name => "boosters";
	public override string Status => Games(Bot).Count > 0 ? _status : "";

	[GeneratedRegex("(?<=parseFloat\\( \")[0-9]+")]
	private static partial Regex GemAmounts();

	[GeneratedRegex("\\[\\{\"[\\s\\S]*\"}]")]
	private static partial Regex OfferList();

	/// <summary>The appIDs in this account's BoosterGames setting.</summary>
	public static List<uint> Games(Bot bot) => [.. bot.Cfg.BoosterGames
		.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
		.Select(static s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0)
		.Where(static id => id != 0)
		.Distinct()];

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			List<uint> games = Games(Bot);

			// Forget games taken off the list; a game added is due straight away.
			foreach (uint gone in _next.Keys.Where(id => !games.Contains(id)).ToList()) {
				_next.Remove(gone);
			}

			DateTime now = DateTime.UtcNow;
			bool due = games.Any(id => !_next.TryGetValue(id, out DateTime at) || (at <= now));

			if (due && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && HumanMode.AwakeFor(Bot)) {
				try {
					await RunDueAsync(games, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't make booster packs: {0}", e.Message), Bot.Name);

					foreach (uint id in games) {
						_next[id] = DateTime.UtcNow.AddHours(1);
					}
				}
			}

			if (games.Count > 0 && _next.Count > 0) {
				DateTime soonest = _next.Values.Min();
				_status = new Said("next booster pack around {0}", (Func<string>) (() => Fmt.Clock(soonest)));
			}

			// A minute at a time, so a change to the list is picked up straight away.
			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private async Task RunDueAsync(List<uint> games, CancellationToken ct) {
		Page? page = await ReadAsync(Bot, ct).ConfigureAwait(false);

		if (page == null) {
			foreach (uint id in games) {
				_next[id] = DateTime.UtcNow.AddHours(1);
			}

			return;
		}

		uint gems = page.Gems, tradable = page.TradableGems, untradable = page.UntradableGems;

		foreach (uint id in games) {
			if (_next.TryGetValue(id, out DateTime at) && (at > DateTime.UtcNow)) {
				continue;
			}

			if (!page.Offers.TryGetValue(id, out Offer? offer)) {
				// No card drops left in it, not owned, or no cards at all. Worth another look later - a game
				// bought today appears once it has drops.
				Log.Debug(new Said("{0} isn't on the booster creator - no booster packs can be made for it right now", GameNames.Of(id)), Bot.Name);
				_next[id] = DateTime.UtcNow.AddHours(8);

				continue;
			}

			if (offer.Unavailable) {
				_next[id] = AvailableAt(offer.AvailableAt);

				continue;
			}

			if (gems < offer.Price) {
				Log.Debug(new Said("not enough gems for a {0} booster pack - {1} needed, {2} here", offer.Name, offer.Price, gems), Bot.Name);
				_next[id] = DateTime.UtcNow.AddHours(8);

				continue;
			}

			await Task.Delay(Rng.Seconds(4, 12), ct).ConfigureAwait(false);
			(bool made, uint left, uint leftTradable, uint leftUntradable, string? why) = await CreateAsync(Bot, offer, tradable, untradable, ct).ConfigureAwait(false);

			if (made) {
				gems = left;
				tradable = leftTradable;
				untradable = leftUntradable;
				Log.Reward(new Said("made a {0} booster pack for {1} gems - {2} gems left", offer.Name, offer.Price, gems), Bot.Name);
				_next[id] = DateTime.UtcNow.AddHours(24).AddMinutes(Rng.Next(1, 30));
			} else {
				Log.Info(new Said("couldn't make a {0} booster pack: {1}", offer.Name, why ?? "-"), Bot.Name);
				_next[id] = DateTime.UtcNow.AddHours(8);
			}
		}
	}

	/// <summary>
	/// Steam writes "available again" in words, e.g. "27 Sep @ 3:45pm", in a time zone of its choosing. Anything
	/// that doesn't read as a sensible time in the next day falls back to trying again in a few hours.
	/// </summary>
	private static DateTime AvailableAt(string text) {
		string[] formats = ["d MMM @ h:mmtt", "MMM d @ h:mmtt", "d MMM, yyyy @ h:mmtt", "MMM d, yyyy @ h:mmtt"];

		if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out DateTime local)) {
			DateTime utc = local.ToUniversalTime().AddMinutes(Rng.Next(1, 15));

			if ((utc > DateTime.UtcNow) && (utc < DateTime.UtcNow.AddHours(25))) {
				return utc;
			}
		}

		return DateTime.UtcNow.AddHours(3);
	}

	/// <summary>Read the booster creator page. Null when it couldn't be read or didn't look as expected.</summary>
	public static async Task<Page?> ReadAsync(Bot bot, CancellationToken ct = default) {
		string? html = await bot.Web.GetAsync(new Uri(WebSession.Community, "/tradingcards/boostercreator?l=english"), ct).ConfigureAwait(false);

		if (html == null) {
			return null;
		}

		MatchCollection amounts = GemAmounts().Matches(html);

		if (amounts.Count < 3) {
			return null;
		}

		Dictionary<uint, Offer> offers = [];
		Match list = OfferList().Match(html);

		if (list.Success) {
			using JsonDocument doc = JsonDocument.Parse(list.Value);

			foreach (JsonElement e in doc.RootElement.EnumerateArray()) {
				uint appId = Num(e, "appid");

				if (appId == 0) {
					continue;
				}

				offers[appId] = new Offer(appId,
					e.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? GameNames.Of(appId) : GameNames.Of(appId),
					Num(e, "series"),
					Num(e, "price"),
					e.TryGetProperty("unavailable", out JsonElement u) && (u.ValueKind == JsonValueKind.True),
					e.TryGetProperty("available_at_time", out JsonElement t) ? t.GetString() ?? "" : "");
			}
		}

		return new Page(Parse(amounts[0].Value), Parse(amounts[1].Value), Parse(amounts[2].Value), offers);
	}

	/// <summary>Make one booster pack.</summary>
	/// <returns>Whether it was made, the gems left afterwards, and Steam's words when it wasn't.</returns>
	public static async Task<(bool Made, uint Gems, uint Tradable, uint Untradable, string? Why)> CreateAsync(
		Bot bot, Offer offer, uint tradable, uint untradable, CancellationToken ct = default) {
		// Spend tradable gems where possible - a pack made from them is tradable too. 1 = tradable gems first,
		// 2 = tradable only (no other kind here), 3 = untradable first. The same choice BoosterCreator makes.
		string preference = untradable > 0 ? (tradable > offer.Price ? "1" : "3") : "2";

		string? answer = await bot.Web.PostAsync(new Uri(WebSession.Community, "/tradingcards/ajaxcreatebooster/"), new Dictionary<string, string> {
			["appid"] = offer.AppId.ToString(CultureInfo.InvariantCulture),
			["series"] = offer.Series.ToString(CultureInfo.InvariantCulture),
			["tradability_preference"] = preference
		}, new Uri(WebSession.Community, "/tradingcards/boostercreator/"), ct).ConfigureAwait(false);

		if (answer == null) {
			return (false, 0, 0, 0, null);
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(answer);
			JsonElement root = doc.RootElement;

			if (root.TryGetProperty("purchase_result", out JsonElement result) && (Num(result, "success") == 1)) {
				return (true, Num(root, "goo_amount"), Num(root, "tradable_goo_amount"), Num(root, "untradable_goo_amount"), null);
			}

			return (false, 0, 0, 0, root.TryGetProperty("purchase_eresult", out JsonElement er) ? $"({er})" : answer.Length > 200 ? answer[..200] : answer);
		} catch (JsonException) {
			return (false, 0, 0, 0, answer.Length > 200 ? answer[..200] : answer);
		}
	}

	/// <summary>Steam writes these numbers sometimes as numbers and sometimes as strings.</summary>
	private static uint Num(JsonElement e, string name) {
		if (!e.TryGetProperty(name, out JsonElement v)) {
			return 0;
		}

		return v.ValueKind switch {
			JsonValueKind.Number => v.TryGetUInt32(out uint n) ? n : 0,
			JsonValueKind.String => Parse(v.GetString()),
			_ => 0
		};
	}

	private static uint Parse(string? s) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) ? n : 0;
}
