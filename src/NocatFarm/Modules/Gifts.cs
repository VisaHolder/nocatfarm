using System.Text.RegularExpressions;
using NocatFarm.Core;
using SteamKit2;

namespace NocatFarm.Modules;

/// <summary>
/// Accepts the gifts people send this account - ArchiSteamFarm's AcceptGifts.
/// </summary>
/// <remarks>
/// Guest passes (free trials), which are redeemed over the client connection; Steam wallet gift cards, which sit
/// on the store's pending-gifts page until somebody presses accept; and, with AcceptGiftedGames, games a friend
/// sends, which are added to the library exactly as the "Add to my library" button does. Steam pushes the number
/// of waiting gifts on login and whenever it changes, so the pages are only read when there is something on them.
/// A gift is only ever accepted here - nothing in this module can decline one, which would refund the sender.
/// </remarks>
public sealed partial class Gifts(Bot bot) : BotModule(bot) {
	private readonly SemaphoreSlim _poke = new(0);
	private readonly HashSet<ulong> _handled = [];
	private readonly HashSet<ulong> _handledGames = [];
	private Said _status = new("");

	public override string Name => "gifts";
	public override string Status => Bot.Cfg.AcceptGifts || Bot.Cfg.AcceptGiftedGames ? _status : "";

	[GeneratedRegex("""id\s*=\s*["']pending_gift_(\d+)["']""")]
	private static partial Regex InventoryGift();

	[GeneratedRegex("class=\"gift_name\"[^>]*>([^<]+)<")]
	private static partial Regex GiftName();

	[GeneratedRegex("id=\"pending_gift_(\\d+)\"")]
	private static partial Regex PendingGift();

	[GeneratedRegex("\"success\"\\s*:\\s*1\\b")]
	private static partial Regex SuccessOne();

	private void Poke() => _poke.Release();

	protected override async Task RunAsync(CancellationToken ct) {
		Bot.GiftsWaiting += Poke;

		try {
			while (!ct.IsCancellationRequested) {
				// Steam's count: -1 until it has said, which is worth one look; 0 means there is nothing to find.
				bool waiting = (Bot.GiftsWaitingCount != 0) || (Bot.GuestPasses.Count > 0);

				if (waiting && (Bot.Cfg.AcceptGifts || Bot.Cfg.AcceptGiftedGames) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused) {
					// Accepting a gift is something a person does - not at 4am on an account that is asleep.
					if (!HumanMode.AwakeFor(Bot)) {
						_status = new Said("gift waiting - until the account is awake");

						if (!await Sleep(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false)) {
							return;
						}

						continue;
					}

					// Not the same second it arrived, either.
					if (!await Sleep(Rng.Seconds(20, 90), ct).ConfigureAwait(false)) {
						return;
					}

					try {
						// Guest passes carry gifted games too, so they are looked at if either switch is on.
						await GuestPassesAsync(ct).ConfigureAwait(false);

						if (Bot.Cfg.AcceptGifts) {
							await GiftCardsAsync(ct).ConfigureAwait(false);
						}

						if (Bot.Cfg.AcceptGiftedGames) {
							await GiftedGamesAsync(ct).ConfigureAwait(false);
						}
					} catch (OperationCanceledException) {
						throw;
					} catch (Exception e) {
						Log.Debug(new Said("couldn't check for gifts: {0}", e.Message), Bot.Name);
					}

					_status = new Said("");
				}

				// Until Steam says something new arrived. The slow pass only covers a push that went missing.
				try {
					await _poke.WaitAsync(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					return;
				}
			}
		} finally {
			Bot.GiftsWaiting -= Poke;
		}
	}

	private static readonly HttpClient Store = Browser.Anonymous(TimeSpan.FromSeconds(20));

	/// <summary>
	/// What a waiting guest pass really is. Steam hands a friend's gifted game over on the same list as a free
	/// trial, so "accepted a guest pass" was being said about real games. The package decides it: a trial is a
	/// package of its own, named for what it is ("... Guest Pass", "... Trial", "... Free Weekend"), while a
	/// gifted game is the game's ordinary package - Steep's is "Steep", sold at $29.99.
	/// </summary>
	/// <returns>Whether it's a gifted game, and the package's name when the store could say.</returns>
	private static async Task<(bool Game, string Name)> WhatIsAsync(uint package, CancellationToken ct) {
		if (package == 0) {
			return (false, "");
		}

		try {
			string json = await Store.GetStringAsync($"https://store.steampowered.com/api/packagedetails?packageids={package}", ct).ConfigureAwait(false);
			string name = Json.Str(json, "name") ?? "";
			string lower = name.ToLowerInvariant();
			bool trial = lower.Contains("guest pass", StringComparison.Ordinal) || lower.Contains("trial", StringComparison.Ordinal)
				|| lower.Contains("free weekend", StringComparison.Ordinal) || lower.Contains("demo", StringComparison.Ordinal);

			return (name.Length > 0 && !trial, name);
		} catch (Exception e) when (e is not OperationCanceledException) {
			return (false, "");
		}
	}

	private async Task GuestPassesAsync(CancellationToken ct) {
		foreach (ulong gid in Bot.GuestPasses.Where(g => !_handled.Contains(g)).ToList()) {
			uint package = Bot.GuestPassPackages.TryGetValue(gid, out uint p) ? p : 0;
			(bool game, string name) = await WhatIsAsync(package, ct).ConfigureAwait(false);

			// Each kind answers to its own switch: a gifted game to AcceptGiftedGames, a trial to AcceptGifts.
			if (game ? !Bot.Cfg.AcceptGiftedGames : !Bot.Cfg.AcceptGifts) {
				if (game) {
					Log.Attention(new Said("{0} was gifted to this account - accept or decline it in Steam (AcceptGiftedGames is off)", name), Bot.Name);
				}

				_handled.Add(gid);

				continue;
			}

			_handled.Add(gid);

			AsyncJob<SteamApps.RedeemGuestPassResponseCallback>? job = Bot.Notifications?.RedeemGuestPass(gid);

			if (job == null) {
				return;   // dropped off - the list is sent again on the next login
			}

			try {
				SteamApps.RedeemGuestPassResponseCallback answer = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

				if (answer.Result != EResult.OK) {
					Log.Info(game ? new Said("couldn't add the gifted game {0} to the library - Steam said {1}", name, answer.Result)
						: new Said("couldn't accept a guest pass - Steam said {0}", answer.Result), Bot.Name);
				} else if (game) {
					Log.Reward(new Said("added a gifted game to the library: {0}", name), Bot.Name);
				} else {
					Log.Good(name.Length > 0 ? new Said("accepted a guest pass: {0}", name)
						: answer.PackageID != 0 ? new Said("accepted a guest pass (package {0})", answer.PackageID) : new Said("accepted a guest pass"), Bot.Name);
				}
			} catch (TimeoutException) {
				Log.Debug(new Said("no answer from Steam to a guest pass - trying again next login"), Bot.Name);
				_handled.Remove(gid);
			}

			await Task.Delay(Rng.Seconds(3, 8), ct).ConfigureAwait(false);
		}
	}

	private async Task GiftCardsAsync(CancellationToken ct) {
		string? page = await Bot.Web.GetAsync(new Uri(WebSession.Store, "/gifts?l=english"), ct).ConfigureAwait(false);

		if (page == null) {
			return;
		}

		MatchCollection hits = PendingGift().Matches(page);
		Log.Debug(new Said("the store's gifts page lists {0} pending gift(s)", hits.Count), Bot.Name);

		for (int i = 0; i < hits.Count; i++) {
			if (!ulong.TryParse(hits[i].Groups[1].Value, out ulong id) || (id == 0) || _handled.Contains(id)) {
				continue;
			}

			_handled.Add(id);

			// A gift card's block carries its own left column; a game gift's doesn't.
			int end = i + 1 < hits.Count ? hits[i + 1].Index : page.Length;
			bool card = page.AsSpan(hits[i].Index, end - hits[i].Index).Contains("pending_giftcard_leftcol", StringComparison.Ordinal);

			if (!card) {
				// Accepted below when AcceptGiftedGames is on; otherwise it's the owner's to take or turn down.
				if (!Bot.Cfg.AcceptGiftedGames) {
					Log.Attention(new Said("a game was sent to this account as a gift - accept or decline it on the store's gifts page"), Bot.Name);
				}

				continue;
			}

			string? answer = await Bot.Web.PostAsync(new Uri(WebSession.Store, "/gifts/0/resolvegiftcard"), new Dictionary<string, string> {
				["accept"] = "1",
				["giftcardid"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture)
			}, new Uri(WebSession.Store, "/gifts"), ct).ConfigureAwait(false);

			if ((answer != null) && SuccessOne().IsMatch(answer)) {
				Log.Good(new Said("accepted a Steam wallet gift card"), Bot.Name);
			} else {
				Log.Info(new Said("couldn't accept a Steam wallet gift card: {0}", answer ?? "-"), Bot.Name);
			}

			await Task.Delay(Rng.Seconds(3, 8), ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Add the games friends have gifted to the library - the "Add to my library" button on the pending gifts in the
	/// account's inventory, which posts to /gifts/&lt;id&gt;/acceptunpack (steamcommunity.com's own gifts.js).
	/// </summary>
	/// <remarks>
	/// Only a gift whose block offers that button is touched - DoAcceptGift(id, true) - so a gift card or anything
	/// Steam wants handled on its own page is left alone. The decline address is never called from here.
	/// </remarks>
	private async Task GiftedGamesAsync(CancellationToken ct) {
		string? page = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/inventory/"), ct).ConfigureAwait(false);

		if (page == null) {
			return;
		}

		MatchCollection hits = InventoryGift().Matches(page);

		for (int i = 0; i < hits.Count; i++) {
			if (!ulong.TryParse(hits[i].Groups[1].Value, out ulong id) || (id == 0) || _handledGames.Contains(id)) {
				continue;
			}

			int end = i + 1 < hits.Count ? hits[i + 1].Index : page.Length;
			string block = page[hits[i].Index..end];

			if (!Regex.IsMatch(block, $@"DoAcceptGift\s*\(\s*['""]?{id}['""]?\s*,\s*(?:true|1)", RegexOptions.IgnoreCase)) {
				continue;
			}

			_handledGames.Add(id);

			Match named = GiftName().Match(block);
			string name = named.Success ? System.Net.WebUtility.HtmlDecode(named.Groups[1].Value).Trim() : "";

			string? answer = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/gifts/{id}/acceptunpack"), [],
				new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/inventory/#pending_gifts"), ct).ConfigureAwait(false);

			if ((answer != null) && SuccessOne().IsMatch(answer)) {
				Log.Reward(name.Length > 0 ? new Said("added a gifted game to the library: {0}", name) : new Said("added a gifted game to the library"), Bot.Name);
			} else {
				// Not remembered as handled, so the next look tries again - a gift is too good to give up on after one miss.
				_handledGames.Remove(id);
				Log.Info(new Said("couldn't add a gifted game to the library: {0}", answer ?? "-"), Bot.Name);
			}

			await Task.Delay(Rng.Seconds(3, 8), ct).ConfigureAwait(false);
		}
	}
}
