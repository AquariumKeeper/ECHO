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
        Assert.Equal("55a08bec76063163ee0fbd96b146d217e229fd163ed0503a6dad6d8149f122ac", Hash(Path.Combine(Pack, "manifest.json")));
        Assert.Equal("1c9c27f36b278f7a77ec0400b04389e9459ac7f802cf0e64a67841fbcbe9d585", Hash(Path.Combine(Delivery, "assembly.json")));
        using var assembly = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Delivery, "assembly.json")));
        Assert.True(assembly.RootElement.GetProperty("complete_pack_assembled").GetBoolean());
        Assert.False(assembly.RootElement.GetProperty("complete_native_source_derivation").GetBoolean());
        var outputs = assembly.RootElement.GetProperty("outputs").EnumerateObject().ToArray();
        Assert.Equal(68, outputs.Length);
        var evidence = outputs.Where(output => output.Name.StartsWith(Evidence, StringComparison.Ordinal)).ToArray();
        Assert.Equal(43, evidence.Length);
        Assert.Equal("e6d18d7ee3b616b4ec87e4aaf314c862d15b630a966df7917c76c69c80813904",
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
        Assert.Equal("55a08bec76063163ee0fbd96b146d217e229fd163ed0503a6dad6d8149f122ac", root.GetProperty("pack_manifest_sha256").GetString());
        // This sits on the r109 candidate.
        Assert.Equal("4aaeaa76099fa053266561642026cc81a676889c98379a7004f47dff3dda54b4", root.GetProperty("base_manifest_sha256").GetString());
        Assert.Equal(Hash(Path.Combine(Delivery, "overlay-plan.json")), root.GetProperty("overlay_plan_sha256").GetString());
        // R110: controlStatus on four skill rules (areas on two), and those skills' output descriptions; no talent or stat moves.
        Assert.Equal(8, root.GetProperty("changes").GetArrayLength());
        Assert.True(root.GetProperty("assets_sha256_unchanged").GetBoolean());
        Assert.True(root.GetProperty("presentation_and_share_bytes_unchanged").GetBoolean());
        Assert.True(root.GetProperty("stats_unchanged").GetBoolean());
        Assert.True(root.GetProperty("talent_modifiers_unchanged").GetBoolean());
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
