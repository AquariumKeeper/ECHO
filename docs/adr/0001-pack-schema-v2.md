# ADR 0001: Replace the Go-era pack format with pack schema v2

- **Status:** Accepted by the owner, 2026-10-25. The field-level details are
  proposed until the [v2 specification](../PACK_SCHEMA_V2.md) is agreed with the
  data project. [ADR 0002](0002-retire-go-compatibility.md) widens the scope:
  all Go compatibility is retired, and the app is specific to Division 2.
- **Related:** findings H1, M5, D7, CR1, CR2 and CR6–CR8 of the rewrite's project
  and code reviews, and its plan's "Packs and calculations" and "Data pipeline and
  web fork" sections (internal records).

## Context

The plan required the C# app to load the frozen Go-generated pack unchanged and to
reproduce the Go loader's behavior exactly, including its error wording. Three
things have since changed:

1. The web app is becoming its own project. Nothing else needs to share the v1
   format.
2. The data pipeline belongs to a separate project, which will deliver data in
   whatever format this app specifies.
3. The owner has retired the Go pack-validation contracts.

A survey of the real packs on 2026-10-25 shows how much of the v1 format and its
loader serves no current purpose:

| Observation | Measurement |
| --- | --- |
| Go JSON quirks in real data | 0 duplicate keys and 0 `null` values across Division 2's 44,696 objects (Ashen Vale is the same). The merge, null-handling, case-folding and Go-wording code in `PackJson`, `PackJsonNormalization`, `PackJsonSyntax` and `PackDiagnosticOrder` only ever runs on hand-made malformed packs. |
| Load cost of that code | About 1.25 s warm and 2.1 s cold, compared with 89 ms for the whole Go process (H1). About 85% of that time is in the compatibility passes. |
| Weapon stats | 5,197 of the 6,616 modifiers are `override`s gated on `weapon_slot`. Each of the 522 weapons sets about 10 base stats this way, and all 522 also have an `effectsRequireContext` gate on `weapon_slot`. The "selected weapon" is expressed only indirectly, through context gates. |
| Repetition | 1,022 items share 57 distinct attribute-slot definitions, 108 mod-slot definitions, 258 talent-slot definitions and 8 upgrade definitions. Items make up 3.4 MB of the pack, and most of that is these repeated definitions. |
| Generic RPG features | Division 2 has a fixed level of 40, no point allocation, no primary stats, no item requirements, no scenarios and no passive skill nodes. Only Ashen Vale and some synthetic test packs use them. |
| Multi-slot items | None; `occupies` always lists exactly one slot. |
| Unused operations | Neither `inc` nor buckets appear. Attributes are always `flat`. |
| Untyped fields | `op`, `kind` and `format` accept any string. An unknown op is silently ignored (M5). |

## Decision

1. **The engine loads pack schema v2, and only v2.** The format is defined in
   [PACK_SCHEMA_V2.md](../PACK_SCHEMA_V2.md) and owned by this repository.
2. **v2 is strict JSON with a published JSON Schema.** Keys are case-sensitive,
   duplicates and `null` values are not allowed, and enumerated fields take only
   the values the schema lists. Structural validation uses System.Text.Json's
   strict options. Semantic validation (references, cycles, compiled expressions)
   produces this app's own diagnostic codes and wording.
3. **v2 models the game, not a generic RPG:**
   - Weapons get a typed base-stat block, and the active weapon becomes an explicit
     engine concept instead of context-gated overrides.
   - Slot, roll and upgrade definitions become shared templates.
   - Each item has a single `slot`.
   - Primary stats, point allocation, requirements, scenarios and passive skill
     trees are removed.
4. **The pack is split by consumer:**
   - rules and catalog for Core
   - simulator rules for Simulation
   - presentation (layout, theme, artwork) for Desktop
   - a share-code table that maps legacy codes to ids
5. **Ids are preserved exactly.** Saved libraries and share codes depend on them.
   The expression language and the modifier math are also unchanged.
6. **During the transition, a v1-to-v2 converter in this repository produces the
   v2 packs.** It only needs to handle the real v1 packs, not adversarial input.
   Once the data project delivers v2 natively, the converter becomes a test-only
   parity tool.

## Consequences

### Removed from production code

- The entire Go-compatible pack reader: `PackJson.CheckShape`,
  `PackJsonNormalization`, `PackJsonSyntax` and `PackDiagnosticOrder`, along with
  the Go type names and `%v` formatting used in pack diagnostics.
- The 692 pack-validation contracts, as the owner decided. Their fixtures stay in
  `baseline/` as history; their tests are removed.
- Primary stats, allocation budgets, requirements, scenarios and passive skill
  nodes:
  - `BuildProgression`
  - most of `LoadoutResolver`'s budget and requirement code
  - the scenario and skill-node parts of `BuildResolver`
  - the corresponding `BuildSnapshot` fields and edit helpers
- The "lowest version" share-code encoder (v1–v12). Nothing that reads v1-era
  codes remains. Codes are written only in a new id-based version; v1–v12 are
  still **decoded**.
- These earlier review findings no longer apply once v2 lands: H1, the D7 reader
  rewrite, M5, CR1 (partly; anchored regexes are still needed in the semantic
  validator), CR2, CR6, CR7 and CR8.

### Parity strategy

Calculated results remain the contract. Pack-loading behavior no longer is.

- The 12 original Division 2 resolve responses and the share-code corpus run
  against the converted v2 pack and must still match: stats, previews, outputs,
  sets, specializations and diagnostics.
- **Weapon base stats keep their current breakdown in the first phase.** The engine
  reports a weapon's base stats as the same override contributions from the same
  item, so the fixtures still compare exactly. A cleaner "weapon base" breakdown
  can follow later as a recorded difference.
- **Synthetic corpora are converted.** Cases that exercise removed features
  (requirements, budgets, primary stats, scenarios, passive nodes) are retired, and
  each retirement is listed in the ledger.
- **The Ashen Vale golden (F07) and its 14-case corpus retire.** Ashen Vale depends
  on primary stats, and the TypeScript engine it cross-checked has left. Small
  purpose-built v2 test packs replace it.

### Ledger updates to make when the code lands

| Entry | Change |
| --- | --- |
| F01, F02 | Accepted difference: v1 loading and Go diagnostics are replaced by v2 schema and semantic validation |
| F03 | Unchanged |
| F05, F06, F08 | Accepted difference: requirements, scenarios, allocations and budgets are removed |
| F07 | Retired, with the reason above |
| F51 | Accepted difference: v1–v12 are decoded only; codes are encoded in a new id-based version; "v1 readable by the web app" is dropped |
| P18 | Closed: the web handoff removes the second-engine requirement |

The plan's "Packs and calculations" contract and its "Data pipeline and web fork"
section also need revising to match.

### Risks

- **Conversion errors.** These are caught by the resolve and share corpora, which
  exercise every weapon slot, Prototype, talent copying, party effects and skills.
  Coverage of the catalog's long tail (1,022 items) is representative, not
  exhaustive. The converter therefore also checks round-trip invariants for every
  item: same ids, same choices, same effective modifiers.
- **Coordination with the data project.** v2 needs a stable specification before
  they deliver it. Until then, the converter provides v2 packs.
- **Schema churn.** v2 is versioned, and the manifest declares the capabilities a
  pack uses (see the specification). A pack that needs a capability the app lacks
  is rejected, never partly applied.

## Owner questions, resolved 2026-10-25

1. **Go wording and formats:** all Go content can be retired. The build-document
   and share-code consequences are in [ADR 0002](0002-retire-go-compatibility.md).
2. **Generic features:** removed. The application is specific to Division 2.
