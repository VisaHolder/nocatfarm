using System.Globalization;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Idling more games than Steam will play at once, a batch at a time.
///
/// Steam plays 32 games at once (31 beside a custom name). A longer idle list - a whole library, say - used to be
/// cut, so the games past the cut never got a minute. This takes the list in turns instead: one batch plays for a
/// set time, then the next, and when the list runs out a new lap starts. Every game gets its turn once a lap.
///
/// A lap is decided once, when it starts - games with an hour target still to reach first, then the least played -
/// and kept, so the order doesn't reshuffle every time the library's playtime refreshes. Games added to the list
/// mid-lap join the end of it; games taken off simply drop out.
///
/// Where it is in the lap is saved, so a restart carries on with the same batch instead of starting over.
/// </summary>
public sealed class IdleRotation {
	public const int DefaultHours = 24;

	private readonly string _path;
	private readonly string _who;
	private readonly Lock _gate = new();

	private List<uint> _lap = [];
	private int _pos;
	private DateTime _movesAt = DateTime.MinValue;   // UTC. MinValue until the first batch starts
	private bool _moveNow;

	public IdleRotation(string path, string who) {
		_path = path;
		_who = who;
		Load();
	}

	public static string PathFor(string bot) => Path.Combine(ConfigStore.ConfigDir, "state", $"rotation-{bot}.json");

	/// <summary>How many real games fit at once. A custom name is sent as a game of its own and takes one slot.</summary>
	public static int Slots(bool customName) => SteamIds.GamesAtOnce - (customName ? 1 : 0);

	/// <summary>
	/// The order a fresh lap takes the games in: an hour target still to reach first, then anything listed in
	/// <paramref name="first"/> in the order it was listed, then the rest least played first. The appID breaks ties,
	/// so the same library always gives the same lap.
	/// </summary>
	public static List<uint> Order(IEnumerable<uint> games, IReadOnlyList<uint> first, Func<uint, int> minutes, Func<uint, bool> targeted) {
		Dictionary<uint, int> listedAt = [];

		for (int i = 0; i < first.Count; i++) {
			listedAt.TryAdd(first[i], i);
		}

		return [.. games.Where(static a => a != 0).Distinct()
			.OrderBy(a => targeted(a) ? 0 : listedAt.ContainsKey(a) ? 1 : 2)
			.ThenBy(a => !targeted(a) && listedAt.TryGetValue(a, out int at) ? at : minutes(a))
			.ThenBy(static a => a)];
	}

	/// <summary>"14:10" when it's within the day, "10-02 14:10" further out. Local time, as the log shows it.</summary>
	public static string When(DateTime utc) {
		DateTime local = utc.ToLocalTime();

		return (local - DateTime.Now < TimeSpan.FromHours(20))
			? local.ToString("HH:mm", CultureInfo.InvariantCulture)
			: local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
	}

	public DateTime MovesAt {
		get {
			lock (_gate) {
				return _movesAt;
			}
		}
	}

	/// <summary>Move on to the next batch the next time it idles, instead of waiting out this one.</summary>
	public void MoveOn() {
		lock (_gate) {
			_moveNow = true;
		}
	}

	public bool MoveRequested {
		get {
			lock (_gate) {
				return _moveNow;
			}
		}
	}

	/// <summary>
	/// The batch to idle now, moving on first if this one's time is up. <paramref name="ordered"/> is every game on
	/// the list in the order a fresh lap would take them (<see cref="Order"/>).
	/// </summary>
	public List<uint> Current(IReadOnlyList<uint> ordered, int size, TimeSpan every, DateTime now) {
		lock (_gate) {
			bool changed = Sync(ordered);

			if (_movesAt == DateTime.MinValue) {
				_movesAt = now + every;
				changed = true;
			} else if (_moveNow || (now >= _movesAt)) {
				// One batch on, however long it has been: after a few days switched off it carries on with the next
				// batch rather than skipping the ones it missed.
				_moveNow = false;
				_pos += size;

				if (_pos >= _lap.Count) {
					NewLap(ordered);
				}

				_movesAt = now + every;
				changed = true;
				Log.Info(new Said("idle rotation: batch {0} of {1} now, {2} games", BatchAt(_pos, size),
					(int) Math.Ceiling(_lap.Count / (double) size), Math.Min(size, _lap.Count)), _who);
			}

			// Rotate every 24h shortened to 6h: the batch on now shouldn't wait out the old, longer time.
			if (_movesAt - now > every) {
				_movesAt = now + every;
				changed = true;
			}

			if (changed) {
				Save();
			}

			return Window(_pos, size);
		}
	}

	/// <summary>The batch after this one - the start of a fresh lap when this is the last.</summary>
	public List<uint> Next(IReadOnlyList<uint> ordered, int size) {
		lock (_gate) {
			int pos = _pos + size;

			return pos >= _lap.Count ? [.. ordered.Take(size)] : Window(pos, size);
		}
	}

	public int Batch(int size) {
		lock (_gate) {
			return BatchAt(_pos, size);
		}
	}

	/// <summary>Rounded up: a game taken off earlier in the lap leaves the spot one short of a whole batch.</summary>
	private static int BatchAt(int pos, int size) => ((pos + Math.Max(1, size) - 1) / Math.Max(1, size)) + 1;

	public int Batches(int size) {
		lock (_gate) {
			return (int) Math.Ceiling(_lap.Count / (double) Math.Max(1, size));
		}
	}

	/// <summary>Bring the lap in line with the list: gone games drop out, new ones join the end.</summary>
	private bool Sync(IReadOnlyList<uint> ordered) {
		HashSet<uint> wanted = [.. ordered];
		bool changed = false;

		for (int i = _lap.Count - 1; i >= 0; i--) {
			if (!wanted.Contains(_lap[i])) {
				_lap.RemoveAt(i);
				changed = true;

				if (i < _pos) {
					_pos--;   // everything after it moved up one - keep pointing at the same game
				}
			}
		}

		HashSet<uint> have = [.. _lap];

		foreach (uint app in ordered) {
			if (have.Add(app)) {
				_lap.Add(app);
				changed = true;
			}
		}

		if ((_lap.Count == 0) || (_pos < 0) || (_pos >= _lap.Count)) {
			NewLap(ordered);
			changed = true;
		}

		return changed;
	}

	private void NewLap(IReadOnlyList<uint> ordered) {
		_lap = [.. ordered];
		_pos = 0;
	}

	/// <summary><paramref name="size"/> games from <paramref name="pos"/>, topped up from the start of the lap at its end.</summary>
	private List<uint> Window(int pos, int size) {
		List<uint> batch = [.. _lap.Skip(pos).Take(size)];

		if (batch.Count < size) {
			batch.AddRange(_lap.Take(Math.Min(size - batch.Count, pos)));
		}

		return batch;
	}

	private sealed record Saved(List<uint> Lap, int Pos, DateTime MovesAt);

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			AtomicFile.Write(_path, JsonSerializer.Serialize(new Saved(_lap, _pos, _movesAt)));
		} catch (Exception e) {
			Log.Debug(new Said("couldn't save the idle rotation: {0}", Log.Describe(e)), _who);
		}
	}

	private void Load() {
		try {
			if (!File.Exists(_path) || (JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) is not { } saved)) {
				return;
			}

			// Hand-edited or half-understood: keep what makes sense, and let Sync sort out the rest.
			_lap = [.. (saved.Lap ?? []).Where(static a => a != 0).Distinct()];
			_pos = Math.Max(0, saved.Pos);
			_movesAt = saved.MovesAt == default ? DateTime.MinValue : DateTime.SpecifyKind(saved.MovesAt, DateTimeKind.Utc);
		} catch (Exception e) {
			_lap = [];
			_pos = 0;
			_movesAt = DateTime.MinValue;
			Log.Debug(new Said("couldn't read the idle rotation - starting it fresh: {0}", Log.Describe(e)), _who);
		}
	}
}
