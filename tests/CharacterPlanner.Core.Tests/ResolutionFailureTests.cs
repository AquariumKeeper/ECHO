using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

/// <summary>
/// The resolver's failure contract (M1): an expression that can't be evaluated, or gives a non-finite value,
/// becomes an error diagnostic and resolution goes on, so every stat stays finite and nothing throws.
/// </summary>
public sealed class ResolutionFailureTests
{
    private static readonly ImmutableDictionary<string, string> Primary =
        ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon").Add("overwatch_active", "true");

    /// <summary>
    /// Tiny with a NaN derived formula, a NaN modifier value on the mask, and on the rifle a NaN condition and a NaN
    /// value that reads a stat (a deferred read, evaluated after the stats it reads).
    /// </summary>
    private static CompiledPackExpressions Failing()
    {
        var pack = TestPacks.Tiny;
        pack = pack with
        {
            Stats = [.. pack.Stats.Select(stat => stat.ID == "total_damage" ? stat with { Formula = "sqrt(0 - weapon_base_damage)" } : stat)],
            Items = [.. pack.Items.Select(item => item.ID switch
            {
                "mask_a" => item with { Modifiers = [item.Modifiers[0] with { Value = new("sqrt(0 - 1)") }] },
                "rifle_a" => item with
                {
                    Modifiers = [item.Modifiers[0] with { When = "sqrt(0 - 1)" }, new() { Stat = "armor", Op = ModifierOps.Flat, Value = new("sqrt(0 - weapon_rpm)") }]
                },
                _ => item
            })]
        };
        var result = PackExpressionCompiler.Compile(pack);
        Assert.Empty(result.Diagnostics);
        return result.Compiled!;
    }

    [Fact]
    public void FailedExpressionsBecomeDiagnosticsAndEveryStatStaysFinite()
    {
        var compiled = Failing();
        var build = BuildSnapshot.Create(compiled.Pack).WithEquipment("mask", "mask_a").WithEquipment("primary_weapon", "rifle_a");
        var resolved = BuildResolver.Resolve(compiled, build, Primary);

        Assert.All(resolved.Calculation.Stats.Values, value => Assert.True(double.IsFinite(value)));
        // The formula counts as 0; the failed modifiers are left out: the mask's +1,000 armor (500 without it, as
        // the unchanged pack's 1,500 with Overwatch shows) and the rifle's +10% weapon damage.
        Assert.Equal(0, resolved.Calculation.Stats["total_damage"]);
        Assert.Equal(500, resolved.Calculation.Stats["armor"]);
        Assert.Equal(0, resolved.Calculation.Stats["weapon_damage"]);
        Assert.Equal(100, resolved.Calculation.Stats["weapon_base_damage"]);

        var failures = resolved.Diagnostics.Where(diagnostic => diagnostic.Code is "modifier-eval" or "stat-formula-eval").ToList();
        Assert.Equal(["modifier-eval", "modifier-eval", "modifier-eval", "stat-formula-eval"], failures.Select(diagnostic => diagnostic.Code).Order(StringComparer.Ordinal));
        Assert.All(failures, diagnostic => Assert.Equal("error", diagnostic.Severity));
        Assert.Contains(failures, diagnostic => diagnostic.Subject == "mask_a" && diagnostic.Target.EndsWith(".value", StringComparison.Ordinal));
        Assert.Contains(failures, diagnostic => diagnostic.Subject == "rifle_a" && diagnostic.Target.EndsWith(".when", StringComparison.Ordinal));
        Assert.Contains(failures, diagnostic => diagnostic.Subject == "rifle_a" && diagnostic.Target.EndsWith("modifiers[1].value", StringComparison.Ordinal));
        // NaN is an evaluation error to the evaluator itself, which made each of these throw out of Resolve before.
        Assert.Contains(failures, diagnostic => diagnostic.Subject == "total_damage" && diagnostic.Code == "stat-formula-eval");
        // The DPS output reads the failed stat, which is now a finite 0, so it still resolves.
        Assert.Equal(0, Assert.Single(resolved.BuildOutputs).Value);
    }

    /// <summary>A giver's stat failures come with its offered effects, so a party import can refuse values read from them.</summary>
    [Fact]
    public void GiverFailuresAreReportedWithTheirEffects()
    {
        var compiled = Failing();
        var giver = BuildSnapshot.Create(compiled.Pack).WithEquipment("mask", "mask_a").WithEquipment("primary_weapon", "rifle_a");
        var offered = PartyEffectSelection.Offered(compiled, giver, Primary);
        Assert.NotEmpty(offered.Failures);
        Assert.Equal(offered.Effects.Select(effect => effect.Id), PartyEffectSelection.EffectsOf(compiled, giver, Primary).Select(effect => effect.Id));
    }

    /// <summary>Finite contributions can overflow; the stat then takes the value in its bounds nearest 0, and the breakdown says it was moved.</summary>
    [Theory]
    [InlineData(null, 0d)]
    [InlineData(5d, 5d)]
    public void AnOverflowingStatFallsBackWithinItsBounds(double? min, double expected)
    {
        var pack = TestPacks.Tiny;
        StatModifier Huge() => new() { Stat = "armor", Op = ModifierOps.Flat, Value = new(double.MaxValue) };
        pack = pack with
        {
            Stats = [.. pack.Stats.Select(stat => stat.ID == "armor" ? stat with { Min = min } : stat)],
            Items = [.. pack.Items.Select(item => item.ID is "mask_a" or "rifle_a" ? item with { Modifiers = [Huge()] } : item)]
        };
        var compiled = PackExpressionCompiler.Compile(pack).Compiled!;
        var build = BuildSnapshot.Create(compiled.Pack).WithEquipment("mask", "mask_a").WithEquipment("primary_weapon", "rifle_a");
        var resolved = BuildResolver.Resolve(compiled, build, Primary);
        Assert.Equal(expected, resolved.Calculation.Stats["armor"]);
        var breakdown = resolved.Calculation.Breakdowns["armor"];
        Assert.Equal((expected, expected != 0), (breakdown.Final, breakdown.Clamped));
        // The breakdown keeps the contributions that overflowed, beside armor's own.
        Assert.Equal(2, breakdown.Contributions.Count(contribution => contribution.Value == double.MaxValue));
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == "stat-non-finite" && diagnostic.Subject == "armor");
    }

    /// <summary>
    /// The resolved build names the assumptions its selected effects read (F46): a gate counts whether or not it's
    /// open, so a view can offer to open it; an item that isn't equipped contributes nothing.
    /// </summary>
    [Fact]
    public void ResolvedBuildsNameTheAssumptionsTheyRead()
    {
        var compiled = PackExpressionCompiler.Compile(TestPacks.Tiny).Compiled!;
        var empty = BuildSnapshot.Create(compiled.Pack);
        Assert.DoesNotContain("overwatch_active", BuildResolver.Resolve(compiled, empty, Primary).ReadAssumptions);

        // mask_a's armor is gated on overwatch_active; the gate is read whether or not it's open.
        var masked = empty.WithEquipment("mask", "mask_a");
        Assert.Contains("overwatch_active", BuildResolver.Resolve(compiled, masked, Primary).ReadAssumptions);
        Assert.Contains("overwatch_active", BuildResolver.Resolve(compiled, masked, ImmutableDictionary<string, string>.Empty).ReadAssumptions);
        // The active weapon has its own control and isn't reported.
        Assert.DoesNotContain(GamePack.WeaponSlotSelector, BuildResolver.Resolve(compiled, masked, Primary).ReadAssumptions);
    }

    [Fact]
    public void AnUnaffectedPackReportsNoEvaluationFailures()
    {
        var compiled = PackExpressionCompiler.Compile(TestPacks.Tiny).Compiled!;
        var build = BuildSnapshot.Create(compiled.Pack).WithEquipment("mask", "mask_a").WithEquipment("primary_weapon", "rifle_a");
        Assert.DoesNotContain(BuildResolver.Resolve(compiled, build, Primary).Diagnostics,
            diagnostic => diagnostic.Code is "modifier-eval" or "stat-formula-eval" or "stat-non-finite");
    }
}
