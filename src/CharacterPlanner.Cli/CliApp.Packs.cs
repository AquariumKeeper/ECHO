using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static async Task<PackValidationResult> LoadPackAsync(string directory, bool metadataOnly = false, CancellationToken cancellationToken = default)
    {
        var path = PackDirectory(directory);
        return metadataOnly
            ? PackValidator.Validate(await PackFileLoader.ReadAsync(path, cancellationToken).ConfigureAwait(false))
            : (await PackFileLoader.LoadAsync(path, cancellationToken).ConfigureAwait(false)).Validation;
    }

    private static string PackDirectory(string directory)
    {
        if (directory.Length == 0) { throw new CliInputException("pack-path-invalid", directory, "Pack directory must not be empty"); }
        try { return Path.GetFullPath(directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new CliInputException("pack-path-invalid", directory, exception.Message); }
    }
}
