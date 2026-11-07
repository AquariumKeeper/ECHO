using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class StatusTriggerStateTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;
    private static CompiledPackExpressions Pack()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        return Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
    }

    private static StatusTriggerState UptimeState()
    {
        var original = Pack();
        var rules = original.Pack.Simulation.Triggers.Where(rule => rule.Talent is "weapon_talent_ignited" or "weapon_talent_sadist")
            .Select(rule => rule with { On = "shot", Count = 0 }).ToImmutableArray();
        var pack = original with { Pack = original.Pack with { Simulation = original.Pack.Simulation with { Triggers = rules } } };
        return new StatusTriggerState(pack, rules);
    }

    [Fact]
    public void UptimeClipsAtExpiryAndUnionsOverlappingStatuses()
    {
        var state = UptimeState();
        state.Fire("shot", 0);
        state.Fire("hit", 1);
        var seconds = state.UptimeSeconds(20);
        Assert.Equal(7, seconds["burning"]);
        Assert.Equal(10, seconds["bleeding"]);
        Assert.Equal(10, seconds["any"]);
        Assert.Equal(0, seconds["shocked"]);
        Assert.Equal(7, seconds.Count);
    }

    [Fact]
    public void UptimeRefreshCountsOnlyTheUnionAndReadsDoNotCommitFutureTime()
    {
        var state = UptimeState();
        state.Fire("shot", 0);
        state.Fire("hit", 1);
        state.UptimeSeconds(100);
        state.Fire("shot", 2);
        state.Fire("hit", 3);
        Assert.Equal(4, state.UptimeSeconds(5)["burning"]);
        Assert.Equal(9, state.UptimeSeconds(20)["burning"]);
        Assert.Equal(12, state.UptimeSeconds(20)["any"]);
        Assert.Equal(4, state.UptimeSeconds(5)["any"]);
    }

    [Fact]
    public void UptimeExcludesDeadTargetsAndRetargetGaps()
    {
        var state = UptimeState();
        state.Fire("shot", 0);
        state.Fire("hit", 1);
        state.ResetTarget(2.5);
        state.Fire("shot", 5);
        state.Fire("hit", 6);
        Assert.Equal(2.5, state.UptimeSeconds(7)["any"]);
        state.ResetTarget(7);
        Assert.Equal(2.5, state.UptimeSeconds(100)["any"]);
    }

    [Fact]
    public void FailedStatusEventDoesNotAccumulateOrChangeUptime()
    {
        // A status going on reads the factor, so that's what can fail. The first has lapsed, and keeps the uptime it earned.
        var state = UptimeState();
        state.Fire("shot", 0);
        state.Fire("hit", 1);
        state.Fire("shot", 30);
        var earned = state.UptimeSeconds(30)["any"];
        Assert.True(earned > 0);
        Assert.Throws<SimulationException>(() => state.Fire("hit", 31, double.MaxValue));
        Assert.Equal(earned, state.UptimeSeconds(30.5)["any"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.UptimeSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.UptimeSeconds(double.NaN));
    }

    // ShadeTech's Dragon's Glare draft (R36): the default burn on the current target every 35 s, on whole seconds.
    private static readonly SimTriggerDefinition Glare = new()
    { Talent = "gear_talent_dragon_s_glare", Key = "target_burning", On = "second", Kind = "status", Duration = 7, Every = 35, Status = "burning" };

    [Fact]
    public void ASkillsStatusKeepsItsFirstAmountAndTwoUsesDontAdd()
    {
        var state = With(Glare);
        Assert.Equal(1, state.DealSkillStatus("burning", 10, 0, 0.1, 5, true, out var first), 9);
        Assert.True(first);
        // A use with more behind it is the same burn, at the first amount, and no new application.
        Assert.Equal(1, state.DealSkillStatus("burning", 20, 0.1, 0.2, 5, true, out var second), 9);
        Assert.False(second);
        // The same moment from another use has already been dealt.
        Assert.Equal(0, state.DealSkillStatus("burning", 20, 0.1, 0.2, 9, true, out _));
        // That use still extended it, and the first amount goes on.
        Assert.Equal(1, state.DealSkillStatus("burning", 20, 8, 8.1, 9, false, out _), 9);
        Assert.DoesNotContain("burning", state.Active(9));
    }

    [Fact]
    public void ASkillsStatusRunsOnOnlyOnAnEnemyItLandedOn()
    {
        var state = With(Glare);
        // Too late to land: nothing is on this enemy, so the tail deals nothing and puts nothing on.
        Assert.Equal(0, state.DealSkillStatus("burning", 10, 0, 0.1, 5, false, out var missed));
        Assert.False(missed);
        Assert.Empty(state.Active(0.1));
        Assert.Equal(1, state.DealSkillStatus("burning", 10, 0.1, 0.2, 5, true, out _), 9);
        Assert.Equal(1, state.DealSkillStatus("burning", 10, 0.2, 0.3, 5, false, out _), 9);
        // The next enemy has none of it.
        state.Hold(new TargetStatuses(), 0.3);
        Assert.Equal(0, state.DealSkillStatus("burning", 10, 0.3, 0.4, 5, false, out _));
    }

    [Fact]
    public void ASkillsStatusWaitsForATalentsAndStartsAfter()
    {
        var state = With(Glare);
        Assert.True(state.Fire("second", 1));
        var ends = state.Application("burning")!.Value.Until;
        // While the talent's runs the skill's deals nothing, and it's no application: the status is already on.
        Assert.Equal(0, state.DealSkillStatus("burning", 10, 1.5, 1.6, ends + 5, true, out var waiting));
        Assert.False(waiting);
        // The moment it ends the skill's runs, on its own, even past the use's applying, and that's a new application.
        Assert.Equal(1, state.DealSkillStatus("burning", 10, ends, ends + 0.1, ends + 5, false, out var started), 9);
        Assert.True(started);
        Assert.Equal(1, state.DealSkillStatus("burning", 10, ends + 0.1, ends + 0.2, ends + 5, false, out var running), 9);
        Assert.False(running);
    }

    [Fact]
    public void ASkillsStatusTakesOverPartWayThroughABatch()
    {
        var state = With(Glare);
        Assert.True(state.Fire("second", 1));
        var ends = state.Application("burning")!.Value.Until;
        // The talent's ends 0.04 s into the batch, so the skill's deals only the 0.06 s after.
        Assert.Equal(0.6, state.DealSkillStatus("burning", 10, ends - 0.04, ends + 0.06, ends + 5, true, out var started), 9);
        Assert.True(started);
    }

    private static StatusTriggerState With(SimTriggerDefinition rule)
    {
        var original = Pack();
        return new StatusTriggerState(original with { Pack = original.Pack with { Simulation = original.Pack.Simulation with { Triggers = [rule] } } }, [rule]);
    }

    [Fact]
    public void ASecondAppliesItsStatusDirectlyAndWaitsOutTheCooldown()
    {
        var state = With(Glare);
        Assert.True(state.Fire("second", 1));
        Assert.Contains("burning", state.Active(1));
        Assert.Equal(8, state.NextExpiry);
        // A hit neither applies nor re-arms it.
        Assert.False(state.Fire("hit", 2));
        for (var second = 2; second < 36; second++) { Assert.False(state.Fire("second", second)); }
        Assert.True(state.Fire("second", 36));
        Assert.Equal(43, state.NextExpiry);
    }

    [Fact]
    public void WithNoTargetASecondAppliesNothingAndLeavesTheCooldownUnspent()
    {
        var state = With(Glare with { Count = 2 });
        Assert.False(state.Fire("second", 1, targetPresent: false));
        Assert.False(state.Fire("second", 2));
        Assert.True(state.Fire("second", 3));
        Assert.Equal(10, state.NextExpiry);
    }

    [Fact]
    public void AnArmingRuleWithACooldownArmsAtMostOncePerCooldown()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0, Every = 10 };
        var state = With(rule);
        state.Fire("shot", 0);
        Assert.True(state.Fire("hit", 1));
        state.Fire("shot", 2);
        Assert.False(state.Fire("hit", 3));
        state.Fire("shot", 10);
        Assert.True(state.Fire("hit", 11));
    }

    [Fact]
    public void ARunningStatusKeepsTheAmountItStartedWith()
    {
        var rule = Pack().Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0, Every = 0, Duration = 5 };
        var state = With(rule);
        var dots = new StatusDamageState([new() { Status = "burning", Tick = "tick", Period = 1 }]);
        state.Fire("shot", 0);
        state.Fire("hit", 1);
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 10 });
        // A refresh under a bigger bonus (Perpetuation's) extends the burn and changes no tick.
        state.Fire("shot", 2);
        state.Fire("hit", 3);
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 99 });
        Assert.Equal(10, dots.Take(2).Damage);
        Assert.Equal(10, dots.Take(3).Damage);
        // A new burn takes the amount of its own moment.
        state.Fire("shot", 20);
        state.Fire("hit", 21);
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 99 });
        Assert.Equal(99, dots.Take(22).Damage);
    }

    [Fact]
    public void ASkillsStatusIsOnTheTargetAndStartsNoTicks()
    {
        var rule = Pack().Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0, Every = 0, Duration = 5 };
        var state = With(rule);
        var dots = new StatusDamageState([new() { Status = "burning", Tick = "tick", Period = 1 }]);
        // Going on is an application, and the next batch of that use isn't.
        Assert.True(state.ApplyFromSkill("burning", 4, 0.9, 1, rateIsStatus: true));
        Assert.False(state.ApplyFromSkill("burning", 4, 1.9, 2, rateIsStatus: true));
        // The batch ending with the window began inside it.
        Assert.False(state.ApplyFromSkill("burning", 4, 3.9, 4 - 0.01, rateIsStatus: true));
        Assert.Contains("burning", state.Active(2));
        Assert.Equal(4, state.NextExpiry);
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 10 });
        Assert.Null(dots.NextTick);
        // While the skill's rate is the burn, a talent's on that enemy starts nothing, and extends nothing.
        state.Fire("shot", 3.99);
        Assert.False(state.Fire("hit", 3.995));
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 10 });
        Assert.Null(dots.NextTick);
        Assert.DoesNotContain("burning", state.Active(4));
        // Once that rate has stopped, the rule, still armed, starts ticks on the next hit, and a further one only refreshes.
        Assert.True(state.Fire("hit", 5));
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 10 });
        Assert.NotNull(dots.NextTick);
        state.Fire("shot", 5.5);
        Assert.False(state.Fire("hit", 6));
        // A skill's status while a talent's runs is no new application, and it's gone at the end of both.
        Assert.False(state.ApplyFromSkill("burning", 7, 6.4, 6.5, rateIsStatus: true));
        Assert.DoesNotContain("burning", state.Active(11));
        Assert.True(state.ApplyFromSkill("burning", 12, 11, 11.1, rateIsStatus: true));
        // Another enemy has none.
        state.Hold(new TargetStatuses(), 11);
        Assert.DoesNotContain("burning", state.Active(11));
        // A target's death takes it.
        state.ApplyFromSkill("burning", 20, 11.9, 12, rateIsStatus: true);
        state.ResetTarget(13);
        Assert.DoesNotContain("burning", state.Active(13));
    }

    [Fact]
    public void ARateThatOnlyCarriesAStatusLeavesATalentsTicksAlone()
    {
        // A Stinger Hive's rate is its stings, not its bleed, so a talent's status on that enemy still ticks. It isn't a new application.
        var rule = Pack().Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0, Every = 0, Duration = 5 };
        var state = With(rule);
        var dots = new StatusDamageState([new() { Status = "burning", Tick = "tick", Period = 1 }]);
        Assert.True(state.ApplyFromSkill("burning", 4, 0.9, 1, rateIsStatus: false));
        state.Fire("shot", 1.5);
        Assert.False(state.Fire("hit", 2));
        Assert.True(state.Started);
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 10 });
        Assert.NotNull(dots.NextTick);
    }

    [Fact]
    public void ARateThatIsTheStatusLeavesAWaitingRuleItsCooldown()
    {
        // Dragon's Glare's shape: a burn on a whole second every 35 s. One due during the rate waits, with its cooldown unspent.
        var state = With(Glare with { Count = 0, Every = 35 });
        state.ApplyFromSkill("burning", 2, 0.9, 1, rateIsStatus: true);
        Assert.False(state.Fire("second", 1));
        Assert.True(state.Fire("second", 3));
        Assert.False(state.Fire("second", 4));
    }

    [Fact]
    public void AShorterRateStillCountsBesideALongerCarriedStatus()
    {
        var rule = Pack().Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0, Every = 0, Duration = 5 };
        var state = With(rule);
        var dots = new StatusDamageState([new() { Status = "burning", Tick = "tick", Period = 1 }]);
        state.ApplyFromSkill("burning", 9, 0.9, 1, rateIsStatus: false);
        state.ApplyFromSkill("burning", 4, 0.9, 1, rateIsStatus: true);
        state.Fire("shot", 1.5);
        state.Fire("hit", 2);
        dots.Sync(state, new Dictionary<string, double> { ["burning"] = 10 });
        Assert.Null(dots.NextTick);
    }

    [Fact]
    public void RefreshingARunningStatusIsNotAnApplication()
    {
        var rule = Pack().Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0, Every = 0, Duration = 5 };
        var state = With(rule);
        state.Fire("shot", 0);
        Assert.True(state.Fire("hit", 1));
        // Still on until 6: this extends it to 8, and isn't an application. A bonus now doesn't reach it: it reads the factor it started with.
        state.Fire("shot", 2);
        Assert.False(state.Fire("hit", 3, 1.75));
        Assert.Equal((3d, 8d), state.Application("burning"));
        // On at its last moment, it has ended, so this goes on afresh.
        state.Fire("shot", 7);
        Assert.True(state.Fire("hit", 8));
        // Another enemy has no burn yet.
        state.Hold(new TargetStatuses(), 9);
        state.Fire("shot", 9);
        Assert.True(state.Fire("hit", 10));
    }

    [Theory]
    [InlineData("", 0, 5)]
    [InlineData("second", 7, -1)]
    [InlineData("second", 7, double.PositiveInfinity)]
    public void InvalidCooldownsAreRefused(string on, double duration, double every)
    {
        var rule = Glare with { On = on, Duration = duration, Every = every };
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => With(rule)).Code);
    }

    [Fact]
    public void AnApplyOnHitRuleAppliesOnItsOwnEventAndNeverArms()
    {
        // Trauma's blind (R38): the headshot that blinds applies it; a later hit doesn't.
        var rule = new SimTriggerDefinition { Talent = "gear_talent_trauma", Key = "target_blinded", On = "headshot", Kind = "status", Duration = 7, Every = 30, Status = "blinded", ApplyOnHit = true };
        var state = With(rule);
        Assert.True(state.Fire("headshot", 1));
        Assert.Equal(8, state.NextExpiry);
        Assert.False(state.Fire("hit", 2));
        Assert.False(state.Fire("headshot", 10));
        Assert.True(state.Fire("headshot", 31));
    }

    [Fact]
    public void AGatedRuleReactsOnlyWhileItsKeyIsOpen()
    {
        // Ardent's burn (R39): applied on a hit only while the heat meter was full; without gates it never applies.
        var rule = new SimTriggerDefinition { Talent = "weapon_talent_ignited", Key = "target_burning", On = "hit", Kind = "status", Duration = 7, Status = "burning", ApplyOnHit = true, WhileKey = "heat", WhileFull = true };
        var state = With(rule);
        var meter = new TimedBuffState([new() { Talent = "ardent", Key = "heat", On = "shot", Kind = "stacks", Max = 2 }]);
        Assert.False(state.Fire("hit", 1));
        meter.Fire("shot", 1);
        Assert.False(state.Fire("hit", 1, gates: meter.Gates(1)));
        meter.Fire("shot", 2);
        Assert.True(state.Fire("hit", 2, gates: meter.Gates(2)));
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => With(rule with { WhileKey = "" })).Code);
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("shot")]
    [InlineData("second")]
    public void ApplyOnHitNeedsAHitEvent(string on)
    {
        var rule = new SimTriggerDefinition { Talent = "gear_talent_trauma", Key = "target_blinded", On = on, Kind = "status", Duration = 7, Status = "blinded", ApplyOnHit = true };
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => With(rule)).Code);
    }

    [Fact]
    public void TalentDebuffsStartOncePerTargetAndClearWithIt()
    {
        var state = new StatusDamageState([], [new() { Talent = "toxic", On = "status_applied", Total = "total", Duration = 10, Period = 0.5 }]);
        var totals = new Dictionary<string, double> { ["total"] = 1000 };
        // Not for another talent, nor without a living target.
        Assert.False(state.StartTalents("status_applied", 0, new HashSet<string> { "other" }, totals, true));
        Assert.False(state.StartTalents("status_applied", 0, null, totals, false));
        Assert.True(state.StartTalents("status_applied", 1, null, totals, true));
        Assert.False(state.StartTalents("status_applied", 2, null, totals, true));
        Assert.Equal(1.5, state.NextTick);
        Assert.Equal(("toxic", 50.0, true), state.Take(1.5));
        state.ResetTalents();
        Assert.Null(state.NextTick);
        Assert.True(state.StartTalents("status_applied", 3, null, totals, true));
        // Twenty ticks, the last at the end of the 10 s, then it ends.
        for (var tick = 1; tick <= 20; tick++) { state.Take(3 + tick * 0.5); }
        Assert.Null(state.NextTick);
    }

    [Fact]
    public void ScopedSkillKillsCannotArmWeaponOwnedStatuses()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited");
        var state = new StatusTriggerState(pack, [rule]);
        for (var count = 0; count < rule.Count; count++) { state.Fire("kill", 0, eligibleTalents: new HashSet<string>()); }
        Assert.False(state.Fire("hit", 1));
        for (var count = 0; count < rule.Count; count++) { state.Fire("kill", 1); }
        Assert.True(state.Fire("hit", 2));
        Assert.Contains("burning", state.Active(2));
    }

    [Theory]
    [InlineData("weapon_talent_ignited", "burning", 7)]
    [InlineData("weapon_talent_sadist", "bleeding", 10)]
    [InlineData("weapon_talent_thunder_strike", "shocked", 5)]
    [InlineData("weapon_talent_immobilize", "ensnared", 7)]
    [InlineData("weapon_talent_eyeless", "blinded", 7)]
    public void CommittedKillCountsArmTheNextHitAndExpireExactly(string talent, string status, double duration)
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == talent);
        var state = new StatusTriggerState(pack, [rule]);
        Assert.False(state.Context(Empty.Add(rule.Key, "true"), 0).ContainsKey(rule.Key));
        for (var kill = 0; kill < rule.Count; kill++) { Assert.False(state.Fire("kill", 0)); }
        Assert.Empty(state.Active(0));
        Assert.True(state.Fire("hit", 1));
        Assert.Contains(status, state.Active(1));
        Assert.Equal(1 + duration, state.NextExpiry);
        Assert.Contains(status, state.Active(Math.BitDecrement(1 + duration)));
        Assert.Empty(state.Active(1 + duration));
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void DurationScalingAndRefreshNeverShortenAnExistingStatus()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited");
        var state = new StatusTriggerState(pack, [rule]);
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 0); }
        state.Fire("hit", 1, 2);
        Assert.Equal(15, state.NextExpiry);
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 2); }
        // The refresh uses the factor the status started with, not this hit's lower one, so 3 + 7 x 2.
        state.Fire("hit", 3);
        Assert.Equal(17, state.NextExpiry);
    }

    [Fact]
    public void CommittedIgnitedStatusRaisesDamageThroughCore()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited");
        var state = new StatusTriggerState(pack, [rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", rule.Talent);
        var context = Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var before = state.Resolve(build, context, 0).Calculation.Stats["equipped_weapon_damage"];
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 0); }
        state.Fire("hit", 1);
        Assert.True(state.Resolve(build, context, 1).Calculation.Stats["equipped_weapon_damage"] > before);
        Assert.Equal(before, state.Resolve(build, context, 8).Calculation.Stats["equipped_weapon_damage"]);
    }

    [Fact]
    public void AStatusArmedEarlierInTheSameShotAppliesOnlyOnALaterHit()
    {
        var committed = Pack();
        var rule = committed.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited") with { On = "shot", Count = 0 };
        var pack = committed with { Pack = committed.Pack with { Simulation = committed.Pack.Simulation with { Triggers = [.. committed.Pack.Simulation.Triggers, rule] } } };
        var state = new StatusTriggerState(pack, [rule]);
        state.Fire("shot", 1);
        Assert.False(state.Fire("hit", 1));
        Assert.True(state.Fire("hit", 2));
    }

    [Fact]
    public void ReadingStatusesNeverMovesTheEventClock()
    {
        var pack = Pack();
        var state = new StatusTriggerState(pack, [pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited")]);
        Assert.Empty(state.Active(10));
        state.Fire("kill", 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Fire("kill", 4));
    }

    [Fact]
    public void AnEventBeforeAnEarlierReadRestartsTheExpiryWatermark()
    {
        var pack = Pack();
        var state = new StatusTriggerState(pack, [pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited")]);
        Assert.Empty(state.Active(20));
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 5); }
        Assert.True(state.Fire("hit", 6));
        Assert.Contains("burning", state.Active(7));
        Assert.Equal(13, state.NextExpiry);
        Assert.Empty(state.Active(13));
        Assert.Null(state.NextExpiry);
        // Clearing the target is an event too, so it also restarts the watermark.
        state.Active(30);
        state.ResetTarget(14);
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 14); }
        state.Fire("hit", 15);
        Assert.Equal(22, state.NextExpiry);
    }

    [Fact]
    public void RetargetClearsStatusesAndKeepsCountersAndArming()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited");
        var state = new StatusTriggerState(pack, [rule]);
        state.Fire("kill", 0);
        state.Fire("kill", 0);
        state.ResetTarget(0);
        state.Fire("kill", 1);
        state.Fire("kill", 1);
        state.ResetTarget(1);
        Assert.True(state.Fire("hit", 2));
        state.ResetTarget(3);
        Assert.Empty(state.Active(3));
        Assert.Null(state.NextExpiry);
    }

    [Fact]
    public void ObserversReadAnyStatusAndSingleTargetCounts()
    {
        var pack = Pack();
        var rules = pack.Pack.Simulation.Triggers.Where(rule => rule.Kind == "status" && rule.Talent is "weapon_talent_sadist" or "weapon_talent_pressure_point" or "gear_talent_bleeding_edge");
        var state = new StatusTriggerState(pack, rules);
        Assert.Equal("0", state.Context(Empty.Add("bleeding_edge_bleeding_enemies", "5"), 0)["bleeding_edge_bleeding_enemies"]);
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 0); }
        state.Fire("hit", 1);
        var context = state.Context(Empty, 1);
        Assert.Equal("true", context["target_status_affected"]);
        Assert.Equal("1", context["bleeding_edge_bleeding_enemies"]);
        Assert.Equal("0", state.Context(Empty, 11)["bleeding_edge_bleeding_enemies"]);
    }

    [Fact]
    public void FailedExpiryDoesNotConsumeTheArmedStatusOrClock()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited");
        var state = new StatusTriggerState(pack, [rule]);
        for (var kill = 0; kill < 4; kill++) { state.Fire("kill", 0); }
        Assert.Throws<SimulationException>(() => state.Fire("hit", 1e100));
        Assert.Empty(state.Active(0));
        Assert.True(state.Fire("hit", 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScalingIsRefused(double factor) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusTriggerState(Pack(), []).Fire("hit", 0, factor));

    [Fact]
    public void UnknownRulesSideEffectsAndInvalidTimeAreRefused()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ignited");
        Assert.Throws<SimulationException>(() => new StatusTriggerState(pack, [rule with { Refill = true }]));
        Assert.Throws<SimulationException>(() => new StatusTriggerState(pack, [rule with { Status = "unknown" }]));
        var state = new StatusTriggerState(pack, [rule]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Active(-1));
        state.Fire("kill", 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ResetTarget(0));
    }

    [Fact]
    public void WhileStatusChecksBeforeCountingAndCommittedVindictiveUsesCore()
    {
        var gate = new TimedBuffState([new() { Talent = "test", Key = "buff", On = "hit", Kind = "buff", Duration = 3, Count = 2, Refill = true, WhileStatus = "any" }]);
        Assert.False(gate.Fire("hit", 0));
        Assert.False(gate.Fire("hit", 1, ImmutableHashSet.Create("burning")));
        Assert.False(gate.Fire("hit", 2));
        Assert.True(gate.Fire("hit", 3, ImmutableHashSet.Create("burning")));
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_vindictive");
        var state = new TimedBuffState([rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", rule.Talent);
        var context = Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var before = state.Resolve(pack, build, context, 0).Calculation.Stats["critical_hit_chance"];
        state.Fire("kill", 0);
        Assert.False(state.Context(Empty, 0).ContainsKey(rule.Key));
        state.Fire("kill", 1, ImmutableHashSet.Create("bleeding"));
        Assert.True(state.Resolve(pack, build, context, 1).Calculation.Stats["critical_hit_chance"] > before);
    }
}
