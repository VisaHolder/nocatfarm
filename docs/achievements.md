[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Achievements and the hunter

You can set achievements by hand:

```
achievements myaccount 440                  # its achievements, easiest first, with how rare each is
achievements myaccount 440 unlock all       # all the ones it's allowed to set, now
achievements myaccount 440 unlock ACH_NAME  # just one
achievements myaccount 440 lock ACH_NAME    # put one back
```

But unlocking a whole list at once shows on the profile forever, all with one timestamp. **Earn achievements over
time** (`UnlockAchievements`, off) is the gentle way. It unlocks them only in a game the account is actually
playing, roughly one every hour or two of play, and never finishes more than 90% of a game (*Finish no more than*). Rare
achievements only open up as playtime builds, and milestones wait for the achievements they depend on. *How fast*
(careful, normal or brisk) doubles or halves every wait - see [How fast](#how-fast). *Never in these games* keeps it out of games you pick, *Only
these games* limits it to a list, and *Earn them in the main game too* (on) decides whether a human-mode account's
main game joins in. Hundreds of hours with no achievements looks odd, so it's best left on.

*Skip multiplayer achievements* (on for a human-mode account, off for a robot) never unlocks achievements that need
other players: multiplayer, co-op, zombies, versus, ranked and online ones, and prestige in a game with multiplayer.
Those games keep a record of every match, and an achievement with no match behind it stands out. It goes by the
achievement's words and its name inside the game: Portal 2's co-op course, Call of Duty's multiplayer, zombies and
co-op campaign are skipped; "in the Campaign or in Co-op" isn't, and Team Fortress 2's class achievements aren't
(only its "five friends in one game" ones). Nor is anything that can be done alone too, with words in between: "Alone in
Single Player or together in Multiplayer" (The Forest), "Complete 10 missions or multiplayer games" (Halo), "7 minutes
on solo, or 4 minutes on multiplayer" (PAYDAY 2). Words only count where they mean other players: "co-op moves", "the
chicken coop", "an online forum" and "a Master-ranked weapon" don't, and "versus" in a name alone ("Man versus
Machine") doesn't either. Skipped ones don't count towards *Finish no more than*. Turning human mode on or off sets it
to match - unless you change it in the same save; after that it stays what you set.

### How fast

*How fast* stretches or shortens every wait between two unlocks in a game, and nothing else. Two waits apply, and the
later of the two wins: time on the clock since the last one, and minutes the game was really played since then. For
most games (the usual figures - a few games have their own):

| | Normal | Careful | Brisk |
|---|---|---|---|
| First 3 in a game: clock gap | 20-120 min | 40-240 min | 10-60 min |
| First 3 in a game: play needed | 25 min | 50 min | 12 min |
| After that: clock gap | 60-150 min | 120-300 min | 30-75 min |
| After that: play needed | 25 min | 50 min | 12 min |
| Quick follow-ups (a level's cluster) | 1-4 min | 2-8 min | 1-2 min |
| During a `grind` (default 12-24 min) | 12-24 min | 24-48 min | 6-12 min |

So in a long sitting, once the first few are in, it's about one every 1-2.5 hours on normal, every 2-5 hours on
careful and every 30-75 minutes on brisk, with the odd cluster of two or three. The chance of a cluster (45% early on, 12% after, 25% in a grind) doesn't change.
Only common achievements cluster: *Quick bursts only for achievements at least* (10% of players by default, behind Show
advanced) - a rarer one never comes a few minutes after another. It ends the cluster and waits the full time, in a
`grind` too. 0 lets any achievement cluster; 100 turns clusters off. No
pace changes which achievements are open: the rarity floor goes by hours played, and the order rules, counters,
DLC holds and the multiplayer skip are the same at every pace. A new choice counts from the next look, within the
minute - the wait already running is stretched or shortened to match, no restart needed. The hunter's rotation (how
long it plays each game, rest days) doesn't use it.

## The order

It earns them in an order a real player could have managed: most players first, the fewest last. Now and then it
takes the second most common instead, so it isn't a perfect robot order - but only when the two are nearly as common
(the second has at least 85% of the first's players), and never when the second is an ending, a step of the story, a
New Game+ one or a step of a ladder. On top of that, nothing comes out of sequence:

- "10 kills" waits for "5 kills", whether the number is in the name or only in the description
- "Chapter Two" waits for "Chapter One", and "Act II" for "Act I"
- "beat it on Hard" waits for "beat it on Normal", also when they're worded differently ("Finish the campaign on Hard
  difficulty" and "Complete the game on Normal"). Only when the easier one is about as common or more: an Easy that
  fewer players have than Normal (most never play Easy) doesn't hold Normal back.
- "finish the game" and "the final mission" wait for every chapter, mission and act before them, and that includes
  missions with names instead of numbers: "Complete Blood Feud in Campaign" comes before "Finish the Campaign".
  Multiplayer and side challenges don't hold the ending back, and nor do an add-on's own steps.
- an add-on's ending waits for the base game's. Which achievements are an add-on's is what the game's DLC map says
  (below), not only the words DLC and expansion.
- anything in New Game+ waits for every ending of the game it hasn't got yet
- an achievement for having the others ("Obtain all other achievements", "Unlock every achievement", a Platinum for
  all trophies) waits until every other achievement in the game is unlocked; "all base game achievements" (Ghost of
  Tsushima's Living Legend) waits for every other base-game one
- an achievement with a counter ("Complete 100 parries") is never unlocked before the game's own counter for it gets
  there. Steam shows that counter next to the achievement, so "37 of 100" beside an unlocked one is a giveaway. Nothing
  writes the counter, so these wait for real play - and a ladder of counted steps waits with them. While they're short
  they don't count towards *Finish no more than* or the hunter's "far enough"; that's worked out again every look.

When nothing left in a game could open at any number of hours - all of it waits on the game's own counters, is too
rare for this account ever (under 1%), or waits on others that stay locked - the game is done for now and the hunter
moves on. Only for a few days (3-5), or until you play the game yourself: then it's looked at again, in case the
counters moved.

Steam's figures for how many players have each achievement are what the order is made of. When there are none at all
for a game (Steam didn't answer, or has none), nothing is unlocked there and it asks again later. One achievement
without a figure in a game that has them (one added recently) can go, but only after everything else that's open.

An achievement Steam says a client can't set, whatever the game's list says, is left alone for a week instead of
being tried every hour - only when Steam says so (it puts the stat back, or answers "access denied"), or refuses one
while others in the same game went through. A plain failure, a timeout, or a family member taking a shared game back
leaves nothing behind, and a game that's "Steam only" because of refusals is looked at again when the first one runs
out. A game with only one to three achievements stops at *Finish no more than* as it rounds: three at 90% is all three,
three at 50% is two.

It also knows how long each game really takes (*Pace by how long games really take*, on). It looks up a typical
player's hours from SteamSpy's public figures (only the game's ID is sent) and paces to them. "Finish the game"
waits until the account has played most of a normal playthrough, chapter 5 of 10 waits for about 40% of it, and a
long game's rare achievements open more slowly than a short game's.

Some games' achievements are set by Steam's servers rather than the client. Counter-Strike 2 is one. Nothing can
unlock those, and the log says so.

## DLC it doesn't own

Many games keep their DLC's achievements in the base game's list. Call of Duty is one: 63 of its 157 come with
Modern Warfare II and III. An achievement from DLC the account doesn't own is one nobody could have earned, so
nothing unlocks it: not the pacer, not the hunter, not `achievements ... unlock`, not unlocking everything. The first time
a game is played it looks up the game's DLC on Steam's store, slowly (a game with a hundred DLC takes a few
minutes), and unlocks nothing there until it has. The answer is kept for a week, and looked up again sooner when an
achievement turns up that wasn't in the game's list last time (a new DLC, usually).

Steam publishes which achievements come with a DLC only for newer DLC. When the account is missing a DLC that Steam
gives no achievement numbers for, or whose store page is the base game's own (Call of Duty's Black Ops 6 and 7), or
that isn't on the store any more, it can't be told which achievements come with it: Fallout: New Vegas, Borderlands
2, Civilization V, Cuphead, and Call of Duty on an account without every DLC. Such a game earns anyway, base game
first - see [Add-ons Steam doesn't explain](#add-ons-steam-doesnt-explain) below. Buying a DLC, or anything else that
changes the account's licences, has a game looked at again - and so does the game's DLC being looked up again and
coming out different. When Steam didn't answer for some DLC's names, that look-up is done again in an hour rather than
a week.

Owning a DLC means the account's own licence, for good. A family member's copy, a free weekend, a timed trial, a guest
pass, and a licence that ran out, was refunded or cancelled, or hasn't gone through yet all stay on Steam's list, and
none of them counts.

Plainly harmless DLC never count as add-ons Steam doesn't explain: a soundtrack, an artbook, wallpapers, and packs whose name says they are
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
charity packs, Modern Warfare II and III's multiplayer, co-op and vault packs, Warzone) still count on an account
without them.

### Add-ons Steam doesn't explain

A game like that isn't held. It earns, from the most players to the fewest, and only what is certainly an add-on's
is held - the same on a human-mode account and a robot:

- an add-on's achievements that Steam does list, on an account without that add-on (Call of Duty with Modern Warfare II
  but not III: III's 39 stay locked);
- an achievement whose name or description names an add-on the account doesn't own ("Complete Old World Blues");
- an achievement with "DLC" in its name inside the game (Cuphead's CompleteWorldDLC) while the account is missing any
  of the game's add-ons - not counting soundtracks, skins and the like.

The game's own layout only changes the order. Steam keeps a game's achievements in numbered slots, and a game's own
come first, in a row. An add-on added later often gets slots further on, after a gap (Cuphead's base game is in slot
2, The Delicious Last Course in slot 5), so what comes after the first gap of two empty slots or more is probably an
add-on's: it's earned after every base-game one that's ready, like a player who finishes the base game first. It's
never held for that - the layout is a guess, and it's wrong both ways: PAYDAY 2, Dead by Daylight and Left 4 Dead 2
put free updates of the base game after a gap, and Fallout: New Vegas, Borderlands 2 and Cyberpunk 2077 put their
add-ons straight after the base game with none. On an account missing those add-ons, `dlc leave` is the safe choice
for a game like that.

Cuphead without The Delicious Last Course: the four with "DLC" in their names (CompleteWorldDLC, SRankAnyDLC...) stay locked; the add-on's other 10 come
after the base game's 28. Call of Duty with Modern Warfare II only: II's own come first, III's never, Black Ops 6 and
7's after II's. Nobody is asked about any of this.

To have a game like that left alone instead, type `dlc leave <account> <game>`. Then only achievements that are
certainly from an add-on the account owns are unlocked there, and the rest of the game is left alone (the status says
"can't tell which achievements come with its DLC - left alone"). The achievements section lists the games left alone,
each with *undo*; in the Console that's `dlc undo <account> <game>`. The game can be typed by name (any case, or two
letters or more of it) or appID. Only a game with add-ons Steam doesn't explain can be left alone. Neither can be done
from Steam chat (see [Commands by Steam chat](settings.md#commands-by-steam-chat)). Games left paused before this
version stay left alone.

Those achievements don't count towards *Finish no more than* or the hunter's "far enough", and the status says how
many were left alone. `dlc myaccount Call of Duty` lists a game's DLC with achievements, whether the account owns
each, and how many of them it already has - and which add-ons Steam doesn't explain, why, and how many achievements it
can't place. `dlc myaccount` lists the games it plays where add-ons matter. (`dlcach` still works.) In
`achievements myaccount <appID>`, `[d]` and `[X]` mark achievements that certainly come with an add-on the account
doesn't own (locked, and unlocked anyway); `[h]` marks ones held because they may come with one (they name it, say
"DLC", or the game was left alone); `[?]` marks ones that are probably an add-on's by the game's layout - earned last,
after the base game's.

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
games always go first. With *Protect refunds* on, a shared game is skipped for its first 14 days in case whoever
bought it wants a refund. *Give a shared game back when they want it* (on) hands a game back the moment someone in the family
starts it, then leaves it alone until 20 minutes after they stop.

## Unlocking everything

For an account that isn't pretending to be anyone, like a throwaway or one you're clearing out, there's **Unlock every
achievement, in every game this account owns** at the bottom of the account's settings, with Show advanced ticked.
Every achievement gets the same unlock time, set by Steam, and it can't be back-dated or hidden, so anyone looking
at the profile can tell. It asks you to type `confirm` first. Even then it leaves alone what nobody could have
earned: an add-on's achievements the account doesn't own (as above - what only looks like an add-on's by the layout is
unlocked), an achievement whose counter in the game isn't there yet, multiplayer ones when *Skip multiplayer
achievements* is on, and "obtain all other achievements" while any of the others stay locked. It says how many it left
and why. `achievements myaccount <appID> unlock all` goes by the same rules, and so does unlocking one by name: it says
why when it won't (held for an add-on, multiplayer on an account that skips those, short of its counter, or waiting
on the others).

---

[← Trades](trades.md) · [Phone and notifications →](phone-and-notifications.md)
