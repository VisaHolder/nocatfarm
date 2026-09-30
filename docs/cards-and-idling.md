[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Cards and idling

## Card farming

Card farming is on by default (*Farm trading cards*). It reads the account's badge pages, and then:

1. Games that need more playtime before cards drop are played together, up to 32 at once (31 with a custom game
   name).
2. Each game is then played on its own until its cards stop dropping.
3. With no cards left, it idles the account's games, or signs out if *Log out when finished* is on.

Drops are noticed the moment Steam announces them. `cards myaccount` shows what's left per game and about how long
it will take. The estimate starts at Steam's usual 30 minutes a card and then learns the account's own pace.

To turn it off for one account, switch off *Farm trading cards* or type `set myaccount FarmCards false`.

Most of the finer controls are under Show advanced: *Farm in this order*, *Farm these first*, *Only farm those*,
*Never touch these*, *Skip games you've never played*, *Hours before cards drop* (set it to 0 if the account has
spent over $5 on Steam), *Give up after* (8 hours per game), *Farm cards only from … until* (a clock window),
*Farm while appearing offline*, *Log out when finished*, and *Farm in sittings, not flat out* with *Hours a day to
farm*. *Farm at most* (a global setting under Steam connection) caps how many accounts farm at once.

On a human-mode account, *When to farm cards* decides when the cards happen:

| Choice | What happens |
|---|---|
| **day, in its sittings** (default) | Cards farm in its normal sittings, with breaks, meals and bedtime. |
| **night, while it's asleep** | Cards farm only while it's asleep and invisible. By day it plays its usual games. |
| **any time** | Non-stop until the cards are done. |
| **mixed** | Some sittings farm cards (*Share of sittings that farm cards*, 40%), the rest play its usual games. |

After a game's last card it keeps playing that game for 15-20 minutes before a break (*After the last card, keep
playing*).

If you want one game's cards first, `drops` lets you pick:

```
drops myaccount 460920 2    # get 2 cards from game 460920 first
drops myaccount             # the next game with cards
drops myaccount off         # stop early
```

A human-mode account plays that game in its normal sittings until those cards drop. Other accounts play it non-stop,
and give up if the drops stop coming.

**Refund protection** (*Protect refundable games*, off by default) leaves alone any game bought in the last 14 days
with under 2 hours played, so you can still refund it. Farming, idling, grinds and the hunter all skip it. Gifted
games count too (*...gifted games too*, on).

## Idling and a custom game name

This part is for farm accounts. Human-mode accounts don't have it.

*Games to idle* (`play myaccount 730, 440`, or `play myaccount none` to clear it) takes up to 32 games, which play
for playtime once the cards are done. Store links work as well as numbers.

**More than 32 games.** Steam plays 32 games at once (31 with a custom name). Two settings, both off by default,
go past that:

- *Idle my whole library* adds every game the account owns after the ones in *Games to idle*. It skips *Never touch
  these*, refundable games, and family-shared games unless *Include family-shared games* is on.
- *Rotate the idle list* idles one batch at a time and moves to the next batch every 24 hours (*Rotate every*,
  under Show advanced). Games with an hour target and the least played go first, so every game gets hours over the
  days. It remembers where it is across restarts. With it on, *Games to idle* can hold more than 32 too.

Card farming still goes first; the rotation carries on when the cards are done. The account card shows e.g.
"idling 31 of 214 games - next batch at 14:10".

```
rotation myaccount        # which batch, when the next one starts, and what's in it
rotation myaccount next   # move on to the next batch now
```

*Show as* (`name myaccount nocat.lol`) makes friends see that name instead of the real game, while the real games
still count playtime. `name myaccount off` removes it, and *Keep the name while farming* (on) keeps it showing
during card farming too. Now and then Steam drops the name and shows the real game; the account card then warns
"Steam shows …" and nocat.farm puts the name back.

*Play as if on* sets the device badge friends see: PC, phone, Big Picture, VR or controller. (Steam Deck is in the
list, but Steam shows the controller badge for it.) *Appear as* (`persona myaccount invisible`) is the status:
online, offline, busy, away, snooze, looking to trade, looking to play or invisible. An invisible account still
plays and farms.

Human mode hides the idle list and the custom game name, since an account showing a made-up game or playing 30
games at once doesn't look like a person. The idle list is set aside and comes back exactly as it was if you turn
human mode off.

## Free stuff

| What | Setting | Default |
|---|---|---|
| Wallet gift cards and guest passes | *Accept gifts and guest passes* | on |
| Games friends gift the account | *Accept gifted games* | on |
| Daily sale sticker, 0-point Points Shop items | *Claim free event items* | on |
| Discovery queue (earns sale items) | *Go through the discovery queue*: off / during sales / every day | during sales |
| Paid games given away free-to-keep | *Claim free games* | off |
| Paid DLC marked down to free | *...free DLC too* | off |
| The DLC's game, if that's free too | *...and its game, if that's free too* | on |

Gifts wait 2-15 minutes before they're accepted (*Accept a gift after*), and a gift is never declined. Turn
*Accept gifted games* off if you'd rather decide on each gifted game yourself.

Free games are found from Steam itself, using the store's list of games at 100% off and Steam's change feed. Only
released, paid games showing 100% off get claimed, never free-to-play games, demos or "free editions". Steam allows
about 30 activations per 90 minutes, so it stops at 20 to leave room for you. Free DLC needs the game; if the game
is missing but free right now, it claims the game first and then the DLC.

`freeitems [account|all]` looks for event items now, and `queue [account|all]` does the discovery queue now.
`addlicense myaccount <subID>` adds a free licence by hand (`a/<appID>` for a free app); Steam refuses paid ones.

## Booster packs, badges, selling and level-up

**Booster packs.** List games in *Make booster packs for* and each gets a pack a day, as Steam allows, using
tradable gems first (*Booster packs use*). `booster myaccount` shows the gems and which games can be made now, and
`booster myaccount 730, 440` makes those packs straight away. *Open booster packs* (off) opens any that land in the
inventory.

**Badges.** *Craft badges from card sets* (off) turns full sets into badges once a day. That's what raises your
Steam level; cards sitting in the inventory raise nothing.

**Selling.** `sell myaccount` shows which spare cards it would list and what you'd get after Steam's fees.
`sell myaccount do` lists them (5 at a time by default), and `sell myaccount relist` takes down week-old listings
the market has gone under. *Sell duplicate cards* (off) does it by itself every 8-14 hours, a cent under the
cheapest listing. It keeps what you need for badges and never sells foils. Listings need confirming in the Steam app
unless the authenticator is in nocat.farm.

**Level-up planning.** `levelup myaccount 50` works out what reaching level 50 would cost: badges it can craft now,
sets it has nearly finished, and the cheapest full sets on the market for the rest. Prices are looked up slowly in
the background, so wait for "level plan … is ready" in the log and run it again.

`level`, `balance` and `points` show each account's Steam level, wallet and Points Shop points. `owns <game>` tells
you which of your accounts already own a game and how long each has played it.

## Product keys

```
redeem AAAAA-BBBBB-CCCCC            # tries each account until one can use it
redeem myaccount AAAAA-BBBBB-CCCCC  # only myaccount
redeem keys.txt                     # a text file full of keys
keys                                # keys still waiting
```

More than five keys queue up and activate slowly, because Steam limits activations. Keys given to a named account
only ever go to that account, queued ones included. `keys clear` empties the queue.

---

[← Accounts and signing in](accounts.md) · [Human mode →](human-mode.md)
