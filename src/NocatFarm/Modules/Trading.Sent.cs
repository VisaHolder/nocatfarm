using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// 1.8.5: what became of the offers this account sent - by nocat.farm or by hand on Steam. reap sent a friend a bunch of
/// trades and nothing in the log said when they went through; he had to sign in to the account to see.
/// </summary>
public sealed partial class Trading {
	/// <summary>How often the sent offers are looked at - one Web API read (not the community site), whatever else is going on.</summary>
	public static TimeSpan SentGap { get; set; } = TimeSpan.FromMinutes(15);

	/// <summary>Test seam: the sent offers live or changed since a time, instead of asking Steam.</summary>
	public Func<DateTime, CancellationToken, Task<List<TradeOffers.Offer>?>>? ReadSent { get; set; }

	/// <summary>Test seam: a partner's name, instead of asking Steam.</summary>
	public Func<ulong, CancellationToken, Task<string>>? ReadName { get; set; }

	private DateTime _sentLooked;

	/// <summary>
	/// What's kept between looks (config/state/sent-&lt;account&gt;.json): from when the next look reads, and the offers already said
	/// - each one once, restarts and updates included.
	/// </summary>
	public sealed record SentState(DateTime Since, List<ulong> Said);

	private static string SentPath(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"sent-{account}.json");

	public static SentState LoadSent(string account) {
		try {
			string path = SentPath(account);

			return (File.Exists(path) ? JsonSerializer.Deserialize<SentState>(File.ReadAllText(path)) : null) ?? new SentState(default, []);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return new SentState(default, []);
		}
	}

	private static void SaveSent(string account, SentState state) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(SentPath(account))!);
			AtomicFile.Write(SentPath(account), JsonSerializer.Serialize(state));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("couldn't note which sent offers were said: {0}", Log.Describe(e)), account);
		}
	}

	/// <summary>
	/// The line for a sent offer that ended - accepted, declined, countered, expired, or gone for Steam's own reasons. Null
	/// for one still out, and for one its sender took back (by hand, 'trade cancel' or scam watch: nothing to tell).
	/// </summary>
	public static Said? SentSaid(TradeOffers.Offer offer, string who) => offer.State switch {
		TradeOffers.Accepted => offer.Receiving.Count == 0
			? new Said("{0} accepted your trade offer · you gave {1}", who, Items(offer.Giving))
			: new Said("{0} accepted your trade offer · you gave {1}, got {2}", who, Items(offer.Giving), Items(offer.Receiving)),
		TradeOffers.Declined => new Said("{0} declined your trade offer (you'd have given {1})", who, Items(offer.Giving)),
		TradeOffers.Countered => new Said("{0} answered your trade offer with a counter-offer", who),
		TradeOffers.Expired => new Said("your trade offer to {0} expired", who),
		TradeOffers.CanceledBySecondFactor => new Said("your trade offer to {0} was cancelled - it wasn't confirmed in time", who),
		TradeOffers.InvalidItems => new Said("your trade offer to {0} can't go through any more - its items are gone", who),
		_ => null
	};

	/// <summary>1.8.5: what became of the offers it sent (by hand too), every <see cref="SentGap"/> - asleep or not, it's only reading.</summary>
	private async Task SentLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			if (Bot.IsOnline && Bot.Web.Ready) {
				try {
					await WatchSentAsync(ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					return;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't check sent trade offers: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), Bot.Name);
				}
			}

			try {
				await Task.Delay(SentGap > TimeSpan.FromMinutes(1) ? SentGap : TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}
	}

	/// <summary>
	/// One look at the sent offers every <see cref="SentGap"/>: each one that ended since the last look is said once - the log
	/// (green when accepted), the trades pop-up and Telegram/Discord's Trades topic. The first look ever starts from now:
	/// nothing from before is dug up.
	/// </summary>
	public async Task WatchSentAsync(CancellationToken ct) {
		DateTime now = DateTime.UtcNow;

		if (now - _sentLooked < SentGap) {
			return;
		}

		_sentLooked = now;
		SentState state = LoadSent(Bot.Name);

		if (state.Since == default) {
			SaveSent(Bot.Name, new SentState(now, []));

			return;
		}

		if (await (ReadSent?.Invoke(state.Since, ct) ?? TradeOffers.SentChangedAsync(Bot, state.Since, ct)).ConfigureAwait(false) is not { } sent) {
			return;   // Steam didn't answer: the same stretch is read again next time
		}

		List<ulong> said = [.. state.Said];

		foreach (TradeOffers.Offer offer in sent.Where(o => o.Ours && !said.Contains(o.Id))) {
			string who = await (ReadName?.Invoke(offer.Partner, ct) ?? SteamNames.OfAsync(Bot, offer.Partner, ct)).ConfigureAwait(false);

			if (SentSaid(offer, who) is { } line) {
				Log.Trade(line, Bot.Name, good: offer.State == TradeOffers.Accepted);
				said.Add(offer.Id);
				SaveSent(Bot.Name, new SentState(state.Since, [.. said.TakeLast(500)]));   // 1.8.6: never said twice, even if stopped part-way
			}
		}

		// A few minutes' overlap, so an offer that changed as Steam answered isn't missed - the ids said keep it to once.
		SaveSent(Bot.Name, new SentState(now.AddMinutes(-5), [.. said.TakeLast(500)]));
	}
}
