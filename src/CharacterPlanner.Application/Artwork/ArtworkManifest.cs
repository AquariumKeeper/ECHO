using System.Collections.Immutable;

namespace CharacterPlanner.Application.Artwork;

/// <summary>The independent appearance-only contract carried by artwork.json in an artwork ZIP.</summary>
public sealed record ArtworkManifest
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string GamePackId { get; init; }
    public string Description { get; init; } = "";
    public required ImmutableArray<ArtworkCredit> Credits { get; init; }
    public required ImmutableArray<ArtworkFile> Assets { get; init; }
    public required ArtworkBindings Bindings { get; init; }
}

public sealed record ArtworkCredit
{
    public required string Author { get; init; }
    public required string License { get; init; }
    public required string LicenseFile { get; init; }
}

public sealed record ArtworkFile
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ArtworkItemBinding
{
    public string? Image { get; init; }
    public string? Icon { get; init; }
}

public sealed record ArtworkBindings
{
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> Icons { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
    public ImmutableDictionary<string, ArtworkItemBinding> ItemArt { get; init; } = ImmutableDictionary<string, ArtworkItemBinding>.Empty;
    public ImmutableDictionary<string, string> SpecializationImages { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed record ArtworkDiagnostic(string Severity, string Code, string Path, string Message);

/// <summary>Successful reads retain authored bindings and separately expose only bindings applicable to the current game data.</summary>
public sealed record ArtworkManifestResult(ArtworkManifest? Manifest, ArtworkBindings? ApplicableBindings, ImmutableArray<ArtworkDiagnostic> Diagnostics)
{
    public bool Succeeded => Manifest is not null && ApplicableBindings is not null;
}

public static class ArtworkLimits
{
    public const int ManifestBytes = 1024 * 1024;
    public const int ArchiveBytes = 128 * 1024 * 1024;
    public const int ExpandedBytes = 256 * 1024 * 1024;
    public const int Files = 4096;
    public const int ImageBytes = 8 * 1024 * 1024;
    public const int ImageDimension = 4096;
    public const int ImagePixels = 16_777_216;
    public const int TotalImagePixels = 64_000_000;
    public const int LicenseBytes = 256 * 1024;
}
