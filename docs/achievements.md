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
main game joins in - hundreds of hours with no achievements looks odd, so it's best left on.

## The order

It earns them in an order a real player could have managed. Mostly that's easiest first, with the second easiest
now and then so it isn't a perfect robot order. On top of that, nothing comes out of sequence:

- "10 kills" waits for "5 kills", whether the number is in the name or only in the description
- "Chapter Two" waits for "Chapter One", and "Act II" for "Act I"
- "beat it on Hard" waits for "beat it on Normal"
- "finish the game" and "the final mission" wait for every chapter, mission and act before them, and that includes
  missions with names instead of numbers - "Complete Blood Feud in Campaign" comes before "Finish the Campaign".
  Multiplayer and side challenges don't hold the ending back.

It also knows how long each game really takes (*Pace by how long games really take*, on). It looks up a typical
player's hours from SteamSpy's public figures - only the game's ID is sent - and paces to them. "Finish the game"
waits until the account has played most of a normal playthrough, chapter 5 of 10 waits for about 40% of it, and a
long game's rare achievements open more slowly than a short game's.

Some games' achievements are set by Steam's servers rather than the client. Counter-Strike 2 is one. Nothing can
unlock those, and the log says so.

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

For an account that isn't pretending to be anyone - a throwaway, or one you're clearing out - there's **Unlock every
achievement, in every game this account owns** at the bottom of the account's settings, with Show advanced ticked.
Every achievement gets the same unlock time, set by Steam, and it can't be back-dated or hidden, so anyone looking
at the profile can tell. It asks you to type `confirm` first.

---

[← Trades](trades.md) · [Phone and notifications →](phone-and-notifications.md)
