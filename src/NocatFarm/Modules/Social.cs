using System.Globalization;
using NocatFarm.Config;
using NocatFarm.Core;
using SteamKit2;

namespace NocatFarm.Modules;

/// <summary>
/// The account's social side: friend requests, group invites, answering messages, and letting you drive
/// nocatFarm by messaging one of your own accounts on Steam.
///
/// Two things here matter beyond convenience. Being friends with somebody lifts most of Steam's comment
/// throttling, which is the whole game for a rep4rep account. And an account that plays eight hours a day and
/// has never once answered a message reads as abandoned or automated - a short "afk, get back to you" costs
/// nothing and closes that off.
/// </summary>
public sealed class Social(Bot bot) : BotModule(bot) {
	private readonly Dictionary<ulong, DateTime> _repliedTo = [];
	private readonly HashSet<ulong> _handledInvites = [];
	private int _accepted;
	private int _replied;

	public override string Name => "social";

	public override string Status {
		get {
			List<string> bits = [];

			if (_accepted > 0) {
				bits.Add(Loc.T("{0} friend(s) added", _accepted));
			}

			if (_replied > 0) {
				bits.Add(Loc.T("{0} reply(s) sent", _replied));
			}

			return bits.Count > 0 ? string.Join(" · ", bits) : "";
		}
	}

	/// <summary>This run's token, taken once when it starts - see <see cref="WaitUntilAwakeAsync"/>.</summary>
	private CancellationToken _runToken;

	protected override async Task RunAsync(CancellationToken ct) {
		_runToken = ct;
		Bot.FriendRequest += OnFriendRequest;
		Bot.ClanInvite += OnClanInvite;
		Bot.ChatMessage += OnChatMessage;

		try {
			CatchUpWaiting();

			// Everything here is callback-driven. The loop exists only to keep the subscription alive for as long
			// as the module is running, and to expire the "already replied" list so it can't grow forever.
			while (!ct.IsCancellationRequested) {
				if (!await Sleep(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false)) {
					return;
				}

				lock (_repliedTo) {
					foreach (ulong stale in _repliedTo.Where(static kv => kv.Value < DateTime.UtcNow.AddDays(-2)).Select(static kv => kv.Key).ToList()) {
						_repliedTo.Remove(stale);
					}
				}
			}
		} finally {
			Bot.FriendRequest -= OnFriendRequest;
			Bot.ClanInvite -= OnClanInvite;
			Bot.ChatMessage -= OnChatMessage;
		}
	}

	/// <summary>
	/// Friend requests and group invites that were already waiting when this started.
	/// </summary>
	/// <remarks>
	/// Steam sends the friends list - every request that came in while the account was signed out - straight after signing
	/// in, and the modules start after that. Nobody was listening yet, so those were never answered; only a request that
	/// arrived later was. The list it already has is walked once here; one both here and in a list Steam sends meanwhile
	/// is only handled once (_handledInvites).
	/// </remarks>
	internal void CatchUpWaiting() {
		if (Bot.Friends is not { } friends) {
			return;
		}

		List<ulong> people = [];
		List<ulong> groups = [];

		try {
			for (int i = 0; i < friends.GetFriendCount(); i++) {
				SteamID id = friends.GetFriendByIndex(i);

				if (friends.GetFriendRelationship(id) == EFriendRelationship.RequestRecipient) {
					people.Add(id.ConvertToUInt64());
				}
			}

			for (int i = 0; i < friends.GetClanCount(); i++) {
				SteamID id = friends.GetClanByIndex(i);

				if (friends.GetClanRelationship(id) == EClanRelationship.Invited) {
					groups.Add(id.ConvertToUInt64());
				}
			}
		} catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException) {
			// The list changed while it was read - Steam sending a fresh one, which the handlers see anyway.
			Log.Debug($"friends list changed while it was read: {Log.Describe(e)}", Bot.Name);
		}

		foreach (ulong id in people) {
			OnFriendRequest(id);
		}

		foreach (ulong id in groups) {
			OnClanInvite(id);
		}
	}

	// ── friend requests ─────────────────────────────────────────────────────
	private void OnFriendRequest(ulong steamId) {
		if (steamId == 0) {
			return;
		}

		// Not accepting is not the same as ignoring. If the account was told to turn requests down, turn them
		// down; otherwise leave them sitting in the list for the user to look at. This setting promised exactly
		// that in its tooltip and, until now, did nothing at all.
		if (!Bot.Cfg.AcceptFriendRequests) {
			if (Bot.Cfg.RejectInvalidFriendInvites) {
				lock (_handledInvites) {
					if (!_handledInvites.Add(steamId)) {
						return;
					}
				}

				// Turned down after a person's delay too, and not at 4am - an instant refusal is as robotic as an
				// instant yes.
				_ = Task.Run(async () => {
					try {
						await Task.Delay(FriendWait()).ConfigureAwait(false);
						await WaitUntilAwakeAsync(FriendWait).ConfigureAwait(false);

						if (GoneMeanwhile(steamId) || !StillAsking(steamId)) {
							Forget(steamId);

							return;
						}

						Bot.Friends?.RemoveFriend(new SteamID(steamId));
						Log.Info(new Said("turned down a friend request from {0}", await SteamNames.OfAsync(Bot, steamId).ConfigureAwait(false)), Bot.Name);
					} catch (Exception e) {
						Log.Debug(new Said("couldn't turn down the request from {0}: {1}", steamId, Log.Describe(e)), Bot.Name);
						Forget(steamId);   // looked at again when Steam next sends the list, like an accept that failed
					}
				});
			}

			return;
		}

		lock (_handledInvites) {
			if (!_handledInvites.Add(steamId)) {
				return;   // Steam re-sends the whole friends list on every change
			}
		}

		_ = Task.Run(async () => {
			try {
				if (Bot.Cfg.IgnoreSuspiciousInvites && await LooksLikeSpamAsync(steamId).ConfigureAwait(false)) {
					await Task.Delay(FriendWait()).ConfigureAwait(false);
					await WaitUntilAwakeAsync(FriendWait).ConfigureAwait(false);

					if (GoneMeanwhile(steamId) || !StillAsking(steamId)) {
						Forget(steamId);

						return;
					}

					Log.Info(new Said("ignored a friend request from {0} - new private profile", await SteamNames.OfAsync(Bot, steamId).ConfigureAwait(false)), Bot.Name);
					Bot.Friends?.RemoveFriend(new SteamID(steamId));

					return;
				}

				// Not instant, and not while it is meant to be asleep. Accepting the same second the request
				// lands is a robot accepting; doing it at 4am while the friends list shows offline is worse.
				await Task.Delay(FriendWait()).ConfigureAwait(false);
				await WaitUntilAwakeAsync(FriendWait).ConfigureAwait(false);

				// Withdrawn, or already answered by hand, in the meantime: adding them now would send a friend request of
				// our own in the owner's name.
				if (GoneMeanwhile(steamId) || !StillAsking(steamId)) {
					Forget(steamId);

					return;
				}

				Bot.Friends?.AddFriend(new SteamID(steamId));
				_accepted++;
				Log.Event(new Said("accepted a friend request from {0}", await SteamNames.OfAsync(Bot, steamId).ConfigureAwait(false)), Bot.Name);
			} catch (Exception e) {
				Log.Debug(new Said("couldn't handle the friend request from {0}: {1}", steamId, Log.Describe(e)), Bot.Name);

				// Forgotten again, so it's picked back up when Steam next sends the friends list - which it does on every
				// reconnect. A disconnect while it waited for morning used to lose the request for as long as the app ran.
				lock (_handledInvites) {
					_handledInvites.Remove(steamId);
				}
			}
		});
	}

	/// <summary>
	/// Still a request waiting on this account. The answer goes out hours later on a human-mode account, and by then the
	/// owner may have accepted them himself - turning the request down then unfriended a real friend.
	/// </summary>
	private bool StillAsking(ulong steamId) => Bot.Friends?.GetFriendRelationship(new SteamID(steamId)) == EFriendRelationship.RequestRecipient;

	/// <summary>Not handled after all - so if it is still waiting when Steam next sends the list, it's looked at again.</summary>
	private void Forget(ulong id) {
		lock (_handledInvites) {
			_handledInvites.Remove(id);
		}
	}

	/// <summary>Signed out, or finishing up before it logs off - nothing sent now would reach Steam, or should.</summary>
	private bool Away => !Bot.IsOnline || Bot.Stopping;

	/// <summary>
	/// Signed out (or finishing up) by the time its wait ran out. The answer used to go anyway: Steam never got it, the
	/// log said "accepted", and the request was marked handled - so the list Steam sends at the next sign-in skipped it
	/// for as long as the app ran. Forgotten instead, so that list brings it back.
	/// </summary>
	private bool GoneMeanwhile(ulong id) {
		if (!Away) {
			return false;
		}

		Forget(id);

		return true;
	}

	/// <summary>The auto-reply didn't go after all, so the once-a-day rule doesn't count it.</summary>
	private void Unreplied(ulong from) {
		lock (_repliedTo) {
			_repliedTo.Remove(from);
		}
	}

	/// <summary>
	/// The shape every scam bot has: a private or brand-new profile with no level and no games. Anything we can't
	/// read is given the benefit of the doubt - refusing a real person is worse than accepting one bot.
	/// </summary>
	private async Task<bool> LooksLikeSpamAsync(ulong steamId) {
		try {
			string? html = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{steamId}/?xml=1")).ConfigureAwait(false);

			if (string.IsNullOrEmpty(html)) {
				return false;
			}

			// visibilityState 1 = private, 2 = friends only, 3 = public
			bool priv = html.Contains("<privacyState>private", StringComparison.OrdinalIgnoreCase)
				|| html.Contains("<visibilityState>1<", StringComparison.Ordinal);

			// A private profile OMITS steamRating altogether rather than sending it empty, so requiring an empty
			// one alongside "is private" could never both be true and the filter never fired once.
			bool noLevel = !html.Contains("<steamRating>", StringComparison.Ordinal)
				|| html.Contains("<steamRating></steamRating>", StringComparison.Ordinal);

			return priv && noLevel;
		} catch (Exception e) {
			Log.Failed($"couldn't read the profile of {steamId} for the spam check - taking it as a real person", e, Bot.Name);

			return false;
		}
	}

	private void OnClanInvite(ulong clanId) {
		if (!Bot.Cfg.AcceptGroupInvites || (clanId == 0)) {
			return;
		}

		lock (_handledInvites) {
			if (!_handledInvites.Add(clanId)) {
				return;
			}
		}

		// Joined after a person's delay, and not while asleep - the same as a friend request.
		_ = Task.Run(async () => {
			try {
				await Task.Delay(FriendWait()).ConfigureAwait(false);
				await WaitUntilAwakeAsync(FriendWait).ConfigureAwait(false);

				// Declined by hand while it waited - that answer stands.
				if (GoneMeanwhile(clanId) || (Bot.Friends?.GetClanRelationship(new SteamID(clanId)) != EClanRelationship.Invited)) {
					Forget(clanId);

					return;
				}

				Bot.Notifications?.AcknowledgeClanInvite(clanId, true);
				Log.Event(new Said("joined the Steam group {0}", ClanName(clanId)), Bot.Name);
			} catch (Exception e) {
				Log.Debug(new Said("couldn't join group {0}: {1}", ClanName(clanId), Log.Describe(e)), Bot.Name);
				Forget(clanId);   // a reconnect while it waited for morning cancels the wait - it's taken up again after
			}
		});
	}

	/// <summary>The group's name as Steam sent it with the invite - its 18-digit number only when there's none.</summary>
	private string ClanName(ulong clanId) => Bot.Friends?.GetClanName(new SteamID(clanId)) is { Length: > 0 } name ? name : clanId.ToString(CultureInfo.InvariantCulture);

	// ── messages ────────────────────────────────────────────────────────────
	/// <summary>
	/// Whether the auto-reply goes to whoever sent this. Never to one of your own accounts or to somebody on the command
	/// list - that's you: "help" typed without its slash got "im not real! add my new main" back, sent to the owner's
	/// own main. Your accounts only ever message each other with a command's answer, which isn't somebody to answer.
	/// </summary>
	public static bool AutoReplyGoesTo(ulong from, bool ownAccount, HashSet<ulong> masters) => (from != 0) && !ownAccount && !masters.Contains(from);

	/// <summary>A message Steam wrote itself, one BBCode tag and nothing else: its kind, and the game it's about if any.</summary>
	public sealed record Embed(string Kind, string? AppId);

	private static readonly System.Text.RegularExpressions.Regex EmbedPattern = new(
		@"^\[(?<kind>tradeoffer|lobbyinvite|gameinvite|remoteplaytogether)\b(?<attrs>[^\]]*)\](?:.*\[/\k<kind>\])?$",
		System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	/// <summary>The message as one of Steam's own - a trade offer, a game invite - or null when a person wrote it.</summary>
	public static Embed? SteamEmbed(string text) {
		System.Text.RegularExpressions.Match m = EmbedPattern.Match(text.Trim());

		if (!m.Success) {
			return null;
		}

		System.Text.RegularExpressions.Match app = System.Text.RegularExpressions.Regex.Match(m.Groups["attrs"].Value, @"appid=""?(\d+)");

		return new Embed(m.Groups["kind"].Value.ToLowerInvariant(), app.Success ? app.Groups[1].Value : null);
	}

	private void OnChatMessage(ulong from, string message) {
		if ((from == 0) || string.IsNullOrWhiteSpace(message)) {
			return;
		}

		DateTime received = DateTime.UtcNow;
		string text = message.Trim();

		// Steam writes some messages itself: "[tradeoffer sender=... id=...][/tradeoffer]" goes with every trade offer,
		// "[lobbyinvite appid=...]" with a game invite. They aren't somebody talking - printed raw they were gibberish in
		// the log, and the auto-reply answered them: "away right now" sent back to a trade offer from your own account.
		if (SteamEmbed(text) is { } embed) {
			if (embed.Kind == "tradeoffer") {
				return;   // the offer itself is announced by trading, with what's in it
			}

			if (embed.Kind is "lobbyinvite" or "gameinvite") {
				uint app = uint.TryParse(embed.AppId, out uint a) ? a : 0;
				_ = Task.Run(async () => Log.Info(new Said("{0} invited you to play {1}", await SteamNames.OfAsync(Bot, from).ConfigureAwait(false),
					app != 0 ? GameNames.Of(app) : new Said("a game").ToString()), Bot.Name));
			}

			return;
		}

		// Log who messaged the account and what they said - a real person talking to something pretending to
		// be one, and the auto-reply can't be judged without seeing what prompted it. Trimmed so a pasted wall
		// of text doesn't take the log with it.
		// Flatten to one short line - a command reply (e.g. the whole /help wall) echoes back to whoever
		// sent it, and logging its newlines and tab columns raw turned the log into a mess.
		// By name, not the 17-digit number it used to print.
		string oneLine = string.Join(' ', text.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
		string said = oneLine.Length > 120 ? oneLine[..120] + "…" : oneLine;
		// From one of your own accounts it's the answer to a command you sent it from this one - you read it in your chat
		// already. On screen it read as a stranger's message: "new: message from kylro: ACCOUNT STATE UPTIME ...".
		bool own = SteamNames.IsOwn(from);
		_ = Task.Run(async () => {
			Said line = new("message from {0}: {1}", await SteamNames.OfAsync(Bot, from).ConfigureAwait(false), said);

			if (own) {
				Log.Debug(line, Bot.Name);
			} else {
				Log.Info(line, Bot.Name);
			}
		});

		// A command must be prefixed with / or ! (e.g. /help or !status) - so ordinary chat is never mistaken for
		// one (a master who just talks gets nothing back - the auto-reply isn't for you). Only masters actually run them; a
		// prefixed command from anyone else is ignored, never answered, since confirming the account takes
		// commands would tell a stranger exactly what it is.
		bool looksLikeCommand = text.StartsWith('/') || text.StartsWith('!');

		if (looksLikeCommand) {
			if (!IsMaster(from)) {
				// Silent - confirming the account takes commands tells a stranger exactly what it is.
				_ = Task.Run(async () => Log.Debug(new Said("ignoring a command from {0} - not on the list", await SteamNames.OfAsync(Bot, from).ConfigureAwait(false)), Bot.Name));

				return;
			}

			string command = text[1..].Trim();

			if (command.Length == 0) {
				Send(from, "type a command after the / or ! - try /help");

				return;
			}

			_ = Task.Run(() => RunCommandAsync(from, command, received));

			return;
		}

		string reply = Bot.Cfg.AutoReply;

		// You're on the account yourself: you'll answer - an "away" reply sent in your name while you play is wrong.
		// And never to you, or to one of your accounts: see AutoReplyGoesTo.
		if (!Bot.Cfg.AutoReplyEnabled || string.IsNullOrWhiteSpace(reply) || Bot.PlayingBlocked || !AutoReplyGoesTo(from, own, ParseIds(Bot.Cfg.CommandMasters))) {
			return;
		}

		if (Bot.Cfg.AutoReplyOncePerDay) {
			lock (_repliedTo) {
				if (_repliedTo.TryGetValue(from, out DateTime last) && (last > DateTime.UtcNow.AddDays(-1))) {
					return;   // they have had the line already today; saying it twice gives the game away
				}

				_repliedTo[from] = DateTime.UtcNow;
			}
		}

		_ = Task.Run(async () => {
			try {
				// Typing takes time. Randomised up to double the setting so it is never the same gap.
				int seconds = Math.Max(0, ReactionSpeed.One(Bot.Cfg, nameof(BotConfig.AutoReplyDelaySeconds)));

				if (seconds > 0) {
					await Task.Delay(Rng.Seconds(seconds, seconds * 2)).ConfigureAwait(false);
				}

				// If the account is meant to be asleep, the reply waits until morning - exactly what a person
				// who was asleep when you messaged them would do.
				// Once up, overnight messages get answered over the morning, not all in its first minute.
				(int wakeLo, int wakeHi) = ReactionSpeed.Fixed(Bot.Cfg, 5, 60);
				await WaitUntilAwakeAsync(() => Rng.HumanMinutes(wakeLo, wakeHi) + (seconds > 0 ? Rng.Seconds(seconds, seconds * 2) : TimeSpan.Zero)).ConfigureAwait(false);

				// Signed out while it waited: nothing would arrive, so it isn't counted as said today either.
				if (Away) {
					Unreplied(from);

					return;
				}

				string who = await SteamNames.OfAsync(Bot, from).ConfigureAwait(false);
				Bot.SendChatMessage(from, reply);
				_replied++;
				Log.Info(new Said("auto-replied to {0} after {1}s", who, (int) (DateTime.UtcNow - received).TotalSeconds), Bot.Name);
			} catch (Exception e) {
				Log.Debug(new Said("couldn't reply to {0}: {1}", from, Log.Describe(e)), Bot.Name);
				Unreplied(from);
			}
		});
	}

	private bool IsMaster(ulong steamId) => ParseIds(Bot.Cfg.CommandMasters).Contains(steamId);

	private void Send(ulong to, string text) {
		try {
			Bot.SendChatMessage(to, text);
		} catch (Exception e) {
			Log.Debug(new Said("couldn't message {0}: {1}", to, Log.Describe(e)), Bot.Name);
		}
	}

	/// <summary>How long a friend request waits before it's accepted - a person's time, not a flat random pick.</summary>
	private TimeSpan FriendWait() {
		(int lo, int hi) = ReactionSpeed.Range(Bot.Cfg, nameof(BotConfig.FriendRequestDelayMinMinutes), nameof(BotConfig.FriendRequestDelayMaxMinutes));

		return Rng.HumanMinutes(lo, hi);
	}

	/// <summary>
	/// Hold on while human mode has the account asleep. If it had to hold, wait <paramref name="afterWaking"/> once
	/// the account is up too: going straight ahead the moment it woke answered everything from the night in the
	/// same minute, which is a burst no person produces.
	/// </summary>
	private async Task WaitUntilAwakeAsync(Func<TimeSpan> afterWaking) {
		// The run's own token, not Cts: that is swapped and disposed as the module stops and starts, and read from here - a
		// request answered hours later - it could be one already disposed, which throws.
		CancellationToken ct = _runToken;
		bool held = false;

		while (!ct.IsCancellationRequested && !HumanMode.ReadyFor(Bot)) {
			held = true;
			await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
		}

		if (held && !ct.IsCancellationRequested) {
			await Task.Delay(afterWaking(), ct).ConfigureAwait(false);
		}
	}

	/// <summary>Run a console command sent by Steam message and send the answer straight back.</summary>
	private async Task RunCommandAsync(ulong from, string command, DateTime received) {
		// Named - it's nearly always one of your own accounts, and "new" says that where 76561199... didn't.
		string who = from.ToString(CultureInfo.InvariantCulture);

		try {
			who = await SteamNames.OfAsync(Bot, from).ConfigureAwait(false);
		} catch (Exception e) {
			Log.Failed($"couldn't look up the Steam name of {from}", e, Bot.Name);   // the number will do
		}

		try {
			Log.Info(new Said("command from {0}: {1}", who, Commands.ForLog(command)), Bot.Name);   // a secret typed in chat stays out of the log
			string answer = await Commands.RunAsync(command, Bot.Name).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(answer)) {
				answer = "done";
			}

			// Steam drops anything much over 2000 characters on the floor rather than truncating it.
			if (answer.Length > 1900) {
				answer = answer[..1900] + "\n… (cut short)";
			}

			Bot.SendChatMessage(from, answer);
			Log.Info(new Said("answered {0} ({1}) in {2}s", who, command.Split(' ')[0], (int) (DateTime.UtcNow - received).TotalSeconds), Bot.Name);
		} catch (Exception e) {
			Log.Warn(new Said("the command from {0} failed: {1}", who, Log.Describe(e)), Bot.Name);
			Log.StackToFile(e, Bot.Name);

			try {
				Bot.SendChatMessage(from, "that didn't work: " + e.Message);
			} catch (Exception x) {
				// nothing more to do
				Log.Failed($"couldn't tell {from} the command failed", x, Bot.Name);
			}
		}
	}

	/// <summary>"76561198000000000, 76561198000000001" -> the ids. Anything unparseable is simply ignored.</summary>
	public static HashSet<ulong> ParseIds(string spec) {
		HashSet<ulong> ids = [];

		if (string.IsNullOrWhiteSpace(spec)) {
			return ids;
		}

		foreach (string token in spec.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && (id > 76561197960265728UL)) {
				ids.Add(id);
			}
		}

		return ids;
	}
}
