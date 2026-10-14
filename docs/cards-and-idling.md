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

**Booster packs.** *Make booster packs* (in *Booster packs*) is off, **games I pick** - each game on the list gets a
pack a day, as Steam allows - or **automatic**, which picks the game closest to its next badge level (see
[Levelling with gems](#levelling-with-gems)). Packs use tradable gems first (*Booster packs use*) and never go below
*Keep at least this many gems*. `booster myaccount` shows the gems and which games can be made now,
`booster myaccount 730, 440` makes those packs straight away, and `booster plan myaccount` shows what automatic would
make next and why. An older list carries over as "games I pick"; an empty one is off. Opening packs has its own
section below.

**Badges.** *Craft badges from card sets* (off) turns full sets into badges once a day. That's what raises your
Steam level; cards sitting in the inventory raise nothing. *Also craft foil badges* (on) crafts foil sets too - turn it
off to level with normal badges only and leave foil cards alone. If Steam refuses a foil badge, only foils wait (about
6 hours); normal badges carry on.

**Selling.** `sell myaccount` shows which spare cards it would list and what you'd get after Steam's fees.
`sell myaccount do` lists them (5 at a time by default), and `sell myaccount relist` takes down week-old listings
the market has gone under. To have it done by itself, see *Deal with spare items* below - it sells or turns into gems,
whichever pays more. Listings need confirming in the Steam app unless the authenticator is in nocat.farm.

**Level-up planning.** `levelup myaccount 50` works out what reaching level 50 would cost: badges it can craft now,
sets it has nearly finished, and the cheapest full sets on the market for the rest. Prices are looked up slowly in
the background, so wait for "level plan … is ready" in the log and run it again.

`level`, `balance` and `points` show each account's Steam level, wallet and Points Shop points. `owns <game>` tells
you which of your accounts already own a game and how long each has played it.

## Levelling with gems

Gems can become Steam levels: gems make booster packs, packs give cards, a full set of cards becomes a badge level.
Only **normal** cards and normal badges count here - foil cards and foil badges are never used for this.

> **Turn it on** (per account)
> 1. *Booster packs* → *Make booster packs*: **automatic - closest badge**
> 2. *Booster packs* → *Open booster packs*: **always**
> 3. *Badges & selling* → *Craft badges from card sets*: **on**
> 4. *Spare items* → *Deal with spare items*: **on** (spare cards, backgrounds and emoticons become more gems)
>
> Then `booster plan myaccount` shows what it would make next, and why.

### The numbers

| What | How much | Source |
|---|---|---|
| A badge level | 100 XP, a full set of cards each time, up to level 5 for a normal badge | Steam's trading cards FAQ [1] |
| A booster pack | 3 random cards from one game's set | Steam's trading cards FAQ [1] |
| What a pack costs | 6,000 gems ÷ cards in the set: 1,200 for a 5-card set, 1,000 for 6 cards, 400 for 15 | community price tables [2] |
| How often | 1 pack per game per day, for the games the booster creator page lists (games you own that have cards) | Steam forums [3] |
| A sack of 1,000 gems | about US$0.65 on the market, about US$0.56 to you after Steam's fees (October 2026) | Steam market [4] |
| The cheapest normal cards | about US$0.03 each, so the cheapest whole sets are roughly US$0.15 to US$0.35 (October 2026) | Steam market [5] |

### One worked example: a 6-card game

A pack for a 6-card set costs 1,000 gems and gives 3 random cards. Cards can repeat, so the last missing card is
the slow one (the "collect them all" problem [6]). On average:

| You're missing | Cards it takes | Packs | Gems | XP |
|---|---|---|---|---|
| 1 card | 6 | 2 | 2,000 | 100 |
| 2 cards | 9 | 3 | 3,000 | 100 |
| all 6 (a fresh game) | about 15 | about 5 | about 4,900 | 100 |

So **1,000 gems is 20 to 50 XP** this way. The best case is about 2,000 gems a level (a set's worth of packs, no
repeats). Repeats aren't lost while the badge still has levels to go - they count toward the next set.

The same 1,000 gems sold as a sack pay about US$0.56, which buys about 1.5 to 3.5 of the cheapest whole sets (US$0.56 ÷ US$0.35 to US$0.15): **150 to 350 XP**.

Two things follow:

- The fewer cards a badge is missing, the cheaper its next level - whatever the size of the set. That's the whole
  rule automatic uses: **closest badge first**.
- Selling gems and buying cheap sets gives the most XP per gem, but it's market work (listings, fees, buying the
  cards yourself). Booster packs are slower, but fully hands-free and never touch the market.

### Cheapest XP routes

| Route | XP for the cost | Who does it |
|---|---|---|
| Card drops from your games | free | nocat.farm (*Farm trading cards*) |
| Steam Points: the seasonal badge | 100 XP per 1,000 points | nocat.farm, once you turn on *Spend Steam Points on levels* |
| Sell the gems, buy the cheapest sets | about 150 to 350 XP per 1,000 gems | you buy the cards; `levelup myaccount 50` lists the cheapest sets |
| Gems into booster packs | about 20 to 50 XP per 1,000 gems | nocat.farm (*Make booster packs: automatic*) |
| Keep the gems | nothing | - |

### What nocat.farm does by itself, and what you choose

**By itself, with automatic on:**

- picks the game whose normal badge is closest to its next level - the fewest cards missing, counting the cards you
  have - among the games the booster creator lists;
- never a game whose badge is at level 5, never one whose packs you've set to stay sealed, never foils;
- one pack per game per day, and only while the gems cover the pack plus *Keep at least this many gems*;
- opens the packs it made, whatever *Open booster packs* says - packs from anywhere else still follow that setting,
  and its own are never sent or sold;
- its own packs stop being its own - and follow *Open booster packs* and *Packs it doesn't open* like any other - when
  you switch *Make booster packs* away from automatic, when one has waited over 2 days, or when its game goes on
  *Never open packs from these games* (then it stays shut);
- if Steam doesn't answer when it makes a pack, it doesn't make another for that game: it looks for the new pack in
  the inventory and opens it, or - if the booster creator shows the game was never made - lets the game go again;
- once a set is complete, *Craft badges from card sets* crafts the level. On a robot account it looks within the hour
  after a pack is opened, at most once every 2 hours. A human-mode account keeps its usual wait unless a whole normal
  set is actually held - then it crafts at its next chance in its day;
- on a human-mode account, all of it only during its day, one step at a time with gaps. *Games I pick* also makes its
  packs only during the account's day.

**You choose:**

- *Make booster packs*: off, games I pick (the list below it), or automatic;
- *Keep at least this many gems* (0) and *Booster packs use* (tradable gems first);
- *Open booster packs* for packs from drops and gifts;
- whether finished sets get crafted (*Craft badges from card sets*).

Picking automatic sets *Gems afterwards* (Spare items) to **keep them loose** when it was automatic or sacks - the
booster creator can't use gems in sacks. It says so once in the log. Change it back if you'd rather keep sacks.

Sources:
[1] [Steam trading cards FAQ](https://steamcommunity.com/tradingcards/faq) ·
[2] [Steam Trading Cards group: booster prices](https://steamcommunity.com/groups/tradingcards/discussions/2/3002178258656791592) ·
[3] [Steam forums: one booster per game per day](https://steamcommunity.com/discussions/forum/7/626329820802667749) ·
[4] [Sack of Gems on the market](https://steamcommunity.com/market/listings/753/753-Sack%20of%20Gems) ·
[5] [cheapest normal cards on the market](https://steamcommunity.com/market/search?appid=753&category_753_item_class%5B%5D=tag_item_class_2&category_753_cardborder%5B%5D=tag_cardborder_0) ·
[6] [the coupon collector's problem](https://en.wikipedia.org/wiki/Coupon_collector%27s_problem)

## Steam Points into levels

The Points Shop sells one thing that raises your Steam level: the **seasonal badge** (in October 2026, *Summer
Collection - 2026*). Each level costs 1,000 points and is worth 100 XP, up to level 40 (4,000 XP for 40,000 points). A
new one comes with the big Steam sales. Nothing else in the shop - profile items, backgrounds, emoticons, avatar
frames - gives any XP, so nocat.farm never buys those. When the shop sells no badge, it buys nothing and says so.

Points spent are gone for good, so it's careful:

- **Preview first.** `points spend preview myaccount` shows the points, what it keeps, this week's spending, and
  what it would buy (*3 levels of Summer Collection - 2026, level 5 to 8, for 3,000 points: +300 XP*). It buys nothing.
- **One switch, and you see it first.** *Spend Steam Points on levels* is the whole thing. Turning it on in the
  account's settings opens a popup with what it would buy (levels, points and XP, in big numbers) and its limits as a
  short list - which badges, from its own switches, and up to which level - *Turn on* or *Cancel*. Off stops it at
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

- anything that came in within the last 7 days (counted from when nocat.farm first saw it). Everything already in
  the inventory the first time it looks counts as old, so months-old cards don't wait a week - only items that come in
  after that do;
- anything on your keep list (item names, or whole games);
- foil cards, unless you switch them on;
- trade-locked items;
- anything worth more than the price limit (0.25 in this account's own Steam wallet currency - for yen, won or
  rupiah set the same value in your own currency, e.g. 40 for yen) - never turned into gems, never sold, and not
  shown either: as far as the preview goes, it simply isn't spare;
- anything with no known price, or a price older than 24 hours - it waits for a fresh one;
- anything with no sale in the last day, or nobody selling it - there's no real price to go by;
- anything on the market or in a trade offer;
- the last copy of anything, unless you set *Keep this many* to 0. A card a badge still needs is kept even then.

On top of that it stops for the day after 10 items or 1.00 worth of items, whichever comes first.

**Too little to sell.** *Only sell if you get at least* (0.05 by default, in the wallet's own currency) is the least
you'd get after Steam's fees. An item that would pay less becomes gems if its gems are worth anything, and is kept if
not (on *sell only* it's kept).

**Prices in each account's own currency.** Every price that decides anything - the price limit, the daily value
cap, gems or selling - is in that account's own Steam wallet currency, so two accounts in different countries never
share a number. Steam's market search prices a whole game's items in one request, but only ever answers in US dollars;
so for a wallet in another currency nocat.farm learns the rate from the market itself - it prices a few items both
ways (their Canadian price over their dollar price, say) and takes the middle one - keeps it for a day, and turns the
search's dollars into the wallet's currency with it. An item whose converted price is close to the price limit (80%
to 125% of it) is priced in its own currency instead, to be sure. A big inventory in Canadian dollars is decided in a
handful of requests and starts acting on its first look - it used to be one request an item, and 720 of them ran
into the market's limit overnight. An account whose wallet currency Steam hasn't said yet prices and sells nothing.

**A sale is never priced from a converted number.** Right before an item is listed it's priced again in the wallet's
own currency (see below). Turning an item into gems needs no exact price: one whose converted price is clearly under
*Only sell if you get at least* simply becomes gems.

**Pricing takes a while.** The first look reads each game's search page, a few games a look; while some wait,
`spare preview` says how far it has to go ("still pricing 146 items - about 25 min") and it looks again every few
minutes until they're done. Market requests are about ten seconds apart, and at most 360 an hour - one every 10
seconds on average - so a long run never runs into the market's limit; past that, the next waits for the hour. When
Steam asks for a slower pace anyway, the preview says when pricing goes on again instead of a time left: "still
pricing 48 items - Steam asked to slow down, pricing resumes around 13:40". That pause is kept over a restart.

**Not on the search page isn't "nobody sells it".** Only the market saying so - an item on the search page with no
listing, or its own price lookup finding none - keeps an item as *nobody sells it*. An item the search pages didn't
show at all (they come ten at a time and can shift between pages) has no price yet, and gets a price lookup of its
own on a later look, inside the hourly limit. Names written differently on the page ("&amp;" for "&") still match.

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

**Only what it does.** `spare preview`, a look's answer and the popup say only what it will do or did: items to gems
and items to sell, with the gems and the money, today's count against the daily limit, and how many items are still
being priced. Never the items it leaves alone (a copy you keep, a card a badge needs, no price yet, over the price
limit) - those are only in the debug log, counted by reason; the ones kept because nobody sells them or they had no
sale in the last day are named there too (up to 15 each). With nothing to do it's one line: "new: nothing to turn into gems or sell
right now". Once a look has done something, its answer is what it did - "new: done - 14 item(s) into 1,080 gems, 0
listed" - and today's count; the items themselves are already in the log, one line each. When `spare preview` or
`spare do` answers after the command line already said "the log says when it's done", the log gets its answer as one
line - "spare: 29 → gems, 1 to sell · today 26/50" - and the full list goes to the debug log; ask again and the
command shows the list.

**Every action is a line in the log.** Short, item first: "Metro (Tom Clancy's The Division) → 80 gems", "listed Scout
(Game) for 0.12 CAD · you get 0.10 CAD", "sold Scout (Game) for 0.12 CAD · you got 0.10 CAD" (once the market history
shows the sale), "Starry Background 1 (Waveform): no price yet, tries later". Once a listing is confirmed and up for buyers: "Starry
Background 1 is on the market · 0.22 CAD". While one waits on its confirmation in the Steam app, it looks at the
market every 5 to 10 minutes (one request), so that line - or the declined one - comes minutes after you confirm,
not at the next look. Why an item went to gems or the market is
a debug line. In the window and the dashboard's Log tab, a run of items into gems is one row - "turned 14 items into
1,080 gems" - that opens on a click to show each one; the log file keeps every line. "lowered
Scout from 0.31 CAD to 0.30 CAD - unsold 7 days", a declined listing, unopened packs or sacks of gems sent to another
account, a pack opened or made, a badge crafted.

**One message a look.** Discord, Telegram and the pop-up get one message when a look ends, not one per item: "14
item(s) into 1,080 gems, 1 listed - you get 0.13 CAD", then the first 10 items by name and "...and 4 more". A human-mode
look spread over an hour is still one message, sent when it ends. The same for opening packs: "opened 2 pack(s)" and
what came out of each.

**One look at a time.** A `spare do` and the timed look never work on the same account at once: the second answers
"already dealing with spare items" (or waits a minute, for the timed look). Two at once once asked Steam for an item
the other had just turned into gems.

**When Steam's answer isn't a yes.** If turning an item into gems gets no answer, a timeout or an error, it reads the
inventory again before calling it a failure: the item gone and the gems in means it went through, and it's logged
and counted as done. Otherwise the log says Steam's own words ("The item you selected has expired or no longer exists"),
the look stops there, and the item doesn't count against today's limit.

**A price that moved, and items held back.** Right before an item goes up it's priced again. A higher price is fine:
it's listed at the real price, as long as it's still under the price limit and today's value limit has room for it
("price went up to 0.23 CAD · over today's value limit, not listed" when it hasn't). Anything not done after all - kept
at the market, refused, stopped part way - gives its place in today's items and value back straight away, so the next
item in the same look can have it.

**One switch.** *Deal with spare items* is the whole thing. Turning it on in the account's settings opens a popup
with what it would do now in big numbers (*12 to gems · 8 to sell*, with what they're worth), a line saying it isn't
all at once (*the first 5 at its next look, then at most 10 a day*), the limits it keeps to as a short list, and how
many items are still being priced - *Turn on* or *Cancel*. Its other settings show
once it's on. Off stops it at once, before the next
item, even in the middle of a look. At the console, `set <account> SpareItems on` answers with the same preview and
`spare on <account>` confirms it; `spare off <account>` turns it off (Steam chat can turn it off, never on). Whether
it's on stays on the PC where you turned it on: a backup brings back the daily counts, never the switch.

Going back to 1.7.x: its *Sell duplicate cards* lists cards at once with no preview, so it's left on there only when
this version would sell on the account too - spare items on and an action that sells. A change you make to *Sell
duplicate cards* or *Open booster packs* on the older version is kept when you update again.

```
spare preview new       # exactly what it would do and why, item by item - only what it does
spare on new            # turn it on (the same as the switch in its settings)
spare off new           # turn it off (works from Steam chat too)
spare do new            # do the next few now (only while it's on; on a human-mode account only in its day)
```

(`gems` works too, as another name for `spare`.)

**The gems afterwards.** *Automatic* packs them into sacks of 1000 and keeps them tradable on a human-mode account,
and keeps them loose on robot accounts so *Make booster packs for* can use them - automatic never sells. You can
choose to send the sacks to another of your accounts, or *Sell the sacks*: that lists one sack per look, inside the
spare-items daily limits.

On a human-mode account it only looks during its day and spreads the day's items evenly through it, never all in one
go: each look does about what's left today over the looks left in its day, rounded up - never more than *Items per
look, human mode* - and the waits between them (inside the 3 to 20 minutes set, random each time) spread them over the
time to the next look. For example 50 a day in a 6-hour day with a look every hour: about 9 a look, a few minutes
apart, every hour. If *Items per look, human mode* is lower than that, the looks come sooner instead (never closer
than 45 minutes). A look that comes back early only to price more items doesn't do any - they wait for the next look
that does. `spare preview` says it: "spread over its day: about 9 a look, next look ~14:20". It asks again before
every item, so if its day ends or you start playing on it in the middle of a look, it stops there. Packing gems into
sacks, sending or listing a sack, and taking down an unsold listing count as items too, with the same wait before each.
Robot accounts keep the gap in seconds.

Turn Spare items off (or scam watch holds the account) in the middle of a look and it stops there: no more gems, no
sacks packed or moved, no listing taken down.

**Unsold listings step down.** A listing nocat.farm put up itself that's still unsold after 7 days comes down and goes
up again a cent cheaper than its last price - or a cent under a cheaper real listing, if there is one (a dumped
listing doesn't count). Never under 80% of what it sold for over the last day: at that floor it stays up, so it
doesn't go round and round. Each step is in the log: "lowered Level ??? Ninja from 0.31 CAD to 0.30 CAD - unsold 4 days". If the price
has gone up by the time it's listed again (0.31 -> 0.50), the step is dropped and it goes up at that day's price. A
listing you made yourself is never touched, nor one Steam is still holding off the market.

**A listing you decline.** If you decline one of its listings in the Steam app (or it's taken down before it ever went
up), that item isn't listed again for 30 days - every copy of it - and the log says so. 0 days means never again.
Steam doesn't say why a listing vanished, so it checks your market history: one that sold before the next look is a
sale, not a decline. If the history can't be read, it waits and checks again next look. While one of its listings
still waits on your phone, no other copy of that item is listed.

Changing *Look every (hours)* moves the next look straight away - for booster packs and Steam Points too.

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
| Items at a time | The most in one look (robot accounts) | 5 |
| Items per look, human mode | The most in one look on a human-mode account, which spreads the day's items through its day (about what's left today over the looks left) | 3 |
| Items a day, at most / Value a day, at most | The daily caps (the value in this account's own Steam wallet currency). A human-mode account spreads them through its day | 10 / 1.00 |
| Look every (hours) | Hours between looks | 12 |
| Wait between items (seconds) | Robot accounts | 40 |
| Wait between items, human mode (minutes) | Shortest and longest, random in between each time | 3 to 20 |
| Trust a price for (hours) | Hours a price counts as fresh | 24 |
| Selling price | A cent under the real price (the last day's median, or the cheapest listing if higher, ignoring a dump), the same as the cheapest, or the median (never under the cheapest; no median - kept). Priced again in the wallet's currency right before each listing. "A cent under the cheapest" is gone - it followed dumps down; a saved choice of it is now a cent under the real price | a cent under the real price |
| Only sell if you get at least | The least you'd get after fees, in the wallet's own currency. Less: gems if they're worth anything, otherwise kept. 0 sells anything | 0.05 |
| Keep an item whose cheapest listing is this far under its sales (%) | With only one sale in the last day and the cheapest listing this far under it, the prices contradict each other and the item is kept. 0 lists it anyway. A listing under the median never sets the price either way | 50 |
| Take down unsold listings after (days) | Only listings nocat.farm put up itself: still unsold, it comes down and goes up again a cent cheaper, or a cent under a cheaper real listing. Yours are never touched | 7 |
| Never go lower than (% of the real sale price) | The floor for those steps, a percent of what it sold for over the last day. At the floor it stays up | 80 |
| If you decline a listing on your phone, try again after (days) | That item, every copy, isn't listed again for this long. 0 never again | 30 |

## Opening booster packs

*Open booster packs* (per account) opens packs in the inventory - from drops, from gems or from gifts - so their
cards count toward badges, or become spare items. It's **never** by default on every account, robots too: opening a
pack gets rid of it, so nothing is opened until you choose. Three choices:

- **only when the cards are worth more**: opens a pack only when its three cards are worth more on
  the market than the sealed pack, after Steam's fees. A pack with no fresh price, or a card nobody sells, stays shut;
- **always**;
- **never** (the default).

Packs that automatic *Make booster packs* made are its own: it opens them itself at the next chance, whatever this
says, and they're never sent or sold. `booster open new do` opens them too. While automatic is on, a pack you make
with `booster new 603750` counts as automatic's own as well.

A pack that came in within the last 7 days (*Leave new packs alone*), or is on the market or in a trade offer, is
never opened. Packs nocat.farm made itself - automatic, *games I pick* or `booster <account> <appIDs>` - are never
held back as new: that rule is for packs from drops and trades. On a human-mode account packs are opened only during
its day, *Items per look, human mode* at a time (3 by default). Every opened pack is written in the log with what came
out and how many of its cards the account didn't have yet: "opened s&box pack: 3× Flying Contraption (1 new)". Why it
was opened is a debug line. A pack it made reads "made s&box pack · 671 gems left".

```
booster open new        # every sealed pack, and whether it would be opened and why
booster open new do     # open those now (on a human-mode account only in its day)
```

*Open booster packs* on from an earlier version is now **always**, as it was. If it was off, it stays **never** - on
every account.

### What each setting does (Booster packs)

| Setting | What it does | Default |
|---|---|---|
| Make booster packs | Off, games I pick, or automatic - closest badge (see [Levelling with gems](#levelling-with-gems)) | off |
| Make booster packs for | The games, for "games I pick" | empty |
| Booster packs use | Tradable gems first, or untradable first | tradable first |
| Keep at least this many gems | A pack is made only while the gems cover it plus this | 0 |
| Open booster packs | Never, only when the cards are worth more, or always. For levelling, always. Packs automatic made are opened by it either way | never |
| Leave new packs alone for (days) | 0 to 30. Always shown: it holds back new packs from selling or sending too. Packs nocat.farm made itself are never held back | 7 |
| Never open packs from these games | Games whose packs stay sealed. Always shown, even with opening off | empty |
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
