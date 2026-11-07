using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Party buffs (F44), ported from Go's party_g394_test.go. Go passed the party to ResolveWithParty as evaluated
/// buffs; here the party lives on the build (WithParty) as members whose ticked effects carry their values, so an
/// effect must be one the pack defines: Go's ad hoc "overwatch"-stack effects become the pack's Overwatch and
/// Perfect Overwatch effects carrying the same total weapon damage values.
/// </summary>
public sealed class PartyTests
{
    private static PartyMember Member(string name, string effect, string stat, double value) =>
        new(name, "", 0, [new PartyEffect(effect, ImmutableDictionary<string, double>.Empty.Add(stat, value), true)]);

    /// <summary>
    /// G394: a giver's Vanguard grants the receiver a share of the giver's armor, the strongest buff per stack applies
    /// once, and no party changes nothing.
    /// </summary>
    [Fact]
    public void G394PartyBuffs()
    {
        var giver = BuildSnapshot.Create(Compiled.Pack, "Giver")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithTalentSelection("chest", "talent", "gear_talent_vanguard");
        var giverArmor = Stats(giver).Of("character_armor");

        var effects = PartyEffectSelection.EffectsOf(Compiled, giver, Context());
        Assert.True(effects.Length == 1 && effects[0].Id == "vanguard" && effects[0].Stack == "vanguard" && effects[0].Source == "Vanguard",
            $"effects = {string.Join("; ", effects)}");
        CloseTo(effects[0].Modifiers[0].Value, 0.45 * giverArmor, 1e-9);

        var unselected = giver.WithTalentSelection("chest", "talent", "");
        var found = PartyEffectSelection.EffectsOf(Compiled, unselected, Context());
        Assert.True(found.Length == 0, $"an unselected talent grants {string.Join("; ", found)}");

        var receiver = BuildSnapshot.Create(Compiled.Pack, "Receiver");
        var plain = Stats(receiver);
        var same = Stats(receiver.WithParty([]));
        foreach (var (id, value) in plain)
        {
            Assert.True(same.Of(id) == value, $"an empty party changes {id}");
        }

        var vanguard = effects[0].Modifiers[0];
        var buffed = Resolve(receiver.WithParty([Member("Giver", effects[0].Id, vanguard.Stat, vanguard.Value)]));
        CloseTo(buffed.Calculation.Stats.Of("party_bonus_armor"), 0.45 * giverArmor, 1e-9);
        CloseTo(buffed.Calculation.Stats.Of("estimated_bonus_armor") - plain.Of("estimated_bonus_armor"), 0.45 * giverArmor, 1e-9);
        var contributions = buffed.Calculation.Breakdowns["party_bonus_armor"].Contributions;
        Assert.True(contributions.Length == 1 && contributions[0].Source.Kind == "party" && contributions[0].Source.Name == "Party: Giver, Vanguard",
            $"breakdown = {string.Join("; ", contributions)}");

        var stacked = Stats(receiver.WithParty([
            Member("A", "overwatch", "total_weapon_damage", 0.12),
            Member("B", "perfect_overwatch", "total_weapon_damage", 0.14),
            Member("C", "overwatch", "total_weapon_damage", 0.12)]));
        CloseTo(stacked.Of("total_weapon_damage") - plain.Of("total_weapon_damage"), 0.14, 1e-12);
    }
}
