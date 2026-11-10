Character Planner 0.2.0-beta.5 — tester release

To start, double-click CharacterPlanner.Desktop.exe. It opens the build editor; its
Simulate tab runs combat simulations against targets, with optional enemy fire,
repairs, shields and trigger-driven talents, within the model limits each result
lists. Keep the packs folder beside it: the app reads the game data from there.

CharacterPlanner.Cli.exe is the command-line version and answers every command in
JSON. Run "CharacterPlanner.Cli.exe describe" for its commands: catalog browsing,
build creation, editing, resolution, sharing, comparison, upgrade ranking and
simulation, the saved-build library, and live control of a running desktop window
(session commands). Builds are saved in %LOCALAPPDATA%\CharacterPlanner\alpha,
which both programs share. If something goes wrong in the desktop window, it says
so and stays open, and the details go to errors.log in the same folder.

Each program is a single self-contained file, with no separate .NET installation
required (Windows x64). On first start it unpacks a few graphics libraries into
%TEMP%\.net. Windows may warn that the program is unrecognised, because it isn't
signed; betas stay unsigned.

Report problems or wrong numbers at https://github.com/AquariumKeeper/ECHO/issues.
Say which build and which numbers, and attach errors.log if the window reported one.

The Division 2 pack is bundled with no game artwork. Its only images are eight
original slot images by Shade-Tech contributors (MIT); the app draws its own symbols
for talents, skills, specializations and items. release-manifest.json lists every
file in this archive with its SHA-256.

New since 0.2.0-beta.4:
- A tree view for allocating specialization nodes, laid out as the game does, with
  each node's artwork.
- Prototype items show in the game's Prototype colour, and Exotics in the game's
  orange.
- If something goes wrong in the window, it reports it and stays open.

Changes since 0.2.0-beta.4 that move fight results, each only for builds with the
talent:
- Skilled resets skill cooldowns on a skill kill, by chance. With no cooldown
  running, the reset waits and lands 1 s after the next starts, so turret, hive
  and Sticky Bomb builds gain too.
- Calculated takes time off skill cooldowns on a kill in cover.
- Tag Team cuts the skill cooldown when a weapon hit follows a skill hit.
- Plague of the Outcasts stacks its debuff on every hit.
- Efficient keeps a used armor kit by chance, from 10 s into a fight, and doubles
  the specialization kit bonuses (Gunner's bonus armor, Demolitionist's and
  Sharpshooter's kit buffs).
- Outsider, Preservation and Ignited count a skill's kill as well as the weapon's,
  while the weapon is in hand. In the game, kills by turrets, drones and hives don't
  count for them, or for Skilled; the planner counts them, so those builds read high.

Changes in 0.2.0-beta.4 that moved fight results, for testers coming from beta.3:
- A burn or other status ends when its enemy dies.
- Turrets and mines space their shots out instead of landing them all at once.
- Stinger Hive's stings share one status per enemy and draw on stored charges.
- Fights reach Overcharge, through the Energize and Future Perfect talents.
- Skills cycle through deployment, duration and cooldown.
Builds saved with an earlier beta open unchanged.

Also in this folder:
- ATTRIBUTION.md: where the game data comes from, its license and what the planner
  doesn't model.
- IN_GAME_CHECKLIST.md: readings testers can take to settle values the sources
  disagree on.
- CLI.md: the command-line guide.
- USER_ARTWORK.md: install a prepared artwork ZIP, preview credits, switch artwork
  or restore the bundled images. User artwork stays in local app data and isn't
  included in this archive or saved builds.
- ARTWORK_PACK_FORMAT.md: the standalone artwork ZIP contract for authors.
- LICENSE.txt and licenses/: this app's license, the game data's CC BY 4.0 license
  and the licenses and notices of every bundled library and font.

Unofficial fan tool. Not affiliated with, endorsed by or sponsored by Ubisoft
or Massive Entertainment. The Division 2 and its content belong to their owners.
This is a tester preview.
