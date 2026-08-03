namespace NocatFarm.Core;

/// <summary>
/// Unlocking every achievement an account can set, across every game it owns.
/// </summary>
/// <remarks>
/// The opposite of <see cref="Modules.AchievementPacer"/> in every way, and deliberately so. The pacer exists
/// to make an account's history look earned; this exists for accounts that are not pretending - a throwaway,
/// or one being cleared out before sale.
///
/// It is permanent in the way that matters. The bits themselves can be cleared again with
/// <c>cheevo &lt;account&gt; &lt;appID&gt; lock all</c>, but Steam stamps the unlock time server-side at the
/// moment of the write and there is no way to change it. Running this puts several thousand achievements on the
/// profile all sharing one timestamp, and that is visible to anybody who looks, forever. Which is why the UI
/// asks twice and makes you type the word out.
/// </remarks>
public static class UnlockEverything {
	/// <summary>One run per account at a time. A second click while one is going would double every request.</summary>
	private static readonly HashSet<string> Running = [];

	public static bool IsRunning(string bot) {
		lock (Running) {
			return Running.Contains(bot);
		}
	}

	/// <summary>Kick it off in the background. Returns false when one is already going for this account.</summary>
	public static bool Start(Bot bot) {
		lock (Running) {
			if (!Running.Add(bot.Name)) {
				return false;
			}
		}

		_ = Task.Run(async () => {
			try {
				await RunAsync(bot).ConfigureAwait(false);
			} catch (Exception e) {
				Log.Error($"unlocking everything stopped: {e.GetType().Name}: {e.Message}", bot.Name);
			} finally {
				lock (Running) {
					Running.Remove(bot.Name);
				}
			}
		});

		return true;
	}

	private static async Task RunAsync(Bot bot) {
		IReadOnlyDictionary<uint, AppOwnership> owned = await bot.GetAppOwnershipAsync().ConfigureAwait(false);

		if (owned.Count == 0) {
			Log.Warn("couldn't work out what this account owns - nothing was changed", bot.Name);

			return;
		}

		Log.Warn($"unlocking every achievement across {owned.Count} owned app(s) - this takes a while and cannot be undone", bot.Name);

		int games = 0, unlocked = 0, failed = 0, looked = 0;

		foreach (uint app in owned.Keys) {
			if (!bot.IsOnline) {
				Log.Warn($"stopped early - the account signed out. {unlocked} achievement(s) across {games} game(s) were unlocked", bot.Name);

				return;
			}

			looked++;

			AchievementSet? set;

			try {
				set = await Achievements.GetAsync(bot, app).ConfigureAwait(false);
			} catch (Exception) {
				set = null;
			}

			// Most owned apps are DLC, tools, soundtracks or films and simply have no achievements. That is the
			// ordinary case, not a failure, so it is not worth a line each.
			if ((set == null) || (set.All.Count == 0)) {
				await Breathe().ConfigureAwait(false);

				continue;
			}

			List<Achievement> locked = set.Locked.Where(static a => a.Settable).ToList();

			if (locked.Count == 0) {
				await Breathe().ConfigureAwait(false);

				continue;
			}

			(bool ok, string message) = await Achievements.SetAsync(bot, set, locked, true).ConfigureAwait(false);

			if (ok) {
				games++;
				unlocked += locked.Count;
				Log.Reward($"unlocked all {locked.Count} in {GameNames.Of(app)}", bot.Name);
			} else {
				failed++;
				Log.Debug($"couldn't unlock {GameNames.Of(app)} - {message}", bot.Name);
			}

			// Progress, because on a big library this runs for a long time and silence looks like a hang.
			if ((looked % 250) == 0) {
				Log.Info($"still going - {looked}/{owned.Count} apps checked, {unlocked} unlocked so far", bot.Name);
			}

			await Breathe().ConfigureAwait(false);
		}

		string trouble = failed > 0 ? $", {failed} game(s) refused" : "";
		Log.Good($"done - {unlocked} achievement(s) unlocked across {games} game(s){trouble}", bot.Name);
	}

	/// <summary>
	/// A pause between apps.
	///
	/// Thousands of back-to-back stat requests is exactly the shape of traffic Steam rate-limits, and being cut
	/// off half way through leaves the job in a worse state than going slowly.
	/// </summary>
	private static Task Breathe() => Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(250, 600)));
}
