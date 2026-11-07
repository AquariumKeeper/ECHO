# ADR 0012: Skills that change during a fight

Status: Accepted, 2026-11-04, with the owner taking the recommendation on all four open points below. Stage 2 built on 2026-11-05 with R57's answer.

## Context

A fight works out its skills once, before the first shot. `SkillDamageSchedule` reads each slot's preview outputs, and
`DamageEvents` lists every impact and every 0.1 s batch of a rate for the whole fight. `SplitAt` cuts those batches at enemy
attempts known in advance, and the loop walks the list.

So a fight can't follow a skill that changes partway through, and refuses instead:
- **At setup,** for a set talent's rule: 4-piece Apex Engine with damaging skills or a shield its stacks change
  (`skill-state-unsupported`, `shield-state-unsupported`), unless the build is already at the top Skill Tier. So is Energize with kits and a shield (R64), since Overcharge gives even a full tier 6 a tier above it.
- **Partway through,** for any other rule, the first time a state with a different plan is resolved.

**This reaches common builds today.** A probe with Explosive Sticky Bomb on the shipped pack: an ACR with Spike, In Sync or Future Perfect
is refused with `skill-state-unsupported`, in a 3 s fight and a 60 s one. Those talents already have rules that drive skill damage or
efficiency. Energize will join them when R56 arrives, and other talents as their rules come.

## Proposal

**Uses are decided as the fight goes.** Each slot has a next deployment. When it comes, the skill reads its preview from the player's
state at that moment, with the weapon in hand, and makes that use's impact and batches. Since a fight has a target profile for
each enemy, it reads the preview for each profile in the sequence, so damage still follows the enemy in front.

**A use keeps what it deployed with:** its impact, rate and window, by profile, and a stack landing mid-window changes the next use, unless
its rule has `rateRead: live`. This was open point 1, and it matters: every first use comes at the start of a fight, before anything has built, and
Assault Turret, Incinerator Turret, Stinger Hive and Oxidizer deal all their damage as a rate.

**R57's answer:** Incinerator Turret and Stinger Hive follow changes live, and Oxidizer does with one link unconfirmed. Burn Sticky Bomb holds, as does a
burn or bleed already on an enemy. Firestarter, Airburst, Explosive Seeker and Artillery read once per explosion or shell, which an impact does here.
Assault Turret isn't known, so it holds until its in-game check. So `rateRead: live` re-reads a rate at each 0.1 s batch, as that batch ends, from the
player's state and the weapon in hand. Holding is the default. A use whose rate reads 0 as it deploys makes no batches, so a live rate can't start later.

**The cooldown is read at deployment too.** The next use is that far on, plus the window where the rule counts from its end. A
change during a cooldown waits for the next one. This was open point 2. R57 says turrets read it at deployment. For the others it isn't proven.

**The weapon in hand decides,** so the refusal for skills that differ by weapon goes too: a build's skills follow whichever it holds at
deployment. Which weapon the game reads isn't settled, and it's a smaller question than the two above.

**What keeps a fixed plan's fight identical,** byte for byte. A use's batches are made as they are now, with the same rules:
- they're cut at the same attempts known in advance, which stay as they are, including no cuts where there are none today, and
  cuts at attempts a shrinking group never makes;
- pieces of a batch share one hit, so `skill_hit` still fires once for it;
- events at the same time keep today's order, which differs with incoming fire.

**What stays refused:** a bad preview output in a state only reached in a fight, or the work limits, still refuse when they happen. A
slot losing its preview refuses too. Those are pack errors, no shipped pack has them, and a check for them can't be complete: the states
multiply.

## What this doesn't do

- **Shields:** a shield's health, regeneration and cooldown stay fixed. See open point 3.
- **Overcharge:** this ADR's reads carry it. The pack's tier formula reads `skill_overcharge`, so a use deployed under Energize at Skill Tier 6 reads tier 7 (R64). Future Perfect's waits on a pack row: `whileStatAtLeast` is built, and R70 asks for the row.
- **Cooldown cuts:** Apex Engine's 10 s reduction, and other talents that cut a running cooldown, need their own event.
- **Charges and player timing:** a skill is used as soon as it's ready, as now.
- **Other mid-fight refusals:** triggered changes to magazine size, pools, enemy cadence or player capacity stay as they are.

## Decided points (owner, 2026-11-04)

1. **Does a running skill follow a change?** The source should say, and I haven't asked. I recommend asking ShadeTech now (R57), building
   stage 1 meanwhile, and starting stage 2 with their answer. If it follows, a use would re-read its rate as the state changes.
2. **Does a cooldown under way follow a change?** The same.
3. **Shields:** with this, or later? I recommend later, since a shield also touches the incoming-fire model. It leaves any talent
   that changes Skill Tier refused with a shield it changes: 4-piece Apex now, and Energize once its rule arrives.
4. **The setup check:** Apex Engine's check for skills goes. Its check for shields stays. I recommend not widening it to other rules:
   it tries one state per rule, so it misses changes that need two keys, and it would refuse fights that run today because they never
   reach that state. So a shield refusal can still depend on the rolls, as it does now.

## Stages

Each is reviewed before merging.

1. Skill damage made as the fight goes, from a plan fixed at setup, with results unchanged, shown by the tests and the comparison with `main`.
   Built on 2026-11-04. Runs take the same time as before, since each use's events are made once for a whole run.
2. Each use reads the state, and the refusals for skills go. Fights that ran keep their results, and contracts advance. The whole-fight
   list goes too, so the stream takes on the work limits and the checks on a fight's length, and becomes the one source of event order.
   Built on 2026-11-05. A fight whose skills can change sees each deployment as an event, in the order a whole-fight list would hold, so the use reads the
   state when the fight gets there, and a tick ending at the same moment still comes first. The read changes nothing, so it can't add a refusal from a state
   the weapon never reached. The list stays in `SkillDamageSchedule` only as what the tests compare the stream with. A work limit a whole fight would
   hit is still checked when a run starts, from each weapon's cooldowns, and a stream bounds its own for a state that shortens them. Mixed targets keep the
   check that a skill's timing doesn't change with the profile, for each weapon, and in each state a use reads.
3. Any in-game check ShadeTech's answer leaves open.

## Alternatives considered

- **Keep refusing:** no engine work, but Spike, In Sync and Future Perfect stay out of skill builds, and more join them as rules arrive.
- **List every use up front for each possible state:** fine for one stack count, but states multiply, and they depend on the rolls.
- **Always follow every change:** each batch would resolve the build. If ShadeTech says a running skill follows, re-reading only when the
  state changes avoids that.

## Consequences

- Builds refused today run: 4-piece Apex Engine and the talents above, with damaging skills.
- Skill damage stops being known before a fight, so it's read per use, per profile.
- The comparison of both weapons' skill plans at setup goes.
- Results for builds that ran don't change: 49 fights compared with `main` match byte for byte, but for the contracts and the note.
