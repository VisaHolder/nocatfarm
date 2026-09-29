[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Linux and Docker

nocat.farm runs on a home server, a NAS, a Raspberry Pi 4 or 5 (64-bit) or a VPS. There's no window or tray icon on
Linux; you get the console and the web dashboard, and everything else is the same. The dashboard brings its own
font (JetBrains Mono), so it looks the same as on Windows.

## From the zip

1. Download `nocat.farm-v<version>_linux-x64.zip` (Intel/AMD) or `nocat.farm-v<version>_linux-arm64.zip` (Raspberry
   Pi, ARM servers) from [Releases](https://github.com/VisaHolder/nocatfarm/releases/latest).
2. Unzip it into its own folder and start it:

   ```
   unzip nocat.farm-v<version>_linux-x64.zip -d nocatfarm
   cd nocatfarm
   ./nocatFarm
   ```

   If it says *permission denied*, run `chmod +x nocatFarm` once.

There's no .NET to install. It prints the dashboard address when it starts. To use the dashboard from another
device, set a password and open it up (see
[Using the dashboard from another device](dashboard.md#using-the-dashboard-from-another-device)).

To run it as a systemd service, with the folder at `/opt/nocatfarm`:

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

`systemctl stop nocatfarm` signs every account out cleanly first. The log goes to the journal
(`journalctl -u nocatfarm -f`). Type commands in the dashboard's Console tab, which is also where Steam Guard prompts
appear.

**Updating.** It tells you when a new version is out, but on Linux it doesn't update itself. Stop it, unzip the new
zip over the folder (`unzip -o`), and start it again. `config/` and `logs/` aren't in the zip, so they're kept.

Saved logins are encrypted with a key in `config/state/secret.key` that only your user can read, so back up the
whole `config` folder together. A config folder from Windows works too, but each account signs in once more,
because Windows ties saved logins to your Windows user.

The Discord profile card needs the Discord desktop app on the same desktop. On a server or in Docker there's no
Discord app, so it stays off.

## Docker

```
git clone https://github.com/VisaHolder/nocatfarm.git
cd nocatfarm
cp docker-compose.example.yml docker-compose.yml
mkdir -p config logs
echo 'NOCATFARM_WEB_PASSWORD=something-long-and-your-own' > .env
docker compose up -d --build
```

Open `http://localhost:7242` and sign in with that password. Add accounts there; the Steam Guard code or QR scan is
asked for on the page.

The example compose file is commented line by line. The parts worth knowing:

| Line | What it's for |
|---|---|
| `./config` and `./logs` | Your data (`/data/config` and `/data/logs` inside the container). Rebuilding keeps them. |
| `NOCATFARM_WEB_PASSWORD` | Required - compose won't start without it. |
| `"127.0.0.1:7242:7242"` | The port, on this machine only. Change it to `"7242:7242"` to reach it from other devices. |
| `NOCATFARM_HOME_ADDRESS` | For your phone. Uncomment it and put in this computer's address on your wifi, like `192.168.1.20` (add `:port` if you publish a different port). |
| `TZ` | Your time zone. Human mode's day and the daily summary follow it. |
| `hostname` | The device name Steam shows. Without it, it changes on every re-create. |
| `user: "1000:1000"` | Must match the owner of `./config` and `./logs` (`id -u`, `id -g`). If the log says it can't write to `/data/config`, fix this, or remove the line and run `sudo chown -R 1654:1654 config logs`. |
| `stop_grace_period: 1m` | Gives `docker compose down` time to sign every account out cleanly. |

About `NOCATFARM_HOME_ADDRESS`: from inside a container, nocat.farm only sees Docker's own network, and no phone can
reach that. So without this line the Phone page and `/dashboard` can't show you a home link or a QR code - they tell
you to open this computer's address with the published port instead. Set it, run `docker compose up -d`, and the
link and code appear. The port also has to be published beyond `127.0.0.1` (the line above).

Commands go in the dashboard's Console tab, or `docker attach nocatfarm` (Ctrl+P then Ctrl+Q to detach). The log is
`docker compose logs -f`. For plugins, uncomment the `./plugins:/data/plugins` line.

**Coming from ArchiSteamFarm:** uncomment the `/import/asf` line and point it at your ASF folder (the one with
`config/` in it), run `docker compose up -d`, then use **Import from another idler** on the Accounts page - it finds
the folder by itself (or type `import asf /import/asf` in the Console). The accounts come
over with their login tokens, so there's no Steam Guard code to type. ASF's files are mounted read-only and never
changed. Stop ASF first: one account can't be signed in by two programs at once.

**Updating:** `git pull`, then `docker compose up -d --build`. It doesn't update itself in Docker.

The image builds for amd64 and arm64 from the same Dockerfile:
`docker buildx build --platform linux/amd64,linux/arm64 -t nocatfarm .`

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
mkdir -p config logs && chown -R 1000:1000 config logs
echo 'NOCATFARM_WEB_PASSWORD=pick-a-long-password-you-use-nowhere-else' > .env
docker compose up -d --build
```

It keeps running after you log out, and starts again by itself when the server restarts.

**3. Open the dashboard.** The safest way keeps the dashboard closed to the internet and goes through an SSH tunnel
from your PC:

```
ssh -L 7242:127.0.0.1:7242 root@your.server.address
```

Keep that window open and go to `http://localhost:7242` on your PC. That's the server's dashboard, over the
encrypted SSH connection, and nobody else can reach it.

If you want it on your phone, or without a tunnel, there are two ways. The quick one is to open the port: in
`docker-compose.yml` change `"127.0.0.1:7242:7242"` to `"7242:7242"`, run `docker compose up -d`, and allow it in the
server's firewall (`ufw allow 7242/tcp` on Ubuntu). It's then at `http://your.server.address:7242`. That's plain
http, so the long password matters; five wrong tries lock that address out for an hour.

The better one is HTTPS with your own domain. Point a domain at the server and put [Caddy](https://caddyserver.com)
in front; it gets the certificate by itself. A two-line `Caddyfile` does it:

```
farm.yourdomain.com
reverse_proxy 127.0.0.1:7242
```

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
| Back up (the `config` folder is everything) | `scp -r root@your.server.address:nocatfarm/config .` from your PC |
| See the log | `docker compose logs -f` on the server, or the dashboard's Log tab |

---

[← Settings and commands](settings.md) · [Plugins →](plugins.md)
