using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Achievement Boost - an auto-rotating hunter that earns achievements across several games without you starting
/// each grind by hand. OFF by default (<c>AchievementBoost = 0</c>); most accounts won't use it.
///
/// It drives the ordinary grind, so a boost session behaves exactly like a deliberate one: it sits on a game and
/// unlocks easiest-first, at the account's Achievement pace, only what the hours in the game make reachable - and
/// it's persisted, so a restart resumes mid-session. When a session ends it rotates to the next game in the list.
///
/// A HUMAN account stays weighted-FIRST: a boost session is only an occasional grind slotted between long
/// stretches of the normal weighted schedule (<c>BoostRestMinutesHuman</c> apart, capped at
/// <c>MaxBoostGamesInARow</c> before a longer weighted rest), and never while the account is asleep. A NON-human
/// account has no weighted schedule, so it rotates targets back-to-back. It never fights a manual grind: while one
/// the operator started is running, the boost stays completely out of the way.
/// </summary>
public sealed class AchievementBoost(Bot bot) : BotModule(bot) {
	private int _index;                          // round-robin position in the target list
	private int _inARow;                         // consecutive boost sessions (human weighted-first cap)
	private bool _ours;                           // is the grind currently running one WE started?
	private DateTime _lastEnded = DateTime.MinValue;
	private string _status = "off";

	public override string Name => "boost";
	public override string Status => On ? _status : "";

	private bool On => (Bot.Cfg.AchievementBoost != 0) && Bot.Cfg.UnlockAchievements;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				if (On) {
					Tick();
				} else {
					_status = "off";
				}
			} catch (Exception e) {
				Log.Warn($"achievement boost hiccup: {e.Message}", Bot.Name);
			}

			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	private void Tick() {
		// A grind is running. If it's ours, let it run; if it's a manual grind, stay completely out of the way.
		if (Bot.Grinding) {
			_status = _ours ? $"hunting {GameNames.Of(Bot.GrindGame)}" : "waiting - a manual grind is running";

			return;
		}

		// Our session just finished (we were grinding, now we're not): count it and take a beat before the next.
		if (_ours) {
			_ours = false;
			_inARow++;
			_lastEnded = DateTime.UtcNow;
			_status = "between games";

			return;
		}

		if (!Bot.IsOnline || !Bot.CanPlay) {
			return;
		}

		List<uint> targets = Targets();

		if (targets.Count == 0) {
			_status = "on - no games to hunt (pick some under \"Boost these games\")";

			return;
		}

		// Human accounts hunt only while awake, and stay weighted-first: a longer stretch of the normal schedule
		// sits between boost sessions, and a longer one still after a run of them.
		if (Bot.HumanOwned) {
			if (!HumanMode.AwakeFor(Bot)) {
				_status = "resting until the account is awake";

				return;
			}

			int rest = Math.Max(15, Bot.Cfg.BoostRestMinutesHuman);
			bool capped = _inARow >= Math.Max(1, Bot.Cfg.MaxBoostGamesInARow);
			int need = capped ? rest * 3 : rest;

			if ((_lastEnded != DateTime.MinValue) && (DateTime.UtcNow - _lastEnded < TimeSpan.FromMinutes(need))) {
				_status = $"weighted schedule - next hunt in {Fmt.Hm((int) (TimeSpan.FromMinutes(need) - (DateTime.UtcNow - _lastEnded)).TotalMinutes)}";

				return;
			}

			if (capped) {
				_inARow = 0;   // the longer weighted rest has been served; start a fresh run of boost sessions
			}
		}

		uint target = targets[_index % targets.Count];
		_index++;

		int hours = Math.Clamp(Bot.Cfg.BoostSessionHours, 1, 24);
		Bot.StartGrind(target, TimeSpan.FromHours(hours));
		_ours = true;
		_status = $"hunting {GameNames.Of(target)}";
		Log.Info($"achievement boost - hunting {GameNames.Of(target)} for ~{hours}h", Bot.Name);
	}

	/// <summary>
	/// The games to work through, in order. Only "games you pick" for now; the all-singleplayer auto-detect mode
	/// (owned-games discovery + Steam store categories) is the next stage.
	/// </summary>
	private List<uint> Targets() => Bot.Cfg.AchievementBoost == 1 ? Bot.Cfg.AchievementBoostGames : [];
}
