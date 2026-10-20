using System.Globalization;
using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// How long a human-mode break or night has left, for the narrow places that show an account's state in a few words:
/// mini mode's rows and the dashboard's state chips. "meal break · 5m to go", "asleep · up ~09:30" - the full window
/// already says it; these said only "meal break" and "asleep".
/// </summary>
public static class StateTime {
	/// <summary>
	/// The few words after the state, or default (nothing) when there's nothing to add: "5m to go" during a break (any
	/// moment now: "any moment"), "up ~09:30" asleep or idling at night - the wake-up in this PC's local time.
	/// </summary>
	/// <param name="group">The account's <see cref="BotStatus.Group"/>.</param>
	/// <param name="next">When it next changes (<see cref="HumanMode.NextChange"/>), in UTC.</param>
	/// <param name="window">When the next day's wake isn't planned yet (asleep after bedtime): its window, local times -
	/// "up 13:15-14:45", rather than the day before's wake time standing in.</param>
	/// <param name="intoBed">On a break that bedtime comes in the middle of: "then off for the night", not a time to go.</param>
	public static Said When(string group, DateTime? next, DateTime nowUtc, (DateTime From, DateTime To)? window = null, bool intoBed = false) {
		if (next is not { } at) {
			return default;
		}

		switch (group) {
			case "break" when intoBed:
				return new Said("then off for the night");

			case "break": {
				int left = (int) Math.Ceiling((at - nowUtc).TotalMinutes);

				return left > 0 ? new Said("{0} to go", Fmt.Hm(left)) : new Said("any moment");
			}

			// Its wake time come and gone while it's still asleep: it gets up at its next look, like a break that's over.
			case "asleep" or "nightidle":
				return at <= nowUtc ? new Said("any moment")
					: window is { } w ? new Said("up {0}", HumanMode.WindowWords(w.From, w.To))
					: new Said("up ~{0}", at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));

			default:
				return default;
		}
	}

	/// <summary>
	/// A row of mini mode for a human-mode account on a break or asleep: the state, short ("asleep", not "asleep, idling 3
	/// game(s)" - the row is narrow), and <see cref="When"/>. Null for anything else: the row says what it always said.
	/// </summary>
	public static (Said State, Said When)? Mini(Bot bot, DateTime nowUtc) {
		// Paused, stood down for you, finishing up or grinding: the row says that, as before.
		if (!bot.IsOnline || bot.Stopping || bot.Paused || bot.PlayingBlocked || bot.InResumeGrace || bot.Grinding) {
			return null;
		}

		string group = BotStatus.Group(bot);

		if ((group is not ("break" or "asleep" or "nightidle")) || (BotManager.ModuleOf<HumanMode>(bot) is not { Current: not HumanMode.Phase.Off } human)) {
			return null;
		}

		Said when = When(group, human.NextChange, nowUtc, human.UpWindowNow, human.BreakIntoBed);

		if (when.IsEmpty) {
			return null;
		}

		return (group == "break" ? human.Doing : new Said("asleep"), when);
	}

	/// <summary>
	/// Asleep or idling at night with the next day not planned yet: the window it gets up in (local times), for the
	/// dashboard's chip to say "up 13:15-14:45". Null otherwise - the chip says "up ~09:30" from <see cref="Until"/>.
	/// </summary>
	public static (DateTime From, DateTime To)? Window(Bot bot) =>
		(Until(bot) != null) && (BotStatus.Group(bot) is "asleep" or "nightidle") && (BotManager.ModuleOf<HumanMode>(bot) is { } human) ? human.UpWindowNow : null;

	/// <summary>On a break that bedtime comes in the middle of, for the dashboard's chip to say "then off for the night".</summary>
	public static bool IntoBed(Bot bot) =>
		(Until(bot) != null) && (BotStatus.Group(bot) == "break") && (BotManager.ModuleOf<HumanMode>(bot) is { BreakIntoBed: true });

	/// <summary>When the dashboard's chip should count to: the next change of a human-mode break or night, else null.</summary>
	public static DateTime? Until(Bot bot) {
		if (!bot.IsOnline || bot.Stopping || bot.Paused || bot.PlayingBlocked) {
			return null;
		}

		string group = BotStatus.Group(bot);

		return (group is "break" or "asleep" or "nightidle") && (BotManager.ModuleOf<HumanMode>(bot) is { Current: not HumanMode.Phase.Off } human)
			? human.NextChange
			: null;
	}
}
