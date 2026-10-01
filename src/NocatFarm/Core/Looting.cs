using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Sending this account's items somewhere else - in practice, sweeping the cards off six idlers onto the one
/// account you actually sell from.
///
/// This is the one thing in here that can genuinely lose you something, so it is deliberately narrow: it will
/// only ever send to the account named in the trade-master settings, it only sends what Steam marks tradable,
/// and it will not touch anything outside the item types you asked for. There is no "send everything" path and
/// no way to point it at an arbitrary SteamID from a command.
/// </summary>
public static partial class Looting {
	/// <summary>Steam's own context ids inside app 753 (Steam itself).</summary>
	private const uint SteamAppId = 753;
	private const uint CommunityContext = 6;

	/// <param name="App">The INVENTORY it sits in - 753 for every card, background and emoticon.</param>
	/// <param name="Game">The game it belongs to (Steam's market_fee_app): a card's own game, not 753. 0 if Steam didn't say.</param>
	/// <param name="Tradable">False for a copy that can't be traded yet - it still counts as held, it just can't be offered.</param>
	public readonly record struct Item(ulong AssetId, ulong ClassId, ulong InstanceId, uint Amount, string Type, string Name, uint App, uint Context, uint Game = 0, bool Tradable = true);

	/// <summary>
	/// This account's tradable Steam community items: cards, backgrounds, emoticons, boosters.
	///
	/// Steam splits the answer in two - assets are the things you hold, descriptions are what they are - and only
	/// the descriptions know whether an item may be traded at all. Sending an untradable item does not fail
	/// politely; the whole offer is rejected, so they are filtered out here rather than discovered later.
	/// </summary>
	/// <summary>
	/// Everything tradable the account holds, across every game - not just Steam's own cards and backgrounds.
	///
	/// It used to read app 753 context 6 and nothing else, which is where cards, backgrounds, emoticons, boosters
	/// and gems live. That is the right answer for "loot the card farmer" and the wrong one for "send me
	/// everything": an account with two hundred game items reported "nothing tradable to send". The inventory
	/// page lists which games hold items, so the list comes from Steam rather than from a guess.
	/// </summary>
	public static async Task<List<Item>> InventoryAsync(Bot bot, CancellationToken ct = default) {
		List<Item> items = [];

		if (!bot.Web.Ready || (bot.SteamId == 0)) {
			return items;
		}

		foreach ((uint app, uint context) in await InventoriesAsync(bot, ct).ConfigureAwait(false)) {
			items.AddRange(await OneInventoryAsync(bot, app, context, ct).ConfigureAwait(false));
		}

		return items;
	}

	/// <summary>Which (game, context) pairs actually hold something. Steam's own inventory is always included.</summary>
	private static async Task<List<(uint App, uint Context)>> InventoriesAsync(Bot bot, CancellationToken ct) {
		List<(uint, uint)> found = [(SteamAppId, CommunityContext)];

		try {
			string? page = await bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{bot.SteamId}/inventory/"), ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(page)) {
				return found;
			}

			Match blob = Regex.Match(page, @"g_rgAppContextData\s*=\s*(\{.*?\})\s*;", RegexOptions.Singleline);

			if (!blob.Success) {
				Log.Debug("inventory page had no list of games - only Steam's own inventory will be read", bot.Name);

				return found;
			}

			using JsonDocument doc = JsonDocument.Parse(blob.Groups[1].Value);

			foreach (JsonProperty app in doc.RootElement.EnumerateObject()) {
				if (!uint.TryParse(app.Name, out uint appId) || !app.Value.TryGetProperty("rgContexts", out JsonElement contexts)) {
					continue;
				}

				foreach (JsonProperty context in contexts.EnumerateObject()) {
					bool holds = context.Value.TryGetProperty("asset_count", out JsonElement a) && a.TryGetInt32(out int count) && (count > 0);

					if (holds && uint.TryParse(context.Name, out uint contextId) && !found.Contains((appId, contextId))) {
						found.Add((appId, contextId));
					}
				}
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't list the inventories: {0}", Log.Describe(e)), bot.Name);
		}

		return found;
	}

	private static async Task<List<Item>> OneInventoryAsync(Bot bot, uint app, uint context, CancellationToken ct) {
		List<Item> items = [];

		try {
			InventoryContents? inventory = await Inventory.ReadAsync(bot, app, context.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

			if (inventory == null) {
				return items;
			}

			// class+instance is what ties an asset to its description; neither alone is unique.
			Dictionary<string, (bool Tradable, string Type, string Name, uint Game)> byClass = [];

			foreach ((string key, JsonElement description) in inventory.Descriptions) {
				bool tradable = description.TryGetProperty("tradable", out JsonElement t) && (t.ValueKind == JsonValueKind.Number ? t.GetInt32() == 1 : t.ValueKind == JsonValueKind.True);
				uint.TryParse(Text(description, "market_fee_app"), out uint game);
				byClass[key] = (tradable, Text(description, "type"), Text(description, "name"), game);
			}

			foreach (JsonElement asset in inventory.Assets) {
				string key = InventoryContents.KeyOf(asset);

				if (!byClass.TryGetValue(key, out (bool Tradable, string Type, string Name, uint Game) info) || !info.Tradable) {
					continue;
				}

				if (!ulong.TryParse(Text(asset, "assetid"), out ulong assetId) || (assetId == 0)) {
					continue;
				}

				ulong.TryParse(Text(asset, "classid"), out ulong classId);
				ulong.TryParse(Text(asset, "instanceid"), out ulong instanceId);
				uint.TryParse(Text(asset, "amount"), out uint amount);

				items.Add(new Item(assetId, classId, instanceId, Math.Max(1, amount), info.Type, info.Name, app, context, info.Game));
			}
		} catch (Exception e) {
			Log.Warn(new Said("couldn't read the inventory: {0}", Log.Describe(e)), bot.Name);
		}

		return items;
	}

	private static string Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value)
			? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
			: "";

	/// <summary>True for the item types this account was told it may send.</summary>
	public static bool WantedType(string type, string wanted) {
		if (string.IsNullOrWhiteSpace(wanted)) {
			return type.Contains("Trading Card", StringComparison.OrdinalIgnoreCase);
		}

		foreach (string token in wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			bool match = token.ToLowerInvariant() switch {
				"cards" or "card" => type.Contains("Trading Card", StringComparison.OrdinalIgnoreCase),
				"backgrounds" or "background" => type.Contains("Profile Background", StringComparison.OrdinalIgnoreCase),
				"emoticons" or "emoticon" => type.Contains("Emoticon", StringComparison.OrdinalIgnoreCase),
				"boosters" or "booster" => type.Contains("Booster Pack", StringComparison.OrdinalIgnoreCase),
				"gems" or "gem" => type.Contains("Gems", StringComparison.OrdinalIgnoreCase) || type.Contains("Sack of Gems", StringComparison.OrdinalIgnoreCase),
				"foil" or "foils" => type.Contains("Foil", StringComparison.OrdinalIgnoreCase),
				"all" or "everything" => true,
				_ => false
			};

			if (match) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Send this account's wanted items to the first trade master.
	///
	/// Returns a line to show the user either way. It refuses rather than guesses: no master, no send.
	/// </summary>
	public static Task<string> SendToMasterAsync(Bot bot, CancellationToken ct = default) => SendToMasterAsync(bot, null, ct);

	/// <summary>The same, with the item types given rather than read from the account's send setting (null = the setting).</summary>
	public static async Task<string> SendToMasterAsync(Bot bot, string? types, CancellationToken ct = default) =>
		(await SendToMasterCheckedAsync(bot, types, ct).ConfigureAwait(false)).Text;

	/// <summary>
	/// The same, also saying whether nothing went only because everything wanted was being sent or sold that moment - the
	/// one "nothing sent" that's worth trying again in a few minutes rather than at the next send.
	/// </summary>
	public static async Task<(string Text, bool Busy)> SendToMasterCheckedAsync(Bot bot, string? types, CancellationToken ct = default) {
		BotConfig cfg = bot.Cfg;
		ulong master = Modules.Social.ParseIds(cfg.TradeMasters).FirstOrDefault();

		if (master == 0) {
			return ($"{bot.Name}: nowhere to send to - set \"Your own accounts\" under Trades first", false);
		}

		if (master == bot.SteamId) {
			return ($"{bot.Name}: it is the trade master, so there is nothing to send anywhere", false);
		}

		return await SendItemsCheckedAsync(bot, master, cfg.TradeMasterToken, types ?? cfg.SendItemTypes, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Send this account's items of the given types to another account. What every form of "send" comes down to.
	/// </summary>
	/// <param name="token">The recipient's trade-link token, if known. Read automatically when it's one of your accounts.</param>
	/// <param name="types">Item types, as in the "Which items to send" setting - blank means trading cards.</param>
	public static async Task<string> SendItemsAsync(Bot bot, ulong to, string? token, string types, CancellationToken ct = default) =>
		(await SendItemsCheckedAsync(bot, to, token, types, ct).ConfigureAwait(false)).Text;

	private static async Task<(string Text, bool Busy)> SendItemsCheckedAsync(Bot bot, ulong to, string? token, string types, CancellationToken ct) {
		BotConfig cfg = bot.Cfg;
		ulong master = to;

		if (!bot.IsOnline || !bot.Web.Ready) {
			return ($"{bot.Name}: not logged in", false);
		}

		// The trade-link token is only needed between accounts that are not Steam friends - and when the
		// recipient is one of YOUR OWN accounts we are signed into it too, so asking the user to go and paste a
		// token out of a URL is asking for something we can simply read. An explicit setting still wins.
		token = (token ?? "").Trim();

		if (token.Length == 0) {
			token = await TradeTokenOfAsync(master, ct).ConfigureAwait(false) ?? "";

			if (token.Length > 0) {
				Log.Debug(new Said("using {0}'s own trade token - it is one of your accounts", BotManager.Instance?.All.FirstOrDefault(b => b.SteamId == master)?.Name ?? master.ToString(CultureInfo.InvariantCulture)), bot.Name);
			}
		}

		List<Item> all = await InventoryAsync(bot, ct).ConfigureAwait(false);

		// Leave out the games this account is banned in.
		//
		// A ban does not just stop you SELLING that game's items, it stops you trading them - and Steam refuses
		// the WHOLE offer if even one is in it, with a bare "(11) try again later". So a single CS2 skin on a
		// CS2-banned account quietly blocked every card, background and emoticon in the same sweep. The list was
		// already being kept for the inventory total and says exactly this; it just was not being read here.
		HashSet<uint> banned = [.. cfg.InventoryIgnoreGames.Select(static a => (uint) a)];
		List<Item> allowed = all.Where(i => !banned.Contains(i.App)).ToList();
		int blocked = all.Count - allowed.Count;

		// Claimed before the waiting trades are read, not after: another send or a sale that already made its offer
		// or listing has released its claim by then, so its trade shows up below - see Bot.ClaimItems.
		List<Item> wanted = allowed.Where(i => WantedType(i.Type, types)).ToList();
		HashSet<ulong> claimed = bot.ClaimItems(wanted.Select(static i => i.AssetId));

		try {
			return await SendClaimedAsync(bot, master, token, types, all, allowed, banned, blocked,
				wanted.Where(i => claimed.Contains(i.AssetId)).ToList(), wanted.Count - claimed.Count, ct).ConfigureAwait(false);
		} finally {
			bot.ReleaseItems(claimed);
		}
	}

	private static async Task<(string Text, bool Busy)> SendClaimedAsync(Bot bot, ulong master, string token, string types, List<Item> all, List<Item> allowed,
		HashSet<uint> banned, int blocked, List<Item> claimed, int busy, CancellationToken ct) {
		// Nothing already promised in a trade that's waiting - sending it would quietly take it out of that trade. When
		// Steam won't say, the send goes ahead: it only goes to your own account.
		HashSet<ulong> promised = await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) ?? [];
		List<Item> sending = claimed.Where(i => !promised.Contains(i.AssetId)).ToList();

		// Everything wanted is being sent or sold by something else of this account at this moment.
		if ((sending.Count == 0) && (busy > 0)) {
			return (new Said("{0}: its items are being sent or sold right now - try again in a few minutes", bot.Name).ToString(), true);
		}

		if (sending.Count == 0) {
			// Blame the ban only when the ban is actually what stopped it. Asked for booster packs on an account
			// with none, this used to answer "44 left out - banned in that game", which were CS2 skins that had
			// nothing to do with the question.
			int wantedButBanned = all.Count(i => banned.Contains(i.App) && WantedType(i.Type, types));

			string why = wantedButBanned > 0
				? $" ({wantedButBanned} of that type, all in a game this account is banned in - Steam won't let it trade those)"
				: allowed.Count > 0 ? $" ({allowed.Count} tradable item(s), none of the types you asked for)"
				: all.Count > 0 ? $" (everything tradable is in a game this account is banned in)" : " (no tradable items)";

			return ($"{bot.Name}: nothing tradable to send{why}", false);
		}

		// Steam rejects an offer with too many items in it outright, so send it in batches.
		const int BatchSize = 100;
		int sent = 0;
		List<string> problems = [];

		foreach (Item[] chunk in sending.Chunk(BatchSize)) {
			// A game learned to be banned partway through is left out of the batches still to go.
			Item[] batch = chunk.Where(i => !banned.Contains(i.App)).ToArray();
			blocked += chunk.Length - batch.Length;

			if (batch.Length == 0) {
				continue;
			}

			(bool ok, string message) = await SendOfferAsync(bot, master, batch, token, ct).ConfigureAwait(false);

			if (ok) {
				sent += batch.Length;
			} else if (message.Contains("(11)", StringComparison.Ordinal) && (batch.Select(static i => i.App).Distinct().Count() > 1)) {
				// Steam refuses the whole offer if one game in it is one this account is banned in - and says only
				// "(11)", which is also what it says when the recipient can't take trades at all. Sending each game
				// on its own tells the two apart: when some games go through and others don't, the ones that don't
				// are the ban, and they're added to the list so no send ever includes them again.
				(int more, List<uint> learned, string? stopped) = await SendGameByGameAsync(batch, async game => {
					await Task.Delay(Rng.Seconds(5, 15), ct).ConfigureAwait(false);

					return await SendOfferAsync(bot, master, game, token, ct).ConfigureAwait(false);
				}).ConfigureAwait(false);
				sent += more;

				if (learned.Count > 0) {
					banned.UnionWith(learned);

					// Onto the settings in force NOW, and as a new list. A send takes minutes, and the dashboard can save
					// the account meanwhile: the copy read when the send began is then an old one, and saving it put the
					// old settings back over the new. Changed in place, the list could also be mid-read by a save.
					lock (bot.CfgGate) {
						BotConfig now = bot.Cfg;
						now.InventoryIgnoreGames = [.. now.InventoryIgnoreGames, .. learned.Where(a => !now.InventoryIgnoreGames.Contains(a))];
						Config.ConfigStore.SaveBot(bot.Name, now);
					}

					Log.Attention(new Said("can't trade {0} items (banned there?) - left out from now on",
						string.Join(", ", learned.Select(GameNames.Of))), bot.Name);
					blocked += batch.Count(i => learned.Contains(i.App));
				}

				if (stopped != null) {
					problems.Add(stopped);

					break;
				}
			} else {
				problems.Add(message);

				break;   // if one batch was refused the next will be too
			}

			await Task.Delay(Rng.Seconds(5, 15), ct).ConfigureAwait(false);
		}

		if (sent == 0) {
			return ($"{bot.Name}: couldn't send - {(problems.Count > 0 ? problems[0] : "Steam refused the offer")}", false);
		}

		// By account name when it's one of yours - a SteamID64 says nothing at a glance.
		string whom = BotManager.Instance?.All.FirstOrDefault(b => b.SteamId == master)?.Name ?? master.ToString(CultureInfo.InvariantCulture);
		string note = $"{bot.Name}: sent {sent} item(s) to {whom}"
			+ (blocked > 0 ? $" ({blocked} left out - banned in that game)" : "");

		return (problems.Count > 0 ? note + $" (then stopped: {problems[0]})" : note, false);
	}

	/// <summary>
	/// The trade-link token belonging to one of our OWN accounts, read from its own session.
	///
	/// Steam shows it on the account's trade-offer privacy page as part of the full trade URL. Only works for an
	/// account this app is signed into - which is exactly the case that matters, because sweeping items between
	/// your own accounts is what the token requirement gets in the way of.
	/// </summary>
	/// <summary>
	/// A send's result, said at the right volume: items sent is a line on screen, "nothing to send" only goes in the
	/// file, and a send Steam refused is a problem you're told about - it used to go in the file too, so an account
	/// that could never send (no mobile authenticator) failed quietly every time.
	/// </summary>
	public static void Report(Bot bot, string result) {
		// The line is already filed under the account, so the "old: " a command's answer starts with comes off - it read
		// "old | old: sent 26 item(s) to new".
		string line = Unprefixed(bot.Name, result);

		if (result.Contains(": sent ", StringComparison.Ordinal)) {
			Log.Good(line, bot.Name);
		} else if (result.Contains(": couldn't send", StringComparison.Ordinal) || result.Contains(": nowhere to send", StringComparison.Ordinal)) {
			Log.Attention(line, bot.Name);
		} else {
			Log.Debug(line, bot.Name);
		}
	}

	/// <summary>A result without the "account: " it opens with for a command's answer.</summary>
	internal static string Unprefixed(string account, string result) =>
		result.StartsWith(account + ": ", StringComparison.Ordinal) ? result[(account.Length + 2)..] : result;

	private static async Task<string?> TradeTokenOfAsync(ulong steamId, CancellationToken ct) {
		Bot? owner = BotManager.Instance?.All.FirstOrDefault(b => b.SteamId == steamId);

		if ((owner == null) || !owner.IsOnline || !owner.Web.Ready) {
			return null;   // not one of ours, or not signed in - nothing we can read
		}

		try {
			string? page = await owner.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{steamId}/tradeoffers/privacy"), ct).ConfigureAwait(false);

			if (page == null) {
				return null;
			}

			Match hit = TradeTokenPattern().Match(page);

			if (!hit.Success) {
				Log.Debug($"no trade URL found on {steamId}'s trade privacy page - sending without a token", owner.Name);

				return null;
			}

			return hit.Groups[1].Value;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the trade token for {0}: {1}", steamId, Log.Describe(e)), owner.Name);

			return null;
		}
	}

	// Anchored on the trade URL itself. A bare "token=" appears elsewhere on Steam's pages, and grabbing the
	// wrong one would send a perfectly well-formed offer that Steam then refuses for a reason nobody could see.
	[GeneratedRegex(@"tradeoffer/new/\?partner=\d+&(?:amp;)?token=([A-Za-z0-9_-]{6,})", RegexOptions.CultureInvariant)]
	private static partial Regex TradeTokenPattern();

	/// <summary>
	/// Steam's trade errors are a sentence and a number, and the number is the useful half.
	///
	/// "There was an error sending your trade offer. Please try again later. (15)" is not a transient fault to be
	/// retried, whatever it says - 15 is access denied, and on this endpoint that almost always means the sending
	/// account has never enabled the mobile authenticator, which Steam requires before an account may send a trade
	/// offer at all. Passing that through unexplained had people waiting for a problem that never resolves.
	/// </summary>
	/// <summary>
	/// Resend a refused offer one game at a time, to find the game Steam won't let this account trade.
	/// </summary>
	/// <returns>How many items went through, the games that were refused while others weren't, and - if nothing at
	/// all went through - the reason to report, since then the problem is the trade itself rather than one game.</returns>
	internal static async Task<(int Sent, List<uint> Banned, string? Stopped)> SendGameByGameAsync(Item[] batch, Func<Item[], Task<(bool Ok, string Message)>> send) {
		int sent = 0;
		List<uint> refused = [];
		string? firstRefusal = null;

		foreach (IGrouping<uint, Item> game in batch.GroupBy(static i => i.App)) {
			(bool ok, string message) = await send([.. game]).ConfigureAwait(false);

			if (ok) {
				sent += game.Count();
			} else if (message.Contains("(11)", StringComparison.Ordinal)) {
				refused.Add(game.Key);
				firstRefusal ??= message;
			} else {
				// A different refusal - not a ban. Stop here rather than guess at the rest.
				return (sent, sent > 0 ? refused : [], message);
			}
		}

		// Only a game refused while another one went through is a ban. If every game was refused, the trade itself
		// is the problem (the recipient can't take offers), and marking every game as banned would be wrong.
		return sent > 0 ? (sent, refused, null) : (0, [], firstRefusal);
	}

	/// <summary>The reason Steam shows on the new-offer page when this account can't trade, or null when it shows none.</summary>
	private static async Task<string?> TradePageReasonAsync(Bot bot, Uri page, CancellationToken ct) {
		try {
			string? html = await bot.Web.GetAsync(page, ct).ConfigureAwait(false);

			if (html == null) {
				return null;
			}

			Match box = Regex.Match(html, "id=\"error_msg\"[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

			if (!box.Success) {
				return null;
			}

			string text = System.Net.WebUtility.HtmlDecode(Regex.Replace(box.Groups[1].Value, "<[^>]+>", " "));
			text = Regex.Replace(text, "\\s+", " ").Trim();

			return text.Length > 0 ? text : null;
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
			Log.Debug(new Said("couldn't read the trade page: {0}", Log.Describe(e)), bot.Name);

			return null;
		}
	}

	private static string Explain(string steamError) {
		string extra = steamError switch {
			_ when steamError.Contains("(15)", StringComparison.Ordinal) =>
				"  -  that's Steam's \"access denied\": the sending account can't trade right now. Common reasons: a trade restriction after a new sign-in, a password or email change, or a recovered account; Steam Guard switched on less than 15 days ago; a trade ban; items that can't be traded yet; or the receiving account can't take them (a full or free-to-play game inventory). Open a new trade offer on that account in a browser and Steam says which.",
			_ when steamError.Contains("(11)", StringComparison.Ordinal) =>
				"  -  Steam won't let this offer through to that account. Between two accounts that are not Steam friends it needs the recipient's trade-link token, which is looked up automatically when the recipient is one of your own accounts - so this usually means the RECIPIENT has trading switched off, is trade banned, or has never set up the mobile authenticator.",
			_ when steamError.Contains("(16)", StringComparison.Ordinal) => "  -  Steam timed out. Worth trying again.",
			_ when steamError.Contains("(26)", StringComparison.Ordinal) =>
				"  -  one of the items is no longer there. The inventory has changed since it was read; try again.",
			_ when steamError.Contains("(20)", StringComparison.Ordinal) => "  -  Steam's trading service is down for the moment.",
			_ when steamError.Contains("(25)", StringComparison.Ordinal) =>
				"  -  too many offers already open between these accounts, or a Steam limit has been hit.",
			_ when steamError.Contains("(2)", StringComparison.Ordinal) =>
				"  -  Steam gave a generic failure. Check neither account is limited, trade banned, or newly password-changed.",
			_ => ""
		};

		return steamError + extra;
	}

	/// <summary>
	/// A two-way offer: these items for those. Used by the card matcher, where a one-sided offer would be a gift.
	///
	/// Steam wants both halves in the same message, so this is the same endpoint as a plain send with the "them"
	/// side filled in as well.
	/// </summary>
	/// <param name="giving">Paired with <paramref name="taking"/> by position, each pair from the same game - so any run
	/// of pairs is still one for one within every game, which is how a big swap can be split into several offers.</param>
	public static async Task<(bool Ok, string Message)> SwapAsync(Bot bot, Bot partner, IReadOnlyList<Item> giving, IReadOnlyList<Item> taking, CancellationToken ct = default) {
		if ((giving.Count == 0) || (giving.Count != taking.Count)) {
			return (false, "a swap needs the same number of items on both sides");
		}

		// Claimed on both sides like a send or a sale claims them (Bot.ClaimItems). Unclaimed, a listing or a send
		// running meanwhile took a card out of the swap and Steam voided the whole offer, and a second 'match do'
		// offered the same cards twice. A swap is pairs, so it goes only when every card on both sides is free.
		HashSet<ulong> mine = bot.ClaimItems(giving.Select(static i => i.AssetId));
		HashSet<ulong> theirs = ReferenceEquals(bot, partner) ? [] : partner.ClaimItems(taking.Select(static i => i.AssetId));

		try {
			if ((mine.Count < giving.Select(static i => i.AssetId).Distinct().Count())
				|| (!ReferenceEquals(bot, partner) && (theirs.Count < taking.Select(static i => i.AssetId).Distinct().Count()))) {
				return (false, "some of those cards are already being sent, sold or swapped - try again once that's done");
			}

			if (!bot.IsOnline || !bot.Web.Ready) {
				return (false, $"{bot.Name} isn't logged in");
			}

			// The PARTNER's trade token, read from its own session - this used to send the sender's own "trade master"
			// token, which belongs to somebody else entirely, so swaps only ever worked between Steam friends.
			string token = await TradeTokenOfAsync(partner.SteamId, ct).ConfigureAwait(false) ?? "";
			List<string> results = [];
			bool anySent = false;

			for (int at = 0; at < giving.Count; at += SwapChunk) {
				if (at > 0) {
					await Task.Delay(Rng.Seconds(4, 10), ct).ConfigureAwait(false);
				}

				(bool ok, string message) = await SendOfferAsync(bot, partner.SteamId, [.. giving.Skip(at).Take(SwapChunk)], token, ct, [.. taking.Skip(at).Take(SwapChunk)]).ConfigureAwait(false);
				anySent |= ok;
				results.Add(message);
			}

			return (anySent, string.Join("; ", results.Distinct()));
		} finally {
			bot.ReleaseItems(mine);

			if (!ReferenceEquals(bot, partner)) {
				partner.ReleaseItems(theirs);
			}
		}
	}

	/// <summary>Card pairs per offer. Steam copes with a few hundred items, but a smaller offer is easier to confirm and less to lose to one refusal.</summary>
	private const int SwapChunk = 100;

	private static async Task<(bool Ok, string Message)> SendOfferAsync(Bot bot, ulong master, IReadOnlyCollection<Item> items, string accessToken, CancellationToken ct, IReadOnlyCollection<Item>? wanted = null) {
		StringBuilder assets = new();

		foreach (Item item in items) {
			if (assets.Length > 0) {
				assets.Append(',');
			}

			assets.Append(CultureInfo.InvariantCulture,
				$"{{\"appid\":{item.App},\"contextid\":\"{item.Context}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}

		StringBuilder theirs = new();

		foreach (Item item in wanted ?? []) {
			if (theirs.Length > 0) {
				theirs.Append(',');
			}

			theirs.Append(CultureInfo.InvariantCulture,
				$"{{\"appid\":{item.App},\"contextid\":\"{item.Context}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}

		string offer = "{\"newversion\":true,\"version\":2,"
			+ "\"me\":{\"assets\":[" + assets + "],\"currency\":[],\"ready\":false},"
			+ "\"them\":{\"assets\":[" + theirs + "],\"currency\":[],\"ready\":false}}";

		// The account id (the low 32 bits) is what the trade URL wants, not the full SteamID64.
		uint partnerAccountId = (uint) (master & 0xFFFFFFFF);
		string token = accessToken.Trim();

		Dictionary<string, string> form = new() {
			["sessionid"] = bot.Web.SessionId,
			["serverid"] = "1",
			["partner"] = master.ToString(CultureInfo.InvariantCulture),
			["tradeoffermessage"] = "",
			["json_tradeoffer"] = offer,
			["captcha"] = "",
			["trade_offer_create_params"] = token.Length > 0 ? "{\"trade_offer_access_token\":\"" + token + "\"}" : "{}"
		};

		Uri referer = new(WebSession.Community, $"/tradeoffer/new/?partner={partnerAccountId}" + (token.Length > 0 ? "&token=" + Uri.EscapeDataString(token) : ""));
		// Keeps the body on a failure: a refusal is a 500 whose body carries Steam's own explanation.
		string? body = await bot.Web.PostAllowingFailureAsync(new Uri(WebSession.Community, "/tradeoffer/new/send"), form, referer, ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(body)) {
			return (false, "Steam didn't answer at all - check the connection and try again.");
		}

		try {
			using JsonDocument doc = JsonDocument.Parse(body);

			if (doc.RootElement.TryGetProperty("strError", out JsonElement error)) {
				// The one-line strError is the polite half. Everything Steam actually said, plus what we asked it
				// to move, so a refusal can be diagnosed from the log instead of guessed at.
				Log.Debug(new Said("trade offer refused. Steam said: {0}", Log.Scrub(body)), bot.Name);
				Log.Debug((token.Length > 0
						? new Said("we offered {0} item(s) to {1}, with a trade token: ", items.Count, master)
						: new Said("we offered {0} item(s) to {1}, with no trade token: ", items.Count, master))
					+ string.Join(", ", items.Take(6).Select(static i => $"{i.App}/{i.Context}/{i.AssetId} {i.Name}")), bot.Name);

				string said = error.GetString() ?? "refused";

				// "Access denied" says nothing about why. The page for a new offer does: Steam writes the actual reason
				// there (a trade restriction and until when, an item that can't be traded yet, a limited account ...).
				if (said.Contains("(15)", StringComparison.Ordinal) && (await TradePageReasonAsync(bot, referer, ct).ConfigureAwait(false) is { } reason)) {
					return (false, $"Steam won't let {bot.Name} send this trade: {reason}");
				}

				return (false, Explain(said));
			}

			if (doc.RootElement.TryGetProperty("tradeofferid", out JsonElement id)) {
				bool needsConfirming = doc.RootElement.TryGetProperty("needs_mobile_confirmation", out JsonElement confirm)
					&& (confirm.ValueKind == JsonValueKind.True || (confirm.ValueKind == JsonValueKind.Number && confirm.GetInt32() == 1));

				bool needsEmail = doc.RootElement.TryGetProperty("needs_email_confirmation", out JsonElement email)
					&& (email.ValueKind == JsonValueKind.True || (email.ValueKind == JsonValueKind.Number && email.GetInt32() == 1));

				// Steam doesn't show the offer to anybody until it's confirmed - so "sent" is only the truth once it is.
				if (needsConfirming && ulong.TryParse(id.GetString() ?? id.ToString(), out ulong offerId)) {
					if (!await bot.ConfirmMobileAsync(offerId, true, ct).ConfigureAwait(false)) {
						// To your phone too: nobody sees the offer until it's confirmed, and nothing else will say so.
						Log.Attention(new Said("offer sent - confirm it in the Steam app on your phone"), bot.Name, Topic.Trades);

						return (true, "sent - waiting for you to confirm it in the Steam app on your phone");
					}
				} else if (needsEmail) {
					// Said, not just returned: the summary only counts what was sent, so this used to go unmentioned and
					// the offer sat unconfirmed until it expired.
					Log.Attention(new Said("offer sent - confirm it from the email Steam sent (no Steam app authenticator, so Steam holds it for up to 15 days)"), bot.Name, Topic.Trades);

					return (true, "sent - waiting for you to confirm it from the email Steam sent");
				}

				return (true, "sent");
			}
		} catch (Exception e) {
			// Mostly an HTML refusal page, which isn't JSON - the start of it is what says why.
			if (!ct.IsCancellationRequested) {
				Log.Debug($"trade offer to {master}: {Log.Describe(e)} - Steam's answer began: {Log.Scrub(body[..Math.Min(150, body.Length)])}", bot.Name);
			}

			return (false, Log.Scrub(e.Message));
		}

		// Steam answers a rejected offer as an HTML page, not JSON. Anything that isn't a trade id is a refusal.
		Log.Debug($"trade offer to {master}: no offer id and no error in Steam's answer: {Log.Scrub(body[..Math.Min(150, body.Length)])}", bot.Name);

		return (false, "Steam refused the offer - the other account may not be a friend, or its trade link may need a token");
	}
}
