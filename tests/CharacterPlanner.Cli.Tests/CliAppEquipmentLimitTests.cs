using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;
using CharacterPlanner.Infrastructure.Sessions;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    [Fact]
    public async Task EquipmentLimitsSurfaceThroughDocumentsSimulationComparisonAndLiveSession()
    {
        var pack = Mini with
        {
            Capabilities = [PackCapabilities.EquipmentLimits],
            Items = [Mini.Items[0] with { Tags = ["limited"] }],
            EquipmentLimits = [new() { ID = "test_allowance", Name = "Test allowance", Tag = "limited", Slots = ["mask"], Maximum = 0 }]
        };
        var directory = WriteMini(pack: pack);
        var build = BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a");
        var path = WriteBuild(build);
        var resolved = await RunAsync("build", "resolve", directory, path);
        Assert.Contains(("error", LoadoutResolver.EquipmentLimitExceeded), Diagnostics(resolved, false));
        var simulated = await RunAsync("build", "simulate", directory, path);
        Assert.Contains(("error", LoadoutResolver.EquipmentLimitExceeded), Diagnostics(simulated, false));
        Assert.False(simulated.Json.TryGetProperty("result", out _));
        var compared = await RunAsync("build", "compare", directory, path, path);
        Assert.Contains(("error", "compare-build-invalid"), Diagnostics(compared, false));
        Assert.Contains(LoadoutResolver.EquipmentLimitExceeded, compared.Output, StringComparison.Ordinal);
        Assert.False(compared.Json.TryGetProperty("result", out _));
        var shared = await RunAsync("build", "share", directory, path);
        Assert.Equal(CliApp.Success, shared.ExitCode);
        Assert.Equal("mask_a", BuildDocument.Decode(File.ReadAllBytes(path)).Equipment["mask"]);

        var loaded = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        var compiled = loaded.Validation.Compiled!;
        var session = new PlannerSession(compiled, PlannerState.Of(build), new OperationApplier(compiled));
        await using var host = await SessionPipeHost.StartAsync(session, directory, loaded.ManifestSha256, Sessions, TestContext.Current.CancellationToken);
        var live = await RunAsync("session", "resolve", "--session-dir", Sessions);
        // Resolving an editable live draft succeeds as a transport request and includes its validation errors.
        Assert.Contains(("error", LoadoutResolver.EquipmentLimitExceeded), Diagnostics(live, true));
        Assert.Equal("mask_a", session.Snapshot.State.Build.Equipment["mask"]);
    }
}
