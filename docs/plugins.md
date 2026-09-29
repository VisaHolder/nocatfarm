[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# Plugins

A plugin is a DLL, written in C#, that adds commands and features. Plugins are off by default. To use one, put the
DLL in the `plugins` folder next to `nocatFarm.exe`, turn on **Load plugins** (Settings → Global settings → Updates &
plugins, under Show advanced, or `set PluginsEnabled true`), and restart. `plugins` and the dashboard's Plugins page
list what's loaded and the commands each one added, and each plugin has its own on/off switch there.

A plugin runs inside nocat.farm, right next to your Steam logins, so only run plugins you wrote yourself or fully
trust.

[PLUGINS.md](../PLUGINS.md) shows how to write one, with a working plugin in about five minutes. ArchiSteamFarm
plugins don't work in nocat.farm; [PLUGINS.md](../PLUGINS.md#not-supported-asf-plugins) explains why.

---

[← Linux and Docker](linux-docker-vps.md) · [Safety and FAQ →](safety-and-faq.md)
