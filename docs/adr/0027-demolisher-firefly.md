# ADR 0027: Demolisher Firefly in fights

Status: Done, 2026-11-20. The owner chose the Firefly next and made the calls below, the last three after R142's readings; the
engine is built, and the rows from `r143.candidate.1` are in. From the coverage survey's "Damaging skills with no fight rule" row
(SIMULATOR_COVERAGE_SURVEY.md, item 3).

## Context

The Demolisher Firefly is a damaging skill with no fight rule, so a build carrying it can't run with skills
(`skill-rule-required`). R131 (ShadeTech, R131_HANDOFF.md) named what it needs: a target gate, since it only goes
after enemies with weakpoints, mechs, skill objects and explosive props, and a cooldown output (50 s base in the source). Its pack
rows today:
- **`damage`:** one payload on one target, its first intact weakpoint, or its body (armor and health) once none is left.
- **`target_capacity`:** the most targets a use holds, 3 plus a tier addition (13 overcharged).
- **`speed_multiplier`:** a normalized movement speed, not a travel time.
- **No `cooldown_seconds` output.**

Fights' enemy profiles (`enemy_target`) group the source's enemy declarations by role, health and armor. They carry no weakpoint
or enemy-type data, and each profile mixes many kinds of enemy. Fights don't model weakpoints. Skill rules (`simulation.skills`)
already have `impact`, `cooldown`, `impacts` and `impactInterval`, and `area`, but no gate on a selector.

## Decision

1. **The gate is a user setting** (owner, 2026-11-20): a new enemy-target assumption, "Target has a weakpoint or is a mech",
   off by default. The source's own gate lets mechs through too (R142), so the label names both (owner, after R142).
   - **Off:** in a fight with skills, the Firefly steps aside, as the skills in `skillsAside` do (ADR 0022): it is never used,
     and the result says why. A build carrying it runs.
   - **On:** every enemy in the fight is taken as one it can target, and it is used on its cooldown.
   - **The pack** marks the gate on the skill rule: a `requires` on the selector, as trigger rules have. The engine reads it for
     the whole fight. It doesn't drive the selector.
2. **The cooldown** comes from a `cooldown_seconds` output the rows add, in the Burster's shape with base 50 (R142). The source
   starts it at the Firefly's end, after its flight to every target; the data has no flight time, so fights end the use as it's
   launched (`cooldownFromEnd` with nothing after launch), and the cycle is shorter than the source's by the flight.
3. **The payload is full body damage** (owner, after R142): each one takes the `damage` output from armor, then health, as a
   body hit does. R142 found the data can't say what a weakpoint hit takes (its health value type is unwired). The body figure is
   exact for a mech or an enemy whose weakpoints are gone, and an upper bound for one with weakpoints left. The result says so.
4. **Several targets land at launch** (owner, after R142): a use strikes up to `target_capacity` distinct living enemies, the
   enemy being shot first, then the nearest others, one payload each, all as it's used. The source visits them
   nearest first with a flight between, whose time the data doesn't give. One enemy at a time, a use strikes the enemy being shot
   once. This needs a new rule field, since `impacts` lands every impact on one enemy. The others go nearest first, as the
   source visits them, and only where a fight reaches other enemies at all (an enemy spacing, ADR 0013).

## Stages

1. **Readings:** R142 asks ShadeTech, readings only, for the payload's damage on a weakpoint, the targets per use and the time
   between them, the cooldown's base, scaling and start, and the time from launch to first strike. Sent 2026-11-20
   (ShadeTech). Answered 2026-11-20 (ShadeTech, R142_SOURCE_REVIEW.md), readings only:
   - **A weakpoint hit:** the payload goes to the weakpoint's body part, with the health value type unwired, so whether it
     reaches armor or health isn't in the data. What the carrier loses when a weakpoint goes comes from that weakpoint's script
     (the generic explode: a third of its maximum health, inferred).
   - **The body:** with no weakpoint left, the payload goes to armor, then health (`HEALTH_AND_ARMOR`). No area, no status.
   - **Targets:** the ones marked at launch, distinct entities, nearest first with line of sight, one payload each. The use ends
     after the last, whatever the capacity.
   - **Timing:** flight only, with no delay and no speed in metres per second; speed bonuses pick one of ten motion groups.
   - **The cooldown:** `max(3, 50 / ((1 + mods) × (1 + haste)))`, no tier scaling, from the Firefly's end.
   - **Damage stats:** Skill Damage and Total Skill Damage, not Explosive; dealt amplifiers as for other skill objects
     (inferred). The pack's `damage` already has them.
2. **The engine:** the skill rule's `requires` gate and stepping aside when it's unmet, then what the readings need.
   - **The gate** is built (2026-11-20): `simulation.skills` rules take `requires` (PACK_SCHEMA_V2.md). Unmet, the slot steps
     aside with a note, "Run without skills whose setting is off", naming the setting, and a use condition on it is dropped, in
     fights and on the Desktop form alike. Met, the rule fights as it would without the gate. Until the rows came the Firefly had no rule,
     so it was refused.
   - **Several targets at launch** are built (2026-11-20): `simulation.skills` rules take `targets`, naming the output with the
     most distinct enemies a use strikes (PACK_SCHEMA_V2.md). The use strikes the enemy being shot, then the nearest other living
     enemies on the line, one full impact each, as it's used. Others are reached only with an enemy spacing, as every effect on
     other enemies is (ADR 0013); without one it strikes the enemy being shot alone, and the result says so. That's "nearest
     first", as the source visits them. The engine is done; its tests now run on the committed rows.
3. **Rows:** the selector, the cooldown output and the skill rule (with the targets field), then their adoption. R143 (sent
   2026-11-20, ShadeTech) asks for them as `r143.candidate.1` on `r141.candidate.1`, with text and figures.
   Delivered 2026-11-20 (ShadeTech) and adopted: the `target_weakpoint_or_mech` selector, `cooldown_seconds` (base
   50), a new Cooldown roll on the propulsion slot (the source's `firefly_platform_cooldown_mod_Bonus`, 2.5 to 7.5%, as on the
   Burster and Blinder), the `damage` and `target_capacity` text, and the rule. The output has no 3 s floor, which its text notes;
   only about 1,567% combined cooldown speed would reach it. The planner's CLI gives ShadeTech's skill DPS exactly (27,651 one at a time, 82,953 in groups of 4, 3 m apart, with
   the setting on) and the same downed times; total DPS differs by the weapon-side inputs, as in R141.

## Consequences

- With the setting off, which is the default, a build with the Firefly runs with skills where it was refused, and the Firefly
  adds nothing. Its result says the Firefly stepped aside.
- With it on, the fight takes every enemy as a valid target, so a profile that in game holds enemies without weakpoints
  overstates it. That's the user's assumption, named in the result.
- No frozen build carries the Firefly, so nothing frozen moves.
