Character Planner 0.2.0-beta.9 — tester release

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

New since 0.2.0-beta.8:
- Nearly every skill now runs in fights with skills on. A build with any of these was
  refused before. (Skills with talent triggers need enemy fire, as before.)
  - Damage: Blackout Seeker, Gas Bomber, Leech Hive, The Howl and the Demolisher Firefly,
    which is left out unless the new "Target has a weakpoint or is a mech" setting is on.
  - Repairs, used once your armor is below full and left out without enemy fire: Fixer
    Drone, Repair Trap, Restorer Hive, Mender Seeker Mine, Reinforcer.
  - Buffs and defense, left out without talent triggers: Booster and Artificer Hives,
    Defender Drone, Fortified and Precision Smart Covers (which need you in cover), Trophy
    System. The Precision's damage bonus also needs "Target out of cover".
  - Decoy, which draws enemy fire until its health or its time is spent.
  - Jammer Pulse and EMP Sticky Bomb: with talent triggers they disrupt enemies, and a
    disrupted machine (profile 2) stops firing. They deal no damage here.
  - Achilles Pulse: it marks the enemy you're shooting. The new "Body Hits on Marked
    Zones (%)" setting (0 by default) says how many of your body hits there deal
    headshot damage, until the zones' health is spent: about 4 hits with a plain ACR
    against profile 6, so 50% and 100% come out much the same. With talent triggers the
    mark sets "Target is pulsed".
  - Reviver Hive, left out without enemy fire: going down with it out no longer ends the
    fight. You're out for about 3 s, fire landing on a downed pool, then back at full
    health with part of your armor, safe for 5 s. It works once a hive, and strong fire
    can finish you first.
  - Scanner Pulse, Remote Pulse and Tactician Drone are left out of the fight, which
    now runs without them.
  Striker and Bombardier Drones and Shrapnel and Corrosive Traps are still refused.
- These fights were refused and now run:
  - Under enemy fire: System Corruption, Future Initiative, Umbra Initiative, and Symbiosis
    (the Oxpecker), whose drain breaks its shield.
  - Under enemy fire with talent triggers: Hotshot, Bloodsucker, Kill Confirmed, and System
    Corruption with armor kits, where Hackstep is the kit and its damage bonus follows your
    bonus armor.
  - With talent triggers: Overflowing (it counts reloads from empty), Close & Personal,
    Restrained without a shield, Salvage, Determined, Headhunter, and Finisher and
    Gunslinger without a weapon rotation.
  - In a weapon rotation: Police UMP-45, The Ravenous, Cooler, Prima Donna, Busy Little Bee
    and Police 686 Magnum.
- Fights with talent triggers list each active gear set's settings that no fight drives,
  with their values, such as "Striker's Battlegear (Striker's Gamble Stacks: 0)". Set them
  on the Assumptions tab if you want them counted.
- Poison and disrupt are statuses in fights, so talents that react to any status see them.
- Enemy-fire readings add, where they happen: hits removed (Trophy System), hits a decoy
  drew, revives, kit ability uses (System Corruption) and shield drained (Symbiosis). A
  fight with the Achilles Pulse adds its hits on marked zones.
- New settings on the Assumptions tab for estimates outside a fight, which fights set
  themselves: Booster Hive stim, Artificer Hive bonus, Defender Drone out, Fortified and
  Precision Smart Cover out, Trophy System Drone out, and Back and Forth's swap-from bonus.
  Versatile has a range band. "Headache Current Armor Pool" no longer does anything, and
  "Hackstep Qualifying Bonus Armor (%)" is now "Hackstep Bonus Armor (%)".
- The Demolisher Firefly's propulsion slot offers a Cooldown roll, and several skills show
  more estimates, such as the cooldowns of the Reviver, Restorer, Booster, Artificer and
  Mender. The six weapons above show long and maximum ranges, and all but the Police 686 a
  reload with rounds left.

Changes since 0.2.0-beta.8 that move stats, previews and estimates, and any fight that
uses them:
- Hackstep Protocol's damage bonus multiplies all weapon and skill damage, 0.4% a point
  of bonus armor up to 20%. It was added to total weapon damage, 2% for each whole 5%.
- Headache's bonus armor cap is a share of your maximum armor, and its bonus goes on a
  headshot with any weapon.
- Loaded for Bear's Afterburn, from the game's own update: up to 30 stacks (was 20), each
  dealing 13% of a pellet's weapon damage (was 100%). ShadeTech's ACR run lost about four
  fifths of its Afterburn damage.
- Transference Overclock's Hive haste, 15% a Skill Tier (105% overcharged), shortens every
  hive's cooldown; no estimate read it before. At tier 6 it divides a cooldown by 1.9. No
  hive's cooldown is under 3 s.
- Prima Donna's reload is 4.99 s (was 4.29), so fights with it lose about 7%.
- Magazine sizes: base magazine bonuses (Overflowing, Pakhan) are raised by Magazine
  Size % too, so an ACR with a 20-round mod, a Legatus piece and Overflowing holds 92
  rounds, not 87.
- Headhunter's estimate is sized by one pellet and capped, so a shotgun's drops to about
  an eighth.
- Leech Hive's and Gas Bomber's damage take amplifiers such as Glass Cannon, and Combat
  Medic's 45% is in the Reviver Hive's cooldown, duration and health.
- The Reinforcer's ammo recovery is never under 8 s, the Fixer's and the Reinforcer's
  repairs are named per second, and the Restorer's, Booster's and Artificer's refill
  baseline is 11.25 s.
- Listed as partly modelled now, which changes no number: Breadbasket, Glass Cannon and
  Salvage (and their Perfects), Hackstep Protocol, Chemical Agent and Perturb.

Changes since 0.2.0-beta.8 that move fight results:
- In fights with talent triggers:
  - Headache's bonus and repair come from your marksman rifle's headshots.
  - Chemical Agent: ensnared no longer counts for it; a status on you adds 2 stacks a
    second; and a kill at 12 stacks of an enemy with a status turns Reactant on for 5 s.
  - Head Scratcher confuses enemies itself, and Banshee Pulse's disorient counts for it.
  - Pakhan's stacks come from kills.
  - An Assault Turret or Oxidizer already out follows a skill-damage talent as it turns
    on or off.
- In fights with a weapon rotation and talent triggers:
  - Unwavering, Back and Forth, Perturb, Faster Than Reloading and Versatile come from the
    swaps, not from their settings.
  - A swap's time and the reload after it read what the swap itself changed, so a bonus
    the swap ends no longer speeds it.
- At a set fight distance, Versatile's bonus goes only to the weapon class in its band.
  Police UMP-45, The Ravenous, Prima Donna, Busy Little Bee and Police 686 Magnum lose
  damage past their optimal range and deal none past 100 m, where they kept it all.

New in 0.2.0-beta.8, for testers coming from beta.7:
- Fights with enemy fire have their own default target (profile 3, an ordinary enemy).
  On the Assumptions tab, "Default" follows that rule; picking profile 6 keeps it. A
  build saved in beta.7 with profile 6 picked shows Default: pick it again.
- Statuses an enemy's hit puts on you: the enemy-fire readings add the status damage you
  took and how long each was on you.
Changes in 0.2.0-beta.8 that moved fight results:
- In fights with talent triggers: Cover Shooter stacks come from shots in a cover cycle
  (without one they stay at 0, whatever "Cover Shooter Stacks" is set to, unless "In
  cover" is on); Calculated's cut needs cover; Doctor Home works on both weapons of a
  rotation.
- Against profile 5 (the nail gun), each hit on you bleeds you for 2 s.

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
