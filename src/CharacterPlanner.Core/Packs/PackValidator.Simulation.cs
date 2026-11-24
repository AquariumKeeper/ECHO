namespace CharacterPlanner.Core.Packs;

public static partial class PackValidator
{
    private sealed partial class Checker
    {
        // The selector a fight's cover cycle holds on (ADR 0009; the simulator's DummyFightOptions.CoverInput).
        private const string CoverSelector = "in_cover";
        private static readonly HashSet<string> Events = ["", "shot", "hit", "body_hit", "headshot", "crit", "kill", "crit_kill", "headshot_kill", "reload", "reload_empty", "reload_end",
            "unarmored_hit", "swap", "swap_after_kill", "armor_break", "damaged", "swapped", "skill_hit", "status_applied", "shield_break", "second", "pickup", "target_death", "crit_pellet", "hit_pellet", "target_changed", "armor_kit", "kit_ability", "skill_kill", "skill_charge", "skill_out"];
        // Poison is a damage status beside burn and bleed from 2026-11-19 (ADR 0024).
        private static readonly HashSet<string> Statuses = [.. GamePack.FightStatuses];
        private static readonly HashSet<string> TriggerKinds = ["buff", "stacks", "status", "magazine", "distance", "pellets", "ready", "lockout", "cycle", "target", "count", "bonus_armor"];

        public void Simulation()
        {
            DistanceDefaults();
            IncomingDefaults();
            EnemyStatuses();
            SharedStackTimers();
            PlayerStatusGates();
            ArmorKit();
            HeadshotCycles();
            NextHitBonuses();
            SkillsAside();
            var talents = pack.Talents.Select(talent => talent.ID).ToHashSet(StringComparer.Ordinal);
            // A rule belongs to a talent, or to a specialization node (R56). Node ids are unique only within their tree, so a rule
            // may only name one that's unique across trees and isn't also a talent.
            var nodes = pack.Specializations.SelectMany(specialization => specialization.Nodes).GroupBy(node => node.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var owners = talents.Concat(nodes.Where(node => node.Value == 1 && !talents.Contains(node.Key)).Select(node => node.Key)).ToHashSet(StringComparer.Ordinal);
            // ADR 0031: a buff hive owns the buff its charges turn on, so an active skill may own a buff rule gaining on its own charges.
            var chargingSkills = pack.Simulation.Skills.Where(rule => rule.ChargeFor.Length > 0).Select(rule => rule.Skill).ToHashSet(StringComparer.Ordinal);
            // ADR 0032: and a skill with a window, not charging, may own a buff rule on skill_out, held while its use runs. Not a repair
            // rule, whose use can end before its window, nor one whose effects start after its deployment.
            var windowSkills = pack.Simulation.Skills.Where(rule => rule.Window.Length > 0 && rule.ChargeFor.Length == 0 && rule.Zones is null && rule.Revive is null && !rule.ControlStatus && rule.Shield.Length == 0 &&
                    rule.Repair.Length == 0 && rule.ImpactDelay == 0 && rule.FarImpactDelay == 0)
                .Select(rule => rule.Skill).ToHashSet(StringComparer.Ordinal);
            owners.UnionWith(pack.ActiveSkills.Select(skill => skill.ID).Where(id => !owners.Contains(id)));
            var skillIds = pack.ActiveSkills.Select(skill => skill.ID).ToHashSet(StringComparer.Ordinal);
            var weaponSlots = pack.Slots.Where(slot => slot.Weapon).Select(slot => slot.ID).ToHashSet(StringComparer.Ordinal);
            var weaponTags = pack.Items.Where(item => weaponSlots.Contains(item.Slot)).SelectMany(item => item.Tags).ToHashSet(StringComparer.Ordinal);
            var triggers = pack.Simulation.Triggers;
            var stats = pack.Stats.Select(stat => stat.ID).ToHashSet(StringComparer.Ordinal);
            // Keys a gated status rule may read: driven buff, ready and stacks state.
            var gateKinds = triggers.Where(rule => rule.Kind is "buff" or "ready" or "stacks").GroupBy(rule => rule.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Kind, StringComparer.Ordinal);
            for (var i = 0; i < triggers.Length; i++)
            {
                var rule = triggers[i];
                var path = $"simulation.triggers[{i}]";
                if (TriggerError(rule, owners) is { } message) { Error("bad-sim-trigger", path, message); continue; }
                var skillOwned = skillIds.Contains(rule.Talent) && !talents.Contains(rule.Talent) && !nodes.ContainsKey(rule.Talent);
                static bool SkillEvent(string name) => name is "skill_charge" or "skill_out";
                if (skillOwned != SkillEvent(rule.On) || skillOwned && (rule.Kind != "buff" || !rule.Also.IsDefaultOrEmpty ||
                        !(rule.On == "skill_charge" ? chargingSkills : windowSkills).Contains(rule.Talent)) ||
                    !skillOwned && (rule.Also.Any(gain => SkillEvent(gain.On)) || rule.OtherEvents.Any(SkillEvent)))
                {
                    Error("bad-sim-trigger", path, "Only a skill owns a trigger rule: a buff gaining on skill_charge alone, of a skill whose rule has chargeFor, or on " +
                        "skill_out alone, of a skill whose rule has a window; no other rule gains on either");
                    continue;
                }
                // ADR 0033: a refill of the weapon put away on each swap belongs to a skill_out buff alone.
                // Its key is its own, so nothing else turns the refill on.
                if (rule.RefillOnSwap && (rule.On != "skill_out" || triggers.Count(other => other.Key == rule.Key) > 1))
                { Error("bad-sim-trigger", path + ".refillOnSwap", "refillOnSwap goes on a skill_out buff alone, whose key no other rule holds"); }
                // ADR 0032: a skill_out buff is held for the use, so it takes no duration of its own.
                if (rule.On == "skill_out" && (rule.Duration != 0 || rule.DurationStat.Length > 0))
                { Error("bad-sim-trigger", path + ".duration", "A skill_out buff lasts as long as its skill's use, so it takes no duration"); }
                // Held while out, it's turned on by each deployment alone: no gate, count, chance or spacing, and nothing ends it early.
                if (rule.On == "skill_out" && (rule.ConsumeOn.Length > 0 || rule.ResetOn.Length > 0 || rule.AlsoResetOn.Length > 0 || rule.CancelOn.Length > 0 ||
                    rule.RefreshOn.Length > 0 || rule.Every != 0 || rule.Chance != 1 || rule.Count != 0 || rule.Within != 0 || rule.WhileKey.Length > 0 ||
                    rule.WhileStatus.Length > 0 || rule.WhilePlayerStatus.Length > 0 || rule.WhileArmorBelow != 0 || rule.WhileStatAtLeast is not null ||
                    rule.GainAtLockoutEnd || rule.FromFirstGain))
                { Error("bad-sim-trigger", path, "A skill_out buff is held while its skill is out: no gate, count, chance, spacing, consume, reset, cancel or refresh"); }
                if (rule.ForceHeadshot && !(rule.Kind == "ready" && rule.On is "hit" or "shot" || rule.Kind == "stacks" && rule.ConsumeOn is "hit" or "shot"))
                { Error("bad-sim-trigger", path + ".forceHeadshot", "forceHeadshot needs a ready rule on hit or shot, or a stacks rule consumed on hit or shot, so the forced shot spends it"); }
                if (rule.ForceCritical && !rule.ForceHeadshot)
                { Error("bad-sim-trigger", path + ".forceCritical", "forceCritical shapes a forced headshot, so it needs forceHeadshot"); }
                if (rule.ForceWholeShot && !rule.ForceHeadshot)
                { Error("bad-sim-trigger", path + ".forceWholeShot", "forceWholeShot shapes a forced headshot, so it needs forceHeadshot"); }
                if (!double.IsFinite(rule.GainWhileHolstered) || rule.GainWhileHolstered < 0 ||
                    rule.GainWhileHolstered > 0 && (rule.Kind != "stacks" || rule.WeaponTags.IsDefaultOrEmpty))
                { Error("bad-sim-trigger", path + ".gainWhileHolstered", "gainWhileHolstered needs a stacks rule with weaponTags and a period above 0"); }
                if (GrantError(rule) is { } grantError) { Error("bad-sim-trigger", path + ".grant", grantError); }
                if (!double.IsFinite(rule.MaxMagazine) || rule.MaxMagazine < 0 || rule.MaxMagazine > 1 || rule.MaxMagazine > 0 && rule.Kind != "stacks")
                { Error("bad-sim-trigger", path + ".maxMagazine", "maxMagazine needs a stacks rule and a share of the magazine above 0 up to 1"); }
                if (rule.WhileKey.Length > 0 && (rule.Kind is not ("status" or "buff" or "ready") || rule.On.Length == 0 || rule.WhileKey == rule.Key ||
                    !gateKinds.TryGetValue(rule.WhileKey, out var gate) || rule.WhileFull && gate != "stacks"))
                { Error("bad-sim-trigger", path + ".whileKey", "whileKey needs a status, buff or ready rule with an event and another buff, ready or stacks rule's key; whileFull needs a stacks key"); }
                else if (rule.WhileFull && rule.WhileKey.Length == 0) { Error("bad-sim-trigger", path + ".whileFull", "whileFull needs a whileKey"); }
                if (!double.IsFinite(rule.WhileArmorBelow) || rule.WhileArmorBelow < 0 || rule.WhileArmorBelow > 1 ||
                    rule.WhileArmorBelow > 0 && (rule.Kind is not ("buff" or "ready") || rule.On.Length == 0))
                { Error("bad-sim-trigger", path + ".whileArmorBelow", "whileArmorBelow needs a buff or ready rule with an event and a share of max armor above 0 up to 1"); }
                if (rule.EveryGroup.Length > 0 && (rule.Kind is not ("buff" or "stacks" or "ready") || rule.Every <= 0 || rule.CooldownFromEnd))
                { Error("bad-sim-trigger", path + ".everyGroup", "everyGroup needs a buff, stacks or ready rule with an every above 0, not from its end"); }
                else if (rule.EveryGroup.Length > 0 && triggers.Where(other => other.EveryGroup == rule.EveryGroup) is var members &&
                    (members.Select(other => other.Talent).Distinct(StringComparer.Ordinal).Count() < 2 || members.Any(other => other.Every != rule.Every)))
                { Error("bad-sim-trigger", path + ".everyGroup", "everyGroup needs rules from two or more talents, all with the same every"); }
                if (!double.IsFinite(rule.Chance) || rule.Chance <= 0 || rule.Chance > 1 || rule.Chance < 1 && (rule.Kind is not ("buff" or "ready") || rule.On.Length == 0))
                { Error("bad-sim-trigger", path + ".chance", "chance needs a buff or ready rule with an event and a share above 0 up to 1"); }
                if (rule.RequiresContext is not null)
                {
                    if (rule.Kind is not ("buff" or "ready" or "stacks") || rule.On.Length == 0)
                    { Error("bad-sim-trigger", path + ".requires", "requires needs a buff, stacks or ready rule with an event"); }
                    Requires(rule.RequiresContext, path + ".requires");
                    // A fight rule always runs in a fight, so the fight context would gate nothing (ADR 0023).
                    if (rule.RequiresContext.ContainsKey(GamePack.FightContextSelector))
                    { Error("bad-sim-trigger", path + ".requires." + GamePack.FightContextSelector, "A fight rule always runs in a fight; fight_context gates modifiers only"); }
                    // The fight decides once, at its start, so a key some rule changes during it can't gate a rule.
                    foreach (var key in rule.RequiresContext.Keys.Where(key => triggers.Any(other => other.Key == key) ||
                        pack.Simulation.Skills.Any(skill => skill.Zones?.Key == key)).Order(StringComparer.Ordinal))
                    { Error("bad-sim-trigger", $"{path}.requires.{key}", $"{Q(key)} is driven by a rule, so it can't gate one; use whileKey"); }
                }
                if (rule.WhileStatAtLeast is { } statGate && (rule.Kind is not ("buff" or "ready") || rule.On.Length == 0 || !stats.Contains(statGate.Stat) ||
                    !double.IsFinite(statGate.Value) || rule.WhileKey.Length > 0 || rule.WhileArmorBelow > 0))
                { Error("bad-sim-trigger", path + ".whileStatAtLeast", "whileStatAtLeast needs a buff or ready rule with an event, a known stat, a finite value and no other gate"); }
                if ((rule.On == "target_death" || rule.Also.Any(gain => gain.On == "target_death")) && rule.Kind is not ("buff" or "stacks" or "ready" or "lockout"))
                { Error("bad-sim-trigger", path + ".on", "target_death reaches buff, stacks, ready and lockout rules only"); }
                if (rule.DurationStat.Length > 0 && (rule.Kind is not ("buff" or "ready") || rule.Duration != 0 || !stats.Contains(rule.DurationStat)))
                { Error("bad-sim-trigger", path + ".durationStat", "durationStat needs a buff or ready rule without a duration and a declared stat"); }
                if (rule.GrantAmountStat.Length > 0 && !stats.Contains(rule.GrantAmountStat))
                { Error("unknown-stat", path + ".grantAmountStat", $"Unknown stat {Q(rule.GrantAmountStat)}"); }
                if (rule.ActiveAfter != 0 && (rule.Kind is not ("buff" or "ready") || rule.On.Length == 0 || !double.IsFinite(rule.ActiveAfter) ||
                    rule.ActiveAfter is <= 0 or > SimTriggerDefinition.MaximumActiveAfter))
                { Error("bad-sim-trigger", path + ".activeAfter", "activeAfter needs a buff or ready rule with an event and a time above 0 up to 600"); }
                // Versatile's weapon types (ADR 0019): tags some weapon carries, for a type gate or a cooldown per type.
                if (!rule.WeaponTypes.IsDefaultOrEmpty && (!rule.DifferentTypes && !rule.EveryPerType ||
                    rule.WeaponTypes.Distinct(StringComparer.Ordinal).Count() != rule.WeaponTypes.Length || rule.WeaponTypes.Any(tag => !weaponTags.Contains(tag))))
                { Error("bad-sim-trigger", path + ".weaponTypes", "weaponTypes must be distinct tags carried by some weapon item, for differentTypes or everyPerType"); }
                if (!rule.WeaponTags.IsDefaultOrEmpty)
                {
                    if (rule.Kind is not ("buff" or "stacks" or "ready" or "lockout"))
                    { Error("bad-sim-trigger", path + ".weaponTags", "Only buff, stacks, ready and lockout rules take weaponTags"); }
                    else if (rule.WeaponTags.Distinct(StringComparer.Ordinal).Count() != rule.WeaponTags.Length || rule.WeaponTags.Any(tag => !weaponTags.Contains(tag)))
                    { Error("bad-sim-trigger", path + ".weaponTags", "weaponTags must be distinct tags carried by some weapon item"); }
                }
                Integer(rule.Max, path + ".max", "A trigger max");
                Integer(rule.Count, path + ".count", "A trigger count");
                Integer(rule.Gain, path + ".gain", "A trigger gain");
                Integer(rule.AtLeast, path + ".atLeast", "A trigger atLeast");
                Integer(rule.PoolMax, path + ".poolMax", "A trigger poolMax");
                for (var also = 0; also < rule.Also.Length; also++)
                {
                    Integer(rule.Also[also].Gain, $"{path}.also[{also}].gain", "An also gain");
                    Integer(rule.Also[also].Count, $"{path}.also[{also}].count", "An also count");
                }
                // A key that is not a numeric input is a condition the simulator sets: it must be a
                // declared selector, and phase values must be among that selector's values.
                selectors.TryGetValue(rule.Key, out var condition);
                if (!numericInputs.Contains(rule.Key) && condition is null)
                { Error("unknown-selector", path + ".key", $"Trigger key {Q(rule.Key)} is neither a numeric input nor a declared selector"); }
                if (rule.Kind is not ("cycle" or "distance" or "magazine")) { continue; }
                for (var phase = 0; phase < rule.Phases.Length; phase++)
                {
                    if (condition is not null && rule.Phases[phase].Value.Length > 0 && !condition.Values.Contains(rule.Phases[phase].Value))
                    { Error("unknown-selector-value", $"{path}.phases[{phase}].value", $"{Q(rule.Phases[phase].Value)} is not a value of selector {Q(rule.Key)}"); }
                }
                for (var phase = 0; phase < rule.Phases.Length; phase++)
                {
                    var value = rule.Phases[phase];
                    if (value.Value.Length == 0 || !(value.Until > 0) || phase > 0 && value.Until <= rule.Phases[phase - 1].Until || rule.Kind == "magazine" && value.Until > 1)
                    {
                        Error("bad-sim-trigger", $"{path}.phases[{phase}]", rule.Kind switch
                        {
                            "distance" => "Distance phases need values and rising distances",
                            "cycle" => "Cycle phases need values and rising end times",
                            _ => "Magazine phases need values and rising shares of the magazine up to 1"
                        });
                    }
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skills = pack.Simulation.Skills;
            for (var i = 0; i < skills.Length; i++)
            {
                var rule = skills[i];
                var path = $"simulation.skills[{i}]";
                var skill = pack.ActiveSkills.FirstOrDefault(skill => skill.ID == rule.Skill);
                if (skill is null) { Error("bad-sim-skill", path, $"Unknown skill {Q(rule.Skill)}"); continue; }
                if (!seen.Add(rule.Skill)) { Error("bad-sim-skill", path, $"Skill {Q(rule.Skill)} has two rules"); continue; }
                foreach (var id in new[] { rule.Rate, rule.Window, rule.Cooldown, rule.Impact, rule.Impacts, rule.Shield, rule.Regeneration, rule.Stock, rule.StatusRate, rule.StatusWindow, rule.ImpactInterval, rule.Area, rule.AreaFull, rule.Targets, rule.ImpactRepair, rule.KillChance, rule.Repair, rule.RepairCount, rule.RepairCloud, rule.ChargeFor, rule.Refresh, rule.DecoyHealth })
                {
                    if (id.Length > 0 && !skill.PreviewOutputs.Any(output => output.ID == id))
                    { Error("bad-sim-skill", path, $"Skill {Q(rule.Skill)} has no preview output {Q(id)}"); }
                }
                if (rule.Shield.Length > 0)
                {
                    if (rule.Cooldown.Length == 0 || rule.Rate.Length > 0 || rule.Window.Length > 0 || rule.Impact.Length > 0 || rule.Status.Length > 0)
                    { Error("bad-sim-skill", path, "A shield rule takes its health and a cooldown only"); }
                    foreach (var slot in rule.Weapons.Where(slot => !weaponSlots.Contains(slot)))
                    { Error("bad-sim-skill", path, $"Unknown weapon slot {Q(slot)}"); }
                }
                else if (rule.Weapons.Length > 0 || rule.ExcludeTags.Length > 0 || rule.Regeneration.Length > 0)
                { Error("bad-sim-skill", path, "Only shield rules take weapons, excludeTags or regeneration"); }
                // ADR 0032: a skill whose use holds a buff (skill_out) needs only its window and cooldown.
                else if (!CompleteSkillRule(rule) && !(rule.Window.Length > 0 && rule.Cooldown.Length > 0 &&
                    (rule.DecoyHealth.Length > 0 || rule.Zones is not null || rule.Revive is not null || triggers.Any(trigger => trigger.Talent == rule.Skill && trigger.On == "skill_out"))))
                { Error("bad-sim-skill", path, "A skill rule needs a window with a rate or a status and a cycle, or an impact and a cooldown"); }
                if (rule.Impacts.Length > 0 && rule.Impact.Length == 0) { Error("bad-sim-skill", path, "impacts needs an impact"); }
                if (rule.ImpactInterval.Length > 0 && rule.Impacts.Length == 0) { Error("bad-sim-skill", path, "impactInterval needs impacts"); }
                // ADR 0027: one impact on each of several enemies, the Firefly's alone; an area, repeated impacts or a status don't go with it.
                if (rule.Targets.Length > 0 && (rule.Impact.Length == 0 || rule.Impacts.Length > 0 || rule.Area.Length > 0 || rule.Status.Length > 0 || rule.Rate.Length > 0))
                { Error("bad-sim-skill", path + ".targets", "targets needs an impact, with no impacts, area, status or rate"); }
                // ADR 0028: a repair to the player at each impact, and the time from a use's last impact to its end.
                // Impacts without an interval land together as one, which would repair once for them all.
                if (rule.ImpactRepair.Length > 0 && (rule.Impact.Length == 0 || rule.Impacts.Length > 0 && rule.ImpactInterval.Length == 0))
                { Error("bad-sim-skill", path + ".impactRepair", "impactRepair needs an impact, and impacts with an impactInterval"); }
                // ADR 0030: a repair landing after its impact; a skill used only once the player's armor is below a share of its maximum.
                if (rule.RepairDelay != 0 && (rule.ImpactRepair.Length == 0 || !double.IsFinite(rule.RepairDelay) || rule.RepairDelay < 0 || rule.RepairDelay > SimSkillDefinition.MaximumUseDelay))
                { Error("bad-sim-skill", path + ".repairDelay", $"repairDelay needs an impactRepair, and is above 0 up to {SimSkillDefinition.MaximumUseDelay:G} s"); }
                if (rule.UseArmorBelow != 0 && (!double.IsFinite(rule.UseArmorBelow) || rule.UseArmorBelow <= 0 || rule.UseArmorBelow > 1 || rule.Shield.Length > 0))
                { Error("bad-sim-skill", path + ".useArmorBelow", "useArmorBelow is a share of maximum armor above 0 up to 1, and not on a shield"); }
                // ADR 0030: a repair skill repairs and deals nothing, is used only once the player's armor falls, and checks through its window.
                // ADR 0031: a buff hive's charges are timed the same way, with no armor test, each lasting chargeFor.
                if (rule.Repair.Length > 0 && rule.ChargeFor.Length > 0) { Error("bad-sim-skill", path + ".chargeFor", "A rule repairs or charges, not both"); }
                if (rule.ChargeFor.Length > 0 && (rule.RepairBelow != 0 || rule.RepairCloud.Length > 0 || rule.UseArmorBelow != 0))
                { Error("bad-sim-skill", path + ".chargeFor", "A charging rule takes no repairBelow, repairCloud or useArmorBelow"); }
                // ADR 0031: the Artificer's charges go to other skills' running uses, each adding its refresh seconds to the use.
                if (rule.ChargeSkills && rule.ChargeFor.Length == 0) { Error("bad-sim-skill", path + ".chargeSkills", "chargeSkills needs chargeFor"); }
                if (rule.Refresh.Length > 0 && !rule.ChargeSkills) { Error("bad-sim-skill", path + ".refresh", "refresh needs chargeSkills"); }
                // ADR 0034: a skill drawing fire holds a window, draws hits on its health, and deals, repairs or charges nothing; with charges
                // several decoys would be out at once, and a delay would leave it out before its effects.
                // Its cooldown runs from its end, so a decoy spent early comes back no later than one that lasts.
                if (rule.DecoyHealth.Length > 0 && (rule.Window.Length == 0 || rule.Cooldown.Length == 0 || !rule.CooldownFromEnd || rule.CooldownAfter != 0 ||
                    rule.Impact.Length > 0 || rule.Rate.Length > 0 || rule.Status.Length > 0 || rule.Repair.Length > 0 || rule.ChargeFor.Length > 0 ||
                    rule.Shield.Length > 0 || rule.Stock.Length > 0 || rule.ImpactDelay != 0 || rule.FarImpactDelay != 0 || rule.Area.Length > 0 ||
                    rule.Targets.Length > 0 || rule.KillChance.Length > 0 || rule.UseArmorBelow != 0 || rule.HolsteredEvery != 0))
                {
                    Error("bad-sim-skill", path + ".decoyHealth",
                        "decoyHealth needs a window and a cooldown from its end, and no damage, status, repair, charges, shield, stock, delay, area, targets, kill or armor wait");
                }
                // ADR 0036: a skill marking zones marks one enemy for its window and does nothing else. Its key is set in the fight, so it
                // must be a selector with only the value "true" that nothing else sets. Its use holds no skill_out buff.
                if (rule.Zones is { } zones)
                {
                    if (rule.Window.Length == 0 || rule.Cooldown.Length == 0 || rule.Impact.Length > 0 || rule.Rate.Length > 0 || rule.Status.Length > 0 ||
                        rule.Repair.Length > 0 || rule.ChargeFor.Length > 0 || rule.Shield.Length > 0 || rule.Stock.Length > 0 || rule.ImpactDelay != 0 ||
                        rule.FarImpactDelay != 0 || rule.Area.Length > 0 || rule.Targets.Length > 0 || rule.KillChance.Length > 0 || rule.UseArmorBelow != 0 ||
                        rule.HolsteredEvery != 0 || rule.DecoyHealth.Length > 0)
                    {
                        Error("bad-sim-skill", path + ".zones",
                            "zones needs a window and a cooldown, and no damage, status, repair, charges, shield, stock, delay, area, targets, kill, armor wait or decoy");
                    }
                    if (skills.Take(i).Any(other => other.Zones is not null)) { Error("bad-sim-skill", path + ".zones", "Only one skill marks zones"); }
                    if (zones.Count.Length == 0 || zones.Health.Length == 0 ||
                        new[] { zones.Count, zones.Health, zones.Factor }.Any(id => id.Length > 0 && !skill.PreviewOutputs.Any(output => output.ID == id)))
                    { Error("bad-sim-skill", path + ".zones", $"zones needs a count and a health, and these and its factor are preview outputs of {Q(rule.Skill)}"); }
                    Stat(zones.Share, path + ".zones.share");
                    Stat(zones.Multiplier, path + ".zones.multiplier");
                    if (zones.Key.Length > 0 && (!selectors.TryGetValue(zones.Key, out var marked) || marked.Values is not ["true"] || zones.Key == CoverSelector || triggers.Any(trigger => trigger.Key == zones.Key || trigger.Bank == zones.Key) ||
                        pack.Simulation.HeadshotCycles.Any(cycle => cycle.Key == zones.Key)))
                    { Error("bad-sim-skill", path + ".zones.key", "A zones key is a selector with only the value true, not the cover, that no trigger or headshot cycle holds"); }
                }
                // ADR 0037: a skill reviving its user does nothing else, and its cooldown runs from its end, which a revive brings forward.
                if (rule.Revive is { } revive)
                {
                    if (rule.Window.Length == 0 || rule.Cooldown.Length == 0 || !rule.CooldownFromEnd || rule.Impact.Length > 0 || rule.Rate.Length > 0 ||
                        rule.Status.Length > 0 || rule.Repair.Length > 0 || rule.ChargeFor.Length > 0 || rule.Shield.Length > 0 || rule.Stock.Length > 0 ||
                        rule.ImpactDelay != 0 || rule.FarImpactDelay != 0 || rule.Area.Length > 0 || rule.Targets.Length > 0 || rule.KillChance.Length > 0 ||
                        rule.UseArmorBelow != 0 || rule.HolsteredEvery != 0 || rule.DecoyHealth.Length > 0 || rule.Zones is not null)
                    {
                        Error("bad-sim-skill", path + ".revive",
                            "revive needs a window and a cooldown from its end, and no damage, status, repair, charges, shield, stock, delay, area, targets, kill, armor wait, decoy or zones");
                    }
                    if (skills.Take(i).Any(other => other.Revive is not null)) { Error("bad-sim-skill", path + ".revive", "Only one skill revives its user"); }
                    if (new[] { revive.Armor, revive.Seconds }.Any(id => !skill.PreviewOutputs.Any(output => output.ID == id)))
                    { Error("bad-sim-skill", path + ".revive", $"revive's armor and seconds are preview outputs of {Q(rule.Skill)}"); }
                    if (!(revive.PoolShare is > 0 and <= 1) || !(revive.DamageTaken is > 0 and <= 1) || !(revive.BleedSeconds is > 0 and <= 600) ||
                        !(revive.ImmuneSeconds is >= 0 and <= 60) || !(revive.EndAfter is >= 0 and <= 60))
                    { Error("bad-sim-skill", path + ".revive", "revive's poolShare and damageTaken are above 0 up to 1, its bleedSeconds above 0 up to 600, and its immuneSeconds and endAfter 0 to 60"); }
                }
                // Stage 2c: its pulses while holstered, each landing a lag later.
                // A hive is holstered from its cooldown's start, so one whose cooldown runs from its use, or one with charges, has no holstered
                // time to tell.
                if (rule.HolsteredEvery != 0 && (!rule.ChargeSkills || !rule.CooldownFromEnd || rule.Stock.Length > 0 || !double.IsFinite(rule.HolsteredEvery) ||
                    rule.HolsteredEvery < SimSkillDefinition.MinimumHolsteredEvery || rule.HolsteredEvery > SimSkillDefinition.MaximumRepairEvery))
                {
                    Error("bad-sim-skill", path + ".holsteredEvery",
                        $"holsteredEvery needs chargeSkills, cooldownFromEnd and no stock, and is {SimSkillDefinition.MinimumHolsteredEvery:G} up to {SimSkillDefinition.MaximumRepairEvery:G} s");
                }
                if (rule.HolsteredLag != 0 && (rule.HolsteredEvery == 0 || !double.IsFinite(rule.HolsteredLag) || rule.HolsteredLag < 0 || rule.HolsteredLag > SimSkillDefinition.MaximumUseDelay))
                { Error("bad-sim-skill", path + ".holsteredLag", $"holsteredLag needs holsteredEvery, and is 0 up to {SimSkillDefinition.MaximumUseDelay:G} s"); }
                if (rule.Repair.Length > 0 || rule.ChargeFor.Length > 0)
                {
                    if (rule.Impact.Length > 0 || rule.Rate.Length > 0 || rule.Status.Length > 0 || rule.StatusRate.Length > 0 || rule.Area.Length > 0 || rule.Targets.Length > 0 ||
                        rule.KillChance.Length > 0 || rule.Shield.Length > 0)
                    { Error("bad-sim-skill", path + ".repair", "A repair or charging rule deals no damage: no impact, rate, status, area, targets, kill or shield"); }
                    if (rule.Repair.Length > 0 && rule.UseArmorBelow <= 0) { Error("bad-sim-skill", path + ".repair", "A repair rule is used only once the player's armor falls (useArmorBelow)"); }
                    // Its use's end, and so a cooldown from it, comes from its repairs; a delayed use or a cooldown after it don't go with that.
                    if (rule.ImpactDelay != 0 || rule.FarImpactDelay != 0 || rule.CooldownAfter != 0)
                    { Error("bad-sim-skill", path + ".repair", "A repair rule takes no impactDelay, farImpactDelay or cooldownAfter"); }
                    // A count ends a use; with charges, uses overlap, and a count would end none.
                    if (rule.RepairCount.Length > 0 && rule.Stock.Length > 0) { Error("bad-sim-skill", path + ".repairCount", "repairCount doesn't go with stock"); }
                    if (!double.IsFinite(rule.RepairEvery) || rule.RepairEvery <= 0 || rule.RepairEvery > SimSkillDefinition.MaximumRepairEvery)
                    { Error("bad-sim-skill", path + ".repairEvery", $"repairEvery is above 0 up to {SimSkillDefinition.MaximumRepairEvery:G} s"); }
                    if (!double.IsFinite(rule.RepairLag) || rule.RepairLag < 0 || rule.RepairLag > SimSkillDefinition.MaximumUseDelay)
                    { Error("bad-sim-skill", path + ".repairLag", $"repairLag is 0 up to {SimSkillDefinition.MaximumUseDelay:G} s"); }
                    if (!double.IsFinite(rule.RepairBelow) || rule.RepairBelow < 0 || rule.RepairBelow > 1)
                    { Error("bad-sim-skill", path + ".repairBelow", "repairBelow is a share of maximum armor up to 1 (0 for below full)"); }
                }
                else if (rule.RepairEvery != 0 || rule.RepairLag != 0 || rule.RepairBelow != 0 || rule.RepairCount.Length > 0 || rule.RepairCloud.Length > 0)
                { Error("bad-sim-skill", path + ".repair", "repairEvery, repairLag, repairBelow, repairCount and repairCloud need a repair or chargeFor"); }
                // ADR 0029: an armor-only impact; a delay from a use to its effects, longer beyond a distance; a cooldown starting after the use.
                if (rule.ArmorOnly && (rule.Impact.Length == 0 || rule.Shield.Length > 0)) { Error("bad-sim-skill", path + ".armorOnly", "armorOnly needs an impact"); }
                static bool UseDelay(double value) => double.IsFinite(value) && value >= 0 && value <= SimSkillDefinition.MaximumUseDelay;
                if (!UseDelay(rule.ImpactDelay) || !UseDelay(rule.FarImpactDelay) || !UseDelay(rule.CooldownAfter))
                { Error("bad-sim-skill", path, $"impactDelay, farImpactDelay and cooldownAfter are 0 up to {SimSkillDefinition.MaximumUseDelay:G} s"); }
                if ((rule.FarImpactDelay != 0 || rule.FarFrom != 0) && (rule.ImpactDelay == 0 || rule.FarImpactDelay == 0 || !double.IsFinite(rule.FarFrom) || rule.FarFrom <= 0 || rule.FarFrom > 100))
                { Error("bad-sim-skill", path + ".farFrom", "farImpactDelay and farFrom (above 0 up to 100 m) go together, beside an impactDelay"); }
                // A cooldown from the use's end would start before a delayed use's effects land.
                if ((rule.ImpactDelay != 0 || rule.CooldownAfter != 0) && (rule.Shield.Length > 0 || rule.Stock.Length > 0 || rule.CooldownFromEnd))
                { Error("bad-sim-skill", path, "impactDelay and cooldownAfter don't go on a shield, a stocked skill or a cooldown from the use's end"); }
                // ADR 0029: an outright kill names its chance, the enemy profiles it reaches, once each, and its delay, beside an impact.
                var targetProfiles = pack.NumericContextInputs.FirstOrDefault(input => input.ID == "enemy_target")?.Options.Select(option => (int)option.Value).ToHashSet() ?? [];
                if ((rule.KillChance.Length > 0 || !rule.KillProfiles.IsDefaultOrEmpty || rule.KillDelay != 0) &&
                    (rule.KillChance.Length == 0 || rule.Impact.Length == 0 || rule.KillProfiles.IsDefaultOrEmpty || rule.KillProfiles.Distinct().Count() != rule.KillProfiles.Length ||
                     rule.KillProfiles.Any(profile => profile <= 0 || !targetProfiles.Contains(profile)) || !double.IsFinite(rule.KillDelay) || rule.KillDelay < 0 ||
                     rule.KillDelay > SimSkillDefinition.MaximumUseDelay ||
                     // Once per enemy per use: one impact a use, on the enemy being shot and its area alone.
                     rule.Impacts.Length > 0 || rule.Stock.Length > 0 || rule.Targets.Length > 0))
                { Error("bad-sim-skill", path + ".killChance", $"killChance, killProfiles (enemy_target profiles, once each) and killDelay (0 up to {SimSkillDefinition.MaximumUseDelay:G} s) go together, beside one impact a use (no impacts, stock or targets)"); }
                if (rule.CooldownAfter != 0 && rule.CooldownFromEnd) { Error("bad-sim-skill", path + ".cooldownAfter", "cooldownAfter is for a cooldown from the use; use endDelay from its end"); }
                if (rule.EndDelay != 0 && (!rule.CooldownFromEnd || !double.IsFinite(rule.EndDelay) || rule.EndDelay < 0 || rule.EndDelay > SimSkillDefinition.MaximumEndDelay))
                { Error("bad-sim-skill", path + ".endDelay", $"endDelay needs a cooldown from the use's end, and is above 0 up to {SimSkillDefinition.MaximumEndDelay:G} s"); }
                // An area is an impact's (ADR 0016), a control status's (ADR 0017) or a status window's (a cloud, ADR 0024); a rate or a
                // shield stays on one enemy.
                if (rule.Area.Length > 0 && rule.Impact.Length == 0 && !rule.ControlStatus && rule.StatusRate.Length == 0)
                { Error("bad-sim-skill", path + ".area", "An area needs an impact, a control status or a status window (statusRate)"); }
                else if (rule.Area.Length > 0 && rule.Shield.Length > 0) { Error("bad-sim-skill", path + ".area", "A shield takes no area"); }
                // An area's falloff (R107): the full-damage radius and the share at the edge go together, on a rule with an area.
                if ((rule.AreaFull.Length > 0 || rule.AreaEdge != 0) && (rule.Area.Length == 0 || rule.AreaFull.Length == 0))
                { Error("bad-sim-skill", path + ".areaFull", "areaFull and areaEdge go together, on a rule with an area"); }
                else if (rule.AreaFull.Length > 0 && rule.Impact.Length == 0)
                { Error("bad-sim-skill", path + ".areaFull", "A falloff is an impact's: a control status goes on whole"); }
                else if (rule.AreaFull.Length > 0 && (!double.IsFinite(rule.AreaEdge) || rule.AreaEdge is <= 0 or > 1))
                { Error("bad-sim-skill", path + ".areaEdge", "areaEdge is the share at the area's edge, above 0 up to 1"); }
                if (rule.Status.Length > 0 && !Statuses.Contains(rule.Status)) { Error("bad-sim-skill", path, $"Unknown status {Q(rule.Status)}"); }
                // A control status goes on for the window's duration (ADR 0017), so it needs a control status and a window.
                if (rule.ControlStatus && (rule.Status is not ("shocked" or "disoriented" or "ensnared" or "blinded" or "disrupted") || rule.Window.Length == 0))
                { Error("bad-sim-skill", path + ".controlStatus", "controlStatus needs a control status (shocked, disoriented, ensnared, blinded or disrupted) and a window"); }
                else if (rule.ControlStatus && rule.Shield.Length > 0) { Error("bad-sim-skill", path + ".controlStatus", "A shield takes no controlStatus"); }
                // ADR 0035: an area measured from the player is a control status's alone; an impact's or a cloud's stays the enemy being shot's.
                if (rule.AreaFromPlayer && (!rule.ControlStatus || rule.Area.Length == 0 || rule.Impact.Length > 0 || rule.Rate.Length > 0 || rule.StatusRate.Length > 0))
                { Error("bad-sim-skill", path + ".areaFromPlayer", "areaFromPlayer goes on a control status with an area, and no impact, rate or status damage"); }
                if (rule.Stock.Length > 0 ? rule.Cooldown.Length == 0 || rule.CooldownFromEnd || rule.Shield.Length > 0 || !double.IsFinite(rule.StockInterval) || rule.StockInterval <= 0 : rule.StockInterval != 0)
                { Error("bad-sim-skill", path, "stock needs a cooldown that doesn't run from a use's end, and a positive stockInterval"); }
                if ((rule.StatusRate.Length > 0 || rule.StatusWindow.Length > 0 || rule.StatusArmorBonus.Length > 0) &&
                    (rule.StatusRate.Length == 0 || rule.StatusWindow.Length == 0 || !DamagingStatuses.Contains(rule.Status)))
                { Error("bad-sim-skill", path, "statusRate and statusWindow go together, on a rule with a burn, bleed or poison"); }
                if (rule.StatusArmorBonus.Length > 0) { Stat(rule.StatusArmorBonus, path + ".statusArmorBonus"); }
                // A skill's poison on other enemies (an area, a spread, a pass-on) ticks by its statusDamage row, so a poison skill needs one;
                // burn and bleed rows are already required wherever copies tick.
                if (rule.Status == "poisoned" && !pack.Simulation.StatusDamage.Any(damage => damage.Status == "poisoned"))
                { Error("bad-sim-skill", path + ".status", "A skill's poison needs a statusDamage row for poisoned, for its copies on other enemies to tick"); }
                // A repair rule's amount may be read live too (ADR 0031, the owner's call after R151: the Fixer's), each check reading it.
                if (rule.RateRead.Length > 0 && (rule.RateRead != "live" || rule.Rate.Length == 0 && rule.Repair.Length == 0))
                { Error("bad-sim-skill", path, "rateRead is live, and needs a rate or a repair"); }
                if (rule.RateArmorBonus.Length > 0)
                {
                    if (rule.Rate.Length == 0) { Error("bad-sim-skill", path, "rateArmorBonus needs a rate"); }
                    Stat(rule.RateArmorBonus, path + ".rateArmorBonus");
                }
                // ADR 0027: a gate on the user's settings, read once for the fight. A shield is placed by the fight's own scheduler.
                if (rule.RequiresContext is not null)
                {
                    Requires(rule.RequiresContext, path + ".requires");
                    if (rule.Shield.Length > 0) { Error("bad-sim-skill", path + ".requires", "A shield takes no requires"); }
                    if (rule.RequiresContext.ContainsKey(GamePack.FightContextSelector))
                    { Error("bad-sim-skill", path + ".requires." + GamePack.FightContextSelector, "A fight rule always runs in a fight; fight_context gates modifiers only"); }
                    // The gate is read once, so a key the fight sets as it goes can't be one: a trigger's, a headshot cycle's (ADR 0021),
                    // a zone mark's (ADR 0036), or the cover a cover cycle holds.
                    foreach (var key in rule.RequiresContext.Keys.Where(key => pack.Simulation.Triggers.Any(other => other.Key == key) ||
                        pack.Simulation.HeadshotCycles.Any(cycle => cycle.Key == key) || skills.Any(other => other.Zones?.Key == key) || key == CoverSelector).Order(StringComparer.Ordinal))
                    { Error("bad-sim-skill", $"{path}.requires.{key}", $"{Q(key)} is driven in fights, so it can't gate a skill"); }
                }
            }
            var damaged = new HashSet<string>(StringComparer.Ordinal);
            var statusDamage = pack.Simulation.StatusDamage;
            for (var i = 0; i < statusDamage.Length; i++)
            {
                var rule = statusDamage[i];
                var path = $"simulation.statusDamage[{i}]";
                if (!DamagingStatuses.Contains(rule.Status)) { Error("bad-sim-status-damage", path + ".status", $"Status {Q(rule.Status)} deals no damage over time"); }
                else if (!damaged.Add(rule.Status)) { Error("bad-sim-status-damage", path + ".status", $"Status {Q(rule.Status)} has two damage rules"); }
                Stat(rule.Tick, path + ".tick");
                if (rule.ArmorBonus.Length > 0) { Stat(rule.ArmorBonus, path + ".armorBonus"); }
                if (!(rule.Period >= MinimumTickPeriod && rule.Period <= MaximumTickPeriod))
                { Error("bad-sim-status-damage", path + ".period", $"A tick period must be from {MinimumTickPeriod} to {MaximumTickPeriod} seconds"); }
            }
            // A talent's own damage over time (Toxic Delivery): the total splits into equal ticks, one per period.
            var damagingTalents = new HashSet<string>(StringComparer.Ordinal);
            var talentDamage = pack.Simulation.TalentDamage;
            for (var i = 0; i < talentDamage.Length; i++)
            {
                var rule = talentDamage[i];
                var path = $"simulation.talentDamage[{i}]";
                if (!talents.Contains(rule.Talent)) { Error("bad-sim-talent-damage", path + ".talent", $"Unknown talent {Q(rule.Talent)}"); }
                else if (!damagingTalents.Add(rule.Talent)) { Error("bad-sim-talent-damage", path + ".talent", $"Talent {Q(rule.Talent)} has two damage rules"); }
                if (rule.On.Length == 0 || !Events.Contains(rule.On)) { Error("bad-sim-talent-damage", path + ".on", "Talent damage needs a simulator event"); }
                Stat(rule.Total, path + ".total");
                var ticks = rule.Duration / rule.Period;
                if (!(rule.Period >= MinimumTickPeriod && rule.Period <= MaximumTickPeriod) || !(rule.Duration > 0 && rule.Duration <= 600) ||
                    Math.Abs(ticks - Math.Round(ticks)) > 1e-9)
                { Error("bad-sim-talent-damage", path + ".period", $"A talent damage period must be from {MinimumTickPeriod} to {MaximumTickPeriod} seconds and divide a duration up to 600"); }
                if (rule.MaxStacks is < 1 or > MaximumTalentDamageStacks)
                { Error("bad-sim-talent-damage", path + ".maxStacks", $"A talent debuff's stacks must be from 1 to {MaximumTalentDamageStacks}"); }
            }
            // A copy of a skill's own burn or bleed ticks as the player's (ADR 0015), so both need their statusDamage rows.
            bool SkillCopiesTick() => pack.Simulation.StatusDamage.Any(damage => damage.Status == "burning") && pack.Simulation.StatusDamage.Any(damage => damage.Status == "bleeding");
            const string SkillCopiesTickMessage = "skillStatuses needs statusDamage rows for burning and bleeding, for the copies to tick";
            // A talent's debuff or status passing on at a death (ADR 0013): one per talent. A debuff needs the talent's talentDamage row
            // and goes to the nearest enemy; a status needs a share of its duration, and may go to all in reach.
            var passing = new HashSet<string>(StringComparer.Ordinal);
            var passOns = pack.Simulation.PassOns;
            for (var i = 0; i < passOns.Length; i++)
            {
                var rule = passOns[i];
                var path = $"simulation.passOns[{i}]";
                if (rule.Status.Length == 0 ? !talentDamage.Any(damage => damage.Talent == rule.Talent) : !talents.Contains(rule.Talent))
                { Error("bad-sim-pass-on", path + ".talent", rule.Status.Length == 0 ? $"Talent {Q(rule.Talent)} has no talentDamage debuff to pass on" : $"Unknown talent {Q(rule.Talent)}"); }
                else if (!passing.Add(rule.Talent)) { Error("bad-sim-pass-on", path + ".talent", $"Talent {Q(rule.Talent)} has two pass-ons"); }
                // The share may come from a stat instead (Proliferation's 75%), read when a fight starts.
                if (rule.Status.Length == 0 ? rule.Share != 0 || rule.ShareStat.Length > 0 || rule.To is not ("" or "nearest")
                    : !(Statuses.Contains(rule.Status) || rule.Status == "any") || (rule.ShareStat.Length > 0 ? rule.Share != 0 : !double.IsFinite(rule.Share) || rule.Share is <= 0 or > 1) ||
                        rule.To is not ("" or "nearest" or "all"))
                { Error("bad-sim-pass-on", path + ".status", "A status pass-on needs a known status or any, a share above 0 up to 1 or a shareStat in its place, and to nearest or all; a debuff's takes neither a share nor all"); }
                else if (DamagingStatuses.Contains(rule.Status) && !pack.Simulation.StatusDamage.Any(damage => damage.Status == rule.Status))
                { Error("bad-sim-pass-on", path + ".status", $"A pass-on's {Q(rule.Status)} needs a statusDamage row to tick"); }
                // A debuff's shareStat is refused above, whatever it names.
                if (rule.Status.Length > 0 && rule.ShareStat.Length > 0) { Stat(rule.ShareStat, path + ".shareStat"); }
                // A skill has no debuff stacks, so only a status pass-on takes its statuses (ADR 0015).
                if (rule.SkillStatuses && rule.Status is not ("any" or "burning" or "bleeding"))
                { Error("bad-sim-pass-on", path + ".skillStatuses", "Only a pass-on of any status, burning or bleeding takes skillStatuses (a skill's poison passes on with any), and a skill has no debuff stacks"); }
                else if (rule.SkillStatuses && !SkillCopiesTick()) { Error("bad-sim-pass-on", path + ".skillStatuses", SkillCopiesTickMessage); }
                // The reach likewise (Proliferation's 15 m).
                if (rule.WithinStat.Length > 0)
                {
                    if (rule.Within != 0) { Error("bad-sim-pass-on", path + ".within", "A pass-on takes within or withinStat, not both"); }
                    Stat(rule.WithinStat, path + ".withinStat");
                }
                else if (!double.IsFinite(rule.Within) || rule.Within <= 0 || rule.Within > SimPassOnDefinition.MaximumWithin)
                { Error("bad-sim-pass-on", path + ".within", $"A pass-on reaches above 0 up to {SimPassOnDefinition.MaximumWithin:G} metres"); }
            }
            // A talent's status spread to other enemies (ADR 0013): one per talent.
            var spreading = new HashSet<string>(StringComparer.Ordinal);
            var spreads = pack.Simulation.Spreads;
            for (var i = 0; i < spreads.Length; i++)
            {
                var rule = spreads[i];
                var path = $"simulation.spreads[{i}]";
                if (!talents.Contains(rule.Talent)) { Error("bad-sim-spread", path + ".talent", $"Unknown talent {Q(rule.Talent)}"); }
                else if (!spreading.Add(rule.Talent)) { Error("bad-sim-spread", path + ".talent", $"Talent {Q(rule.Talent)} has two spreads"); }
                if (!double.IsFinite(rule.Within) || rule.Within <= 0 || rule.Within > SimSpreadDefinition.MaximumWithin)
                { Error("bad-sim-spread", path + ".within", $"A spread reaches above 0 up to {SimSpreadDefinition.MaximumWithin:G} metres"); }
                if (!double.IsFinite(rule.Every) || rule.Every is < 0 or > 600) { Error("bad-sim-spread", path + ".every", "A spread's cooldown is from 0 to 600 seconds"); }
                if (rule.SkillStatuses && !SkillCopiesTick()) { Error("bad-sim-spread", path + ".skillStatuses", SkillCopiesTickMessage); }
            }
            // A talent's trap on the enemies it tagged (ADR 0013, The Trap): one per talent.
            var trapping = new HashSet<string>(StringComparer.Ordinal);
            var traps = pack.Simulation.Traps;
            for (var i = 0; i < traps.Length; i++)
            {
                var rule = traps[i];
                var path = $"simulation.traps[{i}]";
                if (!talents.Contains(rule.Talent)) { Error("bad-sim-trap", path + ".talent", $"Unknown talent {Q(rule.Talent)}"); }
                else if (!trapping.Add(rule.Talent)) { Error("bad-sim-trap", path + ".talent", $"Talent {Q(rule.Talent)} has two traps"); }
                if (!double.IsFinite(rule.TagAfter) || rule.TagAfter is < 0 or > 10) { Error("bad-sim-trap", path + ".tagAfter", "A tag takes from 0 to 10 seconds of hits"); }
                if (rule.MaxTags is < 1 or > SimTrapDefinition.MaximumTags) { Error("bad-sim-trap", path + ".maxTags", $"A trap tags from 1 to {SimTrapDefinition.MaximumTags} enemies"); }
                if (!Statuses.Contains(rule.Status)) { Error("bad-sim-trap", path + ".status", $"Unknown status {Q(rule.Status)}"); }
                else if (DamagingStatuses.Contains(rule.Status) && !pack.Simulation.StatusDamage.Any(damage => damage.Status == rule.Status))
                { Error("bad-sim-trap", path + ".status", $"A trap's {Q(rule.Status)} needs a statusDamage row to tick"); }
                if (!double.IsFinite(rule.StatusDuration) || rule.StatusDuration is <= 0 or > 600) { Error("bad-sim-trap", path + ".statusDuration", "A trap's status lasts above 0 up to 600 seconds"); }
                if (!double.IsFinite(rule.Every) || rule.Every is < 0 or > 600) { Error("bad-sim-trap", path + ".every", "A trap's cooldown is from 0 to 600 seconds"); }
                if (!double.IsFinite(rule.Cut) || rule.Cut is < 0 or > 600) { Error("bad-sim-trap", path + ".cut", "A trap's cut is from 0 to 600 seconds"); }
            }
            // A talent's redirect of weapon damage to another enemy (ADR 0013, Bewildered): one per talent.
            var redirecting = new HashSet<string>(StringComparer.Ordinal);
            var redirects = pack.Simulation.Redirects;
            for (var i = 0; i < redirects.Length; i++)
            {
                var rule = redirects[i];
                var path = $"simulation.redirects[{i}]";
                if (!talents.Contains(rule.Talent)) { Error("bad-sim-redirect", path + ".talent", $"Unknown talent {Q(rule.Talent)}"); }
                else if (!redirecting.Add(rule.Talent)) { Error("bad-sim-redirect", path + ".talent", $"Talent {Q(rule.Talent)} has two redirects"); }
                if (!double.IsFinite(rule.Share) || rule.Share is <= 0 or >= 1) { Error("bad-sim-redirect", path + ".share", "A redirect moves a share above 0 and below 1 of each hit"); }
                if (!double.IsFinite(rule.Within) || rule.Within <= 0 || rule.Within > SimRedirectDefinition.MaximumWithin)
                { Error("bad-sim-redirect", path + ".within", $"A redirect reaches above 0 up to {SimRedirectDefinition.MaximumWithin:G} metres"); }
            }
            // A talent's blast on other enemies (ADR 0013): one per talent and event.
            var blasting = new HashSet<(string, string)>();
            var blasts = pack.Simulation.Blasts;
            for (var i = 0; i < blasts.Length; i++)
            {
                var rule = blasts[i];
                var path = $"simulation.blasts[{i}]";
                if (!talents.Contains(rule.Talent)) { Error("bad-sim-blast", path + ".talent", $"Unknown talent {Q(rule.Talent)}"); }
                if (!BlastEvents.Contains(rule.On))
                { Error("bad-sim-blast", path + ".on", "A blast needs an event about an enemy: a hit or a kill, by the weapon or a skill"); }
                else if (!blasting.Add((rule.Talent, rule.On))) { Error("bad-sim-blast", path + ".on", $"Talent {Q(rule.Talent)} has two blasts on {Q(rule.On)}"); }
                if (rule.Amount.Length > 0) { Stat(rule.Amount, path + ".amount"); }
                if (rule.Amount.Length == 0 && rule.Status.Length == 0 && rule.Mark.Length == 0)
                { Error("bad-sim-blast", path + ".amount", "A blast needs an amount, a status or a mark"); }
                if (rule.Mark.Length > 0 && !pack.Simulation.Triggers.Any(trigger => trigger.Talent == rule.Talent && trigger.Key == rule.Mark && trigger.Kind == "stacks" &&
                    (trigger.ResetOn == "target_death" || trigger.ConsumeOn == "target_death") && trigger.Pool.Length == 0 && trigger.Bank.Length == 0))
                { Error("bad-sim-blast", path + ".mark", $"A blast's mark names a stacks rule of {Q(rule.Talent)} cleared at target_death, held per enemy, with no pool or bank"); }
                if (!double.IsFinite(rule.Within) || rule.Within <= 0 || rule.Within > SimBlastDefinition.MaximumWithin)
                { Error("bad-sim-blast", path + ".within", $"A blast reaches above 0 up to {SimBlastDefinition.MaximumWithin:G} metres"); }
                if (rule.Status.Length > 0 ? !Statuses.Contains(rule.Status) || !double.IsFinite(rule.StatusDuration) || rule.StatusDuration is <= 0 or > 600 : rule.StatusDuration != 0)
                { Error("bad-sim-blast", path + ".status", "A blast's status needs a known status and a duration above 0 up to 600; without one, no duration"); }
                else if (DamagingStatuses.Contains(rule.Status) && !pack.Simulation.StatusDamage.Any(damage => damage.Status == rule.Status))
                { Error("bad-sim-blast", path + ".status", $"A blast's {Q(rule.Status)} needs a statusDamage row to tick"); }
            }
        }

        // Consecutive-headshot cycles (ADR 0021): one per talent, on a tag some weapon carries, a true-only selector no trigger drives,
        // known stats, and a kill threshold from 1.
        private void HeadshotCycles()
        {
            var cycles = pack.Simulation.HeadshotCycles;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stats = pack.Stats.Select(stat => stat.ID).ToHashSet(StringComparer.Ordinal);
            var weaponTags = pack.Items.Where(item => pack.Slots.Any(slot => slot.ID == item.Slot && slot.Weapon)).SelectMany(item => item.Tags).ToHashSet(StringComparer.Ordinal);
            var driven = pack.Simulation.Triggers.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < cycles.Length; i++)
            {
                var cycle = cycles[i];
                var path = $"simulation.headshotCycles[{i}]";
                if (pack.Talents.All(talent => talent.ID != cycle.Talent)) { Error("bad-sim-headshot-cycle", path + ".talent", $"Unknown talent {Q(cycle.Talent)}"); }
                else if (!seen.Add(cycle.Talent)) { Error("bad-sim-headshot-cycle", path + ".talent", $"Talent {Q(cycle.Talent)} has two cycles"); }
                if (!weaponTags.Contains(cycle.WeaponTag)) { Error("bad-sim-headshot-cycle", path + ".weaponTag", $"No weapon carries the tag {Q(cycle.WeaponTag)}"); }
                var selector = pack.Selectors.FirstOrDefault(entry => entry.ID == cycle.Key);
                if (selector is null || selector.Values.Length != 1 || selector.Values[0] != "true" || driven.Contains(cycle.Key))
                { Error("bad-sim-headshot-cycle", path + ".key", "A cycle's key needs a selector with only the value true that no trigger drives"); }
                foreach (var (stat, field) in new[] { (cycle.Repair, "repair"), (cycle.BonusArmorCap, "bonusArmorCap"), (cycle.Allowance, "allowance") })
                {
                    if (!stats.Contains(stat)) { Error("unknown-stat", $"{path}.{field}", $"Unknown stat {Q(stat)}"); }
                }
                if (cycle.KillFrom is < 1 or > 100) { Error("bad-sim-headshot-cycle", path + ".killFrom", "A cycle's kill threshold is from 1 to 100"); }
            }
        }

        // A bonus on the hit after a headshot kill (ADR 0023: Headhunter): a known talent, once, a share above 0 up to 10, a known cap stat
        // and a window above 0 up to 600 s.
        private void NextHitBonuses()
        {
            var bonuses = pack.Simulation.NextHitBonuses;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stats = pack.Stats.Select(stat => stat.ID).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < bonuses.Length; i++)
            {
                var bonus = bonuses[i];
                var path = $"simulation.nextHitBonuses[{i}]";
                if (pack.Talents.All(talent => talent.ID != bonus.Talent)) { Error("bad-sim-next-hit-bonus", path + ".talent", $"Unknown talent {Q(bonus.Talent)}"); }
                else if (!seen.Add(bonus.Talent)) { Error("bad-sim-next-hit-bonus", path + ".talent", $"Talent {Q(bonus.Talent)} has two next-hit bonuses"); }
                if (!double.IsFinite(bonus.Share) || bonus.Share is <= 0 or > 10) { Error("bad-sim-next-hit-bonus", path + ".share", "A next-hit bonus's share is above 0, up to 10"); }
                if (!stats.Contains(bonus.Cap)) { Error("unknown-stat", path + ".cap", $"Unknown stat {Q(bonus.Cap)}"); }
                if (!double.IsFinite(bonus.Window) || bonus.Window is <= 0 or > 600) { Error("bad-sim-next-hit-bonus", path + ".window", "A next-hit bonus's window is above 0, up to 600 s"); }
            }
        }

        // A rule's lists and its requires, emptied. Lists read from a file aren't the default instance, which a record compares by reference
        // (R127), so a check that a rule sets no other field compares this, and checks the lists apart with ListsEmpty.
        private static SimTriggerDefinition WithoutLists(SimTriggerDefinition r) =>
            r with { Phases = [], Also = [], WeaponTypes = [], AnyStatusExcept = [], WeaponTags = [], RequiresContext = null };

        private static bool ListsEmpty(SimTriggerDefinition r) => r.Phases.IsDefaultOrEmpty && r.Also.IsDefaultOrEmpty && r.WeaponTypes.IsDefaultOrEmpty &&
            r.AnyStatusExcept.IsDefaultOrEmpty && r.WeaponTags.IsDefaultOrEmpty && (r.RequiresContext?.Count ?? 0) == 0;

        // Skills that step aside in fights (ADR 0022): known skills, once each, with no fight rule, and a short one-line reason.
        private void SkillsAside()
        {
            var asides = pack.Simulation.SkillsAside;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skills = pack.ActiveSkills.Select(skill => skill.ID).ToHashSet(StringComparer.Ordinal);
            var ruled = pack.Simulation.Skills.Select(rule => rule.Skill).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < asides.Length; i++)
            {
                var aside = asides[i];
                var path = $"simulation.skillsAside[{i}]";
                if (!skills.Contains(aside.Skill)) { Error("bad-sim-skill-aside", path + ".skill", $"Unknown skill {Q(aside.Skill)}"); }
                else if (!seen.Add(aside.Skill)) { Error("bad-sim-skill-aside", path + ".skill", $"Skill {Q(aside.Skill)} is listed twice"); }
                else if (ruled.Contains(aside.Skill))
                { Error("bad-sim-skill-aside", path + ".skill", $"Skill {Q(aside.Skill)} has a fight rule, so it doesn't step aside"); }
                if (string.IsNullOrWhiteSpace(aside.Why) || aside.Why.Length > 200 || aside.Why.Any(char.IsControl))
                { Error("bad-sim-skill-aside", path + ".why", "A reason is one line of up to 200 characters"); }
            }
        }

        // The armor kit a fight can use (ADR 0010).
        private void ArmorKit()
        {
            if (pack.Simulation.ArmorKit is not { } kit) { return; }
            const string path = "simulation.armorKit";
            if (!double.IsFinite(kit.Repair) || kit.Repair <= 0 || kit.Repair > 1) { Error("bad-sim-armor-kit", path + ".repair", "A kit's repair must be a share of max armor, above 0 and up to 1"); }
            if (!double.IsFinite(kit.UseSeconds) || kit.UseSeconds <= 0 || kit.UseSeconds > 60) { Error("bad-sim-armor-kit", path + ".useSeconds", "A kit's use time must be above 0 and up to 60 seconds"); }
            if (!double.IsFinite(kit.ExtendSeconds) || kit.ExtendSeconds < 0 || kit.ExtendSeconds > 60) { Error("bad-sim-armor-kit", path + ".extendSeconds", "A kit's extension must be from 0 to 60 seconds"); }
            if (!double.IsFinite(kit.MaxUseSeconds) || kit.MaxUseSeconds < kit.UseSeconds || kit.MaxUseSeconds > 60) { Error("bad-sim-armor-kit", path + ".maxUseSeconds", "A kit's longest use must be at least its use time, up to 60 seconds"); }
            foreach (var id in kit.ReplacedBySpecializations.Where(id => pack.Specializations.All(entry => entry.ID != id)))
            { Error("bad-sim-armor-kit", path + ".replacedBySpecializations", $"Unknown specialization {Q(id)}"); }
            foreach (var id in kit.ReplacedByTalents.Where(id => pack.Talents.All(entry => entry.ID != id)))
            { Error("bad-sim-armor-kit", path + ".replacedByTalents", $"Unknown talent {Q(id)}"); }
            // A kit ability (ADR 0021) belongs to a talent that replaces the kit, once each, with a known cooldown stat.
            var abilityTalents = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < kit.Abilities.Length; i++)
            {
                var ability = kit.Abilities[i];
                if (!kit.ReplacedByTalents.Contains(ability.Talent) || !abilityTalents.Add(ability.Talent))
                { Error("bad-sim-armor-kit", $"{path}.abilities[{i}].talent", "A kit ability needs a talent listed in replacedByTalents, once"); }
                if (!stats.Contains(ability.Cooldown)) { Error("unknown-stat", $"{path}.abilities[{i}].cooldown", $"Unknown stat {Q(ability.Cooldown)}"); }
            }
            if (kit.Instant is not null && kit.Abilities.IsDefaultOrEmpty)
            { Warning("sim-instant-kit-unused", path + ".instant", "Fights use the instant kit only with a kit ability, and this kit has none"); }
            if (kit.Instant is { } instant && (!double.IsFinite(instant.Repair) || instant.Repair <= 0 || instant.Repair > 1 ||
                !double.IsFinite(instant.Cooldown) || instant.Cooldown < 0 || instant.Cooldown > 600))
            { Error("bad-sim-armor-kit", path + ".instant", "An instant kit's repair must be a share of max armor above 0 and up to 1, and its cooldown from 0 to 600 seconds"); }
        }

        // Assumptions whose default follows the fight distance (P12: the enemy hit rate by distance).
        private void DistanceDefaults()
        {
            var entries = pack.Simulation.DistanceDefaults;
            if (entries.IsDefaultOrEmpty) { return; }
            // Duplicate input IDs are reported by the input checks; the first definition stands here.
            var inputs = new Dictionary<string, NumericContextInputDefinition>(StringComparer.Ordinal);
            foreach (var input in pack.NumericContextInputs) { inputs.TryAdd(input.ID, input); }
            // A trigger's key is driven in fights, which would override a distance default.
            var driven = pack.Simulation.Triggers.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var path = $"simulation.distanceDefaults[{i}]";
                if (!inputs.TryGetValue(entry.Input, out var input)) { Error("bad-sim-distance-default", path + ".input", $"Unknown numeric input {Q(entry.Input)}"); continue; }
                if (driven.Contains(entry.Input)) { Error("bad-sim-distance-default", path + ".input", $"Input {Q(entry.Input)} is a trigger's key, which fights drive"); }
                if (!seen.Add(entry.Input)) { Error("bad-sim-distance-default", path + ".input", $"Input {Q(entry.Input)} has two distance defaults"); }
                var bands = entry.Bands;
                if (bands.IsDefaultOrEmpty) { Error("bad-sim-distance-default", path + ".bands", "Distance defaults need at least one band"); continue; }
                var previous = 0d;
                for (var b = 0; b < bands.Length; b++)
                {
                    var band = bands[b];
                    var bandPath = $"{path}.bands[{b}]";
                    // Every band but the last ends at a distance beyond the last valid one; the last covers what remains.
                    var last = b == bands.Length - 1;
                    if (last ? band.UpTo is not null : band.UpTo is not { } upTo || !double.IsFinite(upTo) || upTo <= previous || upTo > 1000)
                    { Error("bad-sim-distance-default", bandPath + ".upTo", "Each band but the last needs an upTo above the previous one, up to 1000 m; the last has none"); }
                    else if (!last) { previous = band.UpTo!.Value; }
                    if (!input.Accepts(band.Value))
                    { Error("bad-sim-distance-default", bandPath + ".value", $"A band's value must be a legal value of {Q(entry.Input)}: within its range and on its step"); }
                }
            }
        }

        // The inputs that may take a default in fights with enemy fire: the target, by the owner's decision (2026-11-14). Others would need
        // the Desktop's form checks and grouped cards to follow it too.
        private const string IncomingDefaultInput = "enemy_target";

        // An input's default in fights with enemy fire: the target, once, that no trigger drives and no distance default sets, at one of its
        // listed profiles.
        private void IncomingDefaults()
        {
            var entries = pack.Simulation.IncomingDefaults;
            if (entries.IsDefaultOrEmpty) { return; }
            var inputs = new Dictionary<string, NumericContextInputDefinition>(StringComparer.Ordinal);
            foreach (var input in pack.NumericContextInputs) { inputs.TryAdd(input.ID, input); }
            var driven = pack.Simulation.Triggers.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
            var byDistance = pack.Simulation.DistanceDefaults.Select(entry => entry.Input).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var path = $"simulation.incomingDefaults[{i}]";
                if (!inputs.TryGetValue(entry.Input, out var input)) { Error("bad-sim-incoming-default", path + ".input", $"Unknown numeric input {Q(entry.Input)}"); continue; }
                if (entry.Input != IncomingDefaultInput) { Error("bad-sim-incoming-default", path + ".input", $"Only {Q(IncomingDefaultInput)} takes a default in fights with enemy fire"); }
                if (driven.Contains(entry.Input)) { Error("bad-sim-incoming-default", path + ".input", $"Input {Q(entry.Input)} is a trigger's key, which fights drive"); }
                if (byDistance.Contains(entry.Input)) { Error("bad-sim-incoming-default", path + ".input", $"Input {Q(entry.Input)} already has a default by distance"); }
                if (!seen.Add(entry.Input)) { Error("bad-sim-incoming-default", path + ".input", $"Input {Q(entry.Input)} has two defaults for enemy fire"); }
                if (!input.Accepts(entry.Value) || entry.Value < 1 || !input.Options.IsDefaultOrEmpty && !input.Options.Any(option => option.Value == entry.Value))
                { Error("bad-sim-incoming-default", path + ".value", $"The value must be one of {Q(entry.Input)}'s profiles: within its range, on its step and listed"); }
            }
        }

        // A shared timer or a stack refresh reads every live stack on its key as its own, so no other stacks rule may write that key.
        // Within a talent, stacks rules on one key (Chemical Agent's two parts) must shape the stacks alike; Perfect versions of a
        // talent may differ from it, since a build carries one or the other.
        private void SharedStackTimers()
        {
            var triggers = pack.Simulation.Triggers;
            var firsts = new Dictionary<(string Talent, string Key), SimTriggerDefinition>();
            for (var i = 0; i < triggers.Length; i++)
            {
                var rule = triggers[i];
                if (rule.Kind != "stacks") { continue; }
                if (!firsts.TryAdd((rule.Talent, rule.Key), rule) && !firsts[(rule.Talent, rule.Key)].SharesStacksWith(rule))
                { Error("bad-sim-trigger", $"simulation.triggers[{i}]", $"Talent {Q(rule.Talent)} has stacks rules on {Q(rule.Key)}. {SimTriggerDefinition.SharedStacksRule}"); }
            }
            var stackRules = triggers.Where(rule => rule.Kind == "stacks").GroupBy(rule => rule.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(rule => rule.Talent).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
            for (var i = 0; i < triggers.Length; i++)
            {
                var rule = triggers[i];
                if (rule.Kind == "stacks" && (rule.FromFirstGain || rule.RefreshOn.Length > 0) && stackRules.GetValueOrDefault(rule.Key) > 1)
                { Error("bad-sim-trigger", $"simulation.triggers[{i}]", $"Key {Q(rule.Key)} has another talent's stacks rule, which fromFirstGain and refreshOn would read as their own"); }
            }
        }

        // A rule gated on a status on the player that no enemyStatuses row puts on can never open: worth a warning, not a refusal.
        private void PlayerStatusGates()
        {
            var applied = pack.Simulation.EnemyStatuses.Select(entry => entry.Status).ToHashSet(StringComparer.Ordinal);
            var triggers = pack.Simulation.Triggers;
            for (var i = 0; i < triggers.Length; i++)
            {
                var gate = triggers[i].WhilePlayerStatus;
                var except = triggers[i].AnyStatusExcept;
                if (gate.Length > 0 && (gate == "any" ? applied.All(status => except.Contains(status)) : !applied.Contains(gate)))
                { Warning("sim-player-status-unapplied", $"simulation.triggers[{i}].whilePlayerStatus", $"No enemyStatuses row puts {(gate == "any" ? except.IsDefaultOrEmpty ? "any status" : "any status it counts" : Q(gate))} on the player, so the rule never gains"); }
            }
        }

        // The longest a status from one enemy hit lasts on the player.
        private const double MaximumEnemyStatusDuration = 60;

        // Statuses enemies' hits put on the player (ADR 0018): one row per listed target profile and status, a duration, and optionally a
        // tick stat with its period, resistance stats and a floor on the shortened duration.
        private void EnemyStatuses()
        {
            var entries = pack.Simulation.EnemyStatuses;
            if (entries.IsDefaultOrEmpty) { return; }
            var target = pack.NumericContextInputs.FirstOrDefault(input => input.ID == IncomingDefaultInput);
            var stats = pack.Stats.Select(stat => stat.ID).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<(int, string)>();
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var path = $"simulation.enemyStatuses[{i}]";
                if (target is null || !target.Accepts(entry.Target) || entry.Target < 1 || !target.Options.IsDefaultOrEmpty && !target.Options.Any(option => option.Value == entry.Target))
                { Error("bad-sim-enemy-status", path + ".target", $"The target must be one of {Q(IncomingDefaultInput)}'s profiles: within its range, on its step and listed"); }
                if (!Statuses.Contains(entry.Status)) { Error("bad-sim-enemy-status", path + ".status", $"Unknown status {Q(entry.Status)}"); }
                else if (!seen.Add((entry.Target, entry.Status))) { Error("bad-sim-enemy-status", path, $"Target {entry.Target} puts {Q(entry.Status)} on the player twice"); }
                if (!double.IsFinite(entry.Duration) || entry.Duration is <= 0 or > MaximumEnemyStatusDuration)
                { Error("bad-sim-enemy-status", path + ".duration", $"The duration must be above 0 up to {MaximumEnemyStatusDuration:G} seconds"); }
                if (entry.Tick.Length > 0)
                {
                    if (!DamagingStatuses.Contains(entry.Status)) { Error("bad-sim-enemy-status", path + ".tick", $"Status {Q(entry.Status)} deals no damage over time"); }
                    if (!stats.Contains(entry.Tick)) { Error("bad-sim-enemy-status", path + ".tick", $"Unknown stat {Q(entry.Tick)}"); }
                    if (!double.IsFinite(entry.Period) || entry.Period < MinimumTickPeriod || entry.Period > MaximumTickPeriod)
                    { Error("bad-sim-enemy-status", path + ".period", $"A tick needs a period from {MinimumTickPeriod:G} up to {MaximumTickPeriod:G} seconds"); }
                }
                else if (entry.Period != 0 || entry.TickOnApply) { Error("bad-sim-enemy-status", path + ".tick", "period and tickOnApply need a tick"); }
                var resistances = new HashSet<string>(StringComparer.Ordinal);
                foreach (var resistance in entry.Resistances)
                {
                    if (!stats.Contains(resistance)) { Error("bad-sim-enemy-status", path + ".resistances", $"Unknown stat {Q(resistance)}"); }
                    else if (!resistances.Add(resistance)) { Error("bad-sim-enemy-status", path + ".resistances", $"Stat {Q(resistance)} is listed twice"); }
                }
                if (!double.IsFinite(entry.MinDuration) || entry.MinDuration < 0 || entry.MinDuration > entry.Duration || entry.MinDuration > 0 && entry.Resistances.IsDefaultOrEmpty)
                { Error("bad-sim-enemy-status", path + ".minDuration", "minDuration needs resistances to shorten the duration, and is from 0 up to the duration"); }
            }
        }

        private static readonly HashSet<string> DamagingStatuses = [.. GamePack.DamagingStatuses];
        private const double MinimumTickPeriod = 0.01;
        // Plague of the Outcasts runs 50; the cap keeps a fight's tick queue bounded.
        private const int MaximumTalentDamageStacks = 100;
        // The events a blast centres on an enemy for (ADR 0013): its hit or its death.
        private static readonly HashSet<string> BlastEvents = new(StringComparer.Ordinal)
        { "hit", "hit_pellet", "crit", "crit_pellet", "headshot", "body_hit", "unarmored_hit", "kill", "crit_kill", "headshot_kill", "skill_hit", "skill_kill" };
        private const double MaximumTickPeriod = 60;

        // A grant on firing (Ablative Nano-Plating's temporary armor) uses clearGrant's kinds and limits.
        private static string? GrantError(SimTriggerDefinition r)
        {
            // A stat's value stands in for the amount; it's checked against the grant's limits as the fight starts.
            if (r.GrantAmountStat.Length > 0)
            {
                if (r.Grant is "" or "armor_kit_refund" || r.GrantAmount != 0)
                { return "grantAmountStat takes the place of grantAmount, on any grant but armor_kit_refund"; }
                // Its value is checked against the grant's limits as the fight starts, so the rest is checked at the largest amount.
                r = r with { GrantAmount = SimTriggerDefinition.MaximumGrantAmount(r.Grant), GrantAmountStat = "" };
            }
            // Skilled holds its reset while no cooldown runs; the source's delay is 1 s.
            if (r.GrantHoldDelay != 0 && !(r.Grant is "skill_cooldown" or "skill_cooldown_seconds" && double.IsFinite(r.GrantHoldDelay) && r.GrantHoldDelay is > 0 and <= SimTriggerDefinition.MaximumGrantHoldDelay))
            { return "grantHoldDelay needs a skill_cooldown or skill_cooldown_seconds grant and a delay above 0 up to 60"; }
            if (r.Grant.Length == 0)
            { return r.GrantAmount != 0 || r.GrantDuration != 0 || r.GrantInstances.Length > 0 ? "grantAmount, grantDuration and grantInstances need a grant" : null; }
            if (r.Kind is not ("buff" or "ready") || !double.IsFinite(r.GrantAmount) || r.GrantAmount <= 0 || r.GrantAmount > SimTriggerDefinition.MaximumGrantAmount(r.Grant) ||
                !double.IsFinite(r.GrantDuration) || r.GrantDuration < 0 || r.GrantDuration > 600)
            { return "grant needs a buff or ready rule, an amount above 0 up to 1 (seconds up to 600 for skill_cooldown_seconds) and a duration up to 600"; }
            return r.Grant switch
            {
                "temporary_armor" => r.GrantDuration > 0 && r.GrantInstances.Length == 0 ? null : "temporary_armor needs a duration and no grantInstances",
                "shield_repair" or "missing_armor_repair" or "skill_cooldown" or "skill_cooldown_seconds" => r.GrantDuration == 0 && r.GrantInstances.Length == 0 ? null : $"{r.Grant} is instant: no duration or grantInstances",
                // The kit just spent comes back, so the use that fired it costs none (Efficient). Only a kit's own event has one.
                "armor_kit_refund" => r.On == "armor_kit" && r.GrantAmount == 1 && r.GrantDuration == 0 && r.GrantInstances.Length == 0 ? null
                    : "armor_kit_refund is one kit at once on armor_kit: grantAmount 1, no duration or grantInstances",
                // Spread over time, each firing is its own repair (the only stacking the source shows, R41); instant takes none.
                "armor_repair" => (r.GrantDuration > 0) == (r.GrantInstances == "independent") && r.GrantInstances is "" or "independent" ? null
                    : "armor_repair over a duration needs grantInstances \"independent\"; an instant one takes none",
                _ => "grant must be temporary_armor, shield_repair, armor_repair, missing_armor_repair, skill_cooldown, skill_cooldown_seconds or armor_kit_refund"
            };
        }

        // One primary message per rule, checked in a fixed order.
        private string? TriggerError(SimTriggerDefinition r, HashSet<string> owners)
        {
            var number = numericInputs.Contains(r.Key);
            if (!owners.Contains(r.Talent)) { return $"Unknown talent or specialization node {Q(r.Talent)}"; }
            if (!TriggerKinds.Contains(r.Kind)) { return $"Unknown trigger kind {Q(r.Kind)}"; }
            if (!PackIds.IsSlug(r.Key)) { return $"Trigger key {Q(r.Key)} must be lower_snake_case starting with a letter"; }
            if (!(double.IsFinite(r.Duration) && double.IsFinite(r.Every) && double.IsFinite(r.Within) && double.IsFinite(r.Decay) && double.IsFinite(r.CancelFor)))
            { return "Trigger numbers must be finite"; }
            if (r.CancelOn.Length > 0 && (r.Kind != "buff" || !Events.Contains(r.CancelOn)) || r.CancelFor != 0 && r.CancelOn.Length == 0 || r.CancelFor < 0)
            { return "cancelOn needs a simulator event on a buff rule, and cancelFor a cancelOn and a non-negative time"; }
            if (r.Bank.Length > 0 && (r.Pool.Length == 0 || !numericInputs.Contains(r.Bank) || r.Bank == r.Key))
            { return "bank needs a pooled stacks rule and a numeric input key of its own"; }
            if (r.ClearGrant.Length > 0 || r.ClearAmount != 0 || r.ClearDuration != 0)
            {
                var armor = r.ClearGrant == "temporary_armor";
                // A counted reset only restarts the count, so a counted rule needs consumeOn to clear stacks.
                if (!(armor || r.ClearGrant == "shield_repair") || r.Kind != "stacks" || (r.ResetOn.Length == 0 || r.Count > 0) && r.ConsumeOn.Length == 0 ||
                    !(double.IsFinite(r.ClearAmount) && r.ClearAmount > 0 && r.ClearAmount <= 1) ||
                    !double.IsFinite(r.ClearDuration) || (armor ? !(r.ClearDuration > 0 && r.ClearDuration <= 600) : r.ClearDuration != 0))
                { return "clearGrant needs a stacks rule that resets or consumes, temporary_armor or shield_repair, a share per stack above 0 up to 1, and for temporary_armor a duration up to 600"; }
            }
            if (r.Within < 0) { return "within can't be negative"; }
            if (r.Within > 0 && r.Kind is not ("buff" or "stacks" or "distance" or "count" or "status")) { return "Only buff, stacks, distance, count and status rules take within"; }
            if (r.Kind == "status" && r.Within > 0 && r.On.Length == 0) { return "A status rule's within needs an event"; }
            // Ready once the talent's weapon has been put away long enough, spent by drawing it (ADR 0019).
            // It never fires, so it takes nothing that acts on firing, and nothing but its weapon's time away moves it.
            if (r.PrimeHolstered != 0 && (r.Kind != "ready" || r.On.Length > 0 || r.Also.Length > 0 || r.Every != 0 || r.ResetOn.Length > 0 || r.AlsoResetOn.Length > 0 ||
                r.ConsumeOn.Length > 0 || r.CancelOn.Length > 0 || r.Grant.Length > 0 || r.Refill || r.Count != 0 || r.ActiveAfter != 0 || r.Chance < 1 ||
                r.WhileKey.Length > 0 || r.WhileArmorBelow > 0 || r.WhileStatAtLeast is not null || !r.WeaponTags.IsDefaultOrEmpty || r.ForceHeadshot ||
                !double.IsFinite(r.PrimeHolstered) || r.PrimeHolstered is <= 0 or > SimTriggerDefinition.MaximumPrimeHolstered))
            { return $"primeHolstered needs a ready rule without an event, every, reset, consume, cancel, grant, refill, count, gate, chance, activeAfter or weapon tags, and seconds above 0 up to {SimTriggerDefinition.MaximumPrimeHolstered:G}"; }
            // One timer for every stack, from the gain that takes the count from 0 (Cover Shooter, R112).
            if (r.FromFirstGain && (r.Kind != "stacks" || r.Duration <= 0 || r.Refresh || r.Decay > 0 || r.Pool.Length > 0 || r.GainWhileHolstered > 0))
            { return "fromFirstGain needs a stacks rule with a duration, no refresh, decay, pool or gainWhileHolstered"; }
            // A refresh passes none of a gain's gates, so a gated rule can't take one. On stacks it restarts live stacks' timers (Cover
            // Shooter's kills in cover), so it needs a duration of its own and no pool.
            if (r.RefreshOn.Length > 0 && (r.Kind is not ("buff" or "stacks") ||
                r.Kind == "stacks" && (r.Duration <= 0 || r.Pool.Length > 0 || r.GainWhileHolstered > 0 || r.Decay > 0 || r.ResetOn == "target_death" || r.ConsumeOn == "target_death") ||
                r.On.Length == 0 || !Events.Contains(r.RefreshOn) ||
                r.RefreshOn == r.On || r.Also.Any(gain => gain.On == r.RefreshOn) || r.RefreshOn == r.ResetOn || r.RefreshOn == r.AlsoResetOn ||
                r.RefreshOn == r.ConsumeOn || r.RefreshOn == r.CancelOn || r.Duration <= 0 && r.DurationStat.Length == 0 || r.CooldownFromEnd ||
                r.WhileStatus.Length > 0 || r.WhilePlayerStatus.Length > 0 || r.WhileKey.Length > 0 || r.WhileArmorBelow > 0 || r.WhileStatAtLeast is not null || r.Chance < 1 || r.Count > 0))
            { return "refreshOn needs a timed buff, or stacks with a duration and no pool, gainWhileHolstered, decay or stacks on each enemy, with an event, no cooldownFromEnd and no gate, chance or count, and a simulator event none of its other fields name"; }
            if (r.AlsoResetOn.Length > 0 && (r.Kind is not ("buff" or "stacks") || r.ResetOn.Length == 0 || r.AlsoResetOn == r.ResetOn || !Events.Contains(r.AlsoResetOn)))
            { return "alsoResetOn needs a buff or stacks rule with a resetOn, and another simulator event"; }
            if (r.Count != 0 && r.Kind is not ("buff" or "stacks" or "status") || r.Refill && r.Kind is not ("buff" or "stacks"))
            { return "Only buff, stacks and status rules take count, and only buff and stacks rules refill"; }
            if ((r.DifferentTypes || r.EveryPerType) && (r.Kind is not ("buff" or "stacks") || r.On.Length == 0 || r.WeaponTypes.IsDefaultOrEmpty))
            { return "differentTypes and everyPerType need a buff or stacks rule with an event and weaponTypes"; }
            if (r.EveryPerType && (r.Every <= 0 || r.EveryGroup.Length > 0 || r.CooldownFromEnd))
            { return "everyPerType needs an every, without everyGroup or cooldownFromEnd"; }
            // Unwavering's lockout (ADR 0019): on a timed buff with a lockout, nothing else that moves or spends it.
            if (!r.LockoutFieldsFit)
            {
                return "lockoutAlways and gainAtLockoutEnd need a buff with a duration, cancelOn and cancelFor, and no consume, reset, count, every or " +
                    "cooldownFromEnd; gainAtLockoutEnd also takes no gate (whileKey, whileStatus, whilePlayerStatus, whileArmorBelow, whileStatAtLeast), " +
                    "chance, grant, refill or activeAfter";
            }
            if (r.AssumeWithoutDistance && (r.Kind != "distance" || r.Phases.IsDefaultOrEmpty)) { return "assumeWithoutDistance goes on a distance rule with phases (bands)"; }
            if (r.ShareCopies && (r.Kind is not ("buff" or "stacks") || r.On.Length == 0 || r.GainWhileHolstered > 0))
            { return "shareCopies needs a buff or stacks rule with an event, and no gainWhileHolstered"; }
            if (r.RefillBoth && !r.Refill) { return "A refill of both weapons needs refill on, as refill: \"both\" sets it"; }
            if (r.Count < 0 || r.Count == 1) { return "A count needs two or more events"; }
            if (!double.IsFinite(r.CountWindow) || r.CountWindow < 0 || r.CountWindow > 0 && (r.Count == 0 || r.Kind is not ("buff" or "stacks")))
            { return "countWindow needs a counted buff or stacks rule and a time above 0"; }
            // A counted status rule's also adds more events to its one count (Ignited: weapon and skill kills toward the fourth, R85).
            var statusAlso = r.Kind == "status" && r.Also.Length > 0;
            if (statusAlso && !(SimTriggerDefinition.StatusAlsoAllowed(r) && r.Also.All(gain => Events.Contains(gain.On))))
            { return "A status rule's also needs a count on an event other than second, no applyOnHit, and other distinct simulator events, not second, target_death or status_applied, each adding one"; }
            if ((r.Gain != 0 || r.Also.Length > 0 && !statusAlso || r.Decay != 0) && r.Kind != "stacks") { return "Only stacks rules take gain, also or decay; a counted status rule takes also"; }
            if (r.Gain < 0 || r.Decay < 0) { return "gain and decay can't be negative"; }
            if (r.Decay > 0 && (!r.Refresh || r.Duration <= 0)) { return "Decaying stacks need refresh and a duration"; }
            if (!statusAlso && r.Also.Any(gain => gain.On.Length == 0 || !Events.Contains(gain.On) || gain.Gain <= 0 || gain.Count < 0 || gain.Count == 1))
            { return "also needs simulator events with positive gains, and counts of two or more"; }
            if ((r.Pool.Length > 0) != (r.PoolMax > 0) || r.Pool.Length > 0 && r.Kind != "stacks") { return "A pool needs a name and a cap on stacks rules"; }
            if (r.Kind == "distance")
            {
                return number && r.Phases.Length == 0 && r.Within == 0 || !number && r.Within > 0 && r.Phases.Length == 0 || !number && r.Within == 0 && r.Phases.Length >= 2
                    ? null : "A distance rule takes a numeric input, a condition with within, or two or more phases on a condition";
            }
            if (r.Kind == "count")
            {
                var plain = WithoutLists(r) with { Talent = "", Key = "", Kind = "", On = "", Within = 0, Max = 0, AtLeast = 0, Status = "" };
                if (plain != new SimTriggerDefinition() || !ListsEmpty(r) || !Events.Contains(r.On) || r.On == "target_death")
                { return "A count rule takes an event other than target_death, within, status, and max or atLeast only"; }
                if (r.Status.Length > 0 && (!Statuses.Contains(r.Status) || r.On.Length == 0)) { return "A count of a status needs a known status and an event to read it on"; }
                // Field by field, for the same reason: talents that can't be worn together may share a count, as a talent and its Perfect version do.
                // A count of a status may sit on a reading of the same status by a talent with this count, which runs with one enemy at a time.
                bool Counts(string talent) => talent == r.Talent || pack.Simulation.Triggers.Any(rule => rule.Kind == "count" && rule.Key == r.Key && rule.Talent == talent);
                if (pack.Simulation.Triggers.Any(other => other.Key == r.Key && !ReferenceEquals(other, r) &&
                    !(other.Kind == "status" && other.On.Length == 0 && other.Status == r.Status && Counts(other.Talent)) &&
                    (other.Talent == r.Talent || !(other.Kind == r.Kind && other.On == r.On && other.Within == r.Within && other.Max == r.Max && other.AtLeast == r.AtLeast && other.Status == r.Status))))
                { return "A count's key takes no other rule, but the same rule for another talent, or those talents' reading of the same status"; }
                if (number)
                {
                    if (r.Max < 1 || r.AtLeast != 0) { return "A count on a numeric input needs a max of 1 or more, and no atLeast"; }
                    var input = pack.NumericContextInputs.First(input => input.ID == r.Key);
                    var whole = input.Step <= 0 || Math.Abs(1 / input.Step - Math.Round(1 / input.Step)) <= 1e-8;
                    return input.Min == 0 && input.Max >= r.Max && whole ? null : "A count's input needs a min of 0, whole steps and room for its max";
                }
                if (r.AtLeast < 1 || r.Max != 0) { return "A count on a condition needs atLeast of 1 or more, and no max"; }
                return selectors.TryGetValue(r.Key, out var condition) && !(condition.Values.Length == 1 && condition.Values[0] == "true")
                    ? "A count's condition needs a selector with only the value true" : null;
            }
            if (r.WhileStatus.Length > 0 && (!(Statuses.Contains(r.WhileStatus) || r.WhileStatus == "any") || r.Kind is not ("buff" or "stacks")))
            { return "whileStatus needs a known status on a buff or stacks rule"; }
            // A status on the player (ADR 0018) gates a gain as a status on the target does; one rule reads one of the two.
            if (r.WhilePlayerStatus.Length > 0 && (!(Statuses.Contains(r.WhilePlayerStatus) || r.WhilePlayerStatus == "any") || r.Kind is not ("buff" or "stacks") ||
                r.On.Length == 0 || r.WhileStatus.Length > 0 || r.GainWhileHolstered > 0))
            { return "whilePlayerStatus needs a known status or any, on a buff or stacks rule with an event, no whileStatus and no gainWhileHolstered"; }
            if (!r.AnyStatusExceptFits(Statuses.Contains, Statuses.Count))
            { return "anyStatusExcept needs whileStatus or whilePlayerStatus any, and distinct known statuses that leave at least one"; }
            if (r.ApplyOnHit && (r.Kind != "status" || r.On is not ("hit" or "hit_pellet" or "crit" or "crit_pellet" or "headshot" or "body_hit" or "unarmored_hit")))
            { return "applyOnHit needs a status rule on hit, hit_pellet, crit, crit_pellet, headshot, body_hit or unarmored_hit"; }
            if (r.Kind == "status")
            {
                if (!(Statuses.Contains(r.Status) || r.Status == "any")) { return $"Unknown status {Q(r.Status)}"; }
                if (r.On.Length > 0 && (!Events.Contains(r.On) || r.Status == "any" || r.Duration <= 0)) { return "A status rule that applies its status needs an event, a named status and a duration"; }
                if (r.Every < 0) { return "The trigger interval can't be negative"; }
                return r.On.Length == 0 && (r.Count != 0 || r.Duration != 0 || r.Every != 0) ? "Only a status rule with an event takes a count, a duration or an interval" : null;
            }
            if (r.Status.Length > 0) { return "Only status rules take a status"; }
            if (r.Kind == "pellets") { return number != (r.AtLeast == 0) || r.AtLeast < 0 ? "A pellets rule takes a numeric input, or a condition key with atLeast" : null; }
            if (r.AtLeast != 0) { return "Only pellets rules take atLeast"; }
            if (r.Kind == "cycle") { return r.Phases.Length < 2 ? "A cycle rule needs two or more phases" : null; }
            if (r.Kind == "target") { return !number ? "A target rule needs a numeric input" : null; }
            if (r.Kind == "bonus_armor")
            {
                return !number || pack.NumericContextInputs.First(input => input.ID == r.Key).Min != 0 ||
                    WithoutLists(r) with { Talent = "", Key = "", Kind = "" } != new SimTriggerDefinition() || !ListsEmpty(r)
                    ? "A bonus armor rule needs a numeric input with a min of 0, and takes no field but its talent and key" : null;
            }
            if (r.Kind == "magazine")
            {
                if (r.Percent && number && r.Phases.Length == 0) { return null; }
                return r.Percent || r.Phases.Length < 2 || number ? "A magazine rule needs two or more phases on a condition key, or percent on a numeric input" : null;
            }
            // A holstered-gain stacks rule (Quick Draw) may gain on time alone, with no event; one held per enemy, by blasts' marks alone
            // (Unnerve, ADR 0013); a primed ready rule by its weapon's time away (ADR 0019).
            if (r.On.Length == 0 && !(r.Kind == "ready" && r.PrimeHolstered > 0) && !(r.Kind == "stacks" && (r.GainWhileHolstered > 0 || (r.ResetOn == "target_death" || r.ConsumeOn == "target_death") &&
                pack.Simulation.Blasts.Any(blast => blast.Talent == r.Talent && blast.Mark == r.Key))) || !Events.Contains(r.On) || !Events.Contains(r.ResetOn) || !Events.Contains(r.ConsumeOn))
            { return "Trigger events must be simulator events"; }
            // A durationStat stands in for the duration (Clutch's post-kill window); its own field check follows.
            var timed = r.Duration > 0 || r.DurationStat.Length > 0;
            // A primed rule's readiness comes from its weapon's time away, so it takes no duration.
            if (r.PrimeHolstered > 0 && (timed || number)) { return "A primed ready rule takes no duration and needs a condition key"; }
            if (r.Kind is "ready" or "lockout" && r.PrimeHolstered == 0 && (!timed || r.Duration < 0 || number)) { return "A ready or lockout rule needs a positive duration and a condition key"; }
            // A skill_out buff (ADR 0032) lasts as long as its skill's use, so it alone takes no duration.
            if (r.Kind == "buff" && (r.Duration < 0 || !timed && r.ConsumeOn.Length == 0 && r.On != "skill_out" || number || r.Max != 0)) { return "A buff needs a positive duration, or a consumeOn event, and a condition key"; }
            // Only spending ends such a buff, so one moment starts its cooldown, and it belongs to the player, not to one enemy.
            if (r.CooldownFromEnd && (r.Kind != "buff" || r.Every <= 0 || r.ConsumeOn.Length == 0 || r.ConsumeOn == "target_death" || r.Duration != 0 || r.DurationStat.Length > 0 ||
                r.ResetOn.Length > 0 || r.AlsoResetOn.Length > 0 || r.CancelOn.Length > 0))
            { return "cooldownFromEnd needs a buff with every and consumeOn, and no duration, resetOn, cancelOn or target_death"; }
            if (r.Purge && r.Kind != "stacks") { return "Only stacks rules purge"; }
            if (r.Kind == "stacks" && (r.Max < 1 || !number || r.Duration < 0)) { return "Stacks need a numeric input key and a positive cap"; }
            return r.Every < 0 ? "The trigger interval can't be negative" : null;
        }

        // Damage over a window comes round by a cooldown, or by its own end; a hit once per use has only a cooldown. Anything else reads as
        // no damage or no cycle in a fight.
        // A status with a rate of its own needs no window: without one, it goes on once, as the use deploys.
        private static bool CompleteSkillRule(SimSkillDefinition rule) => rule.Repair.Length > 0 || rule.ChargeFor.Length > 0 ? rule.Window.Length > 0 && rule.Cooldown.Length > 0 :
            rule.StatusRate.Length > 0 && rule.Rate.Length == 0 && rule.Window.Length == 0 ? rule.Cooldown.Length > 0 :
            rule.Rate.Length > 0 || rule.Status.Length > 0
                ? rule.Window.Length > 0 && (rule.Cooldown.Length > 0 || rule.CooldownFromEnd)
                : rule.Impact.Length > 0 && rule.Cooldown.Length > 0 && rule.Window.Length == 0;
    }
}
