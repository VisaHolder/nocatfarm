using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// How long a card takes to drop on this account, for "about how long until it's all farmed".
/// </summary>
/// <remarks>
/// Steam's own base rate is a card per 30 minutes of play: that's the default when a game's developer sets none,
/// and what ArchiSteamFarm counts too. Some games are set slower - an hour is common. So the estimate starts at
/// 30 minutes and learns from the drops it actually sees: each game farmed gets its own pace, and the account's
/// running average (kept across restarts) stands in for the games not farmed yet.
/// </remarks>
internal sealed class CardPace(string bot) {
	public const double SteamDefaultMinutes = 30;

	private readonly Dictionary<uint, double> _games = [];
	private readonly Lock _gate = new();
	private Saved? _saved;

	private sealed record Saved(double MinutesPerCard, int Drops);

	private string PathFor => Path.Combine(ConfigStore.ConfigDir, "state", $"cardpace-{bot}.json");

	/// <summary>The account's minutes per card - Steam's 30 until a drop has been seen.</summary>
	public double Minutes {
		get {
			lock (_gate) {
				return Load().MinutesPerCard;
			}
		}
	}

	/// <summary>Whether that figure comes from this account's own drops rather than Steam's default.</summary>
	public bool Learned {
		get {
			lock (_gate) {
				return Load().Drops > 0;
			}
		}
	}

	/// <summary>
	/// <paramref name="cards"/> dropped after <paramref name="played"/> of uninterrupted farming. A wait that is far
	/// outside anything Steam does - the badge page lagging, a drop landing the moment farming resumed - is left out.
	/// </summary>
	public void Dropped(uint appId, TimeSpan played, int cards) {
		if (cards <= 0) {
			return;
		}

		double each = played.TotalMinutes / cards;

		if (each is < 5 or > 180) {
			return;
		}

		lock (_gate) {
			_games[appId] = _games.TryGetValue(appId, out double had) ? Blend(had, each) : each;

			Saved now = Load();
			_saved = new Saved(now.Drops == 0 ? each : Blend(now.MinutesPerCard, each), now.Drops + cards);
			Save(_saved);
		}
	}

	/// <summary>Minutes a card takes in one game: its own pace once one has been timed, else the account's.</summary>
	public double MinutesFor(uint appId) {
		lock (_gate) {
			return _games.TryGetValue(appId, out double pace) ? pace : Load().MinutesPerCard;
		}
	}

	/// <summary>Farming time for everything in <paramref name="queue"/>, playtime still to build included.</summary>
	public int EstimateMinutes(IReadOnlyCollection<FarmTarget> queue, float hoursUntilDrops) {
		double minutes = 0;

		lock (_gate) {
			double account = Load().MinutesPerCard;

			foreach (FarmTarget game in queue) {
				minutes += game.CardsRemaining * (_games.TryGetValue(game.AppId, out double pace) ? pace : account);
			}
		}

		// Games too new to drop build their playtime together, so it's the furthest one behind that counts.
		double behind = queue.Where(g => g.HoursPlayed < hoursUntilDrops).Select(g => (double) (hoursUntilDrops - g.HoursPlayed)).DefaultIfEmpty(0).Max();

		return (int) Math.Ceiling(minutes + (behind * 60));
	}

	/// <summary>Recent drops count for more, so a slow game or a change on Steam's side shows up within a few cards.</summary>
	private static double Blend(double had, double seen) => (had * 0.7) + (seen * 0.3);

	private Saved Load() {
		if (_saved != null) {
			return _saved;
		}

		try {
			if (File.Exists(PathFor) && (JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathFor)) is { MinutesPerCard: >= 5 and <= 180 } saved)) {
				return _saved = saved;
			}
		} catch (Exception e) {
			Log.Debug(new Said("couldn't read the card pace: {0}", e.Message), bot);
		}

		return _saved = new Saved(SteamDefaultMinutes, 0);
	}

	private void Save(Saved saved) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(PathFor)!);
			AtomicFile.Write(PathFor, JsonSerializer.Serialize(saved));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the card pace: {0}", e.Message), bot);
		}
	}
}
