# ADR 0023: Headhunter, Determined and Salvage in fights

Status: Accepted, 2026-11-18; done 2026-11-19 (`r133.candidate.1`). Item 4 of the coverage survey (SIMULATOR_COVERAGE_SURVEY.md). The owner chose the pack to gate the
static values, and all three talents in scope.

## Decisions (owner, 2026-11-18)

1. **The static value:** the pack gates it. Each talent's static expected-value modifier requires a selector that fights switch
   off, so a fight never sees it, and calculations outside fights keep it.
2. **Scope:** all three talents (and their Perfects), staged with Salvage first, since its fight rule needs no new field.

## Context

Headhunter, Determined and Salvage write static expected values, so every driven fight with one of them is refused
(`shot-mechanic-unsupported`):
- **Headhunter** writes `practical_shot_bonus_damage`: a share of the killing blow, capped, spread over shots by
  `practical_kills_per_shot`.
- **Determined** writes `practical_shot_bonus_damage`: the gain from forcing a headshot (Perfect: a critical headshot) after a
  headshot kill.
- **Salvage** writes `practical_rounds_returned_per_shot`: a 70% (Perfect 85%) chance to refill the magazine on a kill, as rounds
  per shot.

Fights see kills, headshots and refills as they happen. They have to set these averages aside and play the mechanics, or they'd count
them twice.

## Proposal

1. **A selector fights set:** `fight_context`, built into the engine like `weapon_slot`, with the values `static` and `fight`.
   - Calculations outside fights read `static`.
   - Fights read `fight`. The user can't set it.
   - A modifier that requires `fight_context: static` holds only outside fights. That is the pack's gate.
2. **The validator:** a modifier's `requires` may name `fight_context` with either value. A pack may not declare a selector with that
   id.
3. **The refusal stays:** `shot-mechanic-unsupported` still refuses a fight in which any of those stats is non-zero. An ungated
   static value can't slip into a fight.
4. **Salvage:** an existing rule shape, `on: kill`, `chance`, `refill`. No new field.
5. **Determined:** a new grant. After a headshot kill, the next landed shot (by R132's reading: any weapon or this one, pellets
   whole or not) is a headshot. Perfect's is a critical headshot.
6. **Headhunter:** a new grant. After a headshot kill, the next weapon hit within the window adds a share of the killing blow's
   damage, capped. What "the killing blow's damage" is, and how the bonus meets armor, waits on R132's readings.

## Stages

1. **The engine:** `fight_context` and its validator checks. Salvage's row then needs no engine work. No result moves: the
   committed pack doesn't gate anything yet. Built 2026-11-18.
   - **Fights with talent triggers** set it to `fight`. Fights without them, and every calculation, read `static`.
   - **A user's own value** never reaches a fight, and the resolver doesn't list it as an assumption.
   - **A trigger key must be a pack selector,** so Salvage's row needs one (as Full Stop's `full_stop_refill_event`).
2. **Determined's forced headshot:** two optional booleans beside `forceHeadshot`, `forceCritical` (Perfect) and `forceWholeShot`
   (decision 3). Determined's row is then Quick Draw's shape on `headshot_kill`: `stacks`, `max: 1`, `consumeOn: hit`. The
   stack's key is a numeric input, as every stacks rule's is. Built 2026-11-18.
3. **Headhunter's bonus hit:** `simulation.nextHitBonuses` (talent, share, cap stat, window), as decision 4 has it. Built
   2026-11-18. The cap is read for each weapon as the fight starts, so a buff that raises weapon damage mid-fight doesn't raise it,
   A swap brings a waiting bonus down to the drawn weapon's cap, and it stays down. A hit that is itself a headshot kill takes the
   waiting bonus and then arms a new one; whether the game keeps the bonus off that hit isn't in the data, and an in-game check is
   listed.
4. **The request** for all three talents' rows and the gated static modifiers, then its adoption. R132 (ShadeTech)
   asked for the readings first. R133 (sent 2026-11-18, ShadeTech) asked for the rows, the gates and the text.
   It was answered (`dc1569e`) with `r133.candidate.1`, adopted 2026-11-19, which finishes this ADR.
   - **Salvage:** two rows each, on `kill` and `skill_kill`, since a buff rule takes no `also`.
   - **Determined:** keyed on a new numeric input, `determined_armed`.
   - **Headhunter:** capped by a new stat, `headhunter_bonus_cap`, at 150% headshot damage or more.
   - **Follow-ups:** R135 (ShadeTech, answered `e6e740f`) gave Perfect Determined its own key and sized Headhunter's
     static estimate like the fight bonus (`r135.candidate.1`, adopted 2026-11-19). From its reading of the source, a forced shot
     that is itself a headshot kill now ends disarmed in fights rather than arming again.

## R132's readings (ShadeTech, R132_SOURCE_REVIEW.md)

- **Salvage:** one roll per kill, refilling the weapon in hand at once. It has no source filter, so any kill while its weapon is in
  hand counts, a skill's or a status's included: `on: kill` with `also: skill_kill`, `chance` and `refill`.
- **Determined:** a headshot kill with this weapon arms it. Its next hit on a live target is a headshot and spends it; Perfect's is
  a critical headshot too. A kill while armed disarms it, and there is no timeout. With pellets the first pellet hit spends it;
  whether the rest of that shot is forced isn't in the data.
- **Headhunter:** the amount is the killing hit's damage event × 1.25 (1.5), capped at weapon damage × 8, or × 12.5 at 150%
  headshot damage or more. The next projectile hit, on any target and body shots included, takes it as an added dealt modifier,
  unless that hit is itself a headshot kill, which replaces the amount. It lapses after 30 s. Whether the amount is measured
  before or after the enemy's armor, and whether the added damage meets armor again, isn't in the data.

## Decisions after R132 (owner, 2026-11-18)

3. **Determined takes the whole shot:** every landed pellet of the forced shot is a headshot (and critical, for Perfect). The
   forcing is a player attribute that the hit event removes, and a shot's pellets land together, so they most likely all see it.
4. **Headhunter's amount is the shot as dealt:** the killing shot's full damage, with the player's own bonuses, before the dead
   enemy's armor and however much health it had left. The bonus is added to the next hit, which meets that target's armor and
   damage-taken factors like the hit itself. By R132 the amount comes from the kill's own damage event, so for a shotgun it's the
   killing pellet's; the call settles armor and overkill, not pellets.

## Consequences

- Builds with these talents run in driven fights, with the mechanics as they happen rather than averages.
- Static results don't change: outside fights the gate is open.
- The pack can gate other static approximations the same way later.
