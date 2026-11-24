# ADR 0021: Gear set repairs under enemy fire

Status: Accepted, 2026-11-17. Item 1 of the coverage survey (SIMULATOR_COVERAGE_SURVEY.md). The owner took the proposal on all
four open points.

## Decisions (owner, 2026-11-17)

1. **Step aside** where the repair can't happen: Ground Control, Umbra, and Hackstep in fights without kits run without the repair,
   with a note naming the set and why.
2. **Hackstep with kits:** built, as the build's kit.
3. **Headache:** the consecutive-headshot cycle is built. Until then its fights under fire stay refused.
4. **Order:** the step-aside, then Hackstep with kits, then Headache.

Stage 1, the step-aside, is built (2026-11-17, #292). R126 asks ShadeTech for the readings stages 2 and 3 need.

### After R126 (owner, 2026-11-17)

ShadeTech's readings (R126_SOURCE_REVIEW.md, ShadeTech):
- **Hackstep:** the held kit press runs the ability instead of the normal kit: instant, no kit used, no block on the weapon, usable
  at full armor. Its 20 s cooldown (15 s with Compiler Optimization) runs from the use and is ready at the start. It adds 50% of max
  armor as bonus armor (100% with Multithreaded Execution), removed at 5 s with whatever is left, and repairs 20% of max armor at
  once, capped at missing armor. The instant kit (double press) still works with the four-piece: 25% of max armor, a kit used, its
  own 5 s cooldown, and it starts Hackstep's cooldown too. The damage bonus reads the bonus armor left from any source:
  `min(bonus armor / max armor / 0.05 × 0.02, 0.2)`, ending as the bonus armor runs out.
- **Headache:** marksman-rifle projectile damage only. A headshot adds 1; a body hit, a shot that deals no damage, and a headshot
  that doesn't kill at a count of 3 or more are misses. A miss resets the count, the headshot bonus and Lucky, unless Lucky (once
  per cycle) forgives it. A swap away resets everything; nothing else does. Steps: the first gives +80% to the next headshot, with
  any weapon, until used; the second repairs 10% of max armor, or at full armor adds 10% of max armor to bonus armor while it's below
  50% of max armor (100% with Stand Tall), with no duration; the third fills the magazine; from the fourth, a headshot kill gives all
  three.

The owner's calls on the three points the source doesn't settle for fights:
5. **When a fight uses Hackstep:** at the kit threshold, as kits are, when its cooldown is ready.
6. **The instant kit:** modelled in fights with Hackstep, where the normal kit is gone. While Hackstep cools, a fight below the
   threshold uses an instant kit from the carried count, on the instant kit's own cooldown; it starts Hackstep's cooldown when
   Hackstep isn't already cooling. Fights without Hackstep keep normal kits only (ADR 0010).
7. **Hackstep's damage bonus:** driven live from the player's bonus armor in any fight under enemy fire, from any source, as the
   source reads it.

Stage 2's first piece, the live reading for point 7 (`kind: "bonus_armor"`), is built (2026-11-17, #295). Its second, the kit
ability and the instant kit (points 5 and 6: `armorKit.abilities`, `armorKit.instant` and the `kit_ability` event), is built too.
Under point 5 an instant kit is used only while Hackstep cools, so its starting Hackstep's cooldown never arises. Whether the
ability also raises the kit event the kit talents read (Efficient, Energize, the specialization kits) isn't in the readings, so for
now it fires only `kit_ability`; the request asks. Stage 3, Headache's cycle (`simulation.headshotCycles`), is built too, so the
engine work is done; one request asks for both sets' rows (R127, ShadeTech).

R127's answer (ShadeTech) brought `r127.candidate.1`, adopted 2026-11-17, which finishes this ADR:
- **The kit event:** Hackstep's ability doesn't raise it, as far as the source shows, so fights fire only `kit_ability` for it. An
  in-game check with Energize is listed.
- **Hackstep's bonus:** a separate multiplier on all damage dealt, continuous in the bonus armor left.
- **Headache's cap:** of max armor. Its next-headshot bonus goes on any weapon's headshot.
- **Two planner bugs** ShadeTech found were fixed first (#301): `bonus_armor` rows read from a file were refused, and a set talent
  whose only rule is a cycle didn't join fights.

Headache follows the source: a miss is a marksman-rifle shot with no hit, a body hit, or a non-killing headshot at 3 or more, and a
swap away resets.

## Context

Four gear sets' four-piece talents carry a repair stat that fights don't model. Any fight under enemy fire with one of them is
refused (`incoming-repair-unsupported`), so a build wearing the set can't be simulated under fire at all. The four:

| Set (talent) | The repair, by the pack's text | Repair stat |
|---|---|---|
| Future Initiative (Ground Control) | When you repair an ally, you and allies within 5 m are repaired for 60% of that amount (120% with Advanced Combat Tactics). | `ground_control_proximity_repair_percent` |
| Umbra Initiative (From the Shadows, Into the Light) | Out of cover, gain 10 Shadow stacks a second up to 50; in cover they're spent 10 a second, each repairing 0.8% of armor. | `umbra_shadow_available_repair_per_stack` |
| System Corruption (Hackstep Protocol) | Replaces armor kits with an instant ability on a 20 s cooldown (15 s with Compiler Optimization): repairs 20% of armor, 50% bonus armor for 5 s (100% with Multithreaded Execution), and 2% weapon damage per 5% bonus armor gained, up to 20%. | `hackstep_armor_repair_share` |
| Hotshot (Headache) | Consecutive Marksman Rifle headshots: the first raises the next headshot by 80%, the second repairs 10% of armor (bonus armor at full armor, up to 50% of current armor; 100% with Stand Tall), the third refills the magazine; from the fourth, each consecutive headshot kill gives all three. A missed headshot resets the cycle (Lucky allows one). | `headache_armor_repair_share` |

What fights model today:
- **One player, no allies.**
- **Cover** (ADR 0009) is a peek cycle in which the player never leaves cover. A fight without the cycle never enters it.
- **Armor kits** (ADR 0010) are used only when the fight's kit options are set. A build that replaces the kit (Hackstep, Firewall,
  Survivalist) is refused with kits on.
- **Shots** fire `shot`, `hit`, `headshot`, `body_hit` and kill events. There's no event for a shot that misses.

So three of the four repairs can't happen in a fight as fights are modelled now:
- **Ground Control** needs an ally to repair.
- **Umbra's Shadow repair** needs the player to move from the open into cover. In the cycle the player is always in cover, so no
  stacks build; in the open, they build but are never spent.
- **Hackstep** is the build's armor kit. A fight without kits uses none, so it uses no Hackstep either.

Headache's repair can: a Marksman Rifle landing consecutive headshots reaches it in any fight. Hackstep's could, in a fight with
kits.

## Proposal

1. **Step aside where the repair can't happen.** A repair stat whose trigger the fight can't reach doesn't refuse the fight. The
   fight runs without the repair, and the result's notes name the set and say why: no allies, no move between the open and cover,
   or no armor kits. The rest of the set (Ground Control's full-armor damage bonus, Into the Light's stacks, Hackstep's damage bonus)
   reads the user's settings as today. This covers Ground Control, Umbra, and Hackstep in fights without kits.
2. **Hackstep as the kit, in fights with kits:** instead of refusing, the fight's kit becomes Hackstep's ability. Used at the kit
   threshold, with no count limit and its own cooldown, it repairs at once and grants temporary armor, and drives the
   `hackstep_qualifying_bonus_armor_percent` key from the grant. ShadeTech reads the details in the source.
3. **Headache's cycle:** a count of consecutive Marksman Rifle headshots, reset by a shot that isn't one, giving the repair at the
   second (bonus armor at full armor), with the 80% next-headshot bonus and the third's magazine refill. This needs a miss event, and
   ShadeTech's readings.
4. **Order:** the step-aside first (engine only, no pack change), then Hackstep with kits, then Headache. Each of 2 and 3 is one
   engine stage and one request.

## Open points (decided above)

1. Step-aside: as proposed, or keep refusing until each set is modelled.
2. Hackstep with kits: build it, or leave it stepping aside (fights with kits keep refusing).
3. Headache: build the cycle, or let its repair step aside too (the fight runs without the repair).
4. Order: as proposed.
