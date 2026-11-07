using CharacterPlanner.Core.Builds;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>Assumption-driven named weapon talents, ported from Go's weapon_talents_g51_test.go.</summary>
public sealed partial class WeaponTalentTests
{
    /// <summary>Go's g51Contracts: hosts, legal slots and the stat each talent changes.</summary>
    private static readonly Dictionary<string, (string[] Ids, string[] Slots, string Stat)> G51Contracts = new(StringComparer.Ordinal)
    {
        ["Overflowing"] = (["primary_weapon_acr", "secondary_weapon_acr"], ["primary_weapon", "secondary_weapon"], "magazine_size_percent"),
        ["Perfect Overflowing"] = (["primary_weapon_manic", "secondary_weapon_manic"], ["primary_weapon", "secondary_weapon"], "magazine_size_percent"),
        ["Pakhan"] = (["primary_weapon_pakhan", "secondary_weapon_pakhan"], ["primary_weapon", "secondary_weapon"], "magazine_size_percent"),
        ["Eagle's Strike / Tenacity"] = (["primary_weapon_eagle_bearer", "secondary_weapon_eagle_bearer"], ["primary_weapon", "secondary_weapon"], "accuracy"),
        ["Restrained"] = (["sidearm_tempest"], ["sidearm"], "weapon_damage_multiplier"),
        ["Ortiz Assault Interface"] = (["primary_weapon_vindicator", "secondary_weapon_vindicator"], ["primary_weapon", "secondary_weapon"], "weapon_damage_multiplier"),
        ["Binary Trigger"] = (["primary_weapon_merciless", "secondary_weapon_merciless"], ["primary_weapon", "secondary_weapon"], "weapon_damage_multiplier"),
        ["Startling"] = (["primary_weapon_vertigo", "secondary_weapon_vertigo"], ["primary_weapon", "secondary_weapon"], "dtoc"),
    };

    private static BuildSnapshot G51Build(string name, string slot) => WeaponBuild(TalentId(name), slot, G51Contracts[name].Ids);

    private static BuildSnapshot G51WithoutTalent(BuildSnapshot b, string name, string slot) => SetTalent(b, slot, TalentId(name), "");

    private static readonly string[] G51UnchangedPreviews = ["weapon_rpm", "weapon_magazine_size", "weapon_reload_time", "weapon_optimal_range"];

    /// <summary>
    /// G51: each talent is legal on exactly its hosts and slots; Overflowing, generically, on assault rifles only.
    /// </summary>
    [Fact]
    public void G51StaticHostContracts()
    {
        foreach (var (name, contract) in G51Contracts)
        {
            var talentId = TalentId(name);
            var hosts = Hosts(talentId);
            if (name == "Overflowing")
            {
                foreach (var id in contract.Ids) { Assert.True(hosts.Contains(id), $"Overflowing missing representative host {id}"); }
                foreach (var item in Compiled.Pack.Items.Where(item => Allows(item, talentId)))
                {
                    Assert.True(item.Tags.Contains("weapon_type_assault_rifle"), $"Overflowing unexpectedly legal on non-assault rifle {item.ID}");
                }
                Assert.True(hosts.Length > 2, $"Overflowing expected generic assault-rifle compatibility, got {hosts.Length} hosts");
            }
            else
            {
                Assert.True(SameStrings(hosts, contract.Ids), $"{name} hosts = [{string.Join(", ", hosts)}], want exactly [{string.Join(", ", contract.Ids)}]");
            }
            Assert.True(SameStrings(LegalSlots(talentId), contract.Slots), $"{name} legal slots = [{string.Join(", ", LegalSlots(talentId))}], want [{string.Join(", ", contract.Slots)}]");
        }
    }

    /// <summary>G51: each talent's assumption changes its stat, with provenance, only while its owner weapon is in hand.</summary>
    [Fact]
    public void G51AssumptionsApplyOnlyToSelectedOwner()
    {
        (string Name, string Key, string Value, double Want)[] cases =
        [
            ("Overflowing", "overflowing_active", "true", 1),
            ("Perfect Overflowing", "overflowing_active", "true", 1),
            ("Pakhan", "pakhan_stacks", "4", 3),
            ("Eagle's Strike / Tenacity", "eagle_strike_accuracy_percent", "30", .30),
            ("Restrained", "restrained_active", "true", .25),
            ("Ortiz Assault Interface", "ortiz_highlighted_hit", "true", .60),
            ("Binary Trigger", "binary_trigger_empowered_shot", "true", 5),
            ("Startling", "startling_active", "true", .30),
        ];
        foreach (var (name, key, value, want) in cases)
        {
            var contract = G51Contracts[name];
            foreach (var slot in contract.Slots)
            {
                var what = $"{name}/{slot}";
                var b = G51Build(name, slot);
                var baselineBuild = G51WithoutTalent(b, name, slot);
                var baseline = Resolve(baselineBuild, On(slot));
                var active = Resolve(b, On(slot, (key, value)));
                Near(Delta(active, baseline, contract.Stat), want, 1e-12, $"{what}: {contract.Stat} delta");
                HasContribution(active, contract.Stat, TalentId(name), want, what);
                var wrongSlot = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
                var wrongContext = On(wrongSlot, (key, value));
                Near(Delta(Resolve(b, wrongContext), Resolve(baselineBuild, wrongContext), contract.Stat), 0, 1e-12, $"{what}: {contract.Stat} delta with {wrongSlot} in hand");
                var noSlot = Context((key, value));
                Near(Delta(Resolve(b, noSlot), Resolve(baselineBuild, noSlot), contract.Stat), 0, 1e-12, $"{what}: {contract.Stat} delta with no weapon in hand");
            }
        }
    }

    /// <summary>G51: missing, false or invalid flags change nothing, and the numeric inputs reject out-of-range values with a diagnostic.</summary>
    [Fact]
    public void G51InactiveFlagsAndNumericBounds()
    {
        foreach (var (name, key) in new[] { ("Overflowing", "overflowing_active"), ("Perfect Overflowing", "overflowing_active"), ("Restrained", "restrained_active"),
            ("Ortiz Assault Interface", "ortiz_highlighted_hit"), ("Binary Trigger", "binary_trigger_empowered_shot"), ("Startling", "startling_active") })
        {
            foreach (var slot in G51Contracts[name].Slots)
            {
                var b = G51Build(name, slot);
                var baseline = Resolve(G51WithoutTalent(b, name, slot), On(slot));
                ExpectInactiveFlag(b, slot, key, G51Contracts[name].Stat, baseline, $"{name}/{slot}");
            }
        }

        var pakhan = Compiled.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == "pakhan_stacks");
        var eagle = Compiled.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == "eagle_strike_accuracy_percent");
        Assert.True(pakhan is not null && eagle is not null, $"missing numeric input definitions pakhan_stacks={pakhan is not null} eagle_strike_accuracy_percent={eagle is not null}");
        Assert.NotNull(pakhan);
        Assert.NotNull(eagle);
        Assert.True(pakhan.Min == 0 && pakhan.Max == 4 && pakhan.Step == 1 && pakhan.Default == 0, $"pakhan_stacks definition = {pakhan.Min}..{pakhan.Max} step {pakhan.Step} default {pakhan.Default}");
        Assert.True(eagle.Min == 0 && eagle.Max == 30 && eagle.Step == 1 && eagle.Default == 0, $"eagle_strike_accuracy_percent definition = {eagle.Min}..{eagle.Max} step {eagle.Step} default {eagle.Default}");

        foreach (var (name, key, stat, invalid) in new[]
        {
            ("Pakhan", "pakhan_stacks", "magazine_size_percent", new[] { "-1", "1.5", "5", "NaN" }),
            ("Eagle's Strike / Tenacity", "eagle_strike_accuracy_percent", "accuracy", new[] { "-1", "1.5", "31", "NaN" }),
        })
        {
            var b = G51Build(name, "primary_weapon");
            var baseline = Resolve(G51WithoutTalent(b, name, "primary_weapon"), On("primary_weapon"));
            foreach (var value in invalid)
            {
                var what = $"{name} with {key}={value}";
                var result = Resolve(b, On("primary_weapon", (key, value)));
                ExpectInvalidContext(result, key, what);
                Near(Delta(result, baseline, stat), 0, 1e-12, what);
            }
        }
    }

    /// <summary>
    /// G51: the damage talents scale the weapon damage preview, and the others change only their raw totals, leaving
    /// the weapon's base previews alone.
    /// </summary>
    [Fact]
    public void G51WeaponPreviewEffectsAndRawTotals()
    {
        foreach (var (name, slot, key, value, factor) in new[]
        {
            ("Restrained", "sidearm", "restrained_active", "true", 1.25),
            ("Ortiz Assault Interface", "primary_weapon", "ortiz_highlighted_hit", "true", 1.60),
            ("Binary Trigger", "primary_weapon", "binary_trigger_empowered_shot", "true", 6),
        })
        {
            var b = G51Build(name, slot);
            var baseline = Resolve(G51WithoutTalent(b, name, slot), On(slot));
            var active = Resolve(b, On(slot, (key, value)));
            Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * factor, 1e-8, $"{name}: weapon damage preview");
            foreach (var stat in G51UnchangedPreviews)
            {
                Near(active.Calculation.Stats.Of(stat), baseline.Calculation.Stats.Of(stat), 1e-12, $"{name}: {stat}");
            }
        }
        foreach (var (name, slot, key, value) in new[]
        {
            ("Overflowing", "primary_weapon", "overflowing_active", "true"),
            ("Perfect Overflowing", "primary_weapon", "overflowing_active", "true"),
            ("Pakhan", "primary_weapon", "pakhan_stacks", "4"),
            ("Eagle's Strike / Tenacity", "primary_weapon", "eagle_strike_accuracy_percent", "30"),
            ("Startling", "primary_weapon", "startling_active", "true"),
        })
        {
            var b = G51Build(name, slot);
            var baseline = Resolve(G51WithoutTalent(b, name, slot), On(slot));
            var active = Resolve(b, On(slot, (key, value)));
            Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage"), 1e-12, $"{name}: weapon damage preview");
            foreach (var stat in G51UnchangedPreviews)
            {
                Near(active.Calculation.Stats.Of(stat), baseline.Calculation.Stats.Of(stat), 1e-12, $"{name}: {stat}");
            }
        }
    }

    /// <summary>G51: the talents add to a maximum roll of the same stat on their host.</summary>
    [Fact]
    public void G51EffectsComposeWithMatchingWeaponAttributeRolls()
    {
        foreach (var (name, itemId, attributeId, key, contextValue, stat, bonus) in new[]
        {
            ("Pakhan", "primary_weapon_pakhan", "magazine_size_weapon_minor", "pakhan_stacks", "1", "magazine_size_percent", .75),
            ("Eagle's Strike / Tenacity", "primary_weapon_eagle_bearer", "accuracy_weapon_minor", "eagle_strike_accuracy_percent", "15", "accuracy", .15),
            ("Startling", "primary_weapon_vertigo", "dtoc_weapon_minor", "startling_active", "true", "dtoc", .30),
        })
        {
            var b = G51Build(name, "primary_weapon");
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
            Assert.True(item is not null, $"missing host {itemId}");
            var attributeSlot = item.AttributeSlots.LastOrDefault(slot => slot.Choices.Any(choice => choice.AttributeID == attributeId));
            var max = Compiled.Pack.Attributes.Where(attribute => attribute.ID == attributeId).Select(attribute => attribute.Max).LastOrDefault();
            Assert.True(attributeSlot is not null && max > 0, $"missing {attributeId} roll contract on {itemId}");
            b = b.WithAttributeRoll("primary_weapon", attributeSlot.ID, new(attributeId, max));
            var baseline = Resolve(G51WithoutTalent(b, name, "primary_weapon"), On("primary_weapon"));
            var active = Resolve(b, On("primary_weapon", (key, contextValue)));
            Near(baseline.Calculation.Stats.Of(stat), max, 1e-12, $"{name}: rolled {stat}");
            Near(Delta(active, baseline, stat), bonus, 1e-12, $"{name}: {stat} delta");
            Near(active.Calculation.Stats.Of(stat), baseline.Calculation.Stats.Of(stat) + bonus, 1e-12, $"{name}: {stat}");
            HasContribution(active, stat, TalentId(name), bonus, name);
        }
    }

    /// <summary>G51: Pakhan adds 75% magazine size per stack and Eagle's Strike 1% accuracy per percent.</summary>
    [Fact]
    public void G51PakhanAndEagleStackValues()
    {
        foreach (var (name, key, stat, values, factor) in new[]
        {
            ("Pakhan", "pakhan_stacks", "magazine_size_percent", new[] { 0, 1, 2, 4 }, .75),
            ("Eagle's Strike / Tenacity", "eagle_strike_accuracy_percent", "accuracy", new[] { 0, 1, 15, 30 }, .01),
        })
        {
            var b = G51Build(name, "primary_weapon");
            var baseline = Resolve(G51WithoutTalent(b, name, "primary_weapon"), On("primary_weapon"));
            foreach (var value in values)
            {
                var what = $"{name} with {key}={value}";
                var active = Resolve(b, On("primary_weapon", (key, Count(value))));
                Near(Delta(active, baseline, stat), value * factor, 1e-12, what);
                HasContribution(active, stat, TalentId(name), value * factor, what);
            }
        }
    }

    /// <summary>G51: two equipped copies of Perfect Overflowing apply once.</summary>
    [Fact]
    public void G51DuplicateEquippedCopiesApplyOnce()
    {
        var talentId = TalentId("Perfect Overflowing");
        const string secondaryId = "secondary_weapon_manic";
        var b = G51Build("Perfect Overflowing", "primary_weapon").WithEquipment("secondary_weapon", secondaryId);
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == secondaryId).SelectMany(item => item.TalentSlots).LastOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(talentSlot is not null, "secondary Manic lacks Perfect Overflowing");
        b = b.WithTalentSelection("secondary_weapon", talentSlot.ID, talentId);
        var active = Resolve(b, On("primary_weapon", ("overflowing_active", "true")));
        var baseline = Resolve(G51WithoutTalent(b, "Perfect Overflowing", "primary_weapon"), On("primary_weapon"));
        Near(Delta(active, baseline, "magazine_size_percent"), 1, 1e-12, "magazine_size_percent delta");
        HasContribution(active, "magazine_size_percent", talentId, 1, "two copies");
    }

    /// <summary>G51: Binary Trigger's multiplier compounds with Glass Cannon's.</summary>
    [Fact]
    public void G51BinaryTriggerComposesWithGlassCannon()
    {
        var glassTalent = Compiled.Pack.Talents.LastOrDefault(talent => talent.Name == "Glass Cannon");
        Assert.True(glassTalent is not null, "missing Glass Cannon");
        var b = WithGearTalent(G51Build("Binary Trigger", "primary_weapon"), glassTalent.ID);
        var baseline = Resolve(G51WithoutTalent(b, "Binary Trigger", "primary_weapon"), On("primary_weapon"));
        var active = Resolve(b, On("primary_weapon", ("binary_trigger_empowered_shot", "true")));
        Near(baseline.Calculation.Stats.Of("weapon_damage_multiplier"), 1.25, 1e-12, "Glass Cannon multiplier");
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1.25 * 6, 1e-12, "composed multiplier");
        HasContribution(active, "weapon_damage_multiplier", TalentId("Binary Trigger"), 5, "Binary Trigger");
        Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * 6, 1e-8, "weapon damage preview");
    }

    /// <summary>G51: sharing keeps the talent selection and carries none of the combat assumptions.</summary>
    [Fact]
    public void G51TalentSharingDoesNotPersistAssumptions()
    {
        var b = G51Build("Pakhan", "primary_weapon");
        var encoded = ShareCode.Encode(b);
        var decoded = ShareCode.Decode(encoded);
        Assert.True(SameSelections(decoded.TalentSelections, b.TalentSelections),
            $"share did not preserve talent selection: decoded={Selections(decoded.TalentSelections)} build={Selections(b.TalentSelections)}");
        foreach (var key in new[] { "weapon_slot", "pakhan_stacks", "eagle_strike_accuracy_percent", "overflowing_active", "restrained_active", "ortiz_highlighted_hit", "binary_trigger_empowered_shot", "startling_active" })
        {
            Assert.False(encoded.Contains(key, StringComparison.Ordinal), $"share unexpectedly contains transient assumption \"{key}\"");
        }
    }
}
