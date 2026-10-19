[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Trades

Out of the box, an account accepts donations, which are offers where it gives nothing away (*Accept donations*,
on), and leaves every other offer for you. Offers from your own accounts can go through by themselves once you set that up
(below). Fair card swaps from anyone are accepted only if you turn on *Accept 1:1 card swaps from anyone*. And if you'd rather
not have offers pile up, *Decline everything else* (off) declines whatever isn't accepted.

Each offer waits 2-15 minutes before it's answered (*Wait at least* / *And at most*). No Steam Web API key is
needed for any of this.

Every new offer is announced once, in the log, as a pop-up (*Pop up for trade offers*) and on Discord/Telegram,
with a short number to answer it by:

```
new trade offer 4 from SomeGuy: you get 1 item(s): ..., you give 3 item(s): ... - waiting for you - trade accept myaccount 4 or trade decline myaccount 4
```

It also tells you when an offer is accepted, declined, needs confirming, or is gone (cancelled, answered elsewhere
or expired).

```
offers myaccount                  # every live offer, with the same numbers
trade accept myaccount 4          # accept offer 4
trade decline myaccount 4         # decline it (or: all)
trade cancel myaccount <offer id> # take back an offer this account sent that hasn't gone through (or: all)
```

Accepting an offer that sends items out also confirms it, if the account's authenticator is in nocat.farm.

**Your other accounts** (`AutoTradeWith`) sets which accounts it trades with without asking you, and in which
direction. List account names or SteamID64s, separated by commas: `farm1` or `farm1:both` trades both ways (the
default), `farm1:from` only accepts what farm1 sends, and `farm1:to` lets farm1 take items. For example,
`farm1, alt:to`. Only list accounts you own. Left empty it's nobody: every offer waits for you (donations follow their
own switch). A config from before 1.8.2 with *Accept anything from your own accounts* on had its *Your own accounts*
put in here, both ways - exactly what it did before.

*Send items to* (`TradeMasters`, Show advanced) is only where `send` and the timed sends go - a name or a SteamID64, the
first one used. Nobody on it can take anything; that's *Your other accounts*.

## Sending items

```
send farm1                       # farm1's items to the account in "Send items to"
send farm1 to myaccount          # farm1's trading cards to myaccount
send all to myaccount cards foils
```

The types are `cards`, `foils`, `backgrounds`, `emoticons`, `boosters`, `gems` and `all`. `all` means everything
tradable, game items like CS2 skins included. Types you name in the command are sent as asked. Without them, `send`
uses the account's *What to send* setting (trading cards by default; `all` sends everything tradable in every game,
TF2 and CS2 items too), and `send … to` sends trading cards.

To send on a schedule, *Send items every* (0 = off) sends on a timer and *Send items when farming finishes* sends
once when the cards run out. Both always use *What to send*. *Send items around* picks the hour of the day for the timed
send (like 4 for about 4 in the morning); left at -1 it goes every so many hours from when you turned it on. The
account's card on the dashboard shows when the next send is, each send is in the log, and a send Steam refuses is
sent to you as a problem with Steam's reason.

Between accounts that aren't Steam friends, Steam needs the other account's trade link token. nocat.farm reads it
by itself when that account is signed in here; otherwise set *Trade link token of the receiving account*. Offers the account sends
confirm themselves when its authenticator is here, and otherwise wait for you in the Steam app.

`match` shows swaps of spare cards between your own accounts that help both finish sets. `match do` sends them, and
the other account accepts by itself.

**Fair card swaps** (*Accept 1:1 card swaps from anyone*, off) are one-for-one card swaps from anyone, like the ones
card-swapping sites send. One is accepted only when every item is an ordinary trading card from the same game, one
for one, and the swap never sets your sets back: every card you give away must still have more copies afterwards
than any incoming card had before. Swaps that would sit in a trade hold are left alone. A swap with a stranger is
accepted, but its confirmation waits for you in the Steam app (or on the Authenticator page). Swaps between accounts
in your own nocat.farm (the ones `match` sends) are always judged this way and accepted by themselves, even with
the setting off. `fairswap myaccount <offer id>` tells you whether an offer passes and why not, without accepting or
declining it.

Items from games the account is banned in can't be traded. Ban watch adds those games to *...but not these games*
by itself, and you can add more.

## Card swaps with other nocat.farm users

*Swap spare cards with other nocat.farm users* (`CardSwaps`, off) swaps your spare normal trading cards with other people's
nocat.farm accounts: one card for one card, in the same game, so both of you finish sets sooner. It's per account.
Ticking it first shows what it could swap now and its limits, and nothing happens until you press **Turn on**. Off
stops it at once. From the console: `swaps preview myaccount`, `swaps on myaccount`, `swaps off myaccount`.

How it goes:

1. About twice a day your copy tells nocat.lol which spare cards each opted-in account has, for games where it has a
   spare copy, with their market prices. With it go the account's SteamID and trade link, so a partner can send it an
   offer. Turning it off deletes all of it from nocat.lol within a minute or two, even if the other accounts' cards
   can't be read just then; its old matches go from nocat.lol within the hour.
   The first time (and every few weeks), each account also signs in to nocat.lol through Steam, the way you'd sign in to
   a trading site, so nocat.lol knows it's really yours. nocat.lol never pairs an account nobody proved like that: no
   one can list your SteamID, block you, or collect trade links with made-up accounts. Steam's answer only says "this
   is SteamID X"; it can't sign in or trade, and it's made out to your copy alone.
2. Once an hour nocat.lol pairs accounts the way ArchiSteamFarm does: a card only goes from a bigger pile to a smaller
   one, on both sides, so neither side's sets lose anything. Several games go in one offer, and of the ways to pair two
   accounts it picks the one that finishes the most badge levels and sets.
3. One side sends the offer, with a note like `nocat.farm card swap #a1b2c3d4e5f6`. Before sending, it checks
   everything again against its own cards and prices. Then, before confirming, it checks the offer Steam actually made
   the same way the other side will: priced by Steam's own names for the cards, never by what the partner told
   nocat.lol. Anything off and the offer is taken back, never confirmed.
4. The other side checks it once more before accepting: it has to be exactly what nocat.lol paired (or part of it),
   from the account it paired, and give nothing else. An offer that isn't is never accepted, is left for you, and that
   account is never swapped with again (nocat.lol is told too; after 3 different people report the same account,
   nocat.lol never pairs it with anyone). An offer that's fine but no longer good for this account (its cards changed,
   or the prices did), for a match it already swapped or one paired over 2 days ago, or asking for a card already on
   another offer or swap, is declined.

What it never does:

- Never foils, backgrounds, emoticons, gems or game items. Only normal trading cards.
- Never a card your sets need, never the last copy of a card, never more than *Keep this many of each item* (Spare
  items) allows, and nothing on *Never touch these items* or *Never touch items from these games*.
- Never a card of a game it bought cards of for a badge in the last week ([Buying cards](cards-and-idling.md#buying-cards)),
  so nothing it paid for leaves before it's crafted.
- Never worth more than *Most a swap may lose* (0.03 by default) less than what it gives, by the market's cheapest
  price in this account's own wallet currency. 0 means a swap is never worth less. A card with no price from the last
  day is never swapped. The log shows both sides' worth: `card swap · you give 0.10 USD · you get 0.11 USD`.
- Never with an account whose trades Steam would hold (*Skip swaps Steam would hold*, on).
- Never more than *Swaps a day, at most* (5) - sent and accepted together. On a human-mode account only in its own
  day, 45 minutes to 2 hours apart.
- Nothing while scam watch holds the account's trades, and an offer already out isn't confirmed then either (nor
  outside a human-mode account's day) - it waits, or is taken back.

An account whose authenticator is in nocat.farm confirms its own offers. Without one, the offer waits for you in the
Steam app on your phone, and the log says so. An offer nobody accepted in 2 days is taken back, and so is every swap
offer still out when you turn swaps off.

Cards promised to a swap stay for it: spare items and `sell` never sell them or turn them into gems while the swap is
open. With [buying cards](cards-and-idling.md#buying-cards) on, a missing card already on its way in a swap offer
this account has checked is waited for instead of bought; a swap that's only paired doesn't hold buying back. Each has its own daily limit: swaps a day, spare items a day, money spent a day - one never uses up
another's.

`swaps` shows each account: on or off, today's swaps, matches waiting and offers out. `swaps sync` asks nocat.lol for
new matches now.

`swaps check myaccount` tries only the sign-in through Steam that swaps need, and says in one line whether it works -
or why not, like `Steam answered 200 and showed the sign-in page again`. It sends nothing to nocat.lol, turns nothing
on, and works with swaps off. It never shows Steam's answer itself. Only at the PC, Telegram or Discord, not from Steam
chat. When the sign-in fails during a sync, the debug log says why the same way: `swap sign-in failed · ...`.

## The Authenticator page

This is for accounts whose maFile is in `config/authenticators`, or whose secrets are in their settings. The
dashboard's **Authenticator** tab shows, for each one, the current Steam Guard code with a ring counting down its 30
seconds and a Copy button. Below that are the confirmations waiting (trades with the items on both sides, market
listings, account changes), each with **Confirm** and **Deny**. *Select all*, then *Confirm* or *Deny*, does several
at once. The list refreshes every 30 seconds.

The same from the console or Telegram:

```
2fa myaccount           # its Steam Guard code (2fa alone lists every account's)
confirmations myaccount # what's waiting, numbered
confirm myaccount 2     # confirm number 2 (or: all)
deny myaccount 1        # deny number 1
```

The tab stays hidden until at least one account's authenticator is in nocat.farm. Once it's showing, an account
with no authenticator gets a note saying where to put its maFile.

## Ban watch

Every few hours each account's bans are looked at - VAC bans, game bans, a trade ban or a community ban - and when a
new one appears you're told straight away: a log line, a pop-up and a Discord/Telegram message. That alert is account
health's *Alert on a new ban* (on); in 1.8.2 *Watch for bans* became it, and a config with it off has the alert off.
The look only reads and never changes anything, apart from keeping games the account is banned in out of trades.
`bans [account|all]` checks now.

## Scam watch

*Watch for scams* is on by default, on every account. Every 30 to 60 minutes it opens three of the account's own
pages, the same as you would in a browser, and looks for the signs of a stolen account or a trade scam:

- **An API key.** A Steam Web API key that appears (or changes) without you is the most common trade scam: the thief
  watches your trade offers and swaps them for fake ones. Only a fingerprint of the key is kept, never the key.
- **A new device.** A device on the account's *Authorized devices* list that wasn't there last time and isn't yours.
  nocat.farm's own sign-ins carry this PC's name (or the account's *Device name* setting), so it never warns about
  itself. Add your own phones and PCs to *Devices of yours to ignore*.
- **Email, phone or Steam Guard changed.** Only the hidden forms Steam shows (like `fa***@example.com`, ends in 42)
  are kept. Steam Guard (phone app, email codes or off) is asked from Steam's own two-factor service, the same one the
  Steam app uses, so the phone app being removed is noticed. If Steam doesn't answer, Steam Guard counts as "not
  known" for that look and nothing is compared.
- **Every session signed out.** Steam refusing the saved login because it was revoked. A normal reconnect, or you
  playing on the account, never counts, and neither does a saved login that simply ran out after about 200 days.

The first look only takes note of what's there; nothing already on the account is reported as new. A key page
that says the account can't have a key (no checked email, no phone app) counts as "no key", so a key appearing later
is a warning.

On a warning you get a red line in the log (which account, what changed and what nocat.farm did), a pop-up, a
Discord/Telegram message and a red banner on the dashboard. Then, as set per account:

- trades stop being accepted by themselves, and nothing is sent, swapped or listed on the market by itself (timed
  sends, the cards after farming, spare items, gem sacks and booster packs), until you've checked and typed
  `scamwatch ok <account>` (or `scamwatch <account> ok`, or pressed *It was me* on the banner) at the PC, on the
  dashboard, or from Telegram or Discord - never from Steam chat, since whoever has the account could type it. New offers are still announced, so you can answer
  them yourself with `trade accept|decline`. A `send`, `sell`, `spare do`, `booster open ... do` or `match do` you type
  still works, with a warning line. Cards held back when farming finished go once you type `scamwatch ok` (or, if the
  account logged out when done, the next time farming finishes - logging out isn't held up by the warning);
- the offers the account sent that haven't gone through are cancelled;
- only if you switch it on (off by default): a new API key is revoked straight away.

If it wasn't you, change the Steam password first. `scamwatch [account]` shows the last look and any warning.

### What each scam watch setting does

| Setting | Default | What it does |
|---|---|---|
| Watch for scams | on | The whole thing, per account. |
| On a warning: stop trading | on | No trades accepted or items sent by themselves until `scamwatch ok`. |
| On a warning: take back sent offers | on | Cancels the account's own offers still waiting. |
| On a new API key: revoke it | off | Presses Revoke on Steam's key page. Leave off if you make keys yourself. |
| Check for an API key / new devices / email, phone and Steam Guard / everything signed out | on | Each check on its own. |
| How often it looks (`ScamWatchMinMinutes` / `ScamWatchMaxMinutes`, not on the page) | 30 to 60 minutes | Each wait is a random time between the two. |
| *Scam warnings* (a chip under Global settings, Notifications) and *Pop up for scam warnings* (Pop-ups) | on | Where warnings go: every channel that's set up. |
| Devices of yours to ignore (Global settings, All accounts) | empty | Your own device names, as Steam lists them, comma separated. |

## Account health

*Keep an eye on account health* is on by default. Each account card gets one **Health** line, green, amber or red:
level, bans, trade hold, market access, limited account, Steam Guard and phone. When everything is fine it says
*all good · level 12*; otherwise what isn't, like *market: blocked for 7 day(s) (new device)*. While it's on, it
takes the place of the old bans line.

It checks a few minutes after the account signs in, then every 5 to 7 hours. It reads only what nocat.farm already
reads (the ban watch, scam watch's account page, the trade-hold lookup, what Steam says at sign-in) plus one look at
the Community Market page. When something gets **worse** it tells you once (log, pop-up, Discord/Telegram); getting
better is only written in the log. A new ban is told once, as health's, whichever look finds it. `health [account]`
shows every item.

### What each account health setting does

| Setting | Default | What it does |
|---|---|---|
| Keep an eye on account health | on | The Health line and the alerts, per account. |
| Alert on a new ban / a trade hold / the market blocked / a limited account / Steam Guard weaker / the phone removed | on | Which changes alert. Off still writes them in the log. |
| How often it checks (`HealthMinHours` / `HealthMaxHours`, not on the page) | 5 to 7 hours | Each wait is a random time between the two. |
| *Health alerts* (a chip under Global settings, Notifications) and *Pop up for account health* (Pop-ups) | on | Where alerts go: every channel that's set up. |

## Inventory value

```
value                    # every account, by game, and the change in the last 24 hours
value myaccount refresh  # read that account's inventory again
```

Items are priced at the lowest price each one is listed for on the Steam market, in *Show inventory values in* (US dollar
by default; match it to your Steam store). Inventories are read again every 6 hours. Prices are asked a whole game's
cards, backgrounds and emoticons at a time, and every wear of a skin at once, where the market allows it - so even a
big inventory is a few dozen requests, not one per item. Requests are still spaced out so Steam doesn't refuse; the
Inventory tile shows how many are left and about how long that is. Turn *Show inventory value* off on
accounts that only hold a few cards. Games listed in *...but not these games* are left out.

## History charts

The Overview page has charts under Today: **Cards dropped**, **Hours banked**, **Hours by game**, **Inventory
value**, and **rep4rep comments** when rep4rep is on. Pick 7, 30 or 90 days, and all accounts (*Whole fleet*) or
just one. A line compares this week with last week. Daily totals are kept for about 400 days in
`config/state/history/`.

---

[← Human mode](human-mode.md) · [Achievements and the hunter →](achievements.md)
