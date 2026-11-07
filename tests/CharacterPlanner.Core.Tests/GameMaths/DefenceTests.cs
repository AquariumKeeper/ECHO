using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Incoming damage, effective HP and time to armor break (F33), ported from Go's effective_hp_g353_test.go and
/// incoming_damage_g358_test.go.
/// </summary>
public sealed class DefenceTests
{
    /// <summary>The pack with some stat formulas replaced, as Go's g353WithFormulas.</summary>
    private static CompiledPackExpressions WithFormulas(params (string Stat, string Formula)[] formulas)
    {
        var replaced = formulas.ToDictionary(formula => formula.Stat, formula => formula.Formula, StringComparer.Ordinal);
        var pack = Compiled.Pack with
        {
            Stats = [.. Compiled.Pack.Stats.Select(stat => replaced.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)],
        };
        var result = PackValidator.Validate(pack);
        return result.Compiled ?? throw new InvalidOperationException($"modified pack failed validation: {string.Join("; ", result.Diagnostics)}");
    }

    private static ImmutableDictionary<string, double> StatsOf(CompiledPackExpressions compiled, BuildSnapshot build, ImmutableDictionary<string, string>? context = null) =>
        BuildResolver.Resolve(compiled, build, context ?? ImmutableDictionary<string, string>.Empty).Calculation.Stats;

    /// <summary>G353: effective HP from the game's pools (Shade-Tech INCOMING_DAMAGE.md).</summary>
    [Fact]
    public void G353EffectiveHPPools()
    {
        var empty = Stats(Build());
        CloseTo(empty.Of("effective_damage_taken"), 1, 1e-12);
        CloseTo(empty.Of("effective_hp"), 270000, 1e-9);
        var stats = Stats(Build().WithEquipment("mask", "mask_n_5_11_tactical_mask"));
        CloseTo(stats.Of("effective_hp"), stats.Of("character_armor") + stats.Of("estimated_bonus_armor") + stats.Of("character_health"), 1e-9);
        CloseTo(stats.Of("effective_hp"), 350000, 1e-9);
    }

    /// <summary>G353: effective HP from the game's damage-taken caps (Shade-Tech INCOMING_DAMAGE.md).</summary>
    [Fact]
    public void G353EffectiveHPCaps()
    {
        var b = Build();
        var half = StatsOf(WithFormulas(("damage_resistance", "0.5")), b);
        CloseTo(half.Of("effective_damage_taken"), 0.5, 1e-12);
        CloseTo(half.Of("effective_hp"), 540000, 1e-6);
        var capped = StatsOf(WithFormulas(("damage_resistance", "0.95"), ("protection_from_elites", "0.9")), b);
        CloseTo(capped.Of("effective_damage_taken"), 0.1, 1e-12);
        CloseTo(capped.Of("effective_hp"), 2700000, 1e-4);
        CloseTo(capped.Of("effective_hp_vs_elites"), 2700000 / 0.2, 1e-3);
        var explosive = StatsOf(WithFormulas(("explosive_resistance", "0.4")), b);
        CloseTo(explosive.Of("effective_hp"), 270000, 1e-9);
        CloseTo(explosive.Of("effective_hp_vs_explosives"), 270000 / 0.6, 1e-6);
        CloseTo(explosive.Of("effective_hp_vs_elites"), 270000, 1e-9);
    }

    private static BuildSnapshot GearedBuild() => Build()
        .WithEquipment("mask", "mask_n_5_11_tactical_mask")
        .WithEquipment("chest", "chest_n_5_11_tactical_chest")
        .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
        .WithEquipment("gloves", "gloves_n_5_11_tactical_gloves")
        .WithEquipment("holster", "holster_n_5_11_tactical_holster")
        .WithEquipment("knees", "knees_n_5_11_tactical_kneepads");

    /// <summary>
    /// G358: the selected enemy's damage to the agent and the time to break its armor. (Go also logged the
    /// default target's figures with t.Logf; that log line isn't an assertion and is left out.)
    /// </summary>
    [Fact]
    public void G358IncomingDamageAndArmorBreak()
    {
        var b = GearedBuild();
        // Default target: Elite Hunters, Heroic, one player, 50% of shots hit.
        var stats = Stats(b);
        double hit = stats.Of("enemy_target_hit_damage"), dps = stats.Of("enemy_target_incoming_dps"), factor = stats.Of("enemy_damage_taken_factor");
        Assert.True(hit > 0 && dps > 0 && factor > 0 && factor <= 1, $"hit {hit}, incoming {dps}, factor {factor}");
        var armor = stats.Of("character_armor") + stats.Of("estimated_bonus_armor");
        CloseTo(stats.Of("time_to_armor_break"), armor / (dps * factor), 1e-9);
        CloseTo(stats.Of("time_to_down"), (armor + stats.Of("character_health")) / (dps * factor), 1e-9);

        // Doubling the hit rate halves the time; a second attacker doubles the damage.
        var full = Stats(b, Context(("enemy_hit_rate", "100")));
        CloseTo(full.Of("time_to_armor_break"), stats.Of("time_to_armor_break") / 2, 1e-9);
        var pair = Stats(b, Context(("enemy_attackers", "2")));
        CloseTo(pair.Of("enemy_target_incoming_dps"), dps * 2, 1e-6);

        // No target gives no estimate.
        var none = Stats(b, Context(("enemy_target", "0")));
        Assert.True(none.Of("time_to_armor_break") == 0 && none.Of("enemy_target_incoming_dps") == 0,
            $"no target: {none.Of("time_to_armor_break")} / {none.Of("enemy_target_incoming_dps")}");
    }

    /// <summary>G358: protection from elites applies to elite and named targets only.</summary>
    [Fact]
    public void G358EliteProtectionAppliesToEliteAndNamedTargets()
    {
        var compiled = WithFormulas(("protection_from_elites", "0.5"));
        var b = Build();
        var elite = StatsOf(compiled, b); // Elite Hunters
        CloseTo(elite.Of("enemy_damage_taken_factor"), 0.5, 1e-12);
        var named = StatsOf(compiled, b, Context(("enemy_target", "14"))); // Aaron Keener
        CloseTo(named.Of("enemy_damage_taken_factor"), 0.5, 1e-12);
        var normal = StatsOf(compiled, b, Context(("enemy_target", "3"))); // a Normal profile
        CloseTo(normal.Of("enemy_damage_taken_factor"), 1, 1e-12);
    }
}
