using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

/// <summary>The resolver's lookups are built once per pack instance (L4), and never shared with a changed copy.</summary>
public sealed class PackIndexTests
{
    [Fact]
    public void EachPackInstanceHasItsOwnIndex()
    {
        var pack = TestPacks.Tiny;
        Assert.Same(PackIndex.Of(pack), PackIndex.Of(pack));

        // A copy made with `with` is equal as a record but a different instance, so it gets its own index.
        var renamed = pack with { Items = [.. pack.Items.Select(item => item.ID == "mask_a" ? item with { Name = "Renamed" } : item)] };
        Assert.NotSame(PackIndex.Of(pack), PackIndex.Of(renamed));
        Assert.Equal("Mask A", PackIndex.Of(pack).Items["mask_a"].Name);
        Assert.Equal("Renamed", PackIndex.Of(renamed).Items["mask_a"].Name);
    }

    /// <summary>Each lookup holds exactly its own collection's ids.</summary>
    [Fact]
    public void EachLookupIndexesItsOwnCollection()
    {
        var pack = TestPacks.Tiny;
        var index = PackIndex.Of(pack);
        static string[] Ids(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];
        Assert.Equal(Ids(pack.Items.Select(item => item.ID)), Ids(index.Items.Keys));
        Assert.Equal(Ids(pack.Attributes.Select(attribute => attribute.ID)), Ids(index.Attributes.Keys));
        Assert.Equal(Ids(pack.Mods.Select(mod => mod.ID)), Ids(index.Mods.Keys));
        Assert.Equal(Ids(pack.Talents.Select(talent => talent.ID)), Ids(index.Talents.Keys));
        Assert.Equal(Ids(pack.ActiveSkills.Select(skill => skill.ID)), Ids(index.ActiveSkills.Keys));
        Assert.Equal(Ids(pack.AccountUpgrades.Select(upgrade => upgrade.ID)), Ids(index.AccountUpgrades.Keys));
        Assert.Equal(Ids(pack.Slots.Select(slot => slot.ID)), Ids(index.SlotIds));
        Assert.Equal(Ids(pack.ActiveSkillSlots.Select(slot => slot.ID)), Ids(index.ActiveSkillSlotIds));
        Assert.All(new[] { index.Items.Count, index.Attributes.Count, index.Talents.Count, index.SlotIds.Count, index.ActiveSkillSlotIds.Count }, count => Assert.NotEqual(0, count));
    }

    /// <summary>A duplicate id fails only the lookup that reads that collection, as each stage's own dictionary did.</summary>
    [Fact]
    public void ADuplicateFailsOnlyItsOwnLookup()
    {
        var pack = TestPacks.Tiny with { ActiveSkills = [.. TestPacks.Tiny.ActiveSkills, TestPacks.Tiny.ActiveSkills[0]] };
        Assert.NotEmpty(PackIndex.Of(pack).Items);
        Assert.Throws<ArgumentException>(() => PackIndex.Of(pack).ActiveSkills);
    }
}
