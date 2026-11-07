namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// The reviewed community source profiles the Overcharge G-tests check against, transcribed from the Go repository's
/// data/sources caches (TU27 workbook, Skill List sheet): skill name, channel id, base, bonus (the capacity
/// source's extraCount), mode and source cell.
/// </summary>
public sealed partial class OverchargeTests
{
    /// <summary>The profiles of data/sources/division-2-skill-overcharge.json in the Go repository.</summary>
    private static readonly Channel[] DamageSource =
    [
        new("Burn Sticky Bomb", "damage", 995434, 0, "", "K29"),
        new("Burn Sticky Bomb", "burn_damage", 79635, 1, "", "K31"), // The source's base (R20); the workbook has 79,794.,
        new("Explosive Sticky Bomb", "damage", 1327245, 0.8, "", "K39"),
        new("Shrapnel Trap", "damage", 663623, 1, "", "K56"),
        new("Assault Turret", "damage", 26545, 1.75, "", "K88"),
        new("Incinerator Turret", "damage", 13272, 0.6, "", "K93"),
        new("Incinerator Turret", "burn_damage", 53090, 0.6, "", "K95"),
        new("Sniper Turret", "damage", 729985, 0.9, "", "K103"),
        new("Artillery Turret", "damage", 729985, 1.375, "", "K110"),
        new("Artillery Turret", "bleed_damage", 29863, 0, "", "K113"),
        new("Stinger Hive", "damage", 265449, 2, "", "K130"),
        new("Stinger Hive", "bleed_damage", 29863, 0, "", "K131"),
        new("Firestarter Chem Launcher", "damage", 265449, 0, "", "K173"),
        new("Firestarter Chem Launcher", "burn_damage", 79635, 0.6, "", "K174"),
        new("Oxidizer Chem Launcher", "cloud_damage", 53090, 0.8, "", "K188"),
        new("Burster Firefly", "damage", 663623, 1.375, "", "K200"),
        new("Demolisher Firefly", "damage", 1659056, 3, "", "K205"),
        new("Explosive Seeker Mine", "damage", 1061796, 0.8, "", "K209"),
        new("Explosive Seeker Mine", "bleed_damage", 29863, 0, "", "K211"),
        new("Airburst Seeker Mine", "damage", 331811, 0.6, "", "K217"),
        new("Airburst Seeker Mine", "burn_damage", 79635, 0.6, "", "K218"),
        new("Cluster Seeker Mine", "damage", 729985, 0.8, "", "K225"),
        new("Striker Drone", "damage", 16591, 1.75, "", "K239"),
        new("Bombardier Drone", "explosion_damage", 564079, 0.6, "", "K248"),
        new("Deflector Shield", "average_damage", 80465, 1, "", "K275"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-healing.json in the Go repository.</summary>
    private static readonly Channel[] HealingSource =
    [
        new("Repair Trap", "repair", 165906, 1.4, "", "K50"),
        new("Restorer Hive", "repair", 59726, 1.4, "", "K122"),
        new("Reinforcer Chem Launcher", "ally_repair", 199087, 1.4, "", "K165"),
        new("Reinforcer Chem Launcher", "repair_over_time", 39817, 1.4, "", "K165"),
        new("Mender Seeker Mine", "repair_rate", 17918, 1.6, "", "K231"),
        new("Fixer Drone", "repair", 17918, 1.75, "", "K255"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-geometry.json in the Go repository.</summary>
    private static readonly Channel[] GeometrySource =
    [
        new("Incinerator Turret", "range_meters", 15, 0, "percent", "K94"),
        new("Artillery Turret", "radius_meters", 4.5, 0.75, "percent", "K112"),
        new("Explosive Seeker Mine", "radius_meters", 4, 2, "percent", "K210"),
        new("Airburst Seeker Mine", "radius_meters", 6, 0, "percent", "K222"),
        new("Cluster Seeker Mine", "radius_meters", 2, 0, "percent", "K227"),
        new("Shrapnel Trap", "radius_meters", 2, 0, "percent", "K58"),
        new("Striker Shield", "buff_angle_reference_degrees", 45, 0, "percent", "K278"),
        new("Striker Shield", "buff_range_reference_meters", 10, 0, "percent", "K279"),
        new("Tactician Drone", "scan_range_meters", 50, 40, "add_meters", "K260"),
        new("Restorer Hive", "healing_range_meters", 8, 2, "percent", "K119"),
        new("Stinger Hive", "range_meters", 8, 1, "percent", "K127"),
        new("Reviver Hive", "range_meters", 8, 1, "percent", "K138"),
        new("Booster Hive", "range_meters", 8, 1, "percent", "K145"),
        new("Artificer Hive", "range_meters", 8, 2, "percent", "K153"),
        new("Reinforcer Chem Launcher", "healing_range_meters", 3, 1.5, "percent", "K163"),
        new("Firestarter Chem Launcher", "radius_meters", 3, 1.35, "percent", "K171"),
        new("Riot Foam Chem Launcher", "radius_meters", 3, 1.35, "percent", "K180"),
        new("Oxidizer Chem Launcher", "radius_meters", 3.5, 1, "percent", "K186"),
        new("Explosive Sticky Bomb", "radius_meters", 3, 2, "percent", "K40"),
        new("Precision Smart Cover", "radius_meters", 6, 1, "percent", "K15"),
        new("Fortified Smart Cover", "radius_meters", 6, 1, "percent", "K21"),
        new("Scanner Pulse", "radius_meters", 100, 0, "percent", "K67"),
        new("Remote Pulse", "radius_meters", 15, 1.2, "percent", "K71"),
        new("Jammer Pulse", "radius_meters", 20, 1.5, "percent", "K74"),
        new("Banshee Pulse", "radius_meters", 30, 0, "percent", "K83"),
        new("Banshee Pulse", "cone_angle_degrees", 20, 1.5, "percent", "K84"),
        new("Shock Trap", "radius_meters", 2.5, 0.7, "percent", "K45"),
        new("Repair Trap", "healing_range_meters", 0.75, 0, "percent", "K52"),
        new("EMP Sticky Bomb", "radius_meters", 4, 1.5, "percent", "K37"),
        new("Mender Seeker Mine", "healing_range_meters", 3, 1.5, "percent", "K232"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-capacity.json in the Go repository.</summary>
    private static readonly Channel[] CapacitySource =
    [
        new("Artillery Turret", "charge_capacity", 3, 10, "", "K111"),
        new("Burster Firefly", "target_capacity", 3, 10, "", "K197"),
        new("Cluster Seeker Mine", "deployable_capacity", 3, 8, "", "K226"),
        new("Bombardier Drone", "deployable_capacity", 3, 6, "", "K249"),
        new("Shrapnel Trap", "deployable_capacity", 9, 6, "", "K57"),
        new("Sniper Turret", "charge_capacity", 3, 15, "", "K104"),
        new("Restorer Hive", "charge_capacity", 8, 24, "", "K118"),
        new("Stinger Hive", "charge_capacity", 8, 6, "", "K126"),
        new("Reviver Hive", "charge_capacity", 1, 6, "", "K137"),
        new("Booster Hive", "charge_capacity", 8, 24, "", "K144"),
        new("Artificer Hive", "charge_capacity", 8, 24, "", "K152"),
        new("Blinder Firefly", "target_capacity", 3, 10, "", "K191"),
        new("Demolisher Firefly", "target_capacity", 3, 10, "", "K202"),
        new("Reinforcer Chem Launcher", "charge_capacity", 2, 6, "", "K161"),
        new("Firestarter Chem Launcher", "charge_capacity", 1, 6, "", "K169"),
        new("Riot Foam Chem Launcher", "charge_capacity", 1, 6, "", "K178"),
        new("Oxidizer Chem Launcher", "charge_capacity", 1, 6, "", "K184"),
        new("Achilles Pulse", "zone_capacity", 1, 5, "", "K76"),
        new("Shock Trap", "deployable_capacity", 6, 12, "", "K44"),
        new("Repair Trap", "deployable_capacity", 2, 6, "", "K51"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-lifetime.json in the Go repository.</summary>
    private static readonly Channel[] LifetimeSource =
    [
        new("Assault Turret", "duration_seconds", 180, 0, "", "K86"),
        new("Incinerator Turret", "duration_seconds", 15, 1, "", "K91"),
        new("Striker Drone", "duration_seconds", 180, 0, "", "K238"),
        new("Shrapnel Trap", "duration_seconds", 60, 0, "", "K59"),
        new("Sniper Turret", "duration_seconds", 300, 0, "", "K101"),
        new("Defender Drone", "duration_seconds", 20, 0, "", "K243"),
        new("Fixer Drone", "duration_seconds", 180, 0, "", "K254"),
        new("Tactician Drone", "duration_seconds", 300, 0, "", "K259"),
        new("Restorer Hive", "duration_seconds", 180, 0, "", "K120"),
        new("Stinger Hive", "duration_seconds", 180, 0, "", "K128"),
        new("Reviver Hive", "duration_seconds", 180, 0, "", "K139"),
        new("Booster Hive", "duration_seconds", 180, 0, "", "K146"),
        new("Artificer Hive", "duration_seconds", 180, 0, "", "K154"),
        new("Reinforcer Chem Launcher", "cloud_duration_seconds", 5, 1, "", "K164"),
        new("Firestarter Chem Launcher", "cloud_duration_seconds", 20, 0, "", "K172"),
        new("Oxidizer Chem Launcher", "cloud_duration_seconds", 5, 1.5, "", "K187"),
        new("Explosive Sticky Bomb", "duration_seconds", 10, 0, "", "K41"),
        new("Precision Smart Cover", "duration_seconds", 24, 0, "", "K17"),
        new("Fortified Smart Cover", "duration_seconds", 36, 0, "", "K23"),
        new("Decoy", "duration_seconds", 15, 0.9, "", "K62"),
        new("Scanner Pulse", "effect_duration_seconds", 8, 2, "", "K66"),
        new("Remote Pulse", "duration_seconds", 45, 1.2, "", "K70"),
        new("Banshee Pulse", "effect_duration_seconds", 10, 0, "", "K81"),
        new("Achilles Pulse", "effect_duration_seconds", 10, 1, "", "K78"),
        new("Shock Trap", "duration_seconds", 60, 0, "", "K47"),
        new("Repair Trap", "duration_seconds", 60, 0, "", "K53"),
        new("Burn Sticky Bomb", "duration_seconds", 10, 0, "", "K30"),
        new("EMP Sticky Bomb", "duration_seconds", 8, 1, "", "K35"),
        new("Mender Seeker Mine", "cloud_duration_seconds", 8, 0, "", "K233"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-status.json in the Go repository.</summary>
    private static readonly Channel[] StatusSource =
    [
        new("Artillery Turret", "bleed_duration_seconds", 7, 0, "", "K114"),
        new("Explosive Seeker Mine", "bleed_duration_seconds", 7, 0, "", "K212"),
        new("Airburst Seeker Mine", "burn_duration_seconds", 4, 4, "", "K219"),
        new("Stinger Hive", "bleed_duration_seconds", 7, 0, "", "K132"),
        new("Blinder Firefly", "blind_duration_seconds", 5, 1, "", "K194"),
        new("Firestarter Chem Launcher", "burn_duration_seconds", 4, 1, "", "K175"),
        new("Riot Foam Chem Launcher", "ensnare_duration_seconds", 6, 1, "", "K181"),
        new("Jammer Pulse", "disrupt_duration_seconds", 3, 1, "", "K73"),
        new("Banshee Pulse", "disorient_duration_seconds", 4, 1, "", "K82"),
        new("Shock Trap", "shock_duration_seconds", 3, 2, "", "K46"),
        new("Burn Sticky Bomb", "burn_duration_seconds", 4.5, 2, "", "K32"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-health.json in the Go repository.</summary>
    private static readonly Channel[] HealthSource =
    [
        new("Assault Turret", "skill_health", 1061796, 5.6, "", "K87"),
        new("Incinerator Turret", "skill_health", 1061796, 5.6, "", "K92"),
        new("Artillery Turret", "skill_health", 1061796, 5.6, "", "K109"),
        new("Burster Firefly", "skill_health", 663623, 0, "", "K198"),
        new("Explosive Seeker Mine", "skill_health", 199087, 0, "", "K208"),
        new("Airburst Seeker Mine", "skill_health", 199087, 0, "", "K216"),
        new("Cluster Seeker Mine", "skill_health", 199087, 0, "", "K224"),
        new("Striker Drone", "skill_health", 530898, 4.2, "", "K237"),
        new("Bombardier Drone", "skill_health", 1061796, 4.2, "", "K247"),
        new("Bulwark Shield", "skill_health", 2654490, 4, "", "K262"),
        new("Crusader Shield", "skill_health", 1327245, 2.5, "", "K267"),
        new("Deflector Shield", "skill_health", 2123592, 2.5, "", "K272"),
        new("Striker Shield", "skill_health", 1327245, 4, "", "K281"),
        new("Sniper Turret", "skill_health", 1061796, 5.6, "", "K102"),
        new("Defender Drone", "skill_health", 1061796, 5.6, "", "K242"),
        new("Fixer Drone", "skill_health", 1061796, 5, "", "K253"),
        new("Tactician Drone", "skill_health", 1061796, 5.6, "", "K258"),
        new("Restorer Hive", "skill_health", 796347, 4.2, "", "K121"),
        new("Stinger Hive", "skill_health", 796347, 4.2, "", "K129"),
        new("Reviver Hive", "skill_health", 796347, 4.2, "", "K140"),
        new("Booster Hive", "skill_health", 796347, 4.2, "", "K147"),
        new("Artificer Hive", "skill_health", 796347, 4.2, "", "K155"),
        new("Blinder Firefly", "skill_health", 663623, 0, "", "K192"),
        new("Demolisher Firefly", "skill_health", 663623, 0, "", "K203"),
        new("Riot Foam Chem Launcher", "foam_health_reference", 452470, 5, "", "K182"),
        new("Precision Smart Cover", "skill_health", 663623, 4.2, "", "K20"),
        new("Fortified Smart Cover", "skill_health", 663623, 4.2, "", "K27"),
        new("Decoy", "skill_health", 929072, 6, "", "K63"),
        new("Achilles Pulse", "zone_health_reference", 398174, 1, "", "K77"),
        new("Mender Seeker Mine", "skill_health", 199087, 0, "", "K234"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-utility.json in the Go repository.</summary>
    private static readonly Channel[] UtilitySource =
    [
        new("Burster Firefly", "speed_multiplier", 1, 2.5, "percent", "K199"),
        new("Booster Hive", "buff_amount_reference", 0.2, 4, "percent", "K148"),
        new("Artificer Hive", "buff_amount_reference", 0.1, 7, "percent", "K156"),
        new("Artificer Hive", "buff_duration_reference_seconds", 10, 0, "percent", "K157"),
        new("Artificer Hive", "skill_repair_reference", 0.1, 0, "percent", "K158"),
        new("Artificer Hive", "skill_refresh_reference_seconds", 3, 22, "add_seconds", "K159"),
        new("Blinder Firefly", "speed_multiplier", 1, 2.5, "percent", "K193"),
        new("Demolisher Firefly", "speed_multiplier", 1, 2.5, "percent", "K204"),
        new("Decoy", "threat_multiplier", 1, 2.5, "percent", "K64"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-recovery.json in the Go repository.</summary>
    private static readonly Channel[] RecoverySource =
    [
        new("Assault Turret", "cooldown_seconds", 25, 0, "", "K85"),
        new("Incinerator Turret", "cooldown_seconds", 30, 0, "", "K90"),
        new("Artillery Turret", "cooldown_seconds", 20, 0, "", "K107"),
        new("Burster Firefly", "cooldown_seconds", 30, 0, "", "K196"),
        new("Explosive Seeker Mine", "cooldown_seconds", 35, 0, "", "K207"),
        new("Airburst Seeker Mine", "cooldown_seconds", 30, 0, "", "K215"),
        new("Cluster Seeker Mine", "cooldown_seconds", 50, 0, "", "K223"),
        new("Striker Drone", "cooldown_seconds", 25, 0, "", "K236"),
        new("Bombardier Drone", "cooldown_seconds", 25, 0, "", "K246"),
        new("Shrapnel Trap", "cooldown_seconds", 60, 0, "", "K55"),
        new("Bulwark Shield", "cooldown_seconds", 20, 0, "", "K261"),
        new("Crusader Shield", "cooldown_seconds", 20, 0, "", "K266"),
        new("Deflector Shield", "cooldown_seconds", 20, 0, "", "K271"),
        new("Striker Shield", "cooldown_seconds", 20, 0, "", "K280"),
        new("Blinder Firefly", "cooldown_seconds", 50, 0, "", "K190"),
        new("Reinforcer Chem Launcher", "ammo_recovery_seconds", 30, 0, "", "K162"),
        new("Firestarter Chem Launcher", "ammo_recovery_seconds", 30, 0, "", "K170"),
        new("Riot Foam Chem Launcher", "ammo_recovery_seconds", 30, 0, "", "K179"),
        new("Oxidizer Chem Launcher", "ammo_recovery_seconds", 25, 0, "", "K185"),
        new("Explosive Sticky Bomb", "cooldown_seconds", 60, 5, "", "K38"),
        new("Precision Smart Cover", "cooldown_seconds", 45, 0, "", "K16"),
        new("Fortified Smart Cover", "cooldown_seconds", 45, 0, "", "K22"),
        new("Decoy", "cooldown_seconds", 25, 2, "", "K61"),
        new("Scanner Pulse", "cooldown_seconds", 20, 0, "", "K65"),
        new("Remote Pulse", "cooldown_seconds", 20, 0, "", "K69"),
        new("Jammer Pulse", "cooldown_seconds", 30, 0, "", "K72"),
        new("Achilles Pulse", "cooldown_seconds", 40, 1, "", "K79"),
        new("Shock Trap", "cooldown_seconds", 60, 0, "", "K43"),
        new("Repair Trap", "cooldown_seconds", 40, 0, "", "K49"),
        new("Burn Sticky Bomb", "cooldown_seconds", 60, 5, "", "K28"),
        new("EMP Sticky Bomb", "cooldown_seconds", 30, 0, "", "K34"),
    ];

    /// <summary>The profiles of data/sources/division-2-overcharge-support.json in the Go repository.</summary>
    private static readonly Channel[] SupportSource =
    [
        new("Restorer Hive", "refill_baseline_seconds", 11, 1.5, "haste_reference", "K117"),
        new("Stinger Hive", "refill_baseline_seconds", 10, 3, "haste_reference", "K125"),
        new("Reviver Hive", "refill_baseline_seconds", 180, 10, "haste_reference", "K136"),
        new("Reviver Hive", "revive_armor_tier_bonus_reference", 0.25, 0.75, "armor_reference", "K141"),
        new("Booster Hive", "refill_baseline_seconds", 11.3, 1.5, "haste_reference", "K143"),
        new("Artificer Hive", "refill_baseline_seconds", 11.3, 1.5, "haste_reference", "K151"),
        new("Jammer Pulse", "charge_time_reference_seconds", 2, 0, "time_reference", "K75"),
    ];
}
