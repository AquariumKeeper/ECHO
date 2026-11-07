using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CharacterPlanner.Core.Packs;
using V1 = CharacterPlanner.Legacy.PackV1;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>The frozen packs and the v1 reader's defaults, and expression compilation of converted packs.</summary>
public sealed class PackTests
{
    private const string MinimalJson = """
        {"id":"test_pack","schemaVersion":1,"stats":[
          {"id":"strength","kind":"derived","formula":"10","min":0,"max":99},
          {"id":"hp","kind":"derived","formula":"strength * 10"},
          {"id":"ehp","kind":"derived","formula":"hp * 2"}
        ]}
        """;
    private static readonly GamePack Minimal = LegacyPacks.Convert(LegacyPacks.Read(Encoding.UTF8.GetBytes(MinimalJson)));

    [Theory]
    [InlineData("division-2.json", "9a78ce0cc038df2f3088798e83103eac358e279c3fd27a1387d6b3d49df1e52e")]
    [InlineData("ashen-vale.json", "7331a6a17b34a87ff3deec4ee6d3d52bd4ab205362a0246e45e3a8835faf37a1")]
    public void FrozenPacksKeepTheirHashes(string filename, string hash) =>
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(LegacyPacks.Bytes(Path.Combine("packs", filename)))));

    [Fact]
    public void ConvertedDivisionPackCompilesEveryExpression()
    {
        var compiled = LegacyPacks.Load(Path.Combine("packs", "division-2.json"));
        Assert.Equal(291, compiled.Pack.Stats.Length);
        Assert.Equal(1022, compiled.Pack.Items.Length);
        Assert.Equal(compiled.Pack.Stats.Length, compiled.Formulas.Count);
        Assert.Equal(compiled.Formulas.Count, compiled.Order.Length);
        foreach (var (id, expression) in compiled.Formulas)
        {
            foreach (var dependency in expression.Identifiers.Where(compiled.Formulas.ContainsKey))
            { Assert.True(compiled.Order.IndexOf(dependency) < compiled.Order.IndexOf(id)); }
        }
        Assert.Equal(compiled.Pack.ActiveSkills.Sum(skill => skill.PreviewOutputs.Length), compiled.SkillPreviewExpressions.Keys.Count(key => !key.EndsWith(".when", StringComparison.Ordinal)));
        Assert.Equal(compiled.Pack.BuildOutputs.Length, compiled.BuildOutputExpressions.Count);
    }

    [Fact]
    public void RetainsAdvancedFrozenPackFields()
    {
        var pack = LegacyPacks.Convert(LegacyPacks.Read(LegacyPacks.Bytes(Path.Combine("packs", "division-2.json"))));
        Assert.Equal(373, pack.Talents.Length);
        Assert.Equal(65, pack.Sets.Length);
        Assert.Equal(43, pack.ActiveSkills.Length);
        Assert.Equal(111, pack.Simulation.Triggers.Length);
        Assert.Equal(17, pack.Simulation.Skills.Length);
        Assert.Equal(91, pack.NumericContextInputs.Length);
        Assert.NotNull(pack.Prototype);
        Assert.NotNull(pack.Presentation.Layout);
        Assert.NotNull(pack.Presentation.Theme);
        Assert.NotEmpty(pack.Presentation.ArtworkAssets);
        Assert.NotEmpty(pack.ActiveSkills.SelectMany(skill => skill.ModSlots));
        Assert.NotEmpty(pack.Talents.SelectMany(talent => talent.AllyEffects));
    }

    [Fact]
    public void ReaderPreservesMissingZeroAndDefaults()
    {
        var pack = LegacyPacks.Read(Encoding.UTF8.GetBytes("""
            {"stats":[{"id":"a","min":0},{"id":"b"}],"items":null,"unknownFutureField":true,
             "numericContextInputs":[{"id":"x","name":"X","min":0,"max":3}]}
            """));
        Assert.Equal(0, pack.Stats[0].Min);
        Assert.Null(pack.Stats[1].Min);
        Assert.Equal("integer", pack.Stats[0].Format);
        Assert.Empty(pack.Items);
        Assert.Empty(pack.Talents);
        Assert.Equal(1, pack.NumericContextInputs[0].Step);
        Assert.Equal(0, pack.NumericContextInputs[0].Default);
        Assert.True(pack.NumericContextInputs[0].Options.IsDefault);
        Assert.Empty(LegacyPacks.Convert(pack with { SchemaVersion = 1, Stats = [] }).NumericContextInputs[0].Options);
    }

    [Fact]
    public void ReaderUsesLastDuplicatePropertyAndIgnoresUnknownProperties()
    {
        var pack = LegacyPacks.Read(Encoding.UTF8.GetBytes("""{"id":"first","id":"last","Name":"Pack","future":{}}"""));
        Assert.Equal("last", pack.ID);
        Assert.Equal("Pack", pack.Name);
    }

    [Theory]
    [InlineData("{\"numericContextInputs\":null}")]
    [InlineData("{\"items\":[{\"modifiers\":[{\"value\":null}]}]}")]
    [InlineData("{\"items\":[{\"modifiers\":[{\"value\":\"\"}]}]}")]
    [InlineData("{\"items\":[{\"modifiers\":[{\"value\":true}]}]}")]
    [InlineData("{\"activeSkills\":[{\"family\":null}]}")]
    [InlineData("{\"stats\":\"not an array\"}")]
    [InlineData("{\"stats\":[{\"base\":1e400}]}")]
    [InlineData("{\"attributes\":[{\"id\":\"a\",\"min\":0}]}")]
    [InlineData("{\"numericContextInputs\":[null]}")]
    [InlineData("{\"curves\":{\"x\":[[1]]}}")]
    [InlineData("{\"curves\":{\"x\":[[1e400,2]]}}")]
    [InlineData("{")]
    public void ReaderRejectsMalformedPacks(string json) =>
        Assert.ThrowsAny<JsonException>(() => LegacyPacks.Read(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ReaderFillsFormatDefaultsAndCurveShape()
    {
        var pack = LegacyPacks.Read(Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"stats":[{"id":"a","kind":"derived","formula":"0","format":null},{"id":"b","kind":"derived","formula":"0","format":""}],
             "attributes":[{"id":"x","op":"flat","step":1,"min":0,"max":1,"format":null},{"id":"y","op":"flat","step":1,"min":0,"max":1,"format":""}],
             "curves":{"c":[[null,2],[2,3]]},
             "numericContextInputs":[{"id":"toggle","name":"Toggle","min":0,"max":0,"options":[{"label":"Off"}]}]}
            """));
        Assert.All(pack.Stats, stat => Assert.Equal("integer", stat.Format));
        Assert.All(pack.Attributes, attribute => Assert.Equal("decimal", attribute.Format));
        Assert.Equal(0, pack.Curves["c"][0].X);
        LegacyPacks.Compile(LegacyPacks.Convert(pack));
    }

    // Diagnostic codes belong to the v2 compiler and its own tests; here only the subject is pinned.
    [Theory]
    [InlineData("missing + 1")]
    [InlineData("unknown(1)")]
    [InlineData("curve('missing', strength)")]
    [InlineData("hp + 1")]
    [InlineData("1 +")]
    [InlineData("context_value(strength)")]
    [InlineData("context_value('missing')")]
    [InlineData("has_talent('missing')")]
    [InlineData("has_talent(1)")]
    public void InvalidFormulasInConvertedPacksAreReportedAtTheirStat(string formula)
    {
        var pack = Minimal with { Stats = Minimal.Stats.SetItem(1, Minimal.Stats[1] with { Formula = formula }) };
        var result = PackExpressionCompiler.Compile(pack);
        Assert.Null(result.Compiled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error" && diagnostic.Path == "stats.hp.formula");
    }

    [Fact]
    public void ModifierValueDependenciesAffectOrderButConditionsCannotReadStats()
    {
        var item = new ItemDefinition { ID = "item", Modifiers = [new() { Stat = "hp", Op = "flat", Value = new ModifierValue("ehp") }] };
        var cycle = PackExpressionCompiler.Compile(Minimal with { Items = [item] });
        Assert.Contains(cycle.Diagnostics, diagnostic => diagnostic.Code == "cycle");
        var condition = PackExpressionCompiler.Compile(Minimal with
        { Items = [item with { Modifiers = [item.Modifiers[0] with { Value = new ModifierValue(1), When = "ehp > 0" }] }] });
        Assert.Contains(condition.Diagnostics, diagnostic => diagnostic.Code == "unknown-stat" && diagnostic.Path == "items.item.modifiers[0].when");
        var valid = PackExpressionCompiler.Compile(Minimal with
        { Items = [item with { Modifiers = [item.Modifiers[0] with { Stat = "ehp", Value = new ModifierValue("hp * 0.1"), When = "has_tag('x')" }] }] });
        Assert.NotNull(valid.Compiled);
        Assert.Contains("items.item.modifiers[0].value", valid.Compiled.DerivedValueReads);
        Assert.Equal(["strength", "hp", "ehp"], valid.Compiled.Order);
    }

    [Fact]
    public void ModifierSelfReferencesAreRejected()
    {
        var result = PackExpressionCompiler.Compile(Minimal with
        { Items = [new() { ID = "item", Modifiers = [new() { Stat = "hp", Op = "flat", Value = new ModifierValue("hp") }] }] });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "self-reference");
    }

    [Fact]
    public void DependencyOrderIsOrdinalAndIncludesBlockedConsumers()
    {
        var result = DependencyOrder.Create(new Dictionary<string, ImmutableHashSet<string>>
        { ["z"] = [], ["a"] = [], ["c"] = ["b"], ["b"] = ["c"], ["d"] = ["c"] });
        Assert.Equal(["a", "z"], result.Order);
        Assert.Equal(["b", "c", "d"], result.Blocked);
    }

    [Fact]
    public void NumericInputsEnforceStepsAndCompleteOptions()
    {
        var input = new NumericContextInputDefinition { ID = "stacks", Name = "Stacks", Min = 0, Max = 2, Step = 1, Default = 0.5 };
        var invalidDefault = PackExpressionCompiler.Compile(Minimal with { NumericContextInputs = [input] });
        Assert.Contains(invalidDefault.Diagnostics, diagnostic => diagnostic.Code == "numeric-context-default-invalid");
        var incomplete = PackExpressionCompiler.Compile(Minimal with { NumericContextInputs = [input with { Default = 0, Options = [new(0, "None")] }] });
        Assert.Contains(incomplete.Diagnostics, diagnostic => diagnostic.Code == "numeric-context-options-invalid");
        var complete = PackExpressionCompiler.Compile(Minimal with { NumericContextInputs = [input with { Default = 0, Options = [new(0, "None"), new(1, "One"), new(2, "Two")] }] });
        Assert.Empty(complete.Diagnostics);
    }

    [Fact]
    public void ReaderDistinguishesMissingNullAndEmptyContextGates()
    {
        var pack = LegacyPacks.Read(Encoding.UTF8.GetBytes("""
            {"items":[{"id":"missing"},{"id":"null","effectsRequireContext":null},
             {"id":"empty","effectsRequireContext":{}},{"id":"valid","effectsRequireContext":{"weapon_slot":"primary"}}]}
            """));
        Assert.Null(pack.Items[0].EffectsRequireContext);
        Assert.Null(pack.Items[1].EffectsRequireContext);
        Assert.Empty(pack.Items[2].EffectsRequireContext!);
        Assert.Equal("primary", pack.Items[3].EffectsRequireContext!["weapon_slot"]);
    }

    [Fact]
    public void ReaderTurnsNullArrayElementsIntoZeroValues()
    {
        var pack = LegacyPacks.Read(Encoding.UTF8.GetBytes("""{"progression":null,"curves":null,"slots":[null],"items":[null]}"""));
        Assert.Equal("", Assert.Single(pack.Slots).ID);
        Assert.Empty(Assert.Single(pack.Items).Occupies);
        Assert.Empty(pack.Curves);
        Assert.Equal(0, pack.Progression.MaxLevel);
    }

    [Fact]
    public void ConverterReportsReaderZeroValuesInsteadOfGuessing()
    {
        var pack = LegacyPacks.Read(Encoding.UTF8.GetBytes("""{"schemaVersion":1,"items":[null]}"""));
        var conversion = V1.PackV1Converter.Convert(pack);
        Assert.Null(conversion.Pack);
        Assert.Equal("items..occupies", Assert.Single(conversion.Problems).Path);
    }
}
