using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

// The 'scamwatch' and 'health' commands.
public static partial class Commands {
	/// <summary>
	/// 'scamwatch [account|all]': the last look and what it found. 'scamwatch ok &lt;account&gt;': the warnings were you -
	/// clear them, and trades go back to normal.
	/// </summary>
	private static string ScamWatchCommand(BotManager mgr, string[] args) {
		// 'scamwatch kylro ok' is 'scamwatch ok kylro' too - the order it reads in when the account is said first.
		if ((args.Length == 2) && args[1].Equals("ok", StringComparison.OrdinalIgnoreCase) && (mgr.Get(args[0]) != null)) {
			args = [args[1], args[0]];
		}

		if ((args.Length > 0) && args[0].Equals("ok", StringComparison.OrdinalIgnoreCase)) {
			if (args.Length < 2) {
				return "scamwatch ok <account>";
			}

			if (mgr.Get(args[1]) is not { } bot) {
				return NoSuchAccount(mgr, args[1]);
			}

			if (BotManager.ModuleOf<ScamWatch>(bot) is not { } watch) {
				return new Said("{0}: nothing to clear", bot.Name).ToString();
			}

			int had = watch.Clear();

			return had == 0
				? new Said("{0}: no warnings to clear", bot.Name).ToString()
				: new Said("{0}: {1} warning(s) cleared - trades back to normal", bot.Name, had).ToString();
		}

		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		return string.Join(Environment.NewLine, bots.Select(static b => ScamWatchLine(b).ToString()));
	}

	/// <summary>One account's scam watch, in a line.</summary>
	internal static Said ScamWatchLine(Bot bot) {
		if (BotManager.ModuleOf<ScamWatch>(bot) is not { } watch) {
			return new Said("{0}: nothing to show", bot.Name);
		}

		string when = watch.CheckedAt is { } at ? Fmt.Clock(at) : "";
		IReadOnlyList<ScamWatch.Hit> hits = watch.Hits;

		if (hits.Count > 0) {
			string what = string.Join("; ", hits.Select(static h => $"{ScamWatch.What(h)} ({Fmt.Clock(h.At)})"));

			return watch.HoldingTrades
				? new Said("{0}: WARNING - {1} - trades on hold; 'scamwatch ok {0}' once you've checked it was you", bot.Name, what)
				: new Said("{0}: WARNING - {1} - 'scamwatch ok {0}' once you've checked it was you", bot.Name, what);
		}

		if (!bot.Cfg.WatchForScams) {
			return new Said("{0}: scam watch is off", bot.Name);
		}

		if (when.Length == 0) {
			return new Said("{0}: no look yet - the first is a few minutes after it signs in", bot.Name);
		}

		return watch.LastDetails is { } d
			? new Said("{0}: all clear, last look {1} - email {2}, phone {3}, Steam Guard {4}", bot.Name, when,
				d.Email.Length > 0 ? d.Email : "?", ScamWatch.PhoneWords(d.Phone), ScamWatch.GuardWords(d.Guard))
			: new Said("{0}: all clear, last look {1}", bot.Name, when);
	}

	/// <summary>'health [account|all]': each account's health, item by item. Checks now when it never has and it's signed in.</summary>
	private static async Task<string> HealthCommandAsync(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		List<string> lines = [];

		foreach (Bot bot in bots) {
			if (BotManager.ModuleOf<AccountHealth>(bot) is not { } health) {
				continue;
			}

			AccountHealth.Facts? facts = health.Last;

			if ((facts == null) && bot.IsOnline && bot.Web.Ready) {
				facts = await health.CheckAsync().ConfigureAwait(false);
			}

			lines.Add(HealthText(bot, facts, health.CheckedAt).ToString());
		}

		return lines.Count == 0 ? new Said("nothing to show").ToString() : string.Join(Environment.NewLine, lines);
	}

	/// <summary>"kylro: market: blocked for 7 day(s) (new device) · level 12 - bans: none, trade hold: none, ... (checked 14:20)".</summary>
	internal static Said HealthText(Bot bot, AccountHealth.Facts? facts, DateTime? checkedAt) {
		if (!bot.Cfg.WatchHealth) {
			return new Said("{0}: account health is off", bot.Name);
		}

		if (facts == null) {
			return new Said("{0}: not checked yet - it checks a few minutes after signing in", bot.Name);
		}

		string items = string.Join(", ", AccountHealth.Assess(facts).Where(static i => i.Level != AccountHealth.Level.Unknown).Select(static i => i.Words.ToString()));
		string level = facts.SteamLevel is { } l ? new Said("level {0}", l).ToString() : "";
		string when = checkedAt is { } at ? Fmt.Clock(at) : "?";

		return new Said("{0}: {1} - {2} (checked {3})", bot.Name, AccountHealth.Line(facts),
			string.Join(", ", new[] { level, items }.Where(static p => p.Length > 0)), when);
	}
}
