using System.Globalization;
using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

/// <summary>'sets [account]': the cheapest whole card sets on the market, from the set finder (Core/SetFinder.cs) - 1.8.2. Read-only.</summary>
public static partial class Commands {
	internal const string SetsUsage = "sets [account]";

	/// <summary>Sets 'sets' lists.</summary>
	internal const int SetsShown = 20;

	private static Task<string> SetsAsync(BotManager mgr, string[] args) {
		if (args.Length > 1) {
			return Task.FromResult(SetsUsage + " - one account, or none for every game.");
		}

		SetFinder finder = SetFinder.Shared;
		DateTime now = DateTime.UtcNow;
		finder.Asked(now);   // asked: it looks for the next day even when no account buys full sets
		SetFinder.Coverage covered = finder.Covered(now);

		if (args.Length == 0) {
			return Task.FromResult(SetsSaid(null, finder.Cheapest(null, 0, SetsShown, now), 0, covered));
		}

		if (mgr.Get(args[0]) is not { } bot) {
			return Task.FromResult(NoSuchAccount(mgr, args[0]));
		}

		// Its badges (one Web API read) and its wallet's currency (as Steam last said it) - nothing is bought or changed.
		return SlowAsync(new Slow("sets", "reading its badges", Key: "sets"), [bot], async b => {
			if (await new SteamCardMarket(b).BadgeLevelsAsync(CancellationToken.None).ConfigureAwait(false) is not { } levels) {
				return $"{b.Name}: Steam wouldn't say what its badges are - try again in a minute.";
			}

			int cur = b.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) b.WalletCurrency;

			return SetsSaid(b.Name, finder.Cheapest(levels, cur, SetsShown, DateTime.UtcNow), cur, covered);
		}, ReplyWithin, cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null);
	}

	/// <summary>
	/// 'sets', written out: the cheapest whole sets - for an account, only badges it can still level, in its wallet's money
	/// at the rate learned from the market - and how much of the market the finder has looked at. English, like every reply.
	/// </summary>
	public static string SetsSaid(string? name, IReadOnlyList<SetFinder.Row> rows, int currency, SetFinder.Coverage covered) {
		string who = name == null ? "" : $"{name}: ";
		List<string> lines = [];

		if (rows.Count == 0) {
			lines.Add($"{who}no whole sets known yet - the set finder reads the market a little at a time; try again in an hour.");
		} else {
			bool usd = rows.Any(static r => r.Wallet == null);
			string money = (name == null) || (currency <= 0) ? "prices in US dollars"
				: usd ? $"prices in US dollars - the {MarketQuotes.Code(currency)} rate isn't learned yet"
				: $"prices in {MarketQuotes.Code(currency)} at the rate learned from the market";
			lines.Add($"{who}the {rows.Count} cheapest whole sets on the market{(name == null ? "" : " for badges it can still level")} ({money}; each card is priced again before anything is bought):");

			for (int i = 0; i < rows.Count; i++) {
				SetFinder.Row r = rows[i];
				string price = BuyPlan.Money(r.Shown, r.Wallet == null ? LevelPlanner.UsDollar : currency);
				string level = name == null ? "" : $", badge level {r.Level}";
				lines.Add($"{who}{(i + 1).ToString(CultureInfo.InvariantCulture),2}. {price}  {r.Game} - {r.Cards} cards{level}, about {r.XpPerUnit:N0} XP per 1.00");
			}
		}

		lines.Add(who + FinderSaid(covered));

		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>
	/// How far the finder has got: of the cheapest sets the lists name, how many were checked on the market lately - or, with
	/// no list yet, how many of the cheapest single cards it has looked at - and when it last looked.
	/// </summary>
	public static string FinderSaid(SetFinder.Coverage c) {
		string last = c.LastLook is { } at ? $"last look {Fmt.Clock(at)}" : "it hasn't looked yet";

		if (c.Candidates > 0) {
			return $"set finder: {c.Checked:N0} of the {c.Candidates:N0} cheapest sets checked on the market in the last 24 hours · {c.Known:N0} whole sets known · {last}";
		}

		string share = c.MarketTotal > 0 ? $" of {c.MarketTotal:N0} ({Math.Min(100, c.Walked * 100.0 / c.MarketTotal).ToString("0.#", CultureInfo.InvariantCulture)}%)" : "";

		return $"set finder: looked at the {c.Walked:N0} cheapest cards{share} today · {c.Known:N0} whole sets known · {last}";
	}
}
