using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class DummyWeaponSimulatorTests
{
    private static DummyFightOptions Seconds(double duration) =>
        new() { Duration = duration, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true };

    [Fact]
    public void EachWholeSecondFiresBeforeAShotAtTheSameTime()
    {
        // One round and a two-second reload after a one-second interval: shots at 0 and 3; seconds at 1 to 5.
        var (original, _) = BuffBuild(on: "second");
        var rules = original.Pack with { Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Duration = 0.5 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, Seconds(6));
        Assert.Equal(new double[] { 0, 3 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new double[] { 10, 20 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        // Half a second after each of the five whole seconds before the end.
        Assert.Equal(2.5, result.TimedTriggerUptime["buff"].Seconds, 8);
    }

    [Fact]
    public void SecondStacksBuildUpToTheirCap()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "count", Name = "Count", Min = 0, Max = 3, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('count') * 10" } : s)],
            Talents = [new() { ID = "test_buff", Name = "Seconds" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "count", On = "second", Kind = "stacks", Max = 3 }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, Seconds(6));
        // Stacks at 1, 2 and 3 s reach the cap before the shot at 3 s; later seconds add nothing.
        Assert.Equal(new double[] { 10, 40 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        // At least one stack from 1 s to the end; one, two, then three stacks for the last three seconds.
        Assert.Equal(5, result.TimedTriggerUptime["count"].Seconds, 8);
        Assert.Equal((1 + 2 + 3 * 3) / 6d, result.TimedTriggerUptime["count"].MeanStacks!.Value, 8);
    }

    [Theory]
    [InlineData("gear_talent_stand_your_ground", "sawyer_stacks", true)]
    [InlineData("gear_talent_chemical_agent", "catalysis_stacks", false)]
    public void CommittedSecondTalentsRunInsteadOfBeingRefused(string talent, string key, bool earns)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var item = pack.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = item.TalentSlots.First(slot => slot.Choices.Contains(talent));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, talent);
        var options = new DummyFightOptions { Duration = 20, Mode = SimulationMode.Random, Iterations = 3, DriveSimpleBuffs = true };
        var baseline = Run(pack, build, Context, options with { DriveSimpleBuffs = false });
        var driven = Run(pack, build, Context, options);
        if (earns)
        {
            // Stand Your Ground earns a stack a second and pays weapon damage for it.
            Assert.True(driven.TimedTriggerUptime[key].Seconds > 0);
            Assert.True(driven.WeaponDps.Mean > baseline.WeaponDps.Mean);
        }
        else
        {
            // Catalysis only builds while the target has a status, and nothing selected applies one.
            Assert.Equal(0, driven.TimedTriggerUptime.GetValueOrDefault(key)?.Seconds ?? 0);
            Assert.Equal(baseline.WeaponDps.Mean, driven.WeaponDps.Mean, 8);
        }
    }

    [Theory]
    [InlineData("weapon_talent_frenzy", "frenzy_active")]
    [InlineData("weapon_talent_on_empty", "on_empty_active")]
    public void CommittedReloadTimingTalentsFireMoreShots(string talent, string key)
    {
        // Frenzy's rate of fire and On Empty's handling (faster reloads) both follow a reload from empty.
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var weapon = pack.Pack.Items.First(item => item.Slot == "primary_weapon" && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = weapon.TalentSlots.First(slot => slot.Choices.Contains(talent));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", weapon.ID)
            .WithTalentSelection("primary_weapon", slot.ID, talent);
        var options = new DummyFightOptions { Duration = 30, Mode = SimulationMode.Random, Iterations = 1 };
        var baseline = Run(pack, build, Context, options);
        var driven = Run(pack, build, Context, options with { DriveSimpleBuffs = true });
        Assert.True(driven.ShotsFired > baseline.ShotsFired);
        Assert.True(driven.TimedTriggerUptime[key].Seconds > 0);
    }
}
