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

	protected override async Task RunAsync(CancellationToken ct) {
		// Well clear of everything a sign-in does.
		if (!await Sleep(Rng.Minutes(15, 40), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			TimeSpan wait = TimeSpan.FromMinutes(10);
			if (Bot.Cfg.SpareItems && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && DayAllows
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				try {
					string said = await SpareRun.RunAsync(Bot, act: true, ct).ConfigureAwait(false);
					Log.Debug($"spare items: {Log.Scrub(said.Length > 300 ? said[..300] : said)}", Bot.Name);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at spare items: {0}", Log.Describe(e)), Bot.Name);
				}

				int hours = Math.Clamp(Bot.Cfg.SpareRunHours, 1, 72);
				wait = Rng.Minutes(hours * 45, hours * 75);
				_gate!.Space(15, 90);
				DateTime next = DateTime.Now + wait;
				_status = new Said("next look at spare items around {0}", (Func<string>) (() => Fmt.Clock(next.ToUniversalTime())));
			}

			if (!await Sleep(wait, ct).ConfigureAwait(false)) {
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

	protected override async Task RunAsync(CancellationToken ct) {
		if (!await Sleep(Rng.Minutes(20, 60), ct).ConfigureAwait(false)) {
			return;
		}

		while (!ct.IsCancellationRequested) {
			TimeSpan wait = TimeSpan.FromMinutes(10);
			bool wanted = (Bot.Cfg.OpenBoosterPacks > 0) || (Bot.Cfg.BoosterUnopened > 0);

			if (wanted && Bot.IsOnline && Bot.Web.Ready && !Bot.Paused && DayAllows
				&& (Limiters.RateLimitedFor(WebSession.Community.Host) == TimeSpan.Zero)) {
				try {
					string said = await SpareRun.PacksAsync(Bot, act: true, ct).ConfigureAwait(false);
					Log.Debug($"booster packs: {Log.Scrub(said.Length > 300 ? said[..300] : said)}", Bot.Name);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					throw;
				} catch (Exception e) {
					Log.Debug(new Said("couldn't look at booster packs: {0}", Log.Describe(e)), Bot.Name);
				}

				int hours = Math.Clamp(Bot.Cfg.BoosterCheckHours, 1, 168);
				wait = Rng.Minutes(hours * 50, hours * 70);
				_gate!.Space(15, 90);
				DateTime next = DateTime.Now + wait;
				_status = new Said("next look at booster packs around {0}", (Func<string>) (() => Fmt.Clock(next.ToUniversalTime())));
			}

			if (!await Sleep(wait, ct).ConfigureAwait(false)) {
				return;
			}
		}
	}
}
