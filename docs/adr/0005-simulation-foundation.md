# ADR 0005: C8 simulation foundation contracts

- **Status:** Implemented for the weapon-only dummy slice; full C8 acceptance is pending.
- **Related:** ADR 0002 (own seeded RNG contract), ledger F60/F62/F69.
- **API:** `Simulation/DummyWeaponSimulator`; not the eventual encounter API.

## Scope

One equipped active weapon fires at an unlimited, unarmored dummy. Core resolves
the build once with `enemy_target=0`, leaving all other assumptions fixed. Core's
resolved body, headshot, critical-body and critical-headshot damage channels are
used directly; average mode uses Core's practical expected damage per shot.
Resolved errors refuse a run; remaining resolver diagnostics are retained.

Skills, target pools/kills, swaps, incoming damage, repairs, pack triggers and
status timers are not simulated. Returned rounds, Echo, per-shot bonuses and
magazine bonuses are explicitly rejected until their mechanics are implemented.
The UI Run Simulation command stays disabled. Results carry these limitations.
This slice does not establish full frozen encounter-trace parity.

## Time and work

Time is seconds. The interval is `60 / resolved RPM`. The first shot is at zero;
the horizon is half-open `[0, duration)`. After a magazine's final round, the next
shot interval elapses, then reload starts, then the resolved reload duration
elapses. A reload completion precedes its same-time shot. Reloads count only when
their start is within the horizon. A no-reload weapon ignores its magazine limit.
Fractional magazine/pellet counts are rejected rather than silently rounded.

Duration is finite, above zero and at most 600 seconds; iterations are 1-10,000.
Pellets are whole counts 1-1,000. The run has a 50-million work-unit ceiling
(one unit per shot and one per random pellet), checks cancellation during firing
and between fights, and refuses nonadvancing times or overflowing damage/DPS.
No partial success is returned on cancellation or failure.

Optional first-fight event capture is capped at 100,000 records. Further events
are omitted with `TraceTruncated=true`, while damage/statistical aggregates still
cover the full run. This trace limit does not truncate the simulation itself.

## Random stream v1

`d2p-splitmix64-v1` defines unsigned 64-bit arithmetic modulo 2^64. Increment is
`0x9e3779b97f4a7c15`. Mix applies xor/right-shift 30, multiply
`0xbf58476d1ce4e5b9`, xor/right-shift 27, multiply `0x94d049bb133111eb`, then
xor/right-shift 31. Initial state is `Mix(seed + increment * (fightIndex + 1))`.
Each integer draw first adds the increment to state, then returns Mix(state).
A uniform double uses the upper 53 bits divided by 2^53, in `[0,1)`.

Each fight has its own mixed stream, so running another fight first cannot change
its results. Random mode rolls each pellet's hit, then, only on a hit, crit and
headshot in that order. Each pellet receives an equal share of the resolved shot
channel. This is the app's stream; it does not reproduce Go's random sequence.
The pinned seed-1 integer vectors were calculated independently in Python.
Any future draw-order or RNG change needs a new contract version.

## Average and statistics

Average mode runs exactly once, independent of seed and requested iteration count,
and assigns the resolved expected damage to each shot. It does not spread discrete
crit/head outcomes as the old simulator did; later trigger-aware average mode
needs its own explicit event/outcome policy and trace acceptance.

Random mode runs the requested number of fights serially for now. Damage bins
contain mean damage per one-second bucket; the last can be a fractional second.
They are not bucket DPS. Weapon DPS is each fight's total damage divided by the
requested duration. Summaries use the population standard deviation and nearest
rank-by-index quantiles (`round(p*(n-1))`, midpoint away from zero), not interpolated
quantiles. Shots and reloads are means over fights. Progress reports completed
fights in order. No scheduling-dependent parallel reductions are introduced.

The overall result contract is `d2p-dummy-weapon-v1/d2p-splitmix64-v1`.


## Static target extension

`RunAgainstTargets` uses the supplied target assumptions instead of forcing a dummy.
It reads armor/health and out-of-cover/armor/health factors from Core. Raw damage
first consumes armor using its factor; unused raw damage then consumes health with
its separate factor. Actual pool damage excludes overkill. Zero armor factors block
health until armor can be removed; health must initially be positive.

The extension contract is `d2p-static-target-v1/d2p-splitmix64-v1`. Target arrival is
kill time plus retarget delay (finite, 0-60 seconds), independently of reloads. The
next shot waits for both weapon readiness and target arrival. Reloads proceed during
retargeting. Kill events and scheduled arrivals are ordered by time, preserving
insertion order on ties. Damage never carries to a second target. Kills are the mean
over fights; KillTimes records first-fight arrival-to-kill durations, capped at
100,000 with TraceTruncated. A target may therefore arrive before the weapon is
ready, and that wait counts toward its kill duration. The horizon remains half-open.

This extension still holds all assumptions fixed, rejects unsupported shot mechanics
and excludes skills, swaps, trigger-driven buffs and incoming damage. It is not
full encounter acceptance and remains disconnected from the UI run command.


## Two-weapon rotation controller

WeaponRotation is a separate state machine, now connected to the damage loop
through the optional DummyFightOptions.Rotation setting.
It resolves both equipped weapon slots independently through Core, so active and
holstered ownership is reflected in each snapshot. Other assumptions remain fixed;
trigger-driven swaps and shield restrictions remain future encounter work.

Fire commits one shot only at/after ReadyAt. Each weapon retains its own magazine;
there is no passive holstered reload. WhenEmpty swaps to a loaded other weapon;
when the second empties it returns to the main weapon, adding that destination's
reload time if empty. AfterKill swaps only following a kill and only if the other
weapon has ammunition (or does not reload); otherwise it reloads the current one
when needed. Partial magazines are retained. ReloadedDestination explicitly marks
an empty destination reloaded as part of a swap action.

A shot interval precedes the action. Swap duration is the sum of the outgoing
holster and incoming draw halves: each uses half the base swap time divided by
that weapon's resolved switch multiplier and max(0.1, 1 + swap speed). Invalid
nonfinite/nonadvancing times refuse the transition before mutating state. Magazine
refills and the destination snapshot are committed prospectively, but firing is
blocked until ReadyAt. The caller owns scheduling and cancellation of an encounter.


## Integrated rotation loop

A null Rotation preserves both earlier single-weapon contracts. With Rotation set,
the result contract is `d2p-weapon-rotation-v1/d2p-splitmix64-v1`. The initial active
slot comes from assumptions; SecondSlot is explicit. Both resolved snapshots and
shot profiles are validated before firing, then reused. Each fight starts with
fresh ammunition state. Unsupported shot mechanics on either weapon refuse a run.
Kill-based rotation requires a target run rather than an unlimited dummy.

Shots read the current weapon's damage channels, pellet count and probabilities.
Target damage uses that weapon's armor/health factors while retaining the target's
remaining pools. Both snapshots must resolve equal initial target pools. Warning
diagnostics from both snapshots are retained. Active-weapon ownership changes;
other assumptions, trigger buffs and timers stay fixed.

The loop waits for both retarget arrival and rotation readiness. Swap completion
and destination reload are separate timestamps. Counts include only starts inside
the half-open fight horizon, and completions outside it are omitted. Rotation
traces identify the weapon slot for shot, swap and reload events; the original
single-weapon traces keep their empty slot labels. Events retain bounded capture
and stable chronological ordering. No random draws are added for swaps.

Swaps remain static: shield restrictions, swap-trigger talents, skills, incoming
fire and dynamic resolver states are still not modeled. UI execution stays disabled.


## Skill deployment planning

SkillDamageSchedule reads resolved Core previews and explicit simulation skill
rules. It emits deployments at zero and each cooldown cycle: cooldown alone when
measured from deployment, or active window plus cooldown when measured from end.
Windows may overlap and are clipped at the half-open fight horizon. DummyTimeline
integrates each window's rate exactly across one-second bin boundaries and adds
impact at deployment. Several impacts are explicitly aggregated per use; individual
Artillery shell timing is not yet modeled. Status fields do not apply statuses.

Missing rules, missing/negative/nonfinite referenced outputs, shields, fractional
impact counts, nonpositive cycles and overflowing damage are refused. Planning is
cancellable and capped at 100,000 deployments. This is a standalone skill/dummy
plan, not yet merged with weapon shots, target pools, skills during swaps or kill
and trigger events. UI execution stays disabled.


## Shared static skill/weapon loop

IncludeSkills opts into `d2p-static-skills-v2/d2p-splitmix64-v1`. Skills draw from
initial Core previews and merge into weapon readiness times; they continue during
reloads and empty-magazine swaps. Impacts are discrete at deployment. Continuous
windows use batches of at most 100 ms, also split at integer bucket boundaries.
A batch is credited at its right endpoint. The final clipped continuous batch is
included even at duration; deployment impacts and weapon shots remain excluded at
duration. Final-endpoint trace records are omitted under the trace horizon.

Skill events win same-time ties with shots, preserving authored order among equal-
time skill events. Skill damage uses unity armor/health factors as the frozen
simulator did, and overkill is discarded. No damage is banked during retargeting;
a batch overlapping a new target's arrival is prorated to its live share. Batch
kill times are approximate to the batch interval, not exact continuous crossings.
A skill kill updates target availability, kill count and first-fight kill durations.
Skill kills do not consume ammunition or roll random weapon outcomes.

WeaponDps stays weapon-only; SkillDps and TotalDps expose separate summaries and the
shared timeline contains combined damage. Null IncludeSkills behavior retains the
old single-weapon/rotation contracts. Skill planning is capped at a million damage
events and all event processing shares the run work/cancellation guards.

Kill-based rotations with skills now queue one swap request until the current
weapon action is ready. Rotations with different skill damage schedules between weapon states are
also refused, rather than freezing a weapon-dependent preview silently. Shields,
statuses, dynamic triggers, individual shell timing and incoming fire remain open;
UI execution stays disabled. The static batching model is not full frozen-trace
acceptance or a live-game accuracy claim.


## Skill-kill rotation requests

Static skill results now use `d2p-static-skills-v2/d2p-splitmix64-v1`.
A skill kill in AfterKill mode queues an external kill request. The current reload,
shot interval or swap finishes first; at readiness, an eligible loaded destination
can be drawn without consuming ammunition or adding another shot interval. Target
availability does not delay starting this swap. The next shot still waits for both
the swap and the target. Several skill kills while busy coalesce to one request.
If the destination has no rounds, the request is consumed without a swap. Empty-
magazine mode ignores external kills. A request at/after the horizon starts no
action. Weapon-generated kills retain their existing post-shot swap policy.

This policy deliberately does not interrupt reloads or model swap-trigger buffs;
it supplies deterministic external-event handling while dynamic trigger states
remain future work. Shared action scheduling keeps trace timestamps/counts identical
for shot-driven and skill-driven swaps.


## Simple timed-buff state foundation

TimedBuffState handles explicit simple buff rules supplied by a caller responsible
for equipped talent/weapon ownership. It is not yet connected to automatic event
emission in the fight loop. Stack, count, range, status, consumption, refill, reset,
pool and other nondefault rule fields are rejected rather than partially applied.

Events refresh matching keys through max(existing expiry, event time + duration).
Activity is half-open: a buff is off at its exact expiry. Driven keys override
manual assumptions; inactive keys are removed, following the pack's true-only
selector convention. Other assumptions are preserved. Shared-key rules do not
shorten a longer active buff. NextExpiry exposes the next active deadline.

Time must be finite, nonnegative and monotonic. All expiry arithmetic is validated
before an event mutates state. Resolve verifies rule membership in the pack and
true-only selectors, then calls Core with the driven assumptions; it does not
reimplement damage formulas. Automatic rule selection, trigger emission, expiry
scheduling and state-aware weapon/skill profile rebuilding remain pending.


## Simple buffs connected to random weapon fights

DriveSimpleBuffs opts into `d2p-simple-buffs-v1/d2p-splitmix64-v1`. It currently
requires random mode without skills or rotation. Directly selected legal talent
choices on gear and the active weapon select the pack rules. Copied-talent owners
and selected rules outside the supported simple-buff subset are refused. Rules for
holstered weapon owners are not driven. Owned keys start inactive even if manually
set. No default changes are made to the static simulation modes.

Random pellets emit at most one shot-level hit/crit/headshot/body-hit event each;
unarmored-hit eligibility is sampled before applying the shot to its target. Kill,
crit-kill and headshot-kill follow actual target death. Shot events occur after the
shot's damage, so a proc does not retroactively empower its activating shot. Reload
is emitted at its scheduled start. Skills, distance/status events and other event
names are refused when required by a selected rule.

Core re-resolves when driven context changes, and the damage/probability snapshot
is updated. Expiry is observed at the next shot/action boundary, including after
reload gaps; this mode does not yet report continuous buff uptime. Timing, magazine,
pellet, no-reload or target-pool changes are rejected at the activating event, even
if no later shot occurs. This avoids freezing a newly changed action clock. Buffs
are fresh per fight; no RNG draws are added for trigger state.

Average trigger outcome policy, stacks/pools, copying, skill/rotation integration,
shield restrictions, incoming fire and full frozen trigger acceptance remain open.
UI execution stays disabled.


## Simple timed stacks

The opt-in trigger contract is now `d2p-simple-buffs-v2/d2p-splitmix64-v1`.
TimedBuffState also accepts positive-duration stacks with Max 1-10,000, Gain 0-10,000
(zero means one), and optional Refresh. A stack key has exactly one rule owner;
mixed buff/stack keys and duplicate stack ownership are refused.

Without Refresh, each gain expires independently. At the cap, new gains replace
the earliest expiries. Refresh renews all active stacks to the latest deadline
before applying the bounded gain. Expiry is half-open. Numeric driven inputs are
written explicitly as counts, including zero, overriding manual/default values.
Core binding requires min zero, step one and sufficient declared maximum.

All expiry arithmetic and replacement lists are prepared before committing an
event. Lists stay bounded and ordered; gain application trims the oldest range
in one operation instead of repeatedly sorting. Buff behavior is unchanged.
Counters, zero-duration stacks, decay, shared pools, reset/consume events and other
nondefault fields remain unsupported. Timing-changing stacks are still rejected
by the weapon loop. Skills, rotations and average trigger outcomes remain pending.

## Stack reset, consumption and persistence

The opt-in contract advances to `d2p-simple-buffs-v3/d2p-splitmix64-v1`.
Simple stacks now accept ResetOn and ConsumeOn. Both clear the rule's stacks
before gains from the same event; clearing also works when the event adds nothing.
Damage is resolved before shot events, so a consuming shot benefits from stacks
that existed before it. Reload resets occur at the scheduled reload start.

Duration zero denotes persistent bounded stacks. Their internal infinite deadlines
are excluded from NextExpiry; they remain until reset or consumption. Positive
durations retain half-open expiry and optional refresh behavior. All updates,
including clears, are prepared only after expiry validation succeeds.

Required reset and consumption events must be among the fight loop's implemented
events. These fields remain unsupported on buffs, whose shared-key behavior is
unchanged. Pack Fast Hands and Breadbasket state rules are covered directly;
Fast Hands reload-speed changes still require dynamic timing support before that
talent can run in the fight loop. Counters, Every, decay, pools and other complex
rule fields, skills, rotations and average trigger outcomes remain pending.

## Dynamic reload duration

The opt-in contract advances to `d2p-simple-buffs-v4/d2p-splitmix64-v1`.
Core-driven buffs and stacks can now change reload duration. At the scheduled
reload start, expired state is removed and Core supplies the duration. That
duration is captured before firing the reload event and clearing reload-reset
stacks. Expiry or activation during a reload does not reschedule it; the next
reload uses the state present at its own start. Zero-duration reloads are valid;
shot intervals still advance time. Nonfinite or nonadvancing completion arithmetic
is refused.

This retains the C# contract's reload event at start. The frozen Go reference emits
its reload event at completion; reproducing that event timing is not required by
ADR 0002. Sampling before clearing lets Fast Hands affect the reload it earned.
Committed-pack Fast Hands is now covered in the fight loop with a legal critical
chance weapon roll: faster reloads yield more shots. Synthetic tests cover reset,
exact expiry at start, expiry during reload, reload-triggered buffs, zero duration
and a reload extending beyond the horizon.

Fire rate, magazine size, pellet count, no-reload mode and target-pool changes still
require further scheduling support and remain refused. Skills, rotations, copied
talents and average trigger outcomes remain outside this opt-in mode.

## Cooldown-limited and counted triggers

The opt-in contract advances to `d2p-simple-buffs-v5/d2p-splitmix64-v1`.
Simple buffs/stacks accept finite nonnegative Every and nonnegative Count. Every
limits the interval between actual activations; the first event is eligible at
time zero. The exact boundary is eligible, with no early-activation tolerance.
Cooldown-blocked events neither increment Count nor refresh an effect. Count zero
means every eligible event; otherwise each Count-th eligible event activates and
restarts the counter. Partial progress does not start the cooldown.

ResetOn and ConsumeOn now work for positive-duration buffs as well as stacks.
For counted rules, ResetOn clears partial progress and preserves earned activity.
For uncounted rules it clears activity. ConsumeOn clears activity while preserving
partial progress. Reset takes precedence when both fields match an event. Neither
operation resets the cooldown. Clearing precedes counting/gaining on that event.

Buff deadlines, counts and activation times are tracked per rule. Shared buff keys
are active while any owner is active, and NextExpiry uses the latest owner expiry
for each key. Stack ownership remains exclusive. All counter, cooldown, expiry and
clear changes are staged before committing the event, preserving atomic failure.

The committed Strained rule is covered through Core and the fight loop; Mosquito
Song's five-hit counter is covered directly in state. These tests verify the pack's
declared model, not unimplemented targeting or status mechanics. Persistent buffs,
refills (including Pummel), alternate gains, decay, pools and other complex fields
remain refused; additional rule kinds and integration with skills/rotations remain
pending. UI execution stays disabled.

## Persistent buffs and magazine refills

The opt-in contract advances to `d2p-simple-buffs-v6/d2p-splitmix64-v1`.
Duration-zero buffs now remain active until reset or consumption. Their infinite
internal deadlines are excluded from NextExpiry, including shared buff keys whose
persistent owner masks a finite expiry. Clearing that owner reveals any remaining
timed owner. Existing counter, cooldown, reset and consumption rules still apply.

Refill is now accepted on simple buff/stack rules. Fire returns a refill request
only when a rule actually activates; partial counts and blocked events do not
request a refill. This result is local to the event and is returned only after the
entire event commits successfully. It does not latch into later events.

The random single-weapon loop combines refill requests across all events from a
shot. After damage and triggers, it spends the shot's round, then restores a full
magazine if requested, before testing for an empty-magazine reload. Multiple
requests produce one full refill; refills cannot exceed capacity. No-reload
weapons need no ammunition update. A refill triggered by the reload-start event
does not cancel or shorten that reload, which already fills the magazine when it
finishes. This retains the C# reload-start event policy.

Committed Pummel now runs against targets, with fewer reloads and more kills.
Overflowing's persistent counted/consumed state is covered directly, but its
magazine-capacity change is still explicitly refused in the fight loop. Dynamic
capacity, alternate gains, decay, pools, additional kinds and skill/rotation
integration remain pending. UI execution remains disabled.

## Alternate gains and refreshing stack decay

The opt-in contract advances to `d2p-simple-buffs-v7/d2p-splitmix64-v1`.
Simple rules accept Also entries with an event name, Gain 1-10,000 and nonnegative
Count. The primary On event takes priority; otherwise the first matching alternate
entry supplies its gain. Each alternate has independent partial progress. The
shared activation cooldown is checked first, then the alternate counter, then the
rule's main counter. Partial or blocked events do not request refills or refresh
activity. All required alternate events must be implemented by the fight loop.

An uncounted reset or consumption clears alternate partial progress along with
activity. A counted reset restarts only the main counter, preserving earned
activity and alternate progress, following the frozen rule-state ordering.
These changes are staged with the rest of the event for atomic failure.

Refreshing stacks with positive Duration accept finite positive Decay. After each
gain, the remaining stacks plus bounded gain hold until time + Duration, then
drop one stack at each successive 1 / Decay interval. The first drop is at
time + Duration + 1 / Decay; exact drop boundaries remove the stack. Counts stay
bounded, and a gain during decay uses the remaining count and restarts the hold.
The implementation stores at most Max ordered drop deadlines. Nonfinite or
indistinguishable deadlines fail atomically. Decay on buffs, independently expiring
stacks or duration-zero stacks remains refused. Decay zero keeps prior behavior.

NextExpiry reports the next drop; Context observes decay at shot/action boundaries
without requiring additional RNG draws. Continuous uptime reporting is still
pending. Full Stop alternate gains and Capacitance decay are covered through Core;
synthetic fights cover alternate damage and decay during reloads. These cover the
declared pack rules, without claiming Full Stop's undeclared armor/refill mechanics
or dynamic skill-loop integration. Shared pools, additional kinds, incoming damage
and skill/rotation integration remain pending. UI execution remains disabled.

## Shared stack pools and purge

The opt-in contract advances to `d2p-simple-buffs-v8/d2p-splitmix64-v1`.
Stack rules may declare Pool and a positive PoolMax. All rules sharing a pool
must declare the same cap; pool metadata on buffs or a cap without a pool is
refused. After all rules process one event, their live stack counts are summed.
Reaching or exceeding the pool cap clears every member's stacks and partial
primary/alternate counters, preserving activation cooldown timestamps. Expired
stacks do not contribute. Pool changes are staged with the event for atomic failure.

Pool caps apply per emitted event, not once per shot. A hit can clear a pool and
the same shot's later headshot event can begin accumulating it again. Driven
context is re-resolved before subsequent damage; pools start fresh each fight.

Purge is supported on stack rules. An eligible activation while already at the
individual cap clears the stacks and alternate counters instead of adding a gain.
Reaching the cap itself does not purge until the next eligible activation. The
activation still starts cooldown and can request a declared refill. Buff purge
remains refused.

Committed Rebalance and Vicious Cycle rules are checked through Core, including
their declared weapon-damage bonuses and clearing behavior. Their required damaged
event is still unimplemented by the fight loop, so selecting them there remains
explicitly refused. Rebalance's post-reset bonus conversion and Vicious Cycle's
healing/status purge are not declared by these pack rules and are not claimed.
Incoming damage, additional kinds, skill/rotation integration and UI acceptance
remain pending.

## Ready and lockout booleans

The opt-in contract advances to `d2p-simple-buffs-v9/d2p-splitmix64-v1`.
TimedBuffState accepts ready and lockout rules. Both begin active and become
inactive after an eligible trigger until the rule's deadline, recovering at that
exact time. Ready ignores matching events while cooling down: they cannot extend
the deadline, increment counters or request refills. Lockout permits later
eligible events to extend its deadline. Other counter, alternate-event, Every,
reset and consumption policies still apply. Duration zero blocks indefinitely
until cleared. Unsupported stack-specific fields remain refused on boolean rules.

Driven boolean keys override manual assumptions. Inactive keys are absent and
active keys are true, using the same true-only selector binding as buffs. Shared
boolean keys must use one kind. For ready/lockout owners, any recovered owner makes
the key active, so NextExpiry uses the earliest recovery. It reports no future
transition for a key already active. Shared buff keys retain their latest-expiry
behavior; mixed boolean kinds on one key are explicitly refused.

Ready cooldown suppression happens before counter/side-effect processing, ensuring
busy events cannot spend progress or request repeated refills. This explicit C#
contract extends the frozen reference's ready-deadline behavior to these compound
fields. Event updates remain atomic across all rules and clocks.

Committed Unbreakable readiness controls Core's armor-repair contribution;
Vigilance lockout removes and recovers Core's damage bonus. These are state/Core
checks, not actual repair or incoming-damage simulation. Their armor_break and
damaged events remain explicitly refused by the fight loop. Synthetic hit-driven
rules test initially active damage, repeat suppression/extension and fresh state
across fights. Incoming combat, other kinds, skills/rotation integration and UI
acceptance remain pending.

## Magazine and cycle phase readings

The opt-in contract advances to `d2p-simple-buffs-v10/d2p-splitmix64-v1`.
PhaseTriggerState supplies pure readings from fight time and current ammunition,
separate from event-driven TimedBuffState. Magazine phases use the fraction fired
from the current capacity; a boundary belongs to the next phase and an empty
magazine uses the last phase. Percent rules emit floored missing-ammo percentages
from zero through 100. Multiplication precedes division when safe to avoid losing
whole percentages such as 29/100 to floating-point rounding.

Cycle phases use fight time modulo the final boundary, with exact boundaries
advancing to the next phase and the period returning to the first. They continue
through reload and retarget gaps; no RNG draws or continuous event scheduling are
added. Readings override manual assumptions, reset with the actual ammunition
after reload/refill, and start from full ammunition/time zero each fight. No-reload
weapons retain a full magazine reading, following their existing ammo policy.

Only pure magazine/cycle rules are accepted: increasing positive finite boundaries
and nonempty values, magazine boundaries ending at one, or percent with no phases.
Side-effect/event fields are refused. Keys require exclusive ownership and cannot
collide with driven event keys. Whole-round readings are checked. Pack membership
and every driven value must bind to a declared selector or numeric grid; percent
inputs must represent every integer from zero to 100, allowing finer input steps.

The random single-weapon loop refreshes phases before damage and samples reload
duration with the current readings. Core still supplies all damage/reload values.
Optimist now increases fight damage as ammunition is spent. Measured phase values
resolve through Core, but its changing fire rate remains explicitly refused by
the loop. Decoy King's declared time cycle is covered as a numeric reading; its
Decoy deployment pause/overcharge is not implemented. Continuous phase uptime,
changing shot timing, remaining kinds, incoming combat, skills/rotation integration
and simulator UI acceptance remain pending.

## Distance, prior-pellet and target readings

The opt-in contract advances to `d2p-simple-buffs-v11/d2p-splitmix64-v1`.
CombatTriggerState supplies pure distance, prior-shot pellet-hit and target-pool
readings. These keys have exclusive ownership across event, phase and combat
rules. Only declared pure metadata is accepted; side effects and unsupported
combinations are refused. Pack membership and selector/numeric bindings are
checked before running, and unrepresentable numeric readings fail explicitly.

Distance rules require an explicit Distance option when selected. The option is
finite, zero through 1,000 meters; individual numeric input bounds still apply.
Raw numeric distance rounds to whole meters, ties away from zero. Within is
inclusive; distance phase boundaries select the next phase, with the last phase
covering distances beyond its boundary. Distance is fixed for a fight and drives
only these rules, without changing other hit-rate or range assumptions.

Pellet rules read the previous shot's landed hits, starting at zero each fight.
AtLeast is inclusive and binds a true-only selector; count readings bind numeric
inputs. Actual hit rolls supply the count, including zero on a miss. The current
shot's damage uses the previous count; the new count is published afterward.
No RNG draws are added and no per-pellet target transfer is introduced.

Target rules read combined current armor and health before shot damage as a
percentage of the original combined pools. Scaled sums avoid overflow. The result
floors directly to the declared input grid (or whole percentage with no step),
so Blossom Harvest's ten-percent input never rounds a partial tier upward. This
is an explicit C# contract under ADR 0002 rather than copying integer percentage
then nearest-grid rounding. Retargeting restores 100%; an unlimited dummy stays
at 100%. The current shot cannot retroactively change its own amplification.

Tests cover committed Ranger, Challenger, Pack Instincts, Brazen and Blossom
Harvest readings, Ranger damage through Core and the fight loop, actual prior-hit
sequencing, armor/health transitions and retarget resets. Incoming combat,
statuses, dynamic timing, skills/rotation integration and UI acceptance remain
pending; these readings do not complete all talent mechanics.

## Status application and conditional events

The opt-in contract advances to `d2p-simple-buffs-v12/d2p-splitmix64-v1`.
StatusTriggerState tracks one target's burning, bleeding, shocked, ensnared,
blinded and disoriented deadlines. Declared status rules either observe a status
(including any) or count named events to arm application on the next hit. The
applying hit occurs after its damage; it does not retroactively benefit. A rule
whose arming event is hit applies already armed state before counting that hit.
Each application uses the applying shot's Core Status Effects bonus as the
duration factor (1 + bonus). Invalid factors or nonadvancing expiry fail atomically.
Refreshing never shortens an existing deadline. Expiry is half-open.

Retargeting clears the prior target's statuses while preserving talent counters
and armed effects. Kill events can still inspect statuses on the dying target.
Owned status selectors override manual assumptions; numeric observers read zero
or one for the single target, without claiming nearby-enemy or range simulation.
Rules and boolean/numeric bindings are checked against the pack. Unsupported
side effects and malformed observers/applications are refused.

TimedBuffState now supports WhileStatus for a named status or any. Gating happens
before alternate/primary counting or side effects, using live status state.
In the fight loop, each event processes status applications before these gates.
After the shot's events, status_applied is emitted once if any application occurred;
it can activate or consume declared timed effects. This explicit ordering avoids
dependence on pack rule order. State starts fresh each fight and adds no RNG draws.

Committed counted-status rules, Ignited's Core damage and Vindictive's conditional
Core bonus are covered. Synthetic fights verify applying-shot order, expiry during
reloads, status_applied buffs and clearing on retarget. This slice implements
application and conditions only: damage-over-time ticks, control effects, status
purging, skill sources, incoming damage and player repairs are excluded and listed
in result limitations. Second events and Chemical Agent's periodic rule remain
refused. Skills/rotation integration and simulator UI acceptance remain pending.

## Incoming combat primitives

EnemyFireSchedule and IncomingDamageState form the static incoming-combat
foundation. DummyWeaponSimulator uses them only through the opt-in IncludeIncoming
option described in the next section; with it off, existing outgoing contracts and
results are unchanged. There is still no UI claim.

EnemyFireSchedule reads character armor, estimated bonus armor, character health,
enemy hit damage, incoming DPS and damage-taken factor from Core. Hit probability
comes from the resolved enemy_hit_rate input. Attempt cadence is incoming DPS /
raw hit damage / hit probability: Core's DPS already incorporates hit rate,
Enemy Firing Time and attacker count. The pack has no separate effective hit
chance, so a Core modifier on incoming DPS cannot be split between cadence and
hit chance; it is refused (`incoming-dps-modifier-unsupported`) rather than read
as a slower cadence. Mitigation multiplies raw hit damage once, independently of
cadence. Cadence comparisons for driven states allow a relative difference of
1e-9, since rescaling hit damage rescales DPS and can move the derived interval
by an ulp; the run keeps its setup cadence. Invalid, missing or inconsistent readings fail with a
simulation code, including nonpositive player health and unrepresentable cadence.

Attempts begin one interval after zero, use ordinal times (n * interval), and
occupy the half-open fight horizon. Duration is finite, positive and at most 600
seconds. Enumeration checks cancellation and caps work at one million attempts.
It consumes no RNG and applies no damage: a future scheduler must sample the
stored hit probability. Zero incoming DPS, including zero hit probability or zero
firing share, produces no scheduled attempts; attempted misses at zero hit rate
are not reported. Zero damage-taken factor preserves cadence with zero-damage hits.

IncomingDamageState applies already mitigated hits through bonus armor, armor,
then health. It reports actual capped damage per pool, a transition when positive
armor reaches zero, and a transition when positive health reaches zero. Excess
overkill is discarded. Starting without armor produces no armor-break event;
hits after downing produce no repeat transitions. First break/down times are
recorded exactly. Initial pools and hits are finite and validated, with monotonic
event time. Invalid hits leave both pools and clock unchanged. No combined pool
sum is needed, so large finite pools do not overflow simply by being added.

Tests compose Core-backed cadence with deterministic landed hits to verify
discrete break/down times, rather than equating them to continuous DPS previews.
Committed-pack tests cover the default 50% Enemy Firing Time and doubling cadence
at 100%. Enemy hit sampling, outgoing event merging, trigger re-resolution,
shields, shock blocking, repairs and incoming result aggregation remain pending.

## Incoming fire merged into target fights

IncludeIncoming opts into `d2p-incoming-random-v1/d2p-splitmix64-v1`. It requires
random, single-weapon target fights without skills or rotation. Existing modes
and contracts stay unchanged when disabled. Enemy cadence and hit probability
come from EnemyFireSchedule; every attempt takes one uniform draw from a separate
SplitMix64 stream with fight id `(1UL << 63) | (uint)fightIndex`. This is disjoint
from outgoing stream ids and cannot perturb weapon hit/crit/headshot rolls.

Enemy attempts run during shot intervals, reloads and retarget waits, first after
one enemy interval and strictly before the horizon. Enemy events take precedence
on ties with weapon/reload events. A landing hit uses the Core-backed mitigation
present immediately before it; a damage-triggered defense change affects later
hits. Positive actual damage emits damaged, armor depletion emits armor_break,
and downing stops the fight before a same-time weapon shot. Zero-damage landed
hits count as hits but do not emit damaged. Retarget pool readings are restored
before an enemy event when arrival occurred during a weapon wait.

With driven triggers enabled, incoming events use the same status and buff state
as weapon events, then Core re-resolves the changed state. Only incoming hit
amount may change dynamically: enemy interval/hit probability and initial player
pool capacities must remain fixed. Cadence/capacity changes are explicitly
refused. Packs declare no recovery capability, so repairs fail closed: any nonzero
resolved stat whose id names recovery (repair, regen, healing, on_kill, restore,
recover) is refused, apart from a short documented list that is not in-fight player
recovery (skill repair scaling, healing given to others, durations). A recovery stat
added by a later pack is therefore refused, not ignored. This prevents an
incomplete Unbreakable or healing loop from silently running. Static assumptions remain fixed when
DriveSimpleBuffs is off. Shields, shock blocking and skill integration are excluded.

Incoming mode defers reload start/completion until their actual event times,
avoiding future trigger-clock updates before intervening enemy hits. Reload
duration is sampled before reload-reset events, as before. A refill earned before
reload starts cancels the empty reload; one earned during reload neither cancels
its remaining duration nor fills the magazine early, so phase readings see an
empty magazine until completion. While the next target has not arrived, target
readings report a full target rather than the dead one. The deferred reload
start and completion steps spend no work budget, and the enemy attempt times are
computed once at setup and shared by every fight. Completions and scheduled target-arrival traces after
downing are omitted. The initial weapon and all player/trigger state restart
independently for each fight.

Incoming results report mean attempts, hits, actual capped damage and armor-break
counts, plus downed fraction. Break/down time distributions include only fights
where that event occurred and are null with no observations; they are not zero
samples or estimates for censored fights. Outgoing DPS still divides by the full
requested duration, including time after downing, and its timeline contains only
outgoing damage. Incoming events share the bounded first-fight trace and overall
work/cancellation checks; per-fight enemy attempts retain their own bound.

Tests pin ties, mid-reload downing, future trace removal, armor-break buffs,
Core-driven mitigation, refill cancellation, immunity, half-open horizons, RNG
isolation/replay, retarget readings and explicit unsupported-mode/repair checks.
Committed Vigilance now loses its weapon bonus after actual enemy hits. Repairs,
shields, shock blocking, skill/rotation integration and UI acceptance remain open.

## Shock blocks an attacker's share of incoming fire

Incoming mode advances to `d2p-incoming-random-v2/d2p-splitmix64-v1`. Live shocked
status now suppresses one target's share of the aggregate incoming schedule.
EnemyFireSchedule retains the resolved positive whole enemy_attackers count,
defaulting to one when that input is absent in a minimal pack. Dynamic attacker
count changes are refused with other cadence changes. Core still supplies the
aggregate cadence, including attacker count; it is not multiplied a second time.

Each fight owns a new ShockFireState. Every scheduled attempt while the current
target is shocked adds one unit of credit. At enemy_attackers units, the attempt
is blocked and credit restarts at zero. Non-shocked attempts neither add nor
discard credit; fractional credit persists across status windows and retargets
within that fight. Thus one attacker is fully blocked while shocked, two lose
half their shocked attempts, and so on. This is a deterministic share model of
uniform aggregate fire, not a simulation of individual attacker identities.

The gate uses actual status deadlines, before the enemy hit roll. Exact shock
expiry restores firing, and clearing a dead target removes its shock immediately.
A blocked attempt is still counted in Attempts and emits EnemyShotBlocked, but
does not draw an incoming hit roll, deal damage, count a hit or emit damaged or
armor_break. It uses the same bounded work accounting and advances the existing
schedule. Outgoing RNG is unchanged; the versioned incoming sequence skips rolls
for blocked attempts. Incoming.BlockedShots reports the mean blocked count.

Tests cover quota fractions, retained credit, invalid attacker readings, one/two
attackers, exact expiry, unarmed status, death/retarget clearing, fresh fight state,
incoming draw order and preserved outgoing rolls. Shock is driven only by actual
applied status in this mode; manual status assumptions do not invent control
events. Repairs, shields, other control effects, skill/rotation integration and
UI acceptance remain pending.

## Immediate kill and armor-depletion recovery

Incoming mode advances to `d2p-incoming-random-v3/d2p-splitmix64-v1`.
IncomingDamageState retains original armor/health capacities and supports capped
immediate repairs. Missing armor/health are restored independently, bonus armor is
not granted, and downed players cannot revive. Invalid amounts or times leave
pools and clock unchanged. Initial capacities remain immutable and dynamic Core
capacity changes are still refused.

Core supplies three recovery amounts: estimated_armor_on_kill plus
estimated_armor_repair_per_kill, estimated_health_on_kill, and
estimated_armor_repair_on_depletion. The first two armor outputs have distinct
raw sources and are summed. Kill recovery uses the profile before that shot's
kill events, after damage and target death but before kill-triggered buffs, so
the activating kill cannot retroactively earn a newly triggered repair amount.
Each event repairs only missing pools; reported totals are actual recovery.

On an enemy hit that depletes positive armor, depletion repair is sampled from
the pre-hit Core profile and capped to armor capacity. The same hit's overflow
still reaches health rather than being absorbed by newly restored armor. Armor
break is recorded, then its actual repair, before the armor_break trigger starts
Unbreakable's cooldown. A subsequent depletion while cooling down does not repair;
after readiness returns, another eligible depletion may repair again. First break
time remains the first event even when later breaks occur. A fatal overflow can
still record the depletion repair preceding death but cannot revive the player.

Core-driven recovery profiles are cached with other resolved state. The fail-closed
recovery guard now has an explicit allowlist for these immediate outputs and their
known sources/summary. Direct modifiers on repair-per-second summaries are refused
because no discrete recovery event can be inferred from them. Other recovery-named
stats remain refused, including regeneration, critical-hit repairs and timed queues.
The existing non-player-recovery metadata exceptions remain unchanged.
An active raw source also requires its resolved amount output; missing outputs
fail explicitly rather than silently turning a declared recovery effect into zero.

ArmorRepaired/HealthRepaired traces carry actual amounts, and Incoming reports
mean actual recovery in separate armor/health fields. Tests cover caps, invalid
updates, same-hit overflow, no revival, kill timing, cooldown sampling/recovery,
fresh fights and actual totals. Committed Unbreakable now repairs armor during an
incoming fight. Regeneration, crit repairs, Preservation/Makeshift queues, shields,
skills/rotation integration and simulator UI acceptance remain pending.


### Static armor regeneration (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v4/d2p-splitmix64-v1 integrates Core's
estimated_armor_regeneration rate with fixed assumptions. Simulation does not
recompute the flat/percentage formula. Active raw armor_regeneration or
armor_regeneration_percent requires the resolved output, and the rate must be
finite and nonnegative. Unknown recovery mechanics still fail closed.

IncomingDamageState.Regenerate integrates from the previous pool update to the
requested time, caps to missing armor before multiplying, grants no bonus armor
or health and cannot revive. Invalid inputs do not mutate pools or clock.
Regeneration advances before incoming attempts and weapon/reload bookkeeping,
and through the remaining interval to the duration when alive. This covers
reloads, retarget gaps, missed/blocked attacks and the final partial interval.
Same-time events earn no extra regeneration. Immediate kill/depletion repairs
retain their ordering and caps.

Actual regeneration contributes to Incoming.ArmorRepaired and ArmorRepaired
traces. Continuous recovery uses interval-end timestamps; a final recovery at
exactly Duration is included in the trace and total. Shots and other discrete
combat events retain the half-open horizon. No recovery occurs after downing.

This slice deliberately requires DriveSimpleBuffs=false for any nonzero rate,
even when that rate appears constant: driven fights need integration across all
expiry/phase boundaries before regeneration can be trusted. Nonzero regeneration
at setup or activation in a driven fight raises incoming-regen-unsupported.
Critical repairs, timed repair queues, shields and driven regeneration remain
pending. Tests cover pool bounds, invalid updates, overflow-safe capping, Core
flat/percentage outputs, reload/retarget waits, death, kill-heal interaction,
end-of-fight recovery and the committed Memento with fixed trophy assumptions.


### Driven armor regeneration (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v5/d2p-splitmix64-v1 removes the fixed-
assumptions restriction on regeneration. The simulator integrates each interval
using the previous Core rate, then refreshes the resolved profile at its endpoint.
Intervals split at effective buff/ready/lockout transitions, stack expiry/decay,
status expiry, cycle phases and target arrival. Timed boundaries are processed
even when no weapon or enemy event occurs there, including during reload and
retarget waits and the final partial interval. They spend the bounded work budget
and check cancellation; they do not draw random numbers or fire combat triggers.

PhaseTriggerState.NextTransition returns the next strictly future cycle boundary.
Reads and scheduling share absolute cycle boundaries instead of modulo, including
decimal wraps whose division rounds down. Invalid or unrepresentable advancement
fails explicitly rather than looping. Magazine-driven rates refresh immediately
after spending/refilling rounds and at reload completion. Buffs activated by a hit
or shot apply only to subsequent time; no elapsed interval uses a future rate.

The horizon closes the last continuous interval but does not resolve a newly
starting phase or other discrete state at Duration. ArmorRepaired traces still
include actual recovery through that endpoint. Capping, no revival, kill/depletion
ordering, independent RNG streams and the broad unknown-recovery guard remain.
Dynamic cadence/capacity, crit repairs, timed repair queues and shields are still
unsupported. Committed Emperor's Guard regeneration can now coexist with driven
Vigilance; synthetic cases pin timed activation/expiry, cooldowns, stack decay,
status gates, cycle wraps, magazine changes and horizon behavior.


### Critical-damage-share armor repair (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v6/d2p-splitmix64-v1 supports Core's
weapon_crit_repair_share, including the committed Caduceus talent. For each shot,
the simulator sums actual target-pool damage from landed critical pellets only.
Resolved armor/health factors apply before attribution; misses, noncritical
pellets, overkill and hits on an already dead target grant no critical repair.
The pre-shot share multiplies this damage. Repair is capped to missing player
armor, grants no bonus armor or health and cannot revive. An overflowing product
saturates to the largest finite value before the finite pool cap.

The Shot trace precedes actual ArmorRepaired, then Kill and its own capped
recovery. Shot/hit/crit/kill triggers run afterward, so a newly activated share
cannot heal its activating shot. Existing regeneration integration, independent
random streams and downing-before-shot ordering remain intact. ArmorRepaired
traces and Incoming.ArmorRepaired include actual recovery; no extra RNG is drawn.

estimated_crit_repair_per_second remains a preview, not a second source of
healing. Direct modifiers on that summary are refused; a nonzero summary without
a raw share, or a negative/nonfinite share/summary, fails explicitly. Unknown
recovery names remain refused. The previous unsupported-crit test now checks
Clutch's missing-armor fraction instead. Clutch needs separate threshold/window
logic; ally repairs, queues, shields and skills/rotation integration remain open.

Tests cover landed body/head critical channels, mixed critical pellets, target
factors/immunity/overkill, caps including huge products, pre-kill recovery order,
no revival, activation timing, invalid summaries, outgoing RNG isolation, replay
and committed Caduceus in a driven incoming fight. Pack data and baseline captures
are unchanged.

### Shock and recovery review corrections (C9, 2026-10-28)

The PR 13 review led to these corrections. Incoming fights advance to
`d2p-incoming-random-v7/d2p-splitmix64-v1`.

- Armor-depletion repair (Unbreakable) needs DriveSimpleBuffs. Without it, the
  readiness assumption is fixed and has no cooldown, so every break would
  restore armor. That case is refused with `incoming-repair-unsupported`. With
  DriveSimpleBuffs, the pack's `ready` trigger supplies the cooldown.
- A hit that both breaks armor and downs the player applies and reports no
  depletion repair. This matches Repair(): a downed player receives nothing.
- Shock credit belongs to the shocked target. It resets when a target dies, with
  its statuses, so a dead target's partial share never blocks an attempt on the
  next target.
- Every enemy attempt draws its hit roll, blocked or not. Blocking one attempt
  therefore never changes whether a later attempt lands.
- Regeneration walks buff, status, cycle and retarget boundaries only while armor
  is missing. With full armor there is nothing to integrate, so the walk is
  skipped. A fight whose armor stays full does no extra boundary work.
- An enemy attempt reuses the state that the regeneration walk refreshed at the
  same time, instead of refreshing again.
- Regeneration is traced as one ArmorRepaired entry per run at one rate. A run
  ends when the rate changes, armor fills, regeneration stops, a discrete repair
  or enemy attempt follows, or the fight ends. The entry is timed at the run's
  last accrual. Totals are unchanged; only the trace is coalesced.

Absolute cycle boundaries also change outgoing phase readings in driven fights
without incoming fire, at exact decimal wraps where modulo rounded into the
previous cycle. The opt-in contract therefore advances to
`d2p-simple-buffs-v13/d2p-splitmix64-v1`.


### Clutch eligibility and post-kill recovery (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v8/d2p-splitmix64-v1 adds the known
gear_talent_clutch and gear_talent_perfect_clutch mechanics to driven, random,
single-weapon incoming fights. The simulator adapter owns their strict below-15%
and below-20% normal-armor thresholds. Bonus armor is excluded. Zero maximum
armor cannot earn repair. The selected talent must be directly equipped and
legal; copied talent ownership remains unsupported.

Core supplies both eligible missing-armor repair fractions and the post-kill
window duration, including configured Weapon-core scaling. Simulation does not
hardcode the repair fraction or window formula. Each cached driven state resolves
low_armor and post_kill probes with zero assumed missing armor. The main profile
clears clutch_repair_state and pins clutch_missing_armor_percent to zero; supplied
manual assumptions cannot grant eligibility or fabricate missing armor. The
estimated_armor_repair_on_crit preview is not credited as a heal. Clutch input
ownership conflicts, fractions outside 0-1, missing/invalid outputs and profiles
where these inputs change other combat stats are refused explicitly. Those side
effects would require continuous threshold scheduling and are not inferred.

Each landed critical pellet against a live target rechecks actual armor and
repairs the selected Core fraction of currently missing armor. A low-armor repair
can cross the threshold and make the next pellet ineligible. During an active
kill window, repeated pellets instead compound against the remaining missing
armor. Clutch is hit-based: a landed critical on a live immune target can heal;
Caduceus still requires actual dealt damage. Pellets after target death cannot
heal. Clutch repairs no health or bonus armor and cannot revive.

A kill opens or refreshes a half-open window using the pre-trigger Core duration,
after all pellets of the killing shot. Its own new window cannot heal that shot.
The window remains active across reload/retarget waits and expires exactly at its
deadline; kills never shorten an already later deadline. State is fresh per fight.
Per-pellet Clutch recovery is summed into an actual ArmorRepaired trace after
Shot, before Caduceus's shot-total repair and then Kill/immediate kill recovery.
The existing aggregate recovery statistics include those actual amounts.

Tests cover exact normal/perfect thresholds, input overrides, Core fractions and
window durations, expiry/refresh, per-pellet threshold crossings/compounding,
dead targets, immunity, misses/noncritical shots, no revival, invalid atomic
updates, Caduceus ordering and the committed normal/Perfect Clutch definitions
with controlled in-memory encounter/critical stats. Frozen fixtures and delivered
pack files are unchanged. The pack's pending in-game verification is not replaced
by simulator regression coverage. Timed repair queues, shields, ally repairs,
skills/rotation integration and UI acceptance remain pending.


### Makeshift Repairs queue (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v9/d2p-splitmix64-v1 delivers the player
portion of Makeshift Repairs. Actual nonfatal mitigated damage to player pools
(including bonus armor) snapshots Core's makeshift_repairs_share and
makeshift_repairs_duration before damaged/armor_break triggers. Each hit creates
an independent constant-rate budget over that duration. Later share/duration
changes affect new hits only. Fatal hits, misses, blocked shots and zero-damage
hits add no deliverable budget. Shield recipients remain excluded until shield
simulation is implemented.

TimedArmorRepairQueue stores independent expiry/rate entries in a priority queue.
The event walker integrates the old combined regeneration/queue rate before
expiring completed budgets. Overlaps add; expiries split reload/retarget/final
intervals exactly. Full-armor time discards delivery while advancing expiries, so
unused repair is never banked. Actual recovery is capped to missing normal armor,
grants no health or bonus armor and stops at downing. Existing coalesced continuous
ArmorRepaired traces and aggregate actual armor-repair statistics include queues.

The queue has a one-million-entry bound, compensated rate addition/removal and
explicit failures for invalid/unrepresentable rates, expiry or aggregate overflow.
A small surviving rate is retained when a much larger one expires. Integration
continues to use the cancellation checks and bounded work budget. No random draws
are added.

makeshift_qualifying_damage is owned as a preview-only input and forced to zero
in incoming fights. Another trigger cannot own it. Nonzero makeshift_repair_budget
or makeshift_repair_average_rate is refused instead of generating hypothetical
heals. Active shares must be in 0-1 with positive finite durations up to 600 s;
unknown recovery remains refused. Core supplies the four-piece threshold, share,
duration and Process Refinery/Improved Materials upgrades. No set coefficients
are duplicated in Simulation.

Tests cover overlaps, exact expiry, combined regeneration, rate/duration snapshots,
full-armor loss, caps, no revival, reload/retarget waits, final partial delivery,
preview isolation, deterministic outgoing rolls, numerical guards and committed
Foundry three/four-piece plus both upgrades under controlled encounter formulas.
No delivered pack or frozen fixture files are changed. Legacy source was read as
reference for independent Makeshift budgets; no Go toolchain was invoked.
Preservation's pack defines a five-second kill budget but leaves overlapping kills
unspecified, so active Preservation budgets remain refused pending an explicit overlap contract. Shield
repair, other queues, skills/rotation integration and UI acceptance remain open.


### Opt-in frontal shield encounters (C9, 2026-10-28)

IncludeShield requires incoming random single-weapon targets and exactly one
accepted shield with an explicit pack simulation rule. ShieldProfile reads Core
preview health, active regeneration and break cooldown; simulation does not
recompute skill/tier formulas or use hypothetical Overcharge outputs. The pack's
allowed weapon slots and excluded tags are enforced on the current active weapon.
Missing, duplicate or invalid shield profiles fail explicitly. Automatic swaps,
manual lowering/holstering, flanking and dynamic shield profiles remain excluded.
The incoming contract is d2p-incoming-random-v10/d2p-splitmix64-v1.

A shield starts raised at full capacity. Landed attempts while it is up consume
raw enemy damage, independent of the player's mitigation. A breaking attack loses
its excess rather than overflowing into player pools, following the frozen
reference encounter model. While broken, later attempts damage the player using
Core mitigation. The shield automatically raises full at its exact cooldown
endpoint before a same-time attack. No deployment is counted at the half-open
fight horizon or after player downing. Deployments include the initial raise.

Active regeneration and queued Makeshift budgets integrate through weapon waits,
reloads, retarget gaps and the final partial interval. Health caps before overflow;
full/broken shield delivery is discarded, never banked. Damage to either player
or shield creates one pre-trigger Makeshift budget using actual damage received;
its rate delivers independently to both recipients while repairable. A broken
shield cannot be repaired back to life; cooldown deployment supplies full health.
Cooldown restoration is counted as deployment, not continuous repair.

ShieldHit/ShieldBreak/ShieldRaised/ShieldRepaired traces identify the skill slot.
Incoming adds mean shield absorption, breaks, deployments and actual continuous
repair. Repairs coalesce by rate and flush before shield damage. Shield hits do
not fire player damaged/armor_break; breaks fire shield_break and can drive buffs
or refills. Every attempt still uses its original enemy RNG roll, and shields draw
no new randomness. Outgoing RNG is preserved unless shield-break buffs change it
through Core or player downing changes the duration of firing.

Driven shield runs visit timed boundaries even with full pools, refusing transient
health/regeneration/cooldown changes instead of missing them between shots. The
existing full-armor shortcut remains for fights without driven shields. Tests pin
raw absorption, break overflow, exact cooldown/horizon timing, capped regeneration,
shared Makeshift budgets, downing, shield-break triggers, allowed slots/tags,
invalid/transient profiles, stream replay and all four committed shield profiles.
Reference coefficients remain subject to the pack's live-game verification notes.
No delivered pack files or frozen fixtures changed. Shield-aware rotations,
holstered regeneration, flank/partial-cover modeling, other repair effects and
UI acceptance remain pending.


### Static skill damage in incoming encounters (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v11/d2p-splitmix64-v1 permits IncludeSkills
alongside incoming damage and an optional frontal shield. Skill previews remain
static: DriveSimpleBuffs and weapon rotation cannot be combined with this mode.
SkillDamageSchedule skips shield rules only when IncludeShield explicitly handles
them; otherwise selecting a shield for damaging skills still fails explicitly.

The shared timeline processes the earliest skill, enemy or weapon event. Enemy
attempts cannot jump over skill impacts during a long reload or retarget wait.
Continuous skill batches are split at enemy attempts before the run, with bounded
output and cancellation. Each split preserves its original budget and interval,
with the last piece taking the remainder to avoid losing damage to repeated
rounding. Elapsed continuous damage is credited before an attack at the endpoint.
Instantaneous ties resolve enemy first, then skill impact, then weapon shot.
Ticks precede same-time impacts. This accounts for a partial batch before a lethal
attack without damage or skill impacts after player downing.

Player/shield regeneration and Makeshift queues advance to each skill event.
Generic Core armor/health-on-kill recovery applies to actual skill kills before
the next enemy attempt. Kill/target traces retain the skill slot, and retarget
arrivals clip continuous damage to the live part of each interval. A continuous
endpoint at Duration still credits its damage and possible kill, but grants no
instant kill repair outside the half-open fight. Critical-damage-share repair
remains weapon-only. Active estimated_armor_repair_per_kill with incoming skills
is refused as skill-kill-repair-unsupported: the committed Doctor Home source
requires a weapon-applied mark, and mark/kill-source eligibility is not yet modeled.
Generic armor_on_kill sources remain supported.

No new random draws are added. The skill splitter validates boundaries and damage,
preserves cancellation, and refuses over one million resulting events. Existing
average/static skill contracts are unchanged. Tests cover reload waits, instant
ties, continuous clipping at downing, kill recovery, endpoint kills, shield/skill
composition, invalid inputs, cancellation, replay and committed Assault Turret
with and without Crusader Shield. Delivered packs and frozen fixtures are unchanged.
Dynamic skill state, skill-trigger events, marked-target repair eligibility,
individual shells, incoming rotations and UI acceptance remain pending.


### Doctor Home target marks (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v12/d2p-splitmix64-v1 supports Doctor Home
with static skill kills. A directly selected legal weapon_talent_doctor_home on
the active weapon owns a per-target five-second mark, matching the committed
talent description. The expiry is a Simulation event mechanic; Core still
supplies the estimated_armor_repair_per_kill budget without duplicating its
repair fraction. Other active repair-per-kill sources with skills remain refused
until their eligibility is modeled.

A landed pellet on a live target applies or refreshes the mark before damage,
including a landed zero-damage/immune hit. Misses and pellets after target death
do not mark. The killing weapon pellet can mark its own target before its repair.
TargetMarked traces record one application/refresh per shot with the weapon slot.
Marks are half-open, expire exactly at five seconds, survive reload waits only
until expiry, reset at target death and start fresh each fight. Reads do not
advance the mark event clock; invalid updates are atomic.

Both weapon and skill kills gate Doctor Home's budget by the actual mark at the
kill time. Generic armor_on_kill and health-on-kill remain independent. Core's
Doctor Home budget is separated from generic armor-on-kill before applying caps,
so an unmarked skill kill cannot receive it. Same-time skill impacts still run
before the weapon shot; a later shot cannot retroactively mark an already killed
target. Existing continuous endpoint/horizon and no-revival rules remain in force.
Repair delivery retains the existing immediate player-portion model; ally kits,
pickup/travel delays and weapon rotations remain excluded.

Tests cover marked skill kills, unmarked kills with independent generic repair,
exact expiry, refresh, long reloads, target/fight isolation, killing hits, immune
hits, invalid state changes and random-stream preservation/replay. Controlled
encounters retain the committed Doctor Home talent and repair formulas and test
active ownership, missed hits and an inactive secondary owner. Delivered pack
files and frozen fixtures are unchanged. Dynamic skill triggers, other mark
mechanics, rotations, timed Preservation and UI acceptance remain pending.


### Skill-hit and scoped skill-kill triggers (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v13/d2p-splitmix64-v1 permits
DriveSimpleBuffs with IncludeSkills in incoming random single-weapon fights.
Skill damage and deployment timing were static here, with skill-state-unsupported
for any state with another plan. ADR 0012 replaced that: each use now reads the
state as it deploys. Non-incoming driven skills remain excluded.

Positive actual skill damage emits skill_hit after damage/recovery, allowing
committed Spark and Perfect Spark to change weapon damage without changing the
skill plan. SkillDamageEvent carries a stable HitGroup for each original impact
or continuous batch. Split fragments retain it, and only the first positive
fragment emits skill_hit. Enemy cadence therefore cannot multiply counted hits.
This is a batch-level hit contract, not individual turret bullets or shells;
physical activation may be observed at the first positive partial endpoint.
Zero damage and final continuous damage at Duration do not fire triggers.

Actual skill kills emit kill only to directly equipped non-weapon talent owners.
Weapon-owned kill counters, resets, consumption and status arming require weapon
kills. TimedBuffState and StatusTriggerState accept an eligible-talent scope
without moving excluded counters or side effects; their common event clock still
advances normally. Gear-owned kill rules retain their timing and caps. Clutch's
explicit post-kill window also opens on a skill kill before later critical hits.
Skill kills do not invent weapon hit, crit, headshot or their kill variants.
Skill-native status application remains excluded.

Skill-hit/kill refills obey the existing reload rules: a pending reload can be
canceled, but rounds cannot become usable during an ongoing reload. Generic kill
repairs and Doctor Home mark eligibility use the pre-event profile, preventing
retroactive heals from newly activated buffs. Unmodeled repair-per-kill sources
with skills are refused both at setup and on activation. Target status cleanup
occurs after its kill events, matching weapon-kill ordering.

Tests cover hit activation/expiry, split-batch counter invariance, gear/weapon
kill scoping, excluded status arming and consumption, Clutch skill-kill windows,
reload refills, transient skill-plan changes, zero/final damage, unsupported repair
activation, RNG/replay and committed Spark/Perfect Spark with Assault Turret.
No delivered pack or frozen fixture files changed. Fully dynamic skill scheduling,
individual shell timing, skill statuses, rotations, Preservation and UI acceptance
remain pending.


### Bounded Preservation delivery (C9, 2026-10-28)

Incoming contract d2p-incoming-random-v14/d2p-splitmix64-v1 supports one active
Preservation repair budget from a weapon kill. Directly equipped normal/Perfect
Preservation on the active weapon owns preservation_kill_type; manual assertions
are cleared. Core resolves body/headshot budgets for each cached driven state.
Simulation uses the actual killing pellet's headshot result, not any headshot in
the shot, then queues the pre-kill-trigger amount over five seconds. The repair
fraction is not duplicated in Simulation. Missing/invalid budgets, ungated outputs,
multiple owners and kill-type inputs changing other combat stats are refused.

Preservation's queue is player-only and separate from Makeshift's shared queue.
Rates integrate together with armor regeneration through reloads, retarget waits,
expiries and final partial intervals, while Shield receives no Preservation rate.
Full-armor delivery is discarded, never banked. Actual armor recovery is capped,
cannot revive and stops at downing. PreservationStarted traces record the queued
budget and weapon slot; ArmorRepaired reports actual delivered recovery. Budgets
survive target death and later profile changes until their own deadline.

The producer's PRESERVATION_SOURCE_REVIEW.md (2026-10-16) supports the five-second
total-budget interpretation and existing Core body/headshot hypotheses, but does
not establish overlap, runtime weapon gating or exact tick endpoints. This slice
uses continuous delivery as an explicit approximation; it does not claim the
source's 0.2-second action frequency is verified runtime scheduling. A second
positive budget before expiry raises preservation-overlap-unsupported, including
while armor is full. A kill exactly at expiry may start a new budget. Skill kills
with active Preservation raise preservation-kill-source-unsupported until their
eligibility is verified. No stacking/refresh policy is silently chosen.

Tests cover normal/perfect body/headshot budgets, killing-pellet attribution,
manual assumption isolation, pre-trigger snapshots, caps/full-armor loss,
partial/end delivery, exact expiry, overlap refusal, no revival, skill-source
refusal, Shield isolation, Makeshift composition, invalid profiles, ownership,
RNG/replay and both committed talent variants in controlled encounters. Pack files
and frozen fixtures are unchanged. Overlap, skill-kill eligibility, exact in-game
ticks, other repair mechanics, rotations and UI acceptance remain pending.


### Target-status uptime (2026-10-29)

Driven fights expose TargetStatusUptime: mean active seconds per fight and the
fraction of the full requested duration, for burning, bleeding, shocked, ensnared,
blinded, disoriented and their union (any). Overlapping statuses contribute once
to any; repeated applications extend their existing interval rather than counting
overlap twice. Only statuses actually applied by the simulator contribute. Fixed
assumptions are not measured; fights without driven status state return an empty
map. Driven fights return zero for statuses that never apply.

Intervals close at expiry, target death, the fight horizon or player downing.
Retarget gaps and the time after downing contribute no active seconds, while the
fraction's denominator remains the requested duration. Measurement is independent
of trace capture, and state reads never commit future measurement or advance the
event clock. Per-event accounting is bounded by the six supported statuses and
does not retain interval history. This is an additive report; damage, event order,
RNG draws and existing simulation contract identifiers are unchanged. Talent
uptime, native skill statuses, damage-over-time ticks and UI results remain open.


### Timed trigger measurements (2026-10-29)

Driven results now expose TimedTriggerUptime by timed boolean/stack input key.
Each entry contains mean true/nonzero seconds per fight and its fraction of the
full requested duration. Stack keys also report the mean live stack count over
that duration, including zero-stack intervals. Boolean keys report null for mean
stacks. Shared boolean owners follow the effective Context value, so shared buffs
form a union and ready/lockout keys are measured while their input is true (when
ready), not while their cooldown is running. These are input measurements, not a
claim that every modifier belonging to a talent is effective during that time.

Integration uses event-time state and exact deadlines, including independent
stack expiry, refreshed expiry, delayed decay, consumption, counted resets,
purge and shared-pool clears. Reads do not advance the event clock or commit a
future measurement. A failed event cannot change totals. Measurement requires no
trace history and stops at player downing or the horizon. Retarget gaps continue
to count player buff state; target status time remains a separate report. Means
use all iterations and the full requested duration as their denominator.

Only TimedBuffState keys are included: cycle/magazine phases, target readings,
status observers and dedicated repair inputs are excluded. Non-driven fights
return an empty map. Per-key coverage of those other state classes and a complete
per-talent effectiveness report remain open. Damage, RNG draws, event ordering
and existing simulation contract identifiers are unchanged by this additive report.


### Cycle-phase occupancy (2026-10-29)

Driven results now expose CyclePhaseUptime, keyed by the cycle input and then its
phase value. Entries contain mean seconds per fight and a fraction of the full
requested fight duration. Repeated values within a cycle combine into one entry;
values not reached before the horizon/downing remain present with zero seconds.
Cycle time starts at fight time zero, continues through reloads and retarget gaps,
and stops at player downing. Non-driven fights and runs without cycle rules
return an empty map. Magazine phases are outside this report.

Measurement integrates full periods arithmetically and the final partial period
using the existing absolute phase boundaries. It does not enumerate cycles or
change combat scheduling. Work is proportional to the declared phases, with
cancellation checks, including for very short cycles. Reads are pure; invalid
times and unrepresentable cycle advancement are refused consistently with the
existing phase reader. Floating-point durations use the usual numeric tolerance.
Damage, event ordering, RNG draws and simulation contract identifiers are
unchanged. Magazine/readings occupancy, complete talent effectiveness and UI
results remain open.


### Magazine-phase occupancy (2026-10-29)

Driven results now expose MagazinePhaseUptime by input key and value, using the
same seconds/fraction shape as cycle occupancy. Categorical phase values combine
repeated labels and retain zero for unvisited labels. Percent readings use a
bounded histogram of integer missing-round percentages 0 through 100, following
the existing floored magazine reader. Fractions use the full requested duration.
This measures the actual ammo state over time, rather than the frequency of
pre-shot readings: a post-shot phase starts at that shot's timestamp. Initial or
reloaded full magazines may have zero occupancy if immediately fired.

Observations occur at ammo mutations, including weapon shots, eligible incoming,
shield/skill refills and reload completion. A reload keeps its empty reading
until completion; refills that the combat loop defers during reload do not create
usable rounds or a full-phase interval. Same-time changes contribute no extra
time. No-reload weapons retain their full magazine reading. Measurement continues
through retarget gaps and reloads, ending at downing or the horizon. Non-incoming
reloads scheduled past the horizon close measurement at the horizon first.

Reads do not move the observation clock; invalid observations are atomic. The
measurement reuses PhaseTriggerState's magazine reader without changing Core,
combat scheduling, RNG draws or simulation contract identifiers. No trace history
is retained. Non-driven fights or fights without magazine rules return an empty
map. Other combat readings, full talent effectiveness and the results UI remain
open.


### Session simulation execution (2026-10-29)

Application now provides SessionSimulation as the background execution boundary
for desktop/CLI session clients. A SimulationRequest captures immutable options,
target/dummy routing and retarget delay (default one second, matching the engine).
Start captures the current session snapshot and runs that exact build/assumptions
through Simulation. Results retain the request and session revision. Engine
validation errors preserve their original exception/code; the service does not
rewrite unsupported options or mutate the session.

Each Start supersedes the previous request, including different options at the
same revision. Accepted session edits cancel/invalidate the old revision; Cancel
and Dispose invalidate work and queued notifications. Rejected edits do not
invalidate a run. If a new request captures an already-committed revision before
its Changed notification arrives, that notification must not cancel it.

Progress is reported with revision/request identity, and IsCurrent checks both
progress and results against the latest request and current session snapshot.
Callbacks run on worker threads outside service/job locks. Consumers posting to
a UI dispatcher must call IsCurrent again when applying the queued notification,
as a new request/edit may overtake publication. Disposal unsubscribes the session
handler; later starts do no work. Existing LatestRevisionJob owns worker execution
and cancellation. The desktop form remains disabled until its binding/results
increment; this service adds no view or transport dependencies to Application.


### PR 14 review corrections (2026-10-29)

The finding-by-finding assessment is an internal review record. Shield regeneration
is optional (zero when omitted); an empty weapon list is unrestricted, subject to
excluded tags. Completed shield recovery assigns exact capacity. Doctor Home's
mark gates only the repair delta attributed by resolving without its active
selection; other unmodeled repair-per-kill sources still refuse skill fights.
Timed kill resets/consumption apply independently of gain ownership. These
corrections advance incoming to v15 and simple-buffs to v14.

The owner selected strict Preservation overlap refusal by default plus an explicit
ApproximatePreservationOverlap opt-in. That approximation adds independent
five-second player budgets and is reported in result Limitations. It is not a
verified game rule; skill-kill Preservation remains refused. Uptime accumulates
on state mutations rather than every event; read/event clocks remain separate.
Known talent IDs/thresholds/durations remain a documented producer/schema gap,
not fields currently present in the pack. No pack data or frozen fixtures change.


### Desktop execution and summary results (2026-10-29)

The Simulate tab now composes Application SessionSimulation for the editor's
session. Run captures current options, Cancel invalidates work, and worker
notifications pass through IUiDispatcher with IsCurrent checks at application.
Accepted session changes and form changes clear prior results and invalidate
pending work. Editor disposal unsubscribes the view model and cancels its job.

The form exposes seed, random/average mode, swaps and an explicit alternate
weapon, driven triggers, static damaging skills, incoming fire, shield and the
opt-in Preservation approximation. Unsupported combinations show an explanation
and cannot run; modes are never silently changed. Engine refusals retain their
code and display no partial result. Average mode requires explicit fixed
assumptions; driven skills require incoming mode; swaps require fixed assumptions
without incoming fire. Target/dummy scheduling and formulas remain in Simulation.
Desktop uses selected-target fights and the engine's default one-second retarget
delay. Displayed progress/results belong to the captured request, including its
seed, requested duration and actual iteration count.

Summary results include weapon/skill/total DPS, mean shots/kills, incoming damage,
downing, armor repairs and shield absorption, followed by expandable model limits.
The typed result remains available for the next timeline/state-report increment.
Timeline/chart, distribution and uptime presentation, real-window visual/DPI and
keyboard acceptance remain pending. No pack or frozen fixture changes are made.


### PR 15 lifecycle and presentation corrections (2026-10-29)

The finding assessment is an internal review record. Editor simulation models are
created lazily after successful construction and require a session/dispatcher.
Request revision and weapon metadata refresh are independent; Run validates
against freshly read weapon choices. Null ComboBox selections are ignored.
Engine RunError is distinct from form Problem, allowing unchanged-input retries;
failed runs clear progress. Nullable total DPS is displayed as Unavailable and
progress is finite/clamped even for invalid future progress payloads.

Application EquippedWeaponSlots supplies the shared structural weapon query.
Idle edits with unchanged weapon metadata produce no simulation UI notifications;
queued refreshes coalesce, while stale results/running jobs still invalidate.
These are UI/Application lifecycle changes; engine contracts and formulas remain
unchanged. Timeline/detailed reports and full visual/DPI acceptance remain open.


### Desktop damage timeline (2026-10-29)

The Simulate tab now plots the existing mean total-damage bins as mean DPS per
interval. Each bin's damage is divided by its actual length; a final fractional
bin retains its time-proportional width. Values already averaged by Simulation
are not divided by the iteration count again. Zero-damage intervals, including
the tail after downing, remain present. At most 600 bars are retained, and only
the current accepted result builds presentation state. No event trace is needed.

SimulationTimeline validates duration, bin count and finite nonnegative values.
Missing/invalid data or an unrepresentable displayed rate yields an unavailable
timeline alongside the summary, never a partial/invalid plot. All-zero damage
uses a zero-height chart without division by zero. Bar heights normalize to the
peak interval rate; axis labels and details state the units explicitly.

The chart uses pack theme brushes and supports pointer selection and Left/Right,
Home/End navigation. Stationary pointer notifications preserve keyboard selection.
A native interval picker exposes the same selection and numeric damage/rate
details; record text is the interval label rather than internal field names.
Selection is synchronized in both directions and reset with new/invalidated
results. Build/options/cancel/dispose guards continue to reject stale reports.
The native window was inspected using a temporary ACR build, then restored by
undo without saving it. This is a default-window check, not full keyboard,
assistive-technology or DPI acceptance. Distributions, detailed uptime/state views
and encounter overlays remain pending. No engine formula/contract, pack data or
frozen fixture changes are made.

### PR 17 timeline review corrections (2026-10-29)

The finding assessment is an internal review record. A fractional final sample is
combined with the preceding display interval to avoid a vanishingly short sample
dominating autoscaling. Actual combined duration, damage and proportional width
are retained. Subsecond fights remain one interval. Raw engine bins and summary
calculations are unchanged. The engine, form and chart share one duration limit.

Pointer selection requires click/drag; hover preserves deliberate selections.
Endpoint fractions and populated-control indices are clamped. A native ComboBox
subclass requires selection while populated, retaining the normal ComboBox style;
VM correction notifications and invariant-before-publication keep details aligned.
Result/Timeline setters own dependent unavailable-state notifications. Pens and
formatted labels are cached across selection renders. This supersedes the original
fractional-tail display and hover behavior above; full DPI/accessibility acceptance
and detailed reports remain pending.


### Desktop DPS distributions (2026-10-29)

An expandable read-only report displays the engine's existing weapon, optional
skill and optional total DPS summaries: mean, standard deviation, minimum, 5th
percentile, median, 95th percentile and maximum. Each channel retains its own
summary; an absent total is not reconstructed by adding channel percentiles.
There is no second division by iteration count and no raw-sample histogram claim.

The report explains that random-run percentiles describe simulated fights rather
than confidence intervals. One random fight cannot characterize variation, and
Average mode is one deterministic fight under fixed assumptions. DPS uses the
full requested duration, including time after going down. Reports are created
only for accepted results and cleared through the Result setter on invalidation.
This is desktop presentation only; engine formulas, statistics, seeded results,
pack data and frozen fixtures are unchanged. Conditional incoming-event timings
and detailed uptime/state reports remain separate pending work.


### Desktop incoming and recovery report (2026-10-29)

An expandable report displays the existing incoming-fire means per fight for
enemy attempts, hits, blocked shots, damage taken, armor breaks, shield absorption,
breaks, deployments and repairs, and actual armor/health repairs. The downed
fraction is a percentage of all simulated fights. Shield zeros may also mean no
shield was included; no activity is inferred from those zeros.

Time to first armor break and time to down retain the engine's conditional
summaries in seconds from fight start. Fights without the respective event are
excluded from its timing sample. Missing events are explicitly not observed,
never a fabricated zero-second event. These summaries share a compiled statistics
view with the DPS report, retaining each engine mean, standard deviation, range
and percentile without recomputation. Result assignment owns report publication
and clearing; stale callbacks remain guarded. No engine statistics, formulas,
seed contract, pack data or frozen fixtures change. Detailed uptime/state reports
and visual/DPI/accessibility acceptance remain pending.


### Desktop measured uptime/state report (2026-10-29)

An expandable report displays four existing result channels: living-target status
uptime, timed condition/stack uptime, magazine value occupancy and cycle value
occupancy. Measured zero rows are retained. Any-status time is a union, not a sum
of overlapping statuses. Boolean time follows the named condition's true polarity,
including readiness/lockout; stack time is time above zero. Mean stacks includes
zero-stack time. Seconds are mean seconds per fight; fractions and mean stacks use
the full requested duration, while measurement stops at downing. Time shares may
therefore total less than 100%. No additional normalization is applied.

Names come from the pack's selectors/numeric inputs, with humanized fallback.
Numeric phase values sort numerically and use authored option labels where
available; unknown values remain visible. Sections have stable key ordering.
Each native list has a bounded viewport and virtualized rows for 101-value
magazine histograms. An accepted result without measurements shows an explicit
empty state rather than implying zero uptime; invalidated results hide the report.
Projection occurs only when Result changes, not during pointer movement/render.

No engine events, formulas, seed/statistical contract, pack or frozen fixture
changes. These are measured readings rather than claims of full talent
effectiveness. Encounter overlays, broader mechanic coverage and full
visual/DPI/accessibility acceptance remain pending.


### Desktop first-fight event inspection (2026-10-29)

An off-by-default capture option now forwards CaptureFirstFightEvents to the
existing engine. An expandable virtualized list shows recorded first-fight times,
event kinds, source slots and amounts. Events and equal-time ordering are retained
as returned; they are never iteration-averaged. Slot names use equipment/skill
metadata, with a humanized fallback and an explicit unspecified marker. Times
retain submillisecond detail and use current-culture formatting.

Amounts describe outgoing/incoming damage, shield absorption or actual repair for
those events. PreservationStarted carries a scheduled budget rather than repair
already delivered. Zero-damage shots remain numeric zero, while non-amount events
show not applicable. Continuous repair entries use interval-end timestamps. The
trace is not a complete state-change log or a replacement for run-wide reports.
An enabled capture with no records has an explicit empty state; truncated captures
explain that omitted events do not truncate the aggregate simulation results.

The existing engine cap remains 100,000 records. Projection happens once for an
accepted result; native rows have a bounded viewport and virtualize. Changing
capture invalidates/cancels the run like other options. No engine event, formula,
seed/statistical contract, pack, baseline fixture or dependency changes. Encounter
chart overlays and full effectiveness reports remain pending.

### PR 18 report review corrections (2026-10-29)

The review assessment is an internal record. Accepted results carry their
captured snapshot; desktop projections use its pack and the original request's
capture option. Event rows are lazy and indexed for native virtualization, with
slot names cached per source. Uptime metadata is indexed once per report, and
phase names once per key. Report presence is explicit state independent of prose.
Downing explanations appear only for incoming runs. Header/data columns use
explicit container insets and matching fixed widths with shared horizontal
scrolling. Event labels use a plain switch, preserving undefined-enum fallback.
Simulation owns amount applicability and rejects undeclared nonzero event amounts.
Existing serialized events, numerical statistics and seeded results are unchanged.


### Skill impact and continuous-window statistics (2026-10-30)

DummyFightResult.SkillDamage is an optional SkillDamageBreakdown containing
ImpactDps and OverTimeDps summaries. It is present when damaging skills are
included, with numeric zero summaries for an inactive component, and absent when
skills are excluded. The scheduler's existing Impact flag classifies each actual
post-target-cap damage contribution. Continuous damage retains its clipped
windows, retarget gaps, incoming splits, downing and final-endpoint behavior.
Each component is summarized across per-fight damage divided by the full requested
duration. It does not depend on first-fight event capture.

Existing skill/weapon/total accumulation is unchanged, including summation order;
component statistics are additive result fields, not a change to damage formulas,
event ordering, contract identifiers or random streams. Component means conserve
skill damage within floating-point tolerance. Percentiles and standard deviations
are computed independently, never added across components.

The desktop summary and distribution cards expose both components when supplied,
while results without the optional breakdown retain their existing presentation.
Over-time denotes modeled continuous skill windows, including continuous-rate
approximations for skills such as Assault Turret. It is not restricted to status
DoT and does not include unmodeled weapon burn/bleed damage. P01 remains open for
that missing mechanic. No pack or frozen fixture changes.


### Skill statistics design review corrections (2026-10-30)

The review assessment is an internal record. This supersedes the
SkillDamageBreakdown/over-time terminology and two optional result fields above.
SkillDamageEvent carries a SkillDamageKind: Impact or ContinuousWindow for
currently produced events, with StatusDot reserved and refused until explicitly
modeled. A default non-impact branch never absorbs new kinds. Continuous-window
DPS includes static rate-based skill fire such as Assault Turret; it is distinct
from status DoT. Weapon burn/bleed damage remains unmodeled.

Optional DummyFightResult.SkillDps is now a grouped SkillDpsSummary containing
required Total, Impact and ContinuousWindow summaries. This deliberately revises
the pre-release result API shape; repository consumers are updated together.
One optional accumulator allocates all skill arrays together, validates all three
per-fight rates and summarizes the complete group. Existing total accumulation,
actual contributions, event order and random stream remain unchanged. Desktop
summary/card labels share constants; tests compare their emitted labels.

### Pack-defined burn and bleed ticks (2026-10-30)

P01's remaining half: statuses a selected talent applies can now deal damage over
time. The damage is pack data, not simulator constants: `simulation.statusDamage`
names the status, a tick stat, a period and whether a tick lands on application
(PACK_SCHEMA_V2.md, section 5). The current Division 2 pack defines none, so its
results are unchanged until ShadeTech delivers the rule (request R30).

StatusDamageState keeps one schedule per damaging status on the current target.
An application to a target without the status (never applied, or lapsed) starts
ticking at the application (tickOnApply) or one period later. Reapplying while
the status is still active extends its deadline and keeps its tick amount (R61
changed this: it used to take the current one) and its cadence, even when its next tick had fallen
at or past the old deadline. Ticks land strictly before the current deadline,
matching when a status reads as active. A target's death clears its
statuses and schedules. Each tick reads the tick stat from the driven state
before the shot that started the status, so Status Effects and other buffs apply.

Ticks are fight events: enemy attempts win ties, skill damage at the same time
lands first, and a tick lands before a shot at the same time. A deferred reload
start now also applies when ticks run, as it does with incoming fire, so a tick
due before the reload is not skipped. A tick deals its amount to the target's
pools without armor or health factors (how status damage meets armor is R20).
A status kill credits gear-talent kill events, Clutch and generic kill recovery
(armor and health on kill). Weapon-bound kill recovery (weapon repair-per-kill,
Doctor Home's mark, Preservation) is not granted, because what a status kill
credits is unverified; the result's limitations say so. Builds with those sources
are not refused, and no run fails depending on whether a tick lands the kill. BurnTick and BleedTick trace each tick; DummyFightResult.StatusDps
summarizes tick damage per full duration, and total DPS includes it. Contracts
advance to d2p-simple-buffs-v15 and d2p-incoming-random-v16.


### Buff cancellation and banked pool stacks (2026-10-30)

Two trigger fields cover talent effects the rules couldn't express (ledger P09):

- **`cancelOn` and `cancelFor`** (buff rules). When the `cancelOn` event fires while
  the buff is active, the buff ends at that moment and can't be gained again for
  `cancelFor` seconds. Cancelling takes the event: if the same event is also the
  rule's `on` event, an active buff ends rather than refreshing, and an inactive
  one is gained unless it is locked out. Only an event credited to the rule's
  talent cancels it: a status or skill kill, credited to gear talents only, leaves
  a weapon talent's buff alone. This is
  Gunslinger: a swap gains 5 s, and a swap while it is active loses it for 5 s.
  The pack approximates that today with `every: 10`.
- **`bank`** (pooled stacks rules). When a pool reaches its cap, each member rule
  with a bank writes its live stack count to the bank key, replacing the previous
  value. All the pool's stacks then clear, as before. Bank keys are whole-number
  inputs that must hold the rule's own max (one event can carry a rule past the
  pool cap). They are driven state, so resolved-state
  caching keys on them too. This is Rebalance: at 80 stacks the red and blue
  counts become separate lasting bonuses until the next cap.

Nothing in the current Division 2 pack uses either field, so its results are
unchanged until ShadeTech delivers the rules (request R32). Driven combat still
refuses weapon rotation, and the simulator never fires `swapped`, so a
`cancelOn: "swapped"` rule only acts once driven rotation exists (F35, F63).
Contracts advance to d2p-simple-buffs-v16 and d2p-incoming-random-v17.

### Grants from cleared stacks and collected pickups (2026-10-30)

Step 2 of the left-out talent effects (P09) adds two more pieces.

- **`clearGrant`, `clearAmount`, `clearDuration`** (stacks rules with `resetOn`
  or `consumeOn`). When the reset or consume event clears live stacks, the rule
  grants `clearAmount` per stack removed:
  - `temporary_armor`: that share of max armor, as bonus armor lasting
    `clearDuration` seconds (Full Stop: 5% per stack for 10 s on reload);
  - `shield_repair`: that share of the shield's health (Liberty or Death: 3% per
    stack on a headshot).

  TimedBuffState records the grants; the simulator applies them right after the
  event that cleared the stacks. Temporary armor is the game's bonus armor:
  IncomingDamageState keeps timed grants beside the build's static bonus armor.
  Grants are spent first, soonest to lapse first, then the static bonus, which
  never lapses. A grant is gone at its expiry (pruned whenever the pools update),
  and a downed player receives none. A counted stacks rule's reset only restarts
  its count, so only its consume event clears stacks and grants. A shield repair first
  advances the shield to the event, then repairs a raised shield up to its
  capacity; a broken shield receives none. Without incoming fire there is no
  player or shield, so grants change nothing. A pool cap is not a clear: Rebalance
  banks rather than grants.
- **Pickups** (Kill Confirmed's trophies). Collecting a dropped item is player
  behaviour, not game data, so it's a simulation option, not a pack rule. Every
  kill (weapon, skill or status) drops a pickup when a selected rule uses the new
  `pickup` event. It is collected `PickupDelay` seconds later (default 2, 0–60)
  with probability `PickupChance` (default 1). Rolls come from their own random
  stream, so other rolls don't change. A collection fires `pickup`. It lands after
  enemy attempts, status ticks and skill damage at the same time, and before a
  shot; none of those later than a pending collection runs before it. A downed player collects nothing, and pickups due at or after the fight's
  end are dropped. Reload starts are deferred while pickups are in play, as they
  are for ticks and incoming fire, so a collection due first is never skipped.
  The result's limitations state the assumed delay and chance whenever a selected
  rule collects pickups.

Trace events BonusArmorGranted (with its amount) and Pickup are added; shield
repairs use ShieldRepaired. The committed pack uses none of these, so its results
are unchanged until ShadeTech delivers the rules (request R33). Contracts stay at
d2p-simple-buffs-v16 and d2p-incoming-random-v17, introduced with step 1 in the
same change.

## C9 driven two-weapon rotation (2026-10-31)

Random weapon fights now combine DriveSimpleBuffs with either WhenEmpty or
AfterKill rotation. The desktop accepts this combination and still refuses
incoming fire with swaps. Incoming/shield rotation and driven damaging-skill
rotation remain outside this slice.

- Select trigger rules from both participating weapons and active gear. Ordinary
  shot/hit/kill/pickup/reload gains and cancellation belong to gear and the weapon
  currently in hand. Status arming/application also follows its weapon. Core
  resolves every driven state with that hand's weapon_slot, which participates
  in the state-cache key; whileHolstered modifiers remain Core's responsibility.
  Previous-shot pellet hits persist separately for each weapon, so a Brazen-style
  reading neither borrows the other weapon's hits nor loses its own on a swap.
  Target death clears pellet history for both weapons, including the holstered one.
- Event ownership scopes reset/consume handling as well as gains. A shot from
  the other weapon cannot consume a holstered weapon's buff. This ownership scope
  is separate from kill-credit eligibility: a skill/status kill may reset the
  current weapon's buff even when only gear can gain a kill trigger. Existing
  single-weapon behavior is unchanged.
- Swap/reload trigger events are queued at action starts, interleaved with pickup
  collections and status ticks. Equal-time pickups/ticks precede action starts;
  action starts precede the next shot. Events at or after Duration do not fire.
  SwapStarted/SwapCompleted traces retain their existing timestamps.
- At the swap start the hand becomes the destination. `swap` and
  `swap_after_kill` dispatch to gear and the source weapon; `swapped` dispatches
  to gear and the destination weapon. `swap_after_kill` requires a weapon kill
  by that source within the inclusive ten-second Finisher window. Other weapons'
  kills and skill/status kills do not qualify. The window is currently an engine
  constant, not general per-rule metadata.
- Gunslinger's existing five-second buff, cancel-on-swapped and five-second
  lockout now run from actual swaps. Finisher can be earned on its sidearm and
  resolved on the drawn rifle. Per-weapon rounds persist; a shot-triggered
  magazine refill leaves that magazine full and prevents an empty-magazine swap.

Refuse magazine/cycle phase rules (`rotation-phase-unsupported`) and a trigger
talent selected on both weapons (`rotation-shared-talent-unsupported`), which
need separate phase/rule instances. Cadence, capacity and pellets retain their
existing dynamic-state checks. Triggered reload or swap-speed changes are refused
with `rotation-timing-unsupported` because WeaponRotation owns a static clock.
A refill before an already queued empty swap/reload starts cancels it, instead of
silently executing an obsolete action (from 2026-11-06; it was refused until then).
The clock returns to when the action would have begun, and a reload waiting at the end
of a swap under way goes the same way. A forced shield swap or a swap after a kill stays.
After a reload starts its completion time remains committed, as in single-weapon
fights. These limits are reported in results rather than approximated.

The outgoing driven contract advances to d2p-simple-buffs-v17. Incoming remains
d2p-incoming-random-v17; static rotation remains d2p-weapon-rotation-v1. No pack
bytes or frozen baseline fixtures change. DrivenRotationTests covers source
ownership, exact damage/timing, window/horizon boundaries, Gunslinger cancellation,
status arming, refills/refusals, seeded replay and trace-free timeline conservation;
committed Gunslinger/Finisher cases exercise the actual pack through Core.

## C9 whole-second events and driven shot timing (2026-10-31)

Two refusals that blocked committed talents are replaced with modelled behavior.

- **`second`:** when a selected rule uses it, the event fires at each whole
  second of the fight (1, 2, 3, … before Duration), as Go's G426 did. At an
  equal time it fires after enemy attempts, pickups, status ticks and skill
  damage, and before swap/reload starts and the next shot; a single-weapon
  reload start is deferred behind it like behind pickups and ticks. Stand Your
  Ground's buff and stacks and Chemical Agent's Catalysis stacks (gated on any
  target status) now run instead of failing with `trigger-event-unsupported`.
- **Fire rate:** a driven state may change it. The interval after a shot is
  the one in effect when the shot fires, before the shot's own triggers (Go's
  semantics). Measured's magazine phases and Frenzy's post-reload rate of fire
  now run. Single-weapon reload time was already read as each reload starts,
  before the reload's own triggers (On Empty already ran); a test now pins that.
  Under rotation the driven interval paces the hand's shots; reload and swap
  times stay the rotation clock's and remain `rotation-timing-unsupported`.

Magazine size, pellet count and target pools still refuse
(`buff-timing-unsupported`): they would change state already committed (the
rounds in the magazine, the pellet capacity checked at setup, the target's pools).
Overflowing therefore remains refused.

Contracts advance to d2p-simple-buffs-v18 and d2p-incoming-random-v18. Results
that ran before are unchanged; only formerly refused builds now produce results.
No pack bytes or frozen baseline fixtures change. SecondEventTests and the timing
cases in DummyWeaponSimulatorTests/DrivenRotationTests pin exact times;
committed Stand Your Ground, Chemical Agent, Measured, Frenzy and On Empty cases
run the actual pack through Core.

## C9 driven rotation under incoming fire (2026-10-31)

Incoming fire now accepts a two-weapon rotation when triggers are driven. The
driven hand already re-resolves its state, including recovery, whenever the
weapon in hand changes, so weapon-bound kill repairs follow the weapon that
made the kill. Fixed-assumption rotations under fire would keep the starting
weapon's recovery and stay refused, as does a shield, whose allowed weapons
would have to steer the rotation (`incoming-rotation-unsupported`).
Preservation's budgets and Doctor Home's marks are tracked for the starting
weapon only, so either talent on a rotated weapon is refused
(`rotation-recovery-unsupported`). A sidearm that changes the player's pools or
the enemy cadence still fails the existing `incoming-state-unsupported` check.

Ordering: a queued swap or reload start now also yields to enemy attempts and
skill damage at earlier or equal times, and it advances armor regeneration to
its time before it changes the hand or fires triggers. Before, nothing could
combine queued actions with enemy fire or skills, so earlier results are
unchanged. The rotation skill-plan comparison now runs before incoming attempts
split continuous windows. Otherwise any skill window under fire would compare
split events with unsplit ones and be refused.

The incoming contract advances to d2p-incoming-random-v19; the driven
(d2p-simple-buffs-v18) and static rotation contracts are unchanged.
RotationIncomingTests pins per-weapon kill repair timing, the enemy/swap
ordering (a 1.5 s swap; without the fix the trigger clock steps backwards), the
skill comparison and the recovery refusal.

## C9 shields under driven rotation (2026-10-31)

A shield may now accompany a driven rotation under fire when it allows both
weapons: each weapon's slot is in the shield's list and its type isn't excluded
(Crusader and Striker with two non-LMG, non-marksman rifles). The shield's state
is unchanged by swaps. Its profile is re-read with the weapon in hand, and the
existing `shield-state-unsupported` check refuses a weapon that would change it.

A shield that bars either rotated weapon is refused (`shield-rotation-unsupported`),
for example Bulwark or Deflector, which allow only a sidearm, rotating with a rifle.
Go (G425) swapped to an allowed weapon the moment the shield was raised and back
to the build's weapon the moment it broke. Copying that would cut off a reload or
swap already under way, and this simulator keeps a started action's completion
time (see the driven-rotation section). Whether to interrupt committed actions,
or to defer the forced swap to the next decision point, is an owner decision
still open.

The incoming contract advances to d2p-incoming-random-v20; results that ran
before are unchanged. RotationIncomingTests pins exact shield absorption,
break and redeployment through swaps, the refusals for a barred slot and an
excluded type, and committed Striker (two assault rifles) and Bulwark (rifle
refused) cases.

## C9 forced shield swaps at decision points (2026-10-31)

Owner decision: when a shield allows only one of two rotated weapons, the
simulator follows Go's forced swaps (G425) but defers them to the next decision
point. This supersedes the `shield-rotation-unsupported` refusal above.

- While the shield is raised, the rotation may not draw the barred weapon. An
  emptied weapon in WhenEmpty mode reloads instead, and an AfterKill swap is
  skipped.
- At each decision point (the next shot, or once the current reload or swap
  finishes), a raised shield with the barred weapon in hand forces a swap to the
  allowed one. This also happens at the start when the build's weapon is barred.
  The barred weapon never fires with the shield up, but it may hold the shield
  until that decision point; results report this approximation.
- A break sets a return. At the next decision point, if the shield is still down,
  the build's weapon is forced back.
- Forced swaps take the rotation's swap time, reload an empty destination on
  arrival, fire swap triggers like any swap, settle a pending kill swap, and are
  never cancelled by a refill. `IncomingFightResult.ShieldSwitches` counts them per
  fight.

A shield that allows neither weapon is refused (`shield-weapon-unsupported`).
The incoming contract advances to d2p-incoming-random-v21. Results that ran
before are unchanged. RotationIncomingTests pins an exact timeline: a forced
draw at 0 s, a barred swap that reloads instead, a break and a forced return at
the next decision point, then a normal swap. It also covers the barred-swap
fallback, the neither-weapon refusal, and committed Striker and Bulwark
(sidearm-only, rifle forced out) cases.

## C9 recovery talents under driven rotation (2026-10-31)

Preservation and Doctor Home now run under a driven rotation and supersede the
`rotation-recovery-unsupported` refusal. Both follow the project's rule that a
holstered weapon's talent doesn't react:

- **Preservation:** owners are counted across both rotated weapons; more than one
  is still refused (`preservation-state-unsupported`). Its kill-type budgets are
  re-read with each weapon in hand, so only a kill by the owning weapon starts a
  budget. A budget already started keeps repairing after a swap.
- **Doctor Home:** only shots from the owning weapon apply or refresh the
  five-second mark. The kit is part of the recovery resolved with that weapon in
  hand, so a marked target killed by the other weapon gives no kit while Doctor
  Home is holstered. The talent's text ("if a marked target is killed") doesn't
  settle a kill made with the talent's weapon holstered; this choice keeps the
  holstered rule and Core's own holstered resolution.

TargetMarked and PreservationStarted traces name the weapon in hand. The incoming
contract advances to d2p-incoming-random-v22; results that ran before are
unchanged. RotationIncomingTests pins a sidearm Preservation budget (only its own
kill, exact repair by the horizon) and Doctor Home on the sidearm: marks only from
its shots, no kit for a rifle kill, and a capped kit for its own kill.

## C9 driven reload and swap timing under rotation (2026-10-31)

A driven rotation no longer refuses triggered reload-time or swap-speed changes
(`rotation-timing-unsupported` is retired). At each rotation decision (the shot
that fires before a swap or reload, a kill swap, or a forced shield swap), the
clock reads each weapon's reload time, swap speed and switch multiplier from the
driven state at that moment:

- The weapon being holstered uses its current state.
- The weapon being drawn uses the state it would have in hand under the current
  triggers, as Go recomputed the drawn weapon at a swap (G427). That state also
  sets its reload on arrival.
- A same-weapon reload uses the hand's state.

Single-weapon fights read the reload as it starts. Under rotation the action is
committed at its decision point, one interval earlier, so a trigger that changes
within that interval doesn't affect it. Reading the drawn weapon's state adds no
trigger effects: the trigger states only raise their observation clocks to a
time the fight has already refreshed.

Contracts advance to d2p-simple-buffs-v19 and d2p-incoming-random-v23 for the new
capability. Every run that used to succeed already had matching static timings,
so its results are identical; only runs that used to be refused now run. DrivenRotationTests pins a reload on arrival lengthened by
the drawn rifle's trigger, owner-only halves of each swap, and committed On Empty
under rotation.

## C9 phase readings under driven rotation (2026-10-31)

Magazine and cycle readings now run under a driven rotation, retiring
`rotation-phase-unsupported`.

- **Cycle readings** depend only on fight time and are unchanged.
- **Magazine readings** read the weapon in hand: its rounds and its capacity. A
  magazine rule owned by the holstered weapon is unset, as Go switched a
  holstered weapon's readings off (G428). A gear-owned rule reads whichever
  weapon is in hand.
- **Reloads:** the rotation clock commits a reload's rounds at its decision
  point, so it now records when each committed reload completes. Until then that
  magazine reads empty, as a single weapon's does between its last shot and its
  reload completion.
- **Uptime:** magazine-phase uptime counts a key only while it is read (its owner
  in hand), against the full fight duration.

Contracts advance to d2p-simple-buffs-v20 and d2p-incoming-random-v24; results
that ran before are unchanged. DrivenRotationTests pins exact Measured-style
damage and uptime through a swap and a reload on arrival, cycle readings across
swaps, and committed Measured under rotation.

## C9 a talent on both rotated weapons (2026-10-31)

The same trigger talent selected on both rotated weapons now runs as two copies
instead of being refused. Each weapon's copy has its own rules, with key, talent,
pool and bank suffixed `@slot`. Each copy reacts as its weapon's talent (gear
plus the weapon in hand), and before resolution the copy in hand supplies the
talent's real keys. The holstered copy keeps its timers and stacks but never
reaches resolution, which matches the holstered rule. Uptime reports each copy
as `key@slot`, and the desktop labels it with the slot name.

`rotation-shared-talent-unsupported` remains for copies that would collide:
- status rules, which act on one shared target;
- holstered effects (for example Finisher on both weapons), which would need
  both copies' state under one key at once;
- a key or pool shared with another talent's rule.

Review fix: a magazine reading for a talent owned by both weapons is kept while
either copy is in hand, and a weapon with no talents no longer fails the
ownership lookup.

Contracts advance to d2p-simple-buffs-v21 and d2p-incoming-random-v25; results
that ran before are unchanged. DrivenRotationTests pins per-copy damage that
differs from shared state, per-copy uptime, the three refusals and committed
Strained on both assault rifles.

## C9 timed status application (2026-11-01)

Status rules take `every`, and a status rule on `second` applies its status
directly. ShadeTech's R36 review of Dragon's Glare found the default burn applied
to one hostile every 35 s while in combat, with no shot involved. Until now a
status rule only armed on its event and applied on the next hit, and `every` was
refused.

- **Interval:** after a status rule arms or applies, its event is ignored for
  `every` seconds. This holds for arming rules too: a rule armed during its
  interval stays unarmed.
- **Direct application:** on `second` the status lands on the current target at
  that second, through the same deadline, duration scaling and tick path as a hit.
  A `status_applied` event follows, as on a shot.
- **No target:** while waiting for the next target there is none to burn. The
  second applies nothing, counts nothing and doesn't start the interval, so the
  burn lands on the first second with a living target. A due target arrives at the
  second, as for enemy fire.
- **Validation:** the pack validator now refuses `every` on an observer and a
  negative `every` on any status rule, matching the simulator.

Not modeled, as in ShadeTech's draft: the 20 m range, line of sight and its dwell
time, and which hostile is chosen; planner fights have one target in range.

No committed rule uses either capability yet, so committed results are unchanged.
Contracts advance to d2p-simple-buffs-v22 and d2p-incoming-random-v26.
StatusTriggerStateTests pin direct application, the interval, the no-target case
and arming under an interval; StatusDamageTests pin the tick times; a committed-pack
check runs ShadeTech's draft rule on the Imperial Dynasty holster.

## C9 swap events that only clear a rule, without rotation (2026-11-01)

A buff or stacks rule whose `cancelOn`, `resetOn` or `consumeOn` is `swap`,
`swapped` or `swap_after_kill` no longer refuses a single-weapon simulation. Without
weapon rotation the fight never swaps, so the event never fires and the rule runs
as if the field were absent; that is exact, not an assumption. ShadeTech's buff
review found Frenzy's buff removed on weapon switch, which needs `cancelOn:
swapped`, and the refusal would have failed every single-weapon Frenzy run.

A rule that gains on a swap event is still refused without rotation (committed
Gunslinger), since it could never trigger and the refusal says the talent needs
rotation. Other events stay refused when their source is off: without incoming fire,
"never damaged" would be an assumption.

Contracts advance to d2p-simple-buffs-v23 and d2p-incoming-random-v27; results that
ran before are unchanged. DrivenRotationTests pin committed Outsider (cancel) and
Strained (consume) with a swap clearing event against the committed rules, and the
Gunslinger refusal.

## C9 weapon tags on trigger rules (2026-11-01)

ShadeTech's R36 schema notes ask for `weaponTypes` on triggers, shared by Resolved
(rifle, marksman rifle and pistol hits), Quick Draw (the first pistol shot spends
the stacks) and the Concussion gap (5 s with marksman rifles). The planner adds it
as `weaponTags`, a list of item tags, because weapon types are item tags in the pack
(`weapon_type_pistol`) and the engine names no game's weapon classes.

- **Scope:** `buff`, `stacks`, `ready` and `lockout` rules. Phase, combat and status
  rules refuse it, as they refuse any field they don't read.
- **Weapon events:** shot, hit, crit, headshot, body hit, unarmored hit, the three
  kills, reload, the three swap events and status applied. The simulator passes the
  producing weapon's item tags: the weapon in hand for shots and their events, the
  reloaded weapon, the source weapon for `swap` and `swap_after_kill`, the drawn
  weapon for `swapped`.
- **Filtering:** a tagged rule ignores a weapon event from an untagged weapon, for
  gains and for resets, consumes and cancels alike. A weapon event with no weapon
  behind it (a skill or status kill credited to gear, a status applied on a second)
  doesn't reach a tagged rule either: it isn't a tagged weapon's event.
- **Other events** (`second`, `damaged`, `armor_break`, `shield_break`, `pickup`,
  `skill_hit`) are never a weapon's, so tags don't filter them.
- **Validation:** tags must be distinct and carried by some item in a weapon slot.
  An older planner rejects the unknown field rather than ignoring it, so no
  capability flag is needed.

No committed rule uses it yet, so committed results are unchanged. Contracts
advance to d2p-simple-buffs-v24 and d2p-incoming-random-v28. TimedBuffStateTests
pin gains and consumes by tag, the untagged-kill case and an unfiltered reset;
DrivenRotationTests pin a gear rule tagged for either rotated weapon; validator
tests pin the refusals and a round trip.

## C9 forced headshots (2026-11-01)

ShadeTech's R36 review of Resolved: a ready state on a 2 s cooldown from the hit
that spends it, during which the next damaging rifle, marksman rifle or pistol hit
is converted to a headshot that fires headshot events; a natural headshot also
spends it. `forceHeadshot` on a `ready` rule expresses it, with `weaponTags` for the
weapon types.

- **Scope:** `ready` rules whose `on` is `hit` or `shot`, so the forcing shot's own
  event spends the rule. A `buff` would stay active after forcing and convert every
  hit, which no reviewed talent does, so it is refused.
- **Forcing:** before a shot resolves, the simulator asks whether a forced-headshot
  rule is ready for the weapon in hand (its talent reacts there and its weapon tags
  match). If so, the first landed pellet is a headshot: headshot or critical headshot
  damage, `headshot` and, if it kills, `headshot_kill`. The pellet's random draws are
  unchanged and the forced flag is OR'd with the natural headshot, so runs without
  such a rule keep their random sequence.
- **Spending:** the same shot's `hit` (or `shot`) event puts the rule on cooldown,
  through the same eligibility and tag checks that allowed the forcing. With `shot`, a
  full miss spends it too (Quick Draw's "hit or miss").

No committed rule uses it yet, so committed results are unchanged. Contracts
advance to d2p-simple-buffs-v25 and d2p-incoming-random-v29. DummyWeaponSimulatorTests
pin forced headshot damage and events once per cooldown, untagged and tagged;
TimedBuffStateTests pin readiness by eligibility, tag and cooldown and the refused
shapes; validator tests pin the refusals and a round trip.

## C9 grants on firing (2026-11-01)

ShadeTech's R36 review of Ablative Nano-Plating: an armor break grants 80% of the
wearer's armor for 10 s, at most once per 45 s. `grant`, `grantAmount` and
`grantDuration` reuse `clearGrant`'s kinds and limits on `buff` and `ready` rules:
each time the rule fires it queues a grant through the same path stack-clear grants
use, so `temporary_armor` becomes bonus armor and `shield_repair` repairs a raised
shield. A ready rule on cooldown, or a buff held back by `every`, a count or a
lockout, doesn't fire and grants nothing. Stacks rules keep `clearGrant`; lockout
rules take no grant. Without incoming fire there is no armor to grant, as for
`clearGrant`. The per-ally cooldown and the specialization-kill reset stay out of
scope.

No committed rule uses it yet, so committed results are unchanged. Contracts
advance to d2p-simple-buffs-v26 and d2p-incoming-random-v30. IncomingFightTests pin Ablative's shape granting 8 at the
armor break and delaying going down; TimedBuffStateTests pin no grant on cooldown and
the refused shapes; validator tests pin three refusals and a round trip.

## C9 reload completion event (2026-11-01)

ShadeTech's trigger event review maps the pack's `reload` rules for On Empty, Frenzy
and Overflowing to the source's `WeaponReloadFinishedCondition`, while the planner
fires `reload` when a reload starts. For a 9–10 s buff that moves the window earlier by
the reload time, several seconds on an LMG. The planner adds `reload_end`, fired when a
reload completes, for the reloaded weapon:
- a single weapon's reload fires it at the completion step, whether the reload was
  deferred behind enemy fire, ticks, pickups or whole seconds or ran straight through;
- under rotation the action queue gains a completion entry at the reload's ready time,
  after its start entry. A queued completion doesn't count as a pending reload start when
  a refill looks for an action to cancel.

`reload` keeps its meaning, so no rule changes timing until a pack moves to `reload_end`;
which talents are reload-finished is the producer's source call. Every new firing is
gated on a selected rule using the event, so runs without one are unchanged. Contracts
advance to d2p-simple-buffs-v27 and d2p-incoming-random-v31. DummyWeaponSimulatorTests
pin the completion timing on both single-weapon paths and the shot after each rotated
reload.

## C9 count windows (2026-11-02)

ShadeTech's R38 answer on Pummel's graph: each qualifying kill adds to a count and
restarts a 20 s drop-off timer whose finish clears the count; reaching the count clears
it, applies the buff and stops the timer. `countWindow` on a counted `buff` or `stacks`
rule expresses it. Before counting an event, a partial count whose last counted event is
at least the window ago is cleared, so an event exactly at the window's end starts a new
count, as the source's timer finishes first. The count is internal, never a key, so the
check runs lazily at the next counted event. Completing the count clears it as before,
and later events count toward the next activation (the graph has no buff gate).

No committed rule uses it yet, so committed results are unchanged. Contracts advance to
d2p-simple-buffs-v28 and d2p-incoming-random-v32. TimedBuffStateTests pin a count
dropped after 21 s of quiet, the exact boundary, counting during the buff and the
refused shapes; validator tests pin two refusals and a round trip.

## C9 status application on the triggering hit (2026-11-02)

ShadeTech's R38 answer on status timing found two source shapes. Ignited, Sadist and
Eyeless mark full stacks and apply the status from the next damage while the mark is
set, which is the planner's arming model. Trauma applies its blind and bleed in the same
rule as the triggering damage condition, on the hit itself. `applyOnHit` on a `status`
rule whose event is a hit (`hit`, `crit`, `headshot`, `body_hit`, `unarmored_hit`)
takes the direct path a whole-second rule uses: the rule's own event applies the status,
through the same deadline, duration scaling, `every` and tick path, and the rule never
arms. The shot's `status_applied` follows as before. Other events are refused: a kill's
target is gone, and a shot may miss.

No committed rule uses it yet, so committed results are unchanged. Contracts advance to
d2p-simple-buffs-v29 and d2p-incoming-random-v33. StatusTriggerStateTests pin the application on the headshot, no arming and
the cooldown; StatusDamageTests pin tick times from the triggering hit; validator tests
pin two refusals.

## C9 holstered-time stacks and stocked headshots (2026-11-02)

ShadeTech's R38 answer on Quick Draw (a gear talent on the Dodge City holster): a
free-running 0.3 s timer, and on each tick, while the weapon in hand is not a pistol, one
instance up to 100, each adding to Pistol Damage. A bullet fired with a pistol in hand
removes the stacks and a forced-headshot override, both a frame later, so that bullet
still carries them; a miss spends them too.

- **Gain:** `gainWhileHolstered` on a `stacks` rule with `weaponTags`. The condition is the
  weapon in hand, not the talent's ownership, so it suits a gear talent and runs with or
  without rotation; in a fight that never draws a tagged weapon the stacks accrue but are
  never used. Ticks are whole multiples of the period from the fight's start, computed by
  index so they don't drift, which gives floor(t / period) or one more for a span of t, as
  the source can. The simulator tells the state the weapon in hand at the start and at
  every swap start (the hand changes at the swap start); events accrue pending ticks first,
  integrating uptime up to each tick before its stack is added. Reads between events don't
  accrue, which can only lag reported uptime, never damage, because the stacks matter only
  once a tagged weapon is drawn.
- **Headshot:** `forceHeadshot` on such a `stacks` rule with `consumeOn: hit` or `shot`
  forces the next landed hit from a tagged weapon while it holds stacks. Modifiers are read
  before the shot's events, so the spending shot carries the bonus, matching the source's
  frame-delayed removal.

No committed rule uses either yet, so committed results are unchanged. Contracts advance
to d2p-simple-buffs-v30 and d2p-incoming-random-v34. TimedBuffStateTests pin the tick
count, the cap with exact stack-seconds, the pistol-only spend and the refused shapes; a
committed-pack check runs the draft rule on the Dodge City holster with an ACR and a D50
under rotation; validator tests pin two refusals and a round trip.

## C9 talent damage over time (2026-11-02)

ShadeTech's R38 answer on Toxic Delivery: each status the player applies starts a debuff
on that target dealing a total over 10 s in 0.5 s ticks (`myInputDamageIsTotalDamage`),
with Status Effects scaling the total; a second application during it neither starts a
second one nor refreshes it. The pack's `toxic_delivery_total_damage` stat (PR #74) holds
the total. `simulation.talentDamage` entries carry the talent, event, total stat, duration
and period.

- **State:** the status tick state hosts talent debuffs beside burn and bleed, so their
  ticks take the same place in event order as status ticks. A debuff's ticks are computed
  from its start by index (no drift); the last lands at the end of the duration and the
  debuff ends.
- **Start:** every event passes through the buff dispatch, which also offers it to the
  talent debuffs with the same eligibility. A matching debuff that isn't running starts on
  a living target, reading its total from the current resolved state's stats (cached with
  the status tick amounts). Clearing the target clears them.
- **Damage:** ticks hit the target's pools without armor or health factors, like status
  ticks; a kill by one credits gear talents only. They count in total DPS and in a separate
  talent debuff DPS, not in burn and bleed DPS, shown in the desktop and CLI.
- **Coverage:** a `talent-damage` channel marks the talent modeled in effect coverage.

No committed rule uses it yet, so committed results are unchanged. Contracts advance to
d2p-simple-buffs-v31 and d2p-incoming-random-v35. StatusDamageTests pin the tick times,
amounts and the ignored reapplications, and that runs without a rule are unchanged; a state
test pins eligibility, the target check, the reset and the twentieth tick ending it;
validator tests pin four refusals and a round trip; TalentCoverageTests pin the channel.

## C9 copied talents without combat rules (2026-11-02)

Driven simulations refused any build with a talent-copying gear talent (Abridged) as
`buff-copy-unsupported`, because a copied talent's triggers would need copied ownership.
But Core resolution already applies a copied talent's stat modifiers for the weapon in
hand, so a copied talent with no combat rules of its own simulates correctly through the
resolved states. The refusal now applies only when an eligible copy source, for the active
weapon or the rotation's second weapon, has its own triggers or talent damage (Abridged
copying Strained, for example). Copy eligibility is Core's `TalentCopySelection`, the same
rules the copied stat batches use.

Contracts advance to d2p-simple-buffs-v32 and d2p-incoming-random-v36; results that ran
before are unchanged. CommittedDeliveryTests run Abridged copying Near Sighted onto an ACR
(its stability copied) with and without rotation, and keep the refusal for Strained.

## C9 gated status rules and magazine-sized caps (2026-11-02)

ShadeTech's R39 answer reads Ardent's graph: a meter of half the magazine fills by 1 per
bullet fired, empties when a reload completes and drains 6 every 0.5 s once 0.5 s pass
without firing. While it is full, every bullet that hits applies the default burn; the
check runs before the bullet's own increment, so the filling bullet doesn't burn.

- **Cap:** `maxMagazine` on a stacks rule sets its cap to floor(magazine × share),
  clamped to 1 and `max`. The magazine is the owning weapon's at the fight's start (a
  copy under rotation reads its own weapon); magazine changes during a fight don't move
  it. A talent with no weapon is refused.
- **Gate:** a status rule's `whileKey` reads a snapshot of the buff state: active
  buff, ready and stacks keys, and stacks keys at their cap (`whileFull`). The snapshot
  for a bullet is taken before its `shot` event, and all of that bullet's status events
  read it; other events take one at the event. A closed gate skips the rule entirely (no
  count, arm or cooldown). A gate key that a copied rotation talent owns is refused.
- **Drain:** the existing decay (`duration: 0.5`, `refresh`, `decay: 12`) starts losing
  stacks 0.5 s after the last shot at 12 a second, where the source drops 6 at a time
  every 0.5 s. At a full meter the first lost stack comes 1/12 s later than the source's
  first drop; both are well below full after a 1 s pause.

Actum Est stays out: its rounds are special ammunition whose effect is game code.

No committed rule uses either field, so committed results are unchanged. Contracts
advance to d2p-simple-buffs-v33 and d2p-incoming-random-v37. Validator tests pin six
refusals and a round trip; TimedBuffStateTests pin the cap and the gate reads;
StatusTriggerStateTests pin a closed and an open gate; StatusDamageTests pin bullets 6 to
10 of each 10-round magazine burning, and that a shot a second never fills the draining
meter.

## C9 weapon falloff at a fight distance (2026-11-02)

R7's in-game readings settled the shape: full damage out to optimal range, linear down to
long range, then a floor, with optimal-range bonuses scaling both ranges. The pack's
`equipped_weapon_optimal_range` and `equipped_weapon_long_range` already include those
bonuses, so the engine needs only the floor, which R42 asks ShadeTech for.

- **When:** a driven fight with a set distance, and a pack that declares the
  `weapon_falloff_floor` stat (a share of full damage, above 0 up to 1). Without the stat a
  distance doesn't change weapon damage, and the result says so.
- **What:** each hit's body, headshot and critical damage is scaled by the factor at that
  distance. It is read for each weapon's profile, so a rotated weapon and a re-resolved
  state use their own ranges. Statuses and talent damage a hit starts don't fall off. The
  floor holds beyond long range, including past maximum range, until R42 says otherwise.
- **Refused:** a floor outside (0, 1], or an optimal range of 0 or a long range short of
  it, at a distance.

The committed pack declares no floor, so committed results are unchanged. Contracts
advance to d2p-simple-buffs-v34 and d2p-incoming-random-v38. WeaponFalloffTests pin the
factor at six distances, the notes, the unchanged cases and the refusals.

## C9 falloff floor adopted; maximum range (2026-11-02)

The falloff-floor candidate (R42) gives each weapon its `myDamageMin / myDamageMax`, and
its source review settles the rest: the schema's `myRangeMax` says the weapon "does 0
damage above this range". The engine now returns 0 beyond `equipped_weapon_max_range`
when that is above 0 (range bonuses don't move it; 0 means no range data). Several
weapons list a long range past their maximum range; the cut-off applies first. A floor of
1 needs no ranges, since it is no falloff (Nemesis), and it also stands for the ten
weapons without source data, whose ranges are 0; before this change those ten were
refused at any distance. Contracts advance to d2p-simple-buffs-v35 and
d2p-incoming-random-v39.

## C9 R41: Preservation instances and Doctor Home status kills (2026-11-02)

ShadeTech's R41 reading (R41_REPAIR_TALENT_SOURCE_REVIEW.md) settles two of this ADR's
open choices:
- **Preservation:** each kill starts its own uncapped 5 s repair beside any running one
  (the source's `MultipleInstanceAction` with no cap or refresh). The independent-budget
  approximation is the source's shape, so it is now the only behavior.
  `ApproximatePreservationOverlap` and `preservation-overlap-unsupported` are removed;
  the CLI still checks and ignores `approximatePreservationOverlap`.
- **Doctor Home:** a marked enemy drops the kit whoever or whatever kills it. A burn or
  bleed kill of a marked target now adds the kit to the generic kill recovery. Weapon
  repair-per-kill and Preservation still follow weapon and skill kills only; skill kills
  are an in-game check.

Clutch's code matches the reading. Doctor Home's marks are per enemy in the source; the
dummy keeps one and clears it on retarget, which differs only if it retargets before a
kill. Contracts advance to d2p-simple-buffs-v36 and d2p-incoming-random-v40.

## C9 armor-repair grants (2026-11-02)

The first of R41's accepted fields. A `buff` or `ready` rule's `grant` may now be:
- `armor_repair`: a share of max armor, at once or spread over `grantDuration`. A timed
  one needs `grantInstances: "independent"` and joins the independent repair queue that
  Preservation's kills use, so overlapping firings add their rates.
- `missing_armor_repair`: a share of the armor missing when the rule fires, at once.

Repairs cap at full armor, apply only with incoming fire, and do nothing while the
player is down. The event's handler has already walked recovery to the firing time,
which the queue requires. ShadeTech proposed `grantPeriod` (0.2 s ticks); it is left out
because repairs here are continuous, as Preservation's already were. No committed rule
uses the grants yet, so results are unchanged. Contracts advance to
d2p-simple-buffs-v37 and d2p-incoming-random-v41.

## C9 armor gate and stat durations (2026-11-02)

The second stage of R41's fields, for Clutch:
- **Key gate on more rules:** `whileKey`/`whileFull` now gate buff and ready rules too.
  TimedBuffState takes a snapshot of its keys before each event (the same `Gates` read
  status rules use), so a rule never reads its own event's changes. A rule can't gate on
  its own key.
- **Armor gate:** `whileArmorBelow` compares the player's armor share at the event, which
  the simulator passes to each firing, against the share; armor counts as full without
  incoming fire. With both gates set, either opens the rule.
- **`durationStat`:** a stat sets the duration. ShadeTech's proposal assumed a formula
  duration already existed; it doesn't, so this is the one field added beyond the
  proposal. The value is read at the fight's start, like `maxMagazine`'s magazine, and
  must be above 0 up to 600.

No committed rule uses these, so results are unchanged. Contracts advance to
d2p-simple-buffs-v38 and d2p-incoming-random-v42.

## C9 target_death (2026-11-02)

The last stage of R41's fields, for Doctor Home. Instead of a new mark kind and a kit
pickup, one event: `target_death`. The simulator fires it from ClearTarget, which every
death path (weapon, skill, status and talent ticks) already calls after the death's kill
events. It goes to every talent in the fight, since a marked enemy drops the kit whoever
kills it, and it skips weapon-tag filtering. Buff-type rules only.

A mark is then a buff on `hit` consumed on `target_death`, and the kit a buff on
`target_death` gated by `whileKey`, whose snapshot is taken before the event. The kit
stays instant, as the existing code has it, rather than a delayed pickup; pickup timing
is an assumption, and this keeps an adopted candidate's results equal to the code's. A
`target_death` that refills reloads like any refill. No committed rule uses it. Contracts
advance to d2p-simple-buffs-v39 and d2p-incoming-random-v43.

## C9 crit_pellet (2026-11-02)

R43's candidate 2 put Clutch's repair on `crit`, which fires once per shot. The source,
and the planner's Clutch code, repair once per critical damage event, so a shotgun shot
with several critical pellets repairs several times. The new `crit_pellet` event fires
once per landed critical pellet, after the shot's `crit`. Each firing applies its grants
before the next, so a gate on armor reads the armor the previous pellet left; the
existing code does the same. No committed rule uses it. Contracts advance to
d2p-simple-buffs-v40 and d2p-incoming-random-v44.

## C9 repair talents as pack rules (2026-11-02)

The repair-talents candidate 3 (R43) moves Clutch, Doctor Home and Preservation into pack
rules. The talent-ID code is removed: ClutchRepairState and ClutchRepairProfile,
DoctorHomeMarkState and DoctorHomeRepairProfile, PreservationRepairProfile, and their
branches in DummyWeaponSimulator. Earlier choices in this ADR about Preservation budgets,
Doctor Home marks and Clutch eligibility are replaced by the rules.
- **Recovery:** RecoveryProfile is the generic kind. Generic kill recovery no longer
  carries Doctor Home's share, which the pack moved to `doctor_home_kit_repair_percent`.
  The previews of rule-made repairs are listed in NotPlayerRecovery, so they don't trip
  the fail-closed recovery check:
  - `missing_armor_repair_on_crit_percent`, `estimated_armor_repair_on_crit`;
  - `armor_repair_on_kill_over_time_percent`, `estimated_armor_repair_on_kill_over_time`;
  - `doctor_home_kit_repair_percent`.

  Their preview assumptions (`clutch_repair_state`, `clutch_missing_armor_percent`,
  `preservation_kill_type`) feed only previews and are no longer reported as simulated
  recovery.
- **crit_pellet:** it now counts only pellets landing on a living target. ShadeTech found
  that PR #92 also counted a dead target's pellets, which the source excludes
  (`myTriggerOnZeroDamage`), adding 41% on a shotgun.
- **Behaviour now:**
  - these repairs need driven triggers;
  - a killing shot's crit repairs follow the kill's repairs (owner-accepted);
  - under rotation a marked target drops the kit whichever weapon kills it.

Contracts advance to d2p-simple-buffs-v41 and d2p-incoming-random-v45.

## C9 distance defaults (2026-11-02)

The first fight-model step (P12). The pack's `enemy_hit_rate` input describes
ShadeTech's in-game counts (about 70% within 15 m, 30% beyond), but its value is a flat
default the user changes. Instead of the engine hard-coding those numbers, the pack may
now declare `simulation.distanceDefaults`: for a numeric input, values by distance band.
At setup, a fight with a set distance gives each such input the band's value when the
user hasn't set it. The fight's assumptions hold only explicit values, so "not set" is
exact. The value goes into the fight's context before the build resolves, so everything
that reads the input sees it: EnemyFireSchedule's hit probability, and any formula. A
result note says which value applied, or that an explicit one overrode it.

It is generic, not tied to the hit rate. Distances already require driven triggers, so
these defaults do too. No committed pack declares bands, so results are unchanged.
Contracts advance to d2p-simple-buffs-v42 and d2p-incoming-random-v46.

## C9 time to clear (2026-11-02)

The second fight-model step (P10, encounters by kill count). Ending a fight at its Nth
kill would change every per-fight rate, since DPS, uptimes and the timeline all divide
by the fixed duration. So fights keep their length, and `ClearKills` only reports: each
fight records the time of kill number N (from the first target's arrival, kills at or
before the horizon), and the result gives `TimeToClear` over the fights that got there
and `ClearedFraction` over all of them. Targets come one after another with the retarget
delay between them, as in every targeted fight. Without targets the option is refused.
The CLI takes `clearKills`, and the Desktop has a "Targets to clear" field and a summary
line, which says the time covers cleared fights only. The option only adds output
fields, and no existing value changes, so no contract advances.

## C9 mixed targets (2026-11-02)

The third fight-model step (P10). `TargetSequence` lists enemy target profiles (the pack's
`enemy_target` input), and a fight's targets cycle through it in order.
- **Pools:** each profile in the sequence is resolved once at setup for its armor and
  health. A profile outside the input's options, or 0 (no target), is refused.
- **Damage:** the current target's profile joins the driven state as `enemy_target`,
  so each state resolves with it, and bonuses that depend on the target apply to that
  target. The pool check in a driven state compares with that state's own profile.
- **Enemy fire and recovery:** they stay the chosen profile's. A state's fire side
  resolves with the chosen profile, so the enemy cadence and hit damage don't change with
  the target, and a result note says so. Per-target enemy fire is left for later.
- **Between targets:** after a kill, driven states read the arriving target's profile, as
  the remaining-pool reading already reports the arriving target at 100%.
- **Scope:** it needs driven fights (the profile rides on the driven state), no weapon
  rotation (the second weapon's template resolves one profile), and no damaging skills
  (their schedule is fixed at setup). Other runs are refused with
  `target-sequence-unsupported`.
- **Resolves:** each profile's resolve and each separate fire-side resolve must succeed
  (`resolve-failed`), and their diagnostics join the result's. A state whose target is
  the chosen profile reuses its own resolve for the fire side.
- **Surfaces:** the CLI takes `targetSequence`. The Desktop's "Mixed targets" picker
  (added in a follow-up PR the same day) offers the pack's `enemy_target` profiles
  without "no target", appends them up to 100, and checks the same restrictions before a
  run.

Results without a sequence are unchanged. Contracts advance to d2p-simple-buffs-v43 and
d2p-incoming-random-v47, the two contracts a sequence can run under.

## C9 mixed targets with rotation and skills (2026-11-02)

Two of the mixed-target restrictions are lifted:
- **Rotation:** the weapon not in hand already resolves through the driven state with the
  current target's profile. The setup's same-pools check between the two weapons is about
  the chosen profile and still holds, and each driven state's pool check uses its own
  profile.
- **Damaging skills:** skill damage is scheduled once at setup, from the chosen profile.
  The setup now compares each sequence profile's skill schedule with it, and refuses the
  run only if a profile changes skill damage (`target-sequence-unsupported`, with that
  reason). That replaces the generic mid-fight skill-state error. Skills against mixed
  targets therefore deal the chosen profile's skill damage, which is exact when no
  profile changes it.

Under rotation, each profile is checked with both weapons: the same pools from each, and
no skill change. The code review found the first version checked the active weapon only.
`TargetSequenceProblem` now only requires driven triggers, and the Desktop form follows
it, so swaps and skills are allowed with a sequence. Results without a sequence are
unchanged. Runs that were refused now produce results, so contracts advance to
d2p-simple-buffs-v44 and d2p-incoming-random-v48.

## C9 targets at several distances (2026-11-02)

`TargetDistances` lists distances that a fight's targets cycle through, with a target
count shared with `TargetSequence` but cycling independently.
- **Per target:** each target's distance feeds the combat context (distance rules such
  as Ranger) and its weapon's falloff. Shot profiles are resolved per distance, and the
  state key carries the distance, so a change of distance re-resolves.
- **Between targets:** after a kill, states read the arriving target's distance, as they
  do its profile.
- **Fight level:** enemy fire, the setup and the assumption defaults by distance
  (`distanceDefaults`, set before the build resolves) stay at the fight distance: the
  `Distance` set, or else the first target's. A note says which. The weapons' first
  profiles use the first target's distance.
- **State key:** the distance joins a state's key only when the pack declares a falloff
  floor. Distance rules already put their keys in the state, so without falloff,
  distances don't multiply resolves.
- **Scope:** targets and driven triggers are required (`target-distances-unsupported`).
  So is the absence of distance rules under incoming fire: such a rule would carry each
  target's distance into enemy fire and recovery. The CLI takes `targetDistances`. The
  Desktop's "Target distances" row (a follow-up PR the same day) takes them one at a time,
  above 0 and up to 100 m like its target distance. Before a run it checks for talent
  triggers and random fights (the shared `TargetDistancesProblem`), and for a typed
  distance not yet added. Whether a selected talent has a distance rule depends on the
  resolved pack rules, so that refusal under incoming fire comes from the engine, as a
  run error.

Results without distances are unchanged. Contracts advance to d2p-simple-buffs-v46 and
d2p-incoming-random-v50.

## C9 skill damage per target (2026-11-02)

Skill damage now follows the target in a mixed-target fight. The skill schedule's plans
keep timing (window and cycle) apart from damage (rate and impact), so:
- **Setup:** each profile's schedule must keep the chosen profile's timing, compared with
  `SameTiming`: the same slots, skills, windows, cycles and damage kinds. It must also
  be the same with both weapons. A change in timing, or in which kinds of damage a skill
  deals, is refused: events come from the chosen profile's schedule.
- **Applying damage:** when a skill event lands, it deals the current target's own amount
  for that slot: its impact, or its rate over the batch, computed as the event's own is.
  The code review found that a ratio of the two could round one unit below an exact
  kill.
- **Runtime check:** a driven state is compared with its own target's plan, so
  trigger-driven skill changes are still refused as before.

The event times are unchanged, so splitting continuous windows at enemy attempts and at a
target's arrival works as before. Runs that were refused now run, so contracts advance to
d2p-simple-buffs-v45 and d2p-incoming-random-v49.

## C9 time to kill (2026-11-02)

P15 asks for a time to kill that counts headshots, falloff, cover and resistances. The
pack's static formulas leave them out, but a targeted fight already applies all four, and
it recorded per-target kill times for its first fight only. Each fight now also sums its
kills' seconds from arrival to death (kills within the fight), and the result's
`TimeToKill` summarises the fights' means over the fights with a kill. One value per
fight keeps memory bounded where every kill would not. It is reported in the CLI
(`timeToKill`) and in the Desktop summary. It only adds output fields, so no contract
advances. The code review set out its limits, now stated in a note and in the docs:
- a target still alive at the end is censored, not counted; `KilledFraction` gives the
  share of fights with a kill, and the Desktop says when none had one;
- fights weigh alike, rather than every kill;
- the time includes the fire interval or reload carried over from the previous target.

Only kills before the fight's end count, now for clearing too, as repairs and pickups
already did. The first fight's `killTimes` trace keeps every credited kill.

## C9 cover as a peek cycle (2026-11-02)

ADR 0009 has the design. In the fight loop:
- **Phases:** `CoverCycle` finds a time's cycle by index, corrected for rounding so a
  cycle's start is never after the time and its end always is. A fight starts at a peek.
- **Weapon:** a shot whose next interval would cross into cover moves the weapon's clock
  to the cover boundary. There, a magazine that isn't full reloads, once per cover phase
  (an empty one reloads there anyway). The weapon then waits for the next peek and for the
  rest of its fire interval, so a short cover phase never hastens a shot. A running reload
  carries on across boundaries.
- **Enemy fire:** each attempt still draws its roll. Behind cover it is blocked (counted in
  `blockedShots`) before shock is checked, so shock credit doesn't build there. While
  peeking it lands below the hit chance times `PeekHitShare`.
- **Cover selector:** when the pack has the `in_cover` selector, it is set for the whole
  fight, before the build resolves. Coverage's input basis says it was set by the cycle.
- **Scope:** driven triggers (`cover-cycle-unsupported`, the shared `CoverCycleProblem`).
  The Desktop's three cover fields (a follow-up PR) check ranges, which come together, random
  fights and that shared rule before a run.
- **Rotation (2026-11-03):** the rotation commits each action on its own clock, so moving
  `time` alone would leave it ahead of the fight. `Fire` takes `stopAt`, ending the interval
  at the cover boundary for the next decision, whatever it is. `CoverReload` commits a
  reload of the weapon in hand at the boundary, and magazine readings keep the rounds it
  held until it completes, as a single weapon's do. Forced shield swaps and kill swaps run
  at the weapon's next decision: every wait, for a target or a fire interval, stops at
  the next boundary, so a decision comes at each one, but not between them.

Results without a cover cycle are unchanged. Contracts advanced to d2p-simple-buffs-v47
and d2p-incoming-random-v51 with the cycle, and to d2p-simple-buffs-v48 and
d2p-incoming-random-v52 with rotation. Not reloading in cover when swapping when empty
(2026-11-03, a deliberate result change) advanced them to d2p-simple-buffs-v49 and
d2p-incoming-random-v55.

## C9 enemy fire by target profile (2026-11-03)

Owner decision, 2026-11-02: each target fires with its own profile's cadence, damage and
accuracy. This replaces "enemy fire and recovery stay the chosen profile's" from the mixed
targets section. The fight has one target at a time, so the model follows it:
- **Who fires:** the target in play, and after a kill the arriving one, as driven states
  already read. `enemy_attackers` still multiplies that profile's fire, and enemies keep
  firing through a retarget gap.
- **Timing:** with a sequence there is no fixed schedule. The first attempt comes one
  interval of the first target in. Attempts count from an anchor, so a steady cadence
  doesn't drift and matches the fixed schedule's times. A kill brings the next target into
  play, and the wait for the next attempt keeps its share of an interval at the new
  cadence, so a slow target's pending attempt can't hold back a fast one's fire. Without a
  sequence the fixed schedule stays, so those fights can't change.
- **Recovery:** read from the same state, so it follows the target too. The separate
  fire-side resolve is gone.
- **Setup:** each profile resolves its own fire. Refused with
  `incoming-state-unsupported`: player armor, health or attackers that change with the
  profile; a mix of firing and silent profiles (a silent target would leave no attempt to
  time the next from); fire that differs by weapon. A state's fire is compared with its own
  target's, so triggers still can't change the cadence. Each profile's cadence limit and
  recovery are also checked here, so whether a run is refused never depends on which
  targets a fight reaches. A fight starts with the weapon in hand's fire.
- **Skills:** continuous skill damage was split at the fixed attempts at setup. Attempts
  are now known only during the fight, so with a sequence it stays in its batches of up to
  100 ms, and an attempt inside one lands before that batch's damage. The note says so.
- **Distance:** enemy fire and distance defaults stay at the fight distance; following
  each target's distance is the next stage.

Fights without a sequence are unchanged. Only mixed-target fights under incoming fire
change, on purpose (ledger P10). d2p-incoming-random advances to v53; d2p-simple-buffs
stays at v48, since nothing without enemy fire changes.

## C9 several enemies at once (2026-11-03)

ADR 0008 has the design. In the fight loop:
- **Clocks:** `EnemyGroup` holds one attempt clock per enemy, keyed by the enemy's place in
  the fight's order. The next attempt is the soonest clock, and ties go to the earlier
  group place, so a fight stays deterministic. One enemy at a time with a sequence is a
  group of one, so the anchor and the kill retiming from the enemy fire section live
  there, with no change in behavior.
- **Skills:** a group sharing one cadence keeps its clocks through kills, so its attempts
  are known at setup and skill batches split at them. With a sequence they aren't known,
  so batches stay whole, as for mixed targets.
- **Who fires:** the enemy being shot uses the state's own fire. Any other enemy uses the
  same state resolved with its profile (`Shooter`, through the state cache), so an enemy
  that isn't being shot still hits as its own profile against the player's current state.
- **Kills:** the dead enemy's place goes to the target behind the group, `N` places on.
  The focus moves to the next in line as before, and the retarget delay is now the time to
  aim at someone already there. Arrival times for time to kill are unchanged.
- **Shock:** statuses cover the target being shot, so only its attempts can be blocked.
  With several enemies the attackers share in `ShockFireState` isn't needed: `enemy_attackers`
  above 1 is refused, because it would count enemies twice.
- **Scope:** incoming fire and driven triggers (`enemies-at-once-unsupported`, the shared
  `EnemiesAtOnceProblem`). The attempt limit counts every enemy. The Desktop's "Enemies at
  once" (a follow-up PR) checks the range, random fights and that shared rule before a run.
  It reads `enemy_attackers` from the session, and rechecks as it changes.

- **Running out (`enemiesShrink`):** the group is told the total, and a dead enemy with nobody
  to replace it loses its clock. After the last kill the next target's arrival is set to
  never. Everything that waits for an arrival already reads that time, so skills find no
  target. The weapon starts nothing new, though a reload under way completes, and its clock
  runs off the end of the fight while other events finish. Readings stay on the last enemy
  rather than the sequence's next.

Results without the option are unchanged. d2p-incoming-random advances to v54, and to v56
with `enemiesShrink`.

## C9 enemy fire by each target's distance (2026-11-03)

With the pack's distance bands (R44), a target's distance decides the enemy hit rate. This
replaces "enemy fire and distance defaults stay at the fight distance" in the several
distances section, and that section's refusal of distance rules under incoming fire.
- **Bands:** with `targetDistances`, each input following distance gets its band at every
  distinct distance at setup. A missing band, or one that would change the cadence or the
  player's pools, is refused there, so the outcome never depends on which targets a fight
  reaches. A value the user sets needs no bands.
  The inputs join the driven keys, so a state reads the band of the target in play, the
  arriving one after a kill.
- **Cadence:** the hit rate is in the pack's incoming DPS, so the cadence doesn't move. A state's
  fire is compared without its hit chance only when the hit rate itself follows the target;
  a trigger can't reach it then, since the validator keeps driven keys out of distance
  defaults.
- **Groups:** `Shooter` resolves a waiting enemy's fire with its own profile and distance, and
  reads distance rules at that distance, so a rule like Challenger's follows the enemy
  firing, not the one being shot.
- **Fight distance:** `Distance` gives way to target distances from setup on.
- **Set values:** a rate the user sets wins at every distance.

Fights without target distances are unchanged. d2p-incoming-random advances to v57.

## C9 pellet hits and set talents in fights (2026-11-03)

Two small additions, both owner decisions on ShadeTech's candidates. Neither changes a result
until a pack rule uses it, so no contract advances.
- **`hit_pellet`:** fires for each pellet landing on a living target, after the shot's `hit`. It draws
  no random numbers, like `crit_pellet`.
- **Set talents:** a fight could select only talents in an item's talent slot, so a set's own
  talent couldn't have a rule. `SetBonusSelection.FullSetTalents` now adds those no item
  offers, once the set's top bonus applies. Every set-modelled talent belongs to a 4-piece set
  today, and 29 of the 86 have no slot. The top bonus is the rule because nothing in the pack
  links a talent to a piece count; a pack needing a lower tier would need a new field. The
  fight asks the resolver which sets apply, so piece credit counts and a rule runs exactly when
  the row reading its key applies. The resolver doesn't select these talents, so their
  own modifiers and `has_talent` don't see them, which keeps static results as they were.
  Their effect stays on the set's bonus row, as Apex Engine's already does. Effect coverage
  still lists a set's talents with any piece equipped, and says a set link establishes no
  event behavior; that needs revisiting when a rule arrives.

## C9 armor kits (2026-11-03)

ADR 0010 has the design, and this is stage 1, normal kits.
- **Pack data:** `simulation.armorKit` holds a kit's numbers, and which specializations and talents
  replace it, so no ID lives in the engine. Without it a fight with kits is refused.
- **State:** `ArmorKitState` holds the kit rules, so later stages have one place to add to.
- **Starting one:** at the weapon's next decision, if armor is below the threshold then, so armor
  that has recovered spends nothing. That comes after any reload start and completion, so a
  reload or swap under way finishes first. With a cover cycle, only behind cover: the weapon's
  clock already stops at each boundary. The weapon's clock then jumps to the kit's end.
- **Hits:** one landing on armor lengthens the use, clamped to the longest use. Hit times are
  computed, so "one extension later" allows for rounding.
- **Ending one:** the repair lands when the weapon's clock reaches the end, after enemy attempts at
  that time, so a tied hit still extends it. The kit is spent then, so a fight ending first or a
  downed player spends nothing.

Results without kits are unchanged. d2p-incoming-random advances to v58.

## C9 enemy counts (2026-11-04)

ADR 0008's `count` rule. `EnemyCountState` holds the rules; `EnemyGroup.Alive` says who is there, silent
groups included. The count joins the driven state through `OnTarget`, so a waiting enemy's fire and a
rotation's decisions read it too, and event rules sample in `FireBuffs`, for talents that react then.

No shipped rule used it then, so no result changed. d2p-incoming-random advanced to v59.

## C9 armor bonuses on ticks (2026-11-04)

R20: burn ticks take Damage to Armor on armor. `armorBonus` on `statusDamage` and `rateArmorBonus` on `simulation.skills` name
a stat added to the armor factor, and health's stays 1. The stats join the resolved state beside the status ticks,
so a tick reads them at the time, with the weapon in hand; which weapon the game uses isn't settled (R20's in-game check).

A skill batch of up to 0.1 s reads the state as the batch ends, so a change partway through applies to all of it.

No shipped rule uses them yet, so no result changes. Contracts advance to d2p-simple-buffs-v50, d2p-incoming-random-v60 and
d2p-static-skills-v3.

## C9 enemies hold their state (2026-11-04)

ADR 0011's first stage. `FightEnemy` holds an enemy's pools, statuses, ticks, shock credit and pellets, and the fight's `target` is
one. `StatusTriggerState` reads and writes the `TargetStatuses` it holds, and keeps the player's arming counters. Enemies are still
made when they arrive, and one at a time, so no result changes and contracts stay.

## C9 moving between enemies (2026-11-04)

ADR 0011's second stage. `focusSeconds` moves the player on at a weapon decision. `FightEnemy.Between` stands in during a move and
reads as dead, so a move is the wait between a kill and the next arrival, and those paths needed no change. Enemies left wait
in `left` by place, and `Ticking` finds the next tick among them and the one being shot. A tick killing an enemy left behind holds that
enemy while the kill runs, then returns. `EnemyGroup.Following` picks who's next, and `Replace` takes the next in reserve,
which under focus fire is the same enemy as before. Rules cleared by `target_death` go with their enemy in `EnemyMarks`.

Without the option no result changes. d2p-incoming-random advances to v61.

## C9 counting a status, and target_changed (2026-11-04)

ADR 0011's third stage. A `count` with `status` reads the group's enemies with it, those left behind too. Its key may keep its
talent's `status` rule, which the simulator drops when the count runs. `target_changed` fires at the start of `MoveOn`, and
`neverFires` covers it in fights that never move. No shipped rule uses either, so no result changes, and contracts stay.

## C9 the armor_kit event (2026-11-04)

ADR 0010's third stage. `armor_kit` fires as a kit's repair lands, through `FirePlayerEvent`, which `target_changed` shares. Gear and the
weapon in hand gain on it, what it clears clears everywhere, and a status rule arms for the next hit. No shipped rule uses it, so no result changes.
d2p-incoming-random advances to v62.

## C9 skill damage made as a fight goes (2026-11-04)

ADR 0012's first stage. `SkillEventStream` replaces the loop's list: each slot has a next use, and a use's impact and batches are made
when the fight reaches them, cut at the attempts known in advance. `SkillFights` keeps each use's events for the run, so fights share
them, and the stream walks the few uses running side by side. `SkillUseEvent.Compare` gives the order the sorted list had. Setup still
builds that list once, for the work limits and the two weapons' comparison. The plan is fixed, so no result changes, and contracts stay.

## C9 two reset events, and a status rule's range (2026-11-04)

For R55. `alsoResetOn` is a second reset event, where a list on `resetOn` would have changed a field every pack uses. A status rule's
`within` reads the enemy being shot's distance when its event fires. Unlike a `count`, it doesn't refuse with no distance: every plain
fight with Bleeding Edge would be refused, so the range is taken as met, and the result says so. The adopted pack's armor bonuses and
Shakedown change results, so contracts advance to d2p-simple-buffs-v51, d2p-incoming-random-v63 and d2p-static-skills-v4.

