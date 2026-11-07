using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Artwork;

public sealed record ArtworkImageSize(int Width, int Height);

/// <summary>Fully decode one preflighted PNG without retaining it. Null means it cannot be decoded.</summary>
public interface IArtworkImageDecoder
{
    ValueTask<ArtworkImageSize?> DecodeAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken);
}

public sealed record InstalledArtworkPack(ArtworkManifest Manifest, ArtworkBindings ApplicableBindings, string Directory, string ContentDigest);
public sealed record ArtworkInstallProgress(int CompletedFiles, int TotalFiles);

/// <summary>Installation does not activate artwork or change a planner session.</summary>
public sealed record ArtworkInstallResult(InstalledArtworkPack? Installed, bool AlreadyInstalled, ImmutableArray<ArtworkDiagnostic> Diagnostics)
{
    public bool Succeeded => Installed is not null;
}

public interface IArtworkPackInstaller
{
    /// <summary>Cancellation propagates before publication; a published revision returns success.</summary>
    Task<ArtworkInstallResult> InstallAsync(string archivePath, GamePack pack, IProgress<ArtworkInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}
