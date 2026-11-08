[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Add-ons

Add-ons are extras that aren't part of nocat.farm itself. Each one is a single file you can install, switch off or
remove at any time. The official ones are made by reap and listed here and on
[nocat.lol/nocatfarm/addons](https://nocat.lol/nocatfarm/addons).

| Add-on | What it does |
|---|---|
| [rep4rep](rep4rep.md) | Earns rep4rep points by posting the profile comments rep4rep hands out, at a human pace, from the accounts you switch on. Was built into nocat.farm until 1.9.0. |

The list nocat.farm reads is [`addons/addons.json`](../addons/addons.json) in this repository.

## Installing one

**From the dashboard:** open the **Plugins** tab. *Official add-ons* lists each one and whether you have it. Press
**Install**, then restart nocat.farm. Installing also turns on *Load plugins* for you.

**From the console:** `addons` lists them, `addons install rep4rep` installs one, and `addons remove rep4rep` takes it
out again. Restart after either one.

Installing and removing only work at this PC: in the nocat.farm window or its console, or on the dashboard opened on
this PC. A dashboard you signed into with its password works too. They don't work from Steam chat, Telegram or Discord.

Once it's loaded, an add-on's settings are under its name on the **Plugins** tab. If it has a tab of its own (rep4rep
does), that shows up next to Plugins. Each add-on has its own on/off switch there as well. Removing one keeps its
settings, so if you install it again it carries on where it left off.

## Why it's safe to install

An add-on runs inside nocat.farm, next to your Steam logins. So nocat.farm only installs an official one when all of
these checks pass:

- **The list is signed.** `addons.json` comes with `addons.json.sig`, a signature made with reap's private key. That
  key is kept on reap's PC and never put online. nocat.farm has the matching public key built in, and refuses a list
  whose signature doesn't match. So someone who got into the GitHub repository still couldn't add or swap an add-on.
- **The file is the one the list names.** The file is downloaded from nocat.farm's own GitHub releases, never from
  anywhere else. Its SHA-256 has to match the one in the signed list, or it isn't installed.
- **It fits your version.** An add-on that needs a newer nocat.farm says so instead of installing.
- **Only names from the list.** The dashboard and the command take an add-on's name, never a web address.

## Your own plugins

A `.dll` you put in the `plugins` folder by hand still loads the way it always has. It isn't checked against anything,
so only run plugins you wrote yourself or got from someone you trust. [PLUGINS.md](../PLUGINS.md) shows how to write
one. Plugins are off until you turn on *Load plugins*.

## What an add-on can't do

Add-ons get the same narrow plugin API as any other plugin. They can see your accounts but can't touch the Steam
connection, your logins or your config files. Anything that changes something goes through a command. The one exception
is posting a profile comment as an account, which rep4rep needs, and nocat.farm stays in charge of it:

- never on an account that's offline, paused, switched off or signing off
- in human mode, only while the account is up and settled in (not while it's asleep, on a break or while you're
  playing on it), and never sooner than a person would leave the next one
- at most 25 comments per account in any 24 hours, from every add-on together, and the count survives restarts
- one comment at a time, and the profile is checked first, so nothing goes out where there's no comment box

---

[← Linux, Mac and Docker](linux-docker-vps.md) · [Plugins →](plugins.md)
