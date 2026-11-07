using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Share and Import (F51, F93), as the Go app's sharedialog tests, with this app's Open semantics.</summary>
public sealed class SharingViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "sharing-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;

    public SharingViewModelTests()
    {
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private BuildSnapshot Build => session.Snapshot.State.Build;

    [Fact]
    public void ShareGivesTheBuildsCodeWithoutItsParty()
    {
        Assert.True(Edit(new EquipmentOperation("mask", "mask_n_5_11_tactical_mask"), new PartyMemberOperation("Sam", Armor: 5)));
        var shared = editor.Sharing.Share();
        Assert.Null(shared.Problem);
        Assert.Equal(ShareCode.Payload(Build), ShareCode.Payload(ShareCode.Decode(shared.Code)));
        Assert.Empty(ShareCode.Decode(shared.Code).Party);

        // A build that can't be encoded says why rather than failing.
        session.Reset(PlannerState.Of(Build with { Meta = new("\uD800") }), session.Snapshot.Revision);
        var refused = editor.Sharing.Share();
        Assert.StartsWith("This build can't be shared: ", refused.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportOpensACodeAsOpenDoesAndRefusesBadOnes()
    {
        Assert.NotEqual("", Problem("not a real share code!!"));
        Assert.Equal("The share code is for pack \"other\", not \"division_2\"", Problem(ShareCode.Encode(BuildSnapshot.Create(TestPack.Division.Pack) with { PackId = "other" })));

        var giver = PartyViewModelTests.Giver("Alex");
        var import = editor.Sharing.Read($"  {ShareCode.Encode(giver)}\n", out _);
        Assert.NotNull(import);

        // Unsaved changes are kept unless discarding them is confirmed.
        Assert.True(Edit(new NameOperation("Mine")));
        editor.Host = new TestHost(() => Task.FromResult(false));
        await editor.Sharing.ImportAsync(import);
        Assert.Equal("Mine", Build.Meta.Name);
        Assert.NotNull(editor.Message);

        editor.Host = new TestHost(() => Task.FromResult(true));
        await editor.Sharing.ImportAsync(import);
        Assert.Equal(ShareCode.Payload(giver), ShareCode.Payload(Build));
        Assert.False(editor.CanUndo);
        Assert.Null(editor.RecordId);
        Assert.Null(editor.Message);
    }

    [Fact]
    public async Task AGoEraCodesDroppedFieldsAreMentioned()
    {
        var giver = PartyViewModelTests.Giver("Old");
        using var legacy = new EditorViewModel(session, new FileBuildLibrary(folder), dispatcher) { ReadShareCode = _ => new(giver, ["allocations", "activeScenarios"]) };
        var import = legacy.Sharing.Read("anything", out _)!;
        await legacy.Sharing.ImportAsync(import);
        Assert.Equal("Loaded. The share code also held attribute allocations, active scenarios, which this planner no longer models.", legacy.Message);
    }

    /// <summary>Applies operations as the editor's controls do, and shows the result.</summary>
    private bool Edit(params BuildOperation[] operations)
    {
        var result = session.Apply([.. operations], session.Snapshot.Revision, EditOrigin.Desktop);
        dispatcher.RunPending();
        return result.Succeeded;
    }

    private string Problem(string code)
    {
        Assert.Null(editor.Sharing.Read(code, out var problem));
        return problem;
    }
}
