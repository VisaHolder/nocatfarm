using SteamKit2;
using SteamKit2.Internal;

namespace NocatFarm.Core;

/// <summary>
/// The Steam Family calls the library needs, so the tests can stand in for Steam. Names confirmed against SteamKit2 3.3.0's
/// generated protobufs (SteamKit2.Internal.FamilyGroups): GetFamilyGroupForUser, GetFamilyGroup (send_running_apps),
/// GetPreferredLenders and SetPreferredLender (family_groupid, appid, lender_steamid).
/// </summary>
public interface IFamilySteam {
	/// <summary>The account's family group: 0 when it isn't in one, null when Steam couldn't be asked.</summary>
	Task<ulong?> FamilyGroupIdAsync(CancellationToken ct);

	/// <summary>Ask Steam to send the "who's running what" picture now (GetFamilyGroup with send_running_apps). True if it took the request.</summary>
	Task<bool> AskRunningAppsAsync(ulong family, CancellationToken ct);

	/// <summary>Which member is this account's preferred lender for each app, or null when Steam couldn't be asked.</summary>
	Task<Dictionary<uint, ulong>?> PreferredLendersAsync(ulong family, CancellationToken ct);

	/// <summary>Make <paramref name="lender"/>'s copy the one this account borrows for <paramref name="app"/>.</summary>
	Task<EResult> SetPreferredLenderAsync(ulong family, uint app, ulong lender, CancellationToken ct);

	/// <summary>A member's Steam name if this session has heard it, or null.</summary>
	string? NameOf(ulong steamId);
}

/// <summary>The real thing: the account's own Steam connection.</summary>
internal sealed class SteamFamilyCalls(Bot bot) : IFamilySteam {
	private FamilyGroups? Service => bot.IsOnline ? bot.Unified?.CreateService<FamilyGroups>() : null;

	public async Task<ulong?> FamilyGroupIdAsync(CancellationToken ct) {
		if ((Service is not { } family) || (bot.SteamId == 0)) {
			return null;
		}

		SteamUnifiedMessages.ServiceMethodResponse<CFamilyGroups_GetFamilyGroupForUser_Response> answer = await family
			.GetFamilyGroupForUser(new CFamilyGroups_GetFamilyGroupForUser_Request { steamid = bot.SteamId })
			.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

		if (answer.Result != EResult.OK) {
			return null;
		}

		return answer.Body.is_not_member_of_any_group ? 0 : answer.Body.family_groupid;
	}

	public async Task<bool> AskRunningAppsAsync(ulong familyId, CancellationToken ct) {
		if (Service is not { } family) {
			return false;
		}

		SteamUnifiedMessages.ServiceMethodResponse<CFamilyGroups_GetFamilyGroup_Response> answer = await family
			.GetFamilyGroup(new CFamilyGroups_GetFamilyGroup_Request { family_groupid = familyId, send_running_apps = true })
			.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

		return answer.Result == EResult.OK;
	}

	public async Task<Dictionary<uint, ulong>?> PreferredLendersAsync(ulong familyId, CancellationToken ct) {
		if (Service is not { } family) {
			return null;
		}

		SteamUnifiedMessages.ServiceMethodResponse<CFamilyGroups_GetPreferredLenders_Response> answer = await family
			.GetPreferredLenders(new CFamilyGroups_GetPreferredLenders_Request { family_groupid = familyId })
			.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

		if (answer.Result != EResult.OK) {
			return null;
		}

		Dictionary<uint, ulong> lenders = [];

		foreach (CFamilyGroups_GetPreferredLenders_Response.FamilyMember member in answer.Body.members) {
			foreach (uint app in member.preferred_appids) {
				lenders[app] = member.steamid;
			}
		}

		return lenders;
	}

	public async Task<EResult> SetPreferredLenderAsync(ulong familyId, uint app, ulong lender, CancellationToken ct) {
		if (Service is not { } family) {
			return EResult.NotLoggedOn;
		}

		SteamUnifiedMessages.ServiceMethodResponse<CFamilyGroups_SetPreferredLender_Response> answer = await family
			.SetPreferredLender(new CFamilyGroups_SetPreferredLender_Request { family_groupid = familyId, appid = app, lender_steamid = lender })
			.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

		return answer.Result;
	}

	public string? NameOf(ulong steamId) {
		try {
			string? name = bot.Friends?.GetFriendPersonaName(new SteamID(steamId));

			return string.IsNullOrWhiteSpace(name) ? null : name;
		} catch (Exception) {
			return null;   // only for a nicer line - never worth failing over
		}
	}
}

/// <summary>
/// Steam Family: who else is playing which shared game, on whose copy, and borrowing another member's free copy.
///
/// Steam lends a shared game one copy at a time, and a game several members own has a copy per owner. Steam pushes the
/// family's whole "who's running what" picture to every member when it changes (CFamilyGroupsClient.NotifyRunningApps,
/// each player with the copy's owner). It is NOT known to send it again by itself after a sign-in - the protocol has an
/// explicit "send_running_apps" switch on GetFamilyGroup for asking - so after every sign-in the picture is asked for,
/// and until it comes the old one is kept and marked as from before the reconnect.
/// </summary>
public sealed partial class Library {
	/// <summary>Where a family game stands for this account right now.</summary>
	public enum CopyState {
		/// <summary>Not a family game: the account's own copy (or not in the library). Never a lender change.</summary>
		NotFamily,

		/// <summary>No copy of it is in use - nothing to wait for.</summary>
		Free,

		/// <summary>A copy is in use, and this account is set to borrow another member's free one.</summary>
		Borrowing,

		/// <summary>A copy is in use and another is free: switching to that one would let it play.</summary>
		Borrow,

		/// <summary>Asking Steam to lend another member's copy right now.</summary>
		Switching,

		/// <summary>Every copy it could use is in use.</summary>
		InUse,

		/// <summary>Nobody is on it, but somebody stopped too recently (the wait after a family member stops).</summary>
		JustStopped,

		/// <summary>Somebody was on it before the last sign-in, and Steam hasn't said since.</summary>
		Unconfirmed,

		/// <summary>A copy is in use and Steam turned down the switch to a free one, a little while ago.</summary>
		CantSwitch
	}

	/// <summary>One family game's standing: whose copy is in use, whose would be borrowed, when it's back.</summary>
	public readonly record struct FamilyCopy(CopyState State, ulong Lender = 0, ulong InUseBy = 0, DateTime? BackAt = null) {
		/// <summary>May be played now.</summary>
		public bool Playable => State is CopyState.NotFamily or CopyState.Free or CopyState.Borrowing;
	}

	private IFamilySteam? _family;

	/// <summary>Steam's family calls - the account's own connection, or a fake in the tests.</summary>
	internal IFamilySteam Family {
		get => _family ??= new SteamFamilyCalls(bot);
		set => _family = value;
	}

	/// <summary>The account's Steam Family group (0: none, or not known yet).</summary>
	public ulong FamilyGroupId { get; internal set; }

	private readonly Lock _familyGate = new();

	// Every member who owns a copy of each shared game, from GetSharedLibraryApps' owner_steamids.
	private Dictionary<uint, ulong[]> _copyOwners = [];

	// Shared games somebody else in the family is playing (any copy) - the whole picture, replaced on every push.
	private HashSet<uint> _familyBusy = [];

	// Whose copies of each are in use. 0 is a copy Steam didn't say the owner of: every copy counts as in use then.
	private Dictionary<uint, HashSet<ulong>> _busyCopies = [];

	// When each copy was put down - the wait after a family member stops, per copy. Owner 0: a copy we couldn't tell.
	private readonly Dictionary<(uint App, ulong Owner), DateTime> _freeSince = [];

	// The picture is from before the last sign-in and Steam hasn't sent a fresh one since; and when it went out of sight.
	private bool _pictureStale;
	private DateTime? _signedOutAt;
	private int _signIn;

	// The member this account borrows each game from, as Steam has it (set here, or read from Steam).
	private readonly Dictionary<uint, ulong> _lenders = [];
	private readonly HashSet<uint> _switching = [];
	private readonly Dictionary<uint, DateTime> _cantSwitchUntil = [];

	/// <summary>The last lender change started - the tests wait on it.</summary>
	internal Task? LastBorrow { get; private set; }

	/// <summary>The last request for a fresh picture after a sign-in - the tests wait on it.</summary>
	internal Task? LastPictureAsk { get; private set; }

	/// <summary>How long a copy stays left alone after its player stops ("Wait after a family member stops"), in minutes.</summary>
	private TimeSpan Grace => TimeSpan.FromMinutes(Math.Clamp(bot.Cfg.FamilyWaitMinutes, 0, 240));

	/// <summary>How long a turned-down lender change is left before trying again.</summary>
	private static readonly TimeSpan RetrySwitchAfter = TimeSpan.FromMinutes(30);

	/// <summary>
	/// A family member is playing this shared game (on every copy this account could use), or stopped too recently - so
	/// it is not ours to touch. The account's own games are never a family question. Never asks Steam for anything;
	/// <see cref="FamilyMayPlay"/> is the one that may switch to a free copy.
	/// </summary>
	/// <remarks>
	/// Steam lends a shared game one copy at a time and the OWNER always wins: start a game they then launch and the
	/// account is thrown out of it mid-session. The wait after they stop is the polite part - somebody who just quit is
	/// quite likely to start it again.
	/// </remarks>
	public bool FamilyIsPlaying(uint app) => !CopyFor(app).Playable;

	/// <summary>
	/// <see cref="FamilyIsPlaying"/> the other way round, for a caller about to PLAY the game: when the copy in use has a
	/// free twin and "Borrow another family member's copy" is on, it starts the switch to that copy, and the game is
	/// playable once Steam has made it (the next pass picks it up).
	/// </summary>
	public bool FamilyMayPlay(uint app) {
		FamilyCopy copy = CopyFor(app);

		if (copy.State == CopyState.Borrow) {
			StartBorrow(app, copy.Lender, copy.InUseBy);
		}

		return copy.Playable;
	}

	/// <summary>Where a family game stands right now. Reads only.</summary>
	public FamilyCopy CopyFor(uint app) {
		if (Find(app) is not { Shared: true } entry) {
			return new FamilyCopy(CopyState.NotFamily);
		}

		DateTime now = DateTime.UtcNow;
		TimeSpan grace = Grace;

		lock (_familyGate) {
			ulong[] owners = _copyOwners.TryGetValue(app, out ulong[]? known) && (known.Length > 0) ? known : [entry.SharedFrom];
			HashSet<ulong>? busy = _busyCopies.GetValueOrDefault(app);
			bool anyBusy = _familyBusy.Contains(app);

			// A copy Steam didn't say the owner of, or one of somebody this account doesn't know as an owner: then which
			// copy is in use can't be told, and every copy counts as in use.
			bool everyCopy = anyBusy && ((busy == null) || (busy.Count == 0) || busy.Any(o => (o == 0) || !owners.Contains(o)));
			ulong inUse = busy?.FirstOrDefault(static o => o != 0) ?? 0;

			bool Busy(ulong owner) => everyCopy || (busy?.Contains(owner) == true);

			DateTime? Back(ulong owner) {
				DateTime? latest = null;

				foreach (ulong key in (ulong[]) [owner, 0]) {
					if (_freeSince.TryGetValue((app, key), out DateTime at) && ((latest == null) || (at > latest))) {
						latest = at;
					}
				}

				return (latest is { } stopped) && (stopped + grace > now) ? stopped + grace : null;
			}

			List<ulong> taken = [.. owners.Where(o => Busy(o) || (Back(o) != null))];

			if (taken.Count == 0) {
				return new FamilyCopy(CopyState.Free);
			}

			List<ulong> free = [.. owners.Except(taken)];

			// Never on a picture from before the reconnect: the copy that looks free could be the one in use by now.
			if (_pictureStale && anyBusy) {
				return new FamilyCopy(CopyState.Unconfirmed, InUseBy: inUse);
			}

			if ((free.Count == 0) || !bot.Cfg.BorrowFamilyCopy) {
				return owners.Any(Busy)
					? new FamilyCopy(CopyState.InUse, InUseBy: inUse)
					: new FamilyCopy(CopyState.JustStopped, BackAt: taken.Select(Back).Where(static t => t != null).Min());
			}

			if (_lenders.TryGetValue(app, out ulong lender) && free.Contains(lender)) {
				return new FamilyCopy(CopyState.Borrowing, lender, inUse);
			}

			if (_switching.Contains(app)) {
				return new FamilyCopy(CopyState.Switching, free[0], inUse);
			}

			if (_cantSwitchUntil.TryGetValue(app, out DateTime until) && (until > now)) {
				return new FamilyCopy(CopyState.CantSwitch, InUseBy: inUse);
			}

			return new FamilyCopy(CopyState.Borrow, free[0], inUse);
		}
	}

	/// <summary>
	/// Why a family game is left out right now, in plain words - or default when it isn't. What the status, 'human',
	/// 'config' and the night's log lines say.
	/// </summary>
	public Said FamilyReason(uint app) {
		FamilyCopy copy = CopyFor(app);
		string name = Find(app)?.Name ?? GameNames.Of(app);
		int wait = (int) Grace.TotalMinutes;

		return copy.State switch {
			CopyState.InUse => wait > 0
				? new Said("{0} left out - someone in your Steam Family is playing it (back {1} min after they stop)", name, wait)
				: new Said("{0} left out - someone in your Steam Family is playing it", name),
			CopyState.JustStopped => new Said("{0} left out - someone in your Steam Family just stopped playing it (back at {1})", name,
				(copy.BackAt ?? DateTime.UtcNow).ToLocalTime().ToString("HH:mm")),
			CopyState.Unconfirmed => new Said("{0} left out - someone in your Steam Family was playing it before the reconnect; asking Steam again", name),
			CopyState.Borrow or CopyState.Switching => new Said("{0}: its copy is in use - switching to {1}'s free copy", name, MemberName(copy.Lender, false)),
			CopyState.CantSwitch => new Said("{0} left out - its copy is in use and Steam wouldn't lend another family member's copy (tries again in a while)", name),
			_ => default
		};
	}

	/// <summary>A member's Steam name, or "a family member" / "another family member" when it isn't known.</summary>
	private object MemberName(ulong id, bool another) =>
		(id != 0) && (Family.NameOf(id) is { } name) ? name : another ? new Said("another family member") : new Said("a family member");

	/// <summary>
	/// Make another member's free copy the one this account borrows. Only the preferred lender for this one game is
	/// changed, only when a copy is in use, and only to a copy nobody is using (or has just put down).
	/// </summary>
	private void StartBorrow(uint app, ulong lender, ulong inUseBy) {
		lock (_familyGate) {
			if ((lender == 0) || !_switching.Add(app)) {
				return;
			}
		}

		LastBorrow = Task.Run(async () => {
			string name = Find(app)?.Name ?? GameNames.Of(app);
			using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

			try {
				ulong family = FamilyGroupId != 0 ? FamilyGroupId : (await Family.FamilyGroupIdAsync(cts.Token).ConfigureAwait(false)) ?? 0;

				if (family == 0) {
					NoSwitch(app, new Said("{0}: its copy is in use and Steam didn't say which family it's in - left out for now", name));

					return;
				}

				FamilyGroupId = family;

				// Only when needed: it may already be the one.
				Dictionary<uint, ulong>? current = await Family.PreferredLendersAsync(family, cts.Token).ConfigureAwait(false);

				if ((current != null) && current.TryGetValue(app, out ulong already) && (already == lender)) {
					lock (_familyGate) {
						_lenders[app] = lender;
					}

					Log.Info(new Said("{0}: {1}'s copy is in use - borrowing {2}'s free copy (already set that way)", name,
						MemberName(inUseBy, false), MemberName(lender, true)), bot.Name);

					return;
				}

				EResult result = await Family.SetPreferredLenderAsync(family, app, lender, cts.Token).ConfigureAwait(false);

				if (result != EResult.OK) {
					NoSwitch(app, new Said("{0}: its copy is in use and Steam wouldn't switch to another family member's copy ({1}) - left out for now", name, result));

					return;
				}

				lock (_familyGate) {
					_lenders[app] = lender;
				}

				Log.Info(new Said("{0}: {1}'s copy is in use - borrowing {2}'s free copy instead", name,
					MemberName(inUseBy, false), MemberName(lender, true)), bot.Name);
			} catch (Exception e) {
				NoSwitch(app, new Said("{0}: couldn't ask Steam to lend another family member's copy ({1}) - left out for now", name, Log.Describe(e)));
			} finally {
				lock (_familyGate) {
					_switching.Remove(app);
				}
			}
		});
	}

	private void NoSwitch(uint app, Said why) {
		lock (_familyGate) {
			_cantSwitchUntil[app] = DateTime.UtcNow + RetrySwitchAfter;
		}

		Log.Info(why, bot.Name);
	}

	/// <summary>
	/// Steam's live "who in the family is running what" push. It carries the WHOLE current picture each time, so it is
	/// replaced rather than added to. This account appears in it too, playing whatever it plays - filtered out, or our
	/// own session would read as somebody else's.
	/// </summary>
	internal void NoteFamilyRunning(IEnumerable<(uint App, IEnumerable<(ulong Member, ulong Owner)> Players)> running) {
		Dictionary<uint, HashSet<ulong>> busy = [];

		foreach ((uint app, IEnumerable<(ulong Member, ulong Owner)> players) in running) {
			foreach ((ulong member, ulong owner) in players) {
				if ((member != 0) && (member != bot.SteamId)) {
					(busy.TryGetValue(app, out HashSet<ulong>? copies) ? copies : busy[app] = []).Add(owner);
				}
			}
		}

		lock (_familyGate) {
			DateTime now = DateTime.UtcNow;

			// Stopped while this account was signed out: the last moment it was known to be in use is when it went out of
			// sight, so the wait after they stop counts from then - not from now, which kept a game out for the whole wait
			// again after every reconnect.
			DateTime stoppedAt = _pictureStale && (_signedOutAt is { } gone) ? gone : now;

			foreach (uint app in _familyBusy) {
				HashSet<ulong> before = _busyCopies.TryGetValue(app, out HashSet<ulong>? was) && (was.Count > 0) ? was : [0];
				HashSet<ulong> after = busy.GetValueOrDefault(app) ?? [];

				foreach (ulong owner in before.Where(o => !after.Contains(o))) {
					_freeSince[(app, owner)] = stoppedAt;
				}
			}

			foreach ((uint app, HashSet<ulong> owners) in busy) {
				foreach (ulong owner in owners) {
					_freeSince.Remove((app, owner));
				}
			}

			_busyCopies = busy;
			_familyBusy = [.. busy.Keys];
			_pictureStale = false;
			_signedOutAt = null;
		}
	}

	/// <summary>The account went offline: the picture it has stops being current.</summary>
	internal void NoteSignedOut() {
		lock (_familyGate) {
			if (!_pictureStale) {
				_pictureStale = true;
				_signedOutAt = DateTime.UtcNow;
			}

			_signIn++;
		}
	}

	/// <summary>Signed in: ask Steam for the current picture rather than wait for the next change.</summary>
	internal void NoteSignedIn() {
		int mine;

		lock (_familyGate) {
			mine = ++_signIn;
		}

		if (!bot.Cfg.IncludeFamilyLibrary) {
			return;   // no family game is played here - nothing to ask about
		}

		LastPictureAsk = Task.Run(() => AskPictureAsync(mine));
	}

	/// <summary>The waits between asks for a fresh picture: soon after the sign-in, then now and then while the old one stands.</summary>
	internal static readonly TimeSpan[] AskAgainAfter = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15)];

	/// <summary>How long to wait for the push after Steam took the request - and the tests' way to make it short.</summary>
	internal TimeSpan PushWait { get; set; } = TimeSpan.FromMinutes(2);

	private bool StillThisSignIn(int mine) {
		lock (_familyGate) {
			return mine == _signIn;
		}
	}

	private async Task AskPictureAsync(int mine) {
		for (int attempt = 0; StillThisSignIn(mine); attempt++) {
			using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));

			try {
				ulong? family = FamilyGroupId != 0 ? FamilyGroupId : await Family.FamilyGroupIdAsync(cts.Token).ConfigureAwait(false);

				if (family == 0) {
					// Not in a family (any more): nothing anybody in it plays is in the way.
					lock (_familyGate) {
						_familyBusy = [];
						_busyCopies = [];
						_pictureStale = false;
						_signedOutAt = null;
					}

					return;
				}

				if (family is { } id) {
					FamilyGroupId = id;

					if (await Family.AskRunningAppsAsync(id, cts.Token).ConfigureAwait(false)) {
						DateTime until = DateTime.UtcNow + PushWait;

						while ((DateTime.UtcNow < until) && StillThisSignIn(mine)) {
							lock (_familyGate) {
								if (!_pictureStale) {
									Log.Debug(new Said("family: Steam sent who's playing what ({0} shared game(s) in use)", _familyBusy.Count), bot.Name);

									return;
								}
							}

							await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, Math.Max(10, PushWait.TotalMilliseconds / 20)))).ConfigureAwait(false);
						}
					}
				}
			} catch (Exception e) {
				Log.DebugOnChange($"family:{bot.Name}", $"family: couldn't ask Steam who's playing what: {Log.Describe(e)}", bot.Name);
			}

			lock (_familyGate) {
				// Nothing was in use before the sign-in: there's no old "in use" to keep anything out, so nothing to chase.
				if (!_pictureStale || (_familyBusy.Count == 0)) {
					return;
				}
			}

			if (attempt == 0) {
				Log.Debug("family: Steam hasn't said who's playing what since the sign-in - keeping the last picture and asking again", bot.Name);
			}

			await Task.Delay(AskAgainAfter[Math.Min(attempt, AskAgainAfter.Length - 1)] * (PushWait.TotalMinutes >= 1 ? 1 : PushWait.TotalMinutes)).ConfigureAwait(false);
		}
	}
}
