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

	/// <summary>Total minutes this account has ever spent with a game running.</summary>
	public int LifetimeMinutes { get; private init; }

	public static BotStatus Of(Bot bot) {
		if (!bot.IsOnline) {
			return new BotStatus(new Said(bot.StatusText), default, default, default, "", default, 0, 0, 0, 0);
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);
		Rep4RepModule? comments = BotManager.ModuleOf<Rep4RepModule>(bot);

		Said doing;
		Said sitting = default;
		Said today = default;
		int done = 0, total = 0, dayDone = 0, dayTotal = 0;
		bool playing = false;

		if (bot.Grinding) {
			doing = new Said("grinding {0}", GameNames.Of(bot.GrindGame));
			playing = true;

			if (bot.GrindUntil is { } until) {
				sitting = new Said("{0} left", Fmt.Hm((int) Math.Max(0, (until - DateTime.UtcNow).TotalMinutes)));
			}
		} else if (bot.Paused) {
			doing = new Said("paused");
		} else if (bot.PlayingBlocked) {
			doing = new Said("waiting - you're playing on this account");
		} else if (bot.InResumeGrace) {
			// You've stopped, Steam freed the account, and we're sitting out the courtesy delay before picking
			// back up. Saying "you're playing on this account" here (the human-mode StoodDown text, which is still
			// the live phase until the delay elapses) directly contradicts the "free again - picking back up"
			// line that just fired, which is exactly the confusion this branch removes.
			doing = new Said("picking back up in a moment");
		} else if (human is { Current: not HumanMode.Phase.Off }) {
			doing = human.Doing;
			(done, total) = human.Session;
			playing = human.PlayingNow != 0;

			if (total > 0) {
				sitting = new Said("{0} of {1}", Fmt.Hm(done), Fmt.Hm(total));
			} else if (human.NextChange is { } next) {
				int left = (int) Math.Max(0, (next - DateTime.UtcNow).TotalMinutes);
				sitting = left > 0 ? new Said("{0} to go", Fmt.Hm(left)) : new Said("any moment");
			}

			dayDone = human.PlayedMinutesToday;
			dayTotal = human.TargetMinutesToday;

			if (dayTotal > 0) {
				today = new Said("{0} of {1} today", Fmt.Hm(dayDone), Fmt.Hm(dayTotal));
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
			AtTheKeyboard = playing,
			LifetimeMinutes = Lifetime.For(bot.Name)
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
