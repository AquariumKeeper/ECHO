using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class UpgradeEffectTests
{
    private static readonly GamePack Pack = LegacyPacks.Load(Path.Combine("upgrade-resolution", "pack.json"), SyntheticV1.Simplify).Pack;
    private static readonly ImmutableDictionary<string, string> Active = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main");

    [Fact]
    public void AccountCapChangesEffectButPreservesSelectedRankAndWorksWithoutEquipment()
    {
        var build = BuildSnapshot.Create(Pack).WithAccountUpgrade("watch", 10);
        var result = BuildResolver.Resolve(Compile(Pack), build);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(120, result.Calculation.Stats["damage"]);
        var contribution = Assert.Single(result.Calculation.Breakdowns["damage"].Contributions);
        Assert.Equal(20, contribution.Value);
        Assert.Equal(10, contribution.Source.Rank);
        Assert.Equal("Watch (rank 10)", contribution.Source.Name);
    }

    [Fact]
    public void ItemGateSuppressesItemUpgradeButKeepsPersistentAccountEffects()
    {
        var build = BuildSnapshot.Create(Pack).WithAccountUpgrade("watch", 10).WithEquipment("main", "weapon").WithEquipmentUpgrade("main", "weapon", 2);
        var result = BuildResolver.Resolve(Compile(Pack), build, Active.SetItem("weapon_slot", "off"));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(120, result.Calculation.Stats["damage"]);
        Assert.Equal("account-upgrade", Assert.Single(result.Calculation.Breakdowns["damage"].Contributions).Source.Kind);
    }

    [Fact]
    public void RankedModifierContextGatesAreAppliedToAccountAndEquipmentEffects()
    {
        var gate = ImmutableDictionary<string, string>.Empty.Add("mode", "on");
        var pack = Pack with
        {
            AccountUpgrades = Pack.AccountUpgrades.Select(upgrade => upgrade with { ModifiersPerRank = upgrade.ModifiersPerRank.Select(modifier => modifier with { RequiresContext = gate }).ToImmutableArray() }).ToImmutableArray(),
            Items = Pack.Items.Select(item => item.Upgrade is { } upgrade ? item with { Upgrade = upgrade with { ModifiersPerRank = upgrade.ModifiersPerRank.Select(modifier => modifier with { RequiresContext = gate }).ToImmutableArray() } } : item).ToImmutableArray()
        };
        var build = BuildSnapshot.Create(pack).WithAccountUpgrade("watch", 10).WithEquipment("main", "weapon").WithEquipmentUpgrade("main", "weapon", 2);
        var compiled = Compile(pack);
        Assert.Equal(101, BuildResolver.Resolve(compiled, build, Active).Calculation.Stats["damage"]);
        Assert.Equal(127, BuildResolver.Resolve(compiled, build, Active.Add("mode", "on")).Calculation.Stats["damage"]);
    }

    [Fact]
    public void ZeroEffectiveRankProducesNoContribution()
    {
        var pack = Pack with { AccountUpgrades = [Pack.AccountUpgrades[0] with { EffectCap = 0 }] };
        var build = BuildSnapshot.Create(pack).WithAccountUpgrade("watch", 10);
        var result = BuildResolver.Resolve(Compile(pack), build);
        Assert.Empty(result.Calculation.Breakdowns["damage"].Contributions);
        Assert.Equal(100, result.Calculation.Stats["damage"]);
        Assert.Equal(10, build.AccountUpgrades["watch"]);
    }

    [Fact]
    public void UpgradeOrderIsOrdinalBeforeAuthoredItemEffects()
    {
        // "main" stops being a weapon slot, so its item applies whichever weapon is active.
        var pack = Pack with
        {
            Slots = Pack.Slots.Reverse().Select(slot => slot.ID == "main" ? slot with { Weapon = false } : slot).ToImmutableArray(),
            Items = Pack.Items.Select(item => item.ID == "shield" ? item with { Upgrade = new() { Name = "Shield plating", MaxRank = 3, ModifiersPerRank = [new() { Stat = "damage", Op = "flat", Value = new(2) }] } } : item).ToImmutableArray(),
            // v1's "bonus" used the removed inc operation.
            AccountUpgrades = Pack.AccountUpgrades.Select(upgrade => upgrade.ID == "bonus" ? upgrade with { ModifiersPerRank = [new() { Stat = "damage", Op = "mult", Value = new(0.1) }] } : upgrade).ToImmutableArray()
        };
        var build = BuildSnapshot.Create(pack).WithEquipment("main", "weapon").WithEquipment("off", "shield")
            .WithEquipmentUpgrade("off", "shield", 1).WithEquipmentUpgrade("main", "weapon", 2)
            .WithAccountUpgrade("watch", 1).WithAccountUpgrade("bonus", 1);
        var result = BuildResolver.Resolve(Compile(pack), build, Active.SetItem("weapon_slot", "off"));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "account-upgrade:bonus", "account-upgrade:watch", "item-upgrade:weapon", "item-upgrade:shield", "item:weapon" },
            result.Calculation.Breakdowns["damage"].Contributions.Select(value => value.Source.Kind + ":" + value.Source.Id));
    }

    [Fact]
    public void InvalidBoundUpgradeIsDiagnosedEvenWhileTheWeaponIsHolstered()
    {
        var build = BuildSnapshot.Create(Pack).WithEquipment("main", "weapon").WithEquipmentUpgrade("main", "weapon", 4);
        var result = BuildResolver.Resolve(Compile(Pack), build, Active.SetItem("weapon_slot", "off"));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(("equipment-upgrade-invalid", "main"), (diagnostic.Code, diagnostic.Subject));
        Assert.Equal(100, result.Calculation.Stats["damage"]);
        Assert.Equal(4, build.EquipmentUpgrades["main"].Rank);
    }

    private static CompiledPackExpressions Compile(GamePack pack) => LegacyPacks.Compile(pack);
}
