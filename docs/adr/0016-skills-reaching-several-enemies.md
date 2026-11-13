# ADR 0016: Skills reaching several enemies

Status: Accepted, 2026-11-13. It follows the owner's call after R105 to model a spread starting wherever a skill's status lands
(ADR 0015, "Amended after R105"), and the owner took the recommendation on all seven points below.

## Context

In fights a skill's damage and status land only on the enemy being shot. An impact, a rate's batch or a status goes on that
enemy, through its own pools (ADR 0011, ADR 0012), and no other enemy is touched. Spaced impacts (R65, `impactInterval`) reach a
new enemy only by landing after the old one died.

In the game many skills hit an area:

| Skill | Its area in the pack |
| --- | --- |
| Explosive, Airburst and Cluster Seeker Mines | `radius_meters` |
| Explosive Sticky Bomb | `radius_meters` |
| Firestarter, Riot Foam and Oxidizer Chem Launchers | `radius_meters` |
| Artillery Turret | `radius_meters` |
| Shock Trap, Banshee Pulse, Riot Foam Chem Launcher | `radius_meters`; in fights they put their control status (shocked, disoriented, ensnared) on the enemy being shot for their window |
| Burn Sticky Bomb | none listed |
| Incinerator Turret, Stinger Hive | `range_meters`: how far they reach, not an area |

Each skill with a `radius_meters` has an overcharged one too (`overcharge_radius_meters`).

Because every enemy but the one being shot is untouched, fights undercount area skills whenever several enemies stand close together.
It also leaves the owner's call after R105 with nothing to act on: R105 found that Creeping Death spreads from whichever enemy a
skill's status lands on, but in fights a skill's status only ever lands on the enemy being shot.

ADR 0013 already places enemies on a line, `enemySpacing` metres apart. Blasts, spreads, pass-ons and redirects all reach "each
living enemy within `within` of" an enemy on that line, and reach nobody without a spacing.

## Proposal

The proposal is written with the owner's decisions on the points below.

**A skill rule may name `area`, a preview output giving a radius in metres.** An impact of that skill then lands on every living
enemy in the group within that radius of the enemy being shot, not only on that enemy. So does the status the impact puts on.
Each enemy reached takes the impact on its own pools, as the enemy being shot does.

**Spaced impacts** (Artillery Turret's shells, a Cluster Seeker Mine's mines; `impactInterval`) are each centred on the enemy being
shot as that impact lands. One landing after its enemy died centres on the next.

**Rates and windows stay on the enemy being shot.** That covers a turret's fire, a hive's stings and a chem cloud's ticking. They
aim at one enemy, or their area moves with the fight in ways the data doesn't describe. A follow-up can take clouds.

**Enemies stand on the `enemySpacing` line** (ADR 0013). Without a spacing, or with one enemy at a time, the area reaches nobody
else, and the result names the skill.

**Events stay with the enemy being shot**, as a blast's do (ADR 0013): an impact fires `skill_hit` once, and `status_applied`
fires for the enemy being shot only. The others take the damage and the status, and nothing else.

**The status's spread:** a skill status newly landing on another enemy through the area sets off spreads whose row takes skill
statuses (ADR 0015) around that enemy, as it would around the enemy being shot. When one impact puts it on several enemies at the
same moment, the enemy being shot comes first, then the others nearest first along the line (the next in group order between two
at the same distance). The spread's one cooldown covers them all, so the first that reaches someone takes it.

**Reporting:** damage dealt to other enemies counts in skill DPS, beside the skill's damage to the enemy being shot.

## Decided points (owner, 2026-11-13: the recommendation on each)

1. **Where the area is centred.**
   - **On the enemy being shot** (recommended). The player aims the skill there, and every other "reach" in fights is measured from
     an enemy on the line.
   - **Wherever it reaches the most enemies.** A player places a mine or a chem cloud for the most hits, so this is the best case,
     not a typical one.
2. **Damage to the others.**
   - **Full damage** (recommended), the same impact as the enemy being shot takes, until ShadeTech's data shows a falloff.
   - **A falloff by distance**, which needs a curve the pack doesn't have.
3. **Kill credit for an enemy killed elsewhere.**
   - **As any kill elsewhere** (recommended): ADR 0013's rule for blasts, `target_death` to all and `kill` to gear talents only,
     with no `skill_kill`. Every kill away from the enemy being shot then counts the same way.
   - **As a skill kill:** `skill_kill` too, as a skill's kill of the enemy being shot is. Skill-kill talents (Skilled, Outsider)
     would then gain from kills a blast's kill wouldn't give them.
4. **Which numbers the area reads.**
   - **The pack's radius output, overcharged when the skill is** (recommended). Rows name `radius_meters`; the overcharged one is
     already in the skill's tier model.
   - **A fixed number in the row.**
5. **The skills.** Recommended: the damaging ones the table lists with a `radius_meters`: the Explosive Sticky Bomb, the three
   seeker mines, the Firestarter Chem Launcher's ignition and Artillery Turret's shells, each on ShadeTech's reading of the scripts.
   The Burn Sticky Bomb has no radius listed, so it waits on R107's answer.
6. **Control skills.** Shock Trap, Banshee Pulse and Riot Foam put shocked, disoriented or ensnared on the enemy being shot for
   their window.
   - **Take them too** (recommended): their status goes on every enemy in the radius for the window, where fights track it. A
     shocked enemy then stops firing, as the enemy being shot does today.
   - **Damaging skills only**, leaving control skills single-target for now.
7. **Events from the others.**
   - **Once, from the enemy being shot** (recommended), as above and as blasts do.
   - **Once per enemy reached:** `skill_hit` and `status_applied` per enemy, so talents stacking on skill hits gain with each enemy
     an area reaches. That reading needs the source to say those talents count each enemy.

## Built, and two corrections (2026-11-13)

Stage 1 is built (see "Stages"). Two things in the proposal turned out differently:
- **Control skills aren't on enemies in fights at all.** Their rules (Shock Trap, Banshee Pulse, Riot Foam) give a cooldown and a
  window, but fights track only a skill's burn or bleed (PACK_SCHEMA_V2.md, under `status`), so not even the enemy being shot is
  shocked, disoriented or ensnared by a skill. The table above was wrong to say they are. Point 6, the owner's call to take control
  skills too, therefore waits on fights tracking control statuses at all: ADR 0017 (accepted 2026-11-13).
- **A status on the others counts as a status.** The impact's burn or bleed goes on each other enemy as ADR 0015's copies do: as the
  player's status, carrying the skill's `statusRate`, for one application, ticking as a status. So its damage counts in burn and
  bleed DPS, not skill DPS, the same as a copy's. The impacts themselves count in skill DPS.
- **Which spread comes first.** The enemy being shot takes the skill's own status with the skill's first batch, just after the
  impact, not at the impact itself, since impacts and status batches are separate events. So a spread from an enemy the area
  reached, at the impact, takes the shared cooldown first. A spread from another enemy also leaves the enemy being shot alone, so
  its own status stays the skill's.

## Falloff (owner, 2026-11-13, after R107)

Point 2 gave full damage "until ShadeTech's data shows a falloff". R107 shows one: each blast goes through the game's
`CommonExplosion`, with full damage inside an inner radius falling to a minimum at the outer. Four of the six skills fall off: the
Sticky Bombs to 10% at their edge from half their radius, and the Seekers to 50% from 1 m. The Firestarter's ignition and
Artillery Turret's shells don't. The data doesn't give the curve between.

The owner chose a straight line between the two: a row names `areaFull` (the full-damage radius, an output) and `areaEdge` (the
edge's share). The impact's status goes on whole, since the falloff is the blast's damage. R107's rows are adopted after this, so
results move once. R107's rows were adopted with R108's on 2026-11-13, so results moved once.

## What this doesn't do

- **Clouds and other areas that last.** A Firestarter cloud's burning area, or an Oxidizer cloud's, applies over time where the
  cloud is. Fights don't place clouds.
- **Control skills' windows on enemies other than the one being shot,** unless point 6 takes them.
- **Moving enemies.** Enemies stand still on the line.

## Stages

1. **The engine and validator:** `area` on skill rules; impacts and their statuses reaching the enemies in the radius; spreads
   from where a skill status lands; reporting; limitation notes. The committed pack's rows don't set it, so no result changes.
   Built on 2026-11-13, for damaging skills (see "Built, and two corrections").
2. **The request (R107):** ShadeTech's reading of each area skill's script: the radius it uses, whether damage falls off, whether
   its status goes on every enemy hit, and how the source credits kills. Then a candidate with the rows, and figures.
3. **Adoption:** a deliberate result change for builds with an area skill, in fights with several enemies and a spacing.

## Alternatives considered

- **A fixed number of extra enemies hit**, such as "an impact hits two". Simpler, but it ignores spacing and the radius the pack
  already has.
- **Leave skills single-target** and keep ADR 0015's spread to the enemy being shot. That was the recommendation the owner declined
  after R105.

## Consequences

- Area skills gain in fights with several enemies close together. Builds that pair them with Creeping Death gain twice, since each
  enemy the status lands on can spread it.
- Skill DPS includes damage to enemies other than the one being shot. Time to kill and kill counts follow.
- Fights without a spacing, or with one enemy at a time, are unchanged.
