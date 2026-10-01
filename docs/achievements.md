[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Achievements and the hunter

You can set achievements by hand:

```
cheevo myaccount 440                  # its achievements, easiest first, with how rare each is
cheevo myaccount 440 unlock all       # all the ones that can be set, now
cheevo myaccount 440 unlock ACH_NAME  # just one
cheevo myaccount 440 lock ACH_NAME    # put one back
```

But unlocking a whole list at once shows on the profile forever, all with one timestamp. **Earn achievements over
time** (`UnlockAchievements`, off) is the gentle way. It unlocks them only in a game the account is actually
playing, roughly one per hour of play, and never finishes more than 90% of a game (*Finish no more than*). Rare
achievements only open up as playtime builds, and milestones wait for the achievements they depend on. *How fast*
(careful, normal or brisk) doubles or halves the gaps. *Never in these games* keeps it out of games you pick, *Only
these games* limits it to a list, and *Earn them in the main game too* (on) decides whether a human-mode account's
main game joins in. Hundreds of hours with no achievements looks odd, so it's best left on.

## The order

It earns them in an order a real player could have managed. Mostly that's easiest first, with the second easiest
now and then so it isn't a perfect robot order. On top of that, nothing comes out of sequence:

- "10 kills" waits for "5 kills", whether the number is in the name or only in the description
- "Chapter Two" waits for "Chapter One", and "Act II" for "Act I"
- "beat it on Hard" waits for "beat it on Normal"
- "finish the game" and "the final mission" wait for every chapter, mission and act before them, and that includes
  missions with names instead of numbers: "Complete Blood Feud in Campaign" comes before "Finish the Campaign".
  Multiplayer and side challenges don't hold the ending back.

It also knows how long each game really takes (*Pace by how long games really take*, on). It looks up a typical
player's hours from SteamSpy's public figures (only the game's ID is sent) and paces to them. "Finish the game"
waits until the account has played most of a normal playthrough, chapter 5 of 10 waits for about 40% of it, and a
long game's rare achievements open more slowly than a short game's.

Some games' achievements are set by Steam's servers rather than the client. Counter-Strike 2 is one. Nothing can
unlock those, and the log says so.

## DLC it doesn't own

Many games keep their DLC's achievements in the base game's list. Call of Duty is one: 63 of its 157 come with
Modern Warfare II and III. An achievement from DLC the account doesn't own is one nobody could have earned, so
nothing unlocks it: not the pacer, not the hunter, not `cheevo ... unlock`, not unlocking everything. The first time
a game is played it looks up the game's DLC on Steam's store, slowly (a game with a hundred DLC takes a few
minutes), and unlocks nothing there until it has. The answer is kept for a week, and looked up again sooner when an
achievement turns up that wasn't in the game's list last time (a new DLC, usually).

Steam publishes which achievements come with a DLC only for newer DLC, so older games with DLC the account doesn't
own are left alone entirely. More exactly: when the account is missing a DLC that Steam gives no achievement numbers
for, or whose store page is the base game's own (Call of Duty's Black Ops 6 and 7), or that isn't on the store any
more, it can't be told which achievements come with it. Then only achievements that are certainly from DLC the
account owns are unlocked - where Steam's names for that DLC's achievements line up exactly, and not the hidden ones
just in front of them, which could as well be the DLC nobody can place - and the rest of the game is left alone:
Fallout: New Vegas, Borderlands 2, Civilization V, Cuphead, and Call of Duty on an account without every DLC. The
status says so ("can't tell which achievements come with its DLC - left alone") and the hunter moves on to another
game. Buying the DLC, or anything else that changes the account's licences, has it looked at again - and so does the
game's DLC being looked up again and coming out different. When Steam didn't answer for some DLC's names, that look-up
is done again in an hour rather than a week.

Owning a DLC means the account's own licence, for good. A family member's copy, a free weekend, a timed trial, a guest
pass, and a licence that ran out, was refunded or cancelled, or hasn't gone through yet all stay on Steam's list, and
none of them counts.

Plainly harmless DLC never hold a game: a soundtrack, an artbook, wallpapers, and packs whose name says they are
only looks or in-game money - skins, tracer packs, operator packs, camos, emblems, calling cards, stickers, charm
packs, outfits, costumes, avatars, profile backgrounds, BlackCell, a battle pass, Pro and Starter packs, a Vault
Edition Pack, COD Points, and amounts or packs of coins, gems, credits or gold ("1,100 Gold", "Currency Pack"). The
game's own name is taken off first: "Avatar: Frontiers of Pandora - The Sky Breaker" is a story DLC, not an avatar. A
word that can mean something else only counts with a pack after it, or as an amount of money - "Charm City", "Closing
Credits" and "The Profile of Evil" aren't harmless. A name that also mentions anything to play never counts as
harmless: a map, a campaign, a story, an episode, chapter or act, missions, zombies, a mode, an arena, an adventure, a
challenge, a heist, a trial, survival, an operation, a saga, a tale, an island, a season pass, an expansion, an
upgrade, "Complete", "Definitive", "Game of the Year", a Gold Edition, a collection, a bundle (bar an operator bundle
or a pack of money), "DLC 2", or a "+" or "&" (two things in one) and the like. Nor does a name in a script those words
can't read (Japanese, Chinese, Russian...). A name that doesn't say ("Gunzerker Madness Pack", "Call of Duty League -
Boston Breach Team Pack") isn't harmless either. And a DLC Steam does give achievement numbers for is always placed
by those, whatever it's called. About 40 of Call of Duty's nearly 100 DLC come out harmless; the rest (team packs,
charity packs, Modern Warfare II and III's multiplayer, co-op and vault packs, Warzone) still hold it on an account
without them.

When you know an account owns every DLC of a game, say so: add the game to *Games I own all the DLC for*
(`AchievementDlcTrusted`, per account, empty to start). Nothing in those games is held for DLC - they're unlocked as
if every DLC were owned, and a game held for DLC is let go within a minute of adding it. Take it off the list and
it's held again. Only you can know this, so nothing adds a game there for you. `dlcach` says when a game is on the
list, and a held game's `dlcach` and log line say how to add it.

Those achievements don't count towards *Finish no more than* or the hunter's "far enough", and the status says how
many were left alone. `dlcach myaccount 1938090` lists a game's DLC with achievements, whether the account owns
each, and how many of them it already has - and when the whole game is held, which DLC it's missing and why.
`dlcach myaccount` lists the games it plays where some are left alone.

With an account picked in Settings, the Achievements section shows what the pacer is doing in each game and what it
unlocked recently.

## The hunter

*Achievement boost* (off) picks games to play so there's always something to earn. It needs *Earn achievements over
time* on as well, and has two modes. *games you pick* works through *Boost these games*. *all single-player* finds
single-player games with achievements in the library, played or not. It skips DLC, demos and games with fewer than
200 reviews (*Only hunt games with at least*; games you pick by hand are always allowed). *Only hunt games you've
played* narrows it to games the account has launched before.

It keeps a few games on the go, like a person does. It takes turns between the first 3 on its list (*Games in
rotation*), plays each for about 2 hours a turn (*Play each for about*), and never plays the same one twice in a
row.

When a game reaches about 65-75% of its achievements (*Move to another game at about* / *...up to*), the hunter has
had enough of it for now. The exact point is random and different for every game. That game then rests for 3-14 days
(*Come back to a game after* / *...to*) and the next game on the list takes its slot. Each time it comes back, it
goes a little further in, up to your *Finish no more than* limit. A game that gets there, or has nothing left to earn,
is left for good (raise *Finish no more than* and it comes back); `hunt` lists them apart. Only a `grind` you type goes
to 100%.

*Hunt at most, hours a day* caps the hunting per day (0, the default, means no limit). Once the day's hours are
played, a human-mode account drops the hunt game from its games until tomorrow, and a robot account stops hunting
for the day.

On a human-mode account the hunter never takes the account over. The game it's hunting joins the games the account
plays, like one more side game in *Games and how often*, at a weight of 15 (*Human mode: hunt game's weight*). It's
played in ordinary sittings, with breaks and bedtime, and never cuts another game short. After about *Play each for
about* of play, spread over those sittings, the next game on the list takes its turn.

At first it may say "the store hasn't answered yet" for a lot of games. It looks each one up slowly so Steam doesn't
block it, and starts as soon as it has a few. `hunt myaccount` shows what it would play next and why other games
were ruled out.

**Steam Families.** *Include family-shared games* (off) lets the hunter use games shared with the account; owned
games always go first. *Leave brand-new family games alone* (on) skips a shared game while it could still be
refunded, and *Give a shared game back when they want it* (on) hands a game back the moment someone in the family
starts it, then leaves it alone until 20 minutes after they stop.

## Unlocking everything

For an account that isn't pretending to be anyone, like a throwaway or one you're clearing out, there's **Unlock every
achievement, in every game this account owns** at the bottom of the account's settings, with Show advanced ticked.
Every achievement gets the same unlock time, set by Steam, and it can't be back-dated or hidden, so anyone looking
at the profile can tell. It asks you to type `confirm` first.

---

[← Trades](trades.md) · [Phone and notifications →](phone-and-notifications.md)
