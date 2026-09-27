# nocat.farm plugins

**A plugin is one DLL file that adds your own commands and features to nocat.farm.** You write a small C# class,
build it, drop the DLL in the `plugins` folder, and it runs.

**Contents** · [Your first plugin](#your-first-plugin-5-minutes) · [A bigger example](#a-bigger-example-card-tally) ·
[What a plugin can do](#what-a-plugin-can-do) · [Cheat sheet](#cheat-sheet) · [Rules](#rules) ·
[Is it safe?](#is-it-safe) · [Sharing it](#sharing-your-plugin) · [ASF plugins](#not-supported-asf-plugins)

---

## Your first plugin (5 minutes)

This one does two things: it says something in the log whenever a card drops, and it adds a `hello` command.

**1. Make a project.** You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet new classlib -n HelloPlugin
cd HelloPlugin
```

**2. Point it at nocat.farm.** Replace everything in `HelloPlugin.csproj` with this, and change the path to
wherever your `nocatFarm.exe` is:

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

> Keep `<Private>false</Private>`. Without it your build copies `nocatFarm.dll` next to your plugin, and you end up
> with two copies of everything that don't recognise each other.

**3. Write it.** Delete `Class1.cs` and make `HelloPlugin.cs`:

```csharp
using NocatFarm.Plugins;

public sealed class HelloPlugin : INocatPlugin {
    public string Name => "Hello";
    public string Version => "1.0.0";

    public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
        // 1. Say something in the log whenever a card drops.
        host.CardDropped += (account, appId, cardsLeft) =>
            host.Log($"{account.Name} got a card! {cardsLeft} left in that game.");

        // 2. Add a command. Type "hello" in the console to run it.
        host.AddCommand("hello", "", "Says hi and counts your accounts.", args => {
            int online = host.Accounts.Count(a => a.IsOnline);
            return Task.FromResult($"Hi! {online} of your {host.Accounts.Count} accounts are online.");
        });

        return Task.CompletedTask;
    }
}
```

**4. Build it and drop it in.**

```
dotnet build -c Release
copy bin\Release\net10.0\HelloPlugin.dll C:\path\to\nocat.farm\plugins\
```

**5. Turn plugins on.** In the dashboard go to **Settings → Show advanced → Dashboard** and switch on
**Load plugins** (or type `set PluginsEnabled true`), then restart nocat.farm.

**6. Try it.** Type `plugins` to see it loaded, then `hello`:

```
> hello
Hi! 2 of your 3 accounts are online.
```

The next time a card drops, the log says `main got a card! 3 left in that game.` That's the whole loop:
**write → build → copy → restart.**

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
farmer: 5 cards
main: 1 card
```

Both examples on this page are built and tested against nocat.farm as it is.

---

## What a plugin can do

| | |
|---|---|
| **Watch** | Know when an account signs in or out, when a card drops, when trade offers are waiting. |
| **Read** | Every account: online or not, what it's playing, cards left, its games and playtime, what its items are worth, any of its settings. |
| **Do** | Run any of the app's commands - the same ones you type, like `pause main` or `grind main 730 2`. |
| **Add** | New commands, and settings that get real controls on the dashboard's Plugins page. |
| **Remember** | Save its own data so it survives restarts and updates. |

What it **can't** do: get at the Steam connection, the login tokens or the config files directly. Anything that
changes something goes through a command, so it's checked and logged exactly like a command you typed.

---

## Cheat sheet

Everything your plugin gets is on `host`, the `IPluginHost` handed to `OnLoadAsync`:

| On `host` | What it's for |
|---|---|
| `Accounts` · `Account("main")` | Every account, or one by name. Each has `Name`, `SteamId`, `IsOnline`, `Persona`, `Status`, `Playing`, `CardsRemaining`, `Library` and `InventoryByGame`. |
| `Log("text")` | Write a line to the log, tagged with your plugin's name. |
| `RunCommandAsync("pause main")` | Run a command, and get back what it would have printed. |
| `AddCommand(verb, usage, help, handler)` | Add a command. It appears in `help` and works in the console, the dashboard and Steam chat. |
| `AddSetting(new PluginSetting(...))` · `Setting("name")` | Declare a setting (Text, Int, Bool or Choice) and read its current value, always as text. |
| `GetSetting("main", "FarmCards")` | Read one of an account's own settings. To change one, run a `set` command. |
| `SaveStateAsync(json)` · `LoadStateAsync()` | Keep your own data between restarts (saved as `config/plugins/<YourPlugin>.json`). |
| `AppVersion` | Which nocat.farm version you're running on. |

| Events | When they fire |
|---|---|
| `AccountOnline` · `AccountOffline` | An account finished signing in, or went offline. |
| `CardDropped` | A card dropped: the account, the game's appID, and how many cards that game has left. |
| `TradeOffersWaiting` | Steam says trade offers are waiting on an account, and how many. |

Subscribe to events in `OnLoadAsync` - it runs **before any account signs in**, so you won't miss the first ones.
Handlers run on Steam's threads: keep them quick, and start a task if you need to do something slow.

The full, commented contract is one file: [`src/NocatFarm/Plugins/IPlugin.cs`](src/NocatFarm/Plugins/IPlugin.cs).
All 61 commands are in [the full guide](docs/GUIDE.md#commands).

---

## Rules

- **Plugins live in `plugins/`**, next to `nocatFarm.exe` - top level only, no subfolders.
- **Plugins load once, when the app starts.** Changed or added one? Restart.
- **Each plugin has its own on/off switch** on the dashboard's Plugins page.
- **You can't take a command that already exists.** A plugin redefining `stop` would be a nasty surprise, so it's
  refused with a warning.
- **A broken plugin only breaks itself.** If it fails to load or throws, it's logged and switched off, and the rest
  of nocat.farm carries on - your farm doesn't stop at 3am because of one DLL.
- **Several plugins in one DLL** is fine - every `INocatPlugin` class in it loads.

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
