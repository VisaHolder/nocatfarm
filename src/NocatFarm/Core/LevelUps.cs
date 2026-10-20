using System.Globalization;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// 1.8.2: one line when an account's Steam level goes up - "Steam level 58 (+1) · this level cost 0.46 CAD" - from whatever
/// read the level (the badges, or the level asked right after crafting). The cost is only money really spent: the cards
/// bought for the badges crafted since the last line (buying cards' list); farmed cards cost nothing, and cards kept or sold
/// instead aren't counted. "Level-up line shows" picks level and cost, the level only, or no line. The last level said is
/// kept in config/state/levelup-&lt;account&gt;.txt, so a restart never says it again; the first level ever seen is only noted.
/// </summary>
public static class LevelUps {
	/// <summary>"Level-up line shows": the level and what it cost, the level only, or nothing.</summary>
	public const int Off = 0, LevelOnly = 1, LevelAndCost = 3;

	private static readonly Lock Gate = new();

	private static string PathOf(string account) => Path.Combine(Config.ConfigStore.ConfigDir, "state", $"levelup-{account}.txt");

	/// <summary>The last level said (or first seen) and when - null when none was ever noted.</summary>
	public static (int Level, DateTime At)? Last(string account) {
		try {
			string[] parts = File.Exists(PathOf(account)) ? File.ReadAllText(PathOf(account)).Trim().Split(' ') : [];

			return (parts.Length == 2) && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int level)
				&& long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? (level, new DateTime(ticks, DateTimeKind.Utc)) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private static bool Note(string account, int level, DateTime at) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(PathOf(account))!);
			AtomicFile.Write(PathOf(account), $"{level.ToString(CultureInfo.InvariantCulture)} {at.Ticks.ToString(CultureInfo.InvariantCulture)}");

			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.Debug(new Said("couldn't note the Steam level: {0}", Log.Describe(e)), account);

			return false;
		}
	}

	/// <summary>
	/// What the badges crafted since then cost in money really spent: every card bought for a game whose badge was crafted
	/// after <paramref name="since"/> (bought before that craft, in the month before it), in one money - with the money, or
	/// (0, 0) when nothing was bought for them.
	/// </summary>
	public static (long Cents, int Currency) CostSince(BuyLedger ledger, DateTime since) {
		List<BuyLedger.Craft> crafts = [.. ledger.Crafts.Where(c => c.At > since)];
		List<BuyLedger.Entry> paid = [.. ledger.All.Where(e => !e.Pending && crafts.Any(c => (c.App == e.App) && (e.At <= c.At) && (e.At > c.At.AddDays(-30))))];

		return paid.Count == 0 ? (0, 0) : (paid.Sum(static e => e.Cents), paid[0].Currency);
	}

	/// <summary>The line: "Steam level 58 (+1) · this level cost 0.46 CAD", "Steam level 60 (+3) · 1.42 CAD for 3 levels", or the level alone.</summary>
	public static Said Line(int level, int gained, long cents, int currency, int shows) =>
		shows != LevelAndCost ? new Said("Steam level {0} (+{1})", level, gained)
			: gained == 1 ? new Said("Steam level {0} (+{1}) · this level cost {2}", level, gained, Spare.Money(cents, currency))
			: new Said("Steam level {0} (+{1}) · {2} for {1} levels", level, gained, Spare.Money(cents, currency));

	/// <summary>
	/// The account's Steam level as just read: higher than the last one said - one line (unless "Level-up line shows" is
	/// off), told on Telegram/Discord with the card and badge news. The first level ever seen is only noted. True when a line was said.
	/// </summary>
	public static bool Seen(string account, int shows, int level, DateTime now, int walletCurrency = 0) {
		if ((level <= 0) || (account.Length == 0)) {
			return false;
		}

		lock (Gate) {
			if (Last(account) is not { } last) {
				Note(account, level, now);

				return false;
			}

			if (level <= last.Level) {
				return false;
			}

			// Noted first: a line is never said twice, even if the next read comes a second later or after a restart.
			if (!Note(account, level, now)) {
				return false;
			}

			if (shows == Off) {
				return false;
			}

			(long cents, int currency) = CostSince(BuyLedger.Of(account), last.At);
			Log.Reward(Line(level, level - last.Level, cents, currency > 0 ? currency : walletCurrency, shows), account, Topic.Cards);

			return true;
		}
	}

	/// <summary>For an account: by its own setting.</summary>
	public static bool Seen(Bot bot, int level) =>
		Seen(bot.Name, bot.Cfg.LevelUpLine, level, DateTime.UtcNow, bot.WalletCurrency == SteamKit2.ECurrencyCode.Invalid ? 0 : (int) bot.WalletCurrency);
}
