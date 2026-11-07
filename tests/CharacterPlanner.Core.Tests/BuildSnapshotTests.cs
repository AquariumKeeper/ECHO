using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

public sealed class BuildSnapshotTests
{
    private static readonly GamePack Pack = new()
    {
        ID = "test",
        Version = "v1",
        Stats = [new() { ID = "hp", Formula = "0" }],
        Slots = [new() { ID = "head" }, new() { ID = "chest" }]
    };

    [Fact]
    public void NewBuildContainsEveryEmptySlot()
    {
        var build = BuildSnapshot.Create(Pack);
        Assert.Equal("test", build.PackId);
        Assert.Equal("v1", build.PackVersion);
        Assert.Equal("New Build", build.Meta.Name);
        Assert.Equal("", build.Meta.Notes);
        Assert.Equal(2, build.Equipment.Count);
        Assert.All(build.Equipment.Values, value => Assert.Equal("", value));
        Assert.Empty(build.Party);
        Assert.Equal(" Named ", BuildSnapshot.Create(Pack, " Named ").Meta.Name);
    }

    private static BuildSnapshot Configured(string slot, string item) => BuildSnapshot.Create(Pack)
        .WithEquipment(slot, item)
        .WithAttributeRoll(slot, "core", new("armor", 100))
        .WithModSelection(slot, "socket", "mod")
        .WithModRoll(slot, "socket", 7)
        .WithTalentSelection(slot, "talent", "talent_id")
        .WithEquipmentUpgrade(slot, item, 3)
        .WithPrototype(slot, new(item, "echo", 2));

    [Theory]
    [InlineData("replacement")]
    [InlineData("")]
    public void SwappingOrClearingEquipmentDropsOnlyThatInstancesConfiguration(string replacement)
    {
        var original = Configured("head", "helm").WithEquipment("chest", "vest")
            .WithAttributeRoll("chest", "core", new("armor", 90));
        var next = original.WithEquipment("head", replacement);
        Assert.Equal(replacement, next.Equipment["head"]);
        Assert.False(next.AttributeRolls.ContainsKey("head"));
        Assert.False(next.ModSelections.ContainsKey("head"));
        Assert.False(next.ModRolls.ContainsKey("head"));
        Assert.False(next.TalentSelections.ContainsKey("head"));
        Assert.False(next.EquipmentUpgrades.ContainsKey("head"));
        Assert.False(next.Prototypes.ContainsKey("head"));
        Assert.Equal(90, next.AttributeRolls["chest"]["core"].Value);
        Assert.Equal("helm", original.Equipment["head"]);
        Assert.Equal(100, original.AttributeRolls["head"]["core"].Value);
        Assert.Equal(3, original.EquipmentUpgrades["head"].Rank);
    }

    [Fact]
    public void ReselectingSameNonemptyItemPreservesConfigurationAndBulkReplacementClearsRemovedSlots()
    {
        var original = Configured("head", "helm");
        var same = original.WithEquipment("head", "helm");
        Assert.Same(original.AttributeRolls, same.AttributeRolls);
        Assert.Same(original.Prototypes, same.Prototypes);
        var input = new Dictionary<string, string> { ["head"] = "helm" };
        var bulk = original.WithEquipmentAll(input);
        input["head"] = "other";
        Assert.Equal("helm", bulk.Equipment["head"]);
        Assert.Equal(7, bulk.ModRolls["head"]["socket"]);
        Assert.False(bulk.Equipment.ContainsKey("chest"));
        var removed = original.WithEquipmentAll(new Dictionary<string, string>());
        Assert.Empty(removed.AttributeRolls);
        Assert.Empty(removed.ModSelections);
        Assert.Empty(removed.ModRolls);
        Assert.Empty(removed.TalentSelections);
        Assert.Empty(removed.EquipmentUpgrades);
        Assert.Empty(removed.Prototypes);
    }

    [Fact]
    public void ModChangesInvalidateOnlyTheirRollAndRemovingLastChoicePrunesTheSlot()
    {
        var original = Configured("head", "helm").WithModRoll("head", "other", 4);
        Assert.Equal(7, original.WithModSelection("head", "socket", "mod").ModRolls["head"]["socket"]);
        var changed = original.WithModSelection("head", "socket", "replacement");
        Assert.False(changed.ModRolls["head"].ContainsKey("socket"));
        Assert.Equal(4, changed.ModRolls["head"]["other"]);
        Assert.False(changed.WithModSelection("head", "socket", "").ModSelections.ContainsKey("head"));
        Assert.False(changed.WithTalentSelection("head", "talent", "").TalentSelections.ContainsKey("head"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteAttributeRollsAreIgnoredButNonfiniteModRollsClear(double invalid)
    {
        var original = Configured("head", "helm");
        Assert.Same(original, original.WithAttributeRoll("head", "core", new("armor", invalid)));
        Assert.False(original.WithModRoll("head", "socket", invalid).ModRolls.ContainsKey("head"));
        Assert.Equal(7, original.ModRolls["head"]["socket"]);
        Assert.False(original.WithAttributeRoll("head", "core", null).AttributeRolls.ContainsKey("head"));
    }

    [Fact]
    public void FiniteInvalidValuesAreRetainedAndZeroRollIsDistinctFromRemoval()
    {
        var build = BuildSnapshot.Create(Pack)
            .WithAttributeRoll("head", "core", new("unknown", -100))
            .WithModRoll("head", "socket", 0).WithAccountUpgrade("unknown", -2);
        Assert.Equal(-100, build.AttributeRolls["head"]["core"].Value);
        Assert.Equal(0, build.ModRolls["head"]["socket"]);
        Assert.Equal(-2, build.AccountUpgrades["unknown"]);
        Assert.Empty(build.WithAccountUpgrade("unknown", 0).AccountUpgrades);
        Assert.Empty(build.WithModRoll("head", "socket", null).ModRolls);
    }

    [Fact]
    public void ActiveSkillChangesClearVariantConfigurationAndSameSkillPreservesIt()
    {
        var original = BuildSnapshot.Create(Pack).WithActiveSkillSelection("one", "turret")
            .WithSkillUpgrade("one", "turret", 5).WithSkillMod("one", "turret", "socket", new("damage", 3));
        var same = original.WithActiveSkillSelection("one", "turret");
        Assert.Same(original.SkillMods, same.SkillMods);
        Assert.Same(original.SkillUpgrades, same.SkillUpgrades);
        foreach (var skill in new[] { "drone", "" })
        {
            var changed = original.WithActiveSkillSelection("one", skill);
            Assert.Empty(changed.SkillMods);
            Assert.Empty(changed.SkillUpgrades);
            if (skill.Length == 0) { Assert.Empty(changed.ActiveSkillSelections); }
        }
        Assert.Equal(5, original.SkillUpgrades["one"].Rank);
        Assert.Empty(original.WithSkillUpgrade("one", "turret", 0).SkillUpgrades);
    }

    [Fact]
    public void SkillModEditsDoNotCarryModsAcrossVariantsAndNullPrunesSelection()
    {
        var original = BuildSnapshot.Create(Pack).WithSkillMod("one", "turret", "a", new("damage", 3));
        var changed = original.WithSkillMod("one", "drone", "b", new("health", 4));
        Assert.Equal("drone", changed.SkillMods["one"].SkillId);
        Assert.Equal("b", Assert.Single(changed.SkillMods["one"].Mods).Key);
        Assert.Empty(changed.WithSkillMod("one", "drone", "b", null).SkillMods);
        Assert.Equal("a", Assert.Single(original.SkillMods["one"].Mods).Key);
    }

    [Fact]
    public void SpecializationNodesAreUniqueSortedAndResetOnChange()
    {
        var original = BuildSnapshot.Create(Pack).WithSpecialization("gunner")
            .WithSpecializationNode("z", true).WithSpecializationNode("a", true).WithSpecializationNode("z", true);
        Assert.Equal(new[] { "a", "z" }, original.SpecializationNodeSelections);
        Assert.Equal(original.SpecializationNodeSelections, original.WithSpecialization("gunner").SpecializationNodeSelections);
        Assert.Empty(original.WithSpecialization("technician").SpecializationNodeSelections);
        Assert.Empty(original.WithSpecialization("").SpecializationNodeSelections);
        Assert.Equal(new[] { "z" }, original.WithSpecializationNode("a", false).SpecializationNodeSelections);
    }

    [Fact]
    public void PartySnapshotsDoNotChangeEarlierBuilds()
    {
        var values = ImmutableDictionary<string, double>.Empty.Add("armor", 100);
        var effect = new PartyEffect("buff", values);
        var members = new List<PartyMember> { new("Ally", "", 1000, [effect]) };
        var original = BuildSnapshot.Create(Pack).WithParty(members);
        members.Clear();
        var changed = original with { Party = [original.Party[0] with { Effects = [effect with { Values = values.SetItem("armor", 200), Active = true }] }] };
        Assert.False(original.Party[0].Effects[0].Active);
        Assert.Equal(100, original.Party[0].Effects[0].Values["armor"]);
        Assert.Equal(200, changed.Party[0].Effects[0].Values["armor"]);
    }
}
