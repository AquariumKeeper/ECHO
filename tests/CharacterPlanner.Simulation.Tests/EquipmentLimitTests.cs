using CharacterPlanner.Core.Packs;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class DummyWeaponSimulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquipmentLimitsFailBeforeAnyFightOrProgress(bool targeted)
    {
        var rules = Tiny().Pack;
        var pack = PackExpressionCompiler.Compile(rules with
        {
            Items = [rules.Items[0] with { Tags = ["limited"] }],
            EquipmentLimits = [new() { ID = "no_gun", Name = "No gun", Tag = "limited", Slots = ["primary_weapon"], Maximum = 0 }]
        }).Compiled!;
        var error = Assert.Throws<SimulationException>(() => targeted
            ? DummyWeaponSimulator.RunAgainstTargets(pack, Build(pack), Context, cancellationToken: TestContext.Current.CancellationToken)
            : DummyWeaponSimulator.Run(pack, Build(pack), Context, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(LoadoutResolver.EquipmentLimitExceeded, error.Code);
        Assert.Contains("No gun", error.Message, StringComparison.Ordinal);
    }
}
