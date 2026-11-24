# ADR 0036: The Achilles Pulse in fights

Status: Done, 2026-11-23; engine built (#396), rows from `r163.candidate.1` adopted. With ADR 0035 done, the owner chose the Achilles
Pulse next among the skills with no fight rule.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md).

## Context

The Achilles Pulse has no fight rule, so a build carrying it can't run with skills (`skill-rule-required`). R128 (ShadeTech, R128_SOURCE_REVIEW.md) read its deploy graph (`skill_player_pulse_achilles_on_deploy`): it marks zones on one enemy's
weak points and armor parts (`WeakPoint1`…, `HeadArmor`…), each with its own health (`pulse_achilles_health_final`), and shows UI on
hits. What a broken zone does isn't in that graph, and nothing there reads `pulse_achilles_damage_base` (35). An in-game check is
listed (ShadeTech's IN_GAME_CHECKS.md).

(R162 corrects two points: the 35 was the next definition's, so `pulse_achilles_damage_base` has no value; and the graph only passes
the zones' health on, spawning its UI as the pulse lands, not on hits.)

The pack carries its cooldown (40 s base), zone capacity, effect duration (10 s base), zone health (398,174 base) and Expertise
(+1% damage per grade, by the workbook), but no damage effect.

What fights have:
- **No enemy body parts:** hits are headshots or body hits. The Demolisher Firefly's gate is the user's "Target has a weakpoint or
  mech" setting (ADR 0027).
- **Damage modifiers gated on keys,** with a skill holding a key while out (`skill_out`, ADR 0032).
- **A skill's own pool** spent by hits (the Decoy's health, ADR 0034).

## Questions for the source (R162)

1. **What a zone does:** more damage on a marked zone, or on the enemy once a zone breaks; how much (`pulse_achilles_damage_base`
   35 as a percent?); weapon or skill damage; armor, health or both.
2. **Breaking:** whether only hits on that part spend a zone, and what a break does.
3. **Which enemy and how many:** one enemy per use, `zone_capacity` zones, enemies without weak points or armor parts.
4. **Timing:** any delay before the mark, `effect_duration_seconds` as its life, and the cooldown from the use or the end.
5. **Scaling:** tier, mods, Expertise, Skill Damage, Status Effects.
6. **Talents:** whether a mark counts as a status.
7. **For fights:** what they'd need, and in-game checks.

### R162's readings

R162 (ShadeTech, R162_SOURCE_REVIEW.md), read from the source without running it:
- **A zone hit** takes `SkillAchillesPulseDamageModFinal`, `max(1, headshot multiplier × (1 + pulse_achilles_damage_mod_Bonus))`,
  which the game's engine applies (inferred): no scanned data file reads it. The headshot multiplier is the pack's
  `1 + weapon_total_headshot_damage`, apart from its cap and a last factor. Armor or health, skill hits, allies' hits and a marked
  head aren't in the data; the multiplier is the weapon in hand's, which suggests weapon hits.
- **Zones' health** is passed to the engine, one number for the list, read as each zone's (inferred). Nothing in the graph spends it
  or listens for a break; the bonus ending with it is inferred.
- **The enemy and parts:** one hostile aimed at within 60 m. Ordinary enemies get zones too (head, body, arms and legs beside weak
  points and armor parts), picked at random, as many as `zone_capacity`.
- **Timing:** the mark lands at the animation's `Spawn` event, with no charge, for `effect_duration_seconds`. The cooldown starts 5 s after
  it lands. A use that finds no target costs 5 s instead.
- **Scaling:** tiers and mods never raise the damage. Expertise adds 0.01 a grade to the bonus, multiplying the whole headshot
  multiplier; Alternating Current and Tend and Befriend write it too (inferred). Skill Damage and Status Effects don't reach it.
- **Talents:** the mark is a pulse, not a status (inferred): Spotter, Flatline and Heartstopper read it. Whether a zone hit counts as
  a headshot isn't in the data.
- Nine in-game checks.

## Decision

The owner's calls (2026-11-23), each the recommendation:
1. **The share of body hits on a zone is a setting,** 0% by default, beside "Headshots among Hits". At 0% the Achilles Pulse adds no
   zone damage.
2. **A zone's bonus ends when its health is spent:** each zone hit's damage, the bonus included, spends one pool of zones × health.
   Emptied, the bonus ends until the next use.
3. **A zone hit stays a body hit** for events and talents, dealing headshot damage only.
4. **The mark sets "Target is pulsed"** on the marked enemy while it lasts. Set by the user, it stays on throughout.

## Plan

1. **Readings** (R162, readings only), sent 2026-11-22 (ShadeTech), answered `c40bcbb`.
2. **The engine, built** (2026-11-23): the rule field `zones` (PACK_SCHEMA_V2.md) names the zones' count and health, the share and
   multiplier stats, Expertise's factor and the pulsed key. A use marks the enemy being shot for its window. A zone hit's bonus adds
   to critical damage, as a headshot's does. The pack's existing `cooldownAfter` (5 s) gives the cooldown's start. Tested on test rows
   over the committed pack: in the mark's 10 s every body hit deals what a headshot does (critical or not) and after it what a body
   hit does; Expertise 30 multiplies by 1.3; the share and the pool limit zone hits; an Average fight takes the mean; no headshot
   event fires; Spotter follows the mark; both go with the enemy, and wait on it under focus fire; the second use comes at 45 s.
3. **Rows, adopted** (R163, sent 2026-11-23, ShadeTech, answered `c1d134c`, its addendum on Breadbasket `ce13fc5`): the share's setting
   (`practical_zone_share`) and stat, the multiplier stat, the rule with `cooldownAfter` 5, and text. The factor is
   `1 + 0.01 × skill_upgrade_rank + achilles_pulse_damage_bonus`: Alternating Current and Tend and Befriend reach it and add to
   Expertise (inferred). The pack's assumptions put both talents' bonuses on both skills, as their Skill Damage.

## Simplifications

- **Headshot-only amplification** (`headshot_damage_multiplier`: In Plain Sight, Shakedown, Headache) multiplies the whole headshot in
  the pack, critical damage included. A zone hit takes none of it, so with one on, a zone hit and a
  headshot differ. R163's reading agrees: In Plain Sight's and Headache's are modifiers on the part struck and Shakedown's is separate
  damage on a head hit, none of it in `DamageMultiplierHeadFinal` (a zone on another part taking none is inferred). Headache's waiting
  bonus isn't spent by a zone hit. The source caps the multiplier at 10, which isn't modeled.
- **Headshot damage a headshot spends:** Breadbasket and Perfect Breadbasket gain a stack of headshot damage on each body hit and a
  headshot spends them. A zone hit takes headshot damage in its multiplier and stays a body hit, so it builds the stacks, takes them and
  spends none; a headshot, a reload or their time still ends them. Shakedown's stacks, which a zone hit doesn't take (above), build the
  same way. R163's addendum (`ce13fc5`) keeps the multiplier: Breadbasket's bonus is inside `DamageMultiplierHeadFinal`, so by the
  formulas it reaches a zone hit. The game's stacks go by the part struck (inferred): a zone on the body or arms builds one, on the legs, a
  weak point or an armor part none, and on the head spends them. Fights build one on every body hit. Whether the engine reports a zone hit
  as a head hit, which would spend them, isn't in the data. Both Breadbaskets' coverage says so (`partial`, R164).
- **Skills used on a marked enemy** keep what they read as they deployed (Spotter's skill bonus, say), as they do for a status on it.
- **All zones are one pool,** spent in turn; which part a hit lands on isn't played, nor the random pick of parts.
- **Damage dealt** spends the pool: after the enemy's armor and health factors, and without what a redirect moves elsewhere.
- **Weapon hits only.** Skill hits, allies and a marked head take no part; a headshot stays an ordinary headshot.
- **Armor and health** both take the bonus, as headshot damage does.
- **A use with no enemy being shot** (a kill's gap) marks nobody and costs its whole cooldown, as other control uses there are lost;
  the game would charge 5 s and the player could use it again.
- **No range:** the 60 m reach isn't checked.
- **Pulsed to the mark's end,** whatever its zones have left: whether a break ends the pulse isn't in the data.
- **A static fight** (no talent triggers) plays the zones but not the pulsed key, which stays the user's.

## Consequences

- Builds with an Achilles Pulse run with skills, where they were refused.
- No frozen build carries this skill, so nothing frozen moves.
