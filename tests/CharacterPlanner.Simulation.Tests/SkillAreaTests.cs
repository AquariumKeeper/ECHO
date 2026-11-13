using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class IncomingFightTests
{
    // ADR 0016: a skill whose impact of 5 reaches a 5 m area, among enemies 4 m apart. With `burn`, the impact also puts on a burn of 4 a
    // second for 3 s (a seeker mine's shape); with `spread`, the gun's talent spreads skill statuses within 5 m.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) AreaFixture(bool area = true, bool burn = false, bool spread = false, string health = "1000000",
        double edge = 0)
    {
        var (original, build) = IncomingSkillFixture(impact: "5", cooldown: "20", changes: [("character_health", "100000"), ("enemy_target_health", health)]);
        var withStatus = burn
            ? WithSkillStatus(original, rule => rule with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" })
            : original;
        var skill = withStatus.Pack.ActiveSkills[0];
        var simulation = withStatus.Pack.Simulation;
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(withStatus.Pack with
        {
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs, new() { ID = "radius", Name = "Radius", Formula = "5" }, new() { ID = "full_radius", Name = "Full", Formula = "1" }] }],
            Simulation = simulation with
            {
                Skills = [simulation.Skills[0] with { Area = area ? "radius" : "", AreaFull = edge > 0 ? "full_radius" : "", AreaEdge = edge }],
                Spreads = spread ? [new() { Talent = "test", Within = 5, SkillStatuses = true }] : []
            }
        }).Compiled);
        return (pack, build.WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static DummyFightResult AreaFight((CompiledPackExpressions Pack, BuildSnapshot Build) fixture, double? spacing = 4) =>
        Run(fixture, Group(10, 3) with { EnemySpacing = spacing, IncludeSkills = true }, delay: 5);

    private static double[] HitsAt(DummyFightResult result, DummyEventKind kind, double at) =>
        [.. result.FirstFightEvents.Where(e => e.Kind == kind && Math.Abs(e.Time - at) < 1e-9).Select(e => e.Damage)];

    [Fact]
    public void AnImpactWithAnAreaLandsOnEveryEnemyInItsRadius()
    {
        // The impact at 0 s lands on the enemy being shot and, as an area hit, on its neighbour 4 m away; the one 8 m away is out of reach.
        var reached = AreaFight(AreaFixture());
        Assert.Equal([5d], HitsAt(reached, DummyEventKind.SkillImpact, 0));
        Assert.Equal([5d], HitsAt(reached, DummyEventKind.SkillAreaHit, 0));
        Assert.Contains(reached.Limitations, note => note.StartsWith("Skills with an area land each impact", StringComparison.Ordinal));
        // Without the area, or without a spacing, only the enemy being shot takes it.
        Assert.Empty(HitsAt(AreaFight(AreaFixture(area: false)), DummyEventKind.SkillAreaHit, 0));
        var unspaced = AreaFight(AreaFixture(), spacing: null);
        Assert.Empty(HitsAt(unspaced, DummyEventKind.SkillAreaHit, 0));
        Assert.Contains(unspaced.Limitations, note => note.StartsWith("These effects on other enemies find nobody", StringComparison.Ordinal));
        // The neighbour's 5 counts in skill DPS.
        Assert.True(reached.SkillDps!.Total.Mean > AreaFight(AreaFixture(area: false)).SkillDps!.Total.Mean);
    }

    [Fact]
    public void AnAreaKillIsAKillElsewhere()
    {
        // Enemies of 4 health: the impact kills the enemy being shot and its neighbour at once, two kills at 0 s.
        var result = AreaFight(AreaFixture(health: "4"));
        Assert.Equal(2, Times(result, DummyEventKind.Kill).Count(at => at == 0));
        Assert.Equal(1, Times(AreaFight(AreaFixture(area: false, health: "4")), DummyEventKind.Kill).Count(at => at == 0));
    }

    [Fact]
    public void AnAreasBurnCarriesTheSkillsRateAndCanSpreadOn()
    {
        // The impact puts the 4-a-second burn on the neighbour for one application, 3 s: ticks of 4 at 1 and 2 s (the enemy being shot
        // takes its own burn as the skill's damage, not as a status tick).
        var burned = AreaFight(AreaFixture(burn: true));
        var ticks = burned.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).ToArray();
        Assert.Equal([1d, 2d], ticks.Select(e => Math.Round(e.Time, 6)));
        Assert.All(ticks, e => Assert.Equal(4, e.Damage, 6));
        // With a spread that takes skill statuses, the neighbour's new burn spreads on to the enemy 4 m beyond it, which the area itself
        // doesn't reach: it burns too, at 1 and 2 s. The spread leaves the enemy being shot alone, so its own burn stays the skill's:
        // the same skill ticks there with or without the spread.
        var spread = AreaFight(AreaFixture(burn: true, spread: true));
        Assert.Equal([1d, 1d, 2d, 2d], spread.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).Select(e => Math.Round(e.Time, 6)));
        Assert.Equal(burned.SkillDps!.Total.Mean, spread.SkillDps!.Total.Mean, 6);
    }

    [Fact]
    public void AnAreasImpactFallsOffInAStraightLine()
    {
        // Full damage within 1 m, half at the 5 m edge: the neighbour 4 m away takes 1 - 0.5 x (4 - 1) / (5 - 1) = 0.625 of 5, and its burn
        // goes on whole.
        Assert.Equal([3.125], HitsAt(AreaFight(AreaFixture(edge: 0.5)), DummyEventKind.SkillAreaHit, 0));
        // The neighbour's burn ticks as without the falloff: 4 at 1 and 2 s (the enemy being shot's own burn is the skill's, no tick).
        var burned = AreaFight(AreaFixture(burn: true, edge: 0.5));
        var ticks = burned.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).ToArray();
        Assert.Equal([1d, 2d], ticks.Select(e => Math.Round(e.Time, 6)));
        Assert.All(ticks, e => Assert.Equal(4, e.Damage, 6));
        // A full-damage radius at or past the area's own leaves the whole area at full damage, as the source's would be (a Seeker's
        // fixed 1 m against a radius mods shrink).
        var wide = AreaFixture(edge: 0.5);
        var pack = wide.Pack.Pack;
        var fullEverywhere = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack with
        {
            ActiveSkills = [pack.ActiveSkills[0] with { PreviewOutputs = [.. pack.ActiveSkills[0].PreviewOutputs.Select(o => o.ID == "full_radius" ? o with { Formula = "6" } : o)] }]
        }).Compiled);
        Assert.Equal([5d], HitsAt(AreaFight((fullEverywhere, wide.Build)), DummyEventKind.SkillAreaHit, 0));
    }
}
