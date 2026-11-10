namespace CharacterPlanner.Core.Packs;

public static partial class PackValidator
{
    private sealed partial class Checker
    {
        private static readonly HashSet<string> Events = ["", "shot", "hit", "body_hit", "headshot", "crit", "kill", "crit_kill", "headshot_kill", "reload", "reload_end",
            "unarmored_hit", "swap", "swap_after_kill", "armor_break", "damaged", "swapped", "skill_hit", "status_applied", "shield_break", "second", "pickup", "target_death", "crit_pellet", "hit_pellet", "target_changed", "armor_kit", "skill_kill"];
        private static readonly HashSet<string> Statuses = ["burning", "bleeding", "shocked", "ensnared", "blinded", "disoriented"];
        private static readonly HashSet<string> TriggerKinds = ["buff", "stacks", "status", "magazine", "distance", "pellets", "ready", "lockout", "cycle", "target", "count"];

        public void Simulation()
        {
            DistanceDefaults();
            ArmorKit();
            var talents = pack.Talents.Select(talent => talent.ID).ToHashSet(StringComparer.Ordinal);
            // A rule belongs to a talent, or to a specialization node (R56). Node ids are unique only within their tree, so a rule
            // may only name one that's unique across trees and isn't also a talent.
            var nodes = pack.Specializations.SelectMany(specialization => specialization.Nodes).GroupBy(node => node.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var owners = talents.Concat(nodes.Where(node => node.Value == 1 && !talents.Contains(node.Key)).Select(node => node.Key)).ToHashSet(StringComparer.Ordinal);
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
                if (rule.ForceHeadshot && !(rule.Kind == "ready" && rule.On is "hit" or "shot" || rule.Kind == "stacks" && rule.ConsumeOn is "hit" or "shot"))
                { Error("bad-sim-trigger", path + ".forceHeadshot", "forceHeadshot needs a ready rule on hit or shot, or a stacks rule consumed on hit or shot, so the forced shot spends it"); }
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
                    if (rule.Kind is not ("buff" or "ready") || rule.On.Length == 0)
                    { Error("bad-sim-trigger", path + ".requires", "requires needs a buff or ready rule with an event"); }
                    Requires(rule.RequiresContext, path + ".requires");
                    // The fight decides once, at its start, so a key some rule changes during it can't gate a rule.
                    foreach (var key in rule.RequiresContext.Keys.Where(key => triggers.Any(other => other.Key == key)).Order(StringComparer.Ordinal))
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
                foreach (var id in new[] { rule.Rate, rule.Window, rule.Cooldown, rule.Impact, rule.Impacts, rule.Shield, rule.Regeneration, rule.Stock, rule.StatusRate, rule.StatusWindow, rule.ImpactInterval })
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
                else if (!CompleteSkillRule(rule))
                { Error("bad-sim-skill", path, "A skill rule needs a window with a rate or a status and a cycle, or an impact and a cooldown"); }
                if (rule.Impacts.Length > 0 && rule.Impact.Length == 0) { Error("bad-sim-skill", path, "impacts needs an impact"); }
                if (rule.ImpactInterval.Length > 0 && rule.Impacts.Length == 0) { Error("bad-sim-skill", path, "impactInterval needs impacts"); }
                if (rule.Status.Length > 0 && !Statuses.Contains(rule.Status)) { Error("bad-sim-skill", path, $"Unknown status {Q(rule.Status)}"); }
                if (rule.Stock.Length > 0 ? rule.Cooldown.Length == 0 || rule.CooldownFromEnd || rule.Shield.Length > 0 || !double.IsFinite(rule.StockInterval) || rule.StockInterval <= 0 : rule.StockInterval != 0)
                { Error("bad-sim-skill", path, "stock needs a cooldown that doesn't run from a use's end, and a positive stockInterval"); }
                if ((rule.StatusRate.Length > 0 || rule.StatusWindow.Length > 0 || rule.StatusArmorBonus.Length > 0) &&
                    (rule.StatusRate.Length == 0 || rule.StatusWindow.Length == 0 || !DamagingStatuses.Contains(rule.Status)))
                { Error("bad-sim-skill", path, "statusRate and statusWindow go together, on a rule with a burn or bleed"); }
                if (rule.StatusArmorBonus.Length > 0) { Stat(rule.StatusArmorBonus, path + ".statusArmorBonus"); }
                if (rule.RateRead.Length > 0 && (rule.RateRead != "live" || rule.Rate.Length == 0))
                { Error("bad-sim-skill", path, "rateRead is live, and needs a rate"); }
                if (rule.RateArmorBonus.Length > 0)
                {
                    if (rule.Rate.Length == 0) { Error("bad-sim-skill", path, "rateArmorBonus needs a rate"); }
                    Stat(rule.RateArmorBonus, path + ".rateArmorBonus");
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

        private static readonly HashSet<string> DamagingStatuses = ["burning", "bleeding"];
        private const double MinimumTickPeriod = 0.01;
        // Plague of the Outcasts runs 50; the cap keeps a fight's tick queue bounded.
        private const int MaximumTalentDamageStacks = 100;
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
            if (r.AlsoResetOn.Length > 0 && (r.Kind is not ("buff" or "stacks") || r.ResetOn.Length == 0 || r.AlsoResetOn == r.ResetOn || !Events.Contains(r.AlsoResetOn)))
            { return "alsoResetOn needs a buff or stacks rule with a resetOn, and another simulator event"; }
            if (r.Count != 0 && r.Kind is not ("buff" or "stacks" or "status") || r.Refill && r.Kind is not ("buff" or "stacks"))
            { return "Only buff, stacks and status rules take count, and only buff and stacks rules refill"; }
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
                // Lists read from a file aren't the default instance, which a record compares by reference.
                var plain = r with { Talent = "", Key = "", Kind = "", On = "", Within = 0, Max = 0, AtLeast = 0, Status = "", Phases = [], Also = [] };
                if (plain != new SimTriggerDefinition() || !r.Phases.IsDefaultOrEmpty || !r.Also.IsDefaultOrEmpty || !Events.Contains(r.On) || r.On == "target_death")
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
            if (r.Kind == "magazine")
            {
                if (r.Percent && number && r.Phases.Length == 0) { return null; }
                return r.Percent || r.Phases.Length < 2 || number ? "A magazine rule needs two or more phases on a condition key, or percent on a numeric input" : null;
            }
            // A holstered-gain stacks rule (Quick Draw) may gain on time alone, with no event.
            if (r.On.Length == 0 && !(r.Kind == "stacks" && r.GainWhileHolstered > 0) || !Events.Contains(r.On) || !Events.Contains(r.ResetOn) || !Events.Contains(r.ConsumeOn))
            { return "Trigger events must be simulator events"; }
            // A durationStat stands in for the duration (Clutch's post-kill window); its own field check follows.
            var timed = r.Duration > 0 || r.DurationStat.Length > 0;
            if (r.Kind is "ready" or "lockout" && (!timed || r.Duration < 0 || number)) { return "A ready or lockout rule needs a positive duration and a condition key"; }
            if (r.Kind == "buff" && (r.Duration < 0 || !timed && r.ConsumeOn.Length == 0 || number || r.Max != 0)) { return "A buff needs a positive duration, or a consumeOn event, and a condition key"; }
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
        private static bool CompleteSkillRule(SimSkillDefinition rule) => rule.StatusRate.Length > 0 && rule.Rate.Length == 0 && rule.Window.Length == 0 ? rule.Cooldown.Length > 0 :
            rule.Rate.Length > 0 || rule.Status.Length > 0
                ? rule.Window.Length > 0 && (rule.Cooldown.Length > 0 || rule.CooldownFromEnd)
                : rule.Impact.Length > 0 && rule.Cooldown.Length > 0 && rule.Window.Length == 0;
    }
}
