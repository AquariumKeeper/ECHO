using System.Text.Json;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    [Fact]
    public async Task ShareEncodesTheBuildAsAnAppCodeThatConvertsBackUnchanged()
    {
        var directory = WriteMini();
        var path = WriteBuild(BuildSnapshot.Create(Mini).WithEquipment("mask", "mask_a") with { Meta = new("Shared") });
        var before = FilesIn(root);
        var run = await RunAsync("build", "share", directory, path);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Empty(Diagnostics(run, true));
        var result = run.Json.GetProperty("result");
        var code = result.GetProperty("code").GetString()!;
        Assert.Equal(ShareCode.Encode(BuildDocument.Decode(File.ReadAllBytes(path))), code);
        Assert.Equal("Shared", result.GetProperty("name").GetString());
        var converted = await RunAsync("share", "convert", directory, code);
        Assert.Equal("app", converted.Json.GetProperty("result").GetProperty("format").GetString());
        Assert.Equal(code, converted.Json.GetProperty("result").GetProperty("code").GetString());
        Assert.Equal(before, FilesIn(root));
    }

    [Fact]
    public async Task CompareReportsEveryStatChangeUnderOneContext()
    {
        var directory = WriteMini(pack: WithContext with
        {
            Stats = [new() { ID = "armor", Name = "Armor", Formula = "context_value('bonus')" }, new() { ID = "health", Name = "Health", Formula = "0" }]
        });
        var a = WriteBuild(BuildSnapshot.Create(Mini).WithEquipment("mask", "mask_a"));
        var b = Path.Combine(root, "b.json");
        File.WriteAllBytes(b, BuildDocument.Encode(BuildSnapshot.Create(Mini)));
        var context = Path.Combine(root, "context.json");
        File.WriteAllText(context, """{"bonus":2}""");
        var run = await RunAsync("build", "compare", directory, a, b, "--context", context);
        Assert.Equal(CliApp.Success, run.ExitCode);
        var result = run.Json.GetProperty("result");
        var stats = result.GetProperty("stats").EnumerateArray().ToArray();
        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.Equal(new[] { "armor", "health" }, stats.Select(stat => stat.GetProperty("id").GetString()));
        Assert.Equal(12, stats[0].GetProperty("a").GetDouble());
        Assert.Equal(2, stats[0].GetProperty("b").GetDouble());
        Assert.Equal(-10, stats[0].GetProperty("delta").GetDouble());
        Assert.Equal(0, stats[1].GetProperty("delta").GetDouble());
    }

    [Fact]
    public async Task CompareRefusesABuildThatDoesNotResolveCleanly()
    {
        var directory = WriteMini();
        var a = WriteBuild();
        var b = Path.Combine(root, "b.json");
        File.WriteAllBytes(b, BuildDocument.Encode(BuildSnapshot.Create(Mini).WithEquipment("mask", "missing_mask")));
        var run = await RunAsync("build", "compare", directory, a, b);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        var diagnostic = run.Json.GetProperty("diagnostics").EnumerateArray().Single(entry => entry.GetProperty("code").GetString() == "compare-build-invalid");
        Assert.Equal(b, diagnostic.GetProperty("path").GetString());
        Assert.False(run.Json.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task RankedOperationsApplyToTheRankedResultUnderTheReportedContext()
    {
        var (build, _) = await WriteDivisionWeaponBuildAsync();
        // No context: ranking judges the weapon slot as the weapon in hand and reports that context.
        var run = await RunAsync("build", "rank", CommittedDivision, build, "primary_weapon", "equipped_weapon_damage");
        Assert.Equal(CliApp.Success, run.ExitCode);
        var result = run.Json.GetProperty("result");
        var upgrades = result.GetProperty("upgrades").EnumerateArray().ToArray();
        Assert.NotEmpty(upgrades);
        var deltas = upgrades.Select(upgrade => upgrade.GetProperty("delta").GetDouble()).ToArray();
        Assert.Equal(deltas.OrderByDescending(delta => delta), deltas);
        var best = upgrades[0];
        Assert.Contains(best.GetProperty("kind").GetString(), new[] { "attribute", "mod", "talent" });
        Assert.Equal("primary_weapon", result.GetProperty("context").GetProperty("weapon_slot").GetString());
        var context = Path.Combine(root, "ranked-context.json");
        File.WriteAllText(context, result.GetProperty("context").GetRawText());

        var operations = WriteOperations(best.GetProperty("operations").GetRawText());
        var applied = await RunAsync("build", "apply", CommittedDivision, build, operations, "--context", context);
        Assert.Equal(CliApp.Success, applied.ExitCode);
        var edited = Path.Combine(root, "edited.json");
        File.WriteAllText(edited, applied.Json.GetProperty("result").GetProperty("document").GetRawText());
        var resolved = await RunAsync("build", "resolve", CommittedDivision, edited, "--context", context);
        Assert.Equal(best.GetProperty("after").GetDouble(),
            resolved.Json.GetProperty("result").GetProperty("stats").GetProperty("equipped_weapon_damage").GetDouble());
    }

    [Theory]
    [InlineData("chest", "equipped_weapon_damage", "has no item equipped")]
    [InlineData("primary_weapon", "no_such_stat", "unknown stat")]
    public async Task RankReportsWhyNothingCanBeRanked(string slot, string stat, string message)
    {
        var (build, context) = await WriteDivisionWeaponBuildAsync();
        var run = await RunAsync("build", "rank", CommittedDivision, build, slot, stat, "--context", context);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        var diagnostic = run.Json.GetProperty("diagnostics").EnumerateArray().Single(entry => entry.GetProperty("severity").GetString() == "error");
        Assert.Equal("rank-unavailable", diagnostic.GetProperty("code").GetString());
        Assert.Contains(message, diagnostic.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("build", "compare", "pack", "a.json")]
    [InlineData("build", "compare", "pack", "a.json", "b.json", "--context")]
    [InlineData("build", "rank", "pack", "build.json", "slot")]
    [InlineData("build", "rank", "pack", "build.json", "slot", "stat", "--options", "x")]
    [InlineData("build", "share", "pack", "build.json", "extra")]
    public async Task MalformedShareCompareAndRankArgumentsAreUsageErrors(params string[] args)
    {
        var run = await RunAsync(args);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        AssertInvalidCommand(run);
    }

    [Fact]
    public async Task ShareReadsOnlyPackMetadataSoMissingArtworkDoesNotBlockIt()
    {
        var created = await RunAsync("build", "new", CommittedDivision);
        var path = Path.Combine(root, "division.json");
        File.WriteAllText(path, created.Json.GetProperty("result").GetRawText());
        // The test copy of the pack has no artwork; full build commands refuse it, share does not.
        Assert.Equal(CliApp.InvalidInput, (await RunAsync("build", "resolve", DivisionV2, path)).ExitCode);
        var run = await RunAsync("build", "share", DivisionV2, path);
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.False(string.IsNullOrEmpty(run.Json.GetProperty("result").GetProperty("code").GetString()));
    }

    [Fact]
    public async Task CompareRefusesAChangeOutsideTheDoubleRange()
    {
        var pack = Mini with
        {
            Items = [Mini.Items[0] with { Modifiers = [Mini.Items[0].Modifiers[0] with { Value = new(1.7e308) }] },
                Mini.Items[0] with { ID = "mask_b", Name = "Mask B", Modifiers = [Mini.Items[0].Modifiers[0] with { Value = new(-1.7e308) }] }]
        };
        var directory = WriteMini(pack: pack);
        var a = WriteBuild(BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a"));
        var b = Path.Combine(root, "b.json");
        File.WriteAllBytes(b, BuildDocument.Encode(BuildSnapshot.Create(pack).WithEquipment("mask", "mask_b")));
        var run = await RunAsync("build", "compare", directory, a, b);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(Diagnostics(run, false), diagnostic => diagnostic.Code == "compare-value-overflow");
    }

    [Fact]
    public async Task AnEmptyContextPathIsRejectedNotIgnored()
    {
        var directory = WriteMini();
        var path = WriteBuild();
        var run = await RunAsync("build", "compare", directory, path, path, "--context", "");
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(Diagnostics(run, false), diagnostic => diagnostic.Code == "input-path-invalid");
        Assert.True(JsonDocument.Parse(run.Output).RootElement.TryGetProperty("diagnostics", out _));
    }
}
