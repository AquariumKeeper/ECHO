using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class TimedBuffStateTests
{
    private static SimTriggerDefinition Rule(double duration = 3) => new() { Talent = "test", Key = "buff", On = "hit", Kind = "buff", Duration = duration };
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;

    private static SimTriggerDefinition Stack(bool refresh = false, long gain = 0) =>
        new() { Talent = "test", Key = "stacks", On = "hit", Kind = "stacks", Duration = 3, Max = 2, Refresh = refresh, Gain = gain };

    private static SimTriggerDefinition StatGated(double every = 0, long count = 0) => new()
    { Talent = "test", Key = "buff", On = "kill", Kind = "buff", Duration = 15, Every = every, Count = count, WhileStatAtLeast = new() { Stat = "skill_tier", Value = 6 } };

    private static ImmutableDictionary<string, double> Tier(double tier) => ImmutableDictionary<string, double>.Empty.Add("skill_tier", tier);

    [Fact]
    public void AStatGateOpensAtItsValueAndItsCooldownStartsThere()
    {
        var state = new TimedBuffState([StatGated(every: 90)]);
        state.Fire("kill", 1, stats: Tier(5.99));
        Assert.False(state.Gates(1).Open("buff", whileFull: false));
        // No stats, as an event no gated rule waits on would have: it stays closed.
        state.Fire("kill", 2);
        Assert.False(state.Gates(2).Open("buff", whileFull: false));
        // Closed kills started no cooldown, and it's a floor, not a match.
        state.Fire("kill", 3, stats: Tier(6));
        Assert.True(state.Gates(3).Open("buff", whileFull: false));
        Assert.False(state.Gates(18).Open("buff", whileFull: false));
        state.Fire("kill", 92, stats: Tier(7));
        Assert.False(state.Gates(92).Open("buff", whileFull: false));
        state.Fire("kill", 93, stats: Tier(7));
        Assert.True(state.Gates(93).Open("buff", whileFull: false));
    }

    [Fact]
    public void AClosedStatGateDoesntCount()
    {
        var state = new TimedBuffState([StatGated(count: 2)]);
        state.Fire("kill", 1, stats: Tier(5));
        state.Fire("kill", 2, stats: Tier(6));
        Assert.False(state.Gates(2).Open("buff", whileFull: false));
        state.Fire("kill", 3, stats: Tier(6));
        Assert.True(state.Gates(3).Open("buff", whileFull: false));
    }

    [Fact]
    public void OnlyAGatedRulesEventsReadStats()
    {
        var state = new TimedBuffState([StatGated() with { Also = [new() { On = "crit_kill", Gain = 1 }], ConsumeOn = "reload" }, Rule()]);
        Assert.True(state.ReadsStats("kill"));
        Assert.True(state.ReadsStats("crit_kill"));
        Assert.False(state.ReadsStats("reload"));
        Assert.False(state.ReadsStats("hit"));
    }

    [Fact]
    public void AStatGateIsRefusedWhereItCantWork()
    {
        Assert.Throws<SimulationException>(() => new TimedBuffState([StatGated() with { Kind = "stacks", Max = 2 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([StatGated() with { WhileArmorBelow = 0.5 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([StatGated() with { WhileKey = "other" }, Rule() with { Key = "other" }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([StatGated() with { WhileStatAtLeast = new() { Stat = "", Value = 6 } }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([StatGated() with { WhileStatAtLeast = new() { Stat = "skill_tier", Value = double.NaN } }]));
    }

    private static SimTriggerDefinition Grouped(string talent, string key = "shared", string on = "kill", string group = "overcharge") => new()
    { Talent = talent, Key = key, On = on, Kind = "buff", Duration = 15, Every = 90, EveryGroup = group };

    [Fact]
    public void AGroupSharesOneCooldown()
    {
        // One talent grants on a kill at 1 s; the other's kill at 20 s finds the group's 90 s running, and its own unspent.
        var state = new TimedBuffState([Grouped("a"), Grouped("b", on: "crit_kill")]);
        state.Fire("kill", 1);
        Assert.True(state.Gates(1).Open("shared", whileFull: false));
        state.Fire("crit_kill", 20);
        Assert.False(state.Gates(20).Open("shared", whileFull: false));
        state.Fire("crit_kill", 91);
        Assert.True(state.Gates(91).Open("shared", whileFull: false));
    }

    [Fact]
    public void AGroupFiresOnceOnAnEventBothAnswer()
    {
        var state = new TimedBuffState([Grouped("a", key: "first"), Grouped("b", key: "second")]);
        state.Fire("kill", 1);
        Assert.True(state.Gates(1).Open("first", whileFull: false));
        Assert.False(state.Gates(1).Open("second", whileFull: false));
    }

    [Fact]
    public void RulesOutsideAGroupKeepTheirOwnCooldowns()
    {
        var state = new TimedBuffState([Grouped("a", key: "first"), Grouped("b", key: "second", on: "crit_kill", group: "other"),
            Grouped("d", key: "fourth", on: "reload", group: "other"), Grouped("e", key: "fifth", on: "reload", group: "overcharge"), Grouped("c", key: "third", on: "headshot_kill", group: "")]);
        state.Fire("kill", 1);
        state.Fire("crit_kill", 20);
        state.Fire("headshot_kill", 21);
        Assert.True(state.Gates(21).Open("second", whileFull: false));
        Assert.True(state.Gates(21).Open("third", whileFull: false));
    }

    [Fact]
    public void AFightHoldingOneOfAGroupRunsIt()
    {
        // A build carries one of the group's talents, so its fight has a group of one (R73). It runs as the same rule with no group.
        foreach (var rule in new[] { Grouped("a"), Grouped("a", group: "") })
        {
            var state = new TimedBuffState([rule]);
            state.Fire("kill", 1);
            Assert.True(state.Gates(1).Open("shared", whileFull: false));
            state.Fire("kill", 50);
            Assert.False(state.Gates(50).Open("shared", whileFull: false));
            state.Fire("kill", 90.5);
            Assert.False(state.Gates(90.5).Open("shared", whileFull: false));
            state.Fire("kill", 92);
            Assert.True(state.Gates(92).Open("shared", whileFull: false));
        }
    }

    [Fact]
    public void AGroupNeedsACooldown()
    {
        Assert.Throws<SimulationException>(() => new TimedBuffState([Grouped("a") with { Every = 0 }, Grouped("b") with { Every = 0 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Grouped("a") with { Kind = "lockout" }, Grouped("b") with { Kind = "lockout" }]));
        // One cooldown: mixed everys, or one spent from its end, isn't one.
        Assert.Throws<SimulationException>(() => new TimedBuffState([Grouped("a"), Grouped("b") with { Every = 10 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Grouped("a"), Grouped("b") with { Duration = 0, CooldownFromEnd = true, ConsumeOn = "reload" }]));
    }

    private static SimTriggerDefinition PoolRule(string key, string on) => new()
    { Talent = "test", Key = key, On = on, Kind = "stacks", Max = 9, Pool = "shared", PoolMax = 10 };

    [Theory]
    [InlineData(100, 50)]
    [InlineData(7, 3)]
    [InlineData(1, 1)]
    [InlineData(1000, 60)]
    public void AMagazineCapIsAShareOfItsWeaponsMagazineWithinMax(double magazine, int cap)
    {
        // Ardent's meter (R39): half the magazine, rounded down, at least 1 and at most the rule's max.
        var rule = new SimTriggerDefinition { Talent = "ardent", Key = "heat", On = "shot", Kind = "stacks", Max = 60, MaxMagazine = 0.5 };
        var state = new TimedBuffState([rule], new Dictionary<string, double> { ["ardent"] = magazine });
        for (var shot = 0; shot < 200; shot++) { state.Fire("shot", shot * 0.1); }
        Assert.Equal(cap.ToString(System.Globalization.CultureInfo.InvariantCulture), state.Context(Empty, 20)["heat"]);
        Assert.True(state.Gates(20).Open("heat", whileFull: true));
        // A talent with no weapon magazine is refused.
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => new TimedBuffState([rule], new Dictionary<string, double>())).Code);
    }

    [Fact]
    public void GatesReadActiveKeysAndFullStacks()
    {
        var state = new TimedBuffState([Rule(), Stack()]);
        state.Fire("hit", 1);
        var gates = state.Gates(1);
        Assert.True(gates.Open("buff", whileFull: false));
        Assert.True(gates.Open("stacks", whileFull: false));
        Assert.False(gates.Open("stacks", whileFull: true));
        state.Fire("hit", 2);
        Assert.True(state.Gates(2).Open("stacks", whileFull: true));
        // Both expire; an unknown key is closed.
        Assert.False(state.Gates(10).Open("buff", whileFull: false));
        Assert.False(state.Gates(10).Open("stacks", whileFull: false));
        Assert.False(state.Gates(10).Open("missing", whileFull: false));
    }

    [Fact]
    public void MeasurementUnionsSharedBuffsAndDoesNotCommitFutureReads()
    {
        var state = new TimedBuffState([Rule(), Rule(5) with { Talent = "other", On = "crit" }]);
        state.Fire("hit", 1);
        Assert.Equal(new TimedTriggerDuration(3, null), state.Measure(100)["buff"]);
        state.Fire("crit", 2);
        Assert.Equal(new TimedTriggerDuration(4, null), state.Measure(5)["buff"]);
        Assert.Equal(new TimedTriggerDuration(6, null), state.Measure(10)["buff"]);
        Assert.Equal(new TimedTriggerDuration(1, null), state.Measure(2)["buff"]);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("lockout")]
    public void BooleanMeasurementFollowsTrueContextPolarityAndSharedOwners(string kind)
    {
        var state = new TimedBuffState([Rule() with { Kind = kind }, Rule(5) with { Kind = kind, On = "crit", Talent = "other" }]);
        state.Fire("hit", 1);
        // The other owner is still ready, so the shared key stays true.
        state.Fire("crit", 2);
        // Both are false from 2 through 4; the first owner recovers at 4.
        Assert.Equal(new TimedTriggerDuration(4, null), state.Measure(6)["buff"]);
        Assert.True(state.Context(Empty, 6).ContainsKey("buff"));
    }

    [Theory]
    [InlineData(false, 5, 6)]
    [InlineData(true, 5, 8)]
    public void StackMeasurementIntegratesLiveCountsAndRefresh(bool refresh, double active, double stackTime)
    {
        var state = new TimedBuffState([Stack(refresh)]);
        state.Fire("hit", 1);
        state.Fire("hit", 3);
        Assert.Equal(new TimedTriggerDuration(active, stackTime), state.Measure(10)["stacks"]);
    }

    [Fact]
    public void DecayConsumptionAndSharedPoolClearsCloseMeasurementAtTheEvent()
    {
        var decaying = new TimedBuffState([Stack(true, 2) with { Decay = 1, ConsumeOn = "reload" }]);
        decaying.Fire("hit", 0);
        // Two stacks until 4, then one until 5; consume at 4.5.
        decaying.Fire("reload", 4.5);
        Assert.Equal(new TimedTriggerDuration(4.5, 8.5), decaying.Measure(10)["stacks"]);
        var pool = new TimedBuffState([PoolRule("a", "hit") with { Gain = 5 }, PoolRule("b", "crit") with { Gain = 5 }]);
        pool.Fire("hit", 1);
        pool.Fire("crit", 3);
        Assert.Equal(new TimedTriggerDuration(2, 10), pool.Measure(10)["a"]);
        Assert.Equal(new TimedTriggerDuration(0, 0), pool.Measure(10)["b"]);
    }

    [Fact]
    public void CooldownFromEndCountsFromTheBonusBeingSpent()
    {
        // Perpetuation's shape: a bonus with no timer, spent by the next status, 20 s after which another can come.
        var bonus = new SimTriggerDefinition { Talent = "test", Key = "buff", On = "hit", Kind = "buff", ConsumeOn = "reload", Every = 20, CooldownFromEnd = true };
        var state = new TimedBuffState([bonus]);
        state.Fire("hit", 0);
        Assert.Equal("true", state.Context(Empty, 1)["buff"]);
        // Past its interval from the gain, it still holds while the bonus is up.
        state.Fire("hit", 25);
        state.Fire("reload", 30);
        Assert.False(state.Context(Empty, 31).ContainsKey("buff"));
        state.Fire("hit", 45);
        Assert.False(state.Context(Empty, 46).ContainsKey("buff"));
        state.Fire("hit", 50);
        Assert.Equal("true", state.Context(Empty, 51)["buff"]);
        // Spending and gaining on one event: the spend starts the interval, so it doesn't regain at once.
        var both = new TimedBuffState([bonus with { ConsumeOn = "hit" }]);
        both.Fire("hit", 0);
        both.Fire("hit", 25);
        Assert.False(both.Context(Empty, 26).ContainsKey("buff"));
        both.Fire("hit", 45);
        Assert.Equal("true", both.Context(Empty, 46)["buff"]);
        // With nothing up, the event spends nothing, so the interval still counts from the gain.
        var idle = new TimedBuffState([bonus]);
        idle.Fire("reload", 1);
        idle.Fire("hit", 2);
        Assert.Equal("true", idle.Context(Empty, 3)["buff"]);
    }

    [Fact]
    public void ConsumptionAndExpiryExcludeInactiveGaps()
    {
        var state = new TimedBuffState([Rule() with { ConsumeOn = "reload" }]);
        state.Fire("hit", 1);
        state.Fire("reload", 2);
        state.Fire("hit", 5);
        state.Fire("unrelated", 10);
        Assert.Equal(new TimedTriggerDuration(4, null), state.Measure(12)["buff"]);
    }

    [Fact]
    public void InvalidEventsLeaveMeasurementAtomicAndUnusedKeysHaveZeroTime()
    {
        var state = new TimedBuffState([Rule(double.MaxValue), Stack()]);
        Assert.Equal(new TimedTriggerDuration(0, null), state.Measure(1)["buff"]);
        Assert.Equal(new TimedTriggerDuration(0, 0), state.Measure(1)["stacks"]);
        Assert.Throws<SimulationException>(() => state.Fire("hit", double.MaxValue));
        state.Fire("hit", 1);
        Assert.Equal(new TimedTriggerDuration(1, null), state.Measure(2)["buff"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Measure(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Measure(double.NaN));
    }

    [Fact]
    public void ScopedEventsPreserveExcludedGainsButStillConsumeOnKills()
    {
        var gear = Rule() with { Talent = "gear", Key = "gear", On = "kill", Count = 2 };
        var weapon = gear with { Talent = "weapon", Key = "weapon" };
        var state = new TimedBuffState([gear, weapon]);
        var allowed = new HashSet<string> { "gear" };
        state.Fire("kill", 0, eligibleTalents: allowed);
        state.Fire("kill", 1, eligibleTalents: allowed);
        Assert.True(state.Context(Empty, 1).ContainsKey("gear"));
        Assert.False(state.Context(Empty, 1).ContainsKey("weapon"));
        state.Fire("kill", 2);
        Assert.False(state.Context(Empty, 2).ContainsKey("weapon"));
        state.Fire("kill", 3);
        Assert.True(state.Context(Empty, 3).ContainsKey("weapon"));
        var consumed = new TimedBuffState([Rule() with { Talent = "weapon", ConsumeOn = "kill" }]);
        consumed.Fire("hit", 0);
        consumed.Fire("kill", 1, eligibleTalents: allowed);
        Assert.False(consumed.Context(Empty, 1).ContainsKey("buff"));
        consumed.Fire("kill", 2);
        Assert.False(consumed.Context(Empty, 2).ContainsKey("buff"));
    }

    [Fact]
    public void ReadingContextNeverMovesTheEventClock()
    {
        var state = new TimedBuffState([Rule()]);
        Assert.False(state.Context(Empty, 10).ContainsKey("buff"));
        state.Fire("hit", 5);
        Assert.Equal("true", state.Context(Empty, 6)["buff"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Fire("hit", 4));
    }

    [Fact]
    public void AnEventBeforeAnEarlierReadRestartsTheExpiryWatermark()
    {
        var state = new TimedBuffState([Rule()]);
        state.Context(Empty, 10);
        state.Fire("hit", 5);
        Assert.Equal("true", state.Context(Empty, 6)["buff"]);
        Assert.Equal(8, state.NextExpiry);
        state.Context(Empty, 8);
        Assert.Null(state.NextExpiry);
    }

    [Theory]
    [InlineData("ready", 3)]
    [InlineData("lockout", 5)]
    public void ReadyAndLockoutStartActiveAndRecoverAtTheirExactDeadline(string kind, double expectedDeadline)
    {
        var state = new TimedBuffState([Rule() with { Kind = kind }]);
        Assert.Equal("true", state.Context(Empty.Add("buff", "false"), 0)["buff"]);
        Assert.Null(state.NextExpiry);
        state.Fire("hit", 0);
        Assert.False(state.Context(Empty, 0).ContainsKey("buff"));
        state.Fire("hit", 2);
        Assert.Equal(expectedDeadline, state.NextExpiry);
        Assert.False(state.Context(Empty, Math.BitDecrement(expectedDeadline)).ContainsKey("buff"));
        Assert.Equal("true", state.Context(Empty, expectedDeadline)["buff"]);
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void BusyReadyRuleCannotCountRefreshOrRequestRefills()
    {
        var state = new TimedBuffState([Rule() with { Kind = "ready", Count = 2, Refill = true }]);
        Assert.False(state.Fire("hit", 0));
        Assert.True(state.Fire("hit", 1));
        Assert.False(state.Fire("hit", 2));
        Assert.False(state.Fire("hit", 3));
        Assert.Equal(4, state.NextExpiry);
        Assert.False(state.Fire("hit", 4));
        Assert.True(state.Fire("hit", 4));
        Assert.Equal(7, state.NextExpiry);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("lockout")]
    public void PersistentInverseStateCanBeClearedByReset(string kind)
    {
        var state = new TimedBuffState([Rule(0) with { Kind = kind, ResetOn = "reload" }]);
        state.Fire("hit", 0);
        Assert.False(state.Context(Empty, 1e100).ContainsKey("buff"));
        Assert.Null(state.NextExpiry);
        state.Fire("reload", 1e100);
        Assert.Equal("true", state.Context(Empty, 1e100)["buff"]);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("lockout")]
    public void SharedInverseKeysRecoverWhenTheFirstOwnerRecovers(string kind)
    {
        var state = new TimedBuffState([Rule(10) with { Kind = kind }, Rule(3) with { Kind = kind, On = "kill" }]);
        state.Fire("hit", 0);
        Assert.Equal("true", state.Context(Empty, 0)["buff"]);
        Assert.Null(state.NextExpiry);
        state.Fire("kill", 1);
        Assert.False(state.Context(Empty, 1).ContainsKey("buff"));
        Assert.Equal(4, state.NextExpiry);
        Assert.Equal("true", state.Context(Empty, 4)["buff"]);
        Assert.Null(state.NextExpiry);
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Kind = kind }, Rule()]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Kind = "ready" }, Rule() with { Kind = "lockout" }]));
    }

    [Fact]
    public void FailedInverseEventLeavesDeadlineAndClockUnchanged()
    {
        var state = new TimedBuffState([Rule(1e101) with { Kind = "ready" }, Rule(1) with { Key = "other" }]);
        Assert.Throws<SimulationException>(() => state.Fire("hit", 1e100));
        Assert.Equal("true", state.Context(Empty, 0)["buff"]);
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void CommittedVigilanceLosesAndRecoversCoreDamageBonus()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "gear_talent_vigilance");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", rule.Talent);
        var ready = state.Resolve(pack, build, Empty, 0).Calculation.Stats["total_weapon_damage"];
        state.Fire("damaged", 0);
        var locked = state.Resolve(pack, build, Empty, 0).Calculation.Stats["total_weapon_damage"];
        Assert.Equal(0.25, ready - locked, 8);
        state.Fire("damaged", 2);
        Assert.Equal(locked, state.Resolve(pack, build, Empty, 4).Calculation.Stats["total_weapon_damage"]);
        Assert.Equal(ready, state.Resolve(pack, build, Empty, 6).Calculation.Stats["total_weapon_damage"]);
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(pack, build, Empty,
            new() { Mode = SimulationMode.Random, DriveSimpleBuffs = true }, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void CommittedUnbreakableReadinessControlsCoreRepairContribution()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "gear_talent_unbreakable");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithTalentSelection("chest", "talent", rule.Talent);
        Assert.Equal(0.95, state.Resolve(pack, build, Empty, 0).Calculation.Stats["armor_repair_on_depletion_percent"], 8);
        state.Fire("armor_break", 0);
        state.Fire("armor_break", 30);
        Assert.Equal(60, state.NextExpiry);
        Assert.Equal(0, state.Resolve(pack, build, Empty, 30).Calculation.Stats["armor_repair_on_depletion_percent"]);
        Assert.Equal(0.95, state.Resolve(pack, build, Empty, 60).Calculation.Stats["armor_repair_on_depletion_percent"], 8);
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(pack, build, Empty,
            new() { Mode = SimulationMode.Random, DriveSimpleBuffs = true }, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void PoolCapClearsAllMembersTogetherAndLeavesOtherStateAlone()
    {
        var state = new TimedBuffState([PoolRule("red", "kill") with { Also = [new() { On = "hit", Gain = 1, Count = 3 }] },
            PoolRule("blue", "damaged") with { Count = 3 }, Rule(10)]);
        for (var hit = 0; hit < 6; hit++) { state.Fire("hit", 0); }
        state.Fire("kill", 1);
        for (var hit = 0; hit < 18; hit++) { state.Fire("damaged", 2); }
        Assert.Equal("3", state.Context(Empty, 2)["red"]);
        Assert.Equal("6", state.Context(Empty, 2)["blue"]);
        state.Fire("kill", 3);
        var context = state.Context(Empty, 3);
        Assert.Equal("0", context["red"]);
        Assert.Equal("0", context["blue"]);
        Assert.Equal("true", context["buff"]);
    }

    [Fact]
    public void PoolUsesPostEventTotalsAndClearsOnOvershoot()
    {
        var state = new TimedBuffState([PoolRule("red", "hit") with { Gain = 6 }, PoolRule("blue", "hit") with { Gain = 6 }]);
        state.Fire("hit", 0);
        Assert.All(state.Context(Empty, 0).Values, value => Assert.Equal("0", value));
    }

    [Fact]
    public void ExpiredStacksDoNotCountTowardThePoolCap()
    {
        var state = new TimedBuffState([PoolRule("red", "kill") with { Gain = 6, Duration = 1 }, PoolRule("blue", "hit") with { Gain = 6 }]);
        state.Fire("kill", 0);
        state.Fire("hit", 1);
        Assert.Equal("0", state.Context(Empty, 1)["red"]);
        Assert.Equal("6", state.Context(Empty, 1)["blue"]);
    }

    [Fact]
    public void PoolResetClearsPartialCountersButKeepsActivationCooldowns()
    {
        var state = new TimedBuffState([PoolRule("red", "kill") with { Every = 5, Also = [new() { On = "hit", Gain = 1, Count = 2 }] },
            PoolRule("blue", "damaged") with { Gain = 9, Count = 2 }]);
        state.Fire("kill", 0);
        state.Fire("hit", 5);
        state.Fire("damaged", 5);
        state.Fire("damaged", 5);
        Assert.Equal("0", state.Context(Empty, 5)["red"]);
        state.Fire("hit", 5);
        Assert.Equal("0", state.Context(Empty, 5)["red"]);
        state.Fire("hit", 5);
        Assert.Equal("1", state.Context(Empty, 5)["red"]);
        state.Fire("damaged", 5);
        Assert.Equal("0", state.Context(Empty, 5)["blue"]);
        state.Fire("damaged", 5);
        state.Fire("kill", 6);
        Assert.Equal("0", state.Context(Empty, 6)["red"]);
    }

    [Fact]
    public void FailedEventCannotCommitGainsOrPoolReset()
    {
        var state = new TimedBuffState([PoolRule("red", "hit") with { Gain = 6 }, PoolRule("blue", "kill") with { Gain = 6 }, Rule(1) with { On = "kill" }]);
        state.Fire("hit", 0);
        Assert.Throws<SimulationException>(() => state.Fire("kill", 1e100));
        Assert.Equal("6", state.Context(Empty, 0)["red"]);
        Assert.Equal("0", state.Context(Empty, 0)["blue"]);
        state.Fire("kill", 1);
        Assert.Equal("0", state.Context(Empty, 1)["red"]);
    }

    [Fact]
    public void PurgeWaitsForTheNextEligibleGainAtCap()
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, Purge = true, Every = 1 }]);
        foreach (var (time, expected) in new[] { (0d, "1"), (1d, "2"), (1.5, "2"), (2d, "0"), (3d, "1") })
        {
            state.Fire("hit", time);
            Assert.Equal(expected, state.Context(Empty, time)["stacks"]);
        }
    }

    [Fact]
    public void InvalidPoolDefinitionsAreRefused()
    {
        Assert.Throws<SimulationException>(() => new TimedBuffState([PoolRule("red", "hit") with { PoolMax = 0 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([PoolRule("red", "hit") with { Pool = "" }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([PoolRule("red", "hit"), PoolRule("blue", "hit") with { PoolMax = 11 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Pool = "shared", PoolMax = 10 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Purge = true }]));
    }

    [Theory]
    [InlineData("gear_talent_rebalance", "backpack", "backpack_harrier_pride", 79, 0.395)]
    [InlineData("gear_talent_vicious_cycle", "mask", "mask_catharsis", 30, 0.45)]
    public void CommittedPoolAndPurgeRulesFeedCoreThenClear(string talent, string slot, string item, int gains, double bonus)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rules = pack.Pack.Simulation.Triggers.Where(rule => rule.Talent == talent).ToArray();
        var state = new TimedBuffState(rules);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment(slot, item).WithTalentSelection(slot, "talent", talent);
        var before = state.Resolve(pack, build, Empty, 0).Calculation.Stats["weapon_damage"];
        var eventName = talent == "gear_talent_rebalance" ? "kill" : "damaged";
        for (var gain = 0; gain < gains; gain++) { state.Fire(eventName, 0); }
        Assert.Equal(bonus, state.Resolve(pack, build, Empty, 0).Calculation.Stats["weapon_damage"] - before, 8);
        for (var hit = 0; hit < (talent == "gear_talent_rebalance" ? 3 : 1); hit++) { state.Fire("damaged", 1); }
        var rebalance = talent == "gear_talent_rebalance";
        // Reaching the cap clears the stacks. Rebalance banks them (R34): the capping Blue stack becomes 0.5% Weapon
        // Damage and the 79 Red stacks become 0.5% Damage Resistance each, until the next cap.
        var after = state.Resolve(pack, build, Empty, 1).Calculation.Stats;
        Assert.Equal(rebalance ? 0.005 : 0, after["weapon_damage"] - before, 8);
        Assert.All(rules, rule => Assert.Equal("0", state.Context(Empty, 1)[rule.Key]));
        if (rebalance)
        {
            Assert.Equal(("79", "1"), (state.Context(Empty, 1)["rebalance_red_banked"], state.Context(Empty, 1)["rebalance_blue_banked"]));
            Assert.Equal(0.395, after["damage_resistance"] - state.Resolve(pack, build.WithTalentSelection(slot, "talent", ""), Empty, 1).Calculation.Stats["damage_resistance"], 8);
        }
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(pack, build, Empty,
            new() { Mode = SimulationMode.Random, DriveSimpleBuffs = true }, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void AlternateGainsAccumulateAndPrimaryEventsTakePriority()
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, Max = 5,
            Also = [new() { On = "headshot", Gain = 2 }, new() { On = "hit", Gain = 4 }] }]);
        state.Fire("hit", 0);
        Assert.Equal("1", state.Context(Empty, 0)["stacks"]);
        state.Fire("headshot", 1);
        Assert.Equal("3", state.Context(Empty, 1)["stacks"]);
        state.Fire("headshot", 2);
        state.Fire("headshot", 3);
        Assert.Equal("5", state.Context(Empty, 3)["stacks"]);
    }

    [Fact]
    public void AlternateCountersAreIndependentAndShareTheActivationCooldown()
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, Max = 10, Every = 5,
            Also = [new() { On = "headshot", Gain = 2, Count = 2 }, new() { On = "crit", Gain = 3, Count = 2 }] }]);
        state.Fire("headshot", 0);
        state.Fire("crit", 0);
        state.Fire("hit", 1);
        state.Fire("headshot", 2);
        state.Fire("headshot", 6);
        Assert.Equal("3", state.Context(Empty, 6)["stacks"]);
        state.Fire("crit", 11);
        Assert.Equal("6", state.Context(Empty, 11)["stacks"]);
    }

    [Theory]
    [InlineData(0, "reload", "", "0")]
    [InlineData(1, "reload", "", "2")]
    [InlineData(1, "", "reload", "0")]
    public void ClearingAlternateCountersFollowsResetAndConsumptionPolicy(long count, string reset, string consume, string expected)
    {
        var state = new TimedBuffState([Stack() with { Count = count, ResetOn = reset, ConsumeOn = consume,
            Also = [new() { On = "headshot", Gain = 2, Count = 2 }] }]);
        state.Fire("headshot", 0);
        state.Fire("reload", 1);
        state.Fire("headshot", 2);
        Assert.Equal(expected, state.Context(Empty, 2)["stacks"]);
    }

    [Fact]
    public void FailedEventDoesNotCommitAlternateProgress()
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, Also = [new() { On = "headshot", Gain = 2, Count = 2 }] },
            Rule(1) with { On = "headshot" }]);
        state.Fire("headshot", 0);
        Assert.Throws<SimulationException>(() => state.Fire("headshot", 1e100));
        state.Fire("headshot", 1);
        Assert.Equal("2", state.Context(Empty, 1)["stacks"]);
    }

    [Theory]
    [InlineData(0, 0, "headshot")]
    [InlineData(-1, 0, "headshot")]
    [InlineData(10001, 0, "headshot")]
    [InlineData(1, -1, "headshot")]
    [InlineData(1, 0, "")]
    public void InvalidAlternateGainIsRefused(long gain, long count, string on) =>
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack() with { Also = [new() { On = on, Gain = gain, Count = count }] }]));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0.5)]
    public void DecayHoldsStacksThenDropsOneAtEachExactBoundary(double rate)
    {
        var state = new TimedBuffState([Stack(refresh: true, gain: 3) with { Max = 5, Decay = rate }]);
        state.Fire("hit", 0);
        Assert.Equal(3 + 1 / rate, state.NextExpiry);
        Assert.Equal("3", state.Context(Empty, 3)["stacks"]);
        Assert.Equal("3", state.Context(Empty, Math.BitDecrement(3 + 1 / rate))["stacks"]);
        Assert.Equal("2", state.Context(Empty, 3 + 1 / rate)["stacks"]);
        Assert.Equal(3 + 2 / rate, state.NextExpiry);
        Assert.Equal("0", state.Context(Empty, 3 + 3 / rate)["stacks"]);
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void GainDuringDecayUsesTheRemainingCountAndRestartsTheHold()
    {
        var state = new TimedBuffState([Stack(refresh: true, gain: 3) with { Max = 5, Decay = 1 }]);
        state.Fire("hit", 0);
        Assert.Equal("2", state.Context(Empty, 4)["stacks"]);
        state.Fire("hit", 4);
        Assert.Equal("5", state.Context(Empty, 7)["stacks"]);
        Assert.Equal(8, state.NextExpiry);
        Assert.Equal("4", state.Context(Empty, 8)["stacks"]);
        Assert.Equal("0", state.Context(Empty, 1e100)["stacks"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDecayIsRefused(double rate) =>
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack(refresh: true) with { Decay = rate }]));

    [Fact]
    public void UnrepresentableDecayDeadlinesLeaveTheWholeEventUnchanged()
    {
        var state = new TimedBuffState([Rule(0) with { Refill = true }, Stack(refresh: true) with { Decay = double.MaxValue }]);
        Assert.Throws<SimulationException>(() => state.Fire("hit", 0));
        Assert.False(state.Context(Empty, 0).ContainsKey("buff"));
        Assert.Equal("0", state.Context(Empty, 0)["stacks"]);
        Assert.Null(state.NextExpiry);
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack(refresh: true) with { Duration = 0, Decay = 1 }]));
    }

    [Fact]
    public void CommittedFullStopAlternateGainsFeedCoreAndClearOnReload()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_full_stop" && rule.Kind == "stacks");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_dread_edict")
            .WithTalentSelection("primary_weapon", "talent", rule.Talent);
        var context = Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var before = state.Resolve(pack, build, context, 0).Calculation.Stats["weapon_damage"];
        state.Fire("body_hit", 0);
        state.Fire("headshot", 1);
        Assert.Equal(0.06, state.Resolve(pack, build, context, 1).Calculation.Stats["weapon_damage"] - before, 8);
        state.Fire("reload", 2);
        Assert.Equal(before, state.Resolve(pack, build, context, 2).Calculation.Stats["weapon_damage"]);
        // The reload clears three stacks into temporary armor: 5% of max armor each, for 10 s (R34).
        var grant = Assert.Single(state.TakeGrants());
        Assert.Equal(("temporary_armor", 10.0, 2.0), (grant.Kind, grant.Duration, grant.Time));
        Assert.Equal(0.15, grant.Share, 12);
    }

    [Fact]
    public void CommittedCapacitanceDecaysSkillDamageThroughCore()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_capacitance");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_capacitor")
            .WithTalentSelection("primary_weapon", "talent", rule.Talent);
        var context = Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var before = state.Resolve(pack, build, context, 0).Calculation.Stats["skill_damage"];
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        Assert.Equal(0.03, state.Resolve(pack, build, context, 6).Calculation.Stats["skill_damage"] - before, 8);
        Assert.Equal(0.015, state.Resolve(pack, build, context, 7).Calculation.Stats["skill_damage"] - before, 8);
        Assert.Equal(before, state.Resolve(pack, build, context, 8).Calculation.Stats["skill_damage"]);
    }

    [Theory]
    [InlineData("reload", "")]
    [InlineData("", "kill")]
    public void PersistentBuffsRemainActiveUntilCleared(string reset, string consume)
    {
        var state = new TimedBuffState([Rule(0) with { ResetOn = reset, ConsumeOn = consume }]);
        state.Fire("hit", 0);
        Assert.Equal("true", state.Context(Empty, 1e100)["buff"]);
        Assert.Null(state.NextExpiry);
        state.Fire(reset.Length > 0 ? reset : consume, 1e100);
        Assert.False(state.Context(Empty, 1e100).ContainsKey("buff"));
    }

    [Fact]
    public void ClearingAPersistentOwnerRevealsTheTimedOwnersExpiry()
    {
        var state = new TimedBuffState([Rule(0) with { ConsumeOn = "kill" }, Rule(10) with { On = "crit" }]);
        state.Fire("hit", 0);
        state.Fire("crit", 1);
        Assert.Null(state.NextExpiry);
        state.Fire("kill", 2);
        Assert.Equal("true", state.Context(Empty, 2)["buff"]);
        Assert.Equal(11, state.NextExpiry);
        Assert.False(state.Context(Empty, 11).ContainsKey("buff"));
    }

    [Fact]
    public void RefillIsAnActivationResultAndDoesNotLeakIntoLaterEvents()
    {
        var state = new TimedBuffState([Rule() with { Count = 2, Every = 5, Refill = true }]);
        Assert.False(state.Fire("hit", 0));
        Assert.True(state.Fire("hit", 1));
        Assert.False(state.Fire("kill", 1));
        Assert.False(state.Fire("hit", 2));
        Assert.False(state.Fire("hit", 6));
        Assert.True(state.Fire("hit", 6));
    }

    [Fact]
    public void FailedRefillEventDoesNotCommitItsCounterOrActivity()
    {
        var state = new TimedBuffState([Rule(0) with { Count = 2, Refill = true }, Rule(1) with { Key = "other" }]);
        Assert.False(state.Fire("hit", 0));
        Assert.Throws<SimulationException>(() => state.Fire("hit", 1e100));
        Assert.False(state.Context(Empty, 0).ContainsKey("buff"));
        Assert.True(state.Fire("hit", 1));
    }

    [Fact]
    public void CommittedOverflowingConsumesAndReactivatesEveryThirdReload()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_overflowing");
        var state = new TimedBuffState([rule]);
        for (var reload = 1; reload <= 6; reload++)
        {
            state.Fire("reload", reload);
            Assert.Equal(reload % 3 == 0, state.Context(Empty, reload).ContainsKey(rule.Key));
            Assert.Null(state.NextExpiry);
        }
    }

    [Fact]
    public void CooldownStartsOnFirstActivationAndAllowsTheExactBoundary()
    {
        var state = new TimedBuffState([Stack() with { Every = 0.5 }]);
        state.Fire("hit", 0);
        state.Fire("hit", 0);
        state.Fire("hit", Math.BitDecrement(0.5));
        Assert.Equal("1", state.Context(Empty, Math.BitDecrement(0.5))["stacks"]);
        state.Fire("hit", 0.5);
        Assert.Equal("2", state.Context(Empty, 0.5)["stacks"]);
        Assert.Equal(3, state.NextExpiry);
    }

    [Theory]
    [InlineData("reload", "")]
    [InlineData("", "kill")]
    public void ClearingActivityDoesNotBypassCooldown(string reset, string consume)
    {
        var state = new TimedBuffState([Rule(1) with { Every = 5, ResetOn = reset, ConsumeOn = consume }]);
        state.Fire("hit", 0);
        state.Fire(reset.Length > 0 ? reset : consume, 0.5);
        state.Fire("hit", 1);
        Assert.False(state.Context(Empty, 1).ContainsKey("buff"));
        state.Fire("hit", 5);
        Assert.Equal("true", state.Context(Empty, 5)["buff"]);
    }

    [Fact]
    public void CountedResetRestartsPartialProgressAndKeepsTheEarnedBuff()
    {
        var state = new TimedBuffState([Rule(10) with { Count = 3, ResetOn = "reload" }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        state.Fire("reload", 2);
        state.Fire("hit", 3);
        state.Fire("hit", 4);
        Assert.False(state.Context(Empty, 4).ContainsKey("buff"));
        state.Fire("hit", 5);
        state.Fire("hit", 6);
        state.Fire("reload", 7);
        Assert.Equal("true", state.Context(Empty, 7)["buff"]);
        Assert.Equal(15, state.NextExpiry);
        state.Fire("hit", 8);
        state.Fire("hit", 9);
        Assert.Equal(15, state.NextExpiry);
        state.Fire("hit", 10);
        Assert.Equal(20, state.NextExpiry);
    }

    [Fact]
    public void ConsumptionClearsTheBuffButPreservesPartialProgress()
    {
        var state = new TimedBuffState([Rule(10) with { Count = 2, ConsumeOn = "kill" }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        state.Fire("hit", 2);
        state.Fire("kill", 3);
        Assert.False(state.Context(Empty, 3).ContainsKey("buff"));
        state.Fire("hit", 4);
        Assert.Equal("true", state.Context(Empty, 4)["buff"]);
    }

    [Fact]
    public void CooldownBlockedEventsDoNotAdvanceTheCountOrRefreshActivity()
    {
        var state = new TimedBuffState([Rule(1) with { Every = 5, Count = 2 }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        state.Fire("hit", 2);
        state.Fire("hit", 5);
        Assert.False(state.Context(Empty, 5).ContainsKey("buff"));
        state.Fire("hit", 6);
        Assert.False(state.Context(Empty, 6).ContainsKey("buff"));
        state.Fire("hit", 6);
        Assert.Equal("true", state.Context(Empty, 6)["buff"]);
    }

    [Fact]
    public void SharedBuffOwnersKeepIndependentCountsCooldownsAndClears()
    {
        var state = new TimedBuffState([Rule(10) with { Every = 5, ConsumeOn = "kill" },
            Rule(3) with { On = "crit", Count = 2, ResetOn = "reload" }]);
        state.Fire("hit", 0);
        state.Fire("crit", 1);
        state.Fire("crit", 2);
        state.Fire("kill", 3);
        Assert.Equal("true", state.Context(Empty, 3)["buff"]);
        Assert.Equal(5, state.NextExpiry);
        state.Fire("hit", 4);
        Assert.False(state.Context(Empty, 5).ContainsKey("buff"));
        state.Fire("hit", 5);
        Assert.Equal("true", state.Context(Empty, 5)["buff"]);
    }

    [Fact]
    public void FailedEventCannotCommitCountsCooldownOrClears()
    {
        var state = new TimedBuffState([Rule(1e101) with { Every = 2, Count = 2 },
            Rule(1) with { Key = "other" }]);
        state.Fire("hit", 0);
        Assert.Throws<SimulationException>(() => state.Fire("hit", 1e100));
        state.Fire("hit", 1);
        Assert.Equal("true", state.Context(Empty, 1)["buff"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCooldownIsRefused(double every) =>
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Every = every }]));

    [Fact]
    public void NegativeCountIsRefused() =>
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Count = -1 }]));

    [Fact]
    public void CountedStackResetPreservesGainsAndResetsOnlyPartialProgress()
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, Count = 2, ResetOn = "reload" }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        state.Fire("hit", 2);
        state.Fire("reload", 3);
        state.Fire("hit", 4);
        Assert.Equal("1", state.Context(Empty, 4)["stacks"]);
        state.Fire("hit", 5);
        Assert.Equal("2", state.Context(Empty, 5)["stacks"]);
    }

    [Fact]
    public void CommittedMosquitoSongActivatesOnTheFifthHitAndExpires()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_mosquito_song");
        var state = new TimedBuffState([rule]);
        for (var hit = 0; hit < 4; hit++) { state.Fire("hit", hit); }
        Assert.False(state.Context(Empty, 3).ContainsKey(rule.Key));
        state.Fire("hit", 4);
        Assert.Equal("true", state.Context(Empty, 4)[rule.Key]);
        Assert.False(state.Context(Empty, 9).ContainsKey(rule.Key));
    }

    [Theory]
    [InlineData("reload", "")]
    [InlineData("", "headshot")]
    public void PersistentStacksRemainBoundedUntilResetOrConsumption(string reset, string consume)
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, ResetOn = reset, ConsumeOn = consume }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        state.Fire("hit", 2);
        Assert.Equal("2", state.Context(Empty, 1e100)["stacks"]);
        Assert.Null(state.NextExpiry);
        state.Fire(reset.Length > 0 ? reset : consume, 1e100);
        Assert.Equal("0", state.Context(Empty, 1e100)["stacks"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetAndConsumptionPrecedeGainsOnTheSameEvent(bool reset)
    {
        var state = new TimedBuffState([Stack() with { ResetOn = reset ? "hit" : "", ConsumeOn = reset ? "" : "hit" }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        Assert.Equal("1", state.Context(Empty, 1)["stacks"]);
        Assert.Equal(4, state.NextExpiry);
    }

    [Fact]
    public void FailedExpiryValidationDoesNotCommitAResetOrTheClock()
    {
        var state = new TimedBuffState([Stack() with { Duration = 0, ResetOn = "hit", On = "crit" }, Rule(1)]);
        state.Fire("crit", 0);
        Assert.Throws<SimulationException>(() => state.Fire("hit", 1e100));
        Assert.Equal("1", state.Context(Empty, 0)["stacks"]);
        state.Fire("hit", 1);
        Assert.Equal("0", state.Context(Empty, 1)["stacks"]);
    }

    [Fact]
    public void TimedConsumptionRemovesPendingExpiriesWithoutAReplacementGain()
    {
        var state = new TimedBuffState([Stack() with { ConsumeOn = "headshot" }]);
        state.Fire("hit", 0);
        state.Fire("headshot", 1);
        Assert.Equal("0", state.Context(Empty, 1)["stacks"]);
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void IndependentStacksExpireSeparatelyAndCapReplacesTheEarliest()
    {
        var state = new TimedBuffState([Stack()]);
        Assert.Equal("0", state.Context(Empty.Add("stacks", "99"), 0)["stacks"]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        state.Fire("hit", 2);
        Assert.Equal(4, state.NextExpiry);
        Assert.Equal("2", state.Context(Empty, 3)["stacks"]);
        Assert.Equal("1", state.Context(Empty, 4)["stacks"]);
        Assert.Equal("0", state.Context(Empty, 5)["stacks"]);
    }

    [Fact]
    public void RefreshingStacksShareTheNewestDeadline()
    {
        var state = new TimedBuffState([Stack(refresh: true)]);
        state.Fire("hit", 0);
        state.Fire("hit", 2);
        Assert.Equal("2", state.Context(Empty, 3)["stacks"]);
        Assert.Equal(5, state.NextExpiry);
        Assert.Equal("0", state.Context(Empty, 5)["stacks"]);
    }

    [Fact]
    public void StackGainIsBoundedAndUnsupportedFieldsRemainRefused()
    {
        var state = new TimedBuffState([Stack(gain: 100)]);
        state.Fire("hit", 0);
        Assert.Equal("2", state.Context(Empty, 0)["stacks"]);
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack() with { Decay = 1 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack(), Stack() with { Talent = "other" }]));
    }

    [Fact]
    public void CommittedObliterateStacksFeedCoreAndShareOneTimer()
    {
        // Stack refresh candidate (STACK_REFRESH_SOURCE_REVIEW.md): every crit restarts all stacks' 10 s timer. Before it,
        // the first stack expired on its own at 10 s.
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "gear_talent_obliterate");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithTalentSelection("chest", "talent", rule.Talent);
        state.Fire("crit", 0);
        state.Fire("crit", 1);
        Assert.Equal(2, state.Resolve(pack, build, Empty, 1).Assumptions.Numeric[rule.Key]);
        Assert.True(rule.Refresh);
        Assert.Equal(2, state.Resolve(pack, build, Empty, 10).Assumptions.Numeric[rule.Key]);
        Assert.Equal(0, state.Resolve(pack, build, Empty, 11).Assumptions.Numeric[rule.Key]);
    }

    [Theory]
    // Fast Hands' stacks clear when the reload completes (R37: the source's ReloadedWeapon listener).
    [InlineData("weapon_talent_fast_hands", "reload_end")]
    [InlineData("weapon_talent_breadbasket", "headshot")]
    // The reset-field candidate: a reload start also clears Breadbasket's stacks (the source's ReloadWeaponStarted).
    [InlineData("weapon_talent_breadbasket", "reload")]
    public void CommittedStackRulesClearOnTheirDeclaredEvent(string talent, string clearEvent)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == talent);
        var state = new TimedBuffState([rule]);
        state.Fire(rule.On, 0);
        state.Fire(rule.On, 1);
        Assert.Equal("2", state.Context(Empty, 1)[rule.Key]);
        state.Fire(clearEvent, 2);
        Assert.Equal("0", state.Context(Empty, 2)[rule.Key]);
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void WeaponTagsFilterGainsAndClearsFromOtherWeaponsOnly()
    {
        var state = new TimedBuffState([new() { Talent = "test", Key = "stacks", On = "hit", Kind = "stacks", Max = 5, ConsumeOn = "shot",
            ResetOn = "damaged", WeaponTags = ["pistol"] }]);
        var pistol = new HashSet<string> { "weapon", "pistol" };
        var rifle = new HashSet<string> { "weapon", "rifle" };
        state.Fire("hit", 0, weaponTags: rifle);
        Assert.Equal("0", state.Context(Empty, 0)["stacks"]);
        state.Fire("hit", 1, weaponTags: pistol);
        state.Fire("hit", 2, weaponTags: pistol);
        Assert.Equal("2", state.Context(Empty, 2)["stacks"]);
        // A rifle shot doesn't spend them; a pistol shot does.
        state.Fire("shot", 3, weaponTags: rifle);
        Assert.Equal("2", state.Context(Empty, 3)["stacks"]);
        state.Fire("shot", 4, weaponTags: pistol);
        Assert.Equal("0", state.Context(Empty, 4)["stacks"]);
        // A weapon event with no weapon behind it (a skill or status kill) doesn't count; one that is never a weapon's does.
        state.Fire("hit", 5);
        Assert.Equal("0", state.Context(Empty, 5)["stacks"]);
        state.Fire("hit", 6, weaponTags: pistol);
        state.Fire("damaged", 7);
        Assert.Equal("0", state.Context(Empty, 7)["stacks"]);
    }

    [Fact]
    public void AForcedHeadshotRuleIsReadyForEligibleTaggedWeaponsUntilItsEventSpendsIt()
    {
        var rule = new SimTriggerDefinition { Talent = "resolved", Key = "ready", On = "hit", Kind = "ready", Duration = 2, ForceHeadshot = true, WeaponTags = ["pistol"] };
        var state = new TimedBuffState([rule]);
        var pistol = new HashSet<string> { "pistol" };
        Assert.True(state.ForcesHeadshot(0, null, pistol));
        Assert.False(state.ForcesHeadshot(0, null, new HashSet<string> { "rifle" }));
        Assert.False(state.ForcesHeadshot(0, new HashSet<string> { "other" }, pistol));
        state.Fire("hit", 0, weaponTags: pistol);
        Assert.False(state.ForcesHeadshot(1.5, null, pistol));
        Assert.True(state.ForcesHeadshot(2, null, pistol));
    }

    [Theory]
    [InlineData("buff", "hit")]
    [InlineData("ready", "kill")]
    public void ForcedHeadshotsNeedAReadyRuleSpentByTheShot(string kind, string on)
    {
        var rule = new SimTriggerDefinition { Talent = "resolved", Key = "ready", On = on, Kind = kind, Duration = 2, ForceHeadshot = true };
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => new TimedBuffState([rule])).Code);
    }

    [Fact]
    public void ABuffOrReadyRuleGrantsEachTimeItFiresAndNotOnCooldown()
    {
        var state = new TimedBuffState([new() { Talent = "ablative", Key = "ready", On = "armor_break", Kind = "ready", Duration = 45,
            Grant = "temporary_armor", GrantAmount = 0.8, GrantDuration = 10 }]);
        state.Fire("armor_break", 0);
        Assert.Equal(new TriggerGrant("temporary_armor", 0.8, 10, 0), Assert.Single(state.TakeGrants()));
        state.Fire("armor_break", 30);
        Assert.Empty(state.TakeGrants());
        state.Fire("armor_break", 45);
        Assert.Equal(45, Assert.Single(state.TakeGrants()).Time);
    }

    [Theory]
    [InlineData("stacks", "temporary_armor", 0.5, 10)]
    [InlineData("buff", "healing", 0.5, 10)]
    [InlineData("buff", "temporary_armor", 0, 10)]
    [InlineData("buff", "temporary_armor", 0.5, 0)]
    [InlineData("buff", "shield_repair", 0.5, 10)]
    [InlineData("buff", "", 0.5, 0)]
    [InlineData("buff", "armor_repair", 0.1, 5)]
    [InlineData("buff", "armor_repair", 0.1, 0, "independent")]
    [InlineData("buff", "armor_repair", 0.1, 5, "refresh")]
    [InlineData("buff", "missing_armor_repair", 0.025, 5, "independent")]
    [InlineData("buff", "temporary_armor", 0.5, 10, "independent")]
    public void InvalidGrantsAreRefused(string kind, string grant, double amount, double duration, string instances = "")
    {
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "key",
            On = "hit",
            Kind = kind,
            Max = kind == "stacks" ? 5 : 0,
            Duration = 5,
            Grant = grant,
            GrantAmount = amount,
            GrantDuration = duration,
            GrantInstances = instances
        };
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => new TimedBuffState([rule])).Code);
    }

    // Clutch's shape (R41): a crit repair while armor is below 15% of max, or while a post-kill flag holds.
    private static SimTriggerDefinition ClutchRepair(string whileKey = "") => new()
    { Talent = "clutch", Key = "clutch_repair", On = "crit", Kind = "buff", Duration = 0.1, Grant = "missing_armor_repair", GrantAmount = 0.025, WhileArmorBelow = 0.15, WhileKey = whileKey };

    [Fact]
    public void AnArmorGateFiresOnlyBelowItsShare()
    {
        var state = new TimedBuffState([ClutchRepair()]);
        state.Fire("crit", 1, armorShare: 0.15);
        Assert.Empty(state.TakeGrants());
        state.Fire("crit", 2);
        Assert.Empty(state.TakeGrants());
        state.Fire("crit", 3, armorShare: 0.149);
        var grant = Assert.Single(state.TakeGrants());
        Assert.Equal(("missing_armor_repair", 0.025), (grant.Kind, grant.Share));
    }

    [Fact]
    public void EitherGateOpensARuleThatHasBoth()
    {
        var flag = new SimTriggerDefinition { Talent = "clutch", Key = "clutch_flag", On = "kill", Kind = "buff", Duration = 5 };
        var state = new TimedBuffState([flag, ClutchRepair("clutch_flag")]);
        state.Fire("crit", 1, armorShare: 0.5);
        Assert.Empty(state.TakeGrants());
        state.Fire("kill", 2, armorShare: 0.5);
        state.Fire("crit", 3, armorShare: 0.5);
        Assert.Single(state.TakeGrants());
        // The flag lapsed at 7 s; low armor still opens it.
        state.Fire("crit", 8, armorShare: 0.5);
        Assert.Empty(state.TakeGrants());
        state.Fire("crit", 9, armorShare: 0.1);
        Assert.Single(state.TakeGrants());
    }

    [Fact]
    public void ADurationStatSetsTheDurationFromTheFightsStats()
    {
        // Clutch's window: 4 s plus 1 s per offensive core, here 7 s.
        var flag = new SimTriggerDefinition { Talent = "clutch", Key = "clutch_flag", On = "kill", Kind = "buff", DurationStat = "clutch_window" };
        var state = new TimedBuffState([flag], durations: new Dictionary<string, double> { ["clutch_window"] = 7 });
        state.Fire("kill", 1);
        Assert.True(state.Gates(7.9).Open("clutch_flag", whileFull: false));
        Assert.False(state.Gates(8).Open("clutch_flag", whileFull: false));
        foreach (var bad in new[] { new Dictionary<string, double>(), new() { ["clutch_window"] = 0 }, new() { ["clutch_window"] = 601 } })
        { Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => new TimedBuffState([flag], durations: bad)).Code); }
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() =>
            new TimedBuffState([flag with { Duration = 3 }], durations: new Dictionary<string, double> { ["clutch_window"] = 7 })).Code);
    }

    [Fact]
    public void ATargetsDeathReadsItsMarkThenClearsIt()
    {
        // Doctor Home's shape (R41): the kit reads the mark as it stood before the death, and the death consumes the mark, so the
        // next target starts unmarked.
        var mark = new SimTriggerDefinition { Talent = "doctor_home", Key = "mark", On = "hit", Kind = "buff", Duration = 5, ConsumeOn = "target_death" };
        var kit = new SimTriggerDefinition { Talent = "doctor_home", Key = "kit", On = "target_death", Kind = "buff", Duration = 0.1, WhileKey = "mark", Grant = "armor_repair", GrantAmount = 0.5 };
        var state = new TimedBuffState([mark, kit]);
        state.Fire("hit", 1);
        state.Fire("target_death", 2);
        Assert.Equal("armor_repair", Assert.Single(state.TakeGrants()).Kind);
        Assert.False(state.Gates(2).Open("mark", whileFull: false));
        state.Fire("target_death", 3);
        Assert.Empty(state.TakeGrants());
    }

    // Pummel's shape (R38): three kills activate a 10 s buff, and a partial count drops off 20 s after the last kill.
    private static TimedBuffState Pummel(double window) =>
        new([new() { Talent = "pummel", Key = "buff", On = "kill", Kind = "buff", Duration = 10, Count = 3, CountWindow = window }]);

    [Theory]
    [InlineData(0, 31)]
    [InlineData(20, 40)]
    public void ACountWindowDropsAPartialCountLeftTooLong(double window, double activates)
    {
        // Kills at 0 and 10, then 21 s of quiet: with the window, the kill at 31 starts a new count that completes at 40.
        var state = Pummel(window);
        foreach (var kill in new double[] { 0, 10, 31, 35, 40 })
        {
            state.Fire("kill", kill);
            Assert.Equal(kill >= activates ? "true" : null, state.Context(Empty, kill).GetValueOrDefault("buff"));
            if (kill == activates) { break; }
        }
    }

    [Fact]
    public void ACountWindowEndsExactlyAtItsLength()
    {
        // The source's drop-off timer finishes before a kill at the same instant, so that kill starts a new count.
        var state = Pummel(20);
        state.Fire("kill", 0);
        state.Fire("kill", 1);
        state.Fire("kill", 21);
        Assert.False(state.Context(Empty, 21).ContainsKey("buff"));
        state.Fire("kill", 22);
        state.Fire("kill", 23);
        Assert.Equal("true", state.Context(Empty, 23)["buff"]);
        // The completing kill clears the count; kills during the buff count toward the next activation.
        state.Fire("kill", 24);
        state.Fire("kill", 25);
        state.Fire("kill", 26);
        Assert.Equal(36, state.NextExpiry);
    }

    [Theory]
    [InlineData("ready", 3, 20)]
    [InlineData("buff", 0, 20)]
    [InlineData("buff", 3, -1)]
    public void InvalidCountWindowsAreRefused(string kind, long count, double window)
    {
        var rule = new SimTriggerDefinition { Talent = "pummel", Key = "buff", On = "kill", Kind = kind, Duration = 10, Count = count, CountWindow = window };
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => new TimedBuffState([rule])).Code);
    }

    // Quick Draw (R38): +1 stack per 0.3 s tick while no pistol is in hand, up to 100; the first pistol shot is a headshot
    // and spends them all, hit or miss.
    private static TimedBuffState QuickDraw() => new([new()
    {
        Talent = "quick_draw", Key = "quick_draw_stacks", Kind = "stacks", Max = 100, GainWhileHolstered = 0.3, ConsumeOn = "shot",
        WeaponTags = ["pistol"], ForceHeadshot = true
    }]);

    [Fact]
    public void HolsteredStacksTickWhileNoTaggedWeaponIsInHandAndAPistolShotSpendsThem()
    {
        var pistol = new HashSet<string> { "pistol" };
        var rifle = new HashSet<string> { "rifle" };
        var state = QuickDraw();
        state.Hold(0, rifle);
        // Ticks at 0.3 … 3.0 s: ten stacks by 3.05 s, when the pistol is drawn; drawing stops the gain and keeps them.
        state.Hold(3.05, pistol);
        Assert.Equal("10", state.Context(Empty, 3.05)["quick_draw_stacks"]);
        state.Fire("hit", 9, weaponTags: pistol);
        Assert.Equal("10", state.Context(Empty, 9)["quick_draw_stacks"]);
        Assert.True(state.ForcesHeadshot(9, null, pistol));
        Assert.False(state.ForcesHeadshot(9, null, rifle));
        // A rifle shot doesn't spend them; the first pistol shot does.
        state.Fire("shot", 9.5, weaponTags: rifle);
        Assert.Equal("10", state.Context(Empty, 9.5)["quick_draw_stacks"]);
        state.Fire("shot", 10, weaponTags: pistol);
        Assert.Equal("0", state.Context(Empty, 10)["quick_draw_stacks"]);
        Assert.False(state.ForcesHeadshot(10, null, pistol));
    }

    [Fact]
    public void HolsteredStacksCapAndTheirUptimeCountsEachTick()
    {
        var state = QuickDraw();
        state.Hold(0, new HashSet<string> { "rifle" });
        state.Fire("shot", 40, weaponTags: new HashSet<string> { "rifle" });
        Assert.Equal("100", state.Context(Empty, 40)["quick_draw_stacks"]);
        // One stack from 0.3 s, two from 0.6 s, … 100 from 30 s, then 100 until 40 s.
        var stackSeconds = Enumerable.Range(1, 99).Sum(count => count * 0.3) + 100 * (40 - 30.0);
        Assert.Equal(stackSeconds, state.Measure(40)["quick_draw_stacks"].StackSeconds!.Value, 6);
    }

    [Theory]
    [InlineData("buff", 0.3, "pistol")]
    [InlineData("stacks", -1, "pistol")]
    [InlineData("stacks", 0.3, "")]
    public void InvalidHolsteredGainsAreRefused(string kind, double period, string tag)
    {
        var rule = new SimTriggerDefinition
        {
            Talent = "quick_draw",
            Key = "key",
            Kind = kind,
            Max = kind == "stacks" ? 100 : 0,
            Duration = kind == "stacks" ? 0 : 5,
            GainWhileHolstered = period,
            WeaponTags = tag.Length == 0 ? [] : [tag]
        };
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => new TimedBuffState([rule])).Code);
    }

    [Fact]
    public void RefreshExtendsActivityAndExactExpiryTurnsItOff()
    {
        var state = new TimedBuffState([Rule()]);
        Assert.False(state.Context(Empty.Add("buff", "true"), 0).ContainsKey("buff"));
        state.Fire("hit", 1);
        Assert.Equal(4, state.NextExpiry);
        state.Fire("hit", 3);
        Assert.Equal(6, state.NextExpiry);
        Assert.Equal("true", state.Context(Empty, 5)["buff"]);
        Assert.False(state.Context(Empty, 6).ContainsKey("buff"));
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void SharedKeysUseTheLatestExpiryWithoutShortening()
    {
        var state = new TimedBuffState([Rule(10), Rule(2) with { On = "kill" }]);
        state.Fire("hit", 0);
        state.Fire("kill", 1);
        Assert.Equal(10, state.NextExpiry);
        Assert.Equal("true", state.Context(Empty.Add("other", "kept"), 4)["buff"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTimeCannotMutateState(double time)
    {
        var state = new TimedBuffState([Rule()]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Fire("hit", time));
        Assert.Null(state.NextExpiry);
        Assert.False(state.Context(Empty, 0).ContainsKey("buff"));
    }

    [Fact]
    public void UnsupportedSideEffectsAndKindsAreNotSilentlyDropped()
    {
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Kind = "status" }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Within = 7 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { Pool = "shared" }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule(-1)]));
    }

    [Fact]
    public void SwappingWhileActiveCancelsTheBuffAndLocksItOut()
    {
        // Gunslinger: a swap gains the 5 s buff; a swap while it is active ends it and blocks it for 5 s.
        var state = new TimedBuffState([new() { Talent = "test", Key = "buff", On = "swapped", Kind = "buff", Duration = 5, CancelOn = "swapped", CancelFor = 5 }]);
        state.Fire("swapped", 0);
        Assert.True(state.Context(Empty, 1).ContainsKey("buff"));
        state.Fire("swapped", 2);
        Assert.False(state.Context(Empty, 2).ContainsKey("buff"));
        // Locked out until 7: a swap at 4 gains nothing, one at 7 gains again.
        state.Fire("swapped", 4);
        Assert.False(state.Context(Empty, 4).ContainsKey("buff"));
        state.Fire("swapped", 7);
        Assert.True(state.Context(Empty, 8).ContainsKey("buff"));
        // After the buff has run out on its own, a swap simply gains it.
        state.Fire("swapped", 20);
        Assert.True(state.Context(Empty, 21).ContainsKey("buff"));
        Assert.Equal(new TimedTriggerDuration(2 + 5 + 5, null), state.Measure(30)["buff"]);
    }

    [Fact]
    public void ReachingThePoolCapBanksEachRulesStacksUntilTheNextCap()
    {
        // Rebalance: red and blue stacks share an 80-stack pool; at the cap both clear into their banks.
        var red = PoolRule("red", "hit") with { Bank = "red_banked" };
        var blue = PoolRule("blue", "damaged") with { Bank = "blue_banked" };
        var state = new TimedBuffState([red, blue]);
        Assert.Equal("0", state.Context(Empty, 0)["red_banked"]);
        for (var hit = 0; hit < 6; hit++) { state.Fire("hit", hit); }
        for (var damage = 0; damage < 3; damage++) { state.Fire("damaged", 10 + damage); }
        Assert.Equal(("6", "3", "0"), (state.Context(Empty, 13)["red"], state.Context(Empty, 13)["blue"], state.Context(Empty, 13)["red_banked"]));
        state.Fire("damaged", 14);
        var capped = state.Context(Empty, 14);
        Assert.Equal(("0", "0", "6", "4"), (capped["red"], capped["blue"], capped["red_banked"], capped["blue_banked"]));
        // The next cap replaces the banks rather than adding to them.
        for (var hit = 0; hit < 9; hit++) { state.Fire("hit", 20 + hit); }
        state.Fire("damaged", 30);
        var again = state.Context(Empty, 30);
        Assert.Equal(("9", "1"), (again["red_banked"], again["blue_banked"]));
    }

    [Fact]
    public void ClearingStacksGrantsPerStackRemoved()
    {
        // Full Stop: reloading clears the stacks and grants 5% of max armor per stack, for 10 s.
        var fullStop = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "full_stop",
            On = "hit",
            Kind = "stacks",
            Max = 20,
            ResetOn = "reload",
            ClearGrant = "temporary_armor",
            ClearAmount = 0.05,
            ClearDuration = 10,
            Also = [new() { On = "headshot", Gain = 2 }]
        };
        // Liberty or Death: a headshot consumes the stacks and repairs the shield 3% per stack.
        var liberty = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "liberty",
            On = "body_hit",
            Kind = "stacks",
            Max = 30,
            ConsumeOn = "headshot",
            ClearGrant = "shield_repair",
            ClearAmount = 0.03
        };
        var state = new TimedBuffState([fullStop, liberty]);
        // A reload with no stacks grants nothing.
        state.Fire("reload", 0);
        Assert.Empty(state.TakeGrants());
        for (var hit = 1; hit <= 3; hit++) { state.Fire("hit", hit); state.Fire("body_hit", hit); }
        state.Fire("headshot", 4);
        // The headshot consumed three Liberty stacks; Full Stop gained two more.
        var shield = Assert.Single(state.TakeGrants());
        Assert.Equal(("shield_repair", 0.09, 0.0, 4.0), (shield.Kind, shield.Share, shield.Duration, shield.Time), new ShareComparer());
        state.Fire("reload", 5);
        var armor = Assert.Single(state.TakeGrants());
        Assert.Equal(("temporary_armor", 0.25, 10.0, 5.0), (armor.Kind, armor.Share, armor.Duration, armor.Time), new ShareComparer());
        Assert.Equal("0", state.Context(Empty, 5)["full_stop"]);
        Assert.Empty(state.TakeGrants());
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { ClearGrant = "temporary_armor", ClearAmount = 0.05, ClearDuration = 10 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([fullStop with { ClearDuration = 0 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([liberty with { ClearDuration = 5 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([liberty with { ClearAmount = 1.5 }]));
    }

    private sealed class ShareComparer : IEqualityComparer<(string, double, double, double)>
    {
        public bool Equals((string, double, double, double) x, (string, double, double, double) y) =>
            x.Item1 == y.Item1 && Math.Abs(x.Item2 - y.Item2) < 1e-12 && x.Item3 == y.Item3 && x.Item4 == y.Item4;
        public int GetHashCode((string, double, double, double) obj) => obj.Item1.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAnEventCreditedToTheTalentCancelsItsBuff()
    {
        var state = new TimedBuffState([new() { Talent = "weapon", Key = "buff", On = "hit", Kind = "buff", Duration = 10, CancelOn = "kill", CancelFor = 5 }]);
        state.Fire("hit", 0);
        // A status kill is credited to gear talents only, so the weapon talent's buff stays.
        state.Fire("kill", 1, eligibleTalents: new HashSet<string>(["gear"]));
        Assert.True(state.Context(Empty, 2).ContainsKey("buff"));
        state.Fire("kill", 2);
        Assert.False(state.Context(Empty, 2).ContainsKey("buff"));
    }

    [Fact]
    public void ACountedResetCannotCarryAClearGrantButACountedConsumeCan()
    {
        var counted = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "stacks",
            On = "hit",
            Kind = "stacks",
            Max = 5,
            Count = 3,
            ClearGrant = "temporary_armor",
            ClearAmount = 0.05,
            ClearDuration = 10
        };
        Assert.Throws<SimulationException>(() => new TimedBuffState([counted with { ResetOn = "reload" }]));
        var state = new TimedBuffState([counted with { ConsumeOn = "reload" }]);
        for (var hit = 0; hit < 3; hit++) { state.Fire("hit", hit); }
        state.Fire("reload", 3);
        Assert.Equal(0.05, Assert.Single(state.TakeGrants()).Share, 12);
    }

    [Fact]
    public void ABankHoldsTheRulesOwnMaxWhichCanExceedThePoolCap()
    {
        // From 5 stacks a gain of 5 reaches 10, past the pool cap of 6: the bank takes all 10.
        var state = new TimedBuffState([PoolRule("a", "hit") with { Max = 10, Gain = 5, PoolMax = 6, Bank = "a_banked" }]);
        state.Fire("hit", 0);
        state.Fire("hit", 1);
        Assert.Equal("10", state.Context(Empty, 1)["a_banked"]);
    }

    [Fact]
    public void CancelAndBankRulesRefuseUnsupportedShapes()
    {
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack() with { CancelOn = "swapped" }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Rule() with { CancelFor = 5 }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([Stack() with { Bank = "banked" }]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([PoolRule("a", "hit") with { Bank = "b" }, PoolRule("b", "crit")]));
        Assert.Throws<SimulationException>(() => new TimedBuffState([PoolRule("a", "hit") with { Bank = "x" }, PoolRule("b", "crit") with { Bank = "x" }]));
    }

    [Fact]
    public void NonadvancingExpiryRefusesTheWholeEvent()
    {
        var state = new TimedBuffState([Rule(1)]);
        Assert.Throws<SimulationException>(() => state.Fire("hit", 1e100));
        state.Fire("hit", 0);
        Assert.Equal(1, state.NextExpiry);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Context(Empty, -1));
    }

    [Fact]
    public void CommittedCombinedArmsReResolvesSkillDamageOnActivationAndExpiry()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "gear_talent_combined_arms");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", rule.Talent);
        var before = state.Resolve(pack, build, Empty, 0).Calculation.Stats["total_skill_damage"];
        state.Fire("hit", 0);
        var during = state.Resolve(pack, build, Empty, 0).Calculation.Stats["total_skill_damage"];
        Assert.Equal(0.25, during - before, 8);
        Assert.Equal(before, state.Resolve(pack, build, Empty, 3).Calculation.Stats["total_skill_damage"]);
    }
}
