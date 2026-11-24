using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0034: the Trophy System Drone and Decoy, on the committed rows (R159, r159.candidate.1; the owner's calls, 2026-11-22). While
/// the drone is out, each landing hit is removed at its reflection rate (32% at tier 0); while the Decoy is out, landing hits land on
/// it, each spending decoy_damage_share of the raw hit (0.4 an elite's, 0.2 others'), until its health is spent.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string TrophyKey = "trophy_system_active";

    // The committed pack, the Trophy's share replaced by a flat one if asked.
    private static CompiledPackExpressions TrophyPack(double? share = null)
    {
        var committed = CommittedPack();
        if (share is null) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != "skill_trophy_system_drone" ? skill : skill with
            {
                Modifiers = [.. skill.Modifiers.Select(modifier => modifier.Stat == EnemyFireSchedule.RemovedShareStat ? modifier with { Value = new(share.Value) } : modifier)]
            })]
        }).Compiled);
    }

    private static DummyFightResult TrophyFight(CompiledPackExpressions pack, string? skill = "skill_trophy_system_drone", int? enemies = null, double duration = 15,
        string hitRate = "100", bool driven = true, string target = "6")
    {
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads");
        if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "90").Add("enemy_target", target).Add("enemy_difficulty", "1").Add("enemy_hit_rate", hitRate), new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = driven,
            CaptureFirstFightEvents = true,
            EnemiesAtOnce = enemies
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheTrophyRemovesAShareOfHitsWhileOut()
    {
        var result = TrophyFight(TrophyPack());
        double[] Times(DummyEventKind kind) => [.. result.FirstFightEvents.Where(entry => entry.Kind == kind).Select(entry => entry.Time)];
        var blocked = Times(DummyEventKind.EnemyShotBlocked);
        var hits = Times(DummyEventKind.EnemyHit);
        // Every attempt lands at a 100% hit rate; while the drone is out (its 10 s), about a third are removed, and none after.
        Assert.Equal(10, Assert.Contains(TrophyKey, result.TimedTriggerUptime).Seconds, 9);
        Assert.All(blocked, time => Assert.True(time < 10));
        var share = (double)blocked.Length / (blocked.Length + hits.Count(time => time < 10));
        Assert.InRange(share, 0.2, 0.45);
        Assert.Contains(hits, time => time >= 10);
        Assert.Equal(blocked.Length, result.Incoming!.RemovedHits!.Value, 9);
        Assert.Equal(0, result.Incoming.BlockedShots);
        // Without the drone, nothing is removed.
        Assert.DoesNotContain(TrophyFight(TrophyPack(), skill: null).FirstFightEvents, entry => entry.Kind == DummyEventKind.EnemyShotBlocked);
    }

    [Fact]
    public void TheShareIsHeldToOneAndReadForEveryEnemy()
    {
        // A share past 1 is held to 1 by the engine itself (the stat's own cap taken off) and removes every landing hit while the drone
        // is out, from each of two enemies firing at once.
        var capped = TrophyPack(share: 1.5);
        var uncapped = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(capped.Pack with
        {
            Stats = [.. capped.Pack.Stats.Select(stat => stat.ID == EnemyFireSchedule.RemovedShareStat ? stat with { Max = null } : stat)]
        }).Compiled);
        var result = TrophyFight(uncapped, enemies: 2);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.EnemyHit && entry.Time < 10);
        Assert.True(result.Incoming!.RemovedHits > 0);
        // A pack without the stat reports none.
        var committed = CommittedPack();
        var without = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Stats = [.. committed.Pack.Stats.Where(stat => stat.ID != EnemyFireSchedule.RemovedShareStat)],
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill with { Modifiers = [.. skill.Modifiers.Where(modifier => modifier.Stat != EnemyFireSchedule.RemovedShareStat)] })]
        }).Compiled);
        Assert.Null(TrophyFight(without, skill: null).Incoming!.RemovedHits);
        Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.EnemyHit && entry.Time >= 10);
    }

    [Fact]
    public void AStaticFightRemovesNoHits()
    {
        // A fight that drives no keys has nothing out to hold the share, even with its key set by the user.
        var pack = TrophyPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithActiveSkillSelection("skill_slot_1", "skill_trophy_system_drone");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add(TrophyKey, "true"), new()
        {
            Duration = 15,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, result.Incoming!.RemovedHits);
    }

    [Fact]
    public void AHitRemovedIsOneThatWouldHaveLanded()
    {
        // The removal reads the hit's own roll, so no draw moves: each attempt landing with the drone out is one that lands without it,
        // and the removed ones are the rest of those that land without it in the drone's 10 s.
        var pack = TrophyPack();
        var noRemoval = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            ActiveSkills = [.. pack.Pack.ActiveSkills.Select(skill => skill.ID != "skill_trophy_system_drone" ? skill : skill with
            {
                Modifiers = [.. skill.Modifiers.Where(modifier => modifier.Stat != EnemyFireSchedule.RemovedShareStat)]
            })]
        }).Compiled);
        double[] Times(DummyFightResult result, DummyEventKind kind, double before) =>
            [.. result.FirstFightEvents.Where(entry => entry.Kind == kind && entry.Time < before).Select(entry => entry.Time)];
        var removing = TrophyFight(pack);
        var keeping = TrophyFight(noRemoval);
        // Through the drone's window the landed and removed attempts make up the hits the fight without removal takes, until that
        // player is downed (at 100% hits, about 8 s in).
        var until = keeping.FirstFightEvents.FirstOrDefault(entry => entry.Kind == DummyEventKind.Downed)?.Time ?? 10;
        Assert.True(until > 5);
        var landed = Times(removing, DummyEventKind.EnemyHit, until).Concat(Times(removing, DummyEventKind.EnemyShotBlocked, until)).Order().ToArray();
        Assert.Equal(Times(keeping, DummyEventKind.EnemyHit, until), landed);
        Assert.NotEmpty(Times(removing, DummyEventKind.EnemyShotBlocked, until));
    }

    // The committed pack, without the Decoy's share stat or with its health replaced if asked.
    private static CompiledPackExpressions DecoyPack(bool share = true, string? health = null)
    {
        var committed = CommittedPack();
        if (share && health is null) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Stats = share ? committed.Pack.Stats : [.. committed.Pack.Stats.Where(stat => stat.ID != EnemyFireSchedule.DecoyShareStat)],
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != "skill_decoy" || health is null ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID == "skill_health" ? output with { Formula = health } : output)]
            })]
        }).Compiled);
    }

    [Fact]
    public void TheDecoyDrawsFireUntilItsHealthRunsOut()
    {
        // At a 100% hit rate every attempt lands. Its full health (929,072 at tier 0) outlasts its 15 s against difficulty 1's hits, so it
        // draws every hit until its window ends, and the player takes the first after.
        var full = TrophyFight(DecoyPack(), skill: "skill_decoy", duration: 20);
        double[] Times(DummyFightResult result, DummyEventKind kind) => [.. result.FirstFightEvents.Where(entry => entry.Kind == kind).Select(entry => entry.Time)];
        var drawn = Times(full, DummyEventKind.EnemyShotDrawn);
        Assert.All(drawn, time => Assert.True(time < 15));
        Assert.DoesNotContain(Times(full, DummyEventKind.EnemyHit), time => time < 15);
        Assert.Contains(Times(full, DummyEventKind.EnemyHit), time => time >= 15);
        Assert.Equal(drawn.Length, full.Incoming!.DrawnHits!.Value, 9);
        // With 100,000 health against target 6, an elite, it's spent after 11 hits (0.4 of each raw hit of 24,433): its use ends and the
        // next hit is the player's. Against target 1, a normal enemy, each hit spends 0.2 of its raw damage instead.
        var small = TrophyFight(DecoyPack(health: "100000"), skill: "skill_decoy", duration: 20);
        var smallDrawn = Times(small, DummyEventKind.EnemyShotDrawn);
        Assert.Equal(11, smallDrawn.Length);
        var firstHit = Times(small, DummyEventKind.EnemyHit).Min();
        Assert.True(firstHit > smallDrawn.Max() && firstHit < 5, $"{firstHit} after {smallDrawn.Max()}");
        // Without the share stat each hit spends its whole raw damage, so the same decoy lasts 4 or 5 hits.
        var whole = Times(TrophyFight(DecoyPack(share: false, health: "100000"), skill: "skill_decoy", duration: 20), DummyEventKind.EnemyShotDrawn);
        Assert.InRange(whole.Length, 4, 5);
    }

    [Fact]
    public void EachDecoyUseDrawsFireAtFullHealthInDrivenAndStaticFights()
    {
        // Two uses in 60 s at a 20% hit rate: 0 s to 15 s, then 25 s after it ends (40 s to 55 s), each drawing every landing hit from full
        // health; between and after, hits land on the player. A static fight deploys each use as it comes too.
        foreach (var driven in new[] { true, false })
        {
            var result = TrophyFight(DecoyPack(), skill: "skill_decoy", duration: 60, hitRate: "20", driven: driven);
            double[] Times(DummyEventKind kind) => [.. result.FirstFightEvents.Where(entry => entry.Kind == kind).Select(entry => entry.Time)];
            var drawn = Times(DummyEventKind.EnemyShotDrawn);
            Assert.Contains(drawn, time => time < 15);
            Assert.Contains(drawn, time => time >= 40 && time < 55);
            Assert.All(drawn, time => Assert.True(time < 15 || time >= 40 && time < 55, $"{time} (driven {driven})"));
            Assert.DoesNotContain(Times(DummyEventKind.EnemyHit), time => time < 15 || time >= 40 && time < 55);
            Assert.Contains(Times(DummyEventKind.EnemyHit), time => time >= 15 && time < 40);
        }
    }

    [Fact]
    public void TheDecoyTakesTwiceTheShareOfAnElitesHit()
    {
        // decoy_damage_share is 0.4 against an elite (target 6, role 3) or named (target 7, role 4) and 0.2 against a normal (target 1)
        // or veteran (target 4, role 2) enemy.
        var pack = CommittedPack();
        double Share(string target) => BuildResolver.Resolve(pack, BuildSnapshot.Create(pack.Pack),
            Context.Add("enemy_target", target)).Calculation.Stats[EnemyFireSchedule.DecoyShareStat];
        Assert.Equal(0.4, Share("6"), 9);
        Assert.Equal(0.2, Share("1"), 9);
        Assert.Equal(0.2, Share("4"), 9);
        Assert.Equal(0.4, Share("7"), 9);
    }

    [Fact]
    public void TheTrophysShareIsItsReflectionRateWhileOut()
    {
        // At tier 0 the key raises enemy_hit_removed_share to 0.32, the drone's own reflection rate; without the key it's 0.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", "skill_trophy_system_drone");
        double Share(ImmutableDictionary<string, string> context) => BuildResolver.Resolve(pack, build, context).Calculation.Stats[EnemyFireSchedule.RemovedShareStat];
        Assert.Equal(0, Share(Context));
        Assert.Equal(0.32, Share(Context.Add(TrophyKey, "true")), 9);
    }
}
