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
				Log.Error(new Said("unlocking everything stopped: {0}: {1}", e.GetType().Name, Log.Scrub(e.Message)), bot.Name);
				Log.StackToFile(e, bot.Name);
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
			Log.Warn("couldn't tell what this account owns - nothing changed", bot.Name);

			return;
		}

		Log.Warn(new Said("unlocking all achievements in {0} app(s) - can't be undone", owned.Count), bot.Name);

		int games = 0, unlocked = 0, failed = 0, looked = 0, held = 0, unsure = 0, steamOnly = 0, unmapped = 0;
		int step = Math.Max(25, owned.Count / 10);

		foreach (uint app in owned.Keys) {
			if (!bot.IsOnline) {
				Log.Warn(new Said("stopped - signed out after {0} achievement(s) in {1} game(s)", unlocked, games), bot.Name);

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
			} catch (Exception e) {
				Log.Failed($"couldn't read the achievements of {app}", e, bot.Name);
				set = null;
			}

			// Most owned apps are DLC, tools, soundtracks or films and simply have no achievements. That is the
			// ordinary case, not a failure, so it is not worth a line each.
			if ((set == null) || (set.All.Count == 0)) {
				await Breathe().ConfigureAwait(false);

				continue;
			}

			List<Achievement> locked = set.Locked.Where(static a => a.Settable).ToList();
			steamOnly += set.Locked.Count(static a => !a.Settable);

			if (locked.Count == 0) {
				await Breathe().ConfigureAwait(false);

				continue;
			}

			// Not even here: an account that isn't pretending still can't have played DLC it doesn't own, and an
			// achievement from one sits on its profile as proof of the tool. Waited for, game by game - a game with a
			// hundred DLC takes a few minutes to work out, which is nothing next to the whole run. A game that can't be
			// worked out (the store not answering) is skipped whole rather than guessed at.
			DlcAchievements.View dlc = await DlcAchievements.ViewAsync(bot, app, TimeSpan.FromMinutes(15)).ConfigureAwait(false);

			if (!dlc.Known) {
				unsure++;
				Log.Debug($"unlock everything: skipped {GameNames.Of(app)} - couldn't tell which of its achievements come with DLC", bot.Name);
				await Breathe().ConfigureAwait(false);

				continue;
			}

			int before = locked.Count;
			locked = [.. locked.Where(dlc.Allows)];

			// A game it can't map is its own count: those may well be the base game's, and "from DLC it doesn't own"
			// would be claiming more than is known.
			if (dlc.Unmapped) {
				unmapped += before - locked.Count;
			} else {
				held += before - locked.Count;
			}

			if (locked.Count == 0) {
				await Breathe().ConfigureAwait(false);

				continue;
			}

			(bool ok, string message, int changed) = await Achievements.SetAsync(bot, set, locked, true, dlc: dlc).ConfigureAwait(false);

			// What really changed, not what was asked for: read again under the write's gate, some can be unlocked by
			// now (the pacer, a 'cheevo'), and none of them is news - counted, they came up twice in the total. All of
			// them already unlocked by the time it wrote is nothing to say at all.
			if (!ok) {
				failed++;
				Log.Debug(new Said("couldn't unlock {0} - {1}", GameNames.Of(app), message), bot.Name);
			} else if (changed > 0) {
				games++;
				unlocked += changed;
				Log.Reward(new Said("unlocked all {0} in {1}", changed, GameNames.Of(app)), bot.Name, topic: Topic.Achievements);
			}

			await Breathe().ConfigureAwait(false);
		}

		Said trouble = failed > 0 ? new Said(", {0} game(s) refused", failed) : default;

		// What it left, and why - said with the result, so "done" is never read as "everything".
		if (held > 0) {
			trouble = new Said("{0}, {1}", trouble, new Said("{0} left alone - from DLC this account doesn't own", held));
		}

		if (unmapped > 0) {
			trouble = new Said("{0}, {1}", trouble, new Said("{0} left alone - can't tell which achievements come with its DLC", unmapped));
		}

		if (unsure > 0) {
			trouble = new Said("{0}, {1}", trouble, new Said("{0} game(s) skipped - couldn't tell which achievements come with DLC", unsure));
		}

		// "done - 0 achievement(s) unlocked" is a true sentence that reads as a broken feature. On an account
		// whose library is mostly multiplayer it is the ORDINARY answer: Steam awards those achievements
		// server-side and no client can set them, so there was never anything here to unlock.
		//
		// But only then. "Steam sets the rest" was said whatever was left - achievements held for DLC included, which
		// Steam will never set either. What's left is said as what it is: the counts above, and Steam's own share.
		if (unlocked == 0) {
			bool heldBack = (held > 0) || (unmapped > 0) || (unsure > 0);

			Log.Good(!heldBack && (steamOnly > 0)
				? new Said("done - nothing it can unlock ({0} app(s) checked{1}), Steam sets the rest", looked, trouble)
				: new Said("done - nothing it can unlock ({0} app(s) checked{1})", looked, steamOnly > 0 ? new Said("{0}, {1}", trouble, new Said("{0} only Steam can award", steamOnly)) : trouble), bot.Name);

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
