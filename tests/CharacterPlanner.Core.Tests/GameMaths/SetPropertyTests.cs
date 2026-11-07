using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Gear-set passive properties, their four-piece thresholds, chest and backpack upgrades, hosts and isolation from
/// other stats (F05 set and brand bonuses), ported from Go's set_properties_g138_test.go, support_sets_g139_test.go,
/// combat_sets_g140_test.go and remaining_sets_g141_test.go.
/// </summary>
public sealed class SetPropertyTests
{
    private sealed record Upgrade(string Slot, string Talent, Dictionary<string, double>? Values = null);
    private sealed record PropertyCase(string Set, Dictionary<string, double> Base, Upgrade[] Upgrades);

    private static Dictionary<string, double> Values(params (string Id, double Value)[] values) => values.ToDictionary(value => value.Id, value => value.Value, StringComparer.Ordinal);

    private static readonly PropertyCase[] G138Cases =
    [
        new("umbra_initiative", Values(("umbra_light_stack_limit", 50), ("umbra_light_stack_gain", 10), ("umbra_light_stack_consumption", 10), ("umbra_light_normal_decay", 2), ("umbra_light_sprint_decay", 1), ("umbra_shadow_stack_limit", 50), ("umbra_shadow_stack_gain", 10), ("umbra_shadow_stack_consumption", 10), ("umbra_shadow_repair_per_stack", 0.008)),
            [new("chest", "from_the_shadows", Values(("umbra_light_stack_limit", 100), ("umbra_light_stack_gain", 20), ("umbra_light_stack_consumption", 20))), new("backpack", "into_the_light", Values(("umbra_shadow_stack_limit", 100), ("umbra_shadow_stack_gain", 20)))]),
        new("measured_assembly", Values(("huddle_overcharge_trigger_time", 4), ("huddle_overcharge_duration", 15), ("huddle_overcharge_cooldown", 40), ("huddle_destruction_base_cooldown", 10)),
            [new("chest", "hivemind", Values(("huddle_overcharge_cooldown", 25))), new("backpack", "smart_cooperation", Values(("huddle_destruction_base_cooldown", 1)))]),
        new("rigger", Values(("rigger_buff_duration", 10)), [new("chest", "best_buds")]),
        new("tip_of_the_spear", Values(("aggressive_recon_ammo_interval", 60)), [new("chest", "specialized_destruction"), new("backpack", "signature_moves")]),
    ];

    private static readonly PropertyCase[] G139Cases =
    [
        new("foundry_bulwark", Values(("makeshift_repairs_share", 0.25), ("makeshift_repairs_duration", 10)),
            [new("chest", "improved_materials", Values(("makeshift_repairs_duration", 5))), new("backpack", "process_refinery", Values(("makeshift_repairs_share", 0.35)))]),
        new("eclipse_protocol", Values(("indirect_transmission_radius", 10), ("indirect_transmission_refresh", 0.5)),
            [new("chest", "proliferation", Values(("indirect_transmission_radius", 15), ("indirect_transmission_refresh", 0.75))), new("backpack", "symptom_aggravator")]),
        new("refactor", Values(("return_to_sender_self_share", 0.1), ("return_to_sender_ally_share", 0.2), ("return_to_sender_bonus_armor_cap", 0)),
            [new("chest", "increased_interest", Values(("return_to_sender_self_share", 0.25), ("return_to_sender_ally_share", 0.35))), new("backpack", "over_engineered", Values(("return_to_sender_bonus_armor_cap", 0.8)))]),
        new("ortiz_reficere", Values(("nanite_duration_reduction", 0.9), ("nanite_range_reduction", 0.9), ("nanite_healing_efficiency", 1.5), ("nanite_ally_hazard_protection", 0.3), ("nanite_ally_buff_duration", 10), ("nanite_ally_cooldown", 10)),
            [new("chest", "overcharged_nanites", Values(("nanite_healing_efficiency", 2.25))), new("backpack", "improved_dampeners", Values(("nanite_duration_reduction", 0.25), ("nanite_range_reduction", 0.25)))]),
    ];

    private static readonly PropertyCase[] G140Cases =
    [
        new("cavalier", Values(("charging_protection_gain", 0.05), ("charging_protection_cap", 0.5), ("charged_share_duration", 10)),
            [new("chest", "overcharging", Values(("charging_protection_cap", 0.7))), new("backpack", "safe_charging", Values(("charging_protection_gain", 0.1)))]),
        new("hotshot", Values(("headache_next_headshot_bonus", 0.8), ("headache_armor_repair_share", 0.1), ("headache_bonus_armor_cap", 0.5), ("headache_miss_allowance", 0)),
            [new("chest", "stand_tall", Values(("headache_bonus_armor_cap", 1))), new("backpack", "lucky", Values(("headache_miss_allowance", 1)))]),
        new("system_corruption", Values(("hackstep_bonus_armor_grant", 0.5), ("hackstep_bonus_armor_duration", 5), ("hackstep_nameplate_duration", 5), ("hackstep_armor_repair_share", 0.2), ("hackstep_cooldown", 20), ("hackstep_damage_per_interval", 0.02), ("hackstep_damage_cap", 0.2)),
            [new("chest", "compiler_optimization", Values(("hackstep_cooldown", 15))), new("backpack", "multithreaded_execution", Values(("hackstep_bonus_armor_grant", 1)))]),
        new("ember_engine", Values(("combustion_burn_chance", 0.4), ("combustion_burn_damage_bonus", 0.25), ("combustion_debuff_duration_bonus", 0)),
            [new("chest", "flashpoint", Values(("combustion_burn_chance", 0.6))), new("backpack", "white_hot", Values(("combustion_debuff_duration_bonus", 0.5)))]),
    ];

    private static readonly PropertyCase[] G141Cases =
    [
        new("aegis", Values(("stoic_resistance_per_enemy", 0.04), ("stoic_decoy_target_eligibility", 0)),
            [new("chest", "deceit", Values(("stoic_decoy_target_eligibility", 1))), new("backpack", "polyethylene_plating", Values(("stoic_resistance_per_enemy", 0.05)))]),
        new("core_strength", Values(("core_exercise_conversion_share", 0.4), ("core_exercise_skill_tier_basis", 0.15)),
            [new("chest", "inner_core", Values(("core_exercise_conversion_share", 0.75))), new("backpack", "outer_core", Values())]),
        new("ortiz_exuro", Values(("exuro_turret_rotation", 360), ("exuro_own_fire_immunity", 1), ("exuro_disabled_explosion", 1), ("exuro_chain_ignite_radius", 0), ("exuro_heatstroke_weapon_bonus", 0), ("exuro_heatstroke_range_bonus", 0)),
            [new("chest", "chain_combustion", Values(("exuro_chain_ignite_radius", 10))), new("backpack", "heatstroke", Values(("exuro_heatstroke_weapon_bonus", 0.4), ("exuro_heatstroke_range_bonus", 0.25)))]),
        new("eclipse_protocol", Values(("symptom_aggravator_damage_bonus", 0)), [new("backpack", "symptom_aggravator", Values(("symptom_aggravator_damage_bonus", 0.3)))]),
        new("ongoing_directive", Values(("trauma_bleed_duration_bonus", 0), ("trauma_bleed_damage_bonus", 0)), [new("backpack", "trauma_specialist", Values(("trauma_bleed_duration_bonus", 0.5), ("trauma_bleed_damage_bonus", 1)))]),
        new("rigger", Values(("rigger_cancel_reset_available", 0)), [new("backpack", "complete_uptime", Values(("rigger_cancel_reset_available", 1)))]),
    ];

    /// <summary>The first pieces of a set, each in its own slot, as Go's g54SetBuild.</summary>
    private static BuildSnapshot SetBuild(string setId, int pieces)
    {
        var set = Compiled.Pack.Sets.FirstOrDefault(set => set.ID == setId) ?? throw new InvalidOperationException($"missing set {setId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G54 set talent regression: " + setId);
        foreach (var itemId in set.Items.Take(pieces))
        {
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
            if (item is not null) { b = b.WithEquipment(item.Slot, item.ID); }
        }
        return b;
    }

    /// <summary>A talent selected on the equipped item's talent slot that offers it, as Go's g54SelectUpgrade.</summary>
    private static BuildSnapshot SelectUpgrade(BuildSnapshot b, string slot, string talentId)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(talentSlot is not null, $"{talentId} is not selectable on equipped {slot} item {itemId}");
        return b.WithTalentSelection(slot, talentSlot.ID, talentId);
    }

    /// <summary>A stat's four-piece contribution from a set, as Go's g54ExpectSetValue.</summary>
    private static void ExpectSetValue(ResolvedBuild result, string stat, string setId, double want)
    {
        var contribution = result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)
            ? breakdown.Contributions.FirstOrDefault(contribution => contribution.Source.Kind == "set" && contribution.Source.Id == setId && contribution.Source.Name.EndsWith("(4pc)", StringComparison.Ordinal))
            : null;
        Assert.True(contribution is not null, $"{stat} has no four-piece contribution from {setId}");
        Near(contribution.Value, want, 1e-12, $"{setId} four-piece {stat}");
    }

    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    private static string WrongSlot(string slot) => slot == "chest" ? "backpack" : "chest";

    /// <summary>
    /// G138: the passive properties need four pieces, each upgrade changes its own values only when selected on its
    /// own slot's set item, and with both upgrades they are the set's four-piece contribution.
    /// </summary>
    [Fact]
    public void G138PropertiesThresholdsHostsAndUpgrades()
    {
        foreach (var tc in G138Cases)
        {
            foreach (var pieces in new[] { 3, 4, 5 })
            {
                var b = SetBuild("gear_set_" + tc.Set, pieces);
                var both = b;
                var plain = Stats(b);
                foreach (var (id, value) in tc.Base) { Near(plain.Of(id), pieces < 4 ? 0 : value, 1e-12, $"{tc.Set} {pieces} pieces {id}"); }
                foreach (var upgrade in tc.Upgrades)
                {
                    var talent = "gear_talent_" + upgrade.Talent;
                    var selected = SelectUpgrade(b, upgrade.Slot, talent);
                    var wrong = b.WithTalentSelection(WrongSlot(upgrade.Slot), "talent", talent);
                    ImmutableDictionary<string, double> selectedStats = Stats(selected), clearedStats = Stats(selected.WithTalentSelection(upgrade.Slot, "talent", "")), wrongStats = Stats(wrong);
                    var staleStats = pieces == 5 ? Stats(selected.WithEquipment(upgrade.Slot, upgrade.Slot + "_belstone_armory_" + upgrade.Slot)) : null;
                    foreach (var (id, value) in tc.Base)
                    {
                        var want = upgrade.Values?.TryGetValue(id, out var v) == true ? v : value;
                        var baseValue = value;
                        if (pieces < 4) { (want, baseValue) = (0, 0); }
                        var what = $"{tc.Set} {pieces} pieces {upgrade.Talent} {id}";
                        Near(selectedStats.Of(id), want, 1e-12, what + " selected");
                        Near(clearedStats.Of(id), baseValue, 1e-12, what + " cleared");
                        Near(wrongStats.Of(id), baseValue, 1e-12, what + " on the wrong slot");
                        if (staleStats is not null) { Near(staleStats.Of(id), baseValue, 1e-12, what + " with a Belstone host"); }
                    }
                    both = SelectUpgrade(both, upgrade.Slot, talent);
                }
                var bothResult = Resolve(both);
                foreach (var (id, value) in tc.Base)
                {
                    var want = value;
                    foreach (var upgrade in tc.Upgrades)
                    {
                        if (upgrade.Values?.TryGetValue(id, out var v) == true) { want = v; }
                    }
                    if (pieces < 4) { Near(bothResult.Calculation.Stats.Of(id), 0, 1e-12, $"{tc.Set} {pieces} pieces both upgrades {id}"); }
                    else { ExpectSetValue(bothResult, id, "gear_set_" + tc.Set, want); }
                }
            }
        }
    }

    /// <summary>G138: the passive properties don't depend on the sets' active-state assumptions.</summary>
    [Fact]
    public void G138PropertiesAreIndependentOfActiveState()
    {
        foreach (var tc in G138Cases)
        {
            var b = SetBuild("gear_set_" + tc.Set, 4);
            foreach (var upgrade in tc.Upgrades) { b = SelectUpgrade(b, upgrade.Slot, "gear_talent_" + upgrade.Talent); }
            var baseline = Stats(b);
            foreach (var flag in new[] { "false", "true" })
            {
                foreach (var allies in new[] { "0", "1", "7" })
                {
                    foreach (var stacks in new[] { "0", "50", "100" })
                    {
                        var r = Stats(b, Context(("umbra_light_active", flag), ("umbra_light_stacks", stacks), ("huddle_qualifying_allies", allies), ("rigger_skill_buff_active", flag),
                            ("aggressive_recon_active", flag), ("aggressive_recon_reload_active", flag), ("signature_moves_active", flag), ("weapon_slot", "primary_weapon")));
                        foreach (var id in tc.Base.Keys) { Near(r.Of(id), baseline.Of(id), 1e-12, $"{tc.Set} {id} flag={flag} allies={allies} stacks={stacks}"); }
                    }
                }
            }
        }
    }

    /// <summary>G138: Into the Light and Hivemind with Smart Cooperation change only their passive properties.</summary>
    [Fact]
    public void G138NewUpgradesOnlyChangePassiveProperties()
    {
        foreach (var set in new[] { "umbra_initiative", "measured_assembly" })
        {
            var b = SetBuild("gear_set_" + set, 4);
            var ctx = Context(("umbra_light_active", "true"), ("umbra_light_stacks", "100"), ("huddle_qualifying_allies", "7"));
            var baseline = Stats(b, ctx);
            HashSet<string> changed;
            BuildSnapshot upgraded;
            if (set == "measured_assembly")
            {
                upgraded = SelectUpgrade(SelectUpgrade(b, "backpack", "gear_talent_smart_cooperation"), "chest", "gear_talent_hivemind");
                changed = ["huddle_overcharge_cooldown", "huddle_destruction_base_cooldown"];
            }
            else
            {
                upgraded = SelectUpgrade(b, "backpack", "gear_talent_into_the_light");
                changed = ["umbra_shadow_stack_limit", "umbra_shadow_stack_gain", "umbra_shadow_full_stack_repair_capacity"];
            }
            var active = Stats(upgraded, ctx);
            foreach (var (id, value) in baseline)
            {
                if (!changed.Contains(id)) { Near(active.Of(id), value, 1e-12, $"{set} upgrade changed {id}"); }
            }
        }
    }

    /// <summary>
    /// The shared body of G139, G140 and G141: thresholds, each upgrade alone (selected, cleared, on the wrong slot
    /// and on a Belstone host), and with all upgrades the expected four-piece values while every other stat matches
    /// the unupgraded build, except the stats <paramref name="special"/> checks itself.
    /// </summary>
    private static void CheckPropertiesAndIsolation(PropertyCase[] cases, Func<string, ImmutableDictionary<string, string>> context,
        Func<PropertyCase, string, int, ImmutableDictionary<string, double>, bool> special)
    {
        foreach (var tc in cases)
        {
            foreach (var pieces in new[] { 3, 4, 5 })
            {
                var b = SetBuild("gear_set_" + tc.Set, pieces);
                var selected = b;
                var expected = new Dictionary<string, double>(tc.Base, StringComparer.Ordinal);
                var plain = Stats(b);
                foreach (var (id, value) in tc.Base) { Near(plain.Of(id), pieces < 4 ? 0 : value, 1e-12, $"{tc.Set} {pieces} pieces {id}"); }
                foreach (var upgrade in tc.Upgrades)
                {
                    var talent = "gear_talent_" + upgrade.Talent;
                    var alone = SelectUpgrade(b, upgrade.Slot, talent);
                    ImmutableDictionary<string, double> aloneStats = Stats(alone), clearedStats = Stats(alone.WithTalentSelection(upgrade.Slot, "talent", "")),
                        wrongStats = Stats(b.WithTalentSelection(WrongSlot(upgrade.Slot), "talent", talent));
                    var staleStats = pieces == 5 ? Stats(alone.WithEquipment(upgrade.Slot, upgrade.Slot + "_belstone_armory_" + upgrade.Slot)) : null;
                    foreach (var (id, value) in tc.Base)
                    {
                        var want = upgrade.Values?.TryGetValue(id, out var v) == true ? v : value;
                        var baseValue = value;
                        if (pieces < 4) { (want, baseValue) = (0, 0); }
                        var what = $"{tc.Set} {pieces} pieces {upgrade.Talent} {id}";
                        Near(aloneStats.Of(id), want, 1e-12, what + " selected");
                        Near(clearedStats.Of(id), baseValue, 1e-12, what + " cleared");
                        Near(wrongStats.Of(id), baseValue, 1e-12, what + " on the wrong slot");
                        if (staleStats is not null) { Near(staleStats.Of(id), baseValue, 1e-12, what + " with a Belstone host"); }
                    }
                    selected = SelectUpgrade(selected, upgrade.Slot, talent);
                    foreach (var (id, v) in upgrade.Values ?? []) { expected[id] = v; }
                }
                foreach (var flag in new[] { "false", "true" })
                {
                    var ctx = context(flag);
                    var baseline = Stats(b, ctx);
                    var r = Resolve(selected, ctx);
                    foreach (var (id, value) in expected)
                    {
                        var want = pieces < 4 ? 0 : value;
                        Near(r.Calculation.Stats.Of(id), want, 1e-12, $"{tc.Set} {pieces} pieces all upgrades {id} flag={flag}");
                        if (pieces >= 4) { ExpectSetValue(r, id, "gear_set_" + tc.Set, want); }
                    }
                    foreach (var (id, value) in baseline)
                    {
                        if (special(tc, id, pieces, r.Calculation.Stats)) { continue; }
                        if (!tc.Base.ContainsKey(id)) { Near(r.Calculation.Stats.Of(id), value, 1e-12, $"{tc.Set} {pieces} pieces upgrades changed {id} flag={flag}"); }
                    }
                }
            }
        }
    }

    /// <summary>The shared body of G139, G140 and G141's NinjaBike tests: the five-piece set without its holster needs the backpack's Resourceful credit.</summary>
    private static void CheckNinjaBikeCredit(PropertyCase[] cases)
    {
        foreach (var tc in cases)
        {
            var b = SetBuild("gear_set_" + tc.Set, 5).WithEquipment("backpack", "backpack_ninjabike_messenger_backpack").WithEquipment("holster", "");
            var selected = SelectUpgrade(b, "backpack", "gear_talent_resourceful");
            ImmutableDictionary<string, double> without = Stats(b), with = Stats(selected), cleared = Stats(selected.WithTalentSelection("backpack", "talent", ""));
            foreach (var (id, want) in tc.Base)
            {
                Near(without.Of(id), 0, 1e-12, $"{tc.Set} {id} without Resourceful");
                Near(with.Of(id), want, 1e-12, $"{tc.Set} {id} with Resourceful");
                Near(cleared.Of(id), 0, 1e-12, $"{tc.Set} {id} with Resourceful cleared");
            }
        }
    }

    /// <summary>
    /// G139: Foundry Bulwark, Eclipse Protocol, Refactor and Ortiz: Reficere properties and upgrades, isolated from
    /// other stats (Return to Sender's bonus armor capacity follows its cap, Symptom Aggravator's bonus is its own).
    /// </summary>
    [Fact]
    public void G139SupportSetPropertiesAndIsolation()
    {
        CheckPropertiesAndIsolation(G139Cases, flag => Context(("full_armor", flag), ("target_status_affected", "false"), ("weapon_slot", "primary_weapon")), (tc, id, pieces, r) =>
        {
            if (id == "return_to_sender_bonus_armor_capacity")
            {
                Near(r.Of(id), r.Of("character_armor") * r.Of("return_to_sender_bonus_armor_cap"), 1e-8, $"{tc.Set} {pieces} pieces {id}");
                return true;
            }
            return id == "symptom_aggravator_damage_bonus";
        });
    }

    /// <summary>G139: the support sets' properties count NinjaBike's Resourceful credit only while it's selected.</summary>
    [Fact]
    public void G139SupportSetsRespectSelectedNinjaBikeCredit() => CheckNinjaBikeCredit(G139Cases);

    /// <summary>
    /// G140: Cavalier, Hotshot, System Corruption and Ember Engine properties and upgrades, isolated from other stats
    /// (Cavalier's full charge time and Hackstep's available bonus armor follow the four-piece bonus).
    /// </summary>
    [Fact]
    public void G140CombatSetPropertiesAndIsolation()
    {
        CheckPropertiesAndIsolation(G140Cases, flag => Context(("full_armor", flag), ("target_status_affected", flag), ("weapon_slot", "primary_weapon")), (tc, id, pieces, r) =>
        {
            if (tc.Set == "cavalier" && id == "charging_full_charge_time")
            {
                Near(r.Of(id), pieces >= 4 ? 7 : 0, 1e-12, $"{tc.Set} {pieces} pieces {id}");
                return true;
            }
            if (tc.Set == "system_corruption" && id == "hackstep_available_bonus_armor")
            {
                Near(r.Of(id), pieces >= 4 ? r.Of("character_armor") : 0, 1e-8, $"{tc.Set} {pieces} pieces {id}");
                return true;
            }
            return false;
        });
    }

    /// <summary>G140: the combat sets' properties count NinjaBike's Resourceful credit only while it's selected.</summary>
    [Fact]
    public void G140CombatSetsRespectSelectedNinjaBikeCredit() => CheckNinjaBikeCredit(G140Cases);

    /// <summary>
    /// G141: Aegis, Core Strength, Ortiz: Exuro, Eclipse Protocol, Ongoing Directive and Rigger properties and
    /// upgrades, isolated from other stats.
    /// </summary>
    [Fact]
    public void G141CombatSetPropertiesAndIsolation() =>
        CheckPropertiesAndIsolation(G141Cases, flag => Context(("full_armor", flag), ("target_status_affected", "false"), ("weapon_slot", "primary_weapon")), (_, _, _, _) => false);

    /// <summary>G141: the remaining sets' properties count NinjaBike's Resourceful credit only while it's selected.</summary>
    [Fact]
    public void G141CombatSetsRespectSelectedNinjaBikeCredit() => CheckNinjaBikeCredit(G141Cases);
}
