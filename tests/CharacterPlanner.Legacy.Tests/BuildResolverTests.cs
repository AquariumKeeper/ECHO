using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class BuildResolverTests
{
    private static GamePack Pack => new()
    {
        Level = 1,
        Stats = [new() { ID = "damage", Formula = "100" }],
        Slots = [new() { ID = "main", Name = "Main", Accepts = ["weapon"], Weapon = true }, new() { ID = "off", Name = "Off", Accepts = ["weapon"], Weapon = true }]
    };

    [Fact]
    public void HolsteredWeaponSuppressesEffectsButPreservesTagsAndSetMembership()
    {
        var pack = Pack with
        {
            Items = [new() { ID = "sword", Name = "Sword", Slot = "main", Tags = ["blade"], Modifiers = [new() { Stat = "damage", Op = "flat", Value = new(10) }] }],
            Sets = [new() { ID = "set", Name = "Set", Items = ["sword"], Bonuses = [new() { Pieces = 1, Modifiers = [new() { Stat = "damage", Op = "flat", Value = new(20), When = "has_tag('blade')" }] }] }]
        };
        var compiled = Compile(pack);
        var build = BuildSnapshot.Create(pack).WithEquipment("main", "sword");
        var holstered = BuildResolver.Resolve(compiled, build, ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "off"));
        Assert.Equal(120, holstered.Calculation.Stats["damage"]);
        Assert.Contains("blade", holstered.Tags);
        Assert.Empty(holstered.SelectedTags);
        Assert.Equal("set", Assert.Single(holstered.ActiveSets).SetId);
        Assert.Equal("set", Assert.Single(holstered.Calculation.Breakdowns["damage"].Contributions).Source.Kind);
        var active = BuildResolver.Resolve(compiled, build, ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main"));
        Assert.Equal(130, active.Calculation.Stats["damage"]);
        Assert.Contains("blade", active.SelectedTags);
        Assert.Equal(120, BuildResolver.Resolve(compiled, build).Calculation.Stats["damage"]);
    }

    [Fact]
    public void ActiveWeaponBaseStatsOverrideBeforeItsOwnModifiers()
    {
        var pack = Pack with
        {
            Stats = [.. Pack.Stats, new() { ID = "rpm", Formula = "0" }],
            Items = [new()
            {
                ID = "rifle", Name = "Rifle", Slot = "main", Weapon = [new("damage", 50), new("rpm", 600)],
                Modifiers = [new() { Stat = "damage", Op = "mult", Value = new(0.1) }]
            }]
        };
        var compiled = Compile(pack);
        var build = BuildSnapshot.Create(pack).WithEquipment("main", "rifle");
        var active = BuildResolver.Resolve(compiled, build, ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main"));
        Assert.Equal(50, active.Calculation.Stats["damage"]);
        Assert.Equal(600, active.Calculation.Stats["rpm"]);
        Assert.Equal(new[] { ("override", 50d), ("mult", 0.1) }, active.Calculation.Breakdowns["damage"].Contributions.Select(value => (value.Op, value.Value)));
        Assert.All(active.Calculation.Breakdowns["damage"].Contributions, value => Assert.Equal(new ModifierSource("item", "rifle", "Rifle"), value.Source));
        var holstered = BuildResolver.Resolve(compiled, build, ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "off"));
        Assert.Equal(100, holstered.Calculation.Stats["damage"]);
        Assert.Equal(0, holstered.Calculation.Stats["rpm"]);
    }

    [Fact]
    public void PartyMembersWithNoKnownEffectsDoNotChangeStats()
    {
        var pack = Pack;
        var compiled = Compile(pack);
        var build = BuildSnapshot.Create(pack).WithParty([new("Ally", "", 0, [])]);
        Assert.Equal(BuildResolver.Resolve(compiled, BuildSnapshot.Create(pack)).Calculation.Stats["damage"], BuildResolver.Resolve(compiled, build).Calculation.Stats["damage"]);
    }

    private static CompiledPackExpressions Compile(GamePack pack) => LegacyPacks.Compile(pack);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-0", 0)]
    [InlineData("2.5", 2.5)]
    [InlineData("1e1", 10)]
    [InlineData("1E+1", 10)]
    public void NumericAssumptionsAcceptReferenceSyntaxAndKeepExplicitStrings(string raw, double expected)
    {
        var context = ImmutableDictionary<string, string>.Empty.Add("stacks", raw).Add("unknown", "retained");
        var result = ResolveAssumptions.Resolve(NumericPack, context);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, result.Numeric["stacks"]);
        Assert.Equal(raw, result.Explicit["stacks"]);
        Assert.Equal("retained", result.Explicit["unknown"]);
        Assert.False(result.Numeric.ContainsKey("unknown"));
    }

    private static GamePack NumericPack => Pack with
    { NumericContextInputs = [new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 10, Step = .5, Default = 3 }] };

    [Theory]
    [InlineData("01")]
    [InlineData("+2")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData(" 2")]
    [InlineData("2\n")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("2.25")]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("")]
    public void InvalidNumericAssumptionsUseDefaultAndReportDiagnostic(string raw)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = ResolveAssumptions.Resolve(NumericPack, ImmutableDictionary<string, string>.Empty.Add("stacks", raw));
            Assert.Equal(3, result.Numeric["stacks"]);
            Assert.Equal(raw, result.Explicit["stacks"]);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(("error", "context-value-invalid", "stacks", ""), diagnostic.Key());
            // Not a wording check: the step must be formatted invariantly under any host culture.
            Assert.Contains("0.5", diagnostic.Message, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void NumericAssumptionsPrecedeLoadoutDiagnosticsAndFeedDerivedFormulas()
    {
        var pack = NumericPack with { Stats = [new() { ID = "damage", Formula = "context_value('stacks') * 2" }] };
        var build = BuildSnapshot.Create(pack).WithEquipment("main", "missing");
        var result = BuildResolver.Resolve(Compile(pack), build, ImmutableDictionary<string, string>.Empty.Add("stacks", "invalid"));
        Assert.Equal(new[] { "context-value-invalid", "unknown-item" }, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(6, result.Calculation.Stats["damage"]);
        Assert.Empty(result.Calculation.RejectedContextInputs);
        Assert.Empty(ResolveAssumptions.Resolve(pack).Diagnostics);
        Assert.Equal(3, ResolveAssumptions.Resolve(pack).Numeric["stacks"]);
    }
}
