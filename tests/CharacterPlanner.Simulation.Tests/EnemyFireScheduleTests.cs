using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class EnemyFireScheduleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidResolvedAttackerCountsAreRefused(double attackers)
    {
        var resolved = Resolve();
        resolved = resolved with { Assumptions = resolved.Assumptions with { Numeric = resolved.Assumptions.Numeric.SetItem("enemy_attackers", attackers) } };
        Assert.Equal("incoming-stat-invalid", Assert.Throws<SimulationException>(() => EnemyFireSchedule.Create(resolved)).Code);
    }
    private static ResolvedBuild Resolve(double hitRate = 50, double firingShare = 50)
    {
        var pack = new GamePack
        {
            ID = "incoming",
            Name = "Incoming test",
            Version = "1",
            NumericContextInputs = [new() { ID = "enemy_hit_rate", Name = "Hit rate", Min = 0, Max = 100, Step = 1, Default = 50 },
                new() { ID = "enemy_firing_share", Name = "Firing share", Min = 0, Max = 100, Step = 1, Default = 50 }],
            Stats = [new() { ID = "character_armor", Formula = "100" }, new() { ID = "estimated_bonus_armor", Formula = "50" },
                new() { ID = "character_health", Formula = "100" }, new() { ID = "enemy_target_hit_damage", Formula = "40" },
                new() { ID = "enemy_damage_taken_factor", Formula = "0.5" },
                new() { ID = "enemy_target_incoming_dps", Formula = "enemy_target_hit_damage * 4 * context_value('enemy_hit_rate') / 100 * context_value('enemy_firing_share') / 100" }]
        };
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack).Compiled);
        var context = ImmutableDictionary<string, string>.Empty.Add("enemy_hit_rate", hitRate.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("enemy_firing_share", firingShare.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return BuildResolver.Resolve(compiled, BuildSnapshot.Create(pack), context);
    }

    [Fact]
    public void CoreHitRateIsRemovedFromCadenceAndMitigationAppliesOnce()
    {
        var half = EnemyFireSchedule.Create(Resolve());
        var full = EnemyFireSchedule.Create(Resolve(hitRate: 100));
        Assert.Equal(0.5, half.HitProbability);
        Assert.Equal(1, full.HitProbability);
        Assert.Equal(0.5, half.Interval);
        Assert.Equal(half.Interval, full.Interval);
        Assert.Equal(20, half.DamagePerHit);
        Assert.Equal(new double[] { 0.5, 1, 1.5 }, half.ShotTimes(2, TestContext.Current.CancellationToken));
        Assert.Equal(1, EnemyFireSchedule.Create(Resolve(firingShare: 25)).Interval);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(50, 0)]
    public void ZeroIncomingDpsHasNoScheduledAttempts(double hitRate, double firingShare)
    {
        var schedule = EnemyFireSchedule.Create(Resolve(hitRate, firingShare));
        Assert.Null(schedule.Interval);
        Assert.Empty(schedule.ShotTimes(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CoreScheduleAndPlayerPoolsProduceDiscreteBreakAndDownTimes()
    {
        var schedule = EnemyFireSchedule.Create(Resolve(hitRate: 100));
        var state = schedule.CreateState();
        var taken = 0d;
        foreach (var time in schedule.ShotTimes(8, TestContext.Current.CancellationToken))
        {
            taken += state.Apply(schedule.DamagePerHit, time).DamageTaken;
            if (state.Downed) { break; }
        }
        Assert.Equal(4, state.FirstArmorBreak);
        Assert.Equal(6.5, state.DownAt);
        Assert.Equal(250, taken);
        Assert.Equal(50, schedule.CreateState().BonusArmor);
    }

    [Fact]
    public void ZeroDamageFactorPreservesEnemyCadenceAndPlayerPools()
    {
        var resolved = Resolve();
        resolved = resolved with { Calculation = resolved.Calculation with { Stats = resolved.Calculation.Stats.SetItem("enemy_damage_taken_factor", 0) } };
        var schedule = EnemyFireSchedule.Create(resolved);
        var state = schedule.CreateState();
        Assert.Equal(0.5, schedule.Interval);
        Assert.Equal(0, schedule.DamagePerHit);
        foreach (var time in schedule.ShotTimes(2, TestContext.Current.CancellationToken)) { state.Apply(schedule.DamagePerHit, time); }
        Assert.Equal(50, state.BonusArmor);
        Assert.Null(state.DownAt);
    }

    [Fact]
    public void InvalidAndInconsistentCoreOutputsAreRefused()
    {
        var resolved = Resolve();
        ResolvedBuild With(string key, double value) => resolved with { Calculation = resolved.Calculation with { Stats = resolved.Calculation.Stats.SetItem(key, value) } };
        var impossibleRate = resolved with
        {
            Calculation = resolved.Calculation with
            { Stats = resolved.Calculation.Stats.SetItem("enemy_target_hit_damage", double.Epsilon).SetItem("enemy_target_incoming_dps", double.MaxValue) }
        };
        foreach (var invalid in new[] { With("character_health", 0), With("character_armor", -1), With("estimated_bonus_armor", double.NaN),
            With("enemy_target_hit_damage", 0), With("enemy_damage_taken_factor", double.PositiveInfinity), impossibleRate })
        { Assert.Equal("incoming-stat-invalid", Assert.Throws<SimulationException>(() => EnemyFireSchedule.Create(invalid)).Code); }
        Assert.Throws<SimulationException>(() => EnemyFireSchedule.Create(resolved with { Assumptions = resolved.Assumptions with { Numeric = resolved.Assumptions.Numeric.Remove("enemy_hit_rate") } }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDurationIsRefused(double duration) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EnemyFireSchedule.Create(Resolve()).ShotTimes(duration, TestContext.Current.CancellationToken).ToArray());

    [Fact]
    public void AttemptsAreCancellableAndBounded()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EnemyFireSchedule.Create(Resolve()).ShotTimes(2, cancellation.Token).ToArray());
        var dense = Resolve();
        dense = dense with { Calculation = dense.Calculation with { Stats = dense.Calculation.Stats.SetItem("enemy_target_incoming_dps", 40000000) } };
        Assert.Equal("incoming-work-limit", Assert.Throws<SimulationException>(() => EnemyFireSchedule.Create(dense).ShotTimes(1, TestContext.Current.CancellationToken).Count()).Code);
    }

    [Fact]
    public void CommittedEnemyFiringTimeScalesCoreCadence()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack);
        var context = ImmutableDictionary<string, string>.Empty.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var half = BuildResolver.Resolve(pack, build, context);
        var full = BuildResolver.Resolve(pack, build, context.Add("enemy_firing_share", "100"));
        var schedule = EnemyFireSchedule.Create(half);
        Assert.Equal(50, half.Assumptions.Numeric["enemy_firing_share"]);
        Assert.Equal(schedule.Interval / 2, EnemyFireSchedule.Create(full).Interval);
        Assert.Equal(half.Calculation.Stats["enemy_target_hit_damage"] * half.Calculation.Stats["enemy_damage_taken_factor"], schedule.DamagePerHit);
        Assert.Equal(half.Calculation.Stats["character_health"], schedule.CreateState().Health);
        Assert.NotEmpty(schedule.ShotTimes(1, TestContext.Current.CancellationToken));
    }
}
