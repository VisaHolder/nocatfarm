using System.Text.RegularExpressions;
using NocatFarm.Core;
using SteamKit2;

namespace NocatFarm.Modules;

/// <summary>
/// Accepts the gifts people send this account - wallet gift cards, guest passes, and games friends gift it.
/// </summary>
/// <remarks>
/// Three kinds reach an account. A game a friend gifts and a free-trial guest pass both arrive on Steam's guest-pass
/// list and are redeemed over the client connection; which one it is comes from the package. Steam wallet gift cards
/// sit on the store's pending-gifts page until somebody presses accept. (A gift can also show on the inventory
/// page with an "Add to my library" button; that path is covered too.) Steam pushes the number of waiting gifts on
/// login and whenever it changes, so the pages are only read when there is something on them.
///
/// Nothing is taken the second it lands. Each gift waits its own person-length time (GiftDelayMin/MaxMinutes),
/// waits for morning while human mode has the account asleep, and gets a fresh wait once it's up.
///
/// A gift is only ever accepted here - nothing in this module can decline one, which would refund the sender.
/// </remarks>
public sealed partial class Gifts(Bot bot) : BotModule(bot) {
	private enum Kind { Pass, Card, Inventory }

	/// <summary>A gift waiting its turn: what it is, for the line in the log when it's taken.</summary>
	private sealed record Waiting(string Name, bool GiftedGame);

	private readonly SemaphoreSlim _poke = new(0);
	private readonly ReactionQueue<(Kind, ulong)> _queue = new();

	/// <summary>Gifts whose wait is running - 'Update by itself' holds off while there are any (held ones don't count).</summary>
	public int WaitingCount => _queue.ArmedCount;
	private readonly Dictionary<(Kind, ulong), Waiting> _waiting = [];
	private readonly HashSet<(Kind, ulong)> _handled = [];

	/// <summary>What each waiting guest pass turned out to be, so the store is asked about a gift once.</summary>
	private readonly Dictionary<ulong, (bool Game, string Name)> _kinds = [];

	/// <summary>Gifted games the owner has been told to take themselves (AcceptGiftedGames off) - said once each.</summary>
	private readonly HashSet<ulong> _pointedOut = [];

	private Said _status = new("");

	/// <summary>Looks in a row where a waiting guest pass couldn't be told apart, because the store didn't answer.</summary>
	private int _unsureLooks;

	public override string Name => "gifts";
	public override string Status => Bot.Cfg.AcceptGifts || Bot.Cfg.AcceptGiftedGames ? _status : "";

	[GeneratedRegex("""id\s*=\s*["']pending_gift_(\d+)["']""")]
	private static partial Regex PendingGift();

	[GeneratedRegex("class=\"gift_name\"[^>]*>([^<]+)<")]
	private static partial Regex GiftName();

	[GeneratedRegex("\"success\"\\s*:\\s*1\\b")]
	private static partial Regex SuccessOne();

	[GeneratedRegex("\"success\"\\s*:\\s*false\\b")]
	private static partial Regex SuccessFalse();

	[GeneratedRegex("\"price\"\\s*:\\s*\\{")]
	private static partial Regex HasPrice();

	/// <summary>How a trial's package is named. Whole words: "Trials Rising" is a game.</summary>
	[GeneratedRegex(@"\b(?:guest\s+pass|trial|free\s+weekend|demo)\b", RegexOptions.IgnoreCase)]
	private static partial Regex TrialName();

	private static readonly HttpClient Store = Browser.Anonymous(TimeSpan.FromSeconds(20));

	private void Poke() => _poke.Release();

	/// <summary>Look for waiting gifts now rather than at the next slow pass - after a switch is turned on, say.</summary>
	public void LookAgain() => Poke();

	/// <summary>How long a gift waits before it's taken - a person's time, not a flat random pick.</summary>
	private TimeSpan GiftWait() => Rng.HumanMinutes(Bot.Cfg.GiftDelayMinMinutes, Bot.Cfg.GiftDelayMaxMinutes);

	protected override async Task RunAsync(CancellationToken ct) {
		Bot.GiftsWaiting += Poke;

		// One look after signing in: Steam hasn't said how many gifts are waiting yet, and one may have come while
		// the account was offline.
		bool look = true;

		// When to ask the store again about a guest pass it couldn't tell us about.
		DateTime? retryAt = null;

		try {
			while (!ct.IsCancellationRequested) {
				if ((Bot.Cfg.AcceptGifts || Bot.Cfg.AcceptGiftedGames) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused) {
					try {
						if (look) {
							look = false;
							await FindAsync(ct).ConfigureAwait(false);
							retryAt = _unsureLooks > 0 ? DateTime.UtcNow + RetryGap(_unsureLooks) : null;
						}

						await AnswerAsync(ct).ConfigureAwait(false);
					} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
						throw;
					} catch (Exception e) {
						// A request timing out lands here too. Let through, it read as a normal shutdown and stopped
						// the module without a word.
						Log.Debug(new Said("couldn't check for gifts: {0}", e.Message), Bot.Name);
					}
				}

				_status = StatusNow();

				// Until Steam says a gift arrived, or the next one comes due. While something is waiting, a minute at
				// a time, so the account waking up, a pause or a changed setting is noticed; with nothing waiting,
				// six hours - that slow pass only covers a push that went missing.
				DateTime now = DateTime.UtcNow;
				DateTime? next = _queue.Next;
				TimeSpan wait = _queue.Count == 0 ? TimeSpan.FromHours(6)
					: next is { } due ? Clamp(due - now, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1))
					: TimeSpan.FromMinutes(1);

				if ((retryAt is { } at) && (at - now < wait)) {
					wait = Clamp(at - now, TimeSpan.FromSeconds(1), wait);
				}

				bool poked;

				try {
					poked = await _poke.WaitAsync(wait, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					return;
				}

				// Not on every minute's wake-up while something waits: only when Steam pokes, the slow pass is up,
				// or it's time to ask the store again.
				if (poked || (_queue.Count == 0) || (retryAt <= DateTime.UtcNow)) {
					look = true;
				}
			}
		} finally {
			Bot.GiftsWaiting -= Poke;
		}
	}

	private static TimeSpan Clamp(TimeSpan value, TimeSpan lo, TimeSpan hi) => value < lo ? lo : value > hi ? hi : value;

	/// <summary>
	/// How long before asking the store again about a guest pass it couldn't tell us about: two minutes, doubling to
	/// an hour. A store that is down, or turning us away, isn't asked every two minutes for hours.
	/// </summary>
	private static TimeSpan RetryGap(int looks) => TimeSpan.FromMinutes(Math.Min(60, 2 << Math.Min(5, looks - 1)));

	private Said StatusNow() {
		if (_queue.Count == 0) {
			return new Said("");
		}

		if (_queue.AnyHeld) {
			return new Said("gift waiting - until the account is awake");
		}

		DateTime? next = _queue.Next;

		return next is { } due ? new Said("accepting a gift around {0}", (Func<string>) (() => Fmt.Clock(due))) : new Said("");
	}

	// ── finding what's waiting ──────────────────────────────────────────────
	/// <summary>Queue every gift that's waiting and hasn't been dealt with. Nothing is accepted here.</summary>
	private async Task FindAsync(CancellationToken ct) {
		IReadOnlyList<ulong> passes = Bot.GuestPasses;
		bool unsure = false;

		// A pass no longer on the list was taken, declined or ran out - there's nothing left to remember about it.
		foreach (ulong gone in _kinds.Keys.Where(gid => !passes.Contains(gid)).ToList()) {
			_kinds.Remove(gone);
		}

		_pointedOut.RemoveWhere(gid => !passes.Contains(gid));

		// Guest passes - gifted games and trials alike - come pushed over the connection; no page to read.
		foreach (ulong gid in passes) {
			(Kind, ulong) key = (Kind.Pass, gid);

			if (_handled.Contains(key) || _queue.Contains(key)) {
				continue;
			}

			if (!_kinds.TryGetValue(gid, out (bool Game, string Name) kind)) {
				uint package = Bot.GuestPassPackages.TryGetValue(gid, out uint p) ? p : 0;
				(bool? known, string name) = await WhatIsAsync(package, ct).ConfigureAwait(false);

				// The store didn't answer. Deciding now would be a guess - and a wrong one could write a real gift
				// off as a trial the account doesn't take. It stays unhandled, and the store is asked again later.
				if (known is not bool game) {
					unsure = true;

					continue;
				}

				kind = (game, name);
				_kinds[gid] = kind;
			}

			// Each kind answers to its own switch: a gifted game to AcceptGiftedGames, a trial to AcceptGifts. One
			// whose switch is off is left for the owner - and taken after all if the switch is turned on later.
			if (kind.Game ? !Bot.Cfg.AcceptGiftedGames : !Bot.Cfg.AcceptGifts) {
				if (kind.Game && _pointedOut.Add(gid)) {
					Log.Attention(new Said("{0} was gifted - answer it in Steam (auto-accept is off)", Named(kind.Name)), Bot.Name);
				}

				continue;
			}

			Queue(key, new Waiting(kind.Name, kind.Game));
		}

		_unsureLooks = unsure ? _unsureLooks + 1 : 0;

		// The pages only when Steam's count says something is on them (or hasn't said yet).
		if (Bot.GiftsWaitingCount == 0) {
			return;
		}

		if (Bot.Cfg.AcceptGifts) {
			await FindGiftCardsAsync(ct).ConfigureAwait(false);
		}

		if (Bot.Cfg.AcceptGiftedGames) {
			await FindInventoryGiftsAsync(ct).ConfigureAwait(false);
		}
	}

	private void Queue((Kind, ulong) key, Waiting what) {
		_waiting[key] = what;
		_queue.Add(key);   // on hold until AnswerAsync gives it its wait
	}

	/// <summary>
	/// What a waiting guest pass really is. Steam hands a friend's gifted game over on the same list as a free
	/// trial, so "accepted a guest pass" was being said about real games. The package decides it. A gifted game was
	/// bought on the store, so its package has a price there - Steep's is "Steep", $29.99. A trial is never sold:
	/// the store doesn't know the package at all, or it's named for what it is ("... Guest Pass", "... Free Trial",
	/// "... Free Weekend").
	/// </summary>
	/// <returns>Whether it's a gifted game - null when the store couldn't say - and the package's name.</returns>
	private static async Task<(bool? Game, string Name)> WhatIsAsync(uint package, CancellationToken ct) {
		if (package == 0) {
			return (false, "");   // Steam named no package: nothing to look up, so it's taken for what it arrived as
		}

		try {
			string json = await Store.GetStringAsync($"https://store.steampowered.com/api/packagedetails?packageids={package}", ct).ConfigureAwait(false);

			// The store saying it doesn't sell this package is an answer, not a failure - asking again won't change it.
			if (SuccessFalse().IsMatch(json)) {
				return (false, "");
			}

			string name = Json.Str(json, "name") ?? "";

			if (name.Length == 0) {
				return (null, "");
			}

			// No price means it isn't on sale right now (taken off the store since it was bought, say), and then the
			// name is all there is to go on.
			return (HasPrice().IsMatch(json) || !TrialName().IsMatch(name), name);
		} catch (Exception) when (!ct.IsCancellationRequested) {
			return (null, "");   // timed out or turned away - a request timing out is an OperationCanceledException too
		}
	}

	private async Task FindGiftCardsAsync(CancellationToken ct) {
		string? page = await Bot.Web.GetAsync(new Uri(WebSession.Store, "/gifts?l=english"), ct).ConfigureAwait(false);

		if (page == null) {
			return;
		}

		MatchCollection hits = PendingGift().Matches(page);
		Log.Debug(new Said("the store's gifts page lists {0} pending gift(s)", hits.Count), Bot.Name);

		for (int i = 0; i < hits.Count; i++) {
			if (!ulong.TryParse(hits[i].Groups[1].Value, out ulong id) || (id == 0)) {
				continue;
			}

			(Kind, ulong) key = (Kind.Card, id);

			if (_handled.Contains(key) || _queue.Contains(key)) {
				continue;
			}

			// A gift card's block carries its own left column; a game gift's doesn't.
			int end = i + 1 < hits.Count ? hits[i + 1].Index : page.Length;
			bool card = page.AsSpan(hits[i].Index, end - hits[i].Index).Contains("pending_giftcard_leftcol", StringComparison.Ordinal);

			// A game gift. The same gift is on the guest-pass list, which is where it's taken - or, with
			// AcceptGiftedGames off, pointed out to the owner - so there is nothing to do about it from here.
			if (!card) {
				continue;
			}

			Queue(key, new Waiting("", false));
		}
	}

	/// <summary>
	/// Gifts shown on the account's inventory page with an "Add to my library" button - DoAcceptGift(id, true) in
	/// steamcommunity.com's own gifts.js. Only a gift offering that button is queued, so a gift card or anything
	/// Steam wants handled on its own page is left alone.
	/// </summary>
	private async Task FindInventoryGiftsAsync(CancellationToken ct) {
		string? page = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/inventory/"), ct).ConfigureAwait(false);

		if (page == null) {
			return;
		}

		MatchCollection hits = PendingGift().Matches(page);

		for (int i = 0; i < hits.Count; i++) {
			if (!ulong.TryParse(hits[i].Groups[1].Value, out ulong id) || (id == 0)) {
				continue;
			}

			(Kind, ulong) key = (Kind.Inventory, id);

			if (_handled.Contains(key) || _queue.Contains(key)) {
				continue;
			}

			int end = i + 1 < hits.Count ? hits[i + 1].Index : page.Length;
			string block = page[hits[i].Index..end];

			if (!Regex.IsMatch(block, $@"DoAcceptGift\s*\(\s*['""]?{id}['""]?\s*,\s*(?:true|1)\b", RegexOptions.IgnoreCase)) {
				continue;
			}

			Match named = GiftName().Match(block);
			Queue(key, new Waiting(named.Success ? System.Net.WebUtility.HtmlDecode(named.Groups[1].Value).Trim() : "", true));
		}
	}

	// ── answering ───────────────────────────────────────────────────────────
	/// <summary>Give new gifts their wait, and take the ones whose wait has run out.</summary>
	private async Task AnswerAsync(CancellationToken ct) {
		// Accepting a gift is something a person does - not at 4am on an account that is asleep. Everything goes
		// back on hold, and gets a fresh wait once the account is up.
		if (!HumanMode.ReadyFor(Bot)) {
			_queue.Hold();

			return;
		}

		// Gifts that waited out the night get their wait from a while after waking, not all in its first quarter hour.
		foreach (((Kind kind, ulong id) key, DateTime due) in _queue.Arm(DateTime.UtcNow, () => GiftWait() + (Bot.Cfg.LegitMode ? Rng.HumanMinutes(10, 60) : TimeSpan.Zero))) {
			Announce(key, due);
		}

		foreach ((Kind, ulong) key in _queue.Due(DateTime.UtcNow)) {
			await TakeAsync(key, ct).ConfigureAwait(false);

			// Human mode takes one per pass; the next waits for a later one.
			if (Bot.Cfg.LegitMode) {
				break;
			}

			await Task.Delay(Rng.Seconds(3, 8), ct).ConfigureAwait(false);
		}
	}

	private void Announce((Kind Kind, ulong Id) key, DateTime due) {
		Waiting what = _waiting.TryGetValue(key, out Waiting? w) ? w : new Waiting("", false);
		Func<string> at = () => Fmt.Clock(due);

		Log.Info(key.Kind == Kind.Card ? new Said("wallet gift card waiting - accepting around {0}", at)
			: what.GiftedGame ? new Said("{0} was gifted - adding it around {1}", Named(what.Name), at)
			: what.Name.Length > 0 ? new Said("guest pass for {0} - accepting around {1}", what.Name, at)
			: new Said("guest pass waiting - accepting around {0}", at), Bot.Name);
	}

	private static object Named(string name) => name.Length > 0 ? name : new Said("a game");

	private async Task TakeAsync((Kind Kind, ulong Id) key, CancellationToken ct) {
		_queue.Remove(key);
		Waiting what = _waiting.TryGetValue(key, out Waiting? w) ? w : new Waiting("", false);
		_waiting.Remove(key);

		// Its switch may have been turned off while it waited. Then it isn't taken - and isn't remembered either,
		// so the next look treats it by the settings as they are then.
		if (what.GiftedGame ? !Bot.Cfg.AcceptGiftedGames : !Bot.Cfg.AcceptGifts) {
			return;
		}

		bool taken = key.Kind switch {
			Kind.Pass => await RedeemPassAsync(key.Id, what, ct).ConfigureAwait(false),
			Kind.Card => await AcceptCardAsync(key.Id, ct).ConfigureAwait(false),
			_ => await AcceptInventoryGiftAsync(key.Id, what, ct).ConfigureAwait(false)
		};

		// Not remembered when it didn't go through, so the next look queues it again - a gift is too good to give up
		// on after one miss.
		if (taken) {
			_handled.Add(key);
		}
	}

	/// <returns>Whether it's dealt with - taken, or no longer there to take.</returns>
	private async Task<bool> RedeemPassAsync(ulong gid, Waiting what, CancellationToken ct) {
		// Already dealt with somewhere else - accepted in the Steam client while it waited, say.
		if (!Bot.GuestPasses.Contains(gid)) {
			Log.Debug(new Said("a waiting gift was already taken care of before its turn came"), Bot.Name);

			return true;
		}

		AsyncJob<SteamApps.RedeemGuestPassResponseCallback>? job = Bot.Notifications?.RedeemGuestPass(gid);

		if (job == null) {
			return false;   // dropped off - the list is sent again on the next login
		}

		try {
			SteamApps.RedeemGuestPassResponseCallback answer = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

			if (answer.Result != EResult.OK) {
				Log.Info(what.GiftedGame ? new Said("couldn't add gifted {0} - Steam said {1}", what.Name, answer.Result)
					: new Said("couldn't accept a guest pass - Steam said {0}", answer.Result), Bot.Name);

				return true;
			}

			if (what.GiftedGame) {
				Log.Reward(new Said("gifted game added: {0}", what.Name), Bot.Name, topic: Topic.FreeStuff);
			} else {
				Log.Good(what.Name.Length > 0 ? new Said("accepted a guest pass: {0}", what.Name)
					: answer.PackageID != 0 ? new Said("accepted a guest pass (package {0})", answer.PackageID) : new Said("accepted a guest pass"), Bot.Name);
			}

			return true;
		} catch (Exception e) when ((e is TimeoutException) || ((e is OperationCanceledException) && !ct.IsCancellationRequested)) {
			// Steam's own job gives up by cancelling rather than timing out, so that comes through as a cancel - not
			// the app shutting down, and not a reason to lose the rest of this look.
			Log.Debug(new Said("no answer from Steam to a guest pass - trying again next login"), Bot.Name);

			return false;
		}
	}

	private async Task<bool> AcceptCardAsync(ulong id, CancellationToken ct) {
		string? answer = await Bot.Web.PostAsync(new Uri(WebSession.Store, "/gifts/0/resolvegiftcard"), new Dictionary<string, string> {
			["accept"] = "1",
			["giftcardid"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture)
		}, new Uri(WebSession.Store, "/gifts"), ct).ConfigureAwait(false);

		// No answer at all - a timeout, a rate limit, a session that needed renewing - is tried again at the next look.
		if (answer == null) {
			return false;
		}

		if (SuccessOne().IsMatch(answer)) {
			Log.Good(new Said("accepted a Steam wallet gift card"), Bot.Name);
		} else {
			Log.Info(new Said("couldn't accept a Steam wallet gift card: {0}", answer.Length > 200 ? answer[..200] : answer), Bot.Name);
		}

		return true;   // Steam's own no isn't retried - it would say no again
	}

	/// <summary>
	/// The "Add to my library" button: POST /gifts/&lt;id&gt;/acceptunpack, as steamcommunity.com's own gifts.js does.
	/// The decline address is never called from here.
	/// </summary>
	private async Task<bool> AcceptInventoryGiftAsync(ulong id, Waiting what, CancellationToken ct) {
		string? answer = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/gifts/{id}/acceptunpack"), [],
			new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/inventory/#pending_gifts"), ct).ConfigureAwait(false);

		if ((answer != null) && SuccessOne().IsMatch(answer)) {
			Log.Reward(what.Name.Length > 0 ? new Said("gifted game added: {0}", what.Name) : new Said("added a gifted game to the library"), Bot.Name, topic: Topic.FreeStuff);

			return true;
		}

		Log.Info(new Said("couldn't add a gifted game to the library: {0}", answer ?? "-"), Bot.Name);

		return false;
	}
}
