# nocat.farm plugins

A plugin is one DLL that adds your own commands and features to nocat.farm. You write a small C# class, build it,
drop the DLL in the `plugins` folder, and it runs.

You need to be able to write a little C#. If you just want to use nocat.farm, you can skip this page.

**Contents** · [Your first plugin](#your-first-plugin) · [A bigger example](#a-bigger-example-card-tally) ·
[What a plugin can do](#what-a-plugin-can-do) · [Cheat sheet](#cheat-sheet) · [Profile comments](#posting-a-profile-comment) ·
[Trade offers](#answering-trade-offers) · [A page of your own](#a-dashboard-tab-of-your-own) · [Rules](#rules) ·
[Is it safe?](#is-it-safe) · [Sharing it](#sharing-your-plugin) · [ASF plugins](#not-supported-asf-plugins)

---

## Your first plugin

This one adds a `hello` command and writes a line in the log whenever a card drops. It takes about five minutes.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everywhere below, replace
`C:\path\to\nocat.farm` with the folder your `nocatFarm.exe` is in.

### Step 1: make a project

```
dotnet new classlib -n HelloPlugin
cd HelloPlugin
```

### Step 2: point it at nocat.farm

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

### Step 3: write the plugin

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

### Step 4: build it and copy it in

```
dotnet build -c Release
copy bin\Release\net10.0\HelloPlugin.dll C:\path\to\nocat.farm\plugins\
```

(Make the `plugins` folder next to `nocatFarm.exe` if it isn't there yet.)

### Step 5: turn plugins on

Plugins are off until you switch them on. In the dashboard, go to **Settings**, tick **Show advanced**, and under
**Updates & plugins** switch on **Load plugins**. Or type `set PluginsEnabled true`. Then restart nocat.farm.

### Step 6: try it

Type `plugins`. It lists your plugin and the `hello` command. Then type `hello`:

```
> hello
Hi! 2 of your 3 accounts are online.
```

The next time a card drops, the log shows `[plugin] farm1 got a card! 3 left in that game.`

From here it's the same loop every time: write, build, copy, restart.

---

## A bigger example: card tally

This one counts every card each account drops and remembers the count across restarts. It also has a setting on
the dashboard and adds a `tally` command.

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

Both examples on this page build against the current nocat.farm.

---

## What a plugin can do

| | |
|---|---|
| **Watch** | Know when an account signs in or goes offline, when a card drops, and when trade offers are waiting. |
| **Read** | Every account: online or not, what it's playing, cards left, its games and playtime, what its items are worth, and any of its settings. |
| **Do** | Run any command, the same ones you type, like `pause farm1` or `grind farm1 730 2`. |
| **Add** | New commands, settings that get real controls on the dashboard's Plugins page (one value, or one per account), and a dashboard tab of its own. |
| **Comment** | Post a Steam profile comment as an account - with nocat.farm deciding when (see [below](#posting-a-profile-comment)). |
| **Trade** | Accept or decline incoming trade offers nocat.farm leaves undecided - with nocat.farm's safety rules on top, and only where the owner allows it (see [below](#answering-trade-offers)). |
| **Remember** | Save its own data so it survives restarts and updates. |

What it can't do through the API is reach the Steam connection, the login tokens or the config files. Anything
that changes something goes through a command, so it's checked and logged exactly like a command you typed. The two
exceptions are a profile comment and an answer to a trade offer, and nocat.farm keeps the say over both.

---

## Cheat sheet

Everything your plugin gets is on `host`, the `IPluginHost` passed to `OnLoadAsync`.

| On `host` | What it's for |
|---|---|
| `Accounts` · `Account("farm1")` | Every account, or one by the name you gave it in nocat.farm (`null` if there's no such account). Each has `Name`, `SteamId`, `IsOnline`, `Persona`, `Status`, `Playing`, `CardsRemaining` (-1 until it has looked), `Library` and `InventoryByGame`. |
| `Log("text")` | Write a line to the log. It shows up tagged `[plugin]`. |
| `RunCommandAsync("pause farm1")` | Run a command and get back the text it would have printed. |
| `AddCommand(verb, usage, help, handler)` | Add a command. It works in the console, the dashboard and Steam chat, and is listed by `plugins` and on the dashboard's Plugins page (not by `help`). |
| `AddSetting(new PluginSetting(...))` · `Setting("name")` | Declare a setting and read its current value (always as text, so parse it yourself). Kinds: `Text`, `Int`, `Bool`, `Choice`, `Secret`. For `Choice`, pass `Choices` as one `"value label"` string per option. A `Secret` (an API key, say) is kept encrypted on disk like the app's own and never sent back to the dashboard; `Setting` gives it to you as plain text. `PerAccount: true` gives it one value per account, shown as a table. |
| `GetSetting("farm1", "FarmCards")` | Read one of an account's own settings, by its setting name. `null` if the account or setting doesn't exist. To change one, run a `set` command. |
| `SaveStateAsync(json)` · `LoadStateAsync()` | Keep your own data between restarts. `LoadStateAsync` returns `null` the first time. |
| `AppVersion` | Which nocat.farm version is running. |
| `Language` | The app's language code (`en`, `de`, `pt-BR`, ...), for a plugin that carries its own translations. |
| `Setting("name", "farm1")` · `SetSetting(name, value, account)` | A per-account setting (declared with `PerAccount: true`): read one account's value, or change one of your own settings from a command or a page button. |
| `CanComment("farm1")` · `PostProfileCommentAsync("farm1", steamId64, text)` | Whether the app would let that account post a profile comment now (and why not, and until when), and post one. See [Posting a profile comment](#posting-a-profile-comment). |
| `AddPage(title, render)` | A dashboard tab of your own. See [A dashboard tab of your own](#a-dashboard-tab-of-your-own). |
| `Notify("text", "farm1")` | Send a line out on Telegram, Discord and the tray, the way the app's own news goes, when *Send add-on messages* is on. It also goes in the log. |
| `AddTradeJudge((offer, ct) => ...)` | Be asked about incoming trade offers nocat.farm leaves undecided, and answer Accept, Decline or Leave. See [Answering trade offers](#answering-trade-offers). |
| `KnownMarketPrice(appId, name)` · `MarketPriceAsync(appId, name)` | A Steam market price: the lowest listing, in the app's market currency, and when it was read. `KnownMarketPrice` only gives what nocat.farm already holds. `MarketPriceAsync` asks the market when the held one is old, through nocat.farm's own spaced-out queue, at most 60 times an hour per plugin. |

| Event | When it fires |
|---|---|
| `AccountOnline` · `AccountOffline` | An account finished signing in, or went offline. |
| `CardDropped` | A card dropped: the account, the game's appID, and how many cards that game has left. |
| `TradeOffersWaiting` | Steam says trade offers are waiting on an account, and how many. Only the count; to look at the offers themselves, use `AddTradeJudge`. |

`INocatPlugin` has one optional extra: `OnUnloadAsync()`, called when nocat.farm closes. It gets 5 seconds.

**Where things are saved** (in the `config/plugins/` folder, named after your plugin's `Name`):

- `<Name>.json`: what you save with `SaveStateAsync`
- `<Name>.settings.json`: the values of your settings, as changed on the Plugins page

The full, commented contract is one file: [`src/NocatFarm/Plugins/IPlugin.cs`](src/NocatFarm/Plugins/IPlugin.cs).
Every built-in command is in [the command list](docs/COMMANDS.md).

---

## Posting a profile comment

`host.PostProfileCommentAsync(account, targetSteamId64, text)` posts one comment on somebody's Steam profile as one of
your accounts. It exists for the [rep4rep add-on](docs/rep4rep.md). A plugin can only ask; nocat.farm decides, and
these rules hold whatever the plugin does:

- **Never on an account that's standing still**: offline, paused, switched off or signing off.
- **Human mode**: only while the account is up and settled in, the same as everything else that shows on an account:
  not asleep, not on a break it spends offline, not while you're playing on it, and not straight after it signs in or
  wakes up. *Only react while awake* and the account's other human-mode settings apply.
- **Spacing**: at least 35 minutes between two comments from a human-mode account, and at least 2 minutes otherwise.
- **A daily ceiling**: at most 25 comments per account in any rolling 24 hours, counted together for every plugin and
  saved to disk, so a restart doesn't reset it. If that count can't be read, no comments go out until it's fixed.
- **One at a time per account**, and the profile is looked at first. If there's no comment box for that account
  (private, or friends only), nothing is sent.
- Only an individual's SteamID64 that isn't the account's own, and 1 to 1000 characters of text.

When nocat.farm says no, nothing is sent or counted and you get `PluginCommentOutcome.NotNow`, with `Detail` saying why
in the app's language and `RetryAt` saying when it expects to say yes, if it knows. `CanComment(account)` asks the same
question without posting. The other outcomes are what Steam said: `Posted`, `CommentsClosed`, `NotSent` (it never left
this PC), `Unknown` (sent, no answer - count it and never send the same comment again), `Refused`, `RateLimited`,
`DailyLimit` and `AccountBlocked`.

Keep your own limits on top: the rep4rep add-on also has its own per-account cap, gaps and hours.

## Answering trade offers

`host.AddTradeJudge(...)` lets your plugin answer incoming trade offers: custom trade rules, a gem exchange, a level-up
bot. nocat.farm asks your judge about each offer it would otherwise leave for the owner, and your judge answers
`Accept`, `Decline` or `Leave` (no opinion), with a short reason that goes in the log.

This one accepts offers that only take normal trading cards and only pay gems, at a price per card set on the Plugins
page, and declines the ones that pay too little:

```csharp
using NocatFarm.Plugins;

public sealed class GemsForCards : INocatPlugin {
    public string Name => "GemsForCards";
    public string Version => "1.0.0";

    public Task OnLoadAsync(IPluginHost host, CancellationToken ct) {
        host.AddSetting(new PluginSetting(
            "Gems", "Gems per card", "Accept offers that only take normal cards and only pay gems, at least this many a card.",
            PluginSettingKind.Int, Default: "15"));

        host.AddTradeJudge((offer, token) => {
            // Not the kind of offer this plugin knows about: no opinion, nocat.farm does what it would anyway.
            if (offer.Giving.Count == 0 || offer.Giving.Any(i => i.Kind != PluginTradeItemKind.Card)
                || offer.Receiving.Count == 0 || offer.Receiving.Any(i => i.Kind != PluginTradeItemKind.Gems)) {
                return Task.FromResult(PluginTradeAnswer.Leave());
            }

            long cards = offer.Giving.Sum(i => (long) i.Amount);
            long gems = offer.Receiving.Sum(i => (long) i.Amount);
            int each = int.TryParse(host.Setting("Gems"), out int n) && n > 0 ? n : 15;

            return Task.FromResult(gems >= cards * each
                ? PluginTradeAnswer.Accept($"{gems} gems for {cards} card(s)")
                : PluginTradeAnswer.Decline($"{gems} gems for {cards} card(s) is under {each} a card"));
        });

        return Task.CompletedTask;
    }
}
```

The `offer` your judge gets has the account's name, the offer's id, who sent it (`PartnerSteamId`), their note, when it
was sent, the trade hold if Steam already said, and the items on both sides: `Giving` is what the account would hand
over, `Receiving` what it would get. Each item has its ids, amount, name, market name, Steam's type line, a simple
`Kind` (`Card`, `FoilCard`, `Background`, `Emoticon`, `BoosterPack`, `Gems`, `GemSack` or `Other`), whether Steam says it's
`Tradable` and `Marketable`, and for Steam community items the game it belongs to (`GameAppId`). It's what nocat.farm
already read from Steam; nothing extra is asked for your plugin. For prices, use
`host.MarketPriceAsync(753, item.MarketHashName, token)` instead of asking the market yourself.

### What nocat.farm does whatever your judge says

- **Off until the owner turns it on**, per account: *Let add-ons answer trade offers* (Settings, Trades, Show advanced).
  While it's off, your judge isn't even asked.
- **Only offers nocat.farm leaves undecided.** Never offers from the owner's own accounts, donations, card swaps
  gamer.mom paired, or fair card swaps nocat.farm accepts by itself.
- **An Accept is refused** when the offer would give away anything on the owner's "never touch" lists or from a game
  whose cards were bought for a badge, anything promised to another offer that's still waiting, or anything Steam
  doesn't call tradable, or when Steam would hold the trade. When nocat.farm can't tell, it doesn't accept. A refused
  offer is left for the owner, with the reason in the log, and your judge isn't asked about it again.
- **Nothing happens at once.** The offer waits like every other one (2 to 15 minutes by default), and your judge is
  asked again right before nocat.farm answers it. If the answer changed, the offer is left alone.
- **No confirming for strangers.** When Steam wants an accepted trade confirmed and the offer is from somebody who
  isn't one of the owner's accounts, it waits for the owner's phone, unless the owner also turned on *Confirm add-on
  accepts from strangers*.
- **A judge that throws, takes longer than 30 seconds or hands back nothing has said Leave**, and the offer is left
  alone that time round, never declined. The same goes when two plugins disagree (one says Accept, another Decline).
- Every accept and decline is logged with your plugin's name and your reason, like
  `offer 4 accepted for add-on GemsForCards · 60 gems for 4 card(s)`.

## A dashboard tab of your own

`host.AddPage("My tab", view => ...)` gives your plugin a tab next to Plugins. The function returns a `PluginPage`: a
list of blocks the dashboard draws itself. It's text only, so a plugin can't put script or markup on the page:

```csharp
host.AddPage("Tally", view => Task.FromResult(new PluginPage([
    PluginBlock.Heading("Cards counted"),
    PluginBlock.Tiles(("12", "today"), ("80", "this week")),
    PluginBlock.Table(["Account", "Cards"], [new PluginRow(["farm1", "7"], new PluginButton("Reset", "tally reset farm1"))]),
    PluginBlock.ButtonRow(new PluginButton("Details", View: "details")),
    PluginBlock.Link("Read more", "https://example.com"),
])));
```

A `PluginButton` runs one of your own commands (its reply is shown on the tab), shows another view of the page
(`View`, handed back to your function), or both. Only your own: a button whose command starts with anything but a verb
your plugin added with `AddCommand` (`set`, `send`, another plugin's) is refused by the app and runs nothing - so text
you put on a page from somewhere else can't turn into a command. To run a built-in command from a button, add one of
your own that does it with `RunCommandAsync`. Links only open `https://` addresses. The tab is asked for again each
time it's shown, refreshed or a button is pressed.

## Rules

Plugins live in the `plugins` folder next to `nocatFarm.exe`, or inside the `--path` folder if you use one. Only the
top level is loaded, so DLLs in subfolders are ignored, but your plugin's own dependencies can sit next to it.
Several plugins in one DLL is fine; every `INocatPlugin` class in it loads.

Plugins load once, when the app starts, before any account signs in. Subscribe to events in `OnLoadAsync` and you
won't miss the first ones. If you add or change a plugin, restart.

`OnLoadAsync` gets 15 seconds. Start-up waits for it, so a plugin that takes longer is left out and logged. Don't
wait on slow network calls there; start a task instead. Event handlers run one after another, in the order things
happened, on a thread of your plugin's own: a slow one only holds up your plugin's next event, and one stuck for good
stops your plugin hearing new ones (after 1000 waiting, they're dropped). Keep them quick anyway.

Every call into your plugin is bounded: a command gets 3 minutes to answer (then the command line is handed back,
and your command may carry on), a page 30 seconds to draw, `OnUnloadAsync` 5 seconds. Code that blocks before its
first `await` counts too - none of it runs on the caller's thread.

There are limits on what one plugin adds and writes: 50 commands (one word each, letters, digits and dashes, up to 32
long), 200 settings, 300 log lines and notifications a minute (the rest of that minute's are dropped, said once), and
lines up to 2000 characters. A page shows up to 200 blocks, 2000 rows a table and 20 cells a row, each text up to 4000
characters. A command's answer is cut at 64 KB. A page or an event handler that keeps failing is written to the log 5
times an hour.

A few more things worth knowing:

- You can't take a command that already exists. A plugin that tries to add `stop`, or any built-in name or alias,
  gets a warning in the log and its version is ignored.
- A broken plugin only breaks itself. One that fails to load is left out with the reason in the log, and an event
  handler that throws is logged under your plugin's name and skipped while the plugin stays loaded. That holds for an
  `async` handler too (`host.CardDropped += async (a, app, left) => { await ...; }`): what it throws after an
  `await` is caught and logged the same way instead of taking the app down.
- Two copies of the same plugin (the same `Name`, say a second copy of its DLL) run once: the first found stays.
- Some failures end any .NET program, and nocat.farm can't catch them for you: an exception thrown on a `Thread` or
  in a `Timer` callback you started yourself, an `async void` you started from your own background task that throws,
  a real stack overflow, or `Environment.Exit`. Catch everything in your own threads and timers.
- Each plugin has its own on/off switch on the dashboard's Plugins page.
- If the app can't read your DLL, the log says it was probably built against a different version. Rebuild against
  the current `nocatFarm.dll`.

## Is it safe?

A plugin runs inside nocat.farm, where your Steam sessions are. The API is kept narrow on purpose, but a determined
DLL could still read anything the app can. So only run plugins you wrote yourself or got from someone you trust.
That's why plugins are off until you switch them on.

## Sharing your plugin

Ship just your DLL, plus any NuGet libraries it uses. Never ship `nocatFarm.dll` with it; `<Private>false</Private>`
keeps it out. Each plugin loads on its own, so two plugins can use different versions of the same library.

Tell people what it does, what settings it has, and why they can trust it. They're running your code next to their
Steam accounts.

Licence it however you like, closed-source or commercial included. nocat.farm is [MPL-2.0](LICENSE), which only asks
you to share changes to nocat.farm's own files, and your plugin is your own files.

## Not supported: ASF plugins

ArchiSteamFarm plugins can't run in nocat.farm. They're built against ASF's own code (its `Bot` type, its config and
its internals), and none of that exists here. Porting is usually easy, though. Most ASF plugins are a few hundred
lines, and against this API they tend to come out shorter.
