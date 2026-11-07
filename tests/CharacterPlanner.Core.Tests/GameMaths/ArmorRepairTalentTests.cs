using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Armor-repair talent events: Unbreakable, Entrench, Clutch and Preservation and their perfect versions (F34
/// repairs), ported from Go's armor_repair_g57_test.go.
/// </summary>
public sealed class ArmorRepairTalentTests
{
    private static readonly string[] EventStats =
        ["armor_repair_on_depletion_percent", "armor_repair_on_headshot_percent", "missing_armor_repair_on_crit_percent", "armor_repair_on_kill_over_time_percent"];

    /// <summary>
    /// Go's g57TalentBuild: the named host (in the slot, when given), or the first ordinary gear or weapon item (in
    /// the slot, when given) that offers the talent, equipped with the talent selected.
    /// </summary>
    private static (BuildSnapshot Build, string Slot, string Item, string TalentSlot) TalentBuild(string talentId, string host = "", string slot = "")
    {
        var tag = talentId.StartsWith("weapon_", StringComparison.Ordinal) ? "weapon" : "gear";
        var item = host.Length > 0
            ? Compiled.Pack.Items.FirstOrDefault(item => item.Name == host && (slot.Length == 0 || item.Slot == slot))
            : Compiled.Pack.Items.FirstOrDefault(item => item.Tags.Contains(tag) && !item.Tags.Contains("named") && !item.Tags.Contains("exotic")
                && (slot.Length == 0 || item.Slot == slot) && item.TalentSlots.Any(talentSlot => talentSlot.Choices.Contains(talentId)));
        Assert.True(item is not null && item.Slot.Length > 0, $"no host for {talentId} (host \"{host}\")");
        var talentSlot = item.TalentSlots.FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(talentSlot is not null, $"{item.Name} does not offer {talentId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G57 armor repair").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, talentSlot.ID, talentId);
        return (b, item.Slot, item.ID, talentSlot.ID);
    }

    private static double Stat(BuildSnapshot b, string stat, params (string Key, string Value)[] context) => Stats(b, Context(context)).Of(stat);

    /// <summary>Go's g57ExpectContribution: an exact flat talent contribution.</summary>
    private static void ExpectContribution(ResolvedBuild result, string stat, string talentId, double value)
    {
        var contribution = result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)
            ? breakdown.Contributions.FirstOrDefault(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId)
            : null;
        Assert.True(contribution is not null, $"no {stat} talent contribution for {talentId}");
        Assert.True(contribution.Op == "flat" && contribution.Value == value, $"{stat} {talentId} contribution=({contribution.Op},{contribution.Value}), want flat {value}");
    }

    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    /// <summary>
    /// G57: the repair stats exist at zero with breakdowns, and each repair talent fills only its own event stat
    /// under its assumption, as a flat talent contribution.
    /// </summary>
    [Fact]
    public void G57ArmorRepairRawStatsAndTalentEvents()
    {
        var statIds = Compiled.Pack.Stats.Select(stat => stat.ID).ToHashSet(StringComparer.Ordinal);
        foreach (var stat in new[]
        {
            "armor_regeneration_percent", "repair_skills", "total_skill_repair", "armor_on_kill", "armor_on_kill_percent",
            "bonus_armor_percent", "weapon_rpm", "weapon_magazine_size", "weapon_reload_time", "weapon_optimal_range",
            "optimal_range", "weapon_base_damage", "weapon_damage_multiplier", "total_weapon_damage", "equipped_weapon_damage",
            "character_armor", "character_health", "health",
        })
        {
            Assert.True(statIds.Contains(stat), $"Division 2 pack stat {stat} missing");
        }
        var empty = Resolve(BuildSnapshot.Create(Compiled.Pack));
        foreach (var stat in EventStats)
        {
            Assert.True(empty.Calculation.Stats.Of(stat) == 0, $"empty {stat} = {empty.Calculation.Stats.Of(stat)}, want 0");
            Assert.True(empty.Calculation.Breakdowns.ContainsKey(stat), $"raw {stat} breakdown missing");
        }
        foreach (var (id, host, stat, selector, activeValue, value) in new[]
        {
            ("gear_talent_unbreakable", "", "armor_repair_on_depletion_percent", "unbreakable_ready", "true", .95),
            ("gear_talent_perfect_unbreakable", "Zero F's", "armor_repair_on_depletion_percent", "unbreakable_ready", "true", 1),
            ("gear_talent_entrench", "", "armor_repair_on_headshot_percent", "entrench_ready", "true", .2),
            ("gear_talent_perfect_entrench", "Bober", "armor_repair_on_headshot_percent", "entrench_ready", "true", .2),
            ("gear_talent_clutch", "", "missing_armor_repair_on_crit_percent", "clutch_repair_state", "low_armor", .025),
            ("gear_talent_perfect_clutch", "Devil's Due", "missing_armor_repair_on_crit_percent", "clutch_repair_state", "low_armor", .025),
        })
        {
            var b = TalentBuild(id, host).Build;
            foreach (var inactive in new[] { "", "false", "unknown" })
            {
                var got = inactive.Length > 0 ? Stat(b, stat, (selector, inactive)) : Stat(b, stat);
                Assert.True(got == 0, $"{id} inactive {stat} = {got} for {selector}=\"{inactive}\"");
            }
            var active = Resolve(b, Context((selector, activeValue)));
            Near(active.Calculation.Stats.Of(stat), value, 1e-12, $"{id} active");
            ExpectContribution(active, stat, id, value);
            foreach (var other in EventStats.Where(other => other != stat))
            {
                Assert.True(active.Calculation.Stats.Of(other) == 0, $"{id} leaked into {other} = {active.Calculation.Stats.Of(other)}");
            }
        }
    }

    /// <summary>
    /// G57: Clutch takes either exclusive repair state, not both, and Unbreakable is withheld when cleared, removed
    /// or replaced by a talent the host doesn't allow.
    /// </summary>
    [Fact]
    public void G57ClutchSelectorsAndGearSelectionLifecycle()
    {
        foreach (var (id, host) in new[] { ("gear_talent_clutch", ""), ("gear_talent_perfect_clutch", "Devil's Due") })
        {
            var clutch = TalentBuild(id, host).Build;
            foreach (var state in new[] { "low_armor", "post_kill" })
            {
                Near(Stat(clutch, "missing_armor_repair_on_crit_percent", ("clutch_repair_state", state)), .025, 1e-12, $"{id} {state}");
            }
            var both = Stat(clutch, "missing_armor_repair_on_crit_percent", ("clutch_repair_state", "low_armor,post_kill"));
            Assert.True(both == 0, $"{id} non-exclusive Clutch state gave {both}");
        }
        var (b, slot, _, talentSlot) = TalentBuild("gear_talent_unbreakable");
        var active = ("unbreakable_ready", "true");
        var cleared = b.WithTalentSelection(slot, talentSlot, "");
        Assert.True(Stat(cleared, "armor_repair_on_depletion_percent", active) == 0, "cleared Unbreakable still contributes");
        var reselected = cleared.WithTalentSelection(slot, talentSlot, "gear_talent_unbreakable");
        Near(Stat(reselected, "armor_repair_on_depletion_percent", active), .95, 1e-12, "reselected Unbreakable");
        Assert.True(Stat(reselected.WithEquipment(slot, ""), "armor_repair_on_depletion_percent", active) == 0, "removed Unbreakable host still contributes");
        var invalid = Resolve(b.WithTalentSelection(slot, talentSlot, "gear_talent_perfect_unbreakable"), Context(active));
        Assert.True(invalid.Calculation.Stats.Of("armor_repair_on_depletion_percent") == 0 && invalid.Diagnostics.Any(diagnostic => diagnostic.Code == "talent-not-allowed"),
            $"unsupported talent not withheld: repair={invalid.Calculation.Stats.Of("armor_repair_on_depletion_percent")} diagnostics={string.Join("; ", invalid.Diagnostics)}");
    }

    /// <summary>
    /// G57: Perfect Preservation repairs over time on a body or headshot kill only while its weapon is the active
    /// one, in each weapon slot and with one in each of two slots.
    /// </summary>
    [Fact]
    public void G57PreservationSelectedWeaponGateAndKillTypes()
    {
        const string stat = "armor_repair_on_kill_over_time_percent";
        const string talent = "weapon_talent_perfect_preservation";
        foreach (var (weaponSlot, name, itemId) in new[]
        {
            ("primary_weapon", "Emeline's Guard", "primary_weapon_emeline_s_guard"),
            ("secondary_weapon", "Emeline's Guard", "secondary_weapon_emeline_s_guard"),
            ("sidearm", "P320 XCompact", "sidearm_p320_xcompact"),
        })
        {
            var (b, slot, _, talentSlot) = TalentBuild(talent, name, weaponSlot);
            Assert.True(slot == weaponSlot, $"host slot {slot}, want {weaponSlot}");
            var unequipped = b.WithEquipment(slot, "");
            var none = Stat(unequipped, stat, ("weapon_slot", weaponSlot), ("preservation_kill_type", "body"));
            Assert.True(none == 0, $"un-equipped {weaponSlot} Preservation = {none}");
            b = unequipped.WithEquipment(slot, itemId).WithTalentSelection(slot, talentSlot, talent);
            var body = Resolve(b, Context(("weapon_slot", weaponSlot), ("preservation_kill_type", "body")));
            var head = Resolve(b, Context(("weapon_slot", weaponSlot), ("preservation_kill_type", "headshot")));
            Near(body.Calculation.Stats.Of(stat), .15, 1e-12, $"{weaponSlot} body kill");
            Near(head.Calculation.Stats.Of(stat), .3, 1e-12, $"{weaponSlot} headshot kill");
            ExpectContribution(body, stat, talent, .15);
            ExpectContribution(head, stat, talent, .3);
            var wrongSlot = weaponSlot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            foreach (var context in new[] { Context(), Context(("weapon_slot", weaponSlot)), Context(("weapon_slot", weaponSlot), ("preservation_kill_type", "unknown")), Context(("weapon_slot", wrongSlot), ("preservation_kill_type", "body")) })
            {
                var got = Stats(b, context).Of(stat);
                Assert.True(got == 0, $"{weaponSlot} inactive Preservation = {got} for {string.Join(", ", context)}");
            }
        }
        var primary = TalentBuild(talent, "Emeline's Guard", "primary_weapon").Build;
        var (_, secondarySlot, _, secondaryTalent) = TalentBuild(talent, "Emeline's Guard", "secondary_weapon");
        var combined = primary.WithEquipment(secondarySlot, "secondary_weapon_emeline_s_guard").WithTalentSelection(secondarySlot, secondaryTalent, talent);
        Near(Stat(combined, stat, ("weapon_slot", "primary_weapon"), ("preservation_kill_type", "body")), .15, 1e-12, "both slots, primary body kill");
        Near(Stat(combined, stat, ("weapon_slot", "secondary_weapon"), ("preservation_kill_type", "headshot")), .3, 1e-12, "both slots, secondary headshot kill");
    }

    /// <summary>G57: ordinary Preservation on a generic weapon repairs 10% on a body kill and 20% on a headshot kill.</summary>
    [Fact]
    public void G57OrdinaryPreservationGenericWeaponValues()
    {
        var b = TalentBuild("weapon_talent_preservation", slot: "primary_weapon").Build;
        Near(Stat(b, "armor_repair_on_kill_over_time_percent", ("weapon_slot", "primary_weapon"), ("preservation_kill_type", "body")), .1, 1e-12, "body kill");
        Near(Stat(b, "armor_repair_on_kill_over_time_percent", ("weapon_slot", "primary_weapon"), ("preservation_kill_type", "headshot")), .2, 1e-12, "headshot kill");
    }

    /// <summary>
    /// G57: a ready Unbreakable changes no existing armor, health or weapon stat, and Unbreakable, Clutch and
    /// Preservation together fill their own event stats, Preservation only while its weapon is active.
    /// </summary>
    [Fact]
    public void G57ExistingStatsRemainUnchangedAndCompositeRepairsStayDistinct()
    {
        var selected = TalentBuild("gear_talent_unbreakable", slot: "chest").Build.WithEquipment("primary_weapon", "primary_weapon_acr");
        var inactive = Stats(selected, Context(("weapon_slot", "primary_weapon")));
        var active = Stats(selected, Context(("weapon_slot", "primary_weapon"), ("unbreakable_ready", "true")));
        foreach (var key in new[]
        {
            "health", "character_armor", "character_health", "armor_on_kill", "armor_on_kill_percent",
            "armor_regeneration_percent", "repair_skills", "total_skill_repair", "bonus_armor_percent", "weapon_damage",
            "equipped_weapon_damage", "weapon_base_damage", "weapon_damage_multiplier", "total_weapon_damage", "weapon_rpm",
            "weapon_magazine_size", "weapon_reload_time", "weapon_optimal_range", "optimal_range",
        })
        {
            Assert.True(inactive.ContainsKey(key) && active.ContainsKey(key), $"expected existing stat {key} in both results");
            Assert.True(active[key] == inactive[key], $"{key} changed from {inactive[key]} to {active[key]}");
        }

        var combined = BuildSnapshot.Create(Compiled.Pack, "G57 composite repair");
        foreach (var (talent, slot) in new[] { ("gear_talent_unbreakable", "chest"), ("gear_talent_clutch", "backpack"), ("weapon_talent_preservation", "primary_weapon") })
        {
            var (_, hostSlot, itemId, talentSlot) = TalentBuild(talent, slot: slot);
            combined = combined.WithEquipment(hostSlot, itemId).WithTalentSelection(hostSlot, talentSlot, talent);
        }
        var composite = Stats(combined, Context(("unbreakable_ready", "true"), ("clutch_repair_state", "low_armor"), ("preservation_kill_type", "body"), ("weapon_slot", "primary_weapon")));
        Near(composite.Of("armor_repair_on_depletion_percent"), .95, 1e-12, "Unbreakable");
        Near(composite.Of("missing_armor_repair_on_crit_percent"), .025, 1e-12, "Clutch");
        Near(composite.Of("armor_repair_on_kill_over_time_percent"), .1, 1e-12, "Preservation");
        Assert.True(composite.Of("armor_repair_on_headshot_percent") == 0, $"unselected Entrench unexpectedly contributed: {composite.Of("armor_repair_on_headshot_percent")}");
        var noOwner = Stats(combined, Context(("unbreakable_ready", "true"), ("clutch_repair_state", "low_armor"), ("preservation_kill_type", "body"), ("weapon_slot", "sidearm")));
        Near(noOwner.Of("armor_repair_on_depletion_percent"), .95, 1e-12, "Unbreakable with the sidearm active");
        Near(noOwner.Of("missing_armor_repair_on_crit_percent"), .025, 1e-12, "Clutch with the sidearm active");
        Assert.True(noOwner.Of("armor_repair_on_kill_over_time_percent") == 0, $"Preservation without its selected owner = {noOwner.Of("armor_repair_on_kill_over_time_percent")}");
    }
}
