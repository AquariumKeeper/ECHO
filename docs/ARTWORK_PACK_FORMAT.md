# Artwork pack format v1

The standalone artwork contract is implemented by Application/Artwork. It is
independent of game pack schema v2. The current slice reads and validates the
manifest and compatible bindings, validates ZIP/image contents and installs immutable
revisions. Installed-pack management and persistent selection are implemented;
desktop activation, previews and controls are implemented. See the
[user guide](USER_ARTWORK.md).

An artwork ZIP has a root `artwork.json`, declared PNG assets below `assets/`,
and declared UTF-8 license notices below `licenses/`. It supplies appearance
only. It cannot replace the game pack, calculations, theme, window/taskbar icon
or a saved build. The source example is in `examples/artwork/minimal-slots/`.
Its image is intentionally a single orange pixel, for format/installer tests;
it is not the proposed release artwork or a ShadeTech delivery.

## Manifest

Required root fields are `schemaVersion` (integer 1), `id`, `name`, `version`,
`author`, `gamePackId`, `credits`, `assets` and `bindings`. `description` is
optional. JSON is UTF-8 with an optional BOM. Duplicate and unknown properties,
wrong types, invalid Unicode and missing required fields are errors. A JSON null
does not stand for an omitted required value. Maximum JSON nesting is 32.

- `id`: 1–64 lowercase ASCII letters/digits/dots/underscores/hyphens; starts with
  a letter or digit; must also be a portable filename without a reserved device
  name or trailing dot. Asset IDs use the same rules and must be unique.
- `name`, `author`, `version`: nonblank Unicode text, limited to 128, 256 and 64
  UTF-16 characters respectively, without control characters. Versions are opaque
  labels and are not compared using semantic-version ordering.
- `description`: optional Unicode text up to 4,096 UTF-16 characters, without
  control characters.
- `gamePackId`: exact current game ID, `division_2` for this app. No exact game
  version/hash restriction is imposed.
- `credits`: at least one `{author, license, licenseFile}`. Author/license labels
  are nonblank and limited to 256/128 characters. License paths use `licenses/`
  and a lowercase `.txt` extension. Several credits can reference the same exact
  path; case variants of one path are rejected.
- `assets`: at least one `{id, path, sha256}`. Paths use `assets/` and a lowercase
  `.png` extension. Digests are exactly 64 lowercase hexadecimal characters.

All member paths are relative, slash-separated and at most 240 UTF-16 characters.
Reject empty/dot/parent segments, backslashes, absolute or drive paths, control
characters, invalid Unicode, Windows device names, trailing dots/spaces, alternate
streams and other Windows-special filename characters. File names cannot collide
case-insensitively or also name a parent directory. Every shared directory prefix
must use the same casing, including explicit ZIP directory entries. Paths are validated independently
of the host OS. Image contents and hashes require the later archive validation
stage; passing manifest validation alone does not validate a ZIP or decode PNGs.
ArtworkPackInstaller performs those additional checks before publishing a revision.

## Bindings and compatibility

`bindings` has three optional maps, each empty when absent:

- `icons`: group to game object ID to local asset ID. Supported groups are
  `slots`, `talents`, `skills`, `specializations` and `nodes`. A `nodes` key is
  a specialization node ID (one tier, such as `gunner_incessant_tier_1`). The
  inspector shows each tier's icon; the tree view shows one icon per perk card,
  from the first of its tiers that has one, so binding tier 1 is enough. The
  tree's middle box shows the specialization's image, or else its icon.
- `itemArt`: item ID to `{image?, icon?}`. At least one nonnull asset reference
  is required. An absent/null layer inherits. An empty asset reference is an
  error, rather than an instruction to erase a lower layer.
- `specializationImages`: specialization ID to local asset ID.

Images may be referenced by any number of bindings. Every reference must name a
declared asset, including references on targets unknown to current game data.
Unknown groups are errors. Unknown object IDs are warnings and are ignored in
the applicable bindings; the original manifest remains available for later data
updates. A pack must contain at least one applicable binding. Target IDs are
nonempty, valid Unicode up to 256 UTF-16 characters without control characters.

`ArtworkManifestReader.Read` returns either the original manifest plus an
immutable applicable-binding projection, or errors with no partial manifest or
projection. Diagnostics have severity/code/path/message. Warning-only results
are successful. Diagnostic map traversal is ordinally ordered; authored arrays
retain their order.

## Archive and image limits

`ArtworkLimits` centralizes the installer bounds: 128 MiB ZIP, 256 MiB
expanded bytes, 4,096 file entries, 1 MiB manifest, 8 MiB per PNG, at most 4,096
pixels per image axis and 16,777,216 per image, 64 million source pixels combined,
and 256 KiB per license. The installer enforces
these bounds, verifies actual member lengths and ZIP CRCs, checks asset hashes,
requires complete static PNG containers with valid chunk CRCs, and fully decodes
each image through the supplied desktop decoder. APNG is rejected. Directory
entries are allowed only when empty and portable; all entries, including
directories, count toward the 4,096-entry limit.

## Installation semantics

Application's `IArtworkPackInstaller` returns an installed snapshot and diagnostics;
installation does not select a pack or issue a planner operation. Infrastructure's
`ArtworkPackInstaller` takes an explicit private store directory and a required
`IArtworkImageDecoder`. Desktop supplies `ArtworkPngDecoder`, using Avalonia's
bitmap decoder off the UI thread, returning only dimensions and disposing images.

The ZIP must contain exactly its declared files and root manifest; links, special
members, unsafe names, duplicate/case-colliding paths, missing/unlisted files and
invalid UTF-8 license notices are rejected. Every source image is checked once,
including unused assets. Pixels count per asset, not per binding. Source ZIP and
game-pack files are never modified.

Files are checked in an app-owned `.staging-<nonce>` directory inside the store.
A revision is published by a same-filesystem directory rename into
`<store>/<id>/<contentDigest>/`. The digest covers ordinal file paths and SHA-256
of each file's exact bytes, including the manifest and license notices; it is
independent of ZIP order, timestamps, compression and empty directory entries.
Reformatting the manifest changes the contents even when its meaning is equal.

A cross-process file gate serializes publication and ID/version conflict checks.
Identical contents are a no-op after the existing revision's inventory and bytes
are verified. A new version installs beside the previous one; the same ID/version
with different contents is refused. A changed/unreadable installed revision is
reported rather than silently overwritten or reused. Installation reports checked
file counts and supports cancellation before publication. Once the rename commits,
it returns success; cancellation does not roll back a published revision.

Only this attempt's staging directory is cleaned on failure/cancellation, with
cleanup constrained to that directory under the configured root. If permissions
prevent cleanup, leftover staging is never treated as an installed revision.
No active selection or settings file is changed by installation itself. The same
service implements `IArtworkPackStore` for the separate management operations below.
An unreadable manifest in another old revision produces a warning and is excluded
from version-conflict checks; that revision stays visible for removal. Intact
manifests still reserve their ID/version. An exact damaged destination is never
overwritten or silently accepted.

## Catalog, selection and revision lifetime

`ListAsync` returns installed revisions, compatibility diagnostics and the current
selection revision. It revalidates the manifest, complete file inventory, byte
limits, PNGs, hashes and directory content digest. Corrupt/incompatible revisions
remain visible as unusable entries. Private staging, lease and removal directories
are excluded. Validation uses a short-lived revision lease; it does not hold the
publication gate while decoding images.
Each installer instance remembers up to 4,096 fully decoded image digests. Reads
still hash the exact bytes and verify the complete inventory/content digest, so
same-size tampering with restored timestamps is detected. A previously verified
digest reuses its PNG container/decode proof, including across concurrent reads;
it does not decompress that image again. Each declared asset still counts toward
the pixel budget, even when several assets contain identical bytes. Proofs are
memory-only and a fresh process validates again.

`SelectAsync` takes the expected selection revision and either an installed
`ArtworkPackReference` (ID/content digest) or null for bundled mode. It validates
the candidate first, then rechecks the revision under the publication gate before
atomically replacing `<store>/artwork-settings.json`. Stale requests fail without
overwriting a newer selection. No-op selections retain the revision. Settings are
schema-v1 JSON, bounded to 64 KiB, strictly parsed and ordinally serialized; they
contain selections keyed by game ID and a global revision. Selecting bundled mode
clears only that game's preference. No game pack, build or library file changes.

`LoadSelectionAsync` returns an independently disposable `ArtworkSelectionState`.
Its active lease holds the validated revision for views and their bitmap cache.
The caller must dispose the previous state only after rebinding/releasing those
views. Lease files are app-owned, exclusive handles under
`<store>/.leases/<id>/<digest>/` and are removed on close/process exit. A slow load
rechecks preferences after decoding and retries a changed choice, bounded to
three attempts; continuously changing selection falls back with a refresh message.
The UI must also discard stale asynchronous refresh callbacks when integrating
these APIs.

Missing, changed, unreadable or incompatible selected artwork falls back to
bundled artwork with diagnostics while preserving the desired preference and its
revision. Malformed settings fall back with revision -1 and block mutation until
explicit recovery. `ResetInvalidSettingsAsync` preserves the exact malformed file
in a private backup, installs bundled defaults with a fresh random revision epoch,
and returns the backup path. Recovery also handles an exhausted revision counter.
It refuses to reset ordinary valid settings that another window has already
repaired. Settings with an I/O error are preserved rather than replaced.
Linked settings entries are quarantined by renaming the link itself; recovery
never reads or changes its target. Both file links and directory links use this
same explicit recovery path.

`RemoveAsync` requires the current selection revision and refuses any revision
selected by any game or held by a live lease. Switch to bundled mode, refresh the
views and dispose their leases before retrying removal. Closed/orphaned lease
files are cleaned. An inactive revision is atomically renamed to a private removal
directory, then deleted; failed physical cleanup returns a warning after logical
removal. Missing revisions are a no-op. Removing an inactive revision does not
increment the preference revision, because its preference has not changed.

The management APIs are independent of planner sessions. The desktop restores
selection at startup and exposes an Artwork dialog. Previewing an inactive pack
uses `OpenRevisionAsync` and a separate disposable lease. A lease can read only
its declared license notices, with the same byte bound; ongoing notice reads
retain the pin until completion even if the preview closes.
The library and assumption sets load before optional artwork initialization.
Removal awaits pending local validations, notice reads and cache ownership
completion instead of retrying for a fixed time. It can wait for another window
in the same process to release its images; Cancel operation stops waiting. A
different process holding a lease is reported as in use. Reopening waits for a
canceled operation's completion without polling. `DisposeAsync` also drains owned
backend work when a caller needs to release store locks before cleanup.

Appearance refresh updates tiles, item/skill/talent inspectors, specialization
images/nodes, the specialization tree's cards and middle box, an open item picker and Compare Talents without issuing an edit or
restarting resolution/simulation. Item picker art is observable on stable rows,
so updating images preserves its highlighted row and candidate preview.

ArtView/IconView use asynchronous shared bitmap handles instead of a permanent
converter cache. Handles retain revision registrations until the last view and
pending decode release them. User bitmaps retire once sources are cleared/views
detach. Bundled images retain at most 128 idle bitmaps and 32 MiB of decoded pixels
in least-recently-used order, avoiding repeated tab/picker decoding; changed file
metadata discards an idle entry, and failed reads are never retained. Decoding is
limited to two concurrent jobs, display widths are capped and sources are never
upscaled. User PNG dimensions/file bytes use the shared Application header gate;
bundled images retain the game presentation's broader format/size support.
Specialization illustrations use the same owned view.

A coalesced filesystem watcher observes settings and published content changes,
including both sides of rename events, and ignores staging/lease/backup work.
Foreground operations defer watcher refreshes so catalog reloads cannot reopen
a preview while removal is releasing it. Generation checks discard stale preview,
notice, catalog and active-selection responses. UI diagnostics are bounded to
100 displayed entries with clipped long subjects/messages; complete structured
backend diagnostics remain available to API consumers.

## Build the example

```powershell
pwsh -File scripts/build-artwork-example.ps1
```

This creates `artifacts/minimal-slots-artwork.zip` from the three fixed example
members, with stable order/timestamps and a printed SHA-256. The output must not
already exist; `-Output` can name a different file. It adds no Python, Go or
image-tool dependency. A ShadeTech exporter for accepted original artwork remains
separate work; no producer receipts or adopted pack pins change in this slice.
