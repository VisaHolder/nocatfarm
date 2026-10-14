using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// With "Deal with spare items" on: every so often, while the account is awake, look at the Steam inventory and turn a few
/// spare cards, backgrounds and emoticons into gems or sell them - whichever pays more - then pack or send the gems as
/// chosen. The first look on an account only posts what it would do and waits for <c>spare ok</c>. Replaces the old
/// "Sell duplicate cards", whose listing (and taking down stale listings) it does as one of its two choices.
/// </summary>
/// <remarks>
/// A human-mode account looks only in its own day (a listing carries the account's name and the time), with longer waits
/// between items. Turning items into gems needs no phone confirmation, so it completes there by itself.
/// </remarks>
public sealed class SpareItems(Bot bot) : BotModule(bot) {
	private Said _status = new("");

	public override string Name => "spare";
	public override string Status => Bot.Cfg.SpareItems ? _status : "";

	private HumanGate? _gate;

	/// <summary>The gate a human-mode account keeps to: its own day, settled in, you not playing on it (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>Whether the account's day lets it act now: any time without human mode, only in its own day with it.</summary>
	public bool DayAllows => Gate.Open;

	/// <summary>When the next look is - from "Look every (hours)" as it's set now, so changing it moves a look already planned.</summary>
	internal LookTimer Timer => _timer ??= new LookTimer(() => Math.Clamp(Bot.Cfg.SpareRunHours, 1, 72), 0.75, 1.25);

	private LookTimer? _timer;

	/// <summary>
	/// A human-mode account's next look that does items, by its spread through the day (see <see cref="SpareSpread"/>). A
	/// look before then - one that comes back soon for items still waiting for a price - only prices: acting in it did the
	/// next look's items early, a burst again.
	/// </summary>
	private DateTime _actAt = DateTime.MinValue;

	/// <summary>When the market is next looked at for a listing of ours waiting on its confirmation (see <see cref="SpareRun.CheckPendingAsync"/>).</summary>
	private DateTime _pendingAt = DateTime.MinValue;

	protected override async Task RunAsync(CancellationToken ct) {
		// Well clear of everything a sign-in does.
		if (!await Sleep(Rng.Minutes(15, 40), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			if (Bot.Cfg.SpareItems && Timer.Due(DateTime.UtcNow) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && DayAllows
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				SpareRun.Ran? ran = null;
				bool acted = !Bot.Cfg.LegitMode || (DateTime.UtcNow >= _actAt);

				try {
					// In the background it prices many items a look - one market request each - and comes back soon while
					// some still wait for a price.
					ran = await SpareRun.LookAndDoAsync(Bot, act: acted, SpareRun.QuotesBackground, ct).ConfigureAwait(false);

					// A 'spare do' is working on the account: this look waits for it to finish - asked again in a minute.
					if (ran.Busy) {
						if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
							return;
						}

						continue;
					}

					// Its answer on one line - every item it did is already a line of its own.
					string said = SpareRun.OneLine(ran.Said);
					Log.Debug($"spare items: {Log.Scrub(said.Length > 300 ? said[..300] : said)}", Bot.Name);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at spare items: {0}", Log.Describe(e)), Bot.Name);
				}

				Timer.Looked(DateTime.UtcNow);

				// A human-mode account spreads its items through its day: the next look that does any is the spread's time
				// away, give or take a fifth. What's left waits for it.
				if (Bot.Cfg.LegitMode && acted && (ran?.Spacing is TimeSpan spacing)) {
					_actAt = DateTime.UtcNow + (spacing * (0.8 + (Random.Shared.NextDouble() * 0.4)));

					if (ran.MoreToDo) {
						Timer.LookAt(_actAt);
					}
				} else if ((ran?.MoreToDo == true) && Bot.Cfg.LegitMode) {
					// No day to spread over yet: what's left waits for a later look in its day, not the next one.
					Timer.LookAt(DateTime.UtcNow + Rng.Minutes(45, 120));
				}

				if (ran?.StillPricing == true) {
					Timer.LookAt(DateTime.UtcNow + Rng.Minutes(5, 10));
				}

				// Spaced after a look that did items; one that only priced did nothing anybody sees.
				if (acted) {
					_gate!.Space(15, 90);
				}

				_status = new Said("next look at spare items around {0}", (Func<string>) (() => Fmt.Clock(Timer.Next)));
			}

			// A listing waiting on its confirmation in the Steam app: a light look at the market every 5 to 10 minutes, only
			// while one waits, so "X is on the market" (or declined) is said minutes after - not at the next look, hours away.
			if ((DateTime.UtcNow >= _pendingAt) && Bot.IsOnline && Bot.Web.Ready && OurListings.Undecided(Bot.Name)
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				_pendingAt = DateTime.UtcNow + Rng.Minutes(5, 10);

				try {
					await SpareRun.CheckPendingAsync(Bot, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at the listings waiting on a confirmation: {0}", Log.Describe(e)), Bot.Name);
				}
			}

			// Asked again every minute, so a changed setting moves the next look straight away.
			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}
}

/// <summary>
/// Opens booster packs as "Open booster packs" says - always, only when the cards are worth more than the sealed pack,
/// or never - and leaves alone any pack that's new, on the market or in a trade offer. A human-mode account opens them
/// only in its day. What comes out is written down, pack by pack; the cards then follow the spare-items settings.
/// </summary>
public sealed class PackOpener(Bot bot) : BotModule(bot) {
	private Said _status = new("");

	public override string Name => "packs";
	public override string Status => Bot.Cfg.OpenBoosterPacks > 0 ? _status : "";

	private HumanGate? _gate;

	/// <summary>The gate a human-mode account keeps to: its own day, settled in, you not playing on it (no effect on a robot).</summary>
	internal HumanGate Gate => _gate ??= HumanGate.OwnDay(Bot);

	/// <summary>Whether the account's day lets it act now: any time without human mode, only in its own day with it.</summary>
	public bool DayAllows => Gate.Open;

	/// <summary>When the next look is - from "Look for packs every (hours)" as it's set now, so changing it moves a look already planned.</summary>
	internal LookTimer Timer => _timer ??= new LookTimer(() => Math.Clamp(Bot.Cfg.BoosterCheckHours, 1, 168), 50 / 60d, 70 / 60d);

	private LookTimer? _timer;

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Minutes(20, 60), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			bool wanted = (Bot.Cfg.OpenBoosterPacks > 0) || (Bot.Cfg.BoosterUnopened > 0);

			if (wanted && Timer.Due(DateTime.UtcNow) && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && DayAllows
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				try {
					string said = await SpareRun.PacksAsync(Bot, act: true, ct).ConfigureAwait(false);
					Log.Debug($"booster packs: {Log.Scrub(said.Length > 300 ? said[..300] : said)}", Bot.Name);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at booster packs: {0}", Log.Describe(e)), Bot.Name);
				}

				Timer.Looked(DateTime.UtcNow);
				_gate!.Space(15, 90);
				_status = new Said("next look at booster packs around {0}", (Func<string>) (() => Fmt.Clock(Timer.Next)));
			}

			// Asked again every minute, so a changed setting moves the next look straight away.
			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}
}
