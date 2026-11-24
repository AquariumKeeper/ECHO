# ADR 0029: The Howl in fights

Status: Done, 2026-11-21. The owner chose The Howl next; R146's readings and the owner's three calls after them settled the
decision below. The engine is built, and the rows from `r147.candidate.1` are in. From the coverage survey's
"Damaging skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md, item 3).

## Context

The Howl is a damaging skill with no fight rule, so a build carrying it can't run with skills (`skill-rule-required`). R131
(ShadeTech, R131_SOURCE_REVIEW.md) read it:
- **The wave:** after a 0.7 s wind-up it grows to `pulse_howl_mark_radius_Final` (20 m) in two 0.5 s steps.
- **Each human hostile in line of sight, once per use:** shock for `pulse_howl_shock_duration_Final`; one PHYSICAL `ARMOR_ONLY`
  hit for `pulse_howl_armor_damage_Final`, with no falloff; and an outright-kill roll at `instakill_chance`, for non-players read
  as non-elites (inferred), below a health test against `pulse_howl_grunts_instakill_armor_threshold_Base` (1). A target that
  passes dies 1.5 s later.
- **Guardians** are only staggered. The mark is a 20 s highlight with no damage.
- **Why no row:** the shock fits `controlStatus`; the hit needs armor-only damage; the kill needs a chance with its eligibility;
  Guardians need an exclusion.

Its pack outputs: `armor_damage`, `radius_meters`, `shock_duration_seconds`, `mark_duration_seconds`, `instakill_chance` (0.5 at
tier 0) and `cooldown_seconds` (35 s base). Fight enemies come from `enemy_target` profiles, each labelled with a role (Normal,
Veteran, Elite, Named) and its health and armor; fights have no Guardians. Skill rules already have `impact`, `area`,
`controlStatus` (shocked) and `window`.

## Decision

1. **The shock** is a control status, as other shocks are: `controlStatus: shocked` for `shock_duration_seconds`, with the area,
   on every enemy in reach (fights have no Guardians).
2. **The hit takes armor only:** a new rule field marks an impact as armor-only, so it stops at the enemy's armor and never
   reaches health; an enemy without armor takes nothing. Every enemy in the area takes it in full.
3. **The outright kill** is a roll per enemy per use at `instakill_chance`, after the armor hit, on an enemy that is:
   - **below full health** as the wave reaches it (R146: the test is health share below 1, so an untouched enemy never dies
     outright, and the armor hit can't make it eligible);
   - **of an eligible profile:** Normal and Veteran, not Elite or Named (owner, after R146, following ShadeTech's inference; the
     in-game check can widen it). The rows mark the eligible profiles.

   A passed roll kills the enemy 1.5 s later, whatever happens in between, unless it's already dead. **No talent credit** (owner,
   after R146): the enemy dies and counts in kills and damage, but the kill fires no `kill` or `skill_kill` event, since the
   source's kill has an invalid combat source. The in-game check can change it.
4. **Timing by distance** (owner, after R146): the effects reach an enemy 0.7 s after the use within 12.5 m, and 1.2 s beyond,
   by the fight's enemy distance where it has one, 0.7 s otherwise. The cooldown runs from 1.2 s after the use, as the wave
   reaches full size.

## Stages

1. **Readings:** R146, readings only, sent 2026-11-21 (ShadeTech). Answered 2026-11-21 (ShadeTech,
   R146_SOURCE_REVIEW.md):
   - **The kill's test:** health share below 1 (`HealthPercent` < the threshold 1), despite the attribute's "armor" name; full
     health can't die outright.
   - **Eligibility:** non-player and not elite (inferred from the pin); Guardians are only staggered. Normal and Veteran in,
     Elite and Named out (inferred).
   - **The roll and death:** once per enemy per use, after the armor hit; death 1.5 s later by `KillAgent`, with the player as
     user but an invalid combat source, so whether talents hear it isn't in the data.
   - **The armor hit:** `ARMOR_ONLY`; takes Skill Damage and Total Skill Damage; the pack's `armor_damage` matches.
   - **Timing:** 0.7 s wind-up; the wave covers 12.5 m at 0.7 s and 20 m at 1.2 s; the cooldown starts at about 1.2 s; the pack's
     `cooldown_seconds` matches.
   - **The shock:** every human in reach with line of sight except Guardians, elites and named included.
2. **The owner's calls** (2026-11-21): Normal and Veteran eligible, no talent credit, timing by distance. Then **the engine:**
   armor-only impacts, the outright kill with its eligible profiles, and the delay by distance.
   - **Built** (2026-11-21): `simulation.skills` rules take `armorOnly`, `impactDelay`, `farImpactDelay` with `farFrom`, and
     `cooldownAfter` (PACK_SCHEMA_V2.md). An armor-only impact stops at armor on every enemy it reaches. A use's effects all land
     the delay after it, the far delay when the fight's one enemy distance is beyond `farFrom`; the cooldown counts from the use,
     plus `cooldownAfter`. With per-target distances the near delay holds, and the result says so. Tests run The Howl on test
     rows without its shock or kill: nothing on an enemy without armor, 0.5 s later at 20 m than at 10 m, uses 36.2 s apart.
   - **The outright kill, built** (2026-11-21): `simulation.skills` rules take `killChance`, `killProfiles` and `killDelay`
     (PACK_SCHEMA_V2.md). After the hit, each enemy the impact reached whose profile is listed and whose health is below full
     rolls once; one that passes dies the delay later, through the path other kills away from the player take, with no kill repair
     and no kill event. Tests run The Howl on test rows with a sure kill: an untouched enemy never dies outright, a shot Normal
     dies 1.5 s after the hit, and the same enemy with its profile unlisted never does.
3. **Rows:** the skill rule and any outputs, then their adoption. R147 (sent 2026-11-21, ShadeTech) asks for them as
   `r147.candidate.1` on `r145.candidate.1`, with text and figures; the rule it gives passes the planner's validator and runs.
   Delivered 2026-11-21 (ShadeTech) and adopted: the rule as asked, text on `armor_damage`,
   `shock_duration_seconds` and `radius_meters` (the handoff lists `instakill_chance` too, but the delivered row has no
   description; a later request can add it), and the Leech Hive's `overcharge_damage` × `skill_damage_multiplier`. The
   planner's CLI gives ShadeTech's target 6 figures (skill DPS 4,048, downed at 7.7 s) exactly. Their figures show what the readings
   imply: profiles 1 and 3 have no armor for the hit to take, and an enemy being shot dies to the weapon before a kill 1.5 s later
   lands, so against them The Howl adds mostly its shock.

## Consequences

- A build with The Howl runs with skills where it's refused today.
- Against eligible enemies the outright kill can end fights far sooner than damage would, so its eligibility matters more than
  its damage.
- No frozen build carries The Howl, so nothing frozen moves.
