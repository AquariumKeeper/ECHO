using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>The one-time v1 document importer keeps v1's accept and reject rules; its messages are its own.</summary>
public sealed class BuildDocumentTests
{
    private static readonly byte[] CorpusBytes = LegacyPacks.Bytes(Path.Combine("document-json", "legacy-v1.json"));
    private static readonly Dictionary<string, JsonElement> Rows = LoadRows();
    private static Dictionary<string, JsonElement> LoadRows()
    {
        using var document = JsonDocument.Parse(CorpusBytes);
        return document.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone(), StringComparer.Ordinal);
    }
    public static IEnumerable<object[]> Cases() => Rows.Keys.Select(id => new object[] { id });

    [Fact]
    public void CorpusPinsFrozenReleaseInputsAndPack()
    {
        using var corpus = JsonDocument.Parse(CorpusBytes);
        using var manifest = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine("pack-validation", "manifest.json")));
        var reference = corpus.RootElement.GetProperty("reference");
        Assert.Equal(manifest.RootElement.GetProperty("commit").GetString(), reference.GetProperty("commit").GetString());
        Assert.Equal(manifest.RootElement.GetProperty("zip").GetProperty("sha256").GetString(), reference.GetProperty("zipSha256").GetString());
        foreach (var (file, property) in new[] { (Path.Combine("document-json", "inputs.json"), "inputsSha256"), (Path.Combine("packs", "ashen-vale.json"), "packSha256") })
        { Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(LegacyPacks.Bytes(file))), reference.GetProperty(property).GetString()); }
        Assert.Equal(135, Rows.Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ImportKeepsFrozenAcceptAndReject(string id)
    {
        var row = Rows[id];
        var input = Encoding.UTF8.GetBytes(row.GetProperty("input").GetString()!);
        if (!row.GetProperty("decodeAccepted").GetBoolean())
        {
            var exception = Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(input));
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
            return;
        }
        var import = BuildDocumentJson.Decode(input);
        if (row.GetProperty("response").TryGetProperty("document", out var expected))
        {
            AssertImportKeepsExport(expected.GetProperty("build"), import);
        }
    }

    public static IEnumerable<object[]> DivisionFixtures() =>
        new[] { "foundry-shield", "hard-wired-skills", "party-buffs", "prototype-gear", "shotgun-close", "striker-rifle" }.Select(name => new object[] { name });

    /// <summary>The Division fixtures fill the fields the document corpus leaves empty: rolls, mods, talents, skills, upgrades, Prototype and party.</summary>
    [Theory]
    [MemberData(nameof(DivisionFixtures))]
    public void ImportKeepsEveryFieldOfTheDivisionFixtures(string name)
    {
        var import = BuildDocumentJson.Decode(LegacyPacks.Bytes(Path.Combine("c0-builds", name, "document.json")));
        using var resolved = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine("c0-builds", name, "resolve.json")));
        AssertImportKeepsExport(resolved.RootElement.GetProperty("response").GetProperty("document").GetProperty("build"), import);
    }

    /// <summary>
    /// Every accepted v1 document converts to a v2 document that reads back unchanged, so a library migrated
    /// once loses nothing beyond the removed fields.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void AcceptedDocumentsConvertToV2Losslessly(string id)
    {
        var row = Rows[id];
        if (!row.GetProperty("decodeAccepted").GetBoolean()) { return; }
        AssertV2RoundTrip(BuildDocumentJson.Decode(Encoding.UTF8.GetBytes(row.GetProperty("input").GetString()!)).Build);
    }

    /// <summary>The Division fixtures fill every field, the party included.</summary>
    [Theory]
    [MemberData(nameof(DivisionFixtures))]
    public void DivisionFixturesConvertToV2Losslessly(string name) =>
        AssertV2RoundTrip(BuildDocumentJson.Decode(LegacyPacks.Bytes(Path.Combine("c0-builds", name, "document.json"))).Build);

    private static void AssertV2RoundTrip(BuildSnapshot build) =>
        Assert.Equal(Normalized(build), Normalized(BuildDocument.Decode(BuildDocument.Encode(build))));

    /// <summary>
    /// A snapshot's full export, for comparisons that must not depend on a format's own normalisation. The one
    /// normalisation ADR 0003 and ADR 0004 record is that an empty per-slot map is not written.
    /// </summary>
    internal static string Normalized(BuildSnapshot build)
    {
        var export = Export(build, 0);
        foreach (var field in new[] { "attributeRolls", "modSelections", "modRolls", "talentSelections" })
        {
            var slots = export[field]!.AsObject();
            foreach (var empty in slots.Where(pair => pair.Value!.AsObject().Count == 0).Select(pair => pair.Key).ToList()) { slots.Remove(empty); }
        }
        return Canonical(export);
    }

    private static readonly string[] Removed = ["allocations", "activeScenarios", "allocatedSkills"];

    /// <summary>
    /// The normalized v1 export shows what v1 kept; the import keeps every one of those values except
    /// the removed fields. v1 omitted empty prototypes and party, and a member's empty code and zero
    /// armor, so omitted and empty compare equal.
    /// </summary>
    private static void AssertImportKeepsExport(JsonElement build, BuildDocumentImport import)
    {
        var expected = JsonNode.Parse(build.GetRawText())!.AsObject();
        foreach (var name in Removed) { Assert.True(expected.Remove(name), $"The export has no {name}"); }
        expected.TryAdd("prototypes", new JsonObject());
        expected.TryAdd("party", new JsonArray());
        foreach (var member in expected["party"]!.AsArray())
        {
            member!.AsObject().TryAdd("code", "");
            member.AsObject().TryAdd("armor", 0);
        }
        Assert.Equal(Canonical(expected), Canonical(Export(import.Build, import.Level)));
        // Zero allocations were every primary stat's default, so only non-zero points count as lost.
        var dropped = Removed.Where(name => name == "allocations"
            ? build.GetProperty(name).EnumerateObject().Any(pair => pair.Value.GetDouble() != 0)
            : build.GetProperty(name).EnumerateObject().Any());
        Assert.Equal(dropped, import.DroppedFields);
    }

    /// <summary>The kept snapshot in v1's export shape, with every field present.</summary>
    internal static JsonObject Export(BuildSnapshot build, long level) => new()
    {
        ["packId"] = build.PackId,
        ["packVersion"] = build.PackVersion,
        ["level"] = level,
        ["equipment"] = Map(build.Equipment, value => value),
        ["attributeRolls"] = Map(build.AttributeRolls, slot => Map(slot, roll => new JsonObject { ["attributeId"] = roll.AttributeId, ["value"] = roll.Value })),
        ["modSelections"] = Map(build.ModSelections, slot => Map(slot, value => value)),
        ["modRolls"] = Map(build.ModRolls, slot => Map(slot, value => value)),
        ["talentSelections"] = Map(build.TalentSelections, slot => Map(slot, value => value)),
        ["activeSkillSelections"] = Map(build.ActiveSkillSelections, value => value),
        ["skillUpgrades"] = Map(build.SkillUpgrades, upgrade => new JsonObject { ["skillId"] = upgrade.SkillId, ["rank"] = upgrade.Rank }),
        ["skillMods"] = Map(build.SkillMods, selection => new JsonObject
        {
            ["skillId"] = selection.SkillId,
            ["mods"] = Map(selection.Mods, roll => new JsonObject { ["modId"] = roll.ModId, ["value"] = roll.Value })
        }),
        ["specializationId"] = build.SpecializationId,
        ["specializationNodeSelections"] = new JsonArray([.. build.SpecializationNodeSelections.Select(node => (JsonNode?)node)]),
        ["accountUpgrades"] = Map(build.AccountUpgrades, rank => rank),
        ["equipmentUpgrades"] = Map(build.EquipmentUpgrades, upgrade => new JsonObject { ["itemId"] = upgrade.ItemId, ["rank"] = upgrade.Rank }),
        ["prototypes"] = Map(build.Prototypes, prototype => new JsonObject { ["itemId"] = prototype.ItemId, ["augment"] = prototype.Augment, ["level"] = prototype.Level }),
        ["party"] = new JsonArray([.. build.Party.Select(member => (JsonNode?)new JsonObject
        {
            ["name"] = member.Name,
            ["code"] = member.Code,
            ["armor"] = member.Armor,
            ["effects"] = new JsonArray([.. member.Effects.Select(effect => (JsonNode?)new JsonObject
            {
                ["id"] = effect.Id,
                ["values"] = Map(effect.Values, value => value),
                ["active"] = effect.Active
            })])
        })]),
        ["meta"] = new JsonObject { ["name"] = build.Meta.Name, ["notes"] = build.Meta.Notes }
    };

    private static JsonObject Map<T>(IEnumerable<KeyValuePair<string, T>> values, Func<T, JsonNode?> convert) =>
        new(values.Select(pair => KeyValuePair.Create(pair.Key, convert(pair.Value))));

    /// <summary>Ordinal key order and round-trip numbers, so equal values compare equal whatever their source spelling.</summary>
    internal static string Canonical(JsonNode? node) => node switch
    {
        JsonObject value => "{" + string.Join(",", value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canonical(pair.Value))) + "}",
        JsonArray value => "[" + string.Join(",", value.Select(Canonical)) + "]",
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonSerializer.Serialize(value.GetValue<string>()),
        _ => node?.ToJsonString() ?? "null"
    };

    [Fact]
    public void RemovedFieldsAreTypeCheckedThenDroppedAndReported()
    {
        static byte[] Document(string fields) => Encoding.UTF8.GetBytes($$$"""{"schemaVersion":1,"build":{"packId":"division_2","packVersion":"1","level":40{{{fields}}}}}""");
        var import = BuildDocumentJson.Decode(Document(""","allocations":{"strength":2.5},"activeScenarios":{"buffed":{}},"allocatedSkills":{}"""));
        Assert.Equal(40, import.Level);
        Assert.Equal(["allocations", "activeScenarios"], import.DroppedFields);
        Assert.Empty(BuildDocumentJson.Decode(Document(",\"allocations\":{}")).DroppedFields);
        Assert.Empty(BuildDocumentJson.Decode(Document(",\"allocations\":{\"strength\":0}")).DroppedFields);
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Document(",\"allocations\":{\"strength\":\"high\"}")));
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Document(",\"activeScenarios\":[]")));
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Document(",\"allocatedSkills\":{\"node\":{\"x\":1}}")));
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Encoding.UTF8.GetBytes("""{"schemaVersion":1,"build":{"packId":"division_2","packVersion":"1"}}""")));
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Document(",\"level\":1.5")));
    }

    [Fact]
    public void FullIntegerRangeIsRetainedRatherThanNarrowedOrClamped()
    {
        var import = BuildDocumentJson.Decode(Encoding.UTF8.GetBytes($$$"""
            {"schemaVersion":1,"build":{"packId":"test","packVersion":"1","level":{{{long.MaxValue}}},
             "accountUpgrades":{"unknown":{{{long.MaxValue}}}},"equipmentUpgrades":{"slot":{"itemId":"item","rank":{{{long.MinValue}}}}},
             "skillUpgrades":{"one":{"skillId":"skill","rank":{{{1L << 40}}}}},"prototypes":{"slot":{"itemId":"item","augment":"","level":{{{1L << 40}}}}}
            """ + "}}"));
        Assert.Equal(long.MaxValue, import.Level);
        Assert.Equal(long.MaxValue, import.Build.AccountUpgrades["unknown"]);
        Assert.Equal(long.MinValue, import.Build.EquipmentUpgrades["slot"].Rank);
        Assert.Equal(1L << 40, import.Build.SkillUpgrades["one"].Rank);
        Assert.Equal(1L << 40, import.Build.Prototypes["slot"].Level);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void VersionDiagnosticsAreIndependentOfHostCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var raw = Encoding.UTF8.GetBytes("""{"schemaVersion":-1,"build":{"packId":"test","packVersion":"1","level":1}}""");
            Assert.EndsWith("schemaVersion -1", Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(raw)).Message, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>
    /// M2: decoding was quadratic in a document's size (8,000 entries took 1.4 s, so 20,000 would take about 9 s).
    /// The bound is generous so a slow machine doesn't fail it; a quadratic decoder still would. An error in the
    /// last entry still names its path, which is now found only when an error needs it.
    /// </summary>
    [Fact]
    public void LargeDocumentsDecodeInLinearTime()
    {
        static byte[] Document(string last) => Encoding.UTF8.GetBytes("""{"schemaVersion":1,"build":{"packId":"d","packVersion":"1","level":40,"allocations":{""" +
            string.Join(",", Enumerable.Range(0, 19_999).Select(index => $"\"k{index}\":{index}")) + $",\"k19999\":{last}" + "}}}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(["allocations"], BuildDocumentJson.Decode(Document("1")).DroppedFields);
        var message = Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Document("\"x\""))).Message;
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 3);
        Assert.Contains("Document.build.allocations.k19999", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The root, build and meta objects are three levels, so arrays nested in notes reach the 64-level limit at 61.
    /// At the limit a document is judged on its content (notes isn't text) and a syntax error keeps its message;
    /// one level more is refused as too deep.
    /// </summary>
    [Theory]
    [InlineData(61, "", false)]
    [InlineData(61, "1x", false)]
    [InlineData(62, "", true)]
    [InlineData(70, "", true)]
    public void DepthIsBoundedAtTheLimit(int arrays, string inner, bool tooDeep)
    {
        var json = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"build":{"packId":"d","packVersion":"1","level":40,"meta":{"notes":""" +
            new string('[', arrays) + inner + new string(']', arrays) + "}}}");
        var message = Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(json)).Message;
        Assert.Equal(tooDeep, message == "build document is nested more than 64 levels deep");
    }

    [Fact]
    public void SizeIsBoundedLikeV2Documents() => Assert.Equal($"build document is larger than {BuildDocument.MaxBytes} bytes",
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(new byte[BuildDocumentJson.MaxBytes + 1])).Message);

    [Fact]
    public void SkillModMapsAreRequiredAndNeverInventedAsEmpty()
    {
        static byte[] Document(string mods) => Encoding.UTF8.GetBytes("""{"schemaVersion":1,"build":{"packId":"t","packVersion":"1","level":1,"skillMods":{"one":{"skillId":"s","mods":""" + mods + "}}}}");
        Assert.Throws<LegacyDocumentException>(() => BuildDocumentJson.Decode(Document("null")));
        Assert.Empty(BuildDocumentJson.Decode(Document("{}")).Build.SkillMods["one"].Mods);
        Assert.Equal(0.5, BuildDocumentJson.Decode(Document("""{"a":{"modId":"m","value":0.5}}""")).Build.SkillMods["one"].Mods["a"].Value);
    }
}
