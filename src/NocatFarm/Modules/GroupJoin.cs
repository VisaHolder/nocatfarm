using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Joins the nocat.farm Steam group once per session - the way ArchiSteamFarm joins its own. Opt out per account
/// with <c>JoinGroup = false</c>. Joining a group is a community web POST (not a CM message), so it waits for the
/// web session, and it is entirely best-effort: a failed join is logged at debug and blocks nothing.
/// </summary>
public sealed class GroupJoin(Bot bot) : BotModule(bot) {
	private const string GroupVanity = "nocatfarm";

	public override string Name => "group";

	protected override async Task RunAsync(CancellationToken ct) {
		if (!Bot.Cfg.JoinGroup) {
			return;
		}

		// Wait until the web session is actually usable - the join is a community POST, and it needs the cookies
		// the web layer synthesises after login.
		while (!Bot.IsOnline || !Bot.Web.Ready) {
			if (!await Sleep(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) {
				return;
			}
		}

		try {
			Uri url = new(WebSession.Community, $"/groups/{GroupVanity}");

			await Bot.Web.PostAsync(url,
				new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = "join" },
				url, ct).ConfigureAwait(false);

			Log.Debug("joined the nocat.farm Steam group", Bot.Name);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			Log.Debug($"couldn't join the group ({e.Message}) - not important", Bot.Name);
		}
	}
}
