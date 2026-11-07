using System.Buffers.Text;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Core.Tests;

public sealed class ShareCodeTests
{
    internal static ImmutableDictionary<string, T> Map<T>(params (string Key, T Value)[] pairs) =>
        pairs.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Every shared field, including values the resolver diagnoses: ids that differ from the slot's selection and configuration on an empty slot.</summary>
    internal static readonly BuildSnapshot Full = new()
    {
        PackId = "division_2",
        PackVersion = "y8s3.2",
        Equipment = Map(("mask", "mask_item"), ("chest", ""), ("primary_weapon", "rifle")),
        AttributeRolls = Map(("mask", Map(("core_1", new AttributeRoll("armor_core", 170000)), ("minor_1", new AttributeRoll("chc", 0.06)))), ("knees", Map(("core_1", new AttributeRoll("armor_core", 1.5))))),
        ModSelections = Map(("primary_weapon", Map(("muzzle", "compensator"), ("optics", "")))),
        ModRolls = Map(("primary_weapon", Map(("muzzle", 0.075)))),
        TalentSelections = Map(("primary_weapon", Map(("talent", "strained")))),
        EquipmentUpgrades = Map(("mask", new EquipmentUpgrade("mask_item", 3)), ("primary_weapon", new EquipmentUpgrade("old_rifle", 2))),
        Prototypes = Map(("mask", new PrototypeSelection("mask_item", "augment_a", 4)), ("chest", new PrototypeSelection("chest_item", "", 0))),
        ActiveSkillSelections = Map(("skill_slot_1", "turret_assault"), ("skill_slot_3", "")),
        SkillUpgrades = Map(("skill_slot_1", new SkillUpgrade("turret_assault", 2)), ("skill_slot_2", new SkillUpgrade("seeker", -1))),
        SkillMods = Map(("skill_slot_1", new SkillModSelection("turret_assault", Map(("ammo", new SkillModRoll("ammo_mod", 0.25))))), ("skill_slot_2", new SkillModSelection("seeker", Map<SkillModRoll>()))),
        SpecializationId = "technician",
        SpecializationNodeSelections = ["node_b", "node_a"],
        AccountUpgrades = Map(("shd_level", long.MaxValue), ("armor", 0L)),
        Party = [new PartyMember("Ally", "", 0, [])],
        Meta = new("Striker \U0001F600 Кирилл", "Notes <&> \"quoted\"\nline two\u0001")
    };

    private static string Payload(BuildSnapshot build) => Encoding.UTF8.GetString(ShareCode.Payload(build));

    /// <summary>Every snapshot field except party, independent of the share code's own normalisation.</summary>
    internal static string Describe(BuildSnapshot build)
    {
        static string Pairs<T>(IEnumerable<KeyValuePair<string, T>> values, Func<T, string> describe) =>
            "{" + string.Join(",", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + describe(pair.Value))) + "}";
        static string Nested<T>(ImmutableDictionary<string, ImmutableDictionary<string, T>> values) => Pairs(values, inner => Pairs(inner, value => $"{value}"));
        return string.Join("\n",
            build.PackId, build.PackVersion, build.Meta, build.SpecializationId, string.Join(",", build.SpecializationNodeSelections),
            Pairs(build.Equipment, item => item), Nested(build.AttributeRolls), Nested(build.ModSelections), Nested(build.ModRolls), Nested(build.TalentSelections),
            Pairs(build.EquipmentUpgrades, upgrade => $"{upgrade}"), Pairs(build.Prototypes, prototype => $"{prototype}"), Pairs(build.AccountUpgrades, rank => $"{rank}"),
            Pairs(build.ActiveSkillSelections, skill => skill), Pairs(build.SkillUpgrades, upgrade => $"{upgrade}"),
            Pairs(build.SkillMods, selection => selection.SkillId + Pairs(selection.Mods, roll => $"{roll}")));
    }

    [Fact]
    public void RoundTripKeepsEverySharedFieldExceptParty()
    {
        var code = ShareCode.Encode(Full);
        Assert.StartsWith("D2P1.", code, StringComparison.Ordinal);
        var decoded = ShareCode.Decode(code);
        Assert.Equal(Describe(Full), Describe(decoded));
        Assert.Empty(decoded.Party);
        Assert.Equal("", decoded.Equipment["chest"]);
        Assert.False(decoded.Equipment.ContainsKey("knees"));
        Assert.Equal("", decoded.ActiveSkillSelections["skill_slot_3"]);
        Assert.Equal("old_rifle", decoded.EquipmentUpgrades["primary_weapon"].ItemId);
        Assert.Equal("mask_item", decoded.EquipmentUpgrades["mask"].ItemId);
        Assert.Equal("chest_item", decoded.Prototypes["chest"].ItemId);
        Assert.Equal("seeker", decoded.SkillUpgrades["skill_slot_2"].SkillId);
        Assert.Empty(decoded.SkillMods["skill_slot_2"].Mods);
        Assert.Equal(["node_b", "node_a"], decoded.SpecializationNodeSelections);
        Assert.Equal(long.MaxValue, decoded.AccountUpgrades["shd_level"]);
        Assert.Equal(0.075, decoded.ModRolls["primary_weapon"]["muzzle"]);
        Assert.Equal(Full.Meta, decoded.Meta);
    }

    /// <summary>Pins the version 1 payload layout; changing it needs a new version.</summary>
    [Fact]
    public void PayloadLayoutIsPinned()
    {
        var build = new BuildSnapshot
        {
            PackId = "division_2",
            PackVersion = "1",
            Equipment = Map(("mask", "mask_item"), ("chest", "")),
            AttributeRolls = Map(("mask", Map(("core_1", new AttributeRoll("armor_core", 170000))))),
            EquipmentUpgrades = Map(("mask", new EquipmentUpgrade("mask_item", 3))),
            ActiveSkillSelections = Map(("skill_slot_1", "turret")),
            SkillMods = Map(("skill_slot_1", new SkillModSelection("turret", Map(("ammo", new SkillModRoll("ammo_mod", 0.25)))))),
            Meta = new("Build", "")
        };
        Assert.Equal(
            """{"name":"Build","pack":"division_2","packVersion":"1","skills":{"skill_slot_1":{"mods":{"rolls":{"ammo":["ammo_mod",0.25]}},"skill":"turret"}},"slots":{"chest":{"item":""},"mask":{"attributes":{"core_1":["armor_core",170000]},"item":"mask_item","upgrade":{"rank":3}}}}""",
            Payload(build));
    }

    [Fact]
    public void EqualBuildsGiveEqualPayloadsWhateverTheirInsertionOrder()
    {
        var reordered = Full with
        {
            Equipment = Full.Equipment.Reverse().ToImmutableDictionary(StringComparer.Ordinal),
            AccountUpgrades = ImmutableDictionary<string, long>.Empty.Add("armor", 0).Add("shd_level", long.MaxValue)
        };
        Assert.Equal(Payload(Full), Payload(reordered));
        Assert.Equal(ShareCode.Encode(Full), ShareCode.Encode(reordered));
    }

    [Fact]
    public void EmptyBuildNeedsOnlyItsPack()
    {
        var build = new BuildSnapshot { PackId = "division_2", Meta = new("", "") };
        Assert.Equal("""{"pack":"division_2"}""", Payload(build));
        Assert.Equal("", ShareCode.Decode(ShareCode.Encode(build)).Meta.Name);
    }

    /// <summary>Text is plain UTF-8; only what JSON requires is escaped, so names in any script stay compact.</summary>
    [Fact]
    public void TextIsEscapedOnlyWhereJsonRequires()
    {
        var payload = Payload(Full);
        Assert.Contains("\"name\":\"Striker \U0001F600 Кирилл\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"notes\":\"Notes <&> \\\"quoted\\\"\\nline two\\u0001\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildsThatCannotBeSharedAreCallerErrorsNamingTheField()
    {
        static string Reject(BuildSnapshot build) => Assert.Throws<ArgumentException>(() => ShareCode.Encode(build)).Message;
        Assert.StartsWith("build.pack is empty", Reject(Full with { PackId = "" }), StringComparison.Ordinal);
        Assert.StartsWith("build.slots.primary_weapon.modRolls.muzzle is not a finite number", Reject(Full with { ModRolls = Map(("primary_weapon", Map(("muzzle", double.NaN)))) }), StringComparison.Ordinal);
        Assert.StartsWith("build.name is not valid text", Reject(Full with { Meta = new("half \ud83d", "") }), StringComparison.Ordinal);
        Assert.StartsWith("build.slots.\udc00 is not valid text", Reject(Full with { Equipment = Map(("\udc00", "item")) }), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultNodeArrayIsShared() =>
        Assert.Empty(ShareCode.Decode(ShareCode.Encode(Full with { SpecializationNodeSelections = default })).SpecializationNodeSelections);

    [Theory]
    [InlineData("D2P1.abc", true)]
    [InlineData("  D2P12.abc\n", true)]
    [InlineData("D2P.abc", false)]
    [InlineData("D2Px.abc", false)]
    [InlineData("D2P1abc", false)]
    [InlineData("zFfLbuPIDv0XrhXD9u10", false)]
    public void AppCodesAreRecognizedByPrefix(string code, bool expected) => Assert.Equal(expected, ShareCode.IsAppCode(code));

    [Fact]
    public void WrappingIsChecked()
    {
        var code = ShareCode.Encode(Full);
        var body = code[5..];
        Assert.Equal(Full.Meta, ShareCode.Decode($"\t{code} \r\n").Meta);
        Assert.Equal("share code is empty", Reject(" "));
        Assert.StartsWith("share code is longer than", Reject("D2P1." + new string('A', ShareCode.MaxCodeLength)), StringComparison.Ordinal);
        Assert.Equal("share code does not start with D2P1.", Reject(body));
        Assert.Equal("share code was made by a newer version of the planner", Reject("D2P2." + body));
        Assert.Equal("share code was made by a newer version of the planner", Reject("D2P99999999999." + body));
        Assert.Equal("unsupported share code version 0", Reject("D2P0." + body));
        Assert.Equal("unsupported share code version 01", Reject("D2P01." + body));
        Assert.Equal("share code is not base64url text", Reject("D2P1." + body + "+"));
        // A changed character or a truncated paste fails the zlib checksum or the JSON parse.
        var flipped = body[..20] + (body[20] == 'A' ? 'B' : 'A') + body[21..];
        Assert.Equal("share code is damaged", Reject("D2P1." + flipped));
        // Bytes are cut or added, so the text stays valid base64url and the checksum must catch it.
        var bytes = Base64Url.DecodeFromChars(body);
        Assert.Equal("share code is damaged", Reject("D2P1." + Base64Url.EncodeToString(bytes[..^3])));
        Assert.Equal("share code is damaged", Reject("D2P1." + Base64Url.EncodeToString([.. bytes, 0, 0, 0])));
        Assert.StartsWith("share code expands to more than", Reject(Wrap(new byte[ShareCode.MaxPayloadBytes + 1])), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "share code build is not an object")]
    [InlineData("""{"packVersion":"1"}""", "share code build.pack is missing")]
    [InlineData("""{"pack":""}""", "share code build.pack is missing")]
    [InlineData("""{"pack":1}""", "share code build.pack is not text")]
    [InlineData("""{"pack":"d","party":[]}""", "share code build has unknown member \"party\"")]
    [InlineData("""{"pack":"d","pack":"e"}""", "share code build lists \"pack\" twice")]
    [InlineData("""{"pack":"d","slots":{"mask":{"item":"a","colour":1}}}""", "share code build.slots.mask has unknown member \"colour\"")]
    [InlineData("""{"pack":"d","slots":{"mask":{},"mask":{}}}""", "share code build.slots lists \"mask\" twice")]
    [InlineData("""{"pack":"d","slots":{"mask":{"attributes":{"core_1":["a"]}}}}""", "share code build.slots.mask.attributes.core_1 is not an [id, value] pair")]
    [InlineData("""{"pack":"d","slots":{"mask":{"modRolls":{"m":"1"}}}}""", "share code build.slots.mask.modRolls.m is not a finite number")]
    [InlineData("""{"pack":"d","slots":{"mask":{"upgrade":{}}}}""", "share code build.slots.mask.upgrade.rank is missing")]
    [InlineData("""{"pack":"d","slots":{"mask":{"upgrade":{"rank":1.5}}}}""", "share code build.slots.mask.upgrade.rank is not a whole number")]
    [InlineData("""{"pack":"d","slots":{"mask":{"prototype":{"level":1}}}}""", "share code build.slots.mask.prototype.augment is missing")]
    [InlineData("""{"pack":"d","skills":{"one":{"mods":{"skill":"s"}}}}""", "share code build.skills.one.mods.rolls is missing")]
    [InlineData("""{"pack":"d","specializationNodes":"a"}""", "share code build.specializationNodes is not a list")]
    [InlineData("""{"pack":"d","account":{"a":"1"}}""", "share code build.account.a is not a whole number")]
    [InlineData("""{"pack":"d","name":"\ud800"}""", "share code build.name is not valid text")]
    [InlineData("""{"pack":"d","\ud800":1}""", "share code build has a member name that is not valid text")]
    [InlineData("""{"pack":"d",""", "share code is damaged")]
    public void PayloadIsChecked(string payload, string error) => Assert.Equal(error, Reject(Wrap(Encoding.UTF8.GetBytes(payload))));

    private static string Reject(string code) => Assert.Throws<ShareCodeException>(() => ShareCode.Decode(code)).Message;

    private static string Wrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal)) { zlib.Write(payload); }
        return "D2P1." + Base64Url.EncodeToString(output.ToArray());
    }
}
