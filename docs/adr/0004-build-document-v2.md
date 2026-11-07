# ADR 0004: Build document v2

- **Status:** Accepted by the owner, 2026-10-26. Follows from ADR 0002's "document schema v2 for
  Division 2, without allocations, scenarios or passive skills"; the encoding
  choices below are the implementation's.
- **Related:** [ADR 0002](0002-retire-go-compatibility.md),
  [ADR 0003](0003-share-code-format.md) (the build object it shares),
  feature ledger item F50 (an internal record).
- **Implementation:** `Core/Builds/BuildDocument`, `Core/Builds/BuildJson` (the
  shared build object), `Core/Builds/PartyRules`.

## Decision

A saved build is UTF-8 JSON, indented two spaces, ending in a newline:

```json
{
  "schemaVersion": 2,
  "build": {
    "name": "Build",
    "pack": "division_2",
    "party": [
      {
        "armor": 5,
        "effects": [
          {
            "active": false,
            "id": "overwatch",
            "values": {
              "total_weapon_damage": 0.12
            }
          }
        ],
        "name": "Medic"
      }
    ],
    "slots": {
      "mask": {
        "attributes": {
          "core_1": ["armor_core", 170000]
        },
        "item": "mask_item"
      }
    }
  }
}
```

- **One build object.** `build` is exactly the object a D2P1 share code carries
  (ADR 0003), plus `party`. One writer and one reader serve both, so a document
  and a share code of the same build can't drift apart. Everything is named by
  id; per-slot grouping, the explicit empty item or skill, and ids recorded only
  where they differ from the slot's selection all carry over.
- **Canonical and diffable.** Keys are in ordinal order, empty collections are
  omitted, text is escaped only where JSON requires, and lists of plain values
  stay on one line. Saving an unchanged build rewrites the same bytes.
- **What a document holds.** The build: pack id and version, name, notes,
  equipment and its configuration, skills, specialization and nodes, account
  upgrades and the party. A party member has a name, an optional share code (its
  giver as an app code), optional armor (by hand) and its effects, each with
  `id`, `values` and `active`. Not held: the level (fixed by the pack),
  assumptions and edit history, which are session state.
- **Party rules.** At most three members, unique non-blank names (compared
  case-insensitively), finite non-negative armor, unique non-blank effect ids and
  finite values. `PartyRules` enforces them for v1 imports and v2 documents alike,
  and `Encode` refuses a build that breaks them, so a saved file always reopens.
  The editor matches member names with the same rule (`PartyRules.Key`).
- **Versions.** `schemaVersion` is read first. Version 1 is a Go-era document,
  imported through `Legacy` and never written. A higher version is reported as
  "made by a newer version of the planner". Unknown and repeated members are
  rejected rather than ignored, so a file has one meaning.
- **Limits.** Documents are limited to 4 MiB, and `Encode` refuses a build that
  would exceed it rather than write a file that can't be reopened. A leading UTF-8
  byte order mark is ignored, and a member name that isn't valid text is a format
  error.

## Consequences

- Every one of the 135 frozen v1 documents that v1 accepted, and the six Division
  fixtures, converts to v2 and reads back unchanged apart from the recorded
  normalisation (an empty per-slot map isn't written).
- Storage (the library, atomic replacement, conflict detection and the one-time
  migration of a copied Go library) is Infrastructure's, at C7; this decision
  covers only the file's contents.
