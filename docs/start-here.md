[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Start here

This page takes you from the download to a running account, then through the handful of things most people change.
You don't need to have used an idler before.

The app works the same way: the first-run walkthrough offers **Easy** or **Advanced**, and the Settings page
shows the everyday switches first, with a **Show advanced** box for everything else.

## Words to know

| Word | What it means |
|---|---|
| **Trading cards** | Many Steam games drop a few collectible cards while you play them, usually about half the set. You can sell them, trade them, or turn a full set into a badge. |
| **Farming cards** | Letting a game "play" until all its free cards have dropped. |
| **Idling** | Telling Steam you're playing a game when nobody is. The game doesn't really run, but the playtime counts. |
| **appID** | A game's number. It's in the store link: `store.steampowered.com/app/`**`730`**`/...` is Counter-Strike 2. Wherever an appID is asked for, you can paste the whole link instead. |
| **Human mode** | Makes an account play like a person, with one game at a time, breaks and a bedtime, instead of running around the clock. |
| **Badge** | What a full set of cards turns into. Badges raise your Steam level. |
| **Steam Guard** | The extra code Steam asks for when you sign in, from your email or the Steam app on your phone. |
| **Authenticator / maFile** | The Steam app's Steam Guard, stored as a file. A `.maFile` comes from tools like Steam Desktop Authenticator or ArchiSteamFarm. With one, nocat.farm can type Steam Guard codes and confirm trades by itself. You don't need one. |
| **SteamID64** | A long number that identifies a Steam account, like `76561198000000000`. Some trade settings ask for it. |
| **Dashboard** | nocat.farm's web page. It runs on your own PC and opens in your browser. |
| **Console** | Where you type commands: in the app window, and on the dashboard's Console tab. |
| **ArchiSteamFarm (ASF)** | Another card farming program. nocat.farm can bring your accounts across from it. |

## 1. Install and run

The [latest release](https://github.com/VisaHolder/nocatfarm/releases/latest) has two Windows downloads. They're
the same app, so pick whichever suits you.

**The setup, `nocat.farm-v…-setup.exe`,** is what most people want. It's one screen. It installs to
`%LOCALAPPDATA%\Programs\nocat.farm` without asking for admin rights, which is what lets it keep updating itself,
and adds a Start menu entry, a desktop shortcut and "start with Windows" if you leave those ticked. *change* picks a
different folder, and *advanced* adds the dashboard port and starting hidden in the tray.

If Windows says it protected your PC, click More info, then Run anyway.

It also checks what's already on the PC:

- Pick ArchiSteamFarm, Idle Master or another idler under *switching from*, and the dashboard opens straight on
  bringing those accounts over (see [Coming from another idler](accounts.md#coming-from-another-idler)). It finds
  ASF by itself on your Desktop, in Documents or in Downloads.
- If there's a portable nocat.farm, it offers to move it into the install. Accounts, settings and history come
  along. The old copy is closed first and its folder is left alone.
- If nocat.farm is already installed, a newer setup offers to update (or reinstall) and to uninstall. Updating keeps
  your accounts and settings. An older setup doesn't offer to go back a version.

To uninstall, go to Windows *Settings → Apps → nocat.farm → Uninstall*. It asks whether to delete your accounts
and settings as well - saved Steam logins, authenticators, backups and logs with them. Say no if you might install it
again.

**The portable zip, `nocat.farm-v…-portable.zip`,** installs nothing. Right-click it, choose **Extract All…**, open the
folder and run **`nocatFarm.exe`**. Settings, logs and login tokens all stay in that folder, and uninstalling is
closing it and deleting the folder. It updates itself the same way the installed one does.

The files ending in `_osx-arm64` (M1 and newer) and `_osx-x64` (Intel) are for a Mac, and `_linux-x64` and
`_linux-arm64` for Linux; see [Linux, Mac and Docker](linux-docker-vps.md).

Either way there's nothing else to install, not even .NET. A small window opens, and the dashboard opens in your
browser at `http://127.0.0.1:7242/`.

For a scripted install, see [the setup's command line](settings.md#the-setups-command-line).

## 2. The first-run setup

The first time the dashboard opens, it walks you through a short setup. A bar along the top shows how far along you
are. Every step has **Back**, most have **Skip**, and you can close it at any point. **Show the walkthrough again**
on the Overview page replays it.

1. **Language.** It switches as soon as you pick one.
2. **Easy or Advanced.** Easy is the default: a few plain questions about your phone, updates and the account,
   about two minutes in all. Advanced adds opening it from anywhere, Discord and Telegram, and the update hours,
   and ends by opening every setting of the account. It's for people who've used ArchiSteamFarm or something like
   it.
3. **Your phone.** *Yes, set it up* opens the dashboard to your Wi-Fi. You pick a dashboard password, then scan a QR
   code with your phone. If Windows Firewall is in the way, the button to let it through is right there. *Not now*
   keeps the dashboard on this PC.
4. **From anywhere** (Advanced). The same thing for away from home. Your router forwards the port, the way it does
   for Jellyfin. This needs a password of at least 12 characters.
5. **Password.** Comes up after a yes to the phone, and always in Advanced. A meter shows how strong it is.
6. **Notifications** (Advanced). Connect Telegram or Discord, and choose whether you're told when an update
   installs.
7. **Keep it up to date.** One switch: install new versions at night while your accounts sleep. Advanced also sets
   the hours, the wait after a release and how often it looks. In Docker, or run as a Linux service, this step
   explains how to update by hand instead.
8. **What kind of account is it?** Asked when you have no accounts yet.
   - *My main - I play on it* adds it in human mode, so it acts like a person. You can also tick
     *I also sign into it from my own Steam app*. Then nocat.farm never changes its online status, so it never
     kicks you off Friends & Chat.
   - *A spare or farm account* is robot mode: it farms cards and idles games around the clock at full speed.

   The **Human mode** switch in the account's settings changes this later.

Then the account itself. Type the **Steam account name**: what you type to sign in to Steam, not your display name
or email. The **password** is optional; leave it empty and you're asked once when it signs in. A nickname for the
account in nocat.farm is optional too. In Advanced you can tick **Sign in with a QR code** instead and scan a code
with the Steam app, so nothing is typed at all.

If the setup finds another idler with accounts in it (ArchiSteamFarm, Idle Master, HourBoostr, Steam Game
Idler...), it lists those accounts instead. Tick the ones to bring, tick **Human mode** on the ones you play on
yourself, and press **Import them**. *Coming from another idler? Import from it* under the form picks a different
one. Close the other idler first, so two programs aren't signed in to the same account.

The Steam Guard code, password or QR code is asked for right there in the setup. Once it's signed in you can pick
its games. A human-mode account shows its most-played games: tap a main game and a few side games, and Advanced
also asks for its routine (hours a day, when it gets on, bedtime). A farm account can pick games to idle and a
custom game name. **Skip - use the defaults** is always there.

From then on nocat.farm keeps a Steam login token and signs itself in. No password is stored unless you type one
into the settings.

## 3. What it does by itself

With no setup at all, every account farms its trading cards one game after another, tells you roughly how long is
left, and then idles the games you gave it (if you gave it any). If you start a game yourself on that account, it
stops, and carries on a few minutes after you finish.

It also, without being asked:

- accepts gifts (wallet gift cards, guest passes, games friends send) and donations (offers where you give nothing)
- collects free event items: the daily sticker during a Steam sale, and anything free in the Points Shop
- goes through the discovery queue during Steam sales, for the sale items and badge progress
- watches for bans and tells you straight away if one appears
- joins the nocat.farm Steam group, which you can change or clear (see
  [Steam groups](phone-and-notifications.md#steam-groups))
- clears Steam's notification counters
- writes a daily summary in the log at 09:30
- checks for updates every 2 hours and tells you what's new, but only installs one by itself if you set *Updates*
  to *install by itself at night*

These stay **off** until you turn them on: human mode (unless you picked "My main"), claiming free games, earning
achievements, selling cards, crafting badges, fair card swaps, Discord and Telegram, rep4rep, and plugins.

## 4. Finding your way around

The dashboard is where you do almost everything. Its tabs, in order:

| Tab | What's on it |
|---|---|
| Overview | Every account at a glance, today's cards, and history charts. |
| Accounts | One card per account with Start, Pause, Stop, Cards and Settings. **+ Add account** is here. |
| rep4rep | Only there if you turn rep4rep on. Most people never see it. |
| Authenticator | Steam Guard codes and trade confirmations. Only there once an account's authenticator is in nocat.farm. |
| Phone | Opening the dashboard on your phone, at home or away, and Telegram and Discord. |
| Log | What happened, in plain words. |
| Console | Where you type commands. |
| Plugins | Add-ons. Off unless you turn them on. |
| Settings | Every switch. Pick **Global settings** or an account on the left. |

On Windows there's also the app window, with the log, a command line, and buttons to start or stop everything,
open the dashboard and list your accounts. **mini** shrinks it to a small panel with one line per account. The
tray icon by the clock has a menu too: open the dashboard, show or hide the window, mini mode, start or stop all
accounts, and exit. Closing the window leaves nocat.farm running in the tray.

## 5. Things most people change

All of these are in **Settings**, with the account picked on the left. The command does the same thing.

**Games to idle** (farm accounts): *What it plays → Games to idle*, or `play myaccount 730, 440`. These play once
the cards are done.

**A custom game name** (farm accounts): *What it plays → Show as*, or `gamename myaccount nocat.lol`. Friends see that
name instead of the real game, and the real games still count playtime.

**Human mode**: *Human mode → Human mode*. Then set **Games and how often** (`730:70, 440:30` means 70%
Counter-Strike 2 and 30% Team Fortress 2, on average over a week - one day can lean more toward one game), the hours it
plays and its bedtime.

**Claim free games**: *Free stuff → Claim free games*, set to *games* (or *games and DLC* for free DLC too). Paid
games that Steam gives away free-to-keep get added to the account.

**I sign into this one myself**: *Account → I sign into this one myself*. Turn it on for any account you also use in
your own Steam app.

**Notifications on your phone**: *Global settings → Notifications*. Paste a Discord webhook or connect a Telegram
bot; [Discord and Telegram](phone-and-notifications.md#discord-and-telegram) has the steps.

**Start with Windows**: *Global settings → Running in the background → Start with Windows*.

Tap or hover the ⓘ beside any setting, or type `help <setting>`, to see what it does.

## 6. Everyday commands

Type these in the app window or the dashboard's Console tab, using the name you gave the account.

| Command | What it does |
|---|---|
| `status` | What every account is doing right now. |
| `start myaccount` · `stop myaccount` | Sign an account in or out. `all` works too. |
| `pause myaccount 60` | Stop playing for 60 minutes, then carry on by itself. `resume myaccount` ends it early. |
| `cards` | Cards left to farm, and about how long it'll take. |
| `play myaccount 730, 440` | Idle these games. |
| `gamename myaccount nocat.lol` | Show this name instead of the real game. `gamename myaccount off` removes it. |
| `offers` | Trade offers waiting on your accounts. |
| `update` | Check for a new version. |
| `help` | Every command. `help <command>` or `help <setting>` explains one; `help rot` lists everything starting with "rot". |

`tutorial` shows a getting-started checklist that ticks off what you've already done.

## 7. Updating

**On Windows**, when a new version is out the dashboard shows an **Update** button. Click it, or type
`update accept`. nocat.farm downloads the new version, signs your accounts out one by one, swaps the files and
restarts. Your accounts, settings, logs and plugins are kept.

That's *Updates* set to *install when I click* (Settings → Global settings → Updates & plugins), the default. If
you'd rather it didn't restart while an account is in the middle of something, set it to *install when I click, once everyone's asleep*. The Update button and
`update accept` then wait until no human-mode account is awake, nobody is playing on any account, and no trade or
gift is waiting its turn. Robot accounts that stay on all night don't hold it up. With no human-mode accounts at
all, it waits for the night install hours instead (3 to 6 in the morning unless you change them). A
waiting update is forgotten if you restart nocat.farm. `update now` always installs straight away.

To have it update with no click at all, set *Updates* to **install by itself at night**. It then installs a new
version on its own, but only between the hours you pick (3 to 6 in the morning by default), only once the version
has been out for a while (*Wait after a release for*, 2 hours) so a bad release can be fixed first, and only while
no human-mode account is awake, nobody is playing and no trade offer or gift is waiting. *Look for updates every*
sets how often it checks (2 hours).

`update file <zip>` installs a nocat.farm zip that's already on this PC, the same way, putting back included. It only
works in the nocat.farm window or its console, or the dashboard opened on this PC. A zip with the same or an older
version needs `update file <zip> force`. Type just `update file` (or press *Install from a file…* under Settings →
Updates & plugins on this PC) and a window opens to pick the zip, starting in Downloads. On Linux or a Mac, type the
path. In Docker, nocat.farm is updated by hand (see below).

To go back to an older version, `update versions` lists the ones still on GitHub (the last 5 releases), and
`update to 1.7.1` installs one, the same way. Or use *Go back to an older version* under Settings → Updates &
plugins. Your settings are saved first (in config/backups), and any the older version doesn't know come back when
you update again, without undoing what you changed in between. The version you came from won't install by itself
until a newer one is out; `update accept` brings it back.

`update skip` stops the reminders for that version, and it will never install by itself. The version after it is
announced as usual, and `update accept` still installs the skipped one if you change your mind.

If a new version won't start, it's put back by itself. The new version has to run for half a minute first. If it
crashes (usually within seconds) or hasn't said it's fine after three minutes, the old files go back, the old
version starts again, the log says so, and that version is skipped.

The message that a new version is out lists what changed. After updating, the log has one line, "what's new in
<version>", with a link to the release. With
*Send install progress* on (it's off by default), Telegram and Discord get "Downloaded 1.5.4 - installing it now",
then "Install complete - now on 1.5.4" with what's new, or that it failed or was undone and why.

**The Linux and Mac zips** update themselves just like Windows, putting back included. **In Docker, or run as a
Linux service** (systemd), it tells you when a new version is out and you update it yourself: see
[Updating as a service](linux-docker-vps.md#from-the-zip) and [in Docker](linux-docker-vps.md#docker).

## 8. If something looks wrong

Check the **Log** tab first. It says what happened in plain words. Some common ones:

| What you see | What to do |
|---|---|
| "Steam Guard code needed", or it asks for a password | Type the answer in the bar at the top of the dashboard, or at the `?` prompt in the app window. `answer <text>` works too. |
| An account stopped trying to sign in | After 3 failed sign-ins in a row it stops, so Steam doesn't block your connection. Check the password, then `start myaccount`. |
| "Rate-limited" or "too many logins" | Steam slows down a PC that asks too much, usually after lots of restarts. It waits it out by itself. |
| Steam kicks you off Friends & Chat | Turn on *I sign into this one myself* for that account. |
| Friends see the real game, not your custom name | The account card warns "Steam shows …" and nocat.farm puts the name back by itself. Human-mode accounts have no custom name, on purpose. |
| Nothing is farming | Type `cards myaccount`. With no cards left it idles its games, if it has any. If it says it's waiting, human mode may have it asleep or on a break; `human myaccount` shows its day. |
| The dashboard says "Too many wrong passwords" | Five wrong tries lock that address out: for a minute on this PC, an hour from anywhere else. The page counts down; type `dashboard unlock` in the app window (or on Telegram) to skip the wait. |
| The settings page looks short | Tick **Show advanced**. |

Still stuck? Open an [issue](https://github.com/VisaHolder/nocatfarm/issues) and paste the log lines.

---

[← Wiki home](README.md) · [The dashboard and the app window →](dashboard.md)
