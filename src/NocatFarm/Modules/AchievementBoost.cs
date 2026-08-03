using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Achievement Boost - an auto-rotating hunter that earns achievements across several games without you starting
/// each grind by hand. OFF by default (<c>AchievementBoost = 0</c>); most accounts won't use it.
///
/// It drives the ordinary grind, so a boost session behaves exactly like a deliberate one: it sits on a game and
/// unlocks easiest-first, at the account's Achievement pace, only what the hours in the game make reachable - and
/// it's persisted, so a restart resumes mid-session. When a session ends it rotates to the next game.
///
/// Targets come from one of two modes: "games you pick" (the <c>AchievementBoostGames</c> list) or "all
/// single-player" (every owned game Steam's store marks Single-player AND with achievements, discovered from the
/// account's own games list and cached). Multiplayer games are left out - grinding a multiplayer game for
/// achievements looks less like a person.
///
/// A HUMAN account stays weighted-FIRST: a boost session is only an occasional grind slotted between long
/// stretches of the normal weighted schedule (<c>BoostRestMinutesHuman</c> apart, capped at
/// <c>MaxBoostGamesInARow</c> before a longer weighted rest), and never while asleep. A NON-human account rotates
/// targets back-to-back. It never fights a manual grind: while one the operator started is running, it stays out.
/// </summary>
public sealed class AchievementBoost(Bot bot) : BotModule(bot) {
	private int _index;                          // round-robin position in the target list
	private int _inARow;                         // consecutive boost sessions (human weighted-first cap)
	private bool _ours;                           // is the grind currently running one WE started?
	private DateTime _lastEnded = DateTime.MinValue;
	private string _status = "off";

	private List<uint> _singleplayer = [];       // discovered owned single-player games with achievements (mode 2)
	private DateTime _discoveredAt = DateTime.MinValue;

	public override string Name => "boost";
	public override string Status => On ? _status : "";

	private bool On => (Bot.Cfg.AchievementBoost != 0) && Bot.Cfg.UnlockAchievements;

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				if (On) {
					await DiscoverIfNeededAsync(ct).ConfigureAwait(false);
					Tick();
				} else {
					_status = "off";
				}
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception e) {
				Log.Warn($"achievement boost hiccup: {e.Message}", Bot.Name);
			}

			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	// ── target discovery (mode 2: all single-player) ─────────────────────────
	private async Task DiscoverIfNeededAsync(CancellationToken ct) {
		if (Bot.Cfg.AchievementBoost != 2) {
			return;   // only "all single-player" needs discovery; the picked list is just the setting
		}

		if ((_discoveredAt != DateTime.MinValue) && (DateTime.UtcNow - _discoveredAt < TimeSpan.FromHours(6))) {
			return;   // rebuilt at most every 6h - store categories don't change and libraries rarely do
		}

		if (!Bot.IsOnline) {
			return;
		}

		// The owned-games list needs the account's web session. Nothing else may have woken it (a pure grind does
		// no web work), so mint it here rather than waiting forever for another module to.
		if (!Bot.Web.Ready && !await Bot.Web.RefreshAsync(false, ct).ConfigureAwait(false)) {
			_status = "on - waiting for a web session";

			return;
		}

		List<uint> owned = await OwnedAppsAsync(ct).ConfigureAwait(false);

		if (owned.Count == 0) {
			if (_singleplayer.Count == 0) {
				_status = "on - couldn't read this account's games yet, retrying";
			}

			return;   // private/blip - keep any list we already had and try again next cycle
		}

		List<uint> found = [];
		int unknown = 0;

		foreach (uint app in owned) {
			if (ct.IsCancellationRequested) {
				return;
			}

			bool? sp = await GameCatalog.IsSingleplayerWithAchievementsAsync(app, ct).ConfigureAwait(false);

			if (sp == true) {
				found.Add(app);
			} else if (sp == null) {
				unknown++;   // store didn't answer for this one; don't finalise a list that's still missing games
			}
		}

		// Only replace the list once every game has a definite answer (or we found some) - so a half-finished store
		// sweep doesn't briefly shrink the target list.
		if ((found.Count > 0) || (unknown == 0)) {
			_singleplayer = found;
			_discoveredAt = DateTime.UtcNow;
			Log.Info($"achievement boost - {found.Count} single-player game(s) with achievements to hunt", Bot.Name);
		}
	}

	private async Task<List<uint>> OwnedAppsAsync(CancellationToken ct) {
		List<uint> apps = [];

		try {
			// The store's own "what do I own" data for the logged-in session - every owned app, independent of the
			// profile's privacy (the games XML needs public game details; this doesn't). Uses the store cookies the
			// web session already sets.
			string? json = await Bot.Web.GetAsync(new Uri(WebSession.Store, "/dynamicstore/userdata/"), ct).ConfigureAwait(false);

			if (string.IsNullOrEmpty(json)) {
				return apps;
			}

			using JsonDocument doc = JsonDocument.Parse(json);

			if (doc.RootElement.TryGetProperty("rgOwnedApps", out JsonElement owned) && (owned.ValueKind == JsonValueKind.Array)) {
				foreach (JsonElement e in owned.EnumerateArray()) {
					if (e.TryGetInt64(out long id) && (id > 0) && (id <= uint.MaxValue)) {
						apps.Add((uint) id);
					}
				}
			}
		} catch (Exception e) {
			Log.Debug($"couldn't read owned games: {e.Message}", Bot.Name);
		}

		return apps;
	}

	/// <summary>The games to work through, in order.</summary>
	private List<uint> Targets() => Bot.Cfg.AchievementBoost switch {
		1 => Bot.Cfg.AchievementBoostGames,
		2 => _singleplayer,
		_ => []
	};

	// ── the boost decision ───────────────────────────────────────────────────
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
			_status = Bot.Cfg.AchievementBoost == 2
				? "on - no single-player games with achievements found"
				: "on - no games to hunt (pick some under \"Boost these games\")";

			return;
		}

		// Human accounts hunt only while awake, and stay weighted-first: a stretch of the normal schedule sits
		// between boost sessions, and a longer one after a run of them.
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
}
