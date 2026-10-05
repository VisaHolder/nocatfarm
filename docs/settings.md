[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Settings and commands

## Settings

On the dashboard's Settings page, pick **Global settings** (things for the whole app) or an account on the left.
Everyday switches show first; tick **Show advanced** for the rest. Search finds a setting by its name or its
explanation, **Only changed** shows only what you've changed, and each changed field has a link back to its default.
Tap or hover the ⓘ beside any setting for a plain explanation. Settings marked ⟳ need a restart.

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

| Group | Everyday | Behind Show advanced |
|---|---|---|
| Dashboard | *Web dashboard*, *Language* | *Listen on*, *Port*, *Dashboard password*, *Stay signed in for* (7 days), *Open from anywhere*, *Public address*, *Trust forwarded addresses from*, opening the browser, refresh speed |
| Running in the background (Windows) | *Tray icon*, *Start with Windows* | *Start hidden*, *Minimise to the tray*, *Keep mini mode on top*, *Mini window shows* (hours today), *Keep this PC awake*, *Close when everything's done* |
| All accounts | *Groups every account joins*, *Restart a stuck account* (on, see [Stuck accounts](phone-and-notifications.md#stuck-accounts)) | *Never touch these (all accounts)*, *Stuck after* (3 hours) |
| Notifications | Discord webhook, Telegram bot and Discord bot, and a test button | what gets sent (see [Discord and Telegram](phone-and-notifications.md#discord-and-telegram)) |
| Discord profile | the *Playing nocat.farm* card: its switch, what it shows, a preview | which accounts, the featured account, the two buttons |
| Pop-ups (Windows) | *Show pop-ups* | which kinds: earnings, comments, problems, trade offers |
| Inventory prices | *Inventory prices in* (the currency) | price lookup speed, how long a price is trusted |
| rep4rep account | *Use rep4rep at all*, *API token* | *Hold commenting for (hours)*, *Register accounts automatically* |
| Updates & plugins | *Updates*: install when I click, once everyone's asleep (the Update button waits until your accounts are asleep), install when I click (right away), or install by itself at night; *Count me as a user* (on: once an hour tells nocat.lol it's running, see [Privacy and safety](safety-and-faq.md#privacy-and-safety)) | the hours it may install in (3 to 6), *Wait after a release for* (2 hours), *Notify if an update is available*, *Look for updates every* (2 hours), *Remind me every hour*, *Load plugins* |
| Steam connection | | gap between logins, reconnect, timeout, *Farm at most* (accounts farming at once), rate-limit cooldown, web request gap, *Connect using*, proxy |
| Logging | | *Say what it's doing every* (5 minutes while playing) and *And while it's resting, every* (30 minutes), where 0 turns them off; *Write a log file*; *Show debug detail on screen* (the log file always has it); *Keep logs for* (14 days); the daily summary and its time; the *Weekly report* (off), its day and hour; the colours of `telegram` and `discord` in the log |

At the bottom of Global settings is **Backup & restore**. See [Backup and restore](dashboard.md#backup-and-restore).

On Linux and in Docker the Windows-only settings (tray, pop-ups, start with Windows and so on) aren't shown. They
stay in the config file untouched, in case you take it back to Windows.

### Account settings

| Group | Everyday | Behind Show advanced |
|---|---|---|
| Account | *Enabled*, *Steam account name*, *Password*, *Appear as*, *I sign into this one myself* | QR sign-in, *Sign in as*, *Start paused*, notes, Family View PIN, device name, authenticator secrets, its own proxy, clearing Steam's notifications |
| Human mode | *Human mode*, *Games and how often*, hours on weekdays and at the weekend, when it gets on and goes to bed, *Learn from how I play* (with *Keeps up with changes* and *Weekends separately* while it's on), *Reaction speed*, *Bank hours overnight* with its games (or its top games when none are chosen) | hour targets, day-off chance, sittings, breaks, meals, going Away or offline on breaks, how long it waits after waking or signing in, finishing up when stopped |
| What it plays | *Protect refunds*; on robot accounts *Games to idle*, *Idle my whole library*, *Rotate the idle list*, *Show a custom game name*, *Show as* | *Rotate every*, *Keep the name while farming*, *Play as if on* |
| Trading cards | *Farm trading cards*, *When to farm cards* (human mode; with *mixed*, its share of sittings) | order, priority list, blacklist, sittings and the clock window (robot accounts), give-up time, and more |
| Badges, boosters & selling | *Craft badges from card sets*, *Sell duplicate cards* | booster packs, opening packs, how many cards to list at a time |
| Achievements | *Earn achievements over time*, *How fast*, *Skip multiplayer achievements* | completion limit, only/never lists, grind spacing, the hunter (rotation, when to move on, rest days, daily cap, game length), family-shared games |
| Free stuff | *Claim free games* (off, games, or games and DLC), event items, the discovery queue | |
| Inventory & bans | *Watch for bans* | *Work out what its inventory is worth*, and games to leave out |
| Trades | donations, gifts, fair card swaps, your own accounts, *Trade by itself with*, *What to send* | *Decline everything else*, waits, sending items, trade link token |
| rep4rep commenting (only when rep4rep is on) | *Post rep4rep comments* | cap, gaps, hours |
| Friends & messages | *Accept friend requests*, *Reply to messages* and its text | delays, spam filter, group invites and joining, *Accept commands from* |
| Staying out of the way | | *Stand down when you play*, *Wait before resuming* |
| Logging | | *Report in every* and *And while resting, every* (0 follows the global setting, -1 keeps this account quiet), and its colour in the log |

Hours of the day (when it gets on and goes to bed, when it farms, installs updates, sends items, comments, writes the
summaries) are picked from a list in your own clock - "9 am" or "09:00", whichever your browser uses. In the Console,
`set` takes the hour as a number (21), or like 9pm or 21:00.

On a human-mode account, *Games to idle*, the idle rotation, the custom game name, *Keep the name while farming*, *Farm in sittings*,
*Hours a day to farm*, the *Farm cards only from … until* clock window, *Log out when finished* and *Farm while appearing offline* are hidden, and human-only
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
Steam chat only reaches the account you messaged: a command that names another account, or `all`, is refused, and so
are the ones that change the app itself: `set`, `anywhere`, `dashboard`, `update`, `import`, `redeem`, `keys`,
`answer`, `add`, `reload`, `plugins`, `notify`, `screen`, `match`, `theme`, `mini`, `visitors`, `backup`, `report`, `stuck`,
`exit` and `remove`, and leaving a game alone for achievements (`dlc leave`, `dlc undo` - looking with `dlc`
is fine). Those are for the PC, the dashboard, Telegram or Discord.

---

[← rep4rep](rep4rep.md) · [Linux, Mac and Docker →](linux-docker-vps.md)
