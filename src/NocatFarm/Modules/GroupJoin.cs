using System.Text.RegularExpressions;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Joins Steam groups: every group in the global "Groups every account joins" list (the nocat.farm group to start
/// with - <c>JoinGroup</c> keeps one account out of it), and this account's own "Also join these
/// groups". A group is joined only when it's open - one that
/// needs approval or an invitation is left alone and the log says which.
///
/// Joining is a community web POST. Steam answers a join it didn't accept with the same 200 it answers a real one
/// with - it just serves an error page as the body - so a fire-and-forget POST loses accounts silently, which is
/// what happened to every account after the first. This reads the reply to tell a real join from an error page,
/// and retries a few times before giving up quietly. (The session-field-casing quirk that made the join fail in
/// the first place is handled one level down, in <see cref="WebSession"/>.)
///
/// Nobody watches an account join a group, so it isn't held for a human-mode account's day - only kept off the
/// moment it signs in, and several joins are spaced out rather than done back to back.
/// </summary>
public sealed partial class GroupJoin(Bot bot) : BotModule(bot) {
	/// <summary>How a group lets people in, read off its page.</summary>
	public enum Access { Open, NeedsApproval, InviteOnly, Unknown }

	/// <summary>What happened to one group, for the log and the joingroup command.</summary>
	public enum Outcome { Joined, AlreadyIn, NeedsApproval, InviteOnly, NotFound, Failed }

	/// <summary>Groups finished with this session - joined, already in, or not joinable. Retried after a restart.</summary>
	private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, int> _tries = new(StringComparer.OrdinalIgnoreCase);
	private HumanGate? _gate;

	/// <summary>
	/// When each newly wanted group may be tried. Each account rolls its own wait, so several accounts told to join
	/// the same group don't all turn up in its member list in the same minute - that's a giveaway on its own.
	/// </summary>
	private readonly Dictionary<string, DateTime> _due = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>No next join before this - one group at a time, with a real gap after each.</summary>
	private DateTime _nextJoin = DateTime.MinValue;

	/// <summary>The wait before a group is first tried: a human-mode account takes a while to get round to it.</summary>
	private TimeSpan FirstWait() => Bot.Cfg.LegitMode ? Rng.HumanMinutes(10, 90) : Rng.Minutes(1, 15);

	/// <summary>The gap after joining one group before the next.</summary>
	private TimeSpan Gap() => Bot.Cfg.LegitMode ? Rng.HumanMinutes(20, 120) : Rng.Minutes(3, 10);

	public override string Name => "group";

	protected override async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			// Re-read every pass, so a group added in Settings is picked up without a restart.
			List<string> pending = [.. Wanted(Bot).Where(g => !_done.Contains(g))];

			if ((pending.Count == 0) || !Bot.IsOnline || !Bot.Web.Ready) {
				if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			// A group joined shows on the profile and in the group's member list with the time, so a human-mode
			// account joins in its own day - not at 4am while its friends list says it's asleep.
			_gate ??= HumanGate.OwnDay(Bot);

			if (!_gate.Open) {
				if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			foreach (string group in pending) {
				_due.TryAdd(group, DateTime.UtcNow + FirstWait());
			}

			// One group per pass, the first that's due - and none at all until the gap after the last join is up.
			string? next = pending.FirstOrDefault(g => DateTime.UtcNow >= _due[g]);

			if ((next == null) || (DateTime.UtcNow < _nextJoin)) {
				if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
					return;
				}

				continue;
			}

			string? mine = await MyGroupsAsync(ct).ConfigureAwait(false);

			foreach (string group in (string[]) [next]) {
				(Outcome outcome, string name) = await JoinAsync(group, mine, ct).ConfigureAwait(false);

				switch (outcome) {
					case Outcome.Joined:
						Log.Info(new Said("joined the Steam group {0}", name), Bot.Name);
						_done.Add(group);

						break;
					case Outcome.AlreadyIn:
						_done.Add(group);

						break;
					case Outcome.NeedsApproval:
						Log.Info(new Said("didn't join {0} - needs admin approval", name), Bot.Name);
						_done.Add(group);

						break;
					case Outcome.InviteOnly:
						Log.Info(new Said("didn't join {0} - it's invite only", name), Bot.Name);
						_done.Add(group);

						break;
					case Outcome.NotFound:
						Log.Info(new Said("no Steam group at \"{0}\" - check the link", group), Bot.Name);
						_done.Add(group);

						break;
					default:
						int tries = _tries[group] = _tries.GetValueOrDefault(group) + 1;

						if (tries >= 4) {
							Log.Info(new Said("couldn't join {0} - trying again next start", name), Bot.Name);
							_done.Add(group);
						}

						break;
				}

				if (outcome == Outcome.Joined) {
					_nextJoin = DateTime.UtcNow + Gap();
				} else if (outcome == Outcome.Failed) {
					_due[group] = DateTime.UtcNow + Rng.Minutes(5, 20);   // Steam had a moment - look again later
				}
			}

			if (!await Sleep(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)) {
				return;
			}
		}
	}

	/// <summary>Every group this account should be in, as normalised paths ("groups/name" or "gid/1035...").</summary>
	public static List<string> Wanted(Bot bot) {
		List<string> all = [];

		// The shared list (the nocat.farm group unless it's been changed) unless this account is kept out of
		// it, then this account's own extras.
		IEnumerable<string> shared = bot.Cfg.JoinGroup ? Split(Live.Global.GroupsToJoin) : [];

		foreach (string raw in shared.Concat(Split(bot.Cfg.ExtraGroupsToJoin))) {
			if (Normalise(raw) is { } path) {
				all.Add(path);
			}
		}

		return [.. all.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	private static IEnumerable<string> Split(string? list) =>
		(list ?? "").Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// A group however it was typed - a full link, a /gid/ link, a bare 18-digit group ID, or just its short name -
	/// as the path its page lives at. Null for something that can't be a group.
	/// </summary>
	public static string? Normalise(string raw) {
		string s = raw.Trim().TrimEnd('/');

		if (s.Length == 0) {
			return null;
		}

		Match link = GroupLink().Match(s);

		if (link.Success) {
			return $"{link.Groups[1].Value.ToLowerInvariant()}/{link.Groups[2].Value}";
		}

		if (s.Length == 18 && s.StartsWith("1035", StringComparison.Ordinal) && s.All(char.IsAsciiDigit)) {
			return "gid/" + s;
		}

		return ShortName().IsMatch(s) ? "groups/" + s : null;
	}

	/// <summary>
	/// Try one group now: already in, or open and joined, or why not. Used by the loop and by the joingroup command
	/// (which passes no profile, so it reads it itself).
	/// </summary>
	public async Task<(Outcome Outcome, string Name)> JoinAsync(string path, string? mine, CancellationToken ct) {
		string xml = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/{path}/memberslistxml/?xml=1&p=999"), ct).ConfigureAwait(false) ?? "";
		Match id = GroupId().Match(xml);
		string name = GroupName().Match(xml) is { Success: true } n ? n.Groups[1].Value : path.Split('/')[1];

		if (!id.Success) {
			// An answer with no group in it is no group; no answer at all is Steam having a moment.
			return (xml.Length > 0 ? Outcome.NotFound : Outcome.Failed, name);
		}

		mine ??= await MyGroupsAsync(ct).ConfigureAwait(false);

		if ((mine != null) && mine.Contains(id.Groups[1].Value, StringComparison.Ordinal)) {
			return (Outcome.AlreadyIn, name);
		}

		Uri url = new(WebSession.Community, $"/{path}");
		string? page = await Bot.Web.GetAsync(new Uri(url + "?l=english"), ct).ConfigureAwait(false);

		if (page == null) {
			return (Outcome.Failed, name);
		}

		switch (AccessOf(page)) {
			case Access.NeedsApproval:
				return (Outcome.NeedsApproval, name);
			case Access.InviteOnly:
				return (Outcome.InviteOnly, name);
			case Access.Unknown:
				// No join button at all on a group this account can see: it's already a member (a private profile
				// hides its groups from the check above).
				return (Outcome.AlreadyIn, name);
		}

		string? body = await Bot.Web.PostAsync(url, new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = "join" }, url, ct).ConfigureAwait(false);

		// A real join returns the group page; a refused one returns Steam's error page (same 200).
		return body != null
			&& !body.Contains("Missing or invalid form session key", StringComparison.OrdinalIgnoreCase)
			&& !body.Contains(":: Error", StringComparison.OrdinalIgnoreCase)
				? (Outcome.Joined, name)
				: (Outcome.Failed, name);
	}

	/// <summary>
	/// How a group page says people get in: "Join Group", "Request To Join", or "Membership by invitation only".
	/// Read from the join box only, so a word elsewhere on the page (a post, the description) can't fool it.
	/// </summary>
	public static Access AccessOf(string page) {
		int at = page.IndexOf("grouppage_join_area", StringComparison.Ordinal);

		if (at < 0) {
			return Access.Unknown;
		}

		string box = page.Substring(at, Math.Min(600, page.Length - at));

		return box.Contains("icon_inviteonly", StringComparison.Ordinal) || box.Contains("invitation only", StringComparison.OrdinalIgnoreCase) ? Access.InviteOnly
			: box.Contains("Request To Join", StringComparison.OrdinalIgnoreCase) ? Access.NeedsApproval
			: box.Contains("Join Group", StringComparison.OrdinalIgnoreCase) ? Access.Open
			: Access.Unknown;
	}

	/// <summary>
	/// The groups this account is in, from its own profile XML (every group's 64-bit ID). Null when the profile
	/// couldn't be read - "unknown, go ahead and look", never "not a member".
	/// </summary>
	private async Task<string?> MyGroupsAsync(CancellationToken ct) {
		string? xml = await Bot.Web.GetAsync(new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/?xml=1"), ct).ConfigureAwait(false);

		return string.IsNullOrEmpty(xml) ? null : xml;
	}

	[GeneratedRegex(@"steamcommunity\.com/(groups|gid)/([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
	private static partial Regex GroupLink();

	[GeneratedRegex(@"^[A-Za-z0-9_\-]{2,64}$")]
	private static partial Regex ShortName();

	[GeneratedRegex(@"<groupID64>(\d+)</groupID64>")]
	private static partial Regex GroupId();

	[GeneratedRegex(@"<groupName><!\[CDATA\[(.*?)\]\]></groupName>")]
	private static partial Regex GroupName();
}
