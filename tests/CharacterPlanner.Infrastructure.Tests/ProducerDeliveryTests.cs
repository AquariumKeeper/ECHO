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

    // The assembly's source evidence (whatever the latest candidate cites: extracted game files, reviews, earlier handoffs) stays in the data project; only its hashes are pinned here,
    // through assembly.json and the evidence packet.
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
        Assert.Equal("bc0afd6f8fae05a8ea1648b001ce56dfc77e6f086a694c844d05ea2655021ef1", Hash(Path.Combine(Pack, "manifest.json")));
        Assert.Equal("71a0339bc5f94881c43db2813b09adbc46e2fd1e9a52dac13f4fe1800c9026df", Hash(Path.Combine(Delivery, "assembly.json")));
        using var assembly = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Delivery, "assembly.json")));
        Assert.True(assembly.RootElement.GetProperty("complete_pack_assembled").GetBoolean());
        Assert.False(assembly.RootElement.GetProperty("complete_native_source_derivation").GetBoolean());
        var outputs = assembly.RootElement.GetProperty("outputs").EnumerateObject().ToArray();
        Assert.Equal(42, outputs.Length);
        var evidence = outputs.Where(output => output.Name.StartsWith(Evidence, StringComparison.Ordinal)).ToArray();
        Assert.Equal(17, evidence.Length);
        Assert.Equal("59351c677ac36c87da2fadc7b20fc42f44ff19129ac17aac147d2ae10e2f9283",
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
        Assert.Equal("bc0afd6f8fae05a8ea1648b001ce56dfc77e6f086a694c844d05ea2655021ef1", root.GetProperty("pack_manifest_sha256").GetString());
        // This sits on the r112-r113 candidate.
        Assert.Equal("01f3d967eb6c742681282392ce280291cdea63c411b8e31d5e53f9f3186ffc3f", root.GetProperty("base_manifest_sha256").GetString());
        Assert.Equal(Hash(Path.Combine(Delivery, "overlay-plan.json")), root.GetProperty("overlay_plan_sha256").GetString());
        // R115: the bleed's tick stat and row, Chemical Agent's and Cover Shooter's text, and Cover Shooter's rule and input text.
        Assert.Equal(6, root.GetProperty("changes").GetArrayLength());
        Assert.True(root.GetProperty("assets_sha256_unchanged").GetBoolean());
        Assert.True(root.GetProperty("presentation_and_share_bytes_unchanged").GetBoolean());
        Assert.False(root.GetProperty("stats_unchanged").GetBoolean());
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
            // A collection an overlay created (R88 to R91's blasts, spreads and passOns) goes with its last row.
            var name = change["collection"]!.GetValue<string>();
            if (rows.Count == 0 && before[name] is null) { adopted.AsObject().Remove(name); }
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
