# Writing a nocat.farm plugin

A plugin is one DLL. You drop it in `plugins/`, turn plugins on, restart, and it's running.

There's no manifest, no registration, no build step beyond `dotnet build`. If you can write a C# class, you can
write a plugin.

**Contents** · [The 5-minute version](#the-5-minute-version) · [What a plugin can do](#what-a-plugin-can-do) ·
[Settings](#giving-your-plugin-settings) · [Saving state](#saving-state) · [Events](#events) ·
[Commands](#adding-commands) · [Doing things](#doing-things) · [Ideas](#things-worth-building) ·
[Rules & limits](#rules-and-limits) · [Shipping it](#shipping-it)

---

## The 5-minute version

**1. Make a class library**

```
dotnet new classlib -n MyPlugin
cd MyPlugin
```

**2. Point it at nocatFarm.dll** — edit `MyPlugin.csproj`:

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

`<Private>false</Private>` matters — it stops your build copying nocatFarm.dll next to your plugin, which would
give you a *second* copy of every type and make every cast fail in ways that look like sorcery.

**3. Write it**

```csharp
using NocatFarm.Plugins;

public sealed class MyPlugin : INocatPlugin {
    public string Name => "MyPlugin";
    public string Version => "1.0.0";

    public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
        host.Log($"up and running on nocat.farm {host.AppVersion}");

        host.CardDropped += (account, appId, left) =>
            host.Log($"{account.Name} got a card from {appId} — {left} to go");

        return Task.CompletedTask;
    }
}
```

**4. Install it**

```
dotnet build -c Release
copy bin\Release\net10.0\MyPlugin.dll  <nocat.farm folder>\plugins\
```

Then `set PluginsEnabled true`, restart, and type `plugins`.

```
1 plugin(s) loaded:
  MyPlugin                 1.0.0      MyPlugin.dll
```

That's the whole loop.

---

## What a plugin can do

| | |
|---|---|
| **Watch** | React to accounts coming online, cards dropping, trade offers arriving |
| **Read** | Every account's state, library, playtime, inventory value, settings |
| **Act** | Run any command the app has — the same ones you type |
| **Extend** | Add your own commands, and your own settings with a real UI |
| **Remember** | Save state that survives restarts and updates |

### What it deliberately can't do

A plugin **never gets the `Bot` object**, the Steam client, the web session, or the config object.

This isn't security theatre — a plugin runs in the same process, so a determined DLL can do whatever the app can,
including reading your Steam tokens off disk. Nothing in an API can stop that; only not running the plugin can.
What the narrow API *does* do is make the honest path the easy one. You get facts about accounts and a way to ask
for things. Anything that changes state goes through the command line, so it is validated the same way, logged
the same way, and can't reach a state you couldn't have reached by typing.

**Which is why the plugin switch says what it says.** Run plugins you wrote or whose author you trust.

---

## Giving your plugin settings

Declare them in `OnLoadAsync` and they appear on the **Plugins** page, under your plugin, with real controls.
No UI work, and the operator edits them where they edit everything else.

```csharp
public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
    host.AddSetting(new PluginSetting(
        "MinValue",
        "Only tell me about items over",
        "In cents. Anything cheaper is ignored.",
        PluginSettingKind.Int,
        Default: "50"));

    host.AddSetting(new PluginSetting(
        "Loud", "Log every single one", "Off keeps it to a daily summary.",
        PluginSettingKind.Bool, Default: "false"));

    host.AddSetting(new PluginSetting(
        "Mode", "What to do", "Pick one.",
        PluginSettingKind.Choice, Default: "watch",
        Choices: ["watch Watch only", "notify Notify me", "act Do something"]));

    return Task.CompletedTask;
}
```

Read them back whenever you need them — always current, never cached by you:

```csharp
int min = int.TryParse(host.Setting("MinValue"), out int v) ? v : 50;
bool loud = host.Setting("Loud") == "true";
```

Values are text, because that's what a form returns. You declared the kind, so you know what it should be — and
you still have to cope with someone typing nonsense into it.

Stored in `config/plugins/<YourPlugin>.settings.json`. Survives updates.

---

## Saving state

```csharp
await host.SaveStateAsync(JsonSerializer.Serialize(myThing));

string? json = await host.LoadStateAsync();
MyThing thing = json == null ? new MyThing() : JsonSerializer.Deserialize<MyThing>(json)!;
```

Goes to `config/plugins/<YourPlugin>.json`. Use this rather than writing files next to the app — it survives an
update, and it's obvious to whoever's running it what belongs to whom.

---

## Events

```csharp
host.AccountOnline      += account => { };                       // finished signing in
host.AccountOffline     += account => { };                       // dropped, deliberately or not
host.CardDropped        += (account, appId, cardsLeft) => { };   // a trading card dropped
host.TradeOffersWaiting += (account, count) => { };              // Steam says offers are waiting
```

Subscribe in `OnLoadAsync` — it runs **before any account signs in**, so you see the first one rather than
missing the whole fleet by a second.

`TradeOffersWaiting` gives you a **count, not an offer**. That's all Steam's push carries. If you want the detail,
react by going and looking.

Handlers fire on Steam callback threads. Throwing is caught and logged rather than taking anything down, but
don't block in one — start a task if you need to do something slow.

---

## Adding commands

```csharp
host.AddCommand("worth", "[account]", "What an account's inventory is worth.", args => {
    if (args.Length == 0) {
        decimal all = host.Accounts.Sum(a => a.InventoryByGame.Sum(g => g.Value));
        return Task.FromResult($"the lot: ${all:N2}");
    }

    IPluginAccount? one = host.Account(args[0]);
    return Task.FromResult(one == null
        ? "no such account"
        : $"{one.Name}: ${one.InventoryByGame.Sum(g => g.Value):N2}");
});
```

Shows up in `help` and works in the console, the dashboard and Steam chat like any built-in.

**You cannot take a verb that already exists.** Try it and the registration is refused with a warning — a plugin
quietly redefining `stop` would be the worst possible surprise.

---

## Doing things

Everything the app can do already has a command, so that's the door:

```csharp
await host.RunCommandAsync("pause kylro");
await host.RunCommandAsync("set kylro FarmCards false");
await host.RunCommandAsync("grind main 730 4");
await host.RunCommandAsync("send kylro main");
```

You get back exactly what would have been printed. Every call is validated and logged like a typed one.

`plugins`, `help` and the [README's command table](README.md#commands) are the full list — 44 of them.

---

## Things worth building

Ideas that fit this API well:

- **A Discord webhook** — post card drops, trade offers and problems to a channel
- **A better daily report** — your own format, your own schedule, your own numbers
- **Inventory watch** — tell you when an account's value moves more than X%
- **Auto-responder** — react to `TradeOffersWaiting` with your own rules on top of the built-in ones
- **A rotation manager** — `RunCommandAsync` to move accounts between grinding, farming and idling on your own logic
- **An exporter** — dump playtime, cards and value to CSV or a database on a timer
- **Anything with a schedule** — the API gives you the facts and the commands; the policy is yours

---

## Rules and limits

- **One folder, no recursion.** `plugins/*.dll`, top level only.
- **One class, one plugin.** Several `INocatPlugin` types in one DLL all load.
- **Plugins load once, at startup.** There's no hot reload — a plugin wires itself up as the app starts, so
  toggling one takes a restart. The Plugins page says so.
- **Individually switchable.** The toggle on the Plugins page disables one plugin without turning the feature off.
- **A broken plugin costs you the plugin.** Failure to load, failure to construct, a throw in `OnLoadAsync` or in
  a handler — each is caught and logged, and everything else carries on. A farm shouldn't stop at 3am because
  somebody's DLL threw.
- **Built against a different version?** You'll get a plain warning saying so rather than a wall of loader errors.
  The API is young; expect it to move.

---

## Shipping it

Ship the one DLL. Don't ship `nocatFarm.dll` with it — `<Private>false</Private>` keeps it out.

If your plugin has NuGet dependencies, ship those DLLs alongside it; each plugin is loaded into its own context,
so two plugins can use different versions of the same library without a fight.

Tell people what it does, what settings it has, and — since they're being asked to run your code inside the
process holding their Steam sessions — why they should trust it.

---

## Not supported: ASF plugins

An ArchiSteamFarm plugin can't run here, and it isn't close.

They're compiled against `ArchiSteamFarm.dll` and implement *ASF's* `IPlugin`, taking ASF's `Bot` type, its
config model, its DI container and its specific SteamKit build. None of those types exist in nocat.farm. Running
one would mean shipping ASF and reimplementing enough of its internals to satisfy whatever the plugin reaches
for — which is "embed ASF inside nocat.farm", and any plugin doing something interesting would break anyway.

**Port it instead.** The ASF plugins worth having are a few hundred lines, and against this API they usually come
out simpler than the original.
