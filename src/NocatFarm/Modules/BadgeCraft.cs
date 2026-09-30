using System.Globalization;
using System.Text.Json;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Turns completed card sets into badges, which is what actually raises your Steam level - farming cards and
/// then leaving the sets sitting in the inventory earns the level nothing.
///
/// Two things about this are load-bearing:
///
/// 1. The craftable state is ONLY visible to the logged-in owner. The public badges page contains no craft
///    markup at all, so this has to go through the account's own session.
/// 2. Steam rate-limits crafting hard, and the limit EXTENDS if you keep knocking. So: read the page once,
///    craft everything it listed, and never re-read after each craft. Steam's own UI reloads every time -
///    do not copy that.
/// </summary>
public sealed class BadgeCraft(Bot bot) : BotModule(bot) {
	private const int SweepLowHours = 22;
	private const int SweepHighHours = 26;
	private const int CraftGapLowSeconds = 6;
	private const int CraftGapHighSeconds = 14;
	private const int UnpackGapLowSeconds = 3;
	private const int UnpackGapHighSeconds = 12;
	private const int BackoffHours = 6;
	private const int MaxBadgePages = 20;

	private Said _status = new("off");
	private int _crafted;

	public override string Name => "badges";
	public override string Status => _status;

	private DateTime _nextSweep = DateTime.MinValue;
	private HumanGate? _gate;

	/// <summary>The last sweep left sets uncrafted on purpose (human mode crafts a few at a time) - look again sooner.</summary>
	private bool _moreToCraft;

	/// <summary>A booster pack Steam didn't say it opened this sweep - look again in a few hours, not tomorrow.</summary>
	private bool _unpackFailed;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			// Either switch runs the daily pass. Opening booster packs used to happen only inside a badge-crafting
			// pass, so turning on "Open booster packs" by itself - which is what its own description invites -
			// never opened a thing.
			if (!Bot.Cfg.CraftBadges && !Bot.Cfg.UnpackBoosterPacks) {
				_status = new Said("off");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (Bot.Paused) {
				_status = new Said("paused");

				if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			if (!Bot.IsOnline || !Bot.Web.Ready) {
				_status = new Said("waiting for the account");

				if (!await Sleep(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			// Not the moment it signs in, and not again on every reconnect - the due time lives on the module, not in
			// this loop.
			if (_nextSweep == DateTime.MinValue) {
				_nextSweep = DateTime.UtcNow + Rng.Minutes(20, 90);
			}

			// A badge crafted shows on the profile and in its activity with the time, so a human-mode account crafts in
			// its own day - not at 4am while it's asleep.
			_gate ??= HumanGate.OwnDay(Bot);

			if ((DateTime.UtcNow < _nextSweep) || !_gate.Open) {
				if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			TimeSpan wait = Rng.Minutes(SweepLowHours * 60, SweepHighHours * 60);

			try {
				int made = await SweepAsync(ct).ConfigureAwait(false);

				if (made < 0) {
					wait = Rng.Minutes(BackoffHours * 50, BackoffHours * 80);   // Steam refused - back off rather than knock again
				} else if (_moreToCraft || _unpackFailed) {
					wait = Rng.HumanMinutes(2 * 60, 6 * 60);   // the rest of the sets later on, not tomorrow
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Warn(new Said("badge sweep failed: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);

				// where it broke, once per new kind of failure - the message says what, only the stack says where
				if (Log.DebugOnChange($"hiccup:{Name}:{Bot.Name}", $"{Name}: {Log.Describe(e)}", Bot.Name)) {
					Log.StackToFile(e, Bot.Name);
				}
			}

			// A minute at a time, so switching either setting takes effect straight away rather than after the day's
			// wait - turning badge crafting on in booster-only mode used to sit out the full 22-26 hours first.
			DateTime due = DateTime.UtcNow + wait;
			_nextSweep = due;
			bool crafting = Bot.Cfg.CraftBadges;

			while (DateTime.UtcNow < due) {
				TimeSpan left = due - DateTime.UtcNow;

				if (!await Sleep(left < TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				if ((Bot.Cfg.CraftBadges != crafting) || (!Bot.Cfg.CraftBadges && !Bot.Cfg.UnpackBoosterPacks)) {
					// A fresh short wait from here. The day's wait was already set, so breaking out alone went straight
					// back to sleeping it out at the top of the loop.
					_nextSweep = DateTime.MinValue;

					break;
				}
			}
		}
	}

	/// <summary>
	/// Open any booster packs sitting in the inventory. A sealed pack is three cards missing from the set count,
	/// so leaving them shut quietly weakens the crafting this module exists to do.
	/// </summary>
	private async Task<int> UnpackBoostersAsync(CancellationToken ct) {
		// All of it, not the first page. Asking for 200 items meant that on an account holding 914 Steam items -
		// cards, backgrounds, emoticons - a booster pack anywhere past the two-hundredth was never seen at all.
		InventoryContents? inventory = await Inventory.ReadAsync(Bot, 753, "6", ct).ConfigureAwait(false);

		if (inventory == null) {
			return 0;
		}

		List<(string App, ulong Asset)> packs = Packs(inventory, []);

		if (packs.Count == 0) {
			return 0;
		}

		// A pack in a trade offer that's waiting - a timed send of boosters to your main, say - isn't this account's to
		// open: opening it takes it out of the offer, and Steam drops the whole offer. Not knowing is a reason to wait, as
		// it is for a sale or a craft.
		if (await TradeOffers.PromisedAsync(Bot, ct).ConfigureAwait(false) is not { } promised) {
			Log.Debug(new Said("Steam wouldn't say which booster packs are in a waiting trade - opening them later"), Bot.Name);
			_unpackFailed = true;

			return 0;
		}

		int opened = 0;

		foreach ((string appId, ulong asset) in Packs(inventory, promised)) {
			ct.ThrowIfCancellationRequested();

			// Claimed like a card for a send or a sale (Bot.ClaimItems): a send reading the inventory this moment could
			// otherwise put the pack in its offer just as it's opened.
			HashSet<ulong> claimed = Bot.ClaimItems([asset]);

			if (claimed.Count == 0) {
				continue;   // being sent right now
			}

			string assetId = asset.ToString(CultureInfo.InvariantCulture);
			string? body;

			try {
				body = await Bot.Web.PostAsync(
					new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/ajaxunpackbooster/"),
					new Dictionary<string, string>(StringComparer.Ordinal) {
						["appid"] = appId,
						["communityitemid"] = assetId
					},
					new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/inventory/"), ct).ConfigureAwait(false);
			} finally {
				Bot.ReleaseItems(claimed);
			}

			// Only Steam's yes counts - "success":1 or true, or the cards it hands over. Anything else, a numeric failure
			// code included, used to be counted as opened; now it's written down and the pack is tried again later.
			if (Succeeded(body, "rgItems")) {
				opened++;
			} else {
				_unpackFailed = true;
				Log.Debug($"opening booster pack {assetId} (app {appId}) didn't go through: {(body == null ? "no answer" : Log.Scrub(body.Length > 150 ? body[..150] : body))}", Bot.Name);
			}

			if (!await Sleep(Rng.Seconds(UnpackGapLowSeconds, UnpackGapHighSeconds), ct).ConfigureAwait(false)) {
				return opened;
			}
		}

		if (opened > 0) {
			Log.Reward(new Said("opened {0} booster pack(s)", opened), Bot.Name);
		}

		return opened;
	}

	/// <summary>
	/// One sweep. Returns how many badges were crafted, or -1 if Steam refused and we should back off.
	/// </summary>
	public async Task<int> SweepAsync(CancellationToken ct) {
		// Only this sweep says whether there's more to craft. Left over from the last one, a sweep that found the badges
		// page unreadable or nothing ready at all still came back in 2-6 hours rather than a day - and every one after it.
		_moreToCraft = false;
		_unpackFailed = false;

		if (Bot.Cfg.UnpackBoosterPacks) {
			_status = new Said("opening booster packs");

			try {
				await UnpackBoostersAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception e) {
				Log.Debug(new Said("booster unpack: {0}", Log.Describe(e)), Bot.Name);
			}
		}

		if (!Bot.Cfg.CraftBadges) {
			_status = new Said("opens booster packs once a day - badge crafting is off");

			return 0;
		}

		_status = new Said("checking for completed sets");

		string? html = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/badges/?l=english&p=1"), ct).ConfigureAwait(false);

		if (html == null) {
			_status = new Said("couldn't read the badges page");

			return 0;
		}

		List<Craftable> ready = Parse(html);
		List<(uint App, bool Foil)> linked = ReadyLinks(html);

		// Steam paginates at ~60 badges. Reading only page 1 meant a big library never crafted anything past it.
		int pages = Math.Min(MaxBadgePages, CardFarmer.ParseMaxPages(html));

		for (int page = 2; page <= pages; page++) {
			await Task.Delay(Rng.Seconds(3, 10), ct).ConfigureAwait(false);   // a person pages through, not all at once

			string? more = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/badges/?l=english&p={page}"), ct).ConfigureAwait(false);

			if (more == null) {
				break;
			}

			foreach (Craftable c in Parse(more)) {
				if (!ready.Exists(existing => SameSet(existing, c.AppId, c.Border > 0))) {
					ready.Add(c);
				}
			}

			linked.AddRange(ReadyLinks(more));
		}

		// The badges list only LINKS a finished set's craft button to the game's card page; the numbers the craft needs
		// are on that page, in Profile_CraftGameBadge(...). Looking for them on the list alone found nothing to craft,
		// ever. Each set still to read costs one page, a few seconds apart.
		foreach ((uint app, bool foil) in CardPagesToRead(linked, ready)) {
			await Task.Delay(Rng.Seconds(3, 10), ct).ConfigureAwait(false);

			string? cards = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/gamecards/{app}/?l=english" + (foil ? "&border=1" : "")), ct).ConfigureAwait(false);

			if ((cards != null) && (ParseCardsPage(cards) is { } c) && (c.AppId == app)) {
				if (!ready.Contains(c)) {
					ready.Add(c);
				}
			} else if (cards != null) {
				// The list said this set is ready; its page not offering the craft is worth knowing about.
				Log.Debug($"{GameNames.Of(app)}: the card page has no craft button to read ({cards.Length} chars) - left for the next sweep", Bot.Name);
			}
		}

		if (ready.Count == 0) {
			_status = new Said("nothing ready to craft");

			return 0;
		}

		Log.Info(new Said("{0} completed card set(s) ready to craft", ready.Count), Bot.Name);
		int made = 0;

		// A human-mode account crafts a few and gets on with its day - the rest wait for a later sweep. Twenty badges
		// a few seconds apart is a burst nobody makes by hand.
		int most = Bot.Cfg.LegitMode ? Rng.Next(1, 4) : ready.Count;
		_moreToCraft = ready.Count > most;
		ready = [.. ready.Take(most)];

		// The cards first, claimed the way a send or a sale claims them (Bot.ClaimItems). A craft eats one of each card
		// of the set, and a send or a listing running at the same moment picked from the same inventory: the offer
		// went out with cards the craft had just used up, and Steam dropped the whole offer. Whoever claims first uses
		// them; the other leaves them out.
		InventoryContents? inventory = await Inventory.ReadAsync(Bot, 753, "6", ct).ConfigureAwait(false);

		// All of it or nothing: a copy on a page that didn't load can't be claimed, and could be the one the craft eats.
		if (inventory is not { Complete: true }) {
			_status = new Said("couldn't read the inventory - crafting later");
			_moreToCraft = true;

			return 0;
		}

		// By set, not by game: a game's foil set and its plain one can both be ready, and are different cards.
		Dictionary<Craftable, HashSet<ulong>> held = [];

		try {
			foreach (Craftable c in ready) {
				List<ulong> cards = SetCards(inventory, c.AppId, c.Border > 0);

				// Steam can't be told which copy to use, so a set is crafted only with every copy of every card in it
				// held - one it doesn't hold could be the one the craft eats.
				if (cards.Count == 0) {
					Log.Debug(new Said("{0}: its cards aren't in the inventory yet - crafted later", GameNames.Of(c.AppId)), Bot.Name);
					_moreToCraft = true;

					continue;
				}

				if (ClaimSet(Bot, cards) is not { } claimed) {
					Log.Info(new Said("{0}: its cards are being sent or sold right now - crafted later", GameNames.Of(c.AppId)), Bot.Name);
					_moreToCraft = true;

					continue;
				}

				held[c] = claimed;
			}

			// Read after claiming, like a send: a send or a sale that already made its offer has let go of its claim by
			// now, so its cards show up here as promised instead. Crafting one of them would break that trade. Not
			// knowing is a reason to wait, as it is for a sale - a craft can always happen tomorrow.
			if (held.Count > 0) {
				HashSet<ulong>? promised = await TradeOffers.PromisedAsync(Bot, ct).ConfigureAwait(false);

				foreach ((Craftable c, HashSet<ulong> cards) in held.Where(h => (promised == null) || h.Value.Overlaps(promised)).ToList()) {
					Bot.ReleaseItems(cards);
					held.Remove(c);
					_moreToCraft = true;

					if (promised != null) {
						Log.Info(new Said("{0}: its cards are in a trade offer that's waiting - crafted later", GameNames.Of(c.AppId)), Bot.Name);
					}
				}

				if (promised == null) {
					Log.Debug(new Said("Steam wouldn't say which cards are in a waiting trade - crafting later"), Bot.Name);
				}
			}

			ready = [.. ready.Where(held.ContainsKey)];

			// Deliberately NOT re-reading the page between crafts: the list we already have is accurate, and every
			// extra request during a craft run is what pushes Steam into extending the rate limit.
			foreach (Craftable c in ready) {
				ct.ThrowIfCancellationRequested();
				_status = new Said("crafting ({0}/{1})", made, ready.Count);

				if (!await CraftAsync(c, ct).ConfigureAwait(false)) {
					Log.Warn(new Said("craft refused - backing off {0}h rather than retrying", BackoffHours), Bot.Name);
					_status = new Said("rate-limited, backing off");

					return made > 0 ? made : -1;
				}

				made++;
				_crafted++;
				Stats.Record(Stats.KindBadge, Bot.Name);

				bool more = await Sleep(Rng.Seconds(CraftGapLowSeconds, CraftGapHighSeconds), ct).ConfigureAwait(false);

				// Handed back after the gap, not the moment it's crafted: a send that read the inventory just before the
				// craft still lists the copies it ate, and finding them claimed a few seconds longer keeps them out of its
				// offer. Then straight back, so a send isn't kept from this set's spare copies for the rest of the run.
				Bot.ReleaseItems(held[c]);
				held.Remove(c);

				if (!more) {
					break;
				}
			}
		} finally {
			// Whatever way it ends - a refusal, a shutdown, an error - no card stays claimed by a craft that isn't running.
			foreach (HashSet<ulong> cards in held.Values) {
				Bot.ReleaseItems(cards);
			}
		}

		if (made > 0) {
			Log.Reward(new Said("crafted {0} badge(s) - Steam level up", made), Bot.Name);
		}

		_status = new Said("{0} crafted since start", _crafted);

		return made;
	}

	private async Task<bool> CraftAsync(Craftable c, CancellationToken ct) {
		Dictionary<string, string> form = new(StringComparer.Ordinal) {
			["appid"] = c.AppId.ToString(CultureInfo.InvariantCulture),
			["series"] = c.Series.ToString(CultureInfo.InvariantCulture),
			["border_color"] = c.Border.ToString(CultureInfo.InvariantCulture),
			["levels"] = Math.Max(1, c.Levels).ToString(CultureInfo.InvariantCulture)
			// sessionid is injected by WebSession
		};

		string? body = await Bot.Web.PostAsync(
			new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/ajaxcraftbadge/"),
			form,
			new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/badges/"),
			ct).ConfigureAwait(false);

		if (body == null) {
			return false;   // the web session already logged why
		}

		if (!Succeeded(body, "rgDroppedItems")) {
			// The caller only says "refused" - Steam's own answer is the why. Only an explicit yes is a craft: "success":2 and
			// every other failure code used to pass as one, and the set was counted as a badge it never became.
			Log.Debug($"crafting the {GameNames.Of(c.AppId)} badge refused: {Log.Scrub(body.Length > 150 ? body[..150] : body)}", Bot.Name);

			return false;
		}

		return true;
	}

	/// <summary>
	/// Whether Steam said yes: "success" of 1 or true - or, with no success flag at all, the result it only sends when it
	/// worked (<paramref name="resultField"/>). A number other than 1, false, an HTML page or nothing at all is a no.
	/// </summary>
	internal static bool Succeeded(string? body, string resultField) {
		if (string.IsNullOrWhiteSpace(body)) {
			return false;
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;

			if (root.ValueKind != JsonValueKind.Object) {
				return false;
			}

			if (root.TryGetProperty("success", out JsonElement s)) {
				return (s.ValueKind == JsonValueKind.True) || ((s.ValueKind == JsonValueKind.Number) && s.TryGetInt32(out int n) && (n == 1));
			}

			return root.TryGetProperty(resultField, out _);
		} catch (JsonException) {
			return false;
		}
	}

	internal readonly record struct Craftable(uint AppId, int Series, int Border, int Levels);

	/// <summary>
	/// Every copy of this game's cards in the inventory - foil or plain, whichever the set is. Asset ids, as a send claims.
	/// </summary>
	internal static List<ulong> SetCards(InventoryContents inventory, uint app, bool foil) {
		string game = app.ToString(CultureInfo.InvariantCulture);
		List<ulong> cards = [];

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } description) {
				continue;
			}

			string type = InventoryContents.Text(description, "type");

			// "Portal 2 Trading Card" or "Portal 2 Foil Trading Card" - the other kind is a different set.
			if (!type.Contains("Trading Card", StringComparison.Ordinal) || (type.Contains("Foil Trading Card", StringComparison.Ordinal) != foil)
				|| (InventoryContents.Text(description, "market_fee_app") != game)) {
				continue;
			}

			if (ulong.TryParse(InventoryContents.Text(asset, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && !cards.Contains(id)) {
				cards.Add(id);
			}
		}

		return cards;
	}

	/// <summary>
	/// The sealed booster packs in the inventory, as (game, asset id) - leaving out the ones in <paramref name="promised"/>,
	/// a trade offer that's waiting.
	/// </summary>
	/// <remarks>
	/// Read as data rather than walked as text. The old version searched the raw JSON for a classid and walked backwards to
	/// the nearest "assetid" - but the same classid also appears in the descriptions section, and walking back from there
	/// landed on whichever asset happened to be listed last.
	/// </remarks>
	internal static List<(string App, ulong Asset)> Packs(InventoryContents inventory, ICollection<ulong> promised) {
		List<(string, ulong)> packs = [];

		foreach (JsonElement asset in inventory.Assets) {
			if (inventory.DescriptionOf(asset) is not { } description) {
				continue;
			}

			string type = InventoryContents.Text(description, "type");
			string name = InventoryContents.Text(description, "name");

			if (!type.Contains("Booster Pack", StringComparison.Ordinal) && !name.EndsWith("Booster Pack", StringComparison.Ordinal)) {
				continue;
			}

			string appId = InventoryContents.Text(description, "market_fee_app");

			if ((appId.Length == 0) || !ulong.TryParse(InventoryContents.Text(asset, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
				|| (id == 0) || promised.Contains(id)) {
				continue;
			}

			packs.Add((appId, id));
		}

		return packs;
	}

	/// <summary>
	/// Claim all of a set's cards, or none: a send or a sale already holding one of them means this set waits. What was
	/// claimed on the way is handed straight back, so it isn't kept from that send for nothing.
	/// </summary>
	internal static HashSet<ulong>? ClaimSet(Bot bot, IReadOnlyCollection<ulong> cards) {
		HashSet<ulong> claimed = bot.ClaimItems(cards);

		if (claimed.Count == cards.Count) {
			return claimed;
		}

		bot.ReleaseItems(claimed);

		return null;
	}

	/// <summary>
	/// Every craftable set on the page. Steam renders a craft button whose onclick is
	/// <c>CraftBadge( appid, series, border, levels )</c> - the numbers live in the MARKUP, not the text, so
	/// this reads attributes rather than rendered content.
	/// </summary>
	internal static List<Craftable> Parse(string html) {
		List<Craftable> found = [];
		int i = 0;

		while (true) {
			int at = html.IndexOf("CraftBadge", i, StringComparison.OrdinalIgnoreCase);

			if (at < 0) {
				return found;
			}

			i = at + "CraftBadge".Length;
			int open = html.IndexOf('(', at);
			int close = open < 0 ? -1 : html.IndexOf(')', open);

			if ((open < 0) || (close < 0) || (close - open > 200)) {
				continue;
			}

			List<int> nums = Numbers(html[(open + 1)..close]);

			if ((nums.Count == 0) || (nums[0] <= 0)) {
				continue;
			}

			uint appId = (uint) nums[0];
			int border = nums.Count >= 3 ? nums[2] : 0;

			if (found.Exists(f => SameSet(f, appId, border > 0))) {
				continue;
			}

			found.Add(new Craftable(
				appId,
				nums.Count >= 2 ? nums[1] : 1,
				border,
				nums.Count >= 4 ? nums[3] : 1));
		}
	}

	/// <summary>
	/// The same set: the same game AND the same kind, foil or plain. A game's foil set and its plain one are two badges
	/// with two craft buttons.
	/// </summary>
	/// <remarks>
	/// Told apart by game alone, a game with both ready crafted one of them and left the other for the next day's sweep -
	/// the foil link was dropped as a repeat of the plain one.
	/// </remarks>
	private static bool SameSet(Craftable c, uint app, bool foil) => (c.AppId == app) && ((c.Border > 0) == foil);

	/// <summary>The card pages still to read: every set the list links to, foil and plain apart, that isn't read already.</summary>
	internal static List<(uint App, bool Foil)> CardPagesToRead(IEnumerable<(uint App, bool Foil)> linked, List<Craftable> ready) =>
		[.. linked.Where(l => !ready.Exists(r => SameSet(r, l.App, l.Foil))).Distinct().Take(MaxCardPages)];

	/// <summary>Card pages read per sweep, at most - each finished set the list only links to is one page.</summary>
	private const int MaxCardPages = 25;

	/// <summary>The games whose craft button on the badges list links to their card page: /gamecards/&lt;appid&gt;/, foil with border=1.</summary>
	internal static List<(uint App, bool Foil)> ReadyLinks(string html) {
		List<(uint, bool)> found = [];
		int i = 0;

		while (true) {
			int at = html.IndexOf("badge_craft_button", i, StringComparison.Ordinal);

			if (at < 0) {
				return found;
			}

			i = at + "badge_craft_button".Length;

			// The link sits on the button's own tag or just inside it - never further back, where the row before's links are.
			int from = Math.Max(0, html.LastIndexOf('<', at));
			string near = html[from..Math.Min(html.Length, at + 300)];
			int link = near.IndexOf("/gamecards/", StringComparison.Ordinal);

			if (link < 0) {
				continue;
			}

			int digits = link + "/gamecards/".Length;
			int end = digits;

			while ((end < near.Length) && char.IsAsciiDigit(near[end])) {
				end++;
			}

			if (uint.TryParse(near.AsSpan(digits, end - digits), NumberStyles.None, CultureInfo.InvariantCulture, out uint app) && (app > 0)) {
				int quote = near.IndexOfAny(['"', '\''], end);
				bool foil = (quote > end) && near[end..quote].Contains("border=1", StringComparison.Ordinal);
				found.Add((app, foil));
			}
		}
	}

	/// <summary>
	/// The craft a game's card page offers: Profile_CraftGameBadge( profile, appid, series, border, levels ). The first
	/// argument is the profile's address - which can hold a SteamID's digits - so the numbers are read after it.
	/// </summary>
	internal static Craftable? ParseCardsPage(string html) {
		int at = html.IndexOf("Profile_CraftGameBadge(", StringComparison.Ordinal);

		if (at < 0) {
			return null;
		}

		int open = at + "Profile_CraftGameBadge".Length;
		int close = html.IndexOf(')', open);

		if ((close < 0) || (close - open > 300)) {
			return null;
		}

		string[] args = html[(open + 1)..close].Split(',');

		if (args.Length < 5) {
			return null;
		}

		List<int> nums = Numbers(string.Join(',', args[1..]));

		return (nums.Count >= 4) && (nums[0] > 0) ? new Craftable((uint) nums[0], nums[1], nums[2], Math.Max(1, nums[3])) : null;
	}

	private static List<int> Numbers(string s) {
		List<int> nums = [];
		int i = 0;

		while (i < s.Length) {
			if (!char.IsAsciiDigit(s[i])) {
				i++;

				continue;
			}

			int start = i;

			while ((i < s.Length) && char.IsAsciiDigit(s[i])) {
				i++;
			}

			if (int.TryParse(s.AsSpan(start, i - start), out int v)) {
				nums.Add(v);
			}
		}

		return nums;
	}
}
