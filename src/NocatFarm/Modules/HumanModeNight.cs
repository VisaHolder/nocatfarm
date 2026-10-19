using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// The night's list made visible: what banks hours, item by item, and every listed game left out with the reason - a
/// family game somebody is playing, a game held for its refund, one over Steam's limit. A game used to drop off the
/// night without a word, and the only sign was a "3 game(s)" where there had been 4.
/// </summary>
public sealed partial class HumanMode {
	/// <summary>Why a game the night would bank is left out of it, or <see cref="No"/> when it isn't.</summary>
	internal enum NightOut {
		No,
		Banned,
		Blacklisted,
		Refund,
		FamilyOff,
		Family
	}

	/// <summary>The rules every overnight game goes by (<see cref="MayBankAtNight"/>), saying which one left it out.</summary>
	private NightOut NightRule(uint app, HashSet<uint> banned, bool play) {
		if (banned.Contains(app)) {
			return NightOut.Banned;
		}

		if (Bot.Cfg.BlacklistedGames.Contains(app) || Live.Global.GlobalBlacklistedGames.Contains(app)) {
			return NightOut.Blacklisted;
		}

		if (Bot.Refunds.Holds(app)) {
			return NightOut.Refund;
		}

		// A game typed into the list that the library doesn't show (a free game never played, software) still goes on -
		// only a family game is known to be one.
		if (Bot.Library.Find(app) is not { Shared: true }) {
			return NightOut.No;
		}

		if (!Bot.Cfg.IncludeFamilyLibrary) {
			return NightOut.FamilyOff;
		}

		return (play ? Bot.Library.FamilyMayPlay(app) : !Bot.Library.FamilyIsPlaying(app)) ? NightOut.No : NightOut.Family;
	}

	/// <summary>
	/// What the night asks for before the rules: the overnight list (or the most-played picked in its place), plus any
	/// dated hour target that day play alone won't finish in time.
	/// </summary>
	private List<uint> NightWanted() {
		List<uint> games = Bot.Cfg.OfflineIdleGames.Count > 0 ? [.. Bot.Cfg.OfflineIdleGames] : TopForNight();

		foreach (HourTargets.Progress p in OpenTargets()) {
			if ((p.Target.By != null) && (p.DailyMinutesNeeded > Math.Max(30, _targetMinutes * 0.6)) && !games.Contains(p.Target.AppId)) {
				games.Add(p.Target.AppId);
			}
		}

		return games;
	}

	/// <summary>
	/// Every game the night asks for that it leaves out, with why - and the ones it plays that the account doesn't own
	/// (they go on, but bank nothing). Empty when banking overnight is off.
	/// </summary>
	public List<(uint App, Said Why)> NightLeftOut() {
		List<(uint App, Said Why)> why = [];

		if (!Bot.Cfg.LegitMode || !Bot.Cfg.OfflineIdleAtNight) {
			return why;
		}

		HashSet<uint> banned = BannedNow();
		int kept = 0;

		foreach (uint app in NightWanted().Distinct()) {
			string name = Bot.Library.ItemName(app);
			NightOut rule = NightRule(app, banned, false);

			Said reason = rule switch {
				NightOut.Banned => new Said("{0} left out - Steam has this account banned in it", name),
				NightOut.Blacklisted => new Said("{0} left out - it's on \"Never play these games\"", name),
				NightOut.Refund => new Said("{0} left out - held back so it can still be refunded", name),
				NightOut.FamilyOff => new Said("{0} left out - it's a family game and \"Include family-shared games\" is off", name),
				NightOut.Family => Bot.Library.FamilyReason(app) is { IsEmpty: false } family ? family : new Said("{0} left out - someone in your Steam Family is using it", name),
				_ => default
			};

			if (rule == NightOut.No) {
				if (++kept > Core.SteamIds.GamesAtOnce) {
					reason = new Said("{0} left out - Steam plays {1} at once", name, Core.SteamIds.GamesAtOnce);
				} else if (Bot.Library.Holds(app) == Library.Holding.No) {
					reason = new Said("{0}: this account doesn't own it, so idling it banks nothing - add it to its Steam library first", name);
				}
			}

			if (!reason.IsEmpty) {
				why.Add((app, reason));
			}
		}

		return why;
	}

	/// <summary>"Counter-Strike 2, ShareX (software)" - each item the night banks on, by name.</summary>
	public string NightItems() {
		List<uint> night = NightGames();

		return string.Join(", ", night.Select(a => Bot.Library.ItemName(a)));   // only said while it banks something
	}

	/// <summary>" · ARC Raiders left out - ..." for the status line, or nothing.</summary>
	private string NightLeftOutLine() {
		List<(uint App, Said Why)> outs = NightLeftOut();

		return outs.Count == 0 ? "" : " · " + string.Join(" · ", outs.Select(static o => o.Why.ToString()));
	}

	// What the night's log last said about each left-out game - said again only when it changes - and which night.
	private readonly Dictionary<uint, string> _nightSaid = [];
	private DateTime _nightSaidFor;

	/// <summary>Log each listed game left out of the night (and why) when that starts or changes, and each one that comes back.</summary>
	private void SayNightChanges() {
		DateTime night = NextWakeTime();

		if (night != _nightSaidFor) {
			_nightSaid.Clear();   // a new night: everything is said again
			_nightSaidFor = night;
		}

		List<(uint App, Said Why)> outs = NightLeftOut();

		foreach ((uint app, Said why) in outs) {
			string said = why.ToEnglish();

			if (!_nightSaid.TryGetValue(app, out string? before) || (before != said)) {
				_nightSaid[app] = said;
				Log.Info(why, Bot.Name);
			}
		}

		foreach (uint app in _nightSaid.Keys.Where(a => !outs.Any(o => o.App == a)).ToList()) {
			_nightSaid.Remove(app);
			Log.Info(new Said("{0} is back on the overnight list", Bot.Library.ItemName(app)), Bot.Name);
		}
	}

	/// <summary>
	/// The day's games (<see cref="Bot.Cfg"/>.GameWeights) left out because someone in the Steam Family is on them, with
	/// why - for 'human' and 'config'. English, as console replies are.
	/// </summary>
	public List<string> DayFamilyReport() {
		if (!Bot.Cfg.LegitMode) {
			return [];
		}

		return [.. ParseWeights(Bot.Cfg.GameWeights).Select(static w => w.Game).Distinct()
			.Where(Bot.Library.FamilyIsPlaying)
			.Select(a => "today       " + Bot.Library.FamilyReason(a).ToEnglish())
			.Where(static l => l.Trim() != "today")];
	}

	/// <summary>For 'human' and 'config': what the night banks on, item by item, and what it leaves out and why. English, as console replies are.</summary>
	public List<string> NightReport() {
		if (!Bot.Cfg.LegitMode || !Bot.Cfg.OfflineIdleAtNight) {
			return [];
		}

		List<string> lines = [$"overnight   {string.Join(", ", NightGames().Select(a => Bot.Library.ItemName(a, english: true)).DefaultIfEmpty("nothing"))}"];
		lines.AddRange(NightLeftOut().Select(static o => "            " + o.Why.ToEnglish()));

		return lines;
	}
}
