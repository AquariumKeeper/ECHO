using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// A driven fight names the gear set settings it leaves as set, on the committed pack: nothing else in its result says a Striker's fight
/// has no stacks (SIMULATOR_COVERAGE_SURVEY.md, 2026-11-24; ADR 0038 plans to drive them).
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string SetNote = "This fight doesn't drive these gear set settings, so their bonuses stay as you set them: ";

    private static BuildSnapshot SetBuild(GamePack pack, string set, int pieces, string weapon = "primary_weapon_acr")
    {
        var build = BuildSnapshot.Create(pack).WithEquipment("primary_weapon", weapon);
        var items = pack.Sets.Single(entry => entry.ID == set).Items;
        foreach (var group in pack.Items.Where(item => items.Contains(item.ID)).GroupBy(item => item.Slot).Take(pieces)) { build = build.WithEquipment(group.Key, group.First().ID); }
        return build;
    }

    private static string? SetNoteOf(CompiledPackExpressions pack, BuildSnapshot build, bool driven = true, bool fire = false, params (string Key, string Value)[] settings)
    {
        var context = settings.Aggregate(Context.Add("enemy_target", "1").Add("enemy_difficulty", "1"), (next, setting) => next.SetItem(setting.Key, setting.Value));
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = driven, IncludeIncoming = fire }, cancellationToken: TestContext.Current.CancellationToken);
        return result.Limitations.SingleOrDefault(note => note.StartsWith(SetNote, StringComparison.Ordinal))?[SetNote.Length..];
    }

    [Fact]
    public void ADrivenFightNamesTheSetSettingsItLeaves()
    {
        var pack = CommittedPack();
        var striker = SetBuild(pack.Pack, "gear_set_striker_s_battlegear", 4);
        var name = pack.Pack.NumericContextInputs.Single(input => input.ID == "striker_stacks").Name;
        Assert.Equal($"Striker's Battlegear ({name}: 0)", SetNoteOf(pack, striker));
        Assert.Equal($"Striker's Battlegear ({name}: 100)", SetNoteOf(pack, striker, settings: [("striker_stacks", "100")]));
        // At its maximum too, where the setting can only come down.
        Assert.Equal($"Striker's Battlegear ({name}: 200)", SetNoteOf(pack, striker, settings: [("striker_stacks", "200")]));
        // Without the four-piece bonus no setting moves anything, and a fight without talent triggers reads every setting anyway.
        Assert.Null(SetNoteOf(pack, SetBuild(pack.Pack, "gear_set_striker_s_battlegear", 3)));
        Assert.Null(SetNoteOf(pack, striker, driven: false));
        // A set whose key a fight drives isn't named.
        Assert.Null(SetNoteOf(pack, SetBuild(pack.Pack, "gear_set_apex_protocol", 4)));
    }

    [Fact]
    public void ASettingIsNamedOnlyWhereItMovesTheBuild()
    {
        // Eclipse's is gated on its backpack's Symptom Aggravator; a Pressure Point weapon drives the key itself.
        var pack = CommittedPack();
        var eclipse = SetBuild(pack.Pack, "gear_set_eclipse_protocol", 4);
        Assert.Null(SetNoteOf(pack, eclipse));
        var aggravator = WithTalent(pack.Pack, SetBuild(pack.Pack, "gear_set_eclipse_protocol", 6), "gear_talent_symptom_aggravator");
        var name = pack.Pack.Selectors.Single(selector => selector.ID == "target_status_affected").Name;
        Assert.Equal($"Eclipse Protocol ({name}: off)", SetNoteOf(pack, aggravator));
        Assert.Equal($"Eclipse Protocol ({name}: on)", SetNoteOf(pack, aggravator, settings: [("target_status_affected", "true")]));
        Assert.Null(SetNoteOf(pack, WithTalent(pack.Pack, aggravator, "weapon_talent_pressure_point")));
    }

    [Fact]
    public void SettingsNoFightUsesArentNamed()
    {
        // Makeshift Repairs' repairs come from the damage taken, Ground Control's and Umbra's Shadow repairs step aside, and True Patriot's
        // White would refuse the fight; the rest of each set's settings are named.
        var pack = CommittedPack();
        Assert.Null(SetNoteOf(pack, SetBuild(pack.Pack, "gear_set_foundry_bulwark", 4)));
        Assert.Null(SetNoteOf(pack, SetBuild(pack.Pack, "gear_set_foundry_bulwark", 4), fire: true));
        foreach (var (set, unused) in new[] { ("gear_set_future_initiative", "ground_control_eligible_ally_repair"), ("gear_set_umbra_initiative", "umbra_shadow_stacks_to_consume"), ("gear_set_true_patriot", "true_patriot_white_ready") })
        {
            var note = SetNoteOf(pack, SetBuild(pack.Pack, set, 4), fire: true);
            Assert.NotNull(note);
            var name = pack.Pack.Selectors.FirstOrDefault(selector => selector.ID == unused)?.Name ?? pack.Pack.NumericContextInputs.First(input => input.ID == unused).Name;
            Assert.DoesNotContain(name, note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingsThatActTogetherAreNamedAtTheirDefaults()
    {
        // Breaking Point's two and Umbra's two move nothing alone while the other is off; Breaking Point's need a rifle or an MMR in hand.
        var pack = CommittedPack();
        string Name(string id) => pack.Pack.Selectors.FirstOrDefault(selector => selector.ID == id)?.Name ?? pack.Pack.NumericContextInputs.First(input => input.ID == id).Name;
        var rifle = pack.Pack.Items.First(item => item.Slot == "primary_weapon" && item.Tags.Contains("weapon_type_rifle")).ID;
        var breaking = SetBuild(pack.Pack, "gear_set_breaking_point", 4, rifle);
        Assert.Equal($"Breaking Point ({string.Join(", ", new[] { $"{Name("on_point_active")}: off", $"{Name("on_point_stacks")}: 0" }.Order(StringComparer.Ordinal))})", SetNoteOf(pack, breaking));
        // Off shows for a stored false too.
        Assert.Contains($"{Name("on_point_active")}: off", SetNoteOf(pack, breaking, settings: [("on_point_active", "false")]), StringComparison.Ordinal);
        var umbra = SetNoteOf(pack, SetBuild(pack.Pack, "gear_set_umbra_initiative", 4));
        Assert.Contains(Name("umbra_light_stacks"), umbra, StringComparison.Ordinal);
        Assert.Contains(Name("umbra_light_active"), umbra, StringComparison.Ordinal);
    }

    [Fact]
    public void ARotationsSecondWeaponCounts()
    {
        // Aces & Eights reads a rifle or an MMR: with an assault rifle in hand first and a rifle second, its setting still moves the fight.
        var pack = CommittedPack();
        var rifle = pack.Pack.Items.First(item => item.Slot == "secondary_weapon" && item.Tags.Contains("weapon_type_rifle") && item.Weapon.Any(stat => stat.Stat == "weapon_switch_speed_multiplier")).ID;
        var build = SetBuild(pack.Pack, "gear_set_aces_eights", 4).WithEquipment("secondary_weapon", rifle);
        Assert.Null(SetNoteOf(pack, build));
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = new("secondary_weapon") }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.Limitations, note => note.StartsWith(SetNote + "Aces & Eights (", StringComparison.Ordinal));
    }

    [Fact]
    public void HeartbreakersPulseIsNamedWithItsStacks()
    {
        // Heartbreaker's damage needs the pulse, so both are named at their defaults.
        var pack = CommittedPack();
        var heart = SetNoteOf(pack, SetBuild(pack.Pack, "gear_set_heartbreaker", 4));
        Assert.Contains("Target is pulsed: off", heart, StringComparison.Ordinal);
        // A set that doesn't read the pulse isn't named for it, with Spotter reading it.
        var striker = SetNoteOf(pack, WithTalent(pack.Pack, SetBuild(pack.Pack, "gear_set_striker_s_battlegear", 6), "gear_talent_spotter"));
        Assert.Equal("Striker's Battlegear (Striker's Gamble Stacks: 0)", striker);
    }

    [Fact]
    public void ARotationNamesAWeaponWithoutASwapSpeed()
    {
        // Every weapon has one since R168, so here two lose theirs, on either side of the swap.
        var committed = CommittedPack();
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Items = [.. committed.Pack.Items.Select(item => item.ID is "primary_weapon_prima_donna" or "sidearm_busy_little_bee"
                ? item with { Weapon = [.. item.Weapon.Where(stat => stat.Stat != "weapon_switch_speed_multiplier")] } : item)]
        }).Compiled);
        foreach (var (first, second) in new[] { ("primary_weapon_prima_donna", "sidearm_d50"), ("primary_weapon_acr", "sidearm_busy_little_bee") })
        {
            var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", first).WithEquipment("sidearm", second);
            // With it, as committed, the rotation runs.
            Assert.True(DummyWeaponSimulator.Run(committed, build, Context, new() { Duration = 5, Rotation = new("sidearm") }, TestContext.Current.CancellationToken).ShotsFired > 0);
            var refused = Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(pack, build, Context, new() { Duration = 5, Rotation = new("sidearm") }, TestContext.Current.CancellationToken));
            Assert.Equal("weapon-stat-invalid", refused.Code);
            Assert.Contains(first == "primary_weapon_acr" ? "Busy Little Bee" : "Prima Donna", refused.Message, StringComparison.Ordinal);
        }
    }
}
