# nocat.farm - the guide

nocat.farm runs your Steam accounts in the background. It farms their trading cards, idles games for playtime, and
can make an account play like a real person. It runs on your own PC - no sign-up, no server.

This guide has two parts:

- **[Start here](#part-1-start-here)** - for everyone. Install it, add an account, and learn the few things most
  people change. No experience needed.
- **[Advanced](#part-2-advanced)** - for when you want more: tuning human mode, trades, phone notifications, every
  command and setting, Linux and Docker, plugins.

The app works the same way. The first-run setup offers **Quick setup**, a **Full tour** or **Advanced setup**, and the Settings page
shows the everyday switches first, with a **Show advanced** box for the rest.

The [front page](../README.md) is the one-minute version.

**Contents**

- Part 1 - Start here: [Words to know](#words-to-know) · [Install and run](#1-install-and-run) ·
  [The first-run setup](#2-the-first-run-setup) · [What it does by itself](#3-what-it-does-by-itself) ·
  [Finding your way around](#4-finding-your-way-around) · [Things most people change](#5-things-most-people-change) ·
  [Everyday commands](#6-everyday-commands) · [Updating](#7-updating) ·
  [If something looks wrong](#8-if-something-looks-wrong)
- Part 2 - Advanced: [Dashboard and window](#the-dashboard-and-the-app-window) · [Accounts and signing in](#accounts-and-signing-in) ·
  [Card farming](#card-farming) · [Idling and a custom game name](#idling-and-a-custom-game-name) ·
  [Human mode](#human-mode) · [Staying out of your way](#staying-out-of-your-way) · [Free stuff](#free-stuff) ·
  [Trades](#trades) · [Authenticator page](#the-authenticator-page) · [Ban watch](#ban-watch) ·
  [Inventory value](#inventory-value) · [History charts](#history-charts) ·
  [Achievements](#achievements-and-the-hunter) · [Booster packs, badges, selling, level-up](#booster-packs-badges-selling-and-level-up) ·
  [Product keys](#product-keys) · [Steam groups](#steam-groups) · [Discord and Telegram](#discord-and-telegram) ·
  [Daily summary](#daily-summary) · [rep4rep](#rep4rep) · [Commands](#commands) · [Settings](#settings) ·
  [Command line](#command-line-options) · [Linux and Docker](#linux-and-docker) · [Plugins](#plugins) ·
  [Privacy and safety](#privacy-and-safety) · [FAQ](#faq) · [Building from source](#building-from-source)

---

# Part 1: Start here

## Words to know

| Word | What it means |
|---|---|
| **Trading cards** | Many Steam games drop a few collectible cards while you play them - usually about half the full set. You can sell them, trade them, or turn a full set into a badge. |
| **Farming cards** | Letting a game "play" until all its free cards have dropped. |
| **Idling** | Telling Steam you're playing a game when nobody is. The game doesn't really run, but the playtime counts. |
| **appID** | A game's number. It's in the game's Steam store link: `store.steampowered.com/app/`**`730`**`/...` is Counter-Strike 2. You can paste the whole link wherever an appID is asked for. |
| **Human mode** | Makes an account play like a person - one game at a time, breaks, bedtime - instead of running around the clock. |
| **Badge** | What a full set of cards turns into. Badges raise your Steam level. |
| **Steam Guard** | The extra code Steam asks for when you sign in, from your email or the Steam app on your phone. |
| **Authenticator / maFile** | The Steam app's Steam Guard, stored as a file. A `.maFile` comes from tools like Steam Desktop Authenticator or ArchiSteamFarm. With it, nocat.farm can type Steam Guard codes and confirm trades by itself. You don't need one. |
| **SteamID64** | A long number that identifies a Steam account, like `76561198000000000`. Some trade settings ask for it. |
| **Dashboard** | nocat.farm's web page. It runs on your own PC and opens in your browser. |
| **Console** | Where you type commands. It's in the app window and on the dashboard's Console tab. |
| **ArchiSteamFarm (ASF)** | Another card farming program. nocat.farm can bring your accounts across from it. |

## 1. Install and run

**On Windows:**

1. Download `nocat.farm-v….zip` from the [latest release](https://github.com/VisaHolder/nocatfarm/releases/latest).
   (The files ending `_linux-x64` and `_linux-arm64` are for Linux.)
2. Right-click the zip and pick **Extract All…**. This gives it a folder of its own.
3. Open the folder and run **`nocatFarm.exe`**.

Nothing else to install. A small window opens, and the dashboard opens in your browser at
`http://127.0.0.1:7242/`.

On Linux or a server, see [Linux and Docker](#linux-and-docker).

## 2. The first-run setup

The first time, the dashboard walks you through a short setup. You can skip it any time, and replay it later from
the Overview page (**Show the walkthrough again**).

1. **Language.** Pick yours. It changes straight away.
2. **Quick setup, Full tour or Advanced setup.**
   - *Quick setup* goes straight to adding your account. About a minute.
   - *Full tour* explains each feature on the way. A few minutes.
   - *Advanced setup* is for people who've used ArchiSteamFarm or another idler: add the account, and it opens
     all of that account's settings with the advanced ones showing.
3. **What kind of account is it?**
   - **My main - I play on it.** It's added in human mode, so it acts like a person. You can also tick
     *I also sign into it from my own Steam app* - then nocat.farm never changes your online status, so it never
     kicks you off Friends & Chat.
   - **A spare or farm account.** Robot mode: it farms cards and idles games around the clock, at full speed.

   You can change this later with the **Human mode** switch in the account's settings.
4. **Add the account.**
   - Type the **Steam account name** (what you type to sign in to Steam - not your display name or email).
   - The **password** is optional. Leave it empty and you're asked once when it signs in.
   - In the full tour and advanced setup you can tick **Sign in with a QR code** instead. You scan a code with the Steam app on your
     phone, and nothing is typed at all.
   - **Using ArchiSteamFarm?** If the setup finds it, it lists your ASF accounts instead. Tick the ones you play on
     yourself (they come across in human mode) and press **Import them**. Close ASF first, so two programs aren't
     signed in to the same account.
5. **Sign in.** The Steam Guard code, password or QR code is asked for right there in the setup.
6. **Pick its games** (optional). A human-mode account shows its most-played games: tap a main game and a few side
   games. The full tour also asks for its daily routine (hours a day, when it gets on, bedtime). A farm account can
   pick games to idle and a custom game name. **Skip - use the defaults** is always there.

After the first sign-in, nocat.farm keeps a Steam login token, so it signs itself in from then on. No password is
stored unless you type one into the settings.

## 3. What it does by itself

With no setup at all, every account:

- **farms its trading cards**, one game after another, and tells you roughly how long is left
- **idles the games you gave it** once the cards are done (if you gave it any)
- **stands down when you play.** Start a game yourself on that account and it stops. It carries on a few minutes
  after you finish.
- **accepts gifts** - Steam wallet gift cards, guest passes, and games friends gift it
- **accepts donations** - trade offers where you give nothing away
- **collects free event items** - the daily sticker during a Steam sale, and anything free in the Points Shop
- **goes through the discovery queue during Steam sales**, which earns sale items and badge progress
- **watches for bans** and tells you straight away if one appears
- **joins the nocat.farm Steam group** (you can change or clear this - see [Steam groups](#steam-groups))
- **clears Steam's notification counters**
- **writes a daily summary** in the log at 09:30
- **checks for updates** every few hours, and tells you. It never installs anything by itself.

These are **off** until you turn them on: human mode (unless you picked "My main"), claiming free games, earning
achievements, selling cards, crafting badges, fair card swaps, Discord and Telegram, rep4rep and plugins.

## 4. Finding your way around

**The dashboard** is where you do almost everything. Its tabs:

- **Overview** - every account at a glance, today's cards, and history charts.
- **Accounts** - one card per account: what it's doing, and Start, Pause, Stop, Cards and Settings buttons.
  **+ Add account** is here.
- **rep4rep** - only there if you turn rep4rep on. Most people never see it.
- **Authenticator** - Steam Guard codes and trade confirmations, for accounts with an authenticator.
- **Log** - what happened, in plain words.
- **Console** - type commands.
- **Plugins** - add-ons (off unless you turn them on).
- **Settings** - every switch. Pick **Global settings** or an account on the left.

**The app window** (Windows) shows the log, a command line, and buttons to start or stop everything, open the
dashboard and list your accounts. Click **mini** to shrink it to a small panel with one line per account.

**The tray icon** by the clock has a menu: open the dashboard, show or hide the window, mini mode, start or stop
all accounts, and exit. Closing the window keeps nocat.farm running in the tray.

## 5. Things most people change

All of these are in **Settings** - pick the account on the left. Or type the command shown.

1. **Games to idle** (farm accounts). *What it plays → Games to idle*, or type `play myaccount 730, 440`. These play
   after the cards are done.
2. **A custom game name** (farm accounts). *What it plays → Show as*, or `name myaccount nocat.lol`. Your friends see
   that name instead of the real game, while the real games still count playtime.
3. **Human mode.** *Human mode → Human mode*. Then set **Games and how often** - for example `730:70, 440:30` means
   70% Counter-Strike 2, 30% Team Fortress 2 - and the hours it plays and its bedtime.
4. **Claim free games.** *Free stuff → Claim free games*. Paid games that Steam gives away free-to-keep
   are added to the account.
5. **I sign into this one myself.** *Account → I sign into this one myself*. Turn it on for any account you also
   use in your own Steam app.
6. **Notifications on your phone.** *Global settings → Notifications*: paste a Discord webhook, or connect a
   Telegram bot. See [Discord and Telegram](#discord-and-telegram).
7. **Start with Windows.** *Global settings → Running in the background → Start with Windows*.

Everyday switches show first. Tick **Show advanced** at the top of Settings for timings, limits and the rest.
Hover over any setting (or type `help <setting>`) to see what it does.

## 6. Everyday commands

Type these in the app window or the dashboard's Console tab. Use the name you gave the account.

| Command | What it does |
|---|---|
| `status` | What every account is doing right now. |
| `start myaccount` · `stop myaccount` | Sign an account in or out. `all` works too. |
| `pause myaccount 60` | Stop playing for 60 minutes, then carry on by itself. `resume myaccount` ends it early. |
| `cards` | Cards left to farm, and about how long it'll take. |
| `play myaccount 730, 440` | Idle these games. |
| `name myaccount nocat.lol` | Show this name instead of the real game. `name myaccount off` removes it. |
| `offers` | Trade offers waiting on your accounts. |
| `update` | Check for a new version. |
| `help` | Every command. `help <command>` or `help <setting>` explains one. |

`tutorial` shows a getting-started checklist that ticks off what you've done.

## 7. Updating

**Windows:** when a new version is out, the dashboard shows an **Update** button. Click it (or type
`update accept`). nocat.farm downloads the new version, signs your accounts out one by one, swaps the files and
restarts. Your accounts, settings, logs and plugins are kept.

**Linux and Docker:** it tells you when a new version is out, but you update it yourself. See
[Updating on Linux](#from-the-zip) and [Updating in Docker](#docker).

## 8. If something looks wrong

- **Check the Log tab first.** It says what happened in plain words.
- **"Steam Guard code needed" / it asks for a password.** Type the answer in the bar at the top of the dashboard,
  or at the `?` prompt in the app window. `answer <text>` works too.
- **An account stopped trying to sign in.** After 3 failed sign-ins in a row it stops, to protect your internet
  connection from Steam's limits. Check the password, then `start myaccount`.
- **"Rate-limited" or "too many logins".** Steam slows down a PC that asks too much (usually after lots of
  restarts). It waits it out by itself.
- **Steam kicks you off Friends & Chat.** Turn on *I sign into this one myself* for that account.
- **Friends see the real game, not your custom name.** The account card warns "Steam shows …" and nocat.farm puts
  the name back by itself. On a human-mode account there is no custom name - that's on purpose.
- **Nothing is farming.** Type `cards myaccount`. If there are no cards left, it idles its games (if any).
  If it says it's waiting, human mode may have it asleep or on a break - `human myaccount` shows its day.
- **The settings page looks short.** Tick **Show advanced**.
- **Still stuck?** Open an [issue](https://github.com/VisaHolder/nocatfarm/issues) and paste the log lines.

---

# Part 2: Advanced

Everything below is optional. Each section starts with who it's for.

## The dashboard and the app window

*For: anyone who wants the details of the interface.*

### Dashboard tabs

The dashboard is at `http://127.0.0.1:7242/`. Its tabs, in order:

- **Overview** - tiles for cards left, games left, cards today and inventory value; an accounts table; **Today**
  (the last 24 hours per account, with a total for all of them); **History** charts; and recent activity. The
  **Show the walkthrough again** link is here.
- **Accounts** - search by name, login, notes or game; filter chips by state; drag cards to change the order (the
  app window and console use the same order). **Import from ASF** and **+ Add account** are here. Removing an
  account asks you to type its name first.
- **rep4rep** - only shown when rep4rep is switched on.
- **Authenticator** - see [the Authenticator page](#the-authenticator-page).
- **Log** - live, with search, level chips (Debug is hidden by default), an account filter, Follow and Copy.
- **Console** - the same commands as the app window, with Tab completion (commands, then account names) and
  up/down history. A bare `help` opens a searchable command list.
- **Plugins** - always there; says "off" until plugins are turned on. See [Plugins](#plugins).
- **Settings** - search, **Show advanced**, **Only changed**, a "default" link to reset any changed field, and a
  **Save N changes** button. Settings marked ⟳ need a restart.

You can open a tab by address, like `http://127.0.0.1:7242/#settings` (also `#overview`, `#accounts`,
`#rep4rep`, `#auth`, `#log`, `#console`, `#plugins`).

**Keyboard shortcuts** (not while you're typing in a box):

| Key | Goes to |
|---|---|
| `1` | Overview |
| `2` | Accounts |
| `3` | rep4rep (Overview when rep4rep is off) |
| `4` | Log |
| `5` | Console |
| `6` | Settings |
| `` ` `` | Console |
| `Esc` | Closes a dialog |

Authenticator and Plugins have no number key.

### The app window (Windows)

- **Title bar:** *mini*, *hide*, *quit*.
- **Toolbar:** *start all*, *stop all*, *dashboard*, *accounts*, *commands*, and how many accounts are signed in.
- **Accounts panel:** each account with start/stop, pause/resume, cards and profile, plus *+ add account*.
- **Command line:** type a command and press Enter. `help` opens the command list.

Closing the window (or *hide*) sends it to the tray when there's a tray icon; without one, closing quits. The
window remembers its size and position.

### Mini mode

Click *mini* (or type `mini`, or use the tray menu). The window shrinks to a small panel: one line per account with
what it's doing and a start/stop button. A farming account also shows cards left, time left and a progress bar.

The title bar has a **pin** to keep it above other windows (the *Keep mini mode on top* setting, off by default),
a button for the dashboard, and one back to the full window. It remembers where you put it, and opens in mini mode
next time if you left it that way.

<p align="center">
  <img src="../assets/mini-mode.png" alt="Mini mode" width="680">
</p>

### Tray

Left- or right-click the icon for the menu: **Open dashboard · Hide/Show the window · Mini mode/Full window ·
Start all accounts · Stop all accounts · Exit nocatFarm**. Double-click shows the window.

*Start hidden* (setting) or `--minimized` starts straight to the tray. *Start with Windows* adds a start-up entry
for your Windows user.

### Console mode

`--no-gui` (or `--console`) runs without the window: a live board with a row per account and the last log lines
under it. Up/down arrows go through your last commands, and Esc clears the line. On Linux this is what you get.

### Using the dashboard from another device

By default the dashboard only answers this PC. To use it from your phone or another PC:

```
set WebPassword something-long-and-your-own
set WebHost 0.0.0.0
```

Then restart. Without a password it still refuses everything that isn't this PC. Five wrong passwords lock an
address out for 60 minutes. A browser stays signed in for 7 days (*Stay signed in for*). A banner warns you if the
password is short enough to guess. The layout works on a phone.

To turn the dashboard off completely: `set WebEnabled false` and restart. Everything keeps working from the
console.

<p align="center">
  <img src="../assets/overview.png" alt="The web dashboard" width="880">
</p>

## Accounts and signing in

*For: adding accounts by command, authenticators, and moving from ArchiSteamFarm.*

```
add myaccount mysteamlogin     # add an account and start signing in
add farm1 qr                   # sign in by scanning a QR code with the Steam app
```

- **Password.** Asked for once. Then a Steam Guard code (approving the sign-in in the Steam app works too). After
  that a login token in `config/tokens/` signs it in - restarts need no password and no code.
- **QR code.** `add <name> qr`, or tick *Sign in with a QR code*. Scan the code on the dashboard with the Steam
  app. The account name comes from Steam.
- **Answering prompts.** Questions show as a bar at the top of the dashboard and a `?` prompt in the app window.
  `answer <text>` answers from anywhere.
- **If the token stops working** (you changed the password, or signed out everywhere), it's thrown away and the
  password is asked for again.
- **3 failed sign-ins in a row** and the account stops trying, rather than risk Steam blocking your IP.
- **Steam's weekly maintenance** (Tuesdays) is recognised and waited out.

**Authenticator.** Put an account's maFile in `config/authenticators/`, named after the account's name in
nocat.farm - `config/authenticators/myaccount.maFile`. Then it enters its own Steam Guard codes and can confirm
trades. Or paste the two secrets into the account's settings (*Authenticator code secret* and *Authenticator confirm
secret*, under Show advanced). They're stored encrypted. nocat.farm does not create or move authenticators.

**I sign into this one myself** (`IUseThisAccount`). For an account you also use in your own Steam app. It then
never changes the online status, so your own app keeps Friends & Chat.

**Enable, disable, remove.** `disable myaccount` keeps the settings but never signs in; `enable myaccount` undoes
it. `remove myaccount` deletes the account and its login token.

**Coming from ArchiSteamFarm.** `import asf` (or **Import from ASF** on the Accounts page) looks for an ASF install
nearby, or give it ASF's config folder: `import asf C:\ASF\config`. Each bot comes across with its login token (no passwords, no Guard
codes), its games, custom name, farming order, priority list, blacklist, trade and gift settings, booster games and
rep4rep settings. A bot's maFile is copied if it's still in ASF's `config` folder. Imported accounts are added but
not started. Close ASF before starting them. Add `force` to overwrite accounts that already exist here.

## Card farming

*For: controlling which cards farm and when.*

On by default (*Farm trading cards*). It reads the account's badge pages, then:

1. Games that need more playtime before cards drop are played together, up to 32 at once (31 with a custom game
   name).
2. Then each game is played on its own until its cards stop dropping.
3. When no cards are left, it idles its games, or signs out if *Log out when finished* is on.

Drops are noticed the moment Steam announces them. `cards myaccount` shows what's left per game and about how long
it will take - Steam's usual 30 minutes a card at first, then the account's own pace.

Turn it off for one account: *Farm trading cards*, or `set myaccount FarmCards false`.

**Useful settings** (most under Show advanced): *Farm in this order*, *Farm these first*, *Only farm those*,
*Never touch these*, *Skip games you've never played*, *Hours before cards drop* (set 0 if the account has spent over
$5 on Steam), *Give up after* (8 hours per game), *Farm cards only from … until* (a clock window), *Farm while
appearing offline*, *Log out when finished*, and *Farm in sittings, not flat out* with *Hours a day to farm*.

**Farming on a human-mode account.** *When to farm cards* decides:

| Choice | What happens |
|---|---|
| **day, in its sittings** (default) | Cards farm in its normal sittings, with breaks, meals and bedtime. |
| **night, while it's asleep** | Cards farm only while it's asleep and invisible. By day it plays its usual games. |
| **any time** | Non-stop until the cards are done. |
| **mixed** | Some sittings farm cards (*Share of sittings that farm cards*, 40%), the rest play its usual games. |

After a game's last card it keeps playing it for 15-20 minutes before a break (*After the last card, keep
playing*).

**The `drops` command - you pick the game:**

```
drops myaccount 460920 2    # get 2 cards from game 460920 first
drops myaccount             # the next game with cards
drops myaccount off         # stop early
```

On a human-mode account the game plays in its normal sittings until those cards drop. On other accounts it plays
non-stop, and gives up if drops stop coming.

**Refund protection** (off by default, *Protect refundable games*). Games bought in the last 14 days with under
2 hours played are left alone by everything - farming, idling, grinds and the hunter - so you can still refund
them. Gifted games count too (*...gifted games too*, on).

## Idling and a custom game name

*For: farm accounts. Not available on human-mode accounts.*

- **Games to idle** (`play myaccount 730, 440`, or `play myaccount none`) - up to 32 games, played for playtime once
  the cards are done. You can paste store links.
- **Show as** (`name myaccount nocat.lol`) - friends see this name instead of the real game, while the real games
  still count playtime. `name myaccount off` removes it. *Keep the name while farming* (on) keeps it during card
  farming too.
- If Steam starts showing the real game instead, the account card warns "Steam shows …" and the name is put back.
- **Play as if on** - the device badge friends see: PC, phone, Big Picture, VR or controller.
- **Appear as** (`persona myaccount invisible`) - online, offline, busy, away, snooze, looking to trade, looking to
  play or invisible. Invisible still plays and farms.

Human mode hides the idle list and the custom game name, because an account showing a made-up game or playing 30
games at once doesn't look like a person. The idle list is set aside and put back exactly as it was if you turn
human mode off.

## Human mode

*For: accounts you want to look like a real player - usually your main.*

Turn it on per account: *Human mode*, or `set myaccount LegitMode true`. Then the account:

- plays **one game at a time**, in sittings of believable length, with a main game and a few side games
- has **different weekdays and weekends**, and now and then a **day off**
- takes **short breaks and meal breaks**, sometimes showing Away or offline
- **settles in** after signing in before starting a game. No game starts until at least 3 minutes after sign-in and
  several checks that you aren't playing on the account yourself - no setting shortens that.
- **goes to bed** at night, and can bank hours invisibly overnight (*Bank hours overnight* with *Games to idle
  overnight*)
- **answers like a person** - trades, gifts and friend requests each wait their own time, and while it sleeps they
  wait for morning (*Only react while awake*, on)
- does behind-the-scenes things (badges, free games, booster packs, selling) at any hour, but not the moment it
  signs in
- when you stop it, **finishes up for a few seconds** instead of vanishing mid-game

**What it switches off on that account:** games to idle, the custom game name, keep-name-while-farming, farm in
sittings, hours a day to farm, farm while appearing offline, and log out when finished. They're hidden while human
mode is on and come back when you turn it off.

**Setting it up.** The main settings are in front; the fine detail is behind Show advanced.

```
set myaccount GameWeights "730:70, 440:20, 550:10"   # games and their share - the first is the main game
set myaccount WeekdayHours 6                         # about 6 hours Monday to Friday
set myaccount WeekendHours 9
set myaccount DayStartHour 13                        # gets on around 1pm
set myaccount BedHour 2                              # goes to bed around 2am
```

Some days go on the main game alone (*Days on the main game only*, 25%), so side games show up in bursts. That
lowers their weekly share - the dashboard and `human` show the real weekly figure next to the one you set.

**Checking on it.**

```
human myaccount          # what it's doing today and what it played
human myaccount week     # a sample of the next seven days
human myaccount reroll   # throw today's plan away and roll a new one from the current settings
wake myaccount           # wake it up and start its day now (bedtime stays the same)
```

A day is planned once, when it wakes, so changes to its hours or main game show from tomorrow - or straight away
with `reroll`.

**Hour targets** (*Hour targets*, Show advanced). Get a game to a number of hours, optionally by a date:
`730:100@2026-12-01, 440:50` means Counter-Strike 2 to 100 hours by 1 December, TF2 to 50 whenever. Cards still
come first; after that sittings lean toward games that are behind. `hours myaccount` shows progress and the daily
pace a date needs.

**selfcheck.** `selfcheck myaccount` gives a human-mode account a score out of 100 for how real it looks from
outside - hours on the profile, games at once, a made-up game name, no daily rhythm, rep4rep comments - and names
the setting that fixes each problem. Without a name it checks every human-mode account.

**grind overrides it.** `grind myaccount 730 3` puts the account on one game for 3 hours, then back to normal. On a
human-mode account it eases in. With *Earn achievements over time* on, a grind earns achievements at the grind
spacing (*While grinding, one achievement every*, 12-24 minutes).

**Every wait is a setting** (Show advanced, human-mode accounts): *After waking, wait at least … up to* (10-90
minutes) for things people can see; *After signing in, behind-the-scenes things wait at least … up to* (5-60);
*Behind-the-scenes things wait for its day too* (off); *On a break, go Away after at least … up to* (2-10);
*Answer one trade offer at a time* (on).

## Staying out of your way

*For: accounts you also play on.*

- **Stand down when you play** (on). Start a game on the account yourself and nocat.farm stops playing on it.
- **Wait before resuming** (5 minutes). How long after you stop before it carries on. A human-mode account waits a
  random time, up to three times this.
- **I sign into this one myself** (off). Never changes the online status, so your own Steam app keeps Friends & Chat.
- **Clear Steam's notifications** (on) marks the notification tray read on every sign-in, and *Clear the new-items
  badge* (on) clears the green counter after each drop. Turn these off if you also use the account and want to see
  them.

## Free stuff

*For: collecting everything Steam gives away.*

| What | Setting | Default |
|---|---|---|
| Wallet gift cards and guest passes | *Accept gifts and guest passes* | on |
| Games friends gift the account | *Accept gifted games* | on |
| Daily sale sticker, 0-point Points Shop items | *Claim free event items* | on |
| Discovery queue (earns sale items) | *Go through the discovery queue*: off / during sales / every day | during sales |
| Paid games given away free-to-keep | *Claim free games* | off |
| Paid DLC marked down to free | *...free DLC too* | off |
| The DLC's game, if that's free too | *...and its game, if that's free too* | on |

- **Gifts** wait 2-15 minutes first (*Accept a gift after*). A gift is never declined. Turn *Accept gifted games*
  off to decide each gifted game yourself.
- **Free games** are found from Steam itself: the store's list of games at 100% off, and Steam's change feed. Only
  released, paid games showing 100% off are claimed - never free-to-play games, demos or "free editions".
  Steam allows about 30 activations per 90 minutes, so it stops at 20 to leave room for you.
- **Free DLC** needs the game. If the game is missing but free right now, it claims the game first, then the DLC.
- Commands: `freeitems [account|all]` looks for event items now; `queue [account|all]` does the discovery queue
  now.

## Trades

*For: moving items between your accounts and answering offers.*

**What happens by itself:**

- **Donations** - offers where the account gives nothing - are accepted (*Accept donations*, on).
- **Your own accounts** can trade with it by themselves, if you set that up (below).
- **Fair card swaps** are accepted only if you turn them on (*Accept fair card swaps*, off).
- **Everything else waits for you.** With *Decline everything else* (off) it's declined instead.

Each offer waits 2-15 minutes before it's answered (*Wait at least / And at most*). No Steam Web API key is needed.

**Trade alerts.** Every new offer is announced once - in the log, as a pop-up (*Pop up for trade offers*) and on
Discord/Telegram - with a short number:

```
new trade offer 4 from SomeGuy: you get 1 item(s): ..., you give 3 item(s): ... - waiting for you - trade accept myaccount 4 or trade decline myaccount 4
```

Accepted, declined, needs-confirming and gone (cancelled, answered elsewhere or expired) are announced too.

```
offers myaccount                  # every live offer, with the same numbers
trade accept myaccount 4          # accept offer 4
trade decline myaccount 4         # decline it (or: all)
trade cancel myaccount <offer id> # take back an offer this account sent that hasn't gone through (or: all)
```

Accepting one that sends items out also confirms it, when the account's authenticator is in nocat.farm.

**Trade by itself with** (`AutoTradeWith`). Which accounts it trades with without asking you, and which way. Account
names or SteamID64s, separated by commas:

- `farm1` or `farm1:both` - both ways (the default)
- `farm1:from` - only accept what farm1 sends
- `farm1:to` - let farm1 take items

Example: `farm1, alt:to`. Left empty, it means the accounts in *Your own accounts*, both ways, when *Accept anything
from your own accounts* is on.

**Sending items.**

```
send farm1                       # farm1's items to the first account in "Your own accounts"
send farm1 to myaccount          # farm1's trading cards to myaccount
send all to myaccount cards foils
```

Types: `cards`, `foils`, `backgrounds`, `emoticons`, `boosters`, `gems`, `all`. Without types, `send` uses the
account's *What to send* setting (trading cards by default), and `send … to` sends trading cards. `all` means
everything tradable, including game items like CS2 skins. Types you type in the command are sent as asked; the
automatic sends below always use *What to send*.

- *Send items every* (0 = off) sends on a timer. *Send items when farming finishes* sends once when the cards run
  out.
- Between accounts that aren't Steam friends, Steam needs the other account's trade link token. It's read by itself
  when that account is signed in here; otherwise set *Their trade link token*.
- Offers the account sends confirm themselves when its authenticator is here. Otherwise confirm them in the Steam
  app.

**Matching cards between your accounts.** `match` shows swaps of spare cards between your own accounts that help
both finish sets. `match do` sends them, and the other account accepts them by itself.

**Fair card swaps** (*Accept fair card swaps*, off). One-for-one card swaps from anyone, like the ones card-swapping
sites send. Accepted only when every item is an ordinary trading card from the same game, one for one, and the swap
never sets your sets back: every card given away must still have more copies afterwards than any card coming in had
before. Swaps that would sit in a trade hold are left alone. A swap with someone else is accepted, but its
confirmation waits for you in the Steam app (or the Authenticator page). Swaps between accounts in your nocat.farm
(the ones `match` sends) are always judged this way and accepted by themselves, even with the setting off.
`fairswap myaccount <offer id>` tells you whether an offer passes, and why not - it never accepts or declines.

**Banned games.** Items from games the account is banned in can't be traded. Ban watch adds those games to
*...but not these games* by itself; you can add them too.

## The Authenticator page

*For: accounts whose maFile is in `config/authenticators`, or whose secrets are in their settings.*

The dashboard's **Authenticator** tab shows, per account:

- the **Steam Guard code**, with a ring counting down its 30 seconds, and a Copy button
- **confirmations** waiting - trades (with the items on both sides), market listings, account changes - each with
  **Confirm** and **Deny**. *Select all* then *Confirm* or *Deny* does several at once. It refreshes every 30 seconds.

The same from the console or Telegram:

```
2fa myaccount           # its Steam Guard code (2fa alone lists every account's)
confirmations myaccount # what's waiting, numbered
confirm myaccount 2     # confirm number 2 (or: all)
deny myaccount 1        # deny number 1
```

An account with no authenticator here shows a note saying where to put its maFile.

## Ban watch

*For: everyone - it's on by default.*

*Watch for bans* (on) checks each account's bans every few hours - VAC, game bans, trade ban, community ban - and
tells you straight away when a new one appears: a log line, a pop-up and Discord/Telegram. It only looks; it never
changes anything. Games the account is banned in are left out of trades by themselves. `bans [account|all]` checks
now.

## Inventory value

*For: seeing what your items are worth.*

```
value                    # every account, by game, and the change in the last 24 hours
value myaccount refresh  # read that account's inventory again
```

Priced at the Steam market's median, in *Inventory prices in* (US dollar by default - match it to your Steam store).
Inventories are re-read every 6 hours; prices are looked up slowly so Steam doesn't refuse, so the first valuation of
a big inventory can take hours. Turn *Work out what its inventory is worth* off on accounts that only hold a few
cards. Games listed in *...but not these games* are left out.

## History charts

*For: seeing how things went over time.*

On the Overview page, under Today: **Cards dropped**, **Hours banked**, **Hours by game**, **Inventory value**, and
**rep4rep comments** (when rep4rep is on). Pick 7, 30 or 90 days, and all accounts (*Whole fleet*) or one. A line
compares this week with last week. Daily totals are kept for about 400 days in `config/state/history/`.

## Achievements and the hunter

*For: earning achievements slowly and believably.*

```
cheevo myaccount 440                  # its achievements, easiest first, with how rare each is
cheevo myaccount 440 unlock all       # all the ones that can be set, now
cheevo myaccount 440 unlock ACH_NAME  # just one
cheevo myaccount 440 lock ACH_NAME    # put one back
```

Unlocking a whole list at once shows on the profile forever, with one timestamp. **Earn achievements over time**
(`UnlockAchievements`, off) is the gentle way: easiest first, only in a game the account is actually playing,
roughly one per hour of play. It never finishes more than 90% of a game (*Finish no more than*), rare achievements
only open up as playtime builds, and milestones wait for the achievements they depend on. *How fast* makes the gaps
longer or shorter. *Never in these games* keeps it out of games you pick.

Some games' achievements are set by Steam's servers, not the client (Counter-Strike 2 is one) - nothing can unlock
those, and the log says so.

**The hunter** (*Achievement boost*, off) picks games to play so there's something to earn. It needs *Earn
achievements over time* on too.

- *games you pick* - works through *Boost these games*
- *all single-player* - finds single-player games with achievements in the library, played or not (never DLC, demos
  or games with few reviews). Turn on *Only hunt games you've played* to skip the ones the account never launched.

One game at a time, about 2 hours each (*Play each for about*), and it stops at your *Finish no more than* limit
(only a `grind` you type goes to 100%). Games with nothing left to earn are skipped, and it moves on once a game is
done.

On a **human-mode account** the hunter never takes the account over. The game it's hunting joins the games the
account plays, like one more side game in *Games and how often*, at *Human mode: hunt game's weight*. It's played in
ordinary sittings, with breaks and bedtime, and never cuts another game short. After about *Play each for about* of
playing (spread over those sittings), the next game on the list takes its place.

At first it may say "the store hasn't answered yet" for many games - it looks each one up slowly so Steam doesn't
block it, and starts as soon as it has some. `hunt myaccount` shows what it would play next and why other games were ruled out.

**Steam Families** (*Include family-shared games*, off) lets the hunter use games shared with the account, and
hands a game back when someone in the family starts it (*Give a shared game back when they want it*, on).

## Booster packs, badges, selling and level-up

*For: turning cards and gems into levels or money.*

- **Booster packs.** List games in *Make booster packs for*, and each gets a pack a day as Steam allows, using
  tradable gems first. `booster myaccount` shows gems and which games can be made now; `booster myaccount 730, 440`
  makes those packs now. *Open booster packs* (off) opens packs that land in the inventory.
- **Badges.** *Craft badges from card sets* (off) turns full sets into badges once a day. That's what raises your
  Steam level.
- **Selling.** `sell myaccount` shows which spare cards it would list and what you'd get after Steam's fees.
  `sell myaccount do` lists them (5 by default); `sell myaccount relist` takes down week-old listings the market has
  gone under. *Sell duplicate cards* (off) does it by itself every 8-14 hours. It keeps what you need for badges and
  never sells foils. Listings need confirming in the Steam app, unless the authenticator is here.
- **Level-up planning.** `levelup myaccount 50` says what reaching level 50 would cost: badges it can craft now, sets
  it has nearly finished, and the cheapest full sets on the market for the rest. Prices are looked up in the
  background - wait for "level plan … is ready" in the log, then run it again.
- `level`, `balance` and `points` show each account's Steam level, wallet and Points Shop points.

## Product keys

*For: activating Steam keys.*

```
redeem AAAAA-BBBBB-CCCCC            # tries each account until one can use it
redeem myaccount AAAAA-BBBBB-CCCCC  # only myaccount
redeem keys.txt                     # a text file full of keys
keys                                # keys still waiting
```

More than five keys queue up and activate slowly, because Steam limits activations. Keys given to a named account
only ever go to that account, queued ones too. `keys clear` empties the queue.

## Steam groups

*For: getting all your accounts into a group - or keeping them out.*

- **Groups every account joins** (*Global settings → All accounts*). It starts with the nocat.farm group. Paste group
  links or short names, separated by commas. **Clear the box to stop joining it.**
- **Join the shared groups** (per account, Show advanced, on). Turn it off to keep one account out.
- **Also join these groups** (per account) - groups only that account joins.
- **Accept group invites** (off) - join groups the account is invited to.

Each account joins at its own random time, minutes apart. Only open groups work; ones that need approval or an
invite are skipped, and the log says why. `joingroup <account|all> <link>` joins one now.

## Discord and Telegram

*For: notifications on your phone and control from anywhere.*

Set up under **Settings → Global settings → Notifications**. The dashboard has step-by-step guides for both.

In the log, lines from Telegram and Discord are marked `telegram` and `discord`. Their colours are under the
same section with **Show advanced** ticked (*Telegram's colour in the log*, *Discord's colour in the log*), from the
same palette as each account's *Colour in the log*.

**Discord** (notifications only):

1. In Discord: Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL.
2. Paste it into **Discord webhook** and save.

**Telegram** (notifications and commands):

1. In Telegram, message **@BotFather**, send `/newbot`, and copy the token it gives you.
2. Paste it into **Telegram bot token** and press **Save**.
3. Press **Connect Telegram**. It opens your bot with a private link - press **Start** there. The dashboard then says
   "Telegram is connected."

Only your own connected chat is listened to. Type `notify test` (or press *Send a test message*) to check both.
`notify` shows what's set up and what gets sent.

**What gets sent** - nine switches (Show advanced):

| Switch | Default |
|---|---|
| Send card drops and badges | on |
| Send free games, items and gifts | on |
| Send trades | on |
| Send problems that need you | on |
| Send updates | on |
| Send the daily summary | on |
| Send profile comments | off |
| Send achievements | off |
| Send rep4rep comments | off |

**Telegram commands** (*Take commands from Telegram*, on):

- `/status` - a summary of every account, the last 24 hours, and the version
- `/help` - the command list
- `/console` - console mode: type commands without the `/`. Send `/console` again to leave.
- Any other command with `/` in front, like `/cards`, `/offers`, `/trade accept myaccount 4`, `/2fa myaccount`.
- `/remove <account>` and `/exit` need `confirm` on the end - `/remove farm1 confirm`, `/exit confirm` - because
  nocat.farm can't be started again from Telegram.

**Pop-ups on Windows.** *Show pop-ups* turns them on or off. Four kinds, each with its own switch (Show advanced):
*Pop up when you earn* (cards, credited comments), *Pop up for comments* (on your profiles), *Pop up for problems*
(Steam Guard needed, failed sign-ins, comment bans, bans) and *Pop up for trade offers*.

**Discord profile card** (*Global settings → Discord profile*, **off**). While nocat.farm is open, your Discord profile
shows *Playing nocat.farm*, with what it's doing and today's cards. It needs the Discord app open on the same PC,
and Discord's Activity Privacy letting it share what you play. Nothing goes over the internet for this. Options
(Show advanced):

- *Accounts it shows* - empty means every account not in human mode; `all`, or names separated by commas
- *Featured account* - whose Steam avatar sits on the logo
- *Show account names*, *Show accounts online*, *Show an account's avatar*, *Show how long it's been running* (all on)
- *Button 1* and *Button 2* - `github` (a Get nocat.farm button), an account name (a link to its Steam profile),
  `Label | https://link`, or empty. Discord doesn't show your own buttons to you; other people see them.

## Daily summary

*For: a once-a-day look at what every account did.*

*Daily summary in the log* (on) writes a summary at 09:30 (*Summary time · hour* and *· minute*): per account, hours
banked, cards and rep4rep comments in the last 24 hours, plus a running total and a line for all accounts together.
If the PC was off at that time, it's written on the next start. With *Send the daily summary* on, it goes to
Discord/Telegram too. `stats` shows the same 24-hour figures any time, then cards and comments by hour.

## rep4rep

*For: people who use rep4rep.com. Most people won't want it.*

rep4rep is an outside site where users trade Steam profile comments for points. It's **off by default** - its tab,
points and settings stay hidden until you turn on *Use rep4rep at all* (*Global settings → rep4rep account*).

1. Turn it on, open the **rep4rep** tab and paste your API token (from rep4rep.com → Settings). It's checked before
   it's saved.
2. Turn it on per account: *Post rep4rep comments*, `set myaccount Rep4Rep true`, or the tab's **Turn it on for
   all** button.

Steam allows about **10 comments per 24 hours** on people who aren't your friends. *Most per 24 hours* (10) enforces
that, and the count survives restarts. Comments are spaced 10-25 minutes apart, only between 10:00 and 23:00. If
Steam refuses an account below the cap, it learns that account's real limit (*Learn the real limit*, on).

```
rep4rep                     # summary
rep4rep points              # points to spend, and points still being checked
rep4rep tasks myaccount     # comments waiting for one account
rep4rep now myaccount       # post now (never past the daily cap)
rep4rep pause myaccount     # hold it; rep4rep resume to carry on
rep4rep rest myaccount      # a full day off, then a clean start
```

*Hold commenting for (hours)* (Show advanced) pauses every account for that many hours, then carries on by itself.
Points show as pending first while rep4rep checks the comment landed.

---

## Commands

*For: doing anything by typing.*

`help` lists every built-in command, and `help <command>` or `help <setting>` explains one. Plugin commands are
listed by `plugins`. The dashboard's Console tab runs the same commands with the same output. Commands that show
`<account|all>` also take `all`. Aliases are shown after the name.

**[Every command, with what it does - one list](COMMANDS.md).** It's made from the app's own command list, so it
always matches the version you have. It starts with every command at a glance, grouped: accounts, playing,
trading cards, trades, Steam Guard, achievements, free stuff and keys, profile and info, rep4rep, settings and
the app itself.

### Commands by Steam chat

You can message an account from Steam with commands. Put your SteamID64 in that account's *Accept commands from*
(`CommandMasters`), then send a message starting with `/` or `!` - like `/help` or `!status`. A bare command such as
`/pause` or `/status` acts on the account you messaged. Messages without `/` or `!` get the normal auto-reply.
Commands from anyone not on the list are ignored. Replies are cut at 1,900 characters. `exit` and `remove` can't be
run this way - they have to be done at the PC (or from Telegram, with `confirm`).

## Settings

*For: fine-tuning.*

**The Settings page.** Pick **Global settings** (things for the whole app) or an account (things for that account)
on the left. Everyday switches show first; tick **Show advanced** for the rest. Search finds settings by name or
explanation, **Only changed** shows what you've changed, and each changed field has a link back to its default.
Hover over a setting for a plain explanation. Settings marked ⟳ need a restart.

**From the console.**

```
config myaccount            # its everyday settings and their values
config myaccount all        # every setting
set myaccount FarmCards false
set WebPort 8080            # no account = a global setting
help FarmCardsWhen          # what a setting does, its default and range
```

On/off settings take `true`/`false` (or `on`/`off`). Game lists take appIDs or store links separated by commas, or
`none`.

**The files.** Settings live in `config/nocatFarm.json` (global) and `config/<account>.json`, as plain JSON with the
same names. Edit them by hand and type `reload` if you like. Back up the `config` folder and you've backed up
everything.

### Global settings

- **Dashboard** - *Web dashboard*, *Language*; advanced: *Listen on*, *Port*, *Dashboard password*, *Stay signed in
  for*, opening the browser, refresh speed.
- **Running in the background** (Windows) - *Tray icon*, *Start with Windows*; advanced: *Start hidden*, *Minimise
  to the tray*, *Keep mini mode on top*, *Keep this PC awake*, *Close when everything's done*.
- **All accounts** - *Groups every account joins*; advanced: *Never touch these (all accounts)*.
- **Notifications** - Discord webhook and Telegram bot, with chips for what gets sent and a test button. See
  [Discord and Telegram](#discord-and-telegram).
- **Discord profile** - the *Playing nocat.farm* card on your Discord profile: its switch, what it shows and a
  preview; advanced: which accounts, the featured account, the two buttons.
- **Pop-ups** (Windows) - *Show pop-ups*; advanced: which kinds (earnings, comments, problems, trade offers).
- **Inventory prices** - *Inventory prices in* (the currency); advanced: price lookup speed and how long a price is
  trusted.
- **rep4rep account** - *Use rep4rep at all*, *API token*; advanced: *Hold commenting for (hours)*, *Register
  accounts automatically*.
- **Updates & plugins** (all advanced) - *Notify if an update is available*, *Remind me every hour*, *Load plugins*.
- **Steam connection** (all advanced) - gap between logins, reconnect, timeout, *Farm at most* (accounts farming at
  once), rate-limit cooldown, web request gap, *Connect using*, proxy.
- **Logging** (all advanced) - *Say what it's doing every* (5 minutes while playing) / *And while it's resting,
  every* (30 minutes) - 0 turns them off - *Write a log file*, *Show debug detail on screen* (the log file always
  has it), *Keep logs for* (14 days), the daily summary and its time, and the colours of `telegram` and `discord` in
  the log.

### Account settings

- **Account** - *Enabled*, *Steam account name*, *Password*, *Appear as*, *I sign into this one myself*; advanced:
  QR sign-in, *Sign in as*, *Start paused*, notes, Family View PIN, device name, authenticator secrets, its own
  proxy, clearing Steam's notifications.
- **Human mode** - *Human mode*, *Games and how often*, hours on weekdays and weekends, when it gets on and goes to
  bed, *Bank hours overnight*; advanced: hour targets, day-off chance, sittings, breaks, meals, going Away or offline
  on breaks, overnight games, how long it waits after waking or signing in before it reacts, finishing up when
  stopped.
- **What it plays** (not on human-mode accounts) - *Games to idle*, *Show a custom game name*, *Show as*; advanced:
  *Keep the name while farming*, *Play as if on*.
- **Trading cards** - *Farm trading cards*, *When to farm cards* (human mode); advanced: order, priority list,
  blacklist, refund protection, sittings, clock window, give-up time, and more.
- **Badges, boosters & selling** - *Craft badges from card sets*, *Sell duplicate cards*; advanced: booster packs,
  opening packs, how many cards to list at a time.
- **Achievements** - *Earn achievements over time*; advanced: pace, completion limit, only/never lists, grind
  spacing, the hunter, family-shared games.
- **Free stuff** - free games, free DLC, event items, the discovery queue.
- **Inventory & bans** - *Watch for bans*; advanced: *Work out what its inventory is worth* and games to leave out.
- **Trades** - donations, gifts, fair card swaps, your own accounts, *Trade by itself with*; advanced: *Decline
  everything else*, waits, sending items, *What to send*, trade link token.
- **rep4rep commenting** (only when rep4rep is on) - *Post rep4rep comments*; advanced: cap, gaps, hours.
- **Friends & messages** - *Accept friend requests*, *Reply to messages* and its text; advanced: delays, spam
  filter, group invites and joining, *Accept commands from*.
- **Staying out of the way** (advanced) - *Stand down when you play*, *Wait before resuming*.
- **Logging** (advanced) - *Report in every* / *And while resting, every* (0 = follow the global setting, -1 = keep
  this account quiet), and its colour in the log.

Human mode hides *Games to idle*, the custom game name, *Keep the name while farming*, *Farm in sittings*, *Hours a
day to farm*, *Log out when finished* and *Farm while appearing offline* on that account, and shows human-only
settings instead. rep4rep settings only show when rep4rep is on.

## Command line options

*For: shortcuts, scripts and running several copies.*

```
--path <dir>    where config/ and logs/ live (default: next to the exe)
--no-web        don't start the dashboard
--no-tray       no notification-area icon
--no-gui        no window - the console board instead (also --console)
--minimized     start hidden, straight to the tray (also --background)
--help          list these (also -h)
```

One copy runs per config folder - a second one says so and exits. To run separate sets of accounts, use separate
`--path` folders.

Environment variables (any install, not just Docker) - when set, they win at every start and are written into
`config/nocatFarm.json`:

| Variable | What it sets |
|---|---|
| `NOCATFARM_WEB_HOST` | *Listen on* (`WebHost`). |
| `NOCATFARM_WEB_PORT` | *Port* (`WebPort`, 7242 by default). |
| `NOCATFARM_WEB_PASSWORD` | *Dashboard password*. |
| `NOCATFARM_WEB_PASSWORD_FILE` | A file to read the password from (how Docker secrets arrive). Wins over the one above. |
| `NOCATFARM_NETLOG=1` | For troubleshooting: writes every Steam message to `netlog-<account>.txt`. |

## Linux and Docker

*For: running it on a home server, NAS, Raspberry Pi 4/5 (64-bit) or VPS.*

There's no window or tray icon on Linux. You get the console and the web dashboard; everything else is the same.

### From the zip

1. Download `nocat.farm-v<version>_linux-x64.zip` (Intel/AMD) or `nocat.farm-v<version>_linux-arm64.zip` (Raspberry
   Pi, ARM servers) from [Releases](https://github.com/VisaHolder/nocatfarm/releases/latest).
2. Unzip it into its own folder and start it:

   ```
   unzip nocat.farm-v<version>_linux-x64.zip -d nocatfarm
   cd nocatfarm
   ./nocatFarm
   ```

   If it says *permission denied*, run `chmod +x nocatFarm` once.

No .NET to install. It prints the dashboard address. To use the dashboard from another device, set a password and
open it up (see [Using the dashboard from another device](#using-the-dashboard-from-another-device)).

**As a service** (systemd, with the folder at `/opt/nocatfarm`):

```ini
[Unit]
Description=nocat.farm
After=network-online.target
Wants=network-online.target

[Service]
User=youruser
WorkingDirectory=/opt/nocatfarm
ExecStart=/opt/nocatfarm/nocatFarm
Restart=on-failure
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
```

`systemctl stop nocatfarm` signs every account out cleanly first. The log is in the journal
(`journalctl -u nocatfarm -f`). Type commands in the dashboard's Console tab, where Steam Guard prompts appear too.

**Updating.** It tells you when a new version is out, but doesn't update itself on Linux. Stop it, unzip the new zip
over the folder (`unzip -o`), and start it again. `config/` and `logs/` aren't in the zip, so they're kept.

**Saved logins** are encrypted with a key in `config/state/secret.key`, readable only by your user. Back up the whole
`config` folder together. A config folder from Windows works, but each account signs in once more, because Windows
ties saved logins to your Windows user.

**Discord profile card** works with the Discord desktop app on the same desktop. On a server or in Docker there's no
Discord app, so it stays off.

### Docker

```
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
cp docker-compose.example.yml docker-compose.yml
mkdir -p config logs
echo 'NOCATFARM_WEB_PASSWORD=something-long-and-your-own' > .env
docker compose up -d --build
```

Open `http://localhost:7242` and sign in with that password. Add accounts there - the Steam Guard code or QR scan
is asked for on the page.

The example compose file is commented line by line. The main points:

- **Your data** is in `./config` and `./logs` (inside the container, `/data/config` and `/data/logs`). Rebuilding
  keeps them.
- **A password is required.** Compose won't start without `NOCATFARM_WEB_PASSWORD`.
- **Port 7242 on this machine only.** Change `"127.0.0.1:7242:7242"` to `"7242:7242"` to reach it from other
  devices.
- **`TZ`** - your time zone. Human mode's day and the daily summary follow it.
- **`hostname`** - the device name Steam shows. Without it, it changes on every re-create.
- **`user: "1000:1000"`** - must match the owner of `./config` and `./logs` (`id -u`, `id -g`). If the log says it
  can't write to `/data/config`, fix this - or remove the line and run `sudo chown -R 1654:1654 config logs`.
- **`stop_grace_period: 1m`** - `docker compose down` signs every account out cleanly first.

Commands: the dashboard's Console tab, or `docker attach nocatfarm` (Ctrl+P then Ctrl+Q to detach). Log:
`docker compose logs -f`. Plugins: uncomment the `./plugins:/data/plugins` line.

**Updating:** `git pull`, then `docker compose up -d --build`. It doesn't update itself in Docker.

**Other CPUs:** the image builds for amd64 and arm64 from the same Dockerfile:
`docker buildx build --platform linux/amd64,linux/arm64 -t nocatfarm .`

## Plugins

*For: people who write C#.*

A plugin is a DLL that adds commands and features. Plugins are off by default: put the DLL in the `plugins` folder
next to `nocatFarm.exe`, turn on **Load plugins** (Settings → Global settings → Updates & plugins, under Show advanced, or
`set PluginsEnabled true`), and restart. `plugins` and the dashboard's Plugins page list what's loaded and the
commands they added; each plugin has its own on/off switch there.

A plugin runs inside nocat.farm, next to your Steam logins. **Only run plugins you wrote or fully trust.**

**→ [How to write one](../PLUGINS.md)** - a working plugin in five minutes.

ArchiSteamFarm plugins don't work in nocat.farm; [PLUGINS.md](../PLUGINS.md#not-supported-asf-plugins) explains why.

## Privacy and safety

*For: knowing what it does with your data.*

- **Everything stays on your PC.** Accounts, login tokens and logs are never uploaded.
- **Passwords are optional.** After the first sign-in a Steam login token does the work. Login tokens, saved
  passwords, authenticator secrets and proxy passwords are encrypted on disk - on Windows tied to your Windows user,
  on Linux and Docker with a key only your user can read.
- **What it contacts:**
  - **Steam** - always.
  - **GitHub** - every few hours to check for a new version, and to download one when you say so. Turn the check off
    with *Notify if an update is available*.
  - **Discord, Telegram and rep4rep** - only if you set them up. The Discord profile card talks only to the Discord
    app on your PC.
- **The dashboard is yours.** It only listens on this PC by default. With no password it refuses anything that isn't
  from this PC, even if you open it to the network. Secrets are never sent to the browser. An empty secret box
  means "leave it as it is" - use its **Clear** button to erase one.
- **It never fights you** for your account - see [Staying out of your way](#staying-out-of-your-way).
- **Steam's rules.** Automating your accounts is against Steam's Subscriber Agreement and can get an account limited
  or banned. nocat.farm is built to be gentle, but run it only on accounts you're willing to risk.

## FAQ

**Will I get banned?** Card idlers have been used for years and bans for idling alone are rare - but it is against
Steam's rules, so there's always some risk. Human mode makes an account look far more normal.

**Can I still play on my account?** Yes. Start a game and that account stands down, then carries on a few minutes
after you stop.

**How many accounts can it run?** As many as you like. They sign in one after another so Steam doesn't mind.

**Do I have to type my password every time?** No - once, or never with the QR code.

**Can all my accounts join my Steam group?** Yes - put its link in *Groups every account joins*. See
[Steam groups](#steam-groups).

**I don't want the nocat.farm group.** Clear *Groups every account joins* (Global settings → All accounts).

**Where are my settings?** In the `config` folder next to `nocatFarm.exe`. Back it up and you've backed up
everything.

**Why don't my custom name and games-to-idle settings show?** The account is in human mode, which hides them on
purpose. Pick its games under *Human mode → Games and how often*.

**Why is a human-mode account offline?** It's asleep, on a break, or taking a day off. `human myaccount` shows its
day, and `wake myaccount` starts it now.

**Replies to commands are in English.** The dashboard, status lines and log are translated into 11 languages;
replies to typed commands stay in English.

## Building from source

*For: developers.*

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). No npm, no bundler - the dashboard is plain
static files.

```
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
dotnet publish src/NocatFarm -c Release -o run
run\nocatFarm.exe
```

On Linux: `dotnet publish src/NocatFarm -c Release -r linux-x64 -o run` (or `linux-arm64`), then `run/nocatFarm`.
`tools/package-release.ps1` builds all three release zips.

`run/` isn't in the repository - it's where your accounts, tokens and logs end up.

Built on [SteamKit2](https://github.com/SteamRE/SteamKit). Everything else is written from scratch for nocat.farm.
