Character Planner 0.2.0-beta.6 — tester release

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

New since 0.2.0-beta.5:
- Enemy spacing (m) on the Simulate tab, under Time on each enemy: with Enemies at
  once, the metres between neighbouring enemies standing on a line. Talents whose
  effects reach other enemies use it; without it they reach nobody, and the result
  says so. It's your assumption about the fight.
- A "Use ... when" row for each skill slot on the Simulate tab, shown once Include
  damaging skills is ticked. Instead of using the skill the moment it's ready, it's
  used only while a talent rule of your build is on (with Drive talent triggers),
  after a time into the fight, or while at least a number of enemies are alive (with
  Enemies at once, and no more than it); until then it waits. The summary shows each
  such skill's uses per fight and how long it waited. It's your assumption about how
  you play. The command-line version has the same as its "skillUse" option; CLI.md
  has the details.

Changes since 0.2.0-beta.5 that move fight results, each only for builds with the
talent:
- In fights with several enemies at once and an enemy spacing set:
  - Regicide: a headshot kill with the Regulus blasts every enemy within 5 m for 400%
    weapon damage. The tooltip's bleed isn't in the game's live script, so there's
    none.
  - Creeping Death (and Perfect): a status your talents put on the target spreads to
    enemies within 8 m every 15 s (10 m every 10 s).
  - Plague of the Outcasts: at a death, the stacks move to the nearest enemy within
    25 m.
  - Indirect Transmission: at a death, the enemy's statuses go on every enemy within
    10 m for half their duration; with the Eclipse Protocol chest's Proliferation,
    within 15 m for 75%.
  - Unnerve: a kill with the Strega marks every enemy within 20 m, up to 5 marks, each
    adding 15% to your shots on that enemy; all of an enemy's marks clear 10 s after
    its last.
  - Bewildered: half of each weapon hit goes to a random enemy within 30 m.
- In fights with several enemies at once and Time on each enemy set: The Trap, on the
  Sacrum Imperium, tags an enemy once you've hit it for 0.5 s without a break, up to
  10. A headshot kill then burns every other tag for 10 s, and the trap waits 30 s,
  10 s less for each later headshot kill. Shooting each enemy until it dies tags only
  that one, so it never springs.
- In every fight with talent triggers, one enemy at a time included: Unnerve's marks
  come from the fight, not the "Unnerve Target Marks" setting, so a fight with nobody
  else to mark has none. The setting still sets static comparisons and fights without
  talent triggers.

Changes in 0.2.0-beta.5 that moved fight results, for testers coming from beta.4:
- Skilled resets skill cooldowns on a skill kill, by chance; with no cooldown running,
  the reset waits and lands 1 s after the next starts, so turret, hive and Sticky Bomb
  builds gain too. Calculated and Tag Team cut skill cooldowns.
- Plague of the Outcasts stacks its debuff on every hit.
- Efficient keeps a used armor kit by chance and doubles the specialization kit
  bonuses.
- Outsider, Preservation and Ignited count a skill's kill as well as the weapon's.
  In the game, kills by turrets, drones and hives don't count for them, or for
  Skilled; the planner counts them, so those builds read high.
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
