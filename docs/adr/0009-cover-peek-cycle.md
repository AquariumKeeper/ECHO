# ADR 0009: Cover as a peek cycle

Numbered 0006 until 2026-11-03, when it was found to share that number with
[ADR 0006](0006-save-name-collisions.md). Older PRs and commits use the old number.

Status: Accepted, 2026-11-02, by the owner with the proposed answers to the open points
below.

## Context

In simulated fights, cover is two fixed assumptions:
- `in_cover`, a selector that Braced, Composure and their Perfect versions require;
- `enemy_out_of_cover`, a numeric input that turns on Damage to Targets Out of Cover.

Neither changes during a fight, and enemy fire ignores cover: every enemy attempt can hit.
In play, a player peeks out to shoot and ducks back to reload, and enemies mostly hit
them while they're exposed. The owner chose to model that as a peek cycle, with the
timing as the user's assumption.

## Decision

The player stays in cover and alternates two phases. (This was corrected before the build:
the first draft had the player leave cover to shoot.)

**Inputs** (simulation options, with no defaults; the two times come together):
- `peekSeconds`: time peeking from cover in each cycle, 0.1 to 60.
- `coverSeconds`: time fully behind cover in each cycle, 0.1 to 60. (The 0.1 s floor keeps
  a fight's boundaries within its work budget.)
- `peekHitShare`: the share of the normal enemy hit chance that still lands while
  peeking, from 0 to 1. It keeps the effects of difficulty and distance on enemy accuracy.
  There is no default; the result notes the value used. It's required with incoming fire,
  and not needed without it (owner, 2026-11-03). The CLI takes it as a percentage,
  `peekHitPercent`, like `pickupPercent`.

A fight starts at the start of a peek.

**Peeking** (shooting from cover):
- The weapon fires as today.
- An enemy attempt lands with the normal hit chance times `peekHitShare`. Every attempt
  still draws its own roll, so the random sequence doesn't shift.

**Behind cover:**
- The weapon doesn't fire. Firing stops at the boundary, even mid-burst, and resumes at
  the next peek.
- Entering cover starts a reload whenever the magazine isn't full, unless one is already
  running or the rotation is swapping when empty (see Scope). A running reload continues
  across boundaries.
- Enemy attempts are fully blocked: they count as attempts and as blocked shots (the
  existing `blockedShots`) and deal no damage. Cover never breaks.
- Skills, statuses, talent damage over time, armor regeneration and timers carry on.

**In cover throughout:**
- `in_cover` is on for the whole fight, since the player never leaves cover. In-cover
  talents (Braced, Composure and their Perfect versions) apply to every shot.
- Fighting in the open is a fight without the cycle.
- `enemy_out_of_cover` stays the user's assumption; the enemy's own cover isn't modelled.

**Reporting:**
- A note gives the cycle, the share of this fight spent behind cover (a fight that ends
  mid-cycle has less than a whole cycle's share, and one that ends in its first peek has
  none) and `peekHitShare`. Coverage says `in_cover` was set by the cycle, not chosen.
- Without incoming fire `peekHitShare` isn't needed. If one is set anyway, the note says
  it isn't used.
- Time to kill and time to clear include time behind cover, since targets aren't hit then.

**Scope:**
- Weapon rotation, added in a later stage (2026-11-03), follows the same rules:
  - only shots wait for a peek, so swaps and reloads under way carry on across boundaries;
  - an empty weapon's swap or reload starts at the cover boundary, like a single weapon's
    reload;
  - only the weapon in hand reloads behind cover. Swapping when empty, a magazine isn't
    reloaded there: topping it up every cycle could hold off the swap forever, so it empties
    and the swap comes (owner, 2026-11-03). The exception is a weapon a raised shield
    holds in hand, since no swap can come. Swapping after kills, it's topped up;
  - after a swap, the drawn weapon still waits out the last shot's fire interval;
  - a forced shield swap or kill swap that comes due behind cover waits for the next
    decision (a boundary or a reload's end), as forced swaps already wait for the next
    shot or reload end. It may then use up part of the peek.
- The cover cycle needs driven triggers (`cover-cycle-unsupported`), like the other fight
  options that change during a fight.
- CLI options, then Desktop fields.
- Contracts advance, since fights with the cycle change results.

## Alternatives considered

- **Share of time in cover:** reduce enemy hits and in-cover talent uptime by a
  percentage, with no timing. Less work, but reload timing and cover-dependent buffs lose
  their meaning. The owner preferred the cycle.
- **Partial cover** (some hits still landing in cover): not proposed. It needs data on
  how often cover is penetrated or flanked (ledger P06, flanking and partial cover).

## Decided points (owner, 2026-11-02)

1. **Reload on entering cover:** whenever the magazine isn't full. On 2026-11-03 the owner
   excepted a rotation swapping when empty.
2. **Enemy fire behind cover:** fully blocked, counted as blocked shots. Cover never
   breaks.
3. **Phase boundaries:** firing stops at the boundary even mid-burst. A reload in
   progress continues across boundaries.
4. **In cover while peeking:** the player shoots from cover, so `in_cover` stays on, with
   a low hit chance set as a share of the normal one, with no default.

The build comes in stages, each reviewed before merging.

## Consequences

- Fights with a cycle shoot only during peeks. DPS over the whole fight drops by about
  the cover share, which is the point.
- Survival numbers (time to down, armor breaks) improve with cover, by the time behind
  cover and by `peekHitShare` while peeking.
- No pack change is needed: `in_cover` already exists, and the cycle is a simulation
  assumption.
