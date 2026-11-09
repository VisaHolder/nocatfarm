# Custom trade rules

Rules that decide incoming trade offers for one account, beyond the built-in fair-swap test. They are **off by default**, and they never override the safety checks below.

## What they can and cannot do

- **They never run** for your own accounts (`Trade masters`), pure donations, or a card swap nocat.lol paired. Those keep their existing behaviour.
- **A custom accept from a stranger waits for your confirmation by default.** The offer is accepted and waits in your Steam app like any other confirmation. Only your own accounts, the accounts you allowed to trade with, and nocat.lol swaps are confirmed automatically. The opt-in **Confirm custom accepts in the Steam app** (`TradeRulesConfirm`) changes that for an account that runs as an exchange or level-up bot - see below.
- **A hard gate always wins.** If the offer would give away an item that is kept, reserved for a badge, already promised in another live offer, or not tradable, the rules are not asked at all.
- **Missing or stale data never accepts or declines.** The offer is left for the next look and tried again.
- **A trade hold is checked after the waiting period**, like a fair swap. A held offer is left for you.
- Human mode still answers one offer at a time, and the offer still waits its usual delay.

## Where they live

Settings → the account → **Trades** → **Custom trade rules**. The switch is **Use custom trade rules**. The rules themselves are edited on the **Trade rules** page in the dashboard: presets, a rule list, a JSON editor, import/export, copying to another account, and a preview that explains a verdict without acting on it.

Turn the switch on only after the preview says what you expect. With the switch off, the stored rules are ignored and nothing changes.

## Running as an exchange or level-up bot

Two settings turn an account into a shop, both off by default:

- **Confirm custom accepts in the Steam app** (`TradeRulesConfirm`, Trades → Custom trade rules) confirms an offer a rule accepted without asking you. This is what lets the account complete trades while you are away - and it is also how one bad rule becomes a scam. Only turn it on once the preview looks right, keep the rules narrow, and keep the account's authenticator in nocat.farm.
- The preset gallery includes **Key/gem exchange: at least a tenth more in gems** (a `value-ratio` on gems - keys and gems both carry a GooValue), **Level-up: sell a complete set for gems** (a `set-size-gems` price table, per complete set), and **Cards for their gem worth**. Edit the factor and the table to your own rate card.

An exchange bot still refuses what the rules do not clearly accept: missing or stale prices, untradable items and anything on a keep list or promised elsewhere leave the offer alone. Turn on **Decline everything else** (Trades → Offers from others) so an offer that matches no rule is declined instead of sitting there.

## The shape

```json
{
  "version": 1,
  "rules": [
    {
      "id": "cards-worth",
      "kind": "card-gems",
      "action": "accept",
      "enabled": true,
      "priority": 100,
      "note": "free text for the page",
      "when": { "partners": { "allow": [], "deny": [] } },
      "match": { "kinds": ["Card"], "apps": [730], "foil": false },
      "payIn": { "kinds": ["GemSack", "Gems"], "only": true }
    }
  ]
}
```

- `version` must be `1`.
- `id` is unique. A missing id becomes `rule-1`, `rule-2`…; a duplicate is skipped with a problem.
- `kind` is one of the seven below. An unknown kind is skipped with a problem.
- `action` is `accept` (default) or `decline`. A `decline` rule is a shape/partner veto only; it has no value conditions.
- `enabled` defaults to `true`.
- `priority` defaults to `100`; lower runs first. Deny wins regardless of priority.
- Limits: 100 rules, 64 KB of JSON, 100 table entries, 256 hashes or names per `match`.

### Partners

```json
{ "allow": ["friend", "76561198000000000"], "deny": ["scammer"] }
```

Names are matched case-insensitively; a number is a SteamID64. Empty `allow` means anyone. `deny` always wins.

### Match

```json
{
  "kinds": ["Card", "FoilCard", "Background", "Emoticon", "BoosterPack", "GemSack", "Gems"],
  "apps": [730],
  "hashes": ["730-Shattered Web"],
  "names": ["Shattered Web"],
  "foil": false,
  "minItems": 1,
  "maxItems": 100
}
```

- `apps` matches the **underlying game** (`market_fee_app`), not the inventory app (753).
- `hashes` and `names` are exact and combine with AND.
- `foil` applies to cards only.
- `minItems` / `maxItems` count the items on the target side.
- The target side must consist **entirely** of matching items. Mixed offers are left to other rules or to `Decline everything else`; a rule never quietly gives away something it did not match.

### Payment

`payIn` applies to gem-priced rules:

```json
{ "kinds": ["GemSack", "Gems"], "only": true }
```

With `only: true`, every item on the payment side must match. The default accepts any item with a gem value. Loose gems are tradable at 1 gem each; a sack is 1,000 gems and is the only form marketable on Steam.

## The seven kinds

### `card-gems` — cards for their gem worth

```json
{
  "id": "cards-worth",
  "kind": "card-gems",
  "worth": "gems",
  "fallback": "none",
  "sameGame": true,
  "marginPct": 0,
  "minMarginGems": 0,
  "match": { "kinds": ["Card"], "foil": false },
  "payIn": { "kinds": ["GemSack", "Gems"], "only": true }
}
```

- `worth`: `gems` (Steam's GooValue) or `wallet` (market value after Steam's fee, converted through a sack).
- `fallback`: `none`, `gems` or `wallet`; must differ from `worth`. Used only when the primary value is missing.
- Required payment = `ceil(sum(card worth) * (1 + marginPct / 100) + minMarginGems)`.
- `sameGame: true` requires every given card to share a game.
- The giving side must be entirely matching cards, or the rule abstains (leaves the offer alone).
- A missing value means Unknown, not zero.

### `set-size-gems` — a price table by card-set size

```json
{
  "id": "set-size-table",
  "kind": "set-size-gems",
  "per": "card",
  "table": { "5": 15, "8": 20, "10": 25, "15": 35 },
  "defaultPerCard": null,
  "onUnknownSet": "unknown",
  "match": { "kinds": ["Card"], "foil": false }
}
```

- `per: "card"` (default): required = the table's value for each given card, summed.
- `per: "completeSet"`: the offer must contain a complete set (`count == set size`) for each represented set; required += the table's value for that size. An incomplete set abstains.
- `defaultPerCard` is used when the set size isn't in the table; `null` means no default.
- `onUnknownSet`: `unknown` (leave the offer) or `abstain` (ignore this rule). Default `unknown`.

### `item-threshold` — backgrounds, emoticons, boosters

Sell:

```json
{
  "id": "sell-backgrounds",
  "kind": "item-threshold",
  "target": "giving",
  "mode": "sell",
  "basis": "gems",
  "minEach": 100,
  "minTotal": 0,
  "match": { "kinds": ["Background"] }
}
```

Buy:

```json
{
  "id": "buy-backgrounds",
  "kind": "item-threshold",
  "target": "receiving",
  "mode": "buy",
  "basis": "gems",
  "maxEach": 100,
  "maxTotal": 0,
  "match": { "kinds": ["Background"] }
}
```

- `target` must agree with `mode`: sell → `giving`, buy → `receiving`.
- `basis`: `gems` or `wallet`.
- Sell: required = `minEach * count + minTotal`; accept when the payment side is worth at least that.
- Buy: allowed = `maxEach * count + maxTotal`; accept when the payment side is worth at most that.
- `count` is the number of matching target items; the target side must be entirely matching.

### `booster-value` — expected, floor, set-size or fixed

```json
{
  "id": "sell-boosters-expected",
  "kind": "booster-value",
  "target": "giving",
  "mode": "sell",
  "value": "expected",
  "valueIn": "gems",
  "marginPct": 10,
  "minMargin": 5,
  "minSales": 3,
  "maxAgeHours": 24,
  "undercutPct": 0,
  "floorMode": "threeCheapestDistinct",
  "match": { "kinds": ["BoosterPack"] }
}
```

- `value`: `expected` (three random cards; 1% foil chance each), `floor` (`threeCheapestDistinct` = the three cheapest cards, or `threeTimesCheapest` = 3 × the cheapest), `setSize` (`6000 / set size` gems), or `fixed` (`gems`).
- `valueIn`: `gems` or `wallet`. `setSize` and `fixed` are gem-based; `expected` and `floor` can be either.
- Sell: payment ≥ `value * (1 + marginPct / 100) + minMargin`.
- Buy: payment ≤ `value * (1 - marginPct / 100) - minMargin`.
- `minMargin` uses the same unit as `valueIn`.
- `minSales`, `maxAgeHours` and `undercutPct` apply to expected/floor.
- An unknown set is Unknown unless the value is `fixed`. An `expected` of 0 because quotes are missing is Unknown, not a free booster.

### `foil-swap` — normals for foils

```json
{
  "id": "normals-for-foils",
  "kind": "foil-swap",
  "give": { "count": 3, "foil": false, "match": { "apps": [730] } },
  "get": { "count": 1, "foil": true, "match": { "apps": [730] } },
  "sameGame": true,
  "mode": "exact",
  "maxLossPct": 0
}
```

- `give` is what you give; `get` is what you receive. Both are cards/foils only.
- `count` is a number of items, not a sum of amounts.
- `mode: "exact"` (default) requires exactly those counts and no other items. `mode: "atLeast"` allows extras, but every item on each side must still match that side's filter.
- `sameGame: true` requires every given and received card to share a game.
- `maxLossPct`: 0–100; default `0` means the received foils must be worth at least the given normals at market net. `null` skips the value check.
- If a value is missing or stale and `maxLossPct` is set, the rule is Unknown.
- A `decline` version of this rule is a shape veto: it matches and declines without a value check.

### `market-profit` — net Steam Community Market profit

```json
{
  "id": "market-profit",
  "kind": "market-profit",
  "minProfitCents": 1,
  "minProfitPct": 0,
  "minSales": 3,
  "maxAgeHours": 24,
  "priceMode": "conservative",
  "undercutPct": 0,
  "looseGems": "sack"
}
```

- `profit = receiveNet - giveNet`, both after Steam's fee and undercut.
- Accept when `profit >= minProfitCents` and `profit >= giveNet * minProfitPct / 100` (when the percentage is above 0).
- `priceMode`: `conservative` (default; lower net for receiving, higher net for giving), `lowest`, or `median`.
- `minSales` and `maxAgeHours` apply to every marketable item on both sides.
- Any unpriced, stale or non-marketable item is Unknown. Loose gems are not listed themselves; by default (`looseGems: "sack"`) they are valued at their share of what a sack of 1,000 sells for, after Steam's market fee (`gem count × sack net / 1,000`), assuming you pack them. `"unknown"` leaves them unpriced.
- `giveNet == 0` is a pure donation, not this rule.

### `value-ratio` — the catch-all

```json
{
  "id": "ratio",
  "kind": "value-ratio",
  "basis": "wallet",
  "left": "receiving",
  "op": ">=",
  "right": "giving",
  "factor": 1.1,
  "offset": 0,
  "minSales": 3,
  "maxAgeHours": 24,
  "priceMode": "conservative",
  "undercutPct": 0
}
```

- `basis`: `gems`, `wallet`, `cardCount`, `foilCount`, `setSize` or `itemCount`.
- `left` / `right`: `receiving` or `giving`.
- `op`: `>=`, `<=`, `>`, `<`, `==`.
- Computes `leftValue OP rightValue * factor + offset`.
- Count bases fetch no prices. Gem/wallet bases use the same pricing rules as above.

### `expression` — anything else

The seven kinds above cover the common cases; an `expression` rule covers the rest. `expr` is a small formula that must be true for the offer to match. There is no scripting language and no access to files or Steam: only the numbers below, arithmetic, comparisons and `&&` / `||`.

```json
{
  "id": "gems-margin",
  "kind": "expression",
  "action": "accept",
  "expr": "receiving.gems >= giving.gems * 1.15",
  "looseGems": "sack",
  "minSales": 3,
  "maxAgeHours": 24,
  "undercutPct": 0
}
```

- `expr` (also accepted as `expression`) is required. An empty or invalid formula is a problem and the rule is not loaded.
- `action` may be `decline`: then a true `expr` declines, and a false one abstains.
- The wallet options (`minSales`, `maxAgeHours`, `priceMode`, `undercutPct`, `looseGems`) are the same as for `market-profit`. Loose gems default to their share of a sack's net.

**Values.** Each side's total, after Steam's fee where it is a wallet value:

| Name | Meaning |
|---|---|
| `giving.gems` / `receiving.gems` | total gem value (loose gems at the sack share) |
| `giving.wallet` / `receiving.wallet` | total market net, after Steam's fee |
| `giving.count` / `receiving.count` | items, longer `amount` counted |
| `giving.cards` / `receiving.cards` | normal cards |
| `giving.foils` / `receiving.foils` | foil cards |
| `giving.boosters` / `receiving.boosters` | booster packs |
| `giving.loosegems` / `receiving.loosegems` | loose gems |
| `giving.sacks` / `receiving.sacks` | sacks of gems |
| `giving.games` / `receiving.games` | different games |
| `giving.sets` / `receiving.sets` | complete card sets held |
| `giving.priced` / `receiving.priced` | 1 when every value on that side is known |
| `profit.gems` / `profit.wallet` | receiving minus giving |

**Functions.**

| Call | Result |
|---|---|
| `count(side)` / `count(side, "Card")` / `count(side, "Card", 730)` | items, optionally of one kind and game |
| `has(...)` | 1 when that count is above zero |
| `sum(side, "gems")` / `sum(side, "wallet", "Card", 730)` | the total of one value |
| `min` / `max` / `avg` (same arguments) | across the matching items |
| `sameGame()` | 1 when every item comes from one game |

Kinds are the inventory's own names: `Card`, `FoilCard`, `Background`, `Emoticon`, `BoosterPack`, `Gems`, `GemSack`, `Other`.

**Unknown is not zero.** If the formula uses a value that isn't known (no price, too few sales, too old), the whole condition is Unknown: the offer is left for the next look, never accepted or declined. A count, by contrast, needs no price and always works.

Examples:

```json
{ "kind": "expression", "expr": "receiving.gems >= giving.gems * 1.15" }
{ "kind": "expression", "expr": "count(\"giving\", \"Card\") == 3 && count(\"receiving\", \"FoilCard\") == 1 && receiving.wallet >= giving.wallet" }
{ "kind": "expression", "expr": "sameGame() && receiving.wallet >= giving.wallet + 5" }
{ "kind": "expression", "action": "decline", "expr": "count(\"giving\", \"FoilCard\") > 0" }
```

The presets `expression-gems` and `expression-foil-swap` are these first two, ready to edit.

## Ordering and verdicts

1. Hard gates run first. Blocked or Unknown leaves the offer for you (a rule never declines it).
2. `decline` rules run next; the first matching one wins.
3. `accept` rules run in priority order; the first Accept wins and its id is logged.
4. If a rule matched but could not value the offer, the result is **Unknown**: the offer is left for the next look.
5. If no rule applies, the result is **Abstain**: the account's ordinary behaviour continues (`Accept fair card swaps`, `Decline everything else`, …).

## The preview

On the Trade rules page, **Preview offers** reads the offers waiting now and shows, for each: what you give and get, each item's gems and wallet value, the final verdict and rule, why, and any gate reason. It performs no accept, decline or claim. Prices are read from what is already known; missing prices are shown as such.

## Steam requests

Valuation is lazy: a rule about counts reads nothing. At most 8 uncached Steam lookups and 10 seconds are spent on one offer. Cached values are free. When the budget runs out the remaining values are Unknown and the offer is left.

## A note on value bases

Steam's GooValue (gems) and the Community Market (wallet) are different bases. They are never mixed silently: a rule says `gems` or `wallet`, and any fallback is explicit. A loose gem is tradable but not marketable; only a sack of 1,000 is. Wallet rules therefore price loose gems at their share of the sack's net after Steam's market fee (`looseGems: "sack"`, the default), or leave them unpriced (`looseGems: "unknown"`).
