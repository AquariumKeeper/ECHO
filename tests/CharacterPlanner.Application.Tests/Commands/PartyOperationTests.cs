using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Legacy.Documents;
using static CharacterPlanner.Application.Tests.Commands.Division;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;

namespace CharacterPlanner.Application.Tests.Commands;

public sealed class PartyOperationTests
{
    private static ImmutableDictionary<string, double> Values(params (string Stat, double Value)[] values) =>
        values.ToImmutableDictionary(pair => pair.Stat, pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public void AddsAHandEnteredMemberWithBuffsOff()
    {
        var outcome = Apply(Empty(), new PartyMemberOperation("Medic"), new PartyMemberOperation("Tank", Armor: 1_000_000));
        Assert.True(outcome.Succeeded);
        Assert.Equal(["Medic", "Tank"], outcome.State.Build.Party.Select(member => member.Name));
        Assert.All(outcome.State.Build.Party, member => Assert.Empty(member.Effects));
        Assert.Equal([0d, 1_000_000d], outcome.State.Build.Party.Select(member => member.Armor));
        Assert.All(outcome.Changes, change => Assert.Equal(["party"], change.Set));
    }

    [Fact]
    public void TickingEvaluatesTheEffectForTheGiversArmorAndArmorChangesReEvaluateIt()
    {
        var build = Applied(Empty(),
            new PartyMemberOperation("Tank", Armor: 1_000_000),
            new PartyBuffOperation("tank", "vanguard", true),
            new PartyBuffOperation("TANK ", "overwatch", true));
        var member = Assert.Single(build.Party);
        Assert.Equal(
            [new("vanguard", Values(("party_bonus_armor", 450_000)), true), new("overwatch", Values(("total_weapon_damage", 0.12), ("total_skill_damage", 0.12)), true)],
            member.Effects, new PartyEffectComparer());

        build = Applied(build, new PartyMemberOperation("Tank", Armor: 2_000_000));
        Assert.Equal(900_000, build.Party[0].Effects[0].Values["party_bonus_armor"]);
        Assert.Equal(2_000_000, build.Party[0].Armor);

        build = Applied(build, new PartyBuffOperation("Tank", "vanguard", false), new PartyBuffOperation("Tank", "vanguard", false));
        Assert.Equal(["overwatch"], build.Party[0].Effects.Select(effect => effect.Id));

        var outcome = Apply(build, PartyMemberOperation.Cleared("tank"));
        Assert.Empty(outcome.State.Build.Party);
        Assert.Equal(["party"], outcome.Changes.Single().Cleared);
    }

    [Fact]
    public void ReReadingAHandEnteredMemberDropsEffectsThePackNoLongerHas()
    {
        var build = Empty().WithParty([new("Old", "", 1000, [new("retired_effect", Values(("armor", 1)), true), new("overwatch", Values(("total_weapon_damage", 0.5)), true)])]);
        var member = Applied(build, new PartyMemberOperation("Old")).Party.Single();
        Assert.Equal(1000, member.Armor);
        Assert.Equal([new("overwatch", Values(("total_weapon_damage", 0.12), ("total_skill_damage", 0.12)), true)], member.Effects, new PartyEffectComparer());
    }

    /// <summary>A giver with an item whose talent gives ally effect <paramref name="effectId"/>, shared in the app's format.</summary>
    private static string Giver(string effectId = "overwatch")
    {
        var talent = Rules.Talents.First(talent => talent.AllyEffects.Any(effect => effect.ID == effectId));
        var item = Rules.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(talent.ID)));
        var slot = item.TalentSlots.First(slot => slot.Choices.Contains(talent.ID));
        return ShareCode.Encode(Applied(Empty(), new EquipmentOperation(item.Slot, item.ID), new TalentOperation(item.Slot, slot.ID, talent.ID)));
    }

    /// <summary>The Go-era (v1–v12) code the frozen party-buffs batch adds a member from.</summary>
    private static string GoCode() => Json("builds", "party-buffs", "operations.json").EnumerateArray()
        .Single(operation => operation.GetProperty("kind").GetString() == OperationKinds.PartyMember && operation.TryGetProperty("text", out _))
        .GetProperty("text").GetString()!;

    [Fact]
    public void ShareCodeMembersOfferTheirGiversEffectsOffUntilTicked()
    {
        var code = Giver();
        var outcome = Apply(Empty(), new PartyMemberOperation("Coded", Text: $"  {code}\n"));
        Assert.True(outcome.Succeeded, string.Join(Environment.NewLine, outcome.Errors));
        Assert.Equal(["party"], outcome.Changes.Single().Set);
        var member = outcome.State.Build.Party.Single();
        Assert.Equal(code, member.Code);
        Assert.Equal(0, member.Armor);
        var offered = Assert.Single(member.Effects);
        Assert.Equal("overwatch", offered.Id);
        Assert.False(offered.Active);
        Assert.NotEmpty(offered.Values);
        Assert.All(offered.Values.Values, value => Assert.True(double.IsFinite(value)));

        var build = Applied(outcome.State.Build, new PartyBuffOperation("coded", "overwatch", true));
        Assert.True(build.Party[0].Effects[0].Active);
        Assert.Equal(Codes.PartyEffectNotOffered, Rejected(build, new PartyBuffOperation("Coded", "vanguard", true)).Code);
        Assert.Equal(Codes.PartyMemberNotHandEntered, Rejected(build, new PartyMemberOperation("Coded", Armor: 5)).Code);

        // Re-reading the code keeps ticked effects ticked; a code whose build gives nothing leaves no effects.
        Assert.Equal(build.Party, Applied(build, new PartyMemberOperation("Coded")).Party, new PartyMemberComparer());
        var plain = ShareCode.Encode(Empty());
        var replaced = Applied(build, new PartyMemberOperation("CODED", Text: plain)).Party.Single();
        Assert.Equal(("Coded", plain), (replaced.Name, replaced.Code));
        Assert.Empty(replaced.Effects);
        Assert.Empty(Applied(build, PartyMemberOperation.Cleared("coded")).Party);
    }

    [Fact]
    public void HandEnteredMembersCanBeReplacedByACode()
    {
        var build = Applied(Empty(), new PartyMemberOperation("Tank", Armor: 1_000_000), new PartyBuffOperation("Tank", "overwatch", true));
        var member = Applied(build, new PartyMemberOperation("Tank", Text: Giver())).Party.Single();
        Assert.Equal(0, member.Armor);
        Assert.True(member.Effects.Single().Active);
    }

    [Fact]
    public void UnreadableOrForeignCodesAreRejected()
    {
        var full = Empty();
        Assert.Equal(Codes.PartyShareCodeInvalid, Rejected(full, new PartyMemberOperation("A", Text: " ")).Code);
        Assert.Equal(Codes.PartyShareCodeInvalid, Rejected(full, new PartyMemberOperation("A", Text: "not-a-share-code")).Code);
        Assert.Equal(Codes.PartyShareCodeWrongPack, Rejected(full, new PartyMemberOperation("A", Text: ShareCode.Encode(Empty() with { PackId = "other" }))).Code);
        Assert.Equal(Codes.PartyCodeWithArmor, Rejected(full, new PartyMemberOperation("A", Text: Giver(), Armor: 5)).Code);
        // Without Legacy's reader, the applier accepts only the app's own format.
        var appOnly = new OperationApplier(Pack).Apply(State(), [new PartyMemberOperation("A", Text: GoCode())]);
        Assert.Equal(Codes.PartyShareCodeInvalid, Assert.Single(appOnly.Errors).Code);
    }

    /// <summary>Go-era formats are never written (ADR 0002): a member read from a Go code keeps its giver as an app code.</summary>
    [Fact]
    public void GoCodesAreStoredAsAppCodes()
    {
        var member = Applied(Empty(), new PartyMemberOperation("Old", Text: GoCode())).Party.Single();
        Assert.StartsWith("D2P1.", member.Code, StringComparison.Ordinal);
        var giver = ShareCodeReader.Read(GoCode(), Rules.LegacyShareOrder).Build;
        Assert.Equal(ShareCode.Payload(giver with { Party = [] }), ShareCode.Payload(ShareCode.Decode(member.Code)));
        // So an applier without Legacy's reader can still re-read the member.
        var reread = new OperationApplier(Pack).Apply(State(Empty().WithParty([member])), [new PartyMemberOperation("Old")]);
        Assert.True(reread.Succeeded, string.Join(Environment.NewLine, reread.Errors));
    }

    /// <summary>A weapon talent counts only for the weapon in hand, which a code doesn't say, so its effects are offered too.</summary>
    [Fact]
    public void WeaponTalentEffectsAreOffered()
    {
        var member = Applied(Empty(), new PartyMemberOperation("Gunner", Text: Giver("vindictive"))).Party.Single();
        Assert.Contains(member.Effects, effect => effect.Id == "vindictive" && !effect.Active);
    }

    [Fact]
    public void RejectsInvalidMembersAndBuffs()
    {
        var full = Applied(Empty(), new PartyMemberOperation("A"), new PartyMemberOperation("B"), new PartyMemberOperation("C"));
        Assert.Equal(Codes.PartyFull, Rejected(full, new PartyMemberOperation("D")).Code);
        Assert.Equal(3, Applied(full, new PartyMemberOperation("a", Armor: 7)).Party.Length);
        Assert.Equal(Codes.PartyMemberNameBlank, Rejected(full, new PartyMemberOperation(" ")).Code);
        Assert.Equal(Codes.PartyArmorInvalid, Rejected(full, new PartyMemberOperation("A", Armor: -1)).Code);
        Assert.Equal(Codes.UnknownPartyMember, Rejected(full, new PartyBuffOperation("D", "overwatch", true)).Code);
        Assert.Equal(Codes.UnknownPartyMember, Rejected(full, PartyMemberOperation.Cleared("D")).Code);
        Assert.Equal(Codes.UnknownAllyEffect, Rejected(full, new PartyBuffOperation("A", "luck", true)).Code);
    }

    private sealed class PartyMemberComparer : IEqualityComparer<PartyMember>
    {
        public bool Equals(PartyMember? x, PartyMember? y) => x is not null && y is not null && (x.Name, x.Code, x.Armor) == (y.Name, y.Code, y.Armor) &&
            x.Effects.SequenceEqual(y.Effects, new PartyEffectComparer());

        public int GetHashCode(PartyMember obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class PartyEffectComparer : IEqualityComparer<PartyEffect>
    {
        public bool Equals(PartyEffect? x, PartyEffect? y) => x is not null && y is not null && x.Id == y.Id && x.Active == y.Active &&
            x.Values.Count == y.Values.Count && x.Values.All(pair => y.Values.TryGetValue(pair.Key, out var value) && Math.Abs(value - pair.Value) < 1e-9);

        public int GetHashCode(PartyEffect obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
