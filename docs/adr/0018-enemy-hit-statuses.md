# ADR 0018: Statuses an enemy's hit puts on the player

Status: Accepted, 2026-11-14. The owner asked for a design (2026-11-14), after R111 found that the shield carrier's nail gun bleeds the
player for 2 s on each hit. On the six points below, the owner took the recommendation on four. On two they widened the scope: control
statuses are in, with their effects settled after ShadeTech's reading, and so are talents reacting to the player's statuses.

## Decisions (owner, 2026-11-14)

1. **Scope:** damaging and control statuses alike. A control status on the player (shocked, disoriented, blinded, ensnared) first
   goes on as a status with no effect on the player's actions. What each one stops or slows is decided after ShadeTech's reading of
   it. Until then the result names it as on, with no effect.
2. **The damage:** from the source. ShadeTech looks for the NPC status's damage on the player. Without one, the status counts but
   deals nothing, and the result says so.
3. **Armor or health:** as the source says. Until then, armor first like a hit, and the result says so.
4. **Resistances:** read them, as ShadeTech's reading says they apply.
5. **Talents reacting to the player's status:** included. The request asks which talents read or clear a status on the player, and
   the engine gives rules a way to read it.
6. **The row:** a new `simulation.enemyStatuses` list.

## Context

Enemy fire is a cadence of hits (`EnemyFireSchedule`): each hit deals the target profile's mitigated hit damage to the player's armor,
then health. Since #242 every fight with enemy fire says a hit deals its damage only, and a status it puts on the player isn't counted.

What the data has (R111_HANDOFF.md):
- **Target 5's representative weapon,** the shield carrier's nail gun. Its impact script applies Bleed for `BleedDuration`, 2 s by
  default, with no damage override. The player's damage from an NPC's Bleed isn't in the files ShadeTech read.
- **Other profiles' weapons:** not yet read for statuses. A flamethrower or an incendiary round may burn; a taser may shock.
- **The player's resistances:** the pack has `hazard_protection` and one resistance stat per status (`bleed_resistance`,
  `burn_resistance`, `shock_resistance` and the others), each 0 unless gear sets it. Nothing in fights reads them.

The fight already models statuses on enemies, not on the player. The player's statuses would be new state.

## Proposal

**A target profile's hit may put a status on the player,** from a pack row per profile. In a fight with enemy fire, a hit from an
enemy of that profile puts the status on the player for its duration, refreshing to the later end if it's already on.
- **A damaging status** (bleed, burn) ticks on the player for the row's damage, through armor then health as a hit does, and can down
  the player.
- **It reads as the player's status** for talents that react to one, if any rule names it (see point 5).
- **The result reports it:** the time each status was on the player, and the damage it dealt, as part of damage taken.

Without a row nothing changes. Results move only when ShadeTech's rows exist, and only in fights with enemy fire.

## The points as proposed

1. **Scope.**
   - **Damaging statuses first** (recommended): bleed and burn, whose effect is damage over time, which fights already know how to
     tick.
   - **Control statuses too:** shock, disorient, blind and ensnare on the player would stop or slow the player's firing, reloading or
     skill use. That needs rules for what each one stops, and most target profiles' weapons don't apply them.
2. **The damage.** The NPC's bleed damage isn't in the files read so far.
   - **Ask ShadeTech for the source amount** (recommended). If it's found, the row carries it as a stat. If not, the row carries the
     status and its duration only, so it counts for uptime and talents, but deals no damage, and the result says so.
   - **Assume a share of the hit's damage,** which would need a figure no source gives.
3. **Armor or health.** Whether a status's damage on the player goes through armor first, as a hit does, or straight to health.
   - **As the source says** (recommended), from ShadeTech's reading. Until then, through armor first like a hit, and the result
     says so.
   - **Straight to health** throughout.
4. **The player's resistances.**
   - **Read them** (recommended): the status's resistance stat, and `hazard_protection` if the source applies it, shorten the
     duration (or the damage) as ShadeTech's reading says. They're 0 on most builds, so this matters only for gear that sets them.
   - **Ignore them,** noted as a limitation.
5. **Talents reacting to the player's status.** Some talents read the player being under a status, or clear one.
   - **Not in this ADR** (recommended). Statuses go on the player and deal damage; a later request finds the talents that read them.
   - **Include them now,** which widens the request.
6. **Where the row lives.**
   - **A new `simulation.enemyStatuses` list** (recommended), each `{ "target", "status", "duration", "damage" }`. `target` is an
     `enemy_target` profile value, `duration` is seconds or a stat, and `damage` is an optional stat. Each target profile takes at
     most one row per status, and a target sequence reads each target's own.
   - **Fields on the profile's enemy-fire stats,** which would tie it to the stat formulas.

## What this doesn't do

- **Read each enemy's own weapon.** Fights use one representative weapon per profile, so a status follows the profile, not a
  spawned enemy's actual weapon.
- **Shields absorbing the status.** A raised shield takes the hit, and the status with it, as the source's nail does on a shield.
  That's assumed, and noted.
- **Cleansing,** by skills or consumables.

## Stages

1. **The decision** on the six points. Done 2026-11-14.
2. **The request** (R114) for ShadeTech's reading of:
   - which target profiles' weapons put a status on the player;
   - its duration and damage;
   - whether that damage goes through armor first;
   - how resistances apply;
   - what each control status stops;
   - which talents read or clear a status on the player.
3. **The engine and validator,** with no result change until rows exist. Control effects follow the reading, with a further owner
   decision if the source leaves a choice. R114 answered on 2026-11-14 (only target 5's nail gun applies one, a 2 s bleed), and
   `simulation.enemyStatuses` was built the same day: damaging statuses tick through armor first, and resistances shorten the
   duration, R114's likely reading. Control statuses go on with no effect, since no profile applies one. Talents reading the
   player's statuses (Chemical Agent) are the next piece. Built the same day as `whilePlayerStatus` on buff and stacks rules.
4. **Adoption:** a deliberate result change for fights with enemy fire against those profiles.

## Alternatives considered

- **Keep the limitation note.** It's accurate, but leaves fights against the nail gun's profile, and any burning enemy, short of
  the damage they deal.
- **Fold the status into the hit's damage** as a flat extra. That hides the status from uptime and talents, and gets its timing
  wrong across armor breaks and repairs.

## Consequences

- Fights with enemy fire against profiles whose weapons apply a status would take more damage, and go down sooner.
- The player gains status state in fights, which later talent work can read.
