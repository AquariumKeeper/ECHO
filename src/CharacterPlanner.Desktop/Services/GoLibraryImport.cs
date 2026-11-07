using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Desktop.Services;

/// <summary>What the Go library import did: how many builds it brought over, and the records it couldn't, with why.</summary>
public sealed record GoLibraryImportResult(int Imported, ImmutableArray<string> Skipped)
{
    /// <summary>A line for the status bar: "Imported 6 builds from the Go app's library (1 couldn't be read)".</summary>
    public string Summary =>
        $"Imported {Imported} build{(Imported == 1 ? "" : "s")} from the Go app's library" + (Skipped.IsEmpty ? "" : $" ({Skipped.Length} couldn't be imported)");
}

/// <summary>
/// The one-time import of the Go app's build library on first launch (ADR 0002, plan "Libraries", ledger F52). The Go
/// library is never written: it is copied into the app's data folder, and the copy is read. Each build of this pack
/// becomes a new record in the app's library, keeping its name and the pack version it was saved with. A marker file
/// records the import, so it runs once, whether or not a Go library was there.
/// </summary>
public sealed class GoLibraryImport(string source, string dataDirectory)
{
    /// <summary>The Go app's library: <c>character-planner/builds.json</c> in Go's user config folder (os.UserConfigDir).</summary>
    public static string DefaultSource
    {
        get
        {
            var config = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "character-planner", "builds.json");
        }
    }

    /// <summary>The record that the import ran, and what it did.</summary>
    public string Marker => Path.Combine(dataDirectory, "go-library-import.json");

    /// <summary>The copy of the Go library the import read.</summary>
    public string Copy => Path.Combine(dataDirectory, "go-library-copy.json");

    /// <summary>
    /// Imports the Go library unless that was done before; null when nothing was to do (already imported, or no Go
    /// library). A Go library that can't be read is reported as one skipped entry, and isn't tried again.
    /// </summary>
    public async Task<GoLibraryImportResult?> RunAsync(IBuildLibrary library, string packId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (File.Exists(Marker)) { return null; }
        Directory.CreateDirectory(dataDirectory);
        if (!File.Exists(source))
        {
            await WriteMarkerAsync(null, cancellationToken);
            return null;
        }
        File.Copy(source, Copy, overwrite: true);
        var skipped = ImmutableArray.CreateBuilder<string>();
        var imported = 0;
        try
        {
            foreach (var record in LegacyLibrary.Read(await File.ReadAllBytesAsync(Copy, cancellationToken)))
            {
                if (record.Import is not { } import) { skipped.Add($"{record.Id}: {record.Problem}"); continue; }
                if (import.Build.PackId != packId) { skipped.Add($"{record.Id}: it is for another pack ({import.Build.PackId})"); continue; }
                try
                {
                    await library.CreateAsync(import.Build, cancellationToken);
                    imported++;
                }
                catch (LibraryException exception) { skipped.Add($"{record.Id}: {exception.Message}"); }
            }
        }
        catch (LegacyDocumentException exception) { skipped.Add($"the library: {exception.Message}"); }
        var result = new GoLibraryImportResult(imported, skipped.ToImmutable());
        await WriteMarkerAsync(result, cancellationToken);
        return result;
    }

    private async Task WriteMarkerAsync(GoLibraryImportResult? result, CancellationToken cancellationToken)
    {
        var marker = new { importedAt = DateTimeOffset.UtcNow, source, found = result is not null, imported = result?.Imported ?? 0, skipped = result?.Skipped ?? [] };
        await File.WriteAllBytesAsync(Marker, JsonSerializer.SerializeToUtf8Bytes(marker, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }
}
