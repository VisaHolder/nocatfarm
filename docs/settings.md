[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Settings and commands

## Settings

On the dashboard's Settings page, pick **Global settings** (things for the whole app) or an account on the left.
Everyday switches show first; tick **Show advanced** for the rest. A new account shows about 28 settings (33 in human
mode), and the global settings 14. Search finds a setting by its name or its explanation, **Only changed** shows only
what you've changed, and each changed field has a link back to its default. Tap or hover the ⓘ beside any setting for a
plain explanation: what it does, an example with numbers, and what each choice changes. Settings marked ⟳ need a restart.

Each account has 12 sections, the everyday ones first and **Items and spending** at the end: spare items, booster packs,
Steam Points and buying cards, together, all off until you turn them on. Inside a section, parts have a heading of their
own (*Scam watch* and *Account health* in *Safety*), and settings sit in small titled boxes - Spare items, for example, is
*What it does*, *What counts as spare*, *Never touch*, *Limits*, *Selling* and *Gems*. A setting that only matters while
a switch is on sits indented under that switch, and is hidden while the switch is off: with *Deal with spare items* off
you see only that switch and one line saying what turning it on does.

A number shows its unit after the box (*minutes*, *days*, *%*, *wallet currency*), and where 0 means something of its
own it says so: *0 = no limit*, *0 = spend nothing*. A shortest and a longest are one row: *Breaks: 2 to 30 minutes*.
*What counts as spare* is one row of chips. A proxy's user name and password open under *needs a sign-in*.

A few internal settings - how often each feature looks, Steam's rate-limit waits, pacing details - aren't on the page at
all. They still work from the console, in `config <account> all`, with `set` and `help`, and in the JSON files.

From the console:

```
config myaccount            # its everyday settings and their values
config myaccount all        # every setting
set myaccount FarmCards false
set WebPort 8080            # no account = a global setting
help FarmCardsWhen          # what a setting does, its default and range
```

On/off settings take `true`/`false` (or `on`/`off`). Game lists take appIDs or store links separated by commas, or
`none`. On the dashboard, a game list's box searches the account's own games by name (every account's, for a global
list): type part of a name and pick it. A pasted appID or store link works there too, for a game it doesn't own. A choice takes its number or the start of its label, so `set myaccount OnlineStatus invisible` works.

Settings live in `config/nocatFarm.json` (global) and `config/<account>.json`, as plain JSON under the same names.
You can edit them by hand and type `reload`. Back up the `config` folder and you've backed up everything.

### Global settings

| Section | Everyday | Behind Show advanced |
|---|---|---|
| General | *Language*, *Start with Windows* (Windows), *Show inventory values in* (the currency) | the tray icon, *Start hidden*, *Minimise to the tray*, *Keep mini mode on top*, *Mini window shows* (hours today), *Keep this PC awake*, *Close when everything's done* |
| Notifications | Telegram bot token, Discord webhook, what gets sent (chips, *Scam warnings* and *Health alerts* among them, on), a test button; *Show pop-ups* | which pop-ups (earnings, comments, problems, trade offers, scam warnings, account health); commands from Telegram and Discord and the Discord bot (see [Discord and Telegram](phone-and-notifications.md#discord-and-telegram)) |
| Updates & plugins | *Updates*: ask me and install once my accounts are asleep, ask me and install right away (the default), install by itself at night, or don't look for updates; *Count me as a user* (on: once an hour tells nocat.lol it's running and how many accounts are signed in, as a number only, see [Privacy and safety](safety-and-faq.md#privacy-and-safety)); right under it *Share my farming totals* (on, only while *Count me as a user* is on: the hourly message also carries the game-hours banked, cards dropped, items turned into gems, badges crafted, packs opened and items sold since the last one, as numbers only, for the totals on nocat.lol's nocat.farm page; the first time, it also sends once, the totals already in its history from before - hours and cards, the only ones it kept before 1.8.1) | *Share cheapest sets with other nocat.farm users* (on, only while *Count me as a user* is on: while an account buys the cheapest full sets, about every 6 hours the set finder sends nocat.lol the cheapest card sets it found - market prices only - and takes back the ones other copies found, only as which games to check first, see [Buying cards](cards-and-idling.md#buying-cards)), *Install by itself between* (3 to 6), *Wait after a release for* (2 hours), *Remind me every hour*, *Load plugins* |
| Backups | *Back up every day* (off), *Save backups in*, *Backup password* (see [Daily backups](dashboard.md#daily-backups-that-open-on-any-pc)) | *Keep the last* (7 backups), *Back up at* (4 am), what's included, *Backup messages* (only when one fails; or off, or after every backup), *Also keep a copy here* |
| All accounts | *Restart a stuck account* (on, see [Stuck accounts](phone-and-notifications.md#stuck-accounts)) | *Stuck after* (3 hours), *Groups every account joins*, *Never play these games (all accounts)*, *Open in Steam buttons on every account*, *Devices of yours to ignore* (see [Scam watch](trades.md#scam-watch)) |
| rep4rep | *Use rep4rep*, *API token* | *Register accounts automatically*; *Pause rep4rep for...* is a button (6 to 48 hours), or `rep4rep hold <hours>` |
| Dashboard & phone | | *Web dashboard*, opening the browser, *Stay signed in for* (7 days), *Listen on*, *Port*, *Dashboard password*; *Open from outside*: *Open from anywhere*, *Ask for a Telegram code when signing in from outside* (on), *Switch off Open from anywhere after this many failed sign-ins a day* (5; 0 never), *Public address*, *Trust forwarded addresses from*; *Show how to install it on a phone* (on, see [The phone app](phone-and-notifications.md#the-phone-app)) |
| Discord profile | the *Playing nocat.farm* card: its switch, what it shows, a preview | which accounts, the featured account, the two buttons |
| Steam connection & logs | | *Accounts farming at once* (0 = no limit), *Connect using*, *Proxy* (a user name and password under *needs a sign-in*); the log: *Write a log file*, *Keep logs for* (14 days), *Show debug detail on screen*, *Say what it's doing every* (5 minutes while playing) and *While resting, say what it's doing every* (30 minutes; 0 turns them off), the daily summary and *Daily summary at* (09:30), the *Weekly report* (off) with its day and hour |

At the bottom of Global settings is **Backup & restore**. See [Backup and restore](dashboard.md#backup-and-restore).

On Linux and in Docker the Windows-only settings (tray, pop-ups, start with Windows and so on) aren't shown. They
stay in the config file untouched, in case you take it back to Windows.

### Account settings

| Section | Everyday | Behind Show advanced |
|---|---|---|
| Account | *Run this account*, *Steam account name*, *Password*, *Appear as*, *I also use this account in my own Steam app* | QR sign-in, *Start paused*, the *Open in Steam button*, notes, Family View PIN, device name; *Stand down when you play* and *Wait before resuming*; the Steam Guard shared_secret and identity_secret; its own proxy; *Clear Steam's notification counters* |
| Human mode | *Play like a person (human mode)*, *Games and how often*, hours on weekdays and at the weekend, *Gets on at* and *Goes to bed at* (13:00 and 02:00; the window is a hard limit), *Learn from how I play*, *Reaction speed*, *Bank hours overnight* with its games | how far each time is *give or take* (2 hours and 1 hour), hour targets, day-off chance (5%), *Stays up later Fri/Sat* (2 hours), *Sleeps* (6 to 9 hours), *Sittings*, *Breaks*, meals, going offline on breaks, *Quiet spells now and then*, new games, friends' games |
| What it plays | *Protect refunds* (on for a new account); on robot accounts *Games to idle*, *Idle my whole library*, *Rotate the idle list*, *Show a custom game name* (off); *Never play these games* | *Protect refunds only for games over* (0 = every game), *Rotate every*, *Keep the custom name while farming cards*, *Play as if on*; *Steam Family*: *Include family-shared games* (off), and while it's on *Give a shared game back when they want it*, *Borrow another family member's copy when one is in use* and *Wait after a family member stops* (20 minutes) |
| Trading cards | *Farm trading cards*, *When to farm cards* (human mode; with *mixed*, its share of sittings) | *Play each game this long before cards can drop*, order, priority list, *Farm cards only between* and sittings (robot accounts), *After the last card, keep playing* (human mode), *Give up on a game after*, and more |
| Free stuff | *Claim free games* (off, games, or games and DLC); *Take part in Steam events* (on, see [Sale events](cards-and-idling.md#sale-events)) | human-mode accounts too, Steam Awards nominations and votes, event items, the discovery queue, how nominations are picked |
| Badges | *Craft badges from card sets* | *Also craft foil badges* (on), *Sell a set instead of crafting it when the money buys more levels* (on, but only does anything while *Buy missing cards* is on - see [Selling a set instead of crafting it](cards-and-idling.md#selling-a-set-instead-of-crafting-it)) |
| Achievements | *Earn achievements over time*, and while it's on *How fast* and *Achievement hunt* | *Never complete more of a game than* (90%), *Quick bursts only for achievements held by at least* (10%: rarer ones never come a few minutes after another; 0 lets any burst, 100 turns bursts off), *Skip multiplayer achievements* (follows human mode), only/never lists, game length, the hunt (games hunted at once, *Move to another game at*, *Come back to a game after*, a daily cap) |
| Safety | *Watch for scams* (on), *Keep an eye on account health* (on) | *On a warning: stop trading* and *take back sent offers* (both on), *On a new API key: revoke it* (off), each check on its own - see [Scam watch](trades.md#scam-watch); which changes alert, a new ban among them (*Alert on a new ban*, on) - see [Account health](trades.md#account-health) |
| Trades | *Accept donations*, *Swap spare cards with other nocat.farm users* (off; it shows what it would do first), *Your other accounts* | gifts and their wait, *Accept 1:1 card swaps from anyone*, *Decline everything else*, the wait before answering, *Send items to* (a name or SteamID64) and sending items, *Show inventory value* and games left out of it and of trades |
| Friends & messages | *Reply to messages* (off) and, while it's on, its text; *rep4rep commenting* while rep4rep is on: *Post rep4rep comments* | *Accept friend requests* and, while it's on, its waits and filters; group invites, *Join the shared groups* (off on a new human-mode account), *Accept commands from*; rep4rep's cap, gaps and hours |
| Items and spending | *Deal with spare items*, *Make booster packs*, *Open booster packs* (never by default on every account), *Spend Steam Points on levels*, *Buy missing cards* - all off until you turn them on, and the ones that sell, spend or swap show what they would do first and ask you to confirm; once one is on, its money limits show without Show advanced | spare items: what counts as spare (chips), *Keep this many of each item* (1), the price limit (0.25), new-item days (7), keep list, daily caps (10 items, 1.00) - all in each account's own Steam wallet currency, selling price, *Only sell if you get at least* (0.05), unsold listings stepping down (never under 80%), declined listings (30 days), gems; booster packs: which gems, *Gems packs never use*, *Use gems for the most levels*, new-pack days (7), packs it doesn't open; Steam Points: *Always keep at least* (1000 points), *Points a week, at most* (3000; 0 = spend nothing), badge level; buying cards: *Buy the cheapest full sets* (off), *Spend at most a day* (1.50) and *a week* (8.00; 0 = spend nothing) - see [Spare items](cards-and-idling.md#spare-items-gems-sell-or-keep), [Levelling with gems](cards-and-idling.md#levelling-with-gems), [Steam Points into levels](cards-and-idling.md#steam-points-into-levels) and [Buying cards](cards-and-idling.md#buying-cards) |
| Logging | | *Report in every* and *While resting, report in every* (0 follows the global setting, -1 keeps this account quiet), and its colour in the log |

Settings that became one in 1.8.2 still load from an older config, and nothing changes what it does: the scam and
health alerts' Telegram, Discord and pop-up switches are the *Scam warnings* and *Health alerts* chips and pop-up kinds
(a channel you'd switched off on its own stays off until you change the chip); *Watch for bans* is account health's
*Alert on a new ban*; *Accept anything from your own accounts* put *Your own accounts* into *Your other accounts* both
ways; *Notify if an update is available* off is *Don't look for updates*; the summary's hour and minute are *Daily summary
at*; *Send backups* and *Tell me* are *Backup messages*; and the three keep-counts of spare items are one, the largest.
New accounts start with *Protect refunds* on, the auto-reply and the custom game name off, and - in human mode - out of
the nocat.farm group; an account you already have keeps what it had.

Hours of the day (when it gets on and goes to bed, when it farms, installs updates, sends items, comments, writes the
summaries) are picked from a list in your own clock - "9 am" or "09:00", whichever your browser uses. In the Console,
`set` takes the hour as a number (21), or like 9pm or 21:00.

On a human-mode account, *Games to idle*, the idle rotation, the custom game name, *Keep the custom name while farming cards*, *Farm in sittings*,
*Farm this long a day*, the *Farm cards only between* clock window, *Log out when finished* and *Farm while appearing offline* are hidden, and human-only
settings show instead.

## Command line options

These are for shortcuts, scripts and running several copies:

```
--path <dir>    where config/ and logs/ live (default: next to the exe)
--no-web        don't start the dashboard
--no-tray       no notification-area icon
--no-gui        no window - the console board instead (also --console)
--minimized     start hidden, straight to the tray (also --background)
--help          list these (also -h)
```

One copy runs per config folder, on Windows, Linux and a Mac alike; a second one says so and exits. To run separate sets of accounts, give each its
own `--path` folder.

### The setup's command line

The Windows setup takes the same choices as its screen, for scripted installs:

```
nocat.farm-v…-setup.exe /VERYSILENT /DIR="C:\nocat.farm" /STARTUP=no /DESKTOP=no /HIDDEN=no /MOVE=no /PORT=7300 /FROM=fresh
```

`/FROM` is `fresh`, `asf`, `idlemaster` or `other`. Leave any of them out to get the screen's default. To uninstall
without questions: `unins000.exe /VERYSILENT` keeps your accounts and settings, `/DELETEDATA=yes` deletes them too.

### Environment variables

Environment variables work on any install, not just Docker. When set, they win at every start (and after a restore)
and are written into `config/nocatFarm.json`. Take `NOCATFARM_PUBLIC_ADDRESS` or `NOCATFARM_TRUSTED_PROXIES` away again
and the next start clears what it had set, unless you've changed it in Settings since:

| Variable | What it sets |
|---|---|
| `NOCATFARM_WEB_HOST` | *Listen on* (`WebHost`). |
| `NOCATFARM_WEB_PORT` | *Port* (`WebPort`, 7242 by default). |
| `NOCATFARM_WEB_PASSWORD` | *Dashboard password*. |
| `NOCATFARM_WEB_PASSWORD_FILE` | A file to read the password from (how Docker secrets arrive). Wins over the one above. |
| `NOCATFARM_PUBLIC_ADDRESS` | *Public address* (`WebPublicAddress`): the address it's reached at from the internet, like `farm.example.com` or `203.0.113.7:7242`. On a VPS it's what lets visitors from outside in at all - see [On a VPS](linux-docker-vps.md#on-a-vps-a-rented-server). |
| `NOCATFARM_TRUSTED_PROXIES` | *Trust forwarded addresses from* (`WebTrustedProxies`): a reverse proxy that isn't on this PC, like Caddy in front of Docker (`172.16.0.0/12`). Its X-Forwarded-For is believed, so internet visitors count as the internet. A proxy on this PC needs nothing. |
| `NOCATFARM_HOME_ADDRESS` | Your computer's address on your Wi-Fi, like `192.168.1.20` (add `:port` if Docker publishes a different one). Inside Docker nocat.farm can't see it, so without this the Phone page and `/dashboard` can't show the home link or its QR code, and tell you what to open instead. |
| `NOCATFARM_PING_URL` | For the browser test only: sends the *Count me as a user* ping to a stand-in on this PC instead of nocat.lol. Only an address on this PC is taken; anything else is ignored. |
| `NOCATFARM_NETLOG=1` | For troubleshooting: writes every Steam message to `netlog-<account>.txt`. |

## Commands

`help` lists every built-in command, and `help <command>` or `help <setting>` explains one. Only know how it starts? `help rot` lists every command and setting that starts with "rot". `plugins` lists the
commands plugins have added. The dashboard's Console tab runs the same commands with the same output. Commands
that show `<account|all>` also take `all`, and aliases are shown after the name.

[Every command, in one list](COMMANDS.md) is made from the app's own command list, so it always matches the version
you have. It starts with every command at a glance, grouped into accounts, playing, trading cards, trades, Steam
Guard, achievements, free stuff and keys, profile and info, rep4rep, settings, and the app itself.

Replies to typed commands are in English. The dashboard, status lines and log follow your *Language*.

### Commands by Steam chat

You can also control an account by messaging it on Steam. Put your SteamID64 in that account's
*Accept commands from* (`CommandMasters`), then send a message starting with `/` or `!`, like `/help` or `!status`.
A bare command such as `/pause`, `/status` or `/2fa` acts on the account you messaged. Messages without `/` or `!` get
the normal auto-reply, and commands from anyone not on the list are ignored. Replies are cut at 1,900 characters.
Steam chat only reaches the account you messaged: a command that names another account, or `all` for every account, is
refused. A name it doesn't know (a typo) is answered with "there's no account called ...", never taken as the account you
messaged. Also refused there are the ones that change the app itself or show every account: `set`, `anywhere`,
`dashboard`, `update`, `import`, `redeem`, `keys`, `answer`, `add`, `reload`, `plugins`, `notify`, `screen`, `match`,
`theme`, `mini`, `visitors`, `backup`, `report`, `stuck`, `log`, `stats`, `owns`, `rep4rep`, `exit` and `remove`. And
anything that spends, sells or decides for you: `sell ... do` and `sell ... relist`, making booster packs
(`booster <appIDs>`), `booster open ... do`, `spare on` and `spare do`, `points on` and `points spend`, `scamwatch ok`,
and `dlc leave` / `dlc undo`. Looking (`sell`, `booster`, `spare preview`, `points`, `dlc`) and turning things off
(`spare off`, `points off`) are fine. Those are for the PC, the dashboard, Telegram or Discord.

---

[← rep4rep](rep4rep.md) · [Linux, Mac and Docker →](linux-docker-vps.md)
