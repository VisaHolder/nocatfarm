[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# The dashboard and the app window

## Dashboard tabs

The dashboard is at `http://127.0.0.1:7242/`. Its tabs, in order:

**Overview** has tiles for cards left, games left, cards today and inventory value, then an accounts table,
**Today** (the last 24 hours per account, with a total), the **History** charts and recent activity. The
**Open on your phone** and **Show the walkthrough again** links are here too.

**Accounts** has a card per account. You can search by name, login, notes or game, filter by state with the chips,
and drag cards to change the order (the app window and the console follow the same order). **Import from another
idler** and **+ Add account** are here. Removing an account asks you to type its name first.

**rep4rep** only shows when rep4rep is switched on. **Authenticator** only shows once at least one account's
authenticator is in nocat.farm, and is covered on [the Authenticator page](trades.md#the-authenticator-page). **Phone** has everything for opening the dashboard on your
phone (see [below](#using-the-dashboard-from-another-device)).

**Log** is live, with search, level chips (Debug is hidden by default), an account filter, Follow and Copy. `clear`
(or `cls`) in the Console empties this tab and the Console on this screen only. The app window keeps its lines, and
the log file keeps everything. *Settings → Logging → Open the log folder* opens the files on the PC it runs on.

**Console** runs the same commands as the app window. Tab completes commands and then account names, and up/down
goes through your history. A bare `help` opens a searchable command list.

**Plugins** is always there, and says "off" until plugins are turned on. See [Plugins](plugins.md).

**Settings** has search, **Show advanced**, **Only changed**, a "default" link beside anything you've changed, and a
**Save N changes** button. Settings marked ⟳ need a restart.

You can open a tab by its address, like `http://127.0.0.1:7242/#settings`. The others are `#overview`,
`#accounts`, `#rep4rep`, `#auth`, `#phone`, `#log`, `#console` and `#plugins`.

When you're not typing in a box, these keys switch tabs:

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

Authenticator, Phone and Plugins have no number key.

The dashboard comes in a dark and a light theme (the *theme* button under the tabs, or `theme light`).

## Backup and restore

At the bottom of *Settings → Global settings* is **Backup & restore**. `backup` in the Console does the same
download into a `backups` folder next to `config` and says where.

**Download a backup** gives one file, like `nocat.farm-backup-2026-09-30.zip`, with your settings, every account,
saved logins, authenticator files, the history, lifetime totals, today's human-mode plans, hunt progress, rep4rep
counts and the key queue. Logs and caches aren't in it. A `README.txt` inside says what's there.

Saved logins stay encrypted in the zip, as they are on disk. A backup made on Windows opens them only on the same
PC, signed in as the same Windows user. Restored on another PC, your settings and history come back, but each
account asks for its password (and Steam Guard) once, and authenticators need adding again. Off Windows, the key
goes in the zip too, so it restores anywhere. Either way, keep the zip private: it signs in to your accounts.

**Restore a backup** checks the zip first and shows what's in it: when and where it was made, the accounts, the
files. Nothing changes until you press *Restore*. Then every account stops, the files go back, and the accounts
start again. Accounts that aren't in the backup are left alone, and what gets replaced is saved in `backups` first.
A zip that isn't a nocat.farm backup, or has anything else in it, is refused. Restoring only works in the
dashboard, not from Telegram, Discord or Steam chat. On Linux and a Mac the backups are only readable by your user,
and in Docker they're kept in `./backups` next to `./config`.

## The app window (Windows)

The title bar has *mini*, *hide* and *quit*. The toolbar under it has *start all*, *stop all*, *dashboard*,
*accounts* and *commands*, and says how many accounts are signed in. The accounts panel lists each account with
start/stop, pause/resume, cards and profile buttons, plus *+ add account*. At the bottom is the command line: type a
command and press Enter, or type `help` for the list.

Closing the window (or *hide*) sends it to the tray if there's a tray icon; without one, closing quits. The window
remembers its size and position.

## Mini mode

Click *mini*, type `mini`, or use the tray menu. The window shrinks to a small panel with one line per account:
what it's doing and a start/stop button. A farming account also shows cards left, time left and a progress bar.

It shows 5 accounts. With more, a thin bar underneath counts the rest by what they're doing, like
`+7 more · 6 idling · 1 asleep`. Click the bar to open the list: 10 accounts at a time, the wheel scrolls through the
rest, and the bar says `12 accounts` - click it again to close it. Open or closed is remembered. An account that
needs you (a Steam Guard code, a sign-in that failed, the stuck alarm) goes to the top in yellow, so it's never one
of the hidden ones. The panel never gets taller than your screen.

Beside the name at the top is one number, picked with *Mini window shows* (Settings, Running in the background,
behind Show advanced): game-hours today (the default), the past week or the past month, cards, achievements or
comments in the last 24 hours, accounts online, inventory value, or nothing. Hours are counted like the daily
summary - every game running counts, so 8 games for a whole day is 192h - and added up over all your accounts.

The mini title bar has a pin that keeps it above other windows (the *Keep mini mode on top* setting, off by
default), a button for the dashboard, and one back to the full window. It remembers where you put it, and opens in
mini mode next time if you left it that way.

<p align="center">
  <img src="../assets/mini-mode.png" alt="Mini mode" width="680">
</p>

## Tray

Left- or right-click the icon for the menu: Open dashboard, Hide/Show the window, Mini mode/Full window, Start all
accounts, Stop all accounts and Exit nocat.farm, in the dashboard's language. Double-clicking shows the window.

*Start hidden* (a setting) or `--minimized` starts straight to the tray. *Start with Windows* adds a start-up entry
for your own Windows user.

## Console mode

`--no-gui` (or `--console`) runs without the window. You get a live board with a row per account and the last few
log lines under it. Up/down goes through your recent commands and Esc clears the line. On Linux this is what you
always get.

## Using the dashboard from another device

Out of the box the dashboard only answers this PC. The **Phone** tab is where you change that, and it's also where
the **Open on your phone** buttons elsewhere in the dashboard lead.

At the top are two cards. *At home* is the link for a phone on the same Wi-Fi. *Away from home* is the link for
mobile data, once *Open from anywhere* is on. Each shows its link as a QR code to scan with the phone's camera, with a
Copy button beside it. Until everything the link needs is in place, the code is covered with "Not ready yet".

Under them, a **Ready?** checklist ticks itself off as things get fixed, and puts the fix next to anything that isn't
done:

1. **Dashboard password.** A box to set one, with a strength meter. It's never shown, only replaced. At least 8
   characters, and 12 or more for away from home.
2. **Open to other devices.** A switch that sets *Listen on* to `0.0.0.0` (or back to `127.0.0.1`). The dashboard
   has to restart to pick that up, so a **Restart the dashboard now** button appears. Only the web page restarts;
   your accounts stay signed in. You can't switch this off from the phone itself, since that would lock the phone
   out.
3. **Windows Firewall.** If your phone just keeps loading, Windows is blocking it; many PCs are set not to ask.
   **Allow through Windows Firewall** lets in only the dashboard's port, only on home networks, after Windows asks
   you to confirm. The rule is called "nocat.farm dashboard" if you ever want to remove it. In Docker this line is
   the computer's address instead (see [Docker](linux-docker-vps.md#docker)).
4. **Router** (away from home only). Ticked once the port is being forwarded.

The page also connects Telegram and Discord, which reach your accounts from anywhere with no router involved, and
lists a few handy commands (`/status`, `/cards`, `/pause`, `/resume`, `/dashboard`, `/help`). A short safety note
at the bottom sums up what's exposed and has a button to turn *Open from anywhere* off.

A phone that signs in stays signed in for 7 days (*Stay signed in for*). Five wrong passwords lock that address out
for an hour. On this PC itself the lockout is only a minute, since whoever is at the PC can open the config folder
anyway. A reverse proxy on the same PC, like Caddy for HTTPS, makes every visitor look like this PC, so there
nocat.farm goes by the address the proxy passes on, and each visitor gets the full hour. A proxy somewhere else (Caddy
in front of Docker comes in from Docker's own network) is only believed once its address is in *Trust forwarded
addresses from* (`WebTrustedProxies`, or `NOCATFARM_TRUSTED_PROXIES`). `dashboard unlock` lifts every lockout at once. Without
a password the dashboard refuses everything that isn't this PC, even when it's open to the network. A banner warns
you if the password is short enough to guess.

**Who's been here.** The Phone page lists every sign-in, wrong password and lockout, and every visitor from the internet
turned away, with the address, whether it was this PC, home or the internet, and the device (`iPhone · Safari`). The
last 200 are kept; `visitors` (or `who`) shows them anywhere, and **Sign every device out** (or `visitors signout`)
signs every browser and phone out at once (`/visitors signout` on Telegram). A sign-in from outside your home
is sent to Telegram and Discord (*Send dashboard sign-ins*), and so is anybody guessing: a lockout, the brake below,
or *Open from anywhere* switching itself off (*Send break-in attempts*). Both are on by default.

**From anywhere.** Turning on *Open from anywhere* (on the Phone page, or Settings → Dashboard with Show advanced)
works like Jellyfin. nocat.farm asks your router to forward the dashboard's port to this PC (UPnP), finds your
internet address, and gives you a link and QR code that work anywhere. It needs a password of at least 12
characters, because anyone on the internet can reach the sign-in page. The link is plain http, so use a password you
don't use anywhere else. The forward is taken away when you turn it off or close nocat.farm, and put back when it
starts. Turned off, it's off at once: nocat.farm itself turns away anything from the internet, so a phone that still
had the page open can't carry on through the router. Ten wrong passwords from the internet in an hour, from any
mix of addresses, pause signing in from outside for an hour (home still works, and `dashboard unlock` lifts it). And after
*Turn Open from anywhere off after* wrong passwords or codes from the internet in a day (5 by default, 0 for
never), it switches itself off and nothing from outside gets in, a *Public address* set by hand included, until you turn it
on again (the Phone page, or `anywhere on`) or type `dashboard unlock`. That survives a restart. Backups and restores only work at home or on
this PC, since the zip holds every saved login.

**A code on Telegram.** With Telegram connected, signing in from outside your home takes the password and then a
6-digit code the bot sends you (*Code on Telegram for sign-ins from outside*, on by default). So the password alone
never lets anyone in from the internet, and a code you didn't ask for means somebody has the password. Change it and
type `visitors signout`. The code works for 5 minutes, only for the address it was sent for, and five wrong codes
lock that address out like five wrong passwords. At home and on this PC the password is all it asks. Until Telegram
is connected, the password is enough from outside too; the Phone page says which. Test it on mobile data with the phone's Wi-Fi off. From inside your own home your internet address often
won't open, and that's the router, not nocat.farm. If your router has UPnP switched off, the page says so; forward
the port by hand and put your address in *Public address* instead (a name like `myname.duckdns.org` works too).

The same can be done by typing:

```
set WebPassword something-long-and-your-own
set WebHost 0.0.0.0
```

then restarting. `dashboard` in the console, and `/dashboard` on Telegram and Discord, give the same links as the
Phone page and say what's still missing.

To turn the dashboard off completely, `set WebEnabled false` and restart. Everything keeps working from the console.

<p align="center">
  <img src="../assets/overview.png" alt="The web dashboard" width="880">
</p>

---

[← Start here](start-here.md) · [Accounts and signing in →](accounts.md)
