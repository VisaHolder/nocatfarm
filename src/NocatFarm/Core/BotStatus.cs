using NocatFarm.Modules;

namespace NocatFarm.Core;

/// <summary>
/// What one account is doing, worked out in exactly one place.
///
/// There were two copies of this - one in the window, one in the console board - and they had already drifted:
/// the console warned you when Steam was showing a different game than the custom name you asked for, and the
/// window silently did not. The heartbeat log would have been a third copy. Every surface now reads the same
/// answer and only decides how to paint it.
///
/// The order the checks run in matters: the most specific true thing wins. "online" is what an account is when
/// there is nothing better to say about it, not something worth saying while it is mid-session on Counter-Strike.
/// </summary>
public readonly record struct BotStatus(
	Said Doing,
	Said Persona,
	Said Sitting,
	Said Today,
	string Comments,
	Said Warning,
	int SessionDone,
	int SessionTotal,
	int DayDone,
	int DayTotal
) {
	/// <summary>True when the account is actually running a game right now, as opposed to asleep or resting.</summary>
	public bool AtTheKeyboard { get; private init; }

	/// <summary>
	/// The courtesy wait after you stop playing, as it really is. "In a moment" was said for the whole of it - on a
	/// human-mode account that can be three times the setting, so a quarter of an hour of "a moment" right after the
	/// log had said "picking back up in 14m".
	/// </summary>
	internal static Said PickingBackUp(DateTime resumesAt, DateTime now) {
		int left = (int) Math.Ceiling((resumesAt - now).TotalMinutes);

		return left > 1 ? new Said("picking back up in {0}", Fmt.Hm(left)) : new Said("picking back up in a moment");
	}

	/// <summary>
	/// The one status vocabulary the whole app uses - the rail chips, the filters, the cards and the mini window's
	/// "+7 more · 6 idling" all agree. A key, not words: "off", "idling", "needsyou"...
	/// </summary>
	public static string Group(Bot b) {
		if (!b.Cfg.Enabled || b.State == BotState.Stopped) {
			return "off";
		}

		if (b.State == BotState.Failed) {
			return "problem";
		}

		if (b.GuardPrompt != null || b.State == BotState.NeedsGuard) {
			return "needsyou";
		}

		if (b.State != BotState.Online) {
			return "connecting";
		}

		if (b.IsFarming) {
			return "farming";
		}

		// Same as the console: a grind is not idling, and saying so contradicted the detail line beside it.
		// Grouped with "playing" rather than given a chip of its own - it IS playing one game deliberately,
		// which is exactly what that chip means, and the row's own text names the game and the time left.
		// Paused, or you're on it: the grind's game is off, and "playing" counted the account as working.
		if (b.Grinding && !b.Paused && !b.PlayingBlocked) {
			return "playing";
		}

		// Finishing up before it logs off - between things, not whatever human mode was about to start.
		if (b.Stopping) {
			return "break";
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(b);

		if (human is { Current: not HumanMode.Phase.Off }) {
			return human.Current switch {
				HumanMode.Phase.Playing => "playing",
				HumanMode.Phase.ShortBreak or HumanMode.Phase.MealBreak => "break",
				HumanMode.Phase.NightIdle => "nightidle",
				HumanMode.Phase.Asleep => "asleep",

				// Up and showing online to everybody, just not in a game - so not "asleep", which is what these used to
				// be filed under while the friends list said otherwise.
				HumanMode.Phase.DoneForToday => "done",
				HumanMode.Phase.DayOff => "dayoff",

				// Settling in and switching games are both "between things", which is what a break already means
				// on the dashboard - and far more honest than the "online" they used to fall through to.
				HumanMode.Phase.WarmingUp or HumanMode.Phase.SwitchingGame => "break",
				_ => "online"
			};
		}

		return string.IsNullOrEmpty(b.Playing) ? "online" : "idling";
	}

	public static BotStatus Of(Bot bot) {
		// Offline, or finishing up before it logs off: the account's own status says it - not what human mode was up to.
		if (!bot.IsOnline || bot.Stopping) {
			return new BotStatus(new Said(bot.StatusText), default, default, default, "", default, 0, 0, 0, 0);
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);
		Rep4RepModule? comments = BotManager.ModuleOf<Rep4RepModule>(bot);

		Said doing;
		Said sitting = default;
		Said today = default;
		int done = 0, total = 0, dayDone = 0, dayTotal = 0;
		bool playing = false;

		// Not while paused or while you're on it: the grind's game is off then, and "grinding" on every screen said
		// otherwise - the pause further down is what's true.
		if (bot.Grinding && !bot.Paused && !bot.PlayingBlocked) {
			doing = bot.GrindDropsLeft > 0
				? new Said("going for {0} card drop(s) in {1}", bot.GrindDropsLeft, GameNames.Of(bot.GrindGame))
				: new Said("grinding {0}", GameNames.Of(bot.GrindGame));
			playing = true;

			if (bot.GrindUntil is { } until) {
				sitting = new Said("{0} left", Fmt.Hm((int) Math.Max(0, (until - DateTime.UtcNow).TotalMinutes)));
			}
		} else if (bot.DropsFirstActive && !bot.Paused) {
			doing = new Said("going for {0}: {1} of {2} card drop(s)", GameNames.Of(bot.DropsFirstApp), bot.DropsFirstGot, bot.DropsFirstWant);
			playing = bot.IsFarming;
		} else if (bot.Paused) {
			doing = new Said("paused");
		} else if (bot.PlayingBlocked) {
			doing = new Said("waiting - you're playing on this account");
		} else if (bot.InResumeGrace) {
			// You've stopped, Steam freed the account, and we're sitting out the courtesy delay before picking
			// back up. Saying "you're playing on this account" here (the human-mode StoodDown text, which is still
			// the live phase until the delay elapses) directly contradicts the "free again - picking back up"
			// line that just fired, which is exactly the confusion this branch removes.
			doing = PickingBackUp(bot.ResumesAt, DateTime.UtcNow);
		} else if (human is { Current: not HumanMode.Phase.Off }) {
			doing = human.Doing;
			(done, total) = human.Session;
			playing = human.PlayingNow != 0;

			if (total > 0) {
				sitting = new Said("{0}/{1}", Fmt.Hm(done), Fmt.Hm(total));
			} else if (human.NextChange is { } next) {
				int left = (int) Math.Max(0, (next - DateTime.UtcNow).TotalMinutes);
				sitting = left > 0 ? new Said("{0} to go", Fmt.Hm(left)) : new Said("any moment");
			}

			dayDone = human.PlayedMinutesToday;
			dayTotal = human.TargetMinutesToday;

			if (dayTotal > 0) {
				today = new Said("{0}/{1} today", Fmt.Hm(dayDone), Fmt.Hm(dayTotal));
			}
		} else if (bot.IsFarming) {
			doing = new Said("farming trading cards");
			sitting = new Said("{0} card(s) in {1} game(s)", bot.CardsRemaining, bot.GamesRemaining);
			playing = true;
		} else if (!string.IsNullOrEmpty(bot.Playing)) {
			doing = new Said("idling {0}", bot.Playing);
			today = bot.CardsRemaining > 0 ? new Said("{0} card(s) left", bot.CardsRemaining) : new Said("nothing to farm");
			playing = true;
		} else {
			// "online, nothing running" was almost never true. Right after logging in the card farmer is part
			// way through reading badge pages and already says so in its own status - this asked nobody and
			// invented a blank answer instead, which is what put "still online, nothing running" in the log one
			// second before the line saying it had started farming.
			string busy = BotManager.ModuleOf<CardFarmer>(bot)?.Status ?? "";

			// Loc.Is, not a plain ==: CardFarmer.Status is localised, so `busy is "idle"` stopped matching the
			// moment the language changed and the readout started reporting "im Leerlauf" as though it were real
			// work. Loc.Is matches the word in any language, including the one the status was cached in.
			doing = (busy.Length > 0) && !Loc.Is(busy, "idle") && !Loc.Is(busy, "off")
				? new Said(busy)
				: new Said("online, nothing running");
		}

		// Kept out of Doing so each surface can dim it or not. Only worth saying when it is NOT the ordinary
		// case - "online" on every single row is pure noise.
		// What we asked for - unless somebody else is winning, in which case say what is actually showing and
		// why. Reporting the request as though it were the outcome is the thing this whole readout exists to
		// prevent, and it was doing it.
		// Never claim a status we are not setting.
		//
		// With "I sign into this one myself" on, the app deliberately never writes the persona - so reporting
		// the override it WOULD have used is exactly the lie this readout exists to prevent. Say who is
		// actually in charge instead.
		//
		// PersonaWord stays English on the Bot - plugins read it as a stable value and the "online" test below
		// has to keep matching in every language. It is translated here, at the point it becomes prose.
		Said persona =
			bot.Cfg.IUseThisAccount ? new Said("status left to your own Steam client") :
			bot.PersonaOverridden ? new Said("{0} - your own Steam client is overriding this", new Said(bot.PersonaReallyWord)) :
			bot.PersonaWord is not "online" ? new Said(bot.PersonaWord) : default;

		// Otherwise this failure is completely silent: the row keeps reporting the name we ASKED for while
		// Steam shows the real game to everybody who looks at the profile.
		Said warning = bot.CustomNameNotShowing ? new Said("Steam shows {0}", bot.PlayingAsSeen) : default;

		string said = (comments != null) && bot.Cfg.Rep4Rep ? $"{comments.PostsToday}/{comments.Cap}" : "";

		return new BotStatus(doing, persona, sitting, today, said, warning, done, total, dayDone, dayTotal) {
			AtTheKeyboard = playing
		};
	}

	/// <summary>One flat line for the log, with the empty parts dropped rather than left as gaps.</summary>
	/// <remarks>
	/// Returns a Said whose single value is a function, so the join happens when somebody reads the line rather
	/// than when it was logged. Joining here would render every part immediately and freeze the whole sentence
	/// in the language of the moment - which is the one thing the heartbeat, printed every few minutes for the
	/// life of the process, must not do.
	/// </remarks>
	public Said Line() {
		Said[] parts = [Doing, Persona, Sitting, Today, Warning];

		return new Said("{0}", (Func<string>) (() => string.Join(" · ",
			parts.Where(static p => !p.IsEmpty).Select(static p => p.ToString()))));
	}
}
