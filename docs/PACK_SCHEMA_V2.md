# Pack schema v2

**Status:** Draft. The owner accepted the direction on 2026-10-25; the field-level
details are for review with the data project.
**Decisions:** [ADR 0001](adr/0001-pack-schema-v2.md) (this schema) and
[ADR 0002](adr/0002-retire-go-compatibility.md) (no Go compatibility; Division 2
only).
**Audience:** the data project, which produces packs, and this repository, which
consumes them.

This document defines the game data the C# planner loads. It replaces the
Go-era format (v1, `schemaVersion: 1`). The active Division 2 delivery is produced
by ShadeTech; its exact pack bytes live in `data/packs/division-2/` and its assembly
pins, reports, notices and evidence in `data/deliveries/division-2/`. The frozen
converter output remains in `data/references/division-2-v1-conversion/` for
read-only migration tests. The [v1 → v2 mapping](#v1--v2-mapping) specifies that
converter, independently of current producer delivery.

The JSON Schema (draft 2020-12) will be published from this repository as
`schemas/pack-v2/*.schema.json`. Where the prose and the schema disagree, the
schema is authoritative.

## 1. Delivery

A pack is a directory:

```text
division-2/
  manifest.json        identity, versions, capabilities, file hashes
  rules.json           everything the calculation engine reads
  simulation.json      simulator rules
  presentation.json    layout, theme, artwork registry
  share-codes.json     tables for decoding legacy (v1–v12) share codes
  provenance.md        source evidence, as today
  assets/              artwork files listed in presentation.json
```

### manifest.json

```json
{
  "schemaVersion": 2,
  "id": "division_2",
  "name": "The Division 2",
  "version": "y8s3.2-pve.2026-10-25.shields-swaps.1",
  "gameVersion": "Year 8 Season 3 – Title Update Y8S3.2 (PvE)",
  "description": "…",
  "capabilities": ["weapons", "prototype", "skill-mods", "party", "talent-copy", "simulation"],
  "files": {
    "rules.json": { "sha256": "…", "bytes": 0 },
    "simulation.json": { "sha256": "…", "bytes": 0 },
    "presentation.json": { "sha256": "…", "bytes": 0 },
    "share-codes.json": { "sha256": "…", "bytes": 0 },
    "provenance.md": { "sha256": "…", "bytes": 0 }
  },
  "assetsSha256": "…"
}
```

- **`capabilities`:** the app rejects a pack that declares a capability it doesn't
  implement. It never loads such a pack with that part ignored. Add capabilities
  only by agreement.
- **`files`:** the app verifies these hashes at load, and packaging verifies them
  again.
- **`assetsSha256`:** the directory digest: SHA-256 of the lines
  `<sha256>  <path>\n`, sorted by path (ordinal).
- **Versioning:** `version` changes with every data delivery. `schemaVersion`
  changes only when this specification changes incompatibly.

## 2. General rules

JSON readers accept at most 64 levels of nesting; deeper input is rejected as
`json-syntax`.

These apply to every file.

- **Encoding:**
  - UTF-8 without a byte-order mark, and only valid UTF-8.
  - Line endings are LF.
  - Pretty-printing is optional. Key order within objects carries no meaning.
- **Strict JSON:**
  - Property names are case-sensitive and must match the schema exactly.
  - Unknown properties are errors.
  - The same property must not appear twice in one object.
  - `null` is never valid. Omit an optional property instead; each optional
    property has a stated default.
- **Ids:**
  - Must match `\A[a-z][a-z0-9_]*\z`.
  - Must be unique within their collection, and stable across deliveries:
    saved builds and share codes refer to them.
  - **Every id that exists in the v1 pack keeps its value in v2.**
  - A renamed or removed id needs an entry in `rules.json` `renamedIds` (see
    below) so that old builds can be migrated.
- **Order:** arrays are kept in the order authored. Order has meaning in these
  places, and the engine follows it:
  - the order in which effects are applied
  - set bonuses
  - talent slots
  - preview outputs
  - prerequisite tie-breaking
  - display order in the app

  Don't re-sort arrays that are already ordered. A deliberate reorder is a data
  change and can change results.
- **Numbers:**
  - Must be finite.
  - Fields specified as integers must be whole numbers no larger than
    9,007,199,254,740,991 (2^53 − 1).
  - Ratios use fractions, so 15% is `0.15`.
- **Text:** `name` values are plain, non-empty text. `description` values are
  plain text and may be empty.
- **Expressions:**
  - Written as strings, in the existing expression language: arithmetic,
    comparisons, `&&`, `||`, `?:`, and the functions `min`, `max`, `floor`, `ceil`,
    `round`, `abs`, `sqrt`, `clamp`, `lerp`, `curve`, `has_tag`,
    `has_selected_tag`, `has_talent`, `context_value`.
  - The first argument of `curve`, `has_tag`, `has_selected_tag`, `has_talent` and
    `context_value` **must be a string literal**. This is new in v2; v1 checked it
    only for `has_talent` and `context_value`.
  - Identifiers refer to stat ids.

## 3. Modifiers

A modifier is the common shape for any effect on a stat.

```json
{ "stat": "weapon_damage", "op": "flat", "value": 0.15 }
{ "stat": "total_weapon_damage", "op": "flat", "value": "0.01 + 0.01 * min(max(skill_tier, 0), 6)",
  "requires": { "empathic_resolve_self_repair": "true" } }
```

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `stat` | stat id | required | The stat being modified |
| `op` | `flat` \| `mult` \| `override` | required | `flat` adds. `mult` multiplies by (1 + value). `override` replaces the stat's value; if several overrides apply, the last one applied wins. |
| `value` | number or expression | required | The amount. An expression is a non-empty string. |
| `when` | expression | none | Applies only when the expression is true. It runs before any stat is computed, so it may use functions (`has_tag`, `has_talent`, `context_value`, …) but no stat identifiers. |
| `requires` | object mapping a selector id to a value | none | Applies only while every listed [selector](#selectors) currently has the listed value |
| `whileHolstered` | boolean | `false` | Applies only while the item's weapon is *not* the active weapon. Only valid on talents in weapon slots. |

The `inc` operation and stat buckets (`bucket`) are **removed**: Division 2 never
used them. Both can be reintroduced as a capability if needed.

Upgrades that apply per rank (account upgrades and item upgrades) must use
numeric values. They must not use `when` or `override`.

## 4. rules.json

```json
{
  "level": 40,
  "stats": [ … ],
  "curves": { … },
  "selectors": [ … ],
  "numericInputs": [ … ],
  "buildOutputs": [ … ],
  "attributes": [ … ],
  "templates": { "attributeSlots": { … }, "modSlots": { … }, "talentSlots": { … }, "upgrades": { … } },
  "slots": [ … ],
  "equipmentLimits": [ … ],
  "items": [ … ],
  "mods": [ … ],
  "talents": [ … ],
  "sets": [ … ],
  "prototype": { … },
  "accountUpgrades": [ … ],
  "skillSlots": [ … ],
  "skills": [ … ],
  "specializations": [ … ],
  "renamedIds": { }
}
```

### level

`level` is the character level, used as `level` in skill-preview expressions.
Division 2 is fixed at 40. Character progression, point allocation, primary
stats and item level requirements are **removed**.

### stats

```json
{ "id": "weapon_damage", "name": "Weapon Damage", "format": "percent", "formula": "0",
  "shortName": "WD", "description": "", "min": 0, "max": 10 }
```

- Every stat is computed from its `formula`. The v1 field `kind` is gone: Division
  2 had only `derived` stats.
- `format` is `integer`, `decimal` or `percent`. It is required.
- `min` and `max` are optional bounds, applied after all modifiers.
- Formulas must not depend on each other in a cycle.

### curves

```json
"curves": { "skill_assault_turret_damage_tiers": [[0, 0], [1, 0.1], [6, 0.6]] }
```

Each curve is at least one `[x, y]` point, sorted so that `x` never decreases.
Between points the value is interpolated linearly; outside the range it is
clamped to the end values.

### selectors

New in v2. In v1, the context keys used in gates were never declared; there are
111 of them besides `weapon_slot`. Declaring them makes it possible to:

- reject a misspelled key in a modifier gate,
- build the Assumptions tab directly from the pack,
- show only the selectors that an equipped source actually reads (F46).

```json
{ "id": "overwatch_active", "name": "Overwatch active", "values": ["true"],
  "section": "Talents and gear", "description": "…" }
{ "id": "headshot_mode", "name": "Hit location", "values": ["body", "headshot"], "section": "Weapon" }
```

- A selector with one value works as an on/off toggle: on means it has that value,
  off means it has no value.
- A selector with several values is a choice.
- `weapon_slot` is **not** declared here. The active weapon is built into the
  engine; see [items](#items).

### numericInputs

These are the v1 `numericContextInputs`, unchanged apart from the name:
`id`, `name`, `description`, `min`, `max`, `step`, `default`, `global`, `options`,
`group`, `groupName` and `section`. `section` refers to an assumption section in
`presentation.json`.

### buildOutputs

Unchanged from v1: `id`, `name`, `description`, `format`, `formula`. Formulas can
also use the values `<skillSlot>_dps` and `<skillSlot>_shield`.

### attributes

```json
{ "id": "assault_rifle_damage_weapon_core", "name": "Assault Rifle Damage (Offensive)",
  "stat": "assault_rifle_damage", "min": 0.005, "max": 0.15, "step": 0.001,
  "format": "percent", "category": "offensive", "core": true,
  "prototype": { "max": 0.225 }, "countStat": "…" }
```

- An attribute always adds its rolled value to its stat. The v1 `op` field is
  removed; it was always `flat`.
- `step` is required.
- `prototype` is optional and holds `max` (required within it) and `min`
  (default: the attribute's `max`).

### templates

These shared definitions replace the slot definitions that v1 repeated on every
item. Division 2 has 57 distinct attribute-slot definitions, 108 mod-slot
definitions, 258 talent-slot definitions and 8 upgrade definitions, spread over
1,022 items.

```json
"templates": {
  "attributeSlots": {
    "gear_standard": [
      { "id": "core_1", "name": "Core 1", "choices": [
        { "attribute": "armor_gear_core" }, { "attribute": "weapon_damage_gear_core" }, { "attribute": "skill_tier_gear_core" } ] },
      { "id": "minor_1", "name": "Minor 1", "choices": [ … ] }
    ]
  },
  "modSlots":    { "gear_one_mod": [ { "id": "mod_1", "name": "Mod", "choices": ["critical_hit_chance_gear_mod", "…"] } ] },
  "talentSlots": { "chest_talents": [ { "id": "talent", "name": "Talent", "choices": ["…"] } ] },
  "upgrades":    { "gear_expertise": { "name": "Expertise", "description": "…", "maxRank": 30,
                   "modifiersPerRank": [ { "stat": "gear_base_armor", "op": "flat", "value": 800 } ] } }
}
```

- An attribute choice can override the attribute's range for that slot with
  `min`, `max` and `step`, as v1's `ItemAttributeChoice` could.
- From 2026-11-02 an attribute choice may name a `countStat`, a declared stat that a
  valid configured roll of that choice adds 1 to, beside the attribute's own
  `countStat`. Because it sits on the slot template, it counts only the items using that
  template (R38: Slotted's colour counts of the Investor mask's own bonus attributes,
  with one stat per colour on its minor slots' choices). Expertise and mod rolls don't
  count; the writer omits it when empty.
- Template names are ids, but they're internal to the pack and never saved in a
  build.

### slots

```json
{ "id": "primary_weapon", "name": "Primary Weapon", "accepts": ["weapon"], "weapon": true }
```

- `weapon: true` marks the slots that can hold the active weapon. In Division 2
  these are `primary_weapon`, `secondary_weapon` and `sidearm`.
- The `icon` moves to `presentation.json`.
- The order of the `slots` array is the order in which effects are applied.

### equipmentLimits (optional)

Omitting this array means no equipment-count limits. A nonempty array uses the
`equipment-limits` manifest capability. Each rule requires all five fields:

```json
{ "id": "exotic_gear", "name": "Exotic gear", "tag": "exotic",
  "slots": ["mask", "chest", "backpack", "gloves", "holster", "knees"],
  "maximum": 1 }
```

`id` is unique within this array; `name` is nonempty. `tag` names a tag present
on at least one item. `slots` is a nonempty, duplicate-free array of declared
slot IDs. `maximum` is an integer from zero through 2^53 − 1. Every correctly
placed item with that exact tag in those exact slots consumes one allowance.
Rules apply independently, in authored order; weapon slots count only when
explicitly listed. Tag matching is ordinal and case-sensitive.

Exceeding a rule emits an error `equipment-limit-exceeded` for each matching
equipped slot (`Subject`), with the rule ID as `Target`. The message names the
allowance, equipped count/items and number to remove. The resolver retains every
item: imports, saves and ordinary edits remain available. Comparisons, candidate
previews, upgrade ranking and simulation must not publish valid results for an
offending build. Repair edits and undo remain ordinary session operations.

The example is not itself game data. The bundled pack has two limits: one exotic
gear piece (`exotic_gear`) and one exotic weapon (`exotic_weapon`).

### items

Gear piece:

```json
{ "id": "mask_n_5_11_tactical_mask", "name": "5.11 Tactical Mask", "slot": "mask",
  "tags": ["gear", "armor", "brand_n_5_11_tactical"],
  "modifiers": [ { "stat": "gear_base_armor", "op": "flat", "value": 80000 } ],
  "attributeSlots": "gear_standard", "modSlots": "gear_one_mod", "talentSlots": "mask_none",
  "upgrade": "gear_expertise" }
```

Weapon:

```json
{ "id": "primary_weapon_acr", "name": "ACR", "slot": "primary_weapon",
  "tags": ["weapon", "weapon_acr", "weapon_type_assault_rifle"],
  "weapon": { "weapon_base_damage": 58896.5, "weapon_rpm": 650, "weapon_magazine_size": 30,
              "weapon_reload_time": 1.91, "weapon_optimal_range": 31, "weapon_long_range": 105,
              "weapon_max_range": 105, "weapon_base_headshot_damage": 0.55 },
  "modifiers": [],
  "attributeSlots": "assault_rifle", "modSlots": "rifle_mods", "talentSlots": "weapon_standard",
  "upgrade": "weapon_expertise" }
```

- **`slot`** replaces v1's `occupies`. Every v1 item occupied exactly one slot.
- **`weapon`** is a block of the weapon's base stats, keyed by stat id. It
  replaces the roughly ten context-gated `override` modifiers per weapon in v1
  (5,197 in total). The **active weapon** is a built-in engine concept, set by the
  `weapon_slot` assumption:
  - When a weapon is active, its base stats override those stats, and its own
    `modifiers` and `upgrade` apply. This replaces v1's `effectsRequireContext`
    and the matching `requiresContext` on every weapon modifier.
  - When a weapon is not active (holstered), only talents marked `whileHolstered`
    apply, and its tags, set membership and talent selections still count. This
    is the same as v1.
  - The planner reports the weapon's base stats in the calculation breakdown
    exactly as v1 reported its overrides: the same item, the same order, before
    the item's other modifiers. This keeps existing results identical.
- **Template references:** `attributeSlots`, `modSlots` and `talentSlots` name a
  template and default to none. `upgrade` names an upgrade template and also
  defaults to none.
- **`tags`** are free-form, but some are meaningful: `gear`, `armor`, `weapon`,
  `named`, `exotic`, `weapon_type_*` and `brand_*`. Slot `accepts`, talent
  copying and Prototype exclusions match on them.
- `artwork` moves to `presentation.json`.
- The v1 fields `tier`, `requirements`, `occupies` and `effectsRequireContext`
  are removed.

### mods, talents and sets

These are unchanged from v1 except for the [modifier](#3-modifiers) changes:

- `mods`: `id`, `name`, `description`, `modifiers`, `rollAttribute` (renamed from
  `rollAttributeId`).
- `talents`: `id`, `name`, `description`, `modifiers`, `modeledBySets`,
  `setPieceCredit`, `copiesTalent` (`from`, `to`, `sameTagPrefix`, `excludeTags`,
  `excludeTalents`), `allyEffects` (`id`, `stack`, `name`, `description`,
  `modifiers`). `icon` moves to presentation.
- Ally-effect modifiers must be `flat`, with no `when` and no `requires`; this is
  unchanged. Their expressions are now checked like every other expression.
- `coverage` (optional, R35): the producer's source audit of how much of the
  talent's mechanic the pack models. A pack with any entry uses the
  `talent-coverage` manifest capability.

  ```json
  "coverage": { "status": "partial", "omits": ["per-ally 45 s cooldown"],
                "evidence": "T7_EXOTICS_SOURCE_REVIEW.md, Tardigrade" }
  ```

  - `status` is `complete`, `partial`, `utility` (nothing to model for damage,
    armor or skill stats), `unmodeled` or `unknown`. It describes this pack's
    model, not the game.
  - `evidence` is required and nonempty: a review file and section, or
    "description only".
  - `omits` (nonempty phrases) is required for `partial`, `unmodeled` and
    `unknown`, and absent otherwise.
  - `settleBy` names the reading that would decide an `unknown`; it is required
    there and absent otherwise.

  Coverage is metadata: it never changes a calculation. Effect coverage reports
  it as a `source-audit` channel. An audit alone doesn't count as a modeled
  channel, and comparisons list the partial, unmodeled and unknown talents.
- `sets`: `id`, `name`, `description`, `items`, `bonuses` (`pieces`, `modifiers`).

### prototype, accountUpgrades, skills and specializations

These are unchanged from v1 apart from the renames listed here and the modifier
rules above.

- `prototype`: `slots`, `excludeTags`, `countStat`, `normalizeInput` (a numeric
  input id), `augments` (`id`, `name`, `description`, `stat`, `base`, `perLevel`,
  `levels`).
- `accountUpgrades`: `id`, `name`, `description`, `maxRank`, `effectCap`,
  `modifiersPerRank`.
- `skillSlots`: renamed from `activeSkillSlots`. Entries have `id`, `name` and
  `choices`.
- `skills`: renamed from `activeSkills`. Entries have `id`, `name`, `description`,
  `family`, `modifiers`, `upgrade`, `modSlots` and `previewOutputs`.
  - `previewOutputs` entries have `id`, `name`, `description`, `format`,
    `formula`, `when`, `kind` and `headline`.
  - `kind` is one of `damage`, `rate`, `shield` or `regeneration`, or is omitted.
  - `headline` can be set only on a `rate` output, and at most once per skill.
  - Skill-mod choices have `id`, `name`, `description`, `input`, `min`, `max`,
    `step`, `format` and `statName`.
- `specializations`: `id`, `name`, `description`, `modifiers`, `budgets`, `nodes`.
  `icon` and `image` move to presentation.
- The v1 passive skill tree (`skills` in v1, which was empty) and `scenarios`
  (also empty) are **removed**. The name `skills` now refers to active skills.

### renamedIds

```json
"renamedIds": { "item": { "old_item_id": "new_item_id" }, "talent": { } }
```

This lets saved builds and share codes from older deliveries keep working. Keep
an entry for as long as old builds might still be loaded. An id may be renamed
only to an id that exists in the same collection.

## 5. simulation.json

```json
{ "triggers": [ … ], "skills": [ … ], "statusDamage": [ … ] }
```

- `triggers` are v1's `simTriggers`. Every field is kept. `kind` is restricted to
  `buff`, `stacks`, `status`, `magazine`, `distance`, `pellets`, `ready`,
  `lockout`, `cycle`, `target` and `count`. Event names are restricted to the simulator's
  event list.
- `alsoResetOn` is new on 2026-11-04 (R55), optional, for `buff` and `stacks` rules with a `resetOn`: a second event that
  resets the rule the same way. `within` also works on `status` rules with an event: the status lands only on an enemy that close,
  and a fight with no distance takes the range as met, and says so.
- The event `armor_kit` is new on 2026-11-04 (ADR 0010, stage 3, R45). It fires when a kit's repair lands, so not on a use
  cancelled by going down. Gear and the weapon in hand gain on it, and what it clears clears on every talent, holstered
  weapons too. With no `armorKits` it never fires: a rule gaining only on it steps aside, so its talent
  reads the user's setting, and the result names the talents. A talent on gear doesn't refuse fights without kits.
- `cooldownFromEnd` (a boolean) is new on 2026-11-06, on a `buff` with `every` and `consumeOn`, and no duration, `resetOn`, `cancelOn` or `consumeOn: target_death` (R61). `every` then counts
  from the buff being spent, not from the gain, and the rule can't regain while the buff is up.
- A status fixes its tick amount and duration factor from the state before the shot that starts it, so it reads a bonus its own application spends, and a bonus that same bullet earned waits for the next status, unspent. A refresh
  extends it by that factor, and keeps its amount and cadence (R61). `armorBonus` is read at each tick, as ADR 0011 has it. `status_applied` doesn't fire for a status landing on the bullet that kills.
- The event `status_applied` fires when a status goes on a target that didn't have it. Refreshing a running status extends it and doesn't fire this (R59), so a shot that keeps a bleed up fires it once.
- `everyGroup` is new on 2026-11-07, on `buff`, `stacks` and `ready` rules with an `every` above 0. Rules naming the same group share their cooldown: one fires only if no rule in its group fired within the `every`, and when two answer one event, the first in the pack fires. A group has rules from two or more talents in the pack, all with the same `every`, and none with `cooldownFromEnd`. A fight holds only the build's rules, so it may hold one of a group, which keeps its own `every`.
- `whileStatAtLeast` is new on 2026-11-06, on `buff` and `ready` rules with an event and no other gate: `{ "stat": "skill_tier", "value": 6 }`. The rule fires only while that stat is at the value or above, resolved as the build stood before the event, so stacks count but the event's own don't, nor those of the bullet's other events. A rule on a swap reads the weapon it belongs to as held. A closed rule neither counts nor fires, and its `every` doesn't start. A rule with this on a selected talent is checked against a shield before the fight, as a set talent's is, unless the stat can't reach the value with every other rule in the fight at its height.
- `impactInterval` is new on 2026-11-06, on a `simulation.skills` rule with `impacts`. It names the output with the seconds between a use's impacts, which must be above zero. Each then lands on its own, with `impact` alone, on the enemy there at that moment, as its own `skill_hit`, and one due with no enemy there is lost. Those due after the fight ends don't land. A use with a cooldown from its end isn't over until its last impact. Without it a use's impacts land together, as their total, as it deploys.
- `statusRate`, `statusWindow` and `statusArmorBonus` are new on 2026-11-06, on a `simulation.skills` rule with a burn or bleed. `statusRate` names the output with the status's own damage per second, and is the only way a rule says it has that damage: since R68's rows, a `rate`'s name no longer does. `statusWindow` names the output with how long the status lasts after its last application; the two go together. `statusArmorBonus` is `rateArmorBonus` for the status's damage. With neither `rate` nor `window` the status goes on once, as the use deploys, and the rule needs only a `cooldown`. With a `window` it applies through that window, so it runs from the first application to the end of the window plus `statusWindow`, and `rateRead` stays on `rate` only. With spaced impacts each one puts it on, so it applies until the last impact if that's later, including to an enemy that arrived between them. There's one per enemy: it keeps the amount it first went on with, two uses don't add, and while a talent's runs it deals nothing and waits for that to end, which counts as a new application. After a use stops applying, it runs on only on an enemy it landed on. Its ticks are no `skill_hit`, as a talent's aren't.
- `stock` and `stockInterval` are new on 2026-11-06, on a `simulation.skills` rule with a `cooldown` that doesn't run from a use's end. `stock` names the output counting the charges a full skill holds, a whole number, and `stockInterval` is the least seconds between uses. The skill starts full, and one charge comes back each cooldown while any are missing. A skill keeps the stock it first deployed with for the whole fight.
- A skill rule's `status`, when it's `burning` or `bleeding` on a rule with a `rate`, is on each enemy it lands on from the first batch there to the end of the use's window, even if the player moves off (R61). It's the same status a talent applies: it fires `status_applied` going on, it reads
  as on for keys, `whileStatus` and counts, and a talent's status on that enemy is no new application. It starts no ticks and it isn't in target status uptime. A rate only carries the status, as Stinger Hive's stings do, and a talent's ticks run beside it. A status's own damage is its `statusRate`, above. Control skills deal no batches, so theirs aren't tracked.
- The event `target_changed` is new on 2026-11-04 (ADR 0011). It fires when the player moves off a
  living enemy, with `focusSeconds`, while still on that enemy. Gear and the weapon in hand gain on it, and
  what it clears clears on every talent, holstered weapons too. A rule may reset or consume on it in
  any fight, and it just never fires; gaining on it needs `focusSeconds`.
- `count` is new on 2026-11-04 (ADR 0008, R46). It writes how many enemies are alive in the group
  to its key, and takes only `talent`, `key`, `on`, `within`, `status`, and `max` or `atLeast`:
  - On a numeric input (min 0, whole steps, holding `max`) it writes the count, up to `max`. On a
    true-only selector it needs `atLeast`, and is true at that many or more.
  - `within` limits it to enemies that close, in metres, so the fight needs a distance.
  - With `on` it reads on that event and holds that until the event comes round, starting from none
    (`reload_end` for a magazine, `second` for a one-second check). Any event but `target_death`, and the
    fight must have it. A reading on a killing blow sees the group after the kill. Without `on` it
    follows the group. Under rotation only the weapon in hand's talent reads, and a talent on both
    weapons shares one reading.
  - `status` counts only enemies with that status (2026-11-04, ADR 0011), and needs `on`. An enemy you've
    left counts, and without `focusSeconds` the only one that can have it is the enemy shot.
  - Its key has no other rule, except the same rule for another talent that can't be worn with it (a fight
    with `enemiesAtOnce` refuses a build with both), or a `status` rule on that key
    reading the same status, from a talent with this count. With `enemiesAtOnce` the count takes over from that rule, and other
    fights keep it.
  - It only runs with `enemiesAtOnce`. In any other fight the key stays the user's assumption, so
    a rule changes no results there.
- Six trigger fields are new in v2 (2026-10-30), all optional; the writer omits
  them when unset:
  - `cancelOn` (an event) and `cancelFor` (seconds, default 0), on `buff` rules
    only. When `cancelOn` fires while the buff is active, it ends and can't be
    gained again for `cancelFor` seconds; `cancelFor` needs `cancelOn`. From
    2026-11-01 a `swap`, `swapped` or `swap_after_kill` used only as `cancelOn`,
    `resetOn` or `consumeOn` never fires in a single-weapon simulation, instead of
    refusing it (for example Frenzy's removal on weapon switch); a rule that gains
    on one still needs weapon rotation.
  - `bank` (a numeric-input key), on `stacks` rules with a `pool` only. When the
    pool reaches `poolMax`, the rule's live stacks are written to `bank`,
    replacing its previous value, and the pool clears. The input must be whole
    numbers from 0 and hold the rule's `max`. Formulas read it like any other input.
  - `clearGrant`, `clearAmount` and `clearDuration`, on `stacks` rules with
    `resetOn` or `consumeOn` only; a rule with `count` needs `consumeOn`, since its
    reset only restarts the count. When that event clears live stacks, the rule
    grants `clearAmount` (above 0, up to 1) per stack removed: `temporary_armor`
    is that share of max armor as bonus armor for `clearDuration` seconds (above
    0, up to 600); `shield_repair` is that share of the shield's health and takes
    no duration.
- `weaponTags` (a list of item tags) is new on 2026-11-01, optional, on `buff`,
  `stacks`, `ready` and `lockout` rules. The rule then reacts to a weapon event
  (`shot`, `hit`, `crit`, `headshot`, `body_hit`, `unarmored_hit`, `kill`,
  `crit_kill`, `headshot_kill`, `reload`, `swap`, `swapped`, `swap_after_kill`,
  `status_applied`) only when the weapon producing it carries one of the tags; that
  covers gains, `also`, `resetOn`, `consumeOn` and `cancelOn`. A weapon event with no
  weapon behind it (a skill or status kill, a status applied on a second) doesn't
  reach the rule. Other events (`second`, `damaged`, `armor_break`,
  `shield_break`, `pickup`, `skill_hit`) are unaffected. Tags must be distinct and
  carried by some item in a weapon slot, for example `weapon_type_pistol` (R36:
  Resolved, Quick Draw, Concussion's marksman-rifle duration).
- `forceHeadshot` (boolean) is new on 2026-11-01, optional, on `ready` rules whose
  `on` is `hit` or `shot` only. While the rule is ready for the weapon in hand (its
  talent reacts and any `weaponTags` match), that weapon's next landed hit is a
  headshot: the first landed pellet uses headshot or critical headshot damage and
  fires `headshot` (and `headshot_kill` if it kills). The same shot's `hit` or `shot`
  event then spends the rule; with `shot`, a full miss spends it too. A natural
  headshot that is a hit spends it as well (R36: Resolved).
- `grant`, `grantAmount` and `grantDuration` are new on 2026-11-01, optional, on
  `buff` and `ready` rules only. Each time the rule fires (not while a ready rule is
  on cooldown, nor when `every`, a count or a lockout holds it back) it grants, in
  `clearGrant`'s vocabulary, `temporary_armor` (`grantAmount` above 0 up to 1 of max
  armor for `grantDuration` seconds, above 0 up to 600) or `shield_repair` (that share
  of the shield's health, no duration). R36: Ablative Nano-Plating's self part.
- Two more `grant` kinds and `grantInstances` are new on 2026-11-02 (R41), optional:
  - `armor_repair`: `grantAmount` of max armor, capped at full armor (no bonus armor).
    Without `grantDuration` it lands at once (Doctor Home's kit). With one (up to 600 s)
    it is spread evenly over that time and needs `grantInstances: "independent"`: each
    firing starts its own repair beside any running one (Preservation). The repair is
    continuous; the source's 0.2 s ticks aren't modeled, so there is no period field.
  - `missing_armor_repair`: `grantAmount` of the armor missing when the rule fires, at
    once, with no duration (Clutch's 2.5%).
  - Both repair the player in fights with incoming fire, and nothing when the player is
    down. `grantInstances` takes no other value and goes only with a timed
    `armor_repair`.
- Two more `grant` kinds are new on 2026-11-08 (P13), optional, for skill cooldowns. Both are
  instant (no `grantDuration` or `grantInstances`) and act on every skill cooldown running
  at the event, the shield's included:
  - `skill_cooldown`: `grantAmount` (above 0, up to 1) of each skill's own cooldown comes off
    what's left of it; 1 is a reset (Skilled; Calculated's 10%).
  - `skill_cooldown_seconds`: `grantAmount` seconds (above 0, up to 600) come off (Tag Team's 6 s).
  - A cooldown never ends before the event, and one that runs from a use's end
    (`cooldownFromEnd`) never ends before that use does. A share is of the cooldown alone, not
    the use. A stocked skill's next charge comes back sooner, and only that one. A skill not yet
    used, one ready now, one used at this very moment and a shield that broke at it are untouched,
    so a rule on a skill's own impact or a shield's break can't redeploy or raise it at once.
- `grantHoldDelay` is new on 2026-11-08 (P13: Skilled's banked reset), optional, on a `skill_cooldown`
  or `skill_cooldown_seconds` grant: seconds above 0, up to 60. When the rule fires with no skill
  cooldown running, the shield's included, the grant isn't applied but held, and it lands this many
  seconds after a skill cooldown next starts: a turret's or hive's as its use ends, any skill's as it's
  used, or the shield's as it breaks. With a cooldown running it applies at once, as without the field.
  Each distinct grant is held once; firing again while the same grant is held adds nothing. A cooldown that starts at
  the very moment of the event isn't running yet, so a use whose own impact kills holds the reset and
  takes it that delay later. Without the field, 0, nothing is held.
- `also` goes on a counted `status` rule from 2026-11-08 (R85: Ignited), optional: each listed event adds one
  to the rule's one count, beside its `on`, so weapon and skill kills count toward the same fourth. Each entry
  names another distinct simulator event, not `second`, `target_death` or `status_applied` (a rule would count
  its own burns), with no `count`, and `gain` left out or 1. The rule needs a `count` on an event other than
  `second`, and no `applyOnHit`: the status still arms the next hit. A gear talent's count can't name both
  `kill` and `skill_kill`, since a skill's kill is already a kill to gear talents; a fight refuses it.
- Two trigger fields are new on 2026-11-08 (P13: Efficient, R82), optional:
  - `activeAfter`, seconds above 0 up to 600, on a `buff` or `ready` rule with an event: the rule takes
    no part until that many seconds into the fight, so an event before then neither counts nor fires,
    and draws no `chance` (Efficient's 10 s activation delay: a kit used before then gets no chance).
  - `grantAmountStat`, a declared stat, in place of `grantAmount` on any grant but `armor_kit_refund`:
    the amount is the stat's value at the fight's start, within the grant's own limits, or the fight is
    refused. With a stat formula such as `0.3 * (1 + kit_bonus)` this scales a specialization kit's grant
    (Gunner's bonus armor) by Efficient's factor. Kit buff durations already take `durationStat`, and
    their modifiers take formula values, so the same stat scales those.
- The `grant` kind `armor_kit_refund` is new on 2026-11-08 (P13: Efficient), optional, only on a rule
  `on` `armor_kit`, which fires as a kit's repair lands. The kit just spent comes back at once:
  `grantAmount` 1, with no `grantDuration` or `grantInstances`. With a `chance` the use is free that share
  of the time. It acts only in fights that carry kits; without them the rule steps aside, as any rule on
  `armor_kit` does, and the result names its talent.
- The event `skill_kill` and the field `chance` are new on 2026-11-08 (P13: Skilled), optional:
  - `skill_kill` fires when a skill's damage kills, its own burn or bleed included, to the gear
    talents a skill kill already reaches as `kill`. From 2026-11-08 (R84) it also reaches the talents of
    the weapon in hand, so a weapon talent whose kill rule takes kills from any source (Outsider, Ignited,
    Preservation) names `skill_kill` too. A holstered weapon's talents aren't active; during a swap the
    weapon being drawn is the one in hand, as for every event. A burn or bleed tick's kill is a `kill` to
    gear talents only, and no `skill_kill`, except a skill's own status ticks in its stream. `kill` is
    unchanged: a weapon talent's `kill` is still only its weapon's. In a fight without skills, from
    2026-11-08, `skill_kill` and `skill_hit` never come, and their rules run without them: a key they
    gain stays driven, off unless another event gains it, and never takes the user's setting. The result
    names the talents with a rule on them.
  - `chance` (above 0, up to 1; default 1) on a `buff` or `ready` rule with an event: an event that
    passes the rule's gates fires it with that chance, drawn from the fight's own seeded chance stream,
    so no other roll moves. A failed draw neither counts nor fires, and the rule's `every` doesn't start.
- `requires` is new on trigger rules on 2026-11-08 (P13: Calculated), optional, on a `buff` or `ready`
  rule with an event: selector values, as a modifier's `requires`. The rule takes part only in a fight
  whose context holds them. A cover cycle sets `in_cover` for the whole fight (ADR 0009), and otherwise
  the user's setting stands. A key some rule drives can't be required, since the fight decides once, at
  its start; use `whileKey` for those. A selected talent's required selectors count as read, so the
  Assumptions tab shows them.
- More R41 fields are new on 2026-11-02, optional, for Clutch:
  - `whileKey` and `whileFull` now also go on `buff` and `ready` rules with an event, and
    gate them like a status rule. The key must be another rule's, and is read as it stood
    before the event.
  - `whileArmorBelow` (a share above 0 up to 1) on `buff` and `ready` rules with an event:
    the rule fires only while the player's armor is strictly below that share of max
    armor, read at the event. Only fights with incoming fire track armor; elsewhere it is
    full, so the gate stays closed. With `whileKey`, **either** gate opens the rule (Clutch:
    low armor, or its post-kill flag). A closed rule neither counts nor fires.
  - `durationStat` (a stat ID) on `buff` and `ready` rules without a `duration`: the
    duration is that stat's value at the fight's start, above 0 up to 600 (Clutch's window,
    4 s plus 1 s per offensive core).
- `countWindow` (seconds) is new on 2026-11-02, optional, on `buff` and `stacks` rules
  with a `count` only. A partial count clears when no counted event arrives within that
  many seconds of the last one; an event exactly at the window's end starts a new count.
  Completing the count clears it as before, and events during the result count toward
  the next one (R38: Pummel's 20 s drop-off).
- `maxMagazine` (a share above 0 up to 1) is new on 2026-11-02, optional, on `stacks`
  rules only. The cap is that share of the owning weapon's magazine at the fight's
  start, rounded down, from 1 up to `max`, which stays required and bounds the key's
  numeric input. The talent must be on a weapon; a gear talent with it is refused when a
  driven run starts (R39: Ardent's heat meter, half the magazine).
- `whileKey` (a key) and `whileFull` (boolean) are new on 2026-11-02, optional, on
  `status` rules with an event only. `whileKey` names a `buff`, `ready` or `stacks`
  rule's key; the rule counts, arms or applies only while that key is active (a buff
  running, a ready rule ready, a stacks key above 0), or with `whileFull` only while the
  stacks key is at its cap. A bullet's events (`shot`, `hit`, kill and
  `status_applied` events) read the key as it stood when the bullet was fired, before
  that bullet's own gains; other events read it at the event. A key of an unselected
  talent is closed. R39: Ardent is a `stacks` rule on `shot` with `maxMagazine: 0.5`,
  `resetOn: reload_end` and the drain as `duration: 0.5`, `refresh`, `decay: 12`, and a
  `burning` rule on `hit` with `applyOnHit`, `whileKey` and `whileFull`.

- `applyOnHit` (boolean) is new on 2026-11-02, optional, on `status` rules whose `on` is
  `hit`, `crit`, `headshot`, `body_hit` or `unarmored_hit`. The status lands on the hit
  that triggers it, instead of arming so the next hit applies it (R38: Trauma's blind on
  the headshot and bleed on the body hit; Ignited, Sadist and Eyeless keep the next hit).
- `gainWhileHolstered` (seconds) is new on 2026-11-02, optional, on `stacks` rules with
  `weaponTags` only. While the weapon in hand carries none of the tags, the rule gains 1
  stack on every tick of a free-running clock with that period (ticks at whole multiples
  of it from the fight's start), up to `max`; the gain stops when a tagged weapon is in
  hand and the stacks stay until a clearing event. Such a rule needs no `on` event.
  `forceHeadshot` now also fits a `stacks` rule whose `consumeOn` is `hit` or `shot`: while
  it holds stacks, the next landed hit from a tagged weapon is a headshot, and the
  consuming event spends them (R38: Quick Draw, `gainWhileHolstered: 0.3`, `max: 100`,
  `consumeOn: shot`, `weaponTags: ["weapon_type_pistol"]`, `forceHeadshot: true`).
- `status` rules with an event take `every` (seconds, default 0) from 2026-11-01:
  after the rule arms or applies, its event is ignored for that long. A status rule
  on `second` applies its status to the current target at that second, since no
  shot carries it (Dragon's Glare's default burn every 35 s, R36); other events
  still arm the next hit. With no living target, for example between targets, the
  second applies nothing and the interval doesn't start. An observer (no event)
  takes no `every`, and `every` can't be negative.
- The event `reload_end` is new on 2026-11-01: a reload completing, for the reloaded
  weapon (so `weaponTags` read it). `reload` stays the reload's start. ShadeTech's
  event review maps On Empty, Frenzy and Overflowing to the source's reload-finished
  condition.
- The event `pickup` is new in v2 (2026-10-30): a kill's dropped item (Kill
  Confirmed's trophy) being collected. When and whether it's collected is a
  simulation assumption (`pickupDelay`, `pickupChance`), not pack data.
- `distanceDefaults` is new on 2026-11-02, optional, at the top of `simulation.json`
  (P12: the enemy hit rate by distance). It is a list of `{ "input", "bands" }`.
  - `input` names a numeric input, at most once, that no trigger uses as its key (fights
    drive those).
  - `bands` is a list of `{ "upTo", "value" }`. Every band but the last has an `upTo`
    above the one before (metres, up to 1000); the last has none and covers everything
    beyond. Each `value` is a legal value of the input: within its range and on its step.
  - In a fight with a set distance, an input the user hasn't set takes the value of the
    first band whose `upTo` reaches the distance (inclusive), or the last band's. A value
    the user set wins. Both cases are noted in the result, and coverage's input basis names
    the inputs that came from the distance.
  - Without a distance, or outside fights, the input keeps its ordinary default.
- The event `crit_pellet` is new on 2026-11-02 (R43). It fires once for each critical pellet
  that lands, right after the shot's single `crit`, with the same weapon, talent and
  weapon-tag reach; a status may apply on it (`applyOnHit`). Rules gated on armor read it
  between pellets, so a repair can close its own gate (Clutch on a shotgun).
- `simulation.armorKit` is new on 2026-11-03 (ADR 0010, R45), optional: `repair` (a share of max
  armor, above 0 and up to 1), `useSeconds`, `extendSeconds` (added by a hit during use, at most
  once per that long), `maxUseSeconds`, and the optional lists `replacedBySpecializations` and
  `replacedByTalents`. Times are up to 60 s, and the lists name real rows. A fight with
  `armorKits` is refused without this field, and for a build with a listed specialization or
  talent (selected in a slot, or a set talent at its top bonus).
- The event `hit_pellet` is new on 2026-11-03. It fires once for each pellet that lands on a
  living target, right after the shot's single `hit`, with the same reach as `crit_pellet`;
  a status may apply on it. A talent counting pellets, such as Afterburn on a shotgun, reads
  it instead of `hit`.
- From 2026-11-03 a rule may name a set talent that no item's talent slot offers, such as Apex
  Engine. A fight selects it, as a gear talent, once its set's top bonus applies; set-piece
  credit counts. A set talent an item offers is selected through that item, as before. Such a
  talent's own `modifiers` don't apply, since it isn't in the build, and `has_talent` doesn't
  see it: its effect goes on the set's top bonus row, reading the rule's key.
- The event `target_death` is new on 2026-11-02 (R41), for `buff`, `stacks`, `ready` and
  `lockout` rules only. It fires when the current target dies, however it died (weapon,
  skill, status or talent damage), after that death's kill events. It reaches every
  talent in the fight, the weapon not in hand included, and weapon tags don't filter it.
  A rule read through `whileKey` is read as it stood before the death. So Doctor Home is:
  - a mark: a `buff` on `hit` for 5 s with `consumeOn: "target_death"`, so it clears
    with its target;
  - a kit: a `buff` on `target_death` with `whileKey` on the mark and `grant:
    "armor_repair"` 0.5.

  A buff restarts its timer each time it fires, so the mark needs no `refresh`. ShadeTech
  proposed dropping the kit as a pickup; it stays instant here, as the planner's Doctor
  Home code has it, since pickup timing is a simulation assumption.
- `skills` are v1's `simSkills`. Every field is kept, and output names refer to
  preview output ids. From 2026-11-04 a rule may be an `impact` and a `cooldown` alone (R15), for
  a skill that hits once per use. A `rate` or `status` still needs a `window`, and a `window` needs
  one of them.
- The rules the v1 validator checked for each kind still apply, and the schema
  states them explicitly. For example, `stacks` needs a numeric-input key and
  `max ≥ 1`.
- `statusDamage` is new in v2 (2026-10-30) and optional; the writer omits it when
  empty. Each entry is the damage a target takes while it has a status:
  `{ "status": "burning", "tick": "<stat id>", "period": 0.5, "tickOnApply": false }`.
  - `status` is `burning` or `bleeding`, at most one entry each.
  - `tick` is a declared stat; its resolved value is the damage of one tick, so
    the pack's formula holds any level, group or Status Effects scaling.
  - `period` is the seconds between ticks, from 0.01 to 60.
  - `armorBonus` (new on 2026-11-04, R20, optional) is a stat added to the ticks' armor factor, read at
    each tick with the weapon in hand, so armor takes the bonus and health doesn't. Unset, ticks
    take no bonus. `simulation.skills` has `rateRead`, on a rule with a `rate`: `live` re-reads the rate at each batch, as a running
    skill that follows the player's state does, and unset holds what the use deployed with (ADR 0012). It also has the same as `rateArmorBonus`, on a rule with a `rate`; an
    impact takes none. A negative or non-finite value refuses the fight.
  - `tickOnApply` (default false) adds a tick at the moment of each new
    application; otherwise the first tick lands one period after it.
  - How long a status lasts still comes from the `status` trigger that applies it.
- `talentDamage` is new on 2026-11-02 and optional; the writer omits it when empty.
  Each entry is a talent's own damage over time, separate from the statuses' (R38:
  Toxic Delivery):
  `{ "talent": "<talent id>", "on": "status_applied", "total": "<stat id>", "duration": 10, "period": 0.5 }`.
  - `talent` is a declared talent, at most one entry each; `on` is a simulator event.
  - `total` is a declared stat; its resolved value when the debuff starts is the whole
    debuff's damage, split into equal ticks.
  - `period` is from 0.01 to 60 seconds and must divide `duration` (above 0, up to
    600) into whole ticks; the first tick lands one period after the start and the last
    at the end of the duration.
  - The event starts one debuff on the current living target. While it runs, the event
    neither restarts it nor starts a second one; a new target starts without it.
  - `maxStacks` (new on 2026-11-08, optional, 1 to 100, default 1; P13: Plague of the
    Outcasts) lets the event start another debuff while fewer than that many run on the
    target. Each runs its own schedule from its own start, with the total read when it
    starts; ties tick in the order they started. At the cap the event does nothing, and the
    running ones keep their schedules. A dead target's debuffs end with it. The writer
    omits the default.

## 6. presentation.json

This is read by the desktop app, never by the calculation engine.

```json
{
  "theme": { "variant": "dark", "colors": { "primary": "#FF8A24", "…": "…" } },
  "layout": { "slotGroups": [ … ], "headline": [ … ], "summary": [ … ],
              "assumptionSections": [ … ], "attributeCategories": [ … ], "rarities": [ … ],
              "prototype": { "name": "Prototype", "color": "#AD78F1" } },
  "icons": { "slots": { "mask": "slot_mask" }, "talents": { }, "skills": { }, "specializations": { }, "nodes": { } },
  "specializationImages": { "demolitionist": "…" },
  "itemArt": { "primary_weapon_acr": { "image": "…", "icon": "…" } },
  "artwork": { "sources": [ … ], "assets": [ … ] }
}
```

- The layout, theme and artwork registry keep their v1 structure, with two
  changes:
  - `weaponContextKey` is removed, because the active weapon is built in.
  - Colours must be `#RRGGBB` or `#RRGGBBAA`, with the pattern anchored at both
    ends.
- Icon and image references that v1 kept on the game objects are collected here,
  keyed by the object's id.
- `layout.prototype` is new on 2026-11-08 (R74), optional: Prototype quality's `name` and
  `color`, beside `rarities`. Prototype is a build choice, not an item tag, so it has no tags.
  The desktop shows a Prototype item in this colour in place of its rarity's; without it, in
  the app's own `#A35CFF`. The name must not be blank and the colour follows the rule above.

## 7. share-codes.json

Share codes v1–v12 store some values by **position** in the v1 pack's arrays. In
Division 2, the meaningful one is the equipment array, in v1 slot order. To decode
existing codes, the pack records the v1 order once:

```json
{ "legacyOrder": { "slots": ["mask", "chest", "backpack", "gloves", "holster", "knees",
                             "primary_weapon", "secondary_weapon", "sidearm"],
                   "stats": [], "scenarios": [], "passiveSkills": [] } }
```

- These lists never change, even if `rules.json` is later reordered.
- New share codes use a new version that refers to everything by id, so they
  don't depend on any order.

## 8. Validation

The app validates in two stages. Both report errors with the app's own codes and
wording, and the pack is rejected if either stage fails.

1. **Structure (JSON Schema).** This checks types, required properties, unknown
   properties, value lists, id patterns, number ranges and string formats. The
   data project should run the same schema before delivering.
2. **Meaning (in the app):**
   - every reference resolves: stats, attributes, mods, talents, items, sets,
     slots, templates, selectors, numeric inputs, curves, skills, budgets, nodes,
     preview outputs and asset ids
   - ids are unique
   - curves are sorted
   - there are no cycles in stat formulas, budget parents or node prerequisites
   - every expression parses, refers only to identifiers in its scope, and uses
     literal first arguments where required
   - weapon stats are applied only through `weapon` blocks (no `weapon_slot`
     gates remain in modifiers)
   - `whileHolstered` appears only on talents that can sit on weapons
   - `requires` values are among the selector's declared values
   - the per-kind simulator rules hold
   - the manifest's hashes match

The data project gets a command-line validator from this repository, so it can
check a delivery without the desktop app.

## v1 → v2 mapping

This mapping is the converter's specification. It also lists the checks the
converter must make, so that nothing is lost unnoticed.

| v1 | v2 | Converter checks |
| --- | --- | --- |
| `schemaVersion`, `id`, `name`, `version`, `gameVersion`, `description` | `manifest.json` | — |
| `progression` | `rules.level` = `maxLevel` | `startingLevel == maxLevel`, and no points per level |
| `stats[]` with `kind: derived` | `rules.stats[]` without `kind` | no `primary` stats |
| `item.occupies` | `item.slot` | exactly one slot |
| `item.effectsRequireContext {weapon_slot: S}` | implicit: the item is a weapon and S is its own slot | S equals the item's `slot`, and that slot is marked `weapon` |
| weapon `override` modifiers with `requiresContext {weapon_slot: S}` | `item.weapon` block | one override per stat, all gated on the item's own slot |
| other modifiers gated on the item's own slot | the same modifier without `requires` | gated slot equals the item's own slot |
| `requiresContext` on any other key | `requires` | key declared as a selector, value among its values |
| `item.attributeSlots`, `modSlots`, `talentSlots`, `upgrade` | template references, deduplicated by structural equality | expanding the template reproduces the v1 definition exactly |
| `attribute.op` | removed | always `flat` |
| `attribute.prototypeMax`, `prototypeMin` | `attribute.prototype {max, min}` | — |
| `mod.rollAttributeId` | `mod.rollAttribute` | — |
| `activeSkillSlots`, `activeSkills` | `skillSlots`, `skills` | — |
| `simTriggers`, `simSkills` | `simulation.triggers`, `simulation.skills` | — |
| `layout`, `theme`, `artwork`, all `icon`, `image` and `artwork` fields | `presentation.json` | every asset reference resolves |
| `skills` (passive), `scenarios`, `requirements`, `bucket`, `inc`, `tier` | removed | empty, or not used, in Division 2 |
| slot order | `share-codes.json legacyOrder.slots` | equals the v1 `slots` order |

Undeclared v1 context keys become `selectors`. Their names, sections and
descriptions come from the data project; until then the converter generates
placeholders from the key and its observed values.

## Implementation notes (2026-10-25)

The reader (`Core/Packs/PackReader`, `Wire/`) and the validator
(`Core/Packs/PackValidator*`) settle details this document left open. Until
JSON Schema files are published, the code is the reference.

- **Required properties.**
  - Manifest: `schemaVersion`, `id`, `name`, `version`, `gameVersion`,
    `capabilities` and `files`.
  - rules.json: `level`, `stats`, `slots` and `items`.
  - Numeric inputs: `min`, `max` and `default`; `step` defaults to 1.
  - Build outputs: `format`. Attribute, preview-output and skill-mod formats
    default to `decimal`.
  - Simulator triggers: `talent`, `key` and `kind`. A trigger's `talent` may name a specialization node, and its rule runs while
    that node is active, reacting as gear does. The node carries the effect, as a modifier reading the rule's key.
  - `share-codes.json`: `legacyOrder.slots`.
- **Manifest and capabilities.**
  - `files` may only name the four JSON files and `provenance.md`, so a manifest
    can't point the loader anywhere else.
  - An unknown capability stops loading. A feature that is used but not declared
    produces a warning.
- **Diagnostic paths.**
  - Reader paths take the form `rules.json:$.items[0].slot`.
  - Validator paths use the rules.json names (`skills`, `skillSlots`,
    `numericInputs`, `simulation.*`, `presentation.*`, `shareCodes.*`).
- **Ally-effect expressions** may use stats, curves and numeric functions only. A
  giver's effects are evaluated without tags, talents or context values.
- **Template names** come from the first item that uses the template:
  `<slot>_<n>`, counted per slot and template kind.
- **Artwork.** When `assets/` is present, the loader checks `assetsSha256` and
  every registered artwork file; a missing or altered file is an error. A
  distribution that leaves `assets/` out can add `artwork-withheld.json`
  (`{"assetsSha256": "…"}`, the manifest's digest) beside the pack files. Without
  `assets/`, and with that marker naming the manifest's digest, the pack loads with
  its artwork withheld and the app draws its own symbols. The marker is not a pack
  file: the manifest never lists it. The release ships the adopted pack's artwork,
  which is original MIT art (R34), so it doesn't use the marker.

## Open items

1. **Selector metadata.** For each of the 111 selectors, the data project
   supplies display names, sections and descriptions. This information exists
   today only in the v1 app's UI code.
2. **Breakdown for weapon base stats.** The first phase reports them exactly as v1
   did. A clearer "weapon base" breakdown is a later, recorded change.
3. **Build-document format.** Decided in
   [ADR 0002](adr/0002-retire-go-compatibility.md): a C# document schema v2
   without allocations, scenarios or passive skills. Document v1 is read only by
   the one-time importer. The v2 document is specified together with the
   application layer.


### Runtime input and JSON limits

Prototype `normalizeInput` reads the validated numeric assumption:
exactly 1 normalizes, including a declared default of 1. An invalid supplied value
reports `context-value-invalid` and uses the declared default, consistently with
other numeric inputs. It never reads or separately parses the raw context string.
