[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Trades

Out of the box, an account accepts donations - offers where it gives nothing away (*Accept donations*, on) - and
leaves every other offer for you. Offers from your own accounts can go through by themselves once you set that up
(below). Fair card swaps from anyone are accepted only if you turn on *Accept fair card swaps*. And if you'd rather
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

**Trade by itself with** (`AutoTradeWith`) sets which accounts it trades with without asking you, and in which
direction. List account names or SteamID64s, separated by commas: `farm1` or `farm1:both` trades both ways (the
default), `farm1:from` only accepts what farm1 sends, and `farm1:to` lets farm1 take items. For example,
`farm1, alt:to`. Left empty, it means the accounts in *Your own accounts*, both ways, when *Accept anything from
your own accounts* is on.

## Sending items

```
send farm1                       # farm1's items to the first account in "Your own accounts"
send farm1 to myaccount          # farm1's trading cards to myaccount
send all to myaccount cards foils
```

The types are `cards`, `foils`, `backgrounds`, `emoticons`, `boosters`, `gems` and `all`. `all` means everything
tradable, game items like CS2 skins included. Types you name in the command are sent as asked. Without them, `send`
uses the account's *What to send* setting (trading cards by default; `all` sends everything tradable in every game,
TF2 and CS2 items too), and `send … to` sends trading cards.

To send on a schedule, *Send items every* (0 = off) sends on a timer and *Send items when farming finishes* sends
once when the cards run out. Both always use *What to send*. *...around* picks the hour of the day for the timed
send (like 4 for about 4 in the morning); left at -1 it goes every so many hours from when you turned it on. The
account's card on the dashboard shows when the next send is, each send is in the log, and a send Steam refuses is
sent to you as a problem with Steam's reason.

Between accounts that aren't Steam friends, Steam needs the other account's trade link token. nocat.farm reads it
by itself when that account is signed in here; otherwise set *Their trade link token*. Offers the account sends
confirm themselves when its authenticator is here, and otherwise wait for you in the Steam app.

`match` shows swaps of spare cards between your own accounts that help both finish sets. `match do` sends them, and
the other account accepts by itself.

**Fair card swaps** (*Accept fair card swaps*, off) are one-for-one card swaps from anyone, like the ones
card-swapping sites send. One is accepted only when every item is an ordinary trading card from the same game, one
for one, and the swap never sets your sets back: every card you give away must still have more copies afterwards
than any incoming card had before. Swaps that would sit in a trade hold are left alone. A swap with a stranger is
accepted, but its confirmation waits for you in the Steam app (or on the Authenticator page). Swaps between accounts
in your own nocat.farm - the ones `match` sends - are always judged this way and accepted by themselves, even with
the setting off. `fairswap myaccount <offer id>` tells you whether an offer passes and why not, without accepting or
declining it.

Items from games the account is banned in can't be traded. Ban watch adds those games to *...but not these games*
by itself, and you can add more.

## The Authenticator page

This is for accounts whose maFile is in `config/authenticators`, or whose secrets are in their settings. The
dashboard's **Authenticator** tab shows, for each one, the current Steam Guard code with a ring counting down its 30
seconds and a Copy button. Below that are the confirmations waiting - trades with the items on both sides, market
listings, account changes - each with **Confirm** and **Deny**. *Select all*, then *Confirm* or *Deny*, does several
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

*Watch for bans* is on by default. Every few hours it checks each account for VAC bans, game bans, a trade ban or a
community ban, and when a new one appears it tells you straight away with a log line, a pop-up and a
Discord/Telegram message. It only looks and never changes anything, apart from keeping games the account is banned
in out of trades. `bans [account|all]` checks now.

## Inventory value

```
value                    # every account, by game, and the change in the last 24 hours
value myaccount refresh  # read that account's inventory again
```

Items are priced at the Steam market's median, in *Inventory prices in* (US dollar by default - match it to your
Steam store). Inventories are read again every 6 hours. Prices are looked up slowly so Steam doesn't refuse, which
means the first valuation of a big inventory can take hours. Turn *Work out what its inventory is worth* off on
accounts that only hold a few cards. Games listed in *...but not these games* are left out.

## History charts

The Overview page has charts under Today: **Cards dropped**, **Hours banked**, **Hours by game**, **Inventory
value**, and **rep4rep comments** when rep4rep is on. Pick 7, 30 or 90 days, and all accounts (*Whole fleet*) or
just one. A line compares this week with last week. Daily totals are kept for about 400 days in
`config/state/history/`.

---

[← Human mode](human-mode.md) · [Achievements and the hunter →](achievements.md)
