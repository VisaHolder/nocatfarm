using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// Where this account's items go (1.8.2): "Send items to" (<see cref="BotConfig.TradeMasters"/>) takes account names as well
/// as SteamID64s, and the gem sacks and unopened packs go to its first account when their own "send to" is left empty.
/// Sending only - who may take items from an account is "Your other accounts", never this.
/// </summary>
public static partial class Looting {
	/// <summary>The lowest SteamID64 there is - anything smaller in the list is a name, not an ID.</summary>
	private const ulong FirstSteamId = 76561197960265728UL;

	private static IEnumerable<string> SendToEntries(BotConfig cfg) =>
		(cfg.TradeMasters ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// The account items are sent to, as a SteamID64: the first entry of "Send items to" that is a SteamID64, or the name of
	/// an account here that has signed in. 0 when there is none.
	/// </summary>
	public static ulong SendToId(BotConfig cfg, BotManager? mgr = null) {
		mgr ??= BotManager.Instance;

		foreach (string entry in SendToEntries(cfg)) {
			if (ulong.TryParse(entry, out ulong id) && (id > FirstSteamId)) {
				return id;
			}

			if ((mgr?.Get(entry) is { SteamId: > 0 } bot)) {
				return bot.SteamId;
			}
		}

		return 0;
	}

	/// <summary>
	/// An account here by name to send to: <paramref name="own"/> (a feature's own "send to") when it's set, or else the first
	/// account of "Send items to" that is one of yours here - never <paramref name="self"/>. "" when there is none.
	/// </summary>
	public static string SendToName(BotConfig cfg, string? own, string self = "", BotManager? mgr = null) {
		if (!string.IsNullOrWhiteSpace(own)) {
			return own.Trim();
		}

		mgr ??= BotManager.Instance;

		foreach (string entry in SendToEntries(cfg)) {
			Bot? bot = ulong.TryParse(entry, out ulong id) && (id > FirstSteamId)
				? mgr?.All.FirstOrDefault(b => b.SteamId == id)
				: mgr?.Get(entry);

			if ((bot != null) && !bot.Name.Equals(self, StringComparison.OrdinalIgnoreCase)) {
				return bot.Name;
			}
		}

		return "";
	}
}
