# ADR 0020: Bonus armor follows the fight's state

Status: Accepted, 2026-11-16. The owner's call after R120 (`r120.candidate.1`): of three ways to build Chemical Agent's Reactant, the
owner chose bonus armor that follows the fight's state, over a grant that replaces itself.

## Context

A talent's bonus armor is a modifier on `bonus_armor_percent`, gated on a key. The pack's `estimated_bonus_armor` turns that share into
an amount: `character_armor` times `bonus_armor_percent`, plus the party's. A fight under enemy fire starts the player with that amount
as bonus armor, spent before armor.

When talent triggers drive a key, the fight re-resolves the build as the key changes. Until now the player's pools had to stay as they
started: a state that changed the player's bonus armor was refused (`incoming-state-unsupported`). So a fight under fire was refused
as soon as any of these keys changed:
- **Chemical Agent's Reactant,** once R120's row drives `reactant_active` from kills. ShadeTech found one such fight refused.
- **Bloodsucker (and Perfect),** whose kill stacks each add bonus armor.
- **Kill Confirmed,** whose pickups turn on `memento_short_buff_active`, which adds bonus armor per armor core.

Adrenaline Rush already ran: its count is fixed for the whole fight.

R120's source reading for Reactant: one bonus armor node, added again when a second kill restarts the timer and removed when the timer
ends. The likelier reading is a single bonus that a second kill sets again, not a second bonus beside the first. Fights stacked
`temporary_armor` grants, which would overstate it.

The options put to the owner:
1. **Bonus armor follows the state** (chosen). Reactant keeps its modifier and drops its grant.
2. **A replacing grant,** with ShadeTech's offer of a candidate without the modifier. Outside fights, switching Reactant on would no
   longer show its bonus armor, and the other talents would stay refused.
3. **A replacing grant,** with fights ignoring a bonus armor change on a key whose own row grants temporary armor. A special case, and
   the other talents would stay refused.

## Decision

1. **The state's bonus armor:** when the fight's state changes, the player's bonus armor follows its `estimated_bonus_armor`.
   - **A rise** adds the difference to what's left. Damage already taken stays taken.
   - **A fall** keeps no more than the new amount, and none at 0.
   - **The same amount** changes nothing. So a second qualifying kill while Reactant is on restarts the key's timer without refilling
     its bonus armor. Whether the game refills it isn't in the data (R120).
   - **A lapse and a new bonus:** a state that drops the amount and then raises it again, even at the same moment, takes what's left
     and then gives the rise in full, as the source removes a bonus when its timer ends and adds it at the next start.
2. **Spend order:** timed grants first, soonest to lapse first, then the state's bonus armor, then armor and health.
3. **A downed player** gains none.
4. **The trace:** a rise is recorded as bonus armor granted, at the time of the change. A fall isn't recorded.
5. **Still refused:** a state that changes the player's armor or health, the number of attackers, the enemies' cadence or their hit
   chance. Target sequences and distance bands still need the same bonus armor for every profile and band at the fight's start,
   checked at setup. Once a driven state holds, its bonus armor is read with the target in play's profile and distance, like any
   other stat, and the player's follows it.
6. **Reactant:** its row drops the `temporary_armor` grant, and its 25% modifier carries the bonus armor in fights as outside them.
   R121 asked ShadeTech for that candidate. `r120.candidate.1` isn't adopted; `r120.candidate.2` is, on 2026-11-17 (#282).
7. **Contracts:** no fight that ran before gives a different result, so `d2p-simple-buffs-v62` and `d2p-incoming-random-v75` stay,
   as with #261.

## Consequences

- Bloodsucker and Kill Confirmed fights under fire with talent triggers run, where they were refused.
- Reactant's armor follows the source's one restarted bonus with no new grant mode.
- `temporary_armor` grants keep stacking, for the rules that have them (Ablative Nano-Plating, Full Stop).
- A state's bonus armor is read from the build, so anything that raises `bonus_armor_percent` while it holds counts: a talent's own
  modifier and any other.
