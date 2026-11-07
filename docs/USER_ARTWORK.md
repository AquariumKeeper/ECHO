# Customize artwork

Open **Artwork** from the editor's top toolbar. Artwork choices apply to this
computer and are shared by its open planner windows. They do not change builds,
share codes, stats, undo history or simulation results.

## Install and use

1. Choose **Install artwork ZIP…** and select a prepared artwork ZIP.
2. Wait for its files and images to be checked. Installation leaves your current
   artwork selected.
3. Select the installed pack to see up to six preview images, its author and
   license notices. An unavailable entry explains its problem under **Details**.
4. Choose **Use this pack** to apply it. Missing artwork inherits from the bundled
   artwork, then the app's own symbols. One user pack is active at a time.

The saved choice is restored on startup. Other open planner windows update when
the choice changes. **Use bundled** disables the user overlay without uninstalling
anything. **Refresh** rereads installed packs and the saved preference.

The ZIP is copied into the app's managed storage, so moving or deleting the source
ZIP does not affect the install. Several versions may coexist. The same exact
contents can be installed again without duplication; changed contents need a new
version label. Updates do not automatically replace your active choice.

**Cancel operation** stops unfinished work; already saved installations or choices
remain available. Escape cancels a running operation, or closes an idle dialog.

## Remove and recover

Select a revision and choose **Remove**. If it is the saved choice, the button
says **Use bundled and remove**. Close previews of that revision in other windows
before removing it. A window still displaying its images can briefly delay removal;
the app retains those files until its views release them.
Removal waits for local image reads and open windows to release the revision;
**Cancel operation** stops waiting. Close another window's preview before trying
again. A separate app process still using a revision may report it as in use.

If selected files are missing, changed or incompatible, the planner uses bundled
artwork and shows the problem in Artwork settings. The saved preference remains,
so exact repair can restore it. To reinstall a damaged revision, remove it first,
then install the original ZIP and choose **Use this pack** again.

If the settings document is malformed, **Reset invalid artwork settings** preserves
it in a backup and restores bundled defaults. The backup location appears under
Details. Read or permission failures are reported rather than silently overwritten.
If the settings entry is a filesystem link, recovery moves that link into the
backup without opening or modifying its target.

## Make a prepared ZIP

An artwork ZIP is separate from a game-data pack. It contains a root
`artwork.json`, declared PNG images, and text license notices. A full ShadeTech
game-pack candidate is not directly installable as an artwork ZIP.

For authors using a repository checkout, see [the v1 format](ARTWORK_PACK_FORMAT.md)
and the source example under `examples/artwork/minimal-slots/`. From that checkout,
build the example with:

```powershell
pwsh -File scripts/build-artwork-example.ps1
```

The generated `artifacts/minimal-slots-artwork.zip` contains a single orange
pixel for format testing, not release artwork. If the output already exists,
use `-Output` with a new destination. No image-authoring UI, stacked user packs,
remote downloads, auto-updates, theme/font overrides or application-icon overrides
are included in v1.

Bindings use game IDs rather than translated names or an exact game-data version.
Unknown targets are ignored with coverage notes; a pack needs some applicable
artwork. Authors can discover IDs through the existing CLI catalog commands.
Include author and license notices so the Artwork dialog can display them.

The managed store is `%LOCALAPPDATA%/CharacterPlanner/alpha/artwork-packs/`.
User-installed artwork is separate from the bundled game pack and is not included
in the portable release ZIP. Artwork controls are desktop-only in this version.

## Historical ShadeTech collection

The old 555-image collection can be used as a user pack. From a repository
checkout, convert the retained ShadeTech expanded-brand candidate 5 with:

```powershell
pwsh -File scripts/build-legacy-artwork.ps1 -SourcePack <path-to-sdf-research>/results/planner-v2/brands-expanded-assembly-v5/pack
```

Install the resulting `artifacts/shadetech-legacy-artwork-v1.zip` through Artwork.
The converter requires the exact pinned historical presentation and verifies every
PNG hash. It preserves all image bytes and mappings, includes each original source
record in a notice, and labels absent license fields **No license recorded**.
It does not change the source pack or the planner's game data. These historical
assets remain a separate local user ZIP rather than bundled release artwork.
The script refuses to overwrite an existing output; use `-Output` for another path.
