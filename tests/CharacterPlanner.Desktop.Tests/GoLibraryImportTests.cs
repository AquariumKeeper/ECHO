using System.Security.Cryptography;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The one-time import of the Go app's library on first launch (ADR 0002, ledger F52).</summary>
public sealed class GoLibraryImportTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "go-import-" + Guid.NewGuid().ToString("N"));
    private string GoLibrary => Path.Combine(folder, "go", "character-planner", "builds.json");
    private string Data => Path.Combine(folder, "data");

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private void PlaceGoLibrary()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GoLibrary)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "go-library", "builds.json"), GoLibrary);
    }

    /// <summary>The captured Go library's six builds arrive once, named and with their pack version, and the Go file is untouched.</summary>
    [Fact]
    public async Task TheGoLibraryIsImportedOnceAndNeverWritten()
    {
        PlaceGoLibrary();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(GoLibrary, TestContext.Current.CancellationToken));
        var library = new FileBuildLibrary(Path.Combine(Data, "library"));
        var import = new GoLibraryImport(GoLibrary, Data);

        var result = await import.RunAsync(library, TestPack.Division.Pack.ID, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(6, result.Imported);
        Assert.Empty(result.Skipped);
        Assert.Equal("Imported 6 builds from the Go app's library", result.Summary);
        var entries = await library.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["foundry-shield", "hard-wired-skills", "party-buffs", "prototype-gear", "shotgun-close", "striker-rifle"], entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.All(entries, entry => Assert.Equal("y8s3.2-pve.2026-10-25.shields-swaps.1", entry.PackVersion));
        Assert.All(entries, entry => Assert.Null(entry.Problem));
        Assert.True(File.Exists(import.Marker));
        Assert.True(File.Exists(import.Copy));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(GoLibrary, TestContext.Current.CancellationToken)));

        // A second launch imports nothing more.
        Assert.Null(await import.RunAsync(library, TestPack.Division.Pack.ID, TestContext.Current.CancellationToken));
        Assert.Equal(6, (await library.ListAsync(TestContext.Current.CancellationToken)).Length);
    }

    /// <summary>Without a Go library the import is still recorded, so one that appears later isn't imported over the app's own builds.</summary>
    [Fact]
    public async Task WithoutAGoLibraryTheImportIsDoneAndNotRetried()
    {
        var library = new FileBuildLibrary(Path.Combine(Data, "library"));
        var import = new GoLibraryImport(GoLibrary, Data);
        Assert.Null(await import.RunAsync(library, TestPack.Division.Pack.ID, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(import.Marker));
        PlaceGoLibrary();
        Assert.Null(await import.RunAsync(library, TestPack.Division.Pack.ID, TestContext.Current.CancellationToken));
        Assert.Empty(await library.ListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Builds for another pack, and a file that isn't a library, are reported rather than imported.</summary>
    [Fact]
    public async Task WhatCantBeImportedIsReported()
    {
        PlaceGoLibrary();
        var library = new FileBuildLibrary(Path.Combine(Data, "library"));
        var other = await new GoLibraryImport(GoLibrary, Data).RunAsync(library, "another_pack", TestContext.Current.CancellationToken);
        Assert.Equal(0, other!.Imported);
        Assert.Equal(6, other.Skipped.Length);
        Assert.All(other.Skipped, line => Assert.Contains("another pack", line, StringComparison.Ordinal));

        await File.WriteAllTextAsync(GoLibrary, "not a library", TestContext.Current.CancellationToken);
        var broken = await new GoLibraryImport(GoLibrary, Path.Combine(folder, "fresh")).RunAsync(library, TestPack.Division.Pack.ID, TestContext.Current.CancellationToken);
        Assert.Equal(0, broken!.Imported);
        Assert.StartsWith("the library: ", Assert.Single(broken.Skipped), StringComparison.Ordinal);
        Assert.Equal("Imported 0 builds from the Go app's library (1 couldn't be imported)", broken.Summary);
    }
}
