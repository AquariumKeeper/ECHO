using System.Collections.Immutable;

namespace CharacterPlanner.Core.Packs;

public sealed record DependencyOrder(ImmutableArray<string> Order, ImmutableArray<string> Blocked)
{
    // Match the frozen Kahn ordering, including a sorted frontier after each step.
    // Blocked includes downstream consumers as well as nodes in the cycle itself.
    public static DependencyOrder Create(IReadOnlyDictionary<string, ImmutableHashSet<string>> dependencies)
    {
        var degree = dependencies.ToDictionary(pair => pair.Key, pair => pair.Value.Count(dependencies.ContainsKey), StringComparer.Ordinal);
        var dependents = dependencies.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (node, inputs) in dependencies)
        {
            foreach (var input in inputs.Where(dependencies.ContainsKey)) { dependents[input].Add(node); }
        }
        var frontier = new SortedSet<string>(degree.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var order = ImmutableArray.CreateBuilder<string>();
        while (frontier.Count > 0)
        {
            var node = frontier.Min!;
            frontier.Remove(node);
            order.Add(node);
            foreach (var dependent in dependents[node])
            { if (--degree[dependent] == 0) { frontier.Add(dependent); } }
        }
        return new(order.ToImmutable(), degree.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToImmutableArray());
    }
}
