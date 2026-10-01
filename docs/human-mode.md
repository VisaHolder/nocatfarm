[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Human mode

Human mode is for an account you want to look like a real player, which usually means your main. Turn it on per
account with the *Human mode* switch, or `set myaccount LegitMode true`.

The account then plays one game at a time, in sittings of believable length, with a main game and a few side games.
Weekdays and weekends look different, and now and then it takes a day off. It takes short breaks and meal breaks,
sometimes showing Away or offline while it's gone, and at night it goes to bed. With *Bank hours overnight* on (it is
by default), it goes invisible while asleep and keeps idling the games in *Games to idle overnight*, so hours still
count while friends see it offline.

It doesn't start a game the moment it signs in. It settles in first, and no game starts until at least 3 minutes
after sign-in and several checks that you aren't already playing on the account yourself. No setting shortens that.

Anything other people can see gets a person's sort of delay. Trades, gifts and friend requests each wait their own
time, and while it's asleep they wait for morning (*Only react while awake*, on). Behind-the-scenes jobs like
badges, free games, booster packs and selling run at any hour, just not the moment it signs in. And when you stop
the account, it finishes up for a few seconds instead of vanishing mid-game.

Some settings make no sense on a believable account, so human mode switches them off and hides them: games to idle,
the custom game name, keep-name-while-farming, farm in sittings, hours a day to farm, farm while appearing offline,
and log out when finished. They come back unchanged when you turn human mode off.

## Setting it up

The main settings are up front and the fine detail is behind Show advanced. From the console:

```
set myaccount GameWeights "730:70, 440:20, 550:10"   # games and their share; the first is the main game
set myaccount WeekdayHours 6                         # about 6 hours Monday to Friday
set myaccount WeekendHours 9
set myaccount DayStartHour 13                        # gets on around 1pm
set myaccount BedHour 2                              # goes to bed around 2am
```

Some days go on the main game alone (*Days on the main game only*, 25%), so side games show up in bursts. That
lowers their weekly share, and the dashboard and `human` show the real weekly figure next to the one you set.

To see what it's up to:

```
human myaccount          # what it's doing today and what it played
human myaccount week     # a sample of the next seven days
human myaccount reroll   # throw today's plan away and roll a new one from the current settings
wake myaccount           # wake it up and start its day now (bedtime stays the same)
habits myaccount         # what it has learned from you playing on the account (see below)
```

A day is planned once, when it wakes, so a change to its hours or main game shows up tomorrow. `reroll` applies it
straight away.

*Hour targets* (Show advanced) get a game to a number of hours, optionally by a date. `730:100@2026-12-01, 440:50`
means Counter-Strike 2 to 100 hours by 1 December, and TF2 to 50 whenever. Cards still come first; after that,
sittings lean toward the games that are behind. `hours myaccount` shows progress and the daily pace a date needs.

`selfcheck myaccount` scores a human-mode account out of 100 for how real it looks from outside. It looks at hours
on the profile, games at once, a made-up game name, no daily rhythm and rep4rep comments, and names the setting
that fixes each problem. Without an account name it checks every human-mode account.

`grind myaccount 730 3` overrides human mode: the account goes on one game for 3 hours, then back to its normal day.
Those are 3 hours of actual play. If the account has only just signed in and is still settling in, the grind waits
for that and its end moves back to match, so the warm-up doesn't come out of the 3 hours. With *Earn achievements
over time* on, a grind earns achievements at its own pace (*While grinding, one achievement every*, 12-24 minutes).

Every wait is a setting, under Show advanced on a human-mode account:

| Setting | Default |
|---|---|
| *After waking, wait at least … up to*, before things people can see | 10-90 minutes |
| *After signing in, behind-the-scenes things wait at least … up to* | 5-60 minutes |
| *Behind-the-scenes things wait for its day too* | off |
| *On a break, go Away after at least … up to* | 2-10 minutes |
| *Answer one trade offer at a time* | on |
| *Settle in for at least … And at most*, after signing in, before the first game | 3-20 minutes |

## A life, not just a day

Four extras make the weeks less alike. All four are off until you switch them on, so an account set up before them
plays exactly as it did.

*Learn from how I play* (off, a little, a lot) notes when you play on the account yourself, and which games. After 7
days of that, its day leans toward yours: about 30% of the way (a little) or 70% (a lot) toward when you get on and
when you stop, and toward how you split your time between the games in *Games and how often*. It never adds a game
that isn't in that list, and how long it plays still comes from the hours settings. It keeps the last 60 days.
`habits myaccount` shows what it has seen (days, your usual hours, top games, and games you play that aren't in the
list), and `habits myaccount forget` wipes it.

*Play new games more at first* gives a game that has just arrived (bought, gifted or free) extra sittings for 3 to 10
days, fewer each day, at most 3 sittings a day. Games still refundable (with refund protection on it waits for that
to end), blacklisted or family-shared games, and games your other accounts are running are left alone.

*Quiet spells and late nights*: once or twice a month a quiet spell of 2 to 5 days with days about half as long, and
now and then a Friday or Saturday night that runs one to two and a half hours later. Days off and the hours still
apply, and `human week` shows the same quiet spells and late nights the real days will have.

*Sometimes play what a friend is playing*: when a Steam friend is in a game this account owns, now and then (at most
twice a day, only while it's up and between sittings) its next sitting is that game. Your own accounts, anything they
are running, blacklisted and refundable games, and the main game are never picked this way.

`human myaccount` says what each one that is on is doing today.

## Staying out of your way

This matters for any account you also play on, human mode or not.

*Stand down when you play* (on) stops nocat.farm playing the moment you start a game on that account yourself. While
you're on it, it doesn't change the account's status either. The right one goes back on once you're done.
*Wait before resuming* (5 minutes) is how long after you stop before it carries on; a human-mode account waits a
random time, up to three times that. *I sign into this one myself* (off) means it never changes the online status,
so your own Steam app keeps Friends & Chat.

*Clear Steam's notifications* (on) marks the notification tray read on every sign-in, and *Clear the new-items
badge* (on) clears the green counter after each drop. If you use the account yourself and want to see those, turn
them off.

---

[← Cards and idling](cards-and-idling.md) · [Trades →](trades.md)
