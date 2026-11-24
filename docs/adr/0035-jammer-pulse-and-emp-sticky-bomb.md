# ADR 0035: The Jammer Pulse and EMP Sticky Bomb in fights

Status: Done, 2026-11-22; engine built (#389, #390), rows from `r161.candidate.1` adopted. With ADR 0034 done, the owner chose the Jammer Pulse and EMP Sticky Bomb next among the skills with
no fight rule. R160's readings are in, and the owner's calls follow them.
From the coverage survey's "Other skills with no fight rule" row (SIMULATOR_COVERAGE_SURVEY.md), and ledger P08 (disrupt).

## Context

Neither skill has a fight rule, so a build carrying one can't run with skills (`skill-rule-required`). R128 (ShadeTech,
R128_SOURCE_REVIEW.md) read them:
- **Jammer Pulse** (`skill_player_pulse_emp`): each hostile agent or skill object in the pulse gets a status for
  `pulse_emp_effectDuration_Final`, then EMP damage, 2,500 × the skill curve. NPCs mitigate EMP damage by a base of 1, so an
  ordinary enemy takes none of it.
- **EMP Sticky Bomb** (`skill_player_sticky_emp`): the blast runs `player_skill_damage_stickybomb`, as the other stickies do,
  with `Sticky_EMP_Damage_Final` (also 2,500 × the skill curve at base) over its radius, and disrupts for
  `Sticky_EMP_Duration_Final`. The damage type is set inside the compound, which wasn't read.
- **Talents react to the status:** Wicked (`status_applied`) and Pressure Point (any status on the target), by R128. So neither
  skill can step aside (ADR 0022) without dropping something fights model, and both stayed refused. More of the pack's rows read
  statuses: Vindictive and Perfect Vindictive (`whileStatus` any), Perpetuation (consumed on `status_applied`), Mosquito Song,
  Toxic Delivery and Chemical Agent. Chemical Agent's check covers disrupt, and its text says fights apply none, so its stacks and
  Reactant would move once they do.

What fights have:
- **Control statuses on enemies** (ADR 0017): a rule with `controlStatus`, a `window` and a status puts the status on the enemy
  being shot as the skill is used, and with an `area` on every enemy in it. It fires `status_applied`, reads as on for keys,
  `whileStatus` and counts, and counts in status uptime. `controlStatus` accepts shocked, disoriented, ensnared and blinded
  (the validator's own list, beside `GamePack.FightStatuses`, which also holds burning, bleeding and poisoned); only shocked acts on
  the enemy, stopping its share of the incoming fire.
- **An area centred on the enemy being shot:** a control status's `area` reaches enemies within it of that enemy, not of the
  player.
- **Skill damage** through `impact` and `area`, as the other stickies have.
- **No enemy skills or electronics:** enemies are profiles firing at the player.

The pack carries both skills' cooldowns and radii, the Jammer's disrupt duration and 2 s charge, and the sticky's 8 s duration. It
has no damage output for either ("Disruption is not a damage number").

## Questions for the source (R160)

1. **Disrupt on an enemy:** its status name, one for both skills or not; what it does to a human NPC in PvE (stops or interrupts
   firing, only skills and gadgets, or nothing a fight plays); which of the pack's targets it would act on; any immunity.
2. **The Jammer Pulse:** its reach (radius around the player, or a cone); whether the status lands after the 2 s charge; its
   duration and what scales it; its cooldown, from the use or the end; whether any target the pack uses takes its EMP damage.
3. **The EMP Sticky Bomb:** its damage type, and whether ordinary enemies mitigate it; whether it falls off past half the radius
   as the other stickies' does (R108); whether its disrupt reaches the whole radius or only enemies the blast damages; whether the
   8 s `duration_seconds` (scaled by Skill Duration) is the disrupt's, or Status Effects scales it; its timing and cooldown.
4. **Talents:** which react to disrupt (Wicked, Pressure Point, Vindictive, Perpetuation, Mosquito Song, Toxic Delivery, Chemical
   Agent), and whether disrupt counts for "any status".

The addendum (ShadeTech) added the more talents, the Jammer's reach against fights' areas, and the sticky's falloff,
reach and duration; it also corrected R160's wording on Disruptor Rounds, which applies disrupt rather than reacting to it.
5. **Both:** what a fight would need, anything else a fight plays, and in-game checks.

### R160's readings

R160 (ShadeTech, R160_SOURCE_REVIEW.md), read from the source without running it:
- **Disrupt** is one status, `Disrupt`, for both skills. A human enemy keeps firing: the status has no blocks-shooting flag (shock
  has), and no behavior gates ordinary shooting. Default and Advanced soldiers stagger once (at most every 20 s), and disrupted NPCs
  throw no grenades. **Machines stop:** disrupted skill objects fizzle, and automated turrets skip their shooting. The pack's
  **target 2** is all machines: 21 of its 27 declarations are automated turrets, the rest deployment boxes, Wardenclyffe units, an
  electric pulse and a crane. Targets 1 and 3 to 6 are human, with a few drones among 1 and 3.
  No immunity timer; how role resistance (0.2 veteran to 0.35 named) and diminishing returns shorten it isn't in the data.
- **The Jammer Pulse** reaches a sphere around the **player** within its radius (20 m at tier 0), with a clear line (inferred). The
  status lands once as the pulse fires, after the 2 s charge and up to 0.5 s of animation. Duration and radius as the pack's;
  cooldown 30 s from the use. Its EMP damage reaches only machines; ordinary NPCs mitigate all of it.
- **The EMP Sticky Bomb** deals EMP damage (2,500 × the skill curve × skill damage) with the other stickies' falloff, mitigated as
  the Jammer's (inferred). Its disrupt goes on every hostile the blast hits across its radius (inferred) and on the enemy it's
  stuck to. That disrupt's duration is the status's default, 15 s × (1 + Status Effects) on an NPC (inferred), not
  `duration_seconds`, which is the bomb's armed life. It detonates no sooner than 1 s after it's thrown; cooldown 30 s from its end.
- **Talents:** Wicked, Toxic Delivery, Perpetuation (spent by it), Vindictive and Chemical Agent see disrupt; Pressure Point too
  (inferred). Mosquito Song reads no status. Only Chemical Agent's text ("Fights apply no disrupt") moves.
- Nineteen in-game checks.

## Decision

The owner's calls (2026-11-22), each the recommendation:
1. **Disrupt stops a machine's fire, not a human's.** A disrupted enemy whose profile is a machine (target 2) stops firing while
   it lasts, as a shocked one does; human enemies keep firing. Their stagger and grenades aren't played.
2. **The Jammer reaches around the player.** It disrupts every living enemy within its radius of the player, not of the enemy
   being shot.
3. **EMP damage is left out.** Neither skill deals damage in fights; only machines would take it, and how their mitigation of −1
   applies isn't in the data. The result's text says so.

## Plan

1. **Readings** (R160, readings only), sent 2026-11-22 (ShadeTech, addendum `9c7c511`), answered `55e296c`.
2. **The engine:**
   - **2a, built** (2026-11-22): `disrupted` joins the fight statuses and `controlStatus`'s list. A pack stat `enemy_disrupt_stops_fire` (0 or 1, read
     per firing enemy's profile, as the other enemy fire stats are) makes a disrupted enemy's fire stop as shock stops it, through
     the same credit: an attempt is blocked when the enemy is shocked, or disrupted with the stat on, so one with both is blocked
     once. As for shock, that's one attacker's share where a profile aggregates several. A pack without the stat stops none; with
     it 0, disrupt is a status talents see and nothing more. Statuses are held only in fights with talent triggers, so static
     fights stop nothing. Per profile, the stat is an approximation: target 2's few non-turret machines stop too. Tested on test
     rows: a Jammer's 3 s disrupt stops target 2's fire for its window (no hit lands in it) and blocks none after, leaves target 1's,
     counts in uptime;
     with a Shock Trap's shock at once, three aggregated attackers are blocked exactly as by the shock alone (two credits would
     block more, which the test catches).
   - **2b, built** (2026-11-22): a rule field `areaFromPlayer` measures a control status's `area` from the player: an enemy is reached when its own
     distance from the player is within it. Fights already give each enemy that distance (the fight's distance, or its own under
     target distances). Without any distance, enemies stand along the line from the enemy being shot as today (it at 0, the others
     `k × spacing` away), the player taken to be beside it. The enemy being shot is reached only if within the area; events
     (`status_applied`) stay the enemy being shot's, so a use reaching only others fires none, as an area's others get none today.
     The status lands as the rule's `impactDelay` passes (the Jammer's 2 s charge; the Howl's row plays a delayed control status
     already). Other enemies are reached only where areas reach them today (several at once and a spacing, ADR 0013). Tested on
     test rows with three machines: at 15 m all are disrupted however far apart, at 25 m none (not even the enemy being shot);
     under target distances 10, 30 and 10 m the one at 30 m keeps firing; without a distance, 5 m apart all are reached and 30 m
     apart only the enemy being shot.
3. **Rows,** then their adoption: both rules with `controlStatus` and `disrupted`; the Jammer's `areaFromPlayer`, `impactDelay`
   and cooldown from the use; the sticky's area, a new output for its disrupt's 15 s default as its window, and its cooldown from
   the use (as the Explosive Sticky Bomb's row), since a cooldown from the end would wait out the 15 s window too;
   `enemy_disrupt_stops_fire` (1 for target 2); text, including Chemical Agent's. R161 sent 2026-11-22 (ShadeTech),
   as `r161.candidate.1` on `r159.candidate.1`, also asking whether Mosquito Song's rows (`whileStatus` any with
   `anyStatusExcept`) should except disrupt, since R160 read it as reading no status. Delivered (ShadeTech,
   R161_HANDOFF.md) and **adopted**: the rules as asked; the sticky's `disrupt_duration_seconds` is
   `max(0.5, 15 × max(0, 1 + Status Effects))`; `enemy_disrupt_stops_fire` is 1 for target 2. Mosquito Song needs no change: its
   rows hold no status condition (the "any" rows were Chemical Agent's). ShadeTech's figures (the ACR with six Badger Tuff, 20
   fights of 60 s): against target 2 the sticky blocks 10.4 shots and the Jammer 0.8, each machine dying within 1.4 to 3.7 s;
   three machines at 15 m see nothing from the Jammer, since its first pulse lands 2 s in, after the first kill at 1.38 s and before
   the next arrives. ShadeTech suggested a pulse measured from the player reach living enemies in reach even between enemies; the
   owner agreed (2026-11-22), and it's built: in that fight the pulse now disrupts the two machines left, blocking their fire.

## Simplifications

- **Resistance and diminishing returns** don't shorten disrupt (not in the data), as for the other control statuses.
- **A line of sight** isn't checked: every enemy in reach is disrupted.
- **The Jammer's timing:** 2 s, not the up to 0.5 s of animation after the charge; a tap's half radius isn't played.
- **Between enemies** (the player moving to the next, or a kill's gap), a control use reaches nobody, as before. A pulse measured
  from the player is the exception, by the owner's call after R161: in such a gap it lands on every living enemy within its radius
  by its own distance, with no events (built 2026-11-22). Without a distance it still reaches nobody there, having no enemy being
  shot to measure the line from.
- **Other enemies need a spacing:** the Jammer reaches others only where areas reach them (several at once and a spacing), though
  measured from the player the spacing plays no part where a distance is set.
- **The sticky** lands on the enemy being shot at the use, its disrupt with its blast; in the game the enemy it's stuck to is
  disrupted from the stick, at least 1 s before. Its cooldown runs from the use rather than the bomb's end, about 1 s sooner.

## Consequences

- Builds with a Jammer Pulse or EMP Sticky Bomb would run with skills, where they're refused today.
- Against machine targets (target 2), each lowers incoming fire while its disrupt lasts.
- Talents reading a status on the target (Wicked, Pressure Point, Vindictive, Chemical Agent and others) would see the skills'
  disrupt, so builds pairing them with these skills move once they fight.
- No frozen build carries these skills, so nothing frozen moves.
