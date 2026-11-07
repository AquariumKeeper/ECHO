using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Application.Tests.Commands;

/// <summary>
/// Replays the six frozen `build apply` batches on an empty build of the committed v2 pack and compares the
/// result with the frozen response: the document field by field, and each operation's set and cleared paths.
/// Not compared, and why:
/// <list type="bullet">
/// <item>level, allocations, activeScenarios and allocatedSkills: v1-only document fields (ADR 0002); the v1 reader drops them.</item>
/// <item>Operations of removed kinds (the hard-wired-skills batch's "level"): rejected by design, see <see cref="OperationJsonTests"/>.</item>
/// <item>The response's stats, diagnostics and other results: resolution outputs, covered by the resolution tests.</item>
/// </list>
/// </summary>
public sealed class FrozenApplyTests
{
    /// <summary>A known difference's reason, and the comparisons it fails. Every other comparison must match.</summary>
    private sealed record Difference(string Reason, ImmutableArray<string> Fails);

    /// <summary>
    /// Builds whose replay differs from the frozen response. Exactly these comparisons must fail, and nothing else.
    /// None remain: the party-buffs member read from a Go share code now imports through Legacy's reader.
    /// </summary>
    private static readonly ImmutableDictionary<string, Difference> Differences = ImmutableDictionary<string, Difference>.Empty;

    public static TheoryData<string> Builds => [.. Division.Builds];

    [Fact]
    public void TheFixturesHold243OperationsAndEveryDifferenceNamesARealBuild()
    {
        Assert.Equal(243, Division.Builds.Sum(name => Division.Json("builds", name, "operations.json").GetArrayLength()));
        Assert.All(Differences.Keys, name => Assert.Contains(name, Division.Builds));
        Assert.All(Differences.Values, difference => Assert.NotEmpty(difference.Fails));
        Assert.Contains(Division.Json("builds", "party-buffs", "operations.json").EnumerateArray(),
            operation => operation.GetProperty("kind").GetString() == OperationKinds.PartyMember && operation.TryGetProperty("text", out _));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void ReplayMatchesTheFrozenDocumentAndChanges(string name)
    {
        var frozen = Division.Json("builds", name, "apply.json").GetProperty("response");
        var (operations, indices) = Replayable(Division.Json("builds", name, "operations.json"));
        var outcome = Division.Applier.Apply(Division.State(), operations);
        Assert.True(outcome.Succeeded, string.Join(Environment.NewLine, outcome.Errors));
        Assert.Empty(outcome.State.Assumptions);

        var mismatches = new List<(string Part, string Detail)>();
        // New builds use the installed delivery version; imported frozen builds retain their original version.
        Assert.Equal(Division.Rules.Version, outcome.State.Build.PackVersion);
        var expected = AppCodes(Division.Decode(frozen.GetProperty("document"))) with { PackVersion = outcome.State.Build.PackVersion };
        foreach (var (part, value) in Fields(expected).Zip(Fields(outcome.State.Build), (left, right) => (left.Part, (Expected: left.Value, Actual: right.Value))))
        {
            if (value.Expected != value.Actual) { mismatches.Add((part, $"expected {value.Expected}{Environment.NewLine}  actual {value.Actual}")); }
        }

        var changes = outcome.Changes.Select((change, index) => (Frozen: indices[index], Change: change)).ToDictionary(pair => pair.Frozen, pair => pair.Change);
        foreach (var change in frozen.GetProperty("result").GetProperty("changes").EnumerateArray())
        {
            var operation = change.GetProperty("operation").GetInt32();
            var kind = change.GetProperty("kind").GetString()!;
            if (OperationKinds.Removed.Contains(kind)) { continue; }
            var want = $"{kind} set [{Paths(change, "set")}] cleared [{Paths(change, "cleared")}]";
            var got = changes.TryGetValue(operation, out var actual)
                ? $"{actual.Kind} set [{string.Join(", ", actual.Set)}] cleared [{string.Join(", ", actual.Cleared)}]"
                : "not applied";
            if (want != got) { mismatches.Add(($"changes {operation}", $"expected {want}{Environment.NewLine}  actual {got}")); }
        }

        var fails = mismatches.Select(mismatch => mismatch.Part).Order(StringComparer.Ordinal).ToArray();
        var allowed = Differences.TryGetValue(name, out var difference) ? difference.Fails.Order(StringComparer.Ordinal).ToArray() : [];
        Assert.True(fails.SequenceEqual(allowed), $"{name} differs in [{string.Join(", ", fails)}], expected [{string.Join(", ", allowed)}]" +
            $"{(difference is null ? "" : $" ({difference.Reason})")}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, mismatches.Select(mismatch => $"{mismatch.Part}: {mismatch.Detail}")));
    }

    /// <summary>The operations to replay with their frozen 1-based indices: removed kinds are left out.</summary>
    private static (ImmutableArray<BuildOperation> Operations, int[] Indices) Replayable(JsonElement operations)
    {
        var kept = new JsonArray();
        var indices = new List<int>();
        var index = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            index++;
            var kind = operation.GetProperty("kind").GetString()!;
            if (OperationKinds.Removed.Contains(kind)) { continue; }
            kept.Add(JsonNode.Parse(operation.GetRawText())!);
            indices.Add(index);
        }
        return (Division.Read(kept.ToJsonString()), [.. indices]);
    }

    /// <summary>
    /// The frozen party keeps its members' Go-era codes; the app stores each giver as an app code instead, because
    /// Go-era formats are never written (ADR 0002). Converting the expected codes keeps every other field compared.
    /// </summary>
    private static BuildSnapshot AppCodes(BuildSnapshot build) => build.WithParty(build.Party.Select(member => member.Code.Length == 0 ? member
        : member with { Code = ShareCode.Encode(ShareCodeReader.Read(member.Code, Division.Rules.LegacyShareOrder).Build with { Party = [] }) }));

    private static string Paths(JsonElement change, string name) => string.Join(", ", change.GetProperty(name).EnumerateArray().Select(path => path.GetString()));

    /// <summary>The v2 build's fields, each as canonical JSON (object keys sorted), in document order.</summary>
    private static IEnumerable<(string Part, string Value)> Fields(BuildSnapshot build)
    {
        yield return ("packId", Canonical(build.PackId));
        yield return ("packVersion", Canonical(build.PackVersion));
        yield return ("equipment", Canonical(build.Equipment));
        yield return ("attributeRolls", Canonical(build.AttributeRolls));
        yield return ("modSelections", Canonical(build.ModSelections));
        yield return ("modRolls", Canonical(build.ModRolls));
        yield return ("talentSelections", Canonical(build.TalentSelections));
        yield return ("activeSkillSelections", Canonical(build.ActiveSkillSelections));
        yield return ("skillUpgrades", Canonical(build.SkillUpgrades));
        yield return ("skillMods", Canonical(build.SkillMods));
        yield return ("specializationId", Canonical(build.SpecializationId));
        yield return ("specializationNodeSelections", Canonical(build.SpecializationNodeSelections));
        yield return ("accountUpgrades", Canonical(build.AccountUpgrades));
        yield return ("equipmentUpgrades", Canonical(build.EquipmentUpgrades));
        yield return ("prototypes", Canonical(build.Prototypes));
        yield return ("party", Canonical(build.Party));
        yield return ("meta", Canonical(build.Meta));
    }

    private static string Canonical<T>(T value) => Sorted(JsonSerializer.SerializeToNode(value))?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value?.DeepClone())))),
        JsonArray value => new JsonArray([.. value.Select(item => Sorted(item?.DeepClone()))]),
        _ => node?.DeepClone()
    };
}
