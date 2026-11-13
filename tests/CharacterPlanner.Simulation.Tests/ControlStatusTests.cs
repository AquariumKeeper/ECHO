using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class IncomingFightTests
{
    // ADR 0017: a Shock Trap's shape, a control skill that shocks for 3 s with a 20 s cooldown and no damage, under incoming fire.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ControlFixture(bool control = true, string status = "shocked", bool area = false)
    {
        var (original, build) = IncomingSkillFixture(impact: "0", window: "3", cooldown: "20", changes: [("character_health", "100000"), ("enemy_target_health", "1000000")]);
        var simulation = original.Pack.Simulation;
        var skill = original.Pack.ActiveSkills[0];
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(original.Pack with
        {
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs, new() { ID = "radius", Name = "Radius", Formula = "5" }] }],
            Simulation = simulation with { Skills = [simulation.Skills[0] with { Impact = "", Rate = "", Status = status, ControlStatus = control, Area = area ? "radius" : "" }] }
        }).Compiled);
        return (pack, build);
    }

    private static DummyFightResult ControlFight((CompiledPackExpressions Pack, BuildSnapshot Build) fixture) =>
        Run(fixture, Options with { Duration = 10, DriveSimpleBuffs = true, IncludeSkills = true });

    [Fact]
    public void AControlSkillsShockStopsTheEnemyFiring()
    {
        // The shock goes on at 0 s for 3 s, so the enemy's attempts then are blocked; later ones land.
        var shocked = ControlFight(ControlFixture());
        Assert.NotEmpty(Times(shocked, DummyEventKind.EnemyShotBlocked));
        Assert.All(Times(shocked, DummyEventKind.EnemyShotBlocked), at => Assert.True(at < 3, $"{at}"));
        Assert.Contains(shocked.Limitations, note => note.StartsWith("Control skills put their status on", StringComparison.Ordinal));
        // Without the row's field the skill is a cooldown only, as before.
        var plain = ControlFight(ControlFixture(control: false));
        Assert.Empty(Times(plain, DummyEventKind.EnemyShotBlocked));
        Assert.True(shocked.Incoming!.DamageTaken < plain.Incoming!.DamageTaken);
    }

    [Fact]
    public void AControlStatusCountsAsOnAndItsAreaReachesTheOthers()
    {
        // The ensnare reads as on the enemy being shot: 3 s of uptime from the use at 0 s.
        var ensnared = ControlFight(ControlFixture(status: "ensnared"));
        Assert.True(ensnared.TargetStatusUptime["ensnared"].Seconds > 0);
        // Three enemies 4 m apart firing at once: with a 5 m area the shock also stops the neighbour's fire, so more attempts are blocked.
        DummyFightResult Group3(bool area) => Run(ControlFixture(area: area), Group(10, 3) with { EnemySpacing = 4, IncludeSkills = true });
        Assert.True(Times(Group3(area: true), DummyEventKind.EnemyShotBlocked).Length > Times(Group3(area: false), DummyEventKind.EnemyShotBlocked).Length);
    }

    [Fact]
    public void AControlSkillChangesNothingWhereStatusesArentHeld()
    {
        // Without talent triggers a fight holds no statuses (several enemies at once need them anyway), so the field changes nothing
        // and the result names no control status.
        DummyFightResult Untriggered(bool control) => Run(ControlFixture(control: control, area: true), Options with { Duration = 10, IncludeSkills = true });
        var withField = Untriggered(control: true);
        Assert.Equal(Untriggered(control: false).Incoming!.DamageTaken, withField.Incoming!.DamageTaken);
        Assert.DoesNotContain(withField.Limitations, note => note.StartsWith("Control skills", StringComparison.Ordinal));
    }
}
