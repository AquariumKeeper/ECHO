using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record ResolvedBuild(
    ResolvedLoadout Loadout,
    StatResolution Calculation,
    ResolvedAssumptions Assumptions,
    ImmutableHashSet<string> Tags,
    ImmutableHashSet<string> SelectedTags,
    ImmutableArray<ActiveSet> ActiveSets,
    ImmutableDictionary<string, SkillPreview> ActiveSkillPreviews,
    ImmutableArray<ResolvedBuildOutput> BuildOutputs,
    ImmutableArray<BuildDiagnostic> Diagnostics)
{
    /// <summary>
    /// The assumptions the build's selected effects read: the selectors their modifiers are gated on (whether or not
    /// the gate is open, so it can be opened), the numeric inputs their formulas, skill previews and build outputs
    /// read, the Prototype normalization input when an item is Prototype, and every global numeric input. An
    /// assumptions view shows these and hides the rest (F46). The active weapon (weapon_slot) is its own control.
    /// </summary>
    public ImmutableHashSet<string> ReadAssumptions { get; init; } = [];
}

/// <summary>
/// Composes configured equipment, the active weapon, Prototype, upgrades, sets and specializations,
/// with party snapshots, talent copying, skill previews with local mods and build outputs.
/// </summary>
public static class BuildResolver
{
    public static ResolvedBuild Resolve(CompiledPackExpressions compiled, BuildSnapshot build, ImmutableDictionary<string, string>? context = null)
    {
        var pack = compiled.Pack;
        var assumptions = ResolveAssumptions.Resolve(pack, context);
        var loadout = LoadoutResolver.Resolve(pack, build);
        var diagnostics = assumptions.Diagnostics.AddRange(loadout.Diagnostics).ToBuilder();
        var tags = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var selectedTags = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var equippedItemIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var selectedSlot = assumptions.Explicit.GetValueOrDefault(GamePack.WeaponSlotSelector, "");
        foreach (var entry in loadout.Equipped)
        {
            tags.UnionWith(entry.Item.Tags);
            equippedItemIds.Add(entry.Item.ID);
            if (entry.SlotId == selectedSlot) { selectedTags.UnionWith(entry.Item.Tags); }
        }

        var equipment = EquipmentEffectSelection.Select(pack, build, loadout, assumptions);
        var upgrades = UpgradeEffectSelection.Select(pack, build, loadout, assumptions.Explicit);
        var prototypes = PrototypeEffectSelection.Select(pack, build, loadout, assumptions);
        diagnostics.AddRange(upgrades.AccountDiagnostics);
        diagnostics.AddRange(prototypes.Diagnostics);
        diagnostics.AddRange(upgrades.EquipmentDiagnostics);
        diagnostics.AddRange(equipment.Diagnostics);
        batches.AddRange(PartyEffectSelection.Select(pack, build).Select(ModifierBatch.Party));
        batches.AddRange(upgrades.AccountBatches);
        batches.AddRange(prototypes.Batches);
        batches.AddRange(upgrades.EquipmentBatches);
        batches.AddRange(equipment.Batches);
        batches.AddRange(TalentCopySelection.Select(pack, build, loadout, assumptions.Explicit));
        var sets = SetBonusSelection.Resolve(pack, equippedItemIds.ToImmutable(), equipment.SelectedTalents);
        batches.AddRange(sets.Batches);
        var activeSkillsSelection = ActiveSkillSelection.Select(pack, build);
        diagnostics.AddRange(activeSkillsSelection.Diagnostics);
        batches.AddRange(activeSkillsSelection.Batches);
        var specialization = SpecializationEffectSelection.Select(pack, build);
        diagnostics.AddRange(specialization.Diagnostics);
        batches.AddRange(specialization.Batches);
        var finalTags = tags.ToImmutable();
        var finalSelectedTags = selectedTags.ToImmutable();
        var scope = new EvaluationScope
        {
            Curves = pack.Curves,
            Tags = finalTags,
            SelectedTags = finalSelectedTags,
            SelectedTalents = equipment.SelectedTalents,
            ContextValues = assumptions.Numeric
        };
        var calculation = StatResolver.Resolve(compiled, batches.ToImmutable(), scope, assumptions.Explicit);
        diagnostics.AddRange(calculation.Diagnostics);
        var previews = SkillPreviewResolver.Resolve(compiled, build, activeSkillsSelection, scope with { Stats = calculation.Stats });
        diagnostics.AddRange(previews.Diagnostics);
        var outputs = BuildOutputResolver.Resolve(compiled, calculation.Stats, previews.Previews, activeSkillsSelection, scope);
        diagnostics.AddRange(outputs.Diagnostics);
        return new(loadout, calculation, assumptions, finalTags, finalSelectedTags, sets.ActiveSets, previews.Previews, outputs.Outputs, diagnostics.ToImmutable())
        {
            ReadAssumptions = ReadsOf(compiled, build, loadout, batches, activeSkillsSelection, equipment.SelectedTalents)
        };
    }

    private static ImmutableHashSet<string> ReadsOf(CompiledPackExpressions compiled, BuildSnapshot build, ResolvedLoadout loadout, IEnumerable<ModifierBatch> batches, SelectedActiveSkills skills,
        ImmutableHashSet<string> talents)
    {
        var pack = compiled.Pack;
        var reads = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        void Read(Expressions.Expression? expression) { if (expression is not null) { reads.UnionWith(expression.ContextReads); } }
        foreach (var batch in batches)
        {
            for (var index = 0; index < batch.Modifiers.Length; index++)
            {
                // The same rule StatResolver uses: a modifier that can never apply reads nothing.
                if (!StatResolver.Applies(batch, batch.Modifiers[index])) { continue; }
                if (batch.Modifiers[index].RequiresContext is { } gate) { reads.UnionWith(gate.Keys); }
                var path = $"{batch.OwnerPath}.modifiers[{index}]";
                Read(compiled.ModifierExpressions.GetValueOrDefault(path + ".when"));
                Read(compiled.ModifierExpressions.GetValueOrDefault(path + ".value"));
            }
        }
        foreach (var skill in skills.Accepted.Values)
        {
            foreach (var output in skill.PreviewOutputs)
            {
                var path = $"activeSkills.{skill.ID}.previewOutputs.{output.ID}";
                Read(compiled.SkillPreviewExpressions.GetValueOrDefault(path));
                Read(compiled.SkillPreviewExpressions.GetValueOrDefault(path + ".when"));
            }
        }
        // Derived stat formulas and build outputs are evaluated for every build.
        foreach (var formula in compiled.Formulas.Values) { Read(formula); }
        foreach (var output in compiled.BuildOutputExpressions.Values) { Read(output); }
        if (pack.Prototype is { NormalizeInput.Length: > 0 } prototype && loadout.Equipped.Any(entry => PrototypeRules.IsPrototype(pack, build, entry.SlotId, entry.Item)))
        { reads.Add(prototype.NormalizeInput); }
        reads.UnionWith(pack.NumericContextInputs.Where(input => input.Global).Select(input => input.ID));
        // A selected talent's fight rule that requires a selector (Calculated: in_cover) reads it, so the user sees what turns it on.
        reads.UnionWith(pack.Simulation.Triggers.Where(rule => rule.RequiresContext is not null && talents.Contains(rule.Talent)).SelectMany(rule => rule.RequiresContext!.Keys));
        reads.Remove(GamePack.WeaponSlotSelector);
        return reads.ToImmutable();
    }
}
