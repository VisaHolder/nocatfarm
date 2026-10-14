using System.Globalization;

namespace NocatFarm.Core;

/// <summary>
/// A human-mode account's spare items spread evenly through its day, the way somebody would clear a few now and then -
/// never the day's whole number in one sitting. Each look does about what's left today over the looks left in its day
/// (rounded up), never more than "Items per look, human mode", and the waits between its items fill the time to the
/// next look. Robot accounts aren't spread: they keep "Items at a time".
/// </summary>
/// <remarks>
/// For example 50 a day in a 6-hour day, a look every hour: about 9 a look, the 9 spread over that hour.
/// </remarks>
public static class SpareSpread {
	/// <summary>What one look does, and when the next comes.</summary>
	/// <param name="PerLook">Items this look does.</param>
	/// <param name="Spacing">Time to the next look: "Look every (hours)" - or sooner, when the per-look number would leave
	/// the day's number undone.</param>
	/// <param name="LooksLeft">Looks left in its day, this one counted.</param>
	/// <param name="LeftToday">Items today's limit still allows.</param>
	/// <param name="Ends">When its day ends, local time.</param>
	/// <param name="NextLookUtc">About when the next look is.</param>
	public sealed record Plan(int PerLook, TimeSpan Spacing, int LooksLeft, int LeftToday, DateTime Ends, DateTime NextLookUtc);

	/// <summary>Looks are never closer together than this, whatever has to fit in the day.</summary>
	private static readonly TimeSpan Closest = TimeSpan.FromMinutes(45);

	/// <summary>
	/// The spread for one look: <paramref name="daily"/> a day with <paramref name="done"/> done, at most
	/// <paramref name="cap"/> a look, a look every <paramref name="lookHours"/>, in a day from <paramref name="wake"/> to
	/// <paramref name="ends"/> (local). Null when its day is over - nothing to spread over.
	/// </summary>
	/// <remarks>
	/// Never more than twice the day's even share in one look: the day's count starts again at midnight, and an account
	/// still up at five past twelve would otherwise do the whole new day's number in its last look.
	/// </remarks>
	public static Plan? For(int daily, int done, int cap, int lookHours, DateTime nowLocal, DateTime wake, DateTime ends) {
		if (ends <= nowLocal) {
			return null;
		}

		cap = Math.Max(1, cap);
		daily = Math.Max(1, daily);
		TimeSpan every = TimeSpan.FromHours(Math.Max(1, lookHours));
		TimeSpan rest = ends - nowLocal;
		int left = Math.Max(0, daily - Math.Max(0, done));
		int looksLeft = Math.Max(1, (int) Math.Ceiling(rest / every));
		int dayLooks = Math.Max(looksLeft, (int) Math.Ceiling((ends - (wake < nowLocal ? wake : nowLocal)) / every));
		int perLook = Math.Max(1, Math.Min(Math.Min(Up(left, looksLeft), 2 * Up(daily, dayLooks)), cap));
		TimeSpan spacing = every;

		// The per-look number leaves part of the day's undone at this pace: looks come sooner, evenly over what's left.
		int needed = Up(left, cap);

		if ((perLook == cap) && (needed > looksLeft)) {
			TimeSpan sooner = rest / needed;
			spacing = sooner < Closest ? Closest : sooner > every ? every : sooner;
		}

		return new Plan(perLook, spacing, looksLeft, left, ends, DateTime.UtcNow + spacing);
	}

	/// <summary>
	/// The account's spread now, or null: on a robot account, or a human-mode one whose day isn't worked out yet (or is
	/// over) - those keep the per-look number as it's set.
	/// </summary>
	public static Plan? Of(Bot bot, SpareLedger ledger, SpareRules rules) {
		if (!bot.Cfg.LegitMode || (Modules.HumanMode.DayWindow(bot) is not { } day)) {
			return null;
		}

		DateTime now = DateTime.Now;
		bool today = ledger.Day == now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		if (For(rules.DailyItems, today ? ledger.DoneItems : 0, Math.Clamp(bot.Cfg.SpareHumanPerLook, 1, 50), Math.Clamp(bot.Cfg.SpareRunHours, 1, 72), now,
			day.Wake, day.Ends) is not { } plan) {
			return null;
		}

		// The next look as the module has it, when one is planned; otherwise this one's spacing from now.
		DateTime? planned = BotManager.ModuleOf<Modules.SpareItems>(bot)?.Timer.Next;

		return planned is DateTime at && (at > DateTime.UtcNow) ? plan with { NextLookUtc = at } : plan;
	}

	/// <summary>
	/// The wait between two items of a spread look: about the time to the next look over the items in this one, so they
	/// spread over it - give or take 30%, and never outside the waits set.
	/// </summary>
	public static (TimeSpan Lo, TimeSpan Hi) Gap(TimeSpan lo, TimeSpan hi, Plan plan) {
		if (hi < lo) {
			hi = lo;
		}

		TimeSpan even = plan.Spacing / Math.Max(1, plan.PerLook);
		TimeSpan centre = even < lo ? lo : even > hi ? hi : even;
		TimeSpan from = centre * 0.7, to = centre * 1.3;

		return (from < lo ? lo : from, to > hi ? hi : to);
	}

	/// <summary>"spread over its day: about 9 a look, next look ~14:20" - or that this is its last look today.</summary>
	public static Said Said(Plan plan) => plan.NextLookUtc.ToLocalTime() >= plan.Ends
		? new Said("spread over its day: about {0} a look - its last look today", plan.PerLook)
		: new Said("spread over its day: about {0} a look, next look ~{1}", plan.PerLook, Fmt.Clock(plan.NextLookUtc));

	private static int Up(int a, int b) => (a + Math.Max(1, b) - 1) / Math.Max(1, b);
}
