using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>A saved build's Rename and Delete and the earlier-pack-version note, as Go's library rows (F52, F53, G391).</summary>
public sealed class SavedBuildTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "saved-builds-" + Guid.NewGuid().ToString("N"));
    private readonly PlannerSession session;
    private readonly FileBuildLibrary library;
    private readonly EditorViewModel editor;

    public SavedBuildTests()
    {
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        library = new FileBuildLibrary(folder);
        editor = new(session, library, new TestPack.QueueDispatcher());
    }

    public void Dispose()
    {
        editor.Dispose();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task DeletingABuildThisVersionCantReadSaysWhy()
    {
        await SaveAs("Tank");
        var path = Assert.Single(Directory.GetFiles(folder, "*" + FileBuildLibrary.Extension));
        var written = $$"""{ "schemaVersion": {{BuildDocument.SchemaVersion + 1}}, "build": {} }""";
        await File.WriteAllTextAsync(path, written, TestContext.Current.CancellationToken);
        await editor.RefreshLibraryCommand.ExecuteAsync(null);
        var host = new TestHost();
        editor.Host = host;
        await editor.DeleteSavedCommand.ExecuteAsync(editor.LibraryEntries.Single());
        Assert.Equal("This saved build can't be read: build document was made by a newer version of the planner. If a newer planner made it, that planner can still open it. Delete it from the library? This can't be undone.",
            Assert.Single(host.Questions));
        Assert.Equal(written, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private async Task SaveAs(string name)
    {
        editor.BuildName = name;
        editor.RenameCommand.Execute(null);
        await editor.SaveAsNewCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ASavedBuildIsRenamedWithoutOpeningIt()
    {
        await SaveAs("Tank");
        await editor.NewBuildCommand.ExecuteAsync(null);
        editor.Host = new TestHost { Answer = "Bulwark" };
        await editor.RenameSavedCommand.ExecuteAsync(editor.LibraryEntries.Single());
        Assert.Equal("Bulwark", Assert.Single(editor.LibraryEntries).Name);
        Assert.Equal("Bulwark", (await library.LoadAsync(editor.LibraryEntries[0].Id, TestContext.Current.CancellationToken)).Build.Meta.Name);
        // The open build, a new one, is untouched.
        Assert.Equal("New Build", session.Snapshot.State.Build.Meta.Name);

        // A blank name, or cancelling, changes nothing.
        editor.Host = new TestHost { Answer = "  " };
        await editor.RenameSavedCommand.ExecuteAsync(editor.LibraryEntries[0]);
        editor.Host = new TestHost();
        await editor.RenameSavedCommand.ExecuteAsync(editor.LibraryEntries[0]);
        Assert.Equal("Bulwark", editor.LibraryEntries[0].Name);
    }

    [Fact]
    public async Task RenamingTheOpenBuildsRecordRenamesTheBuildAndKeepsItSaved()
    {
        await SaveAs("Tank");
        editor.Host = new TestHost { Answer = "Bulwark" };
        await editor.RenameSavedCommand.ExecuteAsync(editor.LibraryEntries.Single());
        Assert.Equal("Bulwark", editor.BuildName);
        Assert.False(editor.IsDirty);
        Assert.Null(editor.Message);
        // Saving again replaces the record, with no conflict over the rename.
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.Message);
        Assert.Equal("Bulwark", Assert.Single(editor.LibraryEntries).Name);
    }

    [Fact]
    public async Task DeletingAsksFirstAndLeavesTheOpenBuildUnsaved()
    {
        await SaveAs("Tank");
        var host = new TestHost();
        editor.Host = host;
        await editor.DeleteSavedCommand.ExecuteAsync(editor.LibraryEntries.Single());
        Assert.Equal("Delete \"Tank\" from the library? This can't be undone.", Assert.Single(host.Questions));
        Assert.Single(editor.LibraryEntries);

        editor.Host = new TestHost { Confirms = true };
        await editor.DeleteSavedCommand.ExecuteAsync(editor.LibraryEntries.Single());
        Assert.Empty(editor.LibraryEntries);
        Assert.Empty(await library.ListAsync(TestContext.Current.CancellationToken));
        Assert.Null(editor.RecordId);
        Assert.True(editor.IsDirty);
        Assert.Equal("Tank", editor.BuildName);
    }

    [Fact]
    public async Task ABuildSavedWithAnEarlierPackVersionSaysSo()
    {
        var older = BuildSnapshot.Create(TestPack.Division.Pack, "Old") with { PackVersion = "an-earlier-version" };
        await library.CreateAsync(older, TestContext.Current.CancellationToken);
        await SaveAs("Current");
        var saved = editor.SavedBuilds.ToDictionary(build => build.Name);
        Assert.True(saved["Old"].IsEarlierPack);
        Assert.EndsWith(" · earlier pack version", saved["Old"].Detail, StringComparison.Ordinal);
        Assert.False(saved["Current"].IsEarlierPack);
        Assert.StartsWith("Saved ", saved["Current"].Detail, StringComparison.Ordinal);
        // It still opens, checked against the current pack.
        await editor.OpenCommand.ExecuteAsync(saved["Old"].Entry);
        Assert.Equal("Old", editor.BuildName);
    }
}
