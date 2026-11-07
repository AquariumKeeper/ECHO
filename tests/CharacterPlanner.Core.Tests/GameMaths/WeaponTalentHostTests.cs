using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Weapon talents' host restrictions, selected-slot gates, explicit boolean and bounded stack assumptions, and how
/// their bonuses compose in the weapon damage pools with provenance (F04/F05 modifier aggregation, conditional
/// modifiers and provenance; F10 configured equipment talents), ported from Go's talent_effects_g40_test.go and
/// talent_effects_g41_test.go.
/// </summary>
public sealed class WeaponTalentHostTests
{
    private sealed record G40Case(string Talent, string Gate, string Stat, double Value, string Input, string OrdinaryHost, string[] FixedHosts, string WeaponType);

    private static readonly G40Case[] G40Cases =
    [
        new("weapon_talent_vindictive", "vindictive_active", "critical_hit_chance", .16, "", "primary_weapon_acr", [], ""),
        new("weapon_talent_perfect_vindictive", "vindictive_active", "critical_hit_chance", .21, "", "", ["primary_weapon_the_grudge"], ""),
        new("weapon_talent_on_empty", "on_empty_active", "weapon_handling", .60, "", "primary_weapon_acr", [], "assault_rifle"),
        new("weapon_talent_perfect_on_empty", "on_empty_active", "weapon_handling", .80, "", "", ["primary_weapon_burn_out"], ""),
        new("weapon_talent_unwavering", "unwavering_active", "weapon_handling", 3, "", "primary_weapon_mp5_st", [], "smg"),
        new("weapon_talent_perfect_unwavering", "unwavering_active", "weapon_handling", 4, "", "", ["primary_weapon_swap_chain", "sidearm_sharpshooter_s_93r"], ""),
        new("weapon_talent_overwhelm", "", "weapon_damage", .10, "overwhelm_stacks", "primary_weapon_stoner_lamg", [], "lmg"),
        new("weapon_talent_perfect_overwhelm", "", "weapon_damage", .12, "overwhelm_stacks", "", ["primary_weapon_quiet_roar"], ""),
        new("weapon_talent_pumped_up", "", "weapon_damage", .012, "pumped_up_stacks", "primary_weapon_acs_12", [], "shotgun"),
        new("weapon_talent_perfect_pumped_up", "", "weapon_damage", .06, "pumped_up_stacks", "", ["primary_weapon_boomstick"], ""),
        new("weapon_talent_stabilize", "", "accuracy", .01, "stabilize_stacks", "primary_weapon_acr", [], "assault_rifle"),
        new("weapon_talent_perfect_stabilize", "", "accuracy", .01, "stabilize_stacks", "", ["primary_weapon_born_great", "primary_weapon_grown_great"], ""),
        new("weapon_talent_streamline", "streamline_active", "weapon_damage", .42, "", "primary_weapon_acr", [], ""),
        new("weapon_talent_perfect_streamline", "streamline_active", "weapon_damage", .47, "", "", ["primary_weapon_lud", "primary_weapon_purist"], ""),
        // Soft Spot's 27% is an in-game reading (G286). Perfect Soft Spot's 32% replaced the stale 24% on 2026-11-01 (ShadeTech's
        // Soft Spot candidate: the game files and an in-game tooltip reading agree).
        new("weapon_talent_soft_spot", "soft_spot_active", "weapon_damage", .27, "", "primary_weapon_acr", [], ""),
        new("weapon_talent_perfect_soft_spot", "soft_spot_active", "weapon_damage", .32, "", "", ["primary_weapon_achilles", "primary_weapon_instigator"], ""),
    ];

    private sealed record G41Case(string Talent, string Gate, string Stat, double Value, string OtherStat, double OtherValue, bool Passive);

    private static readonly G41Case[] G41Cases =
    [
        new("weapon_talent_ranger", "", "weapon_damage_multiplier", .02, "", 0, false),
        new("weapon_talent_perfect_ranger", "", "weapon_damage_multiplier", .02, "", 0, false),
        new("weapon_talent_pressure_point", "target_status_affected", "weapon_damage_multiplier", .15, "", 0, false),
        new("weapon_talent_perfect_pressure_point", "target_status_affected", "weapon_damage_multiplier", .20, "", 0, false),
        new("weapon_talent_behind_you", "target_not_targeting_player", "weapon_damage_multiplier", .20, "", 0, false),
        new("weapon_talent_perfect_behind_you", "target_not_targeting_player", "weapon_damage_multiplier", .25, "", 0, false),
        new("weapon_talent_outsider", "outsider_active", "accuracy", 1, "optimal_range", 1, false),
        new("weapon_talent_perfect_outsider", "outsider_active", "accuracy", 1.25, "optimal_range", 1.5, false),
        new("weapon_talent_near_sighted", "", "stability", .8, "optimal_range", -.35, true),
        new("weapon_talent_perfect_near_sighted", "", "stability", 1, "optimal_range", -.35, true),
    ];

    /// <summary>
    /// G40: each weapon talent is offered by its ordinary and fixed hosts in both weapon slots, applies its bonus
    /// only while its host slot is the selected weapon, and each ordinary talent is offered by its weapon type in
    /// both slots.
    /// </summary>
    [Fact]
    public void G40WeaponTalentHostsAndSelectedSlotGates()
    {
        foreach (var tc in G40Cases)
        {
            var hosts = tc.OrdinaryHost.Length > 0 ? [tc.OrdinaryHost, .. tc.FixedHosts] : tc.FixedHosts;
            foreach (var listedHost in hosts)
            {
                var hostIds = listedHost.StartsWith("primary_weapon_", StringComparison.Ordinal)
                    ? new[] { listedHost, "secondary_weapon_" + listedHost["primary_weapon_".Length..] }
                    : [listedHost];
                foreach (var hostId in hostIds)
                {
                    var what = $"{tc.Talent}/{hostId}";
                    Assert.True(Offers(hostId, tc.Talent), $"{tc.Talent} not offered by expected host {hostId}");
                    var slot = Compiled.Pack.Items.Single(item => item.ID == hostId).Slot;
                    var b = G40Build(tc.Talent, hostId, slot);
                    if (tc.Gate.Length > 0) { b = b.WithActiveSkillSelection("skill_slot_1", "skill_assault_turret"); }
                    var baseline = Stats(b, Weapon(slot));
                    var ctx = Weapon(slot);
                    if (tc.Gate.Length > 0) { ctx = ctx.SetItem(tc.Gate, "true"); }
                    if (tc.Input.Length > 0) { ctx = ctx.SetItem(tc.Input, "1"); }
                    var active = Resolve(b, ctx);
                    NoErrors(active, what);
                    Near(active.Calculation.Stats.Of(tc.Stat), baseline.Of(tc.Stat) + tc.Value, 1e-12, $"{what} {tc.Stat}");
                    if (tc.Stat == "accuracy") { Near(active.Calculation.Stats.Of("stability"), baseline.Of("stability") + tc.Value, 1e-12, $"{what} stability"); }
                    Assert.True(HasContribution(active, tc.Stat, "talent", tc.Talent), $"{what} missing {tc.Stat} provenance");

                    var wrongSlot = slot is "primary_weapon" or "sidearm" ? "secondary_weapon" : "primary_weapon";
                    var inactive = Resolve(b, ctx.SetItem("weapon_slot", wrongSlot));
                    Near(inactive.Calculation.Stats.Of(tc.Stat), baseline.Of(tc.Stat), 1e-12, $"{what} on {wrongSlot}");
                    Assert.False(HasContribution(inactive, tc.Stat, "talent", tc.Talent), $"{what} leaked into unselected {wrongSlot}");
                    Near(Stats(b).Of(tc.Stat), baseline.Of(tc.Stat), 1e-12, $"{what} without a selected weapon");
                }
            }
        }
        foreach (var tc in G40Cases.Where(tc => tc.WeaponType.Length > 0))
        {
            foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
            {
                var found = Compiled.Pack.Items.Any(item => item.Slot == slot && item.Tags.Contains("weapon_type_" + tc.WeaponType) && item.TalentSlots.Any(talentSlot => talentSlot.Choices.Contains(tc.Talent)));
                Assert.True(found, $"ordinary {tc.Talent} is not offered by a {tc.WeaponType} in {slot}");
            }
        }
    }

    /// <summary>
    /// G40: boolean talent assumptions apply only for an explicit "true"; the Sharpshooter 93R's Perfect
    /// Unwavering applies only while the sidearm is selected.
    /// </summary>
    [Fact]
    public void G40WeaponTalentBooleanAssumptionsRequireExplicitTrue()
    {
        foreach (var tc in G40Cases.Where(tc => tc.Gate.Length > 0))
        {
            var host = tc.OrdinaryHost.Length > 0 ? tc.OrdinaryHost : tc.FixedHosts[0];
            var slot = host.StartsWith("sidearm_", StringComparison.Ordinal) ? "sidearm" : "primary_weapon";
            var b = G40Build(tc.Talent, host, slot);
            foreach (var raw in new[] { "", "false", "invalid", "1" })
            {
                var ctx = raw.Length > 0 ? Context(("weapon_slot", "primary_weapon"), (tc.Gate, raw)) : Weapon("primary_weapon");
                var result = Resolve(b, ctx);
                var baseline = Stats(b, Weapon("primary_weapon"));
                Near(result.Calculation.Stats.Of(tc.Stat), baseline.Of(tc.Stat), 1e-12, $"{tc.Talent} with {tc.Gate}=\"{raw}\"");
                Assert.False(HasContribution(result, tc.Stat, "talent", tc.Talent), $"{tc.Talent} contributed with {tc.Gate}=\"{raw}\"");
            }
        }

        // The 93R is a Sharpshooter specialization sidearm. Its host availability
        // and weapon-slot gate both matter; a primary-weapon context cannot activate it.
        var sidearm = G40Build("weapon_talent_perfect_unwavering", "sidearm_sharpshooter_s_93r", "sidearm");
        CloseTo(Stats(sidearm, Context(("weapon_slot", "sidearm"), ("unwavering_active", "true"))).Of("weapon_handling"), 4, 1e-12);
        CloseTo(Stats(sidearm, Context(("weapon_slot", "primary_weapon"), ("unwavering_active", "true"))).Of("weapon_handling"), 0, 1e-12);
    }

    /// <summary>
    /// G40: stack talents add their bonus per stack up to the formula's cap for inputs within the input's range;
    /// out-of-range, fractional and NaN inputs are diagnosed and ignored.
    /// </summary>
    [Fact]
    public void G40WeaponTalentStackInputsAndInvalidDefaults()
    {
        foreach (var (talent, host, input, stat, perStack, formulaCap, inputMax) in new[]
        {
            ("weapon_talent_overwhelm", "primary_weapon_stoner_lamg", "overwhelm_stacks", "weapon_damage", .10, 4.0, 4.0),
            ("weapon_talent_perfect_overwhelm", "primary_weapon_quiet_roar", "overwhelm_stacks", "weapon_damage", .12, 4.0, 4.0),
            ("weapon_talent_pumped_up", "primary_weapon_acs_12", "pumped_up_stacks", "weapon_damage", .012, 25.0, 25.0),
            ("weapon_talent_perfect_pumped_up", "primary_weapon_boomstick", "pumped_up_stacks", "weapon_damage", .06, 5.0, 25.0),
            ("weapon_talent_stabilize", "primary_weapon_acr", "stabilize_stacks", "accuracy", .01, 60.0, 75.0),
            ("weapon_talent_perfect_stabilize", "primary_weapon_born_great", "stabilize_stacks", "accuracy", .01, 75.0, 75.0),
        })
        {
            var b = G40Build(talent, host, "primary_weapon").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Stats(b, Weapon("primary_weapon"));
            foreach (var raw in new[] { "0", Format(Math.Floor(inputMax / 2)), Format(inputMax) })
            {
                var what = $"{talent} with {input}={raw}";
                var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), (input, raw)));
                NoErrors(result, what);
                var applied = Math.Min(double.Parse(raw, CultureInfo.InvariantCulture), formulaCap);
                Near(result.Calculation.Stats.Of(stat), baseline.Of(stat) + perStack * applied, 1e-12, $"{what} {stat}");
                if (stat == "accuracy") { Near(result.Calculation.Stats.Of("stability"), baseline.Of("stability") + perStack * applied, 1e-12, $"{what} stability"); }
            }
            foreach (var raw in new[] { Format(inputMax + 1), "2.5", "NaN" })
            {
                var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), (input, raw)));
                Near(result.Calculation.Stats.Of(stat), baseline.Of(stat), 1e-12, $"{talent} with invalid {input}=\"{raw}\"");
                Assert.True(HasDiagnostic(result, "context-value-invalid", input), $"{input}=\"{raw}\" did not report invalid numeric context");
            }
            Assert.True(HasContribution(Resolve(b, Context(("weapon_slot", "primary_weapon"), (input, Format(inputMax)))), stat, "talent", talent), $"{talent} missing {stat} provenance");
        }
    }

    /// <summary>
    /// G40: bonuses to critical hit chance, weapon handling and accuracy don't change the equipped weapon's damage
    /// preview or the skill previews (Vindictive also adds its value to critical hit damage).
    /// </summary>
    [Fact]
    public void G40NonPreviewBonusesStayOutOfWeaponAndSkillOutputs()
    {
        foreach (var (talent, host, key, value, stat, amount) in new[]
        {
            ("weapon_talent_vindictive", "primary_weapon_acr", "vindictive_active", "true", "critical_hit_chance", .16),
            ("weapon_talent_perfect_vindictive", "primary_weapon_the_grudge", "vindictive_active", "true", "critical_hit_chance", .21),
            ("weapon_talent_on_empty", "primary_weapon_acr", "on_empty_active", "true", "weapon_handling", .60),
            ("weapon_talent_perfect_on_empty", "primary_weapon_burn_out", "on_empty_active", "true", "weapon_handling", .80),
            ("weapon_talent_unwavering", "primary_weapon_mp5_st", "unwavering_active", "true", "weapon_handling", 3),
            ("weapon_talent_perfect_unwavering", "primary_weapon_swap_chain", "unwavering_active", "true", "weapon_handling", 4),
            ("weapon_talent_stabilize", "primary_weapon_acr", "stabilize_stacks", "60", "accuracy", .60),
            ("weapon_talent_perfect_stabilize", "primary_weapon_born_great", "stabilize_stacks", "75", "accuracy", .75),
        })
        {
            var b = G40Build(talent, host, "primary_weapon").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Resolve(b, Weapon("primary_weapon"));
            var bs = baseline.Calculation.Stats;
            var active = Resolve(b, Context(("weapon_slot", "primary_weapon"), (key, value)));
            var s = active.Calculation.Stats;
            Near(s.Of(stat), bs.Of(stat) + amount, 1e-12, $"{talent} {stat}");
            if (talent.Contains("vindictive", StringComparison.Ordinal)) { Near(s.Of("critical_hit_damage"), bs.Of("critical_hit_damage") + amount, 1e-12, $"{talent} critical_hit_damage"); }
            Near(s.Of("equipped_weapon_damage"), bs.Of("equipped_weapon_damage"), 1e-8, $"{talent} equipped_weapon_damage");
            Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews), $"{talent} changed weapon damage or active-skill previews");
        }
    }

    /// <summary>
    /// G40: one stack of Pumped Up composes with the gear core, Watch and installed Expertise in additive weapon
    /// damage, Glass Cannon in the multiplier and Vigilance in total weapon damage.
    /// </summary>
    [Fact]
    public void G40WeaponDamageBuffsComposeInTheirExistingPools()
    {
        // Compose one stack of Pumped Up with gear Weapon Damage, Watch, installed
        // Expertise, Vigilance Total Weapon Damage, and Glass Cannon amplification.
        var b = G40Build("weapon_talent_pumped_up", "primary_weapon_acs_12", "primary_weapon")
            .WithEquipmentUpgrade("primary_weapon", "primary_weapon_acs_12", 20)
            .WithAccountUpgrade("watch_weapon_damage", 50)
            .WithEquipment("mask", "mask_n_5_11_tactical_mask")
            .WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", .15))
            .WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithTalentSelection("chest", "talent", "gear_talent_glass_cannon")
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_vigilance")
            .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("pumped_up_stacks", "1"), ("vigilance_active", "true")));
        NoErrors(result, "Pumped Up composition");
        var s = result.Calculation.Stats;
        CloseTo(s.Of("weapon_damage"), .462, 1e-12); // 15% core + 10% Watch + 20% Expertise + 1.2% talent.
        CloseTo(s.Of("weapon_damage_multiplier"), 1.25, 1e-12);
        CloseTo(s.Of("total_weapon_damage"), .25, 1e-12);
        CloseTo(s.Of("equipped_weapon_damage"), s.Of("weapon_base_damage") * (1 + s.Of("weapon_damage") + s.Of("shotgun_damage")) * s.Of("weapon_damage_multiplier") * (1 + s.Of("total_weapon_damage")), 1e-8);
        foreach (var (stat, talent) in new[] { ("weapon_damage", "weapon_talent_pumped_up"), ("weapon_damage_multiplier", "gear_talent_glass_cannon"), ("total_weapon_damage", "gear_talent_vigilance") })
        {
            Assert.True(HasContribution(result, stat, "talent", talent), $"{stat} missing {talent} provenance");
        }
        Assert.True(HasContribution(result, "weapon_damage", "account-upgrade", "watch_weapon_damage"), "additive Watch Weapon Damage provenance missing");
        Assert.True(SamePreviews(result.ActiveSkillPreviews, Resolve(b, Weapon("primary_weapon")).ActiveSkillPreviews), "weapon-talent assumptions changed active-skill previews");
    }

    /// <summary>
    /// G41: each ordinary talent is offered only on weapons of its allowed types (and on every one of them), and
    /// each perfect talent exactly on its named hosts.
    /// </summary>
    [Fact]
    public void G41WeaponTalentHostRestrictions()
    {
        string[] ordinaryTypes = ["weapon_type_assault_rifle", "weapon_type_smg", "weapon_type_lmg", "weapon_type_rifle", "weapon_type_marksman_rifle", "weapon_type_shotgun", "weapon_type_pistol"];
        var ordinary = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["weapon_talent_ranger"] = ordinaryTypes,
            ["weapon_talent_pressure_point"] = ordinaryTypes,
            ["weapon_talent_behind_you"] = ["weapon_type_rifle", "weapon_type_marksman_rifle"],
            ["weapon_talent_outsider"] = ["weapon_type_smg"],
            ["weapon_talent_near_sighted"] = ["weapon_type_assault_rifle"],
        };
        var perfectHosts = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["weapon_talent_perfect_ranger"] = ["primary_weapon_the_archivist", "secondary_weapon_the_archivist", "primary_weapon_ekim_s_long_stick", "secondary_weapon_ekim_s_long_stick", "primary_weapon_malinois", "secondary_weapon_malinois"],
            ["weapon_talent_perfect_pressure_point"] = ["primary_weapon_goalie", "secondary_weapon_goalie", "primary_weapon_rusty", "secondary_weapon_rusty"],
            ["weapon_talent_perfect_behind_you"] = ["primary_weapon_whisper", "secondary_weapon_whisper", "primary_weapon_brutus", "secondary_weapon_brutus"],
            ["weapon_talent_perfect_outsider"] = ["primary_weapon_safety_distance", "secondary_weapon_safety_distance"],
            ["weapon_talent_perfect_near_sighted"] = ["primary_weapon_glory_daze", "secondary_weapon_glory_daze"],
        };
        foreach (var (talent, allowedTypes) in ordinary)
        {
            var foundTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in TalentHosts(talent))
            {
                Assert.True(host.Slot is "primary_weapon" or "secondary_weapon" or "sidearm", $"{talent} appears on non-weapon slot {host.Slot}");
                var weaponTypes = WeaponTypes(host.Tags);
                Assert.True(weaponTypes.Length == 1 && allowedTypes.Contains(weaponTypes[0]),
                    $"{talent} is offered by {host.ID} with weapon types [{string.Join(", ", weaponTypes)}]; want only [{string.Join(", ", allowedTypes)}]");
                foundTypes.Add(weaponTypes[0]);
            }
            foreach (var weaponType in allowedTypes)
            {
                Assert.True(foundTypes.Contains(weaponType), $"{talent} has no supported host of type {weaponType}");
            }
        }
        foreach (var (talent, wantHosts) in perfectHosts)
        {
            var got = TalentHosts(talent).Select(host => host.ID).ToArray();
            var want = wantHosts.Order(StringComparer.Ordinal).ToArray();
            Assert.True(got.SequenceEqual(want), $"{talent} fixed hosts = [{string.Join(", ", got)}], want [{string.Join(", ", want)}]");
        }
    }

    /// <summary>
    /// G41: Ranger adds 2% weapon damage multiplier per whole step of target distance (4 m, or 3 m when perfect)
    /// up to 100 m; invalid distances are diagnosed and ignored, and no distance means no bonus.
    /// </summary>
    [Fact]
    public void G41RangerDistanceFormulaAndBounds()
    {
        foreach (var (talent, hostId, step) in new[] { ("weapon_talent_ranger", "primary_weapon_acr", 4.0), ("weapon_talent_perfect_ranger", "primary_weapon_the_archivist", 3.0) })
        {
            var hosts = TalentHosts(talent);
            Assert.True(hosts.Length > 0, $"no host offers {talent}");
            var host = hosts.FirstOrDefault(candidate => candidate.ID == hostId) ?? hosts[0];
            var b = TalentBuild(talent, host.ID).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Stats(b, Weapon(host.Slot));
            foreach (var distance in new[] { 0, 2, 3, 4, 7, 8, 99, 100 })
            {
                var what = $"{talent} at {distance} m";
                var result = Resolve(b, Context(("weapon_slot", host.Slot), ("ranger_target_distance_m", Format(distance))));
                NoErrors(result, what);
                var wantBonus = .02 * Math.Floor(distance / step);
                var s = result.Calculation.Stats;
                Near(s.Of("weapon_damage_multiplier"), baseline.Of("weapon_damage_multiplier") + wantBonus, 1e-12, $"{what} weapon_damage_multiplier");
                Near(s.Of("equipped_weapon_damage"), s.Of("weapon_base_damage") * (1 + s.Of("weapon_damage") + s.Of("assault_rifle_damage")) * s.Of("weapon_damage_multiplier") * (1 + s.Of("total_weapon_damage")), 1e-8, $"{what} equipped_weapon_damage");
                Assert.True(wantBonus == 0 || HasContribution(result, "weapon_damage_multiplier", "talent", talent), $"{what} produced no provenance");
            }
            foreach (var invalid in new[] { "-1", "101", "2.5", "NaN" })
            {
                var result = Resolve(b, Context(("weapon_slot", host.Slot), ("ranger_target_distance_m", invalid)));
                Assert.True(HasDiagnostic(result, "context-value-invalid", "ranger_target_distance_m"), $"{talent}: invalid distance \"{invalid}\" has no context-value-invalid diagnostic");
                Near(result.Calculation.Stats.Of("weapon_damage_multiplier"), baseline.Of("weapon_damage_multiplier"), 1e-12, $"{talent} at invalid distance \"{invalid}\"");
            }
            Near(Stats(b, Weapon(host.Slot)).Of("weapon_damage_multiplier"), baseline.Of("weapon_damage_multiplier"), 1e-12, $"{talent} without a distance");
        }
    }

    /// <summary>
    /// G41: on a representative host of each slot and weapon type, each talent applies its bonus (and any second
    /// stat) only while its host is the selected weapon and its flag is an explicit "true", leaves the weapon's
    /// optimal range and the skill previews alone, and keeps the damage preview consistent.
    /// </summary>
    [Fact]
    public void G41WeaponTalentsRequireSelectedWeaponAndExplicitFlags()
    {
        foreach (var tc in G41Cases)
        {
            foreach (var host in RepresentativeHosts(tc.Talent))
            {
                var what = $"{tc.Talent}/{host.ID}";
                var b = TalentBuild(tc.Talent, host.ID).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
                var baseline = Resolve(b.WithTalentSelection(host.Slot, "talent", ""), Weapon(host.Slot));
                var bs = baseline.Calculation.Stats;
                var activeContext = Weapon(host.Slot);
                if (tc.Gate.Length > 0) { activeContext = activeContext.SetItem(tc.Gate, "true"); }
                if (tc.Talent.Contains("ranger", StringComparison.Ordinal)) { activeContext = activeContext.SetItem("ranger_target_distance_m", "8"); }
                var active = Resolve(b, activeContext);
                NoErrors(active, what);
                var s = active.Calculation.Stats;
                Near(s.Of(tc.Stat), bs.Of(tc.Stat) + ExpectedContribution(tc, activeContext), 1e-12, $"{what} {tc.Stat}");
                if (tc.OtherStat.Length > 0) { Near(s.Of(tc.OtherStat), bs.Of(tc.OtherStat) + tc.OtherValue, 1e-12, $"{what} {tc.OtherStat}"); }
                Assert.True(HasContribution(active, tc.Stat, "talent", tc.Talent), $"{what} provenance missing from {tc.Stat}");
                Assert.True(tc.OtherStat.Length == 0 || HasContribution(active, tc.OtherStat, "talent", tc.Talent), $"{what} provenance missing from {tc.OtherStat}");
                if (tc.Gate.Length > 0)
                {
                    foreach (var raw in new[] { "", "false", "invalid", "1" })
                    {
                        var context = raw.Length > 0 ? Context(("weapon_slot", host.Slot), (tc.Gate, raw)) : Weapon(host.Slot);
                        var inactive = Resolve(b, context);
                        Near(inactive.Calculation.Stats.Of(tc.Stat), bs.Of(tc.Stat), 1e-12, $"{what} with {tc.Gate}=\"{raw}\" {tc.Stat}");
                        if (tc.OtherStat.Length > 0) { Near(inactive.Calculation.Stats.Of(tc.OtherStat), bs.Of(tc.OtherStat), 1e-12, $"{what} with {tc.Gate}=\"{raw}\" {tc.OtherStat}"); }
                        Assert.False(HasContribution(inactive, tc.Stat, "talent", tc.Talent), $"{what} contributed with {tc.Gate}=\"{raw}\"");
                    }
                }
                var wrongSlot = host.Slot == "secondary_weapon" ? "primary_weapon" : "secondary_weapon";
                var wrongSlotResult = Resolve(b, activeContext.SetItem("weapon_slot", wrongSlot));
                Near(wrongSlotResult.Calculation.Stats.Of(tc.Stat), bs.Of(tc.Stat), 1e-12, $"{what} on {wrongSlot} {tc.Stat}");
                if (tc.OtherStat.Length > 0) { Near(wrongSlotResult.Calculation.Stats.Of(tc.OtherStat), bs.Of(tc.OtherStat), 1e-12, $"{what} on {wrongSlot} {tc.OtherStat}"); }
                Assert.False(HasContribution(wrongSlotResult, tc.Stat, "talent", tc.Talent), $"{what} leaked into selected {wrongSlot}");
                Near(Stats(b).Of(tc.Stat), bs.Of(tc.Stat), 1e-12, $"{what} without a selected weapon");
                Near(s.Of("weapon_optimal_range"), bs.Of("weapon_optimal_range"), 1e-12, $"{what} weapon_optimal_range");
                if (!tc.Passive || tc.Stat == "weapon_damage_multiplier")
                {
                    Near(s.Of("equipped_weapon_damage"), ExpectedWeaponDamage(s, host.Tags), 1e-8, $"{what} equipped_weapon_damage");
                }
                Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews), $"{what} changed active skill previews");
            }
        }
    }

    /// <summary>
    /// G41: Perfect Pressure Point's multiplier composes with Glass Cannon in the weapon damage multiplier, beside
    /// the additive gear core, Watch and Expertise and Vigilance's total weapon damage.
    /// </summary>
    [Fact]
    public void G41WeaponDamageMultipliersComposeWithExistingPools()
    {
        const string talent = "weapon_talent_perfect_pressure_point";
        var hosts = TalentHosts(talent);
        Assert.True(hosts.Length > 0, "no host offers Perfect Pressure Point");
        var host = hosts.FirstOrDefault(candidate => candidate.Slot == "primary_weapon");
        Assert.True(host is not null, "Perfect Pressure Point has no primary weapon host");
        var b = TalentBuild(talent, host.ID)
            .WithEquipmentUpgrade(host.Slot, host.ID, 20)
            .WithAccountUpgrade("watch_weapon_damage", 50)
            .WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithTalentSelection("chest", "talent", "gear_talent_glass_cannon")
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_vigilance")
            .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var result = Resolve(b, Context(("weapon_slot", host.Slot), ("target_status_affected", "true"), ("vigilance_active", "true")));
        NoErrors(result, "Perfect Pressure Point composition");
        var s = result.Calculation.Stats;
        CloseTo(s.Of("weapon_damage"), .45, 1e-12); // 15% gear core + 10% Watch + 20% Expertise.
        CloseTo(s.Of("weapon_damage_multiplier"), 1.25 * 1.20, 1e-12);
        CloseTo(s.Of("total_weapon_damage"), .25, 1e-12);
        CloseTo(s.Of("equipped_weapon_damage"), ExpectedWeaponDamage(s, host.Tags), 1e-8);
        foreach (var (stat, id) in new[] { ("weapon_damage_multiplier", talent), ("weapon_damage_multiplier", "gear_talent_glass_cannon"), ("total_weapon_damage", "gear_talent_vigilance") })
        {
            Assert.True(HasContribution(result, stat, "talent", id), $"{stat} missing {id} provenance");
        }
        Assert.True(HasContribution(result, "weapon_damage", "account-upgrade", "watch_weapon_damage"), "additive Watch Weapon Damage provenance missing");
        Assert.True(SamePreviews(result.ActiveSkillPreviews, Resolve(b, Weapon(host.Slot)).ActiveSkillPreviews), "weapon talent context changed active skill previews");
    }

    /// <summary>
    /// Go's g40Build: ACRs and an M9 in the other weapon slots, the host in its slot with the talent selected, and
    /// the Sharpshooter specialization for a sidearm host.
    /// </summary>
    private static BuildSnapshot G40Build(string talent, string hostId, string slot)
    {
        var host = Compiled.Pack.Items.FirstOrDefault(item => item.ID == hostId);
        var talentSlot = host?.TalentSlots.LastOrDefault(candidate => candidate.Choices.Contains(talent));
        Assert.True(host is not null && talentSlot is not null, $"host {hostId} does not offer talent {talent}");
        Assert.True(host.Slot == slot, $"G40 host {hostId} occupies {host.Slot}, requested slot {slot}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G40 talent regression: " + talent);
        foreach (var (candidateSlot, item) in new[] { ("primary_weapon", "primary_weapon_acr"), ("secondary_weapon", "secondary_weapon_acr"), ("sidearm", "sidearm_military_m9") })
        {
            if (candidateSlot != host.Slot) { b = b.WithEquipment(candidateSlot, item); }
        }
        b = b.WithEquipment(host.Slot, hostId).WithTalentSelection(host.Slot, talentSlot.ID, talent);
        return host.Slot == "sidearm" ? b.WithSpecialization("sharpshooter") : b;
    }

    /// <summary>
    /// Go's division2TalentBuild (and g41Build): an ACR and a 15% weapon-damage mask core, with the talent selected
    /// on the host item that offers it.
    /// </summary>
    private static BuildSnapshot TalentBuild(string talent, string host)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == host && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        Assert.True(item is not null, $"no item offers talent {talent} as {host}");
        var talentSlot = item.TalentSlots.Last(slot => slot.Choices.Contains(talent));
        return BuildSnapshot.Create(Compiled.Pack, "Talent regression: " + talent)
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("mask", "mask_n_5_11_tactical_mask")
            .WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", 0.15))
            .WithEquipment(item.Slot, item.ID)
            .WithTalentSelection(item.Slot, talentSlot.ID, talent);
    }

    /// <summary>Go's g41TalentHosts: the items offering the talent, by id.</summary>
    private static ItemDefinition[] TalentHosts(string talent) =>
        [.. Compiled.Pack.Items.Where(item => item.Slot.Length > 0 && item.TalentSlots.Any(slot => slot.Choices.Contains(talent))).OrderBy(item => item.ID, StringComparer.Ordinal)];

    /// <summary>Go's g41RepresentativeHosts: every perfect host, or the first ordinary host of each slot and single weapon type.</summary>
    private static ItemDefinition[] RepresentativeHosts(string talent)
    {
        var hosts = TalentHosts(talent);
        if (talent.Contains("perfect_", StringComparison.Ordinal)) { return hosts; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. hosts.Where(host => WeaponTypes(host.Tags) is [var weaponType] && seen.Add(host.Slot + "/" + weaponType))];
    }

    /// <summary>Go's g41WeaponTypes.</summary>
    private static string[] WeaponTypes(ImmutableArray<string> tags) =>
        [.. tags.Where(tag => tag.StartsWith("weapon_type_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>Go's g41ExpectedContribution: Ranger's bonus per whole distance step, otherwise the case's value.</summary>
    private static double ExpectedContribution(G41Case tc, ImmutableDictionary<string, string> context)
    {
        double Distance() => double.TryParse(context.GetValueOrDefault("ranger_target_distance_m", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return tc.Talent switch
        {
            "weapon_talent_ranger" => .02 * Math.Floor(Distance() / 4),
            "weapon_talent_perfect_ranger" => .02 * Math.Floor(Distance() / 3),
            _ => tc.Value,
        };
    }

    /// <summary>Go's g41ExpectedWeaponDamage: the damage preview with the weapon's type damage.</summary>
    private static double ExpectedWeaponDamage(ImmutableDictionary<string, double> s, ImmutableArray<string> tags)
    {
        var byType = 0.0;
        foreach (var tag in tags)
        {
            byType += tag switch
            {
                "weapon_type_assault_rifle" => s.Of("assault_rifle_damage"),
                "weapon_type_lmg" => s.Of("lmg_damage"),
                "weapon_type_smg" => s.Of("smg_damage"),
                "weapon_type_rifle" => s.Of("rifle_damage"),
                "weapon_type_marksman_rifle" => s.Of("marksman_rifle_damage"),
                "weapon_type_shotgun" => s.Of("shotgun_damage"),
                "weapon_type_pistol" => s.Of("pistol_damage"),
                _ => 0,
            };
        }
        return s.Of("weapon_base_damage") * (1 + s.Of("weapon_damage") + byType) * s.Of("weapon_damage_multiplier") * (1 + s.Of("total_weapon_damage"));
    }

    /// <summary>Go's findTalentHost: the item offers the talent on one of its talent slots.</summary>
    private static bool Offers(string itemId, string talent) =>
        Compiled.Pack.Items.Any(item => item.ID == itemId && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    private static void NoErrors(ResolvedBuild result, string what) =>
        Assert.True(!result.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"), $"{what}: legal talent loadout produced an error diagnostic: {string.Join("; ", result.Diagnostics)}");

    private static bool HasDiagnostic(ResolvedBuild result, string code, string subject) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Subject == subject);

    /// <summary>Go's hasContribution on a stat's breakdown.</summary>
    private static bool HasContribution(ResolvedBuild result, string stat, string kind, string id) =>
        result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown) && breakdown.Contributions.Any(contribution => contribution.Source.Kind == kind && contribution.Source.Id == id);

    // Go compared skill previews with reflect.DeepEqual; this compares the same values.
    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && pair.Value.Outputs.Length == other.Outputs.Length && pair.Value.Outputs.Zip(other.Outputs).All(outputs => SameOutput(outputs.First, outputs.Second)));

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula && a.When == b.When
            && a.Value == b.Value && a.Kind == b.Kind && a.Inputs.Count == b.Inputs.Count && a.Inputs.All(pair => b.Inputs.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
