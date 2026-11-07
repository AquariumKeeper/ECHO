using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests;

public sealed class ArtworkManifestTests
{
    private static readonly GamePack Pack = new()
    {
        ID = "division_2",
        Version = "new-data-version",
        Slots = [new() { ID = "mask", Name = "Mask" }],
        Items = [new() { ID = "mask_a", Name = "Mask A", Slot = "mask" }, new() { ID = "mask_b", Name = "Mask B", Slot = "mask" }],
        Talents = [new() { ID = "talent", Name = "Talent" }],
        ActiveSkills = [new() { ID = "skill", Name = "Skill" }],
        Specializations = [new() { ID = "spec", Name = "Specialization", Nodes = [new() { ID = "node", Name = "Node" }] }]
    };

    private const string Valid = """
        {
          "schemaVersion":1,"id":"example.orange","name":"Orange · 橙","version":"2026-10-30",
          "author":"Example","gamePackId":"division_2",
          "credits":[{"author":"Example","license":"CC0-1.0","licenseFile":"licenses/LICENSE.txt"}],
          "assets":[{"id":"shared","path":"assets/mask.png","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],
          "bindings":{"icons":{"slots":{"mask":"shared"}}}
        }
        """;

    private static ArtworkManifestResult Read(string json) => ArtworkManifestReader.Read(Encoding.UTF8.GetBytes(json), Pack);
    private static JsonObject Manifest() => JsonNode.Parse(Valid)!.AsObject();

    [Theory]
    [InlineData("assets/Icons/a.png", "assets/icons/b.png", false)]
    [InlineData("assets/Icons/a.png", "assets/Icons/b.png", true)]
    [InlineData("assets/A/Sub/a.png", "assets/A/sub/b.png", false)]
    public void DirectorySpellingMustBeConsistentAcrossDifferentFiles(string first, string second, bool succeeds)
    {
        var json = Manifest();
        json["assets"]![0]!["path"] = first;
        var other = json["assets"]![0]!.DeepClone();
        other["id"] = "other";
        other["path"] = second;
        json["assets"]!.AsArray().Add(other);
        var result = Read(json.ToJsonString());
        Assert.Equal(succeeds, result.Succeeded);
        if (!succeeds) { Assert.Contains(result.Diagnostics, value => value.Code == "artwork-path-invalid"); }
    }

    [Fact]
    public void LicenseDirectoriesAlsoRequireConsistentCasing()
    {
        var json = Manifest();
        json["credits"]![0]!["licenseFile"] = "licenses/Notices/a.txt";
        var other = json["credits"]![0]!.DeepClone();
        other["licenseFile"] = "licenses/notices/b.txt";
        json["credits"]!.AsArray().Add(other);
        Assert.Contains(Read(json.ToJsonString()).Diagnostics, value => value.Code == "artwork-path-invalid");
    }

    [Fact]
    public void PartialPackUsesStableIdsRatherThanTheDataVersion()
    {
        var bytes = Encoding.UTF8.GetBytes(Valid);
        var result = ArtworkManifestReader.Read(new byte[] { 0xEF, 0xBB, 0xBF, }.Concat(bytes).ToArray(), Pack);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("Orange · 橙", result.Manifest!.Name);
        Assert.Equal("shared", result.ApplicableBindings!.Icons["slots"]["mask"]);
        Assert.Empty(result.ApplicableBindings.ItemArt);
        Assert.Empty(result.ApplicableBindings.SpecializationImages);
        Assert.True(ArtworkManifestReader.Read(bytes, Pack with { Version = "another-version" }).Succeeded);
    }

    [Fact]
    public void UnknownTargetsWarnAndAreExcludedWithoutDroppingValidBindings()
    {
        var json = Manifest();
        json["bindings"]!["icons"]!["slots"]!["removed_slot"] = "shared";
        var result = Read(json.ToJsonString());
        Assert.True(result.Succeeded);
        Assert.Equal("artwork-target-unknown", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("shared", result.Manifest!.Bindings.Icons["slots"]["removed_slot"]);
        Assert.Single(result.ApplicableBindings!.Icons["slots"]);
    }

    [Fact]
    public void UnknownTargetsStillRequireDeclaredAssets()
    {
        var json = Manifest();
        json["bindings"]!["icons"]!["slots"]!["removed_slot"] = "missing_asset";
        var result = Read(json.ToJsonString());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, problem => problem.Code == "artwork-asset-unknown");
        Assert.Null(result.Manifest);
        Assert.Null(result.ApplicableBindings);
    }

    [Fact]
    public void AllArtworkSurfacesAndSharedItemLayersAreSupported()
    {
        var json = Manifest();
        json["bindings"] = JsonNode.Parse("""
            {"icons":{"slots":{"mask":"shared"},"talents":{"talent":"shared"},"skills":{"skill":"shared"},
             "specializations":{"spec":"shared"},"nodes":{"node":"shared"}},
             "itemArt":{"mask_a":{"image":"shared"},"mask_b":{"icon":"shared"}},
             "specializationImages":{"spec":"shared"}}
            """);
        var result = Read(json.ToJsonString());
        Assert.True(result.Succeeded);
        Assert.Equal(5, result.ApplicableBindings!.Icons.Count);
        Assert.Null(result.ApplicableBindings.ItemArt["mask_a"].Icon);
        Assert.Null(result.ApplicableBindings.ItemArt["mask_b"].Image);
        Assert.Equal("shared", result.ApplicableBindings.SpecializationImages["spec"]);
        var serialized = JsonSerializer.Serialize(result.Manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(Read(serialized).Succeeded);
    }

    [Theory]
    [InlineData("schemaVersion", "2", "artwork-schema-unsupported")]
    [InlineData("gamePackId", "\"other\"", "artwork-game-mismatch")]
    [InlineData("id", "\"../escape\"", "artwork-id-invalid")]
    [InlineData("name", "\" \"", "artwork-text-invalid")]
    [InlineData("version", "\"\"", "artwork-text-invalid")]
    [InlineData("credits", "[]", "artwork-credits-missing")]
    [InlineData("assets", "[]", "artwork-assets-invalid")]
    [InlineData("bindings", "{}", "artwork-no-coverage")]
    public void InvalidFieldsRejectTheWholeManifest(string field, string value, string code)
    {
        var json = Manifest();
        json[field] = JsonNode.Parse(value);
        var result = Read(json.ToJsonString());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, problem => problem.Code == code);
        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"id":"duplicate","id":"again"}""")]
    public void NonObjectsAndMalformedJsonReturnDiagnostics(string json)
    {
        Assert.False(Read(json).Succeeded);
        Assert.Contains(Read(json).Diagnostics, problem => problem.Code == "artwork-manifest-invalid");
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"mask\":\"shared\"", "\"mask\":\"shared\",\"mask\":\"shared\"")]
    [InlineData("\"mask\":\"shared\"", "\"mask\":null")]
    [InlineData("\"slots\":{\"mask\":\"shared\"}", "\"slots\":null")]
    [InlineData("\"bindings\":", "\"extra\":1,\"bindings\":")]
    [InlineData("\"author\":\"Example\",\"gamePackId\"", "\"author\":null,\"gamePackId\"")]
    [InlineData("\"name\":\"Orange · 橙\"", "\"name\":\"bad\\uD800\"")]
    [InlineData("\"name\":\"Orange · 橙\"", "\"name\":\"bad\\nname\"")]
    public void StrictReadingRejectsDuplicateUnknownNullAndInvalidUnicodeValues(string before, string after)
    {
        Assert.Contains(before, Valid, StringComparison.Ordinal);
        Assert.False(Read(Valid.Replace(before, after, StringComparison.Ordinal)).Succeeded);
    }

    [Fact]
    public void DeclaredFilesMustHaveUniqueIdsPathsAndValidDigests()
    {
        var json = Manifest();
        var files = json["assets"]!.AsArray();
        var duplicate = files[0]!.DeepClone();
        duplicate["path"] = "assets/MASK.png";
        duplicate["sha256"] = "no";
        files.Add(duplicate);
        var result = Read(json.ToJsonString());
        Assert.Contains(result.Diagnostics, problem => problem.Code == "artwork-asset-id-invalid");
        Assert.Contains(result.Diagnostics, problem => problem.Code == "artwork-path-invalid");
        Assert.Contains(result.Diagnostics, problem => problem.Code == "artwork-hash-invalid");
    }

    [Fact]
    public void UnknownGroupsAndEntirelyUnknownTargetsAreRefused()
    {
        Assert.Contains(Read(Valid.Replace("\"slots\"", "\"brands\"", StringComparison.Ordinal)).Diagnostics, problem => problem.Code == "artwork-group-unknown");
        Assert.Contains(Read(Valid.Replace("\"mask\":", "\"absent\":", StringComparison.Ordinal)).Diagnostics, problem => problem.Code == "artwork-no-coverage");
    }

    [Fact]
    public void OversizedManifestIsRefusedBeforeParsing()
    {
        var result = ArtworkManifestReader.Read(new byte[ArtworkLimits.ManifestBytes + 1], Pack);
        Assert.Equal("artwork-manifest-too-large", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ExampleHasOnlyDeclaredFilesAndMatchingAssetHashes()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artwork-example");
        var result = ArtworkManifestReader.Read(File.ReadAllBytes(Path.Combine(root, "artwork.json")), Pack);
        Assert.True(result.Succeeded);
        var manifest = result.Manifest!;
        foreach (var asset in manifest.Assets)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, asset.Path));
            Assert.Equal(asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        }
        foreach (var credit in manifest.Credits) { Assert.NotEmpty(File.ReadAllText(Path.Combine(root, credit.LicenseFile))); }
        var declared = manifest.Assets.Select(file => file.Path).Concat(manifest.Credits.Select(credit => credit.LicenseFile)).Append("artwork.json").Order(StringComparer.Ordinal);
        Assert.Equal(declared, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("assets", "artwork-asset-invalid")]
    [InlineData("credits", "artwork-credit-invalid")]
    public void NullCollectionElementsAreDiagnostics(string field, string code)
    {
        var json = Manifest();
        json[field]!.AsArray().Add((JsonNode?)null);
        var result = Read(json.ToJsonString());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, problem => problem.Code == code);
    }

    [Fact]
    public void LicenseReferencesMayBeSharedButCannotDifferOnlyByCase()
    {
        var json = Manifest();
        var credit = json["credits"]![0]!.DeepClone();
        json["credits"]!.AsArray().Add(credit);
        Assert.True(Read(json.ToJsonString()).Succeeded);
        credit["licenseFile"] = "licenses/license.txt";
        Assert.Contains(Read(json.ToJsonString()).Diagnostics, problem => problem.Code == "artwork-path-invalid");
    }

    [Fact]
    public void ADeclaredFileCannotAlsoBeAnAssetDirectory()
    {
        var json = Manifest();
        var file = json["assets"]![0]!.DeepClone();
        file["id"] = "nested";
        file["path"] = "assets/mask.png/child.png";
        json["assets"]!.AsArray().Add(file);
        Assert.Contains(Read(json.ToJsonString()).Diagnostics, problem => problem.Code == "artwork-path-invalid");
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("id")]
    [InlineData("assets")]
    [InlineData("credits")]
    [InlineData("bindings")]
    public void RequiredManifestMembersCannotBeOmitted(string field)
    {
        var json = Manifest();
        json.Remove(field);
        Assert.Contains(Read(json.ToJsonString()).Diagnostics, problem => problem.Code == "artwork-manifest-invalid");
    }

    [Theory]
    [InlineData("assets/mask.png", true)]
    [InlineData("assets/nested/mask.png", true)]
    [InlineData("assets/../escape.png", false)]
    [InlineData("assets/./mask.png", false)]
    [InlineData("assets//mask.png", false)]
    [InlineData("/assets/mask.png", false)]
    [InlineData("assets\\mask.png", false)]
    [InlineData("C:/assets/mask.png", false)]
    [InlineData("assets/mask.png:stream", false)]
    [InlineData("assets/CON.png", false)]
    [InlineData("assets/lpt1.png", false)]
    [InlineData("assets/COM¹.png", false)]
    [InlineData("assets/folder./mask.png", false)]
    [InlineData("assets/folder /mask.png", false)]
    [InlineData("assets/control\n.png", false)]
    [InlineData("licenses/LICENSE.txt", false)]
    public void ArchivePathsHaveTheSameRulesOnEveryPlatform(string path, bool valid) => Assert.Equal(valid, ArtworkPaths.IsFile(path, "assets"));

    [Theory]
    [InlineData("example.orange", true)]
    [InlineData("sample_1", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("con", false)]
    [InlineData("Example", false)]
    [InlineData("trailing.", false)]
    public void PackAndAssetIdsArePortable(string id, bool valid) => Assert.Equal(valid, ArtworkPaths.IsIdentifier(id));
}
