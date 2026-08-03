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
	string Doing,
	string Persona,
	string Sitting,
	string Today,
	string Comments,
	string Warning,
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
			return new BotStatus(bot.StatusText, "", "", "", "", "", 0, 0, 0, 0);
		}

		HumanMode? human = BotManager.ModuleOf<HumanMode>(bot);
		Rep4RepModule? comments = BotManager.ModuleOf<Rep4RepModule>(bot);

		string doing;
		string sitting = "";
		string today = "";
		int done = 0, total = 0, dayDone = 0, dayTotal = 0;
		bool playing = false;

		if (bot.Grinding) {
			doing = "grinding " + GameNames.Of(bot.GrindGame);
			playing = true;

			if (bot.GrindUntil is { } until) {
				sitting = $"{Fmt.Hm((int) Math.Max(0, (until - DateTime.UtcNow).TotalMinutes))} left";
			}
		} else if (bot.Paused) {
			doing = "paused";
		} else if (bot.PlayingBlocked) {
			doing = "waiting - you're playing on this account";
		} else if (bot.InResumeGrace) {
			// You've stopped, Steam freed the account, and we're sitting out the courtesy delay before picking
			// back up. Saying "you're playing on this account" here (the human-mode StoodDown text, which is still
			// the live phase until the delay elapses) directly contradicts the "free again - picking back up"
			// line that just fired, which is exactly the confusion this branch removes.
			doing = "picking back up in a moment";
		} else if (human is { Current: not HumanMode.Phase.Off }) {
			doing = human.Doing;
			(done, total) = human.Session;
			playing = human.PlayingNow != 0;

			if (total > 0) {
				sitting = $"{Fmt.Hm(done)} of {Fmt.Hm(total)}";
			} else if (human.NextChange is { } next) {
				int left = (int) Math.Max(0, (next - DateTime.UtcNow).TotalMinutes);
				sitting = left > 0 ? $"{Fmt.Hm(left)} to go" : "any moment";
			}

			dayDone = human.PlayedMinutesToday;
			dayTotal = human.TargetMinutesToday;

			if (dayTotal > 0) {
				today = $"{Fmt.Hm(dayDone)} of {Fmt.Hm(dayTotal)} today";
			}
		} else if (bot.IsFarming) {
			doing = "farming trading cards";
			sitting = $"{bot.CardsRemaining} card(s) in {bot.GamesRemaining} game(s)";
			playing = true;
		} else if (!string.IsNullOrEmpty(bot.Playing)) {
			doing = "idling " + bot.Playing;
			today = bot.CardsRemaining > 0 ? $"{bot.CardsRemaining} card(s) left" : "nothing to farm";
			playing = true;
		} else {
			// "online, nothing running" was almost never true. Right after logging in the card farmer is part
			// way through reading badge pages and already says so in its own status - this asked nobody and
			// invented a blank answer instead, which is what put "still online, nothing running" in the log one
			// second before the line saying it had started farming.
			string busy = BotManager.ModuleOf<CardFarmer>(bot)?.Status ?? "";

			doing = (busy.Length > 0) && (busy is not ("idle" or "off")) ? busy : "online, nothing running";
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
		string persona =
			bot.Cfg.IUseThisAccount ? "status left to your own Steam client" :
			bot.PersonaOverridden ? $"{bot.PersonaReallyWord} - your own Steam client is overriding this" :
			bot.PersonaWord is not "online" ? bot.PersonaWord : "";

		// Otherwise this failure is completely silent: the row keeps reporting the name we ASKED for while
		// Steam shows the real game to everybody who looks at the profile.
		string warning = bot.CustomNameNotShowing ? $"Steam shows {bot.PlayingAsSeen}" : "";

		string said = (comments != null) && bot.Cfg.Rep4Rep ? $"{comments.PostsToday}/{comments.Cap}" : "";

		return new BotStatus(doing, persona, sitting, today, said, warning, done, total, dayDone, dayTotal) {
			AtTheKeyboard = playing,
			LifetimeMinutes = Lifetime.For(bot.Name)
		};
	}

	/// <summary>One flat line for the log, with the empty parts dropped rather than left as gaps.</summary>
	public string Line() {
		List<string> parts = [Doing];

		if (Persona.Length > 0) {
			parts.Add(Persona);
		}

		if (Sitting.Length > 0) {
			parts.Add(Sitting);
		}

		if (Today.Length > 0) {
			parts.Add(Today);
		}

		if (Warning.Length > 0) {
			parts.Add(Warning);
		}

		return string.Join(" · ", parts);
	}
}
