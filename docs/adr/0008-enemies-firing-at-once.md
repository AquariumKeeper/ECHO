# ADR 0008: Several enemies firing at once

Status: Accepted, 2026-11-03, by the owner's answers to the open points below.

## Context

A simulated fight has one target at a time. PR #106 made that target set enemy fire. The
owner asked for an option to have several enemies firing at once, which would also let
talents that need more than one enemy work.

Today's closest thing is the pack's `enemy_attackers` input ("Enemies Attacking"). It
multiplies one target's fire, so:
- all the attackers share one profile and one distance;
- killing the target never reduces the fire, because the next one brings the same fire;
- shock blocks a share of attempts, standing in for one attacker being shocked.

Talents that depend on several enemies fall into three groups:

| Group | Examples | Today | Needs |
| --- | --- | --- | --- |
| Counting enemies | Bleeding Edge, Incessant Chatter, Hoarder | A user assumption or a fixed reading | Engine: a count of live enemies. Pack: rules that read it |
| Switching target | Busy Little Bee, Shakedown, Gangland Hit, Ostracize, Unnerve | Assumption inputs | Engine: a real target choice. Pack: rules |
| Damage or effects reaching other enemies | Bewildered, Plague of the Outcasts, Regicide, Creeping Death, The Trap, Hostile Negotiations, Indirect Transmission | Bewildered, Plague, Regicide, Creeping Death and The Trap are unmodeled; the others have no talent rule of their own, only set bonuses | Engine: pools and statuses on every enemy. Pack: new rule kinds, so a ShadeTech request |

## Proposal

**The option:** `enemiesAtOnce`, a whole number from 2 to 10. Unset, fights run exactly as
they do now, so no existing result changes.

**The group:** the next N targets in the fight's order (`targetSequence` and
`targetDistances`, or the chosen profile) are all alive and firing.

**As enemies die:** both behaviours are offered. By default the next enemy joins at once,
so N keep firing all fight. `enemiesShrink` makes the group shrink instead: nobody replaces
a dead enemy, and fire falls with each kill. Shrinking needs a fixed number of enemies,
which `clearKills` gives. When that equals N, every kill shrinks the group. When it's
larger, the remaining enemies step in as a reserve until none are left, and the group
then shrinks. It can't be smaller. Once the last enemy is dead nobody fires or is shot,
skills find no target, the weapon starts no new reload or swap, and readings stay on the
last enemy. The fight keeps its fixed length, as clearing a count already does, so DPS over
the whole fight counts that empty time.

**Focus fire:** the player shoots one enemy at a time, as now. The others wait at full
health, unless the player has left them (ADR 0011), and keep firing. A kill moves the player to the next enemy after the retarget
delay, which becomes the time to aim at someone already there.

**Enemy fire:**
- Each live enemy has its own attempt clock, with its own profile's cadence, damage and
  accuracy. Enemies start evenly staggered across the shortest interval in the group, so
  they don't all fire on the same instant and each fires within its own first interval.
- Each enemy fires from its own distance, given by `targetDistances` (from 2026-11-03, once
  the pack had distance bands, R44). Without target distances they share the fight's.
- A dead enemy's clock stops, and the next enemy takes its place in the stagger. Its wait
  keeps its share of an interval at its own cadence, so kills don't turn the fire into
  volleys.
- Every attempt still draws its own roll, in time order.
- A cover cycle blocks all of them behind cover, and scales all of them while peeking.
- Shock blocks only the shocked enemy's own fire.

**Recovery and bonuses:** they follow the enemy being shot, as now. The player's own
state applies to every enemy's hits: an enemy that isn't being shot hits with its own
profile against the player's current defenses. A limit: the state also carries the
statuses of the enemy being shot, so a rule making enemy damage depend on the target's
status would reach waiting enemies too. No pack rule does that today, and statuses on each
enemy come with stage 2 below.

**Reporting:** a note gives the group size. The incoming-fire report counts attempts as
today.

**With `enemy_attackers`:** setting it above 1 together with this option is refused, so
enemies are never counted twice. Fights using that input alone keep their results.

**Scope of the first stage:** enemy fire and focus fire with the next enemy joining at once,
in the engine and CLI. The Desktop field follows.

## The game's limit (R46, 2026-11-03)

ShadeTech found a `Shoot` ticket allowing 2 enemies to fire at one player, but only four special
behaviours claim it, so ordinary enemies may ignore it or the engine may apply it out of view. Only
an in-game count settles it. Until then `enemiesAtOnce` is an upper bound, and the result says so.

## The enemy count (stage 1, 2026-11-04)

A `count` rule writes the enemies alive in the group to a key. It reuses `on`, `within`, `max` and
`atLeast`, so it adds a kind and no fields to R46's proposal:
- **`on` is `sample`:** an event, with `second` for a period of 1 s, which is all the source needs.
  A rule on an event holds its reading, and starts from none until that event first comes, as
  Incessant Chatter does before its first reload. A rule with no event follows the group, which
  suits the in-range effects, since enemies don't move. So `hold` isn't needed.
- **No `of`:** only enemies are counted, by the owner's decision on `targeting`. A count of a status
  needs statuses on every enemy, so ADR 0011 added it, with the rule's `status`.
- **Only with `enemiesAtOnce`:** one enemy at a time isn't a claim that nobody else is around, so
  those fights keep the user's assumption and the result says so. No existing result changes.

## Later stages

1. Done above.
2. Enemies that keep their own state. The owner accepted R47's order, which puts this before choosing a target.
   ADR 0011 says how, with a shooting pattern to make it matter.
3. Choosing which enemy to shoot, for the switching talents.
4. Damage and statuses reaching other enemies, with new pack rule kinds from ShadeTech.

## Alternatives considered

- **Keep `enemy_attackers` and add a count reading:** far less work, and it would serve the
  counting talents. Fire still wouldn't drop as enemies die, which is what this option
  is for.
- **All enemies fully simulated from the start:** every enemy with its own statuses and
  stacks. It's the end point of stage 4, but nothing needs it until spreading talents have
  rules, and it touches nearly all of the fight loop.

## Decided points (owner, 2026-11-03)

1. **As enemies die:** offer both: the next enemy joining at once, or the group shrinking.
2. **With `enemy_attackers` above 1:** refuse the combination.
3. **Which talents first** after enemy fire: counting.

The build comes in stages, each reviewed before merging.

## Consequences

- Fights with the option take more enemy fire than one-at-a-time fights, and survival
  numbers drop.
- Contracts advance when the option arrives. Results without it don't change.
- Multi-enemy talents don't change just because this exists: each group needs its stage,
  and most need pack rules.
