using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    [Fact]
    public async Task ComparisonAndRankingExportMissingMechanicsBesideZeroChanges()
    {
        var pack = WithContext with
        {
            Talents = [new() { ID = "missing_effect", Name = "Missing effect", Description = "An unmodeled mechanic" }],
            Items = [WithContext.Items[0] with { TalentSlots = [new() { ID = "talent", Name = "Talent", Choices = ["missing_effect"] }] }]
        };
        var directory = WriteMini(pack: pack);
        var build = BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a").WithTalentSelection("mask", "talent", "missing_effect");
        var path = WriteBuild(build);
        var compared = await RunAsync("build", "compare", directory, path, path);
        Assert.Equal(CliApp.Success, compared.ExitCode);
        var result = compared.Json.GetProperty("result");
        Assert.Equal(0, result.GetProperty("changed").GetInt32());
        var coverage = result.GetProperty("coverage").GetProperty("a");
        var talent = Assert.Single(coverage.GetProperty("talents").EnumerateArray());
        Assert.Empty(talent.GetProperty("channels").EnumerateArray());
        Assert.Contains("does not evaluate", talent.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("not measured", coverage.GetProperty("inputBasis").GetString(), StringComparison.Ordinal);
        var ranked = await RunAsync("build", "rank", directory, path, "mask", "armor");
        Assert.Equal(CliApp.Success, ranked.ExitCode);
        Assert.Equal("missing_effect", Assert.Single(ranked.Json.GetProperty("result").GetProperty("coverage").GetProperty("talents").EnumerateArray()).GetProperty("talent").GetString());
    }
}
