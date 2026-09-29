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

**I sign into this one myself** (`IUseThisAccount`) is for an account you also use in your own Steam app. With it
on, nocat.farm never changes that account's online status, so your own app keeps Friends & Chat.

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

The setup's "coming from" choice is remembered until the first-run setup has shown it once, or until you import.

---

[← The dashboard and the app window](dashboard.md) · [Cards and idling →](cards-and-idling.md)
