using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0032: a skill whose use holds a buff (skill_out), on the committed rows (R154, r154.candidate.1): the Defender Drone's cut on
/// every enemy hit, the Fortified Smart Cover's bonus armor while in cover (owner's calls, 2026-11-22).
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string DefenderKey = "defender_drone_active";
    private const string FortifiedKey = "fortified_cover_active";

    // The committed pack, with the Defender's window cut short if asked.
    private static CompiledPackExpressions HoldingPack(string? defenderSeconds = null)
    {
        var committed = CommittedPack();
        if (defenderSeconds is null) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != "skill_defender_drone" ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID == "duration_seconds" ? output with { Formula = defenderSeconds } : output)]
            })]
        }).Compiled);
    }

    private static DummyFightResult HoldingFight(CompiledPackExpressions pack, string skill, bool triggers = true, double duration = 30, bool cover = false, bool hits = true)
    {
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithActiveSkillSelection("skill_slot_1", skill);
        var context = Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1");
        if (cover) { context = context.Add(DummyFightOptions.CoverInput, "true"); }
        if (!hits) { context = context.Add("enemy_hit_rate", "0"); }
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = triggers,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static DummyEvent[] EnemyHits(DummyFightResult result) => [.. result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.EnemyHit && entry.Damage > 0)];

    [Fact]
    public void TheDefendersUseCutsEveryEnemyHitWhileOut()
    {
        // A 5 s Defender, deployed at 0 s: hits until 5 s take 80% of what they take after, its 20 s cooldown from its end running past.
        var result = HoldingFight(HoldingPack(defenderSeconds: "5"), "skill_defender_drone", duration: 12);
        var hits = EnemyHits(result);
        var during = hits.Where(hit => hit.Time < 5).Select(hit => hit.Damage).Distinct().ToArray();
        var after = hits.Where(hit => hit.Time > 5).Select(hit => hit.Damage).Distinct().ToArray();
        Assert.NotEmpty(during);
        Assert.NotEmpty(after);
        var hit = Assert.Single(after);
        Assert.Equal(hit * 0.8, Assert.Single(during), 6);
        Assert.Equal(5, Assert.Contains(DefenderKey, result.TimedTriggerUptime).Seconds, 9);
    }

    [Fact]
    public void TheFortifiedGivesBonusArmorInCover()
    {
        // In cover as it deploys: half of maximum armor (759,000 with the six Badger Tuff pieces) as bonus armor, once; damage spends it
        // and nothing refills it while the player stays in cover.
        var result = HoldingFight(HoldingPack(), "skill_fortified_smart_cover", cover: true);
        var granted = Assert.Single(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.BonusArmorGranted);
        Assert.Equal(0, granted.Time);
        Assert.Equal(379500, granted.Damage, 3);
        // Out of cover, the key alone gives nothing; with no enemy hits, it's held for the cover's whole 30 s.
        var open = HoldingFight(HoldingPack(), "skill_fortified_smart_cover", hits: false);
        Assert.DoesNotContain(open.FirstFightEvents, entry => entry.Kind == DummyEventKind.BonusArmorGranted);
        Assert.Equal(30, Assert.Contains(FortifiedKey, open.TimedTriggerUptime).Seconds, 9);
    }

    [Fact]
    public void ACoverCycleIsOneStayInCover()
    {
        // The owner's call after R154: peeking from cover is still being in cover (ADR 0009 holds in_cover through a cycle), so a
        // Fortified deployed in a cycle grants its bonus armor once, as it deploys at 0 s during the first peek, and not again.
        var pack = HoldingPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithActiveSkillSelection("skill_slot_1", "skill_fortified_smart_cover");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1"), new()
        {
            Duration = 30,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true,
            PeekSeconds = 3,
            CoverSeconds = 2,
            PeekHitShare = 1
        }, cancellationToken: TestContext.Current.CancellationToken);
        var granted = Assert.Single(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.BonusArmorGranted);
        Assert.Equal(0, granted.Time);
        Assert.Equal(379500, granted.Damage, 3);
    }

    [Fact]
    public void EachUseTurnsTheBuffOnAgain()
    {
        // A 5 s Defender with its 20 s cooldown from its end: out from 0 s to 5 s and from 25 s to 30 s, 10 s in a 32 s fight.
        var result = HoldingFight(HoldingPack(defenderSeconds: "5"), "skill_defender_drone", duration: 32, hits: false);
        Assert.Equal(10, Assert.Contains(DefenderKey, result.TimedTriggerUptime).Seconds, 9);
    }

    [Fact]
    public void AnArtificersRefreshHoldsTheDefendersBuffToItsNewEnd()
    {
        // The Defender (a drone, 20 s) beside a deployed Artificer without pulses, no enemy hits: eight charges 1.6 s apart, each 3 s
        // more, so the drone's buff runs from 0 s to 44 s.
        var pack = HoldingPack();
        pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Skills = [.. pack.Pack.Simulation.Skills.Select(rule => rule.Skill == ArtificerHive ? rule with { HolsteredEvery = 0, HolsteredLag = 0 } : rule)]
            }
        }).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", ArtificerHive)
            .WithActiveSkillSelection("skill_slot_2", "skill_defender_drone");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
        {
            Duration = 60,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(44, Assert.Contains(DefenderKey, result.TimedTriggerUptime).Seconds, 6);
    }

    [Fact]
    public void WithoutTalentTriggersASkillHoldingABuffStepsAside()
    {
        var result = HoldingFight(HoldingPack(), "skill_defender_drone", triggers: false);
        Assert.Contains("Run without skills that turn on a buff while out, since talent triggers are off: Defender Drone", result.Limitations);
    }
}
