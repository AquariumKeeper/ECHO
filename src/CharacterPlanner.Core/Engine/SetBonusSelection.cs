using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record ActiveSet(string SetId, string Name, long Pieces, long BonusPieces, ImmutableArray<long> AppliedBonuses);
public sealed record SelectedSetBonuses(ImmutableArray<ActiveSet> ActiveSets, ImmutableArray<ModifierBatch> Batches);

public static class SetBonusSelection
{
    /// <summary>
    /// Selects active set bonuses: a set with no equipped piece is skipped, and selected
    /// talents' set-piece credit then counts toward every other set.
    /// </summary>
    public static SelectedSetBonuses Resolve(GamePack pack, IReadOnlySet<string> activeItemIds, IReadOnlySet<string> selectedTalentIds)
    {
        var credit = pack.Talents.Where(talent => selectedTalentIds.Contains(talent.ID)).Sum(talent => talent.SetPieceCredit);
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var sets = ImmutableArray.CreateBuilder<ActiveSet>();
        foreach (var set in pack.Sets)
        {
            long pieces = set.Items.Count(activeItemIds.Contains);
            if (pieces == 0) { continue; }
            pieces += credit;
            var applied = ImmutableArray.CreateBuilder<long>();
            for (var index = 0; index < set.Bonuses.Length; index++)
            {
                if (pieces >= set.Bonuses[index].Pieces)
                {
                    batches.Add(ModifierBatch.SetBonus(set, index));
                    applied.Add(set.Bonuses[index].Pieces);
                }
            }
            if (applied.Count > 0) { sets.Add(new(set.ID, set.Name, pieces, credit, applied.ToImmutable())); }
        }
        return new(sets.ToImmutable(), batches.ToImmutable());
    }

    /// <summary>
    /// The set talents a fight runs on their own: those no item's talent slot offers, once their set's top bonus applies (Apex
    /// Engine). A set talent an item offers is selected there, like any other.
    /// </summary>
    public static IEnumerable<string> FullSetTalents(GamePack pack, ImmutableArray<ActiveSet> activeSets)
    {
        var full = activeSets.Where(active => active.AppliedBonuses.Max() == pack.Sets.Single(set => set.ID == active.SetId).Bonuses.Max(bonus => bonus.Pieces))
            .Select(active => active.SetId).ToHashSet(StringComparer.Ordinal);
        return PackIndex.Of(pack).SlotlessSetTalents.Where(talent => talent.ModeledBySets.Any(full.Contains)).Select(talent => talent.ID);
    }

    /// <summary>Whether this talent has a fight rule that runs at its set's top bonus, rather than through an item's slot.</summary>
    public static bool RunsAtTopBonus(GamePack pack, TalentDefinition talent) => PackIndex.Of(pack).RuledSetTalents.Any(ruled => ruled.ID == talent.ID);

    /// <summary>Whether any of these items belongs to a set with such a talent, so a run needs to ask which sets apply.</summary>
    public static bool MayRunSetTalents(GamePack pack, IEnumerable<string> itemIds)
    {
        var ruled = PackIndex.Of(pack).RuledSetTalents;
        if (ruled.IsEmpty) { return false; }
        var items = itemIds.ToHashSet(StringComparer.Ordinal);
        return pack.Sets.Any(set => ruled.Any(talent => talent.ModeledBySets.Contains(set.ID)) && set.Items.Any(items.Contains));
    }
}
