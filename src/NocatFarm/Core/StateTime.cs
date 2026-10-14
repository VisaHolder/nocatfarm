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
	public static Said When(string group, DateTime? next, DateTime nowUtc) {
		if (next is not { } at) {
			return default;
		}

		switch (group) {
			case "break": {
				int left = (int) Math.Ceiling((at - nowUtc).TotalMinutes);

				return left > 0 ? new Said("{0} to go", Fmt.Hm(left)) : new Said("any moment");
			}

			// Its wake time come and gone while it's still asleep: it gets up at its next look, like a break that's over.
			case "asleep" or "nightidle":
				return at <= nowUtc ? new Said("any moment") : new Said("up ~{0}", at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));

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

		Said when = When(group, human.NextChange, nowUtc);

		if (when.IsEmpty) {
			return null;
		}

		return (group == "break" ? human.Doing : new Said("asleep"), when);
	}

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
