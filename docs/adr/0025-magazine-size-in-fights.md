# ADR 0025: Magazine size that changes in fights (Overflowing)

Status: Accepted, 2026-11-20; done 2026-11-20 (`r139.candidate.1`), apart from a `maxMagazine` cap beside a change. The last talent refusal of the coverage survey's "Magazine size in a driven fight" row
(SIMULATOR_COVERAGE_SURVEY.md). The owner chose it ahead of Symbiosis and the remaining damaging skills.

## Context

Overflowing (Perfect): "Every 3 (2) reloads from empty increases your base magazine capacity by 100%." Its committed row is a buff
on `reload` with `count: 3` (2) and `consumeOn: reload`, keyed `overflowing_active`, and its modifiers add 100% to
`magazine_size_percent` and `base_magazine_size_percent` while the key is on.

Fights read a weapon's magazine once. A talent state that changes it refuses the fight (`buff-timing-unsupported`: "Triggered
magazine, pellet or target-pool changes need dynamic scheduling"), so every driven fight with Overflowing is refused.

The magazine is read in many places:
- **Filling:** a reload's completion, a talent's refill, a fight's start, a swap's draw under rotation.
- **Rotation:** each slot's rounds and what's readable while holstered.
- **Magazine phases:** talents reading the share of the magazine left or spent.
- **Stack caps:** `maxMagazine` (a share of the magazine as a cap).

The `reload` event fires as a reload starts, including a partial reload (behind cover, for one).

## Decision

Magazine size becomes fight state, read where a magazine fills.

1. **When it changes:** a state's magazine applies at the next fill: a reload's completion, a refill, or a swap's draw. A magazine
   already loaded keeps its rounds. A fill reads the size as it stands then.
2. **Overflowing's order,** with the committed row: the counted reload's start turns the key on, and that reload completes with
   the doubled magazine. The next reload's start spends the key, and that reload completes at the normal size. Whether the game
   agrees is the first reading asked.
3. **Readers of the size:** magazine phases and `maxMagazine` caps read the magazine as last filled, so a share stays a share of
   what the weapon holds.
4. **Still refused:** pellet and target-pool changes, as now.

## Stages

1. **The readings:** R138 (sent 2026-11-20, ShadeTech) asks ShadeTech, by the source:
   - which reload's fill is doubled: the counted reload's own, or only the next;
   - what "from empty" counts: a reload at zero rounds only, or any reload, and whether a refill, a swap or a partial reload
     counts, resets or spends the count;
   - what ends the doubled magazine: the next reload's start, its completion, or the rounds running out;
   - whether "base magazine capacity" doubles before or after other magazine bonuses (the pack's `base_magazine_size_percent`);
   - Pakhan, the pack's other base-magazine bonus, if anything there changes in fights.
2. **The engine:**
   - **2a, one weapon:** built 2026-11-20, ahead of R138, since decision 1 doesn't wait on it. Every fill (a reload's
     completion, a refill) loads the magazine of the state as it stands, and magazine phases read the magazine as last filled.
     A fight with one weapon no longer refuses a state that changes the magazine, so Overflowing runs with its committed row:
     each counted reload loads the doubled magazine, and the next loads the usual one.
   - **2b, rotation:** built 2026-11-20. Each rotated weapon keeps its magazine as last filled. A fill (a committed reload, a
     refill) reads the weapon's magazine in the driven state, held or in hand, and a committed reload is read again as it starts,
     after its events, so Overflowing turning on there sizes it. Magazine readings see the magazine before a reload still under
     way, beside its rounds. The swap questions R138 couldn't settle (a swap mid-reload, the other weapon's reloads) don't arise:
     fights don't swap mid-reload, and a weapon talent hears only its own weapon's reloads.
   - **Still refused:** a magazine change beside a `maxMagazine` stack cap, which is read once as the fight starts. No committed
     talent pairs the two on one weapon.
   - R138 may still move the row: what counts as a reload from empty, and which fill is doubled.
   - **2c, `reload_empty`:** built 2026-11-20, after R138. A reload starting from an empty magazine also fires `reload_empty`,
     after its `reload`, so Overflowing's row can count only those, as the source does.
3. **Rows:** R139 (sent 2026-11-20, ShadeTech) asks for Overflowing's row on `reload_empty`, the pack's magazine
   formula with base bonuses in the flat term, and Pakhan's stacks driven in fights (or the field they'd need), then their
   adoption. Answered (`8bd1417`) with `r139.candidate.1`, adopted 2026-11-20:
   - **Overflowing:** `on: reload_empty`, with `count` and `consumeOn: reload` kept.
   - **The formula:** base bonuses in the flat term of all three branches. Only builds with a positive Magazine Size % and a base
     bonus move (the ACR example: 92 rounds, from 87).
   - **Pakhan:** existing fields fit: a `stacks` rule on `kill`, `max` 4, `weaponTags: ["weapon_type_lmg"]`, reset by
     `reload_end`; `pakhan_stacks` takes `step: 1`. Kills during a reload can't happen in fights, so reading the stacks at the
     reload's completion gives the source's magazine.

## R138's readings (ShadeTech, R138_SOURCE_REVIEW.md)

- **The plan matches the source.** The bonus goes on as the counted reload starts empty (with 2 finished from empty, 1 for
  Perfect), so that reload's own fill is doubled. It adds the weapon's base magazine as flat rounds.
- **What counts:** a reload that finishes after starting at 0 rounds emptied by firing. A partial reload neither counts nor resets
  the count, but its start ends a running bonus. An interrupted counted reload doesn't count, and the bonus comes off.
- **What ends it:** the next reload's start of any kind, or the counted reload being interrupted. Not the rounds running out, no
  timer, not a swap. A refill isn't a reload event: it neither counts nor ends it, and probably fills to the larger size (inf).
- **Not in the data:** whether a swap mid-reload interrupts it, and whether the other weapon's reloads count or its magazine grows,
  so rotation stays refused.
- **The committed row** is right while every reload starts empty and finishes. Fights' partial reloads behind cover would count
  under it, so `reload_empty` (stage 2c) lets the row count only empty ones.
- **The pack's magazine formula** adds base bonuses (Overflowing, Pakhan) after the percentage, where the source puts them in the
  flat pool before it: with a positive Magazine Size % an ACR shows 87 rounds where the source gives 92. A pack row.
- **Pakhan:** kills with the weapon type in hand build up to 4 stacks; any reload's start applies 0.75 × stacks × base as flat
  rounds; a finished reload clears the stacks, and the bonus stays for the magazine it filled. So kills during one magazine size
  the next.

## Consequences

- Driven fights with Overflowing run.
- A fight whose state changes the magazine no longer refuses it; one that changes pellets or the target's pools still does.
- Results for builds with Overflowing are new, so nothing frozen moves unless a frozen build carries it.
