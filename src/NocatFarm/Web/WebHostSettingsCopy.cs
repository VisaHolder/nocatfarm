using System.Text.Json.Nodes;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>
/// 1.8.2: "Copy settings from..." on an account's Settings page - what it would change, then the copy itself
/// (<see cref="SettingsCopy"/>). Add account's own "Copy settings from" goes through /api/bots.
/// </summary>
public sealed partial class WebHost {
	/// <summary>
	/// The checks a dashboard save of an account makes on what it would write (<see cref="SaveBotFromPage"/>): a game list or
	/// text the console would refuse refuses it, a number out of range goes back to its default, and each shortest/longest
	/// pair is put in order. Changes only <paramref name="body"/>.
	/// </summary>
	internal static (string? Error, List<string> Adjusted) CheckLikeASave(BotConfig body, BotConfig current) {
		if (Invalid(body, current, Settings.Bot) is { } error) {
			return (error, []);
		}

		List<string> adjusted = Clamp(body, Settings.Bot);
		adjusted.AddRange(Settings.FixRanges(body, [.. Settings.Bot
			.Where(d => !Equals(Settings.Show(body, d), Settings.Show(current, d)))
			.Select(static d => d.Name)]).Select(static s => s.ToString()));

		return (null, adjusted);
	}

	private void MapSettingsCopy(WebApplication app) {
		// What copying another account's settings onto this one would change - nothing is changed.
		app.MapGet("/api/bots/{name}/copy-settings/preview", (HttpContext ctx, string name, string? from) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if ((_mgr.Get(name) is not { } to) || (_mgr.Get(from ?? "") is not { } source)) {
				return Results.Json(new { ok = false, error = Loc.T("No such account.") }, statusCode: 404);
			}

			if (source == to) {
				return Results.Json(new { ok = false, error = Loc.T("Pick two different accounts.") }, statusCode: 400);
			}

			BotConfig fromCfg, toCfg;

			lock (source.CfgGate) {
				fromCfg = Clone(source.Cfg);
			}

			lock (to.CfgGate) {
				toCfg = Clone(to.Cfg);
			}

			SettingsCopy.Plan plan = SettingsCopy.Build(fromCfg, toCfg, to.Name, to.SteamId);

			return plan.Error is { } error
				? Results.Json(new { ok = false, error }, statusCode: 400)
				: Results.Json(new { ok = true, data = Shape(source.Name, to.Name, plan) });
		});

		// The copy, after its preview.
		app.MapPost("/api/bots/{name}/copy-settings", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			JsonObject? sent = await ReadSentAsync(ctx).ConfigureAwait(false);

			if ((_mgr.Get(name) is not { } to) || (_mgr.Get(sent?["From"]?.ToString() ?? "") is not { } source)) {
				return Results.Json(new { ok = false, error = Loc.T("No such account.") }, statusCode: 404);
			}

			SettingsCopy.Done done = SettingsCopy.Copy(source, to);

			if (done.Plan.Error is { } error) {
				return Results.Json(new { ok = false, error = Loc.T(error) }, statusCode: 400);
			}

			return Results.Json(new { ok = true, saved = done.Saved, data = Shape(source.Name, to.Name, done.Plan) });
		});
	}

	/// <summary>A copy's plan as the page draws it: each change by its setting's name (the page has the labels, in its language).</summary>
	private static object Shape(string from, string to, SettingsCopy.Plan plan) => new {
		From = from,
		To = to,
		Changes = plan.Changes.Select(static c => new { c.Name, c.Before, c.After }),
		plan.LeftOff,
		TurnedOff = plan.TurnedOff.Where(s => !plan.LeftOff.Contains(s)),
		plan.Cleared,
		plan.Adjusted
	};
}
