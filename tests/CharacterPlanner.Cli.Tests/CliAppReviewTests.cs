using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    [Fact]
    public async Task ResolveExportsSortedTypedAssumptionsThatRoundTripAsContext()
    {
        var pack = WithContext with
        {
            NumericContextInputs = [.. WithContext.NumericContextInputs,
                new() { ID = "z_bonus", Name = "Z bonus", Min = 0, Max = 10, Step = 1 },
                new() { ID = "a_bonus", Name = "A bonus", Min = 0, Max = 10, Step = 1 }]
        };
        var directory = WriteMini(pack: pack);
        var path = WriteBuild();
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, """{"z_bonus":3,"enabled":"true","bonus":2.5,"a_bonus":1}""");
        var run = await RunAsync("build", "resolve", directory, path, "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        var assumptions = run.Json.GetProperty("result").GetProperty("assumptions");
        foreach (var map in assumptions.EnumerateObject())
        {
            var keys = map.Value.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        }
        var explicitValues = assumptions.GetProperty("explicit");
        Assert.Equal(2.5, explicitValues.GetProperty("bonus").GetDouble());
        Assert.Equal("true", explicitValues.GetProperty("enabled").GetString());
        File.WriteAllText(context, explicitValues.GetRawText());
        var repeated = await RunAsync("build", "resolve", directory, path, "--context", context);
        Assert.Equal(CliApp.Success, repeated.ExitCode);
        Assert.Equal(run.Output, repeated.Output);
    }

    [Fact]
    public async Task CatalogSortsNestedDefinitionMapsAndPreservesArrays()
    {
        var flags = new[] { "z_flag", "b_flag", "a_flag", "m_flag", "j_flag" };
        var pack = CatalogPack with
        {
            Selectors = [.. flags.Select(id => new SelectorDefinition { ID = id, Name = id, Values = ["true", "false"] })],
            Items = [CatalogPack.Items[0] with
            {
                AttributeSlots = [CatalogPack.Items[0].AttributeSlots[0] with { ID = "z_minor" },
                    CatalogPack.Items[0].AttributeSlots[0] with { ID = "a_minor" }],
                Modifiers = [CatalogPack.Items[0].Modifiers[0] with
                { RequiresContext = flags.ToImmutableDictionary(id => id, _ => "true", StringComparer.Ordinal) }]
            }]
        };
        var run = await RunAsync("catalog", "show", WriteMini(pack: pack), "items", "mask_z");
        Assert.Equal(CliApp.Success, run.ExitCode);
        var entry = run.Json.GetProperty("result").GetProperty("entry");
        var requires = entry.GetProperty("modifiers")[0].GetProperty("requiresContext");
        Assert.Equal(flags.Order(StringComparer.Ordinal), requires.EnumerateObject().Select(property => property.Name));
        Assert.Equal("2 + context_value('bonus')", entry.GetProperty("modifiers")[0].GetProperty("value").GetString());
        Assert.Equal(new[] { "z_minor", "a_minor" }, entry.GetProperty("attributeSlots").EnumerateArray().Select(slot => slot.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task ApplyAttributesUnsaveableFinalStateToTheBatchWithoutPublishingIt()
    {
        var directory = WriteMini();
        var build = BuildSnapshot.Create(Mini) with { Meta = new() { Notes = new string('n', BuildDocument.MaxBytes / 2) } };
        var path = WriteBuild(build);
        var original = File.ReadAllBytes(path);
        var operations = WriteOperations(JsonSerializer.Serialize(new[] { new { kind = "name", text = new string('x', BuildDocument.MaxBytes / 2) } }));
        var run = await RunAsync("build", "apply", directory, path, operations);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(("error", "build-unsaveable"), Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
        Assert.Equal(operations, run.Json.GetProperty("diagnostics")[0].GetProperty("path").GetString());
        Assert.Equal(0, run.Json.GetProperty("operationErrors")[0].GetProperty("operation").GetInt32());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    public async Task CatalogCanBrowseSemanticallyValidMetadataWithoutArtwork(string command)
    {
        var pack = Mini with
        {
            Presentation = new()
            {
                ArtworkSources = [new() { ID = "source", Name = "Source" }],
                ArtworkAssets = [new() { ID = "icon_mask", Path = "assets/icons/mask.png", Kind = "icon", SHA256 = PackFiles.Sha256(Encoding.UTF8.GetBytes("icon")), SourceID = "source" }]
            }
        };
        var directory = WriteMini(pack: pack);
        var args = command == "list" ? new[] { "catalog", command, directory, "items" } : ["catalog", command, directory, "items", "mask_a"];
        var run = await RunAsync(args);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        var validated = await RunAsync("pack", "validate", directory);
        Assert.Equal(CliApp.InvalidInput, validated.ExitCode);
        Assert.Contains(("error", "artwork-missing"), Diagnostics(validated, false));

        // A release's marker withholds the artwork deliberately: validation and build commands pass cleanly.
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, PackFiles.Manifest))))
        {
            var digest = manifest.RootElement.TryGetProperty("assetsSha256", out var value) ? value.GetString() : "";
            File.WriteAllText(Path.Combine(directory, PackFileLoader.ArtworkWithheldMarker), JsonSerializer.Serialize(new { assetsSha256 = digest }));
        }
        var released = await RunAsync("pack", "validate", directory);
        Assert.Equal(CliApp.Success, released.ExitCode);
        Assert.Empty(Diagnostics(released, true));
        Assert.True(released.Json.GetProperty("result").GetProperty("artworkWithheld").GetBoolean());
    }

    [Theory]
    [InlineData("pack", "validate", "")]
    [InlineData("share", "convert", "", "code")]
    [InlineData("catalog", "list", "", "items")]
    [InlineData("build", "new", "")]
    [InlineData("build", "resolve", "", "build.json")]
    [InlineData("build", "apply", "", "build.json", "operations.json")]
    public async Task EmptyPackPathsReturnStructuredFailures(params string[] args)
    {
        var run = await RunAsync(args);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(("error", "pack-path-invalid"), Diagnostics(run, false));
        Assert.Equal("", run.Error);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    public async Task CatalogStillRejectsSemanticallyInvalidMetadata(string command)
    {
        var directory = WriteMini(pack: Mini with { Stats = [Mini.Stats[0] with { Formula = "invalid(" }] });
        var args = command == "list" ? new[] { "catalog", command, directory, "items" } : ["catalog", command, directory, "items", "mask_a"];
        var run = await RunAsync(args);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.NotEmpty(Diagnostics(run, false));
        Assert.False(run.Json.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task UsageAndDescribeAdvertiseTheSameGrammars()
    {
        var describe = await RunAsync("describe");
        var usage = await RunAsync();
        var commands = describe.Json.GetProperty("result").GetProperty("commands").EnumerateArray().Select(value => value.GetString());
        Assert.Equal("Usage: CharacterPlanner.Cli " + string.Join(" | ", commands) + Environment.NewLine, usage.Error);
    }
}
