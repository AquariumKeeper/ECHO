# ADR 0031: Buff hives in fights

Status: Done, 2026-11-21: the engine is built (#363 to #365), and the rows from `r151.candidate.1` are in. With the repair skills' rows asked for (R149), the owner chose the Booster and Artificer Hives next
among the support skills. R150's readings are in, and the owner's calls follow them.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md).

## Context

Two hives buff the player and have no fight rule, so a build carrying one can't run with skills (`skill-rule-required`). R128
(ShadeTech, R128_SOURCE_REVIEW.md) read what each gives:
- **Booster Hive** (`skill_player_hive_deploy_delayed_effect_stim`): weapon handling (`WeaponHandlingModBonus`), melee damage,
  status resistance (`AllStatusResistancePercentageFlatBonus`) and status cures.
  - **Weapon handling** reaches fights: the pack's Combined Reload Speed is `reload_speed + weapon_handling`, and its Combined Swap
    Speed `swap_speed + weapon_handling`, so it shortens reloads, and swaps in rotation fights. Its accuracy and stability parts
    play no part.
  - **Status resistance and cures** would act on the statuses enemies put on the player (ADR 0018: target 5's bleed, resisted by
    `hazard_protection` and `bleed_resistance`), but fights read those resistances once, as the fight starts.
  - **Melee** plays no part in fights.
- **Artificer Hive** (`skill_player_hive_buff_tech`): the platform damage bonuses of chem launchers, turrets, drones, seeker mines,
  fireflies and the Deflector's ricochet, and the repair skills' repair bonuses, on each skill of a player it reaches. Its pack
  outputs also name a skill repair share and a skill refresh time ("a refreshed deployment timer"), which may repair other skills'
  health (fights don't model skill health) or extend a deployed skill's duration (which would raise a turret's damage).

The pack carries both hives' charges, duration, range and refill baseline. Their buff amounts, the Artificer's buff duration, skill
repair share and skill refresh seconds are only references, not stats a fight can read; the Booster has no buff duration output,
and neither hive has a cooldown output.

Fights have timed buffs on the player held by keys a pack modifier reads, but only trigger events turn them on (`skill_hit` and
`skill_kill` among them), and no skill use or charge fires one; the engine adds that path. A skill's amounts are read as it deploys
from the fight's state, and a live rate reads them as it goes, so a buff on then raises them. The hives' charges, cadence and end can
reuse the Leech Hive's engine (ADR 0028) and the repair skills' (ADR 0030), which waits on a state and schedules what follows.

### R150's readings

R150 (ShadeTech, R150_SOURCE_REVIEW.md, evidence packet `r150-evidence-v1`), read from the source without running it.
Both hives are branches of the Restorer's and Leech's deploy script: the 0.2 s tick, charges, the end 2 s after the last charge or
at the duration, and the cooldown from the end.
- **Booster Hive:** each charge, on arrival, cures 11 statuses once, then gives Weapon Handling, melee damage and Hazard Protection,
  each `buff_amount_reference` (0.2 at tier 0, 0.6 at tier 6, 1.0 overcharged), for 5 s (10 s at tier 6; no pack output). Any
  friendly player in range (12 m, line of sight) is a target, with no damage test; one stim at a time, so the next charge goes as the
  last ends, about every `max(0.35, 0.07 × d) + 5 s` plus a tick at tier 0 (inferred; about 5.45 s on average at 5 m), needed or
  not. Hazard Protection adds to every
  per-status resistance (capped at 1).
- **Artificer Hive:** charges go only to the player's deployed skills (a turret, drone, seeker, firefly, hive), or to the player
  while a Shield or Chem Launcher is active; an agent with an effect running is skipped, and with nothing to reach the hive keeps
  its charges. Each lands `max(0.35, 0.15 × d)` after launch at tier 0 (d the target skill's distance from the hive; the tiers
  shorten the flight) and gives the owner, for 10 s, `buff_amount_reference` (0.1, +0.1 a tier) inside that skill family's damage
  bracket and, where the family has one, its repair skill's healing bracket (drones the Fixer, hives the Restorer, seekers the
  Mender, a Chem Launcher the Reinforcer); repairs the target skill's own health; and adds `skill_refresh_reference_seconds` (3 s,
  +2 a tier) to its remaining duration. Whether bonuses stack isn't in the data. While the Artificer is holstered, a separate graph
  buffs the player's skills every 0.5 s without spending charges (inferred): its search is around the player, not a hive, and its
  landing delay reads the distance in metres as seconds (inferred).
- **Both** read the Booster's duration (180 s) and cooldown (90 s, from the hive's end) attributes; neither has `cooldown_seconds`
  in the pack. The Artificer's `skill_repair_reference` lacks its tier and repair stats; both refill baselines are 11.25, not 11.3.
  Neither landing adds the repair script's 0.8 s.
- Six in-game checks (IN_GAME_CHECKS.md).

## Decision

The owner's calls (2026-11-21, after R150), and the plan they settle:

1. **A buff hive's charge turns on a buff for its duration,** as a key a pack modifier holds: on the player's stats for the
   Booster, on the build's other skills for the Artificer, so the amounts come from the pack.
2. **The Booster as the source has it, 5 m away** (owner): used when ready, a stim at a time, the next charge as the last ends,
   about every 5.55 s at tier 0 (0.35 s flight, the 5 s stim, up to a 0.2 s tick), needed or not, until its charges are spent.
3. **The Booster's weapon handling only** (owner): it shortens reloads and swaps. Hazard Protection and the cures stay out, with a
   note (the planner's reading of R150): only target 5's bleed would differ.
4. **The Artificer's bonus and refresh** (owner): each charge gives the 10 s damage (or repair) bonus to the target skill's family
   and adds its refresh seconds to that skill's remaining duration (a running use extended). It goes only to a deployed skill of the
   build's (a use whose window runs), or to the player while a Shield or Chem Launcher is active, and waits otherwise. The skill
   health repair stays out (fights don't model skill health). **The target skill is 5 m from the hive** (owner, as for the
   Booster): a charge lands 0.75 s after launch at tier 0. **A second charge refreshes, not stacks** (owner): the bonus restarts its
   10 s at the same amount; an in-game check would settle it.
5. **The Artificer while holstered** (owner): between its uses, it buffs the player's deployed skills every 0.5 s without spending
   charges, as R150 infers, each landing by its own delay (the distance read as seconds), not the hive's flight.
6. **Without anything to raise** (an Artificer with no other skill that takes its bonuses), the hive steps aside, as a skill with no
   part in the fight does.
7. **The pack** needs, with the rows: `cooldown_seconds` (90 s) for both, the Booster's stim duration, the Artificer's buff amount
   and duration and refresh as stats a fight reads (or keys), and refill baselines of 11.25.

## Stages

1. **Readings:** R150, readings only, sent 2026-11-21 (ShadeTech), answered the same day (`34189e3`).
2. **The owner's calls** (2026-11-21, above), then **the engine:** a skill charge turning on a key, a running use extended, and the
   holstered pulses.
   - **2a, built** (2026-11-21): the Booster's part. A rule field `chargeFor` times a buff hive's charges as a repair skill's, with
     no armor test; each charge lands as `skill_charge`, which only the skill's own trigger rules hear (an active skill may now own a
     `buff` rule on it), and its key gates the pack's modifiers. Without talent triggers the hive steps aside. Tested on test rows: a
     Booster's eight 5 s stims (40 s of uptime), its weapon handling speeding reloads. The rows would read: `chargeFor` the stim's
     seconds (a new output, 5 s at tier 0), `repairEvery` 0.2, `repairLag` 0.35 (5 m), `repairCount` `charge_capacity`, `window`
     `duration_seconds`, a new 90 s `cooldown_seconds` from the end, `endDelay` 2; a selector for the stim and the skill's buff rule
     on `skill_charge`; and the skill's weapon handling modifier gated on it.
   - **2b, built** (2026-11-21): the Artificer, deployed. Rule fields `chargeSkills` and `refresh` send its charges to the other
     slots' running uses (a use whose window runs, not a control skill's status window or a shield), each use again `chargeFor`
     after the last landing on it (the tech script's end, read as one flight after landing: 0.75 s at 5 m). A charge landing on a use
     still running fires `skill_charge` for the hive's 10 s buff (a second restarts it) and adds `refresh` seconds to the use: its
     window and status run longer and a cooldown from its end starts later. A chem launcher (the player's, R150) takes the buff but no
     refresh, since what a player's refresh reaches isn't in the data. With no skill to reach, the hive steps aside (point 6). Tested
     on test rows with a 10 s Assault Turret: eight charges 1.6 s apart lengthen it to 34 s, its cooldown from then. Left out: the
     Shield path (fights hold no skill shield as a use, and the Deflector's ricochet isn't modelled), skill health, and the holstered
     pulses (2c). The rows would read as the Booster's, with `chargeSkills`, `refresh` `skill_refresh_reference_seconds`,
     `repairLag` 0.75 and a `chargeFor` output of the flight's seconds; the pack's modifiers on each family's damage and repair
     gated on the buff's key.
   - **2c, built** (2026-11-21): the Artificer while holstered. Rule fields `holsteredEvery` (0.5 s) and `holsteredLag` (the
     distance read as seconds: 5 s at 5 m) pulse whenever the hive isn't out, each sending a charge to every running use that may
     take one, spending none; it lands as a deployed charge does, and the use waits one lag more after it (the tech script's end, as
     for the deployed hive). The wait is the target's, shared by both paths, as the source's mark is on the target. Tested on test
     rows: after the hive's use ends at 14.7 s, pulses at 15 s and 25 s lengthen the turret from 34 s to 40 s. Left out: the pause a
     status puts on the timer (the status isn't named in the data). The rows would add `holsteredEvery` 0.5 and `holsteredLag` 5.
3. **Rows,** then their adoption. R151 sent 2026-11-21 (ShadeTech): both hives' rules and trigger rules, the modifiers
   gated on their keys (the Booster's weapon handling; the Artificer's bonus in each family's damage and repair brackets, with which
   reads go live), `cooldown_seconds`, the Booster's stim seconds, the Artificer's flight seconds and `refill_baseline_seconds` 11.25,
   as `r151.candidate.1` on `r149.candidate.1`, with how the tiers' stim and flight would be carried. Delivered the same day
   (`ae05544`, R151_HANDOFF.md) and adopted: the rules as asked, the Booster's buff reading a new `booster_stim_seconds` stat so it
   follows the tiers; `booster_stim_active` gating `weapon_handling`, and `artificer_bonus_active` a new `artificer_skill_bonus` that
   19 skills' damage and repair formulas read in their first (or status) bracket; the cooldowns, stim and flight outputs and 11.25 s
   refills. The Artificer's `repairLag` stays 0.75 s at every tier (tiers above 0 land up to 0.4 s late). No read was made live:
   ShadeTech would make the Oxidizer (by the source) and the Fixer (inferred) live, and names the Assault Turret the owner's call,
   since with its rate read at deployment the Artificer adds no damage to a turret already out (live: +9.6% skill DPS with no hits).
   ShadeTech's Booster figure agrees with the tests (40 s of stims at tier 0, +4.1% DPS without hits); its Artificer run (98.8% of
   the fight with the bonus, the turret's 180 s use outlasting the fight) has no test of its own, which use a 10 s turret.
   **Live reads** (owner, 2026-11-21, after R151): the Assault Turret, Oxidizer and Fixer read their amounts live, so the bonus
   reaches a use already out; the turret's is inferred (the Artificer exists to buff skills already out), an in-game check would
   settle it. The engine reads a repair rule's amount live too (`rateRead` on a `repair`, #368, for the Fixer): at the check that
   makes each repair, so a delayed repair, or a cloud's ticks, keep what their check read, and a bonus landing in a cloud reaches its
   next one. R152 asks for the rows (sent 2026-11-22, ShadeTech: `rateRead` live on the three, text, an in-game check
   for the turret, figures). Delivered the same day (`2c61676`, R152_HANDOFF.md) and adopted: the three live reads and text. Beside
   the Artificer, the turret's skill DPS rises 9.6%, the Oxidizer's 6.0% (its bonus holds only while clouds run), the Fixer's armor
   repaired 9.9%. Before R152 the bonus reached only a use deployed while it was on, which a 10 s bonus rarely met (the Booster and
   Stinger can't sit beside the Artificer, both being hives). The live reads also let a driven talent's skill damage or repair
   (Combined Arms, Spike) reach a turret, cloud or Fixer already out, which they didn't before.

## Consequences

- Builds with a Booster or Artificer Hive run with skills where they're refused today.
- The Booster raises sustained DPS through shorter reloads and swaps. The Artificer lengthens the other skill's use; its bonus raises
  that skill's damage or repair only for a use deployed while it's on, until the live reads (R152) let it reach a use already out.
- No frozen build carries these skills, so nothing frozen moves.
