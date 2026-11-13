Character Planner 0.2.0-beta.7 — tester release

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

New since 0.2.0-beta.6:
- Packs load faster, so each command-line call takes about a fifth less time.

Changes since 0.2.0-beta.6 that move fight results, each only for builds with the
skill or talent:
- In fights with several enemies at once and an enemy spacing set:
  - Skill blasts reach every enemy within the skill's radius of the one you're
    shooting: both Sticky Bombs, the Explosive and Cluster Seeker Mines, the
    Firestarter's ignition and the Artillery Turret's shells. The Sticky Bombs deal
    full damage within half their radius and the Seekers within 1 m, then fall in a
    straight line to 10% (Sticky Bombs) or 50% (Seekers) at the radius's edge. The
    straight line is an assumption: the game gives the ends, not the curve. The
    blast's burn or bleed goes on each enemy it reaches. The Airburst Seeker's blast
    is a cone, so it still hits one enemy.
  - Creeping Death (and Perfect) and Indirect Transmission also spread or pass on a
    skill's own burn or bleed, such as a Burn Sticky Bomb's, carrying the skill's own
    damage, for one application of it.
- In fights with talent triggers: the Shock Trap, Riot Foam, Banshee Pulse and
  Blinder Firefly put their shock, ensnare, disorient or blind on the enemy you're
  shooting when used; the Shock Trap's and Riot Foam's reach every enemy within their
  radius too. A shocked enemy stops firing, and talents that react to statuses, such
  as Wicked, now see these. A Shock Trap shocks once per use here, where the game lays
  several mines.
- At a set fight distance over 20 m, Dragon's Glare no longer burns.

New in 0.2.0-beta.6, for testers coming from beta.5:
- Enemy spacing (m) on the Simulate tab, under Time on each enemy: with Enemies at
  once, the metres between neighbouring enemies standing on a line. Effects that
  reach other enemies use it; without it they reach nobody, and the result says so.
- A "Use ... when" row for each skill slot on the Simulate tab (the command-line
  version's "skillUse" option): a skill is used only while a talent rule is on, after
  a time into the fight, or while at least a number of enemies are alive.
Changes in 0.2.0-beta.6 that moved fight results:
- In fights with several enemies at once and an enemy spacing set: Regicide's blast,
  Creeping Death's spread, Plague of the Outcasts' and Indirect Transmission's
  pass-ons, Unnerve's marks and Bewildered's split of each hit reach other enemies.
- In fights with several enemies at once and Time on each enemy set: The Trap burns
  the enemies you've tagged on a headshot kill.
- In every fight with talent triggers: Unnerve's marks come from the fight, not the
  "Unnerve Target Marks" setting.

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
