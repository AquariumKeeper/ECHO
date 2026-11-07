# ADR 0002: Retire Go compatibility; the app is Division 2 only

- **Status:** Accepted by the owner, 2026-10-25.
- **Supersedes:** the plan's "Compatibility contracts" requirement to match the
  frozen Go application's wording, formats and protocols.
- **Related:** [ADR 0001](0001-pack-schema-v2.md) (pack schema v2), and the
  rewrite's plan, feature ledger and code review (internal records).

## Decision

1. **Go compatibility is no longer a goal.** The C# application need not
   reproduce the frozen Go application's:
   - pack format
   - error wording
   - JSON quirks
   - share-code encodings
   - CLI command surface
   - session protocol
   - random-number stream
   - storage format
2. **The application is specific to The Division 2.** Generic character-planner
   features that Division 2 doesn't use are removed, and the Ashen Vale sample
   pack goes with them.
3. **The frozen Go outputs remain a test reference for game maths, not a
   compatibility contract.** They are the best record of accepted calculation
   results. They stay as regression tests until a result is changed on purpose,
   and each such change is recorded with its reason (see
   [ledger changes](#ledger-and-plan-changes)).
4. **Users' existing data is imported once, then left alone.** Testers' Go build
   libraries and existing share codes (v1–v12) are imported into the C# app's own
   formats. This import is the only place Go-era formats are still read. It reads
   them; it never writes them.

Point 4 is a recommendation within the owner's "if needed": the migration path
the plan promised testers. Dropping it means testers would rebuild their saved
builds by hand.

## What each area becomes

| Area | Was (Go parity) | Becomes |
| --- | --- | --- |
| Pack | Load the v1 pack as Go's decoder did, with Go-worded diagnostics | Pack schema v2 ([ADR 0001](0001-pack-schema-v2.md)) with the app's own diagnostics |
| Calculation results | Exact match with frozen responses: stats, provenance, messages | Same stats, previews, outputs and provenance, within the documented tolerances. Diagnostics are compared by **code and subject**; wording is ours and can be improved. |
| Build documents | Go's `encoding/json` behavior and wording (135 contracts) | A C# document schema v2 for Division 2, without allocations, scenarios or passive skills. A one-time v1 importer keeps v1's accept and reject rules, with the app's own messages. |
| Share codes | Encode the lowest of v1–v12; the frozen reader must accept C# codes | Encode one new version that refers to everything by id. Decode v1–v12 for import only. The "lowest version" logic and the frozen-reader evidence retire. |
| Assumptions | Go's numeric grammar, including `strconv.ParseFloat` quirks such as hexadecimal floats in Prototype normalization | Typed assumption values from the UI, CLI or session, validated against the pack's numeric inputs and selectors. No string-grammar emulation. |
| Simulator | Port Go's `math/rand` stream so seeded runs match exactly (F60's open question) | The app's own seeded random-number contract, versioned. Average mode should still match the frozen average-mode fixtures within tolerance; random mode is checked statistically against the frozen distributions. |
| CLI | Preserve `planner describe`, command names, JSON envelopes and exit codes | The app's own versioned CLI contract, designed with the application layer. The frozen CLI fixtures stop being contracts. |
| Live sessions | Go's named-pipe protocol, plus a decision on interoperating with old sessions | The app's own protocol, with no interop with Go sessions. Fixture K01, a Go bug, becomes irrelevant. |
| Library | Cooperative writes with the Go CLI in the Go config directory | The app's own storage, with a one-time import of a *copy* of the Go library. The Go library is never written, as the plan already required. |
| Number and text formatting | Go `%v` and `strconv.Quote` emulation (`ReferenceText`) | .NET invariant formatting, chosen for the UI and CLI |
| Generic features | Primary stats, allocation, level ranges, requirements, scenarios, passive skill tree | Removed. The level is fixed by the pack (40). |

## What gets deleted

Approximate line counts from the current source:

- **Pack reader (about 800 lines):**
  - `PackJson`'s shape check and Go type-name messages
  - `PackJsonNormalization`
  - `PackJsonSyntax`
  - `PackJsonStrings`' repair fallbacks
  - `PackDiagnosticOrder`
  - the v1 record definitions, which the v2 model replaces
- **Formatting and parsing emulation:**
  - `ReferenceText`'s Go formatting
  - `BuildDocumentShape`'s Go wording
  - Go-specific string-grammar emulation in `ResolveAssumptions`
  - `PrototypeRules`' hexadecimal-float parser
- **Generic features:**
  - `BuildProgression`
  - the budget and requirement parts of `LoadoutResolver`
  - the scenario and skill-node parts of `BuildResolver`
  - the matching `BuildSnapshot` fields and edit helpers
- **Share codes:** the version-selection logic in `ShareCodeEncoder`, and
  `baseline/ShareEncodingProbe`.
- **Tests:**
  - `LegacyPackContractTests` and its 692 cases
  - the Ashen Vale golden and its 14-case corpus
  - the document-wording comparisons
  - the share-encoding frozen-reader checks
  - synthetic-corpus cases that cover only removed features

That is roughly a fifth to a quarter of Core. Several open review findings
disappear with it: H1, M2's wording side, M5, and CR1, CR2, CR6, CR7, CR8 and CR9.
The "questions to settle with new captures" in the code review no longer need
captures. Each becomes an ordinary design choice, decided in favour of the
simplest sensible behavior.

## What stays

- **Calculation fixtures, as regression tests:**
  - the 12 original Division 2 resolve responses
  - the other synthetic resolution corpora, converted to small v2 packs where
    they cover behavior that's kept
  - the simulator fixtures, which become the reference for simulator results
- **The expression language and the modifier maths.**
- **Recorded decisions on invalid values.** The engine keeps an invalid value and
  reports it, rather than silently clamping or dropping it. This was a Go-era
  rule, but it's good behavior for the UI, so it stays.
- **Checksum pinning of baseline evidence.** The frozen fixtures remain pinned by
  `INDEX.txt`, and the manifest's release hash stays in `baseline/` as history.
- **The no-Go-toolchain rule in the contributor instructions.**

## Ledger and plan changes

When the code lands, record the following.

**Ledger:**

| Entry | Change |
| --- | --- |
| F01, F02 | Accepted difference: pack v2 |
| F05, F06, F08 | Accepted difference: generic features removed |
| F07 | Retired |
| F50 | Document v2, plus a v1 importer |
| F51 | Id-based share code, plus v1–v12 import |
| F60 | Own random-number contract; statistical acceptance |
| F80–F86 | Own CLI and session contracts |
| F85 | No Go session interop |
| K01 | Not applicable |
| P18 | Closed |

Each entry should say whether calculated results changed. With the fixtures kept
as regression tests, the default answer is no.

**Plan:**

- Rewrite the "Compatibility contracts" section around these rules: results are
  checked against regression fixtures, formats belong to the app, and Go-era data
  is imported once.
- Drop the "Legacy work during the rewrite" migration-delta process. The Go app
  is frozen, and nothing flows from it any more.
- Update C2's and C3's acceptance gates to refer to pack v2 and the regression
  fixtures.
- Make C10's gate the app's own CLI and session contract.
- Make C8's random-number item a design decision.

**Contributor instructions:** replace "Preserve baseline fixtures. Record deliberate differences
in the ledger" with "Baseline calculation fixtures are regression references:
change an expected result only on purpose, with a ledger entry".

## Consequences

- **The C3 gate shrinks and becomes clearer.** It needs pack v2, the compiled
  pack, an application session, the document v2 importer and the new share code,
  not byte-level reproduction of Go formats.
- **Behavior changes can now be made on purpose**, such as fixing a calculation
  the community has shown to be wrong. The ledger, not parity, keeps them
  disciplined.
- **The risk moves.** Nothing now forces exact parity on everything outside the
  regression fixtures. Coverage beyond those fixtures (the catalog's long tail,
  simulator edge cases) relies on new C# tests and tester feedback, so
  property-style tests across the whole catalog become more important. For
  example: every item resolves without errors when equipped, and every skill
  variant produces finite previews.


## Implementation clarification, 2026-10-26

Prototype normalization now reads the validated numeric assumption, including its
pack default when absent or invalid. Invalid raw strings cannot independently
activate normalization. The hexadecimal-float parser has been removed.

The current session/operation context still transports strings for selectors and
numeric inputs together. `ResolveAssumptions` retains a strict decimal boundary
adapter (JSON-style numbers, finite, bounded and step-aligned); this is an interim
C# contract, not Go grammar emulation. Replacing that transport representation
with separate typed values remains future work. No hexadecimal floats,
underscores, leading plus, leading zeroes or surrounding whitespace are accepted.
