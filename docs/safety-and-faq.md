[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Safety and FAQ

## Privacy and safety

Everything stays on your PC. Accounts, login tokens and logs are never uploaded anywhere.

Passwords are optional. After the first sign-in a Steam login token does the work. Login tokens, saved passwords,
authenticator secrets and proxy passwords are all encrypted on disk: on Windows they're tied to your Windows user,
and on Linux and Docker they use a key only your user can read.

nocat.farm talks to Steam, always. It checks GitHub every 2 hours for a new version (*Look for updates every*) and
downloads one when you say so; *Notify if an update is available* turns the check off. Discord, Telegram and rep4rep
only come into it if you set them up, and the Discord profile card only talks to the Discord app on your own PC. The
hunter's game-length lookups go to SteamSpy with nothing but the game's ID. *Open from anywhere*, if you turn it on,
talks to your router.

The dashboard only listens on this PC by default. With no password it refuses anything that isn't from this PC, even
if you open it to the network, and five wrong passwords lock an address out (an hour, or a minute on this PC).
Secrets are never sent to the browser. An empty secret box means "leave it as it is"; use its **Clear** button to
erase one.

It never fights you for your account - see [Staying out of your way](human-mode.md#staying-out-of-your-way).

Automating your accounts is against Steam's Subscriber Agreement and can get an account limited or banned.
nocat.farm is built to be gentle, but only run it on accounts you're willing to risk.

## FAQ

**Will I get banned?** Card idlers have been around for years and bans for idling alone are rare. It is against
Steam's rules though, so there's always some risk. Human mode makes an account look far more normal.

**Can I still play on my account?** Yes. Start a game and that account stands down, then carries on a few minutes
after you stop.

**How many accounts can it run?** As many as you like. They sign in one after another so Steam doesn't mind.

**Do I have to type my password every time?** No. Once, or never with the QR code.

**Can all my accounts join my Steam group?** Yes, put its link in *Groups every account joins*. See
[Steam groups](phone-and-notifications.md#steam-groups).

**I don't want the nocat.farm group.** Clear *Groups every account joins* (Global settings → All accounts).

**Where are my settings?** In the `config` folder next to `nocatFarm.exe`. Back it up and you've backed up
everything.

**Why can't I find the custom name and games-to-idle settings?** The account is in human mode, which hides them on
purpose. Pick its games under *Human mode → Games and how often*.

**Why is a human-mode account offline?** It's asleep, on a break, or taking a day off. `human myaccount` shows its
day, and `wake myaccount` starts it now.

**Why are replies to commands in English?** The dashboard, status lines and log are translated into 11 languages,
but replies to typed commands stay in English.

**I locked myself out of the dashboard.** Five wrong passwords lock an address out. From this PC that's only a
minute. Otherwise, type `unlock` in the app window, or on Telegram.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). There's no npm and no bundler; the dashboard is
plain static files.

```
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
dotnet publish src/NocatFarm -c Release -o run
run\nocatFarm.exe
```

On Linux it's `dotnet publish src/NocatFarm -c Release -r linux-x64 -o run` (or `linux-arm64`), then
`run/nocatFarm`. `tools/package-release.ps1` builds all the release files.

`run/` isn't in the repository. It's where your accounts, tokens and logs end up.

Built on [SteamKit2](https://github.com/SteamRE/SteamKit). Everything else is written from scratch for nocat.farm.

---

[← Plugins](plugins.md) · [Wiki home →](README.md)
