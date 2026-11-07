using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// Pack-defined burn and bleed ticks (P01). A shot every second arms the status and the next hit applies it, so the
/// first application is at 1 s. Each tick deals the pack's tick stat to the target's pools.
/// </summary>
public sealed class StatusDamageTests
{
    private static ImmutableDictionary<string, string> Context => ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
    private static DummyFightOptions Options => new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true };

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Fixture(SimStatusDamageDefinition[]? damage = null, double duration = 2,
        string status = "burning", string on = "hit", double every = 0, bool applyOnHit = false, params (string Id, string Formula)[] changes)
    {
        var values = new Dictionary<string, string>
        {
            ["equipped_weapon_rpm"] = "60",
            ["equipped_weapon_magazine_size"] = "100",
            ["equipped_weapon_reload_time"] = "2",
            ["equipped_weapon_pellets"] = "1",
            ["practical_hit_fraction"] = "1",
            ["practical_headshot_fraction"] = "0",
            ["effective_critical_hit_chance"] = "0",
            ["equipped_weapon_damage"] = "10",
            ["equipped_weapon_critical_damage"] = "10",
            ["equipped_weapon_headshot_damage"] = "10",
            ["equipped_weapon_critical_headshot_damage"] = "10",
            ["equipped_weapon_practical_shot_damage"] = "10",
            ["enemy_target_armor"] = "0",
            ["enemy_target_health"] = "100000",
            ["out_of_cover_damage_factor"] = "1",
            ["damage_to_armor"] = "0",
            ["health_damage"] = "0",
            ["status_effects"] = "0",
            ["burn_tick"] = "100 * (1 + status_effects)",
            ["bleed_tick"] = "90 * (1 + status_effects)",
            ["character_armor"] = "1000",
            ["estimated_bonus_armor"] = "0",
            ["character_health"] = "1000",
            ["enemy_target_hit_damage"] = "1",
            ["enemy_damage_taken_factor"] = "1",
            ["enemy_target_incoming_dps"] = "enemy_target_hit_damage * context_value('enemy_hit_rate') / 100"
        };
        foreach (var (id, formula) in changes) { values[id] = formula; }
        var rules = new GamePack
        {
            ID = "status-damage",
            Name = "Status damage",
            Version = "1",
            Slots = [new() { ID = "primary_weapon", Name = "Weapon", Weapon = true }],
            Items = [new() { ID = "gun", Name = "Gun", Slot = "primary_weapon", TalentSlots = [new() { ID = "talent", Choices = ["test"] }] }],
            Selectors = [new() { ID = "target_status", Name = "Target status", Values = ["true"] }],
            NumericContextInputs = [new() { ID = "enemy_hit_rate", Name = "Enemy hit rate", Min = 0, Max = 100, Step = 1, Default = 100 }],
            Stats = [.. values.Select(pair => new StatDefinition { ID = pair.Key, Formula = pair.Value })],
            Talents = [new() { ID = "test", Name = "Test" }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test", Key = "target_status", Kind = "status", Status = status, On = on, Duration = on.Length == 0 ? 0 : duration, Every = every, ApplyOnHit = applyOnHit }],
                StatusDamage = [.. damage ?? [new() { Status = "burning", Tick = "burn_tick", Period = .5 }, new() { Status = "bleeding", Tick = "bleed_tick", Period = .5 }]]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static DummyFightResult Run((CompiledPackExpressions Pack, BuildSnapshot Build) fixture, DummyFightOptions? options = null, double delay = 0) =>
        DummyWeaponSimulator.RunAgainstTargets(fixture.Pack, fixture.Build, Context, options ?? Options, delay, TestContext.Current.CancellationToken);

    private static double[] Times(DummyFightResult result, DummyEventKind kind) =>
        [.. result.FirstFightEvents.Where(e => e.Kind == kind).Select(e => e.Time)];

    // Two enemies at once, moving every so often. Shots are a second apart, and a hit burns its enemy for 10 s, from the first
    // shot's arming, with a 100 tick every half second. A 10 s burn outlasts these fights.
    private static DummyFightOptions Moving(double duration, double focus, int enemies = 2) =>
        Options with { Duration = duration, IncludeIncoming = true, EnemiesAtOnce = enemies, FocusSeconds = focus };
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Burning(string health) => Fixture(duration: 10, changes: [("enemy_target_health", health)]);
    private static double[] Hits(DummyFightResult result, DummyEventKind kind) => [.. result.FirstFightEvents.Where(e => e.Kind == kind).Select(e => e.Time)];

    [Fact]
    public void AnEnemyYouLeftKeepsBurning()
    {
        // The player shoots the first enemy at 0, 1 and 2 s, then the second from 3 s. Both burn from there, so ticks double up.
        var result = Run(Burning("1000000"), Moving(8.5, 3), delay: 0);
        Assert.Equal(1, Hits(result, DummyEventKind.BurnTick).Count(time => time == 2));
        Assert.Equal(2, Hits(result, DummyEventKind.BurnTick).Count(time => time == 5));
        Assert.Contains(result.Limitations, note => note.StartsWith("Assumed shooting pattern: 3 s on an enemy", StringComparison.Ordinal));
        // Without the pattern only the enemy being shot burns.
        var focused = Run(Burning("1000000"), Options with { Duration = 8.5, IncludeIncoming = true, EnemiesAtOnce = 2 }, delay: 0);
        Assert.Equal(1, Hits(focused, DummyEventKind.BurnTick).Count(time => time == 5));
    }

    [Fact]
    public void ATickCanKillAnEnemyYouLeftWithoutStoppingYou()
    {
        // 300 health: two 10s and 300 of burn by 2.5 s, with the player on the second enemy from 2 s. Shots carry on, and the player
        // stays, with one more arriving only when they're next due to move.
        var result = Run(Burning("300"), Moving(4.5, 2) with { ClearKills = 1 }, delay: 0);
        Assert.Equal(new[] { 2.5 }, Hits(result, DummyEventKind.Kill).Take(1));
        Assert.Equal(new double[] { 0, 1, 2, 3, 4 }, Hits(result, DummyEventKind.Shot));
        Assert.Equal(2.5, result.TimeToClear!.Mean);
        // Time to kill runs from first reaching the enemy, time away included.
        Assert.Equal(2.5, Run(Burning("300"), Moving(3, 2), delay: 0).TimeToKill!.Mean);
        // A kill away starts no arrival: the player reaches the second at 3 s, and the first dies at 3.5 s.
        var late = Run(Burning("500"), Moving(5, 2), delay: 1);
        Assert.Equal(3.5, Hits(late, DummyEventKind.Kill)[0]);
        Assert.Equal(new double[] { 3 }, Hits(late, DummyEventKind.TargetArrived));
    }

    [Fact]
    public void WithOneEnemyLeftThePlayerStays()
    {
        // Two in all: the first falls to its burn while the player is on the second, and nobody's left to move to.
        var result = Run(Burning("1000000"), Moving(9.5, 2) with { ClearKills = 2, EnemiesShrink = true }, delay: 0);
        Assert.NotEmpty(Hits(result, DummyEventKind.TargetArrived));
        // 480 health: the first dies at 3.5 s, and the second lasts past 4 s, when the clock runs out with nobody to go to.
        var run = Run(Burning("480"), Moving(4.25, 2) with { ClearKills = 2, EnemiesShrink = true }, delay: 0);
        Assert.Equal(3.5, Hits(run, DummyEventKind.Kill)[0]);
        Assert.DoesNotContain(Hits(run, DummyEventKind.TargetArrived), time => time > 2);
    }

    [Fact]
    public void TheEnemyYouWereMovingToDying()
    {
        // 900 health: the first burns from 1 s and dies at 5.5 s, while the player is moving back to it from the second. They carry on
        // to who's next.
        var result = Run(Burning("900"), Moving(8.5, 1.5), delay: 1);
        Assert.Equal(5.5, Hits(result, DummyEventKind.Kill)[0]);
        Assert.Contains(Hits(result, DummyEventKind.Shot), time => time >= 6);
    }

    [Fact]
    public void AKillBehindYouDoesntChangeWhereYoureGoing()
    {
        // Three enemies: the player leaves the second at 5 s for the third, and the first dies behind them at 5.5 s. They still reach the
        // third, which burns from 6 s, so ticks at 7.5 s come from the second and third.
        var result = Run(Burning("900"), Moving(8, 1.5, enemies: 3), delay: 1);
        Assert.Equal(5.5, Hits(result, DummyEventKind.Kill)[0]);
        Assert.Equal(2, Hits(result, DummyEventKind.BurnTick).Count(time => time == 7.5));
    }

    // A burn rule plus a second rule, with a talent adding its key's value to each shot, against enemies that don't die.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Counting(params SimTriggerDefinition[] more)
    {
        var (original, _) = Fixture(duration: 10, changes: [("enemy_target_health", "1000000"), ("status_effects", "0"), ("buff_damage", "0"), ("equipped_weapon_damage", "10 + buff_damage"),
            ("equipped_weapon_critical_damage", "10 + buff_damage"), ("equipped_weapon_headshot_damage", "10 + buff_damage"), ("equipped_weapon_critical_headshot_damage", "10 + buff_damage"), ("equipped_weapon_practical_shot_damage", "10 + buff_damage")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "burning_now", Name = "Burning", Min = 0, Max = 5, Step = 1 }],
            Talents = [original.Pack.Talents[0] with { Modifiers = [new() { Stat = "buff_damage", Op = ModifierOps.Flat, Value = new("context_value('burning_now')") }] }],
            Simulation = original.Pack.Simulation with { Triggers = [.. original.Pack.Simulation.Triggers, .. more] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static double[] ShotDamage(DummyFightResult result) => [.. result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage)];

    [Fact]
    public void ACountOfAStatusReadsEveryEnemyItsOn()
    {
        // Read on each whole second, just before that second's shot: the first enemy burns from 1 s, so from 2 s one counts, and
        // the second burns from 3 s, so from 4 s two do.
        var fixture = Counting(new SimTriggerDefinition { Talent = "test", Key = "burning_now", Kind = "count", On = "second", Status = "burning", Max = 5 });
        Assert.Equal(new double[] { 10, 10, 11, 11, 12, 12 }, ShotDamage(Run(fixture, Moving(5.5, 3), delay: 0)));
    }

    [Fact]
    public void ACountOfAStatusReadsOnlyThoseInRange()
    {
        // The first enemy is at 10 m and the second at 40 m, so only the first counts within 15 m.
        var fixture = Counting(new SimTriggerDefinition { Talent = "test", Key = "burning_now", Kind = "count", On = "second", Status = "burning", Max = 5, Within = 15 });
        Assert.Equal(new double[] { 10, 10, 11, 11, 11, 11 }, ShotDamage(Run(fixture, Moving(5.5, 3) with { TargetDistances = [10, 40] }, delay: 0)));
    }

    [Fact]
    public void ACountTakesOverFromItsTalentsReadingInAGroup()
    {
        // One talent has both: its reading of the enemy shot, then in a group the count of all of them.
        SimTriggerDefinition[] rules = [new() { Talent = "test", Key = "burning_now", Kind = "status", Status = "burning" },
            new() { Talent = "test", Key = "burning_now", Kind = "count", On = "second", Status = "burning", Max = 5 }];
        Assert.Equal(new double[] { 10, 10, 11, 11, 12, 12 }, ShotDamage(Run(Counting(rules), Moving(5.5, 3), delay: 0)));
        // One at a time the reading runs as it always has, and there's no note about a count.
        var alone = Run(Counting(rules), Options with { Duration = 5.5 }, delay: 0);
        Assert.Equal(11, ShotDamage(alone)[3]);
        Assert.DoesNotContain(alone.Limitations, note => note.StartsWith("With one enemy at a time these stay as set", StringComparison.Ordinal));
    }

    [Fact]
    public void AMoveFiresTargetChanged()
    {
        // Stacks cleared by a move: one a hit from the first shot, so 0, 1, 2 on the first enemy, and none on the second.
        var fixture = Counting(new SimTriggerDefinition { Talent = "test", Key = "burning_now", Kind = "stacks", On = "hit", Max = 5, ResetOn = "target_changed" });
        Assert.Equal(new double[] { 10, 11, 12, 10, 11 }, ShotDamage(Run(fixture, Moving(4.5, 3), delay: 0)));
        // Fights that never move don't need the option for such a rule, as with a swap event and no swaps.
        Assert.Equal(new double[] { 10, 11, 12, 13 }, ShotDamage(Run(fixture, Options with { Duration = 3.5 }, delay: 0)));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) InRange(double within) =>
        WithRange(Fixture(duration: 10, changes: [("enemy_target_health", "1000000")]).Pack, within);

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) WithRange(CompiledPackExpressions original, double within)
    {
        var rules = original.Pack with { Simulation = original.Pack.Simulation with { Triggers = [original.Pack.Simulation.Triggers[0] with { Within = within }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    [Fact]
    public void AStatusRuleReachesOnlyItsRange()
    {
        Assert.NotEmpty(Hits(Run(InRange(15), Options with { Distance = 10 }), DummyEventKind.BurnTick));
        Assert.Empty(Hits(Run(InRange(15), Options with { Distance = 20 }), DummyEventKind.BurnTick));
        // With two enemies at their own distances, only the near one burns.
        var mixed = Run(InRange(15), Moving(5.5, 3) with { TargetDistances = [10, 40] }, delay: 0);
        Assert.Equal(1, Hits(mixed, DummyEventKind.BurnTick).Count(time => time == 2));
        Assert.Equal(1, Hits(mixed, DummyEventKind.BurnTick).Count(time => time == 5));
    }

    [Fact]
    public void AWholeSecondsStatusReachesOnlyItsRange()
    {
        var far = Run(WithRange(Fixture(on: "second", changes: [("enemy_target_health", "1000000")]).Pack, 15), Options with { Distance = 20 });
        Assert.Empty(Hits(far, DummyEventKind.BurnTick));
    }

    [Fact]
    public void AKillingHitReadsTheRangeOfTheEnemyItKilled()
    {
        // Enemies at 40 m and 10 m, four shots each, and a rule needing three hits in range to arm. The far enemy's killing hit doesn't
        // count, though the near enemy is next. So the third count is the near enemy's third hit, and the hit it arms is its last, with no tick.
        var (original, _) = Fixture(duration: 10, changes: [("enemy_target_health", "40")]);
        var rules = original.Pack with { Simulation = original.Pack.Simulation with { Triggers = [original.Pack.Simulation.Triggers[0] with { Within = 15, Count = 3 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        Assert.Empty(Hits(Run((pack, build), Options with { Duration = 7.5, TargetDistances = [40, 10] }, delay: 0), DummyEventKind.BurnTick));
    }

    [Fact]
    public void WithNoDistanceAStatusRulesRangeIsMet()
    {
        var result = Run(InRange(15));
        Assert.NotEmpty(Hits(result, DummyEventKind.BurnTick));
        Assert.Contains("With no fight distance, a status rule's range is taken as met", result.Limitations);
    }

    [Fact]
    public void ARuleMayResetOnTwoEvents()
    {
        // Stacks that clear on a kill and on a move: built on the first enemy, gone on the second.
        var fixture = Counting(new SimTriggerDefinition { Talent = "test", Key = "burning_now", Kind = "stacks", On = "hit", Max = 5, ResetOn = "target_death", AlsoResetOn = "target_changed" });
        Assert.Equal(new double[] { 10, 11, 12, 10, 11 }, ShotDamage(Run(fixture, Moving(4.5, 3), delay: 0)));
        // Without moves the rule runs, and the second event just never fires.
        Assert.Equal(new double[] { 10, 11, 12, 13 }, ShotDamage(Run(fixture, Options with { Duration = 3.5 }, delay: 0)));
    }

    [Fact]
    public void AStatusRuleCanArmOnTargetChanged()
    {
        // Nothing burns until the first move, at 2 s, arms the rule. The shot at 2 s comes with it, so the hit at 3 s lands the burn.
        var fixture = Fixture(duration: 10, on: "target_changed", changes: [("enemy_target_health", "1000000")]);
        Assert.Equal(3.5, Hits(Run(fixture, Moving(4.5, 2), delay: 0), DummyEventKind.BurnTick)[0]);
    }

    [Fact]
    public void ACountReadOnAKillBehindYouFindsTheEnemyBeingShot()
    {
        // A kill on an enemy left behind, with the second enemy burning under fire. Read on that kill, the count is the one left burning.
        var fixture = Counting(new SimTriggerDefinition { Talent = "test", Key = "burning_now", Kind = "count", On = "kill", Status = "burning", Max = 5 });
        // A status kill reaches gear talents only, so the talent sits on a chest piece.
        var rules = fixture.Pack.Pack with
        {
            Stats = [.. fixture.Pack.Pack.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "900" } : stat)],
            Slots = [.. fixture.Pack.Pack.Slots, new() { ID = "chest", Name = "Chest" }],
            Items = [fixture.Pack.Pack.Items[0] with { TalentSlots = [] }, new() { ID = "vest", Name = "Vest", Slot = "chest", TalentSlots = fixture.Pack.Pack.Items[0].TalentSlots }]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("chest", "vest").WithTalentSelection("chest", "talent", "test");
        // The first burns from 1 s and dies at 5.5 s, with the player on the second from 4 s. Its shots after that add 1.
        var result = Run((pack, build), Moving(7.5, 3.5), delay: 0);
        Assert.Equal(5.5, Hits(result, DummyEventKind.Kill)[0]);
        Assert.Equal(11, ShotDamage(result)[^1]);
    }

    [Fact]
    public void GainingOnTargetChangedNeedsMoves()
    {
        var fixture = Counting(new SimTriggerDefinition { Talent = "test", Key = "burning_now", Kind = "stacks", On = "target_changed", Max = 5 });
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { Duration = 3 })).Code);
        // The move at 1 s comes before that second's shot, and the move at 2 s before the next.
        Assert.Equal(new double[] { 10, 11, 12 }, ShotDamage(Run(fixture, Moving(2.5, 1), delay: 0)));
    }

    [Fact]
    public void AMoveWaitsForAReloadToFinish()
    {
        // Two rounds a magazine and a 2 s reload: shots at 0 and 1, reloading until 3 or so. The clock runs out at 2, and the move follows it.
        var result = Run(Fixture(duration: 10, changes: [("enemy_target_health", "1000000"), ("equipped_weapon_magazine_size", "2")]), Moving(5, 2), delay: 1);
        var reloaded = Hits(result, DummyEventKind.ReloadCompleted)[0];
        Assert.All(Hits(result, DummyEventKind.TargetArrived), time => Assert.True(time > reloaded, $"{time} against {reloaded}"));
    }

    [Fact]
    public void AMoveWaitsBehindCoverForThePeek()
    {
        // 3 s peeking and 2 s behind cover, with a 1 s reload there. Its end at 4 s is a decision after the clock has run out at
        // 3.5 s, but the move waits for the peek at 5 s.
        var fixture = Fixture(duration: 10, changes: [("enemy_target_health", "1000000"), ("equipped_weapon_reload_time", "1")]);
        var result = Run(fixture, Moving(7.5, 3.5) with { PeekSeconds = 3, CoverSeconds = 2, PeekHitShare = 0.5 }, delay: 0);
        Assert.Equal(5, Hits(result, DummyEventKind.TargetArrived)[0]);
    }

    [Fact]
    public void FocusTimeNeedsAGroup()
    {
        Assert.Equal("focus-unsupported", Assert.Throws<SimulationException>(() => Run(Burning("1000"), Options with { FocusSeconds = 2, IncludeIncoming = true })).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Burning("1000"), Moving(5, 0.05)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Burning("1000"), Moving(5, 61)));
    }

    [Fact]
    public void LeavingAnEnemySettlesItsUptime()
    {
        // The burn on the first enemy runs to 3 s, and the player leaves at 2 s for one with no burn: 1 s counted, and none after.
        var (pack, _) = Fixture();
        var statuses = new StatusTriggerState(pack, pack.Pack.Simulation.Triggers);
        var (first, second) = (new TargetStatuses(), new TargetStatuses());
        statuses.Hold(first);
        statuses.Fire("hit", 0);
        statuses.Fire("hit", 1);
        statuses.Hold(second, 2);
        Assert.Equal(1, statuses.UptimeSeconds(5)["burning"]);
        // Back at 4 s, with its burn run out, nothing is counted for the time away.
        statuses.Hold(first, 4);
        Assert.Equal(1, statuses.UptimeSeconds(5)["burning"]);
    }

    [Fact]
    public void ADeathBehindYouLeavesTheStacksOnTheEnemyYoureShooting()
    {
        // Stacks that clear on target_death: the first enemy's burn kills it at 2.5 s, with the player building stacks on the second.
        SimTriggerDefinition[] rules = [new() { Talent = "test", Key = "burns", On = "hit", Kind = "stacks", Max = 5, ResetOn = "target_death" }];
        var state = new TimedBuffState(rules);
        var (shot, dying) = (new EnemyMarks(), new EnemyMarks());
        state.HoldEnemy(shot, 0);
        state.Fire("hit", 1);
        state.HoldEnemy(dying, 2);
        state.Fire("target_death", 2);
        state.HoldEnemy(shot, 2);
        Assert.Equal("1", state.Context(ImmutableDictionary<string, string>.Empty, 2)["burns"]);
    }

    [Fact]
    public void RulesClearedByADeathStayWithTheirEnemy()
    {
        // A mark and a stack count hold on the enemy that took them, and the player's own stacks don't move.
        SimTriggerDefinition[] rules = [
            new() { Talent = "test", Key = "mark", On = "hit", Kind = "buff", Duration = 100, ConsumeOn = "target_death" },
            new() { Talent = "test", Key = "burns", On = "hit", Kind = "stacks", Max = 5, ConsumeOn = "target_death" },
            new() { Talent = "test", Key = "mine", On = "hit", Kind = "stacks", Max = 5 }];
        var state = new TimedBuffState(rules);
        var (first, second) = (new EnemyMarks(), new EnemyMarks());
        state.HoldEnemy(first, 0);
        state.Fire("hit", 1);
        state.Fire("hit", 2);
        ImmutableDictionary<string, string> Read(double time) => state.Context(ImmutableDictionary<string, string>.Empty, time);
        Assert.Equal(("true", "2", "2"), (Read(2)["mark"], Read(2)["burns"], Read(2)["mine"]));
        state.HoldEnemy(second, 3);
        Assert.False(Read(3).ContainsKey("mark"));
        Assert.Equal(("0", "2"), (Read(3)["burns"], Read(3)["mine"]));
        state.Fire("hit", 4);
        state.HoldEnemy(first, 5);
        Assert.Equal(("true", "2", "3"), (Read(5)["mark"], Read(5)["burns"], Read(5)["mine"]));
        // A death clears only the enemy held.
        state.Fire("target_death", 6);
        state.HoldEnemy(second, 6);
        Assert.Equal(("true", "1"), (Read(6)["mark"], Read(6)["burns"]));
    }

    [Fact]
    public void ClearedStacksDontComeBack()
    {
        // A reload empties what the enemy held. Leaving with nothing live must still save that, or a later visit restores the old stacks.
        SimTriggerDefinition[] rules = [new() { Talent = "test", Key = "burns", On = "hit", Kind = "stacks", Max = 5, ResetOn = "reload", ConsumeOn = "target_death" }];
        var state = new TimedBuffState(rules);
        var (first, between) = (new EnemyMarks(), new EnemyMarks());
        state.HoldEnemy(first, 0);
        state.Fire("hit", 1);
        state.HoldEnemy(between, 2);
        state.HoldEnemy(first, 3);
        state.Fire("reload", 4);
        state.HoldEnemy(between, 5);
        state.HoldEnemy(first, 6);
        Assert.Equal("0", state.Context(ImmutableDictionary<string, string>.Empty, 6)["burns"]);
    }

    [Fact]
    public void AReadyRulesCooldownStaysWithThePlayer()
    {
        SimTriggerDefinition[] rules = [new() { Talent = "test", Key = "armed", On = "hit", Kind = "ready", Duration = 30, ConsumeOn = "target_death" }];
        var state = new TimedBuffState(rules);
        state.Fire("hit", 1);
        var before = state.Context(ImmutableDictionary<string, string>.Empty, 2).ContainsKey("armed");
        state.HoldEnemy(new EnemyMarks(), 2);
        Assert.Equal(before, state.Context(ImmutableDictionary<string, string>.Empty, 2).ContainsKey("armed"));
    }

    [Fact]
    public void EachEnemyKeepsItsOwnStatuses()
    {
        // ADR 0011: statuses sit on the enemy, and the state reads whichever it holds. Arming belongs to the player.
        var (pack, _) = Fixture();
        var statuses = new StatusTriggerState(pack, pack.Pack.Simulation.Triggers);
        var (first, second) = (new TargetStatuses(), new TargetStatuses());
        statuses.Hold(first);
        statuses.Fire("hit", 1);
        statuses.Fire("hit", 2);
        Assert.Contains("burning", statuses.Active(2));
        statuses.Hold(second);
        Assert.DoesNotContain("burning", statuses.Active(2));
        statuses.Hold(first);
        Assert.Contains("burning", statuses.Active(2));
        // A rule armed on one enemy applies on the next hit, whoever that lands on.
        var (armed, next) = (new TargetStatuses(), new TargetStatuses());
        var player = new StatusTriggerState(pack, pack.Pack.Simulation.Triggers);
        player.Hold(armed);
        player.Fire("hit", 1);
        player.Hold(next);
        player.Fire("hit", 2);
        Assert.Contains("burning", player.Active(2));
        player.Hold(armed);
        Assert.DoesNotContain("burning", player.Active(2));
    }

    [Theory]
    [InlineData("1000000", 150)]
    [InlineData("0", 100)]
    public void AnArmorBonusReachesArmorAndNotHealth(string armor, double damage)
    {
        var burn = new SimStatusDamageDefinition { Status = "burning", Tick = "burn_tick", Period = .5, ArmorBonus = "armor_bonus" };
        var result = Run(Fixture(damage: [burn], changes: [("armor_bonus", "0.5"), ("enemy_target_armor", armor), ("enemy_target_health", "1000000")]));
        Assert.Equal(damage, result.FirstFightEvents.First(e => e.Kind == DummyEventKind.BurnTick).Damage);
        Assert.Contains(result.Limitations, note => note.Contains("with the pack's armor bonus on armor and no health factor", StringComparison.Ordinal));
    }

    [Fact]
    public void ATickCrossingArmorTakesTheBonusOnlyOnItsArmorPart()
    {
        // Two 10 shots leave 180 armor. The first tick takes 150. The next finds 30, which costs 20 of its 100 at 50% more, and the
        // other 80 lands on health at no bonus: 110, where a bonus on health would make it 150.
        var burn = new SimStatusDamageDefinition { Status = "burning", Tick = "burn_tick", Period = .5, ArmorBonus = "armor_bonus" };
        var result = Run(Fixture(damage: [burn], changes: [("armor_bonus", "0.5"), ("damage_to_armor", "0"), ("enemy_target_armor", "200"), ("enemy_target_health", "1000000")]));
        var ticks = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).Select(e => e.Damage).ToArray();
        Assert.Equal(new[] { 150d, 110, 100 }, ticks.Take(3).Select(tick => Math.Round(tick, 2)));
    }

    [Fact]
    public void BurnTicksEveryPeriodWhileHitsKeepRefreshingIt()
    {
        var result = Run(Fixture());
        // Applied at 1 s and refreshed by every later hit, so it ticks from 1.5 s to the horizon.
        Assert.Equal([1.5, 2, 2.5, 3, 3.5, 4, 4.5], Times(result, DummyEventKind.BurnTick));
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick), e => Assert.Equal(100, e.Damage));
        Assert.Equal(140, result.StatusDps!.Mean);
        Assert.Equal(10, result.WeaponDps.Mean);
        Assert.Equal(150, result.TotalDps!.Mean);
        Assert.StartsWith("d2p-simple-buffs-v61/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusOnTheSecondLandsWithoutAShotAndWaitsOutItsCooldown()
    {
        // Dragon's Glare's shape (R36): a 2 s burn applied on whole seconds, at most every 3 s. Hits never apply it.
        var result = Run(Fixture(on: "second", every: 3), Options with { Duration = 10 });
        Assert.Equal([1.5, 2, 2.5, 4.5, 5, 5.5, 7.5, 8, 8.5], Times(result, DummyEventKind.BurnTick));
        Assert.Equal(6, result.TargetStatusUptime["burning"].Seconds);
    }

    [Fact]
    public void AnApplyOnHitStatusLandsOnTheTriggeringHit()
    {
        // Trauma's shape (R38): the hit at 0 s applies a 2 s burn itself, at most every 3 s, rather than arming the next hit.
        var result = Run(Fixture(every: 3, applyOnHit: true), Options with { Duration = 10 });
        Assert.Equal([0.5, 1, 1.5, 3.5, 4, 4.5, 6.5, 7, 7.5, 9.5], Times(result, DummyEventKind.BurnTick));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGatedBurnLandsOnlyFromAFullMagazineSizedMeter(bool decay)
    {
        // Ardent's shape (R39): a shot a second from a 10-round magazine; the meter holds half of it (5) and empties when a
        // reload completes. Bullets 6 to 10 of each magazine hit from a full meter and burn for 1 s; the 5th, which fills it,
        // doesn't. With the source's drain (lose stacks 0.5 s after the last shot) a shot a second never fills it at all.
        var fixture = Fixture(changes: [("equipped_weapon_magazine_size", "10")]);
        var rules = fixture.Pack.Pack with
        {
            NumericContextInputs = [.. fixture.Pack.Pack.NumericContextInputs, new() { ID = "heat", Name = "Heat", Min = 0, Max = 100, Step = 1 }],
            Simulation = fixture.Pack.Pack.Simulation with
            {
                Triggers =
                [
                    new() { Talent = "test", Key = "heat", On = "shot", Kind = "stacks", Max = 100, MaxMagazine = 0.5, ResetOn = "reload_end",
                        Duration = decay ? 0.5 : 0, Refresh = decay, Decay = decay ? 12 : 0 },
                    new() { Talent = "test", Key = "target_status", On = "hit", Kind = "status", Status = "burning", Duration = 1, ApplyOnHit = true, WhileKey = "heat", WhileFull = true }
                ]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, fixture.Build), Options with { Duration = 30 });
        var shots = Times(result, DummyEventKind.Shot);
        var reloaded = Times(result, DummyEventKind.ReloadCompleted);
        Assert.NotEmpty(reloaded);
        var burns = Times(result, DummyEventKind.BurnTick);
        if (decay) { Assert.Empty(burns); return; }
        // Each burning bullet's 1 s burn ticks once, half a second later; the next magazine's first shot lands as the reload completes.
        var magazines = new[] { shots.Where(at => at < reloaded[0]).ToArray(), shots.Where(at => at >= reloaded[0] && (reloaded.Length < 2 || at < reloaded[1])).ToArray() };
        var expected = magazines.SelectMany(magazine => magazine.Skip(5)).Select(at => at + 0.5).Where(at => at < 30).ToArray();
        Assert.Equal(expected, burns.Where(at => at <= expected[^1]).ToArray());
        Assert.StartsWith("d2p-simple-buffs-v61/", result.Contract, StringComparison.Ordinal);
    }

    // Toxic Delivery's shape (R38): a status going on starts a debuff that spreads a total (200 here) over 0.5 s ticks for 2 s; while
    // it runs, further applications neither restart nor stack it, which StatusTriggerStateTests pins directly.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) TalentDamageFixture(double total = 200)
    {
        var (pack, build) = Fixture();
        var rules = pack.Pack with
        {
            Stats = [.. pack.Pack.Stats, new() { ID = "talent_total", Formula = total.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
            Simulation = pack.Pack.Simulation with { TalentDamage = [new() { Talent = "test", On = "status_applied", Total = "talent_total", Duration = 2, Period = 0.5 }] }
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Fact]
    public void ATalentDebuffSpreadsItsTotalAndARefreshStartsNoOther()
    {
        // The burn lands at 1 s, and the debuff from it ticks 50 at 1.5–3 s. Every hit after that only refreshes the running burn,
        // which isn't an application (R59), so nothing starts another.
        var result = Run(TalentDamageFixture(), Options with { Duration = 8 });
        Assert.Equal([1.5, 2, 2.5, 3], Times(result, DummyEventKind.TalentTick));
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.TalentTick), e => Assert.Equal(50, e.Damage));
        // Talent debuff DPS is its own figure: in the total, not in burn and bleed DPS.
        Assert.Equal(4 * 50 / 8.0, result.TalentDps!.Mean);
        Assert.Equal(result.WeaponDps.Mean + result.StatusDps!.Mean + result.TalentDps.Mean, result.TotalDps!.Mean, 9);
        Assert.Contains(result.Limitations, line => line.Contains("Talent debuffs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStatusThatSpendsABonusReadsItAndARefreshDoesnt()
    {
        // Perpetuation's shape: the shot at 0 gains +75% Status Effects, and the burn that goes on at 1 s spends it.
        var result = Run(BonusFixture(new() { On = "shot", Kind = "buff", ConsumeOn = "status_applied", Every = 100 }), Options with { Duration = 5 });
        // The burn stays up under steady fire, so every tick keeps the boosted amount.
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick), e => Assert.Equal(175, e.Damage));
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.BurnTick);
        Assert.Equal(1, result.TimedTriggerUptime["bonus"].Seconds, 9);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) BonusFixture(SimTriggerDefinition bonus, params (string Id, string Formula)[] changes)
    {
        var (pack, build) = Fixture(duration: 2, changes: changes);
        var rules = pack.Pack with
        {
            Selectors = [.. pack.Pack.Selectors, new() { ID = "bonus", Name = "Bonus", Values = ["true"] }],
            Talents = [pack.Pack.Talents[0] with { Modifiers = [new() { Stat = "status_effects", Op = ModifierOps.Flat, Value = new(0.75), RequiresContext = ImmutableDictionary<string, string>.Empty.Add("bonus", "true") }] }],
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers, bonus with { Talent = "test", Key = "bonus" }] }
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Fact]
    public void ABonusTheSameBulletEarnedWaitsForTheNextStatus()
    {
        // The second hit, at 1 s, both lands the burn and earns the bonus. The burn doesn't read it, and doesn't spend it.
        var result = Run(BonusFixture(new() { On = "hit", Kind = "buff", Count = 2, ConsumeOn = "status_applied", Every = 100 }), Options with { Duration = 5 });
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick), e => Assert.Equal(100, e.Damage));
        Assert.Equal(4, result.TimedTriggerUptime["bonus"].Seconds, 9);
    }

    [Fact]
    public void AStatusOnTheKillingBulletDiesWithItsEnemy()
    {
        // The second shot kills as it lands the burn, so nothing reacts.
        var result = Run(BonusFixture(new() { On = "status_applied", Kind = "buff", Duration = 1 }, ("enemy_target_health", "20")), Options with { Duration = 1.5 });
        Assert.Equal(1, result.Kills);
        Assert.Equal(0, result.TimedTriggerUptime.GetValueOrDefault("bonus")?.Seconds ?? 0);
    }

    [Fact]
    public void WithoutATalentDamageRuleNothingChanges()
    {
        var plain = Run(Fixture(), Options with { Duration = 8 });
        Assert.Null(plain.TalentDps);
        Assert.DoesNotContain(plain.FirstFightEvents, e => e.Kind == DummyEventKind.TalentTick);
    }

    [Fact]
    public void TicksStopStrictlyBeforeTheDeadline()
    {
        // Two rounds, then a reload past the horizon: applied once at 1 s, burning until 3 s.
        var result = Run(Fixture(changes: [("equipped_weapon_magazine_size", "2"), ("equipped_weapon_reload_time", "10")]));
        Assert.Equal([1.5, 2, 2.5], Times(result, DummyEventKind.BurnTick));
    }

    [Fact]
    public void TickOnApplyAddsATickAtEachNewApplication()
    {
        var damage = new SimStatusDamageDefinition[] { new() { Status = "burning", Tick = "burn_tick", Period = .5, TickOnApply = true } };
        var result = Run(Fixture(damage, changes: [("equipped_weapon_magazine_size", "2"), ("equipped_weapon_reload_time", "10")]));
        Assert.Equal([1, 1.5, 2, 2.5], Times(result, DummyEventKind.BurnTick));
    }

    [Fact]
    public void StatusEffectsRaiseTheTickAndLengthenTheStatus()
    {
        var result = Run(Fixture(changes: [("equipped_weapon_magazine_size", "2"), ("equipped_weapon_reload_time", "10"), ("status_effects", "0.5")]));
        // 2 s x 1.5 = 3 s from 1 s; each tick is 100 x 1.5.
        Assert.Equal([1.5, 2, 2.5, 3, 3.5], Times(result, DummyEventKind.BurnTick));
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick), e => Assert.Equal(150, e.Damage));
    }

    [Fact]
    public void BleedUsesItsOwnTickAndEventKind()
    {
        var result = Run(Fixture(status: "bleeding", changes: [("equipped_weapon_magazine_size", "2"), ("equipped_weapon_reload_time", "10")]));
        Assert.Empty(Times(result, DummyEventKind.BurnTick));
        Assert.Equal([1.5, 2, 2.5], Times(result, DummyEventKind.BleedTick));
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BleedTick), e => Assert.Equal(90, e.Damage));
    }

    [Fact]
    public void ATickCanKillAndTheNextTargetStartsClean()
    {
        // 30 health: two shots leave 10, so the first tick kills and deals only what was left.
        var result = Run(Fixture(changes: [("enemy_target_health", "30")]), Options with { Duration = 2.2 });
        var tick = Assert.Single(result.FirstFightEvents, e => e.Kind == DummyEventKind.BurnTick && e.Time == 1.5);
        Assert.Equal(10, tick.Damage);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.Kill && e.Time == 1.5);
        // The new target is not burning: its first hit at 2 s only arms the status.
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.BurnTick && e.Time > 1.5);
        Assert.Equal(1, result.Kills);
    }

    [Fact]
    public void NoTicksWithoutPackStatusDamageOrAnApplyingTalent()
    {
        var none = Run(Fixture([]));
        Assert.Null(none.StatusDps);
        Assert.Empty(Times(none, DummyEventKind.BurnTick));
        Assert.Contains(none.Limitations, text => text.StartsWith("Status damage over time is excluded", StringComparison.Ordinal));
        // An observer rule reads a status but never applies one.
        var observer = Run(Fixture(on: ""));
        Assert.Null(observer.StatusDps);
        Assert.Empty(Times(observer, DummyEventKind.BurnTick));
    }

    [Fact]
    public void TicksLeaveWeaponRollsAndShotsUnchanged()
    {
        var options = Options with { Iterations = 4, Seed = 7 };
        var ticking = Run(Fixture(changes: [("practical_hit_fraction", "0.6"), ("effective_critical_hit_chance", "0.3")]), options);
        var plain = Run(Fixture([], changes: [("practical_hit_fraction", "0.6"), ("effective_critical_hit_chance", "0.3")]), options);
        Assert.Equal(plain.WeaponDps, ticking.WeaponDps);
        Assert.Equal(plain.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), ticking.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot));
        Assert.Equal(ticking.StatusDps, Run(Fixture(changes: [("practical_hit_fraction", "0.6"), ("effective_critical_hit_chance", "0.3")]), options).StatusDps);
    }

    [Fact]
    public void EnemyAttemptsWinTiesWithTicksInIncomingFights()
    {
        var result = Run(Fixture(), Options with { IncludeIncoming = true });
        Assert.Equal([1.5, 2, 2.5, 3, 3.5, 4, 4.5], Times(result, DummyEventKind.BurnTick));
        var atTwo = result.FirstFightEvents.Where(e => e.Time == 2).Select(e => e.Kind).ToArray();
        Assert.True(Array.IndexOf(atTwo, DummyEventKind.EnemyShot) < Array.IndexOf(atTwo, DummyEventKind.BurnTick));
        Assert.True(Array.IndexOf(atTwo, DummyEventKind.BurnTick) < Array.IndexOf(atTwo, DummyEventKind.Shot));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefreshAfterTheLastScheduledTickKeepsTheCadence()
    {
        // A 1.5 s period on a 2 s burn refreshed every second: the 4 s tick lies past the deadline known after the
        // 2.5 s tick, but the hit at 3 s extends the burn, so ticks continue every 1.5 s.
        var damage = new SimStatusDamageDefinition[] { new() { Status = "burning", Tick = "burn_tick", Period = 1.5 } };
        var result = Run(Fixture(damage), Options with { Duration = 7 });
        Assert.Equal([2.5, 4, 5.5], Times(result, DummyEventKind.BurnTick));
    }

    [Fact]
    public void APeriodLongerThanTheDurationStillTicksWhileRefreshed()
    {
        // A 2 s period on a 1.5 s bleed refreshed every second: it never lapses, so it ticks every 2 s.
        var damage = new SimStatusDamageDefinition[] { new() { Status = "bleeding", Tick = "bleed_tick", Period = 2 } };
        var result = Run(Fixture(damage, duration: 1.5, status: "bleeding"), Options with { Duration = 6 });
        Assert.Equal([3, 5], Times(result, DummyEventKind.BleedTick));
    }

    [Fact]
    public void AStatusThatLapsedStartsANewSchedule()
    {
        // Shots at 0-1 s, then a 2.5 s reload: burning from 1 s to 3 s, lapsed until the hit at 4.5 s reapplies it.
        var result = Run(Fixture(changes: [("equipped_weapon_magazine_size", "2"), ("equipped_weapon_reload_time", "2.5")]), Options with { Duration = 7 });
        var ticks = Times(result, DummyEventKind.BurnTick);
        Assert.Equal([1.5, 2, 2.5], ticks.Where(at => at < 4.5));
        Assert.Equal(5, ticks.First(at => at >= 4.5));
    }

    [Fact]
    public void AStatusKillGrantsOnlyGenericKillRecovery()
    {
        // 30 health dies to the 1.5 s tick. The enemy hit at 1 s took 10 armor; generic armor-on-kill (3) applies,
        // weapon repair-per-kill (4) does not, and the build is not refused for having it.
        var fixture = Fixture(changes: [("enemy_target_health", "30"), ("enemy_target_hit_damage", "10"),
            ("estimated_armor_on_kill", "3"), ("estimated_armor_repair_per_kill", "4")]);
        var result = Run(fixture, Options with { Duration = 1.6, IncludeIncoming = true });
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.Kill && e.Time == 1.5);
        Assert.Equal(3, result.Incoming!.ArmorRepaired);
        Assert.Contains(result.Limitations, text => text.Contains("target_death rules (Doctor Home's kit), not weapon repair-per-kill or weapon talents' kill rules (Preservation)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5, 103)]
    [InlineData(0.25, 3)]
    public void APackRuleMarkDropsAKitWhenItsTargetDiesHoweverItDies(double mark, double repaired)
    {
        // Doctor Home as pack rules (R41): every hit marks the target, the mark clears with its target, and the target's death
        // grants 10% of max armor (100) while it was marked. The hits at 0 and 1 s mark; the 1.5 s burn tick kills. A 0.25 s mark
        // has lapsed by then, leaving only generic armor-on-kill (3).
        var (original, _) = Fixture(changes: [("enemy_target_health", "30"), ("enemy_target_hit_damage", "400"), ("estimated_armor_on_kill", "3")]);
        var rules = original.Pack with
        {
            Selectors = [.. original.Pack.Selectors, new() { ID = "mark", Name = "Mark", Values = ["true"] }, new() { ID = "kit", Name = "Kit", Values = ["true"] }],
            Simulation = original.Pack.Simulation with
            {
                Triggers =
                [
                    .. original.Pack.Simulation.Triggers,
                    new() { Talent = "test", Key = "mark", On = "hit", Kind = "buff", Duration = mark, ConsumeOn = "target_death" },
                    new() { Talent = "test", Key = "kit", On = "target_death", Kind = "buff", Duration = 0.1, WhileKey = "mark", Grant = "armor_repair", GrantAmount = 0.1 }
                ]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test")),
            Options with { Duration = 1.6, IncludeIncoming = true });
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.Kill && e.Time == 1.5);
        Assert.Equal(repaired, result.Incoming!.ArmorRepaired);
    }

    [Theory]
    [InlineData("shocked")]
    [InlineData("ensnared")]
    public void OnlyBurningAndBleedingCanTick(string status) =>
        Assert.Equal("status-damage-invalid", Assert.Throws<SimulationException>(() =>
            new StatusDamageState([new() { Status = status, Tick = "burn_tick", Period = .5 }])).Code);
}
