using System.Globalization;
using NocatFarm.Config;

namespace NocatFarm.Modules;

/// <summary>
/// "Reaction speed" on a human-mode account: how quickly it reacts to people and how long its breaks last, as one choice
/// instead of a dozen pairs of minutes.
///
/// It never rewrites the numbers in the account's settings. Each time a wait is picked, the configured shortest and longest
/// are scaled - Quick halves them, Relaxed doubles them - and the wait is still drawn at random between the two, by the same
/// code as before. Normal hands the configured numbers back untouched, so an account left on Normal draws exactly the waits
/// it always did, with the same random calls.
///
/// Only waits and reactions. How much it plays (sittings, daily hours, the day's plan), card and drop timing, achievement
/// pacing and hour targets are never scaled. Nor is anything that protects the owner or keeps within Steam's limits: the
/// three minutes and the clear reads after signing in before any game starts (HumanMode.SafeToPlay), the wait before
/// picking back up after you play, rep4rep's limits and gaps, and the gaps between web requests and logins - none of those
/// read through here.
/// </summary>
public static class ReactionSpeed {
	public const int Normal = 0;
	public const int Quick = 1;
	public const int Relaxed = 2;

	/// <summary>
	/// The settings it scales: each pair (shortest, longest), or a single wait. Anything not in here is left alone - this
	/// list is also what the tests and the docs go by.
	/// </summary>
	public static readonly IReadOnlyList<(string Min, string? Max)> Scaled = [
		(nameof(BotConfig.GiftDelayMinMinutes), nameof(BotConfig.GiftDelayMaxMinutes)),
		(nameof(BotConfig.TradeDelayMinMinutes), nameof(BotConfig.TradeDelayMaxMinutes)),
		(nameof(BotConfig.FriendRequestDelayMinMinutes), nameof(BotConfig.FriendRequestDelayMaxMinutes)),
		(nameof(BotConfig.WakeDelayMinMinutes), nameof(BotConfig.WakeDelayMaxMinutes)),
		(nameof(BotConfig.QuietDelayMinMinutes), nameof(BotConfig.QuietDelayMaxMinutes)),
		(nameof(BotConfig.WarmUpMinMinutes), nameof(BotConfig.WarmUpMaxMinutes)),
		(nameof(BotConfig.BreakMinMinutes), nameof(BotConfig.BreakMaxMinutes)),
		(nameof(BotConfig.BreakAwayAfterMinMinutes), nameof(BotConfig.BreakAwayAfterMaxMinutes)),
		(nameof(BotConfig.MealBreakMinutes), null),
		(nameof(BotConfig.AutoReplyDelaySeconds), null),
		(nameof(BotConfig.LegitStopMaxSeconds), null)
	];

	/// <summary>How much to scale by on this account: 1 exactly on Normal and on any account not in human mode.</summary>
	public static double Factor(BotConfig cfg) => !cfg.LegitMode ? 1.0 : cfg.ReactionSpeed switch {
		Quick => 0.5,
		Relaxed => 2.0,
		_ => 1.0
	};

	/// <summary>
	/// A configured pair as it is right now: (shortest, longest) of the two settings, scaled. On Normal the two numbers
	/// exactly as configured - an inverted pair included, which the code that draws from it already handles.
	/// </summary>
	public static (int Min, int Max) Range(BotConfig cfg, string min, string max) =>
		Pair(cfg, Read(cfg, min), Read(cfg, max), Cap(max));

	/// <summary>One configured wait (a meal's length, the reply delay), scaled.</summary>
	public static int One(BotConfig cfg, string name) => Scale(Factor(cfg), Read(cfg, name), Cap(name));

	/// <summary>A wait written in the code rather than a setting (the extra after waking), scaled the same way.</summary>
	public static (int Min, int Max) Fixed(BotConfig cfg, int min, int max) => Pair(cfg, min, max, int.MaxValue);

	private static (int Min, int Max) Pair(BotConfig cfg, int min, int max, int cap) {
		double f = Factor(cfg);

		if (f == 1.0) {
			return (min, max);
		}

		int lo = Scale(f, min, cap);
		int hi = Scale(f, max, cap);

		return (lo, Math.Max(lo, hi));
	}

	/// <summary>
	/// One number, scaled and rounded to the nearest whole one. Zero stays zero (a wait switched off stays off), anything
	/// above zero stays at least 1, and nothing goes past the setting's own top.
	/// </summary>
	private static int Scale(double f, int value, int cap) {
		if ((f == 1.0) || (value <= 0)) {
			return value;
		}

		int scaled = (int) Math.Round(value * f, MidpointRounding.AwayFromZero);

		return Math.Max(1, Math.Min(scaled, Math.Max(cap, value)));
	}

	private static int Read(BotConfig cfg, string name) =>
		Convert.ToInt32(Settings.Read(cfg, name) ?? 0, CultureInfo.InvariantCulture);

	/// <summary>The highest the setting itself may be - a doubled wait doesn't go past what could have been typed.</summary>
	private static int Cap(string name) =>
		(Settings.FindBot(name) is { } def) && (def.Max < int.MaxValue) ? (int) def.Max : int.MaxValue;
}
