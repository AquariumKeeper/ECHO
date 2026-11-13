# Division 2 Pack Provenance

Pack version: `y8s3.2-pve.2026-10-25.shields-swaps.1`

Target: level-40 PvE catalog for Year 8 Season 3.1 (Red Horizon), with reviewed
Year 8 Season 3.2 patch-note and in-game corrections.

## Doctor Home base-damage correction

The pinned Division 2 Community Data Hub input (`weapons/rifles.csv`, commit
`9c9ff25552439aabe9f995f4ed66897040ac0cc0`, SHA-256 `fc199b0045cfc7988220b240d046c9dddab720e860c4b8ebd8e528cc6ea23ab5`) lists this
M1A exotic at 129269.5 base damage.
The importer applies 129179.5 only while the full row in
the review manifest still matches.

The Shade-Tech game-file evidence report `results/gameplay/reader_v2/special/rifle/doctor_home_exotic_weapon_evidence.json` has SHA-256
`f6083ca0d9daf705f76a4cfddf08adcb9faab11933f3e697301d406887af1117` and identifies archive build `6386534`,
snapshot `611819b9fdc517ca12d93c63a860844a648064ff60f069cefd41507d9cfdb950`, and asset
`hunter/game system data/juice/item/player_weapon_rifle_doctor_home_exotic.mitem` (UID `F58BDD5662D6903700058E9E9C2F2A89`, decoded SHA-256
`0fae8c1f35732a7f40286577e9eca4d0008dd52b53d813de495394e108c55431`). Its nested weapon declaration UID
`F58BDD5662D6903700058EA06FFBA3E2` at line 77
inherits `player_weapon_rifle_Doctor_Home_exotic_t1`; line 79 gives
`myDamageMax 274.779999`, and line
88 identifies `M1A_T3`.
This is archive-derived source evidence, not runtime formula proof.

The reviewed level-40 card shows 153.08k with a
measured 6.0% SHD Watch weapon-damage pool and
12.5% weapon core. Under the explicitly stated additive-pool
assumption and ±5 display rounding, the card implies
a base interval of 129122.7–129240.2: the corrected value is inside,
and the community value is outside. The source value predicts 153184.4;
the corrected value predicts 153077.7. The card result supports
the correction only under that assumption. This tests candidate bases under the stated additive-pool assumption. It does not prove runtime formula selection, intermediate precision, or the per-bullet half-unit rule. The card
values and cited
`Shade-Tech DAMAGE_TRACE.md` revision `7bd176d8679695e12afe9656a5df1318126449aa`
(SHA-256 `8842D6CD81111470C654B07D9527C6AEC6FCC3084D99E95128F1AEBE52E55011`) are preserved in
[`division-2-weapon-observations.json`](../sources/division-2-weapon-observations.json).
See [SDF_VERIFICATION.md](../../docs/SDF_VERIFICATION.md) for evidence scope.


## Localized talent label (G276)

Negotiator's Dilemma now displays **Hostile Negotiations**, replacing the pinned
community's authoring name Crowd Control. The reviewed English localization GUID
is `BFAAA1FC5E31F8E90047A174A3B47E63` in build 6518373; the
companion records in-game confirmation. Selected evidence, file hashes and the
community rows are pinned in `data/sources/division-2-localized-talent-corrections.json`
at companion revision `9c2f39d864dd9d5c173ed8d9cd0bdeb7fa30bc35`.

Eight display fields change. All IDs, formulas, numeric values, context keys,
save/share formats and the compatible pack version remain unchanged. This label
correction does not validate the talent's runtime mechanics or authenticate the
original in-game capture.


## Reviewed corrections (G286–G323)

`data/sources/division-2-reviewed-corrections.json` applies
45 overrides backed by in-game readings,
34 backed by Ubisoft's TU Y8S3.2 Red Moon Unleashed (general and exotic updates)
(PDF SHA-256 `7eee7585852931b92eab37377da33b691289688a5ae454cefcd7802c63e7a08b`) and 9 backed by
Shade-Tech mods-v1 weapon-mod export, build 6518373, with its strict diff from build 6386534 (SHA-256 `06746f66d8d9…`), where the weapon-mod source
changed in the patch build. An override can cite more than one kind of evidence.
Each override records the importer's exact
source value; regeneration fails if that value drifts. The in-game readings
agree with the companion SDF game-file values wherever the two sources differed.
The pinned Community Data Hub input remains unchanged.

G313 applies six exact weapon-slot values from level-40 inventory cards:
Bullet King magazine 1,000 → 100, Froth magazine 33 → 19, and Oh Carol base
damage 541,954.4 → 433,563.5. The portable card-reading fixture pins source
revision `68f68fbf609b06a3fadb1e1878a1ea350c81ede3` and hashes for the companion
transcription and weapon report mapping. It verifies that 433,563.5 rounds to
the Oh Carol and Classic M44 card reading of 433.6k while the community value
rounds to 542.0k. Froth's base damage remains pending. The Lichtenberg reading
supports only that named weapon's ordinary source base and level-1 Trapper
minimum; it does not establish a global Prototype rule.

G323 records three in-game skill readings against companion research revision
`034759f8f73e50af67926b058e7f72286640d198`; the manifest records SHA-256 hashes
for `IN_GAME_CHECKS.md`, `MINING_TARGETS.md` and `mappings/skill_outputs.json`.
Restorer's installed Expertise reference is retained as +1% per grade in the
reviewed planner, while the community workbook cache still says `+1s Duration`.
The game dictionary records a percentage bonus; the paired readings changed
from 187.6 to 189.4 seconds, a 1.8-second step consistent with the 180-second
source base. Ordinary duration stays unchanged while Nanite application order
awaits verification. G329 adds a separate comparison of the additive variant
pool before Nanite, without replacing ordinary duration or proving live runtime
stacking. Airburst
Overcharge damage and burn damage use the observed +100% values; Explosive Sticky
Overcharge recovery uses +90% Skill Haste. The pinned community workbook cache
remains unchanged; exact importer formulas are guarded by the reviewed-corrections
manifest.

Not applied:

- Perfect Soft Spot: Only the ordinary variant was observed; game files give 32% for 15s.
- Rifle and MMR ammo capacity +20% (Y8S3.2): Per-weapon reserve ammunition is not modeled.
- Rifles with a non-60% base headshot bonus (Y8S3.2): The notes state 60% to 80%; six rifle entries with other community bases need their own check.
- Weapon handling pass (Y8S3.2): The notes give no stability values.
- Doctor Home and Vindicator scope-in or ADS toggle (Y8S3.2): Aiming modes are not modeled.
- Prototype quality inference from Lichtenberg (G313): One Lichtenberg card shows the ordinary Classic M1A source base and Trapper level-1 minimum; it does not establish a global Prototype base-damage or quality-roll rule.


## Turret skill-mod displayed bounds (G308)

Eight already-supported ordinary turret item/effect bindings now use the pinned
level-40 Orange displayed endpoints at power 3777: Ammo Box Health 2.5–10%,
Magnetic Rail Damage 2.5–5%, and Spare Parts and Multi-tool Duration 2.5–7.5%.
The actual mod power was not visible, and the visual bar comparison is approximate.
The source does not establish a roll step, which remains omitted. Existing
G229 observations and formulas continue to justify only these item/effect/variant
bindings; source pool membership and lexical reach do not generate runtime choices.
These bounds apply to ordinary previews only. Overcharge and stacking with
installed Expertise remain uncertain. Decoy, Artillery Turret and all other
skill-mod choices remain fixed or unavailable.

## Weapon falloff ranges and tactical reload (G288–G351)

`data/sources/division-2-weapon-source-fields.json` pins long range, maximum range
and reload channels from the Shade-Tech `shade-tech.weapons` v1 export for build
6518373 (SHA-256 `c2f17ede0f9a3bbf`,
330 records; data requests R1–R3). Records map to 275
community rows by localized display name, by equal RPM, magazine and optimal range,
or by a reviewed card reading. 273 consistent rows' game-file
optimal range equals the community value; the importer accepts the other 2 only
as reviewed differences (Tactical UMP-45 is corrected to its 16 m card; Sweet Dreams
keeps its community value until an in-game check). 275 rows apply.
No row is ambiguous.

Long range scales with the Optimal Range bonus total, as the game's
RangeLongFinal does; maximum range is unmodified. Tactical reload (a reload with
rounds remaining) applies to 224 weapons whose community reload
matches the empty-magazine channel, and uses the existing Combined Reload Speed,
a planner interpretation. It is withheld where the tactical channel is not shorter
than the empty one (Safety Distance). Damage at and beyond long range is not modeled.

## Weapon switch multipliers (G427)

`data/sources/division-2-weapon-switch-multipliers.json` pins each weapon's
`myWeaponSwitchSpeedMultiplier` from Shade-Tech's R25 evidence (SHA-256
`a1c0f007d79a3c33`, build 6518373). It applies to
275 community rows; 50 swap faster than the
default 1.0 (the SCAR family 1.05, six rifles 1.4, pistols 1.55, the PF45 family
2.0). The combat simulator divides the holster and draw halves of its base swap
time by the weapons' multipliers and one plus Combined Swap Speed (Swap Speed plus
Weapon Handling, as the game's switching bonus is). The base animation times are
not in the data and stay a simulator setting.

## Per-shell reloads (G378)

`data/sources/division-2-weapon-shell-reloads.json` pins first-shell, per-shell
and end reload times for 27 catalog rows (33 records)
from Shade-Tech's R21 `weapons-v1` delivery (data request R18). Their Estimated
Weapon Reload Time is (first shell + per shell x (final magazine - 1)) / (1 +
Combined Reload Speed), so it grows with magazine bonuses; the end animation is
not added. 25 rows match the community reload at base
magazine; SPAS-12, Thorn use the in-game-confirmed
5.50 s sequence instead of the community 4.65 s. Of the export's records,
33 are shell sequences, 64
are not and 233 are unresolved and keep the community
reload.

## Prototype gear (G390)

Prototype is an item quality above High-End, Named and Gear Set (Ubisoft's Rise Up
notes). High-End, Named, Gear Set and weapon items can be Prototype; exotics
can't. A Prototype attribute rolls from the item's previous maximum to its
Prototype maximum, the pinned catalog's `proto_max`: 1.5 x the maximum for 73 of
81 catalog attributes. The other eight differ only by rounding (for example
Armor Regeneration 7,388 for 7,387.5) except Skill Haste, 15% against 18% in the
game files. How the game rounds these maxima is unverified. The rifle Critical
Hit Damage core's Prototype maximum is corrected to 51%, 1.5 x its corrected 34%,
as the Y8S3.2 notes state. A Prototype Skill Tier core counts 1.5 tiers (TU
Y8S1.1); skill previews floor the total, as the game's display does.

Each Prototype item carries one of nine augments from a shared pool, with ten
levels from the catalog's minimum in steps of its fidelity. Copies on several
items add, and only the six gear pieces and the active weapon count, matching
the official seven-copy totals. Since G393 four augments take effect: Entropy
adds its share of total armor to health, Trapper lengthens status durations (not
status damage), Echo multiplies practical damage per shot by 1 + its chance, and
Paradox's refills stretch the practical reload cycle by 1 / (1 - 2 x chance).
Their placements are planner interpretations needing live verification; the other
five are recorded as stats only. The Activity assumption normalizes Prototype
items for Raids, the Incursion and the Toxic Dark Zone: attributes count at the
Prototype minimum and augments are disabled. The Blackout Dark Zone's PvP augment
values are not modeled.


## Enemy targets (G292, G352)

`data/sources/division-2-npc-targets.json` pins the level-40 PvE multiplier table
(role x difficulty x 1-4 players) and 13 enemy base profiles from the
Shade-Tech `shade-tech.npc-stats` v1 export for build 6518373
(SHA-256 `1cec69852e8ca5d5`; data request R4). Profiles group the
1549 combat declarations with all bases by role, health base
and armor base (212 distinct); those with at least
20 declarations are selectable and cover
1085. Each profile's example matches base x multiplier.

G352 adds a reviewed sample of 7 named enemies, one per main faction, as further
targets from the Shade-Tech `shade-tech.npc-spawns` v1 export with mission links (SHA-256
`11e6f7c068597597`; data request R10b). Each has a direct localized
name, every base, a combat faction and a mission script spawner referencing a titled
mission; 225 of 274 referenced named NPCs qualify (36 lack a name,
3 lack bases, 9 lack a titled mission and 1 has an excluded
faction). Named targets use the named role's scaling. That a referencing script
runs in that mission, and the spawner's role override, are not established.

Target health and armor are source formula evaluations, not in-game observations.
Which declaration a mission spawns, and absolute health in game, are not established.
Time-to-kill estimates assume every shot is a bodyshot that hits, with no falloff,
reloads, cover, damage resistance or NPC armor plating mechanics.


## Overcharge damage comparison (G198–G199)

An optional default-off view assumption displays 25 hypothetical Overcharge
damage channels for 18 damaging variants alongside unchanged ordinary estimates.
Exact K-column bonuses, baseline cells, fingerprint and revision are retained in
`data/sources/division-2-skill-overcharge.json`. Overcharge replaces the ordinary
tier damage bonus; it is not added on top of tier 6. Existing damage pools remain,
with the same inferred Expertise placement on its 17 supported channels. That
interaction and all community values require live-game verification. This does
not activate buffs or model utility, healing, cooldowns, duration, fire rate,
shield invulnerability, recipients or DPS. See [OVERCHARGE.md](../../docs/OVERCHARGE.md).

## Overcharge healing comparison (G200–G201)

G200–G201 adds six separately gated Overcharge healing comparisons for five
variants. The pinned cache retains measurement units and Reinforcer's K165:K168
shared anchor. The Overcharge bonus replaces ordinary tier scaling while
preserving Combined Repair Skills and Total Skill Repair. Ordinary previews,
damage comparisons and character stats stay unchanged. Healing Expertise,
recipient caps, Incoming Repairs, application events and activation remain
unmodeled; live verification is required. See [OVERCHARGE.md](../../docs/OVERCHARGE.md).

## Overcharge geometry comparison (G202–G203)

Thirty source-backed geometry channels across 28 variants have an independent
default-off comparison. Radius/range/cone percentages replace ordinary tier
bonuses; Tactician scan range adds exact meters. Bombardier's unitless K250 remains
excluded. Geometry references do not establish area, targets hit, detection,
recipient buffs or amplification. Ordinary outputs and character stats remain
unchanged. See [OVERCHARGE.md](../../docs/OVERCHARGE.md).

## Overcharge capacity comparison (G204–G205)

Twenty reviewed variants expose an independent default-off maximum-capacity
comparison. Exact integral source counts replace ordinary tier additions.
Charges, ammo, traps, mines, bombs, zones and maximum targets retain their source
meaning; current availability, refill, self-revive consumption and hit events
remain excluded. Ordinary outputs and character stats remain unchanged.
See [OVERCHARGE.md](../../docs/OVERCHARGE.md).

## Overcharge lifetime comparison (G206–G207)

Twenty-nine reviewed deployment, cloud and effect-lifetime channels expose an
independent default-off comparison. Exact source percentages replace ordinary
tier bonuses, preserving Combined Skill Duration and reviewed Nanite factors.
Status durations, recipient buff durations, time remaining, survival and uptime
are not inferred. Ordinary outputs and character stats remain unchanged.
See [OVERCHARGE.md](../../docs/OVERCHARGE.md).

## Overcharge status-duration comparison (G208–G209)

Eleven reviewed status-duration channels expose an independent default-off
comparison. Exact source percentages replace ordinary tier bonuses, preserving
Combined Status Effects, additive Burn Duration and separate Trauma bleed
scaling. Artillery's unitless bleed baseline retains the ordinary seconds
interpretation with an explicit verification note. Status duration is separate
from deployment lifetime, application, recipient resistance and remaining timers.
Ordinary outputs and character stats remain unchanged. See [OVERCHARGE.md](../../docs/OVERCHARGE.md).

## Overcharge health comparison (G210–G211)

Thirty reviewed health channels have independent, default-off Overcharge comparisons
in both web and native inspectors. Source bonuses replace ordinary tier bonuses;
device health retains Combined Skill Health (Skill Efficiency once), and shields
retain their additive Shield Health pool. Zone and foam health stay source-only
references. This estimates capacity, not current health, survival, foam break time
or shield invulnerability. Installed Expertise and skill mods remain excluded;
source values and stacking require live-game verification.

## Overcharge utility comparison (G212–G213)

Nine utility channels across six variants have an independent default-off
comparison in both planners: Firefly speed, Decoy threat, Booster buff amount,
and Artificer buff amount, duration, repair share and refresh seconds. Source
percentages replace tier percentages; Artificer refresh adds 22 seconds to its
3-second baseline. Generic player bonuses and installed Expertise do not scale
these references. No recipient effects, actual repairs, refreshed timers,
targeting, travel time or activation is inferred. Live verification remains.

## Overcharge recovery comparison (G214–G215)

Thirty-one reviewed recovery channels have independently gated, default-off
comparisons in both planners: 27 ordinary cooldowns and four Chem Launcher
single-ammo recovery estimates. Source haste replaces tier haste, preserving
Combined Skill Haste with Skill Efficiency once. Nonpositive speed yields a
guarded zero estimate, not readiness. Banshee charge speed and Hive refill remain
excluded. Current timers, full refill and activation are not inferred; source
values and stacking require live-game verification.

## Overcharge support references (G216–G217)

Utility Comparison also shows seven support references: raw Overcharge haste
bonuses for all five Hives, Reviver's raw armor bonus and Jammer's charging-time
reference. Baseline refill seconds and revive armor shares stay separate. These
do not calculate combined Hive refill or restored armor, and player bonuses and
Expertise do not scale them. Current timers, recipients and activation remain
outside these source references; interpretation needs live verification.

## Smart Cover support references (G218–G219)

Both Smart Cover variants expose five reviewed support properties as separate
base shares and tier-bonus references. Utility Comparison optionally shows the
source Overcharge bonuses. Weapon Handling, out-of-cover damage, Bonus Armor,
Explosive Resistance and Pulse Resistance do not become recipient modifiers;
the source does not establish their final aggregation. Existing deployment and
player-stat estimates remain unchanged. Source interpretation needs live verification.

## Shield regeneration references (G220–G221)

All four shields expose eight source-tier active/holstered regeneration estimates
plus optional Overcharge comparisons under Utility Comparison. Ordinary estimates
use effective shield tier, including configured armor cores; Overcharge replaces
the tier percentage. Bulwark baselines explicitly use HP/s; the other six retain
unspecified source units. Generic health, efficiency, repair, Expertise and mods
do not scale these references. Current health, actual healing, destruction,
depletion and caps remain outside the model; source stacking needs live verification.

## Additional drone cooldowns (G222–G223)

Defender, Fixer and Tactician now expose ordinary cooldown estimates and optional
Recovery Comparison outputs. Explicit dashed tier and Overcharge cells add zero
haste; Combined Skill Haste includes Skill Efficiency once. Nonpositive speed
retains a guarded zero estimate without asserting readiness. Existing outputs
and player stats remain unchanged. Sniper's blank cooldown tiers remain excluded.
Current timers, destruction, cancellation and deployment delays are not simulated;
source values and stacking need live-game verification.

## Defender protection references (G224–G225)

Defender Drone exposes its source Damage Reduction base share and tier bonus
separately, plus an optional Overcharge bonus under Utility Comparison. The
workbook's 20% baseline, tier bonuses up to 30% and 60% Overcharge bonus do not
become combined player mitigation. Generic player bonuses, Expertise and mods
do not scale these references. Coverage, damage taken, caps and uptime remain
outside the model; source interpretation requires live verification.

## Skill Expertise (G191)

All 43 variants accept installed grades 0–30,
bound to the exact selected skill. 17 damage
channels estimate grades in the tier pool; all variants retain conditional source
bonus references. Skill Info cells, revision and workbook fingerprint are retained
in `data/sources/division-2-skill-expertise.json` and validated by
`scripts/cache_division2_skill_expertise.py`. Damage stacking is a community-based
inference requiring live verification. Generic Burn/Bleed and unreviewed utility,
healing, health and regeneration aggregation stay unchanged. See
[SKILL_EXPERTISE.md](SKILL_EXPERTISE.md) for scope and evidence. Older milestone
notes below describe their original scope before skill grades were added.

## Reviewed gear-mod lower bounds (G349)

Five types use displayed source lower bounds supported by six crafted visual
roll-bar readings: CHC 1.2%, CHD 6%, headshot 1.3%, blind/deaf resistance 5%, and
skill haste 6%. All maxima, catalog steps and the other twelve minima remain
unchanged. The original community rows and byte-pinned source report/notes are
preserved. Steps are catalog assumptions, not verified generation increments.
Stored below-minimum rolls remain serialized and receive the existing invalid-
roll diagnostic; they are not silently increased. See `docs/GEAR_MOD_MINIMA.md`.

## Assumed direct Stinger DPS (G347)

Stinger Hive shows a direct sting rate (always shown since G354) using its final ordinary damage
formula divided by an editable repeat interval (0.1–60 seconds, default 1).
The default is the recorded approximate single-target restinging observation,
not the source 0.2-second targeting pulse. It assumes all stings land while
charges remain and excludes bleed, Overcharge, charge recovery, deployment and
target distribution. It is not a lifetime or whole-cycle estimate. The cadence
gate and shared TS/Go fixtures guard the ordinary-formula boundary.

## Assumed active ticking DPS (G346)

Eight reviewed burn, bleed and Oxidizer channels show a ticking DPS rate
(always shown since G354). It divides the existing final ordinary damage formula by an editable
0.1–10 second interval, default 0.5 seconds from the pinned cadence source.
This assumes the current channel is per tick; no damage bases are replaced.
Burn Sticky retains its workbook base 79794 despite the equal source multiple
used by Firestarter and Airburst at 79635. The mapping audit records this
cross-source discrepancy. Initial impacts, Overcharge, application totals,
refresh behavior, overlapping clouds and deployment uptime are excluded.
See `docs/SKILL_CADENCE.md` and `scripts/audit_skill_tick_mapping.py`.

## Assault Turret assumed burst DPS (G345)

The Assault Turret DPS rates (always shown since G354) reuse the final ordinary damage-per-shot
formula, including modeled tier, Expertise, mods and bonuses, with no second damage
multiplier. Assumed Damage per Burst is damage times editable landed shots; Assumed
Firing DPS divides by editable firing seconds; Assumed Burst-Cycle DPS divides by
firing seconds plus an editable pause. Initial 20 shots / 5 seconds / 1-second pause
come from the preserved R15 observed reference, with the pause recorded as an
approximate upper bound. They are editable assumptions, not universal runtime
cadence. All shots land on one target; Overcharge, targeting, deployment and
redeployment time are excluded. Inputs are unsaved view context. See
[SKILL_CADENCE.md](SKILL_CADENCE.md). The source RPM of 750 is not substituted for
observed timing, and other skills gain no automatic cadence outputs.

## Restorer duration source comparison (G329)

Restorer Hive exposes an optional, default-off comparison using the source's
additive variant-duration pool: installed Expertise and the fixed local
Polycarbonate Housing duration mod are added together, then multiplied by
all-skills Skill Duration with Skill Efficiency counted once. The comparison is
before Nanite, assumes a zero flat-seconds term, and does not change ordinary or
Overcharge duration. The nonnegative global-duration guard follows planner
convention and is not evidence from the source expression. Source inputs and
decimal cases are pinned in `data/sources/division-2-restorer-duration-source.json`
and validated by `scripts/check_restorer_duration_source.py`; the output is a
source comparison only and does not prove live runtime stacking.

Generated from Community Data Hub commit [`9c9ff2555243`](https://github.com/div2hub/game-data/tree/9c9ff25552439aabe9f995f4ed66897040ac0cc0).

## Attribution

Gear, weapon, attribute, set, talent, skill, and weapon-mod compilations are adapted from
[Division 2 Community Data Hub game data](https://github.com/div2hub/game-data), licensed under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/).
The Hub notes that its CC BY 4.0 license covers its compilation, not Ubisoft/Massive game content.
This project is an unofficial fan project and is not affiliated with or endorsed by Ubisoft.

Adaptations include normalizing source IDs to lower snake case, converting percentage strings to
decimal fractions, mapping item slot compatibility into planner choices, and translating simple
`stat:<id>:<value>` set and weapon-mod entries into stat modifiers.

Source percentages are percentage points, so they are summed as `flat` values into
percent-formatted stats (a 6% critical hit chance roll adds 0.06 to `critical_hit_chance`). The
source's stats that take both flat values and percentages (health, armor regeneration, armor on
kill, magazine size) measure different quantities in each form, so their percentages go to a
separate `<id>_percent` stat. Active skills carry their source skill family so the planner can
reject two variants of the same skill. The pinned `skills.csv` provides only variant names and
families, not base values or per-tier skill output tables.

## Imported snapshot

The pinned source lists 61 stats; the pack contains 291 stats
(including weapon fields, weapon-damage preview, and character baseline/total estimates), 98 selectable attributes,
500 armor pieces, 522 weapon-slot entries,
65 brand/gear sets, 373 talent catalog entries (238 with direct modeled effects; 83 linked to modeled gear-set effects),
252 weapon mods, 17 rolled gear mods, and 43 active-skill variants (43 with limited preview outputs).
It also includes 6 specializations and 260 purchasable node tiers.
Its `known_gaps.csv` contains 0 data rows. An empty upstream gap list does not
mean that all game mechanics or values have been independently verified.

## Skill damage estimates (G103)

The supplemental [cell snapshot](../sources/division-2-skill-damage.json) pins the supplied
`Division 2 Gear Spreadsheet.xlsx` (SHA-256
`40ac6006e2eaf8ecc462d0a4ac49449cef34263660ec6bd6d914b36e20a110ac`). Its Skill Info!AI34
labels the revision TU27 by IC3SO. Skill List columns D–J provide tier-zero bases and tier bonuses
for 18 variants / 25 separate damage channels.
This is a community reference, not an independently verified current-game data set.
See [SKILL_DAMAGE.md](../../docs/SKILL_DAMAGE.md) for equations, stacking assumptions, missing
EMP/Jammer damage, automatic Deflector armor-core count, and excluded mechanics. Older milestone
notes below about unconsumed skill bonuses describe the earlier implementations.

## Loadout inputs and shot estimates (G106–G108)

Resourceful adds one threshold credit to every represented set when legally selected on
NinjaBike Messenger Backpack, following [Ubisoft's Season 11 description](https://www.ubisoft.com/en-ca/game/the-division/news-updates/5CVCQT1nolOgAToGGYyBsw/season-11-reign-of-fire).
The credit cannot create an absent set or supply a missing set chest/backpack talent.
This is a separate engine trait (1 modeled talent),
in addition to the 238 direct stat talents and set-linked effects.

Frenzy and On Point now read Estimated Weapon Magazine Capacity, including modeled mods
and magazine bonuses. Deflector reads Configured Armor Cores plus Skill Tier, capped at six.
The three manual capacity/core inputs are removed; buff eligibility and stacks remain assumptions.
Frenzy's completed-interval rounding still requires current live-game verification.

Critical bodyshot, headshot and critical headshot estimates use the selected weapon's
regular damage estimate. Critical bonus is the community-reported innate 25% plus modeled
bonuses; headshot bonus is the pinned weapon's HSD plus modeled bonuses. Factors are
`1 + CHD`, `1 + HSD`, and `1 + CHD + HSD`, respectively. See the original community
[Weapon Damage 101 research](https://www.reddit.com/r/thedivision/comments/gi0uel/weapon_damage_101_bullet_damage/)
(2020, author marks valid for 2025). These conditional shot estimates exclude target
mitigation, range, procs, hit probability and DPS, and require current in-game verification.
Raw Critical Hit Damage and Headshot Damage bonus totals remain separate.

## Shield health (G161)

All four shields now expose estimated health capacity from reviewed TU27 cells.
Valid configured armor cores contribute to effective shield tier alongside Skill
Tier, floored/clamped to 0-6. The source's rounded Bulwark and other shield tier
tables are preserved exactly; they are not replaced by a linear approximation.
Combined Skill Health includes Skill Efficiency once. Shield-specific Health
adds to the tier pool before general Skill Health, an estimated stacking rule
requiring live verification. Player armor/health,
damage and repair bonuses do not scale shield capacity. Current health,
regeneration, damage mitigation, mods, Expertise and Overcharge remain outside
this estimate. Source baselines and stacking require live verification.

## Turret and drone health (G162)

All four turrets and five drones now expose estimated health capacity. The nine
reviewed source curves add 70% per tier through tier six; Striker Drone retains
its distinct lower base. Combined Skill Health includes Skill Efficiency once.
Armor cores raise shield tier only, not turret/drone tier. Capacity is separate
from player health/armor, damage, repair, resistance and current platform health.
No survival time, destruction, regeneration, mods, Expertise or Overcharge is
inferred. Source baselines and stacking need live verification.

## Hive and Firefly health (G163)

Five Hives and three Fireflies now expose estimated health capacity. The five
Hive curves use the exact 70%-per-tier source progression; Firefly tier cells
explicitly contain dashes and keep their constant base capacity before modeled
Skill Health and Skill Efficiency. Reviver's existing official charge preview
remains separate. No healing, revive events, charges consumed, survival time,
current health, regeneration, mods, Expertise or Overcharge is inferred.
Source baselines and stacking need live verification.

## Turret and drone duration (G164)

Seven reviewed platform duration references now consume Combined Skill Duration
(including Skill Efficiency once). Incinerator's exact tier-duration table is
retained; the other six rows explicitly contain dashes. Artillery and Bombardier
are excluded because no reviewed deployment-duration row was selected. Values
describe source duration capacity, not remaining timers, fuel use, health-based
survival, uptime or DPS. Skill mods, Expertise and Overcharge remain excluded.
Four-piece Nanite's permanent healing-duration reduction applies to Fixer Drone,
the only healing variant in this duration scope; it does not shorten other
platforms or gear-talent buff timers. Source behavior needs live verification.

## Skill charge and ammunition capacity (G165)

Four Hives, four Chem Launchers and two ammunition-based turrets now expose
estimated charge/ammunition capacity from reviewed additive tier tables. Values
are integral and tier is floored/clamped to 0-6. Existing Reviver charges remain
governed by the official rule and are not duplicated. Player Ammo Capacity,
Magazine Size, damage, Skill Efficiency and haste do not scale these quantities.
No current ammunition, consumption, replenishment, refill time, skill mods,
Expertise or Overcharge is inferred. Source baselines need live verification.

## Skill radius references (G166)

Ten reviewed blast, explosion or cloud radius references now follow their exact
tier tables. Artillery's tier-six jump and Explosive Sticky's non-linear tier
bonuses are preserved. Skill Efficiency, damage, duration, repair, weapon range
and blast damage bonuses do not scale radius. Labels retain the source's radius
meaning; they do not estimate area, range detection, hit probability, targets
hit or damage falloff. Healing ranges, unreviewed radius overrides, skill mods,
Expertise and Overcharge remain outside this scope. Source baselines need live
verification.

## Smart Cover deployment references (G167)

Both Smart Covers expose cooldown, duration, health and radius references from eight reviewed rows. General Skill Health and Duration include Skill Efficiency once; radius does not. These describe the device, not a claim that the agent is occupying its cover or receiving weapon handling, out-of-cover damage, armor or resistance buffs. Those contextual benefits remain unmodeled.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Decoy utility references (G168)

Decoy exposes cooldown, duration, health and a normalized threat reference. The threat table starts at 1 and uses its exact source tier percentages; player damage, armor, Skill Efficiency and status bonuses do not scale it. The multiplier does not predict enemy targeting, threat distance, aggro or survival time.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Pulse deployment and radius references (G169)

All five Pulses expose ordinary cooldown references; Achilles includes its reviewed tier Skill Haste in the additive haste pool. Scanner, Remote, Jammer and Banshee expose radius; Remote exposes deployment duration; Achilles exposes integral zone capacity, preserving zero increases at tiers one and two. No detection events, marked targets, zone-health depletion, debuffs, charge timing or target damage amplification is inferred.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Trap deployment capacities (G170)

All three traps expose deployment duration and integral trap count. Shock and Repair gain ordinary cooldown references; Shock gains radius. Shock count preserves its repeated tier-one/tier-two value. Repair Trap duration follows the existing Nanite healing-duration reduction, including the legal Improved Dampeners upgrade. Trap deployment, trigger events, shock duration, current traps and healing area remain outside these estimates.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Sticky Bomb utility references (G171)

All three Sticky Bombs expose ordinary cooldown and flare/deployment duration references. Burn and Explosive use their reviewed tier haste in the additive haste pool; EMP gains its own radius and tier-duration references. The unknown EMP disrupt-duration cell is excluded. Flare duration is separate from burn/disrupt duration, damage ticks, detonation events and actual readiness.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Hive deployment duration (G172)

All five Hives expose deployment-duration references from explicitly dashed tier cells. Restorer duration follows the existing Nanite healing-duration reduction with legal threshold and backpack-upgrade handling. Reviver deployment duration is separate from revive timing; Booster and Artificer deployment duration is separate from buff duration. No charge refill, lifetime repair or uptime is inferred.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Firefly targeting and speed references (G173)

All three Fireflies expose integral maximum-target capacity and normalized movement-speed references. Their source tables add one maximum target per tier and 35% speed per tier. These do not scale with player movement speed, magazine size, Skill Efficiency, damage or repair. Blinder gains its dashed-tier ordinary cooldown. Demolisher cooldown stays excluded because its tier cells are blank. Counts and normalized speed do not assert targets hit, travel time, blindness duration or damage total.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

## Seeker health and deployable capacities (G174)

All four Seeker Mines expose health references with explicitly dashed tier cells, before general Skill Health and Skill Efficiency. Cluster Mines and Bombardier Drone expose integral mine/bomb capacity from additive tier tables. Player ammo/magazine bonuses and Skill Efficiency do not increase deployable count. Mender duration remains excluded because adjacent source rows both carry Duration labels with different baselines; resolving their meaning requires further review. No actual mines deployed, bombs dropped, travel events or lifetime totals are inferred.

Reviewed TU27 community PvE source references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities and combat events are excluded.

The completed G167-G174 batch provides at least one preview for every one of the 43 skill variants, with 115 reviewed utility-property channels. This does not mean every skill property or combat mechanic is modeled: unknown EMP disrupt duration, blank cooldown tiers, ambiguous Mender duration rows, healing ranges, recipient effects and combat events remain excluded.

## Healing range (G175)

Four healing variants gain source range/radius references. Legal four-piece Nanite reduces these by 90%, or 25% with Improved Dampeners on the backpack. This does not affect damaging ranges, Artificer skill-support range, or deployment duration. Radius and Range retain their source labels; no target eligibility or healing events are inferred.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

## Hive range (G176)

The remaining four Hives gain range references with their exact tier curves. These are distinct from healing-skill range and do not receive Nanite reductions. Skill Efficiency, player weapon range, repair, damage and status bonuses do not increase these geometric references. No target acquisition or recipients are simulated.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

## Detection geometry (G177)

Incinerator range, Tactician scan range and Banshee cone angle are now separate geometric references. Tactician tiers add meters, not percentages. Banshee preserves the repeated tier-two/tier-three cone value. No area, detected enemies, charging-dependent shape, hits or player weapon-range increase is inferred.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

## Cloud duration (G178)

Four cloud-lifetime references consume Combined Skill Duration once. Reviewed healing clouds (Reinforcer and Mender) receive legal Nanite duration reduction. Cloud lifetime is distinct from status-effect duration, deployment lifetime, healing ticks and lifetime damage/repair. The two ambiguous Mender Duration rows remain excluded.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

## Control duration (G179)

Five control-effect duration references use source tier curves and Combined Status Effects (Skill Efficiency once), independently of generic Skill Duration. These estimate an unresisted PvE reference; enemy resistance, actual application, immunity, PvP diminishing returns and elapsed timers are excluded. Unknown EMP Sticky disrupt duration remains excluded.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

## Burn bleed duration (G180)

Three Burn and three Bleed duration references use source curves and Combined Status Effects. Configured Firewall Burn Duration joins the additive status-duration pool, a planner stacking interpretation requiring live verification. Legal Trauma Specialist supplies its separate bleed-only duration factor. White Hot extends its Burn Damage debuff, not the underlying Burn duration, and does not scale these references. No tick rate, lifetime damage, resistance, PvP or application events are inferred. Incinerator burn duration remains excluded because its tier cells are blank.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

Artillery stores a bare numeric Bleed Duration value; seconds are a planner interpretation requiring live verification, separate from any damage rate.

## Pulse effect timing (G181)

Scanner/Banshee source effect lifetime and Achilles zone lifetime use Combined Skill Duration, separately from status durations. Achilles zone health follows the source tier table only, without player or device Skill Health. Jammer charging time is an unscaled source reference, not Skill Haste cooldown. No charge progress, enemy zones depleted, detection, target damage amplification or actual cooldown timing is simulated.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

## Hive support reference (G182)

Booster and Artificer gain source-tier buff-amount references; Artificer also gains buff duration, skill-repair share and additive refresh-seconds references. These are source properties, not applied agent/recipient buffs or a general Skill Efficiency modifier. Generic damage, repair, duration, Skill Efficiency and Nanite do not alter these references. Booster buff duration remains excluded: the retained workbook reaches 10 seconds while Ubisoft TU20 notes state a 6.5-second maximum; this conflict needs live verification.

Reviewed TU27 community PvE references require live verification. Skill mods, skill Expertise, Overcharge, current timers/capacities, recipient effects and combat events remain excluded.

Conflict source: [Ubisoft Vanguard TU20 notes](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/4Q5mYbSvaH9H3YZtMiraHs).

The completed G175-G182 batch adds 36 reviewed source rows, bringing the utility cache to 151 channels across the 43 skill previews. Known gaps remain: charge recovery timing, skill mods/Expertise, Overcharge, contested Booster buff duration, blank Incinerator burn tiers, unknown EMP Sticky disrupt duration, ambiguous Mender deployment durations, recipient application, enemy resistance and combat events.

## Chem ammo recovery (G183)

Four Chem Launchers gain estimated single-ammo recovery seconds from reviewed Ammo Cooldown rows, divided by one plus Combined Skill Haste (Skill Efficiency once). This is separate from ordinary skill cooldown and maximum ammo capacity. Tier does not change the dashed source cooldown table. Nonpositive recovery speed yields a guarded zero estimate, not readiness. No elapsed timer, ammo consumption, full refill time, Nanite reduction, skill mods, Expertise or Overcharge is inferred. Base values and haste stacking require live-game verification.

## Hive refill baselines (G184)

All five Hives expose the retained source Refill Speed baseline in seconds. It stays unscaled: the relationship between refill speed, capacity, haste and Hive-specific talents needs review before estimating charge recovery. It is neither current cooldown nor full-empty refill time. Generic Skill Haste/Efficiency, tier, repair, duration and Nanite do not alter this source reference. No mods, Expertise, Overcharge or charge events; live verification required.

## Reviver capacity and armor (G185)

Reviver Hive gains integral charge capacity and separate source revive-armor base share and tier-bonus references. The source does not establish whether the tier percentages are additive share points or multiplicative repair bonuses, so no combined revive-armor share is calculated. Repair Skills, Skill Efficiency, player armor, Nanite and incoming repair do not change these source references. No revival, restored armor points, self-revive charge consumption or recipient caps are inferred. Source interpretation requires live verification. Mods, Expertise and Overcharge remain excluded.

## Shield ordinary cooldowns (G186)

All four Shields gain the reviewed 20-second ordinary cooldown baseline, adjusted by Combined Skill Haste once. Their dashed tier rows do not add cooldown haste, including tiers from armor cores. This is separate from active/holstered regeneration, redeployment delays, current shield health and destruction events. No actual readiness, skill mods, Expertise or Overcharge is inferred; live verification required.

## Foam health and shield buff geometry (G187)

Riot Foam gains a source foam-health reference with its exact tier curve, separate from device health. Striker Shield gains source buff angle and range references. Player/device Skill Health, Skill Efficiency, damage, repair, duration and weapon range do not alter these source properties. No foam breaking, enemies inside the cone, recipient buffs, damage amplification, mods, Expertise or Overcharge is inferred; live verification required.

## Ordinary skill cooldown seconds (G160)

Ten reviewed ordinary cooldown baselines now consume combined Skill Haste, using
the inverse-speed estimate (100% haste halves the base). Tier entries explicitly
contain dashes. Sniper Turret and Demolisher blank tier cells, Sticky Bomb tier
haste pooling, charge refill and shield regeneration are excluded. Ordinary
cooldown is independent of Rigger's hypothetical cancellation reset capability.
No actual readiness, elapsed timer, destruction or deployment delay is inferred.
The auditable `division-2-skill-properties.json` cache is checked by
`scripts/cache_division2_skill_properties.py`. Baselines need live verification.
See Ubisoft's [Episode 1 notes](https://www.ubisoft.com/en-ca/game/the-division/the-division-2/news-updates/3pepSvWluJRxzkWZeEWv56/episode-1-patch-notes)
for the inverse-speed rule; these are dated notes, not verification of TU27 bases.

## Healing-skill references (G159)

Five healing variants add six PvE output channels: Repair Trap, Restorer Hive,
Reinforcer Chem Launcher (initial ally repair and overtime), Mender Seeker Mine,
and Fixer Drone. The pinned workbook cell cache is
`data/sources/division-2-skill-healing.json`; its fingerprint and retained cells
are checked by `scripts/cache_division2_skill_healing.py`. Reinforcer overtime
uses the explicitly reviewed vertical tier merges anchored in row 165; blanks
are never treated as zero. Tier is floored/clamped to 0-6. Repair Skills and Skill
Efficiency add once, followed by the separate Total Skill Repair factor.

Only Mender's source explicitly states HP/s. Fixer and Reinforcer overtime keep
unit-unspecified reference labels; no per-second/tick or lifetime total is
invented. Per-trap/charge and initial-ally interpretations, base values and repair
stacking require live verification. No recipient Incoming Repairs, missing-armor
cap, timing, application events, duration, skill mods, skill Expertise or Overcharge
is inferred. Damage/Status Effects pools do not scale healing. Existing current
buff assumptions apply to both slots; saved-build formats are unchanged.

## Conditional skill previews and Rigger cancellation (G158)

Pack-defined preview outputs may include an optional `when` formula. Both engines
validate and evaluate it after final stats resolve; zero omits the row, errors and
non-finite results emit preview diagnostics. Inputs include both formula and
condition dependencies. Ordinary stat resolution and saved-build fields do not
change. Existing unconditional outputs retain their behavior.

At four Rigger pieces with selected Complete Uptime on its legal backpack, four
turret variants and Striker Drone show available zero cooldown after hypothetical
eligible manual cancellation. The pinned talent says cancelling resets cooldown.
The conservative platform allowlist excludes charge-based and other unverified
skills; it is not a claim that every other skill is ineligible. The row does not
assert an actual cancellation, current readiness, health state, deployment delay,
destruction event or animation. No ordinary cooldown factor, damage or skill buff
changes. Best Buds is independent. Removing the talent or threshold removes the
row. NinjaBike cannot retain the removed backpack talent.

## Ember Engine eligible Burn damage (G157)

At four pieces, `combustion_initial_burn_debuff=true` explicitly asserts the
Spontaneous Combustion debuff on a target whose initially applied status was Burn.
The four modeled skill Burn channels receive an estimated separate 1.25 factor.
This agrees with the pinned +25% rule and the official
[Red Horizon launch announcement](https://www.ubisoft.com/ko-kr/game/the-division/the-division-2/news-updates/4mrYiFPIyKpzpoqshDQk80/the-division-2-red-horizon).
The separate stacking pool needs live verification. Initial hits, explosions,
bleed, cloud damage, weapons and global Skill Damage/Status Effects do not change.
Flashpoint changes only proc chance; White Hot changes only available debuff
duration. Generic burning/status flags, proc chance, timers, ticks and uptime do
not establish eligibility. Clearing the assumption or removing the threshold
removes the factor; the assumption remains outside saved builds. PvP uses other
values and is outside this PvE model.

## Trauma Specialist skill bleed (G156)

Selected Trauma Specialist on the legal Ongoing Directive backpack automatically
doubles the three modeled selected-skill bleed channels (Artillery Turret, Stinger
Hive and Explosive Seeker Mine) at four pieces. No activation assumption is needed: the pinned
description increases all bleed damage. The separate bleed-only factor is an
estimate requiring live stacking verification. It does not change Status Effects,
Skill Damage, initial hits, Burn, global skill amplification, weapons, or cooldown.
Its 50% bleed-duration property remains available-only because no duration baseline
is modeled. Hollow-Point Ammo activation is independent; bleed ticks, ammo procs
and lifetime damage are not simulated. Removing the selected talent, replacing its
host, or dropping below threshold removes this factor. NinjaBike cannot supply the
removed backpack talent even when it grants piece credit.

## Hotshot eligible headshots (G155)

At four pieces, `headache_empowered_headshot=true` explicitly asserts an eligible
next Headache shot. Only a selected Marksman Rifle receives an estimated 1.80
headshot-only multiplier, after additive headshot/critical bonuses. Bodyshots,
bodyshot DPS, raw Headshot Damage, and skill damage remain unchanged. Stand Tall
and Lucky do not change damage. No cycle, kill, refill, miss or eligibility is
inferred; the view assumption is outside saved builds. NinjaBike credit may reach
the threshold but does not grant either removed set upgrade.

The pinned catalog's 80% agrees with page 4 of Ubisoft's
[Y8S3 PTS gear sheet](https://canopy.ubisoft.com/AssetLink/4cqi3868j141y5038mjy0sd8581j4il7.pdf),
linked from the official Red Horizon PTS announcement. Neither source specifies
the stacking pool. A separate headshot multiplier is an explicitly estimated
planner interpretation requiring live verification, rather than adding 80 points
of general Headshot Damage or treating the bonus as verified amplification.

## Cavalier retained charge estimate (G154)

Four effective pieces expose explicit retained qualifying charge seconds (0–14).
Protection uses 5% per eligible second, or 10% with selected Safe Charging, capped
at 50% or 70% with selected Overcharging. The standalone skill-damage factor is
one minus this protection; it does not alter general resistance or outgoing skills.
Full-charge time from zero is 10/14/5/7 seconds for base/chest/backpack/both, assuming
uninterrupted eligibility. This is independent of supplied retained progress and
does not start a timer or predict remaining time. Combat, cover, charge consumption,
movement immunity, ally sharing, pauses, uptime and incoming hits remain unmodeled.
Rules are guarded against the pinned catalog and match the original
[Broken Wings announcement](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/2ds4tuZY2fS5x4qSzfmqjX/year-5-season-1-broken-wings).
Current live behavior still needs verification. The input is threshold-relevant and
outside saved builds.

## Aegis Stoic resistance estimate (G153)

Four effective Aegis pieces expose explicit player targets, additional Decoy targets
with equipped selected Deceit, and group agents including yourself. The estimate
uses pinned 4%/5% per target, selected Polyethylene Plating, and the source's 1.X
group multiplier, interpreted as 1.1–1.4 for an ordinary one-to-four-agent group.
Combined targets are limited to eight as a conservative planner bound; this is not
a verified live cap. Targeting, active Decoys and group state are never inferred.
The result contributes to raw Damage Resistance; incoming hits and actual protection
stacking are not simulated. Solo counting and stacking require live verification.
The original [Vanguard announcement](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/4Q5mYbSvaH9H3YZtMiraHs)
documents the group and Decoy rules but predates the pinned 4%/5% balance values.
Assumptions remain outside saved builds and are hidden without the relevant threshold
or, for Decoy targets, selected Deceit.

## Core Exercise conversion (G152)

Four effective Core Strength pieces automatically add cross-core Weapon Damage,
Armor and Skill Efficiency, at 40% or 75% with equipped selected Inner Core.
Only valid configured gear cores count, including off-set items and triple-core
backpacks. Set/talent/specialization skill tiers and converted bonuses never count.
Standard level-40 bases are 15% Weapon Damage, 170,000 Armor and 15% Skill
Efficiency per relevant core. This is a planner interpretation of the official
Rise Up rule: partial valid rolls count once rather than scaling the conversion.
Exact live behavior on partial rolls and Prototype gear remains unverified.
No extra Skill Tiers, context inputs or saved-build fields are introduced.

## Core Strength backpack cores (G151)

The pinned backpack row only contains one generic core. A guarded importer override
adds fixed Weapon Damage, Armor and Skill Tier slots, following Ubisoft's
[Rise Up announcement](https://www.ubisoft.com/de-de/game/the-division/news-updates/15OjYSjEhS2rUfiDINkRxD/the-division-2-rise-up).
All three cores require configured valid rolls and contribute without selecting Outer Core
or reaching four pieces. Existing non-red core_1 rolls must be reconfigured into their
matching new slot. Minor, talent, mod and Expertise slots remain intact. This milestone
does not yet apply Core Exercise cross-core conversions.

## Hotshot armor capacities (G150)

Four effective Hotshot pieces automatically expose the unadjusted available armor
repair budget for an eligible Headache branch: 10% of estimated maximum armor.
Valid core rolls, Watch and Expertise update this planner estimate; neither fixed
upgrade changes its share. No MMR headshot cycle, actual repair or scaling is inferred.

The threshold-relevant Headache Current Armor Pool input supplies the current armor
value for its full-armor bonus-armor ceiling. Capacity is 50% of that explicit pool,
or 100% with equipped selected Stand Tall. Lucky changes only miss allowance.
The input defaults to zero and is not populated from estimated maximum armor;
the 10,000,000 upper bound is a planner bound, not a game cap. A supplied pool
does not assert full armor, headshot eligibility or conversion. Capacity is not a
per-trigger bonus-armor grant. Neither estimate changes actual armor, regeneration,
weapons or skills. Context stays outside saved builds/share codes.

The armor shares match Ubisoft's historical
[Reign of Fire notes](https://www.ubisoft.com/en-ca/game/the-division/news-updates/5CVCQT1nolOgAToGGYyBsw/season-11-reign-of-fire).
The maximum-armor repair basis is a planner interpretation; the pinned catalog,
baseline and actual behavior retain their current live-game verification limits.
Pack: 218 stats, 28 thresholds, 83 linked talents, 102 selector keys, 57 numeric inputs.

## Umbra Shadow repair estimates (G149)

With four effective Umbra Initiative pieces, an explicit Shadow Stacks to Consume
input supplies one continuous-cover budget estimate. The count is capped at 50,
or 100 with equipped selected Into the Light. Each stack supplies an unadjusted
0.8% of estimated maximum armor; delivery time is the capped count divided by
10 stacks/s. Average rate is budget divided by that duration, zero at no stacks.
Into the Light changes capacity and gain, not consumption or per-stack share;
From the Shadows changes the separate Light damage branch.

No actual retained stacks, cover eligibility, elapsed time, tick schedule, missing
armor, Repair Skills/incoming healing scaling, overlaps or healing is inferred.
No asserted stacks means zero budget/time/rate. Passive full stack capacity stays
available independently. The assumption hides below the effective threshold and
stays outside saved builds/share codes. Pack: 216 stats, 28 thresholds, 83 linked
talents, 102 selector keys and 56 numeric inputs. Pinned catalog interpretation and
armor baseline remain subject to current in-game verification.

## Available armor budgets (G148)

Four effective System Corruption pieces automatically expose Hackstep's available
bonus-armor amount per use: 50% of estimated maximum armor, or 100% with equipped
selected Multithreaded Execution. Compiler Optimization changes cooldown only.

Four effective Umbra Initiative pieces automatically expose an unadjusted Shadow
repair budget per stack (0.8% of estimated maximum armor) and full stack repair
capacity (40%, or 80% with equipped selected Into the Light). From the Shadows
changes the separate Light branch; neither upgrade changes the per-stack share.

These capacities track valid armor cores, Watch and Expertise. No current stacks,
ability use, bonus armor, actual healing, missing-armor caps, cover states, scaling,
timers or weapon assumptions are inferred. No new inputs or saved fields. The
calculations use the pinned catalog's existing guarded properties; source values
and the maximum-armor baseline retain their live-game verification limits.
Pack: 213 stats, 28 thresholds, 83 linked talents, 102 context keys, 55 numeric inputs.

## Passive armor repair capacities (G147)

Four effective Refactor pieces and equipped selected Over-engineered expose the
available self-only bonus-armor ceiling: 80% of estimated maximum armor. This
property applies automatically, independently of full armor or asserted damage.
It does not perform conversion, grant bonus armor, or predict current retained armor.

Four effective System Corruption pieces expose the available unadjusted self repair
budget per Hackstep use: 20% of estimated maximum armor. Compiler Optimization
changes cooldown and Multithreaded Execution changes the bonus-armor grant; neither
changes this share. The available budget does not use the separate damage assumption.

Both estimates track modeled maximum armor, including valid core rolls, Watch and
Expertise. Neither adds regeneration or actual healing, applies Repair Skills or
Incoming Repairs, caps by missing armor, infers ability events, or activates buffs.
No new inputs or saved fields. Pack: 210 stats, 28 thresholds, 83 linked talents,
102 context keys and 55 numeric inputs. Refactor's ceiling matches the official
[Burden of Truth notes](https://www.ubisoft.com/en-us/game/the-division/news-updates/5V9sBiXBdAwdb9Migm4zZW/year-6-season-3-burden-of-truth);
Hackstep's repair share matches the
[Vanguard notes](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/4Q5mYbSvaH9H3YZtMiraHs).
Estimated maximum armor and actual live-game behavior remain separately
subject to the existing baseline/source verification limits.

## Support repair budget estimates (G146)

Four effective Refactor pieces expose explicitly supplied eligible skill damage
and unadjusted PvE self/per-ally repair budgets: 10%/20%, or the pinned catalog
25%/35% with equipped selected Increased Interest. Skill preview damage does not
populate the input; actual hits, damage mitigation and recipient state are not inferred.
Ubisoft's [Burden of Truth notes](https://www.ubisoft.com/en-us/game/the-division/news-updates/5V9sBiXBdAwdb9Migm4zZW/year-6-season-3-burden-of-truth)
list the upgrade's PvE/PvP labels opposite to the pinned catalog. This batch preserves
the guarded catalog values; live-game verification of that discrepancy remains open.

Four effective Future Initiative pieces expose explicitly supplied eligible ally
repair and the unadjusted proximity budget for each eligible recipient: 60%, or
120% with selected Advanced Combat Tactics. Tactical Superiority and full armor
still control damage separately; they do not activate or scale these repair budgets.
Neither estimate applies Repair Skills/incoming healing again, caps by missing
armor, splits by party size, infers range/recipients, simulates healing, or feeds
back into itself. Over-engineered capacity/conversion remains separate. Zero input
asserts no event; both inputs hide below threshold and remain outside saved builds.
The 0–10,000,000 input bounds are planner limits. Pack: 208 stats, 28 thresholds,
83 linked talents, 102 context keys and 55 numeric inputs.

## Makeshift repair budget estimates (G145)

Four effective Foundry Bulwark pieces expose a manual eligible damage input and
an unadjusted repair budget for each recipient (agent and shield): 25% of that
damage, raised to 35% by equipped selected Process Refinery. The average delivery
rate divides the budget by ten seconds, reduced to five with selected Improved
Materials. Neither upgrade changes the other upgrade's property. The budget is
not split between recipients and is not inferred from incoming damage, maximum
armor, a skill output or a combat event. Zero is inactive; the input hides below
threshold. The 0–10,000,000 damage bound is a planner limit, not a game cap.
No Repair Skills/incoming healing scaling, missing-armor cap, overlaps, ticks or
actual recovery are applied. Armor Regeneration and skill outputs are unchanged.
The input remains outside saved builds/share codes. Pack: 205 stats, 28 thresholds,
83 linked talents, 102 context keys and 53 numeric inputs.
The source fractions and durations match [Ubisoft's Vanguard notes](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/4Q5mYbSvaH9H3YZtMiraHs).

## Hackstep weapon estimates (G144)

Four effective System Corruption pieces enable an explicit qualifying bonus-armor
percentage assumption. Each complete 5% adds 2% Total Weapon Damage, capped at
20%. The estimate joins the existing additive Total Weapon Damage category before
weapon shot/DPS calculations; it is not an independent amplifier. Zero is inactive.
The property-only armor grant and its selected upgrades do not populate the input.
No armor source, gain/consumption, ability use, timer, repair or skill damage is inferred.
The input hides below the set threshold and remains a view assumption outside saved
builds/share codes. Pack: 203 stats, 28 thresholds, 83 linked talents, 102 context
keys and 52 numeric inputs. The complete-interval estimate needs live-game checking.
The 2%-per-5%, 20% cap matches [Ubisoft's Vanguard notes](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/4Q5mYbSvaH9H3YZtMiraHs).

## Heatstroke weapon estimates (G143)

Selected Heatstroke with four effective Ortiz: Exuro pieces applies a separate
1.40 weapon amplifier only while `exuro_target_ignited_by_prototype=true` asserts
that this target was ignited by your prototype turret. Generic Burn/status flags
are insufficient. Shot and DPS estimates consume the amplifier once; skill
damage, turret range, status application and uptime are not inferred. Its
assumption appears only at the threshold with the legal backpack talent selected.
Passive 40% weapon and 25% range properties remain independent of the target
flag. Pack: 203 stats, 28 thresholds, 83 linked talents, 102 context keys and 51
numeric inputs. Saved fields are unchanged; current in-game verification is open.

## Symptom Aggravator damage estimates (G142)

With four effective Eclipse Protocol pieces and selected Symptom Aggravator,
`target_status_affected=true` applies a separate 1.30 weapon and skill amplifier.
Weapon shot/DPS estimates and all 25 sourced skill damage channels consume it
once. Other bonuses multiply separately; no status application, spread, target
detection, proc uptime or healing is inferred. Assumptions appear only with the
selected legal backpack host at the four-piece threshold. Counts and saved-build
fields are unchanged. Exact source wording/hosts remain guarded; current in-game
verification is open.

## Remaining set and upgrade properties (G141)

Adds 14 passive properties for Aegis, Core Strength, Ortiz: Exuro, Symptom
Aggravator, Trauma Specialist and Complete Uptime. Every one of the 28 catalog
gear sets now has at least partial four-piece coverage: 203 stats and 83 linked
talents. No new assumptions, inputs or saved fields. Outer Core remains
descriptive because its source states no direct change. Actual cross-core
bonuses, group-scaled Stoic resistance, eligible damage, bleed ticks, turret
events and cancellation resets remain deferred. Pinned descriptions, four-piece
references and upgrade hosts fail closed on source drift; live-game verification
is open.

## Four combat-set property previews (G140)

Cavalier, Hotshot, System Corruption and Ember Engine add four guarded four-piece
thresholds and 17 passive properties. Charging exposes gain (5%/10% per qualifying
second), cap (50%/70%) and 10s sharing duration. Headache exposes its 80% next-headshot
property, 10% armor repair, 50%/100% current-armor-based bonus cap and Lucky's one-miss
allowance. Hackstep exposes 50%/100% available bonus armor, 5s armor/nameplate durations,
20% repair, 20s/15s cooldown and 2% Total Weapon Damage per qualifying 5% bonus armor
gained, capped at 20%. Combustion exposes 40%/60% proc chance, 25% initially-Burn damage
property and White Hot's 50% debuff-duration bonus. The latter is not global Burn Duration.
Four effective pieces and correct selected fixed hosts apply properties automatically.
No charge progress, mitigation, headshot cycle, actual armor/repair, ability use,
damage bonus, status application, random procs or timers are activated or inferred.
Existing global stats and damaging-skill outputs are unchanged. Exact wording,
thresholds and hosts are guarded; current in-game verification is open. 189 stats,
25 modeled set thresholds, 72 set-linked talents, 101 context keys, 51 numeric inputs;
no new assumptions or saved-build/share fields.

## Four additional support-set previews (G139)

Foundry Bulwark, Eclipse Protocol, Refactor and Ortiz: Reficere now have guarded
four-piece previews with 13 passive properties. Makeshift Repairs exposes its
25%/35% damage-based repair share and 10s/5s delivery duration. Indirect Transmission
exposes 10m/15m spread radius and 50%/75% refresh; Symptom Aggravator damage remains
descriptive. Return to Sender exposes PvE self/ally repair shares (10%/20%, or
25%/35% with Increased Interest) and selected Over-engineered's self-only 80%
bonus-armor cap. Nanite properties cover healing-skill Duration/Range reductions
(90%, or 25% with Improved Dampeners), Healing Efficiency bonus (150%, or 225%
with Overcharged Nanites) and the ally Hazard Protection grant (30%, 10s, 10s
per-ally cooldown). These properties apply automatically with four pieces and
selected valid hosts; below threshold zero means unavailable. They do not
alter global Repair Skills, Skill Efficiency/Duration, self Hazard Protection,
actual bonus armor or damage/skill outputs. No damage/healing events, spread,
recipient eligibility, status timers, armor conversion or PvP are simulated.
Source wording/thresholds/hosts are pinned; current in-game verification is open.
172 stats, 21 modeled set thresholds, 60 set-linked talents, 101 context keys and
51 numeric assumptions. No new assumptions or saved-build/share fields.

## Set timing and stack properties (G138)

Four-piece passive properties now cover Umbra Initiative, Measured Assembly,
Rigger and Tip of the Spear. Umbra exposes both branch caps and gain/consumption
rates, normal/sprint Light decay and 0.8% armor repair per consumed Shadow stack.
From the Shadows upgrades Light cap/gain/consumption (50/10/10 to 100/20/20);
Into the Light upgrades Shadow cap/gain (50/10 to 100/20), not consumption.
The pinned chest consumption wording is preserved separately from movement decay.
Huddle exposes 4-second trigger, 15-second duration, 40-second Overcharge cooldown
(25 with Hivemind) and 10-second destruction base cooldown (1 with Smart Cooperation).
The unspecified ally-speed aggregation is not chosen. Rigger's duration is 10s;
Aggressive Recon's ammo interval is 60s. These properties need no active flags and
do not scale with Skill Haste/Duration/Efficiency. Exact source wording and hosts
are guarded. No repair events, progression, Overcharge, destruction, cancellation
resets, ammunition or timers are simulated. Current in-game verification is open.
159 stats, 49 set-linked talents, 101 context keys and 51 numeric assumptions;
saved-build/share fields unchanged.

## Dead Man's Hand available shot counts (G137)

Four active Aces & Eights pieces show the per-hand enhanced-shot counts: Four
of a Kind 4, Full House 3, Aces and Eights 2. Equipped selected Ace in the Sleeve
adds one to each (5/4/3). Below four pieces, all are zero/unavailable. Exact
wording and fixed backpack host are guarded. These are passive counts, not a
current hand, remaining shots or activation. No card flips, hand detection,
hits, consumption or ammunition are inferred. Existing empowered-shot flag,
selected Rifle/MMR and No Limit damage strength remain separate. Current count
verification is open. No inputs or saved fields: 144 stats, 46 set-linked talents,
101 context keys and 51 numeric assumptions.

## Ground Control proximity repair property (G136)

Four active Future Initiative pieces show the proximity-repair fraction: 60% of
an eligible ally repair, or 120% with equipped selected Advanced Combat Tactics.
Zero below four pieces means unavailable. Exact wording and fixed backpack host
are guarded. This passive share property is independent of full armor and
Tactical Superiority's damage branch; it is not a Repair Skills bonus or an
actual healing amount. No source repair, missing armor, five-meter recipients,
events, range, feedback or repeated healing is inferred. Current fraction
verification is open. No new inputs or saved fields: 141 stats, 45 set-linked
talents, 101 context keys and 51 numeric assumptions.

## Camaraderie mark capacity (G135)

Four active Concentrated Company pieces show the available mark limit: four,
or eight with equipped selected All for One. Below four pieces, zero means
unavailable. Exact wording and the fixed chest host are guarded. This passive
capacity does not create marks, infer shots/kills, count allies or skill
contributions, or generate/decay stacks. The retained Camaraderie stack count,
Weapon Damage/CHD bonuses and selected One for All strength remain unchanged.
Current capacity verification is open. No new inputs or saved fields: 140 stats,
44 set-linked talents, 101 context keys and 51 numeric assumptions.

## Crowd Control mark and transfer properties (G134)

Four active Negotiator's Dilemma pieces show passive mark limit (3, or 5 with
equipped selected Target Rich Environment) and per-other-marked-enemy transfer
percentage (60%, or 112% with equipped selected Critical Measures). Below four
pieces both are zero/unavailable. Exact wording and fixed hosts are guarded.
Neither upgrade modifies the retained CHD stack bonus or player damage outputs.
The transfer property is not player amplification or a multi-target total;
no actual marks, critical hits, recipients, mitigation, damage amounts, expiry
or combat events are inferred. Current capacity/transfer verification is open.
No new inputs or saved fields: 139 stats, 43 set-linked talents, 101 context keys
and 51 numeric assumptions.

## On Point duration property (G133)

Four active Breaking Point pieces show the available On Point buff duration:
20 seconds, or 40 with equipped selected Point of no Return. Zero below four
pieces means unavailable. Exact wording and its fixed chest host are guarded.
This passive duration does not activate the buff, count stacks or simulate hits,
reloads, timers, ammo refills, expiry or weapon switching. The existing active
flag, Rifle/MMR ownership and stack count still gate damage/handling separately;
Point of Honor strength is unchanged. Current timing verification remains open.
No new inputs or saved fields: 137 stats, 41 set-linked talents, 101 context keys
and 51 numeric assumptions.

## Apex Predator duration and radius properties (G132)

Four active Hunter's Fury pieces show passive kill-stack duration (10 seconds,
30 with equipped selected Endless Hunger) and disorient radius (5 meters,
10 with equipped selected Overwhelming Force). Zero below four pieces means
unavailable. Exact descriptions and fixed chest/backpack hosts are guarded.
These properties do not activate stacks or disorient, change the separate
15-meter proximity damage assumption, or infer kills, range, timer or uptime.
The ambiguous post-kill damage pool remains omitted. Current timing/radius
verification is open. No inputs or saved fields: 136 stats, 40 set-linked talents,
101 context keys and 51 numeric assumptions.

## Feedback Loop cooldown property (G131)

Four active Hard Wired pieces automatically show the available Feedback Loop
trigger cooldown: ten seconds, or five with equipped selected Short Circuit.
Zero below four pieces means unavailable. This passive property is separate
from actual skill cooldowns, haste and efficiency; no current countdown, trigger
readiness, skill use/cancellation, reset or 30-second reduction is inferred.
The existing active flag still independently asserts the damage/repair buff,
and Positive Reinforcement changes only its strength. Exact source wording and
fixed backpack host are guarded; current timing verification remains open.
No new inputs or saved fields: 134 stats, 38 set-linked talents, 101 context keys
and 51 numeric assumptions.

## Symphony trigger threshold (G130)

Four active Virtuoso pieces automatically expose the simultaneous-buff trigger
threshold: four stacks, or three with equipped selected Accelerando. Zero below
four pieces means unavailable. This passive requirement does not count current
stacks, infer kills, generate progress or activate a buff. The existing range
selector independently asserts within/beyond/both. Fortissimo and all effect
strengths are unchanged. Exact Accelerando wording and its fixed Virtuoso
backpack host are guarded; current in-game verification remains open. No new
inputs or saved fields: 133 stats, 37 set-linked talents, 101 context keys and
51 numeric assumptions.

## Virtuoso simultaneous Symphony (G129)

`symphony_range_buff=both` asserts the current simultaneous 15-second buff with
four active Virtuoso pieces. Both ordinary range groups are multiplied by 1.5:
60% Weapon Damage for the seven listed classes, 45% Headshot Damage and 37.5%
Bonus Armor. AR/LMG combine their two 20% branches before scaling. Selected
Fortissimo doubles only Weapon Damage to 120%; the gear-wide headshot and armor
bonuses stay unchanged. The single selector makes within/beyond/both exclusive.
Weapon Damage remains owner-scoped; None selected retains raw headshot/armor,
without weapon damage. No stack count, kill, range, trigger, timer, uptime or
Accelerando trigger inference occurs. Pinned wording/hosts remain guarded;
current game verification is open. Counts, numeric inputs and codecs unchanged.

## True Patriot rotation interval (G128)

The four-piece threshold automatically exposes the available Red/White/Blue
rotation interval: two seconds, or one with equipped selected Waving the Flag.
Zero below the threshold means unavailable. This is a passive timing property,
not time remaining; no debuff, rotation phase, hits or uptime are activated.
Patriotic Boost and the three existing target/ready assumptions remain separate.
Exact Waving the Flag wording and its fixed True Patriot chest host are guarded.
Current in-game timing verification remains open. No saved fields or new inputs:
132 stats, 36 set-linked talents, 101 context keys, 51 numeric assumptions.

## True Patriot Blue enemy factor (G127)

Four active True Patriot pieces expose the Blue enemy damage reduction only with
`true_patriot_blue_target=true`, asserting that the evaluated enemy has Blue.
The pinned base reduction is 10%, raised to 20% by equipped selected Patriotic
Boost. Defense shows the retained outgoing-damage factor (0.9 or 0.8); the factor
is 1 when inactive or below the threshold. Red/White gates and weapon preview are
independent. General Incoming Damage Multiplier, armor, healing and outgoing
player damage remain unchanged. This source-specific debuff is not global armor
or mitigation; no incoming damage amount, all-enemy debuff, application, rotation,
timer, sharing or Full Flag explosion is inferred. The raw reduction stays
separate, and the factor guards it to 0–100%. Exact descriptions and fixed hosts
remain pinned; current in-game verification is open. No build/share changes:
131 stats, 101 context keys including weapon slot, 51 numeric assumptions.

## True Patriot White repair (G126)

The active four-piece True Patriot threshold adds its self White repair fraction
only while `true_patriot_white_ready=true` asserts an eligible hit on a target
with White and the once-per-second repair available. Base repair is 2% of estimated
maximum armor, raised to 5% by equipped selected Patriotic Boost. Defense shows
the separate potential per-hit amount, not a regeneration rate. The hit flag is
independent of Red and the selected weapon; below four pieces both outputs return
zero and the relevant White control is hidden. Fractions, raw maximum armor and Red damage
remain separate; Incoming Repairs, Repair Skills and Skill Efficiency are excluded.
The pinned set/upgrade descriptions and fixed hosts remain guarded. No ally
sharing, missing-armor cap, hits, cooldown, debuff rotation, Blue damage reduction
or Full Flag explosion is inferred. Current in-game verification remains open.
No build/share fields change: 129 stats, 100 context keys including weapon_slot,
and 51 numeric assumptions.

## Clutch post-kill window (G125)

Selected Clutch and Perfect Clutch show their available post-kill repair window:
`min(10, 4 + max(0, floor(weapon_core_count)))` seconds. Counts come from valid
configured red gear cores; invalid/unconfigured rolls, Expertise, weapon cores
on weapons and ordinary damage bonuses do not add time. The window is a passive
talent property, independent of current event assumptions and selected weapon.
It does not activate Clutch, count down, infer kills or change repair amounts.
The pinned talent wording supplies the base, per-core increment and cap; exact
descriptions and fixed hosts remain guarded. In-game verification remains open.
No new assumptions or build/share fields; 127 stats, 51 numeric assumptions.

## Clutch repair amount (G124)

Selected Clutch and Perfect Clutch add Estimated Clutch Armor Repair / Crit:
2.5% of estimated maximum armor times the explicitly supplied missing-armor
fraction. `clutch_missing_armor_percent` is a transient 0–100 percentage-point
input (step 1, default 0) visible only for an equipped selected Clutch talent.
The existing `clutch_repair_state` separately asserts eligibility; the numeric
input does not activate the talent or infer armor thresholds or timers.
Each event mode is exclusive, so the amount is never doubled. Talent provenance
shows the amount's source. Incoming Repairs, Repair Skills and Skill Efficiency
are excluded. No repeated critical hits, repair sequence, red-core timer scaling
or actual current armor is inferred. Raw fractions remain unchanged.
The catalog source wording and fixed host checks remain pinned. This is an amount
conversion pending current in-game verification, not a new game-rule assertion.
No build fields or share codecs change; 126 stats and 51 numeric assumptions.

## Current pack summary (G123)

The in-app description is now a short summary of supported configuration and
current estimates. Historical implementation notes stay in this provenance and
the support documentation. Removed obsolete claims about unavailable skill
damage, manual armor-core counts and unconsumed magazine/handling bonuses.
This is a metadata-only change: formulas, modifiers, catalog entries, profiles,
context inputs and codecs remain unchanged (125 stats, 207 direct talents).

## Bullet King no-reload preview (G122)

Bullet Hell now marks its selected owning weapon as requiring no reload. The
reload-cycle bodyshot DPS output uses the existing firing DPS in this case,
without a magazine/reload penalty. This permanent property applies automatically
when the fixed talent is selected. Clearing it returns to the conventional branch;
a zero reload time without the modeled talent remains unavailable (zero).
Other weapons, raw card values, per-shot estimates and magazine reference values
are unchanged. Ammunition availability, 100-hit replenishment, allies, reserve
capacity, firing interruptions and encounter uptime are not modeled. The pinned
catalog's exact wording, N/A compatibility and Bullet King hosts are guarded;
current live-game behavior and the damage baseline remain unverified. The pack
has 125 stats and 207 directly modeled talents; context and codecs are unchanged.

## Mantis headshot amplification (G121)

In Plain Sight contributes a separate 50% headshot amplification only for the
selected owning Mantis while mantis_target_not_targeting_you=true asserts the
source target condition. Regular and critical headshot estimates multiply by
this factor after their additive shot bonuses. Bodyshots, average bodyshots,
bodyshot DPS, raw Headshot Damage and skill estimates remain unchanged. The flag
appears only for an equipped selected talent and is never saved. Weakpoint
outputs, scoped highlighting, target detection and Decoy resets are excluded.
The exact pinned description, compatibility and Mantis hosts are guarded. The
historical [Ubisoft TU10 developer notes, archived by Dev Tracker](https://devtrackers.gg/the-division/p/7f398b76-title-update-10-patch-notes-updated-2-00-pm-cest-june-18)
corroborate the amplification; current in-game verification remains needed.
The pack has 124 stats, 206 directly modeled talents, 99 context keys including
weapon selection and unchanged 50 numeric assumptions; codecs are unchanged.

## Relative damaging-skill cooldowns (G120)

Fourteen damaging Turret, Drone, Firefly, Seeker Mine, Sticky Bomb and Trap
variants show the existing inverse-speed cooldown factor from Combined Skill
Haste: 1 / (1 + haste), or zero for a nonpositive speed factor. Skill Efficiency
is counted once through the combined stat. No absolute seconds, tier cooldown
scaling, animation floors, resets, uptime or Overcharge are inferred. Hive and
Chem Launcher charge regeneration and Shield recovery remain excluded. This is
an extension of the historical Ubisoft Episode 1 Skill Haste rule (2019), not
current per-variant timing verification. Damage outputs, 19 profiled variants,
123 stats, context keys and saved-build fields remain unchanged.

## Estimated weapon optimal range (G119)

The Weapon summary exposes selected base optimal range in meters multiplied by
one plus modeled additive range bonuses. Both factors floor at zero; a missing
selected base yields zero. Outsider's current buff and Near Sighted's penalty
retain selected-weapon ownership, as do the existing permanent talent/mod effects.
Base card range and raw percentage bonuses remain separate. No damage falloff,
maximum range, accuracy, target distance or Ranger eligibility is inferred.
Pinned catalog baselines, named-item values and additive scaling still need
current in-game verification. No context inputs or saved-build fields change.

## Talent repair amounts (G118)

The Defense summary shows potential armor repaired per eligible depletion trigger,
per eligible headshot, and the total five-second repair budget per eligible
Preservation kill. Existing raw maximum-armor fractions multiply estimated
maximum armor; both factors floor at zero. Existing ready/kill-type assumptions
and selected-weapon ownership are preserved. Immediate Armor on Kill stays
separate from delayed Preservation repair. No missing armor, trigger generation,
cooldowns, ticks, overlapping repairs or buff uptime are inferred. Clutch remains
a missing-armor percentage because the planner does not track current armor.
Incoming Repairs, Repair Skills and Skill Efficiency are excluded; baselines
and conversion still require current in-game verification. See docs/RECOVERY.md.
No context inputs or saved-build fields change.

## Defensive recovery estimates (G117)

The Defense summary converts existing raw bonuses into potential self amounts:
bonus armor = estimated maximum armor times asserted bonus-armor fraction;
armor regen/second = flat regen plus estimated maximum armor times percent regen;
armor restored/kill = flat on-kill armor plus estimated maximum armor times percent;
health restored/kill = estimated maximum health times percent on-kill health.
Nonpositive maximum pools cannot produce percentage recovery; final amounts clamp
at zero. Raw values and maximum armor/health remain separate and unchanged.
Permanent set effects apply automatically; existing temporary assumptions remain
explicit. No current/missing armor or health, kill frequency, uptime, damage
interruptions, ally sharing, delayed talent repair or bonus-armor conversion is
inferred. Incoming Repairs, Repair Skills and Skill Efficiency are not added.
The pinned catalog defines the bonuses; TU11 developer notes describe percentage
armor recovery, Memento and defensive brand bonuses. Baseline precision and
additive conversion still require current in-game verification. See docs/RECOVERY.md.
No context inputs or saved-build fields change.

## Combined weapon handling components (G116)

Combined Accuracy, Stability and Swap Speed join existing Combined Reload Speed
in the weapon summary. Each adds Weapon Handling once to its direct bonus total,
using Ubisoft's TU10 1:1 component rule documented in the archived developer notes:
https://devtrackers.gg/the-division/p/7f398b76-title-update-10-patch-notes-updated-2-00-pm-cest-june-18
Selected-weapon ownership and existing current-buff assumptions are retained.
Raw bonuses remain separate; damage estimates do not change. These totals do not
infer spread, recoil, hit probability, swap seconds or swap-trigger generation.
No context inputs or saved-build fields change; current in-game verification remains open.

## Combined skill bonuses (G115)

The Skill summary now exposes Skill Efficiency's six component totals. Combined
damage, haste, repair, duration, health and status effects each add raw bonuses
and Efficiency once. Skill damage channels consume combined damage/status pools:
direct damage uses damage, bleed uses status, burn adds both (Efficiency contributes
to each distinct component). This preserves existing damage results and separate
tier/total-skill/amplifier factors. The extra totals do not invent absolute healing,
skill health, duration or status lifetime. No assumptions or saved formats change.
The same archived Ubisoft TU11 definition cited below supplies the component rule.

## Memento short-term core bonuses (G114)

Kill Confirmed adds its short-term branch behind memento_short_buff_active=true:
5% Weapon Damage per configured red core, 10% Bonus Armor per configured blue
core and 5% Skill Efficiency per configured yellow core. Counts come from valid
configured gear core attributes, including multiple fixed cores on exotic gear.
Unconfigured/invalid rolls, Expertise and non-core tier/damage/armor bonuses do
not count. The branch is gear-wide and coexists with long-term trophy stacks;
collection, refresh, timers and core-based trigger generation are not simulated.
The pinned description matches Ubisoft's [TU11 developer patch notes, archived
by Dev Tracker](https://devtrackers.gg/the-division/p/eaec3e1d-the-division-2-title-update-11-patch-notes).
Current in-game verification remains open. No saved-build format changes.

## Automatic Capacitor tier (G113)

Capacitance now reads final loadout Skill Tier, floored and clamped to 0–6, for
its 7.5% additive Weapon Damage per tier. Gear cores, set credits and active
specialization/talent tier bonuses feed the same resolved total. Only the selected
owning Capacitor contributes. The manual capacitor_skill_tier input is removed;
capacitance_stacks remains a view assumption. No Overcharge or tier above six is
inferred. Saved builds and share formats are unchanged. Exact wording remains
guarded against the pinned catalog.

## Combined skill haste (G112)

Combined Skill Haste adds raw Skill Haste and Skill Efficiency once. The existing
Assault Turret and Striker Drone relative cooldown factors now use this total.
Nonpositive speed factors return zero without a division error. Raw totals remain
separate; damage formulas already include Efficiency and are unchanged. No base
cooldowns, per-skill tier scaling, mods, Expertise, resets or Overcharge are inferred.
Ubisoft's [TU11 patch notes, archived by Dev Tracker](https://devtrackers.gg/the-division/p/eaec3e1d-the-division-2-title-update-11-patch-notes)
state that each percentage point of Efficiency adds one point of Skill Haste.
This is a dated developer mechanic; current in-game verification remains open.

## Conventional magazine/reload cycle (G111)

Magazine damage is average bodyshot damage times estimated capacity. Firing
duration is capacity * 60 / estimated RPM, using N cadence intervals for the
repeating cycle rather than first-to-last-shot elapsed time. Reload-cycle DPS
divides magazine damage by firing duration plus one estimated reload. Positive
capacity, cadence and reload time are required; unavailable inputs return zero.
Buffs stay fixed throughout the cycle. Refills, infinite magazines, reload skips,
per-shell timing, changing ammo bonuses, phases, trigger/burst limits, misses,
mitigation, range and proc uptime are excluded. This is a conventional reference
cycle, not a simulation of encounter DPS or weapon-specific refill mechanics.

## Bodyshot DPS while firing (G110)

Average bodyshot damage multiplied by estimated RPM / 60 converts the current
buff snapshot to damage per second while firing. This is arithmetic on existing
community/catalog estimates, not a new measured combat baseline. Raw values and
share codes remain unchanged. Reload downtime, trigger/burst caps, phase changes,
misses, target mitigation, range and proc uptime are excluded; the estimate does
not simulate sustained encounter DPS. No extra pellet multiplier is inferred.

## Shotgun pellets (G414)

Pellets per Shot is 8 for shotguns and 1 for other weapons. Shade-Tech's damage
trace found `myBulletsPerRound 8` in every non-named player shotgun report (nine
families) and in the Lullaby exotic, and reproduced each catalog damage as eight
half-unit-floored pellets, so the listed damage is the total of all eight. R26
confirmed 8 for all 43 mapped shotguns, named and exotic included; The Ravenous, an
exotic rifle, fires 2 bullets a round, which the planner doesn't model yet.
Practical shotgun damage keeps the per-shot expectation with the hit and headshot
assumptions read as shares of pellets; the combat simulator rolls each pellet.

## Effective critical chance and average bodyshots (G109)

Effective Critical Hit Chance bounds the raw modeled total to 0–60%, following the
same original [community damage research](https://www.reddit.com/r/thedivision/comments/gi0uel/weapon_damage_101_bullet_damage/).
Estimated Average Bodyshot Damage weights regular and critical bodyshots by this
probability. Raw totals remain unchanged, including negative Boiling Point early-phase
values and late-phase values above the cap. This is a long-run estimate for eligible
landed bodyshots under the current view assumptions; it does not simulate random shots,
talent proc generation or uptime. Headshots, target mitigation, range, misses and DPS
remain excluded. Shot-specific talent assumptions must match the bodyshots being examined.

## Deliberate limits

- This is a catalog and context-gated stat preview, not a full combat simulator.
- Estimated Total Armor combines equipped-slot base armor and flat armor bonuses, then applies
  Total Armor percentage bonuses. Rounded community slot values total 660,000 for six pieces;
  empty slots add no base armor. Some references use 660,014 for the full loadout, so this is an
  estimate rather than an exact in-game total. Estimated Total Health combines the community-reported
  270,000 level-40 base and flat health, then applies Health (%) bonuses. SHD Watch ranks and capped
  automatic SHD health are saved inputs. Installed item Expertise grades increase only the
  selected weapon's additive damage or each gear piece's base armor; blue cores are untouched.
  Normalization, seasonal effects, and unmodeled talents remain excluded. See
  [Watch and Expertise research](../../docs/WATCH_EXPERTISE.md) and [baseline research](../../docs/BASELINES.md)
  for sources and precision. These values have not been independently measured in the live game.
- Talent descriptions are preserved. 238 entries have direct modeled effects from
  exact pinned wording: Glass Cannon and Perfect Glass Cannon expose weapon-damage and raw
  incoming-damage multipliers; Vigilance and Perfect Vigilance contribute total weapon damage
  when the caller supplies `vigilance_active=true`; Braced and Perfect Braced add weapon handling
  when `in_cover=true`; Composure and Perfect Composure contribute total weapon damage when
  `in_cover=true`; Companion and Perfect Companion contribute it when `companion_active=true`;
  Wicked and Perfect Wicked contribute it when `wicked_active=true`; Flatline and Perfect Flatline
  amplify weapon damage when `target_pulsed=true`; Sadist variants use `target_bleeding=true`,
  Eyeless variants use `target_blinded=true`, Ignited variants use `target_burning=true`, and
  Spotter variants use `target_pulsed=true` for the weapon preview; their skill-damage amplification
  is not modeled. Optimist variants use a continuous linear `ammo_missing_percent` assumption;
  100% represents only a theoretical empty-magazine boundary. Killer variants use
  `killer_active=true` as an assumed manually triggered critical-hit kill buff within the source's
  10-second window. Fast Hands variants use an assumed 0-40 stack count and add reload-speed
  percentage only. In Sync variants use explicit manual weapon-hit and non-shield skill-trigger
  assumptions within the stated 5s window; each trigger grants its own flat damage-category bonus,
  and both bonuses are doubled when both assumptions are active. This adds a Skill Damage stat bonus
  only; no absolute skill damage is calculated. Close & Personal variants use a manual active-buff
  assumption for a kill within 7m and its 10s window. Breadbasket variants use an assumed 0-3 current
  stack count (Perfect caps at 2) and add only the next-headshot Headshot Damage stat bonus; body-hit
  generation, consumption, timers, and headshot events are not simulated. Current numeric values for
  the modeled values are pinned from the Community Data Hub snapshot and have not been corroborated
  by current official Ubisoft sources; verify them in the live game. Ubisoft's May 2022
  [TU15 notes](https://www.ubisoft.com/en-au/game/the-division/the-division-2/news-updates/21eVYKAAC5FS0WzeNSOpAE/title-update-15-patch-notes)
  document that shield skills do not trigger In Sync, but are dated and provide no current PvE values.
  Ubisoft's June 2023
  [Year 5 Season 1 notes](https://www.ubisoft.com/en-us/game/the-division/news-updates/2ds4tuZY2fS5x4qSzfmqjX/year-5-season-1-broken-wings)
  describe a Breadbasket nerf for Descent, which is not evidence for PvE values.
  Spike and Perfect Spike use `spike_active=true` to add Skill Damage bonus totals after an assumed
  headshot; Reformation variants use `reformation_active=true` after an assumed headshot to add
  Repair Skills bonus totals. Future Perfect and Future Perfection use an assumed 0-4 current kill-stack
  count (ordinary Future Perfect caps at 3) to add Skill Tier; selected skill previews cap effective
  Skill Tier at 6. These inputs do not simulate headshots, kills, buff timers, Overcharge, or final
  skill damage/repair. All six entries use the pinned source wording and amounts; the modeled values
  need current live-game verification. Ubisoft's
  [Year 5 Season 3 notes](https://www.ubisoft.com/en-us/game/the-division/the-division-2/news-updates/4Q5mYbSvaH9H3YZtMiraHs)
  document Future Perfection's +1 tier per kill, four stacks, 19-second duration, and Overcharge trigger.
  They do not verify current Future Perfect, Spike, or Reformation amounts. Ubisoft's [TU3 notes](https://www.ubisoft.com/en-gb/game/the-division/news-updates/NofHu3ioiObtuB66Gh7L8/title-update-3-pts-phase-2-begins-today)
  record historical Spike and Reformation behavior/values; those old values are not treated as current
  corroboration. Current Spike and Reformation values still need live-game verification. Companion distance,
  status-effect timing, disable timers, kill-triggered status application, target state, proc uptime,
  incoming damage, mitigation,
  and Braced's other weapon-stat subeffects are not simulated. Glass Cannon outgoing amplification
  is applied only to the weapon preview. All weapon-talent effects remain selected-weapon gated;
  In Sync adds a Skill Damage bonus total without calculating skill damage. Bonus totals are distinct
  from preview calculations. Remaining unmodeled weapon-talent skill effects (including Spotter's
  amplification) remain descriptive. Three numeric
  assumptions represent current Obliterate (0-24), Unstoppable
  Force (0-5), and Strained (0-8) stacks; ordinary talent variants cap these at 20, 5, and 5.
  Critical-hit and kill-triggered stack generation, grenade stack events, firing time, and talent
  timers are not simulated. Strained and Killer add Critical Hit Damage as a stat only; the weapon
  preview does not apply critical damage. Fast Hands adds reload speed as a stat only; no reload
  time or DPS is calculated. None of the three limited skill profiles consumes Skill Damage, so
  In Sync's bonus does not change current skill preview outputs. Exact source wording and values
  are allowlisted, so changes fail import pending review. Measured and Perfect Measured use an
  explicit `measured_phase=top/bottom` assumption. Measured's top half applies a Weapon Damage
  penalty, while its bottom half applies a Total Weapon Damage bonus; Perfect Measured uses the
  same categories with different amounts. Rate-of-fire effects add only to the `rate_of_fire`
  bonus stat and are not applied to Weapon Rate of Fire, magazine depletion, damage-per-second, or
  firing simulation. Steady Handed variants use an assumed current pre-consumption stack count from
  0-99: snapshots are valid at 0-99 for Steady Handed and 0-74 for Perfect Steady Handed. At 100
  ordinary or 75 perfect stacks, the talent consumes stacks to refill the magazine, so the Perfect
  bonus is withheld at 75-99 instead of displaying a persistent post-consumption cap. Accuracy
  and Stability bonus totals are shown; hit generation, threshold consumption, magazine refills,
  and timers are not simulated. Ubisoft's March 2026
  [10th Anniversary notes](https://www.ubisoft.com/en-us/game/the-division/the-division-2/news-updates/1myuMJ5wGJFqIy2ic0mYnp/celebrating-10-years-of-the-division)
  document Perfect Measured's top/bottom values and Gear Shift host; they do not verify standard
  Measured or Steady Handed. Remaining values and mechanics use the pinned Community Data Hub
  snapshot and need current live-game verification.
- Combined Arms and Perfect Combined Arms use `combined_arms_active=true` for an assumed current
  3-second shooting buff; Tech Support variants use `tech_support_active=true` for an assumed
  current 25/27-second skill-kill buff. They add 25/30% or 25% to a separate Total Skill Damage
  bonus stat. Kinetic Momentum variants use independent current-stack assumptions for Skill 1 and
  Skill 2 (0-18 each); each stack adds 1.5% Total Skill Damage and 2% Total Skill Repair. Ordinary
  Kinetic Momentum caps each skill at 15 stacks; Perfect caps each at 18. These gear effects are
  independent of weapon selection. Controls are view assumptions outside saved builds, history,
  and share codes. Skill triggers, combat state inference, timers, cooldowns, and stack generation
  are not simulated. Total Skill Damage and Total Skill Repair remain separate from Skill Damage
  and Repair Skills and are not consumed by current selected-skill previews; no absolute skill
  damage or repair is calculated. The pinned Community Data Hub supplies the exact Combined Arms
  and Tech Support wording and values, which still need live-game verification. Ubisoft's March
  2026 [10th Anniversary notes](https://www.ubisoft.com/en-au/game/the-division/the-division-2/news-updates/1myuMJ5wGJFqIy2ic0mYnp/celebrating-10-years-of-the-division)
  corroborate Perfect Kinetic Momentum's per-skill 18-stack cap, 1.5% damage and 2% repair per
  stack, cooldown loss, and Impetus host; ordinary Kinetic Momentum and Combined Arms/Tech Support
  current values are not independently corroborated here.
- Overwatch and Perfect Overwatch use `overwatch_active=true` for the assumed valid timed in-cover
  buff, adding 12% or 14% to Total Weapon Damage and Total Skill Damage. Safeguard and Perfect
  Safeguard use `full_armor=true` to add 130% or 160% Total Skill Repair. These booleans represent
  current effect validity; cover timing and armor inference are not simulated. Only the equipped
  agent's bonus is modeled; buffs received by other agents are outside the pack.
  The controls are view assumptions outside saved builds, history, and share codes. These four
  effects do not add absolute skill outputs or change current selected-skill previews. Values and
  wording are pinned from the Community Data Hub. Ubisoft's [Into the Dark PTS notes](https://www.ubisoft.com/en-us/game/the-division/the-division-2/news-updates/7MbnFYZZj4a5pgeUlrMOY6/into-the-dark-pts-patch-notes)
  historically corroborate Perfect Safeguard's 160% full-armor bonus; current live-game values
  still need verification. Overwatch and ordinary Safeguard values are not independently
  corroborated here.
- Rifleman and Perfect Rifleman use an assumed current `rifleman_stacks` count (0-6; ordinary
  Rifleman caps at 5) and add 10% or 11% per stack to Weapon Damage. Headshot generation, the
  5-second duration refresh, and shot events are not simulated. Pummel and Perfect Pummel use
  `pummel_active=true` for a manually assumed post-proc buff after the source's 3 or 2 consecutive
  kills; each adds 40% Weapon Damage. Kill counting, magazine refill, the 10-second timer, and
  buff transfer are not simulated. All four effects are gated to the selected weapon. Their exact
  wording and values are pinned from the Community Data Hub; no current official Ubisoft source
  corroborating these values was found.
- Unhinged and Perfect Unhinged add 18% or 22% Weapon Damage with -25% Stability and -25% Accuracy
  as passive selected-weapon stats. Naked and Perfect Naked use `naked_active=true` for the assumed
  valid post-hit buff and add 50% Headshot Damage for 5 or 8 seconds. The flag represents a current
  buff after hitting an enemy with no armor; target armor is not inferred, and hit events, buff
  duration, and headshot damage calculation are not simulated. These effects are gated to the
  selected weapon. Exact wording and values are pinned from the Community Data Hub; current live-game
  verification remains open.
- Gunslinger and Perfect Gunslinger use `gunslinger_active=true`; Spark and Perfect Spark use
  `spark_active=true`; Shock and Awe variants use `shock_and_awe_active=true`. Concussion variants
  use independent headshot and kill buff assumptions. Focus and Perfect Focus use an assumed 0-10
  `focus_stacks` count for qualifying 8x-or-higher scoped aiming, capped at ten. These are gear
  effects whose validity is manually asserted;
  skill damage and repair remain bonus totals without absolute outputs.
  Trigger events, timers, scope/aim state, and buff validity are not inferred. Exact pinned source
  wording is allowlisted; current live-game verification remains open.
- G40 adds eight weapon talent pairs. Vindictive variants use `vindictive_active=true` and add 16% or 21% Critical Hit Chance and Critical Hit Damage as uncapped selected-weapon bonus totals; these do not calculate critical results or DPS. The listed effects apply to the equipped agent only; Vindictive's allies-within-range transfer is not modeled. On Empty and Unwavering use explicit `on_empty_active=true` and `unwavering_active=true` assumptions for 60/80% and 300/400% Weapon Handling. Streamline and Soft Spot use `streamline_active=true` and `soft_spot_active=true` for 42/47% and 19/24% Weapon Damage. Streamline's no-skills-deployed-or-on-cooldown condition is asserted manually. Overwhelm, Pumped Up, and Stabilize use current retained stack inputs (0-4, 0-25, and 0-75); ordinary/perfect caps are 4/4, 25/5, and 60/75. Stack generation, current-state eligibility, resets, decay, reloads, and procs are not simulated. Accuracy, Stability, Critical Hit Chance, Critical Hit Damage, and Weapon Handling are bonus totals only; no critical cap, hit accuracy, handling, critical damage, or DPS simulation is applied. All weapon effects remain isolated to the selected weapon by the shared resolution context. Exact source descriptions and values are pinned from the Community Data Hub; they are source numbers, not claims of current live-game behavior, and need in-game verification.
- G41 adds five weapon talent pairs—Ranger, Pressure Point, Behind You, Outsider, and Near Sighted—for 10 additional direct modeled entries (104 of 373 catalog talents have direct modeled effects). Ranger and Perfect Ranger use an assumed `ranger_target_distance_m` distance from 0 to 100m, adding 2% weapon damage per completed 4m or 3m interval respectively; the 100m limit is a planner input bound, and the completed-interval floor interpretation and live behavior remain pending in-game validation. Pressure Point variants use `target_status_affected=true`; Behind You variants use `target_not_targeting_player=true`; Outsider variants use `outsider_active=true` for the assumed 10-second post-kill buff. Near Sighted variants passively add Stability and subtract 35% Optimal Range. Optimal Range is modeled as a percentage bonus total, distinct from each weapon's Weapon Optimal Range distance in meters; effective range, damage falloff, Accuracy, and Stability behavior are not simulated. All effects are gated to the selected weapon. Perfect variants retain catalog hosts: Archivist/Ekim's Long Stick, Goalie/Rusty, Whisper/Brutus, Safety Distance, and Glory Daze respectively. Finisher and Perfect Finisher remain descriptive because their buff transfers across weapons and the selected-weapon context does not model that transfer. Exact source descriptions and values are pinned from the Community Data Hub and require current live-game verification.
- G45 adds six weapon/gear talent pairs (12 additional directly modeled entries from the 373-entry talent catalog). Thunder Strike, Immobilize, and Head Scratcher add 30%/35%, 20%/25%, and 30%/35% weapon-damage multipliers against manually asserted shocked, ensnared, and confused targets; status application after kills is not modeled. Brazen adds 3.5% multiplier per manually supplied prior-shot pellet hit only when the next-shot assumption is active and the count meets the source threshold (6 ordinary, 4 Perfect). Its 0–100 pellet bound is a planner input bound, not a sourced shotgun pellet maximum. Frenzy and Perfect Frenzy use manually supplied magazine capacity and completed 10/8-round intervals to add 3% per interval to Weapon Damage and Rate of Fire while `frenzy_active=true` asserts the 9-second post-empty-reload buff; interval rounding is a planner interpretation pending live verification, final magazine capacity and reload events are not simulated. Intimidate adds 4% weapon-damage multiplier per assumed retained stack (ordinary cap 9, Perfect cap 10) only while `intimidate_active=true` asserts valid bonus armor and a target within 10m. Bonus-armor creation, stack accrual/loss, target distance, timers, and status/shot events are not inferred. Exact source compatibility and fixed hosts are pinned; Perfect versions are restricted to their catalog items. Weapon effects remain gated to the selected weapon. The damage multiplier represents only the selected weapon preview and does not simulate status effects or proc generation; all source values require current live-game verification.
- G46 adds Overclock, Versatile, and Back and Forth with their Perfect variants, bringing direct modeled effects to 122 of 373 entries. `overclock_active=true` manually asserts the source's proximity-to-deployed-skill eligibility. Overclock contributes 25% Reload Speed and Perfect Overclock 30%; cooldown reduction is excluded because it is expressed as elapsed seconds and active skill cooldowns are not modeled. Perfect Overclock's pinned description says `within 15` without a unit, so the planner does not infer or calculate a radius. `versatile_active=true` asserts a qualifying swap between different primary and secondary weapons, the 10-second active window, target range band, and source's once-per-5-seconds-per-weapon-type restriction. The selected weapon class gates the weapon-damage amplification: shotguns/SMGs and rifles/MMRs receive 35% ordinary or 45% Perfect in their stated range bands; assault rifles/LMGs receive 10% or 20% in their stated band; no bonus is modeled for pistols. Range is not calculated. `back_and_forth_swap_to_active=true` asserts the 10-second buff after swapping to the selected weapon and contributes 9%/12% Weapon Damage and 10%/13% Rate of Fire. Its swap-from bonus is omitted because the active buff transfers from the talent-bearing weapon after it is no longer selected. Reload Speed and Rate of Fire remain bonus totals; no reload time, RPM, firing, or DPS is calculated. Perfect Overclock is fixed to Hermano, Perfect Versatile to Vigil, and Perfect Back and Forth to Trader and Interchange; ordinary talents use their generic source compatibility. All exact descriptions/hosts are pinned from the Community Data Hub snapshot and remain unverified against current live-game behavior.
- G42 added 4pc previews for Striker's Battlegear, Future Initiative, Hard Wired, and Tipping Scales, linking four base talents and six fixed upgrades. Striker's Gamble uses an assumed 0–200 retained-stack count, capped at 100 without selected Press the Advantage or 200 with it, and preview-amplifies the selected weapon; hits and stack decay are not simulated. Ground Control adds 15% Total Weapon Damage and Total Skill Damage while `full_armor=true`, increased to 25% by selected Tactical Superiority. Feedback Loop adds assumed-active 10% Total Skill Damage and Total Skill Repair, increased to 25% by selected Positive Reinforcement; cooldown reduction and timers are not modeled. Throttle Control uses an assumed 0–75 stack count, capped at 50 without selected Sustainability and 75 with it, adding Weapon Handling and Critical Hit Damage per stack; selected Snowball raises the Critical Hit Damage per stack. Shooting, decay, and suppression are not simulated.
- G43 adds six 4pc previews—Heartbreaker, Ongoing Directive, Aces & Eights, Concentrated Company, Negotiator's Dilemma, and Umbra Initiative—and links 6 base talents plus 5 fixed upgrade talents. `has_talent()` upgrades apply only while the fixed host is equipped and that talent remains selected. Heartstopper uses 0–100 assumed retained stacks, capped at 50 or 100 with selected Max BPM, and adds 1.1% amplified weapon damage per stack only with `target_pulsed=true`; bonus armor, hits, and decay are excluded. Rules of Engagement assumes active Hollow-Point Ammo through `hollow_point_ammo_active=true` for 40% weapon damage amplification, raised to 60% with selected Parabellum Rounds; marks, ammo grants, bleed, and party transfer are excluded. Dead Man's Hand assumes an empowered shot and gates its 75% or 100% bonus to the selected Rifle/Marksman Rifle; card flips and shot counts are excluded. Camaraderie uses an assumed 0–35 stack count for Weapon Damage and Critical Hit Damage, with selected One for All raising Weapon Damage per stack from 3% to 6%; ally contributions and decay are excluded. Crowd Control contributes only 10% Critical Hit Damage per assumed stack (0–10); marking and damage transfer are excluded. Umbra models only Into the Light's 1.2% Critical Hit Damage and 0.4% Rate of Fire per assumed stack (0–100), capped at 50 or 100 with selected From the Shadows. `umbra_light_active=true` asserts current buff eligibility and that shooting from cover is not suppressing it; no `in_cover` assumption is required. Armor regeneration, stack timers, and generation/decay are excluded.
- Across G42 and G43, 10 thresholds and 21 linked talents were modeled. G44 adds five 4pc previews—Rigger, Tip of the Spear, Breaking Point, Measured Assembly, and Hunter's Fury—and links five base talents plus four supported upgrades. Tend and Befriend exposes an assumed-active 25% Skill Damage bonus, raised to 50% by selected Best Buds; the flag describes the interacted skill's buff, not both skills, and no absolute skill output is calculated. Aggressive Recon exposes 15% Signature Weapon Damage during its manually asserted kill buff and 25% Reload Speed for its manually asserted next reload; selected Specialized Destruction raises only Signature Weapon Damage to 30%. Selected Signature Moves also exposes a 50% weapon-damage multiplier while `signature_moves_active=true` asserts its 15-second buff after fully depleting Signature Weapon ammo. Ammo generation and doubled ammo generation are not modeled. On Point exposes 2% Weapon Handling and 4% Weapon Damage per assumed stack, capped by manually supplied magazine capacity and active only for a currently valid post-reload Rifle/Marksman Rifle state; selected Point of Honor raises Weapon Damage per stack to 9%. Both its stack count and magazine capacity have 0–200 planner bounds; final magazine capacity is not calculated. Huddle adds one Skill Tier per manually supplied qualifying ally count (0–7 planner bound, not a source maximum); the existing selected-skill previews clamp effective tier to 0–6. Apex Predator exposes only its 20% amplified weapon damage against a manually asserted target within 15m. Its 5% kill stacks are excluded because source wording does not specify how they pool with proximity amplification. Fixed upgrades that only alter timers, ammo, cooldowns, radius, or disorient remain descriptive. Across G42–G44, 15 thresholds and 30 linked talents were modeled; direct talent effects remain separate at 172 of 373. No skill damage/repair outputs, ally sharing, bonus armor, or other gear-set effects are calculated. Exact source descriptions, 4pc references, and fixed hosts are pinned from the Community Data Hub and require current live-game verification.
- G54 adds two gear-set 4pc previews, bringing coverage to 17 thresholds and 34 linked talents while direct talent effects remain at 172 of 373. Two new selectors bring the context controls to 80 selectors and 47 numeric inputs. Virtuoso models only the ordinary `symphony_range_buff=within|beyond` group: Weapon Damage bonuses apply to the listed selected weapon classes, Fortissimo on the selected Virtuoso chest doubles those bonuses, and the within-range Headshot Damage bonus is a raw total. The simultaneous four-stack 1.5x group and Bonus Armor are omitted. True Patriot models only Red through `true_patriot_red_active=true`, adding 15% weapon-damage amplification against the assumed current target, or 30% with the selected Patriotic Boost backpack. White healing, Blue damage reduction, Full Flag, timing-only Accelerando and Waving the Flag, stack generation, timers, and hit timing are not modeled. Exact source wording and hosts are pinned; values need current live-game verification.
- G55 adds eight gear-talent previews, bringing direct coverage to 180 of 373 and current controls to 83 selectors and 49 numeric inputs. Protected Reload and Perfect Protected Reload use `protected_reload_active=true` for 20%/40% self bonus armor while reloading; ally effects are omitted, including Perfect Protected Reload's inconsistent source claim of 0–30% for allies despite a stated 18% maximum. Bloodsucker uses 0–10 assumed retained stacks for 10%/12% Bonus Armor per stack. Adrenaline Rush uses 0–3 assumed stacks for 20%/23% each; `adrenaline_rush_active=true` asserts a valid current 5-second bonus from a qualifying within-10m trigger, without inferring current distance. Protector and Perfect Protector use `protector_active=true` for 5%/25% self Bonus Armor from the current 3-second shield-damage buff. Bonus Armor (%) is the raw sum of asserted self bonus fractions as a percentage of the agent's armor; absolute bonus armor and protection are not calculated. The stat does not change estimated armor, health, weapon previews, or DPS, and does not automatically activate Intimidate or other assumptions. These gear effects have no selected-weapon-owner gate. Kills, reload detection, proximity measurement, shield hits, buff generation/refresh/decay, durations, and cooldowns are omitted. Exact descriptions, compatibility, and fixed named hosts (Sleight in Urban Lookout, Liquid Engineer in Belstone Armory, Matador in Walker, Harris & Co., and Keeper in 5.11 Tactical) are guarded; values remain pinned-source references pending live-game verification.
- G56 adds seven direct gear-talent previews, bringing current direct coverage to 187 of 373, and expands linked gear-set talents to 35. Five new selectors bring current controls to 88 selectors and 49 numeric inputs. Leadership variants use `leadership_buff=normal|close` for 15%/20% or doubled/tripled 30%/60% self Bonus Armor; Galvanize variants use `galvanize_active=true` for 40%/50%; Mad Bomber variants use `mad_bomber_aiming=true` for 15%; and Parkour! uses `parkour_active=true` for 25%. These assert the currently valid 10-second or 5-second self buff after the source trigger; ally sharing and automatic weapon reload are excluded. Chemical Agent Reactant uses `reactant_active=true` for 25% after a qualifying status-affected kill at max Catalysis. Its trigger is not continuous eligibility: the flag does not require current Catalysis to remain at 12 or the current target to stay status affected. Virtuoso adds 25% Bonus Armor for its ordinary beyond-range buff with no weapon-class gate. Heartbreaker adds 1% per retained stack, doubled by selected Cold, without requiring `target_pulsed=true`; Cold does not affect the existing pulse-gated weapon damage. Memento remains omitted. Effects stay raw self Bonus Armor (%), without absolute armor/protection or output coupling. Exact descriptions, compatibility, and fixed named hosts (Cap'n, Lavoisier, Carpenter, and NinjaBike Messenger Kneepads) are guarded; pinned values need current live-game verification.
- G57 adds eight direct armor-repair talent previews, bringing current coverage to 195 of 373 while preserving 17 modeled set thresholds and 35 linked talents. Four new selectors bring current controls to 92 selectors and 49 numeric inputs. Unbreakable uses `unbreakable_ready=true`; Entrench uses `entrench_ready=true`; Clutch uses mutually exclusive `clutch_repair_state=low_armor|post_kill`; Preservation uses `preservation_kill_type=body|headshot`. The repair stats keep separate denominators: maximum armor per depletion proc, maximum armor per eligible headshot, missing armor per eligible critical hit, and a per-kill budget over five seconds. Headshot Preservation totals explicitly include the additional headshot amount. No state inference, red-core scaling, trigger generation, cooldown/timer/kill/critical-hit simulation, repair-output coupling, swap retention, overlapping Preservation procs, ticks, or caps are modeled. Exact descriptions, compatibility, and fixed hosts (Zero F's, Bober, Devil's Due, Emeline's Guard, and P320 XCompact) are guarded; values remain pinned-source references pending live-game verification.
- G63 adds nine direct previews (Boiling Point and Perfect Boiling Point, Blossom Harvest, Shakedown, Pack Instincts, Rebalance, Ostracize, Challenger, Impervious), bringing coverage to 204 of 373 with 17 set thresholds and 35 linked talents; controls become 97 selectors and 53 numeric inputs. Magazine phase, target remaining armor+health, Shakedown stacks and shot, Pack Instincts range, Rebalance Red/Blue stacks, unmarked targets, and enemies within 20m are explicit view assumptions. Critical Hit Chance, Damage Resistance, and Hazard Protection are raw totals; ally sharing, post-cap bonuses, weakpoints, and marked-enemy incoming damage are omitted. Exact descriptions, compatibility, and fixed hosts (Steamer, Teapot, First Bloom, Steel & Sons ACR, Coyote's Mask, Harrier Pride, Blacklisters, Provocator, and Nurse's Kneepads) are guarded; values remain pinned-source references pending live-game verification.
- G64 adds derived-stat reads in modifier values (dependency edges, cycle rejection, deferred evaluation in both engines). Dragon's Breath (Fafnir) amplifies the selected owning weapon by 50% of the final Status Effects total, bringing coverage to 205 of 373 with 17 set thresholds and 35 linked talents; controls stay at 97 selectors and 53 numeric inputs. The burn proc is not simulated. The exact description and Fafnir host are guarded; the 50% rule matches Ubisoft's Red Horizon notes.
- G99 adds Estimated Weapon Rate of Fire (`weapon_rpm * max(0, 1 + rate_of_fire)`) to both engines and the desktop weapon summary/comparisons. Existing owner-scoped/current-buff assumptions feed the estimate; base RPM remains unchanged. No selected/equipped weapon yields zero. Additive stacking is an explicit estimate pending live-game verification; firing events, phase transitions, burst behavior, caps, reloads and DPS remain unmodeled. Earlier notes about unconsumed Rate of Fire describe previous milestones.
- G100 adds Combined Reload Speed (`reload_speed + weapon_handling`) and Estimated Weapon Reload Time (selected base seconds divided by 1 plus combined speed, or zero for a nonpositive factor). Raw inputs and base time stay separate; current weapon talents remain owner-scoped. Ubisoft's TU10 developer notes document 1:1 handling contributions, preserved in [this archived developer post](https://devtrackers.gg/the-division/p/7f398b76-title-update-10-patch-notes-updated-2-00-pm-cest-june-18). Inverse-speed scaling and stacking are estimates pending live-game verification. Empty/tactical differences, per-shell timing, animation floors, reload events, skips and refunds remain unmodeled.
- G101 adds Estimated Weapon Magazine Capacity and a Base-Only Magazine Bonus subset while preserving the original aggregate. Ordinary bonuses scale base plus flat rounds; Overflowing variants and Pakhan add their explicitly base-only bonuses once to base capacity. Final estimated rounds are floored and clamped at zero; missing selected base returns zero. Scaling, stacking, rounding and weapon-specific caps need live-game verification. Frenzy and On Point capacity inputs remain manual; chambered rounds, ammo reserves, reload/refill events and DPS are excluded.
- Skill preview coverage now includes 43 of 43 variants; see G103 above for all damage estimates. Historical pre-G103 scope: Reviver Hive exposes effective Skill Tier and charge capacity using Ubisoft's July 2020 TU10.1 rule (one charge at Tier 0 plus one per tier); this is a dated mechanic and needs live-game verification. It does not simulate self-revive behavior, uptime, cooldown, or Overcharge. Assault Turret and Striker Drone expose only effective Skill Tier (clamped to 0-6) and a relative cooldown factor from modeled Skill Haste. Ubisoft's July 2019 Episode 1 notes explain that 100% Skill Haste halves cooldown; the displayed factor applies to an unknown base cooldown and excludes skill-specific cooldown behavior, mods, Expertise, timing, and Overcharge. The pinned source has no current base damage, repair, duration, charge, or tier-scaling tables for these skills, so no absolute skill damage, repair, duration, or cooldown is calculated. See [TU10.1 notes](https://www.ubisoft.com/en-us/game/the-division/news-updates/2R9VeV1ReYJLR6WDalzpaL/this-week-in-the-division-2-july-10), [Episode 1 patch notes](https://www.ubisoft.com/en-gb/game/the-division/the-division-2/news-updates/3pepSvWluJRxzkWZeEWv56/episode-1-patch-notes), and [Skill Tier overview](https://news.ubisoft.com/en-us/article/16gz4Sji1iWf5JpGfpgdAc/the-division-2-state-of-the-game-recap-changes-coming-to-recalibration-and-skill-power-after-episode-3) for scope and dates.
- Gear mods use catalog maxima, format and step, with five reviewed minima (G349); 274
  armor mod slots are imported. Missing rolls are unconfigured and contribute no rolled effect.
- 6 specialization trees with 260 purchasable tiers are imported.
  Global (165-point) and hub caps come from the pinned source README/CSVs. Tier costs are incremental;
  each tier requires the preceding tier. A source parent-node link requires that node's first tier;
  the CSV does not encode the exact unlock tier, which still needs live-game verification.
  Hub parent-node links retain their unlock prerequisite. The seven weapon-type damage branches
  in each tree contribute 5 percentage points per purchased tier (5/10/15% cumulative). The
  importer reads exact allowlisted descriptions and emits incremental values, so selecting all
  three tiers contributes 15%, not 30%. Unrecognized wording or changed increments fail import.
  These 126 tiers use the existing weapon-type bonus stats and specialization-node provenance;
  the selected weapon preview consumes only its matching type.
- Another 57 specialization tiers contribute permanent bonus totals: Pulse Resistance and
  Signature Weapon Damage in all trees; Sharpshooter Stability; Survivalist Protection from Elites
  and Outgoing Healing; Demolitionist Explosive Damage and Burn Resistance; Gunner Armor on Kill (%);
  Technician Skill Tier, Repair Skills, and Skill Damage; Firewall Burn Duration and Health on Kill.
  Exact source descriptions and cumulative values are allowlisted. Uneven increments (3/6/10%
  or 5/7/10%) are differenced rather than treated as equal tiers. Armor-on-kill percentages remain
  separate from flat armor recovery. Recovery and signature-weapon bonuses are rates/totals only;
  no kill frequency, recovered amount, or signature-weapon base damage is simulated. Skill Tier is
  an uncapped bonus total, not overcharge; only the selected three active-skill previews clamp it to
  0-6. Skill output scaling beyond these limited profiles is not calculated. Conditional specialization effects,
  equipment/skill unlocking, and procs remain descriptive/unmodeled.
- Seasonal effects, Prototype Gear/Augments, PvP tuning, normalization, enemy defenses, crit/headshot
  expectation, and proc uptime are outside this initial pack.
- Weapon base fields are item data and only resolve when the caller sets `weapon_slot` to the
  corresponding equipped slot in the resolution context.
- Weapon item effects, including configured rolls, mods, and talents, require that same context
  through `effectsRequireContext`. Inactive weapon configurations are still validated, but do
  not contribute bonuses. Item tags and set membership are unaffected by this effect gate.
- Import validation requires one recognized weapon type and all six base-stat effects, with
  item and base-stat gates matching the equipped slot.
- `equipped_weapon_damage` applies the community-documented all-weapon and weapon-type additive
  bonuses to the selected weapon base, including purchased specialization weapon-type tiers, then
  modeled Glass Cannon and context-active weapon-talent multipliers, plus selected context-active
  total-weapon-damage effects. It is not Ubisoft-verified and includes SHD Watch and selected-weapon
  Expertise, and excludes skill damage, all other talent effects, other target modifiers, critical/headshot
  effects, and proc uptime.
- `has_selected_tag(name)` tests only the item in the selected `weapon_slot`; `has_tag(name)` still
  tests tags from all active equipped items.
- G47 adds seven fixed exotic talent previews: Adaptive Instincts' three manually asserted
  Chameleon headshot/bodyshot/legshot buffs add 20% Critical Hit Chance plus 50% Critical Hit
  Damage, 90% Weapon Damage, or 150% Reload Speed. Capacitance uses assumed 0–40 retained stacks
  for 1.5% Skill Damage each, plus a separate manually supplied 0–6 effective-tier input for 7.5%
  Weapon Damage per tier; this input does not read derived Skill Tier. Stand Your Ground uses
  0–10 assumed retained stacks for 3% Total Weapon Damage each while `sawyer_active=true` asserts
  validity, including retained stacks after moving and before expiry. Kill Confirmed models only
  Memento's 0–30 long-term trophies (1% Weapon Damage, 1% Skill Efficiency, and 0.1% Armor
  Regeneration each); core-scaled short-term buffs are omitted. Liberty or Death adds 2% Weapon
  Damage per 0–30 assumed stack. Breathe Free uses `lady_death_next_shot_active=true` for the
  fixed 75% next-shot weapon-damage multiplier; the flag asserts a valid available stack that the
  shot consumes. Incessant Chatter uses 0–5 assumed retained valid magazine-buff stacks for 25%
  Rate of Fire each, not current enemy count. Shot generation, timers, proc events, stagger immunity,
  shield repair, Memento's short-term core bonus, Skill Efficiency expansion, actual Rate of Fire,
  and DPS are not simulated. Skill Damage, Skill Efficiency, and Armor Regeneration are raw bonus
  totals; no absolute skill damage is calculated. Source descriptions and compatibility are exact
  pinned CSV guards, but live-game behavior and values remain pending verification.
- G48 adds eight selected-weapon talent previews. Precision Strike and Perfect Precision Strike use
  a shared 0–3 assumed stack input (formula caps 3 and 2) and `precision_strike_active` to assert
  the qualifying close-target hit and 5-second bonus window, adding 20% or 35% amplified Weapon
  Damage per effective stack. `swift_active` asserts Swift's 20% Weapon Damage buff after a
  cover-to-cover move. Foam at the Mouth uses `target_ensnared=true` for 25% weapon-damage
  amplification. `high_priority_target_active=true` manually asserts that the target is the
  highest-ranking enemy currently in combat for High Priority Target's 125% amplification; the
  tier hierarchy is preserved in its source description, not inferred. You can look...but you can't
  touch uses 0–10 assumed Prima Donna stacks for 12.5% amplification each. The 0–10 input bound
  is a planner limit, while the source says stacks replenish over time; that timing is not modeled.
  Stack generation/consumption, cover movement, range, buff timing, enemy rank, repair effects, and
  procs are not simulated. Agonizing Bite uses `agonizing_bite_marked_shot=true` to assert the
  qualifying marked-shot state for 20% amplification; its guaranteed critical hit and five-second
  critical-hit follow-up are not modeled. Unnerve uses 0–5 assumed retained target marks for 15%
  amplification per mark; kill/proximity eligibility, mark application, and the 10-second timer are
  not inferred. Exact source descriptions, compatibility, and fixed hosts are guarded.
  Boomerang remains descriptive pending review of its next-shot damage aggregation bucket.
- G49 adds seven selected-weapon retained-state previews, bringing direct coverage to 144 of
  373 talents. Three new flags and five numeric inputs bring the assumption controls to 66
  selectors and 42 numeric inputs. `full_stop_stacks` (0–20) adds 2%
  Weapon Damage and 5% Headshot Damage per retained stack; hit/headshot generation, reload
  clearing, temporary armor, magazine refills, and Dread Edict's no-reload headshot-kill effect
  are not simulated. `payment_in_kind_stacks` (0–100) adds 2% Critical Hit Damage per retained
  stack; damage generation, its 10-second lifetime, reload-triggered bleed, and armor damage are
  omitted. `big_game_hunter_stacks` (0–25) adds 6% Headshot Damage per retained stack; the scoped
  450% weapon-shot branch is deliberately excluded, as are headshot stack generation and decay.
  `cover_shooter_stacks` (0–100) adds 1% Weapon Damage per retained stack; in-cover firing, kill
  refresh, the 15-second duration, and reset conditions are not inferred. `gangland_hit_active=true`
  asserts your own mark on the current target; `gangland_hit_other_marks` (0–3) adds 5% weapon
  amplification per other-agent mark above the base 20%. Marking, transfer, other agents' actions,
  and mark duration are not simulated. `faster_than_reloading_active=true` asserts the primed
  swap-to-weapon bonus for 50% Weapon Damage and 20% Rate of Fire. `perturb_active=true` asserts
  the primed swap-to-weapon bonus for 30% Weapon Damage and 25% Rate of Fire. Reloading, priming,
  swap timing, magazine state, and Threat are omitted. All effects remain selected-weapon-only;
  assumptions are transient and do not model proc uptime or DPS. Busy Little Bee swap transfer
  and both Boomerang talents remain descriptive. Exact source descriptions, compatibility, and
  fixed hosts are pinned from the source snapshot.
- G50 adds eight selected-weapon stat talent previews, bringing direct coverage to 152 of 373
  talents. Perfect Allegro adds 12% Rate of Fire on Invisible Hand; Perfect Accurate adds 30%
  Accuracy on The Railsplitter; Perfect Extra adds 30% Magazine Size on Rock n' Roll; Perfect
  Distance adds 25% Optimal Range on The Send-Off; Perfect Jazz Hands adds 50% Reload Speed on
  Cuelebre; Perfect Optimized adds 20% Weapon Handling on New Reliable; Lucky Shot adds 20%
  Magazine Size for Marksman Rifles, Pistols, and Rifles; and Perfect Lucky Shot adds 30% Magazine
  Size on Baker's Dozen and Incendia. These are raw bonus totals only: they do not change weapon
  RPM, magazine-size, reload-time, or optimal-range card values or add damage to the equipped-
  weapon preview. Named-weapon cards may already include intrinsic stats; those are not applied a
  second time. Lucky Shot's missed-shot magazine refund is not modeled. Exact source descriptions,
  compatibility, and all fixed hosts are guarded. No context controls are added; the pack remains
  at 66 selectors and 42 numeric inputs.
- G51 adds eight selected-weapon conditional previews, bringing direct coverage to 160 of 373
  talents. Five activation flags and two numeric inputs bring the controls to 71 selectors and 44
  numeric inputs. `overflowing_active=true` asserts one eligible +100% raw Magazine Size bonus
  after the source-defined empty-reload sequence for Overflowing or Perfect Overflowing; repeated
  trigger stacking and actual capacity are not calculated. `pakhan_stacks` (0–4) assumes currently
  retained next-magazine stacks, each adding 75% Base Magazine Size as a raw Magazine Size bonus.
  `eagle_strike_accuracy_percent` (0–30 percentage points) supplies Eagle's Strike's current
  Accuracy bonus manually; firing buildup/decay and Tenacity are omitted. `restrained_active=true`
  asserts the 20-second post-shield-break buff for +25% amplification; the pistol's automatic-fire
  behavior is omitted. `ortiz_highlighted_hit=true` asserts a hit on the currently highlighted
  body section for +60% amplification. `binary_trigger_empowered_shot=true` asserts the qualifying
  +500% amplified shot at seven stacks; trigger events, stack generation/consumption, and the 7m
  explosion's separate Weapon Damage effect are omitted. `startling_active=true` asserts the
  15-second buff after this weapon suppresses an enemy, adding 30% Damage to Targets Out of Cover
  to this agent's selected weapon only; ally sharing, suppression, and Disorient are not simulated.
  None of these previews changes weapon-magazine-size, RPM, reload-time, or weapon-card range
  values. Only Restrained, Ortiz Assault Interface, and Binary Trigger affect the equipped-weapon
  damage preview; no absolute capacity, reload, firing, status, or explosion output is calculated.
  Exact source descriptions, compatibility, and all fixed hosts are pinned from the source snapshot.
- G52 adds eight gear-talent previews, bringing direct coverage to 168 of 373 with 75 selectors and
  46 numeric inputs. Opportunistic variants use `opportunistic_target_active=true` for 10% or 15%
  amplification against an asserted debuffed target; Energize variants use
  `energize_skill_tier_active=true` for the +1 Skill Tier branch and omit Overcharge. Vicious Cycle
  uses `vicious_cycle_stacks` (0–30) for 1.5% Weapon Damage per retained stack, without its purge,
  status cleanse, or healing cloud. Weakest Link uses `weakest_link_target_active=true` for 30%
  amplification against an asserted lowest-ranking target, with no ranking inference. Over the top
  uses `seeing_red_active=true` for 25% Total Weapon Damage and raw +100% melee damage; melee/grenade
  triggers and cooldowns are not simulated. Decoy King accepts a supplied 0–25% amplification value
  in 5-point increments, where zero is inactive; phase progression, timers, and Decoy Overcharge
  are omitted. Gear effects are not selected-weapon-owner gated. Ally damage, proc generation, and
  timers remain unmodeled. Exact descriptions, compatibility, and fixed hosts are pinned.
- G53 adds four fixed exotic gear talents, bringing direct coverage to 172 of 373. Bond's
  `bond_proximity_buff` assumption selects only its ally or skill proximity branch, adding raw
  Critical Hit Damage, Skill Efficiency, and Armor Regeneration totals. Chemical Agent uses
  `catalysis_stacks` (0–12) for +2% Weapon Damage and +2% Status Effects per assumed current stack;
  its Reactant bonus armor branch is omitted. Counter's `counter_buff_group` selects either its
  offense group (+20% Weapon Damage and Rate of Fire) or handling group (+50% Magazine Size and
  Reload Speed); swap events and timers are not modeled. One in the Hand... / ...Two in the Bag
  adds passive Ammo Capacity, Repair Skills, and Status Effects totals, with
  `acosta_skill_tier_active=true` asserting the +1 Skill Tier branch when the pre-buff tier is
  below six. Capacity counts and Overcharge are omitted. The four effects are gear-wide, retain
  talent provenance, and add no selected-weapon gate. Their exact source descriptions and fixed
  exotic hosts are guarded; source values remain pending current live-game verification.

The supported catalog values should be checked against the live game before this pack is presented
as complete. Ubisoft's official Y8S3 notes announce gear and brand-set updates, and the Sept. 23
TUY8S3.1 notes announce further PvP-specific tuning; see
[Red Horizon PTS notes](https://www.ubisoft.com/en-us/game/the-division/the-division-2/news-updates/hdUs80E1Fc9nHAhckksLw/red-horizon-pts-patch-notes) and
[TUY8S3.1 notes](https://www.ubisoft.com/en-us/game/the-division/the-division-2/news-updates/3XY3PhLzRfbBPZ8cedURYO).
The modeled weapon-card damage equation is based on community references, not a published Ubisoft
formula: [S-I-N damage guide](https://www.s-i-n.co.uk/div2/builds/dmg/) and
[community formula discussion](https://www.reddit.com/r/thedivision/comments/g1hwju/in_game_damage_calculations_and_you_a_mathy_post/).


## Selector metadata candidate

Base manifest SHA-256: `7db86394c8e28dc773a7e409b33d0d0b0a80cbd7c4d37a28017cfca984707a5a`.

Metadata proposal SHA-256: `0c3a98580d2e383bae87197075150628a15fa99689cd25a219fe0a7a39a26681`.

Only selector names, sections and descriptions changed in rules. This candidate does not establish consumer acceptance or runtime verification.


## Direct weapon composition pilot

Weapons dataset SHA-256: `b9d63c4a65e86ddb82150e9f750f0c201eecd0aa1fe35e00f07a2c1261803113`. Bindings SHA-256: `77c9bb5660b888d0d71d9702cfec0d8b3566f8d0c48af54fd2e5e47d3fc317e9`. Seed manifest SHA-256: `ee857c41351f2d19b78fa84c01ab3c85356240f43e3710d8d4dac1dc37acb5d6`.

Only explicitly bound direct weapon scalars are composed from evidence. All other model content remains from the pinned seed; no runtime behavior is certified.


## Verified artwork packaging

Seed manifest SHA-256: `b5f217ed54345cafd36cd1e9959f53471b9cb71c07128180b3241640a6182bf6`. Assets digest: `6ae569a590cfbea15dd6a2003ec1f4709343484d7b86c498112bd093f3de1a07`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Complete component assembly

Composition plan SHA-256: `0a1dcc81ea068ded11d65e405d3199d8f087fe177422b8ea4426e6f9f187568d`. Consumer review commit: `d85de693504cffe6d9571d08515d2d823deeeca9`.

All rules domains start from explicitly pinned consumer-reviewed authored model components. Source adapters replace only their declared fields. Complete pack assembly does not imply complete native source derivation or runtime verification.

## Shield skill mods (R29)

Shield-mod review SHA-256: `54cd5ea1ab45877710051cd1f49533e3804ff6b444af82694a90963139578c76`. Circuit Board, Hard Coating and Gyro slots on the four shield variants, one choice per pool attribute at item power 3777 (Orange), wired into the ordinary preview tier bracket. Not observed in game; see reports/shield-mods.json.

## Direct PvE brand scalars

Brand dataset SHA-256: `b6ab83386ec9bda2c5d00c8ac7f747863b5ae753dbb61cc51ae0e79669298636`. Bindings SHA-256: `bc8b31c1b1679034ea176936bb05916f489eb97e8406a0c4ba3cfab726749ab5`. Only explicitly bound direct ratio scalars are written; PvP overrides are retained, not applied. Membership, inheritance and runtime behavior are not established; see reports/brands.json.


## Direct weapon composition pilot

Weapons dataset SHA-256: `b9d63c4a65e86ddb82150e9f750f0c201eecd0aa1fe35e00f07a2c1261803113`. Bindings SHA-256: `b3b4084f724dfcc826c7c8c85d18c3134a17797b4030be2f94329316548da0dc`. Seed manifest SHA-256: `ed9852756123cc03d172c92b60cdbf0a5938d0869cde71582f2314f01ad0e861`.

Only explicitly bound direct weapon scalars are composed from evidence. All other model content remains from the pinned seed; no runtime behavior is certified.


## Verified artwork packaging

Seed manifest SHA-256: `a3297218a22fe03f5c2371e24f19ddb9004a0710a77ea1d4d13c06be2a2b2421`. Assets digest: `6ae569a590cfbea15dd6a2003ec1f4709343484d7b86c498112bd093f3de1a07`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R31 original-only slot artwork candidate

Base manifest: `ddbac2589cdae7232ac1f1ec55a2231c5e8b0a1c60c58bd2700d32ba0e9178e3`. Accepted batch: `f52184434e2ba814fd38239ed7b3f33c85ac9826b60991eba242d457ff3ba383`.

Only original slot PNGs are registered and shipped. Uncovered artwork uses consumer fallbacks. Historical source notes above do not describe the new images. New authorship, MIT notices, production records and visual acceptance are preserved separately under support/. Reference photos are not included. Rules, simulation and share-code bytes are unchanged. Declared rights and owner visual acceptance do not establish consumer adoption.


## Combined R34 slot-art and talent-rule candidate

Base manifest `d32bca99d00a47ede8c04b46f41015ea7251d453f30618a66ada472ccbf59e68`; overlay plan `94acb5a2b5261cc99ac00d0e715acf94fc415d4b0afd824d6d4d83a0940da68a`; evidence packet `973fef0fb0064353dd5043cab4685064ba73570062779a89a2d53614e6235c91`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `615d05b58c368b937ee37ebe73ae19a1550da25d5c259c553a2325112a3d2e8b`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Exotic equipment limits (P22)

Base manifest `a58caa957bca2dfaa811d532bde91522896c5c5e900d1a7531a6dddabec7d491`; overlay plan `fa6368b97b054bf05f7e6e95bec111fe35c432033a0806e6560f0007aa25d9c1`; evidence packet `1216b911478a40b14127405931a368513f70c49c36b873ffc6af6febb444eed2`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `296cc9432deea42b6459f958c6a312749960e3051a4d3385f0f6beea4bf3856e`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Weapon-talent burn and bleed ticks (R30)

Base manifest `78c09c32c2ef14f4ca9494574cf92896ec6ae73be5c8d2175d93664ed17fac61`; overlay plan `c83b8ec8ebc3ebecc4baab211c5e301dff1624e66a3e53ef06d58a86924033d7`; evidence packet `2c22509927e338c52671c48617e777c76df7840c4e00f1e9e44c4aca523c5005`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `e7b2fb155df5b4c24a8ab4f751d9cd9ad82628c66a1c79f72a8b7dab6e995c3a`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Rebalance per-colour cap matches the source (single 80-stack pool)

Base manifest `4f688f64b65dab181c266b4afc21614d21fee5ea8d291739fbd225170efdb52b`; overlay plan `9d385675321a606223e27c07721dee6acc952df5daf9ad9ea25ea672e5fdda3b`; evidence packet `a3f6eb0d84d5121d9bb13d370ce2f52b2af55eee0184997469a0da8a40f57445`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `dd4cddb66c3c1399dd81dc6be0f77e1045328df5caab7d47576206bd2ba0281c`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Unstoppable Force stacks share one refresh-on-kill timer

Base manifest `75f250c97f9caf977feb3c2e402e68a4500b416dfe04aaa6be246fee21e1514a`; overlay plan `c5e154fe1abb921fba0fd9f23370787194dd60a54b48e19b9cb1f4829ef72af3`; evidence packet `768687a73ee0778a5c592d4afe85c7d258fe307c1c2d644de1935f4dd9c38c5d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `9fe67c87fdcb928f6e79ec40b8d1884fb56cf31403b77c1cbf1b9bb6722a7d58`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R35 talent coverage audit on every talent

Base manifest `0e9aeef8175a2996eba1137aa3c8c891e6465d78d8eb079ae480f1fe841a898d`; overlay plan `5cb4d81bfb0905ab39864a307ccdcbb04631fe34888fa7fa1108c44b40366d1a`; evidence packet `5a04104a0089589e4df2d439b94fdf9e43fa38a1b4de87df4c103127f25016e4`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d3f1d52e227807e6ca7853afd6ed262b5abfbf9a9976833137b4da32666af5f5`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Stacking talents refresh on every trigger, as their source rules declare

Base manifest `432d615f7859781f7f0086d627875781fcea7b7a0de250e35927fc6afc93e356`; overlay plan `1f9902a0dac5c0355ea8a64228bba28d34413b02895830ac4f0b113151bbea24`; evidence packet `fd99c3b1e7094c3ce543f0f0ce47564192e6396b9ca40cf4ef4e66d99b907072`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `40b1c75f37f709b94dc864ee791cf40987d4268922458fed458bc6b0449b874a`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Non-refreshing talent buffs ignore triggers while active, as their source action groups declare

Base manifest `a6086ac66ea9a3d7d07ef60f2159120daf422947b71e5924df6dfd2eefbd9e87`; overlay plan `10e5a7702e20312b9e9ac9a86a7b8a27662772370af1f9ac1c1c2e322b0e1aeb`; evidence packet `d93839aed0995d903409620f886b3615ee2c365a3dafbc4b672dcc6e0e04b950`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `16adf274b72a0a16cceaac558268105d256f17e8da0c1d26c0a4c425e6e02771`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Graph-driven stacks expire on one shared timer, as their skill scripts restart it

Base manifest `3442bcc93d9d7f86d93def75ada2372c40e727f326c73820df47cbbd75ed54cf`; overlay plan `225ab22cfd66c24e5d48100f302205162328b4bed52d476d459965d44ffac00e`; evidence packet `166d4fc9c8816928538537b0b47acff94c5f0e49f520fc58adbbf592c9f3b0b7`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d82856b24f506b1744fdf863b46ab7bbbcb0142702d19fb40b10c84b82ad871a`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Concussion coverage notes the unmodeled marksman-rifle duration

Base manifest `7577f613cd853dfeed5d22ab4121fce908d9b6fa1220e1d5a99721c319caeac8`; overlay plan `a25d419b28cd8c625f11385c5460a5f7675f08705aacc7e318cee6c58c26d8d5`; evidence packet `55524dfedef7b17dfa548eb9eca9ec733abe55ac6319b401bbe0eac7abc28b4e`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `04b3f33e3ce16fefdc749a8cdf117925fe31f22b2e4a95bdfb54f107c8cd2d18`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Dragon's Glare burn rule (R36)

Base manifest `e590a75297991c0cd91bcdc7516405703ad905e51ef6ad54dd10d56429454636`; overlay plan `9f2fce01658bd26e3ea35c560ecd3a8379e6963d59cade9bf47c8182a9cfc62f`; evidence packet `eed747728c0ab5a7afb66f0d8c9e37cf6f18ad1ea519031335c3c90e1b35fdc6`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `59b8842d76e36f2f319f61378120e60a345d147855ea9b46e89ef97bd0a906d1`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Frenzy ends on weapon switch, as its source remove condition declares

Base manifest `e00c46a53a7ba0a68944be89a956130a747352071e79f6fc27daa44838585d85`; overlay plan `1a2c25004a9b858541b7e2b2fa36b0701512967d0697d999c82f9c1f076d7b9c`; evidence packet `46ae97550d31407c595bb591dbce3317c7b9df50a22c05eda36e9d93c9494d84`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `fde15d65b3cc7a5fb6eebbc4eb2e973548d7d981d18e04a6658905a9c32ecbf6`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Concussion's headshot buff lasts 5 s with marksman rifles, as its source rules declare

Base manifest `966ef3c418bc6985fcf3419cdefd8255177d01fd8c59421eb4c78a04e332aca0`; overlay plan `0b8f3cf9cbd8c1e54db19fc50eb9e675d16e89dcad3ca589d426e4cd63f84e6c`; evidence packet `2175bc986d62cf5804816ad230bc721f3179617c71080a6a828d628f703c81f1`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `31a073e2de456cdce8932f4214b7cdb967154247b1b62b8429c880997827f703`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Resolved converts the next eligible hit to a headshot (R36)

Base manifest `de4303ae5b3ba7fb89a833bb5a3c174fd71cc0551f97a93e6744d358d8d12c13`; overlay plan `ffa6bf0d71f80f62db549820ffe6236c3921e71d35f4be6b86af078400f56bbe`; evidence packet `3ccaf31d821286bcb98d5e2a9c97b041f0d6eda3cff9f09f4369a1a7154b262a`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `145a2963dfea15aa94b7db48c0afa90536d0d7094689b8189b279db2e907a766`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Ablative Nano-Plating grants the wearer bonus armor on armor break (R36)

Base manifest `6658495ffa85580b57821f0c0e3a1a4569c00e6a0ce05c1ba9b90e8b504fbc46`; overlay plan `1e466d0af752809867187b2c345a76a1c87b266ee93f4c01e6ee4006e0d14a38`; evidence packet `9ac38d0563a74c8ecf0a68be7be60677eafd34c6c2d7d99cd942bda5bb6cb6af`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `8208ec96583f960a331ca2ba46df8a5d6618abbf72cc5fdbcd8ee1439bd776b8`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Steady Handed refills the magazine and clears its stacks at the cap, as its source rule declares

Base manifest `0d3655cabd8c73adb3e0b38b4247a04521d2270e3c3df7c3ef5545667a9ebbfe`; overlay plan `f71e42846172d41b13d81b1d3c2b3871670cf1502f00aa111e3d7854fedf2688`; evidence packet `47b7f77eaa4a2411b47e284afd5017ab838b354263959264828f8fc49ccb065d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `fc31a6ce7b20e1c21759e52a87a1cb67659473a22fec8be8323c72fe917d0cd2`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Trauma blinds on headshots and bleeds on body hits, as its source rules declare

Base manifest `668c1a1ad2a4b5b2471af875085afdc22449529ded658b23aba28bbcf4a9b96f`; overlay plan `74a9a9f0ef9e69314dc3bc68f5162a216ebdfde3daedc6db33d1f272b0095133`; evidence packet `3776eb2afd47db800104521278896855330171ccb967b3102093b3dfd6cb6eb1`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `617c787c543ac9393cb4fe9fde5b4b0907847da2ad6d3db4c80084c2fcaf1936`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Perfect Soft Spot's weapon damage is 32%, as the game files and the in-game tooltip show

Base manifest `68b2557c8e3e401b1ccebb1c792bdce2ab6eeaf00a001aa0084a6915e459a940`; overlay plan `e08890558f421d61fde8f76d0c1af54a23e264bf29c36a3c1887c2f2b342452f`; evidence packet `43047e9da442217bf7b144622a3b72c8055e8b3ad9a0a57ce0e843bd9b3128ae`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `2f6a7fd27616546eb25afcc99284279587b27b4d3a69d7786004163fa7551491`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Reload-finished talents fire on reload_end (R37)

Base manifest `7a527cffc3f67703ab31b6cf0226f67d99e3b45825c7c08724060f8f5129d6d7`; overlay plan `1a614f1cbf9f5b4cfab778bbb80da48df114145db81c5f5f5b375f6fdcd86f0f`; evidence packet `d5fa046af9a089118af44db232d0d1d62955c23449e5d0e5fb891be1a4b1b217`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `33d62a4761d1d6a40ce527a923e87e6e83407c71967d03c5ed2c13be051faac5`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Chemical Agent's Reactant also grants 20% reload speed, as its graph applies

Base manifest `991358d6f513bf00b515af4452a3805ca4ef8a97ad87678bbb1f4cd56b13f5da`; overlay plan `36acad9e150399a2a964733eb64f335533e4445b411dcf29a30faaf90c858e1a`; evidence packet `e6219752d85df04fc5a6b3791713940f03945d9e6cc2278988903a5ff0845d69`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `30a85155e84aeee1cdc28e15ef0ada73f51b75ff621cde18c6825447c6ebe3c6`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Pummel's kill count survives reloads, as its graph counts it

Base manifest `249701c5743daef71ed8b8d34be8d5ccbb34177dd2389627cbff7122a480824b`; overlay plan `9b73759951209df0ffe9617048dbd141bee15be94f1cec47363a175425ba64c5`; evidence packet `b100f9135e3ff0190bf9398bd1c394df40b8a3e7dd5df402cd646324b642d5f0`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `7a8dbde1545418fef28e34c55f494de6e597af6dcab22949a7a2c1636241aced`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Reset fields follow the source remove conditions (Breadbasket, Strained)

Base manifest `fa22625ef9bff8deda834d16bcbb057e4ce239e975932aac2953da81eb103162`; overlay plan `0f45a470fb30945d4b8665039734cd548c519213998f089449a3e68232735539`; evidence packet `308ba39e0b710992bc08649034d5f3a8921241470ee130a1661b913cd16ec37d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `1fc03707aad545e4cbd528343e57d0d1adb65fe097edab30d03d118fe5cca9c3`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Reactant's selector names both of its bonuses (R38)

Base manifest `b9be9edb5850fb03fd8380f47119ab8c6b6c6ddce36a4a4bfd3178f201d3172d`; overlay plan `1bc97bb1a16c679668aaf45fdc74c4ed6f9df15c43fc35b1b6550ba9bb15b966`; evidence packet `2521d3f9962748a57166b9033589ee1a85c090f85b5d016badffc6ce77048fcc`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `f28c9aa1d69fb576bac4b24f1cc53c5843eceff562ff9d5107ba8c638e20f227`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Toxic Delivery total damage stat (R38)

Base manifest `e9de634f970e353ebec6bc50abaf11b23a1676ffc96aa73e7c67e3e48753bd31`; overlay plan `67a760a8fe06d6d4e8a33500714667fa5daff8d3678d1cbff58458511daab8a8`; evidence packet `4c3f03d5d0834b0a596ec390b9f1e80797b9a6601b5453e8eaa83867728bac39`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `775c7812b3a3f92d765b77e8e354222ab1d1f9b5965deb900a734209b9d8c5d5`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Pummel's kill count drops after 20 s without a qualifying kill (R38)

Base manifest `682a97d182c33f641913938e6c66c6d327050a766361dad3dcab8e992148454f`; overlay plan `b6512d177f791270ab9d248cfe82d1e10803f38943578d71a2197b64112c4b7f`; evidence packet `616376879af0caa7aac749a49bbceea00d9de661ac0a350146343df378aca50d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `339fec4493bbcc9412fbae12fee34f1cc422d0fe1e18d02a9319e595ee6dc8f1`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Quick Draw and Trauma timing from the R38 source answers

Base manifest `6f62ff6c5a364b837e9de177110d03e8ad98ee9862c3f1b5d84ed9913d6fc3a7`; overlay plan `ccacc387877dd76ec91d3896b03c88faff899b9e92902d2cde015f413311ae37`; evidence packet `2371c481a7cd014cf5ee38096414e57cef2b935af8cea5f7a0e6181498f44d1f`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `816685fe0ef6fca7272ad3f3ef65632ed1ae553eec3db864c7aecd8ae04f9d24`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Toxic Delivery damage over time (R38)

Base manifest `0c1fefc549a91c73944a4142773d1fdbbb757818355aa36278b1191ea2d0be81`; overlay plan `6de513c84cab820f80ec05ceb0e716a0d67a2c5a946e3bc2a0a28db683313aea`; evidence packet `4cc96d4865be45f65b55421b50f5720a852d1065b30c3e308879c5cf15377668`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `7e9bc22c97b495db4dfa69516ea9e5287b8176a4d1f095d42fbb511e972a1d75`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Septic Shock's damage bonus lands on the 8th hitting shot, as its debuff graph counts

Base manifest `bfaa7d2df60e6f881c8b619bcb543d754addf813f9821b8d98040c06b75a46e2`; overlay plan `17dc64038f0b1a08a8b0756c2341aa3901764736bc79eebbeed99fc64c191445`; evidence packet `fdb05599e70f8bb864894c7e2bd217f40a9f1576937682ddf3546e1798ef5367`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `9deff169da7c1e5b3c74d7b32f49724eeccb01bb6b588484c50444dd369085bc`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Slotted's colour counts on the Investor's bonus slots (R38)

Base manifest `334fa52b9d6f0a1b416dc99dc6642ecc4757787fc5be07ea2e8e11264e37b044`; overlay plan `b3740f369aac40b96bac7e6dd555758acd27dcd7facc305f6bdfcbb725ed690b`; evidence packet `9ff26aa30a0222c4d18184143c9c3a5aff1c1822f12564e9dcc24805f1a6f363`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d2d33c107971d29031913353b5b48d143d4ffe13663d51d5d31de1aa1d0682e9`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Ardent's heat meter and burn while full (R39)

Base manifest `921e1831a469797767c954100336b1d0a0cff0be10bdbc4716782811a2e62fd0`; overlay plan `30bc999b7b079f8e29ce0b4d55b972b0ccec592271d19593638ab377bec6944f`; evidence packet `690e77b7096a91e65de32f298852dfc0956d9b6736aeed455f37c5fbdce83e5c`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `635f09334ca74242cddc812189df770ddedd4097315b8ff7c683bc4c88cec734`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Each weapon's falloff floor, myDamageMin / myDamageMax (R42)

Base manifest `f2f13df9fc0f039d04e6d71d1524bc948ce151130fd5645784eb918544744476`; overlay plan `969105b326b104ac5eb355d9b031674dca794eae1129dfa3f1dcce1a97f3d3bb`; evidence packet `c31bfb221ba85071f5224faa912dc6e3de732c29733704683bda2bd399410b1e`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `ce1f7f5cef74cbd80271a02162740495e5b43db33e4145354088d721a5a12925`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Clutch, Doctor Home and Preservation as pack rules (R41)

Base manifest `c8d9a15bb48de0716265898c6d0ce510db9cf100e7b8906918a11eaa47d3dcbb`; overlay plan `3045f076c6cd97af6e6f8704d70ab86873f07d10187796c62bcecba05c87f3dd`; evidence packet `114a00e4f071ab2da906222a887b871f65ddf4014753c4f65f94839354f4a119`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `bc9d90f09821879a5f0f9a168dab66cc53db0ad85e68bec5ecd6f9896677e3c1`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## The enemy hit rate by distance (R44)

Base manifest `4b1b7d748b48eeb1cea636b990827c76aac3a59b23db0d3e1ef5f98d058fc1b2`; overlay plan `619dad97d7c834aa490ed3b3df10d74d45e880cad2db8d5a0144b45fe7dd05ff`; evidence packet `48616128f8de1783e8007b044a550816445ff143f7c60ba285588370489706ba`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `5ae7aa0af5c7732829f3541a78ab6826873e60ca38d3d81fe613c223647ac7db`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Loaded for Bear and Malinois (Y8S3.3)

Base manifest `1c98e101f23aea2ac9f31143d881e46585bc7479632e4dabeba2ae5bf180d9f2`; overlay plan `008a170c7f5f7220c4de9b4162524e402ff5f63b16a084cc8f5b72000d7e83f1`; evidence packet `3849ec97ba8b8abe142357011bc0492ba8e7e67ac1e9c64f0524bde1390493c3`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `6f758540020b4d3185c76f2ad6506654c3314242070a1992eb62f4dc89810b54`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Apex Protocol and The Ratel (Y8S3.3)

Base manifest `567b8cc7046ab776b3a75c6edb43a699c2239fa1a1ebf791d71247f8bcf6d6eb`; overlay plan `b11bba7525b7391eb41fbad4c168d421336c6fe30b3a8d4dd27785d832b4c3ed`; evidence packet `a342254b3875743e14df64bf8493384febd4011488a1aa095ad3fa494527566d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d5581a9ca08ed17f70f7cc2eb4dde7eaa39614089997db1c356b3c9d77c91048`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Clockwork and the Y8S3.3 skills

Base manifest `358a9dbd9a2b2ac8ecc798df74610712839e372f8807e4455b24c5d7711e3564`; overlay plan `a40110ccaa3d32f4d4ef1c7acf96313c9228bb124ec1b1846b8d0cda0a9c6b49`; evidence packet `28dbbd4f359b6de36e7af11a393b558bf747b8f781c8b39c41c92646f4e480c0`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `1cf053bd86c3e2ef7031aa35529965ca2ed1d068a50ca0ea0805f2694a35dc16`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R48 corrections

Base manifest `4e4c73d50f793e9d24889eb8d97b1de531b515dacd74e589b54e5954b765e59c`; overlay plan `33d7f57c39b6818567b5d49f4c9541f73c5c5b8d3a3276612d4729ba637cd60b`; evidence packet `3d6389752725654e5c8082f6f75c809756ff8ad7cba689264d0c1c60ed4b52c1`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `1a7b2133300e849f5f5f7f6b30f60dc2a41d9952f95a5b8bb84841ae6c99a5dd`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Afterburn on hit_pellet and a driven Apex Engine

Base manifest `4fd5e8babec77b525cbe01f8e3fd6eb242eed630c237d108c79ae221347084c2`; overlay plan `57d8cd5a39109ccc4268435ad54b178bb6f37dc5e7790b296c9bd1153ffa358d`; evidence packet `cd4a246960e76d632ff9e64b1f3d0f2ad6c5cd64db3932de9bd2f556e648373b`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `5075f3c80c64753cc25cd96fa9dbacd4f1e6cbde84306950afd0c8c68ef25e3b`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Veer Override and Veer Failsafe rows

Base manifest `f87a9ef5cb8225ef2ffcfef8407c885963b0796a6bcd601970b68b77df6ab84c`; overlay plan `1137f596da37aba2fc53127157dca0ae0780bd3c86af78e1094acd03c74a10a8`; evidence packet `1b247ed1b0fb8bf041abfa921e309a7e83d84852f30e8df7398aeb8321bca417`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `0d2a0048cd384ed38d79ff2c4756059716e3a372d61a5682445821fb06d56343`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Burn Sticky Bomb burn base

Base manifest `9a10a41548554c5c0b304cbc3dd4727c522edaa8c71c503619233a7366cc2d57`; overlay plan `2961eec6d36d7ae22dc856371dd966ecb1795ae1cb17e2080cfb5e4b27a2ee21`; evidence packet `50f751f363836d0dd2b84440bba07a22e746fa88cccaff12b3d3f112c1b474b4`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `74d1849db4b1855efe0689144b3ac75afddf313cea826ca89d39f68915ebf7a3`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Average DPS for three impact skills

Base manifest `78cba14fefcf894d92c6e74aa18223907170fd87466a8049c4392a3b9082a17e`; overlay plan `63a622b8fcd6123c5fc73fbcf4fb60538dcd42e94683aa5d3cafcef2849372ba`; evidence packet `675d3c9a8ca9d8caacbdc1caad40f00d1b2a49fac83eef463469f1ba9f569138`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d7b2a34af76102a770edb1a260e55f2b5b74ca5807d55b772917baa32ce15cf6`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Armor kits in fights

Base manifest `4f7fbb45f53c61b5837c6bc999883f35b88301ff28083ad2d86d85593663a663`; overlay plan `9b9f5d616561b81366f20e72d6d92af54b0c4c9af49622ebd5eac5a226163cb5`; evidence packet `f314e85971f7037ecbf678f014d6d83f6ddb599885e3a308e97e33c1fc0bc7af`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `0f051e4f5de6e51b6673e2e63e3d1ae624053627c49d92bf74d0c5899acd94ad`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Fight rules for impact skills and counting talents

Base manifest `dbebdba77d2080ac4bb9f4a765c0ed1c58a87b8c1a3bfdcdc8ef305862c0e400`; overlay plan `158b5ae2d22509925532bce8fb57b308efbe7949e26b84495103b0f8e43edbbe`; evidence packet `fe6acde196930b11417bcf90e8adf82d9f2812677b4b2e298ea2a028fe4773ef`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `5f002c9c6371e0ac8f6170a6248e6b99292a4313906678b67cc85788a5a44b7b`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Perfect Adrenaline Rush and coverage on the counted talents

Base manifest `c25340a0641e2d3eb606df063fdba22c3896f930b40f12ae12b73fe00dae074a`; overlay plan `dfdde8967a044752e60b9dae278f9cc0bea6a1881b9794b3cb6fd837b098d4fc`; evidence packet `45a6ca19dac60b21ed89b4e76fc997dfe86767c33b6090cf53295887d1dcf616`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `28e77cdc9e7a7247ea6ad6247ead8c5de3c74fae7ebd3f20b5480f2244cae4da`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Sticky Bomb mods and cooldown brackets

Base manifest `7aae1be1123a4449775b122ef77f4e906e6fc327a71e968b0e1a12ef5983c2df`; overlay plan `4a3832ab2bfe2743864983eac072786339849d8b29151ec8e5c9c60db55ab55b`; evidence packet `d8de862351bb009c41e1b32547ed1533277537512ea50848666291359ab52109`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `a8306ef37a63a8890e62d128858c03df48213f043651ad3727291460912f6e68`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Armor bonus rows, Sticky Bomb corrections and target rules

Base manifest `bb110a44727f1104ac54ed153113a0b3c76dbfc5ddc670552dfdceb68adb61ed`; overlay plan `4911b05e7dc554157fb023a30002a3fd5cbc531d2f208dfd556f30af005e6283`; evidence packet `98d2a5ebe7720324709f2af00c54f4709945115dabcc027bb4e08442ec309989`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `88f0396428768555f995758c69fe096c42ffe5ccb99d3d9cc4ce3a5bb1bb842c`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Shakedown's move and Bleeding Edge's own bleed

Base manifest `362d293348fe37aee05c4e82057fee9217bd660405adb0e4479d9186fd18a219`; overlay plan `3be4163ae2299114777ada74a35e515cc6a0ab0f67c914e6bfb6dff310105d0c`; evidence packet `bc41df3ab9c1ecc2cf79050b6f4623a71349eb6fb978a33774ee11f8198bfe59`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `7dd5413a1476fb9ac8e64a4f399a86b6b877629fb0d04b7ff890b0b123a839bc`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Seeker Mine mods and source corrections

Base manifest `86ec313995cbeb8f14dabccff7dbf2f7ddfcfa45e09c93828798b384a33a43ac`; overlay plan `22c4b3fb8115a6d6cd5423f3599813090cad7c377ca283053c2a8fca20311146`; evidence packet `d7bae310f4f9c94f5d6e2a5b42613daf453baedaaebd48dc7775375a3e30b018`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `f5bd9ffdde22b91671d0b8472706c4ca435dfbb108aecaa00e0f872af20b2536`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Chem Launcher mods and status bonus corrections

Base manifest `c91ec52801172d2358b71c15d36e7a4065779d4dd102c45ece81e68654b99978`; overlay plan `07c91d9f4724e536de79a941b5dd3f9b28112a3424673f6162e9ea33e95c4e01`; evidence packet `13623985c5e97510f07a87c529c0cc295a3da6c9230dcd7dd942d549982924de`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `3962111a8f1938214e137dd03d7dd73c0e1abc20357efc5b3de6c04446ccd716`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Turret and Hive mods and health corrections

Base manifest `5f4ff6f4d9188387c1946b09a68688ecd7e5e6debe602b64ffc34a4f2d96f70b`; overlay plan `880568f0d277926dadf7daef63c12f2b122c7a109109fe56df5c98e853408360`; evidence packet `fb8394057817b9e663fe4a5df270b62ebc5436683265b6bba1aed5b537402b9c`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `2778e13ab54125471392f565bf5f2cbacbed92ad5a52285f7de9fdc06575e6f5`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Trap, Firefly and Drone mods and source corrections

Base manifest `7b1ee3949e8f06449cd5ce64031c639d4a2876fc28a9f672bea653b40b38757a`; overlay plan `28aab07502613f98aff216bccb9701b9904db1eec3b964f7ab4ca7846e5ab8f4`; evidence packet `40961dcc4fe73fcb0e90c9cfd45b73108a7af3f80fce85b9861b643075a562f6`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `79d4db0f25bc8e61129ac7088d5316c1d3c5b7ea888cc312e04c54b46adad954`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Smart Cover, Pulse and Decoy mods and source corrections

Base manifest `0ca639a2f9ac3352bc367001f46c1d94ea55da720f8a2de7191c975347064cdc`; overlay plan `b60454e04d3d45b54614b638701d6e01e5a3d9a6d103ab0c231ae15a59827723`; evidence packet `7296364e31553e59bc9586dbae8dd3b0ed00412102d4648c2559cc300f22aca2`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d44fc72dc9abc631f119c4b08c93666a75452147c9c74c3986c3bccaf05ba121`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Energize on armor kits

Base manifest `bafafaf3febf3381c756ac8d7d3b09990eba44efb0910e3788fec3e028a6c2e8`; overlay plan `31a5f805cb2b1a4e6281a4684250018bc31d9b1979c0f76ab3d42082518c094e`; evidence packet `126e847a5846b49b58a23cbfb2e65690235ac6b53ca61eba229e358a0ef483a8`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `bbcb69dbfbdfecdca6713e3659ce90b80a8ceb5d9baf45695be2797d83b36572`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R59 and R60: Banshee cone curve, live rates, specialization node rules and text

Base manifest `4d569f6484cc7b8842b3b9558f4f079b423ab4978a5f30fcd50088949b10da5a`; overlay plan `a85ef3efaa4ff0a7ebc7d4aa11eb63113bb567efde73471bf7b924f440c7bc8b`; evidence packet `c4bc1b252b0a387b3d4c62869ff0840ecbce99af97c35a6a4bfcb005d1e8a018`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `fc9feee0b61282844a80ebfaba9d2ba5f2243c2e03ff0cdb4b5534a38c222a20`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R61: Striker Shield's Overcharge regeneration

Base manifest `fe55320a6edc19206577cb33351ff292715c7165dd38a14b49c67ce4a52aa859`; overlay plan `eaa394a7bd45c93d172d4d80efb52c5aef6120cee300009d17dc2bc46156cb8f`; evidence packet `4499aac24730748d202cc626bfa1b2867a593a5e07a1c9e4485dfc3b8939c2eb`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `dee6159f1cbb76c8a0b5d67e86587e7fd93f7a14ac48b2acaef1c82cd451030a`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R62 and R63: Perpetuation's cooldown and Stinger Hive's bleed

Base manifest `6e221f642a0cf48b1202294acc9883e38486bbfb11cbeaa9b52de8a4c6a1e56c`; overlay plan `d7a3c3ed786b0e18e5ad9055be54d5518b93e3dc14272a50478dc6e3c3d6c73f`; evidence packet `9e3c5dc49ce734ca305e8e891b0242784f57c5b058c7b6408315a936652020a7`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `1bf38c1038391d00f4cd03431737c7d1bcde62a700c0a33514891c596e795021`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Stinger Hive's charges and cooldown

Base manifest `9f76801f84546fa37447ff344c4726b5515f0183f151e22f0f9f8e29f646769c`; overlay plan `5fa87e35f1550ce1e96698d44dd36b16412c3dbe3d4a7ca17c8a9e28d810cb39`; evidence packet `3c07fb51c742948cff02f21c9182bf0ab41b8d82e22f1524aefb1c90dc47d26c`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `7f879b85ffba72232e79c8e9593d7a6ea38a0c5df36ba50e70e23b7de04fcfc0`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Artillery Turret's cooldown

Base manifest `783b1b75107038a12e2090f3cd26fd3ed2601f4ee84d8c7048e6d2d7705247cc`; overlay plan `4f3d074bf30c9f62e3b47731156fe0360f55b527c8d9395f496b52ff217906f3`; evidence packet `79bcbe876b421815f0364d24e24b8ec34aea613f961dc86a5c3db63eed0ded18`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `f911aa441d1c520297b787ac5aa718eea6ccff9c9d77dc743157ab82d859dbbe`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Sniper Turret's cycle

Base manifest `47fcffb9d8b45f06ddfce8537b7d30aa4c16a4521aa58b37c6e06483baabaa21`; overlay plan `84dc64e44f66c8ab7107ff1b7aca33697108287e7c4f146474b866d746a3416d`; evidence packet `9ef452de56dc0225317b1b651ff7af06d04c8a7da936479771b32d654c870a56`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `14a75b0132cdc97bc464fcbe2e0249af8746c21b2ef492cf532fe48a1b31d670`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Overcharge as the tier formula

Base manifest `aacf1a3812d6751797b7e02e13e3ef42f8c15456ebc881aea30c0119afe3b08f`; overlay plan `60daec2dab61066de293380ac52adc6b09cb549b28552d08416dd651382363cd`; evidence packet `eb49f9f94e7bdcfcc5b8dc986b69615d1c60066adc08515348fdae4db1e18ee5`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `6831778d4d0e29be8bfff3fd3ced35b70891c99a4f8119987e3020685ad345f2`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R66 to R69: skill cycles, status rows, spacing and text

Base manifest `72b0e34e938fd08bd849b8b6a8a8e680caca9b09475685cc5b4951ae45517e35`; overlay plan `404cfa1c18fe6b7ec056bc0a689c63dc9b30214a851dc3e2174e6136f11c59d4`; evidence packet `b7f1aae1aea7dbb61631a4134c8929c9418efb22c34044ab36c8fb69f00ea48d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d5e9515adf2a711ff47f92641807a9cb54322ea7fc53cf93bf36001d01dfaed9`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R70 and R71: Future Perfect's Overcharge, and interval inputs

Base manifest `dfe4b5f2039f7e13edec10430b405916a63b961084e359f383710ac11b3ce582`; overlay plan `e5409a6d7e6171209ec0577343da7845532f3980e5da26bcfdabf2f832db24fb`; evidence packet `e34abcc80d7118ea31f3ca798453daf105a7712a5f7dabed7cd52b2a06f587f5`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `dc116d3d79dedf3e5230cb79531e93e1f7d38bbdebe477b9cbeeb9339617752d`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R72: Future Perfect's selector text

Base manifest `49e846e07c8fba2248795d7c3f9d33f0715d5828d255430d0cdf09b3afa48910`; overlay plan `cbaba9fac4ffadd6c90386dad4ef6ecedba5a88d1fb9f1dffbe2fe0c6fdfceac`; evidence packet `c7ad8dfaa774fada4afe2edd0906fd8ec0fec0e098d305001a7faaa0b1d74a4e`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `16bedbe4dbfd4f958fe9c55080a5dcffbcb78548d7aef04de4f9dbfa99f7fe72`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R73: Future Perfect's shared Overcharge cooldown

Base manifest `fc3e3cb0fe4e13a5b8128d3a027031197a068a1b5f6d2376f6eef7ec5077f0ab`; overlay plan `d3ab37ff0041900f0ab2116c49a8687e93609b55d35c55277eaca2166a868828`; evidence packet `39cc370ee5567e39413c6d0cbcafd198a49381e9aec3ad963fc236e6a2963231`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `692bee0585415da4d314414d669e1a263bd0401042ec355c5cc2eab13e0d6725`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R74 to R76: Prototype colour, Plague of the Outcasts and Tag Team

Base manifest `dbec840e49a1cae8cbfcd1369af9a40469552b948732a1f599352872376d4b00`; overlay plan `202ae44140f8d1b6b87df2a288e5f17db96eb72ff1a4235c8f88e10e32922cc0`; evidence packet `8d81c1af56988a546f197fbc3594197661ffc16026760a7e28542b651882013f`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `263e8eb69197f598d1a5aa486c3e36193be980a8652f7c22cd58bf14a2d387a1`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R77: Skilled's rows

Base manifest `ae7173bcce5a1d505ece5d7caf357ccb78ef83213774878a578a7a3217b37ef5`; overlay plan `af725a2775a73f02a336559c2e1c905dc8643f0ce2ea82581140472151c5d5ed`; evidence packet `3c2b037e496663207998b123ac440f87d4e276fe9f7c10369bff7dee377a857e`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `c6b29d129144cf812cc2cbb9b548c749354f029ffb9b82d88731a302f39531a4`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## Unmodeled talents: Safeguard settled

Base manifest `b298b924ebab7adc10e9164af24d4ee7884c4856cf2a7d7ca167ce3d8ab61101`; overlay plan `94b5534259741ebb2a89611b9f83d32e203fcd316882cd4f41ceaed75fa33805`; evidence packet `f9070e140096bdca08f6d0265c1ac1382f00da1f7f8828fe2f376b4c64a0d404`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `617b8356333f367dde84a02c6b622d79771625c45386318028e47bf4255af719`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R78 to R80: Prototype and Exotic colours, and Calculated's rows

Base manifest `6be5312958219c111b6e2fedbf30b3a900f9f9026c6c22b8877e5dc8a0f6bf05`; overlay plan `ab70d826d765a791391c7741b21345c2a9a95a286219d02fe2d6958a053999b3`; evidence packet `b85016d20d960f2d3d6b9fae201e4ee709a24fa5147b43ae25daf6120cac7397`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `2e805280d87c0c7f6c6a62b73a5ab359933f8bdd921147e263bf41fe129e9bd0`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R82 to R84: Skilled's banked reset, Efficient's source, and skill kills for weapon talents

Base manifest `ab5d4860ba521bffd2469a80ed6c76a46790a37debb29b74cdbbe91a52805874`; overlay plan `4f9510efbeb1d111a3e6d4832210a24598e60eba2d5c8201d70ec4e1bcaa67e0`; evidence packet `63540ecfe27b8395834308a765173669100514b3f7e2b8b38fbcd130ea329cab`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `1e3aab60b0d2786838df6c3d0959d1c579c2940b95e1c4d8b3076d90bad523d4`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R85: Efficient's rows, the specialization kit bonus, and skill kills for Outsider and Preservation

Base manifest `a96d2739c0551c6eb235544bb4559f9fe7d77a9959a64d2f08cf4578b90b0f88`; overlay plan `ed17283d6c137f26a59b9467ba9fe667db70931fe36b4e626a77a5328c96db6e`; evidence packet `32ede4e0f80e30ed5e220d0bdb702e5ea3591f2db7e9419efe7a58a25ab545e5`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `28e19acfbbe82d9ec7bdf9a191873fb35205a9b36417cbb78afda1a8a550e1ac`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R86: Ignited's skill kills, and Outsider's cooldown per talent

Base manifest `0652247f6ddd43d739dae0bb8bb137620d6837b4e26aca8302ba558ea271df1d`; overlay plan `47b6666ec48c7d140c46fd65d267d65fd092aa337a641ac5bf513464497a8047`; evidence packet `de7f1ac0e90b6f00ca754cc72be71dc678eedc79bfe33315eed967312236a241`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `f9cfdc49dc8da7c34c29560d8830f6de3df05abea5f75a0e82f159cdb3e71d51`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R88 to R91: Regicide's blast, Creeping Death's spread, and Plague's and Indirect Transmission's pass-ons

Base manifest `d8ebcca4ad1de18a9a6aec0a23512cdd1ee164c0cd32ab4e100ebb7c58722c28`; overlay plan `f8469e64138bdc53e29ecefdf9c9175bd6f5e8b042858f9081238310b8678c45`; evidence packet `a53bc7c2e0f0502d94bb1bdef3253cfdee6ff5f4ace6878cf610189d9a45fc4c`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `bcb07a0b47aaae46228358f0aa43af5af314e0a74627cc37cae3e43acebbf407`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R92: Unnerve's marks, on a blast's mark, and R93's floor for Regicide's blast

Base manifest `61d7b4cd9803532f9e091171fd26dc56a040709553070c625d301c215f9a260c`; overlay plan `4dca6ed39df50077fd5547250e5521f14c6898b72cbdf48d0c449e859e9e8bef`; evidence packet `195022096816d6552221f09b61869466f47aaf19a260d94d73cdbbb430413e87`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `b6297b8e97120afcb3e28afa0037e0fdce7c1efb39b260330e74d12b6a4327e7`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R95 and R96: Bewildered's redirect, and Unnerve's assumption text

Base manifest `95e07f702968663823cb6ae49877c5a881fcb7c2a824e51c42f4d25e39b1d946`; overlay plan `d3e66d25123774dc0ddf4d30e2c22dbbb1194d81e6ca03aeebf7925a51607d01`; evidence packet `fb284a9b303e204151c6dc840001664366029995a2bb67386c73a0990be91d81`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `e3d7a598e37eab4da31f0ff48c1a5b44de53c2edb8325332a879a45a4229af08`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R97: The Trap's row

Base manifest `75c63a162e9da19ec61692073dbf315489a4af962e5b10939bb59d519c5bdce7`; overlay plan `e9c07c5fef004748d43cd43fd3472de0cc89f11f7f9a20f9f71a0729504370e6`; evidence packet `2e190443d2b76240d3d2cb64e43a1db2d14811205d991e961c34ef693b135e09`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `d246327c8cd67bfb457567d2c7e463b5580e9fd277926f2df4a9daab3c227372`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R100: Indirect Transmission's row reading Proliferation's stats

Base manifest `97dede892860c345ed9e58170b3d20c4c09840d4c104ab407f0d32c64ccfe90a`; overlay plan `2ac24613d8359fb744f6a12c87b24d60c055642af6e0c129be28caf0df707dce`; evidence packet `a6ffd8473df4cf228430e476b2a60c974fd5e89d898bd58dd65b10fcfffafacb`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `9313f220e813e0d7d775a9b2d68ba4d1c271828be07068bd7a8e25799a50af36`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R101: Dragon's Glare's 20 m range on the row's within

Base manifest `80b7eaae787c0eb494729c69e075312da309184d952d1eb24e500b1792b82495`; overlay plan `7e09b782e98a4dd4deaa0a42286d5d52d7d12a8a21b658b8bf26f4e65f4d1867`; evidence packet `7274cffd8c4a09d2f30d3200373b9693c7fdbbab1d4e71631f8396ca0285a603`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `4487134ffe79653bac420ac32f97499747c809c235af5d1a13a4a6473c1e0e67`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R102: Indirect Transmission's stat descriptions

Base manifest `ee914b6d7f33bcab348bea6f8337e5fa5a418c7d4e50a24e58ec62ba217363d2`; overlay plan `283193b5eb56eeb0b40733eb08a120c576f652d4676e7cd5be167a4d9cf52212`; evidence packet `20ce66996d5fd7de5b802dc9e4eb038e4357fc389bab1e315e11a8d5b9cd92da`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `23855d00b92186bfc45ed9e452f62b8ef4d8773fe6c117bc943573638a382fa0`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R105: skill statuses reaching other enemies

Base manifest `c34e4ca0b4e09567cbb9aa6dbac01fb81ea82415be7a39929a8c6171beacb155`; overlay plan `1cbc50687f27337de3e200af90b5d72f9ac923e8f3fa4a56f6bbee85bf065c71`; evidence packet `d0e60c0927d11fdd3a82a158caf345dc03f6574afb807d25ec1b6fec691c8986`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `ed2253d121120096ec8bd63ddb839a04aafa67d68e7454d7f0644c9dd530ee83`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R106: coverage after skill status copies follow the source

Base manifest `101ca393bd91b3f0c6d1f43d599a8b15ebc79622c20f27bf4b3be4b65fa2b614`; overlay plan `0c50536d31113b7e485e3d6e8e0653f4de3a04c6aabe1292591c4d087b7b0263`; evidence packet `ab3994d3b0e25de3c4ebc2ded420ae8082191c7d62c6fd36225337b3c056bab1`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `dd52f5297e7582b5082993fbb8749483aa3d96d1e004b6f8f287c4dc8228e8a4`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R107: skills reaching several enemies

Base manifest `a9b8bea59ccf9e7779459f1e87e7c16e90eece5f50d43851f234148611251cd2`; overlay plan `5cc2af6d7016c2cde0a06e14a331e302ec0665915a6f22172dce7f8830f2e34e`; evidence packet `c85b44e89e0106d053589802c49fd0a3b9798cb91794eacf5bb31fa45e880d5d`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `fbc0873f7fb1ad3f8ef9412131a4697bbd9ceffabeb659e8b2600c010e26e471`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R108: an area's falloff

Base manifest `259d28598376665b511d6c57f504c364f2675c308b911922557eda5d7133c5c8`; overlay plan `6da6185071193b432fee8718532711abd312f9caeafa50f5ca68283d44c3e51e`; evidence packet `fad5aa46ed203bd275968a69c38dc81bd8f0e547598d35e0c44e4e8f536f638b`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `2b5910fd209a5c5e0b92de004da0ed6f471603f23584d25fd3f490e3db0b0df1`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.


## R109: three text fixes

Base manifest `48835f01d8d16e951fe71a0a970dbf05513631a18b49b0bc5b79b3a36f945c1b`; overlay plan `54bd8f317e23da04ab5ed8c12deec1e3515af1f19439ef98e57817d47d5da977`; evidence packet `683fa0ac84a50cdee00d4a89acad136bc1a1e8bfbf731cd6df298e228418e157`.

Whole-row preconditions and source evidence are captured in this assembly. This is a reviewed authored-model proposal, not runtime verification or consumer adoption. Unresolved behavior is explicitly excluded in overlay-plan.json. Artwork, share codes and all unselected model rows are preserved. Source archive defaults are not applied.


## Verified artwork packaging

Seed manifest SHA-256: `96aa5101a08699838d030c5033e8af513f8c4a129fb7a4370031d1494c908a11`. Assets digest: `5d935a3700a533d6cd8549b93fd9045ecb34ee0937fb50f9b73353459203ef3d`.

Registered artwork copied unchanged. Source attribution, licenses and preparation notes remain verbatim in presentation.json; hashes do not establish artwork rights.
