using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>ADR 0029: The Howl in fights, its armor-only hit and its delay by distance (R146; owner, 2026-11-21).</summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string Howl = "skill_the_howl";

    // From r147.candidate.1 (R147) the pack carries The Howl's rule: an armor-only hit over its radius with the shock, 0.7 s after the
    // use (1.2 s beyond 12.5 m), a 35 s cooldown from 1.2 s after the use, and an outright kill on profiles 1 to 4, 1.5 s later.
    // For a test, a hit of `armorDamage` overrides the pack's (242,886, below every profile's armor at difficulty 1), `area` false
    // takes the radius off, and the kill's chance and profiles can be set.
    private static CompiledPackExpressions HowlPack(string? armorDamage = null, bool area = false, string? killChance = null, int[]? killProfiles = null)
    {
        var committed = CommittedPack().Pack;
        var rule = committed.Simulation.Skills.Single(rule => rule.Skill == Howl);
        Assert.Equal(("armor_damage", true, "radius_meters", "shocked", true, "shock_duration_seconds", "cooldown_seconds", 1.2, 0.7, 1.2, 12.5, "instakill_chance", 1.5),
            (rule.Impact, rule.ArmorOnly, rule.Area, rule.Status, rule.ControlStatus, rule.Window, rule.Cooldown, rule.CooldownAfter, rule.ImpactDelay,
             rule.FarImpactDelay, rule.FarFrom, rule.KillChance, rule.KillDelay));
        Assert.Equal([1, 2, 3, 4], rule.KillProfiles);
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed with
        {
            ActiveSkills = [.. committed.ActiveSkills.Select(skill => skill.ID != Howl ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID == "armor_damage" && armorDamage is not null ? output with { Formula = armorDamage }
                    : output.ID == "instakill_chance" && killChance is not null ? output with { Formula = killChance } : output)]
            })],
            Simulation = committed.Simulation with
            {
                Skills = [.. committed.Simulation.Skills.Select(other => other != rule ? other : rule with
                {
                    Area = area ? rule.Area : "",
                    KillProfiles = killProfiles is null ? rule.KillProfiles : [.. killProfiles]
                })]
            }
        }).Compiled);
    }

    private static DummyFightResult HowlFight(string target, double? distance = null, double duration = 10, string hitRate = "90",
        CompiledPackExpressions? pack = null, int? atOnce = null, double? after = null)
    {
        pack ??= HowlPack();
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", Howl);
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", hitRate).Add("enemy_target", target).Add("enemy_difficulty", "1")
            .Add("enemy_hit_rate", "0"), new()
            {
                Duration = duration,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                DriveSimpleBuffs = true,
                IncludeIncoming = true,
                IncludeSkills = true,
                CaptureFirstFightEvents = true,
                Distance = distance,
                EnemiesAtOnce = atOnce,
                EnemySpacing = atOnce is null ? null : 3,
                SkillUse = after is { } seconds ? System.Collections.Immutable.ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1",
                    new() { When = SkillUseCondition.AfterSeconds, Seconds = seconds }) : System.Collections.Immutable.ImmutableDictionary<string, SkillUseCondition>.Empty
            }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheHowlsHitStopsAtArmor()
    {
        // Profile 1 has no armor, so the hit deals nothing (the player misses, so the enemy is there when it lands); profile 6 has armor,
        // which it takes, never reaching health.
        var bare = HowlFight("1", hitRate: "0").FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillImpact);
        Assert.Equal(0, bare.Damage);
        // A hit of 10,000,000 against profile 2 (663,623 armor and as much health at difficulty 1) would kill; armor-only, it takes
        // exactly the armor, on the enemy being shot and, with an area, on each other enemy in reach.
        var pack = HowlPack("10000000", area: true);
        var armor = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acr"), Context.Add("enemy_target", "2").Add("enemy_difficulty", "1"))
            .Calculation.Stats["enemy_target_armor"];
        var armored = HowlFight("2", hitRate: "0", pack: pack, atOnce: 3);
        var hit = armored.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillImpact);
        Assert.Equal(armor, hit.Damage, 3);
        var others = armored.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillAreaHit && entry.Time == hit.Time).ToArray();
        Assert.Equal(2, others.Length);
        Assert.All(others, entry => Assert.Equal(armor, entry.Damage, 3));
        Assert.DoesNotContain(armored.FirstFightEvents, entry => entry.Kind == DummyEventKind.Kill);
        Assert.Contains(armored.Limitations, note => note.StartsWith("A skill's armor-only hit (The Howl)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHowlLandsLaterBeyondItsNearRadiusAndCoolsFromAfterItsUse()
    {
        double[] Hits(double? distance) => [.. HowlFight("6", distance, 40).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).Select(entry => entry.Time)];
        var near = Hits(10);
        var far = Hits(20);
        // 0.7 s after the use at the fight's start, 1.2 s beyond 12.5 m; uses 35 + 1.2 s apart either way.
        Assert.Equal(0.7, near[0], 9);
        Assert.Equal(1.2, far[0], 9);
        Assert.Equal(near[0] + 36.2, near[1], 9);
        Assert.Equal(far[0] + 36.2, far[1], 9);
        // Without a distance it's the near delay.
        Assert.Equal(near[0], Hits(null)[0], 9);
        Assert.Contains(HowlFight("6", 20).Limitations, note => note == "The Howl's effects land 0.7 s after its use (1.2 s beyond 12.5 m), by the fight's enemy distance");
    }

    [Fact]
    public void TheHowlKillsOutrightOnlyAnEligibleEnemyBelowFullHealth()
    {
        // A sure kill (chance 1). Untouched (the player misses), a Normal enemy never dies outright.
        var sure = HowlPack(killChance: "1");
        var untouched = HowlFight("3", hitRate: "0", pack: sure, duration: 40);
        Assert.DoesNotContain(untouched.FirstFightEvents, entry => entry.Kind == DummyEventKind.OutrightKill);
        // Shot a few times first (a 10% hit rate, the Howl used at 3 s, so its hit lands at 3.7 s), it dies 1.5 s after the hit.
        var shot = HowlFight("3", hitRate: "10", pack: sure, after: 3);
        var hit = shot.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillImpact);
        var kill = Assert.Single(shot.FirstFightEvents, entry => entry.Kind == DummyEventKind.OutrightKill);
        Assert.Equal(hit.Time + 1.5, kill.Time, 9);
        Assert.True(kill.Damage > 0);
        Assert.Contains(shot.FirstFightEvents, entry => entry.Kind == DummyEventKind.Kill && entry.Time == kill.Time);
        Assert.Contains(shot.Limitations, note => note.StartsWith("The Howl can kill outright", StringComparison.Ordinal));
        // The same enemy, shot the same way, with its profile left out of those the kill reaches (as Elite and Named are), never does.
        var ineligible = HowlFight("3", hitRate: "10", pack: HowlPack(killChance: "1", killProfiles: [1, 2, 4]), after: 3);
        Assert.Contains(ineligible.FirstFightEvents, entry => entry.Kind == DummyEventKind.SkillImpact);
        Assert.DoesNotContain(ineligible.FirstFightEvents, entry => entry.Kind == DummyEventKind.OutrightKill);
    }

    [Fact]
    public void AnOutrightKillGivesNoTalentCredit()
    {
        // Doctor Home marks an enemy on hit and repairs half the missing armor at its death. A bullet kill on a marked enemy repairs
        // at the kill; the Howl's outright kill, by the owner's call, doesn't.
        var pack = HowlPack(killChance: "1");
        var build = Equip(pack.Pack, "primary_weapon_doctor_home", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithActiveSkillSelection("skill_slot_1", Howl);
        DummyFightResult Fight(string hitRate, bool skills) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.Add("practical_hit_rate", hitRate).Add("enemy_target", "3").Add("enemy_difficulty", "1"), new()
            {
                Duration = 10,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                DriveSimpleBuffs = true,
                IncludeIncoming = true,
                IncludeSkills = skills,
                CaptureFirstFightEvents = true,
                SkillUse = !skills ? System.Collections.Immutable.ImmutableDictionary<string, SkillUseCondition>.Empty
                    : System.Collections.Immutable.ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = 3 })
            }, cancellationToken: TestContext.Current.CancellationToken);
        bool RepairedAt(DummyFightResult fight, double at) => fight.FirstFightEvents.Any(entry => entry.Kind == DummyEventKind.ArmorRepaired && entry.Time == at);
        var bullets = Fight("90", skills: false);
        Assert.Contains(bullets.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Kill), kill => RepairedAt(bullets, kill.Time));
        var howled = Fight("10", skills: true);
        var outright = Assert.Single(howled.FirstFightEvents, entry => entry.Kind == DummyEventKind.OutrightKill);
        Assert.False(RepairedAt(howled, outright.Time));
    }
}
