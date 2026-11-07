using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Artwork;

public sealed record ArtworkPackReference(string Id, string ContentDigest);

/// <summary>Pins a validated revision while views/caches use its files. Dispose after rebinding those views.</summary>
public interface IArtworkPackLease : IDisposable
{
    InstalledArtworkPack Pack { get; }
    Task<string> ReadNoticeAsync(string licenseFile, CancellationToken cancellationToken = default);
}

public sealed record ArtworkRevisionState(IArtworkPackLease? Active, ImmutableArray<ArtworkDiagnostic> Diagnostics) : IDisposable
{
    public bool Succeeded => Active is not null;
    public void Dispose() => Active?.Dispose();
}

public sealed record ArtworkSelectionState(long Revision, ArtworkPackReference? Selected, IArtworkPackLease? Active,
    ImmutableArray<ArtworkDiagnostic> Diagnostics) : IDisposable
{
    public bool UsesBundled => Active is null;
    public void Dispose() => Active?.Dispose();
}

public sealed record ArtworkCatalogEntry(ArtworkPackReference Reference, InstalledArtworkPack? Pack, ImmutableArray<ArtworkDiagnostic> Diagnostics)
{
    public bool Usable => Pack is not null;
}

public sealed record ArtworkCatalog(long SelectionRevision, ImmutableArray<ArtworkCatalogEntry> Entries, ImmutableArray<ArtworkDiagnostic> Diagnostics);
public sealed record ArtworkStoreChange(bool Succeeded, bool Changed, long SelectionRevision, ImmutableArray<ArtworkDiagnostic> Diagnostics);

public interface IArtworkPackStore
{
    Task<ArtworkCatalog> ListAsync(GamePack pack, CancellationToken cancellationToken = default);
    Task<ArtworkSelectionState> LoadSelectionAsync(GamePack pack, CancellationToken cancellationToken = default);
    Task<ArtworkRevisionState> OpenRevisionAsync(GamePack pack, ArtworkPackReference reference, CancellationToken cancellationToken = default);
    Task<ArtworkStoreChange> SelectAsync(GamePack pack, ArtworkPackReference? reference, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ArtworkStoreChange> RemoveAsync(ArtworkPackReference reference, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ArtworkStoreChange> ResetInvalidSettingsAsync(CancellationToken cancellationToken = default);
}
