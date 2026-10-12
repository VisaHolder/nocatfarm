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
spent over $5 on Steam), *Give up after* (8 hours per game), and, on robot accounts, *Farm cards only from … until*
(a clock window), *Farm while appearing offline*, *Log out when finished*, and *Farm in sittings, not flat out* with
*Hours a day to farm*. *Farm at most* (a global setting under Steam connection) caps how many accounts farm at once.

On a human-mode account, *When to farm cards* decides when the cards happen (robot accounts farm the moment there
are cards, so they don't show it, and the clock window isn't used there):

| Choice | What happens |
|---|---|
| **day, in its sittings** (default) | Cards farm in its normal sittings, with breaks, meals and bedtime. |
| **night, while it's asleep** | Cards farm only while it's asleep and invisible. By day it plays its usual games. |
| **any time** | Non-stop until the cards are done. |
| **mixed** | Some sittings farm cards (*Share of sittings that farm cards*, 40%, shown once mixed is picked), the rest play its usual games. |

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

**Refund protection** (*Protect refunds*, under *What it plays*, off by default) leaves alone any game bought or
gifted in the last 14 days with under 2 hours played, so it can still be refunded, and a game new to the family
library for its first 14 days. Farming, idling, grinds and the hunter all skip it.

## Idling and a custom game name

This part is for farm accounts. Human-mode accounts don't have it.

*Games to idle* (`play myaccount 730, 440`, or `play myaccount none` to clear it) takes up to 32 games, which play
for playtime once the cards are done. Store links work as well as numbers.

**More than 32 games.** Steam plays 32 games at once (31 with a custom name). Two settings, both off by default,
go past that:

- *Idle my whole library* adds every game the account owns after the ones in *Games to idle*. It skips *Never touch
  these*, family-shared games unless *Include family-shared games* is on, and games *Protect refunds* is holding.
- *Rotate the idle list* idles one batch at a time and moves to the next batch every 24 hours (*Rotate every*,
  under Show advanced). Games with an hour target and the least played go first, so every game gets hours over the
  days. It remembers where it is across restarts. With it on, *Games to idle* can hold more than 32 too.

Card farming still goes first; the rotation carries on when the cards are done. The account card shows e.g.
"idling 31 of 214 games - next batch at 14:10".

```
rotation myaccount        # which batch, when the next one starts, and what's in it
rotation myaccount next   # move on to the next batch now
```

*Show as* (`gamename myaccount nocat.lol`) makes friends see that name instead of the real game, while the real games
still count playtime. `gamename myaccount off` removes it, and *Keep the name while farming* (on) keeps it showing
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
| Daily sale sticker, 0-point Points Shop items | *Claim free event items* (in *Sale events*) | on |
| Discovery queue (earns sale items) | *Go through the discovery queue* (in *Sale events*): off / during sales / every day | during sales |
| Paid games given away free-to-keep, and paid DLC marked down to free | *Claim free games*: off / games / games and DLC | off |

Gifts wait 2-15 minutes before they're accepted (*Accept a gift after*), and a gift is never declined. Turn
*Accept gifted games* off if you'd rather decide on each gifted game yourself.

Free games are found from Steam itself, using the store's list of games at 100% off and Steam's change feed. Only
released, paid games showing 100% off get claimed, never free-to-play games, demos or "free editions". Steam allows
about 30 activations per 90 minutes, so it stops at 20 to leave room for you. Free DLC (with *games and DLC*) needs
the game; if the game is missing but free right now, it claims the game first and then the DLC.

`freegames [account|all]` looks for free games now and says what it claimed, or what was free but couldn't be
claimed and why. It keeps to the same limits, and on an account with *Claim free games* off it takes games only.
`freeitems [account|all]` looks for event items now, and `queue [account|all]` does the discovery queue now.
`addlicense myaccount <subID>` adds a free licence by hand (`a/<appID>` for a free app); Steam refuses paid ones.

## Sale events

During Steam's events it picks up what's free, by itself:

- **Steam sales**: the daily sticker, the discovery queue, and the item the sale gives for the queue.
- **The Steam Awards**: in late November it nominates a game in each category (for the nomination badge), and in the
  winter sale it votes once in each category (each vote gives a sticker).
- **Steam Next Fest**: its free item, if it has one. Steam hasn't given one at recent Next Fests, so this is ready in
  case it comes back.

Outside an event it only looks now and then (every 6 hours), so nothing happens most of the year. It reads the event's
own Steam pages to tell whether something is on, and does nothing when it isn't.

**Nominations use only games the account has played.** Steam itself lists the played games that fit each category;
it picks the most played one (or any of them, if you choose), tries not to use the same game twice, and leaves a
category empty when nothing it played fits. **Votes** go to the finalist the account played most, or any finalist when
it played none of them. Limited accounts (that never spent $5 on Steam) can't nominate or vote: Steam says no, and it
tries again at the next look rather than over and over.

**Farm accounts** do it all straight away, a few seconds apart. **Human-mode accounts** take part too (*Human-mode accounts
too*, on by default), but one step at a time, inside the account's day, 5 to 30 minutes apart. Turn it off and a
human-mode account leaves the awards, the queue item and Next Fest alone. The daily sticker and the discovery queue keep their own switches, as before.

`events [account]` shows what's on (a sale, nominations, votes, Next Fest) and what each account has done.

What each setting does (*Account settings → Sale events*):

| Setting | What it does | Default |
|---|---|---|
| *Take part in Steam events* | The switch for everything here. Off: none of it on this account. | on |
| *Human-mode accounts too* | Human-mode accounts also nominate, vote and claim the queue and Next Fest items, inside their day, minutes apart. Only shown in human mode. | on |
| *Nominate in the Steam Awards* | A game in each category, late November. | on |
| *Vote in the Steam Awards* | Once in each category, in the winter sale. | on |
| *Claim free event items* | The daily sale sticker and anything at 0 points in the Points Shop. | on |
| *Go through the discovery queue* | Off, during sales, or every day. | during sales |
| *Claim the sale's queue item* (advanced) | Picks up the queue's item as soon as the queue is done. | on |
| *Next Fest items* (advanced) | Next Fest's free item, when there is one. | on |
| *Which game to nominate* (advanced) | The game it played most, or any game it played. | most played |
| *Robot accounts wait between steps (seconds)* (advanced) | Seconds between two nominations or votes on a robot account, picked at random in the range. | 5 to 20 seconds |
| *Human mode waits between steps (minutes)* (advanced) | Minutes between two steps on a human-mode account. | 5 to 30 minutes |
| *Look for events every (hours)* (advanced) | Hours between looks outside an event (about every hour during one). | 6 hours |

What can't be checked until the next event: the awards pages and answers it reads were built from Steam's current
pages and code, and tested against saved copies. If Steam changes them before the next sale, it does nothing (it
never guesses), and `events` says what it saw.

## Booster packs, badges, selling and level-up

**Booster packs.** List games in *Make booster packs for* and each gets a pack a day, as Steam allows, using
tradable gems first (*Booster packs use*). `booster myaccount` shows the gems and which games can be made now, and
`booster myaccount 730, 440` makes those packs straight away. Opening packs has its own section below.

**Badges.** *Craft badges from card sets* (off) turns full sets into badges once a day. That's what raises your
Steam level; cards sitting in the inventory raise nothing.

**Selling.** `sell myaccount` shows which spare cards it would list and what you'd get after Steam's fees.
`sell myaccount do` lists them (5 at a time by default), and `sell myaccount relist` takes down week-old listings
the market has gone under. To have it done by itself, see *Deal with spare items* below - it sells or turns into gems,
whichever pays more. Listings need confirming in the Steam app unless the authenticator is in nocat.farm.

**Level-up planning.** `levelup myaccount 50` works out what reaching level 50 would cost: badges it can craft now,
sets it has nearly finished, and the cheapest full sets on the market for the rest. Prices are looked up slowly in
the background, so wait for "level plan … is ready" in the log and run it again.

`level`, `balance` and `points` show each account's Steam level, wallet and Points Shop points. `owns <game>` tells
you which of your accounts already own a game and how long each has played it.

## Steam Points into levels

The Points Shop sells one thing that raises your Steam level: the **seasonal badge** (in October 2026, *Summer
Collection - 2026*). Each level costs 1,000 points and is worth 100 XP, up to level 40 (4,000 XP for 40,000 points). A
new one comes with the big Steam sales. Nothing else in the shop - profile items, backgrounds, emoticons, avatar
frames - gives any XP, so nocat.farm never buys those. When the shop sells no badge, it buys nothing and says so.

Points spent are gone for good, so it's careful:

- **Preview first.** `points spend preview myaccount` shows the points, what it keeps, this week's spending, and
  what it would buy (*3 levels of Summer Collection - 2026, level 5 to 8, for 3,000 points: +300 XP*). It buys nothing.
- **One switch, and you see it first.** *Spend Steam Points on levels* is the whole thing. Turning it on in the
  account's settings opens a popup with what it would buy and its limits - *Turn on* or *Cancel*. Off stops it at
  once. At the console, `set myaccount SpendPoints on` answers with the same preview, and `points on myaccount`
  confirms it; `points off myaccount` turns it off. Steam chat can't turn it on. Whether it's on stays on the PC where
  you turned it on: a backup brings back the week's spending, never the switch.
- **It keeps some.** *Always keep at least* (1,000 points) is never spent, and *Spend at most, a week* (3,000) caps
  any 7 days.
- **Off by default.** *Spend Steam Points on levels* is off on every account, robots too. Turned on, a
  human-mode account only buys during its own day - asked again right before each purchase.
- Right before buying it reads the balance, the price and the badge level again. If anything changed, it buys
  nothing and looks again later.
- If Steam doesn't answer about a purchase, it counts as spent until a later look sees the badge's level - then
  it's kept, or taken off if the level didn't change. It's never bought a second time meanwhile.

While it's on, it looks every 12 hours and buys as many levels as the spare points allow; `points spend myaccount`
buys now, with the same limits. `points` shows each account's points and whether it spends them. Each
purchase goes in the log and, with *Send Steam Points spending* on, to Discord and Telegram.

### What each setting does

Settings, the account, **Steam Points**:

| Setting | Default | What it does |
|---|---|---|
| *Spend Steam Points on levels* | off | Buys badge levels by itself. Turning it on shows what it would buy first and asks you to confirm. |
| *Always keep at least* | 1000 | Points it never spends. |
| *Spend at most, a week* | 3000 | The most it spends in any 7 days. 0 spends nothing. |
| *Buy levels of the seasonal badge* | on | The badge the shop is selling now. |
| *Also older seasonal badges* | off | Older seasonal badges the shop still lists, once the current one is done. Steam has turned some of these down. |
| *Stop each badge at level* | 40 | The highest level it takes a badge to. |
| *Look every (hours)* | 12 | How often it looks at the points and the shop. |

## Spare items: gems, sell or keep

*Deal with spare items* (off, per account) looks at the account's Steam inventory every 12 hours or so and does
something with what it can do without - turns it into gems, or sells it, whichever pays more. A few items at a time,
every one written in the log with its reason. Only Steam's own items (cards, backgrounds, emoticons), never game
items or CS2 skins.

**What's spare:** cards of a badge already at level 5 (or a foil badge already made), card copies beyond what the
badge still needs (at level 2 it keeps 3 of each), and second copies of backgrounds and emoticons.

**Never touched, whatever the settings:**

- anything that came in within the last 7 days (counted from when nocat.farm first saw it, so right after turning
  it on everything waits a week);
- anything on your keep list (item names, or whole games);
- foil cards, unless you switch them on;
- trade-locked items;
- anything worth more than the price limit (0.25 in this account's own Steam wallet currency - for yen, won or
  rupiah set the same value in your own currency, e.g. 40 for yen) - those are only listed in `spare preview` as
  "worth 0.80 CAD - left for you to decide";
- anything with no known price, or a price older than 24 hours - it waits for a fresh one, never a guess;
- anything with no sale in the last day, or nobody selling it - there's no real price to go by;
- anything on the market or in a trade offer;
- the last copy of anything, unless you set *Keep this many* to 0. A card a badge still needs is kept even then.

On top of that it stops for the day after 10 items or 1.00 worth of items, whichever comes first.

**Prices in each account's own currency.** Every price that decides anything - the price limit, the daily value
cap, gems or selling, and what an item is listed at - is in that account's own Steam wallet currency, so two accounts
in different countries never share a number. Steam's market search only ever answers in US dollars, whatever currency
is asked for, so it's only used to see which items anybody sells; each item's real price comes from the market's
price overview in the wallet's own currency. An account whose wallet currency Steam hasn't said yet prices and sells
nothing.

**Gems or sell.** An item becomes gems when its gems are worth at least 20% more than selling it would pay you
after Steam's fees (gems are priced from a sack of 1000, in the wallet's currency). Otherwise it's sold. If neither
pays anything it's kept. Turning items into gems needs no phone confirmation; selling does, unless the authenticator
is in nocat.farm.

**What it's listed at.** Right before each listing the item is priced again. By default (*a cent under the real
price*) the real price is what it sold for over the last day - the median - and it's listed a cent under that or
under the cheapest listing, whichever is higher - so a listing dumped below what it sold for never sets the price: sold
for 0.30 with one at 0.10, it lists at 0.29. No sale in the last day, nobody selling, or prices that contradict each
other (a listing 50% or more under the median with only one sale behind it): kept, never a guess. It's never under the highest buy order either - a buy order is always
under the cheapest listing. The log says each price with its currency: "sells for 0.57 CAD".

**One switch.** *Deal with spare items* is the whole thing. Turning it on in the account's settings opens a popup
with what it would do now and the limits it keeps to - *Turn on* or *Cancel*. Off stops it at once, before the next
item, even in the middle of a look. At the console, `set <account> SpareItems on` answers with the same preview and
`spare on <account>` confirms it; `spare off <account>` turns it off (Steam chat can turn it off, never on). Whether
it's on stays on the PC where you turned it on: a backup brings back the daily counts, never the switch.

Going back to 1.7.x: its *Sell duplicate cards* lists cards at once with no preview, so it's left on there only when
this version would sell on the account too - spare items on and an action that sells. A change you make to *Sell
duplicate cards* or *Open booster packs* on the older version is kept when you update again.

```
spare preview new       # exactly what it would do and why, item by item
spare on new            # turn it on (the same as the switch in its settings)
spare off new           # turn it off (works from Steam chat too)
spare do new            # do the next few now (only while it's on; on a human-mode account only in its day)
```

(`gems` works too, as another name for `spare`.)

**The gems afterwards.** *Automatic* packs them into sacks of 1000 and keeps them tradable on a human-mode account,
and keeps them loose on robot accounts so *Make booster packs for* can use them - automatic never sells. You can
choose to send the sacks to another of your accounts, or *Sell the sacks*: that lists one sack per look, inside the
spare-items daily limits.

On a human-mode account it only looks during its day, with longer waits between items - and it asks again before
every item, so if its day ends or you start playing on it in the middle of a look, it stops there.

When selling, listings nocat.farm put up itself that sit unsold for a week while someone else sells cheaper come down
and go up again at that day's price. A listing you made yourself is never touched.

*Sell duplicate cards* from earlier versions is part of this now. If it was on, *Deal with spare items* starts off and
the log says once: turn it on in Settings to see what it would do.

### What each setting does (Spare items)

| Setting | What it does | Default |
|---|---|---|
| Deal with spare items | The switch. Per account. | off |
| What to do with spare items | Best of gems or selling, gems only, sell only, or keep everything and just report | best of both |
| Gems afterwards | Automatic, keep them loose, pack into 1000-gem sacks and keep them tradable, send the sacks to another of your accounts, or sell the sacks | automatic: sacks on a human-mode account, loose on the others |
| Send the sacks to | The name of another of your accounts here | - |
| Keep at least this many gems loose | Gems never packed, sent or sold | 0 |
| Cards from finished badges / Extra card copies | Which cards count as spare | on / on |
| Keep this many of each card | 0 keeps none, so the last copy can go too; cards a badge still needs are always kept | 1 |
| Foil cards too | Lets foils count as spare | off |
| Duplicate backgrounds / emoticons, and how many of each to keep | 0 keeps none | on, keep 1 |
| Never touch items worth more than | In this account's own Steam wallet currency, e.g. 0.25 for dollars or euros - the same value in your own currency, e.g. 40 for yen. Worth is the higher of the cheapest listing and the last day's median | 0.25 |
| Leave new items alone for (days) | 0 to 30 | 7 |
| Never touch these items / items from these games | Your keep list: full item names as the inventory shows them, e.g. Sans, Gordon Freeman (Foil) - a name keeps that item in every game | empty |
| Gems must beat selling by (%) | Percent | 20 |
| Items at a time | The most in one look | 5 |
| Items a day, at most / Value a day, at most | The daily caps (the value in this account's own Steam wallet currency) | 10 / 1.00 |
| Look every (hours) | Hours between looks | 12 |
| Wait between items (seconds) | Longer on human mode | 40 |
| Trust a price for (hours) | Hours a price counts as fresh | 24 |
| Selling price | A cent under the real price (the last day's median, or the cheapest listing if higher, ignoring a dump), the same as the cheapest, the median (never under the cheapest; no median - kept), or a cent under the cheapest (sells fastest, follows dumps down). Priced again in the wallet's currency right before each listing | a cent under the real price |
| Keep an item whose cheapest listing is this far under its sales (%) | With only one sale in the last day and the cheapest listing this far under it, the prices contradict each other and the item is kept. 0 lists it anyway. A listing under the median never sets the price either way | 50 |
| Take down unsold listings after (days) | Only listings nocat.farm put up itself: one the market has gone under comes down and goes up again at that day's price. Yours are never touched | 7 |

## Opening booster packs

*Open booster packs* (per account) opens packs in the inventory - from drops, from gems or from gifts - so their
cards count toward badges, or become spare items. It's **never** by default on every account, robots too: opening a
pack gets rid of it, so nothing is opened until you choose. Three choices:

- **only when the cards are worth more**: opens a pack only when its three cards are worth more on
  the market than the sealed pack, after Steam's fees. A pack with no fresh price, or a card nobody sells, stays shut;
- **always**;
- **never** (the default).

A pack that came in within the last 7 days, or is on the market or in a trade offer, is never opened. On a
human-mode account packs are opened only during its day, a few at a time. Every opened pack is written in the log
with what came out.

```
booster open new        # every sealed pack, and whether it would be opened and why
booster open new do     # open those now (on a human-mode account only in its day)
```

*Open booster packs* on from an earlier version is now **always**, as it was. If it was off, it stays **never** - on
every account.

### What each setting does (Booster packs)

| Setting | What it does | Default |
|---|---|---|
| Open booster packs | Never, only when the cards are worth more, or always | never |
| Leave new packs alone for (days) | 0 to 30 | 7 |
| Never open packs from these games | Games whose packs stay sealed | empty |
| Packs it doesn't open | Keep them, send them to another of your accounts, or sell them. Sending or selling happens only while Spare items is on; selling keeps to its caps and its selling price | keep |
| Send unopened packs to | The name of another of your accounts here. Sending needs the phone to confirm unless the authenticator is added | - |
| Look for packs every (hours) | Hours | 24 |

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
