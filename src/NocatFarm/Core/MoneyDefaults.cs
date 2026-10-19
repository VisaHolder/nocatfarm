using System.Globalization;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2: money settings still at their defaults on a wallet in a money far smaller than the dollar. The defaults are
/// dollar or euro amounts - 1.50 a day, a swap losing at most 0.03 - and every money setting is in the account's own wallet
/// currency, so on a yen wallet 1.50 a day buys nothing and 0.03 is no loss at all. Nothing is changed by itself (a spend
/// limit going up on its own is money spent nobody chose): the first time Steam says the wallet's money, each money setting
/// still at its default is said once, with about what it should be for that money.
/// </summary>
public static class MoneyDefaults {
	/// <summary>One money setting still at its dollar default, and about what it should be in the wallet's money.</summary>
	public sealed record Hint(string Setting, float Now, float Suggested);

	/// <summary>The money settings whose defaults are dollar amounts.</summary>
	public static readonly IReadOnlyList<string> Names = ["BuyDailyLimit", "BuyWeeklyLimit", "CardSwapMaxLoss", "SpareMaxPrice", "SpareMinSale"];

	/// <summary>Wallet money to the dollar from which the dollar defaults stop making sense (yen 150, yuan 7 ... - not pounds, euros or zloty).</summary>
	public const double FarFromDollar = 5;

	/// <summary>
	/// Each money setting still at its default, with about what it'd be in a money worth <paramref name="rate"/> of it to the
	/// dollar - none for a money near the dollar, or a rate not known.
	/// </summary>
	public static List<Hint> Check(BotConfig cfg, double? rate) {
		if ((rate is not double r) || !double.IsFinite(r) || (r < FarFromDollar)) {
			return [];
		}

		BotConfig defaults = new();
		List<Hint> hints = [];

		foreach (string name in Names) {
			if ((Settings.Read(cfg, name) is float now) && (Settings.Read(defaults, name) is float def) && (def > 0) && (Math.Abs(now - def) < 0.0001f)) {
				hints.Add(new Hint(name, now, Nice(def * r)));
			}
		}

		return hints;
	}

	/// <summary>A suggestion people can type: whole numbers from 10 up (in fives from 100), one decimal under.</summary>
	public static float Nice(double value) =>
		value >= 100 ? (float) (Math.Round(value / 5, MidpointRounding.AwayFromZero) * 5)
			: value >= 10 ? (float) Math.Round(value, MidpointRounding.AwayFromZero)
			: (float) Math.Round(value, 1, MidpointRounding.AwayFromZero);

	/// <summary>The line said for one setting - in the wallet's money's code ("JPY").</summary>
	public static Said Line(Hint h, string code) {
		string now = h.Now.ToString("0.00", CultureInfo.InvariantCulture), about = h.Suggested.ToString("0.##", CultureInfo.InvariantCulture);

		return h.Setting switch {
			"BuyDailyLimit" => new Said("wallet in {0}: \"Spend at most a day\" is still {1}, a dollar amount - for {0} use about {2}", code, now, about),
			"BuyWeeklyLimit" => new Said("wallet in {0}: \"Spend at most a week\" is still {1}, a dollar amount - for {0} use about {2}", code, now, about),
			"CardSwapMaxLoss" => new Said("wallet in {0}: \"Most a swap may lose\" is still {1}, a dollar amount - for {0} use about {2}", code, now, about),
			"SpareMaxPrice" => new Said("wallet in {0}: \"Never touch items worth more than\" is still {1}, a dollar amount - for {0} use about {2}", code, now, about),
			_ => new Said("wallet in {0}: \"Only sell if you get at least\" is still {1}, a dollar amount - for {0} use about {2}", code, now, about)
		};
	}

	private static readonly Lock Gate = new();

	private static string PathOf(string account) => Path.Combine(ConfigStore.ConfigDir, "state", $"money-hints-{account}.txt");

	/// <summary>
	/// Steam said the wallet's money: the money settings still at their dollar defaults said once for it (kept in
	/// config/state/money-hints-&lt;account&gt;.txt, so a restart doesn't say it again). How many were said.
	/// </summary>
	public static int Warn(string account, BotConfig cfg, int currency) {
		if ((currency <= 0) || (account.Length == 0)) {
			return 0;
		}

		string mark = currency.ToString(CultureInfo.InvariantCulture);

		lock (Gate) {
			try {
				if (File.Exists(PathOf(account)) && (File.ReadAllText(PathOf(account)).Trim() == mark)) {
					return 0;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return 0;   // not known whether it was said: never said every sign-in
			}

			List<Hint> hints = Check(cfg, WalletRate.ForShowing(currency));
			string code = MarketQuotes.Code(currency) is { Length: > 0 } c ? c : mark;

			foreach (Hint h in hints) {
				Log.Attention(Line(h, code), account);
			}

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(PathOf(account))!);
				AtomicFile.Write(PathOf(account), mark);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug(new Said("couldn't note the money settings were looked at: {0}", Log.Describe(e)), account);
			}

			return hints.Count;
		}
	}
}
