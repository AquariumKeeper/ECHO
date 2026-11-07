# In-game checklist for testers

The planner's numbers come from sources that sometimes disagree.
One reading on the item card or stats screen usually
settles it. Values below are for the bundled pack
(`y8s3.3-pve.2026-11-07.r73.candidate.1`).

## How to report a reading

For each check, send:

- the game version (title screen or patch notes);
- the item's name, and a screenshot of its card or the stats screen;
- every other bonus that could change the value: gear attributes, mods, talents,
  brand and set bonuses, SHD Watch and specialization. Removing them first, where
  the check says so, makes the reading much easier to use;
- for timing checks, a short video if you can. Frame-by-frame timing beats a
  stopwatch.

## New in this release

Most skill changes in this build come from the game files, with no in-game reading yet.
These matter most. Read them on the skill screen, and note the bonus you used:

- **Hive health:** read a Stinger Hive's health with no Skill Health, then with a known
  amount. The planner counts Skill Health twice, as the game's formula does:
  `(1 + tier + bonus) × (1 + bonus)`. That looks odd, so a reading would settle it.
- **Leech Hive:** read its damage with no Skill Damage, then with a known amount. The
  planner gives it no bonus.
- **Gas Bomber:** read its poison damage and duration, with and without Skill Damage and
  Status Effects. The planner gives them no bonus, and this is its weakest reading.
- **Bleeds:** read a Stinger Hive or Explosive Seeker bleed tick, with and without Skill
  Damage. The planner now adds Skill Damage.
- **Explosions:** read a Firestarter or Airburst explosion, with and without Status
  Effects. The planner now adds Status Effects.
- **Banshee Pulse:** read its range and cone at tier 3. The planner has 39 m and 26°.
- **Energize at tier 6:** at Skill Tier 6 with Energize, read the skill screen when it
  fires, and a Burn Sticky Bomb's cooldown. The planner gives tier 7 and Overcharge's
  values, from the game files.
- **Sniper Turret's shots:** deploy one and fire every shot as fast as it allows, timing
  each, then time from the last shot to the skill being ready. The planner has 3 s
  between shots, then 2 s and a 20 s cooldown at no Skill Haste.
- **Wicked on a running bleed:** with Wicked and Bleeding Edge, shoot one enemy steadily
  within 15 m for 30 s. Watch the bleed's icon and ticks, and time Wicked's bonus. Three
  outcomes are possible: the bleed ends at its duration and restarts, with Wicked staying
  up; the bleed runs on and Wicked ends at 20 s, which is what the planner does; or the
  bleed runs on and Wicked stays up.
- **Perpetuation on a running status:** with Perpetuation on a weapon with a repeating
  status, get the bonus by a headshot while the status is on the enemy. Read the ticks
  before and after, note when the bonus icon goes, and time the cooldown from then. Then
  kill that enemy and read the first and later ticks on the next one. The planner keeps
  running ticks as they were, the bonus goes when a new status starts, and that status
  keeps the boost for as long as it runs.
- **Stinger Hive's charges:** deploy a full Stinger Hive against one enemy and count the
  stings until it powers down, then time how long until it's full again with the skill put
  away. Note whether it refills while out. The planner has 8 stings at tier 0, then 80 s.
  This number changed a lot, so a reading matters.
- **Gaps between shots:** time two chem launcher shots from held charges, fired as fast as
  possible, then two Artillery Turret shells the same way. Then watch a Cluster Seeker's
  mines reach one enemy and time how far apart they land. The planner assumes 2 s, 2 s and
  0.5 s, and none of the three comes from the game files.
- **Future Perfect's Overcharge:** at Skill Tier 6 with Future Perfect, kill an enemy and
  read the skill screen for tier 7, then time how long it lasts and how long until a kill
  grants it again. Then try at tier 5 with Energize up. The planner gives 15 s, then 90 s
  from that grant, and counts Energize's Overcharge toward the 6. Also note whether the
  kill that brings you to tier 6 grants it, or only the next one: the planner says the next.
- **Burns on enemies that die fast:** a burn now ends with the enemy it's on, so Burn
  Sticky Bomb, Firestarter and Airburst Seeker lose most of their damage against enemies
  that die at once. If a burn carries on to the next enemy in the game, report it.

## Still open from earlier releases

- **Shield mods:** equip a shield with one Circuit Board, Gyro or Hard Coating mod
  and read the shield's health at a known Skill Tier, then again without the mod.
  The planner adds the mod to the shield's scaling, `(1 + curve + mod)`; the
  source data multiplies, `curve × (1 + mods)`. The two readings settle which.
- **Crusader and Striker shields:** check whether you can fire your sidearm while
  holding them.
- **The Ravenous:** read the magazine on the weapon card. It fires two bullets a
  round; the planner counts one until the card's magazine is known.
- **Burn and bleed ticks:** with a weapon talent that burns or bleeds an enemy,
  record a video of the damage numbers from one application, so the interval and
  per-tick damage can be read frame by frame. The planner ticks these from the
  game files' values, so a video checks them.

## Weapon talents and stats

Unless a check says otherwise, take off weapon mods and gear that add the same
stat, so the talent is the only bonus.

| Item | What to read | Research says | Community says |
| --- | --- | --- | --- |
| **Rock n' Roll** (Perfect Extra) | Magazine size on the weapon card. Base magazine is 20. | **30** (+50%) | **26** (+30%) |
| **New Reliable** (Perfect Optimized) | The weapon handling stat itself, not reload time or stability, which handling feeds through different formulas. | **+40%** | **+20%** |
| **The Send-Off** (Perfect Distance) | Optimal range on the weapon card. Base is 16 m. | **32 m** (+100%) | **20 m** (+25%) |
| **Safety Distance** | Time a reload with rounds still in the magazine (a tactical reload), with no reload speed bonuses. | **2.79 s** (and 2.09 s from empty) | no tactical value |
| **Sweet Dreams** | Optimal range with a total optimal-range bonus of +30% or +40%. Its fixed Stunner mod gives +25%, so add +5% or +15% more. With only Stunner, both sources show 13 m, so that card doesn't settle it. | **13.0 m** at +30%, **14.0 m** at +40% | **13.5 m** at +30%, **14.6 m** at +40% |
| **Busy Little Bee** | Optimal range with Bee's Focus. | **18 m** expected | – |

## Situational talents

These need the talent's condition to trigger, so a short video helps most.

- **Bober (Perfect Entrench):** with armor below 30%, land a headshot from cover.
  Read how much armor comes back, as a share of your maximum armor. Research says
  **30%**, community says **20%**. The 30% armor threshold is a separate value.
- **Achilles or Instigator (Perfect Soft Spot):** destroy a weak point and note the
  weapon damage bonus and how long it lasts. Research says **32% for 15**,
  community says **24% for 15 s**. Please also confirm that the 15 is seconds.
  Note your other weapon damage bonuses, since they add together.
- **Vigil (Perfect Versatile):** with an assault rifle or LMG, swap between two
  different weapons and shoot enemies 15–25 m away. Read the damage amplification.
  Research says **15%**, community says **20%**, for 10 s.
- **Sleight (Perfect Protected Reload):** needs a teammate. While they reload, read
  the bonus armor you give them, per blue (defensive) core attribute you have.
  Research says **5% per core**, community says **3% per core, up to 18%**. The
  research shows no cap, so six cores would give 30%.

- **Ignited:** after 4 kills, time the burn on the next enemy you hit, with no
  Status Effects bonus. Research says **7 s** on enemies (players burn for 5 s).
  The planner's simulator uses 7 s.

## Prototype gear

The planner models Prototype gear from the official notes and community data; a
few values still need a reading.

- **Upgraded item maxima:** upgrading an item to Prototype rolls one attribute at
  the new maximum. If that attribute is one of these, read its value. Each
  settles how the game rounds 1.5 × the old maximum:

  | Attribute | Planner shows | Exactly 1.5 × |
  | --- | --- | --- |
  | Critical Hit Chance (weapon minor) | 14.5% | 14.25% |
  | Health Damage (weapon minor) | 14.5% | 15% |
  | Armor Regeneration (gear minor) | 7,388 | 7,387.5 |
  | Health (gear minor) | 28,403 | 28,402.5 |
  | Skill Haste (gear minor) | 15% | 18% |
  | Reload Speed on Sharpshooter's 93R | 14% | 13.95% |
  | Health Damage on Firestarter | 11.3% | 11.25% |

  Research lists Skill Haste at 18%, so its reading matters most.
- **Skill Tier:** with two Prototype Skill Tier cores equipped, read the Skill
  Tier. The planner counts 1.5 per item and rounds the total down. One Prototype
  core (tier 1) and one Prototype plus one ordinary core (tier 2) already match;
  two Prototype cores should show tier 3.
- **Augment values:** read a Quantum and an Anomaly augment's percentage at a
  known level. The planner uses Quantum 1.6% at level 1 rising 0.3% per level,
  and Anomaly 4% rising 0.5% per level. Older patch notes give different
  figures.

## Skill timing

These feed the planner's skill DPS. Note the skill tier, mods and Expertise grade,
and what you were shooting at.

- **Striker Drone:** shots per burst, how long it fires, and the pause between
  bursts, recorded separately. It may or may not match the Assault Turret.
- **Sniper Turret:** the time between shots you trigger, and its recharge time.
- **Incinerator Turret:** the interval between damage ticks. Keep direct flame
  hits, burn refreshes and burn ticks apart. The community suggests **0.5 s**, but
  nobody has measured it yet.

## Newly modeled talents

- **Perfect Empathic Resolve:** after repairing yourself, read your weapon damage
  bonus at a known Skill Tier. The planner gives 2.3% plus 2.333% per tier
  (16.3% at tier 6).
- **Alternating Current:** at 10 stacks, check whether both skills or only the
  stacked one deal more damage. The planner applies the stacks to both.
- **Sledgehammer:** with a grenade mark on a target, check whether the extra armor
  damage also applies to your other weapon. The planner applies it only while the
  Sledgehammer weapon is out.

- **Boomerang:** fire a full magazine at a target and count the rounds fired before
  reloading, with a known critical hit chance. The planner expects the magazine to
  last 1 / (1 - 0.5 x crit chance x hit rate) times as long.
- **First Blood:** with an 8x scope, check whether the first shot after any reload
  counts as a headshot, or only after reloading from empty as the text says.

- **Headhunter:** after a headshot kill, read the damage of your next hit and the
  killing blow's. The planner adds 125% of the killing blow, capped at 800% of
  weapon damage (1,250% above 150% headshot damage).

- **Busy Little Bee:** after swapping away with 10 stacks, read your other weapon's
  damage per bullet. The planner triples it (+20% amplified per stack).

- **Abridged:** with two non-named rifles of one type and a stat talent such as
  Near Sighted on the secondary, check the primary's stability on its card. The
  planner adds the secondary talent's effect while the primary is out.

## Party buffs

The Party tab applies teammates' talent buffs. Two readings would settle how they
combine:

- **The same buff twice:** with two teammates both running Overwatch (or both
  Vanguard), read your weapon damage (or bonus armor) while both buffs are up.
  The planner applies the same buff once, the stronger copy.
- **Vanguard's share:** with a Vanguard teammate whose armor you know, deploy their
  shield and read the bonus armor you receive. The planner gives 45% of their
  total armor.

## Bigger tests

- **Damage falloff:** for a few weapons, shoot the same target at measured
  distances from optimal range out past long range, and record the body-shot damage
  number at each distance. This settles how damage drops between optimal and long
  range, and where it bottoms out. The planner doesn't model falloff until then.
- **Enemy health:** pick one enemy type at a stated difficulty and count the body
  shots a known weapon needs to break its armor and then kill it. This confirms the
  absolute health and armor the planner uses for time to kill.
