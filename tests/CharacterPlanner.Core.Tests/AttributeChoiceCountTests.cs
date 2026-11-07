using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

/// <summary>
/// A slot choice's own count stat (Slotted, R38): a valid configured roll in that slot adds 1, so the count belongs to the
/// items using the slot template, unlike an attribute's count stat, which counts every item.
/// </summary>
public sealed class AttributeChoiceCountTests
{
    private static readonly ImmutableDictionary<string, string> Context =
        ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");

    // The mask's core choice counts into mask_rolls; the rifle's core choice declares nothing.
    private static GamePack Counted => TestPacks.Tiny with
    {
        Stats = [.. TestPacks.Tiny.Stats, new() { ID = "mask_rolls", Name = "Mask rolls", Format = StatFormats.Integer, Formula = "0" }],
        Items = [.. TestPacks.Tiny.Items.Select(item => item.ID == "mask_a" ? item with
        { AttributeSlots = [item.AttributeSlots[0] with { Choices = [item.AttributeSlots[0].Choices[0] with { CountStat = "mask_rolls" }] }] } : item)]
    };

    private static double Rolls(BuildSnapshot build)
    {
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Validate(Counted).Compiled);
        return BuildResolver.Resolve(pack, build, Context).Calculation.Stats["mask_rolls"];
    }

    private static BuildSnapshot Build => BuildSnapshot.Create(Counted).WithEquipment("mask", "mask_a").WithEquipment("primary_weapon", "rifle_a")
        .WithAttributeRoll("primary_weapon", "core", new("weapon_damage_core", 0.1));

    [Fact]
    public void AConfiguredRollInACountedChoiceAddsOne()
    {
        Assert.Equal(1, Rolls(Build.WithAttributeRoll("mask", "core", new("armor_core", 100000))));
        // The rifle's roll, in a choice that declares no count stat, adds nothing.
        Assert.Equal(0, Rolls(Build));
    }

    [Fact]
    public void AnInvalidRollDoesNotCount()
    {
        Assert.Equal(0, Rolls(Build.WithAttributeRoll("mask", "core", new("armor_core", 999999))));
    }

    [Fact]
    public void ChoiceCountStatsValidateAndRoundTrip()
    {
        var result = PackValidator.Load(PackWriter.Write(Counted));
        Assert.Empty(result.Diagnostics);
        Assert.Equal("mask_rolls", result.Compiled!.Pack.Items.Single(item => item.ID == "mask_a").AttributeSlots[0].Choices[0].CountStat);
        Assert.DoesNotContain("countStat", System.Text.Encoding.UTF8.GetString(PackWriter.Write(TestPacks.Tiny)[PackFiles.Rules].Span), StringComparison.Ordinal);
        var unknown = Counted with { Stats = TestPacks.Tiny.Stats };
        Assert.Contains(PackValidator.Validate(unknown).Diagnostics,
            diagnostic => diagnostic.Code == "unknown-stat" && diagnostic.Path == "items.mask_a.attributeSlots.core.choices[0].countStat");
    }
}
