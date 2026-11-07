using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

/// <summary>The producer's per-talent source audit (R35): the optional talents[].coverage field and how coverage reports it.</summary>
public sealed class TalentCoverageTests
{
    private static readonly TalentCoverage Partial = new() { Status = "partial", Omits = ["per-ally 45 s cooldown"], Evidence = "REVIEW.md, Section" };

    private static GamePack Audited(TalentCoverage coverage, string talent = "talent_party") => TestPacks.Tiny with
    {
        Talents = [.. TestPacks.Tiny.Talents.Select(definition => definition.ID == talent ? definition with { Coverage = coverage } : definition)],
        Capabilities = [.. TestPacks.Tiny.Capabilities, PackCapabilities.TalentCoverage]
    };

    [Fact]
    public void CoverageRoundTripsWithoutChangingPacksThatOmitIt()
    {
        var pack = Audited(Partial with { Status = "unknown", SettleBy = "an armor reading on depletion" });
        var files = PackWriter.Write(pack);
        var result = PackValidator.Load(files);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(pack.Talents.Single(talent => talent.ID == "talent_party").Coverage,
            result.Compiled!.Pack.Talents.Single(talent => talent.ID == "talent_party").Coverage, new CoverageComparer());
        foreach (var (file, bytes) in files) { Assert.True(bytes.Span.SequenceEqual(PackWriter.Write(result.Compiled.Pack)[file].Span)); }
        Assert.DoesNotContain("\"coverage\"", Encoding.UTF8.GetString(PackWriter.Write(TestPacks.Tiny)[PackFiles.Rules].Span), StringComparison.Ordinal);
        Assert.Contains(PackCapabilities.TalentCoverage, PackCapabilities.Used(pack));
        Assert.DoesNotContain(PackCapabilities.TalentCoverage, PackCapabilities.Used(TestPacks.Tiny));
    }

    [Theory]
    [InlineData("complete", false, "", true)]
    [InlineData("utility", false, "", true)]
    [InlineData("partial", true, "", true)]
    [InlineData("unmodeled", true, "", true)]
    [InlineData("unknown", true, "a reading", true)]
    [InlineData("partial", false, "", false)]
    [InlineData("complete", true, "", false)]
    [InlineData("unknown", true, "", false)]
    [InlineData("partial", true, "a reading", false)]
    [InlineData("mostly", false, "", false)]
    public void OmitsAndSettleByFollowTheStatus(string status, bool omits, string settleBy, bool valid)
    {
        var coverage = new TalentCoverage { Status = status, Omits = omits ? ["something"] : [], Evidence = "REVIEW.md", SettleBy = settleBy };
        var result = PackValidator.Validate(Audited(coverage));
        if (valid) { Assert.Empty(result.Diagnostics); return; }
        Assert.Null(result.Compiled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "bad-coverage" && diagnostic.Path.StartsWith("talents.talent_party.coverage", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceAndOmissionPhrasesCannotBeBlank()
    {
        foreach (var coverage in new[] { Partial with { Evidence = " " }, Partial with { Omits = [" "] } })
        { Assert.Contains(PackValidator.Validate(Audited(coverage)).Diagnostics, diagnostic => diagnostic.Code == "bad-coverage"); }
    }

    private static TalentEffectCoverage Row(GamePack pack)
    {
        var build = BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a").WithTalentSelection("mask", "talent", "talent_party");
        return EffectCoverage.Describe(pack, build, ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon"))
            .Talents.Single(talent => talent.Talent == "talent_party");
    }

    [Fact]
    public void TheAuditIsReportedAlongsideTheModeledChannels()
    {
        var row = Row(Audited(Partial));
        var audit = Assert.Single(row.Channels, channel => channel.Kind == EffectCoverage.SourceAudit);
        Assert.Equal(["partial"], audit.Sources);
        Assert.Equal("partial", row.AuditStatus);
        Assert.True(row.Modeled);
        Assert.Contains("Source audit: the pack models part of the mechanic. Leaves out: per-ally 45 s cooldown. Evidence: REVIEW.md, Section.", row.Description, StringComparison.Ordinal);
        Assert.Contains("Source audit, partial: Party.", EffectCoverage.ComparisonDisclosure([new([row], ImmutableDictionary<string, string>.Empty)]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unmodeled", true)]
    [InlineData("utility", false)]
    public void AnAuditAloneModelsNothingButAUtilityTalentIsNotMissingAModel(string status, bool listedAsMissing)
    {
        var audited = Audited(new() { Status = status, Omits = status == "utility" ? [] : ["the whole effect"], Evidence = "REVIEW.md" });
        // The same talent with no modeled channel left: only the audit describes it.
        var pack = audited with
        {
            Talents = [.. audited.Talents.Select(talent => talent.ID == "talent_party"
                ? talent with { Modifiers = [], ModeledBySets = [], SetPieceCredit = 0, AllyEffects = [] } : talent)]
        };
        var row = Row(pack);
        Assert.False(row.Modeled);
        Assert.StartsWith(EffectCoverage.NoModel, row.Description, StringComparison.Ordinal);
        Assert.Equal(listedAsMissing, EffectCoverage.ComparisonDisclosure([new([row], ImmutableDictionary<string, string>.Empty)])
            .Contains("No modeled pack channel", StringComparison.Ordinal));
    }

    [Fact]
    public void TalentDamageIsAModeledChannel()
    {
        // Toxic Delivery's channel (R38): a talent with only a talent damage rule is modeled, not "No modeled effect".
        var bare = TestPacks.Tiny with
        {
            Talents = [.. TestPacks.Tiny.Talents.Select(talent => talent.ID == "talent_party"
                ? talent with { Modifiers = [], ModeledBySets = [], SetPieceCredit = 0, AllyEffects = [] } : talent)]
        };
        var pack = bare with { Simulation = bare.Simulation with { TalentDamage = [new() { Talent = "talent_party", On = "status_applied", Total = "armor", Duration = 10, Period = 0.5 }] } };
        var row = Row(pack);
        Assert.True(row.Modeled);
        Assert.Equal(["armor"], Assert.Single(row.Channels, channel => channel.Kind == EffectCoverage.TalentDamage).Sources);
        Assert.False(Row(bare).Modeled);
    }

    private sealed class CoverageComparer : IEqualityComparer<TalentCoverage?>
    {
        public bool Equals(TalentCoverage? x, TalentCoverage? y) => x is not null && y is not null && x.Status == y.Status &&
            x.Omits.SequenceEqual(y.Omits) && x.Evidence == y.Evidence && x.SettleBy == y.SettleBy;
        public int GetHashCode(TalentCoverage? obj) => obj?.Status.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
