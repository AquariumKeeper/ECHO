using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0036 (the Achilles Pulse), on the committed rows (R163, r163.candidate.1) with their share, pool and critical chance set by each
/// test: a use marks the enemy being shot for 10 s, and the set share of the weapon's body hits on it deal the headshot multiplier,
/// spending the zones' health by what they deal.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const double MarkSeconds = 10;

    // The committed pack with the zone share a flat stat, and the zones' health replaced if asked.
    private static CompiledPackExpressions ZonePack(string share = "1", string? health = "1000000000000", bool key = true, int count = 1)
    {
        var committed = CommittedPack();
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            // A crit chance of its own, so both kinds of hit come up.
            Stats = [.. committed.Pack.Stats.Select(stat => stat.ID == "effective_critical_hit_chance" ? stat with { Formula = "0.4" } : stat), new() { ID = "test_zone_share", Name = "Share", Formula = share }],
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != "skill_achilles_pulse" ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID switch
                {
                    "zone_health_reference" when health is not null => output with { Formula = health },
                    "zone_capacity" => output with { Formula = count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    _ => output
                })]
            })],
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(rule => rule.Skill != "skill_achilles_pulse" ? rule
                    : rule with { Zones = rule.Zones! with { Share = "test_zone_share", Key = key ? rule.Zones.Key : "" } })]
            }
        }).Compiled);
    }

    private static BuildSnapshot ZoneBuild(GamePack pack, string? skill = "skill_achilles_pulse", long rank = 0)
    {
        var build = Equip(pack, "primary_weapon_acr");
        if (skill is null) { return build; }
        build = build.WithActiveSkillSelection("skill_slot_1", skill);
        return rank > 0 ? build.WithSkillUpgrade("skill_slot_1", skill, rank) : build;
    }

    private static DummyFightResult ZoneDummy(CompiledPackExpressions pack, BuildSnapshot build, string head = "0", SimulationMode mode = SimulationMode.Random,
        double duration = 20, int fights = 1) =>
        DummyWeaponSimulator.Run(pack, build, Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", head), new()
        {
            Duration = duration,
            Mode = mode,
            Iterations = fights,
            Seed = 1,
            IncludeSkills = true,
            CaptureFirstFightEvents = true
        }, TestContext.Current.CancellationToken);

    private static DummyFightResult ZoneTargets(CompiledPackExpressions pack, BuildSnapshot build, ImmutableDictionary<string, string>? settings = null,
        string target = "6", double duration = 20, double after = 0, string enemyHits = "0") =>
        DummyWeaponSimulator.RunAgainstTargets(pack, build, (settings ?? Context).Add("practical_hit_rate", "100").Add("practical_headshot_share", "0")
            .Add("enemy_target", target).Add("enemy_difficulty", "1").Add("enemy_hit_rate", enemyHits), new()
            {
                Duration = duration,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                IncludeIncoming = true,
                IncludeSkills = true,
                DriveSimpleBuffs = true,
                SkillUse = after > 0 ? ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = after })
                    : ImmutableDictionary<string, SkillUseCondition>.Empty,
                CaptureFirstFightEvents = true
            }, cancellationToken: TestContext.Current.CancellationToken);

    private static DummyEvent[] ZoneShots(DummyFightResult result) => [.. result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot && entry.Damage > 0)];
    private static double[] Amounts(IEnumerable<DummyEvent> shots) => [.. shots.Select(shot => Math.Round(shot.Damage, 3)).Distinct().Order()];

    [Fact]
    public void AZoneHitDealsHeadshotDamageForTheMark()
    {
        var pack = ZonePack();
        var result = ZoneDummy(pack, ZoneBuild(pack.Pack));
        var shots = ZoneShots(result);
        // With every body hit on a zone, the 10 s of the mark deal what a headshot does, critical or not, and body damage after.
        Assert.Equal(Amounts(ZoneShots(ZoneDummy(pack, ZoneBuild(pack.Pack, skill: null), head: "100"))), Amounts(shots.Where(shot => shot.Time < MarkSeconds)));
        Assert.Equal(Amounts(ZoneShots(ZoneDummy(pack, ZoneBuild(pack.Pack, skill: null)))), Amounts(shots.Where(shot => shot.Time >= MarkSeconds)));
        Assert.Equal(shots.Count(shot => shot.Time < MarkSeconds), result.ZoneHits!.Value, 9);
        // Headshots take no zone, and a fight without the skill reports none.
        Assert.Equal(0, ZoneDummy(pack, ZoneBuild(pack.Pack), head: "100").ZoneHits);
        Assert.Null(ZoneDummy(pack, ZoneBuild(pack.Pack, skill: null)).ZoneHits);
    }

    [Fact]
    public void ExpertiseMultipliesTheZonesMultiplier()
    {
        var pack = ZonePack();
        var body = Amounts(ZoneShots(ZoneDummy(pack, ZoneBuild(pack.Pack, skill: null))));
        var head = Amounts(ZoneShots(ZoneDummy(pack, ZoneBuild(pack.Pack, skill: null), head: "100")));
        var zoned = Amounts(ZoneShots(ZoneDummy(pack, ZoneBuild(pack.Pack, rank: 30))).Where(shot => shot.Time < MarkSeconds));
        // 1.3 times the whole headshot multiplier, with critical damage added to it as for a critical headshot.
        Assert.Equal(2, zoned.Length);
        Assert.Equal(head[0] * 1.3, zoned[0], 2);
        Assert.Equal(body[1] - body[0] + head[0] * 1.3, zoned[1], 2);
    }

    [Fact]
    public void TheShareAndTheZonesHealthLimitZoneHits()
    {
        // A share of 0 marks but hits no zone, and draws nothing: the shots are those of a fight without the skill.
        var none = ZonePack(share: "0");
        var unmarked = ZoneDummy(none, ZoneBuild(none.Pack, skill: null));
        var idle = ZoneDummy(none, ZoneBuild(none.Pack));
        Assert.Equal(ZoneShots(unmarked).Select(shot => (shot.Time, shot.Damage)), ZoneShots(idle).Select(shot => (shot.Time, shot.Damage)));
        Assert.Equal(0, idle.ZoneHits);
        // A quarter of them, over many fights, and a share past 1 is held to 1.
        var quarter = ZonePack(share: "0.25");
        var shotsInMark = ZoneShots(idle).Count(shot => shot.Time < MarkSeconds);
        Assert.InRange(ZoneDummy(quarter, ZoneBuild(quarter.Pack), fights: 200).ZoneHits!.Value / shotsInMark, 0.23, 0.27);
        var over = ZonePack(share: "3");
        Assert.Equal(shotsInMark, ZoneDummy(over, ZoneBuild(over.Pack)).ZoneHits!.Value, 9);
        // Two zones' health is one pool: zone hits stop with the one that spends the last of it.
        foreach (var count in new[] { 1, 2 })
        {
            var small = ZonePack(health: "1000000", count: count);
            var spent = 0d;
            var expected = ZoneShots(ZoneDummy(small, ZoneBuild(small.Pack))).TakeWhile(shot => { var left = spent < count * 1_000_000; spent += shot.Damage; return left; }).Count();
            Assert.Equal(expected, ZoneDummy(small, ZoneBuild(small.Pack)).ZoneHits!.Value, 9);
            Assert.True(expected < shotsInMark);
        }
    }

    [Fact]
    public void AnAverageFightTakesTheMeanZoneHit()
    {
        var pack = ZonePack(share: "0.5");
        var average = ZoneDummy(pack, ZoneBuild(pack.Pack), head: "25", mode: SimulationMode.Average);
        var random = ZoneDummy(pack, ZoneBuild(pack.Pack), head: "25", fights: 400);
        Assert.Equal(random.ZoneHits!.Value, average.ZoneHits!.Value, random.ZoneHits.Value * 0.02);
        Assert.Equal(random.WeaponDps.Mean, average.WeaponDps.Mean, random.WeaponDps.Mean * 0.01);
        Assert.True(average.WeaponDps.Mean > ZoneDummy(pack, ZoneBuild(pack.Pack, skill: null), head: "25", mode: SimulationMode.Average).WeaponDps.Mean * 1.02);
        // A pool ends the mean's zone hits too.
        var small = ZonePack(share: "0.5", health: "1000000");
        Assert.InRange(ZoneDummy(small, ZoneBuild(small.Pack), head: "25", mode: SimulationMode.Average).ZoneHits!.Value, 1, average.ZoneHits.Value / 2);
    }

    [Fact]
    public void AZoneHitStaysABodyHit()
    {
        // A buff on headshots never turns on from zone hits; one on body hits does.
        double Seconds(string on)
        {
            var zoned = ZonePack();
            var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(zoned.Pack with
            {
                Simulation = zoned.Pack.Simulation with
                {
                    Triggers = [.. zoned.Pack.Simulation.Triggers.Select(rule => rule.Talent != "gear_talent_wicked" ? rule
                        : new() { Talent = rule.Talent, Key = rule.Key, Kind = "buff", On = on, Duration = 100 })]
                }
            }).Compiled);
            return ZoneTargets(pack, WithTalent(pack.Pack, ZoneBuild(pack.Pack), "gear_talent_wicked")).TimedTriggerUptime["wicked_active"].Seconds;
        }
        Assert.Equal(0, Seconds("headshot"));
        Assert.True(Seconds("body_hit") > 19);
    }

    [Fact]
    public void TheMarkSetsTargetPulsedOnTheMarkedEnemy()
    {
        // With no zone hits, Spotter (+15% to pulsed enemies) follows the mark: the first 10 s against one enemy that lives.
        var pack = ZonePack(share: "0");
        var build = WithTalent(pack.Pack, ZoneBuild(pack.Pack), "gear_talent_spotter");
        var tough = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Stats = [.. pack.Pack.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "1000000000", Max = null } : stat)]
        }).Compiled);
        var plain = Amounts(ZoneShots(ZoneTargets(tough, ZoneBuild(tough.Pack, skill: null))).Skip(30));
        // Past the enemy's armor, so every shot takes the same health factor.
        double[] Read(DummyFightResult result, Func<DummyEvent, bool> when) => Amounts(ZoneShots(result).Skip(30).Where(when));
        var marked = ZoneTargets(tough, build);
        Assert.Equal([.. plain.Select(amount => Math.Round(amount * 1.15, 1))], Read(marked, shot => shot.Time < MarkSeconds).Select(amount => Math.Round(amount, 1)));
        Assert.Equal(plain, Read(marked, shot => shot.Time >= MarkSeconds));
        // The user's own setting stays on throughout.
        Assert.Equal([.. plain.Select(amount => Math.Round(amount * 1.15, 1))],
            Read(ZoneTargets(tough, build, Context.Add("target_pulsed", "true")), _ => true).Select(amount => Math.Round(amount, 1)));
        // Without a key the mark sets nothing.
        var keyless = ZonePack(share: "0", key: false);
        var unkeyed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(keyless.Pack with { Stats = tough.Pack.Stats }).Compiled);
        Assert.Equal(plain, Read(ZoneTargets(unkeyed, build), _ => true));
    }

    [Fact]
    public void TheMarkGoesWithItsEnemy()
    {
        // Target 1 dies well within the mark: the zone hits and the pulsed key stop with it, and the next enemy is unmarked.
        var pack = ZonePack();
        var build = WithTalent(pack.Pack, ZoneBuild(pack.Pack), "gear_talent_spotter");
        var result = ZoneTargets(pack, build, target: "1");
        var firstKill = result.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.Kill).Time;
        Assert.InRange(firstKill, 0.1, MarkSeconds - 3);
        Assert.Equal(ZoneShots(result).Count(shot => shot.Time <= firstKill), result.ZoneHits!.Value, 9);
        // So does the pulsed key: with no zone hits and one use, nothing after the first kill is amplified.
        var pulsedOnly = ZonePack(share: "0");
        var amplified = ZoneTargets(pulsedOnly, build, target: "1");
        var plain = Amounts(ZoneShots(ZoneTargets(pulsedOnly, ZoneBuild(pulsedOnly.Pack, skill: null), target: "1")));
        var dead = amplified.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.Kill).Time;
        Assert.Contains(ZoneShots(amplified), shot => shot.Time <= dead && shot.Damage > plain[^1] * 1.1);
        Assert.DoesNotContain(ZoneShots(amplified), shot => shot.Time > dead && shot.Damage > plain[^1] * 1.1);
        // A second use, 45 s after the first (40 s of cooldown, started 5 s in), marks the enemy then.
        var first = ZoneDummy(pack, build, duration: 45).ZoneHits;
        Assert.Equal(ZoneDummy(pack, build, duration: 10).ZoneHits, first);
        var second = ZoneDummy(pack, build, duration: 60);
        Assert.Equal(first!.Value + ZoneShots(second).Count(shot => shot.Time >= 45 && shot.Time < 55), second.ZoneHits!.Value, 9);
        Assert.True(second.ZoneHits > first);
    }

    [Fact]
    public void AgainstAnEnemyThePoolIsSpentByWhatItTakes()
    {
        // The enemy's factors count: hits on a zone stop with the one whose dealt damage spends the last of the pool.
        var pack = ZonePack(health: "1000000", key: false);
        var tough = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Stats = [.. pack.Pack.Stats.Select(stat => stat.ID switch
            {
                "enemy_target_health" => stat with { Formula = "1000000000", Max = null },
                "damage_to_armor" => stat with { Formula = "0.5", Max = null },
                _ => stat
            })]
        }).Compiled);
        var result = ZoneTargets(tough, ZoneBuild(tough.Pack));
        var spent = 0d;
        var expected = ZoneShots(result).TakeWhile(shot => { var left = spent < 1_000_000; spent += shot.Damage; return left; }).Count();
        Assert.Equal(expected, result.ZoneHits!.Value, 9);
        Assert.InRange(expected, 2, 20);
    }

    [Fact]
    public void UnderFocusFireTheMarkWaitsOnItsEnemy()
    {
        // Two tough enemies, the player moving between them every 2 s: zone hits stop while the other is shot and go on when the
        // player is back, within the same mark.
        var pack = ZonePack();
        var tough = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Stats = [.. pack.Pack.Stats.Select(stat => stat.ID == "enemy_target_armor" ? stat with { Formula = "1000000000", Max = null } : stat)]
        }).Compiled);
        DummyFightResult Fight(string? skill) => DummyWeaponSimulator.RunAgainstTargets(tough, ZoneBuild(tough.Pack, skill),
            Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "0").Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
            {
                Duration = 12,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                IncludeIncoming = true,
                IncludeSkills = true,
                DriveSimpleBuffs = true,
                CaptureFirstFightEvents = true,
                EnemiesAtOnce = 2,
                FocusSeconds = 2
            }, cancellationToken: TestContext.Current.CancellationToken);
        var body = Amounts(ZoneShots(Fight(null)));
        var result = Fight("skill_achilles_pulse");
        var zoned = ZoneShots(result).Where(shot => !body.Contains(Math.Round(shot.Damage, 3))).Select(shot => shot.Time).ToArray();
        Assert.Equal(zoned.Length, result.ZoneHits!.Value, 9);
        Assert.All(zoned, time => Assert.True(time < MarkSeconds));
        // The first stay, then nothing until a later stay: a gap as long as the other's.
        var gap = zoned.Zip(zoned.Skip(1), (before, after) => after - before).Max();
        Assert.True(gap > 2, $"{gap}");
        Assert.Contains(ZoneShots(result), shot => shot.Time < MarkSeconds && body.Contains(Math.Round(shot.Damage, 3)));
    }

    [Fact]
    public void AUseInAKillsGapMarksNobody()
    {
        // The skill plays no part before its first use, so the first kill comes when it would without it.
        var pack = ZonePack();
        var build = ZoneBuild(pack.Pack);
        var plain = ZoneTargets(pack, ZoneBuild(pack.Pack, skill: null), target: "1");
        var kill = plain.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.Kill).Time;
        var next = ZoneShots(plain).First(shot => shot.Time > kill).Time;
        Assert.Equal(0, ZoneTargets(pack, build, target: "1", after: (kill + Math.Min(next, kill + 1)) / 2).ZoneHits);
        // Used as it arrives, it marks the next enemy.
        Assert.True(ZoneTargets(pack, build, target: "1", after: next).ZoneHits > 0);
    }

    [Fact]
    public void ANoteSaysWhatAZoneIs()
    {
        var pack = ZonePack();
        Assert.Contains(ZoneTargets(pack, ZoneBuild(pack.Pack)).Limitations, note => note.StartsWith("A skill marking zones", StringComparison.Ordinal) && note.Contains("Target is pulsed holds", StringComparison.Ordinal));
        Assert.DoesNotContain(ZoneTargets(pack, ZoneBuild(pack.Pack, skill: null)).Limitations, note => note.StartsWith("A skill marking zones", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheMarkStaysThroughASwap(bool driven)
    {
        // Swapping when a magazine empties, on the dummy or in a driven fight against one enemy whose armor holds: the mark stays on the
        // enemy, and each weapon's zone hits deal its own headshot's damage.
        CompiledPackExpressions Tough(string health)
        {
            var zoned = ZonePack(health: health);
            return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(zoned.Pack with
            {
                Stats = [.. zoned.Pack.Stats.Select(stat => stat.ID == "enemy_target_armor" ? stat with { Formula = "1000000000", Max = null } : stat)]
            }).Compiled);
        }
        var pack = Tough("1000000000000");
        DummyFightResult Fight(string? skill, string head)
        {
            // No talents: the rifle's default here, Breadbasket, would raise its zone hits as it's driven.
            var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("sidearm", "sidearm_d50");
            if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
            var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", head);
            var options = new DummyFightOptions
            {
                Duration = 20,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                IncludeSkills = skill is not null,
                CaptureFirstFightEvents = true,
                Rotation = new RotationOptions("sidearm")
            };
            return !driven ? DummyWeaponSimulator.Run(pack, build, context, options, TestContext.Current.CancellationToken)
                : DummyWeaponSimulator.RunAgainstTargets(pack, build, context.Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"),
                    options with { IncludeIncoming = true, DriveSimpleBuffs = true }, cancellationToken: TestContext.Current.CancellationToken);
        }
        var result = Fight("skill_achilles_pulse", "0");
        var body = ZoneShots(Fight(null, "0"));
        var heads = ZoneShots(Fight(null, "100"));
        Assert.Equal(ZoneShots(result).Count(shot => shot.Time < MarkSeconds), result.ZoneHits!.Value, 9);
        foreach (var slot in new[] { "primary_weapon", "sidearm" })
        {
            double[] Of(IEnumerable<DummyEvent> shots) => Amounts(shots.Where(shot => shot.WeaponSlot == slot));
            var during = Of(ZoneShots(result).Where(shot => shot.Time < MarkSeconds));
            var after = Of(ZoneShots(result).Where(shot => shot.Time >= MarkSeconds));
            Assert.NotEmpty(during);
            Assert.NotEmpty(after);
            Assert.Subset(Of(heads).ToHashSet(), during.ToHashSet());
            Assert.Subset(Of(body).ToHashSet(), after.ToHashSet());
        }
        // One pool for both weapons: the sidearm's first zone hit spends what the rifle's left of it.
        var rifle = ZoneShots(result).TakeWhile(shot => shot.WeaponSlot == "primary_weapon").ToArray();
        var first = ZoneShots(result)[rifle.Length];
        Assert.Equal("sidearm", first.WeaponSlot);
        Assert.True(first.Time < MarkSeconds);
        pack = Tough(Math.Round(rifle.Sum(shot => shot.Damage) + first.Damage / 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(rifle.Length + 1, Fight("skill_achilles_pulse", "0").ZoneHits!.Value, 9);
    }

    [Fact]
    public void ZoneHitsBuildBreadbasketAndNeverSpendIt()
    {
        // Breadbasket gains a stack of +55% headshot damage on each body hit, up to 3, spent by a headshot. A zone hit takes the headshot
        // damage in its multiplier and stays a body hit, so with no headshots the stacks build and stay within the mark.
        var pack = ZonePack();
        var tough = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Stats = [.. pack.Pack.Stats.Select(stat => stat.ID switch
            {
                "enemy_target_armor" => stat with { Formula = "1000000000", Max = null },
                "effective_critical_hit_chance" => stat with { Formula = "0" },
                _ => stat
            })]
        }).Compiled);
        var plain = BuildSnapshot.Create(tough.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var body = ZoneShots(ZoneTargets(tough, plain))[0].Damage;
        // The first magazine's hits, as multiples of a body hit.
        double[] Multiples(BuildSnapshot build, string? skill) => [.. ZoneShots(ZoneTargets(tough, skill is null ? build : build.WithActiveSkillSelection("skill_slot_1", skill)))
            .Take(30).Select(shot => Math.Round(shot.Damage / body, 6))];
        Assert.Equal([1.55], Multiples(plain, "skill_achilles_pulse").Distinct());
        var breadbasket = plain.WithTalentSelection("primary_weapon", "talent", "weapon_talent_breadbasket");
        Assert.Equal([1], Multiples(breadbasket, null).Distinct());
        Assert.Equal([1.55, 2.1, 2.65, .. Enumerable.Repeat(3.2, 27)], Multiples(breadbasket, "skill_achilles_pulse"));
    }

    [Fact]
    public void AMixOfEnemiesIsMarkedOneByOne()
    {
        // Target 1, which dies within the mark, then target 2: the first's mark goes with it, and a use on the second marks that one.
        var pack = ZonePack();
        DummyFightResult Fight(string? skill, double after) => DummyWeaponSimulator.RunAgainstTargets(pack, ZoneBuild(pack.Pack, skill),
            Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "0").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
            {
                Duration = 20,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                IncludeIncoming = true,
                IncludeSkills = skill is not null,
                DriveSimpleBuffs = true,
                TargetSequence = [1, 2],
                SkillUse = after > 0 ? ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = after })
                    : ImmutableDictionary<string, SkillUseCondition>.Empty,
                CaptureFirstFightEvents = true
            }, cancellationToken: TestContext.Current.CancellationToken);
        static double Kill(DummyFightResult result, int nth) => result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Kill).ElementAt(nth).Time;
        var marked = Fight("skill_achilles_pulse", 0);
        Assert.True(Kill(marked, 0) < MarkSeconds - 3);
        Assert.Equal(ZoneShots(marked).Count(shot => shot.Time <= Kill(marked, 0)), marked.ZoneHits!.Value, 9);
        var plain = Fight(null, 0);
        var next = ZoneShots(plain).First(shot => shot.Time > Kill(plain, 0)).Time;
        var later = Fight("skill_achilles_pulse", next);
        Assert.Equal(Kill(plain, 0), Kill(later, 0));
        var second = Kill(later, 1);
        Assert.True(second < Kill(plain, 1) && second < next + MarkSeconds);
        Assert.Equal(ZoneShots(later).Count(shot => shot.Time >= next && shot.Time <= second), later.ZoneHits!.Value, 9);
    }

    [Fact]
    public void RegenerationFollowsTheMarksKeyToItsEnd()
    {
        // Armor regenerating only against a pulsed enemy regenerates to the mark's end, not to the next shot or enemy hit that reads it.
        const double perSecond = 1000;
        var zoned = ZonePack(share: "0");
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(zoned.Pack with
        {
            Stats = [.. zoned.Pack.Stats.Select(stat => stat.ID switch
            {
                "enemy_target_health" => stat with { Formula = "1000000000", Max = null },
                "character_armor" => stat with { Formula = "1000000000", Max = null },
                _ => stat
            })],
            Talents = [.. zoned.Pack.Talents.Select(talent => talent.ID != "gear_talent_spotter" ? talent
                : talent with { Modifiers = [talent.Modifiers[0] with { Stat = "armor_regeneration", Op = "flat", Value = new ModifierValue(perSecond) }] })]
        }).Compiled);
        // Used between shots and between enemy hits, after the first: the armor is below full from then, and the mark ends between hits too.
        const double use = 0.3;
        var result = ZoneTargets(pack, WithTalent(pack.Pack, ZoneBuild(pack.Pack), "gear_talent_spotter"), Context, after: use, enemyHits: "100");
        var moments = result.FirstFightEvents.Where(entry => entry.Kind is DummyEventKind.EnemyHit or DummyEventKind.Shot).Select(entry => entry.Time).ToArray();
        Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.EnemyHit && entry.Time < use);
        foreach (var moment in new[] { use, use + MarkSeconds }) { Assert.True(moments.Min(at => Math.Abs(at - moment)) > 0.01); }
        Assert.True(moments[^1] > use + MarkSeconds + 1);
        Assert.Equal(perSecond * MarkSeconds, result.Incoming!.ArmorRepaired, 6);
    }

    [Fact]
    public void ASkillHeldForTheMarksKeyIsUsedAsItLands()
    {
        // The Achilles Pulse in the second slot, used after 7.03 s, between shots; the sticky bomb in the first waits for its key.
        const double use = 7.03;
        DummyFightResult Fight(bool key)
        {
            var pack = ZonePack(share: "0", key: key);
            var build = ZoneBuild(pack.Pack, skill: null).WithActiveSkillSelection("skill_slot_1", "skill_explosive_sticky_bomb").WithActiveSkillSelection("skill_slot_2", "skill_achilles_pulse");
            return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "0").Add("enemy_target", "6")
                .Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
                {
                    Duration = 20,
                    Mode = SimulationMode.Random,
                    Iterations = 1,
                    Seed = 1,
                    IncludeIncoming = true,
                    IncludeSkills = true,
                    DriveSimpleBuffs = true,
                    SkillUse = ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.KeyOn, Key = "target_pulsed" })
                        .Add("skill_slot_2", new() { When = SkillUseCondition.AfterSeconds, Seconds = use }),
                    CaptureFirstFightEvents = true
                }, cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Equal(new SkillSlotUse(1, use), Fight(key: true).SkillUses["skill_slot_1"]);
        // A mark setting no key leaves nothing to wait for.
        Assert.Equal("skill-use-unsupported", Assert.Throws<SimulationException>(() => Fight(key: false)).Code);
    }

    [Fact]
    public void TheCommittedRuleAndItsStatsAreAsR163ReadThem()
    {
        var pack = CommittedPack();
        var rule = pack.Pack.Simulation.Skills.Single(entry => entry.Skill == "skill_achilles_pulse");
        Assert.Equal(("effect_duration_seconds", "cooldown_seconds", 5), (rule.Window, rule.Cooldown, rule.CooldownAfter));
        Assert.Equal(new SimSkillZones { Count = "zone_capacity", Health = "zone_health_reference", Factor = "zone_damage_factor", Share = "practical_zone_fraction", Multiplier = "zone_hit_multiplier", Key = "target_pulsed" }, rule.Zones);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_achilles_pulse");
        // The share's setting, 0% by default; the multiplier is the weapon in hand's headshot's.
        double Stat(string id, ImmutableDictionary<string, string> context) => BuildResolver.Resolve(pack, build, context).Calculation.Stats[id];
        Assert.Equal(0, Stat("practical_zone_fraction", Context));
        Assert.Equal(0.25, Stat("practical_zone_fraction", Context.Add("practical_zone_share", "25")), 9);
        Assert.Equal(1.55, Stat("zone_hit_multiplier", Context), 9);
        // The factor: 0.01 a grade, Alternating Current's 0.025 a stack and Rigger's 0.25 (0.5 with Best Buds) while its buff is on.
        double Factor(BuildSnapshot of, ImmutableDictionary<string, string> context) =>
            BuildResolver.Resolve(pack, of, context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(output => output.Id == "zone_damage_factor").Value;
        Assert.Equal(1, Factor(build, Context));
        Assert.Equal(1.3, Factor(build.WithSkillUpgrade("skill_slot_1", "skill_achilles_pulse", 30), Context), 9);
        var current = WithTalent(pack.Pack, build, "gear_talent_alternating_current");
        Assert.Equal(1, Factor(current, Context), 9);
        Assert.Equal(1.25, Factor(current, Context.Add("alternating_current_stacks", "10")), 9);
        var rigger = new[] { "mask", "gloves", "holster" }.Aggregate(build.WithEquipment("knees", "knees_rigger_kneepads"), (next, slot) => next.WithEquipment(slot, $"{slot}_rigger_{slot}"));
        var buffed = Context.Add("rigger_skill_buff_active", "true");
        Assert.Equal(1, Factor(rigger, Context), 9);
        Assert.Equal(1.25, Factor(rigger, buffed), 9);
        Assert.Equal(1.5, Factor(WithTalent(pack.Pack, rigger, "gear_talent_best_buds"), buffed), 9);
    }

    [Fact]
    public void TheCommittedSettingDrivesZoneHits()
    {
        // At the default 0% a fight with the Achilles Pulse is the fight without it, and reports no zone hit; at 100% one zone of 398,174
        // is spent by the zone hits whose damage passes it: 1.55 times 58,896.5 each on this ACR without a critical, so five, or four with enough criticals.
        var pack = CommittedPack();
        DummyFightResult Fight(string? skill, string? share)
        {
            var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
            if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
            return ZoneTargets(pack, build, share is null ? Context : Context.Add("practical_zone_share", share), duration: 10);
        }
        var plain = Fight(null, null);
        var idle = Fight("skill_achilles_pulse", null);
        Assert.Equal(ZoneShots(plain).Select(shot => (shot.Time, shot.Damage)), ZoneShots(idle).Select(shot => (shot.Time, shot.Damage)));
        Assert.Equal(0, idle.ZoneHits);
        var zoned = Fight("skill_achilles_pulse", "100");
        var spent = 0d;
        var expected = ZoneShots(zoned).TakeWhile(shot => { var left = spent < 398_174; spent += shot.Damage; return left; }).Count();
        Assert.Equal(expected, zoned.ZoneHits);
        Assert.InRange(expected, 4, 5);
        Assert.Null(Fight(null, "100").ZoneHits);
    }
}
