# ADR 0014: When the player uses skills, swaps and kits

Status: Accepted, 2026-11-10. The owner took the recommendation on all five points below, and added a fight-time condition to the
first set.

## Context

The ledger's P10 ends with "a priority list for skills, swaps and kits", which ADR 0010 called the owner's longer-term aim.
Today each action has one fixed rule:

| Action | When a fight takes it today | Blocks the weapon |
| --- | --- | --- |
| A damaging skill | As soon as it's ready: at 0 s, then each time its cooldown ends (ADR 0005, ADR 0012) | No: a use is instant |
| A swap | When the magazine is empty, or after a kill (`rotation.mode`) | Yes, for the swap time |
| An armor kit | Below the user's armor share, at the weapon's next decision (ADR 0010) | Yes, for the use time |
| A reload | When the magazine is empty; in cover, when it isn't full | Yes |

When two are due together, the order is fixed: a reload or swap under way finishes, then a kit, then a reload or swap.

**What a fixed rule costs:**
- **Skills whose damage depends on a talent's window.** Spike (`spike_active`, after a headshot), In Sync and Future Perfect
  raise a skill's damage or tier while their buff is up. A fight uses the skill at 0 s, before any of them can be up. ADR 0012
  made each use read the player's state as it deploys, so holding a skill for the window would now pay, but nothing holds it.
- **Skilled and Future Perfect** reset or charge on kills, so when the next use comes matters.

Kits already have a condition (armor below a share), and swaps have two modes. Skills have none. Fights use damaging skills and a
shield only; healing and support skills aren't in fights, so saving one for low armor isn't a case yet.

**Skills are the real gap.** A skill's use doesn't block the weapon, so skills don't compete with reloads, swaps or kits for time;
they only need a condition. The actions that do compete already have a fixed order that matches the game's blocks (ADR 0010). A full
ordered list would mostly re-express that order.

## Proposal

**A condition for each skill slot,** `skillUse` in the engine and CLI, keyed by slot (`skill_slot_1`, `skill_slot_2`). It holds a
skill that's ready until the condition holds, checked at each of the player's decisions and at each event that can change it:

| `when` | The skill is used once ready and | For |
| --- | --- | --- |
| `ready` (the default) | at once, as today | Every build today |
| `key` with a driven key | that key is on (a buff up, stacks above 0) | Spike, In Sync, Future Perfect |
| `enemiesAtLeast` with `count` | at least that many enemies are alive in the group | Area skills, with `enemiesAtOnce` |
| `afterSeconds` with `seconds` | the fight has run that long | An opening the player saves a skill past |

**A held skill's cooldown starts when it's used,** as in the game. A skill that's never used in a fight reports no uses, and the
result names it and its condition, so a condition that never holds can be seen.

**Only driven keys can be named.** A key the build's rules drive, so it changes in the fight; an assumption's key would never change.
The engine refuses any other key with a message.

**Without the option** every fight runs as today, byte for byte. Contracts advance when it arrives.

**Reporting:** each slot's uses per fight, which the result doesn't give today (only skill DPS and first-fight events), and for a
held slot the time it was held, so the cost of waiting shows.

**Pack data:** none. These are the player's choices, not the game's, so they're assumptions, and the result says so.

## What this doesn't do

- **An ordered list across all actions.** Reloads, swaps and kits keep today's order (above). If a build needs another order, a
  later stage could let the user set it.
- **New swap conditions,** such as swapping when the other weapon's talent is ready (Brazen, Frenzy). Swaps keep their two modes.
- **Skill deploy time.** A use stays instant. The game has a deploy animation, which the source hasn't been read for; a reading
  request would come first.
- **Choosing the target** a skill goes to: ADR 0008's stage 3.
- **Shields:** a held shield isn't a fight shape the planner has; shields keep their rules.
- **Healing and support skills,** which fights don't include. Once they do, an `armorBelow` condition would save one for low armor.

## Decided points (owner, 2026-11-10)

1. **Conditions on each skill slot,** not a full ordered list. Reloads, swaps and kits keep their order. The alternative was one list
   of actions, each with a condition, taken in order at each decision: more general, but skills don't compete for time, so the order
   would rarely matter, and it's harder to set up.
2. **The first conditions:** `ready`, a driven `key`, `enemiesAtLeast`, and `afterSeconds`, which the owner added to the three
   recommended. The target's health, or armor once healing skills are in fights, can follow.
3. **When a held skill is used:** at the first decision where the condition holds. A decision is the next shot, reload end or swap
   end, plus the moment a key turns on, an enemy joins or the fight time is reached. Checking only at shots was the alternative.
4. **A condition that never holds:** the skill isn't used, and the result names the slot and its condition. A user-set timeout was
   the alternative.
5. **The Desktop:** a "Use when" choice under each skill slot, after the engine and CLI.

## Stages

Each is reviewed before merging.

1. `skillUse` in the engine and CLI with `ready`, `key` and `afterSeconds`, and each slot's uses and held time in the result. No
   result changes. Built on 2026-11-10.
2. `enemiesAtLeast`, for fights with several enemies. Built on 2026-11-11. A group's living count only falls (a dead enemy's
   place is filled at once until the group runs out), so in practice it stops using a skill once fewer are left.
3. The Desktop's "Use when" per skill slot. Built on 2026-11-11. The summary shows each held slot's uses and held time per fight from the same day.
4. If wanted: new swap conditions, or an order the user sets for reloads, swaps and kits.

## Alternatives considered

- **A full ordered list now:** see point 1. It can still come later, and the conditions above would be its entries.
- **Optimise the timing automatically,** trying each moment and keeping the best: results would read best-case play, slow and
  hard to explain. The user's rule keeps the assumption visible.
- **Hold every skill for its talent's window automatically:** guesses what the player wants; a player may prefer the skill's
  damage early to its bonus later.

## Consequences

- Builds whose skills depend on a talent's window can be simulated as they're played, and usually read higher.
- Each condition is the user's assumption about play, and each result lists it.
