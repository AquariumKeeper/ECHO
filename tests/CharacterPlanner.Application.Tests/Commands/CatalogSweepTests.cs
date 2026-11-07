using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests.Commands;

/// <summary>
/// Acceptance across the whole Division 2 catalog (ADR 0002's property tests): the frozen fixtures cover a few builds,
/// so these check that everything a player can pick resolves cleanly through the same operations the editor uses.
/// Each failure names what was picked and why, so a data or engine fault is easy to find.
/// </summary>
public sealed class CatalogSweepTests
{
    private static readonly GamePack Pack = Division.Rules;

    /// <summary>The weapon_slot assumption that puts an item's slot in hand, when it's a weapon slot.</summary>
    private static ImmutableDictionary<string, string> InHand(string slot) =>
        Pack.Slots.Single(definition => definition.ID == slot).Weapon
            ? ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, slot)
            : ImmutableDictionary<string, string>.Empty;

    /// <summary>Why a resolved build isn't clean: its error diagnostics and any stat or output that isn't finite.</summary>
    private static IEnumerable<string> Problems(ResolvedBuild resolved) =>
        resolved.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            .Concat(resolved.Calculation.Stats.Where(stat => !double.IsFinite(stat.Value)).Select(stat => $"{stat.Key} is {stat.Value}"))
            .Concat(resolved.BuildOutputs.Where(output => !double.IsFinite(output.Value)).Select(output => $"{output.Id} is {output.Value}"));

    /// <summary>Applies the operations to an empty build, or returns why they were refused.</summary>
    private static (BuildSnapshot? Build, string? Refused) Apply(params BuildOperation[] operations)
    {
        var outcome = Division.Applier.Apply(Division.State(), [.. operations]);
        return outcome.Succeeded ? (outcome.State.Build, null) : (null, string.Join("; ", outcome.Errors.Select(error => error.Message)));
    }

    private static void AssertClean(List<string> failures, int checkedCount)
    {
        Assert.True(checkedCount > 0, "nothing was checked");
        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCount} failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(40))}");
    }

    /// <summary>
    /// Every item equips, with every multi-choice attribute, mod and talent slot set to its first choice (single-choice
    /// slots fill themselves), and resolves with no errors and finite stats, a weapon resolving in hand.
    /// </summary>
    [Fact]
    public void EveryItemEquipsAndResolves()
    {
        var failures = new List<string>();
        foreach (var item in Pack.Items)
        {
            var operations = new List<BuildOperation> { new EquipmentOperation(item.Slot, item.ID) };
            operations.AddRange(item.AttributeSlots.Where(slot => slot.Choices.Length > 1).Select(slot => new AttributeOperation(item.Slot, slot.ID, slot.Choices[0].AttributeID)));
            operations.AddRange(item.ModSlots.Where(slot => slot.Choices.Length > 1).Select(slot => new ModOperation(item.Slot, slot.ID, slot.Choices[0])));
            operations.AddRange(item.TalentSlots.Where(slot => slot.Choices.Length > 1).Select(slot => new TalentOperation(item.Slot, slot.ID, slot.Choices[0])));
            var (build, refused) = Apply([.. operations]);
            if (build is null) { failures.Add($"{item.ID}: refused: {refused}"); continue; }
            var problems = Problems(BuildResolver.Resolve(Division.Pack, build, InHand(item.Slot))).ToList();
            if (problems.Count > 0) { failures.Add($"{item.ID}: {string.Join("; ", problems)}"); }
        }
        AssertClean(failures, Pack.Items.Length);
    }

    /// <summary>Every talent an item can take resolves on that item.</summary>
    [Fact]
    public void EveryTalentChoiceResolves()
    {
        var failures = new List<string>();
        var checkedCount = 0;
        // One item per distinct talent choice is enough: the same talent on another item is the same modifiers.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Pack.Items)
        {
            foreach (var slot in item.TalentSlots)
            {
                foreach (var talent in slot.Choices.Where(seen.Add))
                {
                    checkedCount++;
                    var (build, refused) = Apply(new EquipmentOperation(item.Slot, item.ID), new TalentOperation(item.Slot, slot.ID, talent));
                    if (build is null) { failures.Add($"{talent} on {item.ID}: refused: {refused}"); continue; }
                    var problems = Problems(BuildResolver.Resolve(Division.Pack, build, InHand(item.Slot))).ToList();
                    if (problems.Count > 0) { failures.Add($"{talent} on {item.ID}: {string.Join("; ", problems)}"); }
                }
            }
        }
        AssertClean(failures, checkedCount);
    }

    /// <summary>Every Prototype-eligible item takes every augment at its highest level, and resolves.</summary>
    [Fact]
    public void EveryPrototypeAugmentResolves()
    {
        var prototype = Assert.IsType<PrototypeDefinition>(Pack.Prototype);
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var item in Pack.Items.Where(item => PrototypeRules.Eligible(Pack, item, item.Slot)))
        {
            foreach (var augment in prototype.Augments)
            {
                checkedCount++;
                var (build, refused) = Apply(new EquipmentOperation(item.Slot, item.ID), new PrototypeOperation(item.Slot, augment.ID));
                if (build is null) { failures.Add($"{augment.ID} on {item.ID}: refused: {refused}"); continue; }
                var problems = Problems(BuildResolver.Resolve(Division.Pack, build, InHand(item.Slot))).ToList();
                if (problems.Count > 0) { failures.Add($"{augment.ID} on {item.ID}: {string.Join("; ", problems)}"); }
            }
        }
        AssertClean(failures, checkedCount);
    }

    /// <summary>Every skill mod a skill offers, at its highest roll, resolves with finite previews.</summary>
    [Fact]
    public void EverySkillModResolves()
    {
        var failures = new List<string>();
        var checkedCount = 0;
        var slot = Pack.ActiveSkillSlots[0];
        foreach (var skill in Pack.ActiveSkills.Where(skill => slot.Choices.Contains(skill.ID)))
        {
            foreach (var modSlot in skill.ModSlots)
            {
                foreach (var mod in modSlot.Choices)
                {
                    checkedCount++;
                    var (build, refused) = Apply(new ActiveSkillOperation(slot.ID, skill.ID), new SkillModOperation(slot.ID, modSlot.ID, mod.ID));
                    if (build is null) { failures.Add($"{mod.ID} on {skill.ID}: refused: {refused}"); continue; }
                    var resolved = BuildResolver.Resolve(Division.Pack, build, ImmutableDictionary<string, string>.Empty);
                    var problems = Problems(resolved).ToList();
                    problems.AddRange(resolved.ActiveSkillPreviews.GetValueOrDefault(slot.ID)?.Outputs.Where(output => !double.IsFinite(output.Value)).Select(output => $"{output.Id} is {output.Value}") ?? ["no preview"]);
                    if (problems.Count > 0) { failures.Add($"{mod.ID} on {skill.ID}: {string.Join("; ", problems)}"); }
                }
            }
        }
        AssertClean(failures, checkedCount);
    }

    /// <summary>
    /// Every specialization resolves alone and with each of its nodes; allocating every node at once is more than the
    /// budgets allow, and is diagnosed as such rather than resolved as if legal.
    /// </summary>
    [Fact]
    public void EverySpecializationAndNodeResolves()
    {
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var specialization in Pack.Specializations)
        {
            var nodes = specialization.Nodes.ToDictionary(node => node.ID, StringComparer.Ordinal);
            // A node with the chain of prerequisites it needs (tier 3 needs tier 2, which needs tier 1).
            IEnumerable<string> WithPrerequisites(string id) =>
                (nodes.TryGetValue(id, out var node) ? node.Prerequisites.SelectMany(WithPrerequisites) : []).Append(id).Distinct(StringComparer.Ordinal);
            var builds = new[] { ("alone", Apply(new SpecializationOperation(specialization.ID))) }
                .Concat(specialization.Nodes.Select(node => (node.ID, Apply([new SpecializationOperation(specialization.ID),
                    .. WithPrerequisites(node.ID).Select(id => new SpecializationNodeOperation(id, true))]))));
            foreach (var (what, (build, refused)) in builds)
            {
                checkedCount++;
                if (build is null) { failures.Add($"{specialization.ID} {what}: refused: {refused}"); continue; }
                var problems = Problems(BuildResolver.Resolve(Division.Pack, build, ImmutableDictionary<string, string>.Empty)).ToList();
                if (problems.Count > 0) { failures.Add($"{specialization.ID} {what}: {string.Join("; ", problems)}"); }
            }

            var (full, _) = Apply([new SpecializationOperation(specialization.ID), .. specialization.Nodes.Select(node => new SpecializationNodeOperation(node.ID, true))]);
            if (full is null || !BuildResolver.Resolve(Division.Pack, full, ImmutableDictionary<string, string>.Empty).Diagnostics.Any(diagnostic => diagnostic.Code == "specialization-budget-exceeded"))
            { failures.Add($"{specialization.ID}: every node at once isn't diagnosed as over budget"); }
        }
        AssertClean(failures, checkedCount);
    }

    /// <summary>Every skill variant, in every slot that takes it, resolves with finite previews.</summary>
    [Fact]
    public void EverySkillVariantHasFinitePreviews()
    {
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var slot in Pack.ActiveSkillSlots)
        {
            foreach (var skill in slot.Choices)
            {
                checkedCount++;
                var (build, refused) = Apply(new ActiveSkillOperation(slot.ID, skill));
                if (build is null) { failures.Add($"{skill} in {slot.ID}: refused: {refused}"); continue; }
                var resolved = BuildResolver.Resolve(Division.Pack, build, ImmutableDictionary<string, string>.Empty);
                var problems = Problems(resolved).ToList();
                if (!resolved.ActiveSkillPreviews.TryGetValue(slot.ID, out var preview)) { problems.Add("no preview"); }
                else { problems.AddRange(preview.Outputs.Where(output => !double.IsFinite(output.Value)).Select(output => $"{output.Id} is {output.Value}")); }
                if (problems.Count > 0) { failures.Add($"{skill} in {slot.ID}: {string.Join("; ", problems)}"); }
            }
        }
        AssertClean(failures, checkedCount);
    }
}
