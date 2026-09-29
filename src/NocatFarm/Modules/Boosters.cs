using System.Globalization;
using System.Text;
using System.Text.Json;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Turns gems into booster packs for the games you list.
/// </summary>
/// <remarks>
/// Steam lets an account make one booster pack per game a day, for gems, for games it can still get card drops in.
/// The booster creator page lists the games that qualify, what each costs and how many gems the account holds, so
/// that page is read and nothing about Steam's rules is hard-coded here. When a pack is made the next one for that
/// game is simply due a day later; a game that isn't ready yet is looked at again in a few hours rather than trying
/// to read the "available again" wording Steam writes for people. A pack made here is opened by "Open booster packs"
/// if that's on, like any other.
/// </remarks>
public sealed class Boosters(Bot bot) : BotModule(bot) {
	/// <summary>One game on the booster creator page.</summary>
	public sealed record Offer(uint AppId, string Name, uint Series, uint Price, bool Unavailable, string AvailableAt);

	/// <summary>What the booster creator page says: gems in total, tradable and not, and the games on offer.</summary>
	public sealed record Page(uint Gems, uint TradableGems, uint UntradableGems, IReadOnlyDictionary<uint, Offer> Offers);

	/// <summary>When each listed game is next worth trying.</summary>
	private readonly Dictionary<uint, DateTime> _next = [];
	private Said _status = new("");

	public override string Name => "boosters";
	public override string Status => Games(Bot).Count > 0 ? _status : "";

	/// <summary>The appIDs in this account's BoosterGames setting.</summary>
	public static List<uint> Games(Bot bot) => [.. bot.Cfg.BoosterGames
		.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
		.Select(static s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0)
		.Where(static id => id != 0)
		.Distinct()];

	private static DateTime Later(int minHours, int maxHours) => DateTime.UtcNow.AddMinutes(Rng.Next(minHours * 60, (maxHours * 60) + 1));

	private HumanGate? _gate;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			List<uint> games = Games(Bot);

			// Forget games taken off the list; a game added is due straight away.
			foreach (uint gone in _next.Keys.Where(id => !games.Contains(id)).ToList()) {
				_next.Remove(gone);
			}

			DateTime now = DateTime.UtcNow;
			bool due = games.Any(id => !_next.TryGetValue(id, out DateTime at) || (at <= now));

			// Nobody sees a pack being made, so any time of day - one at a time with a gap, not the moment it signs in.
			_gate ??= HumanGate.Quiet(Bot);

			if (due && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && _gate.Open) {
				try {
					List<uint> batch = Bot.Cfg.LegitMode ? [games.First(id => !_next.TryGetValue(id, out DateTime at) || (at <= now))] : games;
					await MakeDueAsync(batch, ct).ConfigureAwait(false);
					_gate.Space(15, 90);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't make booster packs: {0}", Log.Describe(e)), Bot.Name);

					foreach (uint id in games) {
						_next[id] = Later(1, 2);
					}
				}
			}

			if ((games.Count > 0) && (_next.Count > 0)) {
				DateTime soonest = _next.Values.Min();
				_status = new Said("next booster pack around {0}", (Func<string>) (() => Fmt.Clock(soonest)));
			}

			// A minute at a time, so a change to the list is picked up straight away.
			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private async Task MakeDueAsync(List<uint> games, CancellationToken ct) {
		Page? page = await ReadAsync(Bot, ct).ConfigureAwait(false);

		if (page == null) {
			foreach (uint id in games) {
				_next[id] = Later(1, 2);
			}

			return;
		}

		uint gems = page.Gems, tradable = page.TradableGems, untradable = page.UntradableGems;

		foreach (uint id in games) {
			if (_next.TryGetValue(id, out DateTime at) && (at > DateTime.UtcNow)) {
				continue;
			}

			// Not on the page: no drops left in it, not owned, or no cards at all. A game bought today shows up once it
			// has drops, so it's looked at again later rather than given up on.
			if (!page.Offers.TryGetValue(id, out Offer? offer)) {
				Log.Debug(new Said("{0} isn't on the booster creator - no booster packs can be made for it right now", GameNames.Of(id)), Bot.Name);
				_next[id] = Later(6, 10);

				continue;
			}

			// Made in the last day - by us or by hand. Looked at again in a few hours, which a page read a few times a
			// day costs nothing to find out.
			if (offer.Unavailable) {
				_next[id] = Later(2, 4);

				continue;
			}

			if (gems < offer.Price) {
				Log.Debug(new Said("not enough gems for a {0} booster pack - {1} needed, {2} here", offer.Name, offer.Price, gems), Bot.Name);
				_next[id] = Later(6, 10);

				continue;
			}

			await Task.Delay(Rng.Seconds(4, 12), ct).ConfigureAwait(false);
			(bool made, uint left, uint leftTradable, uint leftUntradable, string? why) = await CreateAsync(Bot, offer, tradable, untradable, ct).ConfigureAwait(false);

			if (made) {
				gems = left;
				tradable = leftTradable;
				untradable = leftUntradable;
				Log.Reward(new Said("made a {0} booster ({1} gems, {2} left)", offer.Name, offer.Price, gems), Bot.Name);

				// One a day per game, counted from now - with a little slack so it doesn't land on the same minute daily.
				_next[id] = DateTime.UtcNow.AddHours(24).AddMinutes(Rng.Next(5, 45));
			} else {
				Log.Info(new Said("couldn't make a {0} booster pack: {1}", offer.Name, why ?? "-"), Bot.Name);
				_next[id] = Later(6, 10);
			}
		}
	}

	// ── reading the page ────────────────────────────────────────────────────
	/// <summary>Read the booster creator page. Null when it couldn't be read or didn't look as expected.</summary>
	public static async Task<Page?> ReadAsync(Bot bot, CancellationToken ct = default) {
		string? html = await bot.Web.GetAsync(new Uri(WebSession.Community, "/tradingcards/boostercreator?l=english"), ct).ConfigureAwait(false);

		if (html == null) {
			return null;   // the web session already logged why
		}

		Page? page = ParsePage(html);

		// A sign-in page, or Steam changing the page's script, reads the same as "nothing to make" otherwise.
		if (page == null) {
			Log.Debug($"couldn't read the booster creator page: no CBoosterCreatorPage.Init data in it ({html.Length} chars)", bot.Name);
		}

		return page;
	}

	/// <summary>
	/// The page hands everything to its own script in one call - the games as a JSON array, then the gem counts in
	/// total, tradable and untradable. That call's arguments are read here as they are written, rather than
	/// fishing numbers out of the markup around them.
	/// </summary>
	internal static Page? ParsePage(string html) {
		const string Call = "CBoosterCreatorPage.Init(";
		int at = html.IndexOf(Call, StringComparison.Ordinal);

		if (at < 0) {
			return null;
		}

		List<string> args = Arguments(html, at + Call.Length);

		if (args.Count < 4) {
			return null;
		}

		Dictionary<uint, Offer> offers = [];

		try {
			using JsonDocument doc = JsonDocument.Parse(args[0]);

			if (doc.RootElement.ValueKind == JsonValueKind.Array) {
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
		} catch (JsonException) {
			return null;
		}

		return new Page(Digits(args[1]), Digits(args[2]), Digits(args[3]), offers);
	}

	/// <summary>
	/// The top-level arguments of a script call starting just after its "(", up to the matching ")". Commas inside
	/// strings, brackets and nested calls belong to the argument they sit in.
	/// </summary>
	private static List<string> Arguments(string text, int start) {
		List<string> args = [];
		StringBuilder current = new();
		int depth = 0;
		char quote = '\0';

		for (int i = start; i < text.Length; i++) {
			char c = text[i];

			if (quote != '\0') {
				current.Append(c);

				if (c == '\\' && (i + 1 < text.Length)) {
					current.Append(text[++i]);
				} else if (c == quote) {
					quote = '\0';
				}

				continue;
			}

			switch (c) {
				case '"' or '\'':
					quote = c;
					current.Append(c);

					break;
				case '(' or '[' or '{':
					depth++;
					current.Append(c);

					break;
				case ')' or ']' or '}' when depth > 0:
					depth--;
					current.Append(c);

					break;
				case ')':
					args.Add(current.ToString().Trim());

					return args;
				case ',' when depth == 0:
					args.Add(current.ToString().Trim());
					current.Clear();

					break;
				default:
					current.Append(c);

					break;
			}
		}

		return [];   // never closed - not the page we expected
	}

	/// <summary>The whole number in an argument like <c>parseFloat( "1234" )</c>.</summary>
	private static uint Digits(string arg) {
		StringBuilder digits = new();

		foreach (char c in arg) {
			if (char.IsAsciiDigit(c)) {
				digits.Append(c);
			} else if ((c == '.') && (digits.Length > 0)) {
				break;   // gems are whole numbers; anything after a point is not
			}
		}

		return Parse(digits.ToString());
	}

	// ── making one ──────────────────────────────────────────────────────────
	/// <summary>Make one booster pack.</summary>
	/// <returns>Whether it was made, the gems left afterwards, and Steam's words when it wasn't.</returns>
	public static async Task<(bool Made, uint Gems, uint Tradable, uint Untradable, string? Why)> CreateAsync(
		Bot bot, Offer offer, uint tradable, uint untradable, CancellationToken ct = default) {
		string? answer = await bot.Web.PostAsync(new Uri(WebSession.Community, "/tradingcards/ajaxcreatebooster/"), new Dictionary<string, string> {
			["appid"] = offer.AppId.ToString(CultureInfo.InvariantCulture),
			["series"] = offer.Series.ToString(CultureInfo.InvariantCulture),
			["tradability_preference"] = GemsToSpend(bot, offer.Price, tradable, untradable)
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

			return (false, 0, 0, 0, root.TryGetProperty("purchase_eresult", out JsonElement er) ? $"({er})" : Log.Scrub(answer.Length > 200 ? answer[..200] : answer));
		} catch (JsonException) {
			return (false, 0, 0, 0, Log.Scrub(answer.Length > 200 ? answer[..200] : answer));   // the caller logs it
		}
	}

	/// <summary>
	/// Which gems Steam should take, in its own terms: 1 tradable ones first, 3 untradable ones first, 2 when there's
	/// only one kind anyway. A pack made from tradable gems is tradable itself - worth keeping them for that unless
	/// the account's BoosterGems setting says to use the untradable ones up first. If the preferred kind can't cover
	/// the price, the other is used rather than failing.
	/// </summary>
	private static string GemsToSpend(Bot bot, uint price, uint tradable, uint untradable) {
		if ((tradable == 0) || (untradable == 0)) {
			return "2";
		}

		bool untradableFirst = bot.Cfg.BoosterGems == 1;

		return untradableFirst
			? (untradable >= price ? "3" : "1")
			: (tradable >= price ? "1" : "3");
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
