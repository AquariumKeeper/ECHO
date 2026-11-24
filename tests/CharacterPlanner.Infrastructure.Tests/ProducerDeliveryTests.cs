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
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        var candidate = string.Join('.', version.Split('.')[^3..]);
        var text = File.ReadAllText(Path.Combine(Data, "..", "docs", document));
        Assert.Contains(candidate, text, StringComparison.Ordinal);
        // A doc that gives the whole version, date and all, gives this one: the date moves with the candidate too.
        foreach (System.Text.RegularExpressions.Match named in System.Text.RegularExpressions.Regex.Matches(text, @"y8s3\.3-pve\.\d{4}-\d{2}-\d{2}\.r\d+\.candidate\.\d+"))
        { Assert.Equal(version, named.Value); }
    }

    [Fact]
    public void AdoptedDeliveryMatchesItsPinnedManifestAndEveryOutput()
    {
        Assert.Equal("e417acb4ff14589aa77b912c11df3a9edafc32ecc6538e9fae9522dd10907bbe", Hash(Path.Combine(Pack, "manifest.json")));
        Assert.Equal("ae4bdee56617b1d1395eb98eeae6d7b71c5570530ac2640dd64253e743acf56d", Hash(Path.Combine(Delivery, "assembly.json")));
        using var assembly = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Delivery, "assembly.json")));
        Assert.True(assembly.RootElement.GetProperty("complete_pack_assembled").GetBoolean());
        Assert.False(assembly.RootElement.GetProperty("complete_native_source_derivation").GetBoolean());
        var outputs = assembly.RootElement.GetProperty("outputs").EnumerateObject().ToArray();
        Assert.Equal(77, outputs.Length);
        var evidence = outputs.Where(output => output.Name.StartsWith(Evidence, StringComparison.Ordinal)).ToArray();
        Assert.Equal(52, evidence.Length);
        Assert.Equal("647c0eb5ccb9b50bff9ec0062505ddd36685d756f58619ace8bc25f9ef1c4f32",
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
        Assert.Equal("e417acb4ff14589aa77b912c11df3a9edafc32ecc6538e9fae9522dd10907bbe", root.GetProperty("pack_manifest_sha256").GetString());
        // This sits on r167.candidate.1 (R167: Hive haste), copied and checked in the commit before it.
        Assert.Equal("92b4603b19f9d23202b6ba20e3840a45c5594dc59d08fac5f237d0ea0e39da32", root.GetProperty("base_manifest_sha256").GetString());
        Assert.Equal(Hash(Path.Combine(Delivery, "overlay-plan.json")), root.GetProperty("overlay_plan_sha256").GetString());
        // R168: ten weapon rows, for the six weapons that had no swap multiplier.
        Assert.Equal(10, root.GetProperty("changes").GetArrayLength());
        Assert.True(root.GetProperty("assets_sha256_unchanged").GetBoolean());
        Assert.True(root.GetProperty("presentation_and_share_bytes_unchanged").GetBoolean());
        // Items alone: every stat and talent is unchanged, as the overlay's own rows say (ADocumentChangesOnlyByItsOverlayRows undoes them).
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
            // A single object (the armor kit, R127) is replaced whole, matched by nothing; one an overlay created goes.
            if (collection is JsonObject whole && change["match"]!.AsObject().Count == 0)
            {
                Assert.True(JsonNode.DeepEquals(whole, change["after"]));
                var named = change["collection"]!.GetValue<string>();
                if (change["before"] is { } prior) { adopted[named] = prior.DeepClone(); }
                else { adopted.AsObject().Remove(named); }
                continue;
            }
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
        // R133 added Salvage's refill key, R143 the Demolisher Firefly's setting, R151 the hives' keys, R154 the Defender's and
        // Fortified's, R157 the Precision's, and R159 the Trophy's.
        Assert.Equal(146, selectors.Count);
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
