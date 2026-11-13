# ADR 0017: Control skills' statuses on enemies

Status: Accepted, 2026-11-13. The owner asked for it, so that ADR 0016's call to let control skills' areas reach other enemies has
something to act on, and took the recommendation on all five points below.

## Context

Fights track only a skill's burn or bleed (PACK_SCHEMA_V2.md, under `status`). A control skill's rule gives a cooldown, a window and
a status, but the status never goes on an enemy:

| Skill | Rule today | Status | Its duration output | Its reach |
| --- | --- | --- | --- | --- |
| Shock Trap | `window: shock_duration_seconds`, cooldown | shocked | `shock_duration_seconds`, base 3 s | `radius_meters`, base 2.5 m; the trap lasts `duration_seconds`, base 60 s |
| Banshee Pulse | `window: disorient_duration_seconds`, cooldown | disoriented | `disorient_duration_seconds`, base 4 s | A cone (`radius_meters` is its 30 m length) |
| Riot Foam Chem Launcher | `window: ensnare_duration_seconds`, cooldown, stock | ensnared | `ensnare_duration_seconds`, base 6 s | `radius_meters`, base 3 m |
| Blinder Firefly | `window: blind_duration_seconds`, cooldown | blinded | `blind_duration_seconds` | The enemies it flies to; no radius listed |

Talents already put these statuses on enemies, and fights act on them:
- **Shocked** stops that enemy's share of the incoming fire while it lasts (`Shock.Blocks`, with incoming fire). With one attacker
  that's all of it; where a profile aggregates several, it's one attacker's share.
- **All four statuses** (shocked, disoriented, ensnared, blinded) read as on for talents' keys, `whileStatus` rules, enemy counts by
  status, and target status uptime.

So a build with a control skill gets the skill's cooldown, but neither the shock that would stop an enemy firing nor any talent that
reads a status on the enemy.

## Proposal

**A control skill's status goes on the enemy being shot as the skill is used,** for the duration its `window` output gives. It goes
on as the player's status, through the path talents' statuses take today, so it:
- fires `status_applied` there;
- reads as on for keys, `whileStatus` and counts;
- counts in target status uptime;
- stops a shocked enemy's share of the fire, as a talent's shock does.

It deals no damage, as these statuses deal none. A use with no living enemy being shot (between enemies, or as one dies) puts it on
nobody, as an impact due with no enemy there is lost. A second use while the status is still on refreshes it, as a talent's status
refreshes: it lasts to the later end, and it isn't a new application.

**With an area (ADR 0016)** the status goes on every living enemy within the skill's radius of the enemy being shot. Events stay the
enemy being shot's.

**Without the option** nothing changes: a row opts in with a field, `controlStatus: true`, so results move only when ShadeTech's
rows set it.

## Decided points (owner, 2026-11-13: the recommendation on each)

1. **The duration.**
   - **The window output as it stands** (recommended). Each already reads the skill's tier and, normal and overcharged alike, the
     player's status effects (`… * max(0, 1 + effective_status_effects + status_effect_duration)`), so it isn't scaled again.
   - **Scaled like a talent's status,** by the player's status duration factor. That would count those bonuses twice wherever the
     output already reads them.
2. **When a trap's status lands.**
   - **Once, as the trap is used** (recommended), on the enemy being shot and those in its radius. That's a first step that ignores
     enemies walking into a trap later, which fights don't move.
   - **Again each time it could,** over the trap's lifetime, which needs the trap's re-arm time from ShadeTech's reading.
3. **The Banshee Pulse's cone and the Blinder Firefly's flight.**
   - **The enemy being shot only** (recommended), until fights have cones and flight paths. Like the Airburst Seeker's cone in R107,
     their reach depends on geometry fights don't have.
   - **Treat the pulse's length as a radius,** which would reach far more enemies than a cone does.
4. **Immunity.** In the game an enemy may resist a status it has just had.
   - **None** (recommended), until ShadeTech's reading shows the rule; the result names the assumption.
   - **A fixed immunity window,** which needs data the pack doesn't have.
5. **The field.**
   - **`controlStatus` on the row** (recommended), set by ShadeTech where the source supports it.
   - **Always on** for every control skill's rule.

## What this doesn't do

- **Moving enemies,** so nothing walks into a trap or out of foam.
- **Control effects beyond the status:** foam's hit points, a pulse's reveal or a trap's charges.
- **Enemies' own control effects on the player.**

## Stages

1. **The engine and validator:** `controlStatus`, the status on the enemy being shot and, with an area, on those in reach; notes. No
   result changes until rows set it. Built on 2026-11-13. Fights hold statuses only with talent triggers, so that's where it acts.
   R110's rows were adopted the same day.
2. **The request:** ShadeTech's reading of each control skill's script, covering the duration, how a trap re-arms, the pulse's
   cone, and any immunity. Then rows and figures.
3. **Adoption:** a deliberate result change for builds with a control skill, mostly under incoming fire (shock stops fire) and for
   talents reading statuses.

## Alternatives considered

- **Leave control skills as cooldowns only.** That loses the shock's stop on fire, which is most of what a Shock Trap does in a
  fight.
- **Read the status from an assumption,** as some talents' targets do. That would let the player assume the status was on
  regardless of the skill's cooldown, which is what fights exist to replace.

## Consequences

- Builds with a Shock Trap take less incoming damage in fights under incoming fire, by the shocked enemy's share of it. Builds whose talents read a status gain where a
  control skill now supplies it.
- ADR 0016's point 6, control skills' areas, gets something to act on.
