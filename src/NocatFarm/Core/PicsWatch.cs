using SteamKit2;

namespace NocatFarm.Core;

/// <summary>
/// Watches Steam's own change feed (PICS) for packages that have just become free - the FreePackages plugin's
/// idea, as a second source next to the giveaway list the free-games module already reads.
/// </summary>
/// <remarks>
/// Every package Steam edits shows up in the change feed within seconds, and a paid game being given away is a
/// package edit: its billing type flips to free-on-demand. Reading the feed means a giveaway is seen the moment
/// Steam publishes it rather than whenever somebody adds it to a list. One signed-in account asks for everyone -
/// the feed is the same for all of them.
///
/// This only finds CANDIDATES: anything free, available and not a free weekend. Most of those are free-to-play
/// games getting an update, and the free-games module's store check - the same one every giveaway goes through -
/// is what throws them out.
/// </remarks>
internal static class PicsWatch {
	/// <summary>A package found free, the first app in it, and when it was seen.</summary>
	internal readonly record struct Candidate(uint SubId, uint AppId, DateTime Seen);

	private static readonly SemaphoreSlim Gate = new(1, 1);
	private static readonly Dictionary<uint, Candidate> Found = [];
	private static uint _lastChange;
	private static DateTime _nextPoll = DateTime.MinValue;

	/// <summary>Candidates from the last week. A giveaway that has been over for longer is not coming back.</summary>
	internal static IReadOnlyList<Candidate> Candidates {
		get {
			lock (Found) {
				DateTime cutoff = DateTime.UtcNow.AddDays(-7);

				foreach (uint stale in Found.Values.Where(c => c.Seen < cutoff).Select(static c => c.SubId).ToList()) {
					Found.Remove(stale);
				}

				return [.. Found.Values];
			}
		}
	}

	/// <summary>
	/// Read what has changed since last time, if it is time to. Whichever account asks first does the asking;
	/// the rest return at once.
	/// </summary>
	/// <returns>How many new candidates turned up.</returns>
	internal static async Task<int> PollAsync(Bot bot, CancellationToken ct) {
		if ((DateTime.UtcNow < _nextPoll) || (bot.Apps is not { } apps) || !await Gate.WaitAsync(0, ct).ConfigureAwait(false)) {
			return 0;
		}

		try {
			// Every half hour or so. A giveaway runs for days, so asking more often gains nothing, and every poll that
			// finds changes is followed by product-info lookups for all of them - hundreds, on a busy day.
			_nextPoll = DateTime.UtcNow + Rng.Minutes(30, 40);

			SteamApps.PICSChangesCallback changes = await apps.PICSGetChangesSince(_lastChange, false, true).ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

			// The first ask only learns where the feed is up to. What changed before this started is what the
			// giveaway list is for; replaying hours of Steam's history would be thousands of packages.
			if ((_lastChange == 0) || changes.RequiresFullPackageUpdate) {
				_lastChange = changes.CurrentChangeNumber;

				return 0;
			}

			_lastChange = changes.CurrentChangeNumber;

			List<uint> changed = [.. changes.PackageChanges.Keys.Where(id => id != 0)];

			if (changed.Count == 0) {
				return 0;
			}

			int found = 0;

			foreach (uint[] chunk in changed.Chunk(100)) {
				AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet result = await apps
					.PICSGetProductInfo([], chunk.Select(static id => new SteamApps.PICSRequest(id)), false)
					.ToTask().WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

				if (result.Results == null) {
					Log.Debug($"Steam's change feed: no product info for {chunk.Length} changed package(s) ({(result.Failed ? "job failed" : "no results")})", bot.Name);
				}

				foreach (SteamApps.PICSProductInfoCallback page in result.Results ?? []) {
					foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo package in page.Packages.Values) {
						if (FreeNow(package.KeyValues) is not uint appId) {
							continue;
						}

						lock (Found) {
							if (Found.TryAdd(package.ID, new Candidate(package.ID, appId, DateTime.UtcNow))) {
								found++;
							}
						}
					}
				}
			}

			Log.Debug(new Said("Steam's change feed: {0} package(s) changed, {1} newly free - checking them", changed.Count, found), bot.Name);
			Log.Recovered($"pics:{bot.Name}");

			return found;
		} catch (OperationCanceledException e) when (!ct.IsCancellationRequested) {
			Log.DebugOnChange($"pics:{bot.Name}", $"Steam's change feed didn't answer in time: {Log.Describe(e)}", bot.Name);

			return 0;   // Steam didn't answer in time - next poll
		} catch (TimeoutException e) {
			Log.DebugOnChange($"pics:{bot.Name}", $"Steam's change feed didn't answer in time: {Log.Describe(e)}", bot.Name);

			return 0;
		} catch (Exception e) {
			Log.DebugOnChange($"pics:{bot.Name}", $"couldn't read Steam's change feed: {Log.Describe(e)}", bot.Name);

			return 0;
		} finally {
			Gate.Release();
		}
	}

	/// <summary>
	/// The first app in the package, if the package can be claimed for nothing right now; otherwise null.
	/// </summary>
	/// <remarks>The same tests FreePackages makes, less the ones the store check covers anyway.</remarks>
	internal static uint? FreeNow(KeyValue kv) {
		EBillingType billing = (EBillingType) kv["billingtype"].AsInteger();

		if ((billing != EBillingType.FreeOnDemand) && (billing != EBillingType.NoCost)) {
			return null;
		}

		if (((EPackageStatus) kv["status"].AsInteger() != EPackageStatus.Available)
			|| ((ELicenseType) kv["licensetype"].AsInteger() != ELicenseType.SinglePurchase)) {
			return null;
		}

		KeyValue extended = kv["extended"];
		ulong now = (ulong) DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		ulong expires = extended["expirytime"].AsUnsignedLong();

		// A free weekend is a licence that is taken away again; a beta-tester package can't be claimed this way;
		// a demo Steam has switched off is no longer there to have; and "free if you already own X" is only free
		// to people who own X.
		if (((expires > 0) && (expires < now))
			|| (extended["mustownapptopurchase"].AsUnsignedInteger() != 0)
			|| extended["freeweekend"].AsBoolean()
			|| extended["betatesterpackage"].AsBoolean()
			|| extended["deactivated_demo"].AsBoolean()) {
			return null;
		}

		uint first = kv["appids"].Children.Select(static a => a.AsUnsignedInteger()).FirstOrDefault(static a => a != 0);

		return first == 0 ? null : first;
	}
}
