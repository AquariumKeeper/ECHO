using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedSpecializationEffects(ImmutableArray<ModifierBatch> Batches, ImmutableArray<BuildDiagnostic> Diagnostics);

public static class SpecializationEffectSelection
{
    public static SelectedSpecializationEffects Select(GamePack pack, BuildSnapshot build)
    {
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        if (build.SpecializationId.Length == 0)
        {
            if (!pack.Specializations.IsEmpty) { diagnostics.Add(new("warning", "specialization-unconfigured", "No specialization is selected")); }
            if (!build.SpecializationNodeSelections.IsEmpty)
            { diagnostics.Add(new("error", "specialization-node-without-specialization", "Specialization nodes are selected without a specialization")); }
            return new([], diagnostics.ToImmutable());
        }
        var selected = pack.Specializations.FirstOrDefault(specialization => specialization.ID == build.SpecializationId);
        if (selected is null)
        { return new([], [new("error", "unknown-specialization", $"Unknown specialization {Quote(build.SpecializationId)}")]); }
        batches.Add(ModifierBatch.Specialization(selected));
        var nodes = selected.Nodes.ToDictionary(node => node.ID, StringComparer.Ordinal);
        var allocated = build.SpecializationNodeSelections.ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var id in allocated.Where(id => !nodes.ContainsKey(id)).Order(StringComparer.Ordinal))
        { diagnostics.Add(new("error", "unknown-specialization-node", $"Unknown specialization node {Quote(id)}", id)); }

        var status = SpecializationRules.Status(selected, allocated);
        // Costs count selected known nodes even if their prerequisites are inactive.
        // Overspending reports an error but does not suppress otherwise active effects.
        foreach (var budget in selected.Budgets)
        {
            var value = status.Spent.GetValueOrDefault(budget.ID);
            if (value > budget.Points)
            { diagnostics.Add(new("error", "specialization-budget-exceeded", $"{budget.Name} spends {Number(value)} points but allows {Number(budget.Points)}", budget.ID)); }
        }

        foreach (var node in selected.Nodes)
        {
            if (!allocated.Contains(node.ID)) { continue; }
            if (!status.Active.Contains(node.ID))
            { diagnostics.Add(new("error", "unmet-specialization-prerequisite", $"{node.Name} has an unselected or inactive prerequisite", node.ID)); continue; }
            batches.Add(ModifierBatch.SpecializationNode(selected, node));
        }
        return new(batches.ToImmutable(), diagnostics.ToImmutable());
    }
}

/// <summary>What a specialization's allocated nodes spend from each budget, and which of them are active.</summary>
public sealed record SpecializationStatus(ImmutableDictionary<string, long> Spent, ImmutableHashSet<string> Active);

/// <summary>A specialization tree's rules, shared by resolution and the editor.</summary>
public static class SpecializationRules
{
    /// <summary>
    /// The points spent from each budget (a node charges its budget and each parent budget once, whether or not it is
    /// active) and the active nodes: allocated ones whose prerequisites are all active.
    /// </summary>
    public static SpecializationStatus Status(SpecializationDefinition specialization, IEnumerable<string> allocated)
    {
        ArgumentNullException.ThrowIfNull(specialization);
        var selected = allocated.ToImmutableHashSet(StringComparer.Ordinal);
        var budgets = specialization.Budgets.GroupBy(budget => budget.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var nodes = specialization.Nodes.GroupBy(node => node.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var spent = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var node in specialization.Nodes.Where(node => selected.Contains(node.ID)))
        {
            var charged = new HashSet<string>(StringComparer.Ordinal);
            var id = node.BudgetID;
            while (id.Length > 0 && charged.Add(id))
            {
                spent[id] = spent.GetValueOrDefault(id) + node.Cost;
                id = budgets.GetValueOrDefault(id)?.ParentID ?? "";
            }
        }
        var order = DependencyOrder.Create(nodes.ToDictionary(pair => pair.Key, pair => pair.Value.Prerequisites.ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal));
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in order.Order)
        { if (selected.Contains(id) && nodes[id].Prerequisites.All(active.Contains)) { active.Add(id); } }
        return new(spent.ToImmutableDictionary(StringComparer.Ordinal), active.ToImmutableHashSet(StringComparer.Ordinal));
    }

    /// <summary>Whether an unallocated node can be allocated: its prerequisites are active, and its budget and each parent budget has room for its cost.</summary>
    public static bool CanAllocate(SpecializationDefinition specialization, SpecializationNodeDefinition node, SpecializationStatus status)
    {
        ArgumentNullException.ThrowIfNull(specialization);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(status);
        if (!node.Prerequisites.All(status.Active.Contains)) { return false; }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var id = node.BudgetID; id.Length > 0;)
        {
            var budget = specialization.Budgets.FirstOrDefault(budget => budget.ID == id);
            if (budget is null || !visited.Add(id) || status.Spent.GetValueOrDefault(id) + node.Cost > budget.Points) { return false; }
            id = budget.ParentID;
        }
        return true;
    }
}
