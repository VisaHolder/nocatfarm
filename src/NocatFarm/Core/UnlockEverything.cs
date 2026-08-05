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
				Log.Error(new Said("unlocking everything stopped: {0}: {1}", e.GetType().Name, e.Message), bot.Name);
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

		Log.Warn(new Said("unlocking every achievement across {0} owned app(s) - this takes a while and cannot be undone", owned.Count), bot.Name);

		int games = 0, unlocked = 0, failed = 0, looked = 0;
		int step = Math.Max(25, owned.Count / 10);

		foreach (uint app in owned.Keys) {
			if (!bot.IsOnline) {
				Log.Warn(new Said("stopped early - the account signed out. {0} achievement(s) across {1} game(s) were unlocked", unlocked, games), bot.Name);

				return;
			}

			looked++;

			// Progress, counted here rather than at the bottom of the loop.
			//
			// Down there it sat below two `continue`s, and those two cover almost everything an account owns -
			// DLC, tools, soundtracks, anything without achievements. So the counter only ever got tested on
			// the handful of apps that had some, and a 264-app run produced two lines three minutes apart:
			// "unlocking every achievement across 264 apps", silence, "done - 0 unlocked". Working perfectly
			// and completely indistinguishable from a hang.
			//
			// Ten updates whatever the library size, rather than one every 250 apps - which on a 264-app
			// account meant a single update, at the very end, after the answer was already known.
			if ((looked % step) == 0) {
				Log.Info(new Said("still going - {0}/{1} apps checked, {2} unlocked so far", looked, owned.Count, unlocked), bot.Name);
			}

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
				Log.Reward(new Said("unlocked all {0} in {1}", locked.Count, GameNames.Of(app)), bot.Name);
			} else {
				failed++;
				Log.Debug(new Said("couldn't unlock {0} - {1}", GameNames.Of(app), message), bot.Name);
			}

			await Breathe().ConfigureAwait(false);
		}

		string trouble = failed > 0 ? $", {failed} game(s) refused" : "";

		// "done - 0 achievement(s) unlocked" is a true sentence that reads as a broken feature. On an account
		// whose library is mostly multiplayer it is the ORDINARY answer: Steam awards those achievements
		// server-side and no client can set them, so there was never anything here to unlock.
		if (unlocked == 0) {
			Log.Good(new Said("done - nothing this account can unlock itself ({0} app(s) checked{1}). Steam awards the rest server-side.", looked, trouble), bot.Name);

			return;
		}

		Log.Good(new Said("done - {0} achievement(s) unlocked across {1} game(s){2}", unlocked, games, trouble), bot.Name);
	}

	/// <summary>
	/// A pause between apps.
	///
	/// Thousands of back-to-back stat requests is exactly the shape of traffic Steam rate-limits, and being cut
	/// off half way through leaves the job in a worse state than going slowly.
	/// </summary>
	private static Task Breathe() => Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(250, 600)));
}
