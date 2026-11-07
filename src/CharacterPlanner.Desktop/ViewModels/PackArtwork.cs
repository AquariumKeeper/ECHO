using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Application.Artwork;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// An item's art: its image (the item itself) and its icon (a brand or set logo), either of which may be absent, and the
/// app's own symbol (a slot id or an <see cref="ArtGlyphs"/> kind) drawn when neither can be shown.
/// </summary>
public sealed record ItemArt(string? Image, string? Icon, string? Glyph = null)
{
    public static readonly ItemArt None = new(null, null);
    public bool HasImage => Image is not null;
    public bool IsEmpty => Image is null && Icon is null && Glyph is null;
}

/// <summary>The kinds of choice the app draws its own symbol for (<c>Views.Glyphs</c>); slots use their ids.</summary>
public static class ArtGlyphs
{
    public const string Talent = "talent";
    public const string Skill = "skill";
    public const string Specialization = "specialization";
    public const string Node = "node";
    public const string Upgrades = "upgrades";
    public const string Item = "item";
}

/// <summary>
/// Where the pack's artwork is: asset ids to image files under the pack directory, and the presentation's lookups (an
/// item's art, an icon by group, a specialization's image). The loader has already checked every asset against its
/// hash; a view shows a file and falls back to the app's own symbol when it can't. Without a directory there is no art:
/// composition passes none when a distribution's loader reports the artwork withheld.
/// </summary>
public sealed class PackArtwork
{
    public static readonly PackArtwork None = new(new PackPresentation(), null);

    private readonly PackPresentation presentation;
    private readonly ImmutableDictionary<string, string> files;
    private readonly InstalledArtworkPack? overlay;
    private readonly ImmutableDictionary<string, string> overlayFiles = ImmutableDictionary<string, string>.Empty;

    public PackArtwork(PackArtwork bundled, InstalledArtworkPack overlay)
    {
        presentation = bundled.presentation;
        files = bundled.files;
        this.overlay = overlay;
        overlayFiles = overlay.Manifest.Assets.ToImmutableDictionary(asset => asset.Id,
            asset => Path.Combine(overlay.Directory, asset.Path), StringComparer.Ordinal);
    }

    private string? OverlayAsset(string? id) => id is not null && overlayFiles.TryGetValue(id, out var path) ? path : null;

    public PackArtwork(PackPresentation presentation, string? directory)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        this.presentation = presentation;
        if (directory is null) { files = ImmutableDictionary<string, string>.Empty; return; }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        // An asset path stays inside the pack directory, as the Go app required.
        files = presentation.ArtworkAssets
            .Select(asset => (asset.ID, Path: Path.GetFullPath(Path.Combine(root, asset.Path))))
            .Where(asset => asset.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .GroupBy(asset => asset.ID, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First().Path, StringComparer.Ordinal);
    }

    /// <summary>Whether the pack has any art, so views can keep room for it.</summary>
    public bool HasArtwork => !files.IsEmpty || !overlayFiles.IsEmpty;

    /// <summary>An asset's file, or null when the id is empty or unknown.</summary>
    public string? Asset(string? id) => id is { Length: > 0 } && files.TryGetValue(id, out var path) ? path : null;

    /// <summary>An icon by group ("slots", "talents", "skills", "specializations", "nodes") and object id.</summary>
    public string? Icon(string group, string id) => OverlayAsset(overlay?.ApplicableBindings.Icons.GetValueOrDefault(group)?.GetValueOrDefault(id)) ??
        Asset(presentation.Icons.GetValueOrDefault(group)?.GetValueOrDefault(id));

    /// <summary>An item's art layers; with neither, the slot's icon stands in as its image, and the slot's own symbol behind both.</summary>
    public ItemArt Item(string itemId, string slotId)
    {
        var art = presentation.ItemArt.GetValueOrDefault(itemId);
        var replacement = overlay?.ApplicableBindings.ItemArt.GetValueOrDefault(itemId);
        var layers = new ItemArt(OverlayAsset(replacement?.Image) ?? Asset(art?.Image), OverlayAsset(replacement?.Icon) ?? Asset(art?.Icon), slotId);
        return layers is { Image: null, Icon: null } ? layers with { Image = Icon("slots", slotId) } : layers;
    }

    public string? SpecializationImage(string id) => OverlayAsset(overlay?.ApplicableBindings.SpecializationImages.GetValueOrDefault(id)) ??
        Asset(presentation.SpecializationImages.GetValueOrDefault(id));
}
