using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0035: the Jammer Pulse and EMP Sticky Bomb, on the committed rows (R161, r161.candidate.1; the owner's calls, 2026-11-22).
/// Disrupt is a control status talents see, which stops a machine's fire (target 2) as shock does and leaves a human's; the Jammer's
/// lands after its 2 s charge on every enemy within its radius of the player, the sticky's at the use for 15 s.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const double JammerCharge = 2;

    // The committed pack, without the stat that stops a machine's fire, or with the Jammer's charge or its area changed, if asked.
    private static CompiledPackExpressions JammerPack(bool stops = true, bool charge = true, bool fromPlayer = true)
    {
        var committed = CommittedPack();
        if (stops && charge && fromPlayer) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Stats = stops ? committed.Pack.Stats : [.. committed.Pack.Stats.Where(stat => stat.ID != EnemyFireSchedule.DisruptStopsFireStat)],
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(rule => rule.Skill != "skill_jammer_pulse" ? rule :
                    rule with { ImpactDelay = charge ? rule.ImpactDelay : 0, AreaFromPlayer = fromPlayer })]
            }
        }).Compiled);
    }

    // The player all but never hits, so the first target lives through the disrupt.
    private static DummyFightResult JammerFight(CompiledPackExpressions pack, string target, string? skill = "skill_jammer_pulse", double duration = 10, bool driven = true,
        string? second = null, string attackers = "1")
    {
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads");
        if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
        if (second is not null) { build = build.WithActiveSkillSelection("skill_slot_2", second); }
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "1").Add("enemy_target", target).Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add(DummyFightOptions.AttackersInput, attackers), new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = driven,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static double[] EventTimes(DummyFightResult result, DummyEventKind kind) =>
        [.. result.FirstFightEvents.Where(entry => entry.Kind == kind).Select(entry => entry.Time)];

    [Fact]
    public void DisruptStopsAMachinesFireWhileItLasts()
    {
        // Target 2 is machines: the Jammer, used at 0 s, puts its 3 s disrupt (tier 0) on it as its 2 s charge ends, which stops its fire as
        // shock does (with one attacker, every attempt); fire lands before and after.
        var result = JammerFight(JammerPack(), "2");
        var window = result.TargetStatusUptime["disrupted"].Seconds;
        Assert.Equal(3, window, 9);
        var (from, until) = (JammerCharge, JammerCharge + window);
        var blocked = EventTimes(result, DummyEventKind.EnemyShotBlocked);
        Assert.NotEmpty(blocked);
        Assert.All(blocked, time => Assert.True(time >= from && time < until, $"{time}"));
        var hits = EventTimes(result, DummyEventKind.EnemyHit);
        Assert.DoesNotContain(hits, time => time >= from && time < until);
        Assert.Contains(hits, time => time < from);
        Assert.Contains(hits, time => time >= until);
        // With the stat off, or without the Jammer, nothing is blocked.
        Assert.Empty(EventTimes(JammerFight(JammerPack(stops: false), "2"), DummyEventKind.EnemyShotBlocked));
        Assert.Empty(EventTimes(JammerFight(JammerPack(), "2", skill: null), DummyEventKind.EnemyShotBlocked));
    }

    [Fact]
    public void DisruptLeavesAHumansFireAndIsAStatusTalentsSee()
    {
        // Target 1 is human: disrupted for the same 3 s, it keeps firing, and the status counts in uptime as any other.
        var result = JammerFight(JammerPack(), "1");
        Assert.Equal(3, result.TargetStatusUptime["disrupted"].Seconds, 9);
        Assert.True(result.TargetStatusUptime["any"].Seconds >= 3);
        Assert.Empty(EventTimes(result, DummyEventKind.EnemyShotBlocked));
        Assert.Contains(EventTimes(result, DummyEventKind.EnemyHit), time => time >= JammerCharge && time < JammerCharge + 3);
    }

    [Fact]
    public void DisruptAndShockOnOneEnemyBlockItOnce()
    {
        // The two share shock's credit: a Shock Trap's 3 s shock and the Jammer's 3 s disrupt (its charge taken off, so the two line up)
        // on one machine at once block the same attempts as the shock alone, not twice as many. With three attackers aggregated, each
        // blocks one attacker's share.
        var pack = JammerPack(charge: false);
        var shocked = JammerFight(pack, "2", skill: null, second: "skill_shock_trap", attackers: "3");
        var both = JammerFight(pack, "2", second: "skill_shock_trap", attackers: "3");
        Assert.Equal(3, both.TargetStatusUptime["disrupted"].Seconds, 9);
        Assert.Equal(3, both.TargetStatusUptime["shocked"].Seconds, 9);
        Assert.True(shocked.Incoming!.BlockedShots > 0);
        Assert.Equal(EventTimes(shocked, DummyEventKind.EnemyShotBlocked), EventTimes(both, DummyEventKind.EnemyShotBlocked));
    }

    [Fact]
    public void ADisruptStopsFireStatOtherThanZeroOrOneIsRefused()
    {
        var committed = CommittedPack();
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Stats = [.. committed.Pack.Stats.Select(stat => stat.ID == EnemyFireSchedule.DisruptStopsFireStat ? stat with { Formula = "0.5", Min = null, Max = null } : stat)]
        }).Compiled);
        Assert.Equal("incoming-stat-invalid", Assert.Throws<SimulationException>(() => JammerFight(pack, "1", skill: null)).Code);
    }

    [Fact]
    public void TheEmpStickysDisruptLastsFifteenSecondsFromItsUse()
    {
        // The sticky's disrupt is the status's NPC default, 15 s at no Status Effects, from its use: target 2 fires on none of it.
        var result = JammerFight(JammerPack(), "2", skill: "skill_emp_sticky_bomb", duration: 20);
        Assert.Equal(15, result.TargetStatusUptime["disrupted"].Seconds, 9);
        var hits = EventTimes(result, DummyEventKind.EnemyHit);
        Assert.DoesNotContain(hits, time => time < 15);
        Assert.Contains(hits, time => time >= 15);
        Assert.Equal(0, result.SkillDps?.Total.Mean ?? 0);
    }

    [Fact]
    public void TheCommittedRowsResolveTheStatAndTheStickysDuration()
    {
        // enemy_disrupt_stops_fire is 1 for target 2 alone; the sticky's disrupt output is 15 s, its armed life apart.
        var pack = CommittedPack();
        double Stops(string target) => BuildResolver.Resolve(pack, BuildSnapshot.Create(pack.Pack), Context.Add("enemy_target", target)).Calculation.Stats[EnemyFireSchedule.DisruptStopsFireStat];
        Assert.Equal(1, Stops("2"));
        Assert.Equal(0, Stops("1"));
        Assert.Equal(0, Stops("6"));
        var build = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", "skill_emp_sticky_bomb");
        var outputs = BuildResolver.Resolve(pack, build, Context).ActiveSkillPreviews["skill_slot_1"].Outputs;
        Assert.Equal(15, outputs.Single(entry => entry.Id == "disrupt_duration_seconds").Value, 9);
        var jammer = pack.Pack.Simulation.Skills.Single(rule => rule.Skill == "skill_jammer_pulse");
        Assert.True(jammer is { AreaFromPlayer: true, ControlStatus: true, Status: "disrupted", ImpactDelay: JammerCharge });
    }

    // Three machines at once (target 2, each stopped while disrupted), each attempt landing: the hits in the disrupt's 3 s (after the
    // charge; an attempt at the very moment it lands comes first) come from those the pulse didn't reach.
    private static double[] HitsWhileDisrupted(CompiledPackExpressions pack, double? distance, double spacing, params double[] distances)
    {
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_jammer_pulse");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "1").Add("enemy_target", "2").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100"), new()
        {
            Duration = JammerCharge + 3,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true,
            EnemiesAtOnce = 3,
            EnemySpacing = spacing,
            Distance = distance,
            TargetDistances = [.. distances]
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(EventTimes(result, DummyEventKind.EnemyShot));
        return [.. EventTimes(result, DummyEventKind.EnemyHit).Where(time => time > JammerCharge)];
    }

    [Fact]
    public void TheJammerReachesEveryEnemyWithinItsRadiusOfThePlayer()
    {
        // At 15 m every enemy is within 20 m of the player, however far apart they stand: none lands a hit. At 25 m nobody is, not even
        // the enemy being shot: every attempt lands.
        Assert.Empty(HitsWhileDisrupted(JammerPack(), 15, 30));
        var far = HitsWhileDisrupted(JammerPack(), 25, 30);
        Assert.Equal(far, HitsWhileDisrupted(JammerPack(stops: false), 25, 30));
        Assert.NotEmpty(far);
        // Measured from the enemy being shot instead, enemies 30 m apart are out of reach of each other, so the other two keep firing.
        Assert.NotEmpty(HitsWhileDisrupted(JammerPack(fromPlayer: false), 15, 30));
    }

    [Fact]
    public void UnderTargetDistancesEachEnemyIsReachedByItsOwn()
    {
        // Enemies at 10, 30 and 10 m: the one at 30 m keeps firing, so hits land, fewer than with none reached; all at 10 m, none lands.
        Assert.Empty(HitsWhileDisrupted(JammerPack(), null, 30, 10, 10, 10));
        var one = HitsWhileDisrupted(JammerPack(), null, 30, 10, 30, 10);
        Assert.NotEmpty(one);
        // With nothing stopped the player goes down sooner, so the two are compared over that fight's span.
        var none = HitsWhileDisrupted(JammerPack(stops: false), null, 30, 10, 30, 10);
        Assert.True(one.Count(time => time <= none.Max()) < none.Length);
    }

    [Fact]
    public void WithoutADistanceTheLineFromTheEnemyBeingShotIsUsed()
    {
        // The enemy being shot is taken to be beside the player: 5 m apart, the others (5 and 10 m away) are reached; 30 m apart,
        // only the enemy being shot is.
        Assert.Empty(HitsWhileDisrupted(JammerPack(), null, 5));
        var apart = HitsWhileDisrupted(JammerPack(), null, 30);
        Assert.NotEmpty(apart);
        var none = HitsWhileDisrupted(JammerPack(stops: false), null, 30);
        Assert.True(apart.Count(time => time <= none.Max()) < none.Length);
    }

    [Fact]
    public void AnAreaFromThePlayerIsAControlStatusesAlone()
    {
        var pack = JammerPack().Pack;
        var jammer = pack.Simulation.Skills.Single(rule => rule.Skill == "skill_jammer_pulse");
        Assert.DoesNotContain(PackValidator.Validate(pack).Diagnostics, diagnostic => diagnostic.Path.EndsWith(".areaFromPlayer", StringComparison.Ordinal));
        foreach (var bad in new[] { jammer with { ControlStatus = false }, jammer with { Area = "" }, jammer with { Impact = "cooldown_seconds" } })
        {
            var refused = pack with { Simulation = pack.Simulation with { Skills = [.. pack.Simulation.Skills.Select(rule => rule.Skill == "skill_jammer_pulse" ? bad : rule)] } };
            Assert.Contains(PackValidator.Validate(refused).Diagnostics, diagnostic => diagnostic.Path.EndsWith(".areaFromPlayer", StringComparison.Ordinal));
        }
        // The field is written and read back.
        var loaded = PackValidator.Load(PackWriter.Write(pack)).Compiled!.Pack.Simulation.Skills.Single(rule => rule.Skill == "skill_jammer_pulse");
        Assert.True(loaded.AreaFromPlayer);
    }

    [Fact]
    public void APulseInAKillGapReachesTheLivingEnemiesAroundThePlayer()
    {
        // R161's case (the owner's call after it): three machines 10 m apart at 15 m, the player shooting at 90%, so the first dies before
        // the Jammer's pulse lands 2 s in. The pulse still disrupts the two left within 20 m, so their fire is blocked after it lands; a
        // pulse measured from the enemy being shot reaches nobody in the gap.
        DummyFightResult Fight(CompiledPackExpressions pack)
        {
            var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
                "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithActiveSkillSelection("skill_slot_1", "skill_jammer_pulse");
            return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "90").Add("enemy_target", "2").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100"), new()
            {
                Duration = 10,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                IncludeIncoming = true,
                IncludeSkills = true,
                DriveSimpleBuffs = true,
                CaptureFirstFightEvents = true,
                EnemiesAtOnce = 3,
                EnemySpacing = 10,
                Distance = 15
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
        var gap = Fight(JammerPack());
        var firstKill = gap.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.Kill).Time;
        // The pulse lands in the gap: the next enemy arrives 1 s after the kill (the default retarget), after the pulse.
        Assert.True(firstKill < JammerCharge && firstKill + 1 > JammerCharge, $"{firstKill}");
        var blocked = EventTimes(gap, DummyEventKind.EnemyShotBlocked);
        Assert.NotEmpty(blocked);
        Assert.All(blocked, time => Assert.True(time >= JammerCharge && time < JammerCharge + 3, $"{time}"));
        Assert.Empty(EventTimes(Fight(JammerPack(fromPlayer: false)), DummyEventKind.EnemyShotBlocked));
    }

    // Wicked (18% weapon damage for 20 s as the player applies a status), in a 10 s fight at the given distances.
    private static double WickedSeconds(string? skill, double? distance, params double[] distances)
    {
        var pack = CommittedPack();
        var build = WithTalent(pack.Pack, Equip(pack.Pack, "primary_weapon_acr"), "gear_talent_wicked");
        if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
        var several = distances.Length > 0;
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "1").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
        {
            Duration = 10,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = true,
            EnemiesAtOnce = several ? 3 : null,
            EnemySpacing = several ? 10 : null,
            Distance = distance,
            TargetDistances = [.. distances]
        }, cancellationToken: TestContext.Current.CancellationToken);
        return result.TimedTriggerUptime["wicked_active"].Seconds;
    }

    [Fact]
    public void ATalentReactsToDisruptAsToAnyStatus()
    {
        // Wicked is on from the Jammer's pulse (2 s in, after its charge) and from the sticky's use, to the 10 s fight's end.
        Assert.Equal(0, WickedSeconds(null, null), 9);
        Assert.Equal(10 - JammerCharge, WickedSeconds("skill_jammer_pulse", null), 9);
        Assert.Equal(10, WickedSeconds("skill_emp_sticky_bomb", null), 9);
        // Beyond the Jammer's 20 m the enemy being shot isn't disrupted, so nothing fires.
        Assert.Equal(10 - JammerCharge, WickedSeconds("skill_jammer_pulse", 20), 9);
        Assert.Equal(0, WickedSeconds("skill_jammer_pulse", 25), 9);
    }

    [Fact]
    public void APulseReachingOnlyOtherEnemiesFiresNoEvent()
    {
        // Events stay the enemy being shot's: at 30 m it's out of reach, so the pulse on the two at 10 m gives Wicked nothing.
        Assert.Equal(0, WickedSeconds("skill_jammer_pulse", null, 30, 10, 10), 9);
        Assert.Equal(10 - JammerCharge, WickedSeconds("skill_jammer_pulse", null, 10, 30, 30), 9);
    }
}
