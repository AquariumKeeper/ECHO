using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>How a stat was worked out, from a resolution: what the info buttons show.</summary>
public static class StatBreakdown
{
    /// <summary>
    /// A stat's breakdown, as the Go app's popup: its formula in names, the weapon slot for a weapon stat, the base, each
    /// stat the formula reads, the bonuses with their sources, and the final value.
    /// </summary>
    public static StatDetailsViewModel? Of(CompiledPackExpressions compiled, ImmutableDictionary<string, StatDefinition> statistics, ResolvedBuild? resolved, string statId)
    {
        if (statistics.GetValueOrDefault(statId) is not { } stat) { return null; }
        var stats = resolved?.Calculation.Stats ?? ImmutableDictionary<string, double>.Empty;
        var breakdown = resolved?.Calculation.Breakdowns.GetValueOrDefault(statId);
        var lines = ImmutableArray.CreateBuilder<string>();
        if (stat.Formula.Length > 0) { lines.Add("Formula: " + ReadableFormula(stat.Formula, statistics)); }
        var weapon = resolved?.Assumptions.Explicit.GetValueOrDefault(GamePack.WeaponSlotSelector, "") ?? "";
        if (statId.StartsWith("equipped_weapon_", StringComparison.Ordinal) && weapon.Length > 0)
        { lines.Add($"Weapon Slot: {compiled.Pack.Slots.FirstOrDefault(slot => slot.ID == weapon)?.Name ?? weapon}"); }
        if (breakdown is not null) { lines.Add($"Base: {Formatting.Value(stat.Format, breakdown.Base)}"); }
        if (compiled.Formulas.TryGetValue(statId, out var formula))
        {
            foreach (var input in formula.Identifiers.Order(StringComparer.Ordinal).Select(statistics.GetValueOrDefault).OfType<StatDefinition>())
            { lines.Add($"{input.Name}: {Formatting.Value(input.Format, stats.GetValueOrDefault(input.ID))}"); }
        }
        if (breakdown is { Contributions.IsEmpty: false })
        {
            lines.Add("Bonuses");
            foreach (var contribution in breakdown.Contributions)
            {
                // On a percent stat, and for multipliers, the value reads as a percentage ("+12% Source").
                var percent = stat.Format == StatFormats.Percent || contribution.Op == ModifierOps.Mult;
                var text = Formatting.Delta(StatFormats.Decimal, percent ? contribution.Value * 100 : contribution.Value);
                lines.Add($"{text}{(percent ? "%" : "")} {contribution.Source.Name}");
            }
        }
        lines.Add($"Final: {(stats.TryGetValue(statId, out var final) ? Formatting.Value(stat.Format, final) : "–")}");
        return new(stat.Name, lines.ToImmutable());
    }

    /// <summary>A formula with stat ids read as names, quoted text and operators kept (Go's readableStatFormula).</summary>
    private static string ReadableFormula(string formula, ImmutableDictionary<string, StatDefinition> statistics) => System.Text.RegularExpressions.Regex.Replace(formula, @"'[^']*'|""[^""]*""|[A-Za-z_][A-Za-z_0-9]*",
        match => statistics.GetValueOrDefault(match.Value)?.Name ?? match.Value);
}
