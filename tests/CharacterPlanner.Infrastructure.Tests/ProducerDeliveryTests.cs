using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CharacterPlanner.Infrastructure.Tests;

public sealed class ProducerDeliveryTests
{
    private static readonly string Data = typeof(ProducerDeliveryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "CommittedDataRoot").Value!;
    private static string Pack => Path.Combine(Data, "packs", "division-2");
    private static string Delivery => Path.Combine(Data, "deliveries", "division-2");

    // The assembly's source evidence (the Apex talent sources extracted from the game, with sidecars) stays in the data project; only its hashes are pinned here, through assembly.json and the
    // evidence packet.
    private const string Evidence = "support/evidence/";

    [Theory]
    [InlineData("IN_GAME_CHECKLIST.md")]
    [InlineData("CLEAN_MACHINE_CHECK.md")]
    public void ReleaseDocsNameTheBundledPack(string document)
    {
        // These ship or guide a release, and by hand they drift a candidate at a time.
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Pack, "manifest.json")));
        var candidate = string.Join('.', manifest.RootElement.GetProperty("version").GetString()!.Split('.')[^3..]);
        Assert.Contains(candidate, File.ReadAllText(Path.Combine(Data, "..", "docs", document)), StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptedDeliveryMatchesItsPinnedManifestAndEveryOutput()
    {
        Assert.Equal("d8ebcca4ad1de18a9a6aec0a23512cdd1ee164c0cd32ab4e100ebb7c58722c28", Hash(Path.Combine(Pack, "manifest.json")));
        Assert.Equal("f1e9fc0d964007e2a15975685ea1968b9260075d133a10f27e13210a1fedb37a", Hash(Path.Combine(Delivery, "assembly.json")));
        using var assembly = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Delivery, "assembly.json")));
        Assert.True(assembly.RootElement.GetProperty("complete_pack_assembled").GetBoolean());
        Assert.False(assembly.RootElement.GetProperty("complete_native_source_derivation").GetBoolean());
        var outputs = assembly.RootElement.GetProperty("outputs").EnumerateObject().ToArray();
        Assert.Equal(39, outputs.Length);
        var evidence = outputs.Where(output => output.Name.StartsWith(Evidence, StringComparison.Ordinal)).ToArray();
        Assert.Equal(14, evidence.Length);
        Assert.Equal("de7f1ac0e90b6f00ca754cc72be71dc678eedc79bfe33315eed967312236a241",
            evidence.Single(output => output.Name == Evidence + "packet.json").Value.GetProperty("sha256").GetString());
        var adopted = outputs.Except(evidence).ToArray();
        foreach (var output in adopted)
        {
            var inPack = output.Name.StartsWith("pack/", StringComparison.Ordinal);
            var root = inPack ? Pack : Delivery;
            var relative = inPack ? output.Name[5..] : output.Name;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(output.Value.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
            Assert.Equal(output.Value.GetProperty("sha256").GetString(), Hash(path));
        }
        var actual = Directory.EnumerateFiles(Pack, "*", SearchOption.AllDirectories).Select(path => "pack/" + Relative(Pack, path))
            .Concat(Directory.EnumerateFiles(Delivery, "*", SearchOption.AllDirectories).Select(path => Relative(Delivery, path)))
            .Where(path => path != "assembly.json").Order(StringComparer.Ordinal);
        Assert.Equal(adopted.Select(output => output.Name).Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void TheTalentOverlayReportPinsItsInputsAndOwnerDecisions()
    {
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Delivery, "reports", "talent-overlay.json")));
        var root = report.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("d8ebcca4ad1de18a9a6aec0a23512cdd1ee164c0cd32ab4e100ebb7c58722c28", root.GetProperty("pack_manifest_sha256").GetString());
        // This sits on the r85 candidate.
        Assert.Equal("0652247f6ddd43d739dae0bb8bb137620d6837b4e26aca8302ba558ea271df1d", root.GetProperty("base_manifest_sha256").GetString());
        Assert.Equal(Hash(Path.Combine(Delivery, "overlay-plan.json")), root.GetProperty("overlay_plan_sha256").GetString());
        // R86: Ignited's two rows with also skill_kill, and their coverage.
        Assert.Equal(4, root.GetProperty("changes").GetArrayLength());
        Assert.True(root.GetProperty("assets_sha256_unchanged").GetBoolean());
        Assert.True(root.GetProperty("presentation_and_share_bytes_unchanged").GetBoolean());
        // The report counts a coverage edit on a talent as a talent change.
        Assert.True(root.GetProperty("stats_unchanged").GetBoolean());
        Assert.False(root.GetProperty("talent_modifiers_unchanged").GetBoolean());
        Assert.False(root.GetProperty("runtime_verified").GetBoolean());
    }

    [Theory]
    [InlineData("rules.json")]
    [InlineData("simulation.json")]
    public void ADocumentChangesOnlyByItsOverlayRows(string file)
    {
        // The delivery holds the pack this overlay was built on, and that pack was adopted by its own hash, so undoing this overlay's rows
        // must give it back. Nothing else may have moved.
        var adopted = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Pack, file)))!;
        var before = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Delivery, "support", "base-pack", file)))!;
        var plan = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Delivery, "overlay-plan.json")))!;
        foreach (var change in plan["changes"]!.AsArray().Where(change => change!["document"]!.GetValue<string>() == file).Reverse())
        {
            var collection = adopted[change!["collection"]!.GetValue<string>()]!;
            // Curves are keyed by id, each holding its points.
            if (collection is JsonObject keyed)
            {
                var id = change["match"]!["id"]!.GetValue<string>();
                Assert.True(JsonNode.DeepEquals(keyed[id], change["after"]!["points"]));
                if (change["op"]!.GetValue<string>() == "append") { keyed.Remove(id); }
                else { keyed[id] = change["before"]!["points"]!.DeepClone(); }
                continue;
            }
            var rows = collection.AsArray();
            var index = rows.Select((row, at) => (row, at)).Single(entry => JsonNode.DeepEquals(entry.row, change["after"])).at;
            if (change["op"]!.GetValue<string>() == "append") { rows.RemoveAt(index); }
            else { rows[index] = change["before"]!.DeepClone(); }
        }
        Assert.True(JsonNode.DeepEquals(before, adopted), $"{file} changed beyond its overlay's rows");
    }

    [Fact]
    public void EverySelectorSaysWhatItIsAndWhereItGoes()
    {
        var selectors = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Pack, "rules.json")))!["selectors"]!.AsArray();
        Assert.Equal(132, selectors.Count);
        using var presentation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Pack, "presentation.json")));
        var sections = presentation.RootElement.GetProperty("layout").GetProperty("assumptionSections")
            .EnumerateArray().Select(section => section.GetProperty("name").GetString()).ToArray();
        foreach (var selector in selectors)
        {
            foreach (var field in new[] { "name", "section", "description" }) { Assert.False(string.IsNullOrWhiteSpace(selector![field]!.GetValue<string>())); }
            Assert.Contains(selector!["section"]!.GetValue<string>(), sections);
        }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
