using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Artwork;

/// <summary>Strict, bounded, filesystem-independent reading and compatibility checks for artwork manifest v1.</summary>
public static class ArtworkManifestReader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        MaxDepth = 32
    };

    public static ArtworkManifestResult Read(ReadOnlyMemory<byte> bytes, GamePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (bytes.Length > ArtworkLimits.ManifestBytes) { return Failed("artwork-manifest-too-large", "artwork.json", "Artwork manifest exceeds 1 MiB"); }
        if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { bytes = bytes[3..]; }
        try
        {
            var manifest = JsonSerializer.Deserialize<ArtworkManifest>(bytes.Span, Json);
            return manifest is null ? Failed("artwork-manifest-invalid", "artwork.json", "Artwork manifest must be an object") : Validate(manifest, pack);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or DecoderFallbackException)
        { return Failed("artwork-manifest-invalid", "artwork.json", "Invalid artwork manifest: " + exception.Message); }
    }

    private static ArtworkManifestResult Validate(ArtworkManifest manifest, GamePack pack)
    {
        var diagnostics = ImmutableArray.CreateBuilder<ArtworkDiagnostic>();
        void Error(string code, string path, string message) => diagnostics.Add(new("error", code, path, message));
        void Text(string value, string path, int limit, bool optional = false)
        {
            if (value.Length > limit || !optional && string.IsNullOrWhiteSpace(value) || !ArtworkPaths.WellFormed(value) || value.Any(char.IsControl))
            { Error("artwork-text-invalid", path, $"Expected {(optional ? "optional" : "nonempty")} Unicode text of at most {limit} characters without control characters"); }
        }
        if (manifest.SchemaVersion != 1) { Error("artwork-schema-unsupported", "schemaVersion", "Only artwork schema version 1 is supported"); }
        if (!ArtworkPaths.IsIdentifier(manifest.Id)) { Error("artwork-id-invalid", "id", "Expected a portable lowercase identifier of at most 64 characters"); }
        Text(manifest.Name, "name", 128);
        Text(manifest.Version, "version", 64);
        Text(manifest.Author, "author", 256);
        Text(manifest.Description, "description", 4096, optional: true);
        if (manifest.GamePackId != pack.ID) { Error("artwork-game-mismatch", "gamePackId", $"Expected game pack '{pack.ID}'"); }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "artwork.json" };
        var assets = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.Assets.Length == 0 || manifest.Assets.Length > ArtworkLimits.Files - 2)
        { Error("artwork-assets-invalid", "assets", "Expected at least one asset within the archive file limit"); }
        for (var index = 0; index < manifest.Assets.Length; index++)
        {
            var file = manifest.Assets[index];
            var path = $"assets[{index}]";
            if (file is null) { Error("artwork-asset-invalid", path, "Asset must be an object"); continue; }
            if (!ArtworkPaths.IsIdentifier(file.Id) || !assets.Add(file.Id)) { Error("artwork-asset-id-invalid", path + ".id", "Asset IDs must be valid and unique"); }
            if (!ArtworkPaths.WellFormed(file.Path) || !ArtworkPaths.IsFile(file.Path, "assets") || !file.Path.EndsWith(".png", StringComparison.Ordinal) || !paths.Add(file.Path))
            { Error("artwork-path-invalid", path + ".path", "Expected a unique portable assets/*.png path"); }
            if (file.Sha256.Length != 64 || !file.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            { Error("artwork-hash-invalid", path + ".sha256", "Expected a lowercase SHA-256 digest"); }
        }
        if (manifest.Credits.IsEmpty) { Error("artwork-credits-missing", "credits", "At least one author and license notice is required"); }
        var licenses = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Credits.Length; index++)
        {
            var credit = manifest.Credits[index];
            var path = $"credits[{index}]";
            if (credit is null) { Error("artwork-credit-invalid", path, "Credit must be an object"); continue; }
            Text(credit.Author, path + ".author", 256);
            Text(credit.License, path + ".license", 128);
            if (!ArtworkPaths.WellFormed(credit.LicenseFile) || !ArtworkPaths.IsFile(credit.LicenseFile, "licenses") || !credit.LicenseFile.EndsWith(".txt", StringComparison.Ordinal))
            { Error("artwork-path-invalid", path + ".licenseFile", "Expected a portable licenses/*.txt path"); }
            if (licenses.Add(credit.LicenseFile) && !paths.Add(credit.LicenseFile))
            { Error("artwork-path-invalid", path + ".licenseFile", "License paths must not collide by case"); }
        }
        if (assets.Count + licenses.Count + 1 > ArtworkLimits.Files) { Error("artwork-file-limit", "assets", "Too many declared files"); }
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (paths.Contains(path[..slash]))
                { Error("artwork-path-invalid", path, "A declared file cannot also be a parent directory"); break; }
            }
        }

        if (!ArtworkPaths.HasConsistentCasing(paths))
        { Error("artwork-path-invalid", "assets", "Directory and file paths must use consistent casing"); }

        // Null elements inside generic collections are not covered by RespectNullableAnnotations.
        void Binding(string target, string? asset, string path, HashSet<string> targets, Action accept)
        {
            if (asset is null || !assets.Contains(asset)) { Error("artwork-asset-unknown", path, "Binding must reference a declared asset ID"); return; }
            if (!ArtworkPaths.WellFormed(target) || target.Length is 0 or > 256 || target.Any(char.IsControl))
            { Error("artwork-target-invalid", path, "Expected a nonempty target ID of at most 256 characters without control characters"); return; }
            if (targets.Contains(target)) { accept(); }
            else { diagnostics.Add(new("warning", "artwork-target-unknown", path, $"Target '{target}' is absent from this game data and will be ignored")); }
        }
        var targetGroups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["slots"] = pack.Slots.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            ["talents"] = pack.Talents.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            ["skills"] = pack.ActiveSkills.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            ["specializations"] = pack.Specializations.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            ["nodes"] = pack.Specializations.SelectMany(value => value.Nodes).Select(value => value.ID).ToHashSet(StringComparer.Ordinal)
        };
        var icons = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (group, values) in manifest.Bindings.Icons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!targetGroups.TryGetValue(group, out var targets)) { Error("artwork-group-unknown", "bindings.icons." + group, "Unknown icon group"); continue; }
            if (values is null) { Error("artwork-bindings-invalid", "bindings.icons." + group, "Expected an object of bindings"); continue; }
            var applicable = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var (target, asset) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            { Binding(target, asset, $"bindings.icons.{group}.{target}", targets, () => applicable[target] = asset); }
            icons[group] = applicable.ToImmutable();
        }
        var items = ImmutableDictionary.CreateBuilder<string, ArtworkItemBinding>(StringComparer.Ordinal);
        var itemTargets = pack.Items.Select(value => value.ID).ToHashSet(StringComparer.Ordinal);
        foreach (var (target, value) in manifest.Bindings.ItemArt.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = "bindings.itemArt." + target;
            if (value is null || value.Image is null && value.Icon is null) { Error("artwork-item-invalid", path, "Expected at least one image or icon binding"); continue; }
            if (value.Image is not null) { Binding(target, value.Image, path + ".image", itemTargets, () => items[target] = value); }
            if (value.Icon is not null) { Binding(target, value.Icon, path + ".icon", itemTargets, () => items[target] = value); }
        }
        var specializations = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (target, asset) in manifest.Bindings.SpecializationImages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        { Binding(target, asset, "bindings.specializationImages." + target, targetGroups["specializations"], () => specializations[target] = asset); }
        if (items.Count + specializations.Count + icons.Values.Sum(values => values.Count) == 0)
        { Error("artwork-no-coverage", "bindings", "At least one binding must apply to the current game data"); }
        return diagnostics.Any(value => value.Severity == "error") ? new(null, null, diagnostics.ToImmutable()) :
            new(manifest, new() { Icons = icons.ToImmutable(), ItemArt = items.ToImmutable(), SpecializationImages = specializations.ToImmutable() }, diagnostics.ToImmutable());
    }

    private static ArtworkManifestResult Failed(string code, string path, string message) => new(null, null, [new("error", code, path, message)]);
}
