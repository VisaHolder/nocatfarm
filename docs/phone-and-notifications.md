[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Phone and notifications

Opening the dashboard itself on your phone is covered in
[Using the dashboard from another device](dashboard.md#using-the-dashboard-from-another-device). This page is about
the other ways to keep an eye on things.

## Discord and Telegram

Everything here is under **Settings → Global settings → Notifications**, and the dashboard has step-by-step guides
for both. The Telegram and Discord parts also show on the Phone page.

### Discord notifications

These use a webhook, which can only post. In Discord, go to Server Settings → Integrations → Webhooks → New Webhook
→ Copy Webhook URL, paste it into **Discord webhook**, and save.

### Discord commands

Commands need your own bot. It's optional and separate from the webhook.

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and sign in.
2. Press **New Application**, name it `nocat.farm`, tick the box and press **Create**.
3. Open **Bot** on the left, press **Reset Token** and copy the token.
4. Paste it into **Discord bot token** and press **Save**. A few seconds later the dashboard says the bot is online
   and shows **Add the bot to your server**.
5. Open that link and add the bot to a server you own. A new, empty one is fine.
6. Press **Connect Discord**. You get a one-time code that works once, for 10 minutes. In Discord, type `/connect`
   and the code, either in the server or in a private chat with the bot (click the bot in the member list and send
   it a message). The bot answers "Connected."

From then on only your Discord account is obeyed. Anyone else gets a private "not allowed". In a server, the
answers are only shown to you, since they can hold Steam Guard codes. To connect a different Discord account, press
**Connect Discord** again or clear *Discord owner* (Show advanced). *Take commands from Discord* (on) switches the
bot off without removing the token.

Pick commands from Discord's `/` menu:

| Command | What it does |
|---|---|
| `/status` | A summary of every account, the last 24 hours, and the version |
| `/dashboard` | The dashboard's links: this PC, the same wifi, and from anywhere |
| `/dashboard anywhere on` (or `/anywhere on`) | Opens the dashboard from anywhere (your router forwards the port) and answers with the link. `/dashboard anywhere off` closes it; `/anywhere` on its own says whether it's on |
| `/cards`, `/human`, `/offers`, `/confirmations`, `/2fa`, `/stats`, `/update` | The everyday ones. Each has an optional `args` box for what goes after it: `/human args: week`, `/offers args: kylro`, `/update args: accept`. |
| `/nocat command: ...` | Any console command, like `/nocat command: pause kylro 30` or `/nocat command: trade accept myaccount 4` |
| `/help` | The command list |
| `/connect code: ...` | Connects your Discord account (see above) |

`remove` and `exit` need `confirm` on the end, as on Telegram: `/nocat command: remove farm1 confirm`.

### Telegram

A Telegram bot does notifications and commands in one.

1. In Telegram, message **@BotFather**, send `/newbot`, and copy the token it gives you.
2. Paste it into **Telegram bot token** and press **Save**.
3. Press **Connect Telegram**. It opens your bot with a private link; press **Start** there. The dashboard then
   says "Telegram is connected."

Only your own connected chat is listened to. With *Take commands from Telegram* on (the default), `/status` sends a
summary of every account, the last 24 hours and the version, and `/help` sends the command list. Any other command
works with a `/` in front: `/cards`, `/offers`, `/trade accept myaccount 4`, `/2fa myaccount`, `/dashboard`.
`/console` switches to console mode, where you type commands without the `/`; send `/console` again to leave it.
`/remove <account>` and `/exit` need `confirm` on the end (`/remove farm1 confirm`, `/exit confirm`), because
nocat.farm can't be started again from Telegram.

### What gets sent

`notify test` (or *Send a test message*) checks both. `notify` shows what's set up, whether the Discord bot is
online and under which name, which Discord account it's connected to, and what gets sent. That last part is ten
switches, under Show advanced:

| Switch | Default |
|---|---|
| Send card drops and badges | on |
| Send free games, items and gifts | on |
| Send trades | on |
| Send problems that need you | on |
| Send updates (a new version is out) | on |
| Send install progress (installing, installed, failed) | off |
| Send the daily summary | on |
| Send profile comments | off |
| Send achievements | off |
| Send rep4rep comments | off |

In the log, lines from Telegram and Discord are tagged `telegram` and `discord`. You can change their colours under
Logging with Show advanced ticked (*Telegram's colour in the log*, *Discord's colour in the log*), from the same
palette as each account's *Colour in the log*.

### Pop-ups and the Discord profile card

**Pop-ups on Windows.** *Show pop-ups* turns them on or off. There are four kinds, each with its own switch under
Show advanced: *Pop up when you earn* (cards, credited comments), *Pop up for comments* (on your profiles), *Pop up
for problems* (Steam Guard needed, failed sign-ins, comment bans, bans) and *Pop up for trade offers*.

**Discord profile card** (*Global settings → Discord profile*, off). While nocat.farm is open, your Discord profile
shows *Playing nocat.farm*, with what it's doing (farming cards, idling games, or *Paused* when every account it
shows is paused or you're playing on it) and the hours banked this past week. It needs the Discord app open on the same PC,
with Discord's Activity Privacy letting it share what you play. Nothing goes over the internet for this. The options
are under Show advanced:

- *Accounts it shows*: *every account not in human mode*, or tick the accounts you want. This only decides the names
  on the card - the numbers always count every account, like *3 accounts linked*
- *Featured account*: a list of your accounts - whose Steam avatar sits on the logo. Left on *the first account
  shown*, it's the first one the card names
- *Show account names*, *Show accounts online*, *Show an account's avatar* and *Show how long it's been running*,
  all on
- *Second line*: with account names off, the card counts cards today, hours past week (the default) or hours past
  month, added up over all your accounts. Every running game counts, the way Steam counts it: 8 games for an hour
  is 8 hours
- Discord allows two pictures and two buttons on a card, no more. The big picture is the logo, the small one the
  featured account's avatar
- *Button 1* and *Button 2*: pick *Get nocat.farm*, an account's Steam page, *Your own link* (a box for the button's
  text and one for where it goes), or *none*. Discord doesn't show you your own buttons, but other people see them.

## Daily summary

*Daily summary in the log* (on) writes a summary at 09:30 (*Summary time · hour* and *· minute*). For each account
it gives hours banked, cards and rep4rep comments in the last 24 hours, with a running total and a line for all
accounts together. Hours count every running game, the way Steam counts them: 8 games for 24 hours is 192 hours. If the PC was off at that time, it's written on the next start. With *Send the daily summary* on,
it goes to Discord and Telegram too. `stats` shows the same 24-hour figures any time, followed by cards and comments
by hour.

## Steam groups

Every account joins the groups in **Groups every account joins** (*Global settings → All accounts*). It starts with
the nocat.farm group. Paste group links or short names separated by commas, or clear the box to stop joining it.

Per account there's *Join the shared groups* (Show advanced, on), which you can turn off to keep one account out;
*Also join these groups*, for groups only that account joins; and *Accept group invites* (off), which joins groups
the account is invited to.

Each account joins at its own random time, minutes apart. Only open groups work. Ones that need approval or an
invite are skipped, and the log says why. `joingroup <account|all> <link>` joins one now.

---

[← Achievements and the hunter](achievements.md) · [rep4rep →](rep4rep.md)
