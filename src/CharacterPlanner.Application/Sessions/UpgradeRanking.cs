using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Application.Sessions;

public enum UpgradeKind
{
    Attribute,
    Mod,
    Talent
}

/// <summary>
/// One change to the equipped item's configuration and what it does to the ranked stat. Target and TargetName are the
/// item's attribute, mod or talent slot (TargetName is what the Go app's label led with, e.g. "Core Attribute");
/// Id and Name the choice. Value is the roll the change sets, when the choice has one, with ValueFormat the format
/// of its attribute. Reroll marks the slot's current choice raised to its highest roll. Operations apply the change
/// through <see cref="PlannerSession.Apply"/> as one undo step.
/// </summary>
public sealed record RankedUpgrade(
    UpgradeKind Kind,
    string Target,
    string TargetName,
    string Id,
    string Name,
    double? Value,
    string ValueFormat,
    bool Reroll,
    double Before,
    double After,
    ImmutableArray<BuildOperation> Operations)
{
    public double Delta => After - Before;
    public EffectCoverageReport Coverage { get; init; } = EffectCoverageReport.Empty;
}

/// <summary>
/// The changes to one equipped item ranked by one stat, largest gain first. Problem says why nothing could be ranked
/// (no item in the slot, an unknown stat, a build that can't be evaluated); Upgrades is then empty.
/// </summary>
public sealed record UpgradeRanking(string Stat, double Before, ImmutableArray<RankedUpgrade> Upgrades, string? Problem = null)
{
    public EffectCoverageReport Coverage { get; init; } = EffectCoverageReport.Empty;
}
