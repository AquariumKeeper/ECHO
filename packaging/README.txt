Character Planner 0.2.0-beta.8 — tester release

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

New since 0.2.0-beta.7:
- Fights with enemy fire have their own default target: an ordinary enemy (profile
  3: 1,200 health, no armor), the one the game files declare most often (which
  isn't a spawn rate). Elsewhere the default stays profile 6, and a Mixed targets
  sequence sets its own profiles. On the Assumptions tab, the Enemy Target Profile
  list starts with "Default", which follows that rule; picking profile 6 keeps it
  in every fight.
- Statuses an enemy's hit puts on you: the Simulate tab's enemy-fire readings add the
  status damage you took (0 when none went on) and, for each status that went on, how
  long it was on you.

Changes since 0.2.0-beta.7 that move fight results:
- Fights with enemy fire whose target is at Default now fight profile 3, so you go
  down later than against profile 6. A build saved in beta.7 with profile 6 picked
  saved it as the default, so it shows Default now: pick profile 6 again to keep it.
- In fights with talent triggers:
  - Cover Shooter (Big Alejandro) with a cover cycle: each bullet fired adds 1%
    weapon damage, up to 100%. All stacks end together 15 s after the first, and a
    kill in cover with Big Alejandro restarts that timer; a completed reload or a
    swap clears them.
    Without a cover cycle the stacks stay at 0, whatever "Cover Shooter Stacks" is
    set to, unless "In cover" is set on.
  - Calculated (and Perfect) with "Calculated Cut" set: in a fight that isn't in
    cover, the cut no longer applies.
  - Doctor Home on both weapons of a swap rotation: each weapon's marks now drop its
    armor kit on a kill, as with Doctor Home on one weapon.
- Fights with enemy fire against profile 5 (the shield carrier's nail gun): each hit
  that lands on you (not on a raised shield) bleeds you for 2 s, about 23,000 every
  half second at the pack's skill curve, through bonus armor, armor then health.
  Hazard protection and bleed resistance shorten it, to no less than 0.5 s.
  It speeds the down only where you outlast several nails. No other profile puts a
  status on you. The bleed's slow and blocked regeneration aren't modelled.

New in 0.2.0-beta.7, for testers coming from beta.6:
- Packs load faster, so each command-line call takes about a fifth less time.
Changes in 0.2.0-beta.7 that moved fight results:
- In fights with several enemies at once and an enemy spacing set: skill blasts
  (both Sticky Bombs, the Explosive and Cluster Seeker Mines, the Firestarter and the
  Artillery Turret) reach every enemy within their radius, with a straight-line
  falloff (an assumption), and Creeping Death and Indirect Transmission spread or
  pass on a skill's own burn or bleed. The Airburst Seeker's cone still hits one.
- In fights with talent triggers: the Shock Trap, Riot Foam, Banshee Pulse and
  Blinder Firefly put their status on the enemy you're shooting, so talents such as
  Wicked see it, and a shocked enemy stops firing. A Shock Trap shocks once per use.
- At a set fight distance over 20 m, Dragon's Glare no longer burns.

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
