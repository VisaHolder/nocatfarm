using NocatFarm.Core;
using NocatFarm.Modules;

namespace NocatFarm;

public static partial class Commands {
	/// <summary>'events [account]': what Steam event is on and what each account has done in it (see Modules/SaleEvents.cs).</summary>
	private static string Events(BotManager mgr, string[] args) {
		if (Pick(mgr, args, out string? problem) is not { } bots) {
			return problem!;
		}

		List<string> lines = [];

		foreach (Bot b in bots) {
			if (!b.IsOnline || (BotManager.ModuleOf<SaleEvents>(b) is not { } events)) {
				lines.Add(new Said("{0}: not signed in - it looks for events once it is", b.Name));

				continue;
			}

			lines.AddRange(events.Report());
		}

		return string.Join(Environment.NewLine, lines);
	}
}
