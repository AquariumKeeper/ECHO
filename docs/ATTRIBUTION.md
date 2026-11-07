# Attribution and limits

Character Planner is an unofficial fan tool. It is not affiliated with, endorsed by or
sponsored by Ubisoft or Massive Entertainment. Tom Clancy's The Division 2 is a trademark
of Ubisoft Entertainment; game content and artwork belong to their owners.

## Game data

Gear, weapon, attribute, set, talent, skill and weapon-mod compilations are adapted from
[Division 2 Community Data Hub game data](https://github.com/div2hub/game-data), licensed
under [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/)
(`licenses/division2-data-CC-BY-4.0.txt`). That license covers the Hub's compilation, not
Ubisoft or Massive game content.

The planner changes the data: it renames source IDs, converts percentages to fractions,
maps slot compatibility to planner choices, turns set and mod entries into stat
modifiers, and applies reviewed corrections. Corrections cite Ubisoft's published patch
notes and research checked against in-game readings.

Other community references:

- Skill damage and cooldown baselines: the community Division 2 gear spreadsheet (Skill
  List, "Last Updated by IC3SO TU27").
- Armor and health baselines: the
  [level-40 armor table](https://www.f-space.de/mdwiki/spiele/division2.html) and the
  [Division 2 gear calculator](https://github.com/ImThatTeriyaki/Tom-Clancy-The-Division-2-Gear-calculator).

## Artwork

The released app ships no game artwork. The application icon and the pack's eight slot
images are original work by Shade-Tech contributors under the MIT license
(`licenses/shadetech-artwork-MIT.txt`); the pack's `presentation.json` records each
image's source. Everything else (talents, skills, specializations, items) is shown with
symbols the app draws itself, which are original to this project and share its license.
More original artwork from the data project is planned.

Earlier development packs registered game artwork, which belongs to Ubisoft / Massive
Entertainment. It was never included in a release, and the adopted pack no longer
registers any of it.

## Software

The app's own code is under the MIT license (`LICENSE.txt` in a release). It is built
with .NET and Avalonia and ships their runtime libraries; the licenses and notices of
everything bundled are in the release's `licenses/` folder, listed in its README.

## What the planner estimates

The planner estimates configured builds and the conditions you set in the Assumptions
tab, and its combat simulator plays them through timed fights. Known limits:

- **Each result lists its own limits.** Read them: they say what that fight assumed.
- **Weapon falloff:** fights at a set distance use each weapon's source floor, falling in
  a straight line from optimal to long range. Six weapons have no source floor, and the
  line's shape needs in-game readings. Fights with no distance have no falloff.
- **Stat screen estimates:** Build Time to Kill and similar figures outside a fight ignore
  headshots, falloff, cover and enemy resistances.
- **Enemies:** cadence and hit rates are estimates. Enemies stay at their distance, and
  which enemy you shoot follows a fixed pattern. Talents that choose a target, or
  that write to enemies you aren't shooting, keep their assumptions. Skills, swaps and
  kits have no priority list: a skill is used as soon as it's ready.
- **Skills:** each use reads your build as it deploys and keeps that. Only reviewed skill
  mods are offered, but most mods and many formulas come from the game files and haven't
  been read in game, and some
  look odd, such as hives counting Skill Health twice. Overcharge isn't in fights, and a
  shield stays fixed during one. Some Expertise aggregation, Hive charge recovery,
  Reviver armor stacking and support-buff aggregation are unresolved.
- **Talents:** some set and talent interactions aren't modeled; an unmodeled effect keeps
  its description.

`IN_GAME_CHECKLIST.md` lists the in-game readings that would settle the open values.
