# ADR 0010: Armor kits in fights

Status: Accepted, 2026-11-03, by the owner's answers to the open points below.

## Context

Simulated fights have no armor kits. Under incoming fire, armor only comes back from
regeneration and talents, so time to down and survival read worse than play. ShadeTech's R45
answers (R45_ARMOR_KITS_HANDOFF.md) give the game's rules, read from source. None of this is
verified at runtime yet; the in-game checks are listed in the handoff.

**A normal kit:**
- Repairs 100% of max armor, at once, when a 2.5 s use timer ends. It never gives bonus armor.
- Each hit during use adds 0.3 s, up to 4.9 s in all.
- During use the player can't fire, reload, swap weapons or aim, and can't enter or leave cover.
- Being downed cancels it, and a cancelled kit isn't spent.
- It has no cooldown of its own.

**An instant kit** is a double press of the same key. It repairs 25% of max armor at once, with
no use timer and no blocks, on a 5 s cooldown, and spends a kit.

**Carrying:** 3 kits, plus capacity from Base of Operations perks, a dungeon reward and one exotic
backpack. Fights have no refills.

**Exceptions:**
- Survivalist and Firewall replace the repair with one over time.
- Hackstep Protocol replaces the kit entirely, with bonus armor and a 20 s lockout.
- Talents react to kit use: Energize, Efficient, Selfless, and Gunner, Demolitionist and
  Sharpshooter specialization talents.

## Proposal

**The options:** `armorKits`, the number of kits carried, 1 to 10, and `armorKitAtPercent`, the share
of max armor below which the player uses one, from 1 to 100. They come together, with no
default, so the assumption stays the user's. Unset, fights have no kits and run exactly as they do
now. A kit is used if armor is below the threshold at the weapon's next decision, and one is
left. Armor that has recovered by then doesn't start one.

**A normal kit in a fight:**
- It needs incoming fire and talent triggers, like other fight options.
- Once the kit is used, the weapon stops for the use time. A reload that's running isn't
  cancelled: the kit waits for it to end, since the game blocks reloading during use.
- Hits during use extend it as the source says. A hit a shield absorbs doesn't: the source
  says each damage event, and whether that includes a shield isn't known. This is an
  assumption, and the result note states it.
- With a cover cycle, a kit waits for the next cover phase, and starts at its boundary. A kit used
  behind cover isn't extended by hits, since none land there. A use outlasting the cover phase
  runs on through the peek, firing resumes when it ends, and the block on entering or
  leaving cover is ignored: the cycle's timing stays the user's. A kit comes before that cover
  phase's reload, which then runs only if cover remains. The result note says so.
- The repair lands at the end, capped at the armor missing. Being downed cancels it without
  spending a kit.
- A swap under way finishes first, like a reload.

**Reporting:** kits used per fight, the armor they repaired, and a note giving the rule used.

**Pack data:** the numbers belong to the pack, so a ShadeTech candidate supplies them through new
fields: repair share, use time, the extension and its cap, and the instant kit's share and cooldown.
The planner builds the fields first, as for `distanceDefaults`. The normal kit's row was adopted on 2026-11-04.

## Stages

1. Normal kits in the engine and CLI, with the pack fields, then the Desktop field.
2. Instant kits, if wanted. A setting would choose which kit a fight uses.
3. An `armor_kit` event, so Energize, Selfless and the specialization talents can react. Built on 2026-11-04. It fires when the
   repair lands, as R45 proposes, for normal kits, the only kind so far, and their rules come from ShadeTech (R56).
4. Survivalist, Firewall and Hackstep Protocol.
5. Efficient, with a `chance` on a rule and a grant that returns a kit.

## Alternatives considered

- **A priority list for kits, skills and swaps (ledger P10):** the owner's longer-term aim. A simple
  rule now doesn't block it, and a list needs skills that can be chosen, which fights don't have yet.
- **Treating kits as extra armor at the start of a fight:** a small change, but it would lose the
  use time, which is the real cost of a kit.

## Decided points (owner, 2026-11-03)

1. **When a kit is used:** once armor falls below a percentage the user sets, with no default.
2. **Which kit:** normal first; the instant kit can follow.
3. **With a cover cycle:** a kit waits for cover.
4. **Until stage 4:** a fight with kits is refused for Survivalist, Firewall and Hackstep Protocol, with a
   message. Those builds still run without kits.

The build comes in stages, each reviewed before merging.

## Consequences

- Fights with kits survive longer and deal less damage while a kit is in use.
- Results without the option don't change. Contracts advance when it arrives.
- Time to down reads better than today, and closer to play, but it rests on the user's rule for
  when a kit is used.
