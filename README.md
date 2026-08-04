# nocat.farm

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Windows](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)
![Built on SteamKit2](https://img.shields.io/badge/built%20on-SteamKit2-1b2838?logo=steam&logoColor=white)
![Dependencies: 1](https://img.shields.io/badge/dependencies-1-brightgreen)
![Languages: 11](https://img.shields.io/badge/languages-11-8b5cf6)

A Steam idler, trading-card farmer and — if you want it — rep4rep commenter. One small Windows app with a web
dashboard beside it. It sits in the tray, runs every account in the background all day, and is built to look
like a person doing it rather than a bot.

<p align="center">
  <img src="assets/app.png" alt="The nocat.farm app — a small tray window running the whole fleet" width="700">
</p>

**Everything stays on your machine.** Your accounts, login tokens and logs never leave it. The only things that
touch a third party are opt-in and off by default — see [Privacy](#privacy-and-safety).

---

### Contents

[Building it](#building-it) · [What it does](#what-it-does) · [Three ways to drive it](#three-ways-to-drive-it)
· [Getting started](#getting-started) · [Human mode](#human-mode) · [rep4rep](#rep4rep) ·
[Commands](#commands) · [Achievements](#achievements) · [The hunter](#the-hunter) ·
[Inventory value](#what-the-inventories-are-worth) · [Trades, keys & items](#trades-keys-and-items) ·
[Settings](#settings) · [Privacy & safety](#privacy-and-safety)

### Building it

**Just want to run it?** Grab the latest `nocat.farm-v*.zip` from
[Releases](https://github.com/VisaHolder/nocatfarm/releases), unzip it anywhere, and run `nocatFarm.exe`. The
release build is self-contained — no .NET install, nothing else to set up.

To build from source instead, you need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else —
there is no npm step, no bundler, and the dashboard is plain static files.

```
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
dotnet publish src/NocatFarm -c Release -o run
run\nocatFarm.exe
```

`run/` is deliberately not in the repository — it is where your accounts, login tokens and logs end up, and
none of that belongs in version control. The publish step creates it.

First run creates `config/`, prints the dashboard URL, and tells you the one command you need.

Everything is local. Accounts never leave the machine. Only two features ever talk to a third party, both
**off by default** and opt-in: **rep4rep** (a comment-exchange site — the whole feature is hidden until you
turn it on), and **Claim free games** (reads a public list of current Steam giveaways). Leave them off and
nothing but Steam is ever contacted.

---

## What it does

| | |
|---|---|
| **Idling** | Plays any set of appIDs for playtime, and re-asserts them, so a network hiccup doesn't silently stop it. |
| **Custom game name** | Shows a non-Steam name (e.g. `nocat.lol`) on your profile and friends list **while the real games keep banking playtime**. Both at once. |
| **Card farming** | Reads your own badge pages, bumps playtime on under-threshold games 32 at a time, then farms them solo. Drops are detected from Steam's item-announcement **push**, not a timer, so a finished game is noticed in seconds. |
| **rep4rep** | Posts the comments rep4rep assigns, on a human schedule, from every opted-in account — all into one points pool. |
| **Comment alerts** | Steam pushes the moment somebody comments on one of your profiles; it hits the log and a tray balloon. |
| **Stays out of your way** | Launch a game yourself and every account stands down, then quietly picks back up after a delay you choose. |
| **Achievement hunter** | Optional. Picks single-player games out of the library itself — never DLC, demos or bundle filler nobody plays — and works through them one at a time as occasional sessions, earning at the account's own legit pace. On a human account it stays weighted-first and comes out of the day's play budget rather than being stacked on top of it. |
| **Inventory value** | What every account's items are worth at the market's median, per game, in your own currency, with how it has moved in the last 24 hours. Reads the account's OWN inventory, so a private profile makes no difference. |
| **Refund protection** | A game bought in the last fortnight and under two hours played is left completely alone — by the idler, the schedule, grinds and the hunter alike — until it can no longer be refunded. |
| **Steam Families** | Games shared into the account can be hunted too, and are handed straight back the moment the person who owns them starts playing. |
| **Daily report** | Once a day (default 09:30) it writes a one-look summary to the log: hours banked in the last 24h, cards, rep4rep comments and a running total, per account. Type `report` for it on demand. |
| **Eleven languages** | The dashboard is fully translated into Spanish, Portuguese (BR), Russian, German, French, Simplified Chinese, Turkish, Polish, Japanese and Korean — every label, every explanation, all 162 settings. The walkthrough asks which you want before it says anything else. |

<p align="center">
  <img src="assets/accounts.png" alt="The per-account view — state, custom name, and what your friends actually see" width="880">
</p>

## Three ways to drive it

**Console** — the primary interface. Type `help`. Up-arrow history, tab completion in the browser console,
and `help <setting>` explains any setting in one sentence.

<p align="center">
  <img src="assets/console.png" alt="The console — every action is also a command" width="880">
</p>

**Tray** — right-click for Open dashboard · Hide console · Start/Stop all · Exit. Minimising the console hides
it. `--minimized` boots straight to the tray with no window at all, and *Start with Windows* wires that up for
you.

**Dashboard** — `http://127.0.0.1:7242/`. Overview · Accounts · rep4rep · Log · Console · Settings.
Turn it off with `set WebEnabled false` and nothing is lost.

<p align="center">
  <img src="assets/dashboard.png" alt="The web dashboard — overview" width="880">
</p>

---

## Getting started

Type `tutorial`. It walks the six steps in order and ticks off the ones this machine has already done, so the
next thing to do is always the first unticked line. `tutorial cards`, `tutorial human`, `tutorial rep4rep`,
`tutorial trades`, `tutorial achievements` and `tutorial tray` go deeper on one thing each.

The short version:

```
add myaccount steamlogin      # asks for the password + Steam Guard code, once
play myaccount 730, 440       # idle CS2 and TF2
name myaccount nocat.lol      # show this instead of the real game
```

Card farming is already on, and cards come first: it works through everything with cards left, then falls back
to idling that list. `cards myaccount` shows what's outstanding.

The password is asked for **once per account**. After that a Steam refresh token in `config/tokens/` does the
logging in — restarts are silent, no Guard code, no password. You never have to store a password in a file.
Drop an account's `maFile` into `config/authenticators/` and it answers its own Steam Guard prompts forever.

**Coming from ArchiSteamFarm?** `import asf` brings every bot across *with its login token* — no passwords, no
Guard codes — plus its games, custom name, farming order and rep4rep settings, and its authenticator if ASF had
one. ASF's `FarmingPreferences`, `BotBehaviour`, `TradingPreferences` and `SteamUserPermissions` bitfields are
unpacked into the individual settings here.

## Human mode

The part that separates this from an idler. Turn it on per account and it plays like a person instead of a bot:

* weekdays and weekends have different shapes, and roughly one day in twenty it doesn't play at all
* one game at a time, in sittings of believable length, with a main game that takes most of the week
* other games arrive in **bursts** rather than the same slice every single day — an even daily drip is the
  loudest pattern a farming account leaves
* short breaks, proper meal breaks around real meal times, and some of those spent appearing offline
* a settling-in gap after signing in, because nobody launches a game the second Steam opens
* offline overnight, where it can still bank hours invisibly
* **card farming fits the act** — it settles in first, farms, then plays the game on a little longer before
  stepping away for a break and picking the schedule back up, instead of snapping between tasks
* **grind fits it too** — `grind` on a legit account eases in (finishes the current game first) and earns
  achievements at that account's normal pace; on a boost account it just starts instantly
* stopping it doesn't blink the account out mid-game — it finishes up for a few seconds, then logs off

```
set myaccount LegitMode true
set myaccount GameWeights "730:70, 440:20, 550:10"    # first game is the main one
human myaccount week                                   # the week it rolled, as a sample
```

While it's on, the settings that would give an account away are hidden **and cleared from the config** — and
put back exactly as they were if you turn it off.

## rep4rep

rep4rep is a third-party site where users trade profile comments. It's **entirely optional and off by
default** — the whole feature (its tab, its points, its per-account options) stays hidden until you switch it
on under **Settings → rep4rep account → "Use rep4rep at all."** Most people won't want it; leave it off and
nothing rep4rep-related ever runs or appears.

If you do want it: turn it on, open the **rep4rep** tab and paste your API token (from rep4rep.com → Settings).
It's validated before it's saved, so a typo can't silently stop every account commenting. Then switch it on
per account.

Steam's ceiling for comments on people who aren't your friends is about **10 per rolling 24 hours, per
account**. That ceiling is enforced here and the count is **persisted**, so a restart can't reset it — which is
the mistake that actually gets accounts comment-banned. If the state file can't be read, nobody comments: fail
safe, not fail open.

The pacing, all per account:

* a commenting window (default 10:00–23:00), staggered per account per day
* 10–25 minutes between one account's comments, jittered
* a refused post gets **one** retry, then that *profile* is skipped for 26h and a different one is tried
* only when **three different profiles** refuse in a row is the *account* treated as blocked — 24h off, while
  the other accounts carry on
* an unknown outcome is counted and never retried (Steam may have posted it; a retry would double-comment)
* if Steam keeps rate-limiting one account near its cap, it stops chasing that last slot and rests until its
  24h window has fully cleared, then starts fresh — rather than retrying into the same wall all day
* `rep4rep rest <account|all>` forces exactly that: a full day off, then back at a clean baseline

Points appear as **pending** first — that's rep4rep verifying the comment really landed, usually within a few
hours. Nothing is lost.

Removing a profile, editing your comment list, task history, buying points and referrals have no API. The
dashboard links out to rep4rep.com for those rather than pretending.

---

## Commands

`help` lists everything; `help <command>` or `help <setting>` explains one. The dashboard's console runs the
same commands and prints the same output. Almost anything that takes an account also takes `all`.

You can also drive an account by **Steam chat**: message it from a master (a SteamID64 in that account's
`CommandMasters`), prefixed with `/` or `!` — e.g. `/help`, `!status new`. A plain message with no prefix gets
the auto-reply instead, so ordinary chat is never mistaken for a command.

<details>
<summary><b>Every command</b> — click to expand</summary>

<br>

**Accounts**

| Command | What it does |
|---|---|
| `status [account]` &nbsp;·&nbsp; `s` `bots` | What everything is doing right now. |
| `start <account\|all>` | Log an account in. |
| `stop <account\|all>` | Log an account out (stays configured). A human-mode account finishes up for a few seconds first. |
| `restart <account\|all>` | Stop then start again. |
| `pause <account\|all>` | Stay logged in but stop playing, farming and commenting. |
| `resume <account\|all>` | Undo a pause. |
| `add <name> <steamLogin>` | Add an account. Asks for the password once, then remembers a login token. |
| `remove <account>` &nbsp;·&nbsp; `delete` | Delete an account and its stored login token. |
| `enable <account>` | Let this account log in again. |
| `disable <account>` | Keep it configured but never log it in. |
| `redeem [account] <key…\|file.txt>` &nbsp;·&nbsp; `key` | Activate product keys, or point it at a text file full of them. More than five queues itself. |
| `keys [list\|clear]` | Product keys still waiting to be activated. |
| `2fa <account>` &nbsp;·&nbsp; `guard` | Show this account's Steam Guard code, if its authenticator is set up here. |

**Playing**

| Command | What it does |
|---|---|
| `play <account> <appIDs\|none>` | Set the games this account idles for playtime (multiple allowed). |
| `grind <account\|all> <appID> <hours>` &nbsp;·&nbsp; `grind <account> off` | Play one game hard for N hours, then back to normal. Earns achievements while it runs. On a legit account it eases in and out; on a boost account it's instant. |
| `human [account] [week]` | What human mode is doing today; add `week` for the next seven days. |
| `wake <account>` &nbsp;·&nbsp; `wakeup` `skipsleep` | Wake a sleeping human-mode account and start its day now. Bed time is unchanged. |
| `name <account> [text]` | Custom non-Steam game name shown instead of the real game. No text clears it. |
| `persona <account> <state>` | online \| offline \| busy \| away \| snooze \| invisible. |
| `cheevo <account> <appID> [list\|unlock\|lock] [name\|all]` &nbsp;·&nbsp; `ach` | Achievements: see them, unlock them, or put them back. |
| `hunt [account]` &nbsp;·&nbsp; `boost` | What the achievement hunter would play next, in order - and what it ruled out, with the reason for each. |

**Trading cards**

| Command | What it does |
|---|---|
| `cards [account]` | What is still left to farm. |
| `farm <account> on\|off` | Turn trading-card farming on or off. |
| `match [do]` | Swap duplicate trading cards between your own accounts so sets finish. `match do` sends the offers. |
| `value [account\|all] [refresh]` &nbsp;·&nbsp; `inv` `inventory` | What each inventory is worth at the market's median, by game, with how it has moved in the last 24 hours. `refresh` reads the inventories again. |
| `send <account\|all>` &nbsp;·&nbsp; `loot` | Send an account's tradable items to the account listed under Trades. |

**rep4rep** &nbsp;(alias `r4r`; run bare for a summary)

| Command | What it does |
|---|---|
| `rep4rep status` | Per-account count, cap, last post and current state. |
| `rep4rep points` | Points you can spend, and points still being verified. |
| `rep4rep profiles` | The Steam profiles registered with rep4rep. |
| `rep4rep tasks <account>` | The comment tasks waiting for one account. |
| `rep4rep on\|off <account\|all>` | Turn commenting on or off. |
| `rep4rep now <account\|all>` | Post now, skipping the wait (never the daily cap). |
| `rep4rep pause\|resume <account\|all>` | Hold commenting, or let it go again. |
| `rep4rep clear <account\|all>` | Release a 24h block early. |
| `rep4rep rest <account\|all>` | Pause a full 24h and come back at a clean baseline. |

**Settings & data**

| Command | What it does |
|---|---|
| `config [account]` | Show every setting and its current value. |
| `set [account] <key> <value>` | Change a setting; without an account name it changes a global one. |
| `import asf [path] [force]` | Bring accounts across from ArchiSteamFarm, login tokens and all. |
| `reload` | Re-read every config file from disk. |

**Everything else**

| Command | What it does |
|---|---|
| `log [count]` &nbsp;·&nbsp; `logs` | The last few log lines. |
| `stats [hours]` | Cards dropped and comments posted, by hour. |
| `report` | Write the daily summary to the log now. |
| `answer <text>` | Answer whatever nocat.farm is waiting on — a Steam Guard code, or a password. |
| `tutorial [topic]` &nbsp;·&nbsp; `guide` `setup` | Getting started, in order, ticking off what's done. |
| `help [command\|setting]` &nbsp;·&nbsp; `?` `h` | This list, or what one command or setting does. |
| `theme [dark\|light]` | Switch the dashboard theme. |
| `version` &nbsp;·&nbsp; `about` | Which version this is. |
| `exit` &nbsp;·&nbsp; `quit` `q` | Shut nocat.farm down (local only — never over Steam chat). |

</details>

## Achievements

```
cheevo myaccount 730                  # what it has, easiest first, with how rare each one is
cheevo myaccount 730 unlock all       # all of them, now
cheevo myaccount 730 unlock ACH_NAME  # just one
cheevo myaccount 730 lock ACH_NAME    # put one back
```

Unlocking a whole list at once is permanent, stamped with one shared timestamp, and visible on the profile
forever — so for an account meant to look real there's a drip instead: `set myaccount UnlockAchievements true`
earns a few a day, **easiest first** (the ones most owners have are the ones you get by simply playing), and
only in a game the account actually has open.

`grind` earns them too, on whatever game you point it at: briskly on a boost account, and at the account's own
legit pace on a human-mode one. The catch is that **some games' achievements are set by Steam's servers, not
the client — Counter-Strike 2 is the classic case — and nothing can unlock those.** Grind says so plainly for
such a game instead of looking stuck.

### The hunter

`UnlockAchievements` decides *how* achievements come out of games the account was going to play anyway. It never
starts a game. The **achievement hunter** is the other half: it decides *which games get played at all*, so there
is something to earn in.

```
set myaccount AchievementBoost 2      # 0 off (default) · 1 games you pick · 2 every single-player game it owns
```

Mode 2 works the library out for itself. A game has to be a **game** (never DLC, a demo, a soundtrack or a
tool), be single-player, have achievements, and have enough Steam reviews that a person might plausibly own it —
which is what keeps bundle filler out. Blacklists, the never-list, the achievement allow-list, a human account's
main game and anything inside its refund window are all stripped out on top.

One game at a time, about two hours each (`BoostSessionHours`), then it rotates to the next. On a **human**
account it stays weighted-first: a session, then a long stretch of the normal schedule (`BoostRestMinutesHuman`),
capped at `MaxBoostGamesInARow` before a longer one — never while asleep, never over card farming, never over a
grind you started, and it stops for the day once the schedule's daily target is met. `hunt` prints exactly what
it would play next and why everything else was ruled out.

Without it, the only way to earn across a library is to put every single-player game into `GameWeights` — which
destroys the thing human mode exists for, because a weighted schedule is supposed to look like one game somebody
mains and a couple they dip into, not two hundred at equal weight.

## What the inventories are worth

```
value                    # every account, by game, and how it moved in the last 24h
value myaccount refresh  # read that account's inventory again
```

Priced at the community market's **median**, in whatever currency the global `MarketCurrency` is set to — match
it to your Steam store or the totals won't agree with what you see on the market. Everything in the inventory is
counted at what it is worth, whether or not this particular copy could be sold today: a trade hold doesn't make
a knife worthless. Prices are cached for a day and shared between accounts, and looked up slowly, because
everything the app does on steamcommunity.com shares one rate limit — a big inventory takes an hour to settle
the first time and is instant afterwards.

Games the account is **banned** in go in `InventoryIgnoreGames`. Steam doesn't publish which game a ban is in and
nothing in the inventory reliably shows it, so it is a list you fill in rather than something guessed at.

## Trades, keys and items

```
set myaccount AcceptDonations true      # offers that ask for NOTHING - can never cost the account anything
set myaccount TradeMasters 7656119...   # accounts you own
set myaccount AcceptFromMasters true    # let those take items
send myaccount                          # sweep its cards to the first master
redeem AAAAA-BBBBB-CCCCC                # tries each account until one can use the key
2fa myaccount                           # its current Steam Guard code
```

A donation is an offer where you give up nothing at all. An offer asking for even one of your items is not a
donation and is never accepted on that rule — only accounts on your own masters list can take anything. If a
side of the trade page can't be read, the offer is refused rather than guessed at.

## Settings

**44 global, 120 per account.** Every one has a plain-English explanation attached to it, which the dashboard
shows on hover and the console prints for `help <setting>` — one sentence, written once, in
`Config/Settings.cs`. Advanced settings are collapsed by default and never move. Both the name and the
explanation are translated into all ten languages.

Global: the dashboard (host/port/password/auto-open, **language and the currency prices are shown in**),
background behaviour (tray, minimise-to-tray, start with Windows, keep-awake, three notification categories),
the rep4rep account, the Steam connection (login stagger, reconnect, timeout, farming concurrency, rate-limit
cooldown, web request spacing, protocol, proxy), and logging with retention.

Per account: identity and appearance (persona, device type, notes, start-paused, Family View PIN, device
name), what it plays, trading cards (order, priority list, blacklist, refund protection, skip-unplayed,
give-up time, log out when done, plus *when* to farm — only while asleep, or inside a set clock window, and
how long to wind down on the last game after finishing), human mode (the whole daily shape, and how long it
finishes up for on a manual stop), rep4rep pacing, friends & messages (accept requests, auto-reply, command
masters, and joining the nocat.farm Steam group — on by default, `set <account> JoinGroup false` to opt out),
and *Staying out of the way*.

`config/nocatFarm.json` and `config/<account>.json` are plain JSON with exactly these names. Edit by hand and
run `reload` if you prefer.

## Command line

```
--path <dir>    where config/ and logs/ live (default: next to the exe)
--no-web        don't start the dashboard
--no-tray       no notification-area icon
--minimized     start with the console hidden, straight to the tray
```

---

## Privacy and safety

* **Local only.** Your accounts, passwords, login tokens and logs live on your machine and are never uploaded
  anywhere. `run/` (config, tokens, logs) is git-ignored and is not in this repository.
* **No stored passwords.** A password is typed **once per account**, then a Steam refresh token does the
  logging in — restarts need no password and no Guard code. Drop an account's `maFile` into
  `config/authenticators/` and it answers its own Steam Guard prompts.
* **Opt-in third parties.** rep4rep and *Claim free games* are the only features that contact anything other
  than Steam, and both are off by default (see [rep4rep](#rep4rep) and [Privacy](#privacy-and-safety) above).
* **It never fights you for your account.** Launch a game yourself and every account stands down, then picks
  back up on its own — and on an account you also sign into, it will not throw you off Friends & Chat.
* **The dashboard is yours.** With no password set it refuses every connection that isn't from this PC.
  Secrets are never sent to the browser, and an empty field means "leave unchanged", never "erase it".
* **Steam's rules.** This automates your own Steam accounts; that's against Steam's Subscriber Agreement and
  can get an account limited or banned. It's built to be gentle (human pacing, shared rate-limit cooldowns),
  but you run it at your own risk on accounts you're willing to lose.

## Notes on how it works

* **Web session.** There is no "log in to the website" step. Steam accepts a cookie built locally from the
  access token the Steam connection already handed us: `steamLoginSecure = <steamID64>||<accessToken>`, plus a
  client-chosen `sessionid` that must also be echoed in the body of every POST. An expired token shows up as a
  redirect to `/login` rather than an error, so that redirect is what triggers a re-mint and one retry.
* **Rate limits.** Logins from one machine are serialised with a gap, and everyone shares one cooldown when
  Steam pushes back — three accounts each waiting 25 minutes in series helps nobody. Web requests are spaced
  per host, and a 429 shuts that host for *every* account: the limit is per IP, so one account collecting one
  is everybody's problem, and each further request while it stands is what keeps it alive. The wait doubles
  from 5 minutes to 40 and resets on the next answer that works.
* **Notifications, not polling.** Card drops, profile comments and waiting trade offers all arrive as pushes
  over the Steam connection. Nothing opens the trade offers page on a timer to learn there is nothing there;
  the slow pass that remains exists only to catch what a push might have missed.
* **Occupation.** If you sit down and launch a game, Steam says so, and everything that plays a game stands
  down. It never fights you for your own session.
* **Security.** With no dashboard password set, the dashboard refuses every connection that isn't from this PC.
  Secrets are never sent to the browser and an empty field means "unchanged", never "erase it".

Built on [SteamKit2](https://github.com/SteamRE/SteamKit). The protocol details and the pacing model follow
[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm), which is where the hard-won parts (the
32-games-at-once playtime trick, the badge-page shapes, the login limiter) come from.
