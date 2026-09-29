[Wiki home](README.md) · [Front page](../README.md)

# nocat.farm commands

Every command nocat.farm has, and what it does. You can type them in the app window, in the dashboard's **Console** tab, on **Telegram** (start with `/`), on **Discord** (`/nocat command:` runs any of them), or send them to one of your accounts in **Steam chat** (start with `/` or `!`, from an account listed under *Accept commands from*).

In the app, `help` lists them all and `help <command>` explains one - it also explains any setting: `help FarmCards`.

**How to read them:** `<account>` is something you must type (an account's name in nocat.farm); `[count]` is optional; `a|b` means one or the other. Most commands that take an account also take `all`, for every account.

*This page is generated from the app's own command list, so it always matches the version you download.*

## All commands at a glance

| | |
|---|---|
| **[Accounts](#accounts)** | `status` `start` `stop` `restart` `pause` `resume` `add` `remove` `enable` `disable` |
| **[Playing](#playing)** | `play` `name` `persona` `nickname` `grind` `human` `wake` `hours` `selfcheck` |
| **[Trading cards](#trading-cards)** | `cards` `drops` `match` `sell` `booster` `levelup` |
| **[Trades & items](#trades--items)** | `offers` `trade` `fairswap` `send` |
| **[Steam Guard](#steam-guard)** | `2fa` `confirmations` `confirm` `deny` |
| **[Achievements](#achievements)** | `cheevo` `hunt` |
| **[Free stuff & keys](#free-stuff--keys)** | `freeitems` `queue` `redeem` `keys` `addlicense` |
| **[Profile & info](#profile--info)** | `value` `level` `balance` `points` `bans` `owns` `privacy` `joingroup` |
| **[rep4rep](#rep4rep)** | `rep4rep` |
| **[Settings](#settings)** | `config` `set` `reload` `import` |
| **[The app](#the-app)** | `log` `stats` `notify` `plugins` `tutorial` `help` `theme` `mini` `dashboard` `anywhere` `unlock` `version` `update` `answer` `exit` |

68 commands in all.

## Accounts

| Command | What it does |
|---|---|
| `status [account]` <br>also `s`, `bots` | What everything is doing right now. |
| `start <account\|all>` | Log an account in. |
| `stop <account\|all>` | Log an account out. It stays configured. |
| `restart <account\|all>` | Stop then start again. |
| `pause <account\|all> [minutes]` | Stay logged in but stop playing, farming and commenting. Give it minutes and it picks back up by itself. |
| `resume <account\|all>` | Undo a pause. |
| `add <name> <steamLogin\|qr> [human\|robot]` | Add an account. It asks for the password once, then remembers a login token - or 'qr' signs it in by scanning a code on the dashboard with the Steam app, no password at all. End with 'human' for your main (human mode) or 'robot' for a farm account - it says which it made. |
| `remove <account>` <br>also `delete` | Delete an account and its stored login token. |
| `enable <account>` | Let this account log in again. |
| `disable <account>` | Keep the account configured but never log it in. |

## Playing

| Command | What it does |
|---|---|
| `play <account> <appIDs\|none>` | Set the games this account idles for playtime. |
| `name <account> [text\|off]` | Custom non-Steam game name shown instead of the real game. No text shows the current one; 'off' clears it. |
| `persona <account> <state>` | What the account shows your friends: online \| offline \| busy \| away \| snooze \| looking to trade \| looking to play \| invisible - or its number, 0-7. Same as the OnlineStatus setting. |
| `nickname <account> <profile name>` | Change the name everybody sees on the profile and friends list. Not the custom game name - that's 'name'. |
| `grind <account\|all> <appID> <hours> \| <account> off` | Put an account on one game for a set number of hours, then let it go back to whatever it was doing. Outranks human mode while it runs. |
| `human [account] [week\|reroll]` | What human mode is doing today, and what it played. Add 'week' to see the next seven days, or 'reroll' to throw today's plan away and roll a fresh one from the current settings. |
| `wake <account>` <br>also `wakeup`, `skipsleep` | Wake a sleeping human-mode account and start its day now. Bed time is unchanged. |
| `hours <account>` | How the account's hour targets are going - hours so far, what's left, and the pace needed to make a date. |
| `selfcheck [account]` <br>also `tells` | Does a human-mode account look like a bot? A score out of 100 from what other people can see - hours on the profile, what its status shows, comments - with the setting that fixes each tell. Boost accounts are left out unless you name one. |

## Trading cards

| Command | What it does |
|---|---|
| `cards [account]` | What is still left to farm, and about how long it will take. |
| `drops <account> [appID\|next] [count\|all] \| <account> off` | You pick a game and how many cards, and it goes first. On a human-mode account it's played in the normal sittings - the main game's share of them, with breaks and bedtime - until that many have dropped. On other accounts it's played non-stop until then. Without an appID, the next game with cards. Automatic card farming ("When to farm cards") needs no command. |
| `match [do]` | Swap duplicate trading cards between your own accounts so sets finish - only swaps that help both sides, never a card already on an offer. Shows what it would trade; 'match do' sends the offers, and the other account accepts them by itself. |
| `sell <account> [preview\|do\|relist] [count]` | Spare trading cards on the market: 'preview' (the default) shows what it would list and what you'd get after Steam's fees, 'do' lists them (5 by default), 'relist' takes down week-old listings the market has gone under. SellDuplicates does it by itself. |
| `booster [account\|all] \| <account> <appIDs>` <br>also `boosters` | Gems, and which games can be made into booster packs now. With appIDs it makes those packs straight away; the BoosterGames setting does it by itself every day. |
| `levelup <account> <level>` <br>also `lvlup` | What reaching a Steam level would cost: the XP missing, badges it can craft from its own cards, sets it has nearly finished, and the cheapest complete sets on the market for the rest - priced gently in the background. |

## Trades & items

| Command | What it does |
|---|---|
| `offers [account\|all]` | Live trade offers, straight from Steam: what's waiting to be accepted, what's been sent, and anything stuck on a confirmation or a trade hold. |
| `trade accept\|decline <account> <number\|all> \| cancel <account> <offer id\|all>` | Answer a trade offer yourself, by the number 'offers' and the announcements give it. Accepting one that sends items out confirms it too when this account's authenticator is in nocat.farm - you asked, so that is the confirmation. 'trade cancel' takes back offers the account sent that haven't gone through, such as one stuck waiting on a confirmation. |
| `fairswap <account> <offerID>` | Whether a trade offer is a fair card swap that AcceptFairCardSwaps would accept, and if not, why. Only looks - never accepts or declines. |
| `send <account\|all> [to <account>] [types]` <br>also `loot` | Send an account's tradable items to the account listed under Trades - or 'to' another of your accounts. Types as in the send setting (cards, foils, backgrounds, emoticons, boosters, gems, all); leave them off for what the send setting says, or trading cards when sending 'to' an account. |

## Steam Guard

| Command | What it does |
|---|---|
| `2fa [account]` <br>also `guard` | Show this account's Steam Guard code, if its authenticator is set up here. Without an account, every account's code. |
| `confirmations [account]` | What's waiting to be confirmed on this account, like the Steam app's list: trades, market listings, account changes - numbered for confirm and deny. Needs the account's authenticator in nocat.farm. |
| `confirm <account> <number\|all>` | Confirm what 'confirmations' listed under that number, or all of it. |
| `deny <account> <number\|all>` | Deny (cancel) what 'confirmations' listed under that number, or all of it. |

## Achievements

| Command | What it does |
|---|---|
| `cheevo <account> <appID> [list\|unlock\|lock] [name\|all]` <br>also `ach`, `achievements` | Achievements: see them, unlock them all, or put them back. |
| `hunt [account]` | What the achievement hunter would play, in order - and what it ruled out and why. |

## Free stuff & keys

| Command | What it does |
|---|---|
| `freeitems [account\|all]` | Look for free event items now: the daily sale sticker, and anything in the Points Shop at 0 points. The ClaimEventItems setting does it by itself. |
| `queue [account\|all]` | Go through today's discovery queue now, a few seconds on each game. The DiscoveryQueue setting does it by itself once a day (during sales, by default). |
| `redeem [account] <key\|file.txt> [key...]` | Activate product keys - or point it at a text file full of them. More than five queues itself and activates them slowly. With an account, only that account ever gets them, queued ones too. Without one it tries each account in turn until one can use it. |
| `keys [list\|clear]` | Product keys waiting to be activated. A big batch queues itself rather than burning Steam's per-account activation allowance all at once. |
| `addlicense <account\|all> <IDs>` | Add free licences to an account's library - a subID, or a/<appID> for a free app. Only works for genuinely free licences - a paid one is refused by Steam, and it says why. |

## Profile & info

| Command | What it does |
|---|---|
| `value [account\|all] [refresh]` <br>also `inv`, `inventory` | What each inventory is worth, by game, and how it has moved in the last day. Add 'refresh' to read the inventories again. |
| `level [account\|all]` | Each account's Steam level. |
| `balance [account\|all]` <br>also `wallet` | Steam wallet balance, and anything still pending. |
| `points [account\|all]` | Steam points each account can spend in the Points Shop. |
| `bans [account\|all]` | Look up the account's bans now: VAC, game bans, a trade ban, a community ban, and which games it's banned in when Steam shows that. Read-only. It also checks by itself every few hours. |
| `owns <appID\|name>` | Which accounts already own a game, and how long each has played it. Takes an appID, a store URL, or part of a name. |
| `privacy <account> [public\|friends\|private\|part=level ...]` | See an account's profile privacy, or set it - one word for everything, or parts such as inventory=public comments=friends. Parts: profile, games, playtime, friends, inventory, gifts, comments. |
| `joingroup <account\|all> <group link or name>` | Join a Steam group now, if it's open - one account or all of them. For a group every account should always be in, put it in the "Groups every account joins" setting instead. |

## rep4rep

| Command | What it does |
|---|---|
| `rep4rep status\|points\|profiles\|tasks\|now\|pause\|resume\|clear\|rest` <br>also `r4r` | Everything rep4rep. Run it bare for a summary. To switch it on or off for an account: set <account> Rep4Rep on\|off. |

## Settings

| Command | What it does |
|---|---|
| `config [account] [all]` | Show the settings and their current values. Add 'all' to include the advanced ones. |
| `set [account] <key> <value>` | Change a setting. Without an account name it changes a global one. |
| `reload` | Re-read every config file from disk. |
| `import <asf\|ime\|idlemaster\|hourboostr\|singleboostr\|sgi\|steamidler\|auto> [path] [force]` | Bring accounts and settings across from another idler - ArchiSteamFarm login tokens and all. |

## The app

| Command | What it does |
|---|---|
| `log [count]` <br>also `logs` | The last few log lines. |
| `stats [hours]` | Each account's last 24 hours - hours banked, cards, comments, totals - then cards dropped and comments posted, by hour. |
| `notify [test]` | Discord and Telegram notifications: says what's set up (the webhook, the Telegram bot, the Discord bot) and what gets sent. 'notify test' sends a test message to each right now. |
| `plugins` | Which plugins are loaded, and where they came from. |
| `tutorial [topic]` <br>also `guide`, `setup` | Getting started, in order, ticking off what you have already done. |
| `help [command\|setting]` <br>also `?`, `h` | This list, or what one command or setting does. |
| `theme [dark\|light]` <br>also `dark`, `light` | Switch the dashboard between the dark and light themes. Without an argument it says which is on. |
| `mini [on\|off]` | Shrink the window to a small panel of your accounts - what each is doing, start and stop, the dashboard - or back to the full window. |
| `dashboard [anywhere on\|off]` <br>also `web`, `link` | The dashboard's address - on this PC, on your phone over the same wifi, and from outside your home if you've set that up. /dashboard on Telegram or Discord sends the same links there. 'dashboard anywhere on' opens it from anywhere and answers with the link; 'dashboard anywhere off' closes it (the same as 'anywhere on\|off'). |
| `anywhere [on\|off]` <br>also `remote` | Open the dashboard from anywhere, not just your wifi - your router forwards the port (UPnP), like Jellyfin. 'anywhere on' does all of it and answers with the link; 'anywhere off' closes it again; on its own it says whether it's on and the link. Works from Telegram and Discord too. |
| `unlock` | Locked out of the dashboard after too many wrong passwords? This lets you (and anyone else locked out) sign in again straight away. |
| `version` <br>also `about` | Which version this is. |
| `update [accept\|now\|skip]` | Check for a newer release. 'update accept' downloads it and restarts into it - or, with 'When I say update' set to wait, installs it once your accounts are asleep; 'update now' always installs right away. 'update skip' skips that version - no more reminders about it and it never installs by itself - until a newer one comes out. Nothing installs by itself unless 'Update by itself' is set to install at night. |
| `answer <text>` | Answer whatever nocat.farm is waiting on - a Steam Guard code, or a password. |
| `exit` <br>also `quit`, `q` | Shut nocat.farm down. |

## More

- [The wiki](README.md) - how everything works, every setting, Linux and Docker.
- [Plugins](../PLUGINS.md) - add your own commands in a few lines of C#. A plugin's commands are listed by `plugins` and on the dashboard's Plugins page.
