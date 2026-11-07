using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Configured roll ranges (F10) and Prototype gear with augments and activity normalization (F12), ported from Go's
/// gear_mod_minima_g349_test.go, prototype_g390_test.go and augments_g393_test.go. G349 read its cases from
/// tests/fixtures/gear-mod-minima.json, which this repository doesn't have; its five rows are copied here verbatim.
/// </summary>
public sealed class PrototypeTests
{
    // tests/fixtures/gear-mod-minima.json, verbatim.
    private static readonly (string Id, double Min, double Max)[] GearModMinima =
    [
        ("critical_hit_chance_gear_mod", 0.012, 0.06),
        ("critical_hit_damage_gear_mod", 0.06, 0.12),
        ("headshot_damage_gear_mod", 0.013, 0.1),
        ("blind_deaf_resistance_gear_mod", 0.05, 0.1),
        ("skill_haste_gear_mod", 0.06, 0.12),
    ];

    private static string[] Messages(ResolvedBuild resolved, string code) =>
        [.. resolved.Diagnostics.Where(diagnostic => diagnostic.Code == code).Select(diagnostic => diagnostic.Message)];

    private static string Describe(ResolvedBuild resolved) => string.Join("; ", resolved.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    /// <summary>G349: a gear mod accepts its minimum and maximum rolls only, and a rejected roll is diagnosed and kept as stored.</summary>
    [Fact]
    public void G349GearModMinimaSharedCases()
    {
        Assert.Equal(5, GearModMinima.Length);
        foreach (var (id, min, max) in GearModMinima)
        {
            var b = Build().WithEquipment("mask", "mask_n_5_11_tactical_mask").WithModSelection("mask", "mod_1", id);
            var attribute = Compiled.Pack.Attributes.FirstOrDefault(attribute => attribute.ID == id + "_roll");
            Assert.True(attribute is not null, $"missing roll attribute {id}_roll");
            Assert.True(attribute.Min == min && attribute.Max == max, $"{id}: wrong roll bounds [{attribute.Min}, {attribute.Max}]");
            var stat = attribute.Stat;
            var baseline = Stats(b).Of(stat);
            foreach (var value in new[] { min, max, 0, min - .001 })
            {
                var rolled = b.WithModRoll("mask", "mod_1", value);
                var result = Resolve(rolled);
                var valid = value == min || value == max;
                var want = valid ? baseline + value : baseline;
                Assert.True(Math.Abs(result.Calculation.Stats.Of(stat) - want) <= 1e-12, $"{id} roll {value}: {stat} = {result.Calculation.Stats.Of(stat)}, want {want}");
                Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "mod-roll-invalid") != valid, $"{id} roll {value}: diagnostics {Describe(result)}");
                Assert.True(rolled.ModRolls["mask"]["mod_1"] == value, $"{id} roll {value}: stored roll changed");
            }
        }
    }

    /// <summary>
    /// G390: a Prototype item's attributes roll from the previous maximum to the Prototype maximum, and a Prototype
    /// Skill Tier core counts 1.5 tiers.
    /// </summary>
    [Fact]
    public void G390PrototypeRollBounds()
    {
        var chest = Build().WithEquipment("chest", "chest_n_5_11_tactical_chest");
        BuildSnapshot WithCore(BuildSnapshot b, string attribute, double value) => b.WithAttributeRoll("chest", "core_1", new(attribute, value));
        BuildSnapshot Prototype(BuildSnapshot b) => b.WithPrototype("chest", new("chest_n_5_11_tactical_chest", "", 0));

        var normal = Resolve(WithCore(chest, "weapon_damage_gear_core", 0.2));
        Assert.True(Messages(normal, "attribute-roll-invalid").Length == 1, "a 20% weapon damage core is outside a normal item's range");
        var upgraded = Resolve(Prototype(WithCore(chest, "weapon_damage_gear_core", 0.2)));
        Assert.True(Messages(upgraded, "attribute-roll-invalid").Length == 0 && Messages(upgraded, "prototype-invalid").Length == 0, $"a Prototype item accepts 20%: {Describe(upgraded)}");
        CloseTo(upgraded.Calculation.Stats.Of("prototype_items"), 1, 1e-12);
        Assert.True(Messages(Resolve(Prototype(WithCore(chest, "weapon_damage_gear_core", 0.1))), "attribute-roll-invalid").Length == 1, "a Prototype roll below the previous maximum is invalid");
        Assert.True(Messages(Resolve(Prototype(WithCore(chest, "weapon_damage_gear_core", 0.23))), "attribute-roll-invalid").Length == 1, "a Prototype roll above 22.5% is invalid");

        var tier = Resolve(Prototype(WithCore(chest, "skill_tier_gear_core", 1.5)));
        Assert.True(Messages(tier, "attribute-roll-invalid").Length == 0, $"a Prototype Skill Tier core is 1.5: {Describe(tier)}");
        var plain = Resolve(WithCore(chest, "skill_tier_gear_core", 1));
        CloseTo(tier.Calculation.Stats.Of("skill_tier") - plain.Calculation.Stats.Of("skill_tier"), 0.5, 1e-12);
        Assert.True(Messages(Resolve(Prototype(WithCore(chest, "skill_tier_gear_core", 1))), "attribute-roll-invalid").Length == 1, "a Prototype Skill Tier core can't stay at 1");
    }

    /// <summary>G390: augments add their level's value; invalid selections are diagnosed and leave the item at its normal quality.</summary>
    [Fact]
    public void G390AugmentsAndInvalidSelections()
    {
        ResolvedBuild ResolvePrimary(BuildSnapshot b) => Resolve(b, Weapon("primary_weapon"));
        var b = Build().WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("secondary_weapon", "secondary_weapon_acr")
            .WithPrototype("chest", new("chest_n_5_11_tactical_chest", "augment_echo", 10))
            .WithPrototype("primary_weapon", new("primary_weapon_acr", "augment_echo", 1))
            .WithPrototype("secondary_weapon", new("secondary_weapon_acr", "augment_echo", 10));
        var resolved = ResolvePrimary(b);
        Assert.True(Messages(resolved, "prototype-invalid").Length == 0, $"valid selections were diagnosed: {Describe(resolved)}");
        // Chest level 10 (3.0%) and the active primary at level 1 (1.2%); the inactive secondary doesn't count.
        CloseTo(resolved.Calculation.Stats.Of("augment_echo"), 0.03 + 0.012, 1e-12);
        CloseTo(resolved.Calculation.Stats.Of("prototype_items"), 2, 1e-12);
        var sources = resolved.Calculation.Breakdowns["augment_echo"].Contributions.Count(contribution => contribution.Source.Kind == "prototype");
        Assert.True(sources == 2, $"augment_echo contributions from Prototype items = {sources}, want 2");

        foreach (var (name, selection) in new (string, PrototypeSelection)[]
        {
            ("level above ten", new("chest_n_5_11_tactical_chest", "augment_echo", 11)),
            ("level zero", new("chest_n_5_11_tactical_chest", "augment_echo", 0)),
            ("unknown augment", new("chest_n_5_11_tactical_chest", "augment_ghost", 1)),
            ("stale item", new("chest_other", "augment_echo", 1)),
        })
        {
            var bad = ResolvePrimary(b.WithPrototype("chest", selection));
            var messages = Messages(bad, "prototype-invalid");
            Assert.True(messages.Length == 1, $"{name}: diagnostics {string.Join("; ", messages)}");
            CloseTo(bad.Calculation.Stats.Of("augment_echo"), 0.012, 1e-12);
        }

        var exotic = Build().WithEquipment("mask", "mask_vile").WithPrototype("mask", new("mask_vile", "augment_echo", 1));
        var exoticMessages = Messages(ResolvePrimary(exotic), "prototype-invalid");
        Assert.True(exoticMessages.Length == 1 && exoticMessages[0].Contains("can't be Prototype", StringComparison.Ordinal), $"an exotic can't be Prototype: {string.Join("; ", exoticMessages)}");
        var vile = Compiled.Pack.Items.Single(item => item.ID == "mask_vile");
        Assert.False(PrototypeRules.IsPrototype(Compiled.Pack, exotic, "mask", vile), "an exotic keeps its normal roll ranges");
    }

    private static double PreviewValue(ResolvedBuild resolved, string slot, string output)
    {
        var found = resolved.ActiveSkillPreviews.GetValueOrDefault(slot)?.Outputs.FirstOrDefault(candidate => candidate.Id == output);
        Assert.True(found is not null, $"missing preview output {slot}/{output}");
        return found.Value;
    }

    /// <summary>
    /// G393: modeled augments change their target stats, and a normalized activity disables augments and counts
    /// Prototype rolls at their minimum.
    /// </summary>
    [Fact]
    public void G393AugmentEffectsAndNormalization()
    {
        var baseBuild = BuildSnapshot.Create(Compiled.Pack, "G393")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithAttributeRoll("chest", "core_1", new("weapon_damage_gear_core", 0.2))
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithActiveSkillSelection("skill_slot_1", "skill_shock_trap");
        BuildSnapshot WithAugments(string chest, string weapon, long level) => baseBuild
            .WithPrototype("chest", new("chest_n_5_11_tactical_chest", chest, level))
            .WithPrototype("primary_weapon", new("primary_weapon_acr", weapon, level));
        var open = Weapon("primary_weapon");
        var plain = Resolve(WithAugments("", "", 0), open);
        var p = plain.Calculation.Stats;

        var entropy = Resolve(WithAugments("augment_entropy", "", 10), open).Calculation.Stats;
        CloseTo(entropy.Of("character_health") - p.Of("character_health"), 0.14 * entropy.Of("character_armor"), 1e-6);

        var echo = Resolve(WithAugments("", "augment_echo", 10), open).Calculation.Stats;
        CloseTo(echo.Of("equipped_weapon_practical_shot_damage"), p.Of("equipped_weapon_practical_shot_damage") * 1.03, 1e-6);
        Assert.True(echo.Of("equipped_weapon_average_bodyshot_damage") == p.Of("equipped_weapon_average_bodyshot_damage"), "Echo changes only the practical expected-value chain");

        var s = Resolve(WithAugments("", "augment_paradox", 10), open).Calculation.Stats;
        var factor = 1 / (1 - (2 * 0.06));
        var want = s.Of("equipped_weapon_practical_shot_damage") * s.Of("equipped_weapon_magazine_size") * factor
            / ((s.Of("equipped_weapon_firing_duration") * factor) + s.Of("equipped_weapon_reload_time"));
        CloseTo(s.Of("equipped_weapon_practical_reload_cycle_dps"), want, 1e-6);
        Assert.True(s.Of("equipped_weapon_practical_reload_cycle_dps") > p.Of("equipped_weapon_practical_reload_cycle_dps"), "Paradox refills raise practical reload-cycle DPS");

        var trapper = Resolve(WithAugments("augment_trapper", "", 10), open);
        CloseTo(trapper.Calculation.Stats.Of("status_effect_duration"), 0.14, 1e-12);
        double Duration(ResolvedBuild r) => PreviewValue(r, "skill_slot_1", "shock_duration_seconds");
        CloseTo(Duration(trapper) / Duration(plain), (1 + p.Of("effective_status_effects") + 0.14) / (1 + p.Of("effective_status_effects")), 1e-9);

        var normalized = Resolve(WithAugments("augment_entropy", "augment_echo", 10), Context(("weapon_slot", "primary_weapon"), ("prototype_activity", "1"))).Calculation.Stats;
        Assert.True(normalized.Of("augment_entropy") == 0 && normalized.Of("augment_echo") == 0, "a normalized activity disables augments");
        CloseTo(normalized.Of("character_health"), p.Of("character_health"), 1e-6);
        CloseTo(p.Of("weapon_damage") - normalized.Of("weapon_damage"), 0.05, 1e-12);
    }
}
