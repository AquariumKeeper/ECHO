# ADR 0022: Skills that play no part in a fight step aside

Status: Accepted, 2026-11-17; done 2026-11-17 (`r128.candidate.1`). Item 2 of the coverage survey (SIMULATOR_COVERAGE_SURVEY.md). The owner took the proposal on both
open points.

## Decisions (owner, 2026-11-17)

1. **Which skills:** only those the pack marks. A new list, `simulation.skillsAside`, names each skill that plays no part in a
   fight, with a short reason; ShadeTech fills it in one request. Damaging skills with no rule (Striker Drone and the others) stay
   refused until they have rows, so no damage is silently left out.
2. **In the fight:** a marked skill is never used. It has no uses and no cooldown, so cooldown grants and holds don't see it, and a
   skill use condition on its slot (ADR 0014) is dropped. The result's notes name each skill and its reason, and any dropped
   condition.

## Context

A fight with skills on reads a rule in `simulation.skills` for each selected skill and refuses the fight (`skill-rule-required`)
for any skill without one. Two kinds of skill have none:
- **Damaging skills not yet modelled:** Striker and Bombardier Drones, Shrapnel Trap, Demolisher Firefly, Leech Hive, The Howl,
  Blackout Seeker, Corrosive Trap and Gas Bomber Drone. Each needs ShadeTech's readings (survey item 3).
- **Skills that don't damage the enemy being shot:** the healing, buffing and scanning skills (Restorer, Reviver, Booster and
  Artificer Hives; Scanner, Remote, Jammer and Achilles Pulses; Decoy; the Smart Covers; Repair Trap; Reinforcer Chem Launcher;
  Mender Seeker Mine; Defender, Fixer, Tactician and Trophy System Drones), and EMP Sticky Bomb. Fights model one player and no
  allies, so most of what these do has nothing to act on.

A build carrying any of the second kind can't run with skills at all, so its other skill can't be simulated either.

Nothing in the pack says which kind a skill is. The engine could guess from the preview outputs' names, but that would depend on
how outputs are named, and a wrong guess would drop a skill's damage without a word.

## Proposal

1. **A pack list:** `simulation.skillsAside: [{ "skill", "why" }]`. Each entry is a known skill, listed once, with no fight rule,
   and a one-line reason of up to 200 characters for the result's note.
2. **In fights with skills:** a listed skill with no rule steps aside. The fight runs as if its slot were empty, and the note reads
   "These skills play no part in fights, so this one runs without them: Decoy (draws enemy fire away from the player)".
3. **Skill use conditions** on a slot whose skill steps aside are dropped, and the note names the slots.
4. **One request** asks ShadeTech for the list, with a reason for each skill read from the source, and for any skill whose effect
   should reach a fight after all (a buff on the player, damage to enemies) to be named instead.

## Open points (decided above)

1. Which skills step aside: those the pack marks, every skill without a rule, or those the engine finds with no damage output.
2. In the fight: never used, or used on its cooldown with no effect, so cooldown talents see it.

## Stages

1. **The engine:** the list, its checks, the step-aside and the note. Built 2026-11-17. The committed pack has no list yet, so no
   result moves.
2. **The request** for the list (one round), then its adoption. R128 sent 2026-11-17 (ShadeTech), answered
   (`082cee5`) with `r128.candidate.1`, adopted 2026-11-17. That finishes this ADR.
   - **Listed:** Scanner Pulse, Remote Pulse and Tactician Drone. They only mark enemies, and the "Target is pulsed" input covers
     the mark.
   - **Named, not listed:** sixteen skills whose effects reach a fight's damage or the player under fire (repairs, protection,
     damage buffs, statuses on enemies, a revive). Each needs its own rule; ShadeTech's handoff says what.

## Consequences

- Builds with a marked skill run with skills on, using their other skill.
- What a marked skill does stays out of fights: a Booster Hive's buff, a Decoy drawing fire, a Repair Trap's repair. Its effect can
  still be set by hand where the pack has an input for it.
- A damaging skill is never in the list, so a fight that runs never drops damage without saying so.
