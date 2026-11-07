using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>Static (always-on) weapon talents scoped to their owner weapon, ported from Go's weapon_talents_g50_test.go.</summary>
public sealed partial class WeaponTalentTests
{
    /// <summary>Go's g50Contracts: hosts, legal slots, stat and bonus. Allegro's and Accurate's bonuses are in-game readings (G286).</summary>
    private static readonly Dictionary<string, (string[] Ids, string[] Slots, string Stat, double Bonus)> G50Contracts = new(StringComparer.Ordinal)
    {
        ["Perfect Allegro"] = (["primary_weapon_invisible_hand", "secondary_weapon_invisible_hand"], ["primary_weapon", "secondary_weapon"], "rate_of_fire", 0.15),
        ["Perfect Accurate"] = (["primary_weapon_the_railsplitter", "secondary_weapon_the_railsplitter"], ["primary_weapon", "secondary_weapon"], "accuracy", 0.80),
        ["Perfect Extra"] = (["primary_weapon_rock_n_roll", "secondary_weapon_rock_n_roll"], ["primary_weapon", "secondary_weapon"], "magazine_size_percent", 0.30),
        ["Perfect Distance"] = (["primary_weapon_the_send_off", "secondary_weapon_the_send_off"], ["primary_weapon", "secondary_weapon"], "optimal_range", 0.25),
        ["Perfect Jazz Hands"] = (["primary_weapon_cuelebre", "secondary_weapon_cuelebre"], ["primary_weapon", "secondary_weapon"], "reload_speed", 0.50),
        ["Perfect Optimized"] = (["primary_weapon_new_reliable", "secondary_weapon_new_reliable"], ["primary_weapon", "secondary_weapon"], "weapon_handling", 0.20),
        ["Lucky Shot"] = (["primary_weapon_acr_ss", "secondary_weapon_acr_ss", "primary_weapon_g28", "secondary_weapon_g28", "sidearm_d50"], ["primary_weapon", "secondary_weapon", "sidearm"], "magazine_size_percent", 0.20),
        ["Perfect Lucky Shot"] = (["primary_weapon_baker_s_dozen", "secondary_weapon_baker_s_dozen", "primary_weapon_incendia", "secondary_weapon_incendia"], ["primary_weapon", "secondary_weapon"], "magazine_size_percent", 0.30),
    };

    private static BuildSnapshot G50Build(string name, string slot) => WeaponBuild(TalentId(name), slot, G50Contracts[name].Ids);

    private static BuildSnapshot G50WithoutTalent(BuildSnapshot b, string name, string slot) => SetTalent(b, slot, TalentId(name), "");

    /// <summary>Go's reflect.DeepEqual of talent selections, field by field.</summary>
    private static bool SameSelections(ImmutableDictionary<string, ImmutableDictionary<string, string>> a, ImmutableDictionary<string, ImmutableDictionary<string, string>> b) =>
        a.Count == b.Count && a.All(slot => b.TryGetValue(slot.Key, out var other) && slot.Value.Count == other.Count
            && slot.Value.All(selection => other.TryGetValue(selection.Key, out var talent) && talent == selection.Value));

    private static string Selections(ImmutableDictionary<string, ImmutableDictionary<string, string>> selections) =>
        string.Join("; ", selections.Select(slot => $"{slot.Key}: {string.Join(", ", slot.Value.Select(selection => $"{selection.Key}={selection.Value}"))}"));

    /// <summary>
    /// G50: each static talent is legal on exactly its hosts and slots; Lucky Shot, broadly, on rifles, marksman
    /// rifles and pistols only.
    /// </summary>
    [Fact]
    public void G50StaticWeaponTalentContracts()
    {
        foreach (var (name, contract) in G50Contracts)
        {
            var talentId = TalentId(name);
            var hosts = Hosts(talentId);
            if (name == "Lucky Shot")
            {
                foreach (var id in contract.Ids) { Assert.True(hosts.Contains(id), $"Lucky Shot missing representative host {id}"); }
                string[] compatible = ["weapon_type_rifle", "weapon_type_marksman_rifle", "weapon_type_pistol"];
                foreach (var item in Compiled.Pack.Items.Where(item => Allows(item, talentId)))
                {
                    Assert.True(item.Tags.Any(compatible.Contains), $"Lucky Shot unexpectedly legal on {item.ID} ({string.Join(", ", item.Tags)})");
                }
                Assert.True(hosts.Length > 3, $"Lucky Shot expected broad host compatibility, got {hosts.Length} hosts");
                foreach (var excluded in new[] { "weapon_type_shotgun", "weapon_type_assault_rifle", "weapon_type_submachine_gun" })
                {
                    foreach (var item in Compiled.Pack.Items.Where(item => item.Tags.Contains(excluded) && Allows(item, talentId)))
                    {
                        Assert.Fail($"Lucky Shot unexpectedly legal on {excluded} host {item.ID}");
                    }
                }
            }
            else
            {
                Assert.True(SameStrings(hosts, contract.Ids), $"{name} hosts = [{string.Join(", ", hosts)}], want exactly [{string.Join(", ", contract.Ids)}]");
            }
            Assert.True(SameStrings(LegalSlots(talentId), contract.Slots), $"{name} legal slots = [{string.Join(", ", LegalSlots(talentId))}], want exactly [{string.Join(", ", contract.Slots)}]");
        }
    }

    /// <summary>G50: a static bonus applies, with talent provenance, only while its owner weapon is in hand.</summary>
    [Fact]
    public void G50StaticBonusesApplyOnlyToSelectedOwnerWeapon()
    {
        foreach (var (name, contract) in G50Contracts)
        {
            foreach (var slot in contract.Slots)
            {
                var what = $"{name}/{slot}";
                var b = G50Build(name, slot);
                var talentId = TalentId(name);
                var baselineBuild = G50WithoutTalent(b, name, slot);
                var baseline = Resolve(baselineBuild, On(slot));
                var active = Resolve(b, On(slot));
                Near(Delta(active, baseline, contract.Stat), contract.Bonus, 1e-12, $"{what}: {contract.Stat} delta");
                HasContribution(active, contract.Stat, talentId, contract.Bonus, what);
                var wrongSlot = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
                Near(Delta(Resolve(b, On(wrongSlot)), Resolve(baselineBuild, On(wrongSlot)), contract.Stat), 0, 1e-12, $"{what}: {contract.Stat} delta with {wrongSlot} in hand");
                Near(Delta(Resolve(b), Resolve(baselineBuild), contract.Stat), 0, 1e-12, $"{what}: {contract.Stat} delta with no weapon in hand");
            }
        }
    }

    /// <summary>G50: the static talents change their raw stats, not the weapon's derived base previews.</summary>
    [Fact]
    public void G50StaticTalentsDoNotAlterDerivedWeaponPreviews()
    {
        string[] unchanged = ["weapon_rpm", "weapon_magazine_size", "weapon_reload_time", "weapon_optimal_range", "equipped_weapon_damage"];
        foreach (var (name, contract) in G50Contracts)
        {
            foreach (var slot in contract.Slots)
            {
                var b = G50Build(name, slot);
                var baseline = Resolve(G50WithoutTalent(b, name, slot), On(slot));
                var active = Resolve(b, On(slot));
                foreach (var stat in unchanged)
                {
                    Near(active.Calculation.Stats.Of(stat), baseline.Calculation.Stats.Of(stat), 1e-12, $"{name}/{slot}: {stat}");
                }
            }
        }
    }

    /// <summary>
    /// G50: a second equipped copy of a static talent doesn't stack; sharing keeps the selection; and an unequipped
    /// weapon no longer contributes.
    /// </summary>
    [Fact]
    public void G50DuplicateCopiesAndShareTransientSelection()
    {
        const string name = "Perfect Allegro";
        var talentId = TalentId(name);
        const string secondaryId = "secondary_weapon_invisible_hand";
        var b = G50Build(name, "primary_weapon").WithEquipment("secondary_weapon", secondaryId);
        var secondaryTalentSlot = Compiled.Pack.Items.Where(item => item.ID == secondaryId).SelectMany(item => item.TalentSlots).LastOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(secondaryTalentSlot is not null, "secondary Invisible Hand lacks Perfect Allegro choice");
        b = b.WithTalentSelection("secondary_weapon", secondaryTalentSlot.ID, talentId);
        var single = G50Build(name, "primary_weapon");
        var twice = Resolve(b, On("primary_weapon"));
        var one = Resolve(single, On("primary_weapon"));
        Near(Delta(twice, one, "rate_of_fire"), 0, 1e-12, "two copies over one");
        HasContribution(twice, "rate_of_fire", talentId, 0.15, "two copies");

        var decoded = ShareCode.Decode(ShareCode.Encode(single));
        Assert.True(SameSelections(decoded.TalentSelections, single.TalentSelections),
            $"share did not preserve selected talent: decoded={Selections(decoded.TalentSelections)} single={Selections(single.TalentSelections)}");
        var cleared = decoded.WithEquipment("primary_weapon", "");
        Assert.False(ContributionValue(Resolve(cleared, On("primary_weapon")), "rate_of_fire", talentId).Found, "cleared weapon still contributes Perfect Allegro");
    }

    /// <summary>G50: Perfect Accurate adds to an accuracy roll already on the weapon.</summary>
    [Fact]
    public void G50PerfectAccurateComposesWithExistingRoll()
    {
        const string name = "Perfect Accurate";
        var talentId = TalentId(name);
        var b = G50Build(name, "primary_weapon");
        const string attributeId = "accuracy_weapon_minor";
        var attributeSlot = Compiled.Pack.Items.Where(item => item.ID == "primary_weapon_the_railsplitter").SelectMany(item => item.AttributeSlots)
            .LastOrDefault(slot => slot.Choices.Any(choice => choice.AttributeID == attributeId));
        Assert.True(attributeSlot is not null, "Railsplitter has no accuracy roll contract");
        var max = Compiled.Pack.Attributes.Where(attribute => attribute.ID == attributeId).Select(attribute => attribute.Max).LastOrDefault();
        b = b.WithAttributeRoll("primary_weapon", attributeSlot.ID, new(attributeId, max));
        var baseline = Resolve(G50WithoutTalent(b, name, "primary_weapon"), On("primary_weapon"));
        Assert.True(baseline.Calculation.Stats.Of("accuracy") > 0, $"accuracy roll did not affect baseline: accuracy = {baseline.Calculation.Stats.Of("accuracy")}");
        var active = Resolve(b, On("primary_weapon"));
        Near(Delta(active, baseline, "accuracy"), 0.80, 1e-12, "accuracy delta");
        HasContribution(active, "accuracy", talentId, 0.80, name);
        Near(active.Calculation.Stats.Of("accuracy"), baseline.Calculation.Stats.Of("accuracy") + 0.80, 1e-12, "accuracy");
    }
}
