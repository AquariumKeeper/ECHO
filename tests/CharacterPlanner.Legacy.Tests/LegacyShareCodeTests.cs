using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>Legacy share codes import to the same build the frozen reader imported, through the v1 document rules.</summary>
public sealed class LegacyShareCodeTests
{
    private static readonly byte[] CorpusBytes = LegacyPacks.Bytes(Path.Combine("share-encoding", "csharp-import-v1.json"));
    private static readonly Dictionary<string, JsonElement> Rows = LoadRows();
    private static Dictionary<string, JsonElement> LoadRows()
    {
        using var document = JsonDocument.Parse(CorpusBytes);
        return document.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone(), StringComparer.Ordinal);
    }
    public static IEnumerable<object[]> Cases() => Rows.Keys.Select(id => new object[] { id });

    /// <summary>The legacy order of the synthetic share contract pack (<c>baseline/share-encoding/pack.json</c>): its primary stats, slots, scenarios and passive skills.</summary>
    private static readonly LegacyShareOrder ContractOrder = new() { Stats = ["power"], Slots = ["main"], Scenarios = ["scenario"], PassiveSkills = ["passive"] };
    private static readonly LegacyShareOrder DivisionOrder = LegacyPacks.LoadV2(Path.Combine("packs", "division-2")).Pack.LegacyShareOrder;

    [Fact]
    public void CorpusPinsFrozenReleaseAndCoversEveryVersion()
    {
        using var corpus = JsonDocument.Parse(CorpusBytes);
        using var manifest = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine("pack-validation", "manifest.json")));
        var reference = corpus.RootElement.GetProperty("reference");
        Assert.Equal(manifest.RootElement.GetProperty("commit").GetString(), reference.GetProperty("commit").GetString());
        Assert.Equal(manifest.RootElement.GetProperty("zip").GetProperty("sha256").GetString(), reference.GetProperty("zipSha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(LegacyPacks.Bytes(Path.Combine("share-encoding", "pack.json")))), reference.GetProperty("sharePackSha256").GetString());
        Assert.Equal(27, Rows.Count);
        var versions = Rows.Values.Select(row => Payload(row.GetProperty("legacyCode").GetString()!).RootElement[0].GetInt64()).ToHashSet();
        Assert.Equal(Enumerable.Range(1, 12).Select(version => (long)version), versions.Order());
    }

    /// <summary>Both the Go encoder's code and the retired C# encoder's code import as the frozen reader imported them.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ImportMatchesFrozenReader(string id)
    {
        var row = Rows[id];
        var order = id.StartsWith("division/", StringComparison.Ordinal) ? DivisionOrder : ContractOrder;
        var expected = BuildDocumentJson.Decode(Encoding.UTF8.GetBytes(row.GetProperty("response").GetProperty("document").GetRawText()));
        foreach (var code in new[] { row.GetProperty("legacyCode").GetString()!, row.GetProperty("code").GetString()! })
        {
            var import = LegacyShareCode.Decode(code, order);
            Assert.Equal(BuildDocumentTests.Canonical(BuildDocumentTests.Export(expected.Build, expected.Level)), BuildDocumentTests.Canonical(BuildDocumentTests.Export(import.Build, import.Level)));
            Assert.Equal(expected.DroppedFields, import.DroppedFields);
        }
    }

    /// <summary>
    /// An imported legacy build re-shares losslessly in the app's own code, short enough for a chat message.
    /// The comparison uses the full snapshot export, not the share payload, so nothing the code drops or adds
    /// can hide. The one normalisation, which ADR 0003 records, is that an empty per-slot map is not shared.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ImportedBuildsReshareLosslessly(string id)
    {
        var row = Rows[id];
        var order = id.StartsWith("division/", StringComparison.Ordinal) ? DivisionOrder : ContractOrder;
        var build = LegacyShareCode.Decode(row.GetProperty("legacyCode").GetString()!, order).Build;
        var code = ShareCode.Encode(build);
        Assert.Equal(BuildDocumentTests.Normalized(build), BuildDocumentTests.Normalized(ShareCode.Decode(code)));
        Assert.InRange(code.Length, 1, 2000);
    }

    [Fact]
    public void RemovedFieldsAreReportedAsDropped()
    {
        var import = LegacyShareCode.Decode(Rows["all-features"].GetProperty("legacyCode").GetString()!, ContractOrder);
        Assert.Equal(["allocations", "activeScenarios", "allocatedSkills"], import.DroppedFields);
        Assert.Equal("item", import.Build.Equipment["main"]);
        Assert.Equal(["root"], import.Build.SpecializationNodeSelections);
        Assert.Equal(0.2, import.Build.SkillMods["one"].Mods["socket"].Value);
    }

    [Fact]
    public void PastedWhitespaceIsIgnored()
    {
        var code = Rows["division/striker-rifle"].GetProperty("legacyCode").GetString()!;
        Assert.Equal("division_2", LegacyShareCode.Decode($"  {code}\r\n", DivisionOrder).Build.PackId);
    }

    [Fact]
    public void FrozenInvalidCodeIsRejected()
    {
        using var fixture = JsonDocument.Parse(LegacyPacks.Bytes(Path.Combine("share-encoding", "invalid-share-code.json")));
        var code = fixture.RootElement.GetProperty("args")[3].GetString()!;
        Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode(code, DivisionOrder));
    }

    private const string Head = "\"share_contract\",\"1\",1,[0],[null]";

    [Theory]
    [InlineData("[1," + Head + ",\"Name\",\"\"]", null)]
    [InlineData("[13," + Head + ",\"Name\",\"\"]", "unsupported share code version 13")]
    [InlineData("[0," + Head + ",\"Name\",\"\"]", "unsupported share code version 0")]
    [InlineData("[1.5," + Head + ",\"Name\",\"\"]", "share code has no version")]
    [InlineData("[\"1\"," + Head + ",\"Name\",\"\"]", "share code has no version")]
    [InlineData("[]", "share code has no version")]
    [InlineData("{}", "share code payload is not a tuple")]
    [InlineData("[1," + Head + ",\"Name\"]", "share code v1 has 7 fields; expected 8")]
    [InlineData("[2," + Head + ",\"Name\",\"\"]", "share code v2 has 8 fields; expected 9")]
    [InlineData("[1,\"share_contract\",\"1\",1,[0],[null,null],\"Name\",\"\"]", "share code equipment has 2 entries; the pack's legacy order has 1")]
    [InlineData("[1,\"share_contract\",\"1\",1,[0,0],[null],\"Name\",\"\"]", "share code allocations has 2 entries; the pack's legacy order has 1")]
    [InlineData("[1,\"share_contract\",\"1\",1,[0],{},\"Name\",\"\"]", "share code equipment is not a list")]
    [InlineData("[2," + Head + ",[1],\"Name\",\"\"]", "share code activeScenarios entry 0 is not true or false")]
    [InlineData("[5," + Head + ",[false],[false],{},{\"mods\":{},\"modRolls\":{}},\"Name\",\"\"]", "share code v5 config has unknown member \"modRolls\"")]
    [InlineData("[5," + Head + ",[false],[false],{},{\"extra\":{}},\"Name\",\"\"]", "share code v5 config has unknown member \"extra\"")]
    [InlineData("[6," + Head + ",[false],[false],{},{},{\"nodeSelections\":[]},\"Name\",\"\"]", "share code v6 skills has unknown member \"nodeSelections\"")]
    [InlineData("[6," + Head + ",[false],[false],{},{},{\"activeSkills\":{},\"activeSkills\":{}},\"Name\",\"\"]", "share code skills lists \"activeSkills\" twice")]
    [InlineData("[5," + Head + ",[false],[false],{},[],\"Name\",\"\"]", "share code config is not an object")]
    [InlineData("[1,\"\",\"1\",1,[0],[null],\"Name\",\"\"]", "share code: build document requires non-empty build.packId and build.packVersion")]
    [InlineData("[1,\"share_contract\",\"1\",null,[0],[null],\"Name\",\"\"]", "share code: build document requires build.level")]
    [InlineData("[1,\"share_contract\",\"1\",1,[0],[7],\"Name\",\"\"]", "share code: decode build document")]
    [InlineData("[1,", "share code payload is not JSON")]
    // The tuple's own array and 63 more: a document holding it would be deeper than v1 documents may be.
    [InlineData("[1,\"d\",\"1\",1,[0],[null],\"Name\",[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]", "share code payload is not JSON, or is nested too deeply")]
    public void TupleShapeIsChecked(string tuple, string? error)
    {
        var code = Encode(Encoding.UTF8.GetBytes(tuple));
        if (error is null)
        {
            Assert.Equal("Name", LegacyShareCode.Decode(code, ContractOrder).Build.Meta.Name);
            return;
        }
        Assert.StartsWith(error, Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode(code, ContractOrder)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrappingIsChecked()
    {
        Assert.Equal("share code is empty", Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode(" \t", ContractOrder)).Message);
        Assert.StartsWith("share code is longer than", Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode(new string('A', LegacyShareCode.MaxCodeLength + 1), ContractOrder)).Message, StringComparison.Ordinal);
        Assert.Equal("share code is not base64url text", Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode("ab+/", ContractOrder)).Message);
        Assert.Equal("share code is not compressed build data", Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode("_____w", ContractOrder)).Message);
        var bomb = Encode(new byte[LegacyShareCode.MaxPayloadBytes + 1]);
        Assert.StartsWith("share code expands to more than", Assert.Throws<ShareCodeException>(() => LegacyShareCode.Decode(bomb, ContractOrder)).Message, StringComparison.Ordinal);
    }

    private static string Encode(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal)) { deflate.Write(payload); }
        return Base64Url.EncodeToString(output.ToArray());
    }

    private static JsonDocument Payload(string code)
    {
        using var input = new DeflateStream(new MemoryStream(Base64Url.DecodeFromChars(code)), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return JsonDocument.Parse(output.ToArray());
    }
}
