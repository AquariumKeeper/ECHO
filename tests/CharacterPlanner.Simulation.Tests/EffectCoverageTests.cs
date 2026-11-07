namespace CharacterPlanner.Simulation.Tests;

public sealed partial class DummyWeaponSimulatorTests
{
    [Fact]
    public void CoverageInputsAreLabeledAsSetupRatherThanEarnedUptime()
    {
        var pack = Tiny();
        var fixedFight = Run(pack, Build(pack), Context);
        Assert.Contains("Fixed simulation", fixedFight.Coverage.InputBasis, StringComparison.Ordinal);
        var driven = Run(pack, Build(pack), Context, new() { DriveSimpleBuffs = true, Mode = SimulationMode.Random, Iterations = 1 });
        Assert.Contains("Initial setup", driven.Coverage.InputBasis, StringComparison.Ordinal);
        Assert.Equal("primary_weapon", driven.Coverage.Inputs["weapon_slot"]);
        Assert.Equal(fixedFight.WeaponDps, driven.WeaponDps);
    }
}
