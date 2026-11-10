using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class DummyWeaponSimulatorTests
{
    private static ImmutableDictionary<string, string> Context => ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");

    private static CompiledPackExpressions Tiny(params (string Key, double Value)[] changes)
    {
        var values = new Dictionary<string, double>
        {
            ["equipped_weapon_rpm"] = 60,
            ["equipped_weapon_magazine_size"] = 2,
            ["equipped_weapon_reload_time"] = 2,
            ["equipped_weapon_pellets"] = 1,
            ["practical_hit_fraction"] = 1,
            ["practical_headshot_fraction"] = 0,
            ["effective_critical_hit_chance"] = 0,
            ["equipped_weapon_damage"] = 10,
            ["equipped_weapon_headshot_damage"] = 20,
            ["equipped_weapon_critical_damage"] = 15,
            ["equipped_weapon_critical_headshot_damage"] = 25,
            ["equipped_weapon_practical_shot_damage"] = 10
        };
        foreach (var (key, value) in changes) { values[key] = value; }
        var pack = new GamePack
        {
            ID = "test",
            Name = "Dummy",
            Version = "1",
            Level = 40,
            Slots = [new() { ID = "primary_weapon", Name = "Weapon", Weapon = true }],
            Items = [new() { ID = "gun", Name = "Gun", Slot = "primary_weapon" }],
            Stats = [.. values.Select(pair => new StatDefinition { ID = pair.Key, Name = pair.Key, Formula = pair.Value.ToString("0." + new string('#', 324), CultureInfo.InvariantCulture) })]
        };
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack).Compiled);
    }

    private static DummyFightResult Run(CompiledPackExpressions pack, BuildSnapshot build,
        ImmutableDictionary<string, string> context, DummyFightOptions? options = null) =>
        DummyWeaponSimulator.Run(pack, build, context, options, TestContext.Current.CancellationToken);

    private static BuildSnapshot Build(CompiledPackExpressions pack) => BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "gun");

    [Fact]
    public void ShotReloadOrderAndHalfOpenHorizonAreExplicit()
    {
        var pack = Tiny();
        var result = Run(pack, Build(pack), Context, new() { Duration = 5, CaptureFirstFightEvents = true });
        Assert.Empty(result.TargetStatusUptime);
        Assert.Empty(result.CyclePhaseUptime);
        Assert.Empty(result.MagazinePhaseUptime);
        Assert.Null(result.SkillDps);
        Assert.Equal(3, result.ShotsFired);
        Assert.Equal(1, result.Reloads);
        Assert.Equal(6, result.WeaponDps.Mean);
        Assert.Equal(new double[] { 10, 10, 0, 0, 10 }, result.Timeline);
        Assert.Equal(new[] { new DummyEvent(0, DummyEventKind.Shot, 10), new DummyEvent(1, DummyEventKind.Shot, 10),
            new DummyEvent(2, DummyEventKind.ReloadStarted), new DummyEvent(4, DummyEventKind.ReloadCompleted),
            new DummyEvent(4, DummyEventKind.Shot, 10) }, result.FirstFightEvents);
        Assert.Single(result.Limitations.Where(text => text.Contains("no target", StringComparison.Ordinal)));
    }

    [Fact]
    public void FractionalFinalBinContainsDamageAndReloadCanBeUnfinished()
    {
        var pack = Tiny();
        var result = Run(pack, Build(pack), Context, new() { Duration = 2.5, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 10, 10, 0 }, result.Timeline);
        Assert.Equal(8, result.WeaponDps.Mean);
        Assert.Equal(DummyEventKind.ReloadStarted, result.FirstFightEvents[^1].Kind);
        Assert.Equal(1, result.Reloads);
    }

    [Fact]
    public void NoReloadWeaponContinuesFiring()
    {
        var pack = Tiny(("weapon_no_reload", 1));
        var result = Run(pack, Build(pack), Context, new() { Duration = 5 });
        Assert.Equal(5, result.ShotsFired);
        Assert.Equal(0, result.Reloads);
        Assert.Empty(result.FirstFightEvents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void DurationMustBeFiniteAndBounded(double duration)
    {
        var pack = Tiny();
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(pack, Build(pack), Context, new() { Duration = duration }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void IterationsMustBeBounded(int iterations)
    {
        var pack = Tiny();
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(pack, Build(pack), Context, new() { Iterations = iterations }));
    }

    [Theory]
    [InlineData("equipped_weapon_rpm", 0)]
    [InlineData("equipped_weapon_magazine_size", 0.5)]
    [InlineData("equipped_weapon_pellets", 1001)]
    [InlineData("equipped_weapon_reload_time", -1)]
    public void InvalidResolvedWeaponCannotStart(string id, double value)
    {
        var pack = Tiny((id, value));
        Assert.Equal("weapon-stat-invalid", Assert.Throws<SimulationException>(() => Run(pack, Build(pack), Context)).Code);
    }

    [Fact]
    public void RequiresActiveWeaponAndRejectsUnimplementedShotMechanics()
    {
        var pack = Tiny();
        Assert.Equal("weapon-required", Assert.Throws<SimulationException>(() => Run(pack, Build(pack), ImmutableDictionary<string, string>.Empty)).Code);
        pack = Tiny(("practical_rounds_returned_per_shot", 0.1));
        Assert.Equal("shot-mechanic-unsupported", Assert.Throws<SimulationException>(() => Run(pack, Build(pack), Context)).Code);
    }

    [Fact]
    public void CancellationDoesNotReturnPartialResults()
    {
        var pack = Tiny();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new InlineProgress(update => { if (update.CompletedFights == 2) { cancellation.Cancel(); } });
        Assert.Throws<OperationCanceledException>(() => DummyWeaponSimulator.Run(pack, Build(pack), Context,
            new() { Mode = SimulationMode.Random, Iterations = 4 }, cancellation.Token, progress));
        Assert.Equal(new[] { 1, 2 }, progress.Updates.Select(update => update.CompletedFights));
    }

    [Fact]
    public void RandomOutcomesRepeatAndConvergeToTheResolvedExpectation()
    {
        var pack = Tiny(("weapon_no_reload", 1), ("practical_hit_fraction", 0.7),
            ("practical_headshot_fraction", 0.2), ("effective_critical_hit_chance", 0.4),
            ("equipped_weapon_practical_shot_damage", 9.8));
        var options = new DummyFightOptions { Duration = 120, Iterations = 200, Mode = SimulationMode.Random, Seed = 7 };
        var first = Run(pack, Build(pack), Context, options);
        var second = Run(pack, Build(pack), Context, options);
        Assert.Equal(first.WeaponDps, second.WeaponDps);
        Assert.Equal(first.Timeline, second.Timeline);
        Assert.InRange(first.WeaponDps.Mean, 9.8 * 0.98, 9.8 * 1.02);
        Assert.True(first.WeaponDps.P5 < first.WeaponDps.P95);
        Assert.True(first.WeaponDps.StdDev > 0);
        Assert.Equal(first.WeaponDps.Mean * options.Duration, first.Timeline.Sum(), 8);
    }

    [Fact]
    public void AverageModeIsOneFightIndependentOfSeedAndIterationCount()
    {
        var pack = Tiny();
        var first = Run(pack, Build(pack), Context, new() { Seed = 1 });
        var second = Run(pack, Build(pack), Context, new() { Seed = 999, Iterations = 1 });
        Assert.Equal(1, first.Iterations);
        Assert.Equal(first.WeaponDps, second.WeaponDps);
        Assert.Equal(first.Timeline, second.Timeline);
    }

    [Fact]
    public void CommittedRifleReproducesCoreReloadCycleDpsAgainstDummy()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var result = Run(pack, build, Context, new() { Duration = 600 });
        var resolved = BuildResolver.Resolve(pack, build, Context.SetItem("enemy_target", "0"));
        var expected = resolved.Calculation.Stats["equipped_weapon_practical_reload_cycle_dps"];
        Assert.True(expected > 0);
        Assert.InRange(result.WeaponDps.Mean, expected * 0.99, expected * 1.01);
        Assert.True(result.Reloads > 0);
    }

    [Fact]
    public void DamageOverflowFailsInsteadOfReturningNonFiniteResults()
    {
        var pack = Tiny(("weapon_no_reload", 1), ("equipped_weapon_practical_shot_damage", 1e308));
        Assert.Equal("damage-overflow", Assert.Throws<SimulationException>(() => Run(pack, Build(pack), Context, new() { Duration = 2 })).Code);
    }

    [Fact]
    public void TraceIsBoundedWithoutDroppingAggregateDamage()
    {
        var pack = Tiny(("weapon_no_reload", 1), ("equipped_weapon_rpm", 6000000));
        var result = Run(pack, Build(pack), Context, new() { Duration = 1.1, CaptureFirstFightEvents = true });
        Assert.Equal(100000, result.FirstFightEvents.Length);
        Assert.True(result.TraceTruncated);
        Assert.True(result.ShotsFired > result.FirstFightEvents.Length);
        Assert.Equal(result.ShotsFired * 10, result.Timeline.Sum(), 6);
    }

    [Fact]
    public void RandomPelletsRetainMeanDamageButReduceSpread()
    {
        var options = new DummyFightOptions { Mode = SimulationMode.Random, Duration = 60, Iterations = 200, Seed = 17 };
        var one = Tiny(("weapon_no_reload", 1), ("practical_hit_fraction", 0.5), ("equipped_weapon_practical_shot_damage", 5));
        var eight = Tiny(("weapon_no_reload", 1), ("practical_hit_fraction", 0.5), ("equipped_weapon_practical_shot_damage", 5), ("equipped_weapon_pellets", 8));
        var single = Run(one, Build(one), Context, options);
        var shotgun = Run(eight, Build(eight), Context, options);
        Assert.InRange(single.WeaponDps.Mean, 4.8, 5.2);
        Assert.InRange(shotgun.WeaponDps.Mean, 4.8, 5.2);
        Assert.True(shotgun.WeaponDps.StdDev < single.WeaponDps.StdDev * 0.6);
    }

    [Fact]
    public void TargetsDiscardOverkillAndWaitBeforeRetargeting()
    {
        var pack = Tiny(("enemy_target_armor", 5), ("enemy_target_health", 10),
            ("out_of_cover_damage_factor", 1), ("damage_to_armor", 0), ("health_damage", 0),
            ("equipped_weapon_practical_shot_damage", 100), ("weapon_no_reload", 1));
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, Build(pack), Context,
            new() { Duration = 5, CaptureFirstFightEvents = true }, retargetDelay: 2,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Kills);
        Assert.Equal(3, result.ShotsFired);
        Assert.Equal(45, result.Timeline.Sum());
        Assert.Equal(new double[] { 0, 0, 0 }, result.KillTimes);
        Assert.Equal(new double[] { 0, 2, 4 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time));
        Assert.Equal(new double[] { 2, 4 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.TargetArrived).Select(e => e.Time));
    }

    [Theory]
    [InlineData(10, 10, 1, 1, 50, 20, 0, 0)]
    [InlineData(10, 10, 2, 1, 10, 15, 0, 5)]
    [InlineData(10, 10, 1, 2, 12, 14, 0, 6)]
    [InlineData(10, 10, 0, 2, 100, 0, 10, 10)]
    [InlineData(0, 10, 1, 0, 100, 0, 0, 10)]
    public void ArmorAndHealthConsumeRawDamageWithSeparateFactors(double armor, double health,
        double armorFactor, double healthFactor, double raw, double dealt, double armorLeft, double healthLeft)
    {
        var target = new TargetPool(armor, health, armorFactor, healthFactor);
        Assert.Equal(dealt, target.Apply(raw));
        Assert.Equal(armorLeft, target.Armor);
        Assert.Equal(healthLeft, target.Health);
    }

    [Fact]
    public void InvalidTargetAndDamageAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetPool(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetPool(double.NaN, 1));
        var target = new TargetPool(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => target.Apply(double.PositiveInfinity));
        Assert.Equal(1, target.Armor);
        var tinyArmor = new TargetPool(double.Epsilon, 1, double.MaxValue);
        Assert.Equal(0, tinyArmor.Apply(0));
        Assert.Equal(double.Epsilon, tinyArmor.Armor);
    }

    [Fact]
    public void RetargetingDuringReloadCountsTheWaitInTheNextTimeToKill()
    {
        var pack = Tiny(("enemy_target_armor", 0), ("enemy_target_health", 5),
            ("out_of_cover_damage_factor", 1), ("damage_to_armor", 0), ("health_damage", 0),
            ("equipped_weapon_magazine_size", 1));
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, Build(pack), Context,
            new() { Duration = 4, CaptureFirstFightEvents = true }, retargetDelay: 1,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Kills);
        Assert.Equal(new double[] { 0, 2 }, result.KillTimes);
        Assert.Equal(result.FirstFightEvents.OrderBy(e => e.Time), result.FirstFightEvents);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) TwoWeapons()
    {
        var original = Tiny(("equipped_weapon_magazine_size", 1), ("enemy_target_armor", 0),
            ("enemy_target_health", 5), ("out_of_cover_damage_factor", 1), ("damage_to_armor", 0), ("health_damage", 0)).Pack;
        var channels = new[] { "equipped_weapon_damage", "equipped_weapon_headshot_damage", "equipped_weapon_critical_damage",
            "equipped_weapon_critical_headshot_damage", "equipped_weapon_practical_shot_damage" };
        var pack = original with
        {
            Stats = [.. original.Stats.Select(stat => channels.Contains(stat.ID) ? stat with { Formula = "weapon_base_damage" } : stat), new() { ID = "weapon_base_damage", Formula = "0" }],
            Slots = [.. original.Slots, new() { ID = "secondary_weapon", Name = "Secondary", Weapon = true }],
            Items = [original.Items[0] with { Weapon = [new("weapon_base_damage", 10)] },
                new() { ID = "other", Name = "Other", Slot = "secondary_weapon", Weapon = [new("weapon_base_damage", 100)] }]
        };
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack).Compiled);
        return (compiled, BuildSnapshot.Create(pack).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other"));
    }

    [Fact]
    public void RotationChangesDamageAndIncludesDestinationReload()
    {
        var (pack, build) = TwoWeapons();
        var result = Run(pack, build, Context, new() { Duration = 7, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon") });
        var shots = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).ToArray();
        Assert.Equal(new double[] { 0, 2, 6 }, shots.Select(e => e.Time));
        Assert.Equal(new double[] { 10, 100, 10 }, shots.Select(e => e.Damage));
        Assert.Equal(new[] { "primary_weapon", "secondary_weapon", "primary_weapon" }, shots.Select(e => e.WeaponSlot));
        Assert.Equal(120, result.Timeline.Sum());
        Assert.Equal(2, result.Swaps);
        Assert.Equal(1, result.Reloads);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.ReloadStarted && e.Time == 4);
    }

    [Fact]
    public void KillSwapsOverlapRetargetDelayAndPreserveTimeToKill()
    {
        var (pack, build) = TwoWeapons();
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 4, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill) },
            retargetDelay: 3, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Kills);
        Assert.Equal(new double[] { 0, 3 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(10, result.Timeline.Sum());
        Assert.Equal(new double[] { 0, 0 }, result.KillTimes);
    }

    [Fact]
    public void ATargetedRotationRequiresTwoCompatibleWeaponsAndRejectsKillModeForDummy()
    {
        var (pack, build) = TwoWeapons();
        Assert.Equal("target-required", Assert.Throws<SimulationException>(() => Run(pack, build, Context,
            new() { Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill) })).Code);
        Assert.Equal("weapon-required", Assert.Throws<SimulationException>(() => Run(pack, build, Context,
            new() { Rotation = new("missing") })).Code);
    }

    [Fact]
    public void AWeaponSwapChangesArmorFactorsWithoutResettingRemainingPools()
    {
        var (original, _) = TwoWeapons();
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(s => s.ID is "enemy_target_armor" or "enemy_target_health" ? s with { Formula = "100" } : s)],
            Items = [.. original.Pack.Items.Select(i => i.ID == "other" ? i with { Modifiers = [new() { Stat = "damage_to_armor", Op = ModifierOps.Flat, Value = new(1) }] } : i)]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 7, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon") },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 10, 145, 10 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(0, result.Kills);
    }

    [Fact]
    public void SwapAtTheHorizonIsNotCountedOrTraced()
    {
        var (pack, build) = TwoWeapons();
        var result = Run(pack, build, Context, new() { Duration = 1, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon") });
        Assert.Equal(0, result.Swaps);
        Assert.Equal(DummyEventKind.Shot, Assert.Single(result.FirstFightEvents).Kind);
    }

    [Fact]
    public void RandomRotationsAreRepeatableAndConserveTimelineDamage()
    {
        var (pack, build) = TwoWeapons();
        var options = new DummyFightOptions { Duration = 20, Mode = SimulationMode.Random, Iterations = 5, Rotation = new("secondary_weapon") };
        var first = Run(pack, build, Context, options);
        var second = Run(pack, build, Context, options);
        Assert.Equal(first.WeaponDps, second.WeaponDps);
        Assert.Equal(first.Timeline, second.Timeline);
        Assert.Equal(first.WeaponDps.Mean * options.Duration, first.Timeline.Sum(), 8);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) WithSkill(double impact = 100, double rate = 10)
    {
        var (original, _) = TwoWeapons();
        var rules = original.Pack with
        {
            ActiveSkillSlots = [new() { ID = "skill_slot_1", Name = "Skill", Choices = ["test_skill"] }],
            ActiveSkills = [new() { ID = "test_skill", Name = "Test", Family = "test", PreviewOutputs = [
                new() { ID = "impact", Formula = impact.ToString(CultureInfo.InvariantCulture) },
                new() { ID = "rate", Formula = rate.ToString(CultureInfo.InvariantCulture) },
                new() { ID = "window", Formula = "2" }, new() { ID = "cooldown", Formula = "3" }] }],
            Simulation = new() { Skills = [new() { Skill = "test_skill", Impact = "impact", Rate = "rate", Window = "window", Cooldown = "cooldown" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other").WithActiveSkillSelection("skill_slot_1", "test_skill"));
    }

    [Fact]
    public void SkillsContinueDuringReloadAndMatchTheirStandaloneDummyIntegral()
    {
        var (pack, build) = WithSkill();
        var result = Run(pack, build, Context, new() { Duration = 7, IncludeSkills = true, CaptureFirstFightEvents = true });
        Assert.Equal(50, result.SkillDps!.Total.Mean, 8);
        Assert.Equal(300d / 7, result.SkillDps!.Impact.Mean, 8);
        Assert.Equal(50d / 7, result.SkillDps.ContinuousWindow.Mean, 8);
        Assert.Equal(380, result.Timeline.Sum(), 7);
        Assert.Equal(result.WeaponDps.Mean + result.SkillDps.Total.Mean, result.TotalDps!.Mean, 8);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.SkillTick && e.Time > 1 && e.Time < 3);
    }

    [Fact]
    public void SkillBreakdownKeepsFinalContinuousDamageWithoutEventCapture()
    {
        var (pack, build) = WithSkill(impact: 0, rate: 10);
        var result = Run(pack, build, Context, new() { Duration = 0.25, IncludeSkills = true });
        Assert.Empty(result.FirstFightEvents);
        Assert.Equal(0, result.SkillDps!.Impact.Mean);
        Assert.Equal(10, result.SkillDps.ContinuousWindow.Mean, 8);
        Assert.Equal(2.5, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Theory]
    [InlineData(100, 0, 100, 0)]
    [InlineData(0, 10, 0, 20)]
    [InlineData(0, 0, 0, 0)]
    public void SkillPartsHaveIndependentZeroChannelsAndFullDurationDenominators(double impact, double rate, double impactDamage, double overTimeDamage)
    {
        var (pack, build) = WithSkill(impact, rate);
        var options = new DummyFightOptions { Duration = 2.25, IncludeSkills = true, Mode = SimulationMode.Random, Iterations = 3 };
        var result = Run(pack, build, Context, options);
        Assert.Equal(impactDamage / options.Duration, result.SkillDps!.Impact.Mean, 8);
        Assert.Equal(overTimeDamage / options.Duration, result.SkillDps.ContinuousWindow.Mean, 8);
        Assert.Equal(result.SkillDps!.Total.Mean, result.SkillDps.Impact.Mean + result.SkillDps.ContinuousWindow.Mean, 8);
        Assert.Equal(0, result.SkillDps.Impact.StdDev, 8);
        Assert.Equal(0, result.SkillDps.ContinuousWindow.StdDev, 8);
    }

    [Fact]
    public void SkillKillsSuppressSameTimeWeaponShotsUntilTheNextTarget()
    {
        var (pack, build) = WithSkill();
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 2, IncludeSkills = true, CaptureFirstFightEvents = true },
            retargetDelay: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Kills >= 2);
        Assert.Equal(result.SkillDps!.Total.Mean, result.SkillDps!.Impact.Mean + result.SkillDps.ContinuousWindow.Mean, 8);
        Assert.True(result.SkillDps.Impact.Mean < 100);
        Assert.Equal(DummyEventKind.SkillImpact, result.FirstFightEvents[0].Kind);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.Shot && e.Time == 0);
        Assert.InRange(result.Timeline.Sum(), 0, (result.Kills + 1) * 5);
    }

    [Fact]
    public void SkillsDuringSwapsConserveTotalDamage()
    {
        var (pack, build) = WithSkill();
        var options = new DummyFightOptions { Duration = 7, IncludeSkills = true, Rotation = new("secondary_weapon") };
        var result = Run(pack, build, Context, options);
        Assert.Equal(470, result.Timeline.Sum(), 7);
        Assert.Equal(2, result.Swaps);
        Assert.Equal(50, result.SkillDps!.Total.Mean, 7);
    }

    [Fact]
    public void SkillKillSwapsBeforeFiringWithoutSpendingTheOutgoingRound()
    {
        var (pack, build) = WithSkill();
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 2, IncludeSkills = true, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill) },
            retargetDelay: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.SwapStarted && e.Time == 0 && e.WeaponSlot == "primary_weapon");
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.Shot && e.Time == 0);
        Assert.Equal("d2p-static-skills-v6/" + SimulationRandom.Contract, result.Contract);
    }

    [Fact]
    public void IntegratedDummySkillDamageMatchesStandaloneSchedule()
    {
        var (pack, build) = WithSkill(impact: 0, rate: 10);
        var result = Run(pack, build, Context, new() { Duration = 2.25, IncludeSkills = true });
        var resolved = BuildResolver.Resolve(pack, build, Context.SetItem("enemy_target", "0"));
        var expected = SkillDamageSchedule.Create(pack.Pack, resolved.ActiveSkillPreviews).DummyTimeline(2.25, TestContext.Current.CancellationToken).Sum();
        Assert.Equal(expected, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Fact]
    public void AContinuousSkillKillDuringReloadQueuesSwapUntilWeaponReadiness()
    {
        var (original, _) = WithSkill(impact: 0, rate: 100);
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(s => s.ID == "enemy_target_health" ? s with { Formula = "100" } : s)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other")
            .WithActiveSkillSelection("skill_slot_1", "test_skill");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 3.5, IncludeSkills = true, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill) },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.Kill && e.Time < 1);
        Assert.Equal(3, result.FirstFightEvents.First(e => e.Kind == DummyEventKind.SwapStarted).Time);
        Assert.Equal(1, result.Reloads);
        Assert.Single(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot));
    }

    // Resolved's shape (R36): a ready rule that forces the next hit to a headshot and recovers 2 s after the hit spends it.
    // Headshots deal triple damage here and the magazine never empties within the fight.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ForcedHeadshots(string on = "hit", string[]? weaponTags = null)
    {
        var (original, build) = BuffBuild();
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(stat => stat.ID switch
            {
                "equipped_weapon_headshot_damage" or "equipped_weapon_critical_headshot_damage" => stat with { Formula = "3 * weapon_base_damage" },
                "equipped_weapon_magazine_size" => stat with { Formula = "100" },
                _ => stat
            })],
            Items = [.. original.Pack.Items.Select(item => item.ID == "gun" ? item with { Tags = [.. item.Tags, "tag_gun"] } : item)],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "buff", On = on, Kind = "ready", Duration = 2, ForceHeadshot = true, WeaponTags = [.. weaponTags ?? []] }]
            }
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReadyForcedHeadshotRuleMakesTheNextHitAHeadshotOncePerCooldown(bool tagged)
    {
        // Shots every second never land a natural headshot. The rule is ready at 0, spent by that hit, ready again at 2.
        var (pack, build) = ForcedHeadshots(weaponTags: tagged ? ["tag_gun"] : null);
        var heads = new SimTriggerDefinition { Talent = "test_buff", Key = "heads", On = "headshot", Kind = "stacks", Max = 100 };
        var rules = pack.Pack with
        {
            NumericContextInputs = [.. pack.Pack.NumericContextInputs, new() { ID = "heads", Name = "Headshots", Min = 0, Max = 100, Step = 1 }],
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers, heads] }
        };
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run(compiled, build, Context, new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 30, 10, 30, 10, 30, 10, 30 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        // Each forced headshot fires headshot: 1, 2, 3 and 4 stacks from 0, 2, 4 and 6 s, a mean of 16/7 over the fight.
        Assert.Equal(16.0 / 7, result.TimedTriggerUptime["heads"].MeanStacks!.Value, 8);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) BuffBuild(string stat = "buff_damage", string on = "hit")
    {
        var (original, _) = TwoWeapons();
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(s => s.ID is "equipped_weapon_damage" or "equipped_weapon_critical_damage" or "equipped_weapon_headshot_damage" or "equipped_weapon_critical_headshot_damage"
                ? s with { Formula = "weapon_base_damage + buff_damage" } : s), new() { ID = "buff_damage", Formula = "0" }],
            Selectors = [new() { ID = "buff", Name = "Buff", Values = ["true"] }],
            Talents = [new() { ID = "test_buff", Name = "Buff", Modifiers = [new() { Stat = stat, Op = ModifierOps.Flat, Value = new(10), RequiresContext = ImmutableDictionary<string, string>.Empty.Add("buff", "true") }] }],
            Items = [.. original.Pack.Items.Select(i => i.ID == "gun" ? i with { TalentSlots = [new() { ID = "talent", Choices = ["test_buff"] }] } : i)],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "buff", On = on, Kind = "buff", Duration = 2.5 }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        return (pack, build);
    }

    // Reload-finished talents (On Empty, Frenzy, Overflowing): the reload from 1 s completes at 3 s, so a reload_end buff
    // runs from 3 s to the 5.5 s horizon, where a reload buff ran [1, 3.5) and [4, 5.5). A whole-second rule moves reloads
    // onto the deferred path; the result is the same.
    [Theory]
    [InlineData("reload", false, 4)]
    [InlineData("reload_end", false, 2.5)]
    [InlineData("reload_end", true, 2.5)]
    public void ReloadEndFiresWhenTheReloadCompletes(string on, bool deferred, double uptime)
    {
        var (pack, build) = BuffBuild(on: on);
        var rules = deferred ? pack.Pack with
        {
            Selectors = [.. pack.Pack.Selectors, new() { ID = "tick", Name = "Tick", Values = ["true"] }],
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers, new() { Talent = "test_buff", Key = "tick", On = "second", Kind = "buff", Duration = 0.5 }] }
        } : pack.Pack;
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run(compiled, build, Context, new() { Duration = 5.5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true });
        Assert.Equal(uptime, result.TimedTriggerUptime["buff"].Seconds);
    }

    [Theory]
    [InlineData("reload", new double[] { 10, 100, 10, 100, 10 })]
    [InlineData("reload_end", new double[] { 10, 100, 20, 110, 20 })]
    public void ReloadEndUnderRotationBuffsTheShotAfterTheReload(string on, double[] expected)
    {
        // Reloads run 4–6, 8–10 and 12–14 s; a 1 s gear buff from the reload start has ended by the next shot, one from the
        // completion covers it.
        var (pack, build) = RotatingBuff(on, gear: true, duration: 1);
        var result = Run(pack, build, Context, new() { Duration = 15, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true, Rotation = new("secondary_weapon") });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(3, result.TimedTriggerUptime["buff"].Seconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimedTriggerUptimeMeasuresReloadExpiryAndFinalPartialInterval(bool capture)
    {
        var (pack, build) = BuffBuild(on: "reload");
        var result = Run(pack, build, Context, new()
        {
            Duration = 5.5,
            Mode = SimulationMode.Random,
            Iterations = 3,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = capture
        });
        // Reload buffs span [1, 3.5) and [4, 5.5), including time spent reloading.
        Assert.Equal(new TimedTriggerUptime(4, 4 / 5.5, null), result.TimedTriggerUptime["buff"]);
        Assert.Empty(Run(pack, build, Context, new() { Duration = 5.5 }).TimedTriggerUptime);
    }

    [Theory]
    [InlineData(false, false, new double[] { 10, 10, 10, 20, 20 })]
    [InlineData(false, true, new double[] { 10, 10, 10, 20, 10 })]
    [InlineData(true, false, new double[] { 10, 10, 10, 20, 20 })]
    public void ArmedStatusesAffectLaterShotsAndExpireAcrossReloads(bool appliedEventBuff, bool reload, double[] expected)
    {
        var (original, _) = BuffBuild();
        var status = new SimTriggerDefinition { Talent = "test_buff", Key = "burning", On = "hit", Kind = "status", Status = "burning", Duration = 2.5, Count = 2 };
        SimTriggerDefinition[] triggers = appliedEventBuff ? [status, original.Pack.Simulation.Triggers[0] with { On = "status_applied", WhileStatus = "burning" }] : [status];
        var rules = original.Pack with
        {
            Selectors = [.. original.Pack.Selectors, new() { ID = "burning", Name = "Burning", Values = ["true"] }],
            Talents = [original.Pack.Talents[0] with { Modifiers = [original.Pack.Talents[0].Modifiers[0] with {
                RequiresContext = ImmutableDictionary<string, string>.Empty.Add(appliedEventBuff ? "buff" : "burning", "true") }] }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "equipped_weapon_magazine_size" ? s with { Formula = "4" } : s), new() { ID = "weapon_no_reload", Formula = reload ? "0" : "1" }],
            Simulation = new() { Triggers = [.. triggers] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context.Add("burning", "true"), new() { Duration = reload ? 7 : 5, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        var seconds = reload ? 3.5 : 3;
        Assert.Equal(new StatusUptime(seconds, seconds / result.Duration), result.TargetStatusUptime["burning"]);
        Assert.Equal(result.TargetStatusUptime["burning"], result.TargetStatusUptime["any"]);
    }

    [Theory]
    [InlineData(0, 3, new double[] { 10, 10, 20, 20, 10 })]
    [InlineData(2, 2, new double[] { 10, 10, 20, 20 })]
    public void RetargetClearsAppliedStatusesBeforeTheNextTargetsFirstShot(double delay, double statusSeconds, double[] damage)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "enemy_target_health" ? s with { Formula = "60" } : s), new() { ID = "weapon_no_reload", Formula = "1" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "buff", Kind = "status", Status = "burning", On = "hit", Duration = 10 }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            retargetDelay: delay, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(damage, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(1, result.Kills);
        Assert.Equal(new StatusUptime(statusSeconds, statusSeconds / result.Duration), result.TargetStatusUptime["burning"]);
    }

    [Theory]
    [InlineData(false, new double[] { 10, 90, 90, 90 })]
    [InlineData(true, new double[] { 10, 0, 10, 0 })]
    public void PelletReadingsUseThePreviousShotAndClearAfterAMiss(bool alternateMisses, double[] expected)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "prior", Name = "Prior hits", Min = 0, Max = 8, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('prior') * 10" } :
                s.ID == "equipped_weapon_pellets" ? s with { Formula = "8" } :
                s.ID == "practical_hit_fraction" && alternateMisses ? s with { Formula = "context_value('prior') > 0 ? 0 : 1" } : s), new() { ID = "weapon_no_reload", Formula = "1" }],
            Talents = [new() { ID = "test_buff", Name = "Prior pellets" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "prior", Kind = "pellets" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context.Add("prior", "8"), new() { Duration = 4, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(expected.Sum() / 4, result.WeaponDps.Mean, 8);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    public void KillsDropPickupsCollectedAfterTheAssumedDelay(double chance)
    {
        // Kill Confirmed's shape: a rule on "pickup" gains its buff when a kill's trophy is collected.
        var (original, _) = BuffBuild(on: "pickup");
        var rules = original.Pack with { Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Duration = 100 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 6, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true, PickupDelay = 0.5, PickupChance = chance },
            retargetDelay: 1, cancellationToken: TestContext.Current.CancellationToken);
        var kills = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time).ToArray();
        var pickups = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Pickup).Select(e => e.Time).ToArray();
        Assert.NotEmpty(kills);
        if (chance == 0)
        {
            Assert.Empty(pickups);
            Assert.Equal(0, result.TimedTriggerUptime["buff"].Seconds);
            return;
        }
        // Every kill's trophy is collected half a second later, and the buff runs from the first collection.
        Assert.Equal(kills.Where(kill => kill + 0.5 < 6).Select(kill => kill + 0.5), pickups);
        Assert.Equal(6 - pickups[0], result.TimedTriggerUptime["buff"].Seconds, 8);
        Assert.Contains(result.Limitations, note => note.Contains("collected 0.5 s later, 100% of the time", StringComparison.Ordinal));
    }

    [Fact]
    public void APoolCapBanksStacksIntoALastingBonusThatReResolvesDamage()
    {
        // Rebalance's shape: hits build pooled stacks; at the cap they clear into a bank that a stat reads.
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "count", Name = "Count", Min = 0, Max = 3, Step = 1 }, new() { ID = "banked", Name = "Banked", Min = 0, Max = 3, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('banked') * 10" } : s), new() { ID = "weapon_no_reload", Formula = "1" }],
            Talents = [new() { ID = "test_buff", Name = "Banked hits" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "count", On = "hit", Kind = "stacks", Max = 3, Pool = "p", PoolMax = 3, Bank = "banked" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 4, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        var shots = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage).ToArray();
        Assert.True(shots.Length >= 4);
        // The third hit reaches the cap and banks 3 stacks: every later shot deals 30 more.
        Assert.Equal([shots[0], shots[0], shots[0]], shots[..3]);
        Assert.All(shots[3..], damage => Assert.Equal(shots[0] + 30, damage, 8));
    }

    [Theory]
    [InlineData(50, 50, 1, new double[] { 40, 21, 13 })]
    [InlineData(0, 20, 0, new double[] { 20, 20, 20 })]
    public void TargetReadingsUseCombinedPoolsBeforeDamageAndResetOnRetarget(double armor, double health, double armorBonus, double[] expected)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "remaining", Name = "Remaining", Min = 0, Max = 100, Step = 10 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('remaining') / 10" } :
                s.ID == "enemy_target_armor" ? s with { Formula = armor.ToString(CultureInfo.InvariantCulture) } :
                s.ID == "enemy_target_health" ? s with { Formula = health.ToString(CultureInfo.InvariantCulture) } :
                s.ID == "damage_to_armor" ? s with { Formula = armorBonus.ToString(CultureInfo.InvariantCulture) } : s), new() { ID = "weapon_no_reload", Formula = "1" }],
            Talents = [new() { ID = "test_buff", Name = "Remaining pools" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "remaining", Kind = "target" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, new() { Duration = 3, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(expected.Sum() / 3, result.WeaponDps.Mean, 8);
    }

    [Fact]
    public void CommittedRangerRequiresDistanceAndAmplifiesFightDamage()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", "weapon_talent_ranger");
        var options = new DummyFightOptions { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true };
        Assert.Equal("distance-required", Assert.Throws<SimulationException>(() => Run(pack, build, Context, options)).Code);
        var close = Run(pack, build, Context, options with { Distance = 0 });
        var far = Run(pack, build, Context, options with { Distance = 20 });
        Assert.Equal(close.WeaponDps.Mean * 1.1, far.WeaponDps.Mean, 6);
        Assert.Equal(close.ShotsFired, far.ShotsFired);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(1001)]
    public void InvalidFightDistanceIsRefused(double distance)
    {
        var pack = Tiny();
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(pack, Build(pack), Context, new() { Distance = distance }));
    }

    [Theory]
    [InlineData(false, new double[] { 10, 35, 60, 85, 10 })]
    [InlineData(true, new double[] { 10, 35, 10, 35, 10, 35, 10 })]
    public void MissingMagazinePercentTracksShotsAndResetsOnReloadOrRefill(bool refill, double[] expected)
    {
        var (original, _) = BuffBuild();
        var phase = new SimTriggerDefinition { Talent = "test_buff", Key = "missing", Kind = "magazine", Percent = true };
        SimTriggerDefinition[] triggers = refill ? [phase, original.Pack.Simulation.Triggers[0] with { Count = 2, Refill = true }] : [phase];
        var rules = original.Pack with
        {
            NumericContextInputs = [new() { ID = "missing", Name = "Missing", Min = 0, Max = 100, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('missing')" } :
                s.ID == "equipped_weapon_magazine_size" ? s with { Formula = "4" } : s)],
            Talents = [new() { ID = "test_buff", Name = "Magazine test" }],
            Simulation = new() { Triggers = [.. triggers] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context.Add("missing", "100"), new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(refill ? 0 : 1, result.Reloads);
        Assert.Equal(expected.Sum() / 7, result.WeaponDps.Mean, 8);
        var occupancy = result.MagazinePhaseUptime["missing"];
        Assert.Equal(7, occupancy.Values.Sum(value => value.Seconds));
        Assert.Equal(new PhaseUptime(refill ? 4 : 2, (refill ? 4d : 2d) / 7), occupancy["25"]);
        Assert.Equal(new PhaseUptime(refill ? 0 : 3, (refill ? 0d : 3d) / 7), occupancy["100"]);
        Assert.Equal(new PhaseUptime(refill ? 3 : 0, (refill ? 3d : 0d) / 7), occupancy["0"]);
    }

    [Fact]
    public void CyclePhasesAdvanceAcrossReloadGaps()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "cycle", Name = "Cycle", Min = 0, Max = 20, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('cycle')" } : s)],
            Talents = [new() { ID = "test_buff", Name = "Cycle test" }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "cycle", Kind = "cycle",
                Phases = [new() { Until = 2, Value = "0" }, new() { Until = 4, Value = "20" }] }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 10, 30, 30 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(10, result.WeaponDps.Mean, 8);
        Assert.Equal(new PhaseUptime(4, 4d / 7), result.CyclePhaseUptime["cycle"]["0"]);
        Assert.Equal(new PhaseUptime(3, 3d / 7), result.CyclePhaseUptime["cycle"]["20"]);
        Assert.Empty(result.TimedTriggerUptime);
    }

    [Fact]
    public void CommittedOptimistIncreasesFightDamageAndMeasuredPacesItsMagazine()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_optimist");
        var context = Context.Add("practical_hit_rate", "100").Add("ammo_missing_percent", "0");
        var options = new DummyFightOptions { Duration = 10, Mode = SimulationMode.Random, Iterations = 1 };
        var baseline = Run(pack, build, context, options);
        var driven = Run(pack, build, context, options with { DriveSimpleBuffs = true });
        Assert.True(driven.WeaponDps.Mean > baseline.WeaponDps.Mean);
        Assert.Equal(baseline.ShotsFired, driven.ShotsFired);
        // Measured fires the top half of each magazine faster than the bottom half.
        var measured = Run(pack, build.WithTalentSelection("primary_weapon", "talent", "weapon_talent_measured"), context,
            options with { DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        var reload = measured.FirstFightEvents.First(e => e.Kind == DummyEventKind.ReloadStarted).Time;
        var times = measured.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot && e.Time < reload).Select(e => e.Time).ToArray();
        Assert.True(times[1] - times[0] < times[^1] - times[^2]);
    }

    [Theory]
    [InlineData("ready", new double[] { 20, 10, 10, 20, 10, 10, 20 })]
    [InlineData("lockout", new double[] { 20, 10, 10, 10, 10, 10, 10 })]
    public void InverseBooleanRulesStartActiveAndDriveLaterShotDamage(string kind, double[] expected)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats, new() { ID = "weapon_no_reload", Formula = "1" }],
            Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Kind = kind }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context.Add("buff", "false"), new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(expected.Sum() / 7, result.WeaponDps.Mean, 8);
    }

    [Fact]
    public void HitBuffUpdatesLaterShotsAndExpiresDuringReload()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(s => s.ID == "equipped_weapon_magazine_size" ? s with { Formula = "2" } : s)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context.Add("buff", "true"), new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 10, 20, 10 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
    }

    [Theory]
    [InlineData("hit", new double[] { 0, 1, 2, 3, 4, 5, 6 })]
    [InlineData("kill", new double[] { 0, 1, 2, 5, 6 })]
    public void RefillSpendsTheShotFirstAndPreventsReloadOnlyOnActivation(string on, double[] expected)
    {
        var (original, _) = BuffBuild(on: on);
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "equipped_weapon_magazine_size" ? s with { Formula = "3" } : s)],
            Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Count = 3, Duration = 0, Refill = true }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(on == "hit" ? 0 : 1, result.Reloads);
        Assert.Equal(expected.Length, result.ShotsFired);
    }

    [Fact]
    public void PersistentBuffCannotSilentlyChangeMagazineCapacity()
    {
        var (original, _) = BuffBuild("equipped_weapon_magazine_size", "reload");
        var rules = original.Pack with { Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Count = 3, Duration = 0, ConsumeOn = "reload" }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        Assert.Equal("buff-timing-unsupported", Assert.Throws<SimulationException>(() => Run(pack, build, Context,
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void CommittedPummelRefillsAfterKillsAndReducesReloads()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_m870_express")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_pummel");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "100")
            .Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_players", "1");
        var options = new DummyFightOptions { Duration = 30, Mode = SimulationMode.Random, Iterations = 1 };
        var baseline = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options, retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        var triggered = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options with { DriveSimpleBuffs = true }, retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(triggered.Reloads < baseline.Reloads);
        Assert.True(triggered.Kills > baseline.Kills);
    }

    [Theory]
    [InlineData(2, 3, 1.5, new double[] { 10, 10, 20, 10, 10, 10, 20 })]
    [InlineData(3, 0, 2.5, new double[] { 10, 10, 10, 20, 20, 10, 20 })]
    public void CountedAndCooldownLimitedBuffsAffectLaterShots(long count, double every, double duration, double[] expected)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats, new() { ID = "weapon_no_reload", Formula = "1" }],
            Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Count = count, Every = every, Duration = duration }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
    }

    [Fact]
    public void CommittedStrainedIncreasesCriticalDamageWithoutChangingShotTiming()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_strained")
            .WithAttributeRoll("primary_weapon", "minor_1", new("critical_hit_chance_weapon_minor", 0.095));
        var context = Context.Add("practical_hit_rate", "100").Add("strained_stacks", "0");
        var options = new DummyFightOptions { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, CaptureFirstFightEvents = true };
        var baseline = Run(pack, build, context, options);
        var triggered = Run(pack, build, context, options with { DriveSimpleBuffs = true });
        Assert.True(triggered.WeaponDps.Mean > baseline.WeaponDps.Mean);
        Assert.Equal(baseline.FirstFightEvents.Select(e => (e.Kind, e.Time)), triggered.FirstFightEvents.Select(e => (e.Kind, e.Time)));
    }

    [Fact]
    public void AFireRateBuffPacesIntervalsThatStartAfterItIsGained()
    {
        // +10 RPM from the first hit: the shot that gains it keeps its 1 s interval; the next shot's is 60/70 s.
        var (original, _) = BuffBuild("equipped_weapon_rpm");
        var rules = original.Pack with { Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Duration = 100 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 7, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new[] { 0, 3, 3 + 60 / 70d + 2 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new[] { 1, 3 + 60 / 70d, 3 + 60 / 70d + 2 + 60 / 70d }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadStarted).Select(e => e.Time));
    }

    [Fact]
    public void AReloadTimeBuffAppliesFromTheNextReloadItDoesNotTrigger()
    {
        // +10 s reload time gained on reload: the reload that grants it keeps its 2 s; the next one takes 12 s.
        var (original, _) = BuffBuild("equipped_weapon_reload_time", "reload");
        var rules = original.Pack with { Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Duration = 100 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 20, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 0, 3, 16 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
    }

    [Theory]
    [InlineData("hit", 0.5, 1, new double[] { 0, 3 })]
    [InlineData("hit", 1, 1, new double[] { 0, 3 })]
    [InlineData("hit", 1.5, 1, new double[] { 0, 2, 4 })]
    [InlineData("reload", 10, 1, new double[] { 0, 3, 5 })]
    [InlineData("hit", 10, 2, new double[] { 0, 1, 2, 3, 4, 5 })]
    public void ReloadDurationIsSampledAtStartAndNotRescheduled(string on, double duration, double reduction, double[] expectedShots)
    {
        var (original, _) = BuffBuild("equipped_weapon_reload_time", on);
        var rules = original.Pack with
        {
            Talents = [original.Pack.Talents[0] with { Modifiers = [original.Pack.Talents[0].Modifiers[0] with { Value = new(-reduction) }] }],
            Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Duration = duration }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 6, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expectedShots, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
    }

    [Fact]
    public void PersistentReloadStacksAffectTheReloadBeforeTheyReset()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 2, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "equipped_weapon_reload_time" ? s with { Formula = "2 / (1 + context_value('stacks'))" } : s)],
            Talents = [new() { ID = "test_buff", Name = "Stack talent" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "stacks", On = "hit", Kind = "stacks", Max = 2, ResetOn = "reload" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context.Add("stacks", "2"), new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 0, 2, 4 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new double[] { 2, 4 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadCompleted).Select(e => e.Time));
        Assert.Equal(new TimedTriggerUptime(3, 0.6, 0.6), result.TimedTriggerUptime["stacks"]);
    }

    [Fact]
    public void LongerTriggeredReloadCanExtendBeyondTheFightHorizon()
    {
        var (pack, build) = BuffBuild("equipped_weapon_reload_time");
        var result = Run(pack, build, Context, new() { Duration = 4, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(1, result.ShotsFired);
        Assert.Equal(1, result.Reloads);
        Assert.Single(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadStarted));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ReloadCompleted);
    }

    [Fact]
    public void CommittedFastHandsShortensReloadsAndFiresMoreShots()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_fast_hands")
            .WithAttributeRoll("primary_weapon", "minor_1", new("critical_hit_chance_weapon_minor", 0.095));
        var context = Context.Add("practical_hit_rate", "100").Add("fast_hands_stacks", "0");
        Assert.True(BuildResolver.Resolve(pack, build, context).Calculation.Stats["effective_critical_hit_chance"] > 0);
        var options = new DummyFightOptions { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, CaptureFirstFightEvents = true };
        var baseline = Run(pack, build, context, options);
        var triggered = Run(pack, build, context, options with { DriveSimpleBuffs = true });
        Assert.True(triggered.ShotsFired > baseline.ShotsFired);
        var baseReload = baseline.FirstFightEvents.First(e => e.Kind == DummyEventKind.ReloadCompleted).Time - baseline.FirstFightEvents.First(e => e.Kind == DummyEventKind.ReloadStarted).Time;
        var triggeredReload = triggered.FirstFightEvents.First(e => e.Kind == DummyEventKind.ReloadCompleted).Time - triggered.FirstFightEvents.First(e => e.Kind == DummyEventKind.ReloadStarted).Time;
        Assert.True(triggeredReload < baseReload);
    }

    [Fact]
    public void NonmatchingEventsCannotActivateBuffsAndAverageModeIsRefused()
    {
        var (pack, build) = BuffBuild(on: "kill");
        var result = Run(pack, build, Context, new() { Duration = 4, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), e => Assert.Equal(10, e.Damage));
        Assert.Equal("buff-mode-unsupported", Assert.Throws<SimulationException>(() => Run(pack, build, Context, new() { DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void CommittedConcussionHeadshotsIncreaseLaterFightDamageThroughCore()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack").WithTalentSelection("backpack", "talent", "gear_talent_concussion");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "100");
        var options = new DummyFightOptions { Duration = 10, Mode = SimulationMode.Random, Iterations = 1 };
        var baseline = Run(pack, build, context, options);
        var triggered = Run(pack, build, context, options with { DriveSimpleBuffs = true });
        Assert.True(triggered.WeaponDps.Mean > baseline.WeaponDps.Mean);
        Assert.Equal(baseline.ShotsFired, triggered.ShotsFired);
    }

    [Theory]
    [InlineData("", "", 10, true, false, new double[] { 10, 20, 30, 30 })]
    [InlineData("", "shot", 10, true, false, new double[] { 10, 20, 20, 20 })]
    [InlineData("reload", "", 0, false, false, new double[] { 10, 20, 10 })]
    [InlineData("", "", 0, true, true, new double[] { 10, 20, 30, 10 })]
    public void StackEventsDriveLaterWeaponDamageAndRespectResetAndConsumption(string reset, string consume, double duration, bool noReload, bool purge, double[] expected)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 2, Step = 1 }],
            Stats = original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('stacks') * 10" } : s.ID == "equipped_weapon_magazine_size" ? s with { Formula = "2" } : s).Append(new StatDefinition { ID = "weapon_no_reload", Formula = noReload ? "1" : "0" }).DistinctBy(s => s.ID).ToImmutableArray(),
            Talents = [new() { ID = "test_buff", Name = "Stack talent" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "stacks", On = "hit", Kind = "stacks", Duration = duration, Max = 2, ResetOn = reset, ConsumeOn = consume, Purge = purge }] }
        };
        var compilation = PackExpressionCompiler.Compile(rules);
        Assert.True(compilation.Compiled is not null, string.Join("; ", compilation.Diagnostics));
        var pack = compilation.Compiled!;
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = noReload ? 4 : 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));

        var unsupported = rules with { Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { ResetOn = "distance" }] } };
        var unsupportedPack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(unsupported).Compiled);
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => Run(unsupportedPack, build, Context,
            new() { Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void SharedPoolClearsBeforeTheNextShotsDamageAndStartsFreshEachFight()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "red", Name = "Red", Min = 0, Max = 3, Step = 1 }, new() { ID = "blue", Name = "Blue", Min = 0, Max = 3, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "(context_value('red') + context_value('blue')) * 10" } :
                s.ID == "practical_headshot_fraction" ? s with { Formula = "1" } : s), new() { ID = "weapon_no_reload", Formula = "1" }],
            Talents = [new() { ID = "test_buff", Name = "Shared stacks" }],
            Simulation = new()
            {
                Triggers = [
                new() { Talent = "test_buff", Key = "red", On = "hit", Kind = "stacks", Max = 3, Pool = "shared", PoolMax = 3 },
                new() { Talent = "test_buff", Key = "blue", On = "headshot", Kind = "stacks", Max = 3, Pool = "shared", PoolMax = 3 }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 4, Mode = SimulationMode.Random, Iterations = 2, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        // Hit reaches the cap before the same shot's later headshot event starts a new pool.
        Assert.Equal(new double[] { 10, 30, 20, 10 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(17.5, result.WeaponDps.Mean);
    }

    [Theory]
    [InlineData(1, new double[] { 10, 20, 10, 20 })]
    [InlineData(0.25, new double[] { 10, 20, 20, 30 })]
    public void StacksDecayDuringReloadBeforeTheNextShotsDamage(double decay, double[] expected)
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 5, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('stacks') * 10" } :
                s.ID == "equipped_weapon_magazine_size" ? s with { Formula = "2" } : s.ID == "equipped_weapon_reload_time" ? s with { Formula = "6" } : s)],
            Talents = [new() { ID = "test_buff", Name = "Decaying stacks" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "stacks", On = "hit", Kind = "stacks", Max = 5, Duration = 1, Refresh = true, Decay = decay }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(new double[] { 0, 1, 8, 9 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
    }

    [Fact]
    public void AlternateHeadshotGainsIncreaseLaterDamageAndUnsupportedEventsAreRefused()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 4, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "buff_damage" ? s with { Formula = "context_value('stacks') * 10" } :
                s.ID == "practical_headshot_fraction" ? s with { Formula = "1" } : s), new() { ID = "weapon_no_reload", Formula = "1" }],
            Talents = [new() { ID = "test_buff", Name = "Alternate stacks" }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "stacks", On = "body_hit", Kind = "stacks", Max = 4,
                Also = [new() { On = "headshot", Gain = 2 }] }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        var result = Run(pack, build, Context, new() { Duration = 4, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true });
        Assert.Equal(new double[] { 10, 30, 50, 50 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        var unsupported = rules with { Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Also = [new() { On = "shield_break", Gain = 1 }] }] } };
        var unsupportedPack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(unsupported).Compiled);
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => Run(unsupportedPack, build, Context,
            new() { Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true })).Code);
    }

    // Shot damage is capped by the target, so these trigger checks observe a triggered reload time instead:
    // magazine 1 reloads after every shot, from 1 s, for 2 s, or 12 s while the buff is active.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ReloadBuff(string on, params (string Id, string Formula)[] stats)
    {
        var (original, _) = BuffBuild("equipped_weapon_reload_time", on);
        var formulas = stats.ToDictionary(stat => stat.Id, stat => stat.Formula);
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(s => formulas.TryGetValue(s.ID, out var formula) ? s with { Formula = formula } : s),
                .. formulas.Where(pair => original.Pack.Stats.All(s => s.ID != pair.Key)).Select(pair => new StatDefinition { ID = pair.Key, Formula = pair.Value })]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff"));
    }

    private static double FirstReloadCompleted(CompiledPackExpressions pack, BuildSnapshot build, ulong seed = 1) =>
        DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 20, Mode = SimulationMode.Random, Iterations = 1, Seed = seed, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            retargetDelay: 30, cancellationToken: TestContext.Current.CancellationToken)
        .FirstFightEvents.First(e => e.Kind == DummyEventKind.ReloadCompleted).Time;

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 13)]
    public void CriticalKillsFollowTheKillingPelletNotAnyPelletOfTheShot(bool killingPelletCrits, double reloadCompleted)
    {
        // Two 5-damage pellets against 5 health: the first pellet kills, the second hits a dead target.
        var (pack, build) = ReloadBuff("crit_kill", ("equipped_weapon_pellets", "2"), ("effective_critical_hit_chance", "0.5"));
        // Each pellet draws hit, critical and headshot rolls in turn; pick a seed where only the second pellet decides.
        static double[] Rolls(ulong seed) { var random = new SimulationRandom(seed); return [.. Enumerable.Range(0, 6).Select(_ => random.NextDouble())]; }
        var seed = Enumerable.Range(1, 1000).Select(value => (ulong)value).First(seed =>
            Rolls(seed) is var rolls && (rolls[1] < 0.5) == killingPelletCrits && (rolls[4] < 0.5) != killingPelletCrits);
        Assert.Equal(reloadCompleted, FirstReloadCompleted(pack, build, seed));
    }

    [Theory]
    [InlineData("3", 13)]
    [InlineData("20", 3)]
    public void AShotThatStripsTheLastArmorAndReachesHealthIsUnarmored(string armor, double reloadCompleted)
    {
        var (pack, build) = ReloadBuff("unarmored_hit", ("enemy_target_armor", armor), ("enemy_target_health", "100"));
        Assert.Equal(reloadCompleted, FirstReloadCompleted(pack, build));
    }

    [Fact]
    public void PelletHitsOnADeadTargetDoNotCarryIntoTheRetargetGap()
    {
        // Eight 1.25-damage pellets kill a 10-health target; the reload then reads zero prior hits, not eight.
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "prior", Name = "Prior hits", Min = 0, Max = 8, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID switch
            {
                "equipped_weapon_pellets" => s with { Formula = "8" },
                "enemy_target_health" => s with { Formula = "10" },
                "equipped_weapon_reload_time" => s with { Formula = "2 + context_value('prior')" },
                _ => s
            })],
            Talents = [new() { ID = "test_buff", Name = "Prior pellets" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "prior", Kind = "pellets" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        Assert.Equal(3, FirstReloadCompleted(pack, build));
    }

    [Fact]
    public void APelletReadingThatCannotHoldEveryPelletIsRefusedAtSetup()
    {
        var (original, _) = BuffBuild();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [new() { ID = "prior", Name = "Prior hits", Min = 0, Max = 8, Step = 1 }],
            // No pellet ever lands, so only a setup check can refuse this run.
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "equipped_weapon_pellets" ? s with { Formula = "12" } :
                s.ID == "practical_hit_fraction" ? s with { Formula = "0" } : s)],
            Talents = [new() { ID = "test_buff", Name = "Prior pellets" }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "prior", Kind = "pellets" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test_buff");
        Assert.Equal("trigger-reading-out-of-range", Assert.Throws<SimulationException>(() =>
            Run(pack, build, Context, new() { Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void FightDistanceWithoutTriggerDrivenBuffsIsRefused()
    {
        var pack = Tiny();
        Assert.Equal("distance-unsupported", Assert.Throws<SimulationException>(() => Run(pack, Build(pack), Context, new() { Distance = 20 })).Code);
    }

    [Fact]
    public void ZeroTargetPoolsAreRefusedWithACode()
    {
        var pack = Tiny(("enemy_target_armor", 0), ("enemy_target_health", 0), ("out_of_cover_damage_factor", 1), ("damage_to_armor", 0), ("health_damage", 0));
        Assert.Equal("target-invalid", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, Build(pack), Context,
            cancellationToken: TestContext.Current.CancellationToken)).Code);
    }

    private sealed class InlineProgress(Action<SimulationProgress> report) : IProgress<SimulationProgress>
    {
        public List<SimulationProgress> Updates { get; } = [];
        public void Report(SimulationProgress value) { Updates.Add(value); report(value); }
    }
}
