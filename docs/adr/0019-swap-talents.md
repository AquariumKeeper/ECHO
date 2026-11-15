# ADR 0019: Swap talents in fights

Status: Accepted, 2026-11-14. The owner chose the swap talents as the next group fights drive, after Cover Shooter (R112). On the six
points below, the owner took the recommendation on five. On Versatile they chose to build all five talents together, not Versatile as
a second stage.

## Decisions (owner, 2026-11-14)

1. **Priming:** a new `primeHolstered` on `ready` rules, the time since the weapon was put away, reset by drawing it.
2. **Faster Than Reloading's reload:** both weapons refilled, with a new `refill: "both"`.
3. **Back and Forth's swap-from group:** modelled. ShadeTech adds the `whileHolstered` modifiers and their key, and the two groups
   cancel each other.
4. **The same talent on both weapons:** one key shared between the two copies, as the descriptions say. A second copy's bonus
   replaces the first's for Back and Forth, and a swap away locks both copies for Unwavering.
5. **Versatile:** built with the other four, in one stage. It needs a gate on differing weapon types, a cooldown per weapon type, and
   bands from the fight distance. In a fight without a distance, Versatile keeps its assumption (`versatile_active`) and the result
   says so, as `distanceDefaults` inputs keep their ordinary default.
6. **Order:** the engine first, then one request for the readings and rows. Results move once, at adoption.

## Context

Five talents react to swapping weapons, but each reads a fixed assumption today:

| Talent | What it does (pack description) | Today |
| --- | --- | --- |
| Unwavering (and Perfect) | Swapping to this weapon: +300% (400%) handling for 5 s. Kills refresh it. Swapping away disables it on all weapons for 5 s. | `unwavering_active` |
| Back and Forth (and Perfect) | Swapping to it: +10% (13%) rate of fire and +9% (12%) damage for 10 s. Swapping from it: +5% and +4.5% for 10 s. Each group removes the other. | `back_and_forth_swap_to_active`; the swap-from group isn't in the pack |
| Perturb | 10 s holstered primes it. Swapping to it while primed: +25% rate of fire and +30% damage for 20 s. Swapping away ends it. | `perturb_active` |
| Faster Than Reloading | 5 s holstered primes it. Swapping to it while primed reloads both weapons and gives +20% rate of fire and +50% damage until its magazine is emptied. Reloading or swapping away ends it. | `faster_than_reloading_active` |
| Versatile (and Perfect) | Swapping between different weapon types: +35% (45%) total weapon damage for 10 s, at most once per 5 s per type. Shotguns and SMGs within 15 m, rifles and MMRs beyond 25 m, LMGs and ARs (10%, 20%) between 15 and 25 m. | `versatile_active` |

**What fights have** with a two-weapon rotation (ADR 0005, C9):
- **Swap events:** `swap` goes to gear and the weapon leaving, `swapped` to gear and the weapon drawn, `swap_after_kill` to gear and the
  weapon leaving.
- **A weapon talent's modifiers** apply only while its weapon is in hand, unless marked `whileHolstered`.
- **Buff rules** take `cancelOn` with `cancelFor` (a lockout after cancelling), `whileKey` (gate on another key, read before the
  event), `refill` (the magazine in hand) and `every`.
- **`gainWhileHolstered`** gains stacks on a free-running clock while a weapon without the rule's tags is in hand (Quick Draw).
- **The same talent on both weapons** runs one copy of its rules per weapon. Only the copy in hand drives the keys.

**How the five fit:**
- **Unwavering:** nearly fits today. A buff on `swapped` lasts 5 s, and `cancelOn: swap` with `cancelFor: 5` is the lockout. "Kills
  refresh it" doesn't fit: a second rule on `kill` would need to gate on its own key, and `whileKey` must name another rule's key.
- **Back and Forth:** the swap-to group fits today: a buff on `swapped`, 10 s, `cancelOn: swap`. The swap-from group needs pack rows:
  a `whileHolstered` modifier pair and a rule on `swap` that sets its key for 10 s, so the bonus applies to the other weapon.
- **Perturb and Faster Than Reloading:** need "primed after N s holstered", time since this weapon was put away. The free-running
  clock of `gainWhileHolstered` isn't that, and it is keyed by weapon tags, not by the talent's own weapon.
- **Faster Than Reloading** also reloads both weapons, where `refill` refills only the magazine in hand. "Until its magazine is
  emptied" is a reload starting, since fights reload an empty magazine.
- **Versatile** needs three things fights don't have:
  - a gate that the two weapons are of different types;
  - a cooldown per weapon type;
  - the distance bands by weapon class, from the fight distance.

## Proposal

Drive the five in fights with a weapon rotation and talent triggers, with these engine pieces:
- **`primeHolstered` (seconds)** on a `ready` rule: the rule is ready once its weapon has been holstered for that long without a
  break, and drawing the weapon spends it. Perturb and Faster Than Reloading.
- **`refill: "both"`** on a buff rule: the magazines of both rotated weapons are refilled. Faster Than Reloading.
- **`refreshOn` (an event)** on a buff rule: while the buff is on, that event restarts its duration, and it gains nothing while off.
  Unwavering's kills.
- **Versatile's gate, cooldown and bands** (see point 5).

Then ask ShadeTech (as a request after the decision) for the rows, the swap-from modifiers, and a reading of each script. Without a
rotation, nothing changes: the assumptions stand.

## The points as proposed

1. **Priming.**
   - **A new `primeHolstered` on `ready` rules** (recommended): the time since the weapon was put away, reset by drawing it.
   - **Reuse `gainWhileHolstered`** with one stack as primed. Its clock is free-running, so a prime could come as soon as 0.1 s after
     holstering.
2. **Faster Than Reloading's reload.**
   - **Both weapons refilled** (recommended), as the description says, with a new `refill: "both"`.
   - **Only the weapon drawn,** with today's `refill`. The other weapon's reload is lost.
3. **Back and Forth's swap-from group.**
   - **Model it** (recommended): ShadeTech adds the `whileHolstered` modifiers and their key, and the two groups cancel each other.
   - **Swap-to only,** as the pack has it, noted.
4. **The same talent on both weapons.** Back and Forth says two instances don't work at once, and Unwavering's lockout covers all
   weapons.
   - **Follow the descriptions** (recommended): one key shared between the two copies. A second copy's bonus replaces the first's
     for Back and Forth, and a swap away locks both copies for Unwavering. That needs copies to share a key, which they don't today.
   - **Per-weapon copies as today,** noted, which can overstate a build with the talent on both weapons.
5. **Versatile.**
   - **A second stage** (recommended): the four weapon talents first. Then Versatile, with a gate on differing weapon types, a
     cooldown per type, and bands from the fight distance (refused, or the assumption, without a distance).
   - **All five together,** which delays the four.
6. **Scripts first or rows first.**
   - **Reading and rows in one request** (recommended), with the engine built first, as for earlier talents. Results move once, at
     adoption.
   - **A reading first,** then the engine, then rows.

## What this doesn't do

- **Swap timing** beyond the rotation's own (whenEmpty or afterKill, with its swap time): fights don't choose when to swap for a
  talent.
- **Perturb's threat,** which fights don't model.

## Stages

1. **The decision** on the six points. Done 2026-11-14.
2. **The engine,** with no result change until rows use it:
   - `refreshOn`, done (#248);
   - `primeHolstered`, done (2026-11-14);
   - `refill: "both"`;
   - the shared key across copies of a talent;
   - Versatile's gate on differing weapon types, cooldown per type, and distance bands.
3. **The request:** the rows for all five talents, the swap-from modifiers, and a reading of each script.
4. **Adoption:** a deliberate result change for builds with these talents in fights with a rotation.

## Alternatives considered

- **Leave them as assumptions.** That keeps a swap talent's bonus on for the whole fight, or off, whatever the rotation does.
- **Drive them without a rotation,** with assumed swaps. Fights already have a rotation for that, with its own timing.

## Consequences

- Builds with these talents in rotation fights would show the bonus only while the rotation earns it. That's mostly lower than the
  assumption "always on", and higher than "off".
- A rotation fight starts to matter for these builds, so the Desktop's rotation fields become part of comparing them.
