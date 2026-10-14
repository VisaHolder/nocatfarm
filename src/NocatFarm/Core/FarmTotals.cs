using System.Security.Cryptography;
using System.Text.Json;
using NocatFarm.Config;

namespace NocatFarm.Core;

/// <summary>
/// The hours banked and cards dropped on this install that haven't gone to nocat.lol yet - what the hourly ping (see
/// <see cref="UserCount"/>) adds to the "hours of game time banked · cards farmed" totals on the download page.
/// </summary>
/// <remarks>
/// Only numbers ever leave: game-hours and cards (and from 1.8.1 items turned into gems, badges crafted, packs opened and
/// items sold), since the last time nocat.lol said it had them - with how long that covers and how many accounts this copy
/// has, which nocat.lol caps them by. Nothing about which account, game, card or item.
///
/// A restored backup's history is the install's that made it: it never goes in this install's catch-up (see
/// <see cref="Restoring"/>). A file that can't be read just now is tried again later; a broken one is kept aside and
/// counting goes on from then with no catch-up - never one that could count the history twice.
///
/// Never twice: what's due is put into a "batch" with a random name and saved to disk BEFORE it is sent. Until nocat.lol
/// answers that it has that batch ("got": its name), the same batch - same name, same numbers - goes with every ping, and
/// nocat.lol counts a name only once. Whatever is banked meanwhile waits in the "fresh" counter for the batch after.
/// An older nocat.lol never says "got", so the batch just waits (for <see cref="GiveUpAfter"/> at most) - nothing lost,
/// nothing doubled.
///
/// Never nonsense: a batch is capped to what could physically have happened since the batch before it - every account
/// running Steam's limit of <see cref="MaxGamesPerAccount"/> games the whole time, and a card per game every half hour.
/// nocat.lol caps it again on its side.
///
/// The hours from before (one time only): the history already holds up to ~400 days of game-hours and cards from before
/// this copy started counting. The moment counting starts on this install - the first time sharing is on - that history
/// is added up and frozen as one "catch-up" (<see cref="CatchUp"/>), saved before it's sent, sent with every ping until
/// nocat.lol answers "gotold", and never made again. The same moment starts the fresh counter from 0, so an hour is
/// either in the catch-up or in a batch, never both: the history and the counter are both fed under the history's own
/// lock, and the catch-up is added up under it too. It's capped to what's physically possible (each account's day at
/// most 32 games for 24 hours, and <see cref="MaxCardsPerAccountDay"/> cards). An older nocat.lol never says "gotold":
/// the catch-up simply keeps going with the pings, so it's counted the day nocat.lol can.
///
/// The rest (1.8.1): items turned into gems, badges crafted (foil too), booster packs opened and items sold on the
/// market (<see cref="Extra"/>) go in the same batches, each capped per account per hour
/// (<see cref="MaxGemItemsPerAccountHour"/> and the others). The history didn't record them before 1.8.1, so there's no
/// catch-up of them: they count from when this version runs, and the history records them from then on too. An older
/// nocat.lol simply doesn't read them.
///
/// "Count me as a user" or "Share my farming totals" off: nothing is counted or sent, and the batches waiting
/// are dropped. A catch-up already made is kept, not sent, until sharing is on again - it never overlaps the batches
/// whenever it goes.
/// </remarks>
public static class FarmTotals {
	/// <summary>Steam runs at most 32 games at once on an account.</summary>
	public const int MaxGamesPerAccount = 32;

	/// <summary>A card per game every half hour, 32 games at once - far more than Steam ever drops.</summary>
	public const int MaxCardsPerAccountHour = 2 * MaxGamesPerAccount;

	/// <summary>The most game-minutes one account can bank in a day: 32 games for 24 hours.</summary>
	public const double MaxMinutesPerAccountDay = MaxGamesPerAccount * 24 * 60;

	/// <summary>The most cards one account can get in a day, by the same "a card per game every half hour".</summary>
	public const int MaxCardsPerAccountDay = MaxCardsPerAccountHour * 24;

	/// <summary>Items one account turns into gems in an hour, at most.</summary>
	public const int MaxGemItemsPerAccountHour = 60;

	/// <summary>Badges (foil too) one account crafts in an hour, at most - Steam backs off long before that.</summary>
	public const int MaxBadgesPerAccountHour = 10;

	/// <summary>Booster packs one account opens in an hour, at most.</summary>
	public const int MaxPacksPerAccountHour = 30;

	/// <summary>Items of one account the market history says sold in an hour, at most.</summary>
	public const int MaxSoldPerAccountHour = 60;

	/// <summary>The other things counted besides hours and cards (1.8.1).</summary>
	public enum Extra {
		/// <summary>An item turned into gems.</summary>
		Gems,

		/// <summary>A badge level crafted, foil or not.</summary>
		Badge,

		/// <summary>A booster pack opened.</summary>
		Pack,

		/// <summary>An item of ours the market history says sold.</summary>
		Sold
	}

	/// <summary>
	/// A new batch is made at most this often, not with every hourly ping: each batch is a write on nocat.lol, and one an
	/// hour from every copy was far more than the totals need. A batch nocat.lol hasn't taken yet still goes with every
	/// ping until it does.
	/// </summary>
	public static readonly TimeSpan BatchEvery = TimeSpan.FromHours(6);

	/// <summary><see cref="BatchEvery"/> - which the older checks set to 0, for a batch at every ping.</summary>
	private static TimeSpan _batchGap = BatchEvery;

	/// <summary>A batch nocat.lol never took is dropped after this - an old nocat.lol that never will, or a week offline.</summary>
	public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

	/// <summary>
	/// One batch: its random name, the game-hours (one decimal) and cards in it, and when it was made - plus when the time
	/// it covers began (<paramref name="Start"/>) and how many accounts this copy had (<paramref name="Accounts"/>), which
	/// nocat.lol caps it by. A batch saved by a build before those has neither (default, 0) and goes without them. And
	/// (1.8.1) items turned into gems, badges crafted, packs opened and items sold - 0 in a batch saved before.
	/// </summary>
	public sealed record Batch(string B, double Hours, int Cards, DateTime Made, DateTime Start = default, int Accounts = 0,
		int Gems = 0, int Badges = 0, int Packs = 0, int Sold = 0) {
		/// <summary>Anything at all in it.</summary>
		[System.Text.Json.Serialization.JsonIgnore]
		public bool Any => (Hours > 0) || (Cards > 0) || (Gems > 0) || (Badges > 0) || (Packs > 0) || (Sold > 0);
	}

	/// <summary>
	/// The one-time catch-up: game-hours (one decimal) and cards from the history before counting started, from
	/// <paramref name="Accounts"/> accounts over <paramref name="Days"/> days (what nocat.lol caps it by), its random name,
	/// and when it was made.
	/// </summary>
	public sealed record CatchUp(string B, double Hours, int Cards, int Accounts, int Days, DateTime Made);

	private sealed class State {
		public double FreshMinutes { get; set; }
		public int FreshCards { get; set; }
		public DateTime FreshSince { get; set; }
		public Batch? Pending { get; set; }

		/// <summary>When counting started on this install - and the catch-up was made. Null: not yet.</summary>
		public DateTime? LiveSince { get; set; }

		/// <summary>The catch-up waiting for nocat.lol's "gotold", or null (none, or taken).</summary>
		public CatchUp? Old { get; set; }

		/// <summary>Items turned into gems, badges crafted, packs opened and items sold, not in a batch yet (1.8.1).</summary>
		public int FreshGems { get; set; }
		public int FreshBadges { get; set; }
		public int FreshPacks { get; set; }
		public int FreshSold { get; set; }

		[System.Text.Json.Serialization.JsonIgnore]
		public bool AnyExtra => (FreshGems > 0) || (FreshBadges > 0) || (FreshPacks > 0) || (FreshSold > 0);
	}

	/// <summary>The history so far, added up: game-minutes, cards, accounts and days. <see cref="History.Past"/>; the checks swap it.</summary>
	internal static Func<(double Minutes, int Cards, int Accounts, int Days)> PastTotals = History.Past;

	/// <summary>A catch-up made but not on disk yet: it isn't sent until it is.</summary>
	private static bool _oldUnsaved;

	/// <summary>
	/// A backup's history was put back and that isn't settled on disk yet (see <see cref="Restoring"/>): kept in memory as
	/// well as in <see cref="RestoredPath"/>, so it holds even when the mark can't be written.
	/// </summary>
	private static bool _restorePending;

	/// <summary>How the file is read - the checks swap it for one that fails.</summary>
	internal static Func<string, string> ReadFile = File.ReadAllText;

	private static readonly Lock Gate = new();
	private static State _state = new();
	private static string? _loadedFrom;
	private static DateTime _lastSave = DateTime.MinValue;

	private static string StatePath => Path.Combine(ConfigStore.ConfigDir, "state", "farm-totals.json");

	/// <summary>The mark a restore leaves until it's settled. Not one of a backup's files, so it never travels.</summary>
	private static string RestoredPath => Path.Combine(ConfigStore.ConfigDir, "state", "farm-totals.restored");

	/// <summary>Whether the totals are shared: "Count me as a user" and "Share my farming totals" both on.</summary>
	public static bool Sharing(GlobalConfig g) => g.CountMeAsUser && g.ShareFarmTotals;

	/// <summary>A batch's name: 16 lowercase hex characters.</summary>
	public static bool IsBatchName(string? s) => s is { Length: 16 } && s.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

	/// <summary>Minutes with games running, from the history's playtime tick: every real game counts, 3 games for a minute is 3 minutes.</summary>
	public static void NotePlay(double minutes, int games) => NotePlay(Live.Global, minutes, games, DateTime.UtcNow);

	public static void NotePlay(GlobalConfig g, double minutes, int games, DateTime nowUtc) {
		if (!Sharing(g) || (minutes <= 0) || double.IsNaN(minutes) || (games <= 0)) {
			return;
		}

		if (StartCounting(g, nowUtc)) {
			// Counting starts with this tick, which the history already has - so it's in the catch-up, not here.
			return;
		}

		lock (Gate) {
			if (!Load(nowUtc)) {
				return;   // the file can't be read just now: nothing is added to what isn't known
			}

			_state.FreshMinutes += minutes * Math.Min(games, MaxGamesPerAccount);
			SaveNow(nowUtc, soon: true);
		}
	}

	/// <summary>A card dropped, from the history.</summary>
	public static void NoteCard() => NoteCard(Live.Global, DateTime.UtcNow);

	public static void NoteCard(GlobalConfig g, DateTime nowUtc) {
		if (!Sharing(g)) {
			return;
		}

		if (StartCounting(g, nowUtc)) {
			// The history already has this card: it's in the catch-up.
			return;
		}

		lock (Gate) {
			if (!Load(nowUtc)) {
				return;
			}

			_state.FreshCards++;
			SaveNow(nowUtc, soon: true);
		}
	}

	/// <summary>An item turned into gems, a badge crafted, a pack opened or an item sold - from the history.</summary>
	public static void Note(Extra what) => Note(Live.Global, what, DateTime.UtcNow);

	public static void Note(GlobalConfig g, Extra what, DateTime nowUtc) {
		if (!Sharing(g)) {
			return;
		}

		// Counting may start with this one - it goes in the counter all the same: no catch-up has these.
		StartCounting(g, nowUtc);

		lock (Gate) {
			if (!Load(nowUtc)) {
				return;
			}

			switch (what) {
				case Extra.Gems: _state.FreshGems++; break;
				case Extra.Badge: _state.FreshBadges++; break;
				case Extra.Pack: _state.FreshPacks++; break;
				case Extra.Sold: _state.FreshSold++; break;
			}

			SaveNow(nowUtc, soon: true);
		}
	}

	/// <summary>
	/// What goes with this ping: the batch still waiting for nocat.lol's "got", or a new one made from what's fresh - or
	/// null when there's nothing to send (or sharing is off, which also drops anything waiting). A new batch is on disk
	/// before this returns; if it can't be saved, nothing is sent this time.
	/// </summary>
	/// <param name="accounts">How many accounts this copy has - the cap is every one of them running flat out.</param>
	public static Batch? ForPing(GlobalConfig g, int accounts, DateTime nowUtc) {
		StartCounting(g, nowUtc);

		lock (Gate) {
			if (!Load(nowUtc)) {
				return null;   // can't be read just now: nothing goes, nothing is made - next hour
			}

			if (!Sharing(g)) {
				if ((_state.Pending != null) || (_state.FreshMinutes > 0) || (_state.FreshCards > 0) || _state.AnyExtra) {
					// The catch-up and when counting started stay: the catch-up never overlaps a batch, whenever it goes.
					_state = new State { FreshSince = nowUtc, LiveSince = _state.LiveSince, Old = _state.Old };
					SaveNow(nowUtc, soon: false);
				}

				return null;
			}

			if ((_state.Pending is { } old) && (nowUtc - old.Made > GiveUpAfter)) {
				Log.Debug($"live stats: nocat.lol never took the totals made {old.Made:yyyy-MM-dd HH:mm} UTC - dropped");
				_state.Pending = null;
				SaveNow(nowUtc, soon: false);
			}

			if (_state.Pending != null) {
				return _state.Pending;
			}

			// A new batch only once BatchEvery has gone by since the last one was made: meanwhile it all stays fresh.
			if (nowUtc - _state.FreshSince < _batchGap) {
				return null;
			}

			double hours = Math.Floor(_state.FreshMinutes / 6) / 10;   // whole tenths, never rounded up
			int cards = _state.FreshCards;

			if ((hours <= 0) && (cards <= 0) && !_state.AnyExtra) {
				return null;
			}

			double elapsed = Math.Max(0, (nowUtc - _state.FreshSince).TotalHours);
			int fleet = Math.Max(1, accounts);
			double maxHours = Math.Floor(fleet * MaxGamesPerAccount * elapsed * 10) / 10;
			int maxCards = (int) Math.Min(int.MaxValue, Math.Floor(fleet * MaxCardsPerAccountHour * elapsed));
			bool capped = (hours > maxHours) || (cards > maxCards);
			int Cap(int n, int perHour) => (int) Math.Min(n, Math.Floor(fleet * perHour * elapsed));
			int gems = Cap(_state.FreshGems, MaxGemItemsPerAccountHour), badges = Cap(_state.FreshBadges, MaxBadgesPerAccountHour);
			int packs = Cap(_state.FreshPacks, MaxPacksPerAccountHour), sold = Cap(_state.FreshSold, MaxSoldPerAccountHour);

			State before = new() {
				FreshMinutes = _state.FreshMinutes, FreshCards = _state.FreshCards, FreshSince = _state.FreshSince, LiveSince = _state.LiveSince, Old = _state.Old,
				FreshGems = _state.FreshGems, FreshBadges = _state.FreshBadges, FreshPacks = _state.FreshPacks, FreshSold = _state.FreshSold
			};
			// The time it covers and the accounts go with it: nocat.lol caps it by those, not by how many are signed in when
			// it arrives (none, right after a restart) or how long since it last heard (days, after it was down).
			Batch batch = new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Math.Min(hours, maxHours), Math.Min(cards, maxCards), nowUtc,
				_state.FreshSince, Math.Min(fleet, UserCount.MaxAccounts), gems, badges, packs, sold);

			if (capped) {
				Log.Debug($"live stats: {hours:0.0} hours and {cards} cards in an hour's totals is more than {fleet} account(s) could do - sending {batch.Hours:0.0} and {batch.Cards}");
			}

			if ((gems < _state.FreshGems) || (badges < _state.FreshBadges) || (packs < _state.FreshPacks) || (sold < _state.FreshSold)) {
				Log.Debug($"live stats: {_state.FreshGems} into gems, {_state.FreshBadges} badges, {_state.FreshPacks} packs and {_state.FreshSold} sold is more than {fleet} account(s) could do - sending {gems}, {badges}, {packs} and {sold}");
			}

			// What's left under a tenth of an hour carries over; anything over the cap is gone for good.
			_state.FreshMinutes = capped ? 0 : Math.Max(0, _state.FreshMinutes - (hours * 60));
			_state.FreshCards = 0;
			_state.FreshGems = 0;
			_state.FreshBadges = 0;
			_state.FreshPacks = 0;
			_state.FreshSold = 0;
			_state.FreshSince = nowUtc;
			_state.Pending = batch.Any ? batch : null;

			if (!SaveNow(nowUtc, soon: false)) {
				// Not on disk: sending it could mean sending it again under another name after a restart.
				_state = before;
				_state.Pending = null;

				return null;
			}

			return _state.Pending;
		}
	}

	/// <summary>nocat.lol said it has this batch: it's done, and never sent again.</summary>
	public static void Acknowledged(string b, DateTime nowUtc) {
		lock (Gate) {
			// Can't be read just now: the batch goes again, under the name nocat.lol already has.
			if (Load(nowUtc) && (_state.Pending is { } p) && (p.B == b)) {
				_state.Pending = null;
				SaveNow(nowUtc, soon: false);
			}
		}
	}

	/// <summary>
	/// The one-time catch-up to go with this ping, or null: none (made empty, or taken already), sharing off, or not on
	/// disk (a catch-up that went but wasn't kept could go again under another name after a restart).
	/// </summary>
	public static CatchUp? OldForPing(GlobalConfig g, DateTime nowUtc) {
		if (!Sharing(g)) {
			return null;
		}

		StartCounting(g, nowUtc);

		lock (Gate) {
			if (!Load(nowUtc) || (_state.Old == null)) {
				return null;
			}

			if (_oldUnsaved && !SaveNow(nowUtc, soon: false)) {
				return null;
			}

			return _state.Old;
		}
	}

	/// <summary>nocat.lol said it has the catch-up ("gotold"): done for good - it's never made or sent again.</summary>
	public static void OldAcknowledged(string b, DateTime nowUtc) {
		lock (Gate) {
			if (Load(nowUtc) && (_state.Old is { } o) && (o.B == b)) {
				_state.Old = null;
				SaveNow(nowUtc, soon: false);
			}
		}
	}

	/// <summary>The catch-up waiting for nocat.lol, or null; and when counting started on this install (null: not yet).</summary>
	public static (CatchUp? Old, DateTime? LiveSince) UnsentOld() {
		lock (Gate) {
			return Load(DateTime.UtcNow) ? (_state.Old, _state.LiveSince) : (null, null);
		}
	}

	/// <summary>
	/// Counting starts here when it hasn't yet and sharing is on: the history so far is added up into the catch-up and
	/// frozen, and the fresh counter starts from 0 - both under the history's lock, which every playtime tick and card
	/// drop holds while it feeds the history and this, so nothing falls between them or lands in both.
	/// </summary>
	/// <returns>true when counting started just now.</returns>
	private static bool StartCounting(GlobalConfig g, DateTime nowUtc) {
		if (!Sharing(g)) {
			return false;
		}

		lock (Gate) {
			// Can't be read just now: whether counting started isn't known, so it doesn't start - next time.
			if (!Load(nowUtc) || (_state.LiveSince != null)) {
				return false;
			}
		}

		// The history's lock first, then this one - the order a playtime tick takes them in.
		return History.Locked(() => {
			lock (Gate) {
				if (!Load(nowUtc) || (_state.LiveSince != null)) {
					return false;
				}

				(double minutes, int cards, int accounts, int days) = PastTotals();
				accounts = Math.Clamp(accounts, 0, UserCount.MaxAccounts);
				days = Math.Clamp(days, 0, History.KeepDays + 1);

				// Never more than every account running 32 games all day, every day of the history.
				double maxMinutes = (double) accounts * days * MaxMinutesPerAccountDay;
				long maxCards = (long) accounts * days * MaxCardsPerAccountDay;
				double hours = Math.Floor(Math.Min(double.IsFinite(minutes) ? Math.Max(0, minutes) : 0, maxMinutes) / 6) / 10;
				int capped = (int) Math.Min(Math.Min(Math.Max(0, cards), maxCards), int.MaxValue);

				// Anything counted before now (a batch from a build before the catch-up existed) is in the history too,
				// so it's in the catch-up: the counter starts again from 0 here.
				_state.LiveSince = nowUtc;
				_state.FreshMinutes = 0;
				_state.FreshCards = 0;
				_state.FreshGems = 0;
				_state.FreshBadges = 0;
				_state.FreshPacks = 0;
				_state.FreshSold = 0;
				_state.FreshSince = nowUtc;
				_state.Pending = null;
				_state.Old = ((hours > 0) || (capped > 0))
					? new CatchUp(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), hours, capped, accounts, days, nowUtc)
					: null;
				_oldUnsaved = _state.Old != null;
				SaveNow(nowUtc, soon: false);

				return true;
			}
		});
	}

	/// <summary>What hasn't gone yet: game-hours and cards fresh, and the batch waiting for nocat.lol (or null).</summary>
	public static (double FreshHours, int FreshCards, Batch? Waiting) Unsent() {
		lock (Gate) {
			return Load(DateTime.UtcNow) ? (_state.FreshMinutes / 60, _state.FreshCards, _state.Pending) : (0, 0, null);
		}
	}

	/// <summary>The rest not in a batch yet: items turned into gems, badges crafted, packs opened and items sold.</summary>
	public static (int Gems, int Badges, int Packs, int Sold) UnsentExtras() {
		lock (Gate) {
			return Load(DateTime.UtcNow) ? (_state.FreshGems, _state.FreshBadges, _state.FreshPacks, _state.FreshSold) : (0, 0, 0, 0);
		}
	}

	/// <summary>On the way out: whatever is fresh is kept for next time.</summary>
	public static void Save() {
		lock (Gate) {
			if (_loadedFrom != null) {
				SaveNow(DateTime.UtcNow, soon: false);
			}
		}
	}

	/// <summary>Forget what's in memory, so the next look reads the file again - for the checks.</summary>
	internal static void Reset() {
		lock (Gate) {
			_state = new State();
			_loadedFrom = null;
			_lastSave = DateTime.MinValue;
			_oldUnsaved = false;
			_restorePending = false;
		}
	}

	/// <summary>
	/// A backup's history is about to be put back. That history is the install's that made the backup - this one never
	/// counted it, and the one that made it may have sent it already - so it must never go in a catch-up from here: when
	/// counting hasn't started on this install, it starts now with no catch-up; when it has, nothing changes (a catch-up
	/// waiting was made from this install's own history, before the restore). Marked on disk first and settled at once
	/// when the file can be read, else the next time it can - after a restart too.
	/// </summary>
	public static void Restoring(DateTime nowUtc) {
		lock (Gate) {
			_restorePending = true;

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(RestoredPath)!);
				AtomicFile.Write(RestoredPath, nowUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				Log.Debug($"live stats: couldn't mark the restore on disk ({Log.Describe(e)}) - kept in memory");
			}

			Load(nowUtc);
		}
	}

	/// <summary>The restore's mark, settled: counting starts now with no catch-up, unless it had already.</summary>
	private static void SettleRestore(DateTime nowUtc) {
		if (_state.LiveSince == null) {
			_state = new State { FreshSince = nowUtc, LiveSince = nowUtc };
			_oldUnsaved = false;
			Log.Debug("live stats: a backup's history was put back - counting starts now, with no catch-up (that history isn't this install's)");
		}

		if (!SaveNow(nowUtc, soon: false)) {
			return;   // memory has it; the mark stays until it's on disk
		}

		_restorePending = false;

		try {
			File.Delete(RestoredPath);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// settled on disk all the same: the mark then finds counting started and changes nothing
		}
	}

	/// <summary>
	/// What's on disk, into memory (once): true when it's there. A missing file is "counting hasn't started". A file that's
	/// there but can't be read just now is false - try again later: nothing starts, nothing is saved over it. One that reads
	/// but isn't ours (broken) is kept aside as farm-totals.json.broken and counting starts now with no catch-up: whether
	/// one went already can't be known, and a second would count the history twice.
	/// </summary>
	private static bool Load(DateTime nowUtc) {
		string path = StatePath;

		if (_loadedFrom != path) {
			State loaded;
			bool broken = false;

			if (!File.Exists(path)) {
				loaded = new State { FreshSince = nowUtc };
			} else {
				string text;

				try {
					text = ReadFile(path);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					Log.DebugOnChange("farmtotals:read", $"live stats: couldn't read what's waiting to be sent ({Log.Describe(e)}) - trying again later");

					return false;
				}

				State? saved;

				try {
					saved = JsonSerializer.Deserialize<State>(text);
				} catch (Exception e) when (e is JsonException or NotSupportedException) {
					saved = null;
				}

				if (saved == null) {
					broken = true;
					loaded = new State { FreshSince = nowUtc, LiveSince = nowUtc };

					try {
						File.Move(path, path + ".broken", overwrite: true);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						Log.Debug($"live stats: couldn't keep the broken file aside: {Log.Describe(e)}");
					}

					Log.Debug("live stats: what was waiting to be sent was broken - kept as farm-totals.json.broken; counting goes on from now, with no catch-up");
				} else {
					saved.FreshMinutes = double.IsFinite(saved.FreshMinutes) ? Math.Max(0, saved.FreshMinutes) : 0;
					saved.FreshCards = Math.Max(0, saved.FreshCards);
					saved.FreshGems = Math.Max(0, saved.FreshGems);
					saved.FreshBadges = Math.Max(0, saved.FreshBadges);
					saved.FreshPacks = Math.Max(0, saved.FreshPacks);
					saved.FreshSold = Math.Max(0, saved.FreshSold);

					if ((saved.FreshSince == default) || (saved.FreshSince > nowUtc)) {
						saved.FreshSince = nowUtc;
					}

					if (saved.Pending is { } p && (!IsBatchName(p.B) || !double.IsFinite(p.Hours) || (p.Hours < 0) || (p.Cards < 0)
						|| (p.Gems < 0) || (p.Badges < 0) || (p.Packs < 0) || (p.Sold < 0))) {
						saved.Pending = null;
					}

					if (saved.Old is { } o && (!IsBatchName(o.B) || !double.IsFinite(o.Hours) || (o.Hours < 0) || (o.Cards < 0) || (o.Accounts < 0) || (o.Days < 0))) {
						saved.Old = null;
					}

					loaded = saved;
				}
			}

			_state = loaded;
			_loadedFrom = path;
			_oldUnsaved = false;

			if (broken) {
				SaveNow(nowUtc, soon: false);
			}

			if (File.Exists(RestoredPath)) {
				_restorePending = true;
			}
		}

		if (_restorePending) {
			SettleRestore(nowUtc);
		}

		return true;
	}

	/// <param name="soon">Only every few minutes - a playtime tick comes every 20 seconds.</param>
	private static bool SaveNow(DateTime nowUtc, bool soon) {
		if (soon && (nowUtc - _lastSave < TimeSpan.FromMinutes(5))) {
			return true;
		}

		try {
			string path = StatePath;
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			AtomicFile.Write(path, JsonSerializer.Serialize(_state));
			_lastSave = nowUtc;
			_oldUnsaved = false;

			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Log.DebugOnChange("farmtotals:save", $"live stats: couldn't save what's waiting to be sent: {Log.Describe(e)}");

			return false;
		}
	}
}
