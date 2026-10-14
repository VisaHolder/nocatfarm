using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

/// <summary>
/// 'points': Steam Points - the balance, and spending them on levels (Modules/PointsSpender.cs). Spending is permanent, so
/// it previews first, and an account spends nothing until spending is turned on ('points on &lt;account&gt;', or its switch
/// after the preview).
/// </summary>
public static partial class Commands {
	internal const string PointsUsage = "points [account|all] | points spend preview [account|all] | points on|off|spend <account>";

	/// <summary>What a 'points' line asks for.</summary>
	internal enum PointsAsk { List, Preview, Spend, Ok, Hold, Bad }

	/// <summary>
	/// Read a 'points' line's words, in any order - from Steam chat the account's name comes straight after 'points'. The words
	/// spend, preview, ok and hold ask; anything else is the account (or 'all').
	/// </summary>
	internal static (PointsAsk Ask, string? Who) ParsePoints(string[] args) {
		bool spend = false, preview = false, ok = false, hold = false, all = false;
		string? who = null;

		foreach (string word in args) {
			switch (word.ToLowerInvariant()) {
				case "spend" when !spend:
					spend = true;

					break;
				case "preview" when !preview:
					preview = true;

					break;
				case "ok" or "on" when !ok && !hold:   // 'ok' and 'hold': the old words, still understood
					ok = true;

					break;
				case "hold" or "off" when !hold && !ok:
					hold = true;

					break;
				case "all" when !all && (who == null):
					all = true;

					break;
				default:
					if ((who != null) || all) {
						return (PointsAsk.Bad, null);
					}

					who = word;

					break;
			}
		}

		if (ok || hold) {
			// One account by name: agreeing to spend is never said for every account at once.
			return spend || preview || all || (who == null) ? (PointsAsk.Bad, null) : (ok ? PointsAsk.Ok : PointsAsk.Hold, who);
		}

		if (preview) {
			return (PointsAsk.Preview, who);
		}

		if (spend) {
			return all || (who == null) ? (PointsAsk.Bad, null) : (PointsAsk.Spend, who);
		}

		return (PointsAsk.List, who);
	}

	/// <summary>
	/// Spending points, or agreeing to it, is the owner's word - not something a Steam-chat master gives for them. Turning it
	/// off ('points off', 'points hold') is fine from anywhere, as 'spare off' is: it only stops things.
	/// </summary>
	/// <remarks>By the words alone, not by whether the line is complete: Steam chat puts the account's name in afterwards, so a
	/// bare 'points ok' there would become 'points &lt;account&gt; ok'.</remarks>
	internal static bool PointsNeedsOwner(string[] args) {
		HashSet<string> words = [.. args.Select(static a => a.ToLowerInvariant())];

		return words.Contains("ok") || words.Contains("on") || (words.Contains("spend") && !words.Contains("preview"));
	}

	private static async Task<string> PointsAsync(BotManager mgr, string[] args) {
		(PointsAsk ask, string? who) = ParsePoints(args);

		if (ask == PointsAsk.Bad) {
			return PointsUsage;
		}

		if (ask is PointsAsk.Ok or PointsAsk.Hold or PointsAsk.Spend) {
			if (mgr.Get(who!) is not { } bot) {
				return NoSuchAccount(mgr, who!);
			}

			return ask switch {
				PointsAsk.Ok => PointsOk(bot, true),
				PointsAsk.Hold => PointsOk(bot, false),
				_ => await PointsLookAsync([bot], PointsSpender.Mode.Now).ConfigureAwait(false)
			};
		}

		if (Pick(mgr, who == null ? [] : [who], out string? problem) is not { } bots) {
			return problem!;
		}

		if (ask == PointsAsk.Preview) {
			return await PointsLookAsync(bots, PointsSpender.Mode.Preview).ConfigureAwait(false);
		}

		string[] lines = await Task.WhenAll(bots.Select(static async b => !b.IsOnline
			? $"{b.Name}: not logged in"
			: await b.GetPointsAsync().ConfigureAwait(false) is { } points
				? $"{b.Name}: {points.ToString("N0", CultureInfo.InvariantCulture)} Steam Points · {SpendingState(b)}"
				: $"{b.Name}: Steam didn't say")).ConfigureAwait(false);

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>Whether and how an account spends its points, in a few words.</summary>
	internal static string SpendingState(Bot b) =>
		!b.Cfg.SpendPoints || !PointsLedger.Approved(b) ? "spending on levels is off"
		: $"spends on levels: keeps {PointsPlan.N(b.Cfg.PointsKeepAtLeast)}, at most {PointsPlan.N(b.Cfg.PointsWeeklyCap)} a week";

	/// <summary>
	/// 'points on' and 'points off' (and the old 'points ok' / 'points hold'): the switch and its go-ahead together - kept with
	/// its Steam Points spending (state/points-&lt;account&gt;.json) as well as in its settings (see <see cref="Switches"/>).
	/// </summary>
	internal static string PointsOk(Bot bot, bool ok) {
		Switches.Set(bot, Switches.Points, ok);

		if (!ok) {
			return $"{bot.Name}: spending Steam Points is off - nothing is spent.";
		}

		List<string> lines = [
			$"{bot.Name}: spending Steam Points is on - it buys badge levels by itself, keeping {PointsPlan.N(bot.Cfg.PointsKeepAtLeast)} points and spending at most {PointsPlan.N(bot.Cfg.PointsWeeklyCap)} a week.",
			$"{bot.Name}: 'points spend preview {bot.Name}' shows what it would buy; 'points off {bot.Name}' turns it off."
		];

		if (bot.Cfg.LegitMode) {
			lines.Add($"{bot.Name}: it's a human-mode account, so it only buys during its day.");
		}

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>'points spend preview' and 'points spend': each account's look, said plainly.</summary>
	private static Task<string> PointsLookAsync(IReadOnlyList<Bot> bots, PointsSpender.Mode mode) {
		async Task<string> Look(Bot b) {
			PointsSpender spender = BotManager.ModuleOf<PointsSpender>(b)!;
			PointsSpender.Result result = await spender.LookAsync(mode, CancellationToken.None).ConfigureAwait(false);

			return string.Join(Environment.NewLine, PointsSaid(b, mode, result));
		}

		Slow job = mode == PointsSpender.Mode.Preview ? new("points preview", "looking at its Steam Points") : new("points spend", "spending Steam Points on levels");

		return SlowAsync(job, bots, Look, ReplyWithin, oneAtATime: true,
			cannot: static b => !b.IsOnline || !b.Web.Ready || (BotManager.ModuleOf<PointsSpender>(b) is null) ? $"{b.Name}: not logged in" : null);
	}

	/// <summary>One account's look in plain words: the figures, what it would buy or bought, and why not when nothing.</summary>
	public static List<string> PointsSaid(Bot b, PointsSpender.Mode mode, PointsSpender.Result result) {
		string name = b.Name;
		List<string> lines = [];

		if (result.Plan is not { } plan) {
			lines.Add($"{name}: {(result.Problem.Length > 0 ? result.Problem : "couldn't look")} - try again in a minute");

			return lines;
		}

		lines.Add($"{name}: {PointsPlan.N(plan.Balance)} points, keeps {PointsPlan.N(plan.Keep)}, {PointsPlan.N(plan.WeekSpent)} of {PointsPlan.N(plan.WeekCap)} spent this week - {PointsPlan.N(plan.Spare)} can be spent");

		if (result.Bought.Count > 0) {
			lines.AddRange(result.Bought.Select(s =>
				$"{name}: bought {s.Levels} level(s) of {s.Badge.Name} (level {s.From} to {s.To}) for {PointsPlan.N(s.Cost)} points: +{s.Xp} XP"));
		} else {
			string verb = mode == PointsSpender.Mode.Preview ? "would buy" : "would have bought";
			lines.AddRange(plan.Steps.Select(s =>
				$"{name}: {verb} {s.Levels} level(s) of {s.Badge.Name} (level {s.From} to {s.To}) for {PointsPlan.N(s.Cost)} points: +{s.Xp} XP"));
		}

		lines.AddRange(plan.Notes.Select(n => $"{name}: {n}"));

		if ((plan.Steps.Count == 0) && (plan.Notes.Count == 0)) {
			lines.Add($"{name}: nothing to buy now");
		}

		if (result.Problem.Length > 0) {
			lines.Add($"{name}: {result.Problem}");
		}

		if ((plan.Steps.Count > 0) && (!PointsLedger.Approved(b) || !b.Cfg.SpendPoints)) {
			lines.Add($"{name}: preview only - nothing is spent while spending is off. 'points on {name}' (or the switch in its settings) turns it on.");
		}

		return lines;
	}
}
