using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>ADR 0027: the Demolisher Firefly in fights, its gate and its several targets (R142; owner, 2026-11-20).</summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string Firefly = "skill_demolisher_firefly";
    private const string WeakpointGate = "target_weakpoint_or_mech";

    // From r143.candidate.1 (R143) the pack carries the Firefly's setting, its 50 s cooldown and its rule with the gate and targets.
    private static CompiledPackExpressions FireflyPack()
    {
        var pack = CommittedPack();
        var rule = pack.Pack.Simulation.Skills.Single(rule => rule.Skill == Firefly);
        Assert.Equal(("damage", "cooldown_seconds", true, "target_capacity"), (rule.Impact, rule.Cooldown, rule.CooldownFromEnd, rule.Targets));
        Assert.Equal("true", rule.RequiresContext![WeakpointGate]);
        Assert.Equal("Target has a weakpoint or is a mech", pack.Pack.Selectors.Single(selector => selector.ID == WeakpointGate).Name);
        // 50 s on a build with no haste or mods, as the source's base.
        var preview = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", Firefly), Context).ActiveSkillPreviews["skill_slot_1"];
        Assert.Equal(50, preview.Outputs.Single(output => output.Id == "cooldown_seconds").Value, 6);
        return pack;
    }

    private static DummyFightResult FireflyFight(CompiledPackExpressions pack, bool on, int? atOnce, double? spacing)
    {
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", Firefly);
        var context = Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1");
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, on ? context.Add(WeakpointGate, "true") : context, new()
        {
            Duration = 10,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            DriveSimpleBuffs = true,
            IncludeIncoming = true,
            IncludeSkills = true,
            CaptureFirstFightEvents = true,
            EnemiesAtOnce = atOnce,
            EnemySpacing = spacing
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheFireflyStrikesItsCapacityOfDistinctEnemiesAtLaunch()
    {
        var pack = FireflyPack();
        var result = FireflyFight(pack, on: true, atOnce: 4, spacing: 3);
        var events = result.FirstFightEvents;
        var launch = events.First(entry => entry.Kind == DummyEventKind.SkillImpact);
        // Capacity 3 at tier 0: the enemy being shot and the two nearest others, all as it's used, each its full payload or what kills.
        var others = events.Where(entry => entry.Kind == DummyEventKind.SkillAreaHit && entry.Time == launch.Time).ToArray();
        Assert.Equal(2, others.Length);
        // Two fresh enemies of one profile: each takes the same full payload (or all its pools), never a share.
        Assert.All(others, entry => Assert.True(entry.Damage > 0));
        Assert.Equal(others[0].Damage, others[1].Damage, 6);
        Assert.Contains(result.Limitations, note => note.StartsWith("A skill with several targets (Demolisher Firefly)", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Limitations, note => note.StartsWith("These effects on other enemies find nobody", StringComparison.Ordinal));
        // The cooldown runs 50 s, so a 10 s fight has the one use.
        Assert.Single(events, entry => entry.Kind == DummyEventKind.SkillImpact);
    }

    [Fact]
    public void WithoutASpacingTheFireflyStrikesTheEnemyBeingShotAlone()
    {
        var pack = FireflyPack();
        foreach (var (atOnce, spacing) in new (int?, double?)[] { (4, null), (null, null) })
        {
            var result = FireflyFight(pack, on: true, atOnce, spacing);
            Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.SkillImpact);
            Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.SkillAreaHit);
            // The note on effects that find nobody names it, with the fight's reason.
            Assert.Contains(result.Limitations, note => note.StartsWith("These effects on other enemies find nobody", StringComparison.Ordinal) &&
                note.Contains("Demolisher Firefly", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheFireflyStepsAsideWithItsSettingOff()
    {
        var pack = FireflyPack();
        var off = FireflyFight(pack, on: false, atOnce: 4, spacing: 3);
        Assert.DoesNotContain(off.FirstFightEvents, entry => entry.Kind is DummyEventKind.SkillImpact or DummyEventKind.SkillAreaHit);
        Assert.Contains("Run without skills whose setting is off: Demolisher Firefly (needs Target has a weakpoint or is a mech set)", off.Limitations);
        Assert.DoesNotContain(off.Limitations, note => note.StartsWith("A skill with several targets", StringComparison.Ordinal));
    }
}
