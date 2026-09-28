# nocat.farm plugins

**A plugin is one DLL file that adds your own commands and features to nocat.farm.** You write a small C# class,
build it, drop the DLL in the `plugins` folder, and it runs.

Plugins are for people who can write a little C#. If you just want to use nocat.farm, you don't need any of this.

**Contents** · [Your first plugin](#your-first-plugin) · [A bigger example](#a-bigger-example-card-tally) ·
[What a plugin can do](#what-a-plugin-can-do) · [Cheat sheet](#cheat-sheet) · [Rules](#rules) ·
[Is it safe?](#is-it-safe) · [Sharing it](#sharing-your-plugin) · [ASF plugins](#not-supported-asf-plugins)

---

## Your first plugin

This one adds a `hello` command and writes a line in the log whenever a card drops. It takes about five minutes.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everywhere below, replace
`C:\path\to\nocat.farm` with the folder your `nocatFarm.exe` is in.

### Step 1 - make a project

```
dotnet new classlib -n HelloPlugin
cd HelloPlugin
```

### Step 2 - point it at nocat.farm

Open `HelloPlugin.csproj`, delete everything in it, and paste this:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="nocatFarm">
      <HintPath>C:\path\to\nocat.farm\nocatFarm.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

> Keep `<Private>false</Private>`. Without it, your build copies `nocatFarm.dll` next to your plugin, and the
> plugin ends up with its own copy that doesn't match the running app.

### Step 3 - write the plugin

Delete `Class1.cs`. Make a file called `HelloPlugin.cs` with this in it:

```csharp
using NocatFarm.Plugins;

public sealed class HelloPlugin : INocatPlugin {
    public string Name => "Hello";
    public string Version => "1.0.0";

    public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
        // Write a line in the log whenever a card drops.
        host.CardDropped += (account, appId, cardsLeft) =>
            host.Log($"{account.Name} got a card! {cardsLeft} left in that game.");

        // Add a command. Type "hello" in the console to run it.
        host.AddCommand("hello", "", "Says hi and counts your accounts.", args => {
            int online = host.Accounts.Count(a => a.IsOnline);
            return Task.FromResult($"Hi! {online} of your {host.Accounts.Count} accounts are online.");
        });

        return Task.CompletedTask;
    }
}
```

### Step 4 - build it and copy it in

```
dotnet build -c Release
copy bin\Release\net10.0\HelloPlugin.dll C:\path\to\nocat.farm\plugins\
```

(Make the `plugins` folder next to `nocatFarm.exe` if it isn't there yet.)

### Step 5 - turn plugins on

Plugins are off until you switch them on. In the dashboard, go to **Settings**, tick **Show advanced**, and under
**Updates & plugins** switch on **Load plugins**. Or type `set PluginsEnabled true`. Then restart nocat.farm.

### Step 6 - try it

Type `plugins`. It lists your plugin and the `hello` command. Then type `hello`:

```
> hello
Hi! 2 of your 3 accounts are online.
```

The next time a card drops, the log shows `[plugin] farm1 got a card! 3 left in that game.`

That's the whole loop: **write, build, copy, restart.**

---

## A bigger example: card tally

This one counts every card each account drops, **remembers the count after a restart**, has a **setting** on the
dashboard, and adds a `tally` command.

```csharp
using System.Text.Json;
using NocatFarm.Plugins;

public sealed class CardTally : INocatPlugin {
    public string Name => "CardTally";
    public string Version => "1.0.0";

    private IPluginHost _host = null!;
    private Dictionary<string, int> _cards = new();

    public async Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
        _host = host;

        // A setting. It shows up on the dashboard's Plugins page with a number box.
        host.AddSetting(new PluginSetting(
            "Every", "Shout every", "Write a line in the log every this many cards.",
            PluginSettingKind.Int, Default: "5"));

        // Pick up the count from last time.
        string? saved = await host.LoadStateAsync();
        if (saved != null) {
            _cards = JsonSerializer.Deserialize<Dictionary<string, int>>(saved) ?? new();
        }

        // Count every card that drops.
        host.CardDropped += (account, appId, cardsLeft) => _ = CountAsync(account.Name);

        // "tally" shows the count, "tally reset" starts again.
        host.AddCommand("tally", "[reset]", "Cards counted per account.", async args => {
            if (args.Length > 0 && args[0] == "reset") {
                lock (_cards) { _cards.Clear(); }
                await SaveAsync();
                return "Tally reset.";
            }

            lock (_cards) {
                if (_cards.Count == 0) {
                    return "No cards counted yet.";
                }

                return string.Join("\n", _cards.OrderByDescending(c => c.Value)
                    .Select(c => $"{c.Key}: {c.Value} {(c.Value == 1 ? "card" : "cards")}"));
            }
        });
    }

    private async Task CountAsync(string account) {
        int total;
        lock (_cards) {
            total = _cards[account] = _cards.GetValueOrDefault(account) + 1;
        }

        int every = int.TryParse(_host.Setting("Every"), out int n) && n > 0 ? n : 5;
        if (total % every == 0) {
            _host.Log($"{account} has dropped {total} cards!");
        }

        await SaveAsync();
    }

    private Task SaveAsync() {
        string json;
        lock (_cards) { json = JsonSerializer.Serialize(_cards); }
        return _host.SaveStateAsync(json);
    }
}
```

```
> tally
farm1: 5 cards
myaccount: 1 card
```

Both examples on this page build against nocat.farm as it is now.

---

## What a plugin can do

| | |
|---|---|
| **Watch** | Know when an account signs in or goes offline, when a card drops, and when trade offers are waiting. |
| **Read** | Every account: online or not, what it's playing, cards left, its games and playtime, what its items are worth, and any of its settings. |
| **Do** | Run any command, the same ones you type - like `pause farm1` or `grind farm1 730 2`. |
| **Add** | New commands, and settings that get real controls on the dashboard's Plugins page. |
| **Remember** | Save its own data so it survives restarts and updates. |

What it **can't** do through the API: reach the Steam connection, the login tokens or the config files. Anything
that changes something goes through a command, so it's checked and logged exactly like a command you typed.

---

## Cheat sheet

Everything your plugin gets is on `host`, the `IPluginHost` passed to `OnLoadAsync`.

| On `host` | What it's for |
|---|---|
| `Accounts` · `Account("farm1")` | Every account, or one by the name you gave it in nocat.farm (`null` if there's no such account). Each has `Name`, `SteamId`, `IsOnline`, `Persona`, `Status`, `Playing`, `CardsRemaining` (-1 until it has looked), `Library` and `InventoryByGame`. |
| `Log("text")` | Write a line to the log. It shows up tagged `[plugin]`. |
| `RunCommandAsync("pause farm1")` | Run a command and get back the text it would have printed. |
| `AddCommand(verb, usage, help, handler)` | Add a command. It works in the console, the dashboard and Steam chat, and is listed by `plugins` and on the dashboard's Plugins page (not by `help`). |
| `AddSetting(new PluginSetting(...))` · `Setting("name")` | Declare a setting and read its current value (always as text - parse it yourself). Kinds: `Text`, `Int`, `Bool`, `Choice`. For `Choice`, pass `Choices` as one `"value label"` string per option. |
| `GetSetting("farm1", "FarmCards")` | Read one of an account's own settings, by its setting name. `null` if the account or setting doesn't exist. To change one, run a `set` command. |
| `SaveStateAsync(json)` · `LoadStateAsync()` | Keep your own data between restarts. `LoadStateAsync` returns `null` the first time. |
| `AppVersion` | Which nocat.farm version is running. |

| Event | When it fires |
|---|---|
| `AccountOnline` · `AccountOffline` | An account finished signing in, or went offline. |
| `CardDropped` | A card dropped: the account, the game's appID, and how many cards that game has left. |
| `TradeOffersWaiting` | Steam says trade offers are waiting on an account, and how many. Only the count - use a command such as `offers` if you want the details. |

`INocatPlugin` has one optional extra: `OnUnloadAsync()`, called when nocat.farm closes. It gets 5 seconds.

**Where things are saved** (in the `config/plugins/` folder, named after your plugin's `Name`):

- `<Name>.json` - what you save with `SaveStateAsync`
- `<Name>.settings.json` - the values of your settings, as changed on the Plugins page

The full, commented contract is one file: [`src/NocatFarm/Plugins/IPlugin.cs`](src/NocatFarm/Plugins/IPlugin.cs).
Every built-in command is in [the guide](docs/GUIDE.md#commands).

---

## Rules

- **Plugins live in the `plugins` folder**, next to `nocatFarm.exe` (or inside the `--path` folder if you use
  one). Top level only - DLLs in subfolders are not loaded, but your plugin's own dependencies can sit next to it.
- **Plugins load once, when the app starts,** before any account signs in - so subscribe to events in
  `OnLoadAsync` and you won't miss the first ones. Added or changed a plugin? Restart.
- **`OnLoadAsync` has 15 seconds.** Start-up waits for it, so a plugin that takes longer is left out and logged.
  Don't wait on slow network calls there - start a task instead.
- **Each plugin has its own on/off switch** on the dashboard's Plugins page.
- **You can't take a command that already exists.** A plugin trying to add `stop` (or any built-in name or
  alias) gets a warning in the log, and its version is ignored.
- **A broken plugin only breaks itself.** A plugin that fails to load is left out, with the reason in the log. An
  event handler that throws is logged and skipped - the plugin stays loaded and gets the next event.
- **Event handlers run on nocat.farm's own threads.** Keep them quick, and start a task for anything slow.
- **Several plugins in one DLL** is fine - every `INocatPlugin` class in it loads.
- **Built against an older nocat.farm?** If the app can't read your DLL, the log says it was probably built
  against a different version. Rebuild against the current `nocatFarm.dll`.

## Is it safe?

A plugin runs **inside nocat.farm**, where your Steam sessions are. The API is kept narrow on purpose, but a
determined DLL could still read anything the app can - so **only run plugins you wrote yourself, or from someone you
trust.** That's why plugins are off until you switch them on.

## Sharing your plugin

- Ship **just your DLL** (plus any NuGet libraries it uses). Never ship `nocatFarm.dll` with it -
  `<Private>false</Private>` keeps it out.
- Each plugin loads on its own, so two plugins can use different versions of the same library.
- Tell people what it does, what settings it has, and why they can trust it - they're running your code next to
  their Steam accounts.
- **Licence it however you like**, even closed-source or commercial. nocat.farm is [MPL-2.0](LICENSE), which only
  asks you to share changes to nocat.farm's *own* files - your plugin is your own files.

## Not supported: ASF plugins

ArchiSteamFarm plugins can't run in nocat.farm. They're built against ASF's own code (its `Bot` type, its config and
its internals), and none of that exists here. **Port it instead** - most ASF plugins are a few hundred lines, and
against this API they usually come out shorter.
