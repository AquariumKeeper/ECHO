# ADR 0013: Effects that reach other enemies

Status: Accepted, 2026-11-09, with the owner taking the recommendation on the first four points below; the fifth keeps its recommended default.

## Context

ADR 0008's stage 4, "damage and statuses reaching other enemies", which ADR 0011 lists as R47's stage 3, "damage to
others, with spacing"; the two count stages differently. Enemies in a group already hold their own
pools, statuses and ticks (ADR 0011), but no rule writes to an enemy the player isn't shooting. Eight of the pack's 17
unmodeled talents need that, counting each Perfect, and so does the last part of one partial talent, Plague:

| Talent | What reaches others (R47) | Reach |
| --- | --- | --- |
| Regicide | A headshot kill explodes for 400% weapon damage, with bleed | 5 m around the victim |
| Creeping Death (and Perfect) | Applying a status applies it to every enemy near the target; 15 s (10 s) cooldown | 8 m (10 m) |
| Plague of the Outcasts | On a death, its stacks move to one enemy | 25 m |
| Bewildered | 50% of weapon damage goes to another enemy instead | 30 m |
| The Trap | A headshot kill burns and slows the other tags (R94: any headshot kill, tagged or not, though the tooltip says a tag's) | Tagged enemies |
| Explosive Delivery (and Perfect) | A thrown skill explodes 1.5 s after landing | 5 m around the skill |
| Fireworks Show | Delayed explosions from a Festive Grenade | 20 m |

Indirect Transmission, Unnerve and Apex Predator, modeled today by sets or assumptions, would gain the same way.

**The source fixes no spacing** (R47). Enemies stand where cover and navigation put them, so who is within 5 m of whom
can only be an assumption, and ShadeTech advises a user input. Their reading of the radii: tight (5 to 10 m) is
sensitive to spacing, medium (15 to 25 m) covers most of a typical group, and wide (30 m and up) is nearly always met.

## Proposal

**A spacing assumption, `enemySpacing`** (metres), for fights with `enemiesAtOnce`, and refused without, as
`focusSeconds` is. The enemies in the group stand on a line by their place in the group, neighbours this far apart, so
two enemies are their difference in place times the spacing apart. At 4 m, a 5 m reach finds the neighbour on each side,
and 25 m up to six on each side. One number, and each radius reaches as far as it should. Places don't move: an enemy
joining takes the dead one's place, and when the group shrinks (`enemiesShrink`) the empty places stay empty, so the line
doesn't close up. Enemies in reserve aren't in reach until they join. It has no default, and the result says what it
assumed.

**Without it**, a reaching rule finds nobody: the fight runs, and the result names the talents, as it does for armor
kits and skills a fight doesn't have. A fight with one enemy at a time reaches nobody either.

**Three rule shapes**, each on an event, reaching the enemies in the group within `within` metres of the enemy the
event is about (the victim of a kill, the target of a hit):
- **A blast:** damage, an amount from a stat (Regicide's 400% of weapon damage), to each enemy in reach, and an optional
  status on them. Explosive Delivery and Fireworks Show fit, once grenades have data (P04).
- **A spread:** when the event applies a status, the same status goes on each enemy in reach, every so often
  (Creeping Death).
- **A pass-on:** on a death, what the victim held goes to enemies in reach: a talent debuff's stacks (Plague, to one),
  a status (Indirect Transmission, to one, refreshed to half its duration), or a mark (Unnerve, to all). "One" is the
  nearest enemy in reach, and between two at the same distance the next in group order.

Bewildered, moving a share of each hit to another enemy, and The Trap's tags are larger, and come after these.

**What reads which enemy:** an amount reads the player's build at the event, as a tick's does. Each enemy reached takes
it on its own pools, with its own armor, and a status there ticks as ADR 0011's do on enemies you left.

**Kills elsewhere:** a blast or a passed-on debuff can kill an enemy the player isn't shooting. As proposed, that's
ADR 0011's tick kill: it counts, for `clearKills` too, that enemy's place passes on, `target_death` reaches all talents
(so Plague's stacks move on again), and the player isn't interrupted. Whether `kill` reaches gear talents only is open
point 3.

## What this doesn't do

- **Positions that move**, or spacing by cover: the line is fixed for the fight.
- **Line of sight,** or a blast's falloff inside its radius: the source's falloff wasn't read (R47).
- **Grenades:** not in the catalog (P04), so Explosive Delivery and Fireworks Show wait.
- **Choosing a target by rule** (Weakest Link, Ostracize): ADR 0008's stage 3.

## Decided points (owner, 2026-11-09)

1. **The spacing model.** I recommend one spacing on a line, as above. The alternatives: a count per band (how many
   enemies within 10, 25 and 30 m), which is three inputs and harder to keep consistent; or everyone within every reach,
   which is simplest and overstates tight radii.
2. **Without spacing.** I recommend the effects reach nobody and the result says so, so existing fights keep their
   results. The alternative is refusing any fight with such a talent unless spacing is set.
3. **A kill elsewhere:** `kill` to gear talents only, as status kills are now? I recommend yes. The alternative counts it
   for the weapon too, which the source doesn't settle for blasts.
4. **The first talent.** I recommend Regicide: its values are read, its blast is the simplest shape, and it needs only
   the rows. Then Creeping Death and Plague's pass-on. Each needs ShadeTech's rows.
5. **The Desktop fields:** after the engine and CLI, as before. I recommend yes. Not put to the owner; the default
   follows the earlier stages.

## Stages

Each is reviewed before merging.

1. `enemySpacing`, the reach query, and the blast, in the engine and CLI, with no pack rule using it, so no result
   changes. Then ShadeTech's Regicide rows. The blast's damage and its status on the enemies reached (Regicide's bleed)
   were built on 2026-11-09.
2. The spread, then Creeping Death's rows. Built on 2026-11-09. (Indirect Transmission is a pass-on, stage 3.)
3. The pass-on, then Plague's and Unnerve's rows. A talent debuff's pass-on (Plague) and a status's (Indirect Transmission)
   were built on 2026-11-09. Unnerve's text puts a new mark on every enemy within 20 m of a kill rather than moving the
   victim's marks, so it was built the same day as a blast's `mark`, not a pass-on.

   ShadeTech's rows for Regicide, both Creeping Deaths, Plague and Indirect Transmission (R88 to R91) were adopted on
   2026-11-10. Regicide's blast has no bleed, by the live script. Unnerve's rows (R92) were adopted the same day. ShadeTech would
   like `within` and `share` to take a stat, so Indirect Transmission can read Proliferation's 15 m and 75%. Built on
   2026-11-11 as `withinStat` and `shareStat`, read at the fight's start, and Indirect Transmission's row reads them from the
   r100 candidate, adopted the same day (R100).
4. The Desktop's spacing field. Built on 2026-11-10: "Enemy spacing (m)" on the Simulate tab, under "Time on each enemy".
5. Bewildered and The Trap, after a reading request for each. R94 (2026-11-10) asks for both: which enemy takes
   Bewildered's share and what that share counts as, and what makes The Trap's tags, its cooldown and cut, and its burn.
   Nothing is built until the readings arrive. R94 was answered the same day. Bewildered's redirect was built on
   2026-11-10 as `simulation.redirects`: a share of each weapon hit to a random enemy in reach. The Trap was built the
   same day as `simulation.traps`, reaching the enemies the player has tagged at any distance. A tag needs the player to
   have hit that enemy, so under focus fire it springs only when the player moves between enemies. ShadeTech's rows for
   Bewildered (R96) and The Trap (R97) were adopted the same day (#203, #207). Explosive Delivery and Fireworks
   Show still wait on grenades (P04).

## Alternatives considered

- **Keep effects on the enemy shot, and scale them by a guessed count:** small, but the numbers would be guesses, and
  kills elsewhere would never happen.
- **Enemies on a grid, or in a circle:** closer to a room, but more inputs for a picture the source doesn't fix either.

## Consequences

- Reaching talents gain in fights with several enemies and a spacing; fights without either keep their results.
- Contracts advance when the option arrives.
- Spreads and status pass-ons take only statuses talents put on; a skill's own burn or bleed is left out. ADR 0015 (accepted
  2026-11-13) takes it in, as the owner decided.
