using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static int Describe(TextWriter output)
    {
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                contract = "d2p-cli-v1",
                commands = CommandGrammars,
                catalogKinds = CatalogKinds,
                operationKinds = OperationKinds.Supported,
                removedOperationKinds = OperationKinds.Removed,
                exitCodes = new { success = Success, usage = UsageError, invalidInput = InvalidInput, io = IoFailure, @internal = InternalFailure, staleRevision = StaleRevision, cancelled = Cancelled },
                buildSchemaVersion = BuildDocument.SchemaVersion,
                maxInputBytes = BuildDocument.MaxBytes
            },
            diagnostics = Array.Empty<object>()
        }));
        return Success;
    }

    private static async Task<int> NewBuildAsync(string directory, TextWriter output, CancellationToken cancellationToken = default)
    {
        var validation = await LoadPackAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        using var document = JsonDocument.Parse(EncodeDocument(BuildSnapshot.Create(compiled.Pack), directory));
        output.WriteLine(JsonSerializer.Serialize(new { ok = true, result = document.RootElement, diagnostics = Diagnostics(validation.Diagnostics) }));
        return Success;
    }

    private static async Task<int> ResolveBuildAsync(string directory, string path, string? contextPath, TextWriter output, CancellationToken cancellationToken = default)
    {
        var validation = await LoadPackAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var build = await ReadBuildAsync(path, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        var initialized = await ReadStateAsync(build, contextPath, new OperationApplier(compiled), cancellationToken).ConfigureAwait(false);
        if (!initialized.Succeeded) { return OperationFailure(output, contextPath ?? path, initialized.Errors); }
        var state = initialized.State;
        var resolved = BuildResolver.Resolve(compiled, state.Build, state.Assumptions);
        var diagnostics = Diagnostics(validation.Diagnostics).Concat(resolved.Diagnostics.Select(diagnostic =>
            new { severity = diagnostic.Severity, code = diagnostic.Code, path = diagnostic.Subject, message = diagnostic.Message }));
        if (resolved.Diagnostics.Any(diagnostic => diagnostic.Severity == "error")) { return Fail(output, diagnostics); }
        output.WriteLine(JsonSerializer.Serialize(new { ok = true, result = Resolution(compiled.Pack, resolved), diagnostics }));
        return Success;
    }

    /// <summary>A resolution as build resolve and session resolve report it.</summary>
    private static Dictionary<string, object> Resolution(GamePack pack, ResolvedBuild resolved) => new(StringComparer.Ordinal)
    {
        ["pack"] = new { id = pack.ID, version = pack.Version },
        ["stats"] = resolved.Calculation.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        ["skillPreviews"] = resolved.ActiveSkillPreviews.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
            pair => new { skillId = pair.Value.SkillId, outputs = pair.Value.Outputs.Select(value => new { id = value.Id, name = value.Name, format = value.Format, kind = value.Kind, value = value.Value }) }),
        ["buildOutputs"] = resolved.BuildOutputs.Select(value => new { id = value.Id, name = value.Name, format = value.Format, value = value.Value }),
        ["assumptions"] = new
        {
            @explicit = TypedContext(pack, resolved.Assumptions.Explicit),
            numeric = resolved.Assumptions.Numeric.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        },
        ["readAssumptions"] = resolved.ReadAssumptions.Order(StringComparer.Ordinal)
    };

    private static async Task<ImmutableArray<BuildOperation>> ReadContextAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = WithoutBom(await ReadInputAsync(path, cancellationToken).ConfigureAwait(false));
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            { throw new CliInputException("context-invalid", path, "Context must be a JSON object of numeric values or selector strings"); }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var operations = ImmutableArray.CreateBuilder<BuildOperation>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new CliInputException("context-invalid", path, $"Context repeats '{property.Name}'"); }
                operations.Add(property.Value.ValueKind switch
                {
                    JsonValueKind.String => new ContextOperation(property.Name, Text: property.Value.GetString()!),
                    JsonValueKind.Number when property.Value.TryGetDouble(out var number) && double.IsFinite(number) => new ContextOperation(property.Name, Value: number),
                    _ => throw new CliInputException("context-invalid", path, $"Context '{property.Name}' needs a finite number or selector string")
                });
            }
            return operations.ToImmutable();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw new CliInputException("context-invalid", path, "Invalid context JSON: " + exception.Message); }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadInputAsync(string path, CancellationToken cancellationToken = default)
    {
        if (path.Length == 0) { throw new CliInputException("input-path-invalid", path, "Input path must not be empty"); }
        using var stream = File.OpenRead(path);
        return await ReadBoundedAsync(stream, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Raw bytes up to BuildDocument.MaxBytes, from a file or standard input alike.</summary>
    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(Stream stream, string path, CancellationToken cancellationToken = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > BuildDocument.MaxBytes)
            { throw new CliInputException("input-too-large", path, $"Input exceeds {BuildDocument.MaxBytes} bytes"); }
            bytes.Write(buffer, 0, count);
        }
        return bytes.TryGetBuffer(out var contents) ? contents.AsMemory(0, (int)bytes.Length) : throw new InvalidOperationException("Input buffer is unavailable");
    }

    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> bytes) =>
        bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;

    private static Dictionary<string, object> TypedContext(GamePack pack, IEnumerable<KeyValuePair<string, string>> assumptions)
    {
        var numeric = pack.NumericContextInputs.Select(input => input.ID).ToHashSet(StringComparer.Ordinal);
        return assumptions.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
            pair => numeric.Contains(pair.Key) ? (object)double.Parse(pair.Value, CultureInfo.InvariantCulture) : pair.Value, StringComparer.Ordinal);
    }

    private static byte[] EncodeDocument(BuildSnapshot build, string path)
    {
        try { return BuildDocument.Encode(build); }
        catch (ArgumentException exception) { throw new CliInputException("build-unsaveable", path, exception.Message); }
    }

    private static async Task<BuildSnapshot> ReadBuildAsync(string path, string packId, CancellationToken cancellationToken = default)
    {
        BuildSnapshot build;
        try { build = BuildDocument.Decode(await ReadInputAsync(path, cancellationToken).ConfigureAwait(false)); }
        catch (BuildDocumentException exception) { throw new CliInputException("build-invalid", path, exception.Message); }
        if (build.PackId != packId) { throw new CliInputException("build-wrong-pack", path, $"Build belongs to pack '{build.PackId}', not '{packId}'"); }
        return build;
    }

    private static async Task<OperationOutcome> ReadStateAsync(BuildSnapshot build, string? contextPath, IOperationApplier applier, CancellationToken cancellationToken = default) =>
        contextPath is null ? new(PlannerState.Of(build), [], []) : applier.Apply(PlannerState.Of(build), await ReadContextAsync(contextPath, cancellationToken).ConfigureAwait(false));
}
