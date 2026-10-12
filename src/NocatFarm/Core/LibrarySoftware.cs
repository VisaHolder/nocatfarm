using System.Collections.Concurrent;
using SteamKit2;

namespace NocatFarm.Core;

/// <summary>
/// Software and tools the account holds a licence for - ShareX, OBS Studio, Blender, Godot - kept apart from the games.
///
/// GetOwnedGames lists games only, so a free app added to the library could neither be found by name in the dashboard's
/// game lists nor be reported by 'owns', and only a pasted appID put it on the overnight list. They are kept OUT of
/// <see cref="Games"/> on purpose: the whole-library idler, the most-played picks and the achievement hunter go by games,
/// and an account "playing" a screenshot tool by itself is the last thing anyone wants. Only what someone puts on a list
/// by name is idled.
/// </summary>
public sealed partial class Library {
	/// <summary>One piece of software (or a tool) the account owns: its own licence, for good.</summary>
	public sealed record SoftwareEntry(uint AppId, string Name, int MinutesPlayed);

	private Dictionary<uint, SoftwareEntry> _software = [];
	private Dictionary<uint, int> _playedForever = [];

	/// <summary>The software and tools this account owns, most played first.</summary>
	public IReadOnlyList<SoftwareEntry> Software => [.. _software.Values.OrderByDescending(static s => s.MinutesPlayed).ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// In the library, as a game or as software: what may be played, idled or set as a main game. Software goes anywhere a
	/// game can when someone picks it ("make OBS the main game instead of CS2"); only card farming, achievements, refunds
	/// and DLC stay games-only, as software has none of those.
	/// </summary>
	public bool CanPlay(uint app) => (Find(app) != null) || _software.ContainsKey(app);

	/// <summary>The software it has really used, as library entries - for the "most played" picks, which take it like a game.</summary>
	public IEnumerable<Entry> UsedSoftware => _software.Values.Where(static s => s.MinutesPlayed > 0)
		.Select(static s => new Entry(s.AppId, s.Name, s.MinutesPlayed, DateTime.MinValue, 0));

	/// <summary>Software or a tool this account owns, or null.</summary>
	public SoftwareEntry? FindSoftware(uint app) => _software.GetValueOrDefault(app);

	/// <summary>Steam's own word for what each app is (its PICS type and name), learned once a run - a type never changes.</summary>
	private static readonly ConcurrentDictionary<uint, (string Type, string Name)> Kinds = new();

	/// <summary>What Steam calls software: "Application" (ShareX, OBS, Blender, Godot) and "Tool".</summary>
	internal static bool IsSoftware(string type) =>
		type.Equals("application", StringComparison.OrdinalIgnoreCase) || type.Equals("tool", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The software in a set of licences: the account's own, for good, not already a game, and called software by Steam.
	/// Pure, so the tests can feed it.
	/// </summary>
	internal static List<SoftwareEntry> PickSoftware(IReadOnlyDictionary<uint, AppOwnership> licensed, IEnumerable<uint> games,
		IReadOnlyDictionary<uint, (string Type, string Name)> kinds, IReadOnlyDictionary<uint, int> played) {
		HashSet<uint> isGame = [.. games];

		return [.. licensed
			.Where(l => l.Value.Own && l.Value.Permanent && (l.Key != 480) && !isGame.Contains(l.Key))
			.Where(l => kinds.TryGetValue(l.Key, out (string Type, string Name) k) && IsSoftware(k.Type) && !string.IsNullOrWhiteSpace(k.Name))
			.Select(l => new SoftwareEntry(l.Key, kinds[l.Key].Name, played.GetValueOrDefault(l.Key)))];
	}

	/// <summary>Stands in for Steam's app info in the tests: appIDs in, (type, name) out.</summary>
	internal Func<List<uint>, CancellationToken, Task<Dictionary<uint, (string Type, string Name)>>>? AppInfoFeed { get; set; }

	/// <summary>Stands in for the account's licences in the tests.</summary>
	internal Func<Task<IReadOnlyDictionary<uint, AppOwnership>>>? LicenceFeed { get; set; }

	/// <summary>Apps Steam's app info was already asked about this run - with a type or without one (no store page).</summary>
	private static readonly ConcurrentDictionary<uint, bool> Asked = new();

	/// <summary>How many times this run an app was asked about without its answer coming back.</summary>
	private static readonly ConcurrentDictionary<uint, int> Tries = new();

	/// <summary>Asked this many times this run with no answer, an app isn't asked about again until the next run.</summary>
	internal const int MaxAppInfoTries = 3;

	/// <summary>The software read going on beside the library, or the last one - for the checks.</summary>
	internal Task? SoftwareRead { get; private set; }

	private readonly Lock _softwareGate = new();

	/// <summary>
	/// Starts reading the software once the games are in, as its own task: the library's read (and everybody waiting on
	/// it) doesn't wait for it. One at a time - a read still going isn't started again.
	/// </summary>
	private void StartSoftwareRead(List<Entry> games, CancellationToken ct) {
		lock (_softwareGate) {
			if (SoftwareRead is { IsCompleted: false }) {
				return;
			}

			SoftwareRead = Task.Run(async () => {
				try {
					await ReadSoftwareAsync(games, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					// the account is stopping
				} catch (Exception e) {
					Log.Debug(new Said("couldn't read the software this account holds: {0}", Log.Describe(e)), bot.Name);
				}
			}, CancellationToken.None);
		}
	}

	/// <summary>Read the software along with the library. Steam's app info is asked only for apps not asked about before this run.</summary>
	private async Task ReadSoftwareAsync(List<Entry> games, CancellationToken ct) {
		IReadOnlyDictionary<uint, AppOwnership> licensed = LicenceFeed != null
			? await LicenceFeed().ConfigureAwait(false)
			: await bot.GetAppOwnershipAsync().ConfigureAwait(false);

		if (licensed.Count == 0) {
			return;   // not worked out yet - keep what we had
		}

		HashSet<uint> isGame = [.. games.Select(static g => g.AppId)];
		List<uint> unknown = [.. licensed.Where(l => l.Value.Own && l.Value.Permanent && !isGame.Contains(l.Key) && !Kinds.ContainsKey(l.Key) && !Asked.ContainsKey(l.Key)).Select(static l => l.Key)];

		foreach (uint[] chunk in unknown.Chunk(250)) {
			Dictionary<uint, (string Type, string Name)> learned = AppInfoFeed != null
				? await AppInfoFeed([.. chunk], ct).ConfigureAwait(false)
				: await AppInfoAsync([.. chunk], ct).ConfigureAwait(false);

			// Asked and answered: one that came back with no type (no page) isn't asked about again at every 6-hourly
			// refresh. One whose answer didn't come back this time is asked again at the next - up to a few times a run,
			// so an app Steam never answers for isn't asked about every 6 hours for good.
			foreach (uint app in chunk) {
				if (learned.ContainsKey(app) || (Tries.AddOrUpdate(app, 1, static (_, n) => n + 1) >= MaxAppInfoTries)) {
					Asked[app] = true;
				}
			}

			foreach ((uint app, (string Type, string Name) kind) in learned) {
				if (kind.Type.Length == 0) {
					continue;   // answered, with no type: nothing to learn
				}

				Kinds[app] = kind;

				if (IsSoftware(kind.Type)) {
					GameNames.Learn(app, kind.Name);
				}
			}
		}

		List<SoftwareEntry> found = PickSoftware(licensed, isGame, Kinds, _playedForever);
		bool first = _software.Count == 0;
		_software = found.ToDictionary(static s => s.AppId);

		if (first && (found.Count > 0)) {
			Log.Debug(new Said("library: {0} piece(s) of software owned too ({1})", found.Count,
				string.Join(", ", found.Take(6).Select(static s => s.Name)) + (found.Count > 6 ? ", ..." : "")), bot.Name);
		}
	}

	/// <summary>
	/// Steam's type and name for each app, from its app info: an empty type for one that came back without one (or that
	/// Steam doesn't know), nothing at all for an app whose answer didn't come.
	/// </summary>
	private async Task<Dictionary<uint, (string Type, string Name)>> AppInfoAsync(List<uint> apps, CancellationToken ct) {
		Dictionary<uint, (string Type, string Name)> kinds = [];

		if (bot.Apps is not { } steamApps) {
			return kinds;
		}

		SteamApps.PICSTokensCallback tokens = await steamApps.PICSGetAccessTokens(apps, []).ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = await steamApps
			.PICSGetProductInfo(apps.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id))), [], false)
			.ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

		foreach (SteamApps.PICSProductInfoCallback page in info.Results ?? []) {
			foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo app in page.Apps.Values) {
				KeyValue common = app.KeyValues["common"];
				string type = common["type"].AsString() ?? "";

				// An app with no type at all (no page for it) is answered but left unknown, to be asked about again next run.
				kinds[app.ID] = (type, common["name"].AsString() ?? "");
			}

			foreach (uint unknownApp in page.UnknownApps) {
				kinds.TryAdd(unknownApp, ("", ""));
			}
		}

		return kinds;
	}

	/// <summary>What 'owns' and the "doesn't own it" warning go by.</summary>
	public enum Holding {
		/// <summary>The library isn't read yet (or the licences aren't), so it can't be told.</summary>
		Unknown,

		/// <summary>No licence for it at all.</summary>
		No,

		/// <summary>One of its games.</summary>
		Game,

		/// <summary>Borrowed through Steam Family.</summary>
		Family,

		/// <summary>Software or a tool.</summary>
		Software,

		/// <summary>A licence for something else (a DLC, a demo, a soundtrack).</summary>
		Other
	}

	/// <summary>Does the account hold this app - from what's already known, never asking Steam.</summary>
	public Holding Holds(uint app) {
		if (Find(app) is { } game) {
			return game.Shared ? Holding.Family : Holding.Game;
		}

		if (_software.ContainsKey(app)) {
			return Holding.Software;
		}

		if (!Ready) {
			return Holding.Unknown;
		}

		IReadOnlyDictionary<uint, AppOwnership>? licensed = bot.KnownOwnership;

		if ((licensed == null) || (licensed.Count == 0)) {
			return Holding.Unknown;
		}

		return licensed.ContainsKey(app) ? Holding.Other : Holding.No;
	}

	/// <summary>The name a list item is shown with: a game's, or software's marked "(software)".</summary>
	public string ItemName(uint app, bool english = false) =>
		Find(app)?.Name ?? (FindSoftware(app) is { } soft
			? english ? Loc.Fill("{0} (software)", [soft.Name]) : Loc.T("{0} (software)", soft.Name)
			: GameNames.Of(app));
}
