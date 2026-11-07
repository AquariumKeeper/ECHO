# CLI development surface

C10 currently provides catalog discovery, build creation/resolution, transactional edits, sharing, comparison, upgrade ranking, simulation, the saved-build library and live control of running windows alongside
the existing pack and share migration commands. Run `CharacterPlanner.Cli describe`
for the implemented command grammars, exit codes, input limit and document version.
The discovery contract is `d2p-cli-v1`.

## Catalog commands

```text
CharacterPlanner.Cli catalog list <pack-directory> <kind>
CharacterPlanner.Cli catalog show <pack-directory> <kind> <id>
```

Kinds are `items`, `slots`, `attributes`, `mods`, `talents`, `skills`,
`specializations` and `upgrades`, also listed by describe. Slots are equipment
slots; skills are active skill variants; upgrades are account upgrades. Per-item
and per-skill upgrades/configuration appear inside their detailed definitions.
Skill catalog responses include `result.skillSlots`, listing active skill slot
IDs/names and their declared variant choices; this field is null for other kinds.

List returns pack identity/version, kind, count and entries with id/name/description,
sorted by ordinal ID. Kinds without descriptions use an empty description.
Show returns the selected normalized definition in `result.entry`, with expanded
attribute/mod/talent choices, skill previews or specialization nodes as applicable.
These are model metadata with camel-case property names, not pack/build documents.
Modifier values are numbers or expression strings rather than internal union
fields. Commands validate pack structure, file hashes and semantic metadata, while
skipping artwork file checks; browsing works before artwork is delivered.
Nested definition objects use ordinal property ordering, preserving authored
array order. Commands perform no writes. Search, filtering
and pagination are not implemented; lists contain the whole collection.

Kinds and IDs are case-sensitive. Unknown kinds and IDs return exit 2 with JSON
`catalog-kind-unknown` or `catalog-id-not-found` diagnostics on stdout.
The diagnostic path identifies the unknown kind or missing ID, respectively. This differs
from malformed argument grammar, which keeps exit 2 and usage text on stderr.
Invalid metadata still exits 3, and I/O failures exit 4. Unknown kinds are rejected
before attempting pack I/O.

## Build commands

```text
CharacterPlanner.Cli build new <pack-directory>
CharacterPlanner.Cli build resolve <pack-directory> <build.json> [--context <context.json>]
CharacterPlanner.Cli build apply <pack-directory> <build.json> <operations.json> [--context <context.json>]
```

All build commands fully load/validate a schema-v2 pack, including its declared artwork
checks. A pack distributed without its artwork can carry an `artwork-withheld.json`
marker naming the artwork digest left out; it then loads cleanly and `pack validate`
reports `artworkWithheld: true`. Missing artwork without that marker is an error. The
release's bundled pack ships its (original) artwork, so it reports `false`. New returns Core's fresh build as a v2 document in `result`, without
writing a file. Extract that result object to save it; the response envelope is
not itself a build document. For example in PowerShell:

```powershell
$reply = .\CharacterPlanner.Cli.exe build new .\packs\division-2 | ConvertFrom-Json
if (-not $reply.ok) { throw 'Build creation failed; inspect the diagnostics' }
$reply.result | ConvertTo-Json -Depth 32 | Set-Content -Encoding utf8 .\build.json
.\CharacterPlanner.Cli.exe build resolve .\packs\division-2 .\build.json
```

Resolve accepts v2 documents only, checks pack identity, and uses the same Core
resolver as the desktop. Its result contains pack identity/version, stats keyed
by ID, selected skill previews, headline build outputs, explicit/normalized
numeric assumptions and the assumption IDs the build reads. Numeric values in
`assumptions.explicit` are JSON numbers and selector values are strings, so this
object can be reused directly as a context file. Stats keys, skill-preview
keys, both assumption maps and read-assumption IDs use ordinal ordering. Errors
return no partial result; warnings retain successful results. Build commands do not write the build, pack or library.

## Transactional edits

Apply accepts a JSON array of Application operations, runs them in order against
an in-memory build and explicit context, and returns everything needed to save
the result. A rejected batch returns no partial result. The `describe` response
lists all 18 supported `operationKinds` and four `removedOperationKinds`.
Operation fields and clear forms follow Application's strict `OperationJson`
contract: unknown, duplicate, missing and mistyped fields are rejected.

```json
[
  {"kind":"name","text":"ACR build"},
  {"kind":"equipment","slot":"primary_weapon","id":"primary_weapon_acr"},
  {"kind":"context","id":"weapon_slot","text":"primary_weapon"},
  {"kind":"context","id":"enemy_target","value":1}
]
```

Successful `result` contains `document` (the edited v2 build), `context` (explicit
assumptions as numeric JSON values or selector strings) and `changes` (one entry
per operation with `operation`, `kind`, `set` and `cleared` document paths).
Extract `result.document` and `result.context` separately to reuse them with
resolve or apply. Inputs are never overwritten. Cleared assumptions disappear
from context; defaults are not synthesized. An empty batch returns the input
build and explicit context with an empty changes array.

```powershell
$reply = .\CharacterPlanner.Cli.exe build apply .\packs\division-2 .\build.json .\operations.json | ConvertFrom-Json
if (-not $reply.ok) { throw 'Batch rejected; inspect diagnostics and operationErrors' }
$reply.result.document | ConvertTo-Json -Depth 32 | Set-Content -Encoding utf8 .\edited.json
$reply.result.context | ConvertTo-Json -Depth 32 | Set-Content -Encoding utf8 .\context.json
.\CharacterPlanner.Cli.exe build resolve .\packs\division-2 .\edited.json --context .\context.json
```

Parser and operation failures add `operationErrors` with `operation`, `kind`,
`code` and `message`. Indices are one-based within the failing source file;
zero identifies a batch-level error, including a final document encoding failure
(`build-unsaveable`) attributed to the operations file rather than an individual
operation. Diagnostic paths name that source file,
including the initial context file if its validation fails. Changes refer only
to the operations file, excluding initial context setup.

Apply validates operation acceptance and document encodability; it does not run
the resolver or certify untouched configuration. Use resolve to check the edited
build. Party-member share imports use app-owned codes; convert legacy codes with
`share convert` first. Apply does not create a saved-library record or session
revision/history.

## Share, compare and rank

```text
CharacterPlanner.Cli build share <pack-directory> <build.json>
CharacterPlanner.Cli build compare <pack-directory> <a.json> <b.json> [--context <context.json>]
CharacterPlanner.Cli build rank <pack-directory> <build.json> <slot> <stat> [--context <context.json>]
```

Share returns the build's app share code (`result.code`) with its name and pack
version. `share convert` reads the code back unchanged. Like `share convert`, it
reads only the pack's metadata, so it works before artwork is delivered. A build
the share format cannot hold fails with `build-unshareable`.

Compare resolves both builds under one context, as the desktop's Compare Builds
does; the context is checked against the first build. `result.stats` lists every
pack stat as `id`, `name`, `format`, `a`, `b` and `delta` (b minus a), largest
change first, with equal changes in pack order; `result.changed` counts the stats
that differ. A build for another pack, one the resolver cannot evaluate, or one
with an error diagnostic fails with `compare-build-invalid`, its path naming that
build, so a partial result never reads as a real difference. A change too large to
represent fails with `compare-value-overflow`.

Rank is the desktop's Rank Upgrades: every change to the item equipped in `slot`,
ranked by what it does to `stat`, largest gain first. Changes are each attribute
choice at its highest legal roll, each mod at its default roll, each other talent,
and the current attribute or mod rerolled when its roll is below the best. A weapon
slot is judged as the weapon in hand, so `result.context` reports the context the
ranking used (the input context with `weapon_slot` set to a ranked weapon slot).
Apply an upgrade's operations with that context to reproduce its `after`. Each upgrade has `kind` (attribute, mod or
talent), `target` and `targetName` (the item's slot), `id`, `name`, `value` and
`valueFormat` (the roll, when it has one), `reroll`, `before`, `after`, `delta`, and
`operations`: operation JSON that `build apply` accepts, so a ranked change can be
applied directly. An empty slot or unknown stat fails with `rank-unavailable`; a
change too large to represent fails with `rank-value-overflow`.

## Simulation

```text
CharacterPlanner.Cli build simulate <pack-directory> <build.json> [--options <options.json>] [--context <context.json>]
```

Simulate runs the same simulator as the desktop's Simulate tab on the build and
explicit context, and writes nothing. Without `--options` it runs the simulator's
defaults: one deterministic average fight of 60 s against targets. The options file
is a JSON object; every field is optional:

| Field | Type and default | Meaning |
| --- | --- | --- |
| `mode` | `"average"` or `"random"`; average | Fight model |
| `duration` | seconds, over 0 to 600; 60 | Fight length |
| `iterations` | whole number 1–10000; 500 | Random fights (average runs one) |
| `seed` | whole number from 0; 1 | Random seed |
| `targets` | boolean; true | Fight enemy targets (false: an unlimited dummy) |
| `retargetDelay` | seconds 0–60; 1 | Gap before the next target |
| `clearKills` | whole number 1–10000; none | With targets: report how long each fight takes to kill this many (`timeToClear`, `clearedFraction`) |
| `targetDistances` | list of 1–100 distances, metres 0–1000; none | With targets and `driveSimpleBuffs`: targets cycle through these distances, independently of `targetSequence`; each sets its target's falloff, distance rules and any enemy fire that follows distance, in place of `distance`. With `enemiesAtOnce`, each enemy fires from its own distance |
| `targetSequence` | list of 1–100 enemy target profile numbers; none | With targets and `driveSimpleBuffs`: targets cycle through these profiles, each with its own pools and target-dependent bonuses. Under `includeIncoming`, the target in play sets enemy fire and recovery. Works with `rotation` and `includeSkills`; skill damage follows each target, but a profile that changes skill timing, or which kinds of damage a skill deals, is refused |
| `peekSeconds`, `coverSeconds` | seconds 0.1–60; none | With `driveSimpleBuffs`, and both together: a cover cycle (ADR 0009). Each cycle peeks from cover to shoot, then stays fully behind cover: no shooting, a reload when the magazine isn't full, enemy fire blocked. `in_cover` holds for the whole fight. With `rotation`, swaps and reloads under way carry on behind cover and only the weapon in hand reloads there; swapping `whenEmpty`, a magazine isn't reloaded there unless a raised shield bars the swap |
| `peekHitPercent` | percent 0–100; none | With a cover cycle: the share of the normal enemy hit chance that lands while peeking. Required with `includeIncoming`, with no default |
| `enemiesAtOnce` | whole number 2–10; none | With `includeIncoming` and `driveSimpleBuffs`: this many enemies fire at once (ADR 0008). Talents with an enemy count read them, and one with a range needs `distance` or `targetDistances`. You shoot one at a time; the others wait and keep firing, each as its own `targetSequence` profile, and the next joins in the dead enemy's place. Refused when `enemy_attackers` is above 1 |
| `focusSeconds` | number 0.1–60; none | With `enemiesAtOnce`: the seconds you shoot an enemy before moving to the next living one in turn, taking the retarget delay (ADR 0011). A move waits for a reload and, in cover, for the next peek. Enemies keep what's on them, burns and bleeds keep ticking on enemies you left, and a tick can kill one. There's no default: this is an assumption about play |
| `enemiesShrink` | boolean; false | With `enemiesAtOnce` and `clearKills`: there are `clearKills` enemies in all, at least `enemiesAtOnce`. The next joins until none are left, then fire falls with each kill; after the last nobody fires or is shot, and the fight runs out its length |
| `armorKits`, `armorKitAtPercent` | whole number 1–10, and percent 1–100; none | With `includeIncoming` and `driveSimpleBuffs`, and both together: the kits carried, and the share of max armor below which one is used (ADR 0010). The pack describes the normal kit. A build with Survivalist, Firewall or Hackstep Protocol, which replace it, is refused. `incoming.armorKitsUsed` counts repairs that landed and `incoming.armorKitsCancelled` uses cut short by going down |
| `captureFirstFightEvents` | boolean; false | Return the first fight's events |
| `includeSkills`, `driveSimpleBuffs`, `includeIncoming`, `includeShield` | boolean; false | Skill damage, trigger-driven talents, enemy fire, the selected shield |
| `approximatePreservationOverlap` | boolean; ignored | Deprecated 2026-11-02 and has no effect: overlapping Preservation repairs always run as independent instances (R41) |
| `distance` | metres 0–1000; none | Fight distance for distance rules (needs `driveSimpleBuffs`) |
| `pickupDelay` | seconds 0–60; 2 | From a kill until its dropped pickup (Kill Confirmed's trophy) is collected |
| `pickupPercent` | percent 0–100; 100 | How many dropped pickups are collected at all (the desktop's "Trophies collected (%)") |
| `rotation` | `{"secondSlot": id, "mode": "whenEmpty" or "afterKill", "swapTime": seconds over 0 to 10}` | Two-weapon rotation; mode and swap time default to whenEmpty and 1 |

```json
{"mode":"random","iterations":500,"duration":60,"seed":1,"driveSimpleBuffs":true,"includeIncoming":true}
```

Like operation files, options are strict: unknown, repeated and mistyped fields,
text that is not valid Unicode, out-of-range values and `retargetDelay` without
targets are rejected with `simulation-options-invalid` (exit 3). So is a rotation
`secondSlot` that is not a weapon slot, has no weapon equipped, or is already the
active weapon. Combinations the simulator does not model (for example incoming fire
in an average fight) return the simulator's own code, such as
`incoming-mode-unsupported` or `weapon-required`, also exit 3; their path is empty,
because they depend on the build, context and options together.

Random fights can combine `driveSimpleBuffs: true` with a two-weapon `rotation`.
Ordinary weapon triggers follow the weapon in hand; gear rules remain eligible.
Swap-start events drive Gunslinger and Finisher (the latter needs a kill by the
weapon being holstered within ten seconds). With `includeIncoming` the same driven
rotation runs under enemy fire, and kill repairs follow the weapon in hand. Clutch,
Doctor Home and Preservation repair through their pack rules (R43): Preservation's
kill repairs come from its own weapon's kills; Doctor Home's marks come from its own
weapon's hits, and a marked target's death drops the kit whichever weapon kills it.
With
`includeShield`, a shield that allows only one of the two weapons forces that weapon
at the next shot or after the current reload while it is raised, and returns the
build's weapon the same way after it breaks; `incoming.shieldSwitches` counts these
forced swaps. Swaps under fire without `driveSimpleBuffs` and a shield that allows
neither weapon are refused (`incoming-rotation-unsupported`,
`shield-weapon-unsupported`). Magazine readings follow the weapon in hand
(unset while their owning weapon is holstered, empty during a reload). A trigger
talent on both weapons runs one copy per weapon, and the copy in hand drives the
talent; `timedTriggers` reports each copy as `key@slot`. Such a talent is refused
(`rotation-shared-talent-unsupported`) if it has status rules or holstered effects,
or shares a key or pool with another talent. A refill before a queued empty swap or reload begins cancels it. Results include these model limits. See ADR 0005 for timing.

The `result` reports pack identity, `contract`, `mode`, `iterations`, `duration`,
`seed` and `targets`; DPS summaries (`weaponDps`, `skillDps` with total, impact and
continuousWindow, `statusDps`, `talentDps` when a selected talent has a pack talent damage
rule, `totalDps`), each with mean, stdDev, min, p5, p50,
p95 and max; per-fight means for shots, reloads, swaps and kills; the first fight's
`killTimes`; `timeToKill` with targets and at least one kill: each fight's average seconds from a
target's arrival to its death, summarised over the fights with a kill. It includes the
fight's headshots, falloff, cover factor and resistances, and the fire interval carried
over from the previous target. A target still alive at the end isn't counted, and fights
weigh alike. `killedFraction` (null without targets) is the share of fights with a kill,
and only kills before the fight's end count. The first fight's `killTimes` trace also
lists kills that land after the end. with `clearKills`, `timeToClear` (the same summary over the fights that
reached that many kills, in seconds from the first target's arrival, or null if none
did) and `clearedFraction` (null without `clearKills`); the mean damage `timeline` in
one-second bins; `incoming` for enemy
fire; `uptime` maps (target statuses, timed triggers, cycle and magazine phases)
in ordinal key order; `events` (time, camel-case kind, amount or null, slot) only
when captured; `traceTruncated`; and the model `limitations`. Optional summaries
are null when they don't apply. The same inputs give byte-identical output.
Progress reporting and cancellation are not exposed.

## Library

```text
CharacterPlanner.Cli library list [--library <directory>]
CharacterPlanner.Cli library load <id> [--library <directory>]
CharacterPlanner.Cli library save <build.json> [--replace <id> --expected-version <version>] [--library <directory>]
CharacterPlanner.Cli library delete <id> --expected-version <version> [--library <directory>]
```

These read and write the desktop's own saved-build library: by default the folder
under local app data that the desktop uses (`CharacterPlanner\alpha\library`), or
the folder `--library` names. Records are build documents, one file per build,
written atomically under the same writer lock the desktop takes, so the two
programs can share a library. The library holds builds for any pack, so these
commands take no pack directory.

- List returns the folder and its entries: `id`, `name`, `packId`, `packVersion`,
  `savedAt` (UTC, ISO 8601), `version` and `problem`. A damaged or unreadable
  record is listed with its `problem` rather than hidden.
- Load returns `id`, `version`, `savedAt` and `document` (a v2 build document).
- Save validates the document and creates a new record with a new random id; a
  save never replaces a record by name. To replace one, pass `--replace <id>` with
  `--expected-version`, the version list or load returned; one without the other
  fails with `library-replace-invalid`. The result has `id`, `version`, `savedAt`
  and `replaced`.
- Delete always names the expected version.

If the record changed since that version was read, by the desktop or another
command, replace and delete fail with `library-conflict` instead of losing that
edit. Other library codes are `library-not-found`, `library-damaged`,
`library-invalid-id`, `library-unsaveable` and `library-path-invalid` (exit 3);
`library-busy`, another writer holding the library too long, exits 4.

## Live sessions

```text
CharacterPlanner.Cli session list [--session-dir <directory>]
CharacterPlanner.Cli session inspect [--session <id>] [--wait <milliseconds> [--since-revision <revision>]] [--session-dir <directory>]
CharacterPlanner.Cli session history [--session <id>] [--session-dir <directory>]
CharacterPlanner.Cli session resolve [--session <id>] [--session-dir <directory>]
CharacterPlanner.Cli session apply --expected-revision <revision> --operations <operations.json|-> [--session <id>] [--session-dir <directory>]
CharacterPlanner.Cli session undo --expected-revision <revision> [--session <id>] [--session-dir <directory>]
CharacterPlanner.Cli session redo --expected-revision <revision> [--session <id>] [--session-dir <directory>]
```

Each desktop window registers itself for live control (ADR 0007) in a per-user
folder: `%LOCALAPPDATA%\CharacterPlanner\alpha\sessions`, or the absolute path in
`CHARACTER_PLANNER_SESSION_DIR`, or `--session-dir`. Set the same override before
starting the desktop to use another folder. Requests go to that window over a pipe
only the same user can open, with a token from its registration; endpoints and
tokens never appear in output.

- List asks every registered window for its state and returns `sessionId`,
  `processId`, `startedAt`, pack identity, `available` and, for available
  windows, `buildName` and `revision`. A crashed window's leftover registration is
  listed unavailable with a `problem`, without being contacted, and the next window
  to start removes it.
- The other commands use `--session <id>`, or the one available window. With
  none, several, an unknown id or an unresponsive window they exit 4
  (`session-none`, `session-ambiguous`, `session-not-found`, `session-unavailable`).
  Without `--session`, a running window that doesn't answer the 2 s probe (busy, or
  on a loaded machine) is probed once more before `session-none` is reported.
- Inspect returns the `session`, `revision`, `buildName`, `document`, typed
  `context` (reusable with `--context`), `canUndo` and `canRedo`. With `--wait`
  it answers as soon as the revision passes `--since-revision` (default: the
  current one), or with the current state when the wait (at most 60000 ms) ends.
  `--since-revision` needs a `--wait` above 0.
- History lists each entry's `revision`, `origin` (initial, desktop or session),
  `label` and whether it is `current`.
- Resolve returns `build resolve`'s result for the window's build and assumptions,
  plus `session` and `revision`. It reads the pack the window loaded and fails with
  `session-pack-changed` if the pack on disk no longer matches.
- Apply sends an operation batch (the same JSON as `build apply`; `-` reads stdin
  as UTF-8 bytes, bounded like a file)
  as one undo step. Undo and redo move through the window's history. All three
  require `--expected-revision`, the revision you read. If the window changed since,
  nothing is applied and the command exits 6 with `stale-revision` and the current
  snapshot in `result`. Rejected operations exit 3 with `operations-rejected` (or
  `operations-invalid` when the batch can't be read) and `operationErrors`; an
  empty undo or redo exits 3.

The window shows CLI edits at once and lists them in its history like its own.

## Assumptions

An optional context file is a JSON object. Numeric assumptions take JSON numbers;
selector assumptions take strings, including string `"true"` for true-only
selectors. Example:

```json
{"weapon_slot":"primary_weapon","enemy_target":1}
```

Keys and values are validated by Application's atomic context operations, including
numeric bounds/steps, known selectors and weapon slots. Unknown keys, wrong types
and invalid values are errors rather than silent fallback. Duplicate keys and
nonfinite numbers are rejected. Files are UTF-8, optionally with a BOM. Build, operation
and context inputs are bounded to BuildDocument.MaxBytes (currently 4 MiB), with
bounded reads even for oversized input. Inputs are separate: assumptions are not
saved in build documents.

## Responses and failures

Every command, failures and grammar errors included, writes one JSON response to stdout:

```json
{"ok":true,"result":{},"diagnostics":[]}
```

Diagnostics have string `severity`, `code`, `path` and `message` fields. Resolver
diagnostic paths identify their Core subject; context operation errors retain
their Application codes. New input errors include `build-invalid`,
`build-wrong-pack`, `build-unsaveable`, `context-invalid`, `input-too-large`, `input-path-invalid` and
`pack-path-invalid`. I/O errors retain the existing `io` code.

| Exit | Meaning |
| --- | --- |
| 0 | Success, possibly with warnings |
| 2 | Invalid grammar (code `invalid-command`, with `usage` listing the grammars of the command group attempted) or unknown catalog kind/ID; JSON failure on stdout, and for grammar errors the full usage on stderr |
| 3 | Invalid pack, document, context, operation batch, simulation options or unsupported simulation; failed resolution, comparison or ranking; a library record that is missing, damaged or changed; JSON failure on stdout |
| 4 | I/O failure, including a busy library; no, unknown, ambiguous or unavailable live session; JSON failure on stdout |
| 5 | Internal failure (a broken invariant); JSON failure on stdout with code `internal` |
| 6 | Stale live-session revision; JSON failure on stdout with the current snapshot in `result` |
| 130 | Cancelled with Ctrl+C; JSON failure on stdout with code `cancelled` |

Commands already emit JSON; no flag is needed. The existing `--json version`
alias applies only to version. A success always carries `result`; every failure
carries at least one error diagnostic. Two failures also carry `result`: a stale
session revision (the current snapshot) and `pack validate` (the pack it read).
Absent fields are omitted rather than null. The first Ctrl+C cancels long work
(pack loading, simulation, ranking, library I/O and session requests and waits)
and still answers in JSON; a second one ends the process. A session edit that has
already been sent is not cancelled: the command waits for the window's reply, so
it reports what the window actually did. Only
`session apply --operations -` reads stdin. There are no global flags.
