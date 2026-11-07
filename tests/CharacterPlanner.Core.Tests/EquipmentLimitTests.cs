using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

public sealed class EquipmentLimitTests
{
    private static readonly EquipmentLimitDefinition Limit = new()
    { ID = "weapon_allowance", Name = "Weapon allowance", Tag = "weapon", Slots = ["primary_weapon", "secondary_weapon"], Maximum = 1 };
    private static GamePack Pack => TestPacks.Tiny with
    { EquipmentLimits = [Limit], Capabilities = [.. TestPacks.Tiny.Capabilities, PackCapabilities.EquipmentLimits] };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void GearAllowanceCountsOnlyDeclaredSlotsAndRetainsEveryItem(int count)
    {
        string[] slots = ["mask", "chest", "backpack", "gloves", "holster", "knees"];
        var pack = Pack with
        {
            Slots = [.. slots.Select(id => new SlotDefinition { ID = id, Name = id }), .. Pack.Slots.Where(slot => slot.Weapon)],
            Items = [.. slots.Select(id => new ItemDefinition { ID = id + "_exotic", Name = id, Slot = id, Tags = ["exotic"] }),
                new() { ID = "gun", Name = "Gun", Slot = "primary_weapon", Tags = ["exotic"] }],
            EquipmentLimits = [Limit with { ID = "exotic_gear", Name = "Exotic gear", Tag = "exotic", Slots = [.. slots] }]
        };
        var equipment = slots.Take(count).ToImmutableDictionary(id => id, id => id + "_exotic").Add("primary_weapon", "gun");
        var build = BuildSnapshot.Create(pack) with { Equipment = equipment };
        var result = LoadoutResolver.Resolve(pack, build);
        Assert.Equal(count + 1, result.Equipped.Length);
        Assert.Same(equipment, build.Equipment);
        Assert.Equal(count > 1 ? slots.Take(count) : [], result.Diagnostics.Select(error => error.Subject));
        Assert.All(result.Diagnostics, error =>
        {
            Assert.True(LoadoutResolver.IsEquipmentLimitError(error));
            Assert.Equal("exotic_gear", error.Target);
            Assert.Contains("Remove", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void IndependentRulesKeepTheirAuthoredOrderAndZeroIsAnEffectiveLimit()
    {
        var pack = Pack with { EquipmentLimits = [Limit, Limit with { ID = "no_masks", Tag = "gear", Slots = ["mask"], Maximum = 0 }] };
        var build = BuildSnapshot.Create(pack) with
        {
            Equipment = ImmutableDictionary<string, string>.Empty
            .Add("mask", "mask_a").Add("primary_weapon", "rifle_a").Add("secondary_weapon", "rifle_b")
        };
        Assert.Equal(["weapon_allowance", "weapon_allowance", "no_masks"], LoadoutResolver.Resolve(pack, build).Diagnostics.Select(error => error.Target));
        Assert.Empty(LoadoutResolver.Resolve(pack with { EquipmentLimits = [] }, build).Diagnostics);
    }

    [Fact]
    public void LimitsRoundTripWithoutChangingPacksThatOmitThem()
    {
        var files = PackWriter.Write(Pack);
        var result = PackValidator.Load(files);
        Assert.Empty(result.Diagnostics);
        var rule = Assert.Single(result.Compiled!.Pack.EquipmentLimits);
        Assert.Equal(Limit.ID, rule.ID);
        Assert.Equal(Limit.Name, rule.Name);
        Assert.Equal(Limit.Tag, rule.Tag);
        Assert.Equal(Limit.Maximum, rule.Maximum);
        Assert.Equal(Limit.Slots, rule.Slots);
        foreach (var (file, bytes) in files) { Assert.True(bytes.Span.SequenceEqual(PackWriter.Write(result.Compiled.Pack)[file].Span)); }
        Assert.DoesNotContain("equipmentLimits", Encoding.UTF8.GetString(PackWriter.Write(TestPacks.Tiny)[PackFiles.Rules].Span), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Exotic")]
    [InlineData("exotic-gear")]
    public void LimitsAcceptAndExactlyMatchExistingFreeFormTags(string tag)
    {
        var pack = Pack with
        {
            Items = [.. Pack.Items.Select(item => item.Slot.EndsWith("weapon", StringComparison.Ordinal)
                ? item with { Tags = [.. item.Tags, tag] } : item)],
            EquipmentLimits = [Limit with { Tag = tag }]
        };
        var loaded = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(loaded.Diagnostics);
        var compiled = Assert.IsType<CompiledPackExpressions>(loaded.Compiled);
        var build = BuildSnapshot.Create(pack).WithEquipment("primary_weapon", "rifle_a").WithEquipment("secondary_weapon", "rifle_b");
        Assert.Equal(2, LoadoutResolver.Resolve(compiled.Pack, build).Diagnostics.Count(LoadoutResolver.IsEquipmentLimitError));
        var differentCase = pack with { EquipmentLimits = [Limit with { Tag = tag.ToUpperInvariant() }] };
        Assert.Contains(PackValidator.Validate(differentCase).Diagnostics, diagnostic => diagnostic.Code == "unknown-tag");
    }

    [Theory]
    [InlineData("tag", "unknown-tag")]
    [InlineData("slots", "unknown-slot")]
    [InlineData("duplicate", "duplicate-slot")]
    [InlineData("empty", "empty-slots")]
    [InlineData("maximum", "invalid-integer")]
    [InlineData("id", "invalid-id")]
    [InlineData("name", "empty-name")]
    public void MalformedRulesAreRejected(string field, string code)
    {
        var rule = field switch
        {
            "tag" => Limit with { Tag = "missing" },
            "slots" => Limit with { Slots = ["missing"] },
            "duplicate" => Limit with { Slots = ["primary_weapon", "primary_weapon"] },
            "empty" => Limit with { Slots = [] },
            "maximum" => Limit with { Maximum = -1 },
            "id" => Limit with { ID = "Bad" },
            _ => Limit with { Name = " " }
        };
        var result = PackValidator.Validate(Pack with { EquipmentLimits = [rule] });
        Assert.Null(result.Compiled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Path.StartsWith("equipmentLimits", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("tag")]
    [InlineData("slots")]
    [InlineData("maximum")]
    public void EveryWireFieldIsRequired(string field)
    {
        var files = TestPacks.Edit(PackFiles.Rules, rules => rules["equipmentLimits"]![0]!.AsObject().Remove(field), Pack);
        var read = PackReader.Read(files);
        Assert.Null(read.Pack);
        Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Path.Contains("equipmentLimits", StringComparison.Ordinal));
    }
}
