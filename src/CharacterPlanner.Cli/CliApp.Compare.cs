using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Cli;

internal static partial class CliApp
{
    private static bool ContextOption(string[] rest, out string? context)
    {
        context = rest is ["--context", var path] ? path : null;
        return rest.Length == 0 || context is not null;
    }

    private static async Task<int> ShareBuildAsync(string directory, string path, TextWriter output, CancellationToken cancellationToken = default)
    {
        // Encoding needs only the pack's identity, so artwork that hasn't been delivered doesn't block it.
        var validation = await LoadPackAsync(directory, metadataOnly: true, cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var build = await ReadBuildAsync(path, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        string code;
        try { code = ShareCode.Encode(build); }
        catch (ArgumentException exception) { throw new CliInputException("build-unshareable", path, exception.Message); }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new { code, name = build.Meta.Name, packVersion = build.PackVersion },
            diagnostics = Diagnostics(validation.Diagnostics)
        }));
        return Success;
    }

    /// <summary>Both builds under one context, as the desktop's Compare Builds does; the context is checked against the first.</summary>
    private static async Task<int> CompareBuildsAsync(string directory, string first, string second, string? contextPath, TextWriter output, CancellationToken cancellationToken = default)
    {
        var validation = await LoadPackAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var a = await ReadBuildAsync(first, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        var b = await ReadBuildAsync(second, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        var initialized = await ReadStateAsync(a, contextPath, new OperationApplier(compiled), cancellationToken).ConfigureAwait(false);
        if (!initialized.Succeeded) { return OperationFailure(output, contextPath ?? first, initialized.Errors); }
        var comparison = BuildComparison.Compare(compiled, a, b, initialized.State.Assumptions);
        var problems = new[] { (comparison.A, first), (comparison.B, second) }.Where(side => !side.Item1.Valid)
            .Select(side => new { severity = "error", code = "compare-build-invalid", path = side.Item2, message = side.Item1.Problem! }).ToArray();
        if (problems.Length > 0) { return Fail(output, Diagnostics(validation.Diagnostics).Concat(problems)); }
        if (comparison.Stats.FirstOrDefault(stat => !double.IsFinite(stat.Delta)) is { } overflow)
        { return Fail(output, Diagnostics(validation.Diagnostics).Append(new { severity = "error", code = "compare-value-overflow", path = "", message = $"The change in {overflow.Stat.ID} is outside the representable range" })); }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                pack = new { id = compiled.Pack.ID, version = compiled.Pack.Version },
                changed = comparison.Stats.Count(stat => stat.Delta != 0),
                coverage = new { a = CoverageValue(comparison.A.Coverage), b = CoverageValue(comparison.B.Coverage) },
                stats = comparison.Stats.Select(stat => new { id = stat.Stat.ID, name = stat.Stat.Name, format = stat.Stat.Format, a = stat.A, b = stat.B, delta = stat.Delta })
            },
            diagnostics = Diagnostics(validation.Diagnostics)
        }));
        return Success;
    }

    /// <summary>The desktop's Rank Upgrades. Each upgrade carries operation JSON that build apply accepts.</summary>
    private static async Task<int> RankUpgradesAsync(string directory, string path, string slot, string stat, string? contextPath, TextWriter output, CancellationToken cancellationToken = default)
    {
        var validation = await LoadPackAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation.Compiled is not { } compiled) { return Fail(output, Diagnostics(validation.Diagnostics)); }
        var build = await ReadBuildAsync(path, compiled.Pack.ID, cancellationToken).ConfigureAwait(false);
        var applier = new OperationApplier(compiled);
        var initialized = await ReadStateAsync(build, contextPath, applier, cancellationToken).ConfigureAwait(false);
        if (!initialized.Succeeded) { return OperationFailure(output, contextPath ?? path, initialized.Errors); }
        var session = new PlannerSession(compiled, initialized.State, applier);
        var ranking = session.RankUpgrades(session.Snapshot, slot, stat, cancellationToken);
        if (ranking.Problem is { } problem)
        { return Fail(output, Diagnostics(validation.Diagnostics).Append(new { severity = "error", code = "rank-unavailable", path, message = problem })); }
        if (ranking.Upgrades.FirstOrDefault(upgrade => !double.IsFinite(upgrade.Delta)) is { } overflow)
        { return Fail(output, Diagnostics(validation.Diagnostics).Append(new { severity = "error", code = "rank-value-overflow", path = "", message = $"The change from {overflow.Id} is outside the representable range" })); }
        // Ranking judges a weapon slot as the weapon in hand; this is the context the operations reproduce `after` under.
        var ranked = compiled.Pack.Slots.Any(definition => definition.ID == slot && definition.Weapon)
            ? initialized.State.Assumptions.SetItem(GamePack.WeaponSlotSelector, slot)
            : initialized.State.Assumptions;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                pack = new { id = compiled.Pack.ID, version = compiled.Pack.Version },
                slot,
                stat = ranking.Stat,
                context = TypedContext(compiled.Pack, ranked),
                before = ranking.Before,
                coverage = CoverageValue(ranking.Coverage),
                upgrades = ranking.Upgrades.Select(upgrade => new
                {
                    kind = JsonNamingPolicy.CamelCase.ConvertName(upgrade.Kind.ToString()),
                    target = upgrade.Target,
                    targetName = upgrade.TargetName,
                    id = upgrade.Id,
                    name = upgrade.Name,
                    value = upgrade.Value,
                    valueFormat = upgrade.ValueFormat,
                    reroll = upgrade.Reroll,
                    before = upgrade.Before,
                    after = upgrade.After,
                    delta = upgrade.Delta,
                    coverage = CoverageValue(upgrade.Coverage),
                    operations = upgrade.Operations.Select(RankOperation)
                })
            },
            diagnostics = Diagnostics(validation.Diagnostics)
        }));
        return Success;
    }

    // Ranking only sets choices and rolls, so these are the kinds it produces; the fields follow OperationJson.
    private static Dictionary<string, object> RankOperation(BuildOperation operation)
    {
        var json = operation switch
        {
            AttributeOperation { Clear: false } attribute => new Dictionary<string, object> { ["kind"] = OperationKinds.Attribute, ["slot"] = attribute.Slot, ["target"] = attribute.Target, ["id"] = attribute.Id },
            ModOperation { Clear: false } mod => new Dictionary<string, object> { ["kind"] = OperationKinds.Mod, ["slot"] = mod.Slot, ["target"] = mod.Target, ["id"] = mod.Id },
            ModRollOperation { Clear: false } roll => new Dictionary<string, object> { ["kind"] = OperationKinds.ModRoll, ["slot"] = roll.Slot, ["target"] = roll.Target },
            TalentOperation { Clear: false } talent => new Dictionary<string, object> { ["kind"] = OperationKinds.Talent, ["slot"] = talent.Slot, ["target"] = talent.Target, ["id"] = talent.Id },
            _ => throw new InvalidOperationException($"Ranking produced an unexpected {operation.GetType().Name}")
        };
        double? value = operation switch { AttributeOperation attribute => attribute.Value, ModRollOperation roll => roll.Value, _ => null };
        if (value is { } number) { json["value"] = number; }
        return json;
    }
}
