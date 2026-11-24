# ADR 0024: Gas Bomber Drone and poison in fights

Status: Accepted, 2026-11-19; done 2026-11-19 (`r136.candidate.1`). Item 3 of the coverage survey (SIMULATOR_COVERAGE_SURVEY.md), the first of the damaging skills no rule
shape fits (R131). The owner chose it first, and took the proposal on both open points. Striker and Bombardier Drones keep waiting
on in-game timings.

## Decisions (owner, 2026-11-19)

1. **Poison is a full status.**
   - **On enemies:** poison goes on enemies as burn and bleed do. It deals its own ticks if the source gives it damage, counts
     in target status uptime, fires `status_applied`, and talents reacting to any status (Pressure Point, Wicked) see it.
   - **Its other effects,** on enemy accuracy for example, come in only where the source shows them and fights can apply them.
2. **The clouds land on the enemy being shot.**
   - **Who's in them:** each use's clouds land on the enemy being shot, which stays in them for the cloud's duration. Others
     reach them through the area radius on the `enemySpacing` line, as Blackout Seeker's blast does.
   - **The note:** the result says enemies don't walk out of a cloud.

## Context

Gas Bomber Drone has previews (`damage`, `poison_duration_seconds`, `radius_meters`, `cloud_duration_seconds`, `bomb_count`,
`skill_health`, `cooldown_seconds`) but no fight rule, so every fight with skills on refuses a build carrying it
(`skill-rule-required`).

By R131 it needs two things no field has:
- **A poison status,** reapplied while an enemy is in a cloud;
- **Its clouds,** three laid out in space.

Fights know six statuses: burning, bleeding, shocked, ensnared, blinded and disoriented. Burn and bleed deal ticks
(`statusDamage`); the others are control statuses.

## Proposal

1. **Poison joins the fight statuses** as a damage status, beside burn and bleed: its own `statusDamage` row (tick, period,
   duration, resistances), what applying it does to an enemy already poisoned, and its place in `status_applied`, `any`, uptime and
   spreads. The source's other effects on an enemy are named by R134. An effect fights already model (enemy accuracy under fire, a
   damage-taken factor) comes in; one they don't is left out, with a note.
2. **A cloud rule on a skill:** an existing `simulation.skills` rule's shape gains the cloud. Each use lays its clouds (the
   `bomb_count` output) on the enemy being shot. While a cloud lasts it (re)applies poison, and deals its damage if the source gives
   the cloud damage of its own, to each living enemy within its radius on the spacing line. Without a spacing, or with one enemy at
   a time, only the enemy being shot is in it.
3. **The fields** follow R134's readings. Where an existing field fits (`status`, `window`, `rate`, `area`, `statusRate`,
   `statusWindow`), it's used rather than a new one.

## Stages

1. **The readings:** R134 (sent 2026-11-19, ShadeTech) asks ShadeTech, by the source:
   - what poison does and how it ticks;
   - how Gas Bomber's bombs and clouds deal damage and apply poison;
   - what its preview outputs mean;
   - which talents see poison.
2. **The engine,** after R134:
   - **2a, poison as a status:** built 2026-11-19. `poisoned` joins the damage statuses, so `statusDamage` rows and skill rules
     (`statusRate`, `statusWindow`) can carry it, and its ticks are `PoisonTick` events. With existing fields Gas Bomber's rule
     can then poison the enemy being shot: `window` the cloud's duration, `statusRate` its tick as a rate, `statusWindow` one
     poison's length, and `cooldownFromEnd`.
   - **2b, the cloud reaching others:** built 2026-11-19. A rule with a status window and no impact may name an `area` (a cloud). While the use applies,
     its status goes on every other living enemy in the radius of the enemy being shot, for one application from each tenth of a
     second at most, so each keeps it to the window's end plus one application. It needs talent triggers, as the statuses on other
     enemies do. The cloud follows the enemy being shot rather than staying where it landed: like the window's own status, it's
     on whoever is shot. The result notes that enemies don't walk out.
3. **The request** for poison's `statusDamage` row and Gas Bomber's rule, then their adoption. R136 (sent 2026-11-19,
   ShadeTech) asks for both as `r136.candidate.1`: the row's tick stat and 0.5 s period, and the rule's window,
   status window, area, cooldown from the end and a per-second `statusRate` output, since `damage` is per tick. It also asks
   about the drone's end, whether one 3 m cloud on the line stands for three bombs, the text, and figures. It was answered
   (`395a0e2`) with `r136.candidate.1`, adopted 2026-11-19, which finishes this ADR:
   - **Poison's row:** `poison_tick_damage` (`40 × status_tick_skill_curve`, 26,545) every 0.5 s, ticking on application.
   - **Gas Bomber's rule:** as asked, at `poison_dps` (53,090 per tick, so 106,180 a second, at every tier, without dealt amplifiers since R137), but with the
     cooldown from the use: the drone ends about 1.5 s after its first drop, long before its clouds, so a cooldown from the
     clouds' end would run about 6 s long. One 3 m cloud stands for three bombs, with a note in the radius's description.
   - **Health only:** the source marks poison's tick health only, and burn's and bleed's not (R134 said otherwise). Whether a
     health-only tick lowers health under standing armor isn't in the data. Fights take it off armor first, as a burn's, and an
     in-game check is listed.
   - **Follow-up:** R137 (sent 2026-11-19, ShadeTech) asks whether Gas Bomber's poison takes the player's Skill
     Damage and dealt amplifiers (`skill_damage_multiplier`), as every other skill status rate's output does, and whether
     `poison_dps` can read `damage` rather than repeat it. Answered (`29d03cd`) with `r137.candidate.1`, adopted 2026-11-20:
     the source's poison takes no Skill Damage, Total Skill Damage or Status Effects (the other skills' burns and bleeds get them
     from their own formulas), but the player deals its ticks, so dealt amplifiers reach it; both outputs read a new stat,
     `gas_bomber_poison_tick_damage`, times `skill_damage_multiplier`.

## R134's readings (ShadeTech, R134_SOURCE_REVIEW.md)

- **Poison on an enemy:**
  - **Damage:** a health-only tick every 0.5 s. By default it's 40 × the applier's skill curve, with no Status Effects factor.
    An override replaces it, and Gas Bomber's cloud sets one.
  - **Duration:** 10 s by default, raised by Status Effects. An application's own duration replaces that; Gas Bomber's is 5 s,
    with no Status Effects term.
  - **Reapplying:** one poison at a time, never stacked. Whether a reapplication refreshes isn't in the data.
  - **Other effects:** none: no action blocks, and no change to accuracy or damage.
- **Who sees it:** Wicked and Vindictive list poison, and Pressure Point tests for any status.
- **Gas Bomber Drone:**
  - **The bombs:** three, 4.5 m apart, about 0.375 s between drops. Each cloud lasts 8 s from its landing, 3 m around.
  - **The clouds** deal nothing themselves. They apply poison to everyone inside while they last, so an enemy that stays in one
    is poisoned again when its poison ends.
  - **The cooldown** (30 s base) starts at the drone's end.
- **The preview outputs:** `damage` is per tick, and `poison_duration_seconds` is one poison's length.
- **Other sources:** Septic Shock and a seasonal modifier apply poison. Rule Them All, Ember Engine, Ongoing Directive and a
  specialization node read it.

Fights treat poison as they treat burn and bleed, following decision 1: a skill's window keeps it on to the window's end plus one
poison. An in-game check asks whether an enemy that stays in a cloud stays poisoned for about 10 s or about 13 s.

## Consequences

- Builds with Gas Bomber Drone run in fights with skills.
- Talents that react to any status, or to `status_applied`, see poison from Gas Bomber, and from any other source a later row gives.
- Enemies standing in clouds is an assumption: fights don't move enemies. The note says so.
