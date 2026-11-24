# ADR 0037: The Reviver Hive in fights

Status: Done, 2026-11-24; engine built (#401), rows from `r166.candidate.1` adopted. With ADR 0036 done, the owner chose the Reviver Hive next among the skills with no fight rule.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md).

## Context

The Reviver Hive has no fight rule, so a build carrying it can't run with skills (`skill-rule-required`). R128 (ShadeTech, R128_SOURCE_REVIEW.md) read that it runs `ReviveAgent` (`ReviveFromDowned`) on a downed agent in range. Whether it revives
its own user wasn't in those graphs: the filter sits in the deploy script
(`skill_player_hive_deploy`), which ShadeTech has since read for other hives, naming its revive branch. An in-game check is listed (IN_GAME_CHECKLIST.md).

The pack carries its charges (1 plus the tier's), duration (180 s base), range (8 m base), health (796,347 base), refill baseline
(180 s) and revive armor (0.25 of maximum, the tier's and mod's bonus a separate reference; ShadeTech's TURRET_HIVE_MODS_SOURCE_REVIEW
has `0.25 + hive_revive_armor_percentage_mod_Bonus`, without the tier), and Expertise on range. It has no skill
cooldown output, as the Restorer, Booster and Artificer had none before their rules.

What fights have:
- **Going down ends the fight:** at 0 health the fight stops, its result divided by the full duration. Fights report how many were
  downed and when (`DownedFraction`, `TimeToDown`). ADR 0005 has no revive.
- **No allies,** so nobody else can be revived; for fights only a revive of the user can matter.
- **Repairs to the player** (ADR 0030) restore armor only and never a downed player.
- **Skills stepping aside** (ADR 0022): the pack names a skill playing no part, and a fight runs without it.

## Questions for the source (R165)

1. **Its own user:** whether the deploy script's revive branch allows it.
2. **Downed, alone:** whether a solo agent has a downed state at all, its health and bleed-out, and whether enemy fire reaches it.
3. **Timing:** from going down to the revive, and any invulnerability around it.
4. **What comes back:** armor (its share and the tier's part of it), health, shield; and what is lost.
5. **After:** the weakened debuff a revive sets.
6. **Charges and cooldown:** per revive, the use's end, the cooldown's length and start.
7. **Scaling,** the hive as a target, and in-game checks.

### R165's readings

R165 (ShadeTech, R165_SOURCE_REVIEW.md), read from the source without running it:
- **It revives its own user,** by a path of its own in the deploy script, needing no allies: the user downed or dead, not already
  being revived, within the hive's range, and a charge left. The hive must be out already: its deployment forces the downed state
  (`ForceDownedState`, used nowhere else scanned, so a solo player without one dies at 0 health, inferred).
- **It takes every charge:** one self-revive a hive. The hive ends about 4 s after the revive and its cooldown, 180 s base at any
  charge count, runs from then (16.4 s overcharged). The pack's charges matter only to allies.
- **Downed:** a pool of half of maximum health, bleeding out in 45 s, with damage taken × 0.5 (inferred) and a maximum of 0.4 of
  health whose meaning isn't in the data. That enemy fire reaches a downed player is suggested, not stated.
- **Timing:** up to 0.2 s to notice, then four drones sent 0.5, 1, 1.5 and 2 s later, each flying max(0.35, 0.15 × distance × the
  tier's flight factor): the revive comes as the fourth arrives, or 0.8 s later by a failsafe (inferred).
- **What comes back:** full health (the engine's, inferred) and `min(1, 0.25 + tier + mod)` of maximum armor, 0.1 a tier.
- **After:** the dictionary has 5 s of immunity, read by no scanned file, so the engine applies it (inferred). The Weakened debuff is
  the 30 s "downed in a row" window (inferred), with no cut to anything in the data.
- **Its user's other hives end** as the user goes down. A build carries one hive (one variant of a family), so it can't arise.
- Ten in-game checks.

## Decision

The owner's calls (2026-11-23), each the recommendation:
1. **A fight goes on after the revive,** one a use, and ends at the next down unless the hive is out again. `DownedFraction` and
   `TimeToDown` keep counting fights a down ended; revives are reported apart.
2. **Fire goes on while down,** at half damage on the downed pool, with the bleed-out. Spent before the revive, the fight ends.
3. **5 s of immunity** after the revive.
4. **Timers run on:** no shots and no skill uses; buffs and statuses keep their time; a reload or armor kit under way is cancelled;
   bonus armor is gone (it's spent before health); other skills carry on.

## Plan

1. **Readings** (R165, readings only), sent 2026-11-23 (ShadeTech, addendum `b118d3f`: parts earlier readings
   already give, to confirm for this hive), answered `e321e0b`.
2. **The engine, built** (2026-11-23): the rule field `revive` (PACK_SCHEMA_V2.md) names the armor share's and the seconds' outputs and
   the pool, damage share, bleed-out, immunity and end delay. Going down with a use running spends it: no shot or skill use until the
   revive, a reload (a top-up in cover too) started over then, a swap not begun waiting, a kit stopped unspent, hits and status ticks on the pool; then back at
   full health and the armor share, immune. The use's cooldown starts `endAfter` s after the revive. Without enemy fire it steps aside.
   Results gain `revives` where a skill revives, and the trace `revived` and `died`. Tested on test rows over the committed pack.
3. **Rows, adopted** (R166, sent 2026-11-24, ShadeTech, answered `8a38ba0`): the rule, `cooldown_seconds`,
   `revive_armor_share` and `revive_seconds` (5 m, the fourth drone's arrival, which R166 reads as the revive), Combat Medic's 0.45 in the
   hive's cooldown, duration and health, and text.

## Simplifications

- **In range:** the player is taken to be within the hive's reach, at 5 m, as for the Restorer (ADR 0030); the seconds' output says it.
- **The fourth drone's arrival** is the revive (inferred), without the 0.8 s failsafe. The row's output decides it.
- **Transference Overclock's Hive haste** is in the cooldown, as in every hive's, from `r167.candidate.1` (R167, the owner's call).
- **The pool's 0.4-of-health maximum** on damage isn't modeled, nor is crawling's cost.
- **A downed player stays where they went down:** cover's cycle blocks or lets fire through as it did then, though its clock and key
  run on. A shield raised while down takes no hits until the revive.
- **The immunity** stops hits and ticks on the player. A raised shield still takes hits, and statuses on the player run on unharmful.
- **What's restored** at the revive, the armor share and the whole of health, is in the fight's armor and health repaired.
- **A reload started over** is a reload of its own to talents and counts (Overflowing can gain twice from one magazine).
- **Talents don't react** to damage taken while down, and whole-second rules (Symbiosis's drain too) and pickups due then are missed, as for a downed player
  today. A pickup is lost, where the game might leave it lying.
- **A second down within 30 s** isn't death outright unless no hive is out.
- **A fight ending while down** isn't counted as downed: it ran its whole length.
- **The bleed-out** isn't in damage taken; hits and ticks on the pool are, at what they take of it.
- **Combat Medic's** effects on a revive (damage resistance, Skill Tier, Overcharge) and what the game may take from a downed player (stacks, buffs, a shield) aren't modeled.

## Consequences

- Builds with a Reviver Hive run with skills, where they were refused; under enemy fire, strong fire can take the pool first.
- One frozen build, shotgun-close, carries it (with a Bulwark Shield). Its two runs with skills in the frozen-build comparison stay
  refused whatever the hive does, for the shield (`skill-shield-unsupported`), and its other runs use no skills, so nothing frozen moves.
