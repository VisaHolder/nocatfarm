using NocatFarm.Core;

namespace NocatFarm;

/// <summary>1.8.2: card swaps with other nocat.farm users ('swaps').</summary>
public static partial class Commands {
	/// <summary>
	/// 'swaps [account]' says what each account's card swaps are doing; 'swaps preview|on|off &lt;account&gt;' shows, turns on
	/// (after its preview, as the switch in its settings) or off; 'swaps sync' asks nocat.lol for new matches now.
	/// </summary>
	private static async Task<string> SwapsAsync(BotManager mgr, string[] args) {
		string verb = "status";
		string? account = null;

		foreach (string a in args) {
			string w = a.ToLowerInvariant();

			if ((w is "status" or "preview" or "on" or "off" or "sync") && (mgr.Get(a) == null)) {
				verb = w;
			} else {
				account ??= a;
			}
		}

		if (verb == "sync") {
			CardExchange.Nudge();

			return "Asking nocat.lol for card swap matches in a minute or two.";
		}

		if (verb == "status") {
			if (Pick(mgr, account == null ? [] : [account], out string? problem) is not { } bots) {
				return problem!;
			}

			return string.Join(Environment.NewLine, bots.Select(b => SwapsStatus(b, DateTime.UtcNow)));
		}

		if ((account == null) || account.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			return $"swaps {verb} <account> - one account at a time";
		}

		if (mgr.Get(account) is not { } bot) {
			return NoSuchAccount(mgr, account);
		}

		switch (verb) {
			case "off":
				// Safe from anywhere, Steam chat too: it only stops things.
				Switches.Set(bot, Switches.Swaps, false);

				return $"{bot.Name}: card swaps are off - nothing is swapped, and nocat.lol deletes its SteamID and trade link in a minute or two.";
			case "on":
				Switches.Set(bot, Switches.Swaps, true);

				return $"{bot.Name}: card swaps are on - nocat.lol gets its list in a minute or two and pairs it within the hour. 'swaps off {bot.Name}' turns it off.";
			default:
				return await SwitchPreviewAsync(bot, Switches.Swaps, CancellationToken.None).ConfigureAwait(false)
					+ (CardExchange.On(bot.Cfg, bot.Name) ? "" : $"{Environment.NewLine}To turn it on, type: swaps on {bot.Name}");
		}
	}

	/// <summary>One account's card swaps in a line: on or off, today's count, matches waiting, offers out.</summary>
	public static string SwapsStatus(Bot bot, DateTime nowUtc) {
		if (!CardExchange.On(bot.Cfg, bot.Name)) {
			return $"{bot.Name}: card swaps off ('swaps preview {bot.Name}' shows what it would do)";
		}

		CardExchange.Ledger ledger = CardExchange.Ledger.Load(bot.Name);
		HashSet<string> open = [.. CardExchange.OpenIds(bot.Name, bot.SteamId, nowUtc)];
		List<CardExchange.Match> matches = [.. CardExchange.MatchesOf(bot.SteamId).Where(m => open.Contains(m.Id))];
		int toSend = matches.Count(m => m.Send && (ledger.Of(m.Id) == null)), toGet = matches.Count(static m => !m.Send), out_ = ledger.InState("sent").Count;

		return $"{bot.Name}: card swaps on · {ledger.DoneToday(nowUtc)} of {CardExchange.PerDay(bot.Cfg)} today · {toSend} to send, {toGet} coming · {out_} offer(s) out";
	}
}
