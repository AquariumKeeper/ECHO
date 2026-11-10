using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class SkillDamageScheduleTests
{
    private static SkillDamageSchedule Schedule(bool fromEnd = false, double cooldown = 3, double window = 2, double count = 1)
    {
        var pack = new GamePack
        {
            ActiveSkillSlots = [new() { ID = "slot" }],
            Simulation = new() { Skills = [new() { Skill = "skill", Window = "window", Cooldown = "cooldown", Rate = "rate", Impact = "impact", Impacts = "count", CooldownFromEnd = fromEnd }] }
        };
        SkillPreviewOutput Output(string id, double value) => new(id, id, "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, value, "");
        return SkillDamageSchedule.Create(pack, ImmutableDictionary<string, SkillPreview>.Empty.Add("slot",
            new("skill", [Output("window", window), Output("cooldown", cooldown), Output("rate", 10), Output("impact", 100), Output("count", count)])));
    }

    private static SkillDamageSchedule Stocked(double stock, double cooldown = 10, double interval = 1)
    {
        var pack = new GamePack
        {
            ActiveSkillSlots = [new() { ID = "slot" }],
            Simulation = new() { Skills = [new() { Skill = "skill", Cooldown = "cooldown", Impact = "impact", Stock = "stock", StockInterval = interval }] }
        };
        SkillPreviewOutput Output(string id, double value) => new(id, id, "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, value, "");
        return SkillDamageSchedule.Create(pack, ImmutableDictionary<string, SkillPreview>.Empty.Add("slot", new("skill", [Output("cooldown", cooldown), Output("impact", 100), Output("stock", stock)])));
    }

    private static double[] Impacts(SkillDamageSchedule schedule, double duration)
    {
        var stream = schedule.Fights(duration, [], TestContext.Current.CancellationToken).NewFight();
        var times = new List<double>();
        while (stream.Next is not null) { times.Add(stream.Take().Time); }
        return [.. times];
    }

    // The impacts of a fight in which cooldowns are cut at the given times, before any event at or after each.
    private static double[] Shortened(SkillDamageSchedule schedule, double duration, params (double At, double Share, double Seconds)[] cuts)
    {
        // As in a fight that drives buffs, each use is read as it deploys, so the ones to come aren't made yet.
        var stream = schedule.Fights(duration, [], TestContext.Current.CancellationToken).NewFight();
        stream.Read = (slot, _) => schedule.For(slot);
        var pending = new Queue<(double At, double Share, double Seconds)>(cuts.OrderBy(cut => cut.At));
        var times = new List<double>();
        while (true)
        {
            var next = stream.Next;
            if (pending.Count > 0 && (next is null || pending.Peek().At < next.Time))
            {
                var cut = pending.Dequeue();
                stream.Shorten(cut.At, cut.Share, cut.Seconds);
                continue;
            }
            if (next is null) { break; }
            if (next.Kind == SkillDamageKind.Deployment) { stream.Deploy(); }
            else if (stream.Take() is { Kind: SkillDamageKind.Impact } impact) { times.Add(impact.Time); }
        }
        return [.. times];
    }

    [Fact]
    public void ACooldownCutTakesSecondsOrAShareOfItAndNeverEndsBeforeNow()
    {
        // A 10 s cooldown: uses at 0, 10 and 20.
        Assert.Equal([0d, 10, 20], Shortened(Schedule(cooldown: 10), 25));
        // 4 s off at 3 s brings the next to 6 s, and the one after follows the full cooldown from it.
        Assert.Equal([0d, 6, 16], Shortened(Schedule(cooldown: 10), 25, (3, 0, 4)));
        // A quarter of the cooldown is 2.5 s.
        Assert.Equal([0d, 7.5, 17.5], Shortened(Schedule(cooldown: 10), 25, (3, 0.25, 0)));
        // A reset is the whole cooldown, and the skill deploys at once.
        Assert.Equal([0d, 3, 13, 23], Shortened(Schedule(cooldown: 10), 25, (3, 1, 0)));
        // A cut at the moment of a use leaves the cooldown that use starts.
        Assert.Equal([0d, 10, 20], Shortened(Schedule(cooldown: 10), 25, (10, 1, 0)));
        // More than what's left ends it now: 30 s off at 9 s deploys at 9 s.
        Assert.Equal([0d, 9, 19], Shortened(Schedule(cooldown: 10), 25, (9, 0, 30)));
        Assert.Equal("skill-cooldown-invalid", Assert.Throws<SimulationException>(() => Shortened(Schedule(cooldown: 10), 25, (3, -1, 0))).Code);
        // A stream that makes its uses ahead can't have them cut.
        Assert.Throws<InvalidOperationException>(() => Schedule(cooldown: 10).Fights(25, [], TestContext.Current.CancellationToken).NewFight().Shorten(3, 1, 0));
    }

    [Fact]
    public void ACooldownFromAUsesEndCantEndBeforeTheUseDoes()
    {
        // 10 s from the end of a 2 s use: uses at 0, 12 and 24. A reset during the use waits for its end at 2 s.
        Assert.Equal([0d, 12, 24], Shortened(Schedule(fromEnd: true, cooldown: 10), 30));
        Assert.Equal([0d, 2, 14, 26], Shortened(Schedule(fromEnd: true, cooldown: 10), 30, (1, 1, 0)));
        // After the use, a reset is now; a share is of the cooldown, not of the cooldown and the use.
        Assert.Equal([0d, 5, 17, 29], Shortened(Schedule(fromEnd: true, cooldown: 10), 30, (5, 1, 0)));
        Assert.Equal([0d, 7, 19], Shortened(Schedule(fromEnd: true, cooldown: 10), 30, (5, 0.5, 0)));
    }

    [Fact]
    public void ACutBringsAStockedSkillsNextChargeSooner()
    {
        // Three charges at 0, 1 and 2 s, then one back each 10 s from 10 s.
        Assert.Equal([0d, 1, 2, 10, 20, 30], Shortened(Stocked(3), 35));
        // 4 s off at 5 s: the charge due at 10 s is back at 6 s, and the next 10 s after that.
        Assert.Equal([0d, 1, 2, 6, 16, 26], Shortened(Stocked(3), 35, (5, 0, 4)));
        // A reset brings one charge back now, not the whole stock.
        Assert.Equal([0d, 1, 2, 5, 15, 25], Shortened(Stocked(3), 35, (5, 1, 0)));
        // With charges held the cut still brings the next charge sooner, and the interval holds: 0.25 s off at 0.5 s leaves the next use at
        // 1 s, and the charge due at 10 s comes back at 9.75 s.
        Assert.Equal([0d, 1, 2, 9.75, 19.75, 29.75], Shortened(Stocked(3), 35, (0.5, 0, 0.25)));
    }

    [Fact]
    public void AStockSpendsItsChargesAtItsIntervalThenFollowsTheCooldown()
    {
        // Three held: 0, 1 and 2 s. The cooldown has run since the first, so the next charge is back at 10 s, and one more each 10 s after.
        Assert.Equal([0d, 1, 2, 10, 20, 30], Impacts(Stocked(3), 35));
        // A short cooldown refills between spends, so the stock lasts longer: one back at 2 s and at 4 s, and it's empty at 3 s and at 4 s.
        Assert.Equal([0d, 1, 2, 3, 4, 6], Impacts(Stocked(3, cooldown: 2), 6.5));
        // Spends further apart than the cooldown: the charge due at 10 s comes back while one is still out, so the next starts at 20 s, not from
        // the spend at 14 s. The last charge goes at 28 s, and the one back at 30 s still waits out the interval, to 35 s.
        Assert.Equal([0d, 7, 14, 21, 28, 35, 42, 50], Impacts(Stocked(3, interval: 7), 55));
        // One charge is a plain cooldown.
        Assert.Equal([0d, 10, 20], Impacts(Stocked(1), 25));
        // A stock can't hold part of a charge.
        Assert.Equal("skill-output-invalid", Assert.Throws<SimulationException>(() => Stocked(2.5)).Code);
        // Different charges mean different times, so it isn't the same schedule against another target.
        Assert.False(Stocked(3).SameTiming(Stocked(2)));
        Assert.False(Stocked(3).SameTiming(Stocked(3, interval: 2)));
        Assert.True(Stocked(3).SameTiming(Stocked(3)));
        // Uses read off a cycle would miss the charges.
        Assert.Equal("skill-output-invalid", Assert.Throws<SimulationException>(() => Stocked(3).Uses(30, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void ASkillKeepsTheStockItFirstDeployedWith()
    {
        double[] Live(double first, double later)
        {
            var stream = Stocked(first).Fights(35, [], TestContext.Current.CancellationToken).NewFight();
            stream.Read = (slot, time) => Stocked(time < 0.5 ? first : later).For(slot);
            var times = new List<double>();
            while (stream.Next is { } next)
            {
                if (next.Kind == SkillDamageKind.Deployment) { stream.Deploy(); }
                else { times.Add(stream.Take().Time); }
            }
            return [.. times];
        }
        Assert.Equal([0d, 1, 2, 10, 20, 30], Live(3, 1));
        Assert.Equal([0d, 10, 20, 30], Live(1, 3));
    }

    [Fact]
    public void ScheduleClassifiesImpactAndContinuousWindowsExplicitly()
    {
        var events = Schedule().DamageEvents(0.5, TestContext.Current.CancellationToken);
        Assert.Contains(events, entry => entry.Kind == SkillDamageKind.Impact && entry.Time == 0);
        Assert.Contains(events, entry => entry.Kind == SkillDamageKind.ContinuousWindow && entry.Start < entry.Time);
        Assert.DoesNotContain(events, entry => entry.Kind == SkillDamageKind.StatusDot);
    }

    private static SkillDamageSchedule WithStatus(double statusRate, double statusWindow, double rate = 10, double window = 0.3)
    {
        var pack = new GamePack
        {
            ActiveSkillSlots = [new() { ID = "slot" }],
            Simulation = new() { Skills = [new() { Skill = "skill", Cooldown = "cooldown", Rate = "rate", Window = "window", Status = "bleeding", StatusRate = "status_rate", StatusWindow = "status_window" }] }
        };
        SkillPreviewOutput Output(string id, double value) => new(id, id, "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, value, "");
        return SkillDamageSchedule.Create(pack, ImmutableDictionary<string, SkillPreview>.Empty.Add("slot", new("skill",
            [Output("cooldown", 10), Output("rate", rate), Output("window", window), Output("status_rate", statusRate), Output("status_window", statusWindow)])));
    }

    [Fact]
    public void ASpacedUseAppliesItsStatusUntilItsLastImpact()
    {
        // Three shells 2 s apart from 10 s: the status goes on with each, so the last puts it on at 14 s.
        var use = Spaced(3, 2).For("slot").UseAt(10, 120);
        Assert.Equal((10d, 14d, 14d), (use.WindowEnd, use.ApplyingEnd, use.StatusEnd));
        // A use the fight's end cuts short applies only until its last impact before that.
        var cut = Spaced(3, 2).For("slot").UseAt(117, 120);
        Assert.Equal((2d, 119d, 119d), (cut.Impacts, cut.ApplyingEnd, cut.StatusEnd));
        // A longer window is still the end.
        Assert.Equal(20, Spaced(3, 2, 10, 10).For("slot").UseAt(10, 120).ApplyingEnd);
    }

    private static SkillDamageSchedule Spaced(double count, double interval, double rate = 0, double window = 0, bool fromEnd = false)
    {
        var pack = new GamePack
        {
            ActiveSkillSlots = [new() { ID = "slot" }],
            Simulation = new() { Skills = [new() { Skill = "skill", Cooldown = "cooldown", Impact = "impact", Impacts = "count", ImpactInterval = "interval", Rate = rate > 0 ? "rate" : "", Window = rate > 0 ? "window" : "", CooldownFromEnd = fromEnd }] }
        };
        SkillPreviewOutput Output(string id, double value) => new(id, id, "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, value, "");
        return SkillDamageSchedule.Create(pack, ImmutableDictionary<string, SkillPreview>.Empty.Add("slot", new("skill",
            [Output("cooldown", 20), Output("impact", 100), Output("count", count), Output("interval", interval), Output("rate", rate), Output("window", window)])));
    }

    [Fact]
    public void AUsesImpactsLandAtTheirInterval()
    {
        var token = TestContext.Current.CancellationToken;
        SkillDamageEvent[] Drain(SkillDamageSchedule schedule, double duration)
        {
            var stream = schedule.Fights(duration, [], token).NewFight();
            var taken = new List<SkillDamageEvent>();
            while (stream.Next is not null) { taken.Add(stream.Take()); }
            return [.. taken];
        }
        var spaced = Drain(Spaced(3, 3), 15);
        Assert.Equal([0d, 3, 6], spaced.Select(entry => entry.Time));
        // Each is one impact's damage, not the use's total.
        Assert.All(spaced, entry => Assert.Equal(100, entry.Damage));
        Assert.Equal(3, spaced.Select(entry => entry.HitGroup).Distinct().Count());
        // Those the fight ends before don't land, and the next use has its own.
        Assert.Equal([0d, 3], Drain(Spaced(3, 3), 5).Select(entry => entry.Time));
        Assert.Equal([0d, 3, 6, 20, 23, 26], Drain(Spaced(3, 3), 30).Select(entry => entry.Time));
        // Beside a rate they come in time order, a tick first at the same moment.
        var mixed = Drain(Spaced(3, 0.2, 10, 0.5), 5);
        Assert.Equal(mixed.Select(entry => entry.Time).Order(), mixed.Select(entry => entry.Time));
        Assert.Equal(SkillDamageKind.ContinuousWindow, mixed.First(entry => entry.Time == mixed.Where(other => other.Kind == SkillDamageKind.Impact).ElementAt(1).Time).Kind);
        // Timing differs, so it isn't the same schedule against another target; an interval of none can't space anything.
        Assert.False(Spaced(3, 3).SameTiming(Spaced(3, 2)));
        Assert.False(Spaced(3, 3).SameTiming(Spaced(2, 3)));
        Assert.True(Spaced(3, 3).SameTiming(Spaced(3, 3)));
        Assert.Equal("skill-output-invalid", Assert.Throws<SimulationException>(() => Spaced(3, 0)).Code);
        Assert.Equal(new double[] { 100, 0, 0, 100, 0, 0, 100, 0 }, Spaced(3, 3).DummyTimeline(8, token));
    }

    [Fact]
    public void AnImpactRoundedUpToTheFightsEndDoesntLand()
    {
        // 105.74 + 23 * 0.62 is exactly 120 in doubles, where the division rounds up to 24 impacts, and the 24th has no timeline bucket.
        var plan = Spaced(30, 0.62).For("slot");
        Assert.Equal(120, 105.74 + 23 * 0.62);
        Assert.Equal(23, plan.UseAt(105.74, 120).Impacts);
        Assert.Equal(1, Spaced(1, 3).For("slot").UseAt(10, 11).Impacts);
    }

    [Fact]
    public void ACooldownFromAUsesEndWaitsForItsLastImpact()
    {
        // 20 s from the third shot, at 6 s.
        Assert.Equal(26, Spaced(3, 3, fromEnd: true).For("slot").Cycle);
        // A longer window is still the use's end.
        Assert.Equal(30, Spaced(3, 3, 10, 10, fromEnd: true).For("slot").Cycle);
        Assert.Equal(20, Spaced(3, 3).For("slot").Cycle);
    }

    [Fact]
    public void OneImpactNeedsNoInterval() => Assert.Equal(1, Spaced(1, 0).For("slot").Impacts);

    [Fact]
    public void AFightsWorkCountsOnlyTheImpactsThatFit()
    {
        // 200 impacts a use, whatever the count says.
        Spaced(2000000, 3).CheckWork(600, []);
    }

    [Fact]
    public void AListOfAFightKeepsTicksBeforeImpacts()
    {
        var events = Spaced(3, 0.2, 10, 0.5).DamageEvents(5, TestContext.Current.CancellationToken);
        var impact = events.Where(entry => entry.Kind == SkillDamageKind.Impact).ElementAt(1);
        Assert.Equal(SkillDamageKind.ContinuousWindow, events.First(entry => entry.Time == impact.Time).Kind);
    }

    [Fact]
    public void AUseBuildingTooManyEventsStopsAtTheLimit()
    {
        // A state reading a huge count isn't seen by the work check up front, so a use mustn't be built whole first.
        var fight = Spaced(3, 3).Fights(600, [], TestContext.Current.CancellationToken).NewFight();
        fight.Read = (slot, _) => Spaced(1e9, 1e-6).For(slot);
        Assert.Equal("skill-work-limit", Assert.Throws<SimulationException>(fight.Deploy).Code);
    }

    [Fact]
    public void FightsDontShareAUseWithDifferentImpacts()
    {
        // A use is made once for fights that read the same, and how many impacts it has is part of that.
        var fights = Spaced(3, 3).Fights(15, [], TestContext.Current.CancellationToken);
        int Impacts(double count)
        {
            var stream = fights.NewFight();
            stream.Read = (slot, _) => Spaced(count, 3).For(slot);
            var impacts = 0;
            while (stream.Next is { } next)
            {
                if (next.Kind == SkillDamageKind.Deployment) { stream.Deploy(); }
                else { impacts += stream.Take().Kind == SkillDamageKind.Impact ? 1 : 0; }
            }
            return impacts;
        }
        Assert.Equal(3, Impacts(3));
        Assert.Equal(2, Impacts(2));
    }

    [Fact]
    public void SpacedImpactsCountTowardAFightsWork() =>
        Assert.Equal("skill-work-limit", Assert.Throws<SimulationException>(() => Spaced(2000000, 0.00001).CheckWork(600, [])).Code);

    [Fact]
    public void AStatusRunsBesideARateAndPastItsWindow()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = WithStatus(4, 0.2).Fights(5, [], token).NewFight();
        var taken = new List<SkillDamageEvent>();
        while (stream.Next is not null) { taken.Add(stream.Take()); }
        // Both streams come in time order, however they lie in the use.
        Assert.Equal(taken.Select(entry => entry.Time).Order(), taken.Select(entry => entry.Time));
        Assert.Equal(3, taken.Count(entry => entry.Kind == SkillDamageKind.ContinuousWindow));
        Assert.Equal(5, taken.Count(entry => entry.Kind == SkillDamageKind.StatusDot));
        Assert.Equal(3, taken.Where(entry => entry.Kind == SkillDamageKind.ContinuousWindow).Sum(entry => entry.Damage), 9);
        Assert.Equal(2, taken.Where(entry => entry.Kind == SkillDamageKind.StatusDot).Sum(entry => entry.Damage), 9);
        // A different status means a different schedule against another target.
        Assert.False(WithStatus(4, 1).SameTiming(WithStatus(4, 2)));
        Assert.False(WithStatus(4, 1).SameTiming(WithStatus(0, 1)));
        Assert.True(WithStatus(4, 1).SameTiming(WithStatus(8, 1)));
    }

    [Theory]
    [InlineData(SkillDamageKind.Deployment)]
    [InlineData((SkillDamageKind)999)]
    public void UnsupportedKindsCannotBeReclassifiedAsContinuousDamage(SkillDamageKind kind)
    {
        Assert.Equal("skill-event-kind-unsupported", Assert.Throws<SimulationException>(() =>
            SkillDamageSchedule.SplitAt([new(1, 10, "slot", kind, 0)], [], TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void IncomingCutsConserveContinuousDamageAndOrderTicksBeforeImpacts()
    {
        var events = ImmutableArray.Create(new SkillDamageEvent(1, 10, "slot", SkillDamageKind.ContinuousWindow, 0) { HitGroup = 41 }, new SkillDamageEvent(0.5, 7, "other", SkillDamageKind.Impact, 0) { HitGroup = 42 });
        var split = SkillDamageSchedule.SplitAt(events, [0.25, 0.5, 0.75], TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2.5, 2.5, 2.5, 2.5 }, split.Where(e => e.Kind == SkillDamageKind.ContinuousWindow).Select(e => e.Damage));
        Assert.Equal(new[] { false, true }, split.Where(e => e.Time == 0.5).Select(e => e.Kind == SkillDamageKind.Impact));
        Assert.Equal(17, split.Sum(e => e.Damage));
        Assert.All(split.Where(e => e.Kind == SkillDamageKind.ContinuousWindow), e => Assert.Equal(41, e.HitGroup));
        Assert.Equal(42, Assert.Single(split.Where(e => e.Kind == SkillDamageKind.Impact)).HitGroup);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 1)]
    public void IncomingCutsRequireFiniteStrictlyIncreasingBoundaries(double first, double second) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillDamageSchedule.SplitAt([], [first, second], TestContext.Current.CancellationToken));

    [Fact]
    public void IncomingCutsObserveCancellation()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => SkillDamageSchedule.SplitAt([new(1, 10, "slot", SkillDamageKind.ContinuousWindow, 0)], [0.5], source.Token));
    }

    [Theory]
    [InlineData(true, double.NaN)]
    [InlineData(false, -1)]
    public void IncomingCutsRejectInvalidDamage(bool impact, double damage) =>
        Assert.Equal("skill-event-invalid", Assert.Throws<SimulationException>(() =>
            SkillDamageSchedule.SplitAt([new(1, damage, "slot", impact ? SkillDamageKind.Impact : SkillDamageKind.ContinuousWindow, 0)], [], TestContext.Current.CancellationToken)).Code);

    [Fact]
    public void DuplicateSkillRulesAreRefusedWithACode()
    {
        var rule = new SimSkillDefinition { Skill = "skill", Window = "window" };
        var pack = new GamePack { ActiveSkillSlots = [new() { ID = "slot" }], Simulation = new() { Skills = [rule, rule with { Cooldown = "window" }] } };
        var preview = new SkillPreview("skill", [new("window", "window", "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, 1, "")]);
        Assert.Equal("skill-rule-duplicate", Assert.Throws<SimulationException>(() =>
            SkillDamageSchedule.Create(pack, ImmutableDictionary<string, SkillPreview>.Empty.Add("slot", preview))).Code);
    }

    // Several slots, as a fight has them: an impact, a rate and overlapping windows, with cooldowns that land on shared times.
    private static SkillDamageSchedule Several(params (double Cooldown, double Window, double Rate, double Impact)[] slots)
    {
        var pack = new GamePack
        {
            ActiveSkillSlots = [.. slots.Select((_, index) => new ActiveSkillSlotDefinition { ID = $"slot{index}" })],
            Simulation = new() { Skills = [.. slots.Select((_, index) => new SimSkillDefinition { Skill = $"skill{index}", Window = "window", Cooldown = "cooldown", Rate = "rate", Impact = "impact" })] }
        };
        SkillPreviewOutput Output(string id, double value) => new(id, id, "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, value, "");
        var previews = ImmutableDictionary<string, SkillPreview>.Empty;
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            previews = previews.Add($"slot{index}", new($"skill{index}", [Output("window", slot.Window), Output("cooldown", slot.Cooldown), Output("rate", slot.Rate), Output("impact", slot.Impact)]));
        }
        return SkillDamageSchedule.Create(pack, previews);
    }

    private static readonly (double, double, double, double)[][] Mixes =
    [
        [(3, 2, 10, 100)],
        [(2, 0, 0, 50), (2, 0, 0, 60)],
        [(1.5, 4, 10, 0), (2, 3, 20, 0), (3, 0, 0, 70)],
        [(0.7, 0.35, 5, 1), (1.1, 2.3, 7, 2), (0.9, 0, 0, 3)],
        [(4, 1.25, 3, 9), (4, 1.25, 3, 9), (4, 1.25, 3, 9)],
        // The same batches, with the first slot's one place further on in its use for having an impact, so only the slot decides.
        [(4, 1.25, 3, 9), (4, 1.25, 3, 0)],
    ];

    private static List<SkillDamageEvent> Drain(SkillEventStream stream)
    {
        var taken = new List<SkillDamageEvent>();
        while (stream.Next is not null) { taken.Add(stream.Take()); }
        return taken;
    }

    private static void AssertSameEvents(IReadOnlyList<SkillDamageEvent> expected, IReadOnlyList<SkillDamageEvent> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        // The hit group's number may differ, but which pieces share one may not.
        var groups = new Dictionary<int, int>();
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index] with { HitGroup = 0 }, actual[index] with { HitGroup = 0 });
            if (!groups.TryAdd(expected[index].HitGroup, actual[index].HitGroup)) { Assert.Equal(groups[expected[index].HitGroup], actual[index].HitGroup); }
        }
        Assert.Equal(groups.Count, groups.Values.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AStreamMatchesTheWholeFightsList(int mix)
    {
        var schedule = Several(Mixes[mix]);
        var token = TestContext.Current.CancellationToken;
        AssertSameEvents(schedule.DamageEvents(13, token), Drain(schedule.Fights(13, [], token).NewFight()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AStreamMatchesTheWholeFightsListCutAtAttempts(int mix)
    {
        // Attempts at times that cut batches, and that fall on batch ends.
        ImmutableArray<double> attempts = [.. Enumerable.Range(1, 40).Select(step => step * 0.33), .. new[] { 14.0, 15.5 }];
        var schedule = Several(Mixes[mix]);
        var token = TestContext.Current.CancellationToken;
        var list = SkillDamageSchedule.SplitAt(schedule.DamageEvents(13, token), attempts, token);
        AssertSameEvents(list, Drain(schedule.Fights(13, attempts, token).NewFight()));
    }

    [Fact]
    public void ACutTickComesBeforeAnImpactAtTheSameTime()
    {
        // An attempt at each impact cuts a running rate's batch there, so a tick and an impact share a time, with the tick's whole batch
        // ending later. The tick still comes first.
        var token = TestContext.Current.CancellationToken;
        ImmutableArray<double> attempts = [.. Enumerable.Range(1, 6).Select(step => step * 1.55)];
        var schedule = Several((1.55, 0, 0, 40), (20, 12, 10, 0));
        var list = SkillDamageSchedule.SplitAt(schedule.DamageEvents(10, token), attempts, token);
        var stream = Drain(schedule.Fights(10, attempts, token).NewFight());
        AssertSameEvents(list, stream);
        var at = stream.FindIndex(entry => entry.Time == attempts[0]);
        Assert.Equal((SkillDamageKind.ContinuousWindow, SkillDamageKind.Impact), (stream[at].Kind, stream[at + 1].Kind));
    }

    [Fact]
    public void EachFightGetsTheWholeStream()
    {
        var token = TestContext.Current.CancellationToken;
        var fights = Several(Mixes[3]).Fights(9, [], token);
        var (first, second) = (Drain(fights.NewFight()), Drain(fights.NewFight()));
        Assert.Equal(first, second);
    }

    [Fact]
    public void AnImpactOnACooldownHasNoWindowOrRate()
    {
        var pack = new GamePack
        {
            ActiveSkillSlots = [new() { ID = "slot" }],
            Simulation = new() { Skills = [new() { Skill = "skill", Cooldown = "cooldown", Impact = "impact", Impacts = "count" }] }
        };
        SkillPreviewOutput Output(string id, double value) => new(id, id, "", "decimal", "", "", ImmutableDictionary<string, double>.Empty, value, "");
        var schedule = SkillDamageSchedule.Create(pack, ImmutableDictionary<string, SkillPreview>.Empty.Add("slot",
            new("skill", [Output("cooldown", 4), Output("impact", 100), Output("count", 3)])));
        var events = schedule.DamageEvents(9, TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 0, 4, 8 }, events.Select(entry => entry.Time));
        Assert.All(events, entry => { Assert.Equal(SkillDamageKind.Impact, entry.Kind); Assert.Equal(300, entry.Damage); });
    }

    [Fact]
    public void CooldownFromStartAllowsOverlappingWindows()
    {
        var uses = Schedule(window: 4).Uses(7, TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 0, 3, 6 }, uses.Select(u => u.Time));
        Assert.Equal(new double[] { 4, 7, 7 }, uses.Select(u => u.WindowEnd));
        Assert.All(uses, u => Assert.Equal(10, u.Rate));
    }

    [Fact]
    public void CooldownFromEndWaitsForTheActiveWindow()
    {
        var uses = Schedule(fromEnd: true).Uses(10, TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 0, 5 }, uses.Select(u => u.Time));
        Assert.Equal(new double[] { 2, 7 }, uses.Select(u => u.WindowEnd));
    }

    [Fact]
    public void MultipleImpactsAreExplicitlyAggregatedAtDeployment()
    {
        var use = Assert.Single(Schedule(count: 4).Uses(1, TestContext.Current.CancellationToken));
        Assert.Equal(400, use.Impact);
        Assert.Equal(1, use.WindowEnd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidCyclesAreRefused(double cooldown) =>
        Assert.Throws<SimulationException>(() => Schedule(cooldown: cooldown));

    [Fact]
    public void CancellationDoesNotProducePartialPlans()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Schedule().Uses(10, source.Token));
    }

    [Fact]
    public void TimelineIntegratesClippedWindowsAndDeploymentImpacts()
    {
        var timeline = Schedule().DummyTimeline(7, TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 110, 10, 0, 110, 10, 0, 110 }, timeline);
        Assert.Equal(350, timeline.Sum());
    }

    [Fact]
    public void OverlappingWindowsAddInsteadOfReplacingEarlierDamage()
    {
        Assert.Equal(390, Schedule(window: 4).DummyTimeline(7, TestContext.Current.CancellationToken).Sum());
        Assert.Equal(105, Assert.Single(Schedule().DummyTimeline(0.5, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void CommittedAssaultTurretUsesResolvedPreviewTimingAndRate()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var compiled = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var resolved = BuildResolver.Resolve(compiled, build);
        Assert.DoesNotContain(resolved.Diagnostics, d => d.Severity == "error");
        var preview = resolved.ActiveSkillPreviews["skill_slot_1"];
        double Value(string id) => preview.Outputs.Single(o => o.Id == id).Value;
        var schedule = SkillDamageSchedule.Create(compiled.Pack, resolved.ActiveSkillPreviews);
        var uses = schedule.Uses(600, TestContext.Current.CancellationToken);
        Assert.True(uses.Length > 1);
        Assert.Equal(Value("assumed_burst_cycle_dps"), uses[0].Rate);
        Assert.Equal(Value("duration_seconds") + Value("cooldown_seconds"), uses[1].Time);
        Assert.True(schedule.DummyTimeline(600, TestContext.Current.CancellationToken).Sum() > 0);
    }

    [Fact]
    public void PathologicalCyclesAreBounded()
    {
        Assert.Equal("skill-work-limit", Assert.Throws<SimulationException>(() => Schedule(cooldown: 0.000001).Uses(1, TestContext.Current.CancellationToken)).Code);
        Assert.Equal("skill-work-limit", Assert.Throws<SimulationException>(() => Schedule(cooldown: 0.000001).CheckWork(1, [])).Code);
        // Few enough uses, but too many batches between them.
        Assert.Equal("skill-work-limit", Assert.Throws<SimulationException>(() => Schedule(window: 60, cooldown: 0.01).CheckWork(60, [])).Code);
        Schedule(window: 2, cooldown: 5).CheckWork(60, [1, 2, 3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DeployingAsAFightGoesKeepsTheOrder(int mix)
    {
        var schedule = Several(Mixes[mix]);
        var token = TestContext.Current.CancellationToken;
        ImmutableArray<double> attempts = [0.7, 1.1, 1.25, 2, 2.3, 4, 5.5, 8, 9.3];
        var stream = schedule.Fights(13, attempts, token).NewFight();
        stream.Read = (slot, _) => schedule.For(slot);
        var taken = new List<SkillDamageEvent>();
        while (stream.Next is { } next)
        {
            if (next.Kind == SkillDamageKind.Deployment) { stream.Deploy(); }
            else { taken.Add(stream.Take()); }
        }
        AssertSameEvents(Drain(schedule.Fights(13, attempts, token).NewFight()), taken);
    }
}
