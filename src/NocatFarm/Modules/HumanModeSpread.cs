using NocatFarm.Core;

namespace NocatFarm.Modules;

public sealed partial class HumanMode {
	/// <summary>
	/// The account's day in force, local time: when it got up and when its plan ends (bedtime, or the end of a sitting
	/// past it). Null without human mode, before it has worked out its day, or once that day is over - for spreading
	/// spare items through it (<see cref="SpareSpread"/>).
	/// </summary>
	public static (DateTime Wake, DateTime Ends)? DayWindow(Bot bot) {
		if (!bot.Cfg.LegitMode || (bot.Modules.OfType<HumanMode>().FirstOrDefault() is not { _ticked: true } human) || (human._dayStamp < 0)) {
			return null;
		}

		DateTime ends = human.PlanEnds(), wake = human.PlanWake();

		return (DateTime.Now < ends) && (wake < ends) ? (wake, ends) : null;
	}
}
