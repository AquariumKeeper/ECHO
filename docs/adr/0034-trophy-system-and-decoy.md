# ADR 0034: The Trophy System Drone and Decoy in fights

Status: Done, 2026-11-22; engine built (#383, #384), rows from `r159.candidate.1` adopted. With ADR 0033 done, the owner chose the Trophy System Drone and Decoy next among the support skills.
R158's readings are in, and the owner's calls follow them.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md).

## Context

Neither skill has a fight rule, so a build carrying one can't run with skills (`skill-rule-required`). R128 (ShadeTech,
R128_SOURCE_REVIEW.md) read them:
- **Trophy System Drone** (`skill_player_drone_trophy_system`, `ReflectorDroneNode`): reflects a share of incoming projectiles
  (`drone_trophy_system_reflection_rate_Final`, 32% at tier 0) within its range, and intercepts thrown projectiles (40%). The
  reflection's damage to enemies is native and wasn't read.
- **Decoy** (`skill_player_decoy_update`): `SkillScriptAddThreat` with `Decoy_Distraction_Threat_Final` on hostile agents near it, so
  enemies fire at the decoy instead.

R128 read both as changing how much fire reaches the player: a fight would need a share of incoming fire removed while the skill
lasts. Fights don't have that: an enemy's hit chance is the user's "enemy hit rate" setting, and a modifier on incoming DPS is refused
(it can't be split between cadence and hit chance). What fights do have:
- **A skill holding a key while out** (`skill_out`, ADR 0032), its key gating the pack's modifiers.
- **Blocked attempts:** behind cover in a cover cycle, an attempt is blocked (`EnemyShotBlocked`), so a share of attempts that never
  reach the player has an event and a report already.
- **No thrown projectiles:** enemy hits are their guns' bullets, so the Trophy's intercept plays no part.

The pack carries both skills' durations, cooldowns and skill health, the Trophy's reflection rate and range and intercept chance and
range, and the Decoy's threat multiplier.

## Questions for the source (R158)

1. **The Trophy System:**
   - What a reflection does to a bullet: the player takes none of it, or some; whether every bullet within range is a candidate, at
     what rate, and whether the rate is a chance per bullet or a cap per second.
   - What ends the drone early: reflections draining its health, its duration.
   - Whether reflected bullets damage the enemy, and how much (the native node), if anything in the data says.
   - Its range against a fight's distances, and its cooldown, from the use or from its end.
2. **The Decoy:**
   - How threat decides who an enemy fires at: whether a decoy takes all fire from enemies near it, a share, or the highest-threat
     target, and for how long; whether `threat_multiplier` scales that.
   - Its health: whether fire on it ends it early, and how fast at a fight's enemy damage.
   - Its range, duration and cooldown (from the use or its end).
3. **Both:** what a fight would need as stats (a share of hits removed while out, say), and any in-game checks.

### R158's readings

R158 (ShadeTech, R158_SOURCE_REVIEW.md), read from the source without running it:
- **Trophy System Drone:** the reflection is native (`ReflectorDroneNode`), fed the owner, a range and a rate read at deployment.
  Whether a reflected bullet spares the player, and whether the rate is a chance per bullet or a share of its damage, aren't in the
  data; the script puts no damage-taken change on the player. Rate `0.32 × (1 + tier step)` (0.80 at tier 6, 0.88 overcharged),
  range 8 m at tier 0; no mod roll or Skill Efficiency. Ends at its duration (10 s at tier 0) or the drone's death; reflections
  don't drain it; cooldown 50 s from its end. Reflected damage to the shooter has no amount in the data. Its intercept of thrown
  projectiles plays no part in fights.
- **Decoy:** no share of fire in the data, only threat: every hostile within 70 m gets `1200 × threat_multiplier` threat toward it,
  every 5 s, and a quarter of that less toward the player. It takes 0.2 of NPC damage and 0.4 of an elite's (inferred), so its
  health (929,072 at tier 0) runs out in 8.8 s under one target 6 hitting every shot, inside its 15 s. Cooldown 25 s from its end.
- **Neither** changes anything else a fight plays. Eleven in-game checks.

## Decision

1. **The Trophy removes a share of hits** (owner, 2026-11-22): a reflected bullet is read as sparing the player, a chance per
   bullet. While the drone is out, each landing enemy hit is removed at the reflection rate, as a blocked attempt, every bullet in
   range. Reflected damage to enemies stays out.
2. **The Decoy draws fire until it dies** (owner): while it's out, enemy attempts hit it instead, at 0.2 of their damage (0.4 an
   elite's), until its health runs out or its duration ends; then fire returns to the player.

## Plan

1. **Readings** (R158, readings only), sent 2026-11-22 (ShadeTech).
2. **The owner's calls** (above), then **the engine:**
   - **2a, built** (2026-11-22): a pack stat `enemy_hit_removed_share` (0 to 1), read in the fight's state, removes that share of
     landing hits before they reach the player, as blocked attempts. It's read off the hit's own roll (the lowest share of the
     landing range), so no draw moves. Tested on test rows: a Trophy's key gating 0.32 removes about a third of hits for its 10 s
     and none after, and the hits it lets through and removes are exactly those the same fight without removal lands.
   - **2b, built** (2026-11-22): a rule field `decoyHealth` (an output) makes a skill draw fire: while its use runs and its health
     lasts, landing enemy hits land on it instead of the player, each spending the stat `decoy_damage_share` of its raw damage
     (the whole hit without the stat); spent, its use ends there and its cooldown from the end starts. Drawn hits count apart
     (`DrawnHits`). Tested on test rows: a Decoy draws every hit for its 15 s at difficulty 1; with 100,000 health it's spent after
     21 hits at 0.2 each (3.9 s) and the next hit is the player's; without the share stat, after 4 or 5; each use, driven or static,
     draws fire from full health. Simplifications: behind cover the attempt is blocked as cover blocks it and peeking's share
     applies, as for a hit on the player; an attempt at the moment the decoy deploys goes to the player.
3. **Rows,** then their adoption. R159 sent 2026-11-22 (ShadeTech): both rules, the Trophy's `skill_out` trigger rule
   and key, `enemy_hit_removed_share` raised by the reflection rate while out, `decoy_damage_share` (0.2, 0.4 elite), text, figures,
   as `r159.candidate.1` on `r157.candidate.1`. Delivered (ShadeTech, R159_HANDOFF.md) and **adopted**: the rows as
   asked; `decoy_damage_share` is `enemy_target_role_index >= 3 ? 0.4 : 0.2` (named counting as elite inferred). ShadeTech's
   figures (the ACR with six Badger Tuff, target 6 at difficulty 1, 20 fights of 60 s): downed at 16.65 s alone; the Trophy removes
   7.7 hits and the player is downed at 19.98 s; the Decoy draws 39.5 hits, its 15 s ending before its health, downed at 31.92 s.

## Consequences

- Builds with a Trophy System Drone or Decoy would run with skills, where they're refused today.
- Each lowers how much enemy fire reaches the player while it's out.
- No frozen build carries these skills, so nothing frozen moves.
