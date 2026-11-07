using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.TestPacks;

namespace CharacterPlanner.Core.Tests;

public sealed class PackFileTests
{
    private static PackReadResult Read(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) => PackReader.Read(files);

    private static void AssertRejected(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string code, string pathPrefix)
    {
        var result = Read(files);
        Assert.Null(result.Pack);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error" && diagnostic.Code == code && diagnostic.Path.StartsWith(pathPrefix, StringComparison.Ordinal));
        Assert.Null(PackValidator.Load(files).Compiled);
    }

    private static string Rules => Text(Files(), PackFiles.Rules);

    [Fact]
    public void WritesFiveFilesThatReadBackWithoutDiagnostics()
    {
        var files = Files();
        Assert.Equal(new[] { PackFiles.Manifest, PackFiles.Presentation, PackFiles.Rules, PackFiles.ShareCodes, PackFiles.Simulation }, files.Keys);
        var result = Read(files);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("tiny", result.Manifest!.ID);
        Assert.Equal(TestPacks.Tiny.Capabilities, result.Manifest.Capabilities);
        var validation = PackValidator.Load(files);
        Assert.Empty(validation.Diagnostics);
        Assert.NotNull(validation.Compiled);
        Assert.Equal("tiny", validation.Manifest!.ID);
    }

    [Fact]
    public void RoundTripReproducesThePackAndItsBytes()
    {
        var files = Files();
        var pack = Assert.IsType<GamePack>(Read(files).Pack);
        Assert.Equal(Canonicalize(Tiny), Canonicalize(pack));
        var again = PackWriter.Write(pack);
        Assert.Equal(files.Keys, again.Keys);
        foreach (var (name, bytes) in files) { Assert.True(bytes.Span.SequenceEqual(again[name].Span), name); }
    }

    [Fact]
    public void StatusDamageRoundTripsAndIsOmittedWhenEmpty()
    {
        var pack = Tiny with
        {
            Simulation = Tiny.Simulation with
            {
                StatusDamage = [new() { Status = "burning", Tick = "armor", Period = .5 }, new() { Status = "bleeding", Tick = "armor", Period = .5, TickOnApply = true }]
            }
        };
        var files = PackWriter.Write(pack);
        var simulation = JsonNode.Parse(Encoding.UTF8.GetString(files[PackFiles.Simulation].Span))!;
        Assert.Equal("""[{"status":"burning","tick":"armor","period":0.5},{"status":"bleeding","tick":"armor","period":0.5,"tickOnApply":true}]""",
            simulation["statusDamage"]!.ToJsonString());
        var read = Assert.IsType<GamePack>(Read(files).Pack);
        Assert.Equal(pack.Simulation.StatusDamage, read.Simulation.StatusDamage);
        Assert.NotNull(PackValidator.Load(files).Compiled);
        Assert.Null(JsonNode.Parse(Text(Files(), PackFiles.Simulation))!["statusDamage"]);
    }

    [Fact]
    public void CancelAndBankFieldsRoundTripAndAreOmittedWhenUnset()
    {
        var trigger = new SimTriggerDefinition { Talent = "t", Key = "k", On = "swapped", Kind = "buff", Duration = 5, CancelOn = "swapped", CancelFor = 5 };
        var bank = new SimTriggerDefinition
        {
            Talent = "t",
            Key = "s",
            On = "hit",
            Kind = "stacks",
            Max = 9,
            Pool = "p",
            PoolMax = 10,
            Bank = "s_banked",
            ResetOn = "reload",
            ClearGrant = "temporary_armor",
            ClearAmount = 0.05,
            ClearDuration = 10
        };
        var pack = Tiny with { Simulation = Tiny.Simulation with { Triggers = [trigger, bank] } };
        var files = PackWriter.Write(pack);
        var triggers = JsonNode.Parse(Encoding.UTF8.GetString(files[PackFiles.Simulation].Span))!["triggers"]!;
        Assert.Equal("swapped", triggers[0]!["cancelOn"]!.GetValue<string>());
        Assert.Equal(5, triggers[0]!["cancelFor"]!.GetValue<double>());
        Assert.Equal("s_banked", triggers[1]!["bank"]!.GetValue<string>());
        Assert.Null(triggers[1]!["cancelOn"]);
        Assert.Null(triggers[0]!["bank"]);
        var read = Assert.IsType<GamePack>(Read(files).Pack).Simulation.Triggers;
        Assert.Equal(("swapped", 5.0, ""), (read[0].CancelOn, read[0].CancelFor, read[0].Bank));
        Assert.Equal(("", 0.0, "s_banked"), (read[1].CancelOn, read[1].CancelFor, read[1].Bank));
        Assert.Equal(("temporary_armor", 0.05, 10.0), (read[1].ClearGrant, read[1].ClearAmount, read[1].ClearDuration));
        Assert.Null(triggers[0]!["clearGrant"]);
        Assert.True(files[PackFiles.Simulation].Span.SequenceEqual(PackWriter.Write(Assert.IsType<GamePack>(Read(files).Pack))[PackFiles.Simulation].Span));
    }

    // The tiny pack cannot cover every field the real pack uses, so the committed Division 2 pack
    // round-trips too: reading loses nothing the writer emits; externally authored formatting
    // is normalized, while subsequent writer output is byte deterministic.
    [Fact]
    public void CommittedDivisionPackRoundTrips()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "packs", "division-2");
        var committed = Directory.EnumerateFiles(directory).ToImmutableSortedDictionary(
            path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);
        Assert.Equal(new[] { PackFiles.Manifest, PackFiles.Presentation, PackFiles.Provenance, PackFiles.Rules, PackFiles.ShareCodes, PackFiles.Simulation }, committed.Keys);
        var read = Read(committed);
        Assert.Empty(read.Diagnostics);
        var pack = Assert.IsType<GamePack>(read.Pack);
        var written = PackWriter.Write(pack, new() { Provenance = committed[PackFiles.Provenance], AssetsSha256 = read.Manifest!.AssetsSha256 });
        Assert.Equal(committed.Keys, written.Keys);
        Assert.Equal(committed[PackFiles.Provenance].ToArray(), written[PackFiles.Provenance].ToArray());
        var reread = Read(written);
        Assert.Equal(read.Manifest!.AssetsSha256, reread.Manifest!.AssetsSha256);
        var rereadPack = Assert.IsType<GamePack>(reread.Pack);
        var again = PackWriter.Write(rereadPack, new() { Provenance = committed[PackFiles.Provenance], AssetsSha256 = read.Manifest.AssetsSha256 });
        Assert.All(written, pair => Assert.True(pair.Value.Span.SequenceEqual(again[pair.Key].Span)));
        Assert.Equal(Canonicalize(pack), Canonicalize(rereadPack));
    }

    // The producer-authored pack above is normalized, so its bytes cannot pin the writer's format.
    // The committed conversion reference is writer output; rewriting it must reproduce every byte.
    [Fact]
    public void WriterReproducesThePinnedConversionReferenceBytes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "packs", "conversion-reference");
        var reference = Directory.EnumerateFiles(directory).ToImmutableSortedDictionary(
            path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);
        var read = Read(reference);
        Assert.Empty(read.Diagnostics);
        var written = PackWriter.Write(Assert.IsType<GamePack>(read.Pack), new() { Provenance = reference[PackFiles.Provenance], AssetsSha256 = read.Manifest!.AssetsSha256 });
        Assert.Equal(reference.Keys, written.Keys);
        Assert.All(reference, pair => Assert.True(pair.Value.Span.SequenceEqual(written[pair.Key].Span), $"{pair.Key} differs from the pinned writer output"));
    }

    [Fact]
    public void OutputIsDeterministicUtf8WithLfAndManifestHashes()
    {
        var first = Files();
        var manifest = JsonNode.Parse(first[PackFiles.Manifest].Span)!;
        foreach (var (name, bytes) in first)
        {
            var text = Encoding.UTF8.GetString(bytes.Span);
            Assert.EndsWith("}\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', text);
            Assert.False(bytes.Span.StartsWith("﻿"u8));
            if (name == PackFiles.Manifest) { continue; }
            Assert.Equal(PackFiles.Sha256(bytes.Span), (string)manifest["files"]![name]!["sha256"]!);
            Assert.Equal(bytes.Length, (long)manifest["files"]![name]!["bytes"]!);
        }
        Assert.StartsWith("{\n  \"level\": 40,\n  \"stats\": [\n    {\n      \"id\": \"weapon_damage\",", Text(first, PackFiles.Rules), StringComparison.Ordinal);
    }

    // Mixed case, so ordinal order ("K…" before "k…") differs from culture order, and enough keys
    // that an ImmutableDictionary's hash order never matches ordinal order by chance.
    private static readonly string[] MapKeys = [.. Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? "K" : "k") + i.ToString("D2", CultureInfo.InvariantCulture))];

    private static ImmutableDictionary<string, T> Map<T>(Func<string, T> value) =>
        MapKeys.OrderDescending(StringComparer.Ordinal).Aggregate(ImmutableDictionary<string, T>.Empty, (map, key) => map.Add(key, value(key)));

    [Fact]
    public void WriterOrdersEveryMapByOrdinalKey()
    {
        var expected = MapKeys.Order(StringComparer.Ordinal).ToArray();
        var ids = Map(key => key);
        // Precondition: the maps themselves do not enumerate in ordinal order.
        Assert.NotEqual(expected, ids.Keys);
        var mask = Tiny.Items[0];
        var pack = Tiny with
        {
            Curves = Map<ImmutableArray<Expressions.CurvePoint>>(_ => [new(0, 1)]),
            RenamedIds = Map(_ => ids),
            Items = Tiny.Items.SetItem(0, mask with { Modifiers = [mask.Modifiers[0] with { RequiresContext = ids }] }),
            Presentation = Tiny.Presentation with
            {
                Theme = Tiny.Presentation.Theme! with { Colors = Map(_ => "#000000") },
                Icons = Tiny.Presentation.Icons.SetItem(PresentationIcons.Talents, ids),
                SpecializationImages = ids,
                ItemArt = Map(_ => new ItemArtwork("", "icon_mask"))
            }
        };
        var files = PackWriter.Write(pack);
        var rules = JsonNode.Parse(files[PackFiles.Rules].Span)!;
        var presentation = JsonNode.Parse(files[PackFiles.Presentation].Span)!;
        Assert.Equal(expected, Keys(rules["curves"]));
        Assert.Equal(expected, Keys(rules["renamedIds"]));
        Assert.All(rules["renamedIds"]!.AsObject(), pair => Assert.Equal(expected, Keys(pair.Value)));
        Assert.Equal(expected, Keys(rules["items"]![0]!["modifiers"]![0]!["requires"]));
        Assert.Equal(expected, Keys(presentation["theme"]!["colors"]));
        Assert.Equal(expected, Keys(presentation["icons"]!["talents"]));
        Assert.Equal(expected, Keys(presentation["specializationImages"]));
        Assert.Equal(expected, Keys(presentation["itemArt"]));

        static IEnumerable<string> Keys(JsonNode? node) => node!.AsObject().Select(pair => pair.Key);
    }

    [Fact]
    public void WriterSharesRepeatedSlotListsAsNamedTemplates()
    {
        var rules = JsonNode.Parse(Rules)!;
        var templates = rules["templates"]!;
        Assert.Equal(new[] { "mask_1", "primary_weapon_1" }, templates["attributeSlots"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(new[] { "mask_1" }, templates["modSlots"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(new[] { "mask_1", "primary_weapon_1" }, templates["talentSlots"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(new[] { "primary_weapon_1" }, templates["upgrades"]!.AsObject().Select(pair => pair.Key));
        var rifle = rules["items"]![2]!;
        Assert.Equal("rifle_b", (string)rifle["id"]!);
        Assert.Equal("primary_weapon_1", (string)rifle["attributeSlots"]!);
        Assert.Equal("primary_weapon_1", (string)rifle["upgrade"]!);
        // Weapon base stats keep their authored order; it is the breakdown order.
        Assert.Equal(new[] { "weapon_rpm", "weapon_base_damage" }, rifle["weapon"]!.AsObject().Select(pair => pair.Key));
        Assert.Null(rifle["modSlots"]);
    }

    [Fact]
    public void ReaderExpandsTemplatesForEveryReference()
    {
        var files = WithFile(PackFiles.Rules, """
            {"level":40,"stats":[{"id":"armor","name":"Armor","format":"integer","formula":"0"}],
             "attributes":[{"id":"armor_core","name":"Armor","stat":"armor","min":0,"max":10,"step":1}],
             "templates":{"attributeSlots":{"gear":[{"id":"core","name":"Core","choices":[{"attribute":"armor_core","max":5}]}]},
                          "upgrades":{"expertise":{"name":"Expertise","maxRank":2,"modifiersPerRank":[{"stat":"armor","op":"flat","value":3}]}}},
             "slots":[{"id":"mask","name":"Mask"},{"id":"chest","name":"Chest"}],
             "items":[{"id":"a","name":"A","slot":"mask","attributeSlots":"gear","upgrade":"expertise"},
                      {"id":"b","name":"B","slot":"chest","attributeSlots":"gear"}]}
            """.Replace("\r", "", StringComparison.Ordinal));
        var result = Read(files);
        var pack = Assert.IsType<GamePack>(result.Pack);
        Assert.All(pack.Items, item => Assert.Equal(5, Assert.Single(Assert.Single(item.AttributeSlots).Choices).Max));
        Assert.Equal(3, pack.Items[0].Upgrade!.ModifiersPerRank[0].Value.Number);
        Assert.Null(pack.Items[1].Upgrade);
        Assert.Empty(pack.Items[1].ModSlots);
    }

    [Fact]
    public void ReportsUnknownAndUnusedTemplates()
    {
        AssertRejected(Edit(PackFiles.Rules, rules => rules["items"]![0]!["modSlots"] = "missing"), "unknown-template", "rules.json:$.items[0].modSlots");
        var unused = Read(Edit(PackFiles.Rules, rules => rules["templates"]!["upgrades"]!["spare"] = JsonNode.Parse("""{"name":"Spare","maxRank":1,"modifiersPerRank":[]}""")));
        Assert.NotNull(unused.Pack);
        Assert.Equal("unused-template", Assert.Single(unused.Diagnostics).Code);
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace("\"level\": 40,", "\"level\": 40, \"level\": 41,", StringComparison.Ordinal)), "json-duplicate-property", "rules.json:$.level");
        AssertRejected(WithFile(PackFiles.Presentation, """{"theme":{"colors":{"primary":"#000000","primary":"#FFFFFF"}}}""" + "\n"),
            "json-duplicate-property", "presentation.json:$.theme.colors.primary");
    }

    [Fact]
    public void PropertyNamesAreCaseSensitiveAndUnknownPropertiesAreErrors()
    {
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace("\"level\": 40", "\"Level\": 40", StringComparison.Ordinal)), "json-structure", "rules.json:$");
        var unknown = Read(Edit(PackFiles.Rules, rules => rules["items"]![0]!["occupies"] = new JsonArray("mask")));
        Assert.Null(unknown.Pack);
        var diagnostic = Assert.Single(unknown.Diagnostics);
        Assert.Equal(("error", "json-structure", "rules.json:$.items[0].occupies"), (diagnostic.Severity, diagnostic.Code, diagnostic.Path));
    }

    [Fact]
    public void RequiredMembersAndNullsAreEnforced()
    {
        var missing = Read(Edit(PackFiles.Rules, rules => rules["items"]![0]!.AsObject().Remove("slot")));
        Assert.Null(missing.Pack);
        var diagnostic = Assert.Single(missing.Diagnostics);
        Assert.Equal(("error", "json-structure", "rules.json:$.items[0]"), (diagnostic.Severity, diagnostic.Code, diagnostic.Path));
        AssertRejected(Edit(PackFiles.Rules, rules => rules["items"]![0]!["description"] = null), "json-null", "rules.json:$.items[0].description");
        AssertRejected(Edit(PackFiles.Simulation, simulation => simulation["skills"]![0]!["rate"] = null), "json-null", "simulation.json:$.skills[0].rate");
    }

    [Theory]
    [InlineData("\"value\": 0.1", "\"value\": \"\"")]
    [InlineData("\"level\": 40", "\"level\": 40.5")]
    [InlineData("\"level\": 40", "\"level\": \"40\"")]
    [InlineData("\"format\": \"percent\"", "\"format\": 1")]
    public void RejectsWrongTypes(string original, string replacement)
    {
        Assert.Contains(original, Rules, StringComparison.Ordinal);
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace(original, replacement, StringComparison.Ordinal)), "json-structure", "rules.json:");
    }

    [Fact]
    public void RejectsBadEncodingAndSyntax()
    {
        var bytes = Encoding.UTF8.GetBytes(Rules);
        AssertRejected(WithFile(PackFiles.Rules, [.. "﻿"u8, .. bytes]), "file-encoding", "rules.json");
        var invalid = Encoding.UTF8.GetBytes(Rules.Replace("Weapon Damage", "Weapon Damageé", StringComparison.Ordinal));
        var index = Array.IndexOf(invalid, (byte)0xC3);
        invalid[index] = 0xFF;
        AssertRejected(WithFile(PackFiles.Rules, invalid), "file-encoding", "rules.json");
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace("\n", "\r\n", StringComparison.Ordinal)), "file-encoding", "rules.json");
        AssertRejected(WithFile(PackFiles.Rules, "{"), "json-syntax", "rules.json");
        AssertRejected(WithFile(PackFiles.Rules, Rules + "{}"), "json-syntax", "rules.json");
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace("\"level\": 40", "\"level\": 1e400", StringComparison.Ordinal)), "json-number", "rules.json:$.level");
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace("\"level\": 40,", "\"level\": 40, // comment\n", StringComparison.Ordinal)), "json-syntax", "rules.json");
        // Valid UTF-8 bytes can still escape invalid text.
        AssertRejected(WithFile(PackFiles.Rules, Rules.Replace("Weapon Damage", "Weapon Damage\\uD800", StringComparison.Ordinal)), "file-encoding", "rules.json");
    }

    [Fact]
    public void EmptyIconGroupsAreDroppedLikeTheWriterDoes()
    {
        var files = Edit(PackFiles.Presentation, presentation => presentation["icons"]!["nodes"] = new System.Text.Json.Nodes.JsonObject());
        var pack = Assert.IsType<GamePack>(Read(files).Pack);
        Assert.False(pack.Presentation.Icons.ContainsKey(PresentationIcons.Nodes));
    }

    [Fact]
    public void IdsWithATrailingNewlineReachValidationAndFail()
    {
        var files = Edit(PackFiles.Rules, rules => rules["mods"]![0]!["id"] = "mod_crit\n");
        Assert.NotNull(Read(files).Pack);
        var result = PackValidator.Load(files);
        Assert.Null(result.Compiled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalid-id" && diagnostic.Path == "mods.mod_crit\n");
    }

    [Fact]
    public void VerifiesManifestHashesAndSizes()
    {
        var files = Files();
        var sameLength = Encoding.UTF8.GetBytes(Rules.Replace("Weapon Damage", "Weapon Damagf", StringComparison.Ordinal));
        AssertRejected(files.SetItem(PackFiles.Rules, sameLength), "file-hash-mismatch", "manifest.json:$.files.rules.json.sha256");
        AssertRejected(files.SetItem(PackFiles.Rules, Encoding.UTF8.GetBytes(Rules + " ")), "file-size-mismatch", "manifest.json:$.files.rules.json.bytes");
        AssertRejected(files.Remove(PackFiles.Simulation), "file-missing", "manifest.json:$.files.simulation.json");
        AssertRejected(files.Remove(PackFiles.Manifest), "file-missing", "manifest.json");
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Manifest(Action<JsonNode> edit) => Edit(PackFiles.Manifest, edit);

    [Fact]
    public void RejectsUnknownCapabilitiesAndSchemaVersions()
    {
        var capability = Read(Manifest(manifest => manifest["capabilities"]!.AsArray().Add("teleport")));
        Assert.Null(capability.Pack);
        Assert.Equal("tiny", capability.Manifest!.ID);
        var diagnostic = Assert.Single(capability.Diagnostics);
        Assert.Equal(("unknown-capability", "manifest.json:$.capabilities[6]"), (diagnostic.Code, diagnostic.Path));
        AssertRejected(Manifest(manifest => manifest["capabilities"]!.AsArray().Add("weapons")), "duplicate-capability", "manifest.json:$.capabilities[6]");
        AssertRejected(Manifest(manifest => manifest["schemaVersion"] = 1), "schema-version", "manifest.json:$.schemaVersion");
        AssertRejected(Manifest(manifest => manifest["files"]!.AsObject().Remove(PackFiles.ShareCodes)), "manifest-file-missing", "manifest.json:$.files");
        AssertRejected(Manifest(manifest => manifest["files"]!["../secret"] = JsonNode.Parse("""{"sha256":"00","bytes":1}""")), "manifest-file-unknown", "manifest.json:$.files.../secret");
        AssertRejected(Manifest(manifest => manifest["files"]![PackFiles.Rules]!["sha256"] = "ABC"), "manifest-hash-invalid", "manifest.json:$.files.rules.json.sha256");
        AssertRejected(Manifest(manifest => manifest["assetsSha256"] = "xyz"), "manifest-hash-invalid", "manifest.json:$.assetsSha256");
        AssertRejected(Manifest(manifest => manifest["extra"] = 1), "json-structure", "manifest.json:");
    }

    [Fact]
    public void ManifestCarriesProvenanceAndAssetsDigest()
    {
        var digest = PackFiles.DirectoryDigest([new("assets/icon_mask.png", PackFiles.Sha256(IconBytes))]);
        var files = PackWriter.Write(Tiny, new() { Provenance = Encoding.UTF8.GetBytes("# Sources\n"), AssetsSha256 = digest });
        Assert.Contains(PackFiles.Provenance, files.Keys);
        var manifest = Read(files).Manifest!;
        Assert.Equal(digest, manifest.AssetsSha256);
        Assert.Equal(PackFiles.Sha256(Encoding.UTF8.GetBytes("# Sources\n")), manifest.Files[PackFiles.Provenance].Sha256);
        Assert.Equal(PackFiles.Sha256(Encoding.UTF8.GetBytes(PackFiles.Sha256(IconBytes) + "  assets/icon_mask.png\n")), digest);
    }

    [Fact]
    public void CurvePointsArePairs()
    {
        AssertRejected(Edit(PackFiles.Rules, rules => rules["curves"]!["tiers"]![0] = new JsonArray(0, 1, 2)), "curve-point-invalid", "rules.json:$.curves.tiers[0]");
    }
}
