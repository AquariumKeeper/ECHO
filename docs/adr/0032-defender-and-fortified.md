# ADR 0032: The Defender Drone and Fortified Smart Cover in fights

Status: Done, 2026-11-22: the engine is built (#373), and the rows from `r154.candidate.1` are in (#375). With the live reads asked for (R152), the owner chose the Defender Drone and Fortified Smart Cover
next among the support skills. R153's readings are in, and the owner's calls follow them.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md).

## Context

Neither skill has a fight rule, so a build carrying one can't run with skills (`skill-rule-required`). R128 (ShadeTech,
R128_SOURCE_REVIEW.md) read what each gives:
- **Defender Drone** (`skill_player_drone_guardian_protect`): an `AddCombatModifier` on the protected agent, cutting physical damage
  taken from NPCs (and a second from players) by `drone_guardian_damagereduction_Final`.
- **Fortified Smart Cover** (`skill_player_smartcover_deploy_coverbuff_fortified`): attribute bonuses on agents in cover within its
  radius: bonus armor (`PlayerArmorOverflowFinal`, a share of `MaxPlayerArmor`), blast and pulse resistance.

R128 read both as fitting what fights already read:
- **Damage taken:** enemy hits read the pack's `enemy_damage_taken_factor` in the fight's state, which follows driven keys, so a key
  held while the drone is out can lower each hit.
- **Bonus armor:** bonus armor follows the driven state (ADR 0020), so a key held while the cover is out can grant it, for a player
  in cover (`in_cover`, or a cover cycle's cover).

The pack carries the Defender's duration (20 s), cooldown (20 s) and skill health, and its damage reduction as references
(`damage_reduction_reference_base` 0.2, a tier bonus). It carries the Fortified's duration (30 s), cooldown (45 s), radius, skill
health, and its bonus armor (0.5 base) and resistances as references. Neither has a stat a fight reads, and blast and pulse damage
play no part in fights.

What fights lack: a skill whose use holds a key while it's out. Skill uses turn on keys only through a buff hive's charges (ADR 0031).
A use holding a key for its window, without charges, is the engine piece this needs.

## Questions for the source (R153)

1. **The Defender:**
   - What the reduction cuts: every hit on the player from NPCs (bullets, explosives, statuses' ticks), or only some.
   - How it combines with the player's own damage resistance and `protection_from_elites`.
   - When it starts and ends: at deployment, after the drone reaches the player, for the duration.
   - Whether the drone's health drains with the damage it stops, ending it early, and if so by how much.
   - Whether its cooldown runs from the use or from its end.
2. **The Fortified Smart Cover:**
   - The bonus armor's amount: a share of maximum armor, by the tier and mods, as `bonus_armor_reference` reads.
   - When it's given: once as the player enters cover, or held while in cover. Whether it refills, and what happens to it on leaving
     cover or as the cover ends.
   - Whether "in cover" is any cover within its radius, the cover it deploys, or both, and what a fight should assume.
   - Whether the cover's own health ends it early under fire.
   - Its cooldown, from the use or from its end.
3. **Both:** what the pack would need as stats a fight reads, and whether either changes anything else a fight plays.

### R153's readings

R153 (ShadeTech, R153_SOURCE_REVIEW.md, evidence packet `r153-evidence-v1`), read from the source without running it:
- **Defender Drone:** a `Taken`, `Multiplicative` combat modifier on the player, `1 − drone_guardian_damagereduction_Final`, on NPC
  physical projectile damage only: bullets, not blasts, status ticks or melee. The amount is `MIN(1, (0.2 + 0.05 a tier) × (1 +
  mod + 0.01 an Expertise grade))`: 20% at tier 0, 50% at tier 6, 80% overcharged. It sits outside `DamageTakenModFinal` and its 90%
  cap, read as a further factor beside resistance and protection from elites (inferred). On from the first 1 s check with the drone
  within 5 m, until the drone ends; nothing in the data moves the stopped damage to the drone, so it ends early only if it dies, is
  disrupted or cancelled. 20 s, cooldown 20 s from its end; the pack matches, without the 3 s floor.
- **Fortified Smart Cover:** bonus armor, `(0.5 + 0.1 a tier + the Bonus Armor roll) × MaxPlayerArmor` (50% at tier 0, 110% at tier
  6, 150% overcharged), a flat amount added once on the first 1 s check that finds the player in cover (any cover within its radius),
  once per entry. Damage spends it and nothing refills it while the player stays; what's left goes on leaving cover or as the cover
  ends (inferred); re-entering gives it again in full. Its own 1,000 health ends it if killed; whether enemies aim at it isn't in the
  data. 30 s, cooldown 45 s from its end; the pack matches, without the 3 s floor. Its blast and pulse resistance reach nothing fights
  play.
- **Both:** a pack modifier can't read a skill's mod roll, so neither modifier carries its skill's roll (as R151 found for the
  Booster). Eleven in-game checks (IN_GAME_CHECKS.md).

## Decision

The owner's calls (2026-11-22, after R153), and the plan they settle:

1. **The Defender cuts every enemy hit** (owner): fights' enemy hits are their guns' bullets. While the drone is out each hit is
   multiplied by `1 − cut`, after resistance and protection from elites, outside the 90% cap; status ticks (target 5's bleed) aren't
   cut. The drone is within 5 m throughout, lasts its 20 s (nothing drains it), and its cooldown runs from its end.
2. **The Fortified gives bonus armor per cover entry** (owner): once as the cover deploys with the player in cover, spent by damage,
   kept until the cover's end; out of cover, nothing. The cover lasts its duration. **A cover cycle is one stay in cover** (owner,
   2026-11-22, after R154): peeking from cover is still being in cover (ADR 0009 holds `in_cover` through a cycle, and enemy hits
   land only while peeking), so a cycle gets the single grant as the cover deploys, not one each return behind cover. An earlier
   reading of this call, a grant at each return to cover with leaving taking what's left, would have taken the bonus away exactly
   while hits land.
3. **Mod rolls are noted, not modelled** (owner): fights use each skill's base and tier, with a limitation note; a skill's modifier
   reading its own outputs is a later engine change.

## Plan

1. **Readings** (R153, readings only), sent 2026-11-22 (ShadeTech).
2. **The owner's calls** (2026-11-22, above), then **the engine:** a skill use holding a key for its window (and ending with the
   skill), its key gating the pack's modifiers: the Defender's a factor on enemy hits outside the resistance cap; the Fortified's
   bonus armor of a share of maximum armor, given on each cover entry while the key and cover hold, and taken on leaving.
   - **2a, built** (2026-11-22): a new trigger event `skill_out`. A skill whose rule has a window may own a `buff` on it with no
     duration; the fight fires it as the skill deploys and holds the key until the use ends, or its lengthened end (an Artificer's
     refresh). Such a rule needs only its window and cooldown, and without talent triggers the skill steps aside. Tested on test rows:
     a Defender's key gating a 20% cut through `enemy_damage_taken_factor` (each hit 0.8× while out); a Fortified's key with
     `in_cover` gating half of maximum armor as bonus armor (granted once as it deploys in cover, none out of cover); an Artificer
     lengthening a Defender's buff to 44 s. The grant needs no engine work: state bonus armor already grants a rise, lets damage
     spend it and takes what's left on a fall.
3. **Rows,** then their adoption. R154 sent 2026-11-22 (ShadeTech): both rules and `skill_out` trigger rules with
   their keys, a Defender cut stat in `enemy_damage_taken_factor`, the Fortified's `bonus_armor_percent` gated on its key and
   `in_cover`, text, figures, as `r154.candidate.1` on `r152.candidate.1`. Delivered the same day (`5148bc6`, R154_HANDOFF.md) and
   adopted: the rules and keys as asked, `defender_drone_cut` (0.2 + the tier's step, capped at 1) multiplying
   `enemy_damage_taken_factor`, the Fortified's `bonus_armor_percent` (0.5 + the tier's step) on its key and `in_cover`, text.
   ShadeTech's figures (six Badger Tuff, target 6): a hit with the Defender costs 0.82 of one without it, and the player is downed
   4.3 s later; the Fortified in cover grants half of maximum armor once and the player is downed 6.3 s later.
   - **The cover cycle:** ShadeTech found that a cover cycle holds `in_cover` on for the whole fight (ADR 0009), so with a cycle
     the Fortified grants its bonus once, at deployment. The owner settled it that way (point 2): a cycle is one stay in cover.
     The pack's text on `bonus_armor_reference_base` still describes a grant at each return to cover; R155 asks for it corrected (sent 2026-11-22, ShadeTech,
     text only, with the Defender's text on a raised shield). Delivered the same day (`60f0ad8`) and adopted (#377).
   - **Hits on a shield:** a raised shield takes the enemy's raw hit, so the Defender's cut, like resistance, doesn't reach what a
     shield absorbs.

## Consequences

- Builds with a Defender Drone or Fortified Smart Cover run with skills, where they were refused.
- Each lowers what enemy fire does while it's out, so fights under enemy fire last longer.
- No frozen build carries these skills, so nothing frozen moves.
