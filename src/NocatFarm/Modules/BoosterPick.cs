using System.Globalization;
using System.Text;
using System.Text.Json;
using NocatFarm.Config;
using NocatFarm.Core;

namespace NocatFarm.Modules;

/// <summary>
/// Automatic "Make booster packs": which game a pack goes to next, and the numbers behind it. Normal cards and normal
/// badges only - a foil card never counts toward a set here, and a foil badge never counts as a level.
/// </summary>
/// <remarks>
/// The numbers, as Steam has them (see docs/cards-and-idling.md, "Levelling with gems"):
/// <list type="bullet">
/// <item>a pack costs 6000 / (cards in the game's set) gems, rounded - 1200 for a 5-card set, 400 for a 15-card one - so the
/// set size is read back from the price on the booster creator page;</item>
/// <item>a pack is 3 random cards of the set (a foil now and then, not counted on);</item>
/// <item>one pack per game a day, only for the games the booster creator lists;</item>
/// <item>a normal badge goes to level 5, each level a full set crafted, 100 XP.</item>
/// </list>
/// To finish a set missing m of its n cards from random packs takes on average n x (1 + 1/2 + ... + 1/m) cards (the
/// coupon collector), which is n x H(m) / 3 packs at 6000/n gems each: about 2000 x H(m) gems, whatever the set's size.
/// So the fewest missing cards is also the most XP per gem - and closest first is the whole rule.
/// </remarks>
public static class BoosterPick {
	/// <summary>"Make booster packs": off.</summary>
	public const int Off = 0;

	/// <summary>"Make booster packs": the games on the list.</summary>
	public const int Pick = 1;

	/// <summary>"Make booster packs": automatic - the closest badge.</summary>
	public const int Auto = 2;

	/// <summary>A normal game badge's top level.</summary>
	public const int MaxLevel = 5;

	/// <summary>Gems a pack costs times the cards in its set.</summary>
	public const int GemsPerSetOfPacks = 6000;

	/// <summary>One game automatic could make a pack for.</summary>
	/// <param name="Have">Cards of the set held toward its next level.</param>
	/// <param name="Missing">Cards of the set still missing for its next level.</param>
	/// <param name="Craftable">Sets already complete in the inventory, waiting to be crafted.</param>
	public sealed record Candidate(uint App, string Name, uint Price, int SetSize, int Level, int Have, int Missing, int Craftable);

	/// <summary>The games in order, closest first, and how many were left out and why.</summary>
	/// <param name="Dear">1.8.2: games whose cards are worth more sold than the levels they'd make (<see cref="DearCards"/>) - never packed for.</param>
	public sealed record Ranking(IReadOnlyList<Candidate> Ranked, int Maxed, int Finished, int NeverOpen, int MadeToday, int Dear = 0);

	/// <summary>What automatic does next: make a pack for <see cref="Next"/>, or nothing for now, and why.</summary>
	public sealed record Choice(Candidate? Next, Said Why, bool WaitingForGems = false);

	/// <summary>Cards in a game's set, from its pack price: 6000 / price, rounded.</summary>
	public static int SetSize(uint price) => price == 0 ? 0 : (int) Math.Round(GemsPerSetOfPacks / (double) price, MidpointRounding.AwayFromZero);

	/// <summary>
	/// Every game the booster creator lists, closest normal badge first: fewest cards missing for the next level, then the
	/// cheaper pack. Left out: a badge at level 5, a badge the cards held already finish, a game whose packs are never
	/// opened, and a game made in the last day (Steam's own "unavailable", or made here). 1.8.2: and a game whose cards are
	/// worth more sold than kept (<paramref name="dear"/>) - its cards are being sold, so a pack for it would be wasted.
	/// </summary>
	public static Ranking Rank(Boosters.Page page, IReadOnlyDictionary<uint, int> normalLevels, IReadOnlyDictionary<uint, Dictionary<string, int>> normalCards,
		IReadOnlyCollection<uint> neverOpen, IReadOnlyDictionary<uint, DateTime> madeAt, DateTime now, IReadOnlyCollection<uint>? dear = null) {
		List<Candidate> ranked = [];
		int maxed = 0, finished = 0, never = 0, today = 0, sold = 0;

		foreach (Boosters.Offer offer in page.Offers.Values) {
			int size = SetSize(offer.Price);

			if (size <= 0) {
				continue;
			}

			int level = normalLevels.GetValueOrDefault(offer.AppId);

			if (level >= MaxLevel) {
				maxed++;

				continue;
			}

			if (neverOpen.Contains(offer.AppId)) {
				never++;

				continue;
			}

			if (dear?.Contains(offer.AppId) == true) {
				sold++;

				continue;
			}

			if (offer.Unavailable || (madeAt.TryGetValue(offer.AppId, out DateTime made) && (now - made < TimeSpan.FromDays(1)))) {
				today++;

				continue;
			}

			IReadOnlyDictionary<string, int> held = normalCards.GetValueOrDefault(offer.AppId) ?? [];
			int kinds = held.Count(static c => c.Value > 0);
			int complete = kinds >= size ? held.Values.Where(static v => v > 0).Min() : 0;

			// The sets held already reach level 5 - a pack would be wasted on it.
			if (complete >= MaxLevel - level) {
				finished++;

				continue;
			}

			int have = Math.Min(size, held.Count(c => c.Value > complete));
			ranked.Add(new Candidate(offer.AppId, offer.Name, offer.Price, size, level, have, size - have, complete));
		}

		return new Ranking([.. ranked.OrderBy(static c => c.Missing).ThenBy(static c => c.Price).ThenBy(static c => c.App)], maxed, finished, never, today, sold);
	}

	/// <summary>
	/// The next pack: the closest game, if the gems cover its price and the gems to keep, and the account's day allows it.
	/// Only ever the closest - a cheaper game further away isn't made instead while the gems save up for it.
	/// </summary>
	public static Choice Choose(Ranking ranking, uint gems, int keepGems, bool dayOpen) {
		if (ranking.Ranked.Count == 0) {
			return new Choice(null, new Said("no game to make a booster pack for right now - {0} at badge level 5, {1} made in the last day", ranking.Maxed, ranking.MadeToday));
		}

		Candidate top = ranking.Ranked[0];
		long needs = top.Price + (long) Math.Max(0, keepGems);

		if (gems < needs) {
			return new Choice(null, new Said("saving gems for a {0} booster pack - {1} needed with the gems kept, {2} here", top.Name, needs, gems), true);
		}

		if (!dayOpen) {
			return new Choice(null, new Said("a {0} booster pack is next - waiting for the account's day", top.Name));
		}

		return new Choice(top, new Said("{0} is closest to its next badge level: {1} of {2} cards, {3} missing", top.Name, top.Have, top.SetSize, top.Missing));
	}

	/// <summary>1 + 1/2 + ... + 1/m.</summary>
	public static double Harmonic(int m) {
		double h = 0;

		for (int i = 1; i <= m; i++) {
			h += 1.0 / i;
		}

		return h;
	}

	/// <summary>Gems it takes on average to finish the next level from random packs: price x set x H(missing) / 3.</summary>
	public static int ExpectedGems(Candidate c) => (int) Math.Round(c.Price * (double) c.SetSize * Harmonic(c.Missing) / 3.0);

	/// <summary>Packs it takes on average to finish the next level: set x H(missing) / 3.</summary>
	public static double ExpectedPacks(Candidate c) => c.SetSize * Harmonic(c.Missing) / 3.0;

	// ── the setting ─────────────────────────────────────────────────────────
	/// <summary>
	/// Picking automatic keeps gems loose: "Gems afterwards" on automatic or sacks would pack them into sacks, which the
	/// booster creator can't use. Said once, in the log, when it happens. Only when automatic was just picked - a choice
	/// made afterwards stays.
	/// </summary>
	/// <returns>true when "Gems afterwards" was changed.</returns>
	public static bool Settle(BotConfig cfg, int wasMake, string name) {
		if ((cfg.BoosterMake != Auto) || (wasMake == Auto) || (cfg.SpareGems is not (0 or 2))) {
			return false;
		}

		cfg.SpareGems = 1;
		Log.Info(new Said("{0}: automatic booster packs need loose gems, so \"Gems afterwards\" is now \"keep them loose\"", name), name);

		return true;
	}

	// ── what it made, kept over a restart ───────────────────────────────────
	/// <summary>A pack automatic made and still has to open.</summary>
	/// <param name="Asset">The pack's item id when Steam said it, or once the inventory showed it; else 0.</param>
	/// <param name="After">The highest item id in the inventory before it was made - a pack of the game above it is the new one.</param>
	/// <param name="Maybe">Steam didn't answer the request: whether it was made is known only once the inventory or the
	/// booster creator page says.</param>
	/// <param name="SetSize">Cards in the game's set (0: not known).</param>
	/// <param name="For">1.8.2, what it was made for: 0 the closest badge (opened), <see cref="ToSell"/> sold sealed,
	/// <see cref="ToOpenAndSell"/> opened and its cards sold - see <see cref="GemsChoice"/>.</param>
	public sealed record OwnPack(uint App, string Name, ulong Asset, ulong After, DateTime At, bool Maybe = false, int SetSize = 0, int For = 0);

	/// <summary>1.8.2: a pack made to be sold sealed - never opened, sent or sold by anything else while it's its own.</summary>
	public const int ToSell = 1;

	/// <summary>1.8.2: a pack made to be opened and its cards sold.</summary>
	public const int ToOpenAndSell = 2;

	/// <summary>1.8.2: gems a pack costs for a set of this many cards (6000 / cards, rounded) - 0 when the size isn't known.</summary>
	public static int PackGems(int setSize) => setSize > 0 ? (int) Math.Round(GemsPerSetOfPacks / (double) setSize, MidpointRounding.AwayFromZero) : 0;

	/// <summary>How long a pack it made stays its own to open. Older, it follows "Open booster packs" like any other.</summary>
	public static readonly TimeSpan OwnFor = TimeSpan.FromDays(2);

	/// <summary>How long a pack made here is remembered as made here: past the longest "Leave new packs alone" (30 days).</summary>
	public static readonly TimeSpan MadeFor = TimeSpan.FromDays(31);

	/// <summary>Why a pack it made stopped being its own to open.</summary>
	public enum Let { NotAutomatic, TooOld, NeverOpen }

	/// <summary>When each game last had a pack made here, and the packs made here still to open.</summary>
	/// <remarks>
	/// Shared by two threads: the booster module adds and opens packs while a spare-items look asks which are its own. So
	/// everything that touches <see cref="Owed"/> goes through the lock, and what's handed out is a copy.
	/// </remarks>
	public sealed class Memory {
		private readonly Lock _gate = new();

		public Dictionary<uint, DateTime> Made { get; set; } = [];
		public List<OwnPack> Owed { get; set; } = [];

		/// <summary>
		/// Every pack made here - by automatic, "games I pick" or the booster command - for <see cref="MadeFor"/>: never held
		/// back as new by "Leave new packs alone" (Spare.PlanPacks). Packs from drops and trades still are.
		/// </summary>
		public List<OwnPack> MadePacks { get; set; } = [];

		private static string PathOf(string name) => System.IO.Path.Combine(ConfigStore.ConfigDir, "state", $"boosters-{name}.json");

		public static Memory Load(string name) {
			try {
				if (File.Exists(PathOf(name)) && JsonSerializer.Deserialize<Memory>(File.ReadAllText(PathOf(name))) is { } m) {
					return m;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				Log.Failed("couldn't read which booster packs it made", e, name);
			}

			return new Memory();
		}

		public void Save(string name) {
			try {
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf(name))!);
				DateTime old = DateTime.UtcNow.AddDays(-2);

				lock (_gate) {
					foreach (uint app in Made.Where(m => m.Value < old).Select(static m => m.Key).ToList()) {
						Made.Remove(app);
					}

					MadePacks ??= [];
					MadePacks.RemoveAll(static p => DateTime.UtcNow - p.At > MadeFor);

					AtomicFile.Write(PathOf(name), JsonSerializer.Serialize(this));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Failed("couldn't save which booster packs it made", e, name);
			}
		}

		/// <summary>A game had a pack made (or asked for) at <paramref name="at"/>.</summary>
		public void NoteMade(uint app, DateTime at) {
			lock (_gate) {
				Made[app] = at;
			}
		}

		public void Add(OwnPack own) {
			lock (_gate) {
				Owed.Add(own);
			}
		}

		public bool Remove(OwnPack own) {
			lock (_gate) {
				return Owed.Remove(own);
			}
		}

		/// <summary>A pack made here, by whatever made it - see <see cref="MadePacks"/>.</summary>
		public void AddMade(OwnPack made) {
			lock (_gate) {
				MadePacks ??= [];
				MadePacks.RemoveAll(static p => DateTime.UtcNow - p.At > MadeFor);
				MadePacks.Add(made);
			}
		}

		/// <summary>The item ids of the packs made here in the last <see cref="MadeFor"/> that are known.</summary>
		public HashSet<ulong> MadeIds() {
			lock (_gate) {
				return [.. (MadePacks ?? []).Where(static p => (p.Asset != 0) && (DateTime.UtcNow - p.At <= MadeFor)).Select(static p => p.Asset)];
			}
		}

		/// <summary>An owed pack opened by a booster-packs look instead: off the list, so it isn't looked for again.</summary>
		public bool RemoveAsset(ulong asset) {
			lock (_gate) {
				return Owed.RemoveAll(o => o.Asset == asset) > 0;
			}
		}

		/// <summary>How many packs are still to open (1.8.2: not the ones made to sell sealed).</summary>
		public int CountOwed() {
			lock (_gate) {
				return Owed.Count(static o => o.For != ToSell);
			}
		}

		/// <summary>The packs still to open, as they are now - a copy, safe to go through while the other thread changes the list.</summary>
		public List<OwnPack> Snapshot() {
			lock (_gate) {
				return [.. Owed];
			}
		}

		/// <summary>The item ids of the packs still to open that are known (1.8.2: not the ones made to sell sealed).</summary>
		public HashSet<ulong> Ids() {
			lock (_gate) {
				return [.. Owed.Where(static o => (o.Asset != 0) && (o.For != ToSell)).Select(static o => o.Asset)];
			}
		}

		/// <summary>
		/// 1.8.2: the item ids of the packs made to sell (sealed, or opened and their cards sold) that are known - out of
		/// "Open booster packs" and "Packs it doesn't open": only automatic lists or opens them, and notes the cards.
		/// </summary>
		public HashSet<ulong> ForSaleIds() {
			lock (_gate) {
				return [.. Owed.Where(static o => (o.Asset != 0) && (o.For != 0)).Select(static o => o.Asset)];
			}
		}

		/// <summary>
		/// Lets go of the packs that aren't its own to open any more: all of them once "Make booster packs" isn't automatic
		/// (what would open them doesn't run then), one older than <see cref="OwnFor"/> whatever the choice, and one whose
		/// game went on "Never open packs from these games" after it was made. Let go of, they're packs like any other:
		/// "Open booster packs" and "Packs it doesn't open" decide them.
		/// </summary>
		public List<(OwnPack Pack, Let Why)> Release(int make, IReadOnlyCollection<uint> neverOpen, DateTime now) {
			lock (_gate) {
				List<(OwnPack, Let)> let = [];

				foreach (OwnPack own in Owed) {
					if (make != Auto) {
						let.Add((own, Let.NotAutomatic));
					} else if (now - own.At > OwnFor) {
						let.Add((own, Let.TooOld));
					} else if (neverOpen.Contains(own.App)) {
						let.Add((own, Let.NeverOpen));
					}
				}

				foreach ((OwnPack own, Let _) in let) {
					Owed.Remove(own);
				}

				return let;
			}
		}

		/// <summary><see cref="Release"/>, saved and said in the log - once, since what's let go of is gone from the list.</summary>
		public void ReleaseAndSay(int make, IReadOnlyCollection<uint> neverOpen, DateTime now, string name) {
			List<(OwnPack Pack, Let Why)> let = Release(make, neverOpen, now);

			if (let.Count == 0) {
				return;
			}

			Save(name);
			int notAuto = let.Count(static l => l.Why == Let.NotAutomatic);

			if (notAuto > 0) {
				Log.Info(new Said("\"Make booster packs\" isn't automatic any more - the {0} booster pack(s) it made and hadn't opened follow \"Open booster packs\" now, like any other", notAuto), name);
			}

			foreach ((OwnPack own, Let why) in let.Where(static l => l.Why != Let.NotAutomatic)) {
				// Never seen in the inventory in two days: opened, sent or sold by hand - nothing to let go of.
				if ((why == Let.TooOld) && (own.Asset == 0)) {
					Log.Debug(new Said("the {0} booster pack it made isn't in the inventory any more - not looking for it again", own.Name), name);

					continue;
				}

				Log.Info(why == Let.TooOld
					? new Said("the {0} booster pack it made has waited over 2 days to be opened - it follows \"Open booster packs\" now, like any other", own.Name)
					: new Said("{0} is on \"Never open packs from these games\" now - the booster pack it made for it stays shut", own.Name), name);
			}
		}

		/// <summary>
		/// Writes down the item id of each pack that didn't have one, once the inventory shows it (<see cref="Find"/>), so
		/// it's kept out of "Packs it doesn't open" too - and a pack Steam didn't answer about was made after all.
		/// </summary>
		/// <returns>Whether anything was written down.</returns>
		public bool Locate(IReadOnlyList<(string App, ulong Asset)> packs) {
			lock (_gate) {
				// The packs made here are found the same way, on their own list.
				return LocateIn(Owed, packs) | LocateIn(MadePacks ??= [], packs);
			}
		}

		private static bool LocateIn(List<OwnPack> list, IReadOnlyList<(string App, ulong Asset)> packs) {
			HashSet<ulong> taken = [.. list.Where(static o => o.Asset != 0).Select(static o => o.Asset)];
			bool changed = false;

			foreach (OwnPack own in list.Where(static o => o.Asset == 0).OrderBy(static o => o.At).ToList()) {
				ulong asset = Find(own, packs, taken);

				if (asset == 0) {
					continue;
				}

				taken.Add(asset);
				list[list.IndexOf(own)] = own with { Asset = asset, Maybe = false };
				changed = true;
			}

			return changed;
		}

		/// <summary>
		/// The packs Steam didn't answer about that the booster creator page shows weren't made - the game can still be
		/// made today. Taken off the list, and the game is free to be made again.
		/// </summary>
		public List<OwnPack> NotMade(Boosters.Page page) {
			lock (_gate) {
				List<OwnPack> gone = [.. Owed.Where(o => o.Maybe && (o.Asset == 0) && page.Offers.TryGetValue(o.App, out Boosters.Offer? offer) && !offer.Unavailable)];

				foreach (OwnPack own in gone) {
					Owed.Remove(own);
					Made.Remove(own.App);
				}

				return gone;
			}
		}
	}

	/// <summary>
	/// Where a pack made here is in the inventory now: its own id, or - when Steam didn't say it - the first pack of its
	/// game that came in after it was made and isn't another owed pack. 0 when it isn't there (yet, or any more).
	/// </summary>
	public static ulong Find(OwnPack own, IReadOnlyList<(string App, ulong Asset)> packs, IReadOnlyCollection<ulong> taken) {
		string app = own.App.ToString(CultureInfo.InvariantCulture);

		if (own.Asset != 0) {
			return packs.Any(p => p.Asset == own.Asset) ? own.Asset : 0;
		}

		return packs.Where(p => (p.App == app) && (p.Asset > own.After) && !taken.Contains(p.Asset)).Select(static p => p.Asset).DefaultIfEmpty(0ul).Min();
	}

	/// <summary>
	/// The item ids of the packs automatic made and hasn't opened - kept out of "Open booster packs" and "Packs it doesn't
	/// open". Empty once "Make booster packs" isn't automatic: its packs are let go of then (<see cref="Memory.Release"/>).
	/// </summary>
	public static HashSet<ulong> OwnPackIds(Bot bot) {
		Memory m = BotManager.ModuleOf<Boosters>(bot)?.AutoMemory ?? Memory.Load(bot.Name);
		m.ReleaseAndSay(bot.Cfg.BoosterMake, bot.Cfg.BoosterNeverOpen, DateTime.UtcNow, bot.Name);

		return m.Ids();
	}

	/// <summary>
	/// 1.8.2: the games automatic booster packs is working on - every one it made a pack for that's still to open. Buying
	/// cards leaves them to the packs. A game it's only saving gems for isn't: with 211 of 1,200 gems that pack is weeks
	/// away, and buying may finish the set first when that's the cheapest level (live, 2026-10-19). None unless "Make booster
	/// packs" is automatic.
	/// </summary>
	public static IReadOnlyCollection<uint> Busy(Bot bot) {
		if (bot.Cfg.BoosterMake != Auto) {
			return [];
		}

		Boosters? boosters = BotManager.ModuleOf<Boosters>(bot);

		return Busy(bot.Cfg.BoosterMake, (boosters?.AutoMemory ?? Memory.Load(bot.Name)).Snapshot(), 0);
	}

	/// <summary>
	/// <see cref="Busy(Bot)"/> on its own: the packs still to open, and <paramref name="savingFor"/> - a game whose pack can
	/// be made now (the gems are there) - when automatic.
	/// </summary>
	public static IReadOnlyCollection<uint> Busy(int make, IEnumerable<OwnPack> owed, uint savingFor) {
		if (make != Auto) {
			return [];
		}

		// 1.8.2: a pack made to sell (sealed or its cards) isn't levelling its game's badge - buying can go on with it.
		HashSet<uint> games = [.. owed.Where(static o => o.For == 0).Select(static o => o.App).Where(static a => a != 0)];

		if (savingFor != 0) {
			games.Add(savingFor);
		}

		return games;
	}

	/// <summary>
	/// Whether the cards held already make a whole normal set of a game the booster creator lists whose badge isn't at
	/// level 5 - a level waiting to be crafted.
	/// </summary>
	public static bool AnyComplete(Boosters.Page page, IReadOnlyDictionary<uint, int> normalLevels, IReadOnlyDictionary<uint, Dictionary<string, int>> normalCards) =>
		page.Offers.Values.Any(o => (SetSize(o.Price) is int size and > 0) && (normalLevels.GetValueOrDefault(o.AppId) < MaxLevel)
			&& normalCards.TryGetValue(o.AppId, out Dictionary<string, int>? held) && (held.Count(static c => c.Value > 0) >= size));

	// ── 'booster plan' ──────────────────────────────────────────────────────
	/// <summary>The plan in plain words: what automatic picks next and why, the rough gems for it, and what comes after.</summary>
	public static string Explain(string account, int make, Boosters.Page page, Ranking ranking, int keepGems, bool dayOpen, bool crafting, int openChoice) {
		StringBuilder sb = new();
		string mode = make switch { Auto => "automatic", Pick => "games I pick", _ => "off" };
		sb.Append(CultureInfo.InvariantCulture, $"{account}: {page.Gems:N0} gems ({page.TradableGems:N0} tradable), keeping at least {Math.Max(0, keepGems):N0}. Make booster packs: {mode}.");

		Choice choice = Choose(ranking, page.Gems, keepGems, dayOpen);

		if (ranking.Ranked.Count > 0) {
			Candidate top = ranking.Ranked[0];
			int gems = ExpectedGems(top);
			sb.AppendLine().Append(CultureInfo.InvariantCulture,
				$"  Automatic would pick {top.Name} ({top.App}): badge level {top.Level}, {top.Have} of its {top.SetSize} cards for the next level, {top.Missing} missing. A pack is {top.Price:N0} gems for 3 random cards.");

			if (top.Craftable > 0) {
				sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  It already holds {top.Craftable} full set(s) to craft first.");
			}

			sb.AppendLine().Append(CultureInfo.InvariantCulture,
				$"  On average that's about {ExpectedPacks(top):0.#} pack(s), {gems:N0} gems, for the next level (100 XP) - about {100_000.0 / Math.Max(1, gems):0} XP per 1,000 gems. Luck can make it more or less.");
			sb.AppendLine().Append("  Now: ").Append(choice.Next != null ? "it can make that pack." : choice.Why.ToEnglish() + ".");

			if (ranking.Ranked.Count > 1) {
				sb.AppendLine().Append("  After that: " + string.Join(", ", ranking.Ranked.Skip(1).Take(4).Select(static c => $"{c.Name} ({c.Missing} missing)")));
			}
		} else {
			sb.AppendLine().Append("  ").Append(choice.Why.ToEnglish()).Append('.');
		}

		sb.AppendLine().Append(CultureInfo.InvariantCulture,
			$"  Left out: {ranking.Maxed} at badge level 5, {ranking.Finished} the cards held already finish, {ranking.NeverOpen} never opened, {ranking.MadeToday} made in the last day{(ranking.Dear > 0 ? $", {ranking.Dear} whose cards are worth more sold than kept" : "")}.");
		sb.AppendLine().Append("  The best case is about 2,000 gems a level (a set's worth of packs with no repeats); each card already missing makes it more.");

		if (!crafting) {
			sb.AppendLine().Append("  'Craft badges from card sets' is off - finished sets wait until it's on.");
		}

		if ((make != Auto) && (openChoice != 2)) {
			sb.AppendLine().Append("  Packs made by 'games I pick' follow 'Open booster packs'; automatic opens its own.");
		}

		return sb.ToString();
	}
}
