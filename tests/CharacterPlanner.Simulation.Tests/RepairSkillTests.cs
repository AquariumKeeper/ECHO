using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0030: repair skills in fights, on the rules from r149.candidate.1 (R149), shaped as R148 reads each skill (owner's calls,
/// 2026-11-21): used once armor is below full, the player in range, checks of the armor at the source's own ticks.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    // The committed pack, whose rule for the skill must be this one field by field; with outputs' formulas replaced where a test needs
    // it (the Restorer's 90 s cooldown cut to 5 s, so its next use comes within a fight).
    private static CompiledPackExpressions RepairPack(SimSkillDefinition rule, params (string Id, string Formula)[] outputs)
    {
        var committed = CommittedPack();
        var actual = committed.Pack.Simulation.Skills.Single(existing => existing.Skill == rule.Skill);
        // Every field, so a delivery adding one this test doesn't expect is caught.
        Assert.Equivalent(rule, actual, strict: true);
        if (outputs.Length == 0) { return committed; }
        var skillOutputs = committed.Pack.ActiveSkills.Single(skill => skill.ID == rule.Skill).PreviewOutputs;
        Assert.All(outputs, change => Assert.Contains(skillOutputs, output => output.ID == change.Id));
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != rule.Skill ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => outputs.FirstOrDefault(change => change.Id == output.ID) is { Id.Length: > 0 } change ? output with { Formula = change.Formula } : output)]
            })]
        }).Compiled);
    }

    private static SimSkillDefinition FixerRule => new()
    {
        Skill = "skill_fixer_drone",
        Repair = "repair",
        RepairEvery = 1,
        Window = "duration_seconds",
        Cooldown = "cooldown_seconds",
        CooldownFromEnd = true,
        UseArmorBelow = 1,
        // Read live from R152 (the owner's call after R151).
        RateRead = "live"
    };

    private static SimSkillDefinition TrapRule => new()
    {
        Skill = "skill_repair_trap",
        Repair = "repair",
        RepairEvery = 0.1,
        RepairLag = 0.3,
        RepairCount = "deployable_capacity",
        Window = "duration_seconds",
        Cooldown = "cooldown_seconds",
        CooldownFromEnd = true,
        UseArmorBelow = 1
    };

    private static SimSkillDefinition RestorerRule => new()
    {
        Skill = "skill_restorer_hive",
        Repair = "repair",
        RepairEvery = 0.2,
        RepairLag = 1.55,
        RepairCount = "charge_capacity",
        Window = "duration_seconds",
        Cooldown = "cooldown_seconds",
        CooldownFromEnd = true,
        EndDelay = 2,
        UseArmorBelow = 1
    };

    private static SimSkillDefinition MenderRule => new()
    {
        Skill = "skill_mender_seeker_mine",
        Repair = "repair_rate",
        RepairEvery = 0.5,
        RepairBelow = 0.9,
        RepairCloud = "cloud_duration_seconds",
        Window = "duration_seconds",
        Cooldown = "cooldown_seconds",
        CooldownFromEnd = true,
        UseArmorBelow = 1
    };

    // Rounds held as charges, each back in its recovery time, the launcher's 1.5 s reload between rounds (R148), one cloud a round:
    // the window is the cloud's, so a use checks until it starts.
    private static SimSkillDefinition ReinforcerRule => new()
    {
        Skill = "skill_reinforcer_chem_launcher",
        Repair = "repair_over_time",
        RepairEvery = 0.5,
        RepairCloud = "cloud_duration_seconds",
        Window = "cloud_duration_seconds",
        Cooldown = "ammo_recovery_seconds",
        Stock = "charge_capacity",
        StockInterval = 1.5,
        UseArmorBelow = 1
    };

    // A player with armor (the repairs restore armor only) under enemy fire, the skill in slot 1; or no skill.
    private static DummyFightResult RepairFight(CompiledPackExpressions pack, string? skill, double duration = 30, bool incoming = true, bool hits = true, string? enemyHitRate = null)
    {
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads");
        if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
        var context = Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1");
        if (enemyHitRate is not null) { context = context.Add("enemy_hit_rate", enemyHitRate); }
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, hits ? context : context.Add("enemy_hit_rate", "0"), new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = incoming,
            IncludeSkills = true,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static DummyEvent[] SkillRepairs(DummyFightResult result) => [.. result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillRepair)];

    private static double FirstDamagingHit(DummyFightResult result) =>
        result.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.EnemyHit && entry.Damage > 0).Time;

    [Fact]
    public void TheFixerDroneRepairsEachSecondWhileArmorIsBelowFull()
    {
        var pack = RepairPack(FixerRule);
        var result = RepairFight(pack, "skill_fixer_drone");
        var repairs = SkillRepairs(result);
        Assert.NotEmpty(repairs);
        // Used at the first hit that takes armor, repairing at once, then only on its 1 s ticks from the use.
        var used = FirstDamagingHit(result);
        Assert.Equal(used, repairs[0].Time, 9);
        Assert.All(repairs, repair => Assert.Equal(Math.Round(repair.Time - used), repair.Time - used, 6));
        Assert.All(repairs, repair => Assert.Equal("skill_slot_1", repair.WeaponSlot));
        Assert.Contains("Repair skills restore the player's armor only, capped, with the player assumed in range throughout: Fixer Drone", result.Limitations);
        // It keeps the player's armor up: more repaired than without it.
        Assert.True(result.Incoming!.ArmorRepaired > RepairFight(pack, null).Incoming!.ArmorRepaired);
    }

    [Fact]
    public void ARepairTrapsMinesAreSpentOneAtATimeAndItsCooldownRunsFromTheLast()
    {
        var pack = RepairPack(TrapRule);
        var result = RepairFight(pack, "skill_repair_trap", duration: 120, enemyHitRate: "5");
        var repairs = SkillRepairs(result);
        Assert.True(repairs.Length >= 4, $"got {repairs.Length} repairs");
        // Two mines a use, each landing 0.3 s after it's stepped on, the second no sooner than the first has landed.
        var used = FirstDamagingHit(result);
        Assert.Equal(used + 0.3, repairs[0].Time, 9);
        Assert.True(repairs[1].Time - repairs[0].Time >= 0.3 - 1e-9);
        // With both spent, the 40 s cooldown runs from the last: the next use comes 40 s on, long before the 60 s window and 40 s
        // cooldown would have let it.
        Assert.True(repairs[2].Time - repairs[1].Time >= 40.3 - 1e-9);
        Assert.True(repairs[2].Time < used + 100);
        // Here the second use's second mine is stepped on at the check where the first's repair lands, exactly 0.3 s later: a repair
        // falling on a check is that check's moment, and lands before it.
        Assert.Equal(0.3, repairs[3].Time - repairs[2].Time, 9);
    }

    [Fact]
    public void ARestorerHivesChargesLandOneAtATimeUntilSpent()
    {
        // A 5 s cooldown here (the source's is 90 s), so the next use comes within the fight.
        var pack = RepairPack(RestorerRule, ("cooldown_seconds", "5"));
        var result = RepairFight(pack, "skill_restorer_hive", duration: 60);
        var repairs = SkillRepairs(result);
        // Eight charges at tier 0, all spent under this fire before the player goes down; each lands 1.55 s after its check, and the
        // next check waits for it, on the hive's 0.2 s ticks: 1.6 s apart here.
        Assert.True(repairs.Length > 8, $"got {repairs.Length} repairs");
        Assert.Equal(FirstDamagingHit(result) + 1.55, repairs[0].Time, 9);
        foreach (var (first, second) in repairs.Take(8).Zip(repairs.Skip(1).Take(7))) { Assert.InRange(second.Time - first.Time, 1.55 - 1e-9, 1.75 + 1e-9); }
        // Spent, the hive ends 2 s after its last; its cooldown runs from there, so the next use's first charge lands no sooner than
        // 2 + 5 + 1.55 s after the eighth.
        Assert.True(repairs[8].Time >= repairs[7].Time + 2 + 5 + 1.55 - 1e-9, $"{repairs[7].Time} then {repairs[8].Time}");
    }

    [Fact]
    public void AMenderCloudStartsBelowNinetyPercentAndRunsItsEightSeconds()
    {
        var pack = RepairPack(MenderRule);
        var result = RepairFight(pack, "skill_mender_seeker_mine", duration: 30);
        var repairs = SkillRepairs(result);
        Assert.NotEmpty(repairs);
        // Used at the first hit; a cloud starts at a 0.5 s check once armor is below 90%, its first tick 0.5 s later.
        var used = FirstDamagingHit(result);
        Assert.True(repairs[0].Time >= used + 1 - 1e-9);
        Assert.Equal(Math.Round((repairs[0].Time - used) * 2) / 2, repairs[0].Time - used, 6);
        // Half the rate each tick (17,918 a second at tier 0), every 0.5 s; under steady fire the clouds run back to back, one never
        // over another, so no two ticks come together.
        Assert.All(repairs, repair => Assert.Equal(17918 / 2d, repair.Damage, 0));
        Assert.All(repairs.Zip(repairs.Skip(1)), pair => Assert.Equal(0.5, pair.Second.Time - pair.First.Time, 6));
        Assert.True(repairs.Length > 16, "more than one cloud");
    }

    [Fact]
    public void AReinforcerRoundIsOneCloudOfItsRepairOverTime()
    {
        var pack = RepairPack(ReinforcerRule);
        var result = RepairFight(pack, "skill_reinforcer_chem_launcher", duration: 20);
        var repairs = SkillRepairs(result);
        // The first round is fired at the first hit, its cloud's first tick 0.5 s later, half of 39,817 a second each.
        Assert.Equal(FirstDamagingHit(result) + 0.5, repairs[0].Time, 9);
        Assert.All(repairs, repair => Assert.Equal(39817 / 2d, repair.Damage, 0));
        // Ten ticks a 5 s cloud, each whole within this fight. With two rounds held, the second is fired after the 1.5 s reload, armor
        // still below full, its cloud overlapping the first's; the next round comes back only after its recovery, past this fight.
        Assert.Equal(20, repairs.Length);
        Assert.All(repairs, repair => Assert.Equal(Math.Round((repair.Time - repairs[0].Time) * 2) / 2, repair.Time - repairs[0].Time, 6));
        // The first cloud's ten ticks are all there, and no moment has more than the two rounds' ticks.
        Assert.All(Enumerable.Range(0, 10), tick => Assert.Contains(repairs, repair => Math.Abs(repair.Time - (repairs[0].Time + tick * 0.5)) < 1e-6));
        Assert.All(repairs.GroupBy(repair => Math.Round(repair.Time * 2)), moment => Assert.InRange(moment.Count(), 1, 2));
    }

    [Fact]
    public void WithoutHitsARepairSkillIsNeverUsed()
    {
        var result = RepairFight(RepairPack(FixerRule), "skill_fixer_drone", hits: false);
        Assert.Empty(SkillRepairs(result));
        Assert.Contains("Never used, since the player's armor never fell below its share: Fixer Drone", result.Limitations);
    }

    [Fact]
    public void WithoutEnemyFireARepairSkillStepsAside()
    {
        var result = RepairFight(RepairPack(TrapRule), "skill_repair_trap", incoming: false);
        Assert.Empty(SkillRepairs(result));
        Assert.Contains("Run without skills used only once armor falls, since enemy fire is off: Repair Trap (used once armor is below full)", result.Limitations);
    }
}
