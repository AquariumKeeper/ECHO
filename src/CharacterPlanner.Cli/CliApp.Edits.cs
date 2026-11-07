using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static async Task<int> ApplyBuildAsync(string directory, string path, string operationsPath, string? contextPath, TextWriter output, CancellationToken cancellationToken = default)
    {
        var validation = await LoadPackAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var build = await ReadBuildAsync(path, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        var applier = new OperationApplier(compiled);
        var initial = await ReadStateAsync(build, contextPath, applier, cancellationToken).ConfigureAwait(false);
        if (!initial.Succeeded) { return OperationFailure(output, contextPath ?? path, initial.Errors); }
        var parsed = OperationJson.Read(WithoutBom(await ReadInputAsync(operationsPath, cancellationToken).ConfigureAwait(false)));
        if (!parsed.Succeeded) { return OperationFailure(output, operationsPath, parsed.Errors); }
        var applied = applier.Apply(initial.State, parsed.Operations);
        if (!applied.Succeeded) { return OperationFailure(output, operationsPath, applied.Errors); }
        byte[] encoded;
        try { encoded = BuildDocument.Encode(applied.State.Build); }
        catch (ArgumentException exception)
        { return OperationFailure(output, operationsPath, [new(0, "", "build-unsaveable", exception.Message)]); }
        using var document = JsonDocument.Parse(encoded);
        var context = TypedContext(compiled.Pack, applied.State.Assumptions);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                document = document.RootElement,
                context,
                changes = applied.Changes.Select(change => new { operation = change.Operation, kind = change.Kind, set = change.Set, cleared = change.Cleared })
            },
            diagnostics = Diagnostics(validation.Diagnostics)
        }));
        return Success;
    }

    private static int OperationFailure(TextWriter output, string path, ImmutableArray<OperationError> errors)
    {
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            diagnostics = errors.Select(error => new { severity = "error", code = error.Code, path, message = error.Message }),
            operationErrors = errors.Select(error => new { operation = error.Operation, kind = error.Kind, code = error.Code, message = error.Message })
        }));
        return InvalidInput;
    }
}
