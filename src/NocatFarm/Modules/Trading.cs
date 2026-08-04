using System.Globalization;
using System.Text.RegularExpressions;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Incoming trade offers.
///
/// The safe half of this is donations - an offer that asks for nothing at all. Those can only ever gain items and
/// never lose any, so no offer accepted on that rule can cost the account a thing; an offer that wants even one
/// item of yours is not a donation and is never accepted by it. That covers the ordinary case of somebody sending
/// cards or a gift to an idler.
///
/// The other half is your own accounts. Anything on that list is trusted with everything, which is what makes it
/// possible to sweep the cards off six idlers onto one account without touching a mouse - and exactly why the
/// list has to be accounts you personally own.
///
/// Nothing is acted on the instant it lands. A trade accepted two seconds after it was sent is a bot accepting.
/// </summary>
public sealed partial class Trading(Bot bot) : BotModule(bot) {
	/// <summary>How many looks that turn up nothing to do before Steam's waiting count stops meaning "hurry".</summary>
	private const int FruitlessBeforeBackingOff = 3;

	private readonly Dictionary<ulong, DateTime> _actOn = [];
	private readonly HashSet<ulong> _done = [];
	private int _accepted;
	private int _declined;
	private int _fruitless;

	public override string Name => "trades";

	public override string Status {
		get {
			List<string> bits = [];

			if (_accepted > 0) {
				bits.Add(Loc.T("{0} accepted", _accepted));
			}

			if (_declined > 0) {
				bits.Add(Loc.T("{0} declined", _declined));
			}

			lock (_actOn) {
				if (_actOn.Count > 0) {
					bits.Add(Loc.T("{0} waiting", _actOn.Count));
				}
			}

			return bits.Count > 0 ? string.Join(" · ", bits) : "";
		}
	}

	private bool Wanted => Bot.Cfg.AcceptDonations || Bot.Cfg.AcceptFromMasters || Bot.Cfg.DeclineOtherTrades;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			// A trade accepted at 4am by an account whose friends list says it is offline is not a person. When
			// human mode owns the account, offers simply sit until morning - which is what would really happen.
			if (Wanted && Bot.IsOnline && Bot.Web.Ready && HumanMode.AwakeFor(Bot) && ShouldLook()) {
				try {
					await CheckAsync(ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception e) {
					Log.Debug($"couldn't check trade offers: {e.GetType().Name}: {e.Message}", Bot.Name);
				}
			}

			// Sleeps until the gap runs out OR Steam says an offer arrived, whichever comes first. That is what
			// makes the long gap safe: nothing waits an hour, it just stops asking when there is nothing to ask.
			try {
				await Bot.WaitForTradeOfferAsync(NextGap(), ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}
	}

	/// <summary>
	/// Whether there is any point opening the trade offers page at all.
	///
	/// Steam pushes the number of waiting offers over the client connection - on login and again the instant it
	/// changes - so an account it has told us has none does not need to go and look. Fetching that page every
	/// few minutes on every account, to learn nothing, is what got the community site answering 429; it is also
	/// nothing like what a person does. When Steam hasn't told us yet, or something is mid-flight, we still look.
	/// </summary>
	private bool ShouldLook() {
		lock (_actOn) {
			if (_actOn.Count > 0) {
				return true;   // an offer is sitting out its delay and has to be re-read to be acted on
			}
		}

		// A zero here is Steam's word that nothing is waiting, and it is trustworthy: the notification sweep
		// deliberately resets this to "don't know" and wakes us for one look, so an offer that was already
		// waiting when the sweep ran is found rather than swept away with everything else.
		return Bot.TradeOffersWaiting != 0;
	}

	/// <summary>
	/// How long to wait before looking again. Minutes while something is actually in flight, the better part of
	/// an hour otherwise - the slow pass exists only to catch an offer Steam never pushed a notification for.
	/// </summary>
	private TimeSpan NextGap() {
		lock (_actOn) {
			if (_actOn.Count > 0) {
				return Rng.Minutes(4, 9);
			}
		}

		// Steam's counter can stand at one for something we are never going to touch - an offer held in escrow,
		// or one from a stranger on an account that accepts nothing. Coming back every five minutes to re-read
		// the same untouchable offer is the same wasted request as before, so after a few fruitless looks the
		// count stops being taken as a reason to hurry.
		return (Bot.TradeOffersWaiting > 0) && (_fruitless < FruitlessBeforeBackingOff) ? Rng.Minutes(4, 9) : Rng.Minutes(45, 90);
	}

	private async Task CheckAsync(CancellationToken ct) {
		string? html = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/tradeoffers/"), ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(html)) {
			return;
		}

		bool actedOnSomething = false;
		int seen = 0;
		HashSet<ulong> masters = Social.ParseIds(Bot.Cfg.TradeMasters);

		foreach (Offer offer in Parse(html)) {
			seen++;

			lock (_done) {
				if (_done.Contains(offer.Id)) {
					continue;
				}
			}

			bool fromMaster = Bot.Cfg.AcceptFromMasters && masters.Contains(offer.Partner);
			bool donation = Bot.Cfg.AcceptDonations && offer.IsPureDonation;
			bool accept = fromMaster || donation;

			if (!accept && Bot.Cfg.AcceptDonations && (offer.GivingCount == null)) {
				Log.Warn($"trade offer #{offer.Id}: couldn't tell what it asks for, so it has been left alone - look at it yourself", Bot.Name);
			}

			if (!accept && !Bot.Cfg.DeclineOtherTrades) {
				continue;   // leave it sitting there for the user to look at
			}

			// Everything waits its turn. The wait is per offer, so two arriving together are not handled together.
			actedOnSomething = true;
			DateTime when;

			lock (_actOn) {
				if (!_actOn.TryGetValue(offer.Id, out when)) {
					int lo = Math.Max(0, Bot.Cfg.TradeDelayMinMinutes);
					int hi = Math.Max(lo, Bot.Cfg.TradeDelayMaxMinutes);
					when = DateTime.UtcNow.Add(Rng.Minutes(lo, hi));
					_actOn[offer.Id] = when;

					string what = accept ? fromMaster ? "from one of your accounts" : "a donation" : "unwanted";
					Log.Info($"trade offer #{offer.Id} ({what}: {offer.Describe}) - handling it in {Fmt.Hm((int) Math.Max(1, (when - DateTime.UtcNow).TotalMinutes))}", Bot.Name);
				}
			}

			if (DateTime.UtcNow < when) {
				continue;
			}

			bool ok = accept ? await AcceptAsync(offer, ct).ConfigureAwait(false) : await DeclineAsync(offer, ct).ConfigureAwait(false);

			if (ok) {
				lock (_done) {
					_done.Add(offer.Id);
				}

				lock (_actOn) {
					_actOn.Remove(offer.Id);
				}

				if (accept) {
					_accepted++;
					Log.Reward($"accepted trade offer #{offer.Id} - {offer.ReceivingCount} item(s) in", Bot.Name);
				} else {
					_declined++;
					Log.Info($"declined trade offer #{offer.Id}", Bot.Name);
				}
			}

			await Task.Delay(Rng.Seconds(3, 12), ct).ConfigureAwait(false);
		}

		_fruitless = actedOnSomething ? 0 : _fruitless + 1;

		// We have just read the page, so we know better than the notification counter does - tell it. Without
		// this a count left at "don't know" (which is what the notification sweep deliberately does) stayed that
		// way for ever, and the account went on opening this page every hour or so with nothing to find. Now the
		// first look after a sweep settles it, and a genuinely new offer still arrives as its own push.
		Bot.NoteTradeOffersSeen(seen);
	}

	private async Task<bool> AcceptAsync(Offer offer, CancellationToken ct) {
		Dictionary<string, string> form = new() {
			["sessionid"] = Bot.Web.SessionId,
			["serverid"] = "1",
			["tradeofferid"] = offer.Id.ToString(CultureInfo.InvariantCulture),
			["partner"] = offer.Partner.ToString(CultureInfo.InvariantCulture),
			["captcha"] = ""
		};

		Uri referer = new(WebSession.Community, $"/tradeoffer/{offer.Id}/");
		string? body = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/tradeoffer/{offer.Id}/accept"), form, referer, ct).ConfigureAwait(false);

		if (body == null) {
			return false;
		}

		// Steam answers with needs_mobile_confirmation when the account has the mobile authenticator on. If we
		// hold its secrets we can confirm it ourselves; if not, the offer is accepted but sits pending on Steam.
		if (body.Contains("needs_mobile_confirmation", StringComparison.OrdinalIgnoreCase)) {
			if (await Bot.ConfirmMobileAsync(offer.Id, true, ct).ConfigureAwait(false)) {
				return true;
			}

			Log.Warn($"trade offer #{offer.Id} was accepted but needs confirming on your phone - add this account's mobile authenticator secrets to do that here", Bot.Name);

			return true;
		}

		// A trade offer accepted successfully answers with JSON carrying the offer id. Treating "any 200 without
		// strError" as success meant an HTML refusal page - which is what Steam serves for an expired session or
		// a rate limit - was logged as a reward for items that never arrived, and never retried.
		return body.Contains("tradeid", StringComparison.OrdinalIgnoreCase)
			|| body.Contains("needs_mobile_confirmation", StringComparison.OrdinalIgnoreCase)
			|| body.Contains("\"success\":1", StringComparison.Ordinal)
			|| body.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase);
	}

	private async Task<bool> DeclineAsync(Offer offer, CancellationToken ct) {
		Dictionary<string, string> form = new() { ["sessionid"] = Bot.Web.SessionId };
		string? body = await Bot.Web.PostAsync(new Uri(WebSession.Community, $"/tradeoffer/{offer.Id}/decline"), form, new Uri(WebSession.Community, "/profiles/" + Bot.SteamId + "/tradeoffers/"), ct).ConfigureAwait(false);

		return body != null;
	}

	// ── reading the page ────────────────────────────────────────────────────
	/// <summary>
	/// One incoming offer. The counts are nullable on purpose: null means "that side of the page could not be
	/// read", which must never be mistaken for "that side is empty".
	/// </summary>
	private readonly record struct Offer(ulong Id, ulong Partner, int? ReceivingCount, int? GivingCount) {
		/// <summary>
		/// Gives up nothing whatsoever - the only shape that can be accepted from a stranger without risk.
		///
		/// Both sides must have been positively read. An unreadable giving side is not a donation, and an offer
		/// that appears to contain nothing at all is not one either.
		/// </summary>
		public bool IsPureDonation => (GivingCount == 0) && (ReceivingCount > 0);

		public string Describe => $"+{ReceivingCount?.ToString() ?? "?"} / -{GivingCount?.ToString() ?? "?"}";
	}

	/// <summary>
	/// Pull the incoming offers out of the trade offers page.
	///
	/// This reads the page a person would look at rather than going through the IEconService API, because that
	/// API needs a Steam Web API key registered against the account - one more thing to set up, one more thing to
	/// leak, and one more thing to go stale. The page is already reachable with the session we hold.
	/// </summary>
	private static List<Offer> Parse(string html) {
		List<Offer> offers = [];

		foreach (Match block in OfferBlock().Matches(html)) {
			if (!ulong.TryParse(block.Groups[1].Value, out ulong id)) {
				continue;
			}

			string body = block.Groups[2].Value;

			// An offer that has already been dealt with is still on the page, greyed out.
			if (body.Contains("tradeoffer_items_banner", StringComparison.Ordinal)) {
				continue;
			}

			Match partner = PartnerId().Match(body);

			if (!partner.Success || !ulong.TryParse(partner.Groups[1].Value, out ulong partnerId)) {
				continue;
			}

			offers.Add(new Offer(id, partnerId, CountItems(body, "primary"), CountItems(body, "secondary")));
		}

		return offers;
	}

	/// <summary>
	/// Items on one side of the offer, or NULL when that side could not be found at all.
	///
	/// The distinction is the whole safety of this module. "You give nothing" and "I could not read the giving
	/// side" both used to come back as 0, so a single change to Steam's markup would have turned every
	/// take-my-items offer into something that looked like a donation and got accepted. A side we cannot read is
	/// now unknown, and unknown is never a donation.
	/// </summary>
	private static int? CountItems(string body, string side) {
		Match section = Regex.Match(body, $"tradeoffer_items {side}(.*?)tradeoffer_items_ctn_end", RegexOptions.Singleline);
		string text = section.Success ? section.Groups[1].Value : "";

		if (text.Length == 0) {
			// Fall back to slicing the block at the other side's marker, for the layout without the end marker.
			int start = body.IndexOf("tradeoffer_items " + side, StringComparison.Ordinal);

			if (start < 0) {
				return null;   // the side is not on the page in any shape we recognise
			}

			int end = body.IndexOf("tradeoffer_items ", start + 20, StringComparison.Ordinal);
			text = end > start ? body[start..end] : body[start..];
		}

		return TradeItem().Matches(text).Count;
	}

	[GeneratedRegex("""id="tradeofferid_(\d+)"(.*?)(?=id="tradeofferid_|<div class="pagebtn|\z)""", RegexOptions.Singleline)]
	private static partial Regex OfferBlock();

	[GeneratedRegex("""steamcommunity\.com/profiles/(\d{17})""")]
	private static partial Regex PartnerId();

	[GeneratedRegex("""class="trade_item[\s"]""")]
	private static partial Regex TradeItem();
}
