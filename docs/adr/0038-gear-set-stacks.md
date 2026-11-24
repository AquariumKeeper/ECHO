# ADR 0038: Gear sets' own stacks in fights

Status: Proposed, 2026-11-24; readings drafted (R169, not sent yet). The owner chose it from the coverage survey's refresh (SIMULATOR_COVERAGE_SURVEY.md,
item 6): Striker's Battlegear first. The owner's calls follow R169's readings.

## Context

A set's four-piece bonus often grows with stacks or runs in windows. In fights most of these stay the user's setting, off by default,
so a Striker's Battlegear fight has no Striker stacks unless the user sets them. The rules of `simulation.json` drive only Hackstep's,
Headache's and Apex Protocol's. No ADR decided this for the others but Umbra and Future Initiative (ADR 0021).

This ADR takes the sets in stages, by what fights already have. First:

| Set | What the pack says | What fights have |
|---|---|---|
| Striker's Battlegear (`striker_stacks`) | +0.65% amplified weapon damage a stack (0.9% with Risk Management), from weapon hits, up to 100 (200 with Press the Advantage). Loses 1 a second up to 50, 2 from 51 to 100, 3 above | hits (`hit`, `hit_pellet`), stacks with one loss rate that starts after a pause (`duration`, `refresh`, `decay`), a fixed `max` |
| Tipping Scales (`tipping_scales_stacks`) | +0.5% Weapon Handling and +5% Critical Hit Damage (8% with Snowball) a stack, from shots, up to 50 (75 with Sustainability). Loses 6 a second while not shooting, none while an enemy is Suppressed | shots, the same stacks; no suppression |
| Heartbreaker (`heartbreaker_stacks`) | headshots pulse for 5 s; hits on a pulsed enemy add and refresh a stack of +1% bonus armor (2% with Cold) and +1.1% amplified weapon damage to pulsed enemies, 5 s, up to 50 (100 with Max BPM). Loses 2 a second | headshots, `target_pulsed` on the enemy being shot (the Achilles Pulse's mark, ADR 0036), bonus armor that follows the state (ADR 0020) |
| Eclipse Protocol's Symptom Aggravator (`target_status_affected`) | +30% amplified weapon and skill damage to a target with a status | Pressure Point's `status` rows read "any status" on the target |

What doesn't fit yet, from the pack's words:
- **A cap that depends on a talent:** a rule's `max` is fixed (`maxMagazine` sizes one from the magazine, for weapon talents). The sets'
  formulas already clamp at the talent's cap, so a fixed `max` at the highest gives the right bonus per stack and errs only in stacks
  above the cap putting off the loss: a possible fallback.
- **A loss that depends on the count, starts at once, or runs while gaining:** `decay` needs a `duration` and `refresh`, so the first
  stack goes one step after a pause from the last gain.
- **A stacks gain while the enemy is pulsed:** `whileKey` is refused on `stacks` rules, and `target_pulsed` is no trigger's key.
- **Two drivers of `target_pulsed`:** a headshot's 5 s mark on the enemy fits a `buff` on `headshot` (a per-enemy mark, ADR 0011), but the
  Achilles Pulse's zones hold that key, and a pack with both is invalid.
- **Symptom Aggravator, if its check leaves a status out:** `anyStatusExcept` doesn't go on a `status` rule.

Limits that stay whatever the rows: a skill keeps what it read as it deployed unless it reads live, so a skill putting its own status on
doesn't amplify itself, and talent burn, bleed and poison ticks don't read either multiplier.

## Questions for the source (R169)

1. **What gains:** for each set, the event (a hit by a projectile, a pellet, or a shot; on the enemy or anything; weapons only; with the
   set's weapon only), how many a shot, and whether the gain is at once.
2. **What's lost:** the loss's rate, when it starts (at once, steadily, or after a pause from the last gain), its tick, what stops it, and
   whether it runs while gaining.
3. **Caps,** and what the chest and backpack talents change. Whether stacks above a cap go as a talent is lost isn't a fight's question.
4. **What clears them:** a kill, a reload, a swap, going down, the end of combat.
5. **Heartbreaker** (R162 read its pulse: its own, `PulsedByGroup`, not a status, with an Achilles mark counting): the pulse's time and
   refresh, a killing headshot, whether the hit that pulses gains, whether one count feeds the bonus armor and the damage, whether a scan
   pulse is the same state, and whether a gain refills spent bonus armor and what a loss takes of it.
6. **Eclipse:** which statuses count (its check's list, as Chemical Agent's has one), and whether the spread or a skill's status counts.
7. **The multipliers:** whether the stacks' damage amplifies as the pack has it, and what it reaches (weapon hits, skills, statuses).
8. **Rows:** what fits existing fields, and which fields the engine would need.

## Plan

1. **Readings** (R169, readings only), drafted 2026-11-24 and not sent yet.
2. **The owner's calls,** then the engine's fields, a set at a time.
3. **Rows,** then their adoption, with figures. Results for builds with these sets move by design, recorded in the ledger.
4. **The later sets** of the survey's table, by the same steps.

## Consequences

- A fight with talent triggers and one of these sets would no longer read the user's setting for its stacks, as talents that fights
  drive don't. The setting still holds in other fights and outside fights.
- Two frozen builds carry one: striker-rifle (Striker's Battlegear, four pieces) and shotgun-close (Heartbreaker, four pieces). Their
  `buffs` runs in the frozen-build comparison would move by design (both have 0 stacks there today), recorded in the ledger; their other driven runs stay refused
  for other reasons, and fights without talent triggers don't move. The baseline fixtures, all undriven, stay as they are.
