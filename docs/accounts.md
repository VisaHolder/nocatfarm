[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Accounts and signing in

The dashboard's **+ Add account** does all of this with buttons. From the console it's one line:

```
add myaccount mysteamlogin     # add an account and start signing in
add farm1 qr                   # sign in by scanning a QR code with the Steam app
add myaccount mysteamlogin human   # the same, in human mode (robot for a farm account)
```

The first sign-in asks for the password, then a Steam Guard code (approving the sign-in in the Steam app works
too). After that a login token in `config/tokens/` signs it in, so restarts need neither. With `qr`, or the
*Sign in with a QR code* setting, you scan a code on the dashboard with the Steam app instead, and the account name
comes from Steam.

Questions like these show as a bar at the top of the dashboard and as a `?` prompt in the app window.
`answer <text>` answers from anywhere, including Telegram.

A few things it handles by itself. If the token stops working because you changed the password or signed out
everywhere, it's thrown away and the password is asked for again. After 3 failed sign-ins in a row the account
stops trying, rather than risk Steam blocking your IP. Steam's weekly maintenance on Tuesdays is recognised and
waited out.

**Authenticators.** Put an account's maFile in `config/authenticators/`, named after the account's name in
nocat.farm: `config/authenticators/myaccount.maFile`. It then enters its own Steam Guard codes and can confirm
trades. Or paste the two secrets into the account's settings (*Authenticator code secret* and *Authenticator
confirm secret*, under Show advanced). Either way they're stored encrypted. nocat.farm doesn't create or move
authenticators.

**I also use this account in my own Steam app** (`IUseThisAccount`) is for an account you also use in your own Steam app. With it
on, nocat.farm never changes that account's online status, so your own app keeps Friends & Chat.

**Open in Steam** (`SteamOpenButton`, Windows only) adds a one-click button to an account - on its card and in the
app window - that signs your own Steam desktop app into it, no password typing. Pressing it closes Steam, points
Steam's saved-accounts files at that account, and starts Steam again, so Steam signs straight in from the login it
already remembers. It never stops nocat.farm farming the account: Steam allows the sign-in and only backs the farm
off when you actually launch a game. It's off until you turn it on - tick it in the account's settings, read the
preview of exactly what the button does, and press Turn on; or `steam on myaccount`. Then press the button (it asks
you to confirm first, since Steam closes and reopens), or `steam open myaccount`.

**Want it on every account?** Tick *Open in Steam buttons on every account* in Settings, under All accounts (same
preview, same Turn on), or `steam on all`. Every account then shows the button. To have it on only some accounts,
leave that off and turn it on in those accounts' own settings. `steam off all` hides them again.

**The first time, for an account Steam doesn't remember yet**, the button still works: Steam opens on its sign-in
with the account's login name already filled in. Type its password (and its Steam Guard code if Steam asks), keep
*Remember me* ticked, and from then on it's one click. If nocat.farm has the account's authenticator (its maFile),
the confirm box and the account's card show the current Steam Guard code with a Copy button - just the 5-character
code, nothing else. nocat.farm never types or stores a password for this, and never turns *Remember me* on for you.

**If Steam is set to "Ask which account to use each time Steam starts"**, Steam would show its *Who's playing?*
picker and ignore the account. So the button turns that one setting off (in Steam's `config.vdf`, backed up first,
every other line left exactly as it was) and says so in the log.

It finds the account by its Steam ID (and failing that its Steam login name), never by the name you gave it in
nocat.farm, so a label that differs from the real login still works. It closes Steam first and waits for it to go;
only once Steam has really closed does it back up the files it changes (each backup in its own folder, keeping the
last three), write the change and start Steam again - the same way a double-click on Steam starts it. If Steam
won't close, it changes nothing and says *Steam wouldn't close - if it shows a message box, close it, then try
again*. If the backup fails, it changes nothing and says so. It never touches another account's entry or any login
token.

## Copying settings from another account

A second account doesn't have to be set up again by hand. In **+ Add account**, pick an account under **Copy settings
from**, and the new one starts with that account's settings instead of the defaults. Human or robot is still asked -
it starts as the account you copy from, and you can change it. From the console:

```
add new2 mysteamlogin2 from new          # new2 starts with new's settings
add new2 mysteamlogin2 human from new    # the same, in human mode whatever new is in
```

For an account you already have, open its settings and press **Copy settings from...**, or type
`copy settings new new2`. Both first show every setting that would change on new2, from what to what, and change
nothing. Press **Copy settings**, or type `copy settings new new2 yes`, to copy them. The account you copy from
is never changed. The log says it in one line: *new2: settings copied from new · 3 switches left off for you to turn
on*.

**Never copied** - these belong to one account alone, and the account keeps its own:

- its sign-in: the Steam login name, the password, *Sign in with a QR code* and the Family View PIN
- its authenticator secrets
- its proxy (address, user name and password) and the device name Steam sees
- its notes and its colour in the log
- whether it's switched on, *Start paused*, and *I also use this account in my own Steam app*
- the trade link token of your other account
- the games you left alone for achievements (`dlc leave`)

**Always off after a copy:** Spare items, Steam Points, buying cards, card swaps and Open in Steam. Each one is only
ever turned on after its own preview, on that account. If the account you copy from has one on, the copy leaves it
off and tells you to turn it on yourself after its preview (`spare on new2`, or the switch in its settings).

A setting that names accounts loses the account itself: copying new's *Your other accounts* `new2:to, old` onto new2
leaves `old`, and *Send the sacks to* `new2` becomes empty.

**Kept as the account has them:** whatever spends money, gems or points (making booster packs, using gems for levels,
selling sets, buying full sets, Steam Points levels), its money limits (they're in its own wallet's currency), trading
(fair card swaps, your own accounts, *Trade by itself with*, declining, donations), sending items away (what to send and
when, sacks of gems and unopened packs), *Accept commands from*, and human mode. Copying never starts any of that on
another account - set it there yourself.

The account keeps its own mode: a robot's settings copied onto a human-mode account are switched to human mode the way
a save switching it does (its games become the weights), and the other way round. Only a mode you pick in the same step
(Add account asks human or robot) is taken instead. Everything else comes across as it is. Numbers out of range are put
back the way a save does it, and the preview lists every change.

`disable myaccount` keeps the settings but never signs in, and `enable myaccount` undoes it. `remove myaccount`
deletes the account and its login token. `nickname myaccount <name>` changes the profile name everybody sees, and
`privacy myaccount` shows or sets its profile privacy (`privacy myaccount friends`, or parts like
`inventory=public comments=friends`).

## Coming from another idler

**Import from another idler** is on the Accounts page, under Accounts in Settings, and in the first-run setup. It
lists the idlers nocat.farm can read and what it found in each one's usual place. Pick one, or paste the folder an
idler is in and it works out which one it is. You then see where it looked, every account it found and what comes
with each, and nothing is written until you press **Import**. Tick the accounts to bring, and tick **Human mode** on
the ones you play on yourself. Accounts you already have here are left alone.

The other program's files are only ever read, never changed. Passwords, tokens and authenticator secrets are
stored encrypted like everything else here. Close the other idler before you start the imported accounts, because
two programs on one account keep signing each other out.

| From | Where it looks | What comes across |
|---|---|---|
| **ArchiSteamFarm** | ASF's `config` folder (Desktop, Documents, Downloads, or next to nocat.farm) | The login token (no password, no Guard code), the password (plain, ASF-encrypted, Windows-protected, from an environment variable or a file), the authenticator ASF keeps in `<bot>.db` or a leftover `.maFile` / `.maFile.NEW` / `<SteamID>.maFile`, games, custom name, farming order, priority list, blacklist, Family View PIN, trade and gift settings, booster games, rep4rep. From `ASF.json`: the global blacklist and the web proxy. |
| **Idle Master Extended** | `%LOCALAPPDATA%\IdleMasterExtended` | Blacklist, whitelist (as games to idle), farming order, "only played games". The account name is looked up in this PC's Steam; it asks for the password (or a QR code) once. |
| **Idle Master** | `%LOCALAPPDATA%\IdleMaster`, or ClickOnce's `%LOCALAPPDATA%\Apps\2.0` | Blacklist and farming order. Type the account name in the preview, or leave it empty to sign in with a QR code. |
| **HourBoostr** | Its `Settings.json`, next to its exe | Each account's name, password and games; ignored accounts arrive switched off. Its old login keys don't work on Steam any more, so Steam Guard is asked once. |
| **SingleBoostr** | Its `Settings.json` | No account (it uses whoever the Steam app is signed into). Its blacklist goes into the global one, and its minutes-before-farming onto every account already here. |
| **Steam Game Idler** | `%APPDATA%\com.zevnda.steam-game-idler\cache` (or `cache` beside the portable exe) | Accounts, the games switched on for idling, card-farming blacklist and choices, playtime caps (as hour targets), custom status. Tick **Bring its sign-in over** to read its saved sign-in from the Windows Credential Manager; otherwise it asks for the password. |
| **steam-idler** (3urobeat) | Its folder: `accounts.txt` and `config.json` | Every account with its password and shared secret, the games it plays and its custom status. |

From the console it's `import <asf|ime|idlemaster|hourboostr|singleboostr|sgi|steamidler> [path] [force]`, or
`import auto <folder>`. That imports everything it finds, except Steam Game Idler sign-ins, which need the
dashboard's tick-box. `force` overwrites accounts that already exist here. Imported accounts are added but not
started.

The setup's "switching from" choice is remembered until the first-run setup has shown it once, or until you import.

---

[← The dashboard and the app window](dashboard.md) · [Cards and idling →](cards-and-idling.md)
