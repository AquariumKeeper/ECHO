using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Infrastructure.Tests;

public sealed class FileBuildLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "build-library-" + Guid.NewGuid().ToString("N"));
    private readonly FileBuildLibrary library;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FileBuildLibraryTests() => library = new(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private static BuildSnapshot Build(string name, string mask = "mask_item") => new()
    {
        PackId = "division_2",
        PackVersion = "1",
        Equipment = ImmutableDictionary<string, string>.Empty.Add("mask", mask),
        Meta = new(name, "")
    };

    private static string Text(BuildSnapshot build) => Encoding.UTF8.GetString(BuildDocument.Encode(build));

    private static async Task<string> Rejected(Func<Task> action) => (await Assert.ThrowsAsync<LibraryException>(action)).Code;

    /// <summary>The folder's files apart from the writer lock.</summary>
    private string[] Files() => [.. Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)).Where(name => name != FileBuildLibrary.LockFile).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task AMissingFolderIsAnEmptyLibrary() => Assert.Empty(await library.ListAsync(Token));

    [Fact]
    public async Task CreatedBuildsAreListedAndLoadUnchanged()
    {
        var striker = await library.CreateAsync(Build("Striker"), Token);
        var bulwark = await library.CreateAsync(Build("bulwark"), Token);
        Assert.Matches("^[0-9a-f]{32}$", striker.Id);
        Assert.NotEqual(striker.Id, bulwark.Id);
        // One file per build, and nothing else left behind.
        Assert.Equal(new[] { striker.Id, bulwark.Id }.Select(id => id + FileBuildLibrary.Extension).Order(StringComparer.Ordinal), Files());

        // Listed by name, ignoring case, with what the list needs to show without opening each build.
        var entries = await library.ListAsync(Token);
        Assert.Equal(["bulwark", "Striker"], entries.Select(entry => entry.Name));
        var entry = entries[1];
        Assert.Equal((striker.Id, "division_2", "1", striker.Version, (string?)null), (entry.Id, entry.PackId, entry.PackVersion, entry.Version, entry.Problem));

        var loaded = await library.LoadAsync(striker.Id, Token);
        Assert.Equal(Text(Build("Striker")), Text(loaded.Build));
        Assert.Equal(striker.Version, loaded.Version);
    }

    [Fact]
    public async Task ReplacingNeedsTheVersionThatWasRead()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        var replaced = await library.ReplaceAsync(saved.Id, Build("Striker", "other_mask"), saved.Version, Token);
        Assert.NotEqual(saved.Version, replaced.Version);
        Assert.Equal("other_mask", (await library.LoadAsync(saved.Id, Token)).Build.Equipment["mask"]);

        // An edit made elsewhere since the first read is a conflict, not lost.
        Assert.Equal(LibraryErrorCodes.Conflict, await Rejected(() => library.ReplaceAsync(saved.Id, Build("Stale"), saved.Version, Token)));
        Assert.Equal("Striker", (await library.LoadAsync(saved.Id, Token)).Build.Meta.Name);

        await library.DeleteAsync(saved.Id, replaced.Version, Token);
        Assert.Equal(LibraryErrorCodes.Conflict, await Rejected(() => library.ReplaceAsync(saved.Id, Build("Gone"), replaced.Version, Token)));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task DeletingNeedsTheVersionThatWasRead()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        Assert.Equal(LibraryErrorCodes.Conflict, await Rejected(() => library.DeleteAsync(saved.Id, new string('0', 64), Token)));
        await library.DeleteAsync(saved.Id, saved.Version, Token);
        Assert.Equal(LibraryErrorCodes.NotFound, await Rejected(() => library.LoadAsync(saved.Id, Token)));
        Assert.Equal(LibraryErrorCodes.Conflict, await Rejected(() => library.DeleteAsync(saved.Id, saved.Version, Token)));
    }

    /// <summary>Rolling back: a build from a newer planner says so, stays as it was, and is only replaced by name.</summary>
    [Fact]
    public async Task ARecordFromANewerPlannerIsKeptAndSaysSo()
    {
        Directory.CreateDirectory(directory);
        var id = new string('b', 32);
        var path = Path.Combine(directory, id + FileBuildLibrary.Extension);
        var written = $$"""{ "schemaVersion": {{BuildDocument.SchemaVersion + 1}}, "build": {} }""";
        await File.WriteAllTextAsync(path, written, Token);

        var entry = Assert.Single(await library.ListAsync(Token));
        Assert.Equal("build document was made by a newer version of the planner", entry.Problem);
        Assert.Equal(LibraryErrorCodes.Damaged, await Rejected(() => library.LoadAsync(id, Token)));
        await library.CreateAsync(Build("Another"), Token);
        Assert.Equal(written, await File.ReadAllTextAsync(path, Token));
        // A stale version can't replace it either.
        Assert.Equal(LibraryErrorCodes.Conflict, await Rejected(() => library.ReplaceAsync(id, Build("Over it"), new string('0', 64), Token)));
        Assert.Equal(written, await File.ReadAllTextAsync(path, Token));
    }

    /// <summary>A damaged record is reported, never skipped or replaced without the caller naming it.</summary>
    [Fact]
    public async Task DamagedRecordsAreListedAndReplacedOnlyByName()
    {
        Directory.CreateDirectory(directory);
        var id = new string('a', 32);
        await File.WriteAllTextAsync(Path.Combine(directory, id + FileBuildLibrary.Extension), "{ not a document", Token);

        var entry = Assert.Single(await library.ListAsync(Token));
        Assert.Equal(id, entry.Id);
        Assert.StartsWith("build document is not valid JSON", entry.Problem, StringComparison.Ordinal);
        Assert.Equal(LibraryErrorCodes.Damaged, await Rejected(() => library.LoadAsync(id, Token)));

        await library.ReplaceAsync(id, Build("Recovered"), entry.Version, Token);
        Assert.Equal("Recovered", (await library.LoadAsync(id, Token)).Build.Meta.Name);
    }

    [Fact]
    public async Task OnlyRecordsWithLibraryIdsAreListedOrAddressable()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "notes.build.json"), "{}", Token);
        await File.WriteAllTextAsync(Path.Combine(directory, new string('A', 32) + FileBuildLibrary.Extension), "{}", Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "readme.txt"), "", Token);
        Assert.Equal([saved.Id], (await library.ListAsync(Token)).Select(entry => entry.Id));

        foreach (var id in new[] { "../" + saved.Id, saved.Id.ToUpperInvariant(), "", "notes" })
        { Assert.Equal(LibraryErrorCodes.InvalidId, await Rejected(() => library.LoadAsync(id, Token))); }
    }

    [Fact]
    public async Task AnUnsaveableBuildWritesNothing()
    {
        Assert.Equal(LibraryErrorCodes.Unsaveable, await Rejected(() => library.CreateAsync(Build("Striker") with { PackId = "" }, Token)));
        Assert.False(Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any());
    }

    /// <summary>A record too large to be a document is damaged, and its version is still the hash of its own bytes.</summary>
    [Fact]
    public async Task OversizedRecordsAreDamagedWithTheirOwnVersions()
    {
        Directory.CreateDirectory(directory);
        var (first, second) = (new string('b', 32), new string('c', 32));
        var size = BuildDocument.MaxBytes + 10;
        await File.WriteAllBytesAsync(Path.Combine(directory, first + FileBuildLibrary.Extension), new byte[size], Token);
        await File.WriteAllBytesAsync(Path.Combine(directory, second + FileBuildLibrary.Extension), [.. new byte[size - 1], 1], Token);
        var entries = await library.ListAsync(Token);
        Assert.All(entries, entry => Assert.StartsWith("build document is larger than", entry.Problem, StringComparison.Ordinal));
        Assert.NotEqual(entries[0].Version, entries[1].Version);
        Assert.Equal(LibraryErrorCodes.Damaged, await Rejected(() => library.LoadAsync(first, Token)));
        Assert.Equal(LibraryErrorCodes.Conflict, await Rejected(() => library.ReplaceAsync(first, Build("X"), entries.Single(entry => entry.Id == second).Version, Token)));
    }

    /// <summary>Two libraries on one folder (the desktop and the CLI) can't both replace the same version.</summary>
    [Fact]
    public async Task WritersInDifferentLibrariesOnOneFolderConflictInsteadOfLosingAnEdit()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        var other = new FileBuildLibrary(directory);
        var outcomes = await Task.WhenAll(new[] { library, other }.Select(async (writer, index) =>
        {
            try { await writer.ReplaceAsync(saved.Id, Build($"Edit {index}"), saved.Version, Token); return "saved"; }
            catch (LibraryException exception) { return exception.Code; }
        }));
        Assert.Equal([LibraryErrorCodes.Conflict, "saved"], outcomes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AWriterThatCantGetTheLockReportsTheLibraryBusy()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        var impatient = new FileBuildLibrary(directory, TimeSpan.FromMilliseconds(100));
        await using (new FileStream(Path.Combine(directory, FileBuildLibrary.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(LibraryErrorCodes.Busy, await Rejected(() => impatient.ReplaceAsync(saved.Id, Build("Blocked"), saved.Version, Token)));
            // Readers take no lock.
            Assert.Equal("Striker", (await impatient.LoadAsync(saved.Id, Token)).Build.Meta.Name);
        }
        await impatient.ReplaceAsync(saved.Id, Build("Unblocked"), saved.Version, Token);
    }

    /// <summary>
    /// A save waits out a brief reader (as an antivirus scan or this library's own listing would be) instead of
    /// failing, and reports the record busy only if it stays open past the lock timeout. A delete isn't blocked.
    /// </summary>
    [Fact]
    public async Task ASaveWaitsOutABriefReaderAndADeleteIsntBlocked()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        var path = Path.Combine(directory, saved.Id + FileBuildLibrary.Extension);
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var released = Task.Delay(TimeSpan.FromMilliseconds(150), Token).ContinueWith(_ => reader.Dispose(), TaskScheduler.Default);
        var replaced = await library.ReplaceAsync(saved.Id, Build("Edited"), saved.Version, Token);
        await released;
        Assert.Equal("Edited", (await library.LoadAsync(saved.Id, Token)).Build.Meta.Name);

        var impatient = new FileBuildLibrary(directory, TimeSpan.FromMilliseconds(100));
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(LibraryErrorCodes.Busy, await Rejected(() => impatient.ReplaceAsync(saved.Id, Build("Blocked"), replaced.Version, Token)));
            await library.DeleteAsync(saved.Id, replaced.Version, Token);
        }
        Assert.Empty(await library.ListAsync(Token));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task HiddenRecordsAreStillListed()
    {
        var saved = await library.CreateAsync(Build("Striker"), Token);
        var path = Path.Combine(directory, saved.Id + FileBuildLibrary.Extension);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        Assert.Equal([saved.Id], (await library.ListAsync(Token)).Select(entry => entry.Id));
    }

    [Fact]
    public async Task ParallelSavesEachGetTheirOwnRecordAndLeaveNoTemporaryFiles()
    {
        var saved = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => library.CreateAsync(Build($"Build {index}"), Token)));
        Assert.Equal(20, saved.Select(build => build.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(20, (await library.ListAsync(Token)).Length);
        Assert.All(Files(), file => Assert.EndsWith(FileBuildLibrary.Extension, file, StringComparison.Ordinal));
    }
}
