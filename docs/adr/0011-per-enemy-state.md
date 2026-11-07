# ADR 0011: Enemies that keep their own state

Status: Accepted, 2026-11-04, with the owner taking the recommendation on all four open points below.

## Context

The owner chose per-enemy state as the next fight work (R47's stage 1). Today a fight has one set of
target state:
- `target`: one `TargetPool`. Enemies waiting in a group sit at full pools, and are made when they arrive.
- `StatusTriggerState` and `StatusDamageState`: the statuses and ticks of the enemy being shot.
- `ShockFireState`, and `priorPelletHits`: the same.

`ClearTarget` wipes all of this at a kill, and the player then moves to the next enemy.

## The finding that shapes this

**With today's rules, per-enemy state alone changes no result.** The player shoots one enemy until it dies, and
no shipped rule writes to any other enemy. So no living enemy ever holds a burn, a mark or lost armor. R47's stage 1
talents split in two:
- **Left on an enemy you shot:** burns and bleeds, Afterburn, Hostile Negotiations, Doctor Home. These need the
  player to leave a living enemy.
- **Written to enemies you aren't shooting:** Unnerve marks those near a kill, and Plague moves its stacks on a
  death. These need a new rule kind and no change to who is shot.

So state on its own is unseen work, and something has to make it matter. The owner chose `focusSeconds`, the first decided point.

## Proposal

**Enemies hold their own state.** Each enemy in the group has its pools, statuses, ticks, shock and pellets from when
it joins until it dies. Talent state that belongs to the player (buffs, stacks, armed counters) stays with the player,
except rules that already mean one enemy: those cleared by `target_death`. Doctor Home's mark and Afterburn's and Shakedown's stacks are
that today, and without this a mark on one enemy would pay out on another's death. Those rules are held per enemy, and
their key reads the enemy being shot.

**A shooting pattern, `focusSeconds`.** It's for fights with `enemiesAtOnce`, and refused without. The player shoots an
enemy for this long, then moves to the next living enemy in the group, in turn. Enemies in reserve aren't chosen. Unset is
today's focus fire, so no result changes. This is an assumption about play with no source behind it, so it has no
default, and the result says so.
- **The clock** starts when the player arrives at an enemy, and restarts on a kill or a move.
- **A move** waits for the weapon's next decision, as an armor kit does, and behind cover for the next peek. So a reload
  finishes first.
- **With one enemy left** the player stays, with no delay and no event.
- **During a move** the player takes the retarget delay, which runs as the wait between a kill and the next arrival does:
  profile and distance read the enemy being moved to, nobody is shot, and a skill landing then hits nobody.

**Ticks run on every enemy.** A burn or bleed keeps ticking on an enemy you left, on that enemy's pools. A tick keeps the
amount it took when applied, as it does today. A bonus on its armor factor reads the player's state at the tick.

**A tick killing an enemy you left:**
- It counts as a kill, for `clearKills` too, and only that enemy's state goes. The enemy being shot keeps its own.
- That enemy's place in the group passes on, as for any kill. The player isn't interrupted and no retarget starts.
- `kill` reaches gear talents, and `target_death` all talents, as status kills do now, reading the dead enemy's held rules.
- Weapon swaps after a kill don't fire, since no weapon made it.

**What reads which enemy:**

| Reading | Enemy |
| --- | --- |
| A status rule's key, `target`, `pellets`, distance rules | The one being shot |
| Enemy fire, and shock stopping it | The enemy firing |
| Recovery and bonuses against a target | The one being shot, as ADR 0008 has it |
| A tick's pools | The enemy it's on |
| A `count` | The group; with `status`, those it's on |

**Time to kill** runs from first reaching an enemy to its death, time away included, and the result says so.

**Bleeding Edge:** a `count` with `status: bleeding` read on `second`, within 15 m, by the source (R46). It reuses `status`,
so adds no field. Its talent's `status` reading keeps the same key: that runs with one enemy at a time, and the count takes over
with several. It's the first rule to ask ShadeTech for.

**A new event, `target_changed`:** fires on a move, while the player is still on the enemy they're leaving. A kill isn't a
move: `target_death` covers that. ShadeTech says Shakedown and Gangland Hit fit existing fields with it.

## What this doesn't do

- **A reload clearing what's on enemies you left:** Afterburn's stacks reset on a reload, and in game that reaches every
  enemy. Here it reaches the one being shot, so stacks on an enemy you left outlast the reload until it dies.
- **Choosing a target by rule** (Ostracize, Weakest Link): the pattern is fixed and has no ranks. That's R47's stage 2.
- **Damage to others** (Bewildered, Regicide): R47's stage 3, with spacing.
- **Enemies moving:** each keeps its distance, as now.
- **Enemy fire reading what the firing enemy holds:** its profile and distance are read, but a rule making enemy damage depend on that
  enemy's health or pellets would read the enemy being shot. No shipped rule does.
- **Rules written to waiting enemies** (Unnerve, Plague): these need a new kind of rule, which comes later.

## Decided points (owner, 2026-11-04)

1. **What makes the state matter?** I recommend `focusSeconds`: it reaches burns, bleeds and Bleeding Edge, the widest
   group, with planner work alone. It's an assumption of ours, not sourced. The alternative is a rule kind for
   Unnerve and Plague first, which is sourced but reaches fewer builds and needs ShadeTech's rows.
2. **The pattern:** by time or by shots? I recommend time: a shot count varies with rate of fire.
3. **A kill by a tick on an enemy you left:** count it, and move the group on? I recommend yes, as above, so the group stays full.
4. **The Desktop field:** with the first stage, or after the CLI, as for several enemies? I recommend after.

## Stages

Each is reviewed before merging.

1. The state moves onto each enemy, with results unchanged, shown by the existing tests. This touches most of the fight
   loop (`DummyWeaponSimulator` is 1,920 lines, with 36 uses of `target`), so it stands alone.
2. Rules cleared by `target_death` held per enemy, then `focusSeconds` and ticks on enemies you left, in the engine and CLI. Then the Desktop field.
   Built on 2026-11-04, Desktop field included. A kill on an enemy you left holds that enemy while it runs, so `ClearTarget`
   and ticks read the right one.
3. `count` on a status, and `target_changed`, built on 2026-11-04, then ShadeTech's rules (R55).

## Alternatives considered

- **State with nothing to use it:** R47's order as written. It's safe, but no user sees anything until later work arrives.
- **A random target each shot:** needs no timing, but isn't how anyone plays, and would make every shot a move.
- **Keep one target, and scale for enemies left behind:** far smaller, but the numbers would be guesses.

## Consequences

- With `focusSeconds`, each enemy takes longer to die, and ticks run on more of them. Status builds gain against
  burst builds, on an assumption the user chooses.
- It runs only with `enemiesAtOnce`, so with incoming fire and talent triggers, and plain DPS fights can't use it.
- Contracts advance when the option arrives. Fights without it keep their results.
