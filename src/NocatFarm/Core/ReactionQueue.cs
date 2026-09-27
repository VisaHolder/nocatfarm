namespace NocatFarm.Core;

/// <summary>
/// Things waiting their turn to be answered - trade offers, gifts - each on its own wait.
/// </summary>
/// <remarks>
/// Every item gets its own wait, so two that arrive together aren't answered together. While human mode has the
/// account asleep nothing is answered, and everything waiting goes back on hold; once the account is up again each
/// one gets a fresh wait from that moment. Keeping the old due times instead meant everything that came in
/// overnight was answered the second the account woke - a burst no person produces.
/// </remarks>
internal sealed class ReactionQueue<TKey> where TKey : notnull {
	/// <summary>When each item comes due; null while it is on hold.</summary>
	private readonly Dictionary<TKey, DateTime?> _due = [];
	private readonly Lock _gate = new();

	public int Count {
		get {
			lock (_gate) {
				return _due.Count;
			}
		}
	}

	/// <summary>Whether anything is on hold, waiting for the account to be up.</summary>
	public bool AnyHeld {
		get {
			lock (_gate) {
				return _due.Values.Any(static due => due == null);
			}
		}
	}

	/// <summary>When the next wait runs out, or null if no wait is running.</summary>
	public DateTime? Next {
		get {
			lock (_gate) {
				return _due.Values.Where(static due => due != null).Min();
			}
		}
	}

	public bool Contains(TKey key) {
		lock (_gate) {
			return _due.ContainsKey(key);
		}
	}

	public DateTime? DueOf(TKey key) {
		lock (_gate) {
			return _due.TryGetValue(key, out DateTime? due) ? due : null;
		}
	}

	/// <summary>Queue something to be answered at <paramref name="due"/>, or on hold with null.</summary>
	/// <returns>False if it was already queued - its wait is left as it was.</returns>
	public bool Add(TKey key, DateTime? due = null) {
		lock (_gate) {
			return _due.TryAdd(key, due);
		}
	}

	public void Remove(TKey key) {
		lock (_gate) {
			_due.Remove(key);
		}
	}

	/// <summary>Forget everything not in <paramref name="still"/> - answered or withdrawn somewhere else.</summary>
	public void RetainOnly(IEnumerable<TKey> still) {
		HashSet<TKey> keep = [.. still];

		lock (_gate) {
			foreach (TKey gone in _due.Keys.Where(key => !keep.Contains(key)).ToList()) {
				_due.Remove(gone);
			}
		}
	}

	/// <summary>The account is asleep: put everything back on hold.</summary>
	public void Hold() {
		lock (_gate) {
			foreach (TKey key in _due.Keys.ToList()) {
				_due[key] = null;
			}
		}
	}

	/// <summary>The account is up: give everything on hold its wait, starting now.</summary>
	/// <returns>What was just given a wait, and when it comes due.</returns>
	public List<(TKey Key, DateTime Due)> Arm(DateTime now, Func<TimeSpan> wait) {
		List<(TKey, DateTime)> armed = [];

		lock (_gate) {
			foreach (TKey key in _due.Where(static kv => kv.Value == null).Select(static kv => kv.Key).ToList()) {
				DateTime due = now + wait();
				_due[key] = due;
				armed.Add((key, due));
			}
		}

		return armed;
	}

	/// <summary>Everything whose wait has run out, soonest first.</summary>
	public List<TKey> Due(DateTime now) {
		lock (_gate) {
			return [.. _due.Where(kv => kv.Value <= now).OrderBy(static kv => kv.Value).Select(static kv => kv.Key)];
		}
	}
}
