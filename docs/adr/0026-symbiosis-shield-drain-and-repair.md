# ADR 0026: Symbiosis's shield drain and repair in fights under fire

Status: Done, 2026-11-20. R140's readings and the owner's calls settled the open points, the engine is built, and the rows from `r141.candidate.1` are in. The last talent refusal of the coverage
survey's "Shield repair under fire" row (SIMULATOR_COVERAGE_SURVEY.md). The owner placed Symbiosis after Overflowing (ADR 0025).

## Context

Symbiosis is the Oxpecker's talent (an exotic SMG, primary or secondary): "While having a Shield deployed, lose Shield Health at a
rate of 10% per second. Your Shield receives repairs of 25% of the damage dealt by this weapon." Its one modifier is
`shield_repair_damage_share` 0.25. The pack's `estimated_shield_repair_per_second` multiplies that by practical firing DPS and
leaves out the drain (G403). The drain has no stat.

Fights under enemy fire model one shield (`ShieldState`):
- **Health:** `skill_health`. A raised shield absorbs a whole enemy hit, and a hit that empties it breaks it.
- **Regeneration:** `active_regeneration_reference` while raised, as a rate up to full.
- **A break:** starts `cooldown_seconds`, fires `shield_break`, and the shield is raised again full when the cooldown ends.
- **Repairs:** queued repair rates (Makeshift Repairs) beside regeneration, and discrete grants (Liberty or Death) through
  `ShieldState.Repair`, capped at the shield's health, none while broken.

The Crusader and Striker Shields allow the primary and secondary weapons, so either can be held with the Oxpecker. The Bulwark and
Deflector allow only the sidearm. Under a weapon rotation that allows only one of its two weapons, a raised shield forces that one,
and the build's weapon returns after a break. A shield's health only falls
at an enemy hit, so fights never look for a break between hits.

Fights refuse Symbiosis under fire (`incoming-repair-unsupported`, from `shield_repair_damage_share`). Fights without enemy fire
don't read the shield at all, and Symbiosis changes none of their figures. No frozen build carries the Oxpecker.

## Decision

Symbiosis's drain and repair become part of the fight's shield. The details follow R140 where it answers them; the choices below
hold until it does.

1. **The drain is a rate on a raised shield,** beside its regeneration: the shield's health follows regeneration minus drain. While
   the drain is larger, health falls between hits. A fight finds the time it reaches zero and schedules that as an event. If the
   source lets the drain break the shield, that is an ordinary break (the cooldown, `shield_break`, and a weapon return where the shield forces one).
   Otherwise the drain stops at a floor the readings name.
2. **When the drain runs:** while the talent applies and the shield is raised. Whether that needs the Oxpecker in hand, or only
   equipped, is R140's question, and a rotation follows the answer.
3. **The repair lands per hit of the Oxpecker:** the share times the damage the hit deals, as R140 defines it, through
   `ShieldState.Repair`. It is capped at the shield's health, and a broken shield gets none. Results count it with the shield's other
   repairs, and the trace records it.
4. **The pack carries the numbers.** The drain becomes a stat on Symbiosis's modifiers (a share of shield health per second), and
   the repair keeps `shield_repair_damage_share`. The engine reads those stats, not the talent's ID. If R140 shows the summary stat
   is wrong, its formula or text changes with the rows.
5. **Still refused:** a triggered change to the shield's health, regeneration or cooldown, as now.

## Stages

1. **The readings:** R140 (sent 2026-11-20, ShadeTech) asks ShadeTech, by the source:
   - the drain's basis (maximum or current health), its timing, when it runs, how it meets regeneration, and whether it breaks the
     shield;
   - which damage the repair counts, when it lands, its cap, a broken shield, and what scales it;
   - whether the pack's two stats match, and what fields fights would need.
   Answered 2026-11-20 (ShadeTech, R140_SOURCE_REVIEW.md), readings only:
   - **The drain:** 10% of the shield's maximum health (the Crusader's and Striker's health, read at each raise), once a second on a
     1 s timer from the script's start, while the shield is active, beside its own regeneration. The graph has no floor; whether its
     damage breaks the shield, and which weapon groups pause it, aren't in the data (two in-game checks).
   - **The repair:** 25% of each bullet's processed damage, at once, while the shield is active, from the weapon type in hand.
     Bullets only: no statuses, talent damage, explosions or redirects. No skill or repair stat scales it.
   - **The pack:** `shield_repair_damage_share` matches. `estimated_shield_repair_per_second` stays as it is, since a weapon stat
     can't read the shield's health; fights carry the net.

   The owner's calls on what the source leaves open (2026-11-20):
   - **A drain that empties the shield breaks it,** as a hit would: the cooldown starts and `shield_break` fires. The in-game check
     can change this.
   - **Symbiosis acts only with the Oxpecker in hand,** drain and repair alike, as fights treat weapon talents. With the other
     weapon in hand, the shield neither drains nor repairs from it.
2. **The engine** (built 2026-11-20):
   - **The stats:** the engine reads `shield_health_loss_share` (the drain, a share of the shield's maximum health per second, 0 to
     1) and `shield_repair_damage_share` from the state with the weapon in hand, so a weapon talent's own stats apply only while its
     weapon is out.
   - **The drain** lands on each whole second of the fight, after the second's other events and before its rules, on a raised
     shield. It takes its share of the shield's health through `ShieldState.Apply`, so one that empties the shield breaks it, with a
     `ShieldDrained` event and the result's `ShieldDrained`. The fight's whole seconds stand in for the script's timer, which starts
     with the talent; fights start with it on.
   - **The repair** goes to a raised shield at each shot: the share of the damage the shot dealt its target (to armor and health,
     not past a kill), without a redirect's share. It's capped at the shield's health, and counts in `ShieldRepaired`.
   - **The drain comes with the repair:** a repair share with no drain beside it is refused (`incoming-repair-unsupported`), since
     Symbiosis would then be all gain. The committed pack was refused that way until the rows came. So is a drain that only a
     fight's state turns on, whose seconds aren't scheduled, and a modifier on `estimated_shield_repair_per_second`.
   - **Without a shield** in the fight, the two have nothing to act on, and the result says so.
3. **Rows:** the drain stat and its modifier on Symbiosis, then their adoption. R141 (sent 2026-11-20, ShadeTech)
   asked for them as `r141.candidate.1` on `r139.candidate.1`, with figures. Delivered 2026-11-20 (ShadeTech) and
   adopted: the stat `shield_health_loss_share` (formula `0`, percent), Symbiosis's flat 0.1 beside its 0.25 repair share, with
   coverage naming the two open checks, and `estimated_shield_repair_per_second`'s text pointing at fights for the net.
   - **What it does:** with the Oxpecker and a Striker Shield under fire, and no gear (20 random fights of 60 s, seed 1, hit
     rate 90%, target 1, difficulty 1), the drain outruns regeneration and the repair. The shield breaks once in each fight, and
     the player is downed in every fight, during the shield's cooldown. ShadeTech's figures and the planner's own run agree on
     that shape, not on the amounts. In ShadeTech's Vector comparison, without Symbiosis, the shield figures match exactly and
     the planner's damage is 11% lower, a setup difference on the weapon side. With the Oxpecker the planner's damage is 22%
     lower, and its player is downed 2 s earlier. Both runs used the same engine (planner `144988e` and `main` after the
     adoption have the same `src/`), the same pack bytes and a fixed seed, so the whole gap comes from the inputs, not the model:
     with Symbiosis, less damage also repairs less, so the shield breaks and the player goes down sooner, widening it.

## Consequences

- Fights under fire with Symbiosis run, and their shield breaks, uptime and repairs show the talent.
- A shield's health can fall between enemy hits, so a break can come at a time no hit marks.
- Results with the Oxpecker under fire are new, so nothing frozen moves.
