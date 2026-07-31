using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Joins the nocat.farm Steam group once per session - the way ArchiSteamFarm joins its own. Opt out per account
/// with <c>JoinGroup = false</c>.
///
/// Joining is a community web POST. Steam answers a join it didn't accept with the same 200 it answers a real one
/// with - it just serves an error page as the body - so a fire-and-forget POST loses accounts silently, which is
/// what happened to every account after the first. This reads the reply to tell a real join from an error page,
/// and retries a few times before giving up quietly. (The session-field-casing quirk that made the join fail in
/// the first place is handled one level down, in <see cref="WebSession"/>.)
/// </summary>
public sealed class GroupJoin(Bot bot) : BotModule(bot) {
	private const string GroupVanity = "nocatfarm";

	/// <summary>steamcommunity.com/groups/nocatfarm, as it appears in a profile's group list.</summary>
	private const string GroupId64 = "103582791475758189";

	public override string Name => "group";

	protected override async Task RunAsync(CancellationToken ct) {
		if (!Bot.Cfg.JoinGroup) {
			return;
		}

		// The join is a community POST and needs the cookies the web layer synthesises after login.
		while (!Bot.IsOnline || !Bot.Web.Ready) {
			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}

		// Already in from an earlier run? Then there is nothing to do, and nothing worth saying.
		if (await IsMemberAsync(ct).ConfigureAwait(false) == true) {
			return;
		}

		Uri url = new(WebSession.Community, $"/groups/{GroupVanity}");
		Dictionary<string, string> form = new(StringComparer.Ordinal) { ["action"] = "join" };

		for (int attempt = 1; attempt <= 4; attempt++) {
			string? body = await Bot.Web.PostAsync(url, form, url, ct).ConfigureAwait(false);

			// A real join returns the group page; a refused one returns Steam's error page (same 200). Anything
			// that isn't the error page - and isn't a dropped request - is the account now in the group.
			if (body != null
				&& !body.Contains("Missing or invalid form session key", StringComparison.OrdinalIgnoreCase)
				&& !body.Contains(":: Error", StringComparison.OrdinalIgnoreCase)) {
				Log.Info("joined the nocat.farm Steam group", Bot.Name);

				return;
			}

			if (!await Sleep(TimeSpan.FromSeconds(20 * attempt), ct).ConfigureAwait(false)) {
				return;
			}
		}

		Log.Info("couldn't join the nocat.farm group this session - will try again next start", Bot.Name);
	}

	/// <summary>
	/// Whether this account is already in the group, read from its own public profile XML (which lists every
	/// group it belongs to). Null when the profile couldn't be read - which the caller treats as "unknown, go
	/// ahead and try", never as "not a member".
	/// </summary>
	private async Task<bool?> IsMemberAsync(CancellationToken ct) {
		string? xml = await Bot.Web.GetAsync(
			new Uri(WebSession.Community, $"/profiles/{Bot.SteamId}/?xml=1"), ct).ConfigureAwait(false);

		return string.IsNullOrEmpty(xml) ? null : xml.Contains(GroupId64, StringComparison.Ordinal);
	}
}
