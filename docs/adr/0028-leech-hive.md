# ADR 0028: Leech Hive in fights

Status: Done, 2026-11-21. The owner chose the Leech Hive next; R144's readings and the owner's two calls after them settled the
decision below. The engine is built, and the rows from `r145.candidate.1` are in. From the coverage
survey's "Damaging skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md, item 3).

## Context

The Leech Hive is a damaging skill with no fight rule, so a build carrying it can't run with skills (`skill-rule-required`).
R131 (ShadeTech, R131_SOURCE_REVIEW.md) read it:
- **Each charge is one leech:** an instant BLAST hit on the highest-threat hostile in range with line of sight, for
  `hive_leech_damage_Final`, with no bleed.
- **Then a repair:** a fixed `damage × 0.8` to a damaged player or player-owned agent in range. That's the pack's `repair`; it
  doesn't depend on the damage dealt.
- **Why no row:** the time between hits is flight time, which wasn't read, and fights have no field for a repair to the player
  per impact.

Its pack outputs: `damage`, `repair`, `charge_capacity` (5 plus a tier addition), `duration_seconds` (120 s), `range_meters`
(12 m) and `cooldown_seconds` (60 s base). Skill rules already have `impacts` (several impacts in one use) with `impactInterval`
(the seconds between them), and `cooldownFromEnd`.

Fights under enemy fire already repair the player: armor kits, talent repairs (Preservation, Doctor Home) and gear-set repairs
(ADR 0021) restore armor through the fight's armor, which the enemy's hits take. Fights without enemy fire don't track the
player's armor, so a repair there changes nothing.

## Decision

1. **Each charge is an impact** on the enemy being shot, through `impacts: charge_capacity` and `impactInterval`, the first at
   deployment. R144: each charge searches again for the highest-threat enemy in range; fights strike the enemy being shot, as
   other skills do. The hit takes armor, then health.
2. **The interval is a fixed 0.5 s** (owner, after R144). The source's is `max(0.35, 0.08 × the hive-to-player distance)` plus up
   to a 0.2 s tick, and fights don't know where the player stands. 0.5 s is about 5 m; a pack output carries it, with text saying
   it's an assumption.
3. **Each charge's repair** is the pack's fixed `repair` (0.8 × the nominal hit), to the player's **armor only**, capped at its
   maximum, after each impact, in fights under enemy fire, by a new rule field naming the output. A charge is spent whether or not
   the player needs repair, and the impact never waits (R144). Fights without enemy fire count the damage only, and say so. The
   source lands the repair a travel time after the hit; fights land it with the hit, 0.5 s early at most.
4. **The use ends 2 s after its last charge** (owner, after R144), or at `duration_seconds` if sooner, and the 60 s cooldown runs
   from its end (`cooldownFromEnd`), by a new rule field: seconds between a use's last impact and its end.
5. **The damage takes dealt amplifiers** (R144, inferred, as Gas Bomber's does since R137): the rows add
   `skill_damage_multiplier` to `damage`. No Skill Damage, Total Skill Damage or Explosive term.

## Stages

1. **Readings:** R144, readings only, sent 2026-11-20 (ShadeTech). Answered 2026-11-20 (ShadeTech,
   R144_SOURCE_REVIEW.md):
   - **Cadence:** one hit at a time, each a travel time `max(0.35, 0.08 × hive-to-player distance)` plus up to a 0.2 s tick
     apart; the first at the first tick after deployment. The enemy's distance doesn't enter it.
   - **Targets:** each charge searches again, the highest-threat hostile within range with line of sight.
   - **The hit:** `HEALTH_AND_ARMOR` on an enemy; no skill damage terms; dealt amplifiers apply (inferred).
   - **The repair:** fixed, to the player's armor only, capped, unscaled; a charge is spent whether or not it's needed.
   - **The use:** ends 2 s after the last charge or at its duration; the cooldown (60 s base) runs from its end; one charge refills
     in `cooldown / charges`.
2. **The owner's calls** (2026-11-20): a fixed 0.5 s interval and a 2 s end delay. Then **the engine:** a repair per impact and
   an end delay on skill rules.
   - **Built** (2026-11-20): `simulation.skills` rules take `impactRepair` (an output name) and `endDelay` (seconds)
     (PACK_SCHEMA_V2.md). Under enemy fire each landed impact repairs the player's armor by the output, capped, through the path
     kill repairs take; without enemy fire the result notes it has nothing to restore. A use with `cooldownFromEnd` ends
     `endDelay` after its last impact. Tests run the hive on test rows shaped as R145 will ask for them. A build without gear has
     no armor, so the repair restores nothing there, as in the source.
   - **Left aside:** the use's end at `duration_seconds` when that comes first. With 0.5 s between charges, 20 charges (overcharged)
     take 10 s against a 120 s duration, so it never does.
3. **Rows:** the interval output, `skill_damage_multiplier` in `damage`, and the skill rule, then their adoption. R145 (sent
   2026-11-20, ShadeTech) asks for them as `r145.candidate.1` on `r143.candidate.1`, with text and figures.
   Delivered 2026-11-21 (ShadeTech) and adopted: `leech_interval_seconds` (0.5), `damage` × `skill_damage_multiplier`,
   text on `damage`, `repair` and `charge_capacity`, and the rule. The planner's CLI gives ShadeTech's no-gear figures for skill
   DPS (33,181), armor repaired (0) and the downed time (4.6 s) exactly. `overcharge_damage` didn't carry
   `skill_damage_multiplier` (ShadeTech's note); R147 (ShadeTech) asked for it, and `r147.candidate.1` (adopted with
   ADR 0029's rows) adds it, so an overcharged Leech's static estimate takes dealt amplifiers too.

## Consequences

- A build with the Leech Hive runs with skills where it's refused today.
- Under enemy fire, its repairs keep the player up longer, which moves time to first armor break and downed fights.
- No frozen build carries the Leech Hive, so nothing frozen moves.
