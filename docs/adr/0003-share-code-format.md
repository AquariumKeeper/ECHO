# ADR 0003: Share code format D2P1

- **Status:** Accepted by the owner, 2026-10-26 (option 1 below).
- **Related:** [ADR 0002](0002-retire-go-compatibility.md) (share codes become the
  app's own and refer to everything by id), feature ledger item F51 (an internal record).
- **Implementation:** `Core/Builds/ShareCode` (encode and decode),
  `Legacy/Documents/LegacyShareCode` (v1–v12 import only).

## Decision

A share code is `D2P1.` followed by base64url text, without padding, of
zlib-compressed (RFC 1950), canonical UTF-8 JSON:

```json
{"name":"Build","pack":"division_2","packVersion":"1",
 "skills":{"skill_slot_1":{"mods":{"rolls":{"ammo":["ammo_mod",0.25]}},"skill":"turret"}},
 "slots":{"chest":{},"mask":{"attributes":{"core_1":["armor_core",170000]},"item":"mask_item","upgrade":{"rank":3}}}}
```

- **Prefix.** `D2P` names the format; the digits are its version. Base64url never
  contains a dot, so app codes and legacy codes can't be confused. A higher version
  is reported as "made by a newer version of the planner".
- **Ids, not positions.** Everything is named by its pack id, so codes don't depend
  on pack order. Unknown ids from newer data import and are diagnosed by the
  resolver, as for any retained invalid value.
- **Grouping.** Each equipment slot holds its item, attribute rolls, mods, mod
  rolls, talents, upgrade and Prototype; each skill slot holds its skill, grade and
  mods. A slot's item or skill is written whenever the build has one, even an empty
  one. An upgrade, Prototype, grade or skill-mod set records its own item or skill
  id only when it differs from the slot's selection, so the code stays lossless.
- **Canonical.** Keys are written in ordinal order and empty collections are left
  out, so equal builds have equal payloads; an empty per-slot map (for example a
  slot's attribute rolls with no entries) is therefore not shared. Text is plain
  UTF-8, escaped only where JSON requires (quotation mark, backslash and control
  characters), so names in any script stay compact. The compressed bytes may change
  between runtimes; the payload doesn't.
- **Encoding errors.** A build without a pack id, or with a non-finite number or
  invalid UTF-16 text, can't be shared; `Encode` throws `ArgumentException` naming
  the field. `ShareCodeException` is reserved for codes that can't be read.
- **Shared fields.** Pack id and version, name, notes, equipment and its
  configuration, skills, specialization, specialization nodes and account upgrades.
  **Party members are not shared**, as in the Go codes: one party build exceeded
  2,000 characters, and members carry codes of their own.
- **Integrity and limits.** The decoder checks the zlib Adler-32 trailer itself
  (.NET's `ZLibStream` accepts a truncated stream), so a cut, extended or altered
  paste is reported as damaged. Codes are limited to 64 KiB and payloads to 1 MiB;
  surrounding whitespace is ignored. Unknown or repeated members are rejected.

## Options considered

Code lengths in characters on the six Division fixture builds, party excluded:

| Encoding | Mean | vs Go v12 |
| --- | ---: | ---: |
| Go legacy v12 | 1,007 | — |
| Per-slot JSON, deflate (option 1, as measured in research) | 910 | −10% |
| Per-slot JSON, Brotli | 825 | −18% |
| Per-slot JSON, deflate with a frozen 32 KB pack-id dictionary (option 2) | 762 | −24% |
| Numeric stable ids, varints (option 3) | 390 | −61% |

1. **Chosen: string ids, per-slot JSON, zlib, base64url.** No new pack data, a
   readable payload, and browsers can inflate it with the built-in
   `DecompressionStream("deflate")`. As implemented, the six builds measure
   717–1,197 characters, 5–13% shorter than their Go codes.
2. **Preset dictionary.** About 16% smaller again, but the dictionary becomes a
   permanent versioned artifact, and browsers' built-in streams can't use one.
3. **Numeric ids** (as Hearthstone and Legends of Runeterra do). About 2.3 times
   smaller, but needs an append-only id-to-number table in the pack that the data
   project maintains for ever.

Positional references (as World of Warcraft talent strings use, with a tree hash
to detect change) were ruled out by ADR 0002. The version prefix leaves room to
adopt option 2 or 3 later without breaking existing codes.
