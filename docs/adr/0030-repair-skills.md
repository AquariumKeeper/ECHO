# ADR 0030: Repair skills in fights

Status: Done, 2026-11-21: the engine is built, and the rows from `r149.candidate.1` are in. The owner chose the support skills next, starting with those that repair the player; R148's
readings are in, and the owner's calls follow them. From the coverage survey's "Other skills with no fight rule" row
(SIMULATOR_COVERAGE_SURVEY.md).

## Context

Five skills repair the player and have no fight rule, so a build carrying one can't run with skills (`skill-rule-required`):
Restorer Hive, Repair Trap, Reinforcer Chem Launcher, Mender Seeker Mine and Fixer Drone. R128 (ShadeTech,
R128_SOURCE_REVIEW.md) found each needs an incoming repair, an amount per use or charge, or a rate while it lasts.

Fights under enemy fire already repair the player: kits, talent repairs, gear-set repairs and, from ADR 0028, the Leech Hive's
repair per impact (`impactRepair`), which goes to armor, capped, and needs a damaging impact. Fights without enemy fire don't
track the player's armor. A skill is used when ready, or by a skill-use condition (ready, a driven key, after a time, enemies at
least), not by the player's armor.

### R148's readings

R148 (ShadeTech, R148_SOURCE_REVIEW.md, evidence packet `r148-evidence-v1`), read from the source without running it:
- **All five repair the player's armor only,** capped at its maximum, never health, and none revives. Every amount matches the
  pack's, with Repair Skills and Total Skill Repair as separate factors and no incoming-repair term.
- **Restorer Hive:** `repair` per charge, launched only at a player below full armor (a charge waits; at full armor the hive runs
  its whole duration), one repair in flight per player, about `max(0.35, 0.15 × d) + 0.9` s apart, d being the player's distance
  from the hive (8 m reach). The use ends 2 s after the last charge or at `duration_seconds`; the cooldown, **90 s from the end**,
  isn't in the pack. `refill_baseline_seconds` is 11.25 by the source, not 11.
- **Repair Trap:** `deployable_capacity` mines, each repairing once about 0.3 s after a player steps within 0.75 m, needed or not.
  The use ends at the last mine, or 2 s after `duration_seconds`; `cooldown_seconds` runs from that end.
- **Reinforcer Chem Launcher:** one cloud per round, `charge_capacity` rounds, each back in `ammo_recovery_seconds` (the source
  floors it at 8 s; the pack doesn't). `ally_repair` lands at impact on allies, **not the firer** (in-game check pending);
  `repair_over_time` is per second, as half every 0.5 s, to everyone in the 3 m cloud for `cloud_duration_seconds`, firer
  included (inferred; the same in-game check). An aimed shot starts no cooldown.
- **Mender Seeker Mine:** one mine for up to 180 s (not in the pack), following the player; when it reaches a player whose armor
  share is **below 0.9**, an 8 s cloud of `repair_rate` per second (half every 0.5 s), clouds at least 5 s apart and repeatedly.
  The cooldown, **25 s from the end**, isn't in the pack.
- **Fixer Drone:** `repair` once a second while the player's armor is below full and within 4 m (it follows), for
  `duration_seconds`; `cooldown_seconds` from the end.
- **A correction to R144:** the hive repair script waits its travel time **plus 0.8 s**, so the Leech Hive's repair lands
  `max(0.35, 0.08 × d) + 0.8` s after each hit, not with it.
- Four in-game checks (IN_GAME_CHECKS.md): the Mender's trigger and reach; the Mender's repeats; the Reinforcer's impact repair
  on the firer; the Restorer's charges at full armor.

## Decision

1. **A repair skill needs no damage:** its rule names a repair to the player's armor, capped, in fights under enemy fire, per
   charge, mine or tick, or as a rate over a cloud, by new rule fields. The Leech Hive's `impactRepair` is the model for amounts.
2. **Used once armor is below full** (owner, after R148): a fight uses a repair skill only when the player's armor is below its
   maximum, as a player would, by a new skill-use condition; that keeps the Repair Trap's mines and the Reinforcer's clouds, which
   the source spends needed or not, from repairing nothing, and a drone's or mine's duration from running out unneeded.
3. **Without enemy fire** a repair skill has nothing to restore: it steps aside, as a skill with no part in the fight does, so a
   build carrying one runs.
4. **The player is in range** (owner): of the hive, the cloud, the drone and the Mender's cloud, for the whole use, and the Mender
   arrives at once. **The Restorer Hive is 5 m away,** so its repairs come about 1.65 s apart while armor is below full, a pack
   output with text saying it's an assumption, as the Leech's 0.5 s is.
5. **The Repair Trap's mines are spent one at a time when needed** (owner): the player steps on a mine whenever their armor is
   below full. The planner's reading of the source: each repair lands 0.3 s after the step, and the next mine waits for the last
   one's repair (two together might repair only once, R148), so mines are at least 0.3 s apart; mines left at `duration_seconds`
   expire.
6. **Each skill as the source has it:**
   - **Restorer Hive:** a repair per interval while armor is below full, each spending a charge; the use ends 2 s after the last
     charge or at `duration_seconds`; the 90 s cooldown from the end.
   - **Repair Trap:** as point 5; the use ends at the last mine (or 2 s after the duration), the cooldown from the end.
   - **Reinforcer:** each round a cloud, its repair over time to the player for `cloud_duration_seconds` in 0.5 s ticks; rounds
     recover in `ammo_recovery_seconds`, with the source's 8 s floor (point 8). No impact repair to the firer, as the source reads (the in-game check may change it).
   - **Mender Seeker Mine:** an 8 s cloud whenever armor is below 90%, starts at least 5 s apart and no cloud over another, for
     the mine's 180 s; the 25 s cooldown from the end.
   - **Fixer Drone:** `repair` each second while armor is below full, for `duration_seconds`; the cooldown from the end.
   - Kept out: the skills' health (enemies destroying a hive, mine or drone), line of sight, allies, a recall's half cooldown.
7. **The Leech Hive's repair lands later** (owner, after R148's correction): a travel time plus 0.8 s after each impact, by a new
   rule field (seconds from an impact to its repair): 1.2 s at the 5 m the 0.5 s interval assumes (`max(0.35, 0.08 × 5) + 0.8`). Its damage and cadence
   don't move.
8. **The pack** needs, with the rows: cooldowns for the Restorer and the Mender, the Mender's 180 s duration, the Restorer's
   interval, `refill_baseline_seconds` at 11.25, and the 8 s floor on the Reinforcer's `ammo_recovery_seconds`.

## Stages

1. **Readings:** R148, readings only, sent 2026-11-21 (ShadeTech), answered the same day (`97367ea`).
2. **The owner's calls** (2026-11-21, above), then **the engine:** the armor-below condition, repairs per charge, mine and tick,
   repair clouds, and the repair delay.
   - **2a, built** (2026-11-21): rule fields `useArmorBelow` (a share of maximum armor; a ready skill waits until the player's
     armor is below it, and steps aside without enemy fire) and `repairDelay` (seconds from each impact to its `impactRepair`).
     Tested on the Leech Hive with test rows.
   - **2b, built** (2026-11-21): a repair rule (`repair`, `repairEvery`, `repairLag`, `repairBelow`, `repairCount`,
     `repairCloud`) checks the player's armor at the source's own ticks through its window, and repairs, at once, later, or as a
     cloud, when it's below the share and the last repair has landed; a count of repairs spent ends the use early, its cooldown
     from then. Tested on test rows for all five skills. The rows would read:
     - **Fixer Drone:** `repair` every 1 s, at once, through `duration_seconds`; cooldown from the end.
     - **Repair Trap:** `repair`, checks every 0.1 s, landing 0.3 s later, `deployable_capacity` of them, through
       `duration_seconds`; cooldown from the end, the last mine's repair.
     - **Restorer Hive:** `repair`, checks every 0.2 s, landing 1.55 s later (5 m: `max(0.35, 0.75) + 0.8`), `charge_capacity` of
       them, through `duration_seconds`; ends 2 s after the last; a 90 s cooldown from the end (a new output).
     - **Mender Seeker Mine:** `repair_rate` as an 8 s cloud (`cloud_duration_seconds`), checks every 0.5 s below 90%, through a new
       180 s duration; a 25 s cooldown from the end (a new output).
     - **Reinforcer:** `repair_over_time` as a cloud, its window the cloud's, rounds as `charge_capacity` charges back in
       `ammo_recovery_seconds`, 1.5 s apart (the launcher's reload).
     Simplified: the Repair Trap's use ending 2 s after its duration when a mine is left (fights end it at the duration).
3. **Rows,** then their adoption: the five skills' rules, the outputs in point 8, the Leech's repair delay, and The Howl's
   missing `instakill_chance` text. R149 asks for them as `r149.candidate.1` on `r147.candidate.1`, with text and figures (sent
   2026-11-21, ShadeTech). Delivered the same day (`7754796`, R149_HANDOFF.md) and adopted: the six rules as asked,
   the outputs, the text. ShadeTech's figures (six Badger Tuff pieces, target 6) agree with the shapes the planner's tests assert: each repair skill keeps
   the player up longer (18.1 to 24.5 s, against 16.7 s), the Leech's later repair restores more, the Repair Trap spends both mines
   on the first small losses, and the Reinforcer's second round's cloud overlaps the first. Those last two follow the owner's
   calls; ShadeTech notes a lower share would keep the mines for larger losses.

## Consequences

- Builds with a repair skill run with skills where they're refused today.
- Under enemy fire, their repairs keep the player up longer, which moves time to first armor break and downed fights.
- The Leech Hive's repairs land 1.2 s after its hits, so a fight that ends in between loses them.
- No frozen build carries these skills, so nothing frozen moves.
