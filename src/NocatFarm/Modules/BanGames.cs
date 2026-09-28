using System.Text.RegularExpressions;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Which games an account is banned in. Steam's public ban numbers only count bans; the list of games is shown only
/// to the account itself, on Steam's help site - https://help.steampowered.com/en/wizard/VacBans - read here with the
/// account's own web session.
/// </summary>
/// <remarks>
/// The page's shape, as read by rawensoft/steam_web (Models/VacGameBansData.cs) and chr233/ASFEnhance
/// (Account/WebRequest.cs GetMyBans): a clean account shows a "no_vac_bans_header"; otherwise each
/// "help_issue_details" block (VAC bans, game bans) holds one "refund_info_box" per game, whose link carries the game
/// as ?appid= (or ?steamAppId=). BattlEye bans link to BattlEye instead and have no Steam game to leave out.
/// </remarks>
public static partial class BanGames {
	private static readonly Uri Page = new(WebSession.Help, "/en/wizard/VacBans");

	/// <summary>The banned games' app IDs, an empty list when there are none, or null when the page couldn't be read.</summary>
	public static async Task<List<uint>?> ReadAsync(Bot bot, CancellationToken ct) {
		string? html = await bot.Web.GetAsync(Page, ct).ConfigureAwait(false);

		return html == null ? null : Parse(html);
	}

	/// <summary>The games on the page; empty for a clean account; null when it isn't the bans page at all (a login page).</summary>
	public static List<uint>? Parse(string html) {
		if (html.Contains("no_vac_bans_header", StringComparison.Ordinal)) {
			return [];
		}

		if (!html.Contains("help_issue_details", StringComparison.Ordinal) && !html.Contains("refund_info_box", StringComparison.Ordinal)) {
			return null;
		}

		List<uint> games = [];
		string[] boxes = html.Split("refund_info_box", StringSplitOptions.None);

		// Everything before the first box is the page around them.
		foreach (string box in boxes.Skip(1)) {
			if (AppIdRegex().Match(box) is { Success: true } m && uint.TryParse(m.Groups[1].Value, out uint app) && (app != 0) && !games.Contains(app)) {
				games.Add(app);
			}
		}

		return games;
	}

	[GeneratedRegex(@"[?&;](?:appid|steamAppId)=(\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex AppIdRegex();
}
