# ADR 0033: The Precision Smart Cover in fights

Status: Done, 2026-11-22: the engine is built (#379), and the rows from `r157.candidate.1` are in (#381). With ADR 0032 done, the owner chose the Precision Smart Cover next among the support skills. R156's
readings, the owner's call, the engine piece (stage 2) and R157's rows followed.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md).

## Context

The skill has no fight rule, so a build carrying it can't run with skills (`skill-rule-required`). R128 (ShadeTech,
R128_SOURCE_REVIEW.md) read `skill_player_smartcover_deploy_coverbuff_precision`: on agents in cover, `TargetOutOfCoverDamageModBonus`
(damage to targets out of cover) and `WeaponHandlingModBonus`, plus an instant reload.

What fights already have:
- **A skill holding a key while out** (`skill_out`, ADR 0032), whose key gates the pack's modifiers, here together with `in_cover`
  as the Fortified's does. A cover cycle is one stay in cover (ADR 0032, the owner's call).
- **Damage to targets out of cover:** the pack's `out_of_cover_damage_factor` multiplies weapon damage by `1 + dtoc` while the
  "Target out of cover" setting (`enemy_out_of_cover`) is on.
- **Weapon handling** feeds Combined Reload Speed and Combined Swap Speed (as the Booster's stim does, ADR 0031).
- **A reload:** a trigger rule's `refill` fills the magazine as it fires, so a `skill_out` buff with `refill` would refill it as
  the cover deploys.

The pack carries the cover's duration (20 s), cooldown (45 s), radius and skill health, and its two bonuses as references
(`weapon_handling_reference_base` 0.15, `out_of_cover_damage_reference_base` 0.1, each with a tier bonus). Neither is a stat a
fight reads.

## Questions for the source (R156)

1. **The bonuses:** each one's amount (base, tier, mods, Expertise), whose attributes they read, and how they combine with the
   player's own (`dtoc` in its bracket; weapon handling).
2. **When:** given while the player is in cover within the radius, as the Fortified's (once per entry, or held while in cover),
   and removed on leaving or as the cover ends.
3. **The instant reload:** what it fills (the weapon in hand, both, the magazine to full), and when: once per entry into cover, at
   deployment, or on some other event; whether it counts as a reload for talents (`reload` events).
4. **Its own health, duration and cooldown,** from the use or from its end.
5. **What fights would need** as stats or rows, and any in-game checks.

### R156's readings

R156 (ShadeTech, R156_SOURCE_REVIEW.md), read from the source without running it:
- **The bonuses,** read from the deployer's attributes and put on the player in cover beside it, with no Skill Efficiency:
  weapon handling 0.15 at tier 0 (0.9 at tier 6, 1.15 overcharged), counting one for one in reload and swap speed; damage to
  targets out of cover 0.10 at tier 0 (0.37 at tier 6, 0.45 overcharged), inside the player's own bracket (`dtoc`). The pack's
  overcharge references are the tier step alone, without the base.
- **When:** held while in cover beside it, through the Fortified's 1 s loop; nothing spends them; gone on leaving cover or as the
  cover ends (inferred). A cover cycle is one stay (ADR 0032).
- **The instant reload:** on each weapon swap while the buff holds, the weapon put away is refilled to full (inferred from its name);
  not at entry or deployment. Whether talents hear a reload event isn't in the data.
- **The cover:** the Fortified's health and resistances; 20 s, cooldown 45 s from its end. Mod rolls stay out (a modifier can't
  read them). Five in-game checks.

## Decision

1. **The bonuses** as the Fortified's: a `skill_out` key, with `in_cover`, gating `weapon_handling` and `dtoc` by base and tier.
2. **The reload on swaps is modelled** (owner, 2026-11-22): while the buff is on, each swap in a rotation fight refills the weapon
   put away, with no reload event (an in-game check would settle whether talents hear one).

## Plan

1. **Readings** (R156, readings only), sent 2026-11-22 (ShadeTech).
2. **The owner's calls,** then **the engine** if any piece is missing (the reload's timing, say); otherwise straight to rows.
   **Built** (2026-11-22): a trigger field `refillOnSwap` on a `skill_out` buff: while it's on, each swap refills the weapon put
   away, with no reload event; the rule's own `requires` (`in_cover`) keeps it to a player in cover. Tested on test rows: in a
   rotation fight with the cover out, the ACR is never reloaded (on stage 2's test rows, without the bonuses: 113 shots in 20 s,
   against 80 and three reloads without the refill);
   out of cover the rule takes no part.
3. **Rows,** then their adoption. R157 sent 2026-11-22 (ShadeTech): the rule and `skill_out` buff with
   `refillOnSwap` and `requires` `in_cover`, the two modifiers on its key and `in_cover`, text, figures, as `r157.candidate.1` on
   `r155.candidate.1`. Delivered the same day (`d047b80`, R157_HANDOFF.md) and adopted: the rule, the trigger rule and the
   `precision_cover_active` key as asked; `weapon_handling` by 0.15 and `dtoc` by 0.10 plus the tier's step, on the key and
   `in_cover`; text. The overcharge references keep their tier-step values, with text giving the totals. ShadeTech's figures (the ACR
   with the D50 in rotation, no enemy hits): DPS 384,432 to 473,313 (+23%), reloads 10 to 6.05, and 498,554 with "Target out of
   cover".

## Consequences

- Builds with a Precision Smart Cover would run with skills, where they're refused today.
- While it's out and the player is in cover, damage to targets out of cover and weapon handling rise.
- No frozen build carries this skill, so nothing frozen moves.
