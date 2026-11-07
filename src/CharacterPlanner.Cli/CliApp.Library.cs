using System.Globalization;
using System.Text.Json;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Cli;

/// <summary>
/// The saved-build library the desktop uses (ADR 0004), shared through FileBuildLibrary's writer lock. Saving
/// never replaces a record silently: a new save gets a new id, and replace and delete name the version that
/// list or load returned, so an edit the desktop made in between is a conflict rather than lost.
/// </summary>
internal static partial class CliApp
{
    /// <summary>Options after the positional arguments, each at most once; null when the grammar is wrong.</summary>
    private static Dictionary<string, string>? NamedOptions(string[] rest, params string[] allowed)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Length; i += 2)
        {
            if (i + 1 >= rest.Length || !allowed.Contains(rest[i]) || !options.TryAdd(rest[i], rest[i + 1])) { return null; }
        }
        return options;
    }

    private static FileBuildLibrary OpenLibrary(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--library", out var directory)) { return new(AppDataPaths.Library); }
        if (directory.Length == 0) { throw new CliInputException("library-path-invalid", directory, "Library directory must not be empty"); }
        try { return new(Path.GetFullPath(directory)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new CliInputException("library-path-invalid", directory, exception.Message); }
    }

    private static async Task<int> LibraryAsync(Func<FileBuildLibrary, Task<object>> command, Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var library = OpenLibrary(options);
        object result;
        try { result = await command(library).ConfigureAwait(false); }
        // Another writer holding the library too long is a persistence failure; everything else is the caller's input.
        catch (LibraryException exception)
        { return Fail(output, [new { severity = "error", code = exception.Code, path = library.Directory, message = exception.Message }], exception.Code == LibraryErrorCodes.Busy ? IoFailure : InvalidInput); }
        output.WriteLine(JsonSerializer.Serialize(new { ok = true, result, diagnostics = Array.Empty<object>() }));
        return Success;
    }

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static Task<int> ListLibraryAsync(Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default) =>
        LibraryAsync(async library => new
        {
            directory = library.Directory,
            entries = (await library.ListAsync(cancellationToken).ConfigureAwait(false)).Select(entry => new
            {
                id = entry.Id,
                name = entry.Name,
                packId = entry.PackId,
                packVersion = entry.PackVersion,
                savedAt = entry.Problem is null || entry.SavedAt != default ? Time(entry.SavedAt) : null,
                version = entry.Version,
                problem = entry.Problem
            })
        }, options, output, cancellationToken);

    private static Task<int> LoadLibraryAsync(string id, Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default) =>
        LibraryAsync(async library =>
        {
            var loaded = await library.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(BuildDocument.Encode(loaded.Build));
            return new { id = loaded.Id, version = loaded.Version, savedAt = Time(loaded.SavedAt), document = document.RootElement.Clone() };
        }, options, output, cancellationToken);

    private static async Task<int> SaveLibraryAsync(string path, Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var replace = options.GetValueOrDefault("--replace");
        var expected = options.GetValueOrDefault("--expected-version");
        if ((replace is null) != (expected is null))
        { throw new CliInputException("library-replace-invalid", path, "--replace and --expected-version go together: name the record and the version list or load returned"); }
        BuildSnapshot build;
        try { build = BuildDocument.Decode(await ReadInputAsync(path, cancellationToken).ConfigureAwait(false)); }
        catch (BuildDocumentException exception) { throw new CliInputException("build-invalid", path, exception.Message); }
        return await LibraryAsync(async library =>
        {
            var saved = replace is null
                ? await library.CreateAsync(build, cancellationToken).ConfigureAwait(false)
                : await library.ReplaceAsync(replace, build, expected!, cancellationToken).ConfigureAwait(false);
            return new { id = saved.Id, version = saved.Version, savedAt = Time(saved.SavedAt), replaced = replace is not null };
        }, options, output, cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> DeleteLibraryAsync(string id, Dictionary<string, string> options, TextWriter output, CancellationToken cancellationToken = default) =>
        LibraryAsync(async library =>
        {
            await library.DeleteAsync(id, options["--expected-version"], cancellationToken).ConfigureAwait(false);
            return new { id, deleted = true };
        }, options, output, cancellationToken);
}
