using System.Globalization;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Enemy targets and time to kill (F32), ported from Go's enemy_targets_g292_test.go. Go read the reference
/// targets from data/sources/division-2-npc-targets.json, which isn't part of this repository; the profiles and the
/// one- and four-player multipliers it used are copied here verbatim.
/// </summary>
public sealed class EnemyTargetTests
{
    private static readonly string[] Difficulties = ["Normal", "Hard", "Challenging", "Heroic", "Legendary", "Master"];

    private static readonly (int Index, string Role, double HealthBase, double ArmorBase)[] Profiles =
    [
        (1, "normal", 800.0, 0.0),
        (2, "normal", 1000.0, 1000.0),
        (3, "normal", 1200.0, 0.0),
        (4, "veteran", 1200.0, 1200.0),
        (5, "elite", 2400.0, 1200.0),
        (6, "elite", 3600.0, 3600.0),
        (7, "named", 800.0, 400.0),
        (8, "named", 800.0, 800.0),
        (9, "named", 1200.0, 1200.0),
        (10, "named", 1800.0, 1800.0),
        (11, "named", 2000.0, 2300.0),
        (12, "named", 2100.0, 1200.0),
        (13, "named", 2400.0, 1200.0),
    ];

    private static readonly (string Role, string Difficulty, int Players, double Health, double Armor)[] Multipliers =
    [
        ("normal", "Normal", 1, 663.6225290834745, 663.6225290834745),
        ("normal", "Normal", 4, 1148.066975314411, 663.6225290834745),
        ("normal", "Hard", 1, 1028.6149200793857, 1028.6149200793857),
        ("normal", "Hard", 4, 1779.5038117373372, 1028.6149200793857),
        ("normal", "Challenging", 1, 1393.6073110752968, 1393.6073110752968),
        ("normal", "Challenging", 4, 2410.9406481602637, 1393.6073110752968),
        ("normal", "Heroic", 1, 2355.859978246335, 2355.859978246335),
        ("normal", "Heroic", 4, 4075.637762366159, 2355.859978246335),
        ("normal", "Legendary", 1, 2588.127863425551, 2588.127863425551),
        ("normal", "Legendary", 4, 4477.461203726203, 2588.127863425551),
        ("normal", "Master", 1, 2986.301380875636, 2986.301380875636),
        ("normal", "Master", 4, 5166.30138891485, 2986.301380875636),
        ("veteran", "Normal", 1, 663.6225290834745, 829.5281613543432),
        ("veteran", "Normal", 4, 1148.066975314411, 1327.245058166949),
        ("veteran", "Hard", 1, 1028.6149200793857, 1285.7686500992322),
        ("veteran", "Hard", 4, 1779.5038117373372, 2057.229840158772),
        ("veteran", "Challenging", 1, 1393.6073110752968, 1742.009138844121),
        ("veteran", "Challenging", 4, 2410.9406481602637, 2787.2146221505936),
        ("veteran", "Heroic", 1, 2355.859978246335, 2944.8249728079186),
        ("veteran", "Heroic", 4, 4075.637762366159, 4711.71995649267),
        ("veteran", "Legendary", 1, 2588.127863425551, 3235.1598292819385),
        ("veteran", "Legendary", 4, 4477.461203726203, 5176.255726851102),
        ("veteran", "Master", 1, 2986.301380875636, 3732.876726094545),
        ("veteran", "Master", 4, 5166.30138891485, 5972.602761751273),
        ("elite", "Normal", 1, 663.6225290834745, 1659.0563227086864),
        ("elite", "Normal", 4, 1148.066975314411, 2820.3957486047666),
        ("elite", "Hard", 1, 1028.6149200793857, 2571.5373001984644),
        ("elite", "Hard", 4, 1779.5038117373372, 4371.61341033739),
        ("elite", "Challenging", 1, 1393.6073110752968, 3484.018277688242),
        ("elite", "Challenging", 4, 2410.9406481602637, 5922.831072070011),
        ("elite", "Heroic", 1, 2355.859978246335, 5889.649945615837),
        ("elite", "Heroic", 4, 4075.637762366159, 10012.404907546923),
        ("elite", "Legendary", 1, 2588.127863425551, 6470.319658563877),
        ("elite", "Legendary", 4, 4477.461203726203, 10999.543419558591),
        ("elite", "Master", 1, 2986.301380875636, 7465.75345218909),
        ("elite", "Master", 4, 5166.30138891485, 12691.780868721453),
        ("named", "Normal", 1, 663.6225290834745, 3981.7351745008473),
        ("named", "Normal", 4, 1148.066975314411, 9954.33793625212),
        ("named", "Hard", 1, 1028.6149200793857, 6171.689520476314),
        ("named", "Hard", 4, 1779.5038117373372, 15429.223801190785),
        ("named", "Challenging", 1, 1393.6073110752968, 8361.643866451781),
        ("named", "Challenging", 4, 2410.9406481602637, 20904.10966612945),
        ("named", "Heroic", 1, 2355.859978246335, 14135.159869478008),
        ("named", "Heroic", 4, 4075.637762366159, 35337.89967369502),
        ("named", "Legendary", 1, 2588.127863425551, 15528.767180553306),
        ("named", "Legendary", 4, 4477.461203726203, 38821.917951383264),
        ("named", "Master", 1, 2986.301380875636, 17917.808285253814),
        ("named", "Master", 4, 5166.30138891485, 44794.52071313454),
    ];

    /// <summary>G292: target health and armor scale by role, difficulty and group size, and no target has none.</summary>
    [Fact]
    public void G292TargetsScaleByRoleDifficultyAndGroup()
    {
        var b = Build();
        var checkedRows = 0;
        foreach (var profile in Profiles)
        {
            for (var index = 0; index < Difficulties.Length; index++)
            {
                foreach (var players in new[] { 1, 4 })
                {
                    var stats = Stats(b, Context(("enemy_target", profile.Index.ToString(CultureInfo.InvariantCulture)),
                        ("enemy_difficulty", (index + 1).ToString(CultureInfo.InvariantCulture)), ("enemy_players", players.ToString(CultureInfo.InvariantCulture))));
                    foreach (var row in Multipliers)
                    {
                        if (row.Role == profile.Role && row.Difficulty == Difficulties[index] && row.Players == players)
                        {
                            CloseTo(stats.Of("enemy_target_health"), profile.HealthBase * row.Health, 1e-4);
                            CloseTo(stats.Of("enemy_target_armor"), profile.ArmorBase * row.Armor, 1e-4);
                            checkedRows++;
                        }
                    }
                }
            }
        }
        Assert.Equal(Profiles.Length * Difficulties.Length * 2, checkedRows);
        var none = Stats(b, Context(("enemy_target", "0")));
        foreach (var id in new[] { "enemy_target_health", "enemy_target_armor", "equipped_weapon_target_bodyshots" })
        {
            CloseTo(none.Of(id), 0, 0);
        }
    }

    /// <summary>G292: time to kill uses the selected weapon's bodyshots, magazines and reloads.</summary>
    [Fact]
    public void G292TimeToKillUsesTheSelectedWeapon()
    {
        var b = Build().WithEquipment("primary_weapon", "primary_weapon_acr");
        (string, string)[] target = [("enemy_target", "3"), ("enemy_difficulty", "4"), ("enemy_players", "1")];
        var ctx = Context([(GamePackWeaponSlot, "primary_weapon"), .. target]);
        var stats = Stats(b, ctx);
        var damage = stats.Of("equipped_weapon_average_bodyshot_damage");
        var shots = Math.Ceiling(stats.Of("enemy_target_armor") / damage + stats.Of("enemy_target_health") / damage);
        CloseTo(stats.Of("equipped_weapon_target_bodyshots"), shots, 0);
        CloseTo(stats.Of("equipped_weapon_target_magazines"), shots / 30, 1e-12);
        var reloads = Math.Max(0, Math.Ceiling(shots / 30) - 1);
        CloseTo(stats.Of("equipped_weapon_target_time_to_kill"), (shots - 1) * 60 / 650 + reloads * 1.91, 1e-9);

        b = b.WithAttributeRoll("primary_weapon", "core_2", new("health_damage_weapon_core", 0.21));
        stats = Stats(b, ctx);
        Assert.True(stats.Of("equipped_weapon_target_bodyshots") < shots, $"Health Damage did not reduce bodyshots: {stats.Of("equipped_weapon_target_bodyshots")} >= {shots}");
        var unselected = Stats(b, Context([(GamePackWeaponSlot, "secondary_weapon"), .. target]));
        CloseTo(unselected.Of("equipped_weapon_target_bodyshots"), 0, 0);
        Assert.True(unselected.Of("enemy_target_health") > 0, "target health should not depend on the selected weapon");
    }

    private const string GamePackWeaponSlot = CharacterPlanner.Core.Packs.GamePack.WeaponSlotSelector;
}
