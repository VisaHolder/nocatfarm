using System.Globalization;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Automatic "Make booster packs": gems into a pack for the game whose normal badge is closest to its next level
/// (<see cref="BoosterPick"/>), then that pack opened, so its cards go toward the badge and "Craft badges from card sets"
/// crafts the level once the set is complete.
/// </summary>
/// <remarks>
/// The packs it makes it opens itself, whatever "Open booster packs" says: a pack made to finish a badge and then left
/// sealed would be gems spent on nothing. Packs from anywhere else still follow "Open booster packs" - and so do its own
/// once it isn't automatic any more, once they're two days old, or once their game is never to be opened
/// (BoosterPick.Memory.Release). It never picks a game
/// on "Never open packs from these games", so it never makes a pack it would have to leave shut. A human-mode account
/// makes and opens them only in its own day, one at a time with human gaps; a robot makes every pack its gems allow.
/// </remarks>
public sealed partial class Boosters {
	private HumanGate? _dayGate;
	private DateTime _autoNext = DateTime.MinValue;
	private DateTime _openNext = DateTime.MinValue;
	private BoosterPick.Memory? _memory;
	private Said _autoStatus = new("");
	private bool _saidCraftingOff;

	/// <summary>The gate automatic keeps to: a human-mode account's own day (no effect on a robot).</summary>
	internal HumanGate DayGate => _dayGate ??= HumanGate.OwnDay(Bot);

	/// <summary>What automatic made and still has to open (loaded on first use).</summary>
	internal BoosterPick.Memory AutoMemory => _memory ??= BoosterPick.Memory.Load(Bot.Name);

	/// <summary>1.8.2: the game automatic is saving gems for at its last look (0: none) - see <see cref="BoosterPick.Busy(Core.Bot)"/>.</summary>
	internal uint SavingFor { get; private set; }

	private async Task AutoTickAsync(CancellationToken ct) {
		_dayGate ??= DayGate;

		if (!Bot.IsOnline || !Bot.Web.Ready || Bot.Paused) {
			return;
		}

		if (!_dayGate.Open) {
			_autoStatus = new Said("booster packs wait: {0}", _dayGate.Waiting());

			return;
		}

		try {
			// Its own packs first: the cards are what the gems were spent for.
			if ((AutoMemory.CountOwed() > 0) && (DateTime.UtcNow >= _openNext)) {
				await OpenOwnAsync(ct).ConfigureAwait(false);

				return;
			}

			if (DateTime.UtcNow >= _autoNext) {
				await MakeAutoAsync(ct).ConfigureAwait(false);
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception e) {
			Log.Debug(new Said("couldn't make booster packs: {0}", Log.Describe(e)), Bot.Name);
			_autoNext = Later(1, 2);
			_openNext = Later(1, 2);
		}
	}

	private async Task MakeAutoAsync(CancellationToken ct) {
		Page? page = await ReadAsync(Bot, ct).ConfigureAwait(false);

		if (page == null) {
			_autoNext = Later(1, 2);

			return;
		}

		// A pack Steam didn't answer about that the page shows wasn't made after all: the game can go again.
		List<BoosterPick.OwnPack> notMade = AutoMemory.NotMade(page);

		foreach (BoosterPick.OwnPack own in notMade) {
			Log.Info(new Said("the {0} booster pack wasn't made after all - it can be made again", own.Name), Bot.Name);
		}

		if (notMade.Count > 0) {
			AutoMemory.Save(Bot.Name);
		}

		SpareBadges? badges = SpareBadges.Parse(await Bot.Web.ApiGetAsync("IPlayerService", "GetBadges", new Dictionary<string, string> {
			["steamid"] = Bot.SteamId.ToString(CultureInfo.InvariantCulture)
		}, ct).ConfigureAwait(false));

		InventoryContents? inventory = await Inventory.ReadAsync(Bot, 753, "6", ct).ConfigureAwait(false);

		// Not knowing the badges or every card would rank by nothing - a maxed badge would look like level 0.
		if ((badges == null) || (inventory is not { Complete: true })) {
			Log.Debug(new Said("booster packs: couldn't read the badges or the whole inventory - trying again later"), Bot.Name);
			_autoNext = Later(1, 2);

			return;
		}

		Dictionary<uint, Dictionary<string, int>> normal = LevelPlanner.NormalCards(inventory);

		// A whole normal set is held: a level waiting to be crafted - badge crafting looks at its next chance.
		if (BoosterPick.AnyComplete(page, badges.Levels, normal)) {
			CraftSoon(setComplete: true);
		}

		BoosterPick.Ranking ranking = BoosterPick.Rank(page, badges.Levels, normal, Bot.Cfg.BoosterNeverOpen, AutoMemory.Made, DateTime.UtcNow, DearCards.Marked(Bot));

		// 1.8.2 "Use gems for the most levels": with buying cards on, the gems maths first (Boosters.Gems.cs) - a sack or a
		// pack to sell, or the closest badge as always. Untradable gems only ever go to the badge.
		bool untradableOnly = false;

		if (GemsChoice.Applies(Bot.Cfg, CardBuyer.On(Bot))) {
			GemsGo go = await GemsStepAsync(page, ranking, inventory, ct).ConfigureAwait(false);

			if (go == GemsGo.Handled) {
				return;
			}

			untradableOnly = go == GemsGo.BadgeUntradable;
		} else {
			LastGems = null;
		}

		BoosterPick.Choice choice = untradableOnly ? BoosterPick.Choose(ranking, page.UntradableGems, 0, dayOpen: true)
			: BoosterPick.Choose(ranking, page.Gems, Bot.Cfg.BoosterKeepGems, dayOpen: true);

		SavingFor = choice.WaitingForGems && (ranking.Ranked.Count > 0) ? ranking.Ranked[0].App : 0;

		if (choice.Next is not { } next) {
			_autoStatus = choice.Why;
			_autoNext = choice.WaitingForGems ? Later(6, 10) : Later(2, 4);

			return;
		}

		Offer offer = page.Offers[next.App];
		ulong after = inventory.Assets.Select(static a => ulong.TryParse(InventoryContents.Text(a, "assetid"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0).DefaultIfEmpty(0ul).Max();

		await Task.Delay(Rng.Seconds(4, 12), ct).ConfigureAwait(false);
		Made made = await MakeAsync(Bot, offer, page.TradableGems, page.UntradableGems, ct, prefer: untradableOnly ? "3" : null).ConfigureAwait(false);

		if (!made.Ok && made.NoAnswer) {
			// No answer isn't no pack: Steam may well have made it. Noted as maybe made - the inventory showing a new pack
			// of the game adopts it, the booster creator page showing the game still open lets it go - and never asked
			// for twice meanwhile.
			AutoMemory.NoteMade(next.App, DateTime.UtcNow);
			AutoMemory.Add(new BoosterPick.OwnPack(next.App, offer.Name, 0, after, DateTime.UtcNow, Maybe: true, SetSize: next.SetSize));
			AutoMemory.Save(Bot.Name);
			Log.Info(new Said("Steam didn't answer about the {0} booster pack - looking for it in the inventory before making another", offer.Name), Bot.Name);
			_openNext = DateTime.UtcNow + Rng.Minutes(1, 3);
			_autoNext = DateTime.UtcNow + Rng.Minutes(4, 8);

			return;
		}

		if (!made.Ok) {
			Log.Info(new Said("couldn't make a {0} booster pack: {1}", offer.Name, made.Why ?? "-"), Bot.Name);
			AutoMemory.NoteMade(next.App, DateTime.UtcNow);   // not this game again today; the next closest can go
			AutoMemory.Save(Bot.Name);
			_autoNext = Later(1, 2);

			return;
		}

		AutoMemory.NoteMade(next.App, DateTime.UtcNow);
		BoosterPick.OwnPack pack = new(next.App, offer.Name, made.ItemId, after, DateTime.UtcNow, SetSize: next.SetSize);
		AutoMemory.Add(pack);
		AutoMemory.AddMade(pack);   // never held back as new, should it ever be let go of before it's opened
		AutoMemory.Save(Bot.Name);
		Log.Reward(Boosters.MadeSaid(offer.Name, made.Gems), Bot.Name);
		Log.Debug(new Said("{0} pack cost {1} gems - {2}", offer.Name, offer.Price, choice.Why), Bot.Name);

		// Opened after a short while, like somebody looking at what they made; a robot then goes on to the next game.
		_openNext = DateTime.UtcNow + (Bot.Cfg.LegitMode ? Rng.HumanMinutes(3, 20) : Rng.Minutes(1, 3));
		_autoNext = DateTime.UtcNow + Rng.Minutes(1, 2);
		SpaceOut();
		_autoStatus = new Said("made a {0} booster pack - opening it soon", offer.Name);
	}

	private async Task OpenOwnAsync(CancellationToken ct) {
		InventoryContents? inventory = await Inventory.ReadAsync(Bot, 753, "6", ct).ConfigureAwait(false);
		HashSet<ulong>? promised = inventory == null ? null : await TradeOffers.PromisedAsync(Bot, ct).ConfigureAwait(false);

		if ((inventory == null) || (promised == null)) {
			_openNext = Later(1, 2);

			return;
		}

		List<(string App, ulong Asset)> packs = BadgeCraft.Packs(inventory, promised);
		List<SpareItem> held = Spare.Read(inventory);
		ItemsNote note = new(Bot.Name);

		// A pack without its id yet gets it as soon as the inventory shows it - kept out of "Packs it doesn't open" from then.
		if (AutoMemory.Locate(packs)) {
			AutoMemory.Save(Bot.Name);
		}

		LiveSpareSteam steam = new(Bot);
		bool opened = false;

		foreach (BoosterPick.OwnPack own in AutoMemory.Snapshot().OrderBy(static o => o.At)) {
			ulong asset = own.Asset != 0 && packs.Exists(p => p.Asset == own.Asset) ? own.Asset : 0;

			if (asset == 0) {
				// Not there: Steam hasn't shown it yet, or somebody opened, sent or sold it by hand. Given up after two days.
				if ((DateTime.UtcNow - own.At > BoosterPick.OwnFor) && AutoMemory.Remove(own)) {
					Log.Debug(new Said("the {0} booster pack it made isn't in the inventory any more - not looking for it again", own.Name), Bot.Name);
				}

				continue;
			}

			// A booster-packs look may have opened it meanwhile (SpareRun.PacksAsync opens these too). 1.8.2: one made to
			// sell sealed is never opened (Boosters.Gems.cs lists it).
			if ((own.For == BoosterPick.ToSell) || !AutoMemory.Ids().Contains(asset)) {
				continue;
			}

			HashSet<ulong> claimed = Bot.ClaimItems([asset]);

			if (claimed.Count == 0) {
				continue;   // a send or a sale has it right now - tried again next time
			}

			(bool ok, List<string> cards, string? why) result;
			string name = own.Name + " Booster Pack";

			try {
				result = await steam.UnpackAsync(new SpareItem(asset, own.App, SpareKind.BoosterPack, "", name, false, false), ct).ConfigureAwait(false);

				// Off the list while it's still claimed, so the other opener never tries it after.
				if (result.ok) {
					AutoMemory.Remove(own);
				}
			} finally {
				Bot.ReleaseItems(claimed);
			}

			if (!result.ok) {
				Log.Info(new Said("couldn't open the {0} booster pack it made: {1}", own.Name, result.why ?? "-"), Bot.Name);

				break;
			}

			opened = true;
			// 1.8.2: a pack opened to sell its cards - they're listed at the next looks, by their own copies (not one held before).
			OpenedToSell(Bot, own, result.cards, held.Where(i => (i.Kind == SpareKind.Card) && (i.Game == own.App) && (i.AssetId != 0)).Select(static i => i.AssetId));
			note.Opened(name, result.cards);
			HashSet<string> before = held.Where(i => (i.Kind == SpareKind.Card) && (i.Game == own.App)).Select(static i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
			Log.Earned(SpareRun.OpenedSaid(name, result.cards, SpareRun.NewForBadge(before, result.cards)), Bot.Name);
			Log.Debug(new Said("why it opened {0}: {1}", SpareRun.PackSaid(name), new Said(SpareRun.LevelWhy, own.Name)), Bot.Name);

			// The next pack of the same game counts these as held.
			held.AddRange(result.cards.Select(c => new SpareItem(0, own.App, SpareKind.Card, "", c, false, false)));

			// A human-mode account opens one, then gets on with its day.
			if (Bot.Cfg.LegitMode) {
				break;
			}

			await Task.Delay(Rng.Seconds(3, 9), ct).ConfigureAwait(false);
		}

		AutoMemory.Save(Bot.Name);
		note.Send();
		_openNext = AutoMemory.CountOwed() > 0 ? DateTime.UtcNow + (Bot.Cfg.LegitMode ? Rng.HumanMinutes(5, 30) : Rng.Minutes(10, 20)) : DateTime.MinValue;

		if (opened) {
			SpaceOut();
			CraftSoon();
		}
	}

	/// <summary>A pack made by hand ('booster &lt;account&gt; &lt;appIDs&gt;') is automatic's own now: opened at its next chance in the day.</summary>
	internal void OpenSoon() => _openNext = DateTime.UtcNow + (Bot.Cfg.LegitMode ? Rng.HumanMinutes(3, 20) : Rng.Minutes(1, 3));

	/// <summary>
	/// A set may be complete now (<paramref name="setComplete"/>: it is): badge crafting looks sooner than its daily round,
	/// as BadgeCraft.LookSoon allows - or, while it's off, says once that sets wait.
	/// </summary>
	private void CraftSoon(bool setComplete = false) {
		if (Bot.Cfg.CraftBadges) {
			BotManager.ModuleOf<BadgeCraft>(Bot)?.LookSoon(setComplete);
		} else if (!_saidCraftingOff) {
			_saidCraftingOff = true;
			Log.Info(new Said("booster packs are filling card sets, but \"Craft badges from card sets\" is off - finished sets wait until it's on"), Bot.Name);
		}
	}
}
