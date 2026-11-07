using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Gear talents gated on transient active and stack assumptions, feeding the total weapon damage, total skill
/// damage and total skill repair pools with provenance (F04/F05 modifier aggregation, conditional modifiers and
/// provenance; F10 configured equipment talents), ported from Go's talent_effects_g35_test.go,
/// talent_effects_g36_test.go and talent_effects_g39_test.go.
/// </summary>
public sealed class GearTalentAssumptionTests
{
    private static readonly string[] G35UnaffectedStats =
        ["skill_damage", "repair_skills", "skill_haste", "weapon_damage", "total_weapon_damage", "weapon_damage_multiplier", "equipped_weapon_damage"];

    /// <summary>
    /// G35: Combined Arms and Tech Support add total skill damage while active, and Kinetic Momentum adds total
    /// skill damage and repair per retained stack up to a per-skill cap; the Watch stays in the separate skill
    /// damage and repair stats, and the assumptions don't survive the share code.
    /// </summary>
    [Fact]
    public void G35SkillDamageAndRepairTalentAssumptions()
    {
        foreach (var (name, talent, host, gate, stat, bonus, perfect) in new[]
        {
            ("Combined Arms", "gear_talent_combined_arms", "backpack_n_5_11_tactical_backpack", "combined_arms_active", "total_skill_damage", .25, false),
            ("Perfect Combined Arms", "gear_talent_perfect_combined_arms", "backpack_force_multiplier", "combined_arms_active", "total_skill_damage", .30, true),
            ("Tech Support", "gear_talent_tech_support", "backpack_n_5_11_tactical_backpack", "tech_support_active", "total_skill_damage", .25, false),
            ("Perfect Tech Support", "gear_talent_perfect_tech_support", "backpack_percussive_maintenance", "tech_support_active", "total_skill_damage", .25, true),
            ("Kinetic Momentum", "gear_talent_kinetic_momentum", "chest_n_5_11_tactical_chest", "", "", 0, false),
            ("Perfect Kinetic Momentum", "gear_talent_perfect_kinetic_momentum", "chest_impetus", "", "", 0, true),
        })
        {
            var b = TalentBuild(talent, host)
                .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret")
                .WithActiveSkillSelection("skill_slot_2", "skill_reviver_hive")
                .WithAccountUpgrade("watch_skill_damage", 50)
                .WithAccountUpgrade("watch_repair_skills", 50);
            var baseline = Resolve(b);
            var bs = baseline.Calculation.Stats;
            Assert.True(HasContribution(baseline, "skill_damage", "account-upgrade", "watch_skill_damage"), $"{name}: Watch Skill Damage is missing from the separate Skill Damage stat");
            Assert.True(HasContribution(baseline, "repair_skills", "account-upgrade", "watch_repair_skills"), $"{name}: Watch Repair Skills is missing from the separate Repair Skills stat");
            if (gate.Length > 0)
            {
                var inactive = Resolve(b, Context((gate, "false")));
                foreach (var pool in new[] { "total_skill_damage", "total_skill_repair" })
                {
                    Near(inactive.Calculation.Stats.Of(pool), bs.Of(pool), 1e-12, $"{name} inactive {pool}");
                    Assert.False(HasContribution(inactive, pool, "talent", talent), $"inactive {name} contributed to {pool}");
                }
                var active = Resolve(b, Context((gate, "true")));
                NoErrors(active, name);
                Near(active.Calculation.Stats.Of(stat), bs.Of(stat) + bonus, 1e-12, $"{name} {stat}");
                var other = stat == "total_skill_repair" ? "total_skill_damage" : "total_skill_repair";
                Near(active.Calculation.Stats.Of(other), bs.Of(other), 1e-12, $"{name} {other}");
                Assert.True(HasContribution(active, stat, "talent", talent), $"{name} contribution is missing from {stat} provenance");
                AssertG35Unaffected(active, baseline, name);
                continue;
            }

            // Stack assumptions are manual retained stack counts. No skill selection,
            // combat flag, or weapon slot is inferred by the resolver for this talent.
            var capPerSkill = perfect ? 18 : 15;
            foreach (var (stacksName, one, two) in new[] { ("zero", 0, 0), ("skill one", 4, 0), ("skill two", 0, 6), ("both skills", 4, 6), ("each cap", 18, 18), ("one cap", 18, 2) })
            {
                var what = $"{name} {stacksName}";
                var result = Resolve(b, Context(("kinetic_momentum_skill_1_stacks", Format(one)), ("kinetic_momentum_skill_2_stacks", Format(two))));
                NoErrors(result, what);
                double totalStacks = Math.Min(one, capPerSkill) + Math.Min(two, capPerSkill);
                Near(result.Calculation.Stats.Of("total_skill_damage"), bs.Of("total_skill_damage") + totalStacks * .015, 1e-12, $"{what} total_skill_damage");
                Near(result.Calculation.Stats.Of("total_skill_repair"), bs.Of("total_skill_repair") + totalStacks * .02, 1e-12, $"{what} total_skill_repair");
                if (totalStacks > 0)
                {
                    foreach (var pool in new[] { "total_skill_damage", "total_skill_repair" })
                    {
                        Assert.True(HasContribution(result, pool, "talent", talent), $"{what}: {talent} contribution is missing from {pool} provenance");
                    }
                }
                AssertG35Unaffected(result, baseline, what);
            }
            foreach (var raw in new[] { "-1", "1.5", "31", "NaN" })
            {
                var invalid = Resolve(b, Context(("kinetic_momentum_skill_1_stacks", raw), ("kinetic_momentum_skill_2_stacks", "0")));
                Assert.True(HasDiagnostic(invalid, "context-value-invalid", "kinetic_momentum_skill_1_stacks"), $"{name}: invalid stack value \"{raw}\" lacks a diagnostic");
                Near(invalid.Calculation.Stats.Of("total_skill_damage"), bs.Of("total_skill_damage"), 1e-12, $"{name} with invalid \"{raw}\" total_skill_damage");
                Near(invalid.Calculation.Stats.Of("total_skill_repair"), bs.Of("total_skill_repair"), 1e-12, $"{name} with invalid \"{raw}\" total_skill_repair");
            }
            // Gear talents apply regardless of which weapon is selected.
            var withoutWeapon = Stats(b, Context(("weapon_slot", "secondary_weapon"), ("kinetic_momentum_skill_1_stacks", "4"), ("kinetic_momentum_skill_2_stacks", "6")));
            Near(withoutWeapon.Of("total_skill_damage"), bs.Of("total_skill_damage") + .15, 1e-12, $"{name} with the secondary weapon selected");
        }

        // Kinetic Momentum uses retained stacks without selected skills.
        foreach (var (talent, host) in new[] { ("gear_talent_kinetic_momentum", "chest_n_5_11_tactical_chest"), ("gear_talent_perfect_kinetic_momentum", "chest_impetus") })
        {
            var b = TalentBuild(talent, host);
            var baseline = Stats(b);
            var result = Resolve(b, Context(("kinetic_momentum_skill_1_stacks", "4"), ("kinetic_momentum_skill_2_stacks", "6")));
            Near(result.Calculation.Stats.Of("total_skill_damage"), baseline.Of("total_skill_damage") + .15, 1e-12, $"{talent} without skills total_skill_damage");
            Near(result.Calculation.Stats.Of("total_skill_repair"), baseline.Of("total_skill_repair") + .20, 1e-12, $"{talent} without skills total_skill_repair");
            Assert.True(result.ActiveSkillPreviews.Count == 0, $"{talent}: manual stack assumption unexpectedly inferred selected skills: {string.Join(", ", result.ActiveSkillPreviews.Keys)}");
        }

        // Combined Arms and Tech Support share their backpack talent slot.
        foreach (var host in new[] { "backpack_n_5_11_tactical_backpack" })
        {
            var item = Compiled.Pack.Items.FirstOrDefault(candidate => candidate.ID == host);
            Assert.True(item is not null && item.TalentSlots.Length > 0, $"{host} missing its talent slot");
            foreach (var talent in new[] { "gear_talent_combined_arms", "gear_talent_tech_support" })
            {
                Assert.True(item.TalentSlots[0].Choices.Contains(talent), $"{host} does not offer {talent}");
            }
        }

        // Talent activation assumptions stay transient across the share code.
        foreach (var (talent, host) in new[]
        {
            ("gear_talent_combined_arms", "backpack_n_5_11_tactical_backpack"),
            ("gear_talent_tech_support", "backpack_n_5_11_tactical_backpack"),
            ("gear_talent_kinetic_momentum", "chest_n_5_11_tactical_chest"),
        })
        {
            var b = TalentBuild(talent, host);
            var decoded = ShareCode.Decode(ShareCode.Encode(b));
            // Go compared the builds with reflect.DeepEqual; this compares every shared field.
            Assert.True(ShareCodeTests.Describe(decoded) == ShareCodeTests.Describe(b), $"{talent} share round trip changed build");
            var context = Context(("combined_arms_active", "true"), ("tech_support_active", "true"), ("kinetic_momentum_skill_1_stacks", "4"), ("kinetic_momentum_skill_2_stacks", "6"));
            Assert.True(Stats(decoded, context).Of("total_skill_damage") != Stats(decoded).Of("total_skill_damage"), $"{talent} transient context did not change its expected resolved value");
        }
        var replaced = TalentBuild("gear_talent_combined_arms", "backpack_n_5_11_tactical_backpack").WithTalentSelection("backpack", "talent", "gear_talent_tech_support");
        var both = Resolve(replaced, Context(("combined_arms_active", "true"), ("tech_support_active", "true")));
        CloseTo(both.Calculation.Stats.Of("total_skill_damage"), .25, 1e-12);
        Assert.False(HasContribution(both, "total_skill_damage", "talent", "gear_talent_combined_arms"), "replaced Combined Arms still contributes alongside Tech Support");
    }

    /// <summary>G35: a backpack skill-damage talent and Kinetic Momentum on the chest compose in the total skill pools.</summary>
    [Fact]
    public void G35SkillTalentBonusesComposeAcrossGearSlots()
    {
        foreach (var (backpackTalent, backpackHost, chestTalent, chestHost, backpackBonus) in new[]
        {
            ("gear_talent_combined_arms", "backpack_n_5_11_tactical_backpack", "gear_talent_kinetic_momentum", "chest_n_5_11_tactical_chest", .25),
            ("gear_talent_perfect_combined_arms", "backpack_force_multiplier", "gear_talent_perfect_kinetic_momentum", "chest_impetus", .30),
            ("gear_talent_tech_support", "backpack_n_5_11_tactical_backpack", "gear_talent_kinetic_momentum", "chest_n_5_11_tactical_chest", .25),
            ("gear_talent_perfect_tech_support", "backpack_percussive_maintenance", "gear_talent_perfect_kinetic_momentum", "chest_impetus", .25),
        })
        {
            var what = $"{backpackTalent} + {chestTalent}";
            var b = TalentBuild(backpackTalent, backpackHost).WithEquipment("chest", chestHost).WithTalentSelection("chest", "talent", chestTalent);
            var baseline = Stats(b);
            var result = Resolve(b, Context(("combined_arms_active", "true"), ("tech_support_active", "true"), ("kinetic_momentum_skill_1_stacks", "4"), ("kinetic_momentum_skill_2_stacks", "6")));
            Near(result.Calculation.Stats.Of("total_skill_damage"), baseline.Of("total_skill_damage") + backpackBonus + .15, 1e-12, $"{what} total_skill_damage");
            Near(result.Calculation.Stats.Of("total_skill_repair"), baseline.Of("total_skill_repair") + .20, 1e-12, $"{what} total_skill_repair");
            Assert.True(HasContribution(result, "total_skill_damage", "talent", backpackTalent), $"combined damage lacks {backpackTalent} provenance");
            Assert.True(HasContribution(result, "total_skill_repair", "talent", chestTalent), $"combined repair lacks {chestTalent} provenance");
        }
    }

    /// <summary>
    /// G36: Overwatch adds total weapon and skill damage only for an explicit true, whichever weapon is selected,
    /// and Safeguard adds total skill repair only at full armor, neither changing the non-damage skill previews.
    /// </summary>
    [Fact]
    public void G36OverwatchAndSafeguardAssumptions()
    {
        foreach (var (name, talent, host, damage) in new[] { ("Overwatch", "gear_talent_overwatch", "chest_n_5_11_tactical_chest", .12), ("Perfect Overwatch", "gear_talent_perfect_overwatch", "chest_ferocious_calm", .14) })
        {
            var b = TalentBuild(talent, host)
                .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret")
                .WithActiveSkillSelection("skill_slot_2", "skill_reviver_hive")
                .WithAccountUpgrade("watch_weapon_damage", 50)
                .WithAccountUpgrade("watch_skill_damage", 50);
            var baseline = Stats(b);
            foreach (var context in new[]
            {
                Context(("overwatch_active", "false")),
                Context(("overwatch_active", "yes")),
                Context(("in_cover", "true")),
                Context(("overwatch_active", "false"), ("in_cover", "true")),
            })
            {
                var what = $"{name} with {string.Join(",", context.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}";
                var inactive = Resolve(b, context);
                foreach (var stat in new[] { "total_weapon_damage", "total_skill_damage" })
                {
                    Near(inactive.Calculation.Stats.Of(stat), baseline.Of(stat), 1e-12, $"{what} {stat}");
                    Assert.False(HasContribution(inactive, stat, "talent", talent), $"inactive {what} contributed to {stat}");
                }
            }

            var active = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("overwatch_active", "true")));
            var baselineWithWeapon = Resolve(b, Weapon("primary_weapon"));
            NoErrors(active, name);
            foreach (var stat in new[] { "total_weapon_damage", "total_skill_damage" })
            {
                Near(active.Calculation.Stats.Of(stat), baseline.Of(stat) + damage, 1e-12, $"{name} {stat}");
                Assert.True(HasContribution(active, stat, "talent", talent), $"{name} contribution is missing from {stat} provenance");
            }
            // Overwatch is independent of the selected weapon. Watch Weapon Damage
            // remains in the additive Weapon Damage stat, separate from its buff.
            Near(active.Calculation.Stats.Of("weapon_damage"), baselineWithWeapon.Calculation.Stats.Of("weapon_damage"), 1e-12, $"{name} weapon_damage");
            Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baselineWithWeapon.Calculation.Stats.Of("equipped_weapon_damage") * (1 + damage), 1e-8, $"{name} equipped_weapon_damage");
            var otherWeapon = Stats(b, Context(("weapon_slot", "secondary_weapon"), ("overwatch_active", "true")));
            Near(otherWeapon.Of("total_weapon_damage"), baseline.Of("total_weapon_damage") + damage, 1e-12, $"{name} on the secondary weapon total_weapon_damage");
            Near(otherWeapon.Of("total_skill_damage"), baseline.Of("total_skill_damage") + damage, 1e-12, $"{name} on the secondary weapon total_skill_damage");
            Assert.True(SamePreviews(active.ActiveSkillPreviews, baselineWithWeapon.ActiveSkillPreviews, nonDamageOnly: true), $"{name} changed active skill previews");
        }

        foreach (var (name, talent, host, repair) in new[] { ("Safeguard", "gear_talent_safeguard", "backpack_n_5_11_tactical_backpack", 1.30), ("Perfect Safeguard", "gear_talent_perfect_safeguard", "backpack_momma_badger", 1.60) })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret").WithAccountUpgrade("watch_repair_skills", 50);
            var baseline = Resolve(b);
            var bs = baseline.Calculation.Stats;
            foreach (var context in new[] { Context(), Context(("full_armor", "false")), Context(("full_armor", "yes")) })
            {
                var inactive = Resolve(b, context);
                var what = $"{name} with full_armor={context.GetValueOrDefault("full_armor", "")}";
                Near(inactive.Calculation.Stats.Of("total_skill_repair"), bs.Of("total_skill_repair"), 1e-12, what);
                Assert.False(HasContribution(inactive, "total_skill_repair", "talent", talent), $"inactive {what} contributed without full armor");
            }
            var active = Resolve(b, Context(("full_armor", "true")));
            NoErrors(active, name);
            Near(active.Calculation.Stats.Of("total_skill_repair"), bs.Of("total_skill_repair") + repair, 1e-12, $"{name} total_skill_repair");
            Assert.True(HasContribution(active, "total_skill_repair", "talent", talent), $"{name} contribution is missing from Total Skill Repair provenance");
            Near(active.Calculation.Stats.Of("repair_skills"), bs.Of("repair_skills"), 1e-12, $"{name} repair_skills");
            Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews, nonDamageOnly: true), $"{name} changed active skill previews");
        }
    }

    /// <summary>
    /// G36: Overwatch and Vigilance share the total weapon damage pool beside the additive Watch, and Safeguard
    /// and Vigilance are both offered on the generic 5.11 backpack's talent slot.
    /// </summary>
    [Fact]
    public void G36OverwatchComposesWithVigilanceAndSafeguardSharesBackpackSlot()
    {
        var b = TalentBuild("gear_talent_overwatch", "chest_n_5_11_tactical_chest")
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_vigilance")
            .WithAttributeRoll("primary_weapon", "core_1", new("assault_rifle_damage_weapon_core", .15))
            .WithAccountUpgrade("watch_weapon_damage", 50);
        var baseline = Stats(b, Weapon("primary_weapon"));
        var active = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("overwatch_active", "true"), ("vigilance_active", "true")));
        NoErrors(active, "Overwatch and Vigilance");
        var s = active.Calculation.Stats;
        CloseTo(s.Of("weapon_damage"), baseline.Of("weapon_damage"), 1e-12);
        CloseTo(s.Of("total_weapon_damage"), baseline.Of("total_weapon_damage") + .12 + .25, 1e-12);
        CloseTo(s.Of("equipped_weapon_damage"), s.Of("weapon_base_damage") * (1 + s.Of("weapon_damage") + s.Of("assault_rifle_damage")) * (1 + s.Of("total_weapon_damage")), 1e-8);
        foreach (var talent in new[] { "gear_talent_overwatch", "gear_talent_vigilance" })
        {
            Assert.True(HasContribution(active, "total_weapon_damage", "talent", talent), $"Total Weapon Damage is missing {talent} provenance");
        }

        var backpack = Compiled.Pack.Items.FirstOrDefault(item => item.ID == "backpack_n_5_11_tactical_backpack");
        Assert.True(backpack is not null && backpack.TalentSlots.Length > 0, "generic 5.11 backpack has no talent slot");
        foreach (var talent in new[] { "gear_talent_safeguard", "gear_talent_vigilance" })
        {
            Assert.True(backpack.TalentSlots[0].Choices.Contains(talent), $"generic 5.11 backpack does not offer {talent}");
        }
    }

    /// <summary>G36: Overwatch on the chest and Safeguard on the backpack compose across gear slots.</summary>
    [Fact]
    public void G36OverwatchAndSafeguardComposeAcrossGearSlots()
    {
        foreach (var (name, overwatchTalent, overwatchHost, safeguardTalent, safeguardHost, damage, repair) in new[]
        {
            ("Overwatch + Safeguard", "gear_talent_overwatch", "chest_n_5_11_tactical_chest", "gear_talent_safeguard", "backpack_n_5_11_tactical_backpack", .12, 1.30),
            ("Perfect Overwatch + Perfect Safeguard", "gear_talent_perfect_overwatch", "chest_ferocious_calm", "gear_talent_perfect_safeguard", "backpack_momma_badger", .14, 1.60),
        })
        {
            var b = TalentBuild(overwatchTalent, overwatchHost).WithEquipment("backpack", safeguardHost).WithTalentSelection("backpack", "talent", safeguardTalent);
            var baseline = Stats(b);
            var active = Resolve(b, Context(("overwatch_active", "true"), ("full_armor", "true")));
            NoErrors(active, name);
            Near(active.Calculation.Stats.Of("total_weapon_damage"), baseline.Of("total_weapon_damage") + damage, 1e-12, $"{name} total_weapon_damage");
            Near(active.Calculation.Stats.Of("total_skill_damage"), baseline.Of("total_skill_damage") + damage, 1e-12, $"{name} total_skill_damage");
            Near(active.Calculation.Stats.Of("total_skill_repair"), baseline.Of("total_skill_repair") + repair, 1e-12, $"{name} total_skill_repair");
            foreach (var (stat, talent) in new[] { ("total_weapon_damage", overwatchTalent), ("total_skill_damage", overwatchTalent), ("total_skill_repair", safeguardTalent) })
            {
                Assert.True(HasContribution(active, stat, "talent", talent), $"{name}: {stat} is missing {talent} provenance");
            }
        }
    }

    /// <summary>
    /// G39: Gunslinger and Spark add total weapon damage and Shock and Awe adds total skill damage and repair only
    /// for an explicit true, whichever weapon is selected, and each talent is offered by its expected host.
    /// </summary>
    [Fact]
    public void G39GearTalentActiveGatesAndHostContracts()
    {
        foreach (var (talent, host, gate, stat, amount) in new[]
        {
            ("gear_talent_gunslinger", "chest_n_5_11_tactical_chest", "gunslinger_active", "total_weapon_damage", .23),
            ("gear_talent_perfect_gunslinger", "chest_robin", "gunslinger_active", "total_weapon_damage", .28),
            ("gear_talent_spark", "chest_n_5_11_tactical_chest", "spark_active", "total_weapon_damage", .15),
            ("gear_talent_perfect_spark", "chest_door_kicker_s_knock", "spark_active", "total_weapon_damage", .18),
            ("gear_talent_shock_and_awe", "backpack_n_5_11_tactical_backpack", "shock_and_awe_active", "total_skill_damage", .20),
            ("gear_talent_perfect_shock_and_awe", "backpack_strategic_alignment", "shock_and_awe_active", "total_skill_damage", .20),
        })
        {
            Assert.True(Offers(host, talent), $"{talent} is not offered by expected host {host}");
            var b = TalentBuild(talent, host).WithEquipment("secondary_weapon", "secondary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            foreach (var weaponSlot in new[] { "primary_weapon", "secondary_weapon" })
            {
                var what = $"{talent}/{weaponSlot}";
                var baseline = Resolve(b, Weapon(weaponSlot));
                var bs = baseline.Calculation.Stats;
                foreach (var inactiveGate in new[] { "", "false", "invalid" })
                {
                    var inactiveContext = inactiveGate.Length > 0 ? Context(("weapon_slot", weaponSlot), (gate, inactiveGate)) : Weapon(weaponSlot);
                    var inactive = Resolve(b, inactiveContext);
                    Near(inactive.Calculation.Stats.Of(stat), bs.Of(stat), 1e-12, $"{what} with {gate}=\"{inactiveGate}\"");
                    Assert.False(HasContribution(inactive, stat, "talent", talent), $"{what} contributed with {gate}=\"{inactiveGate}\"");
                }
                Near(Stats(b, Context(("weapon_slot", weaponSlot), ("in_cover", "true"))).Of(stat), bs.Of(stat), 1e-12, $"{what} with an unrelated assumption");
                var active = Resolve(b, Context(("weapon_slot", weaponSlot), (gate, "true")));
                NoErrors(active, what);
                Near(active.Calculation.Stats.Of(stat), bs.Of(stat) + amount, 1e-12, $"{what} {stat}");
                Assert.True(HasContribution(active, stat, "talent", talent), $"{what} provenance missing from {stat}");
                if (talent is "gear_talent_shock_and_awe" or "gear_talent_perfect_shock_and_awe")
                {
                    Near(active.Calculation.Stats.Of("total_skill_repair"), bs.Of("total_skill_repair") + .20, 1e-12, $"{what} total_skill_repair");
                    Assert.True(HasContribution(active, "total_skill_repair", "talent", talent), $"{what} provenance missing from total_skill_repair");
                }
                Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews, nonDamageOnly: true), $"{what} changed active skill previews");
            }
        }

        foreach (var (talent, host) in new[]
        {
            ("gear_talent_gunslinger", "chest_n_5_11_tactical_chest"),
            ("gear_talent_perfect_gunslinger", "chest_robin"),
            ("gear_talent_spark", "chest_n_5_11_tactical_chest"),
            ("gear_talent_perfect_spark", "chest_door_kicker_s_knock"),
            ("gear_talent_shock_and_awe", "backpack_n_5_11_tactical_backpack"),
            ("gear_talent_perfect_shock_and_awe", "backpack_strategic_alignment"),
            ("gear_talent_concussion", "backpack_n_5_11_tactical_backpack"),
            ("gear_talent_perfect_concussion", "backpack_melon_baller"),
            ("gear_talent_focus", "chest_n_5_11_tactical_chest"),
            ("gear_talent_perfect_focus", "chest_pristine_example"),
        })
        {
            Assert.True(Offers(host, talent), $"{talent} is not offered by expected host {host}");
        }
    }

    /// <summary>G39: Focus adds total weapon damage per stack up to ten on either weapon; invalid stacks are diagnosed and ignored.</summary>
    [Fact]
    public void G39FocusStackBoundsAndInvalidDefaults()
    {
        foreach (var (talent, host, perStack) in new[] { ("gear_talent_focus", "chest_n_5_11_tactical_chest", .05), ("gear_talent_perfect_focus", "chest_pristine_example", .06) })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Stats(b, Weapon("primary_weapon"));
            foreach (var (value, stacks) in new[] { ("0", 0.0), ("4", 4), ("10", 10) })
            {
                foreach (var weaponSlot in new[] { "primary_weapon", "secondary_weapon" })
                {
                    var what = $"{talent} at {value} stacks on {weaponSlot}";
                    var result = Resolve(b, Context(("weapon_slot", weaponSlot), ("focus_stacks", value)));
                    NoErrors(result, what);
                    Near(result.Calculation.Stats.Of("total_weapon_damage"), baseline.Of("total_weapon_damage") + perStack * stacks, 1e-12, what);
                }
            }
            foreach (var bad in new[] { "-1", "2.5", "11", "15", "99", "NaN" })
            {
                var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("focus_stacks", bad)));
                Near(result.Calculation.Stats.Of("total_weapon_damage"), baseline.Of("total_weapon_damage"), 1e-12, $"{talent} with invalid \"{bad}\"");
                Assert.True(HasDiagnostic(result, "context-value-invalid", "focus_stacks"), $"{talent}: invalid focus_stacks=\"{bad}\" did not report context-value-invalid");
            }
            Assert.True(HasContribution(Resolve(b, Context(("weapon_slot", "primary_weapon"), ("focus_stacks", "10"))), "total_weapon_damage", "talent", talent), $"{talent} contribution provenance missing");
        }
    }

    /// <summary>
    /// G39: Concussion's headshot and kill buffs are independent explicit assumptions that can overlap, and aren't
    /// inferred from target states.
    /// </summary>
    [Fact]
    public void G39ConcussionBuffsAreIndependentAndCanOverlap()
    {
        foreach (var (talent, host, headshot, kill) in new[] { ("gear_talent_concussion", "backpack_n_5_11_tactical_backpack", .10, .15), ("gear_talent_perfect_concussion", "backpack_melon_baller", .20, .15) })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret").WithEquipment("secondary_weapon", "secondary_weapon_acr");
            foreach (var (context, want) in new (ImmutableDictionary<string, string> Context, double Want)[]
            {
                (Context(), 0),
                (Context(("concussion_headshot_active", "true")), headshot),
                (Context(("concussion_kill_active", "true")), kill),
                (Context(("concussion_headshot_active", "true"), ("concussion_kill_active", "true")), headshot + kill),
                (Context(("target_pulsed", "true"), ("target_bleeding", "true")), 0),
            })
            {
                foreach (var weaponSlot in new[] { "primary_weapon", "secondary_weapon" })
                {
                    var baseline = Stats(b, Weapon(weaponSlot));
                    var result = Stats(b, context.SetItem("weapon_slot", weaponSlot));
                    Near(result.Of("total_weapon_damage"), baseline.Of("total_weapon_damage") + want, 1e-12,
                        $"{talent} on {weaponSlot} with {string.Join(",", context.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))}");
                }
            }
        }
    }

    /// <summary>
    /// G39: Gunslinger and Vigilance share the total weapon damage pool while the gear core and Watch stay additive,
    /// Flatline's pulsed-target multiplier stays separate, and none of it changes the non-damage skill previews.
    /// </summary>
    [Fact]
    public void G39ComposesGearPoolsAndDamagePreviewWithoutChangingSkillPreview()
    {
        var b = TalentBuild("gear_talent_gunslinger", "chest_n_5_11_tactical_chest")
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_vigilance")
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("secondary_weapon", "secondary_weapon_acr")
            .WithAccountUpgrade("watch_weapon_damage", 50)
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_flatline")
            .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("gunslinger_active", "true"), ("vigilance_active", "true"), ("target_pulsed", "true")));
        NoErrors(result, "Gunslinger, Vigilance and Flatline");
        var s = result.Calculation.Stats;
        // Gear core 15% + Watch 10% remain additive; Gunslinger and Vigilance share the TWD pool.
        CloseTo(s.Of("weapon_damage"), .25, 1e-12);
        CloseTo(s.Of("total_weapon_damage"), .48, 1e-12);
        CloseTo(s.Of("weapon_damage_multiplier"), 1.15, 1e-12);
        CloseTo(s.Of("equipped_weapon_damage"), s.Of("weapon_base_damage") * 1.25 * 1.15 * 1.48, 1e-8);
        foreach (var id in new[] { "gear_talent_gunslinger", "gear_talent_vigilance" })
        {
            Assert.True(HasContribution(result, "total_weapon_damage", "talent", id), $"total weapon damage missing {id} provenance");
        }
        Assert.True(HasContribution(result, "weapon_damage", "account-upgrade", "watch_weapon_damage"), "additive Watch Weapon Damage provenance missing");
        Assert.True(SamePreviews(result.ActiveSkillPreviews, Resolve(b, Weapon("primary_weapon")).ActiveSkillPreviews, nonDamageOnly: true), "gear weapon damage buffs changed skill previews");
    }

    private static void AssertG35Unaffected(ResolvedBuild got, ResolvedBuild baseline, string what)
    {
        foreach (var stat in G35UnaffectedStats)
        {
            Near(got.Calculation.Stats.Of(stat), baseline.Calculation.Stats.Of(stat), 1e-12, $"{what} {stat}");
        }
        Assert.True(SamePreviews(got.ActiveSkillPreviews, baseline.ActiveSkillPreviews, nonDamageOnly: true), $"{what}: G35 talent changed active skill preview outputs");
    }

    /// <summary>
    /// Go's division2TalentBuild: an ACR and a 15% weapon-damage mask core, with the talent selected on the host
    /// item that offers it.
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

    /// <summary>Go's findTalentHost: the item offers the talent on one of its talent slots.</summary>
    private static bool Offers(string itemId, string talent) =>
        Compiled.Pack.Items.Any(item => item.ID == itemId && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));

    private static string Format(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    // Go compared skill previews with reflect.DeepEqual, optionally through nonDamagePreviews (which drops the
    // damage channels and the assumed_ rates); this compares the same values.
    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b, bool nonDamageOnly = false)
    {
        ImmutableArray<SkillPreviewOutput> Kept(SkillPreview preview) => nonDamageOnly ? [.. preview.Outputs.Where(output => !IsDamageChannel(output.Id) && !output.Id.StartsWith("assumed_", StringComparison.Ordinal))] : preview.Outputs;
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && Kept(pair.Value).Length == Kept(other).Length && Kept(pair.Value).Zip(Kept(other)).All(outputs => SameOutput(outputs.First, outputs.Second)));
    }

    /// <summary>Go's isDamagePreviewChannel.</summary>
    private static bool IsDamageChannel(string id) => id is "damage" or "average_damage" or "cloud_damage" or "burn_damage" or "bleed_damage" or "explosion_damage";

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula && a.When == b.When
            && a.Value == b.Value && a.Kind == b.Kind && a.Inputs.Count == b.Inputs.Count && a.Inputs.All(pair => b.Inputs.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
