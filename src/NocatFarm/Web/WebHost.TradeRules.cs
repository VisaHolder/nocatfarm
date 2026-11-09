using System.Text.Json;
using System.Text.Json.Nodes;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>Custom trade rules: the dashboard's own page, and the preview that explains a verdict without acting on it.</summary>
public sealed partial class WebHost {
	private void MapTradeRules(WebApplication app) {
		app.MapGet("/api/trade-rules", (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (_mgr.Get(name) is not { } bot) {
				return Results.Json(new { ok = false, error = Loc.T("No such account.") }, statusCode: 404);
			}

			TradeRuleSet set = TradeRuleCodec.Parse(bot.Cfg.TradeRulesJson);

			return Results.Json(new {
				ok = true,
				enabled = bot.Cfg.TradeRulesEnabled,
				confirm = bot.Cfg.TradeRulesConfirm,
				rules = JsonNode.Parse(TradeRuleCodec.Write(set.Rules)),
				problems = set.Problems,
				presets = TradePresets.All().Select(static p => new {
					id = p.Id,
					label = p.Label,
					rule = JsonNode.Parse(TradeRuleCodec.Write([p.Rule]))!["rules"]![0]
				}).ToList()
			});
		});

		app.MapPut("/api/trade-rules", async (HttpContext ctx, string name) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (_mgr.Get(name) is not { } bot) {
				return Results.Json(new { ok = false, error = Loc.T("No such account.") }, statusCode: 404);
			}

			if (await ReadSentAsync(ctx).ConfigureAwait(false) is not { } sent) {
				return Results.Json(new { ok = false, problems = new[] { "no JSON body" } }, statusCode: 400);
			}

			bool enabled = sent["enabled"] is JsonNode e && (e.GetValueKind() == JsonValueKind.True);
			bool confirm = sent["confirm"] is JsonNode c && (c.GetValueKind() == JsonValueKind.True);

			// Strict: the same codec that decides an offer decides what may be saved - a rule Steam's bot would ignore is
			// refused here, with the reason, rather than saved and silently skipped.
			JsonObject whole = new() {
				["version"] = TradeRuleCodec.Version,
				["rules"] = sent["rules"]?.DeepClone()
			};

			TradeRuleSet set = TradeRuleCodec.Parse(whole.ToJsonString());

			if (set.Problems.Count > 0) {
				return Results.Json(new { ok = false, problems = set.Problems }, statusCode: 400);
			}

			lock (bot.CfgGate) {
				bot.Cfg.TradeRulesEnabled = enabled;
				bot.Cfg.TradeRulesConfirm = confirm;
				bot.Cfg.TradeRulesJson = set.Rules.Count == 0 ? "" : TradeRuleCodec.Write(set.Rules);

				if (!ConfigStore.SaveBot(bot.Name, bot.Cfg)) {
					return Results.Json(new { ok = false, problems = new[] { Loc.T("The settings couldn't be saved.") } }, statusCode: 500);
				}
			}

			Log.Info(new Said("trade rules saved from the dashboard: {0} rule(s), {1}", set.Rules.Count, enabled ? "on" : "off"), bot.Name);

			return Results.Json(new { ok = true, saved = true });
		});

		app.MapPost("/api/trade-rules/preview", async (HttpContext ctx, string name, CancellationToken ct) => {
			if (!Authorised(ctx)) {
				return Unauthorised();
			}

			if (_mgr.Get(name) is not { } bot) {
				return Results.Json(new { ok = false, error = Loc.T("No such account.") }, statusCode: 404);
			}

			JsonObject? sent = await ReadSentAsync(ctx).ConfigureAwait(false);
			ulong? only = (sent?["offerId"] as JsonValue) is { } id && id.TryGetValue(out ulong offerId) ? offerId : null;
			bool fetch = (sent?["fetch"] as JsonValue) is { } f && f.TryGetValue(out bool wanted) && wanted;
			TradeValueLookups lookups = fetch ? TradeValueLookups.Stale : TradeValueLookups.Cached;

			if (await TradeOffers.ActiveAsync(bot, received: true, sent: false, ct).ConfigureAwait(false) is not { } live) {
				return Results.Json(new { ok = false, error = Loc.T("Steam didn't say what it has for this account.") }, statusCode: 502);
			}

			TradeRuleSet set = TradeRuleCodec.Parse(bot.Cfg.TradeRulesJson);
			IReadOnlyCollection<ulong> promised = await TradeOffers.PromisedAsync(bot, ct).ConfigureAwait(false) is { } p ? [.. p] : [];

			List<object> offers = [];

			foreach (TradeOffers.Offer offer in live.Where(o => !o.Ours && (o.State == TradeOffers.Active)).Where(o => only == null || (o.Id == only)).Take(only == null ? 10 : 1)) {
				TradeValues values = await TradeValues.ReadAsync(bot, offer, promised, ct, lookups).ConfigureAwait(false);
				GateResult gates = TradeGates.Check(bot, offer, values, promised);
				TradeDecision decision = await TradePolicy.DecideAsync(set, values, gates, ct).ConfigureAwait(false);

				offers.Add(new {
					id = offer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
					partner = offer.Partner.ToString(System.Globalization.CultureInfo.InvariantCulture),
					given = await SideAsync(values, values.Giving, ct).ConfigureAwait(false),
					received = await SideAsync(values, values.Receiving, ct).ConfigureAwait(false),
					verdict = decision.Verdict.ToString(),
					rule = decision.RuleId,
					why = decision.Why,
					gates = gates.Why,
					prices = values.RefreshWanted ? "missing" : "held"
				});
			}

			return Results.Json(new { ok = true, enabled = bot.Cfg.TradeRulesEnabled, problems = set.Problems, fetch, offers });
		});
	}

	/// <summary>Each item of one side as the preview shows it: what it is, and what the rules would weigh it at.</summary>
	private static async Task<List<object>> SideAsync(TradeValues values, IReadOnlyList<TradeOffers.Item> side, CancellationToken ct) {
		List<object> items = [];

		foreach (TradeOffers.Item item in side) {
			ValuedItem gems = await values.GemsAsync(item, ct).ConfigureAwait(false);
			ValuedItem wallet = await values.WalletAsync(item, giving: true, minSales: 3, maxAgeHours: 24, undercutPct: 0, looseGems: "sack", ct).ConfigureAwait(false);
			ValuedItem set = await values.SetAsync(item, ct).ConfigureAwait(false);

			items.Add(new {
				name = item.Name,
				kind = item.Kind.ToString(),
				amount = item.Amount,
				gems = gems.Gems,
				wallet = wallet.WalletCents,
				setSize = set.SetKnown ? set.SetSize : null,
				problem = gems.Problem ?? wallet.Problem ?? set.Problem
			});
		}

		return items;
	}
}
