# ADR 0015: A skill's own burn or bleed reaching other enemies

Status: Accepted, 2026-11-13. The owner decided to model it, and took the recommendation on all five points below. Amended the
same day after R105: ShadeTech's reading of the scripts differs on the copy's damage and a pass-on's length, and the owner chose
to follow the source on both, and to model a spread starting wherever a skill's status lands (see "Amended after R105").

## Context

ADR 0013 built two ways a status reaches other enemies:
- **Spreads (Creeping Death and Perfect Creeping Death):** a status newly on the enemy being shot goes on each enemy in reach, for
  what's left of it.
- **Status pass-ons (Indirect Transmission):** at a death, each status on the dying enemy goes on the enemies in reach, for a share of
  the duration it went on with (50%, or 75% with Proliferation).

Both take only statuses the player's talents put on. A skill's own burn or bleed is left out. Examples are a Burn Sticky Bomb, an
Incinerator Turret's burn, and Stinger Hive's or a Bleed seeker's bleed (ADR 0012, R65). The fight results say so, and both talents
are `partial` for it.

**What the source does (R89, R91):** both scripts listen to each skill slot's latest skill agent as well as to the player. So they
spread or pass on the statuses a skill applies too, and those applications have the player as their source.

**How a skill's own status runs today (R65, PACK_SCHEMA_V2.md under `statusRate`):**
- It's on the enemy from its first application to the end of the skill's window, plus `statusWindow`. `statusWindow` is how long the
  status lasts after its last application. A skill that keeps applying keeps it on.
- It keeps the amount it first went on with, and two uses don't add. Its damage per second is the skill's `statusRate` output,
  counted as the skill's damage.
- **One burn or bleed per enemy, first come (R63):** while a talent's burn runs on an enemy, the skill's deals nothing and waits for
  it to end, and then counts as a new application. While the skill's runs, a talent's can't start there.

So with Burn Sticky Bomb and Creeping Death, the bomb's burn never spreads. With a burning skill and Indirect Transmission, the burn
dies with the enemy. Both talents read low for skill builds, by an amount nobody has measured.

## Proposal

**A spread or a status pass-on takes a skill's own burn or bleed as well as a talent's,** where the rule's row says the source does:
`skillStatuses: true` on its `spreads` or `passOns` entry. Rows without the field behave as today, so every result is unchanged
until ShadeTech's rows set it.

**What the copy is:** the copy is treated as a talent status that has been spread or passed on today (ADR 0013), and is
counted, scaled and ticked the same way:
- it ticks at the player's standard rate for that status, from its `statusDamage` row, not at the skill's `statusRate`;
- it's counted in burn and bleed DPS, not skill DPS;
- it sets off no other spread;
- it follows R63 on the receiving enemy: if a skill's own burn or bleed is already running there, the copy doesn't go on, just as a
  spread talent status doesn't today.

**How long it lasts:** as one application of the skill's status would. The skill keeps its status on the enemy it's hitting by
applying it again and again; nothing renews the copy on the other enemy. So:
- **A spread** gives what's left of the status on the enemy being shot, up to the status's duration from one application
  (`statusWindow`).
- **A pass-on** gives its share of that one-application duration, up to what was left on the dying enemy.

A skill status with no `statusWindow` goes on once as the use deploys and has no single-application duration, so its copy lasts what's
left of it (a spread), or its share of the span it was on (a pass-on), as a talent status's does.

**When a spread fires:** a skill status is newly on the enemy being shot when it fires `status_applied` there today:
- at its first application;
- when it starts dealing again after a talent's burn or bleed ends (R63 counts that as a new application).

A later batch or use that only keeps it on isn't new. The spread's cooldown (`every`) is the talent's one cooldown, shared by all the
statuses it spreads, as in the source.

**At a death with both:** an enemy has at most one burn and one bleed dealing damage at a time (R63), but a skill's own status can be
waiting behind a talent's. A pass-on takes, for each status, the talent's while it runs and the skill's only when no talent's does. So
each receiver gets one copy of each status.

**Only burn and bleed.** Those are the only skill statuses fights track. Control skills' statuses (shock, ensnare, blind) aren't
on enemies in fights (ADR 0012), so there's nothing to copy.

## Decided points (owner, 2026-11-13: the recommendation on each)

1. **The copy's damage.**
   - **The player's standard rate for the status** (recommended). The source applies the copy with the player as its source, as
     its own status, not as the skill's. It needs no new data. ShadeTech is asked to confirm it from the scripts.
   - **The skill's own `statusRate`.** The copy would deal what the skill's burn deals. That rate is the skill's own output, built
     from its tier, mods and the use that applied it, so it has no meaning apart from that use.
2. **How long the copy lasts.**
   - **One application's duration** (recommended), as above. ShadeTech is asked what the source copies.
   - **What's left of the status as the fight holds it**, including the rest of the skill's window. This copies a span that only lasts
     because the skill keeps applying it, so one turret burn could give each neighbour 20 s or more of burn every spread.
3. **A pack field, or always on.**
   - **A row field, `skillStatuses`** (recommended). ShadeTech sets it where the source shows it, so the data owns the behavior,
     as it does every other rule.
   - **Always on** for every spread and status pass-on. It's simpler, but it assumes every future talent of the kind behaves the
     same.
4. **Where the copy's damage is counted.**
   - **Burn and bleed DPS** (recommended), with the talents' statuses. The skill didn't deal it, and skill DPS keeps meaning what
     the skill itself deals.
   - **Skill DPS.** This would credit it to the skill that started it, but the copy isn't the skill's.
5. **Kills by the copy.** Recommended: the same as a tick kill by a spread or passed-on talent status today, which is a kill elsewhere
   (ADR 0013): `target_death` to all, `kill` to gear talents only. It isn't a skill kill.

## Amended after R105 (owner, 2026-11-13)

ShadeTech's R105 reading of both scripts:
- **Damage:** they re-apply the status with the triggering application's damage override. A skill sets that explicitly, so the copy
  carries the skill's own damage, not the player's rate. Whether the player's Status Effects rescale it isn't in the data.
- **A spread's length:** one application of the skill's status, which matches point 2.
- **A pass-on's length:** the share of the full application, with no cap at what was left, as for a talent's status.
- **Where a spread starts:** around any enemy the status lands on, not only the enemy being shot.

The owner's calls:
1. **Damage: follow the source.** A copy ticks at the damage per second the skill's status went on with (its `statusRate`) on the
   enemy it came from. Where the skill's status has no rate of its own, because the skill's rate only carries it as Stinger Hive's
   stings do, the copy ticks at the player's rate. It's still counted in burn and bleed DPS. A copy passed on again keeps the damage
   it carries.
2. **A pass-on's length: follow the source.** Its share of one full application, however little was left. This replaces point 2's
   cap for pass-ons; a spread is unchanged.
3. **Where a spread starts: model it.** Fights only put a skill's damage and status on the enemy being shot, so no other enemy ever
   carries one. A spread starting wherever the status lands therefore needs skills that reach several enemies, such as a Burn
   Sticky Bomb's area, which is a model of its own: ADR 0016 (accepted 2026-11-13).
4. **Adoption** waits for the first two in the engine, so results move once. Both were built in #225, and R105's rows were adopted the
   same day.

## What this doesn't do

- **Debuff pass-ons** (Plague's stacks). A skill has no debuff stacks.
- **Statuses other than burn and bleed**, as above.
- **A skill's own status reaching other enemies through its own impacts.** How far a skill reaches is the skill's own model (ADR
  0012). This is only about what talents copy.

## Stages

1. **The engine and validator:** `skillStatuses` on `spreads` and `passOns`, the copy as above, and the limitation notes. The
   committed pack's rows don't set it, so no result changes. Built on 2026-11-13.
2. **The request:** ask ShadeTech to confirm points 1 and 2 from the scripts, and for Creeping Death's, Perfect Creeping Death's
   and Indirect Transmission's rows with the field, along with figures.
3. **Adoption:** a deliberate result change for builds with those talents and a burning or bleeding skill, in fights with several
   enemies and a spacing. It's recorded in the ledger, and the three talents' coverage notes drop the omission.

## Alternatives considered

- **Leave it out:** the earlier recommendation. Both talents stay `partial` and read low for skill builds. The owner chose to model
  it.
- **Copy the skill's own status as a skill status on the other enemy.** That enemy would then carry a skill status the skill never
  applied, at the rate of a use that wasn't there, and its "one per enemy" rule would compete with the skill's own later uses there.

## Consequences

- With stage 3, builds that pair Creeping Death or Indirect Transmission with a burning or bleeding skill change in fights with
  several enemies and a spacing. Most gain, but not all. A spread's one cooldown now also fires on skill statuses, so a skill's burn can
  take the cooldown just before a talent's burn would have spread for longer, and some builds may lose a little. Their results move,
  as a deliberate change.
- Skill DPS keeps meaning the skill's own damage. Burn and bleed DPS gains the copies.
- The engine gains one row field. No option changes, and fights without the field are byte-identical.
