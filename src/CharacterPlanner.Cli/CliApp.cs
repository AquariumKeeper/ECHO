using System.Reflection;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Diagnostics;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;
using CharacterPlanner.Legacy.Documents;
using PackJsonV1 = CharacterPlanner.Legacy.PackV1.PackJson;
using PackV1Converter = CharacterPlanner.Legacy.PackV1.PackV1Converter;

namespace CharacterPlanner.Cli;

/// <summary>
/// The CLI's commands, with the console passed in so tests can run them in process.
/// C10 starts with command discovery and read-only build creation/resolution alongside
/// pack delivery checks and one-way share conversion. See docs/CLI.md for the current surface.
/// Exit codes: 0 success, 2 usage, 3 invalid input or a failed check, 4 I/O failure or no session, 5 internal failure,
/// 6 a stale live-session revision.
/// </summary>
internal static partial class CliApp
{
    private static readonly string[] CommandGrammars = { "version", "describe", "pack validate <directory>", "pack convert-v1 <v1.json> <output-directory> [--provenance <file>] [--force]",
                    "share convert <pack-directory> <code>", "build new <pack-directory>", "build resolve <pack-directory> <build.json> [--context <context.json>]",
                    "catalog list <pack-directory> <kind>", "catalog show <pack-directory> <kind> <id>",
                    "build apply <pack-directory> <build.json> <operations.json> [--context <context.json>]",
                    "build simulate <pack-directory> <build.json> [--options <options.json>] [--context <context.json>]",
                    "build share <pack-directory> <build.json>", "build compare <pack-directory> <a.json> <b.json> [--context <context.json>]",
                    "build rank <pack-directory> <build.json> <slot> <stat> [--context <context.json>]",
                    "library list [--library <directory>]", "library load <id> [--library <directory>]",
                    "library save <build.json> [--replace <id> --expected-version <version>] [--library <directory>]",
                    "library delete <id> --expected-version <version> [--library <directory>]",
                    "session list [--session-dir <directory>]",
                    "session inspect [--session <id>] [--wait <milliseconds> [--since-revision <revision>]] [--session-dir <directory>]",
                    "session history [--session <id>] [--session-dir <directory>]", "session resolve [--session <id>] [--session-dir <directory>]",
                    "session apply --expected-revision <revision> --operations <operations.json|-> [--session <id>] [--session-dir <directory>]",
                    "session undo --expected-revision <revision> [--session <id>] [--session-dir <directory>]",
                    "session redo --expected-revision <revision> [--session <id>] [--session-dir <directory>]" };

    public const int Success = 0;
    public const int UsageError = 2;
    public const int InvalidInput = 3;
    public const int IoFailure = 4;
    public const int InternalFailure = 5;
    /// <summary>Interrupted by Ctrl+C (the conventional 128 + SIGINT).</summary>
    public const int Cancelled = 130;

    private sealed class CliInputException(string code, string path, string message, int exitCode = InvalidInput) : Exception(message)
    {
        public string Code { get; } = code;
        public string Path { get; } = path;
        public int ExitCode { get; } = exitCode;
    }

    /// <param name="input">Standard input as bytes, so piped JSON is UTF-8 whatever the console code page.</param>
    /// <param name="cancellationToken">Cancels long work; the run then answers with code <c>cancelled</c> and exit 130.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Stream? input = null, CancellationToken cancellationToken = default)
    {
        input ??= Stream.Null;
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return args switch
            {
                ["version"] or ["--json", "version"] => Version(output),
                ["describe"] => Describe(output),
                ["catalog", "list", var directory, var kind] => await CatalogAsync(directory, kind, null, output, cancellationToken).ConfigureAwait(false),
                ["catalog", "show", var directory, var kind, var id] => await CatalogAsync(directory, kind, id, output, cancellationToken).ConfigureAwait(false),
                ["build", "new", var directory] => await NewBuildAsync(directory, output, cancellationToken).ConfigureAwait(false),
                ["build", "resolve", var directory, var document] => await ResolveBuildAsync(directory, document, null, output, cancellationToken).ConfigureAwait(false),
                ["build", "resolve", var directory, var document, "--context", var context] => await ResolveBuildAsync(directory, document, context, output, cancellationToken).ConfigureAwait(false),
                ["build", "apply", var directory, var document, var operations] => await ApplyBuildAsync(directory, document, operations, null, output, cancellationToken).ConfigureAwait(false),
                ["build", "apply", var directory, var document, var operations, "--context", var context] => await ApplyBuildAsync(directory, document, operations, context, output, cancellationToken).ConfigureAwait(false),
                ["build", "simulate", var directory, var document, .. var rest] when SimulateArguments(rest) is { } simulate =>
                    await SimulateBuildAsync(directory, document, simulate.Options, simulate.Context, output, cancellationToken).ConfigureAwait(false),
                ["build", "share", var directory, var document] => await ShareBuildAsync(directory, document, output, cancellationToken).ConfigureAwait(false),
                ["build", "compare", var directory, var first, var second, .. var rest] when ContextOption(rest, out var context) =>
                    await CompareBuildsAsync(directory, first, second, context, output, cancellationToken).ConfigureAwait(false),
                ["build", "rank", var directory, var document, var slot, var stat, .. var rest] when ContextOption(rest, out var context) =>
                    await RankUpgradesAsync(directory, document, slot, stat, context, output, cancellationToken).ConfigureAwait(false),
                ["library", "list", .. var rest] when NamedOptions(rest, "--library") is { } listed => await ListLibraryAsync(listed, output, cancellationToken).ConfigureAwait(false),
                ["library", "load", var id, .. var rest] when NamedOptions(rest, "--library") is { } loaded => await LoadLibraryAsync(id, loaded, output, cancellationToken).ConfigureAwait(false),
                ["library", "save", var document, .. var rest] when NamedOptions(rest, "--library", "--replace", "--expected-version") is { } saved =>
                    await SaveLibraryAsync(document, saved, output, cancellationToken).ConfigureAwait(false),
                ["library", "delete", var id, .. var rest] when NamedOptions(rest, "--library", "--expected-version") is { } deleted && deleted.ContainsKey("--expected-version") =>
                    await DeleteLibraryAsync(id, deleted, output, cancellationToken).ConfigureAwait(false),
                ["session", "list", .. var rest] when NamedOptions(rest, "--session-dir") is { } listed => await ListSessionsAsync(listed, output, cancellationToken).ConfigureAwait(false),
                ["session", "inspect", .. var rest] when NamedOptions(rest, "--session", "--session-dir", "--wait", "--since-revision") is { } inspected =>
                    await InspectSessionAsync(inspected, output, cancellationToken).ConfigureAwait(false),
                ["session", "history", .. var rest] when NamedOptions(rest, "--session", "--session-dir") is { } history => await SessionHistoryAsync(history, output, cancellationToken).ConfigureAwait(false),
                ["session", "resolve", .. var rest] when NamedOptions(rest, "--session", "--session-dir") is { } resolved => await ResolveSessionAsync(resolved, output, cancellationToken).ConfigureAwait(false),
                ["session", "apply", .. var rest] when NamedOptions(rest, "--session", "--session-dir", "--expected-revision", "--operations") is { } applied &&
                    applied.ContainsKey("--expected-revision") && applied.ContainsKey("--operations") =>
                    await ChangeSessionAsync(Application.Sessions.SessionRequests.Apply, applied, input, output, cancellationToken).ConfigureAwait(false),
                ["session", "undo" or "redo", .. var rest] when NamedOptions(rest, "--session", "--session-dir", "--expected-revision") is { } moved && moved.ContainsKey("--expected-revision") =>
                    await ChangeSessionAsync(args[1], moved, input, output, cancellationToken).ConfigureAwait(false),
                ["pack", "validate", var directory] => await ValidateAsync(directory, output, cancellationToken).ConfigureAwait(false),
                ["pack", "convert-v1", var source, var target, .. var options] when ConvertOptions(options) is { } parsed =>
                    ConvertV1(source, target, parsed.Provenance, parsed.Force, output),
                ["share", "convert", var directory, var code] => await ShareConvertAsync(directory, code, output, cancellationToken).ConfigureAwait(false),
                _ => Usage(args, output, error)
            };
        }
        // Cancelling can also surface as a torn-down pipe or file read.
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or IOException or InvalidDataException)
        {
            output.WriteLine(JsonSerializer.Serialize(new { ok = false, diagnostics = new[] { new { severity = "error", code = "cancelled", path = "", message = "The command was cancelled" } } }));
            return Cancelled;
        }
        catch (CliInputException exception)
        {
            return Fail(output, [new { severity = "error", code = exception.Code, path = exception.Path, message = exception.Message }], exception.ExitCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            output.WriteLine(JsonSerializer.Serialize(new { ok = false, diagnostics = new[] { new { severity = "error", code = "io", path = "", message = exception.Message } } }));
            return IoFailure;
        }
        // A broken invariant still answers in JSON rather than crashing the process.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            output.WriteLine(JsonSerializer.Serialize(new { ok = false, diagnostics = new[] { new { severity = "error", code = "internal", path = "", message = exception.GetType().Name + ": " + exception.Message } } }));
            return InternalFailure;
        }
    }

    private static int Version(TextWriter output)
    {
        // Directory.Build.props owns the version; source revision suffixes are disabled there.
        var version = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        output.WriteLine(JsonSerializer.Serialize(new { ok = true, result = new { version }, diagnostics = Array.Empty<object>() }));
        return Success;
    }

    private static async Task<int> ValidateAsync(string directory, TextWriter output, CancellationToken cancellationToken = default)
    {
        var loaded = await PackFileLoader.LoadAsync(PackDirectory(directory), cancellationToken).ConfigureAwait(false);
        var ok = loaded.Validation.Compiled is not null;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok,
            result = new { directory = loaded.Directory, id = loaded.Manifest?.ID, version = loaded.Manifest?.Version, manifestSha256 = loaded.ManifestSha256, artworkWithheld = loaded.ArtworkWithheld },
            diagnostics = Diagnostics(loaded.Validation.Diagnostics)
        }));
        return ok ? Success : InvalidInput;
    }

    private static (string? Provenance, bool Force)? ConvertOptions(string[] options)
    {
        string? provenance = null;
        var force = false;
        for (var i = 0; i < options.Length; i++)
        {
            if (options[i] == "--force" && !force) { force = true; }
            else if (options[i] == "--provenance" && provenance is null && i + 1 < options.Length) { provenance = options[++i]; }
            else { return null; }
        }
        return (provenance, force);
    }

    // One-way migration of a Go-era pack. The converter rejects anything v2 cannot express,
    // and the output must pass v2 validation before any file is written.
    private static int ConvertV1(string source, string output, string? provenance, bool force, TextWriter console)
    {
        GamePack pack;
        try
        {
            var conversion = PackV1Converter.Convert(PackJsonV1.Deserialize(File.ReadAllBytes(source)));
            if (conversion.Pack is null)
            { return Fail(console, conversion.Problems.Select(problem => new { severity = "error", code = "conversion", path = problem.Path, message = problem.Message })); }
            pack = conversion.Pack;
        }
        catch (JsonException exception) { return Fail(console, [new { severity = "error", code = "v1-schema", path = "(root)", message = exception.Message }]); }
        var validation = PackValidator.Validate(pack);
        if (validation.Compiled is null) { return Fail(console, Diagnostics(validation.Diagnostics)); }

        var target = Path.GetFullPath(output);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !force)
        { return Fail(console, [new { severity = "error", code = "output-not-empty", path = target, message = "The output directory is not empty; pass --force to replace it" }]); }
        // An explicit nullable: a bare null would convert through byte[] to an empty, non-null provenance.
        ReadOnlyMemory<byte>? provenanceBytes = provenance is null ? (ReadOnlyMemory<byte>?)null : File.ReadAllBytes(provenance);
        var files = PackWriter.Write(pack, new() { Provenance = provenanceBytes });
        // Write a sibling directory, then swap it in, so a failure never leaves a half-written pack.
        var staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, bytes) in files) { File.WriteAllBytes(Path.Combine(staging, name), bytes.ToArray()); }
            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
        console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = new { output = target, files = files.Keys }, diagnostics = Diagnostics(validation.Diagnostics) }));
        return Success;
    }

    // Reads a code in either format and writes it in the app's own. Only the pack's structure is read
    // (its id and legacy slot order), so a pack whose artwork hasn't been delivered still works.
    private static async Task<int> ShareConvertAsync(string directory, string code, TextWriter output, CancellationToken cancellationToken = default)
    {
        var read = await PackFileLoader.ReadAsync(PackDirectory(directory), cancellationToken).ConfigureAwait(false);
        if (read.Pack is null) { return Fail(output, Diagnostics(read.Diagnostics)); }
        ShareCodeImport import;
        try { import = ShareCodeReader.Read(code, read.Pack.LegacyShareOrder); }
        catch (ShareCodeException exception) { return Fail(output, [new { severity = "error", code = "share-code-invalid", path = "", message = exception.Message }]); }
        if (import.Build.PackId != read.Pack.ID)
        {
            return Fail(output, [new { severity = "error", code = "share-code-wrong-pack", path = "",
                message = $"The share code is for pack \"{import.Build.PackId}\", not \"{read.Pack.ID}\"" }]);
        }
        string converted;
        try { converted = ShareCode.Encode(import.Build); }
        catch (ArgumentException exception) { return Fail(output, [new { severity = "error", code = "share-code-unshareable", path = "", message = exception.Message }]); }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                code = converted,
                format = import.Legacy ? "legacy" : "app",
                name = import.Build.Meta.Name,
                packVersion = import.Build.PackVersion,
                droppedFields = import.DroppedFields
            },
            diagnostics = Array.Empty<object>()
        }));
        return Success;
    }

    private static IEnumerable<object> Diagnostics(IEnumerable<PackDiagnostic> diagnostics) =>
        diagnostics.Select(diagnostic => new { severity = diagnostic.Severity, code = diagnostic.Code, path = diagnostic.Path, message = diagnostic.Message });

    private static int Fail(TextWriter output, IEnumerable<object> diagnostics, int exitCode = InvalidInput)
    {
        output.WriteLine(JsonSerializer.Serialize(new { ok = false, diagnostics }));
        return exitCode;
    }

    /// <summary>
    /// Grammar errors answer like every other failure: JSON on stdout (code <c>invalid-command</c>, with the grammars of
    /// the command group that was attempted), and the full usage on stderr for people.
    /// </summary>
    private static int Usage(string[] args, TextWriter output, TextWriter error)
    {
        static string[] Matching(string prefix) =>
            [.. CommandGrammars.Where(grammar => grammar == prefix || grammar.StartsWith(prefix + " ", StringComparison.Ordinal))];
        var command = args.Length >= 2 ? Matching(args[0] + " " + args[1]) : [];
        var group = args.Length >= 1 ? Matching(args[0]) : [];
        var message = args.Length == 0 ? "A command is required"
            : group.Length == 0 ? $"Unknown command '{args[0]}'"
            : command.Length > 0 || args.Length == 1 ? $"Invalid arguments for '{string.Join(' ', args.Take(Math.Min(2, args.Length)))}'"
            : $"Unknown command '{args[0]} {args[1]}'";
        group = command.Length > 0 ? command : group;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            diagnostics = new[] { new { severity = "error", code = "invalid-command", path = "", message } },
            usage = group.Length == 0 ? CommandGrammars : group
        }));
        error.WriteLine("Usage: CharacterPlanner.Cli " + string.Join(" | ", CommandGrammars));
        return UsageError;
    }
}
