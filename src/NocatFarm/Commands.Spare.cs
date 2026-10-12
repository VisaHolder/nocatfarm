using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

/// <summary>Spare items ('spare', also 'gems') and opening booster packs ('booster open') - 1.8.0.</summary>
public static partial class Commands {
	/// <summary>The verb and the account of a 'spare' line, in either order ('spare ok new' or, from Steam chat, 'spare new ok').</summary>
	public static (string Verb, string? Account) GemsWords(BotManager mgr, string[] args) {
		string verb = "preview";
		string? account = null;

		foreach (string a in args) {
			string w = a.ToLowerInvariant();

			// 'ok' and 'hold' are the old words for on and off - still understood, no longer shown.
			if ((w is "preview" or "do" or "on" or "off" or "ok" or "hold" or "now") && (mgr.Get(a) == null)) {
				verb = w switch {
					"now" => "do",
					"ok" => "on",
					"hold" => "off",
					_ => w
				};
			} else {
				account ??= a;
			}
		}

		return (verb, account);
	}

	/// <summary>
	/// Turning it on or doing it now is the owner's word, as 'dlc leave' is: not for a Steam-chat master. Looking (a preview)
	/// and turning it off are fine. Nor opening packs now.
	/// </summary>
	private static bool SpareRefusesFromChat(string[] words) {
		if (words.Length < 2) {
			return false;
		}

		string? name = Resolve(words[0])?.Name;
		HashSet<string> rest = [.. words[1..].Select(static w => w.ToLowerInvariant())];

		return ((name == "spare") && (rest.Contains("on") || rest.Contains("ok") || rest.Contains("do") || rest.Contains("now")))
			|| ((name == "booster") && rest.Contains("open") && (rest.Contains("do") || rest.Contains("now")));
	}

	/// <summary>The words after 'open' in 'booster open ...' or 'booster &lt;account&gt; open ...' - null when it isn't that.</summary>
	private static string[]? OpenWords(BotManager mgr, string[] args) {
		if ((args.Length > 0) && args[0].Equals("open", StringComparison.OrdinalIgnoreCase) && (mgr.Get(args[0]) == null)) {
			return args[1..];
		}

		if ((args.Length > 1) && (mgr.Get(args[0]) != null) && args[1].Equals("open", StringComparison.OrdinalIgnoreCase)) {
			return [args[0], .. args[2..]];
		}

		return null;
	}

	private static async Task<string> GemsAsync(BotManager mgr, string[] args) {
		(string verb, string? account) = GemsWords(mgr, args);

		if (verb == "preview") {
			if (Pick(mgr, account == null ? [] : [account], out string? problem) is not { } bots) {
				return problem!;
			}

			// Pricing a few games goes through the market's queue - a few minutes on a first look. The reply says it's
			// going; the log has the answer.
			return await SlowAsync(new Slow("spare", "working out what's spare", Key: "spare preview"), bots, static b => SpareRun.RunAsync(b, act: false, CancellationToken.None),
				ReplyWithin, oneAtATime: true, cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
		}

		if ((account == null) || account.Equals("all", StringComparison.OrdinalIgnoreCase)) {
			return $"spare {verb} <account> - one account at a time";
		}

		if (mgr.Get(account) is not { } bot) {
			return NoSuchAccount(mgr, account);
		}

		if (verb == "off") {
			// Safe from anywhere, Steam chat too: it only stops things.
			Switches.Set(bot, Switches.Spare, false);

			return $"{bot.Name}: Spare items is off - nothing is turned into gems, sold or sent.";
		}

		if (verb == "on") {
			Switches.Set(bot, Switches.Spare, true);

			return $"{bot.Name}: Spare items is on - it turns spare items into gems or sells them from its next look (every {Math.Clamp(bot.Cfg.SpareRunHours, 1, 72)}h or so{(bot.Cfg.LegitMode ? ", in its day" : "")}), within its limits. 'spare do {bot.Name}' does the next ones now; 'spare off {bot.Name}' turns it off.";
		}

		// do
		if (!Switches.SpareOn(bot.Cfg, bot.Name)) {
			return $"{bot.Name}: Spare items is off. 'spare preview {bot.Name}' shows what it would do; 'spare on {bot.Name}' turns it on.";
		}

		// A human-mode account acts in its own day only - by hand too, as 'points spend' does.
		if (HumanWaits(bot, SpareRun.SpareGate(bot)) is { } waits) {
			return waits;
		}

		if (!bot.IsOnline || !bot.Web.Ready) {
			return $"{bot.Name}: not logged in";
		}

		return await SlowAsync(new Slow("spare", "dealing with spare items", Key: "spare do"), [bot], static b => Modules.ScamWatch.AsYouAskedAsync(() => SpareRun.RunAsync(b, act: true, CancellationToken.None)), ReplyWithin).ConfigureAwait(false);
	}

	/// <summary>
	/// 'set': Spare items and Steam Points are only turned on after their preview. 'set &lt;account&gt; SpareItems on' answers
	/// with the preview and how to confirm ('spare on &lt;account&gt;'); 'off' turns it off at once. Everything else is 'set' as ever.
	/// </summary>
	private static async Task<string> SetAsync(BotManager mgr, string[] args, string line) {
		if ((args.Length >= 2) && (mgr.Get(args[0]) is { } bot) && (Switches.FeatureOf(KeyOf(args[1]).Key) is { } feature)) {
			string value = Unquote(SetValue(line, Words(line), 2)).Trim().ToLowerInvariant();
			// Every word 'set' reads as on or off ('y', 'n' too): one it doesn't is answered by 'set' as wrong, and changes nothing.
			bool? on = Config.Settings.IsTrue(value) ? true : Config.Settings.IsFalse(value) ? false : null;
			string word = feature == Switches.Spare ? "spare" : "points";

			if (on == false) {
				Switches.Set(bot, feature, false);

				return $"{bot.Name}.{feature} = off";
			}

			if (on == true) {
				bool already = feature == Switches.Spare ? Switches.SpareOn(bot.Cfg, bot.Name) : bot.Cfg.SpendPoints && PointsLedger.Approved(bot);

				if (already) {
					return $"{bot.Name}.{feature} = on";
				}

				string preview = await SwitchPreviewAsync(bot, feature, CancellationToken.None).ConfigureAwait(false);

				return $"{preview}{Environment.NewLine}{bot.Name}.{feature} is still off. To turn it on, type: {word} on {bot.Name}";
			}
		}

		return Set(mgr, args, line);
	}

	/// <summary>
	/// What turning Spare items or Steam Points on would do, for the confirm popup and for 'set &lt;account&gt; SpareItems on':
	/// from the prices already held, with the limits. English, as console replies are.
	/// </summary>
	public static async Task<string> SwitchPreviewAsync(Bot bot, string feature, CancellationToken ct) {
		if (feature == Switches.Spare) {
			string limits = SpareRun.Limits(bot);

			if (!bot.IsOnline || !bot.Web.Ready) {
				return $"{bot.Name}: not signed in, so it can't look at the inventory yet.{Environment.NewLine}{limits}";
			}

			return await SpareRun.QuickPreviewAsync(bot, ct).ConfigureAwait(false) + Environment.NewLine + limits;
		}

		string keeps = $"It keeps at least {PointsPlan.N(bot.Cfg.PointsKeepAtLeast)} points and spends at most {PointsPlan.N(bot.Cfg.PointsWeeklyCap)} a week{(bot.Cfg.LegitMode ? ", only during its day" : "")}. Points spent are gone for good.";

		if (!bot.IsOnline || !bot.Web.Ready || (BotManager.ModuleOf<PointsSpender>(bot) is not { } spender)) {
			return $"{bot.Name}: not signed in, so it can't look at its points yet.{Environment.NewLine}{keeps}";
		}

		PointsSpender.Result result = await spender.LookAsync(PointsSpender.Mode.Preview, ct).ConfigureAwait(false);

		return string.Join(Environment.NewLine, PointsSaid(bot, PointsSpender.Mode.Preview, result).Append(keeps));
	}

	/// <summary>
	/// On a human-mode account whose own-day gate is shut: the same "not now - why" 'points spend' answers. Null when it may
	/// act (always, on a robot). Without the module (not signed in) a fresh gate is asked, which is shut.
	/// </summary>
	private static string? HumanWaits(Bot bot, Modules.HumanGate? gate) {
		if (!bot.Cfg.LegitMode) {
			return null;
		}

		gate ??= Modules.HumanGate.OwnDay(bot);

		return gate.Open ? null : $"{bot.Name}: not now - {gate.Waiting().ToEnglish()}";
	}

	private static async Task<string> BoosterOpenAsync(BotManager mgr, string[] args) {
		bool now = args.Any(static a => a.ToLowerInvariant() is "do" or "now");
		string[] who = [.. args.Where(a => !(a.ToLowerInvariant() is "do" or "now" or "preview") || (mgr.Get(a) != null))];

		if (now && ((who.Length == 0) || who[0].Equals("all", StringComparison.OrdinalIgnoreCase))) {
			return "booster open <account> do - one account at a time";
		}

		if (Pick(mgr, who, out string? problem) is not { } bots) {
			return problem!;
		}

		// Opening now on a human-mode account keeps to its day, as the module does.
		if (now && (bots.Count == 1) && (HumanWaits(bots[0], SpareRun.PacksGate(bots[0])) is { } waits)) {
			return waits;
		}

		return await SlowAsync(new Slow("booster", now ? "opening booster packs" : "looking at booster packs", Key: now ? "booster open do" : "booster open"), bots,
			b => Modules.ScamWatch.AsYouAskedAsync(() => SpareRun.PacksAsync(b, now, CancellationToken.None)), ReplyWithin, oneAtATime: true,
			cannot: static b => !b.IsOnline || !b.Web.Ready ? $"{b.Name}: not logged in" : null).ConfigureAwait(false);
	}
}
