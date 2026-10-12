[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Trades

Out of the box, an account accepts donations, which are offers where it gives nothing away (*Accept donations*,
on), and leaves every other offer for you. Offers from your own accounts can go through by themselves once you set that up
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
in your own nocat.farm (the ones `match` sends) are always judged this way and accepted by themselves, even with
the setting off. `fairswap myaccount <offer id>` tells you whether an offer passes and why not, without accepting or
declining it.

Items from games the account is banned in can't be traded. Ban watch adds those games to *...but not these games*
by itself, and you can add more.

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

*Watch for bans* is on by default. Every few hours it checks each account for VAC bans, game bans, a trade ban or a
community ban, and when a new one appears it tells you straight away with a log line, a pop-up and a
Discord/Telegram message. It only looks and never changes anything, apart from keeping games the account is banned
in out of trades. `bans [account|all]` checks now.

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
| Shortest wait between looks / ...up to (minutes) | 30 / 60 minutes | Each wait is a random time between the two. |
| Scam warnings on Telegram / Discord / as a pop-up (Global settings, Scam watch) | on | Where warnings go. |
| Devices of yours to ignore (Global settings) | empty | Your own device names, as Steam lists them, comma separated. |

## Account health

*Keep an eye on account health* is on by default. Each account card gets one **Health** line, green, amber or red:
level, bans, trade hold, market access, limited account, Steam Guard and phone. When everything is fine it says
*all good · level 12*; otherwise what isn't, like *market: blocked for 7 day(s) (new device)*. While it's on, it
takes the place of the old bans line.

It checks a few minutes after the account signs in, then every 5 to 7 hours. It reads only what nocat.farm already
reads (the ban watch, scam watch's account page, the trade-hold lookup, what Steam says at sign-in) plus one look at
the Community Market page. When something gets **worse** it tells you once (log, pop-up, Discord/Telegram); getting
better is only written in the log. A new ban is told once: by the ban watch while *Watch for bans* is on, and by
health while it's off (health then reads the bans itself at each check). `health [account]` shows every item.

### What each account health setting does

| Setting | Default | What it does |
|---|---|---|
| Keep an eye on account health | on | The Health line and the alerts, per account. |
| Alert on a new ban / a trade hold / the market blocked / a limited account / Steam Guard weaker / the phone removed | on | Which changes alert. Off still writes them in the log. A ban is only alerted here while *Watch for bans* is off. |
| Shortest wait between health checks / ...up to (hours) | 5 / 7 hours | Each wait is a random time between the two. |
| Health alerts on Telegram / Discord / as a pop-up (Global settings, Account health) | on | Where alerts go. |

## Inventory value

```
value                    # every account, by game, and the change in the last 24 hours
value myaccount refresh  # read that account's inventory again
```

Items are priced at the lowest price each one is listed for on the Steam market, in *Inventory prices in* (US dollar
by default; match it to your Steam store). Inventories are read again every 6 hours. Prices are asked a whole game's
cards, backgrounds and emoticons at a time, and every wear of a skin at once, where the market allows it - so even a
big inventory is a few dozen requests, not one per item. Requests are still spaced out so Steam doesn't refuse; the
Inventory tile shows how many are left and about how long that is. Turn *Work out what its inventory is worth* off on
accounts that only hold a few cards. Games listed in *...but not these games* are left out.

## History charts

The Overview page has charts under Today: **Cards dropped**, **Hours banked**, **Hours by game**, **Inventory
value**, and **rep4rep comments** when rep4rep is on. Pick 7, 30 or 90 days, and all accounts (*Whole fleet*) or
just one. A line compares this week with last week. Daily totals are kept for about 400 days in
`config/state/history/`.

---

[← Human mode](human-mode.md) · [Achievements and the hunter →](achievements.md)
