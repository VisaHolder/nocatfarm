using System.Text.RegularExpressions;
using NocatFarm.Core;
using SteamKit2;

namespace NocatFarm.Modules;

/// <summary>
/// Accepts the gifts people send this account - ArchiSteamFarm's AcceptGifts.
/// </summary>
/// <remarks>
/// Two kinds: guest passes (free trials), which are redeemed over the client connection, and Steam wallet gift
/// cards, which sit on the store's pending-gifts page until somebody presses accept. Steam pushes the number of
/// waiting gifts on login and whenever it changes, so the page is only read when there is something on it.
/// A game sent as a gift is only pointed out, not accepted: that one is a choice the account's owner makes
/// (add it to the library, or turn it down so the sender gets a refund).
/// </remarks>
public sealed partial class Gifts(Bot bot) : BotModule(bot) {
	private readonly SemaphoreSlim _poke = new(0);
	private readonly HashSet<ulong> _handled = [];
	private Said _status = new("");

	public override string Name => "gifts";
	public override string Status => Bot.Cfg.AcceptGifts ? _status : "";

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

				if (waiting && Bot.Cfg.AcceptGifts && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused) {
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
						await GuestPassesAsync(ct).ConfigureAwait(false);
						await GiftCardsAsync(ct).ConfigureAwait(false);
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

	private async Task GuestPassesAsync(CancellationToken ct) {
		foreach (ulong gid in Bot.GuestPasses.Where(g => !_handled.Contains(g)).ToList()) {
			_handled.Add(gid);

			AsyncJob<SteamApps.RedeemGuestPassResponseCallback>? job = Bot.Notifications?.RedeemGuestPass(gid);

			if (job == null) {
				return;   // dropped off - the list is sent again on the next login
			}

			try {
				SteamApps.RedeemGuestPassResponseCallback answer = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

				if (answer.Result == EResult.OK) {
					Log.Good(answer.PackageID != 0 ? new Said("accepted a guest pass (package {0})", answer.PackageID) : new Said("accepted a guest pass"), Bot.Name);
				} else {
					Log.Info(new Said("couldn't accept a guest pass - Steam said {0}", answer.Result), Bot.Name);
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
				Log.Attention(new Said("a game was sent to this account as a gift - accept or decline it on the store's gifts page"), Bot.Name);

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
}
