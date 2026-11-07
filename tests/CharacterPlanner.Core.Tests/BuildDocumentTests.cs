using System.Text;
using CharacterPlanner.Core.Builds;
using static CharacterPlanner.Core.Tests.ShareCodeTests;

namespace CharacterPlanner.Core.Tests;

public sealed class BuildDocumentTests
{
    private static readonly BuildSnapshot WithParty = Full with
    {
        Party =
        [
            new("Medic", "D2P1.abc", 0, [new("overwatch", Map(("total_weapon_damage", 0.12), ("total_skill_damage", 0.12)), true), new("vanguard", Map<double>(), false)]),
            new("Tank", "", 1_000_000, [])
        ]
    };

    private static string Party(BuildSnapshot build) => string.Join("\n", build.Party.Select(member =>
        $"{member.Name}|{member.Code}|{member.Armor}|" + string.Join(";", member.Effects.Select(effect =>
            $"{effect.Id}{(effect.Active ? "+" : "-")}" + string.Join(",", effect.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))))));

    private static string Text(byte[] document) => Encoding.UTF8.GetString(document);

    [Fact]
    public void RoundTripKeepsEveryFieldIncludingTheParty()
    {
        var decoded = BuildDocument.Decode(BuildDocument.Encode(WithParty));
        Assert.Equal(Describe(WithParty), Describe(decoded));
        Assert.Equal(Party(WithParty), Party(decoded));
        Assert.Equal(BuildDocument.Encode(WithParty), BuildDocument.Encode(decoded));
    }

    /// <summary>Pins the v2 layout: version first, then the canonical build, indented, plain lists on one line.</summary>
    [Fact]
    public void LayoutIsPinned()
    {
        var build = new BuildSnapshot
        {
            PackId = "division_2",
            Equipment = Map(("mask", "mask_item")),
            AttributeRolls = Map(("mask", Map(("core_1", new AttributeRoll("armor_core", 170000))))),
            SpecializationNodeSelections = ["a", "b"],
            Party = [new("Medic", "", 5, [new("overwatch", Map(("total_weapon_damage", 0.12)), false)])],
            Meta = new("Build", "")
        };
        Assert.Equal("""
            {
              "schemaVersion": 2,
              "build": {
                "name": "Build",
                "pack": "division_2",
                "party": [
                  {
                    "armor": 5,
                    "effects": [
                      {
                        "active": false,
                        "id": "overwatch",
                        "values": {
                          "total_weapon_damage": 0.12
                        }
                      }
                    ],
                    "name": "Medic"
                  }
                ],
                "slots": {
                  "mask": {
                    "attributes": {
                      "core_1": ["armor_core", 170000]
                    },
                    "item": "mask_item"
                  }
                },
                "specializationNodes": ["a", "b"]
              }
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal), Text(BuildDocument.Encode(build)));
    }

    [Fact]
    public void TheSharePayloadIsTheDocumentBuildWithoutTheParty()
    {
        var shared = ShareCode.Decode(ShareCode.Encode(WithParty));
        Assert.Empty(shared.Party);
        Assert.Equal(Describe(BuildDocument.Decode(BuildDocument.Encode(WithParty))), Describe(shared));
    }

    [Fact]
    public void BuildsThatCannotBeSavedAreCallerErrorsNamingTheField()
    {
        static string Reject(BuildSnapshot build) => Assert.Throws<ArgumentException>(() => BuildDocument.Encode(build)).Message;
        Assert.StartsWith("build.pack is empty", Reject(WithParty with { PackId = "" }), StringComparison.Ordinal);
        Assert.StartsWith("build.party: party member \"medic\" is listed twice", Reject(WithParty with { Party = [.. WithParty.Party, new("medic", "", 0, [])] }), StringComparison.Ordinal);
        Assert.StartsWith("build.party: a party has at most 3 other members", Reject(WithParty with { Party = [.. Enumerable.Range(0, 4).Select(index => new PartyMember($"M{index}", "", 0, []))] }), StringComparison.Ordinal);
        Assert.StartsWith("build.party: party member \"A\" effect \"e\" value for \"armor\" must be finite",
            Reject(WithParty with { Party = [new("A", "", 0, [new("e", Map(("armor", double.PositiveInfinity)), false)])] }), StringComparison.Ordinal);
    }

    /// <summary>Decode refuses files over the limit, so Encode won't write one.</summary>
    [Fact]
    public void OversizedBuildsCannotBeSaved() => Assert.StartsWith("build is larger than",
        Assert.Throws<ArgumentException>(() => BuildDocument.Encode(WithParty with { Meta = new("Big", new string('x', BuildDocument.MaxBytes)) })).Message, StringComparison.Ordinal);

    /// <summary>Names and ids in party messages are escaped, so a message stays on one line and unambiguous.</summary>
    [Fact]
    public void PartyMessagesEscapeNames() => Assert.StartsWith("build.party: party member \"A\\\"B\\u000aC\" is listed twice",
        Assert.Throws<ArgumentException>(() => BuildDocument.Encode(WithParty with { Party = [new("a\"b\nc", "", 0, []), new("A\"B\nC", "", 0, [])] })).Message, StringComparison.Ordinal);

    [Fact]
    public void ByteOrderMarkIsIgnored() =>
        Assert.Equal(Describe(WithParty), Describe(BuildDocument.Decode((byte[])[0xEF, 0xBB, 0xBF, .. BuildDocument.Encode(WithParty)])));

    [Theory]
    [InlineData("[]", "build document is not an object")]
    [InlineData("""{"\ud800":1}""", "build document has a member name that is not valid text")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","slots":{"\udc00":{}}}}""", "build document build.slots has a member name that is not valid text")]
    [InlineData("""{"build":{"pack":"d"}}""", "build document has no schemaVersion")]
    [InlineData("""{"schemaVersion":"2","build":{"pack":"d"}}""", "build document schemaVersion is not a whole number")]
    [InlineData("""{"schemaVersion":1,"build":{"packId":"d"}}""", "build document is a v1 (Go-era) document; import it instead of opening it")]
    [InlineData("""{"schemaVersion":3,"extra":1}""", "build document was made by a newer version of the planner")]
    [InlineData("""{"schemaVersion":0}""", "unsupported build document schemaVersion 0")]
    [InlineData("""{"schemaVersion":2,"schemaVersion":2}""", "build document lists \"schemaVersion\" twice")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d"},"assumptions":{}}""", "build document has unknown member \"assumptions\"")]
    [InlineData("""{"schemaVersion":2}""", "build document has no build")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","level":40}}""", "build document build has unknown member \"level\"")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","party":{}}}""", "build document build.party is not a list")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","party":[{"code":"x"}]}}""", "build document build.party.0.name is missing")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","party":[{"name":"A","effects":[{"id":"e","active":1}]}]}}""", "build document build.party.0.effects.0.active is not true or false")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","party":[{"name":"A","armor":-1}]}}""", "build document build.party: party member \"A\" armor must be a finite, non-negative number")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","party":[{"name":"A","effects":[{"id":"e"},{"id":"e"}]}]}}""", "build document build.party: party member \"A\" lists effect \"e\" twice")]
    [InlineData("""{"schemaVersion":2,"build":{"pack":"d","party":[{"name":" "}]}}""", "build document build.party: party member names must not be blank")]
    public void DocumentIsChecked(string json, string error) =>
        Assert.Equal(error, Assert.Throws<BuildDocumentException>(() => BuildDocument.Decode(Encoding.UTF8.GetBytes(json))).Message);

    [Fact]
    public void MalformedAndOversizedInputIsRejected()
    {
        Assert.StartsWith("build document is not valid JSON", Assert.Throws<BuildDocumentException>(() => BuildDocument.Decode("{"u8.ToArray())).Message, StringComparison.Ordinal);
        Assert.StartsWith("build document is larger than", Assert.Throws<BuildDocumentException>(() => BuildDocument.Decode(new byte[BuildDocument.MaxBytes + 1])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOptionalPartyFieldsTakeTheirDefaults()
    {
        var member = BuildDocument.Decode("""{"schemaVersion":2,"build":{"pack":"d","party":[{"name":"A","effects":[{"id":"e"}]}]}}"""u8.ToArray()).Party.Single();
        Assert.Equal(("A", "", 0d), (member.Name, member.Code, member.Armor));
        Assert.Equal("e", member.Effects.Single().Id);
        Assert.False(member.Effects.Single().Active);
        Assert.Empty(member.Effects.Single().Values);
    }
}
