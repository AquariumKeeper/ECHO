using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

public sealed class EndToEndTests
{
    private static ImmutableDictionary<string, string> Context(string weaponSlot) =>
        ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, weaponSlot);

    [Fact]
    public void WrittenPackValidatesAndResolvesTheActiveWeapon()
    {
        var validation = PackValidator.Load(TestPacks.Files());
        Assert.Empty(validation.Diagnostics);
        var compiled = Assert.IsType<CompiledPackExpressions>(validation.Compiled);
        var build = BuildSnapshot.Create(compiled.Pack)
            .WithEquipment("mask", "mask_a")
            .WithEquipment("primary_weapon", "rifle_a")
            .WithEquipment("secondary_weapon", "rifle_b")
            .WithAttributeRoll("primary_weapon", "core", new("weapon_damage_core", 0.1))
            .WithTalentSelection("primary_weapon", "talent", "talent_killer")
            .WithTalentSelection("secondary_weapon", "talent", "talent_holster");

        // Primary active: its base stats, modifiers, attribute and talent apply; the
        // holstered secondary contributes only its whileHolstered talent.
        var primary = BuildResolver.Resolve(compiled, build, Context("primary_weapon"));
        Assert.DoesNotContain(primary.Diagnostics, diagnostic => diagnostic.Severity == "error");
        var stats = primary.Calculation.Stats;
        Assert.Equal(100, stats["weapon_base_damage"]);
        Assert.Equal(600, stats["weapon_rpm"]);
        Assert.Equal(0.45, stats["weapon_damage"], 9);
        Assert.Equal(145, stats["total_damage"], 9);
        Assert.Equal(500, stats["armor"]);
        Assert.Contains(primary.SelectedTags, tag => tag == "weapon_type_rifle");
        // Weapon base stats are reported as the active item's overrides, as pack v1 authored them.
        var baseDamage = Assert.Single(primary.Calculation.Breakdowns["weapon_base_damage"].Contributions);
        Assert.Equal(("item", "rifle_a", ModifierOps.Override), (baseDamage.Source.Kind, baseDamage.Source.Id, baseDamage.Op));
        var dps = Assert.Single(primary.BuildOutputs);
        Assert.Equal(145 * 600 / 60.0, dps.Value, 9);

        // Secondary active: rifle_a is holstered, so neither its talent nor its roll apply,
        // and rifle_b's holster-only talent is now inactive.
        var secondary = BuildResolver.Resolve(compiled, build, Context("secondary_weapon"));
        Assert.Equal(200, secondary.Calculation.Stats["weapon_base_damage"]);
        Assert.Equal(300, secondary.Calculation.Stats["weapon_rpm"]);
        Assert.Equal(0, secondary.Calculation.Stats["weapon_damage"], 9);
        Assert.Equal(200, secondary.Calculation.Stats["total_damage"], 9);

        // A declared selector gates the mask's own modifier.
        var overwatch = BuildResolver.Resolve(compiled, build, Context("primary_weapon").Add("overwatch_active", "true"));
        Assert.Equal(1500, overwatch.Calculation.Stats["armor"]);
    }

    // With no active weapon every weapon counts as holstered, so each weapon's whileHolstered
    // talents apply and nothing else of the weapons does. This is v1's behavior and is kept.
    [Theory]
    [InlineData(null, 0.1)]
    [InlineData("", 0.1)]
    [InlineData("primary_weapon", 0.15)]
    [InlineData("secondary_weapon", 0.05)]
    public void HolsteredTalentsApplyToEveryWeaponThatIsNotActive(string? weaponSlot, double weaponDamage)
    {
        var compiled = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(TestPacks.Files()).Compiled);
        var build = BuildSnapshot.Create(compiled.Pack)
            .WithEquipment("primary_weapon", "rifle_a")
            .WithEquipment("secondary_weapon", "rifle_b")
            .WithTalentSelection("primary_weapon", "talent", "talent_holster")
            .WithTalentSelection("secondary_weapon", "talent", "talent_holster");
        var context = weaponSlot is null ? ImmutableDictionary<string, string>.Empty : Context(weaponSlot);
        var result = BuildResolver.Resolve(compiled, build, context);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == "error");
        // Primary active: rifle_a's own 0.1 plus the holstered secondary's 0.05; its own holster
        // talent is off. Secondary active: only the holstered primary's 0.05. Neither: both 0.05s.
        Assert.Equal(weaponDamage, result.Calculation.Stats["weapon_damage"], 9);
        var holsters = result.Calculation.Breakdowns["weapon_damage"].Contributions.Count(contribution => contribution.Source.Id == "talent_holster");
        Assert.Equal(weaponSlot is null or "" ? 2 : 1, holsters);
    }
}
