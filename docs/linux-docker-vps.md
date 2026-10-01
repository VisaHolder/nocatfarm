[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Linux, Mac and Docker

nocat.farm runs on a home server, a NAS, a Raspberry Pi 4 or 5 (64-bit) or a VPS. There's no window or tray icon on
Linux; you get the console and the web dashboard, and everything else is the same.

## From the zip

1. Download `nocat.farm-v<version>_linux-x64.zip` (Intel/AMD) or `nocat.farm-v<version>_linux-arm64.zip` (Raspberry
   Pi, ARM servers) from [Releases](https://github.com/VisaHolder/nocatfarm/releases/latest).
2. Unzip it into its own folder and start it:

   ```
   unzip nocat.farm-v<version>_linux-x64.zip -d nocatfarm
   cd nocatfarm
   ./nocatFarm
   ```

   If it says *permission denied*, run `chmod +x nocatFarm` once. No `unzip` on a fresh server? `sudo apt install unzip`
   (Debian, Ubuntu, Raspberry Pi OS) or `sudo dnf install unzip` (Fedora).

There's no .NET to install. It prints the dashboard address when it starts. To use the dashboard from another
device, set a password and open it up (see
[Using the dashboard from another device](dashboard.md#using-the-dashboard-from-another-device)).

It runs on the machine's own time zone, which human mode's day and the daily summary follow. A VPS is usually on UTC:
`timedatectl` shows it, and `sudo timedatectl set-timezone Europe/London` (yours) changes it.

To run it as a systemd service, with the folder at `/opt/nocatfarm`: give the folder to the user it runs as, then put
this in `/etc/systemd/system/nocatfarm.service` (with your user name for `youruser`):

```
sudo chown -R youruser: /opt/nocatfarm
sudo nano /etc/systemd/system/nocatfarm.service
```

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

Then start it, and have it start with the machine:

```
sudo systemctl daemon-reload && sudo systemctl enable --now nocatfarm
```

`sudo systemctl stop nocatfarm` signs every account out cleanly first. The log goes to the journal
(`journalctl -u nocatfarm -f`). Type commands in the dashboard's Console tab, which is also where Steam Guard prompts
appear.

**Updating.** Started from a terminal or a desktop, it updates itself like on Windows: **Update** on the dashboard,
`update accept`, or *Update by itself* at night. It makes a safety copy first, and a new version that won't start is
put back. The new version starts in the background; the dashboard is where you see it. **Run as a service** (the systemd
unit above) it doesn't, because systemd would stop the update half way: it tells you when a new version is out, and
you stop it, unzip the new zip over the folder (`unzip -o`), and start it again. `config/` and `logs/` aren't in the
zip, so they're kept.

Closing the terminal it runs in (or the SSH session ending) closes it cleanly, accounts signed out first. To keep it
running after you log out without a service, start it with `nohup ./nocatFarm &`, or inside `tmux` or `screen`. One copy
runs per config folder: a second one on the same folder says which process already has it, and exits.

Saved logins are encrypted with a key in `config/state/secret.key` that only your user can read, so back up the
whole `config` folder together. Backups (`backup`, in the `backups` folder) are only readable by your user too. A config
folder from Windows works too, but each account signs in once more,
because Windows ties saved logins to your Windows user.

The Discord profile card needs the Discord desktop app on the same desktop. On a server or in Docker there's no
Discord app, so it stays off.

## Mac

1. Download `nocat.farm-v<version>_osx-arm64.zip` for an M1 or newer Mac, or `nocat.farm-v<version>_osx-x64.zip` for
   an Intel Mac, from [Releases](https://github.com/VisaHolder/nocatfarm/releases/latest), or *mac* / *mac intel* on
   [nocat.lol/nocatfarm](https://nocat.lol/nocatfarm).
2. Unzip it (Safari does that by itself) and double-click `start.command`. It opens in Terminal, and the dashboard
   opens in your browser. Keep that Terminal window open (minimise it if you like). Closing it signs the accounts out and stops nocat.farm.
3. If the Mac says it can't be opened, go to **System Settings → Privacy & Security**, scroll down and press
   **Open Anyway**, once. `start.command` then takes the same "downloaded from the internet" mark off everything else
   in the folder by itself.

Like on Linux there's no window or tray icon: you get the Terminal and the web dashboard, and everything else is the
same. To have it start when you log in: **System Settings → General → Login Items**, press **+** and pick `start.command`.

**Updating.** It updates itself like on Windows: **Update** on the dashboard, `update accept`, or *Update by itself*
at night. The old version closes, and the new one opens in a Terminal window of its own. A safety copy is made
first, and a new version that won't start is put back.

Saved logins are encrypted with a key in `config/state/secret.key` that only your user can read, as on Linux.

## Docker

Docker from get.docker.com has everything. Ubuntu's own `docker.io` package needs Compose added:
`sudo apt install docker-compose-v2`.

```
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
cp docker-compose.example.yml docker-compose.yml
mkdir -p config logs backups
echo 'NOCATFARM_WEB_PASSWORD=something-long-and-your-own' > .env
echo 'TZ=Europe/London' >> .env
docker compose up -d --build
```

Put your own time zone in `TZ` ([the list](https://en.wikipedia.org/wiki/List_of_tz_database_time_zones), the *TZ
identifier* column). Without it the container runs on UTC.

Open `http://localhost:7242` and sign in with that password. Add accounts there; the Steam Guard code or QR scan is
asked for on the page.

The example compose file is commented line by line. The parts worth knowing:

| Line | What it's for |
|---|---|
| `./config`, `./logs` and `./backups` | Your data (`/data/config`, `/data/logs` and `/data/backups` inside the container). Rebuilding and re-creating keep them. |
| `NOCATFARM_WEB_PASSWORD` | Required. Compose won't start without it. |
| `"127.0.0.1:7242:7242"` | The port, on this machine only. Change it to `"7242:7242"` to reach it from other devices. |
| `NOCATFARM_HOME_ADDRESS` | For your phone. Uncomment it and put in this computer's address on your Wi-Fi, like `192.168.1.20` (add `:port` if you publish a different port). |
| `TZ` | Your time zone, read from `.env`. Human mode's day and the daily summary follow it. UTC without it. |
| `NOCATFARM_PUBLIC_ADDRESS` | Only when it's reached from the internet on purpose (a VPS, a published port, a proxy): the address people use, like `farm.example.com`. Without it every visitor from the internet is refused. See [On a VPS](#on-a-vps-a-rented-server). |
| `NOCATFARM_TRUSTED_PROXIES` | Only behind Caddy or another proxy outside the container: `172.16.0.0/12` (Docker's own networks). See [On a VPS](#on-a-vps-a-rented-server). |
| `hostname` | The device name Steam shows. Without it, it changes on every re-create. |
| `user: "1000:1000"` | Must match the owner of `./config`, `./logs` and `./backups` (`id -u`, `id -g`). If the log says it can't write to `/data/config`, fix this, or remove the line and run `sudo chown -R 1654:1654 config logs backups`. |
| `stop_grace_period: 1m` | Gives `docker compose down` time to sign every account out cleanly. |

About `NOCATFARM_HOME_ADDRESS`: from inside a container, nocat.farm only sees Docker's own network, and no phone can
reach that. So without this line the Phone page and `/dashboard` can't show you a home link or a QR code. They tell
you to open this computer's address with the published port instead. Set it, run `docker compose up -d`, and the
link and code appear. The port also has to be published beyond `127.0.0.1` (the line above).

Commands go in the dashboard's Console tab, or `docker attach nocatfarm` (Ctrl+P then Ctrl+Q to detach). The log is
`docker compose logs -f`. For plugins, uncomment the `./plugins:/data/plugins` line.

**Coming from ArchiSteamFarm:** uncomment the `/import/asf` line and point it at your ASF folder (the one with
`config/` in it), run `docker compose up -d`, then use **Import from another idler** on the Accounts page. It finds
the folder by itself (or type `import asf /import/asf` in the Console). The accounts come
over with their login tokens, so there's no Steam Guard code to type. ASF's files are mounted read-only and never
changed. Stop ASF first: one account can't be signed in by two programs at once.

**Updating:** `git pull`, then `docker compose up -d --build`. It doesn't update itself in Docker.

The image builds for amd64 and arm64 from the same Dockerfile. `docker compose up -d --build` builds it for the
machine it's on. To build both at once you need somewhere to put them, like your own registry:
`docker buildx build --platform linux/amd64,linux/arm64 -t yourname/nocatfarm --push .`
(or one platform with `--load`, to use it on this machine). `docker ps` shows whether it's *healthy*: the dashboard
answering.

## On a VPS (a rented server)

A VPS is a small Linux computer you rent by the month from a hosting company, and it means nocat.farm can run 24/7
without your own PC staying on. The cheapest ones are fine: 1 CPU and 1 GB of memory is plenty for a handful of
accounts. Pick Ubuntu 24.04 (64-bit) when it asks. Intel/AMD and ARM both work.

**1. Get in.** The host gives you an address and a password or key. From your PC (Windows Terminal works):

```
ssh root@your.server.address
```

**2. Install Docker and start nocat.farm.** Paste these one at a time:

```
curl -fsSL https://get.docker.com | sh
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
cp docker-compose.example.yml docker-compose.yml
mkdir -p config logs backups && chown -R 1000:1000 config logs backups
echo 'NOCATFARM_WEB_PASSWORD=pick-a-long-password-you-use-nowhere-else' > .env
echo 'TZ=Europe/London' >> .env
docker compose up -d --build
```

Put your own time zone in `TZ`; the server's is usually UTC, and human mode's day follows this one.

It keeps running after you log out, and starts again by itself when the server restarts.

**3. Open the dashboard.** The safest way keeps the dashboard closed to the internet and goes through an SSH tunnel
from your PC:

```
ssh -L 7242:127.0.0.1:7242 root@your.server.address
```

Keep that window open and go to `http://localhost:7242` on your PC. That's the server's dashboard, over the
encrypted SSH connection, and nobody else can reach it.

If you want it on your phone, or without a tunnel, nocat.farm has to be told it's meant to be reached from the
internet. Everything from outside is turned away until it is: *Open from anywhere* asks a home router to forward the
port (UPnP), and a VPS has no router to ask. The server's own address does it. In `docker-compose.yml`, uncomment
`NOCATFARM_PUBLIC_ADDRESS` and put the address in (or, through the SSH tunnel, type
`set WebPublicAddress your.server.address:7242` in the dashboard's Console). Then there are two ways.

The quick one is to open the port: in `docker-compose.yml` change `"127.0.0.1:7242:7242"` to `"7242:7242"`, set
`NOCATFARM_PUBLIC_ADDRESS: your.server.address:7242`, run `docker compose up -d`, and allow it in the server's
firewall (`ufw allow 7242/tcp` on Ubuntu). It's then at `http://your.server.address:7242`. That's plain http, so the
long password matters; five wrong tries lock that address out for an hour.

The better one is HTTPS with your own domain. Point a domain at the server and put [Caddy](https://caddyserver.com)
in front; it gets the certificate by itself. A two-line `Caddyfile` does it:

```
farm.yourdomain.com
reverse_proxy 127.0.0.1:7242
```

Then, in `docker-compose.yml`, uncomment and fill in both of these, and run `docker compose up -d`:

```
      NOCATFARM_PUBLIC_ADDRESS: farm.yourdomain.com
      NOCATFARM_TRUSTED_PROXIES: 172.16.0.0/12
```

The second one matters. Caddy on the server reaches the container through Docker's own network (an address like
`172.18.0.1`), not as "this PC", so without it nocat.farm doesn't believe the visitor's address Caddy passes on, and
everybody on the internet looks like they're at home: no sign-in code on Telegram, one lockout shared by everyone, and
*Open from anywhere* never switching itself off. `172.16.0.0/12` covers the networks Docker makes; it's the setting
*Trust forwarded addresses from* (`WebTrustedProxies`). With the Linux zip instead of Docker, Caddy on the same server
comes in as this PC, which is already believed, so only `NOCATFARM_PUBLIC_ADDRESS` (or *Public address*) is needed.

**4. Add your accounts** in the dashboard, the same as on a PC. The Steam Guard code, or the QR scan with the Steam
app, is asked for right on the page. If you already have nocat.farm on your PC, stop it there, copy its `config`
folder into the server's `nocatfarm/config` (with `scp -r`), and start it. Each account signs in once more, because
Windows ties saved logins to your Windows user.

A few things to know. Steam sees the server's location: signing in from a data centre in another city or country is
normal for an idler, but the first sign-in may ask for Steam Guard, and your account's recent logins will show the
server. For an account you also play on, a server near you looks most natural. Don't run the same account on your PC
and the server at once, or they keep signing each other out.

| To | Run |
|---|---|
| Update | `cd nocatfarm && git pull && docker compose up -d --build` |
| Back up (the `config` folder is everything) | `scp -r root@your.server.address:nocatfarm/config .` from your PC, or `backup` in the Console (it lands in `nocatfarm/backups`) |
| See the log | `docker compose logs -f` on the server, or the dashboard's Log tab |

---

[← Settings and commands](settings.md) · [Plugins →](plugins.md)
