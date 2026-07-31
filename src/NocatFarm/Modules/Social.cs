using System.Globalization;
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
				bits.Add($"{_accepted} friend(s) added");
			}

			if (_replied > 0) {
				bits.Add($"{_replied} reply(s) sent");
			}

			return bits.Count > 0 ? string.Join(" · ", bits) : "";
		}
	}

	protected override async Task RunAsync(CancellationToken ct) {
		Bot.FriendRequest += OnFriendRequest;
		Bot.ClanInvite += OnClanInvite;
		Bot.ChatMessage += OnChatMessage;

		try {
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

				try {
					Bot.Friends?.RemoveFriend(new SteamID(steamId));
					Log.Info($"turned down a friend request from {steamId}", Bot.Name);
				} catch (Exception e) {
					Log.Debug($"couldn't turn down the request from {steamId}: {e.Message}", Bot.Name);
				}
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
					Log.Info($"ignoring a friend request from {steamId} - brand new private profile", Bot.Name);
					Bot.Friends?.RemoveFriend(new SteamID(steamId));

					return;
				}

				// Not instant, and not while it is meant to be asleep. Accepting the same second the request
				// lands is a robot accepting; doing it at 4am while the friends list shows offline is worse.
				int lo = Math.Max(0, Bot.Cfg.FriendRequestDelayMinMinutes);
				int hi = Math.Max(lo, Bot.Cfg.FriendRequestDelayMaxMinutes);
				await Task.Delay(Rng.Minutes(lo, hi)).ConfigureAwait(false);
				await WaitUntilAwakeAsync().ConfigureAwait(false);

				Bot.Friends?.AddFriend(new SteamID(steamId));
				_accepted++;
				Log.Event($"accepted a friend request from {steamId}", Bot.Name);
			} catch (Exception e) {
				Log.Debug($"couldn't handle the friend request from {steamId}: {e.Message}", Bot.Name);
			}
		});
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
		} catch {
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

		try {
			Bot.Notifications?.AcknowledgeClanInvite(clanId, true);
			Log.Event($"joined group {clanId}", Bot.Name);
		} catch (Exception e) {
			Log.Debug($"couldn't join group {clanId}: {e.Message}", Bot.Name);
		}
	}

	// ── messages ────────────────────────────────────────────────────────────
	private void OnChatMessage(ulong from, string message) {
		if ((from == 0) || string.IsNullOrWhiteSpace(message)) {
			return;
		}

		string text = message.Trim();

		// Say who messaged the account and what they said.
		//
		// Somebody messaging a farming account matters - it is a real person talking to something pretending to
		// be one, and whether the auto-reply was any good is impossible to judge without seeing what prompted
		// it. Trimmed, because a pasted wall of text should not take the log with it.
		Log.Info($"message from {from}: {(text.Length > 160 ? text[..160] + "..." : text)}", Bot.Name);

		// A command has to LOOK like one.
		//
		// Every message from a master used to be run as a command, so saying "hi" to your own account answered
		// "There's no 'hi' command" - and a master could never receive the ordinary auto-reply either, because
		// this branch returned before reaching it. Requiring a leading slash means a command is something you
		// deliberately type and everything else is just talking.
		if (text.StartsWith('/')) {
			string command = text[1..].Trim();

			if (command.Length == 0) {
				Send(from, "Type a command after the slash - /help lists them.");

				return;
			}

			if (!IsMaster(from)) {
				// Deliberately silent. Confirming that an account takes commands, to somebody not on the list,
				// tells a stranger exactly what this account is.
				Log.Debug($"ignoring a command from {from} - not on the list", Bot.Name);

				return;
			}

			_ = Task.Run(() => RunCommandAsync(from, command));

			return;
		}

		string reply = Bot.Cfg.AutoReply;

		if (!Bot.Cfg.AutoReplyEnabled || string.IsNullOrWhiteSpace(reply)) {
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
				// Typing takes time. The wait is randomised up to double the setting so it is never the same gap.
				int seconds = Math.Max(0, Bot.Cfg.AutoReplyDelaySeconds);

				if (seconds > 0) {
					await Task.Delay(Rng.Seconds(seconds, seconds * 2)).ConfigureAwait(false);
				}

				// If the account is meant to be in bed, the reply waits until morning - which is exactly what a
				// person who was asleep when you messaged them would do.
				await WaitUntilAwakeAsync().ConfigureAwait(false);

				Bot.Friends?.SendChatMessage(new SteamID(from), EChatEntryType.ChatMsg, reply);
				_replied++;
				Log.Info($"replied to a message from {from}", Bot.Name);
			} catch (Exception e) {
				Log.Debug($"couldn't reply to {from}: {e.Message}", Bot.Name);
			}
		});
	}

	private bool IsMaster(ulong steamId) => ParseIds(Bot.Cfg.CommandMasters).Contains(steamId);

	private void Send(ulong to, string text) {
		try {
			Bot.Friends?.SendChatMessage(new SteamID(to), EChatEntryType.ChatMsg, text);
		} catch (Exception e) {
			Log.Debug($"couldn't message {to}: {e.Message}", Bot.Name);
		}
	}

	/// <summary>Hold here until human mode says the account is up. Returns straight away when it already is.</summary>
	private async Task WaitUntilAwakeAsync() {
		CancellationToken ct = Cts?.Token ?? CancellationToken.None;

		while (!ct.IsCancellationRequested && !HumanMode.AwakeFor(Bot)) {
			await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
		}
	}

	/// <summary>Run a console command sent by Steam message and send the answer straight back.</summary>
	private async Task RunCommandAsync(ulong from, string command) {
		try {
			Log.Info($"command from {from}: {command}", Bot.Name);
			string answer = await Commands.RunAsync(command, Bot.Name).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(answer)) {
				answer = "done";
			}

			// Steam drops anything much over 2000 characters on the floor rather than truncating it.
			if (answer.Length > 1900) {
				answer = answer[..1900] + "\n… (cut short)";
			}

			Bot.Friends?.SendChatMessage(new SteamID(from), EChatEntryType.ChatMsg, answer);
		} catch (Exception e) {
			Log.Warn($"the command from {from} failed: {e.Message}", Bot.Name);

			try {
				Bot.Friends?.SendChatMessage(new SteamID(from), EChatEntryType.ChatMsg, "that didn't work: " + e.Message);
			} catch {
				// nothing more to do
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
